//! The clipboard through the Win32 API, on a pool thread: `OpenClipboard(NULL)` needs no window and no message
//! loop, so a clipboard owner that does not answer holds up only that pool thread, and the caller gives up after
//! [`TIMEOUT`]. Data is set at once (no delayed rendering), so nothing has to stay behind to serve it. A bitmap is
//! turned into PNG with WinRT's imaging on the caller's thread.
use crate::pool;
use desktop_mcp_core::clipboard::{Clipboard, ClipboardWrite, Image};
use std::ffi::c_void;
use std::future::IntoFuture;
use std::path::PathBuf;
use std::time::Duration;
use windows::Graphics::Imaging::{BitmapAlphaMode, BitmapDecoder, BitmapEncoder, BitmapInterpolationMode, BitmapPixelFormat, BitmapTransform, ColorManagementMode, ExifOrientationMode};
use windows::Storage::Streams::{DataReader, DataWriter, InMemoryRandomAccessStream};
use windows::Win32::Foundation::{GlobalFree, HANDLE, HGLOBAL};
use windows::Win32::System::DataExchange::{CloseClipboard, EmptyClipboard, GetClipboardData, IsClipboardFormatAvailable, OpenClipboard, SetClipboardData};
use windows::Win32::System::Memory::{GlobalAlloc, GlobalLock, GlobalSize, GlobalUnlock, GMEM_MOVEABLE};
use windows::Win32::System::Ole::{CF_DIB, CF_HDROP, CF_UNICODETEXT};
use windows::Win32::UI::Shell::{DragQueryFileW, HDROP};

/// How long a clipboard call may take before the tool gives up on it.
pub const TIMEOUT: Duration = Duration::from_secs(5);
const NOT_ANSWERING: &str = "the clipboard did not answer in 5 seconds; an application holding it may not be responding";

/// `width`×`height` fitted into `max_side`, never up.
pub fn scaled_size(width: u32, height: u32, max_side: u32) -> (u32, u32) {
    let longest = width.max(height);
    if longest <= max_side || longest == 0 {
        return (width, height);
    }
    let scale = max_side as f64 / longest as f64;
    (((width as f64 * scale).round() as u32).max(1), ((height as f64 * scale).round() as u32).max(1))
}

/// `CF_HDROP` data: a `DROPFILES` header (wide paths) and the paths, each NUL-ended, then one more NUL.
pub fn dropfiles(paths: &[PathBuf]) -> Vec<u8> {
    let mut bytes = Vec::new();
    bytes.extend(20u32.to_le_bytes()); // pFiles: the paths follow the 20-byte header
    bytes.extend([0u8; 12]); // pt, fNC
    bytes.extend(1u32.to_le_bytes()); // fWide
    for path in paths {
        for unit in path.as_os_str().to_string_lossy().encode_utf16().chain(Some(0)) {
            bytes.extend(unit.to_le_bytes());
        }
    }
    bytes.extend(0u16.to_le_bytes());
    bytes
}

/// A `CF_DIB` (header, colour table, pixels) as a `.bmp` file: the 14-byte file header in front.
pub fn bmp_file(dib: &[u8]) -> Option<Vec<u8>> {
    let u32_at = |at: usize| dib.get(at..at + 4).map(|bytes| u32::from_le_bytes(bytes.try_into().unwrap()));
    let header = u32_at(0)? as usize;
    let bits = u16::from_le_bytes(dib.get(14..16)?.try_into().ok()?) as u32;
    let compression = u32_at(16)?;
    let used = u32_at(32).unwrap_or(0);
    let palette = if used > 0 { used } else if bits <= 8 { 1 << bits } else { 0 } as usize * 4;
    // BI_BITFIELDS with the 40-byte header: three masks follow it.
    let masks = if compression == 3 && header == 40 { 12 } else { 0 };
    let offset = 14 + header + masks + palette;
    let mut file = Vec::with_capacity(14 + dib.len());
    file.extend(b"BM");
    file.extend(((14 + dib.len()) as u32).to_le_bytes());
    file.extend([0u8; 4]);
    file.extend((offset as u32).to_le_bytes());
    file.extend_from_slice(dib);
    Some(file)
}

/// The clipboard, opened for this thread; closed when dropped.
struct Open;

impl Open {
    fn new() -> Result<Open, String> {
        // Another application may have it open for a moment.
        for _ in 0..50 {
            if unsafe { OpenClipboard(None) }.is_ok() {
                return Ok(Open);
            }
            std::thread::sleep(Duration::from_millis(10));
        }
        Err("the clipboard is in use by another application".to_string())
    }
}

impl Drop for Open {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseClipboard();
        }
    }
}

/// The bytes of a global memory handle.
fn global_bytes(handle: HANDLE) -> Option<Vec<u8>> {
    let memory = HGLOBAL(handle.0);
    let size = unsafe { GlobalSize(memory) };
    let pointer = unsafe { GlobalLock(memory) } as *const u8;
    if pointer.is_null() {
        return None;
    }
    let bytes = unsafe { std::slice::from_raw_parts(pointer, size) }.to_vec();
    unsafe {
        let _ = GlobalUnlock(memory);
    }
    Some(bytes)
}

/// What was read, before the bitmap becomes PNG.
struct Raw {
    text: Option<String>,
    files: Vec<String>,
    dib: Option<Vec<u8>>,
}

