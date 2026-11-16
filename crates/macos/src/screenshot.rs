//! The annotated screenshot of `Snapshot(use_vision=True)`: every display captured by the system's
//! `screencapture` tool, stitched left to right, a numbered box drawn over each element (ids match the snapshot's),
//! scaled and encoded as PNG off the event loop.
use crate::clipboard::{bitmap_context, encode_png};
use crate::displays::DisplayInfo;
use desktop_mcp_core::snapshot::{BoundingBox, TreeNode};
use objc2_core_foundation::{CFAttributedString, CFData, CFDictionary, CFRetained, CFString, CFType, CGAffineTransform, CGPoint, CGRect, CGSize};
use objc2_core_graphics::{CGBitmapContextCreateImage, CGColor, CGContext, CGGetActiveDisplayList, CGImage, CGInterpolationQuality};
use objc2_core_text::{kCTFontAttributeName, kCTForegroundColorAttributeName, CTFont, CTLine};
use objc2_image_io::CGImageSource;
use std::collections::HashSet;
use std::time::Duration;

const PADDING: usize = 5;

/// Asks for Screen Recording once when it is not granted; whether it is granted.
pub fn ensure_permission() -> bool {
    objc2_core_graphics::CGPreflightScreenCaptureAccess() || objc2_core_graphics::CGRequestScreenCaptureAccess()
}

/// The displays' ids in the window server's order, which `screencapture -D` numbers from 1.
fn active_ids() -> Vec<u32> {
    let mut count = 0u32;
    unsafe { CGGetActiveDisplayList(0, std::ptr::null_mut(), &mut count) };
    let mut ids = vec![0u32; count as usize];
    unsafe { CGGetActiveDisplayList(count, ids.as_mut_ptr(), &mut count) };
    ids.truncate(count as usize);
    ids
}

/// One display as PNG bytes, or `None` when it could not be captured (no permission, display gone).
async fn capture(display: &DisplayInfo, ids: &[u32]) -> Option<Vec<u8>> {
    let number = ids.iter().position(|id| *id == display.id)? + 1;
    let path = std::env::temp_dir().join(format!("desktop-mcp-{}.png", uuid::Uuid::new_v4()));
    let argv = ["/usr/sbin/screencapture", "-x", "-D", &number.to_string(), "-t", "png", &path.to_string_lossy()].map(str::to_string).to_vec();
    let run = crate::process::run(argv, Duration::from_secs(10)).await;
    let bytes = std::fs::read(&path).ok();
    let _ = std::fs::remove_file(&path);
    bytes.filter(|_| run.is_ok_and(|run| run.status == 0))
}

/// The annotated screenshot as PNG, `scale` (< 1) shrinking it to logical points; `None` when nothing could be
/// captured.
pub async fn annotated(nodes: Vec<TreeNode>, scale: f64) -> Option<Vec<u8>> {
    let displays = crate::displays::all();
    let ids = active_ids();
    let mut captures = Vec::new();
    for display in &displays {
        captures.push(capture(display, &ids).await);
    }
    if captures.iter().all(Option::is_none) {
        return None;
    }
    crate::main_loop::offload(move || compose(&displays, &captures, &nodes, scale)).await
}

fn decode(png: &[u8]) -> Option<CFRetained<CGImage>> {
    let source = unsafe { CGImageSource::with_data(&CFData::from_bytes(png), None) }?;
    unsafe { source.image_at_index(0, None) }
}

fn rect(x: f64, y: f64, width: f64, height: f64) -> CGRect {
    CGRect { origin: CGPoint { x, y }, size: CGSize { width, height } }
}

fn compose(displays: &[DisplayInfo], captures: &[Option<Vec<u8>>], nodes: &[TreeNode], scale: f64) -> Option<Vec<u8>> {
    // Every display at native resolution, left to right, tops aligned.
    let width: usize = displays.iter().map(|d| d.pixel_width).sum();
    let height = displays.iter().map(|d| d.pixel_height).max()?;
    let canvas = bitmap_context(width, height)?;
    CGContext::set_gray_fill_color(Some(&canvas), 0.0, 1.0);
    CGContext::fill_rect(Some(&canvas), rect(0.0, 0.0, width as f64, height as f64));
    for (display, png) in displays.iter().zip(captures) {
        let Some(image) = png.as_deref().and_then(decode) else { continue };
        // Bottom-left origin: a display's top edge sits at the top of the canvas.
        let at = rect(display.pixel_left as f64, (height - display.pixel_height) as f64, display.pixel_width as f64, display.pixel_height as f64);
        CGContext::draw_image(Some(&canvas), at, Some(&image));
    }
    let shot = CGBitmapContextCreateImage(Some(&canvas))?;
    let annotated = annotate(&shot, nodes, displays)?;
    let last = if scale > 0.0 && scale < 1.0 { resize(&annotated, scale).unwrap_or(annotated) } else { annotated };
    encode_png(&last)
}

