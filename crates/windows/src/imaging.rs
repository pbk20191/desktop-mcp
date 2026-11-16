//! Pixels in memory: numbered boxes drawn with Direct2D (and DirectWrite labels) over a WIC bitmap, and PNG encoding
//! with WIC. Synchronous COM: call these on the thread pool.
use desktop_mcp_core::snapshot::TreeNode;
use windows::core::{w, Interface};
use windows::Win32::Graphics::Direct2D::Common::{D2D1_ALPHA_MODE_PREMULTIPLIED, D2D1_COLOR_F, D2D1_PIXEL_FORMAT, D2D_RECT_F};
use windows::Win32::Graphics::Direct2D::{
    D2D1CreateFactory, ID2D1Factory, D2D1_DRAW_TEXT_OPTIONS_NONE, D2D1_FACTORY_TYPE_SINGLE_THREADED, D2D1_RENDER_TARGET_PROPERTIES, D2D1_RENDER_TARGET_TYPE_SOFTWARE,
};
use windows::Win32::Graphics::DirectWrite::{DWriteCreateFactory, IDWriteFactory, DWRITE_FACTORY_TYPE_SHARED, DWRITE_FONT_STRETCH_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_WEIGHT_BOLD, DWRITE_MEASURING_MODE_NATURAL};
use windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT_B8G8R8A8_UNORM;
use windows::Win32::Graphics::Imaging::{
    CLSID_WICImagingFactory2, GUID_ContainerFormatPng, GUID_WICPixelFormat32bppPBGRA, IWICBitmapSource, IWICImagingFactory, WICBitmapEncoderNoCache,
    WICBitmapInterpolationModeFant, WICBitmapLockRead, WICRect,
};
use windows::Win32::System::Com::StructuredStorage::{CreateStreamOnHGlobal, GetHGlobalFromStream};
use windows::Win32::System::Com::{CoCreateInstance, CLSCTX_INPROC_SERVER};
use windows::Win32::System::Memory::{GlobalLock, GlobalSize, GlobalUnlock};

/// A 32-bit BGRA image (premultiplied, opaque for captures), top-down rows of `width * 4` bytes; `origin` is where
/// its top-left pixel sits on the screen.
#[derive(Clone, Debug)]
pub struct Pixels {
    pub width: u32,
    pub height: u32,
    pub bgra: Vec<u8>,
    pub origin: (i32, i32),
}

fn error(context: &str) -> impl Fn(windows::core::Error) -> String + '_ {
    move |error| format!("{context}: {}", error.message())
}

fn wic() -> Result<IWICImagingFactory, String> {
    crate::uia::ensure_mta();
    unsafe { CoCreateInstance(&CLSID_WICImagingFactory2, None, CLSCTX_INPROC_SERVER) }.map_err(error("WIC"))
}

/// Box colours, cycled by element number (bright and distinct on light and dark content).
const COLOURS: [(f32, f32, f32); 8] = [(0.90, 0.10, 0.10), (0.10, 0.55, 0.95), (0.10, 0.70, 0.25), (0.95, 0.55, 0.05), (0.60, 0.20, 0.85), (0.00, 0.65, 0.65), (0.90, 0.10, 0.60), (0.45, 0.45, 0.45)];

