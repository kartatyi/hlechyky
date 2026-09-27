/*
  Кривуля. Реалтайм: сервер тикає раз на 40 мс і шле кадр із головами, а слід ми домальовуємо самі —
  щокадру відрізок від попередньої голови до нової. Тому кадр і лишається кількасот байтів.

  Кадр   (Impl/Curve.cs): { t, r, heads: [{x,y,a,alive,gap}|null], s: [очки], phase, startIn }.
  Вид    (він же — правда після перемальовування): { width, height, round, target, phase, startIn,
           scores, heads, segments: [{ pts: [x,y,…], gaps: [номери точок] }|null], winners }.
  Ввід:  Input('turn', { d: -1 | 0 | 1 }) — це утримання, а не крок: натиснув — шлемо ±1, відпустив — 0.

  Слід живе на власному канвасі: щокадру домальовуємо один відрізок, а не тисячу, і лише коли приходить
  подія 'room' (кінець раунду, новий раунд, новий глядач), перемальовуємо все з ламаних вида.

  Поле — за складом (24.09.2026): до чотирьох звичні 300×200, на п'ятьох-шістьох 360×240, на сімох-вісьмох
  420×280. Розмір каже вид (width/height), і канваси перебудовуються під нього.
*/
(() => {
  const THICK = 4;              // товщина сліду = два радіуси голови, як на сервері
  const OVER = 3;               // канвас сліду тримаємо втричі дрібнішим за одиницю поля — щоб не милити
  const SEATS = 8;
  // Вісім кольорів, як у класичній Achtung: п'ятий–восьмий — свої змінні з curve.css.
  const COLORS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--text', '#ecf1ea'],
    ['--cblue', '#6fb3e8'], ['--cpink', '#e88ac0'], ['--cviolet', '#a98bef'], ['--cred', '#ef5b5b']];
  const TURN = { ArrowLeft: -1, KeyA: -1, ArrowRight: 1, KeyD: 1 };
  const BOOM_MS = 650;
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

  function state(root, ctx) {
    if (root._curve) return root._curve;
    const st = {
      cv: null, tr: null, trc: null, W: 0, H: 0, K: scale(),
      prev: [],        // остання голова кожного місця — від неї малюємо наступний відрізок
      r: -1, t: -1,    // номер раунду й тик останнього кадра: за ними видно, що раунд почався наново
      keys: [],        // затиснуті клавіші повороту, остання головніша
      touch: 0,        // палець на кнопці або на половині поля
      sent: 0,         // що ми востаннє сказали серверу — щоб не слати те саме 25 разів на секунду
      view: undefined, // вид, з якого востаннє перемальовували поле
      booms: [],       // де щойно хтось урізався — спалах на пів секунди
      alive: [],       // хто був живий у попередньому кадрі
      raf: 0,          // домальовування спалахів між кадрами (після кінця раунду кадрів уже нема)
      up: null, blur: null,
    };
    root._curve = st;
    ctx._curve = st;   // onKey отримує лише ctx, а стан нам потрібен і там
    return st;
  }

  /// Канваси під розмір поля: головний (з DPR і нашим масштабом) і прихований канвас сліду.
  function size(root, st, w, h) {
    if (st.W === w && st.H === h && st.cv) return false;
    st.W = w;
    st.H = h;
    st.cv = HGames.ui.canvas(root, { w: w * st.K, h: h * st.K, cls: 'curveboard' + (w > 300 ? ' big' : '') });
    const tr = document.createElement('canvas');
    tr.width = w * OVER;
    tr.height = h * OVER;
    const trc = tr.getContext('2d');
    trc.setTransform(OVER, 0, 0, OVER, 0, 0);
    trc.lineWidth = THICK;
    trc.lineCap = 'round';
    trc.lineJoin = 'round';
    st.tr = tr;
    st.trc = trc;
    st.prev = [];
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

  // ---------- малювання ----------

  function clearTrail(st) {
    st.trc.save();
    st.trc.setTransform(1, 0, 0, 1, 0, 0);
    st.trc.clearRect(0, 0, st.tr.width, st.tr.height);
    st.trc.restore();
  }

  /// Повне перемальовування з ламаних: єдине місце, де клієнт довіряє серверу, а не своїй пам'яті.
  function rebuild(st, ctx) {
    const v = ctx.view || {};
    const segs = v.segments || [];
    clearTrail(st);
    st.prev = [];
    for (let i = 0; i < segs.length && i < SEATS; i++) {
      const s = segs[i];
      const pts = s && s.pts;
      if (!pts || pts.length < 2) continue;
      const n = pts.length >> 1;
      const gaps = new Set(s.gaps || []);
      st.trc.strokeStyle = color(ctx, i);
      st.trc.beginPath();
      let pen = false;
      for (let k = 1; k < n; k++) {
        if (gaps.has(k)) { pen = false; continue; }
        if (!pen) { st.trc.moveTo(pts[2 * k - 2], pts[2 * k - 1]); pen = true; }
        st.trc.lineTo(pts[2 * k], pts[2 * k + 1]);
      }
      st.trc.stroke();
      st.prev[i] = { x: pts[2 * n - 2], y: pts[2 * n - 1] };
    }
    st.r = v.round == null ? -1 : v.round;
    st.t = -1;
    st.alive = (v.heads || []).map((h) => !!(h && h.alive));
  }

  /// Кадр: один відрізок на кожну живу голову. Дірка — просто не малюємо цей шматок.
  function grow(st, ctx, f) {
    const heads = f.heads || [];
    for (let i = 0; i < heads.length && i < SEATS; i++) {
      const h = heads[i];
      if (!h) { st.prev[i] = null; continue; }
      const p = st.prev[i];
      if (p && h.alive && !h.gap) {
        st.trc.strokeStyle = color(ctx, i);
        st.trc.beginPath();
        st.trc.moveTo(p.x, p.y);
        st.trc.lineTo(h.x, h.y);
        st.trc.stroke();
      }
      st.prev[i] = h.alive ? { x: h.x, y: h.y } : null;
    }
  }

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
    // Кадри йдуть 25 разів на секунду лише в грі; спалах останнього в раунді дограємо самі.
    if (live && !st.raf) {
      st.raf = requestAnimationFrame(() => {
        st.raf = 0;
        if (st.cv && st.cv.el.isConnected && st.ctx) paint(st, st.ctx, st.lastF);
      });
    }
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

  function paint(st, ctx, f) {
    const c = st.cv;
    if (!c) return;
    st.lastF = f;
    st.ctx = ctx;
    // Картку не видно (лобі, інша вкладка сайту, схована вкладка браузера) — поле не малюємо, лише
    // запам'ятовуємо, що воно застаріло: слід на своєму канвасі кадри домальовують і далі. Раніше кожен
    // кадр перемальовував невидиме поле 25 разів на секунду.
    if (document.hidden || st.hidden) { st.stale = true; return; }
    st.stale = false;
    const g = c.ctx;
    const W = st.W, H = st.H;
    c.resize();
    g.save();
    g.scale(st.K, st.K);
    g.fillStyle = cssv(ctx, '--bg2', '#16291f');
    g.fillRect(0, 0, W, H);
    g.drawImage(st.tr, 0, 0, W, H);

    const phase = f && f.phase;
    const heads = (f && f.heads) || [];
    const me = ctx.mine ? ctx.seat : null;
    // Тінь поза грою кладемо під голови: на відліку вони мають світитись, а не тонути разом зі слідом.
    if (phase && phase !== 'play') {
      g.fillStyle = cssv(ctx, '--gshade', 'rgba(15, 31, 24, .62)');
      g.fillRect(0, 0, W, H);
    }
    for (let i = 0; i < heads.length && i < SEATS; i++) {
      const h = heads[i];
      if (!h || !h.alive) continue;
      const a = (h.a || 0) * Math.PI / 180;
      g.strokeStyle = g.fillStyle = color(ctx, i);
      g.lineWidth = 1.4;
      g.beginPath();
      g.moveTo(h.x, h.y);
      g.lineTo(h.x + Math.cos(a) * 5, h.y + Math.sin(a) * 5);
      g.stroke();
      g.beginPath();
      g.arc(h.x, h.y, 2.8, 0, Math.PI * 2);
      g.fill();
      // Око: без нього голова губиться на власному сліді того ж кольору.
      g.fillStyle = cssv(ctx, '--bg2', '#16291f');
      g.beginPath();
      g.arc(h.x, h.y, 1.1, 0, Math.PI * 2);
      g.fill();
      if (i === me) {
        // Своя голова — у білому кільці: на повному столі «де я?» — перше питання раунду.
        g.strokeStyle = cssv(ctx, '--text', '#ecf1ea');
        g.lineWidth = 0.9;
        g.beginPath();
        g.arc(h.x, h.y, 5, 0, Math.PI * 2);
        g.stroke();
      }
      if (phase === 'ready' && (f.startIn || 0) > 0) {
        const nick = i === me ? 'ти' : (ctx.nickOf(i) || ctx.seatName(i));
        label(g, ctx, h, nick.length > 12 ? nick.slice(0, 11) + '…' : nick, color(ctx, i), i === me);
      }
    }
    drawBooms(st, ctx, g);

    if (phase && phase !== 'play') {
      const u = W / 300;   // шрифти ростуть разом із полем, щоб на великому полі не дрібніли
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      if (phase === 'between' || phase === 'done') {
        // Хто взяв раунд (чи партію) — просто на полі, а не лише дрібним рядком під ним.
        let who = heads.map((h, i) => (h && h.alive ? i : -1)).filter((i) => i >= 0);
        if (phase === 'done' && ctx.view && Array.isArray(ctx.view.winners)) who = ctx.view.winners;
        const names = who.map((i) => ctx.nickOf(i) || ctx.seatName(i)).join(', ');
        g.font = '700 ' + Math.round(17 * u) + 'px system-ui, sans-serif';
        g.fillStyle = who.length === 1 ? color(ctx, who[0]) : cssv(ctx, '--text', '#ecf1ea');
        g.fillText(who.length ? '🏆 ' + names : 'Усі вибули разом', W / 2, H / 2 - 30 * u, W - 20);
        g.font = Math.round(10 * u) + 'px system-ui, sans-serif';
        g.fillStyle = cssv(ctx, '--text', '#ecf1ea');
        g.fillText(phase === 'done' ? 'партію зіграно' : (who.length ? 'бере раунд' : 'раунд — нікому'), W / 2, H / 2 - 14 * u);
      }
      if (phase === 'ready' || phase === 'between') {
        g.fillStyle = cssv(ctx, '--text', '#ecf1ea');
        g.font = '700 ' + Math.round(40 * u) + 'px system-ui, sans-serif';
        // Стіл у лобі теж стоїть у фазі 'ready', але без відліку: велике біле «0» посеред поля
        // читалось би як відлік, що застряг.
        const left = Math.ceil((f.startIn || 0) / 1000);
        if (left > 0) g.fillText(String(left), W / 2, H / 2 + (phase === 'between' ? 14 * u : 0));
      }
    }
    g.restore();
  }

  /// Рахунок раундів: чіп на кожного — кружечок кольору, нік і очки; своє обведено.
  function score(root, ctx, f) {
    let el = root.querySelector(':scope > .gscore');
    if (!el) {
      el = document.createElement('div');
      el.className = 'gscore cscore';
      root.insertBefore(el, root.firstChild);
    }
    const s = (f && (f.s || f.scores)) || [];
    const heads = (f && f.heads) || [];
    const parts = [];
    for (let i = 0; i < SEATS; i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      const out = f && f.phase === 'play' && heads[i] && !heads[i].alive;
      parts.push('<span class="cchip c' + i + (i === ctx.seat ? ' me' : '') + (out ? ' out' : '') + '"><i></i>'
        + ctx.esc(nick) + ' <b>' + (s[i] || 0) + '</b></span>');
    }
    const target = ctx.view && ctx.view.target;
    const html = parts.join('') + (target ? '<span class="muted small">до ' + target + '</span>' : '');
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
      v: '2026-09-28',
      title: 'Кривуля: поле більше, слід рівніший',
      items: [
        '🔍 На ноутбуці й Деці поле більше — і влазить без прокрутки навіть на вісьмох',
        '📱 На телефоні з початком партії поле й кнопки ◀ ▶ стають в екран разом, над «💬 Стіл»',
        '〰 Коли хтось урізається, слід решти більше не зрізає кути',
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
      const wake = () => { if (st.stale && st.ctx && st.cv) paint(st, st.ctx, st.lastF); };
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
        const v = ctx.view || {};
        const live = !resized && v.round === st.r && performance.now() - st.frameAt < 300;
        if (!live) rebuild(st, ctx);
      }
      const f = fresh ? (ctx.view || {}) : (ctx.frame || ctx.view || {});
      score(root, ctx, f);
      fitPhone(root, st, ctx, '.cscore', '.cpad');
      paint(st, ctx, f);
      if (!ctx.playing) st.sent = 0;   // партія стала — наступне натискання має долетіти
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!st.cv || !f) return;
      // Новий раунд (або перезапуск партії) — стара мазанина на полі вже ні до чого.
      if (f.r !== st.r || f.t < st.t) {
        clearTrail(st);
        st.prev = [];
        st.booms = [];
        st.alive = [];
        st.r = f.r;
        // Сервер на старті раунду забуває, хто що тримав, а браузер автоповтору вже не пришле:
        // нагадуємо йому те, що палець і досі тримає, інакше кривуля поїде прямо. Глядачеві
        // нагадувати нема чого — його ввід сервер усе одно викине.
        if (ctx.mine) {
          st.sent = null;
          send(ctx, st, want(st));
        }
      }
      st.t = f.t;
      st.frameAt = performance.now();
      noteBooms(st, f);
      grow(st, ctx, f);
      score(root, ctx, f);
      paint(st, ctx, f);
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
      if (f.phase === 'ready') return 'Готуйсь…';
      if (f.phase === 'between') return 'Наступний раунд…';
      if (f.phase === 'done') return '';
      if (!ctx.mine) return 'Дивишся збоку';
      const me = (f.heads || [])[ctx.seat];
      if (me && !me.alive) return 'Аварія! Чекай на наступний раунд';
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
      root._curve = null;
      if (ctx) ctx._curve = null;
    },
  });
})();
