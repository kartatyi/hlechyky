"""Імпорт міста з OpenStreetMap для «Чумаків-далекобійників» (fura).

    python osm_import.py fetch   zhytomyr     # сирі дані в кеш (OSM + DEM), лише раз
    python osm_import.py build   zhytomyr     # кеш → data/fura/maps/zhytomyr.osm.json.gz (проміжний формат)
    python osm_import.py preview zhytomyr     # проміжний → прев'ю PNG/SVG
    python osm_import.py kray    zhytomyr     # проміжний → data/fura/maps/zhytomyr.json (формат kray.json)
    python osm_import.py all     zhytomyr     # усе підряд

Див. README.md і FORMAT.md поруч.
"""
import argparse
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from cities import CITIES  # noqa: E402

REPO = os.path.normpath(os.path.join(HERE, '..', '..'))
CACHE = os.environ.get('FURA_OSM_CACHE', r'D:\or-wt\_fura\osm-cache')
PREVIEW = os.environ.get('FURA_OSM_PREVIEW', r'D:\or-wt\_fura\osm')
MAPS = os.path.join(REPO, 'data', 'fura', 'maps')


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    ap =argparse.ArgumentParser(description='OSM → мапа fura')
    ap.add_argument('step', choices=['fetch', 'build', 'preview', 'kray', 'all'])
    ap.add_argument('city', choices=sorted(CITIES))
    ap.add_argument('--force', action='store_true', help='перекачати сирі дані')
    ap.add_argument('--tol', type=float, default=1.5, help='допуск спрощення полілінії доріг, м')
    a = ap.parse_args()
    cfg = CITIES[a.city]
    steps = ['fetch', 'build', 'preview', 'kray'] if a.step == 'all' else [a.step]
    inter = os.path.join(MAPS, f'{a.city}.osm.json.gz')
    for st in steps:
        t0 = time.time()
        print(f'== {st}')
        if st == 'fetch':
            import fetch
            fetch.fetch_osm(a.city, cfg, CACHE, a.force)
            fetch.fetch_dem(cfg, CACHE, a.force)
        elif st == 'build':
            import build
            build.build(a.city, cfg, CACHE, inter, tol=a.tol)
        elif st == 'preview':
            import preview
            os.makedirs(PREVIEW, exist_ok=True)
            preview.render(inter, os.path.join(PREVIEW, a.city))
        elif st == 'kray':
            import kray
            out = os.path.join(MAPS, f'{a.city}.json')
            kray.export(inter, out, a.city)
            import preview
            os.makedirs(PREVIEW, exist_ok=True)
            preview.render_kray(inter, out, os.path.join(PREVIEW, f'{a.city}-kray.png'))
        print(f'   {time.time() - t0:.1f} с')


if __name__ == '__main__':
    main()
