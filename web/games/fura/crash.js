// Зіткнення фур (крок 2c, specs/fura.md «Зіткнення (2c)»): стіл «💥 зіткнення» (опція collide, вид collide: 'crash').
//
// Кожна чужа фура — два кінематичні тіла (тягач і причіп, коробки за розмірами truck.js) у МОЇЙ Rapier-сцені, поставлені
// прогнозом на «тепер» (ghosts.js Track.predict: остання поза + швидкість і курс, поправки тануть, не стрибають). Тіло
// має швидкість прогнозу, але без складової «на мене»: моя фура б'ється об чужу як об стіну, що їде своїм ходом, а чужа
// мене не «бульдозить» — мене штовхне її удар, коли він прийде.
//
// Удар рахує той, хто б'є: Rapier гасить мою швидкість об чужу фуру (вантаж б'ється від прискорення сам), а подія
// контактної сили дає імпульс. Частку (SHARE, зважену тим, хто з двох наближався) шлю `input hit {to, ix, iz, at, ox, oz}`;
// сервер пересилає її адресату в кадрі (поле h), адресат прикладає імпульс до своєї фури (Δv ≤ DV_MAX).
//
// Примарна (крізь неї їдеш, напівпрозора поблизу): у дворі (+10 м) моя чи її фура; відносна швидкість < 5 км/год (не
// заблокуєш бокс і не підштовхнеш того, хто чіпляється); прогноз стрибнув > 2 м (на мить); щойно з'явилась чи стала
// твердою там, де ми перекриваємось, — доки не роз'їдемось. Промені коліс бачать лише нерухоме (onlyFixed) — і далі так.

import { TRACTOR, TRAILER } from './truck.js';

const FORCE_MIN = 60000;   // Н: поріг події контактної сили (1000 Н·с за крок 60 Гц) — дотик і тертя боком не рахуємо
const SEND_MIN = 2500;     // Н·с: менший удар (після частки) не шлемо
const SHARE = 0.5;         // у моїй фізиці чужа фура — стіна: я віддаю їй удвічі більше, ніж віддали б одна одній дві рівні фури
const DV_MAX = 3;          // м/с: удар міняє швидкість моєї фури не більше за це (~11 км/год)
const GAP_MS = 250;        // удар пари — не частіше за 4/с (і сервер так само)
const EPISODE = 6;         // кроків: довший контакт ділимо на окремі удари
const AFTER_MS = 400;      // після удару (завдав чи отримав) пара на мить примарна: без повторних дрібних ударів і «доштовхування»
const REBOUND = 0.5, REBOUND_MS = 300;   // м/с: відскок від чужої фури — не більше (м'який розв'язувач Rapier дає 2+ м/с)
const SLOW = 5 / 3.6;      // м/с: відносна швидкість менша — примарна
const YARD_M = 10;         // як Fura.HitYardM
const JUMP_MS = 500;       // прогноз стрибнув — колізію гасимо на стільки
const NEAR_DRAW = 30, FAR_DRAW = 60;   // ближче — малюємо прогноз, далі — плавну позу A5 (між — суміш)
const ALPHA = 0.45, ALPHA_M = 40;      // примарна ближче за 40 м — напівпрозора
// висота тіл над землею (як привид у ghosts.js) і коробки в осях тіла: [пів-ширина, пів-висота, пів-довжина, x, y, z]
const TRACTOR_Y = 1.08, TRAILER_Y = 1.33;
const BOX_T = [[1.25, 1.3, 1.1, 0, 1.0, 2.1], [1.2, 0.45, 2.3, 0, -0.15, -0.95]];
const BOX_R = [[1.25, 1.375, 6.8, 0, 1.375, 0], [1.1, 0.45, 6.0, 0, -0.4, -0.6]];
// у плані (2D) — для «чи перекриваємось»: пів-ширина, пів-довжина, зсув уздовж
const FLAT = [[1.25, 3.4, -0.05], [1.25, 6.8, 0]];

// «Стандартна фура» в ударі (GARAGE.md §6): маса теперішнього «Чумака» з повним причепом, незалежно від тягача, тюнінгу
// й вантажу — і для частки ½·m·v, і для стелі Δv адресата. Габарити кінематичних тіл — BOX_T/BOX_R вище, теж стандартні.
const STD_T = TRACTOR.mass.cab + TRACTOR.mass.frame, STD_R = TRAILER.mass.body + TRAILER.mass.cargo;
const stdMass = (coupled, part) => (coupled ? STD_T + STD_R : part ? STD_R : STD_T);

const clamp = (x, a, b) => Math.max(a, Math.min(b, x));

