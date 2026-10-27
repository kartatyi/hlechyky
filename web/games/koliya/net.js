// «Колія» — мережа: кілька браузерів грають одну партію за столом сайту. Власник: K3.
// Контракт: createNet(game, { table, nick, input, bot | makeBot(p), onWorld(s), onStep(s) }) → net
//   net.start() — підключитись до столу (table: id столу, 'new' — новий, 'auto' — перший відкритий «Колії» чи новий);
//   net.update(now) — щокадру замість локального циклу тиків (крутить game.state); net.alpha — частка тику для draw;
//   net.restart(seed, params?) — нова партія всім за столом; net.info() — стан для перевірок (KOL.net).
//
// СХЕМА: сервер (src/Hlechyky/Games/Impl/Koliya.cs) — лише «секретар»: правил не знає, ставить кожній команді номер
// тику партії й розсилає всім однаковий журнал. Світ рахує кожен браузер сам тими самими rules.js — однаковий журнал +
// однаковий сід = однаковий світ (і позиції гравців теж: рух іде командами 'in', тож rules.js рухає всіх однаково, а
// автопідбір стосу під ногами не розводить світ).
//
// Тики. Тик партії сервер рахує від годинника (T = від старту / 50 мс). Водяний знак W = T − 7: тики ≤ W закрито,
// нових команд туди не буде. Команда несе бажаний тик t; запізніла (t ≤ W) лягає на W+1, свої — не раніше попередньої.
// Кадр (20/с) везе { e епоха, t, w, n довжина журналу, f, c записи [f, n) }: запис повторюється в кадрах ~0,5 с
// (каркас може згорнути кадри); діра в номерах, нова епоха чи вхід посеред партії — журнал окремо GET …/log.
//
// Два світи в браузері (rollback, як GGPO, але простіше):
//  C — остаточний: рахується лише до W+1 і лише з журналу; він однаковий у всіх (хеш R.hash(C) кожні 100 тиків іде
//      на сервер — розбіжність сервер позначає в кадрі `d`, тут — console.warn).
//  game.state (P) — передбачений: C + ще не закриті тики з тим, що вже відомо, + свої команди без луни (pending), а
//      чужий рух триває, як був ('in' у rules тримається до нового). Свої дії видно одразу, без затримки мережі.
//      Прийшло щось нове в уже передбачені тики (чужа команда, своя зсунута) — P копіюється з C (на місці: ті самі
//      масиви, рендер не перебудовує світ) і перераховується до того ж тику; звук/події — лише для нових тиків.
// Годинник: зсув «серверні мс ≈ performance.now() + off» — найбільший зі зразків кадрів (найменша затримка), повільно
// тане; P іде за ним і не далі W+40 (зв'язок пропав — світ стає). Свої команди просять t = P.tick: при затримці в
// обидва боки до ~300 мс (Grace) вони лягають саме туди, де їх уже показано.
// Затиснута кнопка: rules вважає use «затиснутим», коли він був і в попередньому тику, а мережа шле до ~6 use/с; тож
// повтор іде з h=1, і перед кроком ставимо гравцеві ut = tick (однаково в усіх браузерах — це ж із журналу).
// Ліміти каркаса: 30 Input/с з з'єднання (тут 'in' ≤ 10/с, use ≤ ~7/с, хеш раз на 5 с), payload ≤ 8 КБ.
//
// K6. Гладкість: перерахунок від запізнілої чужої команди зсуває фігури в P на тому ж тику — рендер (render.js, без
// змін) показав би ривок. Тому net.draw(view) малює гравців у «видимій» позі: справжня + зсув (ox, oy), де зсув при
// перерахунку набирає рівно стільки, щоб видима поза не зрушила, а далі тане щотику (×SMOOTH, ~100–150 мс). Зсув
// додається до p.x/p.y лише на час view.draw і одразу знімається — правила, HUD, підказки бачать справжній світ.
// Свого гравця це не гальмує: свої дії йдуть у P одразу, зсув з'являється лише від перерахунку (рідко).
// K44. Знімки для пізнього входу: раз на SNAP_EVERY тиків (1 хв) кожен гравець, чий C живий (не доганяє), шле на
// сервер стиснутий знімок C (JSON, типізовані масиви — base64, gzip через CompressionStream) окремим POST …/snap
// (у Input ≤ 8 КБ не влазить) разом із хешем. Сервер зберігає останній знімок, хеш якого підтвердили звірки ≥ 2 місць
// (або одного, якщо грає один), не розбираючи вмісту. Пізній чи після діри — GET …/log?from=0&snap=1 → { s: знімок,
// f: журнал з першого запису з тиком ≥ тику знімка }; знімок розпаковуємо, звіряємо тик і R.hash — і переграємо лише
// хвіст. Не зійшлось — журнал з нуля, як раніше. Знімки йдуть повз правила: на детермінізм не впливають.
// Затримка для перевірок: ?lag=<RTT мс>&jit=<мс> — кадри, вид столу, журнал і свої Input ідуть з половиною RTT
// (± jit/2) у кожен бік, порядок зберігається (як TCP). ?smooth=0 — без згладжування (порівняти ривки).
import * as R from './rules.js';

const GAP_IN = 2;          // тиків між 'in' (≤ 10/с)
const GAP_USE = 3;         // тиків між повторами затиснутого use
const HASH_EVERY = 100;    // тиків між звірками
const AHEAD = 40;          // на скільки тиків P може випередити W
const PENDING_TTL = 60;    // своя команда без луни стільки тиків після закриття — загубилась (квота), викидаємо
const STEPS_PER_FRAME = 600;
const SMOOTH = 0.55;       // частка зсуву, що лишається за тик (50 мс): за 3 тики ~17 %
const SNAP = 3 * R.U;      // більший стрибок (поява, телепорт) — не згладжуємо
const CATCH = 2;           // зсув тане не швидше за 2 кроки гравця за тик (разом із ходою — до 3× швидкості)
const JERK = 3.5;          // замір: стрибок за тик більший за 3,5 кроку — ривок (згладжене не перевищує 1 + CATCH)
export const SNAP_EVERY = 1200;   // K44: тиків між знімками (1 хв); кратне HASH_EVERY — знімок на тику звірки
const K = { 1: 'in', 2: 'use', 3: 'join', 4: 'leave', 5: 'throw', 6: 'horn' };   // 5, 6 — кооп-фішки K18

/**
 * Згладжування поз гравців для малювання (K6): зсув на перерахунку, тане щотику. Чисте, без DOM — тестується в Node.
 * before(s) — запам'ятати справжні пози перед перерахунком; after(s) — після нього (той самий тик) додати різницю в зсув;
 * apply(s) — на час малювання додати зсув до p.x/p.y (повертає функцію, що знімає); jerks — лічильник ривків видимої пози.
 */
