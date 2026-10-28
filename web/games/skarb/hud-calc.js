// «Скарб Полуботка» — чисті розрахунки картки столу (без DOM): «що зараз», законні місця за поворотом, курсор
// клавіатури й джойстика, підпис карти, рядки підсумку раунду, звання партії. Правил тут нема — що законно, рахує
// сервер (view.me.legal); тут лише як це показати людині. Покрито тестами: dev/hud-calc.test.mjs (node --test).

import {
  SHAPES, SHAPE_NAME, rot180, TOOLS, TOOL_NAME, TOOL_ACC, TOOL_ICON, BREAK_NAME, FIX_NAME, ACTION_NAME, ACTION_HINT,
  ROLE_NAME, ROLE_ICON, SIDE_NAME, plural,
} from './consts.js';

/** Чи карта однакова в обох поворотах (x v h dx dv dh): тоді «⟲ 180°» нічого не дає. */
export function isSym(s) {
  const sh = SHAPES[s];
  return !sh || rot180(sh.m) === sh.m;
}

/** Маска карти ходу в повороті r. */
export function maskOf(s, r) {
  const sh = SHAPES[s];
  if (!sh) return 0;
  return r ? rot180(sh.m) : sh.m;
}

/**
 * Законні місця карти ходу для сцени (setLegal): `list` — me.legal[id] ([[x, y, r]]), `r` — вибраний поворот.
 * cells — законні саме в цьому повороті (для симетричної — усі), alt — лише в іншому; ghost — привид для наведення.
 */
export function pathTargets(list, s, r) {
  const sym = isSym(s);
  const now = new Set(), other = new Set();
  for (const [x, y, rr] of list || []) {
    const k = x + ',' + y;
    if (sym || (rr | 0) === (r | 0)) now.add(k); else other.add(k);
  }
  const cells = [], alt = [];
  for (const k of now) cells.push(k.split(',').map(Number));
  for (const k of other) if (!now.has(k)) alt.push(k.split(',').map(Number));
  const sh = SHAPES[s] || { m: 0, c: true };
  return { cells, alt, ghost: { m: maskOf(s, sym ? 0 : r), mAlt: maskOf(s, sym ? 0 : 1 - (r | 0)), c: sh.c } };
}

/** Поворот, у якому карта лягає на (x, y): поточний, якщо можна, інакше інший; null — не лягає. */
export function rotFor(list, s, r, x, y) {
  const sym = isSym(s);
  let other = null;
  for (const [a, b, rr] of list || []) {
    if (a !== x || b !== y) continue;
    if (sym) return 0;
    if ((rr | 0) === (r | 0)) return rr | 0;
    other = rr | 0;
  }
  return other;
}

/** Що це за карта: { icon, name, hint, kind }. */
export function cardInfo(c) {
  if (!c) return { icon: '', name: '', hint: '', kind: '' };
  if (c.k === 'path') {
    const dead = SHAPES[c.s] && !SHAPES[c.s].c;
    return { icon: dead ? '🪨' : '', name: SHAPE_NAME[c.s] || 'Хід', hint: ACTION_HINT.path + (dead ? '. Глуха: далі через неї не пройти' : ''), kind: 'path' };
  }
  if (c.k === 'break') return { icon: TOOL_ICON[c.tool] || '💥', name: BREAK_NAME[c.tool] || 'Зламати', hint: ACTION_HINT.break, kind: 'break' };
  if (c.k === 'fix') {
    const t = (c.tools || []).slice();
    return { icon: t.map((x) => TOOL_ICON[x]).join(''), name: FIX_NAME[t.join('+')] || 'Полагодити', hint: ACTION_HINT.fix, kind: 'fix' };
  }
  if (c.k === 'map') return { icon: '🗺', name: ACTION_NAME.map, hint: ACTION_HINT.map, kind: 'map' };
  if (c.k === 'rockfall') return { icon: '🪨', name: ACTION_NAME.rockfall, hint: ACTION_HINT.rockfall, kind: 'rockfall' };
  return { icon: '❔', name: 'Карта', hint: '', kind: c.k || '' };
}

/** Перший зламаний інструмент → «Кирку зламано» / «Лампу розбито» / «Тачка без колеса». */
export function brokenText(broken) {
  const t = TOOLS.find((x) => (broken || []).includes(x));
  return t ? { pick: 'Кирку зламано', lamp: 'Лампу розбито', cart: 'Тачка без колеса' }[t] : '';
}

