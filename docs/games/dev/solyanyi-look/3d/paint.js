// «Соляний шлях» — картина-мапа: одна велика намальована текстура. Це головний засіб проти «квадратів»: земля й море мають
// той колір, який намалювали пером і пензлем (вектори по справжньому контуру), а не колір вершин.
// Малюємо лише в канвас: жодних файлів. Три манери: антична мапа, друкована дошка, петриківка.
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
  routeUnderlay(g, data.routes, data.cities, 'rgba(96,62,24,.16)', 74);
  routeUnderlay(g, data.routes, data.cities, 'rgba(70,40,14,.26)', 56);
  cityShadow(g, data.cities, 'rgba(50,28,8,.42)');
  // контур країни: густа чорнильна лінія і тонка побіч
  g.lineJoin = 'round'; g.lineCap = 'round';
  g.strokeStyle = 'rgba(38,22,8,.9)'; g.lineWidth = 4.6; g.stroke(geo.outline);
  g.save(); g.clip(geo.land, 'evenodd'); g.strokeStyle = 'rgba(132,74,34,.2)'; g.lineWidth = 26; g.stroke(geo.outline); g.restore();
  g.save(); clipRect(g); g.strokeStyle = 'rgba(38,22,8,.8)'; g.lineWidth = 3.4; g.stroke(geo.foreignPath); g.restore();
  // назви річок
  const inkItal = (px) => `italic ${px}px "Iowan Old Style",Palatino,Georgia,serif`;
  for (const [name, at] of [['Дніпро', 0.5], ['Дністер', 0.58], ['Південний Буг', 0.5], ['Десна', 0.5], ['Сіверський Донець', 0.5]]) {
    const best = geo.rivers.filter((q) => q.name === name).sort((a, b) => b.pts.length - a.pts.length)[0];
    if (best) { const off = best.pts.map((q) => [q[0], q[1] - 20]); textAlong(g, name, off, at, inkItal(25), 'rgba(40,52,52,.92)', 'rgba(233,214,170,.62)', 0.16); }
  }
  // морські написи
  g.save(); g.fillStyle = 'rgba(40,30,16,.82)'; g.font = inkItal(84); g.textAlign = 'center';
  const arc = (cx, cy, Rr, a0, a1, n = 30) => { const o = []; for (let i = 0; i <= n; i++) { const a = a0 + (a1 - a0) * i / n; o.push([cx + Math.cos(a) * Rr, cy + Math.sin(a) * Rr]); } return o; };
  textAlong(g, 'Чорне море', arc(1985, 3640, 1260, -1.82, -1.32), 0.5, inkItal(86), 'rgba(44,36,20,.86)', null, 0.2);
  textAlong(g, 'Азовське море', arc(3180, 3000, 980, -1.86, -1.28), 0.5, inkItal(42), 'rgba(44,36,20,.8)', null, 0.14);
  g.restore();
  // роза вітрів і румби
  const cx = 1790, cy = 2620, RR = 122;
  g.save(); clipRect(g); g.clip(geo.seaPath);
  g.strokeStyle = 'rgba(70,46,22,.22)'; g.lineWidth = 1.7;
  for (let i = 0; i < 16; i++) { const a = i * Math.PI / 8 + 0.1; g.beginPath(); g.moveTo(cx, cy); g.lineTo(cx + Math.cos(a) * 3600, cy + Math.sin(a) * 3600); g.stroke(); }
  g.restore();
  compass(g, cx, cy, RR, 'rgba(40,24,10,.96)', 'rgb(232,212,170)', 'rgba(150,42,30,.95)');
  // картуш
  cartouche(g, 640, 1985, 'СОЛЯНИЙ ШЛЯХ', 'мапа чумацьких шляхів', 'rgb(240,226,190)', 'rgba(40,24,10,.95)', 'rgba(150,42,30,.95)');
  legend(g, P, 640, 2400, { ink: '#2a1a0c', frame: 'rgba(40,24,10,.9)', fill: 'rgba(240,226,190,.72)', font: 'italic 600 44px "Iowan Old Style",Palatino,Georgia,serif' });
  chumakSketch(g, 3640, 300, 1.15, 'rgba(40,24,10,.86)', 'rgba(226,202,150,.86)');
  // поле: лінійка з градусною рамкою
  neatline(g, 'rgba(40,24,10,.9)', 'rgb(240,226,190)');
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
  g.font = `italic 700 ${R * 0.38}px "Iowan Old Style",Palatino,Georgia,serif`; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText('Пн', 0, -R * 1.3);
  g.restore();
}
function cartouche(g, x, y, title, sub, paper, ink, accent, opt = {}) {
  const w = opt.w || 780, h = opt.h || 215;
  g.save(); g.translate(x, y);
  // сувій із завитками
  g.fillStyle = paper; g.strokeStyle = ink; g.lineWidth = 3.4;
  g.beginPath(); g.moveTo(-w / 2, -h / 2); g.bezierCurveTo(-w / 4, -h / 2 - 24, w / 4, -h / 2 + 24, w / 2, -h / 2); g.bezierCurveTo(w / 2 + 40, -h / 6, w / 2 + 40, h / 6, w / 2, h / 2); g.bezierCurveTo(w / 4, h / 2 + 24, -w / 4, h / 2 - 24, -w / 2, h / 2); g.bezierCurveTo(-w / 2 - 40, h / 6, -w / 2 - 40, -h / 6, -w / 2, -h / 2); g.closePath(); g.fill(); g.stroke();
  g.lineWidth = 1.6; g.setLineDash([]); g.beginPath(); g.moveTo(-w / 2 + 24, -h / 2 + 22); g.bezierCurveTo(-w / 4, -h / 2 + 2, w / 4, -h / 2 + 46, w / 2 - 24, -h / 2 + 22); g.lineTo(w / 2 - 12, h / 2 - 22); g.bezierCurveTo(w / 4, h / 2 + 2, -w / 4, h / 2 - 46, -w / 2 + 24, h / 2 - 22); g.lineTo(-w / 2 + 12, -h / 2 + 22); g.closePath(); g.stroke();
  g.fillStyle = ink; g.textAlign = 'center'; g.textBaseline = 'middle';
  g.font = `700 ${opt.ts || 84}px ${opt.font || '"Iowan Old Style",Palatino,Georgia,serif'}`;
  try { g.letterSpacing = '6px'; } catch {}
  g.fillStyle = accent; g.fillText(title, 0, -22);
  try { g.letterSpacing = '0px'; } catch {}
  g.fillStyle = ink; g.font = `italic ${opt.ss || 40}px ${opt.font || '"Iowan Old Style",Palatino,Georgia,serif'}`; g.fillText(sub, 0, 52);
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
  for (let i = 0, pl = []; i < 1200 && pl.length < 90; i++) { const x = r() * W, y = r() * H; if (geo.maskAt(x, y) !== 0 || geo.bil(geo.sU, x, y) > -40 && geo.bil(geo.sU, x, y) < 40) continue; if (x > 60 && x < 1250 && y > 1760) continue; let ok = true; for (const q of pl) if (Math.hypot(q[0] - x, q[1] - y) < 120) { ok = false; break; } if (!ok) continue; pl.push([x, y]); const w = 22 + r() * 16; g.beginPath(); g.moveTo(x - w, y); g.quadraticCurveTo(x - w * 0.4, y - w * 0.8, x, y); g.quadraticCurveTo(x + w * 0.4, y - w * 0.5, x + w * 0.9, y); g.stroke(); }
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
  routeUnderlay(g, data.routes, data.cities, 'rgba(44,72,40,.14)', 72);
  routeUnderlay(g, data.routes, data.cities, 'rgba(36,58,30,.2)', 54);
  cityShadow(g, data.cities, 'rgba(30,44,24,.32)');
  g.lineJoin = 'round'; g.lineCap = 'round';
  g.strokeStyle = 'rgba(36,76,70,.55)'; g.lineWidth = 11; g.stroke(geo.outline);
  g.strokeStyle = '#fffdf2'; g.lineWidth = 6; g.stroke(geo.outline);
  // чужий берег
  g.save(); clipRect(g); g.strokeStyle = 'rgba(255,255,255,.7)'; g.lineWidth = 3.4; g.stroke(geo.foreignPath); g.restore();
  // написи
  const fnt = (w, px) => `${w} ${px}px "Avenir Next","Trebuchet MS","Segoe UI",system-ui,sans-serif`;
  const arc = (cx, cy, Rr, a0, a1, n = 30) => { const o = []; for (let i = 0; i <= n; i++) { const a = a0 + (a1 - a0) * i / n; o.push([cx + Math.cos(a) * Rr, cy + Math.sin(a) * Rr]); } return o; };
  textAlong(g, 'ЧОРНЕ МОРЕ', arc(1985, 3640, 1260, -1.82, -1.32), 0.5, fnt(800, 72), 'rgba(255,255,255,.88)', null, 0.22);
  textAlong(g, 'АЗОВСЬКЕ МОРЕ', arc(3180, 3000, 980, -1.86, -1.28), 0.5, fnt(800, 32), 'rgba(255,255,255,.88)', null, 0.14);
  for (const [name, at] of [['Дніпро', 0.5], ['Дністер', 0.58], ['Південний Буг', 0.5], ['Десна', 0.5], ['Сіверський Донець', 0.5]]) {
    const best = geo.rivers.filter((q) => q.name === name).sort((a, b) => b.pts.length - a.pts.length)[0];
    if (best) textAlong(g, name, best.pts.map((q) => [q[0], q[1] - 24]), at, fnt(700, 22), '#1b6f94', 'rgba(255,255,255,.6)', 0.08);
  }
  // банер назви
  banner(g, 640, 1975, 'СОЛЯНИЙ ШЛЯХ', fnt);
  legend(g, P, 640, 2400, { ink: '#27384a', frame: '#5a3a1e', fill: 'rgba(255,253,244,.9)', font: fnt(700, 40) });
  compass(g, 1800, 2640, 100, '#1b5f88', '#ffffff', '#e34a3c');
  chumakSketch(g, 3640, 300, 1.15, 'rgba(60,70,40,.9)', 'rgba(247,238,208,.95)');
  // поле: чумацький орнамент (вишивка, рушник)
  rushnyk(g);
  // легкий папір
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.save(); g.globalCompositeOperation = 'multiply'; g.globalAlpha = 0.14; g.fillStyle = g.createPattern(grainTile(256, 9), 'repeat'); g.fillRect(0, 0, g.canvas.width, g.canvas.height); g.restore();
}
function banner(g, x, y, t, fnt) {
  g.save(); g.translate(x, y);
  g.fillStyle = '#b92f27'; const w = 780, h = 150;
  g.beginPath(); g.moveTo(-w / 2 - 80, -h / 2 + 32); g.lineTo(-w / 2 + 34, -h / 2 + 32); g.lineTo(-w / 2 + 34, h / 2 + 32); g.lineTo(-w / 2 - 80, h / 2 + 32); g.lineTo(-w / 2 - 44, 32); g.closePath(); g.fill();
  g.beginPath(); g.moveTo(w / 2 + 80, -h / 2 + 32); g.lineTo(w / 2 - 34, -h / 2 + 32); g.lineTo(w / 2 - 34, h / 2 + 32); g.lineTo(w / 2 + 80, h / 2 + 32); g.lineTo(w / 2 + 44, 32); g.closePath(); g.fill();
  g.fillStyle = '#e34a3c'; g.beginPath(); g.moveTo(-w / 2, -h / 2); g.quadraticCurveTo(0, -h / 2 - 26, w / 2, -h / 2); g.lineTo(w / 2, h / 2); g.quadraticCurveTo(0, h / 2 - 26, -w / 2, h / 2); g.closePath(); g.fill();
  g.strokeStyle = '#fff4d4'; g.lineWidth = 4; g.stroke();
  g.fillStyle = '#fff8e0'; g.font = fnt(800, 78); g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillText(t, 0, -10);
  g.restore();
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
const FOLK = ['#8e2d22', '#a0701c', '#1f5a4c', '#21456f', '#5f2640', '#62672a', '#864a1a'];
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
/** Квітка петриківки: вінець мазків-«ком», центр-гребінець, ягоди. */
function petFlower(g, x, y, sc, c1, c2, gold, rot = 0) {
  const n = 7;
  for (let i = 0; i < n; i++) { const a = rot + i / n * 6.283; comma(g, x + Math.cos(a) * 4 * sc, y + Math.sin(a) * 4 * sc, a, 34 * sc, 14 * sc, c1); comma(g, x + Math.cos(a) * 7 * sc, y + Math.sin(a) * 7 * sc, a + 0.1, 20 * sc, 7 * sc, c2); }
  g.fillStyle = gold; g.beginPath(); g.arc(x, y, 7.5 * sc, 0, 6.3); g.fill();
  for (let i = 0; i < 10; i++) { const a = rot + i / 10 * 6.283; g.beginPath(); g.arc(x + Math.cos(a) * 12 * sc, y + Math.sin(a) * 12 * sc, 1.9 * sc, 0, 6.3); g.fill(); }
}
function petBranch(g, x, y, ang, L, sc, leaf, gold, berry) {
  // вигнута гілка з листям-мазками і ягодами на кінці
  const n = 7; let px = x, py = y, a = ang;
  for (let i = 0; i < n; i++) {
    const st = L / n; const nx = px + Math.cos(a) * st, ny = py + Math.sin(a) * st;
    g.strokeStyle = gold; g.lineWidth = 2.8 * sc; g.lineCap = 'round'; g.beginPath(); g.moveTo(px, py); g.lineTo(nx, ny); g.stroke();
    comma(g, nx, ny, a + 0.9, 26 * sc, 8 * sc, leaf); comma(g, nx, ny, a - 0.9, 26 * sc, -8 * sc, leaf);
    px = nx; py = ny; a += 0.15;
  }
  g.fillStyle = berry; for (const [dx, dy] of [[0, 0], [9, 5], [-3, 9]]) { g.beginPath(); g.arc(px + dx * sc, py + dy * sc, 5.5 * sc, 0, 6.3); g.fill(); }
}
function paintPetryk(P) {
  const { g, geo, data, s, hi } = P, r = rng(722);
  const gold = '#d9ad3c', goldL = '#f3d27a', ground = '#15100c';
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.fillStyle = ground; g.fillRect(0, 0, g.canvas.width, g.canvas.height);
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // сусіди: темний лак із ледь видними завитками
  g.fillStyle = '#211a13'; g.fillRect(-PAD, -PAD, W + 2 * PAD, H + 2 * PAD);
  // море — ніч із золотою лускою
  g.save(); g.beginPath(); g.rect(-PAD, -PAD, W + 2 * PAD, H + 2 * PAD); g.clip(); g.clip(geo.seaPath);
  g.fillStyle = '#0f2231'; g.fillRect(-PAD, 1200, W + 2 * PAD, H);
  g.strokeStyle = 'rgba(217,173,60,.34)'; g.lineWidth = 2.6;
  // луска хвиль: кожен ряд зсунутий і тремтить, не сітка
  for (let y = 1500, row = 0; y < H + 100; y += 40 + r() * 4, row++) {
    const R0 = 46 + r() * 4;
    for (let x = -100 + (row % 2) * R0 + r() * 10; x < W + 200; x += R0 * 2 + (r() - 0.5) * 3) {
      const yy = y + Math.sin(x * 0.006 + row) * 10;
      if (geo.bil(geo.sS, x, yy) < 24) continue;
      g.beginPath(); g.arc(x, yy, R0, Math.PI * 1.08, Math.PI * 1.92); g.stroke();
    }
  }
  g.restore();
  slabShadow(g, geo, 'rgba(0,0,0,.14)');
  // області: пласкі народні кольори
  const nb = adjacency(geo), col = new Array(geo.regions.length).fill(-1);
  const order = geo.regions.map((_, i) => i).sort((a, b) => nb[b].size - nb[a].size);
  for (const i of order) { const used = new Set(); for (const j of nb[i]) if (col[j] >= 0) used.add(col[j]); const start = i * 3 % FOLK.length; for (let k = 0; k < FOLK.length; k++) { const c = (start + k) % FOLK.length; if (!used.has(c)) { col[i] = c; break; } } }
  geo.regions.forEach((rg, i) => { g.fillStyle = FOLK[col[i]]; g.fill(rg.path, 'evenodd'); });
  // легка мазкова текстура всередині, лише на суші: темний кобальт по краях
  g.save(); g.clip(geo.land, 'evenodd'); softStroke(g, geo.outline, 'rgba(10,6,3,.05)', 110, 10, 1); g.restore();
  // квіти-орнаменти в областях: приглушено, щоб шляхи й підписи лишались головними
  geo.regions.forEach((rg, i) => {
    g.save(); g.clip(rg.path, 'evenodd');
    const fc = hex(FOLK[col[i]]), hi1 = rgba(mixc(fc, [255, 240, 200], 0.38), 0.62), hi2 = rgba(mixc(fc, [0, 0, 0], 0.32), 0.58);
    // шукаємо вільні місця всередині області
    const bb = bbox(rg.rings), placed = [];
    for (let k = 0; k < 340 && placed.length < 5; k++) {
      const x = bb[0] + r() * (bb[2] - bb[0]), y = bb[1] + r() * (bb[3] - bb[1]);
      if (geo.bil(geo.sU, x, y) < 34 || !g.isPointInPath(rg.path, x * s + OFF * s, y * s + OFF * s, 'evenodd') || !freeSpot(geo, x, y, 72)) continue;
      let ok = true; for (const q of placed) if (Math.hypot(q[0] - x, q[1] - y) < 170) { ok = false; break; } if (!ok) continue;
      let c2 = true; for (const c of data.cities) if (Math.hypot(c.x - x, c.y - y) < c.r + 70) { c2 = false; break; } if (!c2) continue;
      placed.push([x, y]);
      const sc = 0.72 + r() * 0.36, a = r() * 6.28;
      petBranch(g, x, y, a, 88 * sc, sc * 0.9, hi2, rgba(mixc(fc, [255, 215, 110], 0.5), 0.72), hi1);
      petBranch(g, x, y, a + 2.5, 74 * sc, sc * 0.8, hi2, rgba(mixc(fc, [255, 215, 110], 0.5), 0.72), hi1);
      petFlower(g, x, y, sc * 0.9, hi2, hi1, rgba(mixc(fc, [255, 220, 130], 0.72), 0.95), a);
    }
    g.restore();
  });
  // рельєф: тінь лише натяком
  const S = hillshade(geo);
  g.setTransform(1, 0, 0, 1, 0, 0);
  g.save(); g.globalCompositeOperation = 'multiply';
  g.drawImage(layer(hi ? 1100 : 760, (x, y, o) => {
    const i = geo.pxOf(x, y), m = geo.mask[i];
    if (m !== 1) { o[0] = o[1] = o[2] = 255; return; }
    const d = Math.min(1, Math.max(0, -S[i] * 0.95)); o[0] = 255 - d * 150; o[1] = 255 - d * 150; o[2] = 255 - d * 130;
  }), 0, 0, g.canvas.width, g.canvas.height);
  g.globalCompositeOperation = 'screen';
  g.drawImage(layer(hi ? 1100 : 760, (x, y, o) => {
    const i = geo.pxOf(x, y), m = geo.mask[i];
    if (m !== 1) { o[0] = o[1] = o[2] = 0; return; }
    const d = Math.min(1, Math.max(0, S[i] * 0.65)); o[0] = d * 120; o[1] = d * 100; o[2] = d * 70;
  }), 0, 0, g.canvas.width, g.canvas.height);
  g.restore();
  g.setTransform(s, 0, 0, s, OFF * s, OFF * s);
  // гори: золоті мазки вздовж хребта
  g.lineCap = 'round';
  for (let i = 0, n = hi ? 9000 : 3200; i < n; i++) {
    const x = r() * W, y = r() * H; if (geo.maskAt(x, y) !== 1) continue;
    const rr = geo.bil(geo.R, x, y); if (rr < 0.11 || r() > (rr - 0.09) * 12) continue;
    const e = 7, dx = geo.bil(geo.R, x + e, y) - geo.bil(geo.R, x - e, y), dy = geo.bil(geo.R, x, y + e) - geo.bil(geo.R, x, y - e), a = Math.atan2(dy, dx) + Math.PI;
    if (!freeSpot(geo, x, y, 36)) continue;
    comma(g, x, y, a, 18 + r() * 12, 2.2, 'rgba(240,205,120,.55)');
  }
  // межі областей — золота нитка з темним зазором
  g.lineJoin = 'round'; g.lineCap = 'round';
  g.save(); g.clip(geo.land, 'evenodd');
  g.strokeStyle = ground; g.lineWidth = 8; g.stroke(geo.inner);
  g.strokeStyle = gold; g.lineWidth = 2.6; g.stroke(geo.inner);
  g.restore();
  // річки
  drawRiversUnder(g, geo, data.routes, 'rgba(10,8,5,.92)', '#2f7fa8');
  // шляхи: темна доріжка
  routeUnderlay(g, data.routes, data.cities, 'rgba(10,6,3,.12)', 70);
  routeUnderlay(g, data.routes, data.cities, 'rgba(10,6,3,.26)', 54);
  cityShadow(g, data.cities, 'rgba(5,3,1,.62)');
  // контур країни: подвійне золото
  g.strokeStyle = ground; g.lineWidth = 12; g.stroke(geo.outline);
  g.strokeStyle = goldL; g.lineWidth = 5; g.stroke(geo.outline);
  g.save(); clipRect(g); g.strokeStyle = 'rgba(217,173,60,.75)'; g.lineWidth = 2.6; g.stroke(geo.foreignPath); g.restore();
  // написи
  const fnt = (w, px, it = '') => `${it} ${w} ${px}px "Iowan Old Style",Palatino,Georgia,serif`;
  const arc = (cx, cy, Rr, a0, a1, n = 30) => { const o = []; for (let i = 0; i <= n; i++) { const a = a0 + (a1 - a0) * i / n; o.push([cx + Math.cos(a) * Rr, cy + Math.sin(a) * Rr]); } return o; };
  textAlong(g, 'Чорне море', arc(1985, 3640, 1260, -1.82, -1.32), 0.5, fnt(600, 84, 'italic'), goldL, '#0f2231', 0.22);
  textAlong(g, 'Азовське море', arc(3180, 3000, 980, -1.86, -1.28), 0.5, fnt(600, 40, 'italic'), goldL, '#0f2231', 0.14);
  for (const [name, at] of [['Дніпро', 0.5], ['Дністер', 0.58], ['Південний Буг', 0.5], ['Десна', 0.5], ['Сіверський Донець', 0.5]]) {
    const best = geo.rivers.filter((q) => q.name === name).sort((a, b) => b.pts.length - a.pts.length)[0];
    if (best) textAlong(g, name, best.pts.map((q) => [q[0], q[1] - 22]), at, fnt(600, 24, 'italic'), '#fff0c8', 'rgba(16,10,5,.75)', 0.1);
  }
  // кути сусідніх земель: великі петриківські букети
  const bouquet = (x, y, a0, sc) => {
    const cols = ['rgba(46,125,100,.92)', 'rgba(52,100,160,.92)'], gl = 'rgba(217,173,60,.9)';
    for (let i = 0; i < 5; i++) petBranch(g, x, y, a0 + (i - 2) * 0.62, (250 - 26 * Math.abs(i - 2)) * sc, sc * 1.25, cols[i % 2], gl, i % 2 ? 'rgba(226,186,90,.95)' : 'rgba(205,62,46,.95)');
    petFlower(g, x, y, sc * 2.2, 'rgba(190,52,40,.98)', 'rgba(242,176,120,.95)', 'rgba(244,206,110,1)', a0);
    petFlower(g, x + Math.cos(a0 + 1.0) * 150 * sc, y + Math.sin(a0 + 1.0) * 150 * sc, sc * 1.25, 'rgba(208,140,40,.98)', 'rgba(250,214,140,.95)', 'rgba(190,52,40,1)', a0);
    petFlower(g, x + Math.cos(a0 - 1.0) * 150 * sc, y + Math.sin(a0 - 1.0) * 150 * sc, sc * 1.25, 'rgba(52,100,160,.98)', 'rgba(170,205,235,.95)', 'rgba(244,206,110,1)', a0);
  };
  g.save(); clipRect(g);
  { const p2 = new Path2D(); p2.rect(-OFF, -OFF, CW, CH); p2.addPath(geo.land); g.clip(p2, 'evenodd'); }
  bouquet(70, 230, 0.35, 0.9); bouquet(3820, 200, 2.7, 1.0); bouquet(3900, 2660, 3.75, 0.9); bouquet(90, 1660, 0.0, 0.62);
  g.restore();
  // картуш
  cartouche(g, 640, 1985, 'СОЛЯНИЙ ШЛЯХ', 'чумацькі шляхи України', '#15100c', goldL, goldL, { font: '"Iowan Old Style",Palatino,Georgia,serif' });
  legend(g, P, 640, 2400, { ink: goldL, frame: gold, fill: 'rgba(16,11,7,.88)', font: fnt(600, 42, 'italic') });
  compass(g, 1800, 2640, 104, goldL, '#15100c', '#d24a35');
  // поле: фриз із петриківських мазків
  const stripe = (d, w, c) => { g.strokeStyle = c; g.lineWidth = w; g.strokeRect(-PAD - d, -PAD - d, W + 2 * PAD + 2 * d, H + 2 * PAD + 2 * d); };
  stripe(12, 8, gold); stripe(28, 2.5, gold); stripe(172, 7, gold); stripe(160, 2.5, gold);
  const leaf = ['#2d7360', '#2c5a92'], fl = ['#c23a2b', '#c98a22'];
  const motif = (x, y, a, i) => {
    petBranch(g, x, y, a, 62, 0.82, leaf[i % 2], gold, '#e8c060');
    petBranch(g, x, y, a + Math.PI, 62, 0.82, leaf[(i + 1) % 2], gold, '#e8c060');
    petFlower(g, x, y, 0.9, fl[i % 2], '#f0b47a', goldL, i);
  };
  let i = 0;
  for (let x = -PAD + 100; x < W + PAD; x += 220) { motif(x, -PAD - 94, 0, i); motif(x, H + PAD + 94, 0, i + 1); i++; }
  for (let y = -PAD + 100; y < H + PAD; y += 220) { motif(-PAD - 94, y, Math.PI / 2, i); motif(W + PAD + 94, y, Math.PI / 2, i + 1); i++; }
}
function bbox(rings) { let a = 1e9, b = 1e9, c = -1e9, d = -1e9; for (const r of rings) for (let k = 0; k < r.length; k += 2) { a = Math.min(a, r[k]); b = Math.min(b, r[k + 1]); c = Math.max(c, r[k]); d = Math.max(d, r[k + 1]); } return [a, b, c, d]; }

// ---------------------------------------------------------------------------------------------------------- атлас плашок: колір + візерунок + значок товару (другий канал для дальтоніків)
export const ATLAS = { cols: 1, cellW: 400, cellH: 100, order: ['red', 'blue', 'green', 'black', 'yellow', 'white', 'orange', 'pink', 'any'] };
export function paintAtlas(look, hi) {
  const k = hi ? 1 : 0.64, cw = Math.round(ATLAS.cellW * k), ch = Math.round(ATLAS.cellH * k), n = ATLAS.order.length;
  const c = newCanvas(cw, ch * (n + 1)), g = c.getContext('2d');
  g.fillStyle = '#fff'; g.fillRect(0, ch * n, cw, ch);
  ATLAS.order.forEach((name, i) => {
    g.save(); g.setTransform(k, 0, 0, k, 0, i * ch);
    const base = look.routes[name];
    const rgb = hex(base), lum = (0.3 * rgb[0] + 0.59 * rgb[1] + 0.11 * rgb[2]) / 255;
    const ink = lum > 0.56 ? '#201812' : '#ffffff';
    const inkA = lum > 0.56 ? 'rgba(32,24,18,' : 'rgba(255,255,255,';
    g.fillStyle = base; g.fillRect(0, 0, ATLAS.cellW, ATLAS.cellH);
    // візерунок тла: свій у кожного кольору (видно навіть у сірому)
    g.save(); g.strokeStyle = inkA + '.3)'; g.fillStyle = inkA + '.3)'; g.lineWidth = 4.5;
    const X = ATLAS.cellW, Y = ATLAS.cellH;
    if (name === 'red') { for (let x = -Y; x < X + Y; x += 17) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x + Y, Y); g.stroke(); } }
    else if (name === 'blue') { g.lineWidth = 4.5; for (let y = 8; y < Y; y += 19) { g.beginPath(); for (let x = 0; x <= X; x += 6) { const yy = y + Math.sin(x * 0.11) * 4; x ? g.lineTo(x, yy) : g.moveTo(x, yy); } g.stroke(); } }
    else if (name === 'green') { for (let x = 6; x < X; x += 14) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x, Y); g.stroke(); } }
    else if (name === 'black') { g.fillStyle = inkA + '.3)'; for (let y = 10, r = 0; y < Y; y += 20, r++) for (let x = 6 + (r % 2) * 10; x < X; x += 20) { g.beginPath(); g.arc(x, y, 3, 0, 6.3); g.fill(); } }
    else if (name === 'yellow') { g.lineWidth = 3.2; const S = 14; for (let r = 0, y = 0; y < Y + S; y += S * 1.5, r++) for (let x = (r % 2) * S * 0.87; x < X + S; x += S * 1.74) { g.beginPath(); for (let a = 0; a < 6; a++) { const an = a / 6 * 6.283 + Math.PI / 6, px = x + Math.cos(an) * S * 0.9, py = y + Math.sin(an) * S * 0.9; a ? g.lineTo(px, py) : g.moveTo(px, py); } g.closePath(); g.stroke(); } }
    else if (name === 'white') { g.lineWidth = 3.4; for (let y = 12, r = 0; y < Y; y += 22, r++) for (let x = 8 + (r % 2) * 13; x < X; x += 26) { g.beginPath(); g.arc(x, y, 6, 0.6, 5); g.stroke(); } }
    else if (name === 'orange') { g.lineWidth = 4.5; for (let x = -20; x < X + 20; x += 21) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x + 16, Y / 2); g.lineTo(x, Y); g.stroke(); } }
    else if (name === 'pink') { g.lineWidth = 3; for (let x = -Y; x < X + Y; x += 20) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x + Y, Y); g.moveTo(x + Y, 0); g.lineTo(x, Y); g.stroke(); } }
    g.restore();
    // значок у центрі на «медальйоні»
    const cx = X / 2, cy = Y / 2, R = 45;
    g.fillStyle = lum > 0.56 ? 'rgba(255,255,255,.55)' : 'rgba(0,0,0,.35)';
    g.beginPath(); g.ellipse(cx, cy, R * 1.4, R * 1.2, 0, 0, 6.3); g.fill();
    goodsIcon(g, name, cx, cy, R * 0.95, ink);
    g.restore();
  });
  return c;
}
function goodsIcon(g, name, x, y, R, ink) {
  g.save(); g.translate(x, y); g.fillStyle = ink; g.strokeStyle = ink; g.lineCap = 'round'; g.lineJoin = 'round';
  if (name === 'red') { // сіль: кристал
    g.beginPath(); g.moveTo(0, -R); g.lineTo(R * 0.86, -R * 0.1); g.lineTo(0, R); g.lineTo(-R * 0.86, -R * 0.1); g.closePath(); g.fill();
  } else if (name === 'blue') { // риба
    g.beginPath(); g.moveTo(R * 0.95, 0); g.quadraticCurveTo(R * 0.3, -R * 0.82, -R * 0.45, 0); g.quadraticCurveTo(R * 0.3, R * 0.82, R * 0.95, 0); g.fill();
    g.beginPath(); g.moveTo(-R * 0.3, 0); g.lineTo(-R * 1.1, -R * 0.56); g.lineTo(-R * 1.1, R * 0.56); g.closePath(); g.fill();
  } else if (name === 'green') { // колос
    g.lineWidth = R * 0.17; g.beginPath(); g.moveTo(0, R); g.lineTo(0, -R * 0.9); g.stroke();
    for (const yy of [-0.65, -0.15, 0.35]) for (const sg of [1, -1]) { g.beginPath(); g.ellipse(sg * R * 0.33, yy * R, R * 0.4, R * 0.17, sg * -0.8, 0, 6.3); g.fill(); }
  } else if (name === 'black') { // дьоготь: крапля
    g.beginPath(); g.moveTo(0, -R); g.bezierCurveTo(R * 0.25, -R * 0.35, R * 0.8, 0, R * 0.8, R * 0.3); g.arc(0, R * 0.3, R * 0.8, 0, Math.PI); g.bezierCurveTo(-R * 0.8, 0, -R * 0.25, -R * 0.35, 0, -R); g.fill();
  } else if (name === 'yellow') { // мед: стільник
    g.beginPath(); for (let a = 0; a < 6; a++) { const an = a / 6 * 6.283, px = Math.cos(an) * R, py = Math.sin(an) * R; a ? g.lineTo(px, py) : g.moveTo(px, py); } g.closePath(); g.lineWidth = R * 0.3; g.stroke(); g.beginPath(); g.arc(0, 0, R * 0.26, 0, 6.3); g.fill();
  } else if (name === 'white') { // вовна: хмарка-завитки
    for (const [dx, dy, r] of [[-0.48, 0.14, 0.46], [0.48, 0.14, 0.46], [0, -0.32, 0.54], [0, 0.34, 0.44]]) { g.beginPath(); g.arc(dx * R, dy * R, r * R, 0, 6.3); g.fill(); }
  } else if (name === 'orange') { // шкіра: розтягнута шкура
    g.beginPath(); const p = [[0, -1], [0.34, -0.5], [0.95, -0.72], [0.56, -0.08], [0.82, 0.8], [0.22, 0.46], [0, 1], [-0.22, 0.46], [-0.82, 0.8], [-0.56, -0.08], [-0.95, -0.72], [-0.34, -0.5]]; p.forEach(([a, b], i) => (i ? g.lineTo(a * R, b * R) : g.moveTo(a * R, b * R))); g.closePath(); g.fill();
  } else if (name === 'pink') { // полотно: ткацький хрест
    g.lineWidth = R * 0.34; g.beginPath(); g.moveTo(-R * 0.8, -R * 0.8); g.lineTo(R * 0.8, R * 0.8); g.moveTo(R * 0.8, -R * 0.8); g.lineTo(-R * 0.8, R * 0.8); g.stroke();
  } else { // будь-який: порожнє кільце
    g.lineWidth = R * 0.26; g.beginPath(); g.arc(0, 0, R * 0.72, 0, 6.3); g.stroke();
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

const GOODS_NAME = { red: 'сіль', blue: 'риба', green: 'збіжжя', black: 'дьоготь', yellow: 'мед', white: 'вовна', orange: 'шкіри', pink: 'полотно' };
/** Легенда товарів: колір + значок + слово (друга ознака для дальтоніків). */
function legend(g, P, cx, cy, st) {
  const names = Object.keys(GOODS_NAME), cols = 2, rows = 4, cw = 440, rh = 104, w = cols * cw + 40, h = rows * rh + 40, x0 = cx - w / 2, y0 = cy - h / 2;
  g.save();
  g.fillStyle = st.fill; g.strokeStyle = st.frame; g.lineWidth = 4;
  g.beginPath(); g.roundRect(x0, y0, w, h, 26); g.fill(); g.stroke();
  g.lineWidth = 1.6; g.beginPath(); g.roundRect(x0 + 10, y0 + 10, w - 20, h - 20, 18); g.stroke();
  names.forEach((name, i) => {
    const col = i % rows < 0 ? 0 : Math.floor(i / rows), row = i % rows, x = x0 + 40 + col * cw, y = y0 + 30 + row * rh + rh / 2 - 8;
    const base = P.look.routes[name], rgb = hex(base), lum = (0.3 * rgb[0] + 0.59 * rgb[1] + 0.11 * rgb[2]) / 255;
    g.fillStyle = base; g.strokeStyle = st.frame; g.lineWidth = 3; g.beginPath();
    { const sh = { red: 'chev', pink: 'chev', yellow: 'pill', white: 'pill', orange: 'bone', black: 'bone' }[name] || 'rect', a = x, b = x + 136, k = 15;
      if (sh === 'pill') g.roundRect(a, y - 36, 136, 72, 36); else if (sh === 'rect') g.roundRect(a, y - 36, 136, 72, 10);
      else if (sh === 'chev') { g.moveTo(a, y - 36); g.lineTo(b, y - 36); g.lineTo(b + k, y); g.lineTo(b, y + 36); g.lineTo(a, y + 36); g.lineTo(a + k, y); g.closePath(); }
      else { g.moveTo(a, y - 36); g.lineTo(b, y - 36); g.quadraticCurveTo(b - k, y, b, y + 36); g.lineTo(a, y + 36); g.quadraticCurveTo(a + k, y, a, y - 36); g.closePath(); } }
    g.fill(); g.stroke();
    goodsIcon(g, name, x + 68, y, 26, lum > 0.56 ? '#201812' : '#ffffff');
    g.fillStyle = st.ink; g.font = st.font; g.textAlign = 'left'; g.textBaseline = 'middle'; g.fillText(GOODS_NAME[name], x + 158, y + 2);
  });
  g.restore();
}