export function createSmoother(on = true) {
  const off = [], seen = [];
  let saved = null, tick = -1;
  // ticks — тиків заміряно; rawMax/visMax — найбільший стрибок справжньої/видимої пози за тик (підодиниці);
  // rawJerk/visJerk — скільки разів стрибок за тик був більший за JERK × крок гравця (ривок, а не хода); corr — перерахунків, що зсунули гравця
  const st = { ticks: 0, rawJerk: 0, visJerk: 0, rawMax: 0, visMax: 0, corr: 0 };
  let cap = 0;
  function decay(t) {
    const n = t - tick;
    if (tick >= 0 && n > 0) {
      for (const o of off) if (o) for (let i = 0; i < Math.min(n, 40) && (o.x || o.y); i++) {
        // тане ×SMOOTH за тик, але не швидше за CATCH кроків гравця — великий зсув доїжджає за 4–5 тиків, а не стрибком
        const d = Math.hypot(o.x, o.y), k = Math.max(SMOOTH, 1 - cap / d);
        o.x = Math.trunc(o.x * k); o.y = Math.trunc(o.y * k);
      }
    }
    tick = t;
  }
  return {
    st, off,
    before(s) { saved = s.players.map((p) => ({ x: p.x, y: p.y })); },
    after(s) {
      if (!saved) return;
      s.players.forEach((p, k) => {
        const b = saved[k]; if (!b) return;
        const dx = b.x - p.x, dy = b.y - p.y;
        if (!dx && !dy) return;
        st.corr++;
        const o = (off[k] ??= { x: 0, y: 0 });
        if (!on || Math.abs(o.x + dx) + Math.abs(o.y + dy) > SNAP) { o.x = 0; o.y = 0; return; }
        o.x += dx; o.y += dy;
      });
      saved = null;
    },
    /** Видима поза гравця k (для заміру й малювання). */
    vis(s, k) { const p = s.players[k], o = off[k]; return o ? { x: p.x + o.x, y: p.y + o.y } : { x: p.x, y: p.y }; },
    apply(s) {
      const stepU = Math.round(s.params.playerSpeed * R.U / R.TPS);
      cap = CATCH * stepU;
      decay(s.tick);
      // замір: за один тик справжня поза (з перерахунками) і видима — наскільки стрибнули понад швидкість гравця
      const lim = JERK * stepU;
      s.players.forEach((p, k) => {
        const v = this.vis(s, k), w = seen[k];
        const n = w ? s.tick - w.t : 0;   // тиків від попереднього заміру (кадр ≤ 10/с у ?test — по 2 тики)
        if (n >= 1 && n <= 3 && !p.out) {
          const raw = Math.hypot(p.x - w.rx, p.y - w.ry) / n, vis = Math.hypot(v.x - w.vx, v.y - w.vy) / n;
          st.ticks++;
          st.rawMax = Math.max(st.rawMax, raw); st.visMax = Math.max(st.visMax, vis);
          if (raw > lim) st.rawJerk++;
          if (vis > lim) st.visJerk++;
        }
        if (!w || w.t !== s.tick) seen[k] = { t: s.tick, rx: p.x, ry: p.y, vx: v.x, vy: v.y };
      });
      const back = [];
      s.players.forEach((p, k) => { const o = off[k]; if (o && (o.x || o.y)) { back.push(p, p.x, p.y); p.x += o.x; p.y += o.y; } });
      return () => { for (let i = 0; i < back.length; i += 3) { back[i].x = back[i + 1]; back[i].y = back[i + 2]; } };
    },
    reset() { off.length = 0; seen.length = 0; saved = null; tick = -1; },
  };
}

/** Затримка мережі для перевірок: одна сторона, ordered — наступне не обганяє попереднє. */
export function createLag(rtt = 0, jit = 0, rnd = Math.random) {
  let last = 0, timer = null;
  const q = [];   // [{ due, fn }] — due не спадає
  function pump() {
    timer = null;
    const now = performance.now();
    while (q.length && q[0].due <= now + 0.5) { try { q.shift().fn(); } catch (x) { console.warn('Колія: затримка', x); } }
    if (q.length) timer = setTimeout(pump, Math.max(0, q[0].due - now));
  }
  return (fn) => {
    if (!(rtt > 0 || jit > 0)) return fn();
    const now = performance.now(), d = Math.max(0, rtt / 2 + (rnd() - 0.5) * jit);
    last = Math.max(last, now + d);
    q.push({ due: last, fn });
    if (!timer) timer = setTimeout(pump, Math.max(0, q[0].due - now));
  };
}

// ---------------- K44: знімок світу (чисте, без DOM — тестується в Node) ----------------
const TA = { Uint8Array, Int8Array, Uint8ClampedArray, Uint16Array, Int16Array, Uint32Array, Int32Array, Float32Array, Float64Array };
function toB64(u8) {
  let str = '';
  for (let i = 0; i < u8.length; i += 0x8000) str += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000));
  return btoa(str);
}
function fromB64(b) {
  const str = atob(b), a = new Uint8Array(str.length);
  for (let i = 0; i < str.length; i++) a[i] = str.charCodeAt(i);
  return a;
}
/** Світ → JSON-текст: типізовані масиви — { $ta: тип, b: base64 байтів }. */
export function snapText(s) {
  return JSON.stringify(s, (k, v) => (ArrayBuffer.isView(v)
    ? { $ta: v.constructor.name, b: toB64(new Uint8Array(v.buffer, v.byteOffset, v.byteLength)) } : v));
}
/** JSON-текст знімка → світ. */
export function snapParse(text) {
  return JSON.parse(text, (k, v) => (v && typeof v === 'object' && typeof v.$ta === 'string' && TA[v.$ta]
    ? new TA[v.$ta](fromB64(v.b).buffer) : v));
}
async function through(u8, stream) {
  return new Uint8Array(await new Response(new Blob([u8]).stream().pipeThrough(stream)).arrayBuffer());
}
/** Знімок C → { z: 1 — gzip, b: байти }. Текст береться одразу (синхронно) — C після виклику може йти далі. */
export async function encodeSnap(s, zip = true) {
  const u8 = new TextEncoder().encode(snapText(s));
  if (zip && typeof CompressionStream === 'function') {
    try { return { z: 1, b: await through(u8, new CompressionStream('gzip')) }; } catch { }
  }
  return { z: 0, b: u8 };
}
/** Знімок з журналу (b — base64, z — gzip) → світ. */
export async function decodeSnap(b, z) { return snapParse(await unzipSnap(b, z)); }
/** Знімок з журналу → JSON-текст (розпаковка асинхронна, розбір — snapParse). */
export async function unzipSnap(b, z) {
  let u8 = fromB64(b);
  if (z) u8 = await through(u8, new DecompressionStream('gzip'));
  return new TextDecoder().decode(u8);
}

function loadScript(src) {
  return new Promise((ok, bad) => {
    const s = document.createElement('script');
    s.src = src; s.onload = ok; s.onerror = () => bad(new Error('не завантажився ' + src));
    document.head.appendChild(s);
  });
}

