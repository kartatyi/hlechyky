// «Колія» — довідка «❓ Як грати» (K65). Власник: K65. Одна сторінка для новачка: мета, цикл, міст, кооп, біоми,
// покращення, керування (таблицею), поради. Слова теми «Чумацький шлях» — t() (theme.js), де словник їх має, і w() тут.
// Контракт: helpSections() → [{ id, icon, title, html }] (без DOM — node-тест); introSteps() → [рядок HTML × 4] — коротко
//   для картки «Як грати» на час збору (hud.js);
//   createHelp(root, { input, isNet(), onClose(from) }) → { open(from), close(), isOpen(), from, key(e) }
//   from: 'menu' | 'pause' | 'game'. Хто відкриває (hud.js) сам ставить гру на паузу й повертає пад після onClose.
//   Поки відкрито: пад — input.setMenu (✚↑↓ гортати, ◀▶/A — розділи, B/Start — закрити); клавіші — ↑↓/WS гортати,
//   ←→/AD — розділи, PgUp/PgDn/Home/End, Esc/H/Backspace — закрити; дотик — гортається пальцем, ✕ закриває.
// Без DOM на імпорті.
import { t, isChumak, upName } from './theme.js';
import { UPGRADES } from './extras.js';
import { pcol, pmark } from './panel.js';

const w = (koliya, chumak) => (isChumak() ? chumak : koliya);   // слова, яких у словнику теми нема

