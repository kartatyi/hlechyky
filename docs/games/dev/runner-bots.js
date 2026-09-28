/*
  Стрибозаври / Лелеки: боти для живої перевірки в headless Chrome (cdp2.py) — окреме з'єднання SignalR кожному.
  Бот сідає за стіл і раз на кілька кадрів «стрибає»/«змахує» вводом { s, k } на крок останнього кадру; вибулий бот-дух
  (опція «spirit») кидає брили Act('drop') щойно може.

    const b = await rb.bot('Бот1', { flap: true }); await b.join(roomId);   // лелека: змах, коли падає нижче 140 px
    const h = await HGames.call('CreateRoom', 'dino', { spirit: 'on' });     // стіл людини у вкладці
*/
window.sleep = window.sleep || ((ms) => new Promise((r) => setTimeout(r, ms)));
window.rb = window.rb || {};
(() => {
  const RS = String.fromCharCode(30);
  rb.bots = rb.bots || [];
  rb.bot = async (nick, opts) => {
    const ws = new WebSocket((location.protocol === 'https:' ? 'wss' : 'ws') + '://' + location.host + '/hub?nick=' + encodeURIComponent(nick));
    const bot = { nick, ws, room: null, seat: null, view: null, inv: 0, pend: {}, frames: 0, maxFrame: 0, keys: 0, drops: 0, opts: opts || {} };
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
          if (rv && rv.room && rv.room.id === bot.room) { bot.view = rv.view; bot.seat = rv.seat; }
        } else if (m.type === 1 && m.target === 'frame') {
          const f = m.arguments[0];
          if (f && f.id === bot.room) { bot.frames++; bot.maxFrame = Math.max(bot.maxFrame, JSON.stringify(f.f).length); bot.onFrame(f.f); }
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
    bot.close = () => { clearInterval(bot.ping); try { ws.close(); } catch { /* уже */ } };
    bot.onFrame = (f) => {
      if (!f || f.ph !== 'run' || bot.seat == null) return;
      if (f.ky) bot.keys++;
      const w = f.p && f.p[bot.seat];
      if (!w) return;
      if (bot.opts.flap) {
        // лелека: [y, vy, mode, …]; тримаємось між 110 і 170 px
        if (w[2] !== 4 && w[0] < (bot.opts.low || 140) * 16 && w[1] <= 0) bot.send('Input', bot.room, 'in', { s: f.s, k: 5 });
        return;
      }
      if (w[3] === 4) {                      // вибув — дух лавини
        if (bot.opts.spirit && (!bot.dropAt || Date.now() - bot.dropAt > 9500)) {
          bot.dropAt = Date.now();
          bot.call('Act', bot.room, 'drop', {}).then((r) => { if (r && r.ok) bot.drops++; else bot.dropAt = Date.now() - 8000; });
        }
        return;
      }
      if (Math.random() < (bot.opts.jump || 0.12)) bot.send('Input', bot.room, 'in', { s: f.s, k: Math.random() < 0.5 ? 5 : 0 });
    };
    rb.bots.push(bot);
    return bot;
  };
  rb.dismiss = async () => {
    for (let k = 0; k < 4; k++) {
      const b = [...document.querySelectorAll('button')].find((x) => /Зрозуміло|Не зараз/.test(x.textContent) && x.offsetParent);
      if (!b) break;
      b.click();
      await sleep(300);
    }
  };
})();
