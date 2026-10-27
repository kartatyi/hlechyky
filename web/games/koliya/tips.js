// «Колія» — підказки новачкові: бульбашка над гравцем «що робити далі» + стрілка на клітинку. Власник: K10.
// Контракт:
//  pickTips(state, memory, opts?) → [{ p, id, text, x, y }] — чиста (без DOM): по одній підказці на гравця;
//    x,y — клітинка, куди вказати (або null). memory — будь-який об'єкт ({} на старті), pickTips сама веде в ньому
//    що вже зроблено (done[p][id] — більше не показується, і після рестарту теж) і знімок минулого виклику.
//    opts.players — індекси гравців, яким підказувати (типово всі, крім тих, що вийшли); боти — не треба.
//    opts.keys(p) → { use, throw } — назви кнопок гравця за пристроєм (K16: пад — «A»/«B», клавіатура — Space/Q чи
//    Enter/правий Ctrl); без нього — useKey/throwKey за номером. createTips бере їх з game.input.hasPad(p).
//  K16: ще підказки — болото, брід (мілина в річковій долині), гуси (geese='on'), розумний приціл (aim='smart'),
//    кидок (throw='on'). «Довідкові» (болото, брід, приціл) висять INFO_TICKS і більше не показуються.
//  createTips(root, { enabled }) → { update(state, game, view?), setEnabled(b), dispose() }
//    update — щокадру; логіку рахує раз на тик, бульбашки водить щокадру. Проєкція клітинки на екран —
//    (game.view ?? view).project(x, y): x,y у клітинках (дробові, центр клітинки = +0,5) → { x, y } у CSS px вікна
//    або null (поза кадром). Нема project — бульбашки стосом у правому нижньому куті з кольором гравця й стрілкою.
//    Підказуємо лише локальним (game.local, типово всім); при game.paused (стартовий екран HUD) — сховано.
// HUD (K9) уже кричить тривоги («котел», «рейки закінчуються») — тут лише «що робити руками».
import { U, G, O, I, TPS, wagonPos, canLay, canUnlay } from './rules.js';
import { t as tt } from './theme.js';   // K25: слова теми «Чумацький шлях»

const COLORS = ['#ffd23f', '#ff5fa2', '#5fd3ff', '#b6ff5f'];   // як у HUD/debug2d
const ROLE_TICKS = 8 * TPS;      // підказка ролей висить до 8 с, якщо гравець не взявся до діла раніше
const HEAT_TIP = 600;            // з якого нагріву вчимо гасити (HUD тривожить з 750)
const NEAR = 3;                  // «поруч» для дров на землі, клітинок
const SEARCH = 14;               // радіус пошуку дерева/скелі/води, клітинок

/** Кнопка дії гравця p (заглушка input.js: гравець 0 — Space, 1 — Enter) */
export const useKey = (p) => (p === 1 ? 'Enter' : 'Space');
export const throwKey = (p) => (p === 1 ? 'правий Ctrl' : 'Q');
/** K16: кнопки гравця p за пристроєм; hasPad(p) — з input.js, local — скільки локальних гравців */
export function keysFor(p, hasPad, local = 1) {
  if (hasPad) return { use: 'A', throw: 'B' };
  if (local <= 1) return { use: 'Space', throw: 'Q' };
  return { use: useKey(p), throw: throwKey(p) };
}
const INFO_TICKS = 5 * TPS;      // довідкова підказка висить 5 с
const THROW_FAR = 2, THROW_NEAR = 4;   // з якої відстані до складу радимо кинути (клітинок)

const KIDS = ['', 'одинак', 'двоє', 'троє', 'четверо'];

// ---------------- дрібниці ----------------
const cellOf = (pl) => ({ x: Math.floor((pl.x ?? 0) / U), y: Math.floor((pl.y ?? 0) / U) });
const cellXY = (s, i) => ({ x: i % s.w, y: (i / s.w) | 0 });
const wagonCell = (s, k) => { const w = wagonPos(s, k); return { x: Math.floor(w.x / U), y: Math.floor(w.y / U) }; };
const d2 = (a, b) => (a.x - b.x) ** 2 + (a.y - b.y) ** 2;

