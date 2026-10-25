// Трафік (T1, specs/fura.md «Хвиля D (продовження) — Житомир-клієнт і трафік»): машини на правих смугах level.lanes().
//
// Детерміновано: на смузі N машин рівними проміжками, s = (t·speed + зсув) mod довжина, зсув — від map.seed і номера
// смуги, t — годинник (Date.now; серверного часу в кадрі Чумаків нема, а годинники гравців NTP-синхронні до ~0,1 с),
// тож у всіх гравців ті самі машини. Між собою машини не б'ються. Не з'являються ближче DOCK_M до боксів і біля кінців смуги.
//
// Локально (лише в мене, сервер не знає): машина гальмує й чекає, коли фура (моя чи привид) на смузі ближче BRAKE_M
// попереду; після удару стоїть STOP_S. І те й те — зсув часу машини (lag), далі їде своїм ходом.
//
// Удар: найближчі до моєї фури машини (до MAX_PHYS у радіусі PHYS_R) — кінематичні коробки Rapier, фура б'ється об них, як
// об чужу фуру (вантаж б'ється від прискорення, truck.raw); Δv моєї фури за удар — не більше DV_CAP (машина легка: важка фура
// її відкидає, а не стає як укопана), далі машина для мене примарна, доки стоїть. Тост і звук — гачок crash.js carBump.
// Малювання — 2 InstancedMesh (легковик і бус), 2 draw calls, лише найближчі до моєї фури (VIS: 350 м і 40 машин на «мало»,
// 600 м і 100 на «багато»); вибірка — раз на PICK_S. __fura.traffic.stats() / list().

import { rect, overlap, carBump } from './crash.js';

const DENSITY = { off: 0, few: 1 / 450, many: 1 / 160 };   // машин на метр смуги
const END_M = 8;            // біля кінців смуги не малюємо (перехрестя: машина переходить на початок)
const DOCK_M = 20;          // як Fura.HitDockM
const BRAKE_M = 15, BRAKE_A = 4;   // фура попереду: стаємо за 15 м, гальмуємо ~4 м/с² заздалегідь
const STOP_S = 5;           // після удару стоїть
const PHYS_R = 90, MAX_PHYS = 8;
const DV_CAP = 3, EPISODE = 8;   // м/с: удар (до 8 кроків контакту) міняє швидкість моєї фури не більше (як DV_MAX у фур)
const VIS = { few: [350, 40], many: [600, 100] };   // малюємо найближчі до моєї фури: радіус, м, і стеля
const PICK_S = 0.3;         // вибірка видимих і «живих» машин — раз на стільки секунд
const CAR = { w: 0.9, h: 0.72, l: 2.2 };          // пів-розміри коробки колайдера

const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
function hash(a, b) {
  let h = (a * 374761393 + b * 668265263) | 0;
  h = (h ^ (h >>> 13)) * 1274126177;
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}

/** Геометрія з коробок з кольором вершин: [w, h, l, x, y, z, колір]; кузов білий — його фарбує колір екземпляра. */
function boxes(THREE, list) {
  const pos = [], nor = [], col = [];
  for (const [w, h, l, x, y, z, c] of list) {
    const g = new THREE.BoxGeometry(w, h, l).toNonIndexed();
    g.translate(x, y, z);
    const C = new THREE.Color(c);
    pos.push(...g.attributes.position.array); nor.push(...g.attributes.normal.array);
    for (let i = 0; i < g.attributes.position.count; i++) col.push(C.r, C.g, C.b);
    g.dispose();
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(nor, 3));
  g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  return g;
}
const WHEELS = (x, z0, z1) => [[0.25, 0.62, 0.62, x, 0.31, z0, 0x202020], [0.25, 0.62, 0.62, -x, 0.31, z0, 0x202020],
  [0.25, 0.62, 0.62, x, 0.31, z1, 0x202020], [0.25, 0.62, 0.62, -x, 0.31, z1, 0x202020]];
