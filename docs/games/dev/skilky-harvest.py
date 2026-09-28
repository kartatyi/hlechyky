"""Збирач кандидатів «Якого року?» з ручних запитів: harvest.py queries.txt out.json
Рядок queries.txt: `cat:Назва категорії|ua|N` або `q:пошук|world|N` або `file:File:Name.jpg|ua`.
Друкує стисло: номер|рік|ua|назва|опис. Рік — лише з DateTimeOriginal, де рівно один рік і без «circa/1930s/between»."""
import json, re, sys, time, urllib.parse, urllib.request, html, os

API = 'https://commons.wikimedia.org/w/api.php'
UA = 'HlechykyGames/1.0 (https://hlechyky.pp.ua) skilky-photos'
FREE = re.compile(r'^(public domain|pd\b|pd-|cc0|cc by(-sa)?( \d\.\d)?( [a-z]{2,3})?$)', re.I)
FUZZY = re.compile(r'circa|\bca\.?\b|\bc\.\s|approx|between|before|after|\bor\b|\?|\d0s\b|\bsome\b|unknown|~|\bto\b|–|/|decade', re.I)
BAD = re.compile(r'\bwar\b|wartime|killed|corpse|dead\b|death|funeral|execut|bomb|ruin|destroy|massacre|victim|propagand|'
                 r'swastika|hitler|nazi|blood|wound|nude|naked|holodomor|famine|gulag', re.I)


def api(**p):
    p.update(format='json', formatversion='2')
    req = urllib.request.Request(API + '?' + urllib.parse.urlencode(p), headers={'User-Agent': UA})
    for _ in range(3):
        try:
            with urllib.request.urlopen(req, timeout=40) as r:
                return json.load(r)
        except Exception:
            time.sleep(3)
    return {}


def clean(s):
    s = re.sub(r'<[^>]+>', ' ', s or '')
    return re.sub(r'\s+', ' ', html.unescape(s)).strip()


def year_of(date):
    if not date or FUZZY.search(date): return None
    ys = set(re.findall(r'\b(18[5-9]\d|19\d\d|20[0-2]\d)\b', date))
    return int(ys.pop()) if len(ys) == 1 else None


def pages(kind, arg, n):
    base = dict(action='query', prop='imageinfo', iiprop='url|size|extmetadata|mime', iiurlwidth='800')
    if kind == 'cat':
        d = api(generator='categorymembers', gcmtitle='Category:' + arg, gcmtype='file', gcmlimit='50', **base)
    elif kind == 'q':
        d = api(generator='search', gsrsearch=arg + ' filetype:bitmap', gsrnamespace='6', gsrlimit='40', **base)
    else:
        d = api(titles=arg, **base)
    return d.get('query', {}).get('pages', [])


def main(qpath, out):
    cand = json.load(open(out, encoding='utf-8')) if os.path.exists(out) else []
    seen = {c['file'] for c in cand}
    start = len(cand)
    for line in open(qpath, encoding='utf-8'):
        line = line.strip()
        if not line or line.startswith('#'): continue
        parts = line.split('|')
        kind, arg = parts[0].split(':', 1)
        ua = len(parts) > 1 and parts[1] == 'ua'
        n = int(parts[2]) if len(parts) > 2 else 5
        got = 0
        for pg in pages(kind, arg, n):
            if got >= n: break
            ii = (pg.get('imageinfo') or [None])[0]
            t = pg['title']
            if not ii or t in seen: continue
            md = ii.get('extmetadata', {})
            lic = clean(md.get('LicenseShortName', {}).get('value'))
            date = clean(md.get('DateTimeOriginal', {}).get('value'))
            desc = clean(md.get('ImageDescription', {}).get('value'))[:400]
            artist = clean(md.get('Artist', {}).get('value'))[:120] or 'невідомий автор'
            y = year_of(date)
            if ii.get('mime') != 'image/jpeg' or ii.get('width', 0) < 600 or not FREE.match(lic) or not y: continue
            if BAD.search(t + ' ' + desc): continue
            thumb = (ii.get('thumburl') or '').split('?')[0].replace('https://thumb.wikimedia.org/', 'https://upload.wikimedia.org/')
            if not thumb.startswith('https://upload.wikimedia.org/'): continue
            cand.append({'file': t, 'year': y, 'url': thumb, 'license': lic, 'author': artist, 'desc': desc, 'ua': ua,
                         'page': 'https://commons.wikimedia.org/wiki/' + urllib.parse.quote(t.replace(' ', '_'), safe=':/'),
                         'q': arg[:40]})
            seen.add(t); got += 1
        time.sleep(0.4)
    json.dump(cand, open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
    for i, c in enumerate(cand[start:], start):
        print(f"{i}|{c['year']}|{'U' if c['ua'] else 'W'}|{c['file'][5:55]}|{c['desc'][:85]}")


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