/** Розділи довідки (рядки HTML — лише наш текст, без введеного користувачем) */
export function helpSections() {
  const loco = t('icon.loco', '🚂'), rails = t('icon.rails', '🛤'), st = t('icon.station', '🏁'), heat = t('icon.heat', '♨');
  const L = w('паротяг', 'воли з мажею'), R = w('рейки', 'гать'), S = w('станції', 'ярмарку');
  const store = w('склад (зелений вагон)', 'віз-комора'), craft = w('майстерня', 'віз-кузня'), water = w('вода', 'криниця');
  const ups = Object.entries(UPGRADES).map(([k, u]) => upName(k, u)).map((u) =>   // K69: слова теми
    `<li>${u.icon} ${u.label}</li>`).join('');
  const P = (i) => `<b style="color:${pcol(i)}">${pmark(i)}П${i + 1}</b>`;
  return [
    { id: 'goal', icon: '🎯', title: 'Мета', html: `<p>${loco} ${w('Паротяг сам повзе', 'Воли самі тягнуть мажу')} до ${S} ${st}.
      Ваша справа — класти ${R} ${rails} перед ${w('ним', 'ними')}, поки ${w('він', 'вони')} не ${w('доїхав', 'дійшли')} до краю.
      ${w('Скінчились рейки — аварія 💥', 'Скінчилась гать — віз загруз 💥')}; ${w('котел перегрівся — пожежа 🔥', 'воли запріли — знесилились 😩')}.
      Щохвилини ${L} ${w('їде', 'йдуть')} трохи швидше.</p>` },
    { id: 'loop', icon: '🔁', title: 'Як усе крутиться', html: `<ol>
      <li>🪓 <b>Сокира → дерево</b> — дрова. ⛏ <b>Кайло → скеля</b> — залізо. Інструмент береш дією, кладеш — дією ж.</li>
      <li>📦 Неси дрова й залізо на <b>${store}</b> — підійди й дія.</li>
      <li>🔨 <b>${craft[0].toUpperCase() + craft.slice(1)}</b> сама робить ${R}: 1 дрово + 1 залізо = 3 ${w('рейки', 'гаті')}.</li>
      <li>${rails} Візьми ${R} з ${w('майстерні', 'кузні')} і клади в продовження колії, впритул до останньої.</li>
      <li>${heat} ${w('Котел гріється', 'Воли пріють')} — 🪣 <b>відро</b>: до ${water === 'вода' ? 'води' : 'криниці'} (дія — набрати), потім дія на ${w('паротяг', 'волів')}.</li>
    </ol><p class="kol-hp-s">Глухий кут? Порожні руки й дія на останню ${w('рейку', 'гать')} — розібрати й повернути.</p>` },
    { id: 'bridge', icon: '🌉', title: 'Річка й міст', html: `<p>${t('tip.bridge', 'Річка: поклади дрова на воду — буде місток')} 🌊 → 🪵 → 🌉, а тоді ${R} по ньому.
      ${t('tip.ford', 'Брід: тут мілина — рейки кладуться без мосту')}.</p>` },
    { id: 'coop', icon: '🤝', title: 'Удвох', html: `<ul>
      <li>🤝 <b>Передача</b> — дія з річчю на партнера, що стоїть перед тобою: річ у його руки.</li>
      <li>🎯 <b>Кидок</b> — дрова/залізо прямо на ${store}, відро — на ${w('паротяг', 'волів')}; партнер ловить, вода — топить.</li>
      <li>📯 <b>Гудок</b> — гукнути напарника; бот-помічник іде до тебе.</li>
      <li>👋👍😂😱 <b>Реакції</b> в мережі — клавіші 1–4, на паді LB/RB/LT/RT.</li>
    </ul><p class="kol-hp-s">Кидок і штовханину вмикають у ⚙ → «Удвох». Розподіліться: один рубає й носить, другий кладе ${R}.</p>` },
    { id: 'biomes', icon: '🗺', title: 'Місцевість', html: `<ul>
      <li>🐸 <b>Болото</b> — ходиш повільно, ${R} класти можна.</li>
      <li>⛸ <b>Лід</b> — ковзаєш, гальмуй заздалегідь. ❄ Сніг — крок повільніший.</li>
      <li>⛰ <b>Яр</b> — ${t('tip.chasm', 'спершу дрова (каркас), потім залізо — і буде міст')} 🌉.</li>
      <li>🌳 гай · 🪨 каміння · 🌾 степ · ⛰ прохід між скелями — різна густота лісу й каменю.</li>
    </ul>` },
    { id: 'ups', icon: '🏪', title: w('Покращення на станціях', 'Запаси в корчмі'), html: `<p>У ♾ нескінченній (і з ⚙ «Станція: проміжна») ${L} ${w('стає', 'стають')} — підійди до таблички й дія, щоб обрати:</p><ul class="kol-hp-ups">${ups}</ul>` },
    { id: 'keys', icon: '🎮', title: 'Керування', html: `<div class="kol-hp-tw"><table class="kol-hp-t">
      <tr><th></th><th>рух</th><th>дія</th><th>кинути</th><th>гудок</th></tr>
      <tr><td>${P(0)}</td><td>WASD</td><td>Space / E</td><td>Q</td><td>F</td></tr>
      <tr><td>${P(1)}</td><td>стрілки</td><td>Enter</td><td>правий Ctrl / Num0</td><td>/</td></tr>
      <tr><td>🎮 пад</td><td>стік / ✚</td><td>A</td><td>B</td><td>Y</td></tr>
      <tr><td>📱 телефон</td><td>палець ліворуч</td><td>✋</td><td>🎯</td><td>📯</td></tr>
    </table></div><p class="kol-hp-s">R / Start — ще раз · Esc / ⏸ — пауза · F1 / \` / Select — ⚙ налаштування · H — ця довідка · другий пад сам стає П2.</p>` },
    { id: 'tips', icon: '💡', title: 'Як не програти', html: `<ul>
      <li>Тримай запас ${R} у руках — ${w('паротяг', 'воли')} не ${w('чекає', 'чекають')}.</li>
      <li>Повне відро 🪣 — завжди поруч із ${w('паротягом', 'волами')}; ${heat} мигає — кидай усе й лий.</li>
      <li>${store[0].toUpperCase() + store.slice(1)} не порожній: і дрова, і залізо, інакше ${craft} стоїть.</li>
      <li>Не тікай далеко — камера тримає всіх, а ${R} потрібні попереду.</li>
      <li>Річку й яр видно заздалегідь — готуй дрова на міст раніше.</li>
      <li>Сокира втонула? За ~10 с з'явиться біля колії.</li>
      <li>${w('Потяг', 'Віз')} заступив дорогу? Іди просто крізь ${w('вагон', 'віз')} — перелізеш верхом, але повільно, тож краще обійти.</li>
    </ul>` },
  ];
}

/** «Як грати» коротко — картка на час збору (hud.js): чотири кроки того самого циклу, що й «Як усе крутиться», без DOM */
export function introSteps() {
  const rails = t('icon.rails', '🛤'), st = t('icon.station', '🏁');
  return [
    `🚂 ${w('Паротяг сам їде до станції', 'Воли самі тягнуть мажу до ярмарку')} ${st} — ${w('рейки', 'гать')} мусять бути перед ${w('ним', 'ними')}, інакше ${w('аварія', 'віз загрузне')} 💥`,
    `🪓 Рубай дерева, ⛏ бий скелі — неси дрова й залізо на ${w('склад (зелений вагон)', 'віз-комору')}`,
    `🔨 ${w('Майстерня сама робить рейки', 'Віз-кузня сама кує гать')} ${rails} — бери й клади впритул до краю ${w('колії', 'гаті')}`,
    `🪣 ${w('Котел гріється', 'Воли пріють')} — набери відром води й вилий ${w('на паротяг', 'на волів')}`,
  ];
}