/** Ціль карти дії з legal: для fix — { seat → [tools] }; для break — { seat → [tool] }. */
export function seatTargets(card, list) {
  const out = new Map();
  if (!card || !list) return out;
  if (card.k === 'break') for (const s of list) out.set(+s, [card.tool]);
  else if (card.k === 'fix') for (const [s, t] of list) { if (!out.has(+s)) out.set(+s, []); out.get(+s).push(t); }
  return out;
}

/**
 * «Що зараз» — смуга над полем і status(). v — вид, me — view.me, sel — { card, r } або null, nick(seat) → нік.
 * Повертає { s: коротко (status), l: довше (смуга, HTML-безпечні лише ніки через esc ззовні — тут лише текст) }.
 */
export function whatNow(v, me, sel, nick, esc = (x) => String(x)) {
  const n = (s) => esc(nick(s));
  if (!v || !v.players || !v.players.length || !v.phase) {
    return { s: '', l: '🪑 Стіл збирається. Сідай на вільне місце — щойно господар натисне «Почати», копаємо. Бракує людей — підсядуть Глеки 🤖' };
  }
  if (v.phase === 'over' || v.over) {
    const w = (v.over && v.over.winners) || [];
    if (!w.length) return { s: 'Партію скінчено', l: '🏁 Партію скінчено' };
    const mine = me && w.includes(me.seat);
    if (w.length > 1) return { s: mine ? 'Перемога! 🏆' : 'Скарб поділили', l: '🤝 Скарб поділили: ' + w.map(n).join(', ') };
    return { s: mine ? 'Перемога! 🏆' : 'Переміг ' + nick(w[0]), l: mine ? '🏆 Перемога! Найбагатший за столом — ти' : '🏆 Найбагатший — ' + n(w[0]) };
  }
  if (v.phase === 'round') {
    const re = v.roundEnd || {};
    return { s: 'Підсумок раунду', l: '🏁 Раунд ' + (re.n || v.round) + ': ' + (SIDE_NAME[re.side] || 'скінчився') + (v.round < v.rounds ? ' · далі — раунд ' + (v.round + 1) : '') };
  }
  const t = v.turn;
  const p = (v.players || []).find((x) => x.seat === t);
  const bot = p && (p.bot || p.away);
  if (me && me.must === 'play') {
    const mineP = v.players.find((x) => x.seat === me.seat) || {};
    const bt = brokenText(mineP.broken);
    if (sel && sel.card) {
      const c = sel.card, list = (me.legal || {})[c.id] || [];
      if (!list.length) return { s: 'Твій хід', l: '🚫 ' + ((me.why || {})[c.id] || 'Цю карту зараз нікуди не зіграєш') + '. Її можна лише скинути — «🗑 Скинути й пропустити»' };
      if (c.k === 'path') return { s: 'Твій хід', l: '👆 Клацни світлу клітинку — туди ляже хід. «⟲ 180°» перевертає карту; клітинки з ⟲ — для іншого боку' };
      if (c.k === 'break') return { s: 'Твій хід', l: '👆 Кому зламати ' + TOOL_ACC[c.tool] + '? Тицни світлий чип гравця' };
      if (c.k === 'fix') return { s: 'Твій хід', l: '👆 Кому полагодити? Тицни світлий чип (себе теж можна)' };
      if (c.k === 'map') return { s: 'Твій хід', l: '👆 Під яку мітку зазирнути? Тицни світлу мітку — побачиш лише ти' };
      if (c.k === 'rockfall') return { s: 'Твій хід', l: '👆 Який хід засипати? Тицни світлу карту на полі' };
    }
    if (bt) return { s: 'Твій хід', l: '🔧 ' + bt + ': ходу не покладеш — полагодь або зіграй дію. Або скинь карту' };
    return { s: 'Твій хід', l: '⛏ Твій хід: поклади хід від колодязя, зламай чи полагодь інструмент, глянь мапу — або скинь карту' };
  }
  if (t == null) return { s: '', l: '' };
  const think = bot ? ' 🤖 думає…' : '…';
  if (!me) return { s: 'Копає ' + nick(t), l: '👀 Дивишся збоку: зараз копає ' + n(t) + think };
  return { s: 'Копає ' + nick(t), l: '⏳ Зараз копає ' + n(t) + think };
}

/** Роль словами: «⛏ Ти — Копач». */
export function roleLine(role) {
  return (ROLE_ICON[role] || '❔') + ' Ти — ' + (ROLE_NAME[role] || '…');
}

