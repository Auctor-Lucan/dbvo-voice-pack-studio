"""DBVO Studio splash: a horned warrior bust drawn only with vertical bars, like the logo's waveform."""
import math, sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageChops
S = 2                      # supersampling
W, H = 900, 520
def P(x, y): return (x * S, y * S)

def bez(p0, p1, p2, p3, n=40):
    return [((1-t)**3*p0[0]+3*(1-t)**2*t*p1[0]+3*(1-t)*t**2*p2[0]+t**3*p3[0], (1-t)**3*p0[1]+3*(1-t)**2*t*p1[1]+3*(1-t)*t**2*p2[1]+t**3*p3[1]) for t in [i/n for i in range(n+1)]]

def silhouette(cx=450):
    m = Image.new('L', (W*S, H*S), 0); d = ImageDraw.Draw(m)
    sc = lambda pts: [P(x, y) for x, y in pts]
    # shoulders: sloping from the collar, rounded at the ends, cut off by the bottom edge
    for sgn in (-1, 1):
        pass
    left = bez((cx, 300), (cx-120, 300), (cx-200, 318), (cx-245, 350)) + bez((cx-245, 350), (cx-285, 372), (cx-300, 420), (cx-305, 470))
    right = [(2*cx - x, y) for x, y in left]
    d.polygon(sc(left + [(cx-310, 540), (cx+310, 540)] + right[::-1]), fill=255)
    # fur collar: a ragged ring of tufts around the neck and shoulders
    for i, dx in enumerate(range(-200, 201, 25)):
        y = 300 + (abs(dx) / 200) ** 1.6 * 48
        r = 15 + (i % 2) * 4
        d.ellipse([P(cx + dx - r, y - r), P(cx + dx + r, y + r)], fill=255)
    # face under the helmet
    d.ellipse([P(cx-60, 200), P(cx+60, 320)], fill=255)
    # long beard, wider at the jaw, to a point on the chest
    d.polygon(sc(bez((cx-66, 262), (cx-80, 330), (cx-52, 390), (cx, 432)) + bez((cx, 432), (cx+52, 390), (cx+80, 330), (cx+66, 262))), fill=255)
    # helmet: dome and rim
    d.ellipse([P(cx-80, 104), P(cx+80, 268)], fill=255)
    d.rounded_rectangle([P(cx-90, 214), P(cx+90, 240)], radius=6*S, fill=255)
    # horns: thick at the helmet, sweeping out and curling up to a point
    for sgn in (-1, 1):
        # a centre line with discs that taper from the helmet to a point
        line = bez((cx + sgn*72, 178), (cx + sgn*168, 158), (cx + sgn*232, 118), (cx + sgn*212, 30), n=160)
        for i, (x, y) in enumerate(line):
            t = i / (len(line) - 1)
            r = 24 * (1 - t) ** 0.85 + 3
            d.ellipse([P(x - r, y - r), P(x + r, y + r)], fill=255)
    return m

def carve(m, cx=450):
    """Horizontal cuts show up as breaks in the bars: helmet rim, eye slits, nose guard, horn bands, beard."""
    d = ImageDraw.Draw(m)
    g = 0
    d.rectangle([P(cx-92, 210), P(cx+92, 214)], fill=g)            # top of the rim
    d.rectangle([P(cx-92, 240), P(cx+92, 244)], fill=g)            # bottom of the rim
    for sgn in (-1, 1):                                             # eye holes of the spectacle guard
        x0, x1 = (cx - 52, cx - 12) if sgn < 0 else (cx + 12, cx + 52)
        d.rounded_rectangle([P(x0, 254), P(x1, 266)], radius=5*S, fill=g)
    d.rectangle([P(cx-9, 244), P(cx-5, 300)], fill=g)               # nose guard edges
    d.rectangle([P(cx+5, 244), P(cx+9, 300)], fill=g)
    d.rectangle([P(cx-30, 300), P(cx+30, 304)], fill=g)            # moustache line
    for y in (340, 375):                                            # beard braids
        d.rectangle([P(cx-60, y), P(cx+60, y+3)], fill=g)
    # the beard's outline, so it reads against the chest
    beard = bez((cx-66, 262), (cx-80, 330), (cx-52, 390), (cx, 432)) + bez((cx, 432), (cx+52, 390), (cx+80, 330), (cx+66, 262))
    d.line([P(x, y) for x, y in beard[12:-12]], fill=g, width=5*S, joint='curve')
    return m

