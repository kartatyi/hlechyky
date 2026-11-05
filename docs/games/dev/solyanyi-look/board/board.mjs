// Складає SVG мапи під заданий масштаб. Використовується і ПК, і телефоном.
import { TYPES, INK } from './art.mjs';
import { FX, GEO, proj, landD, outlineD, bordersD, seaD, CLAIM, layoutAll, placeLabels, riverLabels, terrain, routePath, cartSvg, slotSvg, cityOf, shade } from './map.mjs';
const f1 = (v) => (+v).toFixed(1);
const seatColor = FX.mock.players.map((p) => p.color);
export const ORDER_CITIES = {};
FX.mock.myTickets.forEach((t, i) => { if (!t.done) { ORDER_CITIES[t.a] = i + 1; ORDER_CITIES[t.b] = i + 1; } });

export function mapDefs() {
  return `
  <linearGradient id="landG" x1="0" y1="0" x2="0" y2="2730" gradientUnits="userSpaceOnUse">
    <stop offset="0" stop-color="#8fca5f"/><stop offset=".3" stop-color="#a8d868"/><stop offset=".58" stop-color="#c8de70"/><stop offset=".8" stop-color="#e3dc7a"/><stop offset="1" stop-color="#e8d98a"/>
  </linearGradient>
  <linearGradient id="seaG" x1="0" y1="1700" x2="0" y2="2760" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="#54c0ec"/><stop offset="1" stop-color="#2d97de"/></linearGradient>
  <pattern id="hatchN" width="52" height="52" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="52" height="52" fill="none"/><path d="M0 0 V52" stroke="#c9b98c" stroke-width="6" opacity=".55"/></pattern>
  <filter id="blur30" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="60"/></filter>
  <filter id="blur6" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="6"/></filter>
  <filter id="pieceShadow" x="-20%" y="-20%" width="140%" height="140%"><feDropShadow dx="2" dy="3" stdDeviation="2.4" flood-color="#1b0d03" flood-opacity=".55"/></filter>
  <clipPath id="clipSea"><path d="${seaD}"/></clipPath>
  <clipPath id="clipLand"><path d="${landD}"/></clipPath>`;
}