/** Скопіювати світ src у dst на місці: типізовані масиви тієї ж довжини — .set(), решта — глибока копія. */
export function copyInto(dst, src) {
  for (const k of Object.keys(dst)) if (!(k in src)) delete dst[k];
  for (const k of Object.keys(src)) {
    const a = src[k], b = dst[k];
    if (ArrayBuffer.isView(a) && b && b.constructor === a.constructor && b.length === a.length) b.set(a);
    else dst[k] = a && typeof a === 'object' ? structuredClone(a) : a;
  }
  return dst;
}

/** Затиснутий use з журналу: rules має бачити «use був і в попередньому тику». */
export function prep(s, cmds) {
  for (const c of cmds) if (c.k === 'use' && c.h) { const pl = s.players[c.p]; if (pl) pl.ut = s.tick; }
}

/**
 * K28: нік для мітки над гравцем — до 10 символів (довший — 9 і «…»), суфікс « ²» (другий за тим самим ПК) не ріжемо.
 * Порожній/не рядок — null (мітка лишиться «П1»).
 */
export function shortNick(n) {
  if (typeof n !== 'string' || !n.trim()) return null;
  let base = n.trim(), sup = '';
  if (base.endsWith(' ²')) { sup = ' ²'; base = base.slice(0, -2).trimEnd(); }
  if (/^гість\s+\S/i.test(base)) base = base.replace(/^гість\s+/i, '');   // Auth.GuestPrefix — над головою лише ім'я
  const ch = [...base];
  return (ch.length > 10 ? ch.slice(0, 9).join('') + '…' : base) + sup;
}

/** K28: реакції — 1–4 на клавіатурі (Num1–4 — другий за ПК), на паді LB/RB/LT/RT (їх input.js не чіпає). */
export const REACTS = ['👋', '👍', '😂', '😱'];
export const REACT_GAP = 1000;   // мс між реакціями одного гравця (сервер — 900, із запасом на тремтіння)

/** K28: ліміт «раз на gap мс» за ключем (гравець); true — можна. Чисте, для тестів. */
export function createReactGate(gap = REACT_GAP) {
  const last = new Map();
  return (key, now) => {
    const t = last.get(key);
    if (t != null && now - t < gap) return false;
    last.set(key, now);
    return true;
  };
}

/**
 * K28: нові реакції з кадру (r = [seq, p, e, i] × N; запис їде в кадрах ~1,5 с): лише seq > seen, і без своїх
 * (isMine(p, i) — їх уже показано одразу при натиску). Номери зменшились (сервер перезапустився) — рахуємо з нуля.
 */
export function freshReacts(r, seen, isMine = () => false) {
  const list = [];
  if (!Array.isArray(r) || r.length < 4) return { seen, list };
  if (r[r.length - 4] < seen - 1000) seen = -1;
  for (let j = 0; j + 3 < r.length; j += 4) {
    const seq = r[j], p = r[j + 1], e = r[j + 2], i = r[j + 3];
    if (seq <= seen) continue;
    seen = seq;
    if (!(e >= 0 && e < REACTS.length) || isMine(p, i)) continue;
    list.push({ p, e });
  }
  return { seen, list };
}

export const LOST_MS = 3000;   // K28: без кадрів довше — «відпав» (сервер так само про мовчазне місце, pg = -1)
export const PING_EVERY = 1000;

/** K28: колір пінгу — зелений / жовтий / помаранчевий / червоний; null (ще не заміряно чи відпав) — сірий. */
export function pingColor(ms) {
  if (ms == null || ms < 0) return '#9a9a9a';
  return ms < 80 ? '#8fe36b' : ms < 150 ? '#ffd23f' : ms < 250 ? '#ff9a3c' : '#ff5a4f';
}

/**
 * K28: замір пінгу. Шлемо 'ping' {i} → кадр несе pa[місце] = [i, скільки мс пінг пролежав на сервері до кадру];
 * пінг = (зараз − коли слали) − пролежав. Згладжено: новий зразок важить 0,3, але менший приймаємо одразу наполовину.
 */
export function createPinger(every = PING_EVERY) {
  const sent = new Map();
  let ms = null, next = 0, seq = 0;
  return {
    get ms() { return ms; },
    /** Час слати? → номер пінгу (і запам'ятати), інакше 0. */
    due(now) {
      if (now < next) return 0;
      next = now + every;
      const i = ++seq;
      sent.set(i, now);
      if (sent.size > 10) sent.delete(sent.keys().next().value);
      return i;
    },
    /** Луна з кадру: i, age (мс на сервері). */
    echo(i, age, now) {
      const t = sent.get(i);
      if (t == null) return ms;
      sent.delete(i);
      const v = Math.max(0, Math.round(now - t - (age | 0)));
      ms = ms == null ? v : Math.round(v < ms ? (ms + v) / 2 : ms * 0.7 + v * 0.3);
      return ms;
    },
  };
}

/** K28: хто «відпав»: p → true за pg (-1) з кадру, а якщо кадрів нема довше LOST_MS — і свої (mine). */
export function lostFrom(pg, stale, mine = []) {
  const a = Array.isArray(pg) ? pg.map((v) => v === -1) : [];
  if (stale) for (const p of mine) if (p >= 0) a[p] = true;
  return a;
}

/** K28: «👀 N глядачів» — відмінок за числом. */
export function watchersText(n) {
  const a = n % 10, b = n % 100;
  return `👀 ${n} ` + (a === 1 && b !== 11 ? 'глядач' : a >= 2 && a <= 4 && (b < 12 || b > 14) ? 'глядачі' : 'глядачів');
}
export const WATCH_EVERY = 2000;   // глядач озивається (…/watch) — сервер забуває за 5 с

/** K28: мапа p → короткий нік з виду/журналу сервера (nm). */
export function namesFrom(nm) {
  return Array.isArray(nm) ? nm.map(shortNick) : [];
}

export function toCmd(c, j) {
  const p = c[j + 2], k = K[c[j + 3]];
  if (k === 'in') return { p, k, mx: c[j + 4], my: c[j + 5] };
  if (k === 'use') return { p, k, x: c[j + 4], y: c[j + 5], fx: c[j + 6], fy: c[j + 7], h: c[j + 8] };
  if (k === 'throw') return { p, k, x: c[j + 4], y: c[j + 5], fx: c[j + 6], fy: c[j + 7] };
  if (k === 'join' || k === 'horn') return { p, k };
  // K24: невідомий вид (новий сервер, стара вкладка без F5) — не 'leave' (вибив би гравця), а порожня команда:
  // rules її пропустить, світ розійдеться лише на цій дії, і звірка хешу це покаже
  return k === 'leave' ? { p, k } : { p, k: 'k' + c[j + 3] };
}

/**
 * Скриптовий водій для перевірок мережі (?drive=1): порожні руки — до найближчого інструмента й узяти; сокира — рубати
 * дерева, кайло — бити скелі; інше в руках — покласти. Застряг — трохи блукає. Не бот гри (той — bot.js), лише щоб
 * за дві хвилини світ змінювався від дій кількох гравців.
 */
