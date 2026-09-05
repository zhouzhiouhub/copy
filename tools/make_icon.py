from PIL import Image
from pathlib import Path
import struct

src = Path(r"D:\Desktop\dev\logo\_cmp\k-symbol.png")
out_dir = Path(r"d:\Desktop\dev\Copy\assets")
out_dir.mkdir(parents=True, exist_ok=True)

img = Image.open(src).convert("RGBA")
w, h = img.size
side = min(w, h)
left = (w - side) // 2
top = (h - side) // 2
img = img.crop((left, top, left + side, top + side))

sizes = [16, 24, 32, 48, 64, 128, 256]
images = [img.resize((s, s), Image.Resampling.LANCZOS) for s in sizes]


def png_bytes(im):
    from io import BytesIO
    buf = BytesIO()
    im.save(buf, format="PNG")
    return buf.getvalue()


pngs = [png_bytes(im) for im in images]
count = len(pngs)
offset = 6 + 16 * count
entries = []
payload = b""
for im, data in zip(images, pngs):
    w = 0 if im.width >= 256 else im.width
    h = 0 if im.height >= 256 else im.height
    entries.append(struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(data), offset))
    payload += data
    offset += len(data)

header = struct.pack("<HHH", 0, 1, count)
ico_path = out_dir / "app.ico"
ico_path.write_bytes(header + b"".join(entries) + payload)
images[1].save(out_dir / "tray.png")
images[3].save(out_dir / "logo64.png")
print("ico_bytes", ico_path.stat().st_size)
