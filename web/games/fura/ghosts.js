// Колона: чужі фури «привидами» (specs/fura.md «Етап 2 — колона», «Плавні привиди (A5)»). Сервер кожен тик (100 мс) шле
// один кадр на всіх: { t, v: 2, p: [seat, x·10, z·10, h·1000, v·10, c, rx·10, rz·10, ry·1000, f, …],
// q: [seat, s, n, x·100, z·100, h·1000, v·100, c, rx·100, rz·100, ry·1000, f, …] }.
//
// q — усі пози, що прийшли на сервер за тик, з часом відправника s (мс його performance.now()) і номером n. Кожен привид —
// своя доріжка поз за часом відправника (Track): «його час → мій» — найменший зсув (прихід − s) з повільним дрейфом
// угору, малюємо мить «тепер − зсув − затримка», затримка = p90 спізнень + пів кроку поз (не менше 100 мс). Між сусідніми
// позами — Hermite з дотичними зі швидкості й курсу (рух тягача гладкий, не ламана), курс — по найкоротшій дузі,
// причіп — від тягача через сідло (не роз'їжджаються). Поз забракло (сплеск у мережі) — їде далі швидкістю й курсом
// (~0,2 с як є, далі плавно гальмує й за 0,4 с стає), а коли пози доїдуть, різниця тане за 0,15–0,6 с, без телепорта.
// Годинник малювання сам не стрибає: до цілі підтягується швидкістю ±10 %.
//
// Кадр без q (старий сервер) — як було: між двома кадрами за годинником сервера (HGames.ui.Interp(100)). Висоту, нахил і
// крен беремо з рельєфу, бо в кадрі їх нема. У фізику привиди йдуть лише за столом «💥 зіткнення» (2c, crash.js): там
// Track.predict дає позу на «тепер» для кінематичних тіл, а поблизу й малюємо її (Ghosts.crash.draw).

import { buildGhost } from './model.js';

const N = 10, Q = 12, TICK = 100, STALE_MS = 3000;
const COLORS = ['#2f6fa8', '#2f8a7a', '#8a5bb5', '#d39a1c', '#5a8f2a', '#c2477a', '#3d8fc7', '#a5352a'];
// висота тіла над землею, як стоїть своя фура на підвісці (заміряно в краю, 25.10)
const TRACTOR_Y = 1.08, TRAILER_Y = 1.33;
const KP = 5.6, BACK = 5.0;          // від центру причепа до шворня і до заднього моста — де мацати рельєф
const FIFTH = -2.25;                 // сідло в осях тягача (truck.js TRACTOR.fifth.z): центр причепа = тягач + FIFTH·курс − KP·курс причепа
// доріжка привида (A5)
const D_MIN = 100, D_MAX = 800, D_START = 220;   // затримка малювання, мс
const EXTRA = 300;                   // скільки мс їдемо далі без поз
const KEEP = 4000;                   // скільки мс поз тримати
const SNAP = 30;                     // стрибок більший за це (м) — справжній телепорт (рятунок), не згладжуємо
const EASE = 150, EASE_M = 30, EASE_MAX = 600;   // поправка після екстраполяції тане за 150 мс + 30 мс на метр (до 0,6 с)
const GAP = 0.5;                     // проміжок між позами довший за це (с) — без дотичних
const RATE = 0.1;                    // годинник малювання наздоганяє ціль не швидше ±10 %
// затримка = p90 спізнень + пів кроку поз + 10 мс: решту (рідкі сплески, пізню позу) бере екстраполяція — на «змійці»
// вона помиляється на сантиметри, а p95 + цілий крок тримав привида на 0,1–0,15 с далі (specs/fura.md, A5)
const LATE_Q = 0.9, STEP_K = 0.5;
// прогноз на «тепер» для зіткнень (2c, crash.js): base — найкоротша дорога пози до мене плюс зсув годинників, тож
// справжнє «тепер» відправника — на ту дорогу далі. Її міряємо на СВОЇЙ доріжці (свої пози теж приходять у кадрі, годинник
// той самий: base своєї = мій шлях угору + вниз) і вважаємо, що в нього шлях такий самий (C1: без цього на інтернеті
// 60–150 мс в один бік прогноз відставав на 150–250 мс, 1,5–2,5 м); без своєї доріжки — LEAD. Далі за останню позу
// прогноз не їде довше PRED_MAX; поправка, коли доїхали нові пози, тане за PRED_EASE, але не швидше PRED_V (кінематичне
// тіло не стрибає й лишається твердим); стрибок понад PRED_SNAP — телепорт (рятунок, F5): тоді колізію гасять на мить
const LEAD = 60, LEAD_MIN = 20, LEAD_MAX = 450, PRED_MAX = 600, PRED_EASE = 150, PRED_V = 8, PRED_SNAP = 12;