/** Найближча до from клітинка в квадраті радіуса r, де pred(i) — або null */
function nearest(s, from, r, pred) {
  let best = null, bd = Infinity;
  const x0 = Math.max(0, from.x - r), x1 = Math.min(s.w - 1, from.x + r);
  const y0 = Math.max(0, from.y - r), y1 = Math.min(s.h - 1, from.y + r);
  for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
    if (!pred(y * s.w + x)) continue;
    const d = (x - from.x) ** 2 + (y - from.y) ** 2;
    if (d < bd) { bd = d; best = { x, y }; }
  }
  return best;
}
/** Найближча річ k на підлозі по всій смузі (речі рідкі — повний прохід дешевий) */
function nearestDrop(s, from, k) {
  let best = null, bd = Infinity;
  for (let i = 0; i < s.drop.length; i++) {
    if (s.drop[i] !== k) continue;
    const c = cellXY(s, i), d = d2(c, from);
    if (d < bd) { bd = d; best = c; }
  }
  return best;
}

/**
 * Що перед колією: { last, front, kind, dead }
 *  front — клітинка, куди найлогічніше класти наступну рейку; kind: 'ok' | 'water' | 'blocked' (дерево/скеля/речі)
 *  dead — глухий кут: жодного сусіда кінця колії, крім стіни, краю й самої колії
 */
export function pathFront(s) {
  const n = s.path?.length ?? 0;
  if (!n) return null;
  const last = cellXY(s, s.path[n - 1]);
  let dx = 1, dy = 0;
  if (n >= 2) { const pv = cellXY(s, s.path[n - 2]); dx = last.x - pv.x; dy = last.y - pv.y; }
  // прямо, потім повороти (той, що до середини смуги, — першим)
  const mid = s.h >> 1;
  const turns = dx ? [[0, Math.sign(mid - last.y) || 1], [0, -(Math.sign(mid - last.y) || 1)]] : [[1, 0], [-1, 0]];
  const cand = [[dx, dy], ...turns];
  const recent = new Set(s.path.slice(-8));
  const found = { ok: null, water: null, blocked: null };
  for (const [cx, cy] of cand) {
    const x = last.x + cx, y = last.y + cy;
    if (x < 0 || y < 0 || x >= s.w || y >= s.h) continue;
    const i = y * s.w + x;
    if (s.obj[i] === O.WALL || recent.has(i)) continue;
    let kind;
    if (canLay(s, x, y)) kind = 'ok';
    else if (s.ground[i] === G.WATER && s.obj[i] === O.NONE) kind = 'water';
    else kind = 'blocked';
    if (!found[kind]) found[kind] = { x, y };
  }
  const kind = found.ok ? 'ok' : found.water ? 'water' : found.blocked ? 'blocked' : null;
  return { last, front: kind ? found[kind] : null, kind, dead: !kind };
}

/** Біом клітинки x (s.zones від K7; null — галявина/нема) */
function zoneKind(s, x) {
  for (const z of s.zones ?? []) if (x >= z.x0 && x < z.x1) return z.kind;
  return null;
}

// ---------------- пам'ять ----------------
function initMem(m) {
  m.done ??= [];        // done[p] = { id: тик, коли зроблено }
  m.since ??= [];       // since[p] = { id, t } — що показуємо й з якого тику
  m.pl ??= [];          // знімок гравців минулого виклику
  m.g ??= null;         // знімок спільного
  return m;
}
const isDone = (m, p, id) => !!m.done[p]?.[id];
/** Зроблено руками (не відвисіло за часом) — лише за таке хвалимо «Є!» */
export const didIt = (m, p, id) => (m?.done?.[p]?.[id] ?? 0) > 0;
function markDone(m, p, id, tick) { (m.done[p] ??= {})[id] ??= tick + 1; }

