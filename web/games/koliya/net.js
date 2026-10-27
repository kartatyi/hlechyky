// «Колія» — мережа: кілька браузерів грають одну партію за столом сайту. Власник: K3.
// Контракт: createNet(game, { table, nick, input, bot, onWorld(s), onStep(s) }) → net
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
import * as R from './rules.js';

const GAP_IN = 2;          // тиків між 'in' (≤ 10/с)
const GAP_USE = 3;         // тиків між повторами затиснутого use
const HASH_EVERY = 100;    // тиків між звірками
const AHEAD = 40;          // на скільки тиків P може випередити W
const PENDING_TTL = 60;    // своя команда без луни стільки тиків після закриття — загубилась (квота), викидаємо
const STEPS_PER_FRAME = 600;
const K = { 1: 'in', 2: 'use', 3: 'join', 4: 'leave' };

function loadScript(src) {
  return new Promise((ok, bad) => {
    const s = document.createElement('script');
    s.src = src; s.onload = ok; s.onerror = () => bad(new Error('не завантажився ' + src));
    document.head.appendChild(s);
  });
}

/** Скопіювати світ src у dst на місці: типізовані масиви тієї ж довжини — .set(), решта — глибока копія. */
function copyInto(dst, src) {
  for (const k of Object.keys(dst)) if (!(k in src)) delete dst[k];
  for (const k of Object.keys(src)) {
    const a = src[k], b = dst[k];
    if (ArrayBuffer.isView(a) && b && b.constructor === a.constructor && b.length === a.length) b.set(a);
    else dst[k] = a && typeof a === 'object' ? structuredClone(a) : a;
  }
  return dst;
}

/** Затиснутий use з журналу: rules має бачити «use був і в попередньому тику». */
function prep(s, cmds) {
  for (const c of cmds) if (c.k === 'use' && c.h) { const pl = s.players[c.p]; if (pl) pl.ut = s.tick; }
}

function toCmd(c, j) {
  const p = c[j + 2], k = K[c[j + 3]];
  if (k === 'in') return { p, k, mx: c[j + 4], my: c[j + 5] };
  if (k === 'use') return { p, k, x: c[j + 4], y: c[j + 5], fx: c[j + 6], fy: c[j + 7], h: c[j + 8] };
  if (k === 'join') return { p, k };
  return { p, k: 'leave' };
}

/** Скриптовий водій для перевірок (?drive=1): блукає й тисне «дію». */
export function createDriver() {
  let n = 0, until = 0, dir = [0, 0];
  const DIRS = [[100, 0], [-100, 0], [0, 100], [0, -100], [70, 70], [0, 0]];
  return {
    think() {
      n++;
      if (n >= until) { dir = DIRS[(Math.random() * DIRS.length) | 0]; until = n + 8 + ((Math.random() * 40) | 0); }
      return { mx: dir[0], my: dir[1], use: n % 9 < 3 };
    },
  };
}

