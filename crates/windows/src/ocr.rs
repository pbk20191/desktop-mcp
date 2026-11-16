//! Text on screen with `Windows.Media.Ocr`: the region's display is captured, cropped, and recognized.
use crate::imaging::Pixels;
use desktop_mcp_core::ocr::{Request, TextLine};
use desktop_mcp_core::window::{DisplayFrame, Frame};
use std::future::IntoFuture;
use windows::core::HSTRING;
use windows::Globalization::Language;
use windows::Graphics::Imaging::{BitmapAlphaMode, BitmapPixelFormat, SoftwareBitmap};
use windows::Media::Ocr::OcrEngine;
use windows::Storage::Streams::DataWriter;

/// The part of `pixels` inside `region` (screen pixels), or `None` when they do not overlap.
pub fn crop(pixels: &Pixels, region: Frame) -> Option<Pixels> {
    let left = (region.x as i32).max(pixels.origin.0);
    let top = (region.y as i32).max(pixels.origin.1);
    let right = ((region.x + region.width) as i32).min(pixels.origin.0 + pixels.width as i32);
    let bottom = ((region.y + region.height) as i32).min(pixels.origin.1 + pixels.height as i32);
    if right <= left || bottom <= top {
        return None;
    }
    let (width, height) = ((right - left) as u32, (bottom - top) as u32);
    let mut bgra = Vec::with_capacity((width * height * 4) as usize);
    for row in 0..height {
        let y = (top - pixels.origin.1) as u32 + row;
        let x = (left - pixels.origin.0) as u32;
        let start = ((y * pixels.width + x) * 4) as usize;
        bgra.extend_from_slice(&pixels.bgra[start..start + (width * 4) as usize]);
    }
    Some(Pixels { width, height, bgra, origin: (left, top) })
}

/// The display holding the centre of `region`.
pub fn display_of(displays: &[DisplayFrame], region: Frame) -> Option<&DisplayFrame> {
    let (x, y) = (region.x + region.width / 2.0, region.y + region.height / 2.0);
    displays.iter().find(|display| x >= display.frame.x && x < display.frame.x + display.frame.width && y >= display.frame.y && y < display.frame.y + display.frame.height)
}

fn engine(languages: &[String]) -> Result<OcrEngine, String> {
    for tag in languages {
        if let Ok(language) = Language::CreateLanguage(&HSTRING::from(tag.as_str())) {
            if OcrEngine::IsLanguageSupported(&language).unwrap_or(false) {
                if let Ok(engine) = OcrEngine::TryCreateFromLanguage(&language) {
                    return Ok(engine);
                }
            }
        }
    }
    OcrEngine::TryCreateFromUserProfileLanguages().map_err(|_| "no OCR language is installed (Settings > Time & language > Language & region: add a language with Optical character recognition)".to_string())
}

/// The text lines in `request`'s region (default: the focused window, else the primary display), in screen pixels.
pub async fn read_text(request: Request, focused: Option<Frame>) -> Result<Vec<TextLine>, String> {
    let displays = crate::pool::offload(crate::displays::displays).await;
    let main = displays.iter().find(|display| display.main).map(|display| display.frame);
    let region = request.region.or(focused).or(main).ok_or("no display")?;
    let display = display_of(&displays, region).ok_or(desktop_mcp_core::ocr::OUTSIDE_ERROR)?.clone();
    let shot = crate::capture::capture_display(&display).await?;
    let image = crop(&shot, region).ok_or(desktop_mcp_core::ocr::OUTSIDE_ERROR)?;
    let writer = DataWriter::new().map_err(|error| error.message())?;
    writer.WriteBytes(&image.bgra).map_err(|error| error.message())?;
    let buffer = writer.DetachBuffer().map_err(|error| error.message())?;
    let bitmap = SoftwareBitmap::CreateCopyWithAlphaFromBuffer(&buffer, BitmapPixelFormat::Bgra8, image.width as i32, image.height as i32, BitmapAlphaMode::Premultiplied).map_err(|error| error.message())?;
    let engine = engine(&request.languages)?;
    let result = engine.RecognizeAsync(&bitmap).map_err(|error| error.message())?.into_future().await.map_err(|error| error.message())?;
    let mut lines = Vec::new();
    for line in result.Lines().map_err(|error| error.message())? {
        let text = line.Text().map(|text| text.to_string()).unwrap_or_default();
        let (mut left, mut top, mut right, mut bottom) = (f32::MAX, f32::MAX, f32::MIN, f32::MIN);
        for word in line.Words().map_err(|error| error.message())? {
            if let Ok(rect) = word.BoundingRect() {
                left = left.min(rect.X);
                top = top.min(rect.Y);
                right = right.max(rect.X + rect.Width);
                bottom = bottom.max(rect.Y + rect.Height);
            }
        }
        if text.trim().is_empty() || right <= left {
            continue;
        }
        let frame = Frame::new(image.origin.0 as f64 + left as f64, image.origin.1 as f64 + top as f64, (right - left) as f64, (bottom - top) as f64);
        // Windows OCR reports no confidence.
        lines.push(TextLine { text, frame, confidence: 1.0 });
    }
    Ok(lines)
}
