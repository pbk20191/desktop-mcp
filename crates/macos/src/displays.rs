//! The displays, as the window server and NSScreen describe them.
use desktop_mcp_core::window::{DisplayFrame, Frame};
use objc2::MainThreadMarker;
use objc2_app_kit::NSScreen;
use objc2_core_graphics::{CGDisplayBounds, CGDisplayCopyDisplayMode, CGDisplayMode, CGDisplayPixelsHigh, CGDisplayPixelsWide, CGGetActiveDisplayList, CGMainDisplayID};
use objc2_foundation::{NSNumber, NSString};

/// Geometry of one active display.
#[derive(Clone, Debug, PartialEq)]
pub struct DisplayInfo {
    pub id: u32,
    /// Points, origin top-left of the main display.
    pub logical: Frame,
    pub pixel_width: usize,
    pub pixel_height: usize,
    /// Pixels per point.
    pub scale: f64,
    /// Horizontal offset of this display inside a left-to-right stitch of all displays.
    pub pixel_left: usize,
}

impl DisplayInfo {
    pub fn contains(&self, x: f64, y: f64) -> bool {
        let f = &self.logical;
        f.x <= x && x < f.x + f.width && f.y <= y && y < f.y + f.height
    }
}

/// Active displays, left to right, with `pixel_left` filled in for a left-to-right stitch.
pub fn all() -> Vec<DisplayInfo> {
    let mut count = 0u32;
    if unsafe { CGGetActiveDisplayList(0, std::ptr::null_mut(), &mut count) }.0 != 0 || count == 0 {
        return vec![];
    }
    let mut ids = vec![0u32; count as usize];
    if unsafe { CGGetActiveDisplayList(count, ids.as_mut_ptr(), &mut count) }.0 != 0 {
        return vec![];
    }
    let mut displays: Vec<DisplayInfo> = ids[..count as usize]
        .iter()
        .map(|&id| {
            let bounds = CGDisplayBounds(id);
            let mode = CGDisplayCopyDisplayMode(id);
            let pixel_width = mode.as_deref().map_or_else(|| CGDisplayPixelsWide(id), |mode| CGDisplayMode::pixel_width(Some(mode)));
            let pixel_height = mode.as_deref().map_or_else(|| CGDisplayPixelsHigh(id), |mode| CGDisplayMode::pixel_height(Some(mode)));
            DisplayInfo {
                id,
                logical: Frame::new(bounds.origin.x, bounds.origin.y, bounds.size.width, bounds.size.height),
                pixel_width,
                pixel_height,
                scale: if bounds.size.width > 0.0 { pixel_width as f64 / bounds.size.width } else { 1.0 },
                pixel_left: 0,
            }
        })
        .collect();
    displays.sort_by(|a, b| a.logical.x.total_cmp(&b.logical.x));
    let mut left = 0;
    for display in &mut displays {
        display.pixel_left = left;
        left += display.pixel_width;
    }
    displays
}

/// The display containing the point, in points.
pub fn containing(x: f64, y: f64) -> Option<DisplayInfo> {
    all().into_iter().find(|display| display.contains(x, y))
}

/// Every screen with its visible frame, in [`all`] order, top-left origin. Reads NSScreen: main thread.
pub fn frames(mtm: MainThreadMarker) -> Vec<DisplayFrame> {
    let screens = NSScreen::screens(mtm);
    // The first screen has the menu bar; its bottom-left is the Cocoa origin.
    let Some(main_height) = screens.iter().next().map(|screen| screen.frame().size.height) else { return vec![] };
    let key = NSString::from_str("NSScreenNumber");
    let by_id: Vec<(u32, objc2::rc::Retained<NSScreen>)> = screens
        .iter()
        .filter_map(|screen| {
            let number = screen.deviceDescription().objectForKey(&key)?.downcast::<NSNumber>().ok()?;
            Some((number.unsignedIntValue(), screen))
        })
        .collect();
    let main_id = CGMainDisplayID();
    let rect = |r: objc2_foundation::NSRect| Frame::new(r.origin.x, r.origin.y, r.size.width, r.size.height);
    all()
        .iter()
        .enumerate()
        .filter_map(|(index, display)| {
            let (_, screen) = by_id.iter().find(|(id, _)| *id == display.id)?;
            Some(DisplayFrame::from_cocoa(index, &screen.localizedName().to_string(), rect(screen.frame()), rect(screen.visibleFrame()), main_height, screen.backingScaleFactor(), display.id == main_id))
        })
        .collect()
}
