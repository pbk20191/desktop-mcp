//! The OCR tool: reads the text on screen with the platform's text recognition and reports where each line is, in
//! the screen points Click takes. Opt-in ([`OPT_IN`]): recognition is slow.
use crate::mcp::Desktop;
use crate::window::Frame;

/// Tools listed only when named in `include` (`[tools] include`, `--include-tools`).
pub const OPT_IN: &[&str] = &["OCR"];

pub const LINE_LIMIT: usize = 500;
pub const DEFAULT_LANGUAGES: [&str; 2] = ["ko-KR", "en-US"];
pub const DEFAULT_MIN_CONFIDENCE: f64 = 0.3;
pub const REGION_ERROR: &str = "region must be [x, y, width, height] with positive width and height";
pub const CONFIDENCE_ERROR: &str = "min_confidence must be between 0 and 1";
pub const OUTSIDE_ERROR: &str = "region is outside every display.";
pub const CAPTURE_ERROR: &str = "Screen capture failed. If this persists, grant Screen Recording in System Settings > Privacy & Security.";

/// What to read. `region` in screen points; `None` means the focused window, else the main display.
#[derive(Clone, Debug, PartialEq)]
pub struct Request {
    pub region: Option<Frame>,
    pub languages: Vec<String>,
    pub fast: bool,
    pub min_confidence: f32,
}

/// One recognized line, `frame` in screen points (origin top-left of the main display).
#[derive(Clone, Debug, PartialEq)]
pub struct TextLine {
    pub text: String,
    pub frame: Frame,
    pub confidence: f32,
}

/// The pixels of one display's capture that show `region`, and how they map back to screen points.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Crop {
    /// Left, top, right, bottom, in the capture's pixels (top-left origin).
    pub pixels: (usize, usize, usize, usize),
    /// Where the cropped pixels start, in points (the intersection's top-left, to the pixel).
    pub origin: (f64, f64),
    /// Pixels per point of this capture (a capture may differ from the display mode).
    pub scale: f64,
}

/// The part of a capture of the display at `display` (points), `shot` pixels wide and high, that shows `region`;
/// `None` when they do not overlap.
pub fn crop(display: Frame, region: Frame, shot: (usize, usize)) -> Option<Crop> {
    let left = display.x.max(region.x);
    let top = display.y.max(region.y);
    let right = (display.x + display.width).min(region.x + region.width);
    let bottom = (display.y + display.height).min(region.y + region.height);
    if right <= left || bottom <= top || display.width <= 0.0 {
        return None;
    }
    let scale = shot.0 as f64 / display.width;
    let x0 = ((left - display.x) * scale).floor().max(0.0);
    let y0 = ((top - display.y) * scale).floor().max(0.0);
    let x1 = ((right - display.x) * scale).ceil().min(shot.0 as f64);
    let y1 = ((bottom - display.y) * scale).ceil().min(shot.1 as f64);
    if x1 <= x0 || y1 <= y0 {
        return None;
    }
    Some(Crop { pixels: (x0 as usize, y0 as usize, x1 as usize, y1 as usize), origin: (display.x + x0 / scale, display.y + y0 / scale), scale })
}

/// A recognizer's box (normalized 0-1, bottom-left origin) in an image of `image` pixels cropped as `crop`, as
/// screen points.
pub fn to_screen(normalized: Frame, image: (usize, usize), crop: &Crop) -> Frame {
    let (w, h) = (image.0 as f64, image.1 as f64);
    Frame::new(
        crop.origin.0 + normalized.x * w / crop.scale,
        crop.origin.1 + (1.0 - normalized.y - normalized.height) * h / crop.scale,
        normalized.width * w / crop.scale,
        normalized.height * h / crop.scale,
    )
}

