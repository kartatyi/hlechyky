// «Під глеком»: швидкодія update() на шістьох (spec §6.1: < 4 мс у середньому за 300 викликів).
// Модуль вантажимо ще раз зі справжнім HGames.ui у накладку поверх сторінки й женемо дві серії:
//  • перебіг — як у грі: ставка за ставкою (історія росте до 12), розкриття, «Далі» по одному, новий раунд;
//  • стрес — кожен вид зовсім інший (нові руки, історія, лічильники): верхня межа, у грі так не буває.
// Для кожної — середній час самого update() і update() + примусова розкладка (void offsetHeight).
const src = await (await fetch('/games/dice.js?cb=' + Date.now())).text();
let mod = null;
new Function('HGames', src)({ register(m) { mod = m; }, ui: HGames.ui });
const host = document.createElement('div');
host.className = 'gtable';
host.style.cssText = 'position:fixed;left:0;top:0;width:900px;z-index:99999;background:var(--panel)';
const root = document.createElement('div');
root.className = 'gbody';
host.appendChild(root);
document.body.appendChild(host);
const nicks = ['Оля', 'Петро', 'Ганна', 'Іван', 'Марта', 'Богдан'];
const rnd = (n) => Math.floor(Math.random() * n);
function view(k) {
  const phase = k % 5 === 4 ? 'reveal' : 'bid';
  const hist = [];
  let q = 1, f = 2;
  for (let i = 0; i < 6 + (k % 9); i++) { hist.push({ seat: i % 6, q, f, auto: i === 0 && k % 7 === 0 }); if (f < 6) f++; else { f = 2; q++; } }
  const players = nicks.map((n, s) => ({ seat: s, nick: n, dice: 5 - (s + k) % 3, alive: true, left: false, palificoUsed: false, wasAtOne: false }));
  const total = players.reduce((a, p) => a + p.dice, 0);
  const b = hist[hist.length - 1];
  const hands = players.map((p) => Array.from({ length: p.dice }, () => 1 + rnd(6)).sort());
  return {
    turn: phase === 'bid' ? (b.seat + 1) % 6 : null, phase, round: 3 + Math.floor(k / 5), endsAt: new Date(Date.now() + 20000).toISOString(),
    phaseMs: phase === 'bid' ? 30000 : 6000, rules: { dice: 5, turnMs: 30000, exact: true, palifico: true },
    palifico: false, wild: true, starter: 0, total, players, my: hands[0], bid: b, history: hist,
    canExact: true, ready: phase === 'reveal' ? [1, 2] : [], note: null,
    reveal: phase === 'reveal' ? { kind: 'liar', caller: (b.seat + 1) % 6, bid: b, count: 7, jokers: 2, dice: hands, loser: b.seat, gainer: null, out: false, next: b.seat, say: 'Розкусили! На столі лише 7 шісток. Петро платить кісточкою.' } : null,
    result: null,
  };
}
// Справжній перебіг: кожен наступний вид — +1 ставка (хід далі), кожні 12 ставок — розкриття, далі новий раунд.
function seq(n) {
  const out = [];
  let round = 1, hist = [], q = 1, f = 2;
  const players = nicks.map((nk, s) => ({ seat: s, nick: nk, dice: 5, alive: true, left: false, palificoUsed: false, wasAtOne: false }));
  let hands = players.map((p) => Array.from({ length: p.dice }, () => 1 + rnd(6)).sort());
  const endsAt = new Date(Date.now() + 600000).toISOString();
  for (let k = 0; out.length < n; k++) {
    const total = players.reduce((a, p) => a + p.dice, 0);
    if (hist.length === 12) {
      const b = hist[hist.length - 1];
      const loser = (b.seat + 1) % 6;
      out.push({ turn: null, phase: 'reveal', round, endsAt, phaseMs: 6000, rules: { dice: 5, turnMs: 30000, exact: true, palifico: true },
        palifico: false, wild: true, starter: 0, total, players: players.map((p) => ({ ...p })), my: hands[0], bid: b, history: hist.slice(),
        canExact: false, ready: [], note: null, reveal: { kind: 'liar', caller: loser, bid: b, count: 7, jokers: 2, dice: hands, loser, gainer: null, out: false, next: loser, say: 'Чесна ставка: 7 шісток. Петро віддає кісточку.' }, result: null });
      for (let r = 1; r <= 4 && out.length < n; r++) out.push({ ...out[out.length - 1], ready: [1, 2, 3, 4].slice(0, r) });
      players[loser].dice = Math.max(1, players[loser].dice - 1);
      round++; hist = []; q = 1; f = 2;
      hands = players.map((p) => Array.from({ length: p.dice }, () => 1 + rnd(6)).sort());
      out.push({ turn: null, phase: 'shake', round, endsAt, phaseMs: 1500, rules: { dice: 5, turnMs: 30000, exact: true, palifico: true },
        palifico: false, wild: true, starter: loser, total: players.reduce((a, p) => a + p.dice, 0), players: players.map((p) => ({ ...p })), my: hands[0], bid: null, history: [],
        canExact: false, ready: [], note: 'Раунд ' + round + '. Починає Петро', reveal: null, result: null });
      continue;
    }
    const seat = hist.length % 6;
    hist.push({ seat, q, f, auto: false });
    if (f < 6) f++; else { f = 2; q++; }
    const b = hist[hist.length - 1];
    out.push({ turn: (seat + 1) % 6, phase: 'bid', round, endsAt, phaseMs: 30000, rules: { dice: 5, turnMs: 30000, exact: true, palifico: true },
      palifico: false, wild: true, starter: 0, total, players: players.map((p) => ({ ...p })), my: hands[0], bid: b, history: hist.slice(),
      canExact: seat !== 0, ready: [], note: null, reveal: null, result: null });
  }
  return out;
}
const ctx = {
  room: { id: 'perf', game: 'dice', status: 'playing', options: {} }, seat: 0, me: { nick: 'Оля' }, frame: null,
  playing: true, mine: true, myTurn: false, act: async () => ({ ok: true }), input() {}, toast() {},
  esc: (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])),
  seatName: (i) => nicks[i], nickOf: (i) => nicks[i], css: (v, d) => d, ui: HGames.ui,
};
function bench(views, warm) {
  ctx.view = views[0];
  for (let i = 0; i < warm; i++) { ctx.view = views[i]; mod.update(root, ctx); }
  const N = 300;
  let js = 0, full = 0, worst = 0;
  for (let i = 0; i < N; i++) {
    ctx.view = views[(warm + i) % views.length];
    ctx.myTurn = ctx.view.turn === 0;
    const t0 = performance.now();
    mod.update(root, ctx);
    const t1 = performance.now();
    void root.offsetHeight;
    const t2 = performance.now();
    js += t1 - t0; full += t2 - t0; worst = Math.max(worst, t2 - t0);
  }
  return { updateMs: +(js / N).toFixed(3), withLayoutMs: +(full / N).toFixed(3), worstMs: +worst.toFixed(2) };
}
ctx.view = seq(1)[0];
mod.mount(root, ctx);
const real = bench(seq(330), 30);
const stress = bench(Array.from({ length: 40 }, (_, k) => view(k)), 30);
let same = 0;
for (let i = 0; i < 300; i++) { const t0 = performance.now(); mod.update(root, ctx); same += performance.now() - t0; }
const nodes = root.getElementsByTagName('*').length;
mod.unmount(root, ctx);
host.remove();
return { calls: 300, real, stress, sameViewMs: +(same / 300).toFixed(4), nodes };
