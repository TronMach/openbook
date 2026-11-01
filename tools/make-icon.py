"""Makes packaging/windows/openbook.ico (the desktop launcher's and installer's icon) from the app's 512 px icon.

    python tools/make-icon.py

Needs Pillow. Run again when wwwroot/icons/icon-512.png changes, and commit the .ico.
"""
from pathlib import Path

from PIL import Image

root = Path(__file__).resolve().parent.parent
source = root / "src" / "OpenBook.Web" / "wwwroot" / "icons" / "icon-512.png"
target = root / "packaging" / "windows" / "openbook.ico"

image = Image.open(source).convert("RGBA")
image.save(target, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print(f"{target.relative_to(root)}: {target.stat().st_size} bytes")
