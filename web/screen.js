/*
  🖥 Екран — вкладка адміна в Бібліотеці (#lib/screen), window.HScreen (12.11.2026). Сервер — Screen.cs,
  інструкція для програми екрана — docs/screen.md.

  Адмін міняє яскравість (0–100), колір (0–1023), пресет (червоний/зелений/синій/білий/чорний) і калібровку.
  Кожна зміна — POST /api/screen; програма екрана дізнається одразу (WebSocket чи довге опитування), а інші
  відкриті вкладки адміна — з події хаба `screen` (група адмінів). Повзунок шле зміни на ходу, не частіше
  одного запиту за раз: поки летить попередній, нові злипаються в один.

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
  const DOC = 'https://github.com/kartatyi/hlechyky/blob/main/docs/screen.md';

  let o = null;          // що дає app.js: esc, toast, busy, api, me, isOpen
  let view = null;       // останнє, що сказав сервер: { state, device, key }
  let box = null;        // відкрита вкладка
  let showKey = false;
  let pend = null;       // ще не відправлене
  let inflight = false;
  let timer = 0;
  let poll = 0;          // поки вкладка відкрита — раз на 15 с перепитуємо, чи екран на зв'язку
  const held = new Set();   // повзунки, які зараз тягнуть: подія з іншого пристрою їх не смикає

  const esc = (s) => (o ? o.esc(s) : String(s));
  const open = () => !!box && box.isConnected && !!o && o.isOpen();
  const q = (sel) => (box ? box.querySelector(sel) : null);
  const hm = (t) => (t ? new Date(t).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' }) : '');
  function when(t) {
    if (!t) return '';
    const d = new Date(t);
    return d.toDateString() === new Date().toDateString() ? hm(t)
      : d.toLocaleDateString('uk-UA', { day: '2-digit', month: '2-digit' }) + ' ' + hm(t);
  }

  /// Нове від сервера (відповідь на POST, подія хаба, перепит) — лише якщо не старіше за те, що вже маємо.
  /// Поки мої зміни ще летять, чужі події беремо лише як «на зв'язку»: відповідь на мій запит принесе повний свіжий
  /// стан, а проміжні луни моїх же змін смикали б повзунок назад.
  function take(v, mine) {
    if (!v || !v.state) return;
    if (!mine && (pend || inflight) && view) {
      view.device = v.device || view.device;
    } else if (view && view.state && v.state.rev < view.state.rev) {
      view.device = v.device || view.device;   // стан старий, але «на зв'язку» — свіже
    } else {
      view = Object.assign({}, view || {}, v, { key: v.key || (view && view.key) || null });
    }
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
    const chips = [0, 25, 50, 75, 100].map((v) => `<button type="button" class="chip" data-br="${v}">${v}</button>`).join('');
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
      </div>

      <section class="scr-sec">
        <h3>☀ Яскравість</h3>
        <div class="scr-row">
          <input type="range" min="0" max="100" step="1" data-f="brightness" aria-label="Яскравість, 0–100">
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

      <details class="scr-conn">
        <summary>🔌 Підключення програми екрана</summary>
        <p class="muted small">Програма входить ключем, не акаунтом. Ключ бачить лише адмін; новий ключ — і старий одразу перестає пускати.
          Як слухати зміни — <a href="${DOC}" target="_blank" rel="noopener">інструкція</a>.</p>
        <div class="scr-kv"><span class="scr-k">Ключ</span><code data-el="key"></code>
          <button type="button" class="ghost" data-a="eye">👁</button>
          <button type="button" class="ghost" data-a="copy" data-what="key">📋</button>
          <button type="button" class="ghost" data-a="newkey">🔄 Новий ключ</button></div>
        <div class="scr-kv"><span class="scr-k">WebSocket</span><code data-el="ws"></code>
          <button type="button" class="ghost" data-a="copy" data-what="ws">📋</button></div>
        <div class="scr-kv"><span class="scr-k">Опитування</span><code data-el="poll"></code>
          <button type="button" class="ghost" data-a="copy" data-what="poll">📋</button></div>
      </details>
    </div>`;
  }

  const urls = (key) => {
    const k = encodeURIComponent(key || '');
    return {
      key: key || '',
      ws: location.origin.replace(/^http/, 'ws') + '/api/screen/ws?key=' + k,
      poll: location.origin + '/api/screen/device?key=' + k + '&since=<rev>',
    };
  };
  const mask = (s) => (showKey ? s : s.replace(/key=[^&]*/, 'key=••••••'));

  function paint() {
    if (!open() || !view || !view.state) return;
    const s = view.state;
    const d = view.device || {};
    const set = (sel, v) => { const el = q(sel); if (el && document.activeElement !== el && !held.has(el.dataset.f || el.dataset.n)) el.value = v; };
    set('[data-f="brightness"]', s.brightness);
    set('[data-n="brightness"]', s.brightness);
    set('[data-f="color"]', s.color);
    set('[data-n="color"]', s.color);
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

    const u = urls(view.key);
    q('[data-el="key"]').textContent = showKey ? u.key : '••••••••••••';
    q('[data-el="ws"]').textContent = mask(u.ws);
    q('[data-el="poll"]').textContent = mask(u.poll);
    q('[data-a="eye"]').textContent = showKey ? '🙈' : '👁';
  }

  function wire() {
    for (const f of ['brightness', 'color']) {
      const r = q(`[data-f="${f}"]`);
      const n = q(`[data-n="${f}"]`);
      const max = f === 'color' ? 1023 : 100;
      r.addEventListener('pointerdown', () => held.add(f));
      const release = () => { held.delete(f); };
      r.addEventListener('pointerup', release);
      r.addEventListener('pointercancel', release);
      r.addEventListener('blur', release);
      r.addEventListener('input', () => { n.value = r.value; push({ [f]: +r.value }); });
      r.addEventListener('change', release);
      n.addEventListener('change', () => {
        const v = Math.max(0, Math.min(max, Math.round(+n.value || 0)));
        n.value = v;
        r.value = v;
        push({ [f]: v });
      });
      n.addEventListener('keydown', (e) => { if (e.key === 'Enter') n.blur(); });
    }
    box.querySelectorAll('[data-br]').forEach((b) => b.addEventListener('click', () => push({ brightness: +b.dataset.br })));
    box.querySelectorAll('[data-preset]').forEach((b) => b.addEventListener('click', () => push({ preset: b.dataset.preset })));
    q('[data-el="cal"]').addEventListener('click', () => push({ calibration: !(view && view.state && view.state.calibration) }));
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
