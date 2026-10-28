// «Сорочинський ярмарок» — чисті розрахунки картки столу (без DOM): вибір жетонів, повернення зайвого, поступ до гостей,
// позначки крамниць, перелік «бракує», «що закриє крамниця», звання підсумку, графік ⭐. Правила — лише на сервері
// (законність кожного ходу каже сервер у view.me — can/why/take/cards); тут — як це показати й підказати.
// Контракт: docs/games/specs/sorochyntsi.md (§5, §9, §12). Перевірка: node --test web/games/sorochyntsi/dev/hud-calc.test.mjs

import {
  GOODS, BONUSES, TOKENS, GOOD_ICON, GOOD_NAME, GOOD_GEN, GOOD_ACC, DUCAT, TOKEN_WORD, HAND_MAX, TWO_MIN, GUESTS, STARSHYNA,
  card as cardOf, plural,
} from './consts.js';

const n0 = (x) => (Number.isFinite(+x) ? +x : 0);
const sum = (o) => Object.values(o || {}).reduce((a, b) => a + n0(b), 0);

/** Лише додатні значення, у порядку TOKENS (щоб ключі завжди йшли однаково). */
export function compact(o) {
  const r = {};
  for (const g of [...TOKENS, 'fern']) if (n0(o && o[g]) > 0) r[g] = n0(o[g]);
  return r;
}

/** «a, b й c» — як у журналі сервера. */
export function joinUk(list) {
  if (list.length <= 1) return list.join('');
  return list.slice(0, -1).join(', ') + ' й ' + list[list.length - 1];
}

/** «2 зілля й 1 глини» (родовий після числа, порядок GOODS; дукати — через plural). */
export function goodsPhrase(o) {
  const parts = [];
  for (const g of [...GOODS, 'fern']) if (n0(o && o[g]) > 0) parts.push(o[g] + ' ' + GOOD_GEN[g]);
  if (n0(o && o.gold) > 0) parts.push(o.gold + ' ' + plural(o.gold, ...DUCAT));
  return joinUk(parts);
}

/** «рибу, зілля й глину» (знахідний, без чисел) або «2 вовни» — як журнал сервера для take. */
export function takePhrase(got) {
  const c = compact(got);
  const ks = Object.keys(c);
  if (ks.length === 1 && c[ks[0]] > 1) return c[ks[0]] + ' ' + GOOD_GEN[ks[0]];
  return joinUk(ks.map((g) => GOOD_ACC[g]));
}

/** Значки з числами: «🏺1 🪙1». */
export function iconsText(o, sep = ' ') {
  const c = compact(o);
  return Object.keys(c).map((g) => GOOD_ICON[g] + c[g]).join(sep);
}

/** Значки без чисел, повтором: «🐟🌿🏺» / «🐑🐑». */
export function iconsRepeat(o) {
  const c = compact(o);
  return Object.keys(c).map((g) => GOOD_ICON[g].repeat(Math.min(4, c[g]))).join('');
}

// ─── Взяти товари (§2.3) ──────────────────────────────────────────────────────────────────────────────────────────

/**
 * Стан вибору жетонів у панелі ходу.
 * @param take  me.take з виду: { piles: [...], need, two: [...] }
 * @param pick  { fish: 1, herbs: 1 } — що обрано в HUD
 * @param bank  view.bank — лише для тексту «з купи 3 два не беруть»
 * @returns { ok, mode: 'diff'|'two'|null, total, goods, why, hint, rule }
 */
export function takeState(take, pick, bank) {
  const t = take || { piles: [], need: 0, two: [] };
  const piles = t.piles || [], two = t.two || [], need = n0(t.need);
  const p = compact(pick);
  delete p.gold; delete p.fern;
  const ks = Object.keys(p), total = sum(p);
  const rule = need >= 3 ? '3 різні або 2 однакові (купа ≥ ' + TWO_MIN + ')'
    : need > 0 ? 'по одному з кожної непорожньої купи (' + need + ')' + (two.length ? ' або 2 однакові' : '') : 'купи порожні';
  const res = { ok: false, mode: null, total, goods: [], why: '', hint: '', rule };
  if (!piles.length) { res.why = 'Купи порожні'; return res; }
  if (!total) { res.hint = need >= 3 ? 'обери 3 різні товари або 2 однакові' : 'обери ' + need + ' ' + plural(need, 'товар', 'товари', 'товарів'); return res; }
  for (const g of ks) if (!piles.includes(g)) { res.why = 'купа «' + GOOD_NAME[g] + '» порожня'; return res; }
  const dbl = ks.filter((g) => p[g] >= 2);
  if (dbl.length) {
    const g = dbl[0];
    if (ks.length > 1 || p[g] > 2) { res.why = 'або 2 однакові, або різні — не разом'; return res; }
    if (!two.includes(g)) {
      const n = bank && bank[g] != null ? bank[g] : null;
      res.why = n != null ? 'з купи ' + n + ' два не беруть (треба ≥ ' + TWO_MIN + ')' : 'два однакові — лише з купи, де ≥ ' + TWO_MIN;
      return res;
    }
    return { ...res, ok: true, mode: 'two', goods: [g, g] };
  }
  if (ks.length < need) {
    const left = need - ks.length;
    res.hint = 'ще ' + left + ' ' + plural(left, 'різний', 'різні', 'різних');
    // Один обраний і з нього можна два — підкажемо й це.
    if (ks.length === 1 && two.includes(ks[0])) res.hint += ' — або ще ' + GOOD_ICON[ks[0]] + ' (два однакові)';
    return res;
  }
  if (ks.length > need) { res.why = 'забагато: бери ' + need; return res; }
  return { ...res, ok: true, mode: 'diff', goods: GOODS.filter((g) => p[g]) };
}

