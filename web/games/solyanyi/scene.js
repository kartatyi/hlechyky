// «Соляний шлях» — 3D-поле: мапа України на столі, шляхи-плашки, вози, міста. Контракт — docs/games/specs/solyanyi.md §10–11.
//   const sc = await createScene(host, { onPick, onHover, quality: 'high'|'low', reducedMotion, theme });
//   sc.setBoard(board); sc.setState(view); sc.setLegal({ routes, cities }); sc.setMarks(marks); sc.setTickets(list);
//   sc.setPath(ids); sc.setSelected(id); sc.setCursor(kind, id);
//   await sc.claim(ev); await sc.ticketDone(ev); await sc.last(ev); await sc.longest(ev); await sc.finale(over);
//   sc.focus(kind, id); sc.zoomBy(k); sc.routeAt(x, y); sc.screenPos(kind, id); sc.setTheme(id); sc.theme;
//   sc.setHud(rects); sc.setCrop(on); sc.setHome(list); sc.labelBoxes(); sc.resetView(); sc.resize(); sc.dispose();
// Кадр: на широкому полі типово видно всі міста, шляхи й підписи, і нічого з них не лежить під HUD (setHud — прямокутники картки
// поверх поля); на телефоні (setCrop) типово — читабельна ділянка навколо своїх замовлень (setHome), ⌂ перемикає на всю мапу.
// Сцена не знає правил і не шле дій — лише малює й повідомляє про кліки. Будова — world.js; земля й картина — terrain.js, paint.js.
//
// Попадання — лише в пікселях екрана за слотами з consts.js (ніякого raycast по мешах). Рендер на вимогу; на high фон — ≤ 30 к/с,
// на low і з reducedMotion фону нема. Карта тіней оновлюється лише коли змінилось, що стоїть на мапі (кінець claim, зміна рівня воза).
import * as THREE from '/static/vendor/three-0.186.1/three.module.js';
import { wheelZoomFactor, twoFinger } from '/games/khutory/gestures.js';
import { MAP_SIG, ticket as ticketOf } from './consts.js';
import { homeCrop } from './hud-calc.js';
import { themeOf } from './themes.js';
import { lookOf } from './looks.js';
import { U, W, H, wx, wz, heightAt } from './terrain.js';
import { buildBase, buildWorld, atS, clamp } from './world.js';

const D2R = Math.PI / 180;
const FOV = 35, POL_DEF = 20 * D2R, POL_MAX = 40 * D2R, AZ_MAX = 15 * D2R, ZOOM_MAX = 6;
const EDGE_PX = 6, VOID = 0.12;                  // поле кадру при наближенні 1 і найбільша порожнеча за межею мапи (§11.2)
const BOX = { x0: 35, y0: 43, x1: 3960, y1: 2687 };   // крайні точки країни (geo.md §5.1)
const CROP_W = 1150, CROP_MAX = 1.5;             // телефон: скільки одиниць мапи видно завширшки типово і наскільки це можна розширити заради замовлень
const TAP_PX = 8, R_TOUCH = 22, R_MOUSE = 10, DBL_MS = 320, HANDS_OFF_MS = 4000, IDLE_MS = 2000;
const NEAR_K = { hi: [0.66, 0.58], lo: [0.5, 0.44] };   // px/одиницю: ближче — докладний віз (з гістерезисом)
const KIND = { none: 0, legal: 1, near: 2, plan: 3, path: 4, hover: 5, cursor: 6, sel: 7, flash: 8, ok: 9 };

const easeOut = (t) => 1 - (1 - t) * (1 - t) * (1 - t);
const easeInOut = (t) => t * t * (3 - 2 * t);

