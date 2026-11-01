from io import BytesIO
from pathlib import Path

from PIL import Image
from pypdf import PdfReader, PdfWriter


root = Path(__file__).resolve().parent
with Image.open(root / "sample-letter.png") as source:
    image = source.convert("RGB")
    image.save(root / "sample-letter.jpg", quality=98, subsampling=0, exif=b"")
    buffer = BytesIO()
    image.save(buffer, format="PDF", resolution=150)

buffer.seek(0)
reader = PdfReader(buffer)
writer = PdfWriter()
writer.add_page(reader.pages[0])
writer.add_metadata({"/Title": "Sample document", "/Producer": "FormPotok"})
with (root / "sample-letter.pdf").open("wb") as destination:
    writer.write(destination)
