use std::collections::VecDeque;
use std::io::Cursor;
use std::path::Path;

use base64::{engine::general_purpose::STANDARD, Engine as _};
use image::{DynamicImage, ImageFormat, Rgba, RgbaImage};
use resvg::{tiny_skia, usvg};
use serde::Serialize;

const MAX_PIXELS: u64 = 24_000_000;
const COMPOSITE_GAP: u32 = 24;
const MIN_REGION_PIXELS: usize = 1;
const REGION_OUTLINE_THICKNESS: u32 = 2;
const DEFAULT_SVG_SIDE: u32 = 1024;
const MAX_SVG_RASTER_SIDE: u32 = 4096;

#[derive(Clone, Copy, PartialEq, Eq)]
enum VisualizationMode {
    Overlay,
    Heatmap,
    Split,
    Composite,
}

#[derive(Clone, Copy)]
struct PixelRegion {
    left: u32,
    top: u32,
    right: u32,
    bottom: u32,
}

#[derive(Serialize)]
struct ImageDiffRenderPayload {
    png_base64: String,
    pixel_width: u32,
    pixel_height: u32,
    changed_pixel_count: u32,
    changed_pixel_ratio: f64,
    changed_region_count: u32,
}

pub fn render_image_diff_json(
    baseline_path: &Path,
    current_path: &Path,
    sensitivity_percent: u32,
    mode: u32,
    split_percent: u32,
    show_region_boxes: bool,
) -> Result<Vec<u8>, String> {
    if !baseline_path.exists() || !baseline_path.is_file() {
        return Err(format!(
            "baseline image not found: {}",
            baseline_path.display()
        ));
    }

    if !current_path.exists() || !current_path.is_file() {
        return Err(format!(
            "current image not found: {}",
            current_path.display()
        ));
    }

    let baseline = load_image_rgba(baseline_path)?;
    let current = load_image_rgba(current_path)?;

    let compare_width = baseline.width().max(current.width());
    let compare_height = baseline.height().max(current.height());

    if compare_width == 0 || compare_height == 0 {
        return Err("image dimensions must be positive".to_string());
    }

    let visualization_mode = VisualizationMode::from_u32(mode);
    let width = if visualization_mode == VisualizationMode::Composite {
        baseline
            .width()
            .saturating_add(current.width())
            .saturating_add(COMPOSITE_GAP)
    } else {
        compare_width
    };
    let height = if visualization_mode == VisualizationMode::Composite {
        baseline.height().max(current.height())
    } else {
        compare_height
    };

    let compare_pixel_count = compare_width as u64 * compare_height as u64;
    let output_pixel_count = width as u64 * height as u64;
    if compare_pixel_count > i32::MAX as u64 || output_pixel_count > MAX_PIXELS {
        return Err("image diff render exceeds the supported pixel budget".to_string());
    }

    let sensitivity = (sensitivity_percent.min(100) as f64) / 100.0;
    let diff_threshold = lerp(2.5, 0.0, sensitivity);
    let split_ratio = (split_percent.min(100) as f64) / 100.0;
    let split_x = ((width as f64) * split_ratio).round() as u32;

    let background = if visualization_mode == VisualizationMode::Composite {
        Rgba([18, 22, 30, 255])
    } else {
        Rgba([0, 0, 0, 0])
    };
    let mut output = RgbaImage::from_pixel(width, height, background);
    let mut mask = vec![false; compare_pixel_count as usize];
    let mut changed_pixels = 0u32;

    for y in 0..compare_height {
        for x in 0..compare_width {
            let baseline_pixel = get_pixel_or_default(&baseline, x, y);
            let current_pixel = get_pixel_or_default(&current, x, y);

            let difference = compute_difference(baseline_pixel, current_pixel);
            let changed = has_any_channel_difference(baseline_pixel, current_pixel);
            if changed {
                let offset = (y as usize * compare_width as usize) + x as usize;
                if offset < mask.len() {
                    mask[offset] = true;
                }
                changed_pixels = changed_pixels.saturating_add(1);
            }

            if visualization_mode == VisualizationMode::Composite {
                continue;
            }

            let pixel = match visualization_mode {
                VisualizationMode::Heatmap => {
                    build_heatmap_pixel(current_pixel, baseline_pixel, difference, diff_threshold)
                }
                VisualizationMode::Split => build_split_pixel(x, split_x, baseline_pixel, current_pixel),
                VisualizationMode::Overlay => {
                    build_overlay_pixel(current_pixel, baseline_pixel, difference, diff_threshold)
                }
                VisualizationMode::Composite => unreachable!(),
            };

            output.put_pixel(x, y, pixel);
        }
    }

    if visualization_mode == VisualizationMode::Composite {
        render_composite(&mut output, &baseline, &current);
    }

    let regions = extract_regions(&mask, compare_width as usize, compare_height as usize);
    if show_region_boxes {
        let region_color = Rgba([255, 225, 94, 255]);
        for region in &regions {
            if visualization_mode == VisualizationMode::Composite {
                let left_region = clamp_region(*region, baseline.width(), baseline.height());
                let right_region = clamp_region(*region, current.width(), current.height());
                draw_region_outline(&mut output, left_region, region_color, 0);
                draw_region_outline(
                    &mut output,
                    right_region,
                    region_color,
                    baseline.width().saturating_add(COMPOSITE_GAP),
                );
            } else {
                let clamped = clamp_region(*region, output.width(), output.height());
                draw_region_outline(&mut output, clamped, region_color, 0);
            }
        }
    }

    if visualization_mode == VisualizationMode::Split {
        draw_split_divider(&mut output, split_x, Rgba([128, 184, 255, 255]));
    } else if visualization_mode == VisualizationMode::Composite {
        let divider_center = baseline.width().saturating_add(COMPOSITE_GAP / 2);
        draw_composite_divider(&mut output, divider_center, Rgba([128, 184, 255, 255]));
    }

    let mut cursor = Cursor::new(Vec::new());
    DynamicImage::ImageRgba8(output.clone())
        .write_to(&mut cursor, ImageFormat::Png)
        .map_err(|e| format!("encode image diff png: {e}"))?;
    let png_bytes = cursor.into_inner();

    let payload = ImageDiffRenderPayload {
        png_base64: STANDARD.encode(&png_bytes),
        pixel_width: output.width(),
        pixel_height: output.height(),
        changed_pixel_count: changed_pixels,
        changed_pixel_ratio: (changed_pixels as f64 / compare_pixel_count as f64).clamp(0.0, 1.0),
        changed_region_count: regions.len() as u32,
    };

    serde_json::to_vec(&payload).map_err(|e| format!("serialize image diff payload: {e}"))
}

