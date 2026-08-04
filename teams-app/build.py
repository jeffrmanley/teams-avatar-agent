# /// script
# requires-python = ">=3.10"
# dependencies = ["Pillow>=10.0"]
# ///
"""Build the Iris Teams app package.

Generates the two required icons (color 192×192, outline 32×32) and zips
them together with manifest.json into ``iris-teams-app.zip`` — the file
Teams Admin Center / Teams client expects for sideload.

Run with ``uv``:

    uv run teams-app/build.py

PEP-723 inline metadata at the top means uv resolves Pillow on the fly
into a throwaway venv; no separate pyproject / requirements file.

Why programmatic icons: Microsoft's sideload validator rejects packages
with the wrong dimensions or non-PNG icons. Generating them from code
guarantees they're correct every time and makes branding tweaks a
one-line edit instead of a Photoshop session.
"""

from __future__ import annotations

import sys
import zipfile
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
OUT_ZIP = HERE / "iris-teams-app.zip"

# Iris brand: indigo background with a single white "I" glyph. Matches the
# manifest's accentColor (#4f46e5).
ACCENT = (79, 70, 229)  # #4f46e5
WHITE  = (255, 255, 255)


def _draw_glyph(draw: ImageDraw.ImageDraw, size: int, color: tuple[int, int, int]) -> None:
    """Draw a chunky letter 'I' centered in a ``size`` × ``size`` canvas.

    Uses a hand-built serif-ish I (top bar + stem + bottom bar) instead of
    a typed font character because Pillow's default font is too thin at
    small sizes and any real font would be a system-dep risk.
    """
    cx          = size // 2
    stem_w      = max(2, size // 8)
    stem_top    = size // 4
    stem_bottom = size - size // 4
    bar_w       = max(4, size // 3)
    bar_h       = max(2, size // 14)

    # Top bar
    draw.rectangle(
        [(cx - bar_w // 2, stem_top), (cx + bar_w // 2, stem_top + bar_h)],
        fill=color,
    )
    # Stem
    draw.rectangle(
        [(cx - stem_w // 2, stem_top), (cx + stem_w // 2, stem_bottom)],
        fill=color,
    )
    # Bottom bar
    draw.rectangle(
        [(cx - bar_w // 2, stem_bottom - bar_h), (cx + bar_w // 2, stem_bottom)],
        fill=color,
    )


def make_color_icon(path: Path, size: int = 192) -> None:
    """Solid indigo circle on transparent, with white 'I' glyph centered."""
    img  = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    margin = size // 16
    draw.ellipse(
        [(margin, margin), (size - margin, size - margin)],
        fill=ACCENT + (255,),
    )
    _draw_glyph(draw, size, WHITE)
    img.save(path, format="PNG", optimize=True)


def make_outline_icon(path: Path, size: int = 32) -> None:
    """Transparent background with a white 'I' glyph.

    Teams Admin Center requires the outline icon to be monochrome on a
    transparent background — it gets used in the dark-themed sidebar and
    Teams recolors it according to the user's theme.
    """
    img  = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    _draw_glyph(draw, size, WHITE)
    img.save(path, format="PNG", optimize=True)


def main() -> int:
    color   = HERE / "color.png"
    outline = HERE / "outline.png"
    manifest = HERE / "manifest.json"
    if not manifest.exists():
        print(f"ERROR: {manifest} missing", file=sys.stderr)
        return 1

    make_color_icon(color)
    make_outline_icon(outline)
    print(f"  wrote {color} (192×192)")
    print(f"  wrote {outline} (32×32)")

    with zipfile.ZipFile(OUT_ZIP, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.write(manifest, "manifest.json")
        zf.write(color,    "color.png")
        zf.write(outline,  "outline.png")
    print(f"  wrote {OUT_ZIP}")
    print()
    print("Sideload via Teams Admin Center -> Manage apps -> Upload, or in the")
    print("Teams client via Apps -> Manage your apps -> Upload a custom app.")
    print("See teams-app/README.md for the full runbook.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
