"""DBVO Studio splash: the logo's waveform bars, the name and "Launching studio…", in the app's colours."""
import math
from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageChops
S = 3; W, H = 640, 360
BG = (13, 17, 23); PANEL = (22, 27, 34); ACC = (88, 166, 255); DEEP = (31, 111, 235); TEXT = (230, 237, 243); DIM = (139, 148, 158)
img = Image.new('RGB', (W*S, H*S), BG)
# soft blue glow behind the logo
glow = Image.new('L', (W*S, H*S), 0); ImageDraw.Draw(glow).ellipse([220*S, 30*S, 420*S, 230*S], fill=110)
glow = glow.filter(ImageFilter.GaussianBlur(60*S))
img = Image.composite(Image.new('RGB', img.size, DEEP), img, glow.point(lambda v: int(v*.45)))
d = ImageDraw.Draw(img)
# the logo: a circle holding five rounded bars, like the top bar's mark
cx, cy, R = 320, 118, 62
d.ellipse([(cx-R)*S, (cy-R)*S, (cx+R)*S, (cy+R)*S], fill=PANEL, outline=(48, 54, 61), width=2*S)
xs = [-0.66, -0.33, 0, 0.33, 0.66]
for i, fx in enumerate(xs):
    x = cx + fx*R
    half = math.sqrt(max(0, R*R*0.86 - (fx*R)**2)) * (0.62 + 0.38*(1 - abs(fx)))
    w = 11
    t = i / 4
    col = tuple(int(ACC[k] + (DEEP[k]-ACC[k]) * abs(fx)) for k in range(3))
    d.rounded_rectangle([(x-w/2)*S, (cy-half)*S, (x+w/2)*S, (cy+half)*S], radius=int(w/2*S), fill=col)
# name and status
f1 = ImageFont.truetype('/usr/share/fonts/opentype/inter/Inter-SemiBold.otf', 34*S)
f2 = ImageFont.truetype('/usr/share/fonts/opentype/inter/Inter-Regular.otf', 15*S)
def ctext(t, f, y, col):
    w = d.textlength(t, font=f); d.text(((W*S - w)/2, y*S), t, font=f, fill=col)
ctext('DBVO Studio', f1, 200, TEXT)
ctext('Launching studio…', f2, 248, DIM)
# a waveform strip along the bottom, fading at both ends
n = 61; base = 318
for i in range(n):
    x = 120 + i * (400/(n-1))
    a = math.sin(i*0.55)*0.5 + math.sin(i*0.23+1)*0.35 + math.sin(i*1.7)*0.15
    h_ = 4 + (abs(a))*16
    fade = math.sin(math.pi * i/(n-1)) ** 0.8
    col = tuple(int(BG[k] + (ACC[k]-BG[k]) * fade) for k in range(3))
    d.rounded_rectangle([(x-1.6)*S, (base-h_)*S, (x+1.6)*S, (base+h_)*S], radius=int(1.6*S), fill=col)
img = img.resize((W, H), Image.LANCZOS).convert('RGBA')
m = Image.new('L', (W*4, H*4), 0); ImageDraw.Draw(m).rounded_rectangle([0, 0, W*4-1, H*4-1], radius=16*4, fill=255)
img.putalpha(m.resize((W, H), Image.LANCZOS))
# a 1 px border in the app's line colour
b = Image.new('RGBA', (W, H)); ImageDraw.Draw(b).rounded_rectangle([0, 0, W-1, H-1], radius=16, outline=(48, 54, 61, 255), width=1)
img.alpha_composite(b)
img.save('launch.png'); print('ok')
