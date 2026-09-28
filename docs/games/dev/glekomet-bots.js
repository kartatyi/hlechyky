/*
  Глекомети: помічники для живої перевірки в headless Chrome (cdp2.py) або з консолі браузера.
  Боти сидять на сирому WebSocket SignalR (JSON) — окреме з'єднання кожному, тож на шістьох вистачає одного
  Chrome: людина у вкладці + п'ятеро ботів. Планувальник пострілу — та сама фізика, що в GlekometCore
  (G 240, 4.8·p, вітер 10·k, крок 5 мс), зі шумом «skill» у силі, щоб промахувались по-людськи.

    const b = await gk.bot('Ганнуся', { skill: 10, mover: true }); await b.join(roomId);  // бот сів і грає сам
    gk.autopilot(true, { skill: 8 });   // за людину в цій вкладці: цілиться стрілками, стріляє Enter-ом
    await gk.waitPhase('over', 170000); gk.perf();  // середній і найдовший draw за останні 300 кадрів

  Запуск із cdp2.py: склеїти цей файл із кроком перевірки в один .js і віддати в --js, напр.
    cat docs/games/dev/glekomet-bots.js step.js > run.js
    python D:/or-wt/_tools/cdp2.py --port 9661 --url "http://127.0.0.1:8225/?cb=1#games" --nick Оля --js run.js --shot x.png
*/
window.sleep = window.sleep || ((ms) => new Promise((r) => setTimeout(r, ms)));
window.gk = window.gk || {};
const RS = String.fromCharCode(30);

gk.dismiss = async () => {
  for (let k = 0; k < 4; k++) {
    const b = [...document.querySelectorAll('button')].find((x) => /Зрозуміло|Не зараз/.test(x.textContent) && x.offsetParent);
    if (!b) break;
    b.click();
    await sleep(300);
  }
};
gk.st = () => { const c = document.querySelector('.gk-canvas'); return c && c.parentElement._gk; };
gk.room = () => localStorage.getItem('gkRoom');

gk.key = (code, o) => {
  const keyOf = { Space: ' ', Enter: 'Enter', ArrowLeft: 'ArrowLeft', ArrowRight: 'ArrowRight', ArrowUp: 'ArrowUp', ArrowDown: 'ArrowDown' };
  const key = keyOf[code] || (code.startsWith('Key') ? code.slice(3).toLowerCase() : code.startsWith('Digit') ? code.slice(5) : code);
  const opt = { code, key, bubbles: true, cancelable: true, shiftKey: !!(o && o.shift) };
  if (!o || !o.upOnly) document.dispatchEvent(new KeyboardEvent('keydown', opt));
  if (!o || !o.downOnly) document.dispatchEvent(new KeyboardEvent('keyup', opt));
};

// ---------- фізика (копія GlekometCore: G 240, 4.8·p, вітер 10·k, крок 5 мс) ----------
gk.sim = (v, seat, a, p, w, skip) => {
  const hut = v.huts[seat];
  const windK = w === 2 ? 0.4 : 1;
  let x = hut.x, y = hut.y + 40;
  const r = (a * Math.PI) / 180;
  let vx = 4.8 * p * Math.cos(r), vy = 4.8 * p * Math.sin(r);
  const ax = v.wind * 10 * windK;
  for (let s = 0; s < 1600; s++) {
    vx += ax * 0.005; vy -= 240 * 0.005; x += vx * 0.005; y += vy * 0.005;
    if (x < 0 || x > 1000) return { x: x < 0 ? -50 : 1050, y, out: true };
    if (y < v.water) return { x, y, splash: true };
    for (const h of v.huts) {
      if (!h.alive || h.seat === skip) continue;
      if (x >= h.x - 20 && x <= h.x + 20 && y >= h.y && y <= h.y + 34) return { x, y, hut: h.seat };
    }
    const c = Math.max(0, Math.min(249, Math.floor(x / 4)));
    if (y <= v.h[c]) return { x, y };
  }
  return { x, y, cloud: true };
};

