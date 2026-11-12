/*
  🖥 Екран — вкладка адміна в Бібліотеці (#lib/screen), window.HScreen (12.11.2026). Сервер — Screen.cs,
  інструкція для програми екрана — docs/screen.md.

  Адмін міняє яскравість (0–100), колір (0–1023), пресет (червоний/зелений/синій/білий/чорний) і калібровку.
  Кожна зміна — POST /api/screen; програма екрана дізнається одразу (WebSocket чи довге опитування), а інші
  відкриті вкладки адміна — з події хаба `screen` (група адмінів). Повзунок шле зміни на ходу, не частіше
  одного запиту за раз: поки летить попередній, нові злипаються в один.

  Повзунок яскравості — у децибелах, як гучність радіо (app.js posToVol): 1…100 на 40 дБ, тож 1, 2, 5, 10 мають
  стільки ж місця, скільки 25…100. Програма екрана пише температуру (°C) і відповідає на «🔬 Виміряти колір»
  координатами x, y і температурою кольору в K — це тут і показуємо.

  app.js кличе init() на старті, attach(conn) після з'єднання, renderTab(box) — коли відкрили вкладку.
*/
(() => {
  'use strict';

  const PRESETS = [
    ['red', 'Червоний', '#e53935'],
    ['green', 'Зелений', '#43a047'],
    ['blue', 'Синій', '#1e88e5'],
    ['white', 'Білий', '#ffffff'],
    ['black', 'Чорний', '#000000'],
  ];
  const PRESET = Object.fromEntries(PRESETS.map(([id, name, css]) => [id, { name, css }]));
  const BR_CHIPS = [0, 1, 2, 5, 10, 25, 50, 75, 100];
  const DOC = 'https://github.com/kartatyi/hlechyky/blob/main/docs/screen.md';

  // Яскравість: позиція 0…1000 → 0 або 1…100 по децибелах (крок 0,04 дБ — кожне ціле число досяжне).
  const BR_DB = 40;
  const BR_STEPS = 1000;
  const posToBr = (p) => (p <= 0 ? 0 : Math.max(1, Math.round(100 * Math.pow(10, -BR_DB * (1 - p / BR_STEPS) / 20))));
  const brToPos = (b) => (b <= 0 ? 0 : Math.min(BR_STEPS, Math.max(1, Math.round(BR_STEPS * (1 + 20 * Math.log10(b / 100) / BR_DB)))));
  const FIELDS = {
    brightness: { max: 100, toPos: brToPos, toVal: posToBr },
    color: { max: 1023, toPos: (v) => v, toVal: (p) => p },
  };

  let o = null;          // що дає app.js: esc, toast, busy, api, me, isOpen
  let view = null;       // останнє, що сказав сервер: { state, device, temperature, measures, key }
  let box = null;        // відкрита вкладка
  let showKey = false;
  let pend = null;       // ще не відправлене
  let inflight = false;
  let timer = 0;
  let poll = 0;          // поки вкладка відкрита — раз на 15 с перепитуємо, чи екран на зв'язку
  let fresh = 0;         // id виміру, який щойно прийшов — блимнути ним
  let tick = 0;          // щосекунди оновлює «Екран міряє… 0:42», поки висить запит
  const held = new Set();   // повзунки, які зараз тягнуть: подія з іншого пристрою їх не смикає

  const esc = (s) => (o ? o.esc(s) : String(s));
  const open = () => !!box && box.isConnected && !!o && o.isOpen();
  const q = (sel) => (box ? box.querySelector(sel) : null);
  const hm = (t, sec) => (t ? new Date(t).toLocaleTimeString('uk-UA', sec ? { hour: '2-digit', minute: '2-digit', second: '2-digit' } : { hour: '2-digit', minute: '2-digit' }) : '');
  function when(t, sec) {
    if (!t) return '';
    const d = new Date(t);
    return d.toDateString() === new Date().toDateString() ? hm(t, sec)
      : d.toLocaleDateString('uk-UA', { day: '2-digit', month: '2-digit' }) + ' ' + hm(t, sec);
  }
  const num = (v, digits) => Number(v).toLocaleString('uk-UA', { minimumFractionDigits: digits, maximumFractionDigits: digits });
  const showsWhat = (m) => `☀ ${m.brightness} · ${m.preset ? esc(PRESET[m.preset] ? PRESET[m.preset].name : m.preset) : '🎨 ' + m.color}${m.calibration ? ' · 🎯' : ''}`;

  /// Колір точки xy (CIE 1931) для кружечка: у sRGB, найяскравіший канал = 1. Лише щоб око бачило «тепліше/холодніше».
  function xyCss(x, y) {
    if (!(y > 0)) return '#888';
    const X = x / y, Z = (1 - x - y) / y;
    const lin = [3.2406 * X - 1.5372 - 0.4986 * Z, -0.9689 * X + 1.8758 + 0.0415 * Z, 0.0557 * X - 0.2040 + 1.0570 * Z].map((c) => Math.max(0, c));
    const m = Math.max(...lin) || 1;
    const enc = (c) => Math.round(255 * (c <= 0.0031308 ? 12.92 * c : 1.055 * Math.pow(c, 1 / 2.4) - 0.055));
    return `rgb(${lin.map((c) => enc(c / m)).join(', ')})`;
  }

  /// Нове від сервера (відповідь на POST, подія хаба, перепит). Стан — лише якщо не старіший за наш; поки мої зміни
  /// ще летять, чужий стан не беремо: відповідь на мій запит принесе повний свіжий, а проміжні луни моїх же змін
  /// смикали б повзунок назад. «На зв'язку», температуру й виміри беремо завжди.
  function take(v, mine) {
    if (!v || !v.state) return;
    const prev = view;
    const was = prev && prev.measures && prev.measures[0];
    view = Object.assign({}, prev || {}, {
      device: v.device || (prev && prev.device),
      temperature: 'temperature' in v ? v.temperature : prev && prev.temperature,
      measures: v.measures || (prev && prev.measures) || [],
      key: v.key || (prev && prev.key) || null,
    });
    const busy = !mine && (pend || inflight);
    if (!prev || !prev.state || (!busy && v.state.rev >= prev.state.rev)) view.state = v.state;
    const top = view.measures[0];
    if (prev && top && (!was || top.at !== was.at)) fresh = top.at;
    if (open()) paint();
  }

  // =============================================================================================
  // Відправка
  // =============================================================================================

  function push(patch) {
    pend = Object.assign(pend || {}, patch);
    // Свій колір після пресету — пресет знято; пресет після кольору — пресет. Порядок кліків важливіший за злипання.
    if ('color' in patch && !('preset' in patch)) delete pend.preset;
    if ('preset' in patch && patch.preset && !('color' in patch)) delete pend.color;
    // одразу показуємо в себе, не чекаючи сервера
    if (view && view.state) {
      const s = Object.assign({}, view.state, patch);
      if ('color' in patch && !('preset' in patch)) s.preset = null;
      view = Object.assign({}, view, { state: s });
      paint();
    }
    if (!inflight && !timer) timer = setTimeout(flush, 0);
  }

  async function flush() {
    timer = 0;
    if (!pend || inflight) return;
    const body = pend;
    pend = null;
    inflight = true;
    let failed = false;
    try {
      const r = await o.api('POST', '/api/screen', body);
      if (!pend) take(r, true);   // ще щось чекає — його відповідь буде свіжіша, цю пропускаємо
    } catch (e) {
      o.toast('Екран не почув: ' + e.message, 'err');
      failed = true;
    } finally {
      inflight = false;
      if (pend) timer = setTimeout(flush, 80);
      else if (failed) reload();   // показати те, що справді на екрані
    }
  }

  async function reload() {
    try { take(await o.api('GET', '/api/screen')); } catch { /* наступного разу */ }
  }

  // =============================================================================================
  // Малюємо
  // =============================================================================================

  async function renderTab(el) {
    box = el;
    if (!o || o.me.role !== 'admin') { el.innerHTML = '<div class="empty">Це бачить лише адмін</div>'; return; }
    if (!view || !view.key) {
      el.innerHTML = '<div class="empty">Питаю екран…</div>';
      try { view = await o.api('GET', '/api/screen'); } catch (e) { el.innerHTML = `<div class="empty">Ой-йой: ${esc(e.message)}</div>`; return; }
      if (box !== el) return;
    }
    el.innerHTML = skeleton();
    wire();
    paint();
    clearInterval(poll);
    poll = setInterval(() => {
      if (!open()) { clearInterval(poll); poll = 0; return; }
      if (!document.hidden) reload();
    }, 15000);
    reload();
  }

  function skeleton() {
    const chips = BR_CHIPS.map((v) => `<button type="button" class="chip" data-br="${v}">${v}</button>`).join('');
    const presets = PRESETS.map(([id, name, css]) =>
      `<button type="button" class="scr-preset" data-preset="${id}" style="--sw:${css}" aria-pressed="false"><span class="scr-sw"></span>${name}</button>`).join('');
    return `<div class="scr">
      <div class="scr-top">
        <div class="scr-tile" data-el="tile" aria-hidden="true"><span class="scr-tile-num" data-el="tileNum"></span><span class="scr-tile-cal">калібровка</span></div>
        <div class="scr-info">
          <div class="scr-online" data-el="online"></div>
          <div class="muted small" data-el="who"></div>
          <div class="scr-now" data-el="now"></div>
        </div>
        <div class="scr-temp" data-el="tempBox" title="Температура, яку пише програма екрана">
          <span class="scr-temp-k">🌡 Температура</span>
          <span class="scr-temp-v" data-el="temp">—</span>
          <span class="muted small" data-el="tempAt"></span>
        </div>
      </div>

      <section class="scr-sec">
        <h3>☀ Яскравість</h3>
        <div class="scr-row">
          <input type="range" min="0" max="${BR_STEPS}" step="1" data-f="brightness" aria-label="Яскравість, 0–100 (шкала в децибелах)">
          <input type="number" min="0" max="100" step="1" inputmode="numeric" data-n="brightness" aria-label="Яскравість числом">
        </div>
        <div class="scr-chips">${chips}</div>
      </section>

      <section class="scr-sec">
        <h3>🎨 Колір</h3>
        <div class="scr-row">
          <input type="range" min="0" max="1023" step="1" data-f="color" aria-label="Колір, 0–1023">
          <input type="number" min="0" max="1023" step="1" inputmode="numeric" data-n="color" aria-label="Колір числом">
        </div>
        <div class="scr-note muted small" data-el="presetNote"></div>
      </section>

      <section class="scr-sec">
        <h3>Пресети</h3>
        <div class="scr-presets">${presets}</div>
      </section>

      <section class="scr-sec">
        <h3>🎯 Калібровка</h3>
        <button type="button" class="scr-switch" role="switch" data-el="cal" aria-checked="false">
          <span class="scr-knob"></span><span data-el="calText">Вимкнено</span>
        </button>
      </section>

      <section class="scr-sec">
        <h3>🔬 Вимір кольору</h3>
        <div class="scr-mrow">
          <button type="button" class="scr-measure" data-a="measure">🔬 Виміряти колір</button>
          <button type="button" class="ghost" data-a="mcancel" hidden>✕ Скасувати</button>
          <span class="scr-mstate" data-el="mstate"></span>
        </div>
        <div class="muted small scr-mhint">Вимір може тривати довго — на малій яскравості особливо; сайт чекає, скільки треба.</div>
        <div class="scr-mres" data-el="mres"></div>
        <ol class="scr-mlist" data-el="mlist"></ol>
      </section>

      <details class="scr-conn">
        <summary>🔌 Підключення програми екрана</summary>
        <p class="muted small">Програма входить ключем, не акаунтом. Ключ бачить лише адмін; новий ключ — і старий одразу перестає пускати.
          Як слухати зміни, писати температуру й відповідати на вимір — <a href="${DOC}" target="_blank" rel="noopener">інструкція</a>.</p>
        <div class="scr-kv"><span class="scr-k">Ключ</span><code data-el="key"></code>
          <button type="button" class="ghost" data-a="eye">👁</button>
          <button type="button" class="ghost" data-a="copy" data-what="key">📋</button>
          <button type="button" class="ghost" data-a="newkey">🔄 Новий ключ</button></div>
        <div class="scr-kv"><span class="scr-k">WebSocket</span><code data-el="ws"></code>
          <button type="button" class="ghost" data-a="copy" data-what="ws">📋</button></div>
        <div class="scr-kv"><span class="scr-k">Опитування</span><code data-el="poll"></code>
          <button type="button" class="ghost" data-a="copy" data-what="poll">📋</button></div>
        <div class="scr-kv"><span class="scr-k">Звіт (POST)</span><code data-el="post"></code>
          <button type="button" class="ghost" data-a="copy" data-what="post">📋</button></div>
      </details>
    </div>`;
  }

  const urls = (key) => {
    const k = encodeURIComponent(key || '');
    return {
      key: key || '',
      ws: location.origin.replace(/^http/, 'ws') + '/api/screen/ws?key=' + k,
      poll: location.origin + '/api/screen/device?key=' + k + '&since=<rev>',
      post: location.origin + '/api/screen/device?key=' + k,
    };
  };
  const mask = (s) => (showKey ? s : s.replace(/key=[^&]*/, 'key=••••••'));

  function paint() {
    if (!open() || !view || !view.state) return;
    const s = view.state;
    const d = view.device || {};
    const set = (sel, v) => { const el = q(sel); if (el && document.activeElement !== el && !held.has(el.dataset.f || el.dataset.n)) el.value = v; };
    set('[data-f="brightness"]', brToPos(s.brightness));
    set('[data-n="brightness"]', s.brightness);
    set('[data-f="color"]', s.color);
    set('[data-n="color"]', s.color);
    q('[data-f="brightness"]').setAttribute('aria-valuetext', String(s.brightness));
    box.querySelectorAll('[data-br]').forEach((b) => b.classList.toggle('on', +b.dataset.br === s.brightness));
    box.querySelectorAll('[data-preset]').forEach((b) => {
      const on = b.dataset.preset === s.preset;
      b.classList.toggle('on', on);
      b.setAttribute('aria-pressed', on ? 'true' : 'false');
    });
    // підказка є завжди (інакше з пресетом усе нижче стрибало б на рядок)
    q('[data-el="presetNote"]').textContent = s.preset
      ? `Зараз пресет «${PRESET[s.preset].name}» — посунь повзунок, і екран покаже цей колір.`
      : 'Число 0–1023 — як його показати, вирішує програма екрана.';
    q('[data-f="color"]').closest('.scr-row').classList.toggle('dim', !!s.preset);

    const cal = q('[data-el="cal"]');
    cal.setAttribute('aria-checked', s.calibration ? 'true' : 'false');
    cal.classList.toggle('on', !!s.calibration);
    q('[data-el="calText"]').textContent = s.calibration ? 'Увімкнено' : 'Вимкнено';

    // «плитка» — як приблизно виглядає екран: пресет кольором, свій колір — числом; яскравість — світлом
    const tile = q('[data-el="tile"]');
    tile.style.setProperty('--sw', s.preset ? PRESET[s.preset].css : '#2b4c3c');
    tile.style.setProperty('--lit', String(0.12 + 0.88 * s.brightness / 100));
    tile.classList.toggle('preset', !!s.preset);
    tile.classList.toggle('cal', !!s.calibration);
    q('[data-el="tileNum"]').textContent = s.preset ? '' : s.color;
    q('[data-el="now"]').innerHTML = `☀ <b>${s.brightness}</b> · 🎨 <b>${s.preset ? esc(PRESET[s.preset].name) : s.color}</b>`
      + (s.calibration ? ' · 🎯 <b>калібровка</b>' : '');

    const on = q('[data-el="online"]');
    on.classList.toggle('ok', !!d.online);
    on.textContent = d.online ? '🟢 Екран на зв\'язку' : '⚪ Екрана не чути' + (d.seenAt ? ' — востаннє ' + when(d.seenAt) : ' — ще жодного разу');
    q('[data-el="who"]').textContent = s.updatedAt ? `зміна №${s.rev} · ${s.by || ''} · ${when(s.updatedAt)}` : 'ще нічого не міняли';

    paintTemp();
    paintMeasure(s);

    const u = urls(view.key);
    q('[data-el="key"]').textContent = showKey ? u.key : '••••••••••••';
    q('[data-el="ws"]').textContent = mask(u.ws);
    q('[data-el="poll"]').textContent = mask(u.poll);
    q('[data-el="post"]').textContent = mask(u.post);
    q('[data-a="eye"]').textContent = showKey ? '🙈' : '👁';
  }

  function paintTemp() {
    const t = view.temperature;
    const boxT = q('[data-el="tempBox"]');
    // стара (понад 2 хв) — сіра: програма замовкла, показуємо останнє відоме
    boxT.classList.toggle('stale', !t || Date.now() - new Date(t.at).getTime() > 120000);
    q('[data-el="temp"]').textContent = t ? num(t.value, 1) + ' °C' : '—';
    q('[data-el="tempAt"]').textContent = t ? 'о ' + when(t.at, true) : 'програма ще не писала';
  }

  /// «0:42», «12:05» — скільки минуло від <paramref name="from"/> до <paramref name="to"/> (типово — до зараз).
  function span(from, to) {
    const s = Math.max(0, Math.floor(((to ? new Date(to).getTime() : Date.now()) - new Date(from).getTime()) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  /// Рядок «📏 Екран міряє… 0:42» — щосекунди, поки висить запит (вимір буває довгим).
  function paintWait() {
    const st = q('[data-el="mstate"]');
    if (!st || !view || !view.state) return;
    const ask = view.state.measure;
    const online = view.device && view.device.online;
    st.classList.toggle('wait', !!ask);
    st.textContent = !ask ? ''
      : (online ? `📏 Екран міряє… ${span(ask.at)}` : `⏳ Екрана не чути — виміряє, щойно підключиться · ${span(ask.at)}`) + ` · запит №${ask.id}`;
  }

  function paintMeasure(s) {
    const ask = s.measure;
    const btn = q('[data-a="measure"]');
    if (!btn.classList.contains('busy')) btn.textContent = ask ? '🔬 Виміряти заново' : '🔬 Виміряти колір';
    q('[data-a="mcancel"]').hidden = !ask;
    paintWait();
    if (ask && !tick) {
      tick = setInterval(() => {
        if (!open() || !view || !view.state || !view.state.measure) { clearInterval(tick); tick = 0; return; }
        paintWait();
      }, 1000);
    }
    const list = view.measures || [];
    const top = list[0];
    const res = q('[data-el="mres"]');
    if (!top) {
      res.innerHTML = '<span class="muted small">Ще не міряли. Натисни — програма екрана виміряє й пришле x, y і температуру кольору.</span>';
    } else {
      res.innerHTML = `<span class="scr-mdot" style="background:${xyCss(top.x, top.y)}"></span>`
        + `<span class="scr-mval"><b>x</b> ${num(top.x, 4)}</span><span class="scr-mval"><b>y</b> ${num(top.y, 4)}</span>`
        + `<span class="scr-mval scr-mk">${Math.round(top.k)} K</span>`
        + `<span class="muted small scr-mwhen">${top.id ? '№' + top.id + ' · ' : ''}${when(top.at, true)}`
        + `${top.askedAt ? ' · міряло ' + span(top.askedAt, top.at) : ''} · ${showsWhat(top)}</span>`
        + (top.changed ? '<span class="scr-mwarn">⚠ поки міряло, налаштування екрана міняли — вимір про те, що було на старті</span>' : '');
      if (fresh && fresh === top.at) {
        fresh = 0;
        res.classList.remove('flash');
        void res.offsetWidth;
        res.classList.add('flash');
      }
    }
    q('[data-el="mlist"]').innerHTML = list.slice(1, 10).map((m) =>
      `<li><span class="scr-mdot sm" style="background:${xyCss(m.x, m.y)}"></span>`
      + `<span>${num(m.x, 4)}, ${num(m.y, 4)}</span><b>${Math.round(m.k)} K</b>`
      + `<span class="muted">${when(m.at, true)} · ${showsWhat(m)}${m.changed ? ' · ⚠ міняли під час виміру' : ''}</span></li>`).join('');
  }

  function wire() {
    for (const [f, spec] of Object.entries(FIELDS)) {
      const r = q(`[data-f="${f}"]`);
      const n = q(`[data-n="${f}"]`);
      r.addEventListener('pointerdown', () => held.add(f));
      const release = () => { held.delete(f); };
      r.addEventListener('pointerup', release);
      r.addEventListener('pointercancel', release);
      r.addEventListener('blur', release);
      r.addEventListener('input', () => {
        const v = spec.toVal(+r.value);
        n.value = v;
        // по децибелах кілька позицій дають те саме ціле — шлемо лише справжню зміну
        if (!view || !view.state || view.state[f] !== v) push({ [f]: v });
      });
      r.addEventListener('change', release);
      n.addEventListener('change', () => {
        const v = Math.max(0, Math.min(spec.max, Math.round(+n.value || 0)));
        n.value = v;
        r.value = spec.toPos(v);
        push({ [f]: v });
      });
      n.addEventListener('keydown', (e) => { if (e.key === 'Enter') n.blur(); });
    }
    box.querySelectorAll('[data-br]').forEach((b) => b.addEventListener('click', () => push({ brightness: +b.dataset.br })));
    box.querySelectorAll('[data-preset]').forEach((b) => b.addEventListener('click', () => push({ preset: b.dataset.preset })));
    q('[data-el="cal"]').addEventListener('click', () => push({ calibration: !(view && view.state && view.state.calibration) }));
    const mb = q('[data-a="measure"]');
    mb.addEventListener('click', () => {
      const ask = view && view.state && view.state.measure;
      // довгий вимір легко перебити випадковим кліком — тоді все почнеться спочатку
      if (ask && !confirm(`Екран ще міряє (запит №${ask.id}, ${span(ask.at)}). Почати вимір заново?`)) return;
      o.busy(mb, 'питаю…', async () => {
        try { take(await o.api('POST', '/api/screen/measure')); } catch (e) { o.toast(e.message, 'err'); }
      }).then(() => paint());
    });
    const mc = q('[data-a="mcancel"]');
    mc.addEventListener('click', () => o.busy(mc, '…', async () => {
      try { take(await o.api('POST', '/api/screen/measure/cancel')); } catch (e) { o.toast(e.message, 'err'); }
    }).then(() => paint()));
    q('[data-a="eye"]').addEventListener('click', () => { showKey = !showKey; paint(); });
    box.querySelectorAll('[data-a="copy"]').forEach((b) => b.addEventListener('click', async () => {
      const text = urls(view && view.key)[b.dataset.what];
      try { await navigator.clipboard.writeText(text); o.toast('Скопійовано'); } catch { o.toast('Не вдалося скопіювати', 'err'); }
    }));
    const nk = q('[data-a="newkey"]');
    nk.addEventListener('click', () => {
      if (!confirm('Новий ключ екрана? Програма зі старим одразу відпаде — доведеться вписати новий.')) return;
      o.busy(nk, '…', async () => {
        try { take(await o.api('POST', '/api/screen/key')); showKey = true; paint(); o.toast('Новий ключ готовий'); } catch (e) { o.toast(e.message, 'err'); }
      });
    });
  }

  window.HScreen = {
    init(opts) { o = opts; },
    attach(conn) { conn.on('screen', take); },
    renderTab,
  };
})();
