"""Проєкція в метри гри: поперечний Меркатор WGS84 з осьовим меридіаном через центр міста, зсунутий так, що центр = (0, 0).
Осі як у грі: +X — схід, +Z — північ. На 10 км від центру масштаб відходить від 1 на ~1e-6, кути правдиві.
"""
import math

import numpy as np

A = 6378137.0
F = 1 / 298.257223563
E2 = F * (2 - F)
EP2 = E2 / (1 - E2)


def _meridian(phi):
    e4, e6 = E2 * E2, E2 * E2 * E2
    return A * ((1 - E2 / 4 - 3 * e4 / 64 - 5 * e6 / 256) * phi
                - (3 * E2 / 8 + 3 * e4 / 32 + 45 * e6 / 1024) * np.sin(2 * phi)
                + (15 * e4 / 256 + 45 * e6 / 1024) * np.sin(4 * phi)
                - (35 * e6 / 3072) * np.sin(6 * phi))


class Proj:
    def __init__(self, lat0, lon0):
        self.lat0, self.lon0 = lat0, lon0
        self.m0 = _meridian(math.radians(lat0))

    def fwd(self, lat, lon):
        """(lat, lon) градуси → (x, z) метри; приймає числа або numpy-масиви."""
        phi = np.radians(np.asarray(lat, 'f8'))
        lam = np.radians(np.asarray(lon, 'f8') - self.lon0)
        s, c = np.sin(phi), np.cos(phi)
        n = A / np.sqrt(1 - E2 * s * s)
        t = np.tan(phi) ** 2
        cc = EP2 * c * c
        a = lam * c
        x = n * (a + (1 - t + cc) * a ** 3 / 6 + (5 - 18 * t + t * t + 72 * cc - 58 * EP2) * a ** 5 / 120)
        z = _meridian(phi) - self.m0 + n * np.tan(phi) * (a * a / 2 + (5 - t + 9 * cc + 4 * cc * cc) * a ** 4 / 24
                                                         + (61 - 58 * t + t * t + 600 * cc - 330 * EP2) * a ** 6 / 720)
        return x, z

    def inv(self, x, z, iters=4):
        """Назад (для вибірки DEM у вузлах сітки): Ньютон на fwd, точність < 1 мм за 3–4 кроки."""
        x, z = np.asarray(x, 'f8'), np.asarray(z, 'f8')
        lat = self.lat0 + z / 111_200.0
        lon = self.lon0 + x / (111_320.0 * math.cos(math.radians(self.lat0)))
        for _ in range(iters):
            fx, fz = self.fwd(lat, lon)
            lat = lat + (z - fz) / 111_200.0
            lon = lon + (x - fx) / (111_320.0 * np.cos(np.radians(lat)))
        return lat, lon
