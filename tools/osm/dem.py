"""Висоти з DEM: найменший читач GeoTIFF (класичний TIFF, тайли або смуги, DEFLATE або без стиску, предиктори 1/2/3)
на numpy, без GDAL. Вистачає для Copernicus GLO-30 COG (float32, DEFLATE, предиктор 3).
"""
import struct
import zlib

import numpy as np

TYPES = {1: ('B', 1), 2: ('c', 1), 3: ('H', 2), 4: ('I', 4), 5: ('II', 8), 6: ('b', 1), 7: ('B', 1),
         8: ('h', 2), 9: ('i', 4), 10: ('ii', 8), 11: ('f', 4), 12: ('d', 8), 16: ('Q', 8)}


def _ifd(buf, off, bo):
    n = struct.unpack_from(bo + 'H', buf, off)[0]
    tags = {}
    for i in range(n):
        tag, typ, cnt, val = struct.unpack_from(bo + 'HHI4s', buf, off + 2 + i * 12)
        fmt, size = TYPES[typ]
        total = size * cnt
        data = val if total <= 4 else buf[struct.unpack(bo + 'I', val)[0]:][:total]
        if typ == 2:
            tags[tag] = data[:cnt].rstrip(b'\0').decode('latin1')
        elif typ in (5, 10):
            v = struct.unpack(bo + fmt[0] * (2 * cnt), data[:total])
            tags[tag] = [v[i] / v[i + 1] for i in range(0, len(v), 2)]
        else:
            tags[tag] = list(struct.unpack(bo + fmt * cnt, data[:total]))
    return tags


class Dem:
    """Один тайл DEM у пам'яті: висоти float32 [рядок, стовпчик], рядок 0 — північ."""

    def __init__(self, path):
        with open(path, 'rb') as f:
            buf = f.read()
        bo = {b'II': '<', b'MM': '>'}[buf[:2]]
        if struct.unpack_from(bo + 'H', buf, 2)[0] != 42:
            raise ValueError('BigTIFF не підтримано')
        t = _ifd(buf, struct.unpack_from(bo + 'I', buf, 4)[0], bo)   # перший IFD — повна роздільність
        w, h = t[256][0], t[257][0]
        comp = t.get(259, [1])[0]
        pred = t.get(317, [1])[0]
        bits = t[258][0]
        sfmt = t.get(339, [1])[0]
        if t.get(277, [1])[0] != 1:
            raise ValueError('очікую один канал')
        dt = np.dtype({(3, 32): 'f4', (3, 64): 'f8', (1, 16): 'u2', (2, 16): 'i2', (2, 32): 'i4'}[(sfmt, bits)]).newbyteorder(bo)
        if 322 in t:
            tw, th, offs, cnts = t[322][0], t[323][0], t[324], t[325]
        else:
            tw, th, offs, cnts = w, t.get(278, [h])[0], t[273], t[279]
        out = np.zeros((h, w), dtype='f4')
        across = (w + tw - 1) // tw
        for k, (o, c) in enumerate(zip(offs, cnts)):
            raw = buf[o:o + c]
            if comp == 8 or comp == 32946:
                raw = zlib.decompress(raw)
            elif comp != 1:
                raise ValueError(f'стиск {comp} не підтримано')
            rows = len(raw) // (tw * dt.itemsize)
            if pred == 3:
                b = np.frombuffer(raw, 'u1').reshape(rows, dt.itemsize * tw)
                b = np.cumsum(b, axis=1, dtype='u1')
                b = b.reshape(rows, dt.itemsize, tw).transpose(0, 2, 1)   # байтові площини → числа (старший байт перший)
                a = np.ascontiguousarray(b).view(dt.newbyteorder('>')).reshape(rows, tw)
            else:
                a = np.frombuffer(raw, dt).reshape(rows, tw)
                if pred == 2:
                    a = np.cumsum(a, axis=1, dtype=a.dtype)
            r0, c0 = (k // across) * th, (k % across) * tw
            rr, cc = min(rows, h - r0), min(tw, w - c0)
            if rr > 0 and cc > 0:
                out[r0:r0 + rr, c0:c0 + cc] = a[:rr, :cc]
        sx, sy = t[33550][0], t[33550][1]
        _, _, _, lon0, lat0, _ = t[33922][:6]
        # GTRasterTypeGeoKey (1025): 1 — тайпоінт на куті пікселя, 2 — у центрі
        geo = t.get(34735, [])
        point = any(geo[i] == 1025 and geo[i + 3] == 2 for i in range(4, len(geo), 4))
        self.h = out
        self.lon0 = lon0 + (0 if point else sx / 2)   # центр пікселя [0, 0]
        self.lat0 = lat0 - (0 if point else sy / 2)
        self.sx, self.sy = sx, sy
        self.w, self.hgt = w, h

    def covers(self, lat, lon):
        c = (lon - self.lon0) / self.sx
        r = (self.lat0 - lat) / self.sy
        return -0.5 <= c <= self.w - 0.5 and -0.5 <= r <= self.hgt - 0.5

    def sample(self, lat, lon):
        """Білінійно, numpy-масиви або числа."""
        c = np.clip((np.asarray(lon) - self.lon0) / self.sx, 0, self.w - 1.000001)
        r = np.clip((self.lat0 - np.asarray(lat)) / self.sy, 0, self.hgt - 1.000001)
        c0, r0 = np.floor(c).astype(int), np.floor(r).astype(int)
        u, v = c - c0, r - r0
        H = self.h
        a, b = H[r0, c0], H[r0, c0 + 1]
        cc, d = H[r0 + 1, c0], H[r0 + 1, c0 + 1]
        return a * (1 - u) * (1 - v) + b * u * (1 - v) + cc * (1 - u) * v + d * u * v


class DemSet:
    def __init__(self, paths):
        self.tiles = [Dem(p) for p in paths]

    def sample(self, lat, lon):
        lat, lon = np.atleast_1d(np.asarray(lat, 'f8')), np.atleast_1d(np.asarray(lon, 'f8'))
        out = np.full(lat.shape, np.nan)
        for t in self.tiles:
            m = np.isnan(out) & (lon >= t.lon0 - t.sx / 2) & (lon <= t.lon0 + t.sx * (t.w - 0.5)) \
                & (lat <= t.lat0 + t.sy / 2) & (lat >= t.lat0 - t.sy * (t.hgt - 0.5))
            if m.any():
                out[m] = t.sample(lat[m], lon[m])
        if np.isnan(out).any():
            raise ValueError('точка поза тайлами DEM — додай тайл у cities.py')
        return out