gk.plan = (v, seat, skill) => {
  const me = v.huts[seat];
  const foes = v.huts.filter((h) => h.alive && h.seat !== seat && !(v.teams && h.seat % 2 === seat % 2));
  if (!foes.length) return { a: 90, p: 5, w: 0 };
  foes.sort((a, b) => Math.abs(a.x - me.x) - Math.abs(b.x - me.x));
  const t = foes[Math.random() < 0.7 ? 0 : Math.floor(Math.random() * foes.length)];
  const inv = v.inv[seat];
  const r = Math.random();
  let w = 0;
  if (inv[1] > 0 && r < 0.2) w = 1;
  else if (inv[2] > 0 && r < 0.32) w = 2;
  else if (inv[5] > 0 && r < 0.44) w = 5;
  else if (inv[3] > 0 && r < 0.5) w = 3;
  else if (inv[4] > 0 && r < 0.55) w = 4;
  else if (inv[6] > 0 && r < 0.63) w = 6;          // приколи (прохід №3), якщо комора їх має
  else if (inv[7] > 0 && r < 0.69) w = 7;
  else if (inv[8] > 0 && r < 0.74) w = 8;
  else if (inv[9] > 0 && r < 0.78) w = 9;
  const right = t.x > me.x;
  let best = null;
  for (let a0 = 30; a0 <= 80; a0 += 5) {
    const a = right ? a0 : 180 - a0;
    for (let p = 20; p <= 100; p += 2) {
      const end = gk.sim(v, seat, a, p, w === 4 || w >= 6 ? 0 : w, seat);
      const miss = end.hut === t.seat ? 0 : Math.abs(end.x - t.x) + (end.out ? 500 : 0) + (end.hut === seat ? 800 : 0);
      if (!best || miss < best.miss) best = { a, p, w, miss };
    }
  }
  const noise = skill == null ? 4 : skill;
  best.p = Math.max(5, Math.min(100, Math.round(best.p + (Math.random() - 0.5) * 2 * noise)));
  if (w === 4) { best.a = right ? 60 : 120; best.p = 45 + Math.floor(Math.random() * 20); }
  return best;
};

// ---------- бот: окреме з'єднання з хабом ----------
gk.bots = gk.bots || [];
gk.bot = async (nick, opts) => {
  const ws = new WebSocket((location.protocol === 'https:' ? 'wss' : 'ws') + '://' + location.host + '/hub?nick=' + encodeURIComponent(nick));
  const bot = { nick, ws, room: null, seat: null, view: null, inv: 0, pend: {}, frames: 0, bytes: 0, maxFrame: 0, acts: [], opts: opts || {} };
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
  ws.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
  ws.onmessage = (e) => {
    for (const part of String(e.data).split(RS)) {
      if (!part) continue;
      let m;
      try { m = JSON.parse(part); } catch { continue; }
      if (m.type === 3) { const p = bot.pend[m.invocationId]; if (p) { delete bot.pend[m.invocationId]; p(m.result || { ok: false, message: m.error }); } }
      else if (m.type === 1 && m.target === 'room') {
        const rv = m.arguments[0];
        if (rv && rv.room && rv.room.id === bot.room) { bot.rv = rv; bot.view = rv.view; bot.seat = rv.seat; bot.think(); }
      } else if (m.type === 1 && m.target === 'frame') {
        const f = m.arguments[0];
        if (f && f.id === bot.room) { bot.frames++; const n = JSON.stringify(f.f).length; bot.bytes += n; bot.maxFrame = Math.max(bot.maxFrame, n); }
      }
    }
  };
  bot.ping = setInterval(() => { try { ws.send('{"type":6}' + RS); } catch { /* закрито */ } }, 8000);
  bot.call = (target, ...args) => new Promise((res) => {
    const id = String(++bot.inv);
    bot.pend[id] = res;
    ws.send(JSON.stringify({ type: 1, invocationId: id, target, arguments: args }) + RS);
  });
  bot.send = (target, ...args) => ws.send(JSON.stringify({ type: 1, target, arguments: args }) + RS);
  bot.join = async (room) => { bot.room = room; const r = await bot.call('JoinRoom', room); bot.send('WatchRoom', room); return r; };
  bot.watch = (room) => { bot.room = room; bot.send('WatchRoom', room); };
  bot.close = () => { clearInterval(bot.ping); try { ws.close(); } catch { /* уже */ } };
  bot.think = () => {
    const v = bot.view;
    const mine = v && (v.mode === 'volley' ? v.huts[bot.seat] && v.huts[bot.seat].alive && !(v.ready || [])[bot.seat] : v.turn === bot.seat);
    if (bot.opts.idle || !v || v.phase !== 'aim' || !mine || bot.turnNo === v.turnNo) return;
    bot.turnNo = v.turnNo;
    const turnNo = v.turnNo;
    setTimeout(async () => {
      // за паузу бот міг устати з-за столу або хід міг згоріти — тоді мовчимо
      if (bot.opts.idle || bot.seat == null || !bot.view || bot.view.phase !== 'aim' || bot.view.turnNo !== turnNo) return;
      const plan = gk.plan(bot.view, bot.seat, bot.opts.skill);
      if (bot.opts.mover && Math.random() < 0.4) {
        const r = await bot.call('Act', bot.room, 'move', { dir: Math.random() < 0.5 ? -1 : 1 });
        bot.acts.push(['move', r.ok, r.message]);
        await sleep(250);
      }
      // «цілиться» на очах у всіх: кілька косметичних прицілів
      for (let k = 1; k <= 3; k++) { bot.send('Input', bot.room, 'aim', { a: Math.round(plan.a * k / 3 + 45 * (3 - k) / 3), p: Math.round(plan.p * k / 3 + 60 * (3 - k) / 3), w: plan.w }); await sleep(120); }
      const r = await bot.call('Act', bot.room, 'fire', { a: plan.a, p: plan.p, w: plan.w });
      bot.acts.push(['fire', r.ok, r.message, plan.a, plan.p, plan.w]);
    }, 500 + Math.random() * 700);
  };
  gk.bots.push(bot);
  return bot;
};