const CSS = `
.kol-help { position: fixed; inset: 0; z-index: 35; display: none; align-items: center; justify-content: center; background: #0b0d0999;
  pointer-events: auto; padding: 12px; box-sizing: border-box; }
.kol-help.kol-on { display: flex; }
.kol-hp-card { display: flex; flex-direction: column; width: min(100%, 44em); max-height: 100%; background: #171b13f5; border: 1px solid #ffffff2a;
  border-radius: 16px; overflow: hidden; font-weight: 500; font-size: .82em; line-height: 1.4; text-align: left; }
.kol-hp-head { display: flex; align-items: center; gap: .4em; padding: .5em .6em .4em 1em; border-bottom: 1px solid #ffffff1a; }
.kol-hp-head h2 { margin: 0; font-size: 1.25em; flex: 1; }
.kol-hp-x { min-width: 44px; min-height: 44px; font: inherit; font-size: 1.2em; color: #fff; background: #2c3a22; border: 1px solid #ffffff2a;
  border-radius: 10px; cursor: pointer; touch-action: manipulation; }
.kol-hp-nav { display: flex; gap: .3em; padding: .4em .8em; overflow-x: auto; border-bottom: 1px solid #ffffff1a; scrollbar-width: none; }
.kol-hp-nav button { flex: none; min-height: 36px; padding: 0 .6em; font: inherit; color: #f4efe2; background: #1d2418; border: 1px solid #ffffff22;
  border-radius: 999px; cursor: pointer; touch-action: manipulation; white-space: nowrap; }
.kol-hp-nav button.kol-hp-on { background: #4a6a2a; border-color: #9bc25a; }
.kol-hp-body { position: relative; overflow-y: auto; padding: .2em 1.2em 1em; overscroll-behavior: contain; touch-action: pan-y; -webkit-overflow-scrolling: touch; }
.kol-hp-body h3 { margin: .8em 0 .25em; font-size: 1.1em; color: #ffe9a8; }
.kol-hp-body p, .kol-hp-body ul, .kol-hp-body ol { margin: .2em 0; }
.kol-hp-body ul, .kol-hp-body ol { padding-left: 1.3em; }
.kol-hp-body li { margin: .15em 0; }
.kol-hp-s { opacity: .8; font-size: .92em; }
.kol-hp-tw { overflow-x: auto; }
.kol-hp-t { border-collapse: collapse; width: 100%; font-size: .92em; }
.kol-hp-t th, .kol-hp-t td { padding: .25em .45em; border-bottom: 1px solid #ffffff14; text-align: center; white-space: nowrap; }
.kol-hp-t th { opacity: .7; font-weight: 600; }
.kol-hp-t td:first-child { text-align: left; }
.kol-hp-foot { padding: .35em 1em; border-top: 1px solid #ffffff1a; font-size: .85em; opacity: .7; text-align: center; }
@media (max-width: 640px) { .kol-help { padding: 8px; } .kol-hp-card { font-size: .78em; } .kol-hp-head h2 { font-size: 1.1em; } }
/* K69: телефон — текст не дрібніший за 12 px (було 9–11), кнопки-розділи ≥ 40 px, ландшафт — тонша шапка, тіло гортається */
.kol-hp-card { font-size: max(13px, .82em); }
.kol-hp-s, .kol-hp-t { font-size: max(12px, .92em); }
.kol-hp-foot { font-size: max(12px, .85em); }
.kol-hp-nav button { min-height: 40px; }
.kol-hp-body { min-height: 0; flex: 1 1 auto; }
.kol-hp-head, .kol-hp-nav, .kol-hp-foot { flex: none; }   /* K69: низька картка стискала смугу розділів до 16–28 px */
@media (max-width: 640px) { .kol-hp-card { font-size: 13px; } .kol-hp-body { padding: .2em .9em .8em; } }
@media (max-height: 500px) { .kol-help { padding: 4px 8px; } .kol-hp-head { padding: .2em .5em .2em .9em; } .kol-hp-nav { padding: .25em .6em; }
  .kol-hp-foot { display: none; } }
@media (hover: none) and (pointer: coarse) { .kol-help:not(.kol-haspad *) .kol-hp-foot { display: none; } }
`;