/** Кадр → Map(seat → {x, z, h, v, c, rx, rz, ry, f}); розбираємо раз на кадр (старий шлях, без q). */
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
const dA = (a, b) => Math.atan2(Math.sin(b - a), Math.cos(b - a));
const lerpA = (a, b, t) => a + dA(a, b) * t;
const clamp = (x, a, b) => Math.max(a, Math.min(b, x));

/** Залишок «причіп мінус те, де він мав би бути за сідлом» — тримаємо його, щоб причіп малювати від тягача. */
function hitch(o) {
  o.ex = o.rx - (o.x + FIFTH * Math.sin(o.h) - KP * Math.sin(o.ry));
  o.ez = o.rz - (o.z + FIFTH * Math.cos(o.h) - KP * Math.cos(o.ry));
  o.hooked = o.c && Math.hypot(o.ex, o.ez) < 1;
  return o;
}

/** Скільки мс їхати навмання, коли поз нема вже e мс: до 2/3 EXTRA — як є, далі плавно гальмуємо й стаємо на EXTRA
 *  (не обрив руху: швидкість спадає до нуля за ще 2/3 EXTRA). */
function fade(e) {
  const a = EXTRA * 2 / 3, b = EXTRA - a;
  if (e <= a) return Math.max(0, e);
  const k = Math.min(1, (e - a) / (2 * b));
  return a + 2 * b * (k - k * k / 2);
}

/** Їхати від пози a ще e мс: швидкістю по дузі (кутова швидкість — з двох останніх поз). */
function ahead(a, w, wr, e) {
  const t = e / 1000, h1 = a.h + w * t, ry = a.ry + wr * t;
  let dx, dz;
  if (Math.abs(w) < 1e-4) { dx = Math.sin(a.h) * a.v * t; dz = Math.cos(a.h) * a.v * t; }
  else { dx = (a.v / w) * (Math.cos(a.h) - Math.cos(h1)); dz = (a.v / w) * (Math.sin(h1) - Math.sin(a.h)); }
  return { x: a.x + dx, z: a.z + dz, h: h1, v: a.v, c: a.c, ry, rx: a.rx, rz: a.rz, ex: a.ex, ez: a.ez, hooked: a.hooked, f: a.f, extra: e };
}

/**
 * Пози одного привида за часом відправника. add() — поза з кадру (now — коли прийшла), at(now, dt) — що малювати.
 * Годинник: base — найменше (прихід − s), повзе вгору на 1 % (інакше годинник відправника, що трохи відстає, з часом
 * накопичив би запізнення); затримка — p90 спізнень над base + пів медіанного кроку поз + 10 мс.
 */
class Track {
  constructor() { this.reset(); }
  reset() {
    this.b = []; this.base = null; this.baseAt = 0; this.late = []; this.gaps = [];
    this.want = D_START; this.tau = null; this.lastNow = 0; this.inAt = 0;
    this.ver = 0; this.seen = -1; this.raw = null; this.rawTau = 0;
    this.off = { x: 0, z: 0, h: 0, ry: 0 }; this.ease = EASE;
    this.extraN = 0; this.frames = 0;
    this.pLast = 0; this.pVer = -1; this.pRaw = null; this.pS = 0; this.pOff = { x: 0, z: 0, h: 0, ry: 0 };
  }
  cur(now) { return this.base + (now - this.baseAt) * 0.01; }