const MODELS = [
  // легковик: кузов, дах зі склом, фари
  [[1.76, 0.62, 4.3, 0, 0.66, 0, 0xffffff], [1.56, 0.5, 2.1, 0, 1.22, -0.25, 0x9fb2c4], [1.5, 0.08, 2.0, 0, 1.5, -0.25, 0xffffff],
    [1.5, 0.14, 0.04, 0, 0.78, 2.16, 0xfff4cc], [1.5, 0.12, 0.04, 0, 0.8, -2.16, 0x7a1010], ...WHEELS(0.8, 1.35, -1.35)],
  // бус
  [[1.86, 1.55, 4.6, 0, 1.13, -0.1, 0xffffff], [1.7, 0.55, 0.06, 0, 1.45, 2.14, 0x9fb2c4],
    [1.6, 0.14, 0.04, 0, 0.75, 2.16, 0xfff4cc], ...WHEELS(0.85, 1.45, -1.5)],
];
const PAINT = [0xc8ccd0, 0x2a3a5a, 0x8a1c1c, 0xe8e8e4, 0x222428, 0x3d5a3a, 0xd9a521, 0x6a6e74, 0x2f6fa8, 0xa0522d];

export class Traffic {
  constructor({ THREE, RAPIER, world, scene, level, truck, ghosts, say, sound, density }) {
    Object.assign(this, { T: THREE, R: RAPIER, world, scene, level, truck, ghosts, say, sound });
    this.seed = (level.map && level.map.seed) | 0;
    // смуги: накопичена довжина, точки біля боксів (там машин не видно)
    const docks = [];
    for (const p of level.places || []) for (const d of p.docks || []) docks.push(d);
    this.lanes = (level.lanes() || []).map((ln) => {
      const pts = ln.pts, s = [0];
      for (let i = 1; i < pts.length; i++) s.push(s[i - 1] + Math.hypot(pts[i].x - pts[i - 1].x, pts[i].z - pts[i - 1].z));
      const dock = pts.map((p) => docks.some((d) => Math.hypot(p.x - d.x, p.z - d.z) < DOCK_M));
      return { ...ln, s, L: s[s.length - 1], dock };
    }).filter((ln) => ln.L > 2 * END_M + 10);
    this.t = Date.now() / 1000; this.catch = 0;
    this.cars = [];
    this.meshes = MODELS.map((m) => {
      const mesh = new THREE.InstancedMesh(boxes(THREE, m), new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.55, metalness: 0.15 }), 1);
      mesh.frustumCulled = false; mesh.castShadow = true; mesh.receiveShadow = false; mesh.count = 0;
      scene.add(mesh);
      return mesh;
    });
    this.o = new THREE.Object3D(); this.e = new THREE.Euler(0, 0, 0, 'YXZ'); this.q = new THREE.Quaternion();
    // фізика: пул кінематичних коробок для найближчих машин
    this.pool = [];
    for (let i = 0; i < MAX_PHYS; i++) {
      const b = world.createRigidBody(RAPIER.RigidBodyDesc.kinematicPositionBased().setTranslation(0, -100 - i * 10, 0));
      const c = world.createCollider(RAPIER.ColliderDesc.cuboid(CAR.w, CAR.h, CAR.l).setFriction(0)
        .setFrictionCombineRule(RAPIER.CoefficientCombineRule.Min).setRestitution(0), b);
      c.setEnabled(false);
      this.pool.push({ b, c, car: null, solid: false, ep: null });
    }
    this.v0 = [{ x: 0, z: 0 }, { x: 0, z: 0 }];
    this.rawUntil = 0;
    this.n = { hits: 0, waits: 0 };
    this.log = [];
    this.setDensity(density);
  }

  /** «⚙ Графіка» → Трафік: off | few | many. Розклад машин — той самий у всіх з тим самим пунктом. */
  setDensity(d) {
    if (d === this.density) return;
    this.density = d;
    const k = DENSITY[d] || 0, cars = [];
    this.lanes.forEach((ln, li) => {
      const n = Math.floor(ln.L * k + hash(this.seed, li * 31 + 7));
      const off = hash(this.seed + 1, li) * ln.L;
      for (let j = 0; j < n; j++) {
        const id = li * 1000 + j, r = hash(this.seed + 2, id);
        cars.push({ id, ln, off: off + j * ln.L / n, model: r < 0.75 ? 0 : 1, paint: PAINT[Math.floor(hash(this.seed + 3, id) * PAINT.length)],
          lag: 0, f: 1, stopUntil: 0, ghostUntil: 0, p: null, slot: null });
      }
    });
    for (const sl of this.pool) this.free(sl);
    this.cars = cars;
    this.vis = []; this.act = []; this.pickT = -1;
    const cap = (VIS[d] || VIS.few)[1];
    this.meshes.forEach((m, i) => {
      if (m.instanceMatrix.count < cap) {
        const fresh = new this.T.InstancedMesh(m.geometry, m.material, cap);
        fresh.frustumCulled = false; fresh.castShadow = true;
        this.scene.remove(m); m.dispose(); this.scene.add(fresh); this.meshes[i] = fresh;
      }
      this.meshes[i].count = 0;
    });
  }

  /**
   * Вибірка (≈3/с, від часу машин — і під __fura.step): vis — найближчі до моєї фури в радіусі VIS (стеля), їх малюємо;
   * act — ті, кому потрібен локальний крок: поблизу будь-якої фури (гальмо, коробки) або ще стоять/розганяються.
   * Решта машин мапи — лише формула позиції, нічого не коштують.
   */
  pick() {
    this.pickT = this.t;
    const [R, cap] = VIS[this.density] || VIS.few, me = this.truck.tb.translation(), t = this.t;
    const hubs = [me];
    const g = this.ghosts;
    if (g && g.names) { const now = performance.now(); for (const seat of g.names.keys()) { if (seat === g.me) continue; const p = g.predict(seat, now); if (p) hubs.push(p); } }
    const vis = [], act = [], A = PHYS_R + 40;    // +40 м: за 0,3 с між вибірками і 20 м корпусу
    for (const c of this.cars) {
      const p = this.pose(c, t);
      if (!p) { if (c.f < 1 || t < c.stopUntil) act.push(c); continue; }
      const d = Math.hypot(p.x - me.x, p.z - me.z);
      if (d < R) vis.push([d, c]);
      if (c.f < 1 || t < c.stopUntil || hubs.some((q) => Math.abs(q.x - p.x) < A && Math.abs(q.z - p.z) < A)) act.push(c);
    }
    vis.sort((a, b) => a[0] - b[0]);
    this.vis = vis.slice(0, cap).map((x) => x[1]);
    this.act = act;
    const n = [0, 0], col = this.col || (this.col = new this.T.Color());
    for (const c of this.vis) { c.ix = n[c.model]++; this.meshes[c.model].setColorAt(c.ix, col.set(c.paint)); }
    this.meshes.forEach((m, i) => { m.count = n[i]; if (m.instanceColor) m.instanceColor.needsUpdate = true; });
  }

  /** Поза машини на час t: точка смуги, курс, нахил; null — схована (кінець смуги, біля боксів). */
  pose(c, t) {
    const ln = c.ln, L = ln.L;
    let s = ((t - c.lag) * ln.speed + c.off) % L;
    if (s < 0) s += L;
    if (s < END_M || s > L - END_M) return null;
    let lo = 0, hi = ln.s.length - 1;
    while (hi - lo > 1) { const m = (lo + hi) >> 1; if (ln.s[m] <= s) lo = m; else hi = m; }
    if (ln.dock[lo] || ln.dock[hi]) return null;
    const a = ln.pts[lo], b = ln.pts[hi], u = (s - ln.s[lo]) / (ln.s[hi] - ln.s[lo] || 1), d = ln.s[hi] - ln.s[lo] || 1;
    return { x: a.x + (b.x - a.x) * u, y: a.y + (b.y - a.y) * u, z: a.z + (b.z - a.z) * u,
      h: Math.atan2(b.x - a.x, b.z - a.z), pitch: -Math.atan2(b.y - a.y, d), s };
  }

  /** Фури на дорозі: моя (тягач, причіп) і привиди — точки вздовж корпусу через ~2,3 м (навскіс фура теж перекриває смугу). */
  rigs() {
    const out = [];
    const part = (x, z, h, l) => { for (let k = -l; k <= l + 0.01; k += l / Math.ceil(l / 1.15)) out.push({ x: x + Math.sin(h) * k, z: z + Math.cos(h) * k }); };
    const t = this.truck, a = t.tb.translation(), r = t.rb.translation();
    part(a.x, a.z, t.yawOf(t.tb), 3.4); part(r.x, r.z, t.yawOf(t.rb), 6.8);
    const g = this.ghosts;
    if (g && g.names) {
      const now = performance.now();
      for (const seat of g.names.keys()) {
        if (seat === g.me) continue;
        const p = g.predict(seat, now);
        if (p) { part(p.x, p.z, p.h, 3.4); part(p.rx, p.rz, p.ry, 6.8); }
      }
    }
    return out;
  }

  /** Перед world.step: час, гальмування за фурою, кінематичні коробки найближчих машин. */
  before(dt) {
    const k = clamp(this.catch, -dt / 2, dt);   // наздоганяння годинника — частинами, не більше кроку
    this.catch -= k; dt += k;
    this.t += dt;
    const t = this.t, tr = this.truck, me = tr.tb.translation();
    const va = tr.tb.linvel(), vr = tr.rb.linvel();
    this.v0[0] = { x: va.x, y: va.y, z: va.z }; this.v0[1] = { x: vr.x, y: vr.y, z: vr.z };
    if (!this.cars.length) return;
    if (!(t - this.pickT < PICK_S && t >= this.pickT)) this.pick();
    const rigs = this.rigs(), near = [];
    for (const c of this.act) {
      const p = c.p = this.pose(c, t);
      if (!p) continue;
      const dMe = Math.hypot(p.x - me.x, p.z - me.z);
      // далеко від усіх фур і їде — нічого локального (гальмо з 76 км/год до 15 м від фури — ~80 м)
      if (c.f === 1 && t >= c.stopUntil && !rigs.some((q) => Math.abs(q.x - p.x) < PHYS_R && Math.abs(q.z - p.z) < PHYS_R)) continue;
      // чекати фуру попереду: на смузі (вбік < 3 м) — стати за BRAKE_M до її краю, гальмуючи ~BRAKE_A заздалегідь
      let want = t < c.stopUntil ? 0 : 1;
      if (want) {
        const fx = Math.sin(p.h), fz = Math.cos(p.h);
        for (const q of rigs) {
          const dx = q.x - p.x, dz = q.z - p.z, along = dx * fx + dz * fz, side = Math.abs(dx * fz - dz * fx);
          if (along <= 0 || side > 1.25 + CAR.w + 0.4) continue;
          const gap = along - CAR.l - BRAKE_M;
          want = Math.min(want, gap <= 0 ? 0 : Math.sqrt(2 * BRAKE_A * gap) / c.ln.speed);
        }
      }
      const rate = (want < c.f ? 7 : 2.5) / c.ln.speed * dt;            // гальмо ~7 м/с², розгін ~2,5
      const f0 = c.f;
      c.f = clamp(c.f + clamp(want - c.f, -rate, rate), 0, 1);
      if (f0 === 1 && c.f < 1 && !(t < c.stopUntil)) this.n.waits++;
      c.lag += (1 - c.f) * dt;
      if (dMe <= PHYS_R) near.push([dMe, c]);
    }
    // фізика: найближчі MAX_PHYS; машина, що вже має коробку, її тримає (без телепорту)
    near.sort((a, b) => a[0] - b[0]);
    const keep = new Set(near.slice(0, MAX_PHYS).map((x) => x[1]));
    for (const sl of this.pool) if (sl.car && (!keep.has(sl.car) || !sl.car.p)) this.free(sl);
    for (const c of keep) {
      if (c.slot) continue;
      const sl = this.pool.find((x) => !x.car);
      if (!sl) break;
      sl.car = c; c.slot = sl; sl.fresh = true;
    }
    const mine = this.mineRects();
    for (const sl of this.pool) {
      const c = sl.car;
      if (!c) continue;
      const p = c.p;
      this.e.set(p.pitch, p.h, 0); this.q.setFromEuler(this.e);
      const pos = { x: p.x, y: p.y + CAR.h + 0.05, z: p.z }, was = sl.b.translation();
      if (Math.hypot(was.x - pos.x, was.z - pos.z) > 3) sl.fresh = true;    // стрибок (кінець смуги, час) — не «таран»
      if (sl.fresh) {
        if (sl.solid) { sl.solid = false; sl.c.setEnabled(false); }
        sl.b.setTranslation(pos, true); sl.b.setRotation(this.q, true); sl.fresh = false;
      }
      sl.b.setNextKinematicTranslation(pos); sl.b.setNextKinematicRotation(this.q);
      // тверда, якщо не стоїть після удару й не в моїй фурі (з'явилась усередині — крізь, доки не роз'їдемось)
      let solid = t >= c.ghostUntil;
      if (solid && !sl.solid && mine.some((m) => overlap(m, this.carRect(p, 0)))) solid = false;
      if (solid !== sl.solid) { sl.solid = solid; sl.c.setEnabled(solid); }
    }
  }

  /**
   * Після world.step: контакт з машиною — епізод (≤ EPISODE кроків). Δv моєї фури за епізод — не більше DV_CAP (машина
   * легка: на швидкості фура її «проламує» — далі машина для мене примарна); удар (Δv > 0,3 м/с) — машина стоїть STOP_S,
   * тост і звук; повільно вперлась — машина просто стіна.
   */
  after() {
    const t = this.t, tr = this.truck;
    let mine = null;
    for (const sl of this.pool) {
      const c = sl.car;
      if (!c || !sl.solid) continue;
      mine = mine || this.mineRects(0.15);
      const touch = mine.some((m) => overlap(m, this.carRect(c.p, 0.15)));
      let ep = sl.ep;
      if (touch) {
        if (!ep) ep = sl.ep = { dv: 0, steps: 0, rel: Math.hypot(this.v0[0].x - Math.sin(c.p.h) * c.ln.speed * c.f, this.v0[0].z - Math.cos(c.p.h) * c.ln.speed * c.f) };
        ep.steps++;
        let d = 0;
        // з вертикаллю: кінематична коробка під причепом підкидала б його (вантаж б'ється від вертикалі найбільше)
        const dvs = [tr.tb, tr.rb].map((b, i) => { const v = b.linvel(), v0 = this.v0[i], dx = v.x - v0.x, dy = Math.max(0, v.y - v0.y), dz = v.z - v0.z; d = Math.max(d, Math.hypot(dx, dy, dz)); return [b, v, v0, dx, dy, dz]; });
        const room = DV_CAP - ep.dv, k = d > room ? Math.max(0, room) / d : 1;
        if (k < 1) for (const [b, v, v0, dx, dy, dz] of dvs) b.setLinvel({ x: v0.x + dx * k, y: v.y - dy * (1 - k), z: v0.z + dz * k }, true);
        ep.dv += d * k;
        if (ep.dv > 0.3) this.rawUntil = performance.now() + 700;
      }
      if (!ep || (touch && ep.steps < EPISODE && ep.dv < DV_CAP - 1e-3)) continue;
      sl.ep = null;
      if (ep.dv <= 0.3) continue;
      c.stopUntil = t + STOP_S; c.f = 0;
      if (ep.dv >= DV_CAP - 1e-3) { c.ghostUntil = t + STOP_S; sl.solid = false; sl.c.setEnabled(false); }
      this.n.hits++;
      this.log.push({ at: Math.round(performance.now()), car: c.id, dv: +ep.dv.toFixed(2), rel: +(ep.rel * 3.6).toFixed(1), through: !sl.solid, cargo: +tr.cargo.toFixed(1) });
      if (this.log.length > 20) this.log.shift();
      carBump({ say: this.say, sound: this.sound }, ep.dv / DV_CAP, '💥 Удар — машина');
    }
  }

  /** Удар з машиною щойно був: вантаж б'ється без пом'якшення тюнінгом (як від чужої фури). */
  get raw() { return performance.now() < this.rawUntil; }

  mineRects(pad = 0) {
    const t = this.truck, a = t.tb.translation(), r = t.rb.translation();
    return [rect(a.x, a.z, t.yawOf(t.tb), 0, pad), rect(r.x, r.z, t.yawOf(t.rb), 1, pad)];
  }
  carRect(p, pad) { return { x: p.x, z: p.z, h: p.h, w: CAR.w + pad, l: CAR.l + pad }; }

  free(sl) {
    if (sl.car) sl.car.slot = null;
    sl.ep = null;
    sl.car = null;
    if (sl.solid) { sl.solid = false; sl.c.setEnabled(false); }
    sl.b.setTranslation({ x: 0, y: -100, z: 0 }, false);
  }

  /** Кадр: годинник до Date.now (після __fura.step наздоганяє), матриці екземплярів. */
  update(cam) {
    // плавно (≤ 0,05 с за кадр, через крок фізики — машина, що стоїть, поглинає й це): стрибок часу — це телепорт машин
    // просто у фуру; стрибком — лише після схованої вкладки
    const wall = Date.now() / 1000, d = wall - this.t;
    if (Math.abs(d) > 30) this.t += d;
    else this.catch = clamp(this.catch + d * 0.05, -0.05, 0.05);
    if (!this.cars.length) return;
    if (!(this.t - this.pickT < PICK_S && this.t >= this.pickT)) this.pick();
    const o = this.o;
    for (const c of this.vis) {
      const p = c.slot ? c.p : this.pose(c, this.t);
      if (!p) o.scale.setScalar(0);
      else { o.position.set(p.x, p.y, p.z); o.rotation.set(p.pitch, p.h, 0, 'YXZ'); o.scale.setScalar(1); }
      o.updateMatrix();
      this.meshes[c.model].setMatrixAt(c.ix, o.matrix);
    }
    for (const m of this.meshes) m.instanceMatrix.needsUpdate = true;
  }

  /** Для консолі й тестів: де машини зараз (на час t, без локальних зсувів — однаково в усіх). */
  list(t = this.t, n = 10) {
    const out = [];
    for (const c of this.cars) { if (out.length >= n) break; const p = this.pose({ ...c, lag: 0 }, t); if (p) out.push({ id: c.id, x: +p.x.toFixed(1), z: +p.z.toFixed(1) }); }
    return out;
  }
  stats() {
    return { density: this.density, lanes: this.lanes.length, cars: this.cars.length, vis: this.vis.length, act: this.act.length, t: +this.t.toFixed(2), ...this.n,
      phys: this.pool.filter((s) => s.car).map((s) => ({ id: s.car.id, solid: s.solid, f: +s.car.f.toFixed(2), x: +s.car.p.x.toFixed(1), z: +s.car.p.z.toFixed(1) })),
      log: this.log.slice(-8) };
  }

  dispose() {
    for (const m of this.meshes) { this.scene.remove(m); m.geometry.dispose(); m.material.dispose(); m.dispose(); }
    for (const sl of this.pool) { try { this.world.removeRigidBody(sl.b); } catch { /* світ уже звільнено */ } }
    this.pool = []; this.cars = [];
  }
}