impl VisualizationMode {
    fn from_u32(value: u32) -> Self {
        match value {
            1 => Self::Heatmap,
            2 => Self::Split,
            3 => Self::Composite,
            _ => Self::Overlay,
        }
    }
}

fn get_pixel_or_default(image: &RgbaImage, x: u32, y: u32) -> Rgba<u8> {
    if x < image.width() && y < image.height() {
        *image.get_pixel(x, y)
    } else {
        Rgba([0, 0, 0, 0])
    }
}

fn load_image_rgba(path: &Path) -> Result<RgbaImage, String> {
    if is_svg_path(path) {
        return rasterize_svg_rgba(path);
    }

    image::open(path)
        .map_err(|e| format!("load image {}: {e}", path.display()))
        .map(|image| image.to_rgba8())
}

fn rasterize_svg_rgba(path: &Path) -> Result<RgbaImage, String> {
    let svg_data = std::fs::read(path).map_err(|e| format!("read svg {}: {e}", path.display()))?;
    let options = usvg::Options::default();
    let tree = usvg::Tree::from_data(&svg_data, &options)
        .map_err(|e| format!("parse svg {}: {e}", path.display()))?;

    let tree_size = tree.size();
    let natural_width = tree_size.width().round().max(1.0) as u32;
    let natural_height = tree_size.height().round().max(1.0) as u32;
    let mut width = natural_width.max(DEFAULT_SVG_SIDE);
    let mut height = natural_height.max(DEFAULT_SVG_SIDE);

    let max_side = width.max(height);
    if max_side > MAX_SVG_RASTER_SIDE {
        let scale = MAX_SVG_RASTER_SIDE as f32 / max_side as f32;
        width = ((width as f32 * scale).round() as u32).max(1);
        height = ((height as f32 * scale).round() as u32).max(1);
    }

    let mut pixmap =
        tiny_skia::Pixmap::new(width, height).ok_or_else(|| "allocate svg pixmap".to_string())?;

    let transform = tiny_skia::Transform::from_scale(
        width as f32 / natural_width.max(1) as f32,
        height as f32 / natural_height.max(1) as f32,
    );
    resvg::render(&tree, transform, &mut pixmap.as_mut());

    let png_bytes = pixmap
        .encode_png()
        .map_err(|e| format!("encode rasterized svg png {}: {e}", path.display()))?;

    image::load_from_memory(&png_bytes)
        .map_err(|e| format!("decode rasterized svg png {}: {e}", path.display()))
        .map(|image| image.to_rgba8())
}