/** Звіряємо з минулим викликом: що гравці щойно зробили з порадженого */
function learn(s, m) {
  const t = s.train ?? {}, st = s.stats ?? {}, tick = s.tick ?? 0;
  const P = s.players ?? [];
  // рестарт (тик пішов назад) — знімки не порівнюємо, але зроблене пам'ятаємо
  if (m.g && tick < m.g.tick) { m.g = null; m.pl = []; }
  const g = m.g;
  const all = (id) => { for (let p = 0; p < P.length; p++) markDone(m, p, id, tick); };
  P.forEach((pl, p) => {
    if (!pl || pl.out) return;
    const h = pl.hold ?? { k: 0, n: 0 };
    if (h.k === I.AXE) markDone(m, p, 'axe', tick);
    if (h.k === I.PICK) markDone(m, p, 'pick', tick);
    if (h.k === I.RAIL) markDone(m, p, 'rails', tick);
    if (h.k === I.WOOD || h.k === I.IRON) markDone(m, p, 'grab', tick);
    if (h.k === I.AXE && p % 2 === 0) markDone(m, p, 'role', tick);
    if (h.k === I.RAIL && p % 2 === 1) markDone(m, p, 'role', tick);
    const pv = m.pl[p];
    if (pv && g) {
      const less = h.k !== pv.k || (h.n ?? 0) < pv.n;
      if ((pv.k === I.WOOD || pv.k === I.IRON) && less && (t.wood ?? 0) + (t.iron ?? 0) > g.wood + g.iron) markDone(m, p, 'store', tick);
      if (pv.k === I.RAIL && less && (s.path?.length ?? 0) > g.pathLen) markDone(m, p, 'lay', tick);
      // удар по тій самій клітинці перед собою: hp упало або дерево/скеля зникли
      if (pv.fc >= 0 && pv.fo && (s.hp[pv.fc] < pv.fh || s.obj[pv.fc] !== pv.fo)) {
        if (pv.fo === O.TREE && pv.k === I.AXE) markDone(m, p, 'chop', tick);
        if (pv.fo === O.ROCK && pv.k === I.PICK) markDone(m, p, 'mine', tick);
      }
    }
  });
  for (const e of s.ev ?? []) {
    if (e.p === undefined) continue;
    if (e.t === 'chop') markDone(m, e.p, 'chop', tick);
    if (e.t === 'rock') markDone(m, e.p, 'mine', tick);
    if (e.t === 'douse') all('heat');
    if (e.t === 'scare') markDone(m, e.p, 'goose', tick);   // K16
    if (e.t === 'throw') markDone(m, e.p, 'throw', tick);   // K16
  }
  if (g) {
    if ((st.douses ?? 0) > g.douses) all('heat');
    if ((st.bridges ?? 0) > g.bridges) all('bridge');
    if ((s.path?.length ?? 0) < g.pathLen) all('dead');
  }
}

function snapshot(s, m) {
  const t = s.train ?? {}, st = s.stats ?? {};
  m.g = { tick: s.tick ?? 0, wood: t.wood ?? 0, iron: t.iron ?? 0, pathLen: s.path?.length ?? 0,
          douses: st.douses ?? 0, bridges: st.bridges ?? 0 };
  m.pl = (s.players ?? []).map((pl) => {
    if (!pl) return null;
    const c = cellOf(pl), fx = c.x + (pl.fx ?? 0), fy = c.y + (pl.fy ?? 0);
    const ok = fx >= 0 && fy >= 0 && fx < s.w && fy < s.h;
    const fc = ok ? fy * s.w + fx : -1;
    return { k: pl.hold?.k ?? 0, n: pl.hold?.n ?? 0, fc, fo: ok ? s.obj[fc] : 0, fh: ok ? s.hp[fc] : 0 };
  });
}

// ---------------- вибір ----------------
/**
 * Підказки на цей момент. Порядок важливості для гравця: спільна біда (котел, глухий кут, річка) → ролі на старті →
 * що в руках → що взяти. Спільну біду отримує один — той, кому найзручніше.
 */
