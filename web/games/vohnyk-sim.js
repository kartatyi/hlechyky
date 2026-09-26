/*
  «Вогник і Крапля»: детермінована симуляція рівня — слово в слово те саме, що src/Hlechyky/Games/Impl/VohnykWorld.cs.
  Клієнт крутить її, щоб свій герой рухався миттєво (передбачення), сервер — щоб судити. Лише цілі числа:
  ділення — Math.trunc, хеш — Math.imul, жодного Math.round і жодної випадковості. Будь-яка правка тут — дослівно
  й у C#, інакше впадуть тести паритету (docs/games/dev/vohnyk-parity.js і Every_level_solution_matches_its_recorded_check_hashes).

  Одиниці: субпікселі (su), 1 px = 16 su, плитка = 640 su; крок = 20 мс; швидкості — su/крок.
  Без DOM: window.VohnykSim у браузері, module.exports — для майбутнього Node.
*/
(function (root) {
  'use strict';

  const Px = 16, T = 640;
  const HeroW = 384, HeroH = 576;
  const RunMax = 72, RunAcc = 12, AirAcc = 8, Friction = 16, AirDrag = 4;
  const Gravity = 12, FallMax = 240;
  const JumpV = 190, JumpMin = 4, Coyote = 4, BufferSteps = 5;
  const BoxSize = 640, BoxPush = 32;
  const DoorSpeed = 96, LiftSpeed = 48, LiftH = 256;
  const ExitHold = 10;
  const FeetInset = 4 * Px, FeetH = 16 * Px;
  const KeyLeft = 1, KeyRight = 2, KeyJump = 4;
  const HeroInts = 14;
  const Air = 0, Stone = 1, Water = 2, Lava = 3, Mud = 4;

  const trunc = Math.trunc;
  const floorDiv = (a, b) => (a >= 0 ? trunc(a / b) : -trunc((-a + b - 1) / b));
  const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
  const overlap = (ax, ay, aw, ah, bx, by, bw, bh) => ax < bx + bw && ax + aw > bx && ay < by + bh && ay + ah > by;
  const solidFor = (t, who) => t === Stone || (who === 0 && t === Lava) || (who === 1 && t === Water);

  /** Рівень із виду (або з файла): плитки байтами, сутності з масками сигналів — як VohnykLevels.Build. */
  function parseLevel(src) {
    const W = src.w, H = src.h;
    const tiles = new Uint8Array(W * H);
    for (let r = 0; r < H && r < src.rows.length; r++) {
      const row = src.rows[r];
      for (let c = 0; c < W && c < row.length; c++) {
        const ch = row[c];
        tiles[r * W + c] = ch === '#' ? Stone : ch === 'W' ? Water : ch === 'L' ? Lava : ch === 'M' ? Mud : Air;
      }
    }
    const signals = {};
    let n = 0;
    for (const b of src.buttons || []) if (!(b.id in signals)) signals[b.id] = n++;
    for (const l of src.levers || []) if (!(l.id in signals)) signals[l.id] = n++;
    const mask = (by) => { let m = 0; for (const id of by || []) m |= 1 << signals[id]; return m; };
    const who = (s) => (s === 'fire' ? 0 : 1);
    return {
      n: src.n, W, H, tiles,
      spawn: [src.spawn.fire, src.spawn.water],
      exits: [src.exits.fire, src.exits.water],
      gems: (src.gems || []).map((g) => ({ who: who(g.who), col: g.at[0], row: g.at[1] })),
      buttons: (src.buttons || []).map((b) => ({ id: b.id, col: b.at[0], row: b.at[1] })),
      levers: (src.levers || []).map((l) => ({ id: l.id, col: l.at[0], row: l.at[1], init: l.init || 0 })),
      doors: (src.doors || []).map((d) => ({ id: d.id, col: d.at[0], row: d.at[1], tiles: d.h || 2, mask: mask(d.by), all: d.mode === 'all', inv: !!d.inv })),
      lifts: (src.lifts || []).map((l) => ({ id: l.id, col: l.at[0], row: l.at[1], tiles: l.w || 2, toCol: l.to[0], toRow: l.to[1], mask: mask(l.by), all: l.mode === 'all', inv: !!l.inv })),
      boxes: (src.boxes || []).map((b) => ({ col: b.at[0], row: b.at[1] })),
    };
  }

  function World(src) {
    const L = src.tiles ? src : parseLevel(src);
    this.L = L;
    const nb = this.nb = L.buttons.length, nl = this.nl = L.levers.length, nd = this.nd = L.doors.length;
    const nf = this.nf = L.lifts.length, nx = this.nx = L.boxes.length;
    const I = (k) => new Int32Array(k);
    this.X = I(2); this.Y = I(2); this.Vx = I(2); this.Vy = I(2); this.K = I(2);
    this.Grounded = I(2); this.Facing = I(2); this.CoyoteLeft = I(2); this.Buffer = I(2); this.JumpAge = I(2);
    this.WantCut = I(2); this.InExit = I(2); this.Died = I(2); this.LeverIn = I(2);
    this.BoxX = I(nx); this.BoxY = I(nx); this.BoxVy = I(nx);
    this.DoorO = I(nd); this.LiftX = I(nf); this.LiftY = I(nf); this.Lever = I(nl); this.Button = I(nb);
    this.Gems = 0; this.Hold = 0; this.Cleared = 0;
    this._doorX = I(nd); this._doorY = I(nd); this._doorH = I(nd);
    for (let i = 0; i < nd; i++) { const d = L.doors[i]; this._doorX[i] = d.col * T; this._doorY[i] = d.row * T; this._doorH[i] = d.tiles * T; }
    this._liftAX = I(nf); this._liftAY = I(nf); this._liftBX = I(nf); this._liftBY = I(nf); this._liftW = I(nf);
    for (let i = 0; i < nf; i++) {
      const l = L.lifts[i];
      this._liftAX[i] = l.col * T; this._liftAY[i] = l.row * T; this._liftBX[i] = l.toCol * T; this._liftBY[i] = l.toRow * T; this._liftW[i] = l.tiles * T;
    }
    const ng = L.gems.length;
    this._gemX = I(ng); this._gemY = I(ng);
    for (let i = 0; i < ng; i++) { this._gemX[i] = L.gems[i].col * T + 10 * Px; this._gemY[i] = L.gems[i].row * T + 10 * Px; }
    this._btnX = I(nb); this._btnY = I(nb);
    for (let i = 0; i < nb; i++) { this._btnX[i] = L.buttons[i].col * T + 4 * Px; this._btnY[i] = L.buttons[i].row * T + 32 * Px; }
    this._levX = I(nl); this._levY = I(nl);
    for (let i = 0; i < nl; i++) { this._levX[i] = L.levers[i].col * T; this._levY[i] = L.levers[i].row * T; }
    this._exitX = I(2); this._exitY = I(2);
    for (let i = 0; i < 2; i++) { this._exitX[i] = L.exits[i][0] * T; this._exitY[i] = L.exits[i][1] * T; }
    this._riderHero = [false, false];
    this._riderBox = new Array(nx).fill(false);
    this.stateLength = 2 * HeroInts + 3 * nx + nd + 2 * nf + nl + nb + 3;
    this._hashBuf = new Int32Array(this.stateLength);
    this.reset(false);
  }

  const P = World.prototype;

  P.tile = function (c, r) {
    const L = this.L;
    return c < 0 || r < 0 || c >= L.W || r >= L.H ? Stone : L.tiles[r * L.W + c];
  };

  P.signalMask = function () {
    let m = 0;
    for (let i = 0; i < this.nb; i++) if (this.Button[i] !== 0) m |= 1 << i;
    for (let i = 0; i < this.nl; i++) if (this.Lever[i] !== 0) m |= 1 << (this.nb + i);
    return m;
  };

  P.anyDied = function () { return this.Died[0] !== 0 || this.Died[1] !== 0; };

  // ============================================================================================
  // Скидання, знімки, хеш
  // ============================================================================================

  P.reset = function (keepGems) {
    const L = this.L;
    for (let i = 0; i < 2; i++) {
      const c = L.spawn[i][0], r = L.spawn[i][1];
      this.X[i] = c * T + 8 * Px;
      this.Y[i] = (r + 1) * T - HeroH;
      this.Vx[i] = 0; this.Vy[i] = 0;
      this.Grounded[i] = 1; this.Facing[i] = 1; this.CoyoteLeft[i] = Coyote; this.Buffer[i] = 0; this.JumpAge[i] = -1;
      this.WantCut[i] = 0; this.InExit[i] = 0; this.Died[i] = 0; this.LeverIn[i] = 0;
    }
    for (let i = 0; i < this.nx; i++) { this.BoxX[i] = L.boxes[i].col * T; this.BoxY[i] = L.boxes[i].row * T; this.BoxVy[i] = 0; }
    for (let i = 0; i < this.nd; i++) this.DoorO[i] = 0;
    for (let i = 0; i < this.nf; i++) { this.LiftX[i] = this._liftAX[i]; this.LiftY[i] = this._liftAY[i]; }
    for (let i = 0; i < this.nl; i++) this.Lever[i] = L.levers[i].init;
    for (let i = 0; i < this.nb; i++) this.Button[i] = 0;
    if (!keepGems) this.Gems = 0;
    this.Hold = 0;
    this.Cleared = 0;
    this.updateSignals();
  };

  P.save = function (s) {
    let p = 0;
    for (let i = 0; i < 2; i++) {
      s[p++] = this.X[i]; s[p++] = this.Y[i]; s[p++] = this.Vx[i]; s[p++] = this.Vy[i]; s[p++] = this.K[i];
      s[p++] = this.Grounded[i]; s[p++] = this.Facing[i]; s[p++] = this.CoyoteLeft[i]; s[p++] = this.Buffer[i]; s[p++] = this.JumpAge[i];
      s[p++] = this.WantCut[i]; s[p++] = this.InExit[i]; s[p++] = this.Died[i]; s[p++] = this.LeverIn[i];
    }
    for (let i = 0; i < this.nx; i++) { s[p++] = this.BoxX[i]; s[p++] = this.BoxY[i]; s[p++] = this.BoxVy[i]; }
    for (let i = 0; i < this.nd; i++) s[p++] = this.DoorO[i];
    for (let i = 0; i < this.nf; i++) { s[p++] = this.LiftX[i]; s[p++] = this.LiftY[i]; }
    for (let i = 0; i < this.nl; i++) s[p++] = this.Lever[i];
    for (let i = 0; i < this.nb; i++) s[p++] = this.Button[i];
    s[p++] = this.Gems; s[p++] = this.Hold; s[p] = this.Cleared;
  };

  P.load = function (s) {
    let p = 0;
    for (let i = 0; i < 2; i++) {
      this.X[i] = s[p++]; this.Y[i] = s[p++]; this.Vx[i] = s[p++]; this.Vy[i] = s[p++]; this.K[i] = s[p++];
      this.Grounded[i] = s[p++]; this.Facing[i] = s[p++]; this.CoyoteLeft[i] = s[p++]; this.Buffer[i] = s[p++]; this.JumpAge[i] = s[p++];
      this.WantCut[i] = s[p++]; this.InExit[i] = s[p++]; this.Died[i] = s[p++]; this.LeverIn[i] = s[p++];
    }
    for (let i = 0; i < this.nx; i++) { this.BoxX[i] = s[p++]; this.BoxY[i] = s[p++]; this.BoxVy[i] = s[p++]; }
    for (let i = 0; i < this.nd; i++) this.DoorO[i] = s[p++];
    for (let i = 0; i < this.nf; i++) { this.LiftX[i] = s[p++]; this.LiftY[i] = s[p++]; }
    for (let i = 0; i < this.nl; i++) this.Lever[i] = s[p++];
    for (let i = 0; i < this.nb; i++) this.Button[i] = s[p++];
    this.Gems = s[p++]; this.Hold = s[p++]; this.Cleared = s[p];
  };

  /** FNV-1a 32-біт над знімком (кожне ціле — 4 байти little-endian), беззнаково. */
  function hashArr(s, len) {
    let h = 0x811c9dc5;
    for (let i = 0; i < len; i++) {
      const v = s[i] >>> 0;
      h = Math.imul(h ^ (v & 0xff), 16777619) >>> 0;
      h = Math.imul(h ^ ((v >>> 8) & 0xff), 16777619) >>> 0;
      h = Math.imul(h ^ ((v >>> 16) & 0xff), 16777619) >>> 0;
      h = Math.imul(h ^ (v >>> 24), 16777619) >>> 0;
    }
    return h >>> 0;
  }

  P.hash = function () {
    this.save(this._hashBuf);
    return hashArr(this._hashBuf, this.stateLength);
  };

  // ============================================================================================
  // Крок: ліфти → двері → герої → скрині → сигнали → самоцвіти → небезпека → виходи
  // ============================================================================================

  P.step = function (kFire, kWater) {
    const sig = this.signalMask();
    for (let i = 0; i < this.nf; i++) this.stepLift(i, sig);
    for (let i = 0; i < this.nd; i++) this.stepDoor(i, sig);
    this.stepHero(0, kFire & 7);
    this.stepHero(1, kWater & 7);
    for (let i = 0; i < this.nx; i++) this.stepBox(i);
    this.updateSignals();
    const gems = this.L.gems;
    for (let g = 0; g < gems.length; g++) {
      if ((this.Gems & (1 << g)) !== 0) continue;
      const who = gems[g].who;
      if (overlap(this.X[who], this.Y[who], HeroW, HeroH, this._gemX[g], this._gemY[g], 20 * Px, 20 * Px)) this.Gems |= 1 << g;
    }
    for (let i = 0; i < 2; i++) if (this.Died[i] === 0 && this.feetInDanger(i)) this.Died[i] = 1;
    for (let i = 0; i < 2; i++) {
      const cx = this.X[i] + trunc(HeroW / 2), cy = this.Y[i] + trunc(HeroH / 2);
      this.InExit[i] = this.Grounded[i] !== 0 && cx >= this._exitX[i] && cx < this._exitX[i] + T && cy >= this._exitY[i] && cy < this._exitY[i] + 2 * T ? 1 : 0;
    }
    if (this.InExit[0] !== 0 && this.InExit[1] !== 0) {
      this.Hold++;
      if (this.Hold >= ExitHold) this.Cleared = 1;
    } else this.Hold = 0;
  };

  function active(sig, mask, all, inv) {
    const on = all ? (sig & mask) === mask : (sig & mask) !== 0;
    return on !== inv;
  }

  // ---------- ліфти ----------

  P.stepLift = function (i, sig) {
    const def = this.L.lifts[i];
    const act = active(sig, def.mask, def.all, def.inv);
    const tx = act ? this._liftBX[i] : this._liftAX[i];
    const ty = act ? this._liftBY[i] : this._liftAY[i];
    const dx = clamp(tx - this.LiftX[i], -LiftSpeed, LiftSpeed);
    const dy = clamp(ty - this.LiftY[i], -LiftSpeed, LiftSpeed);
    if (dx === 0 && dy === 0) return;
    const lx = this.LiftX[i], ly = this.LiftY[i], lw = this._liftW[i];
    const rh = this._riderHero, rb = this._riderBox;
    rh[0] = rh[1] = false;
    for (let b = 0; b < this.nx; b++)
      rb[b] = this.BoxY[b] + BoxSize === ly && this.BoxX[b] < lx + lw && this.BoxX[b] + BoxSize > lx;
    for (let h = 0; h < 2; h++) {
      const feet = this.Y[h] + HeroH;
      if (feet === ly && this.X[h] < lx + lw && this.X[h] + HeroW > lx) { rh[h] = true; continue; }
      for (let b = 0; b < this.nx; b++)
        if (rb[b] && feet === this.BoxY[b] && this.X[h] < this.BoxX[b] + BoxSize && this.X[h] + HeroW > this.BoxX[b]) { rh[h] = true; break; }
    }
    const nx = lx + dx, ny = ly + dy;
    for (let h = 0; h < 2; h++)
      if (!rh[h] && overlap(nx, ny, lw, LiftH, this.X[h], this.Y[h], HeroW, HeroH)) return;
    for (let b = 0; b < this.nx; b++)
      if (!rb[b] && overlap(nx, ny, lw, LiftH, this.BoxX[b], this.BoxY[b], BoxSize, BoxSize)) return;
    for (let h = 0; h < 2; h++)
      if (rh[h] && this.heroBlockedAt(h, this.X[h] + dx, this.Y[h] + dy, i)) return;
    for (let b = 0; b < this.nx; b++)
      if (rb[b] && this.boxBlockedAt(b, this.BoxX[b] + dx, this.BoxY[b] + dy, i)) return;
    this.LiftX[i] = nx;
    this.LiftY[i] = ny;
    for (let h = 0; h < 2; h++) if (rh[h]) { this.X[h] += dx; this.Y[h] += dy; }
    for (let b = 0; b < this.nx; b++) if (rb[b]) { this.BoxX[b] += dx; this.BoxY[b] += dy; }
  };

  P.heroBlockedAt = function (h, x, y, lift) {
    if (this.tileSolidIn(x, y, HeroW, HeroH, h)) return true;
    for (let d = 0; d < this.nd; d++)
      if (this._doorH[d] > this.DoorO[d] && overlap(x, y, HeroW, HeroH, this._doorX[d], this._doorY[d], T, this._doorH[d] - this.DoorO[d])) return true;
    for (let f = 0; f < this.nf; f++)
      if (f !== lift && overlap(x, y, HeroW, HeroH, this.LiftX[f], this.LiftY[f], this._liftW[f], LiftH)) return true;
    for (let b = 0; b < this.nx; b++)
      if (!this._riderBox[b] && overlap(x, y, HeroW, HeroH, this.BoxX[b], this.BoxY[b], BoxSize, BoxSize)) return true;
    return false;
  };

  P.boxBlockedAt = function (box, x, y, lift) {
    if (this.tileSolidIn(x, y, BoxSize, BoxSize, -1)) return true;
    for (let d = 0; d < this.nd; d++)
      if (this._doorH[d] > this.DoorO[d] && overlap(x, y, BoxSize, BoxSize, this._doorX[d], this._doorY[d], T, this._doorH[d] - this.DoorO[d])) return true;
    for (let f = 0; f < this.nf; f++)
      if (f !== lift && overlap(x, y, BoxSize, BoxSize, this.LiftX[f], this.LiftY[f], this._liftW[f], LiftH)) return true;
    for (let b = 0; b < this.nx; b++)
      if (b !== box && !this._riderBox[b] && overlap(x, y, BoxSize, BoxSize, this.BoxX[b], this.BoxY[b], BoxSize, BoxSize)) return true;
    return false;
  };

  // ---------- двері ----------

  P.stepDoor = function (i, sig) {
    const def = this.L.doors[i];
    const h = this._doorH[i];
    if (active(sig, def.mask, def.all, def.inv)) {
      this.DoorO[i] = Math.min(h, this.DoorO[i] + DoorSpeed);
      return;
    }
    if (this.DoorO[i] === 0) return;
    const o = Math.max(0, this.DoorO[i] - DoorSpeed);
    const dh = h - o;
    for (let k = 0; k < 2; k++)
      if (overlap(this._doorX[i], this._doorY[i], T, dh, this.X[k], this.Y[k], HeroW, HeroH)) return;
    for (let b = 0; b < this.nx; b++)
      if (overlap(this._doorX[i], this._doorY[i], T, dh, this.BoxX[b], this.BoxY[b], BoxSize, BoxSize)) return;
    this.DoorO[i] = o;
  };

  // ---------- герої ----------

  P.stepHero = function (i, k) {
    const X = this.X, Y = this.Y, Vx = this.Vx, Vy = this.Vy;
    const edge = (k & KeyJump) !== 0 && (this.K[i] & KeyJump) === 0;
    this.K[i] = k;
    for (let b = 0; b < this.nx; b++)
      if (overlap(X[i], Y[i], HeroW, HeroH, this.BoxX[b], this.BoxY[b], BoxSize, BoxSize)) {
        Y[i] = this.BoxY[b] - HeroH;
        if (Vy[i] > 0) Vy[i] = 0;
      }

    const dir = ((k & KeyRight) !== 0 ? 1 : 0) - ((k & KeyLeft) !== 0 ? 1 : 0);
    const ground = this.Grounded[i] !== 0;
    if (dir !== 0) {
      let acc = ground ? RunAcc : AirAcc;
      if (Vx[i] * dir < 0) acc += ground ? Friction : AirDrag;
      Vx[i] = clamp(Vx[i] + dir * acc, -RunMax, RunMax);
      this.Facing[i] = dir > 0 ? 1 : 0;
    } else {
      const dec = ground ? Friction : AirDrag;
      Vx[i] = Vx[i] > 0 ? Math.max(0, Vx[i] - dec) : Math.min(0, Vx[i] + dec);
    }
    if (Vx[i] !== 0) this.moveHeroX(i, Vx[i]);

    if (edge) this.Buffer[i] = BufferSteps;
    if (this.Buffer[i] > 0 && (ground || this.CoyoteLeft[i] > 0)) {
      Vy[i] = -JumpV;
      this.JumpAge[i] = 0;
      this.WantCut[i] = 0;
      this.Buffer[i] = 0;
      this.CoyoteLeft[i] = 0;
    } else if (this.Buffer[i] > 0 && !edge) this.Buffer[i]--;
    if (this.JumpAge[i] >= 0 && (k & KeyJump) === 0) this.WantCut[i] = 1;
    if (this.WantCut[i] !== 0 && this.JumpAge[i] >= JumpMin && Vy[i] < 0) {
      Vy[i] = trunc(Vy[i] * 2 / 5);
      this.WantCut[i] = 0;
      this.JumpAge[i] = -1;
    }
    if (Vy[i] !== 0) this.moveHeroY(i, Vy[i]);
    Vy[i] = Math.min(Vy[i] + Gravity, FallMax);
    if (this.JumpAge[i] >= 0) {
      this.JumpAge[i]++;
      if (Vy[i] >= 0) { this.JumpAge[i] = -1; this.WantCut[i] = 0; }
    }

    this.Grounded[i] = Vy[i] >= 0 && this.heroSupported(i) ? 1 : 0;
    this.CoyoteLeft[i] = this.Grounded[i] !== 0 ? Coyote : ground ? this.CoyoteLeft[i] : Math.max(0, this.CoyoteLeft[i] - 1);
  };

  P.moveHeroX = function (i, dx) {
    const X = this.X, Y = this.Y;
    const x0 = X[i], y0 = Y[i];
    let limit = x0 + dx;
    const r0 = floorDiv(y0, T), r1 = floorDiv(y0 + HeroH - 1, T);
    let pushed = false;
    if (dx > 0) {
      const front = x0 + HeroW;
      for (let c = floorDiv(front - 1, T) + 1; c <= floorDiv(limit + HeroW - 1, T); c++)
        for (let r = r0; r <= r1; r++)
          if (solidFor(this.tile(c, r), i)) { limit = Math.min(limit, c * T - HeroW); break; }
      for (let d = 0; d < this.nd; d++) {
        const dh = this._doorH[d] - this.DoorO[d];
        if (dh > 0 && this._doorY[d] < y0 + HeroH && this._doorY[d] + dh > y0 && this._doorX[d] >= front && this._doorX[d] < limit + HeroW)
          limit = this._doorX[d] - HeroW;
      }
      for (let f = 0; f < this.nf; f++)
        if (this.LiftY[f] < y0 + HeroH && this.LiftY[f] + LiftH > y0 && this.LiftX[f] >= front && this.LiftX[f] < limit + HeroW)
          limit = this.LiftX[f] - HeroW;
      let box = -1;
      for (let b = 0; b < this.nx; b++)
        if (this.BoxY[b] < y0 + HeroH && this.BoxY[b] + BoxSize > y0 && this.BoxX[b] >= front && this.BoxX[b] < limit + HeroW && (box < 0 || this.BoxX[b] < this.BoxX[box]))
          box = b;
      if (box >= 0) {
        const push = Math.min(limit + HeroW - this.BoxX[box], BoxPush);
        const was = this.BoxX[box];
        this.pushBox(box, push, i);
        pushed = this.BoxX[box] !== was;
        limit = Math.min(limit, this.BoxX[box] - HeroW);
      }
    } else {
      for (let c = floorDiv(x0, T) - 1; c >= floorDiv(limit, T); c--)
        for (let r = r0; r <= r1; r++)
          if (solidFor(this.tile(c, r), i)) { limit = Math.max(limit, (c + 1) * T); break; }
      for (let d = 0; d < this.nd; d++) {
        const dh = this._doorH[d] - this.DoorO[d];
        const right = this._doorX[d] + T;
        if (dh > 0 && this._doorY[d] < y0 + HeroH && this._doorY[d] + dh > y0 && right <= x0 && right > limit) limit = right;
      }
      for (let f = 0; f < this.nf; f++) {
        const right = this.LiftX[f] + this._liftW[f];
        if (this.LiftY[f] < y0 + HeroH && this.LiftY[f] + LiftH > y0 && right <= x0 && right > limit) limit = right;
      }
      let box = -1;
      for (let b = 0; b < this.nx; b++) {
        const right = this.BoxX[b] + BoxSize;
        if (this.BoxY[b] < y0 + HeroH && this.BoxY[b] + BoxSize > y0 && right <= x0 && right > limit && (box < 0 || right > this.BoxX[box] + BoxSize))
          box = b;
      }
      if (box >= 0) {
        const push = Math.max(limit - (this.BoxX[box] + BoxSize), -BoxPush);
        const was = this.BoxX[box];
        this.pushBox(box, push, i);
        pushed = this.BoxX[box] !== was;
        limit = Math.max(limit, this.BoxX[box] + BoxSize);
      }
    }
    if (limit !== x0 + dx) this.Vx[i] = pushed ? clamp(this.Vx[i], -BoxPush, BoxPush) : 0;
    X[i] = limit;
  };

  P.pushBox = function (b, d, pusher) {
    if (d === 0) return;
    const BX = this.BoxX, BY = this.BoxY;
    const bx = BX[b], by = BY[b];
    for (let o = 0; o < this.nx; o++)
      if (o !== b && BY[o] + BoxSize === by && BX[o] < bx + BoxSize && BX[o] + BoxSize > bx) return;
    let limit = bx + d;
    const r0 = floorDiv(by, T), r1 = floorDiv(by + BoxSize - 1, T);
    const other = 1 - pusher;
    if (d > 0) {
      const front = bx + BoxSize;
      for (let c = floorDiv(front - 1, T) + 1; c <= floorDiv(limit + BoxSize - 1, T); c++)
        for (let r = r0; r <= r1; r++)
          if (this.tile(c, r) === Stone) { limit = Math.min(limit, c * T - BoxSize); break; }
      for (let dd = 0; dd < this.nd; dd++) {
        const dh = this._doorH[dd] - this.DoorO[dd];
        if (dh > 0 && this._doorY[dd] < by + BoxSize && this._doorY[dd] + dh > by && this._doorX[dd] >= front && this._doorX[dd] < limit + BoxSize)
          limit = this._doorX[dd] - BoxSize;
      }
      for (let f = 0; f < this.nf; f++)
        if (this.LiftY[f] < by + BoxSize && this.LiftY[f] + LiftH > by && this.LiftX[f] >= front && this.LiftX[f] < limit + BoxSize)
          limit = this.LiftX[f] - BoxSize;
      for (let o = 0; o < this.nx; o++)
        if (o !== b && BY[o] < by + BoxSize && BY[o] + BoxSize > by && BX[o] >= front && BX[o] < limit + BoxSize)
          limit = BX[o] - BoxSize;
      if (this.Y[other] < by + BoxSize && this.Y[other] + HeroH > by && this.X[other] >= front && this.X[other] < limit + BoxSize)
        limit = this.X[other] - BoxSize;
      BX[b] = Math.max(bx, limit);
    } else {
      for (let c = floorDiv(bx, T) - 1; c >= floorDiv(limit, T); c--)
        for (let r = r0; r <= r1; r++)
          if (this.tile(c, r) === Stone) { limit = Math.max(limit, (c + 1) * T); break; }
      for (let dd = 0; dd < this.nd; dd++) {
        const dh = this._doorH[dd] - this.DoorO[dd];
        const right = this._doorX[dd] + T;
        if (dh > 0 && this._doorY[dd] < by + BoxSize && this._doorY[dd] + dh > by && right <= bx && right > limit) limit = right;
      }
      for (let f = 0; f < this.nf; f++) {
        const right = this.LiftX[f] + this._liftW[f];
        if (this.LiftY[f] < by + BoxSize && this.LiftY[f] + LiftH > by && right <= bx && right > limit) limit = right;
      }
      for (let o = 0; o < this.nx; o++) {
        const right = BX[o] + BoxSize;
        if (o !== b && BY[o] < by + BoxSize && BY[o] + BoxSize > by && right <= bx && right > limit) limit = right;
      }
      const hr = this.X[other] + HeroW;
      if (this.Y[other] < by + BoxSize && this.Y[other] + HeroH > by && hr <= bx && hr > limit) limit = hr;
      BX[b] = Math.min(bx, limit);
    }
  };

  P.moveHeroY = function (i, dy) {
    const x0 = this.X[i], y0 = this.Y[i];
    let limit = y0 + dy;
    const c0 = floorDiv(x0, T), c1 = floorDiv(x0 + HeroW - 1, T);
    if (dy > 0) {
      const feet = y0 + HeroH;
      for (let r = floorDiv(feet - 1, T) + 1; r <= floorDiv(limit + HeroH - 1, T); r++) {
        let hit = false;
        for (let c = c0; c <= c1; c++) if (solidFor(this.tile(c, r), i)) { hit = true; break; }
        if (hit) { limit = Math.min(limit, r * T - HeroH); break; }
      }
      for (let d = 0; d < this.nd; d++) {
        const dh = this._doorH[d] - this.DoorO[d];
        if (dh > 0 && this._doorX[d] < x0 + HeroW && this._doorX[d] + T > x0 && this._doorY[d] >= feet && this._doorY[d] < limit + HeroH)
          limit = this._doorY[d] - HeroH;
      }
      for (let f = 0; f < this.nf; f++)
        if (this.LiftX[f] < x0 + HeroW && this.LiftX[f] + this._liftW[f] > x0 && this.LiftY[f] >= feet && this.LiftY[f] < limit + HeroH)
          limit = this.LiftY[f] - HeroH;
      for (let b = 0; b < this.nx; b++)
        if (this.BoxX[b] < x0 + HeroW && this.BoxX[b] + BoxSize > x0 && this.BoxY[b] >= feet && this.BoxY[b] < limit + HeroH)
          limit = this.BoxY[b] - HeroH;
      if (limit !== y0 + dy) this.Vy[i] = 0;
    } else {
      for (let r = floorDiv(y0, T) - 1; r >= floorDiv(limit, T); r--) {
        let hit = false;
        for (let c = c0; c <= c1; c++) if (solidFor(this.tile(c, r), i)) { hit = true; break; }
        if (hit) { limit = Math.max(limit, (r + 1) * T); break; }
      }
      for (let d = 0; d < this.nd; d++) {
        const bottom = this._doorY[d] + this._doorH[d] - this.DoorO[d];
        if (bottom > this._doorY[d] && this._doorX[d] < x0 + HeroW && this._doorX[d] + T > x0 && bottom <= y0 && bottom > limit) limit = bottom;
      }
      for (let f = 0; f < this.nf; f++) {
        const bottom = this.LiftY[f] + LiftH;
        if (this.LiftX[f] < x0 + HeroW && this.LiftX[f] + this._liftW[f] > x0 && bottom <= y0 && bottom > limit) limit = bottom;
      }
      for (let b = 0; b < this.nx; b++) {
        const bottom = this.BoxY[b] + BoxSize;
        if (this.BoxX[b] < x0 + HeroW && this.BoxX[b] + BoxSize > x0 && bottom <= y0 && bottom > limit) limit = bottom;
      }
      if (limit !== y0 + dy) { this.Vy[i] = 0; this.JumpAge[i] = -1; this.WantCut[i] = 0; }
    }
    this.Y[i] = limit;
  };

  P.heroSupported = function (i) {
    const x = this.X[i], feet = this.Y[i] + HeroH;
    const r = floorDiv(feet, T);
    if (feet % T === 0)
      for (let c = floorDiv(x, T); c <= floorDiv(x + HeroW - 1, T); c++)
        if (solidFor(this.tile(c, r), i)) return true;
    for (let d = 0; d < this.nd; d++)
      if (this._doorY[d] === feet && this._doorH[d] - this.DoorO[d] > 0 && this._doorX[d] < x + HeroW && this._doorX[d] + T > x) return true;
    for (let f = 0; f < this.nf; f++)
      if (this.LiftY[f] === feet && this.LiftX[f] < x + HeroW && this.LiftX[f] + this._liftW[f] > x) return true;
    for (let b = 0; b < this.nx; b++)
      if (this.BoxY[b] === feet && this.BoxX[b] < x + HeroW && this.BoxX[b] + BoxSize > x) return true;
    return false;
  };

  // ---------- скрині ----------

  P.tipIntoGap = function (b) {
    const x = this.BoxX[b], y = this.BoxY[b];
    const c0 = floorDiv(x, T);
    const off = x - c0 * T;
    const bottom = y + BoxSize;
    if (off === 0 || bottom % T !== 0) return;
    const r = trunc(bottom / T);
    const left = this.tile(c0, r) === Stone;
    const right = this.tile(c0 + 1, r) === Stone;
    if (left === right) return;
    for (let f = 0; f < this.nf; f++)
      if (this.LiftY[f] === bottom && this.LiftX[f] < x + BoxSize && this.LiftX[f] + this._liftW[f] > x) return;
    for (let d = 0; d < this.nd; d++)
      if (this._doorY[d] === bottom && this._doorH[d] > this.DoorO[d] && this._doorX[d] < x + BoxSize && this._doorX[d] + T > x) return;
    for (let o = 0; o < this.nx; o++)
      if (o !== b && this.BoxY[o] === bottom && this.BoxX[o] < x + BoxSize && this.BoxX[o] + BoxSize > x) return;
    let nx;
    if (left) {
      if (T - off >= trunc(T / 2)) return;
      nx = (c0 + 1) * T;
    } else {
      if (off >= trunc(T / 2)) return;
      nx = c0 * T;
    }
    const col = trunc(nx / T);
    for (let rr = floorDiv(y, T); rr <= floorDiv(y + BoxSize - 1, T); rr++)
      if (this.tile(col, rr) === Stone) return;
    for (let d = 0; d < this.nd; d++)
      if (this._doorH[d] > this.DoorO[d] && overlap(nx, y, BoxSize, BoxSize, this._doorX[d], this._doorY[d], T, this._doorH[d] - this.DoorO[d])) return;
    for (let f = 0; f < this.nf; f++)
      if (overlap(nx, y, BoxSize, BoxSize, this.LiftX[f], this.LiftY[f], this._liftW[f], LiftH)) return;
    for (let o = 0; o < this.nx; o++)
      if (o !== b && overlap(nx, y, BoxSize, BoxSize, this.BoxX[o], this.BoxY[o], BoxSize, BoxSize)) return;
    for (let h = 0; h < 2; h++)
      if (overlap(nx, y, BoxSize, BoxSize, this.X[h], this.Y[h], HeroW, HeroH)) return;
    this.BoxX[b] = nx;
  };

  P.stepBox = function (b) {
    this.tipIntoGap(b);
    this.BoxVy[b] = Math.min(this.BoxVy[b] + Gravity, FallMax);
    const dy = this.BoxVy[b];
    const x0 = this.BoxX[b], y0 = this.BoxY[b];
    let limit = y0 + dy;
    const c0 = floorDiv(x0, T), c1 = floorDiv(x0 + BoxSize - 1, T);
    const bottom = y0 + BoxSize;
    for (let r = floorDiv(bottom - 1, T) + 1; r <= floorDiv(limit + BoxSize - 1, T); r++) {
      let hit = false;
      for (let c = c0; c <= c1; c++) if (this.tile(c, r) === Stone) { hit = true; break; }
      if (hit) { limit = Math.min(limit, r * T - BoxSize); break; }
    }
    for (let d = 0; d < this.nd; d++) {
      const dh = this._doorH[d] - this.DoorO[d];
      if (dh > 0 && this._doorX[d] < x0 + BoxSize && this._doorX[d] + T > x0 && this._doorY[d] >= bottom && this._doorY[d] < limit + BoxSize)
        limit = this._doorY[d] - BoxSize;
    }
    for (let f = 0; f < this.nf; f++)
      if (this.LiftX[f] < x0 + BoxSize && this.LiftX[f] + this._liftW[f] > x0 && this.LiftY[f] >= bottom && this.LiftY[f] < limit + BoxSize)
        limit = this.LiftY[f] - BoxSize;
    for (let o = 0; o < this.nx; o++)
      if (o !== b && this.BoxX[o] < x0 + BoxSize && this.BoxX[o] + BoxSize > x0 && this.BoxY[o] >= bottom && this.BoxY[o] < limit + BoxSize)
        limit = this.BoxY[o] - BoxSize;
    if (limit !== y0 + dy) this.BoxVy[b] = 0;
    this.BoxY[b] = limit;
  };

  // ---------- сигнали, небезпека ----------

  P.updateSignals = function () {
    for (let i = 0; i < this.nb; i++) {
      let on = 0;
      for (let h = 0; h < 2 && on === 0; h++)
        if (overlap(this._btnX[i], this._btnY[i], 32 * Px, 8 * Px, this.X[h], this.Y[h], HeroW, HeroH)) on = 1;
      for (let b = 0; b < this.nx && on === 0; b++)
        if (overlap(this._btnX[i], this._btnY[i], 32 * Px, 8 * Px, this.BoxX[b], this.BoxY[b], BoxSize, BoxSize)) on = 1;
      this.Button[i] = on;
    }
    for (let h = 0; h < 2; h++) {
      let mask = 0;
      for (let i = 0; i < this.nl; i++) {
        const inside = overlap(this.X[h], this.Y[h], HeroW, HeroH, this._levX[i], this._levY[i], T, T);
        if (inside) { mask |= 1 << i; continue; }
        if ((this.LeverIn[h] & (1 << i)) === 0) continue;
        if (this.X[h] >= this._levX[i] + T) this.Lever[i] = 1;
        else if (this.X[h] + HeroW <= this._levX[i]) this.Lever[i] = 0;
      }
      this.LeverIn[h] = mask;
    }
  };

  P.deathTile = function (i) {
    const fx0 = this.X[i] + FeetInset, fx1 = this.X[i] + HeroW - FeetInset;
    const fy1 = this.Y[i] + HeroH, fy0 = fy1 - FeetH;
    let found = Air;
    for (let r = floorDiv(fy0, T); r <= floorDiv(fy1 - 1, T); r++)
      for (let c = floorDiv(fx0, T); c <= floorDiv(fx1 - 1, T); c++) {
        const t = this.tile(c, r);
        if (t === Mud) return t;
        if ((t === Water && i === 0) || (t === Lava && i === 1)) found = t;
      }
    return found;
  };

  P.feetInDanger = function (i) {
    const fx0 = this.X[i] + FeetInset, fx1 = this.X[i] + HeroW - FeetInset;
    const fy1 = this.Y[i] + HeroH, fy0 = fy1 - FeetH;
    for (let r = floorDiv(fy0, T); r <= floorDiv(fy1 - 1, T); r++)
      for (let c = floorDiv(fx0, T); c <= floorDiv(fx1 - 1, T); c++) {
        const t = this.tile(c, r);
        if (t === Mud || (t === Water && i === 0) || (t === Lava && i === 1)) return true;
      }
    return false;
  };

  P.tileSolidIn = function (x, y, w, h, who) {
    for (let r = floorDiv(y, T); r <= floorDiv(y + h - 1, T); r++)
      for (let c = floorDiv(x, T); c <= floorDiv(x + w - 1, T); c++)
        if (solidFor(this.tile(c, r), who)) return true;
    return false;
  };

  /**
   * Прогнати журнал проходження (як VohnykRecord.Replay у тестах): [крок від 1, герой, k]. Повертає
   * { clearedAt, steps, hash, hashes, gems, diedAt } — хеші беззнакові.
   */
  function replay(level, solution, every, maxSteps) {
    const w = new World(level);
    const k = [0, 0];
    const log = solution.slice().sort((a, b) => a[0] - b[0]);
    const hashes = [];
    let p = 0;
    maxSteps = maxSteps || 20000;
    for (let s = 1; s <= maxSteps; s++) {
      while (p < log.length && log[p][0] <= s) { k[log[p][1]] = log[p][2]; p++; }
      w.step(k[0], k[1]);
      if (every && s % every === 0) hashes.push(w.hash());
      if (w.anyDied()) return { clearedAt: 0, steps: s, hash: w.hash(), hashes, gems: w.Gems, diedAt: s };
      if (w.Cleared !== 0) return { clearedAt: s, steps: s, hash: w.hash(), hashes, gems: w.Gems, diedAt: 0 };
    }
    return { clearedAt: 0, steps: maxSteps, hash: w.hash(), hashes, gems: w.Gems, diedAt: 0 };
  }

  const api = {
    create: (level) => new World(level),
    parseLevel,
    hashArr,
    replay,
    step: (w, kF, kW) => w.step(kF, kW),
    save: (w, s) => w.save(s),
    load: (w, s) => w.load(s),
    hash: (w) => w.hash(),
    C: { Px, T, HeroW, HeroH, BoxSize, LiftH, KeyLeft, KeyRight, KeyJump, HeroInts, Air, Stone, Water, Lava, Mud, ExitHold },
  };
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.VohnykSim = api;
})(typeof window !== 'undefined' ? window : this);
