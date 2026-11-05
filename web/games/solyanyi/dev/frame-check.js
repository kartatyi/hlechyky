// «Соляний шлях» — перевірка кадру (§11.2): усі міста й шляхи в полі, підписи в полі, нічого не лежить під HUD.
// Стенд: dev/scene.html?frame — результат у window.__frame. Жива гра: import() зі сторінки столу, sc = document.querySelector('.sol').parentElement._sl.sc.
import { CITIES, ROUTES } from '../consts.js';

const inRect = (p, r, pad = 0) => p.x + pad > r.x0 && p.x - pad < r.x1 && p.y + pad > r.y0 && p.y - pad < r.y1;
const cross = (a, b) => a.x0 < b.x1 && a.x1 > b.x0 && a.y0 < b.y1 && a.y1 > b.y0;

/** HUD картки поверх поля, у px від кута поля: те саме, що обходить кадр (solyanyi.js hudRects). */
export function hudOf(sol) {
  const f = sol.querySelector('.sol-field').getBoundingClientRect(), out = {};
  const add = (name, el) => {
    if (!el) return;
    const b = el.getBoundingClientRect();
    if (!b.width || !b.height) return;
    const r = { x0: Math.max(0, b.left - f.left), y0: Math.max(0, b.top - f.top), x1: Math.min(f.width, b.right - f.left), y1: Math.min(f.height, b.bottom - f.top) };
    if (r.x1 > r.x0 && r.y1 > r.y0) out[name] = r;
  };
  for (const k of ['title', 'tools', 'zoom', 'hand']) add(k, sol.querySelector('.sol-' + k));
  if (sol.classList.contains('sol-t-petryk')) add('banner', sol.querySelector('.sol-banner'));
  if (sol.classList.contains('sol-t-board')) [...sol.querySelector('.sol-deco').children].forEach((el, i) => add('deco' + i, el));
  return out;
}

/** Телефон (кадр — ділянка замовлень, не вся мапа): видимі назви цілком у полі й не наїжджають одна на одну; скільки міст видно. */
export function cropCheck(sc, w, h) {
  const L = (sc.labelBoxes ? sc.labelBoxes() : []).filter((x) => x.x1 > 0 && x.y1 > 0 && x.x0 < w && x.y0 < h);
  const out = { ok: true, size: [w, h], labels: L.length, cut: [], labelHits: [], cities: 0 };
  for (const c of CITIES) { const p = sc.screenPos('city', c[0]); if (p && p.x >= 0 && p.y >= 0 && p.x <= w && p.y <= h) out.cities++; }
  for (const x of L) if (x.x0 < 0 || x.y0 < 0 || x.x1 > w || x.y1 > h) out.cut.push(x.name);
  for (let i = 0; i < L.length; i++) for (let j = i + 1; j < L.length; j++) if (cross(L[i], L[j])) out.labelHits.push(L[i].name + '×' + L[j].name);
  out.ok = !out.cut.length && !out.labelHits.length && out.labels > 0;
  return out;
}

/** Міряє сцену (3D чи 2D): w × h — розмір поля, hud — { назва: рамка }. ok — усе видно й нічого не закрито. */
export function frameCheck(sc, w, h, hud = {}) {
  const edge = 1, out = { ok: true, size: [w, h], citiesOut: [], routesOut: [], labelsOut: [], labelHits: [], underHud: [], hiddenLabels: [], labels: 0, margins: null };
  const box = { x0: edge, y0: edge, x1: w - edge, y1: h - edge };
  const R = Object.entries(hud);
  let l = 1e9, r = 1e9, t = 1e9, b = 1e9;
  for (const c of CITIES) {
    const p = sc.screenPos('city', c[0]);
    if (!p) { out.citiesOut.push(c[2]); continue; }
    l = Math.min(l, p.x); r = Math.min(r, w - p.x); t = Math.min(t, p.y); b = Math.min(b, h - p.y);
    if (p.x < box.x0 || p.x > box.x1 || p.y < box.y0 || p.y > box.y1) out.citiesOut.push(c[2] + ' ' + p.x.toFixed(0) + ',' + p.y.toFixed(0));
    for (const [name, q] of R) if (inRect(p, q, 3)) out.underHud.push(c[2] + ' під ' + name);
  }
  for (const rt of ROUTES) {
    const p = sc.screenPos('route', rt[0]);
    if (!p) { out.routesOut.push(rt[0]); continue; }
    if (p.x < box.x0 || p.x > box.x1 || p.y < box.y0 || p.y > box.y1) out.routesOut.push(rt[0]);
    for (const [name, q] of R) if (inRect(p, q, 2)) out.underHud.push('шлях ' + rt[0] + ' під ' + name);
  }
  if (sc.labelBoxes) {
    const L = sc.labelBoxes();
    out.labels = L.length;
    out.hiddenLabels = CITIES.map((c) => c[2]).filter((n) => !L.some((x) => x.name === n));
    for (const x of L) {
      if (x.x0 < 0 || x.y0 < 0 || x.x1 > w || x.y1 > h) out.labelsOut.push(x.name);
      for (const [name, q] of R) if (cross(x, q)) out.underHud.push('підпис ' + x.name + ' під ' + name);
    }
    for (let i = 0; i < L.length; i++) for (let j = i + 1; j < L.length; j++) if (cross(L[i], L[j])) out.labelHits.push(L[i].name + '×' + L[j].name);
  }
  out.margins = { left: Math.round(l), right: Math.round(r), top: Math.round(t), bottom: Math.round(b) };
  out.ok = !out.citiesOut.length && !out.routesOut.length && !out.labelsOut.length && !out.underHud.length;
  return out;
}