export function pickTips(s, memory = {}, opts = {}) {
  const m = initMem(memory);
  if (!s || !s.players) return [];
  learn(s, m);
  const out = [];
  if (s.status === 'run') {
    const P = s.players;
    const who = (opts.players ?? P.map((_, i) => i)).filter((p) => P[p] && !P[p].out);
    const taken = new Map();   // p → підказка
    const give = (p, id, text, at) => { taken.set(p, { p, id, text, x: at?.x ?? null, y: at?.y ?? null }); };
    const free = (p, id) => !taken.has(p) && !isDone(m, p, id);
    const keys = (p) => (typeof opts.keys === 'function' ? opts.keys(p) : null) ?? { use: useKey(p), throw: throwKey(p) };
    const useK = (p) => keys(p).use;
    const tick = s.tick ?? 0;
    // K16: довідкова — висить сумарно dur тиків (з перервами теж), далі «зроблено» без «Є!» (shared — одразу всім)
    const info = (p, id, text, at, shared = false, dur = INFO_TICKS) => {
      const rec = ((m.info ??= [])[p] ??= {})[id] ??= { n: 0, t: -1 };
      if (rec.t !== tick) { rec.n += rec.t >= 0 && m.g && rec.t === m.g.tick ? tick - rec.t : 1; rec.t = tick; }
      if (rec.n > dur) {
        for (const q of shared ? P.map((_, i) => i) : [p]) (m.done[q] ??= {})[id] = -1;
        return false;
      }
      give(p, id, text, at); return true;
    };
    const nearestOf = (cands, at) => cands.reduce((a, b) => (d2(cellOf(P[b]), at) < d2(cellOf(P[a]), at) ? b : a));
    const t = s.train ?? {};
    const pf = pathFront(s);
    const loco = wagonCell(s, 0), store = wagonCell(s, 1), craft = wagonCell(s, 2);

    // 1) котел: вчимо гасити тому, хто з відром, або найближчому до відра
    if ((t.heat ?? 0) >= HEAT_TIP || (t.fire ?? 0) > 0) {
      const cands = who.filter((p) => free(p, 'heat'));
      const holds = (k) => cands.find((p) => P[p].hold?.k === k);
      let p = holds(I.BUCKET_FULL) ?? holds(I.BUCKET);
      const bucket = p === undefined ? nearestDrop(s, loco, I.BUCKET) : null;
      if (p === undefined && cands.length) {
        const aim = bucket ?? loco;
        const empty = cands.filter((q) => !P[q].hold?.k);
        p = (empty.length ? empty : cands).reduce((a, b) => (d2(cellOf(P[b]), aim) < d2(cellOf(P[a]), aim) ? b : a));
      }
      if (p !== undefined) {
        const k = P[p].hold?.k, me = cellOf(P[p]), key = useK(p);
        if (k === I.BUCKET_FULL) give(p, 'heat', tt('tip.heatFull', `Котел гріється! Повне відро — на паротяг, ${key} 💦`, key), loco);
        else if (k === I.BUCKET) give(p, 'heat', tt('tip.heatEmpty', `Котел гріється! До води й ${key} — набрати відро`, key),
          nearest(s, me, SEARCH, (i) => s.ground[i] === G.WATER) ?? loco);
        else if (k) give(p, 'heat', tt('tip.heatDrop', 'Котел гріється! Кинь що в руках — і бігом по відро 🪣'), bucket ?? loco);
        else give(p, 'heat', tt('tip.heat', 'Котел гріється! Відро → вода → паротяг'), bucket ?? loco);
      }
    }

    // 2) глухий кут: розібрати останню рейку (лише поки потяг до неї не доїхав)
    if (pf?.dead && canUnlay(s)) {
      const cands = who.filter((p) => free(p, 'dead') && (!P[p].hold?.k || P[p].hold.k === I.RAIL));
      if (cands.length) {
        const p = cands.reduce((a, b) => (d2(cellOf(P[b]), pf.last) < d2(cellOf(P[a]), pf.last) ? b : a));
        give(p, 'dead', tt('tip.dead', `Глухий кут? Розбери останню рейку: порожні руки й ${useK(p)}`, useK(p)), pf.last);
      }
    }

    // 3) річка перед колією: місток із дров
    if (pf?.kind === 'water') {
      const cands = who.filter((p) => free(p, 'bridge'));
      const p = cands.find((q) => P[q].hold?.k === I.WOOD) ?? cands.find((q) => !P[q].hold?.k || P[q].hold.k === I.RAIL);
      if (p !== undefined) give(p, 'bridge', tt('tip.bridge', 'Річка: поклади дрова на воду — буде місток'), pf.front);
    }

    // K16 3а) гуси: хтось тягне інструмент — найближчому, хто ще не лякав
    if (s.params?.geese === 'on' && s.geese?.length) {
      const g = s.geese.find((q) => q.hold) ?? s.geese.find((q) => q.mode === 'go');
      const cands = g ? who.filter((p) => free(p, 'goose')) : [];
      if (cands.length) {
        const at = { x: Math.floor(g.x / U), y: Math.floor(g.y / U) };
        const p = nearestOf(cands, at);
        info(p, 'goose', g.hold ? `Гуска тягне інструмент! Наздожени й тисни ${useK(p)} — впустить` : `Гуска йде красти! Підбіжи й ${useK(p)} — налякаєш`, at, false, 2 * INFO_TICKS);
      }
    }

    // K16 3б) брід: колія впирається в мілину річкової долини — рейки кладуться без мосту
    if (pf?.kind === 'ok' && pf.front && s.ground[pf.front.y * s.w + pf.front.x] === G.SAND && zoneKind(s, pf.front.x) === 'river'
        && nearest(s, pf.front, 2, (i) => s.ground[i] === G.WATER)) {
      const cands = who.filter((p) => free(p, 'ford'));
      if (cands.length) {
        const p = cands.find((q) => P[q].hold?.k === I.RAIL) ?? nearestOf(cands, pf.front);
        info(p, 'ford', tt('tip.ford', 'Брід: тут мілина — рейки кладуться без мосту'), pf.front, true);
      }
    }

    // 4) решта — кожному своє
    const ahead = (s.path.length - 1) - (t.pos ?? 0) / U;   // клітинок колії перед головою
    for (const p of who) {
      if (taken.has(p)) continue;
      const pl = P[p], h = pl.hold ?? { k: 0, n: 0 }, me = cellOf(pl), key = useK(p);
      // K16: болото під ногами — раз пояснити
      if (free(p, 'swamp') && s.ground[me.y * s.w + me.x] === G.SWAMP && info(p, 'swamp', 'Болото — повільно, але рейки класти можна', null)) continue;
      const axe = () => nearestDrop(s, me, I.AXE), pick = () => nearestDrop(s, me, I.PICK);
      const near = (k) => nearest(s, me, NEAR, (i) => s.drop[i] === k);
      // ролі: двоє і більше — на старті кожному своя справа
      if (P.filter((q) => q && !q.out).length >= 2) {
        if (free(p, 'role')) {
          const since = m.since[p];
          if (since?.id === 'role' && (s.tick ?? 0) - since.t >= ROLE_TICKS) (m.done[p] ??= {}).role = -1;   // відвисіла — без «Є!»
          else {
            const kids = KIDS[P.filter((q) => q && !q.out).length] ?? 'гурт';
            if (p % 2 === 0) give(p, 'role', tt('tip.roleChop', `Вас ${kids}: один рубає, другий кладе. Ти — рубаєш 🪓`, kids), axe());
            else give(p, 'role', tt('tip.roleLay', `Вас ${kids}: один рубає, другий кладе. Ти — кладеш рейки 🛤`, kids), craft);
            continue;
          }
        }
      }
      // K16: розумний приціл — раз пояснити, коли ролі вже позаду
      if (s.params?.aim === 'smart' && free(p, 'aim') && (isDone(m, p, 'role') || P.filter((q) => q && !q.out).length < 2)
          && info(p, 'aim', `Кнопка ${key} сама знаходить справу поруч — дивись на підсвітку`, pl.aim ?? null)) continue;
      if (h.k === I.RAIL) {
        if (free(p, 'lay') && pf?.front) {
          give(p, 'lay', pf.kind === 'blocked' ? tt('tip.layBlocked', 'Клади рейку перед колією — та спершу розчисть місце') : tt('tip.lay', `Клади рейку перед колією: стань поруч і ${key}`, key), pf.front);
        }
        continue;
      }
      if (h.k === I.WOOD || h.k === I.IRON) {
        if (free(p, 'store')) give(p, 'store', tt('tip.store', `Неси ${h.k === I.WOOD ? 'дрова' : 'залізо'} на склад — зелений вагон`, h.k === I.WOOD ? 'дрова' : 'залізо'), store);
        else if (s.params?.throw === 'on' && free(p, 'throw')) {   // K16: кидок у склад з кількох кроків
          const d = Math.max(Math.abs(store.x - me.x), Math.abs(store.y - me.y));
          if (d >= THROW_FAR && d <= THROW_NEAR) info(p, 'throw', tt('tip.throw', `Можна кинути на склад: стань лицем до вагона й ${keys(p).throw} 🎯`, keys(p).throw), store);
        }
        continue;
      }
      if (h.k === I.AXE) {
        if (free(p, 'chop')) {
          const tree = nearest(s, me, SEARCH, (i) => s.obj[i] === O.TREE);
          if (tree) give(p, 'chop', `Рубай дерево: підійди й тисни ${key}`, tree);
        } else if (free(p, 'grab')) {
          const wood = near(I.WOOD);
          if (wood) give(p, 'grab', `Дрова на землі! Поклади сокиру (${key} на порожнє) — і бери їх`, wood);
        }
        continue;
      }
      if (h.k === I.PICK) {
        if (free(p, 'mine')) {
          const rock = nearest(s, me, SEARCH, (i) => s.obj[i] === O.ROCK);
          if (rock) give(p, 'mine', `Бий скелю кайлом: підійди й тисни ${key}`, rock);
        } else if (free(p, 'grab')) {
          const iron = near(I.IRON);
          if (iron) give(p, 'grab', `Залізо на землі! Поклади кайло (${key} на порожнє) — і бери`, iron);
        }
        continue;
      }
      if (h.k) continue;   // відро тощо — без порад, поки котел не гріється
      // порожні руки: рейки (кладільнику, або всім, коли колії лишилось мало) чи інструмент
      const railsUrgent = (t.rails ?? 0) > 0 && (p % 2 === 1 || (t.started && ahead < 4));
      const railsTip = () => { if ((t.rails ?? 0) > 0 && free(p, 'rails')) { give(p, 'rails', tt('tip.railsReady', `Рейки готові — візьми з майстерні (${key} біля вагона)`, key), craft); return true; } return false; };
      if (railsUrgent && railsTip()) continue;
      const wood = near(I.WOOD) ?? near(I.IRON);
      if (wood && free(p, 'grab')) { give(p, 'grab', `Бери ${s.drop[wood.y * s.w + wood.x] === I.WOOD ? 'дрова' : 'залізо'}: стань поруч і ${key}`, wood); continue; }
      const a = axe();
      if (a && free(p, 'axe')) { give(p, 'axe', 'Візьми сокиру — он лежить', a); continue; }
      const k = pick();
      if (k && free(p, 'pick') && (isDone(m, p, 'chop') || !a)) { give(p, 'pick', tt('tip.pick', 'Тепер кайло — скелі дають залізо, без нього рейок не буде'), k); continue; }
      railsTip();
    }
    for (const p of who) if (taken.has(p)) out.push(taken.get(p));
  }
  // що показуємо й з якого тику (для ролей і «Є!» у DOM)
  const now = new Map(out.map((o) => [o.p, o.id]));
  (s.players ?? []).forEach((_, p) => {
    const id = now.get(p) ?? null;
    if (m.since[p]?.id !== id) m.since[p] = { id, t: s.tick ?? 0 };
  });
  snapshot(s, m);
  return out;
}

