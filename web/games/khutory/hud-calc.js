// «Хутори» — чисті розрахунки картки столу (без DOM): підказка вершини, «Глек радить», пропозиція скидання, банк,
// графік підсумку. Правил тут нема — сервер вирішує сам; це лише те, що показати людині, щоб вона зрозуміла хід.
// Покрито тестами: web/games/khutory/dev/hud-calc.test.mjs (node --test).

// Сусідні модулі — з тим самим ?v=, що й у цього файла: сайт оновлює гру без F5, а карта модулів сторінки
// тримає старі './x.js' назавжди; із відбитком у адресі після деплою підтягнеться нове (khutory.js, modQ).
const Q = new URL(import.meta.url).search;
const { RES, PIPS, TERRAIN_RES, RES_NAME, RES_ACC } = await import('./consts.js' + Q);

/** Форми іменників ресурсів для «1 дерево · 2 дерева · 5 дерев». */
const FORMS = {
  wood: ['дерево', 'дерева', 'дерев'],
  clay: ['глина', 'глини', 'глин'],
  wool: ['вовна', 'вовни', 'вовни'],
  grain: ['зерно', 'зерна', 'зерна'],
  ore: ['залізо', 'заліза', 'заліза'],
};

function form(n, f) {
  const a = Math.abs(n) % 100, b = a % 10;
  if (a > 10 && a < 20) return f[2];
  if (b === 1) return f[0];
  if (b >= 2 && b <= 4) return f[1];
  return f[2];
}

/** «2 глини» */
export function resPhrase(res, n) { return n + ' ' + form(n, FORMS[res] || [res, res, res]); }

/** { wood: 1, ore: 2 } → «1 дерево, 2 заліза» (порядок — як у руці; нулі пропускаються). */
export function resList(obj, sep = ', ') {
  const out = [];
  for (const r of RES) if (obj && obj[r] > 0) out.push(resPhrase(r, obj[r]));
  return out.join(sep) || 'нічого';
}

export function total(hand) {
  let s = 0;
  for (const r of RES) s += (hand && hand[r]) || 0;
  return s;
}

/** Для кожної вершини — гекси, яких вона торкається. */
export function vertexHexes(board) {
  const out = board.vx.map(() => []);
  board.hexes.forEach((h, i) => { for (const v of h.v) if (out[v].indexOf(i) < 0) out[v].push(i); });
  return out;
}

const vhCache = new WeakMap();
function vh(board) {
  let m = vhCache.get(board);
  if (!m) { m = vertexHexes(board); vhCache.set(board, m); }
  return m;
}

/**
 * Що родить вершина: { items: [{ hex, res, num, pips, robbed }], pips, pct }.
 * robber — id гекса з Кармелюком (його числа зараз не родять; в pips не рахуємо).
 */
export function vertexYield(board, v, robber = null) {
  const items = [];
  let pips = 0;
  for (const h of vh(board)[v] || []) {
    const hx = board.hexes[h];
    const res = TERRAIN_RES[hx.t];
    if (!res || !hx.num) continue;
    const p = PIPS[hx.num] || 0;
    const robbed = h === robber;
    items.push({ hex: h, res, num: hx.num, pips: p, robbed });
    if (!robbed) pips += p;
  }
  items.sort((a, b) => b.pips - a.pips);
  return { items, pips, pct: Math.round(pips / 36 * 100) };
}

/** «дерево (4) · вовна (5) — родить у 19 % кидків» — для підказки «Хата тут дасть: …». */
export function yieldText(y) {
  if (!y.items.length) return 'нічого — тут лише Дике поле та море';
  const parts = y.items.map((it) => RES_NAME[it.res] + ' (' + it.num + (it.robbed ? ', там Кармелюк' : '') + ')');
  return parts.join(' · ') + ' — родить у ' + y.pct + ' % кидків';
}

/** Пристань при вершині: { kind, rate } або null. */
export function portAt(board, v) {
  for (const p of board.ports || []) {
    const e = board.edges[p.e];
    if (e && (e[0] === v || e[1] === v)) return p;
  }
  return null;
}

