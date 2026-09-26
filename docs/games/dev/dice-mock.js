// «Під глеком»: рідкісні стани без везіння — модуль у накладці поверх сторінки з виготовленим видом.
// window.__mode: 'exact' — влучне «Точно!» у розкритті на шістьох; 'pal' — мій хід у паліфіко (грань заморожена);
// 'fall' — «Брешеш!» на шістьох: у Марти падає кісточка, що не рахувалась, у Петра (друге розкриття) рахувались усі;
// 'exact0' — у мене вже більше потрібних, ніж у ставці («Точно!» — 0 %); 'win' — підсумок зі смішними нагородами;
// 'lobby3' — лобі столу на трьох кісточках, четверо за столом.
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
} else if (MODE === 'fall' || MODE === 'fallall') {
  // Марта (4) програла; у неї [3, 5] при ставці на ⚄ — падає трійка, а не п'ятірка (світних рівно «на столі N»).
  // fallall: у Петра (1) рахувались усі — падає остання, але з зеленою рамкою поруч із червоною.
  const b = { seat: MODE === 'fall' ? 4 : 1, q: MODE === 'fall' ? 8 : 7, f: 5, auto: false };
  const dice = MODE === 'fall'
    ? [[1, 5], [2, 4, 5, 6], [5], [1, 3, 3, 6, 6], [3, 5], []]
    : [[1, 5], [1, 5, 5, 5], [2], [3, 3, 4, 6, 6], [2, 4], []];
  const count = dice.flat().filter((d) => d === 5 || d === 1).length;
  v = { turn: null, phase: 'reveal', round: 9, endsAt, phaseMs: 6000, rules: { dice: 5, turnMs: 30000, exact: true, palifico: true },
    palifico: false, wild: true, starter: 1, total: 15, players, my: dice[0], bid: b, history: [b], canExact: false, ready: [], note: null,
    reveal: { kind: 'liar', caller: MODE === 'fall' ? 0 : 2, bid: b, count, jokers: dice.flat().filter((d) => d === 1).length, dice,
      loser: b.seat === 4 && count < b.q ? 4 : MODE === 'fall' ? 0 : 1, gainer: null, out: false, next: 4, say: 'Розкусили! На столі жодної шістки. Марта платить кісточкою.' },
    result: null };
  v.reveal.loser = count >= b.q ? v.reveal.caller : b.seat;
} else if (MODE === 'exact0') {
  const b = { seat: 1, q: 2, f: 5, auto: false };
  v = { turn: 2, phase: 'bid', round: 4, endsAt: new Date(Date.now() + 22000).toISOString(), phaseMs: 30000, rules: { dice: 5, turnMs: 30000, exact: true, palifico: true },
    palifico: false, wild: true, starter: 1, total: 9, players: players.map((p) => ({ ...p, dice: [4, 3, 2, 0, 0, 0][p.seat], alive: p.seat < 3 })),
    my: [5, 5, 5, 6], bid: b, history: [b], canExact: true, ready: [], note: null, reveal: null, result: null };
} else if (MODE === 'win') {
  v = { turn: null, phase: 'done', round: 22, endsAt, phaseMs: 6000, rules: { dice: 5, turnMs: 30000, exact: true, palifico: true },
    palifico: false, wild: true, starter: 0, total: 2, players: players.map((p) => ({ ...p, dice: p.seat === 0 ? 2 : 0, alive: p.seat === 0 })),
    my: [2, 6], bid: null, history: [], canExact: false, ready: [], note: null, reveal: null,
    result: { winner: 0, places: [5, 3, 1, 4, 2], rounds: 22, say: 'Оля перетрушує всіх.',
      fun: ['🤥 Найнахабніший блеф — Петро: 9 × ⚅, а було 4', '🎯 Снайпер — Оля: 2 влучні «Точно!»',
        '🕵 Нюх на брехню — Іван: 3 рази «Брешеш!» у яблучко', '😴 Соня — Марта: 3 ходи проспано'] } };
} else if (MODE === 'lobby3') {
  v = null;
} else if (MODE === 'pal') {
  const b = { seat: 1, q: 3, f: 3, auto: false };
  v = { turn: 0, phase: 'bid', round: 7, endsAt: new Date(Date.now() + 22000).toISOString(), phaseMs: 30000, rules: { dice: 5, turnMs: 30000, exact: true, palifico: true },
    palifico: true, wild: false, starter: 2, total: 15, players, my: [3, 5], bid: b,
    history: [{ seat: 2, q: 2, f: 3, auto: false }, { seat: 3, q: 2, f: 3, auto: true }, { seat: 4, q: 1, f: 3, auto: false }, b].slice(0, 2).concat([b]), canExact: true, ready: [], note: null, reveal: null, result: null };
}
const status = MODE === 'win' ? 'finished' : MODE === 'lobby3' ? 'lobby' : 'playing';
const ctx = {
  room: { id: 'mock', game: 'dice', status, options: MODE === 'lobby3' ? { dice: '3' } : {},
    seats: MODE === 'lobby3' ? nicks.slice(0, 4).map((n, i) => ({ i, nick: n })) : [] },
  seat: MODE === 'exact0' ? 2 : 0, me: { nick: 'Оля' }, frame: null,
  playing: status === 'playing', mine: true, myTurn: !!v && v.turn === 0, act: async () => ({ ok: true }), input() {}, toast() {},
  esc: (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])),
  seatName: (i) => nicks[i], nickOf: (i) => nicks[i], css: (x, d) => d, ui: HGames.ui, view: v,
};
host._mod = mod; host._ctx = ctx;
mod.mount(root, ctx);
const probe = {};
if (MODE === 'fall' || MODE === 'fallall') {
  // посеред рахунку: лічильник іде, частина рахункових ще не спалахнула
  await sleep(1300);
  probe.midCount = q('.dimock .di-cnt') && q('.dimock .di-cnt').textContent;
}
await sleep(window.__wait || 2600);
if (MODE === 'fall' || MODE === 'fallall') {
  // світних (зелена рамка) — рівно «на столі N»
  const lit = [...root.querySelectorAll('.di-rows .di-die')].filter((d) => getComputedStyle(d).boxShadow.includes('123, 211, 137')).length;
  const fall = root.querySelector('.di-die.fall');
  probe.count = v.reveal.count;
  probe.lit = lit;
  probe.cnt = q('.dimock .di-cnt').textContent;
  probe.fallClass = fall && fall.className;
  probe.fallShadow = fall && getComputedStyle(fall).boxShadow;
}
if (MODE === 'exact0') probe.exactBtn = root.querySelector('.di-exact').innerText;
return { status: mod.status(ctx), probe, text: root.innerText.slice(0, 500) };
