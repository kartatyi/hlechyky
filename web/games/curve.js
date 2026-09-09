/*
  Кривуля. Реалтайм: сервер тикає раз на 40 мс і шле кадр із головами, а слід ми домальовуємо самі —
  щокадру відрізок від попередньої голови до нової. Тому кадр і лишається кількасот байтів.

  Кадр   (Impl/Curve.cs): { t, r, heads: [{x,y,a,alive,gap}|null], s: [очки], phase, startIn }.
  Вид    (він же — правда після перемальовування): { width, height, round, target, phase, startIn,
           scores, heads, segments: [{ pts: [x,y,…], gaps: [номери точок] }|null], winners }.
  Ввід:  Input('turn', { d: -1 | 0 | 1 }) — це утримання, а не крок: натиснув — шлемо ±1, відпустив — 0.

  Слід живе на власному канвасі: щокадру домальовуємо один відрізок, а не тисячу, і лише коли приходить
  подія 'room' (кінець раунду, новий раунд, новий глядач), перемальовуємо все з ламаних вида.
*/
(() => {
  const W = 300, H = 200;
  const THICK = 4;              // товщина сліду = два радіуси голови, як на сервері
  const OVER = 3;               // канвас сліду тримаємо втричі дрібнішим за одиницю поля — щоб не милити
  const COLORS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--text', '#ecf1ea']];
  const TURN = { ArrowLeft: -1, KeyA: -1, ArrowRight: 1, KeyD: 1 };
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M1.5 12.5c3.4 0 3.4-9 6.8-9s3.4 9 6.2 9" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round"/>'
    + '<circle cx="14.2" cy="12.5" r="1.8" fill="var(--ok)"/></svg>';

  const color = (ctx, i) => ctx.css(COLORS[i][0], COLORS[i][1]);

  function state(root, ctx) {
    if (root._curve) return root._curve;
    const tr = document.createElement('canvas');
    tr.width = W * OVER;
    tr.height = H * OVER;
    const trc = tr.getContext('2d');
    trc.setTransform(OVER, 0, 0, OVER, 0, 0);
    trc.lineWidth = THICK;
    trc.lineCap = 'round';
    trc.lineJoin = 'round';
    const st = {
      cv: null, tr, trc,
      prev: [],        // остання голова кожного місця — від неї малюємо наступний відрізок
      r: -1, t: -1,    // номер раунду й тик останнього кадра: за ними видно, що раунд почався наново
      keys: [],        // затиснуті клавіші повороту, остання головніша
      touch: 0,        // палець на кнопці або на половині поля
      sent: 0,         // що ми востаннє сказали серверу — щоб не слати те саме 25 разів на секунду
      view: undefined, // вид, з якого востаннє перемальовували поле
      up: null, blur: null,
    };
    root._curve = st;
    ctx._curve = st;   // onKey отримує лише ctx, а стан нам потрібен і там
    return st;
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
    for (let i = 0; i < segs.length; i++) {
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
  }

  /// Кадр: один відрізок на кожну живу голову. Дірка — просто не малюємо цей шматок.
  function grow(st, ctx, f) {
    const heads = f.heads || [];
    for (let i = 0; i < heads.length; i++) {
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

  function paint(st, ctx, f) {
    const c = st.cv;
    if (!c) return;
    const g = c.ctx;
    c.resize();
    g.fillStyle = ctx.css('--bg2', '#16291f');
    g.fillRect(0, 0, W, H);
    g.drawImage(st.tr, 0, 0, W, H);

    const heads = (f && f.heads) || [];
    for (let i = 0; i < heads.length; i++) {
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
      g.fillStyle = ctx.css('--bg2', '#16291f');
      g.beginPath();
      g.arc(h.x, h.y, 1.1, 0, Math.PI * 2);
      g.fill();
    }

    const phase = f && f.phase;
    if (phase && phase !== 'play') {
      g.fillStyle = ctx.css('--gshade', 'rgba(15, 31, 24, .62)');
      g.fillRect(0, 0, W, H);
      if (phase === 'ready' || phase === 'between') {
        g.fillStyle = ctx.css('--text', '#ecf1ea');
        g.font = '700 40px system-ui, sans-serif';
        g.textAlign = 'center';
        g.textBaseline = 'middle';
        g.fillText(String(Math.ceil((f.startIn || 0) / 1000)), W / 2, H / 2);
      }
    }
  }

  /// Рахунок раундів; підписи — кольорами місць, як чіпи в шапці картки.
  function score(root, ctx, f) {
    let el = root.querySelector(':scope > .gscore');
    if (!el) {
      el = document.createElement('div');
      el.className = 'gscore cscore';
      root.insertBefore(el, root.firstChild);
    }
    const s = (f && (f.s || f.scores)) || [];
    const parts = [];
    for (let i = 0; i < 4; i++) {
      if (!ctx.nickOf(i)) continue;
      parts.push('<b class="c' + i + '">' + (s[i] || 0) + '</b>');
    }
    const target = ctx.view && ctx.view.target;
    const html = parts.join('<i>:</i>') + (target ? '<span class="muted small">до ' + target + '</span>' : '');
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  /// Дві кнопки під палець: не тап, а утримання, тож слухаємо саме pointer-події.
  function pad(root, ctx, st) {
    let el = root.querySelector(':scope > .cpad');
    if (!ctx.mine) { if (el) el.remove(); return; }
    if (el) return;
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
    seatNames: ['жовта', 'зелена', 'глиняна', 'біла'],
    seatClass: ['x', 'o', 'c', 'd'],

    mount(root, ctx) {
      const st = state(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: W, h: H, cls: 'curveboard' });
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
    },

    update(root, ctx) {
      const st = state(root, ctx);
      if (!st.cv) return;
      pad(root, ctx, st);
      halves(st, ctx);
      // update() приходить і на чужі новини лобі, і тоді ctx.view — той самий об'єкт, що був. Перемальовувати
      // з нього не можна: кадри вже намалювали слід далі, і ми б стерли все, що набігло після події 'room'.
      const fresh = ctx.view !== st.view;
      if (fresh) { st.view = ctx.view; rebuild(st, ctx); }
      const f = fresh ? (ctx.view || {}) : (ctx.frame || ctx.view || {});
      score(root, ctx, f);
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
        st.r = f.r;
        // Сервер на старті раунду забуває, хто що тримав, а браузер автоповтору вже не пришле:
        // нагадуємо йому те, що палець і досі тримає, інакше кривуля поїде прямо.
        st.sent = null;
        send(ctx, st, want(st));
      }
      st.t = f.t;
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
      if (me && !me.alive) return 'Вибув — чекай на наступний раунд';
      return HGames.ui.coarse() ? 'Тримай ліворуч або праворуч' : '← → або A/D, тримати';
    },

    unmount(root, ctx) {
      const st = root._curve;
      if (!st) return;
      if (st.up) document.removeEventListener('keyup', st.up);
      if (st.blur) window.removeEventListener('blur', st.blur);
      root._curve = null;
      if (ctx) ctx._curve = null;
    },
  });
})();