/** Прямокутник у плані: центр, курс, пів-розміри → чи перетинаються два (теорема про розділову вісь). */
function overlap(a, b) {
  const axes = [a.h, a.h + Math.PI / 2, b.h, b.h + Math.PI / 2];
  for (const t of axes) {
    const ux = Math.sin(t), uz = Math.cos(t);
    const ra = Math.abs(a.l * (Math.sin(a.h) * ux + Math.cos(a.h) * uz)) + Math.abs(a.w * (Math.cos(a.h) * ux - Math.sin(a.h) * uz));
    const rb = Math.abs(b.l * (Math.sin(b.h) * ux + Math.cos(b.h) * uz)) + Math.abs(b.w * (Math.cos(b.h) * ux - Math.sin(b.h) * uz));
    if (Math.abs((b.x - a.x) * ux + (b.z - a.z) * uz) > ra + rb) return false;
  }
  return true;
}
const rect = (x, z, h, i, pad = 0) => ({ x: x + Math.sin(h) * FLAT[i][2], z: z + Math.cos(h) * FLAT[i][2], h, w: FLAT[i][0] + pad, l: FLAT[i][1] + pad });

export class Crash {
  constructor({ THREE, RAPIER, world, truck, ghosts, level, server, say, sound }) {
    Object.assign(this, { T: THREE, R: RAPIER, world, truck, ghosts, level, server, say, sound });
    this.on = false;
    this.queue = new RAPIER.EventQueue(true);
    this.rigs = new Map();       // seat → { seat, tb, rb, cols: [], solid, offUntil, pose, why }
    this.byCol = new Map();      // хендл колайдера чужої фури → { rig, part }
    this.hits = new Map();       // seat → удар, що збирається (кілька кроків контакту)
    this.sentAt = new Map();     // seat → коли востаннє слав
    this.seen = new Set();       // id отриманих ударів (кадр несе удар 3 тики)
    this.log = [];               // для консолі: що й кому, скільки
    this.q = new THREE.Quaternion(); this.e = new THREE.Euler(0, 0, 0, 'YXZ');
    this.myVel = [{ x: 0, z: 0 }, { x: 0, z: 0 }];
    ghosts.crash = this;
  }

  /** З виду столу: collide 'crash' — зіткнення, інакше привиди (тіла лишаються, але крізь них). */
  view(v) {
    const on = !!(v && v.collide === 'crash');
    if (on === this.on) return;
    this.on = on;
    if (!on) for (const r of this.rigs.values()) this.solid(r, false);
  }

  /** Частина моєї фури за хендлом колайдера: 0 тягач, 1 причіп, null — не моя. */
  myPart(h) {
    const t = this.truck;
    if (h === t.cabCol.handle || h === t.frameCol.handle) return 0;
    if (h === t.boxCol.handle || h === t.cargoCol.handle || h === t.legsCol.handle) return 1;
    return null;
  }

  inYard(x, z) {
    return this.level.places.some((p) => Math.abs(x - p.yard.x) <= p.yard.w / 2 + YARD_M && Math.abs(z - p.yard.z) <= p.yard.d / 2 + YARD_M);
  }

  /** Моя фура в плані: [тягач, причіп]. */
  mine(pad = 0) {
    const t = this.truck, a = t.tb.translation(), r = t.rb.translation();
    return [rect(a.x, a.z, t.yawOf(t.tb), 0, pad), rect(r.x, r.z, t.yawOf(t.rb), 1, pad)];
  }

  rig(seat) {
    let r = this.rigs.get(seat);
    if (r) return r;
    const { R, world } = this;
    r = { seat, cols: [], solid: false, offUntil: 0, pose: null, why: 'новий' };
    for (const [part, boxes] of [[0, BOX_T], [1, BOX_R]]) {
      const b = world.createRigidBody(R.RigidBodyDesc.kinematicVelocityBased().setTranslation(0, -50, 0));
      for (const [w, h, l, x, y, z] of boxes) {
        const c = world.createCollider(R.ColliderDesc.cuboid(w, h, l).setTranslation(x, y, z)
          .setFriction(0).setFrictionCombineRule(R.CoefficientCombineRule.Min).setRestitution(0)
          .setActiveEvents(R.ActiveEvents.CONTACT_FORCE_EVENTS).setContactForceEventThreshold(FORCE_MIN), b);
        c.setEnabled(false);
        r.cols.push(c);
        this.byCol.set(c.handle, { rig: r, part });
      }
      if (part === 0) r.tb = b; else r.rb = b;
    }
    this.rigs.set(seat, r);
    return r;
  }

