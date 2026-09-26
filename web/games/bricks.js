/*
  Цеглини (bricks) і «Цеглини: 40 рядів» (bricks-sprint) — docs/games/specs/bricks.md.

  Свою стіну браузер рахує сам, миттєво: той самий рушій, що й Impl/BricksCore.cs, побайтно (цілі числа,
  xorshift32, FNV-1a). Кожен натиск одразу йде в локальну стіну, отримує номер і пачками по 50 мс летить на
  сервер: Input('j', { q, e: [t, k, …], h, f }). Сервер — суддя: переганяє журнал, роздає сміття, вирішує, хто
  впав. Посилка сміття (подія g у кадрі) стає в моєму часі туди, де сервер її поставив, — я відкочуюсь до
  знімка, додаю її й повторюю свої натиски. Розійшлись хеші — sync, сервер шле fix (повний стан стіни).
  Чужі стіни малюємо з кадрів як є: фігурки ходять клітинками, згладжувати нема чого.

  Кадр (Impl/Bricks.cs): { t, ph, in, lvl, sd, b: [{ s, a, l, pd, rp, v, r?, p, hd, n, k, q, g, x }], ev: [...] }
  Вид: { phase, startIn, t, round, need, wins, seed, speed, garbage, stage, target, rules, boards: [BoardWire], result }
*/
(() => {
  'use strict';

  // =============================================================================================
  // Рушій (дзеркало BricksCore.cs — будь-яку зміну правил робити в обох файлах разом)
  // =============================================================================================

  const W = 10, H = 24, VIS = 20;
  const DAS = 10, ARR = 2, SOFTG = 2, LOCKD = 30, MAXRES = 15, CLEART = 10, RIPE = 30, MAXINS = 8, AHEAD = 15, BEHIND = 90;
  const SPRINTG = 48, SPRINT_LINES = 40, SUDDEN = 18000, CAP = 28800, MAXCRED = 32, FULL = (1 << W) - 1;
  const GRAV = [48, 36, 26, 18, 12, 8, 5, 3, 2, 1];
  const PI_ = 0, PO = 1, PT = 2;
  const K = { PULSE: 0, LEFT: 1, LEFT_UP: 2, RIGHT: 3, RIGHT_UP: 4, SOFT: 5, SOFT_UP: 6, CW: 7, CCW: 8, HARD: 10, HOLD: 11 };

  const SHAPES = new Int8Array([
    0, 2, 1, 2, 2, 2, 3, 2, 2, 0, 2, 1, 2, 2, 2, 3, 0, 1, 1, 1, 2, 1, 3, 1, 1, 0, 1, 1, 1, 2, 1, 3,
    1, 1, 2, 1, 1, 2, 2, 2, 1, 1, 2, 1, 1, 2, 2, 2, 1, 1, 2, 1, 1, 2, 2, 2, 1, 1, 2, 1, 1, 2, 2, 2,
    0, 1, 1, 1, 2, 1, 1, 2, 1, 0, 1, 1, 1, 2, 2, 1, 0, 1, 1, 1, 2, 1, 1, 0, 1, 0, 1, 1, 1, 2, 0, 1,
    0, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 2, 2, 0, 2, 1, 0, 0, 1, 0, 1, 1, 2, 1, 0, 1, 0, 2, 1, 0, 1, 1,
    0, 2, 1, 2, 1, 1, 2, 1, 1, 0, 1, 1, 2, 1, 2, 2, 0, 1, 1, 1, 1, 0, 2, 0, 0, 0, 0, 1, 1, 1, 1, 2,
    0, 2, 0, 1, 1, 1, 2, 1, 1, 0, 1, 1, 1, 2, 2, 2, 0, 1, 1, 1, 2, 1, 2, 0, 0, 0, 1, 0, 1, 1, 1, 2,
    2, 2, 0, 1, 1, 1, 2, 1, 1, 0, 1, 1, 1, 2, 2, 0, 0, 0, 0, 1, 1, 1, 2, 1, 0, 2, 1, 2, 1, 1, 1, 0,
  ]);
  const KJ = new Int8Array([
    0, 0, -1, 0, -1, 1, 0, -2, -1, -2,
    0, 0, 1, 0, 1, -1, 0, 2, 1, 2,
    0, 0, 1, 0, 1, -1, 0, 2, 1, 2,
    0, 0, -1, 0, -1, 1, 0, -2, -1, -2,
    0, 0, 1, 0, 1, 1, 0, -2, 1, -2,
    0, 0, -1, 0, -1, -1, 0, 2, -1, 2,
    0, 0, -1, 0, -1, -1, 0, 2, -1, 2,
    0, 0, 1, 0, 1, 1, 0, -2, 1, -2,
  ]);
  const KI = new Int8Array([
    0, 0, -2, 0, 1, 0, -2, -1, 1, 2,
    0, 0, 2, 0, -1, 0, 2, 1, -1, -2,
    0, 0, -1, 0, 2, 0, -1, 2, 2, -1,
    0, 0, 1, 0, -2, 0, 1, -2, -2, 1,
    0, 0, 2, 0, -1, 0, 2, 1, -1, -2,
    0, 0, -2, 0, 1, 0, -2, -1, 1, 2,
    0, 0, 1, 0, -2, 0, 1, -2, -2, 1,
    0, 0, -1, 0, 2, 0, -1, 2, 2, -1,
  ]);
  const ATK_N = [0, 0, 1, 2, 4], ATK_H = [0, 1, 1, 2, 4], ATK_T = [0, 2, 4, 6, 6];

  /// xorshift32 на беззнакових: у JS кожен крок >>> 0.
  function nextRand(x) {
    x = (x ^ (x << 13)) >>> 0;
    x = (x ^ (x >>> 17)) >>> 0;
    x = (x ^ (x << 5)) >>> 0;
    return x;
  }

  /// Мішок номер n: перестановка 0..6 (I O T S Z J L) Фішером-Єйтсом, засіяна seed + n·0x9E3779B9.
  function bag(seed, n, into) {
    let s = (seed + Math.imul(n, 0x9E3779B9)) >>> 0;
    if (s === 0) s = 1;
    for (let i = 0; i < 7; i++) into[i] = i;
    for (let k = 6; k >= 1; k--) {
      s = nextRand(s);
      const j = s % (k + 1);
      const t = into[k]; into[k] = into[j]; into[j] = t;
    }
    return into;
  }

  function mixHash(h, v) {
    h = Math.imul(h ^ (v & 255), 16777619);
    h = Math.imul(h ^ ((v >>> 8) & 255), 16777619);
    h = Math.imul(h ^ ((v >>> 16) & 255), 16777619);
    h = Math.imul(h ^ ((v >>> 24) & 255), 16777619);
    return h;
  }

  class Core {
    constructor() {
      this.cells = new Uint8Array(W * H);
      this.mask = new Uint16Array(H);
      this.type = -1; this.bx = 0; this.by = 0; this.rot = 0;
      this.seed = 1; this.pi = 0; this.hold = -1; this.holdUsed = false;
      this.keys = 0; this.dir = 0; this.das = 0; this.gravT = 0; this.lockT = 0; this.resets = 0; this.lowY = 0;
      this.clearing = 0; this.lastRot = false;
      this.combo = -1; this.b2b = -1; this.lines = 0; this.sent = 0; this.recv = 0;
      this.tick = 0; this.seq = 0;
      this.cr = new Int32Array(MAXCRED * 5);   // [g, rows, hole, ripeAt, from] × MAXCRED
      this.crN = 0; this.gseq = 0;
      this.alive = true; this.outTick = -1; this.outWhy = 0;
      this.garbage = 0; this.stageTicks = 0; this.cellsVer = 0; this.ver = 0;
      // що сталось — для ефектів і звуку; клієнт забирає й обнуляє
      this.clears = new Int32Array(64 * 7); this.clearN = 0;
      this.inserted = 0; this.fours = 0; this.locks = 0;
      this.drops = 0; this.dropX0 = 0; this.dropX1 = 0; this.dropY0 = 0; this.dropY1 = 0;
      this._bag = new Int8Array(7); this._bagN = -1; this._bagSeed = 0;
      this._take = new Int32Array(MAXCRED);
    }

    reset(seed, garbage, stageTicks) {
      this.cells.fill(0); this.mask.fill(0);
      this.seed = (seed >>> 0) || 1;
      this.garbage = garbage; this.stageTicks = stageTicks;
      this.pi = 0; this.hold = -1; this.holdUsed = false;
      this.keys = this.dir = this.das = this.gravT = this.lockT = this.resets = this.clearing = 0;
      this.lastRot = false;
      this.combo = this.b2b = -1;
      this.lines = this.sent = this.recv = 0;
      this.tick = this.seq = 0;
      this.crN = this.gseq = 0;
      this.alive = true; this.outTick = -1; this.outWhy = 0;
      this.cellsVer = this.ver = 0;
      this.clearN = this.inserted = this.fours = this.locks = this.drops = 0;
      this._bagN = -1;
      this.spawnNext();
    }

    pieceAt(i) {
      const n = (i / 7) | 0;
      if (n !== this._bagN || this._bagSeed !== this.seed) {
        bag(this.seed, n, this._bag);
        this._bagN = n; this._bagSeed = this.seed;
      }
      return this._bag[i % 7];
    }

    fits(type, bx, by, rot) {
      const o = (type * 4 + rot) * 8;
      for (let i = 0; i < 8; i += 2) {
        const x = bx + SHAPES[o + i], y = by + SHAPES[o + i + 1];
        if (x < 0 || x >= W || y < 0 || y >= H) return false;
        if (this.mask[y] & (1 << x)) return false;
      }
      return true;
    }

    get grounded() { return !this.fits(this.type, this.bx, this.by - 1, this.rot); }
    get stage() { return this.stageTicks <= 0 ? 0 : (this.tick / this.stageTicks) | 0; }
    get gravity() { return this.stageTicks <= 0 ? SPRINTG : GRAV[Math.min(GRAV.length - 1, (this.tick / this.stageTicks) | 0)]; }

    step() {
      if (!this.alive) return;
      this.tick++;
      if (this.clearing > 0) {
        this.clearing--;
        if (this.clearing === 0) { this.removeFullRows(); this.spawnNext(); }
        return;
      }
      if (this.type < 0) return;
      if (this.dir !== 0) {
        this.das++;
        if (this.das >= DAS && (this.das - DAS) % ARR === 0) this.tryMove(this.dir);
      }
      let g = this.gravity;
      if ((this.keys & 4) && g > SOFTG) g = SOFTG;
      this.gravT++;
      while (this.gravT >= g) {
        this.gravT -= g;
        if (!this.fits(this.type, this.bx, this.by - 1, this.rot)) break;
        this.by--;
        this.lastRot = false;
        this.ver++;
        if (this.by < this.lowY) { this.lowY = this.by; this.resets = 0; this.lockT = 0; }
      }
      if (!this.grounded) this.lockT = 0;
      else if (++this.lockT >= LOCKD) this.lock();
    }

    advanceTo(t) { while (this.tick < t && this.alive) this.step(); }

    apply(k) {
      if (!this.alive) return;
      switch (k) {
        case 1: this.keys |= 1; this.dir = -1; this.das = 0; this.tryMove(-1); break;
        case 2:
          this.keys &= ~1;
          if (this.dir === -1) {
            if (this.keys & 2) { this.dir = 1; this.das = 0; this.tryMove(1); } else this.dir = 0;
          }
          break;
        case 3: this.keys |= 2; this.dir = 1; this.das = 0; this.tryMove(1); break;
        case 4:
          this.keys &= ~2;
          if (this.dir === 1) {
            if (this.keys & 1) { this.dir = -1; this.das = 0; this.tryMove(-1); } else this.dir = 0;
          }
          break;
        case 5: this.keys |= 4; if (this.gravT > SOFTG - 1) this.gravT = SOFTG - 1; break;
        case 6: this.keys &= ~4; break;
        case 7: this.tryRotate(1); break;
        case 8: this.tryRotate(3); break;
        case 10: this.hardDrop(); break;
        case 11: this.doHold(); break;
        default:
      }
    }

    tryMove(dx) {
      if (this.type < 0 || !this.fits(this.type, this.bx + dx, this.by, this.rot)) return false;
      this.bx += dx;
      this.lastRot = false;
      this.ver++;
      this.moved();
      return true;
    }

    moved() {
      if (this.resets < MAXRES) { this.resets++; this.lockT = 0; }
    }

    tryRotate(turn) {
      if (this.type < 0 || this.type === PO) return;
      const to = (this.rot + turn) & 3;
      const idx = turn === 1 ? this.rot * 2 : to * 2 + 1;
      const kicks = this.type === PI_ ? KI : KJ;
      const o = idx * 10;
      for (let i = 0; i < 10; i += 2) {
        const nx = this.bx + kicks[o + i], ny = this.by + kicks[o + i + 1];
        if (!this.fits(this.type, nx, ny, to)) continue;
        this.bx = nx; this.by = ny; this.rot = to;
        this.lastRot = true;
        this.ver++;
        this.moved();
        if (this.by < this.lowY) { this.lowY = this.by; this.resets = 0; this.lockT = 0; }
        return;
      }
    }

    hardDrop() {
      if (this.type < 0) return;
      const y0 = this.by;
      let d = 0;
      while (this.fits(this.type, this.bx, this.by - 1, this.rot)) { this.by--; d++; }
      if (d > 0) this.lastRot = false;
      // слід для ефекту: стовпці фігурки й висота падіння
      const o = (this.type * 4 + this.rot) * 8;
      let x0 = 99, x1 = -1;
      for (let i = 0; i < 8; i += 2) { const x = this.bx + SHAPES[o + i]; if (x < x0) x0 = x; if (x > x1) x1 = x; }
      this.drops++; this.dropX0 = x0; this.dropX1 = x1; this.dropY0 = y0; this.dropY1 = this.by;
      this.lock();
    }

    doHold() {
      if (this.type < 0 || this.holdUsed) return;
      const was = this.type;
      if (this.hold < 0) { this.hold = was; this.spawnNext(); } else { const t = this.hold; this.hold = was; this.spawn(t); }
      this.holdUsed = true;
      this.ver++;
    }

    spawnNext() { const t = this.pieceAt(this.pi); this.pi++; this.spawn(t); }

    spawn(type) {
      this.type = type; this.rot = 0; this.bx = 3; this.by = type === PI_ ? 18 : 19;
      this.holdUsed = false; this.lockT = 0; this.resets = 0; this.gravT = 0; this.lastRot = false; this.lowY = this.by;
      this.ver++;
      if (!this.fits(type, this.bx, this.by, 0)) this.topOut(1);
    }

    taken(x, y) { return x < 0 || x >= W || y < 0 || y >= H || (this.mask[y] & (1 << x)) ? 1 : 0; }

    lock() {
      const o = (this.type * 4 + this.rot) * 8;
      let above = 0;
      const color = this.type + 1;
      for (let i = 0; i < 8; i += 2) {
        const x = this.bx + SHAPES[o + i], y = this.by + SHAPES[o + i + 1];
        this.cells[y * W + x] = color;
        this.mask[y] |= 1 << x;
        if (y >= VIS) above++;
      }
      this.cellsVer++; this.ver++; this.locks++;
      if (above === 4) { this.topOut(2); return; }
      let n = 0, left = 0;
      for (let y = 0; y < H; y++) { if (this.mask[y] === FULL) n++; else if (this.mask[y] !== 0) left++; }
      let tspin = false;
      if (this.type === PT && this.lastRot) {
        const bx = this.bx, by = this.by;
        tspin = this.taken(bx, by) + this.taken(bx + 2, by) + this.taken(bx, by + 2) + this.taken(bx + 2, by + 2) >= 3;
      }
      this.type = -1;
      if (n === 0) {
        this.combo = -1;
        this.insertGarbage();
        if (this.alive) this.spawnNext();
        return;
      }
      const perfect = left === 0;
      this.combo++;
      const strong = n === 4 || tspin;
      let atk = tspin ? ATK_T[n] : this.garbage === 1 ? ATK_H[n] : ATK_N[n];
      if (strong) { if (this.b2b >= 0) atk++; this.b2b++; } else this.b2b = -1;
      if (this.combo >= 1) atk += Math.min(3, (this.combo + 1) >> 1);
      if (perfect) atk += 4;
      if (this.garbage === 1 && atk > 0) atk++;
      if (this.garbage === 2) atk = 0;
      if (n === 4) this.fours++;
      this.lines += n;
      let rest = atk, w = 0;
      const cr = this.cr;
      for (let i = 0; i < this.crN; i++) {
        let rows = cr[i * 5 + 1];
        if (rest > 0) { const take = Math.min(rest, rows); rows -= take; rest -= take; }
        if (rows > 0) {
          if (w !== i) for (let j = 0; j < 5; j++) cr[w * 5 + j] = cr[i * 5 + j];
          cr[w * 5 + 1] = rows;
          w++;
        }
      }
      this.crN = w;
      this.sent += rest;
      if (this.clearN < 64) {
        const e = this.clearN++ * 7, c = this.clears;
        c[e] = n; c[e + 1] = (tspin ? 1 : 0) + (perfect ? 2 : 0); c[e + 2] = this.combo; c[e + 3] = this.b2b;
        c[e + 4] = atk; c[e + 5] = rest; c[e + 6] = this.tick;
      }
      this.clearing = CLEART;
    }

    removeFullRows() {
      let dst = 0;
      for (let y = 0; y < H; y++) {
        if (this.mask[y] === FULL) continue;
        if (dst !== y) { this.cells.copyWithin(dst * W, y * W, y * W + W); this.mask[dst] = this.mask[y]; }
        dst++;
      }
      for (let y = dst; y < H; y++) { this.cells.fill(0, y * W, y * W + W); this.mask[y] = 0; }
      this.cellsVer++; this.ver++;
    }

    get pending() { let s = 0; for (let i = 0; i < this.crN; i++) s += this.cr[i * 5 + 1]; return s; }
    get ripe() { let s = 0; for (let i = 0; i < this.crN; i++) if (this.cr[i * 5 + 3] <= this.tick) s += this.cr[i * 5 + 1]; return s; }

    insertGarbage() {
      const cr = this.cr, take = this._take;
      let k = 0;
      for (let i = 0; i < this.crN; i++) {
        take[i] = 0;
        if (cr[i * 5 + 3] > this.tick || k >= MAXINS) continue;
        const t = Math.min(MAXINS - k, cr[i * 5 + 1]);
        take[i] = t; k += t;
      }
      if (k === 0) return;
      let top = -1;
      for (let y = H - 1; y >= 0; y--) if (this.mask[y] !== 0) { top = y; break; }
      const over = top >= 0 && top + k >= H - 2;
      for (let y = H - 1; y >= k; y--) {
        this.cells.copyWithin(y * W, (y - k) * W, (y - k) * W + W);
        this.mask[y] = this.mask[y - k];
      }
      let row = k - 1, w = 0;
      for (let i = 0; i < this.crN; i++) {
        const hole = cr[i * 5 + 2];
        for (let r = 0; r < take[i]; r++, row--) {
          const b = row * W;
          for (let x = 0; x < W; x++) this.cells[b + x] = x === hole ? 0 : 8;
          this.mask[row] = FULL & ~(1 << hole);
        }
        const rows = cr[i * 5 + 1] - take[i];
        if (rows > 0) {
          if (w !== i) for (let j = 0; j < 5; j++) cr[w * 5 + j] = cr[i * 5 + j];
          cr[w * 5 + 1] = rows;
          w++;
        }
      }
      this.crN = w;
      this.recv += k; this.inserted += k;
      this.cellsVer++; this.ver++;
      if (over) this.topOut(3);
    }

    topOut(why) {
      this.alive = false; this.outTick = this.tick; this.outWhy = why;
      this.type = -1; this.clearing = 0; this.ver++;
    }

    addCredit(g, rows, hole, ripeAt, from) {
      if (rows <= 0) return;
      this.gseq = g;
      if (this.crN === MAXCRED) { this.cr[(MAXCRED - 1) * 5 + 1] += rows; this.ver++; return; }
      const e = this.crN++ * 5;
      this.cr[e] = g; this.cr[e + 1] = rows; this.cr[e + 2] = hole; this.cr[e + 3] = ripeAt; this.cr[e + 4] = from;
      this.ver++;
    }

    hash() {
      let h = 2166136261 | 0;
      const c = this.cells;
      for (let i = 0; i < c.length; i++) h = Math.imul(h ^ c[i], 16777619);
      h = mixHash(h, this.type + 1);
      h = mixHash(h, this.bx + 8);
      h = mixHash(h, this.by + 8);
      h = mixHash(h, this.rot);
      h = mixHash(h, this.pi);
      h = mixHash(h, this.hold + 1);
      h = mixHash(h, this.holdUsed ? 1 : 0);
      h = mixHash(h, this.lines);
      h = mixHash(h, this.pending);
      h = mixHash(h, this.crN);
      h = mixHash(h, this.combo + 1);
      h = mixHash(h, this.b2b + 1);
      h = mixHash(h, this.tick);
      h = mixHash(h, this.clearing);
      h = mixHash(h, this.keys);
      h = mixHash(h, this.das);
      h = mixHash(h, this.lockT);
      h = mixHash(h, this.resets);
      h = mixHash(h, this.gravT);
      h = mixHash(h, this.lastRot ? 1 : 0);
      h = mixHash(h, this.dir + 1);
      h = mixHash(h, this.lowY + 8);
      return h >>> 0;
    }

    copyFrom(o) {
      this.cells.set(o.cells); this.mask.set(o.mask);
      this.type = o.type; this.bx = o.bx; this.by = o.by; this.rot = o.rot;
      this.seed = o.seed; this.pi = o.pi; this.hold = o.hold; this.holdUsed = o.holdUsed;
      this.keys = o.keys; this.dir = o.dir; this.das = o.das; this.gravT = o.gravT; this.lockT = o.lockT;
      this.resets = o.resets; this.lowY = o.lowY; this.clearing = o.clearing; this.lastRot = o.lastRot;
      this.combo = o.combo; this.b2b = o.b2b; this.lines = o.lines; this.sent = o.sent; this.recv = o.recv;
      this.tick = o.tick; this.seq = o.seq;
      this.cr.set(o.cr); this.crN = o.crN; this.gseq = o.gseq;
      this.alive = o.alive; this.outTick = o.outTick; this.outWhy = o.outWhy;
      this.garbage = o.garbage; this.stageTicks = o.stageTicks; this.cellsVer = o.cellsVer; this.ver = o.ver;
      return this;
    }

    /// Ряди стіни з рядків «0033100777» знизу вгору (стенд і тести; решта стану не чіпається).
    loadRows(rows) {
      this.cells.fill(0); this.mask.fill(0);
      for (let y = 0; y < rows.length && y < H; y++) {
        const s = rows[y];
        for (let x = 0; x < W && x < s.length; x++) {
          const v = s.charCodeAt(x) - 48;
          this.cells[y * W + x] = v;
          if (v) this.mask[y] |= 1 << x;
        }
      }
      this.cellsVer++; this.ver++;
    }

    /// Повний стан із BoardWire (виправлення, F5).
    loadWire(w, seed, garbage, stageTicks) {
      this.seed = (seed >>> 0) || 1; this.garbage = garbage; this.stageTicks = stageTicks; this._bagN = -1;
      this.loadRows(w.r || []);
      if (w.p) { this.type = w.p[0]; this.bx = w.p[1]; this.by = w.p[2]; this.rot = w.p[3]; } else this.type = -1;
      this.alive = w.a === 1; this.lines = w.l; this.sent = w.sn; this.recv = w.rc;
      this.tick = w.k; this.seq = w.q; this.gseq = w.g;
      this.hold = w.hd; this.holdUsed = w.hu === 1; this.pi = w.pi;
      this.combo = w.cb; this.b2b = w.bb; this.keys = w.ky; this.dir = w.dr; this.das = w.ds;
      this.gravT = w.gt; this.lockT = w.lt; this.resets = w.rs; this.lowY = w.ly; this.clearing = w.cl; this.lastRot = w.lr === 1;
      this.crN = 0;
      for (const c of w.cr || []) {
        if (this.crN === MAXCRED) break;
        const e = this.crN++ * 5;
        for (let j = 0; j < 5; j++) this.cr[e + j] = c[j];
      }
      this.outTick = this.alive ? -1 : this.tick;
      this.clearN = this.inserted = this.locks = this.drops = 0;
      this.ver++;
    }
  }

  /// Випадковий журнал для стенда детермінізму: той самий генератор, що й у BricksFixtures.cs.
  function gen(seed, n) {
    const keys = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11];
    let x = (seed >>> 0) || 1, t = 0;
    const e = [];
    for (let i = 0; i < n; i++) {
      x = nextRand(x); t += x % 9;
      x = nextRand(x); e.push(t, keys[x % 11]);
    }
    return e;
  }

  /// Посилки для журналу з n подій: кожна every-та подія — посилка [after, dt, rows, hole, from] (GenCredits у C#).
  function genCredits(seed, n, every = 23) {
    let x = (Math.imul(seed, 31) + 7) >>> 0;
    const out = [];
    for (let i = 3; i < n; i += every) {
      x = nextRand(x);
      out.push(i, x % 20, 1 + Math.floor(x / 20) % 6, Math.floor(x / 120) % 10, Math.floor(x / 1200) % 5 - 1);
    }
    return out;
  }

  // ---------- бот (дзеркало BricksBot у тестах): жадібний, цілими числами ----------

  const BOT_DELAY = 8, BW_LINES = 760, BW_HEIGHT = 510, BW_HOLES = 3560, BW_BUMP = 184;

  function botPlace(c, r, dx) {
    if (r === 3) { c.apply(K.CCW); if (c.rot !== 3) return false; }
    else for (let i = 0; i < r; i++) { const was = c.rot; c.apply(K.CW); if (c.rot === was) return false; }
    const n = Math.abs(dx);
    for (let i = 0; i < n; i++) {
      const bx = c.bx;
      c.apply(dx < 0 ? K.LEFT : K.RIGHT);
      c.apply(dx < 0 ? K.LEFT_UP : K.RIGHT_UP);
      if (c.bx === bx) return false;
    }
    return true;
  }

  /// Оцінка стіни так, ніби повні ряди вже зняли: ряди — добре, висота, дірки й горби — погано.
  function botScore(c, lines) {
    if (!c.alive) return -2147483647;
    let agg = 0, holes = 0, bump = 0, prev = -1;
    const m = c.mask;
    for (let x = 0; x < W; x++) {
      let ey = 0, top = 0;
      for (let y = 0; y < H; y++) { if (m[y] === FULL) continue; if ((m[y] >> x) & 1) top = ey + 1; ey++; }
      ey = 0;
      for (let y = 0; y < H; y++) { if (m[y] === FULL) continue; if (ey < top && !((m[y] >> x) & 1)) holes++; ey++; }
      agg += top;
      if (prev >= 0) bump += Math.abs(top - prev);
      prev = top;
    }
    return lines * BW_LINES - agg * BW_HEIGHT - holes * BW_HOLES - bump * BW_BUMP;
  }

  /// Клавіші для поточної фігурки: оберти, кроки (натиск+відпуск), жорстке падіння. scratch — робоча стіна.
  function botPlan(c, scratch) {
    if (c.type < 0 || !c.alive) return [];
    let best = -2147483648, bestR = 0, bestDx = 0;
    const rots = c.type === PO ? 1 : 4;
    for (let r = 0; r < rots; r++) {
      for (let dx = -5; dx <= 5; dx++) {
        scratch.copyFrom(c);
        if (!botPlace(scratch, r, dx)) continue;
        const lines = scratch.lines;
        scratch.apply(K.HARD);
        const s = botScore(scratch, scratch.lines - lines);
        if (s > best) { best = s; bestR = r; bestDx = dx; }
      }
    }
    const keys = [];
    if (bestR === 3) keys.push(K.CCW); else for (let i = 0; i < bestR; i++) keys.push(K.CW);
    for (let i = 0; i < Math.abs(bestDx); i++) keys.push(bestDx < 0 ? K.LEFT : K.RIGHT, bestDx < 0 ? K.LEFT_UP : K.RIGHT_UP);
    keys.push(K.HARD);
    return keys;
  }

  /// Прогін зафіксованого журналу так, як його переганяє сервер: посилка [after, dt, rows, hole, from] стає
  /// після події номер after, на тику min(tick + dt, t наступної події). fx.bot > 0 — журнал грає бот.
  function run(fx) {
    const c = new Core();
    c.reset(fx.seed, fx.mode || 0, fx.stage || 0);
    if (fx.rows) c.loadRows(fx.rows);
    const cr = fx.credits || [];
    let seq = 0, ci = 0, g = 0;
    const credit = (next) => {
      while (ci * 5 < cr.length && cr[ci * 5] === seq) {
        const at = Math.min(c.tick + cr[ci * 5 + 1], next), from = cr[ci * 5 + 4];
        c.advanceTo(at);
        c.addCredit(++g, cr[ci * 5 + 2], cr[ci * 5 + 3], from === -1 ? at : at + RIPE, from);
        ci++;
      }
    };
    const event = (t, k) => { credit(t); c.advanceTo(t); c.apply(k); c.seq = ++seq; };
    if (fx.bot > 0) {
      const scratch = new Core();
      for (let piece = 0; piece < fx.bot && c.alive; piece++) {
        while (c.type < 0 && c.alive) c.step();
        if (!c.alive) break;
        const keys = botPlan(c, scratch);
        let t = c.tick + BOT_DELAY;
        for (const k of keys) { if (!c.alive) break; event(t++, k); }
      }
      credit(Infinity);
      c.advanceTo(c.tick + 60);
    } else {
      const ev = fx.gen ? gen(fx.gen[0], fx.gen[1]) : fx.events;
      for (let i = 0; i < ev.length; i += 2) event(ev[i], ev[i + 1]);
      credit(Infinity);
      c.advanceTo(fx.end || 0);
    }
    return { hash: c.hash(), lines: c.lines, tick: c.tick, alive: c.alive, pi: c.pi, sent: c.sent, recv: c.recv, core: c };
  }

  /// 24 випадкові журнали й 6 ботів із посилками в усіх режимах сміття й темпах, згорнуті в одне число (Suite у C#).
  function suite() {
    let h = 2166136261 | 0;
    const stages = [0, 300, 600, 1200];
    for (let s = 1; s <= 30; s++) {
      const bot = s > 24;
      const ev = bot ? null : gen(s, 400);
      const r = run({ seed: (s * 7919) >>> 0, mode: s % 3, stage: stages[s % 4], events: ev, credits: genCredits(s, 400, bot ? 71 : 23),
        end: bot ? 0 : ev[ev.length - 2] + 200, bot: bot ? 80 : 0 });
      h = mixHash(h, r.hash); h = mixHash(h, r.lines); h = mixHash(h, r.sent); h = mixHash(h, r.recv);
    }
    return h >>> 0;
  }

  window.BricksCore = {
    Core, bag, nextRand, gen, genCredits, run, suite, botPlan, botScore, K, BOT_DELAY,
    consts: { W, H, VIS, DAS, ARR, SOFTG, LOCKD, MAXRES, CLEART, RIPE, MAXINS, AHEAD, BEHIND, SPRINTG, SPRINT_LINES, SUDDEN, CAP },
  };

  // Стенд детермінізму підставляє порожній HGames — далі лише модуль гри.
  if (!window.HGames || !HGames.register || !HGames.ui) return;

  /*@@UI@@*/
})();
