//! One frame of a display with `Windows.Graphics.Capture`, copied to the CPU as BGRA.
//!
//! One Direct3D device serves every capture: made on first use, made again once the system has removed it (a
//! driver update or reset, a GPU gone). The capture session runs on the main loop; the frame's copy to the CPU
//! runs on the pool. Direct3D is not apartment-bound, and the device is free-threaded with its immediate context
//! multithread-protected, so a pool thread may use it while the free-threaded frame pool does.
use crate::imaging::Pixels;
use desktop_mcp_core::window::DisplayFrame;
use std::time::Duration;
use windows::core::Interface;
use windows::Foundation::TypedEventHandler;
use windows::Graphics::Capture::{Direct3D11CaptureFramePool, GraphicsCaptureItem};
use windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;
use windows::Graphics::DirectX::DirectXPixelFormat;
use windows::Win32::Foundation::{HMODULE, POINT};
use windows::Win32::Graphics::Direct3D::{D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP};
use windows::Win32::Graphics::Direct3D11::{
    D3D11CreateDevice, ID3D11Device, ID3D11DeviceContext, ID3D11Multithread, ID3D11Texture2D, D3D11_CPU_ACCESS_READ, D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_MAPPED_SUBRESOURCE,
    D3D11_MAP_READ, D3D11_SDK_VERSION, D3D11_TEXTURE2D_DESC, D3D11_USAGE_STAGING,
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
    let answer = tokio::select! {
        status = request.into_future() => Some(status.is_ok_and(|status| status == AppCapabilityAccessStatus::Allowed)),
        _ = crate::pool::sleep(Duration::from_secs(2)) => None,
    };
    // A request that was only slow is asked again next time.
    if let Some(allowed) = answer {
        ANSWER.store(if allowed { 1 } else { 2 }, Ordering::Relaxed);
    }
    answer.unwrap_or(false)
}

/// How long to wait for the first frame.
const FRAME_TIMEOUT: Duration = Duration::from_secs(2);

fn message(error: windows::core::Error) -> String {
    error.message()
}

/// The shared Direct3D device, and the same device for `Windows.Graphics.Capture`.
#[derive(Clone)]
struct Gpu {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    winrt: IDirect3DDevice,
}

impl Gpu {
    fn new() -> Result<Gpu, String> {
        let (mut device, mut context): (Option<ID3D11Device>, Option<ID3D11DeviceContext>) = (None, None);
        // The GPU, else the WARP software rasterizer (no usable GPU driver).
        let create = |driver, device: &mut Option<ID3D11Device>, context: &mut Option<ID3D11DeviceContext>| unsafe {
            D3D11CreateDevice(None, driver, HMODULE::default(), D3D11_CREATE_DEVICE_BGRA_SUPPORT, None, D3D11_SDK_VERSION, Some(device), None, Some(context))
        };
        create(D3D_DRIVER_TYPE_HARDWARE, &mut device, &mut context).or_else(|_| create(D3D_DRIVER_TYPE_WARP, &mut device, &mut context)).map_err(message)?;
        let (device, context) = (device.ok_or("no Direct3D device")?, context.ok_or("no Direct3D context")?);
        if let Ok(multithread) = context.cast::<ID3D11Multithread>() {
            let _ = unsafe { multithread.SetMultithreadProtected(true) };
        }
        let dxgi: IDXGIDevice = device.cast().map_err(message)?;
        let winrt = unsafe { CreateDirect3D11DeviceFromDXGIDevice(&dxgi) }.map_err(message)?.cast().map_err(message)?;
        Ok(Gpu { device, context, winrt })
    }

    /// The system has removed the device: it must be made again.
    fn removed(&self) -> bool {
        unsafe { self.device.GetDeviceRemovedReason() }.is_err()
    }
}

thread_local! {
    /// The device captures on this thread (the main loop's) share.
    static GPU: std::cell::RefCell<Option<Gpu>> = const { std::cell::RefCell::new(None) };
}

