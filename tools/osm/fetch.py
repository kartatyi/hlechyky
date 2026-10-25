"""Завантаження сирих даних у кеш поза репо: OSM (один запит Overpass) і DEM (тайли Copernicus GLO-30 з AWS).

Ввічливо: один запит на місто, таймаут, User-Agent без особистих даних; уже скачане не качаємо вдруге
(перекачати — прапорець --force).
"""
import os
import time
import urllib.error
import urllib.parse
import urllib.request

UA = 'hlechyky-fura-osm/0.1'
OVERPASS = 'https://overpass-api.de/api/interpreter'
DEM_URL = 'https://copernicus-dem-30m.s3.amazonaws.com/{n}/{n}.tif'


def overpass_query(bbox):
    s, w, n, e = bbox
    return f"""[out:json][timeout:300][maxsize:1073741824][bbox:{s},{w},{n},{e}];
(
  way[highway];
  node[highway~"^(traffic_signals|stop|give_way|motorway_junction|mini_roundabout|crossing)$"];
  way[building]; relation[building];
  way[natural~"^(water|wood|scrub|wetland|grassland|heath)$"]; relation[natural~"^(water|wood|wetland)$"];
  way[waterway]; relation[waterway];
  way[landuse]; relation[landuse];
  way[leisure~"^(park|garden|pitch|stadium|playground)$"]; relation[leisure~"^(park|garden)$"];
  way[railway]; node[railway~"^(station|halt)$"]; way[public_transport=station];
  way[amenity~"^(marketplace|parking|fuel|bus_station)$"]; node[amenity~"^(marketplace|fuel|bus_station)$"];
  way[shop~"^(wholesale|mall|supermarket|doityourself|trade)$"];
  node[place];
);
out geom qt;"""


def _get(url, path, data=None, timeout=600):
    tmp = path + '.part'
    req = urllib.request.Request(url, data=data, headers={'User-Agent': UA})
    t0 = time.time()
    with urllib.request.urlopen(req, timeout=timeout) as r, open(tmp, 'wb') as f:
        while chunk := r.read(1 << 20):
            f.write(chunk)
    os.replace(tmp, path)
    print(f'  {os.path.basename(path)}: {os.path.getsize(path) / 1e6:.1f} МБ за {time.time() - t0:.0f} с')


def fetch_osm(city, cfg, cache, force=False):
    path = os.path.join(cache, city, 'overpass.json')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    if os.path.exists(path) and not force:
        print(f'  OSM уже є: {path}')
        return path
    print('  Overpass: один запит на bbox', cfg['bbox'])
    body = urllib.parse.urlencode({'data': overpass_query(cfg['bbox'])}).encode()
    for attempt in range(3):   # 429/502/504 — сервер зайнятий: чекаємо й пробуємо ще, не частіше раз на 1–2 хв
        try:
            _get(OVERPASS, path, data=body)
            return path
        except urllib.error.HTTPError as e:
            if e.code not in (429, 502, 503, 504) or attempt == 2:
                raise
            print(f'  Overpass {e.code}, чекаю {60 * (attempt + 1)} с')
            time.sleep(60 * (attempt + 1))
    return path


def dem_name(lat, lon):
    ns = 'N' if lat >= 0 else 'S'
    ew = 'E' if lon >= 0 else 'W'
    return f'Copernicus_DSM_COG_10_{ns}{abs(lat):02d}_00_{ew}{abs(lon):03d}_00_DEM'


def fetch_dem(cfg, cache, force=False):
    out = []
    os.makedirs(os.path.join(cache, 'dem'), exist_ok=True)
    for lat, lon in cfg['dem_tiles']:
        n = dem_name(lat, lon)
        path = os.path.join(cache, 'dem', n + '.tif')
        if not os.path.exists(path) or force:
            print('  DEM:', n)
            _get(DEM_URL.format(n=n), path)
        else:
            print(f'  DEM уже є: {path}')
        out.append(path)
    return out