export function createNet(game, opts = {}) {
  const { input, onWorld, onStep } = opts;
  let table = String(opts.table || 'auto');
  const nick = opts.nick || '';
  let bot = opts.bot || null;           // { think(state) → { mx, my, use } } — керує своїм гравцем замість клавіатури

  let conn = null, room = null, seat = null, ready = false, resyncing = false, lobby = null;
  let e = 0, C = null, count = 0, w = -1, hi = 0, dirty = false, off = null, idSeq = 0, desyncSaid = false;
  let lastIn = { mx: 0, my: 0, t: -99 }, lastUse = { t: -99, down: false };
  const log = new Map();                // тик → [cmd] (лише тики ≥ C.tick)
  let pending = [];                     // свої без луни: { t, i, cmd }
  const hashes = new Map();             // тик → хеш C
  const stats = { frames: 0, rebuilds: 0, resyncs: 0, sent: 0, dropped: 0 };

  const net = {
    p: -1, alpha: 0, status: 'старт', desync: -1,
    get room() { return room; },
    get ready() { return ready; },
    get C() { return C; },
    start, update, restart,
    info() {
      return { room, seat, p: net.p, e, count, w, cTick: C?.tick, pTick: game.state?.tick, pending: pending.length,
        status: net.status, desync: net.desync, cStatus: C?.status, hash: C ? R.hash(C) : 0, stats: { ...stats } };
    },
    hashAt: (t) => hashes.get(t),
    hashes: () => [...hashes.entries()],
    setBot(b) { bot = b; },
  };

  function say(st) { net.status = st; }

  async function start() {
    if (!window.signalR) await loadScript('/static/signalr.min.js');
    conn = new signalR.HubConnectionBuilder()
      .withUrl('/hub?nick=' + encodeURIComponent(nick))
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (r) => [0, 1000, 2000, 3000, 5000][Math.min(r.previousRetryCount, 4)] })
      .build();
    conn.on('rooms', (list) => { lobby = list; });
    conn.on('frame', (m) => { if (m && m.id === room) onFrame(m.f); });
    conn.on('room', (rv) => { if (rv && rv.room && rv.room.id === room) onRoom(rv); });
    conn.onreconnecting(() => { ready = false; say('перепідключаюсь…'); });
    conn.onreconnected(() => enter().catch((x) => { console.warn('Колія: не зайшов знову', x); say('не зайшов: ' + x.message); }));
    say('підключаюсь…');
    await conn.start();
    await enter();
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
    if (!j.ok) console.info('Колія: JoinRoom —', j.message);   // «уже за столом» — це нормально
    await conn.invoke('WatchRoom', room);
    await resync();
  }

  function onRoom(rv) {
    seat = rv.seat ?? null;
    const v = rv.view;
    if (!v) return;
    net.p = v.p ?? -1;
    game.me = net.p;
    if (v.e !== e && !resyncing) resync();
  }

  async function resync() {
    if (!room) return;
    resyncing = true;
    stats.resyncs++;
    say('беру журнал…');
    try {
      const res = await fetch(`/api/games/koliya/${encodeURIComponent(room)}/log?from=0`, { cache: 'no-store' });
      if (!res.ok) throw new Error('журнал: ' + res.status);
      build(await res.json());
    } catch (x) {
      console.warn('Колія: журнал не взявся', x);
      say('журнал не взявся');
      setTimeout(() => { if (!ready) resync(); }, 2000);
    } finally { resyncing = false; }
  }

  /** Світ з нуля: newGame + увесь журнал до водяного знаку. */
  function build(j) {
    e = j.e;
    C = R.newGame(j.seed >>> 0, j.prm || {}, j.n0 || 1);
    log.clear(); count = 0; pending = []; hashes.clear(); hi = 0;
    lastIn = { mx: 0, my: 0, t: -99 }; lastUse = { t: -99, down: false };
    if (seat != null && j.pOf) net.p = j.pOf[seat] ?? -1;
    game.me = net.p;
    game.seed = j.seed >>> 0;
    if (off == null) off = j.t * R.TICK_MS - performance.now();
    if (!ingest(j.f | 0, j.c || [])) { console.warn('Колія: журнал з дірою'); return; }
    w = j.w;
    if (j.d >= 0) noteDesync(j.d);
    advanceC();
    const me = C.players[net.p];   // що rules уже знає про мій рух — від цього й міряємо зміни
    if (me) lastIn = { mx: me.mx | 0, my: me.my | 0, t: -99 };
    const fresh =!game.state || game.state.w !== C.w || game.state.h !== C.h;
    if (fresh) game.state = R.clone(C); else copyInto(game.state, C);
    hi = C.tick;
    ready = true;
    say('грає');
    onWorld?.(game.state);
  }

  function onFrame(f) {
    stats.frames++;
    if (!f) return;
    const sample = f.t * R.TICK_MS - performance.now();
    off = off == null ? sample : Math.max(sample, off - 1);
    if (f.d >= 0) noteDesync(f.d);
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
      const k = i > 0 && cmd.p === net.p ? pending.findIndex((x) => x.i === i) : -1;
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
        conn?.send('Input', room, 'hash', { t: C.tick, h }).catch(() => { });
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
  function localInput(t) {
    const s = game.state, pl = s.players[net.p];
    if (net.p < 0 || !pl || pl.out) return;
    let v;
    if (bot) v = bot.think(s, net.p);
    else { const a = input?.poll?.()[0]; v = a ? { mx: Math.round(a.mx * 100), my: Math.round(a.my * 100), use: !!a.use } : null; }
    if (!v) return;
    const mx = Math.max(-100, Math.min(100, v.mx | 0)), my = Math.max(-100, Math.min(100, v.my | 0));
    if ((mx !== lastIn.mx || my !== lastIn.my) && t - lastIn.t >= GAP_IN) {
      lastIn = { mx, my, t };
      send('in', { mx, my }, t, { p: net.p, k: 'in', mx, my });
    }
    if (v.use) {
      const press = !lastUse.down;
      if (press || (pl.cd === 0 && t - lastUse.t >= GAP_USE)) {
        const h = press ? 0 : 1;
        const pose = { x: pl.x, y: pl.y, fx: pl.fx, fy: pl.fy };
        lastUse.t = t;
        send('use', { ...pose, h }, t, { p: net.p, k: 'use', ...pose, h });
      }
    }
    lastUse.down = !!v.use;
  }

  function send(kind, payload, t, cmd) {
    const i = ++idSeq;
    pending.push({ t, i, cmd });
    stats.sent++;
    conn.send('Input', room, kind, { ...payload, t, i }).catch((x) => console.warn('Колія: Input', x));
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
    copyInto(s, C);
    advanceP(back, STEPS_PER_FRAME * 4);
  }

  function update(now = performance.now()) {
    if (!ready || !C || !game.state) return;
    if (dirty) { dirty = false; rebuild(); }
    const est = (now + off) / R.TICK_MS;
    const target = Math.min(Math.floor(est), w + 1 + AHEAD);
    advanceP(target, STEPS_PER_FRAME);
    net.alpha = Math.max(0, Math.min(1, est - game.state.tick));
  }

  async function restart(seed = game.seed, params = game.params) {
    if (!conn || !room) return;
    const r = await conn.invoke('Act', room, 'restart', { seed: seed >>> 0, params: params || {} });
    if (!r.ok) console.warn('Колія: перезапуск —', r.message);
    else resync();
  }

  return net;
}
