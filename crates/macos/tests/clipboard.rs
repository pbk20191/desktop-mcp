//! The clipboard on a private pasteboard: the cases of the Swift port's ClipboardToolTests.
mod support;
use desktop_mcp_core::clipboard::{Clipboard, ClipboardWrite, IMAGE_MAX_SIDE};
use desktop_mcp_core::mcp::Desktop;
use desktop_mcp_macos::desktop::MacDesktop;
use desktop_mcp_macos::main_loop::run;
use objc2_app_kit::{NSPasteboard, NSPasteboardTypePNG};
use objc2_core_foundation::{CFMutableData, CFRetained, CFString, CGRect, CGPoint, CGSize};
use objc2_core_graphics::{CGBitmapContextCreate, CGBitmapContextCreateImage, CGColorSpace, CGContext, CGImageAlphaInfo, kCGColorSpaceSRGB};
use objc2_foundation::{NSData, NSString};
use objc2_image_io::CGImageDestination;
use std::path::PathBuf;

/// A pasteboard of its own for each test, released afterwards.
struct Board(String);
impl Board {
    fn new() -> Board {
        Board(format!("desktop-mcp-test-{}-{}", std::process::id(), std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()))
    }
    fn pasteboard(&self) -> objc2::rc::Retained<NSPasteboard> {
        NSPasteboard::pasteboardWithName(&NSString::from_str(&self.0))
    }
    fn desktop(&self) -> MacDesktop {
        MacDesktop::with_pasteboard(&self.0)
    }
    fn read(&self) -> Clipboard {
        let desktop = self.desktop();
        run(move |_| async move { desktop.read_clipboard(IMAGE_MAX_SIDE).await })
    }
    fn write(&self, contents: ClipboardWrite) -> Result<(), String> {
        let desktop = self.desktop();
        run(move |_| async move { desktop.write_clipboard(contents).await })
    }
}
impl Drop for Board {
    fn drop(&mut self) {
        let _: () = unsafe { objc2::msg_send![&*self.pasteboard(), releaseGlobally] };
    }
}

fn png(width: usize, height: usize) -> Vec<u8> {
    unsafe {
        let space = CGColorSpace::with_name(Some(kCGColorSpaceSRGB)).unwrap();
        let context: CFRetained<CGContext> = CGBitmapContextCreate(std::ptr::null_mut(), width, height, 8, 0, Some(&space), CGImageAlphaInfo::PremultipliedLast.0).unwrap();
        CGContext::set_rgb_fill_color(Some(&context), 1.0, 0.0, 0.0, 1.0);
        CGContext::fill_rect(Some(&context), CGRect { origin: CGPoint { x: 0.0, y: 0.0 }, size: CGSize { width: width as f64, height: height as f64 } });
        let image = CGBitmapContextCreateImage(Some(&context)).unwrap();
        let data = CFMutableData::new(None, 0).unwrap();
        let destination = CGImageDestination::with_data(&data, &CFString::from_str("public.png"), 1, None).unwrap();
        destination.add_image(&image, None);
        assert!(destination.finalize());
        data.to_vec()
    }
}

/// A PNG's size, from its header.
fn png_size(png: &[u8]) -> (u32, u32) {
    assert_eq!(&png[..8], b"\x89PNG\r\n\x1a\n");
    (u32::from_be_bytes(png[16..20].try_into().unwrap()), u32::from_be_bytes(png[20..24].try_into().unwrap()))
}

fn temporary_file(directory: &tempfile::TempDir, name: &str) -> PathBuf {
    let path = directory.path().canonicalize().unwrap().join(name);
    std::fs::write(&path, "x").unwrap();
    path
}

fn text_is_written_and_read_back() {
    let board = Board::new();
    board.write(ClipboardWrite::Text("안녕 hi".into())).unwrap();
    assert_eq!(board.read(), Clipboard { text: Some("안녕 hi".into()), ..Clipboard::default() });
}

fn files_are_written_and_read_back() {
    let board = Board::new();
    let directory = tempfile::tempdir().unwrap();
    let (first, second) = (temporary_file(&directory, "a.txt"), temporary_file(&directory, "문서 b.txt"));
    board.write(ClipboardWrite::Files(vec![first.clone(), second.clone()])).unwrap();
    let read = board.read();
    assert_eq!(read.files, [first.to_str().unwrap(), second.to_str().unwrap()]);
}

fn a_large_image_is_scaled_to_the_maximum_side() {
    let board = Board::new();
    let pasteboard = board.pasteboard();
    pasteboard.clearContents();
    assert!(pasteboard.setData_forType(Some(&NSData::with_bytes(&png(3000, 1000))), unsafe { NSPasteboardTypePNG }));
    let image = board.read().image.expect("an image");
    assert_eq!((image.width, image.height), (1568, 523));
    assert_eq!(png_size(&image.png), (1568, 523));
}

fn main() {
    support::run(&[
        ("text_is_written_and_read_back", text_is_written_and_read_back),
        ("files_are_written_and_read_back", files_are_written_and_read_back),
        ("a_large_image_is_scaled_to_the_maximum_side", a_large_image_is_scaled_to_the_maximum_side),
    ])
}
