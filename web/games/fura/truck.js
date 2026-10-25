// Тягач 6×4 і напівпричіп 13,6 м на Rapier. Кожен — своє тіло з DynamicRayCastVehicleController (колеса —
// промені з підвіскою), між ними сферичний шарнір «сідло ↔ шворень», поки зчеплені. Відчеплений причіп стоїть
// передом на «ногах» (окремий колайдер, вмикається лише без тягача), трохи вище за раму тягача — так тягач
// заїжджає під нього заднім ходом, як у житті.
//
// Осі: вгору +Y, вперед +Z, праворуч −X. Одиниці — метри, кілограми, ньютони.
// Підвіска Rapier рахує силу × масу шасі (див. ray_cast_vehicle_controller.rs), тому жорсткість — «на кілограм»,
// а гальмо — це стеля імпульсу на колесо за крок, а не сила.

const V = (x, y, z) => ({ x, y, z });
const DOWN = V(0, -1, 0);
const AXLE = V(-1, 0, 0);

// ---- тягач
export const TRACTOR = {
  mass: { cab: 2600, frame: 4900 },
  wheelR: 0.52,
  // [x, y, z, рульове, ведуче]
  wheels: [
    [1.05, -0.2, 2.05, true, false], [-1.05, -0.2, 2.05, true, false],
    [1.0, -0.2, -1.55, false, true], [-1.0, -0.2, -1.55, false, true],
    [1.0, -0.2, -2.9, false, true], [-1.0, -0.2, -2.9, false, true],
  ],
  rest: 0.42,
  stiff: 30, comp: 1.7, relax: 2.3, slip: 1.5, side: 1.15,
  fifth: V(0, 0.16, -2.25),          // сідло (верх плити) в осях тягача
  maxSteer: 0.6,                     // ~34° на колесах
  power: 400000,                     // Вт (≈540 к.с.)
  fmax: 64000,                       // Н сумарно на ведучих колесах, на першій
  brake: 150,                        // стеля імпульсу на колесо за крок при повному гальмі
};

// ---- причіп
export const TRAILER = {
  mass: { body: 6800, cargo: 12000 },
  wheelR: 0.5,
  wheels: [-3.7, -5.0, -6.3].flatMap((z) => [[1.0, -0.38, z], [-1.0, -0.38, z]]),
  rest: 0.5,
  stiff: 12, comp: 1.0, relax: 1.4, slip: 1.5, side: 1.15,
  kingpin: V(0, 0, 5.6),             // шворень у осях причепа (низ рами)
  len: 13.6, w: 2.5, h: 2.75,
  brake: 330,
};

/**
 * «Чумак» з «Тентом» без тюнінгу — те саме, що `base` у data/fura/garage.json. Гараж (0.0.6) шле готові числа у виді
 * (`career.params`), фура бере їх через setParams; гість, «⚖ Рівні фури» і полігон без тест-драйву їдуть цим.
 * Константи TRACTOR/TRAILER не міняємо: з них crash.js бере «стандартну фуру» для ударів (маса в 💥 — завжди ця).
 */
export const BASE = {
  power: TRACTOR.power, fmax: TRACTOR.fmax, mass: TRACTOR.mass.cab + TRACTOR.mass.frame, steer: TRACTOR.maxSteer, sway: 1,
  brake: TRACTOR.brake, trailerBrake: TRAILER.brake, slip: TRACTOR.slip, side: TRACTOR.side, limits: 1, hurt: 1,
  trailerStiff: TRAILER.stiff, trailerRelax: TRAILER.relax, engineBrake: 0.06, shiftPause: 1, cabVol: 1, shake: 1,
  cruise: false, abs: false, allSeason: false, nav: false,
};
const NUM = ['power', 'fmax', 'mass', 'steer', 'sway', 'brake', 'trailerBrake', 'slip', 'side', 'limits', 'hurt',
  'trailerStiff', 'trailerRelax', 'engineBrake', 'shiftPause', 'cabVol', 'shake'];