fn is_svg_path(path: &Path) -> bool {
    path.extension()
        .and_then(|value| value.to_str())
        .map(|value| value.eq_ignore_ascii_case("svg"))
        .unwrap_or(false)
}

fn compute_difference(left: Rgba<u8>, right: Rgba<u8>) -> f64 {
    let dr = (left[0] as f64 - right[0] as f64).abs();
    let dg = (left[1] as f64 - right[1] as f64).abs();
    let db = (left[2] as f64 - right[2] as f64).abs();
    let da = (left[3] as f64 - right[3] as f64).abs();
    let rgb_euclidean = (((dr * dr) + (dg * dg) + (db * db)) / 3.0).sqrt();
    let max_channel = dr.max(dg).max(db);
    max_channel.max((rgb_euclidean * 0.78) + (da * 0.22))
}

fn has_any_channel_difference(left: Rgba<u8>, right: Rgba<u8>) -> bool {
    left[0] != right[0] || left[1] != right[1] || left[2] != right[2] || left[3] != right[3]
}

fn build_overlay_pixel(
    current_pixel: Rgba<u8>,
    baseline_pixel: Rgba<u8>,
    difference: f64,
    threshold: f64,
) -> Rgba<u8> {
    let base_pixel = build_base_display_pixel(if current_pixel[3] > 0 {
        current_pixel
    } else {
        baseline_pixel
    });

    if difference < threshold {
        return base_pixel;
    }

    let strength = ((difference - threshold) / (255.0 - threshold).max(1.0)).clamp(0.0, 1.0);
    let highlight = heat_color(strength);
    blend(base_pixel, highlight, 0.40 + (strength * 0.45))
}

fn build_heatmap_pixel(
    current_pixel: Rgba<u8>,
    baseline_pixel: Rgba<u8>,
    difference: f64,
    threshold: f64,
) -> Rgba<u8> {
    let base_pixel = build_heatmap_base_pixel(if current_pixel[3] > 0 {
        current_pixel
    } else {
        baseline_pixel
    });

    if difference <= 0.0 {
        return base_pixel;
    }

    let normalized = (difference / 255.0).powf(0.42).clamp(0.0, 1.0);
    let emphasized = if difference < threshold {
        ((normalized * 0.70) + 0.30).clamp(0.0, 1.0)
    } else {
        ((normalized * 0.88) + 0.12).clamp(0.0, 1.0)
    };

    let highlight = heat_color(emphasized);
    let opacity = if difference < threshold {
        0.82 + (emphasized * 0.08)
    } else {
        0.93 + (emphasized * 0.05)
    };

    blend(base_pixel, highlight, opacity)
}

