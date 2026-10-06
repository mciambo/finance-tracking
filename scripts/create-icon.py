"""Build the app's geometric wallet icon with antialiased Windows ICO sizes."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
assets = root / "src" / "Finance.Desktop" / "Assets"
assets.mkdir(parents=True, exist_ok=True)
scale = 4
canvas = Image.new("RGBA", (256 * scale, 256 * scale), (0, 0, 0, 0))
draw = ImageDraw.Draw(canvas)

def rect(box, radius, fill):
    draw.rounded_rectangle(tuple(round(v * scale) for v in box), radius=radius * scale, fill=fill)

rect((8, 8, 248, 248), 52, "#182A3B")
rect((49, 66, 205, 195), 19, "#EAF4F2")
rect((49, 52, 189, 93), 15, "#65C4A5")
rect((49, 80, 205, 195), 16, "#EAF4F2")
rect((163, 106, 216, 150), 12, "#65C4A5")
draw.ellipse((178*scale, 120*scale, 191*scale, 133*scale), fill="#182A3B")
rect((70, 148, 84, 175), 4, "#182A3B")
rect((95, 131, 109, 175), 4, "#182A3B")
rect((120, 111, 134, 175), 4, "#182A3B")
icon = canvas.resize((256, 256), Image.Resampling.LANCZOS)
icon.save(assets / "Finance.png")
icon.save(assets / "Finance.ico", format="ICO", sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])
