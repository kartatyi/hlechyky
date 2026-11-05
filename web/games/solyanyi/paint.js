// «Соляний шлях» — картина-мапа: одна велика намальована текстура. Це головний засіб проти «квадратів»: земля й море мають
// той колір, який намалювали пером і пензлем (вектори по справжньому контуру), а не колір вершин.
// Малюємо лише в канвас: жодних файлів. Три манери: антична мапа, друкована дошка, петриківка.
// Назви гри й легенди на мапі нема: це HUD картки. Значки вантажів — ATLAS: той самий порядок, що CARGO + сірий.
import { W, H, PAD, MAT, fbm, noise, smooth, rng } from './terrain.js';

const OFF = PAD + MAT, CW = W + 2 * OFF, CH = H + 2 * OFF;
export const CANVAS_DOMAIN = { OFF, CW, CH };
const hex = (h) => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
const mixc = (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
const rgba = (c, a = 1) => `rgba(${c[0] | 0},${c[1] | 0},${c[2] | 0},${a})`;

function newCanvas(w, h) { const c = document.createElement('canvas'); c.width = w; c.height = h; return c; }

/** Шар: поповнює пікселі функцією f(x, y, out) у координатах карти (x,y); низька роздільність + білінійне розтягнення = м'які змивки. */
function layer(wpx, f) {
  const hpx = Math.round(wpx * CH / CW), c = newCanvas(wpx, hpx), g = c.getContext('2d'), img = g.createImageData(wpx, hpx), d = img.data;
  const o = [0, 0, 0, 255], kx = CW / wpx, ky = CH / hpx;
  for (let j = 0, i = 0; j < hpx; j++) { const y = (j + 0.5) * ky - OFF; for (let k = 0; k < wpx; k++, i += 4) { o[3] = 255; f((k + 0.5) * kx - OFF, y, o); d[i] = o[0]; d[i + 1] = o[1]; d[i + 2] = o[2]; d[i + 3] = o[3]; } }
  g.putImageData(img, 0, 0);
  return c;
}
function grainTile(size, seed) {
  const c = newCanvas(size, size), g = c.getContext('2d'), img = g.createImageData(size, size), r = rng(seed);
  for (let i = 0; i < img.data.length; i += 4) { const v = 255 - (r() * r() * 70 | 0); img.data[i] = v; img.data[i + 1] = v; img.data[i + 2] = v; img.data[i + 3] = 255; }
  g.putImageData(img, 0, 0); return c;
}

/** Рівні кільця вздовж берега: справжні зсуви контуру, бо це обведення зі стиранням усередині. */
function coastRings(paint, coast, clip, widths, lw, styleFn) {
  const { g, px, s } = paint;
  const c = newCanvas(px, g.canvas.height), q = c.getContext('2d');
  q.setTransform(s, 0, 0, s, OFF * s, OFF * s); q.lineJoin = 'round'; q.lineCap = 'round';
  for (let i = widths.length - 1; i >= 0; i--) {
    q.globalCompositeOperation = 'source-over'; q.strokeStyle = styleFn(i); q.lineWidth = widths[i] * 2 + lw; q.stroke(coast);
    q.globalCompositeOperation = 'destination-out'; q.strokeStyle = '#000'; q.lineWidth = Math.max(0.1, widths[i] * 2 - lw); q.stroke(coast);
  }
  g.save(); g.clip(clip); { const nl = new Path2D(); nl.rect(-OFF, -OFF, CW, CH); nl.addPath(paint.geo.land); g.clip(nl, 'evenodd'); } g.setTransform(1, 0, 0, 1, 0, 0); g.drawImage(c, 0, 0); g.restore();
}
function softStroke(g, path, col, maxW, steps, alpha) {
  g.lineJoin = 'round'; g.lineCap = 'round'; g.strokeStyle = col;
  const a0 = g.globalAlpha; g.globalAlpha = alpha;
  for (let i = steps; i > 0; i--) { g.lineWidth = maxW * i / steps; g.stroke(path); }
  g.globalAlpha = a0;
}
function textAlong(g, text, pts, at, font, col, halo, spacing = 0.08) {
  // літери вздовж ламаної (річки, моря)
  g.save(); g.font = font; g.textAlign = 'center'; g.textBaseline = 'middle';
  const chars = [...text]; const size = parseFloat(font.match(/(\d+(?:\.\d+)?)px/)[1]);
  const ws = chars.map((c) => g.measureText(c).width + spacing * size), tot = ws.reduce((a, b) => a + b, 0);
  const acc = [0]; for (let i = 1; i < pts.length; i++) acc.push(acc[i - 1] + Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]));
  const L = acc[acc.length - 1]; if (tot > L) { g.restore(); return; }
  let d = L * at - tot / 2;
  const posAt = (t) => { let i = 1; while (i < acc.length - 1 && acc[i] < t) i++; const k = (t - acc[i - 1]) / (acc[i] - acc[i - 1] || 1); return [pts[i - 1][0] + (pts[i][0] - pts[i - 1][0]) * k, pts[i - 1][1] + (pts[i][1] - pts[i - 1][1]) * k, Math.atan2(pts[i][1] - pts[i - 1][1], pts[i][0] - pts[i - 1][0])]; };
  // напрям — щоб текст не став догори ногами
  const a0 = posAt(L * at - 20), a1 = posAt(L * at + 20), flip = a1[0] < a0[0];
  const seq = flip ? chars.map((_, i) => chars.length - 1 - i) : chars.map((_, i) => i);
  for (const i of chars.map((_, k) => k)) {
    const c = chars[i], w = ws[i], t = flip ? d + tot - (ws.slice(0, i).reduce((a, b) => a + b, 0) + w / 2) : d + ws.slice(0, i).reduce((a, b) => a + b, 0) + w / 2;
    const [x, y, an] = posAt(t);
    g.save(); g.translate(x, y); g.rotate(flip ? an + Math.PI : an);
    if (halo) { g.strokeStyle = halo; g.lineWidth = size * 0.22; g.lineJoin = 'round'; g.strokeText(c, 0, 0); }
    g.fillStyle = col; g.fillText(c, 0, 0); g.restore();
  }
  g.restore();
}

/** Лісовий пояс: Полісся й гори. 0..1. Спільне для картини й 3D-дерев. */
export function forestAt(geo, x, y) {
  if (geo.maskAt(x, y) !== 1) return 0;
  const R = geo.bil(geo.R, x, y);
  const n = fbm(x / 210 + 3, y / 210 + 7, 3);
  const north = smooth(760, 480, y + (n - 0.5) * 520 + (x > 2900 ? 300 : 0));
  const mount = smooth(0.04, 0.08, R) * (1 - smooth(0.13, 0.18, R)) * smooth(0.42, 0.6, fbm(x / 95 + 21, y / 95 + 4, 3));
  const patch = smooth(0.66, 0.74, fbm(x / 120 + 11, y / 120 - 3, 3)) * (y < 1500 ? 0.7 : 0.15);
  return Math.max(north * smooth(0.36, 0.56, n), mount, patch);
}

function hillshade(geo) {
  const { mw, mh, R, ms } = geo, S = new Float32Array(mw * mh), lx = -0.78, ly = -0.25, e = 1;
  const ex = 2 * e / ms * (1 / 400);
  for (let j = 1; j < mh - 1; j++) for (let i = 1; i < mw - 1; i++) {
    const k = j * mw + i, dx = (R[k + 1] - R[k - 1]) / ex, dy = (R[k + mw] - R[k - mw]) / ex;
    S[k] = (-dx * lx - dy * ly) * 1.0;                // >0 — до світла
  }
  return S;
}
function coastPathOf(geo) {
  const p = new Path2D(geo.outline);
  p.addPath(geo.foreignPath);
  return p;
}
/** Тінь підняної суші на море й сусідні землі (намальована, бо справжня тінь є лише на високій якості). */
function slabShadow(g, geo, col) {
  g.save(); clipRect(g);
  const p = new Path2D(); p.rect(-OFF, -OFF, CW, CH); p.addPath(geo.land);
  g.clip(p, 'evenodd');
  for (const k of [18, 13, 9, 5]) { g.save(); g.translate(k * 1.0, k * 0.28); g.fillStyle = col; g.fill(geo.land, 'evenodd'); g.restore(); }
  g.restore();
  // і від паспарту всередину вікна
  g.save(); clipRect(g); g.strokeStyle = col; for (const [dx, w] of [[6, 40], [4, 26], [2, 14]]) { g.lineWidth = w; g.strokeRect(-PAD + dx * 0.6, -PAD + dx * 0.2, W + 2 * PAD, H + 2 * PAD); } g.restore();
}
/** Чумацька валка: віз і два воли збоку, пером — прикраса порожнього кута мапи. */
function chumakSketch(g, x, y, sc, ink, fill) {
  g.save(); g.translate(x, y); g.scale(sc, sc); g.lineCap = 'round'; g.lineJoin = 'round'; g.strokeStyle = ink; g.lineWidth = 4;
  // віз
  g.fillStyle = fill; g.beginPath(); g.moveTo(-120, -44); g.lineTo(-8, -44); g.lineTo(6, -82); g.lineTo(-136, -82); g.closePath(); g.fill(); g.stroke();
  for (let i = 1; i < 6; i++) { const t = -130 + i * 22; g.beginPath(); g.moveTo(t, -80); g.lineTo(t + 3, -46); g.stroke(); }
  // купа солі
  g.fillStyle = 'rgba(255,255,255,.75)'; g.beginPath(); g.moveTo(-132, -82); g.quadraticCurveTo(-66, -142, 2, -82); g.closePath(); g.fill(); g.stroke();
  for (const [wx, R] of [[-104, 30], [-24, 26]]) { g.beginPath(); g.arc(wx, -28, R, 0, 6.3); g.fillStyle = fill; g.fill(); g.stroke(); g.beginPath(); for (let k = 0; k < 6; k++) { const a = k / 6 * 6.283; g.moveTo(wx, -28); g.lineTo(wx + Math.cos(a) * R, -28 + Math.sin(a) * R); } g.stroke(); g.beginPath(); g.arc(wx, -28, 5, 0, 6.3); g.fillStyle = ink; g.fill(); }
  // дишель і воли
  g.beginPath(); g.moveTo(0, -44); g.lineTo(84, -52); g.stroke();
  for (const dx of [0, 26]) {
    g.save(); g.translate(dx, dx ? 6 : 0); g.fillStyle = fill;
    g.beginPath(); g.moveTo(80, -46); g.bezierCurveTo(80, -86, 170, -92, 178, -74); g.lineTo(204, -74); g.lineTo(208, -58); g.lineTo(188, -54); g.bezierCurveTo(180, -40, 170, -40, 166, -38); g.lineTo(166, 0); g.lineTo(156, 0); g.lineTo(152, -34); g.lineTo(106, -34); g.lineTo(102, 0); g.lineTo(92, 0); g.closePath(); g.fill(); g.stroke();
    g.beginPath(); g.moveTo(186, -76); g.quadraticCurveTo(190, -98, 214, -100); g.moveTo(178, -76); g.quadraticCurveTo(176, -98, 156, -104); g.stroke();
    g.restore();
  }
  g.beginPath(); g.moveTo(-170, 2); g.lineTo(250, 4); g.stroke();
  g.restore();
}
function clipRect(g) { g.beginPath(); g.rect(-PAD, -PAD, W + 2 * PAD, H + 2 * PAD); g.clip(); }