/**
 * Додати один жетон до вибору (тап по кнопці чи купі). Вертає { pick, why } — pick новий (або той самий, якщо не можна).
 */
export function addPick(take, pick, g, bank) {
  const t = take || { piles: [], need: 0, two: [] };
  const p = compact(pick);
  if (g === 'gold') return { pick: p, why: 'Дукат — лише разом із бронею' };
  if (!(t.piles || []).includes(g)) return { pick: p, why: 'Купа порожня' };
  const ks = Object.keys(p), total = sum(p);
  if (p[g]) {
    // Другий того самого: лише коли він один обраний і купа ≥ 4.
    if (ks.length === 1 && p[g] === 1) {
      if ((t.two || []).includes(g)) return { pick: { [g]: 2 }, why: '' };
      const n = bank && bank[g] != null ? bank[g] : null;
      return { pick: p, why: n != null ? 'з купи ' + n + ' два не беруть' : 'два однакові — лише з купи ≥ ' + TWO_MIN };
    }
    return { pick: p, why: p[g] >= 2 ? 'більше двох однакових не беруть' : 'два однакові — лише без інших товарів' };
  }
  if (ks.some((k) => p[k] >= 2)) return { pick: { [g]: 1 }, why: '' };   // були «два однакові» — почнемо різні наново
  if (ks.length >= n0(t.need)) {
    if (!ks.length) return { pick: p, why: 'Купи порожні' };
    return { pick: p, why: 'уже ' + total + ' — зніми один «−», щоб узяти ' + GOOD_ICON[g] };
  }
  return { pick: compact({ ...p, [g]: 1 }), why: '' };
}

/** Мінус один жетон із вибору. */
export function subPick(pick, g) {
  const p = compact(pick);
  if (p[g]) p[g]--;
  return compact(p);
}

/** «стане 12 жетонів — 2 доведеться повернути» або ''. */
export function overflowText(ntok, add) {
  const after = n0(ntok) + n0(add);
  if (after <= HAND_MAX) return '';
  const n = after - HAND_MAX;
  return 'стане ' + after + ' ' + plural(after, ...TOKEN_WORD) + ' — ' + n + ' доведеться повернути';
}

// ─── Повернути зайве (§2.6) ──────────────────────────────────────────────────────────────────────────────────────

/**
 * Готова пропозиція повернення n жетонів: спершу ті, яких найбільше понад потребу (keep — скільки кожного треба цілі),
 * рівність — раніший у GOODS; дукати — останніми.
 */
export function suggestDiscard(tokens, n, keep) {
  const t = { ...compact(tokens) };
  const k = keep || {};
  const give = {};
  let left = n0(n);
  while (left > 0) {
    let best = null, bv = -Infinity;
    for (const g of GOODS) {
      const have = n0(t[g]);
      if (have <= 0) continue;
      const over = have - n0(k[g]);
      // Понад потребу — першими (за надлишком), далі — просто найчисленніші.
      const val = over > 0 ? 1000 + over * 10 + have : have;
      if (val > bv) { bv = val; best = g; }
    }
    if (!best) {
      if (n0(t.gold) > 0) best = 'gold';
      else break;
    }
    t[best]--; give[best] = (give[best] || 0) + 1; left--;
  }
  return compact(give);
}

/** Чи план повернення законний: рівно n, не більше, ніж є. */
export function discardOk(tokens, plan, n) {
  const p = compact(plan);
  for (const g of Object.keys(p)) if (p[g] > n0(tokens && tokens[g])) return false;
  return sum(p) === n0(n);
}

// ─── Гості (§1.2) ────────────────────────────────────────────────────────────────────────────────────────────────

