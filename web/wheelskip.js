// 🛞 Скіп кнопкою керма. Граєш в ETS2/ATS, Глечики відкриті в браузері десь збоку — кнопка на кермі (чи на будь-якому
// паді) перемикає трек, як ⏭ на сайті. Яка кнопка — кожен призначає сам у себе, лежить у localStorage цього браузера.
//
// Чому так, а не через HPad: pad.js кермо навмисно не бачить (передачі G29 — це кнопки, вони водили сайт), а без фокуса
// вікна не тисне нічого. Тут навпаки: потрібна саме одна кнопка керма і саме тоді, коли фокус у грі.
//
// Опитуємо самі: подій на кнопки пада браузер не шле. Такт — від Worker'а, а не від setInterval сторінки: вкладку, яку
// не видно, Chrome гальмує до разу на секунду, а за п'ять хвилин — до разу на хвилину, і коротке натискання губилось би.
// Таймери Worker'а так не гальмують. Але свіжі дані пада Chrome дає лише сторінці, яку видно (вікно не згорнуте й не
// закрите грою на весь екран) — про це підказка в панельці.
(() => {
  const KEY = 'wheelSkip';       // { id: назва пристрою від браузера, idx: номер кнопки, name: коротка назва }
  const AT_KEY = 'wheelSkipAt';  // коли востаннє скіпали кермом — спільне для всіх вкладок цього браузера
  const GAP_MS = 4000;           // друге натискання одразу — не другий скіп: трек ще не встиг змінитись
  const TICK_MS = 30;

  let o = null;                  // { $, esc, toast, skip }
  let bind = load();
  let binding = false, open = false;
  let btn = null, pop = null;
  let ticker = null, fallbackTimer = 0;
  const prev = new Map();        // index пада → які кнопки були натиснуті минулого такту
  let seen = '';                 // які пади видно — щоб перемальовувати панельку лише на зміну

  function load() {
    try {
      const v = JSON.parse(localStorage.getItem(KEY) || 'null');
      return v && typeof v.id === 'string' && Number.isInteger(v.idx) ? v : null;
    } catch { return null; }
  }
  function save() {
    try { bind ? localStorage.setItem(KEY, JSON.stringify(bind)) : localStorage.removeItem(KEY); } catch { /* приватне вікно */ }
  }

  const pads = () => [...((navigator.getGamepads && navigator.getGamepads()) || [])].filter(Boolean);
  /// «G29 Driving Force Racing Wheel (Vendor: 046d Product: c24f)» → «G29 Driving Force Racing Wheel».
  const shortName = (id) => String(id || '').replace(/\s*\((?:STANDARD GAMEPAD\s*)?Vendor:.*\)\s*$/i, '').replace(/^[0-9a-f]{4}-[0-9a-f]{4}-/i, '').trim() || 'пад';

  // ---------- такт і натискання ----------
  function tick() {
    const list = pads();
    const sig = list.map((g) => g.index + ':' + g.id).join('|');
    if (sig !== seen) { seen = sig; if (open) paint(); }
    for (const g of list) {
      const now = g.buttons.map((b) => b.pressed);
      const was = prev.get(g.index);
      prev.set(g.index, now);
      if (!was) continue;          // пад щойно з'явився: що вже затиснуте — не натискання
      for (let i = 0; i < now.length; i++) if (now[i] && !was[i]) press(g, i);
    }
  }

  function press(g, i) {
    if (binding) {
      bind = { id: g.id, idx: i, name: shortName(g.id) };
      save();
      binding = false;
      sync();
      paint();
      o.toast(`Готово: ${label(bind)} — скіп`, 'ok');
      return;
    }
    if (bind && g.id === bind.id && i === bind.idx) fire();
  }

  /// Дві відкриті вкладки бачать те саме натискання — скіпає лише одна (замок браузера), і не частіше за GAP_MS.
  function fire() {
    const go = () => {
      let at = 0;
      try { at = +localStorage.getItem(AT_KEY) || 0; } catch { /* нема — то й нема */ }
      if (Date.now() - at < GAP_MS) return;
      try { localStorage.setItem(AT_KEY, String(Date.now())); } catch { /* приватне вікно */ }
      o.skip();
    };
    if (navigator.locks) navigator.locks.request('hl-wheel-skip', { ifAvailable: true }, (lock) => { if (lock) go(); });
    else go();
  }

  /// Опитуємо лише тоді, коли є що ловити: кнопку призначено або чекаємо, яку натиснуть.
  function sync() {
    const need = binding || !!bind;
    if (need && !ticker && !fallbackTimer) {
      try {
        const src = `setInterval(() => postMessage(0), ${TICK_MS});`;
        ticker = new Worker(URL.createObjectURL(new Blob([src], { type: 'text/javascript' })));
        ticker.onmessage = tick;
      } catch {
        ticker = null;
        fallbackTimer = setInterval(tick, TICK_MS);   // без Worker'а — хоча б так, у видимій вкладці працює
      }
    }
    if (!need) {
      if (ticker) { ticker.terminate(); ticker = null; }
      if (fallbackTimer) { clearInterval(fallbackTimer); fallbackTimer = 0; }
      prev.clear();
    }
    if (btn) btn.classList.toggle('on', !!bind);
  }

  // ---------- кнопка в шапці й панелька ----------
  const label = (b) => `${b.name} · кнопка ${b.idx + 1}`;

  function paint() {
    if (!pop) return;
    const e = o.esc;
    const list = pads();
    const mine = bind && list.some((g) => g.id === bind.id);
    const state = binding
      ? '<div class="ws-wait"><span class="spin"></span> Натисни на кермі кнопку, яка буде скіпом… <small>(Esc — відміна)</small></div>'
      : bind
        ? `<div class="ws-now">Скіп: <b>${e(label(bind))}</b></div>`
        : '<div class="ws-now">Кнопку ще не призначено</div>';
    const seenTxt = list.length
      ? (bind && !mine
        ? '<span class="ws-bad">Призначеного керма зараз не видно — натисни на ньому будь-яку кнопку</span>'
        : '<span class="ws-ok">Бачу: ' + list.map((g) => e(shortName(g.id))).join(', ') + '</span>')
      : '<span class="ws-bad">Браузер ще не бачить керма — натисни на ньому будь-яку кнопку, поки ця вкладка перед очима</span>';
    pop.innerHTML = `
      <div class="ws-head">🛞 Скіп кнопкою керма</div>
      ${state}
      <div class="ws-seen">${seenTxt}</div>
      <div class="ws-acts">
        <button type="button" data-ws="bind" class="${bind ? '' : 'primary'}">${binding ? 'Відміна' : bind ? 'Інша кнопка' : 'Призначити'}</button>
        ${bind && !binding ? '<button type="button" data-ws="off" class="ghost">Прибрати</button>' : ''}
      </div>
      <div class="ws-tip">Граєш, а Глечики відкриті в браузері: кнопка перемикає трек для всіх, як ⏭.
        Браузер чує кермо, лише коли вікно з сайтом <b>видно</b>: не згорнуте й не сховане за грою на весь екран.
        Найпростіше — тримати його на другому моніторі або грати у вікні.</div>`;
    pop.querySelector('[data-ws="bind"]').onclick = () => { binding = !binding; sync(); paint(); };
    const off = pop.querySelector('[data-ws="off"]');
    if (off) off.onclick = () => { bind = null; save(); sync(); paint(); };
  }

  function setOpen(v) {
    open = v;
    if (!v && binding) { binding = false; sync(); }
    pop.hidden = !v;
    btn.setAttribute('aria-expanded', String(v));
    if (v) paint();
  }

  function mount() {
    const host = document.getElementById('mini');
    if (!host || !navigator.getGamepads) return;
    const wrap = document.createElement('div');
    wrap.className = 'wswrap';
    wrap.innerHTML = '<button id="wsBtn" class="icon ghost" type="button" title="Скіп кнопкою керма" aria-label="Скіп кнопкою керма" aria-expanded="false">🛞</button>'
      + '<div class="ws-pop" hidden></div>';
    host.appendChild(wrap);
    btn = wrap.querySelector('#wsBtn');
    pop = wrap.querySelector('.ws-pop');
    btn.onclick = (ev) => { ev.stopPropagation(); setOpen(!open); };
    pop.addEventListener('click', (ev) => ev.stopPropagation());
    document.addEventListener('click', () => { if (open) setOpen(false); });
    addEventListener('keydown', (ev) => {
      if (ev.code !== 'Escape' || !open) return;
      if (binding) { binding = false; sync(); paint(); } else setOpen(false);
    });
    // Пад з'явився чи зник — показати це в панельці відразу, навіть якщо кнопку ще не призначено (тоді такту нема).
    addEventListener('gamepadconnected', () => { if (open) paint(); });
    addEventListener('gamepaddisconnected', (ev) => { prev.delete(ev.gamepad.index); if (open) paint(); });
  }

  window.HWheelSkip = {
    init(opts) {
      o = opts;
      mount();
      sync();
    },
  };
})();
