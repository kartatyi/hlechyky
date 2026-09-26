// Сільське ралі: боти для живої перевірки в headless Chrome (cdp2.py --js). Ставить window.RallyBots:
//   const bot = await RallyBots.bot('Бот1');           // окреме з'єднання SignalR зі своїм ніком (/hub?nick=…)
//   await bot.join(roomId);                           // сісти за стіл і дивитись його кадри
//   bot.drive = true;                                 // автопілот: поле відстаней до воріт, як RallyPilot у тестах
//   await bot.call('Rematch', roomId); bot.close();
//   RallyBots.keys(true)                              // автопілот для СВОЄЇ машини сторінки — справжніми KeyboardEvent
// Пілот — чесний водій, не чемпіон: кермує на точку за кілька клітинок уперед уздовж поля, гальмує перед
// крутими поворотами, застряг — здає назад.
(() => {
  const RS = '\x1e';
  const S = () => window.RallySim;

  function field(tr, g) {
    const COLS = 48, N = COLS * 27;
    const open = (x, y) => { const k = tr.codeAt(x, y); return !S().isWall(k) && k !== S().HAY; };
    const pen = (x, y) => {
      const k = tr.codeAt(x, y);
      let p = k === S().GRASS || k === S().CORN ? 14 : k === S().MUD ? 30 : k === S().OIL ? 8 : 0;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) if (!open(x + dx, y + dy)) return p + 10;
      return p;
    };
    const dist = new Int32Array(N).fill(1e9), next = new Int32Array(N).fill(-1);
    const heap = [];
    const push = (c, d) => { heap.push([d, c]); let i = heap.length - 1; while (i > 0) { const p = (i - 1) >> 1; if (heap[p][0] <= heap[i][0]) break; [heap[p], heap[i]] = [heap[i], heap[p]]; i = p; } };
    const pop = () => { const top = heap[0], last = heap.pop(); if (heap.length) { heap[0] = last; let i = 0; for (;;) { const l = i * 2 + 1, r = l + 1; let m = i; if (l < heap.length && heap[l][0] < heap[m][0]) m = l; if (r < heap.length && heap[r][0] < heap[m][0]) m = r; if (m === i) break; [heap[m], heap[i]] = [heap[i], heap[m]]; i = m; } } return top; };
    for (let c = 0; c < N; c++) if (tr.gateAt[c] === g && open(c % COLS, (c / COLS) | 0)) { dist[c] = 0; push(c, 0); }
    while (heap.length) {
      const [d, c] = pop();
      if (d !== dist[c]) continue;
      const x = c % COLS, y = (c / COLS) | 0;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        if (!dx && !dy) continue;
        const nx = x + dx, ny = y + dy;
        if (!open(nx, ny)) continue;
        if (dx && dy && (!open(x + dx, y) || !open(x, y + dy))) continue;
        const n = ny * COLS + nx, nd = d + (dx && dy ? 14 : 10) + pen(nx, ny);
        if (nd >= dist[n]) continue;
        dist[n] = nd; next[n] = c; push(n, nd);
      }
    }
    return { dist, next };
  }

  function pilot(track) {
    const tr = S().buildTrack(track);
    const fields = [];
    for (let g = 0; g < tr.K; g++) fields.push(field(tr, g));
    const mem = {};
    return (seat, c, T) => {
      // c: { x, y, a, vf, next, ghost, stall }
      const m = mem[seat] || (mem[seat] = { stuck: 0, back: 0, gate: 1 });
      const speed = Math.abs(c.vf);
      if (m.back > 0) { m.back--; return 8 | (m.back % 20 < 10 ? 1 : 2); }
      if (speed < 40 && !c.stall && T > 80) { if (++m.stuck > 18) { m.stuck = 0; m.back = 14; } } else m.stuck = 0;
      let cell = S().cellOf(c.x, c.y);
      if (!c.ghost) m.gate = c.next; else if (fields[m.gate].dist[cell] === 0) m.gate = (m.gate + 1) % tr.K;
      let g = m.gate;
      const ahead = 2 + ((speed / 280) | 0);
      for (let i = 0; i < ahead; i++) {
        if (fields[g].dist[cell] === 0) g = (g + 1) % tr.K;
        const n = fields[g].next[cell];
        if (n < 0) break;
        cell = n;
      }
      const tx = (cell % 48) * 2048 + 1024, ty = ((cell / 48) | 0) * 2048 + 1024;
      const want = Math.round(Math.atan2(ty - c.y, tx - c.x) / (2 * Math.PI) * 1024) & 1023;
      const diff = ((want - c.a + 512) & 1023) - 512;
      let mask = diff > 10 ? 2 : diff < -10 ? 1 : 0;
      const turn = Math.abs(diff);
      if (turn < 110 || speed < 300) mask |= 4;
      else if (turn > 170 && speed > 520) mask |= 8;
      return mask;
    };
  }

  const fromFrame = (f, seat) => {
    const o = seat * 15;
    return { x: f.c[o], y: f.c[o + 1], a: f.c[o + 2], vf: f.c[o + 3], next: f.c[o + 7], ghost: f.c[o + 10] > 0, stall: (f.c[o + 9] >> 9) & 31, present: f.c[o + 10] >= 0 };
  };

  async function bot(nick) {
    const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/hub?nick=' + encodeURIComponent(nick);
    const ws = new WebSocket(url);
    let inv = 0;
    const waits = new Map();
    const b = { nick, ws, room: null, seat: null, view: null, f: null, drive: false, sent: -1, frames: 0, laps: 0, pilot: null, log: [], lead: 2 };
    b.send = (target, args) => ws.send(JSON.stringify({ type: 1, target, arguments: args }) + RS);
    b.call = (target, ...args) => new Promise((resolve) => {
      const id = String(++inv);
      waits.set(id, resolve);
      ws.send(JSON.stringify({ type: 1, target, arguments: args, invocationId: id }) + RS);
      setTimeout(() => { if (waits.has(id)) { waits.delete(id); resolve({ ok: false, message: 'тайм-аут' }); } }, 8000);
    });
    ws.onmessage = (e) => {
      for (const rec of String(e.data).split(RS)) {
        if (!rec) continue;
        let m;
        try { m = JSON.parse(rec); } catch { continue; }
        if (m.type === 3 && waits.has(m.invocationId)) { const r = waits.get(m.invocationId); waits.delete(m.invocationId); r(m.result || { ok: false, message: m.error }); }
        else if (m.type === 1 && m.target === 'room') {
          const rv = m.arguments[0];
          if (!rv || !rv.room || rv.room.id !== b.room) continue;
          b.view = rv.view; b.seat = rv.seat; b.status = rv.room.status; b.roomInfo = rv.room;
          if (b.view && b.view.track && (!b.pilot || b.pilotTrack !== b.view.track.id)) { b.pilot = pilot(b.view.track); b.pilotTrack = b.view.track.id; }
          if (rv.view && rv.view.f && (!b.f || rv.view.f.t < b.f.t)) b.sent = -1;
        } else if (m.type === 1 && m.target === 'frame') {
          const fr = m.arguments[0];
          if (!fr || fr.id !== b.room) continue;
          b.f = fr.f;
          b.frames++;
          if (b.drive && b.seat != null && b.pilot && (b.f.ph === 1 || b.f.ph === 2)) {
            const c = fromFrame(b.f, b.seat);
            if (!c.present) continue;
            if (b.f.c[b.seat * 15 + 8] & 4) b.laps++;
            let k = b.f.ph === 1 ? 4 : b.pilot(b.seat, c, b.f.t);
            if (b.noise && Math.random() < b.noise) k ^= 1 << ((Math.random() * 3) | 0);
            if (k !== b.sent) { b.sent = k; b.send('Input', [b.room, 'ctl', { t: b.f.t + b.lead, k }]); }
          }
        } else if (m.type === 6) ws.send('{"type":6}' + RS);
      }
    };
    await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = () => reject(new Error('ws ' + nick)); });
    ws.send('{"protocol":"json","version":1}' + RS);
    await new Promise((r) => setTimeout(r, 150));
    b.join = async (roomId) => {
      b.room = roomId;
      const r = await b.call('JoinRoom', roomId);
      b.send('WatchRoom', [roomId]);
      return r;
    };
    b.watch = (roomId) => { b.room = roomId; b.send('WatchRoom', [roomId]); };
    b.close = () => { try { ws.close(); } catch { /* уже */ } };
    b.ping = setInterval(() => { try { ws.send('{"type":6}' + RS); } catch { /* закрито */ } }, 10000);
    return b;
  }

  // ---------- автопілот своєї машини сторінки: натискає справжні клавіші ----------
  const CODES = { 1: 'ArrowLeft', 2: 'ArrowRight', 4: 'ArrowUp', 8: 'ArrowDown', 16: 'Space' };
  let keysTimer = 0, held = 0, pagePilot = null;
  function press(code, down) {
    document.dispatchEvent(new KeyboardEvent(down ? 'keydown' : 'keyup', { code, key: code === 'Space' ? ' ' : code, bubbles: true, cancelable: true }));
  }
  function keys(on, opts) {
    clearInterval(keysTimer);
    for (const bit of [1, 2, 4, 8, 16]) if (held & bit) press(CODES[bit], false);
    held = 0;
    if (!on) return;
    const o = opts || {};
    keysTimer = setInterval(() => {
      const el = document.querySelector('.rl-body');
      const st = el && el._rally;
      if (!st || !st.sim || st.mine < 0 || !st.f || st.f.ph !== 2) return;
      if (!pagePilot || pagePilot.track !== st.td.id) pagePilot = { track: st.td.id, fn: pilot(st.td) };
      const car = st.sim.cars[st.mine];
      let k = pagePilot.fn(st.mine, { x: car.x, y: car.y, a: car.a, vf: car.vf, next: st.f.c[st.mine * 15 + 7], ghost: car.ghost, stall: car.stall }, st.sim.T);
      if (o.drift && (k & 3) && Math.abs(car.vf) > 500 && Math.random() < o.drift) k |= 16;
      for (const bit of [1, 2, 4, 8, 16]) {
        const want = (k & bit) !== 0, have = (held & bit) !== 0;
        if (want !== have) { press(CODES[bit], want); held ^= bit; }
      }
    }, o.every || 30);
  }

  window.RallyBots = { bot, pilot, keys, fromFrame };
})();
