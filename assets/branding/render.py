"""Renders the branding SVGs into the app's icon files. Needs Google Chrome, Pillow and (for .icns) macOS iconutil.

python3 assets/branding/render.py   (from the repo root)
"""
import pathlib, shutil, subprocess, sys, tempfile
from PIL import Image

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parent.parent
ASSETS = ROOT / "src/AiLocalPack.Desktop/Assets"
CHROME = next((p for p in [
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    shutil.which("google-chrome") or "", shutil.which("chromium") or "",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe"] if p and pathlib.Path(p).exists()), None)


def render(svg: pathlib.Path, out: pathlib.Path, width: int, height: int, full_bleed: bool, background: str = "transparent",
           title: str = "", subtitle: str = ""):
    """Draws the SVG with Chrome. full_bleed crops the macOS grid margin (824 of 1024) for Windows / Linux / tray."""
    text = svg.read_text()
    if full_bleed:
        text = text.replace('viewBox="0 0 1024 1024"', 'viewBox="100 100 824 824"')
    with tempfile.TemporaryDirectory() as tmp:
        page = pathlib.Path(tmp) / "page.html"
        page.write_text(f'<html><body style="margin:0;background:{background};overflow:hidden">'
                        f'<div style="width:{width}px;height:{height}px;display:flex;align-items:center;justify-content:center;gap:48px;'
                        f'font-family:system-ui,sans-serif;color:#fff">'
                        f'{text.replace("<svg ", f"<svg style=\"flex:none\" width={min(width, height) - (160 if title else 0)} height={min(width, height) - (160 if title else 0)} ", 1)}'
                        + (f'<div><div style="font-size:76px;font-weight:800;letter-spacing:-.03em;white-space:nowrap">{title}</div>'
                           f'<div style="font-size:34px;opacity:.7;margin-top:8px;max-width:620px">{subtitle}</div></div>' if title else '')
                        + '</div></body></html>')
        subprocess.run([CHROME, "--headless", "--disable-gpu", "--hide-scrollbars", "--default-background-color=00000000",
                        f"--window-size={width},{height}", f"--screenshot={out}", page.as_uri()],
                       check=True, capture_output=True)
    image = Image.open(out)
    if image.size != (width, height):  # Chrome may add a few pixels on some OSes
        image.crop((0, 0, width, height)).save(out)


def main():
    if CHROME is None:
        sys.exit("Google Chrome not found")
    with tempfile.TemporaryDirectory() as tmp:
        tmp = pathlib.Path(tmp)
        full, grid = tmp / "full.png", tmp / "grid.png"
        render(HERE / "app-icon.svg", full, 1024, 1024, full_bleed=True)
        render(HERE / "app-icon.svg", grid, 1024, 1024, full_bleed=False)
        full_img, grid_img = Image.open(full).convert("RGBA"), Image.open(grid).convert("RGBA")

        # Desktop app: tray / notification icon and the multi-size .ico (exe and window icon).
        full_img.resize((64, 64), Image.LANCZOS).save(ASSETS / "tray.png")
        full_img.save(ASSETS / "app-icon.ico", sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
        # Linux (AppImage / Flatpak / .desktop) and docs.
        full_img.resize((512, 512), Image.LANCZOS).save(HERE / "app-icon-512.png")
        # macOS: .icns on the Apple grid (with the margin).
        iconset = tmp / "app-icon.iconset"
        iconset.mkdir()
        for size in (16, 32, 128, 256, 512):
            grid_img.resize((size, size), Image.LANCZOS).save(iconset / f"icon_{size}x{size}.png")
            grid_img.resize((size * 2, size * 2), Image.LANCZOS).save(iconset / f"icon_{size}x{size}@2x.png")
        if shutil.which("iconutil"):
            subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(HERE / "app-icon.icns")], check=True)
        else:
            print("iconutil not found (not macOS): app-icon.icns left as it is")

        # Registry repository: avatar-sized PNG.
        reg = tmp / "registry.png"
        render(HERE / "registry-icon.svg", reg, 1024, 1024, full_bleed=True)
        Image.open(reg).convert("RGBA").resize((512, 512), Image.LANCZOS).save(HERE / "registry-icon-512.png")

        # GitHub social previews (1280×640, icon on the left of a dark band).
        for name, svg, bg, title, subtitle in (
                ("social-preview-app.png", "app-icon.svg", "linear-gradient(135deg,#232845,#11131b)",
                 "ailocalpack", "Install or generate a translation for any game you own"),
                ("social-preview-registry.png", "registry-icon.svg", "linear-gradient(135deg,#1b3631,#0b1412)",
                 "ailocalpack registry", "Community game translations")):
            render(HERE / svg, HERE / name, 1280, 640, full_bleed=True, background=bg, title=title, subtitle=subtitle)


if __name__ == "__main__":
    main()