fn read_raw() -> Result<Raw, String> {
    let _open = Open::new()?;
    let available = |format: u16| unsafe { IsClipboardFormatAvailable(format as u32) }.is_ok();
    let mut raw = Raw { text: None, files: Vec::new(), dib: None };
    if available(CF_UNICODETEXT.0) {
        if let Some(bytes) = unsafe { GetClipboardData(CF_UNICODETEXT.0 as u32) }.ok().and_then(global_bytes) {
            let units: Vec<u16> = bytes.chunks_exact(2).map(|pair| u16::from_le_bytes([pair[0], pair[1]])).take_while(|unit| *unit != 0).collect();
            raw.text = Some(String::from_utf16_lossy(&units));
        }
    }
    if available(CF_HDROP.0) {
        if let Ok(handle) = unsafe { GetClipboardData(CF_HDROP.0 as u32) } {
            let drop = HDROP(handle.0);
            let count = unsafe { DragQueryFileW(drop, u32::MAX, None) };
            for index in 0..count {
                let length = unsafe { DragQueryFileW(drop, index, None) } as usize;
                let mut path = vec![0u16; length + 1];
                let copied = unsafe { DragQueryFileW(drop, index, Some(&mut path)) } as usize;
                raw.files.push(String::from_utf16_lossy(&path[..copied]));
            }
        }
    }
    if available(CF_DIB.0) {
        raw.dib = unsafe { GetClipboardData(CF_DIB.0 as u32) }.ok().and_then(global_bytes);
    }
    Ok(raw)
}

/// Global memory holding `bytes`, for `SetClipboardData` (which then owns it).
fn global_copy(bytes: &[u8]) -> Result<HGLOBAL, String> {
    let memory = unsafe { GlobalAlloc(GMEM_MOVEABLE, bytes.len()) }.map_err(|error| error.message())?;
    let pointer = unsafe { GlobalLock(memory) } as *mut u8;
    if pointer.is_null() {
        unsafe {
            let _ = GlobalFree(Some(memory));
        }
        return Err("cannot lock clipboard memory".to_string());
    }
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), pointer, bytes.len());
        let _ = GlobalUnlock(memory);
    }
    Ok(memory)
}

fn write_raw(contents: ClipboardWrite) -> Result<(), String> {
    let (format, bytes) = match contents {
        ClipboardWrite::Text(text) => (CF_UNICODETEXT.0, text.encode_utf16().chain(Some(0)).flat_map(u16::to_le_bytes).collect::<Vec<u8>>()),
        ClipboardWrite::Files(paths) => (CF_HDROP.0, dropfiles(&paths)),
    };
    let _open = Open::new()?;
    unsafe { EmptyClipboard() }.map_err(|error| format!("cannot empty the clipboard: {}", error.message()))?;
    let memory = global_copy(&bytes)?;
    if let Err(error) = unsafe { SetClipboardData(format as u32, Some(HANDLE(memory.0 as *mut c_void))) } {
        unsafe {
            let _ = GlobalFree(Some(memory));
        }
        return Err(format!("cannot set the clipboard: {}", error.message()));
    }
    Ok(())
}

/// `work` on a pool thread, given up after [`TIMEOUT`].
async fn on_pool<T: Send + 'static>(work: impl FnOnce() -> Result<T, String> + Send + 'static) -> Result<T, String> {
    tokio::select! {
        result = pool::offload(work) => result,
        _ = pool::sleep(TIMEOUT) => Err(NOT_ANSWERING.to_string()),
    }
}

pub async fn read(image_max_side: u32) -> Clipboard {
    let Ok(raw) = on_pool(read_raw).await else { return Clipboard::default() };
    let image = match raw.dib.as_deref().and_then(bmp_file) {
        Some(bmp) => png(&bmp, image_max_side).await.ok(),
        None => None,
    };
    Clipboard { text: raw.text, files: raw.files, image }
}

pub async fn write(contents: ClipboardWrite) -> Result<(), String> {
    on_pool(move || write_raw(contents)).await
}

/// A `.bmp` file as PNG at most `max_side` on its longer side.
async fn png(bmp: &[u8], max_side: u32) -> windows::core::Result<Image> {
    let input = InMemoryRandomAccessStream::new()?;
    let writer = DataWriter::CreateDataWriter(&input)?;
    writer.WriteBytes(bmp)?;
    writer.StoreAsync()?.into_future().await?;
    writer.DetachStream()?;
    input.Seek(0)?;
    let decoder = BitmapDecoder::CreateAsync(&input)?.into_future().await?;
    let (width, height) = scaled_size(decoder.PixelWidth()?, decoder.PixelHeight()?, max_side);
    let transform = BitmapTransform::new()?;
    transform.SetScaledWidth(width)?;
    transform.SetScaledHeight(height)?;
    transform.SetInterpolationMode(BitmapInterpolationMode::Fant)?;
    let pixels = decoder
        .GetPixelDataTransformedAsync(BitmapPixelFormat::Bgra8, BitmapAlphaMode::Premultiplied, &transform, ExifOrientationMode::RespectExifOrientation, ColorManagementMode::DoNotColorManage)?
        .into_future()
        .await?
        .DetachPixelData()?;
    let output = InMemoryRandomAccessStream::new()?;
    let encoder = BitmapEncoder::CreateAsync(BitmapEncoder::PngEncoderId()?, &output)?.into_future().await?;
    encoder.SetPixelData(BitmapPixelFormat::Bgra8, BitmapAlphaMode::Premultiplied, width, height, 96.0, 96.0, &pixels.as_slice())?;
    encoder.FlushAsync()?.into_future().await?;
    let size = output.Size()? as u32;
    let reader = DataReader::CreateDataReader(&output.GetInputStreamAt(0)?)?;
    reader.LoadAsync(size)?.into_future().await?;
    let mut png = vec![0u8; size as usize];
    reader.ReadBytes(&mut png)?;
    Ok(Image { png, width, height })
}