/** Вимога гостя за id: { goods: { linen: 4, ... } }. */
export function guestReq(id) {
  const g = GUESTS[id];
  if (!g) return {};
  const r = {};
  GOODS.forEach((k, i) => { if (g[2][i]) r[k] = g[2][i]; });
  return r;
}

/** Поступ гравця до гостя: { items: [{ g, have, need, ok }], done, left } — left = Σ max(0, need − bonus). */
export function guestProgress(id, bonus) {
  const req = guestReq(id);
  const items = Object.keys(req).map((g) => {
    const have = Math.min(n0(bonus && bonus[g]), req[g]);
    return { g, have, need: req[g], ok: have >= req[g] };
  });
  const left = items.reduce((a, x) => a + (x.need - x.have), 0);
  return { items, done: left === 0, left };
}

export const guestName = (id) => (GUESTS[id] ? GUESTS[id][1] : 'гість');

// ─── Позначки крамниць (§12) ─────────────────────────────────────────────────────────────────────────────────────

/** me.cards → { [id]: 'buy' | 'gold' | 'near' } для sc.setMarks. */
export function marks(cards) {
  const m = {};
  for (const id of Object.keys(cards || {})) {
    const c = cards[id];
    if (!c) continue;
    if (c.ok) m[id] = c.pay && n0(c.pay.gold) > 0 ? 'gold' : 'buy';
    else if (n0(c.short) > 0 && n0(c.short) <= 2) m[id] = 'near';
  }
  return m;
}

/** Ціна мінус мої знижки: { linen: 1, ... } (лише > 0). */
export function costAfter(id, bonus) {
  const c = cardOf(id);
  if (!c) return {};
  const r = {};
  for (const g of GOODS) { const x = Math.max(0, c.cost[g] - n0(bonus && bonus[g])); if (x) r[g] = x; }
  return r;
}

/**
 * Що закриє купівля крамниці: для булави (товар зі знижкою 0), для гостей столу, для старшини.
 * @returns [{ kind: 'bulava'|'guest'|'starshyna', text }]
 */
export function closes(id, view, seat) {
  const c = cardOf(id);
  if (!c || !view) return [];
  const p = (view.players || []).find((x) => x.seat === seat);
  if (!p) return [];
  const b = p.bonus || {};
  const out = [];
  if (view.bulava == null && !n0(b[c.bonus])) out.push({ kind: 'bulava', text: GOOD_ICON[c.bonus] + ' для булави' });
  if (c.bonus !== 'fern') {
    for (const gs of view.guests || []) {
      if (gs.by != null) continue;
      const req = guestReq(gs.id);
      const need = req[c.bonus];
      if (need && n0(b[c.bonus]) < need) out.push({ kind: 'guest', text: (n0(b[c.bonus]) + 1) + ' з ' + need + ' ' + GOOD_ICON[c.bonus] + ' — ' + guestName(gs.id) });
    }
  }
  if (c.k) {
    const mine = n0(p.icons) + 1;
    const s = view.starshyna;
    if (s && s.seat === seat) out.push({ kind: 'starshyna', text: '⚔ ' + mine + ' — старшина й далі твоя' });
    else if (mine >= STARSHYNA.icons && (!s || mine > n0(s.icons))) out.push({ kind: 'starshyna', text: '🎖 Січова старшина стане твоєю (+3 ⭐)' });
    else out.push({ kind: 'starshyna', text: '⚔ ' + mine + ' з ' + Math.max(STARSHYNA.icons, s ? n0(s.icons) + 1 : 0) + ' до старшини' });
  }
  return out;
}

/** Скільки ⚔ треба, щоб перебрати старшину (мінімум 3; строго більше за власника). */
export function starshynaNeed(view, seat) {
  const s = view && view.starshyna;
  if (s && s.seat === seat) return 0;
  return Math.max(STARSHYNA.icons, s ? n0(s.icons) + 1 : 0);
}

// ─── Порада Глека (me.tip) ──────────────────────────────────────────────────────────────────────────────────────

/** «Глек би: взяв 🐟🌿🏺» тощо. names — (id) → назва крамниці. */
export function tipText(tip, names) {
  if (!tip) return '';
  const nm = (id) => '«' + (names ? names(id) : (cardOf(id) || {}).name || id) + '»';
  const roman = { 1: 'I', 2: 'II', 3: 'III' };
  switch (tip.act) {
    case 'take': {
      const got = {};
      for (const g of tip.goods || []) got[g] = (got[g] || 0) + 1;
      return 'взяв ' + iconsRepeat(got);
    }
    case 'buy': return 'купив ' + nm(tip.card);
    case 'reserve': return tip.card != null ? 'забронював ' + nm(tip.card) : 'забронював наосліп з колоди ' + roman[tip.tier];
    case 'pass': return 'пропустив хід';
    case 'discard': return 'повернув ' + iconsText(tip.give);
    case 'guest': return 'покликав ' + guestName(tip.guest);
    default: return '';
  }
}

