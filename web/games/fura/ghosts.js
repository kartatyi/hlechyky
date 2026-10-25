// Колона: чужі фури «привидами» (specs/fura.md «Етап 2 — колона»). Сервер кожен тик (100 мс) шле один кадр на всіх:
// { t, p: [seat, x·10, z·10, h·1000, v·10, c, rx·10, rz·10, ry·1000, f, …] } — по 10 чисел на фуру. Малюємо між двома
// кадрами за годинником сервера (HGames.ui.Interp(100)): нерівний прихід кадрів рухом не смикає. Висоту, нахил і крен
// беремо з рельєфу, бо в кадрі їх нема. У фізику привиди не йдуть — зіткнення буде кроком 2c.

import { buildGhost } from './model.js';

const N = 10, TICK = 100, STALE_MS = 3000;
const COLORS = ['#2f6fa8', '#2f8a7a', '#8a5bb5', '#d39a1c', '#5a8f2a', '#c2477a', '#3d8fc7', '#a5352a'];
// висота тіла над землею, як стоїть своя фура на підвісці (заміряно в краю, 25.10)
const TRACTOR_Y = 1.08, TRAILER_Y = 1.33;
const KP = 5.6, BACK = 5.0;          // від центру причепа до шворня і до заднього моста — де мацати рельєф

/** Кадр → Map(seat → {x, z, h, v, c, rx, rz, ry, f}); розбираємо раз на кадр. */
function parse(f) {
  if (f._m) return f._m;
  const m = new Map(), p = f.p || [];
  for (let i = 0; i + N <= p.length; i += N) {
    m.set(p[i], { x: p[i + 1] / 10, z: p[i + 2] / 10, h: p[i + 3] / 1000, v: p[i + 4] / 10, c: !!p[i + 5],
      rx: p[i + 6] / 10, rz: p[i + 7] / 10, ry: p[i + 8] / 1000, f: p[i + 9] | 0 });
  }
  Object.defineProperty(f, '_m', { value: m });
  return m;
}
const lerp = (a, b, t) => a + (b - a) * t;
const lerpA = (a, b, t) => a + Math.atan2(Math.sin(b - a), Math.cos(b - a)) * t;

export class Ghosts {
  constructor({ THREE, scene, camera, level }) {
    this.T = THREE; this.scene = scene; this.camera = camera; this.level = level;
    this.interp = window.HGames && HGames.ui && HGames.ui.Interp ? HGames.ui.Interp(TICK) : null;
    this.me = null;              // своє місце — з кадру не малюємо
    this.names = new Map();      // seat → нік (з виду convoy)
    this.rigs = new Map();       // seat → { g: модель, label, nick, pose, at }
    this.lastAt = 0;
    this.frames = 0;
    this.q = new THREE.Quaternion(); this.e = new THREE.Euler(0, 0, 0, 'YXZ');
  }

  /** Кадр із сервера (або підставний з консолі). */
  push(f) {
    if (!f || !Array.isArray(f.p) || !this.interp) return;
    this.interp.push(f);
    this.lastAt = performance.now();
    this.frames++;
  }

  setMe(seat) { this.me = seat == null ? null : seat; }

  /** Ніки з виду: convoy [{seat, nick, …}]. */
  setConvoy(list) {
    const next = new Map();
    for (const c of list || []) if (c && c.seat != null) next.set(c.seat, c.nick || '');
    this.names = next;
    for (const [seat, r] of this.rigs) if (r.nick !== (next.get(seat) || '')) this.relabel(seat, r, r.horn);
  }

  reset() {
    if (this.interp) this.interp.reset();
    this.lastAt = 0;
    for (const r of this.rigs.values()) { r.g.setVisible(false); r.label.visible = false; r.pose = null; }
  }

  rig(seat) {
    let r = this.rigs.get(seat);
    if (r) return r;
    const g = buildGhost(this.T, this.scene, COLORS[seat % COLORS.length]);
    r = { seat, g, label: null, nick: null, pose: null, spin: 0, hornAt: 0 };
    this.rigs.set(seat, r);
    this.relabel(seat, r);
    return r;
  }