export function createDriver() {
  let n = 0, wander = 0, dir = [0, 0], lx = 0, ly = 0, still = 0, holdSince = 0, lastK = -1;
  const DIRS = [[100, 0], [-100, 0], [0, 100], [0, -100]];
  const { U, I, O } = R;
  return {
    think(s, p) {
      const v = this.walk(s, p), pl = s.players[p];
      // K18 у мережі: з річчю в руках зрідка кидає, ще рідше гудить (перевірка throw/horn через журнал)
      if (pl && pl.hold.k !== R.I.NONE && n % 37 === 0) v.throw = true;
      if (n % 211 === 0) v.horn = true;
      return v;
    },
    walk(s, p) {
      n++;
      const pl = s.players[p];
      if (!pl) return { mx: 0, my: 0, use: false };
      if (pl.hold.k !== lastK) { lastK = pl.hold.k; holdSince = n; }
      if (pl.x === lx && pl.y === ly) still++; else still = 0;
      lx = pl.x; ly = pl.y;
      if (still > 12 && !wander) { wander = 10 + ((Math.random() * 20) | 0); dir = DIRS[(Math.random() * 4) | 0]; }
      if (wander > 0) { wander--; return { mx: dir[0], my: dir[1], use: false }; }
      const k = pl.hold.k;
      let want;
      if (k === I.NONE) want = (i) => s.drop[i] === I.AXE || s.drop[i] === I.PICK || s.drop[i] === I.WOOD;
      else if (k === I.AXE) want = (i) => s.obj[i] === O.TREE;
      else if (k === I.PICK) want = (i) => s.obj[i] === O.ROCK;
      if (k !== I.NONE && (n - holdSince > 400 || !want)) return { mx: 0, my: 0, use: n % 6 === 0 };   // покласти
      // BFS по прохідних клітинках до тієї, що поруч із ціллю
      const cx = Math.floor(pl.x / U), cy = Math.floor(pl.y / U), W = s.w, H = s.h;
      const prev = new Int32Array(W * H).fill(-1), start = cy * W + cx, qu = [start];
      prev[start] = start;
      let goal = -1, face = null;
      for (let qi = 0; qi < qu.length && goal < 0 && qi < 4000; qi++) {
        const c = qu[qi], x = c % W, y = (c / W) | 0;
        for (const [dx, dy] of DIRS.map(([a, b]) => [Math.sign(a), Math.sign(b)])) {
          const nx = x + dx, ny = y + dy;
          if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
          const ni = ny * W + nx;
          if (want(ni)) { goal = c; face = [dx, dy]; break; }
          if (prev[ni] >= 0 || R.cellBlocked(s, nx, ny)) continue;
          prev[ni] = c; qu.push(ni);
        }
      }
      if (goal < 0) { wander = 20; dir = DIRS[(Math.random() * 4) | 0]; return { mx: dir[0], my: dir[1], use: false }; }
      if (goal === start) {
        // на місці: довернутись до цілі (малий намір лише повертає) і тиснути
        const ok = pl.fx === face[0] && pl.fy === face[1];
        return { mx: face[0] * 11, my: face[1] * 11, use: ok && n % 3 !== 2 };   // < 12 — мертва зона rules: лише поворот
      }
      let c = goal;
      while (prev[c] !== start) c = prev[c];
      const tx = (c % W) * U + U / 2, ty = ((c / W) | 0) * U + U / 2;
      const ddx = tx - pl.x, ddy = ty - pl.y;
      return { mx: Math.abs(ddx) > 60 ? Math.sign(ddx) * 100 : 0, my: Math.abs(ddy) > 60 ? Math.sign(ddy) * 100 : 0, use: false };
    },
  };
}

// K46: «Ти вже за столом. Встань, якщо хочеш новий» (Rooms.Say.Seated) — сидиш за ІНШИМ столом; «Ти вже за цим столом» — ні
export const seatedElsewhereMsg = (m) => /^Ти вже за столом/.test(String(m || ''));
/** K46: пауза перед n-м (з 1) повтором журналу: 2, 4, 8, 15 с…; стола нема (404) — після 3 спроб годі (-1), без вічних 404 */
export function resyncDelay(n, is404) { return is404 && n >= 3 ? -1 : Math.min(15000, 2000 * 2 ** Math.max(0, n - 1)); }

/** K46: «Ти вже за іншим столом» — дві кнопки; ok → 'back' | 'new' | null (DOM нема) */
function askSeated(other) {
  if (typeof document === 'undefined') return Promise.resolve(null);
  return new Promise((done) => {
    const d = document.createElement('div');
    d.style.cssText = 'position:fixed;inset:0;z-index:60;display:flex;align-items:center;justify-content:center;background:#000a;font:600 16px system-ui,sans-serif;color:#fff';
    const here = other?.game === 'koliya';
    d.innerHTML = `<div style="max-width:min(26em,92vw);padding:1em 1.2em;border-radius:14px;background:#1d2418;border:1px solid #9bc25a;text-align:center">
<div style="font-size:1.15em;margin-bottom:.4em">🚂 Ти вже за іншим столом</div>
<div style="opacity:.8;font-weight:500;margin-bottom:.9em">${here ? 'Там іде твоя «Колія».' : 'Там інша гра.'} Новий стіл можна поставити, лише вставши з того.</div>
<div style="display:flex;flex-direction:column;gap:.5em"></div></div>`;
    const row = d.firstElementChild.lastElementChild;
    const btn = (t, v) => {
      const b = document.createElement('button'); b.type = 'button'; b.textContent = t;
      b.style.cssText = 'pointer-events:auto;min-height:46px;font:inherit;color:#fff;background:#2c3a22;border:1px solid #ffffff2a;border-radius:12px;cursor:pointer';
      b.addEventListener('click', () => { d.remove(); done(v); });
      row.appendChild(b); return b;
    };
    btn('↩ Повернутися туди', 'back').focus();
    btn('🆕 Вийти з нього й створити новий', 'new');
    document.body.appendChild(d);
  });
}