fn annotate(shot: &CGImage, nodes: &[TreeNode], displays: &[DisplayInfo]) -> Option<CFRetained<CGImage>> {
    let (shot_width, shot_height) = (CGImage::width(Some(shot)), CGImage::height(Some(shot)));
    let width = (shot_width as f64 + 1.5 * PADDING as f64) as usize;
    let height = (shot_height as f64 + 1.5 * PADDING as f64) as usize;
    let context = bitmap_context(width, height)?;
    let c = Some(&*context);
    CGContext::set_rgb_fill_color(c, 1.0, 1.0, 1.0, 1.0);
    CGContext::fill_rect(c, rect(0.0, 0.0, width as f64, height as f64));
    CGContext::draw_image(c, rect(PADDING as f64, (height - PADDING - shot_height) as f64, shot_width as f64, shot_height as f64), Some(shot));
    // Everything below is in top-left coordinates.
    CGContext::translate_ctm(c, 0.0, height as f64);
    CGContext::scale_ctm(c, 1.0, -1.0);
    CGContext::set_text_matrix(c, CGAffineTransform { a: 1.0, b: 0.0, c: 0.0, d: -1.0, tx: 0.0, ty: 0.0 });

    let font_scale = displays.first().map_or(1.0, |d| d.scale);
    let font = unsafe { CTFont::with_name(&CFString::from_static_str("Helvetica"), (14.0 * font_scale).floor().max(12.0), std::ptr::null()) };
    let virtual_left = displays.first().map_or(0.0, |d| d.logical.x);
    let virtual_top = displays.iter().map(|d| d.logical.y).fold(f64::INFINITY, f64::min);
    let virtual_top = if virtual_top.is_finite() { virtual_top } else { 0.0 };
    let logical_width: f64 = displays.iter().map(|d| d.logical.width).sum();
    let average_scale = shot_width as f64 / logical_width.max(1.0);
    let to_pixels = |x: f64, y: f64, display: Option<&DisplayInfo>| -> (i64, i64) {
        match display {
            Some(d) => (d.pixel_left as i64 + ((x - d.logical.x) * d.scale) as i64 + PADDING as i64, ((y - d.logical.y) * d.scale) as i64 + PADDING as i64),
            None => (((x - virtual_left) * average_scale) as i64 + PADDING as i64, ((y - virtual_top) * average_scale) as i64 + PADDING as i64),
        }
    };

    let mut seen: HashSet<BoundingBox> = HashSet::new();
    for (label, node) in nodes.iter().enumerate() {
        let b = node.bounding_box;
        if b.width <= 0 || b.height <= 0 || !seen.insert(b) {
            continue;
        }
        let (cx, cy) = ((b.left + b.right) as f64 / 2.0, (b.top + b.bottom) as f64 / 2.0);
        let display = displays.iter().find(|d| d.contains(cx, cy));
        let (x1, y1) = to_pixels(b.left as f64, b.top as f64, display);
        let (x2, y2) = to_pixels(b.right as f64, b.bottom as f64, display);
        let (r, g, bl) = label_color(label);
        CGContext::set_rgb_stroke_color(c, r, g, bl, 1.0);
        CGContext::set_line_width(c, 2.0);
        CGContext::stroke_rect(c, rect(x1 as f64 + 1.0, y1 as f64 + 1.0, (x2 - x1) as f64 - 2.0, (y2 - y1) as f64 - 2.0));

        let white = CGColor::new_srgb(1.0, 1.0, 1.0, 1.0);
        let keys: [&CFString; 2] = unsafe { [kCTFontAttributeName, kCTForegroundColorAttributeName] };
        let values: [&CFType; 2] = [&font, &white];
        let attributes = CFDictionary::<CFString, CFType>::from_slices(&keys, &values);
        let text = unsafe { CFAttributedString::new(None, Some(&CFString::from_str(&label.to_string())), Some(attributes.as_opaque())) }?;
        let line = unsafe { CTLine::with_attributed_string(&text) };
        let (mut ascent, mut descent, mut leading) = (0.0, 0.0, 0.0);
        let text_width = unsafe { line.typographic_bounds(&mut ascent, &mut descent, &mut leading) }.ceil() as i64;
        let text_height = (ascent + descent).ceil() as i64;
        // The tag sits above the box's right edge, or below it when there is no room.
        let tag_x = x2 - text_width - 4;
        let mut tag_y = y1 - text_height - 4;
        if tag_y < PADDING as i64 {
            tag_y = y2;
        }
        CGContext::set_rgb_fill_color(c, r, g, bl, 1.0);
        CGContext::fill_rect(c, rect(tag_x as f64, tag_y as f64, (text_width + 4) as f64, (text_height + 4) as f64));
        CGContext::set_text_position(c, (tag_x + 2) as f64, (tag_y + 2) as f64 + ascent);
        unsafe { line.draw(&context) };
    }
    CGBitmapContextCreateImage(Some(&context))
}

/// A stable, readable colour per label (each channel 50 to 255): a splitmix sequence seeded by the label.
fn label_color(label: usize) -> (f64, f64, f64) {
    let mut state = (label as u64).wrapping_add(0x9E37_79B9_7F4A_7C15);
    let mut next = || {
        state = (state ^ (state >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        state = (state ^ (state >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        state ^= state >> 31;
        (50 + state % 206) as f64 / 255.0
    };
    (next(), next(), next())
}

fn resize(image: &CGImage, scale: f64) -> Option<CFRetained<CGImage>> {
    let width = ((CGImage::width(Some(image)) as f64 * scale) as usize).max(1);
    let height = ((CGImage::height(Some(image)) as f64 * scale) as usize).max(1);
    let context = bitmap_context(width, height)?;
    CGContext::set_interpolation_quality(Some(&context), CGInterpolationQuality::High);
    CGContext::draw_image(Some(&context), rect(0.0, 0.0, width as f64, height as f64), Some(image));
    CGBitmapContextCreateImage(Some(&context))
}