  /** Табличка з ніком над кабіною: спрайт із canvas (завжди лицем до камери). */
  relabel(seat, r, horn) {
    const T = this.T, nick = this.names.get(seat) || 'фура ' + (seat + 1);
    r.nick = this.names.get(seat) || '';
    const c = document.createElement('canvas');
    c.width = 512; c.height = 128;
    const g = c.getContext('2d');
    g.font = '700 54px system-ui, sans-serif';
    const text = (horn ? '📯 ' : '') + nick, w = Math.min(500, g.measureText(text).width + 56);
    g.fillStyle = 'rgba(20,22,28,.72)';
    g.beginPath(); g.roundRect((512 - w) / 2, 18, w, 92, 30); g.fill();
    g.fillStyle = COLORS[seat % COLORS.length]; g.fillRect((512 - w) / 2 + 22, 50, 12, 28);
    g.fillStyle = '#fff'; g.textAlign = 'center'; g.textBaseline = 'middle';
    g.fillText(text, 256 + 12, 66, 440);
    const tex = new T.CanvasTexture(c);
    tex.colorSpace = T.SRGBColorSpace;
    if (r.label) { r.label.material.map.dispose(); r.label.material.map = tex; r.label.material.needsUpdate = true; }
    else {
      r.label = new T.Sprite(new T.SpriteMaterial({ map: tex, depthTest: false, transparent: true }));
      r.label.renderOrder = 10;
      this.scene.add(r.label);
    }
    r.horn = !!horn;
  }

  /** Раз на кадр малювання. dt — с від попереднього. */
  update(now, dt) {
    const fresh = this.interp && this.lastAt && now - this.lastAt < STALE_MS;
    const s = fresh ? this.interp.at(now) : null;
    const seen = new Set();
    if (s) {
      const A = parse(s.a), B = parse(s.b), t = s.t;
      for (const [seat, b] of B) {
        if (seat === this.me) continue;
        const a = A.get(seat) || b;
        seen.add(seat);
        this.place(this.rig(seat), {
          x: lerp(a.x, b.x, t), z: lerp(a.z, b.z, t), h: lerpA(a.h, b.h, t), v: lerp(a.v, b.v, t), c: b.c,
          rx: lerp(a.rx, b.rx, t), rz: lerp(a.rz, b.rz, t), ry: lerpA(a.ry, b.ry, t), f: t < 0.5 ? a.f : b.f,
        }, now, dt);
      }
    }
    for (const [seat, r] of this.rigs) {
      if (seen.has(seat)) continue;
      if (r.pose) { r.g.setVisible(false); r.label.visible = false; r.pose = null; }
    }
  }

  place(r, p, now, dt) {
    const H = this.level.height, e = this.e;
    r.pose = p;
    r.g.setVisible(true);
    // тягач: нахил по осі з рельєфу під передком і задком, крен — з боків
    const sh = Math.sin(p.h), ch = Math.cos(p.h);
    const hf = H(p.x + sh * 2.2, p.z + ch * 2.2), hb = H(p.x - sh * 2.4, p.z - ch * 2.4);
    const hl = H(p.x + ch * 1.1, p.z - sh * 1.1), hr = H(p.x - ch * 1.1, p.z + sh * 1.1);
    const y = (hf + hb) / 2;
    e.set(-Math.atan2(hf - hb, 4.6), p.h, Math.atan2(hl - hr, 2.2) * 0.8);
    r.g.tractor.position.set(p.x, y + TRACTOR_Y, p.z);
    r.g.tractor.quaternion.setFromEuler(e);
    // причіп: шворень над сідлом, задок — на мості
    const sy = Math.sin(p.ry), cy = Math.cos(p.ry);
    const kf = H(p.rx + sy * KP, p.rz + cy * KP), kb = H(p.rx - sy * BACK, p.rz - cy * BACK);
    e.set(-Math.atan2(kf - kb, KP + BACK), p.ry, 0);
    r.g.trailer.position.set(p.rx, (kf * BACK + kb * KP) / (KP + BACK) + TRAILER_Y, p.rz);
    r.g.trailer.quaternion.setFromEuler(e);
    // колеса крутяться від швидкості (кадр її несе)
    r.spin += (p.v * dt) / 0.52;
    for (const w of r.g.wheels) w.rotation.x = r.spin;
    r.g.setFlags(p.f);
    // табличка: над кабіною, з відстанню трохи більшає, щоб нік читався й за сотню метрів
    const d = this.camera.position.distanceTo(r.g.tractor.position);
    const k = Math.max(1, Math.min(4, d / 30));
    r.label.visible = true;
    r.label.position.set(p.x, y + TRACTOR_Y + 3.6 + k * 0.5, p.z);
    r.label.scale.set(4.4 * k, 1.1 * k, 1);
    const horn = !!(p.f & 1);
    if (horn) r.hornAt = now;
    const showHorn = now - r.hornAt < 600;
    if (showHorn !== !!r.horn) this.relabel(r.seat, r, showHorn);
  }