// ---------------------------------------------------------------------------------------------------------- спільні елементи
function drawRiversUnder(g, geo, routes, outer, inner) {
  g.lineCap = 'round'; g.lineJoin = 'round';
  for (const rv of geo.rivers) {
    const P = new Path2D(); rv.pts.forEach((q, i) => (i ? P.lineTo(q[0], q[1]) : P.moveTo(q[0], q[1])));
    rv.path = P;
    g.strokeStyle = outer; g.lineWidth = rv.w * 2.5; g.stroke(P);
  }
  for (const rv of geo.rivers) { g.strokeStyle = inner; g.lineWidth = rv.w * 1.9; g.stroke(rv.path); }
}
function routeUnderlay(g, routes, cities, col, W_) {
  g.lineCap = 'butt'; g.lineJoin = 'round'; g.strokeStyle = col; g.lineWidth = W_;
  for (const r of routes) { g.beginPath(); r.samples.forEach((p, i) => (i ? g.lineTo(p[0], p[1]) : g.moveTo(p[0], p[1]))); g.stroke(); }
}
function cityShadow(g, cities, col) {
  for (const c of cities) { const gr = g.createRadialGradient(c.x + 5, c.y + 6, c.r * 0.5, c.x + 5, c.y + 6, c.r * 1.55); gr.addColorStop(0, col); gr.addColorStop(1, col.replace(/[\d.]+\)$/, '0)')); g.fillStyle = gr; g.beginPath(); g.arc(c.x + 5, c.y + 6, c.r * 1.55, 0, 6.3); g.fill(); }
}
function freeSpot(geo, x, y, dRoute) {
  if (geo.routeDist) { const d = geo.routeDist[geo.pxOf(x, y)] / geo.ms; if (d < dRoute) return false; }
  return true;
}