/**
 * «Глек радить»: k найкращих вершин зі spots. Бал = крапки + різноманіття (по 1,2 за кожен різний ресурс)
 * + 0,6 за пристань; рівні — менший id (стабільно). have — уже мої ресурси з хат (друга хата добирає чого бракує).
 */
export function adviseTowns(board, spots, k = 3, have = null) {
  const scored = [];
  for (const v of spots || []) {
    const y = vertexYield(board, v);
    if (!y.pips) continue;
    const kinds = new Set(y.items.map((it) => it.res));
    let score = y.pips + 1.2 * kinds.size + (portAt(board, v) ? 0.6 : 0);
    if (have) for (const r of kinds) if (!have[r]) score += 0.8;
    scored.push({ v, score });
  }
  scored.sort((a, b) => b.score - a.score || a.v - b.v);
  return scored.slice(0, k).map((s) => s.v);
}

/**
 * Готова пропозиція скидання: n карт, щоразу з найчисленнішого ресурсу (рівні — з того, що далі в RES:
 * залізо й зерно бережемо останніми). Повертає повний об'єкт за RES.
 */
export function discardPlan(hand, n) {
  const left = {}, out = {};
  for (const r of RES) { left[r] = (hand && hand[r]) || 0; out[r] = 0; }
  const keep = ['wood', 'clay', 'wool', 'grain', 'ore'];   // порядок «що віддати першим» при рівності
  for (let i = 0; i < n; i++) {
    let best = null;
    for (const r of keep) if (left[r] > 0 && (best === null || left[r] > left[best])) best = r;
    if (best === null) break;
    left[best]--; out[best]++;
  }
  return out;
}

/** Лише ненульові — так payload `discard`/`offer` чистіший. */
export function compact(obj) {
  const o = {};
  for (const r of RES) if (obj && obj[r] > 0) o[r] = obj[r];
  return o;
}

/** Банк: що можна віддати й за яким курсом, найвигідніше — першим. */
export function bankChoices(hand, rates) {
  return RES.map((r) => ({ res: r, rate: (rates && rates[r]) || 4, have: (hand && hand[r]) || 0 }))
    .map((c) => ({ ...c, can: c.have >= c.rate, max: Math.floor(c.have / c.rate) }))
    .sort((a, b) => (b.can - a.can) || (a.rate - b.rate) || (RES.indexOf(a.res) - RES.indexOf(b.res)));
}

/** Скільки бракує на ціну: { ore: 3 } або {} — лише для кружечків на кнопках (причину пише сервер). */
export function lacking(hand, cost) {
  const o = {};
  for (const r of RES) {
    const need = (cost && cost[r]) || 0, have = (hand && hand[r]) || 0;
    if (need > have) o[r] = need - have;
  }
  return o;
}

/**
 * Графік очок за колами: polyline-точки для кожного місця в прямокутнику w×h з полями pad.
 * timeline — [[очки за місцями після кола]]; target — межа (пунктир).
 */
export function chartGeom(timeline, w, h, target = 0, pad = 22) {
  const rows = timeline || [];
  const seats = rows.length ? rows[0].length : 0;
  let max = target || 0;
  for (const row of rows) for (const p of row) if (p > max) max = p;
  max = Math.max(1, max);
  const n = Math.max(1, rows.length - 1);
  const x = (i) => Math.round((pad + (w - 2 * pad) * i / n) * 10) / 10;
  const y = (p) => Math.round((h - pad - (h - 2 * pad) * p / max) * 10) / 10;
  const lines = [];
  for (let s = 0; s < seats; s++) lines.push(rows.map((row, i) => [x(i), y(row[s] || 0)]));
  return { lines, max, x, y, targetY: target ? y(target) : null };
}

// ─── Торг і увага (khutory-look.md §1, §10) ─────────────────────────────

