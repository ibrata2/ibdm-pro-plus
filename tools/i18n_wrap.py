"""Wraps every Arabic string shown to people in L.T("…") (plain strings) or L.F("… {0} …", args) (interpolated
strings), and lists them in tools/strings.json for translation. Strings used as data or for matching are left alone."""
import re, sys, json, glob, os

AR = re.compile(r'[؀-ۿ]')
SKIP_FILES = {'Settings.cs', 'Snapshots.cs', 'Strings.cs', 'L.cs'}
SKIP_BEFORE = re.compile(r'(Contains\(|CategoryNamed\(|StartsWith\(|EndsWith\(|const string \w+ = |case |L\.T\(|L\.F\()\s*$')
keys = []

def read_string(s, i):
    """s[i] starts a string literal (", @", $", $@", @$", or \"\"\"). Returns (end, kind, prefix, body)."""
    j = i
    prefix = ''
    while s[j] in '$@':
        prefix += s[j]; j += 1
    if s.startswith('"""', j):
        k = s.index('"""', j + 3)
        while k + 3 < len(s) and s[k + 3] == '"': k += 1
        return k + 3, 'raw', prefix, s[j + 3:k]
    j += 1
    start = j
    verbatim = '@' in prefix
    interp = '$' in prefix
    depth = 0
    while True:
        c = s[j]
        if interp and depth > 0:
            if c == '"' or (c in '$@' and s[j:j + 2] in ('$"', '@"', '$@', '@$')):
                k, *_ = read_string(s, j); j = k; continue
            if c == "'":
                j = s.index("'", j + 2 if s[j + 1] == '\\' else j + 1) + 1; continue
            if c == '{': depth += 1
            elif c == '}': depth -= 1
            j += 1; continue
        if not verbatim and c == '\\': j += 2; continue
        if c == '"':
            if verbatim and s[j + 1:j + 2] == '"': j += 2; continue
            break
        if interp and c == '{':
            if s[j + 1] == '{': j += 2; continue
            depth = 1
        j += 1
    return j + 1, ('interp' if interp else 'plain'), prefix, s[start:j]

def split_holes(body):
    """Interpolated body → (template with {0}.., [expressions])."""
    out, exprs, i = '', [], 0
    while i < len(body):
        c = body[i]
        if c == '{' and body[i + 1:i + 2] == '{': out += '{{'; i += 2; continue
        if c == '}' and body[i + 1:i + 2] == '}': out += '}}'; i += 2; continue
        if c == '{':
            depth, j = 1, i + 1
            while depth:
                ch = body[j]
                if ch == '"' or (ch in '$@' and body[j:j + 2] in ('$"', '@"', '$@', '@$')):
                    k, *_ = read_string(body, j); j = k; continue
                if ch == "'":
                    j = body.index("'", j + 2 if body[j + 1] == '\\' else j + 1) + 1; continue
                if ch in '({[': depth += 1 if ch == '{' else 0
                if ch == '{': pass
                if ch == '}': depth -= 1
                j += 1
            hole = body[i + 1:j - 1]
            # split off ,alignment and :format at top level
            lvl, cut = 0, None
            k = 0
            while k < len(hole):
                ch = hole[k]
                if ch == '"' or (ch in '$@' and hole[k:k + 2] in ('$"', '@"', '$@', '@$')):
                    e, *_ = read_string(hole, k); k = e; continue
                if ch == "'":
                    k = hole.index("'", k + 2 if hole[k + 1] == '\\' else k + 1) + 1; continue
                if ch in '([{': lvl += 1
                elif ch in ')]}': lvl -= 1
                elif ch == '?' and lvl == 0: lvl_q = True
                elif ch in ':,' and lvl == 0 and cut is None:
                    # a ':' inside a ?: ternary is not a format: ternaries are wrapped in parentheses in this code
                    cut = k
                k += 1
            expr, spec = (hole[:cut], hole[cut:]) if cut is not None else (hole, '')
            out += '{' + str(len(exprs)) + spec + '}'
            exprs.append(transform(expr))
            i = j; continue
        out += c; i += 1
    return out, exprs

def transform(s, skip_ranges=()):
    res, i = [], 0
    while i < len(s):
        c = s[i]
        if s.startswith('//', i):
            j = s.find('\n', i); j = len(s) if j < 0 else j
            res.append(s[i:j]); i = j; continue
        if s.startswith('/*', i):
            j = s.index('*/', i) + 2; res.append(s[i:j]); i = j; continue
        if c == "'":
            j = s.index("'", i + 2 if s[i + 1] == '\\' else i + 1) + 1; res.append(s[i:j]); i = j; continue
        if c == '"' or (c in '$@' and s[i:i + 2] in ('$"', '@"', '$@', '@$')):
            end, kind, prefix, body = read_string(s, i)
            lit = s[i:end]
            before = ''.join(res)[-60:]
            inskip = any(a <= i < b for a, b in skip_ranges)
            if not AR.search(body) or kind == 'raw' or inskip or SKIP_BEFORE.search(before) or '@' in prefix:
                if kind == 'interp' and not inskip and '@' not in prefix:
                    tpl, exprs = split_holes(body)
                    if AR.search(tpl) and not SKIP_BEFORE.search(before):
                        keys.append(tpl)
                        res.append('L.F("' + tpl + '"' + ''.join(', ' + e for e in exprs) + ')')
                    else:
                        # still transform Arabic strings inside the holes
                        res.append(lit if not exprs else '$"' + rebuild(body) + '"')
                else:
                    res.append(lit)
                i = end; continue
            if kind == 'plain':
                keys.append(body)
                res.append('L.T(' + lit + ')')
            else:
                tpl, exprs = split_holes(body)
                keys.append(tpl)
                res.append('L.F("' + tpl + '"' + ''.join(', ' + e for e in exprs) + ')')
            i = end; continue
        res.append(c); i += 1
    return ''.join(res)

def rebuild(body):
    """Interpolated string without Arabic in its text: keep it, but translate Arabic strings inside its holes."""
    out, i = '', 0
    while i < len(body):
        c = body[i]
        if c in '{}' and body[i + 1:i + 2] == c: out += c * 2; i += 2; continue
        if c == '{':
            depth, j = 1, i + 1
            while depth:
                ch = body[j]
                if ch == '"' or (ch in '$@' and body[j:j + 2] in ('$"', '@"', '$@', '@$')):
                    k, *_ = read_string(body, j); j = k; continue
                if ch == '{': depth += 1
                elif ch == '}': depth -= 1
                j += 1
            out += '{' + transform(body[i + 1:j - 1]) + '}'
            i = j; continue
        out += c; i += 1
    return out

root = sys.argv[1]
for f in sorted(glob.glob(f'{root}/src/**/*.cs', recursive=True)):
    if '/obj/' in f or '/bin/' in f or os.path.basename(f) in SKIP_FILES: continue
    s = open(f, encoding='utf-8').read()
    skips = []
    for name in ('DevNames', 'LoadNames'):
        m = re.search(name + r'\s*=\s*\[', s)
        if m: skips.append((m.start(), s.index('];', m.start())))
    t = transform(s, skips)
    if t != s:
        open(f, 'w', encoding='utf-8').write(t)
        print('wrapped', f)
uniq = list(dict.fromkeys(keys))
json.dump(uniq, open(f'{root}/tools/strings-keys.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print(len(keys), 'strings,', len(uniq), 'unique')
