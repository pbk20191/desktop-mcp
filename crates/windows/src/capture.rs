//! One frame of a display with `Windows.Graphics.Capture`, copied to the CPU as BGRA.
use crate::imaging::Pixels;
use desktop_mcp_core::window::DisplayFrame;
use std::time::Duration;
use windows::core::Interface;
use windows::Foundation::TypedEventHandler;
use windows::Graphics::Capture::{Direct3D11CaptureFramePool, GraphicsCaptureItem};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Win32::Foundation::{HMODULE, POINT};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D, D3D11_CPU_ACCESS_READ, D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_MAPPED_SUBRESOURCE, D3D11_MAP_READ,
    D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC, D3D11_USAGE_STAGING,
};
use windows::Win32::Graphics::Dxgi::IDXGIDevice;
use windows::Win32::Graphics::Gdi::{MonitorFromPoint, MONITOR_DEFAULTTOPRIMARY};
use windows::Win32::System::WinRT::Direct3D11::{CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess};
use windows::Win32::System::WinRT::Graphics::Capture::IGraphicsCaptureItemInterop;

/// Asks once per process for capture without the yellow border; the answer is kept.
async fn borderless_allowed() -> bool {
    use std::future::IntoFuture;
    use std::sync::atomic::{AtomicU8, Ordering};
    use windows::Graphics::Capture::{GraphicsCaptureAccess, GraphicsCaptureAccessKind};
    use windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus;
    // 0: not asked, 1: allowed, 2: refused.
    static ANSWER: AtomicU8 = AtomicU8::new(0);
    match ANSWER.load(Ordering::Relaxed) {
        1 => return true,
        2 => return false,
        _ => {}
    }
    let Ok(request) = GraphicsCaptureAccess::RequestAccessAsync(GraphicsCaptureAccessKind::Borderless) else {
        ANSWER.store(2, Ordering::Relaxed);
        return false;
    };
    let allowed = tokio::select! {
        status = request.into_future() => status.is_ok_and(|status| status == AppCapabilityAccessStatus::Allowed),
        _ = crate::pool::sleep(Duration::from_secs(2)) => false,
    };
    ANSWER.store(if allowed { 1 } else { 2 }, Ordering::Relaxed);
    allowed
}

/// How long to wait for the first frame.
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);

fn message(error: windows::core::Error) -> String {
    error.message()
}

/// One frame of `display`, without the cursor, and without the capture border where the system allows that.
pub async fn capture_display(display: &DisplayFrame) -> Result<Pixels, String> {
    let center = POINT { x: (display.frame.x + display.frame.width / 2.0) as i32, y: (display.frame.y + display.frame.height / 2.0) as i32 };
    let monitor = unsafe { MonitorFromPoint(center, MONITOR_DEFAULTTOPRIMARY) };
    let interop = windows::core::factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>().map_err(message)?;
    let item: GraphicsCaptureItem = unsafe { interop.CreateForMonitor(monitor) }.map_err(message)?;

    let (mut device, mut context): (Option<ID3D11Device>, Option<ID3D11DeviceContext>) = (None, None);
    unsafe { D3D11CreateDevice(None, D3D_DRIVER_TYPE_HARDWARE, HMODULE::default(), D3D11_CREATE_DEVICE_BGRA_SUPPORT, None, D3D11_SDK_VERSION, Some(&mut device), None, Some(&mut context)) }
        .map_err(message)?;
    let (device, context) = (device.ok_or("no Direct3D device")?, context.ok_or("no Direct3D context")?);
    let dxgi: IDXGIDevice = device.cast().map_err(message)?;
    let winrt_device: IDirect3DDevice = unsafe { CreateDirect3D11DeviceFromDXGIDevice(&dxgi) }.map_err(message)?.cast().map_err(message)?;

    let size = item.Size().map_err(message)?;
    let pool = Direct3D11CaptureFramePool::CreateFreeThreaded(&winrt_device, DirectXPixelFormat::B8G8R8A8UIntNormalized, 1, size).map_err(message)?;
    let session = pool.CreateCaptureSession(&item).map_err(message)?;
    let _ = session.SetIsCursorCaptureEnabled(false);
    if borderless_allowed().await {
        let _ = session.SetIsBorderRequired(false);
    }
    let (sender, receiver) = tokio::sync::oneshot::channel();
    let sender = std::sync::Mutex::new(Some(sender));
    pool.FrameArrived(&TypedEventHandler::new(move |pool: windows::core::Ref<Direct3D11CaptureFramePool>, _| {
        if let (Some(pool), Some(sender)) = (pool.as_ref(), sender.lock().unwrap().take()) {
            let _ = sender.send(pool.TryGetNextFrame());
        }
        Ok(())
    }))
    .map_err(message)?;
    session.StartCapture().map_err(message)?;
    let frame = tokio::select! {
        frame = receiver => frame.map_err(|_| "the capture ended".to_string())?.map_err(message)?,
        _ = crate::pool::sleep(FRAME_TIMEOUT) => {
            let _ = session.Close();
            let _ = pool.Close();
            return Err("no frame came from the display".to_string());
        }
    };

    let surface = frame.Surface().map_err(message)?;
    let access: IDirect3DDxgiInterfaceAccess = surface.cast().map_err(message)?;
    let texture: ID3D11Texture2D = unsafe { access.GetInterface() }.map_err(message)?;
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    unsafe { texture.GetDesc(&mut desc) };
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    desc.MiscFlags = 0;
    let mut staging = None;
    unsafe { device.CreateTexture2D(&desc, None, Some(&mut staging)) }.map_err(message)?;
    let staging = staging.ok_or("no staging texture")?;
    unsafe { context.CopyResource(&staging, &texture) };
    let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
    unsafe { context.Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped)) }.map_err(message)?;
    let (width, height) = (desc.Width, desc.Height);
    let mut bgra = vec![0u8; (width * height * 4) as usize];
    for row in 0..height {
        let source = unsafe { std::slice::from_raw_parts((mapped.pData as *const u8).add((row * mapped.RowPitch) as usize), (width * 4) as usize) };
        bgra[(row * width * 4) as usize..][..(width * 4) as usize].copy_from_slice(source);
    }
    unsafe { context.Unmap(&staging, 0) };
    let _ = frame.Close();
    let _ = session.Close();
    let _ = pool.Close();
    // Captured pixels are opaque; alpha is forced so the premultiplied encoders see them as such.
    for pixel in bgra.chunks_exact_mut(4) {
        pixel[3] = 255;
    }
    Ok(Pixels { width, height, bgra, origin: (display.frame.x as i32, display.frame.y as i32) })
}