// ---------- автопілот людини в цій вкладці: цілиться стрілками, стріляє Enter-ом ----------
gk.autopilot = (on, opts) => {
  if (gk.ap) { clearInterval(gk.ap); gk.ap = 0; }
  if (!on) return;
  gk.apLog = gk.apLog || [];
  gk.ap = setInterval(async () => {
    const st = gk.st();
    const myAim = () => st.volley ? st.huts[st.ctx.seat].alive && !st.ready[st.ctx.seat] : st.turn === st.ctx.seat;
    if (!st || gk.apBusy || !st.view || !st.ctx || !st.ctx.mine || st.phase !== 'aim' || !myAim() || st.fired === st.turnNo || st.myTurnNo !== st.turnNo) return;
    gk.apBusy = true;
    const turnNo = st.turnNo;
    // хід міг згоріти чи партія скінчитись посеред прицілювання — тоді кидаємо, а не крутимо стрілки вічно
    // (раніше автопілот так відкрутив кут Олі до 180° у наступній партії, і вона прогавила хід)
    const still = () => st.phase === 'aim' && myAim() && st.turnNo === turnNo && st.fired !== turnNo;
    try {
      await sleep(700);                              // перші 0,6 с ходу Enter не стріляє (TURN_GRACE)
      if (!still()) return;
      const plan = gk.plan(st.view, st.ctx.seat, opts && opts.skill);
      const cur = st.my;
      if (plan.w !== cur.w) { gk.key('Digit' + ((plan.w + 1) % 10)); await sleep(60); }
      let da = plan.a - st.my.a, guard = 0;
      while (Math.abs(da) >= 5 && still() && guard++ < 200) { gk.key(da > 0 ? 'ArrowLeft' : 'ArrowRight', { shift: true }); await sleep(25); da = plan.a - st.my.a; }
      while (da !== 0 && still() && guard++ < 400) { gk.key(da > 0 ? 'ArrowLeft' : 'ArrowRight'); await sleep(25); da = plan.a - st.my.a; }
      let dp = plan.p - st.my.p;
      while (Math.abs(dp) >= 5 && still() && guard++ < 600) { gk.key(dp > 0 ? 'ArrowUp' : 'ArrowDown', { shift: true }); await sleep(25); dp = plan.p - st.my.p; }
      while (dp !== 0 && still() && guard++ < 800) { gk.key(dp > 0 ? 'ArrowUp' : 'ArrowDown'); await sleep(25); dp = plan.p - st.my.p; }
      if (!still()) return;
      await sleep(250);
      if (!still()) return;
      gk.key('Enter');
      gk.apLog.push([st.turnNo, plan.a, plan.p, plan.w, st.my.a, st.my.p, st.my.w]);
    } finally { gk.apBusy = false; }
  }, 250);
};

gk.waitPhase = async (want, ms) => {
  const t0 = Date.now();
  while (Date.now() - t0 < (ms || 30000)) {
    const st = gk.st();
    if (st && (typeof want === 'function' ? want(st) : st.phase === want)) return true;
    await sleep(60);
  }
  return false;
};
gk.perf = () => {
  const st = gk.st();
  const n = Math.min(st.perfN, st.perf.length);
  let s = 0, mx = 0;
  for (let i = 0; i < n; i++) { s += st.perf[i]; mx = Math.max(mx, st.perf[i]); }
  return { frames: st.perfN, avg: +(s / Math.max(1, n)).toFixed(3), max: +mx.toFixed(2) };
};