  /** Де зараз кожен привид (для мінімапи й відстаней у списку колони). */
  list() {
    const out = [];
    for (const [seat, r] of this.rigs) if (r.pose) out.push({ seat, nick: this.names.get(seat) || '', x: r.pose.x, z: r.pose.z, h: r.pose.h, color: COLORS[seat % COLORS.length] });
    return out;
  }
  poseOf(seat) { const r = this.rigs.get(seat); return r && r.pose; }
  colorOf(seat) { return COLORS[seat % COLORS.length]; }

  dispose() {
    for (const r of this.rigs.values()) {
      r.g.dispose();
      if (r.label) { this.scene.remove(r.label); r.label.material.map.dispose(); r.label.material.dispose(); }
    }
    this.rigs.clear();
  }
}

/** Підставна колона для перевірки без сервера 2a: n фур їдуть по дорогах краю, кадри — 10 Гц із дрижанням приходу.
 *  Повертає { stop() }. Кадр будуємо так само, як мав би сервер: цілі числа ×10/×1000. */
export function fakeConvoy(ghosts, level, { n = 3, jitter = 40, speed = 14, me = 0, onFrame, manual = false } = {}) {
  const roads = level.roads.filter((r) => r.kind !== 'yard' && r.pts.length > 4);
  const trucks = [];
  for (let i = 0; i < n; i++) {
    const r = roads[(i * 3) % roads.length];
    trucks.push({ seat: me + 1 + i, road: r, s: (r.pts[r.pts.length - 1].s * (0.2 + 0.25 * i)) % r.pts[r.pts.length - 1].s, dir: i % 2 ? -1 : 1, v: speed * (0.8 + 0.15 * i) });
  }
  const at = (road, s) => {
    const P = road.pts, L = P[P.length - 1].s;
    s = Math.max(0, Math.min(L, s));
    let j = 1;
    while (j < P.length - 1 && P[j].s < s) j++;
    const a = P[j - 1], b = P[j], k = (s - a.s) / Math.max(1e-6, b.s - a.s);
    return { x: a.x + (b.x - a.x) * k, z: a.z + (b.z - a.z) * k, L };
  };
  let t = 1000, stopped = false, timer = 0;
  const t0 = performance.now();
  function frame() {
    const p = [];
    for (const tr of trucks) {
      tr.s += tr.dir * tr.v * TICK / 1000;
      const L = tr.road.pts[tr.road.pts.length - 1].s;
      if (tr.s > L - 1 || tr.s < 1) { tr.dir = -tr.dir; tr.s = Math.max(1, Math.min(L - 1, tr.s)); }
      const a = at(tr.road, tr.s), b = at(tr.road, tr.s + tr.dir * 2);
      const h = Math.atan2(b.x - a.x, b.z - a.z);
      // центр причепа — 7,85 м позаду сідла по дорозі (сідло −2,25 у тягачі, шворень +5,6 у причепі)
      const r = at(tr.road, tr.s - tr.dir * 8.85), r2 = at(tr.road, tr.s - tr.dir * 6.85);
      const ry = Math.atan2(r2.x - r.x, r2.z - r.z), rc = { x: (r.x + r2.x) / 2, z: (r.z + r2.z) / 2 };
      const f = (t % 40 < 3 ? 1 : 0) | 2 | (t % 60 > 50 ? 4 : 0);
      p.push(tr.seat, Math.round(a.x * 10), Math.round(a.z * 10), Math.round(h * 1000), Math.round(tr.v * 10), 1,
        Math.round(rc.x * 10), Math.round(rc.z * 10), Math.round(ry * 1000), f);
    }
    return { t: t++, p };
  }
  // manual — без таймерів: next() дає наступний кадр, а коли його «доставити», вирішує той, хто перевіряє (схована
  // Browser-панель гальмує і rAF, і таймери — заміри плавності там ідуть на підміненому годиннику)
  if (manual) return { trucks, next: frame, stop() {} };
  function send() {
    const fr = frame();
    // прихід: рівний тик сервера + дрижання ±jitter мс (кадр може й обігнати сусіда — Interp відкине запізнілий)
    const due = t0 + (fr.t - 1000) * TICK + (Math.random() * 2 - 1) * jitter;
    setTimeout(() => { if (!stopped) { ghosts.push(fr); onFrame && onFrame(fr); } }, Math.max(0, due - performance.now()));
  }
  // кадри «народжуються» рівно раз на 100 мс; що приходять нерівно — робить setTimeout вище
  timer = setInterval(send, TICK);
  return { trucks, stop() { stopped = true; clearInterval(timer); } };
}