def bars(mask, step=7, width=4, top=(160, 214, 255), bottom=(31, 111, 235), fade_from=None):
    """Fill the mask with vertical rounded bars, one per column, broken where the mask is."""
    out = Image.new('RGBA', mask.size, (0, 0, 0, 0)); d = ImageDraw.Draw(out)
    px = mask.load(); w, h = mask.size
    for x0 in range(0, w // S, step):
        xc = int((x0 + step / 2) * S)
        y = 0; runs = []
        while y < h:
            if px[xc, y] > 127:
                s = y
                while y < h and px[xc, y] > 127: y += 1
                runs.append((s, y))
            y += 1
        for s, e in runs:
            if e - s < 4 * S: continue
            for yy in range(s, e, 2):
                t = yy / h
                c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
                a = 255
                if fade_from and yy > fade_from * S: a = max(0, int(255 * (1 - (yy - fade_from * S) / ((H - fade_from) * S))))
                d.line([(xc - width * S // 2, yy), (xc + width * S // 2, yy)], fill=c + (a,), width=2)
            r = width * S // 2
            ct = tuple(int(top[i] + (bottom[i] - top[i]) * (s / h)) for i in range(3))
            d.ellipse([xc - r, s - r, xc + r, s + r], fill=ct + (255,))
    return out

def make(text=True, sub='Starting…'):
    m = carve(silhouette())
    art = bars(m, fade_from=400)
    bg = Image.new('RGBA', (W*S, H*S), (13, 17, 23, 255))
    # soft blue glow behind the bust
    glow = Image.new('L', (W*S, H*S), 0); ImageDraw.Draw(glow).ellipse([P(250, 120), P(650, 560)], fill=70)
    glow = glow.filter(ImageFilter.GaussianBlur(70 * S))
    bg = Image.composite(Image.new('RGBA', bg.size, (31, 111, 235, 255)), bg, glow.point(lambda v: int(v * 0.55)))
    img = Image.alpha_composite(bg, art)
    if text:
        # dark band behind the title so it reads over the chest
        band = Image.new('L', (W*S, H*S), 0); ImageDraw.Draw(band).rectangle([P(0, 400), P(W, H)], fill=230)
        band = band.filter(ImageFilter.GaussianBlur(26 * S))
        img = Image.composite(Image.new('RGBA', img.size, (13, 17, 23, 255)), img, band)
        d = ImageDraw.Draw(img)
        f1 = ImageFont.truetype('/usr/share/fonts/opentype/inter/Inter-SemiBold.otf', 46 * S)
        f2 = ImageFont.truetype('/usr/share/fonts/opentype/inter/Inter-Regular.otf', 15 * S)
        t = 'DBVO Studio'
        tw = d.textlength(t, font=f1)
        d.text(((W * S - tw) / 2, 418 * S), t, font=f1, fill=(230, 237, 243, 255))
        sw = d.textlength(sub, font=f2)
        d.text(((W * S - sw) / 2, 478 * S), sub, font=f2, fill=(139, 148, 158, 255))
    img = img.resize((W, H), Image.LANCZOS)
    # rounded corners
    mask = Image.new('L', (W, H), 0); ImageDraw.Draw(mask).rounded_rectangle([0, 0, W - 1, H - 1], radius=18, fill=255)
    img.putalpha(ImageChops.multiply(img.getchannel('A'), mask))
    return img

def art_only():
    """The bust alone on a transparent background, for the in-app loading overlay."""
    m = carve(silhouette())
    a = bars(m, fade_from=380).resize((W, H), Image.LANCZOS)
    return a

if __name__ == '__main__':
    make().save('splash.png')
    art_only().save('warrior.png')
    print('ok')