  drop(r) {
    for (const c of r.cols) this.byCol.delete(c.handle);
    this.world.removeRigidBody(r.tb); this.world.removeRigidBody(r.rb);
    this.rigs.delete(r.seat); this.hits.delete(r.seat);
  }

  solid(r, on) {
    if (r.solid === on) return;
    r.solid = on;
    // вимикаємо колайдер цілком, а не групою 0: CCD моєї фури групи не питає й «упирався» б у примарну (25.10, двір депо)
    for (const c of r.cols) c.setEnabled(on);
  }

  /** Поставити тіло: позиція й курс (нахил — з рельєфу), швидкість прогнозу без складової «на мене» (до точки me). */
  put(b, x, z, h, lift, back, front, v, me) {
    const H = this.level.height, sh = Math.sin(h), ch = Math.cos(h);
    const hf = H(x + sh * front, z + ch * front), hb = H(x - sh * back, z - ch * back);
    this.e.set(-Math.atan2(hf - hb, front + back), h, 0);
    this.q.setFromEuler(this.e);
    b.setTranslation({ x, y: (hf * back + hb * front) / (front + back) + lift, z }, true);
    b.setRotation(this.q, true);
    let vx = v * sh, vz = v * ch;
    const dx = me.x - x, dz = me.z - z, d = Math.hypot(dx, dz);
    if (d > 0.1) { const c = (vx * dx + vz * dz) / d; if (c > 0) { vx -= c * dx / d; vz -= c * dz / d; } }
    b.setLinvel({ x: vx, y: 0, z: vz }, true);
  }

  /** Перед world.step: чужі фури — на прогноз «тепер», рішення «тверда чи примарна». */
  before() {
    const now = performance.now(), t = this.truck, g = this.ghosts;
    for (const r of this.rigs.values()) if (!g.names.has(r.seat) || r.seat === g.me) this.drop(r);
    if (!this.on) return;
    const a = t.tb.translation(), ra = t.rb.translation(), va = t.tb.linvel(), vr = t.rb.linvel();
    this.myVel[0] = { x: va.x, z: va.z }; this.myVel[1] = { x: vr.x, z: vr.z };
    const meYard = this.inYard(a.x, a.z) || this.inYard(ra.x, ra.z);
    let mine = null;
    for (const seat of g.names.keys()) {
      const p = g.predict(seat, now);
      if (!p) { const r = this.rigs.get(seat); if (r) { this.solid(r, false); r.pose = null; } continue; }
      const r = this.rig(seat);
      if (p.jump || !r.pose) { r.offUntil = now + JUMP_MS; r.off = 'стрибок'; }
      r.pose = p;
      const near = (x, z) => (Math.hypot(a.x - x, a.z - z) < Math.hypot(ra.x - x, ra.z - z) ? a : ra);
      this.put(r.tb, p.x, p.z, p.h, TRACTOR_Y, 2.4, 2.2, p.v, near(p.x, p.z));
      this.put(r.rb, p.rx, p.rz, p.ry, TRAILER_Y, 5.0, 5.6, p.v, near(p.rx, p.rz));
      // тверда чи примарна
      const rel = Math.hypot(va.x - p.v * Math.sin(p.h), va.z - p.v * Math.cos(p.h));
      let why = '';
      if (meYard || this.inYard(p.x, p.z) || this.inYard(p.rx, p.rz)) why = 'двір';
      else if (rel < SLOW) why = 'повільно';
      else if (now < r.offUntil) why = r.off || 'стрибок';
      if (!why && !r.solid) {
        // твердою стає лише там, де ми не перекриваємось (інакше Rapier вистрілив би нас одне з одного)
        mine = mine || this.mine(0.3);
        const hers = [rect(p.x, p.z, p.h, 0), rect(p.rx, p.rz, p.ry, 1)];
        if (mine.some((m) => hers.some((o) => overlap(m, o)))) why = 'крізь';
      }
      r.why = why;
      this.solid(r, !why);
    }
  }

