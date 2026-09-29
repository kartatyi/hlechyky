/*
  Корчма (tavern) — Unspottable у сільській корчмі. Реалтайм 25 Гц: сервер тикає раз на 40 мс і шле кадр, ми згладжуємо
  його й малюємо. Усіх відвідувачів — і ботів, і гравців, і себе — малюємо однаково, з кадрів через інтерполяцію: так
  свого не видасть ні лаг, ні «підсмикування». Передбачаємо лише косметику свого: поворот і анімацію ходи на keydown.

  Кадр (Impl/Tavern.cs): { t, ph: 'start'|'go'|'reveal'|'over', left, v: [x, y, d, s] × N, k: [x, y, d, mode], ev }
    s: 0 стоїть, 1 іде, 2 сидить, 3 замах, 4 п'є, 5 отетерів, 6 лежить, 7 вибув, 8 відкинуло; d: 0 → 1 ↓ 2 ← 3 ↑.
    k — корчмар: mode 0 порається, 1 гримає «Хто тут б'ється?!», 2 дивиться (махнеш кулаком — за двері).
    ev: [1, a, b, r, seat] удар (b −1 — у повітря; r 0 повітря, 1 упав, 2 відкинуло, 3 вибув + seat) · [2, place] хильнув
        · [3, id, seat] корчмар виніс за двері (seat ≥ 0 — і той вибув) · [4, 1] корчмар гримнув.
  Вид (Hidden, свій кожному): { phase, round, of, left, t, n, map, places, looks, names, v, k, seats, dead, me, reveal, result }
    me — лише своєму місцю: { id, hearts, places, mugs, cool, drinkCool, drink, sit, alive }.

  Ввід: Input('move', { dir }) лише на зміну (-1 — відпустив); Act('punch', {} | { dir }); Act('drink', {}); Act('sit', {}).
*/
(() => {
  // в'юпорт телефона — 4:3: на вузькому екрані вища мапа важить більше, ніж ширша
  const TICK_MS = 40, CELL = 32, WW = 960, WH = 640, PW = 480, PH = 360;
  const DX = [1, 0, -1, 0], DY = [0, 1, 0, -1];
  const REACH = 40, CONE = 500, CLOSE = 10;
  const PUNCH_COOL_MS = 1000, DRINK_COOL_MS = 2000, DRINK_MS = 1000, WIND_MS = 400;
  const HEARTS = 3, MUGS = 3;
  const PEEK_MS = 1500, BAM_MS = 450, BUBBLE_MS = 1300, GLINT_MS = 1200, NEWS_MS = 6000, LOCAL_MS = 250;
  // стіл стоїть (лобі, партію зіграно): стільки ще малюємо після останньої події — довше за найдовшу анімацію
  const IDLE_MS = 2000;
  // затиснуту стрілку підтверджуємо раз на секунду: сервер відпускає її сам, якщо 3 с не чув (обрив зв'язку)
  const HOLD_MS = 1000, TIP_MS = 1800, F5_PEEK_MS = 3000;
  const TAU = Math.PI * 2;

  // іконка: кухоль із піною й кулак
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.5 5.5h6v8h-6z" fill="var(--clay)"/><path d="M8.5 7h1.6a1.6 1.6 0 0 1 0 3.2H8.5" fill="none" stroke="var(--clay)" stroke-width="1.3"/>'
    + '<path d="M2 5.5c0-1.6 1.2-2.4 2.2-2 .6-1 2.2-1 2.8 0 1-.4 2 .4 2 2z" fill="var(--text)"/>'
    + '<rect x="11" y="2" width="4" height="4.4" rx="1.3" fill="var(--accent)"/><path d="M11.4 6.4v1.2h3.2V6.4" fill="none" stroke="var(--accent)" stroke-width="1"/></svg>';

  const SEAT_NAMES = ['жовтий', 'зелений', 'рудий', 'сірий', 'синій', 'рожевий', 'фіолетовий', 'червоний'];
  const SEAT_VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'],
    ['--tavern-blue', '#6fb3e8'], ['--tavern-pink', '#e88ac0'], ['--tavern-violet', '#b48ae8'], ['--tavern-red', '#e86a6a']];
  // Одяг — палітра села: червоний, синій, зелений, жовтий, білий, фіолетовий, помаранчевий, чорний.
  const CLOTH = ['#c0392b', '#2f6db5', '#3f9142', '#e8c33a', '#f2efe6', '#7d4fa8', '#e07b2a', '#2a2a2a'];
  const HAIR = ['#2b1d14', '#4a2f1d', '#7a4b27', '#c9a15a', '#8c4a2f', '#3b2a20', '#6b4a2e', '#1a1410'];
  const SKIN = ['#f3d0b0', '#e2b48c', '#c48a5e', '#8d5a3b'];
  const CLOTH_F = ['червоній', 'синій', 'зеленій', 'жовтій', 'білій', 'фіолетовій', 'помаранчевій', 'чорній'];
  const CLOTH_M = ['червоному', 'синьому', 'зеленому', 'жовтому', 'білому', 'фіолетовому', 'помаранчевому', 'чорному'];
  const HAT_WORD = ['хустці', 'капелюсі', 'картузі'];
  const FEMALE = new Set(['Параска', 'Ганна', 'Одарка', 'Марічка', 'Оксана', 'Домаха', 'Соломія', 'Мотря', 'Ярина', 'Христя',
    'Марта', 'Устя', 'Килина', 'Наталка', 'Пріська', 'Гафія', 'Софійка', 'Орися', 'Настя', 'Явдоха', 'Меланка', 'Феся', 'Текля',
    'Зоряна']);
  const OUCH = ['Ой!', 'Та ти що?!', 'Ах ти ж!', 'Рятуйте!', 'Ну, начувайся!', 'За що?!'];
  const OOF = ['Ай!', 'Ух!', 'Гей!', 'Ого!'];

  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  const KEY_DIRS = { d: 0, 'в': 0, s: 1, 'і': 1, a: 2, 'ф': 2, w: 3, 'ц': 3 };
  const key = (e) => String(e.key || '').toLowerCase();
  const dirOf = (e) => {
    const d = DIRS[e.code];
    return d !== undefined ? d : KEY_DIRS[key(e)];
  };
  const isPunch = (e) => e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar';
  const isDrink = (e) => e.code === 'KeyE' || e.code === 'KeyX' || e.code === 'Enter' || e.code === 'NumpadEnter'
    || (!e.code && ['e', 'x', 'у', 'ч', 'enter'].includes(key(e)));
  const isSit = (e) => e.code === 'KeyF' || e.code === 'KeyC' || (!e.code && ['f', 'c', 'а', 'с'].includes(key(e)));
  const isPeek = (e) => e.code === 'KeyQ' || e.code === 'Tab' || (!e.code && ['q', 'й'].includes(key(e)));

  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const readMute = () => { try { return localStorage.getItem('tavernMute') === '1'; } catch { return false; } };

  // ---------------------------------------------------------------------------------------------
  // Палітра і статика (долівка, стіни, шинквас, піч, бочки, столи, лави, ґанок) — один раз в offscreen-канвас
  // ---------------------------------------------------------------------------------------------

  function palette() {
    const c = HGames.ui.css;
    return {
      floor: c('--tavern-floor', '#8a6440'),
      floor2: c('--tavern-floor2', '#7a5634'),
      wall: c('--tavern-wall', '#4a3020'),
      wood: c('--tavern-wood', '#a0703f'),
      table: c('--tavern-table', '#b98a52'),
      yard: c('--tavern-yard', '#6f8a4e'),
      shadow: c('--tavern-shadow', 'rgba(20, 12, 5, .34)'),
      text: c('--text', '#ecf1ea'),
      accent: c('--accent', '#f4c542'),
      danger: c('--danger', '#e57373'),
      ink: '#1d160f',
      seats: SEAT_VARS.map(([n, f]) => c(n, f)),
    };
  }

  function lcg(seed) {
    let s = seed >>> 0;
    return () => ((s = (Math.imul(s, 1103515245) + 12345) >>> 0) / 4294967296);
  }

  /// Відрізки однакових літер у рядку: [x0, x1, y] — столи й лави малюємо цілими, а не клітинками.
  function runs(map, ch) {
    const out = [];
    for (let y = 0; y < map.length; y++) {
      const row = map[y];
      for (let x = 0; x < row.length; x++) {
        if (row[x] !== ch) continue;
        let e = x;
        while (e + 1 < row.length && row[e + 1] === ch) e++;
        out.push([x, e, y]);
        x = e;
      }
    }
    return out;
  }

  function drawStatic(map, places, pal, S, labelPx) {
    const c = document.createElement('canvas');
    c.width = Math.round(WW * S);
    c.height = Math.round(WH * S);
    const g = c.getContext('2d');
    g.scale(S, S);
    const at = (x, y) => (map[y] && map[y][x]) || '#';
    const rnd = lcg(2709);

    // долівка: дошки вздовж, шви й цвяшки
    g.fillStyle = pal.floor;
    g.fillRect(0, 0, WW, WH);
    for (let y = 0; y < WH; y += 16) {
      let x = -rnd() * 90;
      while (x < WW) {
        const len = 70 + rnd() * 110;
        g.fillStyle = rnd() < 0.35 ? pal.floor2 : 'rgba(255, 230, 190, ' + (rnd() * 0.06).toFixed(3) + ')';
        g.fillRect(x, y, len, 16);
        g.fillStyle = 'rgba(40, 22, 8, .45)';
        g.fillRect(x, y, 1.2, 16);
        g.fillRect(x + 3, y + 3, 1.2, 1.2); g.fillRect(x + 3, y + 12, 1.2, 1.2);
        x += len;
      }
      g.fillStyle = 'rgba(40, 22, 8, .38)';
      g.fillRect(0, y + 15, WW, 1);
    }

    // двір і ґанок
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (at(x, y) === ',') {
          g.fillStyle = pal.yard;
          g.fillRect(x * CELL, y * CELL, CELL, CELL);
        }
    g.fillStyle = 'rgba(40, 60, 20, .5)';
    for (let i = 0; i < 160; i++) {
      const x = CELL + rnd() * (WW - 2 * CELL), y = 17 * CELL + rnd() * 2 * CELL;
      g.fillRect(x, y, 1.2, -3 - rnd() * 3);
    }
    // ґанок перед дверима — дошки впоперек
    const door = runs(map, 'd')[0];
    if (door) {
      const x0 = door[0] * CELL - 16, x1 = (door[1] + 1) * CELL + 16;
      g.fillStyle = '#9b7247';
      g.fillRect(x0, (door[2] + 1) * CELL, x1 - x0, 20);
      g.strokeStyle = 'rgba(40, 22, 8, .45)';
      g.lineWidth = 1;
      g.beginPath();
      for (let x = x0; x < x1; x += 10) { g.moveTo(x, (door[2] + 1) * CELL); g.lineTo(x, (door[2] + 1) * CELL + 20); }
      g.stroke();
    }

    // приступки шинквасу й бочок — утоптано, світліше
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (at(x, y) === ':') {
          g.fillStyle = 'rgba(255, 220, 150, .12)';
          g.fillRect(x * CELL + 2, y * CELL + 2, CELL - 4, CELL - 4);
        }

    // закуток корчмаря за шинквасом — темніше, полиці з пляшками під стіною
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (at(x, y) === 'k') {
          g.fillStyle = 'rgba(30, 15, 5, .28)';
          g.fillRect(x * CELL, y * CELL, CELL, CELL);
        }
    const back = runs(map, 'k')[0];
    if (back) {
      const x0 = back[0] * CELL + 4, x1 = (back[1] + 1) * CELL - 4, y0 = back[2] * CELL + 2;
      g.fillStyle = '#6b4424';
      g.fillRect(x0, y0, x1 - x0, 8);
      const bottles = ['#2f7d4a', '#7a2a2a', '#c9a15a', '#3a5f8a', '#e8e0c8'];
      for (let x = x0 + 4; x < x1 - 6; x += 9 + rnd() * 6) {
        g.fillStyle = bottles[Math.floor(rnd() * bottles.length)];
        g.beginPath(); g.roundRect(x, y0 - 1, 5, 7, 1.5); g.fill();
      }
      // в'язки часнику й трав
      g.fillStyle = '#efe6cf';
      for (let x = x0 + 30; x < x1; x += 70) { g.beginPath(); g.arc(x, y0 + 13, 3, 0, TAU); g.arc(x + 5, y0 + 15, 3, 0, TAU); g.fill(); }
    }

    // стіни: зруб — колоди з торцями
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (at(x, y) === '#') {
          g.fillStyle = pal.wall;
          g.fillRect(x * CELL, y * CELL, CELL, CELL);
          g.fillStyle = 'rgba(255, 220, 170, .10)';
          for (let k = 0; k < 4; k++) g.fillRect(x * CELL, y * CELL + k * 8 + 1, CELL, 3);
          g.fillStyle = 'rgba(0, 0, 0, .25)';
          for (let k = 0; k < 4; k++) g.fillRect(x * CELL, y * CELL + k * 8 + 7, CELL, 1);
        }
    // вікна у верхній стіні
    for (const wx of [12, 19, 26]) {
      if (at(wx, 0) !== '#') continue;
      g.fillStyle = '#9cc7dd';
      g.fillRect(wx * CELL + 4, 6, CELL * 1.5, 18);
      g.strokeStyle = '#3a2412';
      g.lineWidth = 2;
      g.strokeRect(wx * CELL + 4, 6, CELL * 1.5, 18);
      g.beginPath(); g.moveTo(wx * CELL + 4 + CELL * 0.75, 6); g.lineTo(wx * CELL + 4 + CELL * 0.75, 24); g.stroke();
    }
    // двері: одвірки й розчинена стулка
    if (door) {
      const x0 = door[0] * CELL, x1 = (door[1] + 1) * CELL, y = door[2] * CELL;
      g.fillStyle = '#2b1a0e';
      g.fillRect(x0 - 5, y, 6, CELL); g.fillRect(x1 - 1, y, 6, CELL);
      g.fillStyle = '#8b3a2a';
      g.beginPath(); g.roundRect(x0 + 10, y + 6, x1 - x0 - 20, 20, 4); g.fill();   // рядно на порозі
    }
    // вивіска на ґанку
    if (door) {
      const sx = (door[1] + 3) * CELL, sy = (door[2] + 1) * CELL + 8;
      g.fillStyle = '#5a3a1e';
      g.fillRect(sx, sy, 3, 34);
      g.fillStyle = '#c9a15a';
      g.beginPath(); g.roundRect(sx - 30, sy + 4, 64, 18, 4); g.fill();
      g.fillStyle = '#3a2412';
      g.font = '700 11px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText('КОРЧМА', sx + 2, sy + 13.5);
    }

    // піч: біла глиняна, з челюстями й вогником
    const oven = runs(map, 'P');
    if (oven.length) {
      const x0 = oven[0][0] * CELL, x1 = (oven[0][1] + 1) * CELL, y0 = oven[0][2] * CELL, y1 = (oven[oven.length - 1][2] + 1) * CELL;
      g.fillStyle = 'rgba(0, 0, 0, .3)';
      g.beginPath(); g.roundRect(x0 + 3, y0 + 5, x1 - x0, y1 - y0, 10); g.fill();
      g.fillStyle = '#efe7d6';
      g.beginPath(); g.roundRect(x0 + 1, y0 + 1, x1 - x0 - 2, y1 - y0 - 2, 10); g.fill();
      g.strokeStyle = '#b8a888';
      g.lineWidth = 1.5;
      g.stroke();
      // розпис — калинка
      g.fillStyle = '#c0392b';
      for (let i = 0; i < 5; i++) { g.beginPath(); g.arc(x0 + 12 + i * 4, y0 + 12 + (i % 2) * 3, 2, 0, TAU); g.fill(); }
      g.fillStyle = '#3f9142';
      g.beginPath(); g.ellipse(x0 + 30, y0 + 13, 5, 2, 0.4, 0, TAU); g.fill();
      const mx = (x0 + x1) / 2, my = y1 - 14;
      g.fillStyle = '#2a1a10';
      g.beginPath(); g.roundRect(mx - 16, my - 10, 32, 20, [10, 10, 2, 2]); g.fill();
      g.fillStyle = '#f08a2a';
      g.beginPath(); g.ellipse(mx, my + 5, 10, 5, 0, 0, TAU); g.fill();
      g.fillStyle = '#ffd24a';
      g.beginPath(); g.ellipse(mx, my + 6, 5, 3, 0, 0, TAU); g.fill();
    }

    // столи: довгі, з мисками, хлібом і кухлями
    for (const [a, b, y] of runs(map, 'T')) {
      const x0 = a * CELL, x1 = (b + 1) * CELL, y0 = y * CELL;
      g.fillStyle = 'rgba(0, 0, 0, .3)';
      g.beginPath(); g.roundRect(x0 + 4, y0 + 6, x1 - x0, CELL - 2, 5); g.fill();
      g.fillStyle = pal.table;
      g.beginPath(); g.roundRect(x0 + 1, y0 + 2, x1 - x0 - 2, CELL - 4, 5); g.fill();
      g.strokeStyle = 'rgba(60, 35, 15, .35)';
      g.lineWidth = 1;
      g.beginPath();
      for (let k = 1; k < 4; k++) { g.moveTo(x0 + 4, y0 + 2 + k * 7); g.lineTo(x1 - 4, y0 + 2 + k * 7); }
      g.stroke();
      g.strokeStyle = 'rgba(40, 22, 8, .6)';
      g.lineWidth = 1.5;
      g.beginPath(); g.roundRect(x0 + 1, y0 + 2, x1 - x0 - 2, CELL - 4, 5); g.stroke();
      for (let x = x0 + 18; x < x1 - 10; x += 26 + rnd() * 14) {
        const r = rnd();
        if (r < 0.35) {          // миска
          g.fillStyle = '#e8dcc0'; g.beginPath(); g.arc(x, y0 + 16, 6, 0, TAU); g.fill();
          g.fillStyle = '#c0632a'; g.beginPath(); g.arc(x, y0 + 16, 3.6, 0, TAU); g.fill();
        } else if (r < 0.65) {   // кухоль
          g.fillStyle = '#8a5a33'; g.beginPath(); g.roundRect(x - 3.5, y0 + 11, 7, 9, 2); g.fill();
          g.fillStyle = '#fff6dc'; g.beginPath(); g.ellipse(x, y0 + 11.5, 3.6, 2, 0, 0, TAU); g.fill();
        } else if (r < 0.85) {   // хлібина
          g.fillStyle = '#c98a3a'; g.beginPath(); g.ellipse(x, y0 + 16, 8, 5, 0, 0, TAU); g.fill();
          g.strokeStyle = '#8a5a20'; g.lineWidth = 1;
          g.beginPath(); g.moveTo(x - 4, y0 + 13); g.lineTo(x - 2, y0 + 19); g.moveTo(x + 1, y0 + 13); g.lineTo(x + 3, y0 + 19); g.stroke();
        } else {                 // свічка
          g.fillStyle = '#f2efe6'; g.fillRect(x - 1.5, y0 + 12, 3, 8);
          g.fillStyle = '#ffd24a'; g.beginPath(); g.arc(x, y0 + 10.5, 2, 0, TAU); g.fill();
        }
      }
    }

    // лави: дошка ближче до столу
    for (const [a, b, y] of runs(map, 'b')) {
      const tableBelow = at(a, y + 1) === 'T';
      const x0 = a * CELL + 3, x1 = (b + 1) * CELL - 3, yy = tableBelow ? y * CELL + 15 : y * CELL + 3;
      g.fillStyle = 'rgba(0, 0, 0, .25)';
      g.fillRect(x0 + 2, yy + 3, x1 - x0, 14);
      g.fillStyle = pal.wood;
      g.beginPath(); g.roundRect(x0, yy, x1 - x0, 14, 3); g.fill();
      g.fillStyle = 'rgba(255, 230, 190, .16)';
      g.fillRect(x0 + 2, yy + 2, x1 - x0 - 4, 3);
      g.strokeStyle = 'rgba(40, 22, 8, .5)';
      g.lineWidth = 1;
      g.beginPath(); g.roundRect(x0, yy, x1 - x0, 14, 3); g.stroke();
    }

    for (const p of places || []) place(g, p, pal, labelPx || 9);
    return c;
  }

  /// Шинквас — довгий прилавок із кухлями й краником; бочка — лежача, з обручами й краником у бік приступки.
  function place(g, p, pal, labelPx) {
    const x = p.x * CELL, y = p.y * CELL, w = p.w * CELL, h = CELL;
    const toward = p.face === 3 ? 1 : -1;       // приступка під корпусом (3) чи над ним (1)
    if (p.i === 0) {
      g.fillStyle = 'rgba(0, 0, 0, .32)';
      g.fillRect(x + 3, y + 6, w, h);
      g.fillStyle = '#7a4b27';
      g.beginPath(); g.roundRect(x + 1, y + 2, w - 2, h - 4, 4); g.fill();
      g.fillStyle = '#9b6a3a';
      g.fillRect(x + 1, y + 2, w - 2, 9);
      g.strokeStyle = 'rgba(30, 15, 5, .6)';
      g.lineWidth = 1.5;
      g.beginPath(); g.roundRect(x + 1, y + 2, w - 2, h - 4, 4); g.stroke();
      for (let k = 0; k < 6; k++) {
        const mx = x + 26 + k * 38;
        g.fillStyle = '#8a5a33'; g.beginPath(); g.roundRect(mx - 4, y + 6, 8, 10, 2); g.fill();
        g.fillStyle = '#fff6dc'; g.beginPath(); g.ellipse(mx, y + 6.5, 4.2, 2.2, 0, 0, TAU); g.fill();
      }
      // краник
      g.fillStyle = '#c9a15a';
      g.fillRect(x + w - 34, y + 4, 6, 12);
      g.fillRect(x + w - 38, y + 14, 14, 3);
    } else {
      const cx = x + w / 2, cy = y + h / 2;
      g.fillStyle = 'rgba(0, 0, 0, .32)';
      g.beginPath(); g.ellipse(cx + 3, cy + 5, w / 2 - 1, h / 2 - 2, 0, 0, TAU); g.fill();
      g.fillStyle = '#c08a4c';
      g.beginPath(); g.roundRect(x + 1, y + 1, w - 2, h - 2, 14); g.fill();
      g.fillStyle = 'rgba(255, 235, 200, .22)';
      g.beginPath(); g.roundRect(x + 5, y + 4, w - 10, 7, 4); g.fill();
      g.strokeStyle = 'rgba(40, 20, 5, .5)';
      g.lineWidth = 1;
      g.beginPath();
      for (let k = 1; k < 5; k++) { g.moveTo(x + 5, y + 1 + k * 6); g.lineTo(x + w - 5, y + 1 + k * 6); }
      g.stroke();
      g.strokeStyle = '#2a1a10';
      g.lineWidth = 1.6;
      g.beginPath(); g.roundRect(x + 1, y + 1, w - 2, h - 2, 14); g.stroke();
      g.fillStyle = '#4a4a4a';
      g.fillRect(x + 10, y + 1, 5, h - 2); g.fillRect(x + w - 15, y + 1, 5, h - 2);
      // днище з краником у бік приступки
      g.fillStyle = '#6b4424';
      g.beginPath(); g.ellipse(cx, toward > 0 ? y + h - 5 : y + 5, 8, 3, 0, 0, TAU); g.fill();
      g.fillStyle = '#c9a15a';
      g.fillRect(cx - 2, toward > 0 ? y + h - 5 : y, 4, 5);
    }
    // табличка з назвою — з боку, протилежного приступці (там її не затулять ті, хто п'є)
    g.font = '600 ' + labelPx + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const label = (p.emoji ? p.emoji + ' ' : '') + p.name;
    const tw = g.measureText(label).width + labelPx * 0.9, th = labelPx + 4;
    let lx = x + w / 2, ly = toward > 0 ? y - th / 2 + 2 : y + h + th / 2 - 2;
    if (p.i === 0) ly = y + h / 2 + 5;                                  // шинквас — табличка на самій стійці
    else if (ly - th / 2 < 2) { ly = y + h / 2; lx = x + w + tw / 2 + 2; }   // під стелею місця нема — збоку
    if (lx + tw / 2 > WW - 2) lx = x - tw / 2 - 2;
    g.fillStyle = 'rgba(250, 244, 226, .94)';
    g.beginPath(); g.roundRect(lx - tw / 2, ly - th / 2, tw, th, 3); g.fill();
    g.strokeStyle = 'rgba(58, 36, 18, .35)';
    g.lineWidth = 1;
    g.stroke();
    g.fillStyle = '#3a2412';
    g.fillText(label, lx, ly + 0.5);
  }

  // ---------------------------------------------------------------------------------------------
  // Відвідувачі й корчмар
  // ---------------------------------------------------------------------------------------------

  /// Відвідувач у виді 3/4: ноги на ходу, сорочка з крайкою, руки, голова з обличчям у бік погляду, шапка. (x, y) —
  /// точка на долівці між ногами; фігурка стоїть над нею. Стан s міняє позу: сидить нижче, замах — кулак угорі, п'є —
  /// кухоль біля рота, отетерів — очі кружальцями, відкинуло — нахилений. Без save/restore, крім нахилу.
  function guest(g, x, y, d, s, look, now, id, ink) {
    if (s === 6 || s === 7) return lying(g, x, y, d, look, ink, s === 7);
    const hat = look[0], hc = look[1], shirt = look[2], skin = look[3];
    const side = d === 0 ? 1 : d === 2 ? -1 : 0;     // праворуч / ліворуч / анфас чи спиною
    let bob = 0, step = 0;
    if (s === 1) {
      const ph = Math.sin(now * 0.0503 + id * 1.7);
      bob = ph > 0 ? -1 : 0;
      step = ph > 0 ? 1 : -1;
    }
    const tilt = s === 8;
    if (tilt) {
      g.save();
      g.translate(x, y);
      g.rotate((d === 2 ? 1 : -1) * 0.35);
      g.translate(-x, -y);
    }
    const sit = s === 2;
    if (!sit) {
      g.fillStyle = '#3a2618';
      g.beginPath();
      if (side) {
        g.ellipse(x + side * 2.5 * step, y + 0.5, 2.6, 2, 0, 0, TAU);
        g.ellipse(x - side * 2.5 * step, y + 0.5, 2.6, 2, 0, 0, TAU);
      } else {
        g.ellipse(x - 3.2, y + 0.5 - (step > 0 ? 1.5 : 0), 2.3, 2.1, 0, 0, TAU);
        g.ellipse(x + 3.2, y + 0.5 - (step < 0 ? 1.5 : 0), 2.3, 2.1, 0, 0, TAU);
      }
      g.fill();
    }
    y += bob + (sit ? 5 : 0);
    const cloth = CLOTH[shirt] || CLOTH[0], skinC = SKIN[skin] || SKIN[0];
    g.fillStyle = cloth;
    g.beginPath(); g.roundRect(x - 7, y - 14, 14, sit ? 10 : 13, 4); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.stroke();
    g.fillStyle = 'rgba(0, 0, 0, .22)';
    if (!sit) g.fillRect(x - 7, y - 6, 14, 2);
    if (d === 1) {
      g.fillStyle = shirt === 0 ? '#f2efe6' : '#c0392b';
      g.fillRect(x - 1, y - 13, 2, 5);
    }
    // руки: за станом
    g.fillStyle = skinC;
    g.beginPath();
    const fx = DX[d] || 0, fy = DY[d] || 0;
    if (s === 3) {
      // замах: кулак відведений назад і вгору — видно здалеку
      g.arc(x - fx * 7 + (fy ? 7 : 0), y - 19 - 4, 3.4, 0, TAU);
    } else if (s === 4) {
      // п'є: рука з кухлем біля рота
      g.arc(x + (side || 1) * 6, y - 13, 2.2, 0, TAU);
    } else if (side) g.arc(x - side * 1 + side * 3 * step, y - 5, 2.2, 0, TAU);
    else { g.arc(x - 8, y - 6 + step, 2.2, 0, TAU); g.arc(x + 8, y - 6 - step, 2.2, 0, TAU); }
    g.fill();
    if (s === 3) {
      g.strokeStyle = ink;
      g.lineWidth = 1;
      g.stroke();
    }
    // голова
    const hx = x + side * 1.5, hy = y - 19;
    g.fillStyle = skinC;
    g.beginPath(); g.arc(hx, hy, 6.2, 0, TAU); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 0.9;
    g.stroke();
    g.fillStyle = ink;
    if (s === 5) {
      // отетерів: очі-кружальця
      g.lineWidth = 0.9;
      g.beginPath(); g.arc(hx - 2.2, hy + 0.6, 1.5, 0, TAU); g.moveTo(hx + 3.7, hy + 0.6); g.arc(hx + 2.2, hy + 0.6, 1.5, 0, TAU); g.stroke();
    } else if (d === 1) {
      g.fillRect(hx - 2.8, hy - 0.2, 1.6, 1.8);
      g.fillRect(hx + 1.2, hy - 0.2, 1.6, 1.8);
    } else if (side) {
      g.fillRect(hx + side * 2.2 - 0.8, hy - 0.4, 1.6, 1.8);
      g.fillStyle = skinC;
      g.beginPath(); g.arc(hx + side * 5.8, hy + 1.2, 1.5, 0, TAU); g.fill();
    }
    hatOn(g, hat, hc, hx, hy, d, side);
    if (s === 4) mug(g, x + (side || 1) * 8, y - 16);
    if (tilt) g.restore();
  }

  function hatOn(g, hat, hc, hx, hy, d, side) {
    const hcol = CLOTH[hc] || CLOTH[0];
    switch (hat) {
      case 0: // хустка з горошком
        g.fillStyle = hcol;
        g.beginPath();
        if (d === 3) g.arc(hx, hy, 6.9, 0, TAU);
        else if (side) g.ellipse(hx - side * 1.6, hy - 1.6, 6.4, 6.2, 0, Math.PI * 0.95, Math.PI * 2.05);
        else g.arc(hx, hy - 0.4, 6.9, Math.PI * 1.02, Math.PI * 1.98);
        g.fill();
        if (side) { g.beginPath(); g.arc(hx - side * 6.4, hy + 1.6, 2, 0, TAU); g.fill(); }
        else if (d === 3) { g.beginPath(); g.arc(hx, hy + 6.4, 2.2, 0, TAU); g.fill(); }
        g.fillStyle = 'rgba(255, 255, 255, .45)';
        g.fillRect(hx - 3, hy - 4.5, 1.3, 1.3); g.fillRect(hx + 1.5, hy - 3.2, 1.3, 1.3);
        break;
      case 1: // бриль
        g.fillStyle = hcol;
        g.beginPath(); g.ellipse(hx, hy - 3.5, 10, 3.6, 0, 0, TAU); g.fill();
        g.strokeStyle = 'rgba(0, 0, 0, .35)';
        g.lineWidth = 0.8;
        g.stroke();
        g.beginPath(); g.ellipse(hx, hy - 6, 5.4, 4.2, 0, Math.PI, TAU); g.closePath(); g.fill();
        g.fillStyle = 'rgba(0, 0, 0, .28)';
        g.fillRect(hx - 5.4, hy - 6.2, 10.8, 1.6);
        break;
      case 2: // картуз із козирком
        g.fillStyle = hcol;
        g.beginPath(); g.ellipse(hx, hy - 4.2, 6.6, 3.8, 0, Math.PI, TAU); g.closePath(); g.fill();
        g.fillRect(hx - 6.6, hy - 4.6, 13.2, 1.8);
        g.fillStyle = '#1e1e1e';
        g.beginPath();
        if (side) g.ellipse(hx + side * 6.6, hy - 3, 3.6, 1.3, 0, 0, TAU);
        else if (d === 1) g.ellipse(hx, hy - 2.5, 5.2, 1.6, 0, 0, TAU);
        g.fill();
        break;
      default: // чуб
        g.fillStyle = HAIR[hc] || HAIR[0];
        g.beginPath();
        if (d === 3) g.arc(hx, hy, 6.3, 0, TAU);
        else if (side) g.ellipse(hx - side * 1.4, hy - 1.8, 6, 5.4, 0, Math.PI * 0.9, Math.PI * 2.1);
        else g.arc(hx, hy - 0.8, 6.3, Math.PI * 1.05, Math.PI * 1.95);
        g.fill();
    }
  }

  /// Кухоль із піною.
  function mug(g, x, y) {
    g.fillStyle = '#8a5a33';
    g.beginPath(); g.roundRect(x - 3.5, y - 3, 7, 9, 1.8); g.fill();
    g.strokeStyle = '#5a3a1e';
    g.lineWidth = 1.2;
    g.beginPath(); g.arc(x + 4, y + 1.5, 2.3, -1.2, 1.2); g.stroke();
    g.fillStyle = '#fff6dc';
    g.beginPath(); g.ellipse(x, y - 3, 4, 2.2, 0, 0, TAU); g.fill();
  }

  /// Лежить: навзнак упоперек, голова вбік, очі ✕✕, шапка відлетіла. Вибулий — ще й блідий.
  function lying(g, x, y, d, look, ink, out) {
    const dir = d === 2 ? -1 : 1;
    if (out) g.globalAlpha = 0.72;
    g.fillStyle = '#3a2618';
    g.beginPath();
    g.ellipse(x - dir * 11, y - 3, 2.2, 2.4, 0, 0, TAU);
    g.ellipse(x - dir * 11, y + 2, 2.2, 2.4, 0, 0, TAU);
    g.fill();
    g.fillStyle = CLOTH[look[2]] || CLOTH[0];
    g.beginPath(); g.roundRect(x - 9, y - 6, 16, 11, 4); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.stroke();
    const hx = x + dir * 12, hy = y - 1;
    g.fillStyle = SKIN[look[3]] || SKIN[0];
    g.beginPath(); g.arc(hx, hy, 6, 0, TAU); g.fill();
    g.stroke();
    g.lineWidth = 1.1;
    g.beginPath();
    g.moveTo(hx - 3.5, hy - 2.5); g.lineTo(hx - 1, hy); g.moveTo(hx - 1, hy - 2.5); g.lineTo(hx - 3.5, hy);
    g.moveTo(hx + 1, hy - 2.5); g.lineTo(hx + 3.5, hy); g.moveTo(hx + 3.5, hy - 2.5); g.lineTo(hx + 1, hy);
    g.stroke();
    if (look[0] < 3) {
      g.fillStyle = CLOTH[look[1]] || CLOTH[0];
      g.beginPath(); g.ellipse(hx + dir * 9, hy + 6, 5, 3, 0.4 * dir, 0, TAU); g.fill();
    }
    g.globalAlpha = 1;
  }

  /// Корчмар: більший за відвідувачів, лисий, із вусами й білим фартухом. Гримає — червоніє й махає рушником.
  function barman(g, k, now, ink) {
    const x = k[0], y = k[1] + 4, d = k[2], mode = k[3];
    const side = d === 0 ? 1 : d === 2 ? -1 : 0;
    g.fillStyle = 'rgba(0, 0, 0, .3)';
    g.beginPath(); g.ellipse(x + 1, y + 5, 13, 5, 0, 0, TAU); g.fill();
    g.fillStyle = '#5a3a24';
    g.beginPath(); g.roundRect(x - 10, y - 18, 20, 18, 7); g.fill();       // кунтуш
    g.fillStyle = '#f5f0e2';
    g.beginPath(); g.roundRect(x - 7, y - 14, 14, 14, 4); g.fill();        // фартух
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.stroke();
    const face = mode === 1 ? '#e88a6a' : '#e8b48c';
    g.fillStyle = face;
    // руки: гримає — рушник угору
    if (mode === 1) {
      const wave = Math.sin(now / 70) * 3;
      g.beginPath(); g.arc(x + 11, y - 24 + wave, 3, 0, TAU); g.fill();
      g.fillStyle = '#f5f0e2';
      g.beginPath(); g.roundRect(x + 9, y - 36 + wave, 5, 12, 2); g.fill();
      g.fillStyle = face;
    } else { g.beginPath(); g.arc(x - 11, y - 9, 3, 0, TAU); g.arc(x + 11, y - 9, 3, 0, TAU); g.fill(); }
    const hx = x + side * 1.5, hy = y - 25;
    g.beginPath(); g.arc(hx, hy, 8, 0, TAU); g.fill();
    g.strokeStyle = ink;
    g.stroke();
    g.fillStyle = '#6b4a2e';                                               // вінчик волосся з боків
    g.beginPath(); g.arc(hx, hy + 1, 8, Math.PI * 0.85, Math.PI * 1.15); g.arc(hx, hy + 1, 8, -Math.PI * 0.15, Math.PI * 0.15); g.fill();
    if (d !== 3) {
      g.fillStyle = ink;
      if (mode === 2) {
        // дивиться: великі очі
        g.fillStyle = '#fff';
        g.beginPath(); g.arc(hx - 3, hy - 1, 2.6, 0, TAU); g.arc(hx + 3, hy - 1, 2.6, 0, TAU); g.fill();
        g.fillStyle = ink;
        g.beginPath(); g.arc(hx - 3, hy - 0.5, 1.2, 0, TAU); g.arc(hx + 3, hy - 0.5, 1.2, 0, TAU); g.fill();
      } else {
        g.fillRect(hx - 3.6 + side, hy - 1.5, 1.8, 2);
        g.fillRect(hx + 1.8 + side, hy - 1.5, 1.8, 2);
      }
      g.fillStyle = '#4a2f1d';                                             // вуса
      g.beginPath(); g.ellipse(hx - 3, hy + 3.5, 3.6, 1.5, 0.3, 0, TAU); g.ellipse(hx + 3, hy + 3.5, 3.6, 1.5, -0.3, 0, TAU); g.fill();
      if (mode === 1) { g.fillStyle = '#5a1a10'; g.beginPath(); g.ellipse(hx, hy + 6, 2.2, 1.6, 0, 0, TAU); g.fill(); }
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Стан модуля
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    let st = root._tavern;
    if (!st) {
      st = root._tavern = {
        root, ctx: null, cv: null, interp: HGames.ui.Interp(), mode: 'full', S: 1,
        view: null, n: 0, looks: [], names: [], map: null, mapKey: '', places: [], meId: -1, me: null,
        round: -1, vphase: '', last: null, lastAt: 0, fph: '', k: [0, 0, 1, 0], kmode: 0,
        px: new Float64Array(64), py: new Float64Array(64), pd: new Int8Array(64), ps: new Int8Array(64), order: [],
        stat: null, statKey: '', mini: null, pal: null, palAt: -1e9,
        cam: { x: WW / 2, y: WH / 2 }, box: [0, 0, WW, WH], drag: null, hover: false,
        keys: [], touchDir: -1, dir: -1, localDir: -1, localUntil: 0, peekUntil: 0, sentAt: 0,
        punchAt: -1e9, drinkAt: -1e9, drinkUntil: 0, tip: null, tipUntil: 0, autoPeek: false,
        bams: [], glints: [], bubbles: new Map(), swing: new Map(),
        raf: 0, keyup: null, blur: null, ro: null, io: null, onResize: null, visible: true,
        padOn: false, padH: 0, padAt: 0, labelPx: 9, lab: [],
        hudEl: null, clockEl: null, barEl: null, newsEl: null, sumEl: null, padEl: null, stageEl: null, seatsEl: null,
        audio: null, mute: readMute(),
        perf: { sum: 0, n: 0, max: 0 }, wakeAt: 0,
      };
    }
    st.ctx = ctx;
    ctx._tavern = st;
    return st;
  }

  const seatOf = (st, i) => (st.view && st.view.seats || []).find((s) => s.seat === i) || null;
  const nickOfSeat = (st, i) => { const s = seatOf(st, i); return (s && s.nick) || (st.ctx && st.ctx.nickOf(i)) || SEAT_NAMES[i] || '?'; };
  const lookOf = (st, id) => { const l = st.looks; return [l[id * 4] | 0, l[id * 4 + 1] | 0, l[id * 4 + 2] | 0, l[id * 4 + 3] | 0]; };
  const nameOf = (st, id) => (st.names && st.names[id]) || 'відвідувач';
  const alive = (st) => !!(st.me && st.me.alive);
  /// Фаза для малювання: кадри її несуть щотика, але коли партія скінчилась посеред раунду, кадрів більше нема.
  const phaseOf = (st) => (st.vphase === 'over' ? 'over' : st.fph || st.vphase);

  /// Відмова — тостом і підписом над своїм відвідувачем: на Деку тости ховаються під смужкою підказок пада.
  function refuse(st, text, toasted) {
    if (!text) return;
    if (!toasted && st.ctx) st.ctx.toast(text, 'err');
    st.tip = text === 'Підійди до шинквасу чи бочки' ? 'Стань на приступку біля шинквасу чи бочки' : text;
    st.tipUntil = performance.now() + TIP_MS;
    wake(st);
  }

  const cellAt = (st, x, y) => (st.map && st.map[Math.floor(y / CELL)] || '')[Math.floor(x / CELL)] || '#';

  /// «у синьому картузі» / «без шапки, у жовтій сорочці» — як описати забіяку в новинах.
  function looksLike(st, id) {
    const [hat, hc, shirt] = lookOf(st, id);
    if (hat >= 3) return 'без шапки, у ' + CLOTH_F[shirt] + ' сорочці';
    return 'у ' + (hat === 0 ? CLOTH_F[hc] : CLOTH_M[hc]) + ' ' + HAT_WORD[hat];
  }

  // ---------------------------------------------------------------------------------------------
  // Звуки (WebAudio, тихо, лише після першого жесту)
  // ---------------------------------------------------------------------------------------------

  function unlock(st) {
    if (st.audio || st.mute) return;
    try {
      const A = window.AudioContext || window.webkitAudioContext;
      if (A) st.audio = new A();
    } catch { st.audio = null; }
  }

  function sfx(st, kind) {
    const A = st.audio;
    if (!A || st.mute || document.hidden) return;
    try {
      const t = A.currentTime;
      const out = A.createGain();
      out.connect(A.destination);
      const tone = (f0, f1, dur, vol, type, at) => {
        const o = A.createOscillator();
        const t0 = t + (at || 0);
        o.type = type || 'sine';
        o.frequency.setValueAtTime(f0, t0);
        if (f1 !== f0) o.frequency.exponentialRampToValueAtTime(f1, t0 + dur);
        const gn = A.createGain();
        gn.gain.setValueAtTime(vol, t0);
        gn.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
        o.connect(gn); gn.connect(out);
        o.start(t0); o.stop(t0 + dur + 0.02);
      };
      if (kind === 'bam') {
        const len = Math.floor(A.sampleRate * 0.09);
        const buf = A.createBuffer(1, len, A.sampleRate);
        const ch = buf.getChannelData(0);
        for (let i = 0; i < len; i++) ch[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, 2);
        const src = A.createBufferSource();
        src.buffer = buf;
        const gn = A.createGain();
        gn.gain.value = 0.07;
        src.connect(gn); gn.connect(out);
        src.start(t);
        tone(140, 60, 0.1, 0.06, 'sine');
      } else if (kind === 'ouch') tone(520, 260, 0.16, 0.035, 'triangle');
      else if (kind === 'gulp') { tone(300, 520, 0.07, 0.03, 'sine'); tone(320, 560, 0.07, 0.025, 'sine', 0.1); }
      else if (kind === 'shout') { tone(180, 150, 0.22, 0.04, 'square'); tone(220, 170, 0.18, 0.03, 'square', 0.22); }
      else if (kind === 'door') tone(260, 90, 0.3, 0.04, 'sawtooth');
      else if (kind === 'out') tone(420, 110, 0.25, 0.05, 'triangle');
    } catch { /* без звуку гра повна */ }
  }

  // ---------------------------------------------------------------------------------------------
  // Вид і кадри
  // ---------------------------------------------------------------------------------------------

  function applyView(st, v) {
    if (!v) return;
    st.view = v;
    if (v.map) {
      const key = v.map.join('');
      if (key !== st.mapKey) { st.mapKey = key; st.map = v.map; st.places = v.places || []; st.statKey = ''; }
    }
    st.n = v.n | 0;
    st.looks = v.looks || [];
    st.names = v.names || [];
    st.me = v.me || null;
    st.meId = st.me ? st.me.id : -1;
    if (v.k) st.k = v.k;
    // новий раунд — і коли змінився номер, і коли після розкриття/кінця знову «роздивись»
    if (v.round !== st.round || (v.phase === 'start' && st.vphase && st.vphase !== 'start')) {
      st.round = v.round;
      st.camRound = -1;
      st.interp.reset();
      st.last = null;
      st.bams.length = 0;
      st.glints.length = 0;
      st.bubbles.clear();
      st.swing.clear();
      st.punchAt = st.drinkAt = -1e9;
      st.drinkUntil = 0;
    }
    if (st.me && st.me.drink > 0) st.drinkUntil = Math.max(st.drinkUntil, performance.now() + st.me.drink * TICK_MS);
    if (st.autoPeek && st.me) {
      if (v.phase === 'go' && st.me.alive) { st.peekUntil = performance.now() + F5_PEEK_MS; st.autoPeek = false; }
      else if (v.phase === 'start') st.autoPeek = false;
    }
    if (v.v && v.v.length) {
      const f = { t: v.t | 0, ph: v.phase, left: v.left | 0, v: v.v, k: v.k, ev: [] };
      const last = st.last;
      if (!last || last.v.length !== f.v.length || f.t > last.t || (f.t < last.t && f.ph === 'start')) push(st, f, false);
    }
    st.vphase = v.phase;
  }

  function push(st, f, fromFrame) {
    const last = st.last;
    if (last && (f.t < last.t || last.v.length !== f.v.length)) st.interp.reset();
    st.interp.push(f);
    st.last = f;
    if (f.k) st.k = f.k;
    if (fromFrame) st.lastAt = performance.now();
  }

  function onFrame(st, f) {
    if (!f || !f.v) return;
    const now = performance.now();
    push(st, f, true);
    if (f.ev && f.ev.length) events(st, f, now);
    const km = f.k ? f.k[3] | 0 : 0;
    if (km !== st.kmode) { st.kmode = km; barChip(st); }
    const walk = (f.ph === 'start' || f.ph === 'go') && st.dir >= 0 && st.ctx && st.ctx.mine && st.ctx.playing;
    if (f.ph !== st.fph) {
      if (walk) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
      st.fph = f.ph;
      barChip(st);
    }
    if (walk && now - st.sentAt > HOLD_MS) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
    if (st.localUntil && st.meId >= 0 && f.v[st.meId * 4 + 2] === st.localDir) st.localUntil = 0;
    paintClock(st, f);
  }

  function bubble(st, id, text, now) {
    st.bubbles.set(id, { text, until: now + BUBBLE_MS });
    if (st.bubbles.size > 16) st.bubbles.delete(st.bubbles.keys().next().value);
  }

  function events(st, f, now) {
    const esc = st.ctx.esc;
    for (const e of f.ev) {
      if (e[0] === 1) {
        const a = e[1], b = e[2], r = e[3], seat = e[4];
        st.swing.set(a, now);
        if (b < 0) continue;                 // у повітря — лише кулак, що вилетів
        st.bams.push({ x: f.v[b * 4], y: f.v[b * 4 + 1] - 16, at: now });
        if (st.bams.length > 10) st.bams.shift();
        const name = nameOf(st, b), she = FEMALE.has(name);
        if (r === 3) {
          news(st, '💥 <b class="tavern-s' + seat + '">' + esc(nickOfSeat(st, seat)) + '</b> вибуває — це ' + (she ? 'була ' : 'був ')
            + esc(name) + '! ' + (a === st.meId ? 'Твій кулак' : 'Кулак когось ' + looksLike(st, a)));
          sfx(st, 'out');
        } else if (r === 1) {
          bubble(st, b, OUCH[(b + f.t) % OUCH.length], now);
          news(st, '👊 Бам! ' + esc(name) + (she ? ' впала' : ' впав') + ' — '
            + (a === st.meId ? 'твій кулак, і тебе бачили' : 'від когось ' + looksLike(st, a)));
        } else {
          bubble(st, b, OOF[(b + f.t) % OOF.length], now);
          if (b === st.meId) news(st, '💢 Тебе відкинуло — хтось ' + looksLike(st, a) + ' дав кулаком');
          else news(st, '💨 ' + esc(name) + ' відлеті' + (she ? 'ла' : 'в') + ' від кулака когось ' + looksLike(st, a));
        }
        sfx(st, 'bam');
      } else if (e[0] === 2) {
        const p = st.places[e[1]];
        if (p) {
          st.glints.push({ k: e[1], at: now });
          if (st.glints.length > 10) st.glints.shift();
          news(st, p.emoji + ' Хтось хильнув ' + esc(p.what));
        }
        sfx(st, 'gulp');
      } else if (e[0] === 3) {
        const id = e[1], seat = e[2], name = nameOf(st, id), she = FEMALE.has(name);
        news(st, '🚪 Корчмар виставив за двері: ' + esc(name) + (seat >= 0 ? ' — і <b class="tavern-s' + seat + '">'
          + esc(nickOfSeat(st, seat)) + '</b> вибуває!' : id === st.meId ? ' — це ти, і мінус серце' : (she ? ' полетіла на ґанок' : ' полетів на ґанок')));
        sfx(st, 'door');
      } else if (e[0] === 4) {
        news(st, '😠 Корчмар: «Хто тут б\'ється?!» — не махай кулаками, поки дивиться');
        sfx(st, 'shout');
      }
    }
  }

  function news(st, html) {
    const el = st.newsEl;
    if (!el) return;
    const item = document.createElement('div');
    item.className = 'tavern-ni';
    item.innerHTML = html;
    el.insertBefore(item, el.firstChild);
    while (el.children.length > 2) el.lastChild.remove();
    setTimeout(() => item.remove(), NEWS_MS);
  }

  // ---------------------------------------------------------------------------------------------
  // Позиції, камера, досяжність кулака
  // ---------------------------------------------------------------------------------------------

  function grow(st, n) {
    if (st.px.length >= n) return;
    const m = Math.max(n, st.px.length * 2);
    st.px = new Float64Array(m); st.py = new Float64Array(m);
    st.pd = new Int8Array(m); st.ps = new Int8Array(m);
  }

  /// Інтерпольовані позиції всіх відвідувачів за id — однаково для всіх, свого теж. Повертає кількість.
  function positions(st, now) {
    const cur = st.interp.at();
    const b = (cur && cur.b) || st.last;
    if (!b || !b.v) return 0;
    let a = cur && cur.a;
    if (!a || !a.v || a.v.length !== b.v.length) a = b;
    const k = cur ? cur.t : 1;
    const va = a.v, vb = b.v, n = vb.length >> 2;
    grow(st, n);
    const { px, py, pd, ps } = st;
    for (let i = 0, j = 0; i < n; i++, j += 4) {
      const x0 = va[j], y0 = va[j + 1], x1 = vb[j], y1 = vb[j + 1];
      const jump = Math.abs(x1 - x0) > 12 || Math.abs(y1 - y0) > 12;       // виніс за двері — стрибок, не ковзання
      px[i] = jump ? x1 : x0 + (x1 - x0) * k;
      py[i] = jump ? y1 : y0 + (y1 - y0) * k;
      pd[i] = vb[j + 2];
      ps[i] = vb[j + 3] === 1 && st.vphase === 'over' ? 0 : vb[j + 3];
    }
    if (b.k && a.k) {
      st.kx = a.k[0] + (b.k[0] - a.k[0]) * k;
      st.k = b.k;
    }
    // косметика свого: миттєвий поворот і хода, поки сервер не підтвердив
    const me = st.meId;
    if (me >= 0 && me < n && now < st.localUntil && (ps[me] === 0 || ps[me] === 1)) {
      pd[me] = st.localDir;
      if (st.dir >= 0) ps[me] = 1;
    }
    // порядок малювання — за y (нижні поверх верхніх); вставками, бо відвідувачі майже впорядковані
    const order = st.order;
    if (order.length !== n) { order.length = 0; for (let i = 0; i < n; i++) order.push(i); }
    for (let i = 1; i < n; i++) {
      const id = order[i], y = py[id];
      let j = i - 1;
      while (j >= 0 && py[order[j]] > y) { order[j + 1] = order[j]; j--; }
      order[j + 1] = id;
    }
    return n;
  }

  /// Той самий кулак, що й на сервері (TavernCore.Reach): найближчий на ногах у ±45° на 40 перед мною або впритул.
  function reachTarget(st, n) {
    const me = st.meId;
    if (me < 0 || me >= n) return -1;
    const d = st.pd[me], fx = DX[d] || 0, fy = DY[d] || 0, x = st.px[me], y = st.py[me];
    let best = -1, bestD = Infinity;
    for (let i = 0; i < n; i++) {
      if (i === me || st.ps[i] === 6 || st.ps[i] === 7) continue;
      const dx = st.px[i] - x, dy = st.py[i] - y, d2 = dx * dx + dy * dy;
      if (d2 > REACH * REACH) continue;
      if (d2 > CLOSE * CLOSE) {
        const dot = dx * fx + dy * fy;
        if (dot <= 0 || dot * dot * 1000 < CONE * d2) continue;
      }
      if (d2 < bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  /// Лівий верхній кут в'юпорта в одиницях світу (у режимі всієї мапи — нуль).
  function camera(st, n) {
    const box = st.box;
    if (st.mode !== 'port') { box[0] = 0; box[1] = 0; box[2] = WW; box[3] = WH; return box; }
    const me = st.meId;
    const follow = me >= 0 && me < n && !st.drag && st.ctx && st.ctx.mine && (alive(st) || st.vphase === 'start');
    if (follow) { st.cam.x = st.px[me]; st.cam.y = st.py[me]; }
    else if (!st.drag && st.vphase === 'reveal' && st.view && st.view.reveal && st.camRound !== st.round) {
      st.camRound = st.round;
      const r = st.view.reveal, w = (r.winners || [])[0];
      const hit = (r.ids || []).find((p) => p.seat === w) || (r.ids || [])[0];
      if (hit && hit.id >= 0 && hit.id < n) { st.cam.x = st.px[hit.id]; st.cam.y = st.py[hit.id]; }
    }
    box[0] = clamp(st.cam.x - PW / 2, 0, WW - PW);
    box[1] = clamp(st.cam.y - PH / 2, 0, WH - PH);
    box[2] = PW;
    box[3] = PH;
    return box;
  }

  // ---------------------------------------------------------------------------------------------
  // Малювання кадру
  // ---------------------------------------------------------------------------------------------

  function ensureStatic(st, now) {
    if (st.stat && st.statKey && now - st.palAt <= 1000) return true;
    st.pal = palette();
    st.palAt = now;
    if (!st.map) return false;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const S = Math.min(2, dpr);
    const key = S + '|' + st.labelPx + '|' + st.mapKey.length + '|' + st.pal.floor + st.pal.wall + st.pal.table + st.pal.yard;
    if (st.stat && st.statKey === key) return true;
    st.S = S;
    st.stat = drawStatic(st.map, st.places, st.pal, S, st.labelPx);
    st.statKey = key;
    const m = document.createElement('canvas');
    m.width = Math.round(96 * dpr);
    m.height = Math.round(64 * dpr);
    const mg = m.getContext('2d');
    mg.imageSmoothingQuality = 'high';
    mg.drawImage(st.stat, 0, 0, m.width, m.height);
    st.mini = m;
    return true;
  }

  function draw(st) {
    const t0 = performance.now();
    const cv = st.cv;
    if (!cv || !ensureStatic(st, t0)) return;
    const g = cv.ctx, pal = st.pal, now = t0;
    const n = positions(st, now);
    const box = camera(st, n), cx = box[0], cy = box[1], vw = box[2], vh = box[3];
    const bw = cv.el.width, bh = cv.el.height, k = bw / cv.w;
    const S = st.S;

    g.setTransform(1, 0, 0, 1, 0, 0);
    g.imageSmoothingEnabled = true;
    g.drawImage(st.stat, cx * S, cy * S, vw * S, vh * S, 0, 0, bw, bh);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);

    const phase = phaseOf(st);
    const playing = !!(st.ctx && st.ctx.playing);
    const mine = !!(st.ctx && st.ctx.mine) && st.meId >= 0 && st.meId < n;

    placeMarks(st, g, pal, now, phase, mine);
    glints(st, g, now);
    trails(st, g, pal, phase);

    const peek = mine && (phase === 'start' || (phase === 'go' && now < st.peekUntil));
    if (peek) ring(g, st.px[st.meId], st.py[st.meId], pal, now);

    // корчмар — за шинквасом, завжди вище за залу
    if (st.k) {
      const kk = st.k, bx = st.kx != null ? st.kx : kk[0];
      barman(g, [bx, kk[1], kk[2], kk[3]], now, pal.ink);
      if (kk[3] === 2 && phase === 'go') {
        g.font = '18px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';
        g.textAlign = 'center';
        g.textBaseline = 'middle';
        g.fillText('👀', bx, kk[1] - 44);
      } else if (kk[3] === 1 && phase === 'go') {
        g.font = '800 12px system-ui, sans-serif';
        const text = 'Хто тут б\'ється?!', tw = g.measureText(text).width + 14;
        const tx = clamp(bx + 30, tw / 2 + 36, 330);
        g.fillStyle = 'rgba(255, 250, 238, .97)';
        g.strokeStyle = pal.ink;
        g.lineWidth = 1.2;
        g.beginPath(); g.roundRect(tx - tw / 2, kk[1] - 58, tw, 20, 9); g.fill(); g.stroke();
        g.fillStyle = '#8b1a10';
        g.textAlign = 'center';
        g.textBaseline = 'middle';
        g.fillText(text, tx, kk[1] - 47.5);
      }
    }

    g.fillStyle = pal.shadow;
    g.beginPath();
    for (let i = 0; i < n; i++) {
      const x = st.px[i], y = st.py[i];
      if (x < cx - 20 || x > cx + vw + 20 || y < cy - 20 || y > cy + vh + 30) continue;
      g.moveTo(x + 11, y + 5);
      g.ellipse(x + 1, y + 5, 10, 4, 0, 0, TAU);
    }
    g.fill();
    const look = [0, 0, 0, 0];
    for (let o = 0; o < n; o++) {
      const i = st.order[o];
      const x = st.px[i], y = st.py[i];
      if (x < cx - 20 || x > cx + vw + 20 || y < cy - 20 || y > cy + vh + 30) continue;
      look[0] = st.looks[i * 4] | 0; look[1] = st.looks[i * 4 + 1] | 0; look[2] = st.looks[i * 4 + 2] | 0; look[3] = st.looks[i * 4 + 3] | 0;
      guest(g, x, y + 4, st.pd[i], st.ps[i], look, now, i, pal.ink);
      const sw = st.swing.get(i);
      if (sw !== undefined) {
        if (now - sw > 220) st.swing.delete(i);
        else fist(g, x, y + 4, st.pd[i], pal.ink, st.looks[i * 4 + 3] | 0);
      }
    }
    stars(st, g, now, n);
    bams(st, g, now);
    bubbles(st, g, pal, now, n);

    if (mine && alive(st) && phase === 'go' && playing && st.ps[st.meId] < 5 && st.ps[st.meId] !== 2) {
      const t = reachTarget(st, n);
      if (t >= 0) target(g, st.px[t], st.py[t] - 30, pal);
      if (now < st.drinkUntil) drinkRing(g, st.px[st.meId], st.py[st.meId], pal, 1 - (st.drinkUntil - now) / DRINK_MS);
    }
    labels(st, g, pal, n, phase);

    g.setTransform(k, 0, 0, k, 0, 0);
    shade(st, g, pal, cv.w, cv.h, cx, cy, phase, playing, mine, now);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);
    if (peek) meArrow(g, st.px[st.meId], st.py[st.meId], pal, true);
    if (mine && now < st.tipUntil && st.tip) tipPlate(g, st, st.px[st.meId], st.py[st.meId], pal);
    g.setTransform(k, 0, 0, k, 0, 0);
    if (st.mode === 'port') minimap(st, g, pal, cv.w, cx, cy, vw, vh, now, mine);
    if (playing && st.lastAt && now - st.lastAt > 600 && phase !== 'over') {
      g.font = '600 12px system-ui, sans-serif';
      g.textAlign = 'left';
      g.textBaseline = 'top';
      outlined(g, 'з\'єднання…', 8, 8, pal.text, pal.ink);
    }

    const dt = performance.now() - t0;
    st.perf.sum += dt;
    st.perf.n++;
    if (dt > st.perf.max) st.perf.max = dt;
  }

  function fitFont(g, text, maxW, px, weight) {
    g.font = weight + ' ' + px + 'px system-ui, sans-serif';
    const w = g.measureText(text).width;
    if (w > maxW) g.font = weight + ' ' + Math.max(9, Math.floor((px * maxW) / w)) + 'px system-ui, sans-serif';
  }

  function outlined(g, text, x, y, fill, ink) {
    g.lineWidth = 3.5;
    g.lineJoin = 'round';
    g.strokeStyle = ink;
    g.strokeText(text, x, y);
    g.fillStyle = fill;
    g.fillText(text, x, y);
  }

  /// Кулак, що вилетів уперед (0,2 с після «бам»).
  function fist(g, x, y, d, ink, skin) {
    const fx = DX[d] || 0, fy = DY[d] || 0;
    g.fillStyle = SKIN[skin] || SKIN[0];
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.beginPath(); g.arc(x + fx * 14, y - 12 + fy * 8, 3.8, 0, TAU); g.fill(); g.stroke();
  }

  /// Ще не випиті мною місця — ледь помітний пульс (бачу лише я).
  function placeMarks(st, g, pal, now, phase, mine) {
    if (!mine || !st.me || !alive(st) || (phase !== 'go' && phase !== 'start')) return;
    const a = 0.3 + 0.22 * Math.sin(now / 260);
    g.strokeStyle = pal.accent;
    g.lineWidth = 2;
    g.globalAlpha = a;
    g.setLineDash([5, 4]);
    g.beginPath();
    const done = st.me.places || [];
    for (const p of st.places) {
      if (done[p.i]) continue;
      const top = Math.min(p.y, p.face === 3 ? p.y : p.y - 1), x = p.x * CELL;
      g.roundRect(x - 3, top * CELL - 2, p.w * CELL + 6, 2 * CELL + 4, 7);
    }
    g.stroke();
    g.setLineDash([]);
    g.globalAlpha = 1;
  }

  /// Хтось хильнув: кухоль злітає над місцем, піна бризкає.
  function glints(st, g, now) {
    const list = st.glints;
    for (let i = list.length - 1; i >= 0; i--) if (now - list[i].at > GLINT_MS) list.splice(i, 1);
    for (const f of list) {
      const p = st.places[f.k];
      if (!p) continue;
      const t = (now - f.at) / GLINT_MS;
      g.globalAlpha = 1 - t;
      g.fillStyle = '#ffe28a';
      g.beginPath(); g.arc(p.fx, p.fy, 12 + t * 34, 0, TAU); g.fill();
      g.globalAlpha = Math.max(0, 1 - t * 1.4);
      mug(g, p.fx, p.fy - 20 - t * 16);
      g.globalAlpha = 1;
    }
  }

  function ring(g, x, y, pal, now) {
    g.strokeStyle = pal.accent;
    g.lineWidth = 2.5;
    g.setLineDash([5, 4]);
    g.lineDashOffset = -now / 60;
    g.beginPath(); g.ellipse(x, y + 5, 15, 7.5, 0, 0, TAU); g.stroke();
    g.setLineDash([]);
    g.lineDashOffset = 0;
  }

  function meArrow(g, x, y, pal, big) {
    const top = y - (big ? 32 : 28);
    g.fillStyle = pal.accent;
    g.strokeStyle = pal.ink;
    g.lineWidth = 1.5;
    g.beginPath();
    g.moveTo(x - 7, top - 9); g.lineTo(x + 7, top - 9); g.lineTo(x, top); g.closePath();
    g.fill(); g.stroke();
    if (big) {
      g.font = '800 13px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'bottom';
      outlined(g, 'ти', x, top - 10, pal.accent, pal.ink);
    }
  }

  /// П'ю: кільце над собою заповнюється за секунду — бачу лише я (іншим — просто хтось із кухлем).
  function drinkRing(g, x, y, pal, p) {
    const cx = x, cy = y - 44;
    g.fillStyle = 'rgba(20, 12, 5, .72)';
    g.beginPath(); g.arc(cx, cy, 12, 0, TAU); g.fill();
    g.lineWidth = 3.5;
    g.strokeStyle = pal.accent;
    g.beginPath(); g.arc(cx, cy, 12, -Math.PI / 2, -Math.PI / 2 + TAU * clamp(p, 0, 1)); g.stroke();
    mug(g, cx, cy + 1);
  }

  function tipPlate(g, st, x, y, pal) {
    const px = Math.max(12, 13 * (st.cssK ? 1 / st.cssK : 1));
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const w = g.measureText(st.tip).width + px, h = px + 8;
    const vx = st.box[0], vw = st.box[2];
    const tx = clamp(x, vx + w / 2 + 4, vx + vw - w / 2 - 4), ty = Math.max(st.box[1] + h, y - 54);
    g.fillStyle = 'rgba(20, 12, 5, .88)';
    g.beginPath(); g.roundRect(tx - w / 2, ty - h / 2, w, h, h / 2); g.fill();
    g.strokeStyle = pal.danger;
    g.lineWidth = 1.5;
    g.stroke();
    g.fillStyle = pal.text;
    g.fillText(st.tip, tx, ty + 0.5);
  }

  function trails(st, g, pal, phase) {
    const v = st.view, r = v && v.reveal;
    if (!r || !r.trails || (phase !== 'reveal' && phase !== 'over')) return;
    g.lineJoin = 'round';
    g.lineCap = 'round';
    for (const t of r.trails) {
      const p = t.pts || [];
      if (p.length < 4) continue;
      const col = pal.seats[t.seat] || pal.text;
      g.globalAlpha = 0.85;
      g.strokeStyle = 'rgba(20, 12, 5, .55)';
      g.lineWidth = 5;
      g.beginPath();
      g.moveTo(p[0], p[1]);
      for (let i = 2; i < p.length; i += 2) g.lineTo(p[i], p[i + 1]);
      g.stroke();
      g.strokeStyle = col;
      g.lineWidth = 2.5;
      g.setLineDash([6, 5]);
      g.stroke();
      g.setLineDash([]);
      g.fillStyle = col;
      g.beginPath(); g.arc(p[0], p[1], 3.5, 0, TAU); g.fill();
      g.globalAlpha = 1;
    }
  }

  /// Кого дістане мій кулак — кулачок над ним (бачу лише я).
  function target(g, x, y, pal) {
    g.fillStyle = pal.danger;
    g.strokeStyle = pal.ink;
    g.lineWidth = 1.5;
    g.beginPath();
    g.moveTo(x - 6, y - 6); g.lineTo(x + 6, y - 6); g.lineTo(x, y + 1); g.closePath();
    g.fill(); g.stroke();
  }

  /// Зірочки над тими, хто отетерів (5) чи лежить (6).
  function stars(st, g, now, n) {
    g.font = '10px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillStyle = '#ffe28a';
    for (let i = 0; i < n; i++) {
      const s = st.ps[i];
      if (s !== 5 && s !== 6) continue;
      const x = st.px[i], y = st.py[i] - (s === 5 ? 34 : 10);
      for (let k = 0; k < 3; k++) {
        const a = now / 140 + (k * TAU) / 3 + i;
        g.fillText('✦', x + Math.cos(a) * 9, y + Math.sin(a) * 3.5);
      }
    }
  }

  /// «БАМ!» — зірка з написом над тим, куди влетів кулак.
  function bams(st, g, now) {
    const list = st.bams;
    for (let i = list.length - 1; i >= 0; i--) if (now - list[i].at > BAM_MS) list.splice(i, 1);
    const quiet = reduced();
    for (const b of list) {
      const t = (now - b.at) / BAM_MS;
      const r = quiet ? 14 : 10 + t * 10;
      g.globalAlpha = 1 - t * t;
      g.fillStyle = '#ffd24a';
      g.strokeStyle = '#c0392b';
      g.lineWidth = 2;
      g.beginPath();
      for (let k = 0; k < 16; k++) {
        const a = (k / 16) * TAU, rr = k % 2 ? r * 0.55 : r;
        const x = b.x + Math.cos(a) * rr * 1.3, y = b.y + Math.sin(a) * rr;
        if (k) g.lineTo(x, y); else g.moveTo(x, y);
      }
      g.closePath();
      g.fill(); g.stroke();
      g.font = '900 11px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillStyle = '#7a1a10';
      g.fillText('БАМ!', b.x, b.y + 0.5);
      g.globalAlpha = 1;
    }
  }

  /// Хмаринки «Ой!» над збитими й відкинутими — однаково над ботом і гравцем.
  function bubbles(st, g, pal, now, n) {
    if (!st.bubbles.size) return;
    g.font = '700 11px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    for (const [id, b] of st.bubbles) {
      if (now > b.until) { st.bubbles.delete(id); continue; }
      if (id >= n) continue;
      const x = st.px[id], y = st.py[id] - (st.ps[id] >= 6 ? 26 : 46);
      const w = g.measureText(b.text).width + 12;
      g.fillStyle = 'rgba(255, 250, 238, .95)';
      g.strokeStyle = pal.ink;
      g.lineWidth = 1;
      g.beginPath(); g.roundRect(x - w / 2, y - 9, w, 18, 8); g.fill(); g.stroke();
      g.beginPath(); g.moveTo(x - 3, y + 9); g.lineTo(x + 1, y + 14); g.lineTo(x + 4, y + 9); g.fill();
      g.fillStyle = '#3a1a10';
      g.fillText(b.text, x, y + 0.5);
    }
  }

  /// Ніки над гравцями: над вибулими — завжди, над усіма — на розкритті. ⭐ — переможцю раунду, 🏆 — партії.
  function labels(st, g, pal, n, phase) {
    const v = st.view;
    if (!v) return;
    const open = v.reveal && (phase === 'reveal' || phase === 'over' || v.phase === 'reveal' || v.phase === 'over');
    const rows = open ? v.reveal.ids || [] : v.dead || [];
    if (!rows.length) return;
    const over = phase === 'over' || v.phase === 'over';
    const win = !open ? [] : over ? (v.result && v.result.winners) || [] : v.reveal.winners || [];
    const mark = over ? '🏆 ' : '⭐ ';
    g.lineWidth = 2.5;
    for (let r = 0; r < rows.length; r++) {
      const id = rows[r].id, seat = rows[r].seat;
      if (id < 0 || id >= n) continue;
      g.strokeStyle = pal.seats[seat] || pal.text;
      g.beginPath(); g.ellipse(st.px[id], st.py[id] + 5, 14, 7, 0, 0, TAU); g.stroke();
    }
    const px = st.mode === 'port' ? 15 : 13, h = px + 5;
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const lab = st.lab, pool = st.labPool || (st.labPool = []);
    lab.length = 0;
    for (let r = 0; r < rows.length; r++) {
      const id = rows[r].id, seat = rows[r].seat;
      if (id < 0 || id >= n) continue;
      const a = pool[lab.length] || (pool[lab.length] = { seat: 0, text: '', x: 0, y: 0, w: 0 });
      a.seat = seat;
      a.text = (win.includes(seat) ? mark : '') + nickOfSeat(st, seat);
      a.x = st.px[id];
      a.y = st.py[id] - (st.ps[id] >= 6 ? 16 : 36);
      a.w = g.measureText(a.text).width + 12;
      lab.push(a);
    }
    lab.sort((a, b) => b.y - a.y);
    for (let i = 1; i < lab.length; i++) {
      const a = lab[i];
      for (let moved = true, guard = 0; moved && guard < 8; guard++) {
        moved = false;
        for (let j = 0; j < i; j++) {
          const b = lab[j];
          if (Math.abs(a.x - b.x) * 2 < a.w + b.w && Math.abs(a.y - b.y) < h + 2) { a.y = b.y - h - 2; moved = true; }
        }
      }
    }
    for (const a of lab) {
      g.fillStyle = 'rgba(20, 12, 5, .84)';
      g.beginPath(); g.roundRect(a.x - a.w / 2, a.y - h / 2, a.w, h, h / 2); g.fill();
      g.fillStyle = pal.seats[a.seat] || pal.text;
      g.fillText(a.text, a.x, a.y + 0.5);
    }
  }

  function shade(st, g, pal, w, h, cx, cy, phase, playing, mine, now) {
    const v = st.view;
    if (!v) return;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const room = st.ctx && st.ctx.room;
    if (!playing && room && room.status === 'lobby') {
      g.fillStyle = 'rgba(20, 12, 5, .45)';
      g.fillRect(0, 0, w, h);
      if (st.mode === 'port') {
        fitFont(g, 'щойно господар натисне «Почати»', w * 0.9, 22, 700);
        outlined(g, 'Корчма відчиниться,', w / 2, h / 2 - 14, pal.text, pal.ink);
        outlined(g, 'щойно господар натисне «Почати»', w / 2, h / 2 + 14, pal.text, pal.ink);
      } else {
        const msg = 'Корчма відчиниться, щойно господар натисне «Почати»';
        fitFont(g, msg, w * 0.9, Math.round(w / 34), 700);
        outlined(g, msg, w / 2, h / 2, pal.text, pal.ink);
      }
      return;
    }
    const minPx = 14 / (st.cssK || 1);
    // корчмар дивиться — червонувата рамка по краю: махнеш кулаком — за двері
    if (phase === 'go' && st.k && st.k[3] === 2) {
      const a = 0.42 + 0.14 * Math.sin(now / 160);
      const grd = g.createRadialGradient(w / 2, h / 2, Math.min(w, h) * 0.42, w / 2, h / 2, Math.hypot(w, h) / 2);
      grd.addColorStop(0, 'rgba(200, 40, 20, 0)');
      grd.addColorStop(1, 'rgba(200, 40, 20, ' + (a + 0.1).toFixed(3) + ')');
      g.fillStyle = grd;
      g.fillRect(0, 0, w, h);
      g.strokeStyle = 'rgba(220, 50, 30, ' + (a + 0.25).toFixed(3) + ')';
      g.lineWidth = 6;
      g.strokeRect(3, 3, w - 6, h - 6);
    }
    if (phase === 'start') {
      if (mine) spotlight(st, g, w, h, cx, cy, 0.42);
      const left = st.last ? st.last.left : v.left;
      g.font = '800 ' + Math.round(h / 5) + 'px system-ui, sans-serif';
      outlined(g, String(Math.max(1, Math.ceil((left * TICK_MS) / 1000))), w / 2, h / 2, pal.text, pal.ink);
      const sub = mine ? 'Роздивись: ти — під стрілкою' : 'Корчма відчиняється…';
      const px = Math.round(Math.max(h / 26, minPx));
      fitFont(g, sub, w * 0.92, px, 700);
      outlined(g, sub, w / 2, h / 2 + h / 7, pal.text, pal.ink);
      if (mine) {
        const how = st.mode === 'port' ? '3 кухлі в різних місцях · або 👊' : 'Випий 3 кухлі в різних місцях або дай кулаком суперникам · не бийся, як корчмар дивиться';
        fitFont(g, how, w * 0.92, Math.round(px * 0.92), 600);
        outlined(g, how, w / 2, h / 2 + h / 7 + px * 1.5, pal.accent, pal.ink);
      }
      return;
    }
    if (phase === 'go' && mine && now < st.peekUntil) {
      const left = st.peekUntil - now;
      spotlight(st, g, w, h, cx, cy, 0.46 * Math.min(1, left / 300));
    }
    if (phase === 'reveal' || phase === 'over' || (!playing && v.phase === 'over')) {
      g.fillStyle = 'rgba(20, 12, 5, .28)';
      g.fillRect(0, 0, w, h);
      const title = phase === 'reveal' ? revealTitle(st, st.mode === 'port') : overTitle(st);
      // уся мапа — плашка між столами (там найменше ніків); на телефоні — угорі ліворуч від мінімапи
      const port = st.mode === 'port', left = port ? w - 108 : w, mid = left / 2;
      fitFont(g, title, left - (port ? 24 : 40), Math.round(Math.max(minPx, h / 22)), 800);
      const tw = g.measureText(title).width + 28;
      const th = Math.max(24, h / 13), ty = port ? 8 : Math.round(h * 0.47 - th / 2);
      g.fillStyle = 'rgba(20, 12, 5, .78)';
      g.beginPath(); g.roundRect(mid - tw / 2, ty, tw, th, 10); g.fill();
      g.fillStyle = pal.text;
      g.fillText(title, mid, ty + th / 2);
    }
  }

  function spotlight(st, g, w, h, cx, cy, alpha) {
    const x = st.px[st.meId] - cx, y = st.py[st.meId] - cy - 8;
    g.fillStyle = 'rgba(20, 12, 5, ' + alpha.toFixed(3) + ')';
    g.beginPath();
    g.rect(0, 0, w, h);
    g.arc(x, y, 60, 0, TAU, true);
    g.fill('evenodd');
  }

  function revealTitle(st, short) {
    const v = st.view, r = v && v.reveal;
    if (!r) return '';
    const head = short ? '' : 'Раунд ' + v.round + ' з ' + v.of + ' · ';
    const who = (r.winners || []).map((s) => nickOfSeat(st, s)).join(' і ');
    switch (r.why) {
      case 'mugs': return head + '🍺 Три кухлі — ' + who;
      case 'last': return head + '👊 На ногах лише ' + who;
      case 'time': return head + '⏱ Корчму зачиняють — найбільше випи' + (r.winners.length ? 'в(ла) ' : 'ли ') + who;
      default: return head + '⏱ Корчму зачиняють — раунд нічий';
    }
  }

  function overTitle(st) {
    const v = st.view, res = v && v.result;
    if (!res) return 'Партію зіграно';
    if (res.why === 'left') {
      return res.winners.length ? '🚪 Суперники розійшлись — перемога: ' + res.winners.map((s) => nickOfSeat(st, s)).join(', ')
        : '🚪 Усі розійшлись';
    }
    if (!res.winners.length) return '🤝 Нічия';
    return '🏆 Перемога: ' + res.winners.map((s) => nickOfSeat(st, s) + ' ' + (res.totals[s] | 0)).join(', ');
  }

  function minimap(st, g, pal, w, cx, cy, vw, vh, now, mine) {
    if (!st.mini) return;
    const mw = 96, mh = 64, x0 = w - mw - 6, y0 = 6;
    g.globalAlpha = 0.92;
    g.drawImage(st.mini, x0, y0, mw, mh);
    g.globalAlpha = 1;
    g.strokeStyle = 'rgba(0, 0, 0, .6)';
    g.lineWidth = 1;
    g.strokeRect(x0 - 0.5, y0 - 0.5, mw + 1, mh + 1);
    const sx = mw / WW, sy = mh / WH;
    for (const f of st.glints) {
      const p = st.places[f.k];
      if (!p) continue;
      g.fillStyle = '#ffd24a';
      g.globalAlpha = Math.max(0, 1 - (now - f.at) / GLINT_MS);
      g.beginPath(); g.arc(x0 + p.fx * sx, y0 + p.fy * sy, 4, 0, TAU); g.fill();
      g.globalAlpha = 1;
    }
    g.strokeStyle = '#fff';
    g.lineWidth = 1.2;
    g.strokeRect(x0 + cx * sx, y0 + cy * sy, vw * sx, vh * sy);
    if (mine) {
      g.fillStyle = pal.accent;
      g.beginPath(); g.arc(x0 + st.px[st.meId] * sx, y0 + st.py[st.meId] * sy, 2.2, 0, TAU); g.fill();
    }
  }

  // ---------------------------------------------------------------------------------------------
  // HUD, підсумок, годинник
  // ---------------------------------------------------------------------------------------------

  function paintClock(st, f) {
    const el = st.clockEl;
    if (!el) return;
    const ph = st.vphase === 'over' ? 'over' : f ? f.ph : st.vphase;
    const left = f ? f.left : (st.view && st.view.left) || 0;
    const text = ph === 'go' ? '⏱ ' + clock(left) : ph === 'start' ? '⏱ ' + clock(1500) : '⏱ —';
    if (el.textContent !== text) el.textContent = text;
    const hot = ph === 'go' && left * TICK_MS <= 10000;
    if (el.classList.contains('hot') !== hot) el.classList.toggle('hot', hot);
  }

  /// Фішка корчмаря: порається / гримає / дивиться — найважливіше, що треба знати, перш ніж махнути кулаком.
  function barChip(st) {
    const el = st.barEl;
    if (!el) return;
    const ph = phaseOf(st), m = ph === 'go' ? st.kmode : 0;
    const text = m === 2 ? '👀 Корчмар дивиться!' : m === 1 ? '😠 «Хто тут б\'ється?!»' : '🧔 Корчмар порається';
    if (el.textContent !== text) el.textContent = text;
    el.classList.toggle('warn', m === 1);
    el.classList.toggle('hot', m === 2);
  }

  function hud(st) {
    const ctx = st.ctx, v = st.view, el = st.hudEl;
    if (!el || !v) return;
    const me = v.me;
    let html = '<span class="tavern-chip">Раунд ' + Math.max(1, v.round | 0) + '/' + (v.of | 0) + '</span>';
    if (me) {
      html += '<span class="tavern-chip tavern-hearts" title="Твої серця — бачиш лише ти">';
      for (let i = 0; i < HEARTS; i++) html += '<i' + (i < me.hearts ? '' : ' class="lost"') + '>❤</i>';
      html += '</span><span class="tavern-chip tavern-mugs" title="Кухлі в різних місцях: три — і раунд твій"><small>Кухлі ' + (me.mugs | 0) + '/' + MUGS + ':</small>';
      for (const p of st.places) html += '<i' + (me.places && me.places[p.i] ? ' class="done"' : '') + ' title="' + ctx.esc(p.name) + '">' + p.emoji + '</i>';
      html += '</span>';
    }
    const seats = v.seats || [];
    const active = seats.filter((s) => !s.out);
    if (v.phase !== 'lobby') html += '<span class="tavern-chip">на ногах ' + active.filter((s) => s.alive).length + '/' + active.length + '</span>';
    html += '<button type="button" class="tavern-mute" data-pad-skip title="Звук">' + (st.mute ? '🔇' : '🔊') + '</button>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.querySelector('.tavern-chips').innerHTML = html;
    }
    let row = '';
    for (const s of seats) {
      row += '<span class="tavern-seat tavern-s' + s.seat + (s.alive ? '' : ' dead') + (s.out ? ' out' : '') + (s.seat === ctx.seat ? ' me' : '') + '">'
        + '<i></i>' + ctx.esc(s.nick) + ' <b>' + (s.total | 0) + '</b>' + (s.mugs != null ? ' <small>🍺' + (s.mugs | 0) + '</small>' : '') + '</span>';
    }
    const se = st.seatsEl;
    if (se && se.dataset.sig !== row) {
      se.dataset.sig = row;
      se.innerHTML = row;
    }
    barChip(st);
  }

  function placeSeats(root, st) {
    const se = st.seatsEl;
    if (!se) return;
    if (st.mode === 'port') {
      if (se.parentNode !== root || se.nextSibling) root.appendChild(se);
    } else if (se.parentNode !== st.hudEl) st.hudEl.appendChild(se);
  }

  /// Підсумок — рядком фішок за очками партії: разом, а дрібно — що дав раунд (кухлі, серця, вибиті).
  function summary(st) {
    const el = st.sumEl, v = st.view;
    if (!el || !v) return;
    let html = '';
    const r = v.reveal;
    const open = !!(r && (v.phase === 'reveal' || v.phase === 'over'));
    if (open) {
      const over = v.phase === 'over';
      const champ = over ? (v.result && v.result.winners) || [] : r.winners || [];
      const total = (s) => { const x = seatOf(st, s); return x ? x.total | 0 : 0; };
      const rows = (r.rows || []).slice().sort((a, b) => total(b.seat) - total(a.seat) || b.pts - a.pts || a.seat - b.seat);
      html = rows.map((x) => '<span class="tavern-sc tavern-s' + x.seat + (champ.includes(x.seat) ? ' win' : '') + '">'
        + (champ.includes(x.seat) ? (over ? '🏆 ' : '⭐ ') : '') + '<b>' + st.ctx.esc(nickOfSeat(st, x.seat)) + '</b> '
        + '<em>' + total(x.seat) + '</em> <small title="за раунд: кухлі, вибиті серця, вибиті гравці">+' + x.pts
        + ' · 🍺' + x.mugs + (x.hits ? ' · 👊' + x.hits : '') + (x.kos ? ' · 💥' + x.kos : '') + '</small></span>').join('');
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      el.hidden = !html;
    }
    if (st.stageEl) st.stageEl.classList.toggle('tavern-open', open);
  }

  function placeSum(root, st) {
    const el = st.sumEl;
    if (!el) return;
    if (st.mode === 'port') { if (el.parentNode !== root || el.previousSibling !== st.stageEl) st.stageEl.after(el); }
    else if (el.parentNode !== st.stageEl) st.stageEl.appendChild(el);
  }

  function placePad(root, st) {
    const el = st.padEl;
    if (!el) return;
    const after = st.sumEl && st.sumEl.parentNode === root ? st.sumEl : st.stageEl;
    if (el.previousSibling !== after) after.after(el);
  }

  // ---------------------------------------------------------------------------------------------
  // Керування
  // ---------------------------------------------------------------------------------------------

  function want(st) {
    const d = st.touchDir >= 0 ? st.touchDir : st.keys.length ? st.keys[st.keys.length - 1] : -1;
    if (d === st.dir) return;
    st.dir = d;
    if (d >= 0) { st.localDir = d; st.localUntil = performance.now() + LOCAL_MS; }
    const ctx = st.ctx;
    if (ctx && ctx.mine && ctx.playing) { ctx.input('move', { dir: d }); st.sentAt = performance.now(); }
  }

  const myState = (st) => (st.meId >= 0 && st.last && st.meId * 4 < st.last.v.length ? st.last.v[st.meId * 4 + 3] : -1);

  function punch(st, dir) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    const me = st.me;
    if (me && !me.alive) { refuse(st, 'Тебе вже винесли — дивись, хто кого'); return; }
    if (myState(st) === 2) { refuse(st, HGames.ui.coarse() ? 'Сидячи не розмахнешся — встань 🪑' : 'Сидячи не розмахнешся — встань (F)'); return; }
    if (now - st.punchAt < PUNCH_COOL_MS + WIND_MS) { refuse(st, 'Кулак ще не відпочив'); return; }
    if (dir != null) { st.localDir = dir; st.localUntil = now + LOCAL_MS; }
    ctx.act('punch', dir == null ? {} : { dir }).then((r) => {
      if (r && r.ok) st.punchAt = performance.now();
      else if (r) refuse(st, r.message, true);
    });
  }

  function drink(st) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    if (now < st.drinkUntil) return;
    if (now - st.drinkAt < DRINK_COOL_MS + DRINK_MS) { refuse(st, 'Дай духу перевести'); return; }
    ctx.act('drink', {}).then((r) => {
      if (r && r.ok) { st.drinkAt = performance.now(); st.drinkUntil = st.drinkAt + DRINK_MS; }
      else if (r) refuse(st, r.message, true);
    });
  }

  function sit(st) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    ctx.act('sit', {}).then((r) => { if (r && !r.ok) refuse(st, r.message, true); });
  }

  function peek(st) { st.peekUntil = performance.now() + PEEK_MS; wake(st); }

  function toWorld(st, e) {
    const r = st.cv.el.getBoundingClientRect();
    const [cx, cy, vw, vh] = st.box;
    return [cx + ((e.clientX - r.left) / r.width) * vw, cy + ((e.clientY - r.top) / r.height) * vh, r];
  }

  /// Клік у бочку, шинквас чи приступку перед ними.
  function pickPlace(st, x, y) {
    for (const p of st.places) {
      const top = (p.face === 3 ? p.y : p.y - 1) * CELL, bottom = top + 2 * CELL;
      if (x >= p.x * CELL - 6 && x <= (p.x + p.w) * CELL + 6 && y >= top - 6 && y <= bottom + 6) return p.i;
    }
    return -1;
  }

  /// Клік чи тап по мапі: у бочку/шинквас — кухоль; у лаву, на якій стою, — сісти; деінде — кулак у той бік.
  function clickAt(st, x, y) {
    if (pickPlace(st, x, y) >= 0) return drink(st);
    const me = st.meId;
    if (me < 0 || !st.last) return;
    const mx = st.px[me], my = st.py[me];
    const onBench = cellAt(st, mx, my) === 'b';
    if (onBench && Math.floor(x / CELL) === Math.floor(mx / CELL) && Math.floor(y / CELL) === Math.floor(my / CELL)) return sit(st);
    if (myState(st) === 2) return sit(st);                     // сидячи — будь-який клік: устати
    const dx = x - mx, dy = y - (my - 10);
    const dir = Math.abs(dx) >= Math.abs(dy) ? (dx >= 0 ? 0 : 2) : (dy >= 0 ? 1 : 3);
    punch(st, dir);
  }

  function canAct(st) {
    const ctx = st.ctx;
    return !!(ctx && ctx.mine && ctx.playing && alive(st) && phaseOf(st) === 'go');
  }

  function wireCanvas(st) {
    const el = st.cv.el;
    el.addEventListener('pointerdown', (e) => {
      wake(st);
      unlock(st);
      st.down = { id: e.pointerId, x: e.clientX, y: e.clientY, moved: false, cam: { x: st.cam.x, y: st.cam.y } };
      if (st.mode === 'port') {
        const [, , r] = toWorld(st, e);
        const lx = ((e.clientX - r.left) / r.width) * PW, ly = ((e.clientY - r.top) / r.height) * PH;
        if (lx >= PW - 102 && ly <= 72 && !(st.ctx && st.ctx.mine && alive(st))) {
          st.cam.x = ((lx - (PW - 102)) / 96) * WW;
          st.cam.y = ((ly - 6) / 64) * WH;
          st.down.moved = true;
        }
      }
    });
    el.addEventListener('pointermove', (e) => {
      wake(st);
      const d = st.down;
      if (d && d.id === e.pointerId) {
        const dx = e.clientX - d.x, dy = e.clientY - d.y;
        if (Math.abs(dx) + Math.abs(dy) > 6) d.moved = true;
        if (st.mode === 'port' && d.moved && !(st.ctx && st.ctx.mine && alive(st))) {
          const r = el.getBoundingClientRect();
          st.drag = true;
          st.cam.x = clamp(d.cam.x - (dx / r.width) * PW, PW / 2, WW - PW / 2);
          st.cam.y = clamp(d.cam.y - (dy / r.height) * PH, PH / 2, WH - PH / 2);
        }
        return;
      }
      if (e.pointerType !== 'mouse' || !canAct(st)) { if (st.hover) { st.hover = false; el.style.cursor = ''; } return; }
      const [x, y] = toWorld(st, e);
      const me = st.meId;
      let on = pickPlace(st, x, y) >= 0;
      if (!on && me >= 0) {
        const dx = x - st.px[me], dy = y - st.py[me];
        on = dx * dx + dy * dy <= 4 * REACH * REACH;
      }
      if (on !== st.hover) { st.hover = on; el.style.cursor = on ? 'pointer' : ''; }
    });
    const up = (e) => {
      const d = st.down;
      if (!d || d.id !== e.pointerId) return;
      st.down = null;
      st.drag = false;
      if (d.moved || e.type === 'pointercancel' || !canAct(st)) return;
      const [x, y] = toWorld(st, e);
      clickAt(st, x, y);
    };
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
  }

  function pad(root, st) {
    const ctx = st.ctx;
    let el = st.padEl;
    if (!ctx.mine) {
      if (el) { el.remove(); st.padEl = null; }
      return;
    }
    if (el) return;
    el = document.createElement('div');
    el.className = 'tavern-pad';
    const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
    el.innerHTML = '<div class="tavern-dirs" role="group" aria-label="хрестовина: тримай і веди пальцем">'
      + [3, 2, 0, 1].map((d) => '<span class="tavern-arr" data-dir="' + d + '">' + label[d] + '</span>').join('')
      + '<button type="button" class="tavern-peek" data-act="peek" aria-label="де я">👁</button>'
      + '</div><div class="tavern-acts">'
      + '<button type="button" data-act="punch" aria-label="кулак">👊</button>'
      + '<button type="button" data-act="drink" aria-label="кухоль">🍺</button>'
      + '<button type="button" data-act="sit" aria-label="сісти чи встати">🪑</button></div>';
    const dirs = el.querySelector('.tavern-dirs');
    const arrows = dirs.querySelectorAll('.tavern-arr');
    const light = (d) => arrows.forEach((a) => a.classList.toggle('on', +a.dataset.dir === d));
    const aim = (e) => {
      const r = dirs.getBoundingClientRect();
      const dx = e.clientX - (r.left + r.width / 2), dy = e.clientY - (r.top + r.height / 2);
      if (Math.abs(dx) < r.width * 0.12 && Math.abs(dy) < r.height * 0.12) return st.touchDir;
      return Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3);
    };
    el.addEventListener('pointerdown', (e) => {
      const b = e.target.closest('button');
      unlock(st);
      if (b) {
        e.preventDefault();
        if (b.dataset.act === 'punch') return punch(st, null);
        if (b.dataset.act === 'drink') return drink(st);
        if (b.dataset.act === 'sit') return sit(st);
        if (b.dataset.act === 'peek') return peek(st);
        return;
      }
      if (!e.target.closest('.tavern-dirs')) return;
      e.preventDefault();
      try { dirs.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
      st.touchPid = e.pointerId;
      st.touchDir = aim(e);
      light(st.touchDir);
      want(st);
    });
    dirs.addEventListener('pointermove', (e) => {
      if (st.touchPid !== e.pointerId) return;
      const d = aim(e);
      if (d === st.touchDir) return;
      st.touchDir = d;
      light(d);
      want(st);
    });
    const release = (e) => {
      if (st.touchPid !== e.pointerId) return;
      st.touchPid = null;
      st.touchDir = -1;
      light(-1);
      want(st);
    };
    dirs.addEventListener('pointerup', release);
    dirs.addEventListener('pointercancel', release);
    dirs.addEventListener('lostpointercapture', release);
    st.padEl = el;
    placePad(root, st);
  }

  function padStrip(st) {
    const hints = document.querySelector('.padhints');
    const on = !!(hints && !hints.hidden);
    if (on === st.padOn) return false;
    st.padOn = on;
    st.padH = on ? hints.offsetHeight + 8 : 0;
    return true;
  }

  /// Мапа 3:2 має влізти у вікно разом зі статусом і кнопками під нею (фішки на вісьмох — два рядки, на Деку — смужка пада).
  function sizeStage(st, mode) {
    const el = st.stageEl;
    if (!el || !el.isConnected) return;
    if (mode === 'port') { if (el.style.maxWidth) el.style.maxWidth = ''; return; }
    const r = el.getBoundingClientRect();
    const card = el.closest('.gtable');
    const below = Math.max(48, card ? card.getBoundingClientRect().bottom - r.bottom : 92) + 22;
    const h = window.innerHeight - (r.top + (window.scrollY || 0)) - below - st.padH;
    const w = clamp(Math.floor(h * 1.5), 480, 960);
    const cur = parseFloat(el.style.maxWidth) || 0;
    if (Math.abs(cur - w) >= 3) el.style.maxWidth = w + 'px';
  }

  /// Режим камери — за шириною картки: < 600 px — в'юпорт за своїм, > 640 — уся корчма (між ними — як було).
  function fit(root, st) {
    wake(st);
    padStrip(st);
    const cw = root.clientWidth;
    let mode = st.mode;
    // Телефон лежачи (body.g-land): мапа — посередині між хрестовиною й кнопками, заввишки з екран (~300 px), тож
    // уся корчма там дрібна — беремо в'юпорт за своїм, як стоячи.
    if (document.body.classList.contains('g-land')) mode = 'port';
    else if (cw) mode = cw < 600 ? 'port' : cw > 640 ? 'full' : st.mode;
    sizeStage(st, mode);
    if (!st.cv || mode !== st.mode) {
      st.mode = mode;
      st.cv = HGames.ui.canvas(st.stageEl, mode === 'port' ? { w: PW, h: PH, cls: 'tavern-board tavern-port' } : { w: WW, h: WH, cls: 'tavern-board' });
      st.stageEl.classList.toggle('tavern-portmode', mode === 'port');
      placeSum(root, st);
      placePad(root, st);
      placeSeats(root, st);
    } else st.cv.resize();
    const css = st.cv.el.clientWidth;
    if (css) {
      st.cssK = css / (mode === 'port' ? PW : WW);
      const lp = clamp(Math.round(10.5 / st.cssK), 9, 16);
      if (lp !== st.labelPx) { st.labelPx = lp; st.statKey = ''; }
    }
  }

  /// Телефон: шапка столу з вісьмома місцями штовхала мапу вниз, і кнопки опинялись під нижнім меню — видно було
  /// або мапу, або кнопки. Раз на партію (room.startedAt), коли вона пішла, прокручуємо сторінку так, щоб рядок стану
  /// гри став під шапку сайту: тоді мапа й кнопки вміщаються разом. Якщо й так усе видно — не чіпаємо.
  function fitPhone(st) {
    const ctx = st.ctx, padEl = st.padEl;
    if (!ctx || !ctx.mine || !ctx.playing || !ctx.room || !st.hudEl || !padEl || !HGames.ui.coarse()) return;
    const key = ctx.room.startedAt || '';
    if (st.fitFor === key) return;
    const a = st.hudEl.getBoundingClientRect(), b = padEl.getBoundingClientRect();
    if (!a.height || !b.height) return;              // картку чи кнопки зараз не видно — спробуємо на наступному виді
    st.fitFor = key;
    // верх і низ видимого місця: шапка сайту, а внизу меню, міні-плеєр і згорнута шторка «💬 Стіл» (--gdock-h каркаса;
    // у зануреному режимі g-imm їх нема — 0)
    const fit = HGames.ui.fit ? HGames.ui.fit() : null;
    const head = document.querySelector('header');
    const top = (fit ? fit.top : (head ? head.getBoundingClientRect().bottom : 0)) + 4;
    // --tabs-h — calc(58px + safe-area), parseFloat з нього дає 0; --gdock-h каркас пише числом (весь зайнятий низ)
    const tabs = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--gdock-h')) || 64;
    const fab = document.querySelector('.tchat.drawer:not(.open) .tc-head');
    const fr = fab && fab.getBoundingClientRect();
    const limit = (fit ? innerHeight - fit.dock : fr && fr.height ? Math.min(fr.top, innerHeight - tabs) : innerHeight - tabs) - 6;
    const lo = b.bottom - limit, hi = a.top - top;   // на скільки прокрутити: не менше lo, не більше hi
    const dy = lo <= hi ? Math.min(Math.max(0, lo), hi) : lo;   // не влазить усе — кнопки важливіші за рядок стану
    if (Math.abs(dy) < 2) return;
    const calm = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    window.scrollBy({ top: dy, behavior: calm ? 'auto' : 'smooth' });
  }

  /// rAF живе, лише поки є що малювати: іде партія (кадри 25 Гц і інтерполяція між ними) або ще доживають анімації
  /// після останньої події (усе коротше за IDLE_MS). Лобі й дограний стіл — статичні: цикл засинає (раніше малював ту
  /// саму картинку 60 разів на секунду). Мапи не видно (інша вкладка сайту, прокрутили геть) — теж спить. Будять вид,
  /// кадр, розкладка, мишка чи палець на мапі, «де я?», відмова з підписом і поява мапи на екрані.
  function wake(st) { st.wakeAt = performance.now(); spin(st); }

  function spin(st) {
    if (st.raf) return;
    const loop = (now) => {
      if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
      if (now - st.padAt > 1000) { st.padAt = now; if (padStrip(st)) fit(st.root, st); }
      const live = !!(st.ctx && st.ctx.playing) && phaseOf(st) !== 'over';
      if (!st.visible || (!live && now - st.wakeAt > IDLE_MS)) { st.raf = 0; return; }
      if (!document.hidden) draw(st);
      st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  HGames.register({
    id: 'tavern',
    added: '2026-09-27',
    icon: ICON,
    seatNames: SEAT_NAMES,
    seatClass: ['x', 'o', 'c', 'd', 'tavern-b', 'tavern-p', 'tavern-v', 'tavern-r'],
    pad: {
      dirs: true,
      a: 'Space',
      x: 'KeyE',
      on(btn, ctx) {
        const st = ctx && ctx._tavern;
        if (!st) return false;
        if (btn === 'lb') { peek(st); return true; }
        if (btn === 'rb' || btn === 'rt') { sit(st); return true; }
        return false;
      },
      hint: '{dpad} іти · {a} кулак · {x} кухоль · {rb} сісти · {lb} де я?',
    },
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Корчма',
      items: [
        '🍺 Повна корчма люду — і десь серед них твої друзі. Ніхто не знає, хто тут живий',
        '👊 Пробіл (Ⓐ) — кулаком перед собою: три серця на раунд. Вдарив бота — отетерієш і видаси себе',
        '🍺 Тихий шлях: E (Ⓧ) — кухоль біля шинквасу чи бочки. Три кухлі в трьох різних місцях — раунд твій',
        '🪑 F (RB) — сісти на лаву й сховатись серед тих, хто вечеряє. Тільки встають не одразу',
        '😠 Корчмар гримнув «Хто тут б\'ється?!» — не махай кулаками, поки дивиться, бо полетиш за двері',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      root.classList.add('tavern-body');
      st.hudEl = document.createElement('div');
      st.hudEl.className = 'tavern-hud';
      st.hudEl.innerHTML = '<span class="tavern-chip tavern-clock">⏱ —</span><span class="tavern-chip tavern-bar">🧔 Корчмар порається</span><span class="tavern-chips"></span>';
      st.clockEl = st.hudEl.querySelector('.tavern-clock');
      st.barEl = st.hudEl.querySelector('.tavern-bar');
      st.seatsEl = document.createElement('div');
      st.seatsEl.className = 'tavern-seats';
      st.hudEl.appendChild(st.seatsEl);
      st.hudEl.addEventListener('click', (e) => {
        if (!e.target.closest('.tavern-mute')) return;
        st.mute = !st.mute;
        try { localStorage.setItem('tavernMute', st.mute ? '1' : ''); } catch { /* приватне вікно */ }
        if (!st.mute) unlock(st);
        hud(st);
      });
      st.newsEl = document.createElement('div');
      st.newsEl.className = 'tavern-news';
      st.newsEl.setAttribute('aria-live', 'polite');
      st.sumEl = document.createElement('div');
      st.sumEl.className = 'tavern-sum';
      st.sumEl.hidden = true;
      st.stageEl = document.createElement('div');
      st.stageEl.className = 'tavern-stage';
      st.stageEl.appendChild(st.newsEl);
      root.append(st.hudEl, st.stageEl, st.sumEl);
      st.cv = HGames.ui.canvas(st.stageEl, { w: WW, h: WH, cls: 'tavern-board' });
      wireCanvas(st);
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => { if (!st.roQ) st.roQ = requestAnimationFrame(() => { st.roQ = 0; if (root.isConnected) fit(root, st); }); });   // через rAF: синхронна зміна розміру в колбеку RO давала «ResizeObserver loop completed…»
        st.ro.observe(root);
      }
      st.onResize = () => fit(root, st);
      window.addEventListener('resize', st.onResize);
      // Поворот телефона чи ⛶ (g-imm/g-land): режим камери й розмір мапи — наново, а мапа з кнопками знову в кадр.
      if (HGames.ui.onFit) HGames.ui.onFit(root, (f) => {
        fit(root, st);
        const k = (f.w > f.h ? 'L' : 'P') + (f.imm ? 'i' : '');
        if (k === st.fitKey) return;
        const was = st.fitKey;
        st.fitKey = k;
        if (was) { st.fitFor = null; fitPhone(st); }
      });
      if (window.IntersectionObserver) {
        st.io = new IntersectionObserver((es) => { for (const e of es) st.visible = e.isIntersecting; if (st.visible) wake(st); });
        st.io.observe(st.cv.el);
      }
      st.autoPeek = true;
      st.keyup = (e) => {
        const d = dirOf(e);
        if (d === undefined) return;
        const i = st.keys.indexOf(d);
        if (i >= 0) { st.keys.splice(i, 1); want(st); }
      };
      st.blur = () => {
        st.keys.length = 0;
        st.touchDir = -1;
        want(st);
      };
      document.addEventListener('keyup', st.keyup);
      window.addEventListener('blur', st.blur);
      // F5 посеред партії: стрілка, затиснута до перезавантаження, не має вести в стіну
      if (ctx.mine && ctx.playing) ctx.input('move', { dir: -1 });
      wake(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      applyView(st, ctx.view);
      fit(root, st);
      pad(root, st);
      placeSum(root, st);
      placePad(root, st);
      placeSeats(root, st);
      if (st.padEl) {
        const ph = st.vphase, off = !(alive(st) && (ph === 'start' || ph === 'go'));
        if (st.padEl.classList.contains('tavern-off') !== off) st.padEl.classList.toggle('tavern-off', off);
        if (off && st.touchDir >= 0) { st.touchDir = -1; st.touchPid = null; want(st); }
      }
      hud(st);
      summary(st);
      paintClock(st, st.last && st.last.ph ? st.last : null);
      fitPhone(st);
      wake(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      onFrame(st, f);
      wake(st);
    },

    onKey(e, ctx) {
      const st = ctx._tavern;
      if (!st || !ctx.mine || !ctx.playing) return false;
      unlock(st);
      const d = dirOf(e);
      if (d !== undefined) {
        if (!st.keys.includes(d)) st.keys.push(d);
        else if (st.keys[st.keys.length - 1] !== d) { st.keys.splice(st.keys.indexOf(d), 1); st.keys.push(d); }
        want(st);
        return true;
      }
      if (isPunch(e)) { if (!e.repeat) punch(st, null); return true; }
      if (isDrink(e)) { if (!e.repeat) drink(st); return true; }
      if (isSit(e)) { if (!e.repeat) sit(st); return true; }
      if (isPeek(e)) { peek(st); return true; }
      return false;
    },

    status(ctx) {
      const st = ctx._tavern;
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f) return '';
      const ph = f.ph || f.phase;
      if (ph === 'start') return ctx.mine ? 'Роздивись: стрілка показує тебе' : 'Корчма відчиняється…';
      if (ph === 'reveal') {
        const v = st && st.view, s = Math.max(1, Math.ceil(((f.left | 0) * TICK_MS) / 1000));
        return v && v.round >= v.of ? 'Підсумок партії за ' + s + ' с' : 'Наступний раунд за ' + s + ' с';
      }
      if (ph !== 'go') return '';
      if (!ctx.mine) return HGames.ui.coarse() ? 'Вгадуй разом із гравцями, хто тут живий · тягни мапу пальцем' : 'Вгадуй разом із гравцями, хто тут живий';
      if (st && st.me && !st.me.alive) return 'Тебе винесли — дивись, хто кого';
      if (st && performance.now() < st.drinkUntil) return '🍺 П\'єш… до дна — стій, не тікай';
      if (st && myState(st) === 2) return '🪑 Сидиш за столом — F (RB), стрілка чи клік, щоб устати';
      if (window.HPad && window.HPad.on) return 'Стік — іти · Ⓐ кулак · Ⓧ кухоль · RB сісти · LB де я? Кухоль — на приступці біля бочки';
      return HGames.ui.coarse()
        ? 'Хрестовина — іти · 👊 кулак · 🍺 кухоль · 🪑 сісти · 👁 де я?'
        : 'Стрілки/WASD — іти · пробіл — кулак · E — кухоль · F — сісти · Q — де я? · клік — кулак у той бік';
    },

    unmount(root) {
      const st = root._tavern;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (st.ro) st.ro.disconnect();
      if (st.io) st.io.disconnect();
      if (st.audio) { try { st.audio.close(); } catch { /* уже закритий */ } }
      root._tavern = null;
    },
  });
})();
