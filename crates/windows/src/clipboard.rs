//! The clipboard through `Windows.ApplicationModel.DataTransfer`: text, copied files and a bitmap (as PNG).
use desktop_mcp_core::clipboard::{Clipboard, ClipboardWrite, Image};
use std::future::IntoFuture;
use windows::core::{Interface, HSTRING};
use windows::ApplicationModel::DataTransfer::{Clipboard as SystemClipboard, DataPackage, StandardDataFormats};
use windows_collections::IIterable;
use windows::Graphics::Imaging::{BitmapAlphaMode, BitmapDecoder, BitmapEncoder, BitmapInterpolationMode, BitmapPixelFormat, BitmapTransform, ColorManagementMode, ExifOrientationMode};
use windows::Storage::Streams::{DataReader, InMemoryRandomAccessStream};
use windows::Storage::{IStorageItem, StorageFile, StorageFolder};

/// `width`×`height` scaled to fit `max_side`, never up.
pub fn scaled_size(width: u32, height: u32, max_side: u32) -> (u32, u32) {
    let longest = width.max(height);
    if longest <= max_side || longest == 0 {
        return (width, height);
    }
    let scale = max_side as f64 / longest as f64;
    (((width as f64 * scale).round() as u32).max(1), ((height as f64 * scale).round() as u32).max(1))
}

pub async fn read(image_max_side: u32) -> Clipboard {
    let mut clipboard = Clipboard::default();
    let Ok(view) = SystemClipboard::GetContent() else { return clipboard };
    let has = |format: windows::core::Result<HSTRING>| format.and_then(|format| view.Contains(&format)).unwrap_or(false);
    if has(StandardDataFormats::Text()) {
        if let Ok(operation) = view.GetTextAsync() {
            clipboard.text = operation.into_future().await.ok().map(|text| text.to_string());
        }
    }
    if has(StandardDataFormats::StorageItems()) {
        if let Ok(operation) = view.GetStorageItemsAsync() {
            if let Ok(items) = operation.into_future().await {
                clipboard.files = items.into_iter().filter_map(|item| item.Path().ok()).map(|path| path.to_string()).collect();
            }
        }
    }
    if has(StandardDataFormats::Bitmap()) {
        clipboard.image = read_image(&view, image_max_side).await.ok();
    }
    clipboard
}

async fn read_image(view: &windows::ApplicationModel::DataTransfer::DataPackageView, max_side: u32) -> windows::core::Result<Image> {
    let reference = view.GetBitmapAsync()?.into_future().await?;
    let stream = reference.OpenReadAsync()?.into_future().await?;
    let decoder = BitmapDecoder::CreateAsync(&stream)?.into_future().await?;
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

pub async fn write(contents: ClipboardWrite) -> Result<(), String> {
    let package = DataPackage::new().map_err(|error| error.message())?;
    match contents {
        ClipboardWrite::Text(text) => package.SetText(&HSTRING::from(text)).map_err(|error| error.message())?,
        ClipboardWrite::Files(paths) => {
            let mut items: Vec<Option<IStorageItem>> = Vec::new();
            for path in paths {
                let name = HSTRING::from(path.as_os_str());
                let item: IStorageItem = if path.is_dir() {
                    StorageFolder::GetFolderFromPathAsync(&name).map_err(|error| error.message())?.into_future().await.map_err(|error| error.message())?.cast().map_err(|error| error.message())?
                } else {
                    StorageFile::GetFileFromPathAsync(&name).map_err(|error| error.message())?.into_future().await.map_err(|error| error.message())?.cast().map_err(|error| error.message())?
                };
                items.push(Some(item));
            }
            let items: IIterable<IStorageItem> = items.into();
            package.SetStorageItemsReadOnly(&items).map_err(|error| error.message())?;
        }
    }
    SystemClipboard::SetContent(&package).map_err(|error| error.message())?;
    SystemClipboard::Flush().map_err(|error| error.message())
}