// ---------------------------------------------------------------------------------------------------------- ANTICHNA
function paintParchment(P) {
  const { g, geo, data, s, hi } = P, r = rng(901);
  const coast = coastPathOf(geo);
  const paper = hex('#dcc493'), paperD = hex('#c3a26c'), paperL = hex('#e9d6ab');
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.drawImage(layer(hi ? 900 : 600, (x, y, o) => {
    const a = fbm(x / 900 + 2, y / 900 + 5, 4), b = fbm(x / 170, y / 170, 3), c = smooth(0.62, 0.76, fbm(x / 260 + 40, y / 260, 3));
    const t = Math.min(1, Math.max(0, (a - 0.3) * 1.5 + (b - 0.5) * 0.4));
    const m = mixc(paperL, paperD, t);
    const k = 1 - c * 0.08;
    o[0] = m[0] * k; o[1] = m[1] * k; o[2] = m[2] * k;
  }), 0, 0, g.canvas.width, g.canvas.height);
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // сусідні землі — штрих в один бік; море — холодніша змивка
  const seaClip = new Path2D(geo.seaPath);
  g.save(); g.clip(new Path2D(`M${-OFF} ${-OFF}H${W + OFF}V${H + OFF}H${-OFF}Z`));
  g.restore();
  g.save(); clipRect(g);
  g.fillStyle = 'rgba(92,124,116,.36)'; g.fill(geo.seaPath);
  g.fillStyle = 'rgba(40,74,80,.06)'; g.fill(geo.seaPath);
  g.restore();
  // суша поверх моря — свіжа змивка
  slabShadow(g, geo, 'rgba(44,28,10,.07)');
  g.fillStyle = 'rgb(233,214,170)'; g.fill(geo.land, 'evenodd');
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.save(); g.globalAlpha = 0.55; g.globalCompositeOperation = 'multiply';
  g.drawImage(layer(hi ? 700 : 500, (x, y, o) => {
    if (geo.maskAt(x, y) !== 1) { o[0] = o[1] = o[2] = 255; return; }
    const a = fbm(x / 300 + 12, y / 300 + 5, 4), c = fbm(x / 60, y / 60, 2);
    const v = 215 + (a - 0.5) * 120 + (c - 0.5) * 34;
    o[0] = v + 18; o[1] = v + 2; o[2] = v - 38;
  }), 0, 0, g.canvas.width, g.canvas.height);
  g.restore();
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // штрих на чужих землях
  {
    const c = newCanvas(g.canvas.width, g.canvas.height), q = c.getContext('2d');
    q.setTransform(s, 0, 0, s, OFF * s, OFF * s);
    q.strokeStyle = 'rgba(70,46,22,.46)'; q.lineWidth = 1.5;
    for (let d = -3000; d < 7000; d += 22 + r() * 9) { q.beginPath(); let x = d, y = -OFF; while (y < H + OFF) { const seg = 90 + r() * 240, gap = 8 + r() * 26; q.moveTo(x, y); x += seg + (r() - 0.5) * 3; y += seg + (r() - 0.5) * 3; q.lineTo(x, y); x += gap; y += gap; } q.stroke(); }
    q.globalCompositeOperation = 'destination-out'; q.fillStyle = '#000'; q.fill(geo.seaPath); q.fill(geo.land, 'evenodd');
    q.fillRect(-OFF, -OFF, CW, OFF - PAD); q.fillRect(-OFF, H + PAD, CW, OFF - PAD); q.fillRect(-OFF, -OFF, OFF - PAD, CH); q.fillRect(W + PAD, -OFF, OFF - PAD, CH);
    // штрих густий лише біля кордону й сходить нанівець — як тушування гравера, а не лінійований зошит
    q.setTransform(1, 0, 0, 1, 0, 0); q.globalCompositeOperation = 'destination-in';
    q.drawImage(layer(hi ? 700 : 480, (x, y, o) => { o[0] = o[1] = o[2] = 0; const d = -geo.bil(geo.sU, x, y), n = fbm(x / 120 + 3, y / 120, 2); o[3] = 255 * (1 - smooth(30, 210 + (n - 0.5) * 170, d)); }), 0, 0, c.width, c.height);
    g.save(); g.setTransform(1, 0, 0, 1, 0, 0); g.drawImage(c, 0, 0); g.restore();
  }
  // хвилеписи: тонкі лінії, що повторюють берег, як гравери XVII ст.
  const seaEven = geo.seaPath;
  const widths = [7, 16, 27, 40, 56, 76, 100, 130];
  g.save(); clipRect(g);
  coastRings(P, coast, seaEven, widths, 3.0, (i) => `rgba(52,42,28,${0.8 - i * 0.085})`);
  g.restore();
  // тінь суші біля берега всередині — м'яка сепія
  g.save(); g.clip(geo.land, 'evenodd');
  softStroke(g, geo.outline, 'rgba(120,76,34,.05)', 90, 12, 1);
  g.restore();
  // отмивка рельєфу
  const S = hillshade(geo), ms = geo.ms, Rf = geo.R;
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.save(); g.globalCompositeOperation = 'multiply';
  g.drawImage(layer(hi ? 1100 : 760, (x, y, o) => {
    const i = geo.pxOf(x, y); const v = S[i], m = geo.mask[i];
    if (m === 2 || x < -PAD || y < -PAD || x > W + PAD || y > H + PAD) { o[0] = o[1] = o[2] = 255; return; }
    const d = Math.min(1, Math.max(0, -v * 0.85 + smooth(0.05, 0.22, Rf[i]) * 0.1)) * (m === 1 ? 1 : 0.4);
    o[0] = 255 - d * 110; o[1] = 255 - d * 150; o[2] = 255 - d * 190;
  }), 0, 0, g.canvas.width, g.canvas.height);
  g.restore();
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // штрихи схилів — гравюрні гачури, що йдуть вниз по схилу
  g.lineCap = 'round';
  const nH = hi ? 26000 : 9000;
  for (let i = 0; i < nH; i++) {
    const x = r() * W, y = r() * H;
    if (geo.maskAt(x, y) !== 1) continue;
    const e = 7, dx = geo.bil(Rf, x + e, y) - geo.bil(Rf, x - e, y), dy = geo.bil(Rf, x, y + e) - geo.bil(Rf, x, y - e);
    const sl = Math.hypot(dx, dy) / (2 * e) * 400;
    if (sl < 0.055 || r() > sl * 2.2) continue;
    const L = 6 + Math.min(17, sl * 30), ang = Math.atan2(dy, dx) + Math.PI + (r() - 0.5) * 0.16;
    const shadeSide = (-dx * -0.78 + -dy * -0.25) < 0;
    g.strokeStyle = shadeSide ? 'rgba(66,40,16,.62)' : 'rgba(84,56,26,.34)';
    g.lineWidth = shadeSide ? 2.1 : 1.3;
    g.beginPath(); g.moveTo(x, y); g.lineTo(x + Math.cos(ang) * L, y + Math.sin(ang) * L); g.stroke();
  }
  // межі областей — пунктир
  g.save(); g.clip(geo.land, 'evenodd');
  g.setLineDash([3, 11]); g.lineCap = 'round'; g.strokeStyle = 'rgba(92,58,28,.72)'; g.lineWidth = 3.2; g.stroke(geo.inner); g.setLineDash([]);
  g.restore();
  // ліс: перові деревця
  const nF = hi ? 7000 : 2600, placed = [];
  for (let i = 0; i < nF; i++) {
    const x = r() * W, y = r() * H, f = forestAt(geo, x, y);
    if (f < 0.25 || r() > f * 0.8 || !freeSpot(geo, x, y, 46)) continue;
    let ok = true; for (let k = placed.length - 1; k >= Math.max(0, placed.length - 80); k--) if (Math.hypot(placed[k][0] - x, placed[k][1] - y) < 15) { ok = false; break; }
    if (!ok) continue; placed.push([x, y]);
    const h = 13 + r() * 9;
    g.strokeStyle = 'rgba(60,44,20,.72)'; g.lineWidth = 1.7;
    g.fillStyle = 'rgba(95,100,50,.28)';
    g.beginPath(); g.moveTo(x, y); g.lineTo(x, y - h * 0.3); g.stroke();
    g.beginPath(); g.moveTo(x - h * 0.34, y - h * 0.26); g.quadraticCurveTo(x - h * 0.1, y - h * 0.8, x, y - h); g.quadraticCurveTo(x + h * 0.1, y - h * 0.8, x + h * 0.34, y - h * 0.26); g.closePath(); g.fill(); g.stroke();
  }
  // степ: дрібні кущики
  const nT = hi ? 2400 : 900;
  g.strokeStyle = 'rgba(92,70,36,.5)'; g.lineWidth = 1.5;
  for (let i = 0; i < nT; i++) {
    const x = r() * W, y = 1100 + r() * 1600;
    if (geo.maskAt(x, y) !== 1 || !freeSpot(geo, x, y, 40) || geo.bil(geo.sS, x, y) > -30) continue;
    if (noise(x / 160, y / 160) < 0.45) continue;
    g.beginPath(); g.moveTo(x - 6, y); g.lineTo(x - 8, y - 6); g.moveTo(x, y); g.lineTo(x, y - 9); g.moveTo(x + 6, y); g.lineTo(x + 8, y - 6); g.stroke();
  }
  // Сиваш: соляні лагуни — штрих рожевуватий
  // річки: чорнильна двоконтурна лінія
  drawRiversUnder(g, geo, data.routes, 'rgba(58,40,22,.85)', 'rgb(158,180,160)');
  // шляхи: притінення під плашками
  routeUnderlay(g, data.routes, data.cities, 'rgba(96,62,24,.14)', 62);
  routeUnderlay(g, data.routes, data.cities, 'rgba(70,40,14,.22)', 50);
  cityShadow(g, data.cities, 'rgba(50,28,8,.42)');
  // контур країни: густа чорнильна лінія і тонка побіч
  g.lineJoin = 'round'; g.lineCap = 'round';
  g.strokeStyle = 'rgba(38,22,8,.9)'; g.lineWidth = 4.6; g.stroke(geo.outline);
  g.save(); g.clip(geo.land, 'evenodd'); g.strokeStyle = 'rgba(132,74,34,.2)'; g.lineWidth = 26; g.stroke(geo.outline); g.restore();
  g.save(); clipRect(g); g.strokeStyle = 'rgba(38,22,8,.8)'; g.lineWidth = 3.4; g.stroke(geo.foreignPath); g.restore();
  // назви річок
  const inkItal = (px) => `italic ${px}px Georgia,'Noto Serif','Times New Roman',serif`;
  for (const [name, at] of [['Дніпро', 0.5], ['Дністер', 0.58], ['Південний Буг', 0.5], ['Десна', 0.5], ['Сіверський Донець', 0.5]]) {
    const best = geo.rivers.filter((q) => q.name === name).sort((a, b) => b.pts.length - a.pts.length)[0];
    if (best) { const off = best.pts.map((q) => [q[0], q[1] - 20]); textAlong(g, name, off, at, inkItal(25), 'rgba(40,52,52,.92)', 'rgba(233,214,170,.62)', 0.16); }
  }
  // морські написи
  g.save(); g.fillStyle = 'rgba(40,30,16,.82)'; g.font = inkItal(84); g.textAlign = 'center';
  const arc = (cx, cy, Rr, a0, a1, n = 30) => { const o = []; for (let i = 0; i <= n; i++) { const a = a0 + (a1 - a0) * i / n; o.push([cx + Math.cos(a) * Rr, cy + Math.sin(a) * Rr]); } return o; };
  textAlong(g, 'Чорне море', arc(2050, 3760, 1320, -1.92, -1.2), 0.5, inkItal(80), 'rgba(44,36,20,.86)', null, 0.2);
  textAlong(g, 'Азовське море', arc(3180, 3000, 980, -1.86, -1.28), 0.5, inkItal(42), 'rgba(44,36,20,.8)', null, 0.14);
  g.restore();
  // роза вітрів і румби
  const cx = 3380, cy = 2560, RR = 118;
  g.save(); clipRect(g); g.clip(geo.seaPath);
  g.strokeStyle = 'rgba(70,46,22,.22)'; g.lineWidth = 1.7;
  for (let i = 0; i < 16; i++) { const a = i * Math.PI / 8 + 0.1; g.beginPath(); g.moveTo(cx, cy); g.lineTo(cx + Math.cos(a) * 3600, cy + Math.sin(a) * 3600); g.stroke(); }
  g.restore();
  compass(g, cx, cy, RR, 'rgba(40,24,10,.96)', 'rgb(232,212,170)', 'rgba(150,42,30,.95)');
  parchmentNames(g);
  wallachia(g, geo, r);
  seaSerpent(g, 2010, 2660, 0.86, 'rgba(40,26,12,.9)', 'rgba(120,132,104,.9)');
  inkShip(g, 3020, 2640, 0.8, 'rgba(40,26,12,.9)', 'rgb(232,212,170)');
  chumakSketch(g, 3640, 300, 1.15, 'rgba(40,24,10,.86)', 'rgba(226,202,150,.86)');
  // поле: лінійка з градусною рамкою; згини аркуша і кільце від кухля
  neatline(g, 'rgba(40,24,10,.9)', 'rgb(240,226,190)');
  sheetWear(g);
  // обпалені краї й плями
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.save(); g.globalCompositeOperation = 'multiply';
  g.drawImage(layer(hi ? 800 : 500, (x, y, o) => {
    const d = Math.min(x + OFF, y + OFF, W + OFF - x, H + OFF - y);
    const n = fbm(x / 220 + 3, y / 220 - 2, 4);
    const e = Math.max(0, 1 - smooth(14, 230 + (n - 0.5) * 240, d));
    const fox = smooth(0.74, 0.8, fbm(x / 70 + 21, y / 70, 2)) * 0.18;
    const k = Math.min(1, e * 0.9 + fox);
    o[0] = 255 - k * 112; o[1] = 255 - k * 150; o[2] = 255 - k * 190;
  }), 0, 0, g.canvas.width, g.canvas.height);
  g.globalAlpha = 0.5;
  g.fillStyle = g.createPattern(grainTile(256, 5), 'repeat'); g.fillRect(0, 0, g.canvas.width, g.canvas.height);
  g.restore();
}
function compass(g, cx, cy, R, ink, paper, accent) {
  g.save(); g.translate(cx, cy);
  g.lineWidth = 2.4; g.strokeStyle = ink;
  g.beginPath(); g.arc(0, 0, R * 0.92, 0, 6.3); g.stroke(); g.lineWidth = 1.3; g.beginPath(); g.arc(0, 0, R * 0.84, 0, 6.3); g.stroke();
  for (let i = 0; i < 32; i++) { const a = i * Math.PI / 16; g.beginPath(); g.moveTo(Math.cos(a) * R * 0.84, Math.sin(a) * R * 0.84); g.lineTo(Math.cos(a) * R * 0.92, Math.sin(a) * R * 0.92); g.stroke(); }
  const pt = (a, L, w, c1, c2) => { const dx = Math.cos(a), dy = Math.sin(a), nx = -dy, ny = dx; g.fillStyle = c1; g.beginPath(); g.moveTo(dx * L, dy * L); g.lineTo(nx * w, ny * w); g.lineTo(0, 0); g.closePath(); g.fill(); g.fillStyle = c2; g.beginPath(); g.moveTo(dx * L, dy * L); g.lineTo(-nx * w, -ny * w); g.lineTo(0, 0); g.closePath(); g.fill(); g.strokeStyle = ink; g.lineWidth = 1.2; g.beginPath(); g.moveTo(nx * w, ny * w); g.lineTo(dx * L, dy * L); g.lineTo(-nx * w, -ny * w); g.stroke(); };
  for (let i = 0; i < 4; i++) pt(Math.PI / 4 + i * Math.PI / 2, R * 0.72, R * 0.1, paper, ink);
  for (let i = 0; i < 4; i++) pt(i * Math.PI / 2, R * 1.08, R * 0.16, paper, i === 3 ? accent : ink);
  g.fillStyle = ink; g.beginPath(); g.arc(0, 0, R * 0.055, 0, 6.3); g.fill();
  g.font = `italic 700 ${R * 0.38}px Georgia,'Noto Serif','Times New Roman',serif`; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText('Пн', 0, -R * 1.3);
  g.restore();
}
function neatline(g, ink, paper) {
  const a = -PAD - 22, b = -PAD - 62, c = -PAD - 76;
  g.lineWidth = 5; g.strokeStyle = ink; g.strokeRect(a, a, W - 2 * a, H - 2 * a);
  g.lineWidth = 3; g.strokeRect(b, b, W - 2 * b, H - 2 * b);
  g.lineWidth = 7; g.strokeRect(c, c, W - 2 * c, H - 2 * c);
  // градусні шашки
  const seg = 120; g.fillStyle = ink;
  const y0 = a - 9, h = -(b - a) - 22;
  for (let i = 0, x = a; x < W - a - 1; x += seg, i++) if (i % 2 === 0) { const w = Math.min(seg, W - a - x); g.fillRect(x, b + 9, w, -b + a - 18); g.fillRect(x, H - a + 9, w, -b + a - 18); }
  for (let i = 0, y = a; y < H - a - 1; y += seg, i++) if (i % 2 === 0) { const hh = Math.min(seg, H - a - y); g.fillRect(b + 9, y, -b + a - 18, hh); g.fillRect(W - a + 9, y, -b + a - 18, hh); }
}

