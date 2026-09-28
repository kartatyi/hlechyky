"""Добирач фото для «📷 Якого року?» («Скільки?»): кандидати з категорій Вікісховища «<рік> in <місто>».

    python skilky-photos.py scan --out cand.json [--per 6] [--cities Kyiv,Lviv] [--years 1900-2010:5]
    python skilky-photos.py list cand.json            # коротко: номер | рік | місто | назва | опис
    python skilky-photos.py add cand.json 3,7,12 --captions caps.json   # дописати обрані в data/skilky/photos.json

Беремо лише JPEG із вільною ліцензією (Public domain / PD-* / CC0 / CC BY / CC BY-SA), де дата зйомки
(DateTimeOriginal) містить рік категорії, ширина ≥ 800; відсіваємо війну, смерть, агітацію за словами в назві й описі.
Адреса — мініатюра 1024 px (upload.wikimedia.org), сервер докачує її в cache/skilky сам (SkilkyPhotos).
captions.json — {"<номер>": "Підпис після відповіді", ...}; решту полів (автор, ліцензія, сторінка) скрипт бере з API.
"""
import json, re, sys, time, urllib.parse, urllib.request, os, html

API = 'https://commons.wikimedia.org/w/api.php'
UA = 'HlechykyGames/1.0 (https://hlechyky.pp.ua) skilky-photos'
BAD = re.compile(r'war\b|wartime|killed|corpse|dead|death|funeral|execut|bomb|ruin|destroy|soldier|army|militar|tank\b|nazi|'
                 r'wehrmacht|holodomor|famine|genocide|massacre|victim|protest|demonstrat|rally|parade|lenin|stalin|propagand|poster|'
                 r'prison|gulag|occupation|invasion|refugee|wound|hospital|grave|cemetery|memorial|burn|fire\b|flood|disaster|crash|'
                 r'nude|naked|weapon|gun|rifle|police|arrest|riot|blood|swastika|hitler|kgb|nkvd|ss\b|luftwaffe|artillery', re.I)
FREE = re.compile(r'^(public domain|pd\b|pd-|cc0|cc by(-sa)?( \d\.\d)?( [a-z]{2,3})?$)', re.I)
UA_CITIES = ['Kyiv', 'Lviv', 'Odesa', 'Kharkiv', 'Chernivtsi', 'Dnipro']
WORLD = ['Paris', 'London', 'New York City', 'Berlin', 'Tokyo', 'Rome', 'Warsaw', 'Prague', 'Amsterdam', 'Stockholm']


def api(**p):
    p.update(format='json', formatversion='2')
    req = urllib.request.Request(API + '?' + urllib.parse.urlencode(p), headers={'User-Agent': UA})
    for _ in range(3):
        try:
            with urllib.request.urlopen(req, timeout=40) as r:
                return json.load(r)
        except Exception:
            time.sleep(2)
    return {}


def clean(s):
    s = re.sub(r'<[^>]+>', ' ', s or '')
    return re.sub(r'\s+', ' ', html.unescape(s)).strip()


def files_in(cat, n):
    d = api(action='query', list='categorymembers', cmtitle='Category:' + cat, cmtype='file', cmlimit=str(min(50, n * 5)))
    return [m['title'] for m in d.get('query', {}).get('categorymembers', []) if m['title'].lower().endswith(('.jpg', '.jpeg'))]


def infos(titles):
    out = []
    for i in range(0, len(titles), 20):
        d = api(action='query', prop='imageinfo', titles='|'.join(titles[i:i + 20]), iiprop='url|size|extmetadata|mime',
                iiurlwidth='1024')
        for pg in d.get('query', {}).get('pages', []):
            ii = (pg.get('imageinfo') or [None])[0]
            if ii: out.append((pg['title'], ii))
        time.sleep(0.5)
    return out


