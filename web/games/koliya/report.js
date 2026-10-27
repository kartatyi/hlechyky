// «Колія» — звіт партії (27.10): чим скінчилась, скільки тривала, хто й як грав, як ішли кадри й мережа. Сервер кладе
// рядком у data/diag/koliya-<дата>.jsonl (KoliyaSetup: POST /api/games/koliya/report, вимикач Diag:Koliya), а
// D:\or-wt\_tools\koliya\report.py зводить: де програють, скільки грають, у кого гальмує. На правила й світ не впливає.
// Контракт: createReport(game, { net?, prefs?, send? }) → { frame(now, s), flush(end), get cur() }
//   frame — щокадру з main.js (не в повторі): рахує видимі кадри й пінг; раз на секунду — знімок підсумку; won/lost —
//   звіт; нова партія замість живої — звіт «restart»; сторінку закрито посеред гри — «left» (pagehide, sendBeacon).
//   У мережі звіт шле кожен, хто сидить за столом (свої кадри й пінг; партію впізнати за net.room + net.e), глядач — ні.
//   Не шле: ?report=0, ?test, автоматизація (navigator.webdriver); коротші за MIN_SEC «restart»/«left» — теж ні.
//   buildReport(s, x) — чисте, без DOM (Node-тест).
import { TPS, U, G, wagonPos, defaultParams } from './rules.js';
import { isChumak } from './theme.js';

export const REPORT_URL = '/api/games/koliya/report';
export const MIN_SEC = 5;     // «restart»/«left» коротші за 5 с — не партія, а клацання
const SNAP_MS = 1000;         // як часто оновлюємо підсумок живої партії (у мережі стан нової партії затирає старий)
const SLOW_MS = 34;           // кадр довший — уже менше 30 к/с
const JANK_MS = 100;          // ривок, який видно оком

/** Перша (найлівіша) клітинка станції, -1 — нема */
function stationX(s) {
  const w = s.w | 0, h = s.h | 0, g = s.ground;
  if (!g) return -1;
  for (let x = 0; x < w; x++) for (let y = 0; y < h; y++) if (g[y * w + x] === G.STATION) return x;
  return -1;
}

/** Підсумок партії зі стану s; x — те, чого в стані нема (кінець, пристрій, кадри, мережа). Чисте. */
export function buildReport(s, x = {}) {
  const p = s.params || {}, st = s.stats || {}, E = s.endless, def = defaultParams();
  const prm = {};   // лише те, що відрізнялось від типового: так видно, з якими повзунками грали
  for (const k of Object.keys(p)) if (k !== 'mode' && k in def && p[k] !== def[k]) prm[k] = p[k];
  let head = 0;
  try { head = wagonPos(s, 0).x / U; } catch { }
  const sx = stationX(s);
  const r = {
    v: 1,
    end: x.end || s.status,
    why: s.reason || '',
    mode: p.mode === 'tutorial' ? 'tutorial' : x.daily ? 'daily' : E ? 'endless' : 'normal',
    seed: s.seed >>> 0,
    sec: Math.round((s.tick | 0) / TPS),
    prog: sx > 0 ? Math.max(0, Math.min(1, Math.round(head / sx * 100) / 100)) : 0,
    pl: s.players?.length | 0,
    local: x.local | 0,
    bots: x.bots | 0,
    st: { rails: st.railsLaid | 0, crafted: st.crafted | 0, wood: st.wood | 0, iron: st.iron | 0, hits: st.hits | 0,
      douses: st.douses | 0, bridges: st.bridges | 0 },
    prm,
  };
  if (E) r.stations = E.stations | 0;
  for (const k of ['theme', 'look', 'tips', 'zeal', 'net', 'fps', 'dev', 'hid']) if (x[k] !== undefined) r[k] = x[k];
  return r;
}

/** Браузер і система коротко («Chrome · Windows»), розмір вікна, ядра й пам'ять — без повного User-Agent */
function device() {
  const ua = (typeof navigator !== 'undefined' && navigator.userAgent) || '';
  const b = /Edg\//.test(ua) ? 'Edge' : /OPR\//.test(ua) ? 'Opera' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome'
    : /Safari\//.test(ua) ? 'Safari' : 'інше';
  const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad|iPod/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows'
    : /Mac OS X/.test(ua) ? 'Mac' : /SteamOS|Valve Steam/i.test(ua) ? 'SteamOS' : /Linux/.test(ua) ? 'Linux' : 'інше';
  return { ua: `${b} · ${os}`, cores: navigator.hardwareConcurrency | 0, mem: navigator.deviceMemory | 0 };
}