// ---------------------------------------------------------------------------------------------------------- назви, малюнки на старій мапі
/** Сусіди: [назва, x, y, кут, кегль]. Лишаються на мапі в усіх темах (рішення власника: solyanyi-look.md). */
const NEIGHBOURS = [
  ['Литва', 1320, 96, -0.02, 62], ['Польща', 170, 372, -0.34, 58], ['Московія', 3560, 640, 0.14, 62],
  ['Волощина', 980, 1880, 0.1, 62], ['Дон', 3880, 1700, 1.3, 50], ['Кубань', 3740, 2360, 0.3, 50],
];
const OLD_LANDS = [
  ['Гетьманщина', 2250, 690, -0.06, 40], ['Дике поле', 2480, 1660, 0.03, 42], ['Поділля', 1250, 1370, -0.3, 34],
  ['Таврія', 2830, 2150, 0.02, 36], ['Буджак', 1640, 2200, -0.5, 30], ['Запорожжя', 2720, 1750, -0.1, 30],
];
/** Напис із розрядкою, повернутий на кут. */
function spaced(g, t, x, y, a, font, col, sp = 0.18, halo) {
  g.save(); g.translate(x, y); g.rotate(a); g.font = font; g.textAlign = 'left'; g.textBaseline = 'middle';
  const size = parseFloat(font.match(/(\d+(?:\.\d+)?)px/)[1]), ch = [...t], ws = ch.map((c) => g.measureText(c).width + sp * size), tot = ws.reduce((p, q) => p + q, 0);
  let cx = -tot / 2;
  ch.forEach((c, i) => {
    if (halo) { g.strokeStyle = halo; g.lineWidth = size * 0.18; g.lineJoin = 'round'; g.strokeText(c, cx, 0); }
    g.fillStyle = col; g.fillText(c, cx, 0); cx += ws[i];
  });
  g.restore();
}
function parchmentNames(g) {
  const ital = (w, px) => `italic ${w} ${px}px Georgia,'Noto Serif','Times New Roman',serif`;
  for (const [t, x, y, a, px] of NEIGHBOURS) spaced(g, t, x, y, a, ital(600, px), 'rgba(52,34,16,.74)', 0.26);
  for (const [t, x, y, a, px] of OLD_LANDS) spaced(g, t.toUpperCase(), x, y, a, ital(600, px), 'rgba(116,76,38,.46)', 0.24);
}
/** Порожній південно-західний кут: кілька гірських пасом пером — земля далі живе, хоч шляхів там нема. */
function wallachia(g, geo, r) {
  g.save(); clipRect(g); g.lineCap = 'round'; g.lineJoin = 'round';
  const peak = (x, y, h, w) => {
    g.fillStyle = 'rgb(226,204,158)'; g.strokeStyle = 'rgba(60,40,18,.78)'; g.lineWidth = 3;
    g.beginPath(); g.moveTo(x - w, y); g.lineTo(x - w * 0.12, y - h); g.lineTo(x + w * 0.2, y - h * 0.68); g.lineTo(x + w, y); g.fill(); g.stroke();
    g.lineWidth = 1.8; g.strokeStyle = 'rgba(60,40,18,.5)';
    for (let k = 1; k < 5; k++) { const f = k / 5; g.beginPath(); g.moveTo(x + w * 0.04 * k, y - h * (1 - f) * 0.92); g.lineTo(x + w * 0.04 * k + w * f * 0.4, y - 1); g.stroke(); }
  };
  for (const [x0, y0, n, dir] of [[210, 1760, 7, 0.24], [150, 2300, 8, -0.12], [760, 2500, 6, -0.3]]) {
    for (let i = 0; i < n; i++) {
      const x = x0 + i * 62 + r() * 18, y = y0 + i * 62 * dir + r() * 26, h = 46 + r() * 44;
      if (geo.maskAt(x, y) === 0 && geo.bil(geo.sU, x, y) < -30) peak(x, y, h, h * (0.72 + r() * 0.24));
    }
  }
  g.restore();
}
/** Морський змій на три вигини — як на гравюрах. */
function seaSerpent(g, x, y, sc, ink, body) {
  g.save(); g.translate(x, y); g.scale(sc, sc); g.lineJoin = 'round'; g.lineCap = 'round'; g.lineWidth = 3.6; g.strokeStyle = ink; g.fillStyle = body;
  for (const [dx, R, h] of [[-104, 34, 50], [-30, 28, 42], [30, 20, 28]]) {
    g.beginPath(); g.moveTo(dx - R, 0); g.bezierCurveTo(dx - R * 0.9, -h * 1.3, dx + R * 0.9, -h * 1.3, dx + R, 0); g.lineTo(dx + R * 0.56, 0); g.bezierCurveTo(dx + R * 0.46, -h * 0.66, dx - R * 0.46, -h * 0.66, dx - R * 0.56, 0); g.closePath(); g.fill(); g.stroke();
  }
  // шия й голова
  g.beginPath(); g.moveTo(-178, 0); g.bezierCurveTo(-186, -60, -190, -86, -170, -104); g.lineTo(-132, -108); g.lineTo(-104, -92); g.lineTo(-142, -86); g.bezierCurveTo(-154, -70, -152, -30, -148, 0); g.closePath(); g.fill(); g.stroke();
  g.beginPath(); for (const k of [0, 1, 2]) { g.moveTo(-178 + k * 14, -106 - k); g.lineTo(-186 + k * 14, -126 - k); g.lineTo(-166 + k * 14, -108 - k); } g.stroke();
  g.fillStyle = ink; g.beginPath(); g.arc(-140, -99, 3.6, 0, 6.3); g.fill();
  g.lineWidth = 2.2; g.beginPath();
  for (let i = 0; i < 7; i++) { const X = -210 + i * 46; g.moveTo(X, 8 + (i % 2) * 7); g.quadraticCurveTo(X + 11, 1 + (i % 2) * 7, X + 22, 8 + (i % 2) * 7); }
  g.stroke();
  g.restore();
}
function inkShip(g, x, y, sc, ink, sail) {
  g.save(); g.translate(x, y); g.scale(sc, sc); g.lineJoin = 'round'; g.lineCap = 'round'; g.lineWidth = 3.2; g.strokeStyle = ink; g.fillStyle = sail;
  g.beginPath(); g.moveTo(-70, -16); g.lineTo(66, -16); g.quadraticCurveTo(58, 14, 30, 14); g.lineTo(-44, 14); g.quadraticCurveTo(-62, 10, -70, -16); g.closePath(); g.fill(); g.stroke();
  g.beginPath(); g.moveTo(-6, -16); g.lineTo(-6, -104); g.stroke();
  g.beginPath(); g.moveTo(-50, -90); g.quadraticCurveTo(-12, -68, -46, -28); g.lineTo(40, -28); g.quadraticCurveTo(20, -66, 38, -90); g.closePath(); g.fill(); g.stroke();
  g.beginPath(); g.moveTo(-6, -104); g.lineTo(18, -114); g.lineTo(-6, -118); g.stroke();
  g.lineWidth = 2.2; g.beginPath(); for (let i = 0; i < 5; i++) { const X = -86 + i * 38; g.moveTo(X, 24); g.quadraticCurveTo(X + 10, 17, X + 20, 24); } g.stroke();
  g.restore();
}
/** Згини аркуша й кільце від кухля. */
function sheetWear(g) {
  g.save(); g.lineCap = 'butt';
  for (const [a, b] of [[[W / 2 - 40, -PAD - MAT], [W / 2 + 26, H + PAD + MAT]], [[-PAD - MAT, H / 2 + 40], [W + PAD + MAT, H / 2 - 16]]]) {
    g.strokeStyle = 'rgba(70,44,18,.1)'; g.lineWidth = 5; g.beginPath(); g.moveTo(a[0], a[1]); g.lineTo(b[0], b[1]); g.stroke();
    g.strokeStyle = 'rgba(255,244,214,.1)'; g.lineWidth = 3; g.beginPath(); g.moveTo(a[0] + 5, a[1] + 3); g.lineTo(b[0] + 5, b[1] + 3); g.stroke();
  }
  g.strokeStyle = 'rgba(94,58,24,.2)'; g.lineWidth = 9; g.beginPath(); g.arc(760, 130, 92, 0.5, 5.9); g.stroke();
  g.strokeStyle = 'rgba(94,58,24,.1)'; g.lineWidth = 4; g.beginPath(); g.arc(760, 130, 104, 2, 7); g.stroke();
  g.restore();
}