const FLAG = ['cruise', 'abs', 'allSeason', 'nav'];
const ABS_K = 0.9;                   // ABS: гальмо колеса — не більше 90 % того, що шина тримає (колесо котиться, не ковзає)

/** Передачі. Тягнемо F = fmax · ratio (і не більше за P/v), понад vmax — відсічка. */
const AUTO = [8, 12, 17, 23, 30, 38, 47, 57, 66, 75, 84, 90].map((k) => k / 3.6);
const MANUAL = [13, 22, 34, 50, 68, 90].map((k) => k / 3.6);
const REV = 12 / 3.6;
const IDLE = 600, RED = 2100;

export class Truck {
  constructor(THREE, RAPIER, world, scene, opts = {}) {
    this.T = THREE; this.R = RAPIER; this.world = world; this.scene = scene;
    this.coupled = false; this.joint = null;
    this.gearMode = 'auto';            // 'auto' | 'manual' (H-перемикач чи пелюстки)
    this.gear = 1;                     // 1…N, −1 — задня, 0 — нейтраль
    this.rpm = IDLE;
    this.revHold = 0; this.fwdHold = 0;
    this.cargo = 100;                  // «ціле %»
    this.hit = 0;                      // сила останнього удару по вантажу (для звуку/спалаху)
    this.prevVel = null; this.acc = { x: 0, y: 0, z: 0 };
    this.steerNow = 0;
    this.lights = false;
    this.P = { ...BASE };              // числа фізики зараз (setParams)
    this.cruiseV = 0; this.cruiseI = 0; this.cruiseDropped = false;   // круїз-контроль: швидкість, м/с (0 — вимкнено)
    this.dragNow = 0; this.massNow = 1; this.vPrev = null;            // опір руху для автомата (canUp)
    this.raw = false;                  // 💥 удар чужої фури: вантаж б'ється без пом'якшення тюнінгом (main.js з crash)
    this.buildTractor();
    this.buildTrailer();
    this.place(opts.spawn, false);
  }

  // ------------------------------------------------------------ фізика
  buildTractor() {
    const { R, world } = this, C = TRACTOR;
    const b = world.createRigidBody(R.RigidBodyDesc.dynamic().setTranslation(0, 1.2, 0).setCanSleep(false)
      .setLinearDamping(0.02).setAngularDamping(0.4).setCcdEnabled(true));
    // кабіна спереду (висока), рама ззаду (низька: над нею піде причіп)
    this.cabCol = world.createCollider(R.ColliderDesc.cuboid(1.25, 1.3, 1.1).setTranslation(0, 1.0, 2.1).setMass(C.mass.cab).setFriction(0.5), b);
    this.frameCol = world.createCollider(R.ColliderDesc.cuboid(0.55, 0.22, 2.25).setTranslation(0, -0.12, -0.95).setMass(C.mass.frame).setFriction(0.5), b);
    const vc = world.createVehicleController(b);
    vc.indexUpAxis = 1;
    vc.setIndexForwardAxis = 2;
    for (const [x, y, z] of C.wheels) vc.addWheel(V(x, y, z), DOWN, AXLE, C.rest, C.wheelR);
    this.tune(vc, C);
    this.tb = b; this.tvc = vc;
  }