fn build_split_pixel(
    x: u32,
    split_x: u32,
    baseline_pixel: Rgba<u8>,
    current_pixel: Rgba<u8>,
) -> Rgba<u8> {
    let visible = if x < split_x { baseline_pixel } else { current_pixel };
    if visible[3] == 0 {
        Rgba([18, 22, 30, 255])
    } else {
        Rgba([visible[0], visible[1], visible[2], 255])
    }
}

fn draw_split_divider(image: &mut RgbaImage, split_x: u32, color: Rgba<u8>) {
    if split_x >= image.width() {
        return;
    }

    for y in 0..image.height() {
        image.put_pixel(split_x, y, color);
        if split_x + 1 < image.width() {
            image.put_pixel(split_x + 1, y, Rgba([238, 245, 255, 255]));
        }
    }
}

fn draw_composite_divider(image: &mut RgbaImage, divider_center_x: u32, color: Rgba<u8>) {
    if divider_center_x >= image.width() {
        return;
    }

    for y in 0..image.height() {
        for dx in 0..=2 {
            let x = divider_center_x.saturating_sub(1).saturating_add(dx);
            if x < image.width() {
                image.put_pixel(x, y, color);
            }
        }
    }
}

fn render_composite(output: &mut RgbaImage, baseline: &RgbaImage, current: &RgbaImage) {
    for y in 0..baseline.height() {
        for x in 0..baseline.width() {
            output.put_pixel(x, y, build_base_display_pixel(*baseline.get_pixel(x, y)));
        }
    }

    let offset_x = baseline.width().saturating_add(COMPOSITE_GAP);
    for y in 0..current.height() {
        for x in 0..current.width() {
            let target_x = offset_x.saturating_add(x);
            if target_x < output.width() {
                output.put_pixel(target_x, y, build_base_display_pixel(*current.get_pixel(x, y)));
            }
        }
    }
}

fn extract_regions(mask: &[bool], width: usize, height: usize) -> Vec<PixelRegion> {
    let mut visited = vec![false; mask.len()];
    let mut queue = VecDeque::new();
    let mut regions = Vec::new();

    for index in 0..mask.len() {
        if !mask[index] || visited[index] {
            continue;
        }

        visited[index] = true;
        queue.push_back(index);

        let mut min_x = usize::MAX;
        let mut min_y = usize::MAX;
        let mut max_x = 0usize;
        let mut max_y = 0usize;
        let mut pixels = 0usize;

        while let Some(current) = queue.pop_front() {
            let x = current % width;
            let y = current / width;

            pixels += 1;
            min_x = min_x.min(x);
            min_y = min_y.min(y);
            max_x = max_x.max(x);
            max_y = max_y.max(y);

            enqueue_if_needed(&mut queue, mask, &mut visited, width, height, x.wrapping_sub(1), y);
            enqueue_if_needed(&mut queue, mask, &mut visited, width, height, x + 1, y);
            enqueue_if_needed(&mut queue, mask, &mut visited, width, height, x, y.wrapping_sub(1));
            enqueue_if_needed(&mut queue, mask, &mut visited, width, height, x, y + 1);
        }

        if pixels >= MIN_REGION_PIXELS {
            regions.push(PixelRegion {
                left: min_x as u32,
                top: min_y as u32,
                right: max_x as u32,
                bottom: max_y as u32,
            });
        }
    }

    regions
}

fn enqueue_if_needed(
    queue: &mut VecDeque<usize>,
    mask: &[bool],
    visited: &mut [bool],
    width: usize,
    height: usize,
    x: usize,
    y: usize,
) {
    if x >= width || y >= height {
        return;
    }

    let offset = (y * width) + x;
    if visited[offset] || !mask[offset] {
        return;
    }

    visited[offset] = true;
    queue.push_back(offset);
}