/// Top to bottom, then left to right: lines whose vertical centres differ by less than half the median line
/// height share a row.
pub fn order(mut lines: Vec<TextLine>) -> Vec<TextLine> {
    if lines.is_empty() {
        return lines;
    }
    let mut heights: Vec<f64> = lines.iter().map(|line| line.frame.height).collect();
    heights.sort_by(f64::total_cmp);
    let tolerance = heights[heights.len() / 2] / 2.0;
    let mid_y = |line: &TextLine| line.frame.y + line.frame.height / 2.0;
    lines.sort_by(|a, b| mid_y(a).total_cmp(&mid_y(b)));
    let mut rows: Vec<Vec<TextLine>> = Vec::new();
    for line in lines {
        match rows.last_mut() {
            Some(row) if (mid_y(&line) - mid_y(&row[0])).abs() < tolerance => row.push(line),
            _ => rows.push(vec![line]),
        }
    }
    rows.into_iter()
        .flat_map(|mut row| {
            row.sort_by(|a, b| a.frame.x.total_cmp(&b.frame.x));
            row
        })
        .collect()
}

/// `[cx,cy] "<text>" (w×h, confidence 0.93)` per line, at most [`LINE_LIMIT`] lines.
pub fn format(lines: &[TextLine]) -> String {
    if lines.is_empty() {
        return "No text found.".to_string();
    }
    let mut out: Vec<String> = lines
        .iter()
        .take(LINE_LIMIT)
        .map(|line| {
            let f = &line.frame;
            let (cx, cy) = ((f.x + f.width / 2.0).floor() as i64, (f.y + f.height / 2.0).floor() as i64);
            format!("[{cx},{cy}] \"{}\" ({}×{}, confidence {:.2})", line.text, f.width.round() as i64, f.height.round() as i64, line.confidence)
        })
        .collect();
    if lines.len() > LINE_LIMIT {
        out.push(format!("{} more lines not shown", lines.len() - LINE_LIMIT));
    }
    out.join("\n")
}

/// The tool's arguments checked and turned into a [`Request`].
pub fn request(region: Option<Vec<i64>>, languages: Option<Vec<String>>, fast: bool, min_confidence: f64) -> Result<Request, String> {
    let region = match region.as_deref() {
        None => None,
        Some(&[x, y, width, height]) if width > 0 && height > 0 => Some(Frame::new(x as f64, y as f64, width as f64, height as f64)),
        Some(_) => return Err(REGION_ERROR.to_string()),
    };
    if !(0.0..=1.0).contains(&min_confidence) {
        return Err(CONFIDENCE_ERROR.to_string());
    }
    let languages = languages.unwrap_or_else(|| DEFAULT_LANGUAGES.map(str::to_string).to_vec());
    Ok(Request { region, languages, fast, min_confidence: min_confidence as f32 })
}

pub async fn run(desktop: &dyn Desktop, request: Request) -> Result<String, String> {
    let lines = desktop.read_text(request).await?;
    Ok(format(&order(lines)))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn crop_maps_a_region_on_a_retina_display() {
        let display = Frame::new(0.0, 0.0, 1000.0, 500.0);
        let crop = crop(display, Frame::new(100.0, 50.0, 200.0, 100.0), (2000, 1000)).unwrap();
        assert_eq!(crop, Crop { pixels: (200, 100, 600, 300), origin: (100.0, 50.0), scale: 2.0 });
        assert_eq!(super::crop(display, Frame::new(1000.0, 0.0, 10.0, 10.0), (2000, 1000)), None);
        // A region reaching past the display is clipped to it.
        let second = Frame::new(1000.0, -100.0, 800.0, 600.0);
        let clipped = super::crop(second, Frame::new(900.0, 0.0, 200.0, 50.0), (800, 600)).unwrap();
        assert_eq!(clipped, Crop { pixels: (0, 100, 100, 150), origin: (1000.0, 0.0), scale: 1.0 });
    }

    #[test]
    fn to_screen_flips_the_bottom_left_box() {
        let crop = Crop { pixels: (200, 100, 600, 300), origin: (100.0, 50.0), scale: 2.0 };
        // The top-left quarter of a 400×200-pixel image is 100×50 points at the crop's origin.
        assert_eq!(to_screen(Frame::new(0.0, 0.5, 0.5, 0.5), (400, 200), &crop), Frame::new(100.0, 50.0, 100.0, 50.0));
    }
}