// ---------------------------------------------------------------------------------------------------------- ДОШКА
function paintBoard(P) {
  const { g, geo, data, s, hi } = P, r = rng(333);
  const coast = coastPathOf(geo);
  // поле картону
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.fillStyle = '#fbf0d4'; g.fillRect(0, 0, g.canvas.width, g.canvas.height);
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // сусідні землі: мʼяка сіро-шавлієва
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.save(); g.setTransform(s, 0, 0, s, OFF * s, OFF * s); clipRect(g); g.setTransform(1, 0, 0, 1, 0, 0);
  g.drawImage(layer(hi ? 800 : 500, (x, y, o) => { const a = fbm(x / 420 + 2, y / 420, 4), b = fbm(x / 90, y / 90 + 7, 2); const v = (a - 0.5) * 34 + (b - 0.5) * 10; o[0] = 214 + v; o[1] = 220 + v; o[2] = 190 + v * 1.2; }), 0, 0, g.canvas.width, g.canvas.height);
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // друковані горби сусідніх земель (хвилясті дуги)
  g.strokeStyle = 'rgba(120,136,96,.46)'; g.lineWidth = 3; g.lineCap = 'round';
  for (let i = 0, pl = []; i < 1200 && pl.length < 90; i++) { const x = r() * W, y = r() * H; if (geo.maskAt(x, y) !== 0 || geo.bil(geo.sU, x, y) > -40 && geo.bil(geo.sU, x, y) < 40) continue; let ok = true; for (const q of pl) if (Math.hypot(q[0] - x, q[1] - y) < 120) { ok = false; break; } if (!ok) continue; pl.push([x, y]); const w = 22 + r() * 16; g.beginPath(); g.moveTo(x - w, y); g.quadraticCurveTo(x - w * 0.4, y - w * 0.8, x, y); g.quadraticCurveTo(x + w * 0.4, y - w * 0.5, x + w * 0.9, y); g.stroke(); }
  g.restore();
  g.setTransform(1, 0, 0, 1, 0, 0);
  const seaDeep = hex('#2f93c4'), seaMid = hex('#3fafcf'), seaShal = hex('#6ccfd8');
  // море: глибина за відстанню до берега
  const seaL = layer(hi ? 1000 : 640, (x, y, o) => {
    const d = geo.bil(geo.sS, x, y), n = fbm(x / 340, y / 340 + 3, 3);
    const t = smooth(0, 420, d + (n - 0.5) * 180); const c = mixc(seaMid, seaDeep, t); o[0] = c[0]; o[1] = c[1]; o[2] = c[2];
  });
  g.save(); g.setTransform(s, 0, 0, s, OFF * s, OFF * s); g.beginPath(); g.rect(-PAD, -PAD, W + 2 * PAD, H + 2 * PAD); g.clip(); g.clip(geo.seaPath); g.setTransform(1, 0, 0, 1, 0, 0); g.drawImage(seaL, 0, 0, g.canvas.width, g.canvas.height); g.restore();
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // мілина: два друковані пояси
  const seaEven = new Path2D(geo.seaPath); seaEven.addPath(geo.land);
  g.save(); clipRect(g); g.clip(geo.seaPath); softStroke(g, coast, 'rgba(120,224,222,.34)', 120, 1, 1); softStroke(g, coast, 'rgba(160,236,226,.42)', 52, 1, 1); g.restore();
  // хвилі-галочки
  g.save(); clipRect(g); g.clip(geo.seaPath); g.strokeStyle = 'rgba(255,255,255,.62)'; g.lineWidth = 4.2; g.lineCap = 'round';
  const pl = [];
  for (let i = 0; i < 1900 && pl.length < 110; i++) {
    const x = r() * W * 1.05, y = 1800 + r() * (H - 1700), d = geo.bil(geo.sS, x, y);
    if (d < 34) continue; let ok = true; for (const q of pl) if (Math.hypot(q[0] - x, (q[1] - y) * 1.6) < 150) { ok = false; break; } if (!ok) continue; pl.push([x, y]);
    const w = 19 + r() * 9; g.beginPath(); for (let k = 0; k < 2; k++) { const X = x + k * w * 1.9; g.moveTo(X - w, y); g.quadraticCurveTo(X - w / 2, y - w * 0.75, X, y); g.quadraticCurveTo(X + w / 2, y - w * 0.75, X + w, y); } g.stroke();
  }
  g.restore();
  slabShadow(g, geo, 'rgba(20,50,70,.07)');
  // Україна: природні пояси з хвилястими межами
  const pol = hex('#7cbf5f'), fst = hex('#a9d467'), stp = hex('#efd979'), stp2 = hex('#f6e596'), crm = hex('#d7d96f');
  const lv = [[0.055, hex('#8fbe5a')], [0.095, hex('#a4b35a')], [0.14, hex('#c8ad6a')], [0.185, hex('#e2ccaa')], [0.225, hex('#fbf4e6')]];
  const Rf = geo.R;
  const landL = layer(hi ? 1500 : 800, (x, y, o) => {
    if (geo.maskAt(x, y) === 2) { o[3] = 0; return; }
    const wob = (fbm(x / 240 + 3, y / 240 + 9, 3) - 0.5) * 300;
    const yy = y + wob - Math.max(0, x - 1400) * 0.13 - Math.max(0, 1400 - x) * 0.95;
    const a = smooth(632, 648, yy), b = smooth(1250, 1266, yy), c = smooth(1640, 1656, yy);
    let col = mixc(mixc(mixc(pol, fst, a), stp, b), stp2, c);
    const rr = geo.bil(Rf, x, y);
    for (const [t, cc] of lv) { const k = smooth(t - 0.002, t + 0.002, rr); if (k > 0) col = mixc(col, cc, k); }
    const v = 1 + (fbm(x / 150 + 40, y / 150, 3) - 0.5) * 0.17 + (fbm(x / 26, y / 26 + 8, 2) - 0.5) * 0.07;
    o[0] = Math.min(255, col[0] * v); o[1] = Math.min(255, col[1] * v); o[2] = Math.min(255, col[2] * v);
  });
  g.save(); g.clip(geo.land, 'evenodd'); g.setTransform(1, 0, 0, 1, 0, 0); g.drawImage(landL, 0, 0, g.canvas.width, g.canvas.height); g.restore();
  // м'яка отмивка рельєфу
  const S = hillshade(geo);
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.save(); g.globalCompositeOperation = 'multiply';
  g.drawImage(layer(hi ? 1100 : 760, (x, y, o) => {
    const i = geo.pxOf(x, y), m = geo.mask[i];
    if (m !== 1) { o[0] = o[1] = o[2] = 255; return; }
    const d = Math.min(1, Math.max(0, -S[i] * 0.9));
    o[0] = 255 - d * 130; o[1] = 255 - d * 95; o[2] = 255 - d * 65;
  }), 0, 0, g.canvas.width, g.canvas.height);
  g.globalCompositeOperation = 'screen';
  g.drawImage(layer(hi ? 1100 : 760, (x, y, o) => {
    const i = geo.pxOf(x, y), m = geo.mask[i];
    if (m !== 1) { o[0] = o[1] = o[2] = 0; return; }
    const d = Math.min(1, Math.max(0, S[i] * 0.6)); o[0] = d * 120; o[1] = d * 110; o[2] = d * 80;
  }), 0, 0, g.canvas.width, g.canvas.height);
  g.restore();
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // поля: смугасті плями в степу, ніби вирізані
  const fields = [];
  for (let i = 0; i < 800 && fields.length < (hi ? 46 : 26); i++) {
    const x = r() * W, y = 720 + r() * 1400; if (geo.maskAt(x, y) !== 1 || !freeSpot(geo, x, y, 96) || geo.bil(geo.sU, x, y) < 70) continue;
    let ok = true; for (const q of fields) if (Math.hypot(q[0] - x, q[1] - y) < 170) { ok = false; break; } if (!ok) continue;
    let cty = true; for (const c of data.cities) if (Math.hypot(c.x - x, c.y - y) < c.r + 70) { cty = false; break; } if (!cty) continue;
    fields.push([x, y]);
    const R0 = 40 + r() * 28, ang = r() * 3.14, pal = y > 1250 ? ['#dfa92e', '#f4cf4e'] : ['#b9c844', '#dbe05a'], n = 9;
    g.save(); g.translate(x, y); g.rotate(ang); g.scale(1, 0.72);
    const q = []; for (let k = 0; k < n; k++) { const a = k / n * 6.283, rr = R0 * (0.82 + 0.34 * noise(x * 0.3 + k * 1.7, y * 0.3)); q.push([Math.cos(a) * rr, Math.sin(a) * rr]); }
    g.beginPath(); for (let k = 0; k < n; k++) { const A = q[k], B = q[(k + 1) % n], mx = (A[0] + B[0]) / 2, my = (A[1] + B[1]) / 2; if (k === 0) g.moveTo(mx, my); else g.quadraticCurveTo(A[0], A[1], mx, my); } g.quadraticCurveTo(q[0][0], q[0][1], (q[0][0] + q[1][0]) / 2, (q[0][1] + q[1][1]) / 2); g.closePath(); g.fillStyle = pal[0]; g.fill();
    g.strokeStyle = 'rgba(120,90,30,.42)'; g.lineWidth = 2.4; g.stroke();
    g.clip(); g.strokeStyle = pal[1]; g.lineWidth = 5.4; for (let k = -R0; k < R0; k += 10.6) { g.beginPath(); g.moveTo(-R0 * 1.3, k); g.bezierCurveTo(-R0 * 0.4, k + 7, R0 * 0.4, k - 5, R0 * 1.3, k + 2); g.stroke(); }
    g.restore();
  }
  // трава: дрібні кущики-галочки пером художника
  g.lineCap = 'round';
  for (let i = 0, N = hi ? 3600 : 1300; i < N; i++) {
    const x = r() * W, y = r() * H; if (geo.maskAt(x, y) !== 1 || !freeSpot(geo, x, y, 38) || geo.bil(geo.sU, x, y) < 12 || noise(x / 130, y / 130) < 0.42) continue;
    const dark = y < 1200 ? 'rgba(46,110,50,.5)' : 'rgba(150,120,36,.48)';
    g.strokeStyle = dark; g.lineWidth = 2.2; const w = 4 + r() * 3.6;
    g.beginPath(); g.moveTo(x - w, y); g.lineTo(x - w * 1.25, y - w); g.moveTo(x, y); g.lineTo(x, y - w * 1.45); g.moveTo(x + w, y); g.lineTo(x + w * 1.25, y - w); g.stroke();
  }
  // межі областей: білі риски
  g.save(); g.clip(geo.land, 'evenodd');
  g.setLineDash([16, 12]); g.lineCap = 'butt'; g.strokeStyle = 'rgba(255,255,255,.72)'; g.lineWidth = 3.2; g.stroke(geo.inner); g.setLineDash([]);
  g.restore();
  // річки
  drawRiversUnder(g, geo, data.routes, 'rgba(32,110,148,.85)', '#62c3e0');
  // кордон країни: біла смуга + теплий контур
  routeUnderlay(g, data.routes, data.cities, 'rgba(44,72,40,.14)', 62);
  routeUnderlay(g, data.routes, data.cities, 'rgba(36,58,30,.2)', 50);
  cityShadow(g, data.cities, 'rgba(30,44,24,.32)');
  g.lineJoin = 'round'; g.lineCap = 'round';
  g.strokeStyle = 'rgba(36,76,70,.55)'; g.lineWidth = 11; g.stroke(geo.outline);
  g.strokeStyle = '#fffdf2'; g.lineWidth = 6; g.stroke(geo.outline);
  // чужий берег
  g.save(); clipRect(g); g.strokeStyle = 'rgba(255,255,255,.7)'; g.lineWidth = 3.4; g.stroke(geo.foreignPath); g.restore();
  // написи
  const fnt = (w, px) => `${w} ${px}px Onest,system-ui,sans-serif`;
  const arc = (cx, cy, Rr, a0, a1, n = 30) => { const o = []; for (let i = 0; i <= n; i++) { const a = a0 + (a1 - a0) * i / n; o.push([cx + Math.cos(a) * Rr, cy + Math.sin(a) * Rr]); } return o; };
  textAlong(g, 'ЧОРНЕ МОРЕ', arc(2050, 3760, 1320, -1.92, -1.2), 0.5, fnt(800, 66), 'rgba(255,255,255,.9)', null, 0.26);
  textAlong(g, 'АЗОВСЬКЕ МОРЕ', arc(3180, 3000, 980, -1.86, -1.28), 0.5, fnt(800, 32), 'rgba(255,255,255,.88)', null, 0.14);
  for (const [name, at] of [['Дніпро', 0.5], ['Дністер', 0.58], ['Південний Буг', 0.5], ['Десна', 0.5], ['Сіверський Донець', 0.5]]) {
    const best = geo.rivers.filter((q) => q.name === name).sort((a, b) => b.pts.length - a.pts.length)[0];
    if (best) textAlong(g, name, best.pts.map((q) => [q[0], q[1] - 24]), at, fnt(700, 22), '#1b6f94', 'rgba(255,255,255,.6)', 0.08);
  }
  // сусіди — дрібним друком, порожній кут не лишаємо голим
  for (const [t, x, y, a, px] of NEIGHBOURS) spaced(g, t, x, y, a, fnt(700, px * 0.82), 'rgba(70,90,70,.72)', 0.2);
  compass(g, 3380, 2560, 100, '#1b5f88', '#ffffff', '#e34a3c');
  chumakSketch(g, 3640, 300, 1.15, 'rgba(60,70,40,.9)', 'rgba(247,238,208,.95)');
  // поле: чумацький орнамент (вишивка, рушник)
  rushnyk(g);
  // легкий папір
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.save(); g.globalCompositeOperation = 'multiply'; g.globalAlpha = 0.14; g.fillStyle = g.createPattern(grainTile(256, 9), 'repeat'); g.fillRect(0, 0, g.canvas.width, g.canvas.height); g.restore();
}
function rushnyk(g) {
  const a = -PAD - 56, b = -PAD - 150;
  // облямівка карти
  g.lineWidth = 9; g.strokeStyle = '#5a3a1e'; g.strokeRect(-PAD - 10, -PAD - 10, W + 2 * PAD + 20, H + 2 * PAD + 20);
  const dr = (x, y, ang) => {
    g.save(); g.translate(x, y); g.rotate(ang);
    g.fillStyle = '#c8362b'; g.beginPath(); g.moveTo(0, -44); g.quadraticCurveTo(10, -10, 44, 0); g.quadraticCurveTo(10, 10, 0, 44); g.quadraticCurveTo(-10, 10, -44, 0); g.quadraticCurveTo(-10, -10, 0, -44); g.fill();
    g.fillStyle = '#23303a'; g.beginPath(); g.moveTo(0, -18); g.lineTo(18, 0); g.lineTo(0, 18); g.lineTo(-18, 0); g.closePath(); g.fill();
    g.fillStyle = '#fbf0d4'; g.beginPath(); g.arc(0, 0, 6, 0, 6.3); g.fill();
    g.fillStyle = '#2a8a4a'; for (const [dx, dy] of [[58, 0], [-58, 0]]) { g.beginPath(); g.ellipse(dx, dy, 13, 7, 0, 0, 6.3); g.fill(); }
    g.restore();
  };
  const L = W + 2 * PAD, H2 = H + 2 * PAD, st = 200;
  for (let x = -PAD + st / 2; x < W + PAD; x += st) { dr(x, -PAD - 92, 0); dr(x, H + PAD + 92, 0); }
  for (let y = -PAD + st / 2; y < H + PAD; y += st) { dr(-PAD - 92, y, Math.PI / 2); dr(W + PAD + 92, y, Math.PI / 2); }
  g.strokeStyle = '#c8362b'; g.lineWidth = 6; g.strokeRect(-PAD - 34, -PAD - 34, W + 2 * PAD + 68, H + 2 * PAD + 68);
  g.strokeRect(-PAD - 150, -PAD - 150, W + 2 * PAD + 300, H + 2 * PAD + 300);
}