  add(o, now) {
    const b = this.b, last = b[b.length - 1];
    // відправник перезавантажив сторінку (годинник із нуля) чи пропав надовго — доріжка наново
    if (last && (o.s < last.s - 1000 || o.s > last.s + 10000)) this.reset();
    for (let i = b.length - 1; i >= 0 && i >= b.length - 8; i--) if (b[i].n === o.n && b[i].s === o.s) return;
    hitch(o);
    let i = b.length;
    while (i > 0 && b[i - 1].s > o.s) i--;
    if (i > 0 && b[i - 1].s === o.s) b[i - 1] = o; else b.splice(i, 0, o);
    if (i === b.length - 1 && i > 0) { this.gaps.push(o.s - b[i - 1].s); if (this.gaps.length > 30) this.gaps.shift(); }
    while (b.length > 4 && b[0].s < b[b.length - 1].s - KEEP) b.shift();
    // годинник
    const off = now - o.s;
    if (this.base == null || off < this.cur(now) - 1000) { this.base = off; this.late.length = 0; this.tau = null; }
    else this.base = Math.min(this.cur(now), off);
    this.baseAt = now;
    this.late.push(off - this.base);
    if (this.late.length > 60) this.late.shift();
    if (this.late.length >= 10) {
      const s = this.late.slice().sort((x, y) => x - y), g = this.gaps.slice().sort((x, y) => x - y);
      const hi = s[Math.min(s.length - 1, Math.floor(s.length * LATE_Q))];
      const step = g.length ? g[g.length >> 1] : TICK;
      this.want = clamp(hi + step * STEP_K + 10, D_MIN, D_MAX);
    }
    this.inAt = now;
    this.ver++;
  }

  /** Поза доріжки на мить s відправника (без згладжування). */
  eval(s) {
    const b = this.b, n = b.length;
    if (!n) return null;
    if (s <= b[0].s) return Object.assign({}, b[0], { extra: 0 });
    let j = n - 1;
    while (j > 0 && b[j].s > s) j--;
    const a = b[j];
    if (j === n - 1) {
      // далі поз нема — їдемо швидкістю й курсом, до EXTRA мс
      const p = j > 0 ? b[j - 1] : null, dt = p ? (a.s - p.s) / 1000 : 0;
      const w = p && dt > 0.02 && dt < 0.5 ? clamp(dA(p.h, a.h) / dt, -0.8, 0.8) : 0;
      const wr = p && dt > 0.02 && dt < 0.5 ? clamp(dA(p.ry, a.ry) / dt, -0.8, 0.8) : 0;
      return ahead(a, w, wr, fade(s - a.s));
    }
    const c = b[j + 1], T = (c.s - a.s) / 1000, u = (s - a.s) / (c.s - a.s);
    // телепорт (рятунок, перезапуск): ковзати між ними нема чого — стоїмо на старій, далі одразу на новій
    if (Math.hypot(c.x - a.x, c.z - a.z) > SNAP) return Object.assign({}, u < 1 ? a : c, { extra: 0 });
    // Hermite: дотичні — швидкість уздовж курсу тягача, на весь проміжок. Дірка довша за GAP (вкладку ховали, зв'язок
    // рвався) — рівна лінія: дотичні v·T на секунди дали б петлю
    const u2 = u * u, u3 = u2 * u, gap = T > GAP;
    const h00 = gap ? 1 - u : 2 * u3 - 3 * u2 + 1, h10 = gap ? 0 : u3 - 2 * u2 + u;
    const h01 = gap ? u : -2 * u3 + 3 * u2, h11 = gap ? 0 : u3 - u2;
    const ta = a.v * T, tc = c.v * T;
    return {
      x: h00 * a.x + h10 * ta * Math.sin(a.h) + h01 * c.x + h11 * tc * Math.sin(c.h),
      z: h00 * a.z + h10 * ta * Math.cos(a.h) + h01 * c.z + h11 * tc * Math.cos(c.h),
      h: lerpA(a.h, c.h, u), v: lerp(a.v, c.v, u), c: c.c, ry: lerpA(a.ry, c.ry, u),
      rx: lerp(a.rx, c.rx, u), rz: lerp(a.rz, c.rz, u), ex: lerp(a.ex, c.ex, u), ez: lerp(a.ez, c.ez, u),
      hooked: a.hooked && c.hooked, f: u < 0.5 ? a.f : c.f, extra: 0,
    };
  }