/// The shared device, made now when there is none or the system removed the last one.
fn gpu() -> Result<Gpu, String> {
    GPU.with(|gpu| {
        let mut gpu = gpu.borrow_mut();
        match gpu.as_ref() {
            Some(current) if !current.removed() => Ok(current.clone()),
            _ => {
                let made = Gpu::new()?;
                *gpu = Some(made.clone());
                Ok(made)
            }
        }
    })
}

/// One frame of `display`, without the cursor, and without the capture border where the system allows that.
pub async fn capture_display(display: &DisplayFrame) -> Result<Pixels, String> {
    let gpu = gpu()?;
    match capture_with(&gpu, display).await {
        // The device went away mid-capture: once more on a new one.
        Err(_) if gpu.removed() => capture_with(&self::gpu()?, display).await,
        result => result,
    }
}

async fn capture_with(gpu: &Gpu, display: &DisplayFrame) -> Result<Pixels, String> {
    let center = POINT { x: (display.frame.x + display.frame.width / 2.0) as i32, y: (display.frame.y + display.frame.height / 2.0) as i32 };
    let monitor = unsafe { MonitorFromPoint(center, MONITOR_DEFAULTTOPRIMARY) };
    let interop = windows::core::factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>().map_err(message)?;
    let item: GraphicsCaptureItem = unsafe { interop.CreateForMonitor(monitor) }.map_err(message)?;

    let size = item.Size().map_err(message)?;
    let pool = Direct3D11CaptureFramePool::CreateFreeThreaded(&gpu.winrt, DirectXPixelFormat::B8G8R8A8UIntNormalized, 1, size).map_err(message)?;
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

    let texture = frame.Surface().and_then(|surface| surface.cast::<IDirect3DDxgiInterfaceAccess>()).and_then(|access| unsafe { access.GetInterface::<ID3D11Texture2D>() });
    // The frame stays open until its texture has been read.
    let read = match texture {
        Ok(texture) => {
            let (device, context) = (gpu.device.clone(), gpu.context.clone());
            crate::pool::offload(move || read_back(&device, &context, &texture)).await
        }
        Err(error) => Err(message(error)),
    };
    let _ = frame.Close();
    let _ = session.Close();
    let _ = pool.Close();
    let (width, height, bgra) = read?;
    Ok(Pixels { width, height, bgra, origin: (display.frame.x as i32, display.frame.y as i32) })
}

/// `texture` copied to the CPU: width, height and opaque BGRA rows. Blocking (it waits for the GPU): on the pool.
fn read_back(device: &ID3D11Device, context: &ID3D11DeviceContext, texture: &ID3D11Texture2D) -> Result<(u32, u32, Vec<u8>), String> {
    let mut desc = D3D11_TEXTURE2D_DESC::default();
    unsafe { texture.GetDesc(&mut desc) };
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
    desc.MiscFlags = 0;
    let mut staging = None;
    unsafe { device.CreateTexture2D(&desc, None, Some(&mut staging)) }.map_err(message)?;
    let staging = staging.ok_or("no staging texture")?;
    unsafe { context.CopyResource(&staging, texture) };
    let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
    unsafe { context.Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped)) }.map_err(message)?;
    let (width, height) = (desc.Width, desc.Height);
    let mut bgra = vec![0u8; (width * height * 4) as usize];
    for row in 0..height {
        let source = unsafe { std::slice::from_raw_parts((mapped.pData as *const u8).add((row * mapped.RowPitch) as usize), (width * 4) as usize) };
        bgra[(row * width * 4) as usize..][..(width * 4) as usize].copy_from_slice(source);
    }
    unsafe { context.Unmap(&staging, 0) };
    // Captured pixels are opaque; alpha is forced so the premultiplied encoders see them as such.
    for pixel in bgra.chunks_exact_mut(4) {
        pixel[3] = 255;
    }
    Ok((width, height, bgra))
}