// ---------------- DOM ----------------
const CSS = `
.kol-tips { position: fixed; inset: 0; pointer-events: none; z-index: 6; font: 600 clamp(13px, 1.3vw, 18px)/1.25 system-ui, sans-serif; }
.kol-tip { position: absolute; left: 0; top: 0; max-width: min(22em, 70vw); padding: .4em .7em; border-radius: 10px;
  background: #fffbe9f2; color: #20180c; box-shadow: 0 3px 10px #0007; border: 2px solid var(--c); white-space: normal;
  transition: opacity .25s ease; will-change: transform; }
.kol-tips-corner { position: absolute; right: 12px; bottom: 12px; display: flex; flex-direction: column-reverse; gap: .5em; align-items: flex-end; }
.kol-tips-corner .kol-tip { position: static; }
.kol-tips-corner .kol-tip.kol-tip-off { display: none; }
.kol-tip.kol-tip-up::after { content: ''; position: absolute; left: var(--tail, 50%); bottom: -9px; margin-left: -8px;
  border: 8px solid transparent; border-bottom: 0; border-top-color: var(--c); }
.kol-tip small { display: block; font-size: .72em; opacity: .65; font-weight: 700; }
.kol-tip .kol-tip-dir { float: right; margin-left: .5em; color: var(--c); text-shadow: 0 0 2px #000; }
.kol-tip.kol-tip-ok { background: #e6ffd2f2; }
.kol-tip.kol-tip-off { opacity: 0; }
.kol-tip-mark { position: absolute; left: 0; top: 0; color: var(--c); font-size: 1.6em; text-shadow: 0 0 3px #000, 0 0 6px #000;
  animation: kol-tip-bob .7s ease-in-out infinite alternate; }
@keyframes kol-tip-bob { to { margin-top: -.3em; } }
`;
const ARROWS = ['→', '↘', '↓', '↙', '←', '↖', '↑', '↗'];

