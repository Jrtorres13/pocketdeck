# Builds app/index.html from dev/index.src.html: inlines every Lucide icon (+ search tags),
# the QR library, the keep-awake video and the two fonts, so the deck works with no internet.
# Usage: python3 dev/build.py <node_modules with lucide-static and qrcode-generator>
import json, re, base64, os, sys, glob
here = os.path.dirname(os.path.abspath(__file__))
root = os.path.dirname(here)
npm = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, 'node_modules')
src = open(os.path.join(here, 'index.src.html'), encoding='utf-8').read()
lucide = os.path.join(npm, 'lucide-static')
icons = {}
for f in sorted(glob.glob(os.path.join(lucide, 'icons', '*.svg'))):
    s = open(f, encoding='utf-8').read()
    icons[os.path.basename(f)[:-4]] = re.sub(r'\s+', ' ', re.search(r'<svg[^>]*>(.*)</svg>', s, re.S).group(1)).strip()
tags_all = json.load(open(os.path.join(lucide, 'tags.json')))
tags = {n: ' '.join(t[:6]).lower() for n, t in tags_all.items() if n in icons and t}
qr = open(os.path.join(npm, 'qrcode-generator', 'qrcode.js'), encoding='utf-8').read()
qr = re.sub(r'(?m)^\s*//.*$', '', qr).replace("img += '<img';", "img += '<' + 'img';").replace("'#000000' : '#ffffff'", "'currentColor' : 'transparent'")
b64 = lambda p, mime: 'data:%s;base64,%s' % (mime, base64.b64encode(open(os.path.join(here, p), 'rb').read()).decode())
out = src.replace('/*@ICONS@*/{}', json.dumps(icons, separators=(',', ':')))
out = out.replace('/*@TAGS@*/{}', json.dumps(tags, separators=(',', ':')))
out = out.replace('/*@QR@*/', qr)
out = out.replace('/*@VIDEO@*/', b64('tiny.mp4', 'video/mp4'))
out = out.replace('url(/*@GLOOCK@*/)', 'url(' + b64('gloock.woff2', 'font/woff2') + ')')
out = out.replace('url(/*@GEIST@*/)', 'url(' + b64('geist.woff2', 'font/woff2') + ')')
assert '/*@' not in out
open(os.path.join(root, 'app', 'index.html'), 'w', encoding='utf-8').write(out)
print('built', len(out), 'bytes,', len(icons), 'icons')
