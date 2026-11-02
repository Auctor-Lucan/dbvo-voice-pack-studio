"""Converts the guide's images (help/img/*.webp) to the JPEGs build.mjs puts in the PDF: python3 make_img.py <repo>"""
import sys, os
from PIL import Image
repo = sys.argv[1] if len(sys.argv) > 1 else '/home/claude/dbvo-voice-pack-studio'
src = os.path.join(repo, 'src/DbvoStudio.App/wwwroot/help/img')
os.makedirs('img', exist_ok=True)
for f in os.listdir(src):
    if f.lower().endswith(('.webp', '.png', '.jpg')):
        Image.open(os.path.join(src, f)).convert('RGB').save(os.path.join('img', os.path.splitext(f)[0] + '.jpg'), 'JPEG', quality=86)
print('done')