fn draw_region_outline(image: &mut RgbaImage, region: PixelRegion, color: Rgba<u8>, x_offset: u32) {
    for y in region.top..=region.bottom {
        for x in region.left..=region.right {
            if x.saturating_sub(region.left) < REGION_OUTLINE_THICKNESS
                || region.right.saturating_sub(x) < REGION_OUTLINE_THICKNESS
                || y.saturating_sub(region.top) < REGION_OUTLINE_THICKNESS
                || region.bottom.saturating_sub(y) < REGION_OUTLINE_THICKNESS
            {
                let target_x = x.saturating_add(x_offset);
                if target_x < image.width() && y < image.height() {
                    image.put_pixel(target_x, y, color);
                }
            }
        }
    }
}

fn build_base_display_pixel(pixel: Rgba<u8>) -> Rgba<u8> {
    if pixel[3] == 0 {
        return Rgba([18, 22, 30, 255]);
    }

    Rgba([
        (pixel[0] as f64 * 0.7).round().clamp(0.0, 255.0) as u8,
        (pixel[1] as f64 * 0.7).round().clamp(0.0, 255.0) as u8,
        (pixel[2] as f64 * 0.7).round().clamp(0.0, 255.0) as u8,
        255,
    ])
}

fn build_heatmap_base_pixel(pixel: Rgba<u8>) -> Rgba<u8> {
    if pixel[3] == 0 {
        return Rgba([8, 10, 14, 255]);
    }

    let luminance = ((pixel[0] as f64 * 0.2126)
        + (pixel[1] as f64 * 0.7152)
        + (pixel[2] as f64 * 0.0722))
        .round();
    let toned = ((luminance * 0.18) + 8.0).round().clamp(0.0, 255.0) as u8;
    Rgba([toned, toned, toned, 255])
}

fn clamp_region(region: PixelRegion, width: u32, height: u32) -> PixelRegion {
    if width == 0 || height == 0 {
        return PixelRegion {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
    }

    let left = region.left.min(width - 1);
    let top = region.top.min(height - 1);
    let right = region.right.clamp(left, width - 1);
    let bottom = region.bottom.clamp(top, height - 1);
    PixelRegion {
        left,
        top,
        right,
        bottom,
    }
}

fn blend(base_pixel: Rgba<u8>, overlay_pixel: Rgba<u8>, opacity: f64) -> Rgba<u8> {
    let clamped = opacity.clamp(0.0, 1.0);
    let inverse = 1.0 - clamped;

    Rgba([
        ((base_pixel[0] as f64 * inverse) + (overlay_pixel[0] as f64 * clamped))
            .round()
            .clamp(0.0, 255.0) as u8,
        ((base_pixel[1] as f64 * inverse) + (overlay_pixel[1] as f64 * clamped))
            .round()
            .clamp(0.0, 255.0) as u8,
        ((base_pixel[2] as f64 * inverse) + (overlay_pixel[2] as f64 * clamped))
            .round()
            .clamp(0.0, 255.0) as u8,
        255,
    ])
}

fn heat_color(normalized: f64) -> Rgba<u8> {
    let clamped = normalized.clamp(0.0, 1.0);
    if clamped < 0.5 {
        let local = clamped / 0.5;
        return lerp_color(Rgba([64, 174, 255, 255]), Rgba([255, 221, 82, 255]), local);
    }

    lerp_color(
        Rgba([255, 221, 82, 255]),
        Rgba([255, 84, 84, 255]),
        (clamped - 0.5) / 0.5,
    )
}

fn lerp_color(start: Rgba<u8>, end: Rgba<u8>, t: f64) -> Rgba<u8> {
    let clamped = t.clamp(0.0, 1.0);
    Rgba([
        lerp(start[0] as f64, end[0] as f64, clamped).round().clamp(0.0, 255.0) as u8,
        lerp(start[1] as f64, end[1] as f64, clamped).round().clamp(0.0, 255.0) as u8,
        lerp(start[2] as f64, end[2] as f64, clamped).round().clamp(0.0, 255.0) as u8,
        255,
    ])
}

fn lerp(start: f64, end: f64, t: f64) -> f64 {
    start + ((end - start) * t.clamp(0.0, 1.0))
}
