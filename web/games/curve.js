/*
  Кривуля. Реалтайм: сервер тикає раз на 40 мс і шле кадр із головами, а слід ми домальовуємо самі —
  щокадру відрізок від попередньої голови до нової. Тому кадр і лишається кількасот байтів.

  Кадр   (Impl/Curve.cs): { t, r, heads: [{x,y,a,alive,gap[,fx]}|null], s: [очки], phase, startIn
           [, b: [[вид,x,y]…], k: скільки разів 🧹 стерло поле] [, ev: [[жертва, через кого (-1 стіна), лоб?]…]]
           [, pk: [[місце, вид бонуса]…]] }.
  Вид    (він же — правда після перемальовування): { width, height, round, target, phase, startIn,
           scores, heads, segments: [{ pts: [x,y,…], gaps: [номери точок][, fat: [номери]] }|null], winners,
           kills [, wrap] [, b, k] [, teams] [, note] }.
  Ввід:  Input('turn', { d: -1 | 0 | 1 }) — це утримання, а не крок: натиснув — шлемо ±1, відпустив — 0.

  Слід живе на власному канвасі: щокадру домальовуємо один відрізок, а не тисячу, і лише коли приходить
  подія 'room' (кінець раунду, новий раунд, новий глядач), перемальовуємо все з ламаних вида.

  Поле — за складом (24.09.2026): до чотирьох звичні 300×200, на п'ятьох-шістьох 360×240, на сімох-вісьмох
  420×280. Розмір каже вид (width/height), і канваси перебудовуються під нього.

  Прохід №3 (29.09.2026):
  - плавні голови: між кадрами голова їде далі сама (екстраполяція на ≤ 1 кадр, rAF лише поки йде раунд
    і поле видно) — без затримки, яку дала б інтерполяція «на кадр позаду»;
  - «хто кого»: стрічка на полі з подій кадру ev, наприкінці — 🕸 павук партії з kills вида;
  - повтор раунду: кадри раунду пишемо в пам'ять і між раундами за ~2 с програємо весь слід наново;
  - опції столу: поле-тор (wrap), бонуси (b/k/pk/fx), команди (teams).
*/
(() => {
  const THICK = 4;              // товщина сліду = два радіуси голови, як на сервері
  const FAT = 7;                // ⬛ товстий слід: радіус 3,5
  const OVER = 3;               // канвас сліду тримаємо втричі дрібнішим за одиницю поля — щоб не милити
  const SEATS = 8;
  // Вісім кольорів, як у класичній Achtung: п'ятий–восьмий — свої змінні з curve.css.
  const COLORS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--text', '#ecf1ea'],
    ['--cblue', '#6fb3e8'], ['--cpink', '#e88ac0'], ['--cviolet', '#a98bef'], ['--cred', '#ef5b5b']];
  const TURN = { ArrowLeft: -1, KeyA: -1, ArrowRight: 1, KeyD: 1 };
  const BOOM_MS = 650;
  const FEED_MS = 4000;         // скільки висить рядок «хто кого»
  const REPLAY_MS = 2200;       // повтор раунду — не довше за це (пауза між раундами — 3 с)
  const TEAMS = ['🐍 Вужі', '🦎 Ящірки'];
  // Бонуси: значок і кому — собі (зелений обідок), іншим (червоний), усім (синій).
  const BONUS = [['⚡', 'me'], ['⚡', 'them'], ['🐢', 'me'], ['🐢', 'them'], ['🔄', 'them'], ['🧹', 'all'], ['🚪', 'me'], ['⬛', 'them']];
  const BONUS_SAY = ['⚡ собі', '⚡ усім іншим', '🐢 собі', '🐢 усім іншим', '🔄 кермо навпаки іншим', '🧹 чисте поле', '🚪 крізь стіни', '⬛ товстий слід іншим'];
  const FX_INV = 4, FX_THROUGH = 8, FX_FAT = 16;
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M1.5 12.5c3.4 0 3.4-9 6.8-9s3.4 9 6.2 9" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round"/>'
    + '<circle cx="14.2" cy="12.5" r="1.8" fill="var(--ok)"/></svg>';

  /// Кольори з :root — у кеші. Кожен ctx.css — це getComputedStyle, а кадр на вісьмох питав його під сорок
  /// разів (по два на голову, око, кільце, підписи) 25 разів на секунду. Кеш скидає зміна теми (data-theme)
  /// і кожен новий вид (подія 'room' рідка).
  const pal = { sig: null, v: {} };
  function cssv(ctx, name, fallback) {
    const sig = document.documentElement.getAttribute('data-theme') || '';
    if (pal.sig !== sig) { pal.sig = sig; pal.v = {}; }
    const hit = pal.v[name];
    return hit !== undefined ? hit : (pal.v[name] = ctx.css(name, fallback));
  }
  const color = (ctx, i) => cssv(ctx, COLORS[i][0], COLORS[i][1]);
  /// Масштаб малювання: на звичайному моніторі (DPR 1) поле 300 одиниць розтягувалось на 460+ пікселів і
  /// слід милився сходинками. Малюємо вдвічі щільніше — на телефонах із DPR ≥ 2 це й так уже зроблено.
  const scale = () => ((window.devicePixelRatio || 1) >= 2 ? 1 : 2);

  /// Перемалювати, лише коли рядок справді інший. Порівнювати з el.innerHTML марно: браузер серіалізує його
  /// по-своєму (&#39; → ', лапки, style), тож «інше» виходило майже завжди — і DOM перебудовувався щокадру.
  const putHtml = (el, html) => { if (el._h !== html) { el._h = html; el.innerHTML = html; } };

  const nick = (ctx, i) => ctx.nickOf(i) || ctx.seatName(i);
  const teamsOf = (ctx) => (ctx.view && Array.isArray(ctx.view.teams) ? ctx.view.teams : null);

  function state(root, ctx) {
    if (root._curve) return root._curve;
    const st = {
      cv: null, tr: null, trc: null, W: 0, H: 0, K: scale(),
      prev: [],        // остання голова кожного місця — від неї малюємо наступний відрізок
      vel: [],         // зсув голови за останній кадр — щоб між кадрами вести її далі (плавні голови)
      r: -1, t: -1,    // номер раунду й тик останнього кадра: за ними видно, що раунд почався наново
      k: 0,            // скільки разів 🧹 уже стерло поле цього раунду
      keys: [],        // затиснуті клавіші повороту, остання головніша
      touch: 0,        // палець на кнопці або на половині поля
      sent: 0,         // що ми востаннє сказали серверу — щоб не слати те саме 25 разів на секунду
      view: undefined, // вид, з якого востаннє перемальовували поле
      booms: [],       // де щойно хтось урізався — спалах на пів секунди
      feed: [],        // «хто кого» на полі: [{ parts: [[текст, колір]…], at }]
      alive: [],       // хто був живий у попередньому кадрі
      rec: null,       // запис раунду для повтору: { r, ok, fr: [{ k, d: Float32Array }] }
      rp: null,        // повтор, що зараз іде: { at, dur, n, done, cv, c }
      phase: '',
      raf: 0,          // один rAF на все: плавні голови, спалахи, повтор
      up: null, blur: null,
    };
    root._curve = st;
    ctx._curve = st;   // onKey отримує лише ctx, а стан нам потрібен і там
    return st;
  }

  function trailCanvas(w, h) {
    const tr = document.createElement('canvas');
    tr.width = w * OVER;
    tr.height = h * OVER;
    const c = tr.getContext('2d');
    c.setTransform(OVER, 0, 0, OVER, 0, 0);
    c.lineWidth = THICK;
    c.lineCap = 'round';
    c.lineJoin = 'round';
    return { tr, c };
  }

  /// Канваси під розмір поля: головний (з DPR і нашим масштабом) і прихований канвас сліду.
  function size(root, st, w, h) {
    if (st.W === w && st.H === h && st.cv) return false;
    st.W = w;
    st.H = h;
    st.cv = HGames.ui.canvas(root, { w: w * st.K, h: h * st.K, cls: 'curveboard' + (w > 300 ? ' big' : '') });
    const t = trailCanvas(w, h);
    st.tr = t.tr;
    st.trc = t.c;
    st.prev = [];
    st.rp = null;
    return true;
  }

  // ---------- намір гравця ----------

  function send(ctx, st, d) {
    if (st.sent === d) return;
    st.sent = d;
    ctx.input('turn', { d });
  }

  /// Що зараз тримає гравець: палець важить більше за клавіші, бо його видно на екрані.
  function want(st) {
    return st.touch || (st.keys.length ? st.keys[st.keys.length - 1] : 0);
  }

  function press(ctx, st, d) {
    if (st.keys.indexOf(d) < 0) st.keys.push(d);
    send(ctx, st, want(st));
  }

  function release(ctx, st, d) {
    st.keys = st.keys.filter((k) => k !== d);
    send(ctx, st, want(st));
  }

  // ---------- малювання сліду ----------

  function wipe(c, cv) {
    c.save();
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.clearRect(0, 0, cv.width, cv.height);
    c.restore();
  }
  const clearTrail = (st) => wipe(st.trc, st.tr);

  /// Стрибок крізь край (тор чи 🚪): лінію через усе поле не тягнемо.
  const jumped = (st, a, b) => Math.abs(a.x - b.x) > st.W / 2 || Math.abs(a.y - b.y) > st.H / 2;

  /// Повне перемальовування з ламаних: єдине місце, де клієнт довіряє серверу, а не своїй пам'яті.
  function rebuild(st, ctx) {
    const v = ctx.view || {};
    const segs = v.segments || [];
    clearTrail(st);
    st.prev = [];
    st.vel = [];
    for (let i = 0; i < segs.length && i < SEATS; i++) {
      const s = segs[i];
      const pts = s && s.pts;
      if (!pts || pts.length < 2) continue;
      const n = pts.length >> 1;
      const gaps = new Set(s.gaps || []);
      const fat = new Set(s.fat || []);
      st.trc.strokeStyle = color(ctx, i);
      for (const wide of fat.size ? [false, true] : [false]) {
        st.trc.lineWidth = wide ? FAT : THICK;
        st.trc.beginPath();
        let pen = false;
        for (let k = 1; k < n; k++) {
          if (gaps.has(k) || fat.has(k) !== wide) { pen = false; continue; }
          if (!pen) { st.trc.moveTo(pts[2 * k - 2], pts[2 * k - 1]); pen = true; }
          st.trc.lineTo(pts[2 * k], pts[2 * k + 1]);
        }
        st.trc.stroke();
      }
      st.trc.lineWidth = THICK;
      st.prev[i] = { x: pts[2 * n - 2], y: pts[2 * n - 1] };
    }
    st.r = v.round == null ? -1 : v.round;
    st.t = -1;
    st.k = v.k || 0;
    st.alive = (v.heads || []).map((h) => !!(h && h.alive));
  }

  /// Відрізок сліду одного кадру на будь-якому канвасі сліду.
  function seg(c, col, a, b, fat) {
    c.strokeStyle = col;
    c.lineWidth = fat ? FAT : THICK;
    c.lineCap = 'round';
    c.beginPath();
    c.moveTo(a.x, a.y);
    c.lineTo(b.x, b.y);
    c.stroke();
  }

  /// Кадр: один відрізок на кожну живу голову. Дірка — просто не малюємо цей шматок.
  function grow(st, ctx, f) {
    const heads = f.heads || [];
    for (let i = 0; i < heads.length && i < SEATS; i++) {
      const h = heads[i];
      if (!h) { st.prev[i] = null; st.vel[i] = null; continue; }
      const p = st.prev[i];
      const jump = p && jumped(st, p, h);
      if (p && h.alive && !h.gap && !jump) seg(st.trc, color(ctx, i), p, h, h.fx & FX_FAT);
      st.vel[i] = p && h.alive && !jump ? { x: h.x - p.x, y: h.y - p.y } : null;
      st.prev[i] = h.alive ? { x: h.x, y: h.y } : null;
    }
  }

  // ---------- повтор раунду ----------

  /// Пишемо кожен кадр раунду: 3 числа на голову (x, y, прапорці). Хвилина вісьмох — ~150 КБ, і живе лише до
  /// наступного раунду. Хто підключився посеред раунду, записав не все — тому й повтору в нього не буде.
  function record(st, f) {
    const heads = f.heads || [];
    if (!st.rec || st.rec.r !== f.r) st.rec = { r: f.r, ok: f.t <= 2, fr: [], t: f.t };
    const rec = st.rec;
    // кадр смерті, що закінчив раунд, уже каже 'between' — його теж пишемо (новий тик), повтори того самого тика — ні
    if (f.phase === 'ready' || !rec.ok || f.t <= rec.t) return;
    if (f.t - rec.t > 3) { rec.ok = false; return; }   // пропустили кадри — повтор був би брехнею
    rec.t = f.t;
    const d = new Float32Array(SEATS * 3);
    for (let i = 0; i < SEATS; i++) {
      const h = heads[i];
      if (!h) continue;
      d[3 * i] = h.x;
      d[3 * i + 1] = h.y;
      d[3 * i + 2] = 8 | (h.alive ? 1 : 0) | (h.gap ? 2 : 0) | (h.fx & FX_FAT ? 4 : 0);
    }
    rec.fr.push({ k: f.k || 0, d });
  }

  function startReplay(st) {
    const rec = st.rec;
    if (!rec || !rec.ok || rec.fr.length < 25 || st.rp) return;
    const t = trailCanvas(st.W, st.H);
    // Коротку сутичку (до 2 с) крутимо як є, довгу — стискаємо в REPLAY_MS.
    st.rp = { at: performance.now(), dur: Math.min(REPLAY_MS, rec.fr.length * 40), n: 0, cv: t.tr, c: t.c, k: rec.fr[0].k };
    rec.ok = false;   // один раз на раунд
  }

  /// Домалювати повтор до «зараз». Повертає номер кадру запису, на якому ми (для голів), або -1.
  function stepReplay(st, ctx) {
    const rp = st.rp;
    const fr = st.rec && st.rec.fr;
    if (!rp || !fr) return -1;
    const k = Math.min(1, (performance.now() - rp.at) / rp.dur);
    const upto = Math.max(1, Math.round(k * (fr.length - 1)));
    for (let j = Math.max(1, rp.n); j <= upto; j++) {
      const a = fr[j - 1], b = fr[j];
      if (b.k !== rp.k) { wipe(rp.c, rp.cv); rp.k = b.k; continue; }
      for (let i = 0; i < SEATS; i++) {
        const fa = a.d[3 * i + 2], fb = b.d[3 * i + 2];
        if (!(fa & 1) || !(fb & 1) || (fb & 2)) continue;
        const pa = { x: a.d[3 * i], y: a.d[3 * i + 1] }, pb = { x: b.d[3 * i], y: b.d[3 * i + 1] };
        if (!jumped(st, pa, pb)) seg(rp.c, color(ctx, i), pa, pb, fb & 4);
      }
    }
    rp.n = upto + 1;
    if (k >= 1) { st.rp = null; return -1; }
    return upto;
  }

  // ---------- «хто кого» ----------

  function say(st, parts) {
    st.feed.push({ parts, at: performance.now() });
    if (st.feed.length > 4) st.feed.shift();
  }

  function noteEvents(st, ctx, f) {
    const teams = teamsOf(ctx);
    for (const e of f.ev || []) {
      const [v, k, head] = e;
      const cv = color(ctx, v);
      if (k < 0) say(st, [[nick(ctx, v), cv], [' ➜ стіна', null]]);
      else if (k === v) say(st, [[nick(ctx, v), cv], [' ➜ свій слід 🤦', null]]);
      else if (head) say(st, [[nick(ctx, v), cv], [' ⚔ ', null], [nick(ctx, k), color(ctx, k)], [' лоб у лоб', null]]);
      else {
        const own = teams && teams[k] === teams[v];
        say(st, [[nick(ctx, v), cv], [' ➜ слід ', null], [nick(ctx, k), color(ctx, k)], [own ? ' (свій!)' : '', null]]);
      }
    }
    for (const p of f.pk || []) {
      const [s, kind] = p;
      say(st, [[nick(ctx, s), color(ctx, s)], [': ' + (BONUS_SAY[kind] || '?'), null]]);
    }
  }

  function drawFeed(st, ctx, g, u) {
    const now = performance.now();
    st.feed = st.feed.filter((e) => now - e.at < FEED_MS);
    if (!st.feed.length) return;
    // На телефоні поле вужче за 300 px — шрифт тримаємо не дрібнішим за ~10 px на екрані.
    const ppu = (st.cv.el.clientWidth || st.W) / st.W;
    const fs = Math.max(7, Math.round(7.5 * u), Math.ceil(10 / ppu));
    g.font = '600 ' + fs + 'px system-ui, sans-serif';
    g.textAlign = 'left';
    g.textBaseline = 'top';
    g.lineWidth = 2.5;
    g.strokeStyle = cssv(ctx, '--bg2', '#16291f');
    const text = cssv(ctx, '--text', '#ecf1ea');
    st.feed.forEach((e, row) => {
      g.globalAlpha = Math.min(1, (FEED_MS - (now - e.at)) / 600);
      let x = 5;
      const y = 4 + row * (fs + 2);
      for (const [s, col] of e.parts) {
        if (!s) continue;
        g.strokeText(s, x, y);
        g.fillStyle = col || text;
        g.fillText(s, x, y);
        x += g.measureText(s).width;
      }
    });
    g.globalAlpha = 1;
  }

  // ---------- спалахи ----------

  /// Хто щойно врізався: живий у минулому кадрі, неживий у цьому (того самого раунду).
  function noteBooms(st, f) {
    const heads = f.heads || [];
    const now = performance.now();
    for (let i = 0; i < heads.length; i++) {
      const h = heads[i];
      if (h && !h.alive && st.alive[i] && f.phase !== 'ready') st.booms.push({ i, x: h.x, y: h.y, at: now });
    }
    st.alive = heads.map((h) => !!(h && h.alive));
    st.booms = st.booms.filter((b) => now - b.at < BOOM_MS);
  }

  function drawBooms(st, ctx, g) {
    const now = performance.now();
    let live = false;
    for (const b of st.booms) {
      const k = (now - b.at) / BOOM_MS;
      if (k < 0 || k >= 1) continue;
      live = true;
      g.globalAlpha = 1 - k;
      g.strokeStyle = color(ctx, b.i);
      g.lineWidth = 2;
      g.beginPath();
      g.arc(b.x, b.y, 3 + 14 * k, 0, Math.PI * 2);
      g.stroke();
      g.fillStyle = cssv(ctx, '--danger', '#e57373');
      g.beginPath();
      g.arc(b.x, b.y, 3.5 * (1 - k) + 1, 0, Math.PI * 2);
      g.fill();
      g.globalAlpha = 1;
    }
    return live;
  }

  // ---------- rAF: один на все ----------

  /// Потрібен лише, поки є що рухати між кадрами: голови в грі, спалах, повтор. Поле не видно — не крутимо.
  function kick(st) {
    if (st.raf || document.hidden || st.hidden) return;
    st.raf = requestAnimationFrame(() => {
      st.raf = 0;
      if (!st.cv || !st.cv.el.isConnected || !st.ctx) return;
      if (paint(st, st.ctx, st.lastF)) kick(st);
    });
  }

  /// Підпис над головою на відліку: хто де народився, а своя — «ти».
  function label(g, ctx, h, text, fill, big) {
    g.font = (big ? '700 10' : '600 8') + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'bottom';
    g.lineWidth = 3;
    g.strokeStyle = cssv(ctx, '--bg2', '#16291f');
    g.strokeText(text, h.x, h.y - 6);
    g.fillStyle = fill;
    g.fillText(text, h.x, h.y - 6);
  }

  function drawBonuses(ctx, g, items) {
    if (!items || !items.length) return;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.font = '6px system-ui, sans-serif';
    const rim = { me: cssv(ctx, '--ok', '#7bd389'), them: cssv(ctx, '--danger', '#e57373'), all: cssv(ctx, '--cblue', '#6fb3e8') };
    for (const [kind, x, y] of items) {
      const b = BONUS[kind] || ['?', 'all'];
      g.fillStyle = cssv(ctx, '--bg', '#0f1f18');
      g.strokeStyle = rim[b[1]];
      g.lineWidth = 1.3;
      g.beginPath();
      g.arc(x, y, 5, 0, Math.PI * 2);
      g.fill();
      g.stroke();
      g.fillStyle = cssv(ctx, '--text', '#ecf1ea');
      g.fillText(b[0], x, y + 0.4);
    }
  }

  function drawHead(st, ctx, g, i, x, y, a, me, fx) {
    g.strokeStyle = g.fillStyle = color(ctx, i);
    g.lineWidth = 1.4;
    g.beginPath();
    g.moveTo(x, y);
    g.lineTo(x + Math.cos(a) * 5, y + Math.sin(a) * 5);
    g.stroke();
    g.beginPath();
    g.arc(x, y, fx & FX_FAT ? 3.8 : 2.8, 0, Math.PI * 2);
    g.fill();
    // Око: без нього голова губиться на власному сліді того ж кольору.
    g.fillStyle = cssv(ctx, '--bg2', '#16291f');
    g.beginPath();
    g.arc(x, y, 1.1, 0, Math.PI * 2);
    g.fill();
    if (i === me) {
      // Своя голова — у білому кільці: на повному столі «де я?» — перше питання раунду.
      g.strokeStyle = cssv(ctx, '--text', '#ecf1ea');
      g.lineWidth = 0.9;
      g.beginPath();
      g.arc(x, y, 5, 0, Math.PI * 2);
      g.stroke();
    }
    if (fx & (FX_INV | FX_THROUGH)) {
      g.font = '6px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'bottom';
      g.fillText((fx & FX_INV ? '🔄' : '') + (fx & FX_THROUGH ? '🚪' : ''), x, y - 5);
    }
  }

  /// Малюємо поле. Повертає true, коли є що рухати й далі (тоді kick() попросить ще кадр).
  function paint(st, ctx, f) {
    const c = st.cv;
    if (!c) return false;
    st.lastF = f;
    st.ctx = ctx;
    // Картку не видно (лобі, інша вкладка сайту, схована вкладка браузера) — поле не малюємо, лише
    // запам'ятовуємо, що воно застаріло: слід на своєму канвасі кадри домальовують і далі. Раніше кожен
    // кадр перемальовував невидиме поле 25 разів на секунду.
    if (document.hidden || st.hidden) { st.stale = true; return false; }
    st.stale = false;
    const g = c.ctx;
    const W = st.W, H = st.H;
    const u = W / 300;   // шрифти ростуть разом із полем, щоб на великому полі не дрібніли
    const v = ctx.view || {};
    c.resize();
    g.save();
    g.scale(st.K, st.K);
    g.fillStyle = cssv(ctx, '--bg2', '#16291f');
    g.fillRect(0, 0, W, H);
    const ri = stepReplay(st, ctx);
    const replay = ri >= 0;
    g.drawImage(replay ? st.rp.cv : st.tr, 0, 0, W, H);
    if (v.wrap) {
      // Поле-тор: пунктир по краю — стін нема, край прохідний.
      g.strokeStyle = cssv(ctx, '--muted', '#8aa597');
      g.lineWidth = 0.8;
      g.setLineDash([3, 4]);
      g.strokeRect(0.5, 0.5, W - 1, H - 1);
      g.setLineDash([]);
    }

    const phase = f && f.phase;
    const heads = (f && f.heads) || [];
    const me = ctx.mine ? ctx.seat : null;
    let more = replay;
    if (!replay) {
      if (phase === 'play') drawBonuses(ctx, g, f.b);
      // Тінь поза грою кладемо під голови: на відліку вони мають світитись, а не тонути разом зі слідом.
      if (phase && phase !== 'play') {
        g.fillStyle = cssv(ctx, '--gshade', 'rgba(15, 31, 24, .62)');
        g.fillRect(0, 0, W, H);
      }
      // Плавні голови: між кадрами ведемо голову далі тим самим кроком (не довше за один кадр).
      const live = phase === 'play' && performance.now() - (st.frameAt || 0) < 120;
      const k = live ? Math.min(1, (performance.now() - st.frameAt) / 40) : 0;
      const teams = teamsOf(ctx);
      for (let i = 0; i < heads.length && i < SEATS; i++) {
        const h = heads[i];
        if (!h || !h.alive) continue;
        const vel = st.vel[i];
        let x = h.x, y = h.y;
        if (k > 0 && vel) {
          x += vel.x * k;
          y += vel.y * k;
          if (!h.gap) seg(g, color(ctx, i), h, { x, y }, h.fx & FX_FAT);
        }
        drawHead(st, ctx, g, i, x, y, (h.a || 0) * Math.PI / 180, me, h.fx || 0);
        if (phase === 'ready' && (f.startIn || 0) > 0) {
          let name = i === me ? 'ти' : nick(ctx, i);
          if (name.length > 12) name = name.slice(0, 11) + '…';
          if (teams && teams[i] >= 0) name = TEAMS[teams[i]].split(' ')[0] + ' ' + name;
          label(g, ctx, { x, y }, name, color(ctx, i), i === me);
        }
      }
      more = live;
    } else {
      // Повтор: голови там, де вони були в цю мить раунду.
      const d = st.rec.fr[ri].d, p = st.rec.fr[Math.max(0, ri - 1)].d;
      for (let i = 0; i < SEATS; i++) {
        if (!(d[3 * i + 2] & 1)) continue;
        const a = Math.atan2(d[3 * i + 1] - p[3 * i + 1], d[3 * i] - p[3 * i]);
        drawHead(st, ctx, g, i, d[3 * i], d[3 * i + 1], a, me, 0);
      }
      g.font = '700 ' + Math.round(9 * u) + 'px system-ui, sans-serif';
      g.textAlign = 'right';
      g.textBaseline = 'top';
      g.fillStyle = cssv(ctx, '--text', '#ecf1ea');
      g.globalAlpha = 0.8;
      g.fillText('⏪ повтор раунду', W - 6, 5);
      g.globalAlpha = 1;
    }
    if (drawBooms(st, ctx, g)) more = true;
    drawFeed(st, ctx, g, u);

    if (phase && phase !== 'play') {
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      if (phase === 'between' || phase === 'done') {
        // Хто взяв раунд (чи партію) — просто на полі, а не лише дрібним рядком під ним. На повторі — внизу,
        // щоб не затуляти сам повтор і стрічку «хто кого» вгорі.
        let who = heads.map((h, i) => (h && h.alive ? i : -1)).filter((i) => i >= 0);
        if (phase === 'done' && Array.isArray(v.winners)) who = v.winners;
        const teams = teamsOf(ctx);
        const team = teams && who.length && who.every((i) => teams[i] === teams[who[0]]) ? teams[who[0]] : -1;
        const names = team >= 0 ? TEAMS[team] : who.map((i) => nick(ctx, i)).join(', ');
        const cy = replay ? H - 14 * u : H / 2 - 30 * u;
        g.font = '700 ' + Math.round((replay ? 13 : 17) * u) + 'px system-ui, sans-serif';
        g.lineWidth = 3;
        g.strokeStyle = cssv(ctx, '--bg2', '#16291f');
        g.fillStyle = who.length === 1 ? color(ctx, who[0]) : cssv(ctx, '--text', '#ecf1ea');
        const line = who.length ? '🏆 ' + names : 'Усі вибули разом';
        if (replay) g.strokeText(line, W / 2, cy, W - 20);
        g.fillText(line, W / 2, cy, W - 20);
        if (!replay) {
          g.font = Math.round(10 * u) + 'px system-ui, sans-serif';
          g.fillStyle = cssv(ctx, '--text', '#ecf1ea');
          const verb = team >= 0 ? 'беруть' : 'бере';
          g.fillText(phase === 'done' ? 'партію зіграно' : (who.length ? verb + ' раунд' : 'раунд — нікому'), W / 2, H / 2 - 14 * u);
          const spider = phase === 'done' ? spiderLine(ctx, v) : '';
          if (spider) {
            g.font = Math.round(9 * u) + 'px system-ui, sans-serif';
            g.fillText(spider, W / 2, H / 2 + 14 * u, W - 20);
          }
        }
      }
      if ((phase === 'ready' || phase === 'between') && !replay) {
        g.fillStyle = cssv(ctx, '--text', '#ecf1ea');
        g.font = '700 ' + Math.round(40 * u) + 'px system-ui, sans-serif';
        // Стіл у лобі теж стоїть у фазі 'ready', але без відліку: велике біле «0» посеред поля
        // читалось би як відлік, що застряг.
        const left = Math.ceil((f.startIn || 0) / 1000);
        if (left > 0) g.fillText(String(left), W / 2, H / 2 + (phase === 'between' ? 14 * u : 0));
      }
    }
    g.restore();
    return more || st.feed.length > 0 && phase !== 'play' && phase !== 'between';
  }

  /// «🕸 Павук партії: Петро — у його слід урізались 7 разів» — хто найбільше «замкнув» інших.
  function spiderLine(ctx, v) {
    const kills = v.kills || [];
    let best = -1;
    for (let i = 0; i < SEATS; i++) if ((kills[i] || 0) > 0 && (best < 0 || kills[i] > kills[best])) best = i;
    if (best < 0) return '';
    const n = kills[best];
    const times = n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? 'рази' : 'разів';
    return '🕸 Павук партії: ' + nick(ctx, best) + ' — у слід урізались ' + n + ' ' + times;
  }

  /// Рахунок раундів: чіп на кожного — кружечок кольору, нік і очки; своє обведено. У командах — по командах.
  function score(root, ctx, f) {
    let el = root.querySelector(':scope > .gscore');
    if (!el) {
      el = document.createElement('div');
      el.className = 'gscore cscore';
      root.insertBefore(el, root.firstChild);
    }
    const s = (f && (f.s || f.scores)) || [];
    const heads = (f && f.heads) || [];
    const teams = teamsOf(ctx);
    const chip = (i, withScore) => {
      const out = f && f.phase === 'play' && heads[i] && !heads[i].alive;
      return '<span class="cchip c' + i + (i === ctx.seat ? ' me' : '') + (out ? ' out' : '') + '"><i></i>'
        + ctx.esc(ctx.nickOf(i)) + (withScore ? ' <b>' + (s[i] || 0) + '</b>' : '') + '</span>';
    };
    const parts = [];
    if (teams) {
      for (let t = 0; t < 2; t++) {
        const seats = [];
        for (let i = 0; i < SEATS; i++) if (teams[i] === t && ctx.nickOf(i)) seats.push(i);
        if (!seats.length) continue;
        parts.push('<span class="curve-team"><span class="curve-tname">' + TEAMS[t] + ' <b>' + (s[seats[0]] || 0) + '</b></span>'
          + seats.map((i) => chip(i, false)).join('') + '</span>');
      }
    } else {
      for (let i = 0; i < SEATS; i++) if (ctx.nickOf(i)) parts.push(chip(i, true));
    }
    const v = ctx.view || {};
    const html = parts.join('') + (v.target ? '<span class="muted small">до ' + v.target + '</span>' : '')
      + (v.note ? '<span class="muted small curve-note">' + ctx.esc(v.note) + '</span>' : '');
    putHtml(el, html);
  }

  /// Дві кнопки під палець: не тап, а утримання, тож слухаємо саме pointer-події.
  function pad(root, ctx, st) {
    let el = root.querySelector(':scope > .cpad');
    // Кнопки лише поки йде партія: у лобі й після кінця вони штовхали «Почати» / «Ану ще раз» під нижнє меню.
    if (!ctx.mine || !ctx.playing) { if (el) { el.remove(); st.touch = 0; } return; }
    if (el) {
      // канвас могли перебудувати під нове поле — кнопки лишаються під ним
      if (el.nextElementSibling) root.appendChild(el);
      return;
    }
    el = document.createElement('div');
    el.className = 'cpad';
    el.innerHTML = '<button type="button" data-d="-1" aria-label="ліворуч">◀</button>'
      + '<button type="button" data-d="1" aria-label="праворуч">▶</button>';
    el.addEventListener('pointerdown', (e) => {
      const b = e.target.closest('button');
      if (!b) return;
      e.preventDefault();
      st.touch = +b.dataset.d;
      send(ctx, st, want(st));
    });
    const off = (e) => {
      if (!st.touch) return;
      e.preventDefault();
      st.touch = 0;
      send(ctx, st, want(st));
    };
    el.addEventListener('pointerup', off);
    el.addEventListener('pointercancel', off);
    el.addEventListener('pointerleave', off);
    root.appendChild(el);
  }

  /// Телефон: на вісьмох шапка столу з місцями й чіпи очок штовхали поле вниз, і кнопки ◀ ▶ ховались під
  /// нижнім меню. Раз на партію (room.startedAt), коли вона пішла, прокручуємо так, щоб поле з кнопками
  /// стало між шапкою сайту й меню (і над «💬 Стіл»). Усе й так видно — не чіпаємо.
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

  /// Половини поля — те саме, але без прицілювання в кнопку. Гортати сторінку пальцем по полю
  /// заважаємо лише тоді, коли людина справді грає.
  function halves(st, ctx) {
    const el = st.cv && st.cv.el;
    if (!el) return;
    const live = !!(ctx.mine && ctx.playing && HGames.ui.coarse());
    if (el.classList.contains('hold') !== live) el.classList.toggle('hold', live);
    if (el._curveHold) return;
    el._curveHold = true;
    el.addEventListener('pointerdown', (e) => {
      if (!el.classList.contains('hold')) return;
      e.preventDefault();
      st.touch = e.clientX - el.getBoundingClientRect().left < el.clientWidth / 2 ? -1 : 1;
      send(ctx, st, want(st));
    });
    const off = (e) => {
      if (!st.touch) return;
      e.preventDefault();
      st.touch = 0;
      send(ctx, st, want(st));
    };
    el.addEventListener('pointerup', off);
    el.addEventListener('pointercancel', off);
    el.addEventListener('pointerleave', off);
  }

  HGames.register({
    id: 'curve',
    icon: ICON,
    seatNames: ['жовта', 'зелена', 'глиняна', 'біла', 'синя', 'рожева', 'фіалкова', 'червона'],
    seatClass: ['x', 'o', 'c', 'd', 'cb', 'cp', 'cv', 'cr'],
    pad: { dirs: 'x', hint: '{dpad} повертати ліворуч-праворуч' },
    news: {
      v: '2026-09-29',
      title: 'Кривуля: бонуси, тор, команди й повтор',
      items: [
        '⚡ Опція «Бонуси», як в Achtung: ⚡🐢 собі чи іншим, 🔄 кермо навпаки, 🧹 чисте поле, 🚪 крізь стіни, ⬛ товстий слід',
        '🍩 Опція «Стіни: нема» — поле-тор, вилетів справа — з\'явився зліва',
        '🐍 Команди 2×2, 3×3 і 4×4 — очко команді за кожного вибулого суперника',
        '💥 На полі видно, хто в чий слід урізався, а між раундами — ⏪ повтор усього раунду',
        '🧈 Голови їдуть плавно, без стрибків між кадрами',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      const v = ctx.view || {};
      size(root, st, v.width || 300, v.height || 200);
      // Каркас віддає модулю лише keydown, а утримання без keyup не буває — слухаємо самі.
      st.up = (e) => {
        const d = TURN[e.code];
        if (d) release(ctx, st, d);
      };
      st.blur = () => {
        st.keys = [];
        st.touch = 0;
        send(ctx, st, 0);
      };
      document.addEventListener('keyup', st.up);
      window.addEventListener('blur', st.blur);
      // Видно картку чи ні — каже IntersectionObserver (без читання розкладки щокадру); знову видно —
      // домальовуємо те, що пропустили.
      const wake = () => { if (st.stale && st.ctx && st.cv && paint(st, st.ctx, st.lastF)) kick(st); };
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
      const v = ctx.view || {};
      // Поле могло вирости (сіли вп'ятьох) або знову стати звичним — канваси за ним, і слід доведеться
      // перемалювати з вида.
      const resized = size(root, st, v.width || st.W || 300, v.height || st.H || 200);
      pad(root, ctx, st);
      halves(st, ctx);
      // update() приходить і на чужі новини лобі, і тоді ctx.view — той самий об'єкт, що був. Перемальовувати
      // з нього не можна: кадри вже намалювали слід далі, і ми б стерли все, що набігло після події 'room'.
      const fresh = ctx.view !== st.view || resized;
      if (fresh) {
        st.view = ctx.view;
        // Той самий раунд, а кадри йдуть без перерви — слід на своєму канвасі й так точний, точніший за
        // проріджену ламану вида (на вісьмох — лише 250 точок на кривулю). У компанії вид летить на кожну
        // смерть, і перемальовувати з нього все поле було і дарма (3–6 мс), і грубше: кути сліду зрізались.
        // Після реконекту, повернення до столу чи нового розміру кадрів щойно не було — тоді з вида.
        const live = !resized && v.round === st.r && performance.now() - st.frameAt < 300;
        if (!live) rebuild(st, ctx);
        if (st.phase === 'play' && (v.phase === 'between' || v.phase === 'done')) startReplay(st);
        st.phase = v.phase || st.phase;
      }
      const f = fresh ? v : (ctx.frame || v);
      score(root, ctx, f);
      fitPhone(root, st, ctx, '.cscore', '.cpad');
      if (paint(st, ctx, f)) kick(st);
      if (!ctx.playing) st.sent = 0;   // партія стала — наступне натискання має долетіти
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!st.cv || !f) return;
      // Новий раунд (або перезапуск партії) — стара мазанина на полі вже ні до чого.
      if (f.r !== st.r || f.t < st.t) {
        clearTrail(st);
        st.prev = [];
        st.vel = [];
        st.booms = [];
        st.alive = [];
        st.k = 0;
        st.rp = null;
        st.r = f.r;
        // Сервер на старті раунду забуває, хто що тримав, а браузер автоповтору вже не пришле:
        // нагадуємо йому те, що палець і досі тримає, інакше кривуля поїде прямо. Глядачеві
        // нагадувати нема чого — його ввід сервер усе одно викине.
        if (ctx.mine) {
          st.sent = null;
          send(ctx, st, want(st));
        }
      }
      // 🧹 стерло поле — стираємо й ми; слід далі росте від голів.
      if ((f.k || 0) !== st.k) {
        st.k = f.k || 0;
        clearTrail(st);
      }
      st.t = f.t;
      st.frameAt = performance.now();
      record(st, f);
      if (st.phase === 'play' && (f.phase === 'between' || f.phase === 'done')) startReplay(st);
      st.phase = f.phase;
      noteBooms(st, f);
      noteEvents(st, ctx, f);
      if (f.ev && ctx.mine && f.ev.some((e) => e[0] === ctx.seat) && navigator.vibrate
        && !(navigator.userActivation && !navigator.userActivation.hasBeenActive)) {
        try { navigator.vibrate(120); } catch (_) { /* ні то й ні */ }
      }
      grow(st, ctx, f);
      score(root, ctx, f);
      if (paint(st, ctx, f)) kick(st);
    },

    onKey(e, ctx) {
      const st = ctx._curve;
      const d = TURN[e.code];
      if (!d || !st || !ctx.mine || !ctx.playing) return false;
      press(ctx, st, d);
      return true;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      const f = ctx.frame && ctx.frame.phase ? ctx.frame : ctx.view;
      if (!f) return '';
      if (f.phase === 'ready') {
        const teams = teamsOf(ctx);
        if (teams && ctx.mine && teams[ctx.seat] >= 0) return 'Готуйсь… ти за ' + TEAMS[teams[ctx.seat]];
        return 'Готуйсь…';
      }
      if (f.phase === 'between') return 'Наступний раунд…';
      if (f.phase === 'done') return '';
      if (!ctx.mine) return 'Дивишся збоку';
      const me = (f.heads || [])[ctx.seat];
      if (me && !me.alive) return 'Аварія! Чекай на наступний раунд';
      if (me && me.fx & FX_INV) return '🔄 Кермо навпаки!';
      if (me && me.fx & FX_THROUGH) return '🚪 Можна крізь стіни!';
      return HGames.ui.coarse() ? 'Тримай ліворуч або праворуч' : '← → або A/D, тримати';
    },

    unmount(root, ctx) {
      const st = root._curve;
      if (!st) return;
      if (st.up) document.removeEventListener('keyup', st.up);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.vis) document.removeEventListener('visibilitychange', st.vis);
      if (st.io) st.io.disconnect();
      if (st.raf) cancelAnimationFrame(st.raf);
      st.raf = 0;
      st.rec = null;
      st.rp = null;
      root._curve = null;
      if (ctx) ctx._curve = null;
    },
  });
})();
