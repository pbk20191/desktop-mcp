//! The clipboard: an `NSPasteboard`, used from a libdispatch worker (pasteboard calls talk to the pasteboard
//! server and can wait on another app's promised data). Only the pasteboard's name crosses threads; the
//! `NSPasteboard` is looked up on the worker each time.
use desktop_mcp_core::clipboard::{Clipboard, ClipboardWrite, Image};
use objc2::rc::Retained;
use objc2::runtime::{AnyClass, AnyObject};
use objc2::ClassType;
use objc2_app_kit::{NSPasteboard, NSPasteboardTypePNG, NSPasteboardTypeString, NSPasteboardTypeTIFF, NSPasteboardURLReadingFileURLsOnlyKey};
use objc2_core_foundation::{CFData, CFMutableData, CFRetained, CFString, CGPoint, CGRect, CGSize};
use objc2_core_graphics::{kCGColorSpaceSRGB, CGBitmapContextCreate, CGBitmapContextCreateImage, CGColorSpace, CGContext, CGImage, CGImageAlphaInfo, CGInterpolationQuality};
use objc2_foundation::{NSArray, NSDictionary, NSNumber, NSString, NSURL};
use objc2_image_io::{CGImageDestination, CGImageSource};

/// The general pasteboard, or the one named.
fn pasteboard(name: Option<&str>) -> Retained<NSPasteboard> {
    match name {
        Some(name) => NSPasteboard::pasteboardWithName(&NSString::from_str(name)),
        None => NSPasteboard::generalPasteboard(),
    }
}

pub fn read(name: Option<&str>, image_max_side: u32) -> Clipboard {
    let pasteboard = pasteboard(name);
    let text = pasteboard.stringForType(unsafe { NSPasteboardTypeString }).map(|text| text.to_string());
    let classes = NSArray::from_slice(&[NSURL::class() as &AnyClass]);
    let file_urls_only = NSDictionary::<NSString, AnyObject>::from_slices(&[unsafe { NSPasteboardURLReadingFileURLsOnlyKey }], &[&*NSNumber::new_bool(true) as &AnyObject]);
    let files = unsafe { pasteboard.readObjectsForClasses_options(&classes, Some(&file_urls_only)) }
        // File URLs carry decomposed (NFD) names; composed is what a person types and a model reads, and the file
        // system accepts either.
        .map(|objects| objects.iter().filter_map(|object| Some(object.downcast::<NSURL>().ok()?.path()?.precomposedStringWithCanonicalMapping().to_string())).collect())
        .unwrap_or_default();
    let png = pasteboard.dataForType(unsafe { NSPasteboardTypePNG });
    let image = match &png {
        Some(png) => scaled_png(&png.to_vec(), true, image_max_side),
        None => pasteboard.dataForType(unsafe { NSPasteboardTypeTIFF }).and_then(|tiff| scaled_png(&tiff.to_vec(), false, image_max_side)),
    };
    Clipboard { text, files, image }
}

pub fn write(name: Option<&str>, contents: ClipboardWrite) -> Result<(), String> {
    let pasteboard = pasteboard(name);
    pasteboard.clearContents();
    let written = match contents {
        ClipboardWrite::Text(text) => pasteboard.setString_forType(&NSString::from_str(&text), unsafe { NSPasteboardTypeString }),
        ClipboardWrite::Files(files) => {
            let urls: Vec<Retained<NSURL>> = files.iter().map(|file| NSURL::fileURLWithPath(&NSString::from_str(&file.to_string_lossy()))).collect();
            let urls = NSArray::from_retained_slice(&urls);
            // NSURL conforms to NSPasteboardWriting in AppKit, which the binding does not declare.
            unsafe { objc2::msg_send![&*pasteboard, writeObjects: &*urls] }
        }
    };
    if written { Ok(()) } else { Err("Could not write to the clipboard.".to_string()) }
}

/// PNG bytes for `data` (PNG or any image ImageIO reads), scaled down so the longer side is at most `max_side`.
fn scaled_png(data: &[u8], is_png: bool, max_side: u32) -> Option<Image> {
    let source = unsafe { CGImageSource::with_data(&CFData::from_bytes(data), None) }?;
    let image = unsafe { source.image_at_index(0, None) }?;
    let (width, height) = (CGImage::width(Some(&image)), CGImage::height(Some(&image)));
    let (max_side, longest) = (max_side as usize, width.max(height));
    if longest <= max_side {
        let png = if is_png { data.to_vec() } else { encode_png(&image)? };
        return Some(Image { png, width: width as u32, height: height as u32 });
    }
    let ratio = max_side as f64 / longest as f64;
    let scaled_width = if width >= height { max_side } else { ((width as f64 * ratio).round() as usize).max(1) };
    let scaled_height = if height > width { max_side } else { ((height as f64 * ratio).round() as usize).max(1) };
    let context = bitmap_context(scaled_width, scaled_height)?;
    CGContext::set_interpolation_quality(Some(&context), CGInterpolationQuality::High);
    let bounds = CGRect { origin: CGPoint { x: 0.0, y: 0.0 }, size: CGSize { width: scaled_width as f64, height: scaled_height as f64 } };
    CGContext::draw_image(Some(&context), bounds, Some(&image));
    let scaled = CGBitmapContextCreateImage(Some(&context))?;
    Some(Image { png: encode_png(&scaled)?, width: scaled_width as u32, height: scaled_height as u32 })
}

/// An sRGB, 8-bit, premultiplied RGBA bitmap.
pub(crate) fn bitmap_context(width: usize, height: usize) -> Option<CFRetained<CGContext>> {
    let space = CGColorSpace::with_name(Some(unsafe { kCGColorSpaceSRGB }))?;
    unsafe { CGBitmapContextCreate(std::ptr::null_mut(), width, height, 8, 0, Some(&space), CGImageAlphaInfo::PremultipliedLast.0) }
}

pub(crate) fn encode_png(image: &CGImage) -> Option<Vec<u8>> {
    let data = CFMutableData::new(None, 0)?;
    let destination = unsafe { CGImageDestination::with_data(&data, &CFString::from_static_str("public.png"), 1, None) }?;
    unsafe { destination.add_image(image, None) };
    unsafe { destination.finalize() }.then(|| data.to_vec())
}
