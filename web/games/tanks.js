/*
  Танчики. Реалтайм: сервер тикає раз на 40 мс і шле кадр, ми його малюємо й згладжуємо.

  Кадр (Impl/Tanks.cs):
    { t, p: [{x, y, d, alive, shield, frags, reload, back, perks}], s: [{i, x, y, d, big}], pw: [{x, y, kind}],
      bricks: int[], phase, startIn, left }
  Координати танків (лівий верхній кут) і снарядів (точка) — у дванадцятих частках клітинки (SUB).
  Сталь не міняється за партію, тому її шле лише вид: { width, height, sub, walls, need, ... }.

  Ввід: Input('move', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору, -1 стоп (напрямок «тримають»);
  Input('fire', { on: true|false }) — затиснув / відпустив «💥» (сервер стріляє сам, щойно можна);
  Input('fire') без on — один натиск (зарано на ~150 мс — сервер його запам'ятає).

  Прохід №3 (29.09): кадр ще має ev (події [id, як, хто, кого, серія]), e (ворожі 🤖 [слот, x, y, дуло, щит]) і wv
  ([хвиля, скільки 🤖 лишилось]); вид — bush/ice (кущі й лід), teams, bases ([клітинка, команда, цілий]), coop
  (скільки хвиль), note, end ('base' | 'waves' | 'time'), sum (підсумок, див. summary()).
*/
(() => {
  // Розмір поля каже вид (width/height): до чотирьох — 21×15, на п'ятьох-шістьох — 27×19.
  const PX = 22, SUB = 12, TICK_MS = 40;
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="2" y="5" width="9" height="8" rx="2" fill="var(--accent)"/>'
    + '<rect x="9" y="8" width="6" height="2" fill="var(--clay)"/>'
    + '<rect x="1" y="4" width="11" height="2" fill="var(--clay)"/><rect x="1" y="12" width="11" height="2" fill="var(--clay)"/></svg>';

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
  const isFire = (e) => e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar';
  const DELTA = [[1, 0], [0, 1], [-1, 0], [0, -1]];
  const SEATS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'], ['--tblue', '#6fb3e8'], ['--tpink', '#e88ac0']];
  const BOOM_MS = 380;
  const GLYPH = { speed: '⚡', twin: '🔫', rapid: '🚀', shield: '🛡', pierce: '💥', bounce: '🪃' };
  const PERK = { s: '⚡', t: '🔫', r: '🚀', p: '💥', b: '🪃' };
  const TEAMS = ['🥒 Огірки', '🍅 Помідори'];
  const FEED_MS = 3000, BANNER_MS = 1600;
  const SEATS_N = 6;

  const at = (cell, W) => [(cell % W) * PX, Math.floor(cell / W) * PX];
  /// На звичайному моніторі (DPR 1) мапа розтягується на 620+ пікселів і милиться — малюємо вдвічі щільніше.
  /// На телефонах із DPR ≥ 2 це вже зробив каркас.
  const scale = () => ((window.devicePixelRatio || 1) >= 2 ? 1 : 2);
  const lerp = (a, b, t) => a + (b - a) * t;
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };

  // ---------------------------------------------------------------------------------------------
  // Малювання
  // ---------------------------------------------------------------------------------------------

  function palette(st) {
    return {
      bg2: st.css('--bg2', '#16291f'),
      panel: st.css('--panel', '#1c3328'),
      steel: st.css('--panel3', '#2b4c3c'),
      edge: st.css('--line', '#2f4d3d'),
      clay: st.css('--clay', '#c5763a'),
      mortar: st.css('--tmortar', '#8f5527'),
      dark: st.css('--tdark', '#0d1a13'),
      accent: st.css('--accent', '#f4c542'),
      danger: st.css('--danger', '#e57373'),
      text: st.css('--text', '#ecf1ea'),
      shade: st.css('--gshade', 'rgba(15, 31, 24, .62)'),
      ice: st.css('--tice', '#a9d6ea'),
      bush: st.css('--tbush', '#3f8f4a'),
      bush2: st.css('--tbush2', '#2c6b36'),
      bot: st.css('--tbot', '#b0524a'),
      ok: st.css('--ok', '#7bd389'),
      seats: SEATS.map(([name, fallback]) => st.css(name, fallback)),
    };
  }

  function drawWalls(pal, g, walls, W) {
    for (const cell of walls) {
      const [x, y] = at(cell, W);
      g.fillStyle = pal.steel;
      g.fillRect(x, y, PX, PX);
      g.fillStyle = pal.edge;
      g.fillRect(x, y, PX, 2);
      g.fillRect(x, y, 2, PX);
    }
  }

  function drawBricks(pal, g, cells, W) {
    for (const cell of cells || []) {
      const [x, y] = at(cell, W);
      g.fillStyle = pal.clay;
      g.fillRect(x + 1, y + 1, PX - 2, PX - 2);
      // кладка: два ряди, шов зі зсувом — щоб цегла читалась цеглою
      g.strokeStyle = pal.mortar;
      g.lineWidth = 1.4;
      g.beginPath();
      g.moveTo(x + 1, y + PX / 2); g.lineTo(x + PX - 1, y + PX / 2);
      g.moveTo(x + PX / 2, y + 1); g.lineTo(x + PX / 2, y + PX / 2);
      g.moveTo(x + PX / 4, y + PX / 2); g.lineTo(x + PX / 4, y + PX - 1);
      g.moveTo(x + 3 * PX / 4, y + PX / 2); g.lineTo(x + 3 * PX / 4, y + PX - 1);
      g.stroke();
    }
  }

  function drawIce(pal, g, cells, W) {
    for (const cell of cells || []) {
      const [x, y] = at(cell, W);
      g.fillStyle = pal.ice;
      g.globalAlpha = 0.5;
      g.fillRect(x, y, PX, PX);
      g.globalAlpha = 0.9;
      g.strokeStyle = pal.text;
      g.lineWidth = 1;
      g.beginPath();
      g.moveTo(x + 4, y + PX - 7); g.lineTo(x + 10, y + PX - 13);
      g.moveTo(x + 11, y + PX - 5); g.lineTo(x + 18, y + PX - 12);
      g.stroke();
      g.globalAlpha = 1;
    }
  }

  function drawBushes(pal, g, cells, W) {
    for (const cell of cells || []) {
      const [x, y] = at(cell, W);
      g.fillStyle = pal.bush2;
      g.fillRect(x, y, PX, PX);
      g.fillStyle = pal.bush;
      for (const [a, b, r] of [[6, 6, 6], [16, 7, 6], [8, 16, 6], [16, 16, 6], [11, 11, 5]]) {
        g.beginPath();
        g.arc(x + a, y + b, r, 0, Math.PI * 2);
        g.fill();
      }
    }
  }

  /// Глеки-бази команд: плитка кольору команди й 🏺; розбитий — черепки.
  function drawBases(pal, g, bases, W) {
    for (const [cell, team, up] of bases || []) {
      const [x, y] = at(cell, W);
      g.fillStyle = team === 0 ? pal.ok : pal.danger;
      g.globalAlpha = 0.35;
      g.beginPath();
      g.roundRect(x + 1, y + 1, PX - 2, PX - 2, 5);
      g.fill();
      g.globalAlpha = 1;
      g.font = '15px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(up ? '🏺' : '💥', x + PX / 2, y + PX / 2 + 1);
    }
  }

  function drawLoot(pal, g, drops, now) {
    for (const d of drops || []) {
      const x = d.x * PX, y = d.y * PX;
      g.fillStyle = pal.panel;
      g.globalAlpha = 0.8 + 0.2 * Math.sin(now / 160);
      g.beginPath();
      g.roundRect(x + 3, y + 3, PX - 6, PX - 6, 6);
      g.fill();
      g.globalAlpha = 1;
      g.font = '12px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(GLYPH[d.kind] || '?', x + PX / 2, y + PX / 2 + 1);
    }
  }

  /// i — місце (його номер пишемо на башті: шість кольорів близькі, а цифру не сплутає й дальтонік),
  /// mine — це мій танк (кільце й стрілочка), start — іде відлік (тоді ще й «ти»).
  function drawTank(pal, g, m, color, now, i, mine, start, team) {
    const px = (m.x / SUB) * PX, py = (m.y / SUB) * PX;
    const cx = px + PX / 2, cy = py + PX / 2;
    const [dx, dy] = DELTA[m.d] || DELTA[0];
    const horizontal = dy === 0;
    if (team === 0 || team === 1) {
      // Команда — кільцем під танком: 🥒 зелене, 🍅 червоне (номер і колір місця лишаються).
      g.strokeStyle = team === 0 ? pal.ok : pal.danger;
      g.lineWidth = 2;
      g.beginPath();
      g.roundRect(px - 1, py - 1, PX + 2, PX + 2, 6);
      g.stroke();
    }
    // гусениці — дві смуги по боках, перпендикулярно до руху
    g.fillStyle = pal.dark;
    if (horizontal) { g.fillRect(px + 2, py + 1, PX - 4, 5); g.fillRect(px + 2, py + PX - 6, PX - 4, 5); }
    else { g.fillRect(px + 1, py + 2, 5, PX - 4); g.fillRect(px + PX - 6, py + 2, 5, PX - 4); }
    // корпус
    g.fillStyle = color;
    g.beginPath();
    g.roundRect(px + 3, py + 3, PX - 6, PX - 6, 3);
    g.fill();
    // башта й дуло
    g.strokeStyle = pal.dark;
    g.lineWidth = 3;
    g.lineCap = 'round';
    g.beginPath();
    g.moveTo(cx, cy);
    g.lineTo(cx + dx * PX * 0.55, cy + dy * PX * 0.55);
    g.stroke();
    g.fillStyle = pal.dark;
    g.beginPath();
    g.arc(cx, cy, PX * 0.24, 0, Math.PI * 2);
    g.fill();
    g.fillStyle = color;
    g.font = '700 7px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(i < SEATS_N ? String(i + 1) : '×', cx, cy + 0.5);
    if (mine) {
      // Своя стрілочка над танком: на шістьох «де я?» — перше питання після кожного повернення.
      const top = py - 2;
      g.fillStyle = pal.text;
      g.beginPath();
      g.moveTo(cx - 4, top - 5);
      g.lineTo(cx + 4, top - 5);
      g.lineTo(cx, top);
      g.closePath();
      g.fill();
      if (start) {
        g.strokeStyle = pal.text;
        g.lineWidth = 1.5;
        g.beginPath();
        g.roundRect(px - 1, py - 1, PX + 2, PX + 2, 5);
        g.stroke();
        g.font = '700 10px system-ui, sans-serif';
        g.textBaseline = 'bottom';
        g.fillText('ти', cx, top - 6);
      }
    }
    if (m.shield > 0) {
      g.strokeStyle = pal.accent;
      g.lineWidth = 2;
      g.globalAlpha = 0.55 + 0.35 * Math.sin(now / 90);
      g.beginPath();
      g.arc(cx, cy, PX * 0.62, 0, Math.PI * 2);
      g.stroke();
      g.globalAlpha = 1;
    }
  }

  function drawShells(pal, g, shells) {
    for (const s of shells || []) {
      const x = (s.x / SUB) * PX, y = (s.y / SUB) * PX;
      const [dx, dy] = DELTA[s.d] || DELTA[0];
      g.strokeStyle = pal.accent;
      g.lineWidth = 2;
      g.globalAlpha = 0.5;
      g.beginPath();
      g.moveTo(x - dx * 7, y - dy * 7);
      g.lineTo(x, y);
      g.stroke();
      g.globalAlpha = 1;
      g.fillStyle = s.big ? pal.danger : pal.text;
      g.beginPath();
      g.arc(x, y, s.big ? 4 : 2.6, 0, Math.PI * 2);
      g.fill();
    }
  }

  function drawBooms(pal, g, booms, now) {
    for (const b of booms) {
      const k = (now - b.at) / BOOM_MS;
      if (k >= 1) continue;
      const r = PX * (0.3 + 0.9 * k);
      g.globalAlpha = 1 - k;
      g.fillStyle = pal.danger;
      g.beginPath();
      g.arc(b.x, b.y, r, 0, Math.PI * 2);
      g.fill();
      g.fillStyle = pal.accent;
      g.beginPath();
      g.arc(b.x, b.y, r * 0.55, 0, Math.PI * 2);
      g.fill();
      g.globalAlpha = 1;
    }
  }

  /// 1 фраг, 2 фраги, 5 фрагів.
  const frags = (n) => {
    const t = n % 100, o = n % 10;
    return n + (t > 10 && t < 20 ? ' фрагів' : o === 1 ? ' фраг' : o >= 2 && o <= 4 ? ' фраги' : ' фрагів');
  };

  /// Плашка просто на мапі: великий заголовок і рядок під ним.
  function plate(pal, g, c, u, title, color, sub) {
    g.fillStyle = pal.dark;
    g.globalAlpha = 0.85;
    g.beginPath();
    g.roundRect(24, c.h / 2 - 36 * u, c.w - 48, 70 * u, 14);
    g.fill();
    g.globalAlpha = 1;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.font = '700 ' + Math.round(24 * u) + 'px system-ui, sans-serif';
    g.fillStyle = color;
    g.fillText(title, c.w / 2, c.h / 2 - 9 * u, c.w - 60);
    g.font = Math.round(14 * u) + 'px system-ui, sans-serif';
    g.fillStyle = pal.text;
    g.fillText(sub, c.w / 2, c.h / 2 + 17 * u, c.w - 60);
  }

  function drawShade(pal, g, c, f, waiting, st) {
    if (f.phase === 'go' || (f.phase === 'start' && waiting)) return;
    g.fillStyle = pal.shade;
    g.fillRect(0, 0, c.w, c.h);
    if (f.phase === 'over') {
      // Хто взяв партію — великими літерами просто на мапі (як у Бомбера й Кривулі), а не лише рядком під нею.
      const ctx = st && st.ctx;
      const res = ctx && ctx.room && ctx.room.result;
      if (!res) return;
      const v = ctx.view || {};
      const u0 = c.w / (21 * PX);
      if (v.coop || (v.teams && res.winners && res.winners.length) || (v.teams && v.end)) {
        const won = v.coop ? v.end === 'waves' : true;
        const team = !v.coop && res.winners && res.winners.length ? v.teams[res.winners[0]] : -1;
        const wave = (f.wv && f.wv[0]) || 0;
        const title = v.coop ? (won ? '🏆 Глек вистояв!' : '💔 Глек розбили') : team >= 0 ? '🏆 ' + TEAMS[team] : 'Нічия';
        const sub = v.coop ? (won ? 'відбили ' + wave + ' з ' + v.coop + ' хвиль' : 'на ' + wave + '-й хвилі 🤖')
          : team < 0 ? 'фрагів порівну' : v.end === 'base' ? 'розбили чужий глек' : 'більше фрагів за час';
        plate(pal, g, c, u0, title, team === 0 ? pal.ok : team === 1 ? pal.danger : won ? pal.accent : pal.danger, sub);
        return;
      }
      const who = res.winners || [];
      const one = who.length === 1 ? who[0] : -1;
      const nick = one >= 0 ? (ctx.nickOf(one) || ctx.seatName(one)) : '';
      const m = one >= 0 && f.p ? f.p[one] : null;
      const u = c.w / (21 * PX);   // на великій мапі літери більші — інакше на телефоні вони дрібніють
      g.fillStyle = pal.dark;
      g.globalAlpha = 0.85;
      g.beginPath();
      g.roundRect(24, c.h / 2 - 36 * u, c.w - 48, 70 * u, 14);
      g.fill();
      g.globalAlpha = 1;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.font = '700 ' + Math.round(24 * u) + 'px system-ui, sans-serif';
      g.fillStyle = one >= 0 ? (pal.seats[one] || pal.text) : pal.text;
      g.fillText(one >= 0 ? '🏆 ' + nick : who.length ? '🏆 ' + who.map((i) => ctx.nickOf(i) || ctx.seatName(i)).join(', ') : 'Нічия', c.w / 2, c.h / 2 - 9 * u, c.w - 60);
      g.font = Math.round(14 * u) + 'px system-ui, sans-serif';
      g.fillStyle = pal.text;
      g.fillText(one >= 0 ? 'бере партію — ' + frags((m && m.frags) || 0) : who.length ? 'поділили першість' : 'фрагів порівну', c.w / 2, c.h / 2 + 17 * u);
      return;
    }
    if (f.phase !== 'start' || waiting) return;
    g.fillStyle = pal.text;
    g.font = '700 46px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(Math.ceil((f.startIn * TICK_MS) / 1000)), c.w / 2, c.h / 2);
  }

  /// Кадри йдуть 25 разів на секунду — між ними танки й снаряди ведемо лінійно. Снаряди — за id:
  /// «третій у списку» після пострілу чи влучання — це вже інший снаряд.
  function shot(st) {
    const cur = st.interp.at();
    const f = (cur && cur.b) || st.last;
    if (!f) return null;
    const a = cur && cur.a;
    const smooth = a && a !== f && a.p && a.t <= f.t && a.phase === f.phase;
    const bots = (f.e || []).map((b) => ({ x: b[1], y: b[2], d: b[3], shield: b[4] ? 1 : 0, alive: true, slot: b[0] }));
    if (!smooth) return { f, men: f.p || [], shells: f.s || [], bots };
    const k = cur.t;
    const was = new Map((a.s || []).map((s) => [s.i, s]));
    return {
      f,
      men: (f.p || []).map((m, i) => {
        const w = a.p[i];
        return !w || !w.alive || !m.alive ? m : Object.assign({}, m, { x: lerp(w.x, m.x, k), y: lerp(w.y, m.y, k) });
      }),
      shells: (f.s || []).map((s) => {
        const w = was.get(s.i);
        return !w ? s : Object.assign({}, s, { x: lerp(w.x, s.x, k), y: lerp(w.y, s.y, k) });
      }),
      // 🤖 — за слотом; слот після підбиття віддають новому, тож далекий стрибок не згладжуємо
      bots: bots.map((b) => {
        const w = (a.e || []).find((o) => o[0] === b.slot);
        return !w || Math.abs(w[1] - b.x) + Math.abs(w[2] - b.y) > SUB ? b : Object.assign(b, { x: lerp(w[1], b.x, k), y: lerp(w[2], b.y, k) });
      }),
    };
  }

  /// Палітра — один раз, поки не зміниться тема (data-theme) або не прийде вид: раніше її збирали
  /// щокадру, а це два десятки getComputedStyle 60 разів на секунду.
  function palOf(st) {
    const sig = document.documentElement.getAttribute('data-theme') || '';
    if (!st.pal || st.palSig !== sig) { st.pal = palette(st); st.palSig = sig; }
    return st.pal;
  }

  const sameCells = (a, b) => {
    if (a === b) return true;
    if (!a || !b || a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
  };

  /// Тло, сталь і цегла — окремим канвасом: сталь стоїть усю партію, цегла міняється лише тоді, коли в неї
  /// влучили. Щокадру кладемо його однією картинкою замість сотні прямокутників і швів.
  function staticLayer(st, pal, bricks) {
    const el = st.cv.el;
    const L = st.layer || (st.layer = document.createElement('canvas'));
    if (L.width === el.width && L.height === el.height && st.layerPal === pal && st.layerWalls === st.walls
      && st.layerIce === st.ice && sameCells(st.layerBricks, bricks)) return L;
    if (L.width !== el.width) L.width = el.width;
    if (L.height !== el.height) L.height = el.height;
    const g = L.getContext('2d');
    const k = el.width / (st.W * PX);
    g.setTransform(k, 0, 0, k, 0, 0);
    g.fillStyle = pal.bg2;
    g.fillRect(0, 0, st.W * PX, st.H * PX);
    drawIce(pal, g, st.ice, st.W);
    drawWalls(pal, g, st.walls, st.W);
    drawBricks(pal, g, bricks, st.W);
    st.layerPal = pal;
    st.layerWalls = st.walls;
    st.layerIce = st.ice;
    st.layerBricks = bricks ? bricks.slice() : [];
    return L;
  }

  /// Кущі — теж окремим канвасом, але поверх танків: хто в кущі, того не видно ні суперникам, ні глядачам.
  function bushLayer(st, pal) {
    if (!st.bush || !st.bush.length) return null;
    const el = st.cv.el;
    const L = st.bushL || (st.bushL = document.createElement('canvas'));
    if (L.width === el.width && L.height === el.height && st.bushPal === pal && st.bushOf === st.bush) return L;
    L.width = el.width;
    L.height = el.height;
    const g = L.getContext('2d');
    const k = el.width / (st.W * PX);
    g.setTransform(k, 0, 0, k, 0, 0);
    drawBushes(pal, g, st.bush, st.W);
    st.bushPal = pal;
    st.bushOf = st.bush;
    return L;
  }

  /// Чи центр танка в кущі.
  function inBush(st, m) {
    if (!st.bushSet || !st.bushSet.size) return false;
    const x = Math.floor((m.x + SUB / 2) / SUB), y = Math.floor((m.y + SUB / 2) / SUB);
    return st.bushSet.has(y * st.W + x);
  }

  function draw(st, waiting) {
    const c = st.cv;
    if (!c) return;
    const now = performance.now();
    const cur = shot(st);
    const g = c.ctx;
    const pal = palOf(st);
    // Малюємо в логічних одиницях поля, а канвас щільніший у K разів (див. scale()).
    const box = { w: st.W * PX, h: st.H * PX };
    g.save();
    g.scale(st.K, st.K);
    g.drawImage(staticLayer(st, pal, cur ? cur.f.bricks : null), 0, 0, box.w, box.h);
    if (cur) {
      const f = cur.f;
      const me = st.ctx && st.ctx.mine ? st.ctx.seat : null;
      const v = st.ctx && st.ctx.view;
      const teams = v && v.teams;
      drawBases(pal, g, v && v.bases, st.W);
      drawLoot(pal, g, f.pw, now);
      const start = f.phase === 'start' && !waiting;
      for (let i = 0; i < cur.men.length; i++) {
        const m = cur.men[i];
        if (m && m.alive) drawTank(pal, g, m, pal.seats[i] || SEATS[i][1], now, i, i === me, start, teams && !v.coop ? teams[i] : -1);
      }
      for (const b of cur.bots) drawTank(pal, g, b, pal.bot, now, SEATS_N, false, false, -1);
      const bushes = bushLayer(st, pal);
      if (bushes) {
        g.drawImage(bushes, 0, 0, box.w, box.h);
        // Себе й своїх видно крізь кущ — напівпрозоро; суперників і 🤖 — ні.
        g.globalAlpha = 0.6;
        for (let i = 0; i < cur.men.length; i++) {
          const m = cur.men[i];
          const own = i === me || (me !== null && teams && teams[me] >= 0 && teams[i] === teams[me]);
          if (m && m.alive && own && inBush(st, m)) drawTank(pal, g, m, pal.seats[i] || SEATS[i][1], now, i, i === me, start, teams && !v.coop ? teams[i] : -1);
        }
        g.globalAlpha = 1;
      }
      drawShells(pal, g, cur.shells);
      if (st.banner && now - st.banner.at < BANNER_MS && f.phase === 'go') {
        const k = (now - st.banner.at) / BANNER_MS;
        g.globalAlpha = k < 0.8 ? 1 : (1 - k) * 5;
        const u = box.w / (21 * PX);
        g.fillStyle = pal.text;
        g.font = '700 ' + Math.round(30 * u) + 'px system-ui, sans-serif';
        g.textAlign = 'center';
        g.textBaseline = 'middle';
        g.fillText(st.banner.text, box.w / 2, box.h / 2);
        g.globalAlpha = 1;
      }
      drawBooms(pal, g, st.booms, now);
      drawShade(pal, g, box, f, waiting, st);
    }
    g.restore();
  }

  // ---------------------------------------------------------------------------------------------
  // Рядок над полем і керування пальцем
  // ---------------------------------------------------------------------------------------------

  function hud(root, ctx, f, st) {
    const v = ctx.view || {};
    let el = root.querySelector(':scope > .thud');
    if (!el) {
      el = document.createElement('div');
      el.className = 'thud';
      root.insertBefore(el, root.firstChild);
    }
    const men = (f && f.p) || [];
    let html = '';
    for (let i = 0; i < 6; i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      const m = men[i] || {};
      const perks = String(m.perks || '').split('').map((k) => PERK[k] || '').join('');
      const team = v.teams && !v.coop ? v.teams[i] : -1;
      html += '<span class="tchip s' + i + (m.alive === false ? ' out' : '') + (i === ctx.seat ? ' me' : '') + '">'
        + (team >= 0 ? '<span class="tk-team">' + TEAMS[team].split(' ')[0] + '</span>' : '')
        + '<i>' + (i + 1) + '</i><span class="tnick">' + ctx.esc(nick) + '</span> <b>' + (m.frags || 0) + '</b>' + (perks ? ' <span class="tperks">' + perks + '</span>' : '')
        + (m.back > 0 ? ' <span class="tback">⌛</span>' : '')
        + (st && st.nemesis === i && ctx.mine && i !== ctx.seat ? ' <span class="tk-nem" title="Підбив тебе — помстися">😈</span>' : '') + '</span>';
    }
    if (v.teams && !v.coop && f && f.p) {
      const sum = [0, 0];
      for (let i = 0; i < SEATS_N; i++) if (v.teams[i] >= 0 && f.p[i]) sum[v.teams[i]] += f.p[i].frags || 0;
      html += '<span class="tchip tk-score">' + TEAMS[0].split(' ')[0] + ' ' + sum[0] + ' : ' + sum[1] + ' ' + TEAMS[1].split(' ')[0] + '</span>';
    }
    if (f && f.wv && f.phase === 'go') html += '<span class="tchip tk-wave">🌊 ' + Math.max(1, f.wv[0]) + '/' + (v.coop || 5) + ' · 🤖 ' + f.wv[1] + '</span>';
    else if (f && f.phase === 'go') html += '<span class="tchip tclock' + ((f.left || 0) * TICK_MS <= 15000 ? ' hot' : '') + '">⏱ ' + clock(f.left || 0) + '</span>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
    }
  }

  function pad(root, ctx, st) {
    let el = root.querySelector(':scope > .tpad');
    // Хрестовина лише поки йде партія: у лобі й після кінця вона штовхала «Почати» / «Ану ще раз» під нижнє меню телефона.
    if (!ctx.mine || !ctx.playing) {
      if (el) { el.remove(); st.touch = -1; st.pid = null; }
      return;
    }
    if (!el) {
      el = document.createElement('div');
      el.className = 'tpad';
      const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
      const aria = { 0: 'праворуч', 1: 'вниз', 2: 'ліворуч', 3: 'вгору' };
      el.innerHTML = '<div class="tdirs">'
        + [3, 2, 0, 1].map((d) => '<button type="button" data-dir="' + d + '" aria-label="' + aria[d] + '">' + label[d] + '</button>').join('')
        + '</div><button type="button" class="tfire" aria-label="постріл">💥</button>';
      el.addEventListener('pointerdown', (e) => {
        const b = e.target.closest('button');
        if (!b) return;
        e.preventDefault();
        try { b.setPointerCapture(e.pointerId); } catch (_) { /* старий браузер */ }
        if (b.classList.contains('tfire')) {
          // Тримаєш — сервер стріляє сам, щойно перезарядився; відпустив — перестав.
          st.firePid = e.pointerId;
          el._tanksCtx.input('fire', { on: true });
          return;
        }
        st.pid = e.pointerId;
        st.touch = +b.dataset.dir;
        steer(st, el._tanksCtx);
      });
      const release = (e) => {
        if (st.firePid === e.pointerId) {
          st.firePid = null;
          el._tanksCtx.input('fire', { on: false });
          return;
        }
        if (st.pid !== e.pointerId) return;
        st.pid = null;
        st.touch = -1;
        steer(st, el._tanksCtx);
      };
      el.addEventListener('pointerup', release);
      el.addEventListener('pointercancel', release);
      root.appendChild(el);
    }
    el._tanksCtx = ctx;
  }

  /// Телефон: на шістьох шапка столу з місцями й рядок гравців штовхали мапу вниз, а хрестовина з «💥»
  /// опинялась під нижнім меню — видно було або мапу, або кнопки. Раз на партію (room.startedAt), коли
  /// партія пішла, прокручуємо так, щоб рядок гравців став під шапку сайту. Усе й так видно — не чіпаємо.
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

  // ---------------------------------------------------------------------------------------------
  // Модуль
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    if (!root._tanks) {
      root._tanks = {
        cv: null, walls: [], last: null, held: -1, pid: null, fireDown: false, booms: [],
        keys: [], touch: -1, seenAt: 0, firePid: null,
        feed: [], evSeen: 0, nemesis: -1, banner: null, bush: null, bushSet: null, ice: null,
        raf: 0, keyup: null, blur: null, phase: '', css: ctx.css, W: 21, H: 15, K: scale(),
      };
    }
    root._tanks.ctx = ctx;
    ctx._tanks = root._tanks;
    return root._tanks;
  }

  /// Куди їхати: палець на хрестовині головніший, далі — остання із затиснутих клавіш. Затиснув →, додав ↑
  /// і відпустив ↑ — танк знову їде праворуч (раніше ставав, хоч → іще тиснули). Серверу — лише зміна.
  function steer(st, ctx) {
    const d = st.touch >= 0 ? st.touch : (st.keys.length ? st.keys[st.keys.length - 1] : -1);
    if (st.held === d) return;
    st.held = d;
    if (ctx && ctx.mine && ctx.playing) ctx.input('move', { dir: d });
  }

  /// Підбиття бачимо як alive true→false між сусідніми кадрами — і малюємо спалах самі.
  function noteBooms(st, f) {
    const was = st.last;
    if (!was || !was.p || !f || !f.p) return;
    const now = performance.now();
    for (let i = 0; i < f.p.length; i++) {
      const a = was.p[i], b = f.p[i];
      if (a && a.alive && b && !b.alive && b.back > 0)
        st.booms.push({ x: (a.x / SUB) * PX + PX / 2, y: (a.y / SUB) * PX + PX / 2, at: now });
    }
    st.booms = st.booms.filter((b) => now - b.at < BOOM_MS);
  }

  const who = (ctx, i) => i >= SEATS_N || i < 0 ? '🤖' : '<b class="tk-n s' + i + '">' + ctx.esc(ctx.nickOf(i) || ctx.seatName(i)) + '</b>';
  const IN_ROW = { 3: 'три', 4: 'чотири', 5: 'п\'ять', 6: 'шість', 7: 'сім', 8: 'вісім', 9: 'дев\'ять', 10: 'десять' };

  function eventHtml(ctx, e) {
    const [, how, a, b, n] = e;
    if (how === 2) return '🏺 ' + who(ctx, a) + ' розбиває глек!';
    if (how === 3) return '🌊 Хвиля ' + n + '!';
    if (how === 4) return '🏆 Усі хвилі відбито!';
    const row = n >= 3 && a < SEATS_N ? ' — 🔥 ' + (IN_ROW[n] || n) + ' поспіль!' : '';
    const rv = ctx.view && ctx.view.rv ? ' +1' : '';
    return how === 1 ? '😈 помста' + rv + ': ' + who(ctx, a) + ' → ' + who(ctx, b) + row : '💥 ' + who(ctx, a) + ' → ' + who(ctx, b) + row;
  }

  /// Нові події з кадру: подія їде в кадрах три секунди — беремо кожну раз, за id.
  function takeEvents(st, ctx, f) {
    if (!f || !f.ev) return;
    const now = performance.now();
    const me = ctx.mine ? ctx.seat : -1;
    for (const e of f.ev) {
      if (e[0] <= st.evSeen) continue;
      st.evSeen = e[0];
      st.feed.push({ html: eventHtml(ctx, e), at: now });
      const [, how, a, b, n] = e;
      if (how <= 1 && b === me) st.nemesis = a;                 // хто мене підбив — 😈 на його чіпі
      if (how === 1 && a === me) st.nemesis = -1;
      if (how === 3) st.banner = { text: '🌊 Хвиля ' + n, at: now };
    }
    if (st.feed.length > 3) st.feed = st.feed.slice(-3);
  }

  function feed(root, ctx, st) {
    let el = root.querySelector(':scope > .tk-feed');
    if (!el) {
      el = document.createElement('div');
      el.className = 'tk-feed';
      el.setAttribute('aria-live', 'polite');
      st.cv.el.insertAdjacentElement('afterend', el);   // одразу під полем, над хрестовиною
    }
    el.classList.toggle('tk-live', !!ctx.playing);
    const now = performance.now();
    st.feed = st.feed.filter((x) => now - x.at < FEED_MS);
    const html = st.feed.map((x) => '<span>' + x.html + '</span>').join('');
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
  }

  /// Підсумок партії: рядок на кожного — фраги, смерті, влучність, найдовша серія, хто найчастіше діставав.
  /// Мені — ще й «ну я тобі» тому, хто підбивав мене найчастіше: реванш за кнопкою «Ще раз».
  function summary(root, ctx, st) {
    let el = root.querySelector(':scope > .tk-sum');
    const v = ctx.view || {};
    const show = v.phase === 'over' && v.sum && v.sum.length;
    if (!show) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'tk-sum';
      const after = root.querySelector(':scope > .tk-feed') || st.cv.el;
      after.insertAdjacentElement('afterend', el);
    }
    const rows = v.sum.slice().sort((a, b) => b[1] - a[1] || a[2] - b[2]);
    const me = ctx.mine ? ctx.seat : -1;
    let html = '';
    for (const [seat, fr, deaths, shots, hits, best, nem, nemN, rv] of rows) {
      const acc = shots ? Math.round((100 * hits) / shots) + '%' : '—';
      html += '<div class="tk-row' + (seat === me ? ' me' : '') + '"><span class="tk-who">' + who(ctx, seat) + '</span>'
        + '<span title="фраги">💥 ' + fr + '</span><span title="підбили">💀 ' + deaths + '</span>'
        + '<span title="влучність: ' + hits + ' з ' + shots + '">🎯 ' + acc + '</span>'
        + (best >= 2 ? '<span title="найдовша серія без смерті">🔥 ' + best + '</span>' : '')
        + (rv ? '<span title="помст">😈 ' + rv + '</span>' : '')
        + (nem >= 0 && nemN >= 2 ? '<span class="tk-nemsum">дістав(ла) найчастіше: ' + who(ctx, nem) + ' ×' + nemN + '</span>' : '')
        + '</div>';
    }
    const mine = v.sum.find((r) => r[0] === me);
    if (mine && mine[6] >= 0 && mine[7] >= 2)
      html += '<div class="tk-grr">😤 Ну я тобі, ' + who(ctx, mine[6]) + '! Реванш — «Ще раз»</div>';
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
  }

  function syncHeld(st) {
    const phase = (st.last && st.last.phase) || '';
    if (phase === 'go' && st.phase !== 'go' && st.held >= 0 && st.ctx && st.ctx.mine && st.ctx.playing)
      st.ctx.input('move', { dir: st.held });
    st.phase = phase;
  }

  /// Цикл малювання живе, лише поки є що рухати: йдуть кадри (плюс три тики на інтерполяцію) або догорає
  /// спалах. Стіл у лобі, дограна партія, картка під іншою вкладкою — жодного rAF; новий кадр чи вид
  /// розбудять цикл самі. Раніше він крутився 60 разів на секунду, поки картка існувала.
  const LIVE_MS = TICK_MS * 3;
  function spin(st) {
    if (st.raf || !st.cv) return;
    st.raf = requestAnimationFrame(() => loop(st));
  }
  function loop(st) {
    st.raf = 0;
    if (!st.cv || !st.cv.el.isConnected || !st.cv.el.offsetParent) return;
    draw(st, !(st.ctx && st.ctx.playing));
    const now = performance.now();
    if (st.booms.length) st.booms = st.booms.filter((b) => now - b.at < BOOM_MS);
    if (now - st.seenAt < LIVE_MS || st.booms.length) spin(st);
  }

  HGames.register({
    id: 'tanks',
    added: '2026-09-18',          // нова гра: «🆕» у лобі два тижні тим, хто ще не грав (core.js, isNewGame)
    icon: ICON,
    seatNames: ['жовтий', 'зелений', 'рудий', 'сірий', 'синій', 'рожевий'],
    seatClass: ['x', 'o', 'c', 'd', 'tb', 'tp'],
    pad: { dirs: true, a: 'Space', anyBtn: true, hint: '{dpad} їхати · {a} бахнути, тримай — стріляє сам' },
    news: {
      v: '2026-09-29',
      title: 'Танчики: команди, хвилі 🤖, кущі й автовогонь',
      items: [
        '💥 Тримай пробіл чи «💥» — танк стріляє сам, щойно перезарядився; натиск трохи зарано теж не пропаде',
        '🏺 Опція «Грають»: команди з глеком-базою («Бережи базу») або разом проти хвиль ворожих 🤖',
        '🌳 Мапа з кущами (ховають танк) і ❄ льодом (ковзко); новий бонус 🪃 — снаряд відскакує від сталі за ріг',
        '🔥 Серії й 😈 помста в стрічці під полем, а наприкінці — підсумок: влучність, серії, хто кого діставав',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      st.interp = HGames.ui.Interp();
      st.cv = HGames.ui.canvas(root, { w: st.W * PX * st.K, h: st.H * PX * st.K, cls: 'tboard' });
      st.keyup = (e) => {
        if (isFire(e)) {
          if (st.fireDown && st.ctx && st.ctx.mine && st.ctx.playing) st.ctx.input('fire', { on: false });
          st.fireDown = false;
          return;
        }
        const d = dirOf(e);
        if (d === undefined || st.keys.indexOf(d) < 0) return;
        st.keys = st.keys.filter((k) => k !== d);
        steer(st, st.ctx);
      };
      // Вікно втратило фокус — keyup уже не прийде, і танк їхав би, поки не впреться. Відпускаємо все.
      st.blur = () => {
        st.keys = [];
        st.touch = -1;
        if ((st.fireDown || st.firePid !== null) && st.ctx && st.ctx.mine && st.ctx.playing) st.ctx.input('fire', { on: false });
        st.fireDown = false;
        st.firePid = null;
        steer(st, st.ctx);
      };
      document.addEventListener('keyup', st.keyup);
      window.addEventListener('blur', st.blur);
      // Картку сховали (лобі, інша вкладка сайту) й показали знову: rAF поки спав, а подія 'room' могла
      // прийти, поки її не було видно. Щойно канвас знову на екрані — малюємо свіже.
      if (window.IntersectionObserver) {
        st.io = new IntersectionObserver((es) => { if (es[es.length - 1].isIntersecting) spin(st); });
        st.io.observe(st.cv.el);
      }
      if (ctx.mine && ctx.playing) ctx.input('move', { dir: -1 });
      spin(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      st.ctx = ctx;
      if (!st.cv) return;
      st.pal = null;   // тема могла змінитись — палітру зберемо заново (подія 'room' рідка)
      const v = ctx.view;
      // На «Почати» мапа може стати більшою (шестеро) чи знову звичною (Ще раз учотирьох) — канвас за нею.
      if (v && v.width && v.height && (v.width !== st.W || v.height !== st.H)) {
        st.W = v.width;
        st.H = v.height;
        st.cv = HGames.ui.canvas(root, { w: st.W * PX * st.K, h: st.H * PX * st.K, cls: 'tboard' });
        st.interp.reset();
        st.booms = [];
      }
      if (v && v.walls && v.walls.length) st.walls = v.walls;
      // Кущі й лід не міняються за партію: беремо новий масив, лише коли він справді інший.
      if (v && v.p && !sameCells(st.bush, v.bush || null)) { st.bush = v.bush || null; st.bushSet = new Set(v.bush || []); }
      if (v && v.p && !sameCells(st.ice, v.ice || null)) st.ice = v.ice || null;
      if (ctx.room && ctx.room.startedAt && ctx.room.startedAt !== st.startKey) {
        st.startKey = ctx.room.startedAt;                // нова партія — стара помста й стрічка не тягнуться
        st.nemesis = -1;
        st.feed = [];
      }
      // Вид накладаємо рівно раз. Каркас кличе update() і на кожну подію лобі, з тим самим давно збереженим
      // видом, а вид танчиків приходить лише на старті й наприкінці партії. Раніше такий старий вид ставав
      // «останнім кадром»: танки на мить відскакували на старти, а наступний кадр бачив «живий → підбитий»
      // у тих, кого підбили давно, — і на полі спалахували вибухи, яких не було.
      if (v && v.p && v !== st.seenView) {
        st.seenView = v;
        const jump = !st.last || v.t < (st.last.t || 0) || (st.last.bricks && v.bricks && st.last.bricks.length < v.bricks.length);
        if (!jump) noteBooms(st, v);
        st.last = v;
        if (jump) st.interp.reset();
        st.interp.push(v);
      }
      pad(root, ctx, st);
      hud(root, ctx, st.last, st);
      feed(root, ctx, st);
      summary(root, ctx, st);
      fitPhone(root, st, ctx, '.thud', '.tpad');
      syncHeld(st);
      st.cv.resize();
      spin(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      st.ctx = ctx;
      if (!st.cv || !f) return;
      noteBooms(st, f);
      takeEvents(st, ctx, f);
      st.last = f;
      st.seenAt = performance.now();
      st.interp.push(f);
      hud(root, ctx, f, st);
      feed(root, ctx, st);
      syncHeld(st);
      spin(st);
    },

    onKey(e, ctx) {
      const st = ctx._tanks;
      if (!st || !ctx.mine || !ctx.playing) return false;
      if (isFire(e)) {
        if (!st.fireDown) { st.fireDown = true; ctx.input('fire', { on: true }); }
        return true;
      }
      const dir = dirOf(e);
      if (dir === undefined) return false;
      // Остання натиснута — головна; автоповтор тієї самої нічого не міняє й нічого не шле.
      if (st.keys[st.keys.length - 1] !== dir) st.keys = st.keys.filter((k) => k !== dir).concat(dir);
      steer(st, ctx);
      return true;
    },

    status(ctx) {
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f || !f.phase) return '';
      const v = ctx.view || {};
      if (f.phase === 'start') return v.note ? '⚠ ' + v.note : v.coop ? 'Готуйсь: 🤖 лізуть справа — бережіть 🏺 ліворуч!'
        : v.teams ? 'Готуйсь: розбий чужий 🏺 і бережи свій' : 'Готуйсь…';
      if (f.phase === 'over') return '';
      const how = HGames.ui.coarse() ? 'Хрестовина — їхати, тримай 💥 — стріляє сам' : 'Стрілки або WASD, тримай пробіл — стріляє сам';
      const goal = v.coop ? 'бережіть 🏺 від ' + (v.coop || 5) + ' хвиль 🤖' : v.teams ? 'розбий чужий 🏺 або більше фрагів за 3 хв'
        : 'до ' + (v.need || 5) + ' фрагів';
      return (ctx.mine ? how : 'Дивишся збоку') + ' · ' + goal;
    },

    unmount(root) {
      const st = root._tanks;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.io) st.io.disconnect();
      st.cv = null;
      root._tanks = null;
    },
  });
})();
