//! Annotation and PNG encoding on synthetic pixels (no screen capture in tests).
#![cfg(windows)]
use desktop_mcp_core::snapshot::{BoundingBox, TreeNode};
use desktop_mcp_windows::imaging::{annotate, encode_png, Pixels};

fn white(width: u32, height: u32) -> Pixels {
    Pixels { width, height, bgra: vec![255; (width * height * 4) as usize], origin: (100, 50) }
}

#[test]
fn annotate_draws_inside_the_box_only() {
    let mut pixels = white(200, 100);
    // Screen coordinates: the image's origin is (100, 50), so this box is at (20..80, 20..60) in the image.
    let element = TreeNode { bounding_box: BoundingBox::from_edges(120.0, 70.0, 180.0, 110.0), name: "OK".into(), control_type: "Button".into(), window_name: "W".into(), metadata: vec![] };
    annotate(&mut pixels, &[element]).unwrap();
    let changed = |x: u32, y: u32| pixels.bgra[((y * pixels.width + x) * 4) as usize..][..3] != [255, 255, 255];
    assert!((20..=80).any(|x| changed(x, 20)), "the box's top edge is drawn");
    for (x, y) in [(5, 5), (195, 95), (100, 90), (150, 10)] {
        assert!(!changed(x, y), "({x},{y}) is outside the box and its label");
    }
}

#[test]
fn encode_png_scales_the_long_side() {
    let png = encode_png(&white(3000, 1000), Some(1568)).unwrap();
    assert_eq!(&png[..8], b"\x89PNG\r\n\x1a\n");
    let width = u32::from_be_bytes(png[16..20].try_into().unwrap());
    let height = u32::from_be_bytes(png[20..24].try_into().unwrap());
    assert_eq!((width, height), (1568, 523));
    let png = encode_png(&white(10, 5), None).unwrap();
    assert_eq!(u32::from_be_bytes(png[16..20].try_into().unwrap()), 10);
}