/** Знахідний відмінок: «дає 1 глину», «хоче 2 вовни», «5 глин». */
export function resPhraseAcc(res, n) {
  const a = Math.abs(n) % 100;
  if (a % 10 === 1 && a !== 11 && RES_ACC[res]) return n + ' ' + RES_ACC[res];
  return resPhrase(res, n);
}
/** { clay: 1, ore: 2 } → «1 глину, 2 заліза». */
export function resListAcc(obj, sep = ', ') {
  const out = [];
  for (const r of RES) if (obj && obj[r] > 0) out.push(resPhraseAcc(r, obj[r]));
  return out.join(sep) || 'нічого';
}

/** Чипи банку: кожен ресурс із найкращим курсом (`me.rates`), скільки разів можна обміняти; порядок — як у руці. */
export function bankChips(hand, rates) {
  return RES.map((r) => {
    const rate = (rates && rates[r]) || 4, have = (hand && hand[r]) || 0;
    return { res: r, rate, have, max: Math.floor(have / rate), port: rate < 4 };
  });
}

/**
 * «Обміняй і будуй»: чи покриває банк нестачу на ціну `cost` (бракує 1–2 карт) — і як саме.
 * Повертає обміни `[{ give, get, n }]` (n — скільки ОТРИМАТИ, як у дії `bank`) або null (не бракує / бракує > 2 /
 * банк не покриє). Віддаємо лише надлишок понад ціну, найдешевший курс першим.
 */
export function bankFix(hand, cost, rates, maxLack = 2) {
  const lack = lacking(hand, cost);
  const need = total(lack);
  if (!need || need > maxLack) return null;
  const spare = {};
  for (const r of RES) spare[r] = Math.max(0, ((hand && hand[r]) || 0) - ((cost && cost[r]) || 0));
  const out = [];
  for (const get of RES) {
    for (let i = 0; i < (lack[get] || 0); i++) {
      let best = null;
      for (const g of RES) {
        if (g === get || lack[g]) continue;
        const rate = (rates && rates[g]) || 4;
        if (spare[g] < rate) continue;
        if (!best || rate < best.rate || (rate === best.rate && spare[g] > spare[best.res])) best = { res: g, rate };
      }
      if (!best) return null;
      spare[best.res] -= best.rate;
      const t = out.find((x) => x.give === best.res && x.get === get);
      if (t) t.n++; else out.push({ give: best.res, get, n: 1, rate: best.rate });
    }
  }
  return out;
}

/** Мою пропозицію відхилили всі, кому вона була (і ніхто не прийняв). seats — місця всіх гравців. */
export function allRefused(offer, seats) {
  if (!offer || (offer.acc || []).length) return false;
  const dec = new Set(offer.dec || []);
  const to = offer.to == null ? (seats || []).filter((s) => s !== offer.from) : [offer.to];
  return to.length > 0 && to.every((s) => dec.has(s));
}

/**
 * Пропозиції, що чекають МОЄЇ відповіді, найновіша першою: від поточного гравця мені чи всім (я ще не відповів) —
 * kind 'offer'; зустрічна мені, коли хід мій, — kind 'counter'.
 */
export function incomingOffers(offers, seat, turn) {
  if (seat == null) return [];
  const out = [];
  for (const o of offers || []) {
    if (o.from === seat) continue;
    if (o.from === turn) {
      if ((o.to == null || o.to === seat) && !(o.acc || []).includes(seat) && !(o.dec || []).includes(seat)) out.push({ o, kind: 'offer' });
    } else if (seat === turn) out.push({ o, kind: 'counter' });
  }
  return out.sort((a, b) => b.o.id - a.o.id);
}

// ─── Плашки гравців, смуга чисел, звання підсумку (khutory-look.md §4, §9, §12) ───────────────────────

/**
 * Скільки бракує до нагород: «🛤 3/7», «⚔ 1/3». Шлях — щонайменше 5 і на 1 довший за власника; сотня — щонайменше 3
 * козаки і на 1 більше за власника. has — нагорода в цього гравця; near — за крок до нагороди.
 */