  /** Після world.step: події контактної сили → удари, які я завдав. */
  after() {
    if (!this.on) { this.queue.clear(); return; }
    const now = performance.now(), touched = new Set();
    this.queue.drainContactForceEvents((e) => {
      const h1 = e.collider1(), h2 = e.collider2();
      let o = this.byCol.get(h1), mine = this.myPart(h2);
      if (!o) { o = this.byCol.get(h2); mine = this.myPart(h1); }
      if (!o || mine == null || !o.rig.pose) return;
      const f = e.totalForce();
      this.gather(o.rig, o.part, mine, f, now);
      touched.add(o.rig.seat);
    });
    for (const [seat, h] of this.hits) if (!touched.has(seat) || h.steps >= EPISODE) this.finish(seat, h, now);
    // відскок від чужої фури: швидкість «геть від неї» — не більше REBOUND (стіна з restitution 0 відскоку не дає;
    // це причіп через сідло пружинить тягач об неї)
    const c = this.calm;
    if (c && now < c.until) {
      for (const b of [this.truck.tb, this.truck.rb]) {
        const v = b.linvel(), vn = v.x * c.nx + v.z * c.nz;
        if (vn < -REBOUND) b.setLinvel({ x: v.x + (-REBOUND - vn) * c.nx, y: v.y, z: v.z + (-REBOUND - vn) * c.nz }, true);
      }
    }
  }

  /** Крок контакту з чужою частиною: накопичити імпульс (на неї), перший крок — хто наближався й де точка. */
  gather(r, part, mine, f, now) {
    const t = this.truck, my = (mine ? t.rb : t.tb).translation(), p = r.pose;
    const cx = part ? p.rx : p.x, cz = part ? p.rz : p.z, yaw = part ? p.ry : p.h;
    let nx = cx - my.x, nz = cz - my.z;
    const d = Math.hypot(nx, nz) || 1;
    nx /= d; nz /= d;
    // імпульс на неї — від мене до неї (знак totalForce залежить від порядку колайдерів)
    let fx = f.x, fz = f.z;
    if (fx * nx + fz * nz < 0) { fx = -fx; fz = -fz; }
    let h = this.hits.get(r.seat);
    if (!h) {
      const v = this.myVel[mine], meC = v.x * nx + v.z * nz;
      const herC = -(p.v * Math.sin(p.h) * nx + p.v * Math.cos(p.h) * nz);
      // моя точка в осях її частини (вбік, уздовж), у межах коробки — куди прикласти удар у неї
      const lx = my.x - cx, lz = my.z - cz, s = Math.sin(yaw), c = Math.cos(yaw);
      h = { part, mine, ix: 0, iz: 0, steps: 0, me: meC, her: herC,
        ox: clamp(lx * c - lz * s, -1.25, 1.25), oz: clamp(lx * s + lz * c, -FLAT[part][1], FLAT[part][1]) };
      this.hits.set(r.seat, h);
    }
    h.ix += fx * this.world.timestep; h.iz += fz * this.world.timestep;
    h.steps++;
    // відскок обмежуємо лише тому, хто бив: кого штовхнули, той і має відлетіти
    if (h.me > 0.3) this.calm = { nx, nz, until: now + REBOUND_MS };
  }

  /**
   * Контакт скінчився (чи затягнувся): частку імпульсу — адресату, якщо наближався я. Частка — менше з двох: SHARE мого
   * імпульсу, зважена тим, хто наближався, і ½·m·v — скільки дістала б така сама фура від непружного удару (мій імпульс
   * об «стіну» має в собі ще й поштовх причепа через сідло).
   */
  finish(seat, h, now) {
    this.hits.delete(seat);
    const r = this.rigs.get(seat);
    if (r) { r.offUntil = now + AFTER_MS; r.off = 'удар'; }
    const J = Math.hypot(h.ix, h.iz);
    if (h.me <= 0.3) { this.note('out', seat, J, 0, 'мене штовхнули'); return; }    // мене вдарили: я лише відлетів
    const w = h.me / (h.me + Math.max(0, h.her)), t = this.truck;
    const m = stdMass(t.coupled, h.mine);
    const sent = Math.min(J * SHARE * w, 0.5 * m * h.me), k = J > 0 ? sent / J : 0, ix = h.ix * k, iz = h.iz * k;
    if (sent < SEND_MIN) { this.note('out', seat, J, sent, 'слабко'); return; }
    if (now - (this.sentAt.get(seat) || 0) < GAP_MS) { this.note('out', seat, J, sent, 'частіше 4/с'); return; }
    this.sentAt.set(seat, now);
    this.server.input('hit', { to: seat, ix: Math.round(ix), iz: Math.round(iz), at: h.part, ox: +h.ox.toFixed(2), oz: +h.oz.toFixed(2) });
    this.note('out', seat, J, sent, 'послав');
    this.bump(sent / 20000, '💥 Удар — ' + this.nick(seat));
  }