  /** Що малювати зараз (now — мить кадру, мс). */
  at(now) {
    if (!this.b.length) return null;
    const dt = this.lastNow ? clamp(now - this.lastNow, 0, 250) : 0;
    this.lastNow = now;
    // годинник малювання: ціль — тепер − base − затримка; тягнемося до неї швидкістю 1 ± 10 %, не стрибком
    const goal = now - this.cur(now) - this.want;
    if (this.tau == null || Math.abs(goal - this.tau) > 1500) this.tau = goal;
    else this.tau += dt * (1 + clamp((goal - this.tau) / 400, -RATE, RATE));
    // нові пози після того, як ми їхали навмання: різниця «що малювали — що тепер виходить» тане, а не стрибає
    const o = this.off;
    if (this.ver !== this.seen && this.raw) {
      const was = this.raw, now2 = this.eval(this.rawTau);
      if (now2 && was.extra > 0) {
        const jx = was.x - now2.x, jz = was.z - now2.z;
        if (Math.hypot(jx, jz) < SNAP) {
          o.x += jx; o.z += jz; o.h += dA(now2.h, was.h); o.ry += dA(now2.ry, was.ry);
          // велика поправка (пози не йшли пів секунди й більше) тане довше: наздоганяє, а не пролітає метри за мить
          this.ease = clamp(EASE + EASE_M * Math.hypot(o.x, o.z), EASE, EASE_MAX);
        }
        else { o.x = o.z = o.h = o.ry = 0; }
      }
    }
    this.seen = this.ver;
    const k = Math.exp(-dt / this.ease);
    o.x *= k; o.z *= k; o.h *= k; o.ry *= k;
    const r = this.eval(this.tau);
    if (!r) return null;
    this.raw = r; this.rawTau = this.tau;
    this.frames++;
    if (r.extra > 0) this.extraN++;
    return dress(r, o);
  }

  /** Поза на мить s відправника для прогнозу: між позами — як eval, за останньою — швидкістю й курсом до PRED_MAX. */
  predRaw(s) {
    const b = this.b, n = b.length, a = b[n - 1];
    if (s < a.s) return this.eval(s);
    const p = n > 1 ? b[n - 2] : null, dt = p ? (a.s - p.s) / 1000 : 0;
    const w = p && dt > 0.02 && dt < 0.5 ? clamp(dA(p.h, a.h) / dt, -0.8, 0.8) : 0;
    const wr = p && dt > 0.02 && dt < 0.5 ? clamp(dA(p.ry, a.ry) / dt, -0.8, 0.8) : 0;
    return ahead(a, w, wr, Math.min(s - a.s, PRED_MAX));
  }

  /**
   * Прогноз на «тепер» (2c): де фура відправника є зараз, а не де її малюємо (at — на затримку позаду). Нові пози
   * зсувають прогноз — різниця тане за PRED_EASE; jump — прогноз стрибнув на понад PRED_JUMP (тоді колізію гасять).
   */
  predict(now, lead = LEAD) {
    if (!this.b.length || this.base == null) return null;
    const dt = this.pLast ? clamp(now - this.pLast, 0, 250) : 0;
    this.pLast = now;
    const o = this.pOff;
    let jump = false;
    if (this.pVer !== this.ver && this.pRaw) {
      const re = this.predRaw(this.pS), was = this.pRaw;
      const jx = was.x - re.x, jz = was.z - re.z, j = Math.hypot(jx, jz);
      if (j > PRED_SNAP) { o.x = o.z = o.h = o.ry = 0; jump = true; }
      else {
        o.x += jx; o.z += jz; o.h += dA(re.h, was.h); o.ry += dA(re.ry, was.ry);
        if (j > 0.5) { this.fixN = (this.fixN || 0) + 1; this.fixMax = Math.max(this.fixMax || 0, j); }
      }
    }
    this.pVer = this.ver;
    // поправка тане за PRED_EASE, але тіло зсувається нею не швидше PRED_V м/с
    const m = Math.hypot(o.x, o.z);
    let k = Math.exp(-dt / PRED_EASE);
    if (m * (1 - k) > PRED_V * dt / 1000) k = 1 - PRED_V * dt / 1000 / m;
    o.x *= k; o.z *= k; o.h *= k; o.ry *= k;
    const s = now - this.cur(now) + lead;
    const r = this.predRaw(s);
    this.pRaw = r; this.pS = s;
    const p = dress(r, o);
    p.jump = jump;
    return p;
  }
}