  buildTrailer() {
    const { R, world } = this, C = TRAILER;
    const b = world.createRigidBody(R.RigidBodyDesc.dynamic().setTranslation(0, 1.32, -10).setCanSleep(false)
      .setLinearDamping(0.02).setAngularDamping(0.4).setCcdEnabled(true));
    this.boxCol = world.createCollider(R.ColliderDesc.cuboid(C.w / 2, C.h / 2, C.len / 2).setTranslation(0, C.h / 2, 0)
      .setMass(C.mass.body).setFriction(0.5), b);
    // вантаж — низько, щоб центр ваги не був на даху
    this.cargoCol = world.createCollider(R.ColliderDesc.cuboid(1.1, 0.5, 5.8).setTranslation(0, 0.55, -0.6)
      .setMass(C.mass.cargo).setFriction(0.5), b);
    this.legsCol = world.createCollider(R.ColliderDesc.cuboid(1.0, 0.66, 0.15).setTranslation(0, -0.66, 3.2)
      .setMass(80).setFriction(1.2), b);
    const vc = world.createVehicleController(b);
    vc.indexUpAxis = 1;
    vc.setIndexForwardAxis = 2;
    for (const [x, y, z] of C.wheels) vc.addWheel(V(x, y, z), DOWN, AXLE, C.rest, C.wheelR);
    this.tune(vc, C);
    this.rb = b; this.rvc = vc;
  }

  tune(vc, C) {
    for (let i = 0; i < vc.numWheels(); i++) {
      vc.setWheelSuspensionStiffness(i, C.stiff);
      vc.setWheelSuspensionCompression(i, C.comp);
      vc.setWheelSuspensionRelaxation(i, C.relax);
      vc.setWheelMaxSuspensionTravel(i, 0.35);
      vc.setWheelMaxSuspensionForce(i, 400000);
      vc.setWheelFrictionSlip(i, C.slip);
      vc.setWheelSideFrictionStiffness(i, C.side);
    }
  }

  /**
   * Числа гаража (`career.params` з виду чи тест-драйв): потужність, тяга, маса тягача, кермо, гальма й ABS, шини,
   * підвіска причепа, пороги й шкода вантажу, гальмо двигуном, коробка. Чого нема — як у «Чумака». true — щось змінилось.
   */
  setParams(p) {
    const next = { ...BASE };
    if (p) {
      for (const k of NUM) if (Number.isFinite(+p[k]) && p[k] !== null && p[k] !== '') next[k] = +p[k];
      for (const k of FLAG) if (p[k] != null) next[k] = !!p[k];
      next.tractor = p.tractor || 'chumak'; next.trailer = p.trailer || 'tent';
    }
    const old = this.P;
    if (NUM.every((k) => old[k] === next[k]) && FLAG.every((k) => old[k] === next[k]) && old.tractor === next.tractor && old.trailer === next.trailer) return false;
    this.P = next;
    if (old.mass !== next.mass) {
      // кабіна й рама — у тій самій пропорції, що в «Чумака» (центр ваги той самий)
      const k = next.mass / (TRACTOR.mass.cab + TRACTOR.mass.frame);
      this.cabCol.setMass(TRACTOR.mass.cab * k);
      this.frameCol.setMass(TRACTOR.mass.frame * k);
    }
    for (let i = 0; i < this.tvc.numWheels(); i++) {
      this.tvc.setWheelFrictionSlip(i, next.slip);
      this.tvc.setWheelSideFrictionStiffness(i, next.side);
    }
    for (let i = 0; i < this.rvc.numWheels(); i++) {
      this.rvc.setWheelFrictionSlip(i, next.slip * TRAILER.slip / TRACTOR.slip);
      this.rvc.setWheelSideFrictionStiffness(i, next.side * TRAILER.side / TRACTOR.side);
      this.rvc.setWheelSuspensionStiffness(i, next.trailerStiff);
      this.rvc.setWheelSuspensionRelaxation(i, next.trailerRelax);
    }
    if (!next.cruise) this.cruiseV = 0;
    return true;
  }

  /** Круїз (коробка, щабель 1): увімкнути на теперішній швидкості чи вимкнути. 'none' — не куплено, 'slow' — замало. */
  toggleCruise() {
    if (!this.P.cruise) return 'none';
    if (this.cruiseV) { this.cruiseV = 0; return 'off'; }
    const v = this.speed();
    if (this.gear < 1 || v < 20 / 3.6) return 'slow';
    this.cruiseV = v; this.cruiseI = 0;
    return 'on';
  }

