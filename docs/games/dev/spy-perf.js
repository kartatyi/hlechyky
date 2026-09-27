/*
  Замір клієнта «Шпигуна» (spec §6.3): середній час update() на 10 гравцях по 300 викликах із різними видами
  і час одного кроку rAF-годинника. Запускати в сторінці сайту, де відкрито стіл «Шпигуна» (хоч гравцем, хоч
  глядачем): скрипт бере справжній вид із картки, розмножує гравців до десяти й ганяє окрему копію модуля в
  прихованому від людей, але видимому для верстки контейнері поруч.

    python D:/or-wt/_tools/cdp2.py --port <p> --js docs/games/dev/spy-perf.js
*/
const live = document.querySelector('.spy');
if (!live || !live._sp || !live._sp.ctx.view) return { err: 'нема живої картки Шпигуна' };
const base = JSON.parse(JSON.stringify(live._sp.ctx.view));
const nicks = ['Оля', 'Петро', 'Ганна', 'Микола', 'Іван', 'Марія', 'Тарас', 'Соломія', 'Богдан', 'Леся'];
base.players = nicks.map((n, i) => ({ seat: i, nick: 'гість ' + n, here: true, score: i % 4, accused: i % 3 === 0, ready: false }));

// Окрема копія модуля: той самий файл, але register ловимо собі.
let mod = null;
const src = await (await fetch('/games/spy.js?perf=' + Date.now())).text();
new Function('HGames', src)({ register: (m) => { mod = m; }, ui: HGames.ui, openTable() {} });
const host = document.createElement('div');
host.className = 'gbody';
host.style.cssText = 'position:fixed;left:0;top:0;width:1100px;opacity:0;pointer-events:none;z-index:-1';
document.body.appendChild(host);
const ctx = Object.assign({}, live._sp.ctx, { seat: 0, act: async () => ({ ok: true }) });
ctx.view = base;
mod.mount(host, ctx);

const phases = ['play', 'play', 'vote', 'final', 'reveal'];
function variant(i) {
  const v = JSON.parse(JSON.stringify(base));
  v.phase = phases[i % phases.length];
  v.asker = i % 10;
  v.askedBy = (i + 3) % 10;
  v.askGrace = i % 7 === 0;
  v.phaseLeftMs = 20000 - (i % 20) * 500;
  v.clock = { endsAt: null, leftMs: 300000 - i * 250, paused: v.phase !== 'play', totalMs: 360000 };
  v.me = i % 2 ? { spy: true, loc: null, role: null } : { spy: false, loc: base.deck[i % base.deck.length][0], role: 'роль ' + i };
  v.vote = v.phase === 'vote' ? { suspect: (i + 1) % 10, accuser: i % 10, votes: { [i % 10]: true, [(i + 2) % 10]: i % 2 === 0 }, need: 9, endsAt: v.endsAt } : null;
  v.blame = v.phase === 'final' ? { votes: { 1: 2, 3: 2, 4: (i % 9) + 1 }, need: 6 } : null;
  v.reveal = v.phase === 'reveal' ? { spy: 1, loc: base.deck[0][0], roles: Object.fromEntries(nicks.map((_, s) => [s, 'роль ' + s])), how: 'caught', gained: Object.fromEntries(nicks.map((_, s) => [s, s === 1 ? 0 : 1])), guess: null, suspect: 1, accuser: 0 } : null;
  v.players.forEach((p, k) => { p.score = (k + i) % 5; p.ready = v.phase === 'reveal' && k < i % 10; });
  return v;
}
const views = Array.from({ length: 300 }, (_, i) => variant(i));
// прогрів
for (let i = 0; i < 30; i++) { ctx.view = views[i]; mod.update(host, ctx); }
let t0 = performance.now();
for (let i = 0; i < 300; i++) { ctx.view = views[i]; mod.update(host, ctx); }
const updateMs = (performance.now() - t0) / 300;
t0 = performance.now();
for (let i = 0; i < 300; i++) { ctx.view = views[i]; mod.update(host, ctx); void host.offsetHeight; }
const updateLayoutMs = (performance.now() - t0) / 300;
// той самий вид ще раз (подія rooms) — мусить нічого не чіпати
t0 = performance.now();
for (let i = 0; i < 300; i++) mod.update(host, ctx);
const sameMs = (performance.now() - t0) / 300;
// Типовий потік: та сама фаза й картка, міняється лише покажчик «хто питає» (так іде весь раунд).
const typical = Array.from({ length: 300 }, (_, i) => {
  const v = JSON.parse(JSON.stringify(views[0]));
  v.phase = 'play'; v.vote = null; v.blame = null; v.reveal = null;
  v.me = { spy: false, loc: base.deck[0][0], role: 'роль' };
  v.asker = i % 10; v.askedBy = (i + 9) % 10; v.clock.paused = false;
  return v;
});
for (let i = 0; i < 20; i++) { ctx.view = typical[i]; mod.update(host, ctx); }
t0 = performance.now();
for (let i = 0; i < 300; i++) { ctx.view = typical[i]; mod.update(host, ctx); void host.offsetHeight; }
const typicalMs = (performance.now() - t0) / 300;
const el = host.querySelector('.spy');
t0 = performance.now();
for (let i = 0; i < 3000; i++) el._sp.step();
const stepMs = (performance.now() - t0) / 3000;
mod.unmount(host);
host.remove();
return { typicalAskWithLayoutMs: +typicalMs.toFixed(3), updateMs: +updateMs.toFixed(3), updateWithLayoutMs: +updateLayoutMs.toFixed(3), sameViewMs: +sameMs.toFixed(4), rafStepMs: +stepMs.toFixed(4) };
