// «Колія» — HUD і екрани (DOM поверх канви). Власник: K9.
// Контракт: createHud(root, { players: n, onStart() }) → { update(state, game), dispose() }
//  root — <div id="kol-hud"> (стилі тут, класи з префіксом kol-); update — щокадру з main.js.
//  Стартовий екран тримає game.paused = true до першого натиску клавіші/кнопки пада/кліку, тоді onStart().
//  Поля стану читаються обережно (?? 0): правила пишуться паралельно.
import { TPS, U, G, I, wagonPos } from './rules.js';

const COLORS = ['#ffd23f', '#ff5fa2', '#5fd3ff', '#b6ff5f'];
const HOLD_ICON = { [I.WOOD]: '🪵', [I.IRON]: '⛓', [I.RAIL]: '🛤', [I.AXE]: '🪓', [I.PICK]: '⛏', [I.BUCKET]: '🪣', [I.BUCKET_FULL]: '💧' };
const HOLD_NAME = { [I.WOOD]: 'дерево', [I.IRON]: 'залізо', [I.RAIL]: 'рейки', [I.AXE]: 'сокира', [I.PICK]: 'кайло', [I.BUCKET]: 'відро', [I.BUCKET_FULL]: 'повне відро' };
const ALARM_SEC = 3;

const CSS = `
#kol-hud.kol-root { position: fixed; inset: 0; left: 0; top: 0; padding: 0; background: none; border-radius: 0;
  pointer-events: none; font: 600 clamp(14px, 1.6vw, 22px)/1.25 system-ui, sans-serif; color: #f4efe2; z-index: 5;
  text-shadow: 0 1px 2px #000a; }
.kol-root * { box-sizing: border-box; }
.kol-box { background: #0f120dcc; border: 1px solid #ffffff22; border-radius: 10px; padding: .35em .7em; }
.kol-top { position: absolute; left: 12px; right: 72px; top: 10px; display: flex; gap: .6em; align-items: center; }
.kol-prog { flex: 1; position: relative; height: 1.5em; padding: 0; overflow: hidden; }
.kol-prog-track { position: absolute; top: 0; bottom: 0; left: 0; background: #8a7a5a55; }
.kol-prog-head { position: absolute; top: 50%; transform: translate(-50%, -50%); font-size: 1.1em; transition: left .2s linear; }
.kol-prog-end { position: absolute; top: 0; bottom: 0; width: 3px; background: #ffd23f; transform: translateX(-1px); }
.kol-prog-st { position: absolute; right: .3em; top: 50%; transform: translateY(-50%); }
.kol-prog-ahead { position: absolute; left: 50%; top: 50%; transform: translate(-50%, -50%); white-space: nowrap; font-size: .8em; opacity: .9; }
.kol-stat { white-space: nowrap; }
.kol-res { position: absolute; left: 12px; top: calc(10px + 2.6em); display: flex; flex-direction: column; gap: .4em; }
.kol-row { display: flex; gap: .7em; align-items: center; white-space: nowrap; }
.kol-full { color: #ffb347; }
.kol-craft { display: inline-block; width: 3em; height: .5em; background: #ffffff22; border-radius: 4px; overflow: hidden; vertical-align: middle; }
.kol-craft > i { display: block; height: 100%; width: 0; background: #b6ff5f; }
.kol-heat { display: inline-block; width: 7em; height: .7em; background: #ffffff22; border-radius: 5px; overflow: hidden; vertical-align: middle; }
.kol-heat > i { display: block; height: 100%; width: 0; background: #ffd23f; }
.kol-hot .kol-heat > i { background: #ff3b2f; }
.kol-hot { animation: kol-blink .6s steps(2) infinite; }
@keyframes kol-blink { 50% { border-color: #ff3b2f; background: #5a0f0bcc; } }
.kol-banner { position: absolute; left: 50%; top: 22%; transform: translateX(-50%); text-align: center; font-size: 2.1em;
  padding: .3em .9em; border-radius: 14px; display: none; white-space: nowrap; }
.kol-fire { background: #a3160ee6; border: 2px solid #ffb347; animation: kol-pulse .5s ease-in-out infinite alternate; }
.kol-alarm { top: 36%; font-size: 1.6em; background: #8a5a00e6; border: 2px solid #ffd23f; animation: kol-pulse .4s ease-in-out infinite alternate; }
@keyframes kol-pulse { to { transform: translateX(-50%) scale(1.06); } }
.kol-count { position: absolute; left: 50%; top: 30%; transform: translate(-50%, -50%); text-align: center; display: none; }
.kol-count b { display: block; font-size: 5em; line-height: 1; color: #ffd23f; text-shadow: 0 3px 10px #000; }
.kol-pls { position: absolute; left: 12px; bottom: 12px; display: flex; gap: .5em; flex-wrap: wrap; }
.kol-pl { border-left: 5px solid; min-width: 5.5em; }
.kol-screen { position: absolute; inset: 0; display: none; align-items: center; justify-content: center; background: #0b0d09c4; pointer-events: auto; }
.kol-card { max-width: min(92vw, 40em); background: #171b13f2; border: 1px solid #ffffff2a; border-radius: 16px; padding: 1.2em 1.6em; text-align: center; }
.kol-card h1 { margin: 0 0 .2em; font-size: 2.6em; color: #ffd23f; }
.kol-card p { margin: .4em 0; font-weight: 500; }
.kol-keys { display: grid; grid-template-columns: auto 1fr; gap: .25em .9em; text-align: left; margin: .8em auto; width: fit-content; font-weight: 500; }
.kol-keys b { font-weight: 700; }
.kol-hint { opacity: .7; font-size: .85em; }
.kol-go { margin-top: .8em; font-size: 1.2em; color: #b6ff5f; animation: kol-blink2 1.2s ease-in-out infinite; }
@keyframes kol-blink2 { 50% { opacity: .35; } }
.kol-sum { display: grid; grid-template-columns: auto auto; gap: .2em 1em; width: fit-content; margin: .6em auto; text-align: left; font-weight: 500; }
`;

