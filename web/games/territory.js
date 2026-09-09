/*
  Земля (splix-подібна). Реалтайм: сервер тикає раз на 100 мс і шле кадр, у якому лежать ЛИШЕ зміни
  поля — 1200 клітинок по десять разів на секунду ніхто б не витримав. Тому модуль тримає своє поле:
  повні рядки бере з виду (він приходить на старті, після реконекту й на кінець раунду), а між ними
  накладає зміни з кадрів.

  Вид (Impl/Territory.cs): { width, height, t, owner, trail, heads, area, timeLeft }
    owner / trail — рядки з 1200 символів '0'..'4' ('0' — нічия, далі номер місця плюс один).
  Кадр:            { t, heads, area, changes: [[клітинка, хто], …], trails: [[клітинка, хто], …], timeLeft }
    Тик, у якому змін більше, ніж саме поле (велика пожежа, замикання пів поля), приїжджає теж
    повними рядками owner/trail — тоді changes і trails порожні.
  Ввід:            Input('turn', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.
*/
(() => {
  const W = 40, H = 30, N = W * H, PX = 12;
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.4" y="1.4" width="7" height="7" rx="1.2" fill="var(--accent)" opacity=".6"/>'
    + '<rect x="7.6" y="7.6" width="7" height="7" rx="1.2" fill="var(--ok)" opacity=".6"/>'
    + '<path d="M8.6 1.4h6v6" fill="none" stroke="var(--clay)" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>';
  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  const SEATS = ['жовта', 'зелена', 'глиняна', 'блакитна'];
  // Четвертий колір свій: у теми сайту трьох акцентів вистачає на все, крім гри на чотирьох.
  const VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--gterr4', '#6aa9e9']];

  function state(root, ctx) {
    if (!root._terr) {
      root._terr = {
        cv: null, css: ctx.css, seen: null,
        own: new Uint8Array(N), trl: new Uint8Array(N),
        heads: [], area: [0, 0, 0, 0], left: 0,
      };
    }
    return root._terr;
  }

  /// Повні рядки поля: приходять і у виді, і у важкому кадрі. false — рядків нема, поле не чіпали.
  function rows(st, v) {
    if (!v || typeof v.owner !== 'string' || v.owner.length !== N) return false;
    for (let i = 0; i < N; i++) st.own[i] = v.owner.charCodeAt(i) - 48;
    const t = typeof v.trail === 'string' && v.trail.length === N ? v.trail : null;
    for (let i = 0; i < N; i++) st.trl[i] = t ? t.charCodeAt(i) - 48 : 0;
    return true;
  }

  /// Вид накладаємо рівно раз: core.js кличе update() на кожну подію 'rooms' із тим самим кешованим видом,
  /// і без цієї перевірки він відкочував би поле назад, затираючи свіжі кадри. Звіряємось із самим об'єктом,
  /// а не з його t: у нового раунду лічильник тиків знову нульовий, і після «Ще раз» його вид пройти МАЄ —
  /// інакше на полі лишилась би минула партія (стартові наділи їдуть тільки у виді, кадром їх нема).
  function fromView(st, v) {
    if (!v || st.seen === v || !rows(st, v)) return false;
    st.seen = v;
    take(st, v);
    return true;
  }

  /// Зміни з кадру: спершу поле, потім голови й площі.
  function fromFrame(st, f) {
    if (!f) return;
    const put = (list, map) => {
      for (let i = 0; i < (list || []).length; i++) {
        const p = list[i];
        if (p && p.length >= 2 && p[0] >= 0 && p[0] < N) map[p[0]] = p[1];
      }
    };
    if (!rows(st, f)) {              // важкий тик приїхав повними рядками, решта — парами
      put(f.changes, st.own);
      put(f.trails, st.trl);
    }
    take(st, f);
  }

  function take(st, f) {
    if (Array.isArray(f.heads)) st.heads = f.heads;
    if (Array.isArray(f.area)) st.area = f.area;
    if (typeof f.timeLeft === 'number') st.left = f.timeLeft;
  }

  function draw(st) {
    const c = st.cv;
    if (!c) return;
    const g = c.ctx;
    const colour = VARS.map((v) => st.css(v[0], v[1]));
    g.fillStyle = st.css('--bg2', '#16291f');
    g.fillRect(0, 0, c.w, c.h);

    // Земля — блідо, слід — на повний колір: так одразу видно, що вже твоє, а що ще горить.
    for (let s = 0; s < 4; s++) {
      g.globalAlpha = 0.4;
      g.fillStyle = colour[s];
      for (let i = 0; i < N; i++) if (st.own[i] === s + 1) g.fillRect((i % W) * PX, ((i / W) | 0) * PX, PX, PX);
      g.globalAlpha = 1;
      for (let i = 0; i < N; i++) if (st.trl[i] === s + 1) g.fillRect((i % W) * PX + 1, ((i / W) | 0) * PX + 1, PX - 2, PX - 2);
    }

    for (let s = 0; s < st.heads.length && s < 4; s++) {
      const h = st.heads[s];
      if (!h || !h.on || !h.alive || h.x < 0) continue;
      g.fillStyle = colour[s];
      g.beginPath();
      g.roundRect(h.x * PX - 1, h.y * PX - 1, PX + 2, PX + 2, 4);
      g.fill();
      g.strokeStyle = st.css('--text', '#ecf1ea');
      g.lineWidth = 1.5;
      g.stroke();
    }
    g.globalAlpha = 1;
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
      return '<span class="gterr-p' + (dead ? ' out' : '') + '"><i style="background:' + st.css(VARS[s][0], VARS[s][1]) + '"></i>'
        + ctx.esc(nick) + ' <b>' + pct + '%</b></span>';
    }).join('');
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  const secs = (ms) => Math.max(0, Math.ceil((ms || 0) / 1000));

  HGames.register({
    id: 'territory',
    icon: ICON,
    seatNames: SEATS,
    seatClass: ['x', 'o', 'c', 'd'],

    mount(root, ctx) {
      const st = state(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: W * PX, h: H * PX, cls: 'territoryboard' });
    },

    update(root, ctx) {
      const st = state(root, ctx);
      if (!st.cv) return;
      // хрестовина потрібна лише тому, хто грає: сів глядач за стіл — вона з'явиться тут
      if (ctx.mine) HGames.ui.dpad(root, (d) => ctx.input('turn', { dir: d }));
      else { const d = root.querySelector(':scope > .dpad'); if (d) d.remove(); }
      fromView(st, ctx.view);
      bar(root, ctx, st);
      st.cv.resize();
      draw(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!st.cv || !f) return;
      fromFrame(st, f);
      bar(root, ctx, st);
      draw(st);
    },

    onKey(e, ctx) {
      const dir = DIRS[e.code];
      if (dir === undefined || !ctx.mine || !ctx.playing) return false;
      ctx.input('turn', { dir });
      return true;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      // відлік живе в кадрах, а не у видах: вид приходить лише на старті й на кінець раунду
      const f = ctx.frame && ctx.frame.timeLeft != null ? ctx.frame : (ctx.view || {});
      const left = 'ще ' + secs(f.timeLeft) + ' с';
      const me = ctx.mine && f.heads ? f.heads[ctx.seat] : null;
      if (me && me.on && !me.alive) return 'Згорів, повертаєшся за ' + secs(me.respawnIn) + ' с · ' + left;
      return (ctx.mine ? 'Стрілки або WASD' : 'Дивишся збоку') + ' · ' + left;
    },

    unmount(root) { root._terr = null; },
  });
})();
