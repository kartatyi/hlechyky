/*
  Понг. Сервер тикає 25 разів на секунду і шле кадр із дробовими координатами; ми його не малюємо
  «як є», а проганяємо через HGames.ui.Interp і домальовуємо на requestAnimationFrame — інакше на
  144-герцовому екрані м'яч смикався б двадцять п'ять разів на секунду.

  Кадр (Impl/Pong.cs): { t, bx, by, vx, vy, p: [y0, y1], s: [s0, s1], serveIn, startIn, winner }.
  Поле — 160 × 100 умовних одиниць, вісь y дивиться вниз. Правила й стан — тільки на сервері,
  звідси летять самі наміри:
    Input('move', { dir: -1|0|1 })  — клавішу тримають, поки не прийде наступний dir;
    Input('to',   { y })            — палець або миша просять центр ракетки отут.
*/
(() => {
  const W = 160, H = 100;        // поле в одиницях сервера
  const SC = 2;                  // одиниця поля → пікселі канваса (16:10, як просить spec)
  const TICK_MS = 40;
  const TRAIL = 10;              // скільки слідів тягне за собою м'яч
  const SEND_MS = 40;            // не частіше 25 вводів на секунду: хаб пускає 30

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.5" y="4" width="2" height="8" rx="1" fill="var(--accent)"/>'
    + '<rect x="12.5" y="4" width="2" height="8" rx="1" fill="var(--ok)"/>'
    + '<circle cx="8" cy="8" r="1.8" fill="var(--text)"/></svg>';

  // Розкладконезалежно читаємо e.code, але терпимо й e.key: синтетичний keydown із панелі
  // браузера приходить без code (INTEGRATION-NOTES §3).
  const KEYS = { ArrowUp: -1, KeyW: -1, ArrowDown: 1, KeyS: 1, w: -1, W: -1, s: 1, S: 1 };
  const dirOf = (e) => (KEYS[e.code] !== undefined ? KEYS[e.code] : KEYS[e.key]);

  // ---- клавіатура: одна на всі картки, бо тиснуть її не в картку, а в документ ----
  const live = new Set();        // стани карток понга, які зараз на екрані
  const held = { up: false, down: false };

  /// Шлемо не кожне натискання, а лише зміну напрямку: автоповтор клавіші інакше з'їв би всю квоту.
  function pushDir() {
    const dir = (held.down ? 1 : 0) - (held.up ? 1 : 0);
    for (const st of live) {
      const ctx = st.ctx;
      if (!ctx || !ctx.mine || !ctx.playing) { st.sent = null; continue; }
      if (st.sent === dir) continue;
      st.sent = dir;
      ctx.input('move', { dir });
    }
  }
  function setHeld(dir, on) {
    if (dir < 0) held.up = on; else held.down = on;
    pushDir();
  }
  document.addEventListener('keyup', (e) => {
    const d = dirOf(e);
    if (d !== undefined) setHeld(d, false);
  });
  // Пішли з вкладки із затиснутою клавішею — ракетка має спинитись, а не їхати в стіну.
  window.addEventListener('blur', () => { held.up = held.down = false; pushDir(); });

  // ---- стан однієї картки ----
  function state(root, ctx) {
    let st = root._pong;
    if (!st) {
      st = root._pong = {
        cv: null, ctx, interp: HGames.ui.Interp(), last: null, trail: [],
        raf: 0, want: null, sentAt: 0, sent: null,
      };
      live.add(st);
    }
    st.ctx = ctx;
    return st;
  }

  /// Кадр «на зараз»: два останні кадри, змішані інтерполятором. null — ще нічого не приходило.
  function blend(st) {
    const at = st.interp.at();
    if (!at) return st.last;
    const a = at.a || at.b, b = at.b;
    if (!a || !b || !a.p || !b.p) return b;
    // Гол: м'яч стрибнув з-за краю в центр. Плавно вести його через усе поле назад — брехня,
    // тож на такій парі кадрів просто показуємо новіший.
    const jump = !a.s || !b.s || a.s[0] !== b.s[0] || a.s[1] !== b.s[1];
    const L = HGames.ui.lerp, t = at.t;
    return {
      bx: jump ? b.bx : L(a.bx, b.bx, t),
      by: jump ? b.by : L(a.by, b.by, t),
      p: [L(a.p[0], b.p[0], t), L(a.p[1], b.p[1], t)],
      s: b.s, serveIn: b.serveIn, startIn: b.startIn, winner: b.winner, jump,
    };
  }

  // ---- малювання ----
  function draw(st) {
    const c = st.cv;
    if (!c) return;
    const g = c.ctx, ctx = st.ctx;
    const f = blend(st);
    const waiting = !ctx || !ctx.playing;    // стіл ще чекає на суперника — відлік не крутимо
    const css = (n, d) => (ctx ? ctx.css(n, d) : d);

    g.fillStyle = css('--bg2', '#16291f');
    g.fillRect(0, 0, c.w, c.h);

    // середина поля — пунктиром, щоб було видно, чия половина
    g.strokeStyle = css('--line', '#2f4d3d');
    g.lineWidth = 2;
    g.setLineDash([6, 8]);
    g.beginPath();
    g.moveTo(c.w / 2, 0);
    g.lineTo(c.w / 2, c.h);
    g.stroke();
    g.setLineDash([]);

    if (!f) return;
    const yellow = css('--accent', '#f4c542'), green = css('--ok', '#7bd389');
    score(g, c, f, css, yellow, green);

    // ракетки: місце 0 ліворуч, місце 1 праворуч — кольори ті самі, що в чіпах місць
    const ph = 18 * SC, pw = 2 * SC;
    [0, 1].forEach((i) => {
      const x = (i === 0 ? 4 : W - 4) * SC;
      g.fillStyle = i === 0 ? yellow : green;
      g.beginPath();
      g.roundRect(x - pw / 2, f.p[i] * SC - ph / 2, pw, ph, pw / 2);
      g.fill();
    });

    // м'яч зі слідом: слід живе лише поки м'яч летить
    const flying = !f.startIn && !f.serveIn && f.winner == null;
    if (f.jump || !flying) st.trail.length = 0;
    else {
      st.trail.push([f.bx, f.by]);
      if (st.trail.length > TRAIL) st.trail.shift();
    }
    const white = css('--text', '#ecf1ea');
    g.fillStyle = white;
    st.trail.forEach(([x, y], i) => {
      g.globalAlpha = 0.06 + 0.2 * (i / TRAIL);
      g.beginPath();
      g.arc(x * SC, y * SC, 1.5 * SC * (0.4 + 0.6 * (i / TRAIL)), 0, Math.PI * 2);
      g.fill();
    });
    g.globalAlpha = 1;
    g.beginPath();
    g.arc(f.bx * SC, f.by * SC, 1.5 * SC, 0, Math.PI * 2);
    g.fill();

    overlay(g, c, f, ctx, css, waiting);
  }

  /// Рахунок великими цифрами по центру вгорі.
  function score(g, c, f, css, yellow, green) {
    const s = f.s || [0, 0];
    g.font = '700 26px ' + css('--font', 'system-ui, sans-serif');
    g.textBaseline = 'top';
    g.textAlign = 'right';
    g.fillStyle = yellow;
    g.fillText(String(s[0]), c.w / 2 - 14, 8);
    g.textAlign = 'left';
    g.fillStyle = green;
    g.fillText(String(s[1]), c.w / 2 + 14, 8);
    g.textAlign = 'center';
    g.fillStyle = css('--muted', '#9db3a5');
    g.fillText(':', c.w / 2, 8);
  }

  /// Затемнення з написом: «готуйсь» перед першим ударом і підсумок після сьомого очка.
  function overlay(g, c, f, ctx, css, waiting) {
    const ready = f.startIn > 0 && !waiting;
    if (!ready && f.winner == null) return;
    g.fillStyle = css('--gshade', 'rgba(15, 31, 24, .62)');
    g.fillRect(0, 0, c.w, c.h);
    g.fillStyle = css('--text', '#ecf1ea');
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    if (ready) {
      g.font = '700 44px ' + css('--font', 'system-ui, sans-serif');
      g.fillText(String(Math.ceil((f.startIn * TICK_MS) / 1000)), c.w / 2, c.h / 2);
      return;
    }
    const nick = ctx && ctx.nickOf ? ctx.nickOf(f.winner) : null;
    g.font = '700 20px ' + css('--font', 'system-ui, sans-serif');
    g.fillText(nick ? 'Виграв ' + nick : 'Партію зіграно', c.w / 2, c.h / 2);
  }

  // ---- керування пальцем ----

  /// Позиція пальця на канвасі → бажаний центр ракетки в одиницях поля.
  function aimAt(st, ev) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing || !st.cv) return;
    const r = st.cv.el.getBoundingClientRect();
    if (!r.height) return;
    st.want = Math.max(0, Math.min(H, ((ev.clientY - r.top) / r.height) * H));
  }

  /// Останню позицію пальця відправляємо з rAF-циклу: так вона не губиться і не б'є у квоту хаба.
  function flush(st, now) {
    if (st.want == null || now - st.sentAt < SEND_MS) return;
    const ctx = st.ctx;
    st.sentAt = now;
    const y = st.want;
    st.want = null;
    if (ctx && ctx.mine && ctx.playing) { st.sent = null; ctx.input('to', { y }); }
  }

  /// Дві кнопки-утримання під палець. ui.dpad тут не годиться: він на клік, а ракетку треба тримати.
  function pad(root, st) {
    let el = root.querySelector(':scope > .pongpad');
    if (!st.ctx || !st.ctx.mine) { if (el) el.remove(); return; }
    if (el) return;
    el = document.createElement('div');
    el.className = 'pongpad';
    el.innerHTML = '<button type="button" data-d="-1" aria-label="вгору">↑</button>'
      + '<button type="button" data-d="1" aria-label="вниз">↓</button>';
    const grab = (e) => {
      const b = e.target.closest('button');
      if (!b) return;
      e.preventDefault();
      setHeld(+b.dataset.d, e.type === 'pointerdown');
    };
    el.addEventListener('pointerdown', grab);
    el.addEventListener('pointerup', grab);
    el.addEventListener('pointercancel', grab);
    el.addEventListener('pointerleave', grab);
    root.appendChild(el);
  }

  HGames.register({
    id: 'pong',
    icon: ICON,
    seatNames: ['ліва', 'права'],
    seatClass: ['x', 'o'],

    mount(root, ctx) {
      const st = state(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: W * SC, h: H * SC, cls: 'pongboard' });
      st.cv.el.addEventListener('pointermove', (e) => aimAt(st, e));
      st.cv.el.addEventListener('pointerdown', (e) => aimAt(st, e));
      const loop = () => {
        if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
        flush(st, performance.now());
        draw(st);
        st.raf = requestAnimationFrame(loop);
      };
      st.raf = requestAnimationFrame(loop);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      if (!st.cv) return;
      // Перший вид приходить ще до кадрів — з нього й малюємо поле, поки тик не поїхав.
      if (ctx.view && ctx.view.frame) st.last = ctx.view.frame;
      // Скролити сторінку пальцем по канвасу можна лише глядачеві: гравцеві той самий рух — це ракетка.
      st.cv.el.classList.toggle('play', !!ctx.mine);
      pad(root, st);
      st.cv.resize();
      if (!ctx.playing) { st.interp.reset(); st.trail.length = 0; st.sent = null; }
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!f) return;
      st.last = f;
      st.interp.push(f);
    },

    onKey(e, ctx) {
      const d = dirOf(e);
      if (d === undefined || !ctx.mine || !ctx.playing) return false;
      setHeld(d, true);
      return true;
    },

    status(ctx) {
      // Фаза й відлік реалтайму живуть у кадрах, а не у видах.
      const f = ctx.frame || (ctx.view && ctx.view.frame) || null;
      if (!ctx.playing) return '';
      if (f && f.startIn > 0) return 'Готуйсь…';
      if (!ctx.mine) return 'Дивишся збоку';
      const s = (f && f.s) || (ctx.view && ctx.view.scores) || [0, 0];
      const target = (ctx.view && ctx.view.target) || 7;
      return '↑↓ або W/S, чи тягни пальцем · до ' + target + ' (' + s[0] + ':' + s[1] + ')';
    },

    unmount(root) {
      const st = root._pong;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      live.delete(st);
      root._pong = null;
    },
  });
})();
