// «Під глеком»: рідкісні стани без везіння — модуль у накладці поверх сторінки з виготовленим видом.
// window.__mode: 'exact' — влучне «Точно!» у розкритті на шістьох; 'pal' — мій хід у паліфіко (грань заморожена).
for (const el of qa('.dimock')) { el._mod && el._mod.unmount(el.firstChild, el._ctx); el.remove(); }
const src = await (await fetch('/games/dice.js?cb=' + Date.now())).text();
let mod = null;
new Function('HGames', src)({ register(m) { mod = m; }, ui: HGames.ui });
const host = document.createElement('div');
host.className = 'gtable dimock';
host.style.cssText = 'position:fixed;inset:0;z-index:99999;background:var(--bg);overflow:auto;padding:16px;box-sizing:border-box';
const root = document.createElement('div');
root.className = 'gbody';
host.appendChild(root);
document.body.appendChild(host);
const nicks = ['Оля', 'Петро', 'Ганна', 'Іван', 'Марта', 'Богдан'];
const players = nicks.map((n, s) => ({ seat: s, nick: n, dice: [2, 4, 1, 5, 3, 0][s], alive: s !== 5, left: false, palificoUsed: s === 2, wasAtOne: s === 2 }));
const endsAt = new Date(Date.now() + 5000).toISOString();
const MODE = window.__mode || 'exact';
let v;
if (MODE === 'exact') {
  const b = { seat: 3, q: 6, f: 4, auto: false };
  v = { turn: null, phase: 'reveal', round: 9, endsAt, phaseMs: 6000, rules: { dice: 5, turnMs: 30000, exact: true, palifico: true },
    palifico: false, wild: true, starter: 1, total: 15, players, my: [1, 4], bid: b,
    history: [{ seat: 1, q: 3, f: 4, auto: false }, { seat: 2, q: 4, f: 4, auto: false }, b], canExact: false, ready: [1], note: null,
    reveal: { kind: 'exact', caller: 0, bid: b, count: 6, jokers: 2, dice: [[1, 4], [2, 4, 5, 6], [4], [1, 3, 3, 6, 6], [2, 4, 5], []], loser: null, gainer: 0, out: false, next: 0,
      say: 'Точнісінько 6! Оля повертає кісточку.' }, result: null };
} else if (MODE === 'pal') {
  const b = { seat: 1, q: 3, f: 3, auto: false };
  v = { turn: 0, phase: 'bid', round: 7, endsAt: new Date(Date.now() + 22000).toISOString(), phaseMs: 30000, rules: { dice: 5, turnMs: 30000, exact: true, palifico: true },
    palifico: true, wild: false, starter: 2, total: 15, players, my: [3, 5], bid: b,
    history: [{ seat: 2, q: 2, f: 3, auto: false }, { seat: 3, q: 2, f: 3, auto: true }, { seat: 4, q: 1, f: 3, auto: false }, b].slice(0, 2).concat([b]), canExact: true, ready: [], note: null, reveal: null, result: null };
}
const ctx = {
  room: { id: 'mock', game: 'dice', status: 'playing', options: {} }, seat: 0, me: { nick: 'Оля' }, frame: null,
  playing: true, mine: true, myTurn: v.turn === 0, act: async () => ({ ok: true }), input() {}, toast() {},
  esc: (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])),
  seatName: (i) => nicks[i], nickOf: (i) => nicks[i], css: (x, d) => d, ui: HGames.ui, view: v,
};
host._mod = mod; host._ctx = ctx;
mod.mount(root, ctx);
await sleep(window.__wait || 2600);
return { status: mod.status(ctx), text: root.innerText.slice(0, 400) };
