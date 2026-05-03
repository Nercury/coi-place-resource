use std::path::PathBuf;
use std::process::ExitCode;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 3 || args.len() > 4 {
        eprintln!("usage: svg-rasterize <input.svg> <output.png> [size_px]");
        eprintln!("  size_px defaults to 256.");
        return ExitCode::from(2);
    }

    let input = PathBuf::from(&args[1]);
    let output = PathBuf::from(&args[2]);
    let size: u32 = if args.len() == 4 {
        match args[3].parse() {
            Ok(v) if v > 0 => v,
            _ => {
                eprintln!("invalid size: {}", args[3]);
                return ExitCode::from(2);
            }
        }
    } else {
        256
    };

    let svg_data = match std::fs::read(&input) {
        Ok(b) => b,
        Err(e) => {
            eprintln!("read {:?}: {}", input, e);
            return ExitCode::from(1);
        }
    };

    let opt = usvg::Options::default();
    let tree = match usvg::Tree::from_data(&svg_data, &opt) {
        Ok(t) => t,
        Err(e) => {
            eprintln!("parse SVG: {}", e);
            return ExitCode::from(1);
        }
    };

    // Compute scale so the rasterized image is exactly `size` x `size`. Assumes the SVG's
    // viewBox is square, which our toolbar-icon.svg is (0 0 100 100). Non-square viewBoxes
    // would render letterboxed, which is fine for our use.
    let svg_size = tree.size();
    let scale = size as f32 / svg_size.width().max(svg_size.height());

    let mut pixmap = match tiny_skia::Pixmap::new(size, size) {
        Some(p) => p,
        None => {
            eprintln!("alloc pixmap {}x{}", size, size);
            return ExitCode::from(1);
        }
    };

    resvg::render(
        &tree,
        tiny_skia::Transform::from_scale(scale, scale),
        &mut pixmap.as_mut(),
    );

    if let Err(e) = pixmap.save_png(&output) {
        eprintln!("write {:?}: {}", output, e);
        return ExitCode::from(1);
    }

    println!("wrote {:?} ({}x{})", output, size, size);
    ExitCode::SUCCESS
}