export function buildMap(o) {
  const sc = o.sc, u = (p) => p / sc;
  const lay = o.layout || layoutAll(o);
  const T = terrain(lay, o);
  const labels = placeLabels(lay, o, o.fixed);
  const rivers = GEO.rivers;
  let s = '';
  // сусіди й море
  s += `<rect x="-200" y="-200" width="4400" height="3300" fill="#efe3c0"/><rect x="-200" y="-200" width="4400" height="3300" fill="url(#hatchN)"/>`;
  s += `<path d="${seaD}" fill="url(#seaG)"/>`;
  s += `<g clip-path="url(#clipSea)">
    <path d="${seaD}" fill="none" stroke="#7fd2f2" stroke-width="${u(26)}" stroke-linejoin="round"/>
    <path d="${seaD}" fill="none" stroke="#b8ebf8" stroke-width="${u(11)}" stroke-linejoin="round"/>
    <path d="${outlineD}" fill="none" stroke="#7fd2f2" stroke-width="${u(26)}" stroke-linejoin="round"/>
    <path d="${outlineD}" fill="none" stroke="#c6f0fa" stroke-width="${u(11)}" stroke-linejoin="round"/>
    ${T.waves}
  </g>`;
  s += `<path d="${seaD}" fill="none" stroke="#8d7a4a" stroke-width="${u(1.4)}" stroke-linejoin="round"/>`;
  // суходіл
  s += `<path d="${outlineD}" fill="none" stroke="#fff6da" stroke-width="${u(7)}" stroke-linejoin="round" />`;
  s += `<path d="${landD}" fill="url(#landG)"/>`;
  s += `<g clip-path="url(#clipLand)" filter="url(#blur30)">
    <ellipse cx="420" cy="1290" rx="430" ry="290" fill="#5fa24d" opacity=".75"/>
    <ellipse cx="1100" cy="330" rx="1100" ry="260" fill="#5ea850" opacity=".5"/>
    <ellipse cx="2830" cy="2600" rx="330" ry="140" fill="#6fa64e" opacity=".85"/>
    <ellipse cx="3500" cy="1400" rx="380" ry="360" fill="#b4b562" opacity=".55"/>
    <ellipse cx="2100" cy="1000" rx="700" ry="330" fill="#b8e070" opacity=".6"/>
  </g>`;
  s += `<path d="${bordersD}" fill="none" stroke="#5f8a3e" stroke-width="${u(1.1)}" stroke-dasharray="${u(1.5)} ${u(3.2)}" stroke-linecap="round" opacity=".85"/>`;
  s += `<g clip-path="url(#clipLand)">${T.tufts}${T.flowers}${T.hills}${T.trees}</g>`;
  s += `<path d="${outlineD}" fill="none" stroke="#355a2d" stroke-width="${u(1.9)}" stroke-linejoin="round"/>`;
  // соляні озера: Сиваш і Перекоп — звідки їхали чумаки
  const lake = (lat, lon, rx, ry, rot) => { const [x, y] = proj(lat, lon); return `<ellipse cx="${f1(x)}" cy="${f1(y)}" rx="${u(rx)}" ry="${u(ry)}" transform="rotate(${rot} ${f1(x)} ${f1(y)})" fill="#fbe3ec" stroke="#d27aa0" stroke-width="${u(1.2)}"/><ellipse cx="${f1(x - u(rx * .2))}" cy="${f1(y - u(ry * .15))}" rx="${u(rx * .55)}" ry="${u(ry * .5)}" transform="rotate(${rot} ${f1(x)} ${f1(y)})" fill="#fff"/>`; };
  s += lake(46.08, 33.72, 11, 5.5, -30) + lake(45.92, 33.84, 8, 4, 20) + lake(45.2, 33.45, 9, 4.5, 15);
  // річки
  for (const rv of rivers) {
    const w = rv.big ? 5.4 : (rv.name === 'Дністер' || rv.name === 'Південний Буг' || rv.name === 'Сіверський Донець' || rv.name === 'Десна') ? 3.2 : 2.4;
    const d = rv.lines.map((l) => { let p = 'M' + l[0] + ' ' + l[1]; for (let k = 2; k < l.length; k += 2) p += 'L' + l[k] + ' ' + l[k + 1]; return p; }).join('');
    s += `<path d="${d}" fill="none" stroke="#1672bf" stroke-width="${u(w + 1.6)}" stroke-linejoin="round" stroke-linecap="round" opacity=".92"/><path d="${d}" fill="none" stroke="#3db3f0" stroke-width="${u(w)}" stroke-linejoin="round" stroke-linecap="round"/>`;
  }
  s += `<g clip-path="url(#clipLand)">${T.mts}</g>`;
  s += o.printed ? o.printed(u) : '';
  for (const r of riverLabels(lay, labels, o, o.rivers || [])) {
    const a = r.ang * 57.2958;
    s += `<text x="${f1(r.cx)}" y="${f1(r.cy)}" dy=".34em" transform="rotate(${f1(a)} ${f1(r.cx)} ${f1(r.cy)})" text-anchor="middle" font-size="${u(o.riverFs)}" font-weight="800" font-style="italic" fill="#0b5a9c" stroke="#f2fbd8" stroke-width="${u(2.6)}" paint-order="stroke" stroke-linejoin="round">${r.text}</text>`;
  }
  // шляхи: дорога під клітинками
  const sel = FX.mock.selectedRoute;
  for (const L of lay) {
    if (L.R.id === sel) continue;
    s += `<path d="${routePath(L)}" fill="none" stroke="#fff8e2" stroke-width="${f1(o.T + u(4))}" stroke-linecap="round" opacity=".55"/>`;
  }
  for (const L of lay) {
    if (L.R.id === sel || CLAIM[L.R.id] != null) continue;
    for (const sl of L.slots) s += slotSvg(sl, L.R.color, o);
  }
  // обраний шлях
  const SL = lay.find((L) => L.R.id === sel);
  s += `<path d="${routePath(SL)}" fill="none" stroke="#1a0d05" stroke-width="${f1(o.T + u(17))}" stroke-linecap="round" opacity=".9"/>
    <path d="${routePath(SL)}" fill="none" stroke="#ffe066" stroke-width="${f1(o.T + u(12.5))}" stroke-linecap="round"/>`;
  for (const sl of SL.slots) s += slotSvg(sl, SL.R.color, o, true);
  // вози
  let carts = '';
  for (const L of lay) {
    const p = CLAIM[L.R.id];
    if (p == null) continue;
    for (const sl of L.slots) carts += cartSvg(sl, seatColor[p], o, p);
  }
  s += `<g>${carts}</g>`;
  // міста
  let cit = '';
  for (const c of FX.cities) {
    const r = o.node, hot = c.name === SL.R.a || c.name === SL.R.b, ord = ORDER_CITIES[c.name];
    cit += `<circle cx="${f1(c.x + u(.6))}" cy="${f1(c.y + u(1.2))}" r="${f1(r + u(1.2))}" fill="#1b0d03" opacity=".45"/>`;
    if (hot) cit += `<circle cx="${c.x}" cy="${c.y}" r="${f1(r + u(4.2))}" fill="#ffe066" stroke="#1a0d05" stroke-width="${u(1.4)}"/>`;
    cit += `<circle cx="${c.x}" cy="${c.y}" r="${f1(r)}" fill="#fff8e6" stroke="${INK}" stroke-width="${u(1.9)}"/>`;
    cit += `<circle cx="${c.x}" cy="${c.y}" r="${f1(r * .42)}" fill="${ord ? '#c0392b' : INK}"/>`;
  }
  s += cit;
  let lab = '';
  for (const c of FX.cities) {
    if (o.view && (c.x < o.view[0] || c.x > o.view[2] || c.y < o.view[1] || c.y > o.view[3])) continue;
    const r = labels[c.name], fs = u(o.fs), ord = ORDER_CITIES[c.name], hot = c.name === SL.R.a || c.name === SL.R.b;
    lab += `<rect x="${f1(r.x0)}" y="${f1(r.y0)}" width="${f1(r.w)}" height="${f1(r.h)}" rx="${u(3)}" fill="${hot ? '#ffe066' : '#fff8e6'}" stroke="${ord ? '#c0392b' : '#6b5335'}" stroke-width="${u(ord ? 1.3 : .7)}"/>`;
    r.lines.forEach((t, i) => {
      lab += `<text x="${f1(r.cx)}" y="${f1(r.cy + (i - (r.lines.length - 1) / 2) * fs * 1.14 + fs * .36)}" font-size="${f1(fs)}" font-weight="${hot || c.name === 'Київ' ? 800 : 700}" text-anchor="middle" fill="${INK}">${t}</text>`;
    });
  }
  s += lab;
  s += o.top ? o.top(u, SL, lay) : '';
  return { svg: s, lay, labels, SL };
}
