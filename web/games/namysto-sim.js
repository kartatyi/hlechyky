/*
  «Намисто»: детермінована симуляція разка — слово в слово те саме, що src/Hlechyky/Games/Impl/NamystoCore.cs.
  Браузер крутить її, щоб усе рухалось миттєво, сервер — щоб судити: той самий журнал вводу дає ті самі очки до біта.
  Лише цілі числа: ділення — Math.trunc, корінь — isqrt, випадковість — mulberry32 через Math.imul, жодної
  тригонометрії. Будь-яка правка тут — дослівно й у C#, інакше впаде паритет (docs/games/dev/namysto-parity.js).

  Одиниці: субпікселі (su), 1 px = 16 su; поле 720×720 px; крок = 1/60 с. Разок — від голови (найближча до криниці)
  до хвоста. Без DOM: window.NamystoSim.
*/
(function (root) {
  'use strict';

  const Px = 16, Field = 720 * Px, Dm = 32 * Px, StepsPerSecond = 60;
  const PushMax = 6 * Px, Magnet = 5 * Px, BackSpeed = 2 * Px, IntroSpeed = 4 * Px, IntroPct = 26, IntroMaxSteps = 900;
  const ShotSub = 6 * Px, SubsNormal = 2, SubsEye = 3, Mouth = 40 * Px, Cooldown = 9, MaxShots = 5, MinAim = 8 * Px;
  const OutMargin = Dm, RunPct = 32;
  const BonusFirst = 420, BonusEvery = 720, BonusJitter = 360, BonusLife = 900;
  const SlowSteps = 360, BackSteps = 120, EyeSteps = 480, BombR = 112 * Px;
  const BonusSlow = 1, BonusBack = 2, BonusBomb = 3, BonusEye = 4, BonusKinds = 4;
  const PtsBead = 10, PtsCombo = 100, PtsStreak = 30, PtsGap = 100, PtsClear = 1000, PtsSecond = 10, MaxGaps = 3;
  const Playing = 0, Won = 1, Lost = 2, EvFire = 1, EvSwap = 2;

  const trunc = Math.trunc;

  function isqrt(n) {
    if (n <= 0) return 0;
    let r = Math.floor(Math.sqrt(n));
    while (r * r > n) r--;
    while ((r + 1) * (r + 1) <= n) r++;
    return r;
  }

  // ---------- mulberry32 ----------

  function rng(seed) { return { s: seed >>> 0 }; }
  function next(r) {
    r.s = (r.s + 0x6D2B79F5) >>> 0;
    let t = r.s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return (t ^ (t >>> 14)) >>> 0;
  }

  // ---------- жолоб ----------

  /** xy — [x0, y0, x1, y1, …] у su (так, як віддає сервер). */
  function Track(xy, sx, sy) {
    const n = xy.length >> 1;
    const X = new Int32Array(n), Y = new Int32Array(n), L = new Int32Array(n);
    for (let i = 0; i < n; i++) {
      X[i] = xy[2 * i];
      Y[i] = xy[2 * i + 1];
      if (i === 0) continue;
      const dx = X[i] - X[i - 1], dy = Y[i] - Y[i - 1];
      L[i] = L[i - 1] + isqrt(dx * dx + dy * dy);
    }
    this.X = X; this.Y = Y; this.L = L; this.Sx = sx; this.Sy = sy;
    this.End = L[n - 1];
    this.Count = n;
  }
  Track.prototype.seg = function (d) {
    const L = this.L;
    let lo = 0, hi = L.length - 2;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (L[mid] <= d) lo = mid;
      else hi = mid - 1;
    }
    return lo;
  };
  /** Позиція на відстані d — у out[0], out[1] (без алокацій у гарячому циклі). */
  Track.prototype.at = function (d, out) {
    const X = this.X, Y = this.Y, L = this.L;
    if (d <= 0) { out[0] = X[0]; out[1] = Y[0]; return out; }
    if (d >= this.End) { out[0] = X[X.length - 1]; out[1] = Y[Y.length - 1]; return out; }
    const i = this.seg(d);
    const len = L[i + 1] - L[i];
    if (len <= 0) { out[0] = X[i]; out[1] = Y[i]; return out; }
    const t = d - L[i];
    out[0] = X[i] + trunc((X[i + 1] - X[i]) * t / len);
    out[1] = Y[i] + trunc((Y[i + 1] - Y[i]) * t / len);
    return out;
  };
  Track.prototype.tangent = function (d, out) {
    const i = d <= 0 ? 0 : d >= this.End ? this.L.length - 2 : this.seg(d);
    out[0] = this.X[i + 1] - this.X[i];
    out[1] = this.Y[i + 1] - this.Y[i];
    return out;
  };
  /** Жолоб із різниць (як у /api/games/namysto/tracks). */
  Track.fromDelta = function (d, sx, sy) {
    const xy = new Array(d.length);
    for (let i = 0; i < d.length; i++) xy[i] = i < 2 ? d[i] : d[i] + xy[i - 2];
    return new Track(xy, sx, sy);
  };

  // ---------- симуляція ----------

  function Sim(track, colors, beads, speed, parSeconds, seed) {
    this.Track = track;
    this.Colors = Math.min(6, Math.max(2, colors));
    this.Speed = Math.max(1, speed);
    this.ParSteps = parSeconds * StepsPerSecond;
    this.ToSpawn = Math.max(1, beads);
    seed = seed >>> 0;
    this.ChainRng = rng(seed);
    this.ShotRng = rng((seed ^ 0x9E3779B9) >>> 0);
    this.BonusRng = rng((seed ^ 0x85EBCA6B) >>> 0);
    this.Beads = [];
    this.Shots = [];
    this.S = 0; this.LastColor = -1; this.CooldownLeft = 0; this.SlowLeft = 0; this.BackLeft = 0; this.EyeLeft = 0;
    this.Intro = true; this.BonusIn = BonusFirst;
    this.ChainCombo = 0; this.Streak = 0; this.MaxCombo = 0; this.Misses = 0; this.Fired = 0; this.Popped = 0; this.MaxHead = 0;
    this.NextId = 1; this.Score = 0; this.State = Playing; this.ClearBonus = 0; this.TimeBonus = 0;
    this._bx = []; this._by = []; this._posDirty = true;
    this._p = [0, 0]; this._q = [0, 0];
    /** Лише для малювання: що сталось за крок (розсипи, вставки, промахи) — сервер цього не має. */
    this.events = [];
    this.Cur = this.pick();
    this.Next = this.pick();
  }
  const P = Sim.prototype;

  Object.defineProperty(P, 'End', { get() { return this.Track.End; } });

  // ---------- ввід ----------

  P.fire = function (tx, ty) {
    if (this.State !== Playing || this.CooldownLeft > 0 || this.Shots.length >= MaxShots) return false;
    const t = this.Track;
    const dx = tx * Px - t.Sx, dy = ty * Px - t.Sy;
    const len = isqrt(dx * dx + dy * dy);
    if (len < MinAim) return false;
    const subs = this.EyeLeft > 0 ? SubsEye : SubsNormal;
    this.Shots.push({
      X: t.Sx + trunc(dx * Mouth / len),
      Y: t.Sy + trunc(dy * Mouth / len),
      Vx: trunc(dx * ShotSub / len),
      Vy: trunc(dy * ShotSub / len),
      C: this.Cur, G: 0, Subs: subs, Id: this.NextId++,
      // для малювання: звідки летить (мить між кроками домальовує клієнт)
      px: 0, py: 0,
    });
    const s = this.Shots[this.Shots.length - 1];
    s.px = s.X; s.py = s.Y;
    this.Cur = this.Next;
    this.Next = this.pick();
    this.CooldownLeft = Cooldown;
    this.Fired++;
    return true;
  };

  P.swap = function () {
    if (this.State !== Playing) return false;
    const c = this.Cur; this.Cur = this.Next; this.Next = c;
    return true;
  };

  P.apply = function (kind, x, y) {
    if (kind === EvFire) return this.fire(x, y);
    if (kind === EvSwap) return this.swap();
    return false;
  };

  // ---------- крок ----------

  P.step = function () {
    if (this.State !== Playing) return;
    for (const b of this.Beads) b.pd = b.D;
    for (const s of this.Shots) { s.px = s.X; s.py = s.Y; }
    if (this.CooldownLeft > 0) this.CooldownLeft--;
    if (this.SlowLeft > 0) this.SlowLeft--;
    if (this.BackLeft > 0) this.BackLeft--;
    if (this.EyeLeft > 0) this.EyeLeft--;
    this.bonuses();
    this.moveChain();
    this.spawn();
    this.moveShots();
    const B = this.Beads;
    if (B.length > 0 && B[0].D > this.MaxHead) this.MaxHead = B[0].D;
    if (B.length > 0 && B[0].D >= this.End) this.State = Lost;
    else if (this.ToSpawn === 0 && B.length === 0) {
      this.State = Won;
      this.ClearBonus = PtsClear;
      this.TimeBonus = this.S < this.ParSteps ? trunc((this.ParSteps - this.S) * PtsSecond / StepsPerSecond) : 0;
      this.Score += this.ClearBonus + this.TimeBonus;
    }
    this.S++;
  };

  P.bonuses = function () {
    const B = this.Beads;
    let any = false;
    for (let i = 0; i < B.length; i++) {
      const b = B[i];
      if (b.Bonus === 0) continue;
      if (--b.BonusLeft <= 0) { b.Bonus = 0; b.BonusLeft = 0; }
      else any = true;
    }
    if (this.Intro) return;
    if (this.BonusIn > 0) { this.BonusIn--; return; }
    if (any) return;
    const lo = Dm, hi = this.End - Dm * 4;
    let count = 0;
    for (let i = 0; i < B.length; i++) if (B[i].D >= lo && B[i].D <= hi) count++;
    if (count === 0) { this.BonusIn = 60; return; }
    let k = next(this.BonusRng) % count;
    const kind = 1 + next(this.BonusRng) % BonusKinds;
    for (let i = 0; i < B.length; i++) {
      if (B[i].D < lo || B[i].D > hi) continue;
      if (k-- !== 0) continue;
      B[i].Bonus = kind;
      B[i].BonusLeft = BonusLife;
      break;
    }
    this.BonusIn = BonusEvery + next(this.BonusRng) % BonusJitter;
  };

  // ---------- разок ----------

  P.moveChain = function () {
    const B = this.Beads;
    const n = B.length;
    if (n === 0) { this.ChainCombo = 0; return; }
    const gap = new Array(n > 1 ? n - 1 : 0);
    for (let k = 0; k < n - 1; k++) gap[k] = B[k].D - B[k + 1].D > Dm;

    let segStart = 0;
    for (let k = 0; k < n - 1; k++) {
      if (!gap[k]) continue;
      if (B[k].C === B[k + 1].C) {
        const room = B[k].D - B[k + 1].D - Dm;
        const m = room < Magnet ? room : Magnet;
        for (let i = segStart; i <= k; i++) B[i].D -= m;
      }
      segStart = k + 1;
    }
    let v;
    if (this.BackLeft > 0) v = -BackSpeed;
    else if (this.Intro) v = IntroSpeed;
    else v = this.SlowLeft > 0 ? Math.max(1, trunc(this.Speed / 2)) : this.Speed;
    for (let i = segStart; i < n; i++) B[i].D += v;
    if (this.Intro && (B[0].D * 100 >= this.End * IntroPct || this.S >= IntroMaxSteps)) this.Intro = false;

    for (let k = n - 2; k >= 0; k--) {
      const need = B[k + 1].D + Dm - B[k].D;
      if (need > 0) B[k].D += need < PushMax ? need : PushMax;
    }
    for (let k = 0; k < n - 1; k++)
      if (gap[k] && B[k].D - B[k + 1].D <= Dm) B[k + 1].J = true;
    this._posDirty = true;

    for (;;) {
      let j = -1;
      for (let i = 1; i < B.length; i++) if (B[i].J) { j = i; break; }
      if (j < 0) break;
      B[j].J = false;
      if (B[j - 1].C !== B[j].C) continue;
      const r = this.run(j);
      if (r[1] - r[0] + 1 >= 3) this.pop(r[0], r[1], false, 0);
    }
    for (let i = 0; i < B.length; i++) B[i].J = false;

    let pull = false;
    for (let k = 0; k < B.length - 1; k++)
      if (B[k].D - B[k + 1].D > Dm && B[k].C === B[k + 1].C) { pull = true; break; }
    if (!pull) this.ChainCombo = 0;
  };

  P.spawn = function () {
    const B = this.Beads;
    while (this.ToSpawn > 0 && (B.length === 0 || B[B.length - 1].D >= 0)) {
      const r1 = next(this.ChainRng);
      const r2 = next(this.ChainRng);
      const c = this.LastColor >= 0 && r1 % 100 < RunPct ? this.LastColor : r2 % this.Colors;
      const d = B.length === 0 ? 0 : B[B.length - 1].D - Dm;
      B.push({ C: c, D: d, Bonus: 0, BonusLeft: 0, Id: this.NextId++, J: false, pd: d });
      this.LastColor = c;
      this.ToSpawn--;
      this._posDirty = true;
    }
  };

  P.run = function (i) {
    const B = this.Beads;
    const c = B[i].C;
    let a = i;
    while (a > 0 && B[a - 1].C === c && B[a - 1].D - B[a].D <= Dm) a--;
    let b = i;
    while (b < B.length - 1 && B[b + 1].C === c && B[b].D - B[b + 1].D <= Dm) b++;
    return [a, b];
  };

  P.pop = function (a, b, shot, gaps) {
    const B = this.Beads;
    const count = b - a + 1;
    let slow = false, back = false, eye = false, bomb = false, bombD = 0;
    for (let i = a; i <= b; i++) {
      switch (B[i].Bonus) {
        case BonusSlow: slow = true; break;
        case BonusBack: back = true; break;
        case BonusEye: eye = true; break;
        case BonusBomb: if (!bomb) { bomb = true; bombD = B[i].D; } break;
      }
    }
    let pts;
    if (shot) {
      this.Streak++;
      this.ChainCombo = 1;
      pts = PtsBead * count + (this.Streak >= 3 ? PtsStreak * (this.Streak - 2) : 0) + PtsGap * gaps;
    } else {
      this.ChainCombo = this.ChainCombo > 0 ? this.ChainCombo + 1 : 1;
      pts = PtsBead * count + PtsCombo * (this.ChainCombo - 1);
    }
    if (this.ChainCombo > this.MaxCombo) this.MaxCombo = this.ChainCombo;
    this.Score += pts;
    this.Popped += count;
    const gone = B.splice(a, count);
    this.events.push({ t: 'pop', beads: gone, pts, combo: this.ChainCombo, streak: shot ? this.Streak : 0, gaps, s: this.S });
    this._posDirty = true;
    if (bomb) this.bomb(bombD);
    if (slow) { this.SlowLeft = SlowSteps; this.events.push({ t: 'bonus', k: BonusSlow, s: this.S }); }
    if (back) { this.BackLeft = BackSteps; this.events.push({ t: 'bonus', k: BonusBack, s: this.S }); }
    if (eye) { this.EyeLeft = EyeSteps; this.events.push({ t: 'bonus', k: BonusEye, s: this.S }); }
    this.fixShooter();
  };

  P.bomb = function (d) {
    const B = this.Beads, t = this.Track;
    const c = t.at(d, [0, 0]);
    const x = c[0], y = c[1];
    const r2 = BombR * BombR;
    const gone = [];
    const p = this._q;
    for (let i = B.length - 1; i >= 0; i--) {
      t.at(B[i].D, p);
      const dx = p[0] - x, dy = p[1] - y;
      if (dx * dx + dy * dy > r2) continue;
      gone.push(B[i]);
      B.splice(i, 1);
      this.Score += PtsBead;
      this.Popped++;
    }
    this.events.push({ t: 'bomb', x, y, beads: gone, s: this.S });
    this._posDirty = true;
  };

  P.fixShooter = function () {
    if (this.Beads.length === 0) return;
    if (!this.has(this.Cur)) this.Cur = this.pick();
    if (!this.has(this.Next)) this.Next = this.pick();
  };

  P.has = function (c) {
    const B = this.Beads;
    for (let i = 0; i < B.length; i++) if (B[i].C === c) return true;
    return false;
  };

  P.pick = function () {
    const B = this.Beads;
    let mask = 0;
    for (let i = 0; i < B.length; i++) mask |= 1 << B[i].C;
    if (mask === 0) mask = (1 << this.Colors) - 1;
    let count = 0;
    for (let c = 0; c < 6; c++) if ((mask & (1 << c)) !== 0) count++;
    let k = next(this.ShotRng) % count;
    for (let c = 0; c < 6; c++) {
      if ((mask & (1 << c)) === 0) continue;
      if (k-- === 0) return c;
    }
    return 0;
  };

  // ---------- постріли ----------

  P.positions = function () {
    if (!this._posDirty) return;
    const B = this.Beads, bx = this._bx, by = this._by, p = this._p;
    for (let i = 0; i < B.length; i++) { this.Track.at(B[i].D, p); bx[i] = p[0]; by[i] = p[1]; }
    this._posDirty = false;
  };

  P.moveShots = function () {
    const S = this.Shots;
    for (let si = 0; si < S.length;) {
      const s = S[si];
      let done = false;
      for (let k = 0; k < s.Subs && !done; k++) {
        this.positions();
        const x0 = s.X, y0 = s.Y;
        s.X += s.Vx;
        s.Y += s.Vy;
        if (s.G < MaxGaps) s.G += this.gaps(x0, y0, s.X, s.Y);
        const hit = this.hit(s.X, s.Y);
        if (hit >= 0) {
          this.insert(s, hit);
          done = true;
        } else if (s.X < -OutMargin || s.Y < -OutMargin || s.X > Field + OutMargin || s.Y > Field + OutMargin) {
          this.Misses++;
          this.Streak = 0;
          this.events.push({ t: 'miss', s: this.S });
          done = true;
        }
      }
      if (done) S.splice(si, 1);
      else si++;
    }
  };

  P.hit = function (x, y) {
    const B = this.Beads, bx = this._bx, by = this._by, end = this.End;
    let best = -1, bestD = Dm * Dm;
    for (let i = 0; i < B.length; i++) {
      const d = B[i].D;
      if (d < Dm / 2 || d >= end) continue;
      const dx = bx[i] - x, dy = by[i] - y;
      const q = dx * dx + dy * dy;
      if (q < bestD) { bestD = q; best = i; }
    }
    return best;
  };

  P.gaps = function (x0, y0, x1, y1) {
    const B = this.Beads, bx = this._bx, by = this._by, end = this.End;
    let n = 0;
    const lim = Dm * 3;
    for (let i = 0; i < B.length - 1; i++) {
      if (B[i].D - B[i + 1].D <= Dm) continue;
      if (B[i + 1].D < Dm / 2 || B[i].D >= end) continue;
      const ax = bx[i], ay = by[i], cx = bx[i + 1], cy = by[i + 1];
      if ((ax - cx) * (ax - cx) + (ay - cy) * (ay - cy) >= lim * lim) continue;
      if (cross(x0, y0, x1, y1, ax, ay, cx, cy)) n++;
    }
    return n;
  };

  function orient(ax, ay, bx, by, cx, cy) { return (bx - ax) * (cy - ay) - (by - ay) * (cx - ax); }
  function cross(x0, y0, x1, y1, ax, ay, bx, by) {
    const d1 = orient(x0, y0, x1, y1, ax, ay), d2 = orient(x0, y0, x1, y1, bx, by);
    const d3 = orient(ax, ay, bx, by, x0, y0), d4 = orient(ax, ay, bx, by, x1, y1);
    return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
  }

  P.insert = function (s, j) {
    const B = this.Beads;
    const t = this.Track.tangent(B[j].D, this._q);
    const rx = s.X - this._bx[j], ry = s.Y - this._by[j];
    const ahead = rx * t[0] + ry * t[1] > 0;
    const idx = ahead ? j : j + 1;
    const d = ahead ? B[j].D + Dm : B[j].D;
    // pd — звідки малювати перший кадр: з того місця, куди влучила летюча
    B.splice(idx, 0, { C: s.C, D: d, Bonus: 0, BonusLeft: 0, Id: s.Id, J: false, pd: d, fx: s.X, fy: s.Y, at: this.S });
    this._posDirty = true;
    const r = this.run(idx);
    this.events.push({ t: 'hit', id: s.Id, s: this.S });
    if (r[1] - r[0] + 1 >= 3) this.pop(r[0], r[1], true, s.G);
    else this.Streak = 0;
  };

  // ---------- для перевірки ----------

  P.hash = function () {
    let h = 2166136261;
    const mix = (v) => { h = Math.imul(h ^ v, 16777619) >>> 0; };
    mix(this.S); mix(this.Score | 0); mix(Math.floor(this.Score / 4294967296) | 0);
    mix(this.ChainRng.s | 0); mix(this.ShotRng.s | 0); mix(this.BonusRng.s | 0);
    mix(this.Cur); mix(this.Next); mix(this.ToSpawn); mix(this.CooldownLeft); mix(this.SlowLeft); mix(this.BackLeft); mix(this.EyeLeft);
    mix(this.Intro ? 1 : 0); mix(this.BonusIn); mix(this.ChainCombo); mix(this.Streak); mix(this.State); mix(this.Beads.length); mix(this.Shots.length);
    for (const b of this.Beads) { mix(b.C); mix(b.D); mix(b.Bonus); mix(b.BonusLeft); }
    for (const s of this.Shots) { mix(s.X); mix(s.Y); mix(s.Vx); mix(s.Vy); mix(s.C); mix(s.G); mix(s.Subs); }
    return h >>> 0;
  };

  /** Прогнати журнал з нуля до кроку upTo (або до кінця): [крок, вид, x, y] / [крок, 2]. hashes — кожні every кроків. */
  function replay(track, lv, seed, journal, upTo, every) {
    const sim = new Sim(track, lv.colors, lv.beads, lv.speed, lv.par, seed);
    const hashes = [];
    let k = 0;
    const lim = upTo == null ? 60 * 60 * 8 : upTo;
    while (sim.State === Playing && sim.S < lim) {
      while (k < journal.length && journal[k][0] === sim.S) {
        const e = journal[k++];
        sim.apply(e[1], e[2] || 0, e[3] || 0);
      }
      sim.step();
      if (every && sim.S % every === 0) hashes.push(sim.hash());
    }
    sim.events.length = 0;
    return { sim, hashes };
  }

  root.NamystoSim = {
    Sim, Track, replay, isqrt, rng, next,
    Px, Field, Dm, StepsPerSecond, Playing, Won, Lost, EvFire, EvSwap,
    BonusSlow, BonusBack, BonusBomb, BonusEye, BombR, MaxShots, Cooldown, Mouth,
  };
})(typeof window !== 'undefined' ? window : globalThis);
