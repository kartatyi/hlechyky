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
  return { p, k: 'leave' };
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
  let e = 0, C = null, count = 0, w = -1, hi = 0, dirty = false, off = null, idSeq = 0, desyncSaid = false;
  let lastIn = [], lastUse = [];       // [q] — свій рух/дія: q 0 — я, 1 — другий за цим ПК (?p=2)
  let asked2 = 0;                       // епоха, у якій уже просили другого гравця
  const log = new Map();                // тик → [cmd] (лише тики ≥ C.tick)
  let pending = [];                     // свої без луни: { t, i, cmd }
  const hashes = new Map();             // тик → хеш C
  const stats = { frames: 0, rebuilds: 0, resyncs: 0, sent: 0, dropped: 0 };
  // K28: люди за столом — реакції (повз журнал), панелька в куті
  let reactSeen = -1, ui = null;
  const myReacts = new Set(), reactGate = createReactGate();

  const net = {
    p: -1, p2: -1, alpha: 0, status: 'старт', desync: -1,
    get room() { return room; },
    get ready() { return ready; },
    get C() { return C; },
    start, update, restart, draw,
    info() {
      return { room, seat, p: net.p, p2: net.p2, e, count, w, cTick: C?.tick, pTick: game.state?.tick, pending: pending.length,
        status: net.status, desync: net.desync, cStatus: C?.status, hash: C ? R.hash(C) : 0, stats: { ...stats },
        lag, jit, smooth: { ...smooth.st } };
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
    const m = { e: k, i, ...(q ? { q: 1 } : {}) };
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
    el.append(re);
    document.body.appendChild(el);
    ui = { el, re, btns };
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
    if (!ui) return;
    const playing = net.p >= 0;
    ui.re.style.display = playing ? '' : 'none';
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
    conn.on('room', (rv) => lagIn(() => { if (rv && rv.room && rv.room.id === room) onRoom(rv); }));
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
        const r = await conn.invoke('CreateRoom', 'koliya', {});
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
      const res = await fetch(`/api/games/koliya/${encodeURIComponent(room)}/log?from=0`, { cache: 'no-store' });
      if (!res.ok) throw new Error('журнал: ' + res.status);
      const j = await res.json();
      if (lag) await new Promise((r) => lagIn(r));
      build(j);
    } catch (x) {
      console.warn('Колія: журнал не взявся', x);
      say(/: 404$/.test(x?.message || '') ? 'стола нема (закрився?) — відкрий новий' : 'журнал не взявся: ' + (x?.message || x));
      setTimeout(() => { if (!ready) resync(); }, 2000);
    } finally { resyncing = false; }
  }

  /** Світ з нуля: newGame + увесь журнал до водяного знаку. */
  function build(j) {
    if (j.e !== e) { off = null; net.desync = -1; desyncSaid = false; }   // нова епоха — тик з нуля, старий зсув годинника й розбіжність не годяться
    e = j.e;
    C = R.newGame(j.seed >>> 0, j.prm || {}, j.n0 || 1);
    log.clear(); count = 0; pending = []; hashes.clear(); hi = 0;
    lastIn = [0, 1].map(() => ({ mx: 0, my: 0, t: -99 })); lastUse = [0, 1].map(() => ({ t: -99, down: false }));
    if (seat != null && j.pOf) net.p = j.pOf[seat] ?? -1;
    if (seat != null && j.p2Of) net.p2 = j.p2Of[seat] ?? -1;
    game.me = net.p;
    if (j.nm) game.names = namesFrom(j.nm);   // K28: мітки над гравцями — ніки
    game.seed = j.seed >>> 0;
    if (off == null) off = j.t * R.TICK_MS - performance.now();
    if (!ingest(j.f | 0, j.c || [])) { console.warn('Колія: журнал з дірою'); return; }
    w = j.w;
    if (j.d >= 0) noteDesync(j.d);
    advanceC();
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
  function ingest(from, c) {
    const pTick = game.state ? game.state.tick : 0;
    for (let j = 0; j < c.length; j += 10) {
      const seq = c[j];
      if (seq < count) continue;
      if (seq !== count) return false;
      const t = c[j + 1];
      if (C && t < C.tick) return false;
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
      prep(C, cmds);
      R.step(C, cmds);
      log.delete(t);
      if (C.tick % HASH_EVERY === 0) {
        const h = R.hash(C);
        hashes.set(C.tick, h);
        if (hashes.size > 64) hashes.delete(hashes.keys().next().value);
        const m = { t: C.tick, h };   // тик — зараз, а не коли затримка відпустить (C тим часом піде далі)
        lagOut(() => conn?.send('Input', room, 'hash', m).catch(() => { }));
      }
    }
    const before = pending.length;
    pending = pending.filter((x) => x.t > C.tick - PENDING_TTL);
    if (pending.length !== before) { stats.dropped += before - pending.length; dirty = true; }
    // C наздогнав P (рідко: зв'язок стояв) — P не може бути позаду остаточного
    if (game.state && game.state.tick < C.tick) dirty = true;
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
    lagOut(() => conn.send('Input', room, kind, { ...payload, t, i }).catch((x) => console.warn('Колія: Input', x)));
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
