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

  function state(root, ctx) {
    if (!root._snake) {
      root._snake = { cv: null, last: null, css: ctx.css };
    }
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
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  HGames.register({
    id: 'snake',
    icon: ICON,
    seatNames: ['жовта', 'зелена'],
    seatClass: ['x', 'o'],

    mount(root, ctx) {
      const st = state(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: W * PX, h: H * PX, cls: 'snakeboard' });
    },

    update(root, ctx) {
      const st = state(root, ctx);
      if (!st.cv) return;
      // хрестовина потрібна лише тому, хто грає: сів глядач за стіл — вона з'явиться тут
      if (ctx.mine) HGames.ui.dpad(root, (d) => ctx.input('turn', { dir: d }));
      else { const d = root.querySelector(':scope > .dpad'); if (d) d.remove(); }
      // 'room' приходить рідше за кадри, але після нього вид свіжіший: малюємо з нього
      const f = (ctx.view && ctx.view.a) ? ctx.view : st.last;
      if (f) st.last = f;
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
      ctx.input('turn', { dir });
      return true;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      // відлік іде в кадрах, а не у видах: беремо свіжіше з двох, інакше «Готуйсь…» висіло б
      // на екрані ще довго після того, як змійки поїхали
      const f = ctx.frame && ctx.frame.startIn != null ? ctx.frame
        : (ctx.view && ctx.view.startIn != null ? ctx.view : null);
      if (f && f.startIn > 0) return 'Готуйсь…';
      return ctx.mine ? 'Стрілки або WASD' : 'Дивишся збоку';
    },

    unmount(root) { root._snake = null; },
  });
})();
