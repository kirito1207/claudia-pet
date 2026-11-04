"""Turn the generated fall/bump sprites into game-ready WebP files.

Usage (Python 3 with Pillow, numpy and scipy):
    py -I scripts/prepare-sprites.py <folder with the original PNGs> [--out characters/claude] [--sheet compare.png]

What it does for each sprite:
  1. Reads the transparent PNG and snaps nearly opaque pixels (alpha >= 250) to fully opaque.
  2. Optionally removes small detached pieces (the grey sway arcs on the stagger images).
  3. Scales it so the character matches the standing sprites (see SCALE below), crops the empty space,
     and puts it on a transparent canvas 640 px high, standing on the bottom edge, centred horizontally.
     Unlike CrossPet's prepare.py it never scales a sprite up to 640 px high, so sitting and lying poses
     stay their real size.
  4. Saves WebP (quality 92).
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage

HEIGHT = 640
WIDTH = 640  # wider sprites get clipped in the pet window
# The bump sprite (character 1174 px high in the 1254 px original) maps to the full 640 px, like the
# standing sprites. The other factors were tuned by eye in the pet window next to idle.webp, so the
# character reads about as big as she usually does (sitting and lying poses stay a little lower).
BASE = HEIGHT / 1174

# original file name -> (output name, size factor, remove detached sway arcs)
SPRITES = {
    "橙发花饰少女的受击姿态.png": ("bonk", 1.0, False),
    "眩晕摇摆的花饰少女.png": ("stagger", F_STAND := 0.95, True),
    "晕乎乎的橙发花饰少女.png": ("stagger-2", F_STAND, True),
    "橙发少女跌倒抱书萌态插画.png": ("fallen", 0.92, False),
    "克劳德趴地脸朝下的可爱场景.png": ("faceplant", 0.95, False),
    "橙发少女努力起身.png": ("getup", 0.92, False),
}


def clean(a, specks, keep_ratio=0.01, solid=64, edge=3):
    """Keep only the clearly visible pieces plus a few pixels of their soft edge.

    Pieces are found on clearly opaque pixels (alpha >= solid), because faint noise can bridge a sway arc
    to the hair. With specks=True every piece smaller than keep_ratio of the largest is dropped too (the
    sway arcs), which still keeps a dropped book. Everything else, including stray faint noise, is cleared.
    """
    strong = a[:, :, 3] >= solid
    labels, n = ndimage.label(strong, structure=np.ones((3, 3)))
    sizes = ndimage.sum(strong, labels, range(1, n + 1))
    removed = []
    keep = np.zeros_like(strong)
    for i, size in enumerate(sizes, start=1):
        if specks and size < sizes.max() * keep_ratio:
            if size >= 50:
                ys, xs = np.nonzero(labels == i)
                removed.append((int(size), (int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max()))))
        else:
            keep |= labels == i
    keep = ndimage.binary_dilation(keep, iterations=edge)
    a[~keep] = 0
    return a, removed


def prepare(path, factor, specks):
    im = Image.open(path).convert("RGBA")
    a = np.array(im)
    alpha = a[:, :, 3]
    alpha[alpha >= 250] = 255
    a, removed = clean(a, specks)
    im = Image.fromarray(a)
    im = im.crop(im.getchannel("A").getbbox())
    scale = BASE * factor
    w, h = round(im.width * scale), round(im.height * scale)
    if h > HEIGHT:  # never taller than the canvas
        w, h = round(w * HEIGHT / h), HEIGHT
    if w > WIDTH:
        w, h = WIDTH, round(h * WIDTH / w)
    im = im.resize((w, h), Image.LANCZOS)
    canvas = Image.new("RGBA", (w + 2, HEIGHT), (0, 0, 0, 0))
    canvas.paste(im, (1, HEIGHT - h - 1), im)
    return canvas, removed


def sheet(paths, out):
    """Side-by-side check at the scale the pet shows them: all bottoms on one floor line."""
    ims = [Image.open(p).convert("RGBA") for p in paths]
    gap = 20
    W = sum(i.width for i in ims) + gap * (len(ims) + 1)
    img = Image.new("RGBA", (W, HEIGHT + 60), (205, 225, 205, 255))
    d = ImageDraw.Draw(img)
    x = gap
    for p, i in zip(paths, ims):
        img.alpha_composite(i, (x, 20))
        d.text((x, HEIGHT + 30), os.path.splitext(os.path.basename(p))[0], fill="black")
        x += i.width + gap
    d.line([(0, HEIGHT + 20), (W, HEIGHT + 20)], fill="red")
    img.convert("RGB").save(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("source")
    chars = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "characters", "claude")
    ap.add_argument("--out", default=chars)
    ap.add_argument("--sheet")
    args = ap.parse_args()
    outputs = []
    for src, (name, factor, specks) in SPRITES.items():
        path = os.path.join(args.source, src)
        if not os.path.exists(path):
            sys.exit(f"missing: {path}")
        im, removed = prepare(path, factor, specks)
        dest = os.path.join(args.out, name + ".webp")
        im.save(dest, "WEBP", quality=92, method=6)
        outputs.append(dest)
        print(f"{name}: {im.width}x{im.height}, removed {len(removed)} piece(s) {removed}")
    if args.sheet:
        refs = [os.path.join(chars, n + ".webp") for n in ("idle", "dizzy", "compact")]
        sheet(refs + outputs, args.sheet)


if __name__ == "__main__":
    main()
