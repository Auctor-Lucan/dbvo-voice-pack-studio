# Guide PDF

```sh
mkdir -p /tmp/dbvo-work/pdf && cp <repo>/tools/pdf/* /tmp/dbvo-work/pdf/ && cd /tmp/dbvo-work/pdf
python3 make_img.py <repo>      # help/img/*.webp → img/*.jpg (splash included)
W=<repo>/src/DbvoStudio.App/wwwroot node build.mjs     # content.pdf (Playwright Chromium)
python3 under.py                # dark page background + metadata → "DBVO Studio Guide <version>.pdf"
```

Before building, set the version in three places: build.mjs (cover "version X" and the footer) and under.py
(title and file name). Check a page with `pdftoppm -f N -l N -r 60 -jpeg "<pdf>" chk`.