def scan(args):
    per = int(args.get('--per', 6))
    cities = args.get('--cities', ','.join(UA_CITIES + WORLD)).split(',')
    a, b, step = 1900, 2010, 5
    if '--years' in args:
        m = re.match(r'(\d+)-(\d+):(\d+)', args['--years']); a, b, step = map(int, m.groups())
    cand = json.load(open(args['--out'], encoding='utf-8')) if os.path.exists(args['--out']) else []
    seen = {c['file'] for c in cand}
    for city in cities:
        for year in range(a, b + 1, step):
            titles = [t for t in files_in(f'{year} in {city}', per) if t not in seen]
            got = 0
            for title, ii in infos(titles[:per * 3]):
                if got >= per: break
                md = ii.get('extmetadata', {})
                lic = clean(md.get('LicenseShortName', {}).get('value'))
                date = clean(md.get('DateTimeOriginal', {}).get('value'))
                desc = clean(md.get('ImageDescription', {}).get('value'))[:300]
                artist = clean(md.get('Artist', {}).get('value'))[:120] or 'невідомий автор'
                if ii.get('mime') != 'image/jpeg' or ii.get('width', 0) < 800 or not FREE.match(lic): continue
                if str(year) not in date or re.search(r'\b(1[89]|20)\d\d\b', date.replace(str(year), '')): continue
                if BAD.search(title + ' ' + desc): continue
                thumb = ii.get('thumburl') or ''
                if not thumb.startswith('https://upload.wikimedia.org/'): continue
                cand.append({'file': title, 'year': year, 'city': city, 'url': thumb.split('?')[0], 'license': lic, 'author': artist,
                             'page': 'https://commons.wikimedia.org/wiki/' + urllib.parse.quote(title.replace(' ', '_'), safe=':/'), 'desc': desc,
                             'ua': city in UA_CITIES})
                seen.add(title); got += 1
            print(city, year, got, file=sys.stderr)
            json.dump(cand, open(args['--out'], 'w', encoding='utf-8'), ensure_ascii=False, indent=0)


def listing(path):
    for i, c in enumerate(json.load(open(path, encoding='utf-8'))):
        print(f"{i}|{c['year']}|{c['city']}|{c['file'][5:60]}|{c['desc'][:90]}")


def add(path, picks, caps_path):
    cand = json.load(open(path, encoding='utf-8'))
    caps = json.load(open(caps_path, encoding='utf-8'))
    root = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..'))
    man_path = os.path.join(root, 'data', 'skilky', 'photos.json')
    man = json.load(open(man_path, encoding='utf-8')) if os.path.exists(man_path) else {
        'note': 'Фото «📷 Якого року?» (Скільки?): лише вільні ліцензії з Вікісховища; файли качає сервер у cache/skilky. '
                'Добирач: docs/games/dev/skilky-photos.py', 'photos': []}
    have = {p['file'] for p in man['photos']}
    for i in picks:
        c = cand[i]
        if c['file'] in have: continue
        slug = re.sub(r'[^a-z0-9]+', '-', c['city'].lower()).strip('-')[:14]
        pid = f"{slug}-{c['year']}-{len(man['photos']):03d}"
        man['photos'].append({'id': pid, 'file': c['file'], 'url': c['url'], 'year': c['year'], 'caption': caps[str(i)],
                              'author': c['author'], 'license': c['license'], 'page': c['page'], 'ua': c['ua']})
    os.makedirs(os.path.dirname(man_path), exist_ok=True)
    json.dump(man, open(man_path, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print(len(man['photos']), 'у маніфесті')


if __name__ == '__main__':
    cmd = sys.argv[1]
    rest = sys.argv[2:]
    opts = {rest[i]: rest[i + 1] for i in range(len(rest) - 1) if rest[i].startswith('--')}
    if cmd == 'scan': scan(opts)
    elif cmd == 'list': listing(rest[0])
    elif cmd == 'add': add(rest[0], [int(x) for x in rest[1].split(',')], opts['--captions'])
