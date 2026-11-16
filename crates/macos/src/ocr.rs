//! OCR on macOS: every display the region touches is captured the way Snapshot's screenshot is, then decoded,
//! cropped to the region and read by Vision's text recognizer as a Core Image image (the crop copies no pixels), on
//! a worker.
use crate::ax::{Element, Value};
use crate::displays::DisplayInfo;
use desktop_mcp_core::ocr::{crop, to_screen, Request, TextLine, CAPTURE_ERROR, OUTSIDE_ERROR};
use desktop_mcp_core::window::Frame;
use objc2::rc::Retained;
use objc2::AnyThread;
use objc2_core_foundation::{CGAffineTransform, CGPoint, CGRect, CGSize};
use objc2_core_image::CIImage;
use objc2_foundation::{NSArray, NSData, NSDictionary, NSString};
use objc2_vision::{VNImageRequestHandler, VNRecognizeTextRequest, VNRequest, VNRequestTextRecognitionLevel};

/// The focused window's frame, else the main display's, in screen points. Reads Accessibility: off the main thread.
fn default_region() -> Frame {
    let window = crate::apps::frontmost().and_then(|pid| Element::application(pid).attribute("AXMainWindow")?.element().cloned());
    let frame = window.and_then(|window| match (window.attribute("AXPosition"), window.attribute("AXSize")) {
        (Some(Value::Point(x, y)), Some(Value::Size(w, h))) if w > 0.0 && h > 0.0 => Some(Frame::new(x, y, w, h)),
        _ => None,
    });
    frame.unwrap_or_else(|| {
        let main = objc2_core_graphics::CGDisplayBounds(objc2_core_graphics::CGMainDisplayID());
        Frame::new(main.origin.x, main.origin.y, main.size.width, main.size.height)
    })
}

/// The lines Vision finds in `image` at or above the request's confidence, each with its normalized, bottom-left
/// origin box.
fn recognize(image: &CIImage, request: &Request) -> Result<Vec<(String, Frame, f32)>, String> {
    let text = unsafe { VNRecognizeTextRequest::init(VNRecognizeTextRequest::alloc()) };
    text.setRecognitionLevel(if request.fast { VNRequestTextRecognitionLevel::Fast } else { VNRequestTextRecognitionLevel::Accurate });
    let languages: Vec<Retained<NSString>> = request.languages.iter().map(|language| NSString::from_str(language)).collect();
    text.setRecognitionLanguages(&NSArray::from_retained_slice(&languages));
    text.setUsesLanguageCorrection(true);
    let handler = unsafe { VNImageRequestHandler::initWithCIImage_options(VNImageRequestHandler::alloc(), image, &NSDictionary::new()) };
    let requests: Retained<NSArray<VNRequest>> = NSArray::from_slice(&[&**text]);
    handler.performRequests_error(&requests).map_err(|error| format!("Text recognition failed: {}", error.localizedDescription()))?;
    let observations = text.results().map(|results| results.to_vec()).unwrap_or_default();
    Ok(observations
        .iter()
        .filter_map(|observation| {
            let best = observation.topCandidates(1).firstObject()?;
            let confidence = best.confidence();
            if confidence < request.min_confidence {
                return None;
            }
            let b = unsafe { observation.boundingBox() };
            Some((best.string().to_string(), Frame::new(b.origin.x, b.origin.y, b.size.width, b.size.height), confidence))
        })
        .collect())
}

/// The text lines in the request's region, in screen points, unordered.
pub async fn read(request: Request) -> Result<Vec<TextLine>, String> {
    let region = match request.region {
        Some(region) => region,
        None => crate::main_loop::offload(default_region).await,
    };
    let displays: Vec<DisplayInfo> = crate::displays::all().into_iter().filter(|display| crop(display.logical, region, (display.pixel_width, display.pixel_height)).is_some()).collect();
    if displays.is_empty() {
        return Err(OUTSIDE_ERROR.to_string());
    }
    if !objc2_core_graphics::CGPreflightScreenCaptureAccess() {
        return Err(CAPTURE_ERROR.to_string());
    }
    let ids = crate::screenshot::active_ids();
    let mut lines = Vec::new();
    let mut captured = false;
    for display in &displays {
        let Some(png) = crate::screenshot::capture(display, &ids).await else { continue };
        captured = true;
        let (frame, request) = (display.logical, request.clone());
        let found = crate::main_loop::offload(move || -> Result<Vec<TextLine>, String> {
            let Some(shot) = (unsafe { CIImage::imageWithData(&NSData::with_bytes(&png)) }) else { return Ok(vec![]) };
            let extent = unsafe { shot.extent() };
            let size = (extent.size.width as usize, extent.size.height as usize);
            let Some(crop) = crop(frame, region, size) else { return Ok(vec![]) };
            let (x0, y0, x1, y1) = crop.pixels;
            let pixels = (x1 - x0, y1 - y0);
            // Core Image's origin is bottom-left; the crop keeps its offset, so it is moved back to the origin.
            let bottom = size.1 - y1;
            let rect = CGRect { origin: CGPoint { x: x0 as f64, y: bottom as f64 }, size: CGSize { width: pixels.0 as f64, height: pixels.1 as f64 } };
            let moved = CGAffineTransform { a: 1.0, b: 0.0, c: 0.0, d: 1.0, tx: -(x0 as f64), ty: -(bottom as f64) };
            let image = unsafe { shot.imageByCroppingToRect(rect).imageByApplyingTransform(moved) };
            Ok(recognize(&image, &request)?.into_iter().map(|(text, normalized, confidence)| TextLine { text, frame: to_screen(normalized, pixels, &crop), confidence }).collect())
        })
        .await?;
        lines.extend(found);
    }
    if !captured {
        return Err(CAPTURE_ERROR.to_string());
    }
    Ok(lines)
}