/// Draws a numbered box (0, 1, 2…, as the snapshot numbers the interactive elements) around each element, given
/// in screen coordinates. The number sits inside the box's top-left corner, or above it when the box is small.
pub fn annotate(pixels: &mut Pixels, elements: &[TreeNode]) -> Result<(), String> {
    let factory = wic()?;
    let stride = pixels.width * 4;
    let bitmap = unsafe { factory.CreateBitmapFromMemory(pixels.width, pixels.height, &GUID_WICPixelFormat32bppPBGRA, stride, &pixels.bgra) }.map_err(error("WIC bitmap"))?;
    let d2d: ID2D1Factory = unsafe { D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, None) }.map_err(error("Direct2D"))?;
    let properties = D2D1_RENDER_TARGET_PROPERTIES {
        r#type: D2D1_RENDER_TARGET_TYPE_SOFTWARE,
        pixelFormat: D2D1_PIXEL_FORMAT { format: DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode: D2D1_ALPHA_MODE_PREMULTIPLIED },
        ..Default::default()
    };
    let target = unsafe { d2d.CreateWicBitmapRenderTarget(&bitmap, &properties) }.map_err(error("render target"))?;
    let dwrite: IDWriteFactory = unsafe { DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED) }.map_err(error("DirectWrite"))?;
    let format = unsafe { dwrite.CreateTextFormat(w!("Segoe UI"), None, DWRITE_FONT_WEIGHT_BOLD, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 12.0, w!("en-us")) }.map_err(error("text format"))?;
    let white = unsafe { target.CreateSolidColorBrush(&D2D1_COLOR_F { r: 1.0, g: 1.0, b: 1.0, a: 1.0 }, None) }.map_err(error("brush"))?;
    unsafe { target.BeginDraw() };
    for (index, element) in elements.iter().enumerate() {
        let b = &element.bounding_box;
        let (left, top) = ((b.left - pixels.origin.0 as i64) as f32, (b.top - pixels.origin.1 as i64) as f32);
        let (right, bottom) = ((b.right - pixels.origin.0 as i64) as f32, (b.bottom - pixels.origin.1 as i64) as f32);
        if right <= 0.0 || bottom <= 0.0 || left >= pixels.width as f32 || top >= pixels.height as f32 {
            continue;
        }
        let (r, g, bl) = COLOURS[index % COLOURS.len()];
        let Ok(brush) = (unsafe { target.CreateSolidColorBrush(&D2D1_COLOR_F { r, g, b: bl, a: 1.0 }, None) }) else { continue };
        unsafe { target.DrawRectangle(&D2D_RECT_F { left: left + 1.0, top: top + 1.0, right: right - 1.0, bottom: bottom - 1.0 }, &brush, 2.0, None) };
        let label: Vec<u16> = index.to_string().encode_utf16().collect();
        let width = 8.0 * label.len() as f32 + 6.0;
        let small = right - left < width + 4.0 || bottom - top < 20.0;
        let label_top = if small && top >= 16.0 { top - 16.0 } else { top };
        let label_box = D2D_RECT_F { left, top: label_top, right: left + width, bottom: label_top + 16.0 };
        unsafe {
            target.FillRectangle(&label_box, &brush);
            target.DrawText(&label, &format, &D2D_RECT_F { left: left + 3.0, ..label_box }, &white, D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE_NATURAL);
        }
    }
    unsafe { target.EndDraw(None, None) }.map_err(error("drawing"))?;
    let lock = unsafe { bitmap.Lock(&WICRect { X: 0, Y: 0, Width: pixels.width as i32, Height: pixels.height as i32 }, WICBitmapLockRead.0 as u32) }.map_err(error("lock"))?;
    let (mut size, mut data) = (0u32, std::ptr::null_mut());
    unsafe { lock.GetDataPointer(&mut size, &mut data) }.map_err(error("pixels"))?;
    let lock_stride = unsafe { lock.GetStride() }.map_err(error("stride"))?;
    for row in 0..pixels.height {
        let source = unsafe { std::slice::from_raw_parts(data.add((row * lock_stride) as usize), stride as usize) };
        pixels.bgra[(row * stride) as usize..][..stride as usize].copy_from_slice(source);
    }
    Ok(())
}

/// The image as PNG, scaled down (never up) so its longer side is at most `max_side`.
pub fn encode_png(pixels: &Pixels, max_side: Option<u32>) -> Result<Vec<u8>, String> {
    let factory = wic()?;
    let bitmap = unsafe { factory.CreateBitmapFromMemory(pixels.width, pixels.height, &GUID_WICPixelFormat32bppPBGRA, pixels.width * 4, &pixels.bgra) }.map_err(error("WIC bitmap"))?;
    let (width, height) = match max_side {
        Some(max_side) => crate::clipboard::scaled_size(pixels.width, pixels.height, max_side),
        None => (pixels.width, pixels.height),
    };
    let source: IWICBitmapSource = if (width, height) == (pixels.width, pixels.height) {
        bitmap.cast().map_err(error("bitmap source"))?
    } else {
        let scaler = unsafe { factory.CreateBitmapScaler() }.map_err(error("scaler"))?;
        unsafe { scaler.Initialize(&bitmap, width, height, WICBitmapInterpolationModeFant) }.map_err(error("scaling"))?;
        scaler.cast().map_err(error("bitmap source"))?
    };
    let stream = unsafe { CreateStreamOnHGlobal(Default::default(), true) }.map_err(error("stream"))?;
    let encoder = unsafe { factory.CreateEncoder(&GUID_ContainerFormatPng, std::ptr::null()) }.map_err(error("PNG encoder"))?;
    unsafe { encoder.Initialize(&stream, WICBitmapEncoderNoCache) }.map_err(error("encoder"))?;
    let mut frame = None;
    unsafe { encoder.CreateNewFrame(&mut frame, std::ptr::null_mut()) }.map_err(error("frame"))?;
    let frame = frame.ok_or("no PNG frame")?;
    unsafe {
        frame.Initialize(None).map_err(error("frame"))?;
        frame.SetSize(width, height).map_err(error("size"))?;
        let mut format = GUID_WICPixelFormat32bppPBGRA;
        frame.SetPixelFormat(&mut format).map_err(error("pixel format"))?;
        frame.WriteSource(&source, std::ptr::null()).map_err(error("writing"))?;
        frame.Commit().map_err(error("frame"))?;
        encoder.Commit().map_err(error("PNG"))?;
    }
    let memory = unsafe { GetHGlobalFromStream(&stream) }.map_err(error("stream memory"))?;
    let mut statistics = Default::default();
    unsafe { stream.Stat(&mut statistics, windows::Win32::System::Com::STATFLAG_NONAME) }.map_err(error("stream size"))?;
    let length = (statistics.cbSize as usize).min(unsafe { GlobalSize(memory) });
    let pointer = unsafe { GlobalLock(memory) } as *const u8;
    if pointer.is_null() {
        return Err("cannot read the PNG".to_string());
    }
    let png = unsafe { std::slice::from_raw_parts(pointer, length) }.to_vec();
    unsafe {
        let _ = GlobalUnlock(memory);
    }
    Ok(png)
}