export function createReport(game, opts = {}) {
  const q = new URLSearchParams(location.search);
  const off = q.get('report') === '0' || q.has('test') || !!navigator.webdriver;
  const net = opts.net || null, prefs = opts.prefs || {};
  const send = opts.send || ((r) => {
    const body = JSON.stringify(r);
    try { if (navigator.sendBeacon?.(REPORT_URL, new Blob([body], { type: 'application/json' }))) return; } catch { }
    fetch(REPORT_URL, { method: 'POST', body, keepalive: true, headers: { 'Content-Type': 'application/json' } }).catch(() => { });
  });
  const dev0 = device();
  let cur = null, lastNow = 0;

  const token = (s) => (net ? 'e' + (net.epoch ?? 0) : s);   // у мережі стан той самий об'єкт і в новій партії
  function begin(tok) {
    cur = { tok, e: net?.epoch, last: null, sent: false, frames: 0, ms: 0, slow: 0, jank: 0, pingN: 0, pingSum: 0, pingMax: 0, pingAt: 0,
      snapAt: 0, hidMs: 0, hidAt: document.hidden ? performance.now() : 0 };
  }
  function extra(end) {
    const c = cur, view = game.view;
    const x = {
      end, local: net ? (net.p2 >= 0 ? 2 : 1) : game.local | 0, bots: net ? 0 : game.bots | 0, daily: !!game.daily,
      theme: isChumak() ? 'chumak' : 'koliya', look: view?.look, tips: prefs.tips !== false, zeal: game.bots ? prefs.zeal : undefined,
      fps: { avg: c.ms > 0 ? Math.round(c.frames / (c.ms / 1000)) : 0, slow: c.frames ? Math.round(c.slow / c.frames * 100) : 0,
        jank: c.jank, start: game.fpsStart | 0 },
      dev: { ...dev0, w: innerWidth, h: innerHeight, dpr: Math.round((devicePixelRatio || 1) * 100) / 100, q: view?.quality ?? prefs.quality,
        gpu: view?.gpu || '', pad: !!game.input?.padCount?.(), touch: document.documentElement.classList.contains('kol-touch') },
      hid: Math.round((c.hidMs + (c.hidAt ? performance.now() - c.hidAt : 0)) / 1000),
    };
    return x;
  }
  /** Мережа — лише в мить відправки (net.info() рахує хеш світу — не щосекунди) */
  function netInfo() {
    const c = cur, i = net.info?.() || {};
    // e — епоха цієї партії (на «restart» net.epoch уже нової); me — нік за столом: гостя сервер знає лише як «гість»
    return { room: net.room, e: c.e, p: net.p, me: (game.names || [])[net.p] || '',
      ping: c.pingN ? Math.round(c.pingSum / c.pingN) : -1, pingMax: c.pingMax,
      rebuilds: i.stats?.rebuilds | 0, resyncs: i.stats?.resyncs | 0, dropped: i.stats?.dropped | 0, desync: (net.desync ?? -1) >= 0,
      lag: i.lag | 0, names: (game.names || []).length };
  }
  function ship(r) {
    cur.sent = true;
    if (off || (net && !(net.p >= 0))) return;   // перевірки й глядач — без звіту
    if (net) r.net = netInfo();
    try { send(r); } catch { }
  }

  function frame(now, s) {
    if (!s) return;
    const tok = token(s);
    if (!cur || cur.tok !== tok) {
      // нова партія замість живої — та, попередня, теж партія: «restart»
      if (cur && !cur.sent && cur.last && cur.last.end === 'run' && cur.last.sec >= MIN_SEC) ship({ ...cur.last, end: 'restart' });
      begin(tok);
      lastNow = now;
    }
    const dt = now - lastNow; lastNow = now;
    if (dt > 0 && dt < 1000 && !document.hidden) { cur.frames++; cur.ms += dt; if (dt > SLOW_MS) cur.slow++; if (dt > JANK_MS) cur.jank++; }
    if (net && now - cur.pingAt >= 1000 && net.ping >= 0) { cur.pingAt = now; cur.pingN++; cur.pingSum += net.ping; cur.pingMax = Math.max(cur.pingMax, net.ping | 0); }
    if (cur.sent) return;
    if (s.status === 'won' || s.status === 'lost') { ship(buildReport(s, extra(s.status))); return; }
    if (s.status === 'run' && !net?.wait && now - cur.snapAt >= SNAP_MS) { cur.snapAt = now; cur.last = buildReport(s, extra('run')); }
  }

  /** Сторінку закривають (чи гру зупинили самі): жива партія — звіт з кінцем end */
  function flush(end = 'left') {
    const s = game.state;
    if (!cur || cur.sent || !s || s.status !== 'run' || net?.wait) return;
    const r = buildReport(s, extra(end));
    if (r.sec >= MIN_SEC) ship(r);
  }

  if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', () => {
      if (!cur) return;
      if (document.hidden) cur.hidAt = performance.now();
      else if (cur.hidAt) { cur.hidMs += performance.now() - cur.hidAt; cur.hidAt = 0; }
    });
    addEventListener('pagehide', () => flush('left'));
  }
  return { frame, flush, get cur() { return cur; } };
}
