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

  // =============================================================================================
  // Мережа своєї стіни: передбачення, журнал, відкат (spec §5.5). Без DOM — цим самим користуються
  // боти в живій перевірці (window.BricksCore.Net) і картка гри.
  // =============================================================================================

  const RING = 128;          // знімків і подій у кільці
  const PULSE = 12;          // пульс, якщо 12 тиків (200 мс) без натисків — чужі бачать, як падає фігурка
  const MAX_BATCH = 60;      // подій в одній пачці (120 чисел із 128 дозволених)
  const MODE = { normal: 0, hard: 1, none: 2 };
  const HELD = [[1, K.LEFT, K.LEFT_UP], [2, K.RIGHT, K.RIGHT_UP], [4, K.SOFT, K.SOFT_UP]];

  class Net {
    /// send(action, payload) — Input каркаса (ctx.input) або хаб бота.
    constructor(send) {
      this.send = send;
      this.core = new Core();
      this.snap = new Array(RING);
      this.snapSeq = new Int32Array(RING).fill(-1);
      this.evT = new Int32Array(RING);
      this.evK = new Int8Array(RING);
      this.evH = new Float64Array(RING);
      this.evG = new Int32Array(RING);
      this.credits = [];
      this.q = [];
      this.qFirst = 1;
      this.seat = -1; this.seed = 0; this.round = -1; this.mode = 0; this.stage = 0; this.phase = '';
      this.epoch = 0; this.knownG = 0; this.seq = 0; this.rank = 0; this.loaded = false;
      this.t0 = null; this.lastEvT = 0; this.sameT = -1; this.sameN = 0;
      this.held = 0; this.lastSync = -1e9;
      this.fixes = 0; this.rollbacks = 0; this.syncs = 0;
    }

    get live() { return this.loaded && this.phase === 'go' && this.t0 != null && this.core.alive; }

    /// Тик стіни за годинником сервера: не раніше, ніж сервер, — кожна оцінка береться з кадра, який уже
    /// долетів (тобто запізнілого), і лишається найменша з них.
    clock(now) { return this.t0 == null ? -1 : Math.floor((now - this.t0) * 0.06); }

    clockFrom(t, now) {
      const c = now - t / 0.06;
      if (this.t0 == null || c < this.t0) this.t0 = c;
    }

    /// Вид кімнати: новий раунд або перший погляд (F5) — стіна з сервера; решта видів свою стіну не чіпають.
    adoptView(v, seat, now) {
      if (!v) return;
      const fresh = v.round !== this.round || (v.seed >>> 0) !== this.seed;
      this.round = v.round;
      this.seed = v.seed >>> 0;
      this.mode = MODE[v.garbage] || 0;
      this.stage = v.stage | 0;
      this.phase = v.phase;
      if (fresh) { this.t0 = null; this.rank = 0; }
      this.seat = seat == null ? -1 : seat;
      const b = this.seat < 0 ? null : (v.boards || []).find((x) => x.s === this.seat);
      if (!b) { this.loaded = false; return; }
      // Новіша епоха у виді — сервер уже слав fix, а кадр із ним загубився (обрив і перепідключення): без цього
      // мої пачки зі старою епохою сервер мовчки викидав би до кінця раунду.
      const load = fresh || !this.loaded || (b.fx | 0) > this.epoch;
      if (load) { this.load(b); this.lastSync = now; }
      if (!b.a) this.rank = b.rk | 0;
      if (v.phase === 'go') {
        this.clockFrom(v.t, now);
        if (load) this.syncHeld(now);
      }
    }

    load(b) {
      const c = this.core;
      c.loadWire(b, this.seed, this.mode, this.stage);
      c.seq = b.q;
      this.seq = b.q;
      this.epoch = b.fx | 0;
      this.knownG = b.g | 0;
      this.credits.length = 0;
      this.q.length = 0;
      this.qFirst = b.q + 1;
      this.snapSeq.fill(-1);
      this.keep(b.q, b.k, 0);
      this.lastEvT = b.k;
      // скільки подій сервер уже прийняв на тику k, ми не знаємо — першу свою на цьому тику зсунемо на наступний
      this.sameT = b.k;
      this.sameN = 4;
      this.loaded = true;
    }

    keep(seq, t, k) {
      const i = seq % RING;
      if (!this.snap[i]) this.snap[i] = new Core();
      this.snap[i].copyFrom(this.core);
      this.snapSeq[i] = seq;
      this.evT[i] = t;
      this.evK[i] = k;
      this.evH[i] = this.core.hash();
      this.evG[i] = this.core.gseq;
    }

    /// Крок до годинника; довго мовчали — пульс, щоб сервер бачив час.
    step(now) {
      if (!this.live) return;
      const c = this.core;
      const target = this.clock(now);
      if (target > c.tick) {
        if (target - c.tick > BEHIND) this.wantSync(now);   // спали довше, ніж сервер чекає, — він уже вів стіну сам
        c.advanceTo(target);
      }
      if (c.alive && c.tick - this.lastEvT >= PULSE) this.event(K.PULSE, now);
    }

    event(k, now) {
      const c = this.core;
      let t = Math.max(this.clock(now), c.tick);
      c.advanceTo(t);
      if (!c.alive) return false;
      if (t === this.sameT) {
        // понад 4 події на тик сервер не приймає — зайву зсуваємо на тик пізніше
        if (++this.sameN > 4) { t++; c.advanceTo(t); this.sameT = t; this.sameN = 1; if (!c.alive) return false; }
      } else { this.sameT = t; this.sameN = 1; }
      c.apply(k);
      this.seq++;
      c.seq = this.seq;
      this.keep(this.seq, t, k);
      this.q.push(t, k);
      this.lastEvT = t;
      return true;
    }

    /// Натиск: 'L' 'R' 'D' тримаються, решта — разові. Повертає, чи пішов він у стіну.
    press(key, now) {
      const bit = key === 'L' ? 1 : key === 'R' ? 2 : key === 'D' ? 4 : 0;
      if (bit) {
        if (this.held & bit) return false;
        this.held |= bit;
        if (!this.live) return false;
        return this.event(bit === 1 ? K.LEFT : bit === 2 ? K.RIGHT : K.SOFT, now);
      }
      if (!this.live) return false;
      const k = key === 'CW' ? K.CW : key === 'CCW' ? K.CCW : key === 'HARD' ? K.HARD : key === 'HOLD' ? K.HOLD : -1;
      return k >= 0 && this.event(k, now);
    }

    release(key, now) {
      const bit = key === 'L' ? 1 : key === 'R' ? 2 : key === 'D' ? 4 : 0;
      if (!bit) return;
      this.held &= ~bit;
      if (this.live && (this.core.keys & bit)) this.event(bit === 1 ? K.LEFT_UP : bit === 2 ? K.RIGHT_UP : K.SOFT_UP, now);
    }

    releaseAll(now) { this.release('L', now); this.release('R', now); this.release('D', now); }

    /// Стіна пам'ятає одні клавіші, пальці тримають інші (F5, виправлення, старт раунду) — вирівнюємо.
    syncHeld(now) {
      if (!this.live) return;
      for (const [bit, down, up] of HELD) {
        const want = this.held & bit, has = this.core.keys & bit;
        if (want && !has) this.event(down, now);
        else if (!want && has) this.event(up, now);
      }
    }

    flush() {
      while (this.q.length) {
        const n = Math.min(this.q.length, MAX_BATCH * 2);
        const batch = this.q.splice(0, n);
        const first = this.qFirst, last = first + n / 2 - 1;
        const i = last % RING;
        const known = this.snapSeq[i] === last;
        const hash = known ? this.evH[i] : undefined;
        const gseq = known ? this.evG[i] : undefined;
        this.send('j', { q: first, e: batch, h: hash, g: gseq, f: this.epoch });
        this.qFirst = last + 1;
      }
    }

    wantSync(now, force) {
      if (!force && now - this.lastSync < 1000) return;
      this.lastSync = now;
      this.syncs++;
      this.send('sync');
    }

    /// Кадр: годинник, посилки мені (відкат), виправлення, моє місце, звірка хешу.
    onFrame(f, now) {
      if (!f) return;
      const prev = this.phase;
      this.phase = f.ph;
      if (f.ph === 'go') this.clockFrom(f.t, now);
      if (this.seat < 0) return;
      for (const ev of f.ev || []) {
        if (ev[1] !== this.seat) continue;
        if (ev[0] === 'g') this.credit(ev, now);
        else if (ev[0] === 'f') this.fix(ev[2], now);
        else if (ev[0] === 'o') this.rank = ev[2];
      }
      if (f.ph === 'go' && this.loaded) {
        for (const b of f.b || []) if (b.s === this.seat) this.check(b, now);
      }
      if (prev !== 'go' && f.ph === 'go') this.syncHeld(now);
    }

    check(b, now) {
      const i = b.q % RING;
      if (this.snapSeq[i] !== b.q || this.evT[i] !== b.k || this.evG[i] !== b.g) return;
      if ((this.evH[i] >>> 0) !== (b.x >>> 0)) this.wantSync(now);
    }

    /// Посилка сміття в моєму часі: після події after, на тику at. Відкочуюсь і повторюю свої натиски.
    credit(ev, now) {
      const g = ev[2];
      if (g <= this.knownG || !this.loaded) return;
      this.knownG = g;
      const from = ev[7], at = ev[5];
      this.credits.push({ after: ev[6], at, g, rows: ev[3], hole: ev[4], ripeAt: from === -1 ? at : at + RIPE, from });
      if (this.credits.length > 64) this.credits = this.credits.filter((c) => c.after > this.seq - RING);
      this.rebase(ev[6], now);
    }

    creditsAfter(s) {
      const c = this.core;
      for (const cr of this.credits) {
        if (cr.after !== s) continue;
        c.advanceTo(cr.at);
        c.addCredit(cr.g, cr.rows, cr.hole, cr.ripeAt, cr.from);
      }
    }

    rebase(a, now) {
      const i = a % RING;
      if (this.snapSeq[i] !== a || a > this.seq) { this.wantSync(now, true); return; }
      const c = this.core;
      const tickNow = c.tick, wasAlive = c.alive;
      const clears = c.clearN, ins = c.inserted, locks = c.locks, drops = c.drops;
      c.copyFrom(this.snap[i]);
      this.creditsAfter(a);
      for (let s = a + 1; s <= this.seq; s++) {
        const j = s % RING;
        c.advanceTo(this.evT[j]);
        c.apply(this.evK[j]);
        c.seq = s;
        this.snap[j].copyFrom(c);
        this.evH[j] = c.hash();
        this.evG[j] = c.gseq;
        this.creditsAfter(s);
      }
      c.advanceTo(tickNow);
      // повтор — не нові події: ефекти й звуки вже були
      c.clearN = clears; c.inserted = ins; c.locks = locks; c.drops = drops;
      if (wasAlive && !c.alive) c.ver++;
      this.rollbacks++;
    }

    fix(w, now) {
      // цю епоху вже взято (вид після перепідключення приніс її раніше за кадр) — удруге не відкочуємось
      if (this.loaded && (w.fx | 0) <= this.epoch) return;
      this.load(w);
      // стіну, яку вів сервер, клієнт зараз наздожене своїм годинником (гравітацією) — просити ще одну звірку
      // одразу по виправленню нема чого: сервер пройде ті самі кроки з тих самих подій
      this.lastSync = now;
      this.fixes++;
      this.syncHeld(now);
    }
  }

  window.BricksCore = {
    Core, Net, bag, nextRand, gen, genCredits, run, suite, botPlan, botScore, K, BOT_DELAY,
    consts: { W, H, VIS, DAS, ARR, SOFTG, LOCKD, MAXRES, CLEART, RIPE, MAXINS, AHEAD, BEHIND, SPRINTG, SPRINT_LINES, SUDDEN, CAP },
  };

  // Стенд детермінізму підставляє порожній HGames — далі лише модуль гри.
  if (!window.HGames || !HGames.register || !HGames.ui) return;

  // =============================================================================================
  // Модуль гри: розкладка, малювання, керування
  // =============================================================================================

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1" y="9.5" width="6.5" height="5" rx="1" fill="var(--clay)"/>'
    + '<rect x="8.5" y="9.5" width="6.5" height="5" rx="1" fill="var(--clay)"/>'
    + '<rect x="4.75" y="4" width="6.5" height="5" rx="1" fill="var(--clay)"/>'
    + '<rect x="4.75" y="0.5" width="6.5" height="3" rx="1" fill="var(--accent)"/>'
    + '<path d="M1 9.5h14M4.75 9V4" stroke="var(--bg2)" stroke-width=".6"/></svg>';

  const SEATS = ['теракота', 'бірюза', 'олива', 'кобальт'];
  const SEAT_VARS = [['--bk-seat0', '#c5763a'], ['--bk-seat1', '#3fb1a8'], ['--bk-seat2', '#8aa83d'], ['--bk-seat3', '#4f78d1']];
  // глазур: I O T S Z J L, 8 — сира глина (сміття)
  const GLAZE = [['--bk-i', '#4fc3c7'], ['--bk-o', '#e6c15a'], ['--bk-t', '#9a6fc9'], ['--bk-s', '#7bb661'],
    ['--bk-z', '#d9534f'], ['--bk-j', '#4f78d1'], ['--bk-l', '#e08a3c'], ['--bk-g', '#7a6a5e']];
  const ROWS = 22;           // малюємо ряди 0..21: два над стіною — там з'являються фігурки
  const LBL_H = 40;          // підпис чужої стіни: два рядки (нік і ★; ряди й ціль)
  const STRIP_W = 96;        // стовпчик смужок чужих стін на телефоні
  const cssPx = (name) => { try { return parseFloat(getComputedStyle(document.body).getPropertyValue(name)) || 0; } catch { return 0; } };
  const TICK_MS = 1000 / 60;

  const KEYS = {
    ArrowLeft: 'L', KeyA: 'L', ArrowRight: 'R', KeyD: 'R', ArrowDown: 'D', KeyS: 'D',
    Space: 'HARD', KeyW: 'HARD', ArrowUp: 'CW', KeyX: 'CW', KeyE: 'CW', KeyZ: 'CCW', KeyQ: 'CCW',
    KeyC: 'HOLD', ShiftLeft: 'HOLD', ShiftRight: 'HOLD',
  };
  // синтетичний keydown буває без code — тоді за key (і на українській розкладці теж)
  const BY_KEY = {
    arrowleft: 'L', a: 'L', ф: 'L', arrowright: 'R', d: 'R', в: 'R', arrowdown: 'D', s: 'D', і: 'D',
    ' ': 'HARD', w: 'HARD', ц: 'HARD', arrowup: 'CW', x: 'CW', ч: 'CW', e: 'CW', у: 'CW', z: 'CCW', я: 'CCW', q: 'CCW', й: 'CCW',
    c: 'HOLD', с: 'HOLD', shift: 'HOLD',
  };
  const keyOf = (e) => KEYS[e.code] || BY_KEY[String(e.key || '').toLowerCase()];

  const reduced = () => { try { return matchMedia('(prefers-reduced-motion: reduce)').matches; } catch { return false; } };
  const coarse = () => HGames.ui.coarse();
  const clockText = (ticks, frac) => {
    const cs = Math.max(0, Math.floor(ticks * 100 / 60));
    const m = Math.floor(cs / 6000), s = Math.floor(cs / 100) % 60, f = cs % 100;
    return m + ':' + String(s).padStart(2, '0') + (frac ? ',' + String(f).padStart(2, '0') : '');
  };
  /// 54.87 → «0:54,87» — рівно як у рядку Журналу.
  const secText = (sec) => {
    const cs = Math.round(sec * 100);
    return Math.floor(cs / 6000) + ':' + String(Math.floor(cs / 100) % 60).padStart(2, '0') + ',' + String(cs % 100).padStart(2, '0');
  };
  const rowsWord = (n) => {
    const t = n % 100, o = n % 10;
    return n + ' ' + (t > 10 && t < 20 ? 'рядів' : o === 1 ? 'ряд' : o >= 2 && o <= 4 ? 'ряди' : 'рядів');
  };

  // ---------- кольори й спрайти цеглинок ----------

  function hexRgb(c) {
    const m = /^#?([0-9a-f]{6})$/i.exec(String(c).trim());
    if (!m) return [128, 128, 128];
    const n = parseInt(m[1], 16);
    return [n >> 16, (n >> 8) & 255, n & 255];
  }
  const mix = (rgb, to, k) => 'rgb(' + rgb.map((v, i) => Math.round(v + (to[i] - v) * k)).join(',') + ')';

  function palette(ctx) {
    const css = (n, f) => ctx.css(n, f);
    return {
      bg: css('--bg', '#0f1f18'), bg2: css('--bg2', '#16291f'), panel: css('--panel', '#1c3328'),
      line: css('--line', '#2f4d3d'), text: css('--text', '#ecf1ea'), muted: css('--muted', '#9db3a5'),
      accent: css('--accent', '#f4c542'), danger: css('--danger', '#e57373'), ok: css('--ok', '#7bd389'),
      glaze: GLAZE.map(([n, f]) => css(n, f)), seats: SEAT_VARS.map(([n, f]) => css(n, f)),
    };
  }

  /// Цеглинка кожного кольору на поточну клітинку — один раз на зміну розміру, далі лише drawImage.
  function sprites(pal, cell, dpr) {
    const px = Math.max(2, Math.round(cell * dpr));
    const make = () => { const c = document.createElement('canvas'); c.width = px; c.height = px; return c; };
    const brick = [null], ghost = [null];
    for (let i = 0; i < 8; i++) {
      const rgb = hexRgb(pal.glaze[i]);
      const c = make(), g = c.getContext('2d');
      const r = px * 0.16, pad = Math.max(0.5, px * 0.04);
      g.fillStyle = mix(rgb, [0, 0, 0], 0.35);
      g.beginPath(); g.roundRect(pad, pad, px - pad * 2, px - pad * 2, r); g.fill();
      g.fillStyle = pal.glaze[i];
      g.beginPath(); g.roundRect(pad, pad, px - pad * 2, px - pad * 2 - Math.max(1, px * 0.1), r); g.fill();
      g.fillStyle = mix(rgb, [255, 255, 255], 0.38);
      g.fillRect(pad + r * 0.6, pad + Math.max(0.5, px * 0.04), px - pad * 2 - r * 1.2, Math.max(1, px * 0.07));
      if (i < 7) {
        // глазур блищить
        g.fillStyle = 'rgba(255,255,255,.22)';
        g.beginPath(); g.ellipse(px * 0.33, px * 0.32, px * 0.16, px * 0.09, -0.5, 0, Math.PI * 2); g.fill();
      } else {
        // сира глина з тріщинкою
        g.strokeStyle = mix(rgb, [0, 0, 0], 0.45);
        g.lineWidth = Math.max(1, px * 0.05);
        g.beginPath(); g.moveTo(px * 0.22, px * 0.3); g.lineTo(px * 0.45, px * 0.52); g.lineTo(px * 0.38, px * 0.74); g.stroke();
      }
      brick.push(c);
      const gc = make(), gg = gc.getContext('2d');
      gg.globalAlpha = 0.3;
      gg.fillStyle = pal.glaze[i];
      gg.beginPath(); gg.roundRect(pad, pad, px - pad * 2, px - pad * 2, r); gg.fill();
      gg.globalAlpha = 0.85;
      gg.strokeStyle = pal.glaze[i];
      gg.lineWidth = Math.max(1, px * 0.07);
      gg.beginPath(); gg.roundRect(pad + gg.lineWidth / 2, pad + gg.lineWidth / 2, px - pad * 2 - gg.lineWidth, px - pad * 2 - gg.lineWidth, r); gg.stroke();
      ghost.push(gc);
    }
    return { brick, ghost, cell, dpr };
  }

  /// Тло стіни — шви між цеглинами і межа ряду 20; раз на розмір.
  function wallBack(pal, cell, dpr) {
    const w = Math.round(10 * cell * dpr), h = Math.round(ROWS * cell * dpr);
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    const g = c.getContext('2d');
    g.fillStyle = pal.bg2;
    g.fillRect(0, 0, w, h);
    g.strokeStyle = pal.line;
    g.globalAlpha = 0.5;
    g.lineWidth = 1;
    const k = cell * dpr;
    g.beginPath();
    for (let y = 1; y < ROWS; y++) { g.moveTo(0, Math.round(y * k) + 0.5); g.lineTo(w, Math.round(y * k) + 0.5); }
    for (let row = 0; row < ROWS; row++) {
      const off = row % 2 ? k / 2 : 0;           // кладка зі зсувом, як у справжній стіні
      for (let x = 0; x <= 10; x++) {
        const xx = Math.round(x * k + off) + 0.5;
        if (xx <= 0 || xx >= w) continue;
        g.moveTo(xx, row * k); g.lineTo(xx, (row + 1) * k);
      }
    }
    g.stroke();
    g.globalAlpha = 1;
    g.fillStyle = 'rgba(0,0,0,.28)';
    g.fillRect(0, 0, w, 2 * k);                  // над стіною — запас, темніше
    g.strokeStyle = pal.muted;
    g.globalAlpha = 0.6;
    g.lineWidth = Math.max(1, dpr);
    g.beginPath(); g.moveTo(0, Math.round(2 * k) + 0.5); g.lineTo(w, Math.round(2 * k) + 0.5); g.stroke();
    return c;
  }

  // ---------- стан картки ----------

  function state(root, ctx) {
    if (!root._bk) {
      const st = root._bk = {
        ctx, net: null, sprint: ctx.room.game === 'bricks-sprint' || ctx.room.game === 'bricks-daily',
        daily: ctx.room.game === 'bricks-daily',
        view: null, frame: null, phase: '', inTicks: 0, round: -1, seed: 0, lvl: 0, sd: -1, fwall: 0, fAt: 0,
        others: {}, order: [], mySeat: null,
        el: {}, cells: 0, layout: '', pal: null, spr: null, sprMini: null, back: null, backMini: null,
        dirty: true, lastVer: -1, raf: 0, timer: 0, keyup: null, onResize: null, onBlur: null,
        fx: { parts: [], labels: [], shake: 0, trail: null, flash: 0, go: 0, splat: null },
        sound: soundPref(), audio: null, perf: { n: 0, sum: 0, max: 0, ring: new Float64Array(300), i: 0 },
        readySent: 0, lastRender: 0, lastClock: 0, touch: null, sumShown: false,
        pb: null, run: null, pbDelta: null, pbNew: null, pbGap: 0, tossE: 0, tossAt: 0,
      };
      st.net = new Net((a, p) => st.ctx && st.ctx.input(a, p));
    }
    root._bk.ctx = ctx;
    ctx._bk = root._bk;
    return root._bk;
  }

  function soundPref() { try { return localStorage.getItem('bricksSound') !== '0'; } catch { return true; } }

  /// Чужа (або глядачева) стіна, як її видно з кадрів.
  function other(st, s) {
    let o = st.others[s];
    if (!o) {
      o = st.others[s] = {
        s, cells: new Uint8Array(240), mask: new Uint16Array(24), p: null, hd: -1, n: [], l: 0, pd: 0, rp: 0, a: 1, rk: 0,
        k: 0, v: -1, at: 0, nick: '', dirty: true, drawnY: null, sn: 0, rc: 0,
      };
    }
    return o;
  }

  function setRows(o, rows) {
    o.cells.fill(0);
    o.mask.fill(0);
    for (let y = 0; y < rows.length && y < 24; y++) {
      const s = rows[y];
      for (let x = 0; x < 10; x++) {
        const v = s.charCodeAt(x) - 48;
        if (v > 0) { o.cells[y * 10 + x] = v; o.mask[y] |= 1 << x; }
      }
    }
  }

  function takeBoard(st, b, full) {
    const o = other(st, b.s);
    if (b.nk) o.nick = b.nk;
    o.a = b.a; o.l = b.l;
    if (b.r) setRows(o, b.r);
    o.p = b.p; o.hd = b.hd; o.n = b.n || o.n; o.k = b.k;
    if (full) {
      o.rk = b.rk; o.sn = b.sn; o.rc = b.rc;
      let pd = 0, rp = 0;
      for (const c of b.cr || []) { pd += c[1]; if (c[3] <= b.k) rp += c[1]; }
      o.pd = pd; o.rp = rp;
    } else { o.pd = b.pd; o.rp = b.rp; }
    o.at = performance.now();
    o.dirty = true;
  }

  // ---------- розкладка ----------

  function seatsInRound(st) {
    const v = st.view;
    return v && v.boards ? v.boards.map((b) => b.s) : [];
  }

  function layout(root, st) {
    const ctx = st.ctx;
    const mine = ctx.mine && st.view && (st.view.boards || []).some((b) => b.s === ctx.seat);
    const seats = seatsInRound(st);
    const others = seats.filter((s) => !mine || s !== ctx.seat);
    const W = Math.max(280, root.clientWidth || 320);
    const top = root.getBoundingClientRect().top;
    const vh = document.documentElement.clientHeight || window.innerHeight || 800;
    const narrow = W < 640;
    const touch = mine && coarse() && ctx.playing;
    // Унизу: на телефоні — міні-плеєр і вкладки сайту (фіксовані, стіна під ними не видна) і кнопки під палець,
    // статус і «Встати» можна догорнути; на ПК — статус і кнопки картки, щоб усе було в одному екрані.
    const chrome = cssPx('--tabs-h') + cssPx('--mini-h');
    // смужка підказок пада (Дека) висить унизу поверх сторінки — статус і кнопки картки мусять лягти над нею
    const padBar = padBarH();
    // Під стінами на ПК — статус і кнопки картки та нижній відступ сторінки: їх міряємо (від розміру стін вони не
    // залежать), статус — одним рядком (на відліку він буває у два). Решту похибки (підписи, секундомір спринту)
    // знімає st.fitCut із build(). Було: сталі 86 px, і дуель на 1280…1920 гортала сторінку на 10–28 px (прохід 28.09).
    let under = 86 + chrome;
    const card = root.closest('.gtable');
    if (card && !(narrow && touch)) {
      let cardBelow = card.getBoundingClientRect().bottom - root.getBoundingClientRect().bottom;
      const se = card.querySelector('.gstatus');
      if (se) {
        const sh = se.getBoundingClientRect().height, lh = parseFloat(getComputedStyle(se).lineHeight) || 18;
        if (sh > lh + 2) cardBelow -= sh - lh;
      }
      under = Math.max(40, cardBelow) + 18 + chrome;
    }
    const below = (narrow && touch ? chrome + 10 : under) + (touch ? 64 : 0) + padBar, hud = st.sprint ? 50 : 30;   // у спринті HUD вищий: великий секундомір
    let hAvail = Math.max(260, vh - Math.max(0, top) - below - hud - (st.fitCut || 0));
    let c;
    let kind;
    let mini = 0;
    let strips = false;
    if (!mine) {
      kind = 'watch';
      const n = Math.max(1, others.length);
      const perRow = narrow ? Math.min(n, 2) : n;
      const rows = Math.ceil(n / perRow);
      c = Math.floor(Math.min((W - 16 * perRow) / (perRow * 10.4), (hAvail - rows * LBL_H) / (rows * ROWS)));
    } else if (narrow) {
      // Телефон: своя стіна з кишенею праворуч, поруч — чужі дрібно (одна або дві колонки), кнопки — рядком під усім.
      kind = 'narrow';
      const n = others.length;
      // Двоє й більше чужих на телефоні — не мікростіни з клітинкою 4 px і ніком з однієї літери, а смужки
      // небезпеки над своєю стіною: ім'я, висота стіни, що летить. Одна чужа стіна (дуель) — поруч, як була.
      // Стовпчик смужок стоїть праворуч від своєї стіни, де й так лишалось порожньо, — висоти стіні не забирає.
      strips = n >= 2;
      const k = n === 1 ? 0.5 : 0;
      const minisW = (m) => (n === 1 ? Math.max(3, Math.round(m * 0.36)) + 10 * m + 6 : strips ? STRIP_W + 6 : 0);
      // найбільша клітинка, за якої своя стіна з кишенею й чужа (що дрібнішає разом із нею) влазять у ширину
      c = Math.floor(hAvail / ROWS);
      while (c > 10 && 13.3 * c + minisW(Math.max(4, Math.floor(c * k))) + 4 > W) c--;
      mini = n === 1 ? Math.max(4, Math.floor(c * k)) : 0;
    } else {
      kind = 'wide';
      const extra = others.length === 0 ? 0 : others.length === 1 ? 10.8 : 11.2;
      // три чужі стіни — у два ряди, і кожен ряд має ще й підпис: своя стіна мусить лишити їм місце
      const extraH = others.length > 2 ? 2 * LBL_H + 12 - 30 : 0;
      c = Math.floor(Math.min((W - 40) / (15.6 + extra), (hAvail - extraH) / ROWS));
    }
    c = Math.max(kind === 'narrow' ? 10 : 12, Math.min(34, c | 0));
    if (kind === 'wide') mini = others.length > 1 ? Math.max(6, Math.floor(c / 2)) : c;
    if (kind === 'watch') mini = c;
    // Три чужі на широкому екрані (Full HD): рядком вони більші, ніж квадратом 2×2 з діркою. Рахуємо ширину своєї
    // стіни з кишенею й чергою (як у build) і беремо рядок, лише якщо він справді дає більшу клітинку.
    let row3 = false;
    if (kind === 'wide' && others.length === 3) {
      const pc = Math.max(7, Math.round(c * 0.56)), side = Math.round(pc * 4.6);
      const own = side * 2 + Math.max(4, Math.round(c * 0.25)) * 2 + Math.max(4, Math.round(c * 0.36)) + 10 * c + 18;
      const m3 = Math.min(Math.floor((W - own - 30) / 31.2), Math.floor(c * 0.7), Math.floor((ROWS * c - LBL_H) / ROWS));
      if (m3 > mini) { mini = m3; row3 = true; }
    }
    const pad = padish();
    const sig = [kind, c, mini, row3, strips, seats.join(','), mine ? ctx.seat : -1, touch, st.sprint, pad].join('|');
    return { kind, c, mini, mine, others, seats, touch, sig, narrow, row3, strips, pad };
  }

  /// Висота смужки підказок пада, якщо вона зараз на екрані (0 — пада нема або смужку сховали).
  function padBarH() {
    if (!document.body.classList.contains('pad-on')) return 0;
    const hb = document.querySelector('.padhints');
    return hb && !hb.hidden && hb.offsetHeight ? hb.offsetHeight + 14 : 0;
  }

  function build(root, st) {
    const L = layout(root, st);
    if (L.sig === st.layout) return L;
    st.layout = L.sig;
    st.L = L;
    const ctx = st.ctx;
    root.innerHTML = '';
    const scene = document.createElement('div');
    scene.className = 'bricks-scene bricks-' + L.kind + (st.sprint ? ' bricks-sprintmode' : '');
    root.appendChild(scene);
    st.el = { scene, boards: {}, strips: null };
    // канваси рівно свого розміру в CSS-пікселях — не розтягуються, тож досить справжнього DPR (не більше 2)
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    st.dpr = dpr;

    if (L.mine) {
      const me = document.createElement('div');
      me.className = 'bricks-me';
      const hudEl = hudBuild(st);
      // на телефоні HUD — рядком над усією сценою, інакше в стовпчику стіни він переноситься в три рядки
      (L.kind === 'narrow' ? scene : me).appendChild(hudEl);
      const cv = document.createElement('canvas');
      cv.className = 'bricks-cv bricks-mine';
      me.appendChild(cv);
      scene.appendChild(me);
      if (L.strips) scene.appendChild(stripsBuild(st, L.others));
      st.el.me = me;
      st.el.hud = hudEl;
      const pc = Math.max(7, Math.round(L.c * 0.56));
      const side = Math.round(pc * 4.6);
      const bar = Math.max(4, Math.round(L.c * 0.36));
      const gap = Math.max(4, Math.round(L.c * 0.25));
      const geo = L.kind === 'wide'
        ? { c: L.c, pc, x0: side + gap + bar, bar: side + gap, holdX: 0, nextX: side + gap + bar + 10 * L.c + gap, w: side * 2 + gap * 2 + bar + 10 * L.c }
        : { c: L.c, pc, x0: bar, bar: 0, holdX: bar + 10 * L.c + gap, nextX: bar + 10 * L.c + gap, w: bar + 10 * L.c + gap + side };
      geo.h = ROWS * L.c;
      geo.side = side;
      geo.barW = bar;
      sizeCanvas(cv, geo.w, geo.h, dpr);
      st.el.mine = { cv, g: cv.getContext('2d'), geo };
      if (L.touch && L.kind !== 'narrow') buildTouch(me, st);
      if (coarse()) gestures(cv, st);
    }
    if (L.others.length && !L.strips) {
      const box = document.createElement('div');
      box.className = 'bricks-others n' + L.others.length;
      scene.appendChild(box);
      for (const s of L.others) {
        const bd = document.createElement('div');
        bd.className = 'bricks-bd bk' + s;
        bd.dataset.s = s;
        bd.innerHTML = '<div class="bricks-lbl"></div>';
        const cv = document.createElement('canvas');
        cv.className = 'bricks-cv';
        bd.appendChild(cv);
        box.appendChild(bd);
        if (L.mine && !st.sprint) cv.addEventListener('click', () => tossAt(st, s));
        const c = L.mini;
        const bar = Math.max(3, Math.round(c * 0.36));
        const geo = { c, x0: bar, bar: 0, barW: bar, w: bar + 10 * c, h: ROWS * c, mini: true };
        sizeCanvas(cv, geo.w, geo.h, dpr);
        bd.style.width = geo.w + 'px';   // довгий підпис не розпирає колонку — обрізається трьома крапками
        st.el.boards[s] = { bd, cv, g: cv.getContext('2d'), geo, lbl: bd.querySelector('.bricks-lbl') };
        other(st, s).dirty = true;
      }
      if ((L.kind === 'wide' || L.kind === 'narrow') && L.others.length > 1) box.style.gridTemplateColumns = L.row3 ? 'repeat(3, auto)' : 'repeat(2, auto)';
    }
    if (L.mine && L.touch && L.kind === 'narrow') buildTouch(scene, st);
    if (!L.seats.length && !st.sprint) scene.appendChild(lobbyHint());
    const sum = document.createElement('div');
    sum.className = 'bricks-summary';
    sum.hidden = true;
    scene.appendChild(sum);
    st.el.sum = sum;
    st.spr = st.sprMini = st.back = st.backMini = null;
    st.dirty = true;
    // Сторінка все ж вилазить за екран — меншаємо стіни рівно на стільки й перебудовуємо одразу, ще до малювання.
    // Лише ПК (на телефоні сторінку гортають) і не більше двох спроб; скидається на зміну розміру вікна.
    if (!coarse() && root.offsetParent && (st.fitTries = (st.fitTries || 0) + 1) <= 2) {
      const doc = document.documentElement, over = doc.scrollHeight - (doc.clientHeight || window.innerHeight);
      if (over > 0 && over < 300) { st.fitCut = (st.fitCut || 0) + over; st.layout = ''; return build(root, st); }
    }
    st.fitTries = 0;
    return L;
  }

  /// Лобі: поки збираються, нагадати, як грати, — і що самому є спринт у Соло (стіл на одного каркас не почне).
  function lobbyHint() {
    const d = document.createElement('div');
    d.className = 'bricks-lobby';
    const keys = padish() ? '✥ рух і м’яко · Ⓐ крутити · Ⓧ кинути · Ⓨ сховати · LB RB оберти'
      : coarse() ? 'Кнопки під стіною; тап по стіні — крутити, свайп униз — кинути, угору — сховати'
        : '← → рухати · ↑ або X крутити · пробіл — кинути · C — сховати';
    d.innerHTML = '<div class="bricks-lobby-wall" aria-hidden="true">' + '<i></i>'.repeat(6) + '</div><ul>'
      + '<li>🧱 ' + keys + '</li>'
      + '<li>💥 Закрив два ряди й більше — сусідові за стрілкою знизу лізе сміття, а свої ряди гасять те, що летить тобі</li>'
      + '<li>🏁 Хто завалився — вибув, останній бере раунд; сідайте вдвох-учотирьох</li></ul>'
      + '<div class="bricks-lobby-solo">Сам? «Цеглини: 40 рядів» — у Соло, на секундомір</div>';
    return d;
  }

  function sizeCanvas(cv, w, h, dpr) {
    cv.width = Math.round(w * dpr);
    cv.height = Math.round(h * dpr);
    cv.style.width = w + 'px';
    cv.style.height = h + 'px';
  }

  // ---------- палець: кнопки й жести ----------

  function buildTouch(me, st) {
    const pad = document.createElement('div');
    pad.className = 'bricks-touch';
    const btns = [['L', '◁', 'ліворуч'], ['D', '▽', 'м’яко вниз'], ['R', '▷', 'праворуч'], ['CCW', '↺', 'проти годинникової'],
      ['CW', '↻', 'за годинниковою'], ['HOLD', '⧉', 'сховати'], ['HARD', '⤓', 'кинути']];
    pad.innerHTML = btns.map(([k, t, a]) => '<button type="button" data-k="' + k + '" aria-label="' + a + '" data-pad-skip>' + t + '</button>').join('');
    const held = {};
    pad.addEventListener('pointerdown', (e) => {
      const b = e.target.closest('button');
      if (!b) return;
      e.preventDefault();
      const k = b.dataset.k;
      audioOn(st);
      if (st.sprint && st.phase === 'ready') { ready(st); return; }
      if (k === 'L' || k === 'R' || k === 'D') {
        try { b.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
        held[e.pointerId] = k;
        st.net.press(k, performance.now());
      } else st.net.press(k, performance.now());
      b.classList.add('on');
      st.dirty = true;
    });
    const up = (e) => {
      const k = held[e.pointerId];
      const b = e.target.closest && e.target.closest('button');
      if (b) b.classList.remove('on');
      if (!k) return;
      delete held[e.pointerId];
      st.net.release(k, performance.now());
    };
    pad.addEventListener('pointerup', up);
    pad.addEventListener('pointercancel', up);
    pad.addEventListener('contextmenu', (e) => e.preventDefault());
    me.appendChild(pad);
  }

  /// Жести на своїй стіні: тап — крутити, два пальці — проти, вбік — крок на кожні 0,8 клітинки,
  /// тягнути вниз — м'яко, різко вниз — кинути, угору — сховати.
  function gestures(cv, st) {
    const pts = new Map();
    let g = null;
    cv.style.touchAction = 'none';
    cv.addEventListener('pointerdown', (e) => {
      audioOn(st);
      pts.set(e.pointerId, true);
      if (st.sprint && st.phase === 'ready') { ready(st); return; }
      if (pts.size === 2) { if (g) g.two = true; return; }
      g = { id: e.pointerId, x: e.clientX, y: e.clientY, t: performance.now(), sx: e.clientX, moved: false, soft: false, two: false };
      try { cv.setPointerCapture(e.pointerId); } catch { /* нічого */ }
    });
    cv.addEventListener('pointermove', (e) => {
      if (!g || e.pointerId !== g.id) return;
      const c = st.el.mine ? st.el.mine.geo.c : 20;
      const step = c * 0.8;
      const now = performance.now();
      while (e.clientX - g.sx >= step) { g.sx += step; tapKey(st, 'R', now); g.moved = true; }
      while (g.sx - e.clientX >= step) { g.sx -= step; tapKey(st, 'L', now); g.moved = true; }
      const dy = e.clientY - g.y;
      if (!g.soft && dy > c * 1.2 && Math.abs(e.clientX - g.x) < c) {
        const v = dy / Math.max(1, now - g.t);
        if (v <= 1.2) { g.soft = true; st.net.press('D', now); }
      }
    });
    const end = (e) => {
      pts.delete(e.pointerId);
      if (!g || e.pointerId !== g.id) return;
      const now = performance.now();
      const dt = now - g.t, dx = e.clientX - g.x, dy = e.clientY - g.y;
      if (g.soft) st.net.release('D', now);
      else if (g.two) st.net.press('CCW', now);
      else if (dy >= 40 && dy / Math.max(1, dt) > 1.2 && Math.abs(dy) > Math.abs(dx)) st.net.press('HARD', now);
      else if (dy <= -40 && Math.abs(dy) > Math.abs(dx)) st.net.press('HOLD', now);
      else if (!g.moved && dt < 250 && Math.abs(dx) < 10 && Math.abs(dy) < 10) st.net.press('CW', now);
      g = null;
      st.dirty = true;
    };
    cv.addEventListener('pointerup', end);
    cv.addEventListener('pointercancel', (e) => { pts.delete(e.pointerId); if (g && g.soft) st.net.release('D', performance.now()); g = null; });
  }

  function tapKey(st, k, now) {
    st.net.press(k, now);
    st.net.release(k, now);
  }

  function ready(st) {
    const now = performance.now();
    if (now - st.readySent < 800) return;
    st.readySent = now;
    st.ctx.input('ready');
  }

  function rematch(st) {
    const now = performance.now();
    if (now - st.readySent < 1500 || !window.HGames || !HGames.call) return;
    st.readySent = now;
    HGames.call('Rematch', st.ctx.room.id).catch(() => {});
  }

  // ---------- звук (WebAudio, тихо, лише після жесту) ----------

  function audioOn(st) {
    if (st.audio || !st.sound) return;
    try { st.audio = new (window.AudioContext || window.webkitAudioContext)(); } catch { st.audio = null; }
  }

  function beep(st, kind) {
    const a = st.audio;
    if (!a || !st.sound || a.state === 'suspended') { if (a && a.state === 'suspended') a.resume().catch(() => {}); if (!a || !st.sound) return; }
    const vol = st.ctx.mine ? 0.15 : 0.05;
    const t = a.currentTime;
    const tone = (f, at, dur, type) => {
      const o = a.createOscillator(), g = a.createGain();
      o.type = type || 'sine';
      o.frequency.setValueAtTime(f, t + at);
      g.gain.setValueAtTime(0.0001, t + at);
      g.gain.exponentialRampToValueAtTime(vol, t + at + 0.01);
      g.gain.exponentialRampToValueAtTime(0.0001, t + at + dur);
      o.connect(g).connect(a.destination);
      o.start(t + at);
      o.stop(t + at + dur + 0.02);
    };
    if (kind === 'lock') {
      const n = a.createBufferSource(), buf = a.createBuffer(1, Math.floor(a.sampleRate * 0.05), a.sampleRate);
      const d = buf.getChannelData(0);
      for (let i = 0; i < d.length; i++) d[i] = (Math.random() * 2 - 1) * (1 - i / d.length);
      n.buffer = buf;
      const f = a.createBiquadFilter(); f.type = 'lowpass'; f.frequency.value = 600;
      const g = a.createGain(); g.gain.value = vol * 1.4;
      n.connect(f).connect(g).connect(a.destination);
      n.start(t);
    } else if (kind === 'line') tone(880, 0, 0.09);
    else if (kind === 'four') { tone(880, 0, 0.1); tone(1320, 0.1, 0.14); }
    else if (kind === 'garbage') tone(90, 0, 0.12, 'triangle');
    else if (kind === 'out') { tone(523, 0, 0.14, 'triangle'); tone(415, 0.15, 0.14, 'triangle'); tone(330, 0.3, 0.25, 'triangle'); }
  }

  // ---------- малювання ----------

  function ensureArt(st) {
    const need = st.el.mine ? st.el.mine.geo.c : 0;
    const needMini = Object.values(st.el.boards)[0] ? Object.values(st.el.boards)[0].geo.c : 0;
    if (!st.pal) st.pal = palette(st.ctx);
    if (need && (!st.spr || st.spr.cell !== need)) { st.spr = sprites(st.pal, need, st.dpr); st.back = wallBack(st.pal, need, st.dpr); }
    if (st.el.mine) st.sprPc = st.sprPc && st.sprPc.cell === st.el.mine.geo.pc ? st.sprPc : sprites(st.pal, st.el.mine.geo.pc, st.dpr);
    if (needMini && (!st.sprMini || st.sprMini.cell !== needMini)) { st.sprMini = sprites(st.pal, needMini, st.dpr); st.backMini = wallBack(st.pal, needMini, st.dpr); }
  }

  const rowY = (geo, y) => (ROWS - 1 - y) * geo.c;

  /// Клітинки стіни: ряди над стіною — напівпрозорі.
  function drawCells(g, geo, spr, cells, dim) {
    const c = geo.c, img = spr.brick;
    for (let y = 0; y < ROWS; y++) {
      const base = y * 10;
      let any = false;
      for (let x = 0; x < 10; x++) if (cells[base + x]) { any = true; break; }
      if (!any) continue;
      if (y >= VIS) g.globalAlpha = 0.6;
      const yy = geo.y0 + rowY(geo, y);
      for (let x = 0; x < 10; x++) {
        const v = cells[base + x];
        if (v) g.drawImage(dim ? img[8] : img[v], geo.x0 + x * c, yy, c, c);
      }
      if (y >= VIS) g.globalAlpha = 1;
    }
  }

  function drawPiece(g, geo, spr, type, bx, by, rot, ghost) {
    const o = (type * 4 + rot) * 8, c = geo.c;
    const img = ghost ? spr.ghost[type + 1] : spr.brick[type + 1];
    for (let i = 0; i < 8; i += 2) {
      const y = by + SHAPES[o + i + 1];
      if (y >= ROWS) continue;
      g.drawImage(img, geo.x0 + (bx + SHAPES[o + i]) * c, geo.y0 + rowY(geo, y), c, c);
    }
  }

  /// Прев'ю фігурки (кишеня, наступні) по центру прямокутника.
  function drawPreview(g, spr, type, x, y, w, h, pc, alpha) {
    if (type < 0) return;
    const o = type * 32;
    let minX = 9, maxX = -1, minY = 9, maxY = -1;
    for (let i = 0; i < 8; i += 2) {
      const sx = SHAPES[o + i], sy = SHAPES[o + i + 1];
      if (sx < minX) minX = sx; if (sx > maxX) maxX = sx; if (sy < minY) minY = sy; if (sy > maxY) maxY = sy;
    }
    const bw = (maxX - minX + 1) * pc, bh = (maxY - minY + 1) * pc;
    const ox = x + (w - bw) / 2, oy = y + (h - bh) / 2;
    if (alpha < 1) g.globalAlpha = alpha;
    for (let i = 0; i < 8; i += 2) {
      const sx = SHAPES[o + i] - minX, sy = maxY - SHAPES[o + i + 1];
      g.drawImage(spr.brick[type + 1], ox + sx * pc, oy + sy * pc, pc, pc);
    }
    if (alpha < 1) g.globalAlpha = 1;
  }

  /// Смуга вхідного сміття біля лівого краю стіни: сіре — летить, червоне — вже дозріло.
  function drawBar(g, geo, pd, rp, pal) {
    const x = geo.bar, w = geo.barW - 2, c = geo.c;
    g.fillStyle = pal.bg;
    g.fillRect(x, 0, w, geo.h);
    if (!pd) return;
    const all = Math.min(VIS, pd) * c, ripe = Math.min(VIS, rp) * c;
    g.fillStyle = pal.muted;
    g.fillRect(x, geo.h - all, w, all);
    if (ripe) { g.fillStyle = pal.danger; g.fillRect(x, geo.h - ripe, w, ripe); }
  }

  function drawMine(st, now) {
    const m = st.el.mine;
    if (!m) return;
    const net = st.net, core = net.core, geo = m.geo, g = m.g, pal = st.pal, spr = st.spr;
    geo.y0 = 0;
    g.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
    g.clearRect(0, 0, geo.w, geo.h);
    let shake = 0;
    if (st.fx.shake > now && !reduced()) shake = Math.round(Math.sin(now / 18) * 2);
    g.save();
    g.translate(shake, 0);
    g.drawImage(st.back, geo.x0, 0, 10 * geo.c, geo.h);
    const loaded = net.loaded;
    const dead = loaded && !core.alive;
    // фігурка й черга — лише поки раунд живий; у паузі й після партії вони тільки заважали читати підсумок
    const live = st.phase === 'go' || st.phase === 'start' || st.phase === 'ready';
    if (loaded) {
      drawCells(g, geo, spr, core.cells, dead);
      // ряди, що зараз знімаються, — блимають
      if (core.clearing > 0 && !dead && st.phase === 'go') {
        const k = core.clearing / CLEART;
        g.fillStyle = 'rgba(255,255,255,' + (0.15 + 0.6 * k).toFixed(3) + ')';
        for (let y = 0; y < ROWS; y++) if (core.mask[y] === FULL) g.fillRect(geo.x0, rowY(geo, y), 10 * geo.c, geo.c);
      }
      if (core.type >= 0 && !dead && live) {
        let gy = core.by;
        while (core.fits(core.type, core.bx, gy - 1, core.rot)) gy--;
        // примара — підказка, куди ляже; коли раунд скінчився, вона лише заважає читати підсумок
        if (gy !== core.by && st.phase === 'go') drawPiece(g, geo, spr, core.type, core.bx, gy, core.rot, true);
        drawPiece(g, geo, spr, core.type, core.bx, core.by, core.rot, false);
      }
      const tr = st.fx.trail;
      if (tr && tr.until > now && !reduced()) {
        const a = (tr.until - now) / 90;
        g.fillStyle = 'rgba(255,255,255,' + (0.22 * a).toFixed(3) + ')';
        g.fillRect(geo.x0 + tr.x0 * geo.c, rowY(geo, tr.y0 + 1), (tr.x1 - tr.x0 + 1) * geo.c, (tr.y0 - tr.y1) * geo.c);
      }
    }
    drawParticles(g, st, now, geo);
    g.restore();
    const pend = loaded && !dead ? core.pending : 0, ripe = loaded && !dead ? core.ripe : 0;
    drawBar(g, geo, pend, ripe, pal);
    if (pend && st.phase === 'go') threat(g, st, geo, now, pend, ripe);
    // кишеня й наступні
    const pc = geo.pc, sp = st.sprPc;
    g.font = '600 ' + Math.max(9, Math.round(pc * 0.9)) + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'top';
    g.fillStyle = pal.muted;
    const hx = geo.holdX, nx = geo.nextX, sw = geo.side;
    const holdY = 0;
    const show = loaded && !dead && live;
    g.fillText('сховано', hx + sw / 2, holdY + 2);
    box(g, hx, holdY + pc * 1.4, sw, pc * 3.2, pal);
    if (show) drawPreview(g, sp, core.hold, hx, holdY + pc * 1.4, sw, pc * 3.2, pc, core.holdUsed ? 0.35 : 1);
    const nextY = st.L && st.L.kind === 'wide' ? 0 : holdY + pc * 5.2;
    g.fillStyle = pal.muted;
    g.fillText('далі', nx + sw / 2, nextY + 2);
    box(g, nx, nextY + pc * 1.4, sw, pc * 8.6, pal);
    if (show) for (let i = 0; i < 3; i++) drawPreview(g, sp, core.pieceAt(core.pi + i), nx, nextY + pc * 1.6 + i * pc * 2.8, sw, pc * 2.6, pc, i ? 0.8 : 1);
    drawOverlay(g, st, geo, now, dead);
    drawLabels(g, st, now, geo);
    drawSplat(g, st, now, geo);
  }

  /// Кинуте вибулим у мою стіну: 🍅 чи 👏 великим по центру, секунду, потім тане.
  function drawSplat(g, st, now, geo) {
    const sp = st.fx.splat;
    if (!sp) return;
    const age = now - sp.born;
    if (age >= 1200) { st.fx.splat = null; return; }
    if (age < 0) return;
    const k = age / 1200;
    g.globalAlpha = k < 0.6 ? 1 : 1 - (k - 0.6) / 0.4;
    g.font = Math.round(geo.c * (reduced() ? 3 : 2.4 + Math.min(1, age / 120) * 0.8)) + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(sp.icon, geo.x0 + 5 * geo.c, geo.h * 0.45);
    g.globalAlpha = 1;
  }

  /// Загроза сміття: скільки летить — числом угорі стіни; дозріло (влізе з наступною фіксацією без рядів) —
  /// рамка стіни блимає кольором небезпеки. Вузенька смуга збоку на ноуті майже не впадала в око.
  function threat(g, st, geo, now, pend, ripe) {
    const c = geo.c, pal = st.pal;
    if (ripe) {
      const a = reduced() ? 0.75 : 0.45 + 0.4 * Math.sin(now / 110);
      g.globalAlpha = a;
      g.strokeStyle = pal.danger;
      g.lineWidth = Math.max(2, Math.round(c * 0.14));
      g.strokeRect(geo.x0 + g.lineWidth / 2, g.lineWidth / 2, 10 * c - g.lineWidth, geo.h - g.lineWidth);
      g.globalAlpha = 1;
    }
    const size = Math.max(10, Math.round(c * 0.62));
    g.font = '800 ' + size + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    pill(g, (ripe ? '⚠ ' : '') + '+' + pend, geo.x0 + 5 * c, c * 2 + size, size, 10 * c, ripe ? pal.danger : pal.muted);
  }

  /// Рамка кишені й черги: канвас прозорий, тож коробочка мусить бути видно на будь-якому тлі картки.
  function box(g, x, y, w, h, pal) {
    g.fillStyle = pal.bg2;
    g.strokeStyle = pal.line;
    g.lineWidth = 1;
    g.beginPath(); g.roundRect(x + 0.5, y + 0.5, w - 1, h - 1, 6); g.fill(); g.stroke();
  }

  function drawOverlay(g, st, geo, now, dead) {
    const pal = st.pal, cx = geo.x0 + 5 * geo.c, cy = geo.h / 2, hi = geo.h * 0.3;   // hi — над підсумком раунду
    const text = (s, size, color, y) => {
      g.font = '800 ' + size + 'px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.lineWidth = Math.max(3, size / 8);
      g.strokeStyle = 'rgba(0,0,0,.55)';
      g.strokeText(s, cx, y, 10 * geo.c - 8);
      g.fillStyle = color;
      g.fillText(s, cx, y, 10 * geo.c - 8);
    };
    const c = geo.c;
    if (st.sprint && st.phase === 'ready') {
      shade(g, geo, 0.55);
      text('Готовий?', Math.round(c * 1.3), pal.text, cy - c);
      text(padish() ? 'Натисни Ⓐ' : coarse() ? 'Торкнись стіни' : 'Натисни будь-яку клавішу', Math.round(c * 0.62), pal.accent, cy + c * 0.4);
      text('сорок рядів на час', Math.round(c * 0.5), pal.muted, cy + c * 1.4);
      return;
    }
    if (st.phase === 'start') {
      const left = Math.max(0, st.inTicks * 40 - (now - st.fAt));
      shade(g, geo, 0.35);
      text(String(Math.max(1, Math.ceil(left / 1000))), Math.round(c * 3), pal.text, cy);
      return;
    }
    if (st.phase === 'go' && st.fx.go > now) text('Поїхали!', Math.round(c * 1.4), pal.accent, cy);
    const rank = dead ? myRank(st) : st.net.rank || (st.view && myBoard(st) && myBoard(st).rk);
    if (dead) shade(g, geo, 0.45);
    // під плашкою підсумку мітки місць лише стирчали б обрізками — місця й так є в таблиці
    if (st.sumShown) return;
    if (dead) text(rank && rank > 1 ? '✗ ' + rank + '-й' : '✗', Math.round(c * 1.6), pal.danger, hi);
    else if (rank === 1 && st.phase !== 'go') text('🏆', Math.round(c * 2.4), pal.accent, hi);
  }

  function shade(g, geo, a) {
    g.fillStyle = 'rgba(0,0,0,' + a + ')';
    g.fillRect(geo.x0, 0, 10 * geo.c, geo.h);
  }

  function myBoard(st) {
    const v = st.view;
    return v && v.boards ? v.boards.find((b) => b.s === st.ctx.seat) : null;
  }

  /// Чужа стіна: з кадрів, фігурка між кадрами тоне під власною вагою (гравітація її етапу) — не смикається.
  function drawOther(st, s, now) {
    const e = st.el.boards[s], o = st.others[s];
    if (!e || !o) return;
    const geo = e.geo, g = e.g, pal = st.pal, spr = st.sprMini || st.spr;
    geo.y0 = 0;
    g.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
    g.clearRect(0, 0, geo.w, geo.h);
    g.drawImage(st.backMini || st.back, geo.x0, 0, 10 * geo.c, geo.h);
    const dead = !o.a;
    drawCells(g, geo, spr, o.cells, dead);
    let full = false;
    for (let y = 0; y < 24; y++) if (o.mask[y] === FULL) { full = true; break; }
    if (full && !dead) {
      g.fillStyle = 'rgba(255,255,255,.45)';
      for (let y = 0; y < ROWS; y++) if (o.mask[y] === FULL) g.fillRect(geo.x0, rowY(geo, y), 10 * geo.c, geo.c);
    }
    if (o.p && !dead && (st.phase === 'go' || st.phase === 'start')) {
      const [t, bx, by0, rot] = o.p;
      const fits = (y) => {
        const off = (t * 4 + rot) * 8;
        for (let i = 0; i < 8; i += 2) {
          const x = bx + SHAPES[off + i], yy = y + SHAPES[off + i + 1];
          if (x < 0 || x > 9 || yy < 0 || yy > 23 || (o.mask[yy] & (1 << x))) return false;
        }
        return true;
      };
      const grav = st.stage > 0 ? GRAV[Math.min(GRAV.length - 1, Math.floor(o.k / st.stage))] : SPRINTG;
      let fall = st.phase === 'go' ? Math.floor(Math.min(90, (now - o.at) / TICK_MS) / grav) : 0;
      let by = by0;
      while (fall-- > 0 && fits(by - 1)) by--;
      o.drawnY = by;
      // примара — лише коли фігурка справді падає: на відліку контури внизу чужих стін тільки плутали
      if (st.phase === 'go') {
        let gy = by;
        while (fits(gy - 1)) gy--;
        if (gy !== by) drawPiece(g, geo, spr, t, bx, gy, rot, true);
      }
      drawPiece(g, geo, spr, t, bx, by, rot, false);
    }
    drawBar(g, geo, dead ? 0 : o.pd, dead ? 0 : o.rp, pal);
    // сміття вже дозріло — рамка кольору небезпеки: видно й на дрібній стіні
    if (!dead && o.rp > 0 && st.phase === 'go') {
      g.strokeStyle = pal.danger;
      g.lineWidth = 2;
      g.strokeRect(geo.x0 + 1, 1, 10 * geo.c - 2, geo.h - 2);
    }
    if (dead) shade(g, geo, 0.45);
    if ((dead || (o.rk === 1 && st.phase !== 'go')) && !st.sumShown) {
      g.font = '800 ' + Math.round(geo.c * 1.8) + 'px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillStyle = dead ? pal.danger : pal.accent;
      g.fillText(dead ? (o.rk > 1 ? '✗ ' + o.rk + '-й' : '✗') : '🏆', geo.x0 + 5 * geo.c, geo.h * 0.3, 10 * geo.c);
    }
    if (o.fx && o.fx.length) drawBoardLabels(g, o, now, geo);
    o.dirty = false;
  }

  /// Подія «c» чужої стіни → плашка на ній: лише те, чим варто похвалитись (четвірка, оберт Т, поспіль, чиста стіна).
  function otherLabel(st, ev) {
    const n = ev[2], kind = ev[3], b2b = ev[5];
    const text = kind & 2 ? 'Чиста стіна!' : kind & 1 ? 'Оберт Т' : n === 4 ? 'Четвірка!' : '';
    if (!text && !(b2b > 0)) return;
    const o = other(st, ev[1]);
    const pal = st.pal || palette(st.ctx);
    const fx = o.fx || (o.fx = []);
    if (text) fx.push({ text, color: kind & 1 ? pal.glaze[2] : pal.accent, born: performance.now() });
    if (b2b > 0) fx.push({ text: 'Поспіль!', color: pal.ok, born: performance.now() });
    while (fx.length > 3) fx.shift();
  }

  /// Плашки на чужій стіні («Четвірка!», «Оберт Т», «Поспіль!») — щоб і глядач, і вибулий бачили, хто що встругнув.
  function drawBoardLabels(g, o, now, geo) {
    let w = 0;
    const size = Math.max(10, Math.round(geo.c * 1.05));
    g.font = '800 ' + size + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    for (let i = 0; i < o.fx.length; i++) {
      const l = o.fx[i];
      const age = now - l.born;
      if (age >= 1100) continue;
      if (age < 0) { o.fx[w++] = l; continue; }   // ще летить — з'явиться, коли долетить
      const k = age / 1100;
      g.globalAlpha = k < 0.7 ? 1 : 1 - (k - 0.7) / 0.3;
      const y = geo.h * 0.42 + w * size * 1.4 - (reduced() ? 0 : k * size);
      pill(g, l.text, geo.x0 + 5 * geo.c, y, size, 10 * geo.c - 4, l.color);
      o.fx[w++] = l;
    }
    o.fx.length = w;
    g.globalAlpha = 1;
  }

  /// Напис на темній напівпрозорій підкладці — читається і на строкатих цеглинах.
  function pill(g, text, cx, y, size, maxW, color) {
    const tw = Math.min(maxW - 8, g.measureText(text).width);
    const h = size * 1.3;
    g.fillStyle = 'rgba(12,20,16,.72)';
    g.beginPath();
    g.roundRect(cx - tw / 2 - 6, y - h / 2, tw + 12, h, h / 2);
    g.fill();
    g.fillStyle = color;
    g.fillText(text, cx, y + 1, maxW - 8);
  }

  // ---------- ефекти ----------

  function burst(st, rowsY, geo) {
    if (reduced()) return;
    const parts = st.fx.parts;
    const c = geo.c;
    for (const y of rowsY) {
      for (let x = 0; x < 10; x++) {
        for (let k = 0; k < 2; k++) {
          if (parts.length >= 120) parts.shift();
          parts.push({
            x: geo.x0 + (x + Math.random()) * c, y: rowY(geo, y) + Math.random() * c,
            vx: (Math.random() - 0.5) * 4, vy: -Math.random() * 4 - 1, life: 400, born: performance.now(),
            col: st.pal.glaze[(x + y) % 7], s: Math.max(2, c * 0.22),
          });
        }
      }
    }
  }

  function drawParticles(g, st, now, geo) {
    const parts = st.fx.parts;
    if (!parts.length) return;
    let w = 0;
    for (let i = 0; i < parts.length; i++) {
      const p = parts[i];
      const age = now - p.born;
      if (age >= p.life) continue;
      const f = age / 16.7;
      const x = p.x + p.vx * f, y = p.y + p.vy * f + 0.45 * f * f;
      g.globalAlpha = 1 - age / p.life;
      g.fillStyle = p.col;
      g.fillRect(x, y, p.s, p.s);
      parts[w++] = p;
    }
    parts.length = w;
    g.globalAlpha = 1;
  }

  function label(st, text, color) {
    const labels = st.fx.labels;
    if (labels.length > 4) labels.shift();
    // кожна плашка — у своєму рядку стовпчика: найнижчий вільний, тож нові не налазять на ті, що ще не згасли
    let slot = 0;
    for (;;) {
      let used = false;
      for (const l of labels) if (l.slot === slot) { used = true; break; }
      if (!used) break;
      slot++;
    }
    labels.push({ text, color, born: performance.now(), slot });
  }

  function drawLabels(g, st, now, geo) {
    const labels = st.fx.labels;
    if (!labels.length) return;
    let w = 0;
    const mv = !reduced();
    const size = Math.round(geo.c * 0.85);
    g.font = '800 ' + size + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    for (let i = 0; i < labels.length; i++) {
      const l = labels[i];
      const age = now - l.born;
      if (age >= 1000) continue;
      const k = age / 1000;
      g.globalAlpha = k < 0.7 ? 1 : 1 - (k - 0.7) / 0.3;
      const y = geo.h * 0.28 + l.slot * size * 1.55 - (mv ? k * geo.c * 0.8 : 0);
      pill(g, l.text, geo.x0 + 5 * geo.c, y, size, 10 * geo.c - 2, l.color);
      labels[w++] = l;
    }
    labels.length = w;
    g.globalAlpha = 1;
  }

  /// Що рушій своєї стіни натворив з минулого кадра — плашки, черепки, звуки.
  function effects(st, now) {
    const core = st.net.core;
    if (!st.net.loaded) return;
    const geo = st.el.mine && st.el.mine.geo;
    if (core.drops && geo) {
      st.fx.trail = { x0: core.dropX0, x1: core.dropX1, y0: core.dropY0, y1: core.dropY1, until: now + 90 };
      core.drops = 0;
    }
    if (core.clearN) {
      const cl = core.clears;
      for (let i = 0; i < core.clearN; i++) {
        const e = i * 7, n = cl[e], kind = cl[e + 1], combo = cl[e + 2], b2b = cl[e + 3], sent = cl[e + 5];
        const tspin = kind & 1, perfect = kind & 2;
        if (tspin) label(st, 'Оберт Т' + (n > 1 ? ' ×' + n : ''), st.pal.glaze[2]);
        else if (n === 4) label(st, 'Четвірка!', st.pal.accent);
        if (b2b > 0) label(st, 'Поспіль!', st.pal.ok);
        if (combo >= 2) label(st, 'Серія ×' + combo, st.pal.text);
        if (perfect) label(st, 'Чиста стіна!', st.pal.accent);
        if (sent > 0 && !st.sprint) {
          const t = st.view && st.view.target ? st.view.target[st.ctx.seat] : -1;
          label(st, '+' + sent + ' летить →', st.pal.danger);
          if (t >= 0) shot(st, st.ctx.seat, t, sent);
        }
        beep(st, n === 4 ? 'four' : 'line');
      }
      if (geo) {
        const ys = [];
        for (let y = 0; y < 24; y++) if (core.mask[y] === FULL) ys.push(y);
        burst(st, ys, geo);
      }
      core.clearN = 0;
      st.fx.flash = now;
    }
    if (core.locks) { if (!core.clearing) beep(st, 'lock'); core.locks = 0; }
    if (core.inserted) { beep(st, 'garbage'); st.fx.shake = now + 120; core.inserted = 0; }
    if (!core.alive && !st.fx.outPlayed) { st.fx.outPlayed = true; beep(st, 'out'); }
    const lvl = core.stage;
    if (!st.sprint && lvl !== st.fx.lvl) {
      if (st.fx.lvl != null && lvl > st.fx.lvl) label(st, 'темп ' + (lvl + 1), st.pal.muted);
      st.fx.lvl = lvl;
    }
  }

  /// Посилка між стінами: число летить від нападника до жертви (DOM, одна нода на подію).
  function shot(st, from, to, rows) {
    if (reduced()) return;
    const scene = st.el.scene;
    const a = boardEl(st, from), b = boardEl(st, to);
    if (!scene || !a || !b) return;
    fly(st, a, b, '+' + rows, 'bricks-shot', 360);
  }

  /// Щось летить від стіни a до стіни b (DOM, одна нода на подію, не на кадр). Зі своєї стіни — з правого нижнього
  /// краю, а не з центру: там якраз висять плашки «Серія», «+1 летить», і бульбашка лягала б на них.
  function fly(st, a, b, text, cls, ms) {
    const scene = st.el.scene;
    const sr = scene.getBoundingClientRect(), ar = a.getBoundingClientRect(), br = b.getBoundingClientRect();
    const own = st.el.mine && a === st.el.mine.cv;
    const x0 = ar.left + ar.width * (own ? 0.72 : 0.5), y0 = ar.top + ar.height * (own ? 0.66 : 0.5);
    const x1 = br.left + br.width / 2, y1 = br.top + br.height * (b.classList.contains('bricks-strip') ? 0.5 : 0.4);
    const d = document.createElement('div');
    d.className = cls;
    d.textContent = text;
    d.style.left = (x0 - sr.left) + 'px';
    d.style.top = (y0 - sr.top) + 'px';
    scene.appendChild(d);
    requestAnimationFrame(() => {
      d.style.transform = 'translate(' + (x1 - x0) + 'px,' + (y1 - y0) + 'px) scale(1.3)';
      d.style.opacity = cls === 'bricks-shot' ? '0.2' : '1';
    });
    setTimeout(() => d.remove(), ms);
  }

  function boardEl(st, s) {
    if (st.el.mine && s === st.ctx.seat) return st.el.mine.cv;
    if (st.el.boards[s]) return st.el.boards[s].cv;
    return st.el.strips && st.el.strips[s] ? st.el.strips[s].el : null;
  }

  // ---------- HUD, підписи, підсумок ----------

  function nickOf(st, s) {
    const b = st.view && st.view.boards ? st.view.boards.find((x) => x.s === s) : null;
    return (b && b.nk) || st.ctx.nickOf(s) || SEATS[s];
  }

  /// HUD будується раз на розкладку: текст — окремим вузлом, кнопка звуку — одна й та сама. Інакше в спринті
  /// (секундомір із сотими) рядок перебудовувався щокадру разом із кнопкою, і натиск на 🔈 губився.
  function hudBuild(st) {
    const el = document.createElement('div');
    el.className = 'bricks-hud';
    const txt = document.createElement('span');
    txt.className = 'bricks-hudtxt';
    el.appendChild(txt);
    const snd = document.createElement('button');
    snd.type = 'button';
    snd.className = 'bricks-snd';
    snd.title = 'Звук';
    snd.setAttribute('data-pad-skip', '');
    snd.textContent = st.sound ? '🔈' : '🔇';
    snd.onclick = () => {
      st.sound = !st.sound;
      try { localStorage.setItem('bricksSound', st.sound ? '1' : '0'); } catch { /* приватне вікно */ }
      if (st.sound) audioOn(st);
      snd.textContent = st.sound ? '🔈' : '🔇';
    };
    el.appendChild(snd);
    st.el.hudTxt = txt;
    if (st.sprint) {
      txt.innerHTML = '<span class="bricks-big">⏱ <span class="bricks-clock">0:00,00</span></span>'
        + '<span class="bricks-big"><small>рядів </small><span class="bricks-lines">0</span><small>/' + SPRINT_LINES + '</small></span>'
        + '<span class="bricks-pbline"></span>';
      st.el.clock = txt.querySelector('.bricks-clock');
      st.el.lines = txt.querySelector('.bricks-lines');
      st.el.pb = txt.querySelector('.bricks-pbline');
    }
    return el;
  }

  function setText(el, s) { if (el && el.textContent !== s) el.textContent = s; }

  function hud(st, now) {
    const txt = st.el.hudTxt;
    if (!txt) return;
    const net = st.net, core = net.core, v = st.view || {};
    if (st.sprint) {
      // після фінішу — офіційний час сервера (сотими, як у таблиці), доти — свій годинник стіни
      const done = st.phase === 'over' && v.result && v.result.sec != null;
      setText(st.el.clock, done ? secText(v.result.sec) : clockText((st.phase === 'go' || st.phase === 'over') && net.loaded ? core.tick : 0, true));
      setText(st.el.lines, String(Math.min(SPRINT_LINES, net.loaded ? core.lines : 0)));
      pbLine(st, now);
      return;
    }
    const bits = [];
    const secs = st.phase === 'go' && net.loaded ? core.tick : st.fwall;
    const sudden = st.sd >= 0 && st.phase === 'go';
    bits.push('⏱ ' + clockText(secs, false));
    // у раптовій смерті темп уже найвищий — місце в рядку краще віддати «земля росте»
    if (sudden) bits.push('<span class="bricks-hot">⛰ земля росте: ' + Math.ceil(st.sd / 60) + ' с</span>');
    else bits.push('темп ' + ((net.loaded ? core.stage : st.lvl) + 1));
    bits.push(rowsWord(net.loaded ? core.lines : 0));
    if ((v.need || 1) > 1) bits.push('★ ' + ((v.wins || [])[st.ctx.seat] || 0) + '/' + v.need);
    // ціль — останньою: довге ім'я обріжеться трьома крапками, а рядок і стіна під ним не зсунуться
    const t = v.target ? v.target[st.ctx.seat] : -1;
    if (t >= 0 && v.garbage !== 'none') bits.push('<span class="bricks-to bk' + t + '">→ ' + st.ctx.esc(nickOf(st, t)) + '</span>');
    const html = bits.join(' · ');
    if (txt.dataset.sig !== html) { txt.dataset.sig = html; txt.innerHTML = html; }
  }

  function labels(st) {
    const v = st.view || {};
    for (const s in st.el.boards) {
      const e = st.el.boards[s], o = st.others[s];
      if (!o) continue;
      const i = +s;
      const t = v.target ? v.target[i] : -1;
      const wins = (v.wins || [])[i] || 0;
      // на телефоні чужа стіна завширшки з палець: «гість Ярина» → «Ярина», інакше видно лише «гіст…»
      let nick = o.nick || nickOf(st, i);
      if (st.L && st.L.kind === 'narrow') nick = nick.replace(/^гість\s+/i, '') || nick;
      const html = '<span class="bricks-l1"><span class="bricks-dot"></span><b>' + st.ctx.esc(nick) + '</b>'
        + ((v.need || 1) > 1 ? '<span class="bricks-w">★' + wins + '</span>' : '') + '</span>'
        + '<span class="bricks-l2"><span class="bricks-n">' + rowsWord(o.l) + '</span>'
        + (t >= 0 && o.a && v.garbage !== 'none' && st.phase === 'go' ? '<span class="bricks-to bk' + t + '">→ ' + st.ctx.esc(nickOf(st, t)) + '</span>' : '')
        + '</span>';
      if (e.lbl.dataset.sig !== html) { e.lbl.dataset.sig = html; e.lbl.innerHTML = html; }
    }
  }

  function summary(st) {
    const el = st.el.sum;
    if (!el) return;
    const v = st.view;
    const res = v && v.phase === 'over' ? v.result : null;
    const show = !st.sprint && v && (v.phase === 'pause' || v.phase === 'over') && ((v.boards && v.boards.length) || res);
    st.sumShown = !!show;
    if (!show) { if (!el.hidden) el.hidden = true; return; }
    const esc = st.ctx.esc;
    const over = v.phase === 'over';
    const wins = v.wins || [];
    let rows;
    if (res) {
      // підсумок партії — з result: там і ті, хто встав у паузі між раундами (їхньої стіни в boards уже нема)
      rows = [];
      for (let s = 0; s < res.ranks.length; s++) {
        if (!res.ranks[s]) continue;
        const b = (v.boards || []).find((x) => x.s === s);
        rows.push({
          s, nick: (res.nk && res.nk[s]) || (b && b.nk) || SEATS[s], rk: res.ranks[s], lines: res.lines[s],
          sn: res.sent ? res.sent[s] : b ? b.sn : 0, rc: res.recv ? res.recv[s] : b ? b.rc : 0, wins: wins[s] || 0,
        });
      }
    } else {
      rows = (v.boards || []).map((b) => ({ s: b.s, nick: b.nk || SEATS[b.s], rk: b.rk || 99, lines: b.l, sn: b.sn, rc: b.rc, wins: wins[b.s] || 0 }));
    }
    rows.sort((a, b) => (a.rk || 99) - (b.rk || 99) || a.s - b.s);
    const win = rows.filter((r) => r.rk === 1);
    let head = win.length
      ? '🏆 ' + win.map((r) => esc(r.nick)).join(', ') + (over ? (win.length > 1 ? ' беруть партію' : ' бере партію') : (win.length > 1 ? ' ділять раунд' : ' бере раунд'))
      : over ? 'Партію не дограли' : 'Раунд нікому';
    // перемога через те, що решта встали, — так і кажемо, інакше «бере партію» при рахунку 0 : 1 виглядає як збій
    if (res && res.why === 'left' && win.length) {
      const meWon = st.ctx.mine && win.some((r) => r.s === st.ctx.seat);
      const who = rows.length > 2 ? 'Решта пішли' : 'Суперник пішов';
      head += '<div class="bricks-why">' + who + (meWon ? ' — партія твоя' : ' з-за столу') + '</div>';
    }
    // рахунок раундів: на двох — з іменами, спершу той, хто попереду («Петро 1 : 0 Оля»); на трьох-чотирьох — ★ у таблиці
    const multi = (v.need || 1) > 1, pair = rows.length === 2;
    let score = '';
    if (multi && pair) {
      const [a, b] = rows.slice().sort((x, y) => y.wins - x.wins || (x.rk || 99) - (y.rk || 99) || x.s - y.s);
      score = '<div class="bricks-score">' + esc(a.nick) + ' <b>' + a.wins + ' : ' + b.wins + '</b> ' + esc(b.nick) + '</div>';
    }
    const star = multi && !pair;
    // хто встав посеред партії — інакше «Рахунок 0 : 0» під «бере партію» виглядає як збій
    const left = res && res.left ? rows.filter((r) => res.left[r.s]) : [];
    const gone = left.length ? '<div class="bricks-next">🚪 ' + left.map((r) => esc(r.nick)).join(', ') + ' — з-за столу</div>' : '';
    const html = '<div class="bricks-sumbox"><div class="bricks-sumhead">' + head + '</div>' + score
      + '<table><tr><th></th><th></th>' + (star ? '<th>раундів</th>' : '') + '<th>рядів</th><th class="bricks-sr">вислав</th><th class="bricks-sr">отримав</th></tr>'
      + rows.map((r) => '<tr class="bk' + r.s + '"><td>' + (r.rk === 1 ? '🏆' : r.rk > 1 && r.rk < 99 ? r.rk + '-й' : '') + '</td><td><span class="bricks-dot"></span>'
        + esc(r.nick) + '</td>' + (star ? '<td class="bricks-w">★' + r.wins + '</td>' : '') + '<td>' + r.lines + '</td><td class="bricks-sr">' + r.sn + '</td><td class="bricks-sr">' + r.rc + '</td></tr>').join('')
      + '</table>' + medals(st, res, rows) + gone + (over ? '' : '<div class="bricks-next">новий раунд за кілька секунд…</div>') + '</div>';
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
    el.hidden = false;
  }

  /// «Медалі» партії — привід для балачки: найбільша посилка одним ударом, найдовша серія, хто з'їв найбільше сміття.
  function medals(st, res, rows) {
    if (!res || !res.shot) return '';
    const esc = st.ctx.esc;
    const nick = (s) => { const r = rows.find((x) => x.s === s); return r ? esc(r.nick) : (res.nk && res.nk[s] ? esc(res.nk[s]) : SEATS[s]); };
    const best = (arr) => { let b = -1; for (const r of rows) if (arr[r.s] > 0 && (b < 0 || arr[r.s] > arr[b])) b = r.s; return b; };
    const out = [];
    const sh = best(res.shot);
    if (sh >= 0 && res.shot[sh] >= 2) {
      const to = res.shotTo ? res.shotTo[sh] : -1;
      out.push('💥 Найбільша посилка: ' + nick(sh) + (to >= 0 ? ' → ' + nick(to) : '') + ', ' + rowsWord(res.shot[sh]));
    }
    const cb = best(res.combo || []);
    if (cb >= 0 && res.combo[cb] >= 2) out.push('🔗 Найдовша серія: ' + nick(cb) + ' ×' + res.combo[cb]);
    const rc = best(res.recv || []);
    if (rc >= 0 && res.recv[rc] >= 4) out.push('🧱 Найбільше сміття з’їв: ' + nick(rc) + ', ' + rowsWord(res.recv[rc]));
    return out.length ? '<div class="bricks-medals">' + out.map((x) => '<div>' + x + '</div>').join('') + '</div>' : '';
  }

  // ---------- телефон: смужки небезпеки замість мікростін ----------

  function stripsBuild(st, others) {
    const box = document.createElement('div');
    box.className = 'bricks-strips';
    st.el.strips = {};
    for (const s of others) {
      const el = document.createElement('div');
      el.className = 'bricks-strip bk' + s;
      el.dataset.s = s;
      el.innerHTML = '<span class="bricks-dot"></span><b class="bricks-snk"></b><span class="bricks-meter"><i></i></span><span class="bricks-sv"></span>';
      el.addEventListener('click', () => tossAt(st, s));
      box.appendChild(el);
      st.el.strips[s] = { el, nick: el.querySelector('.bricks-snk'), bar: el.querySelector('.bricks-meter i'), val: el.querySelector('.bricks-sv'), sig: '' };
    }
    return box;
  }

  /// Висота стіни (скільки рядів знизу вже зайнято), вхідне сміття, місце вибулого — лише коли щось змінилось.
  function stripsUpdate(st) {
    if (!st.el.strips) return;
    const v = st.view || {};
    const me = st.ctx.mine ? st.ctx.seat : -1;
    const myT = v.target && me >= 0 ? v.target[me] : -1;
    const multi = (v.need || 1) > 1;
    for (const s in st.el.strips) {
      const e = st.el.strips[s], o = st.others[s];
      if (!o) continue;
      let top = 0;
      for (let y = 23; y >= 0; y--) if (o.mask[y]) { top = y + 1; break; }
      const dead = !o.a;
      const pct = dead ? 0 : Math.min(100, top * 5);
      const nick = (o.nick || nickOf(st, +s)).replace(/^гість\s+/i, '') || o.nick;
      const val = (dead ? (o.rk > 1 ? '✗ ' + o.rk + '-й' : '✗') : top + '/20' + (o.pd ? ' · +' + o.pd : ''))
        + (multi ? ' ★' + ((v.wins || [])[+s] || 0) : '');
      const target = +s === myT && st.phase === 'go';
      const sig = [nick, pct, val, dead, target, o.rp > 0].join('|');
      if (sig === e.sig) continue;
      e.sig = sig;
      e.nick.textContent = (target ? '→ ' : '') + nick;
      e.bar.style.width = pct + '%';
      e.bar.className = pct >= 70 ? 'hot' : pct >= 45 ? 'warm' : '';
      e.val.textContent = val;
      e.el.classList.toggle('dead', dead);
      e.el.classList.toggle('danger', !dead && o.rp > 0);
    }
  }

  // ---------- спринт: особистий рекорд (у цьому браузері) і відставання від нього ----------

  const pbKey = (st) => 'bricksSprintPB:' + ((st.ctx.me && st.ctx.me.nick) || '');
  function pbGet(st) {
    try { const x = JSON.parse(localStorage.getItem(pbKey(st)) || 'null'); return x && x.sec > 0 ? x : null; } catch { return null; }
  }
  function pbSet(st, x) { try { localStorage.setItem(pbKey(st), JSON.stringify(x)); } catch { /* приватне вікно */ } }
  const secDelta = (d) => (d <= 0 ? '−' : '+') + Math.abs(d).toFixed(2).replace('.', ',') + ' с';

  /// Відсічки кожні 10 рядів: на якому тику стіни їх набралось — і наскільки це швидше чи повільніше за рекорд.
  function sprintSplits(st, now) {
    if (st.phase !== 'go' || !st.net.loaded || !st.run) return;
    const run = st.run, core = st.net.core;
    while (run.splits.length < 3 && core.lines >= (run.splits.length + 1) * 10) {
      run.splits.push(core.tick);
      const i = run.splits.length - 1;
      const pb = st.pb;
      if (pb && pb.splits && pb.splits[i] > 0) {
        const d = (core.tick - pb.splits[i]) / 60;
        st.pbDelta = { text: (i + 1) * 10 + ' рядів: ' + secDelta(d), good: d <= 0, until: now + 4000 };
        label(st, secDelta(d), d <= 0 ? st.pal.ok : st.pal.danger);
      }
    }
  }

  /// Фініш: офіційний час сервера проти рекорду; новий рекорд пам'ятаємо разом із відсічками.
  function sprintFinish(st) {
    if (st.daily) return;
    const v = st.view;
    if (!st.run || st.run.saved || !v || v.phase !== 'over' || !st.ctx.mine) return;
    st.run.saved = true;
    const sec = v.result && v.result.sec;
    if (sec == null) { st.pbNew = null; return; }
    const pb = st.pb;
    if (!pb || sec < pb.sec) {
      st.pbNew = true;
      st.pbGap = pb ? pb.sec - sec : 0;
      st.pb = { sec, splits: st.run.splits.slice() };
      pbSet(st, st.pb);
    } else {
      st.pbNew = false;
      st.pbGap = sec - pb.sec;
    }
  }

  function pbLine(st, now) {
    const el = st.el.pb;
    if (!el) return;
    let text = '', cls = 'bricks-pbline';
    const pb = st.pb;
    if (st.phase === 'over' && st.pbNew === true) {
      text = '🎉 новий рекорд!' + (st.pbGap > 0 ? ' ' + secDelta(-st.pbGap) : '');
      cls += ' good';
    } else if (st.phase === 'over' && st.pbNew === false) {
      text = 'до рекорду ' + st.pbGap.toFixed(2).replace('.', ',') + ' с';
    } else if (st.phase === 'go' && st.pbDelta && st.pbDelta.until > now) {
      text = st.pbDelta.text;
      cls += st.pbDelta.good ? ' good' : ' bad';
    } else if (pb) text = 'рекорд ' + secText(pb.sec);
    if (st.daily && st.phase !== 'go') text = dayLine(st);
    setText(el, text);
    if (el.className !== cls) el.className = cls;
  }

  /// «Цеглини дня №20 · 🥇 Оля 1:37 🔥3 · Петро 1:52 · ти — 3-й» — табло дня з виду (сервер тримає його в пам'яті).
  function dayLine(st) {
    const d = st.view && st.view.daily;
    if (!d) return '';
    const b = d.board, rows = (b && b.rows) || [];
    const me = ((st.ctx && st.ctx.me) || '').trim().toLowerCase();
    const mine = rows.findIndex((r) => (r.n || '').trim().toLowerCase() === me);
    const t = (ms) => secText(ms / 1000).replace(/,\d+$/, '');
    const one = (r, i) => (i === 0 ? '🥇 ' : '') + r.n + ' ' + t(r.ms) + (r.a > 1 ? ' ×' + r.a : '') + (r.st >= 2 ? ' 🔥' + r.st : '');
    let s = 'Цеглини дня №' + d.no;
    if (!b) return s;
    s += rows.length ? ' · ' + rows.slice(0, 3).map(one).join(' · ') : ' · сьогодні ще ніхто не склав — будь першим';
    if (mine >= 3) s += ' · ти — ' + (mine + 1) + '-й';
    if (d.solved && d.streak >= 2) s += ' · у тебе 🔥' + d.streak + ' дн. поспіль';
    return s;
  }

  // ---------- вибулий кидає 🍅 чи 👏 у стіну живого ----------

  const TOSS = ['🍅', '👏'];

  /// Панель під своєю стіною, поки я вибув, а раунд іде: обираєш, що кидати, і тицяєш у чужу стіну.
  function tossBar(st) {
    const me = st.el.me;
    if (!me) return;
    // лише коли вибування підтвердив сервер (подія «o» чи вид): до того він кидок не прийме
    const mb = myBoard(st);
    const want = st.ctx.mine && st.phase === 'go' && st.net.loaded && !st.net.core.alive && !st.sprint
      && (st.net.rank > 0 || (mb && !mb.a))
      && seatsInRound(st).some((s) => s !== st.ctx.seat && st.others[s] && st.others[s].a);
    // вибулому кнопки керування ні до чого — на телефоні замість них панелька з 🍅
    if (st.el.scene && st.el.scene.classList.contains('bricks-out') !== want) st.el.scene.classList.toggle('bricks-out', want);
    let bar = st.el.toss;
    if (!want) { if (bar && !bar.hidden) bar.hidden = true; return; }
    if (!bar) {
      bar = st.el.toss = document.createElement('div');
      bar.className = 'bricks-toss';
      bar.innerHTML = TOSS.map((e, i) => '<button type="button" data-e="' + i + '" data-pad-skip>' + e + '</button>').join('')
        + '<span>тицяй у стіну живого</span>';
      bar.addEventListener('click', (e) => {
        const b = e.target.closest('button');
        if (!b) return;
        st.tossE = +b.dataset.e;
        tossMark(st);
      });
      me.appendChild(bar);
      tossMark(st);
    }
    if (bar.hidden) bar.hidden = false;
  }

  function tossMark(st) {
    const bar = st.el.toss;
    if (!bar) return;
    bar.querySelectorAll('button').forEach((b) => b.classList.toggle('on', +b.dataset.e === st.tossE));
  }

  /// Клік по чужій стіні (чи смужці): якщо я вибув — кидаю туди обране. Сервер пустить не частіше разу на секунду.
  function tossAt(st, s) {
    if (!st.el.toss || st.el.toss.hidden) return;
    const o = st.others[s];
    if (!o || !o.a) return;
    const now = performance.now();
    if (now - st.tossAt < 1000) return;
    st.tossAt = now;
    st.ctx.input('toss', { e: st.tossE, to: s });
  }

  /// Подія «r» з кадра: хтось із вибулих кинув — летить у всіх на екрані, а на стіні-жертві лишається слід.
  function tossFly(st, from, to, e) {
    const icon = TOSS[e] || TOSS[0];
    const o = st.others[to];
    // у мою стіну — великим, на пів стіни: помідор має бути помідором
    if (st.ctx.mine && to === st.ctx.seat && st.el.mine) st.fx.splat = { icon, born: performance.now() + (reduced() ? 0 : 420) };
    else if (o) {
      (o.fx || (o.fx = [])).push({ text: icon, color: '#fff', born: performance.now() + (reduced() ? 0 : 420) });
      if (o.fx.length > 3) o.fx.shift();
    }
    if (reduced()) return;
    const a = boardEl(st, from), b = boardEl(st, to);
    if (!st.el.scene || !a || !b) return;
    fly(st, a, b, icon, 'bricks-tossed', 520);
  }

  // ---------- цикл ----------

  /// Лобі, «готовий?» спринту й підсумок: мережа стоїть, стіни не рухаються. Тоді цикл не крутить 60 порожніх кадрів
  /// на секунду, а дрімає по 0,25 с (прохід 28.09); вид, кадр і розмір будять одразу (frameLoop).
  const IDLE_PH = new Set(['lobby', 'ready', 'over']);
  const IDLE_MS = 250;
  const fxBusy = (st, now) => st.fx.parts.length || st.fx.labels.length || st.fx.splat || st.fx.shake > now
    || (st.fx.trail && st.fx.trail.until > now) || st.fx.go > now;

  function frameLoop(st) {
    const loop = (now) => {
      st.raf = 0;
      const root = st.root;
      if (!root || !root.isConnected) return;
      tickNet(st, now);
      const shown = !document.hidden && !!root.offsetParent;
      if (shown) render(st, now);
      if (IDLE_PH.has(st.phase) && (!shown || (!st.dirty && !fxBusy(st, now)))) st.idleT = setTimeout(() => { st.idleT = 0; st.raf = requestAnimationFrame(loop); }, IDLE_MS);
      else st.raf = requestAnimationFrame(loop);
    };
    if (st.idleT) { clearTimeout(st.idleT); st.idleT = 0; }
    if (!st.raf) st.raf = requestAnimationFrame(loop);
  }

  function tickNet(st, now) {
    const net = st.net;
    if (!st.ctx.mine || !net.loaded) return;
    net.step(now);
    if (now - (st.lastFlush || 0) >= 50) { st.lastFlush = now; net.flush(); }
  }

  function render(st, now) {
    const t0 = performance.now();
    ensureArt(st);
    effects(st, now);
    const core = st.net.core;
    const busy = st.fx.parts.length || st.fx.labels.length || st.fx.splat || st.fx.shake > now || (st.fx.trail && st.fx.trail.until > now)
      || st.phase === 'start' || st.fx.go > now || (st.sprint && st.phase === 'go')
      || (st.phase === 'go' && st.net.loaded && core.alive && core.crN > 0 && !reduced());   // рамка дозрілого сміття блимає
    let drew = false;
    if (st.el.mine && (st.dirty || busy || core.ver !== st.lastVer)) {
      st.lastVer = core.ver;
      drawMine(st, now);
      drew = true;
    }
    for (const s in st.el.boards) {
      const o = st.others[s];
      if (!o) continue;
      // між кадрами фігурка тоне сама — перемальовуємо, лише коли вона справді зрушила
      let moving = false;
      if (o.p && o.a && st.phase === 'go') {
        const grav = st.stage > 0 ? GRAV[Math.min(GRAV.length - 1, Math.floor(o.k / st.stage))] : SPRINTG;
        const want = Math.floor(Math.min(90, (now - o.at) / TICK_MS) / grav);
        moving = o.drawnFall !== want;
        o.drawnFall = want;
      }
      if (o.dirty || st.dirty || moving || (o.fx && o.fx.length)) { drawOther(st, s, now); drew = true; }
    }
    if (st.sprint) sprintSplits(st, now);
    if (st.dirty || now - st.lastRender > 250) {
      // з'явився (чи зник) пад — розкладка інша: смужка його підказок унизу забирає висоту
      if (st.L && padish() !== st.L.pad && st.root) { build(st.root, st); summary(st); }
      // стіл збудувався, поки картка була схована (build не міг поміряти прокрутку), — перевірити ще раз, раз на розкладку
      else if (st.L && st.root && !coarse() && st.fitSeen !== st.layout + '|' + window.innerHeight) {
        st.fitSeen = st.layout + '|' + window.innerHeight;
        const doc = document.documentElement;
        if (doc.scrollHeight > doc.clientHeight + 1) { st.layout = ''; build(st.root, st); summary(st); }
      }
      hud(st, now); labels(st); stripsUpdate(st); tossBar(st); st.lastRender = now;
    } else if (st.sprint && st.phase === 'go' && now - st.lastClock > 90) {
      st.lastClock = now;
      hud(st, now);   // секундомір спринту — лише textContent одного вузла, десять разів на секунду
    }
    st.dirty = false;
    if (drew) {
      const dt = performance.now() - t0;
      const p = st.perf;
      p.ring[p.i] = dt;
      p.i = (p.i + 1) % p.ring.length;
      p.n++;
      p.sum += dt;
      if (dt > p.max) p.max = dt;
    }
  }

  // ---------- модуль ----------

  function mountCommon(root, ctx) {
    const st = state(root, ctx);
    st.root = root;
    st.keyup = (e) => {
      const k = keyOf(e);
      if (!k || !st.ctx || !st.ctx.mine) return;
      st.net.release(k, performance.now());
    };
    document.addEventListener('keyup', st.keyup);
    // вікно втратило фокус — пальці вже не на клавішах, хоч keyup і не прийшов
    st.onBlur = () => st.net.releaseAll(performance.now());
    window.addEventListener('blur', st.onBlur);
    st.onResize = () => { st.layout = ''; st.fitCut = 0; st.dirty = true; if (st.root && st.root.isConnected) update(st.root, st.ctx); };
    window.addEventListener('resize', st.onResize);
    // прихована вкладка: rAF мовчить, а стіна жити має — крок і пачки раз на 100 мс (браузер урізає до 1/с)
    st.timer = setInterval(() => {
      if (!document.hidden) return;
      const now = performance.now();
      tickNet(st, now);
    }, 100);
    st.onVis = () => { if (!document.hidden && st.net.live) st.net.wantSync(performance.now()); };
    document.addEventListener('visibilitychange', st.onVis);
    frameLoop(st);
    return st;
  }

  function update(root, ctx) {
    const st = state(root, ctx);
    st.root = root;
    let v = ctx.view;
    if (!v) return;
    // Дограний стіл, за який підсів новий гравець, каркас повертає в лобі, а гра ще тримає вид минулої партії
    // (стіни, підсумок). Новачок бачив би чужий підсумок і чужу стіну як свою — тож у лобі це просто лобі.
    if (ctx.room && ctx.room.status === 'lobby' && v.phase !== 'lobby') {
      v = Object.assign({}, v, { phase: 'lobby', boards: [], result: null, target: [], wins: [] });
    }
    const now = performance.now();
    const fresh = v.round !== st.round || (v.seed >>> 0) !== st.seed;
    st.view = v;
    st.phase = v.phase;
    st.stage = v.stage | 0;
    st.lvl = v.lvl | 0;
    st.sd = v.sd;
    if (fresh) {
      st.round = v.round;
      st.seed = v.seed >>> 0;
      st.others = {};
      st.fx.parts.length = 0;
      st.fx.labels.length = 0;
      st.fx.outPlayed = false;
      st.fx.lvl = null;
      if (v.phase === 'start') { st.inTicks = v.startIn; st.fAt = now; }
      if (st.sprint) {
        st.pb = st.daily ? null : pbGet(st);   // у «Цеглинах дня» мірило — табло дня, а не власний рекорд
        st.run = { seed: st.seed, splits: [], saved: false };
        st.pbDelta = st.pbNew = null;
      }
    }
    for (const b of v.boards || []) takeBoard(st, b, true);
    st.net.adoptView(v, ctx.mine ? ctx.seat : -1, now);
    if (v.phase === 'go' && !st.fwall) st.fwall = v.t;
    const pal = palette(ctx);
    const sig = pal.glaze.join() + pal.bg2 + pal.line;
    if (sig !== st.palSig) { st.palSig = sig; st.pal = pal; st.spr = st.sprMini = st.sprPc = st.back = st.backMini = null; }
    build(root, st);
    st.dirty = true;
    summary(st);
    if (st.sprint) sprintFinish(st);
    frameLoop(st);
  }

  function frame(root, ctx, f) {
    const st = state(root, ctx);
    if (!f) return;
    const now = performance.now();
    const prev = st.phase;
    st.phase = f.ph;
    st.lvl = f.lvl;
    st.sd = f.sd;
    st.fwall = f.t;
    if (f.ph === 'start' || f.ph === 'pause') { st.inTicks = f.in; st.fAt = now; }
    if (prev !== 'go' && f.ph === 'go') st.fx.go = now + 600;
    const me = ctx.mine ? ctx.seat : -1;
    for (const b of f.b || []) if (b.s !== me) takeBoard(st, b, false);
    for (const ev of f.ev || []) {
      if (ev[0] === 'c' && ev[1] !== me) {
        if (ev[6] > 0 && ev[7] >= 0) shot(st, ev[1], ev[7], ev[6]);
        otherLabel(st, ev);
      } else if (ev[0] === 'o' && ev[1] !== me) { const o = other(st, ev[1]); o.rk = ev[2]; o.a = 0; o.dirty = true; }
      else if (ev[0] === 'r') tossFly(st, ev[1], ev[2], ev[3]);
    }
    st.net.onFrame(f, now);
    st.dirty = true;
    if (prev !== f.ph && st.root) summary(st);
    if (st.idleT) frameLoop(st);
  }

  /// Лобі столу на 2+: хто сидить, бачить, чи вже можна рушати (каркас пише «Чекаємо, хто підсяде», навіть коли
  /// господареві досить натиснути «Почати»). Глядач — без статусу, як і було.
  function lobbyLine(ctx) {
    const r = ctx.room;
    if (!r || r.status !== 'lobby' || !ctx.mine) return '';
    const seated = (r.seats || []).filter((s) => s.nick).length;
    const host = String(r.host || '').toLowerCase() === String((ctx.me && ctx.me.nick) || '').toLowerCase();
    if (seated < (r.minPlayers || 2)) return host ? 'Чекаємо, хто підсяде: гукни когось за стіл 📣' : 'Чекаємо, хто підсяде';
    return host ? 'Гайда: тисни «Почати» — або зачекай, хто ще підсяде' : 'Чекаємо, поки господар тисне «Почати»';
  }

  function status(ctx) {
    const st = ctx._bk;
    const f = ctx.frame;
    const ph = (st && st.phase) || (f && f.ph) || (ctx.view && ctx.view.phase) || '';
    if (!(st && st.sprint) && ctx.room && ctx.room.status === 'lobby') return lobbyLine(ctx);
    if (!ctx.playing) return '';
    if (st && st.sprint) {
      const d = ctx.view && ctx.view.daily;
      if (d && d.solved) return 'Цеглини дня складено — нове зерно після півночі';
      if (ph === 'ready') return (d ? 'Зерно дня однакове для всіх' + (d.attempts ? ' · спроба ' + (d.attempts + 1) : '') + ' — ' : '')
        + (padish() ? 'натисни Ⓐ — і поїхали' : coarse() ? 'торкнись стіни — і поїхали' : 'натисни будь-яку клавішу');
      if (ph === 'start') return 'Готуйсь…';
      if (ph === 'go' && st.net.loaded) return st.net.core.lines + '/' + SPRINT_LINES + ' · ' + clockText(st.net.core.tick, false);
      return '';
    }
    if (ph === 'pause') {
      const v = ctx.view;
      const w = v && v.boards ? v.boards.filter((b) => b.rk === 1) : [];
      return w.length ? (w.length > 1 ? 'Раунд беруть ' : 'Раунд бере ') + w.map((b) => b.nk || SEATS[b.s]).join(', ') : 'Раунд нікому';
    }
    if (ph === 'over') return '';
    // Глядачеві «Дивлюсь збоку» каркас уже пише під кнопками; порожній статус він доповнив би ще одним
    // «Дивишся збоку». Тож глядачеві — щось корисне: скільки ще в грі й хто попереду за рядами.
    if (!ctx.mine) return st ? watchText(st, ph) : '';
    if (ph === 'start') return 'Готуйсь… ' + controlsText();
    if (st && st.net.loaded && !st.net.core.alive) {
      const rk = myRank(st);
      return 'Ти вибув' + (rk > 1 ? ' — ' + rk + '-й' : '') + '. Дивись, як мучаться інші';
    }
    return '';
  }

  function watchText(st, ph) {
    const seats = seatsInRound(st);
    if (ph === 'start' || !seats.length) return 'Зараз почнуть…';
    let alive = 0, best = -1;
    for (const s of seats) {
      const o = st.others[s];
      if (!o) continue;
      if (o.a) alive++;
      if (o.a && (best < 0 || o.l > st.others[best].l)) best = s;
    }
    const lead = best >= 0 && st.others[best].l > 0 ? ' · найбільше рядів — ' + nickOf(st, best) + ', ' + st.others[best].l : '';
    return 'У грі ' + alive + ' з ' + seats.length + lead;
  }

  /// Хто з джойстиком (Дека, підключений пад) — тому й підказки кнопками пада, а не клавіатури.
  function padish() { try { return !!(window.HPad && (HPad.on || HPad.pads > 0)); } catch { return false; } }

  function controlsText() {
    if (padish()) return '✥ рух · Ⓐ крутити · Ⓧ кинути · Ⓨ сховати · LB RB оберти';
    if (coarse()) return 'кнопки під стіною, тап — крутити, свайп униз — кинути';
    return '← → рухати · ↑ або X крутити · пробіл — кинути · C — сховати';
  }

  /// Моє місце в раунді: із кадра (подія «o») чи виду, а поки сервер не підтвердив — скільки чужих ще живі + 1.
  function myRank(st) {
    if (st.net.rank) return st.net.rank;
    const b = myBoard(st);
    if (b && !b.a && b.rk) return b.rk;
    let alive = 0;
    for (const s of seatsInRound(st)) if (s !== st.ctx.seat && st.others[s] && st.others[s].a) alive++;
    return alive + 1;
  }

  /// Клавіші, якими «Натисни будь-яку» не стартує: вийти, фокус, F5/F11 і самі модифікатори.
  const NOT_ANY = /^(Escape|Tab|F\d+|Meta.*|Alt.*|Control.*|OS.*|ContextMenu|CapsLock|NumLock|ScrollLock|Pause|PrintScreen)$/;

  function onKey(e, ctx) {
    const st = ctx._bk;
    if (!st || !ctx.mine) return false;
    // спринт дограно: Enter чи R — ще раз, без пошуку кнопки мишкою
    if (st.sprint && ctx.room && ctx.room.status === 'finished' && (e.code === 'Enter' || e.code === 'NumpadEnter' || e.code === 'KeyR')) {
      if (!e.repeat) rematch(st);
      return true;
    }
    if (!ctx.playing) return false;
    // «Натисни будь-яку клавішу» — справді будь-яку, Enter і літери теж
    if (st.sprint && st.phase === 'ready' && !NOT_ANY.test(e.code || e.key || '')) {
      audioOn(st);
      if (!e.repeat) ready(st);
      return true;
    }
    const k = keyOf(e);
    if (!k) return false;
    audioOn(st);
    if (e.repeat) return true;
    st.net.press(k, performance.now());
    st.dirty = true;
    return true;   // стрілки й пробіл не гортають сторінку
  }

  function unmount(root) {
    const st = root._bk;
    if (!st) return;
    if (st.raf) cancelAnimationFrame(st.raf);
    st.raf = 0;
    clearTimeout(st.idleT);
    st.idleT = 0;
    clearInterval(st.timer);
    if (st.keyup) document.removeEventListener('keyup', st.keyup);
    if (st.onBlur) window.removeEventListener('blur', st.onBlur);
    if (st.onResize) window.removeEventListener('resize', st.onResize);
    if (st.onVis) document.removeEventListener('visibilitychange', st.onVis);
    if (st.audio) { try { st.audio.close(); } catch { /* уже закрито */ } }
    st.root = null;
    root._bk = null;
  }

  const pad = {
    dirs: true, a: 'KeyX', x: 'Space',
    on(btn, ctx) {
      const st = ctx._bk;
      if (!st) return false;
      const now = performance.now();
      if (st.sprint && st.phase === 'ready' && (btn === 'y' || btn === 'lb' || btn === 'rb')) { ready(st); return true; }
      if (btn === 'y') { st.net.press('HOLD', now); return true; }
      if (btn === 'lb') { st.net.press('CCW', now); return true; }
      if (btn === 'rb') { st.net.press('CW', now); return true; }
      return false;
    },
    when(ctx) {
      const st = ctx._bk;
      if (!ctx.mine || !ctx.playing || !st) return false;
      if (st.sprint && st.phase === 'ready') return true;
      return !st.net.loaded || st.net.core.alive;
    },
    hint: '{dpad} рух і м’яко · {a} крутити · {x} кинути · {y} сховати · {lb}{rb} оберти',
  };

  const common = {
    icon: ICON,
    seatNames: SEATS,
    seatClass: ['bk0', 'bk1', 'bk2', 'bk3'],
    pad,
    mount(root, ctx) { mountCommon(root, ctx); },
    update,
    frame,
    unmount,
    onKey,
    status,
  };

  HGames.register(Object.assign({
    id: 'bricks',
    added: '2026-09-27',
    news: {
      v: '2026-09-29',
      title: 'Цеглини: коротші дуелі, рейтинг і забіг дня',
      items: [
        '⛰ Опція «Земля росте: з 2:30» — рівні суперники більше не мучаться по п’ять хвилин до раптової смерті',
        '⚔ «Цеглини: дуель на рейтинг» — один на один з Ело (можна й на черепки), земля там росте вже з 2:30',
        '📅 «Цеглини дня» в Соло: одне зерно на всіх на цілу добу, однакові фігурки й табло дня над стіною',
      ],
    },
  }, common));
  HGames.register(Object.assign({ id: 'bricks-sprint', added: '2026-09-27' }, common, { seatNames: ['муляр'] }));
  HGames.register(Object.assign({ id: 'bricks-duel', added: '2026-09-29' }, common));
  HGames.register(Object.assign({ id: 'bricks-daily', added: '2026-09-29' }, common, { seatNames: ['муляр'] }));
})();