/** Поза доріжки + поправка o; причіп у зчепі — від тягача через сідло (тягач і причіп не роз'їжджаються). */
function dress(r, o) {
  const p = { x: r.x + o.x, z: r.z + o.z, h: r.h + o.h, v: r.v, c: r.c, ry: r.ry + o.ry, f: r.f };
  if (r.hooked) {
    p.rx = p.x + FIFTH * Math.sin(p.h) - KP * Math.sin(p.ry) + r.ex;
    p.rz = p.z + FIFTH * Math.cos(p.h) - KP * Math.cos(p.ry) + r.ez;
  } else { p.rx = r.rx; p.rz = r.rz; }
  return p;
}

export class Ghosts {
  constructor({ THREE, scene, camera, level }) {
    this.T = THREE; this.scene = scene; this.camera = camera; this.level = level;
    this.interp = window.HGames && HGames.ui && HGames.ui.Interp ? HGames.ui.Interp(TICK) : null;
    this.me = null;              // своє місце — з кадру не малюємо
    this.names = new Map();      // seat → нік (з виду convoy); кого там нема — не малюємо
    this.cars = new Map();       // seat → car з convoy (гараж 0.0.6): рівень для таблички, вигляд — фарба, наліпки, номер
    this.alone = true;           // за столом менше двох фур — кадрів більше не буде, привидів ховаємо одразу
    this.rigs = new Map();       // seat → { g: модель, label, nick, pose, at }
    this.tracks = new Map();     // seat → Track (кадри з q)
    this.q = false;              // сервер шле q — малюємо за часом відправника
    this.lastAt = 0;
    this.frames = 0;
    this.e = new THREE.Euler(0, 0, 0, 'YXZ');
    this.crash = null;           // 2c (crash.js): поблизу малюємо прогноз, примарну фуру — напівпрозорою
  }

  /** Прогноз на «тепер» для фізики зіткнень (2c): лише живі доріжки чужих фур, що є в колоні. */
  predict(seat, now) {
    const t = this.tracks.get(seat);
    if (!t || seat === this.me || !this.names.has(seat) || this.alone || now - t.inAt > STALE_MS) return null;
    return t.predict(now, this.leadMs(now));
  }

  /** Скільки мс від «тепер» відправника до пози, що щойно прийшла: мій шлях угору й униз (base своєї доріжки). */
  leadMs(now) {
    const t = this.me == null ? null : this.tracks.get(this.me);
    if (!t || t.base == null || t.late.length < 10 || now - t.inAt > STALE_MS) return LEAD;
    return clamp(t.cur(now), LEAD_MIN, LEAD_MAX);
  }

  /** Кадр із сервера (або підставний з консолі). */
  push(f) {
    if (!f || !Array.isArray(f.p)) return;
    const now = performance.now();
    if (Array.isArray(f.q)) {
      this.q = true;
      const q = f.q;
      for (let i = 0; i + Q <= q.length; i += Q) {
        const seat = q[i];
        let t = this.tracks.get(seat);
        if (!t) this.tracks.set(seat, (t = new Track()));
        t.add({ s: q[i + 1], n: q[i + 2], x: q[i + 3] / 100, z: q[i + 4] / 100, h: q[i + 5] / 1000, v: q[i + 6] / 100, c: !!q[i + 7],
          rx: q[i + 8] / 100, rz: q[i + 9] / 100, ry: q[i + 10] / 1000, f: q[i + 11] | 0 }, now);
      }
    } else if (this.interp) this.interp.push(f);
    else return;
    this.lastAt = now;
    this.frames++;
  }

