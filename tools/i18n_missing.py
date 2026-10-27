# Lists L.T("…") / L.F("…") texts in the code that have no English yet (not in tools/strings-keys.json).
import re, glob, json, sys
root = sys.argv[1]
keys = set(json.load(open(f'{root}/tools/strings-keys.json', encoding='utf-8')))
extra = {'فيديو', 'صوتيات', 'مضغوطات', 'برامج', 'مستندات', 'صور', 'تورنت', 'أخرى'}
found = []
for f in glob.glob(f'{root}/src/**/*.cs', recursive=True):
    if '/obj/' in f or f.endswith('Strings.cs'): continue
    for m in re.finditer(r'L\.[TF]\("((?:[^"\\]|\\.)*)"', open(f, encoding='utf-8').read()):
        k = m.group(1)
        if k not in keys and k not in extra and k not in found: found.append(k)
for k in found: print(k)
