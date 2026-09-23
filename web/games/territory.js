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
    return VARS.map((v) => st.css(v[0], v[1]));
  }

  function draw(st) {
    const c = st.cv;
    if (!c || !st.N) return;
    const g = c.ctx;
    const colour = colours(st);
    const W = st.W, N = st.N, PX = st.PX;
    const text = st.css('--text', '#ecf1ea');
    const ctx = st.ctx;
    const me = ctx && ctx.mine ? ctx.seat : null;
    c.resize();
    g.save();
    g.scale(st.K, st.K);
    g.fillStyle = st.css('--bg2', '#16291f');
    g.fillRect(0, 0, CW, CH);

    // Земля — блідо, слід — на повний колір: так одразу видно, що вже твоє, а що ще горить.
    for (let s = 0; s < VARS.length; s++) {
      g.globalAlpha = 0.4;
      g.fillStyle = colour[s];
      for (let i = 0; i < N; i++) if (st.own[i] === s + 1) g.fillRect((i % W) * PX, ((i / W) | 0) * PX, PX, PX);
      g.globalAlpha = 1;
      for (let i = 0; i < N; i++) if (st.trl[i] === s + 1) g.fillRect((i % W) * PX + 1, ((i / W) | 0) * PX + 1, PX - 2, PX - 2);
    }

    const ready = st.phase === 'ready' && st.startIn > 0 && ctx && ctx.playing;
    if (ready) {
      g.fillStyle = st.css('--gshade', 'rgba(15, 31, 24, .62)');
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
      g.fillStyle = st.css('--bg', '#0f1f18');
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
        g.strokeStyle = st.css('--bg2', '#16291f');
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
      g.fillStyle = st.css('--danger', '#e57373');
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
      g.fillStyle = st.css('--gshade', 'rgba(15, 31, 24, .62)');
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
        + st.css(VARS[s][0], VARS[s][1]) + '">' + (s + 1) + '</i>' + ctx.esc(nick) + ' <b>' + pct + '%</b></span>';
    }).join('');
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  const secs = (ms) => Math.max(0, Math.ceil((ms || 0) / 1000));

  HGames.register({
    id: 'territory',
    icon: ICON,
    seatNames: SEATS,
    // Четверте місце — блакитне, як його наділ (раніше чіп був сірим, а земля синьою)
    seatClass: ['x', 'o', 'c', 'tq', 'tr5', 'tr6'],
    pad: { dirs: true, hint: '{dpad} куди бігти' },
    news: {
      v: '2026-09-24',
      title: 'Земля: тепер до шести загарбників',
      items: [
        '🗺 За столом 2–6 гравців; уп’ятьох і вшістьох поле більше — 48×36, землі на кожного вистачає',
        '⏳ Перед раундом три секунди «Готуйсь»: над кожною головою видно, чия вона, а своя — «ти»',
        '🔢 На голові — номер місця, а твоя обведена другим кільцем: не загубишся навіть серед шести',
        '🔥 Хто згорів — спалахує на місці, а наприкінці на полі написано, хто взяв найбільше',
        '🔵 Чіп блакитного гравця нарешті блакитний, а не сірий',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      st.cv = HGames.ui.canvas(root, { w: CW * st.K, h: CH * st.K, cls: 'territoryboard' });
      const v = ctx.view || {};
      size(st, v.width || 40, v.height || 30);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      if (!st.cv) return;
      // хрестовина потрібна лише тому, хто грає: сів глядач за стіл — вона з'явиться тут
      if (ctx.mine) HGames.ui.dpad(root, (d) => ctx.input('turn', { dir: d }));
      else { const d = root.querySelector(':scope > .dpad'); if (d) d.remove(); }
      fromView(st, ctx.view);
      bar(root, ctx, st);
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
      if (f.phase === 'ready' && f.startIn > 0) return ctx.mine ? 'Готуйсь… можна вже повернути' : 'Готуйсь…';
      const left = 'ще ' + secs(f.timeLeft) + ' с';
      const me = ctx.mine && f.heads ? f.heads[ctx.seat] : null;
      if (me && me.on && !me.alive) return 'Згорів, повертаєшся за ' + secs(me.respawnIn) + ' с · ' + left;
      const how = HGames.ui.coarse() ? 'Хрестовина — куди бігти' : 'Стрілки або WASD';
      return (ctx.mine ? how : 'Дивишся збоку') + ' · ' + left;
    },

    unmount(root) {
      const st = root._terr;
      if (st && st.raf) cancelAnimationFrame(st.raf);
      root._terr = null;
    },
  });
})();
