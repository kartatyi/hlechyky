/*
  Клавоперегони (typerace) і Клавоперегони: тренування (typerace-solo) — один модуль на дві гри.
  Spec: docs/games/specs/typerace.md. Сервер: Impl/Typerace.cs, TyperaceSolo.cs, TyperaceRace.cs, TyperaceJudge.cs.

  Дріт:
    вид  { phase: lobby|pick|ready|go|done, len, text, src, opts, readyAt, goAt, endsAt, goIn, endsIn, tail,
           racers: [{ seat, nick, gone, c, s, wrong, fin, place, cpm, acc, flag }], result, me?, noTexts? }
    кадр { t, p: [c0, s0, c1, s1, …] }  (s: 0 друкує, 1 висить червоний, 2 фініш, 3 пішов; порожнє місце — -1, -1)
    ввід Input('pos', { c, e }) ≤ 5/с; Act('finish', { k, d }); соло — Act('go', { length, source }), Act('stop', {}).

  Свій трактор і свій текст рахуються тут, миттєво (сервер свого не виправляє), чужі — плавно за кадрами.
  Офіційний час і зарахованість — лише сервер; журнал натискань (k/d) — для судді.

  Розділи: 1) ядро (рівність знаків, журнал, звання — дзеркало C#), 2) стан і ввід, 3) траса на канвасі,
  4) текст, числа, підсумок, 5) звук, 6) модуль і два register.
*/
(() => {
  'use strict';

  // =============================================================================================
  // 1. Ядро — те саме, що TyperaceText.Same, TyperaceJudge.Encode і TyperaceLines.Title у C#. Міняти разом.
  // =============================================================================================

  /** Поля payload'ів — рівно те, що читає сервер (тест The_server_accepts_exactly_what_the_module_sends). */
  const WIRE = { pos: ['c', 'e'], finish: ['k', 'd'], go: ['length', 'source'], stop: [] };
  const WIRE_POS = 'pos', WIRE_FINISH = 'finish', WIRE_GO = 'go', WIRE_STOP = 'stop';

  const POS_EVERY_MS = 200;
  const MAX_EVENTS = 1600, STEP_MS = 4, MAX_STEP = 4095;
  const ALPHA = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';

  /** Клас рівності знака: 1 апострофи, 2 тире/дефіси, 3 лапки, 4 пробіл і кінець рядка; 0 — лише сам собі. */
  function cls(ch) {
    switch (ch) {
      case '’': case '\'': case 'ʼ': case '‘': case '`': case '´': return 1;
      case '—': case '–': case '‒': case '-': case '―': case '‐': case '‑': return 2;
      case '«': case '»': case '"': case '„': case '“': case '”': return 3;
      case '\n': case ' ': return 4;
      default: return 0;
    }
  }
  const same = (exp, typed) => exp === typed || (cls(exp) !== 0 && cls(exp) === cls(typed));

  /** Дельта в мілісекундах → два знаки base64url кроками по 4 мс (стеля 16 380 мс). */
  function enc(ms) {
    const v = Math.min(MAX_STEP, Math.max(0, Math.floor(ms / STEP_MS + 0.5)));
    return ALPHA[v >> 6] + ALPHA[v & 63];
  }

  const TITLES = [[120, 'Равлик', '🐌'], [200, 'Пішохід', '🚶'], [280, 'Велосипед', '🚲'], [360, 'Трактор', '🚜'],
    [450, 'Мотоцикл', '🏍'], [Infinity, 'Ракета', '🚀']];
  const titleOf = (cpm) => TITLES.find((t) => cpm < t[0]);

  /** Фізичні клавіші QWERTY → те, що на тому ж місці в ЙЦУКЕН: «ghbdsn» замість «привіт» — це розкладка. */
  const LAT2UA = {};
  'qй wц eу rк tе yн uг iш oщ pз [х ]ї aф sі dв fа gп hр jо kл lд ;ж \'є zя xч cс vм bи nт mь ,б .ю'.split(' ')
    .forEach((p) => { LAT2UA[p[0]] = p[1]; LAT2UA[p[0].toUpperCase()] = p[1].toUpperCase(); });
  const isLatin = (ch) => /[A-Za-z]/.test(ch);

  /** Для звірки з C# (docs/games/dev/typerace-parity.js): сценарій [[подія, мс], …] → { k, d } тим самим кодувальником. */
  window.TyperaceCore = {
    same, enc, titleOf, WIRE,
    log(scenario) { return { k: scenario.map((e) => e[0]).join(''), d: scenario.map((e) => enc(e[1])).join('') }; },
  };

  // =============================================================================================
  // 2. Стан, ввід, журнал, мережа
  // =============================================================================================

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true"><rect x="1" y="5" width="14" height="9" rx="2" fill="none" stroke="var(--accent)" stroke-width="1.4"/>'
    + '<rect x="3" y="7" width="2" height="2" fill="var(--accent)"/><rect x="6" y="7" width="2" height="2" fill="var(--accent)"/><rect x="9" y="7" width="2" height="2" fill="var(--accent)"/>'
    + '<rect x="4.5" y="10.5" width="7" height="1.6" fill="var(--ok)"/><path d="M10.5 1h4.5v3.5h-4.5z" fill="var(--text)"/>'
    + '<path d="M10.5 1h1.5v1.75h-1.5zM13.5 1h1.5v1.75h-1.5zM12 2.75h1.5V4.5H12z" fill="var(--clay)"/></svg>';

  const SEAT_COLORS = ['#f4c542', '#7bd389', '#c5763a', '#6fb3e8', '#e88ac0', '#b48ef0', '#4fd1c5', '#ff8a65', '#c6e377', '#9db3a5'];
  const LENGTHS = [['short', 'Коротко', '~150'], ['medium', 'Середньо', '~300'], ['long', 'Довго', '~600']];
  const SOURCES = [['all', 'Усе'], ['classic', 'Класика'], ['proverbs', 'Прислів’я'], ['twisters', 'Скоромовки']];
  const REASON = {
    'bad-log': 'журнал не читається', mismatch: 'журнал не сходиться з текстом', clock: 'годинник не сходиться',
    fast: 'швидше за людину', script: 'натиски не з клавіатури', metronome: 'ритм метронома', burst: 'черга натисків',
  };
  /** Проміжок у полі вводу: поле ніколи не порожнє, тож Backspace на початку слова теж доходить подією input. */
  const SENT = ' ';
  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const now = () => Date.now();

  function state(root) {
    if (!root._tr) {
      root._tr = {
        ctx: null, key: '', round: -1, phase: '', text: '', len: 0,
        // свій заїзд
        c: 0, red: null, wrong: 0, correct: 0, buf: '', k: '', d: '', lastAt: 0, events: 0,
        localGo: 0, finAt: 0, finished: false, finSent: false, latinRun: 0, prev: null,
        // мережа
        sent: { c: -1, e: -1 }, posAt: 0, posTimer: 0,
        // годинник: зсув сервера відносно Date.now і локальні мітки відліку
        skew: 0, readyLocal: 0, goShownAt: 0,
        // малювання
        raf: 0, lastT: 0, stopAt: 0, cv: null, bg: null, bgKey: '', lanes: 0, laneH: 0, W: 0, H: 0, pal: null,
        x: new Float64Array(10), tx: new Float64Array(10), s: new Int8Array(10).fill(-1),
        dustAt: new Float64Array(10), mineX: 0, trackSig: '', nickW: new Float64Array(10), nickOf: new Array(10).fill(null), nickTxt: new Array(10).fill(''),
        parts: new Float32Array(160 * 7), partN: 0,
        perf: { frames: 0, ms: 0, max: 0 },
        // DOM
        ws: -1, we: -1, wordSig: '', inputTop: -1, statsAt: 0, statsSig: '', liveSig: '', liveAt: 0, boardSig: '', pickSig: '',
        hintTimer: 0, shakeTimer: 0, showPick: false, pickLen: null, pickSrc: null,
        sound: soundOn(), ac: null,
      };
    }
    return root._tr;
  }

  const view = (st) => (st.ctx && st.ctx.view) || {};
  const racers = (st) => view(st).racers || [];
  const meRacer = (st) => { const s = st.ctx && st.ctx.seat; return s == null ? null : racers(st).find((r) => r.seat === s) || null; };
  const solo = (st) => !!(st.ctx && st.ctx.room && st.ctx.room.maxPlayers === 1);
  /** Друкувати можна: сиджу, заїзд іде, я на старті й не встав, ще не на фініші. */
  function canType(st) {
    const r = meRacer(st);
    return !!(st.ctx && st.ctx.mine && st.phase === 'go' && r && !r.gone && !st.finished && st.localGo > 0);
  }

  const storeKey = (st) => 'typerace:' + (st.ctx && st.ctx.room ? st.ctx.room.id + ':' + st.ctx.room.round : '');
  function save(st) {
    try {
      sessionStorage.setItem(storeKey(st), JSON.stringify({ buf: st.buf, k: st.k, d: st.d, lastAt: st.lastAt, wrong: st.wrong,
        correct: st.correct, events: st.events, localGo: st.localGo, text: st.text.length }));
    } catch { /* приватне вікно — без відновлення, та й годі */ }
  }
  function restore(st) {
    try {
      const raw = sessionStorage.getItem(storeKey(st));
      if (!raw) return false;
      const o = JSON.parse(raw);
      if (typeof o.buf !== 'string' || !st.text.startsWith(o.buf) || typeof o.k !== 'string' || typeof o.d !== 'string'
        || o.text !== st.text.length) return false;
      st.buf = o.buf; st.c = o.buf.length; st.k = o.k; st.d = o.d; st.events = o.events | 0;
      st.lastAt = +o.lastAt || now(); st.wrong = o.wrong | 0; st.correct = o.correct | 0; st.localGo = +o.localGo || st.lastAt;
      st.red = null;
      return true;
    } catch { return false; }
  }
  function forget(key) { try { sessionStorage.removeItem(key); } catch { /* нема то й нема */ } }

  /** Подія в журнал: вид натиску (велика літера — не людина) і дельта від попереднього. */
  function journal(st, kind, human) {
    if (st.events >= MAX_EVENTS) return;
    const t = now();
    st.k += human ? kind : kind.toUpperCase();
    st.d += enc(t - st.lastAt);
    st.lastAt = t;
    st.events++;
  }

  function isHuman(st, ev) {
    const ui = st.ctx && st.ctx.ui;
    return !!((ui && ui.human ? ui.human(ev) : ev && ev.isTrusted) || (window.HPad && window.HPad.on));
  }

  /** Натиснуто знак ch. */
  function key(root, st, ch, ev) {
    if (!canType(st)) return;
    const human = isHuman(st, ev);
    unlockSound(st, human);
    if (st.red != null) {
      journal(st, 's', human);
      shake(root, st);
      save(st);
      return;
    }
    const exp = st.text[st.c];
    if (same(exp, ch)) {
      st.buf += exp;              // зберігаємо очікуваний знак, а не набраний варіант («'» стає «’»)
      st.c++;
      st.correct++;
      st.latinRun = 0;
      journal(st, 'c', human);
      paintWord(root, st);
      if (st.c >= st.len) { finish(root, st); return; }
    } else {
      st.red = ch;
      st.wrong++;
      journal(st, 'x', human);
      layoutHint(root, st, ch, exp);
      paintWord(root, st);
      shake(root, st);
      beep(st, 'err');
      smoke(st, st.ctx.seat);
    }
    schedulePos(st);
    save(st);
  }

  function backspace(root, st, ev) {
    if (!canType(st)) return;
    const human = isHuman(st, ev);
    if (st.red != null) {
      st.red = null;
      journal(st, 'b', human);
    } else if (st.c > 0) {
      st.c--;
      st.buf = st.buf.slice(0, -1);
      journal(st, 'b', human);
    } else return;
    paintWord(root, st);
    schedulePos(st);
    save(st);
  }

  /** Розкладка: латинська літера на місці нашої — одразу; незрозуміла латиниця — після двох поспіль. */
  function layoutHint(root, st, ch, exp) {
    if (!isLatin(ch)) { st.latinRun = 0; return; }
    st.latinRun++;
    if (LAT2UA[ch] === exp || st.latinRun >= 2) hint(root, st, '⌨ Розкладка! Перемкни на українську', 2500, 'lay');
  }

  function schedulePos(st) {
    if (st.posTimer) return;
    const wait = Math.max(0, POS_EVERY_MS - (now() - st.posAt));
    st.posTimer = setTimeout(() => { st.posTimer = 0; sendPos(st); }, wait);
  }

  function sendPos(st) {
    if (!st.ctx || !st.ctx.mine || st.phase !== 'go' || st.finSent) return;
    const e = st.red != null ? 1 : 0;
    if (st.sent.c === st.c && st.sent.e === e) return;
    st.sent = { c: st.c, e };
    st.posAt = now();
    const payload = {};
    payload[WIRE.pos[0]] = st.c;
    payload[WIRE.pos[1]] = e;
    st.ctx.input(WIRE_POS, payload);
  }

  function finish(root, st) {
    st.finished = true;
    st.finAt = now();
    if (st.posTimer) { clearTimeout(st.posTimer); st.posTimer = 0; }
    paintWord(root, st);
    confetti(st);
    beep(st, 'fin');
    save(st);
    const input = root.querySelector('.tr-in');
    if (input && document.activeElement === input) input.blur();
    sendFinish(st, 0);
  }

  function sendFinish(st, attempt) {
    if (!st.ctx) return;
    st.finSent = true;
    const payload = {};
    payload[WIRE.finish[0]] = st.k;
    payload[WIRE.finish[1]] = st.d;
    const ctx = st.ctx;
    Promise.resolve(ctx.act(WIRE_FINISH, payload)).catch(() => {
      // мережа кліпнула — ще раз через секунду, до трьох разів; час однаково рахує сервер від миті, коли дійде
      if (attempt < 3 && st.ctx === ctx) setTimeout(() => sendFinish(st, attempt + 1), 1000);
    });
  }

  /** Поле тримає лише проміжок і хвіст поточного слова — так IME Android бачить звичайне слово. */
  function tail(st) {
    let i = st.buf.length;
    while (i > 0 && st.buf[i - 1] !== ' ' && st.buf[i - 1] !== '\n' && st.buf.length - i < 30) i--;
    return SENT + st.buf.slice(i);
  }
  function syncInput(input, st) {
    const want = tail(st);
    if (input.value !== want) input.value = want;
    st.prev = want;
    try { input.setSelectionRange(want.length, want.length); } catch { /* буває */ }
  }

  function onInput(root, st, input, ev) {
    const v = input.value, prev = st.prev == null ? tail(st) : st.prev;
    if (v === prev) return;
    if (!canType(st)) { syncInput(input, st); return; }
    if (v.length === prev.length + 1 && v.startsWith(prev)) key(root, st, v[v.length - 1], ev);
    else if (v.length === prev.length - 1 && prev.startsWith(v)) backspace(root, st, ev);
    else reject(root, st);
    syncInput(input, st);
  }

  function reject(root, st) {
    hint(root, st, '✋ По літері, без свайпів, вставки й підказок', 1500, 'rej');
  }

  function bindInput(root, st, input) {
    input.addEventListener('input', (ev) => onInput(root, st, input, ev));
    input.addEventListener('beforeinput', (ev) => {
      const t = ev.inputType || '';
      if (/^(insertFromPaste|insertFromDrop|insertReplacementText|insertFromYank|historyUndo|historyRedo|insertTranspose)/.test(t)) {
        ev.preventDefault();
        reject(root, st);
      }
    });
    input.addEventListener('paste', (ev) => { ev.preventDefault(); reject(root, st); });
    input.addEventListener('drop', (ev) => { ev.preventDefault(); reject(root, st); });
    input.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter') {
        ev.preventDefault();
        const exp = st.text[st.c];
        // Enter = пробіл на межі слова чи рядка; при червоному — такий самий проковтнутий натиск, як будь-який інший
        if (canType(st) && (st.red != null || exp === ' ' || exp === '\n')) key(root, st, st.red != null ? '\n' : ' ', ev);
        syncInput(input, st);
        return;
      }
      if (ev.key === 'Escape') { ev.preventDefault(); input.blur(); }
    });
    input.addEventListener('focus', () => { syncInput(input, st); root.querySelector('.tr-textbox').classList.add('focus'); paintHelp(root, st); });
    input.addEventListener('blur', () => { root.querySelector('.tr-textbox').classList.remove('focus'); paintHelp(root, st); });
  }

  function focusInput(root, st, force) {
    const input = root.querySelector('.tr-in');
    if (!input || !canType(st)) return;
    const a = document.activeElement;
    // у балачці людина пише — фокус не відбираємо (але схованому полю, що лишилось активним, — відбираємо)
    if (!force && a && a !== document.body && a !== input && a.matches && a.matches('input, textarea, [contenteditable]')
      && a.getClientRects().length > 0) return;
    try { input.focus({ preventScroll: true }); } catch { input.focus(); }
    syncInput(input, st);
  }

  function hint(root, st, text, ms, kind) {
    const el = root.querySelector('.tr-hint');
    if (!el) return;
    el.textContent = text;
    el.dataset.kind = kind || '';
    el.hidden = false;
    clearTimeout(st.hintTimer);
    st.hintTimer = setTimeout(() => { el.hidden = true; }, ms);
  }

  function shake(root, st) {
    const box = root.querySelector('.tr-textbox');
    if (!box || reduced()) return;
    box.classList.remove('tr-shake');
    void box.offsetWidth;          // перезапуск анімації (рідко: лише на помилку)
    box.classList.add('tr-shake');
    clearTimeout(st.shakeTimer);
    st.shakeTimer = setTimeout(() => box.classList.remove('tr-shake'), 160);
  }

  /** Новий заїзд (новий раунд чи новий текст у соло): чистий свій стан. */
  function resetRun(st) {
    st.c = 0; st.red = null; st.wrong = 0; st.correct = 0; st.buf = ''; st.k = ''; st.d = ''; st.lastAt = 0; st.events = 0;
    st.localGo = 0; st.finAt = 0; st.finished = false; st.finSent = false; st.latinRun = 0; st.sent = { c: -1, e: -1 };
    st.ws = -1; st.we = -1; st.wordSig = ''; st.prev = null; st.inputTop = -1; st.goShownAt = 0; st.readyLocal = 0;
    if (st.posTimer) { clearTimeout(st.posTimer); st.posTimer = 0; }
    st.x.fill(0); st.tx.fill(0); st.s.fill(-1); st.partN = 0; st.mineX = 0;
  }

  // =============================================================================================
  // 3. Траса: фон один раз в offscreen-канвасі, поверх — трактори й частинки щокадру
  // =============================================================================================

  function laneHeight(st, n) {
    // у підсумку траса лише показує, хто де зупинився, — нижча, щоб таблиця влізла в екран
    if (st.phase === 'done' && n >= 5) return st.W < 600 ? 18 : 22;
    if (st.W < 600) return n >= 7 ? 22 : 28;
    // Steam Deck і невисокі ноути (≤ 820 px): десять доріжок по 30 з'їли б пів екрана
    if (window.innerHeight <= 820) return n <= 4 ? 38 : n <= 7 ? 28 : 22;
    return n <= 4 ? 44 : n <= 7 ? 36 : 30;
  }

  function palette(st) {
    const css = (n, f) => (st.ctx ? st.ctx.css(n, f) : f);
    return {
      sky: css('--tr-sky', '#1a2e3a'), field: css('--tr-field', '#2c4a2e'), line: css('--line', '#2f4d3d'),
      text: css('--text', '#ecf1ea'), bg2: css('--bg2', '#16291f'), accent: css('--accent', '#f4c542'),
      danger: css('--danger', '#e57373'), dust: css('--tr-dust', 'rgba(214, 190, 140, .55)'), muted: css('--muted', '#9db3a5'),
      seats: SEAT_COLORS.map((f, i) => css('--tr-c' + i, f)),
    };
  }

  /**
   * Канвас траси під ширину картки й склад. Ширину міряємо лише тоді, коли щось могло змінитись (ResizeObserver
   * скидає trackSig), тож щокадру тут — порівняння рядка, а не читання розкладки.
   */
  function ensureTrack(root, st) {
    const host = root.querySelector('.tr-trackbox');
    if (!host || !st.ctx) return;
    const n = Math.max(solo(st) ? 1 : 2, racers(st).length);
    const theme = document.documentElement.getAttribute('data-theme') || '';
    const sig = n + ':' + theme + ':' + (st.phase === 'done');
    if (st.cv && st.bg && st.trackSig === sig) return;
    const W = Math.max(280, Math.floor(host.clientWidth || 600));
    st.W = W;
    const laneH = laneHeight(st, n);
    const H = n * laneH + 8;
    st.cv = st.ctx.ui.canvas(host, { w: W, h: H, cls: 'tr-track' });
    st.lanes = n; st.laneH = laneH; st.H = H; st.trackSig = sig;
    st.pal = palette(st);
    drawBackground(st);
  }

  const X0 = 24, FLAG = 18, NOSE = 30;
  const xOf = (st, c) => X0 + (st.W - X0 - FLAG - NOSE) * (st.len > 0 ? Math.min(1, c / st.len) : 0);

  function drawBackground(st) {
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const bg = st.bg || document.createElement('canvas');
    bg.width = Math.round(st.W * dpr); bg.height = Math.round(st.H * dpr);
    const g = bg.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    const pal = st.pal;
    const grad = g.createLinearGradient(0, 0, 0, st.H);
    grad.addColorStop(0, pal.sky); grad.addColorStop(0.35, pal.field); grad.addColorStop(1, pal.field);
    g.fillStyle = grad;
    g.fillRect(0, 0, st.W, st.H);
    // борозни поля — ледь помітні смуги, щоб доріжка читалась як оранка
    g.globalAlpha = 0.07;
    g.fillStyle = pal.text;
    for (let y = 4; y < st.H; y += 6) g.fillRect(0, y, st.W, 1);
    g.globalAlpha = 1;
    // доріжки пунктиром
    g.strokeStyle = pal.line;
    g.lineWidth = 1;
    g.setLineDash([6, 6]);
    for (let i = 1; i < st.lanes; i++) {
      const y = 4 + i * st.laneH + 0.5;
      g.beginPath(); g.moveTo(X0 - 8, y); g.lineTo(st.W - FLAG, y); g.stroke();
    }
    g.setLineDash([]);
    // старт — стовпчик ліворуч
    g.fillStyle = pal.muted;
    g.globalAlpha = 0.5;
    g.fillRect(X0 - 4, 4, 2, st.H - 8);
    g.globalAlpha = 1;
    // фініш — шахова смуга праворуч
    const fx = st.W - FLAG, cell = 6;
    for (let y = 4, r = 0; y < st.H - 4; y += cell, r++)
      for (let x = 0, c2 = 0; x < FLAG; x += cell, c2++) {
        g.fillStyle = (r + c2) % 2 ? pal.bg2 : pal.text;
        g.fillRect(fx + x, y, Math.min(cell, FLAG - x), Math.min(cell, st.H - 4 - y));
      }
    st.bg = bg;
  }

  /** Частинки: плаский Float32Array на 160 записів — x, y, vx, vy, вік, скільки живе, вид (0 пил, 1 дим, 2+ конфеті). */
  function spawn(st, x, y, vx, vy, life, kind) {
    if (reduced()) return;
    let i = st.partN;
    if (i >= 160) i = (st.spawnAt = ((st.spawnAt || 0) + 1) % 160);   // повний — перезаписуємо по колу
    else st.partN++;
    const o = i * 7, p = st.parts;
    p[o] = x; p[o + 1] = y; p[o + 2] = vx; p[o + 3] = vy; p[o + 4] = 0; p[o + 5] = life; p[o + 6] = kind;
  }

  const laneY = (st, lane) => 4 + lane * st.laneH + st.laneH / 2;

  function laneOf(st, seat) {
    const list = racers(st);
    for (let i = 0; i < list.length; i++) if (list[i].seat === seat) return i;
    return -1;
  }

  function smoke(st, seat) {
    const lane = laneOf(st, seat);
    if (lane < 0 || !st.cv) return;
    const x = (st.ctx && seat === st.ctx.seat ? st.mineX : st.x[seat]) + 20, y = laneY(st, lane) - 10;
    for (let k = 0; k < 3; k++) spawn(st, x, y, (Math.random() - 0.5) * 0.02, -0.03 - Math.random() * 0.02, 600, 1);
  }

  function confetti(st) {
    const lane = laneOf(st, st.ctx.seat);
    if (lane < 0 || !st.cv) return;
    const x = st.W - FLAG - 10, y = laneY(st, lane);
    for (let k = 0; k < 40; k++) {
      const a = Math.random() * Math.PI - Math.PI, v = 0.08 + Math.random() * 0.18;
      spawn(st, x, y, Math.cos(a) * v, Math.sin(a) * v, 1200, 2 + (k % 6));
    }
  }

  const TAU = Math.PI * 2;

  /**
   * Трактор, що їде праворуч: велике заднє колесо під кабіною, мале переднє під капотом, димар, номер місця на
   * капоті. (x, y) — лівий край і вісь заднього колеса; k — масштаб (на вузьких доріжках телефона трактор менший).
   */
  function drawTractor(g, st, x, y, k, color, seat, mine, s) {
    const pal = st.pal;
    const gone = s === 3;
    g.save();
    g.translate(x, y);
    g.scale(k, k);
    g.globalAlpha = gone ? 0.4 : 1;
    const body = gone ? pal.muted : color;
    const spin = x / (7 * k);                 // кут спиць — пропорційно шляху (радіус заднього колеса 7)
    g.fillStyle = body;
    g.beginPath(); g.roundRect(8, -7, 22, 9, 3); g.fill();          // капот
    g.beginPath(); g.roundRect(2, -19, 13, 16, 2); g.fill();        // кабіна
    g.fillStyle = pal.bg2;
    g.fillRect(4, -17, 9, 6);                                       // скло
    g.fillStyle = '#2a2a2a';
    g.fillRect(1, -20, 15, 2);                                      // дах
    g.fillRect(24, -14, 2.5, 7);                                    // димар
    // колеса: шини, маточини й спиці
    g.fillStyle = '#1b1b1b';
    g.beginPath(); g.arc(8, 3, 7.5, 0, TAU); g.fill();
    g.beginPath(); g.arc(26, 6, 4.5, 0, TAU); g.fill();
    g.fillStyle = '#9a9a9a';
    g.beginPath(); g.arc(8, 3, 3.2, 0, TAU); g.fill();
    g.beginPath(); g.arc(26, 6, 1.9, 0, TAU); g.fill();
    g.strokeStyle = '#1b1b1b';
    g.lineWidth = 1.3;
    g.beginPath();
    for (let q = 0; q < 2; q++) {
      const a = spin + q * 1.5708, b = spin * 1.66 + q * 1.5708;
      g.moveTo(8 - Math.cos(a) * 3.2, 3 - Math.sin(a) * 3.2); g.lineTo(8 + Math.cos(a) * 3.2, 3 + Math.sin(a) * 3.2);
      g.moveTo(26 - Math.cos(b) * 1.9, 6 - Math.sin(b) * 1.9); g.lineTo(26 + Math.cos(b) * 1.9, 6 + Math.sin(b) * 1.9);
    }
    g.stroke();
    // номер місця на капоті: колір — не єдина позначка
    g.fillStyle = '#111';
    g.font = 'bold 9px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(seat + 1), 22, -2.5);
    if (mine) {
      g.strokeStyle = pal.accent;
      g.lineWidth = 1.5;
      g.beginPath(); g.roundRect(-3, -23, 37, 37, 7); g.stroke();
      g.fillStyle = pal.accent;
      g.beginPath(); g.moveTo(15, -25); g.lineTo(11, -31); g.lineTo(19, -31); g.fill();
    }
    if (s === 2) {                            // на фініші — прапорець на даху
      g.fillStyle = pal.text; g.fillRect(12, -30, 1.5, 11);
      g.fillStyle = pal.accent; g.fillRect(13.5, -30, 7, 5);
    }
    g.restore();
  }

  /** Нік — праворуч від трактора, а біля фінішу, де праворуч місця нема, — ліворуч. */
  function drawNick(g, st, x, y, k, seat, raw, gone) {
    if (st.laneH < 28) return;
    g.font = '11px system-ui, sans-serif';
    // обрізаний нік і його ширину рахуємо раз на нік, а не щокадру
    if (st.nickOf[seat] !== raw) { st.nickOf[seat] = raw; st.nickTxt[seat] = nickShort(raw); st.nickW[seat] = g.measureText(st.nickTxt[seat]).width; }
    const nick = st.nickTxt[seat], w = st.nickW[seat];
    const right = x + 36 * k;
    g.globalAlpha = gone ? 0.5 : 0.95;
    g.fillStyle = st.pal.text;
    g.textBaseline = 'middle';
    if (right + w < st.W - FLAG - 4) { g.textAlign = 'left'; g.fillText(nick, right, y - 12 * k); }
    else { g.textAlign = 'right'; g.fillText(nick, x - 5, y - 12 * k); }
    g.globalAlpha = 1;
  }

  const nickShort = (n) => { n = n || ''; return n.length > 12 ? n.slice(0, 11) + '…' : n; };

  function draw(root, st, t, dt) {
    const cv = st.cv;
    if (!cv || !st.bg) return;
    const t0 = performance.now();
    const g = cv.ctx;
    g.drawImage(st.bg, 0, 0, st.W, st.H);
    const list = racers(st), me = st.ctx.seat, rm = reduced();
    const k = rm ? 1 : 1 - Math.exp(-dt / 120);
    const kk = Math.min(1, st.laneH / 38);           // трактор ~30 px заввишки; на вузьких доріжках — менший
    for (let lane = 0; lane < list.length && lane < st.lanes; lane++) {
      const r = list[lane], i = r.seat;
      let x, s = st.s[i] >= 0 ? st.s[i] : r.s;
      let aim;
      if (i === me && st.phase === 'go' && !r.gone) {
        // свій — з локального c, лише з легким згладжуванням 60 мс, щоб не стрибав по літері
        aim = xOf(st, st.finished ? st.len : st.c);
        st.mineX = rm || !st.mineX ? aim : st.mineX + (aim - st.mineX) * (1 - Math.exp(-dt / 60));
        x = st.mineX;
        s = st.finished ? 2 : st.red != null ? 1 : 0;
      } else {
        aim = xOf(st, st.tx[i]);
        if (!st.x[i] || rm) st.x[i] = aim;
        else st.x[i] += (aim - st.x[i]) * k;
        x = st.x[i];
      }
      const y = laneY(st, lane) + 5 * kk;
      // пил за трактором, що їде
      if (!rm && st.phase === 'go' && s === 0 && t - st.dustAt[i] > 90 && aim - x > 0.5) {
        st.dustAt[i] = t;
        spawn(st, x + 1, y + 8 * kk, -0.02 - Math.random() * 0.02, -0.01 - Math.random() * 0.01, 500, 0);
      }
      drawTractor(g, st, x, y, kk, st.pal.seats[i % 10], i, i === me, r.gone ? 3 : s);
      drawNick(g, st, x, y, kk, i, r.nick, r.gone);
    }
    // частинки: рух, вигорання й стискання масиву зсувом — без алокацій
    const p = st.parts;
    let w = 0;
    for (let j = 0; j < st.partN; j++) {
      const o = j * 7;
      p[o + 4] += dt;
      if (p[o + 4] >= p[o + 5]) continue;
      const kind = p[o + 6];
      p[o] += p[o + 2] * dt; p[o + 1] += p[o + 3] * dt;
      if (kind >= 2) p[o + 3] += 0.0004 * dt;      // конфеті падає
      g.globalAlpha = 1 - p[o + 4] / p[o + 5];
      g.fillStyle = kind === 0 ? st.pal.dust : kind === 1 ? st.pal.danger : st.pal.seats[(kind - 2) % 10];
      const size = kind === 0 ? 4 : kind === 1 ? 5 : 3;
      g.fillRect(p[o] - size / 2, p[o + 1] - size / 2, size, size);
      if (w !== j) for (let q = 0; q < 7; q++) p[w * 7 + q] = p[o + q];
      w++;
    }
    st.partN = w;
    g.globalAlpha = 1;
    const ms = performance.now() - t0;
    st.perf.frames++; st.perf.ms += ms; if (ms > st.perf.max) st.perf.max = ms;
  }

  function loop(root, st) {
    if (st.raf) return;
    st.lastT = performance.now();
    const tick = (t) => {
      st.raf = 0;
      if (!root.isConnected || !st.ctx) return;
      const dt = Math.min(100, t - st.lastT);
      st.lastT = t;
      if (!document.hidden && root.offsetParent !== null) {
        ensureTrack(root, st);
        draw(root, st, t, dt);
        if (t - st.statsAt > 250) { st.statsAt = t; paintStats(root, st); paintCountdown(root, st); }
      }
      const live = st.phase === 'ready' || st.phase === 'go' || t < st.stopAt || st.partN > 0;
      if (live) st.raf = requestAnimationFrame(tick);
    };
    st.raf = requestAnimationFrame(tick);
  }

  // =============================================================================================
  // 4. Текст, числа, підсумок
  // =============================================================================================

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', '\'': '&#39;' }[c]));
  const isSep = (ch) => ch === ' ' || ch === '\n';

  /** Межі поточного слова: від попереднього пробілу/\n до наступного включно. */
  function wordBounds(st) {
    const t = st.text, c = Math.min(st.c, st.len);
    let ws = c;
    while (ws > 0 && !isSep(t[ws - 1])) ws--;
    let we = c;
    while (we < st.len && !isSep(t[we])) we++;
    if (we < st.len) we++;
    return [ws, Math.max(we, Math.min(st.len, c + 1))];
  }

  /** На натиск перемальовується лише слово (≤ 25 елементів); решта — два textContent, і то коли слово змінилось. */
  function paintWord(root, st) {
    const box = root.querySelector('.tr-text');
    if (!box) return;
    const done = box.querySelector('.tr-done'), word = box.querySelector('.tr-word'), rest = box.querySelector('.tr-rest');
    const mine = !!(st.ctx && st.ctx.mine && meRacer(st) && (st.phase === 'go' || st.phase === 'done' || st.phase === 'ready'));
    if (!st.text) {
      done.textContent = ''; word.innerHTML = ''; rest.textContent = '';
      st.ws = st.we = -1; st.wordSig = '';
      return;
    }
    const c = mine ? st.c : 0;
    const [ws, we] = mine ? wordBounds(st) : [0, 0];
    let moved = false;
    if (ws !== st.ws || we !== st.we) {
      st.ws = ws; st.we = we;
      done.textContent = st.text.slice(0, ws);
      rest.textContent = st.text.slice(we);
      moved = true;
    }
    const sig = ws + ':' + we + ':' + c + ':' + (st.red == null ? '' : st.red) + ':' + st.finished + ':' + st.phase;
    if (sig === st.wordSig) return;
    st.wordSig = sig;
    let html = '';
    for (let i = ws; i < we; i++) {
      const ch = st.text[i];
      let cl = i < c ? 'ok' : i === c && !st.finished ? (st.red != null ? 'bad' : 'cur') : '';
      if (ch === '\n') cl += ' nl';
      const shown = ch === '\n' ? '↵\n' : ch;
      const typed = i === c && st.red != null ? ' data-typed="' + esc(st.red === ' ' ? '␣' : st.red === '\n' ? '↵' : st.red) + '"' : '';
      html += '<b class="' + cl.trim() + '"' + typed + '>' + esc(shown) + '</b>';
    }
    word.innerHTML = html;
    word.classList.toggle('on', mine && st.phase === 'go' && !st.finished);
    if (moved) placeInput(root, st);
  }

  /** Невидиме поле стоїть над поточним рядком: iOS, прокручуючи до фокуса, покаже саме той рядок, а кільце пада — поле. */
  function placeInput(root, st) {
    const input = root.querySelector('.tr-in'), word = root.querySelector('.tr-word');
    if (!input || !word) return;
    const top = word.offsetTop;
    if (st.inputTop !== top) { st.inputTop = top; input.style.top = top + 'px'; }
  }

  function fmtTime(ms) {
    if (ms == null) return '—';
    const s = ms / 1000, m = Math.floor(s / 60), r = s - m * 60;
    return m + ':' + (r < 10 ? '0' : '') + r.toFixed(1).replace('.', ',');
  }
  const clock = (ms) => { const s = Math.max(0, Math.floor(ms / 1000)); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); };

  /** Скільки мс від старту за годинником сервера (зсув пораховано з endsIn на прийомі виду). */
  function elapsed(st) {
    const v = view(st);
    if (!v.goAt) return 0;
    return now() + st.skew - Date.parse(v.goAt);
  }

  /** Живе місце: хто вже доїхав — попереду (за місцем, далі за часом), решта — за c. */
  function liveOrder(st) {
    const meSeat = st.ctx ? st.ctx.seat : null;
    const list = racers(st).map((r) => {
      const mine = r.seat === meSeat && st.phase === 'go';
      const c = mine ? (st.finished ? st.len : st.c) : (st.tx[r.seat] || r.c);
      const fin = r.fin != null ? r.fin : (mine && st.finished ? 1e12 : null);
      return { r, c, fin };
    });
    list.sort((a, b) => {
      const af = a.fin != null, bf = b.fin != null;
      if (af !== bf) return af ? -1 : 1;
      if (af) return (a.r.place || 99) - (b.r.place || 99) || a.fin - b.fin;
      return b.c - a.c || a.r.seat - b.r.seat;
    });
    return list;
  }

  function myNumbers(st) {
    const el = st.localGo ? Math.max(1000, (st.finished && st.finAt ? st.finAt : now()) - st.localGo) : 0;
    const cpm = el ? Math.round(st.c * 60000 / el) : 0;
    const acc = st.correct + st.wrong > 0 ? Math.round(100 * st.correct / (st.correct + st.wrong)) : 100;
    const place = liveOrder(st).findIndex((x) => x.r.seat === st.ctx.seat) + 1;
    return { cpm, acc, place };
  }

  function paintStats(root, st) {
    const el = root.querySelector('.tr-nums');
    if (!el || !st.ctx) return;
    const v = view(st);
    let html = '';
    const r = meRacer(st);
    if (st.ctx.mine && r && st.phase === 'go') {
      const n = myNumbers(st);
      const cpm = st.phase === 'done' && r.cpm != null ? r.cpm : r.fin != null && r.cpm != null ? r.cpm : n.cpm;
      const acc = r.acc != null ? r.acc : n.acc;
      const place = r.place || (st.phase === 'go' && !solo(st) && !r.flag ? n.place : null);
      html = '<span title="Знаків за хвилину">⌨ <b>' + cpm + '</b> зн/хв</span><span title="Точність">🎯 ' + acc + ' %</span>'
        + (place && !solo(st) ? '<span title="Місце">🏁 ' + place + '-е</span>' : '')
        + '<span title="Час">⏱ ' + (r.fin != null ? fmtTime(r.fin) : clock(elapsed(st))) + '</span>';
      if (st.phase === 'go' && v.endsAt && v.tail) {
        const left = Date.parse(v.endsAt) - now() - st.skew;
        html += '<span class="tr-tail" title="Скільки лишилось до кінця заїзду">⌛ ' + clock(left) + '</span>';
      }
    } else if (st.phase === 'go') {
      // глядач: лідера каже рядок статусу, тут — годинник і скільки вже доїхало
      const list = racers(st);
      const fin = list.filter((x) => x.fin != null || st.s[x.seat] === 2).length;
      html = '<span>👁 Дивишся збоку</span><span>⏱ ' + clock(elapsed(st)) + '</span><span>🏁 ' + fin + ' з ' + list.length + '</span>';
    }
    if (html !== st.statsSig) { st.statsSig = html; el.innerHTML = html; }
    paintLive(root, st);
  }

  /** Живий список праворуч (на широкій картці): місце, нік, %, зн/хв. Не частіше двох разів на секунду. */
  function paintLive(root, st) {
    const el = root.querySelector('.tr-live');
    if (!el) return;
    if (st.phase !== 'go' || solo(st)) { if (st.liveSig) { el.innerHTML = ''; st.liveSig = ''; } return; }
    const t = performance.now();
    if (t - st.liveAt < 500) return;
    st.liveAt = t;
    const el2 = elapsed(st);
    const html = liveOrder(st).map((x) => {
      const r = x.r;
      const pct = Math.floor(100 * Math.min(1, x.c / Math.max(1, st.len)));
      const cpm = r.cpm != null ? r.cpm : el2 > 1000 ? Math.round(x.c * 60000 / el2) : 0;
      return '<div class="tr-lrow' + (st.ctx && r.seat === st.ctx.seat ? ' me' : '') + (r.gone ? ' gone' : '') + '">'
        + '<span class="tr-dot tr-s' + r.seat + '">' + (r.seat + 1) + '</span>'
        + '<span class="tr-lnick">' + esc(r.nick) + (r.flag ? ' 🤖' : '') + '</span>'
        + '<span class="tr-lpct">' + (r.fin != null && !r.flag ? '🏁' : pct + ' %') + '</span>'
        + '<span class="tr-lcpm">' + cpm + '</span></div>';
    }).join('');
    if (html !== st.liveSig) { st.liveSig = html; el.innerHTML = html; }
  }

  function paintCountdown(root, st) {
    const el = root.querySelector('.tr-count');
    if (!el) return;
    let text = '';
    if (st.phase === 'ready' && st.readyLocal) {
      const left = st.readyLocal - performance.now();
      text = left > 2000 ? '3' : left > 1000 ? '2' : '1';
    } else if (st.phase === 'go' && st.goShownAt && performance.now() - st.goShownAt < 700) text = 'Поїхали!';
    if (el.dataset.t !== text) {
      el.dataset.t = text;
      el.textContent = text;
      el.hidden = !text;
      if (text) {
        el.classList.remove('tr-pop'); void el.offsetWidth; el.classList.add('tr-pop');
        beep(st, text === 'Поїхали!' ? 'go' : 'tick');
      }
    }
  }

  function srcLine(v) {
    const s = v.src;
    if (!s) return '';
    if (s.kind === 'classic') return (s.author ? s.author + ' — ' : '') + '«' + s.title + '»' + (s.year ? ', ' + s.year : '');
    return s.title;
  }

  /** Підсумок партії (за столом) чи заїзду (соло) — таблиця за result.order, репліка Глека, кнопки модуля. */
  function paintBoard(root, st) {
    const el = root.querySelector('.tr-board');
    if (!el || !st.ctx) return;
    const v = view(st), ctx = st.ctx;
    let html = '';
    if (st.phase === 'done' && v.result && !st.showPick) {
      const rows = (v.result.order || []).map((seat) => {
        const r = racers(st).find((x) => x.seat === seat);
        if (!r) return '';
        const medal = r.place === 1 ? '🥇' : r.place === 2 ? '🥈' : r.place === 3 ? '🥉' : r.place ? r.place + '.' : '·';
        const t = r.cpm != null && r.fin != null && !r.flag ? titleOf(r.cpm) : null;
        let note = '';
        if (r.flag) note = '🤖 не зараховано: ' + (REASON[r.flag] || r.flag);
        else if (r.fin == null) note = 'не дописав: ' + Math.floor(100 * r.c / Math.max(1, v.len)) + ' %';
        if (r.gone) note += (note ? ' · ' : '') + 'пішов';
        return '<tr class="' + (seat === ctx.seat ? 'me' : '') + (r.gone ? ' gone' : '') + '">'
          + '<td class="tr-medal">' + medal + '</td>'
          + '<td class="tr-bnick"><span class="tr-dot tr-s' + seat + '">' + (seat + 1) + '</span>' + esc(r.nick)
          + (note ? '<div class="tr-note">' + esc(note) + '</div>' : '') + '</td>'
          + '<td class="tr-num"><b>' + (r.cpm != null ? r.cpm : '—') + '</b> <small>зн/хв</small></td>'
          + '<td class="tr-num">' + (r.acc != null ? r.acc + ' %' : '—') + '</td>'
          + '<td class="tr-num tr-opt">' + (r.fin != null ? r.wrong : '—') + '</td>'
          + '<td class="tr-num">' + (r.fin != null ? fmtTime(r.fin) : '—') + '</td>'
          + '<td class="tr-title tr-opt">' + (t ? t[2] + ' ' + t[1] : '') + '</td></tr>';
      }).join('');
      html = (solo(st) ? soloSummary(st)
        : '<table class="tr-table"><thead><tr><th></th><th>Хто</th><th class="tr-num">Швидкість</th><th class="tr-num">Точність</th>'
          + '<th class="tr-num tr-opt">Помилок</th><th class="tr-num">Час</th><th class="tr-opt">Звання</th></tr></thead><tbody>' + rows + '</tbody></table>')
        + '<p class="tr-say">🏺 <i>' + esc(v.result.say || '') + '</i></p>'
        + '<div class="tr-acts">' + (solo(st)
          ? '<button type="button" class="primary tr-again" data-pad-first>Ще раз</button>'
            + '<button type="button" class="ghost tr-change">Змінити</button>'
            + '<button type="button" class="ghost tr-challenge">🏁 Кинути виклик друзям</button>'
          : '<button type="button" class="ghost tr-train">🏋️ Потренуватись самому</button>') + '</div>';
    }
    if (html === st.boardSig) return;
    st.boardSig = html;
    el.innerHTML = html;
    el.hidden = !html;
    const b = (sel, fn) => { const x = el.querySelector(sel); if (x) x.onclick = fn; };
    b('.tr-again', () => soloGo(st, {}));
    b('.tr-change', () => { st.showPick = true; paint(root, st); });
    b('.tr-challenge', (e) => openRoom(e.currentTarget, 'CreateRoom', 'typerace', { length: v.opts.length, source: v.opts.source }));
    b('.tr-train', (e) => openRoom(e.currentTarget, 'OpenSolo', 'typerace-solo', null));
  }

  function soloSummary(st) {
    const v = view(st), r = meRacer(st), me = v.me || {};
    if (!r) return '';
    const t = r.cpm != null && !r.flag && r.fin != null ? titleOf(r.cpm) : null;
    return '<div class="tr-solo">'
      + (r.fin != null
        ? '<div class="tr-big">' + (me.isRecord ? '🏆 ' : '') + '<b>' + r.cpm + '</b> зн/хв' + (t ? ' · ' + t[2] + ' ' + t[1] : '') + '</div>'
          + '<div class="muted">🎯 ' + (r.acc != null ? r.acc : '—') + ' % · помилок ' + r.wrong + ' · ⏱ ' + fmtTime(r.fin)
          + (r.flag ? ' · 🤖 не зараховано: ' + esc(REASON[r.flag] || r.flag) : '') + '</div>'
        : '<div class="tr-big">Не доїхав: ' + Math.floor(100 * r.c / Math.max(1, v.len)) + ' %</div>')
      + '<div class="muted small">Рекорд: ' + (me.best != null ? me.best + ' зн/хв' : 'ще нема') + ' · заїздів: ' + (me.runs || 0) + '</div></div>';
  }

  function openRoom(btn, method, game, arg) {
    btn.disabled = true;
    HGames.call(method, game, arg).then((r) => {
      btn.disabled = false;
      if (r && r.ok && r.roomId) location.hash = '#games/room/' + encodeURIComponent(r.roomId);
    }, () => { btn.disabled = false; });
  }

  function soloGo(st, extra) {
    const v = view(st);
    const payload = {};
    payload[WIRE.go[0]] = extra.length || st.pickLen || (v.opts && v.opts.length) || 'medium';
    payload[WIRE.go[1]] = extra.source || st.pickSrc || (v.opts && v.opts.source) || 'all';
    st.showPick = false;
    unlockSound(st, true);
    st.ctx.act(WIRE_GO, payload);
  }

  /** Соло: вибір довжини й джерела, «Поїхали», рекорд. */
  function paintPick(root, st) {
    const el = root.querySelector('.tr-pick');
    if (!el || !st.ctx) return;
    const v = view(st);
    const show = solo(st) && st.ctx.mine && (st.phase === 'pick' || (st.phase === 'done' && st.showPick));
    if (!show) { if (!el.hidden) { el.hidden = true; el.innerHTML = ''; st.pickSig = ''; } return; }
    const len = st.pickLen || (v.opts && v.opts.length) || 'medium';
    const src = st.pickSrc || (v.opts && v.opts.source) || 'all';
    const me = v.me || {};
    const sig = len + '|' + src + '|' + JSON.stringify(me) + '|' + !!v.noTexts;
    if (sig === st.pickSig && !el.hidden) return;
    st.pickSig = sig;
    el.hidden = false;
    el.innerHTML = v.noTexts
      ? '<p class="tr-empty">Тексти кудись подівались — Клавоперегони відпочивають. Глек уже шукає.</p>'
      : '<div class="tr-row"><span class="muted small">Довжина</span><div class="tr-chips" role="group" aria-label="Довжина">'
        + LENGTHS.map((l) => '<button type="button" class="tr-chip' + (l[0] === len ? ' on' : '') + '" data-len="' + l[0] + '" aria-pressed="' + (l[0] === len)
          + '">' + l[1] + ' <small>' + l[2] + '</small></button>').join('') + '</div></div>'
        + '<div class="tr-row"><span class="muted small">Тексти</span><div class="tr-chips" role="group" aria-label="Тексти">'
        + SOURCES.map((s) => '<button type="button" class="tr-chip' + (s[0] === src ? ' on' : '') + '" data-src="' + s[0] + '" aria-pressed="' + (s[0] === src)
          + '">' + s[1] + '</button>').join('') + '</div></div>'
        + '<button type="button" class="primary tr-go" data-pad-first>Поїхали</button>'
        + '<div class="muted small">Рекорд: ' + (me.best != null ? me.best + ' зн/хв' : 'ще нема') + ' · заїздів: ' + (me.runs || 0)
        + ' · Enter — старт</div>';
    el.querySelectorAll('[data-len]').forEach((b) => b.onclick = () => { st.pickLen = b.dataset.len; paintPick(root, st); });
    el.querySelectorAll('[data-src]').forEach((b) => b.onclick = () => { st.pickSrc = b.dataset.src; paintPick(root, st); });
    const go = el.querySelector('.tr-go');
    if (go) go.onclick = () => soloGo(st, {});
  }

  function paintHelp(root, st) {
    const el = root.querySelector('.tr-help');
    if (!el || !st.ctx) return;
    const coarse = st.ctx.ui.coarse();
    const input = root.querySelector('.tr-in');
    const focused = input && document.activeElement === input;
    let text = '';
    if (st.ctx.mine && meRacer(st) && (st.phase === 'ready' || (st.phase === 'go' && canType(st) && !focused)))
      text = coarse ? '👆 Тапни по тексту й друкуй по літері — без свайпів'
        : st.phase === 'ready' ? '⌨ Прочитай перший рядок — і руки на клавіатуру' : '⌨ Друкуй — помилка висить червоним, доки не натиснеш Backspace';
    if (el.textContent !== text) el.textContent = text;
    el.hidden = !text;
  }

  // =============================================================================================
  // 5. Звук — WebAudio-синтез, тихо, лише після жесту людини
  // =============================================================================================

  function soundOn() { try { return localStorage.getItem('typerace.sound') !== '0'; } catch { return true; } }

  function unlockSound(st, human) {
    if (!human || st.ac || !st.sound) return;
    try { const AC = window.AudioContext || window.webkitAudioContext; if (AC) st.ac = new AC(); } catch { st.ac = null; }
  }

  function tone(st, freq, ms, type, at) {
    const ac = st.ac;
    if (!ac || !st.sound) return;
    const t = ac.currentTime + (at || 0);
    const o = ac.createOscillator(), g = ac.createGain();
    o.type = type || 'sine';
    o.frequency.value = freq;
    g.gain.setValueAtTime(0.0001, t);
    g.gain.exponentialRampToValueAtTime(0.125, t + 0.01);        // ≈ −18 дБ
    g.gain.exponentialRampToValueAtTime(0.0001, t + ms / 1000);
    o.connect(g); g.connect(ac.destination);
    o.start(t); o.stop(t + ms / 1000 + 0.02);
  }

  function beep(st, what) {
    if (!st.ac || !st.sound) return;
    if (what === 'tick') tone(st, 880, 60);
    else if (what === 'go') tone(st, 1320, 180);
    else if (what === 'err') tone(st, 140, 50, 'triangle');
    else if (what === 'fin') { tone(st, 660, 120); tone(st, 990, 120, 'sine', 0.12); }
  }

  // =============================================================================================
  // 6. Модуль
  // =============================================================================================

  function mount(root, ctx) {
    const st = state(root);
    st.ctx = ctx;
    root.innerHTML = '<div class="tr-wrap">'
      + '<div class="tr-trackbox" aria-hidden="true"></div>'
      + '<div class="tr-cols"><div class="tr-main">'
      + '<div class="tr-src muted small"></div>'
      + '<div class="tr-textbox" hidden><div class="tr-text"><span class="tr-done"></span><span class="tr-word"></span><span class="tr-rest"></span></div>'
      + '<input class="tr-in" type="text" autocomplete="off" autocorrect="off" autocapitalize="off" spellcheck="false" enterkeyhint="done"'
      + ' aria-label="Поле для друку" data-pad-first>'
      + '<div class="tr-count" hidden></div><div class="tr-hint" hidden role="status"></div></div>'
      + '<div class="tr-help muted small" hidden></div>'
      + '<div class="tr-stats"><span class="tr-nums"></span><span class="tr-sbtns"><button type="button" class="ghost tr-stop" hidden>■ Стоп</button>'
      + '<button type="button" class="ghost tr-snd" data-pad-skip></button></span></div>'
      + '<div class="tr-pick" hidden></div>'
      + '</div><div class="tr-side"><div class="tr-live"></div></div></div>'
      + '<div class="tr-board" hidden></div>'
      + '</div>';
    const input = root.querySelector('.tr-in');
    bindInput(root, st, input);
    const box = root.querySelector('.tr-textbox');
    // клік мишкою по тексту — фокус у поле без виділення; на пальці — через click, щоб iOS відкрила клавіатуру
    box.addEventListener('pointerdown', (e) => {
      if (e.target === input || e.pointerType === 'touch') return;
      if (canType(st)) { e.preventDefault(); focusInput(root, st, true); }
    });
    box.addEventListener('click', (e) => { if (e.target !== input) focusInput(root, st, true); });
    const snd = root.querySelector('.tr-snd');
    const paintSnd = () => {
      snd.textContent = st.sound ? '🔈' : '🔇';
      snd.title = st.sound ? 'Звук увімкнено' : 'Звук вимкнено';
      snd.setAttribute('aria-label', st.sound ? 'Вимкнути звук' : 'Увімкнути звук');
    };
    snd.onclick = (e) => {
      st.sound = !st.sound;
      try { localStorage.setItem('typerace.sound', st.sound ? '1' : '0'); } catch { /* та й годі */ }
      unlockSound(st, isHuman(st, e));
      paintSnd();
    };
    paintSnd();
    root.querySelector('.tr-stop').onclick = () => { if (st.ctx) st.ctx.act(WIRE_STOP, {}); };
    st.onVis = () => { if (!document.hidden) loop(root, st); };
    document.addEventListener('visibilitychange', st.onVis);
    if (window.ResizeObserver) {
      st.ro = new ResizeObserver(() => { st.trackSig = ''; st.inputTop = -1; placeInput(root, st); if (st.bg) loop(root, st); });
      st.ro.observe(root.querySelector('.tr-trackbox'));
    }
    update(root, ctx);
  }

  function update(root, ctx) {
    const st = state(root);
    st.ctx = ctx;
    const wrap = root.querySelector('.tr-wrap');
    if (!wrap) return;
    const v = ctx.view || {};
    const round = ctx.room ? ctx.room.round : 0;
    const key = (v.text || '') + '|' + round + '|' + (v.readyAt || '');
    const was = st.phase;
    // новий заїзд: інший раунд, інший текст або новий відлік у соло
    if (key !== st.key) {
      if (st.key && st.round !== round && ctx.room) forget('typerace:' + ctx.room.id + ':' + st.round);
      st.key = key;
      st.round = round;
      st.text = v.text || '';
      st.len = v.len || 0;
      resetRun(st);
      st.trackSig = '';
      // вірш (багато коротких рядків) — у дві-три колонки, інакше на Деку й на ноуті текст лізе за екран
      let lines = st.text ? 1 : 0;
      for (let i = 0; i < st.len; i++) if (st.text.charCodeAt(i) === 10) lines++;
      const tx = root.querySelector('.tr-text');
      tx.classList.toggle('tr-verse', lines >= 8 && st.len / lines <= 48);
      tx.classList.toggle('tr-many', lines >= 14);
    }
    st.phase = v.phase || '';
    // годинник сервера: зсув — з endsIn на мить прийому; відлік — з goIn
    if (v.endsIn != null && v.endsAt) st.skew = Date.parse(v.endsAt) - v.endsIn - now();
    if (st.phase === 'ready' && v.goIn != null) st.readyLocal = performance.now() + v.goIn;
    // цілі тракторів — з виду (на подіях він свіжіший за кадр)
    for (const r of v.racers || []) { st.tx[r.seat] = r.c; st.s[r.seat] = r.gone ? 3 : r.s; }

    const me = meRacer(st);
    if (st.phase === 'go' && ctx.mine && me && !me.gone && !st.localGo) {
      if (me.fin != null) { st.finished = true; st.finSent = true; st.c = st.len; st.localGo = now(); }
      else if (!restore(st)) { st.localGo = now(); st.lastAt = st.localGo; save(st); }
      if (!st.finished) { st.sent = { c: -1, e: -1 }; sendPos(st); }
      st.goShownAt = was === 'ready' ? performance.now() : 0;
      st.wordSig = ''; st.ws = -1;
      setTimeout(() => focusInput(root, st, false), 0);
    }
    if (me && me.fin != null && !st.finished) { st.finished = true; st.finSent = true; st.c = st.len; }
    if (st.phase === 'done' && was === 'go') st.stopAt = performance.now() + 1500;
    if (st.phase !== 'done') st.showPick = false;

    const src = root.querySelector('.tr-src');
    const sl = st.phase === 'lobby' || st.phase === 'pick' || st.showPick ? '' : srcLine(v);
    if (src.textContent !== sl) src.textContent = sl;
    wrap.dataset.phase = st.phase;
    wrap.classList.toggle('tr-solo-mode', solo(st));
    paint(root, st);
    ensureTrack(root, st);
    if (st.cv && st.bg) draw(root, st, performance.now(), 0);
    loop(root, st);
  }

  function paint(root, st) {
    const tb = root.querySelector('.tr-textbox');
    // у підсумку текст уже не потрібен — таблиця стає одразу під трасою (на Full HD — без прокрутки)
    if (tb) tb.hidden = !st.text || st.phase === 'done' || (solo(st) && (st.showPick || st.phase === 'pick'));
    paintWord(root, st);
    paintStats(root, st);
    paintBoard(root, st);
    paintPick(root, st);
    paintHelp(root, st);
    paintCountdown(root, st);
    const src = root.querySelector('.tr-src');
    if (src && st.showPick) src.textContent = '';
    const stop = root.querySelector('.tr-stop');
    if (stop) stop.hidden = !(solo(st) && st.ctx && st.ctx.mine && (st.phase === 'ready' || st.phase === 'go') && !st.finished);
  }

  function frame(root, ctx, f) {
    const st = state(root);
    if (!f || !f.p || st.phase !== 'go') return;
    const p = f.p;
    for (let i = 0; i * 2 < p.length && i < 10; i++) {
      const c = p[2 * i], s = p[2 * i + 1];
      if (c < 0) continue;
      if (i !== ctx.seat) {
        if (s === 1 && st.s[i] === 0) smoke(st, i);
        st.tx[i] = c;
      }
      st.s[i] = s;
    }
    if (!st.raf) loop(root, st);
  }

  function status(ctx) {
    const v = ctx.view || {};
    const st = ctx._trst;
    const soloGame = ctx.room && ctx.room.maxPlayers === 1;
    if (v.phase === 'pick') return v.noTexts ? 'Тексти відпочивають' : 'Обери довжину й тисни «Поїхали»';
    if (v.phase === 'ready') return 'Читай уривок — старт за три секунди';
    if (v.phase === 'go') {
      const me = ctx.mine ? (v.racers || []).find((r) => r.seat === ctx.seat) : null;
      if (me && me.fin != null) return me.flag ? 'Фініш, але не зараховано: ' + (REASON[me.flag] || me.flag)
        : soloGame ? 'Фініш!' : 'Фініш! ' + (me.place ? me.place + '-е місце' : '') + ' — чекаємо на решту';
      if (me && st && st.finished) return 'Фініш! Суддя дивиться журнал…';
      if (me) return 'Друкуй!';
      // глядач: лідер — з кадрів (вид приходить лише на подіях і відстає)
      const lead = st ? liveOrder(st).find((x) => !x.r.gone) : null;
      return lead ? 'Попереду ' + lead.r.nick + ' — ' + Math.floor(100 * Math.min(1, lead.c / Math.max(1, v.len))) + ' %' : 'Дивишся збоку';
    }
    if (v.phase === 'done' && soloGame) {
      const me = (v.racers || [])[0];
      if (!me) return '';
      if (me.fin == null) return 'Не доїхав — спробуй коротший текст';
      if (me.flag) return 'Не зараховано: ' + (REASON[me.flag] || me.flag);
      const t = titleOf(me.cpm || 0);
      return (v.me && v.me.isRecord ? 'Рекорд! ' : '') + me.cpm + ' зн/хв · ' + me.acc + ' % · ' + t[1] + ' ' + t[2];
    }
    return '';
  }

  function onKey(e, ctx) {
    const root = ctx._trroot;
    if (!root || !root._tr) return false;
    const st = root._tr;
    const v = ctx.view || {};
    // соло: Enter у виборі чи в підсумку — «Поїхали» / «Ще раз»
    if (e.key === 'Enter' && ctx.mine && ctx.room && ctx.room.maxPlayers === 1 && (v.phase === 'pick' || v.phase === 'done') && !v.noTexts) {
      soloGo(st, {});
      return true;
    }
    // друкований знак чи Backspace, а поле не у фокусі — ставимо фокус, і сама літера ляже вже туди
    if (canType(st) && (e.key === 'Backspace' || (e.key && e.key.length === 1))) focusInput(root, st, true);
    return false;
  }

  function unmount(root) {
    const st = root._tr;
    if (!st) return;
    if (st.raf) cancelAnimationFrame(st.raf);
    st.raf = 0;
    if (st.posTimer) clearTimeout(st.posTimer);
    clearTimeout(st.hintTimer);
    clearTimeout(st.shakeTimer);
    if (st.ro) st.ro.disconnect();
    if (st.onVis) document.removeEventListener('visibilitychange', st.onVis);
    if (st.ac) { try { st.ac.close(); } catch { /* уже */ } }
    st.ctx = null;
    root._tr = null;
  }

  const NEWS = {
    v: '2026-09-27',
    title: 'Нова гра: Клавоперегони',
    items: [
      '⌨️ Усі друкують той самий уривок української класики — чий трактор перший доїде до прапорця',
      '🔴 Помилка? Літера червоніє, трактор стоїть: Backspace — і далі',
      '📈 Наживо: знаків за хвилину, точність і твоє місце; наприкінці — таблиця й звання від «Равлика» до «Ракети»',
      '🏋️ Сам? У Соло є «Клавоперегони: тренування» з рекордом і таблицею',
      '🎮 На Steam Deck незручно — краще з клавіатури або STEAM+X',
    ],
  };

  function module(id) {
    return {
      id,
      icon: ICON,
      news: NEWS,
      mount(root, ctx) { ctx._trroot = root; mount(root, ctx); ctx._trst = state(root); },
      update(root, ctx) { ctx._trroot = root; ctx._trst = state(root); update(root, ctx); },
      frame,
      unmount,
      onKey,
      status,
      // Стік лишаємо навігації, Ⓑ — виходу: кільце стоїть на полі для друку, Ⓐ відкриває екранну клавіатуру пада.
      pad: { hint: 'Друкувати з пада незручно: {a} на тексті відкриває екранну клавіатуру пада, а зручніше — фізична клавіатура або STEAM+X' },
    };
  }

  HGames.register(module('typerace'));
  HGames.register(module('typerace-solo'));

  // Для заміру швидкодії з headless Chrome: середній draw() за кадр (див. «Як реалізовано» у spec).
  window.__typerace = {
    perf() {
      const out = [];
      document.querySelectorAll('.tr-wrap').forEach((w) => { const st = w.parentElement && w.parentElement._tr; if (st) out.push(Object.assign({ parts: st.partN }, st.perf)); });
      return out;
    },
    reset() { document.querySelectorAll('.tr-wrap').forEach((w) => { const st = w.parentElement && w.parentElement._tr; if (st) st.perf = { frames: 0, ms: 0, max: 0 }; }); },
    state() { const w = document.querySelector('.grbox .tr-wrap'); return w && w.parentElement && w.parentElement._tr; },
  };
})();