// ---------------------------------------------------------------------------------------------------------- ПЕТРИКІВКА
// За макетом: чорний лак, темні області з візерунком В ОДИН ТОН, вишитий шов уздовж кордону; колір — лише в декорі в порожнечах
// і на шляхах. Так барвисті плашки не сперечаються з тлом.
const FIELD = ['#3a2362', '#14483d', '#1c3d72', '#4e2b14', '#5a2238', '#3f3a14', '#114a50'];
function adjacency(geo) {
  const owners = new Map(); geo.regions.forEach((g, i) => g.arcIds.forEach((a) => (owners.get(a) || owners.set(a, []).get(a)).push(i)));
  const nb = geo.regions.map(() => new Set());
  for (const list of owners.values()) if (list.length > 1) for (const a of list) for (const b of list) if (a !== b) nb[a].add(b);
  return nb;
}
function comma(g, x, y, ang, L, w, col) {
  const dx = Math.cos(ang), dy = Math.sin(ang), nx = -dy, ny = dx;
  g.fillStyle = col; g.beginPath(); g.moveTo(x, y);
  g.bezierCurveTo(x + nx * w + dx * L * 0.3, y + ny * w + dy * L * 0.3, x + nx * w * 0.7 + dx * L * 0.8, y + ny * w * 0.7 + dy * L * 0.8, x + dx * L + nx * w * 0.2, y + dy * L + ny * w * 0.2);
  g.bezierCurveTo(x + dx * L * 0.7 - nx * w * 0.2, y + dy * L * 0.7 - ny * w * 0.2, x + dx * L * 0.2 - nx * w * 0.6, y + dy * L * 0.2 - ny * w * 0.6, x, y); g.fill();
}
/** Квітка петриківки: вінець мазків-«ком», центр-гребінець. */
function petFlower(g, x, y, sc, c1, c2, gold, rot = 0) {
  const n = 7;
  for (let i = 0; i < n; i++) { const a = rot + i / n * 6.283; comma(g, x + Math.cos(a) * 4 * sc, y + Math.sin(a) * 4 * sc, a, 34 * sc, 14 * sc, c1); comma(g, x + Math.cos(a) * 7 * sc, y + Math.sin(a) * 7 * sc, a + 0.1, 20 * sc, 7 * sc, c2); }
  g.fillStyle = gold; g.beginPath(); g.arc(x, y, 7.5 * sc, 0, 6.3); g.fill();
  for (let i = 0; i < 10; i++) { const a = rot + i / 10 * 6.283; g.beginPath(); g.arc(x + Math.cos(a) * 12 * sc, y + Math.sin(a) * 12 * sc, 1.9 * sc, 0, 6.3); g.fill(); }
}
/** Вигнута гілка з листям-мазками і ягодами на кінці. */
function petBranch(g, x, y, ang, L, sc, leaf, stem, berry) {
  const n = 7; let px = x, py = y, a = ang;
  for (let i = 0; i < n; i++) {
    const st = L / n, nx = px + Math.cos(a) * st, ny = py + Math.sin(a) * st;
    g.strokeStyle = stem; g.lineWidth = 2.8 * sc; g.lineCap = 'round'; g.beginPath(); g.moveTo(px, py); g.lineTo(nx, ny); g.stroke();
    comma(g, nx, ny, a + 0.9, 26 * sc, 8 * sc, leaf); comma(g, nx, ny, a - 0.9, 26 * sc, -8 * sc, leaf);
    px = nx; py = ny; a += 0.15;
  }
  g.fillStyle = berry; for (const [dx, dy] of [[0, 0], [9, 5], [-3, 9]]) { g.beginPath(); g.arc(px + dx * sc, py + dy * sc, 5.5 * sc, 0, 6.3); g.fill(); }
}
/** Соняшник: два вінці пелюсток і темний кошик у шашечку-спіраль. */
function sunflower(g, x, y, R, r) {
  g.save(); g.translate(x, y);
  for (const [n, L, W_, col, ph] of [[26, 1, 0.17, '#e8920e', 0.12], [26, 0.9, 0.15, '#fbc924', 0]]) {
    for (let i = 0; i < n; i++) {
      const a = ph + i / n * 6.283, l = R * L * (0.94 + r() * 0.1), w = R * W_;
      g.save(); g.rotate(a); g.fillStyle = col; g.beginPath(); g.moveTo(R * 0.34, 0); g.quadraticCurveTo(R * 0.62, -w, l, 0); g.quadraticCurveTo(R * 0.62, w, R * 0.34, 0); g.fill();
      g.strokeStyle = 'rgba(120,60,0,.5)'; g.lineWidth = R * 0.012; g.beginPath(); g.moveTo(R * 0.42, 0); g.lineTo(l * 0.9, 0); g.stroke(); g.restore();
    }
  }
  g.fillStyle = '#1b0f06'; g.beginPath(); g.arc(0, 0, R * 0.4, 0, 6.3); g.fill();
  g.fillStyle = '#7a4a12';
  for (let i = 1; i < 160; i++) { const a = i * 2.39996, d = R * 0.385 * Math.sqrt(i / 160); g.beginPath(); g.arc(Math.cos(a) * d, Math.sin(a) * d, R * 0.015, 0, 6.3); g.fill(); }
  g.restore();
}
/** Калина: гілка з гронами. */
function kalyna(g, x, y, ang, sc) {
  petBranch(g, x, y, ang, 150 * sc, sc * 1.05, '#178a55', '#2ba56a', '#d9301f');
  for (let i = 0; i < 16; i++) { const a = ang + 0.5 + i * 2.4, d = 13 * sc * Math.sqrt(i); g.fillStyle = i % 3 ? '#d9301f' : '#ef5a3a'; g.beginPath(); g.arc(x + Math.cos(ang) * 20 * sc + Math.cos(a) * d, y + Math.sin(ang) * 20 * sc + Math.sin(a) * d, 8.5 * sc, 0, 6.3); g.fill(); }
}
function paintPetryk(P) {
  const { g, geo, data, s, hi } = P, r = rng(722);
  const gold = '#f6bf26', ground = '#08070f';
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.fillStyle = ground; g.fillRect(0, 0, g.canvas.width, g.canvas.height);
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // море — нічна синь, луска хвиль у той самий тон, рядки зсунуті й тремтять: не сітка
  g.save(); g.beginPath(); g.rect(-PAD, -PAD, W + 2 * PAD, H + 2 * PAD); g.clip(); g.clip(geo.seaPath);
  g.fillStyle = '#121a58'; g.fillRect(-PAD, 1200, W + 2 * PAD, H);
  g.strokeStyle = 'rgba(92,116,226,.34)'; g.lineWidth = 3;
  for (let y = 1500, row = 0; y < H + 100; y += 44 + r() * 5, row++) {
    const R0 = 50 + r() * 5;
    for (let x = -100 + (row % 2) * R0 + r() * 10; x < W + 200; x += R0 * 2 + (r() - 0.5) * 3) {
      const yy = y + Math.sin(x * 0.006 + row) * 11;
      if (geo.bil(geo.sS, x, yy) < 26) continue;
      g.beginPath(); g.arc(x, yy, R0, Math.PI * 1.08, Math.PI * 1.92); g.stroke();
    }
  }
  g.restore();
  // області: темні, сусіди різні; візерунок — трохи світліший за своє тло, той самий відтінок
  const nb = adjacency(geo), col = new Array(geo.regions.length).fill(-1);
  const order = geo.regions.map((_, i) => i).sort((a, b) => nb[b].size - nb[a].size);
  for (const i of order) { const used = new Set(); for (const j of nb[i]) if (col[j] >= 0) used.add(col[j]); const start = i * 3 % FIELD.length; for (let k = 0; k < FIELD.length; k++) { const c = (start + k) % FIELD.length; if (!used.has(c)) { col[i] = c; break; } } }
  geo.regions.forEach((rg, i) => {
    const fc = hex(FIELD[col[i]]), tone1 = rgba(mixc(fc, [255, 244, 220], 0.17), 1), tone2 = rgba(mixc(fc, [255, 244, 220], 0.1), 1), tone3 = rgba(mixc(fc, [255, 244, 220], 0.27), 1);
    g.fillStyle = FIELD[col[i]]; g.fill(rg.path, 'evenodd');
    g.save(); g.clip(rg.path, 'evenodd');
    const bb = bbox(rg.rings), placed = [], want = hi ? 16 : 9;
    for (let k = 0; k < 900 && placed.length < want; k++) {
      const x = bb[0] + r() * (bb[2] - bb[0]), y = bb[1] + r() * (bb[3] - bb[1]);
      if (geo.bil(geo.sU, x, y) < -20) continue;
      if (placed.some((q) => Math.hypot(q[0] - x, q[1] - y) < 118)) continue;
      placed.push([x, y]);
      const sc = 0.82 + r() * 0.5, a = r() * 6.28;
      petBranch(g, x, y, a, 86 * sc, sc * 0.95, tone2, tone1, tone1);
      petBranch(g, x, y, a + 2.5, 72 * sc, sc * 0.85, tone2, tone1, tone1);
      petFlower(g, x, y, sc, tone1, tone2, tone3, a);
    }
    g.restore();
  });
  // рельєф — лише натяком, щоб Карпати читались
  const S = hillshade(geo);
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.save(); g.globalCompositeOperation = 'multiply';
  g.drawImage(layer(hi ? 1100 : 760, (x, y, o) => {
    const i = geo.pxOf(x, y), m = geo.mask[i];
    if (m !== 1) { o[0] = o[1] = o[2] = 255; return; }
    const d = Math.min(1, Math.max(0, -S[i] * 0.95)); o[0] = o[1] = o[2] = 255 - d * 130;
  }), 0, 0, g.canvas.width, g.canvas.height);
  g.globalCompositeOperation = 'screen';
  g.drawImage(layer(hi ? 1100 : 760, (x, y, o) => {
    const i = geo.pxOf(x, y), m = geo.mask[i];
    if (m !== 1) { o[0] = o[1] = o[2] = 0; return; }
    const d = Math.min(1, Math.max(0, S[i] * 0.5)); o[0] = d * 90; o[1] = d * 80; o[2] = d * 70;
  }), 0, 0, g.canvas.width, g.canvas.height);
  g.restore();
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // межі областей — чорний лак між полями
  g.lineJoin = 'round'; g.lineCap = 'round';
  g.save(); g.clip(geo.land, 'evenodd');
  g.strokeStyle = ground; g.lineWidth = 6; g.stroke(geo.inner);
  g.restore();
  // річки
  drawRiversUnder(g, geo, data.routes, 'rgba(6,6,14,.95)', '#58d0f0');
  // шляхи: темна доріжка
  routeUnderlay(g, data.routes, data.cities, 'rgba(4,3,8,.3)', 62);
  routeUnderlay(g, data.routes, data.cities, 'rgba(4,3,8,.42)', 50);
  cityShadow(g, data.cities, 'rgba(3,2,6,.6)');
  // кордон — вишитий шов: чорний кант, золоті й червоні стібки навперемін
  g.strokeStyle = ground; g.lineWidth = 16; g.stroke(geo.outline);
  g.lineCap = 'round';
  g.setLineDash([0.1, 37]); g.lineDashOffset = 0; g.strokeStyle = '#ef7d1a'; g.lineWidth = 12; g.stroke(geo.outline);
  g.lineDashOffset = 18.5; g.strokeStyle = '#d9301f'; g.lineWidth = 10; g.stroke(geo.outline);
  g.setLineDash([]); g.lineDashOffset = 0;
  g.save(); clipRect(g); g.setLineDash([0.1, 24]); g.strokeStyle = 'rgba(246,191,38,.6)'; g.lineWidth = 6; g.stroke(geo.foreignPath); g.setLineDash([]); g.restore();
  // написи
  const fnt = (w, px, it = '') => `${it} ${w} ${px}px Onest,system-ui,sans-serif`;
  const arc = (cx, cy, Rr, a0, a1, n = 30) => { const o = []; for (let i = 0; i <= n; i++) { const a = a0 + (a1 - a0) * i / n; o.push([cx + Math.cos(a) * Rr, cy + Math.sin(a) * Rr]); } return o; };
  textAlong(g, 'ЧОРНЕ МОРЕ', arc(2050, 3760, 1320, -1.92, -1.2), 0.5, fnt(800, 62), '#6d90ee', null, 0.4);
  textAlong(g, 'АЗОВСЬКЕ МОРЕ', arc(3180, 3000, 980, -1.86, -1.28), 0.5, fnt(800, 30), '#5f83e6', null, 0.26);
  for (const [name, at] of [['Дніпро', 0.5], ['Дністер', 0.58], ['Південний Буг', 0.5], ['Десна', 0.5], ['Сіверський Донець', 0.5]]) {
    const best = geo.rivers.filter((q) => q.name === name).sort((a, b) => b.pts.length - a.pts.length)[0];
    if (best) textAlong(g, name, best.pts.map((q) => [q[0], q[1] - 22]), at, fnt(600, 21, 'italic'), '#8fe2f7', 'rgba(6,6,14,.7)', 0.1);
  }
  for (const [t, x, y, a, px] of NEIGHBOURS) spaced(g, t, x, y, a, fnt(600, px * 0.74, 'italic'), 'rgba(246,191,38,.5)', 0.22);
  // ДЕКОР — лише в порожнечах поза шляхами: тут і весь колір
  g.save(); clipRect(g);
  { const p2 = new Path2D(); p2.rect(-OFF, -OFF, CW, CH); p2.addPath(geo.land); g.clip(p2, 'evenodd'); }
  // соняшник на стеблі — північний схід
  g.strokeStyle = '#178a55'; g.lineWidth = 15; g.lineCap = 'round'; g.beginPath(); g.moveTo(3640, 260); g.bezierCurveTo(3790, 420, 3760, 560, 3890, 720); g.stroke();
  comma(g, 3780, 440, 0.2, 130, 44, '#178a55'); comma(g, 3800, 560, 2.8, 120, -40, '#1fa868');
  sunflower(g, 3620, 240, 205, r);
  kalyna(g, 3350, 210, 2.7, 0.92);
  // букет у порожньому південно-західному куті
  const stem = '#2ba56a';
  for (let i = 0; i < 6; i++) petBranch(g, 560, 2360, -2.7 + i * 0.48, (300 - 22 * Math.abs(i - 2.5)), 1.5, i % 2 ? '#178a55' : '#2474e8', stem, i % 2 ? '#fbc924' : '#d9301f');
  petFlower(g, 560, 2360, 3.0, '#d9301f', '#f29a7a', '#fbc924', 0.3);
  petFlower(g, 300, 2190, 1.7, '#ef7d1a', '#fbd99a', '#d9301f', 0.8);
  petFlower(g, 860, 2230, 1.8, '#2474e8', '#a8d0f5', '#fbc924', 0.4);
  petFlower(g, 780, 2560, 1.5, '#f46fab', '#fbd0e0', '#fbc924', 1.2);
  petFlower(g, 300, 2560, 1.45, '#fbc924', '#fff0b8', '#d9301f', 0.2);
  kalyna(g, 1060, 2500, -0.7, 1.05);
  // квітка в морі внизу, букетик на сході й на заході
  petFlower(g, 1500, 2640, 1.6, '#d9301f', '#f29a7a', '#fbc924', 0.5);
  petBranch(g, 1500, 2640, -0.5, 180, 1.2, '#178a55', stem, '#fbc924');
  petBranch(g, 3740, 2520, 3.5, 220, 1.3, '#178a55', stem, '#d9301f');
  petFlower(g, 3740, 2520, 1.8, '#ef7d1a', '#fbd99a', '#d9301f', 0.2);
  petBranch(g, 40, 300, 0.6, 210, 1.1, '#178a55', stem, '#d9301f');
  petFlower(g, 40, 300, 1.4, '#f46fab', '#fbd0e0', '#fbc924', 0.9);
  g.restore();
  // поле: фриз
  const stripe = (d, w, c) => { g.strokeStyle = c; g.lineWidth = w; g.strokeRect(-PAD - d, -PAD - d, W + 2 * PAD + 2 * d, H + 2 * PAD + 2 * d); };
  stripe(12, 8, gold); stripe(28, 2.5, gold); stripe(172, 7, gold); stripe(160, 2.5, gold);
  const leaf = ['#178a55', '#2474e8'], fl = ['#d9301f', '#ef7d1a'];
  const motif = (x, y, a, i) => {
    petBranch(g, x, y, a, 62, 0.82, leaf[i % 2], gold, '#fbc924');
    petBranch(g, x, y, a + Math.PI, 62, 0.82, leaf[(i + 1) % 2], gold, '#fbc924');
    petFlower(g, x, y, 0.9, fl[i % 2], '#f0b47a', '#fbc924', i);
  };
  let i = 0;
  for (let x = -PAD + 100; x < W + PAD; x += 220) { motif(x, -PAD - 94, 0, i); motif(x, H + PAD + 94, 0, i + 1); i++; }
  for (let y = -PAD + 100; y < H + PAD; y += 220) { motif(-PAD - 94, y, Math.PI / 2, i); motif(W + PAD + 94, y, Math.PI / 2, i + 1); i++; }
}