export function awardNeed(longest, army, p) {
  const one = (holder, n, base) => {
    const has = !!holder && holder.seat === p.seat;
    const need = Math.max(base, holder && !has ? holder.n + 1 : base);
    return { n, need, has, near: !has && n >= need - 1, pct: Math.min(100, Math.round(n / need * 100)) };
  };
  return {
    road: one(longest ? { seat: longest.seat, n: longest.len } : null, p.road || 0, 5),
    army: one(army ? { seat: army.seat, n: army.n } : null, p.knights || 0, 3),
  };
}

/** Пристані, біля яких стоять хати/містечка гравця: ['any', 'wood', …] без повторів (any — першою). */
export function seatPorts(board, towns, seat) {
  const out = [];
  for (const t of towns || []) {
    if (t.seat !== seat) continue;
    const p = portAt(board, t.v);
    if (p && out.indexOf(p.kind) < 0) out.push(p.kind);
  }
  return out.sort((a, b) => (a === 'any' ? -1 : b === 'any' ? 1 : RES.indexOf(a) - RES.indexOf(b)));
}

/**
 * Що дасть кожне число 2..12 місцю seat: [{ n, pips, res: { wood: 2 }, cnt, hexes, robbed }] і avg — скільки карт
 * у середньому за один кидок. Містечко — ×2; гекс із Кармелюком не родить (robbed — там мої землі під ним).
 * hexes — усі гекси з цим числом (для підсвітки на полі).
 */
export function numberYield(view, seat) {
  const b = view.board, mine = (view.towns || []).filter((t) => t.seat === seat);
  const cells = [];
  let avg = 0;
  for (let n = 2; n <= 12; n++) {
    const cell = { n, pips: n === 7 ? 6 : PIPS[n] || 0, res: {}, cnt: 0, hexes: [], robbed: false };
    if (n !== 7) {
      b.hexes.forEach((hx, i) => {
        if (hx.num !== n) return;
        cell.hexes.push(i);
        const res = TERRAIN_RES[hx.t];
        if (!res) return;
        let k = 0;
        for (const t of mine) if (hx.v.indexOf(t.v) >= 0) k += t.city ? 2 : 1;
        if (!k) return;
        if (i === view.robber) { cell.robbed = true; return; }
        cell.res[res] = (cell.res[res] || 0) + k;
        cell.cnt += k;
      });
    }
    avg += cell.pips / 36 * cell.cnt;
    cells.push(cell);
  }
  return { cells, avg };
}

/**
 * Звання підсумку: [{ seat|null, icon, name, desc }]. Лише чесний лідер (значення > 0, без нічиєї), не більше двох
 * звань на людину (у порядку важливості). Загальні (seat null — «Щасливе число») — у кінці.
 * over — view.over; ctx — { longest, army, players } з виду (нагороди й козаки на кінець партії).
 */