export function createHelp(root, opts = {}) {
  let style = null, box = null, body = null, nav = null, secs = [], chips = [], cur = 0, from = null;
  function build() {
    if (box) return;
    style = document.createElement('style'); style.textContent = CSS; document.head.appendChild(style);
    box = document.createElement('div'); box.className = 'kol-help';
    box.innerHTML = `<div class="kol-hp-card" role="dialog" aria-label="Як грати"><div class="kol-hp-head"><h2></h2>
      <button class="kol-hp-x" type="button" aria-label="Закрити">✕</button></div><div class="kol-hp-nav"></div><div class="kol-hp-body"></div>
      <div class="kol-hp-foot"><span class="kol-kbonly">↑↓ — гортати · ←→ — розділи · Esc / H — закрити</span><span class="kol-padonly">✚ — гортати · ◀▶ / A — розділи · B — закрити</span></div></div>`;
    root.appendChild(box);
    body = box.querySelector('.kol-hp-body'); nav = box.querySelector('.kol-hp-nav');
    box.querySelector('.kol-hp-x').addEventListener('click', (e) => { e.preventDefault(); close(); });
    box.addEventListener('click', (e) => { if (e.target === box) close(); });   // тап повз картку — закрити
    nav.addEventListener('click', (e) => { const b = e.target.closest('[data-i]'); if (b) { e.preventDefault(); b.blur(); go(+b.dataset.i); } });
    body.addEventListener('scroll', () => mark(), { passive: true });
  }
  function fill() {
    const list = helpSections();
    box.querySelector('h2').textContent = `❓ Як грати · ${t('start.title', '🚂 Колія')}`;
    nav.innerHTML = list.map((s, i) => `<button type="button" data-i="${i}">${s.icon} ${s.title}</button>`).join('');
    body.innerHTML = (opts.isNet?.() ? '<p class="kol-hp-s">🌐 Гра в мережі — поки читаєш, світ не стоїть!</p>' : '')
      + list.map((s) => `<section data-id="${s.id}"><h3>${s.icon} ${s.title}</h3>${s.html}</section>`).join('');
    secs = [...body.querySelectorAll('section')]; chips = [...nav.querySelectorAll('button')];
  }
  function mark() {
    const y = body.scrollTop + 8;
    let i = 0;
    secs.forEach((s, j) => { if (s.offsetTop <= y) i = j; });
    if (body.scrollTop + body.clientHeight >= body.scrollHeight - 2) i = secs.length - 1;
    if (i === cur && chips[i]?.classList.contains('kol-hp-on')) return;
    cur = i;
    chips.forEach((c, j) => c.classList.toggle('kol-hp-on', j === i));
    chips[i]?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
  }
  function go(i) {
    const n = secs.length; if (!n) return;
    i = Math.max(0, Math.min(n - 1, i));
    body.scrollTop = secs[i].offsetTop - 4;
    cur = i; chips.forEach((c, j) => c.classList.toggle('kol-hp-on', j === i));
    chips[i]?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
  }
  const scroll = (dy) => { body.scrollTop += dy; };
  function pad(k) {
    if (k === 'up') scroll(-70); else if (k === 'down') scroll(70);
    else if (k === 'left') go(cur - 1); else if (k === 'right') go(cur + 1);
    else if (k === 'a') go(cur + 1 >= secs.length ? 0 : cur + 1);
    else if (k === 'b' || k === 'start') close();
  }
  function open(f = 'game') {
    build(); fill();
    from = f;
    box.classList.add('kol-on');
    body.scrollTop = 0; cur = -1; mark();
    opts.input?.setMenu?.(pad);
  }
  function close() {
    if (!from) return;
    const f = from; from = null;
    box.classList.remove('kol-on');
    opts.input?.setMenu?.(null);   // ковтає затиснуту B/A, щоб не стала дією
    opts.onClose?.(f);
  }
  return {
    open, close,
    isOpen: () => !!from,
    get from() { return from; },
    /** клавіатура, поки відкрито: true — з'їдено (усі клавіші, щоб не йшли в гру/меню під довідкою) */
    key(e) {
      if (!from) return false;
      const k = e.code;
      if (k === 'Escape' || k === 'KeyH' || k === 'Backspace') { if (!e.repeat) close(); }
      else if (k === 'ArrowUp' || k === 'KeyW') scroll(-60);
      else if (k === 'ArrowDown' || k === 'KeyS' || k === 'Space') scroll(60);
      else if (k === 'ArrowLeft' || k === 'KeyA') go(cur - 1);
      else if (k === 'ArrowRight' || k === 'KeyD' || k === 'Enter' || k === 'NumpadEnter') go(cur + 1);
      else if (k === 'PageUp') scroll(-body.clientHeight * 0.85);
      else if (k === 'PageDown') scroll(body.clientHeight * 0.85);
      else if (k === 'Home') go(0); else if (k === 'End') go(secs.length - 1);
      else return true;
      e.preventDefault();
      return true;
    },
    dispose() { style?.remove(); box?.remove(); },
  };
}
