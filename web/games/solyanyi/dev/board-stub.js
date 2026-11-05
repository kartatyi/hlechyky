// Заглушка поля для стенду картки (dev/hud.html?stub=1): та сама форма API, що й у scene.js/board2d.js (§10). Для перевірки
// HUD, коли справжнє поле ще редагують. У грі її нема: стенд підставляє import map, картка тягне /games/solyanyi/board2d.js.
import { ROUTES, CITIES, routeSlots, routeCurve, route, city, MAP } from '../consts.js';
import { themeOf } from '../themes.js';
import { parseGeo } from '../hud-calc.js';

export async function createBoard2d(host, opts) {
  const pal = () => themeOf(api.theme).pal;
  const NS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 ' + MAP.w + ' ' + MAP.h);
  svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
  svg.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;';
  host.appendChild(svg);
  let view = null, marks = {}, sel = null, geo = null, path = [];
  const api = {
    theme: opts.theme,
    kind: 'stub',
    setBoard() {}, setState(v) { view = v; draw(); }, setLegal() {}, setMarks(m) { marks = m || {}; draw(); }, setTickets() {},
    setPath(p) { path = p || []; draw(); }, setSelected(id) { sel = id; draw(); }, setCursor() {},
    async claim() {}, async ticketDone() {}, async last() {}, async longest() {}, async finale() {},
    focus() {}, zoomBy() {}, resetView() {}, resize() {}, setTheme(t) { api.theme = t; draw(); },
    routeAt() { return []; },
    screenPos(kind, id) {
      const r = svg.getBoundingClientRect(), k = Math.min(r.width / MAP.w, r.height / MAP.h), ox = (r.width - MAP.w * k) / 2, oy = (r.height - MAP.h * k) / 2;
      const P = (x, y) => ({ x: ox + x * k, y: oy + y * k });
      if (kind === 'city') { const c = city(id); return P(c.x, c.y); }
      if (kind === 'route') { const s = routeSlots(id); const m = s[Math.floor(s.length / 2)]; return P(m.x, m.y); }
      return null;
    },
    dispose() { svg.remove(); },
  };
  function draw() {
    const p = pal();
    let o = '<rect x="-600" y="-600" width="5200" height="3930" fill="' + p.water + '"/>';
    if (geo) o += '<path d="' + geo.land + '" fill="' + p.land + '" stroke="' + p.region + '" stroke-width="4"/><path d="' + geo.edge + '" fill="none" stroke="' + p.landEdge + '" stroke-width="10"/>';
    const own = {};
    for (const c of (view && view.claimed) || []) own[c.route] = c.seat;
    for (const rr of ROUTES) {
      const id = rr[0], r = route(id);
      for (const s of routeSlots(id)) {
        const seat = own[id];
        const fill = seat != null ? p.seat[seat] : p.cargo[r.cargo] || p.slot;
        const stroke = sel === id ? p.sel : marks[id] === 'ok' ? p.legal : p.slotEdge;
        o += '<rect x="' + (-s.len / 2) + '" y="-14" width="' + s.len + '" height="28" rx="5" transform="translate(' + s.x.toFixed(0) + ' ' + s.y.toFixed(0) + ') rotate(' + (s.a * 180 / Math.PI).toFixed(1) + ')" fill="' + fill + '" stroke="' + stroke + '" stroke-width="' + (sel === id ? 12 : 4) + '" data-r="' + id + '"/>';
      }
    }
    for (const c of CITIES) o += '<circle cx="' + c[3] + '" cy="' + c[4] + '" r="24" fill="' + p.city + '" stroke="' + p.cityEdge + '" stroke-width="6"/>'
      + '<text x="' + (c[3] + 30) + '" y="' + (c[4] + 12) + '" font-size="40" fill="' + p.cityInk + '" stroke="' + (p.cityInk === '#fbf1da' ? '#0a0812' : '#fff8') + '" stroke-width="8" paint-order="stroke">' + c[2] + '</text>';
    svg.innerHTML = o;
  }
  svg.addEventListener('click', (e) => {
    const t = e.target.closest('[data-r]');
    opts.onPick(t ? 'route' : null, t ? +t.dataset.r : null);
  });
  fetch(MAP.url).then((r) => r.json()).then((g) => { geo = parseGeo(g); draw(); }).catch(() => {});
  draw();
  return api;
}
export const createScene = async () => { throw new Error('заглушка: без 3D'); };
