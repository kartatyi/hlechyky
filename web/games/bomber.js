/*
  Бомбер. Реалтайм: сервер тикає раз на 60 мс і шле кадр, ми його малюємо й трохи згладжуємо.

  Кадр (Impl/Bomber.cs):
    { t, p: [{x, y, alive, bombs, range, boots}], b: [{x, y, fuse}], f: int[] (клітинки полум'я),
      boxes: int[], pw: [{x, y, kind}], wins: int[], phase, startIn }
  Координати бомберів — у дванадцятих частках клітинки (SUB), бомб і бонусів — у цілих клітинках.
  Стіни не міняються за партію, тому їх шле лише вид: { width, height, sub, walls, need, round, ... }.

  Ввід: Input('move', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору, -1 стоп (напрямок «тримають»,
  тому на відпускання клавіші шлемо -1); Input('bomb') — покласти бомбу під себе.
*/
(() => {
  const W = 15, H = 13, PX = 26, SUB = 12, TICK_MS = 60;
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="7" cy="10" r="5" fill="var(--accent)"/>'
    + '<path d="M10.6 5.4 12.6 3.4" stroke="var(--clay)" stroke-width="1.8" stroke-linecap="round" fill="none"/>'
    + '<circle cx="13.4" cy="2.6" r="1.7" fill="var(--ok)"/></svg>';

  // Розкладконезалежно читаємо e.code, але терпимо й e.key: синтетичний keydown із панелі браузера
  // приходить без code, а на українській розкладці WASD — це ЦФІВ.
  const DIRS = {
    ArrowRight: 0, KeyD: 0, d: 0, в: 0,
    ArrowDown: 1, KeyS: 1, s: 1, і: 1,
    ArrowLeft: 2, KeyA: 2, a: 2, ф: 2,
    ArrowUp: 3, KeyW: 3, w: 3, ц: 3,
  };
  const dirOf = (e) => {
    const byCode = DIRS[e.code];
    if (byCode !== undefined) return byCode;
    return DIRS[String(e.key || '').toLowerCase()];
  };
  const isBomb = (e) => e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar';

  const SEATS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5']];
  const GLYPH = { range: '🔥', bomb: '💣', boots: '👟' };

  const at = (cell) => [(cell % W) * PX, Math.floor(cell / W) * PX];
  const lerp = (a, b, t) => a + (b - a) * t;

  // ---------------------------------------------------------------------------------------------
  // Малювання
  // ---------------------------------------------------------------------------------------------

  function box(g, x, y, pad, r, fill) {
    g.fillStyle = fill;
    g.beginPath();
    g.roundRect(x + pad, y + pad, PX - pad * 2, PX - pad * 2, r);
    g.fill();
  }

  function drawWalls(st, g) {
    const wall = st.css('--panel3', '#2b4c3c');
    const edge = st.css('--line', '#2f4d3d');
    for (const cell of st.walls) {
      const [x, y] = at(cell);
      box(g, x, y, 0, 3, wall);
      g.fillStyle = edge;
      g.fillRect(x, y, PX, 3);
    }
  }

  function drawBoxes(st, g, cells) {
    const clay = st.css('--clay', '#c5763a');
    const dark = st.css('--bbox', '#8f5527');
    for (const cell of cells || []) {
      const [x, y] = at(cell);
      box(g, x, y, 2, 4, clay);
      // дві дошки навхрест — щоб ящик читався ящиком, а не просто плиткою
      g.strokeStyle = dark;
      g.lineWidth = 2;
      g.beginPath();
      g.moveTo(x + 4, y + PX / 2);
      g.lineTo(x + PX - 4, y + PX / 2);
      g.moveTo(x + PX / 2, y + 4);
      g.lineTo(x + PX / 2, y + PX - 4);
      g.stroke();
    }
  }

  function drawDrops(st, g, drops) {
    for (const d of drops || []) {
      const x = d.x * PX, y = d.y * PX;
      box(g, x, y, 4, 6, st.css('--panel', '#1c3328'));
      g.font = '13px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(GLYPH[d.kind] || '?', x + PX / 2, y + PX / 2 + 1);
    }
  }

  function drawFlame(st, g, cells, now) {
    // Червоне по краю й жовте всередині: інакше полум'я на цьому полі плутається з глиняними ящиками.
    const outer = st.css('--danger', '#e57373');
    const inner = st.css('--accent', '#f4c542');
    const beat = 0.88 + 0.12 * Math.sin(now / 70);
    g.globalAlpha = beat;
    for (const cell of cells || []) {
      const [x, y] = at(cell);
      box(g, x, y, 1, 5, outer);
      box(g, x, y, 5, 4, inner);
    }
    g.globalAlpha = 1;
  }

  function drawBombs(st, g, bombs, now) {
    const body = st.css('--bbomb', '#12211a');
    const spark = st.css('--accent', '#f4c542');
    for (const b of bombs || []) {
      const cx = b.x * PX + PX / 2, cy = b.y * PX + PX / 2;
      // що менше лишилось запалу, то швидше бомба «дихає» — це єдина підказка про час
      const beat = 1 + 0.13 * Math.sin(now / (60 + Math.max(0, b.fuse) * 4));
      g.fillStyle = body;
      g.beginPath();
      g.arc(cx, cy + 1, (PX * 0.36) * beat, 0, Math.PI * 2);
      g.fill();
      g.strokeStyle = st.css('--clay', '#c5763a');
      g.lineWidth = 1.6;
      g.beginPath();
      g.moveTo(cx + 3, cy - PX * 0.3);
      g.lineTo(cx + 6, cy - PX * 0.45);
      g.stroke();
      g.fillStyle = spark;
      g.beginPath();
      g.arc(cx + 7, cy - PX * 0.5, 2.1, 0, Math.PI * 2);
      g.fill();
    }
  }

  function drawMen(st, g, men) {
    for (let i = 0; i < men.length; i++) {
      const m = men[i];
      if (!m || !m.alive) continue;
      const cx = (m.x / SUB) * PX + PX / 2, cy = (m.y / SUB) * PX + PX / 2;
      const r = PX * 0.38;
      g.fillStyle = st.css('--bg', '#0f1f18');
      g.beginPath();
      g.ellipse(cx, cy + r * 0.85, r * 0.8, r * 0.3, 0, 0, Math.PI * 2);   // тінь під ногами
      g.globalAlpha = 0.35;
      g.fill();
      g.globalAlpha = 1;
      g.fillStyle = st.css(SEATS[i][0], SEATS[i][1]);
      g.beginPath();
      g.arc(cx, cy, r, 0, Math.PI * 2);
      g.fill();
      g.fillStyle = st.css('--bg', '#0f1f18');
      g.beginPath();
      g.arc(cx - r * 0.32, cy - r * 0.2, r * 0.17, 0, Math.PI * 2);
      g.arc(cx + r * 0.32, cy - r * 0.2, r * 0.17, 0, Math.PI * 2);
      g.fill();
    }
  }

  function drawShade(st, g, c, f, waiting) {
    if (f.phase === 'go' || (f.phase === 'start' && waiting)) return;
    g.fillStyle = st.css('--gshade', 'rgba(15, 31, 24, .62)');
    g.fillRect(0, 0, c.w, c.h);
    if (f.phase !== 'start' || waiting) return;
    g.fillStyle = st.css('--text', '#ecf1ea');
    g.font = '700 46px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(Math.ceil((f.startIn * TICK_MS) / 1000)), c.w / 2, c.h / 2);
  }

  /// Кадри приходять 16 разів на секунду — між ними бомберів ведемо лінійно, інакше рух смикається.
  function men(st) {
    const cur = st.interp.at();
    const f = (cur && cur.b) || st.last;
    if (!f) return null;
    const a = cur && cur.a;
    // t падає на новому раунді, фаза міняється на паузі — у таких стрибках згладжувати нема чого
    const smooth = a && a !== f && a.p && a.t <= f.t && a.phase === f.phase;
    if (!smooth) return { f, men: f.p || [] };
    const k = cur.t;
    return {
      f,
      men: (f.p || []).map((m, i) => {
        const was = a.p[i];
        return !was || !was.alive ? m : Object.assign({}, m, { x: lerp(was.x, m.x, k), y: lerp(was.y, m.y, k) });
      }),
    };
  }

  function draw(st, waiting) {
    const c = st.cv;
    if (!c) return;
    const now = performance.now();
    const shot = men(st);
    const g = c.ctx;
    g.fillStyle = st.css('--bg2', '#16291f');
    g.fillRect(0, 0, c.w, c.h);
    drawWalls(st, g);
    if (!shot) return;
    const f = shot.f;
    drawBoxes(st, g, f.boxes);
    drawDrops(st, g, f.pw);
    drawBombs(st, g, f.b, now);
    drawMen(st, g, shot.men);
    drawFlame(st, g, f.f, now);
    drawShade(st, g, c, f, waiting);
  }

  // ---------------------------------------------------------------------------------------------
  // Рядок над полем: раунди й апгрейди кожного
  // ---------------------------------------------------------------------------------------------

  function hud(root, ctx, f) {
    let el = root.querySelector(':scope > .bhud');
    if (!el) {
      el = document.createElement('div');
      el.className = 'bhud';
      root.insertBefore(el, root.firstChild);
    }
    const wins = (f && f.wins) || [];
    const men = (f && f.p) || [];
    let html = '';
    for (let i = 0; i < 4; i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      const m = men[i] || {};
      const ups = '💣' + (m.bombs || 1) + ' 🔥' + (m.range || 2) + (m.boots ? ' 👟' : '');
      html += '<span class="bchip s' + i + (m.alive === false ? ' out' : '') + '">'
        + ctx.esc(nick) + ' <b>' + (wins[i] || 0) + '</b> <span class="bups">' + ups + '</span></span>';
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Керування пальцем
  // ---------------------------------------------------------------------------------------------

  function pad(root, ctx, st) {
    let el = root.querySelector(':scope > .bpad');
    if (!ctx.mine) {
      if (el) el.remove();
      return;
    }
    if (!el) {
      el = document.createElement('div');
      el.className = 'bpad';
      const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
      const aria = { 0: 'праворуч', 1: 'вниз', 2: 'ліворуч', 3: 'вгору' };
      el.innerHTML = '<div class="bdirs">'
        + [3, 2, 0, 1].map((d) => '<button type="button" data-dir="' + d + '" aria-label="' + aria[d] + '">' + label[d] + '</button>').join('')
        + '</div><button type="button" class="bbomb" aria-label="бомба">💣</button>';
      // Напрямок «тримають», тому слухаємо саме натиск і відпускання, а не клік.
      el.addEventListener('pointerdown', (e) => {
        const b = e.target.closest('button');
        if (!b) return;
        e.preventDefault();
        if (b.classList.contains('bbomb')) { el._ctx.input('bomb'); return; }
        st.held = +b.dataset.dir;
        el._ctx.input('move', { dir: st.held });
      });
      const release = (e) => {
        const b = e.target.closest('button');
        if (!b || b.classList.contains('bbomb') || st.held !== +b.dataset.dir) return;
        st.held = -1;
        el._ctx.input('move', { dir: -1 });
      };
      el.addEventListener('pointerup', release);
      el.addEventListener('pointercancel', release);
      root.appendChild(el);
    }
    el._ctx = ctx;      // колбеки завжди з останнього update, а не з першого
  }

  // ---------------------------------------------------------------------------------------------
  // Модуль
  // ---------------------------------------------------------------------------------------------

  /// Стан картки живе на її ж корені, але onKey отримує лише ctx — тому кладемо посилання і туди.
  function state(root, ctx) {
    if (!root._bomber) {
      root._bomber = { cv: null, walls: [], last: null, held: -1, bombDown: false, raf: 0, keyup: null, css: ctx.css };
    }
    root._bomber.ctx = ctx;
    ctx._bomber = root._bomber;
    return root._bomber;
  }

  HGames.register({
    id: 'bomber',
    icon: ICON,
    seatNames: ['жовтий', 'зелений', 'рудий', 'сірий'],
    seatClass: ['x', 'o', 'c', 'd'],

    mount(root, ctx) {
      const st = state(root, ctx);
      st.interp = HGames.ui.Interp();
      st.cv = HGames.ui.canvas(root, { w: W * PX, h: H * PX, cls: 'bboard' });
      // Каркас віддає модулю лише keydown, а напрямок тут тримають — відпускання ловимо самі.
      st.keyup = (e) => {
        if (isBomb(e)) { st.bombDown = false; return; }
        if (dirOf(e) !== st.held || st.held < 0) return;
        st.held = -1;
        if (st.ctx && st.ctx.mine && st.ctx.playing) st.ctx.input('move', { dir: -1 });
      };
      document.addEventListener('keyup', st.keyup);
      const loop = () => {
        if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
        draw(st, !(st.ctx && st.ctx.playing));
        st.raf = requestAnimationFrame(loop);
      };
      st.raf = requestAnimationFrame(loop);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      st.ctx = ctx;
      if (!st.cv) return;
      const v = ctx.view;
      if (v && v.walls && v.walls.length) st.walls = v.walls;
      // Вид приходить рідко, зате свіжіший за останній кадр — з ним і починаємо новий раунд.
      if (v && v.p) { st.last = v; st.interp.reset(); st.interp.push(v); }
      pad(root, ctx, st);
      hud(root, ctx, st.last);
      st.cv.resize();
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      st.ctx = ctx;
      if (!st.cv || !f) return;
      st.last = f;
      st.interp.push(f);
      hud(root, ctx, f);
    },

    onKey(e, ctx) {
      const st = ctx._bomber;
      if (!st || !ctx.mine || !ctx.playing) return false;
      if (isBomb(e)) {
        // Автоповтор клавіші дав би десяток Input на секунду дарма — бомба ставиться один раз на натиск.
        if (!st.bombDown) { st.bombDown = true; ctx.input('bomb'); }
        return true;
      }
      const dir = dirOf(e);
      if (dir === undefined) return false;
      if (st.held !== dir) { st.held = dir; ctx.input('move', { dir }); }
      return true;   // інакше стрілки гортали б сторінку
    },

    status(ctx) {
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f || !f.phase) return '';
      if (f.phase === 'start') return 'Готуйсь…';
      if (f.phase === 'pause') {
        const alive = ((f.p || []).map((m, i) => (m && m.alive ? i : -1))).filter((i) => i >= 0);
        return alive.length === 1 ? 'Раунд узяв ' + (ctx.nickOf(alive[0]) || ctx.seatName(alive[0])) : 'Раунд нічий';
      }
      if (f.phase === 'over') return '';
      return ctx.mine ? 'Стрілки або WASD, пробіл — бомба' : 'Дивишся збоку';
    },

    unmount(root) {
      const st = root._bomber;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      root._bomber = null;
    },
  });
})();