  /** Поставити фуру: spawn = {tractor:{x,z}, trailer:{x,z}}, coupled — одразу зчеплено (причіп під сідлом). */
  place(spawn, coupled) {
    // spawn: {tractor:{x,z,yaw?,y?}, trailer:{x,z,yaw?,y?}} — y — висота землі (на мапі з рельєфом), yaw — курс
    const s = spawn || { tractor: { x: 0, z: 17 }, trailer: { x: 0, z: 0 } };
    this.unjoin();
    const zero = V(0, 0, 0);
    const ry = s.trailer.yaw || 0, ty = coupled ? ry : (s.tractor.yaw || 0);
    const d = TRAILER.kingpin.z - TRACTOR.fifth.z;
    const tx = coupled ? s.trailer.x + Math.sin(ry) * d : s.tractor.x;
    const tz = coupled ? s.trailer.z + Math.cos(ry) * d : s.tractor.z;
    const tg = coupled ? (s.trailer.y || 0) : (s.tractor.y || 0);
    for (const [b, x, y, z, yaw] of [[this.tb, tx, tg + 1.18, tz, ty], [this.rb, s.trailer.x, (s.trailer.y || 0) + (coupled ? 1.3 : 1.36), s.trailer.z, ry]]) {
      b.setTranslation(V(x, y, z), true); b.setRotation(this.yawQuat(yaw), true); b.setLinvel(zero, true); b.setAngvel(zero, true);
    }
    this.legsCol.setEnabled(!coupled);
    if (coupled) this.join();
    this.gear = 1; this.rpm = IDLE; this.cargo = 100; this.steerNow = 0;
    this.settle();
  }

  /** Після телепорту фура осідає на підвіску — це не удар по вантажу: датчик мовчить пів секунди. */
  settle() { this.prevVel = null; this.acc = { x: 0, y: 0, z: 0 }; this.calm = 30; this.vPrev = null; this.dragNow = 0; }

  /** Поставити на колеса там, де стоїш (перекинувся, застряг): та сама точка й курс, на метр вище. */
  rescue() {
    const t = this.tb.translation();
    const yaw = this.yawOf(this.tb);
    const keep = this.coupled;
    const fwd = V(Math.sin(yaw), 0, Math.cos(yaw));
    const q = this.yawQuat(yaw);
    const zero = V(0, 0, 0);
    this.unjoin();
    this.settle();
    const g = this.ground || (() => 0);       // висота землі: main.js дає її з мапи (на полігоні — нуль)
    this.tb.setTranslation(V(t.x, g(t.x, t.z) + 1.6, t.z), true); this.tb.setRotation(q, true);
    this.tb.setLinvel(zero, true); this.tb.setAngvel(zero, true);
    if (keep) {
      const d = TRAILER.kingpin.z - TRACTOR.fifth.z;
      const rx = t.x - fwd.x * d, rz = t.z - fwd.z * d;
      this.rb.setTranslation(V(rx, g(rx, rz) + 1.7, rz), true); this.rb.setRotation(q, true);
      this.rb.setLinvel(zero, true); this.rb.setAngvel(zero, true);
      this.legsCol.setEnabled(false);
      this.join();
    } else {
      const r = this.rb.translation(), up = this.upOf(this.rb);
      if (up < 0.5) {   // причіп лежить на боці — теж підняти
        this.rb.setTranslation(V(r.x, g(r.x, r.z) + 1.8, r.z), true); this.rb.setRotation(this.yawQuat(this.yawOf(this.rb)), true);
        this.rb.setLinvel(zero, true); this.rb.setAngvel(zero, true);
      }
    }
  }

  /** Де стоїть причіп: {x, z, yaw, y — земля під ним} (для place, щоб лишити причіп на місці). */
  rbPose() { const r = this.rb.translation(); return { x: r.x, z: r.z, yaw: this.yawOf(this.rb), y: (this.ground || (() => 0))(r.x, r.z) }; }

