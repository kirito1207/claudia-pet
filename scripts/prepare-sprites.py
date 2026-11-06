"""Turn the generated sprites (fall/bump poses, and the standing poses redrawn in the animation proportions) into game-ready WebP files.

Usage (Python 3 with Pillow, numpy and scipy):
    py -I scripts/prepare-sprites.py <folder with the original PNGs> [--out characters/claude] [--sheet compare.png] [--only name,name]

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
    # getup（爬起）2026-11-06 起不用了：原图 01e1ee1d…_720.png（第三版）、橙发少女努力起身.png（第一版）
}

# 2026-11-05 按动画比例重画的 20 张站立立绘（提示词见 docs/prompts/立绘重画-统一比例/）。
# 生图时人物大小不固定（同一套提示词，人物高 1114–1242 px 不等），所以不用固定系数，而是每张单独缩放：
# 人物从脚底到头顶缩到 PERSON 高（和待机图 idle.webp 一样）。帽子不算人物：第三个数是帽子高出头顶多少像素（原图里量的），
# 帽子也要装进 640 高的画布，装不下时整张缩小（戴派对帽的元旦会比别的小一点）。
# original file name -> (output name, hat px above the head, remove detached pieces)
PERSON = 638
STANDING = {
    # 第二轮（21:25 放进来的这一批）。第一轮（20:25）按旧提示词画，腿露太多、人偏瘦高，没用
    "f290517545a8fbc18e5cae6f4b36edae_720.png": ("listening", 0, False),
    "fdca2cd3f5645517d1356d76f333faaa_720.png": ("reading", 0, False),
    "502c419be016cf212c25f9673c50521b_720.png": ("busy_left", 0, False),
    "be3f062a7950f18cf7eff77d83672445_720.png": ("busy_right", 0, False),
    "28ef8401960f0826115cd4d6a494bbfd_720.png": ("searching", 0, False),
    "923f6301baf6769ecfee7c471fdfba0e_720.png": ("oops", 0, False),
    "c38cbab2c033c78b9e2362b99132a80b_720.png": ("tired", 0, False),
    "652d9dbc99c5b4fd4e57c104fe83c0f1_720.png": ("puffed", 0, False),
    "6a54bf089f8413ef42b9af0933ffdaeb_720.png": ("sulk", 0, False),
    "4f15013daf5fd80e0d08c7e7fd6c69ad_720.png": ("dizzy", 0, False),
    "4f44f9804ae59e69d9ea8a77a9629187_720.png": ("coaxed", 0, False),
    "aefe895d0a39b69bcbdbdc75261e04d9_720.png": ("forgiven", 0, False),
    "96a5a8f850cae2a6b6c056ef63b9af51_720.png": ("greet_dawn", 75, False),
    "cf7a0e5de88ff904afbbcb144d32332f_720.png": ("greet_morning", 0, False),
    "b84ffd377046421c9908894ef4cf05ac_720.png": ("greet_noon", 0, False),
    "761b2dca91f1a8f1ab55b3555160b3d1_720.png": ("greet_afternoon", 0, False),
    "a55e95904b1b2265dca781e5c81e4f81_720.png": ("greet_evening", 0, False),
    "48596cda49720f7cf208f4e49d1ccf39_720.png": ("greet_night", 0, False),
    "66ecac6aa29a22e4a97e887e41137342_720.png": ("fest_newyear", 140, False),
    # fest_national（国庆）2026-11-05 用户决定不要了，原图 f7ce2230…_720.png 没用
    # 2026-11-06 得意按新比例重画（Codex 命令行，提示词 docs/prompts/立绘重画-统一比例/得意-2026-11-06-提示词.md）；花冠高出头顶约 125 px
    "proud_codex_8c33ee85.png": ("proud", 125, False),
}


# 2026-11-06 碰边一套按新比例重画 + 补帧（Codex 命令行生成，原图在 D:\claudia图codex生成，提示词见
# docs/prompts/碰边补帧/）。连着翻页播放，所以同一套里人物大小和位置要接得上：
#   - 缩放是原图像素的固定倍数（凭眼按头的大小和待机图对齐；生图时坐着、跪着的头会画得偏大）
#   - 水平按头的中线对齐（画面上 30% 里不透明像素的平均 x），书掉在哪边人都不跳；趴地两张身体横着，按整体中心
#   - 画布左右对称地包住对齐线，网页按画布中心摆放，对齐线就落在中间
# original file name -> (output name, scale, anchor: 'head' / 'box')
SEQUENCE = {
    "bonk_codex_b4a59525.png": ("bonk", 0.515, "head"),
    "bonk-2_codex_edb0559f.png": ("bonk-2", 0.52, "head"),
    "bonk-3_codex_4fb85672.png": ("bonk-3", 0.515, "head"),
    "stagger_codex_5c0b840e.png": ("stagger", 0.51, "head"),
    "stagger-2_codex_7bead917.png": ("stagger-2", 0.50, "head"),
    "stagger-3_codex_e91054b2.png": ("stagger-3", 0.51, "head"),
    "faceplant_codex_8505d95c.png": ("faceplant", 0.52, "box"),
    "faceplant-2_codex_fe7688c3.png": ("faceplant-2", 0.60, "box"),
    "fallen-1_codex_886961a1.png": ("fallen-1", 0.50, "head"),
    "fallen_codex_c400066f.png": ("fallen", 0.49, "head"),
    # fallen-2 第二版 864e3e50 头顶头发堆太高、像在生气（用户 2026-11-06 说头奇怪、糊），换第三版
    "fallen-2_codex_105ef5c9.png": ("fallen-2", 0.48, "head"),
    # 爬起三张（getup_codex_e8aded8d / getup-2_codex_d327e671 / getup-3_codex_06631ed6，缩放 0.50 / 0.49 / 0.528）
    # 2026-11-06 用户决定不要爬起：坐着、趴着两种倒法接不上同一套爬起，倒地后直接站回去接情绪
}
MAX_WIDTH = 880  # 画布再宽，网页里 object-fit: contain 会把整张缩小，和别的帧不一样大


def prepare_anchored(path, scale, anchor):
    im = Image.open(path).convert("RGBA")
    a = np.array(im)
    a[:, :, 3][a[:, :, 3] >= 250] = 255
    a, removed = clean(a, False)
    solid = a[:, :, 3] >= 64
    ys, xs = np.nonzero(solid)
    top, bottom, left, right = ys.min(), ys.max(), xs.min(), xs.max()
    if anchor == "head":
        band = solid[top:top + int((bottom - top) * 0.3)]
        cx = np.nonzero(band)[1].mean()
    else:
        cx = (left + right) / 2
    im = Image.fromarray(a).crop((0, top, a.shape[1], bottom + 1))
    w, h = round(im.width * scale), round(im.height * scale)
    im = im.resize((w, h), Image.LANCZOS)
    cx *= scale
    l, r = left * scale, (right + 1) * scale
    half = int(np.ceil(max(cx - l, r - cx))) + 2
    if h > HEIGHT or 2 * half > MAX_WIDTH:
        sys.exit(f"{path}: too big after scaling ({2 * half}x{h})")
    canvas = Image.new("RGBA", (2 * half, HEIGHT), (0, 0, 0, 0))
    canvas.paste(im, (round(half - cx), HEIGHT - h - 1), im)
    return canvas, removed


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


def prepare(path, factor, specks, hat=None):
    """factor：按原图像素乘 BASE * factor；hat 不为 None 时改成按人物身高缩放（扣掉 hat 像素的帽子），见 STANDING"""
    im = Image.open(path).convert("RGBA")
    a = np.array(im)
    alpha = a[:, :, 3]
    alpha[alpha >= 250] = 255
    a, removed = clean(a, specks)
    im = Image.fromarray(a)
    im = im.crop(im.getchannel("A").getbbox())
    scale = BASE * factor if hat is None else PERSON / (im.height - hat)
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
    ap.add_argument("--only", help="只处理这几张（输出名，逗号分隔），例如 --only listening,reading")
    args = ap.parse_args()
    only = set(args.only.split(",")) if args.only else None
    # 碰边一套的旧原图（SPRITES）已被 SEQUENCE 取代：同名输出以 SEQUENCE 为准
    seq_names = {name for name, _, _ in SEQUENCE.values()}
    jobs = [(src, name, factor, specks, None) for src, (name, factor, specks) in SPRITES.items() if name not in seq_names]
    jobs += [(src, name, None, specks, hat) for src, (name, hat, specks) in STANDING.items()]
    jobs += [(src, name, scale, anchor, "seq") for src, (name, scale, anchor) in SEQUENCE.items()]
    if only:
        unknown = only - {j[1] for j in jobs}
        if unknown:
            sys.exit(f"unknown: {', '.join(sorted(unknown))}")
        jobs = [j for j in jobs if j[1] in only]
    outputs = []
    for src, name, factor, specks, hat in jobs:
        path = os.path.join(args.source, src)
        if not os.path.exists(path):
            sys.exit(f"missing: {path}")
        im, removed = prepare_anchored(path, factor, specks) if hat == "seq" else prepare(path, factor, specks, hat)
        dest = os.path.join(args.out, name + ".webp")
        im.save(dest, "WEBP", quality=92, method=6)
        outputs.append(dest)
        print(f"{name}: {im.width}x{im.height}, removed {len(removed)} piece(s) {removed}")
    if args.sheet:
        refs = [os.path.join(chars, n + ".webp") for n in ("idle", "bonk")]
        sheet(refs + outputs, args.sheet)


if __name__ == "__main__":
    main()