  setMe(seat) { this.me = seat == null ? null : seat; }

  /** Ніки з виду: convoy [{seat, nick, …}]. */
  setConvoy(list) {
    const next = new Map(), cars = new Map();
    for (const c of list || []) if (c && c.seat != null) { next.set(c.seat, c.nick || ''); cars.set(c.seat, c.car || null); }
    this.names = next; this.cars = cars;
    this.alone = next.size < 2;
    for (const [seat, r] of this.rigs) {
      if (r.nick !== (next.get(seat) || '') || r.tag !== this.tagOf(seat)) this.relabel(seat, r, r.horn);
      this.dress(seat, r);
    }
    // місце звільнилось — його доріжка більше не потрібна (хто сяде, почне свою)
    for (const seat of this.tracks.keys()) if (!next.has(seat)) this.tracks.delete(seat);
  }

  reset() {
    if (this.interp) this.interp.reset();
    this.tracks.clear();
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
    this.dress(seat, r);
    return r;
  }

  /** «· Р12 ★» до ніка — з convoy[].car (гараж); нема гаража — порожньо. */
  tagOf(seat) {
    const car = this.cars.get(seat);
    return car && car.lvl ? ' · Р' + car.lvl + (car.stars ? ' ★' + (car.stars > 1 ? car.stars : '') : '') : '';
  }

  dress(seat, r) { const car = this.cars.get(seat); r.g.setLook(car && car.look, this.names.get(seat) || ''); }