export function sumTitles(over, ctx = {}) {
  const st = (over && over.stats) || {}, S = st.seats || [];
  const out = [], per = {};
  const give = (seat, icon, name, desc) => {
    if (seat == null || (per[seat] || 0) >= 2) return;
    per[seat] = (per[seat] || 0) + 1;
    out.push({ seat, icon, name, desc });
  };
  /** Лідер за f (або найменший, low) без нічиєї; для «найменше» — ще й відрив хоча б 20 % від наступного. */
  const lead = (rows, f, low = false, min = 1) => {
    if (rows.length < 2) return null;
    const sorted = [...rows].sort((a, b) => (low ? f(a) - f(b) : f(b) - f(a)));
    const a = f(sorted[0]), b = f(sorted[1]);
    if (a === b) return null;
    if (!low && a < min) return null;   // «1 обмін» — не звання
    if (low && b - a < Math.max(1, Math.round(b * 0.2))) return null;
    return sorted[0];
  };
  const players = ctx.players || [];
  const kn = (s) => { const p = players.find((x) => x.seat === s); return p ? p.knights || 0 : 0; };
  const forms = (n, a, b, c) => { const m = Math.abs(n) % 100, d = m % 10; return m > 10 && m < 20 ? c : d === 1 ? a : d >= 2 && d <= 4 ? b : c; };

  if (ctx.longest && ctx.longest.seat != null) give(ctx.longest.seat, '🐂', 'Чумак року', 'найдовший шлях — ' + ctx.longest.len + ' ' + forms(ctx.longest.len, 'відтинок', 'відтинки', 'відтинків'));
  else { const r = lead(S, (x) => (x.built || {}).road || 0, false, 4); if (r) give(r.seat, '🐂', 'Чумак року', r.built.road + ' ' + forms(r.built.road, 'шлях', 'шляхи', 'шляхів') + ' прокладено'); }
  const ot = ctx.army && ctx.army.seat != null ? { seat: ctx.army.seat, n: ctx.army.n } : (() => { const r = lead(S, (x) => kn(x.seat), false, 2); return r ? { seat: r.seat, n: kn(r.seat) } : null; })();
  if (ot) give(ot.seat, '⚔', 'Отаман', ot.n + ' ' + forms(ot.n, 'козак', 'козаки', 'козаків') + ' зіграно');
  const rb = lead(S, (x) => x.robbed || 0, false, 2);
  if (rb) give(rb.seat, '😿', 'Улюбленець Кармелюка', 'обкрадали ' + rb.robbed + ' ' + forms(rb.robbed, 'раз', 'рази', 'разів'));
  const tr = lead(S, (x) => x.trades || 0, false, 2);
  if (tr) give(tr.seat, '🤝', 'Майстер торгу', tr.trades + ' ' + forms(tr.trades, 'обмін', 'обміни', 'обмінів'));
  const sl = lead(S, (x) => x.stolen || 0, false, 2);
  if (sl) give(sl.seat, '🥷', 'Кармелюків побратим', sl.stolen + ' ' + forms(sl.stolen, 'раз', 'рази', 'разів') + ' обібрав сусідів');
  const gt = lead(S, (x) => x.got || 0);
  if (gt) give(gt.seat, '🌾', 'Багатий урожай', gt.got + ' ' + forms(gt.got, 'карта', 'карти', 'карт') + ' з полів');
  const bd = lead(S, (x) => ((x.built || {}).town || 0) + ((x.built || {}).city || 0) * 2, false, 3);
  if (bd) { const tw = (bd.built || {}).town || 0, ct = (bd.built || {}).city || 0; give(bd.seat, '🏗', 'Будівничий', tw + ' ' + forms(tw, 'хата', 'хати', 'хат') + ', ' + ct + ' ' + forms(ct, 'містечко', 'містечка', 'містечок')); }
  const lo = lead(S, (x) => x.got || 0, true);
  if (lo) give(lo.seat, '🙈', 'Кубики не люблять', 'лише ' + lo.got + ' ' + forms(lo.got, 'карта', 'карти', 'карт') + ' з полів');
  const di = st.dice || [];
  if (di.length === 11) {
    const mx = Math.max(...di);
    if (mx > 0) {
      const n = di.indexOf(mx) + 2;
      out.push({ seat: null, icon: '🎲', name: 'Щасливе число ' + n, desc: 'випало ' + mx + ' ' + forms(mx, 'раз', 'рази', 'разів') + (n === 7 ? ' — Кармелюк не вилазив із хуторів' : '') });
    }
  }
  return out;
}

/**
 * «Твій момент»: коло, де місце seat найбільше піднялось у таблиці (рівні — більший приріст очок).
 * timeline[i][seat] — очки ЗА НОМЕРАМИ місць; seats — хто грав. null — моменту нема (ні підйому, ні очок).
 * round — номер кола (приблизно, за часткою від rounds), place — місце після нього (1 — перше; tie — ділить його), up/gain.
 */
