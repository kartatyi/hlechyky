/*
  Пакет «runner»: Стрибозаври (dino), Забіг дня (dino-daily), Лелеки (storks) — один файл на три гри
  (Client: "runner" у всіх трьох GameInfo). Специфікація — docs/games/specs/dino.md (§5 — мережа, §6 — клієнт),
  dino-daily.md, storks.md.

  Будова:
    1. RunnerSim — ДОСЛІВНЕ дзеркало src/Hlechyky/Games/Impl/RunnerSim.cs: цілі числа в суб-пікселях (16 на px),
       крок 20 мс, курс від зерна (xorshift32), стрибок/політ, перемотування. Без DOM; у window.RunnerSim — лише на
       стенді docs/games/dev/runner-check.html (хеш парності з C#) і з ?rnrdebug=1.
    2. Мережа й годинник: свій герой рахується тут же з власного вводу (передбачення), сервер — суддя; кадр
       звіряє стан на кроці f.s і, якщо розійшлись, перераховує хвіст. Чужі — інтерполяція кадрів.
    3. Малювання на canvas: статичне (небо, гори, село) — в offscreen-шарах, спрайти — в кеші.
    4. HUD, керування (клавіатура, пад, палець, мишка), звук, три HGames.register.

  Дріт: вид { mode, ph, s, seed, readySteps, pmCap, snowOpt|featherOpt, plays, alive, place, points, p, … },
  кадр { s, ph, d?, m?, p, sn?, pg?, ev? }; sn — кинуті брили [x, хто, id, з якого кроку, у кого цілили];
  p[seat] Стрибозавра = [lag, y, vy, mode, stun, boost, eggs, snow, lp, lt, hs],
  Лелеки = [y, vy, mode, feather, ifr, lp]. Ввід — Input('in', { s, k }): k = 1 стрибок тримають, 2 ↓, 4 натиск.
*/
(() => {
  'use strict';

  // =============================================================================================
  // 1. Симуляція — дзеркало RunnerSim.cs. Будь-яка правка — в обох файлах одночасно.
  // =============================================================================================

  const SUB = 16, STEP_MS = 20, SEATS = 8, FUTURE_MAX = 8, REWIND_MAX = 15;
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
    StunSteps: 30, PitStun: 50, PitDepth: 384, PitOut: 16 * 16, GraceSteps: 12,
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
      this.eggs = 0; this.snow = 0; this.place = 0; this.held = 0; this.hits = 0; this.ifr = 0; this.snowId = 0; this.grace = 0;
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
      this.held = o.held; this.hits = o.hits; this.ifr = o.ifr; this.snowId = o.snowId; this.grace = o.grace;
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
      return (this.air ? 1 : 0) | (this.duck ? 2 : 0) | (this.holdOn ? 4 : 0) | (this.hold << 3) | (this.buffer << 8) | (this.coyote << 10) | (this.grace << 12);
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
      this.hold = (hs >> 3) & 31; this.buffer = (hs >> 8) & 3; this.coyote = (hs >> 10) & 3; this.grace = (hs >> 12) & 15;
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
      if (p.stun > 0) { p.stun--; if (p.stun === 0) p.grace = D.GraceSteps; }   // оговтався: ще мить брили не збивають
      else if (p.grace > 0) p.grace--;
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
      p.stun = D.PitStun; p.grace = 0; p.y = 0; p.air = false; p.vy = 0; p.duck = false; p.holdOn = false; p.buffer = 0; p.coyote = 0;
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
      const ghost = p.grace > 0;
      for (let i = 0; i < this.obCount && !hit; i++) {
        const o = this.obstacle(i);
        if (!solid(o.kind)) continue;
        const ox = obXAt(o, run);
        if (ox >= x1 || ox + o.w <= x0) continue;
        if (o.base >= y1 || o.base + o.h <= y0) continue;
        if (p.hasPassed(o.id)) continue;
        if (ghost) { p.addPassed(o.id); p.lp = o.id; continue; }   // оговтався — проламується крізь брилу
        this.hitDino(seat, p, run, o.id);
        hit = true;
      }
      for (let i = 0; i < this.snCount && !hit; i++) {
        const o = this.sn[i];
        if (o.since > run) continue;
        if (o.x >= x1 || o.x + o.w <= x0) continue;
        if (o.base >= y1 || o.base + o.h <= y0) continue;
        if (p.hasPassed(o.id)) continue;
        if (ghost) { p.addPassed(o.id); p.lp = o.id; continue; }
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
      p.stun = D.StunSteps; p.grace = 0; p.duck = false; p.holdOn = false; p.buffer = 0; p.hits++;
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

  // Симуляція назовні — лише стенду парності (без каркаса) і з ?rnrdebug=1 (боти-стенди): на проді готовий генератор
  // курсу в консолі — ще один подарунок автострибу.
  const stand = !window.HGames || !HGames.ui || !HGames.ui.canvas;
  if (stand || /[?&]rnrdebug=1/.test(location.search)) window.RunnerSim = {
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

  if (stand) return;   // стенд: лише симуляція

  // =============================================================================================
  // 2. Клієнт: годинник кроків, передбачення свого героя, звірка з сервером, чужі — з кадрів.
  // =============================================================================================

  const ui = HGames.ui;
  const VIEW_H = 300, GROUND = 240, HIST_N = 64, FR_N = 24, STORK_OY = 52;
  const THIN = String.fromCharCode(0x2009);
  const SEAT_VARS = [['--ok', '#7bd389'], ['--accent', '#f4c542'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'],
    ['--rnr-b', '#6fb3e8'], ['--rnr-p', '#e88ac0'], ['--rnr-v', '#b08cf0'], ['--rnr-r', '#e05a5a']];
  const SEAT_CLASS = ['o', 'x', 'c', 'd', 'rnr-b', 'rnr-p', 'rnr-v', 'rnr-r'];
  const DINO_NAMES = ['зелений', 'жовтий', 'рудий', 'сірий', 'синій', 'рожевий', 'фіалковий', 'червоний'];
  const STORK_NAMES = ['зелена', 'жовта', 'руда', 'сіра', 'синя', 'рожева', 'фіалкова', 'червона'];
  const DEBUG = /[?&]rnrdebug=1/.test(location.search);

  const fmtNum = (n) => String(Math.max(0, Math.floor(n))).replace(/\B(?=(\d{3})+(?!\d))/g, THIN);
  const fmtClock = (steps) => {
    const s = Math.max(0, Math.floor((steps * STEP_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const kyivToday = () => { try { return new Date().toLocaleDateString('sv-SE', { timeZone: 'Europe/Kyiv' }); } catch (_) { return ''; } };
  const reducedMotion = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);

  /// Клавіша → дія. Спершу e.code (розкладка не важить), літера — лише коли code нема (синтетичні події).
  function keyKind(e) {
    const c = e.code || '';
    if (c) {
      if (c === 'Space' || c === 'ArrowUp' || c === 'KeyW') return 'jump';
      if (c === 'ArrowDown' || c === 'KeyS') return 'down';
      if (c === 'KeyX' || c === 'KeyE') return 'throw';
      if (c === 'Enter' || c === 'NumpadEnter') return 'enter';
      return '';
    }
    const k = String(e.key || '').toLowerCase();
    if (k === ' ' || k === 'spacebar' || k === 'arrowup' || k === 'w' || k === 'ц') return 'jump';
    if (k === 'arrowdown' || k === 's' || k === 'і') return 'down';
    if (k === 'x' || k === 'ч' || k === 'e' || k === 'у') return 'throw';
    if (k === 'enter') return 'enter';
    return '';
  }

  // ---------- колір ----------
  function rgbOf(hex) {
    let h = String(hex || '').trim();
    if (h[0] !== '#') return [180, 200, 220];
    if (h.length === 4) h = '#' + h[1] + h[1] + h[2] + h[2] + h[3] + h[3];
    const n = parseInt(h.slice(1, 7), 16);
    return Number.isFinite(n) ? [(n >> 16) & 255, (n >> 8) & 255, n & 255] : [180, 200, 220];
  }
  function mix(hex, toHex, f) {
    const a = rgbOf(hex), b = rgbOf(toHex);
    return 'rgb(' + Math.round(a[0] + (b[0] - a[0]) * f) + ',' + Math.round(a[1] + (b[1] - a[1]) * f) + ',' + Math.round(a[2] + (b[2] - a[2]) * f) + ')';
  }
  function palette() {
    const c = (n, f) => ui.css(n, f);
    return {
      seats: SEAT_VARS.map(([n, f]) => c(n, f)),
      text: c('--text', '#ecf1ea'), muted: c('--muted', '#9db3a5'), shade: c('--gshade', 'rgba(15, 31, 24, .62)'),
      bg2: c('--bg2', '#16291f'), accent: c('--accent', '#f4c542'), danger: c('--danger', '#e57373'), clay: c('--clay', '#c5763a'),
      ice: c('--rnr-ice', '#b8e6ff'), ice2: c('--rnr-ice2', '#7fbfe6'), snow: c('--rnr-snow', '#f2f8ff'),
      sky1: c('--rnr-sky1', '#0f1f2e'), sky2: c('--rnr-sky2', '#2b4a66'), far: c('--rnr-far', '#1c3346'), mid: c('--rnr-mid', '#24425a'),
      ava: c('--rnr-ava', '#dfeeff'),
      dusk1: c('--rnr-dusk1', '#2a1d3a'), dusk2: c('--rnr-dusk2', '#d97d4a'), field: c('--rnr-field', '#6f8f3a'), field2: c('--rnr-field2', '#48661f'),
      roof: c('--rnr-roof', '#7a4a2a'), wall: c('--rnr-wall', '#efe3c8'), wire: c('--rnr-wire', '#1a1a1a'),
      stork: c('--rnr-stork', '#f7f7f2'), beak: c('--rnr-beak', '#d9482f'),
    };
  }

  /// Детермінований шум для декорацій (гори, зірки, хати) — щоб сцена не мінялась від F5.
  function Lcg(seed) {
    let x = seed >>> 0 || 1;
    return () => { x = (Math.imul(x, 1664525) + 1013904223) >>> 0; return x / 4294967296; };
  }

  /// Offscreen-полотно в логічних px із густиною s (DPR × K): drawImage(c, x, y, w, h) лягає піксель у піксель.
  function off(w, h, s) {
    const c = document.createElement('canvas');
    c.width = Math.max(1, Math.ceil(w * s));
    c.height = Math.max(1, Math.ceil(h * s));
    const g = c.getContext('2d');
    g.scale(s, s);
    return { c, g, w, h };
  }

  // =============================================================================================
  // 3. Звук: WebAudio-синтез, тихо, після першого жесту, вимикач у HUD
  // =============================================================================================

  const Snd = {
    ctx: null, master: null, noiseBuf: null, rumble: null, rumbleGain: null,
    on: (() => { try { return localStorage.getItem('runner.sound') !== '0'; } catch (_) { return true; } })(),
    wake() {
      if (!this.on) return;
      if (this.ctx) { if (this.ctx.state === 'suspended') this.ctx.resume().catch(() => {}); return; }
      try {
        const AC = window.AudioContext || window.webkitAudioContext;
        if (!AC) return;
        this.ctx = new AC();
        this.master = this.ctx.createGain();
        this.master.gain.value = 0.09;          // ≈ −21 дБ: фон, а не сирена
        this.master.connect(this.ctx.destination);
        const len = this.ctx.sampleRate;
        this.noiseBuf = this.ctx.createBuffer(1, len, this.ctx.sampleRate);
        const d = this.noiseBuf.getChannelData(0);
        let last = 0;
        for (let i = 0; i < len; i++) { const w = Math.random() * 2 - 1; last = (last + 0.02 * w) / 1.02; d[i] = i & 1 ? w : last * 3.5; }
      } catch (_) { this.ctx = null; }
    },
    set(on) {
      this.on = on;
      try { localStorage.setItem('runner.sound', on ? '1' : '0'); } catch (_) { /* приватне вікно */ }
      if (!on) this.rumbleTo(0);
      if (on) this.wake();
    },
    ok() { return this.on && this.ctx && this.ctx.state === 'running'; },
    tone(f0, f1, ms, type, vol) {
      if (!this.ok()) return;
      const t = this.ctx.currentTime, o = this.ctx.createOscillator(), g = this.ctx.createGain();
      o.type = type || 'sine';
      o.frequency.setValueAtTime(f0, t);
      if (f1 && f1 !== f0) o.frequency.exponentialRampToValueAtTime(f1, t + ms / 1000);
      g.gain.setValueAtTime(vol || 0.6, t);
      g.gain.exponentialRampToValueAtTime(0.001, t + ms / 1000);
      o.connect(g); g.connect(this.master);
      o.start(t); o.stop(t + ms / 1000 + 0.02);
    },
    noise(ms, vol, freq, q) {
      if (!this.ok()) return;
      const t = this.ctx.currentTime, s = this.ctx.createBufferSource(), f = this.ctx.createBiquadFilter(), g = this.ctx.createGain();
      s.buffer = this.noiseBuf;
      f.type = 'bandpass'; f.frequency.value = freq || 1200; f.Q.value = q || 0.8;
      g.gain.setValueAtTime(vol || 0.5, t);
      g.gain.exponentialRampToValueAtTime(0.001, t + ms / 1000);
      s.connect(f); f.connect(g); g.connect(this.master);
      s.start(t, Math.random() * 0.5); s.stop(t + ms / 1000 + 0.02);
    },
    /// Гул лавини: коричневий шум, гучність від відстані (0 — тиша).
    rumbleTo(level) {
      if (!this.ctx) return;
      if (!this.rumble && level > 0 && this.ok()) {
        const s = this.ctx.createBufferSource(), f = this.ctx.createBiquadFilter(), g = this.ctx.createGain();
        s.buffer = this.noiseBuf; s.loop = true;
        f.type = 'lowpass'; f.frequency.value = 260;
        g.gain.value = 0;
        s.connect(f); f.connect(g); g.connect(this.master);
        s.start();
        this.rumble = s; this.rumbleGain = g;
      }
      if (this.rumbleGain) this.rumbleGain.gain.setTargetAtTime(this.on ? clamp(level, 0, 1) * 0.9 : 0, this.ctx.currentTime, 0.12);
    },
    jump() { this.tone(220, 440, 60, 'square', 0.25); },
    land() { this.noise(30, 0.35, 900); },
    hit() { this.tone(120, 90, 150, 'sawtooth', 0.35); this.noise(90, 0.3, 500); },
    egg() { this.tone(880, 1320, 120, 'sine', 0.5); },
    pepper() { this.tone(500, 900, 140, 'triangle', 0.4); },
    grab() { this.tone(660, 990, 90, 'triangle', 0.35); },
    toss() { this.noise(200, 0.4, 2400, 1.4); },
    out() { this.tone(400, 100, 300, 'sine', 0.5); },
    flap() { this.noise(40, 0.4, 700, 0.6); },
    thud() { this.tone(90, 70, 120, 'sawtooth', 0.4); this.noise(120, 0.25, 3000, 0.5); },
    fall() { this.tone(400, 150, 350, 'sine', 0.45); },
    go() { this.tone(660, 660, 110, 'sine', 0.4); setTimeout(() => this.tone(990, 990, 160, 'sine', 0.4), 130); },
  };

  // =============================================================================================
  // 4. Спрайти: процедурні фігури в кеші (8 місць × пози), перешкоди, підбирачки, бирки
  // =============================================================================================

  const DINO_BOX = { w: 64, h: 60, foot: 56, left: 12 };   // лівий край хітбокса — x = 12 у спрайті
  const STORK_BOX = { w: 64, h: 44, bottom: 34, left: 14 };

  /// Візерунок місця поверх тулуба: 0 без, 1 смужки, 2 плями, 3 цятки, 4 зигзаг, 5 ромби, 6 хвилі, 7 клітинка.
  function pattern(g, kind, x0, y0, w, h, col) {
    g.fillStyle = col; g.strokeStyle = col; g.lineWidth = 1.6;
    switch (kind) {
      case 1: for (let x = x0 + 3; x < x0 + w; x += 6) g.fillRect(x, y0, 2.4, h); break;
      case 2: g.beginPath(); g.ellipse(x0 + w * 0.3, y0 + h * 0.35, 4, 3, 0.4, 0, 7); g.ellipse(x0 + w * 0.68, y0 + h * 0.55, 5, 3.2, -0.3, 0, 7); g.ellipse(x0 + w * 0.45, y0 + h * 0.8, 3, 2, 0, 0, 7); g.fill(); break;
      case 3: g.beginPath(); for (let y = y0 + 3; y < y0 + h; y += 5) for (let x = x0 + 3 + ((y / 5) % 2) * 2.5; x < x0 + w; x += 5) { g.moveTo(x + 1.2, y); g.arc(x, y, 1.2, 0, 7); } g.fill(); break;
      case 4: g.beginPath(); for (let y = y0 + 4; y < y0 + h; y += 7) { g.moveTo(x0, y); for (let x = x0; x < x0 + w; x += 4) g.lineTo(x + 2, y + ((x / 4) % 2 ? -2.5 : 2.5)); } g.stroke(); break;
      case 5: g.beginPath(); for (let y = y0 + 4; y < y0 + h; y += 8) for (let x = x0 + 4 + ((y / 8) % 2) * 4; x < x0 + w; x += 8) { g.moveTo(x, y - 3); g.lineTo(x + 3, y); g.lineTo(x, y + 3); g.lineTo(x - 3, y); g.closePath(); } g.fill(); break;
      case 6: g.beginPath(); for (let y = y0 + 4; y < y0 + h; y += 6) { g.moveTo(x0, y); for (let x = x0; x <= x0 + w; x += 2) g.lineTo(x, y + Math.sin(x * 0.7) * 1.8); } g.stroke(); break;
      case 7: for (let y = y0; y < y0 + h; y += 5) for (let x = x0 + ((y / 5) % 2) * 5; x < x0 + w; x += 10) g.fillRect(x, y, 5, 5); break;
      default: break;
    }
  }

  function numberOn(g, n, x, y, px) {
    g.font = '800 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineWidth = 2.4;
    g.strokeStyle = 'rgba(10, 20, 30, .85)';
    g.strokeText(String(n), x, y);
    g.fillStyle = '#ffffff';
    g.fillText(String(n), x, y);
  }

  /// Динозавр у коробці 64×60, ноги на y = 56, морда праворуч. pose: 0/1 біг, 2 у повітрі, 3 пригнувся, 4 спотик, 5 сидить.
  function paintDino(g, pose, col, seat) {
    const dark = mix(col, '#0b1620', 0.45), belly = mix(col, '#ffffff', 0.45), deep = mix(col, '#0b1620', 0.7);
    g.save();
    g.translate(4, 2);
    if (pose === 4) { g.translate(24, 54); g.rotate(0.44); g.translate(-24, -54); }
    g.lineCap = 'round';
    g.lineJoin = 'round';
    const leg = (x0, y0, x1, y1, x2, y2) => {
      g.strokeStyle = dark; g.lineWidth = 5;
      g.beginPath(); g.moveTo(x0, y0); g.lineTo(x1, y1); if (x2 !== undefined) g.lineTo(x2, y2); g.stroke();
    };
    if (pose === 3) {
      // пригнувся: тулуб низько й видовжено, голова вперед
      leg(18, 49, 15, 54); leg(31, 49, 34, 54);
      g.fillStyle = col;
      g.beginPath(); g.moveTo(10, 42); g.lineTo(-2, 45); g.lineTo(10, 48); g.fill();
      g.beginPath(); g.ellipse(24, 45, 17, 7.5, 0, 0, 7); g.fill();
      g.save(); g.beginPath(); g.ellipse(24, 45, 17, 7.5, 0, 0, 7); g.clip(); pattern(g, seat, 7, 37, 34, 16, dark); g.restore();
      g.fillStyle = belly; g.beginPath(); g.ellipse(26, 49, 10, 3, 0, 0, 7); g.fill();
      g.fillStyle = col; g.beginPath(); g.roundRect(36, 36, 21, 12, 5); g.fill();
      g.fillStyle = '#fff'; g.beginPath(); g.arc(50, 40, 2.2, 0, 7); g.fill();
      g.fillStyle = deep; g.beginPath(); g.arc(50.8, 40, 1.1, 0, 7); g.fill();
      g.strokeStyle = deep; g.lineWidth = 1.2; g.beginPath(); g.moveTo(51, 45); g.lineTo(56, 45); g.stroke();
      if (seat >= 0) numberOn(g, seat + 1, 22, 45, 9);
      g.restore();
      return;
    }
    if (pose === 5) {
      // вибув: сидить у снігу, очі хрестиками
      leg(24, 50, 36, 54); leg(16, 50, 26, 54);
      g.fillStyle = col;
      g.beginPath(); g.moveTo(12, 44); g.lineTo(0, 52); g.lineTo(14, 50); g.fill();
      g.beginPath(); g.ellipse(22, 45, 13, 10, 0, 0, 7); g.fill();
      g.fillStyle = col; g.beginPath(); g.roundRect(28, 30, 18, 12, 5); g.fill();
      g.strokeStyle = deep; g.lineWidth = 1.3;
      g.beginPath(); g.moveTo(38, 33); g.lineTo(42, 37); g.moveTo(42, 33); g.lineTo(38, 37); g.stroke();
      if (seat >= 0) numberOn(g, seat + 1, 20, 46, 9);
      g.restore();
      return;
    }
    // ноги
    if (pose === 0) { leg(17, 38, 12, 51, 17, 54); leg(27, 38, 32, 54); }
    else if (pose === 1) { leg(17, 38, 20, 54); leg(27, 38, 24, 49, 29, 49); }
    else { leg(17, 38, 14, 46, 19, 48); leg(27, 38, 31, 45, 34, 47); }
    // хвіст і шипи
    g.fillStyle = col;
    g.beginPath(); g.moveTo(14, 25); g.quadraticCurveTo(4, 26, 0, 22); g.lineTo(2, 30); g.quadraticCurveTo(8, 36, 14, 37); g.fill();
    g.fillStyle = dark;
    g.beginPath(); g.moveTo(12, 24); g.lineTo(15, 18); g.lineTo(18, 23); g.moveTo(18, 22); g.lineTo(21, 16); g.lineTo(24, 22); g.fill();
    // тулуб
    g.fillStyle = col;
    g.beginPath(); g.ellipse(22, 31, 13, 10, 0, 0, 7); g.fill();
    g.save(); g.beginPath(); g.ellipse(22, 31, 13, 10, 0, 0, 7); g.clip(); pattern(g, seat, 9, 21, 26, 20, dark); g.restore();
    g.fillStyle = belly; g.beginPath(); g.ellipse(25, 36, 8, 4.5, -0.2, 0, 7); g.fill();
    // шия й голова
    g.fillStyle = col;
    g.beginPath(); g.roundRect(26, 14, 9, 18, 4); g.fill();
    g.beginPath(); g.roundRect(27, 5, 21, 13, 5); g.fill();
    g.fillStyle = belly; g.beginPath(); g.roundRect(33, 13, 15, 4, 2); g.fill();
    g.fillStyle = '#fff'; g.beginPath(); g.arc(39, 9.5, 2.4, 0, 7); g.fill();
    g.fillStyle = deep; g.beginPath(); g.arc(pose === 4 ? 39 : 39.9, 9.5, 1.2, 0, 7); g.fill();
    g.strokeStyle = deep; g.lineWidth = 1.2; g.beginPath(); g.moveTo(42, 15); g.lineTo(47, 15); g.stroke();
    // лапка
    g.strokeStyle = dark; g.lineWidth = 2.6; g.beginPath(); g.moveTo(32, 30); g.lineTo(37, pose === 2 ? 28 : 34); g.stroke();
    if (seat >= 0) numberOn(g, seat + 1, 20, 31, 9);
    g.restore();
  }

  /// Лелека в коробці 64×44: низ хітбокса на y = 34, лівий край — x = 14. frame: 0 крила вгору, 1 вниз.
  function paintStork(g, frame, col, seat, pal) {
    const white = pal.stork, black = '#1b1b1f';
    g.save();
    g.lineCap = 'round'; g.lineJoin = 'round';
    // ноги назад
    g.strokeStyle = pal.beak; g.lineWidth = 1.8;
    g.beginPath(); g.moveTo(18, 27); g.lineTo(3, 30); g.moveTo(19, 29); g.lineTo(4, 33); g.stroke();
    // хвіст — кольору місця: зграю з восьми лелек мало б читати й без бирок
    g.fillStyle = mix(col, black, 0.25); g.beginPath(); g.moveTo(16, 22); g.lineTo(9, 21); g.lineTo(10, 27); g.lineTo(17, 27); g.fill();
    // тіло
    g.fillStyle = white; g.beginPath(); g.ellipse(27, 24, 13, 7, -0.08, 0, 7); g.fill();
    // шия, голова, дзьоб
    g.strokeStyle = white; g.lineWidth = 5; g.beginPath(); g.moveTo(36, 21); g.quadraticCurveTo(42, 17, 46, 15); g.stroke();
    g.fillStyle = white; g.beginPath(); g.arc(47, 14, 4.4, 0, 7); g.fill();
    g.fillStyle = pal.beak; g.beginPath(); g.moveTo(50, 12.5); g.lineTo(62, 15.5); g.lineTo(50, 16.5); g.fill();
    g.fillStyle = black; g.beginPath(); g.arc(48.2, 13, 1.1, 0, 7); g.fill();
    // хустка кольору місця
    g.fillStyle = col; g.beginPath(); g.moveTo(37, 17); g.lineTo(42, 14); g.lineTo(44, 19); g.lineTo(39, 23); g.fill();
    g.beginPath(); g.moveTo(38, 21); g.lineTo(34, 29); g.lineTo(40, 24); g.fill();
    // крило — теж у колір місця (світліше), кінчики лишаються чорними, як у справжньої лелеки
    g.fillStyle = mix(col, white, 0.3);
    g.beginPath();
    if (frame === 0) { g.moveTo(21, 21); g.lineTo(11, 3); g.lineTo(22, 5); g.lineTo(33, 20); }
    else { g.moveTo(21, 24); g.lineTo(12, 40); g.lineTo(24, 38); g.lineTo(33, 25); }
    g.fill();
    g.fillStyle = black;
    g.beginPath();
    if (frame === 0) { g.moveTo(11, 3); g.lineTo(22, 5); g.lineTo(19, 9); g.lineTo(13, 8); }
    else { g.moveTo(12, 40); g.lineTo(24, 38); g.lineTo(21, 35); g.lineTo(15, 35); }
    g.fill();
    g.fillStyle = col; g.beginPath(); g.arc(frame === 0 ? 24 : 24, frame === 0 ? 13 : 31, 5.2, 0, 7); g.fill();
    numberOn(g, seat + 1, 24, frame === 0 ? 13 : 31, 8);
    g.restore();
  }

  /// Обвідка кольором місця навколо фігури (кільце ≈ 1,6 px). Чужих малюємо напівпрозорими, і без неї жовтий на
  /// темному небі ставав бурим (плутався з рудим), а лелеки були однаково білі. Будується раз, у кадрі — один drawImage.
  function ringOf(src, w, h, s, col) {
    const sil = off(w, h, s);
    sil.g.drawImage(src, 0, 0, w, h);
    sil.g.globalCompositeOperation = 'source-in';
    sil.g.fillStyle = col; sil.g.fillRect(0, 0, w, h);
    const o = off(w, h, s), g = o.g;
    for (let a = 0; a < 8; a++) g.drawImage(sil.c, Math.cos(a * Math.PI / 4) * 1.6, Math.sin(a * Math.PI / 4) * 1.6, w, h);
    g.globalCompositeOperation = 'destination-out';
    g.drawImage(src, 0, 0, w, h);
    return o.c;
  }

  function buildSprites(st) {
    const s = st.dpr * st.K, pal = st.pal;
    const spr = { dino: [], stork: [], ring: [], ob: {}, pk: {}, lazy: new Map(), labels: new Map() };
    for (let i = 0; i < SEATS; i++) {
      const col = pal.seats[i], a = [];
      for (let pose = 0; pose < 6; pose++) { const o = off(DINO_BOX.w, DINO_BOX.h, s); paintDino(o.g, pose, col, i); a.push(o.c); }
      spr.dino.push(a);
      const b = [];
      for (let fr = 0; fr < 2; fr++) { const o = off(STORK_BOX.w, STORK_BOX.h, s); paintStork(o.g, fr, col, i, pal); b.push(o.c); }
      spr.stork.push(b);
      // обвідки — лише для свого режиму (чужі — у кожного місця свої)
      spr.ring.push(st.mode === DINO ? a.map((c) => ringOf(c, DINO_BOX.w, DINO_BOX.h, s, col)) : b.map((c) => ringOf(c, STORK_BOX.w, STORK_BOX.h, s, col)));
    }
    // привид найкращої спроби дня — блідо-крижаний, без номера й візерунка
    if (st.daily) {
      spr.ghost = [];
      for (let pose = 0; pose < 6; pose++) { const o = off(DINO_BOX.w, DINO_BOX.h, s); paintDino(o.g, pose, '#dff3ff', -1); spr.ghost.push(o.c); }
    }
    // ---- брили, бурулька, птеродактиль, сніжка ----
    const block = (g, x, y, w, h, cap) => {
      g.fillStyle = pal.ice2; g.beginPath(); g.roundRect(x, y, w, h, 4); g.fill();
      g.fillStyle = pal.ice; g.beginPath(); g.roundRect(x + 1.5, y + 1.5, w - 3, h - 4, 3.5); g.fill();
      g.strokeStyle = 'rgba(255,255,255,.75)'; g.lineWidth = 1.6;
      g.beginPath(); g.moveTo(x + 4, y + h - 7); g.lineTo(x + 4, y + 5); g.lineTo(x + w * 0.55, y + 5); g.stroke();
      g.strokeStyle = mix(pal.ice2, '#0b1620', 0.25); g.lineWidth = 1;
      g.beginPath(); g.moveTo(x + w * 0.62, y + h * 0.35); g.lineTo(x + w * 0.74, y + h * 0.55); g.lineTo(x + w * 0.66, y + h * 0.78); g.stroke();
      if (cap) { g.fillStyle = pal.snow; g.beginPath(); g.moveTo(x - 1, y + 4); g.quadraticCurveTo(x + w / 2, y - 6, x + w + 1, y + 4); g.quadraticCurveTo(x + w / 2, y + 3, x - 1, y + 4); g.fill(); }
    };
    const mk = (name, w, h, paint) => { const o = off(w, h, s); paint(o.g); spr.ob[name] = o.c; };
    mk('low', 36, 36, (g) => block(g, 1, 5, 34, 30, true));
    mk('low2', 70, 36, (g) => { block(g, 1, 5, 34, 30, true); block(g, 35, 5, 34, 30, true); });
    mk('high', 36, 70, (g) => { block(g, 1, 5, 34, 30, false); block(g, 1, 35, 34, 30, false); block(g, 1, 5, 34, 64, true); });
    mk('wide', 102, 36, (g) => { block(g, 1, 5, 34, 30, true); block(g, 34, 5, 34, 30, true); block(g, 67, 5, 34, 30, true); });
    mk('snow', 30, 32, (g) => {
      g.fillStyle = mix(pal.ice2, '#3a7ab8', 0.35); g.beginPath(); g.roundRect(2, 5, 26, 26, 6); g.fill();
      g.fillStyle = mix(pal.ice, '#6fb3e8', 0.35); g.beginPath(); g.roundRect(3.5, 6.5, 23, 21, 5); g.fill();
      g.fillStyle = pal.snow; g.beginPath(); g.arc(9, 11, 2.2, 0, 7); g.arc(20, 16, 1.6, 0, 7); g.arc(13, 21, 1.3, 0, 7); g.fill();
      g.beginPath(); g.moveTo(1, 8); g.quadraticCurveTo(15, -2, 29, 8); g.quadraticCurveTo(15, 6, 1, 8); g.fill();
    });
    // бурулька: від «стелі» кадру (крижаний карниз) до 30 px над землею — 210 px завдовжки
    mk('icicle', 56, 214, (g) => {
      g.fillStyle = mix(pal.mid, '#0b1620', 0.2); g.beginPath(); g.roundRect(0, 0, 56, 12, 5); g.fill();
      g.fillStyle = pal.snow; g.beginPath(); g.roundRect(0, 0, 56, 7, 4); g.fill();
      const grd = g.createLinearGradient(13, 0, 43, 0);
      grd.addColorStop(0, pal.ice2); grd.addColorStop(0.45, pal.ice); grd.addColorStop(1, pal.ice2);
      g.fillStyle = grd;
      g.beginPath(); g.moveTo(13, 10); g.lineTo(43, 10); g.lineTo(42, 180); g.lineTo(35, 206); g.lineTo(28, 212); g.lineTo(21, 206); g.lineTo(14, 180); g.closePath(); g.fill();
      g.fillStyle = mix(pal.ice, '#ffffff', 0.4);
      g.beginPath(); g.moveTo(4, 10); g.lineTo(12, 10); g.lineTo(8, 40); g.closePath(); g.fill();
      g.beginPath(); g.moveTo(44, 10); g.lineTo(52, 10); g.lineTo(48, 56); g.closePath(); g.fill();
      g.strokeStyle = 'rgba(255,255,255,.7)'; g.lineWidth = 1.5; g.beginPath(); g.moveTo(19, 14); g.lineTo(20, 180); g.stroke();
    });
    for (let fr = 0; fr < 2; fr++) mk('ptero' + fr, 52, 32, (g) => {
      const body = '#8b7bb0', dark = '#4c3f6b';
      g.fillStyle = dark; g.beginPath();
      if (fr === 0) { g.moveTo(16, 16); g.lineTo(28, 1); g.lineTo(34, 16); } else { g.moveTo(16, 16); g.lineTo(26, 31); g.lineTo(34, 16); }
      g.fill();
      g.fillStyle = body; g.beginPath(); g.ellipse(26, 17, 13, 5, 0, 0, 7); g.fill();
      g.beginPath(); g.moveTo(8, 12); g.lineTo(1, 16); g.lineTo(9, 18); g.fill();                   // дзьоб — назустріч бігунам
      g.beginPath(); g.moveTo(34, 13); g.lineTo(46, 8); g.lineTo(38, 16); g.fill();                  // гребінь
      g.fillStyle = '#ffd35a'; g.beginPath(); g.arc(12, 14, 1.7, 0, 7); g.fill();
    });
    // ---- підбирачки 24×24 ----
    const pk = (name, paint) => { const o = off(24, 24, s); paint(o.g); spr.pk[name] = o.c; };
    pk('egg', (g) => {
      g.fillStyle = '#fbf6ea'; g.beginPath(); g.ellipse(12, 13, 7.5, 9.5, 0, 0, 7); g.fill();
      g.fillStyle = '#7fae6a'; g.beginPath(); g.arc(9, 10, 1.5, 0, 7); g.arc(14, 15, 1.8, 0, 7); g.arc(10, 18, 1.1, 0, 7); g.arc(15, 8, 1, 0, 7); g.fill();
      g.strokeStyle = 'rgba(0,0,0,.25)'; g.lineWidth = 1; g.beginPath(); g.ellipse(12, 13, 7.5, 9.5, 0, 0, 7); g.stroke();
    });
    pk('pepper', (g) => {
      g.fillStyle = '#e0412f'; g.beginPath(); g.moveTo(6, 8); g.quadraticCurveTo(20, 6, 19, 18); g.quadraticCurveTo(17, 22, 12, 20); g.quadraticCurveTo(14, 12, 5, 11); g.fill();
      g.strokeStyle = '#4f9a3a'; g.lineWidth = 2.4; g.lineCap = 'round'; g.beginPath(); g.moveTo(6, 9); g.quadraticCurveTo(4, 5, 8, 3); g.stroke();
      g.fillStyle = 'rgba(255,255,255,.55)'; g.beginPath(); g.ellipse(13, 10, 3, 1.2, 0.2, 0, 7); g.fill();
    });
    pk('snowball', (g) => {
      g.fillStyle = '#ffffff'; g.beginPath(); g.arc(12, 12, 9, 0, 7); g.fill();
      g.fillStyle = mix(pal.ice2, '#3a7ab8', 0.2); g.beginPath(); g.arc(14, 14, 7, 0.1, 2.9); g.fill();
      g.fillStyle = '#ffffff'; g.beginPath(); g.arc(12, 11, 6.6, 0, 7); g.fill();
      g.strokeStyle = pal.ice2; g.lineWidth = 1.2; g.beginPath(); g.moveTo(7, 12); g.lineTo(17, 12); g.moveTo(12, 7); g.lineTo(12, 17); g.moveTo(8.5, 8.5); g.lineTo(15.5, 15.5); g.moveTo(15.5, 8.5); g.lineTo(8.5, 15.5); g.stroke();
    });
    st.spr = spr;
  }

  /// Бирка з ніком — один раз на нік і місце, далі drawImage.
  function label(st, i, nick) {
    const key = i + '|' + nick;
    let c = st.spr.labels.get(key);
    if (c) return c;
    const s = st.dpr * st.K, text = nick.length > 12 ? nick.slice(0, 11) + '…' : nick;
    const probe = off(1, 1, 1).g;
    probe.font = '700 10px system-ui, sans-serif';
    const w = Math.ceil(probe.measureText(text).width) + 16;
    const o = off(w, 15, s), g = o.g;
    g.fillStyle = 'rgba(8, 16, 24, .72)'; g.beginPath(); g.roundRect(0, 0, w, 15, 7.5); g.fill();
    g.fillStyle = st.pal.seats[i]; g.beginPath(); g.arc(7, 7.5, 3.2, 0, 7); g.fill();
    g.font = '700 10px system-ui, sans-serif'; g.textBaseline = 'middle'; g.textAlign = 'left';
    g.fillStyle = '#ffffff'; g.fillText(text, 13, 8);
    c = o.c; c._w = w;
    st.spr.labels.set(key, c);
    return c;
  }

  // =============================================================================================
  // 5. Сцена: небо й паралакс-смуги (offscreen, тайл 1600 px), земля, перешкоди Лелек за висотою
  // =============================================================================================

  const TILE = 1600;

  /// Смуга паралаксу: малюємо фігури двічі (x і x ± 1600), щоб тайл сходився без шва.
  function band(st, top, h, paint) {
    const o = off(TILE, h, st.dpr * st.K);
    o.g.translate(0, -top);
    paint(o.g, (fn) => { for (const dx of [-TILE, 0, TILE]) { o.g.save(); o.g.translate(dx, 0); fn(); o.g.restore(); } });
    return { c: o.c, top, h };
  }

  function buildScene(st) {
    const pal = st.pal, W = st.viewW, s = st.dpr * st.K;
    const sc = {};
    const VH = st.vh, oy = st.oy;
    const sky = off(W, VH, s), g = sky.g;
    const rnd = Lcg(st.mode === STORKS ? 77 : 42);
    if (st.mode === DINO) {
      const grd = g.createLinearGradient(0, 0, 0, GROUND + oy);
      grd.addColorStop(0, pal.sky1); grd.addColorStop(1, pal.sky2);
      g.fillStyle = grd; g.fillRect(0, 0, W, VH);
      for (let i = 0; i < 40; i++) {
        g.globalAlpha = 0.35 + rnd() * 0.6;
        g.fillStyle = '#ffffff';
        const r = rnd() < 0.2 ? 1.6 : 1;
        g.fillRect(rnd() * W, rnd() * 150, r, r);
      }
      g.globalAlpha = 1;
      const mx = W * 0.8, my = 52;
      const glow = g.createRadialGradient(mx, my, 10, mx, my, 60);
      glow.addColorStop(0, 'rgba(230, 244, 255, .35)'); glow.addColorStop(1, 'rgba(230, 244, 255, 0)');
      g.fillStyle = glow; g.fillRect(mx - 60, my - 60, 120, 120);
      g.fillStyle = '#eef6ff'; g.beginPath(); g.arc(mx, my, 17, 0, 7); g.fill();
      g.fillStyle = 'rgba(160, 190, 215, .45)'; g.beginPath(); g.arc(mx - 5, my - 4, 3.5, 0, 7); g.arc(mx + 6, my + 5, 2.5, 0, 7); g.fill();
      // далекі гори (0,10×)
      sc.far = band(st, 70, 175, (b, rep) => {
        const r2 = Lcg(5);
        const peaks = [];
        for (let x = 0; x < TILE; x += 90 + r2() * 110) peaks.push([x, 95 + r2() * 70]);
        rep(() => {
          b.fillStyle = pal.far;
          b.beginPath(); b.moveTo(0, 245);
          for (const [x, y] of peaks) { b.lineTo(x, y + 40); b.lineTo(x + 45, y); b.lineTo(x + 95, y + 45); }
          b.lineTo(TILE, 245); b.closePath(); b.fill();
          b.fillStyle = mix(pal.far, pal.snow, 0.42);
          for (const [x, y] of peaks) { b.beginPath(); b.moveTo(x + 30, y + 13); b.lineTo(x + 45, y); b.lineTo(x + 62, y + 15); b.lineTo(x + 52, y + 12); b.lineTo(x + 45, y + 17); b.lineTo(x + 38, y + 11); b.fill(); }
        });
      });
      // ялини й пагорби (0,30×)
      sc.mid = band(st, 130, 115, (b, rep) => {
        const r2 = Lcg(9);
        const trees = [];
        for (let x = 10; x < TILE; x += 24 + r2() * 60) trees.push([x, 26 + r2() * 30, r2()]);
        rep(() => {
          b.fillStyle = pal.mid;
          b.beginPath(); b.moveTo(0, 245);
          for (let x = 0; x <= TILE; x += 40) b.lineTo(x, 214 + Math.sin(x / 130) * 10 + Math.sin(x / 57) * 5);
          b.lineTo(TILE, 245); b.closePath(); b.fill();
          for (const [x, h, k] of trees) {
            const base = 214 + Math.sin(x / 130) * 10 + Math.sin(x / 57) * 5;
            b.fillStyle = mix(pal.mid, '#0b1620', 0.25 + k * 0.2);
            for (let t = 0; t < 3; t++) { const y = base - h + t * h * 0.28, w = 7 + t * 5; b.beginPath(); b.moveTo(x, y - 6); b.lineTo(x + w, y + h * 0.36); b.lineTo(x - w, y + h * 0.36); b.fill(); }
            b.fillStyle = mix(pal.snow, pal.mid, 0.25);
            b.beginPath(); b.moveTo(x, base - h - 6); b.lineTo(x + 4, base - h + 3); b.lineTo(x - 4, base - h + 3); b.fill();
          }
        });
      });
      // кучугури (0,60×)
      sc.near = band(st, 200, 50, (b, rep) => {
        const r2 = Lcg(13);
        const drifts = [];
        for (let x = 0; x < TILE; x += 70 + r2() * 160) drifts.push([x, 40 + r2() * 80, 8 + r2() * 12]);
        rep(() => {
          for (const [x, w, h] of drifts) {
            b.fillStyle = pal.ice2; b.beginPath(); b.ellipse(x + 3, 242, w, h, 0, Math.PI, 0); b.fill();
            b.fillStyle = pal.snow; b.beginPath(); b.ellipse(x, 242, w, h - 2, 0, Math.PI, 0); b.fill();
          }
        });
      });
    } else {
      const grd = g.createLinearGradient(0, 0, 0, GROUND + oy);
      grd.addColorStop(0, pal.dusk1); grd.addColorStop(1, pal.dusk2);
      g.fillStyle = grd; g.fillRect(0, 0, W, VH);
      const sx = W * 0.72, sy = 196 + oy;
      const glow = g.createRadialGradient(sx, sy, 14, sx, sy, 110);
      glow.addColorStop(0, 'rgba(255, 214, 140, .55)'); glow.addColorStop(1, 'rgba(255, 190, 120, 0)');
      g.fillStyle = glow; g.fillRect(sx - 110, sy - 110, 220, 220);
      g.fillStyle = '#ffd98a'; g.beginPath(); g.arc(sx, sy, 30, 0, 7); g.fill();
      for (let i = 0; i < 6; i++) {
        const cx = rnd() * W, cy = 30 + oy / 2 + rnd() * 90, w = 40 + rnd() * 50;
        g.fillStyle = 'rgba(255, 228, 210, ' + (0.18 + rnd() * 0.18).toFixed(2) + ')';
        g.beginPath(); g.ellipse(cx, cy, w, 8, 0, 0, 7); g.ellipse(cx + w * 0.3, cy - 6, w * 0.45, 8, 0, 0, 7); g.fill();
      }
      // далекі пагорби й церква (0,10×)
      sc.far = band(st, 120, 125, (b, rep) => {
        rep(() => {
          b.fillStyle = mix(pal.dusk1, pal.field2, 0.45);
          b.beginPath(); b.moveTo(0, 245);
          for (let x = 0; x <= TILE; x += 40) b.lineTo(x, 200 + Math.sin(x / 210) * 16 + Math.sin(x / 83) * 6);
          b.lineTo(TILE, 245); b.closePath(); b.fill();
          for (const cx of [380, 1180]) {
            const base = 200 + Math.sin(cx / 210) * 16 + Math.sin(cx / 83) * 6;
            b.fillStyle = mix(pal.dusk1, '#000000', 0.15);
            b.fillRect(cx - 16, base - 34, 32, 36); b.fillRect(cx - 6, base - 52, 12, 20);
            b.beginPath(); b.ellipse(cx, base - 56, 8, 9, 0, 0, 7); b.fill();
            b.beginPath(); b.ellipse(cx - 11, base - 36, 5, 6, 0, 0, 7); b.ellipse(cx + 11, base - 36, 5, 6, 0, 0, 7); b.fill();
            b.fillRect(cx - 0.8, base - 72, 1.6, 10); b.fillRect(cx - 4, base - 68, 8, 1.6);
          }
        });
      });
      // село (0,35×)
      sc.mid = band(st, 150, 95, (b, rep) => {
        const r2 = Lcg(21);
        const houses = [];
        for (let x = 20; x < TILE; x += 90 + r2() * 120) houses.push([x, 36 + r2() * 20, 20 + r2() * 8, r2()]);
        rep(() => {
          for (const [x, w, h, k] of houses) {
            const base = 236;
            b.fillStyle = mix(pal.wall, pal.dusk1, 0.35); b.fillRect(x, base - h, w, h);
            b.fillStyle = mix(pal.roof, pal.dusk1, 0.3);
            b.beginPath(); b.moveTo(x - 6, base - h + 1); b.lineTo(x + w / 2, base - h - 17); b.lineTo(x + w + 6, base - h + 1); b.fill();
            b.fillStyle = k > 0.3 ? '#ffcf6b' : mix(pal.wall, pal.dusk1, 0.6);
            b.fillRect(x + w * 0.2, base - h + 6, 6, 6); if (w > 44) b.fillRect(x + w * 0.62, base - h + 6, 6, 6);
          }
        });
      });
      // соняшники й тин (0,60×)
      sc.near = band(st, 190, 60, (b, rep) => {
        const r2 = Lcg(33);
        const fl = [];
        for (let x = 0; x < TILE; x += 11 + r2() * 26) fl.push([x, 26 + r2() * 22]);
        rep(() => {
          b.strokeStyle = mix(pal.roof, '#000000', 0.2); b.lineWidth = 2;
          for (let x = 0; x < TILE; x += 180) {
            b.beginPath(); for (let k = 0; k < 4; k++) { b.moveTo(x + k * 14, 244); b.lineTo(x + k * 14, 226); } b.stroke();
            b.beginPath(); b.moveTo(x - 4, 232); b.quadraticCurveTo(x + 21, 228, x + 46, 232); b.moveTo(x - 4, 238); b.quadraticCurveTo(x + 21, 234, x + 46, 238); b.stroke();
          }
          for (const [x, h] of fl) {
            b.strokeStyle = pal.field; b.lineWidth = 1.6; b.beginPath(); b.moveTo(x, 244); b.lineTo(x + 1, 244 - h); b.stroke();
            b.fillStyle = '#f2b93b'; b.beginPath(); b.arc(x + 1, 244 - h, 4.2, 0, 7); b.fill();
            b.fillStyle = '#5a3a1a'; b.beginPath(); b.arc(x + 1, 244 - h, 1.8, 0, 7); b.fill();
          }
        });
      });
    }
    sc.sky = sky.c;
    // земля: тайл 256 × 60
    const gr = off(256, VIEW_H - GROUND, s), q = gr.g;
    if (st.mode === DINO) {
      const lg = q.createLinearGradient(0, 0, 0, 60);
      lg.addColorStop(0, pal.ice); lg.addColorStop(1, mix(pal.ice2, '#0b1620', 0.35));
      q.fillStyle = lg; q.fillRect(0, 0, 256, 60);
      q.fillStyle = 'rgba(255,255,255,.85)'; q.fillRect(0, 0, 256, 2);
      q.strokeStyle = mix(pal.ice2, '#0b1620', 0.15); q.lineWidth = 1;
      const r3 = Lcg(3);
      q.beginPath();
      for (let i = 0; i < 9; i++) { const x = r3() * 256, y = 6 + r3() * 40; q.moveTo(x, y); q.lineTo(x + 10 + r3() * 18, y + 3 + r3() * 8); q.lineTo(x + 20 + r3() * 16, y + r3() * 6); }
      q.stroke();
    } else {
      q.fillStyle = pal.field2; q.fillRect(0, 0, 256, 60);
      q.fillStyle = mix(pal.field2, '#d9c08a', 0.45); q.fillRect(0, 18, 256, 12);
      q.fillStyle = pal.field; q.fillRect(0, 0, 256, 3);
      const r3 = Lcg(4);
      q.strokeStyle = mix(pal.field, '#ffffff', 0.15); q.lineWidth = 1;
      q.beginPath(); for (let i = 0; i < 30; i++) { const x = r3() * 256, y = 3 + r3() * 50; q.moveTo(x, y); q.lineTo(x + 1.5, y - 4); } q.stroke();
    }
    sc.ground = gr.c;
    if (st.mode === DINO) {
      // Іній з лівого краю: лавина ще за кадром, але вже близько — сцена мерзне від краю (малюється розтягнутою)
      const fr = off(160, VIEW_H, s), q2 = fr.g;
      const lg2 = q2.createLinearGradient(0, 0, 160, 0);
      lg2.addColorStop(0, 'rgba(236, 246, 255, .95)'); lg2.addColorStop(0.35, 'rgba(214, 234, 252, .55)'); lg2.addColorStop(1, 'rgba(214, 234, 252, 0)');
      q2.fillStyle = lg2; q2.fillRect(0, 0, 160, VIEW_H);
      const r4 = Lcg(9);
      q2.strokeStyle = 'rgba(255, 255, 255, .8)'; q2.lineWidth = 1.2;
      q2.beginPath();
      for (let i = 0; i < 26; i++) {
        const x = r4() * 70, y = r4() * VIEW_H, len = 5 + r4() * 9;
        for (let a = 0; a < 3; a++) { const an = a * Math.PI / 3 + r4() * 0.3; q2.moveTo(x - Math.cos(an) * len, y - Math.sin(an) * len); q2.lineTo(x + Math.cos(an) * len, y + Math.sin(an) * len); }
      }
      q2.stroke();
      sc.frost = fr.c;
    }
    st.scene = sc;
  }

  function drawBand(g, b, camPx, k, W) {
    if (!b) return;
    let x = -((camPx * k) % TILE);
    if (x > 0) x -= TILE;
    for (; x < W; x += TILE) g.drawImage(b.c, x, b.top, TILE, b.h);
  }

  /// Перешкоди Лелек мають змінну висоту — спрайти будуємо лениво, по одному на висоту.
  function storkSprite(st, kind, hPx) {
    const key = kind * 1000 + hPx;
    let c = st.spr.lazy.get(key);
    if (c) return c;
    const pal = st.pal, s = st.dpr * st.K;
    let o;
    if (kind === K.Chimney) {
      // вузька мазанка з комином: весь силует — хітбокс (40 px), тож чесно
      o = off(48, hPx + 10, s);
      const g = o.g, top = 10, H = hPx;
      const roofY = top + Math.max(18, H * 0.34);
      g.fillStyle = pal.wall; g.fillRect(4, roofY, 40, top + H - roofY);
      g.fillStyle = mix(pal.wall, '#000000', 0.12); g.fillRect(4, top + H - 6, 40, 6);
      g.fillStyle = '#ffcf6b'; g.fillRect(16, roofY + 12, 10, 9);
      g.strokeStyle = mix(pal.roof, '#000000', 0.3); g.lineWidth = 1; g.strokeRect(16, roofY + 12, 10, 9);
      g.fillStyle = pal.roof; g.beginPath(); g.moveTo(1, roofY + 4); g.lineTo(24, roofY - 12); g.lineTo(47, roofY + 4); g.closePath(); g.fill();
      g.fillStyle = mix(pal.clay, '#000000', 0.15); g.fillRect(8, top, 32, roofY - top - 4);
      g.fillStyle = pal.clay; g.fillRect(6, top, 36, 6);
      g.strokeStyle = mix(pal.clay, '#000000', 0.4); g.lineWidth = 1;
      g.beginPath(); for (let y = top + 10; y < roofY - 6; y += 6) { g.moveTo(8, y); g.lineTo(40, y); } g.stroke();
    } else if (kind === K.Tree) {
      // верба, що звисає згори: густа крона від самого верху кадру й завіса гілок до низу хітбокса —
      // щоб читалось «звідси й донизу не пролетиш», а не кулька в небі
      o = off(64, hPx + 4, s);
      const g = o.g, H = hPx, rnd = Lcg(hPx * 7 + 3);
      const dark = mix(pal.field2, '#000000', 0.3), leaf = pal.field2, light = mix(pal.field, '#ffe7a0', 0.25);
      g.strokeStyle = mix(pal.roof, '#000000', 0.35); g.lineWidth = 4; g.lineCap = 'round';
      g.beginPath(); g.moveTo(-4, 0); g.quadraticCurveTo(18, H * 0.12, 30, H * 0.3); g.stroke();   // гілляка з-за кадру
      g.fillStyle = dark;
      g.fillRect(0, 0, 64, H * 0.3);
      g.beginPath();
      for (let x = 4; x <= 60; x += 11) { const r = 9 + rnd() * 4; g.moveTo(x + r, H * 0.32); g.arc(x, H * 0.32, r, 0, 7); }
      g.fill();
      // завіса гілок: від крони до самого низу хітбокса
      g.lineWidth = 1.6;
      for (let x = 3; x <= 61; x += 4.2) {
        const len = H * (0.86 + rnd() * 0.12), bend = (rnd() - 0.5) * 5;
        g.strokeStyle = rnd() < 0.5 ? leaf : mix(leaf, '#000000', 0.15);
        g.beginPath(); g.moveTo(x, H * 0.25); g.quadraticCurveTo(x + bend, H * 0.6, x + bend * 0.6, Math.min(H - 1, len)); g.stroke();
      }
      g.fillStyle = light;
      g.beginPath();
      for (let k = 0; k < 22; k++) { const x = 3 + rnd() * 58, y = H * (0.35 + rnd() * 0.6); g.moveTo(x + 1.6, y); g.ellipse(x, y, 1.6, 2.6, 0.3, 0, 7); }
      g.fill();
      g.fillStyle = leaf;
      g.beginPath();
      for (let x = 8; x <= 56; x += 12) { g.moveTo(x + 7, H * 0.18); g.arc(x, H * 0.18, 7, 0, 7); }
      g.fill();
    } else if (kind === K.Pole) {
      o = off(18, hPx + 2, s);
      const g = o.g;
      g.fillStyle = mix(pal.roof, '#000000', 0.25); g.fillRect(2, 2, 14, hPx);
      g.fillStyle = mix(pal.roof, '#ffffff', 0.15); g.fillRect(3, 2, 3, hPx);
      g.fillStyle = '#dfe7ea'; g.fillRect(0, 8, 18, 3); g.beginPath(); g.arc(3, 7, 2.2, 0, 7); g.arc(15, 7, 2.2, 0, 7); g.fill();
    } else { // Nest
      o = off(56, 34, s);
      const g = o.g;
      g.fillStyle = '#8a6a3c'; g.beginPath(); g.ellipse(28, 20, 26, 10, 0, 0, 7); g.fill();
      g.strokeStyle = '#5b4020'; g.lineWidth = 1.4;
      g.beginPath(); for (let i = 0; i < 9; i++) { g.moveTo(4 + i * 5, 14 + (i % 2) * 6); g.lineTo(10 + i * 5, 26 - (i % 3) * 4); } g.stroke();
      g.fillStyle = pal.stork; g.beginPath(); g.arc(30, 9, 6, 0, 7); g.fill();
      g.fillStyle = pal.beak; g.beginPath(); g.moveTo(34, 8); g.lineTo(42, 10); g.lineTo(34, 11); g.fill();
      g.fillStyle = '#1b1b1f'; g.beginPath(); g.arc(32, 7.5, 0.9, 0, 7); g.fill();
    }
    c = o.c;
    st.spr.lazy.set(key, c);
    return c;
  }

  // =============================================================================================
  // 6. Стан картки: раунд, кадри, годинник кроків, передбачення й звірка (dino.md §5.6)
  // =============================================================================================

  function makeState(root, ctx, kind) {
    return {
      root, ctx, kind, mode: kind === 'storks' ? STORKS : DINO, daily: kind === 'daily',
      el: {}, cv: null, viewW: 800, K: 1, dpr: 1, pal: null, spr: null, scene: null, sizeSig: '',
      // Лелеки літають до 290 px над землею, а в кризі над землею лише 240: їхня сцена вища на 52 px
      oy: kind === 'storks' ? STORK_OY : 0, vh: VIEW_H + (kind === 'storks' ? STORK_OY : 0),
      sim: null, simArgs: null, key: '', me: null, ph: '', running: false, waitView: false,
      acc: 0, lastT: 0, adj: 0, adjUntil: 0, eAvg: 0, rate: 1 / STEP_MS, arr: [],
      lead: 5, rtt: 80, rtts: [], slow: 0, pingAt: 0,
      hist: Array.from({ length: HIST_N }, () => new Player()), histAt: new Int32Array(HIST_N).fill(-1),
      held: 0, edge: false, lastHeld: 0, pend: [], sent: 0, fixes: 0, snaps: 0, snapWhy: {}, snapLog: [], fixLog: [],
      fr: new Array(FR_N).fill(null), frN: 0, frHead: 0,
      latest: null, latestAt: 0, snowWire: new Map(),
      vis: { lag: 0, y: 0 }, camBias: 0, lastCam: 0, camFocus: null, wasLive: null,
      prevMode: new Int8Array(SEATS).fill(-1), outAt: new Float64Array(SEATS), pops: [],
      goAt: 0, ownOutAt: 0, throwAt: 0, rematchAt: 0, finAt: 0, goNext: 0, lastLand: 0, lastDraw: 0, ownSx: null,
      runLog: null, ghost: null,
      ptr: null, ptrY: 0, ptrDuck: false, rmb: false,
      raf: 0, hudAt: 0, stripAt: 0, hudSig: '', overSig: '', perf: { frames: 0, ms: 0, max: 0 },
      flakes: null, dust: null, board: null, boardAt: 0, boardBusy: false, boardRuns: -1,
      listeners: [], ro: null,
    };
  }

  const isRunPhase = (st, ph) => (st.daily ? ph === 'run' : ph === 'ready' || ph === 'run');
  const meOf = (ctx, v) => (ctx.mine && v && v.plays && v.plays[ctx.seat] ? ctx.seat : null);
  const keyOf = (ctx, v) => (ctx.room.round || 0) + ':' + (v.round || 0) + ':' + (v.seed || 0) + ':' + meOf(ctx, v) + ':' + (v.ph === 'lobby' ? 'l' : 'g');

  /// Кільце кадрів (s, p) для інтерполяції чужих за годинником кроків, а не за часом приходу.
  function pushFrame(st, s, p) {
    if (st.frN > 0) {
      const last = st.fr[(st.frHead + st.frN - 1) % FR_N];
      if (s < last.s) return;
      if (s === last.s) { last.p = p; return; }
    }
    const f = { s, p };
    if (st.frN < FR_N) { st.fr[(st.frHead + st.frN) % FR_N] = f; st.frN++; }
    else { st.fr[st.frHead] = f; st.frHead = (st.frHead + 1) % FR_N; }
  }

  const BR = { a: null, b: null, k: 0 };
  function bracket(st, rs) {
    let a = null, b = null;
    for (let n = st.frN - 1; n >= 0; n--) {
      const f = st.fr[(st.frHead + n) % FR_N];
      if (f.s <= rs) { a = f; break; }
      b = f;
    }
    if (!a) a = b;
    if (!b) b = a;
    BR.a = a; BR.b = b;
    BR.k = a && b && b.s > a.s ? clamp((rs - a.s) / (b.s - a.s), 0, 1) : 0;
    return BR;
  }

  /// Світ до кроку target без гравців: курс генерується крок за кроком — точно як на сервері.
  function ffWorld(sim, target) {
    while (sim.S < target) {
      const run = sim.S - sim.readySteps;
      if (run >= 0) sim.generate(run);
      sim.S++;
    }
  }

  function makeSim(st) {
    const a = st.simArgs;
    const sim = new Sim(st.mode, a.seed, a.plays, a.ready, a.pmCap, a.snowOn, a.feather);
    for (const w of st.snowWire.values()) sim.knowSnow(w);
    return sim;
  }

  /// Телефон боком (чи інший низький екран): шапка й нижні панелі сайту з'їдають висоту, і сцена опиняється
  /// під ними. На старті раунду/спроби підкручуємо сторінку так, щоб сцена була на видноті.
  function showStage(st) {
    if (!ui.coarse() || window.innerHeight > 520 || !st.el.stage) return;
    const r = st.el.stage.getBoundingClientRect();
    if (r.top < 0 || r.bottom > window.innerHeight - 70) {
      try { st.el.stage.scrollIntoView({ block: 'center', behavior: reducedMotion() ? 'auto' : 'smooth' }); } catch (_) { /* старий браузер */ }
    }
  }

  function newRound(st, v) {
    const ctx = st.ctx, me = meOf(ctx, v);
    const plays = new Array(SEATS).fill(false);
    if (me != null) plays[me] = true;
    st.simArgs = { seed: v.seed || 1, plays, ready: v.readySteps | 0, pmCap: v.pmCap || 1000, snowOn: !!v.snowOpt, feather: v.featherOpt !== false };
    st.snowWire.clear();
    st.sim = makeSim(st);
    st.me = me;
    st.key = keyOf(ctx, v);
    st.histAt.fill(-1);
    st.frN = 0; st.frHead = 0;
    st.vis.lag = st.vis.y = 0; st.camBias = 0; st.camFocus = null; st.wasLive = null;
    st.prevMode.fill(-1); st.outAt.fill(0); st.pops.length = 0;
    st.goAt = 0; st.ownOutAt = 0; st.waitView = false; st.pend.length = 0;
    st.ghost = null; st.runLog = null;
    st.lastHeld = 0; st.edge = false;
    const s = v.s | 0;
    ffWorld(st.sim, s);
    if (me != null && v.p && v.p[me]) st.sim.P[me].fromWire(st.mode, v.p[me]);
    if (v.snow) for (const w of v.snow) knowSnow(st, w, false);
    pushFrame(st, s, v.p || []);
    for (let i = 0; i < SEATS; i++) { const w = v.p && v.p[i]; st.prevMode[i] = w ? w[st.mode === DINO ? 3 : 2] : -1; }
    st.latest = { s, ph: v.ph, p: v.p || [] };
    st.latestAt = performance.now();
    st.ph = v.ph || '';
    st.running = isRunPhase(st, st.ph) && ctx.playing;
    st.acc = 0; st.adj = 0; st.eAvg = 0; st.lastT = performance.now();
    if (st.running) { const target = s + st.lead; while (st.sim.S < target) stepOnce(st, false); }
    st.overSig = null;
    if (canSend(st)) { send(st, st.sim.S, st.held); st.lastHeld = st.held; }   // після F5: сервер дізнається, що зараз тримають
    if (me != null && ctx.playing) setTimeout(() => showStage(st), 60);
    // Забіг дня: «Ще раз» стрибком чи тапом — той самий натиск і стартує нову спробу, як у хромівського динозаврика
    // (раніше треба було тапнути двічі: перший ховав підсумок, другий стартував).
    const go = st.goNext;
    st.goNext = 0;
    if (st.daily && go && performance.now() - go < 3000 && st.ph === 'wait' && me != null && ctx.playing) dailyStart(st);
  }

  /// Кинута брила з кадру — у курс (клієнт дізнається про кидок лише з кадру, dino.md §9). Сцена показує кидок
  /// сама: сніжка летить від кидальника до брили, над ціллю — «❄ нік» кольором кидальника (балачку столу сніжки
  /// більше не засмічують). w[4] — у кого цілили.
  function knowSnow(st, w, fx) {
    if (st.snowWire.has(w[2])) return;
    st.snowWire.set(w[2], w);
    st.sim.knowSnow(w);
    if (!fx) return;
    popAt(st, 'throw', w[0] / SUB + 13, GROUND - 20, w[1]);
    popAt(st, 'fly', w[0] / SUB + 13, GROUND - 16, w[1]);
    const to = w.length > 4 ? w[4] : -1;
    if (to >= 0 && to < SEATS && to !== w[1]) {
      const q = popAt(st, 'snowhit', 0, 0, to, true);
      if (q) {
        const nick = st.ctx.nickOf(w[1]) || st.ctx.seatName(w[1]);
        q.by = w[1];
        q.text = to === st.me ? '❄ ' + nick + ' кидає сніжку — стрибай!' : '❄ ' + nick;
      }
    }
    if (w[1] !== st.me) Snd.toss();
  }

  function rttSample(st, t) {
    const now = Math.floor(performance.now()) & 0xFFFFF;
    const d = (now - t) & 0xFFFFF;
    if (d > 5000) return;
    st.rtts.push(d);
    if (st.rtts.length > 5) st.rtts.shift();
    let m = d;
    for (const x of st.rtts) if (x < m) m = x;
    st.rtt = m;                                           // мінімум відсіює очікування тика сервера
    st.slow = d > 250 ? st.slow + 1 : 0;
    st.lead = clamp(Math.ceil(st.rtt * st.rate) + 3, 3, FUTURE_MAX);
  }

  function send(st, s, k) {
    st.ctx.input('in', { s, k });
    const now = performance.now();
    st.pend.push({ s, at: now });
    while (st.pend.length && now - st.pend[0].at > 2000) st.pend.shift();
    st.sent++;
  }

  const canSend = (st) => st.me != null && st.ctx.mine && st.ctx.playing && isRunPhase(st, st.ph);

  /// Один крок 20 мс у себе: ввід на межі кроку (лише на зміну), знімок для звірки, крок світу й свого героя.
  function stepOnce(st, fx) {
    const sim = st.sim, t = sim.S, me = st.me;
    let p = null, wasAir = false, wasOut = false;
    if (me != null) {
      p = sim.P[me];
      // Лелекам важить лише ребро змаху: відпускання не шлемо — інакше кожен тап коштував би два вводи, і на
      // швидкому клацанні квота каркаса (30/с) різала б саме змахи.
      if (!p.out && !p.down && canSend(st) && (st.mode === STORKS ? st.edge : st.edge || st.held !== st.lastHeld)) {
        const k = st.held | (st.edge ? 4 : 0);
        sim.input(me, t, k);
        send(st, t, k);
        if (st.runLog) st.runLog.push(t, k);
        st.lastHeld = st.held;
      }
      st.edge = false;
      st.hist[t & (HIST_N - 1)].copyFrom(p);
      st.histAt[t & (HIST_N - 1)] = t;
      wasAir = p.air; wasOut = p.out || p.down;
    }
    const vy0 = p ? p.vy : 0;
    sim.step();
    if (st.ghost) ghostTo(st.ghost, sim.S);
    if (p && fx) ownEvents(st, p, wasAir, wasOut, vy0);
    sim.clearEvents();
    if (t + 1 === sim.readySteps && sim.readySteps > 0 && fx) { st.goAt = performance.now(); Snd.go(); }
  }

  /// Свої події — одразу з передбачення: звук і спалах без чекання сервера.
  function ownEvents(st, p, wasAir, wasOut, vy0) {
    const sim = st.sim, now = performance.now();
    for (let i = 0; i < sim.evCount; i++) {
      const e = sim.ev[i];
      if (e.seat !== st.me) continue;
      if (e.kind === 1) { if (st.mode === DINO) Snd.hit(); else { Snd.thud(); popAt(st, 'feather', 0, 0, st.me, true); } }
      else if (e.kind === 2) Snd.hit();
      else if (e.kind === 3) { Snd.egg(); popAt(st, 'egg', 0, 0, st.me, true); }
      else if (e.kind === 4) { Snd.pepper(); popAt(st, 'pepper', 0, 0, st.me, true); }
      else if (e.kind === 5) Snd.grab();
    }
    if (st.mode === DINO) {
      if (p.air && p.vy > 0 && (!wasAir || vy0 <= 0) && !p.out) Snd.jump();
      if (wasAir && !p.air && !p.out && p.stun === 0) { Snd.land(); if (now - st.lastLand > 120) { st.lastLand = now; dustAt(st, 'own', 5); } }
    } else if (!p.down && p.vy === ST.Flap - ST.G && vy0 !== ST.Flap - ST.G) Snd.flap();
    if (!wasOut && (p.out || p.down)) ownOut(st);
  }

  function ownOut(st) {
    if (st.ownOutAt) return;
    st.ownOutAt = performance.now();
    if (st.mode === DINO) Snd.out();                     // камера сама плавно перейде на лідера (draw)
    else Snd.fall();
  }

  function applyFrame(st, f) {
    const sim = st.sim;
    if (!sim || !f) return;
    const now = performance.now();
    if (st.latest && (f.s + 6 < st.latest.s || (f.ph === 'ready' && st.latest.ph === 'over'))) st.waitView = true;
    if (st.waitView) return;                             // новий раунд: кадр обігнав вид із новим зерном
    st.latest = f;
    st.latestAt = now;
    rateSample(st, f.s, now);
    pushFrame(st, f.s, f.p || []);
    if (f.pg && st.me != null && f.pg[st.me] != null) rttSample(st, f.pg[st.me]);
    if (f.sn) for (const w of f.sn) knowSnow(st, w, true);
    if (f.ev) for (const e of f.ev) serverEvent(st, e);
    trackOuts(st, f);
    if (f.ph !== st.ph) phaseTo(st, f.ph, f);
    if (!st.running) return;
    reconcile(st, f);
    syncClock(st, f);
  }

  function serverEvent(st, e) {
    const seat = e[1];
    if (seat === st.me) return;                          // свої — з передбачення
    if (e[0] === 'egg') popAt(st, 'egg', 0, 0, seat, true);
    else if (e[0] === 'pepper') popAt(st, 'pepper', 0, 0, seat, true);
    else if (e[0] === 'hit' && st.mode === STORKS) popAt(st, 'feather', 0, 0, seat, true);
  }

  function trackOuts(st, f) {
    const p = f.p || [], mi = st.mode === DINO ? 3 : 2, now = performance.now();
    for (let i = 0; i < SEATS; i++) {
      const w = p[i];
      const m = w ? w[mi] : -1;
      if (m === 4 && st.prevMode[i] !== 4 && st.prevMode[i] !== -1) st.outAt[i] = now;
      st.prevMode[i] = m;
    }
    if (st.me != null && p[st.me] && p[st.me][mi] === 4 && !st.ownOutAt) ownOut(st);
  }

  function phaseTo(st, ph, f) {
    st.ph = ph;
    if (!isRunPhase(st, ph)) {
      // раунд скінчився: сцена застигає на останньому кадрі сервера
      st.running = false;
      const sim = st.sim;
      if (f.s < sim.S - 48) { st.sim = makeSim(st); ffWorld(st.sim, f.s); }
      else if (f.s > sim.S) ffWorld(sim, f.s);
      else sim.S = f.s;
      if (st.me != null && f.p && f.p[st.me]) st.sim.P[st.me].fromWire(st.mode, f.p[st.me]);
      st.histAt.fill(-1);
      st.vis.lag = st.vis.y = 0;
      st.overSig = null;
      return;
    }
    if (!st.running && st.ctx.playing) { st.running = true; snap(st, f, 'start'); }
  }

  /// Серверний кадр проти свого передбачення на кроці f.s: збіглось — нічого, ні — стан із кадру й перерахунок хвоста.
  function reconcile(st, f) {
    const me = st.me;
    if (me == null) return;
    const w = f.p && f.p[me];
    if (!w) return;
    const sim = st.sim, p = sim.P[me], mode = st.mode;
    if (f.s >= sim.S || sim.S - f.s >= HIST_N - 4) { snap(st, f, f.s >= sim.S ? 'behind' : 'ahead'); return; }
    const j = f.s & (HIST_N - 1);
    if (st.histAt[j] !== f.s) { snap(st, f, 'nohist'); return; }
    const h = st.hist[j];
    if (h.same(mode, w)) return;
    if (graced(st, f.s)) return;
    st.fixes++;
    const oldLag = p.lag, oldY = p.y;
    h.fromWire(mode, w);
    p.copyFrom(h);
    const S = sim.S;
    for (let t = f.s; t < S; t++) {
      st.hist[t & (HIST_N - 1)].copyFrom(p);
      st.histAt[t & (HIST_N - 1)] = t;
      sim.stepPlayer(me, p, t, t - sim.readySteps);
      p.held = sim.heldAt[me][t & (LOG_N - 1)];
    }
    sim.clearEvents();
    const dl = oldLag - p.lag, dy = oldY - p.y;
    if (Math.abs(dl) > 48 * SUB || Math.abs(dy) > 48 * SUB) { st.vis.lag = 0; st.vis.y = 0; }
    else { st.vis.lag += dl; st.vis.y += dy; }
    if (!(p.out || p.down)) st.ownOutAt = 0;
    if (st.fixLog.length >= 16) st.fixLog.shift();
    st.fixLog.push([Math.round(performance.now()), f.s, S, Math.round(dl / SUB), Math.round(dy / SUB)]);
    if (DEBUG) console.log('[runner] виправлення на кроці', f.s, 'зсув', dl / SUB, dy / SUB);
  }

  /// Свій ввід, що ще летить до сервера: кадр, порахований без нього, звіряти рано.
  function graced(st, fs) {
    const now = performance.now(), lim = st.rtt * 1.5 + 60;
    for (let i = 0; i < st.pend.length; i++) { const q = st.pend[i]; if (q.s < fs && now - q.at < lim) return true; }
    return false;
  }

  /// Стрибок годинника: свій стан — із кадру, годинник — f.s + lead.
  function snap(st, f, why) {
    let sim = st.sim;
    if (f.s < sim.S - 48) { sim = st.sim = makeSim(st); ffWorld(sim, f.s); }
    else if (f.s > sim.S) ffWorld(sim, f.s);
    else sim.S = f.s;
    if (st.me != null && f.p && f.p[st.me]) sim.P[st.me].fromWire(st.mode, f.p[st.me]);
    st.histAt.fill(-1);
    st.vis.lag = st.vis.y = 0;
    st.acc = 0; st.adj = 0; st.eAvg = 0;
    st.snaps++;
    st.snapWhy[why || '?'] = (st.snapWhy[why || '?'] || 0) + 1;
    if (st.snapLog.length >= 16) st.snapLog.shift();
    st.snapLog.push([Math.round(performance.now()), why, f.s, sim.S, +st.eAvg.toFixed(1)]);
    if (DEBUG) console.log('[runner] снап', why, 'кадр', f.s, 'свій', sim.S);
    if (st.running) { const target = f.s + st.lead; while (sim.S < target) stepOnce(st, false); }
  }

  /// Темп сервера в кроках за мс — з кадрів за останні ~3 с. Сервер крокує за стінним годинником (RunnerPacer),
  /// тож це ≈ 0,05; під навантаженням менше — і тоді свій годинник іде так само повільніше, а не тікає вперед.
  function rateSample(st, s, now) {
    const a = st.arr;
    if (a.length && s < a[a.length - 1]) a.length = 0;       // новий раунд: лічильник кроків почався знову
    a.push(now, s);
    while (a.length > 4 && now - a[0] > 3000) a.splice(0, 2);
    if (a.length >= 8 && now - a[0] >= 600) {
      const r = (s - a[1]) / (now - a[0]);
      st.rate = clamp(st.rate * 0.7 + r * 0.3, 0.02, 0.06);
    }
  }

  /// Тримаємо cs ≈ f.s + lead: трохи попереду сервера, щоб свій ввід приходив до нього «вчасно».
  function syncClock(st, f) {
    const e = st.sim.S - f.s - st.lead;
    if (Math.abs(e) >= 10) { snap(st, f, 'clock'); return; }
    st.eAvg = st.eAvg * 0.75 + e * 0.25;
    const now = performance.now();
    if (now < st.adjUntil) return;
    if (st.eAvg > 2.5) { st.adj = -1; st.adjUntil = now + 100; st.eAvg -= 1; }
    else if (st.eAvg < -0.5) { st.adj = 1; st.adjUntil = now + 100; st.eAvg += 1; }
  }

  function advance(st, now) {
    const dt = st.lastT ? now - st.lastT : 0;
    st.lastT = now;
    if (st.me != null && st.ctx.playing && now - st.pingAt > 2000) {
      st.pingAt = now;
      st.ctx.input('ping', { t: Math.floor(now) & 0xFFFFF });
    }
    if (!st.running || !st.sim) return;
    if (dt > 250) { if (st.latest) snap(st, st.latest, 'stall'); return; }
    st.acc += dt;
    const stepMs = 1 / st.rate;
    let n = 0;
    while (st.acc >= stepMs && n < 8) {
      st.acc -= stepMs;
      n++;
      if (st.adj < 0) { st.adj++; continue; }
      stepOnce(st, true);
      if (st.adj > 0) { st.adj--; stepOnce(st, true); }
    }
    if (st.acc > stepMs * 2) st.acc = stepMs;
  }

  // ---------- ввід ----------

  function press(st, bit) {
    Snd.wake();
    if (bit === 1) { if (!(st.held & 1)) { st.held |= 1; st.edge = true; } }
    else st.held |= bit;
    if (st.daily && st.ph === 'wait' && bit === 1) dailyStart(st);
  }

  function release(st, bit) { st.held &= ~bit; }

  /// Забіг дня: перший стрибок і є старт (як у хромівського динозаврика) — годинник рушає в мить натиску.
  function dailyStart(st) {
    const sim = st.sim, ctx = st.ctx;
    if (!sim || st.me == null || !ctx.mine || !ctx.playing || sim.S !== 0) return;
    const k = st.held | 4;
    st.ph = 'run';
    st.running = true;
    sim.input(st.me, 0, k);
    send(st, 0, k);
    st.runLog = [0, k];
    st.ghost = ghostStart(st);
    st.lastHeld = st.held;
    st.edge = false;
    st.acc = 0; st.adj = 0; st.eAvg = 0; st.lastT = performance.now();
    st.goAt = performance.now();
    stepOnce(st, true);
    // Сервер почне крокувати, лише коли отримає цей ввід, і перший його тик одразу дасть два кроки. Щоб годинник
    // не «снапався» вже на першому кадрі, одразу стаємо на своє випередження, як після відліку в партії.
    while (sim.S < st.lead) stepOnce(st, false);
  }

  // ---------- привид найкращої спроби дня (лише Забіг дня, лише на цьому пристрої) ----------
  // Траса дня однакова й детермінована, тож досить пам'ятати свій ввід (крок, клавіші) і прогнати його на власному
  // екземплярі Sim поруч зі своїм: напівпрозорий динозавр найкращої спроби біжить поруч — «ще одну, обжену себе».
  const GHOST_KEY = 'runner.ghost';

  function ghostLoad() {
    try { const g = JSON.parse(localStorage.getItem(GHOST_KEY) || 'null'); return g && Array.isArray(g.log) ? g : null; } catch (_) { return null; }
  }

  function ghostStart(st) {
    const v = st.ctx.view || {}, g = ghostLoad();
    if (!st.daily || !g || g.day !== v.day || !(g.m > 0)) return null;
    return { sim: new Sim(DINO, v.seed || 1, [true], 0, v.pmCap || 1250, false), log: g.log, idx: 0, m: g.m, outAt: 0 };
  }

  /// Привид — до кроку target (ввід із журналу подається на свій крок; той, що в минулому, пропускається).
  function ghostTo(gh, target) {
    const sim = gh.sim, log = gh.log;
    let n = 0;
    while (sim.S < target && n++ < 64) {
      const t = sim.S;
      while (gh.idx < log.length && log[gh.idx] <= t) { if (log[gh.idx] === t) sim.input(0, t, log[gh.idx + 1]); gh.idx += 2; }
      sim.step();
      sim.clearEvents();
    }
  }

  /// Кінець спроби: якщо вона найкраща за день на цьому пристрої — запам'ятати ввід. Перед тим журнал проганяємо
  /// з нуля й звіряємо метри з тими, що порахував сервер: спроба з F5, снапом чи відкинутим вводом привидом не стане.
  function ghostSave(st) {
    const v = st.ctx.view || {}, log = st.runLog, last = v.last;
    st.runLog = null;
    if (!st.daily || !log || !last || !(last.m > 0) || !v.day) return;
    const old = ghostLoad();
    if (old && old.day === v.day && old.m >= last.m) return;
    setTimeout(() => {
      const gh = { sim: new Sim(DINO, v.seed || 1, [true], 0, v.pmCap || 1250, false), log, idx: 0 };
      for (let guard = 0; guard < 40000 && !gh.sim.P[0].out; guard++) ghostTo(gh, gh.sim.S + 1);
      const m = Math.floor(paceOf(gh.sim.R, gh.sim.S) / D.SubPerMetre);
      if (!gh.sim.P[0].out || m !== last.m) { if (DEBUG) console.log('[runner] привид не збігся з сервером', m, last.m); return; }
      try { localStorage.setItem(GHOST_KEY, JSON.stringify({ day: v.day, m, log })); } catch (_) { /* приватне вікно */ }
    }, 30);
  }

  function doThrow(st) {
    const ctx = st.ctx;
    if (st.kind !== 'dino' || st.me == null || !ctx.playing || st.ph !== 'run') return;
    const now = performance.now();
    if (now - st.throwAt < 350) return;
    st.throwAt = now;
    Snd.wake();
    ctx.act('throw').then((r) => { if (r && r.ok) Snd.toss(); }).catch(() => {});
  }

  /// «Ще раз» стрибком, тапом чи Ⓐ. Щойно скінчилось — натиск ще з гри (гравець саме гатив стрибок, коли його
  /// наздогнала лавина): підсумок мусить побути на екрані — соло 0,7 с, партія 1,5 с (там пробіл будь-кого
  /// перезапускає стіл для всіх, а таблицю хочуть роздивитись). У Забігу дня той самий натиск одразу й стартує.
  function rematch(st) {
    const ctx = st.ctx, now = performance.now();
    if (!ctx.mine || ctx.room.status !== 'finished' || now - st.rematchAt < 900) return;
    const v = ctx.view || {};
    const lock = st.daily || (v.n0 | 0) <= 1 ? 700 : 1500;
    if (!st.finAt || now - st.finAt < lock) return;
    st.rematchAt = now;
    if (st.daily) st.goNext = now;
    Promise.resolve(HGames.call('Rematch', ctx.room.id))
      .then((r) => { if (r && r.ok === false && r.message) ctx.toast(r.message, 'err'); })
      .catch(() => {});
  }

  // =============================================================================================
  // 7. Малювання кадру (rAF): шари, перешкоди, чужі (інтерпольовані), свій (передбачений), лавина
  // =============================================================================================

  /// Лінія темпу в дробовий крок t (стан «на початок кроку»): між цілими — лінійно.
  function paceF(R, ready, t) {
    const r = t - ready;
    if (r <= 0) return 0;
    const i = Math.floor(r);
    const a = paceOf(R, i);
    return a + (paceOf(R, i + 1) - a) * (r - i);
  }

  // пули частинок — фіксовані, без алокацій у циклі
  function pools(st) {
    if (st.flakes) return;
    const rnd = Lcg(11);
    st.flakes = [];
    for (let i = 0; i < 48; i++) st.flakes.push({ x: rnd() * 800, y: rnd() * 300, v: 12 + rnd() * 26, r: 1 + rnd() * 1.6, ph: rnd() * 6 });
    st.dust = [];
    for (let i = 0; i < 64; i++) st.dust.push({ on: false, x: 0, y: 0, vx: 0, vy: 0, life: 0, max: 1, r: 2, col: 0 });
  }

  function dustAt(st, where, n, x, y) {
    const p = st.me != null ? st.sim.P[st.me] : null;
    if (where === 'own') {
      if (!p || st.ownSx == null) return;
      x = st.ownSx + 15; y = GROUND - p.y / SUB;
    }
    for (const d of st.dust) {
      if (d.on) continue;
      d.on = true; d.x = x + (Math.random() - 0.5) * 14; d.y = y - Math.random() * 3;
      d.vx = (Math.random() - 0.7) * 60; d.vy = -20 - Math.random() * 50; d.life = 0; d.max = 0.35 + Math.random() * 0.3; d.r = 1.5 + Math.random() * 2; d.col = 0;
      if (--n <= 0) break;
    }
  }

  /// Короткий спалах над героєм місця seat (яйце, перчик, пір'я, «❄ від кого») або в точці (кидок). Повертає спалах.
  function popAt(st, kind, x, y, seat, onSeat) {
    const now = performance.now();
    for (let i = st.pops.length - 1; i >= 0; i--) if (now - st.pops[i].at > POP_MS) st.pops.splice(i, 1);
    if (st.pops.length >= 24) return null;
    const q = { kind, x, y, seat, onSeat: !!onSeat, at: now, by: -1, text: '', tz: 0, tw: 0 };
    st.pops.push(q);
    return q;
  }
  const POP_MS = 1400;

  const ORDER = new Int8Array(SEATS);
  const SX = new Float64Array(SEATS), SY = new Float64Array(SEATS), HEAD = new Float64Array(SEATS);
  // Бирки ніків розкладаємо прямокутниками (x, y, ширина): нова не лягає на вже покладену — піднімається
  // над нею (під стелею Лелек — опускається під птаха). Фіксовані масиви, без алокацій у кадрі.
  const LB_H = 15, LB_GAP = 2, LB_MAX = 10;
  // h — висота бирки в цьому кадрі: на телефоні сцена стиснута до ~0,67, і бирки там у 1,4 раза більші, щоб читались
  const LB = { n: 0, h: LB_H, z: 1, x: new Float64Array(LB_MAX), y: new Float64Array(LB_MAX), w: new Float64Array(LB_MAX) };
  function lbHit(x, y, w) {
    for (let k = 0; k < LB.n; k++)
      if (x < LB.x[k] + LB.w[k] + LB_GAP && x + w + LB_GAP > LB.x[k] && y < LB.y[k] + LB.h + 1 && y + LB.h + 1 > LB.y[k]) return k;
    return -1;
  }
  function lbPut(x, y, w) { if (LB.n < LB_MAX) { LB.x[LB.n] = x; LB.y[LB.n] = y; LB.w[LB.n] = w; LB.n++; } }
  // Чужі — «табун» навколо лінії: хто не спотикався, у того відставання рівно 0, і вісім динозаврів злипались
  // в одного. Тож кожне місце малюємо з невеликим сталим зсувом (лише малюнок, не правила): трохи вбік і
  // трохи «вглиб» кризи (вище); глибші малюються першими. Лелекам — лише вбік: висота в них — сама гра.
  const GHOST_DX = [0, 14, -12, 24, -22, 6, -6, 18], GHOST_DY = [0, 6, 12, 3, 9, 15, 2, 10];

  function draw(st, now) {
    const t0 = performance.now();
    const sim = st.sim;
    if (!sim || !st.scene || !st.spr || !st.cv) return;
    const g = st.cv.ctx, pal = st.pal, W = st.viewW, mode = st.mode, R = sim.R, ready = sim.readySteps;
    const ctx = st.ctx, reduced = reducedMotion();
    const lobby = ctx.room.status === 'lobby';
    pools(st);
    g.save();
    g.scale(st.K, st.K);

    // ---- час: свій — передбачений (дріб між кроками), чужі — з кадрів, трохи позаду ----
    const frac = st.running ? clamp(st.acc * st.rate, 0, 1) : 1;
    const ownT = st.running ? sim.S - 1 + frac : sim.S;
    const newest = st.frN ? st.fr[(st.frHead + st.frN - 1) % FR_N].s : sim.S;
    let othersT = st.running ? ownT - st.lead - 2 : newest;
    if (othersT > newest) othersT = newest;
    const me = st.me, p = me != null ? sim.P[me] : null;
    // Свій герой живе на своєму годиннику (попереду сервера); хто вибув чи дивиться збоку — на годиннику чужих,
    // інакше лавина й камера були б на кілька кроків «у майбутньому» відносно тих, хто ще біжить.
    const ownLive = !!p && p.plays && !((p.out || p.down) && st.ownOutAt && now - st.ownOutAt > 900);
    const wt = ownLive ? ownT : othersT;
    const runT = wt - ready;

    let ownLag = 0, ownY = 0;
    if (p) {
      const j = (sim.S - 1) & (HIST_N - 1);
      const a = st.running && st.histAt[j] === sim.S - 1 ? st.hist[j] : p;
      ownLag = a.lag + (p.lag - a.lag) * frac + st.vis.lag;
      ownY = a.y + (p.y - a.y) * frac + st.vis.y;
      st.vis.lag *= 0.72; st.vis.y *= 0.72;
      if (Math.abs(st.vis.lag) < 4) st.vis.lag = 0;
      if (Math.abs(st.vis.y) < 4) st.vis.y = 0;
    }
    const ownGone = p && (p.out || p.down) && st.ownOutAt && now - st.ownOutAt > 700;

    // ---- камера ----
    const anchor = W >= 800 ? 260 : 180, lo = W >= 800 ? -80 : -60, hi = W >= 800 ? 200 : 120;
    const paceW = paceF(R, ready, wt) / SUB;
    const br = st.frN ? bracket(st, othersT) : null;
    let camPx;
    if (mode === DINO && p && !ownGone && !(p.out && st.ownOutAt)) {
      const lagPx = ownLag / SUB;
      camPx = paceW - lagPx - (anchor - clamp(lagPx, lo, hi));
      st.camFocus = lagPx;
    } else if (mode === DINO && !lobby) {
      // вибув чи глядач: камера — за лідером серед тих, хто ще біжить (плавно, щоб не смикалась на спотиках)
      const f = leaderLag(br, me);
      st.camFocus = st.camFocus == null ? f : st.camFocus + (f - st.camFocus) * 0.08;
      camPx = paceW - st.camFocus - (anchor - clamp(st.camFocus, lo, hi));
    } else camPx = paceW - anchor;
    if (st.wasLive !== ownLive) {
      // свій щойно вибув: перемикаємось на годинник чужих без стрибка сцени — зсув гасне за кілька кадрів
      if (st.wasLive && st.lastCam) st.camBias += st.lastCam - camPx;
      st.wasLive = ownLive;
    }
    st.camBias *= 0.9;
    if (Math.abs(st.camBias) < 0.5) st.camBias = 0;
    camPx += st.camBias;
    const camMove = camPx - st.lastCam;
    st.lastCam = camPx;

    // тремтіння, коли лавина дихає в потилицю
    const D0 = avD(Math.max(0, Math.floor(runT)));
    let shake = 0;
    if (mode === DINO && p && !p.out && !reduced && st.running && runT > 0) {
      const gap = (D0 - ownLag) / SUB;
      if (gap < 120) shake = (1 - gap / 120) * 3;
    }
    if (shake) g.translate((Math.random() - 0.5) * 2 * shake, (Math.random() - 0.5) * 2 * shake);

    // ---- 1–5: небо, паралакс, земля ----
    g.drawImage(st.scene.sky, 0, 0, W, st.vh);
    g.translate(0, st.oy);            // далі — світ: земля на GROUND, над нею в Лелек 290 px неба
    drawBand(g, st.scene.far, camPx, 0.10, W);
    drawBand(g, st.scene.mid, camPx, mode === DINO ? 0.30 : 0.35, W);
    drawBand(g, st.scene.near, camPx, 0.60, W);
    let gx = -(camPx % 256);
    if (gx > 0) gx -= 256;
    for (; gx < W; gx += 256) g.drawImage(st.scene.ground, gx, GROUND, 256, VIEW_H - GROUND);

    // ---- 6: перешкоди ----
    const camSub = camPx * SUB, runI = Math.max(0, Math.floor(runT));
    for (let i = 0; i < sim.obCount; i++) {
      const o = sim.obstacle(i);
      const ox = o.kind === K.Ptero && runT > o.since ? o.x - D.PteroV * (runT - o.since) : o.x;
      const sx = (ox - camSub) / SUB, w = o.w / SUB;
      if (sx > W + 20 || sx + w < -80) continue;
      drawObstacle(st, g, o, sx, runT, runI, pal);
    }
    for (let i = 0; i < sim.snCount; i++) {
      const o = sim.sn[i];
      const sx = (o.x - camSub) / SUB;
      if (sx > W + 20 || sx < -40) continue;
      g.drawImage(st.spr.ob.snow, sx - 2, GROUND - 31, 30, 32);
      const nick = ctx.nickOf(o.by);
      if (nick) { const lb = label(st, o.by, nick); g.globalAlpha = 0.85; g.drawImage(lb, sx + 13 - lb._w * 0.4, GROUND + 5, lb._w * 0.8, 12); g.globalAlpha = 1; }
    }
    // ---- 7: підбирачки (свої взяті — не видно) ----
    if (mode === DINO) {
      for (let i = 0; i < sim.pkCount; i++) {
        const k = sim.pickup(i);
        if (p && p.hasTaken(k.id)) continue;
        const sx = (k.x - camSub) / SUB;
        if (sx > W || sx < -30) continue;
        const bob = reduced ? 0 : Math.sin(now / 260 + k.id) * 2;
        g.drawImage(st.spr.pk[k.kind === K.Egg ? 'egg' : k.kind === K.Pepper ? 'pepper' : 'snowball'], sx, GROUND - k.base / SUB - 24 + bob, 24, 24);
      }
    }

    // ---- 8: чужі — за кадрами, прозорі, з бирками ----
    LB.n = 0;
    LB.z = W < 800 ? 1.4 : 1;
    LB.h = LB_H * LB.z;
    // над своїм — стрілка й «ти»: це місце зайняте, чужі бирки обходять його
    if (p && p.plays && !(p.out || p.down)) {
      const ox = (lobby ? 70 + me * ((W - 140) / 8) : paceW - ownLag / SUB - camPx) + 15;
      const top = mode === DINO ? GROUND - ownY / SUB - 54 : GROUND - ownY / SUB - STORK_BOX.bottom + 8;
      lbPut(ox - 16, top - 26, 32);
    }
    let n = 0;
    const paceO = paceF(R, ready, othersT) / SUB;
    for (let i = 0; i < SEATS; i++) {
      HEAD[i] = -1;
      if (i === me || !br) continue;
      const wa = br.a.p[i], wb = br.b.p[i];
      if (!wa && !wb) continue;
      const A = wa || wb, B = wb || wa, k = br.k;
      const md = (k < 0.5 ? A : B)[mode === DINO ? 3 : 2];
      if (md === 4 && !(now - st.outAt[i] < 650)) continue;
      if (mode === DINO) {
        const lag = A[0] + (B[0] - A[0]) * k;
        SX[i] = lobby ? 70 + i * ((W - 140) / 8) : paceO - lag / SUB - camPx + GHOST_DX[i];
        SY[i] = A[1] + (B[1] - A[1]) * k + (lobby ? 0 : GHOST_DY[i] * SUB);
      } else {
        SX[i] = lobby ? 70 + i * ((W - 140) / 8) : paceO - camPx + GHOST_DX[i] * 1.4;
        SY[i] = A[0] + (B[0] - A[0]) * k;
      }
      ORDER[n++] = i;
    }
    // хто глибше в кризі (Стрибозаври) чи далі позаду — малюється першим
    const depth = (i) => (mode === DINO && !lobby ? GHOST_DY[i] * 1000 : 0) - SX[i];
    for (let a = 1; a < n; a++) { const v = ORDER[a]; let b = a - 1; while (b >= 0 && depth(ORDER[b]) < depth(v)) { ORDER[b + 1] = ORDER[b]; b--; } ORDER[b + 1] = v; }
    for (let q = 0; q < n; q++) {
      const i = ORDER[q];
      const w = br.k < 0.5 ? (br.a.p[i] || br.b.p[i]) : (br.b.p[i] || br.a.p[i]);
      g.globalAlpha = 0.6;
      if (mode === DINO) HEAD[i] = drawDino(st, g, i, SX[i], SY[i], w[3], ((w[10] | 0) >> 12) & 15, now, st.outAt[i], true);
      else HEAD[i] = drawStork(st, g, i, SX[i], SY[i], w[1], w[2], w[3], now, st.outAt[i], false);
      g.globalAlpha = 1;
    }
    for (let q = 0; q < n; q++) {
      const i = ORDER[q], nick = ctx.nickOf(i);
      if (!nick || HEAD[i] < 0 || SX[i] + 40 < 0 || SX[i] - 12 > W) continue;
      const lb = label(st, i, nick), lw = lb._w * LB.z, lx = clamp(SX[i] + 15 - lw / 2, 2, W - lw - 2);
      // угору над головою, поки не знайдеться вільне місце; під самою стелею (Лелеки) — униз, під птаха
      let ly = HEAD[i] - 3 - LB.h, dir = -1, ok = false;
      for (let tries = 0; tries < 12; tries++) {
        if (dir < 0 && ly < 2 - st.oy) { dir = 1; ly = GROUND - SY[i] / SUB + 6; }
        const k = lbHit(lx, ly, lw);
        if (k < 0) { ok = true; break; }
        ly = dir < 0 ? LB.y[k] - LB.h - LB_GAP : LB.y[k] + LB.h + LB_GAP;
      }
      if (!ok || ly > VIEW_H - LB.h) continue;     // бирок більше, ніж місця над табуном, — решту видно в чіпах
      lbPut(lx, ly, lw);
      g.globalAlpha = 0.9;
      g.drawImage(lb, lx, ly, lw, LB.h);
      g.globalAlpha = 1;
    }

    // ---- 8½: привид своєї найкращої спроби дня ----
    if (st.ghost && st.spr.ghost && !lobby) drawGhost(st, g, paceW, camPx, now);

    // ---- 9: свій — непрозорий, кільце під ногами, стрілка над головою ----
    st.ownSx = null;
    if (p && p.plays && !(ownGone && mode === STORKS)) {
      const sx = lobby ? 70 + me * ((W - 140) / 8) : paceW - ownLag / SUB - camPx;
      st.ownSx = sx;
      const feet = GROUND - ownY / SUB;
      if (mode === DINO) {
        const md = p.modeOf(DINO);
        if (md !== 4 || now - st.ownOutAt < 900) {
          const under = lobby ? 0 : sim.groundAt((paceW - ownLag / SUB + 15) * SUB);
          if (under !== NO_GROUND) {
            g.strokeStyle = pal.seats[me]; g.lineWidth = 2; g.globalAlpha = 0.8;
            g.beginPath(); g.ellipse(sx + 15, GROUND - under / SUB + 3, 17, 3.5, 0, 0, 7); g.stroke(); g.globalAlpha = 1;
          }
          if (p.boost > 0 && !reduced) speedLines(g, sx, feet, pal.seats[me], now);
          const head = drawDino(st, g, me, sx, ownY, md, p.grace, now, st.ownOutAt);
          if (md !== 4) arrow(g, sx + 15, head - 6, pal.text, isReady(st, ownT) || lobby);
        }
      } else {
        const md = p.modeOf(STORKS);
        g.fillStyle = 'rgba(0,0,0,.22)'; g.beginPath(); g.ellipse(sx + 16, GROUND + 3, 14, 3, 0, 0, 7); g.fill();
        const head = drawStork(st, g, me, sx, ownY, p.vy, md, p.feather ? 1 : 0, now, st.ownOutAt, true);
        if (md !== 4) {
          if (head - 24 > -st.oy) arrow(g, sx + 16, head - 6, pal.text, isReady(st, ownT) || lobby);
          else {
            // під стелею стрілка «ти» дивиться знизу вгору
            const y = GROUND - ownY / SUB + 6;
            g.fillStyle = pal.text; g.beginPath(); g.moveTo(sx + 11.5, y + 6); g.lineTo(sx + 20.5, y + 6); g.lineTo(sx + 16, y); g.closePath(); g.fill();
          }
        }
      }
    }

    // ---- 10: лавина ----
    if (mode === DINO && !lobby && runT > -1) {
      const avX = (paceF(R, ready, wt) - D0) / SUB - camPx;
      drawAvalanche(st, g, avX, runT, now, reduced);
      // Стіна ще за лівим краєм, а свій уже близько (< 420 px): край сцени береться інеєм, що росте й густішає, —
      // «ой-ой» видно без читання статусу. Коли стіна в кадрі — вона сама все каже.
      if (p && ownLive && !p.out && st.scene.frost && avX < 0) {
        const gap = (D0 - ownLag) / SUB;
        if (gap < 420) {
          const f = clamp(1 - gap / 420, 0, 1);
          g.globalAlpha = clamp(0.25 + f * 0.65 + (reduced ? 0 : Math.sin(now / 140) * 0.05), 0, 1);
          g.drawImage(st.scene.frost, 0, 0, 50 + f * 130, VIEW_H);
          g.globalAlpha = 1;
        }
      }
    }

    // ---- 11: частинки ----
    const dt = Math.min(0.05, st.lastDraw ? (now - st.lastDraw) / 1000 : 0.016);
    st.lastDraw = now;
    if (!reduced) {
      g.fillStyle = mode === DINO ? 'rgba(242, 248, 255, .8)' : 'rgba(255, 236, 200, .35)';
      for (const f of st.flakes) {
        f.y += f.v * dt; f.x -= camMove * (0.3 + f.r * 0.25) + Math.sin(now / 900 + f.ph) * 0.2;
        if (f.y > VIEW_H) { f.y = -4 - st.oy; f.x = Math.random() * W; }
        if (f.x < -4) f.x += W + 8; else if (f.x > W + 4) f.x -= W + 8;
        if (mode === DINO || f.r > 2) g.fillRect(f.x, f.y, f.r, f.r);
      }
    }
    g.fillStyle = pal.snow;
    for (const d of st.dust) {
      if (!d.on) continue;
      d.life += dt;
      if (d.life >= d.max) { d.on = false; continue; }
      d.x += d.vx * dt - camMove; d.y += d.vy * dt; d.vy += 140 * dt;
      g.globalAlpha = 1 - d.life / d.max;
      g.fillRect(d.x, d.y, d.r, d.r);
    }
    g.globalAlpha = 1;
    drawPops(st, g, now, camPx, paceO);

    // ---- 12: накладки ----
    g.translate(0, -st.oy);
    overlays(st, g, now, ownT, othersT, lobby);
    g.restore();

    const ms = performance.now() - t0;
    st.perf.frames++; st.perf.ms += ms;
    if (ms > st.perf.max) st.perf.max = ms;
  }

  const isReady = (st, t) => st.ph === 'ready' && t < st.sim.readySteps;

  function drawGhost(st, g, paceW, camPx, now) {
    const gh = st.ghost, gp = gh.sim.P[0];
    if (gp.out && !gh.outAt) gh.outAt = now;
    if (gh.outAt && now - gh.outAt > 700) return;
    // трохи «вглиб» кризи (вище й лівіше), як чужі в табуні: поки біжите однаково, привид видно з-за свого
    const sx = paceW - gp.lag / SUB - camPx - 8;
    if (sx < -60 || sx > st.viewW + 20) return;
    g.globalAlpha = 0.45;
    const head = drawDino(st, g, 0, sx, gp.y + 9 * SUB, gp.modeOf(DINO), 0, now, gh.outAt, false, st.spr.ghost);
    g.globalAlpha = 1;
    if (gh.outAt) return;
    const lb = label(st, 3, '👻 ' + fmtNum(gh.m) + ' м'), lw = lb._w * LB.z, lx = clamp(sx + 15 - lw / 2, 2, st.viewW - lw - 2), ly = head - 3 - LB.h;
    if (lbHit(lx, ly, lw) >= 0) return;          // над своїм «ти» — не пишемо
    lbPut(lx, ly, lw);
    g.globalAlpha = 0.7;
    g.drawImage(lb, lx, ly, lw, LB.h);
    g.globalAlpha = 1;
  }

  /// Відставання (px) того, хто з живих найменше відстав, — за кадрами чужих; нікого — лінія темпу.
  function leaderLag(br, me) {
    if (!br || !br.a) return 0;
    let best = Infinity;
    for (let i = 0; i < SEATS; i++) {
      if (i === me) continue;
      const A = br.a.p[i] || br.b.p[i], B = br.b.p[i] || br.a.p[i];
      if (!A || A[3] === 4 || B[3] === 4) continue;
      const lag = A[0] + (B[0] - A[0]) * br.k;
      if (lag < best) best = lag;
    }
    return best === Infinity ? 0 : best / SUB;
  }

  function arrow(g, x, y, col, you) {
    g.fillStyle = col;
    g.beginPath(); g.moveTo(x - 4.5, y - 6); g.lineTo(x + 4.5, y - 6); g.lineTo(x, y); g.closePath(); g.fill();
    if (you) {
      g.font = '700 11px system-ui, sans-serif'; g.textAlign = 'center'; g.textBaseline = 'bottom';
      g.fillText('ти', x, y - 7);
    }
  }

  function speedLines(g, sx, feet, col, now) {
    g.strokeStyle = col; g.lineWidth = 2; g.globalAlpha = 0.6;
    g.beginPath();
    for (let k = 0; k < 3; k++) {
      const y = feet - 12 - k * 11, len = 14 + ((now / 40 + k * 7) % 12);
      g.moveTo(sx - 6, y); g.lineTo(sx - 6 - len, y);
    }
    g.stroke(); g.globalAlpha = 1;
  }

  /// Динозавр місця i: ліва межа хітбокса — sx, ноги — y (суб над землею). Повертає y верхівки голови.
  /// grace > 0 — щойно оговтався після спотику й брили його не збивають: блимає, як лелека з пір'ям.
  function drawDino(st, g, i, sx, y, md, grace, now, outAt, ghost, set) {
    const feet = GROUND - y / SUB;
    let pose;
    if (md === 4) pose = 5;
    else if (md === 3) pose = 4;
    else if (md === 1) pose = 2;
    else if (md === 2) pose = 3;
    else pose = ((sx + st.lastCam) / 22) & 1 ? 1 : 0;
    const img = (set || st.spr.dino[i])[pose];
    if (md === 4 && outAt) {
      const k = clamp((now - outAt) / 600, 0, 1);
      g.globalAlpha *= 1 - k * 0.8;
    }
    const a0 = g.globalAlpha;
    if (grace > 0 && md !== 4 && ((now / 80) | 0) % 2 === 0) g.globalAlpha = a0 * 0.4;
    g.drawImage(img, sx - DINO_BOX.left, feet - DINO_BOX.foot, DINO_BOX.w, DINO_BOX.h);
    if (ghost) {
      // чужий: тіло напівпрозоре, а обвідка кольору місця — майже суцільна
      g.globalAlpha = Math.min(1, g.globalAlpha * 1.6);
      g.drawImage(st.spr.ring[i][pose], sx - DINO_BOX.left, feet - DINO_BOX.foot, DINO_BOX.w, DINO_BOX.h);
    }
    g.globalAlpha = a0;
    const head = pose === 3 ? feet - 26 : pose === 5 ? feet - 30 : feet - 54;
    if (md === 3) stars(g, sx + 26, head - 4, now);
    return head;
  }

  function stars(g, x, y, now) {
    g.fillStyle = '#ffe07a';
    for (let k = 0; k < 3; k++) {
      const a = now / 180 + k * 2.1;
      const px = x + Math.cos(a) * 11, py = y + Math.sin(a) * 4;
      g.beginPath(); g.moveTo(px, py - 3); g.lineTo(px + 1, py - 1); g.lineTo(px + 3, py); g.lineTo(px + 1, py + 1); g.lineTo(px, py + 3); g.lineTo(px - 1, py + 1); g.lineTo(px - 3, py); g.lineTo(px - 1, py - 1); g.fill();
    }
  }

  /// Лелека місця i: sx — лівий край хітбокса, y — низ хітбокса (суб). Повертає y верхівки голови.
  function drawStork(st, g, i, sx, y, vy, md, feather, now, outAt, mine) {
    let bottom = GROUND - y / SUB;
    const frame = vy > 40 ? 0 : 1;
    const img = st.spr.stork[i][frame];
    const x = sx - STORK_BOX.left, top = bottom - STORK_BOX.bottom;
    if (md === 4) {
      // вибула: падає з обертанням і зникає
      const k = outAt ? clamp((now - outAt) / 600, 0, 1) : 1;
      g.save();
      g.globalAlpha *= 1 - k;
      g.translate(x + 32, top + 22 + k * 40);
      g.rotate(k * 2.4);
      g.drawImage(img, -32, -22, STORK_BOX.w, STORK_BOX.h);
      g.restore();
      return -1;
    }
    if (md === 3 && ((now / 90) | 0) % 2 === 0) g.globalAlpha *= 0.45;   // невразлива — блимає
    g.drawImage(img, x, top, STORK_BOX.w, STORK_BOX.h);
    if (!mine) {
      const a1 = g.globalAlpha;
      g.globalAlpha = Math.min(1, a1 * 1.6);
      g.drawImage(st.spr.ring[i][frame], x, top, STORK_BOX.w, STORK_BOX.h);
      g.globalAlpha = a1;
    }
    if (feather) {
      g.strokeStyle = st.pal.stork; g.lineWidth = 1.6;
      g.beginPath(); g.moveTo(x + 9, top + 24); g.quadraticCurveTo(x + 2, top + 18, x - 2, top + 22); g.stroke();
    }
    return top + 8;
  }

  function drawObstacle(st, g, o, sx, runT, runI, pal) {
    const ob = st.spr.ob, u = SUB;
    switch (o.kind) {
      case K.Pit: {
        const w = o.w / u;
        const grd = g.createLinearGradient(0, GROUND, 0, VIEW_H);
        grd.addColorStop(0, '#08121c'); grd.addColorStop(1, '#16304a');
        g.fillStyle = grd; g.fillRect(sx, GROUND, w, VIEW_H - GROUND);
        g.fillStyle = pal.ice2; g.fillRect(sx - 2, GROUND, 4, 18); g.fillRect(sx + w - 2, GROUND, 4, 18);
        g.fillStyle = pal.snow; g.fillRect(sx - 4, GROUND - 2, 7, 3); g.fillRect(sx + w - 3, GROUND - 2, 7, 3);
        return;
      }
      case K.Hill: {
        g.fillStyle = pal.ice;
        g.beginPath(); g.moveTo(sx, GROUND + 1); g.lineTo(sx + 160, GROUND - 40); g.lineTo(sx + 260, GROUND - 40); g.lineTo(sx + 420, GROUND + 1); g.closePath(); g.fill();
        g.strokeStyle = 'rgba(255,255,255,.9)'; g.lineWidth = 2;
        g.beginPath(); g.moveTo(sx, GROUND); g.lineTo(sx + 160, GROUND - 40); g.lineTo(sx + 260, GROUND - 40); g.lineTo(sx + 420, GROUND); g.stroke();
        return;
      }
      case K.Low: g.drawImage(ob.low, sx - 1, GROUND - o.base / u - 35, 36, 36); return;
      case K.Low2: g.drawImage(ob.low2, sx - 1, GROUND - 35, 70, 36); return;
      case K.High: g.drawImage(ob.high, sx - 1, GROUND - 69, 36, 70); return;
      case K.Wide: g.drawImage(ob.wide, sx - 1, GROUND - 35, 102, 36); return;
      case K.Icicle: g.drawImage(ob.icicle, sx - 13, GROUND - o.base / u - 212, 56, 214); return;
      case K.Ptero: g.drawImage((runI / 6) & 1 ? ob.ptero1 : ob.ptero0, sx - 6, GROUND - o.base / u - o.h / u - 6, 52, 32); return;
      case K.Chimney: {
        const h = o.h / u, sc = o.w / (40 * u), img = storkSprite(st, K.Chimney, h);
        g.drawImage(img, sx - 4 * sc, GROUND - h - 10, 48 * sc, h + 10);
        return;
      }
      case K.Tree: {
        const h = o.h / u, sc = o.w / (60 * u), img = storkSprite(st, K.Tree, h);
        g.drawImage(img, sx - 2 * sc, GROUND - (o.base + o.h) / u - 3, 64 * sc, h + 7);   // крона впритул до верху неба
        return;
      }
      case K.Pole: {
        const h = o.h / u, img = storkSprite(st, K.Pole, h);
        g.drawImage(img, sx - 2, GROUND - h - 2, 18, h + 2);
        return;
      }
      case K.Wire: {
        const y = GROUND - o.base / u - 5, w = o.w / u;
        g.strokeStyle = pal.wire; g.lineWidth = 1.8;
        g.beginPath(); g.moveTo(sx, y); g.quadraticCurveTo(sx + w * 0.25, y + 4, sx + w * 0.5 - 7, y); g.moveTo(sx + w * 0.5 + 7, y); g.quadraticCurveTo(sx + w * 0.75, y + 4, sx + w, y); g.stroke();
        g.lineWidth = 1; g.beginPath(); g.moveTo(sx, y - 3); g.lineTo(sx, y + 4); g.moveTo(sx + w, y - 3); g.lineTo(sx + w, y + 4); g.stroke();
        return;
      }
      case K.Nest: {
        const img = storkSprite(st, K.Nest, 0);
        g.drawImage(img, sx - 3, GROUND - o.base / u - 30, 56, 34);
        return;
      }
      case K.Kite: {
        const b = obBaseAt(o, runI) / u;
        const cx = sx + 18, cy = GROUND - b - 18;
        g.strokeStyle = 'rgba(40,30,20,.55)'; g.lineWidth = 1;
        g.beginPath(); g.moveTo(cx, cy + 18); g.quadraticCurveTo(cx - 30, cy + 90, cx - 70, GROUND); g.stroke();
        g.fillStyle = pal.accent;
        g.beginPath(); g.moveTo(cx, cy - 18); g.lineTo(cx + 18, cy); g.lineTo(cx, cy + 18); g.lineTo(cx - 18, cy); g.closePath(); g.fill();
        g.fillStyle = pal.danger;
        g.beginPath(); g.moveTo(cx, cy - 18); g.lineTo(cx + 18, cy); g.lineTo(cx, cy); g.closePath(); g.moveTo(cx, cy + 18); g.lineTo(cx - 18, cy); g.lineTo(cx, cy); g.closePath(); g.fill();
        g.strokeStyle = 'rgba(60,30,10,.6)'; g.beginPath(); g.moveTo(cx, cy - 18); g.lineTo(cx, cy + 18); g.moveTo(cx - 18, cy); g.lineTo(cx + 18, cy); g.stroke();
        g.fillStyle = pal.danger;
        for (let k = 1; k <= 3; k++) { const tx = cx - 4 - k * 9, ty = cy + 18 + k * 7 + Math.sin(runT / 6 + k) * 3; g.fillRect(tx - 2.5, ty - 1.5, 5, 3); }
        return;
      }
      default: return;
    }
  }

  function drawAvalanche(st, g, xf, runT, now, reduced) {
    if (xf < -70) return;
    const pal = st.pal;
    g.fillStyle = pal.ava;
    g.fillRect(-20, 0, xf + 20 - 18, VIEW_H);
    const t = reduced ? 0 : runT;
    g.beginPath();
    for (let i = 0; i < 12; i++) {
      const r = 24 + ((i * 37) % 37);
      const cy = 6 + i * 26 + Math.sin(t / 9 + i) * 4;
      const cx = xf - r * 0.55 + Math.sin(t / 7 + i * 1.7) * 7;
      g.moveTo(cx + r, cy); g.arc(cx, cy, r, 0, 7);
    }
    g.fill();
    g.fillStyle = 'rgba(160, 190, 215, .35)';
    g.beginPath();
    for (let i = 0; i < 6; i++) { const cy = 30 + i * 48, cx = xf - 40 + Math.sin(t / 11 + i) * 6; g.moveTo(cx + 18, cy); g.arc(cx, cy, 18, 0, 7); }
    g.fill();
    if (!reduced) {
      // сніговий пил попереду стіни
      g.fillStyle = 'rgba(242, 248, 255, .55)';
      for (let i = 0; i < 26; i++) {
        const k = (i * 53 + Math.floor(now / 30)) % 97 / 97;
        const px = xf + k * 46 + 4, py = VIEW_H - 8 - ((i * 71) % 230) - k * 20;
        g.fillRect(px, py, 2 + (i % 3), 2 + (i % 3));
      }
    }
  }

  /// Де зараз герой місця seat на сцені (px): свій — з передбачення, чужий — з цього ж кадру (SX/SY). null — не видно.
  function seatAt(st, seat) {
    if (seat === st.me) {
      if (st.ownSx == null) return null;
      PT.x = st.ownSx + 15; PT.y = GROUND - st.sim.P[st.me].y / SUB - 58;
      return PT;
    }
    if (seat < 0 || seat >= SEATS || HEAD[seat] < 0 || !Number.isFinite(SX[seat])) return null;
    PT.x = SX[seat] + 15; PT.y = GROUND - SY[seat] / SUB - 58;
    return PT;
  }
  const PT = { x: 0, y: 0 };

  function drawPops(st, g, now, camPx, paceO) {
    if (!st.pops.length) return;
    const W = st.viewW, z = W < 800 ? 1.3 : 1;
    // два проходи: спершу чужі, потім свої — свій напис не мусить ховатися під значками табуна
    for (let pass = 0; pass < 2; pass++) for (const q of st.pops) {
      if ((q.onSeat && q.seat === st.me) !== (pass === 1)) continue;
      const life = q.kind === 'snowhit' ? 1300 : q.kind === 'fly' ? 380 : 700;
      const k = (now - q.at) / life;
      if (k >= 1 || k < 0) continue;
      let x = q.x, y = q.y;
      if (q.kind === 'fly') {
        // сніжка летить від кидальника до місця, де ляже брила (кидальник за кадром — з лівого краю)
        const from = seatAt(st, q.seat);
        const x0 = from ? from.x : -10, y0 = from ? from.y + 20 : GROUND - 60;
        const x1 = q.x - camPx, y1 = q.y;
        const bx = x0 + (x1 - x0) * k, by = y0 + (y1 - y0) * k - Math.sin(k * Math.PI) * 46;
        g.drawImage(st.spr.pk.snowball, bx - 8, by - 8, 16, 16);
        continue;
      }
      if (q.onSeat) {
        const at = seatAt(st, q.seat);
        if (!at) continue;
        x = at.x; y = at.y;
      } else x = q.x - camPx;
      g.globalAlpha = q.kind === 'snowhit' ? Math.min(1, (1 - k) * 3) : 1 - k;
      if (q.kind === 'egg') { g.fillStyle = '#fff4c2'; for (let a = 0; a < 6; a++) { const r = 6 + k * 16; g.fillRect(x + Math.cos(a) * r - 1.5, y + 20 + Math.sin(a) * r - 1.5, 3, 3); } }
      else if (q.kind === 'pepper') {
        // напис — лише над своїм: коли табун бере перчик разом, шість «+25%» злипались у кашу
        if (q.seat === st.me) {
          g.fillStyle = '#ff7a3d'; g.font = '700 ' + Math.round(12 * z) + 'px system-ui, sans-serif'; g.textAlign = 'center'; g.textBaseline = 'alphabetic';
          g.fillText('🌶 +25 % швидкості', x, y + 8 - k * 14);
        } else g.drawImage(st.spr.pk.pepper, x - 7, y + 10 - k * 12, 14, 14);
      }
      else if (q.kind === 'feather') { g.fillStyle = '#ffffff'; for (let a = 0; a < 3; a++) { g.save(); g.translate(x - 10 + a * 10 + k * (a - 1) * 20, y + 40 + k * 26); g.rotate(k * 4 + a); g.fillRect(-4, -1.2, 8, 2.4); g.restore(); } }
      else if (q.kind === 'throw') { g.strokeStyle = '#dff3ff'; g.lineWidth = 2; g.beginPath(); g.arc(x, y, 8 + k * 30, 0, 7); g.stroke(); }
      else if (q.kind === 'snowhit' && q.text) {
        // ціль бачить, хто це зробив, у мить кидка: підпис кольором кидальника; собі — більший і вгорі сцени
        const mine = q.seat === st.me, px = Math.round((mine ? 15 : 11) * z);
        g.font = '800 ' + px + 'px system-ui, sans-serif'; g.textAlign = 'center'; g.textBaseline = 'middle';
        if (q.tz !== px) { q.tz = px; q.tw = g.measureText(q.text).width + 14; }
        const tw = q.tw, th = px + 8;
        const cx = mine ? W / 2 : clamp(x, tw / 2 + 2, W - tw / 2 - 2);
        let cy = mine ? 28 - st.oy + 12 : y - 20;
        if (!mine) {
          // над ціллю, але не поверх бирок табуна: піднімаємось над тією, на яку лягли б
          for (let tries = 0; tries < 8; tries++) {
            const hit = lbHit(cx - tw / 2, cy - th / 2, tw);
            if (hit < 0) break;
            cy = LB.y[hit] - th / 2 - LB_GAP;
          }
          if (cy - th / 2 < -st.oy + 2) cy = -st.oy + 2 + th / 2;
          lbPut(cx - tw / 2, cy - th / 2, tw);
        }
        g.fillStyle = 'rgba(8, 16, 24, .78)'; g.beginPath(); g.roundRect(cx - tw / 2, cy - th / 2, tw, th, th / 2); g.fill();
        g.fillStyle = q.by >= 0 ? st.pal.seats[q.by] : '#dff3ff';
        g.fillText(q.text, cx, cy + 1);
      }
    }
    g.globalAlpha = 1;
  }

  function overlays(st, g, now, ownT, othersT, lobby) {
    const W = st.viewW, pal = st.pal, sim = st.sim, VH = st.vh;
    const t = st.me != null ? ownT : othersT;
    // на телефоні сцена стискається до ~0,57 — написи на ній робимо більшими, щоб читались
    const z = W < 800 ? 1.3 : 1, font = (w, px) => w + ' ' + Math.round(px * z) + 'px system-ui, sans-serif';
    if (st.ph === 'ready' && st.ctx.room.status === 'playing' && t < sim.readySteps) {
      g.fillStyle = pal.shade; g.fillRect(0, 0, W, VH);
      g.fillStyle = pal.text; g.font = font(700, 46); g.textAlign = 'center'; g.textBaseline = 'middle';
      g.fillText(String(Math.max(1, Math.ceil((sim.readySteps - t) / 50))), W / 2, VH / 2 - 10);
      g.font = font(600, 15);
      const v = st.ctx.view || {};
      const rnd = v.rounds > 1 ? 'Раунд ' + v.round + ' з ' + v.rounds : v.n0 === 1 ? 'Тренування' : '';
      if (rnd) g.fillText(rnd, W / 2, VH / 2 + 28 * z);
      return;
    }
    if (st.goAt && now - st.goAt < 450 && st.ph === 'run' && !st.daily) {
      g.fillStyle = pal.text; g.font = font(800, 40); g.textAlign = 'center'; g.textBaseline = 'middle';
      g.globalAlpha = 1 - (now - st.goAt) / 450;
      g.fillText(st.mode === DINO ? 'Біжи!' : 'Лети!', W / 2, VH / 2 - 20);
      g.globalAlpha = 1;
    }
    if (st.daily && st.ph === 'wait' && st.ctx.room.status === 'playing') {
      const bw = Math.min(W - 40, 380 * z), bh = 58 * z;
      g.fillStyle = 'rgba(8, 16, 24, .55)'; g.beginPath(); g.roundRect(W / 2 - bw / 2, 70, bw, bh, 12); g.fill();
      g.fillStyle = pal.text; g.textAlign = 'center'; g.textBaseline = 'middle';
      g.font = font(700, 16); g.fillText(ui.coarse() ? 'Тап — і побігли!' : 'Пробіл, ↑ або тап — і побігли!', W / 2, 70 + bh * 0.34);
      g.font = font(500, 13); g.fillText('Тримай довше — стрибнеш вище', W / 2, 70 + bh * 0.72);
    }
    if (st.ctx.room.status === 'finished' || st.ph === 'over') {
      g.fillStyle = pal.shade; g.fillRect(0, 0, W, VH);
    }
    if (lobby && st.me != null && !st.ctx.playing) {
      const bw = Math.min(W - 40, 340 * z), bh = 34 * z;
      g.fillStyle = 'rgba(8, 16, 24, .5)'; g.beginPath(); g.roundRect(W / 2 - bw / 2, 26, bw, bh, 10); g.fill();
      g.fillStyle = pal.text; g.textAlign = 'center'; g.textBaseline = 'middle'; g.font = font(600, 14);
      g.fillText(st.mode === DINO ? 'Розминаємо лапи — чекаємо на старт' : 'Розправляємо крила — чекаємо на старт', W / 2, 26 + bh / 2);
    }
  }

  // =============================================================================================
  // 8. DOM: HUD, смужка гонки, таблиця раунду/партії, кнопки для пальця, розмір
  // =============================================================================================

  function buildDom(st) {
    const wrap = document.createElement('div');
    wrap.className = 'rnr rnr-' + st.kind;
    const snd = '<button type="button" class="rnr-snd" data-pad-skip title="Звук" aria-label="Звук"></button>';
    wrap.innerHTML = (st.daily
      ? '<div class="rnr-hud rnr-dhud"><div class="rnr-big"><b class="rnr-m">0 м</b><span class="rnr-sub"></span></div>'
        + '<div class="rnr-meta"><span class="rnr-eggs">🥚 0</span>' + snd + '</div></div>'
      : '<div class="rnr-hud"><div class="rnr-chips"></div><div class="rnr-meta"><span class="rnr-round"></span>'
        + '<span class="rnr-m"></span><span class="rnr-clock"></span>' + snd + '</div></div>')
      + (st.mode === DINO ? '<div class="rnr-strip" aria-hidden="true"><span class="rnr-sico">🌨</span><div class="rnr-track">'
        + '<i class="rnr-avabar"></i>' + [0, 1, 2, 3, 4, 5, 6, 7].map((i) => '<b class="rnr-dot s' + i + '" hidden></b>').join('')
        + '</div><span class="rnr-sico">🏁</span></div>' : '')
      + '<div class="rnr-stage"><div class="rnr-over" hidden></div></div>'
      + '<div class="rnr-touch">'
      // лівий великий палець — стрибок (тап по сцені теж стрибає), правий — пригнутись; сніжка — посередині
      + (st.mode === DINO ? '<button type="button" class="rnr-tbtn rnr-jump">⬆<small>Стрибок</small></button>' : '')
      + (st.kind === 'dino' ? '<button type="button" class="rnr-tbtn rnr-throw" hidden>❄<small>Кинути</small></button>' : '')
      + (st.mode === DINO ? '<button type="button" class="rnr-tbtn rnr-duck">⬇<small>Пригнись</small></button>'
        : '<button type="button" class="rnr-tbtn rnr-flap">🪽<small>Змах</small></button>')
      + '</div><div class="rnr-how"></div>';
    st.root.appendChild(wrap);
    const q = (s) => wrap.querySelector(s);
    st.el = {
      wrap, hud: q('.rnr-hud'), chips: q('.rnr-chips'), round: q('.rnr-round'), m: q('.rnr-m'), clock: q('.rnr-clock'),
      sub: q('.rnr-sub'), eggs: q('.rnr-eggs'), snd: q('.rnr-snd'), strip: q('.rnr-strip'), bar: q('.rnr-avabar'),
      dots: [...wrap.querySelectorAll('.rnr-dot')], stage: q('.rnr-stage'), over: q('.rnr-over'),
      touch: q('.rnr-touch'), throwBtn: q('.rnr-throw'), duckBtn: q('.rnr-duck'), flapBtn: q('.rnr-flap'), jumpBtn: q('.rnr-jump'),
      how: q('.rnr-how'),
    };
    st.el.snd.textContent = Snd.on ? '🔊' : '🔈';
    st.el.snd.onclick = () => { Snd.set(!Snd.on); st.el.snd.textContent = Snd.on ? '🔊' : '🔈'; };
    // кнопки для пальця: тримати — pointerdown/up із захопленням
    const hold = (btn, bit) => {
      if (!btn) return;
      btn.addEventListener('pointerdown', (e) => {
        e.preventDefault();
        if (!st.ctx.mine || !st.ctx.playing) return;
        try { btn.setPointerCapture(e.pointerId); } catch (_) { /* старий браузер */ }
        press(st, bit);
      });
      const up = () => release(st, bit);
      btn.addEventListener('pointerup', up);
      btn.addEventListener('pointercancel', up);
      btn.addEventListener('contextmenu', (e) => e.preventDefault());
    };
    hold(st.el.duckBtn, 2);
    hold(st.el.flapBtn, 1);
    hold(st.el.jumpBtn, 1);
    if (st.el.throwBtn) st.el.throwBtn.addEventListener('pointerdown', (e) => { e.preventDefault(); doThrow(st); });
    st.el.over.addEventListener('pointerdown', (e) => {
      if (e.target.closest('a, button')) return;
      if (st.ctx.room.status === 'finished') rematch(st);
    });
  }

  /// Канвас: 800 логічних px завширшки, коли картка ≥ 600 px, інакше 560 (телефон). На DPR 1 і великій
  /// картці малюємо щільніше (K), щоб Full HD не милився.
  function fit(st) {
    const w = st.el.stage.clientWidth || st.root.clientWidth || 800;
    const viewW = w >= 600 ? 800 : 560;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const K = dpr < 2 && w > viewW * 1.12 ? Math.min(2, Math.ceil((w / viewW) * 4) / 4) : 1;
    const sig = viewW + ':' + K + ':' + dpr;
    if (sig === st.sizeSig && st.cv) return;
    st.sizeSig = sig;
    st.viewW = viewW; st.K = K; st.dpr = dpr;
    st.cv = ui.canvas(st.el.stage, { w: viewW * K, h: st.vh * K, cls: 'rnr-cv' });
    st.pal = palette();
    buildSprites(st);
    buildScene(st);
    if (st.flakes) for (const f of st.flakes) f.x = Math.random() * viewW;
    wireCanvas(st);
  }

  function wireCanvas(st) {
    const el = st.cv.el;
    if (el._rnr) { el._rnr.st = st; return; }
    const box = { st };
    el._rnr = box;
    el.addEventListener('pointerdown', (e) => {
      const s = box.st, ctx = s.ctx;
      if (!ctx.mine) return;
      if (ctx.room.status === 'finished') { rematch(s); return; }
      if (!ctx.playing) return;
      e.preventDefault();
      if (e.pointerType === 'mouse' && e.button === 2) { if (s.mode === DINO) { s.rmb = true; press(s, 2); } return; }
      if (e.pointerType === 'mouse' && e.button !== 0) return;
      try { el.setPointerCapture(e.pointerId); } catch (_) { /* старий браузер */ }
      s.ptr = e.pointerId; s.ptrY = e.clientY; s.ptrDuck = false;
      press(s, 1);
    });
    el.addEventListener('pointermove', (e) => {
      const s = box.st;
      if (e.pointerId !== s.ptr || s.mode !== DINO || s.ptrDuck) return;
      if (e.clientY - s.ptrY >= 24) { s.ptrDuck = true; release(s, 1); press(s, 2); }   // свайп униз — пригнутись
    });
    const up = (e) => {
      const s = box.st;
      if (s.rmb && e.pointerType === 'mouse' && e.button === 2) { s.rmb = false; release(s, 2); }
      if (e.pointerId !== s.ptr) return;
      s.ptr = null;
      release(s, 1);
      if (s.ptrDuck) { s.ptrDuck = false; release(s, 2); }
    };
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
    el.addEventListener('contextmenu', (e) => { if (box.st.ctx.mine && box.st.ctx.playing) e.preventDefault(); });
  }

  function chipHtml(st, i, nick, w, own) {
    const mode = st.mode, esc = st.ctx.esc;
    let extra = '';
    let cls = 'rnr-chip s' + i;
    if (own && st.me === i) cls += ' me';
    if (w) {
      if (mode === DINO) {
        const eggs = own && st.me === i ? st.sim.P[i].eggs : w[6];
        const snow = own && st.me === i ? st.sim.P[i].snow : w[7];
        if (eggs) extra += ' 🥚' + eggs;
        if (snow) extra += ' ❄';
        if (w[3] === 3) extra += ' ⌛';
        if (w[3] === 4) { extra += ' 💀'; cls += ' out'; }
      } else {
        if (w[3]) extra += ' 🪶';
        if (w[2] === 4) { extra += ' 💀'; cls += ' out'; }
      }
    }
    return '<span class="' + cls + '" title="' + esc(nick) + '"><i>' + (i + 1) + '</i><span class="rnr-nick">' + esc(nick) + '</span>' + (extra ? '<em>' + extra + '</em>' : '') + '</span>';
  }

  function hud(st) {
    const ctx = st.ctx, v = ctx.view || {}, sim = st.sim, el = st.el;
    if (!sim) return;
    const f = st.latest || { p: v.p || [] };
    const run = Math.max(0, (st.me != null ? sim.S : (f.s || 0)) - sim.readySteps);
    const metres = Math.floor(paceOf(sim.R, run) / D.SubPerMetre);
    if (st.daily) {
      const p = st.me != null ? sim.P[st.me] : null;
      setText(el.m, fmtNum(ctx.room.status === 'finished' && v.last ? v.last.m : metres) + ' м');
      const yest = v.day && kyivToday() && v.day !== kyivToday();
      setText(el.sub, yest ? (ctx.room.status === 'finished' ? 'настав новий день — «Ще раз» дасть нову кризу' : 'це ще вчорашня траса — наступна спроба вже буде сьогоднішня')
        : 'рекорд дня ' + fmtNum(v.best || 0) + ' м · спроба ' + ((v.runs || 0) + (ctx.room.status === 'finished' ? 0 : 1)));
      setText(el.eggs, '🥚 ' + (ctx.room.status === 'finished' && v.last ? v.last.eggs : p ? p.eggs : 0));
    } else {
      let html = '';
      const ps = f.p || [];
      for (let i = 0; i < SEATS; i++) {
        const nick = ctx.nickOf(i);
        if (!nick) continue;
        if (v.plays && !v.plays[i] && ctx.room.status !== 'lobby') continue;
        html += chipHtml(st, i, nick, ps[i], true);
      }
      if (html !== st.hudSig) { st.hudSig = html; el.chips.innerHTML = html; }
      const playing = ctx.room.status !== 'lobby';
      setText(el.round, playing && (v.rounds || 1) > 1 ? 'Раунд ' + (v.round || 1) + '/' + v.rounds : playing && v.n0 === 1 ? 'Тренування' : '');
      if (st.mode === DINO) setText(el.m, playing ? fmtNum(metres) + ' м' : '');
      else setText(el.m, playing ? '⏱ ' + fmtClock(run) : '');
      const left = (v.cap || 6000) - run;
      const showClock = playing && st.ph === 'run' && (st.mode === DINO ? left * STEP_MS <= 30000 : false);
      setText(el.clock, showClock ? '⌛ ' + fmtClock(left) : '');
      el.clock.classList.toggle('hot', showClock && left * STEP_MS <= 15000);
    }
    // кнопки для пальця
    const live = ctx.mine && ctx.playing && isRunPhase(st, st.ph);
    el.stage.classList.toggle('rnr-live', !!(ctx.mine && ctx.playing));
    el.touch.classList.toggle('on', !!(ctx.mine && ctx.playing));
    if (el.throwBtn) {
      const has = live && st.me != null && sim.P[st.me].snow > 0;
      if (el.throwBtn.hidden === has) el.throwBtn.hidden = !has;
    }
    // підказка новачку
    const how = ctx.room.status === 'lobby' || (st.daily && st.ph === 'wait' && ctx.playing);
    setText(el.how, how ? (st.mode === STORKS ? '🪽 змах · 🏠 перелети · 🌳 пронирни · 🪶 один зачеп прощається'
      : st.daily ? '⬆ стрибок (тримай — вище) · ⬇ пригнись · 🌨 лавина не чекає'
        : '⬆ стрибок (тримай — вище) · ⬇ пригнись · ❄ сніжка в лідера · 🌨 лавина за спиною') : '');
    over(st);
  }

  function setText(el, t) { if (el && el.textContent !== t) el.textContent = t; }

  /// Смужка гонки: крапки-голови на 1 − lag/D від лавини до лінії темпу (оновлюється 20 разів на секунду).
  function strip(st, now) {
    const el = st.el;
    if (!el.strip || now - st.stripAt < 50) return;
    st.stripAt = now;
    const sim = st.sim, f = st.latest;
    if (!sim || !f || !f.p) return;
    const run = Math.max(0, sim.S - sim.readySteps);
    const Dv = avD(run), plays = st.ctx.view && st.ctx.view.plays;
    for (let i = 0; i < SEATS; i++) {
      const dot = el.dots[i], w = f.p[i];
      // хто встав з-за столу, того вже нема в «plays» виду, хоч останній кадр його ще пам'ятає
      const show = !!w && w[3] !== 4 && st.ctx.room.status !== 'lobby' && !(plays && !plays[i]);
      if (dot.hidden === show) dot.hidden = !show;
      if (!show) continue;
      const lag = i === st.me ? sim.P[i].lag : w[0];
      const x = (1 - clamp(lag / Dv, 0, 1)) * 100;
      dot.style.left = x.toFixed(1) + '%';
      dot.classList.toggle('me', i === st.me);
    }
  }

  /// «Оля й Петро», «Петро і Оля», «Влад і Юра»: між голосними й після голосної перед приголосною — «й»,
  /// між приголосними й перед і/й/є/ї/ю/я — «і». Імена вже екрановані.
  function andJoin(a) {
    if (a.length <= 1) return a.join('');
    const head = a.slice(0, -1).join(', '), tail = a[a.length - 1];
    const prev = head.slice(-1).toLowerCase(), next = tail.slice(0, 1).toLowerCase();
    return head + (('аеєиіїоуюя'.includes(prev) && !'іїйєюя'.includes(next)) ? ' й ' : ' і ') + tail;
  }

  /// «❄ Снайпер раунду — Тарас: 7 спотиків від 1 сніжки» (сервер дає [місце, влучань, кинуто] лише після раунду).
  /// Одна брила може збити пів табуна: вона лягає перед лідером, а решта біжить слідом.
  function sniperHtml(ctx, sn, what) {
    if (!Array.isArray(sn) || sn.length < 3) return '';
    const nick = ctx.esc(ctx.nickOf(sn[0]) || ctx.seatName(sn[0]));
    const hits = sn[1] + ' ' + plural(sn[1], 'спотик', 'спотики', 'спотиків');
    const balls = sn[2] + ' ' + (sn[2] % 10 === 1 && sn[2] % 100 !== 11 ? 'сніжки' : 'сніжок');
    return '<p class="rnr-ovsnow">❄ Снайпер ' + what + ' — <b class="s' + sn[0] + '">' + nick + '</b>: ' + hits + ' від ' + balls + '</p>';
  }

  function plural(n, one, few, many) {
    const t = n % 100, o = n % 10;
    if (t > 10 && t < 20) return many;
    return o === 1 ? one : o >= 2 && o <= 4 ? few : many;
  }

  /// Таблиця раунду (over) і партії (done) поверх канвасу; для Забігу дня — підсумок спроби й «сьогодні».
  /// На вузькому екрані (телефон) CSS ставить її під сцену: у сцені 170 px заввишки вісім рядків не вміщались.
  function over(st) {
    const ctx = st.ctx, v = ctx.view || {}, el = st.el;
    const done = ctx.room.status === 'finished';
    const show = done || st.ph === 'over';
    const sig = show ? [ctx.room.status, st.ph, v.round, v.s, JSON.stringify(v.result || v.last || null), JSON.stringify(v.sniper || null), st.board ? st.boardAt : 0].join('|') : '';
    if (sig === st.overSig) return;
    st.overSig = sig;
    el.wrap.classList.toggle('rnr-showover', show);
    if (!show) { el.over.hidden = true; el.over.innerHTML = ''; return; }
    const esc = ctx.esc, nick = (i) => esc(ctx.nickOf(i) || ctx.seatName(i));
    const again = '<p class="rnr-ovhint">' + (device() === 'pad' ? 'Ⓐ — ще раз' : ui.coarse() ? 'Тап — ще раз' : 'Пробіл або Enter — ще раз') + '</p>';
    let html = '';
    if (st.daily) {
      const last = v.last || { m: 0, eggs: 0, record: false };
      html = '<div class="rnr-ovbox"><h3>' + (last.record && last.m > 0 ? '🏆 Новий рекорд дня: ' + fmtNum(last.m) + ' м' : fmtNum(last.m) + ' м · 🥚 ' + last.eggs) + '</h3>'
        + '<p class="rnr-ovsub">Рекорд дня ' + fmtNum(v.best || 0) + ' м · спроб сьогодні ' + (v.runs || 0) + '</p>'
        + boardHtml(st) + again + '</div>';
      loadBoard(st);
    } else if (done && v.n0 === 1) {
      const run = Math.max(0, (v.s || 0) - (v.readySteps || 0));
      html = '<div class="rnr-ovbox"><h3>' + (st.mode === DINO ? 'Тренування: ' + fmtNum(v.m || 0) + ' м' : 'Тренування: у небі ' + fmtClock(run)) + '</h3>'
        + '<p class="rnr-ovsub">' + (st.mode === DINO ? 'Для таблиці рекордів є «Забіг дня» в Соло. ' : '') + 'Клич друзів — удвох веселіше.</p>' + again + '</div>';
    } else if (done && v.result && v.result.table && v.result.table.length) {
      const t = v.result.table, wn = v.result.winners || [];
      const top = t[0].points;
      const head = v.result.draw || !wn.length
        ? '🤝 Нічия — усі по ' + top + ' ' + points(top)
        : '🏆 ' + andJoin(wn.map(nick)) + ' — ' + (wn.length > 1 ? 'по ' : '') + top + ' ' + points(top);
      // рівні очки — рівне місце: «1, 1, 3», а не «1, 2, 3»
      const rank = (r) => 1 + t.filter((x) => x.points > r.points).length;
      html = '<div class="rnr-ovbox"><h3>' + head + '</h3>'
        + '<table><tr><th>#</th><th>хто</th><th>очки</th>' + (st.mode === DINO ? '<th title="з них за яйця">з них 🥚</th>' : '') + '</tr>'
        + t.map((r) => '<tr class="s' + r.seat + (r.seat === ctx.seat ? ' me' : '') + '"><td>' + rank(r) + '</td><td><i></i>' + nick(r.seat) + '</td><td>' + r.points + '</td>'
          + (st.mode === DINO ? '<td>' + r.eggs + '</td>' : '') + '</tr>').join('')
        + '</table>' + sniperHtml(ctx, v.sniperParty, 'партії') + (ctx.mine ? again : '') + '</div>';
    } else if (done) {
      html = '<div class="rnr-ovbox"><h3>' + esc((ctx.room.result && ctx.room.result.text) || 'Партію зіграно') + '</h3>' + (ctx.mine ? again : '') + '</div>';
    } else {
      const rp = (v.roundPoints && v.roundPoints[v.roundPoints.length - 1]) || [];
      const rows = [];
      for (let i = 0; i < SEATS; i++) if (v.plays && v.plays[i]) rows.push(i);
      rows.sort((a, b) => (v.place[a] || 99) - (v.place[b] || 99) || a - b);
      // «раунд» раніше вже містив яйця, а поруч стояла ще колонка 🥚 — здавалось, що яйця лічаться двічі.
      // Тепер окремо: за місце, за яйця, разом за партію.
      const eggs = (i) => (st.mode === DINO && v.eggs && v.eggs[i]) || 0;
      html = '<div class="rnr-ovbox"><h3>Раунд ' + (v.round || 1) + (v.rounds > 1 ? ' з ' + v.rounds : '') + '</h3>'
        + '<table><tr><th>#</th><th>хто</th><th>за місце</th>' + (st.mode === DINO ? '<th>🥚</th>' : '') + '<th>разом</th></tr>'
        + rows.map((i) => '<tr class="s' + i + (i === ctx.seat ? ' me' : '') + '"><td>' + (v.place[i] || '—') + '</td><td><i></i>' + nick(i) + '</td><td>+' + Math.max(0, (rp[i] || 0) - eggs(i)) + '</td>'
          + (st.mode === DINO ? '<td>+' + eggs(i) + '</td>' : '') + '<td>' + ((v.points && v.points[i]) || 0) + '</td></tr>').join('')
        + '</table>' + sniperHtml(ctx, v.sniper, 'раунду') + (v.round < v.rounds ? '<p class="rnr-ovhint">Наступний раунд за мить…</p>' : '') + '</div>';
    }
    el.over.innerHTML = html;
    el.over.hidden = false;
  }

  function points(n) {
    const t = n % 100, o = n % 10;
    if (t > 10 && t < 20) return 'очок';
    if (o === 1) return 'очко';
    if (o >= 2 && o <= 4) return 'очки';
    return 'очок';
  }

  function boardHtml(st) {
    if (!st.board) return '<p class="rnr-ovsub">Таблиця «сьогодні» вантажиться…</p>';
    if (!st.board.length) return '<p class="rnr-ovsub">Сьогодні ти перший на цій кризі.</p>';
    const me = String((st.ctx.me && st.ctx.me.nick) || '').toLowerCase(), esc = st.ctx.esc;
    return '<table class="rnr-board"><tr><th>#</th><th>сьогодні</th><th>м</th><th>спроб</th></tr>'
      + st.board.slice(0, 10).map((r, k) => '<tr' + (String(r.nick || '').toLowerCase() === me ? ' class="me"' : '') + '><td>' + (k + 1) + '</td><td>'
        + esc(r.nick || '') + '</td><td>' + fmtNum(r.best || 0) + '</td><td>' + (r.tries || 0) + '</td></tr>').join('') + '</table>';
  }

  /// Таблиця «сьогодні»: не частіше ніж раз на 10 с, але нова спроба (runs змінився) — привід перечитати одразу,
  /// інакше щойно поставлений рекорд не видно. Рядок у базу пише каркас уже після Finish — тож із запасом 0,7 с.
  function loadBoard(st) {
    const now = Date.now(), runs = (st.ctx.view && st.ctx.view.runs) || 0;
    const fresh = runs !== st.boardRuns;
    if (st.boardBusy || (!fresh && now - st.boardAt < 10000)) return;
    st.boardBusy = true;
    st.boardRuns = runs;
    const nick = (st.ctx.me && st.ctx.me.nick) || '';
    const go = () => fetch('/api/games/leaderboard?game=dino-daily&period=day', { headers: { 'X-Nick': encodeURIComponent(nick) } })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => { st.board = d && Array.isArray(d.rows) ? d.rows : []; st.boardAt = Date.now(); st.overSig = null; })
      .catch(() => { st.board = st.board || []; })
      .finally(() => { st.boardBusy = false; });
    if (fresh) setTimeout(go, 700); else go();
  }

  // =============================================================================================
  // 9. Цикл, модуль, статус, три HGames.register
  // =============================================================================================

  function loop(st) {
    if (st.raf) return;
    const tick = (now) => {
      st.raf = 0;
      if (!st.cv || !st.cv.el.isConnected) return;
      advance(st, now);
      if (!document.hidden && st.cv.el.offsetParent) {
        draw(st, now);
        strip(st, now);
      }
      if (now - st.hudAt > 100) { st.hudAt = now; hud(st); rumble(st); }
      st.raf = requestAnimationFrame(tick);
    };
    st.raf = requestAnimationFrame(tick);
  }

  function rumble(st) {
    const sim = st.sim;
    if (st.mode !== DINO || st.me == null || !sim || !st.running || document.hidden) { if (Snd.rumble) Snd.rumbleTo(0); return; }
    const p = sim.P[st.me];
    const gap = (avD(Math.max(0, sim.S - sim.readySteps)) - p.lag) / SUB;
    Snd.rumbleTo(p.out || sim.S < sim.readySteps ? 0 : clamp((240 - gap) / 240, 0, 1));
  }

  function listen(st, target, type, fn, opt) {
    target.addEventListener(type, fn, opt);
    st.listeners.push([target, type, fn, opt]);
  }

  function mountCard(root, ctx, kind) {
    const st = makeState(root, ctx, kind);
    root._rnr = st;
    ctx._rnr = st;
    buildDom(st);
    fit(st);
    listen(st, document, 'keyup', (e) => {
      const k = keyKind(e);
      if (k === 'jump') release(st, 1);
      else if (k === 'down') release(st, 2);
    });
    const letGo = () => { st.held = 0; st.ptr = null; st.rmb = false; };
    listen(st, window, 'blur', letGo);
    listen(st, document, 'visibilitychange', () => { if (document.hidden) { letGo(); Snd.rumbleTo(0); } });
    if (window.ResizeObserver) { st.ro = new ResizeObserver(() => fit(st)); st.ro.observe(st.el.stage); }
    else listen(st, window, 'resize', () => fit(st));
    // Відладка (стан, заміри, найближчі перешкоди для ботів-стендів) — лише з ?rnrdebug=1: на проді готовий
    // перелік перешкод у консолі був би подарунком автострибу для Забігу дня.
    if (DEBUG) window.__rnr = debugApi(st);
    loop(st);
  }

  function updateCard(root, ctx) {
    const st = root._rnr;
    if (!st) return;
    st.ctx = ctx;
    ctx._rnr = st;
    const v = ctx.view;
    if (!v) return;
    if (ctx.room.status !== 'finished') st.finAt = 0;
    else if (!st.finAt) { st.finAt = performance.now(); ghostSave(st); }
    if (keyOf(ctx, v) !== st.key) newRound(st, v);
    else {
      if (v.p && v.s != null && !st.running) pushFrame(st, v.s, v.p);
      if (v.snow) for (const w of v.snow) knowSnow(st, w, false);
      if (st.daily && v.ph !== st.ph && !(st.ph === 'run' && v.ph === 'wait')) {
        if (v.ph === 'done') phaseTo(st, 'done', { s: v.s, p: v.p, ph: 'done' });
        else if (v.ph === 'run' && !st.running) { st.ph = 'wait'; phaseTo(st, 'run', { s: v.s, p: v.p, ph: 'run' }); }
      }
      if (!st.daily && (v.ph === 'over' || v.ph === 'done') && st.ph !== v.ph && st.ph !== 'done') phaseTo(st, v.ph, { s: v.s, p: v.p, ph: v.ph });
      if (!ctx.playing && st.running) st.running = false;
    }
    if (ctx.playing && st.me != null && st.ph === 'wait') st.running = false;
    fit(st);
    hud(st);
    loop(st);
  }

  function frameCard(root, ctx, f) {
    const st = root._rnr;
    if (!st) return;
    st.ctx = ctx;
    applyFrame(st, f);
  }

  function unmountCard(root) {
    const st = root._rnr;
    if (!st) return;
    cancelAnimationFrame(st.raf);
    st.raf = 0;
    for (const [t, type, fn, opt] of st.listeners) t.removeEventListener(type, fn, opt);
    if (st.ro) st.ro.disconnect();
    Snd.rumbleTo(0);
    if (window.__rnr && window.__rnr.st === st) window.__rnr = null;
    root._rnr = null;
  }

  function onKey(e, ctx) {
    const st = ctx._rnr;
    if (!st) return false;
    const kk = keyKind(e);
    if (!kk) return false;
    if (ctx.room.status === 'finished') {
      if (ctx.mine && (e.code === 'Space' || e.key === ' ' || kk === 'enter')) { if (!e.repeat) rematch(st); return true; }
      return false;
    }
    if (!ctx.mine || !ctx.playing || kk === 'enter') return false;
    if (e.repeat) return true;
    if (kk === 'jump') press(st, 1);
    else if (kk === 'down') { if (st.mode === DINO) press(st, 2); }
    else if (kk === 'throw') { if (st.kind !== 'dino') return false; doThrow(st); }
    return true;
  }

  function place(n) { return n === 1 ? '🥇' : n === 2 ? '🥈' : n === 3 ? '🥉' : n + '.'; }

  /// Чим людина грає зараз: пад (шар пада вмикає body.pad-on), палець чи клавіатура — підказки кажуть її мовою.
  const device = () => (document.body.classList.contains('pad-on') ? 'pad' : ui.coarse() ? 'touch' : 'kbd');

  /// Найближча перешкода «на рівні голови» попереду свого динозавра (≤ 300 px), поки він на землі: тоді й лише тоді
  /// підказка «пригнись» має сенс — постійне «↓ пригнутись» новачок читав як наказ «пригнись зараз».
  function duckAhead(st) {
    const sim = st.sim;
    if (!sim || st.me == null || st.mode !== DINO) return '';
    const p = sim.P[st.me];
    if (p.out || p.air || p.duck) return '';
    const run = Math.max(0, sim.S - sim.readySteps), x = paceOf(sim.R, run) - p.lag + D.HitW;
    let best = Infinity, kind = 0;
    for (let i = 0; i < sim.obCount; i++) {
      const o = sim.obstacle(i);
      if (o.kind !== K.Icicle && o.kind !== K.Ptero) continue;
      const dx = obXAt(o, run) - x;
      if (dx < -o.w || dx > 300 * SUB || dx >= best) continue;
      best = dx; kind = o.kind;
    }
    if (!kind) return '';
    const hint = device() === 'touch' ? '⬇' : '↓';
    return hint + ' ' + (kind === K.Icicle ? 'бурулька' : 'птеродактиль') + ' — пригнись!';
  }

  function status(ctx) {
    const st = ctx._rnr, v = ctx.view || {}, room = ctx.room;
    if (!st) return '';
    const dev = device();
    if (room.status === 'lobby') {
      const seated = room.seats.filter((x) => x.nick).length;
      const host = String(room.host || ''), iHost = host.toLowerCase() === String(ctx.me.nick).toLowerCase();
      if (ctx.mine && seated === 1 && iHost) return 'Можна почати самому — це тренування; клич друзів у балачках';
      if (iHost && seated >= 2) return seated >= room.seats.length ? 'Усі на місці — тисни «Почати»' : 'За столом ' + seated + ' — можна тиснути «Почати»';
      if (host && seated >= 1) return 'Чекаємо, поки ' + host + ' почне';
      return '';
    }
    if (room.status === 'finished') {
      if (st.daily) {
        const m = v.last ? v.last.m : 0;
        return fmtNum(m) + ' м · рекорд дня ' + fmtNum(v.best || 0) + ' м · спроба ' + (v.runs || 0);
      }
      if (v.n0 === 1) return st.mode === DINO ? 'Тренування: ' + fmtNum(v.m || 0) + ' м' : 'Тренування: у небі ' + fmtClock(Math.max(0, (v.s || 0) - (v.readySteps || 0)));
      return '';
    }
    const ph = st.ph || v.ph;
    const slow = st.slow >= 3 ? (st.mode === DINO ? ' · зв\'язок повільний, стрибки можуть запізнюватись' : ' · зв\'язок повільний') : '';
    const p = st.sim && st.me != null ? st.sim.P[st.me] : null;
    const gapM = () => Math.max(0, Math.floor((avD(Math.max(0, st.sim.S - st.sim.readySteps)) - p.lag) / D.SubPerMetre));
    if (st.daily) {
      if (ph === 'wait') {
        return (dev === 'pad' ? 'Ⓐ' : dev === 'touch' ? 'Тап' : 'Пробіл, ↑ або тап') + ' — стрибок і старт. Тримай довше — вище';
      }
      if (!p) return '';
      return (duckAhead(st) || '🌨 лавина за ' + fmtNum(gapM()) + ' м') + slow;
    }
    if (ph === 'ready') {
      if (!ctx.mine) return 'Зараз почнуть…';
      const jump = dev === 'pad' ? 'Ⓐ' : dev === 'touch' ? 'тап' : 'пробіл';
      return 'Готуйсь… ' + (st.mode === DINO ? jump + ' — стрибок, ' + (dev === 'touch' ? '⬇' : '↓') + ' — пригнутись' : jump + ' — змах');
    }
    if (ph === 'over') {
      const rows = [];
      for (let i = 0; i < SEATS; i++) if (v.plays && v.plays[i] && v.place && v.place[i]) rows.push(i);
      rows.sort((a, b) => v.place[a] - v.place[b] || a - b);
      return 'Раунд ' + (v.round || 1) + ': ' + rows.map((i) => place(v.place[i]) + ' ' + (ctx.nickOf(i) || ctx.seatName(i))).join(' · ');
    }
    // хто ще в грі — з останнього кадру (глядачу й тому, хто вибув)
    const f = st.latest || { p: v.p || [] }, fp = f.p || [], mi = st.mode === DINO ? 3 : 2;
    let alive = 0, n = 0, lead = -1;
    for (let i = 0; i < SEATS; i++) {
      const w = fp[i];
      if (!w || (v.plays && !v.plays[i])) continue;
      n++;
      if (w[mi] === 4) continue;
      alive++;
      if (st.mode === DINO && (lead < 0 || w[0] < fp[lead][0])) lead = i;
    }
    // сам на сам (тренування) рахувати нема кого
    const left = n <= 1 ? '' : st.mode === DINO ? 'бігуть ' + alive + ' з ' + n : 'у небі ' + alive + ' з ' + n;
    const leader = lead >= 0 && alive > 1 ? 'попереду ' + (ctx.nickOf(lead) || ctx.seatName(lead)) + ' · ' : '';
    // Глядач: каркас уже пише «Дивлюсь збоку» — тут корисніше, хто лідирує і скільки ще в грі.
    if (!ctx.mine || !p) return leader + left;
    if (st.mode === DINO) {
      if (p.out) return 'Лавина тебе забрала' + (left ? ' — ' + leader + left : '');
      const snow = p.snow ? ' · ' + (dev === 'pad' ? 'Ⓧ' : dev === 'touch' ? '❄' : 'X') + ' — сніжка в лідера' : '';
      return (duckAhead(st) || '🌨 лавина за ' + fmtNum(gapM()) + ' м') + snow + slow;
    }
    if (p.out || p.down) return 'Ти на землі' + (left ? ' — ' + left : '');
    return (p.feather ? '🪶 запасне пір\'я є' : 'пір\'я вже нема — обережно') + (left ? ' · ' + left : '') + slow;
  }

  /// Для headless-перевірок і ботів: швидкодія малювання, найближча перешкода, стан годинника.
  function debugApi(st) {
    return {
      st,
      get perf() { return st.perf; },
      resetPerf() { st.perf = { frames: 0, ms: 0, max: 0 }; },
      net() { return { lead: st.lead, rtt: st.rtt, rate: +st.rate.toFixed(4), sent: st.sent, fixes: st.fixes, snaps: st.snaps, why: st.snapWhy, e: st.eAvg, cs: st.sim && st.sim.S, fs: st.latest && st.latest.s, ph: st.ph, running: st.running }; },
      log() { return { now: Math.round(performance.now()), snaps: st.snapLog, fixes: st.fixLog }; },
      /// Що попереду свого героя: перешкоди в px від переднього краю хітбокса.
      next() {
        const sim = st.sim;
        if (!sim || st.me == null) return null;
        const p = sim.P[st.me], run = Math.max(0, sim.S - sim.readySteps);
        const x = paceOf(sim.R, run) - p.lag;
        const out = [];
        const add = (o, ox, base) => {
          const dx = (ox - x) / SUB;
          if (dx + o.w / SUB < -4 || dx > 700) return;
          out.push({ dx: Math.round(dx), w: o.w / SUB, base: base / SUB, h: o.h / SUB, kind: o.kind, id: o.id });
        };
        for (let i = 0; i < sim.obCount; i++) {
          const o = sim.obstacle(i);
          add(o, obXAt(o, run), st.mode === STORKS ? obBaseAt(o, run) : o.base);
        }
        for (let i = 0; i < sim.snCount; i++) add(sim.sn[i], sim.sn[i].x, 0);
        out.sort((a, b) => a.dx - b.dx);
        return { y: p.y / SUB, vy: p.vy, air: p.air, out: p.out || p.down, stun: p.stun, lag: p.lag / SUB, speed: sim.speed(run) / SUB, run, ph: st.ph, obs: out.slice(0, 6) };
      },
    };
  }

  /// Джойстик (PROTOCOL §3). Посеред партії — стрибок/змах на будь-якій кнопці під пальцем. Після кінця партії
  /// Ⓐ — «Ще раз» (раніше Ⓐ тиснула те, на чому з самого початку стояло кільце навігації, — хоч «Ефір» у шапці),
  /// а решта кнопок — як на всьому сайті: Ⓧ балачки, Ⓨ підказки, Ⓑ вийти.
  function padFor(kind) {
    let last = null;
    const done = () => !!(last && last.room && last.room.status === 'finished');
    const play = kind === 'storks' ? '{a} змах крил (будь-яка кнопка) · {dpad} ↑ теж змах'
      : kind === 'daily' ? '{a} стрибок (тримай — вище) · {dpad} ↓ пригнутись'
        : '{a} стрибок (тримай — вище) · {dpad} ↓ пригнутись · {x} сніжка';
    return {
      dirs: 'y', a: 'Space',
      get anyBtn() { return !done(); },
      get hint() { return done() ? '{a} ще раз' : play; },
      when(ctx) { last = ctx; return !!(ctx && ctx.mine && (ctx.playing || (ctx.room && ctx.room.status === 'finished'))); },
      on(btn, ctx) {
        if (kind !== 'dino' || btn !== 'x' || !ctx || !ctx._rnr || !ctx.playing) return false;
        doThrow(ctx._rnr);
        return true;
      },
    };
  }

  function makeModule(kind) {
    return {
      mount(root, ctx) { mountCard(root, ctx, kind); },
      update: updateCard,
      frame: frameCard,
      unmount: unmountCard,
      onKey,
      status,
    };
  }

  const DINO_ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M9 2h5v4h-2v2h-1v3l2 2v1H9l-1-2H6l-1 2H3l2-3V7c0-2 1-3 3-3h1z" fill="var(--ok)"/>'
    + '<rect x="1" y="12" width="4" height="3" rx="1" fill="var(--rnr-ice, #b8e6ff)"/>'
    + '<circle cx="12" cy="4" r="0.9" fill="var(--bg2)"/></svg>';

  HGames.register(Object.assign(makeModule('dino'), {
    id: 'dino',
    icon: DINO_ICON,
    seatNames: DINO_NAMES,
    seatClass: SEAT_CLASS,
    pad: padFor('dino'),
    news: {
      v: '2026-09-27', title: 'Нова гра: Стрибозаври',
      items: [
        '🦖 Усі біжать по одній кризі від лавини: свій динозавр яскравий, чужі прозорі',
        '⬆️ Пробіл, ↑ або тап — стрибок; тримай довше — стрибнеш вище',
        '⬇️ Стрілка вниз — пригнутись під бурулькою чи птеродактилем (у повітрі — швидко вниз)',
        '❄ Підібрав сніжку — тисни X: брила ляже під ноги тому, хто попереду',
        '🏁 Спіткнувся — сповільнився, лавина ближче. Останній на ногах бере раунд, партія з трьох',
      ],
    },
  }));

  HGames.register(Object.assign(makeModule('daily'), {
    id: 'dino-daily',
    icon: DINO_ICON.replace('</svg>', '<rect x="10" y="10" width="5" height="5" rx="1" fill="var(--clay)"/><path d="M11 12h3" stroke="#fff" stroke-width="1"/></svg>'),
    seatNames: ['стрибозавр'],
    seatClass: ['o'],
    pad: padFor('daily'),
    news: {
      v: '2026-09-27', title: 'Нова гра: Забіг дня',
      items: [
        '📅 Одна траса на день для всіх — порівняй метри з друзями в Таблиці',
        '⬆️ Пробіл, ↑ або тап — стрибок (тримай — вище), ↓ — пригнутись чи швидко вниз',
        '🌨 Лавина не дрімає: кожен спотик — вона ближче. Дожене — забіг скінчився',
        '🔁 Спроб скільки завгодно, у таблицю йде найкраща за день; 500 м — черепки дня',
        '👻 Поруч біжить привид твоєї найкращої спроби сьогодні — обжени себе',
      ],
    },
  }));

  HGames.register(Object.assign(makeModule('storks'), {
    id: 'storks',
    icon: '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
      + '<path d="M2 9c3-3 6-4 9-3l3-2-1 3c-1 3-4 5-8 5H3l2-2z" fill="var(--text)"/>'
      + '<path d="M11 6l4-1" stroke="var(--clay)" stroke-width="1.4" stroke-linecap="round"/>'
      + '<path d="M6 12l-1 3M8 12l-1 3" stroke="var(--clay)" stroke-width="1.2" stroke-linecap="round"/>'
      + '<circle cx="11.5" cy="5.5" r="0.8" fill="var(--bg2)"/></svg>',
    seatNames: STORK_NAMES,
    seatClass: SEAT_CLASS,
    pad: padFor('storks'),
    news: {
      v: '2026-09-27', title: 'Нова гра: Лелеки',
      items: [
        '🪽 Тап, пробіл або Ⓐ — змах крил; не змахнув — падаєш',
        '🏠 Комини, стовпи з дротами, гнізда й повітряні змії — однакові для всіх, летите поруч',
        '🪶 Одне запасне пір\'я на раунд: перший зачеп прощається, другий — на землю',
        '🏁 Хто останній у небі — бере раунд; партія з трьох, очки за місця',
      ],
    },
  }));
})();
