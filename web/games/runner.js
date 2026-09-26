/*
  Пакет «runner»: Стрибозаври (dino), Забіг дня (dino-daily), Лелеки (storks) — один файл на три гри
  (Client: "runner" у всіх трьох GameInfo). Специфікація — docs/games/specs/dino.md (§5 — мережа, §6 — клієнт),
  dino-daily.md, storks.md.

  Будова:
    1. RunnerSim — ДОСЛІВНЕ дзеркало src/Hlechyky/Games/Impl/RunnerSim.cs: цілі числа в суб-пікселях (16 на px),
       крок 20 мс, курс від зерна (xorshift32), стрибок/політ, перемотування. Без DOM, лежить у window.RunnerSim
       ще до HGames.register — його ганяє стенд docs/games/dev/runner-check.html (хеш парності з C#).
    2. Мережа й годинник: свій герой рахується тут же з власного вводу (передбачення), сервер — суддя; кадр
       звіряє стан на кроці f.s і, якщо розійшлись, перераховує хвіст. Чужі — інтерполяція кадрів.
    3. Малювання на canvas: статичне (небо, гори, село) — в offscreen-шарах, спрайти — в кеші.
    4. HUD, керування (клавіатура, пад, палець, мишка), звук, три HGames.register.

  Дріт: вид { mode, ph, s, seed, readySteps, pmCap, snowOpt|featherOpt, plays, alive, place, points, p, … },
  кадр { s, ph, d?, m?, p, sn?, pg?, ev? }; p[seat] Стрибозавра = [lag, y, vy, mode, stun, boost, eggs, snow, lp, lt, hs],
  Лелеки = [y, vy, mode, feather, ifr, lp]. Ввід — Input('in', { s, k }): k = 1 стрибок тримають, 2 ↓, 4 натиск.
*/
(() => {
  'use strict';

  // =============================================================================================
  // 1. Симуляція — дзеркало RunnerSim.cs. Будь-яка правка — в обох файлах одночасно.
  // =============================================================================================

  const SUB = 16, STEP_MS = 20, SEATS = 8, FUTURE_MAX = 4, REWIND_MAX = 15;
  const NO_GROUND = -2147483648, SNOW_ID_BASE = 1 << 20, INT_MIN = -2147483648;
  const SNAP_N = 16, LOG_N = 64, OB_N = 64, PK_N = 32, SN_N = 16, EV_N = 64;
  const DINO = 0, STORKS = 1;

  const K = {
    Low: 1, Low2: 2, High: 3, Wide: 4, Icicle: 5, Ptero: 6, Pit: 7, Hill: 8, Snow: 9,
    Chimney: 20, Tree: 21, Pole: 22, Wire: 23, Nest: 24, Kite: 25,
    Combo: 10, Gate: 26, Chimney2: 27,
    Egg: 1, Pepper: 2, SnowBall: 3,
  };
  const solid = (kind) => kind !== K.Pit && kind !== K.Hill;

  const D = {
    JumpV: 160, G: 14, GHold: 6, HoldMax: 14, FastFall: -160, VyMin: -400,
    CoyoteSteps: 2, BufferSteps: 3,
    HitW: 30 * 16, HitH: 44 * 16, DuckW: 36 * 16, DuckH: 24 * 16, FootX: 15 * 16,
    StunSteps: 30, PitStun: 50, PitDepth: 384, PitOut: 16 * 16,
    StunMult: 40, BoostMult: 125, RecoverMult: 108, BoostSteps: 100,
    LagMin: -1280, AvD0: 8960, AvDmin: 3200,
    PteroV: 64, PteroWake: 480 * 16,
    SnowAhead: 320 * 16, SnowW: 26 * 16, SnowH: 26 * 16, SnowRoom: 100 * 16, SnowShift: 40 * 16, SnowTries: 10,
    PickBox: 24 * 16, AirEgg: 104 * 16, TopEgg: 66 * 16, PickChance: 22, SubPerMetre: 320,
  };
  const ST = {
    G: 8, Flap: 128, VyMin: -200, StartY: 150 * 16, Ceiling: 268 * 16, Sky: 290,
    HitW: 32 * 16, HitH: 22 * 16, IFrames: 40, MinYAfterHit: 10 * 16, GroundId: -2,
  };

  const RULES = [
    {
      mode: DINO, speed0: 112, speedMax: 224, ramp: 32,
      firstAt: 900 * 16, lookahead: 1600 * 16, behind: D.AvD0 + 1600 * 16,
      gmin0: 60, gminDrop: 28, gmax0: 100, gmaxDrop: 50, specialKind: K.Pit, specialGap: 10,
      events: [K.Low, K.Low2, K.High, K.Wide, K.Icicle, K.Ptero, K.Pit, K.Hill, K.Combo],
      weights: [
        [40, 10, 8, 6, 0, 0, 0, 6, 0],
        [32, 14, 12, 8, 10, 0, 8, 6, 0],
        [26, 16, 14, 10, 12, 8, 8, 6, 0],
        [22, 16, 16, 12, 12, 10, 8, 4, 10],
        [18, 16, 16, 12, 12, 12, 8, 4, 10],
      ],
    },
    {
      mode: STORKS, speed0: 96, speedMax: 192, ramp: 40,
      firstAt: 700 * 16, lookahead: 1600 * 16, behind: 1200 * 16,
      gmin0: 55, gminDrop: 25, gmax0: 90, gmaxDrop: 40, specialKind: K.Gate, specialGap: 8,
      events: [K.Chimney, K.Tree, K.Gate, K.Pole, K.Nest, K.Kite, K.Chimney2],
      weights: [
        [40, 20, 0, 14, 10, 0, 0],
        [34, 18, 14, 14, 10, 0, 10],
        [28, 16, 20, 14, 10, 12, 0],
        [24, 14, 24, 14, 10, 14, 0],
        [22, 14, 26, 12, 10, 16, 0],
      ],
    },
  ];

  const idiv = (a, b) => Math.floor(a / b);   // лише для невід'ємних — так само, як «/» у C#

  function speedOf(R, run) { return Math.min(R.speedMax, R.speed0 + idiv(run, R.ramp)); }

  function paceOf(R, run) {
    if (run <= 0) return 0;
    const k = idiv(run, R.ramp), kMax = R.speedMax - R.speed0;
    if (k <= kMax) return R.ramp * (k * R.speed0 + idiv(k * (k - 1), 2)) + (run - k * R.ramp) * (R.speed0 + k);
    return R.ramp * (kMax * R.speed0 + idiv(kMax * (kMax - 1), 2)) + (run - kMax * R.ramp) * R.speedMax;
  }

  function runAtOf(R, x) {
    if (x <= 0) return 0;
    let lo = 0, hi = 1 << 22;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (paceOf(R, mid) >= x) hi = mid; else lo = mid + 1;
    }
    return lo;
  }

  const avD = (run) => Math.max(D.AvDmin, D.AvD0 - idiv(run * 4, 5));

  function Rng(seed) {
    let x = seed >>> 0;
    if (x === 0) x = 0x9E3779B9;
    return {
      u32() {
        x ^= x << 13; x >>>= 0;
        x ^= x >>> 17;
        x ^= x << 5; x >>>= 0;
        return x;
      },
      next(n) { return this.u32() % n; },
    };
  }

  const obNew = () => ({ id: 0, kind: 0, x: 0, w: 0, base: 0, h: 0, since: 0, by: -1 });
  function obCopy(d, s) { d.id = s.id; d.kind = s.kind; d.x = s.x; d.w = s.w; d.base = s.base; d.h = s.h; d.since = s.since; d.by = s.by; }
  const obXAt = (o, run) => (o.kind === K.Ptero && run > o.since ? o.x - D.PteroV * (run - o.since) : o.x);
  function obBaseAt(o, run) {
    if (o.kind !== K.Kite) return o.base;
    const ph = run % 80;
    return o.base + ((ph < 40 ? ph : 80 - ph) - 20) * 13;
  }

  class Player {
    constructor() {
      this.plays = false; this.out = false; this.down = false;
      this.lag = 0; this.y = 0; this.vy = 0; this.hold = 0; this.buffer = 0; this.coyote = 0; this.stun = 0; this.boost = 0;
      this.eggs = 0; this.snow = 0; this.place = 0; this.held = 0; this.hits = 0; this.ifr = 0; this.snowId = 0;
      this.air = false; this.duck = false; this.holdOn = false; this.feather = false;
      this.lp = -1; this.lt = -1;
      this.passed = new Int32Array(8).fill(INT_MIN);
      this.taken = new Int32Array(8).fill(INT_MIN);
      this.passedN = 0; this.takenN = 0;
    }
    copyFrom(o) {
      this.plays = o.plays; this.out = o.out; this.down = o.down;
      this.lag = o.lag; this.y = o.y; this.vy = o.vy; this.hold = o.hold; this.buffer = o.buffer; this.coyote = o.coyote;
      this.stun = o.stun; this.boost = o.boost; this.eggs = o.eggs; this.snow = o.snow; this.place = o.place;
      this.held = o.held; this.hits = o.hits; this.ifr = o.ifr; this.snowId = o.snowId;
      this.air = o.air; this.duck = o.duck; this.holdOn = o.holdOn; this.feather = o.feather;
      this.lp = o.lp; this.lt = o.lt;
      this.passed.set(o.passed); this.taken.set(o.taken);
      this.passedN = o.passedN; this.takenN = o.takenN;
    }
    hasPassed(id) { const a = this.passed; for (let i = 0; i < 8; i++) if (a[i] === id) return true; return false; }
    hasTaken(id) { const a = this.taken; for (let i = 0; i < 8; i++) if (a[i] === id) return true; return false; }
    addPassed(id) { this.passed[this.passedN & 7] = id; this.passedN++; }
    addTaken(id) { this.taken[this.takenN & 7] = id; this.takenN++; }
    modeOf(mode) {
      if (mode === STORKS) return this.down || this.out ? 4 : this.ifr > 0 ? 3 : 1;
      if (this.out) return 4;
      if (this.stun > 0) return 3;
      if (this.air) return 1;
      return this.duck ? 2 : 0;
    }
    get hidden() {
      return (this.air ? 1 : 0) | (this.duck ? 2 : 0) | (this.holdOn ? 4 : 0) | (this.hold << 3) | (this.buffer << 8) | (this.coyote << 10);
    }
    /// Стан із кадру: те, що сервер вважає правдою на початок кроку.
    fromWire(mode, w) {
      if (mode === STORKS) {
        this.y = w[0]; this.vy = w[1]; this.down = this.out = w[2] === 4; this.feather = w[3] === 1; this.ifr = w[4];
        if (w[5] >= 0 && !this.hasPassed(w[5])) this.addPassed(w[5]);
        this.lp = w[5];
        return;
      }
      this.lag = w[0]; this.y = w[1]; this.vy = w[2]; this.out = this.down = w[3] === 4; this.stun = w[4]; this.boost = w[5];
      this.eggs = w[6]; this.snow = w[7];
      const hs = w[10] | 0;
      this.air = (hs & 1) !== 0; this.duck = (hs & 2) !== 0; this.holdOn = (hs & 4) !== 0;
      this.hold = (hs >> 3) & 31; this.buffer = (hs >> 8) & 3; this.coyote = (hs >> 10) & 3;
      if (w[8] >= 0 && !this.hasPassed(w[8])) this.addPassed(w[8]);
      if (w[9] >= 0 && !this.hasTaken(w[9])) this.addTaken(w[9]);
      this.lp = w[8]; this.lt = w[9];
    }
    /// Чи збігається з кадром (усе, що кадр знає).
    same(mode, w) {
      if (!w) return true;
      if (mode === STORKS)
        return this.y === w[0] && this.vy === w[1] && this.modeOf(mode) === w[2] && (this.feather ? 1 : 0) === w[3] && this.ifr === w[4];
      return this.lag === w[0] && this.y === w[1] && this.vy === w[2] && this.modeOf(mode) === w[3] && this.stun === w[4]
        && this.boost === w[5] && this.eggs === w[6] && this.snow === w[7] && this.hidden === (w[10] | 0);
    }
  }

  class Sim {
    constructor(mode, seed, plays, readySteps, pmCap, snowOn, featherOn = true, hashing = false) {
      this.mode = mode;
      this.R = RULES[mode];
      this.seed = seed;
      this.readySteps = readySteps;
      this.pmCap = pmCap;
      this.snowOn = !!snowOn;
      this.hashing = !!hashing;
      this.hash = 2166136261;
      this.S = 0;
      this.P = [];
      this.heldAt = []; this.stamp = []; this.edge = []; this.expl = []; this.snap = []; this.snapAt = [];
      for (let i = 0; i < SEATS; i++) {
        const p = new Player();
        p.plays = !!plays[i];
        p.feather = mode === STORKS && featherOn;
        p.y = mode === STORKS ? ST.StartY : 0;
        this.P.push(p);
        this.heldAt.push(new Int32Array(LOG_N));
        this.stamp.push(new Int32Array(LOG_N).fill(-1));
        this.edge.push(new Uint8Array(LOG_N));
        this.expl.push(new Uint8Array(LOG_N));
        const s = [];
        for (let k = 0; k < SNAP_N; k++) s.push(new Player());
        this.snap.push(s);
        this.snapAt.push(new Int32Array(SNAP_N).fill(-1));
      }
      this.ob = []; for (let i = 0; i < OB_N; i++) this.ob.push(obNew());
      this.obHead = 0; this.obCount = 0;
      this.pk = []; for (let i = 0; i < PK_N; i++) this.pk.push({ id: 0, kind: 0, x: 0, base: 0 });
      this.pkHead = 0; this.pkCount = 0;
      this.sn = []; for (let i = 0; i < SN_N; i++) this.sn.push(obNew());
      this.snCount = 0; this.snNext = 0;
      this.rng = Rng(seed);
      this.cursor = this.R.firstAt;
      this.prev1 = -1; this.prev2 = -1; this.nextOb = 1; this.nextPk = 1;
      this.pitIdx = 0;
      this.gone = new Uint8Array(SEATS);
      this.ev = []; for (let i = 0; i < EV_N; i++) this.ev.push({ kind: 0, seat: 0, a: 0, b: 0 });
      this.evCount = 0;
      this.rewinding = false;
      this.prePassed = new Int32Array(8); this.preTaken = new Int32Array(8);
      this.worldRun = 0;
      this.generate(0);
    }

    // ---------- світ ----------
    speed(run) { return speedOf(this.R, run); }
    paceX(run) { return paceOf(this.R, run); }
    runAt(x) { return runAtOf(this.R, x); }
    obstacle(i) { return this.ob[(this.obHead + i) & (OB_N - 1)]; }
    pickup(i) { return this.pk[(this.pkHead + i) & (PK_N - 1)]; }
    clearEvents() { this.evCount = 0; }

    groundAt(x) {
      for (let i = 0; i < this.obCount; i++) {
        const idx = (this.obHead + i) & (OB_N - 1);
        const o = this.ob[idx];
        if (o.kind === K.Pit) {
          if (x >= o.x && x < o.x + o.w) { this.pitIdx = idx; return NO_GROUND; }
        } else if (o.kind === K.Hill && x >= o.x && x < o.x + o.w) {
          const d = x - o.x;
          if (d < 160 * 16) return idiv(640 * d, 2560);
          if (d < 260 * 16) return 640;
          return idiv(640 * (o.x + o.w - x), 2560);
        }
      }
      return 0;
    }

    generate(run) {
      const pace = this.paceX(run);
      const lim = pace - this.R.behind;
      while (this.obCount > 0 && this.ob[this.obHead].x + this.ob[this.obHead].w < lim) {
        this.obHead = (this.obHead + 1) & (OB_N - 1);
        this.obCount--;
      }
      while (this.pkCount > 0 && this.pk[this.pkHead].x + D.PickBox < lim) {
        this.pkHead = (this.pkHead + 1) & (PK_N - 1);
        this.pkCount--;
      }
      if (this.snCount > 0) {
        let w = 0;
        for (let r = 0; r < this.snCount; r++) {
          if (this.sn[r].x + this.sn[r].w >= lim) {
            if (w !== r) obCopy(this.sn[w], this.sn[r]);
            w++;
          }
        }
        this.snCount = w;
      }
      while (this.cursor < pace + this.R.lookahead) this.addEvent(run);
    }

    /// Клієнт: догенерувати курс крок за кроком до run — у тій самій послідовності, що й сервер.
    advanceWorld(run) {
      while (this.worldRun < run) {
        this.worldRun++;
        this.generate(this.worldRun);
      }
    }

    addEvent(run) {
      const R = this.R;
      const pm = Math.min(this.pmCap, idiv(run * 1000, 4500));
      const sp = this.speed(run);
      const kind = this.pick(pm);
      if (this.eventLog) this.eventLog.push(kind);
      const x = this.cursor;
      const width = this.place(kind, x, pm);
      const gmin = R.gmin0 - idiv(R.gminDrop * pm, 1000);
      const gmax = R.gmax0 - idiv(R.gmaxDrop * pm, 1000);
      let gs = gmin + this.rng.next(gmax - gmin + 1);
      if (kind === R.specialKind && gs < gmin + R.specialGap) gs = gmin + R.specialGap;
      const gap = gs * sp;
      const end = x + width;
      if (this.mode === DINO && this.rng.next(100) < D.PickChance) this.addPickup(kind, x, end, gap);
      this.cursor = end + gap;
      this.prev2 = this.prev1;
      this.prev1 = kind;
    }

    pick(pm) {
      const R = this.R;
      const band = pm < 150 ? 0 : pm < 300 ? 1 : pm < 500 ? 2 : pm < 800 ? 3 : 4;
      const w = R.weights[band];
      let i = this.roll(w);
      if (this.bad(R.events[i])) {
        i = this.roll(w);
        if (this.bad(R.events[i])) {
          for (let k = 0; k < w.length; k++) if (w[k] > 0 && !this.bad(R.events[k])) { i = k; break; }
        }
      }
      return R.events[i];
    }

    roll(w) {
      let total = 0;
      for (let k = 0; k < w.length; k++) total += w[k];
      let r = this.rng.next(total);
      for (let k = 0; k < w.length; k++) {
        r -= w[k];
        if (r < 0) return k;
      }
      return w.length - 1;
    }

    bad(kind) {
      if (kind === this.prev1 && kind === this.prev2) return true;
      if (this.mode === DINO) return this.prev1 === K.Hill && (kind === K.Hill || kind === K.Pit);
      return (this.prev1 === K.Tree && kind === K.Kite) || (this.prev1 === K.Kite && kind === K.Tree);
    }

    place(kind, x, pm) {
      const u = SUB;
      switch (kind) {
        case K.Low: this.add(K.Low, x, 34 * u, 0, 30 * u); return 34 * u;
        case K.Low2: this.add(K.Low2, x, 68 * u, 0, 30 * u); return 68 * u;
        case K.High: this.add(K.High, x, 34 * u, 0, 64 * u); return 34 * u;
        case K.Wide: this.add(K.Wide, x, 100 * u, 0, 30 * u); return 100 * u;
        case K.Icicle: this.add(K.Icicle, x, 30 * u, 30 * u, 270 * u); return 30 * u;
        case K.Ptero: {
          const px = x + 160 * u;
          this.add(K.Ptero, px, 40 * u, 30 * u, 20 * u, this.runAt(px - D.PteroWake));
          return 200 * u;
        }
        case K.Pit: {
          const w = (60 + idiv(40 * Math.min(pm, 1000), 1000)) * u;
          this.add(K.Pit, x, w, 0, 0);
          return w;
        }
        case K.Hill:
          this.add(K.Hill, x, 420 * u, 0, 40 * u);
          if (pm >= 300) this.add(K.Low, x + 193 * u, 34 * u, 40 * u, 30 * u);
          return 420 * u;
        case K.Combo:
          this.add(K.Low, x, 34 * u, 0, 30 * u);
          this.add(K.Icicle, x + 164 * u, 30 * u, 30 * u, 270 * u);
          return 194 * u;
        case K.Chimney: {
          const h = 80 + this.rng.next(91);
          this.add(K.Chimney, x, 40 * u, 0, h * u);
          return 40 * u;
        }
        case K.Tree: {
          const h = 70 + this.rng.next(61);
          this.add(K.Tree, x, 60 * u, (ST.Sky - h) * u, h * u);
          return 60 * u;
        }
        case K.Gate: {
          const h1 = 60 + this.rng.next(71);
          const gapH = 150 - idiv(50 * pm, 1000);
          const tb = h1 + gapH;
          this.add(K.Chimney, x, 44 * u, 0, h1 * u);
          this.add(K.Tree, x, 44 * u, tb * u, (ST.Sky - tb) * u);
          return 44 * u;
        }
        case K.Pole: {
          const h = 110 + this.rng.next(81);
          this.add(K.Wire, x, 120 * u, (h - 6) * u, 6 * u);
          this.add(K.Pole, x + 53 * u, 14 * u, 0, h * u);
          return 120 * u;
        }
        case K.Nest: {
          const h = 100 + this.rng.next(61);
          this.add(K.Nest, x, 50 * u, h * u, 26 * u);
          this.add(K.Pole, x + 18 * u, 14 * u, 0, h * u);
          return 50 * u;
        }
        case K.Kite: {
          const hk = 110 + this.rng.next(101);
          this.add(K.Kite, x, 36 * u, hk * u - 288, 36 * u);
          return 36 * u;
        }
        default: {
          const h1 = 80 + this.rng.next(71);
          const h2 = 80 + this.rng.next(71);
          this.add(K.Chimney, x, 40 * u, 0, h1 * u);
          this.add(K.Chimney, x + 90 * u, 40 * u, 0, h2 * u);
          return 130 * u;
        }
      }
    }

    add(kind, x, w, base, h, since = 0) {
      if (this.obCount === OB_N) {
        this.obHead = (this.obHead + 1) & (OB_N - 1);
        this.obCount--;
      }
      const o = this.ob[(this.obHead + this.obCount) & (OB_N - 1)];
      o.id = this.nextOb++; o.kind = kind; o.x = x; o.w = w; o.base = base; o.h = h; o.since = since; o.by = -1;
      this.obCount++;
      if (this.hashing) { this.H(0x0B); this.H(o.id); this.H(kind); this.H(x); this.H(base); this.H(h); }
    }

    addPickup(eventKind, x, end, gap) {
      const roll = this.rng.next(100);
      let kind = roll < 50 ? K.Egg : roll < 80 ? K.Pepper : K.SnowBall;
      if (kind === K.SnowBall && !this.snowOn) kind = K.Egg;
      let px, pb;
      if (kind === K.Egg && eventKind === K.High) {
        px = x + 5 * SUB;
        pb = D.TopEgg;
      } else {
        px = end + idiv(gap, 2) - 12 * SUB;
        pb = kind === K.Egg && this.rng.next(2) === 0 ? D.AirEgg : 0;
      }
      if (this.pkCount === PK_N) {
        this.pkHead = (this.pkHead + 1) & (PK_N - 1);
        this.pkCount--;
      }
      const k = this.pk[(this.pkHead + this.pkCount) & (PK_N - 1)];
      k.id = this.nextPk++; k.kind = kind; k.x = px; k.base = pb;
      this.pkCount++;
      if (this.hashing) { this.H(0x0C); this.H(k.id); this.H(kind); this.H(px); this.H(pb); }
    }

    placeSnow(x, by, run) {
      for (let tries = 0; tries < D.SnowTries && this.crowded(x, run); tries++) x += D.SnowShift;
      if (this.snCount === SN_N) {
        for (let i = 0; i < SN_N - 1; i++) obCopy(this.sn[i], this.sn[i + 1]);
        this.snCount--;
      }
      const id = SNOW_ID_BASE + this.snNext++;
      const o = this.sn[this.snCount++];
      o.id = id; o.kind = K.Snow; o.x = x; o.w = D.SnowW; o.base = 0; o.h = D.SnowH; o.since = run; o.by = by;
      if (this.hashing) { this.H(0x0D); this.H(id); this.H(x); this.H(by); this.H(run); }
      this.emit(6, by, x, id);
      return id;
    }

    /// Клієнт: брила з кадру (sn) — [x, хто, id, з якого кроку б'є]; уже відомі не дублюємо.
    knowSnow(w) {
      for (let i = 0; i < this.snCount; i++) if (this.sn[i].id === w[2]) return;
      if (this.snCount === SN_N) {
        for (let i = 0; i < SN_N - 1; i++) obCopy(this.sn[i], this.sn[i + 1]);
        this.snCount--;
      }
      const o = this.sn[this.snCount++];
      o.id = w[2]; o.kind = K.Snow; o.x = w[0]; o.w = D.SnowW; o.base = 0; o.h = D.SnowH; o.since = w[3] | 0; o.by = w[1];
    }

    crowded(x, run) {
      const a = x - D.SnowRoom, b = x + D.SnowW + D.SnowRoom;
      for (let i = 0; i < this.obCount; i++) {
        const o = this.obstacle(i);
        const ox = obXAt(o, run);
        if (a < ox + o.w && b > ox) return true;
      }
      for (let i = 0; i < this.snCount; i++) if (a < this.sn[i].x + this.sn[i].w && b > this.sn[i].x) return true;
      return false;
    }

    // ---------- крок ----------
    step() {
      const t = this.S, run = t - this.readySteps;
      if (run >= 0) this.generate(run);
      if (run > this.worldRun) this.worldRun = run;
      for (let i = 0; i < SEATS; i++) {
        const p = this.P[i];
        if (!p.plays) continue;
        this.entry(i, t);
        this.snap[i][t & (SNAP_N - 1)].copyFrom(p);
        this.snapAt[i][t & (SNAP_N - 1)] = t;
        this.stepPlayer(i, p, t, run);
        p.held = this.heldAt[i][t & (LOG_N - 1)];
      }
      this.S = t + 1;
      if (run >= 0) this.eliminate(run);
      if (this.hashing) this.hashStep(run);
    }

    stepPlayer(seat, p, t, run) {
      if (run < 0) return;
      const j = t & (LOG_N - 1);
      if (this.mode === DINO) this.stepDino(seat, p, run, this.heldAt[seat][j], this.edge[seat][j] !== 0);
      else this.stepStork(seat, p, run, this.edge[seat][j] !== 0);
    }

    stepDino(seat, p, run, held, edge) {
      const sp = this.speed(run);
      if (p.out) { p.lag += sp; return; }
      const mult = p.stun > 0 ? D.StunMult : p.boost > 0 ? D.BoostMult : p.lag > 0 ? D.RecoverMult : 100;
      const fwd = idiv(sp * mult, 100);
      p.lag += sp - fwd;
      if (p.lag < D.LagMin) p.lag = D.LagMin;
      const worldX = this.paceX(run + 1) - p.lag;
      if (p.stun > 0) p.stun--;
      if (p.boost > 0) p.boost--;
      const gx = worldX + D.FootX;
      if (p.stun === 0) {
        if (!p.air) {
          if (edge || p.buffer > 0) this.jump(p, held);
          else p.duck = (held & 2) !== 0;
        } else {
          if (p.buffer > 0) p.buffer--;
          if (edge) {
            if (p.coyote > 0) this.jump(p, held);
            else p.buffer = D.BufferSteps;
          }
          if (p.coyote > 0) p.coyote--;
        }
        if (p.air) {
          const heldUp = p.holdOn && (held & 1) !== 0 && p.hold < D.HoldMax && p.vy > 0;
          if (heldUp) p.hold++;
          let g = heldUp ? D.GHold : D.G;
          if ((held & 2) !== 0) {
            if (p.vy > D.FastFall) p.vy = D.FastFall;
            g = D.G * 2;
            p.holdOn = false;
            p.duck = true;
          } else p.duck = false;
          this.fly(seat, p, run, gx, held, g);
        } else this.walk(p, gx);
      } else {
        p.duck = false;
        p.holdOn = false;
        if (p.air) this.fly(seat, p, run, gx, 0, D.G);
        else this.walk(p, gx);
      }
      if (p.stun === 0) this.collideDino(seat, p, run, worldX);
    }

    jump(p, held) {
      p.air = true; p.vy = D.JumpV; p.hold = 0; p.holdOn = (held & 1) !== 0; p.buffer = 0; p.coyote = 0; p.duck = false;
    }

    fly(seat, p, run, gx, held, g) {
      p.vy -= g;
      if (p.vy < D.VyMin) p.vy = D.VyMin;
      p.y += p.vy;
      const ground = this.groundAt(gx);
      if (ground !== NO_GROUND && p.y <= ground && p.vy <= 0) {
        p.y = ground; p.air = false; p.vy = 0; p.duck = (held & 2) !== 0; p.coyote = 0;
      } else if (ground === NO_GROUND && p.y < -D.PitDepth) this.fall(seat, p, run);
    }

    walk(p, gx) {
      const ground = this.groundAt(gx);
      if (ground === NO_GROUND) { p.air = true; p.vy = 0; p.coyote = D.CoyoteSteps; }
      else p.y = ground;
    }

    fall(seat, p, run) {
      const pit = this.ob[this.pitIdx];
      p.stun = D.PitStun; p.y = 0; p.air = false; p.vy = 0; p.duck = false; p.holdOn = false; p.buffer = 0; p.coyote = 0;
      p.lag = this.paceX(run + 1) - (pit.x + pit.w + D.PitOut);
      if (p.lag < D.LagMin) p.lag = D.LagMin;
      p.hits++;
      const fresh = !(this.rewinding && was(this.prePassed, pit.id));
      p.addPassed(pit.id);
      p.lp = pit.id;
      if (fresh) this.emit(2, seat, run, pit.id);
    }

    collideDino(seat, p, run, worldX) {
      const x0 = worldX, x1 = worldX + (p.duck ? D.DuckW : D.HitW);
      const y0 = p.y, y1 = p.y + (p.duck ? D.DuckH : D.HitH);
      let hit = false;
      for (let i = 0; i < this.obCount && !hit; i++) {
        const o = this.obstacle(i);
        if (!solid(o.kind)) continue;
        const ox = obXAt(o, run);
        if (ox >= x1 || ox + o.w <= x0) continue;
        if (o.base >= y1 || o.base + o.h <= y0) continue;
        if (p.hasPassed(o.id)) continue;
        this.hitDino(seat, p, run, o.id);
        hit = true;
      }
      for (let i = 0; i < this.snCount && !hit; i++) {
        const o = this.sn[i];
        if (o.since > run) continue;
        if (o.x >= x1 || o.x + o.w <= x0) continue;
        if (o.base >= y1 || o.base + o.h <= y0) continue;
        if (p.hasPassed(o.id)) continue;
        this.hitDino(seat, p, run, o.id);
        hit = true;
      }
      for (let i = 0; i < this.pkCount; i++) {
        const k = this.pickup(i);
        if (k.x >= x1 || k.x + D.PickBox <= x0) continue;
        if (k.base >= y1 || k.base + D.PickBox <= y0) continue;
        if (p.hasTaken(k.id)) continue;
        this.take(seat, p, k.id, k.kind);
      }
    }

    hitDino(seat, p, run, id) {
      p.stun = D.StunSteps; p.duck = false; p.holdOn = false; p.buffer = 0; p.hits++;
      const fresh = !(this.rewinding && was(this.prePassed, id));
      p.addPassed(id);
      p.lp = id;
      if (fresh) this.emit(1, seat, run, id);
    }

    take(seat, p, id, kind) {
      const fresh = !(this.rewinding && was(this.preTaken, id));
      p.addTaken(id);
      p.lt = id;
      if (kind === K.Egg) p.eggs++;
      else if (kind === K.Pepper) p.boost = D.BoostSteps;
      else { p.snow = 1; p.snowId = id; }
      if (fresh) this.emit(kind === K.Egg ? 3 : kind === K.Pepper ? 4 : 5, seat, id, 0);
    }

    stepStork(seat, p, run, edge) {
      if (p.down) return;
      if (p.ifr > 0) p.ifr--;
      if (edge) p.vy = ST.Flap;
      p.vy -= ST.G;
      if (p.vy < ST.VyMin) p.vy = ST.VyMin;
      p.y += p.vy;
      if (p.y > ST.Ceiling) { p.y = ST.Ceiling; p.vy = 0; }
      if (p.ifr > 0) return;
      if (p.y <= 0) { this.hitStork(seat, p, run, ST.GroundId); return; }
      const x0 = this.paceX(run + 1), x1 = x0 + ST.HitW, y0 = p.y, y1 = p.y + ST.HitH;
      for (let i = 0; i < this.obCount; i++) {
        const o = this.obstacle(i);
        if (o.since > run) continue;
        if (o.x >= x1 || o.x + o.w <= x0) continue;
        const ob = obBaseAt(o, run);
        if (ob >= y1 || ob + o.h <= y0) continue;
        if (p.hasPassed(o.id)) continue;
        this.hitStork(seat, p, run, o.id);
        return;
      }
    }

    hitStork(seat, p, run, id) {
      p.hits++;
      const fresh = !(this.rewinding && id !== ST.GroundId && was(this.prePassed, id));
      if (id !== ST.GroundId) { p.addPassed(id); p.lp = id; }
      if (p.feather) {
        p.feather = false; p.ifr = ST.IFrames; p.vy = ST.Flap;
        if (p.y < ST.MinYAfterHit) p.y = ST.MinYAfterHit;
        if (fresh) this.emit(1, seat, run, id);
      } else p.down = true;
    }

    eliminate(run) {
      let newly = 0;
      for (let i = 0; i < SEATS; i++) {
        const p = this.P[i];
        this.gone[i] = 0;
        if (!p.plays || p.out) continue;
        if (this.mode === DINO ? p.lag >= avD(run) : p.down) { this.gone[i] = 1; newly++; }
      }
      if (newly === 0) return;
      let alive = 0;
      for (let i = 0; i < SEATS; i++) if (this.P[i].plays && !this.P[i].out && !this.gone[i]) alive++;
      for (let i = 0; i < SEATS; i++) {
        if (!this.gone[i]) continue;
        const p = this.P[i];
        p.out = true; p.down = true; p.place = alive + 1;
        this.emit(7, i, run, 0);
      }
    }

    // ---------- ввід і перемотування ----------
    entry(seat, t) {
      const j = t & (LOG_N - 1);
      if (this.stamp[seat][j] === t) return;
      const prev = (t - 1) & (LOG_N - 1);
      this.heldAt[seat][j] = this.stamp[seat][prev] === t - 1 ? this.heldAt[seat][prev] : this.P[seat].held;
      this.edge[seat][j] = 0;
      this.expl[seat][j] = 0;
      this.stamp[seat][j] = t;
    }

    input(seat, s, k) {
      if (seat < 0 || seat >= SEATS || k < 0 || k > 7) return false;
      const p = this.P[seat];
      if (!p.plays || p.out || p.down) return false;
      if (s > this.S + FUTURE_MAX) s = this.S + FUTURE_MAX;
      if (s < this.S - REWIND_MAX || s < 0) return false;
      this.record(seat, s, k);
      if (s < this.S) this.rewind(seat, s);
      return true;
    }

    record(seat, s, k) {
      const j = s & (LOG_N - 1);
      let edge = (k & 4) !== 0;
      if (this.stamp[seat][j] === s && this.expl[seat][j]) edge = edge || this.edge[seat][j] !== 0;
      this.heldAt[seat][j] = k & 3;
      this.edge[seat][j] = edge ? 1 : 0;
      this.expl[seat][j] = 1;
      this.stamp[seat][j] = s;
      if (s >= this.S) return;
      let t = s + 1;
      for (; t < this.S; t++) {
        const jj = t & (LOG_N - 1);
        if (this.stamp[seat][jj] === t && this.expl[seat][jj]) break;
        this.heldAt[seat][jj] = k & 3;
        this.stamp[seat][jj] = t;
      }
      if (t === this.S) this.P[seat].held = k & 3;
    }

    rewind(seat, s) {
      if (this.snapAt[seat][s & (SNAP_N - 1)] !== s) return;
      const p = this.P[seat];
      this.prePassed.set(p.passed);
      this.preTaken.set(p.taken);
      this.rewinding = true;
      p.copyFrom(this.snap[seat][s & (SNAP_N - 1)]);
      for (let t = s; t < this.S; t++) {
        this.snap[seat][t & (SNAP_N - 1)].copyFrom(p);
        this.snapAt[seat][t & (SNAP_N - 1)] = t;
        this.stepPlayer(seat, p, t, t - this.readySteps);
      }
      this.rewinding = false;
    }

    emit(kind, seat, a, b) {
      if (this.evCount === EV_N) return;
      const e = this.ev[this.evCount++];
      e.kind = kind; e.seat = seat; e.a = a; e.b = b;
    }

    // ---------- хеш ----------
    hashStep(run) {
      this.H(run);
      this.H(this.paceX(run < 0 ? 0 : run + 1));
      for (let i = 0; i < SEATS; i++) {
        const p = this.P[i];
        if (!p.plays) continue;
        this.H(i);
        if (this.mode === DINO) {
          this.H(p.lag); this.H(p.y); this.H(p.vy); this.H(p.modeOf(this.mode)); this.H(p.stun); this.H(p.boost);
          this.H(p.eggs); this.H(p.snow); this.H(p.hidden);
        } else {
          this.H(p.y); this.H(p.vy); this.H(p.modeOf(this.mode)); this.H(p.feather ? 1 : 0); this.H(p.ifr); this.H(p.lp);
        }
      }
    }

    H(v) {
      v |= 0;
      let h = this.hash;
      h = Math.imul(h ^ (v & 255), 16777619) >>> 0;
      h = Math.imul(h ^ ((v >>> 8) & 255), 16777619) >>> 0;
      h = Math.imul(h ^ ((v >>> 16) & 255), 16777619) >>> 0;
      h = Math.imul(h ^ (v >>> 24), 16777619) >>> 0;
      this.hash = h;
    }
  }

  function was(ring, id) { for (let i = 0; i < 8; i++) if (ring[i] === id) return true; return false; }

  /// Прогін для парності з C# (RunnerSim.Fixture): той самий випадковий ввід, ті самі кидки.
  function fixture(mode, seed, players, inputSeed, steps, readySteps, pmCap, snowOn, throws) {
    const plays = [];
    for (let i = 0; i < SEATS; i++) plays.push(i < players);
    const sim = new Sim(mode, seed, plays, readySteps, pmCap, snowOn, true, true);
    const rng = Rng(inputSeed);
    const held = new Array(SEATS).fill(0);
    for (let step = 0; step < steps; step++) {
      for (let seat = 0; seat < players; seat++) {
        const u = rng.next(100);
        let edge = false;
        if (u < 6) { held[seat] ^= 1; edge = (held[seat] & 1) !== 0; }
        else if (u < 9) held[seat] ^= 2;
        sim.input(seat, sim.S, held[seat] | (edge ? 4 : 0));
      }
      if (throws) for (const [at, by] of throws) {
        if (at !== step) continue;
        const run = Math.max(0, sim.S - sim.readySteps);
        sim.placeSnow(sim.paceX(run) + 400 * SUB, by, run);
      }
      sim.step();
      sim.clearEvents();
    }
    return sim;
  }

  /// Ті самі п'ять фікстур, що в RunnerSimTests.Fixtures (dino.md §8.3).
  const FIXTURES = [
    ['dino 1×3000', () => fixture(DINO, 1693571063, 1, 11, 3000, 150, 1000, false)],
    ['dino 8×6000', () => fixture(DINO, 424242, 8, 12, 6000, 150, 1000, true)],
    ['dino 8×3000 сніжки', () => fixture(DINO, 777, 8, 13, 3000, 150, 1000, true, [[600, 0], [1500, 3], [2400, 5]])],
    ['storks 4×4000', () => fixture(STORKS, 402881377, 4, 14, 4000, 150, 1000, false)],
    ['dino-daily 1×5000', () => fixture(DINO, 390981302, 1, 15, 5000, 0, 1250, false)],   // Days.Seed("dino-daily","2026-09-27")
  ];

  window.RunnerSim = {
    Sim, Player, Rng, K, D, ST, RULES, DINO, STORKS, SUB, STEP_MS, SEATS, NO_GROUND, SNOW_ID_BASE,
    paceOf, speedOf, runAtOf, avD, obXAt, obBaseAt, fixture, FIXTURES,
    check() {
      return FIXTURES.map(([name, make]) => {
        const t0 = performance.now();
        const sim = make();
        let eggs = 0, hits = 0;
        for (const p of sim.P) { eggs += p.eggs; hits += p.hits; }
        return { name, hash: sim.hash, hits, eggs, ms: Math.round(performance.now() - t0) };
      });
    },
  };

  if (!window.HGames || !HGames.ui || !HGames.ui.canvas) return;   // стенд: лише симуляція

  // RUNNER_CLIENT
})();
