# Builds every icon size from his artwork (assets/source/ibdm-icon-original.png):
# the black background around the glass tile becomes transparent, keeping the red glow.
import sys, os
from PIL import Image, ImageDraw, ImageFilter
root = sys.argv[1]
src = Image.open(f"{root}/assets/source/ibdm-icon-original.png").convert("RGBA")
w, h = src.size
px = src.load()
# Alpha from brightness for the near-black surroundings; the tile itself is bright, so it stays opaque.
out = Image.new("RGBA", (w, h))
op = out.load()
for y in range(h):
    for x in range(w):
        r, g, b, a = px[x, y]
        m = max(r, g, b)
        if m < 60:
            alpha = int(max(0, (m - 18)) / 42 * 255)
        else:
            alpha = 255
        op[x, y] = (r, g, b, min(a, alpha))
# Opaque inside the tile no matter how dark (the black B): fill the tile's rounded square.
mask = Image.new("L", (w, h), 0)
ImageDraw.Draw(mask).rounded_rectangle([int(w*0.075), int(h*0.075), int(w*0.925), int(h*0.925)], radius=int(w*0.14), fill=255)
alpha = out.getchannel("A")
alpha = Image.composite(Image.new("L", (w, h), 255), alpha, mask)
out.putalpha(alpha)
bbox = out.getchannel("A").point(lambda v: 255 if v > 24 else 0).getbbox()
out = out.crop(bbox)
side = max(out.size)
sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
sq.paste(out, ((side - out.width) // 2, (side - out.height) // 2))
big = sq.resize((1024, 1024), Image.LANCZOS)
os.makedirs(f"{root}/assets", exist_ok=True)
big.resize((256, 256), Image.LANCZOS).save(f"{root}/assets/ibdm-256.png")
big.resize((512, 512), Image.LANCZOS).save(f"{root}/assets/ibdm-512.png")
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
big.save(f"{root}/assets/ibdm.ico", sizes=[(s, s) for s in sizes])
for n in (16, 32, 48, 128):
    big.resize((n, n), Image.LANCZOS).save(f"{root}/extension/icons/icon{n}.png")

def side_img(wd, ht):
    img = Image.new("RGB", (wd, ht), (246, 247, 250))
    d = ImageDraw.Draw(img)
    for y in range(ht):
        t = y / ht
        c = int(250 - 18 * t)
        d.line([(0, y), (wd, y)], fill=(c, c, min(255, c + 3)))
    logo = big.resize((int(wd * 0.7), int(wd * 0.7)), Image.LANCZOS)
    img.paste(logo, ((wd - logo.width) // 2, int(ht * 0.26)), logo)
    d.rectangle([0, ht - max(3, int(ht * 0.012)), wd, ht], fill=(220, 20, 30))
    return img
for scale, (wd, ht) in {100: (164, 314), 150: (246, 459), 200: (328, 628)}.items():
    side_img(wd, ht).save(f"{root}/installer/wizard-side-{scale}.bmp")
for scale, n in {100: 55, 150: 83, 200: 110}.items():
    img = Image.new("RGB", (n, n), (255, 255, 255))
    logo = big.resize((n, n), Image.LANCZOS)
    img.paste(logo, (0, 0), logo)
    img.save(f"{root}/installer/wizard-corner-{scale}.bmp")
print("ok", src.size, bbox)