/** Порада take → вибір для HUD: { fish: 1, herbs: 1, clay: 1 }. */
export function tipPick(tip) {
  const p = {};
  if (tip && tip.act === 'take') for (const g of tip.goods || []) p[g] = (p[g] || 0) + 1;
  return compact(p);
}

// ─── Скільки ходів лишилось в останньому колі ───────────────────────────────────────────────────────────────────

/** Чи матиме seat ще хід у поточному (останньому) колі: коло — від first по order; turn — хто ходить зараз. */
export function turnsLeft(view, seat) {
  const order = view.order || [];
  const n = order.length;
  if (!n || view.turn == null) return 0;
  const f = order.indexOf(view.first);
  const pos = (s) => ((order.indexOf(s) - f) % n + n) % n;
  if (order.indexOf(seat) < 0) return 0;
  return pos(seat) >= pos(view.turn) ? 1 : 0;
}

// ─── Підсумок (§9, §12) ──────────────────────────────────────────────────────────────────────────────────────────

/**
 * Звання: чесний лідер (> 0, без нічиєї), щонайбільше 2 на гравця, у порядку списку.
 * @returns [{ key, icon, name, seat, why }]
 */
export function titles(stats) {
  const S = (stats && stats.seats) || [];
  const out = [], per = {};
  const give = (key, icon, name, seat, why) => {
    if (seat == null || (per[seat] || 0) >= 2) return;
    per[seat] = (per[seat] || 0) + 1;
    out.push({ key, icon, name, seat, why });
  };
  const leader = (f, pos = true) => {
    let best = null, bv = null, tie = false;
    for (const x of S) {
      const val = f(x);
      if (val == null || !Number.isFinite(val)) continue;
      if (pos && val <= 0) continue;
      if (bv == null || (pos ? val > bv : val < bv)) { bv = val; best = x.seat; tie = false; } else if (val === bv) tie = true;
    }
    return tie || best == null ? null : { seat: best, v: bv };
  };
  const bought = (x) => (x.buys || []).reduce((a, b) => a + n0(b), 0);
  let L;
  if ((L = leader((x) => n0(x.tokens)))) give('tokens', '🧺', 'Скупник', L.seat, L.v + ' ' + plural(L.v, ...TOKEN_WORD) + ' з куп');
  if ((L = leader((x) => n0(x.reserves)))) give('reserves', '📌', 'Майстер броні', L.seat, L.v + ' ' + plural(L.v, 'бронь', 'броні', 'броней'));
  if ((L = leader((x) => n0(x.guests)))) give('guests', '🏠', 'Гостинний', L.seat, L.v + ' ' + plural(L.v, 'гість', 'гості', 'гостей'));
  if ((L = leader((x) => n0(x.icons)))) give('icons', '⚔', 'Козацька душа', L.seat, L.v + ' ⚔');
  if ((L = leader((x) => (x.firstT3 == null ? null : x.firstT3), false))) give('firstT3', '👑', 'Перший славний', L.seat, 'III рівень уже в колі ' + L.v);
  if ((L = leader((x) => (bought(x) >= 10 ? n0(x.goldSpent) : null), false))) give('gold', '🪙', 'Ощадливий', L.seat, L.v + ' ' + plural(L.v, ...DUCAT) + ' на ' + bought(S.find((x) => x.seat === L.seat)) + ' крамниць');
  return out;
}

/**
 * Графік ⭐ за колами: timeline[i][seat] (за НОМЕРАМИ місць). Вертає { max, y(v), lines: [{ seat, pts: [[x, y]] }] }.
 */
export function chart(timeline, seats, W, H, target) {
  const tl = timeline || [];
  let max = Math.max(1, n0(target));
  for (const row of tl) for (const s of seats) max = Math.max(max, n0(row && row[s]));
  max = Math.ceil(max / 2) * 2;
  const L = 24, R = 26, T = 10, B = 20;
  const n = Math.max(1, tl.length - 1);
  const x = (i) => +(L + (W - L - R) * i / n).toFixed(1);
  const y = (v) => +(H - B - (H - B - T) * v / max).toFixed(1);
  const lines = seats.map((s) => ({ seat: s, pts: tl.map((row, i) => [x(i), y(n0(row && row[s]))]) }));
  return { max, x, y, lines, targetY: target ? y(target) : null };
}

/** Рядок банку для журналу/тостів: скільки жетонів у руці гравця. */
export const ntokOf = (p) => (p ? (p.ntok != null ? p.ntok : sum(p.tokens)) : 0);

export { sum as total, BONUSES };