  yawOf(b) { const q = b.rotation(); return Math.atan2(2 * (q.w * q.y + q.x * q.z), 1 - 2 * (q.y * q.y + q.x * q.x)); }
  yawQuat(yaw) { return { x: 0, y: Math.sin(yaw / 2), z: 0, w: Math.cos(yaw / 2) }; }
  upOf(b) { const q = b.rotation(); return 1 - 2 * (q.x * q.x + q.z * q.z); }

  worldPoint(b, p) {
    const T = this.T, t = b.translation(), r = b.rotation();
    return new T.Vector3(p.x, p.y, p.z).applyQuaternion(new T.Quaternion(r.x, r.y, r.z, r.w)).add(new T.Vector3(t.x, t.y, t.z));
  }

  /** Наскільки шворень над сідлом: {dx: вбік/вперед у площині, dy: висота}, і чи можна чіплятись. */
  hitch() {
    const a = this.worldPoint(this.tb, TRACTOR.fifth), k = this.worldPoint(this.rb, TRAILER.kingpin);
    const flat = Math.hypot(a.x - k.x, a.z - k.z), dy = k.y - a.y;
    const yawDiff = Math.abs(Math.atan2(Math.sin(this.yawOf(this.tb) - this.yawOf(this.rb)), Math.cos(this.yawOf(this.tb) - this.yawOf(this.rb))));
    const v = this.tb.linvel();
    const slow = Math.hypot(v.x, v.z) < 1.5;
    return { flat, dy, yawDiff, ok: !this.coupled && flat < 0.45 && dy > -0.25 && dy < 0.35 && yawDiff < 0.9 && slow, slow };
  }

  join() {
    if (this.joint) return;
    const R = this.R;
    const jd = R.JointData.spherical(TRACTOR.fifth, TRAILER.kingpin);
    this.joint = this.world.createImpulseJoint(jd, this.tb, this.rb, true);
    this.joint.setContactsEnabled(false);
    this.coupled = true;
    this.legsCol.setEnabled(false);
  }

  unjoin() {
    if (this.joint) { this.world.removeImpulseJoint(this.joint, true); this.joint = null; }
    this.coupled = false;
  }

  /** Зчепити, якщо стоїмо під причепом; відчепити, якщо зчеплені й майже стоїмо. Повертає, що сталось. */
  toggleCouple() {
    if (this.coupled) {
      const v = this.tb.linvel();
      if (Math.hypot(v.x, v.z) > 1) return 'fast';
      this.legsCol.setEnabled(true);
      this.unjoin();
      return 'off';
    }
    const h = this.hitch();
    if (!h.ok) return h.flat < 2.5 ? 'near' : 'far';
    this.join();
    return 'on';
  }

  speed() { return this.tvc.currentVehicleSpeed(); }

  /** Вантаж замовлення: маса (кг) у причепі й крихкість (0 — не б'ється, 1 — глечики, 1,3 — кахлі). */
  setCargo(mass, fragile) {
    this.cargoCol.setMass(Math.max(500, mass || TRAILER.mass.cargo));
    this.fragile = fragile == null ? 1 : fragile;
  }

  /**
   * «Автозчеп»: тягач плавно (за sec с) їде під шворень і чіпляється. Плавно — бо сервер судить позу 4 рази на секунду
   * і стрибок на 30 м прийняв би за телепорт. Поки ковзаємо, тягач ні з чим не стикається (інакше штовхнув би причіп).
   */
  glideUnder(sec, done) {
    const T = this.T, r = this.rb.translation(), ry = this.yawOf(this.rb), d = TRAILER.kingpin.z - TRACTOR.fifth.z;
    const g = this.ground || (() => 0);
    const tx = r.x + Math.sin(ry) * d, tz = r.z + Math.cos(ry) * d;
    const a = this.tb.translation(), q = this.tb.rotation();
    this.glide = {
      t: 0, sec, done,
      p0: new T.Vector3(a.x, a.y, a.z), p1: new T.Vector3(tx, Math.max(g(tx, tz) + 1.2, r.y - 0.12), tz),
      q0: new T.Quaternion(q.x, q.y, q.z, q.w), q1: new T.Quaternion(0, Math.sin(ry / 2), 0, Math.cos(ry / 2)),
    };
    for (const c of [this.cabCol, this.frameCol]) c.setCollisionGroups(0);
  }