  /** Кадр із сервера: удари, адресовані мені (h: [to, from, id, at, ix, iz, ox·10, oz·10, …]). */
  frame(f) {
    const h = f && f.h, me = this.ghosts.me;
    if (!Array.isArray(h) || me == null) return;
    for (let i = 0; i + 8 <= h.length; i += 8) {
      if (h[i] !== me || this.seen.has(h[i + 2])) continue;
      this.seen.add(h[i + 2]);
      if (this.seen.size > 64) this.seen.delete(this.seen.values().next().value);
      this.take(h[i + 1], h[i + 3], h[i + 4], h[i + 5], h[i + 6] / 10, h[i + 7] / 10);
    }
  }

  /** Удар від фури from: прикласти імпульс до моєї частини at у точці (ox вбік, oz уздовж), Δv ≤ DV_MAX. */
  take(from, at, ix, iz, ox, oz) {
    const t = this.truck, a = t.tb.translation(), ra = t.rb.translation();
    if (!this.on || this.inYard(a.x, a.z) || this.inYard(ra.x, ra.z)) { this.note('in', from, Math.hypot(ix, iz), 0, 'не взято'); return; }
    const r = this.rigs.get(from);
    if (r) { r.offUntil = performance.now() + AFTER_MS; r.off = 'удар'; }   // її прогноз ще «доїжджає» в мене — не доштовхувати
    this.calm = null;
    const b = at ? t.rb : t.tb;
    const mass = stdMass(t.coupled, at);
    const J = Math.hypot(ix, iz), cap = DV_MAX * mass, k = J > cap ? cap / J : 1;
    const p = b.translation(), yaw = t.yawOf(b), s = Math.sin(yaw), c = Math.cos(yaw);
    const px = p.x + ox * c + oz * s, pz = p.z - ox * s + oz * c;
    const v0 = b.linvel();
    b.applyImpulseAtPoint({ x: ix * k, y: 0, z: iz * k }, { x: px, y: p.y, z: pz }, true);
    const v1 = b.linvel();
    this.note('in', from, J * k, Math.hypot(v1.x - v0.x, v1.z - v0.z), at ? 'у причіп' : 'у тягач');
    this.bump(J * k / 20000, '💥 Тебе вдарив ' + this.nick(from));
  }

  nick(seat) { return this.ghosts.names.get(seat) || 'фура ' + (seat + 1); }

  bump(p, text) {
    p = clamp(p, 0.1, 1);
    this.say(text, 1800);
    try { this.sound.thump(); } catch { /* пусто */ }
    if (window.HPad && HPad.rumble) try { HPad.rumble(150 + p * 250, 0.4 + p * 0.6, 0.6); } catch { /* пусто */ }
  }

  note(dir, seat, J, sent, what) {
    this.log.push({ at: Math.round(performance.now()), dir, seat, J: Math.round(J), [dir === 'out' ? 'sent' : 'dv']: dir === 'out' ? Math.round(sent) : +sent.toFixed(2), what });
    if (this.log.length > 40) this.log.shift();
  }

  /** Що малювати (ghosts.js): поблизу — прогноз (тіло там, де б'ється), далі — плавна поза; примарна поблизу — прозора. */
  draw(seat, p) {
    const r = this.rigs.get(seat);
    if (!this.on || !r || !r.pose) return { p, alpha: 1 };
    const q = r.pose, a = this.truck.tb.translation(), d = Math.hypot(q.x - a.x, q.z - a.z);
    const w = clamp((FAR_DRAW - d) / (FAR_DRAW - NEAR_DRAW), 0, 1);
    const alpha = !r.solid && r.why !== 'стрибок' && r.why !== 'удар' && d < ALPHA_M ? ALPHA : 1;
    if (w <= 0) return { p, alpha };
    const L = (x, y) => x + (y - x) * w, LA = (x, y) => x + Math.atan2(Math.sin(y - x), Math.cos(y - x)) * w;
    return { p: { ...p, x: L(p.x, q.x), z: L(p.z, q.z), h: LA(p.h, q.h), rx: L(p.rx, q.rx), rz: L(p.rz, q.rz), ry: LA(p.ry, q.ry) }, alpha };
  }

  /** Для консолі: хто де і чому примарний, останні удари. */
  stats() {
    const out = { on: this.on, rigs: {}, log: this.log.slice(-12) };
    for (const r of this.rigs.values()) out.rigs[r.seat] = { solid: r.solid, why: r.why, x: r.pose && +r.pose.x.toFixed(2), z: r.pose && +r.pose.z.toFixed(2), v: r.pose && +r.pose.v.toFixed(2) };
    return out;
  }

  dispose() {
    for (const r of [...this.rigs.values()]) { try { this.drop(r); } catch { /* світ уже звільнено */ } }
    try { this.queue.free(); } catch { /* пусто */ }
    this.ghosts.crash = null;
  }
}
