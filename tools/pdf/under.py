from pypdf import PdfReader, PdfWriter, PageObject
from reportlab.pdfgen import canvas
import io
r = PdfReader('content.pdf')
w0, h0 = float(r.pages[0].mediabox.width), float(r.pages[0].mediabox.height)
buf = io.BytesIO(); c = canvas.Canvas(buf, pagesize=(w0, h0)); c.setFillColorRGB(0x1f/255, 0x24/255, 0x2c/255); c.rect(0, 0, w0, h0, stroke=0, fill=1); c.save()
bg = PdfReader(buf).pages[0]
out = PdfWriter()
for pg in r.pages:
    base = PageObject.create_blank_page(width=w0, height=h0)
    base.merge_page(bg); base.merge_page(pg)
    out.add_page(base)
out.add_metadata({'/Title': 'DBVO Studio 11.0.8 · User guide', '/Author': 'Auctor Lucan'})
with open('DBVO Studio Guide 11.0.8.pdf', 'wb') as f: out.write(f)