  /** Табличка з ніком над кабіною: спрайт із canvas (завжди лицем до камери). */
  relabel(seat, r, horn) {
    const T = this.T, nick = this.names.get(seat) || 'фура ' + (seat + 1);
    r.nick = this.names.get(seat) || '';
    const c = document.createElement('canvas');
    c.width = 512; c.height = 128;
    const g = c.getContext('2d');
    g.font = '700 54px system-ui, sans-serif';
    r.tag = this.tagOf(seat);
    const text = (horn ? '📯 ' : '') + nick + r.tag, w = Math.min(500, g.measureText(text).width + 56);
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

  /** Раз на кадр малювання. now — мить кадру (мс), dt — с від попереднього. */
  update(now, dt) {
    const fresh = !this.alone && this.lastAt && now - this.lastAt < STALE_MS;
    const seen = new Set();
    if (fresh && this.q) {
      for (const [seat, t] of this.tracks) {
        if (seat === this.me || !this.names.has(seat) || now - t.inAt > STALE_MS) continue;
        let p = t.at(now), alpha = 1;
        if (!p) continue;
        if (this.crash) ({ p, alpha } = this.crash.draw(seat, p));
        seen.add(seat);
        this.place(this.rig(seat), p, now, dt, alpha);
      }
    } else if (fresh && this.interp) {
      const s = this.interp.at(now);
      if (s) {
        const A = parse(s.a), B = parse(s.b), t = s.t;
        for (const [seat, b] of B) {
          if (seat === this.me || !this.names.has(seat)) continue;   // своя фура чи місце, якого в колоні вже нема
          const a = A.get(seat) || b;
          seen.add(seat);
          this.place(this.rig(seat), {
            x: lerp(a.x, b.x, t), z: lerp(a.z, b.z, t), h: lerpA(a.h, b.h, t), v: lerp(a.v, b.v, t), c: b.c,
            rx: lerp(a.rx, b.rx, t), rz: lerp(a.rz, b.rz, t), ry: lerpA(a.ry, b.ry, t), f: t < 0.5 ? a.f : b.f,
          }, now, dt);
        }
      }
    }
    for (const [seat, r] of this.rigs) {
      if (seen.has(seat)) continue;
      if (r.pose) { r.g.setVisible(false); r.label.visible = false; r.pose = null; }
    }
  }

  place(r, p, now, dt, alpha = 1) {
    const H = this.level.height, e = this.e;
    r.pose = p;
    r.g.setVisible(true);
    if (r.alpha !== alpha) {
      // примарна фура (2c: двір, повільно, щойно з'явилась) — напівпрозора; матеріали в кожного привида свої
      r.alpha = alpha;
      for (const g of [r.g.tractor, r.g.trailer]) g.traverse((o) => {
        if (!o.isMesh) return;
        o.material.transparent = alpha < 1; o.material.opacity = alpha; o.material.depthWrite = alpha >= 1; o.material.needsUpdate = true;
      });
    }
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

  /** Для консолі (__fura.ghosts.stats()): затримка й скільки кадрів кожен привид їхав без поз. */
  stats() {
    const out = {};
    for (const [seat, t] of this.tracks) {
      out[seat] = { poses: t.b.length, want: Math.round(t.want), behind: t.tau == null ? null : Math.round(performance.now() - t.cur(performance.now()) - t.tau),
        extra: t.frames ? +(100 * t.extraN / t.frames).toFixed(1) + ' %' : '—', fix: t.fixN || 0, fixMax: +(t.fixMax || 0).toFixed(2) };
    }
    return out;
  }

  dispose() {
    for (const r of this.rigs.values()) {
      r.g.dispose();
      if (r.label) { this.scene.remove(r.label); r.label.material.map.dispose(); r.label.material.dispose(); }
    }
    this.rigs.clear();
    this.tracks.clear();
  }
}

/** Підставна колона для перевірки без сервера: n фур їдуть по дорогах краю, кадри — 10 Гц із дрижанням приходу, з q
 *  (час відправника — мить, коли поза «знята»). Повертає { stop() }. Кадр будуємо так само, як сервер: цілі числа. */
export function fakeConvoy(ghosts, level, { n = 3, jitter = 40, speed = 14, me = 0, onFrame, manual = false } = {}) {
  const roads = level.roads.filter((r) => r.kind !== 'yard' && r.pts.length > 4);
  const trucks = [];
  for (let i = 0; i < n; i++) {
    const r = roads[(i * 3) % roads.length];
    trucks.push({ seat: me + 1 + i, road: r, s: (r.pts[r.pts.length - 1].s * (0.2 + 0.25 * i)) % r.pts[r.pts.length - 1].s, dir: i % 2 ? -1 : 1, v: speed * (0.8 + 0.15 * i), n: 0 });
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
    const p = [], q = [], sAt = Math.round(t0 + (t - 1000) * TICK);
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
      q.push(tr.seat, sAt, tr.n++, Math.round(a.x * 100), Math.round(a.z * 100), Math.round(h * 1000), Math.round(tr.v * 100), 1,
        Math.round(rc.x * 100), Math.round(rc.z * 100), Math.round(ry * 1000), f);
    }
    return { t: t++, v: 2, p, q };
  }
  // manual — без таймерів: next() дає наступний кадр, а коли його «доставити», вирішує той, хто перевіряє (схована
  // Browser-панель гальмує і rAF, і таймери — заміри плавності там ідуть на підміненому годиннику)
  if (manual) return { trucks, next: frame, stop() {} };
  function send() {
    const fr = frame();
    // прихід: рівний тик сервера + дрижання ±jitter мс; порядок зберігається, як у WebSocket
    const due = Math.max(send.last || 0, t0 + (fr.t - 1000) * TICK + 50 + (Math.random() * 2 - 1) * jitter);
    send.last = due;
    setTimeout(() => { if (!stopped) { ghosts.push(fr); onFrame && onFrame(fr); } }, Math.max(0, due - performance.now()));
  }
  // кадри «народжуються» рівно раз на 100 мс; що приходять нерівно — робить setTimeout вище
  timer = setInterval(send, TICK);
  return { trucks, stop() { stopped = true; clearInterval(timer); } };
}