  stepGlide(dt) {
    const G = this.glide, T = this.T;
    G.t += dt;
    const k = Math.min(1, G.t / G.sec), e = k * k * (3 - 2 * k);
    const p = new T.Vector3().lerpVectors(G.p0, G.p1, e), q = new T.Quaternion().slerpQuaternions(G.q0, G.q1, e);
    this.tb.setTranslation(p, true); this.tb.setRotation(q, true);
    this.tb.setLinvel({ x: 0, y: 0, z: 0 }, true); this.tb.setAngvel({ x: 0, y: 0, z: 0 }, true);
    if (k >= 1) {
      this.glide = null;
      for (const c of [this.cabCol, this.frameCol]) c.setCollisionGroups(0xFFFFFFFF);
      this.join();
      this.settle();
      G.done && G.done();
    }
  }

  /** Один крок фізики керування: input з Input.poll, dt — крок (1/60). */
  drive(inp, dt) {
    if (this.glide) { this.stepGlide(dt); return; }
    const C = TRACTOR, P = this.P, vc = this.tvc;
    const v = this.speed();
    const av = Math.abs(v);
    // що тягне фуру назад (гора, кочення), згладжене ~0,5 с: тяга минулого кроку мінус m·a — автомату, щоб знати,
    // чи потягне вища передача (на відсічці тяга 0, тож згладжуємо саме опір, а не прискорення)
    const mass = this.tb.mass() + (this.coupled ? this.rb.mass() : 0);
    if (this.vPrev != null) this.dragNow += ((this.forceNow || 0) - mass * (v - this.vPrev) / dt - this.dragNow) * Math.min(1, dt / 0.5);
    this.vPrev = v; this.massNow = mass;

    // ---- кермо: колеса доганяють бажаний кут (рульова ~0,25 с від упору до упору)
    const want = inp.steer * P.steer;
    this.steerNow += Math.max(-dt * 4, Math.min(dt * 4, want - this.steerNow));
    // мінус: додатний кут Rapier повертає ліворуч, а наше «кермо +» — праворуч
    for (let i = 0; i < 2; i++) vc.setWheelSteering(i, -this.steerNow);

    // ---- передачі
    let throttle = inp.throttle, brake = inp.brake;
    // круїз: тримає швидкість газом (а на спуску — легким гальмом); газ, гальмо, ручник чи не та передача — знімають
    if (this.cruiseV) {
      if (throttle > 0.05 || brake > 0.05 || inp.handbrake || this.gear < 1 || !P.cruise) { this.cruiseV = 0; this.cruiseDropped = true; }
      else {
        const e = this.cruiseV - v;
        this.cruiseI = Math.max(-0.3, Math.min(0.6, this.cruiseI + e * dt * 0.15));
        throttle = Math.max(0, Math.min(1, e * 0.6 + this.cruiseI));
        brake = e < -1.5 ? Math.min(0.3, (-e - 1.5) * 0.12) : 0;
      }
    }
    // H-перемикач бере коробку на себе, лише коли в ньому ввімкнули передачу: інакше з відкаліброваним
    // перемикачем у нейтралі фура вічно стояла б на N, хоч людина їздить автоматом
    if (inp.hGear && inp.hGear !== 'N' && !this.hUsed) { this.hUsed = true; this.gearMode = 'manual'; }
    if (inp.hGear == null) this.hUsed = false;           // перемикач від'єднали — коробка знову своя
    if (this.gearMode === 'auto') {
      // «Аркадна» задня: стоїш і тримаєш гальмо — вмикається R, і тоді гальмо = газ назад
      if (this.gear > 0 && av < 0.4 && brake > 0.3 && throttle < 0.05) { this.revHold += dt; if (this.revHold > 0.45) { this.gear = -1; this.revHold = 0; } } else this.revHold = 0;
      if (this.gear < 0 && av < 0.4 && throttle > 0.3 && brake < 0.05) { this.fwdHold += dt; if (this.fwdHold > 0.25) { this.gear = 1; this.fwdHold = 0; } } else this.fwdHold = 0;
      if (this.gear < 0) { const t = throttle; throttle = brake; brake = t; }
      if (this.gear > 0) {
        const top = AUTO[this.gear - 1];
        if (av > top * 0.92 && this.gear < AUTO.length && this.canUp(av, throttle)) this.gear++;
        // «швидке перемикання» (коробка, щабель 2: shiftPause 0,5) — автомат раніше скидає передачу на підйомі
        else if (this.gear > 1 && av < AUTO[this.gear - 2] * (0.62 + (1 - P.shiftPause) * 0.26)) this.gear--;
      }
    } else if (this.hUsed) {
      this.gear = inp.hGear === 'N' ? 0 : inp.hGear === 'R' ? -1 : +inp.hGear;
    }
    if (inp.hit.has('gearUp')) this.shift(+1);
    if (inp.hit.has('gearDown')) this.shift(-1);

    // ---- двигун
    const table = this.gearMode === 'manual' ? MANUAL : AUTO;
    const top = this.gear < 0 ? REV : this.gear > 0 ? table[this.gear - 1] : 0;
    const ratio = top ? table[0] / Math.max(top, 0.1) : 0;   // перша — 1, вища — менше
    const clutchIn = inp.clutch > 0.5 || this.gear === 0;
    const wheelRpm = top ? IDLE + (av / top) * (RED - IDLE) : IDLE;
    const rpmGoal = clutchIn ? IDLE + throttle * (RED - IDLE) * 0.9 : Math.max(IDLE, wheelRpm);
    this.rpm += (rpmGoal - this.rpm) * Math.min(1, dt * 8);
    let force = 0;
    if (!clutchIn && top && throttle > 0) {
      force = this.pull(ratio, av) * throttle;
      if (av > top) force = 0;                                  // відсічка
      if (this.gearMode === 'manual' && av < top * 0.12 && this.gear > 2) force *= 0.35;   // «душить» на високій
      if (this.gear < 0) force = -force * 0.6;
    }
    const drives = C.wheels.map((w) => w[4]);
    const nDrive = drives.filter(Boolean).length;
    for (let i = 0; i < drives.length; i++) vc.setWheelEngineForce(i, drives[i] ? force / nDrive : 0);
    this.forceNow = force;

    // ---- гальма: педаль на всі, ручник — задні тягача й причеп; легке гальмо двигуном без газу
    const engineBrake = throttle < 0.02 && top && !clutchIn ? P.engineBrake : 0;
    const b = Math.max(brake, engineBrake);
    // ABS (гальма, щабель 3): педаль не дає колесу більше, ніж шина тримає, — колесо котиться й тримає бік; ручник — без ABS
    const abs = (c, i, imp) => (P.abs && imp > 0 ? Math.min(imp, Math.max(1, c.wheelSuspensionForce(i) || 0) * dt * c.wheelFrictionSlip(i) * ABS_K) : imp);
    for (let i = 0; i < vc.numWheels(); i++) {
      const hb = inp.handbrake && i >= 2;
      vc.setWheelBrake(i, (hb ? P.brake : abs(vc, i, b * P.brake)) + 0.4);
    }
    const rvc = this.rvc;
    for (let i = 0; i < rvc.numWheels(); i++) rvc.setWheelBrake(i, (inp.handbrake || !this.coupled ? P.trailerBrake : abs(rvc, i, b * P.trailerBrake)) + 0.4);
    this.throttleNow = throttle; this.brakeNow = b;
  }

