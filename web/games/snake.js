/*
  Змійка-дуель. Реалтайм: сервер тикає раз на 120 мс і шле кадр, ми його просто малюємо.
  Малювання портоване зі старого app.js один в один — гра має виглядати так, як виглядала.

  Кадр (Impl/SnakeGame.cs): { a: int[], b: int[], apple, winsA, winsB, startIn, winner }.
  Ввід: Input('turn', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.
*/
(() => {
  const W = 26, H = 18, PX = 16, TICK_MS = 120;
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2 13h4.2a2.6 2.6 0 0 0 0-5.2H5.2a2.6 2.6 0 0 1 0-5.2H9" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
    + '<circle cx="13.2" cy="3.2" r="2.1" fill="var(--clay)"/></svg>';
  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };

  /// waiting — стіл ще чекає на другого гравця, тоді відлік не показуємо: він і не йде.
  function draw(st, f, waiting) {
    if (!f || !st) return;
    const c = st.cv, ctx = c.ctx;
    const at = (cell) => [(cell % W) * PX, Math.floor(cell / W) * PX];
    ctx.fillStyle = st.css('--bg2', '#16291f');
    ctx.fillRect(0, 0, c.w, c.h);
    if (st.wrap) {
      // тор (прохід №3): край — пунктир, а не стіна
      ctx.strokeStyle = 'rgba(111, 179, 232, .55)';
      ctx.lineWidth = 2;
      ctx.setLineDash([6, 6]);
      ctx.strokeRect(1, 1, c.w - 2, c.h - 2);
      ctx.setLineDash([]);
    }

    if (f.apple != null) {
      const [ax, ay] = at(f.apple);
      ctx.fillStyle = st.css('--clay', '#c5763a');
      ctx.beginPath();
      ctx.arc(ax + PX / 2, ay + PX / 2, PX / 2 - 2.5, 0, Math.PI * 2);
      ctx.fill();
    }

    const snake = (cells, head, body) => (cells || []).forEach((cell, i) => {
      const [x, y] = at(cell);
      ctx.fillStyle = i ? body : head;
      ctx.beginPath();
      ctx.roundRect(x + 1, y + 1, PX - 2, PX - 2, i ? 3 : 6);
      ctx.fill();
    });
    snake(f.a, st.css('--accent', '#f4c542'), st.css('--accent2', '#d9a92f'));
    snake(f.b, st.css('--ok', '#7bd389'), st.css('--gsnake2', '#4f9a5e'));

    if ((f.startIn > 0 && !waiting) || f.winner != null) {
      ctx.fillStyle = st.css('--gshade', 'rgba(15, 31, 24, .62)');
      ctx.fillRect(0, 0, c.w, c.h);
      if (f.startIn > 0) {
        ctx.fillStyle = st.css('--text', '#ecf1ea');
        ctx.font = '700 46px system-ui, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(String(Math.ceil((f.startIn * TICK_MS) / 1000)), c.w / 2, c.h / 2);
      }
    }
  }

  /// Палітра з CSS-змінних: getComputedStyle — раз на колір і вид, а не на кожен кадр.
  function palette(css) {
    const m = new Map();
    return (name, fallback) => {
      let v = m.get(name);
      if (v === undefined) { v = css(name, fallback); m.set(name, v); }
      return v;
    };
  }

  function state(root, ctx) {
    // view — вид, з якого вже взято поле. Каркас кладе в ctx.view КЕШОВАНИЙ вид останньої події 'room' і смикає
    // update() ще й на кожну 'rooms' (будь-хто на сайті створив чи покинув стіл); посеред раунду 'room' не летить,
    // тож без цієї позначки змійки на мить відскакували б на стартові місця — туди, де їх застав той старий вид.
    if (!root._snake) {
      root._snake = { cv: null, last: null, view: null, css: palette(ctx.css) };
    }
    root._snake.ctx = ctx;
    return root._snake;
  }

  function score(root, f) {
    let el = root.querySelector(':scope > .gscore');
    if (!el) {
      el = document.createElement('div');
      el.className = 'gscore';
      root.insertBefore(el, root.firstChild);
    }
    const html = '<b>' + (f && f.winsA != null ? f.winsA : 0) + '</b> : <b>' + (f && f.winsB != null ? f.winsB : 0) + '</b>';
    // порівнюємо з тим, що клали самі: el.innerHTML браузер серіалізує по-своєму
    if (el._h !== html) { el._h = html; el.innerHTML = html; }
  }

  function turn(ctx, dir) {
    if (ctx && ctx.mine && ctx.playing) ctx.input('turn', { dir });
  }

  /// Хрестовина потрібна лише тому, хто грає: сів глядач за стіл — вона з'явиться тут.
  function pad(root, ctx) {
    if (!ctx.mine) { const d = root.querySelector(':scope > .dpad'); if (d) d.remove(); return; }
    const el = HGames.ui.dpad(root, (d) => turn(ctx, d));
    // без подвійного тапу-зуму, коли швидко тиснуть сусідні стрілки
    if (el && el.style.touchAction !== 'manipulation') el.style.touchAction = 'manipulation';
  }

  /// Свайп по полю (≥ 18 px — поворот у бік переважної осі; не відриваючи пальця, можна крутити далі).
  /// Прокрутку пальцем по полю забираємо лише в того, хто грає.
  function swipe(root, el) {
    if (el._swipe) return;
    el._swipe = true;
    let from = null;
    const live = () => { const s = root._snake; return s && s.ctx.mine && s.ctx.playing ? s.ctx : null; };
    el.addEventListener('pointerdown', (e) => {
      if (e.button > 0 || !live()) return;
      from = { x: e.clientX, y: e.clientY, id: e.pointerId };
      try { el.setPointerCapture(e.pointerId); } catch { /* стара миша без capture */ }
    });
    el.addEventListener('pointermove', (e) => {
      if (!from || e.pointerId !== from.id) return;
      const dx = e.clientX - from.x, dy = e.clientY - from.y;
      if (Math.max(Math.abs(dx), Math.abs(dy)) < 18) return;
      from.x = e.clientX;
      from.y = e.clientY;
      turn(live(), Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3));
    });
    const end = (e) => { if (from && e.pointerId === from.id) from = null; };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
  }

  HGames.register({
    id: 'snake',
    icon: ICON,
    seatNames: ['жовта', 'зелена'],
    seatClass: ['x', 'o'],
    pad: { dirs: true, hint: '{dpad} куди повзти' },
    news: {
      v: '2026-09-29',
      title: 'Змійка: поле-тор',
      items: [
        '🌀 Нова опція столу «Край поля: тор» — стін нема, виповзла праворуч — з'явилась ліворуч',
        '👆 На телефоні крути свайпом просто по полю, а стрілки під полем спрацьовують на дотик, а не на відпускання',
        '⌨️ Затиснута стрілка більше не з\'їдає наступного повороту',
        '🐍 Змійки більше не смикаються на мить назад, коли хтось на сайті ставить чи закриває стіл',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: W * PX, h: H * PX, cls: 'snakeboard' });
      swipe(root, st.cv.el);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      if (!st.cv) return;
      st.css = palette(ctx.css);
      // хрестовина потрібна лише тому, хто грає: сів глядач за стіл — вона з'явиться тут
      pad(root, ctx);
      const touch = ctx.mine && ctx.playing ? 'none' : '';
      if (st.cv.el.style.touchAction !== touch) st.cv.el.style.touchAction = touch;
      // Новий вид (подія 'room') свіжіший за кадри — малюємо з нього. Той самий об'єкт удруге — застарілий кеш.
      if (ctx.view && ctx.view.a && ctx.view !== st.view) { st.view = ctx.view; st.last = ctx.view; st.wrap = !!ctx.view.wrap; }
      const f = st.last;
      score(root, f);
      st.cv.resize();
      draw(st, f, !ctx.playing);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!st.cv || !f) return;
      st.last = f;
      score(root, f);
      draw(st, f, !ctx.playing);
    },

    onKey(e, ctx) {
      const dir = DIRS[e.code];
      if (dir === undefined || !ctx.mine || !ctx.playing) return false;
      // Автоповтор затиснутої клавіші (~30 на секунду на Windows) з'їдав квоту каркаса — 30 Input на секунду, —
      // і справжній поворот у ту саму секунду мовчки губився. Клавішу з'їдаємо, щоб сторінка не гортала.
      if (e.repeat) return true;
      turn(ctx, dir);
      return true;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      // відлік іде в кадрах, а не у видах: беремо свіжіше з двох, інакше «Готуйсь…» висіло б
      // на екрані ще довго після того, як змійки поїхали
      const f = ctx.frame && ctx.frame.startIn != null ? ctx.frame
        : (ctx.view && ctx.view.startIn != null ? ctx.view : null);
      if (f && f.startIn > 0) return 'Готуйсь…';
      const tor = ctx.view && ctx.view.wrap ? ' · 🌀 тор: край наскрізь' : '';
      return ctx.mine ? 'Стрілки або WASD' + tor : 'Дивишся збоку' + tor;
    },

    unmount(root) { root._snake = null; },
  });
})();