export function createTips(root, { enabled = true } = {}) {
  const doc = root?.ownerDocument ?? document;
  const mem = {};
  let on = !!enabled, lastTick = -1, lastSeed = null, tips = [];
  const style = doc.createElement('style'); style.textContent = CSS; doc.head.appendChild(style);
  const box = doc.createElement('div'); box.className = 'kol-tips'; root.appendChild(box);
  const corner = doc.createElement('div'); corner.className = 'kol-tips-corner'; box.appendChild(corner);
  const ui = [];   // p → { bub, mark, id, text, okUntil, bw (кеш ширини) }
  let vwCache = 0;
  const onResize = () => { vwCache = 0; for (const u of ui) if (u) u.bw = 0; };
  addEventListener('resize', onResize);

  function slot(p) {
    if (ui[p]) return ui[p];
    const bub = doc.createElement('div'); bub.className = 'kol-tip kol-tip-off';
    const mark = doc.createElement('div'); mark.className = 'kol-tip-mark'; mark.textContent = '▼'; mark.style.display = 'none';
    bub.style.setProperty('--c', COLORS[p % 4]); mark.style.setProperty('--c', COLORS[p % 4]);
    box.append(bub, mark);
    return (ui[p] = { bub, mark, id: null, html: '', okUntil: 0 });
  }
  function hideAll() { for (const u of ui) if (u) { u.bub.classList.add('kol-tip-off'); u.mark.style.display = 'none'; } }

  return {
    update(state, game, view) {
      if (!on || !state || game?.paused) { hideAll(); return; }
      if (state.seed !== lastSeed || state.tick !== lastTick) {
        const local = game?.local ?? state.players.length;
        const inp = game?.input, nLocal = game?.local ?? 1;
        tips = pickTips(state, mem, { players: state.players.map((_, i) => i).filter((i) => i < local),
          keys: (p) => keysFor(p, !!inp?.hasPad?.(p), nLocal) });
        lastTick = state.tick; lastSeed = state.seed;
      }
      const v = game?.view ?? view;
      const proj = typeof v?.project === 'function' ? (x, y) => v.project(x, y) : null;
      const now = performance.now();
      const by = new Map(tips.map((t) => [t.p, t]));
      const n = Math.max(ui.length, state.players.length);
      for (let p = 0; p < n; p++) {
        const tip = by.get(p), pl = state.players[p];
        if (!tip && !ui[p]) continue;
        const u = slot(p);
        // щойно зробив пораджене — коротке «Є!»
        // щойно зробив пораджене — коротке «Є!», а тоді вже наступна порада
        if (u.id && (!tip || tip.id !== u.id)) { if (didIt(mem, p, u.id)) u.okUntil = now + 900; u.id = null; }
        let html, ok = false;
        if (u.okUntil > now) { html = `${proj ? '' : `<small>Гравець ${p + 1}</small>`}Є! 👍`; ok = true; }
        else if (tip) {
          u.id = tip.id;
          let dir = '';
          if (!proj && tip.x !== null && pl) {
            const me = cellOf(pl), dx = tip.x - me.x, dy = tip.y - me.y, dist = Math.max(Math.abs(dx), Math.abs(dy));
            dir = dist <= 1 ? '• тут' : `${ARROWS[(Math.round(Math.atan2(dy, dx) / (Math.PI / 4)) + 8) % 8]} ${dist}`;
          }
          html = `${proj ? '' : `<span class="kol-tip-dir">${dir}</span><small>Гравець ${p + 1}</small>`}${esc(tip.text)}`;
        } else { u.bub.classList.add('kol-tip-off'); u.mark.style.display = 'none'; u.id = null; continue; }
        if (html !== u.html) { u.bub.innerHTML = html; u.html = html; u.bw = 0; }
        u.bub.classList.remove('kol-tip-off');
        u.bub.classList.toggle('kol-tip-ok', ok);
        // де малювати
        const at = proj && pl ? proj(pl.x / U, pl.y / U) : null;
        if (at) {
          if (u.bub.parentNode !== box) { box.appendChild(u.bub); u.bw = 0; }
          u.bub.classList.add('kol-tip-up');
          // по центру над гравцем, але не за краєм вікна (хвостик лишається над гравцем)
          // K16: ширину міряємо раз на текст (offsetWidth щокадру — примусове перекомпонування)
          const bw = u.bw || (u.bw = u.bub.offsetWidth), vw = vwCache || (vwCache = doc.documentElement.clientWidth);
          const left = Math.max(6, Math.min(vw - bw - 6, at.x - bw / 2));
          u.bub.style.setProperty('--tail', `${Math.round(Math.max(10, Math.min(bw - 10, at.x - left)))}px`);
          u.bub.style.transform = `translate(${Math.round(left)}px, ${Math.round(at.y)}px) translateY(calc(-100% - 2.2em))`;
        } else {
          // фолбек: стос у правому нижньому куті, гравець 1 — найнижче
          if (u.bub.parentNode !== corner) corner.appendChild(u.bub);
          u.bub.classList.remove('kol-tip-up');
          u.bub.style.transform = '';
          u.bub.style.order = String(p);
        }
        const mk = proj && !ok && tip && tip.x !== null ? proj(tip.x + 0.5, tip.y + 0.5) : null;
        if (mk) { u.mark.style.display = ''; u.mark.style.transform = `translate(${Math.round(mk.x)}px, ${Math.round(mk.y)}px) translate(-50%, -110%)`; }
        else u.mark.style.display = 'none';
      }
    },
    setEnabled(b) { on = !!b; if (!on) hideAll(); },
    dispose() { removeEventListener('resize', onResize); box.remove(); style.remove(); ui.length = 0; },
  };
}

const esc = (t) => String(t).replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]);