  /** Тяга на колесах при повному газі: передача ratio (перша — 1), швидкість av, м/с. */
  pull(ratio, av) { return Math.min(this.P.fmax * Math.pow(ratio, 0.85), this.P.power / Math.max(av, 2)); }

  /**
   * Автомат «швидке перемикання» (коробка, щабель 2): на підйомі вгору не перемикає, якщо на вищій фура вже не
   * розганялась би (інакше — вгору, тяга падає, вниз, і так по колу: «полювання» передач). Без щабля — як було.
   */
  canUp(av, throttle) {
    if (this.P.shiftPause >= 1 || throttle < 0.5) return true;
    const r1 = AUTO[0] / AUTO[this.gear - 1], r2 = AUTO[0] / AUTO[this.gear];
    return (this.pull(r2, av) * throttle - this.dragNow) / this.massNow > 0.05 || this.pull(r1, av) <= this.pull(r2, av);
  }

  shift(d) {
    if (this.gearMode === 'auto') {
      // пелюстки в автоматі: + з R → у D, − з D на місці → R
      if (d > 0 && this.gear < 0) this.gear = 1;
      else if (d < 0 && this.gear === 1 && Math.abs(this.speed()) < 0.5) this.gear = -1;
      else { this.gearMode = 'manual'; this.gear = Math.max(-1, Math.min(MANUAL.length, this.gear > MANUAL.length ? MANUAL.length : this.gear + d)); }
      return;
    }
    this.gear = Math.max(-1, Math.min(MANUAL.length, this.gear + d));
  }