export async function createScene(host, opts = {}) {
  const hi = opts.quality !== 'low', reduced = !!opts.reducedMotion;
  const onPick = typeof opts.onPick === 'function' ? opts.onPick : () => {};
  const onHover = typeof opts.onHover === 'function' ? opts.onHover : () => {};
  let themeId = themeOf(opts.theme).id;
  const T0 = performance.now();

  // ------------------------------------------------------------------------------------------------ рендерер
  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance' });
  } catch (e) { throw new Error('WebGL недоступний: ' + (e && e.message || e)); }
  if (!renderer.getContext()) { renderer.dispose(); throw new Error('WebGL недоступний'); }
  renderer.setPixelRatio(Math.min(hi ? 2 : 1, window.devicePixelRatio || 1));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.shadowMap.enabled = hi;
  renderer.shadowMap.type = THREE.PCFShadowMap;
  renderer.shadowMap.autoUpdate = false;
  const canvas = renderer.domElement;
  canvas.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;display:block;touch-action:none;outline:none;-webkit-tap-highlight-color:transparent;user-select:none';
  if (getComputedStyle(host).position === 'static') host.style.position = 'relative';
  host.style.overflow = 'hidden';
  host.appendChild(canvas);
  const vignette = document.createElement('div'); vignette.style.cssText = 'position:absolute;inset:0;pointer-events:none';
  const overlay = document.createElement('div'); overlay.style.cssText = 'position:absolute;inset:0;overflow:hidden;pointer-events:none';
  // До першого кадру нічого не видно: підписи ще без стилів, а будова на слабкій відеокарті йде секундами. Заглушку малює картка.
  for (const el of [canvas, vignette, overlay]) el.style.visibility = 'hidden';
  host.appendChild(vignette); host.appendChild(overlay);

  const scene = new THREE.Scene();
  const cam = new THREE.PerspectiveCamera(FOV, 1, 1, 80);
  const hemi = new THREE.HemisphereLight('#ffffff', '#888888', 2); scene.add(hemi);
  const sun = new THREE.DirectionalLight('#ffffff', 1.5);
  sun.position.set(-7, 10, 2.8); sun.target.position.set(0, 0, 0);
  if (hi) {
    sun.castShadow = true; sun.shadow.mapSize.set(1024, 1024);
    Object.assign(sun.shadow.camera, { left: -6.4, right: 6.4, top: 5.2, bottom: -5.2, near: 2, far: 26 });
    sun.shadow.bias = -0.0006; sun.shadow.normalBias = 0.016; sun.shadow.radius = 2;
  }
  scene.add(sun); scene.add(sun.target);

  // ------------------------------------------------------------------------------------------------ стан і спільні уніформи
  let base = null, world = null, look = null, alive = true, buildSeq = 0, shown = false;
  const claimed = new Map();                     // route → seat
  let closed = new Set(), seatColor = [0, 1, 2, 3, 4], lastRoute = {};
  let legalR = null, legalC = null, marks = null, path = null, selected = null, cursor = null, hover = null, hoverCity = null;
  let tickets = [], finaleOn = null, dimLights = 1;
  const flash = new Map();                       // № слота → { kind, a } — поверх усього на час анімації
  const baseOver = new Map();                    // № слота → a: колір власника наростає, коли віз доїхав

  const shared = {
    time: { value: 0 }, uPulse: { value: 1 }, uBadgePx: { value: 14 }, uRes: { value: new THREE.Vector2(1, 1) },
    uKinds: { value: Array.from({ length: 10 }, () => new THREE.Color('#ffffff')) }, uState: { value: null },
  };

  // ------------------------------------------------------------------------------------------------ будова
  const fail = (e) => { try { renderer.dispose(); canvas.remove(); vignette.remove(); overlay.remove(); } catch { /* уже прибрано */ } throw e; };
  try {
    base = await buildBase(hi);
  } catch (e) { fail(e); }
  const { routes, slots, cities, geo } = base;
  const N = slots.length;
  const stateData = new Uint8Array(N * 2 * 4);
  const stateTex = new THREE.DataTexture(stateData, N, 2, THREE.RGBAFormat, THREE.UnsignedByteType);
  stateTex.minFilter = stateTex.magFilter = THREE.NearestFilter; stateTex.needsUpdate = true;
  shared.uState.value = stateTex;

  function applyLookLights() {
    renderer.toneMapping = look.tone === 'aces' ? THREE.ACESFilmicToneMapping : look.tone === 'neutral' ? THREE.NeutralToneMapping : THREE.NoToneMapping;
    renderer.toneMappingExposure = look.exp;
    scene.background = new THREE.Color(look.bg);
    if (shown) host.style.background = look.bg;
    hemi.color.set(look.hemi[0]); hemi.groundColor.set(look.hemi[1]);
    sun.color.set(look.sun[0]);
    setLightK(dimLights);
    vignette.style.background = look.vignette;
    const K = shared.uKinds.value, H_ = look.hl;
    K[KIND.legal].set(H_.legal); K[KIND.ok].set(H_.ok); K[KIND.near].set(H_.near); K[KIND.plan].set(H_.plan); K[KIND.path].set(H_.sel);
    K[KIND.hover].set('#ffffff'); K[KIND.cursor].set('#ffffff'); K[KIND.sel].set(H_.sel); K[KIND.flash].set('#fffbe0');
  }
  function setLightK(k) { dimLights = k; hemi.intensity = look.hemi[2] * k; sun.intensity = look.sun[1] * k; }

  async function loadWorld(id) {
    const seq = ++buildSeq, nextLook = lookOf(id), w = await buildWorld(base, nextLook, hi, renderer, shared);
    if (!alive || seq !== buildSeq) { w.dispose(); return false; }
    if (world) { scene.remove(world.group); world.dispose(); renderer.renderLists.dispose(); }
    world = w; look = nextLook; themeId = id;
    scene.add(world.group);
    applyLookLights(); styleLabels();
    fit();                                       // кадр рахує й ширину підписів, а вони в кожної теми свої
    rebuildCarts(); syncState(); syncRings();
    renderer.shadowMap.needsUpdate = true;
    return true;
  }

  // ------------------------------------------------------------------------------------------------ стан слотів → текстура
  const seatCol = (s) => (look.players[seatColor[s] ?? s] || '#888888');
  const _c = new THREE.Color();
  function syncState() {
    if (!world) return;
    let any = false;
    for (const r of routes) {
      const id = r.id, owner = claimed.get(id);
      let kind = KIND.none, str = 0, lift = 0, dim = 0;
      if (closed.has(id)) dim = 0.8;
      else if (legalR && !legalR.has(id) && owner === undefined) dim = 0.3;
      const mk = marks && marks[id];
      if (mk === 'plan') { kind = KIND.plan; str = 0.95; }
      if (mk === 'near') { kind = KIND.near; str = 0.9; }
      if (mk === 'ok') { kind = KIND.ok; str = 0.9; }
      if (legalR && legalR.has(id)) { kind = KIND.legal; str = 1; }
      if (path && path.has(id)) { kind = KIND.path; str = 1; if (finaleOn) dim = 0; }
      if (finaleOn && !(path && path.has(id))) dim = Math.max(dim, finaleOn.winners.has(owner) ? 0.15 : 0.6);
      if (hover === id) { kind = KIND.hover; str = 0.85; }
      if (cursor && cursor.kind === 'route' && cursor.id === id) { kind = KIND.cursor; str = 1; }
      if (selected === id) { kind = KIND.sel; str = 1; lift = 1; }
      if (owner !== undefined) { _c.set(seatCol(owner)); }
      for (const s of r.slots) {
        const i = s.i * 4, j = (s.i + N) * 4, f = flash.get(s.i);
        stateData[i] = f ? f.kind : kind; stateData[i + 1] = Math.round((f ? f.a : str) * 255); stateData[i + 2] = lift * 255; stateData[i + 3] = Math.round(dim * 255);
        if (f || str > 0) any = true;
        if (owner !== undefined) {
          const a = baseOver.has(s.i) ? baseOver.get(s.i) : 0.92;
          stateData[j] = Math.round(_c.r * 255); stateData[j + 1] = Math.round(_c.g * 255); stateData[j + 2] = Math.round(_c.b * 255); stateData[j + 3] = Math.round(a * 255);
        } else { stateData[j] = stateData[j + 1] = stateData[j + 2] = stateData[j + 3] = 0; }
      }
    }
    stateTex.needsUpdate = true;
    world.glow.visible = any;
    wake();
  }

  // ------------------------------------------------------------------------------------------------ вози
  let carts = [], useNear = false;               // { r, s, seat, tone, i, bi, pose } — один на слот
  const cartAt = new Map();                      // № слота → віз
  const _m = new THREE.Matrix4(), _q = new THREE.Quaternion(), _p = new THREE.Vector3(), _s = new THREE.Vector3(), UP = new THREE.Vector3(0, 1, 0);
  const slotPose = (s) => ({ x: wx(s.x), y: (s.y0 + s.y1) / 2 + 0.0135, z: wz(s.y), a: s.a, k: 1 });
  function writeCart(c) {
    const s = c.s, sx = s.len * 0.97 * U * c.pose.k;
    _p.set(c.pose.x, c.pose.y, c.pose.z); _q.setFromAxisAngle(UP, -c.pose.a);
    if (useNear) {
      const sz = Math.min(s.w * 0.98 / 0.25 * U, sx * 1.2), sy = Math.min(sx, sz) * 0.95;
      _s.set(sx, sy, sz); _m.compose(_p, _q, _s);
      world.neutral.setMatrixAt(c.i, _m); world.bodies[c.tone].setMatrixAt(c.bi, _m);
    } else {
      const sz = s.w * 0.98 / 0.25 * U * c.pose.k, sy = Math.min(sx, sz) * 0.8;
      _s.set(sx, sy, sz); _m.compose(_p, _q, _s);
      world.far.setMatrixAt(c.i, _m);
    }
  }
  function flushCarts() {
    if (useNear) { world.neutral.instanceMatrix.needsUpdate = true; for (const b of world.bodies) b.instanceMatrix.needsUpdate = true; }
    else world.far.instanceMatrix.needsUpdate = true;
  }
  function paintCartColors() {
    for (const c of carts) {
      _c.set(seatCol(c.seat));
      if (finaleOn && !finaleOn.winners.has(c.seat)) _c.multiplyScalar(0.5);
      if (useNear) world.bodies[c.tone].setColorAt(c.bi, _c); else world.far.setColorAt(c.i, _c);
    }
    if (useNear) { for (const b of world.bodies) if (b.instanceColor) b.instanceColor.needsUpdate = true; } else if (world.far.instanceColor) world.far.instanceColor.needsUpdate = true;
  }
  function rebuildCarts() {
    if (!world) return;
    const keep = new Map(cartAt);
    carts = []; cartAt.clear();
    const nb = [0, 0, 0, 0, 0], B = [];
    let n = 0;
    for (const [id, seat] of claimed) {
      const r = routes[id]; if (!r) continue;
      for (const s of r.slots) {
        const tone = (seatColor[seat] ?? seat) % 5, old = keep.get(s.i);
        const c = { r, s, seat, tone, i: n++, bi: nb[tone]++, pose: old && old.seat === seat ? old.pose : slotPose(s) };
        carts.push(c); cartAt.set(s.i, c);
      }
      const m = r.slots[r.slots.length >> 1];
      B.push(wx(m.x), (m.y0 + m.y1) / 2 + 0.07, wz(m.y), seatColor[seat] ?? seat);
    }
    world.far.visible = !useNear; world.neutral.visible = useNear; world.bodies.forEach((b) => { b.visible = useNear; });
    world.far.count = useNear ? 0 : n; world.neutral.count = useNear ? n : 0;
    world.bodies.forEach((b, i) => { b.count = useNear ? nb[i] : 0; });
    for (const c of carts) writeCart(c);
    flushCarts(); paintCartColors();
    const at = world.badgeG.getAttribute('aC');
    at.array.set(B); at.needsUpdate = true; world.badgeG.instanceCount = B.length / 4;
    renderer.shadowMap.needsUpdate = true; wake();
  }

  // ------------------------------------------------------------------------------------------------ кільця міст
  const _rm = new THREE.Matrix4();
  function syncRings() {
    if (!world) return;
    const list = [];
    const add = (cityNo, color, grow = 1) => { const c = cities[cityNo]; if (c) list.push([c, color, grow]); };
    for (const t of tickets) { add(t.a, t.done ? '#6f7a5c' : look.hl.plan); add(t.b, t.done ? '#6f7a5c' : look.hl.plan); }
    if (legalC) for (const i of legalC) add(i, look.hl.legal, 1.25);
    if (hoverCity !== null) add(hoverCity, '#ffffff', 1.25);
    if (cursor && cursor.kind === 'city') add(cursor.id, '#ffffff', 1.5);
    for (const [c, color, grow] of ringFlash) list.push([c, color, grow]);
    const R = world.rings; let n = 0;
    for (const [c, color, grow] of list.slice(0, 64)) {
      const rad = (c.r + 13 * grow) * U * (ringK);
      _p.set(wx(c.x), c.h + 0.018, wz(c.y)); _q.identity(); _s.set(rad, 1, rad);
      _m.compose(_p, _q, _s); R.setMatrixAt(n, _m); _c.set(color); R.setColorAt(n, _c); n++;
    }
    R.count = n; R.instanceMatrix.needsUpdate = true; if (R.instanceColor) R.instanceColor.needsUpdate = true;
    wake();
  }
  let ringFlash = [], ringK = 1;

  // ------------------------------------------------------------------------------------------------ підписи
  const labels = cities.map((c) => {
    const el = document.createElement('span'); el.textContent = c.name; overlay.appendChild(el);
    return { c, el, w0: 0, h0: 0, w: 0, h: 0, ox: 0, oy: 0, px: 0, py: 0, rx: 0, ry: 0, vis: true, want: true };
  });
  function styleLabels() {
    const S = look.label, pill = S.bg !== 'none';
    const halo = S.halo === 'transparent' ? '' : `text-shadow:0 0 1px ${S.halo},0 0 2px ${S.halo},0 0 3px ${S.halo};`;
    for (const l of labels) {
      l.el.style.cssText = `position:absolute;left:0;top:0;white-space:nowrap;${hi ? 'will-change:transform;' : ''}transform-origin:0 0;font:${l.c.lvl === 1 ? S.big : S.font};color:${S.color};${halo}` +
        (pill ? `background:${S.bg};padding:${S.pad || '1px 4px'};border-radius:${S.radius}px;box-shadow:${S.shadow || 'none'};` : '');
      l.w0 = 0;
    }
    placed = false;
  }
  /** Міряємо кожен підпис без масштабу: і кадр, і розкладка рахують по цьому. */
  function measureLabels() {
    for (const l of labels) if (!l.w0) { l.el.style.transform = 'none'; l.el.style.display = ''; l.vis = true; const b = l.el.getBoundingClientRect(); l.w0 = b.width || 40; l.h0 = b.height || 14; }
  }
  // Підпис на екрані сталого розміру: на ПК і телефоні трохи росте з наближенням, але назви не стають важчими за мапу.
  const labelScale = (k) => clamp(Math.pow(k / 0.3, 0.3), 0.92, dense ? 1.04 : 1.12);
  let placed = false, labelK = 1;
  const pj = new THREE.Vector3(), _o = { x: 0, y: 0 };
  const project = (x, y, z, out = _o) => { pj.set(x, y, z).project(cam); out.x = (pj.x + 1) / 2 * Wp; out.y = (1 - pj.y) / 2 * Hp; return out; };
  const ANCH = [[0, 1, 0], [0, -1, 1], [1, 0, 1.4], [-1, 0, 1.4], [1, 1, 1.8], [-1, 1, 1.8], [1, -1, 2.3], [-1, -1, 2.3], [0, 2.1, 4], [0, -2.1, 4.5], [1.3, 0, 4.5], [-1.3, 0, 4.5]];
  function layoutLabels() {
    measureLabels();
    for (const l of labels) {
      l.w = l.w0 * labelK; l.h = l.h0 * labelK;
      const c = l.c; project(wx(c.x), c.h + 0.03, wz(c.y)); l.px = _o.x; l.py = _o.y;
      project(wx(c.x + c.r), c.h + 0.03, wz(c.y)); l.rx = Math.abs(_o.x - l.px);
      project(wx(c.x), c.h + 0.03, wz(c.y + c.r)); l.ry = Math.abs(_o.y - l.py);
    }
    // перешкоди: кінці й середини слотів на екрані
    const obs = slotScreen(), phone = dense, wPlate = phone ? 8 : 6;
    // свої замовлення мусять читатись першими: їхні міста йдуть поперед рівня
    const mineC = new Set(); for (const t of tickets) if (!t.done) { mineC.add(t.a); mineC.add(t.b); }
    const rank = (l) => (mineC.has(l.c.i) ? 0 : l.c.lvl);
    const order = labels.slice().sort((a, b) => rank(a) - rank(b) || b.c.r - a.c.r);
    // хто взагалі показаний (§11.3): на вузькому екрані до наближення 1,6 — лише рівень 1 і свої замовлення; далі менший рівень ховається біля більшого
    for (const l of labels) l.want = !phone || l.c.lvl === 1 || mineC.has(l.c.i) || cs.zoom >= 1.6;
    const near = [];
    if (phone) for (const l of order) {
      if (!l.want) continue;
      if (rank(l) > 1 && near.some((o) => Math.hypot(o.px - l.px, o.py - l.py) < 40)) { l.want = false; continue; }
      near.push(l);
    }
    // обраний шлях і показаний шлях замовлення не закриваємо: це те, на що людина зараз дивиться
    const heavy = (i) => { const id = slots[i].route.id; return id === selected ? 90 : path && path.has(id) ? 26 : wPlate; };
    const rect = new Map();
    const costAt = (l, ax, ay, c0) => {
      const gx = ax === 0 ? 0 : ax * (l.rx + 3 + l.w / 2), gy = ay === 0 ? 0 : ay * (l.ry + 2 + l.h / 2);
      const cx = l.px + gx, cy = l.py + gy;
      const x0 = cx - l.w / 2, x1 = cx + l.w / 2, y0 = cy - l.h / 2, y1 = cy + l.h / 2;
      let s = c0;
      if (x0 < 2) s += 500 + (2 - x0); if (x1 > Wp - 2) s += 500 + (x1 - Wp + 2); if (y0 < 2) s += 500; if (y1 > Hp - 2) s += 500;
      for (const r of hud) if (x0 < r.x1 && x1 > r.x0 && y0 < r.y1 && y1 > r.y0) s += 220;
      for (const [m, r] of rect) { if (m === l) continue; if (x0 < r[2] && x1 > r[0] && y0 < r[3] && y1 > r[1]) s += 300; }
      for (const o of labels) {
        if (o === l || !o.want) continue;
        if (x1 > o.px - o.rx && x0 < o.px + o.rx && y1 > o.py - o.ry && y0 < o.py + o.ry) s += 70;
        const dO = Math.hypot(Math.max(x0 - o.px, 0, o.px - x1), Math.max(y0 - o.py, 0, o.py - y1)), dM = Math.hypot(Math.max(x0 - l.px, 0, l.px - x1), Math.max(y0 - l.py, 0, l.py - y1));
        if (dO < dM + 4) s += 40;
      }
      for (let i = 0; i < N; i++) {
        const X0 = obs[i * 5], Y0 = obs[i * 5 + 1], X1 = obs[i * 5 + 2], Y1 = obs[i * 5 + 3];
        for (let k = 0; k < 3; k++) {
          const f = k / 2, X = X0 + (X1 - X0) * f, Y = Y0 + (Y1 - Y0) * f;
          if (X > x0 - 4 && X < x1 + 4 && Y > y0 - 3 && Y < y1 + 3) s += heavy(i);
        }
      }
      return { s, x0, y0, x1, y1 };
    };
    for (let pass = 0; pass < 3; pass++) for (const l of order) {
      if (!l.want) continue;
      let best = null, bs = Infinity;
      for (const a of ANCH) { const r = costAt(l, a[0], a[1], a[2]); if (r.s < bs) { bs = r.s; best = r; } }
      rect.set(l, [best.x0, best.y0, best.x1, best.y1]);
      l.ox = best.x0 - l.px; l.oy = best.y0 - l.py;
    }
    // накладання, яких не уникнути: менш важливий підпис ховається. На вузькому екрані ховається будь-який (спершу дрібні, бо order
    // від важливого), що наїжджає на вже показаний чи вилазить за поле, і підпис ще й не сміє лягти на шлях (§ look).
    const kept = [];
    const onRoutes = (r) => { let n = 0; for (let i = 0; i < N; i++) { const X0 = obs[i * 5], Y0 = obs[i * 5 + 1], X1 = obs[i * 5 + 2], Y1 = obs[i * 5 + 3]; for (let k = 0; k < 3; k++) { const f = k / 2, X = X0 + (X1 - X0) * f, Y = Y0 + (Y1 - Y0) * f; if (X > r[0] && X < r[2] && Y > r[1] && Y < r[3]) n++; } } return n; };
    for (const l of order) {
      if (!l.want) continue;
      const r = rect.get(l);
      const hit = kept.some((q) => r[0] < q[2] && r[2] > q[0] && r[1] < q[3] && r[3] > q[1]);
      const inView = l.px > -40 && l.px < Wp + 40 && l.py > -20 && l.py < Hp + 20;
      if (phone ? inView && (hit || r[0] < 1 || r[2] > Wp - 1 || r[1] < 1 || r[3] > Hp - 1 || (rank(l) > 1 && onRoutes(r) >= 3) || hud.some((q) => r[0] < q.x1 && r[2] > q.x0 && r[1] < q.y1 && r[3] > q.y0)) : hit && !mineC.has(l.c.i)) { l.want = false; continue; }
      kept.push(r);
    }
    placed = true;
  }
  function updateLabels(relayout) {
    labelK = labelScale(pxPerUnit);
    if (relayout || !placed) layoutLabels();
    for (const l of labels) { project(wx(l.c.x), l.c.h + 0.03, wz(l.c.y)); l.px = _o.x; l.py = _o.y; }
    for (const l of labels) {
      const x = l.px + l.ox, y = l.py + l.oy;
      // На телефоні обрізану при краї назву не показуємо: місце їй знайдеться, щойно камера стане (layoutLabels).
      const vis = l.want && (dense ? x >= 0 && y >= 0 && x + l.w <= Wp && y + l.h <= Hp : x > -l.w && y > -20 && x < Wp && y < Hp);
      if (vis !== l.vis) { l.el.style.display = vis ? '' : 'none'; l.vis = vis; }
      if (vis) l.el.style.transform = `translate3d(${x.toFixed(1)}px,${y.toFixed(1)}px,0) scale(${labelK.toFixed(2)})`;
    }
  }

  // ------------------------------------------------------------------------------------------------ камера
  let Wp = 0, Hp = 0, baseDist = 12, tx1 = 0, tz1 = 0, pxPerUnit = 0.3, camVer = 0;
  let hud = [], hudSig = '', crop = opts.crop ?? (host.clientWidth < 600 && host.clientHeight > host.clientWidth), home = [];
  let dense = opts.dense ?? crop;                // телефон (і лежачи): підписів рівно стільки, скільки вміщається без наїзду
  const cs = { az: 0, pol: POL_DEF, zoom: 1, tx: 0, tz: 0 }, def = { az: 0, pol: POL_DEF, zoom: 1, tx: 0, tz: 0 };
  const vel = { x: 0, z: 0 };
  let touched = false, lastUserMove = -1e9;
  const tanF = Math.tan(FOV * D2R / 2);
  function applyCam() {
    const dist = baseDist / cs.zoom, s = Math.sin(cs.pol), c = Math.cos(cs.pol);
    cam.position.set(cs.tx + dist * s * Math.sin(cs.az), dist * c, cs.tz + dist * s * Math.cos(cs.az));
    cam.lookAt(cs.tx, 0, cs.tz);
    cam.near = Math.max(0.2, dist * 0.2); cam.far = dist * 4 + 40;
    cam.updateProjectionMatrix(); cam.updateMatrixWorld();
    pxPerUnit = Hp / (2 * dist * tanF) * U;
    camVer++;
    shared.uBadgePx.value = clamp(pxPerUnit * 21, 4, 10);
    if (world) {
      const lim = hi ? NEAR_K.hi : NEAR_K.lo, near = useNear ? pxPerUnit >= lim[1] : pxPerUnit >= lim[0];
      if (near !== useNear) { useNear = near; rebuildCarts(); }
    }
  }
  // Точки кадру: міста з колом і половиною підпису, кінці слотів, крайні точки країни (ті лише не сміють вилізти за поле).
  const framePts = (() => {
    const out = [];
    for (const c of cities) out.push({ x: wx(c.x), y: c.h, z: wz(c.y), r: c.r, c });
    for (const s of slots) for (const q of [s.P, s.Q]) out.push({ x: wx(q[0]), y: (s.y0 + s.y1) / 2, z: wz(q[1]), r: s.w / 2 });
    for (const [x, y] of [[BOX.x0, BOX.y0], [BOX.x1, BOX.y0], [BOX.x1, BOX.y1], [BOX.x0, BOX.y1]]) out.push({ x: wx(x), y: 0, z: wz(y), r: 0, soft: true });
    return out;
  })();
  const _fv = new THREE.Vector3();
  function aimFit(dist, tx, tz) {
    const s = Math.sin(POL_DEF), c = Math.cos(POL_DEF);
    cam.position.set(tx, dist * c, tz + dist * s); cam.lookAt(tx, 0, tz);
    cam.near = Math.max(0.2, dist * 0.2); cam.far = dist * 4 + 40; cam.updateProjectionMatrix(); cam.updateMatrixWorld();
  }
  /** Рамка всіх точок на екрані за такої камери. Для підписів — половина ширини, щоб назва лишалась у полі (§11.2). */
  function frameBox(dist, tx, tz) {
    aimFit(dist, tx, tz);
    const k = Hp / (2 * dist * tanF) * U, K = labelScale(k);
    let x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
    for (const p of framePts) {
      _fv.set(p.x, p.y, p.z).project(cam);
      const sx = (_fv.x + 1) / 2 * Wp, sy = (1 - _fv.y) / 2 * Hp, rp = p.r * k;
      const half = p.c && labels[p.c.i] ? Math.max(rp, labels[p.c.i].w0 * K / 2) : rp;
      x0 = Math.min(x0, sx - half); x1 = Math.max(x1, sx + half); y0 = Math.min(y0, sy - rp); y1 = Math.max(y1, sy + rp);
    }
    return { x0, x1, y0, y1 };
  }
  /** Жодного міста й слота під HUD (коло міста із запасом на око). */
  function clearOfHud(dist, tx, tz) {
    if (!hud.length) return true;
    aimFit(dist, tx, tz);
    const k = Hp / (2 * dist * tanF) * U;
    for (const p of framePts) {
      if (p.soft) continue;
      _fv.set(p.x, p.y, p.z).project(cam);
      const sx = (_fv.x + 1) / 2 * Wp, sy = (1 - _fv.y) / 2 * Hp, pad = p.r * k + 3;
      for (const r of hud) if (sx + pad > r.x0 && sx - pad < r.x1 && sy + pad > r.y0 && sy - pad < r.y1) return false;
    }
    return true;
  }
  /** Центр рамки посередині поля (перспектива нелінійна: кілька кроків). */
  function centered(dist, tz0) {
    let tx = 0, tz = tz0, b = null;
    const c = Math.cos(POL_DEF);
    for (let i = 0; i < 4; i++) {
      b = frameBox(dist, tx, tz);
      const k = Hp / (2 * dist * tanF);
      tx += ((b.x0 + b.x1) / 2 - Wp / 2) / k;
      tz += ((b.y0 + b.y1) / 2 - Hp / 2) / k / c;
    }
    b = frameBox(dist, tx, tz);
    return { tx, tz, b };
  }
  /**
   * Наближення 1 — найближча камера, за якої видно все й нічого не лежить під HUD. Спершу найближча без HUD, далі крок за кроком
   * відходимо й у вільному зазорі шукаємо зсув, найближчий до центру.
   */
  function solveFit() {
    let lo = 2, hi_ = 80, at = null;
    for (let i = 0; i < 18; i++) {
      const d = (lo + hi_) / 2, q = centered(d, 0);
      if (q.b.x1 - q.b.x0 <= Wp - 2 * EDGE_PX && q.b.y1 - q.b.y0 <= Hp - 2 * EDGE_PX) { hi_ = d; at = q; } else lo = d;
    }
    baseDist = hi_; at = at || centered(hi_, 0); tx1 = at.tx; tz1 = at.tz;
    if (!hud.length || dense) return;              // на телефоні HUD кадр не рухає, лише назви тікають із-під кнопок
    const c = Math.cos(POL_DEF), d0 = hi_, offs = (n) => Array.from({ length: 2 * n + 1 }, (_, i) => (i - n) / n).sort((a, b) => Math.abs(a) - Math.abs(b));
    const ox = offs(6), oz = offs(5);
    for (let step = 0; step < 34; step++) {
      const d = d0 * (1 + step * 0.015), q = centered(d, tz1), k = Hp / (2 * d * tanF);
      const sx = Math.max(0, (Wp - 2 * EDGE_PX - (q.b.x1 - q.b.x0)) / 2), sz = Math.max(0, (Hp - 2 * EDGE_PX - (q.b.y1 - q.b.y0)) / 2);
      for (const dz of oz) for (const dx of ox) {
        const tx = q.tx + dx * sx / k, tz = q.tz + dz * sz / k / c;
        if (clearOfHud(d, tx, tz)) { baseDist = d; tx1 = tx; tz1 = tz; return; }
      }
    }
  }
  /** Телефон: читабельна ділянка навколо замовлень; ⌂ перемикає між нею і всією мапою. */
  function cropDef() {
    const k1 = Hp / (2 * baseDist * tanF) * U, fitW = Wp / k1;
    const q = homeCrop(home, CROP_W, CROP_W * Hp / Wp, CROP_MAX);
    if (!q || q.w >= fitW * 0.92) { def.zoom = 1; def.tx = tx1; def.tz = tz1; return; }
    def.zoom = clamp(fitW / q.w, 1, ZOOM_MAX); def.tx = wx(q.x); def.tz = wz(q.y);
    clampPan(def);
  }
  function fit() {
    if (look) measureLabels();
    const was = { ...cs };
    solveFit();
    def.az = 0; def.pol = POL_DEF; def.zoom = 1; def.tx = tx1; def.tz = tz1;
    if (crop) cropDef();
    if (touched) Object.assign(cs, was); else Object.assign(cs, def);
    clampPan(); applyCam();
  }
  function clampPan(q = cs) {
    const dist = baseDist / q.zoom, hd = dist * tanF / Math.cos(q.pol), hw = dist * tanF * (Wp / Hp), A = W * U / 2, B = H * U / 2;
    const lx = Math.max(A - hw * (1 - 2 * VOID), 0), lz = Math.max(B - hd * (1 - 2 * VOID), 0);
    q.tx = clamp(q.tx, tx1 - lx, tx1 + lx);
    q.tz = clamp(q.tz, tz1 - lz, tz1 + lz);
  }
  function resize() {
    const w = Math.max(1, host.clientWidth | 0), h = Math.max(1, host.clientHeight | 0);
    if (w === Wp && h === Hp) return;
    Wp = w; Hp = h; renderer.setSize(w, h, false); cam.aspect = w / h; cam.updateProjectionMatrix();
    shared.uRes.value.set(w, h);
    fit(); placed = false; updateLabels(true);
  }
  const rayPlane = (x, y) => {
    pj.set(x / Wp * 2 - 1, 1 - y / Hp * 2, 0.5).unproject(cam);
    const dx = pj.x - cam.position.x, dy = pj.y - cam.position.y, dz = pj.z - cam.position.z, t = -cam.position.y / dy;
    return { x: cam.position.x + dx * t, z: cam.position.z + dz * t };
  };
  function zoomAround(x, y, z) {
    const a = rayPlane(x, y);
    cs.zoom = clamp(z, 1, ZOOM_MAX); applyCam();
    const b = rayPlane(x, y);
    cs.tx += a.x - b.x; cs.tz += a.z - b.z; clampPan(); applyCam();
  }
  let camTween = null;
  function flyTo(to, ms = 520) {
    if (camTween) camTween.cancel();
    const from = { ...cs };
    if (reduced || document.hidden) { Object.assign(cs, to); clampPan(); camMoved(true); return; }
    camTween = tween(ms, (k) => {
      const e = easeInOut(k);
      for (const key of ['az', 'pol', 'tx', 'tz']) cs[key] = from[key] + (to[key] - from[key]) * e;
      cs.zoom = Math.exp(Math.log(from.zoom) + (Math.log(to.zoom) - Math.log(from.zoom)) * e);
      applyCam(); updateLabels(false);
    }, () => { camTween = null; clampPan(); camMoved(true); });
  }
  let settleT = 0;
  function camMoved(now) {
    applyCam();
    updateLabels(false);
    clearTimeout(settleT);
    const settle = () => { if (alive) { updateLabels(true); wake(); } };
    if (now) settle(); else settleT = setTimeout(settle, 130);
    wake();
  }
  function userMoved() { touched = true; lastUserMove = performance.now(); if (camTween) camTween.cancel(); camMoved(false); }
  const allView = () => ({ az: 0, pol: POL_DEF, zoom: 1, tx: tx1, tz: tz1 });
  /** ⌂: додому. На телефоні «дім» — ділянка замовлень: уже там — наступний дотик показує всю мапу. */
  function resetView() {
    vel.x = vel.z = 0;
    const atHome = !touched && Math.abs(cs.zoom - def.zoom) < 0.03 && Math.abs(cs.tx - def.tx) < 0.05 && Math.abs(cs.tz - def.tz) < 0.05;
    if (crop && atHome && def.zoom > 1.03) { touched = true; flyTo(allView()); return; }
    touched = false;
    flyTo({ ...def });
  }
  function bboxView(x0, y0, x1, y1, minZ = 1) {
    const dist1 = baseDist, hd = dist1 * tanF / Math.cos(POL_DEF), hw = dist1 * tanF * (Wp / Hp);
    const z = clamp(Math.min(2 * hw / (Math.max(x1 - x0, 300) * U * 1.5), 2 * hd / (Math.max(y1 - y0, 300) * U * 1.5)), minZ, 4.5);
    return { az: cs.az, pol: cs.pol, zoom: z, tx: wx((x0 + x1) / 2), tz: wz((y0 + y1) / 2) };
  }
  function focus(kind, id) {
    if (performance.now() - lastUserMove < HANDS_OFF_MS) return;
    if (kind === 'all') { flyTo(allView()); return; }
    let box = null;
    if (kind === 'route' && routes[id]) { let a = 1e9, b = 1e9, c = -1e9, d = -1e9; for (const p of routes[id].samples) { a = Math.min(a, p[0]); b = Math.min(b, p[1]); c = Math.max(c, p[0]); d = Math.max(d, p[1]); } box = [a, b, c, d]; }
    else if (kind === 'city' && cities[id]) { const q = cities[id]; flyTo({ az: cs.az, pol: cs.pol, zoom: Math.max(cs.zoom, 2.4), tx: wx(q.x), tz: wz(q.y) }); return; }
    else if (kind === 'cities' && Array.isArray(id)) { const q = id.map((i) => cities[i]).filter(Boolean); if (q.length) box = [Math.min(...q.map((c) => c.x)), Math.min(...q.map((c) => c.y)), Math.max(...q.map((c) => c.x)), Math.max(...q.map((c) => c.y))]; }
    if (box) flyTo(bboxView(box[0], box[1], box[2], box[3], Math.max(1, Math.min(cs.zoom, 2))));
  }

  // ------------------------------------------------------------------------------------------------ попадання: лише пікселі
  let scr = new Float32Array(N * 5), scrVer = -1;
  const _a = { x: 0, y: 0 }, _b = { x: 0, y: 0 };
  function slotScreen() {
    if (scrVer === camVer) return scr;
    scrVer = camVer;
    const pxW = pxPerUnit;
    for (let i = 0; i < N; i++) {
      const s = slots[i], h = (s.y0 + s.y1) / 2 + 0.013;
      project(wx(s.P[0]), h, wz(s.P[1]), _a); project(wx(s.Q[0]), h, wz(s.Q[1]), _b);
      scr[i * 5] = _a.x; scr[i * 5 + 1] = _a.y; scr[i * 5 + 2] = _b.x; scr[i * 5 + 3] = _b.y; scr[i * 5 + 4] = s.w / 2 * pxW;
    }
    return scr;
  }
  let lastPointerType = 'mouse';
  const radiusNow = () => (lastPointerType === 'mouse' ? R_MOUSE : R_TOUCH);
  /** [[id, відстань]] шляхів у радіусі, найближчий перший. */
  function routesNear(x, y, rad) {
    const o = slotScreen(), best = new Map();
    for (let i = 0; i < N; i++) {
      const X0 = o[i * 5], Y0 = o[i * 5 + 1], dx = o[i * 5 + 2] - X0, dy = o[i * 5 + 3] - Y0, l2 = dx * dx + dy * dy;
      const t = l2 > 0 ? clamp(((x - X0) * dx + (y - Y0) * dy) / l2, 0, 1) : 0;
      const d = Math.max(0, Math.hypot(x - X0 - t * dx, y - Y0 - t * dy) - o[i * 5 + 4]);
      if (d > rad) continue;
      const id = slots[i].route.id, cur = best.get(id);
      if (cur === undefined || d < cur) best.set(id, d);
    }
    return [...best.entries()].sort((a, b) => a[1] - b[1]);
  }
  function cityAt(x, y) {
    let found = null, bd = Infinity;
    for (const c of cities) {
      project(wx(c.x), c.h, wz(c.y));
      const d = Math.hypot(_o.x - x, _o.y - y), r = Math.max(c.r * pxPerUnit + 2, 7);
      if (d <= r && d < bd) { bd = d; found = c; }
    }
    return found;
  }
  function hitAt(x, y) {
    const rad = radiusNow(), near = routesNear(x, y, rad), c = cityAt(x, y);
    if (legalR) { const l = near.find(([id]) => legalR.has(id)); if (l && !(c && legalC && legalC.has(c.i))) return { kind: 'route', id: l[0] }; }
    if (legalC && c && legalC.has(c.i)) return { kind: 'city', id: c.i };
    if (c) return { kind: 'city', id: c.i };
    if (near.length) return { kind: 'route', id: near[0][0] };
    return null;
  }

  // ------------------------------------------------------------------------------------------------ жести
  const ptrs = new Map(); let drag = null, pinch = null, lastTap = { t: 0, kind: null, id: null }, hoverKey = '';
  const local = (e) => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  function setHover(h) {
    const key = h ? h.kind + ':' + h.id : '';
    if (key === hoverKey) return;
    hoverKey = key;
    hover = h && h.kind === 'route' ? h.id : null;
    hoverCity = h && h.kind === 'city' ? h.id : null;
    syncState(); syncRings();
    canvas.style.cursor = h ? 'pointer' : 'grab';
    onHover(h ? h.kind : null, h ? h.id : null);
  }
  const startPinch = () => { const [a, b] = [...ptrs.values()]; pinch = { a: { ...a }, b: { ...b }, rotAcc: 0, rotating: false }; drag = null; vel.x = vel.z = 0; };
  function onDown(e) {
    lastPointerType = e.pointerType || 'mouse';
    try { canvas.setPointerCapture(e.pointerId); } catch { /* миша вже пішла */ }
    const [x, y] = local(e);
    ptrs.set(e.pointerId, { x, y });
    vel.x = vel.z = 0;
    if (camTween) camTween.cancel();
    if (ptrs.size === 1) drag = { x0: x, y0: y, x, y, moved: false, orbit: e.button === 2 || e.shiftKey, a: rayPlane(x, y), lt: performance.now() };
    else if (ptrs.size === 2) startPinch();
    canvas.style.cursor = 'grabbing';
  }
  function onMove(e) {
    lastPointerType = e.pointerType || 'mouse';
    const [x, y] = local(e);
    if (!ptrs.has(e.pointerId)) { if (lastPointerType === 'mouse') setHover(hitAt(x, y)); return; }
    ptrs.get(e.pointerId).x = x; ptrs.get(e.pointerId).y = y;
    if (ptrs.size >= 2 && pinch) {
      const [a, b] = [...ptrs.values()], g = twoFinger(pinch.a, pinch.b, a, b), mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
      zoomAround(mx, my, cs.zoom * g.scale);
      if (pinch.rotating) cs.az = clamp(cs.az + g.rot, -AZ_MAX, AZ_MAX);
      else if (Math.abs(pinch.rotAcc += g.rot) > 0.14) pinch.rotating = true;
      // зсув двома пальцями — і зсув карти
      const p0 = rayPlane(mx - g.dx, my - g.dy), p1 = rayPlane(mx, my);
      cs.tx += p0.x - p1.x; cs.tz += p0.z - p1.z; clampPan();
      pinch.a = { ...a }; pinch.b = { ...b };
      userMoved();
      return;
    }
    if (!drag) return;
    if (!drag.moved && Math.hypot(x - drag.x0, y - drag.y0) < TAP_PX) return;
    if (!drag.moved) { drag.moved = true; setHover(null); }
    if (drag.orbit) {
      cs.az = clamp(cs.az + (x - drag.x) * 0.004, -AZ_MAX, AZ_MAX);
      cs.pol = clamp(cs.pol - (y - drag.y) * 0.004, 0, POL_MAX);
    } else {
      const p = rayPlane(x, y), dx = drag.a.x - p.x, dz = drag.a.z - p.z, now = performance.now(), dt = Math.max(1, now - drag.lt);
      const ox = cs.tx, oz = cs.tz;
      cs.tx += dx; cs.tz += dz; clampPan();
      vel.x = 0.6 * vel.x + 0.4 * (cs.tx - ox) / dt; vel.z = 0.6 * vel.z + 0.4 * (cs.tz - oz) / dt;
      drag.lt = now;
    }
    drag.x = x; drag.y = y;
    userMoved();
  }
  function onUp(e) {
    const p = ptrs.get(e.pointerId);
    ptrs.delete(e.pointerId);
    try { canvas.releasePointerCapture(e.pointerId); } catch { /* вже відпущено */ }
    if (ptrs.size < 2) pinch = null;
    if (ptrs.size >= 2) { startPinch(); return; }
    if (ptrs.size === 1) { const [q] = [...ptrs.values()]; drag = { x0: q.x, y0: q.y, x: q.x, y: q.y, moved: true, orbit: false, a: rayPlane(q.x, q.y), lt: performance.now() }; return; }
    canvas.style.cursor = 'grab';
    if (!drag || !p || e.type === 'pointercancel') { drag = null; return; }
    const d = drag; drag = null;
    if (d.moved) { if (performance.now() - d.lt > 80) vel.x = vel.z = 0; wake(); return; }
    if (d.orbit) return;
    const hit = hitAt(p.x, p.y), now = performance.now(), same = lastTap.kind === (hit && hit.kind) && lastTap.id === (hit && hit.id) && now - lastTap.t < DBL_MS;
    if (same && (!hit || hit.kind === 'city')) {
      lastTap = { t: 0 };
      if (!hit) { resetView(); return; }
      touched = true; lastUserMove = now;
      const c = cities[hit.id]; project(wx(c.x), c.h, wz(c.y));
      flyTo({ az: cs.az, pol: cs.pol, zoom: clamp(cs.zoom * 2, 1, ZOOM_MAX), tx: wx(c.x), tz: wz(c.y) });
      return;
    }
    lastTap = { t: now, kind: hit && hit.kind, id: hit && hit.id };
    if (hit) onPick(hit.kind, hit.id); else onPick(null, null);
  }
  function onWheel(e) {
    e.preventDefault();
    const [x, y] = local(e);
    zoomAround(x, y, cs.zoom * wheelZoomFactor(e.deltaY, e.deltaMode));
    userMoved();
  }
  const onLeave = () => { if (!ptrs.size) setHover(null); };
  const noCtx = (e) => e.preventDefault();
  const noGesture = (e) => e.preventDefault();
  canvas.addEventListener('pointerdown', onDown);
  canvas.addEventListener('pointermove', onMove);
  canvas.addEventListener('pointerup', onUp);
  canvas.addEventListener('pointercancel', onUp);
  canvas.addEventListener('pointerleave', onLeave);
  canvas.addEventListener('wheel', onWheel, { passive: false });
  canvas.addEventListener('contextmenu', noCtx);
  canvas.addEventListener('gesturestart', noGesture);
  canvas.addEventListener('gesturechange', noGesture);
  canvas.style.cursor = 'grab';

  // ------------------------------------------------------------------------------------------------ анімації
  const tweens = new Set();
  let slowK = 1;                                 // лише стенд: розтягнути анімації, щоб роздивитись
  function tween(ms, fn, onDone) {
    const tw = { t0: performance.now(), ms: Math.max(1, ms * slowK), fn, onDone, done: false, resolve: null, cancel() { if (!tw.done) { tw.done = true; tweens.delete(tw); tw.resolve(); } } };
    tw.promise = new Promise((r) => { tw.resolve = r; });
    if (document.hidden) { finishTween(tw); return tw; }
    tweens.add(tw); wake();
    return tw;
  }
  function finishTween(tw) { if (tw.done) return; tw.done = true; tweens.delete(tw); tw.fn(1); if (tw.onDone) tw.onDone(); tw.resolve(); }
  function runTweens(now) {
    let any = false;
    for (const tw of [...tweens]) {
      const k = clamp((now - tw.t0) / tw.ms, 0, 1);
      if (k >= 1) finishTween(tw); else { tw.fn(k); any = true; }
    }
    return any || tweens.size > 0;
  }
  const dur = (ms) => (reduced ? Math.min(ms, 150) : ms);

  /** Вози виїжджають із міста a по черзі, перший — у найдальший слот; колір власника наростає, коли віз став. */
  function claim(ev) {
    const r = routes[ev && ev.route]; if (!r) return Promise.resolve();
    const seat = ev.seat;
    claimed.set(r.id, seat);
    lastRoute[seat] = r.id;
    for (const s of r.slots) baseOver.set(s.i, 0);
    rebuildCarts();
    if (reduced || document.hidden) {
      for (const s of r.slots) baseOver.delete(s.i);
      syncState(); renderer.shadowMap.needsUpdate = true;
      return document.hidden ? Promise.resolve() : tween(120, () => {}).promise;
    }
    const n = r.slots.length, T = Math.min(1200, 480 + 120 * n), gap = (T * 0.42) / Math.max(1, n), travel = T - gap * (n - 1);
    const startS = Math.max(0, r.A.r * 0.4);
    for (const s of r.slots) { const c = cartAt.get(s.i); c.pose = { ...c.pose, k: 0 }; writeCart(c); }
    flushCarts();
    return tween(T, (k) => {
      const t = k * T;
      r.slots.forEach((s, idx) => {
        const order = n - 1 - idx, f = clamp((t - order * gap) / travel, 0, 1), c = cartAt.get(s.i);
        if (!c) return;
        if (f <= 0) { c.pose.k = 0; writeCart(c); return; }
        const e = easeOut(f), d = startS + (s.m - startS) * e, q = atS(r, d), h = heightAt(geo, q.x, q.y) + 0.0135;
        const end = (s.y0 + s.y1) / 2 + 0.0135, hop = Math.abs(Math.sin(f * 14)) * 0.006 * (1 - f);
        c.pose = { x: wx(q.x), y: Math.max(h, end * e + h * (1 - e)) + hop, z: wz(q.y), a: Math.atan2(q.ty, q.tx), k: Math.min(1, f * 5) };
        if (f >= 1) c.pose = slotPose(s);
        writeCart(c);
        const a = clamp((f - 0.62) / 0.38, 0, 1);
        baseOver.set(s.i, a * 0.92);
        if (a > 0 && a < 1) flash.set(s.i, { kind: KIND.flash, a: (1 - a) * 0.9 }); else flash.delete(s.i);
      });
      flushCarts(); syncState();
    }, () => {
      for (const s of r.slots) { baseOver.delete(s.i); flash.delete(s.i); }
      for (const s of r.slots) { const c = cartAt.get(s.i); if (c) { c.pose = slotPose(s); writeCart(c); } }
      flushCarts(); syncState();
      renderer.shadowMap.needsUpdate = true;
    }).promise;
  }

  /** Шлях між містами замовлення мережею власника (лише для сяйва; правила на сервері). */
  function ownPath(seat, a, b) {
    const adj = new Map();
    for (const [id, s] of claimed) if (s === seat) { const r = routes[id]; (adj.get(r.a) || adj.set(r.a, []).get(r.a)).push([r.b, id]); (adj.get(r.b) || adj.set(r.b, []).get(r.b)).push([r.a, id]); }
    const prev = new Map([[a, null]]), q = [a];
    while (q.length) { const v = q.shift(); if (v === b) break; for (const [u, id] of adj.get(v) || []) if (!prev.has(u)) { prev.set(u, [v, id]); q.push(u); } }
    if (!prev.has(b)) return [];
    const o = []; for (let v = b; prev.get(v); v = prev.get(v)[0]) o.unshift(prev.get(v)[1]);
    return o;
  }
  function ticketDone(ev) {
    const t = ticketOf(ev && ev.ticket); if (!t) return Promise.resolve();
    const seat = ev.seat, ids = ownPath(seat, t.a, t.b), ss = ids.flatMap((id) => routes[id].slots);
    ringFlash = [[cities[t.a], look.hl.sel, 1.4], [cities[t.b], look.hl.sel, 1.4]];
    return tween(dur(950), (k) => {
      const a = Math.sin(k * Math.PI) * (reduced ? 1 : 0.6 + 0.4 * Math.sin(k * Math.PI * 4));
      ringK = 1 + a * 0.45;
      for (const s of ss) flash.set(s.i, { kind: KIND.sel, a: clamp(a, 0, 1) });
      syncState(); syncRings();
    }, () => { ringFlash = []; ringK = 1; for (const s of ss) flash.delete(s.i); syncState(); syncRings(); }).promise;
  }
  /** Хвиля від того, хто запустив, і міста блимають. */
  function last(ev) {
    const seat = ev && ev.seat, r = routes[lastRoute[seat]];
    const ox = r ? r.slots[r.slots.length >> 1].x : W / 2, oy = r ? r.slots[r.slots.length >> 1].y : H / 2;
    const wave = ensureWave(), far_ = 11;
    wave.material.uniforms.uO.value.set(wx(ox), wz(oy)); wave.material.uniforms.uC.value.set(look.hl.near); wave.visible = true;
    ringFlash = cities.filter((c) => hi || c.lvl === 1).map((c) => [c, look.hl.near, 0.9]);
    ringK = 1;
    return tween(dur(950), (k) => {
      wave.material.uniforms.uR.value = far_ * k;
      wave.material.uniforms.uA.value = (1 - k * k) * 0.8;
      const blink = Math.sin(k * Math.PI * 3);
      if (ringFlash.length && (blink > 0) !== ringOn) { ringOn = blink > 0; world.rings.visible = ringOn; }
      syncRings();
    }, () => { wave.visible = false; ringFlash = []; world.rings.visible = true; ringOn = true; syncRings(); }).promise;
  }
  let ringOn = true, waveMesh = null;
  function ensureWave() {
    if (waveMesh) return waveMesh;
    const g = new THREE.PlaneGeometry(40, 30); g.rotateX(-Math.PI / 2);
    const m = new THREE.ShaderMaterial({
      transparent: true, depthWrite: false, depthTest: false, toneMapped: false,
      uniforms: { uO: { value: new THREE.Vector2() }, uR: { value: 0 }, uA: { value: 0 }, uC: { value: new THREE.Color('#fff4c8') } },
      vertexShader: 'varying vec2 vP; void main(){ vP = position.xz; gl_Position = projectionMatrix*modelViewMatrix*vec4(position.x,0.06,position.z,1.); }',
      fragmentShader: `uniform vec2 uO; uniform float uR,uA; uniform vec3 uC; varying vec2 vP;
        void main(){ float d = abs(length(vP-uO)-uR); float a = (1.-smoothstep(0.,0.16,d))*uA + (1.-smoothstep(0.,1.2,d))*uA*0.3; gl_FragColor = vec4(uC,a);
          #include <colorspace_fragment>
        }`,
    });
    waveMesh = new THREE.Mesh(g, m); waveMesh.frustumCulled = false; waveMesh.renderOrder = 9; waveMesh.visible = false; scene.add(waveMesh);
    return waveMesh;
  }
  /** Порядок слотів дороги від кінця до кінця. */
  function roadSlots(ids) {
    const list = ids.map((id) => routes[id]).filter(Boolean), out = [];
    let at = null;
    list.forEach((r, i) => {
      let forward = true;
      if (at !== null) forward = r.a === at;
      else if (list[i + 1]) { const nx = list[i + 1]; forward = r.b === nx.a || r.b === nx.b; }
      at = forward ? r.b : r.a;
      out.push(...(forward ? r.slots : [...r.slots].reverse()));
    });
    return out;
  }
  function longest(ev) {
    const groups = ((ev && ev.seats) || Object.keys((ev && ev.routes) || {})).map((s) => ((ev.routes || {})[s]) || []);
    const all = groups.flat();
    if (!all.length) return Promise.resolve();
    path = new Set(); syncState();
    const seqs = groups.map(roadSlots), lit = new Set();
    return tween(dur(1900), (k) => {
      let again = false;
      seqs.forEach((q) => {
        const head = k * (q.length + 3);
        q.forEach((s, i) => {
          const d = head - i;
          if (d >= 0) { lit.add(s.route.id); }
          if (d >= 0 && d < 3) { flash.set(s.i, { kind: KIND.flash, a: 1 - d / 3 }); again = true; } else flash.delete(s.i);
        });
      });
      path = new Set(lit);
      if (again || k < 1) syncState();
    }, () => { flash.clear(); path = new Set(all); syncState(); }).promise;
  }
  /** Переможцеві — стрічки й світло на дорогу, решта тьмяніє. Лишається до setState без `over`. */
  function finale(over) {
    const winners = new Set((over && over.winners) || []);
    const road = over && over.longest && over.longest.routes ? [...winners].flatMap((s) => over.longest.routes[s] || []) : [];
    const ids = road.length ? road : [...claimed].filter(([, s]) => winners.has(s)).map(([id]) => id);
    finaleOn = { winners };
    path = new Set(ids);
    paintCartColors(); syncState();
    const rb = reduced ? null : ensureRibbons(winners);
    if (rb) { rb.visible = true; rb.material.uniforms.uT.value = 0; }
    const k0 = dimLights;
    return tween(dur(2400), (k) => {
      setLightK(k0 + (0.62 - k0) * clamp(k * 3, 0, 1));
      if (rb) rb.material.uniforms.uT.value = k * 2.4;
    }, () => { if (rb) rb.visible = false; setLightK(0.62); wake(); }).promise;
  }
  let ribbons = null;
  function ensureRibbons(winners) {
    const n = hi ? 110 : 40;
    if (ribbons) { scene.remove(ribbons); ribbons.geometry.dispose(); ribbons.material.dispose(); }
    const g = new THREE.InstancedBufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute([-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0], 3)); g.setIndex([0, 1, 2, 0, 2, 3]);
    const A = new Float32Array(n * 4), C = new Float32Array(n * 3), cols = [...[...winners].map((s) => seatCol(s)), '#ffd34d', '#ffffff', '#d9301f'], c = new THREE.Color();
    for (let i = 0; i < n; i++) { const r = (x) => { const s = Math.sin(i * 12.9898 + x * 78.233) * 43758.5453; return s - Math.floor(s); }; A[i * 4] = r(1) * 2.2 - 1.1; A[i * 4 + 1] = r(2) * 0.9; A[i * 4 + 2] = 0.8 + r(3) * 0.8; A[i * 4 + 3] = r(4) * 6.28; c.set(cols[i % cols.length]); C[i * 3] = c.r; C[i * 3 + 1] = c.g; C[i * 3 + 2] = c.b; }
    g.setAttribute('aP', new THREE.InstancedBufferAttribute(A, 4)); g.setAttribute('aC', new THREE.InstancedBufferAttribute(C, 3)); g.instanceCount = n;
    const m = new THREE.ShaderMaterial({
      transparent: true, depthTest: false, depthWrite: false, toneMapped: false, side: THREE.DoubleSide,
      uniforms: { uT: { value: 0 }, uAsp: { value: Hp / Wp } },
      vertexShader: `attribute vec4 aP; attribute vec3 aC; uniform float uT, uAsp; varying vec3 vC; varying float vA;
        void main(){ float t = max(0., uT - aP.y); float y = 1.15 - t*aP.z*1.1; float x = aP.x + sin(t*3.+aP.w)*0.05;
          float a = aP.w + t*(2.+aP.z*2.); vec2 s = vec2(0.012, 0.042); vec2 q = position.xy*s; q = vec2(q.x*cos(a)-q.y*sin(a), q.x*sin(a)+q.y*cos(a)); q.x *= uAsp;
          vC = aC*(0.75+0.25*sin(a*2.)); vA = smoothstep(2.4,1.9,uT);
          gl_Position = vec4(x+q.x, y+q.y, 0., 1.); }`,
      fragmentShader: `varying vec3 vC; varying float vA; void main(){ gl_FragColor = vec4(vC, vA);
        #include <colorspace_fragment>
      }`,
    });
    ribbons = new THREE.Mesh(g, m); ribbons.frustumCulled = false; ribbons.renderOrder = 10; ribbons.visible = false; scene.add(ribbons);
    return ribbons;
  }

  // ------------------------------------------------------------------------------------------------ цикл
  let raf = 0, dirty = true, clockT = 0, lastNow = performance.now(), lastRender = 0, lastActive = performance.now();
  const stats = { frames: 0, shadowFrames: 0 };
  const bgAnim = hi && !reduced;
  const pulsing = () => hi && !reduced && (!!legalR || !!(marks && Object.values(marks).some((m) => m === 'near' || m === 'ok')));
  function frame(now) {
    raf = 0;
    if (!alive || document.hidden) return;
    const dt = Math.min(0.05, Math.max(0, (now - lastNow) / 1000));
    lastNow = now; clockT += dt;
    let active = false;
    if (!ptrs.size && (Math.abs(vel.x) > 1e-5 || Math.abs(vel.z) > 1e-5)) {
      const ms = dt * 1000, ox = cs.tx, oz = cs.tz;
      cs.tx += vel.x * ms; cs.tz += vel.z * ms; clampPan();
      const f = Math.exp(-dt * 6);
      vel.x = cs.tx === ox ? 0 : vel.x * f; vel.z = cs.tz === oz ? 0 : vel.z * f;
      if (Math.abs(vel.x) < 1e-5) vel.x = 0;
      if (Math.abs(vel.z) < 1e-5) vel.z = 0;
      camMoved(false); active = true;
    }
    if (runTweens(now)) active = true;
    const pulse = pulsing(), bg = bgAnim || pulse;
    const due = bg && now - lastRender >= 32;
    if (active || dirty || due) {
      if (bgAnim) shared.time.value = clockT;
      if (pulse) shared.uPulse.value = 0.5 + 0.5 * Math.sin(clockT * 3.4);
      if (renderer.shadowMap.needsUpdate) stats.shadowFrames++;
      renderer.render(scene, cam);
      lastRender = now; dirty = false; stats.frames++;
    }
    if (active) lastActive = now;
    // анімація могла вже попросити кадр через wake(): другий ланцюжок rAF множив би кадри без кінця
    if (!raf && (active || bg || dirty || now - lastActive < IDLE_MS)) raf = requestAnimationFrame(frame);
  }
  function kick() {
    if (!alive || raf || document.hidden) return;
    lastNow = performance.now();
    raf = requestAnimationFrame(frame);
  }
  function wake() { dirty = true; lastActive = performance.now(); kick(); }
  function onVis() {
    if (document.hidden) { if (raf) cancelAnimationFrame(raf); raf = 0; for (const tw of [...tweens]) finishTween(tw); } else wake();
  }
  document.addEventListener('visibilitychange', onVis);
  const ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { resize(); wake(); }) : null;

  // ------------------------------------------------------------------------------------------------ API
  function setBoard(board) {
    if (board && board.sig && board.sig !== MAP_SIG) console.warn('solyanyi: відбиток мапи не збігається', board.sig, MAP_SIG);
  }
  function setState(view) {
    if (!view) return;
    const list = view.claimed || [], cl = view.closed || [];
    const colors = [0, 1, 2, 3, 4];
    for (const p of view.players || []) { if (p.color !== undefined) colors[p.seat] = p.color; const rs = p.routes; if (rs && rs.length) lastRoute[p.seat] = rs[rs.length - 1]; }
    const colorsChanged = colors.some((c, i) => c !== seatColor[i]);
    seatColor = colors;
    let same = !colorsChanged && list.length === claimed.size;
    if (same) for (const c of list) if (claimed.get(c.route) !== c.seat) { same = false; break; }
    if (!same) { claimed.clear(); for (const c of list) claimed.set(c.route, c.seat); rebuildCarts(); }
    closed = new Set(cl);
    if (finaleOn && view.phase !== 'over') { finaleOn = null; path = null; setLightK(1); paintCartColors(); }
    syncState();
  }
  function setLegal(l) {
    legalR = l && l.routes ? new Set(l.routes) : null;
    legalC = l && l.cities ? new Set(l.cities) : null;
    syncState(); syncRings();
  }
  function setMarks(m) { marks = m || null; syncState(); }
  function setTickets(list) { tickets = (list || []).map((t) => ({ a: t.a, b: t.b, done: !!t.done })); syncRings(); }
  function setPath(ids) { path = ids && ids.length ? new Set(ids) : null; syncState(); relabel(); }
  function setSelected(id) { const nid = id === null || id === undefined ? null : id; if (nid === selected) return; selected = nid; syncState(); relabel(); }
  /** Підписи тікають із-під обраного шляху, а не лишаються на ньому до наступного руху камери. */
  function relabel() { if (shown) { updateLabels(true); wake(); } }
  /** Прямокутники HUD поверх поля (px від кута поля): кадр їх обходить. Дрібні рухи (текст у банері) кадр не смикають. */
  function setHud(list) {
    const next = (list || []).map((r) => ({ x0: r.x0, y0: r.y0, x1: r.x1, y1: r.y1 }));
    const sig = next.map((r) => [r.x0, r.y0, r.x1, r.y1].map((v) => Math.round(v / 12)).join()).join('|');
    if (sig === hudSig) return;
    hudSig = sig; hud = next;
    if (!Wp) return;
    fit(); placed = false; updateLabels(true); wake();
  }
  function setCrop(on, tight = on) {
    if (!!on === crop && !!tight === dense) return;
    crop = !!on; dense = !!tight;
    if (Wp) { fit(); placed = false; updateLabels(true); wake(); }
  }
  /** Місця замовлень, навколо яких телефон відкриває мапу: [{ a, b, need? }]. Камеру рухаємо, лише поки людина її не чіпала. */
  function setHome(list) {
    home = (list || []).slice();
    if (!crop) return;
    cropDef();
    if (!touched) flyTo({ ...def });
  }
  function labelBoxes() {
    return labels.filter((l) => l.vis).map((l) => {
      const x = l.px + l.ox, y = l.py + l.oy;
      return { name: l.c.name, x0: x, y0: y, x1: x + l.w, y1: y + l.h, px: l.px, py: l.py };
    });
  }
  function setCursor(kind, id) { cursor = kind ? { kind, id } : null; syncState(); syncRings(); }
  function routeAt(x, y) { return routesNear(x, y, radiusNow()).map(([id]) => id); }
  function zoomBy(k) { zoomAround(Wp / 2, Hp / 2, cs.zoom * k); userMoved(); updateLabels(true); wake(); }
  function screenPos(kind, id) {
    let P = null;
    if (kind === 'city' && cities[id]) { const c = cities[id]; P = project(wx(c.x), c.h, wz(c.y), {}); }
    else if (kind === 'route' && routes[id]) { const r = routes[id], s = r.slots[r.slots.length >> 1]; P = project(wx(s.x), (s.y0 + s.y1) / 2, wz(s.y), {}); }
    else if (kind === 'seat') { const r = routes[lastRoute[id]]; if (!r) return null; const s = r.slots[r.slots.length >> 1]; P = project(wx(s.x), (s.y0 + s.y1) / 2, wz(s.y), {}); }
    return P && { x: P.x, y: P.y };
  }
  async function setTheme(id) {
    const t = themeOf(id).id;
    // А→Б→А, поки будується Б: лишаємось на А, а Б, коли добудується, викине себе сам (loadWorld порівнює buildSeq).
    if (t === themeId && world) { buildSeq++; return; }
    if (await loadWorld(t)) { updateLabels(true); wake(); }
  }
  function dispose() {
    if (!alive) return;
    alive = false; buildSeq++;
    if (raf) cancelAnimationFrame(raf); raf = 0;
    clearTimeout(settleT);
    for (const tw of [...tweens]) tw.cancel();
    document.removeEventListener('visibilitychange', onVis);
    ro?.disconnect();
    canvas.removeEventListener('pointerdown', onDown);
    canvas.removeEventListener('pointermove', onMove);
    canvas.removeEventListener('pointerup', onUp);
    canvas.removeEventListener('pointercancel', onUp);
    canvas.removeEventListener('pointerleave', onLeave);
    canvas.removeEventListener('wheel', onWheel);
    canvas.removeEventListener('contextmenu', noCtx);
    canvas.removeEventListener('gesturestart', noGesture);
    canvas.removeEventListener('gesturechange', noGesture);
    world?.dispose();
    for (const m of [waveMesh, ribbons]) if (m) { m.geometry.dispose(); m.material.dispose(); }
    stateTex.dispose();
    sun.dispose?.(); hemi.dispose?.();
    renderer.renderLists.dispose();
    renderer.dispose();
    renderer.forceContextLoss?.();
    canvas.remove(); overlay.remove(); vignette.remove();
  }

  // ------------------------------------------------------------------------------------------------ перший кадр
  try {
    resizeBefore();
    await loadWorld(themeId);
  } catch (e) { fail(e); }
  function resizeBefore() { const w = Math.max(1, host.clientWidth | 0), h = Math.max(1, host.clientHeight | 0); Wp = w; Hp = h; renderer.setSize(w, h, false); cam.aspect = w / h; shared.uRes.value.set(w, h); if (opts.hud) { hud = opts.hud; hudSig = hud.map((r) => [r.x0, r.y0, r.x1, r.y1].map((v) => Math.round(v / 12)).join()).join('|'); } home = opts.home || []; fit(); }
  applyCam(); updateLabels(true);
  ro?.observe(host);
  renderer.render(scene, cam);
  // Перший кадр готовий (шейдери зібрано) — тепер показуємо: полотно, віньєтку, підписи й тло разом.
  shown = true;
  host.style.background = look.bg;
  for (const el of [canvas, vignette, overlay]) el.style.visibility = '';
  stats.buildMs = performance.now() - T0;
  wake();

  const wrap = (fn) => (...a) => { if (!alive) return undefined; const r = fn(...a); wake(); return r; };
  const anim = (fn) => (...a) => { if (!alive) return Promise.resolve(); wake(); try { return Promise.resolve(fn(...a)).then(() => { wake(); }); } catch (e) { console.error(e); return Promise.resolve(); } };
  const api = {
    setBoard: wrap(setBoard), setState: wrap(setState), setLegal: wrap(setLegal), setMarks: wrap(setMarks), setTickets: wrap(setTickets),
    setPath: wrap(setPath), setSelected: wrap(setSelected), setCursor: wrap(setCursor), setHud: wrap(setHud), setCrop: wrap(setCrop), setHome: wrap(setHome),
    labelBoxes: () => (alive ? labelBoxes() : []),
    claim: anim(claim), ticketDone: anim(ticketDone), last: anim(last), longest: anim(longest), finale: anim(finale),
    focus: wrap(focus), zoomBy: wrap(zoomBy), routeAt: (x, y) => (alive ? routeAt(x, y) : []), screenPos: (k, id) => (alive ? screenPos(k, id) : null),
    setTheme: (id) => (alive ? setTheme(id) : Promise.resolve()), get theme() { return themeId; },
    resetView: wrap(resetView), resize: wrap(() => { Wp = Hp = 0; resize(); }), dispose,
  };
  // Лише для стенду (dev/scene.html): у контракт не входить.
  Object.defineProperty(api, '_dbg', {
    enumerable: false,
    value: {
      renderer, scene, camera: cam, stats, cs, def, base,
      get raf() { return raf; }, get world() { return world; }, get useNear() { return useNear; }, get pxPerUnit() { return pxPerUnit; },
      get labels() { return labels.filter((l) => l.vis).length; }, get carts() { return carts.length; },
      view(o) { Object.assign(cs, o); touched = true; clampPan(); camMoved(true); renderer.render(scene, cam); },
      /** Чесний вимір: кадр без тіней і кадр із оновленням карти тіней. */
      measure() {
        const out = {};
        renderer.info.autoReset = true; renderer.shadowMap.needsUpdate = false; renderer.render(scene, cam);
        out.frame = { calls: renderer.info.render.calls, triangles: renderer.info.render.triangles };
        renderer.info.autoReset = false; renderer.info.reset(); renderer.shadowMap.needsUpdate = hi; renderer.render(scene, cam);
        out.withShadow = { calls: renderer.info.render.calls, triangles: renderer.info.render.triangles };
        renderer.info.autoReset = true;
        out.memory = { ...renderer.info.memory }; out.programs = renderer.info.programs?.length;
        out.mesh = { verts: base.mesh.verts, tris: base.mesh.tris };
        out.buildMs = Math.round(stats.buildMs); out.quality = hi ? 'high' : 'low'; out.dpr = renderer.getPixelRatio(); out.size = [Wp, Hp]; out.frames = stats.frames; out.near = useNear; out.k = +pxPerUnit.toFixed(3);
        return out;
      },
      render() { renderer.render(scene, cam); }, slow(k) { slowK = k; },
    },
  });
  return api;
}
