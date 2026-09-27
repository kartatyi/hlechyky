// «Вогник і Крапля»: бот-гравець для перевірок у headless Chrome (docs/games/dev/vohnyk-bot.py підставляє сюди
// window.__sols — записані проходження з data/vohnyk/levels — і window.__cfg). Грає через справжній шлях модуля:
// гачок st.botK віддає «натиснуті клавіші» на кожен крок, а модуль сам вирішує, що й коли слати, передбачає й звіряє.
// Ролі: host створює стіл (або бере --room) і тисне «Почати»/«Ще раз», guest сідає за --room; --solo — сам за двох;
// extra: leave — встати посеред рівня й лишитись глядачем, f5 — перезавантажити сторінку посеред рівня, short — 16 с і стоп.
window.kd = (code) => document.dispatchEvent(new KeyboardEvent('keydown', { code, key: code, bubbles: true }));
window.ku = (code) => document.dispatchEvent(new KeyboardEvent('keyup', { code, key: code, bubbles: true }));
window.btnT = (re) => [...document.querySelectorAll('button')].find(b => re.test(b.textContent.trim()));
window.clickT = (re) => { const b = btnT(re); if (b) b.click(); return !!b; };
window.vst = () => window.__vohnyk && window.__vohnyk.st();
window.leaveAll = async (skip) => {
  const ids = new Set([...document.querySelectorAll('[data-room]')].map((e) => e.dataset.room));
  const old = localStorage.getItem('vbotRoom');
  if (old && old !== 'null') ids.add(old);
  const out = [];
  for (const id of ids) { if (id === skip) continue; try { const r = await HGames.call('LeaveRoom', id); if (r.ok) out.push(id); } catch (_) { /* нема */ } await sleep(130); }
  await sleep(1100);
  return out;
};

// Бот «Вогника і Краплі»: сідає за стіл (host створює, guest сідає за window.__cfg.room), грає рівні записаними
// проходженнями — шле Input зі своїм героєм рівно тоді, коли настає крок (оцінка кроку сервера — з кадрів модуля).
const cfg = window.__cfg;
const log = [];
const st = () => vst();
const until = async (fn, ms, step = 50) => {
  const t0 = performance.now();
  while (performance.now() - t0 < ms) { try { if (fn()) return true; } catch (_) { /* ще не готове */ } await sleep(step); }
  return false;
};
async function feed(roomId, lv) {
  // клавіші — з записаного проходження, крок у крок: модуль сам вирішує, коли й що слати, і передбачає
  const sol = window.__sols[lv].slice().sort((a, b) => a[0] - b[0]);
  const x = st();
  x.botK = (c, s) => {
    let k = 0;
    for (const e of sol) { if (e[0] + 100 > s) break; if (e[1] === c) k = e[2]; }
    return k;
  };
  const t0 = performance.now();
  while (st().ctx.room.status === 'playing' && performance.now() - t0 < 100000) {
    await sleep(50);
    if (cfg.extra === 'f5' && performance.now() - t0 > 5000) { location.reload(); await sleep(60000); }
    if (cfg.extra === 'short' && performance.now() - t0 > 16000) { st().botK = null; return { short: true, status: (q('.gstatus') || {}).textContent }; }
    if (cfg.extra === 'leave' && performance.now() - t0 > 7000) {
      const r = await HGames.call('LeaveRoom', roomId);
      st().botK = null;
      await sleep(4000);   // лишаємось на сторінці столу глядачем
      const s2 = st();
      return { left: r.ok, watcher: s2.ctx.seat == null, status: (q('.gstatus') || {}).textContent, solo: s2.ctx.view.solo,
        fire: s2.world.X[0], water: s2.world.X[1], lastN: s2.lastN, t: s2.t };
    }
  }
  if (st()) st().botK = null;
  return { of: sol.length };
}
clickT(/^Не зараз$/);
clickT(/^Зрозуміло/);
await sleep(300);
// зі столу минулого запуску — встати (нік тримає лише одне місце)
if (!cfg.room || cfg.role === 'guest') log.push('left ' + (await leaveAll(cfg.room)).join(','));
let roomId = cfg.room;
if (cfg.role === 'host' && !roomId) {
  const r = await HGames.call('CreateRoom', 'vohnyk', {});
  roomId = r.roomId;
  log.push('create ' + r.ok + ' ' + roomId + ' ' + r.message);
  if (!r.ok) return { log };
} else if (cfg.role === 'guest' && cfg.extra !== 'seated') {
  const r = await HGames.call('JoinRoom', roomId);
  log.push('join ' + r.ok + ' ' + r.message);
}
window.__room = roomId;
localStorage.setItem('vbotRoom', roomId);
location.hash = '#games/room/' + roomId;
await until(() => st() && st().world, 10000);
await sleep(400);
clickT(/^Зрозуміло/);
clickT(/^Не зараз$/);
if (cfg.role === 'host' && !cfg.solo) { const g = await until(() => st().ctx.room.seats.filter((s) => s.nick).length === 2, 40000); log.push('guest ' + g); if (!g) return { log }; }
const results = [];
for (let i = 0; i < cfg.levels.length; i++) {
  const lv = cfg.levels[i];
  if (cfg.role === 'host') {
    if (i === 0) {
      if (lv !== st().ctx.view.picked) { const r = await HGames.call('Act', roomId, 'pick', { level: lv }); log.push('pick ' + lv + ' ' + r.ok + ' ' + r.message); }
      await sleep(400);
      const r = await HGames.call('StartRoom', roomId);
      log.push('start ' + r.ok + ' ' + r.message);
    } else {
      await sleep(800);
      const r = await HGames.call('Rematch', roomId);
      log.push('rematch ' + r.ok + ' ' + r.message);
    }
  }
  const ok = await until(() => st().ctx.room.status === 'playing' && st().lvN === lv, 40000);
  if (!ok) { log.push('не почався рівень ' + lv + ' (' + st().ctx.room.status + ', ' + st().lvN + ')'); break; }
  const t0 = performance.now();
  const fed = await feed(roomId, lv);
  if (fed.left || fed.short) { results.push({ lv, fed }); break; }
  await sleep(300);
  const res = st().ctx.view.result;
  results.push({ lv, seat: st().ctx.seat, cleared: res && res.cleared, stars: res && res.stars, ms: res && res.ms, deaths: res && res.deaths,
    secs: ((performance.now() - t0) / 1000).toFixed(1), fed });
  if (cfg.extra === 'pause') await sleep(1500);
}
return { roomId, log, results, draw: window.__vohnyk.drawStats(), status: (q('.gstatus') || {}).textContent };