export function myMoment(timeline, seat, seats, rounds = 0) {
  const tl = timeline || [];
  if (tl.length < 2 || seat == null) return null;
  // Місце в таблиці (рівні — спільне), а для «піднявся» нічия рахується як «позаду»: вийти з нічиї вперед — теж підйом.
  const place = (row) => 1 + seats.filter((s) => s !== seat && (row[s] || 0) > (row[seat] || 0)).length;
  const strict = (row) => 1 + seats.filter((s) => s !== seat && (row[s] || 0) >= (row[seat] || 0)).length;
  let mo = null;
  for (let i = 1; i < tl.length; i++) {
    const up = strict(tl[i - 1]) - strict(tl[i]), gain = (tl[i][seat] || 0) - (tl[i - 1][seat] || 0);
    if (up <= 0 && gain <= 0) continue;
    const sc = Math.max(0, up) * 10 + gain;
    if (!mo || sc > mo.sc) mo = { i, up, gain, sc, place: place(tl[i]), tie: seats.some((s) => s !== seat && (tl[i][s] || 0) === (tl[i][seat] || 0)) };
  }
  if (!mo) return null;
  mo.round = Math.max(1, Math.round((rounds || tl.length - 1) * mo.i / (tl.length - 1)));
  return mo;
}

/** Графік за місцями: лінії лише тих, хто грав (timeline — за НОМЕРАМИ місць, з нулями на порожніх). */
export function chartLines(geom, seats) {
  return seats.filter((s) => geom.lines[s]).map((s) => ({ seat: s, pts: geom.lines[s] }));
}

/** Кармелюк: що буде на кожному дозволеному гексі (бейджі на полі, khutory-look.md §2). Для кожного h з view.me.robber
 *  (або всіх гексів, крім поточного, якщо me нема): own — тут стоїть і моя хата/містечко; victims — чужі гравці з фігурами
 *  на гексі [{ seat, cards }] (за зростанням seat); best — «⭐ Глек радить»: один гекс без моєї хати з жертвами, де
 *  найбільше «крапок» × жертв, а при рівності — де лідер (Σ відкритих очок жертв). Нікого нема — best ні в кого. */
export function robberInfo(view, seat) {
  const b = view && view.board;
  if (!b || !Array.isArray(b.hexes)) return [];
  const list = view.me && Array.isArray(view.me.robber) ? view.me.robber : b.hexes.map((_, i) => i).filter((i) => i !== view.robber);
  const pl = new Map((view.players || []).map((p) => [p.seat, p]));
  const towns = view.towns || [];
  let bi = -1, bs = 0;
  const out = list.map((h, i) => {
    const hx = b.hexes[h];
    const vs = new Set(hx ? hx.v : []);
    const on = towns.filter((t) => vs.has(t.v));
    const own = seat != null && on.some((t) => t.seat === seat);
    const seats = [...new Set(on.map((t) => t.seat).filter((s) => s !== seat))].sort((a, c) => a - c);
    const victims = seats.map((s) => ({ seat: s, cards: (pl.get(s) || {}).cards || 0 }));
    if (!own && victims.length) {
      const score = (PIPS[hx.num] || 0) * victims.length + 0.3 * seats.reduce((a, s) => a + ((pl.get(s) || {}).points || 0), 0)
        + 0.01 * victims.reduce((a, x) => a + x.cards, 0);
      if (score > bs) { bs = score; bi = i; }
    }
    return { h, own, victims, best: false };
  });
  if (bi >= 0) out[bi].best = true;
  return out;
}

/** Будови, ціни яких завжди перед очима (§12 «Ціни будов»), у цьому порядку. */
export const BUILD_KEYS = ['road', 'town', 'city', 'dev'];

/**
 * «Є / бракує» по кожній будові за рукою: [{ k, dots: [{ res, have }], lack: { clay: 1 }, n, ok }].
 * dots — кружечки ціни по одному на карту (у порядку RES), have — чи покриває її рука. hand == null (глядач) —
 * нейтрально: have = null, ok = false, lack = {}. Лише рахунок карт: чи є місце й фігура — каже сервер (me.can / me.why).
 */
export function buildNeeds(hand, costs, keys = BUILD_KEYS) {
  return keys.map((k) => {
    const cost = (costs && costs[k]) || {};
    const dots = [];
    for (const r of RES) for (let i = 0; i < (cost[r] || 0); i++) dots.push({ res: r, have: hand ? i < (hand[r] || 0) : null });
    if (!hand) return { k, dots, lack: {}, n: 0, ok: false };
    const lack = lacking(hand, cost);
    const n = total(lack);
    return { k, dots, lack, n, ok: n === 0 };
  });
}