function bbox(rings) { let a = 1e9, b = 1e9, c = -1e9, d = -1e9; for (const r of rings) for (let k = 0; k < r.length; k += 2) { a = Math.min(a, r[k]); b = Math.min(b, r[k + 1]); c = Math.max(c, r[k]); d = Math.max(d, r[k + 1]); } return [a, b, c, d]; }

// ---------------------------------------------------------------------------------------------------------- атлас плашок: колір + фактура + значок
/** Рядки атласу: вісім вантажів за CARGO, сірий, останній рядок — суцільний білий (край плашки). */
export const ATLAS = { cellW: 400, cellH: 100, order: ['salt', 'fish', 'grain', 'tar', 'honey', 'wool', 'hides', 'linen', 'grey'] };
export function paintAtlas(look, hi) {
  const k = hi ? 1 : 0.64, cw = Math.round(ATLAS.cellW * k), ch = Math.round(ATLAS.cellH * k), n = ATLAS.order.length;
  const c = newCanvas(cw, ch * (n + 1)), g = c.getContext('2d');
  g.fillStyle = '#fff'; g.fillRect(0, ch * n, cw, ch);
  ATLAS.order.forEach((name, i) => {
    g.save(); g.setTransform(k, 0, 0, k, 0, i * ch);
    const base = look.routes[name], rgb = hex(base), lum = (0.3 * rgb[0] + 0.59 * rgb[1] + 0.11 * rgb[2]) / 255;
    const ink = (look.routeInk && look.routeInk[name]) || (lum > 0.56 ? '#201812' : '#ffffff');
    const inkRgb = hex(ink), inkA = (a) => rgba(inkRgb, a);
    g.fillStyle = base; g.fillRect(0, 0, ATLAS.cellW, ATLAS.cellH);
    // фактура: своя в кожного вантажу (видно й у сірому, і дальтонікам)
    g.save(); g.strokeStyle = inkA(0.26); g.fillStyle = inkA(0.26); g.lineWidth = 5;
    const X = ATLAS.cellW, Y = ATLAS.cellH;
    if (name === 'salt') { for (let x = -Y; x < X + Y; x += 19) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x + Y, Y); g.stroke(); } }
    else if (name === 'fish') { for (let y = 8; y < Y; y += 21) { g.beginPath(); for (let x = 0; x <= X; x += 6) { const yy = y + Math.sin(x * 0.11) * 4; x ? g.lineTo(x, yy) : g.moveTo(x, yy); } g.stroke(); } }
    else if (name === 'grain') { for (let x = 6; x < X; x += 16) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x, Y); g.stroke(); } }
    else if (name === 'tar') { for (let y = 10, r = 0; y < Y; y += 20, r++) for (let x = 6 + (r % 2) * 10; x < X; x += 20) { g.beginPath(); g.arc(x, y, 3.4, 0, 6.3); g.fill(); } }
    else if (name === 'honey') { g.lineWidth = 3.4; const S = 15; for (let r = 0, y = 0; y < Y + S; y += S * 1.5, r++) for (let x = (r % 2) * S * 0.87; x < X + S; x += S * 1.74) { g.beginPath(); for (let a = 0; a < 6; a++) { const an = a / 6 * 6.283 + Math.PI / 6, px = x + Math.cos(an) * S * 0.9, py = y + Math.sin(an) * S * 0.9; a ? g.lineTo(px, py) : g.moveTo(px, py); } g.closePath(); g.stroke(); } }
    else if (name === 'wool') { g.lineWidth = 3.6; for (let y = 12, r = 0; y < Y; y += 22, r++) for (let x = 8 + (r % 2) * 13; x < X; x += 26) { g.beginPath(); g.arc(x, y, 6.5, 0.6, 5); g.stroke(); } }
    else if (name === 'hides') { for (let x = -20; x < X + 20; x += 23) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x + 16, Y / 2); g.lineTo(x, Y); g.stroke(); } }
    else if (name === 'linen') { g.lineWidth = 3.4; for (let x = -Y; x < X + Y; x += 22) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x + Y, Y); g.moveTo(x + Y, 0); g.lineTo(x, Y); g.stroke(); } }
    else { g.lineWidth = 2.6; for (let x = -Y; x < X + Y; x += 30) { g.beginPath(); g.moveTo(x + Y, 0); g.lineTo(x, Y); g.stroke(); } }
    g.restore();
    g.restore();
  });
  return c;
}
/** Значки окремо від фактури: завжди стоять рівно (не крутяться за шляхом) і лежать поверх плашки власним квадратом.
 *  Сітка 3 × 3 за ATLAS.order; у кожній клітинці — кружок кольору вантажу й простий силует. */