/** Курсор клавіатури: наступна ціль у напрямку dir ('N','E','S','W') від cur ([x, y]); targets — [[x, y]]. */
export function stepCursor(targets, cur, dir) {
  if (!targets || !targets.length) return null;
  if (!cur) return targets[0];
  const [dx, dy] = { N: [0, -1], E: [1, 0], S: [0, 1], W: [-1, 0] }[dir] || [0, 0];
  let best = null, bs = Infinity;
  for (const t of targets) {
    const ex = t[0] - cur[0], ey = t[1] - cur[1];
    const along = ex * dx + ey * dy;
    if (along <= 0) continue;
    const side = Math.abs(ex * dy - ey * dx);
    const sc = along + side * 2.2;
    if (sc < bs) { bs = sc; best = t; }
  }
  return best || cur;
}

/** Ціль за порядком (LB/RB, Tab): наступна/попередня в списку. */
export function cycle(targets, cur, d) {
  if (!targets || !targets.length) return null;
  const i = cur ? targets.findIndex((t) => t[0] === cur[0] && t[1] === cur[1]) : -1;
  if (i < 0) return d < 0 ? targets[targets.length - 1] : targets[0];
  return targets[(i + d + targets.length) % targets.length];
}

/** Рядки «злитки раунду»: [{ seat, n, cards, total }] у порядку order. */
export function goldRows(re, order) {
  const by = new Map(((re && re.gold) || []).map((g) => [g.seat, g]));
  return (order || []).map((seat, i) => {
    const g = by.get(seat);
    return { seat, n: g ? g.n : 0, cards: g ? g.cards : null, total: re && re.totals ? re.totals[i] : null, role: re && re.roles ? re.roles[seat] : null };
  });
}

/** «3 злитки» */
export function goldText(n) { return n + ' ' + plural(n, 'злиток', 'злитки', 'злитків'); }

/** Звання партії (до трьох, лише ненульові): [{ seat, icon, name, why }]. */
export function titles(stats) {
  const seats = (stats && stats.seats) || [];
  const T = [
    ['paths', '🥇', 'Золота лопата', (n) => n + ' ' + plural(n, 'хід', 'ходи', 'ходів') + ' прокопав'],
    ['moleGold', '🦊', 'Підземний лис', (n) => goldText(n) + ' кротом'],
    ['fixes', '🔧', 'Майстер-ремонтник', (n) => n + ' ' + plural(n, 'ремонт', 'ремонти', 'ремонтів')],
    ['breaks', '🔨', 'Ламайло', (n) => n + ' ' + plural(n, 'злам', 'злами', 'зламів')],
    ['rockfalls', '🪨', 'Обвальщик', (n) => n + ' ' + plural(n, 'завал', 'завали', 'завалів')],
  ];
  const out = [];
  for (const [k, icon, name, why] of T) {
    let best = null;
    for (const s of seats) if ((s[k] || 0) > 0 && (!best || s[k] > best[k])) best = s;
    if (best) out.push({ seat: best.seat, icon, name, why: why(best[k]) });
    if (out.length >= 3) break;
  }
  return out;
}

/** Стовпчики «злитки за раундами»: timeline — [[за order] після кожного раунду] → по гравцю [приріст раунду 1, 2, 3]. */
export function roundBars(timeline, nPlayers) {
  const tl = timeline || [];
  const out = [];
  for (let i = 0; i < nPlayers; i++) {
    const parts = [];
    let prev = 0;
    for (const row of tl) { const x = row[i] || 0; parts.push(Math.max(0, x - prev)); prev = x; }
    out.push(parts);
  }
  const max = Math.max(1, ...out.map((p) => p.reduce((a, b) => a + b, 0)));
  return { bars: out, max };
}

/** Що підсвітити з «💡 Глек радить»: { card, kind: 'cell'|'seat'|'goal'|'pass', x, y, r, seat, tool, goal }. */
export function tipTarget(tip, hand) {
  if (!tip || tip.card == null) return null;
  const card = (hand || []).find((c) => c.id === tip.card);
  if (!card) return null;
  if (tip.pass) return { card, kind: 'pass' };
  if (tip.goal != null) return { card, kind: 'goal', goal: tip.goal };
  if (tip.seat != null) return { card, kind: 'seat', seat: tip.seat, tool: tip.tool || null };
  if (tip.x != null) return { card, kind: 'cell', x: tip.x, y: tip.y, r: tip.r | 0 };
  return { card, kind: 'card' };
}

/** Підпис інструментів: «⛏ кирка». */
export function toolLabel(t) { return TOOL_ICON[t] + ' ' + TOOL_NAME[t]; }
