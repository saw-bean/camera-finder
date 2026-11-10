import sys, math
# Draws the Camera Finder icon (CCTV camera sending discovery waves) as a multi-size .ico.
# Usage: python3 tools/make_icon.py <out.ico> <preview-512.png> <preview-32.png>   (needs Pillow)
from PIL import Image, ImageDraw

S = 1024  # draw large, downsample for crisp edges

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))

def render(detail=True):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    # Background: rounded tile, vertical gradient navy -> blue.
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    top, bot = (16, 42, 92, 255), (28, 110, 210, 255)
    for y in range(S): gd.line([(0, y), (S, y)], fill=lerp(top, bot, y / (S - 1)))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([24, 24, S - 24, S - 24], radius=210, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    white = (255, 255, 255, 255)
    cyan = (64, 224, 255, 255)

    # Camera body: CCTV bullet tilted down to the left, hanging from a wall/ceiling arm at the upper right.
    body = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    bd = ImageDraw.Draw(body)
    bd.rounded_rectangle([330, 360, 830, 560], radius=64, fill=white)                 # housing
    bd.rounded_rectangle([300, 340, 860, 392], radius=24, fill=white)                 # sun shield
    bd.rounded_rectangle([280, 384, 370, 548], radius=28, fill=(214, 228, 244, 255))  # lens bezel
    bd.ellipse([246, 410, 362, 526], fill=(12, 30, 70, 255))                          # lens
    bd.ellipse([272, 436, 336, 500], fill=cyan)                                       # glass
    if detail: bd.ellipse([284, 446, 310, 472], fill=white)                           # glint
    pivot = (600, 460)
    body = body.rotate(24, center=pivot, resample=Image.BICUBIC)                      # lens end goes down
    # Arm from the camera's back up to the mount plate.
    d.rounded_rectangle([700, 250, 760, 470], radius=26, fill=white)
    d.rounded_rectangle([640, 200, 860, 268], radius=28, fill=white)
    img.alpha_composite(body)

    # Lens position after rotation (rotate is counter-clockwise about pivot, y axis down).
    lx, ly = 304 - pivot[0], 468 - pivot[1]
    t = math.radians(24)
    lens = (pivot[0] + lx * math.cos(t) + ly * math.sin(t), pivot[1] - lx * math.sin(t) + ly * math.cos(t))
    # Discovery waves coming out of the lens, down and to the left.
    radii = (150, 245, 340) if detail else (170, 290)
    for i, r in enumerate(radii):
        w = 40 if detail else 64
        x, y = lens
        d.arc([x - r, y - r, x + r, y + r], start=112, end=168, fill=(140, 220, 255, 255 - i * 50), width=w)
    # Keep everything inside the rounded tile.
    clipped = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    clipped.paste(img, (0, 0), mask)
    return clipped

big = render(True)
small = render(False)  # bolder strokes for 16-32 px
out = sys.argv[1]
sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [(small if s <= 32 else big).resize((s, s), Image.LANCZOS) for s in sizes]
frames[-1].save(out, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
big.resize((512, 512), Image.LANCZOS).save(sys.argv[2])
small.resize((32, 32), Image.LANCZOS).resize((128, 128), Image.NEAREST).save(sys.argv[3])