export function createNet(game, opts = {}) {
  const { input, onWorld, onStep } = opts;
  let table = String(opts.table || 'auto');
  const nick = opts.nick || '';
  let bot = opts.bot || null;           // { think(state) → { mx, my, use } } — керує своїм гравцем замість клавіатури
  const qs = typeof location !== 'undefined' ? new URLSearchParams(location.search) : new URLSearchParams();
  const lag = +(opts.lag ?? qs.get('lag') ?? 0) || 0, jit = +(opts.jit ?? qs.get('jit') ?? (lag ? lag / 3 : 0)) || 0;
  const lagIn = createLag(lag, jit), lagOut = createLag(lag, jit);   // K6: ?lag=120 — емуляція пінгу
  const smooth = createSmoother((opts.smooth ?? qs.get('smooth')) !== '0' && opts.smooth !== false);

  let conn = null, room = null, seat = null, ready = false, resyncing = false, lobby = null;
  let miss = 0, mineRv = null;          // K46: невдалих спроб журналу підряд; вид столу, де сидимо (з події 'room')
  let e = 0, C = null, count = 0, w = -1, hi = 0, dirty = false, off = null, idSeq = 0, desyncSaid = false;
  let lastIn = [], lastUse = [];       // [q] — свій рух/дія: q 0 — я, 1 — другий за цим ПК (?p=2)
  let asked2 = 0;                       // епоха, у якій уже просили другого гравця
  const log = new Map();                // тик → [cmd] (лише тики ≥ C.tick)
  let pending = [];                     // свої без луни: { t, i, cmd }
  const hashes = new Map();             // тик → хеш C
  const stats = { frames: 0, rebuilds: 0, resyncs: 0, sent: 0, dropped: 0,
    snapUp: 0, snapBytes: 0, snapFrom: -1, snapBad: 0, buildTicks: 0, buildMs: 0 };   // K44
  const useSnap = opts.snap !== false && qs.get('snap') !== '0';   // K44: ?snap=0 — пізній вхід завжди з нуля (порівняти)
  const zip = opts.zip !== false;   // K44: false — без gzip (Node-тести: стиснення йде реальним часом повз віртуальний годинник)
  // K28: люди за столом — реакції (повз журнал), панелька в куті
  let reactSeen = -1, ui = null;
  const myReacts = new Set(), reactGate = createReactGate();
  const pinger = createPinger();
  let lastFrameAt = 0, pg = null;
  const watchPing = createPinger(WATCH_EVERY), wid = Math.random().toString(36).slice(2, 12);   // глядач: номер вкладки

  const net = {
    p: -1, p2: -1, alpha: 0, status: 'старт', desync: -1,
    get room() { return room; },
    get ready() { return ready; },
    get C() { return C; },
    start, update, restart, draw,
    info() {
      return { room, seat, p: net.p, p2: net.p2, e, count, w, cTick: C?.tick, pTick: game.state?.tick, pending: pending.length,
        status: net.status, desync: net.desync, cStatus: C?.status, hash: C ? R.hash(C) : 0, stats: { ...stats },
        lag, jit, smooth: { ...smooth.st },
        ping: net.ping, pg, names: game.names, lost: game.lost, reactSeen };   // K28
    },
    hashAt: (t) => hashes.get(t),
    hashes: () => [...hashes.entries()],
    setBot(b) { bot = b; },
    react,
  };

  /** K28: реакція k (0..3) від свого гравця (q 1 — другий за ПК): показати одразу й надіслати. false — не можна/зарано. */
  function react(k, q = 0) {
    const p = q ? net.p2 : net.p;
    if (!conn || !ready || p < 0 || !(k >= 0 && k < REACTS.length)) return false;
    const now = performance.now();
    if (!reactGate(q, now)) return false;
    const i = ++idSeq;
    myReacts.add(i);
    if (myReacts.size > 32) myReacts.delete(myReacts.values().next().value);
    showReact(p, k, now);
    const m = { em: k, i, e, ...(q ? { q: 1 } : {}) };   // e — епоха (K24: стару сервер викидає)
    lagOut(() => conn.send('Input', room, 'react', m).catch(() => { }));
    return true;
  }
  function showReact(p, k, at = performance.now()) { (game.reacts ||= [])[p] = { e: REACTS[k], k, at }; }

  /** K28: панелька в правому куті під ⚙ — кнопки реакцій (миша/дотик); клавіші 1–4 і плечі пада. */
  function mountPeople() {
    if (typeof document === 'undefined' || ui) return;
    const st = document.createElement('style');
    st.textContent = `.kol-net{position:fixed;right:8px;top:62px;z-index:25;display:flex;flex-direction:column;align-items:flex-end;gap:6px;
font:600 13px system-ui,sans-serif;pointer-events:none;user-select:none}
.kol-net-tag{background:rgba(0,0,0,.5);padding:2px 8px;border-radius:6px;color:#ddd;white-space:nowrap}
.kol-net-re{display:flex;flex-direction:column;gap:4px;pointer-events:auto}
.kol-net-re button{position:relative;width:42px;height:42px;padding:0;border-radius:10px;border:1px solid #fff3;background:rgba(20,22,28,.6);
font-size:21px;line-height:1;cursor:pointer;transition:opacity .2s}
.kol-net-re button:active{transform:scale(.92)}
.kol-net-re button.cool{opacity:.4}
.kol-net-re small{position:absolute;right:3px;bottom:1px;font-size:9px;color:#aaa}`;
    document.head.appendChild(st);
    const el = document.createElement('div');
    el.className = 'kol-net';
    const re = document.createElement('div');
    re.className = 'kol-net-re';
    const btns = REACTS.map((e, k) => {
      const b = document.createElement('button');
      b.type = 'button'; b.tabIndex = -1; b.title = `Реакція (клавіша ${k + 1}; пад — ${['LB', 'RB', 'LT', 'RT'][k]})`;
      b.innerHTML = `${e}<small>${k + 1}</small>`;
      b.addEventListener('pointerdown', (ev) => { ev.preventDefault(); react(k, 0); });
      re.appendChild(b);
      return b;
    });
    const ping = document.createElement('div');
    ping.className = 'kol-net-tag';
    ping.title = 'Пінг до сервера (туди й назад)';
    const watch = document.createElement('div');
    watch.className = 'kol-net-tag';
    watch.style.display = 'none';
    el.append(ping, watch, re);
    document.body.appendChild(el);
    ui = { el, re, btns, ping, pingKey: '', watch, watchKey: '' };
    addEventListener('keydown', (ev) => {
      if (ev.repeat || ev.ctrlKey || ev.metaKey || ev.altKey) return;
      const t = ev.target;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable)) return;
      const m = /^(Digit|Numpad)([1-4])$/.exec(ev.code);
      if (!m) return;
      react(+m[2] - 1, m[1] === 'Numpad' && net.p2 >= 0 ? 1 : 0);
    });
  }

  // K28: плечі пада — реакції (input.js їх не чіпає); керма/педалі — не пади
  const SIM = /wheel|racing|driving force|steering|pedal|shifter|handbrake|hotas|throttle|flight|yoke|rudder|simucube|simagic|cammus/i;
  const padPrev = new Map();
  function padReacts() {
    if (typeof navigator === 'undefined' || !navigator.getGamepads) return;
    let pads;
    try { pads = [...navigator.getGamepads()].filter((g) => g && g.connected && !SIM.test(g.id || '')); } catch { return; }
    pads.forEach((g, n) => {
      const q = net.p2 >= 0 ? (pads.length >= 2 ? Math.min(n, 1) : 1) : 0;   // як input.js: p=2 і один пад — пад у другого
      const cur = [4, 5, 6, 7].map((b) => { const x = g.buttons?.[b]; return !!x && (x.pressed || x.value > 0.5); });
      const prev = padPrev.get(g.index) || [];
      cur.forEach((d, k) => { if (d && !prev[k]) react(k, q); });
      padPrev.set(g.index, cur);
    });
  }

  /** K28: панелька щокадру — кнопки реакцій лише гравцю, «охолола» — напівпрозора. */
  function peopleUi(now) {
    // пінг: раз на секунду, якщо маємо місце (Input без місця сервер не приймає)
    if (conn && room && seat != null && ready) {
      const i = pinger.due(now);
      if (i) { const m = { i, r: pinger.ms ?? 0 }; lagOut(() => conn.send('Input', room, 'ping', m).catch(() => { })); }
    }
    // глядач (не грає): озивається на …/watch раз на 2 с — стіл рахує глядачів; відповідь — його пінг
    if (room && ready && net.p < 0) {
      const i = watchPing.due(now);
      if (i) lagOut(() => fetch(`/api/games/koliya/${encodeURIComponent(room)}/watch?id=${wid}`, { method: 'POST', cache: 'no-store' })
        .then((r) => lagIn(() => { if (r.ok) watchPing.echo(i, 0, performance.now()); })).catch(() => { }));
    }
    const stale = ready && lastFrameAt > 0 && now - lastFrameAt > LOST_MS;
    game.lost = lostFrom(pg, stale, [net.p, net.p2]);
    const ms = seat != null ? pinger.ms : watchPing.ms;
    net.ping = stale ? -1 : ms;
    if (!ui) return;
    const key = stale ? 'lost' : String(ms);
    if (ui.pingKey !== key) {
      ui.pingKey = key;
      ui.ping.textContent = stale ? '📶? нема зв’язку' : ms == null ? '📶 …' : `📶 ${ms} мс`;
      ui.ping.style.color = pingColor(stale ? null : ms);
    }
    const playing = net.p >= 0;
    ui.re.style.display = playing ? '' : 'none';
    const wk = (playing ? '' : 'я') + (game.watchers | 0);
    if (ui.watchKey !== wk) {
      ui.watchKey = wk;
      const n = game.watchers | 0;
      ui.watch.textContent = playing ? watchersText(n) : `👀 дивишся збоку${n > 1 ? ` · ${watchersText(n).slice(3)}` : ''}`;
      ui.watch.style.display = playing && !n ? 'none' : '';
    }
    const r = game.reacts?.[net.p], cool = !!r && now - r.at < REACT_GAP;
    for (const b of ui.btns) b.classList.toggle('cool', cool);
  }

  function say(st) { net.status = st; }

  async function start() {
    if (!window.signalR) await loadScript('/static/signalr.min.js');
    conn = new signalR.HubConnectionBuilder()
      .withUrl('/hub?nick=' + encodeURIComponent(nick))
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (r) => [0, 1000, 2000, 3000, 5000][Math.min(r.previousRetryCount, 4)] })
      .build();
    conn.on('rooms', (list) => { lobby = list; });
    conn.on('frame', (m) => lagIn(() => { if (m && m.id === room) onFrame(m.f); }));
    conn.on('room', (rv) => lagIn(() => {
      if (rv?.room && rv.seat != null && rv.room.id !== room) mineRv = rv.room;   // K46: де ми вже сидимо
      if (rv && rv.room && rv.room.id === room) onRoom(rv);
    }));
    conn.onreconnecting(() => { ready = false; say('перепідключаюсь…'); });
    conn.onreconnected(() => enter().catch((x) => { console.warn('Колія: не зайшов знову', x); say('не зайшов: ' + x.message); }));
    say('підключаюсь…');
    try {
      mountPeople();   // K28
      await conn.start();
      await enter();
    } catch (x) {
      say('не підключився: ' + (x?.message || x));   // QA K21: не висіти на «підключаюсь…»
      throw x;
    }
    return net;
  }

  /** Сісти за стіл, підписатись на кадри й узяти журнал. */
  async function enter() {
    if (!room) {
      if (table === 'auto') {
        for (let i = 0; i < 20 && !lobby; i++) await new Promise((r) => setTimeout(r, 100));
        const open = (lobby || []).find((r) => r.game === 'koliya' && r.status === 'playing'
          && (r.seats || []).some((s) => !s || !s.nick));
        table = open ? open.id : 'new';
      }
      if (table === 'new') {
        let r = await conn.invoke('CreateRoom', 'koliya', {});
        if (!r.ok && seatedElsewhereMsg(r.message)) r = await seatedElsewhere(r);   // K46
        if (!r.ok) throw new Error(r.message || 'стіл не створився');
        room = r.roomId;
      } else room = table;
      try { const u = new URL(location.href); u.searchParams.set('net', room); history.replaceState(null, '', u); } catch { }
    }
    const j = await conn.invoke('JoinRoom', room);
    net.joinMsg = j.ok ? '' : j.message || '';
    if (!j.ok) console.info('Колія: JoinRoom —', j.message);   // «уже за столом» — це нормально; інше — дивимось збоку (net.joinMsg)
    await conn.invoke('WatchRoom', room);
    await resync();
  }

  /** K46: CreateRoom відмовив — сидимо за іншим столом. Питаємо: повернутись туди чи встати й поставити новий. */
  async function seatedElsewhere(r0) {
    for (let i = 0; i < 20 && !lobby && !mineRv; i++) await new Promise((r) => setTimeout(r, 100));
    const me = String(nick || '').toLowerCase();
    const other = mineRv || (me && (lobby || []).find((r) => (r.seats || []).some((x) => String((typeof x === 'string' ? x : x?.nick) || '').toLowerCase() === me))) || null;
    if (!other?.id) return r0;   // не знаємо, де сидимо, — main.js покаже «не вдалося» з «Грати без мережі»
    say('ти вже за іншим столом');
    const v = await askSeated(other);
    if (v === 'back') {
      if (other.game === 'koliya') return { ok: true, roomId: other.id };
      try { (window.top || window).location.href = '/'; } catch { location.href = '/'; }
      return r0;
    }
    if (v !== 'new') return r0;
    const left = await conn.invoke('LeaveRoom', other.id);
    if (!left?.ok) return left || r0;
    mineRv = null;
    return conn.invoke('CreateRoom', 'koliya', {});
  }

  function onRoom(rv) {
    seat = rv.seat ?? null;
    const v = rv.view;
    if (!v) return;
    net.p = v.p ?? -1;
    net.p2 = v.p2 ?? -1;
    game.me = net.p;
    if (v.nm) game.names = namesFrom(v.nm);   // K28
    if (v.e !== e && !resyncing) resync();
    else if (ready) want2();
  }

  async function resync() {
    if (!room) return;
    resyncing = true;
    stats.resyncs++;
    say('беру журнал…');
    try {
      if (lag) await new Promise((r) => lagOut(r));
      let res = await fetch(`/api/games/koliya/${encodeURIComponent(room)}/log?from=0${useSnap ? '&snap=1' : ''}`, { cache: 'no-store' });
      if (!res.ok) throw new Error('журнал: ' + res.status);
      let j = await res.json();
      miss = 0;
      if (lag) await new Promise((r) => lagIn(r));
      let snap = null, text = null, t0 = Date.now();
      if (j.s) {   // K44: знімок — розпакувати й звірити; не той — журнал з нуля
        try {
          text = await unzipSnap(j.s.b, j.s.z);
          t0 = Date.now();   // замір відновлення — без очікування розпаковки (кілька КБ; між await іде інше)
          snap = snapParse(text);
          if (snap.tick !== j.s.t || R.hash(snap) >>> 0 !== j.s.h >>> 0) throw new Error('хеш знімка не той');
        } catch (x) {
          console.warn('Колія: знімок не годиться —', x?.message || x);
          stats.snapBad++;
          snap = null;
          res = await fetch(`/api/games/koliya/${encodeURIComponent(room)}/log?from=0`, { cache: 'no-store' });
          if (!res.ok) throw new Error('журнал: ' + res.status);
          j = await res.json();
          t0 = Date.now();
        }
      }
      build(j, snap);
      stats.buildMs = Date.now() - t0;
    } catch (x) {
      console.warn('Колія: журнал не взявся', x);
      const is404 = /: 404$/.test(x?.message || ''), wait = resyncDelay(++miss, is404);   // K46: без вічних 404
      say(is404 ? 'стола нема (закрився?) — відкрий новий' : 'журнал не взявся: ' + (x?.message || x));
      if (wait >= 0) setTimeout(() => { if (!ready) resync(); }, wait);
    } finally { resyncing = false; }
  }

  /** Світ з нуля: newGame + увесь журнал до водяного знаку. K44: snap — світ зі знімка, журнал лише з його тику. */
  function build(j, snap = null) {
    if (j.e !== e) { off = null; net.desync = -1; desyncSaid = false; }   // нова епоха — тик з нуля, старий зсув годинника й розбіжність не годяться
    e = j.e;
    C = snap || R.newGame(j.seed >>> 0, j.prm || {}, j.n0 || 1);
    // K35: повтор — із журналу від тику 0; зі знімка початку нема — повтору цієї партії не пишемо
    if (!snap) game.replay?.begin?.(C, j.seed >>> 0, j.prm || {}, j.n0 || 1, { net: true, local: 99 });
    stats.snapFrom = snap ? C.tick : -1;
    log.clear(); count = snap ? j.f | 0 : 0; pending = []; hashes.clear(); hi = 0;
    lastIn = [0, 1].map(() => ({ mx: 0, my: 0, t: -99 })); lastUse = [0, 1].map(() => ({ t: -99, down: false }));
    if (seat != null && j.pOf) net.p = j.pOf[seat] ?? -1;
    if (seat != null && j.p2Of) net.p2 = j.p2Of[seat] ?? -1;
    game.me = net.p;
    if (j.nm) game.names = namesFrom(j.nm);   // K28: мітки над гравцями — ніки
    game.seed = j.seed >>> 0;
    if (off == null) off = j.t * R.TICK_MS - performance.now();
    if (!ingest(j.f | 0, j.c || [], !!snap)) { console.warn('Колія: журнал з дірою'); return; }
    w = j.w;
    if (j.d >= 0) noteDesync(j.d);
    const c0 = C.tick;
    advanceC();
    stats.buildTicks = C.tick - c0;
    [net.p, net.p2].forEach((p, q) => {   // що rules уже знає про мій рух — від цього й міряємо зміни
      const me = C.players[p];
      if (p >= 0 && me) lastIn[q] = { mx: me.mx | 0, my: me.my | 0, t: -99 };
    });
    want2();
    const fresh =!game.state || game.state.w !== C.w || game.state.h !== C.h;
    if (fresh) game.state = R.clone(C); else copyInto(game.state, C);
    smooth.reset();
    hi = C.tick;
    ready = true;
    say(net.p >= 0 ? 'грає' : 'дивишся збоку' + (net.joinMsg ? ': ' + net.joinMsg : ''));
    onWorld?.(game.state);
  }

  /** Двоє за одним ПК (?p=2): попросити сервер про другого гравця за цим місцем (раз на епоху). */
  function want2() {
    if ((game.local | 0) < 2 || net.p < 0 || net.p2 >= 0 || asked2 === e || !conn) return;
    asked2 = e;
    conn.invoke('Act', room, 'join2', {}).then((r) => {
      if (!r.ok) { console.info('Колія: другий гравець —', r.message); say('грає (другому місця нема)'); }
    }).catch((x) => console.warn('Колія: join2', x));
  }

  function onFrame(f) {
    stats.frames++;
    if (!f) return;
    const now = performance.now();   // K28: пінг і «відпав»
    lastFrameAt = now;
    if (f.pg) pg = f.pg;
    if (f.sp != null) game.watchers = f.sp | 0;
    if (seat != null && f.pa && f.pa[seat * 2] > 0) pinger.echo(f.pa[seat * 2], f.pa[seat * 2 + 1], now);
    if (f.e === e) {
      const sample = f.t * R.TICK_MS - performance.now();
      off = off == null ? sample : Math.max(sample, off - 1);
    }
    if (f.d >= 0) noteDesync(f.d);
    if (f.r?.length) {   // K28: реакції — повз журнал і світ
      const fr = freshReacts(f.r, reactSeen, (p, i) => (p === net.p || p === net.p2) && myReacts.has(i));
      reactSeen = fr.seen;
      for (const x of fr.list) showReact(x.p, x.e);
    }
    if (resyncing || !C) return;
    if (f.e !== e || f.f > count) { resync(); return; }
    if (!ingest(f.f, f.c)) { resync(); return; }
    if (f.w > w) w = f.w;
    advanceC();
  }

  function noteDesync(t) {
    net.desync = t;
    if (!desyncSaid) { desyncSaid = true; console.warn('Колія: світи розійшлись на тику', t); }
  }

  /** Нові записи журналу (номери з from). false — діра чи запис у вже остаточний тик: треба журнал заново. */
  function ingest(from, c, old = false) {
    const pTick = game.state ? game.state.tick : 0;
    for (let j = 0; j < c.length; j += 10) {
      const seq = c[j];
      if (seq < count) continue;
      if (seq !== count) return false;
      const t = c[j + 1];
      if (C && t < C.tick) { if (old) { count++; continue; } return false; }   // K44: зі знімком — уже в ньому
      const cmd = toCmd(c, j);
      if (!log.has(t)) log.set(t, []);
      log.get(t).push(cmd);
      count++;
      const i = c[j + 9];
      const k = i > 0 && (cmd.p === net.p || cmd.p === net.p2) ? pending.findIndex((x) => x.i === i) : -1;
      if (k >= 0) { if (pending[k].t !== t) dirty = true; pending.splice(k, 1); }
      else if (t < pTick) dirty = true;
    }
    return true;
  }

  /** Остаточний світ — до W+1. */
  function advanceC() {
    while (C.status === 'run' && C.tick <= w) {
      const t = C.tick;
      const cmds = log.get(t) || [];
      game.replay?.rec?.(C, cmds);   // K35
      prep(C, cmds);
      R.step(C, cmds);
      log.delete(t);
      if (C.tick % HASH_EVERY === 0) {
        const h = R.hash(C);
        hashes.set(C.tick, h);
        if (hashes.size > 64) hashes.delete(hashes.keys().next().value);
        // K24: e — щоб сервер не звірив хеш старої епохи з новою; лише останні звірки — пізній вхід доганяє тисячі тиків
        // за раз, і пачка хешів (раз на 100 тиків) з'їла б квоту 30 Input/с, а з нею — власні команди гравця
        if (w + 1 - C.tick < 2 * HASH_EVERY) {
          const m = { t: C.tick, h, e };   // тик — зараз, а не коли затримка відпустить (C тим часом піде далі)
          lagOut(() => conn?.send('Input', room, 'hash', m).catch(() => { }));
          if (C.tick % SNAP_EVERY === 0 && net.p >= 0) upSnap(C.tick, h);   // K44
        }
      }
    }
    const before = pending.length;
    pending = pending.filter((x) => x.t > C.tick - PENDING_TTL);
    if (pending.length !== before) { stats.dropped += before - pending.length; dirty = true; }
    // C наздогнав P (рідко: зв'язок стояв) — P не може бути позаду остаточного
    if (game.state && game.state.tick < C.tick) dirty = true;
  }

  /** K44: знімок C на тику t (хеш h) — на сервер окремим POST; він тримає підтверджений хешами інших. */
  function upSnap(t, h) {
    const ep = e, rm = room;
    encodeSnap(C, zip).then(({ z, b }) => {
      stats.snapUp++; stats.snapBytes = b.length;
      lagOut(() => fetch(`/api/games/koliya/${encodeURIComponent(rm)}/snap?e=${ep}&t=${t}&h=${h >>> 0}&z=${z}`,
        { method: 'POST', body: b, headers: { 'Content-Type': 'application/octet-stream' }, cache: 'no-store' }).catch(() => { }));
    }).catch((x) => console.warn('Колія: знімок не стиснувся', x));
  }

  function cmdsFor(t) {
    const a = (log.get(t) || []).slice();
    for (const x of pending) if (x.t === t) a.push(x.cmd);
    return a;
  }

  /** Своє керування на тик t: шлемо зміни руху (не частіше GAP_IN) і натиски/повтори дії. */
  /** Своє керування на тик t: шлемо зміни руху (не частіше GAP_IN) і натиски/повтори дії. Двоє за ПК — обидва. */
  function localInput(t) {
    const s = game.state;
    let ins = null;
    for (let q = 0; q < 2; q++) {
      const p = q ? net.p2 : net.p, pl = s.players[p];
      if (p < 0 || !pl || pl.out) continue;
      let v;
      if (q === 0 && opts.makeBot && (!bot || bot.p !== p)) bot = opts.makeBot(p);   // справжній бот (bot.js) знає своє p
      if (q === 0 && bot) v = bot.think(s, p);
      else { ins ??= input?.poll?.() || []; const a = ins[q]; v = a ? { mx: Math.round(a.mx * 100), my: Math.round(a.my * 100), use: !!a.use, throw: !!a.throw, horn: !!a.horn } : null; }
      if (!v) continue;
      const li = lastIn[q], lu = lastUse[q], Q = q ? { q: 1 } : {};
      const mx = Math.max(-100, Math.min(100, v.mx | 0)), my = Math.max(-100, Math.min(100, v.my | 0));
      if ((mx !== li.mx || my !== li.my) && t - li.t >= GAP_IN) {
        lastIn[q] = { mx, my, t };
        send('in', { mx, my, ...Q }, t, { p, k: 'in', mx, my });
      }
      if (v.use) {
        const press = !lu.down;
        if (press || (pl.cd === 0 && t - lu.t >= GAP_USE)) {
          const h = press ? 0 : 1;
          const pose = { x: pl.x, y: pl.y, fx: pl.fx, fy: pl.fy };
          lu.t = t;
          send('use', { ...pose, h, ...Q }, t, { p, k: 'use', ...pose, h });
        }
      }
      lu.down = !!v.use;
      // K18: кидок (з позою — куди полетить, залежить від пози) і гудок — раз на натиск
      if (v.throw) { const pose = { x: pl.x, y: pl.y, fx: pl.fx, fy: pl.fy }; send('throw', { ...pose, ...Q }, t, { p, k: 'throw', ...pose }); }
      if (v.horn) send('horn', { ...Q }, t, { p, k: 'horn' });
    }
  }

  function send(kind, payload, t, cmd) {
    const i = ++idSeq;
    pending.push({ t, i, cmd });
    stats.sent++;
    const ep = e;   // K24: епоха — запізнілий після перезапуску ввід сервер викине
    lagOut(() => conn.send('Input', room, kind, { ...payload, t, i, e: ep }).catch((x) => console.warn('Колія: Input', x)));
  }

  /** Передбачений світ до тику target; нові тики — зі своїм керуванням і подіями. */
  function advanceP(target, budget) {
    const s = game.state;
    while (s.status === 'run' && s.tick < target && budget-- > 0) {
      const t = s.tick, fresh = t >= hi;
      if (fresh) localInput(t);
      const cmds = cmdsFor(t);
      prep(s, cmds);
      R.step(s, cmds);
      if (fresh) { hi = t + 1; onStep?.(s); }
    }
  }

  function rebuild() {
    stats.rebuilds++;
    const s = game.state, back = s.tick;
    smooth.before(s);
    copyInto(s, C);
    advanceP(back, STEPS_PER_FRAME * 4);
    if (s.tick === back) smooth.after(s); else smooth.before({ players: [] });   // інший тик — не згладжуємо
  }

  /** Малювання (гачок main.js): гравці — у видимій (згладженій) позі лише на час view.draw. */
  function draw(view) {
    const s = game.state;
    if (!s || !view) return;
    const undo = smooth.apply(s);
    try { view.draw(s, net.alpha); } finally { undo(); }
  }

  function update(now = performance.now()) {
    padReacts(); peopleUi(now);   // K28
    if (!ready || !C || !game.state) return;
    if (dirty) { dirty = false; rebuild(); }
    const est = (now + off) / R.TICK_MS;
    const target = Math.min(Math.floor(est), w + 1 + AHEAD);
    advanceP(target, STEPS_PER_FRAME);
    net.alpha = Math.max(0, Math.min(1, est - game.state.tick));
  }

  async function restart(seed = game.seed, params = game.params) {
    if (!conn || !room) return;
    let r;
    try { r = await conn.invoke('Act', room, 'restart', { seed: seed >>> 0, params: params || {} }); }
    catch (x) { console.warn('Колія: перезапуск не дійшов', x); return; }   // QA K21: обрив — без unhandled rejection
    if (!r.ok) console.warn('Колія: перезапуск —', r.message);
    else resync();
  }

  return net;
}
