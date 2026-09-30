/*
  Сільське ралі. Гонки згори на всю трасу: сервер (Impl/Rally.cs + RallyCore.cs) тикає раз на 40 мс і
  лишається суддею, а своя машина їде миттєво — клієнт рахує її тією самою цілочисельною фізикою
  (RallySim нижче — порт RallyCore рядок у рядок) і лише плавно підправляє, коли сервер каже інакше.

  Вид (раз, і на події): { ph, laps, random, track: { id, title, map[27], gates, gateA, gateC, slots, heading,
    night, ice, corn, line }, cars[6], records[{nick, ms, car}], results[{seat, fin, ms, best, laps}] | null, f }
  Кадр (25/с): { t, ph, s, c: int[90], r: int[6] } — по 15 чисел на місце:
    x y (sub = 1/64 u) · a (0..1023) · vf vl · k (маска) · lap · g (наступні ворота) · ev (біти подій) ·
    tm (air | oil<<4 | stall<<9 | boostT<<14 | boostCd<<19) · fin (−1 — нема) · bl ll (мс) · lt (запізнення вводу) · it (тик вводу)
  Ввід: Input('ctl', { t, k }) — маска 1 ← 2 → 4 газ 8 гальмо 16 ручник, чинна з тика t (лише на зміну);
        Input('reset') — назад на трасу; Input('horn'); Act('car', { car }) — машина в лобі.
*/
(() => {
  'use strict';

  // ===============================================================================================
  // RallySim — детермінована симуляція, двійник RallyCore.cs. Лише цілі числа; ділення — з усіченням
  // до нуля (`| 0`), зсуви — арифметичні. Порядок операцій не міняти без C# і журналів паритету.
  // ===============================================================================================
  const RallySim = (() => {
    const COLS = 48, ROWS = 27, CELLS = COLS * ROWS, CELL = 2048, SHIFT = 11;
    const SEATS = 6, TICK_MS = 40, COUNT = 75;
    const WORLD_W = COLS * CELL, WORLD_H = ROWS * CELL;
    const RWALL = 640, RCAR = 768, RHAY = 896;
    const BRAKE = 58, REV = 19, MAX_REV = 320, TURN = 28, VTURN = 256, ROLL = 2, STOP_V = 24;
    const BOOST_ADD = 384, BOOST_CAP = 1408, BOOST_TICKS = 25, BOOST_CD = 40, BOOST_DRAG = 4;
    const JUMP_MIN = 512, AIR_TICKS = 14, OIL_TICKS = 30, OIL_GRIP = 10, HB_GRIP = 30;
    const STALL = 25, RESET_CD = 75, HORN_CD = 25, MAX_AHEAD = 10;
    const E_WALL = 90, WALL_FRICTION = 64, HAY_BACK = 320, HAY_DAMP = 160, HAY_EV_MIN = 32, E_CAR = 384, HIT_EV_MIN = 128;
    const EV = { wall: 1, car: 2, lap: 4, boost: 8, puddle: 16, oil: 32, hay: 64, jump: 128, horn: 256, land: 512, reset: 1024, critter: 2048 };

    // поверхні: = ~ * . c M o + J H # T D W
    const ROAD = 0, PUDDLE = 1, ICE = 2, GRASS = 3, CORN = 4, MUD = 5, OIL = 6, BOOST = 7, RAMP = 8, HAY = 9;
    const FENCE = 10, TREE = 11, HOUSE = 12, WATER = 13;
    const DRAG = [16, 16, 12, 32, 32, 64, 16, 16, 16, 16, 16, 16, 16, 16];
    const GRIP = [110, 40, 14, 70, 70, 90, 110, 110, 110, 110, 110, 110, 110, 110];
    const ACC = [56, 56, 44, 28, 28, 28, 56, 56, 56, 56, 56, 56, 56, 56];
    const SURF = { '=': ROAD, '~': PUDDLE, '*': ICE, '.': GRASS, c: CORN, M: MUD, o: OIL, '+': BOOST, J: RAMP, H: HAY, '#': FENCE, T: TREE, D: HOUSE, W: WATER };
    const isWall = (code) => code >= FENCE && code <= WATER;

    // sin/cos на 1024 кроки в масштабі 2¹⁴: floor(x + 0.5), як у C#
    const SIN = new Int32Array(1024), COS = new Int32Array(1024);
    for (let i = 0; i < 1024; i++) {
      SIN[i] = Math.floor(Math.sin(2 * Math.PI * i / 1024) * 16384 + 0.5);
      COS[i] = Math.floor(Math.cos(2 * Math.PI * i / 1024) * 16384 + 0.5);
    }

    const FNV_START = 0x811c9dc5, FNV_PRIME = 0x01000193;
    function mix(h, v) {
      for (let b = 0; b < 4; b++) {
        h = (h ^ ((v >> (8 * b)) & 0xff)) >>> 0;
        h = Math.imul(h, FNV_PRIME) >>> 0;
      }
      return h;
    }
    const hex = (h) => (h >>> 0).toString(16).padStart(8, '0');

    /// Траса з виду (або журналу): ті самі похідні масиви, що рахує RallyTrack.cs.
    function buildTrack(td) {
      const map = td.map, gates = td.gates, K = gates.length;
      const tile = new Uint8Array(CELLS), gateAt = new Uint8Array(CELLS).fill(255);
      for (let y = 0; y < ROWS; y++) {
        for (let x = 0; x < COLS; x++) {
          const code = SURF[map[y][x]];
          tile[y * COLS + x] = code === undefined ? FENCE : code;
        }
      }
      const gateCX = new Int32Array(K), gateCY = new Int32Array(K), gateA = new Int32Array(K);
      for (let g = 0; g < K; g++) {
        for (const r of gates[g]) {
          for (let y = r[1]; y < r[1] + r[3]; y++) for (let x = r[0]; x < r[0] + r[2]; x++) gateAt[y * COLS + x] = g;
        }
        const f = gates[g][0];
        gateCX[g] = (f[0] * 2 + f[2]) * (CELL / 2);
        gateCY[g] = (f[1] * 2 + f[3]) * (CELL / 2);
      }
      for (let g = 0; g < K; g++) {
        const f = gates[g][0], n = (g + 1) % K;
        gateA[g] = f[2] <= f[3] ? (gateCX[n] >= gateCX[g] ? 0 : 512) : (gateCY[n] >= gateCY[g] ? 256 : 768);
      }
      const l = gates[0][0], prev = K - 1;
      let lineAxis, lineEdge, lineDir;
      if (l[2] === 1) {
        lineAxis = 0;
        const left = l[0] * CELL, right = (l[0] + 1) * CELL;
        const fromLeft = Math.abs(left - gateCX[prev]) <= Math.abs(right - gateCX[prev]);
        lineEdge = fromLeft ? left : right;
        lineDir = fromLeft ? 1 : -1;
      } else {
        lineAxis = 1;
        const top = l[1] * CELL, bottom = (l[1] + 1) * CELL;
        const fromTop = Math.abs(top - gateCY[prev]) <= Math.abs(bottom - gateCY[prev]);
        lineEdge = fromTop ? top : bottom;
        lineDir = fromTop ? 1 : -1;
      }
      const slots = td.slots || [];
      const slotX = slots.map((s) => s[0] * CELL + CELL / 2), slotY = slots.map((s) => s[1] * CELL + CELL / 2);
      return {
        id: td.id, map, gates, K, tile, gateAt, gateCX, gateCY, gateA, slotX, slotY, critters: td.critters || [],
        heading: td.heading | 0, lineAxis, lineEdge, lineDir,
        codeAt: (x, y) => (x < 0 || y < 0 || x >= COLS || y >= ROWS ? FENCE : tile[y * COLS + x]),
      };
    }

    function newCar() {
      return {
        present: false, ghost: false, x: 0, y: 0, a: 0, vf: 0, vl: 0, mask: 0, sched: new Int32Array(16).fill(-1),
        lt: 0, it: 0, cell: 0, lap: 0, next: 1, fin: 0, finishMs: 0, lapStartMs: 0, bestMs: 0, lastMs: 0,
        air: 0, oil: 0, stall: 0, boostT: 0, boostCd: 0, resetCd: 0, hornCd: 0, ev: 0, pendEv: 0, px: 0, py: 0,
        vx: 0, vy: 0, hit: false, car: 'traktor',
      };
    }

    const cellOf = (x, y) => (y >> SHIFT) * COLS + (x >> SHIFT);

    /// Живі перешкоди (№89): радіус за видом і де перешкода на тику t — двійник RallyCore.CritterAt.
    const CRITTER_R = [448, 512, 1152, 512, 576];
    function critterAt(d, live, t, out) {
      const move = d[5], rest = d[6], period = 2 * (move + rest);
      const off = ((live % 9973) * (2 * d[7] + 1) * 131 + d[8]) % period;
      const p = (t + off) % period;
      if (p < move) { out[0] = d[1] + (((d[3] - d[1]) * p / move) | 0); out[1] = d[2] + (((d[4] - d[2]) * p / move) | 0); }
      else if (p < move + rest) { out[0] = d[3]; out[1] = d[4]; }
      else if (p < 2 * move + rest) {
        const q = p - move - rest;
        out[0] = d[3] + (((d[1] - d[3]) * q / move) | 0); out[1] = d[4] + (((d[2] - d[4]) * q / move) | 0);
      } else { out[0] = d[1]; out[1] = d[2]; }
      return out;
    }

    function create(track, laps) {
      const cars = [];
      for (let i = 0; i < SEATS; i++) cars.push(newCar());
      const sim = { track, laps, cars, T: 0, finished: 0, anyLap: false, live: 0 };
      const crN = (track.critters || []).length, ox = new Int32Array(crN), oy = new Int32Array(crN), cp = [0, 0];

      function reset(c) {
        c.present = c.ghost = false;
        c.x = c.y = c.a = c.vf = c.vl = c.mask = c.lt = c.it = 0;
        c.sched.fill(-1);
        c.lap = 0; c.next = 1;
        c.fin = c.finishMs = c.lapStartMs = c.bestMs = c.lastMs = 0;
        c.air = c.oil = c.stall = c.boostT = c.boostCd = c.resetCd = c.hornCd = 0;
        c.ev = c.pendEv = c.px = c.py = c.vx = c.vy = 0;
        c.hit = false;
      }

      sim.grid = (seat, car) => {
        const c = cars[seat];
        reset(c);
        c.present = true;
        c.car = car || 'traktor';
        c.x = track.slotX[seat];
        c.y = track.slotY[seat];
        c.a = track.heading;
        c.cell = cellOf(c.x, c.y);
        return c;
      };

      sim.schedule = (seat, t, k) => {
        const c = cars[seat];
        if (t > sim.T + MAX_AHEAD) t = sim.T + MAX_AHEAD;
        if (t >= sim.T + 1) c.sched[t & 15] = k;
        else c.mask = k;
        c.lt = sim.T + 1 - t;
        c.it = t;
        return c.lt;
      };

      function clamp(c) {
        if (c.x < RWALL) c.x = RWALL; else if (c.x > WORLD_W - RWALL) c.x = WORLD_W - RWALL;
        if (c.y < RWALL) c.y = RWALL; else if (c.y > WORLD_H - RWALL) c.y = WORLD_H - RWALL;
      }

      function controls(c) {
        let drag, grip, acc, gas, steer, hb;
        if (c.air > 0) {
          drag = grip = acc = gas = steer = 0;
          hb = false;
        } else {
          const code = track.tile[c.cell];
          drag = DRAG[code]; grip = GRIP[code]; acc = ACC[code];
          gas = (c.mask & 8) !== 0 ? -1 : (c.mask & 4) !== 0 ? 1 : 0;
          steer = ((c.mask & 2) !== 0 ? 1 : 0) - ((c.mask & 1) !== 0 ? 1 : 0);
          hb = (c.mask & 16) !== 0;
          if (c.stall > 0) { gas = steer = 0; hb = false; }
          if (c.boostT > 0) drag = BOOST_DRAG;
          if (c.oil > 0) grip = OIL_GRIP;
          if (hb && grip > HB_GRIP) grip = HB_GRIP;
        }
        if (gas > 0) c.vf += acc;
        else if (gas < 0) c.vf = c.vf > 0 ? Math.max(0, c.vf - BRAKE) : Math.max(-MAX_REV, c.vf - REV);
        c.vf -= (c.vf * drag / 256) | 0;
        c.vl -= (c.vl * grip / 256) | 0;
        // опір коченню: малу швидкість, якої не бере цілочисельний опір, на землі гасимо кроками до нуля
        if (c.air === 0) {
          if (gas === 0 && c.vf !== 0 && c.vf > -STOP_V && c.vf < STOP_V) c.vf += c.vf > 0 ? -Math.min(ROLL, c.vf) : Math.min(ROLL, -c.vf);
          if (c.vl !== 0 && c.vl > -STOP_V && c.vl < STOP_V) c.vl += c.vl > 0 ? -1 : 1;
        }
        if (c.boostT > 0 && c.vf > BOOST_CAP) c.vf = BOOST_CAP;

        let tr = (TURN * Math.min(Math.abs(c.vf), VTURN) / 256) | 0;
        if (hb) tr = (tr * 3 / 2) | 0;
        if (c.vf < 0) tr = -tr;
        const dA = steer * tr;
        if (dA !== 0) {
          const co = COS[dA & 1023], si = SIN[dA & 1023];
          const vf = (c.vf * co + c.vl * si) >> 14;
          const vl = (-c.vf * si + c.vl * co) >> 14;
          c.vf = vf;
          c.vl = vl;
          c.a = (c.a + dA) & 1023;
        }
        c.vx = (c.vf * COS[c.a] - c.vl * SIN[c.a]) >> 14;
        c.vy = (c.vf * SIN[c.a] + c.vl * COS[c.a]) >> 14;
      }

      const wallAt = (cx, cy) => isWall(track.codeAt(cx, cy));

      function wall(c, cx, cy) {
        const rx = cx << SHIFT, ry = cy << SHIFT, s = CELL;
        const px = c.x < rx ? rx : c.x > rx + s ? rx + s : c.x;
        const py = c.y < ry ? ry : c.y > ry + s ? ry + s : c.y;
        const dx = c.x - px, dy = c.y - py;
        let axis, sign;
        if (dx === 0 && dy === 0) {
          const dl = c.x - rx + (wallAt(cx - 1, cy) ? 4 * s : 0);
          const dr = rx + s - c.x + (wallAt(cx + 1, cy) ? 4 * s : 0);
          const du = c.y - ry + (wallAt(cx, cy - 1) ? 4 * s : 0);
          const dd = ry + s - c.y + (wallAt(cx, cy + 1) ? 4 * s : 0);
          axis = 0; sign = -1;
          let best = dl;
          if (dr < best) { best = dr; sign = 1; }
          if (du < best) { best = du; axis = 1; sign = -1; }
          if (dd < best) { axis = 1; sign = 1; }
          if (axis === 0) c.x = sign < 0 ? rx - RWALL : rx + s + RWALL;
          else c.y = sign < 0 ? ry - RWALL : ry + s + RWALL;
        } else {
          if (dx * dx + dy * dy >= RWALL * RWALL) return;
          const ax = Math.abs(dx), ay = Math.abs(dy);
          axis = ax >= ay ? 0 : 1;
          if (axis === 0 && ay > 0 && wallAt(cx + (dx > 0 ? 1 : -1), cy)) axis = 1;
          else if (axis === 1 && ax > 0 && wallAt(cx, cy + (dy > 0 ? 1 : -1))) axis = 0;
          if (axis === 0) { sign = dx > 0 ? 1 : -1; c.x += sign * (RWALL - ax); }
          else { sign = dy > 0 ? 1 : -1; c.y += sign * (RWALL - ay); }
        }
        const vn = axis === 0 ? sign * c.vx : sign * c.vy;
        if (vn >= 0) return;
        const back = (-vn * E_WALL / 256) | 0;
        const fr = ((back - vn) * WALL_FRICTION / 256) | 0;
        if (axis === 0) {
          c.vx = sign * back;
          c.vy = c.vy > 0 ? Math.max(0, c.vy - fr) : Math.min(0, c.vy + fr);
        } else {
          c.vy = sign * back;
          c.vx = c.vx > 0 ? Math.max(0, c.vx - fr) : Math.min(0, c.vx + fr);
        }
        c.hit = true;
        if (-vn > HIT_EV_MIN) c.ev |= EV.wall;
      }

      function soft(c, hx, hy, r, ev) {
        let dx = c.x - hx;
        const dy = c.y - hy;
        if (dx >= r || dx <= -r || dy >= r || dy <= -r) return;
        const d2 = dx * dx + dy * dy;
        if (d2 >= r * r) return;
        let d = isqrt(d2);
        if (d === 0) { dx = 1; d = 1; }
        const nx = (dx * 16384 / d) | 0, ny = (dy * 16384 / d) | 0;
        const push = r - d;
        c.x += (nx * push) >> 14;
        c.y += (ny * push) >> 14;
        const vn = (c.vx * nx + c.vy * ny) >> 14;
        if (vn >= 0) return;
        const j = (vn * HAY_BACK / 256) | 0;
        c.vx -= (j * nx) >> 14;
        c.vy -= (j * ny) >> 14;
        const damp = 256 - Math.min(256 - HAY_DAMP, (-vn * (256 - HAY_DAMP) / 512) | 0);
        c.vx = (c.vx * damp / 256) | 0;
        c.vy = (c.vy * damp / 256) | 0;
        c.hit = true;
        if (-vn > HAY_EV_MIN) c.ev |= ev;
      }

      function walls(c) {
        const x0 = (c.x - RWALL) >> SHIFT, x1 = (c.x + RWALL) >> SHIFT;
        const y0 = (c.y - RWALL) >> SHIFT, y1 = (c.y + RWALL) >> SHIFT;
        for (let cy = y0; cy <= y1; cy++) {
          for (let cx = x0; cx <= x1; cx++) {
            const code = track.codeAt(cx, cy);
            if (isWall(code)) wall(c, cx, cy);
            else if (code === HAY) soft(c, (cx << SHIFT) + CELL / 2, (cy << SHIFT) + CELL / 2, RWALL + RHAY, EV.hay);
          }
        }
        if (sim.live !== 0 && c.air === 0) for (let k = 0; k < crN; k++) soft(c, ox[k], oy[k], RWALL + CRITTER_R[track.critters[k][0]], EV.critter);
      }

      function bump(a, b) {
        if (!a.present || !b.present || a.ghost || b.ghost || (a.air > 0) !== (b.air > 0)) return;
        const r = RCAR * 2;
        let dx = b.x - a.x;
        const dy = b.y - a.y;
        if (dx >= r || dx <= -r || dy >= r || dy <= -r) return;
        const d2 = dx * dx + dy * dy;
        if (d2 >= r * r) return;
        let d = isqrt(d2);
        if (d === 0) { dx = 1; d = 1; }
        const nx = (dx * 16384 / d) | 0, ny = (dy * 16384 / d) | 0;
        const push = ((r - d) / 2) | 0;
        const px = (nx * push) >> 14, py = (ny * push) >> 14;
        a.x -= px; a.y -= py; b.x += px; b.y += py;
        clamp(a);
        clamp(b);
        const vrel = ((b.vx - a.vx) * nx + (b.vy - a.vy) * ny) >> 14;
        if (vrel >= 0) return;
        const j = (((-vrel * E_CAR / 2) | 0) / 256) | 0;
        const jx = (j * nx) >> 14, jy = (j * ny) >> 14;
        a.vx -= jx; a.vy -= jy; b.vx += jx; b.vy += jy;
        a.hit = b.hit = true;
        if (-vrel > HIT_EV_MIN) { a.ev |= EV.car; b.ev |= EV.car; }
      }

      function cells(c) {
        const cell = cellOf(c.x, c.y);
        if (cell === c.cell) return;
        c.cell = cell;
        if (c.air === 0) {
          switch (track.tile[cell]) {
            case PUDDLE: c.ev |= EV.puddle; break;
            case OIL: c.oil = OIL_TICKS; c.ev |= EV.oil; break;
            case BOOST:
              if (c.boostCd === 0) {
                c.vf = Math.min(BOOST_CAP, Math.max(c.vf, 0) + BOOST_ADD);
                c.boostT = BOOST_TICKS;
                c.boostCd = BOOST_CD;
                c.ev |= EV.boost;
              }
              break;
            case RAMP:
              if (c.vf >= JUMP_MIN) { c.air = AIR_TICKS; c.ev |= EV.jump; }
              break;
            default:
          }
        }
        const g = track.gateAt[cell];
        if (!c.ghost && g === c.next) passGate(c);
      }

      function passGate(c) {
        if (c.next !== 0) { c.next = (c.next + 1) % track.K; return; }
        c.next = 1;
        c.lap++;
        c.ev |= EV.lap;
        sim.anyLap = true;
        const ms = lapCrossMs(c);
        c.lastMs = ms - c.lapStartMs;
        c.lapStartMs = ms;
        if (c.bestMs === 0 || c.lastMs < c.bestMs) c.bestMs = c.lastMs;
        if (c.lap < sim.laps) return;
        c.fin = ++sim.finished;
        c.finishMs = ms;
        c.ghost = true;
      }

      function lapCrossMs(c) {
        let num, den;
        if (track.lineAxis === 0) { num = (track.lineEdge - c.px) * track.lineDir; den = (c.x - c.px) * track.lineDir; }
        else { num = (track.lineEdge - c.py) * track.lineDir; den = (c.y - c.py) * track.lineDir; }
        const part = den <= 0 || num < 0 ? 0 : num > den ? TICK_MS : (TICK_MS * num / den) | 0;
        return (sim.T - COUNT - 1) * TICK_MS + part;
      }

      function timers(c) {
        if (c.air > 0 && --c.air === 0) c.ev |= EV.land;
        if (c.oil > 0) c.oil--;
        if (c.stall > 0) c.stall--;
        if (c.boostT > 0) c.boostT--;
        if (c.boostCd > 0) c.boostCd--;
        if (c.resetCd > 0) c.resetCd--;
        if (c.hornCd > 0) c.hornCd--;
      }

      sim.tick = () => {
        sim.T++;
        const slot = sim.T & 15;
        sim.anyLap = false;
        for (let i = 0; i < SEATS; i++) {
          const c = cars[i];
          const s = c.sched[slot];
          if (s >= 0) { c.mask = s; c.sched[slot] = -1; }
          c.ev = c.pendEv;
          c.pendEv = 0;
          c.px = c.x;
          c.py = c.y;
          c.hit = false;
        }
        if (sim.T <= COUNT) {
          for (let i = 0; i < SEATS; i++) if (cars[i].hornCd > 0) cars[i].hornCd--;
          return;
        }
        for (let i = 0; i < SEATS; i++) if (cars[i].present) controls(cars[i]);
        if (sim.live !== 0) for (let k = 0; k < crN; k++) { critterAt(track.critters[k], sim.live, sim.T, cp); ox[k] = cp[0]; oy[k] = cp[1]; }
        for (let h = 0; h < 2; h++) {
          for (let i = 0; i < SEATS; i++) {
            const c = cars[i];
            if (!c.present) continue;
            c.x += h === 0 ? (c.vx / 2) | 0 : c.vx - ((c.vx / 2) | 0);
            c.y += h === 0 ? (c.vy / 2) | 0 : c.vy - ((c.vy / 2) | 0);
            clamp(c);
            walls(c);
            clamp(c);
          }
          for (let a = 0; a < SEATS; a++) for (let b = a + 1; b < SEATS; b++) bump(cars[a], cars[b]);
        }
        for (let i = 0; i < SEATS; i++) {
          const c = cars[i];
          if (!c.present) continue;
          if (c.hit) {
            c.vf = (c.vx * COS[c.a] + c.vy * SIN[c.a]) >> 14;
            c.vl = (-c.vx * SIN[c.a] + c.vy * COS[c.a]) >> 14;
          }
          cells(c);
          timers(c);
        }
      };

      sim.hash = (h) => {
        for (let i = 0; i < SEATS; i++) {
          const c = cars[i];
          if (!c.present) continue;
          h = mix(h, c.x); h = mix(h, c.y); h = mix(h, c.a); h = mix(h, c.vf); h = mix(h, c.vl);
        }
        return h;
      };

      /// Стан машини з кадру сервера: усе, що впливає на фізику однієї машини.
      sim.load = (seat, c15, o) => {
        const c = cars[seat];
        c.present = c15[o + 10] >= 0;
        c.x = c15[o]; c.y = c15[o + 1]; c.a = c15[o + 2]; c.vf = c15[o + 3]; c.vl = c15[o + 4]; c.mask = c15[o + 5];
        c.lap = c15[o + 6]; c.next = c15[o + 7];
        const tm = c15[o + 9];
        c.air = tm & 15; c.oil = (tm >> 4) & 31; c.stall = (tm >> 9) & 31; c.boostT = (tm >> 14) & 31; c.boostCd = (tm >> 19) & 63;
        c.fin = Math.max(0, c15[o + 10]);
        c.ghost = c.fin > 0;
        c.cell = cellOf(c.x, c.y);
        c.sched.fill(-1);
        c.ev = c.pendEv = 0;
        return c;
      };
      return sim;
    }

    function isqrt(n) {
      if (n <= 0) return 0;
      let x = Math.floor(Math.sqrt(n));
      while (x * x > n) x--;
      while ((x + 1) * (x + 1) <= n) x++;
      return x;
    }

    /// Журнал паритету: те саме, що RallyReplays.Play у C#. Повертає hex-хеш стану після всіх тиків.
    function replay(j) {
      const sim = create(buildTrack({ id: j.track, map: j.map, gates: j.gates, slots: j.slots, heading: j.heading, critters: j.critters }), j.laps);
      sim.live = j.live | 0;
      j.cars.forEach((car, s) => { if (car) sim.grid(s, car); });
      let h = FNV_START, next = 0;
      for (let i = 0; i < j.ticks; i++) {
        while (next < j.inputs.length && j.inputs[next][0] <= sim.T + 1) {
          const inp = j.inputs[next++];
          sim.schedule(inp[1], inp[0], inp[2]);
        }
        sim.tick();
        h = sim.hash(h);
      }
      return hex(h);
    }

    function tableHash(t) {
      let h = FNV_START;
      for (let i = 0; i < t.length; i++) h = mix(h, t[i]);
      return hex(h);
    }

    return {
      SIN, COS, EV, SURF, DRAG, GRIP, ACC, COLS, ROWS, CELL, SHIFT, SEATS, TICK_MS, COUNT, RWALL, BOOST_CAP,
      ROAD, PUDDLE, ICE, GRASS, CORN, MUD, OIL, BOOST, RAMP, HAY, FENCE, TREE, HOUSE, WATER,
      isWall, cellOf, buildTrack, create, replay, isqrt, mix, hex, FNV_START, critterAt, CRITTER_R,
      tables: () => ({ sin: tableHash(SIN), cos: tableHash(COS) }),
    };
  })();
  window.RallySim = RallySim;

  // ===============================================================================================
  // Константи клієнта
  // ===============================================================================================
  const S = RallySim;
  const SUB = 64, WU = 1536, HU = 864;          // світ у u: 48×27 клітинок по 32
  const STRIDE = 15, SEATS = 6, TICK = 40;
  const EXTRAPOLATE = true;                      // чужі — екстраполяція (spec §5.6); false — інтерполяція назад
  const LEAD0 = 2, LEAD_MIN = 1, LEAD_MAX = 8;
  const AWAY_MS = 200, QUIET_MS = 250;           // цикл малювання: схована картка / тихе лобі й підсумок (loop)
  const GMAX = 1500;                             // привид: коло до 60 с (довше — не пишемо)
  // іконка: «запорожець» збоку, що курить пилом
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="1.9" cy="11.4" r="1.4" fill="var(--clay)" opacity=".5"/>'
    + '<path d="M3 12.2V9.7c0-.7.4-1.2 1.1-1.4l1.7-.5 1.9-2.3c.3-.3.7-.5 1.1-.5h2.4c.4 0 .8.2 1.1.5l1.5 2c.8.1 1.3.6 1.3 1.3v3.4z" fill="var(--accent)"/>'
    + '<path d="M7.4 7.6 8.7 6h1.4v1.6zM11 6h.6l1.2 1.6H11z" fill="var(--bg2)"/>'
    + '<circle cx="5.9" cy="12.3" r="1.9" fill="var(--clay)"/><circle cx="12" cy="12.3" r="1.9" fill="var(--clay)"/></svg>';

  const CARS = [
    { id: 'traktor', title: 'Трактор', emoji: '🚜' },
    { id: 'zapor', title: '«Запорожець»', emoji: '🚗' },
    { id: 'moped', title: 'Мопед', emoji: '🛵' },
    { id: 'viz', title: 'Віз із конем', emoji: '🐴' },
    { id: 'motoblok', title: 'Мотоблок', emoji: '🛻' },
    { id: 'kopiyka', title: '«Копійка»', emoji: '🚙' },
  ];
  const CAR = Object.fromEntries(CARS.map((c) => [c.id, c]));
  /// Гараж (прохід №3): фарби за номером, як їх перевіряє сервер (Rally.Paints); −1 — колір місця.
  const PAINTS = ['#d9412b', '#f08a24', '#f4d03f', '#9bd14b', '#2f9e4f', '#2fb3a8', '#3f78d8', '#8a5ad6', '#e88ac0', '#f2f2ee', '#34363a', '#a9b0b6'];
  const PAINT_NAMES = ['червона', 'помаранчева', 'жовта', 'салатова', 'зелена', 'бірюзова', 'синя', 'фіолетова', 'рожева', 'біла', 'чорна', 'срібляста'];
  /// Траси для вибору між гонками (№85): id — як у RallyTracks, емодзі — мініатюра на кнопці.
  const TRACKS = [
    { id: 'selo', emoji: '🏡', title: 'Село' }, { id: 'ozero', emoji: '🧊', title: 'Крижане озеро' }, { id: 'nich', emoji: '🌙', title: 'Нічна' },
    { id: 'kukurudza', emoji: '🌽', title: 'Кукурудзяне поле' }, { id: 'yarmarok', emoji: '🎡', title: 'Ярмарок' },
    { id: 'vesillia', emoji: '💒', title: 'Весілля' }, { id: 'hora', emoji: '🏔', title: 'Гора' },
    { id: 'random', emoji: '🎲', title: 'Яка випаде' },
  ];
  function myGarage() {
    try { const g = JSON.parse(store.get('rally.garage', '') || 'null'); if (g && typeof g === 'object') return { paint: g.paint | 0, plate: String(g.plate || '').slice(0, 6) }; } catch { /* зіпсоване */ }
    return null;
  }
  /// Хто за кермом: нік людини, «🤖 Дід Панас» бота (№87) чи колір місця.
  function whoOf(st, i) {
    return st.ctx.nickOf(i) || (st.view && st.view.bots && st.view.bots[i]) || SEAT_NAMES[i];
  }
  function carColor(st, seat) {
    const p = st.view && st.view.paint ? st.view.paint[seat] : -1;
    return p >= 0 && p < PAINTS.length ? PAINTS[p] : st.pal.seats[seat];
  }
  const SEAT_VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--text', '#ecf1ea'],
    ['--rl-blue', '#6fb3e8'], ['--rl-pink', '#e88ac0']];
  const SEAT_NAMES = ['жовтий', 'зелений', 'рудий', 'білий', 'синій', 'рожевий'];
  const SEAT_NUM = ['1', '2', '3', '4', '5', '6'];

  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v === null ? d : v; } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* приватне вікно */ } },
  };
  /// м:сс,д — час гонки; м:сс,сс — коло. Частки відкидаємо, як сервер.
  function clock(ms, digits) {
    ms = Math.max(0, ms | 0);
    const m = Math.floor(ms / 60000), s = Math.floor(ms / 1000) % 60;
    const frac = digits === 1 ? String(Math.floor((ms % 1000) / 100)) : String(Math.floor((ms % 1000) / 10)).padStart(2, '0');
    return m + ':' + String(s).padStart(2, '0') + ',' + frac;
  }
  const lapsWord = (n) => (n === 1 ? '1 коло' : n >= 2 && n <= 4 ? n + ' кола' : n + ' кіл');
  const place = (n) => n + '-й';
  /// Відставання «+0,4 с» / «+1:02,3»: десяті частки відкидаємо, як сервер.
  const gap = (ms) => (ms >= 60000 ? '+' + clock(ms, 1) : '+' + Math.floor(ms / 1000) + ',' + Math.floor((ms % 1000) / 100) + ' с');
  /// Скільки машин у кадрі й чи хтось уже взяв перші ворота: доти місця — лише порядок на решітці, і «1-й» у
  /// того, хто стоїть ближче до воріт, а не на пол-позиції, тільки плутав би. Соло місць не показує взагалі.
  function field(f) {
    let cars = 0, moved = false;
    if (f) {
      for (let i = 0; i < SEATS; i++) {
        const o = i * STRIDE;
        if (f.c[o + 10] < 0) continue;
        cars++;
        if (f.c[o + 6] > 0 || f.c[o + 7] !== 1 || f.c[o + 10] > 0) moved = true;
      }
    }
    return cars > 1 && moved ? 2 : cars > 1 ? 1 : 0;   // 2 — місця показуємо, 1 — ще ні, 0 — соло
  }
  const wrapA = (d) => ((d + 512) & 1023) - 512;
  const blank = () => ({ x: 0, y: 0, a: 0, vf: 0, vl: 0, air: 0, oil: 0, stall: 0, boostT: 0, boostCd: 0, cell: 0, mask: 0, t: -1 });

  // ===============================================================================================
  // Траса: фон один раз в offscreen, поле напрямків (для шевронів і «не туди»)
  // ===============================================================================================

  /// Відстань (у кроках клітинок) до кожних воріт по проїзних клітинках — для стрілок турбо й «не туди».
  function distFields(tr) {
    const out = [];
    for (let g = 0; g < tr.K; g++) {
      const d = new Int16Array(S.COLS * S.ROWS).fill(-1);
      const q = [];
      for (let c = 0; c < d.length; c++) if (tr.gateAt[c] === g && !S.isWall(tr.tile[c])) { d[c] = 0; q.push(c); }
      for (let qi = 0; qi < q.length; qi++) {
        const c = q[qi], x = c % S.COLS, y = (c / S.COLS) | 0;
        const nb = [x > 0 ? c - 1 : -1, x < S.COLS - 1 ? c + 1 : -1, y > 0 ? c - S.COLS : -1, y < S.ROWS - 1 ? c + S.COLS : -1];
        for (const n of nb) {
          if (n < 0 || d[n] >= 0 || S.isWall(tr.tile[n])) continue;
          // ворота g−1 — межа сектора: далі назад поле не тече
          if (tr.gateAt[n] === (g + tr.K - 1) % tr.K) { d[n] = d[c] + 1; continue; }
          d[n] = d[c] + 1;
          q.push(n);
        }
      }
      out.push(d);
    }
    return out;
  }

  /// Сектор кожної клітинки — номер воріт, до яких вона веде (ділянка між воротами g−1 і g). R_g — куди
  /// дістанешся від воріт g, не перетнувши ні g−1, ні g+1 (тобто сектори обабіч воріт g); сектор g — спільне
  /// R_g і R_{g−1}. Клітинки самих воріт g ведуть до g+1. Без цього ближчими «здавались» попередні ворота, і
  /// шеврони турбо на Ярмарку дивились убік.
  function sectors(tr) {
    const N = S.COLS * S.ROWS, K = tr.K, reach = [];
    for (let g = 0; g < K; g++) {
      const a = (g + K - 1) % K, b = (g + 1) % K;
      const seen = new Uint8Array(N), q = [];
      for (let c = 0; c < N; c++) if (tr.gateAt[c] === g && !S.isWall(tr.tile[c])) { seen[c] = 1; q.push(c); }
      for (let qi = 0; qi < q.length; qi++) {
        const c = q[qi], x = c % S.COLS, y = (c / S.COLS) | 0;
        const nb = [x > 0 ? c - 1 : -1, x < S.COLS - 1 ? c + 1 : -1, y > 0 ? c - S.COLS : -1, y < S.ROWS - 1 ? c + S.COLS : -1];
        for (const n of nb) {
          if (n < 0 || seen[n] || S.isWall(tr.tile[n]) || tr.gateAt[n] === a || tr.gateAt[n] === b) continue;
          seen[n] = 1;
          q.push(n);
        }
      }
      reach.push(seen);
    }
    const sec = new Int16Array(N).fill(-1);
    for (let c = 0; c < N; c++) {
      if (tr.gateAt[c] !== 255) { sec[c] = (tr.gateAt[c] + 1) % K; continue; }
      for (let g = 0; g < K; g++) if (reach[g][c] && reach[(g + K - 1) % K][c]) { sec[c] = g; break; }
    }
    return sec;
  }

  /// Напрямок руху в клітинці (0..3: праворуч, вниз, ліворуч, вгору) — куди спадає відстань до воріт її сектора.
  function flowDir(fields, sec, cell) {
    const g = sec[cell];
    if (g < 0) return 0;
    const f = fields[g], x = cell % S.COLS, y = (cell / S.COLS) | 0;
    const best = f[cell];
    const cand = [[1, 0, 0], [0, 1, 1], [-1, 0, 2], [0, -1, 3]];
    let dir = 0, low = best < 0 ? 1e9 : best;
    for (const [dx, dy, d] of cand) {
      const nx = x + dx, ny = y + dy;
      if (nx < 0 || ny < 0 || nx >= S.COLS || ny >= S.ROWS) continue;
      const v = f[ny * S.COLS + nx];
      if (v >= 0 && v < low) { low = v; dir = d; }
    }
    return dir;
  }

  /// Детермінований шум клітинки (LCG): щоб трава й лід мали «свої» плями, однакові в усіх.
  function lcg(seed) {
    let s = (seed * 2654435761) >>> 0;
    return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; };
  }

  const LOOK = {
    selo: { road: '#6b5a46', edge: '#584935', grass: '#4f7d3a', fence: 'wood', yard: '#3f6a2f' },
    ozero: { road: '#8a8f94', edge: '#6f757a', grass: '#e9f1f5', fence: 'snow', yard: '#dfe9ef' },
    nich: { road: '#3a3f45', edge: '#2b2f34', grass: '#2f4a2a', fence: 'dark', yard: '#263d22' },
    kukurudza: { road: '#6b5a46', edge: '#584935', grass: '#5d7a35', fence: 'wood', yard: '#4c6a2b' },
    yarmarok: { road: '#6f5e49', edge: '#5b4c3a', grass: '#4f7d3a', fence: 'flags', yard: '#44703a' },
    vesillia: { road: '#75654f', edge: '#5f513f', grass: '#5a8a3e', fence: 'flags', yard: '#4a7a35' },
    hora: { road: '#6e6a62', edge: '#57534c', grass: '#6f8a4a', fence: 'rock', yard: '#8a857b' },
  };
  const lookOf = (id) => LOOK[id] || LOOK.selo;

  function isRoadish(code) {
    return code === S.ROAD || code === S.PUDDLE || code === S.OIL || code === S.BOOST || code === S.RAMP || code === S.HAY || code === S.MUD;
  }

  /// Статичний фон траси у світових одиницях (u): викликається раз на трасу й розмір канваса.
  function paintTrack(g, tr, td, fields, sec) {
    const look = lookOf(td.id);
    const C = 32, tile = tr.tile, code = (x, y) => tr.codeAt(x, y);
    g.fillStyle = look.grass;
    g.fillRect(0, 0, WU, HU);
    // 1) земля під усім: трава з плямами, кукурудзяне тло, лід
    for (let y = 0; y < S.ROWS; y++) {
      for (let x = 0; x < S.COLS; x++) {
        const k = tile[y * S.COLS + x], px = x * C, py = y * C, rnd = lcg(y * 97 + x * 13 + 7);
        if (k === S.GRASS || S.isWall(k)) {
          g.fillStyle = S.isWall(k) ? look.yard : look.grass;
          g.fillRect(px, py, C, C);
          g.fillStyle = td.id === 'ozero' ? 'rgba(160,190,210,.25)' : 'rgba(0,0,0,.10)';
          for (let i = 0; i < 6; i++) g.fillRect(px + rnd() * 30, py + rnd() * 30, 2 + rnd() * 2, 2);
        } else if (k === S.CORN) {
          g.fillStyle = '#7c8f2e';
          g.fillRect(px, py, C, C);
        } else if (k === S.ICE) {
          g.fillStyle = '#cfe6f2';
          g.fillRect(px, py, C, C);
          g.strokeStyle = 'rgba(120,160,190,.45)';
          g.lineWidth = 0.8;
          g.beginPath();
          const sx = px + rnd() * C, sy = py + rnd() * C;
          g.moveTo(sx, sy);
          g.lineTo(sx + (rnd() - 0.5) * 22, sy + (rnd() - 0.5) * 22);
          g.lineTo(sx + (rnd() - 0.5) * 30, sy + (rnd() - 0.5) * 30);
          g.stroke();
          g.fillStyle = 'rgba(255,255,255,.35)';
          g.fillRect(px + rnd() * 26, py + rnd() * 26, 5, 1.5);
        } else {
          g.fillStyle = look.road;
          g.fillRect(px, py, C, C);
        }
      }
    }
    // 2) дорога: темніші краї біля трави й колія
    g.fillStyle = look.edge;
    for (let y = 0; y < S.ROWS; y++) {
      for (let x = 0; x < S.COLS; x++) {
        if (!isRoadish(code(x, y))) continue;
        const px = x * C, py = y * C;
        if (!isRoadish(code(x - 1, y)) && code(x - 1, y) !== S.ICE) g.fillRect(px, py, 3, C);
        if (!isRoadish(code(x + 1, y)) && code(x + 1, y) !== S.ICE) g.fillRect(px + C - 3, py, 3, C);
        if (!isRoadish(code(x, y - 1)) && code(x, y - 1) !== S.ICE) g.fillRect(px, py, C, 3);
        if (!isRoadish(code(x, y + 1)) && code(x, y + 1) !== S.ICE) g.fillRect(px, py + C - 3, C, 3);
      }
    }
    if (td.id !== 'nich') {
      g.fillStyle = 'rgba(0,0,0,.09)';
      for (let y = 0; y < S.ROWS; y++) {
        for (let x = 0; x < S.COLS; x++) {
          const k = code(x, y);
          if (k !== S.ROAD) continue;
          const d = flowDir(fields, sec, y * S.COLS + x);
          if (d === 0 || d === 2) { g.fillRect(x * C, y * C + 9, C, 3); g.fillRect(x * C, y * C + 21, C, 3); }
          else { g.fillRect(x * C + 9, y * C, 3, C); g.fillRect(x * C + 21, y * C, 3, C); }
        }
      }
    } else {
      // асфальт уночі: біла переривчаста посередині прямих
      g.fillStyle = 'rgba(235,235,225,.55)';
      for (let y = 0; y < S.ROWS; y++) {
        for (let x = 0; x < S.COLS; x++) {
          if (code(x, y) !== S.ROAD) continue;
          let y0 = y, y1 = y, x0 = x, x1 = x;
          while (isRoadish(code(x, y0 - 1))) y0--;
          while (isRoadish(code(x, y1 + 1))) y1++;
          while (isRoadish(code(x0 - 1, y))) x0--;
          while (isRoadish(code(x1 + 1, y))) x1++;
          const vRun = y1 - y0 + 1, hRun = x1 - x0 + 1;
          if (vRun <= 6 && hRun > vRun && (x % 2 === 0) && y === ((y0 + y1 + 1) >> 1)) g.fillRect(x * C + 4, y * C - 1, 18, 2);
          else if (hRun <= 6 && vRun > hRun && (y % 2 === 0) && x === ((x0 + x1 + 1) >> 1)) g.fillRect(x * C - 1, y * C + 4, 2, 18);
        }
      }
    }
    // 3) особливі клітинки
    for (let y = 0; y < S.ROWS; y++) {
      for (let x = 0; x < S.COLS; x++) {
        const k = code(x, y), px = x * C, py = y * C, cx = px + C / 2, cy = py + C / 2, rnd = lcg(y * 131 + x * 17 + 3);
        if (k === S.PUDDLE) {
          g.fillStyle = '#4f7fa8';
          g.beginPath(); g.ellipse(cx, cy, 15, 12, rnd() * 0.6, 0, Math.PI * 2); g.fill();
          g.fillStyle = 'rgba(210,235,255,.45)';
          g.beginPath(); g.ellipse(cx - 4, cy - 4, 5, 2.4, -0.4, 0, Math.PI * 2); g.fill();
        } else if (k === S.MUD) {
          g.fillStyle = '#4a3220';
          g.fillRect(px, py, C, C);
          g.fillStyle = '#3a2717';
          for (let i = 0; i < 5; i++) { g.beginPath(); g.arc(px + 4 + rnd() * 24, py + 4 + rnd() * 24, 3 + rnd() * 4, 0, Math.PI * 2); g.fill(); }
          g.fillStyle = 'rgba(120,90,60,.5)';
          for (let i = 0; i < 3; i++) g.fillRect(px + rnd() * 28, py + rnd() * 28, 4, 1.5);
        } else if (k === S.OIL) {
          const gr = g.createRadialGradient(cx, cy, 2, cx, cy, 15);
          gr.addColorStop(0, '#15151c'); gr.addColorStop(0.6, '#2a2440'); gr.addColorStop(0.85, 'rgba(80,130,120,.55)'); gr.addColorStop(1, 'rgba(40,40,50,0)');
          g.fillStyle = gr;
          g.beginPath(); g.ellipse(cx, cy, 16, 13, 0.5, 0, Math.PI * 2); g.fill();
        } else if (k === S.BOOST || k === S.RAMP) {
          const d = flowDir(fields, sec, y * S.COLS + x);
          g.save();
          g.translate(cx, cy);
          g.rotate(d * Math.PI / 2);
          if (k === S.BOOST) {
            g.fillStyle = '#f4c542';
            for (let i = -1; i <= 1; i++) {
              g.beginPath(); g.moveTo(-6 + i * 9, -9); g.lineTo(2 + i * 9, 0); g.lineTo(-6 + i * 9, 9); g.lineTo(-10 + i * 9, 9); g.lineTo(-2 + i * 9, 0); g.lineTo(-10 + i * 9, -9);
              g.closePath(); g.fill();
            }
          } else {
            g.fillStyle = '#9a6b3c';
            g.fillRect(-15, -16, 30, 32);
            g.strokeStyle = '#6b4424';
            g.lineWidth = 1.2;
            for (let i = -12; i <= 12; i += 6) { g.beginPath(); g.moveTo(i, -16); g.lineTo(i, 16); g.stroke(); }
            g.fillStyle = 'rgba(255,240,200,.85)';
            g.beginPath(); g.moveTo(-6, -7); g.lineTo(7, 0); g.lineTo(-6, 7); g.closePath(); g.fill();
          }
          g.restore();
        } else if (k === S.CORN) {
          paintCorn(g, px, py, rnd, 1);
        }
      }
    }
    // 4) лінія старту — шахівниця на воротах 0; слоти решітки
    const line = tr.gates[0][0];
    for (let y = line[1]; y < line[1] + line[3]; y++) {
      for (let x = line[0]; x < line[0] + line[2]; x++) {
        if (S.isWall(code(x, y)) || code(x, y) === S.GRASS) continue;
        for (let i = 0; i < 4; i++) for (let j = 0; j < 4; j++) {
          g.fillStyle = (i + j) % 2 ? '#1b1b1b' : '#f2f2ec';
          g.fillRect(x * C + i * 8, y * C + j * 8, 8, 8);
        }
      }
    }
    g.strokeStyle = 'rgba(255,255,255,.55)';
    g.lineWidth = 1.2;
    for (const [sx, sy] of td.slots || []) g.strokeRect(sx * C + 4, sy * C + 7, 24, 18);
    // 5) стіни: тин по краю, дерева, хати, вода
    paintWalls(g, tr, td, look);
    // 6) копиці сіна
    for (let y = 0; y < S.ROWS; y++) {
      for (let x = 0; x < S.COLS; x++) {
        if (code(x, y) !== S.HAY) continue;
        const cx = x * C + C / 2, cy = y * C + C / 2, rnd = lcg(y * 7 + x * 3);
        g.fillStyle = 'rgba(0,0,0,.28)';
        g.beginPath(); g.arc(cx + 3, cy + 4, 14, 0, Math.PI * 2); g.fill();
        g.fillStyle = '#d9b44a';
        g.beginPath(); g.arc(cx, cy, 14, 0, Math.PI * 2); g.fill();
        g.strokeStyle = '#b08a2e';
        g.lineWidth = 1;
        for (let i = 0; i < 9; i++) {
          const a = rnd() * Math.PI * 2, r = 3 + rnd() * 9;
          g.beginPath(); g.moveTo(cx + Math.cos(a) * r, cy + Math.sin(a) * r); g.lineTo(cx + Math.cos(a + 0.5) * (r + 3), cy + Math.sin(a + 0.5) * (r + 3)); g.stroke();
        }
        g.fillStyle = 'rgba(255,240,180,.35)';
        g.beginPath(); g.arc(cx - 4, cy - 5, 5, 0, Math.PI * 2); g.fill();
      }
    }
  }

  function paintCorn(g, px, py, rnd, alpha) {
    g.globalAlpha = alpha;
    for (let r = 0; r < 4; r++) {
      const ry = py + 4 + r * 8;
      for (let i = 0; i < 4; i++) {
        const sx = px + 3 + i * 8 + rnd() * 2, sy = ry + rnd() * 2;
        g.strokeStyle = '#b9a33c';
        g.lineWidth = 1.4;
        g.beginPath(); g.moveTo(sx, sy + 3); g.lineTo(sx + 2, sy - 3); g.stroke();
        g.strokeStyle = '#8fa23a';
        g.beginPath(); g.moveTo(sx + 1, sy); g.lineTo(sx + 5, sy - 2); g.moveTo(sx + 1, sy + 1); g.lineTo(sx - 3, sy - 1); g.stroke();
      }
    }
    g.globalAlpha = 1;
  }

  function paintWalls(g, tr, td, look) {
    const C = 32, code = (x, y) => tr.codeAt(x, y);
    const open = (x, y) => !S.isWall(code(x, y));
    const houses = new Set();
    const flags = ['#e05a4f', '#f4c542', '#5aa0e0', '#7bd389', '#e88ac0'];
    let post = 0;
    for (let y = 0; y < S.ROWS; y++) {
      for (let x = 0; x < S.COLS; x++) {
        const k = code(x, y), px = x * C, py = y * C, cx = px + C / 2, cy = py + C / 2, rnd = lcg(y * 53 + x * 29 + 1);
        if (k === S.WATER) {
          g.fillStyle = td.id === 'ozero' ? '#2d5f86' : '#3f6f9e';
          g.fillRect(px, py, C, C);
          g.strokeStyle = 'rgba(200,230,255,.28)';
          g.lineWidth = 1;
          g.beginPath(); const wy = py + 8 + rnd() * 16; g.moveTo(px + 4, wy); g.quadraticCurveTo(px + 12, wy - 3, px + 20, wy); g.stroke();
        } else if (k === S.TREE) {
          g.fillStyle = 'rgba(0,0,0,.25)';
          g.beginPath(); g.arc(cx + 4, cy + 5, 15, 0, Math.PI * 2); g.fill();
          g.fillStyle = td.id === 'ozero' ? '#3d6b4f' : td.id === 'nich' ? '#1f3a22' : '#2f6b2e';
          g.beginPath(); g.arc(cx, cy, 15, 0, Math.PI * 2); g.fill();
          g.fillStyle = td.id === 'ozero' ? 'rgba(240,248,255,.7)' : 'rgba(120,180,90,.55)';
          g.beginPath(); g.arc(cx - 4, cy - 4, 7, 0, Math.PI * 2); g.fill();
          g.beginPath(); g.arc(cx + 5, cy + 2, 5, 0, Math.PI * 2); g.fill();
        } else if (k === S.HOUSE && !houses.has(y * S.COLS + x)) {
          // хата — увесь зв'язний блок D одним дахом
          let x1 = x, y1 = y;
          while (code(x1 + 1, y) === S.HOUSE) x1++;
          while (code(x, y1 + 1) === S.HOUSE) y1++;
          for (let yy = y; yy <= y1; yy++) for (let xx = x; xx <= x1; xx++) houses.add(yy * S.COLS + xx);
          const w = (x1 - x + 1) * C, h = (y1 - y + 1) * C;
          paintHouse(g, px, py, w, h, td.id, rnd);
        } else if (k === S.FENCE && look.fence === 'rock') {
          // Гора: скеля замість тину — сірий камінь із брилами, темний уступ до дороги
          g.fillStyle = rnd() < 0.5 ? '#7d786f' : '#868177';
          g.fillRect(px, py, C, C);
          g.fillStyle = 'rgba(255,255,255,.12)';
          g.beginPath(); g.arc(px + 6 + rnd() * 20, py + 6 + rnd() * 20, 5 + rnd() * 6, 0, Math.PI * 2); g.fill();
          g.fillStyle = 'rgba(0,0,0,.16)';
          g.beginPath(); g.arc(px + 6 + rnd() * 20, py + 6 + rnd() * 20, 3 + rnd() * 5, 0, Math.PI * 2); g.fill();
          g.fillStyle = 'rgba(40,36,32,.55)';
          if (open(x, y - 1)) g.fillRect(px, py, C, 4);
          if (open(x, y + 1)) g.fillRect(px, py + C - 4, C, 4);
          if (open(x - 1, y)) g.fillRect(px, py, 4, C);
          if (open(x + 1, y)) g.fillRect(px + C - 4, py, 4, C);
        } else if (k === S.FENCE) {
          if (look.fence === 'snow') {
            g.fillStyle = '#f4f8fb';
            g.fillRect(px, py, C, C);
            g.fillStyle = 'rgba(170,200,220,.35)';
            g.beginPath(); g.arc(px + rnd() * C, py + rnd() * C, 6 + rnd() * 5, 0, Math.PI * 2); g.fill();
          }
          // тин лише там, де поруч дорога чи трава: по краю клітинки, звернутому до проїзного
          const edges = [[open(x, y - 1), px, py + 2, px + C, py + 2], [open(x, y + 1), px, py + C - 2, px + C, py + C - 2],
            [open(x - 1, y), px + 2, py, px + 2, py + C], [open(x + 1, y), px + C - 2, py, px + C - 2, py + C]];
          for (const [on, ax, ay, bx, by] of edges) {
            if (!on) continue;
            post++;
            if (look.fence === 'snow') {
              g.strokeStyle = 'rgba(150,185,210,.8)';
              g.lineWidth = 3;
              g.beginPath(); g.moveTo(ax, ay); g.lineTo(bx, by); g.stroke();
              continue;
            }
            const dark = look.fence === 'dark';
            g.strokeStyle = dark ? '#1c1a18' : '#7a5230';
            g.lineWidth = 2.2;
            g.beginPath(); g.moveTo(ax, ay); g.lineTo(bx, by); g.stroke();
            g.strokeStyle = dark ? '#2c2824' : '#9b6c40';
            g.lineWidth = 1;
            g.beginPath(); g.moveTo(ax + (ay === by ? 0 : 1.5), ay + (ay === by ? 1.5 : 0)); g.lineTo(bx + (ay === by ? 0 : 1.5), by + (ay === by ? 1.5 : 0)); g.stroke();
            g.fillStyle = dark ? '#111' : '#5b3a1f';
            g.fillRect(ax - 1.5, ay - 1.5, 3, 3);
            if (look.fence === 'flags' && post % 2 === 0) {
              g.fillStyle = flags[post % flags.length];
              const mx = (ax + bx) / 2, my = (ay + by) / 2;
              g.beginPath(); g.moveTo(mx - 3, my - 3); g.lineTo(mx + 3, my - 3); g.lineTo(mx, my + 3); g.closePath(); g.fill();
            }
            if (dark && post % 6 === 0) {
              g.fillStyle = '#555';
              g.beginPath(); g.arc(ax, ay, 2.5, 0, Math.PI * 2); g.fill();
              g.fillStyle = '#ffe9a8';
              g.beginPath(); g.arc(ax, ay, 1.4, 0, Math.PI * 2); g.fill();
            }
          }
        }
      }
    }
  }

  function paintHouse(g, px, py, w, h, id, rnd) {
    g.fillStyle = 'rgba(0,0,0,.3)';
    g.fillRect(px + 5, py + 6, w - 4, h - 4);
    if (id === 'vesillia' && w >= 256) {
      // весільний намет: біле полотно, рожеві фестони по краю, гребінь і прапорці
      g.fillStyle = '#f7f3ee';
      g.fillRect(px + 3, py + 3, w - 6, h - 6);
      g.fillStyle = 'rgba(0,0,0,.07)';
      g.fillRect(px + 3, py + h / 2, w - 6, h / 2 - 3);
      g.fillStyle = '#e88ac0';
      for (let sx = px + 3; sx < px + w - 6; sx += 16) {
        g.beginPath(); g.arc(sx + 8, py + 3, 8, 0, Math.PI); g.fill();
        g.beginPath(); g.arc(sx + 8, py + h - 3, 8, Math.PI, Math.PI * 2); g.fill();
      }
      g.strokeStyle = 'rgba(160,90,120,.55)';
      g.lineWidth = 2;
      g.beginPath(); g.moveTo(px + 3, py + h / 2); g.lineTo(px + w - 3, py + h / 2); g.stroke();
      const fl = ['#e05a4f', '#f4c542', '#5aa0e0', '#7bd389', '#e88ac0'];
      for (let i = 0, fx = px + 12; fx < px + w - 8; fx += 20, i++) {
        g.fillStyle = fl[i % fl.length];
        g.beginPath(); g.moveTo(fx - 4, py + h / 2); g.lineTo(fx + 4, py + h / 2); g.lineTo(fx, py + h / 2 + 7); g.closePath(); g.fill();
      }
      g.font = '28px serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText('💒', px + w / 2, py + h / 2 - 22);
      return;
    }
    if (id === 'yarmarok') {
      // смугаста ятка
      const cols = ['#d9534f', '#f2efe6'];
      const n = Math.max(4, Math.round(w / 12));
      for (let i = 0; i < n; i++) {
        g.fillStyle = cols[i % 2];
        g.fillRect(px + 3 + (i * (w - 6)) / n, py + 3, (w - 6) / n + 0.5, h - 6);
      }
      g.strokeStyle = 'rgba(0,0,0,.25)';
      g.lineWidth = 1.2;
      g.strokeRect(px + 3, py + 3, w - 6, h - 6);
      return;
    }
    const roof = id === 'ozero' ? '#8a5a3c' : id === 'nich' ? '#3b2f2f' : rnd() < 0.5 ? '#a4523a' : '#8b4a33';
    g.fillStyle = roof;
    g.fillRect(px + 3, py + 3, w - 6, h - 6);
    // скати даху й гребінь
    const horiz = w >= h;
    g.fillStyle = 'rgba(255,255,255,.10)';
    if (horiz) g.fillRect(px + 3, py + 3, w - 6, (h - 6) / 2); else g.fillRect(px + 3, py + 3, (w - 6) / 2, h - 6);
    g.strokeStyle = 'rgba(0,0,0,.35)';
    g.lineWidth = 1.5;
    g.beginPath();
    if (horiz) { g.moveTo(px + 3, py + h / 2); g.lineTo(px + w - 3, py + h / 2); } else { g.moveTo(px + w / 2, py + 3); g.lineTo(px + w / 2, py + h - 3); }
    g.stroke();
    if (id === 'ozero') {
      g.fillStyle = 'rgba(255,255,255,.75)';
      g.fillRect(px + 3, py + 3, w - 6, 5);
    }
    // димар
    g.fillStyle = '#5b5550';
    g.fillRect(px + w * 0.7, py + h * 0.25, 5, 5);
  }

  // ===============================================================================================
  // Машини: спрайти (6 машин × 6 кольорів, лише потрібні) — ніс праворуч, центр у (0, 0), розміри в u
  // ===============================================================================================

  const SIZE = { traktor: [36, 22], zapor: [32, 18], moped: [30, 12], viz: [44, 18], motoblok: [30, 16], kopiyka: [36, 18] };
  /// Спрайти малюємо в 1,2 раза більшими за їхні розміри в u: на 1280 і Деці машина була 12–16 px і губилась у купі.
  /// Фізика (коло 24 u) від цього не міняється — лише картинка.
  const SPR = 1.2;
  const DARK = '#1d1b19', GLASS = 'rgba(30,45,60,.85)';

  function rr(g, x, y, w, h, r) { g.beginPath(); g.roundRect(x, y, w, h, r); g.fill(); }

  function paintCar(g, id, color, phase) {
    switch (id) {
      case 'traktor':
        g.fillStyle = DARK;
        rr(g, -17, -12, 11, 6, 1.5); rr(g, -17, 6, 11, 6, 1.5);          // великі задні колеса
        rr(g, 8, -10, 7, 4, 1); rr(g, 8, 6, 7, 4, 1);                   // малі передні
        g.fillStyle = color;
        rr(g, -4, -5, 21, 10, 2);                                        // капот
        rr(g, -15, -8, 13, 16, 2);                                       // кабіна
        g.fillStyle = GLASS;
        rr(g, -13, -6, 9, 12, 1.5);
        g.fillStyle = 'rgba(255,255,255,.35)';
        rr(g, -12, -5, 3, 10, 1);
        g.fillStyle = DARK;
        g.beginPath(); g.arc(8, -2, 1.6, 0, Math.PI * 2); g.fill();      // вихлопна труба
        g.fillStyle = 'rgba(255,255,200,.9)';
        rr(g, 15, -4, 2, 2.5, .5); rr(g, 15, 1.5, 2, 2.5, .5);
        break;
      case 'zapor':
        g.fillStyle = DARK;
        rr(g, -12, -9.5, 7, 3, 1); rr(g, -12, 6.5, 7, 3, 1); rr(g, 6, -9.5, 7, 3, 1); rr(g, 6, 6.5, 7, 3, 1);
        g.fillStyle = color;
        rr(g, -16, -8, 32, 16, 7);
        g.fillStyle = 'rgba(0,0,0,.2)';
        rr(g, -16, -7, 9, 14, 5);                                        // горбатий зад
        g.fillStyle = GLASS;
        rr(g, 1, -6, 6, 12, 2.5);
        rr(g, -9, -6, 4, 12, 2);
        g.fillStyle = 'rgba(255,255,255,.3)';
        rr(g, -4, -6, 5, 12, 2);
        g.fillStyle = '#fff6c9';
        g.beginPath(); g.arc(14, -4.5, 2, 0, Math.PI * 2); g.arc(14, 4.5, 2, 0, Math.PI * 2); g.fill();
        break;
      case 'moped':
        g.fillStyle = DARK;
        rr(g, 8, -1.8, 8, 3.6, 1.5); rr(g, -15, -1.8, 8, 3.6, 1.5);     // колеса в лінію
        g.fillStyle = '#9aa0a6';
        rr(g, -8, -2.5, 17, 5, 2);
        g.strokeStyle = '#444';
        g.lineWidth = 1.4;
        g.beginPath(); g.moveTo(8, -5.5); g.lineTo(8, 5.5); g.stroke();  // кермо
        g.fillStyle = '#5a4a3a';
        rr(g, -7, -4.5, 9, 9, 3);                                        // вершник
        g.fillStyle = color;
        g.beginPath(); g.arc(0, 0, 4.2, 0, Math.PI * 2); g.fill();       // шолом кольору місця
        g.fillStyle = 'rgba(255,255,255,.4)';
        g.beginPath(); g.arc(1, -1.3, 1.5, 0, Math.PI * 2); g.fill();
        break;
      case 'viz': {
        // кінь спереду: дві фази ніг
        g.fillStyle = '#3b2616';
        const leg = phase ? 2.5 : -2.5;
        rr(g, 10 + leg, -5, 3, 2, .8); rr(g, 10 - leg, 3, 3, 2, .8); rr(g, 18 - leg, -5, 3, 2, .8); rr(g, 18 + leg, 3, 3, 2, .8);
        g.fillStyle = '#8a5a34';
        g.beginPath(); g.ellipse(15, 0, 8, 4.2, 0, 0, Math.PI * 2); g.fill();
        g.beginPath(); g.ellipse(23.5, 0, 3.5, 2.2, 0, 0, Math.PI * 2); g.fill();
        g.fillStyle = '#2a1a0e';
        rr(g, 16, -1, 7, 2, 1);                                          // грива
        g.strokeStyle = '#6b4a2a';
        g.lineWidth = 1;
        g.beginPath(); g.moveTo(1, -3.5); g.lineTo(9, -3); g.moveTo(1, 3.5); g.lineTo(9, 3); g.stroke();   // голоблі
        g.fillStyle = DARK;
        rr(g, -14, -10, 11, 3, 1); rr(g, -14, 7, 11, 3, 1);              // великі колеса
        g.fillStyle = '#9b7148';
        rr(g, -21, -7.5, 22, 15, 1.5);                                   // ящик воза
        g.fillStyle = color;
        g.fillRect(-21, -7.5, 22, 2.5); g.fillRect(-21, 5, 22, 2.5);     // борти кольору місця
        g.fillStyle = 'rgba(0,0,0,.2)';
        g.fillRect(-15, -5, 1, 10); g.fillRect(-8, -5, 1, 10);
        break;
      }
      case 'motoblok':
        g.fillStyle = DARK;
        rr(g, 4, -8, 7, 3, 1); rr(g, 4, 5, 7, 3, 1);
        rr(g, -12, -8.5, 6, 3, 1); rr(g, -12, 5.5, 6, 3, 1);
        g.fillStyle = '#5e6368';
        rr(g, 5, -4.5, 9, 9, 1.5);                                       // двигун
        g.strokeStyle = '#333';
        g.lineWidth = 1.2;
        g.beginPath(); g.moveTo(5, -3); g.lineTo(-2, -6.5); g.moveTo(5, 3); g.lineTo(-2, 6.5); g.stroke();   // кермо
        g.fillStyle = color;
        rr(g, -15, -6.5, 14, 13, 1.5);                                   // причіп
        g.fillStyle = 'rgba(0,0,0,.18)';
        g.fillRect(-13, -4.5, 10, 9);
        g.fillStyle = '#5a4a3a';
        g.beginPath(); g.arc(-3, 0, 3.4, 0, Math.PI * 2); g.fill();      // водій
        g.fillStyle = '#e8c9a0';
        g.beginPath(); g.arc(-2.5, 0, 2, 0, Math.PI * 2); g.fill();
        break;
      default: // kopiyka
        g.fillStyle = DARK;
        rr(g, -13, -9.5, 7, 3, .8); rr(g, -13, 6.5, 7, 3, .8); rr(g, 7, -9.5, 7, 3, .8); rr(g, 7, 6.5, 7, 3, .8);
        g.fillStyle = color;
        rr(g, -18, -8, 36, 16, 2);
        g.fillStyle = GLASS;
        rr(g, 2, -6.5, 5, 13, 1);
        rr(g, -11, -6.5, 3, 13, 1);
        g.fillStyle = 'rgba(255,255,255,.18)';
        rr(g, -8, -6, 10, 12, 1);
        g.strokeStyle = 'rgba(0,0,0,.45)';
        g.lineWidth = 0.9;
        g.beginPath(); for (let i = -6; i <= 0; i += 3) { g.moveTo(i, -5); g.lineTo(i, 5); } g.stroke();  // багажник на даху
        g.fillStyle = '#fff6c9';
        g.fillRect(16, -6.5, 2, 3.5); g.fillRect(16, 3, 2, 3.5);
        g.fillStyle = '#c44';
        g.fillRect(-18, -6.5, 1.5, 3); g.fillRect(-18, 3.5, 1.5, 3);
    }
  }

  function sprite(st, id, seat, phase) {
    const color = carColor(st, seat);
    const key = id + ':' + color + ':' + (phase || 0) + ':' + st.sprK;
    let c = st.sprites.get(key);
    if (c) return c;
    const k = st.sprK, pad = 4;
    const [L, W] = SIZE[id] || SIZE.traktor;
    const w = Math.ceil((L + pad * 2) * k), h = Math.ceil((W + pad * 2) * k);
    c = document.createElement('canvas');
    c.width = w; c.height = h;
    const g = c.getContext('2d');
    g.setTransform(k, 0, 0, k, w / 2, h / 2);
    g.fillStyle = 'rgba(0,0,0,.28)';
    g.beginPath(); g.roundRect(-L / 2 + 1.5, -W / 2 + 2, L, W, 4); g.fill();
    paintCar(g, id, color, phase);
    c.uw = (L + pad * 2); c.uh = (W + pad * 2);
    st.sprites.set(key, c);
    return c;
  }

  // ===============================================================================================
  // Частинки: пул на 256, без алокацій у циклі малювання
  // ===============================================================================================

  const PMAX = 256;
  function particles() {
    const p = [];
    for (let i = 0; i < PMAX; i++) p.push({ on: false, x: 0, y: 0, vx: 0, vy: 0, t: 0, life: 1, r: 1, col: '', kind: 0 });
    return { list: p, next: 0 };
  }
  function spawn(ps, x, y, vx, vy, life, r, col, kind) {
    const p = ps.list[ps.next];
    ps.next = (ps.next + 1) % PMAX;
    p.on = true; p.x = x; p.y = y; p.vx = vx; p.vy = vy; p.t = 0; p.life = life; p.r = r; p.col = col; p.kind = kind || 0;
  }

  // ===============================================================================================
  // Звук: WebAudio-синтез, лише після жесту, типово вимкнено (на сайті грає радіо)
  // ===============================================================================================

  function makeAudio() {
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    const ac = new AC();
    const master = ac.createGain();
    master.gain.value = 0.25;
    master.connect(ac.destination);
    const noiseBuf = ac.createBuffer(1, ac.sampleRate, ac.sampleRate);
    const nd = noiseBuf.getChannelData(0);
    for (let i = 0; i < nd.length; i++) nd[i] = Math.random() * 2 - 1;
    // двигун своєї машини
    const eng = ac.createOscillator(), engF = ac.createBiquadFilter(), engG = ac.createGain();
    eng.type = 'sawtooth'; eng.frequency.value = 55;
    engF.type = 'lowpass'; engF.frequency.value = 420;
    engG.gain.value = 0;
    eng.connect(engF); engF.connect(engG); engG.connect(master);
    eng.start();
    // занос
    const skid = ac.createBufferSource(), skF = ac.createBiquadFilter(), skG = ac.createGain();
    skid.buffer = noiseBuf; skid.loop = true;
    skF.type = 'bandpass'; skF.frequency.value = 900; skF.Q.value = 1.4;
    skG.gain.value = 0;
    skid.connect(skF); skF.connect(skG); skG.connect(master);
    skid.start();
    const a = { ac, master, engG, eng, skG, hoofAt: 0 };
    a.tone = (f, dur, type, vol, f2) => {
      const t = ac.currentTime, o = ac.createOscillator(), gg = ac.createGain();
      o.type = type || 'sine';
      o.frequency.setValueAtTime(f, t);
      if (f2) o.frequency.exponentialRampToValueAtTime(f2, t + dur);
      gg.gain.setValueAtTime(vol || 0.2, t);
      gg.gain.exponentialRampToValueAtTime(0.001, t + dur);
      o.connect(gg); gg.connect(master);
      o.start(t); o.stop(t + dur + 0.02);
    };
    a.noise = (dur, vol, freq) => {
      const t = ac.currentTime, src = ac.createBufferSource(), f = ac.createBiquadFilter(), gg = ac.createGain();
      src.buffer = noiseBuf;
      f.type = 'bandpass'; f.frequency.value = freq || 700;
      gg.gain.setValueAtTime(vol || 0.3, t);
      gg.gain.exponentialRampToValueAtTime(0.001, t + dur);
      src.connect(f); f.connect(gg); gg.connect(master);
      src.start(t, Math.random() * 0.5, dur + 0.05);
    };
    a.horn = (car) => {
      switch (car) {
        case 'traktor': a.tone(180, 0.35, 'square', 0.12); a.tone(220, 0.35, 'square', 0.1); break;
        case 'zapor': a.tone(600, 0.12, 'square', 0.1); setTimeout(() => a.tone(800, 0.14, 'square', 0.1), 150); break;
        case 'moped': a.tone(1200, 0.18, 'square', 0.07); break;
        case 'viz': a.tone(2400, 0.6, 'sine', 0.12); a.tone(3600, 0.4, 'sine', 0.05); break;
        case 'motoblok': for (let i = 0; i < 3; i++) setTimeout(() => a.tone(90, 0.08, 'square', 0.16), i * 110); break;
        default: a.tone(500, 0.3, 'sawtooth', 0.09);
      }
    };
    a.close = () => { try { ac.close(); } catch { /* уже закрито */ } };
    return a;
  }

  // ===============================================================================================
  // Стан картки
  // ===============================================================================================

  function state(root, ctx) {
    if (!root._rally) {
      root._rally = {
        ctx, raf: 0, wrap: null, cv: null, g: null, bg: null, skid: null, dark: null, lamps: null,
        pxW: 0, pxH: 0, k: 1, sprK: 1, sprites: new Map(), pal: null, trackKey: '', track: null, td: null, fields: null,
        view: null, f: null, fAt: 0, prevF: null,
        // годинник сервера
        base: 0, baseNow: 0, P: TICK, clockOn: false, rate: [],
        lead: LEAD0, leadUpAt: 0, leadDownAt: 0, lateSeen: -1, late: [],
        // своя машина
        mine: -1, sim: null, ring: [], maskAt: new Int32Array(64), sent: 0, sentT: 0, cur: 0, hbUntil: 0,
        plan: new Int32Array(64), planT: new Int32Array(64).fill(-1),
        keys: new Set(), touch: new Set(), off: { x: 0, y: 0, a: 0 }, drawn: [],
        // чужі
        others: [], oT: [], oOff: [],
        // ефекти
        ps: particles(), flashes: [], shakeUntil: 0, lastEv: new Int32Array(6), skidPrev: new Float64Array(24), skidOk: new Uint8Array(6), wrongN: 0,
        horns: new Float64Array(6), audio: null, sound: store.get('rally.sound', '0') === '1',
        autogas: store.get('rally.autogas', '1') === '1',
        perf: new Float64Array(300), perfN: 0, perfI: 0, hudSig: '', lowerSig: '', lastT: -1, lastDraw: 0,
        sentReset: false, mountCtl: false, seenFin: 0, lastPh: -1,
        // готові об'єкти для циклу малювання — щоб rAF не смітив
        prevOwn: blank(), oSims: [], oPrev: [], rp: { x: 0, y: 0, a: 0 }, order: new Int32Array(6),
        cs: { x: 0, y: 0, a: 0, vf: 0, vl: 0, air: 0, mask: 0, stall: 0, boostT: 0, cell: 0 },
        efx: { x: 0, y: 0, a: 0, vf: 0, vl: 0, air: 0, mask: 0, stall: 0, boostT: 0, cell: 0, ev: 0 },
        rm: reduced() ? 0.5 : 1, fixes: 0, jumps: 0, lbox: new Float64Array(24), lord: new Int32Array(6), labW: [],
        // привид найкращого кола (соло): запис поточного кола й сам привид траси
        gRec: new Float32Array(GMAX * 3), gRecN: 0, gLapT: -1, ghost: null, ghostTried: '', solo: false,
      };
      for (let i = 0; i < SEATS; i++) {
        root._rally.drawn.push({ x: 0, y: 0, a: 0, ok: false });
        root._rally.oOff.push({ x: 0, y: 0, a: 0 });
        root._rally.oPrev.push(blank());
      }
    }
    root._rally.ctx = ctx;
    ctx._rally = root._rally;
    return root._rally;
  }

  function palette(st) {
    const c = st.ctx.css;
    return {
      text: c('--text', '#ecf1ea'), muted: c('--muted', '#9db3a5'), panel: c('--panel', '#1c3328'), bg2: c('--bg2', '#16291f'),
      accent: c('--accent', '#f4c542'), danger: c('--danger', '#e57373'), ok: c('--ok', '#7bd389'),
      seats: SEAT_VARS.map(([n, f]) => c(n, f)),
    };
  }

  // ---------- розмір канваса ----------

  function layout(st) {
    if (!st.cv) return;
    const rect = st.wrap.getBoundingClientRect();
    const cssW = Math.max(200, rect.width || 800);
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    let pxW = Math.round(cssW * dpr);
    if (pxW > 1920) pxW = 1920;
    const pxH = Math.round(pxW * 9 / 16);
    if (pxW === st.pxW && pxH === st.pxH) return;
    st.pxW = pxW; st.pxH = pxH;
    st.cv.width = pxW; st.cv.height = pxH;
    st.k = pxW / WU;
    st.cssK = pxW / cssW;
    st.sprK = Math.max(1, Math.round(st.k * SPR * 2) / 2 * 1.25);
    st.sprites.clear();
    st.trackKey = '';
    st.skid = null;
  }

  function offscreen(w, h) {
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    return c;
  }

  function ensureTrack(st) {
    const td = st.td;
    if (!td || !st.pxW) return;
    const key = td.id + ':' + st.pxW + ':' + (st.view && st.view.random && st.view.ph === 0 ? 'r' : '');
    if (key === st.trackKey) return;
    st.trackKey = key;
    st.track = S.buildTrack(td);
    st.fields = distFields(st.track);
    st.sectors = sectors(st.track);
    st.bg = offscreen(st.pxW, st.pxH);
    const g = st.bg.getContext('2d');
    g.setTransform(st.k, 0, 0, st.k, 0, 0);
    paintTrack(g, st.track, td, st.fields, st.sectors);
    st.cornTop = null;
    if (td.corn) {
      st.cornTop = offscreen(st.pxW, st.pxH);
      const cg = st.cornTop.getContext('2d');
      cg.setTransform(st.k, 0, 0, st.k, 0, 0);
      for (let y = 0; y < S.ROWS; y++) for (let x = 0; x < S.COLS; x++)
        if (st.track.tile[y * S.COLS + x] === S.CORN) paintCorn(cg, x * 32, y * 32, lcg(y * 131 + x * 17 + 3), 1);
    }
    if (!st.skid || st.skid.width !== st.pxW) {
      st.skid = offscreen(st.pxW, st.pxH);
      st.skidG = st.skid.getContext('2d');
      st.skidG.setTransform(st.k, 0, 0, st.k, 0, 0);
    } else st.skidG.clearRect(0, 0, WU, HU);
    if (td.night) buildNight(st);
    else st.dark = st.lamps = null;
  }

  /// Ніч: темрява з уже вирізаними ліхтарями (статично) — щокадру лише копія й фари машин. Темрява м'яка, тож
  /// живе в половинній роздільності: вчетверо менше пікселів на кожне вирізання, а на канвас лягає одним
  /// розтягнутим drawImage. Світло фар — готовий спрайт (конус + коло довкола), а не градієнти щокадру.
  const NIGHT = 'rgba(6,10,20,.8)', NIGHT_DIV = 4;
  function buildNight(st) {
    const hk = st.k / NIGHT_DIV, hw = Math.ceil(st.pxW / NIGHT_DIV), hh = Math.ceil(st.pxH / NIGHT_DIV);
    st.lamps = offscreen(hw, hh);
    const g = st.lamps.getContext('2d');
    g.setTransform(hk, 0, 0, hk, 0, 0);
    g.fillStyle = NIGHT;
    g.fillRect(0, 0, WU, HU);
    g.globalCompositeOperation = 'destination-out';
    const tr = st.track;
    const hole = (x, y, r, a) => {
      const gr = g.createRadialGradient(x, y, 0, x, y, r);
      gr.addColorStop(0, 'rgba(0,0,0,' + a + ')'); gr.addColorStop(1, 'rgba(0,0,0,0)');
      g.fillStyle = gr;
      g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill();
    };
    // ліхтарі — на кожному шостому стовпі тину, що дивиться на дорогу (упритул чи через смужку трави)
    const lamps = [];
    let n = 0;
    for (let y = 0; y < S.ROWS; y++) {
      for (let x = 0; x < S.COLS; x++) {
        if (tr.codeAt(x, y) !== S.FENCE) continue;
        let road = null;
        for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
          const k = tr.codeAt(x + dx, y + dy);
          if (isRoadish(k) || ((k === S.GRASS || k === S.CORN) && isRoadish(tr.codeAt(x + 2 * dx, y + 2 * dy)))) { road = [dx, dy]; break; }
        }
        if (!road || n++ % 6) continue;
        const lx = x * 32 + 16 + road[0] * 12, ly = y * 32 + 16 + road[1] * 12;
        lamps.push(lx, ly);
        hole(lx, ly, 74, 0.85);
      }
    }
    const l = tr.gates[0][0];
    for (let y = l[1]; y < l[1] + l[3]; y++) hole(l[0] * 32 + 16, y * 32 + 16, 34, 0.7);
    // сама лампа: тепла цятка з ореолом поверх темряви
    g.globalCompositeOperation = 'source-over';
    for (let i = 0; i < lamps.length; i += 2) {
      const gr = g.createRadialGradient(lamps[i], lamps[i + 1], 0, lamps[i], lamps[i + 1], 16);
      gr.addColorStop(0, 'rgba(255,236,170,.95)'); gr.addColorStop(0.25, 'rgba(255,220,140,.45)'); gr.addColorStop(1, 'rgba(255,210,120,0)');
      g.fillStyle = gr;
      g.beginPath(); g.arc(lamps[i], lamps[i + 1], 16, 0, Math.PI * 2); g.fill();
    }
    st.dark = offscreen(hw, hh);
    st.darkG = st.dark.getContext('2d');
    // світло машини: конус фар 55° на 220 u і м'яке коло 60 u — ніс праворуч, центр спрайта в машині
    const R = 220, rp = Math.ceil(R * hk) + 2;
    st.light = offscreen(rp * 2, rp * 2);
    const lg = st.light.getContext('2d');
    lg.setTransform(hk, 0, 0, hk, rp, rp);
    const cone = lg.createRadialGradient(0, 0, 8, 0, 0, R);
    cone.addColorStop(0, 'rgba(0,0,0,.95)'); cone.addColorStop(0.6, 'rgba(0,0,0,.55)'); cone.addColorStop(1, 'rgba(0,0,0,0)');
    lg.fillStyle = cone;
    lg.beginPath(); lg.moveTo(0, 0); lg.arc(0, 0, R, -0.48, 0.48); lg.closePath(); lg.fill();
    const glow = lg.createRadialGradient(0, 0, 0, 0, 0, 60);
    glow.addColorStop(0, 'rgba(0,0,0,.85)'); glow.addColorStop(1, 'rgba(0,0,0,0)');
    lg.fillStyle = glow;
    lg.beginPath(); lg.arc(0, 0, 60, 0, Math.PI * 2); lg.fill();
    st.lightR = rp;
  }

  // ===============================================================================================
  // Годинник сервера, lead, передбачення своєї машини, екстраполяція чужих
  // ===============================================================================================

  /// Оцінка поточного тика сервера (дробова): база + час від неї / мс на тик.
  function srvTick(st, now) {
    return st.base + (now - st.baseNow) / st.P;
  }

  function noteClock(st, t, now) {
    if (!st.clockOn) {
      st.clockOn = true;
      st.base = t; st.baseNow = now; st.P = TICK;
      st.rate = [[t, now]];
      return;
    }
    const pred = srvTick(st, now);
    const err = t - pred;
    if (Math.abs(err) > 5) { st.base = t; st.baseNow = now; }
    else { st.base = pred + err * 0.08; st.baseNow = now; }
    // мс на тик: сервер тикає не рівно по 40 (його годинник кроком 20 мс), тож міряємо наклон за кілька секунд
    const last = st.rate[st.rate.length - 1];
    if (t - last[0] >= 25) {
      st.rate.push([t, now]);
      if (st.rate.length > 8) st.rate.shift();
      const a = st.rate[0], b = st.rate[st.rate.length - 1];
      if (b[0] - a[0] >= 50) {
        const p = (b[1] - a[1]) / (b[0] - a[0]);
        if (p > 30 && p < 90) st.P = st.P * 0.7 + p * 0.3;
      }
    }
  }

  /// Своє запізнення з кадру: лише коли сервер отримав НОВИЙ ввід (it змінився) — по одному зразку на ввід.
  function adaptLead(st, c, o, now) {
    const it = c[o + 14], lt = c[o + 13];
    if (it === st.lateSeen || it === 0) return;
    st.lateSeen = it;
    st.late.push(lt);
    if (st.late.length > 6) st.late.shift();
    if (lt > -1 && now - st.leadUpAt > 500 && st.lead < LEAD_MAX) { st.lead++; st.leadUpAt = now; return; }
    if (st.late.length >= 6 && Math.max(...st.late) <= -3 && now - st.leadDownAt > 2000 && st.lead > LEAD_MIN) {
      st.lead--; st.leadDownAt = now; st.late.length = 0;
    }
  }

  /// Знімок стану машини в готовий об'єкт (кільце історії й «попередній тик» живуть без алокацій).
  function snapInto(s, c) {
    s.x = c.x; s.y = c.y; s.a = c.a; s.vf = c.vf; s.vl = c.vl; s.air = c.air; s.oil = c.oil; s.stall = c.stall;
    s.boostT = c.boostT; s.boostCd = c.boostCd; s.cell = c.cell; s.mask = c.mask;
    return s;
  }

  // ---------- привид найкращого кола (соло) ----------
  // Заїзд на час: своя симуляція щокроку пише позицію в буфер поточного кола; закрилось коло, кращe за привида
  // траси, — буфер стає привидом і лягає в localStorage (x, y у чвертях u, курс — ціле; ~1–2 тис. чисел).
  // Наступні кола напівпрозорий привид їде поруч: видно, де саме програєш. Лише клієнт — сервер про нього не знає.

  function saveGhost(st, ms) {
    const n = st.gRecN, id = st.td && st.td.id;
    if (!id || n < 10) return;
    st.ghost = { id, ms, n, d: st.gRec.slice(0, n * 3) };
    try {
      const a = new Array(n * 3);
      for (let i = 0; i < n * 3; i++) a[i] = Math.round(st.ghost.d[i] * (i % 3 === 2 ? 1 : 4));
      store.set('rally.ghost.' + id, JSON.stringify({ ms, n, d: a }));
    } catch { /* повне сховище — привид лишиться до кінця сесії */ }
  }

  function loadGhost(st) {
    const id = st.td && st.td.id;
    if (!id || st.ghostTried === id) return;
    st.ghostTried = id;
    st.ghost = null;
    try {
      const j = JSON.parse(store.get('rally.ghost.' + id, '') || 'null');
      if (!j || !(j.ms > 0) || !Array.isArray(j.d) || !(j.n > 10) || j.n > GMAX || j.d.length !== j.n * 3) return;
      const d = new Float32Array(j.n * 3);
      for (let i = 0; i < d.length; i++) d[i] = j.d[i] / (i % 3 === 2 ? 1 : 4);
      st.ghost = { id, ms: j.ms, n: j.n, d };
    } catch { /* кривий запис — без привида */ }
  }

  /// Крок своєї симуляції t: коло закрилось — може, це новий привид; далі — позиція в буфер поточного кола.
  function ghostRec(st, car, t) {
    if (!st.solo) return;
    if (car.ev & S.EV.lap) {
      // перше коло — з місця, з решітки: привидом стає лише коло з ходу (друге й далі)
      if (st.gLapT >= 0 && car.lap >= 2 && car.lastMs > 0 && (!st.ghost || car.lastMs < st.ghost.ms)) saveGhost(st, car.lastMs);
      st.gLapT = t;
      st.gLapN = car.lap;
      st.gRecN = 0;
    }
    if (st.gLapT < 0) return;
    const i = t - st.gLapT;
    if (i < 0 || i >= GMAX) return;
    const d = st.gRec, o = i * 3;
    d[o] = car.x / SUB; d[o + 1] = car.y / SUB; d[o + 2] = car.a;
    st.gRecN = i + 1;
  }

  function drawGhost(st, g, rt) {
    const gh = st.ghost, f = st.f;
    if (!st.solo || !gh || f.ph !== 2 || st.gLapT < 0 || st.mine < 0 || f.c[st.mine * STRIDE + 10] !== 0) return;
    const gi = rt - st.gLapT;
    if (gi < 0 || gi >= gh.n - 1) return;
    const i0 = gi | 0, fr = gi - i0, d = gh.d, o = i0 * 3;
    const x = d[o] + (d[o + 3] - d[o]) * fr, y = d[o + 1] + (d[o + 4] - d[o + 1]) * fr;
    const a = d[o + 2] + wrapA(d[o + 5] - d[o + 2]) * fr;
    const id = (st.view && st.view.cars && st.view.cars[st.mine]) || 'traktor';
    const spr = sprite(st, id, st.mine, 0);
    g.save();
    g.globalAlpha = 0.36;
    g.translate(x, y);
    g.rotate(a / 1024 * Math.PI * 2);
    g.scale(SPR, SPR);
    g.drawImage(spr, -spr.uw / 2, -spr.uh / 2, spr.uw, spr.uh);
    g.restore();
  }

  /// Своя машина: симуляція лише з нею (без чужих — зіткнення виправить сервер), кільце станів на 64 тики.
  function startSim(st, f) {
    const seat = st.mine;
    st.sim = S.create(st.track, st.view.laps || 3);
    st.sim.live = st.view.live | 0;
    st.sim.T = f.t;
    const c = st.sim.load(seat, f.c, seat * STRIDE);
    c.car = (st.view.cars && st.view.cars[seat]) || 'traktor';
    if (!st.ring.length) for (let i = 0; i < 64; i++) st.ring.push(blank());
    for (const r of st.ring) r.t = -1;
    snapInto(st.ring[f.t & 63], c).t = f.t;
    st.maskAt.fill(0);
    st.maskAt[f.t & 63] = c.mask;
    // чинна маска — серверна; надіслане на пізніші тики чекає свого тика в плані
    st.cur = c.mask;
    snapInto(st.prevOwn, c);
    st.off.x = st.off.y = st.off.a = 0;
    // коло привида: на світлофорі — з тика зеленого; перезапуск симуляції посеред того самого кола (кадр
    // обігнав свою симуляцію, вкладка спала) початку кола не міняє; після F5 чи на іншому колі — з наступної лінії
    const lapNow = f.c[seat * STRIDE + 6];
    if (f.t <= S.COUNT) { st.gLapT = S.COUNT; st.gLapN = 0; st.gRecN = 0; }
    else if (st.gLapT < 0 || st.gLapN !== lapNow) { st.gLapT = -1; st.gRecN = 0; }
  }

  /// Своя симуляція зібрана не з тієї траси чи не з тим зерном живності, що в чинному виді. Кадр нової гонки
  /// чемпіонату сервер шле в одній пачці з видом, але ПЕРЕД ним: симуляція вставала на трасі минулої гонки й так
  /// їхала до фінішу — сервер виправляв її щокадру, і з другої траси машина смикалась.
  function staleSim(st) {
    return st.sim.track.id !== st.track.id || st.sim.live !== ((st.view && st.view.live) | 0);
  }

  /// Кадр сервера про свою машину: збіглось — нічого; ні — переписати історію й переграти свої маски.
  function reconcile(st, f, now) {
    const seat = st.mine, o = seat * STRIDE, c = f.c;
    if (!st.sim || staleSim(st) || f.t > st.sim.T || st.sim.T - f.t > 60) { startSim(st, f); return; }
    const was = st.ring[f.t & 63];
    const tm = c[o + 9];
    const same = was.t === f.t && was.x === c[o] && was.y === c[o + 1] && was.a === c[o + 2] && was.vf === c[o + 3] && was.vl === c[o + 4]
      && was.air === (tm & 15) && was.oil === ((tm >> 4) & 31) && was.stall === ((tm >> 9) & 31) && was.boostT === ((tm >> 14) & 31)
      && was.boostCd === ((tm >> 19) & 63);
    const car = st.sim.cars[seat];
    car.lap = c[o + 6]; car.next = c[o + 7]; car.fin = Math.max(0, c[o + 10]); car.ghost = car.fin > 0;
    if (same) return;
    const oldX = car.x, oldY = car.y, oldA = car.a;
    const saveT = st.sim.T;
    st.sim.T = f.t;
    st.sim.load(seat, c, o);
    snapInto(st.ring[f.t & 63], car).t = f.t;
    while (st.sim.T < saveT) {
      const t = st.sim.T + 1;
      car.mask = st.maskAt[t & 63];
      snapInto(st.prevOwn, car);
      st.sim.tick();
      snapInto(st.ring[t & 63], car).t = t;
    }
    st.fixes++;
    // скільки «з'їхало» — гасимо поступово; великий стрибок чесніше показати стрибком
    st.off.x += (oldX - car.x) / SUB;
    st.off.y += (oldY - car.y) / SUB;
    st.off.a += wrapA(oldA - car.a);
    if (Math.abs(st.off.x) > 120 || Math.abs(st.off.y) > 120) { st.off.x = st.off.y = st.off.a = 0; st.jumps++; }
  }

  function stepOwn(st, target) {
    const seat = st.mine, car = st.sim.cars[seat];
    let n = 0;
    if (target - st.sim.T > 25) {
      // вкладка спала: наздоганяти нема сенсу — з останнього кадру
      if (st.f) startSim(st, st.f);
      return;
    }
    while (st.sim.T < target && n < 5) {
      const t = st.sim.T + 1;
      flush(st, t);
      if (st.planT[t & 63] === t) st.cur = st.plan[t & 63];
      car.mask = st.cur;
      st.maskAt[t & 63] = st.cur;
      snapInto(st.prevOwn, car);
      st.sim.tick();
      snapInto(st.ring[t & 63], car).t = t;
      n++;
      ghostRec(st, car, t);
      effects(st, seat, car, true);
    }
  }

  /// Чужі машини: від стану в кадрі — кроки власною симуляцією з їхньою маскою (без зіткнень між ними).
  function resetOthers(st, f, rt) {
    for (let i = 0; i < SEATS; i++) {
      const o = i * STRIDE;
      if (i === st.mine || f.c[o + 10] < 0) { st.others[i] = null; continue; }
      const d = st.drawn[i];
      // одна симуляція на місце на всю гонку: кадр лише перезаписує стан машини
      let sim = st.oSims[i];
      if (!sim || sim.track !== st.track) sim = st.oSims[i] = S.create(st.track, 99);
      sim.live = (st.view && st.view.live) | 0;
      sim.T = f.t;
      const c = sim.load(i, f.c, o);
      c.car = (st.view && st.view.cars && st.view.cars[i]) || 'traktor';
      st.others[i] = sim;
      snapInto(st.oPrev[i], c);
      if (EXTRAPOLATE && f.ph === 2) advanceOther(st, i, rt, f.t);
      // де малювали — там і лишаємо: різницю гасимо поступово
      if (d.ok) {
        const n = renderPos(st, i, rt);
        const off = st.oOff[i];
        off.x = d.x - n.x; off.y = d.y - n.y; off.a = wrapA(Math.round(d.a - n.a));
        if (Math.abs(off.x) > 120 || Math.abs(off.y) > 120) off.x = off.y = off.a = 0;
      }
    }
  }

  function advanceOther(st, i, rt, fT) {
    const sim = st.others[i];
    if (!sim) return;
    const cap = fT + Math.min(st.lead + 1, 6);
    const target = Math.min(Math.floor(rt), cap);
    const c = sim.cars[i];
    while (sim.T < target) {
      snapInto(st.oPrev[i], c);
      sim.tick();
    }
  }

  /// Позиція для малювання (u) з екстраполяцією всередині тика — у спільний об'єкт st.rp (без алокацій).
  function renderPos(st, i, rt) {
    const sim = i === st.mine ? st.sim : st.others[i];
    const c = sim.cars[i];
    const prev = i === st.mine ? st.prevOwn : st.oPrev[i];
    let al = rt - sim.T;
    if (al < 0) al = 0; else if (al > 1) al = 1;
    if (st.f && st.f.ph !== 2) al = 0;
    const rp = st.rp;
    rp.x = (c.x + (c.x - prev.x) * al) / SUB;
    rp.y = (c.y + (c.y - prev.y) * al) / SUB;
    rp.a = c.a + wrapA(c.a - prev.a) * al;
    return rp;
  }

  // ===============================================================================================
  // Ввід: маска з клавіш/пальця, на сервер — лише зміна, не частіше ніж раз на тик
  // ===============================================================================================

  const KEYS = {
    ArrowLeft: 'l', KeyA: 'l', ArrowRight: 'r', KeyD: 'r', ArrowUp: 'g', KeyW: 'g', ArrowDown: 'b', KeyS: 'b', Space: 'h',
  };
  const KEY_BY_CHAR = { a: 'l', ф: 'l', d: 'r', в: 'r', w: 'g', ц: 'g', s: 'b', і: 'b', ' ': 'h' };
  function keyOf(e) {
    return KEYS[e.code] || KEY_BY_CHAR[String(e.key || '').toLowerCase()] || (e.key === 'ArrowLeft' ? 'l' : e.key === 'ArrowRight' ? 'r' : e.key === 'ArrowUp' ? 'g' : e.key === 'ArrowDown' ? 'b' : null);
  }
  const has = (st, k) => st.keys.has(k) || st.touch.has(k);

  function wantMask(st, t) {
    let m = 0;
    if (has(st, 'l')) m |= 1;
    if (has(st, 'r')) m |= 2;
    if (has(st, 'g') || (st.autogas && coarse())) m |= 4;
    if (has(st, 'b')) m |= 8;
    if (has(st, 'h') || t < st.hbUntil) m |= 16;
    return m;
  }
  const coarse = () => HGames.ui.coarse();

  /// Надіслати маску, якщо змінилась. t — тик, на який вона ляже (наступний крок своєї симуляції).
  /// Не частіше ніж раз на тик — ≤ 25 на секунду при квоті каркаса 30 Input/с (понад неї сервер мовчки
  /// відкидає, і передбачення розходилось би з сервером на пів секунди). Друга зміна в межах того самого тика
  /// чекає наступного кроку й їде вже з останньою маскою; soon — не чекати кроку (вкладка ховається, rAF стане):
  /// одразу на наступний після надісланого тик.
  function flush(st, t, soon) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing || !st.sim || !st.f || st.f.ph === 3) return;
    const k = wantMask(st, t);
    if (k === st.sent && !st.forceSend) return;
    if (t <= st.sentT) {
      if (!soon) return;
      t = st.sentT + 1;
    }
    st.forceSend = false;
    st.sent = k;
    st.sentT = t;
    // свій план — як кільце сервера: маска ляже рівно на тику t і в симуляції, і на сервері
    st.plan[t & 63] = k;
    st.planT[t & 63] = t;
    ctx.input('ctl', { t: t, k: k });
  }

  function release(st) {
    st.keys.clear();
    st.touch.clear();
    st.hbUntil = 0;
    if (st.sim) flush(st, st.sim.T + 1, true);
  }

  function horn(st) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    if (now - (st.hornAt || 0) < 1000) return;
    st.hornAt = now;
    ctx.input('horn');
  }

  function resetCar(st) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing || !st.f || st.f.ph !== 2) return;
    ctx.input('reset');
  }

  // ===============================================================================================
  // Ефекти з подій кадру й своєї симуляції
  // ===============================================================================================

  function effects(st, i, c, own, evOnly) {
    const ps = st.ps, x = c.x / SUB, y = c.y / SUB, ev = c.ev, rm = st.rm;
    const code = st.track ? st.track.tile[c.cell] : 0;
    const speed = Math.abs(c.vf), a = c.a / 1024 * Math.PI * 2, ca = Math.cos(a), sa = Math.sin(a);
    const rx = x - ca * 12, ry = y - sa * 12;
    if (evOnly) {
      if (ev & S.EV.car) { for (let n = 0; n < 5 * rm; n++) spawn(ps, x, y, (Math.random() - 0.5) * 3, (Math.random() - 0.5) * 3, 0.25, 1.5, '#ffffff', 2); sfx(st, 'hit'); }
      return;
    }
    // пил на траві й кукурудзі, сніг на льоду в заносі, сліди шин
    if (c.air === 0 && speed > 150 && (code === S.GRASS || code === S.CORN || code === S.MUD) && Math.random() < 0.6 * rm)
      spawn(ps, rx + (Math.random() - 0.5) * 8, ry + (Math.random() - 0.5) * 8, -ca * 0.3, -sa * 0.3, 0.3, 3 + Math.random() * 3, code === S.MUD ? '#4a3220' : '#9c8558', 0);
    const sliding = Math.abs(c.vl) >= 192 || ((c.mask & 16) && speed > 128);
    if (sliding && c.air === 0 && code === S.ICE && Math.random() < 0.7 * rm)
      spawn(ps, rx, ry, (Math.random() - 0.5) * 0.8, (Math.random() - 0.5) * 0.8, 0.35, 2.5, '#ffffff', 0);
    if (sliding && c.air === 0 && (code === S.ROAD || code === S.ICE || code === S.PUDDLE || code === S.OIL || code === S.BOOST)) skid(st, i, c, code === S.ICE);
    else st.skidOk[i] = 0;
    if (c.stall > 0 && Math.random() < 0.5 * rm) spawn(ps, x, y, (Math.random() - 0.5) * 0.4, -0.5, 0.6, 4, 'rgba(150,150,150,.7)', 0);
    if (c.boostT > 0 && Math.random() < 0.9 * rm) spawn(ps, rx - ca * 4, ry - sa * 4, -ca * 1.5, -sa * 1.5, 0.2, 3.5, '#ff9a3c', 1);
    if (!ev) return;
    if (ev & S.EV.puddle) for (let n = 0; n < 10 * rm; n++) spawn(ps, x, y, (Math.random() - 0.5) * 3, (Math.random() - 0.5) * 3, 0.4, 2, '#8fc3ee', 0);
    if (ev & S.EV.wall) {
      for (let n = 0; n < 6 * rm; n++) spawn(ps, x + ca * 10, y + sa * 10, (Math.random() - 0.5) * 4, (Math.random() - 0.5) * 4, 0.25, 1.5, '#ffd84a', 2);
      // тряска — лише від справжнього удару (швидкість змінилась понад 6 u/тик), а не від кожного дотику тину боком
      if (own && st.rm === 1 && Math.hypot(st.prevOwn.vf - c.vf, st.prevOwn.vl - c.vl) > 384) st.shakeUntil = performance.now() + 120;
      sfx(st, 'hit');
    }
    if (ev & S.EV.hay) { for (let n = 0; n < 10 * rm; n++) spawn(ps, x, y, (Math.random() - 0.5) * 3, (Math.random() - 0.5) * 3, 0.5, 2, '#e6c35a', 0); sfx(st, 'hit'); }
    // живність: пір'я на все подвір'я
    if (ev & S.EV.critter) { for (let n = 0; n < 12 * rm; n++) spawn(ps, x, y, (Math.random() - 0.5) * 2.5, (Math.random() - 0.5) * 2.5, 0.8, 2.2, '#fbf6e8', 0); sfx(st, 'hit'); }
    if (ev & S.EV.car) { for (let n = 0; n < 5 * rm; n++) spawn(ps, x, y, (Math.random() - 0.5) * 3, (Math.random() - 0.5) * 3, 0.25, 1.5, '#ffffff', 2); sfx(st, 'hit'); }
    if (ev & S.EV.oil) for (let n = 0; n < 6 * rm; n++) spawn(ps, x, y, (Math.random() - 0.5) * 2, (Math.random() - 0.5) * 2, 0.4, 2, '#2a2440', 0);
    if (ev & S.EV.boost) sfx(st, 'boost');
    if (ev & S.EV.jump) sfx(st, 'jump');
    if (ev & S.EV.land) for (let n = 0; n < 8 * rm; n++) spawn(ps, x, y, (Math.random() - 0.5) * 3, (Math.random() - 0.5) * 3, 0.35, 3, '#9c8558', 0);
  }

  function skid(st, i, c, ice) {
    const g = st.skidG;
    if (!g) return;
    const a = c.a / 1024 * Math.PI * 2, ca = Math.cos(a), sa = Math.sin(a);
    const x = c.x / SUB, y = c.y / SUB;
    const bx = x - ca * 10, by = y - sa * 10;
    const w1x = bx - sa * 6, w1y = by + ca * 6, w2x = bx + sa * 6, w2y = by - ca * 6;
    // попередні точки коліс — у готовому масиві по 4 числа на місце: занос не смітить щокроку
    const p = st.skidPrev, o = i * 4;
    if (st.skidOk[i] && Math.abs(p[o] - w1x) < 30 && Math.abs(p[o + 1] - w1y) < 30) {
      g.strokeStyle = ice ? 'rgba(255,255,255,.45)' : 'rgba(20,15,10,.35)';
      g.lineWidth = 2.2;
      g.beginPath();
      g.moveTo(p[o], p[o + 1]); g.lineTo(w1x, w1y);
      g.moveTo(p[o + 2], p[o + 3]); g.lineTo(w2x, w2y);
      g.stroke();
    }
    p[o] = w1x; p[o + 1] = w1y; p[o + 2] = w2x; p[o + 3] = w2y;
    st.skidOk[i] = 1;
  }

  function sfx(st, kind, arg) {
    const a = st.audio;
    if (!a || !st.sound) return;
    try {
      switch (kind) {
        case 'hit': a.noise(0.06, 0.25, 500); break;
        case 'boost': a.tone(200, 0.4, 'sine', 0.18, 900); break;
        case 'jump': a.tone(700, 0.35, 'triangle', 0.14, 220); break;
        case 'light': a.tone(440, 0.1, 'square', 0.1); break;
        case 'green': a.tone(880, 0.3, 'square', 0.12); break;
        case 'lap': a.tone(1320, 0.12, 'sine', 0.15); setTimeout(() => a.tone(1760, 0.16, 'sine', 0.15), 120); break;
        case 'finish': [523, 659, 784, 1047].forEach((f, n) => setTimeout(() => a.tone(f, 0.18, 'triangle', 0.16), n * 150)); break;
        case 'horn': a.horn(arg); break;
        default:
      }
    } catch { /* звук не критичний */ }
  }

  function engineSound(st) {
    const a = st.audio;
    if (!a) return;
    const t = a.ac.currentTime;
    const on = st.sound && st.sim && st.f && st.f.ph === 2 && st.mine >= 0;
    const c = on ? st.sim.cars[st.mine] : null;
    const speed = c ? Math.abs(c.vf) : 0;
    const car = c ? c.car : '';
    if (!on || car === 'viz') a.engG.gain.setTargetAtTime(0, t, 0.05);
    else {
      const mul = car === 'moped' ? 2 : car === 'traktor' ? 0.7 : 1;
      a.eng.frequency.setTargetAtTime((55 + Math.min(speed, 1408) / 1408 * 155) * mul, t, 0.05);
      a.engG.gain.setTargetAtTime(0.03 + (c.mask & 4 ? 0.015 : 0), t, 0.05);
    }
    if (on && car === 'viz' && speed > 60) {
      const now = performance.now();
      const period = 420 - Math.min(speed, 900) / 900 * 300;
      if (now - a.hoofAt > period) { a.hoofAt = now; a.noise(0.04, 0.18, 1800); }
    }
    a.skG.gain.setTargetAtTime(on && c && Math.abs(c.vl) >= 192 && c.air === 0 ? 0.05 : 0, t, 0.04);
  }

  // ===============================================================================================
  // Малювання кадру
  // ===============================================================================================

  function draw(st, now) {
    const g = st.g;
    if (!g || !st.bg || !st.f) return;
    const t0 = performance.now();
    st.pal = st.pal || palette(st);
    const f = st.f, ph = f.ph, k = st.k;
    const dt = Math.min(100, now - (st.lastDraw || now));
    st.lastDraw = now;

    // час: своя машина живе на srvTick + lead; глядач — на lead = 1
    let rt = f.t;
    if (st.clockOn && (ph === 1 || ph === 2)) {
      const stale = now - st.fAt > 300;
      rt = srvTick(st, now) + (st.mine >= 0 ? st.lead : 1);
      if (st.sim) {
        if (!stale || st.sim.T - f.t < 25) stepOwn(st, Math.floor(rt));
        rt = Math.min(rt, st.sim.T + 0.999);
      } else if (stale) rt = Math.min(rt, f.t + 1);
      for (let i = 0; i < SEATS; i++) if (st.others[i] && EXTRAPOLATE && ph === 2 && !stale) advanceOther(st, i, rt, f.t);
    }

    if ((st.perfI & 63) === 0) st.rm = reduced() ? 0.5 : 1;
    const shake = st.shakeUntil > now ? 3 * st.cssK : 0;
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.drawImage(st.bg, shake ? (Math.random() - 0.5) * shake : 0, shake ? (Math.random() - 0.5) * shake : 0);
    if (st.skid) g.drawImage(st.skid, 0, 0);
    g.setTransform(k, 0, 0, k, 0, 0);

    // частинки під машинами (пил, сніг, бризки)
    const decay = Math.pow(0.8, dt / 16.7);
    st.off.x *= decay; st.off.y *= decay; st.off.a *= decay;
    for (const o of st.oOff) { o.x *= decay; o.y *= decay; o.a *= decay; }

    // машини в повітрі — зверху: вставне сортування в готовий масив
    const order = st.order;
    let n = 0;
    for (let i = 0; i < SEATS; i++) {
      if (f.c[i * STRIDE + 10] < 0) continue;
      const air = f.c[i * STRIDE + 9] & 15;
      let j = n++;
      while (j > 0 && (f.c[order[j - 1] * STRIDE + 9] & 15) > air) { order[j] = order[j - 1]; j--; }
      order[j] = i;
    }
    if (photoOn(st, now)) {
      drawPhoto(st, g, now);
      const ms = performance.now() - t0;
      st.perf[st.perfI] = ms;
      st.perfI = (st.perfI + 1) % 300;
      if (st.perfN < 300) st.perfN++;
      return;
    }
    drawGhost(st, g, rt);
    if (st.view && st.view.live && st.track && st.track.critters.length) drawCritters(st, g, ph === 2 ? rt : f.t);
    for (let j = 0; j < n; j++) drawCar(st, g, order[j], rt, now);

    tickParticles(st, g, dt);
    if (st.cornTop) {
      g.setTransform(1, 0, 0, 1, 0, 0);
      g.globalAlpha = 0.55;
      g.drawImage(st.cornTop, 0, 0);
      g.globalAlpha = 1;
      g.setTransform(k, 0, 0, k, 0, 0);
    }
    if (st.lamps) drawNight(st, g);
    g.setTransform(1, 0, 0, 1, 0, 0);
    labels(st, g, now);
    overlay(st, g, now);

    const ms = performance.now() - t0;
    st.perf[st.perfI] = ms;
    st.perfI = (st.perfI + 1) % 300;
    if (st.perfN < 300) st.perfN++;
  }

  // ---------- живність (№89): курка, гуси, віз, гості, коза — де й сервер, за тиком ----------
  const CRITTER_EMO = ['🐔', '🦢', '🐂', '💃', '🐐'];
  const CRITTER_FONT = '"Segoe UI Emoji","Apple Color Emoji","Noto Color Emoji",sans-serif';
  const crA = [0, 0], crB = [0, 0];
  function drawCritters(st, g, rt) {
    const cr = st.track.critters, live = st.view.live | 0, t0 = Math.floor(rt), u = rt - t0;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    for (let k = 0; k < cr.length; k++) {
      const d = cr[k];
      S.critterAt(d, live, t0, crA);
      S.critterAt(d, live, t0 + 1, crB);
      const x = (crA[0] + (crB[0] - crA[0]) * u) / SUB, y = (crA[1] + (crB[1] - crA[1]) * u) / SUB;
      const r = S.CRITTER_R[d[0]] / SUB;
      if (d[0] === 2) {
        // віз: дерев'яна платформа з сіном, віл попереду
        const dir = crB[0] < crA[0] ? -1 : 1;
        g.fillStyle = '#7a5230';
        g.fillRect(x - r * 0.9 - dir * r * 0.5, y - r * 0.55, r * 1.3, r * 1.1);
        g.fillStyle = '#e6c35a';
        g.fillRect(x - r * 0.8 - dir * r * 0.5, y - r * 0.45, r * 1.1, r * 0.9);
      }
      g.font = Math.round(r * 2.6) + 'px ' + CRITTER_FONT;
      g.fillText(CRITTER_EMO[d[0]] || '🐔', d[0] === 2 ? x + (crB[0] < crA[0] ? -1 : 1) * r * 0.5 : x, y);
    }
  }

  // ---------- 📸 фотофініш (№88): кільце останніх кадрів сервера, уповільнений показ близького фінішу ----------
  const PH_N = 64, PH_BEFORE = 20, PH_AFTER = 8, PH_SPEED = 0.25, PH_HOLD = 1400;

  /// Кожен кадр гонки — в кільце (x, y, курс шести машин; −1 — машини нема). Без алокацій: масиви готові.
  function photoRec(st, f) {
    if (f.ph !== 2) return;
    let r = st.phRing;
    if (!r) r = st.phRing = { t: new Int32Array(PH_N), c: new Int32Array(PH_N * 18), n: 0 };
    if (r.n > 0 && r.t[(r.n - 1) % PH_N] >= f.t) return;
    const j = r.n % PH_N;
    r.t[j] = f.t;
    for (let i = 0; i < SEATS; i++) {
      const o = i * STRIDE, q = j * 18 + i * 3;
      r.c[q] = f.c[o + 10] < 0 ? -1 : f.c[o]; r.c[q + 1] = f.c[o + 1]; r.c[q + 2] = f.c[o + 2];
    }
    r.n++;
    const p = st.photo;
    if (p && !p.clip && f.t >= p.T + PH_AFTER) photoCut(st);
  }

  /// Вирізати з кільця кліп навколо фінішу другого з пари.
  function photoCut(st) {
    const p = st.photo, r = st.phRing;
    if (!p || p.clip || !r) return;
    const t = [], c = [];
    for (let n = Math.max(0, r.n - PH_N); n < r.n; n++) {
      const j = n % PH_N;
      if (r.t[j] < p.T - PH_BEFORE || r.t[j] > p.T + PH_AFTER) continue;
      t.push(r.t[j]);
      for (let q = 0; q < 18; q++) c.push(r.c[j * 18 + q]);
    }
    p.clip = t.length >= 4 ? { t, c } : { t: [], c: [] };
  }

  /// Вид приніс фотофініш: [хто, за ким, мс, тик].
  function photoView(st, v) {
    const ph = v.photo;
    if (!ph) return;
    if (!st.photo || st.photo.T !== ph[3]) st.photo = { a: ph[0], b: ph[1], gap: ph[2], T: ph[3], clip: null, playAt: 0, played: false };
    if (!st.photo.clip && (v.ph === 3 || (st.f && st.f.t >= ph[3] + PH_AFTER))) photoCut(st);
  }

  /// Чи крутимо фотофініш зараз. Той, хто ще їде, його не бачить — покажемо, щойно доїде (чи в підсумку).
  function photoOn(st, now) {
    const p = st.photo, f = st.f;
    if (!p || !p.clip || p.clip.t.length < 4 || !f) return false;
    if (!p.playAt) {
      if (p.played || f.ph === 4) return false;   // між гонками чемпіонату — таблиця, а не повтор
      const busy = f.ph === 2 && st.mine >= 0 && f.c[st.mine * STRIDE + 10] === 0;
      if (busy) return false;
      p.playAt = now;
    }
    const len = (p.clip.t[p.clip.t.length - 1] - p.clip.t[0]) * TICK / PH_SPEED + PH_HOLD;
    if (now - p.playAt > len) { p.playAt = 0; p.played = true; st.lowerSig = ''; paintLower(st); return false; }
    return true;
  }

  function drawPhoto(st, g, now) {
    const p = st.photo, cl = p.clip, T = cl.t, n = T.length;
    let pt = T[0] + (now - p.playAt) / TICK * PH_SPEED;
    if (pt > T[n - 1]) pt = T[n - 1];
    let j = 0;
    while (j < n - 2 && T[j + 1] <= pt) j++;
    const u = Math.max(0, Math.min(1, (pt - T[j]) / Math.max(1, T[j + 1] - T[j])));
    // лінія фінішу — шахівниця через усю ширину воріт 0
    const td = st.td, gr = td.gates && td.gates[0] && td.gates[0][0];
    if (gr) {
      const vert = gr[3] >= gr[2], sq = 8;
      const x0 = vert ? td.line[1] - sq / 2 : gr[0] * 32, y0 = vert ? gr[1] * 32 : td.line[1] - sq / 2;
      const len = (vert ? gr[3] : gr[2]) * 32;
      for (let q = 0; q * sq < len; q++) for (let w = 0; w < 2; w++) {
        g.fillStyle = (q + w) & 1 ? '#111' : '#fff';
        g.fillRect(vert ? x0 + w * sq / 2 : x0 + q * sq, vert ? y0 + q * sq : y0 + w * sq / 2, vert ? sq / 2 : sq, vert ? sq : sq / 2);
      }
    }
    for (let i = 0; i < SEATS; i++) {
      const qa = j * 18 + i * 3, qb = (j + 1) * 18 + i * 3;
      if (cl.c[qa] < 0 || cl.c[qb] < 0) continue;
      const x = (cl.c[qa] + (cl.c[qb] - cl.c[qa]) * u) / SUB, y = (cl.c[qa + 1] + (cl.c[qb + 1] - cl.c[qa + 1]) * u) / SUB;
      let da = cl.c[qb + 2] - cl.c[qa + 2];
      if (da > 512) da -= 1024; else if (da < -512) da += 1024;
      const ang = (cl.c[qa + 2] + da * u) / 1024 * Math.PI * 2;
      const id = (st.view && st.view.cars && st.view.cars[i]) || 'traktor';
      const spr = sprite(st, id, i, 0);
      g.save();
      g.globalAlpha = i === p.a || i === p.b ? 1 : 0.45;
      g.translate(x, y);
      g.rotate(ang);
      g.scale(SPR, SPR);
      g.drawImage(spr, -spr.uw / 2, -spr.uh / 2, spr.uw, spr.uh);
      g.restore();
    }
    // напис згори: хто кого й на скільки
    g.setTransform(1, 0, 0, 1, 0, 0);
    const F = fonts(st), ck = st.cssK, W = st.pxW;
    const nick = (i) => whoOf(st, i);
    const text = '📸 Фотофініш: ' + nick(p.a) + ' на ' + (p.gap / 1000).toFixed(2).replace('.', ',') + ' с раніше за ' + nick(p.b);
    g.font = F.nick;
    const tw = Math.min(W - 24 * ck, g.measureText(text).width + 28 * ck), fh = 30 * ck;
    g.fillStyle = 'rgba(10,16,12,.8)';
    g.beginPath(); g.roundRect(W / 2 - tw / 2, 10 * ck, tw, fh, 10 * ck); g.fill();
    g.fillStyle = '#fff';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(text, W / 2, 10 * ck + fh / 2, tw - 16 * ck);
    g.fillStyle = 'rgba(255,255,255,.7)';
    g.fillText('× ' + String(PH_SPEED).replace('.', ','), W - 30 * ck, st.pxH - 16 * ck);
  }

  /// Що малювати для машини i — у спільний об'єкт st.cs (u, курс у кроках, швидкості й таймери).
  function carState(st, i, rt) {
    const f = st.f, o = i * STRIDE, s = st.cs;
    let c = null, off = null;
    if (i === st.mine && st.sim && (f.ph === 1 || f.ph === 2)) { c = st.sim.cars[i]; off = st.off; }
    else if (st.others[i] && f.ph === 2) { c = st.others[i].cars[i]; off = st.oOff[i]; }
    if (c) {
      const p = renderPos(st, i, rt);
      s.x = p.x + off.x; s.y = p.y + off.y; s.a = p.a + off.a;
      s.vf = c.vf; s.vl = c.vl; s.air = c.air; s.mask = c.mask; s.stall = c.stall; s.boostT = c.boostT; s.cell = c.cell;
      return s;
    }
    const tm = f.c[o + 9];
    s.x = f.c[o] / SUB; s.y = f.c[o + 1] / SUB; s.a = f.c[o + 2]; s.vf = f.c[o + 3]; s.vl = f.c[o + 4];
    s.air = tm & 15; s.mask = f.c[o + 5]; s.stall = (tm >> 9) & 31; s.boostT = (tm >> 14) & 31; s.cell = S.cellOf(f.c[o], f.c[o + 1]);
    return s;
  }

  function drawCar(st, g, i, rt, now) {
    const f = st.f, o = i * STRIDE;
    const s = carState(st, i, rt);
    const d = st.drawn[i];
    d.x = s.x; d.y = s.y; d.a = s.a; d.ok = true;
    const id = (st.view && st.view.cars && st.view.cars[i]) || 'traktor';
    const ghost = f.c[o + 10] > 0 && f.ph === 2;
    const air = s.air;
    const scale = SPR * (1 + 0.35 * (air / 14));
    const phase = id === 'viz' ? (Math.floor(s.x / 7) & 1) : 0;
    const spr = sprite(st, id, i, phase);
    const ang = s.a / 1024 * Math.PI * 2;
    if (i === st.mine) {
      // своя машина завжди з кільцем кольору місця — у купі з шести себе видно одразу, не лише за кольором
      const px = st.cssK / st.k, rr2 = 25 + 3 * (air / 14);
      g.fillStyle = 'rgba(255,255,255,.14)';
      g.strokeStyle = st.pal.seats[i];
      g.lineWidth = 2.4 * px;
      g.beginPath(); g.arc(s.x, s.y, rr2, 0, Math.PI * 2); g.fill(); g.stroke();
      g.strokeStyle = 'rgba(255,255,255,.9)';
      g.lineWidth = 1.1 * px;
      g.beginPath(); g.arc(s.x, s.y, rr2 + 2 * px, 0, Math.PI * 2); g.stroke();
    }
    if (air > 0) {
      g.fillStyle = 'rgba(0,0,0,.25)';
      g.beginPath(); g.ellipse(s.x + 6 * scale, s.y + 8 * scale, 16 * SPR, 10 * SPR, ang, 0, Math.PI * 2); g.fill();
    }
    g.save();
    g.globalAlpha = ghost ? 0.5 : 1;
    g.translate(s.x, s.y);
    g.rotate(ang);
    if (id === 'moped' && st.rm === 1) g.transform(1, 0, Math.max(-0.21, Math.min(0.21, -s.vl / 900)), 1, 0, 0);
    g.scale(scale, scale);
    g.drawImage(spr, -spr.uw / 2, -spr.uh / 2, spr.uw, spr.uh);
    g.restore();
    // полум'я турбо, дим, ефекти з кадрів чужих
    // чужі (і своє зіткнення з чужими — його знає лише сервер): події з кадру, раз на кадр
    if (f.ph === 2 && st.lastEv[i] !== st.lastT) {
      st.lastEv[i] = st.lastT;
      const e = st.efx, own = i === st.mine;
      e.x = s.x * SUB; e.y = s.y * SUB; e.a = s.a | 0; e.vf = s.vf; e.vl = s.vl; e.air = s.air; e.mask = s.mask;
      e.stall = s.stall; e.boostT = s.boostT; e.cell = s.cell; e.ev = own ? f.c[o + 8] & S.EV.car : f.c[o + 8];
      if (!own || e.ev) effects(st, i, e, own, own);
    }
  }

  function tickParticles(st, g, dt) {
    const sec = dt / 1000;
    for (const p of st.ps.list) {
      if (!p.on) continue;
      p.t += sec;
      if (p.t >= p.life) { p.on = false; continue; }
      p.x += p.vx * dt / 16.7;
      p.y += p.vy * dt / 16.7;
      const k = 1 - p.t / p.life;
      g.globalAlpha = k;
      g.fillStyle = p.col;
      if (p.kind === 2) g.fillRect(p.x - p.r, p.y - 0.5, p.r * 2, 1);
      else { g.beginPath(); g.arc(p.x, p.y, p.r * (p.kind === 1 ? k : 0.6 + 0.6 * (1 - k)), 0, Math.PI * 2); g.fill(); }
    }
    g.globalAlpha = 1;
  }

  function drawNight(st, g) {
    const d = st.darkG, hk = st.k / NIGHT_DIV, L = st.light, lr = st.lightR;
    d.setTransform(1, 0, 0, 1, 0, 0);
    d.globalCompositeOperation = 'copy';
    d.drawImage(st.lamps, 0, 0);
    d.globalCompositeOperation = 'destination-out';
    for (let i = 0; i < SEATS; i++) {
      const s = st.drawn[i];
      if (!s.ok || st.f.c[i * STRIDE + 10] < 0) continue;
      const a = s.a / 1024 * Math.PI * 2, ca = Math.cos(a), sa = Math.sin(a);
      d.setTransform(ca, sa, -sa, ca, s.x * hk, s.y * hk);
      d.drawImage(L, -lr, -lr);
    }
    d.globalCompositeOperation = 'source-over';
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.drawImage(st.dark, 0, 0, st.pxW, st.pxH);
    g.setTransform(st.k, 0, 0, st.k, 0, 0);
  }

  /// Ніки, номери місць, «ти», гудки — у пікселях, щоб текст не милився й не залежав від масштабу.
  /// Шрифти канваса — рядки складаються раз на розмір, а не щокадру.
  function fonts(st) {
    const ck = st.cssK || 1;
    if (st.fontK === ck) return st.fonts;
    st.fontK = ck;
    const fs = Math.max(10, Math.round(11 * ck)), big = Math.max(11, Math.round(13 * ck));
    st.fonts = {
      fs, big,
      nick: '600 ' + fs + 'px system-ui, sans-serif',
      num: '700 ' + Math.round(fs * 0.8) + 'px system-ui, sans-serif',
      horn: Math.round(fs * 1.4) + 'px system-ui, sans-serif',
      warn: '700 ' + Math.round(fs * 1.4) + 'px system-ui, sans-serif',
      hud: '700 ' + big + 'px system-ui, sans-serif',
      flash: '800 ' + Math.round(big * 1.7) + 'px system-ui, sans-serif',
    };
    st.labW = [];
    st.timerKey = -1;
    return st.fonts;
  }

  /// Ширина підпису — з кешу: measureText щокадру для шести ніків ні до чого.
  function labelWidth(st, g, i, text) {
    const c = st.labW[i];
    if (c && c.text === text) return c.w;
    const w = g.measureText(text).width;
    st.labW[i] = { text, w };
    return w;
  }

  /// З якою з уже поставлених міток (0..q−1) перетинається прямокутник; −1 — ні з якою.
  function labelHit(box, q, lx, y, w, h) {
    for (let p = 0; p < q; p++) {
      const b = p * 4;
      if (lx < box[b] + box[b + 2] && lx + w > box[b] && y - h / 2 < box[b + 1] + h / 2 + 1 && y + h / 2 > box[b + 1] - h / 2 - 1) return p;
    }
    return -1;
  }

  /// Ніки, номери місць, «ти», гудки — у пікселях, щоб текст не милився й не залежав від масштабу.
  /// Своя мітка — «ти» й першою; у купі чужі підписи стискаються до номера й підсуваються вгору.
  function labels(st, g, now) {
    const f = st.f, k = st.k, ck = st.cssK, pal = st.pal, F = fonts(st), fs = F.fs;
    const box = st.lbox, ord = st.lord;
    const r = fs * 0.62, h = fs * 1.4;
    g.textBaseline = 'middle';
    g.font = F.nick;
    // підписи згори донизу: вставне сортування за y машини
    let n = 0;
    for (let i = 0; i < SEATS; i++) {
      const s = st.drawn[i];
      if (!s.ok || f.c[i * STRIDE + 10] < 0) continue;
      let j = n++;
      while (j > 0 && st.drawn[ord[j - 1]].y > s.y) { ord[j] = ord[j - 1]; j--; }
      ord[j] = i;
    }
    // своя мітка — першою: її ніщо не підсуває й не стискає
    for (let q = 1; q < n; q++) {
      if (ord[q] !== st.mine) continue;
      for (let j = q; j > 0; j--) ord[j] = ord[j - 1];
      ord[0] = st.mine;
      break;
    }
    const lift = 24 * k * SPR / 1.2;
    for (let q = 0; q < n; q++) {
      const i = ord[q], s = st.drawn[i];
      const mine = i === st.mine;
      const nick = whoOf(st, i);
      const plate = st.view && st.view.plates ? st.view.plates[i] : null;
      const text = (mine ? 'ти' : nick) + (plate ? ' ▭' + plate : '');
      const maxW = (plate ? 120 : 76) * ck;
      const tw = Math.min(labelWidth(st, g, i, text), maxW);
      let w = tw + r * 2 + 6 * ck;
      const x = s.x * k;
      let y = s.y * k - lift - 6 * ck;
      // мітка не вилазить за канвас: по боках притискаємо до краю, згори — див. нижче
      let lx = Math.max(2, Math.min(st.pxW - w - 2, x - w / 2));
      // у купі чужа мітка стискається до кружка з номером: шість плашок з ніками закривали машини одна одній
      let compact = false;
      if (!mine && labelHit(box, q, lx, y, w, h) >= 0) {
        compact = true;
        w = r * 2 + 4 * ck;
        lx = Math.max(2, Math.min(st.pxW - w - 2, x - w / 2));
      }
      // не налазити на вже поставлені: піднімаємо, поки є перетин
      for (let tries = 0; tries < 4; tries++) {
        const hit = labelHit(box, q, lx, y, w, h);
        if (hit < 0) break;
        y = box[hit * 4 + 1] - h - 1;
      }
      // машина на самій верхній прямій (чи купа підняла мітку за край) — підпис під машиною
      if (y - h / 2 < 2) y = Math.max(y, s.y * k + lift + 6 * ck);
      box[q * 4] = lx; box[q * 4 + 1] = y; box[q * 4 + 2] = w;
      g.fillStyle = mine ? 'rgba(10,16,12,.78)' : 'rgba(10,16,12,.55)';
      g.beginPath(); g.roundRect(lx, y - fs * 0.7, w, h, fs * 0.7); g.fill();
      if (mine) { g.strokeStyle = pal.seats[i]; g.lineWidth = 1.4 * ck; g.stroke(); }
      g.fillStyle = pal.seats[i];
      g.beginPath(); g.arc(lx + r + 2 * ck, y, r, 0, Math.PI * 2); g.fill();
      if (mine) { g.strokeStyle = '#fff'; g.lineWidth = 1.6 * ck; g.stroke(); }
      g.textAlign = 'center';
      g.fillStyle = '#10150f';
      g.font = F.num;
      g.fillText(SEAT_NUM[i], lx + r + 2 * ck, y + 0.5);
      g.font = F.nick;
      if (!compact) {
        g.fillStyle = '#fff';
        g.textAlign = 'left';
        g.fillText(text, lx + r * 2 + 5 * ck, y + 0.5, maxW);
      }
      if (now - st.horns[i] < 400) {
        g.font = F.horn;
        g.textAlign = 'center';
        g.fillText('📣', lx + w + fs, y);
        g.font = F.nick;
      }
    }
    g.textAlign = 'center';
    // «↩ не туди!» під своєю машиною
    if (st.mine >= 0 && st.wrong && f.ph === 2) {
      const s = st.drawn[st.mine];
      g.font = F.warn;
      g.fillStyle = pal.danger;
      g.fillText('↩ не туди!', s.x * k, s.y * k + 34 * k + 8 * ck);
    }
  }

  function overlay(st, g, now) {
    const f = st.f, W = st.pxW, H = st.pxH, ck = st.cssK, pal = st.pal, F = fonts(st);
    const fs = F.big;
    g.textBaseline = 'middle';
    // світлофор
    if (f.ph === 1 || (f.ph === 2 && now - st.greenAt < 600)) {
      // посеред поля: старт на всіх трасах — на верхній прямій, і світлофор згори закривав би решітку
      const r = Math.max(12 * ck, 26 * st.k), cx = W / 2, cy = H / 2 - r * 0.6, n = f.ph === 1 ? (f.s > 50 ? 1 : f.s > 25 ? 2 : 3) : 0;
      g.fillStyle = 'rgba(20,20,20,.88)';
      g.beginPath(); g.roundRect(cx - r * 4.2, cy - r * 1.5, r * 8.4, r * 3, r * 1.2); g.fill();
      for (let i = 0; i < 3; i++) {
        g.fillStyle = f.ph === 2 ? '#3ddc5a' : i < n ? '#e84a3c' : '#3a2a28';
        g.beginPath(); g.arc(cx + (i - 1) * r * 2.6, cy, r, 0, Math.PI * 2); g.fill();
      }
      g.textAlign = 'center';
      g.font = F.flash;
      g.fillStyle = f.ph === 2 ? '#3ddc5a' : '#fff';
      g.fillText(f.ph === 2 ? 'Руш!' : 'Готуйсь…', cx, cy + r * 2.7);
    }
    // таймер гонки й коло — унизу ліворуч
    if (f.ph >= 2 && st.raceMs !== undefined) {
      const mine = st.mine >= 0 ? f.c[st.mine * STRIDE + 6] : -1;
      const laps = (st.view && st.view.laps) || 3;
      const shown = mine >= 0 ? Math.min(laps, mine + (f.c[st.mine * STRIDE + 10] > 0 ? 0 : 1)) : 0;
      g.font = F.hud;
      // доїхав — годинник стоїть на своєму часі (у підсумку — рівно той, що в таблиці), а не біжить за іншими
      let ms = st.raceMs;
      if (mine >= 0 && f.c[st.mine * STRIDE + 10] > 0) {
        const row = f.ph === 3 && st.view && st.view.results ? st.view.results.find((r) => r.seat === st.mine) : null;
        ms = row && row.fin ? row.ms : st.finMs || ms;
      }
      // рядок міняється раз на десяту секунди — тоді й складаємо та міряємо
      const key = ((ms / 100) | 0) * 64 + shown * 8 + laps + (mine >= 0 ? 0.5 : 0);
      if (key !== st.timerKey) {
        st.timerKey = key;
        st.timerText = clock(ms, 1) + (mine >= 0 ? '   Коло ' + shown + '/' + laps : '');
        st.timerW = g.measureText(st.timerText).width;
      }
      const text = st.timerText;
      const w = st.timerW + 16 * ck;
      // під пальцем унизу — кнопки керма й газу: тоді таймер і «⌛» угорі (праворуч угорі — гудок і ↺)
      const ty = st.touchOn ? 6 * ck : H - 30 * ck, tr = st.touchOn ? W - 92 * ck : W - 8 * ck;
      g.fillStyle = 'rgba(10,16,12,.6)';
      g.beginPath(); g.roundRect(8 * ck, ty, w, 22 * ck, 8 * ck); g.fill();
      g.fillStyle = '#fff';
      g.textAlign = 'left';
      g.fillText(text, 16 * ck, ty + 11 * ck);
      if (f.s > 0 && f.ph === 2) {
        g.textAlign = 'right';
        const left = '⌛ ' + Math.ceil(f.s * TICK / 1000) + ' с';
        g.fillStyle = 'rgba(10,16,12,.6)';
        const w2 = g.measureText(left).width + 16 * ck;
        g.beginPath(); g.roundRect(tr - w2, ty, w2, 22 * ck, 8 * ck); g.fill();
        g.fillStyle = f.s * TICK <= 5000 ? pal.danger : '#fff';
        g.fillText(left, tr - 8 * ck, ty + 11 * ck);
      }
    }
    // спалахи (коло, фініш)
    g.textAlign = 'center';
    if (st.flashes.length && now - st.flashes[0].at >= st.flashes[0].ms) st.flashes = st.flashes.filter((fl) => now - fl.at < fl.ms);
    // гонка скінчилась — таблиця результатів сама все каже, спалах «Фініш» не лізе на неї
    if (f.ph >= 3 && st.flashes.length) st.flashes.length = 0;
    let fy = H * 0.3;
    for (const fl of st.flashes) {
      const a = Math.min(1, (fl.ms - (now - fl.at)) / 300);
      g.globalAlpha = a;
      g.font = F.flash;
      const w = g.measureText(fl.text).width + 30 * ck;
      g.fillStyle = 'rgba(10,16,12,.72)';
      g.beginPath(); g.roundRect(W / 2 - w / 2, fy - fs * 1.4, w, fs * 2.8, 12 * ck); g.fill();
      g.fillStyle = fl.col || '#fff';
      g.fillText(fl.text, W / 2, fy);
      fy += fs * 3.2;
      g.globalAlpha = 1;
    }
    if (f.ph === 0) lobbyOverlay(st, g);
    if (st.view && st.view.champ && st.view.champ.n && (f.ph === 4 || f.ph === 3)) champTable(st, g);
    else if (f.ph === 3 && st.view && st.view.results) results(st, g);
    if (st.clockOn && f.ph === 2 && now - st.fAt > 700) {
      g.font = F.hud;
      g.fillStyle = 'rgba(10,16,12,.7)';
      g.beginPath(); g.roundRect(W / 2 - 60 * ck, H / 2 - 16 * ck, 120 * ck, 32 * ck, 10 * ck); g.fill();
      g.fillStyle = '#fff';
      g.fillText('зв\'язок…', W / 2, H / 2);
    }
  }

  function lobbyOverlay(st, g) {
    const W = st.pxW, ck = st.cssK, v = st.view, ctx = st.ctx;
    const fs = Math.max(12, Math.round(15 * ck));
    let text;
    if (v && v.champ) text = '🏆 Чемпіонат: ' + v.champ.of + ' трас за жеребом';
    else if (v && v.random) text = '🎲 Траса випаде на старті';
    else if (!ctx.mine) text = 'Чекаємо на гонщиків';
    else if (ctx.room && ctx.room.host && ctx.me && ctx.room.host.toLowerCase() === String(ctx.me.nick || '').toLowerCase()) text = 'Обери машину — і тисни «Почати»';
    else text = 'Обери машину, господар тисне «Почати»';
    // посеред поля, а не згори: старт на всіх трасах — на верхній прямій, і плашка закривала пол-позицію з міткою «ти»
    g.font = '700 ' + fs + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    let tw = g.measureText(text).width, f2 = fs;
    const room = W - 16 * ck - 34 * ck;
    if (tw > room) {
      // вузький канвас (телефон): шрифт менший, щоб плашка не вилазила за край
      f2 = Math.max(9, Math.floor(fs * room / tw));
      g.font = '700 ' + f2 + 'px system-ui, sans-serif';
      tw = g.measureText(text).width;
    }
    const w = tw + 34 * ck, h = f2 * 2.2, y = st.pxH / 2 - h / 2;
    g.fillStyle = 'rgba(10,16,12,.78)';
    g.beginPath(); g.roundRect(W / 2 - w / 2, y, w, h, 12 * ck); g.fill();
    g.fillStyle = '#fff';
    g.fillText(text, W / 2, y + h / 2, room);
  }

  /// Таблиця результатів не міняється, поки не прийде новий вид чи стіл: малюємо її раз в offscreen-канвас
  /// (емодзі й текст у канвасі дорогі) і далі щокадру лише копіюємо.
  function results(st, g) {
    const v = st.view, W = st.pxW, H = st.pxH, room = st.ctx && st.ctx.room;
    const rc = st.resC;
    if (!rc || rc.v !== v || rc.room !== room || rc.W !== W || rc.H !== H || rc.pal !== st.pal) {
      const rows = v.results || [], ck = st.cssK;
      const fs = Math.max(11, Math.round(14 * ck));
      const lh = fs * 1.9;
      const rec = v.records && v.records[0];
      const w = Math.min(W - 20 * ck, 460 * ck), h = lh * (rows.length + (rec ? 2.5 : 1.6));
      const x = W / 2 - w / 2, y = Math.max(8 * ck, H / 2 - h / 2);
      const x0 = Math.floor(x), y0 = Math.floor(y), cw = Math.ceil(w) + 2, chh = Math.ceil(h) + 2;
      const cv = rc && rc.cv.width === cw && rc.cv.height === chh ? rc.cv : offscreen(cw, chh);
      const c = cv.getContext('2d');
      c.setTransform(1, 0, 0, 1, 0, 0);
      c.clearRect(0, 0, cw, chh);
      c.setTransform(1, 0, 0, 1, -x0, -y0);
      c.textBaseline = 'middle';
      paintResults(st, c, rows, rec, x, y, w, h, fs, lh);
      st.resC = { cv, x: x0, y: y0, v, room, W, H, pal: st.pal };
    }
    g.drawImage(st.resC.cv, st.resC.x, st.resC.y);
  }

  /// Чемпіонат (№86): таблиця очок між гонками (ph 4, з відліком до наступної траси) і підсумок серії (ph 3).
  /// Як і results(): малюємо раз в offscreen-канвас, щокадру лише копіюємо; між гонками — перемальовка раз на секунду.
  function champTable(st, g) {
    const v = st.view, f = st.f, W = st.pxW, H = st.pxH, ch = v.champ, room = st.ctx && st.ctx.room;
    const final = f.ph === 3;
    const sec = final ? -1 : Math.max(0, Math.ceil(f.s * TICK / 1000));
    const rc = st.chC;
    if (!rc || rc.v !== v || rc.room !== room || rc.W !== W || rc.H !== H || rc.pal !== st.pal || rc.sec !== sec) {
      const rows = ch.pts || [], ck = st.cssK, pal = st.pal;
      const fs = Math.max(11, Math.round(14 * ck)), lh = fs * 1.9;
      const w = Math.min(W - 20 * ck, 440 * ck), h = lh * (rows.length + 2.9);
      const x = W / 2 - w / 2, y = Math.max(8 * ck, H / 2 - h / 2);
      const x0 = Math.floor(x), y0 = Math.floor(y), cw = Math.ceil(w) + 2, chh = Math.ceil(h) + 2;
      const cv = rc && rc.cv.width === cw && rc.cv.height === chh ? rc.cv : offscreen(cw, chh);
      const c = cv.getContext('2d');
      c.setTransform(1, 0, 0, 1, 0, 0);
      c.clearRect(0, 0, cw, chh);
      c.setTransform(1, 0, 0, 1, -x0, -y0);
      c.textBaseline = 'middle';
      c.fillStyle = 'rgba(10,16,12,.93)';
      c.beginPath(); c.roundRect(x, y, w, h, 14 * ck); c.fill();
      const res = room && room.result;
      const win = final && res && res.winners && res.winners.length ? st.ctx.nickOf(res.winners[0]) : null;
      const humans = rows.filter((r) => !r.bot).length;
      const head = !final ? '🏆 Чемпіонат · гонка ' + ch.n + ' з ' + ch.of
        : win ? '🏆 Чемпіон — ' + win : humans >= 2 ? '🏆 Чемпіонат: нічия' : '🏆 Чемпіонат позаду';
      c.textAlign = 'center';
      c.fillStyle = '#fff';
      c.font = '800 ' + Math.round(fs * 1.15) + 'px system-ui, sans-serif';
      c.fillText(fitText(c, head, w - 20 * ck), W / 2, y + lh * 0.7);
      const me = String((st.ctx.me && st.ctx.me.nick) || '').toLowerCase();
      // між гонками — місце й очки щойно минулої гонки; у підсумку серії — скільки гонок виграв
      const gainOf = (r) => (final ? (r.wins ? '🏁×' + r.wins : '') : r.last ? place(r.last) + ' +' + r.gain : '—');
      c.font = '600 ' + fs + 'px system-ui, sans-serif';
      let gw = 0;
      for (const r of rows) gw = Math.max(gw, c.measureText(gainOf(r)).width);
      c.font = '800 ' + fs + 'px system-ui, sans-serif';
      const pw = c.measureText('888').width;
      const ptsR = x + w - 14 * ck, gainR = ptsR - pw - 14 * ck, nickX = x + 16 * ck + fs * 1.7;
      const nickMax = Math.max(30 * ck, gainR - gw - 10 * ck - nickX);
      rows.forEach((r, n) => {
        const ry = y + lh * (n + 1.6);
        const mine = !r.bot && String(r.nick).toLowerCase() === me;
        c.textAlign = 'left';
        c.font = '700 ' + fs + 'px system-ui, sans-serif';
        c.fillStyle = mine ? pal.accent : '#fff';
        c.fillText(n === 0 ? '🥇' : n === 1 ? '🥈' : n === 2 ? '🥉' : (n + 1) + '.', x + 14 * ck, ry);
        c.fillText(fitText(c, ((CAR[r.car] || {}).emoji || '') + ' ' + r.nick, nickMax), nickX, ry);
        c.textAlign = 'right';
        c.font = '600 ' + fs + 'px system-ui, sans-serif';
        c.fillStyle = r.last === 1 ? '#ffe27a' : pal.muted;
        c.fillText(gainOf(r), gainR, ry);
        c.font = '800 ' + fs + 'px system-ui, sans-serif';
        c.fillStyle = mine ? pal.accent : '#fff';
        c.fillText(String(r.pts), ptsR, ry);
      });
      c.textAlign = 'center';
      c.fillStyle = pal.accent;
      c.font = '600 ' + Math.round(fs * 0.92) + 'px system-ui, sans-serif';
      const ids = ch.tracks || [];
      const emo = (id) => (TRACKS.find((t) => t.id === id) || { emoji: '🏁' }).emoji;
      let foot;
      if (!final) {
        const nx = TRACKS.find((t) => t.id === ids[ch.n]);
        foot = 'Далі — ' + (nx ? nx.emoji + ' ' + nx.title : 'наступна траса') + ' · за ' + sec + ' с';
      } else foot = ids.slice(0, ch.n).map(emo).join(' → ') + ' · очки 10/6/4/3/2/1';
      c.fillText(fitText(c, foot, w - 20 * ck), W / 2, y + lh * (rows.length + 2.1));
      st.chC = { cv, x: x0, y: y0, v, room, W, H, pal: st.pal, sec };
    }
    g.drawImage(st.chC.cv, st.chC.x, st.chC.y);
  }

  /// Текст, що не ширший за max: обрізаємо з трикрапкою (у підсумку — раз на вид, тож цикл не страшний).
  function fitText(g, text, max) {
    if (g.measureText(text).width <= max) return text;
    let t = text;
    while (t.length > 1 && g.measureText(t + '…').width > max) t = t.slice(0, -1);
    return t + '…';
  }

  /// Таблиця підсумку. Колонки рахуються від справжньої ширини текстів: на телефоні нік не лізе на час.
  /// Переможець — з часом, решта — відставанням «+0,4 с»; ⚡ — найшвидше коло гонки, ★ — рекорд траси.
  /// Соло — заїзд на час: без медалі самому собі, зате з різницею до рекорду траси.
  function paintResults(st, g, rows, rec, x, y, w, h, fs, lh) {
    const v = st.view, W = st.pxW, ck = st.cssK, pal = st.pal, L = v.laps || 3;
    const room = st.ctx && st.ctx.room;
    const solo = rows.length === 1 && !!(room && room.result && room.result.draw);
    const win = rows.find((r) => r.fin === 1);
    let fast = 0;
    for (const r of rows) if (r.best && (!fast || r.best < fast)) fast = r.best;
    if (rows.length < 2) fast = 0;
    const narrow = w < 380 * ck;
    const me = String((st.ctx && st.ctx.me && st.ctx.me.nick) || '').toLowerCase();
    g.fillStyle = 'rgba(10,16,12,.93)';
    g.beginPath(); g.roundRect(x, y, w, h, 14 * ck); g.fill();
    g.textAlign = 'center';
    g.fillStyle = '#fff';
    g.font = '800 ' + Math.round(fs * 1.15) + 'px system-ui, sans-serif';
    g.fillText(fitText(g, (solo ? '⏱ Заїзд на час · ' : '🏁 ') + ((v.track && v.track.title) || '') + ' · ' + lapsWord(L), w - 20 * ck), W / 2, y + lh * 0.7);
    const nickOf = (r) => whoOf(st, r.seat);
    const timeOf = (r) => (!r.fin ? (narrow ? '✕ ' : 'без фінішу · ') + r.laps + '/' + L
      : solo || r === win || !win ? clock(r.ms, 1) : gap(r.ms - win.ms));
    const bestOf = (r) => {
      if (!r.best) return '';
      const star = rec && rec.ms === r.best && (rec.nick || '').toLowerCase() === String(nickOf(r)).toLowerCase() ? '★' : '';
      return (narrow ? '' : '⏱ ') + clock(r.best, 2) + (r.best === fast ? '⚡' : '') + star;
    };
    // ширини колонок — за найдовшим текстом
    g.font = '600 ' + fs + 'px system-ui, sans-serif';
    let tw = 0, bw = 0;
    for (const r of rows) {
      tw = Math.max(tw, g.measureText(timeOf(r)).width);
      bw = Math.max(bw, g.measureText(bestOf(r)).width);
    }
    const bestR = x + w - 12 * ck, timeR = bestR - bw - 14 * ck, nickX = x + 16 * ck + fs * 1.7;
    const nickMax = Math.max(30 * ck, timeR - tw - 10 * ck - nickX);
    rows.forEach((r, n) => {
      const ry = y + lh * (n + 1.5);
      const nick = nickOf(r);
      const car = CAR[(v.cars && v.cars[r.seat]) || 'traktor'];
      g.textAlign = 'left';
      g.font = '700 ' + fs + 'px system-ui, sans-serif';
      g.fillStyle = pal.seats[r.seat];
      const medal = solo ? (r.fin ? '🏁' : '—') : r.fin ? (r.fin === 1 ? '🥇' : r.fin === 2 ? '🥈' : r.fin === 3 ? '🥉' : r.fin + '.') : '—';
      g.fillText(medal, x + 14 * ck, ry);
      g.fillText(fitText(g, (car ? car.emoji + ' ' : '') + nick, nickMax), nickX, ry);
      g.textAlign = 'right';
      g.font = '600 ' + fs + 'px system-ui, sans-serif';
      g.fillStyle = r.fin && (solo || r === win) ? '#fff' : r.fin ? '#e8ecd9' : pal.muted;
      g.fillText(timeOf(r), timeR, ry);
      g.fillStyle = r.best && r.best === fast ? '#ffe27a' : pal.muted;
      g.fillText(bestOf(r), bestR, ry);
    });
    // рекорд траси — одним рядком унизу (повна десятка — у лобі, щоб «Ще раз» лишався на екрані);
    // соло — ще й скільки бракує до нього (або що він твій)
    if (rec) {
      g.textAlign = 'center';
      g.fillStyle = pal.accent;
      g.font = '600 ' + Math.round(fs * 0.92) + 'px system-ui, sans-serif';
      let line = '⏱ Рекорд траси: ' + rec.nick + ' ' + clock(rec.ms, 2);
      const r0 = rows[0];
      if (solo && r0 && r0.best && st.ctx.mine) {
        if ((rec.nick || '').toLowerCase() === me && rec.ms === r0.best) line = '🏆 Рекорд траси твій: ' + clock(rec.ms, 2);
        else if (r0.best > rec.ms) {
          const d = r0.best - rec.ms;   // кола — до сотих, як і сам рекорд
          line += ' · тобі до нього ' + Math.floor(d / 1000) + ',' + String(Math.floor((d % 1000) / 10)).padStart(2, '0') + ' с';
        }
      }
      g.fillText(fitText(g, line, w - 20 * ck), W / 2, y + lh * (rows.length + 1.75));
    }
  }

  // ===============================================================================================
  // DOM: HUD над канвасом, кнопки пальця, вибір машини й рекорди під ним
  // ===============================================================================================

  function build(root, st) {
    root.classList.add('rl-body');
    st.root = root;
    const hud = document.createElement('div');
    hud.className = 'rl-hud';
    const wrap = document.createElement('div');
    wrap.className = 'rl-wrap';
    const cv = document.createElement('canvas');
    cv.className = 'gcanvas rl-cv';
    wrap.appendChild(cv);
    const touch = document.createElement('div');
    touch.className = 'rl-touch';
    touch.innerHTML = '<div class="rl-tl"><button type="button" data-k="l" aria-label="ліворуч">◀</button><button type="button" data-k="r" aria-label="праворуч">▶</button></div>'
      + '<div class="rl-tr"><button type="button" class="rl-sm" data-a="horn" aria-label="гудок">📣</button><button type="button" class="rl-sm" data-a="reset" aria-label="назад на трасу">↺</button></div>'
      + '<div class="rl-br"><button type="button" class="rl-hb" data-k="h" aria-label="ручник">🅷</button><button type="button" class="rl-brake" data-k="b" aria-label="гальмо">🛑</button><button type="button" class="rl-gas" data-k="g" aria-label="газ">⛽</button></div>';
    wrap.appendChild(touch);
    const tip = document.createElement('div');
    tip.className = 'rl-turn';
    tip.textContent = 'Поверни телефон ▭ — гонка в ландшафті';
    // ландшафт на телефоні: канвас дрібний — один раз підказати ⛶
    if (store.get('rally.fullhint', '') !== '1') {
      const full = document.createElement('button');
      full.type = 'button';
      full.className = 'rl-fullhint';
      full.textContent = '⛶ на весь екран — зручніше';
      full.onclick = () => {
        store.set('rally.fullhint', '1');
        const b = document.querySelector('.grfull');
        if (b && !document.body.classList.contains('gfull')) b.click();
        full.remove();
      };
      // не над HUD: посеред гонки сторінку прокручено до канваса, і рядок над ним ховався під липкою шапкою сайту
      wrap.appendChild(full);
    }
    const lower = document.createElement('div');
    lower.className = 'rl-lower';
    root.appendChild(hud);
    root.appendChild(tip);
    root.appendChild(wrap);
    root.appendChild(lower);
    st.hud = hud; st.wrap = wrap; st.cv = cv; st.g = cv.getContext('2d'); st.touchEl = touch; st.lower = lower;

    const down = (e) => {
      const b = e.target.closest('button');
      if (!b) return;
      e.preventDefault();
      if (b.dataset.a === 'horn') { horn(st); return; }
      if (b.dataset.a === 'reset') { resetCar(st); return; }
      const key = b.dataset.k;
      if (!key) return;
      try { b.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
      st.pointers = st.pointers || new Map();
      st.pointers.set(e.pointerId, key);
      st.touch.add(key);
      if (key === 'h' && st.sim) st.hbUntil = st.sim.T + 13;
      b.classList.add('on');
      if (st.sim) flush(st, st.sim.T + 1);
    };
    const up = (e) => {
      if (!st.pointers || !st.pointers.has(e.pointerId)) return;
      const key = st.pointers.get(e.pointerId);
      st.pointers.delete(e.pointerId);
      if (![...st.pointers.values()].includes(key)) {
        st.touch.delete(key);
        const b = touch.querySelector('[data-k="' + key + '"]');
        if (b) b.classList.remove('on');
      }
      if (st.sim) flush(st, st.sim.T + 1);
    };
    touch.addEventListener('pointerdown', down);
    touch.addEventListener('pointerup', up);
    touch.addEventListener('pointercancel', up);
    touch.addEventListener('contextmenu', (e) => e.preventDefault());

    hud.addEventListener('click', (e) => {
      const b = e.target.closest('button');
      if (!b) return;
      if (b.dataset.t === 'sound') {
        st.sound = !st.sound;
        store.set('rally.sound', st.sound ? '1' : '0');
        if (st.sound && !st.audio) st.audio = makeAudio();
        if (st.audio && st.audio.ac.state === 'suspended') st.audio.ac.resume();
        st.hudSig = '';
        paintHud(st);
      } else if (b.dataset.t === 'gas') {
        st.autogas = !st.autogas;
        store.set('rally.autogas', st.autogas ? '1' : '0');
        st.hudSig = '';
        paintHud(st);
        paintTouch(st);
        if (st.sim) flush(st, st.sim.T + 1);
      }
    });
    lower.addEventListener('toggle', (e) => { if (e.target.matches('.rl-garage')) st.garageOpen = e.target.open; }, true);
    lower.addEventListener('change', (e) => {
      if (!e.target.matches('.rl-plate input') || !st.ctx || !st.ctx.mine) return;
      const loc = myGarage();
      const v = st.view;
      const paint = loc ? loc.paint : (v && v.paint ? v.paint[st.ctx.seat] : -1);
      setGarage(st, paint, e.target.value);
    });
    lower.addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.target.matches('.rl-plate input')) e.target.blur(); });
    lower.addEventListener('click', (e) => {
      const ph = e.target.closest('button[data-photo]');
      if (ph && st.photo) { st.photo.played = false; st.photo.playAt = 0; st.lowerSig = ''; paintLower(st); wake(st, true); return; }
      if (!st.ctx || !st.ctx.mine) return;
      const pb = e.target.closest('button[data-paint]');
      if (pb) {
        const inp = lower.querySelector('.rl-plate input');
        setGarage(st, +pb.dataset.paint, inp ? inp.value : '');
        return;
      }
      const tb = e.target.closest('button[data-track]');
      if (tb) { again(st, tb.dataset.track); return; }
      const b = e.target.closest('button[data-car]');
      if (!b) return;
      const id = b.dataset.car;
      st.ctx.act('car', { car: id });
    });

    st.keyup = (e) => {
      const key = keyOf(e);
      if (!key || !st.keys.has(key)) return;
      st.keys.delete(key);
      if (st.sim) flush(st, st.sim.T + 1);
    };
    st.blur = () => release(st);
    st.vis = () => { if (document.hidden) release(st); };
    document.addEventListener('keyup', st.keyup);
    window.addEventListener('blur', st.blur);
    document.addEventListener('visibilitychange', st.vis);
    if (window.ResizeObserver) {
      st.ro = new ResizeObserver(() => { layout(st); ensureTrack(st); if (st.centred) centreCanvas(st); wake(st, true); });
      st.ro.observe(wrap);
      // ширина тіла картки (⛶ ховає балачки й розсуває стіл) від канваса не залежить — петлі нема
      st.ro2 = new ResizeObserver(() => {
        const w = root.clientWidth;
        if (w === st.rootW) return;
        st.rootW = w;
        st.fitDirty = true;
        st.fitGrow = true;
        wake(st, true);
      });
      st.ro2.observe(root);
    }
    st.onResize = () => { st.fitDirty = true; st.fitGrow = true; wake(st, true); };
    window.addEventListener('resize', st.onResize);
  }

  const phoneLandscape = () => !!(window.matchMedia && window.matchMedia('(pointer: coarse) and (orientation: landscape) and (max-height: 500px)').matches);

  /// Від низу картки столу до низу вікна (поле сторінки під карткою).
  const FIT_PAD = 18;

  /// Канвас по висоті вікна (мишка, пад, Steam Deck; не телефон — там ⛶ і centreCanvas). CSS тримає грубий
  /// резерв «100vh − 250 px», а з 4–6 гравцями рядок місць каркаса й HUD переносяться в два рядки, і на
  /// 1280×800 «Ще раз» ховався під низом екрана. Тут міряємо, що справді стоїть над канвасом (шапка сайту,
  /// заголовок, місця, HUD) і під ним (статус, кнопки; у лобі — ще вибір машини й рекорди), і підтискаємо канвас.
  /// Ці рядки — на всю ширину картки, від ширини канваса не залежать, тож петлі нема. У гонці канвас лише
  /// меншає: чіп, що перескочив на другий рядок посеред кола, не має смикати трасу туди-сюди.
  function fitHeight(st) {
    const wrap = st.wrap, table = wrap && wrap.closest('.gtable');
    let px = 0;
    if (table && !coarse() && window.innerHeight > 500) {
      const r = wrap.getBoundingClientRect(), tb = table.getBoundingClientRect();
      const above = r.top + window.scrollY, below = tb.bottom - r.bottom + FIT_PAD;
      const room = (st.root && st.root.clientWidth) || r.width;
      // у лобі канвас — лише показ траси: хай менший, аби «Почати» лишалась на екрані Деки й з шістьма
      const floor = st.view && st.view.ph === 0 ? 320 : 480;
      px = Math.max(Math.min(floor, room), Math.floor((window.innerHeight - above - below) * 16 / 9));
    }
    const ph = st.f ? st.f.ph : 0;
    const racing = ph === 1 || ph === 2;
    const same = st.fitPh === ph;
    st.fitPh = ph;
    // у гонці канвас сам лише меншає (чіп, що перескочив рядок, не смикає трасу), а ⛶ чи нове вікно — явна дія гравця
    const grow = st.fitGrow;
    st.fitGrow = false;
    if (px && st.fitPx && racing && same && px > st.fitPx && !grow) return;
    if (!!px === !!st.fitPx && Math.abs(px - (st.fitPx || 0)) < 6) return;
    st.fitPx = px;
    wrap.style.maxWidth = px ? 'min(1280px, ' + px + 'px)' : '';
  }

  /// Прокрутити сторінку так, щоб канвас став посередині видимого: під липкою шапкою сайту, над вкладками й
  /// міні-плеєром (їхні висоти — змінні сайту --tabs-h/--mini-h; у ⛶ плеєра нема).
  function centreCanvas(st) {
    try {
      const cs = getComputedStyle(document.body);
      const px = (v) => parseFloat(cs.getPropertyValue(v)) || 0;
      const head = document.querySelector('header');
      let top = head ? head.getBoundingClientRect().bottom : 0;
      let bottom = window.innerHeight - px('--tabs-h') - px('--mini-h');
      // каркас знає точніше: у g-imm шапки й меню нема, а шторка «💬 Стіл» — є чи нема
      if (HGames.ui.fit) { const f = HGames.ui.fit(); top = f.top; bottom = f.h - f.dock; }
      const r = st.wrap.getBoundingClientRect();
      window.scrollBy(0, r.top - (top + Math.max(0, (bottom - top - r.height) / 2)));
    } catch { /* без прокрутки теж можна грати */ }
  }

  /// Телефон боком: обабіч канваса по 190–240 px порожнього поля — кнопки переїздять туди й не закривають трасу
  /// (ліворуч ◀ ▶ і гудок з ↺, праворуч газ, гальмо й ручник). Бульбашка балачки столу (каркас, fixed праворуч
  /// унизу) накривала ручник і гальмо — праву купку піднімаємо над нею. Її ширина міняється з кожною реплікою,
  /// тож перевіряємо двічі на секунду з циклу малювання (лише getBoundingClientRect, без перебудови DOM).
  function sideCheck(st) {
    const root = st.root, wrap = st.wrap;
    if (!root || !wrap) return;
    const on = phoneLandscape() && st.touchEl.classList.contains('show');
    const m = on ? Math.floor((root.clientWidth - wrap.clientWidth) / 2) : 0;
    const side = on && m >= 130;
    root.classList.toggle('rl-side', side);
    if (!side) return;
    const wr = wrap.getBoundingClientRect();
    let lift = 0;
    const head = document.querySelector('.tchat.drawer:not(.open) .tc-head');
    if (head && head.offsetParent !== null) {
      const hb = head.getBoundingClientRect();
      if (hb.width && hb.left < wr.right + m && hb.right > wr.right && hb.top < wr.bottom) lift = Math.ceil(wr.bottom - hb.top + 6);
    }
    lift = Math.max(0, Math.min(lift, wr.height - 120));
    const bt = Math.max(44, Math.min(76, Math.floor((m - 28) / 2), Math.floor((wr.height - 24 - lift) / 1.9)));
    const key = m + ':' + bt + ':' + lift;
    if (key === st.sideKey) return;
    st.sideKey = key;
    wrap.style.setProperty('--rl-m', m + 'px');
    wrap.style.setProperty('--rl-bt', bt + 'px');
    wrap.style.setProperty('--rl-lift', lift + 'px');
  }

  /// Телефон боком, гонка скінчилась: «Ще раз» стоїть під нижніми вкладками й міні-плеєром — прокрутити до нього.
  function revealButtons(st) {
    try {
      const table = st.root && st.root.closest('.gtable');
      const btns = table && table.querySelector('.gbtns');
      if (!btns || !btns.firstElementChild) return;
      const cs = getComputedStyle(document.body);
      const px = (v) => parseFloat(cs.getPropertyValue(v)) || 0;
      const f = HGames.ui.fit && HGames.ui.fit();
      const bottom = (f ? f.h - f.dock : window.innerHeight - px('--tabs-h') - px('--mini-h')) - 6;
      const r = btns.getBoundingClientRect();
      if (r.bottom > bottom) window.scrollBy(0, r.bottom - bottom);
    } catch { /* без прокрутки кнопку теж видно — трохи нижче */ }
  }

  function paintTouch(st) {
    const on = st.ctx && st.ctx.mine && st.ctx.playing && st.f && (st.f.ph === 1 || st.f.ph === 2);
    st.touchEl.classList.toggle('show', !!on);
    st.touchOn = !!on && coarse();
    const racing = !!(st.f && (st.f.ph === 1 || st.f.ph === 2));
    if (st.root) {
      st.root.classList.toggle('rl-racing', racing);
      // лобі: канвас — лише показ траси, тож менший, щоб вибір машини, рекорди й «Почати» влізли в екран
      st.root.classList.toggle('rl-lobby', !!(st.view && st.view.ph === 0));
    }
    // телефон боком: канвас сам стає між шапкою сайту й тим, що прибито внизу, щойно почався відлік
    if (on && !st.centred && phoneLandscape()) {
      st.centred = true;
      centreCanvas(st);
    }
    if (!racing) st.centred = false;
    if (st.f && st.f.ph === 3 && !st.revealed && st.ctx && st.ctx.mine && phoneLandscape()) {
      st.revealed = true;
      st.revealAt = performance.now() + 250;   // каркас домальовує кнопки після update
    }
    st.touchEl.classList.toggle('auto', st.autogas);
    st.wrap.classList.toggle('rl-live', !!on);
  }

  function paintHud(st) {
    const ctx = st.ctx, f = st.f, v = st.view;
    if (!ctx || !v) return;
    let html = '';
    // у лобі ніки є в рядку місць каркаса, а машини — на решітці: другий ряд чіпів лише штовхав «Почати» за екран
    const lobby = v.ph === 0 || !f || f.ph === 0;
    const fl = field(f);
    for (let i = 0; !lobby && i < SEATS; i++) {
      const nick = ctx.nickOf(i) || (v.bots && v.bots[i]);
      const o = i * STRIDE;
      const present = f && f.c[o + 10] >= 0;
      if (!nick && !(f && f.ph >= 1 && present)) continue;
      const fin = f ? f.c[o + 10] : 0, lap = f ? f.c[o + 6] : 0, pos = f && f.r ? f.r[i] : 0;
      const car = CAR[(v.cars && v.cars[i]) || ''] || null;
      let extra = '';
      if (f && f.ph >= 1) {
        if (!present) extra = ' <span class="rl-x">✕</span>';
        else if (fin > 0) extra = (fl ? ' <b>' + place(fin) + '</b>' : '') + ' 🏁';
        else if (f.ph >= 2) extra = (fl === 2 && pos ? ' <b>' + place(pos) + '</b>' : '') + ' ' + Math.min(v.laps, lap + 1) + '/' + v.laps;
      }
      html += '<span class="rl-chip s' + i + (i === ctx.seat ? ' me' : '') + (!present && f && f.ph >= 1 ? ' out' : '') + '"><i>' + (i + 1) + '</i>'
        + (car ? car.emoji + ' ' : '') + '<span class="rl-nk">' + ctx.esc(nick || SEAT_NAMES[i]) + '</span>' + extra + '</span>';
    }
    const t = v.track || {};
    const ch = v.champ;
    html += '<span class="rl-chip rl-info">' + (ch ? '🏆 ' + (ch.n ? ch.n + '/' + ch.of + ' · ' : 'Чемпіонат · ') : '')
      + (v.random && v.ph === 0 ? (ch ? ch.of + ' трас' : '🎲 Яка випаде') : ctx.esc(t.title || '')) + ' · ' + lapsWord(v.laps || 3) + '</span>';
    const rec = v.records && v.records[0];
    if (rec && !(v.random && v.ph === 0)) html += '<span class="rl-chip rl-info">⏱ ' + ctx.esc(rec.nick) + ' ' + clock(rec.ms, 2) + '</span>';
    html += '<button type="button" class="rl-tog" data-t="sound" title="Звук (типово вимкнено — на сайті грає радіо)">' + (st.sound ? '🔈' : '🔇') + '</button>';
    if (coarse()) html += '<button type="button" class="rl-tog' + (st.autogas ? ' on' : '') + '" data-t="gas" title="Газ завжди натиснуто">⛽ автогаз</button>';
    if (html !== st.hudSig) { st.hudSig = html; st.hud.innerHTML = html; st.fitDirty = true; }
  }

  /// Що є на трасі й що воно робить — рядком під вибором машини: новачок не знає, що жовті кружки — копиці.
  const LEGEND = [
    ['H', '🌾 копиця гальмує'], ['~', '💧 калюжа — бризки й занос'], ['o', '⚫ мастило — ковзко'], ['*', '🧊 лід — ковзко'],
    ['+', '»» турбо'], ['J', '🪵 трамплін — розженись і летиш'], ['M', '🟫 багнюка — в\'язне'], ['c', '🌽 кукурудза гальмує'], ['.', '🌱 трава гальмує'],
  ];
  function legend(st) {
    const td = st.td;
    if (!td || !td.map) return '';
    if (st.legendId === td.id) return st.legendText;
    const all = td.map.join('');
    st.legendId = td.id;
    st.legendText = LEGEND.filter(([c]) => all.includes(c)).map(([, t]) => t).join(' · ');
    return st.legendText;
  }

  function paintLower(st) {
    const ctx = st.ctx, v = st.view;
    if (!ctx || !v) return;
    let html = '';
    if (v.ph === 0 && ctx.mine) {
      const mine = v.cars && v.cars[ctx.seat];
      html += '<div class="rl-pick"><div class="rl-cars">' + CARS.map((c) => '<button type="button" data-car="' + c.id + '"' + (c.id === mine ? ' class="on" aria-pressed="true"' : '')
        + '><span class="rl-emo">' + c.emoji + '</span><span>' + c.title + '</span></button>').join('') + '</div>'
        + '<div class="muted small rl-note">Фізика в усіх однакова — різняться виглядом і гудком</div>'
        + (v.random ? '' : '<div class="small rl-legend">' + ctx.esc(legend(st)) + '</div>') + '</div>';
    }
    if ((v.ph === 0 || v.ph === 3) && ctx.mine) html += garageHtml(st, v);
    if (v.ph === 3 && st.photo && st.photo.clip && st.photo.clip.t.length >= 4 && !st.photo.playAt) {
      html += '<div class="rl-photo"><button type="button" data-photo="1">📸 Фотофініш ще раз</button></div>';
    }
    if (v.ph === 3 && ctx.mine && !v.champ) {
      const cur = v.random ? 'random' : v.track && v.track.id;
      html += '<div class="rl-next"><div class="rl-rh">🗺 Наступна гонка — клацни трасу</div><div class="rl-tracks">'
        + TRACKS.map((t) => '<button type="button" data-track="' + t.id + '"' + (t.id === cur ? ' class="on"' : '') + ' title="' + t.title + '"><span class="rl-emo">' + t.emoji + '</span><span>' + t.title + '</span></button>').join('')
        + '</div></div>';
    }
    const recs = v.records || [];
    if (v.ph === 0 && !v.random) {
      const me = String((ctx.me && ctx.me.nick) || '').toLowerCase();
      html += '<div class="rl-records"><div class="rl-rh">🏁 Рекорди траси «' + ctx.esc((v.track && v.track.title) || '') + '»</div>'
        + (recs.length ? '<ol>' + recs.map((r, n) => '<li' + (String(r.nick).toLowerCase() === me ? ' class="me"' : '') + '><i>' + (n + 1) + '.</i><span class="rl-rn">' + ctx.esc(r.nick) + '</span>'
          + '<span class="rl-rc">' + ((CAR[r.car] || {}).emoji || '') + '</span><b>' + clock(r.ms, 2) + '</b></li>').join('') + '</ol>'
          : '<div class="muted small">Ще жодного кола — будь першим</div>') + '</div>';
    }
    if (html !== st.lowerSig) { st.lowerSig = html; st.lower.innerHTML = html; }
  }

  /// «🎨 Гараж»: фарба й напис на номері. Згорнутий, щоб лобі лишалось коротким; пам'ятається в браузері за ніком.
  function garageHtml(st, v) {
    const seat = st.ctx.seat;
    const loc = myGarage();
    const paint = v.ph === 3 && loc ? loc.paint : (v.paint ? v.paint[seat] : -1);
    const plate = v.ph === 3 && loc ? loc.plate : ((v.plates && v.plates[seat]) || '');
    const sw = [-1].concat(PAINTS.map((_, n) => n)).map((n) => '<button type="button" data-paint="' + n + '"' + (n === paint ? ' class="on" aria-pressed="true"' : '')
      + ' style="--rl-sw:' + (n < 0 ? st.pal.seats[seat] : PAINTS[n]) + '" aria-label="' + (n < 0 ? 'колір місця' : PAINT_NAMES[n]) + '">' + (n < 0 ? '↺' : '') + '</button>').join('');
    return '<details class="rl-garage"' + (st.garageOpen ? ' open' : '') + '><summary>🎨 Гараж: фарба й номер' + (plate ? ' · ▭' + st.ctx.esc(plate) : '') + '</summary>'
      + '<div class="rl-sw">' + sw + '</div>'
      + '<label class="rl-plate">Номер <input type="text" maxlength="6" value="' + st.ctx.esc(plate) + '" placeholder="ВЛАД77" autocomplete="off" spellcheck="false"></label>'
      + (v.ph === 3 ? '<div class="muted small">Поїде з наступної гонки</div>' : '') + '</details>';
  }

  /// Змінили гараж: у браузер, а в лобі — і на сервер (у підсумку сервер слухає вже на відліку «Ще раз»).
  function setGarage(st, paint, plate) {
    const g = { paint, plate: String(plate || '').toUpperCase().replace(/[^\p{L}\p{N} -]/gu, '').slice(0, 6).trim() };
    store.set('rally.garage', JSON.stringify(g));
    st.garageSent = '';
    syncGarage(st);
    st.lowerSig = '';
    paintLower(st);
  }

  /// Гараж із браузера — на сервер, якщо там інакше (новий стіл, «Ще раз», F5). Раз на той самий вид.
  function syncGarage(st) {
    const ctx = st.ctx, v = st.view;
    if (!ctx || !ctx.mine || !v || !(v.ph === 0 || v.ph === 1) || ctx.seat == null) return;
    const g = myGarage();
    if (!g) return;
    const srvP = v.paint ? v.paint[ctx.seat] : -1, srvN = (v.plates && v.plates[ctx.seat]) || '';
    if (srvP === g.paint && srvN === g.plate) return;
    const key = g.paint + ':' + g.plate + ':' + (ctx.room && ctx.room.round);
    if (st.garageSent === key) return;
    st.garageSent = key;
    ctx.act('garage', g);
  }

  /// Після гонки: «Ще раз» каркаса і тут же обрана траса — сервер перебудовує відлік на ній (як мапа рівнів у Вогнику).
  async function again(st, track) {
    if (st.againBusy || !st.ctx || !st.ctx.room) return;
    st.againBusy = true;
    try {
      const id = st.ctx.room.id;
      const r = await HGames.call('Rematch', id);
      if (r && r.ok && track) await HGames.call('Act', id, 'track', { track });
    } finally { st.againBusy = false; }
  }

  // ===============================================================================================
  // Кадри й види
  // ===============================================================================================

  /// «Ще раз»: усе, що жило однією гонкою, — з нуля (маска на сервері теж нульова).
  function newRound(st) {
    st.sim = null; st.others = []; st.clockOn = false; st.mountCtl = false; st.seenFin = 0; st.lateSeen = -1; st.late = [];
    st.lead = LEAD0; st.sent = 0; st.sentT = 0; st.cur = 0; st.planT.fill(-1); st.flashes = []; st.lastPh = -1; st.wrongN = 0; st.wrong = false;
    for (const d of st.drawn) d.ok = false;
    st.skidOk.fill(0);
    st.finMs = 0; st.gLapT = -1; st.gRecN = 0; st.revealed = false;
    st.photo = null;
    if (st.phRing) st.phRing.n = 0;
    if (st.skidG) st.skidG.clearRect(0, 0, WU, HU);
  }

  function takeFrame(st, f, now) {
    const was = st.f;
    if (was && (f.t < was.t || (was.ph === 3 && f.ph !== 3))) newRound(st);
    // вид несе той самий кадр, що щойно прийшов окремо (сервер шле кадр і вид одним тиком): стан оновити
    // можна, а події (коло, фініш, гудок, «світлофор») — лише раз
    const fresh = !was || f.t !== was.t || f.ph !== was.ph;
    st.prevF = was;
    st.f = f;
    st.fAt = now;
    if (f.ph === 1 || f.ph === 2) noteClock(st, f.t, now);
    if (!st.track) return;
    const ctx = st.ctx;
    st.mine = ctx && ctx.mine && ctx.seat != null && f.c[ctx.seat * STRIDE + 10] >= 0 ? ctx.seat : -1;
    st.solo = st.mine >= 0 && field(f) === 0;
    if (st.solo) loadGhost(st);
    // фаза змінилась: відлік — стерти сліди; зелене — звук і спалах; кінець — стоп симуляції
    if (f.ph !== st.lastPh) {
      if (f.ph === 1 && st.skidG) st.skidG.clearRect(0, 0, WU, HU);
      // лише справжній старт: після ⛶, F5 чи входу глядачем перший кадр береться з виду, а вид сервер шле лише
      // на подіях — він буває ще з відліку, і наступний живий кадр посеред гонки вдавав би «Руш!» ще раз
      if (f.ph === 2 && st.lastPh === 1 && f.t - S.COUNT <= 6) {
        st.greenAt = performance.now();
        sfx(st, 'green');
        if (st.solo && st.ghost) st.flashes.push({ text: '👻 Наздожени привида: ' + clock(st.ghost.ms, 2), at: now, ms: 2400, col: '#dfe8ff' });
      }
      if (f.ph === 3 || f.ph === 0 || f.ph === 4) { st.sim = null; st.others = []; }
      st.lastPh = f.ph;
    }
    if (f.ph === 1 && was && was.ph === 1 && Math.ceil(was.s / 25) !== Math.ceil(f.s / 25)) sfx(st, 'light');
    for (let i = 0; fresh && i < SEATS; i++) {
      if (!(f.c[i * STRIDE + 8] & S.EV.horn) || f.c[i * STRIDE + 10] < 0) continue;
      st.horns[i] = now;
      sfx(st, 'horn', st.view && st.view.cars && st.view.cars[i]);
    }
    if (f.ph === 1 || f.ph === 2) {
      st.raceMs = Math.max(0, (f.t - S.COUNT) * TICK);
      if (st.mine >= 0) {
        adaptLead(st, f.c, st.mine * STRIDE, now);
        reconcile(st, f, now);
        if (fresh) ownEvents(st, f, now);
        if (!st.mountCtl) {
          st.mountCtl = true;
          // F5 посеред гонки: машина не має газувати сама — «нічого не тисну» (чи що тисну) з поточного тика
          st.forceSend = true;
          flush(st, st.sim.T + 1);
        }
      }
      const rt = st.clockOn ? srvTick(st, now) + (st.mine >= 0 ? st.lead : 1) : f.t;
      resetOthers(st, f, rt);
    }
    photoRec(st, f);
    st.lastT = f.t;
  }

  function ownEvents(st, f, now) {
    const o = st.mine * STRIDE, ev = f.c[o + 8], fin = f.c[o + 10];
    if (ev & S.EV.lap && fin <= 0) {
      const ll = f.c[o + 12], bl = f.c[o + 11];
      st.flashes.push({ text: 'Коло ' + clock(ll, 2) + (ll === bl ? ' · найкраще!' : ''), at: now, ms: 1200, col: ll === bl ? '#ffe27a' : '#fff' });
      sfx(st, 'lap');
    }
    if (ev & S.EV.lap) {
      const ll = f.c[o + 12], v = st.view, recs = v && v.records;
      if (recs && !v.random && ll > 0) {
        const rec = recs[0], me = String((st.ctx.me && st.ctx.me.nick) || '').toLowerCase();
        // вид із новою десяткою може прийти й раніше за кадр — тоді рекорд уже наш і рівно цей
        if (!rec || ll < rec.ms || (ll === rec.ms && String(rec.nick).toLowerCase() === me)) {
          st.flashes.push({ text: '🏆 Новий рекорд траси! ' + clock(ll, 2), at: now, ms: 2600, col: '#ffe27a' });
        }
      }
    }
    if (fin > 0 && st.seenFin !== fin) {
      st.seenFin = fin;
      const own = st.sim && st.sim.cars[st.mine];
      st.finMs = own && own.fin > 0 && own.finishMs > 0 ? own.finishMs : st.raceMs;
      st.flashes.push({ text: field(f) ? '🏁 Фініш — ' + place(fin) + '!' : '🏁 Фініш — ' + clock(st.finMs, 1), at: now, ms: 2000, col: fin === 1 ? '#ffe27a' : '#fff' });
      sfx(st, 'finish');
    }
    // «не туди»: понад 40 кадрів поспіль ніс дивиться проти «течії» траси до наступних воріт, а ми їдемо вперед.
    // Задній хід, щоб вибратись із купи, чи штовханина боком — не рахуються.
    if (st.fields && f.ph === 2 && fin === 0) {
      const field = st.fields[f.c[o + 7]], cell = S.cellOf(f.c[o], f.c[o + 1]);
      let against = false;
      if (field && field[cell] > 0 && f.c[o + 3] > 128) {
        const x = cell % S.COLS, y = (cell / S.COLS) | 0, d = field[cell];
        // напрям униз по полю відстаней: сума кроків до сусідів, ближчих до воріт
        let fx = 0, fy = 0;
        if (x > 0 && field[cell - 1] >= 0 && field[cell - 1] < d) fx--;
        if (x < S.COLS - 1 && field[cell + 1] >= 0 && field[cell + 1] < d) fx++;
        if (y > 0 && field[cell - S.COLS] >= 0 && field[cell - S.COLS] < d) fy--;
        if (y < S.ROWS - 1 && field[cell + S.COLS] >= 0 && field[cell + S.COLS] < d) fy++;
        const a = f.c[o + 2];
        against = (fx || fy) && (fx * S.COS[a] + fy * S.SIN[a]) < -0.3 * 16384 * Math.hypot(fx, fy);
      }
      // понад 40 тиків (1,6 с) поспіль — за тиками кадрів, не за кадрами: кадр буває й через два тики
      if (!against) st.wrongN = 0;
      else if (!st.wrongN) st.wrongN = f.t;
      st.wrong = !!st.wrongN && f.t - st.wrongN >= 40;
    } else { st.wrong = false; st.wrongN = 0; }
  }

  /// Лобі чи підсумок, де вже нічого не рухається: ні частинок, ні спалахів, ні трясіння, ні гудка, ні зсуву.
  function quiet(st, now) {
    const f = st.f;
    if (!f || (f.ph !== 0 && f.ph !== 3 && f.ph !== 4) || st.fitDirty || st.revealAt) return false;
    if (st.photo && st.photo.playAt) return false;
    if (st.flashes.length || st.shakeUntil > now) return false;
    for (let i = 0; i < SEATS; i++) if (now - st.horns[i] < 400) return false;
    for (const p of st.ps.list) if (p.on) return false;
    return true;
  }

  /// rAF — лише поки є що малювати (прохід 28.09). Картка схована (інший розділ сайту, лобі ігор) чи вкладка —
  /// перевіряємо раз на 0,2 с без rAF; лобі й підсумок, де нічого не рухається, — 4 кадри на секунду, а новий вид,
  /// кадр чи розмір будять одразу (wake). Було: 60 повних кадрів траси на секунду й у підсумку (1,2 мс на Full HD),
  /// і порожні колбеки в схованій картці.
  function loop(st) {
    if (st.raf || st.idleT) return;
    const tick = (now) => {
      st.raf = 0;
      if (!st.cv || !st.cv.isConnected) return;
      const shown = !document.hidden && !!st.cv.offsetParent;
      st.away = !shown;
      if (shown) {
        if (st.fitDirty) { st.fitDirty = false; fitHeight(st); }
        if (now - (st.sideAt || 0) > 500) { st.sideAt = now; sideCheck(st); }
        if (st.revealAt && now > st.revealAt) { st.revealAt = 0; revealButtons(st); }
        draw(st, now);
        engineSound(st);
      }
      const gap = !shown ? AWAY_MS : quiet(st, now) ? QUIET_MS : 0;
      if (gap) st.idleT = setTimeout(() => { st.idleT = 0; st.raf = requestAnimationFrame(tick); }, gap);
      else st.raf = requestAnimationFrame(tick);
    };
    st.raf = requestAnimationFrame(tick);
  }

  /// Цикл дрімає — розбудити зараз (force — навіть якщо картка була схована: вид приходить і тоді, коли стіл знову
  /// на екрані, а кадри гонки в схованій картці будити не мусять — там досить перевірки раз на 0,2 с).
  function wake(st, force) {
    if (!st || !st.idleT || (st.away && !force)) return;
    clearTimeout(st.idleT);
    st.idleT = 0;
    loop(st);
  }

  /// Чемпіонат бере траси жеребом, а каркас у шапці столу підписує трасу, обрану при створенні (чіп опції «Траса»),
  /// хоч серія її й не гратиме. Опція «Траса» — перша в списку, тож її чіп, коли він є, стоїть одразу за назвою гри:
  /// ховаємо його класом на картці (клас переживає перемальовку шапки каркасом).
  function champChip(root, ctx, v) {
    const table = root.closest && root.closest('.gtable');
    if (!table) return;
    const opts = (ctx.room && ctx.room.options) || {};
    const on = !!(v && v.champ) && !!opts.track && opts.track !== 'selo';
    table.classList.toggle('rl-champtrk', on);
  }

  function apply(root, ctx) {
    const st = state(root, ctx);
    const v = ctx.view;
    champChip(root, ctx, v);
    if (!v || !v.track) return;
    const prevView = st.view;
    st.view = v;
    st.td = v.track;
    st.pal = palette(st);
    layout(st);
    ensureTrack(st);
    const now = performance.now();
    void prevView;
    if (v.f && (!st.f || v.f.t >= st.f.t || v.f.ph !== st.f.ph)) takeFrame(st, v.f, now);
    photoView(st, v);
    paintHud(st);
    // поле номера в фокусі — не перемальовувати з-під пальців
    if (!(document.activeElement && document.activeElement.matches && document.activeElement.matches('.rl-plate input'))) paintLower(st);
    syncGarage(st);
    paintTouch(st);
    // висоти над і під канвасом міряємо в наступному кадрі: каркас домальовує статус і кнопки після update
    st.fitDirty = true;
    wake(st, true);
    loop(st);
  }

  HGames.register({
    id: 'rally',
    added: '2026-09-27',
    icon: ICON,
    seatNames: SEAT_NAMES,
    seatClass: ['x', 'o', 'c', 'rl-w', 'rl-b', 'rl-p'],
    pad: {
      dirs: true,
      a: 'ArrowUp',
      x: 'Space',
      on(btn, ctx) {
        const st = ctx._rally;
        if (!st) return false;
        // Ⓨ лишаємо каркасу (він пише «Ⓨ підказки» в кожній грі), Ⓑ — щоб вийти
        if (btn === 'lb') { horn(st); return true; }
        if (btn === 'rb') { resetCar(st); return true; }
        return false;
      },
      hint: '{dpad} кермо (стік убік) · {a} газ · {x} ручник · {lb} гудок · {rb} на трасу',
    },
    news: {
      v: '2026-09-29',
      title: 'Сільське ралі: чемпіонат, Весілля й Гора, Дід Панас',
      items: [
        '🏆 Опція «Чемпіонат»: п\'ять гонок поспіль на різних трасах, очки 10/6/4/3/2/1, між гонками — таблиця, наприкінці — чемпіон',
        '💒 Нові траси: Весілля — вузька вулиця повз намет, гості перебігають дорогу; ⛰ Гора — серпантин, трамплін з уступу й кози',
        '🤖 Опція «Суперники-боти»: Дід Панас, Баба Параска й Кум Степан сідають на вільні місця до чотирьох. Черепків і рекордів їм не дають',
        '🐔 Опція «Живність»: кури, гуси, віз, гості й кози на дорозі · 📸 фініш ближче ніж 0,3 с — фотофініш уповільнено',
        '🗺 Після гонки клацни трасу — «Ще раз» поїде туди · 🎨 Гараж: своя фарба й напис на номері',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      build(root, st);
      layout(st);
    },

    update(root, ctx) {
      apply(root, ctx);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!f || !f.c) return;
      if (st.f && f.t < st.f.t && f.ph === st.f.ph) return;
      takeFrame(st, f, performance.now());
      paintHud(st);
      paintTouch(st);
      wake(st);
    },

    onKey(e, ctx) {
      const st = ctx._rally;
      if (!st || !ctx.mine || !ctx.playing || !st.f || st.f.ph === 0 || st.f.ph >= 3) return false;
      if (e.code === 'KeyH' || e.key === 'h' || e.key === 'р') { if (!e.repeat) horn(st); return true; }
      if (e.code === 'KeyR' || e.key === 'r' || e.key === 'к') { if (!e.repeat) resetCar(st); return true; }
      const key = keyOf(e);
      if (!key) return false;
      if (!st.keys.has(key)) {
        st.keys.add(key);
        if (key === 'h' && st.sim) st.hbUntil = st.sim.T + 13;
        if (st.sim) flush(st, st.sim.T + 1);
      }
      if (st.sound && !st.audio) st.audio = makeAudio();
      return true;
    },

    status(ctx) {
      const st = ctx._rally;
      const f = (st && st.f) || ctx.frame || (ctx.view && ctx.view.f);
      const v = ctx.view;
      if (!f || !v) return '';
      if (f.ph === 0) {
        if (!ctx.mine) return '';
        const host = ctx.room && ctx.me && String(ctx.room.host || '').toLowerCase() === String(ctx.me.nick || '').toLowerCase();
        const bots = ctx.room && ctx.room.options && +ctx.room.options.bots > 0 && (ctx.room.seats || []).filter(Boolean).length < 4;
        if (v.champ) return (host ? 'Обери машину й тисни «Почати»' : 'Обери машину, чекаємо господаря') + ' · 🏆 чемпіонат: ' + v.champ.of + ' гонок на різних трасах, очки 10/6/4/3/2/1';
        if (bots) return (host ? 'Обери машину й тисни «Почати»' : 'Обери машину, чекаємо господаря') + ' · 🤖 Дід Панас із кумами сядуть на вільні місця';
        return host ? 'Обери машину й тисни «Почати» — можна й самому, на час' : 'Обери машину, чекаємо господаря';
      }
      if (f.ph === 1) return 'Готуйсь…' + (v.champ && v.champ.n ? ' · 🏆 гонка ' + v.champ.n + ' з ' + v.champ.of + ': ' + ((v.track && v.track.title) || '') : '');
      if (f.ph === 4 && v.champ) {
        const nx = TRACKS.find((t) => t.id === (v.champ.tracks || [])[v.champ.n]);
        return '🏆 Гонка ' + v.champ.n + ' з ' + v.champ.of + ' позаду · далі — ' + (nx ? nx.emoji + ' ' + nx.title : 'наступна') + ' за ' + Math.max(0, Math.ceil(f.s * TICK / 1000)) + ' с';
      }
      const laps = v.laps || 3;
      if (f.ph === 2) {
        if (ctx.mine && ctx.seat != null) {
          const o = ctx.seat * STRIDE, fin = f.c[o + 10];
          const fl = field(f);
          if (fin > 0) return 'Фініш' + (fl ? ' — ' + place(fin) : '') + (f.s > 0 ? ' · решті ' + Math.ceil(f.s * TICK / 1000) + ' с' : '');
          if (fin < 0) return 'Твоєї машини на трасі нема — дивишся збоку';
          const lap = Math.min(laps, f.c[o + 6] + 1), pos = f.r ? f.r[ctx.seat] : 0;
          let s = 'Коло ' + lap + '/' + laps + (fl === 2 && pos ? ' · ' + place(pos) : '');
          // хтось уже доїхав — тому, хто ще на трасі, цей відлік потрібніший за все
          if (f.s > 0) return s + ' · ⌛ ' + Math.ceil(f.s * TICK / 1000) + ' с до кінця гонки';
          if (coarse()) s += ' · ◀ ▶ кермо, 🅷 ручник';
          else if (!document.body.classList.contains('pad-on')) s += ' · ← → кермо, ↑ газ, пробіл — ручник, R — на трасу';
          return s;
        }
        let lead = -1;
        for (let i = 0; i < SEATS; i++) if (f.r && f.r[i] === 1) lead = i;
        return lead >= 0 ? 'Дивишся збоку · веде ' + (ctx.nickOf(lead) || (v.bots && v.bots[lead]) || SEAT_NAMES[lead]) + ', коло ' + Math.min(laps, f.c[lead * STRIDE + 6] + 1) + '/' + laps : 'Дивишся збоку';
      }
      if (f.ph === 3 && v.results && v.results.length === 1 && ctx.room && ctx.room.result && ctx.room.result.draw) {
        const r = v.results[0];
        return r.fin ? 'Заїзд на час: ' + clock(r.ms, 1) + (r.best ? ' · найкраще коло ' + clock(r.best, 2) : '') : 'Заїзд на час: фінішу нема';
      }
      return '';
    },

    unmount(root) {
      const table = root.closest && root.closest('.gtable');
      if (table) table.classList.remove('rl-champtrk');
      const st = root._rally;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      clearTimeout(st.idleT);
      st.idleT = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.vis) document.removeEventListener('visibilitychange', st.vis);
      if (st.ro) st.ro.disconnect();
      if (st.ro2) st.ro2.disconnect();
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (st.audio) st.audio.close();
      root._rally = null;
    },
  });

  /// Для перевірок: показати фотофініш [хто, за ким, мс, тик] на кадрах, що лежать у кільці (після гонки).
  window.__rallyPhoto = (ph) => {
    const el = document.querySelector('.rl-body');
    const st = el && el._rally;
    if (!st || !st.view) return false;
    st.photo = null;
    photoView(st, { photo: ph, ph: 3 });
    wake(st, true);
    return !!(st.photo && st.photo.clip && st.photo.clip.t.length);
  };

  /// Для перевірок: середній і найдовший час draw за останні 300 кадрів (мс).
  window.__rallyPerf = () => {
    const el = document.querySelector('.rl-body');
    const st = el && el._rally;
    if (!st || !st.perfN) return null;
    let sum = 0, max = 0;
    for (let i = 0; i < st.perfN; i++) { sum += st.perf[i]; max = Math.max(max, st.perf[i]); }
    return { avg: +(sum / st.perfN).toFixed(3), max: +max.toFixed(3), n: st.perfN, lead: st.lead, P: +st.P.toFixed(2) };
  };
})();
