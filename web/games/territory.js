/*
  Земля (splix-подібна). Реалтайм: сервер тикає раз на 100 мс і шле кадр, у якому лежать ЛИШЕ зміни
  поля — тисячу з гаком клітинок по десять разів на секунду ніхто б не витримав. Тому модуль тримає своє
  поле: повні рядки бере з виду (він приходить на старті, після реконекту й на кінець раунду), а між ними
  накладає зміни з кадрів.

  Вид (Impl/Territory.cs): { width, height, t, phase, startIn, owner, trail, heads, area, timeLeft }
    owner / trail — рядки по символу на клітинку '0'..'6' ('0' — нічия, далі номер місця плюс один).
  Кадр:            { t, phase, startIn, heads, area, changes: [[клітинка, хто], …], trails: [[клітинка, хто], …], timeLeft }
    Тик, у якому змін більше, ніж саме поле (велика пожежа, замикання пів поля), приїжджає теж
    повними рядками owner/trail — тоді changes і trails порожні.
  Ввід:            Input('turn', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.

  Поле — за складом (24.09.2026): до чотирьох 40×30, на п'ятьох-шістьох 48×36. Канвас однаковий (480×360),
  клітинка на великому полі дрібніша. Перед раундом — три секунди «Готуйсь».
*/
(() => {
  const CW = 480, CH = 360;     // логічний розмір канваса за будь-якого поля
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.4" y="1.4" width="7" height="7" rx="1.2" fill="var(--accent)" opacity=".6"/>'
    + '<rect x="7.6" y="7.6" width="7" height="7" rx="1.2" fill="var(--ok)" opacity=".6"/>'
    + '<path d="M8.6 1.4h6v6" fill="none" stroke="var(--clay)" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  const SEATS = ['жовта', 'зелена', 'глиняна', 'блакитна', 'рожева', 'фіалкова'];
  // Четвертий–шостий кольори свої: у теми сайту трьох акцентів вистачає на все, крім гри на шістьох.
  const VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--gterr4', '#6aa9e9'],
    ['--gterr5', '#e88ac0'], ['--gterr6', '#a98bef']];
  const BOOM_MS = 700;
  /// На звичайному моніторі (DPR 1) малюємо вдвічі щільніше: поле на Full HD росте, і клітинки милились.
  const scale = () => ((window.devicePixelRatio || 1) >= 2 ? 1 : 2);

  /// Кольори з :root — у кеші: кожен st.css — це getComputedStyle, а кадр на шістьох питав його з два
  /// десятки разів (наділи, голови, смуга площ) десять разів на секунду. Кеш скидає зміна теми (data-theme)
  /// і кожен новий вид (подія 'room' рідка).
  const pal = { sig: null, v: {} };
  function cssv(st, name, fallback) {
    const sig = document.documentElement.getAttribute('data-theme') || '';
    if (pal.sig !== sig) { pal.sig = sig; pal.v = {}; }
    const hit = pal.v[name];
    return hit !== undefined ? hit : (pal.v[name] = st.css(name, fallback));
  }

  /// Перемалювати, лише коли рядок справді інший. Порівнювати з el.innerHTML марно: браузер серіалізує його
  /// по-своєму (&#39; → ', лапки, style), тож «інше» виходило майже завжди — і DOM перебудовувався щокадру.
  const putHtml = (el, html) => { if (el._h !== html) { el._h = html; el.innerHTML = html; } };

  function state(root, ctx) {
    if (!root._terr) {
      root._terr = {
        cv: null, css: ctx.css, seen: null, K: scale(),
        W: 0, H: 0, N: 0, PX: 12,
        own: new Uint8Array(0), trl: new Uint8Array(0),
        heads: [], area: [], left: 0, phase: 'ready', startIn: 0,
        booms: [], alive: [], raf: 0, ctx,
      };
    }
    root._terr.ctx = ctx;
    return root._terr;
  }

  /// Поле іншого розміру (сіли вп'ятьох або знову вчотирьох): нові масиви і нова клітинка.
  function size(st, w, h) {
    if (st.W === w && st.H === h) return;
    st.W = w;
    st.H = h;
    st.N = w * h;
    st.PX = CW / w;
    st.own = new Uint8Array(st.N);
    st.trl = new Uint8Array(st.N);
    st.seen = null;
  }

  /// Повні рядки поля: приходять і у виді, і у важкому кадрі. false — рядків нема, поле не чіпали.
  function rows(st, v) {
    if (!v || typeof v.owner !== 'string' || v.owner.length !== st.N) return false;
    for (let i = 0; i < st.N; i++) st.own[i] = v.owner.charCodeAt(i) - 48;
    const t = typeof v.trail === 'string' && v.trail.length === st.N ? v.trail : null;
    for (let i = 0; i < st.N; i++) st.trl[i] = t ? t.charCodeAt(i) - 48 : 0;
    return true;
  }

  /// Вид накладаємо рівно раз: core.js кличе update() на кожну подію 'rooms' із тим самим кешованим видом,
  /// і без цієї перевірки він відкочував би поле назад, затираючи свіжі кадри. Звіряємось із самим об'єктом,
  /// а не з його t: у нового раунду лічильник тиків знову нульовий, і після «Ще раз» його вид пройти МАЄ —
  /// інакше на полі лишилась би минула партія (стартові наділи їдуть тільки у виді, кадром їх нема).
  function fromView(st, v) {
    if (!v || st.seen === v) return false;
    if (v.width && v.height) size(st, v.width, v.height);
    if (!rows(st, v)) return false;
    st.seen = v;
    st.alive = [];          // новий раунд чи реконект: «хто щойно згорів» рахуємо з нуля
    take(st, v);
    return true;
  }

  /// Зміни з кадру: спершу поле, потім голови й площі.
  function fromFrame(st, f) {
    if (!f) return;
    const put = (list, map) => {
      for (let i = 0; i < (list || []).length; i++) {
        const p = list[i];
        if (p && p.length >= 2 && p[0] >= 0 && p[0] < st.N) map[p[0]] = p[1];
      }
    };
    if (!rows(st, f)) {              // важкий тик приїхав повними рядками, решта — парами
      put(f.changes, st.own);
      put(f.trails, st.trl);
    }
    noteBooms(st, f);
    take(st, f);
  }

  function take(st, f) {
    if (Array.isArray(f.heads)) st.heads = f.heads;
    if (Array.isArray(f.area)) st.area = f.area;
    if (typeof f.timeLeft === 'number') st.left = f.timeLeft;
    if (typeof f.phase === 'string') st.phase = f.phase;
    if (typeof f.startIn === 'number') st.startIn = f.startIn;
    st.alive = st.heads.map((h) => !!(h && h.on && h.alive));
  }

  /// Хто щойно згорів: був живий, а в цьому кадрі — ні. Спалах там, де стояла його голова.
  function noteBooms(st, f) {
    const heads = f.heads || [];
    const now = performance.now();
    for (let i = 0; i < heads.length; i++) {
      const was = st.heads[i];
      if (st.alive[i] && heads[i] && heads[i].on && !heads[i].alive && was && was.x >= 0)
        st.booms.push({ i, x: was.x, y: was.y, at: now });
    }
    st.booms = st.booms.filter((b) => now - b.at < BOOM_MS);
  }

  function colours(st) {
    return VARS.map((v) => cssv(st, v[0], v[1]));
  }

  function draw(st) {
    const c = st.cv;
    if (!c || !st.N) return;
    // Картку не видно (лобі, інша вкладка сайту чи браузера) — не малюємо, лише позначаємо, що поле
    // застаріло: зміни з кадрів у пам'яті накладаються й далі. Раніше невидиме поле малювалось щокадру.
    if (document.hidden || st.hidden) { st.stale = true; return; }
    st.stale = false;
    const g = c.ctx;
    const colour = colours(st);
    const W = st.W, N = st.N, PX = st.PX;
    const text = cssv(st, '--text', '#ecf1ea');
    const ctx = st.ctx;
    const me = ctx && ctx.mine ? ctx.seat : null;
    c.resize();
    g.save();
    g.scale(st.K, st.K);
    g.fillStyle = cssv(st, '--bg2', '#16291f');
    g.fillRect(0, 0, CW, CH);

    // Земля — блідо, слід — на повний колір: так одразу видно, що вже твоє, а що ще горить.
    for (let s = 0; s < VARS.length; s++) {
      g.globalAlpha = 0.4;
      g.fillStyle = colour[s];
      for (let i = 0; i < N; i++) if (st.own[i] === s + 1) g.fillRect((i % W) * PX, ((i / W) | 0) * PX, PX, PX);
      g.globalAlpha = 1;
      for (let i = 0; i < N; i++) if (st.trl[i] === s + 1) g.fillRect((i % W) * PX + 1, ((i / W) | 0) * PX + 1, PX - 2, PX - 2);
    }

    // «Тебе ріжуть»: твій слід у червоній рамці, поки чужа голова поруч, і рамка пульсує (товща-тонша
    // через кадр — кадри йдуть 10 разів на секунду, окремий rAF заради цього не заводимо).
    if (st.danger && me != null) {
      st.blink = !st.blink;
      g.strokeStyle = cssv(st, '--danger', '#e57373');
      g.lineWidth = st.blink ? 3 : 1.5;
      for (let i = 0; i < N; i++) if (st.trl[i] === me + 1) g.strokeRect((i % W) * PX + 1, ((i / W) | 0) * PX + 1, PX - 2, PX - 2);
    }

    const ready = st.phase === 'ready' && st.startIn > 0 && ctx && ctx.playing;
    if (ready) {
      g.fillStyle = cssv(st, '--gshade', 'rgba(15, 31, 24, .62)');
      g.fillRect(0, 0, CW, CH);
    }

    for (let s = 0; s < st.heads.length && s < VARS.length; s++) {
      const h = st.heads[s];
      if (!h || !h.on || !h.alive || h.x < 0) continue;
      g.fillStyle = colour[s];
      g.beginPath();
      g.roundRect(h.x * PX - 1, h.y * PX - 1, PX + 2, PX + 2, 4);
      g.fill();
      g.strokeStyle = text;
      g.lineWidth = 1.5;
      g.stroke();
      // Номер місця в голові — на шістьох кольори близькі, а цифру не сплутаєш.
      g.fillStyle = cssv(st, '--bg', '#0f1f18');
      g.font = '700 ' + Math.round(PX * 0.75) + 'px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(String(s + 1), h.x * PX + PX / 2, h.y * PX + PX / 2 + 0.5);
      if (s === me) {
        // Своя голова — у другому кільці: «де я?» на повному полі не питатимеш.
        g.strokeStyle = text;
        g.lineWidth = 1.2;
        g.beginPath();
        g.roundRect(h.x * PX - 4, h.y * PX - 4, PX + 8, PX + 8, 6);
        g.stroke();
      }
      if (ready) {
        const nick = s === me ? 'ти' : (ctx.nickOf(s) || ctx.seatName(s));
        g.font = (s === me ? '700 13px' : '600 11px') + ' system-ui, sans-serif';
        g.textBaseline = 'bottom';
        g.lineWidth = 3;
        g.strokeStyle = cssv(st, '--bg2', '#16291f');
        const label = nick.length > 12 ? nick.slice(0, 11) + '…' : nick;
        const ly = h.y * PX - 6;
        g.strokeText(label, h.x * PX + PX / 2, ly);
        g.fillStyle = colour[s];
        g.fillText(label, h.x * PX + PX / 2, ly);
      }
    }

    // Спалахи згорілих: кільце кольору місця розлітається й тане.
    const now = performance.now();
    let live = false;
    for (const b of st.booms) {
      const k = (now - b.at) / BOOM_MS;
      if (k < 0 || k >= 1) continue;
      live = true;
      g.globalAlpha = 1 - k;
      g.strokeStyle = colour[b.i] || text;
      g.lineWidth = 3;
      g.beginPath();
      g.arc(b.x * PX + PX / 2, b.y * PX + PX / 2, PX * (0.6 + 2.4 * k), 0, Math.PI * 2);
      g.stroke();
      g.fillStyle = cssv(st, '--danger', '#e57373');
      g.beginPath();
      g.arc(b.x * PX + PX / 2, b.y * PX + PX / 2, PX * 0.7 * (1 - k), 0, Math.PI * 2);
      g.fill();
      g.globalAlpha = 1;
    }
    if (live && !st.raf) st.raf = requestAnimationFrame(() => { st.raf = 0; if (st.cv && st.cv.el.isConnected) draw(st); });

    if (ready) {
      g.fillStyle = text;
      g.font = '700 64px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(String(Math.ceil(st.startIn / 1000)), CW / 2, CH / 2);
    }

    // Раунд зіграно — хто взяв поле, пишемо просто на ньому.
    const room = ctx && ctx.room;
    if (room && room.status === 'finished' && room.result && st.seen) {
      const who = room.result.winners || [];
      g.fillStyle = cssv(st, '--gshade', 'rgba(15, 31, 24, .62)');
      g.fillRect(0, 0, CW, CH);
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.font = '700 30px system-ui, sans-serif';
      g.fillStyle = who.length === 1 ? (colour[who[0]] || text) : text;
      const names = who.map((i) => ctx.nickOf(i) || ctx.seatName(i)).join(', ');
      g.fillText(who.length ? '🏆 ' + names : 'Нічия', CW / 2, CH / 2 - 12, CW - 30);
      g.font = '15px system-ui, sans-serif';
      g.fillStyle = text;
      const best = who.length ? Math.max(...who.map((i) => st.area[i] || 0)) : 0;
      const line = !who.length ? 'поле поділили порівну' : who.length === 1 ? 'тримає ' + best + '% поля' : 'у кожного по ' + best + '% поля';
      g.fillText(line, CW / 2, CH / 2 + 22);
    }
    g.restore();
  }

  /// Смуга площ: хто скільки поля тримає просто зараз.
  function bar(root, ctx, st) {
    let el = root.querySelector(':scope > .gterr');
    if (!el) {
      el = document.createElement('div');
      el.className = 'gterr gscore';
      root.insertBefore(el, root.firstChild);
    }
    const html = SEATS.map((name, s) => {
      const nick = ctx.nickOf(s);
      if (!nick) return '';
      const dead = st.heads[s] && st.heads[s].on && !st.heads[s].alive;
      const pct = st.area[s] == null ? 0 : st.area[s];
      return '<span class="gterr-p' + (dead ? ' out' : '') + (s === ctx.seat ? ' me' : '') + '"><i style="background:'
        + cssv(st, VARS[s][0], VARS[s][1]) + '">' + (s + 1) + '</i>' + ctx.esc(nick) + ' <b>' + pct + '%</b></span>';
    }).join('');
    putHtml(el, html);
  }

  const secs = (ms) => Math.max(0, Math.ceil((ms || 0) / 1000));

  /// «Тебе ріжуть» (прохід №3): чужа жива голова за ≤ 3 клітинки від твого сліду. Дивимось не весь слід,
  /// а квадрат 7×7 довкола кожної чужої голови — це ≤ 49 клітинок на суперника.
  const NEAR = 3;
  function danger(st, ctx) {
    if (!ctx || !ctx.mine || st.phase !== 'play') return false;
    const mine = ctx.seat + 1, W = st.W, H = st.H;
    for (let s = 0; s < st.heads.length; s++) {
      const h = st.heads[s];
      if (s === ctx.seat || !h || !h.on || !h.alive || h.x < 0) continue;
      for (let y = Math.max(0, h.y - NEAR); y <= Math.min(H - 1, h.y + NEAR); y++)
        for (let x = Math.max(0, h.x - NEAR); x <= Math.min(W - 1, h.x + NEAR); x++)
          if (st.trl[y * W + x] === mine) return true;
    }
    return false;
  }

  /// Небезпека почалась — телефон вібрує (не частіше ніж раз на 1,5 с; лише після першого дотику до сторінки).
  function warn(st, ctx) {
    const was = st.danger;
    st.danger = danger(st, ctx);
    if (ctx) ctx._terrDanger = st.danger;   // status() бачить лише ctx
    if (!st.danger || was || !navigator.vibrate) return;
    const now = performance.now();
    if (now - (st.buzzAt || 0) < 1500) return;
    if (navigator.userActivation && !navigator.userActivation.hasBeenActive) return;
    st.buzzAt = now;
    try { navigator.vibrate([90, 60, 90]); } catch (_) { /* ні то й ні */ }
  }

  /// Хрестовина під палець — своя, на pointerdown: хрестовина каркаса слухає click, а він приходить аж коли
  /// палець відпустили (+50–120 мс). Голова тут біжить клітинку за 100 мс, тож запізнілий поворот — це
  /// поворот не там. Вигляд той самий (клас .dpad каркаса, видно лише на сенсорному екрані).
  function dpad(root, ctx) {
    let el = root.querySelector(':scope > .dpad');
    // Хрестовина лише поки йде партія: у лобі й після кінця вона штовхала «Почати» / «Ану ще раз» під нижнє меню.
    if (!ctx.mine || !ctx.playing) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'dpad tdp';
      const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
      const aria = { 0: 'праворуч', 1: 'вниз', 2: 'ліворуч', 3: 'вгору' };
      el.innerHTML = [3, 2, 1, 0].map((d) => '<button type="button" data-dir="' + d + '" aria-label="' + aria[d] + '">' + label[d] + '</button>').join('');
      el.addEventListener('pointerdown', (e) => {
        const b = e.target.closest('button');
        if (!b || !el._ctx) return;
        e.preventDefault();
        el._ctx.input('turn', { dir: +b.dataset.dir });
      });
      root.appendChild(el);
    }
    el._ctx = ctx;   // колбек — з останнього update
  }

  /// Телефон: на шістьох шапка столу й смуга площ штовхали поле вниз, і хрестовина ховалась під нижнім
  /// меню. Раз на партію (room.startedAt), коли вона пішла, прокручуємо так, щоб поле з хрестовиною стало між
  /// шапкою сайту й меню (і над «💬 Стіл»). Усе й так видно — не чіпаємо.
  function fitPhone(root, st, ctx, hudSel, padSel) {
    if (!ctx.mine || !ctx.playing || !ctx.room || !HGames.ui.coarse()) return;
    const key = ctx.room.startedAt || '';
    if (st.fitFor === key) return;
    const hudEl = root.querySelector(':scope > ' + hudSel), padEl = root.querySelector(':scope > ' + padSel);
    if (!hudEl || !padEl) return;
    const a = hudEl.getBoundingClientRect(), b = padEl.getBoundingClientRect();
    if (!a.height || !b.height) return;              // картку зараз не видно — спробуємо на наступному виді
    st.fitFor = key;
    const head = document.querySelector('header');
    const top = (head ? head.getBoundingClientRect().bottom : 0) + 4;
    const tabs = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--tabs-h')) || 0;
    // кнопки мають стати над нижнім меню й над плаваючою кнопкою балачки столу («💬 Стіл»)
    const fab = document.querySelector('.tchat.drawer:not(.open) .tc-head');
    const fr = fab && fab.getBoundingClientRect();
    const limit = (fr && fr.height ? Math.min(fr.top, innerHeight - tabs) : innerHeight - tabs) - 6;
    const lo = b.bottom - limit, hi = a.top - top;   // на скільки прокрутити: не менше lo, не більше hi
    const dy = lo <= hi ? Math.min(Math.max(0, lo), hi) : lo;   // не влазить усе — кнопки важливіші за рядок гравців
    if (Math.abs(dy) < 2) return;
    const calm = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    window.scrollBy({ top: dy, behavior: calm ? 'auto' : 'smooth' });
  }

  HGames.register({
    id: 'territory',
    icon: ICON,
    seatNames: SEATS,
    // Четверте місце — блакитне, як його наділ (раніше чіп був сірим, а земля синьою)
    seatClass: ['x', 'o', 'c', 'tq', 'tr5', 'tr6'],
    pad: { dirs: true, hint: '{dpad} куди бігти' },
    news: {
      v: '2026-09-29',
      title: 'Земля: «тебе ріжуть» і довжина раунду',
      items: [
        '⚠ Чужа голова за три клітинки від твого сліду — слід блимає червоним, телефон вібрує: тікай додому!',
        '⏱ Опція «Раунд»: 60 с на двох, 90 як було, 150 для компанії — або «до 40 % поля», хто перший',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: CW * st.K, h: CH * st.K, cls: 'territoryboard' });
      const v = ctx.view || {};
      size(st, v.width || 40, v.height || 30);
      // Видно картку чи ні — каже IntersectionObserver (без читання розкладки щокадру); знову видно —
      // домальовуємо пропущене.
      const wake = () => { if (st.stale && st.cv) draw(st); };
      if (window.IntersectionObserver) {
        st.io = new IntersectionObserver((es) => { st.hidden = !es[es.length - 1].isIntersecting; if (!st.hidden) wake(); });
        st.io.observe(st.cv.el);
      }
      st.vis = () => { if (!document.hidden) wake(); };
      document.addEventListener('visibilitychange', st.vis);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      if (!st.cv) return;
      pal.sig = null;   // тема могла змінитись — кольори зберемо заново
      // хрестовина потрібна лише тому, хто грає: сів глядач за стіл — вона з'явиться тут
      dpad(root, ctx);
      fromView(st, ctx.view);
      bar(root, ctx, st);
      fitPhone(root, st, ctx, '.gterr', '.dpad');
      draw(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!st.cv || !f) return;
      fromFrame(st, f);
      warn(st, ctx);
      bar(root, ctx, st);
      draw(st);
    },

    onKey(e, ctx) {
      const dir = DIRS[e.code];
      if (dir === undefined || !ctx.mine || !ctx.playing) return false;
      // Автоповтор затиснутої стрілки (~30 на секунду) з'їдав квоту каркаса на ввід (30 Input/с), і
      // справжній поворот одразу після нього мовчки губився. Поворот — лише на справжній натиск.
      if (!e.repeat) ctx.input('turn', { dir });
      return true;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      // відлік живе в кадрах, а не у видах: вид приходить лише на старті й на кінець раунду
      const f = ctx.frame && ctx.frame.timeLeft != null ? ctx.frame : (ctx.view || {});
      if (f.phase === 'ready' && f.startIn > 0) return ctx.mine ? 'Готуйсь… можна вже повернути' : 'Готуйсь…';
      const goal = ctx.view && ctx.view.goal;
      const left = (goal ? 'до ' + goal + ' % поля · ' : '') + 'ще ' + secs(f.timeLeft) + ' с';
      const me = ctx.mine && f.heads ? f.heads[ctx.seat] : null;
      if (me && me.on && !me.alive) return 'Отакої, згоріло! Повертаєшся за ' + secs(me.respawnIn) + ' с · ' + left;
      if (ctx.mine && ctx._terrDanger) return '⚠ Тебе ріжуть — додому! · ' + left;
      const how = HGames.ui.coarse() ? 'Хрестовина — куди бігти' : 'Стрілки або WASD';
      return (ctx.mine ? how : 'Дивишся збоку') + ' · ' + left;
    },

    unmount(root) {
      const st = root._terr;
      if (st && st.raf) cancelAnimationFrame(st.raf);
      if (st && st.vis) document.removeEventListener('visibilitychange', st.vis);
      if (st && st.io) st.io.disconnect();
      root._terr = null;
    },
  });
})();
