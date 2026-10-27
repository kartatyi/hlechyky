// «Колія» — генератор смуги від сіду. Чистий і детермінований (без Math.random/DOM/three).
// Власник: K1 «Світ». Нижче — заглушка зі скелета: рівна трава, трохи лісу й скель, колія на старті.
// Контракт: generate(seed, params) → { w, h, ground, obj, hp, drop, dropN, path, spawn }
//  w = params.stripLen (уздовж, x), h = params.stripWidth (упоперек, y); масиви Uint8Array(w*h)
//  path — idx клітинок початкової колії (≥ WAGONS.length + 2) від хвоста; станція — G.STATION біля x = w-1
//  на підлозі лежать сокира, кайло й відро (drop) біля старту; spawn — клітинки появи гравців (≥ 4)
import { G, O, I } from './rules.js';

function prng(seed) {
  let a = seed >>> 0;
  return () => {
    let t = (a = (a + 0x6D2B79F5) >>> 0);
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function generate(seed, params) {
  const w = params.stripLen | 0, h = params.stripWidth | 0, n = w * h;
  const r = prng(seed);
  const ground = new Uint8Array(n), obj = new Uint8Array(n), hp = new Uint8Array(n);
  const drop = new Uint8Array(n), dropN = new Uint8Array(n);
  const mid = h >> 1;
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const i = y * w + x;
    if (y === 0 || y === h - 1) { obj[i] = O.WALL; continue; }
    if (x < 8) continue;
    if (x >= w - 4) { if (Math.abs(y - mid) <= 1) ground[i] = G.STATION; continue; }
    const v = r();
    if (v < params.forest) { obj[i] = O.TREE; hp[i] = params.treeHits; }
    else if (v < params.forest + params.rocks) { obj[i] = O.ROCK; hp[i] = params.rockHits; }
    else if (v < params.forest + params.rocks + params.water) ground[i] = G.WATER;
  }
  const path = [];
  for (let x = 0; x < 6; x++) path.push(mid * w + x);
  const put = (x, y, k) => { drop[y * w + x] = k; dropN[y * w + x] = 1; };
  put(2, mid - 2, I.AXE); put(3, mid - 2, I.PICK); put(4, mid + 2, I.BUCKET);
  const spawn = [{ x: 2, y: mid - 1 }, { x: 3, y: mid + 1 }, { x: 1, y: mid - 1 }, { x: 1, y: mid + 1 }];
  return { w, h, ground, obj, hp, drop, dropN, path, spawn };
}