export function paintIcons(look, hi) {
  const cell = hi ? 128 : 96, c = newCanvas(cell * 3, cell * 3), g = c.getContext('2d');
  ATLAS.order.forEach((name, i) => {
    const x = (i % 3 + 0.5) * cell, y = ((i / 3 | 0) + 0.5) * cell;
    const base = look.routes[name], rgb = hex(base), lum = (0.3 * rgb[0] + 0.59 * rgb[1] + 0.11 * rgb[2]) / 255;
    g.fillStyle = rgba(rgb, 0.96); g.beginPath(); g.arc(x, y, cell * 0.49, 0, 6.3); g.fill();
    goodsIcon(g, name, x, y, cell * 0.39, (look.routeInk && look.routeInk[name]) || (lum > 0.56 ? '#201812' : '#ffffff'));
  });
  return c;
}
/** Фігури місць (SEAT_SHAPE): кружок кольору місця, біла фігура. Рядок із п'яти клітинок. */
export function paintSeatBadges(colors) {
  const cell = 96, c = newCanvas(cell * 5, cell), g = c.getContext('2d');
  colors.forEach((col, i) => {
    const x = (i + 0.5) * cell, y = cell / 2, R = cell * 0.44;
    g.fillStyle = '#16100a'; g.beginPath(); g.arc(x, y, R + 3, 0, 6.3); g.fill();
    g.fillStyle = col; g.beginPath(); g.arc(x, y, R, 0, 6.3); g.fill();
    g.fillStyle = '#ffffff'; g.beginPath();
    const r = R * 0.6;
    if (i === 0) g.arc(x, y, r * 0.9, 0, 6.3);
    else if (i === 1) { g.moveTo(x, y - r * 1.05); g.lineTo(x + r, y + r * 0.8); g.lineTo(x - r, y + r * 0.8); g.closePath(); }
    else if (i === 2) g.rect(x - r * 0.82, y - r * 0.82, r * 1.64, r * 1.64);
    else if (i === 3) { g.moveTo(x, y - r * 1.15); g.lineTo(x + r, y); g.lineTo(x, y + r * 1.15); g.lineTo(x - r, y); g.closePath(); }
    else for (let k = 0; k < 10; k++) { const a = -Math.PI / 2 + k * Math.PI / 5, q = k % 2 ? r * 0.46 : r * 1.14, px = x + Math.cos(a) * q, py = y + Math.sin(a) * q; k ? g.lineTo(px, py) : g.moveTo(px, py); }
    g.fill();
  });
  return c;
}
/** Прості суцільні силуети: на 12 px вони мають лишатись собою. */
function goodsIcon(g, name, x, y, R, ink) {
  g.save(); g.translate(x, y); g.fillStyle = ink; g.strokeStyle = ink; g.lineCap = 'round'; g.lineJoin = 'round';
  if (name === 'salt') { // кристал
    g.beginPath(); g.moveTo(0, -R); g.lineTo(R * 0.9, 0); g.lineTo(0, R); g.lineTo(-R * 0.9, 0); g.closePath(); g.fill();
  } else if (name === 'fish') { // риба носом уперед
    g.beginPath(); g.moveTo(R * 1.15, 0); g.quadraticCurveTo(R * 0.4, -R * 0.95, -R * 0.5, 0); g.quadraticCurveTo(R * 0.4, R * 0.95, R * 1.15, 0); g.fill();
    g.beginPath(); g.moveTo(-R * 0.3, 0); g.lineTo(-R * 1.25, -R * 0.72); g.lineTo(-R * 1.25, R * 0.72); g.closePath(); g.fill();
  } else if (name === 'grain') { // колос: стрілка з трьох шевронів
    g.lineWidth = R * 0.3;
    for (const yy of [-0.72, -0.12, 0.48]) { g.beginPath(); g.moveTo(-R * 0.82, (yy + 0.42) * R); g.lineTo(0, yy * R); g.lineTo(R * 0.82, (yy + 0.42) * R); g.stroke(); }
  } else if (name === 'tar') { // крапля
    g.beginPath(); g.moveTo(0, -R * 1.05); g.bezierCurveTo(R * 0.25, -R * 0.4, R * 0.82, 0, R * 0.82, R * 0.26); g.arc(0, R * 0.26, R * 0.82, 0, Math.PI); g.bezierCurveTo(-R * 0.82, 0, -R * 0.25, -R * 0.4, 0, -R * 1.05); g.fill();
  } else if (name === 'honey') { // стільник
    g.beginPath(); for (let a = 0; a < 6; a++) { const an = a / 6 * 6.283, px = Math.cos(an) * R * 0.92, py = Math.sin(an) * R * 0.92; a ? g.lineTo(px, py) : g.moveTo(px, py); } g.closePath(); g.lineWidth = R * 0.36; g.stroke(); g.beginPath(); g.arc(0, 0, R * 0.2, 0, 6.3); g.fill();
  } else if (name === 'wool') { // кучерява хмарка
    for (const [dx, dy, r] of [[-0.56, 0.16, 0.5], [0.56, 0.16, 0.5], [0, -0.36, 0.6], [0, 0.32, 0.54]]) { g.beginPath(); g.arc(dx * R, dy * R, r * R, 0, 6.3); g.fill(); }
  } else if (name === 'hides') { // розтягнута шкура
    g.beginPath(); [[0, -1.02], [0.34, -0.5], [1, -0.76], [0.58, -0.06], [0.88, 0.84], [0.22, 0.48], [0, 1.02], [-0.22, 0.48], [-0.88, 0.84], [-0.58, -0.06], [-1, -0.76], [-0.34, -0.5]].forEach(([a, b], i) => (i ? g.lineTo(a * R, b * R) : g.moveTo(a * R, b * R))); g.closePath(); g.fill();
  } else if (name === 'linen') { // ткацький хрест
    g.lineWidth = R * 0.42; g.beginPath(); g.moveTo(-R * 0.78, -R * 0.78); g.lineTo(R * 0.78, R * 0.78); g.moveTo(R * 0.78, -R * 0.78); g.lineTo(-R * 0.78, R * 0.78); g.stroke();
  } else { // будь-який вантаж: порожнє кільце
    g.lineWidth = R * 0.32; g.beginPath(); g.arc(0, 0, R * 0.72, 0, 6.3); g.stroke();
  }
  g.restore();
}

// ---------------------------------------------------------------------------------------------------------- стіл
export function paintWood(look, size = 1024) {
  const a = hex(look.tableA), b = hex(look.tableB), c = newCanvas(size, size), g = c.getContext('2d'), im = g.createImageData(size, size), d = im.data;
  const planks = 5;
  for (let j = 0, i = 0; j < size; j++) {
    const y = j / size, pf = y * planks, pi = Math.floor(pf), pv = pf - pi;
    for (let k = 0; k < size; k++, i += 4) {
      const x = k / size, sh = pi * 37.7;
      const warp = (fbm(x * 2.2 + sh, pv * 1.6 + sh, 3) - 0.5) * 2.4;
      const ring = Math.sin((pv * 22 + warp * 7) + sh) * 0.5 + 0.5;
      const fine = fbm(x * 9 + sh, y * 260, 2);
      const fib = noise(x * 34 + sh, y * 620);
      let t = 0.32 * ring + 0.42 * fine + 0.26 * fib + (noise(pi * 7.3, 2.1) - 0.5) * 0.3;
      t = Math.min(1, Math.max(0, t));
      let cc = mixc(a, b, t);
      const edge = Math.min(pv, 1 - pv) * size / planks;
      const ek = edge < 1.6 ? 0.42 : edge < 3.4 ? 0.82 : 1;
      d[i] = cc[0] * ek; d[i + 1] = cc[1] * ek; d[i + 2] = cc[2] * ek; d[i + 3] = 255;
    }
  }
  g.putImageData(im, 0, 0);
  return c;
}

export function paintMap(look, name, geo, data, hi) {
  const t0 = performance.now();
  const px = hi ? 3072 : 1536, c = newCanvas(px, Math.round(px * CH / CW)), g = c.getContext('2d');
  const s = px / CW;
  const P = { g, geo, data, s, hi, px, look };
  if (name === 'parchment') paintParchment(P); else if (name === 'board') paintBoard(P); else paintPetryk(P);
  c._ms = performance.now() - t0;
  return c;
}