  setAuto() { this.gearMode = 'auto'; this.hUsed = false; if (this.gear >= 0) this.gear = 1; }

  /** Після world.step: прискорення причепа → удари по вантажу. */
  afterStep(dt) {
    const b = this.rb, v = b.linvel();
    if (this.prevVel) {
      const ax = (v.x - this.prevVel.x) / dt, ay = (v.y - this.prevVel.y) / dt, az = (v.z - this.prevVel.z) / dt;
      const k = Math.min(1, dt / 0.06);           // згладжування ~60 мс: шум кроку фізики не рахуємо
      this.acc.x += (ax - this.acc.x) * k; this.acc.y += (ay - this.acc.y) * k; this.acc.z += (az - this.acc.z) * k;
      // у осях причепа: вбік, вздовж, вертикаль
      const T = this.T, r = b.rotation();
      const inv = new T.Quaternion(r.x, r.y, r.z, r.w).invert();
      const a = new T.Vector3(this.acc.x, this.acc.y, this.acc.z).applyQuaternion(inv);
      const over = (x, t) => Math.max(0, Math.abs(x) - t);
      // Лінійно від перевищення: горби на 30 км/год — кілька відсотків, удар об дерево — десяток-два, а не все.
      // Пороги: трясе вгору-вниз (ями) найчутливіше, бокове — лише понад звичайний поворот, гальмо — лише різке.
      // Тюнінг (підвіска, «Пух», «Ластівка»): пороги × limits, шкода × hurt — лише від доріг: удар чужої фури (raw) — як у всіх
      const L = this.raw ? 1 : this.P.limits, H = this.raw ? 1 : this.P.hurt;
      const hurt = (3 * over(a.y, 1.8 * L) + 0.5 * over(a.x, 4.5 * L) + 0.8 * over(a.z, 5.0 * L)) * H;
      if (this.calm > 0) this.calm--;
      else if (hurt > 0) {
        const loss = hurt * dt * 8 * (this.fragile == null ? 1 : this.fragile);
        this.cargo = Math.max(0, this.cargo - loss);
        this.hit = Math.max(this.hit, loss);
      }
      this.gforce = { lat: a.x, lon: a.z, vert: a.y };
    }
    this.prevVel = { x: v.x, y: v.y, z: v.z };
  }

  update(dt, predicateT, predicateR) {
    this.tvc.updateVehicle(dt, undefined, undefined, predicateT);
    this.rvc.updateVehicle(dt, undefined, undefined, predicateR);
  }
}