function el(tag, cls, parent, html) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (html != null) e.innerHTML = html;
  if (parent) parent.appendChild(e);
  return e;
}
const fmtTime = (sec) => { sec = Math.max(0, Math.floor(sec)); return `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}`; };

export function createHud(root, opts = {}) {
  const style = el('style', null, document.head); style.textContent = CSS;
  root.classList.add('kol-root');
  root.textContent = '';
  const cache = new Map();   // пишемо в DOM лише зміни
  const set = (node, key, v) => { if (cache.get(key) !== v) { cache.set(key, v); node[key.split('|')[1]] = v; } };
  const txt = (node, id, v) => set(node, id + '|textContent', v);

  // — верх: прогрес, час, швидкість
  const top = el('div', 'kol-top', root);
  const prog = el('div', 'kol-box kol-prog', top);
  const progTrack = el('div', 'kol-prog-track', prog);
  const progEnd = el('div', 'kol-prog-end', prog);
  const progAhead = el('div', 'kol-prog-ahead', prog);
  el('div', 'kol-prog-st', prog, '🏁');
  const progHead = el('div', 'kol-prog-head', prog, '🚂');
  const timeEl = el('div', 'kol-box kol-stat', top);
  const speedEl = el('div', 'kol-box kol-stat', top);

  // — ресурси й котел
  const res = el('div', 'kol-res', root);
  const store = el('div', 'kol-box kol-row', res);
  const sWood = el('span', null, store), sIron = el('span', null, store);
  const craft = el('div', 'kol-box kol-row', res);
  const cRails = el('span', null, craft);
  const cBar = el('span', 'kol-craft', craft); const cFill = el('i', null, cBar);
  const boiler = el('div', 'kol-box kol-row', res);
  el('span', null, boiler, '♨');
  const hBar = el('span', 'kol-heat', boiler); const hFill = el('i', null, hBar);
  const hPct = el('span', null, boiler);

  // — банери
  const fire = el('div', 'kol-banner kol-fire', root);
  const alarm = el('div', 'kol-banner kol-alarm', root, '⚠ Рейки закінчуються!');
  const count = el('div', 'kol-count', root);
  const countN = el('b', null, count); const countT = el('div', 'kol-box', count, 'паротяг рушає');

  // — таблички гравців
  const pls = el('div', 'kol-pls', root);
  const plEls = [];

  // — старт
  const start = el('div', 'kol-screen', root);
  const n = opts.players ?? 1;
  el('div', 'kol-card', start, `
    <h1>🚂 Колія</h1>
    <p>Паротяг сам повзе до станції 🏁 — встигніть класти рейки перед ним.</p>
    <p>Рубайте ліс 🪓, бийте скелі ⛏, несіть у склад — майстерня робить рейки 🛤.
      Котел гріється: відро води 💧 на паротяг, інакше пожежа.</p>
    <div class="kol-keys">
      <b style="color:${COLORS[0]}">П1</b><span>WASD — ходити, Space / E — дія</span>
      <b style="color:${COLORS[1]}">П2</b><span>стрілки — ходити, Enter — дія</span>
      <b>🎮</b><span>стік — ходити, A — дія</span>
      <b>R</b><span>рестарт · <b>F1</b> — налаштування</span>
    </div>
    <p class="kol-hint">${n > 1 ? `Гравців: ${n}. ` : ''}<code>?p=2</code> — удвох, <code>&amp;bot=1</code> — додати бота</p>
    <div class="kol-go">Натисніть будь-яку кнопку</div>`);

  // — кінець
  const end = el('div', 'kol-screen', root);
  end.style.pointerEvents = 'none';   // не заступати ⚙ панелі
  const endCard = el('div', 'kol-card', end);

  let started = false, lastState = null, game0 = null, stationX = -1;
  const begin = () => {
    if (started) return;
    started = true; start.style.display = 'none';
    if (game0) game0.paused = false;
    opts.onStart?.();
  };
  const onKey = (e) => { if (!started && !['F1', 'Backquote', 'Shift', 'Control', 'Alt', 'Meta'].includes(e.key) && e.code !== 'Backquote') begin(); };
  addEventListener('keydown', onKey);
  start.addEventListener('pointerdown', begin);

  function padPressed() {
    try { for (const p of navigator.getGamepads?.() || []) if (p && p.buttons.some((b) => b.pressed)) return true; } catch { }
    return false;
  }

  function findStation(s) {
    if (s.ground) for (let x = 0; x < s.w; x++) for (let y = 0; y < s.h; y++) if (s.ground[y * s.w + x] === G.STATION) return x;
    return (s.w ?? 1) - 1;
  }

  function update(s, game) {
    if (!s) return;
    game0 = game;
    if (!started) { if (game) game.paused = true; if (padPressed()) begin(); }
    start.style.display = started ? 'none' : 'flex';
    if (s !== lastState) { lastState = s; stationX = findStation(s); }

    const p = s.params || {}, t = s.train || {};
    const w = s.w || 1, path = s.path || [];
    const sec = (s.tick ?? 0) / TPS;

    // прогрес: голова, кінець колії, станція (за x уздовж смуги)
    let headX = 0;
    try { headX = wagonPos(s, 0).x / U; } catch { }
    const lastCell = path.length ? path[path.length - 1] : 0;
    const endX = (lastCell % w) + 0.5;
    const span = Math.max(1, stationX + 1);
    set(progTrack.style, 'track|width', `${Math.min(100, endX / span * 100).toFixed(1)}%`);
    set(progEnd.style, 'end|left', `${Math.min(100, endX / span * 100).toFixed(1)}%`);
    set(progHead.style, 'head|left', `${Math.min(100, headX / span * 100).toFixed(1)}%`);
    const ahead = Math.max(0, path.length - 1 - Math.floor((t.pos ?? 0) / U));
    txt(progAhead, 'ahead', `🛤 ${ahead} попереду · до 🏁 ${Math.max(0, Math.round(stationX - headX))}`);

    const cps = (t.speed ?? 0) / 1000 / U * TPS;   // клітинок/с
    txt(timeEl, 'time', `⏱ ${fmtTime(sec)}`);
    txt(speedEl, 'speed', `💨 ${cps.toFixed(2)}`);

    // склад і майстерня
    const sm = p.storeMax ?? 0;
    txt(sWood, 'wood', `🪵 ${t.wood ?? 0}/${sm}`); set(sWood, 'wood|className', (t.wood ?? 0) >= sm ? 'kol-full' : '');
    txt(sIron, 'iron', `⛓ ${t.iron ?? 0}/${sm}`); set(sIron, 'iron|className', (t.iron ?? 0) >= sm ? 'kol-full' : '');
    txt(cRails, 'rails', `🛤 ${t.rails ?? 0}/${p.railsMax ?? 0}`);
    const craftTot = Math.max(1, (p.craftSec ?? 1) * TPS);
    const cT = t.craftT ?? 0;
    set(cFill.style, 'craft|width', cT > 0 ? `${Math.round((1 - cT / craftTot) * 100)}%` : '0%');
    set(cBar.style, 'cbar|opacity', cT > 0 ? '1' : '.35');

    // котел
    const heat = Math.max(0, Math.min(1000, t.heat ?? 0));
    set(hFill.style, 'heat|width', `${heat / 10}%`);
    txt(hPct, 'hpct', `${Math.round(heat / 10)}%`);
    set(boiler, 'boiler|className', `kol-box kol-row${heat > 750 ? ' kol-hot' : ''}`);

    const run = s.status === 'run';
    const fireT = t.fire ?? 0;
    let fireTxt = '';
    if (run && fireT > 0) fireTxt = `🔥 ПОЖЕЖА! Відро води на паротяг · ${Math.max(0, Math.ceil((p.fireSec ?? 0) - fireT / TPS))}`;
    else if (run && heat >= 1000 && p.overheat === 'stop') fireTxt = '♨ Котел перегрівся — паротяг стоїть! Відро води';
    txt(fire, 'fire', fireTxt); set(fire.style, 'fire|display', fireTxt ? 'block' : 'none');

    // тривога: до кінця колії менше ALARM_SEC ходу
    const toEnd = cps > 0 ? (ahead - ((t.pos ?? 0) % U) / U) / cps : Infinity;
    set(alarm.style, 'alarm|display', run && !fireTxt && toEnd < ALARM_SEC ? 'block' : 'none');

    // відлік до рушання
    const left = (p.startSec ?? 0) - sec;
    const counting = run && (t.speed ?? 0) === 0 && left > 0;
    set(count.style, 'count|display', counting && started ? 'block' : 'none');
    if (counting) txt(countN, 'countN', String(Math.ceil(left)));

    // гравці
    const players = s.players || [];
    const local = game?.local ?? players.length;
    while (plEls.length < players.length) {
      const i = plEls.length, d = el('div', 'kol-box kol-pl', pls);
      d.style.borderColor = COLORS[i % 4];
      plEls.push(d);
    }
    for (let i = 0; i < plEls.length; i++) {
      const pl = players[i], d = plEls[i];
      set(d.style, `pl${i}|display`, pl ? 'block' : 'none');
      if (!pl) continue;
      const k = pl.hold?.k ?? 0, nn = pl.hold?.n ?? 0;
      const what = k ? `${HOLD_ICON[k] ?? '?'}${nn > 1 ? ' ×' + nn : ''}` : '✋';
      const name = `${i >= local ? '🤖' : ''}П${i + 1}`;
      set(d, `pl${i}|innerHTML`, `<b style="color:${COLORS[i % 4]}">${name}</b> ${what}`);
      set(d, `pl${i}|title`, HOLD_NAME[k] ?? 'порожні руки');
    }

    // кінець
    const over = s.status === 'won' || s.status === 'lost';
    set(end.style, 'end|display', over ? 'flex' : 'none');
    if (over) {
      const st = s.stats || {};
      const head = s.status === 'won' ? '🏁 Доїхали!' : s.reason === 'fire' ? '🔥 Згоріли' : '💥 Зійшли з колії';
      const maxCps = (st.maxSpeed ?? 0) / 1000 / U * TPS;
      set(endCard, 'endc|innerHTML', `<h1>${head}</h1>
        <div class="kol-sum">
          <span>⏱ Час</span><b>${fmtTime(sec)}</b>
          <span>🛤 Рейок покладено</span><b>${st.railsLaid ?? 0}</b>
          <span>🪓 Ударів</span><b>${st.hits ?? 0}</b>
          <span>💨 Найшвидше</span><b>${maxCps.toFixed(2)} кл/с</b>
        </div>
        <div class="kol-go">R — ${s.status === 'won' ? 'далі' : 'ще раз'}</div>`);
    }
  }

  return {
    update,
    dispose() { removeEventListener('keydown', onKey); style.remove(); root.textContent = ''; root.classList.remove('kol-root'); },
  };
}
