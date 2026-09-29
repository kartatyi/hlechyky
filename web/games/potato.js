/*
  Гарячий горщик (potato) — Unspottable-«бомба» на сільській толоці. Реалтайм 25 Гц: сервер тикає раз на 40 мс і шле
  кадр, ми згладжуємо його й малюємо. Усіх селян — і ботів, і гравців, і себе — малюємо однаково, з кадрів через
  інтерполяцію: так свого не видасть ні лаг, ні «підсмикування». Передбачаємо лише косметику свого: поворот і хода.

  Кадр (Impl/Potato.cs): { t, ph: 'start'|'go'|'reveal'|'over', left, v: [x, y, d, s] × N, p: [id, heat] × горщиків, ev }
    s: 0 стоїть, 1 іде, 2 лежить бот (рвонуло), 3 вибулий гравець, 4 оглушений / отетерів; d: 0 → 1 ↓ 2 ← 3 ↑.
    p: у кого горщик (-1 — ніде, після вибуху) і як димить: 0 ледь курить, 1 дим, 2 іскри, 3 трусить.
    ev: [1, горщик, від, кому] передав · [2, хто, кого, хто закліпав] ляпас · [3, горщик, у кого, 0 бот | 1 гравець, місце]
        вибух · [4, горщик, у кого] новий горщик.
  Вид (Hidden, свій кожному): { phase, round, of, left, t, n, pots, map, looks, names, v, p, seats, dead, me, reveal, result }
    me — лише своєму місцю: { id, alive, stun, slapCool, held, passIn }.

  Ввід: Input('move', { dir }) лише на зміну (-1 — відпустив); Act('pass', {} | { id }); Act('slap', {} | { id }).
*/
(() => {
  // в'юпорт телефона — 4:3, половина толоки завширшки: селянина видно, а горщик поруч не губиться
  const TICK_MS = 40, CELL = 32, WW = 768, WH = 512, PW = 384, PH = 288;
  const DX = [1, 0, -1, 0], DY = [0, 1, 0, -1];
  const PASS_RANGE = 36, PASS_MAX = 48, SLAP_RANGE = 40, SLAP_MAX = 52, SLAP_CONE = 500;
  const HOLD_MS = 480, SLAP_COOL_MS = 4000, NO_BACK_MS = 2000, ROUND_TICKS = 2250;
  const PEEK_MS = 1500, NEWS_MS = 6000, LOCAL_MS = 250, FLY_MS = 180, BOOM_MS = 900, SLAP_MS = 750, SOOT_MS = 12000;
  // стіл стоїть (лобі, партію зіграно): стільки ще малюємо після останньої події — довше за найдовшу анімацію
  const IDLE_MS = 2000;
  // затиснуту стрілку підтверджуємо раз на секунду: сервер відпускає її сам, якщо 3 с не чув (обрив зв'язку)
  const HOLD_KEY_MS = 1000, TIP_MS = 1800, F5_PEEK_MS = 3000;
  const TAU = Math.PI * 2;

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M6 3.2c-.8-1 .4-1.6 0-2.4M9 3.4c-.9-1.2.5-1.7 0-2.6" fill="none" stroke="var(--muted)" stroke-width="1.1" stroke-linecap="round"/>'
    + '<path d="M3.2 7.2c0-1.4 2.2-2.4 4.8-2.4s4.8 1 4.8 2.4c0 3.8-2 6.6-4.8 6.6S3.2 11 3.2 7.2z" fill="var(--clay)"/>'
    + '<ellipse cx="8" cy="5.4" rx="4.1" ry="1.3" fill="var(--danger)"/><circle cx="6.6" cy="5.3" r=".7" fill="var(--accent)"/>'
    + '<circle cx="9.3" cy="5.5" r=".6" fill="var(--accent)"/><path d="M4.4 9.2h7.2" stroke="var(--text)" stroke-width=".9" opacity=".55"/></svg>';

  const SEAT_NAMES = ['жовтий', 'зелений', 'рудий', 'сірий', 'синій', 'рожевий', 'фіолетовий', 'червоний'];
  const SEAT_VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'],
    ['--potato-blue', '#6fb3e8'], ['--potato-pink', '#e88ac0'], ['--potato-violet', '#b48ae8'], ['--potato-red', '#e86a6a']];
  // Одяг — палітра толоки, а не тема: червоний, синій, зелений, жовтий, білий, фіолетовий, помаранчевий, чорний.
  const CLOTH = ['#c0392b', '#2f6db5', '#3f9142', '#e8c33a', '#f2efe6', '#7d4fa8', '#e07b2a', '#2a2a2a'];
  const HAIR = ['#2b1d14', '#4a2f1d', '#7a4b27', '#c9a15a', '#8c4a2f', '#3b2a20', '#6b4a2e', '#1a1410'];
  const SKIN = ['#f3d0b0', '#e2b48c', '#c48a5e', '#8d5a3b'];
  const FEMALE = new Set(['Параска', 'Ганна', 'Одарка', 'Марічка', 'Оксана', 'Домаха', 'Соломія', 'Мотря', 'Ярина', 'Христя',
    'Марта', 'Устя', 'Килина', 'Наталка', 'Пріська', 'Гафія', 'Софійка', 'Орися', 'Настя', 'Явдоха', 'Меланка', 'Феся', 'Текля',
    'Зоряна']);
  // родовий відмінок для «горщик у …»: Параска → Параски, Микола → Миколи, Степан → Степана
  const GEN = { 'Іванко': 'Іванка', 'Андрійко': 'Андрійка', 'Юрко': 'Юрка', 'Левко': 'Левка', 'Сашко': 'Сашка', 'Лесь': 'Леся',
    'Гриць': 'Гриця', 'Тиміш': 'Тимоша', 'Прокіп': 'Прокопа', 'Йосип': 'Йосипа', 'Ігнат': 'Ігната', 'Матвій': 'Матвія' };
  const genitive = (name) => {
    if (GEN[name]) return GEN[name];
    if (/ія$/.test(name)) return name.slice(0, -1) + 'ї';
    if (/[гкхжчшщ]а$/.test(name)) return name.slice(0, -1) + 'и';
    if (/а$/.test(name)) return name.slice(0, -1) + 'и';
    if (/я$/.test(name)) return name.slice(0, -1) + 'і';
    if (/о$/.test(name)) return name.slice(0, -1) + 'а';
    if (/ь$/.test(name)) return name.slice(0, -1) + 'я';
    return name + 'а';
  };
  // знахідний для «ляснула Степана», «ляснув Параску»: жіночі й «Микола» на -а/-я — у/ю, решта (чоловічі, живі) = родовий
  const accusative = (name) => (/а$/.test(name) ? name.slice(0, -1) + 'у' : /я$/.test(name) ? name.slice(0, -1) + 'ю' : genitive(name));

  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  const KEY_DIRS = { d: 0, 'в': 0, s: 1, 'і': 1, 'ы': 1, a: 2, 'ф': 2, w: 3, 'ц': 3 };
  const dirOf = (e) => {
    const d = DIRS[e.code];
    return d !== undefined ? d : KEY_DIRS[String(e.key || '').toLowerCase()];
  };
  const low = (e) => String(e.key || '').toLowerCase();
  const isPass = (e) => e.code === 'Space' || e.code === 'Enter' || e.code === 'NumpadEnter'
    || (!e.code && (e.key === ' ' || e.key === 'Spacebar' || e.key === 'Enter'));
  const isSlap = (e) => e.code === 'KeyE' || e.code === 'KeyF' || (!e.code && ['e', 'у', 'f', 'а'].includes(low(e)));
  const isPeek = (e) => e.code === 'KeyQ' || e.code === 'Tab' || (!e.code && ['q', 'й'].includes(low(e)));

  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const secs = (ticks) => ((ticks * TICK_MS) / 1000).toFixed(1).replace('.', ',');
  const readMute = () => { try { return localStorage.getItem('potatoMute') === '1'; } catch { return false; } };

  // ---------------------------------------------------------------------------------------------
  // Палітра і статика (трава, стежки, тин, верби, криниця, лави, віз) — один раз в offscreen-канвас
  // ---------------------------------------------------------------------------------------------

  function palette() {
    const c = HGames.ui.css;
    return {
      grass: c('--potato-grass', '#7aa152'),
      grass2: c('--potato-grass2', '#6a9146'),
      earth: c('--potato-earth', '#c4a473'),
      wood: c('--potato-wood', '#8a5a33'),
      wicker: c('--potato-wicker', '#a47a45'),
      shadow: c('--potato-shadow', 'rgba(20, 30, 15, .32)'),
      fire: c('--potato-fire', '#ff8a2a'),
      text: c('--text', '#ecf1ea'),
      accent: c('--accent', '#f4c542'),
      danger: c('--danger', '#e57373'),
      ink: '#1d241c',
      seats: SEAT_VARS.map(([n, f]) => c(n, f)),
    };
  }

  function lcg(seed) {
    let s = seed >>> 0;
    return () => ((s = (Math.imul(s, 1103515245) + 12345) >>> 0) / 4294967296);
  }

  function drawStatic(map, pal, S) {
    const c = document.createElement('canvas');
    c.width = Math.round(WW * S);
    c.height = Math.round(WH * S);
    const g = c.getContext('2d');
    g.scale(S, S);
    const at = (x, y) => (map[y] && map[y][x]) || '#';
    const rnd = lcg(2709);

    // трава з плямами й травинками
    g.fillStyle = pal.grass;
    g.fillRect(0, 0, WW, WH);
    g.fillStyle = pal.grass2;
    for (let i = 0; i < 520; i++) {
      g.beginPath();
      g.ellipse(rnd() * WW, rnd() * WH, 3 + rnd() * 7, 2 + rnd() * 3, rnd() * 3, 0, TAU);
      g.fill();
    }
    g.strokeStyle = 'rgba(255, 255, 220, .16)';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = 0; i < 320; i++) {
      const x = rnd() * WW, y = rnd() * WH;
      g.moveTo(x, y); g.lineTo(x - 1.5, y - 4);
      g.moveTo(x + 2, y); g.lineTo(x + 3, y - 3.5);
    }
    g.stroke();
    // квіточки (ромашки й маки) — толока ж
    for (let i = 0; i < 70; i++) {
      const x = CELL + rnd() * (WW - 2 * CELL), y = CELL + rnd() * (WH - 2 * CELL);
      g.fillStyle = rnd() < 0.7 ? '#f6f3e6' : '#d8453a';
      g.beginPath(); g.arc(x, y, 1.4, 0, TAU); g.fill();
    }

    // утоптана земля
    g.fillStyle = pal.earth;
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (at(x, y) === '=') { g.beginPath(); g.roundRect(x * CELL - 1, y * CELL - 1, CELL + 2, CELL + 2, 8); g.fill(); }
    g.fillStyle = 'rgba(80, 55, 30, .2)';
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (at(x, y) === '=')
          for (let k = 0; k < 3; k++) {
            g.beginPath();
            g.arc(x * CELL + 4 + rnd() * 24, y * CELL + 4 + rnd() * 24, 0.8 + rnd() * 1.4, 0, TAU);
            g.fill();
          }

    wattle(g, pal, rnd);

    const blocks = new Set();
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++) {
        const ch = at(x, y), cx = x * CELL + 16, cy = y * CELL + 16;
        if (ch === 'T') willow(g, cx, cy, rnd);
        else if (ch === 'Y') hay(g, cx, cy);
        else if ((ch === 'B' || ch === 'V') && !blocks.has(y * 100 + x - 1) && at(x + 1, y) === ch) {
          blocks.add(y * 100 + x);
          if (ch === 'B') bench(g, x * CELL, y * CELL, pal);
          else cart(g, x * CELL, y * CELL, pal);
        } else if (ch === 'W' && at(x - 1, y) !== 'W' && at(x, y - 1) !== 'W') well(g, x * CELL + 32, y * CELL + 32, pal);
      }
    return c;
  }

  /// Тин по краю — плетений, з глечиками на кілках (це ж «Глечики») і соняшниками за ним.
  function wattle(g, pal, rnd) {
    g.fillStyle = 'rgba(0, 0, 0, .16)';
    g.fillRect(0, 0, WW, CELL); g.fillRect(0, WH - CELL, WW, CELL); g.fillRect(0, 0, CELL, WH); g.fillRect(WW - CELL, 0, CELL, WH);
    // соняшники за тином угорі
    for (let x = 20; x < WW - 10; x += 46 + rnd() * 30) {
      g.strokeStyle = '#4c7a2a'; g.lineWidth = 2;
      g.beginPath(); g.moveTo(x, 20); g.lineTo(x, 8); g.stroke();
      g.fillStyle = '#f2b61c';
      g.beginPath(); g.arc(x, 7, 5.5, 0, TAU); g.fill();
      g.fillStyle = '#5a3a18';
      g.beginPath(); g.arc(x, 7, 2.4, 0, TAU); g.fill();
    }
    const band = (x0, y0, x1, y1) => {
      const horiz = y0 === y1;
      g.strokeStyle = pal.wicker;
      g.lineWidth = 2.2;
      for (let r = -3; r <= 3; r += 3) {
        g.beginPath();
        const len = horiz ? x1 - x0 : y1 - y0;
        for (let s = 0; s <= len; s += 8) {
          const wob = ((s / 8) % 2 ? 1 : -1) * 1.6;
          const px = horiz ? x0 + s : x0 + r + wob, py = horiz ? y0 + r + wob : y0 + s;
          if (s === 0) g.moveTo(px, py); else g.lineTo(px, py);
        }
        g.stroke();
      }
    };
    band(CELL - 8, CELL - 8, WW - CELL + 8, CELL - 8);
    band(CELL - 8, WH - CELL + 8, WW - CELL + 8, WH - CELL + 8);
    band(CELL - 8, CELL - 8, CELL - 8, WH - CELL + 8);
    band(WW - CELL + 8, CELL - 8, WW - CELL + 8, WH - CELL + 8);
    // кілки, а на кожному третьому — глечик
    let n = 0;
    const stake = (x, y) => {
      g.fillStyle = '#5e3b20';
      g.fillRect(x - 2, y - 7, 4, 13);
      if (n++ % 3 === 0) {
        g.fillStyle = n % 2 ? '#b5652e' : '#9c5428';
        g.beginPath(); g.ellipse(x, y - 11, 4.5, 4, 0, 0, TAU); g.fill();
        g.fillRect(x - 2, y - 17, 4, 3);
      }
    };
    for (let x = CELL - 8; x <= WW - CELL + 8; x += 32) { stake(x, CELL - 8); stake(x, WH - CELL + 8); }
    for (let y = CELL + 24; y <= WH - CELL - 8; y += 32) { stake(CELL - 8, y); stake(WW - CELL + 8, y); }
  }

  function willow(g, x, y, rnd) {
    g.fillStyle = 'rgba(10, 25, 5, .28)';
    g.beginPath(); g.ellipse(x + 5, y + 7, 17, 12, 0, 0, TAU); g.fill();
    g.fillStyle = '#6b4424';
    g.beginPath(); g.arc(x, y + 2, 4, 0, TAU); g.fill();
    const greens = ['#3f7430', '#4d8a3a', '#5f9c46'];
    for (let i = 0; i < 3; i++) {
      g.fillStyle = greens[i];
      g.beginPath();
      g.arc(x + (i - 1) * 5 + rnd() * 2, y - 3 + (i === 1 ? -4 : 0), 12 - i * 2, 0, TAU);
      g.fill();
    }
    // верба: звислі гілочки
    g.strokeStyle = 'rgba(40, 80, 30, .7)';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = -10; i <= 10; i += 4) { g.moveTo(x + i, y - 6); g.quadraticCurveTo(x + i * 1.2, y + 2, x + i * 1.1, y + 8); }
    g.stroke();
  }

  function hay(g, x, y) {
    g.fillStyle = 'rgba(20, 20, 0, .25)';
    g.beginPath(); g.ellipse(x + 3, y + 6, 15, 10, 0, 0, TAU); g.fill();
    g.fillStyle = '#d9b44a';
    g.beginPath(); g.ellipse(x, y, 14, 11, 0, 0, TAU); g.fill();
    g.strokeStyle = '#a8842c';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = -10; i <= 10; i += 4) { g.moveTo(x + i, y - 8); g.lineTo(x + i * 0.7, y + 8); }
    g.stroke();
  }

  function well(g, x, y, pal) {
    g.fillStyle = 'rgba(0, 0, 0, .25)';
    g.beginPath(); g.arc(x + 3, y + 5, 27, 0, TAU); g.fill();
    g.fillStyle = '#9b958a';
    g.beginPath(); g.arc(x, y, 26, 0, TAU); g.fill();
    g.strokeStyle = '#7b756b';
    g.lineWidth = 1.2;
    for (let a = 0; a < TAU; a += TAU / 10) {
      g.beginPath(); g.moveTo(x + Math.cos(a) * 17, y + Math.sin(a) * 17); g.lineTo(x + Math.cos(a) * 26, y + Math.sin(a) * 26); g.stroke();
    }
    g.fillStyle = '#1f3d57';
    g.beginPath(); g.arc(x, y, 16, 0, TAU); g.fill();
    g.fillStyle = 'rgba(160, 210, 255, .35)';
    g.beginPath(); g.ellipse(x - 4, y - 5, 6, 3, -0.5, 0, TAU); g.fill();
    g.fillStyle = pal.wood;
    g.fillRect(x - 30, y - 3, 60, 6);
    g.fillStyle = '#5e3b20';
    g.fillRect(x - 32, y - 6, 6, 12); g.fillRect(x + 26, y - 6, 6, 12);
    g.fillStyle = '#7a8a94';
    g.beginPath(); g.roundRect(x + 5, y + 6, 9, 8, 2); g.fill();
  }

  function bench(g, x0, y0, pal) {
    g.fillStyle = 'rgba(0, 0, 0, .25)';
    g.fillRect(x0 + 5, y0 + 13, 58, 10);
    g.fillStyle = '#5e3b20';
    g.fillRect(x0 + 8, y0 + 14, 4, 10); g.fillRect(x0 + 52, y0 + 14, 4, 10);
    g.fillStyle = pal.wood;
    g.beginPath(); g.roundRect(x0 + 3, y0 + 8, 58, 9, 3); g.fill();
    g.strokeStyle = 'rgba(40, 20, 5, .4)';
    g.lineWidth = 1;
    g.beginPath(); g.moveTo(x0 + 5, y0 + 12.5); g.lineTo(x0 + 59, y0 + 12.5); g.stroke();
  }

  function cart(g, x0, y0, pal) {
    g.fillStyle = 'rgba(0, 0, 0, .25)';
    g.fillRect(x0 + 6, y0 + 8, 58, 22);
    g.fillStyle = '#9a6a3a';
    g.fillRect(x0 + 6, y0 + 5, 50, 20);
    g.strokeStyle = '#5e3b20';
    g.lineWidth = 1.5;
    g.strokeRect(x0 + 6, y0 + 5, 50, 20);
    g.beginPath(); for (let i = 1; i < 5; i++) { g.moveTo(x0 + 6 + i * 10, y0 + 5); g.lineTo(x0 + 6 + i * 10, y0 + 25); } g.stroke();
    // гарбузи на возі
    for (const [dx, dy, r] of [[18, 13, 6], [31, 11, 5], [43, 15, 6]]) {
      g.fillStyle = '#e0832a'; g.beginPath(); g.arc(x0 + dx, y0 + dy, r, 0, TAU); g.fill();
      g.fillStyle = '#4c7a2a'; g.fillRect(x0 + dx - 1, y0 + dy - r - 2, 2, 3);
    }
    g.strokeStyle = pal.wood; g.lineWidth = 2.5;
    g.beginPath(); g.moveTo(x0 + 56, y0 + 15); g.lineTo(x0 + 64, y0 + 15); g.stroke();
    g.fillStyle = '#3a2618';
    g.beginPath(); g.arc(x0 + 14, y0 + 27, 5, 0, TAU); g.arc(x0 + 48, y0 + 27, 5, 0, TAU); g.fill();
  }

  // ---------------------------------------------------------------------------------------------
  // Селяни (фігурка та сама, що в «Юрмі»: юрма має бути знайома)
  // ---------------------------------------------------------------------------------------------

  function villager(g, x, y, d, s, look, now, id, ink, soot) {
    if (s === 2 || s === 3) return lying(g, x, y, d, look, ink, soot);
    const hat = look[0], hc = look[1], shirt = look[2], skin = look[3];
    const side = d === 0 ? 1 : d === 2 ? -1 : 0;
    let bob = 0, step = 0;
    if (s === 1) {
      const ph = Math.sin(now * 0.0503 + id * 1.7);
      bob = ph > 0 ? -1 : 0;
      step = ph > 0 ? 1 : -1;
    }
    let sway = 0;
    if (s === 4) sway = Math.sin(now * 0.012 + id) * 1.6;      // оглушений — похитується
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
    y += bob;
    x += sway;
    const cloth = CLOTH[shirt] || CLOTH[0], skinC = SKIN[skin] || SKIN[0];
    g.fillStyle = cloth;
    g.beginPath(); g.roundRect(x - 7, y - 14, 14, 13, 4); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.stroke();
    g.fillStyle = 'rgba(0, 0, 0, .22)';
    g.fillRect(x - 7, y - 6, 14, 2);
    if (d === 1) {
      g.fillStyle = shirt === 0 ? '#f2efe6' : '#c0392b';
      g.fillRect(x - 1, y - 13, 2, 5);
    }
    g.fillStyle = skinC;
    g.beginPath();
    if (side) g.arc(x - side * 1 + side * 3 * step, y - 5, 2.2, 0, TAU);
    else { g.arc(x - 8, y - 6 + step, 2.2, 0, TAU); g.arc(x + 8, y - 6 - step, 2.2, 0, TAU); }
    g.fill();
    const hx = x + side * 1.5 + sway * 0.6, hy = y - 19;
    g.beginPath(); g.arc(hx, hy, 6.2, 0, TAU); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 0.9;
    g.stroke();
    if (soot) {                  // закопчений після вибуху
      g.fillStyle = 'rgba(30, 24, 20, .55)';
      g.beginPath(); g.arc(hx - 1.5, hy + 1.5, 3.2, 0, TAU); g.arc(hx + 2.5, hy - 1, 2.2, 0, TAU); g.fill();
    }
    g.fillStyle = ink;
    if (s === 4 && d !== 3) {    // очі-спіральки: ✕
      g.lineWidth = 1;
      g.strokeStyle = ink;
      g.beginPath();
      const ex = side ? [hx + side * 2.2] : [hx - 2, hx + 2];
      for (const e of ex) { g.moveTo(e - 1.2, hy - 1); g.lineTo(e + 1.2, hy + 1.4); g.moveTo(e + 1.2, hy - 1); g.lineTo(e - 1.2, hy + 1.4); }
      g.stroke();
    } else if (d === 1) {
      g.fillRect(hx - 2.8, hy - 0.2, 1.6, 1.8);
      g.fillRect(hx + 1.2, hy - 0.2, 1.6, 1.8);
    } else if (side) {
      g.fillRect(hx + side * 2.2 - 0.8, hy - 0.4, 1.6, 1.8);
      g.fillStyle = skinC;
      g.beginPath(); g.arc(hx + side * 5.8, hy + 1.2, 1.5, 0, TAU); g.fill();
    }
    const hcol = CLOTH[hc] || CLOTH[0];
    switch (hat) {
      case 0:
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
      case 1:
        g.fillStyle = hcol;
        g.beginPath(); g.ellipse(hx, hy - 3.5, 10, 3.6, 0, 0, TAU); g.fill();
        g.strokeStyle = 'rgba(0, 0, 0, .35)';
        g.lineWidth = 0.8;
        g.stroke();
        g.beginPath(); g.ellipse(hx, hy - 6, 5.4, 4.2, 0, Math.PI, TAU); g.closePath(); g.fill();
        g.fillStyle = 'rgba(0, 0, 0, .28)';
        g.fillRect(hx - 5.4, hy - 6.2, 10.8, 1.6);
        break;
      case 2:
        g.fillStyle = hcol;
        g.beginPath(); g.ellipse(hx, hy - 4.2, 6.6, 3.8, 0, Math.PI, TAU); g.closePath(); g.fill();
        g.fillRect(hx - 6.6, hy - 4.6, 13.2, 1.8);
        g.fillStyle = '#1e1e1e';
        g.beginPath();
        if (side) g.ellipse(hx + side * 6.6, hy - 3, 3.6, 1.3, 0, 0, TAU);
        else if (d === 1) g.ellipse(hx, hy - 2.5, 5.2, 1.6, 0, 0, TAU);
        g.fill();
        break;
      default: {
        g.fillStyle = HAIR[hc] || HAIR[0];
        g.beginPath();
        if (d === 3) g.arc(hx, hy, 6.3, 0, TAU);
        else if (side) g.ellipse(hx - side * 1.4, hy - 1.8, 6, 5.4, 0, Math.PI * 0.9, Math.PI * 2.1);
        else g.arc(hx, hy - 0.8, 6.3, Math.PI * 1.05, Math.PI * 1.95);
        g.fill();
      }
    }
  }

  /// Лежить: навзнак упоперек, голова вбік, очі ✕✕, шапка відлетіла, на обличчі сажа.
  function lying(g, x, y, d, look, ink, soot) {
    const dir = d === 2 ? -1 : 1;
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
    if (soot) {
      g.fillStyle = 'rgba(30, 24, 20, .6)';
      g.beginPath(); g.arc(hx, hy + 1, 4, 0, TAU); g.fill();
    }
    g.lineWidth = 1.1;
    g.beginPath();
    g.moveTo(hx - 3.5, hy - 2.5); g.lineTo(hx - 1, hy); g.moveTo(hx - 1, hy - 2.5); g.lineTo(hx - 3.5, hy);
    g.moveTo(hx + 1, hy - 2.5); g.lineTo(hx + 3.5, hy); g.moveTo(hx + 3.5, hy - 2.5); g.lineTo(hx + 1, hy);
    g.stroke();
    if (look[0] < 3) {
      g.fillStyle = CLOTH[look[1]] || CLOTH[0];
      g.beginPath(); g.ellipse(hx + dir * 9, hy + 6, 5, 3, 0.4 * dir, 0, TAU); g.fill();
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Горщик, дим, іскри
  // ---------------------------------------------------------------------------------------------

  /// Горщик над головою носія: глиняний, з вушками, жар угорі. Чим гарячіше — тим червоніший і дужче трусить.
  function pot(g, x, y, heat, now, k, pal) {
    let sx = 0, sy = 0;
    if (heat >= 3) { sx = Math.sin(now * 0.09 + k * 2) * 2.2; sy = Math.cos(now * 0.113 + k) * 1.3; }
    else if (heat === 2) { sx = Math.sin(now * 0.06 + k) * 0.8; }
    // горщик — головний герой: у півтора раза більший за голову селянина, щоб читався й на малій мапі
    g.save();
    g.translate(x + sx, y + sy);
    g.scale(1.3, 1.3);
    x = 0; y = 0;
    const pulse = 0.5 + 0.5 * Math.sin(now / (heat >= 3 ? 70 : heat === 2 ? 140 : 260));
    // сяйво довкола — видно й на маленькій мапі
    g.globalAlpha = 0.18 + heat * 0.08 + pulse * 0.08 * heat;
    g.fillStyle = heat >= 2 ? '#ff5a1f' : pal.fire;
    g.beginPath(); g.arc(x, y, 13 + heat * 3 + pulse * 2, 0, TAU); g.fill();
    g.globalAlpha = 1;
    // тіло
    const body = heat >= 3 ? (pulse > 0.5 ? '#d9442a' : '#b8502c') : heat === 2 ? '#bf5a2c' : '#a9602f';
    g.fillStyle = body;
    g.beginPath();
    g.moveTo(x - 6.5, y - 4);
    g.bezierCurveTo(x - 11, y, x - 9, y + 8.5, x, y + 8.5);
    g.bezierCurveTo(x + 9, y + 8.5, x + 11, y, x + 6.5, y - 4);
    g.closePath();
    g.fill();
    g.strokeStyle = '#4a2410';
    g.lineWidth = 1;
    g.stroke();
    // вушка
    g.beginPath(); g.arc(x - 8.6, y - 0.5, 2, Math.PI * 0.5, Math.PI * 1.5); g.arc(x + 8.6, y - 0.5, 2, -Math.PI * 0.5, Math.PI * 0.5); g.stroke();
    // поясок-орнамент
    g.strokeStyle = 'rgba(255, 230, 190, .55)';
    g.beginPath(); g.moveTo(x - 8, y + 2); g.lineTo(x + 8, y + 2); g.stroke();
    // вінця й жар
    g.fillStyle = '#6a3317';
    g.beginPath(); g.ellipse(x, y - 4, 7.2, 2.4, 0, 0, TAU); g.fill();
    const f = now / 90;
    g.fillStyle = '#ffcf4a';
    g.beginPath(); g.ellipse(x, y - 4.3, 5.4, 1.6, 0, 0, TAU); g.fill();
    g.fillStyle = '#ff6a1a';
    for (let i = 0; i < 3; i++) {
      g.beginPath(); g.arc(x - 3 + i * 3, y - 4.3 + Math.sin(f + i * 2) * 0.5, 1.1 + 0.4 * Math.sin(f * 1.3 + i), 0, TAU); g.fill();
    }
    g.restore();
  }

  /// Кільце під ногами носія — щоб горщик читався навіть у тисняві.
  function carrierRing(g, x, y, heat, now, pal) {
    const p = 0.5 + 0.5 * Math.sin(now / (heat >= 3 ? 80 : 200));
    g.strokeStyle = heat >= 2 ? '#ff5a1f' : pal.fire;
    g.globalAlpha = 0.55 + 0.35 * p;
    g.lineWidth = 2;
    g.beginPath(); g.ellipse(x, y + 5, 13 + p * 2, 6.5 + p, 0, 0, TAU); g.stroke();
    g.globalAlpha = 1;
  }

  /// Частинки — пул без алокацій у циклі: дим (сіре коло, що росте й тане) та іскри (гарячі точки з гравітацією).
  function particles(n) {
    const a = [];
    for (let i = 0; i < n; i++) a.push({ on: false, x: 0, y: 0, vx: 0, vy: 0, r: 0, life: 0, max: 1, spark: false, dark: 0 });
    return a;
  }

  function emit(st, x, y, spark, dark, big) {
    const pool = st.parts;
    for (let i = 0; i < pool.length; i++) {
      const q = pool[(st.partAt + i) % pool.length];
      if (q.on) continue;
      st.partAt = (st.partAt + i + 1) % pool.length;
      q.on = true;
      q.spark = spark;
      q.dark = dark;
      q.x = x + (Math.random() - 0.5) * (big ? 18 : 6);
      q.y = y;
      if (spark) {
        q.vx = (Math.random() - 0.5) * 70; q.vy = -40 - Math.random() * 60; q.r = 1 + Math.random() * 0.8; q.max = 0.45 + Math.random() * 0.3;
      } else {
        q.vx = (Math.random() - 0.3) * (big ? 40 : 10); q.vy = -(big ? 20 : 16) - Math.random() * 12; q.r = big ? 7 : 2.5 + Math.random() * 1.5;
        q.max = big ? 1.4 : 1 + Math.random() * 0.5;
      }
      q.life = 0;
      return;
    }
  }

  function stepParts(st, dt) {
    for (const q of st.parts) {
      if (!q.on) continue;
      q.life += dt;
      if (q.life >= q.max) { q.on = false; continue; }
      q.x += q.vx * dt;
      q.y += q.vy * dt;
      if (q.spark) q.vy += 140 * dt;
      else { q.vx *= 0.985; q.r += dt * (q.dark > 1 ? 16 : 9); }
    }
  }

  function drawParts(st, g) {
    for (const q of st.parts) {
      if (!q.on || q.spark) continue;
      const k = 1 - q.life / q.max;
      // світлий дим на траві й стежці губився — він сірий і щільніший, а з іскрами — темний
      g.globalAlpha = k * (q.dark ? 0.7 : 0.55);
      g.fillStyle = q.dark > 1 ? '#34302c' : q.dark ? '#5d5750' : '#9b948a';
      g.beginPath(); g.arc(q.x, q.y, q.r, 0, TAU); g.fill();
    }
    for (const q of st.parts) {
      if (!q.on || !q.spark) continue;
      g.globalAlpha = 1 - q.life / q.max;
      g.fillStyle = q.life < 0.15 ? '#fff2a8' : '#ff9a2a';
      g.fillRect(q.x - q.r, q.y - q.r, q.r * 2, q.r * 2);
    }
    g.globalAlpha = 1;
  }

  // ---------------------------------------------------------------------------------------------
  // Стан модуля
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    let st = root._potato;
    if (!st) {
      st = root._potato = {
        root, ctx: null, cv: null, interp: HGames.ui.Interp(), mode: 'full', S: 1,
        view: null, n: 0, looks: [], names: [], map: null, mapKey: '', meId: -1, me: null,
        round: -1, vphase: '', last: null, lastAt: 0, fph: '', pots: [],
        px: new Float64Array(64), py: new Float64Array(64), pd: new Int8Array(64), ps: new Int8Array(64), order: [],
        stat: null, statKey: '', mini: null, pal: null, palAt: -1e9,
        cam: { x: WW / 2, y: WH / 2 }, box: [0, 0, WW, WH], drag: null, dragUntil: 0, hover: false,
        keys: [], touchDir: -1, dir: -1, localDir: -1, localUntil: 0, peekUntil: 0, sentAt: 0,
        slapAt: -1e9, gotAt: -1e9, giver: -1, tip: null, tipUntil: 0, autoPeek: false,
        parts: particles(260), partAt: 0, emitAcc: [0, 0, 0, 0], lastDraw: 0,
        flies: [], booms: [], slaps: [], soot: new Map(),
        raf: 0, keyup: null, blur: null, ro: null, io: null, onResize: null, visible: true,
        padOn: false, padH: 0, padAt: 0, lab: [],
        hudEl: null, clockEl: null, newsEl: null, sumEl: null, padEl: null, stageEl: null, seatsEl: null,
        audio: null, mute: readMute(),
        perf: { sum: 0, n: 0, max: 0 }, wakeAt: 0,
      };
    }
    st.ctx = ctx;
    ctx._potato = st;
    return st;
  }

  const seatOf = (st, i) => (st.view && st.view.seats || []).find((s) => s.seat === i) || null;
  const nickOfSeat = (st, i) => { const s = seatOf(st, i); return (s && s.nick) || (st.ctx && st.ctx.nickOf(i)) || SEAT_NAMES[i] || '?'; };
  const nameOf = (st, id) => (st.names && st.names[id]) || 'селянин';
  const alive = (st) => !!(st.me && st.me.alive);
  const phaseOf = (st) => (st.vphase === 'over' ? 'over' : st.fph || st.vphase);
  /// Номер горщика в моїх руках (з останнього кадру); -1 — нема.
  const myPot = (st) => {
    const me = st.meId, p = st.pots;
    if (me < 0) return -1;
    for (let k = 0; k * 2 < p.length; k++) if (p[k * 2] === me) return k;
    return -1;
  };
  const carrierPot = (st, id) => {
    const p = st.pots;
    for (let k = 0; k * 2 < p.length; k++) if (p[k * 2] === id) return k;
    return -1;
  };

  /// Відмова — тостом і підписом над своїм селянином (на Деку тости ховаються під смужкою пада). Та сама відмова,
  /// поки підпис ще видно, тост не множить: людина з горщиком тисне пробіл очманіло, і стовпчик тостів закрив би мапу.
  function refuse(st, text, toasted) {
    if (!text) return;
    const now = performance.now();
    const again = st.tip === text && now < st.tipUntil;
    if (!toasted && !again && st.ctx) st.ctx.toast(text, 'err');
    st.tip = text;
    st.tipUntil = now + TIP_MS;
    wake(st);
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

  function noise(A, out, t, dur, vol, freq) {
    const len = Math.max(1, Math.floor(A.sampleRate * dur));
    const buf = A.createBuffer(1, len, A.sampleRate);
    const ch = buf.getChannelData(0);
    for (let i = 0; i < len; i++) ch[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, 2);
    const src = A.createBufferSource();
    src.buffer = buf;
    const f = A.createBiquadFilter();
    f.type = 'lowpass';
    f.frequency.value = freq;
    const gn = A.createGain();
    gn.gain.value = vol;
    src.connect(f); f.connect(gn); gn.connect(out);
    src.start(t);
  }

  function sfx(st, kind) {
    const A = st.audio;
    if (!A || st.mute || document.hidden) return;
    try {
      const t = A.currentTime;
      const out = A.createGain();
      out.connect(A.destination);
      const tone = (f0, f1, dur, vol, type) => {
        const o = A.createOscillator();
        o.type = type || 'sine';
        o.frequency.setValueAtTime(f0, t);
        if (f1 !== f0) o.frequency.exponentialRampToValueAtTime(f1, t + dur);
        const gn = A.createGain();
        gn.gain.setValueAtTime(vol, t);
        gn.gain.exponentialRampToValueAtTime(0.0001, t + dur);
        o.connect(gn); gn.connect(out);
        o.start(t); o.stop(t + dur + 0.02);
      };
      if (kind === 'boom') { noise(A, out, t, 0.5, 0.12, 900); tone(90, 40, 0.35, 0.09, 'sine'); }
      else if (kind === 'slap') noise(A, out, t, 0.05, 0.08, 4000);
      else if (kind === 'pass') tone(520, 820, 0.08, 0.025, 'triangle');
      else if (kind === 'out') tone(420, 110, 0.25, 0.05, 'triangle');
      else if (kind === 'spawn') noise(A, out, t, 0.18, 0.03, 2500);
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
      if (key !== st.mapKey) { st.mapKey = key; st.map = v.map; st.statKey = ''; }
    }
    st.n = v.n | 0;
    st.looks = v.looks || [];
    st.names = v.names || [];
    st.me = v.me || null;
    st.meId = st.me ? st.me.id : -1;
    if (v.round !== st.round || (v.phase === 'start' && st.vphase && st.vphase !== 'start')) {
      st.round = v.round;
      st.camRound = -1;
      st.interp.reset();
      st.last = null;
      st.flies.length = 0;
      st.booms.length = 0;
      st.slaps.length = 0;
      st.soot.clear();
      st.slapAt = st.gotAt = -1e9;
      st.giver = -1;
      for (const q of st.parts) q.on = false;
    }
    // F5 посеред раунду: скільки ще не можна передати й скільки відходить рука — з виду
    if (st.me) {
      const now = performance.now();
      if (st.me.passIn > 0) st.gotAt = Math.max(st.gotAt, now - HOLD_MS + st.me.passIn * TICK_MS);
      if (st.me.slapCool > 0) st.slapAt = Math.max(st.slapAt, now - SLAP_COOL_MS + st.me.slapCool * TICK_MS);
    }
    if (st.autoPeek && st.me) {
      if (v.phase === 'go' && st.me.alive) { st.peekUntil = performance.now() + F5_PEEK_MS; st.autoPeek = false; }
      else if (v.phase === 'start') st.autoPeek = false;
    }
    if (v.v && v.v.length) {
      const f = { t: v.t | 0, ph: v.phase, left: v.left | 0, v: v.v, p: v.p || [], ev: [] };
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
    st.pots = f.p || [];
    if (fromFrame) st.lastAt = performance.now();
  }

  function onFrame(st, f) {
    if (!f || !f.v) return;
    const now = performance.now();
    const prev = st.last;
    push(st, f, true);
    if (f.ev && f.ev.length) events(st, f, prev, now);
    const walk = (f.ph === 'start' || f.ph === 'go') && st.dir >= 0 && st.ctx && st.ctx.mine && st.ctx.playing;
    if (f.ph !== st.fph) {
      if (walk) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
      st.fph = f.ph;
    }
    if (walk && now - st.sentAt > HOLD_KEY_MS) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
    if (st.localUntil && st.meId >= 0 && f.v[st.meId * 4 + 2] === st.localDir) st.localUntil = 0;
    paintClock(st, f);
  }

  function events(st, f, prev, now) {
    const quiet = reduced();
    const esc = st.ctx.esc;
    for (const e of f.ev) {
      if (e[0] === 1) {
        const a = e[2], b = e[3];
        const from = prev && prev.v ? prev.v : f.v;
        if (!quiet) st.flies.push({ k: e[1], ax: from[a * 4], ay: from[a * 4 + 1] - 40, at: now });
        if (st.flies.length > 8) st.flies.shift();
        if (b === st.meId) {
          st.gotAt = now;
          st.giver = a;
          news(st, '🔥 Тобі тицьнули горщик! Хутко — впритул до когось і пробіл');
        } else if (a === st.meId) news(st, '😮‍💨 Горщик тепер у ' + esc(genitive(nameOf(st, b))) + '. Тікай!');
        sfx(st, 'pass');
      } else if (e[0] === 2) {
        const a = e[1], b = e[2], dz = e[3];
        st.slaps.push({ x: f.v[b * 4], y: f.v[b * 4 + 1] - 24, at: now });
        if (st.slaps.length > 8) st.slaps.shift();
        const aName = nameOf(st, a), bName = nameOf(st, b), she = FEMALE.has(bName), sheA = FEMALE.has(aName);
        if (dz === b) news(st, '✋ Ляп! ' + esc(bName) + (she ? ' оглушена — вона з живих!' : ' оглушений — він з живих!')
          + (a === st.meId ? ' Твоя рука' : ''));
        else if (a === st.meId) news(st, '✋ Ляп! А то ' + (she ? 'була просто селянка' : 'був просто селянин') + ' — у тебе іскри з очей. Усі бачили');
        else news(st, '✋ ' + esc(aName) + (sheA ? ' ляснула ' : ' ляснув ') + esc(accusative(bName)) + ' — і ' + (sheA ? 'сама отетеріла' : 'сам отетерів'));
        sfx(st, 'slap');
      } else if (e[0] === 3) {
        const id = e[2], kind = e[3], seat = e[4];
        const x = (prev && prev.v ? prev.v : f.v)[id * 4], y = (prev && prev.v ? prev.v : f.v)[id * 4 + 1] - 30;
        st.booms.push({ x, y, at: now });
        if (st.booms.length > 6) st.booms.shift();
        st.soot.set(id, now + SOOT_MS);
        if (!quiet) {
          for (let i = 0; i < 16; i++) emit(st, x, y, false, 2, true);
          for (let i = 0; i < 26; i++) emit(st, x, y, true, 0, true);
        }
        const name = nameOf(st, id), she = FEMALE.has(name);
        if (kind === 1) {
          news(st, '💥 БАХ! <b class="potato-s' + seat + '">' + esc(nickOfSeat(st, seat)) + '</b> вибуває — це ' + (she ? 'була ' : 'був ')
            + esc(name) + '!' + (seat === st.ctx.seat ? ' Не встиг' : ''));
          sfx(st, 'out');
        } else news(st, '💥 БАХ! Рвонуло в ' + esc(genitive(name)) + ' — ' + (she ? 'просто селянка' : 'просто селянин') + ', полежить і встане');
        sfx(st, 'boom');
      } else if (e[0] === 4) {
        const id = e[2];
        if (!quiet) for (let i = 0; i < 6; i++) emit(st, f.v[id * 4], f.v[id * 4 + 1] - 40, false, 1, true);
        if (id === st.meId) {
          st.gotAt = now;
          st.giver = -1;
          news(st, '🔥 Горщик — у ТЕБЕ! Хутко спихни комусь упритул');
        } else news(st, '🔥 Новий горщик — у ' + esc(genitive(nameOf(st, id))));
        sfx(st, 'spawn');
      }
    }
  }

  function news(st, html) {
    const el = st.newsEl;
    if (!el) return;
    const item = document.createElement('div');
    item.className = 'potato-ni';
    item.innerHTML = html;
    el.insertBefore(item, el.firstChild);
    while (el.children.length > 2) el.lastChild.remove();
    setTimeout(() => item.remove(), NEWS_MS);
  }

  // ---------------------------------------------------------------------------------------------
  // Позиції, камера, цілі
  // ---------------------------------------------------------------------------------------------

  function grow(st, n) {
    if (st.px.length >= n) return;
    const m = Math.max(n, st.px.length * 2);
    st.px = new Float64Array(m); st.py = new Float64Array(m);
    st.pd = new Int8Array(m); st.ps = new Int8Array(m);
  }

  /// Інтерпольовані позиції всіх селян за id — однаково для всіх, свого теж. Повертає кількість.
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
      const jump = Math.abs(x1 - x0) > 12 || Math.abs(y1 - y0) > 12;
      px[i] = jump ? x1 : x0 + (x1 - x0) * k;
      py[i] = jump ? y1 : y0 + (y1 - y0) * k;
      pd[i] = vb[j + 2];
      ps[i] = vb[j + 3] === 1 && st.vphase === 'over' ? 0 : vb[j + 3];
    }
    const me = st.meId;
    if (me >= 0 && me < n && now < st.localUntil && ps[me] < 2) {
      pd[me] = st.localDir;
      if (st.dir >= 0) ps[me] = 1;
    }
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

  /// Кому піде горщик від пробілу: найближчий на ногах без горщика впритул (той самий вибір, що на сервері).
  function passTarget(st, n, now) {
    const me = st.meId;
    if (me < 0 || me >= n) return -1;
    const x = st.px[me], y = st.py[me];
    let best = -1, bestD = PASS_RANGE * PASS_RANGE + 1;
    for (let i = 0; i < n; i++) {
      if (i === me || st.ps[i] === 2 || st.ps[i] === 3 || carrierPot(st, i) >= 0) continue;
      if (i === st.giver && now - st.gotAt < NO_BACK_MS) continue;
      const dx = st.px[i] - x, dy = st.py[i] - y, d2 = dx * dx + dy * dy;
      if (d2 < bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  /// Кого ляпне E: найближчий на ногах у конусі ±45° на 40 перед мною (як PotatoCore.SlapTarget).
  function slapTarget(st, n) {
    const me = st.meId;
    if (me < 0 || me >= n) return -1;
    const d = st.pd[me], fx = DX[d] || 0, fy = DY[d] || 0, x = st.px[me], y = st.py[me];
    let best = -1, bestD = Infinity;
    for (let i = 0; i < n; i++) {
      if (i === me || st.ps[i] >= 2) continue;
      const dx = st.px[i] - x, dy = st.py[i] - y, d2 = dx * dx + dy * dy;
      if (d2 > SLAP_RANGE * SLAP_RANGE) continue;
      const dot = dx * fx + dy * fy;
      if (dot <= 0 || dot * dot * 1000 < SLAP_CONE * d2) continue;
      if (d2 < bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  /// Лівий верхній кут в'юпорта (у режимі всієї мапи — нуль). Гравець — у центрі; глядач і вибулий — за горщиком.
  function camera(st, n) {
    const box = st.box;
    if (st.mode !== 'port') { box[0] = 0; box[1] = 0; box[2] = WW; box[3] = WH; return box; }
    const me = st.meId;
    // глядач потягнув мапу пальцем — 6 с не заважаємо йому дивитись, куди хоче
    const held = st.drag || performance.now() < st.dragUntil;
    const follow = me >= 0 && me < n && !st.drag && st.ctx && st.ctx.mine && (alive(st) || st.vphase === 'start');
    if (follow) { st.cam.x = st.px[me]; st.cam.y = st.py[me]; }
    else if (!st.drag && st.vphase === 'reveal' && st.view && st.view.reveal && st.camRound !== st.round) {
      st.camRound = st.round;
      const r = st.view.reveal, w = (r.winners || [])[0];
      const hit = (r.ids || []).find((p) => p.seat === w) || (r.ids || [])[0];
      if (hit && hit.id >= 0 && hit.id < n) { st.cam.x = st.px[hit.id]; st.cam.y = st.py[hit.id]; }
    } else if (!held && st.vphase === 'go' && st.pots.length && st.pots[0] >= 0 && st.pots[0] < n) {
      // глядач: камера м'яко пливе за першим горщиком — там уся вистава
      st.cam.x += (st.px[st.pots[0]] - st.cam.x) * 0.06;
      st.cam.y += (st.py[st.pots[0]] - st.cam.y) * 0.06;
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
    const key = S + '|' + st.mapKey.length + '|' + st.pal.grass + st.pal.earth + st.pal.wood;
    if (st.stat && st.statKey === key) return true;
    st.S = S;
    st.stat = drawStatic(st.map, st.pal, S);
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
    const dt = st.lastDraw ? Math.min(0.1, (now - st.lastDraw) / 1000) : 0.016;
    st.lastDraw = now;
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
    const quiet = reduced();

    trails(st, g, pal, phase);
    const peek = mine && (phase === 'start' || (phase === 'go' && now < st.peekUntil));
    if (peek) ring(g, st.px[st.meId], st.py[st.meId], pal, now);

    // кільця під носіями — під селянами
    const pots = st.pots;
    for (let q = 0; q * 2 < pots.length; q++) {
      const id = pots[q * 2];
      if (id >= 0 && id < n) carrierRing(g, st.px[id], st.py[id], pots[q * 2 + 1], now, pal);
    }

    g.fillStyle = pal.shadow;
    g.beginPath();
    for (let i = 0; i < n; i++) {
      const x = st.px[i], y = st.py[i];
      if (x < cx - 20 || x > cx + vw + 20 || y < cy - 20 || y > cy + vh + 20) continue;
      g.moveTo(x + 11, y + 5);
      g.ellipse(x + 1, y + 5, 10, 4, 0, 0, TAU);
    }
    g.fill();
    const look = [0, 0, 0, 0];
    for (let o = 0; o < n; o++) {
      const i = st.order[o];
      const x = st.px[i], y = st.py[i];
      if (x < cx - 20 || x > cx + vw + 20 || y < cy - 20 || y > cy + vh + 20) continue;
      look[0] = st.looks[i * 4] | 0; look[1] = st.looks[i * 4 + 1] | 0; look[2] = st.looks[i * 4 + 2] | 0; look[3] = st.looks[i * 4 + 3] | 0;
      const soot = st.soot.size && (st.soot.get(i) || 0) > now;
      villager(g, x, y + 4, st.pd[i], st.ps[i], look, now, i, pal.ink, soot);
    }
    stars(st, g, n, now);

    // дим і іскри: скільки вилітає — від того, як горщик димить
    if (!quiet && (phase === 'go')) {
      for (let q = 0; q * 2 < pots.length; q++) {
        const id = pots[q * 2], heat = pots[q * 2 + 1];
        if (id < 0 || id >= n) continue;
        const rate = [3, 8, 14, 24][heat] || 3, sparks = [0, 0, 8, 20][heat] || 0;
        st.emitAcc[q * 2] += rate * dt;
        st.emitAcc[q * 2 + 1] += sparks * dt;
        const x = st.px[id], y = st.py[id] - 50;
        while (st.emitAcc[q * 2] >= 1) { st.emitAcc[q * 2]--; emit(st, x, y, false, heat >= 2 ? 1 : 0, false); }
        while (st.emitAcc[q * 2 + 1] >= 1) { st.emitAcc[q * 2 + 1]--; emit(st, x, y, true, 0, false); }
      }
    }
    stepParts(st, dt);
    drawParts(st, g);

    // горщики — поверх усіх; щойно переданий ще летить від того, хто дав
    for (let q = 0; q * 2 < pots.length; q++) {
      const id = pots[q * 2];
      if (id < 0 || id >= n) continue;
      let x = st.px[id], y = st.py[id] - (st.ps[id] >= 2 ? 16 : 40);
      for (const f of st.flies) {
        if (f.k !== q || now - f.at >= FLY_MS) continue;
        const p = (now - f.at) / FLY_MS;
        x = f.ax + (x - f.ax) * p;
        y = f.ay + (y - f.ay) * p - Math.sin(p * Math.PI) * 14;
      }
      pot(g, x, y, pots[q * 2 + 1], now, q, pal);
    }
    booms(st, g, now);
    slaps(st, g, now);

    const carry = mine ? myPot(st) : -1;
    if (mine && alive(st) && phase === 'go' && playing) {
      if (carry >= 0) {
        const t = now - st.gotAt >= HOLD_MS ? passTarget(st, n, now) : -1;
        if (t >= 0) marker(g, st.px[t], st.py[t] - 30, '#ff7a1a', pal);
      } else if (st.ps[st.meId] !== 4) {
        const t = slapTarget(st, n);
        if (t >= 0) marker(g, st.px[t], st.py[t] - 30, pal.danger, pal, true);
      }
    }
    labels(st, g, pal, n, phase);

    g.setTransform(k, 0, 0, k, 0, 0);
    shade(st, g, pal, cv.w, cv.h, cx, cy, phase, playing, mine, now, carry);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);
    if (peek) meArrow(g, st.px[st.meId], st.py[st.meId], pal, true);
    if (mine && now < st.tipUntil && st.tip) tipPlate(g, st, st.px[st.meId], st.py[st.meId], pal);
    g.setTransform(k, 0, 0, k, 0, 0);
    if (st.mode === 'port') {
      offscreen(st, g, pal, n, cx, cy, vw, vh, now);
      minimap(st, g, pal, cv.w, cx, cy, vw, vh, now, mine, n);
    }
    if (playing && st.lastAt && now - st.lastAt > 600 && phase !== 'over') {
      g.font = '600 12px system-ui, sans-serif';
      g.textAlign = 'left';
      g.textBaseline = 'top';
      outlined(g, 'з\'єднання…', 8, 8, pal.text, pal.ink);
    }

    const took = performance.now() - t0;
    st.perf.sum += took;
    st.perf.n++;
    if (took > st.perf.max) st.perf.max = took;
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

  /// Позначка над тим, кому піде горщик (помаранчева) чи кого ляпну (червона долоня) — бачу лише я.
  function marker(g, x, y, col, pal, hand) {
    g.fillStyle = col;
    g.strokeStyle = pal.ink;
    g.lineWidth = 1.5;
    g.beginPath();
    g.moveTo(x - 6, y - 6); g.lineTo(x + 6, y - 6); g.lineTo(x, y + 1); g.closePath();
    g.fill(); g.stroke();
    if (hand) {
      g.font = '10px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'bottom';
      g.fillText('✋', x, y - 7);
    }
  }

  function tipPlate(g, st, x, y, pal) {
    const px = Math.max(12, 13 * (st.cssK ? 1 / st.cssK : 1));
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const w = g.measureText(st.tip).width + px, h = px + 8;
    const vx = st.box[0], vw = st.box[2];
    const tx = clamp(x, vx + w / 2 + 4, vx + vw - w / 2 - 4), ty = Math.max(st.box[1] + h, y - 56);
    g.fillStyle = 'rgba(12, 22, 14, .88)';
    g.beginPath(); g.roundRect(tx - w / 2, ty - h / 2, w, h, h / 2); g.fill();
    g.strokeStyle = pal.danger;
    g.lineWidth = 1.5;
    g.stroke();
    g.fillStyle = pal.text;
    g.fillText(st.tip, tx, ty + 0.5);
  }

  /// Зірочки над оглушеними й отетерілими (стан 4) — однаково, хоч хто.
  function stars(st, g, n, now) {
    g.font = '9px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillStyle = '#ffe28a';
    for (let i = 0; i < n; i++) {
      if (st.ps[i] !== 4) continue;
      const x = st.px[i], y = st.py[i] - 27;
      for (let s = 0; s < 3; s++) {
        const a = now / 140 + (s * TAU) / 3 + i;
        g.fillText('✦', x + Math.cos(a) * 9, y + Math.sin(a) * 3.5);
      }
    }
  }

  function booms(st, g, now) {
    const list = st.booms;
    for (let i = list.length - 1; i >= 0; i--) if (now - list[i].at > BOOM_MS) list.splice(i, 1);
    for (const b of list) {
      const p = (now - b.at) / BOOM_MS;
      if (p < 0.35) {
        const q = p / 0.35;
        g.globalAlpha = 1 - q;
        g.fillStyle = '#fff3b0';
        g.beginPath(); g.arc(b.x, b.y, 10 + q * 44, 0, TAU); g.fill();
        g.fillStyle = '#ff7a1a';
        g.beginPath(); g.arc(b.x, b.y, 6 + q * 30, 0, TAU); g.fill();
      }
      g.globalAlpha = 1 - p;
      g.strokeStyle = '#ff9a3a';
      g.lineWidth = 3;
      g.beginPath(); g.arc(b.x, b.y + 20, 12 + p * 70, 0, TAU); g.stroke();
      g.font = '900 ' + Math.round(16 + 10 * Math.min(1, p * 4)) + 'px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      outlined(g, 'БАХ!', b.x, b.y - 26 - p * 18, '#ffd23a', '#3a1406');
      g.globalAlpha = 1;
    }
  }

  function slaps(st, g, now) {
    const list = st.slaps;
    for (let i = list.length - 1; i >= 0; i--) if (now - list[i].at > SLAP_MS) list.splice(i, 1);
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    for (const s of list) {
      const p = (now - s.at) / SLAP_MS;
      g.globalAlpha = 1 - p;
      g.font = '900 ' + Math.round(13 + 5 * Math.min(1, p * 5)) + 'px system-ui, sans-serif';
      outlined(g, 'ЛЯП!', s.x, s.y - p * 16, '#fff', '#7a1010');
      g.globalAlpha = 1;
    }
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
      g.strokeStyle = 'rgba(12, 22, 14, .55)';
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

  /// Ніки над гравцями: над вибулими — завжди, над усіма — на розкритті (⭐ переможцю раунду, 🏆 — партії).
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
      a.y = st.py[id] - (st.ps[id] >= 2 && st.ps[id] <= 3 ? 16 : 36);
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
      g.fillStyle = 'rgba(12, 22, 14, .82)';
      g.beginPath(); g.roundRect(a.x - a.w / 2, a.y - h / 2, a.w, h, h / 2); g.fill();
      g.fillStyle = pal.seats[a.seat] || pal.text;
      g.fillText(a.text, a.x, a.y + 0.5);
    }
  }

  function shade(st, g, pal, w, h, cx, cy, phase, playing, mine, now, carry) {
    const v = st.view;
    if (!v) return;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const room = st.ctx && st.ctx.room;
    if (!playing && room && room.status === 'lobby') {
      g.fillStyle = 'rgba(10, 20, 12, .45)';
      g.fillRect(0, 0, w, h);
      if (st.mode === 'port') {
        fitFont(g, 'щойно господар натисне «Почати»', w * 0.9, 20, 700);
        outlined(g, 'Толока збереться,', w / 2, h / 2 - 13, pal.text, pal.ink);
        outlined(g, 'щойно господар натисне «Почати»', w / 2, h / 2 + 13, pal.text, pal.ink);
      } else {
        const msg = 'Толока збереться, щойно господар натисне «Почати»';
        fitFont(g, msg, w * 0.9, Math.round(w / 32), 700);
        outlined(g, msg, w / 2, h / 2, pal.text, pal.ink);
      }
      return;
    }
    const minPx = 14 / (st.cssK || 1);
    // горщик у моїх руках — краї екрана жевріють (бачу лише я): не прогав, що це в тебе
    if (phase === 'go' && carry >= 0 && alive(st)) {
      const heat = st.pots[carry * 2 + 1] | 0;
      const p = 0.5 + 0.5 * Math.sin(now / (heat >= 3 ? 90 : 220));
      g.strokeStyle = heat >= 2 ? '#ff4a1a' : '#ff8a2a';
      g.globalAlpha = 0.25 + 0.2 * p + heat * 0.1;
      g.lineWidth = 10 + heat * 3;
      g.strokeRect(0, 0, w, h);
      g.globalAlpha = 1;
    }
    if (phase === 'start') {
      if (mine) spotlight(st, g, w, h, cx, cy, 0.42);
      const left = st.last ? st.last.left : v.left;
      g.font = '800 ' + Math.round(h / 5) + 'px system-ui, sans-serif';
      outlined(g, String(Math.max(1, Math.ceil((left * TICK_MS) / 1000))), w / 2, h / 2, pal.text, pal.ink);
      const sub = mine ? 'Роздивись: ти — під стрілкою' : 'Толока збирається…';
      const px = Math.round(Math.max(h / 24, minPx));
      fitFont(g, sub, w * 0.92, px, 700);
      outlined(g, sub, w / 2, h / 2 + h / 7, pal.text, pal.ink);
      if (mine) {
        const how = st.mode === 'port' ? 'Горщик 🔥 — спихни впритул · ✋ — ляпни' : 'Зараз з\'явиться горщик 🔥 — хто його тримає, не знає, коли рвоне. Спихни впритул, ляпни підозрілого';
        fitFont(g, how, w * 0.92, Math.round(px * 0.9), 600);
        outlined(g, how, w / 2, h / 2 + h / 7 + px * 1.5, pal.accent, pal.ink);
      }
      return;
    }
    if (phase === 'go' && mine && now < st.peekUntil) {
      const left = st.peekUntil - now;
      spotlight(st, g, w, h, cx, cy, 0.46 * Math.min(1, left / 300));
    }
    if (phase === 'reveal' || phase === 'over' || (!playing && v.phase === 'over')) {
      g.fillStyle = 'rgba(10, 20, 12, .28)';
      g.fillRect(0, 0, w, h);
      const title = phase === 'reveal' ? revealTitle(st, st.mode === 'port') : overTitle(st);
      const port = st.mode === 'port', left = port ? w - 108 : w, mid = left / 2;
      fitFont(g, title, left - (port ? 24 : 40), Math.round(Math.max(minPx, h / 22)), 800);
      const tw = g.measureText(title).width + 28;
      const th = Math.max(24, h / 13), ty = port ? 8 : Math.round(h * 0.3 - th / 2);
      // плашка посеред толоки накриває чиїсь ніки: прочитали за 2,5 с — вона тьмяніє, і видно, хто під нею стояв
      const key = phase + st.round;
      if (st.plateKey !== key) { st.plateKey = key; st.plateAt = now; }
      const a = clamp(1 - (now - st.plateAt - 2500) / 600, 0.28, 1);
      g.globalAlpha = a;
      g.fillStyle = 'rgba(10, 20, 12, .76)';
      g.beginPath(); g.roundRect(mid - tw / 2, ty, tw, th, 10); g.fill();
      g.fillStyle = pal.text;
      g.fillText(title, mid, ty + th / 2);
      g.globalAlpha = 1;
    }
  }

  function spotlight(st, g, w, h, cx, cy, alpha) {
    const x = st.px[st.meId] - cx, y = st.py[st.meId] - cy - 8;
    g.fillStyle = 'rgba(10, 20, 12, ' + alpha.toFixed(3) + ')';
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
      case 'last': return head + '🔥 На ногах лише ' + who;
      case 'time': return head + '⏱ Час! Найхолодніші руки — ' + who;
      default: return head + '⏱ Час! Ніхто не взяв раунду';
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

  /// В'юпорті телефона: горщик поза екраном — стрілка з 🔥 на краю, звідки чекати біди.
  function offscreen(st, g, pal, n, cx, cy, vw, vh, now) {
    const pots = st.pots;
    for (let q = 0; q * 2 < pots.length; q++) {
      const id = pots[q * 2];
      if (id < 0 || id >= n || id === st.meId) continue;
      const x = st.px[id], y = st.py[id];
      if (x >= cx && x <= cx + vw && y >= cy && y <= cy + vh) continue;
      const mx = cx + vw / 2, my = cy + vh / 2, dx = x - mx, dy = y - my;
      const s = Math.min((vw / 2 - 16) / Math.abs(dx || 1e-6), (vh / 2 - 16) / Math.abs(dy || 1e-6));
      const ex = vw / 2 + dx * s, ey = vh / 2 + dy * s, a = Math.atan2(dy, dx);
      const heat = pots[q * 2 + 1];
      g.globalAlpha = 0.7 + 0.3 * Math.sin(now / (heat >= 3 ? 80 : 240));
      g.fillStyle = heat >= 2 ? '#ff4a1a' : '#ff8a2a';
      g.beginPath();
      g.moveTo(ex + Math.cos(a) * 10, ey + Math.sin(a) * 10);
      g.lineTo(ex + Math.cos(a + 2.5) * 9, ey + Math.sin(a + 2.5) * 9);
      g.lineTo(ex + Math.cos(a - 2.5) * 9, ey + Math.sin(a - 2.5) * 9);
      g.closePath();
      g.fill();
      g.globalAlpha = 1;
    }
  }

  function minimap(st, g, pal, w, cx, cy, vw, vh, now, mine, n) {
    if (!st.mini) return;
    const mw = 96, mh = 64, x0 = w - mw - 6, y0 = 6;
    g.globalAlpha = 0.92;
    g.drawImage(st.mini, x0, y0, mw, mh);
    g.globalAlpha = 1;
    g.strokeStyle = 'rgba(0, 0, 0, .6)';
    g.lineWidth = 1;
    g.strokeRect(x0 - 0.5, y0 - 0.5, mw + 1, mh + 1);
    const sx = mw / WW, sy = mh / WH;
    const pots = st.pots;
    for (let q = 0; q * 2 < pots.length; q++) {
      const id = pots[q * 2];
      if (id < 0 || id >= n) continue;
      g.fillStyle = '#ff5a1a';
      g.beginPath(); g.arc(x0 + st.px[id] * sx, y0 + st.py[id] * sy, 3 + Math.sin(now / 150), 0, TAU); g.fill();
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
    const text = ph === 'go' ? '⏱ ' + clock(left) : ph === 'start' ? '⏱ ' + clock(ROUND_TICKS) : '⏱ —';
    if (el.textContent !== text) el.textContent = text;
    const hot = ph === 'go' && left * TICK_MS <= 10000;
    if (el.classList.contains('hot') !== hot) el.classList.toggle('hot', hot);
  }

  function hud(st) {
    const ctx = st.ctx, v = st.view, el = st.hudEl;
    if (!el || !v) return;
    const me = v.me;
    let html = '<span class="potato-chip">Раунд ' + Math.max(1, v.round | 0) + '/' + (v.of | 0) + '</span>';
    if (me && v.phase !== 'lobby') {
      // скільки я тримав горщик — бачу лише я (іншим — на розкритті)
      html += '<span class="potato-chip potato-held" title="Скільки горщик був у тебе цього раунду: хто найменше — той і бере раунд на час">🔥 '
        + secs(me.held | 0) + ' с</span>';
    }
    const seats = v.seats || [];
    const active = seats.filter((s) => !s.out);
    if (v.phase !== 'lobby') html += '<span class="potato-chip">на ногах ' + active.filter((s) => s.alive).length + '/' + active.length + '</span>';
    html += '<button type="button" class="potato-mute" data-pad-skip title="Звук">' + (st.mute ? '🔇' : '🔊') + '</button>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.querySelector('.potato-chips').innerHTML = html;
    }
    let row = '';
    for (const s of seats) {
      row += '<span class="potato-seat potato-s' + s.seat + (s.alive ? '' : ' dead') + (s.out ? ' out' : '') + (s.seat === ctx.seat ? ' me' : '') + '">'
        + '<i></i>' + ctx.esc(s.nick) + ' <b>' + (s.total | 0) + '</b>' + (s.held != null ? ' <small>🔥' + secs(s.held) + '</small>' : '') + '</span>';
    }
    const se = st.seatsEl;
    if (se && se.dataset.sig !== row) {
      se.dataset.sig = row;
      se.innerHTML = row;
    }
  }

  function placeSeats(root, st) {
    const se = st.seatsEl;
    if (!se) return;
    if (st.mode === 'port') {
      if (se.parentNode !== root || se.nextSibling) root.appendChild(se);
    } else if (se.parentNode !== st.hudEl) st.hudEl.appendChild(se);
  }

  /// Підсумок — фішками за очками партії: разом, а дрібно — що дав раунд і скільки тримав горщик.
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
      html = rows.map((x) => '<span class="potato-sc potato-s' + x.seat + (champ.includes(x.seat) ? ' win' : '') + (x.alive ? '' : ' dead') + '">'
        + (champ.includes(x.seat) ? (over ? '🏆 ' : '⭐ ') : '') + '<b>' + st.ctx.esc(nickOfSeat(st, x.seat)) + '</b> '
        + '<em>' + total(x.seat) + '</em> <small title="за раунд: очки, скільки тримав горщик, підкинуті, що рвонули, влучні ляпаси">+' + x.pts
        + ' · 🔥' + secs(x.held) + ' с' + (x.burns ? ' · 💥' + x.burns : '') + (x.slaps ? ' · ✋' + x.slaps : '') + '</small></span>').join('');
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      el.hidden = !html;
    }
    if (st.stageEl) st.stageEl.classList.toggle('potato-open', open);
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

  /// Спихнути горщик: найближчому впритул або тому, по кому клікнули. Що можна перевірити тут — перевіряємо тут.
  function pass(st, id) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    if (st.me && !st.me.alive) { refuse(st, 'Тебе вже рознесло — дивись, хто кого'); return; }
    if (phaseOf(st) === 'go' && myPot(st) < 0) { refuse(st, 'Нема в тебе горщика'); return; }
    if (now - st.gotAt < HOLD_MS) {
      // щойно впіймав і вже тисне — не сваримо, а передаємо, щойно дозволять (пів секунди): так і задумано
      if (!st.passT) st.passT = setTimeout(() => { st.passT = 0; pass(st, id); }, HOLD_MS - (now - st.gotAt) + 30);
      return;
    }
    ctx.act('pass', id == null ? {} : { id }).then((r) => {
      if (r && !r.ok) refuse(st, r.message, true);
    });
  }

  function slap(st, id) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    if (st.me && !st.me.alive) { refuse(st, 'Тебе вже рознесло — дивись, хто кого'); return; }
    if (now - st.slapAt < SLAP_COOL_MS) { refuse(st, 'Рука ще не відійшла'); return; }
    if (id == null) {
      const n = positions(st, now);
      const t = slapTarget(st, n);
      id = t >= 0 ? t : null;
    }
    ctx.act('slap', id == null ? {} : { id }).then((r) => {
      if (r && r.ok) st.slapAt = performance.now();
      else if (r) refuse(st, r.message, true);
    });
  }

  function peek(st) { st.peekUntil = performance.now() + PEEK_MS; wake(st); }

  function toWorld(st, e) {
    const r = st.cv.el.getBoundingClientRect();
    const [cx, cy, vw, vh] = st.box;
    return [cx + ((e.clientX - r.left) / r.width) * vw, cy + ((e.clientY - r.top) / r.height) * vh, r];
  }

  function pickVillager(st, x, y, reach) {
    const n = st.last ? st.last.v.length >> 2 : 0;
    let best = -1, bestD = reach * reach;
    for (let i = 0; i < n; i++) {
      if (i === st.meId || st.ps[i] === 2 || st.ps[i] === 3) continue;
      const dx = st.px[i] - x, dy = st.py[i] - 9 - y, d2 = dx * dx + dy * dy;
      if (d2 <= bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  function canAct(st) {
    const ctx = st.ctx;
    return !!(ctx && ctx.mine && ctx.playing && alive(st) && phaseOf(st) === 'go');
  }

  /// Клік чи тап по селянину: з горщиком у руках — передати йому, без — ляпнути.
  function clickOn(st, id) {
    if (myPot(st) >= 0) pass(st, id);
    else slap(st, id);
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
          st.drag = true;
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
      const id = pickVillager(st, x, y, 20);
      const me = st.meId;
      let on = false;
      if (id >= 0 && me >= 0) {
        const dx = st.px[id] - st.px[me], dy = st.py[id] - st.py[me], reach = myPot(st) >= 0 ? PASS_MAX : SLAP_MAX;
        on = dx * dx + dy * dy <= reach * reach;
      }
      if (on !== st.hover) { st.hover = on; el.style.cursor = on ? 'pointer' : ''; }
    });
    const up = (e) => {
      const d = st.down;
      if (!d || d.id !== e.pointerId) return;
      st.down = null;
      if (st.drag) st.dragUntil = performance.now() + 6000;
      st.drag = false;
      if (d.moved || e.type === 'pointercancel' || !canAct(st)) return;
      const [x, y] = toWorld(st, e);
      const id = pickVillager(st, x, y, e.pointerType === 'mouse' ? 20 : 26);
      if (id >= 0) clickOn(st, id);
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
    el.className = 'potato-pad';
    const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
    el.innerHTML = '<div class="potato-dirs" role="group" aria-label="хрестовина: тримай і веди пальцем">'
      + [3, 2, 0, 1].map((d) => '<span class="potato-arr" data-dir="' + d + '">' + label[d] + '</span>').join('')
      + '<button type="button" class="potato-peek" data-act="peek" aria-label="де я">👁</button>'
      + '</div><div class="potato-acts">'
      + '<button type="button" data-act="pass" aria-label="передати горщик">🔥</button>'
      + '<button type="button" data-act="slap" aria-label="ляпас">✋</button></div>';
    const dirs = el.querySelector('.potato-dirs');
    const arrows = dirs.querySelectorAll('.potato-arr');
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
        if (b.dataset.act === 'pass') return pass(st, null);
        if (b.dataset.act === 'slap') return slap(st, null);
        if (b.dataset.act === 'peek') return peek(st);
        return;
      }
      if (!e.target.closest('.potato-dirs')) return;
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

  /// Телефон лежачи: мапа посередині за висотою екрана, хрестовина ліворуч, кнопки праворуч (клас potato-land).
  function phoneLand() {
    return HGames.ui.coarse() && window.innerWidth > window.innerHeight && window.innerHeight <= 500;
  }
  /// Скільки в'юпорта вільно: каркасний ui.fit(), а без нього — липка шапка й нижні панелі з CSS-змінних.
  function fitNow() {
    if (HGames.ui.fit) return HGames.ui.fit();
    const cs = getComputedStyle(document.documentElement);
    const n = (v) => parseFloat(cs.getPropertyValue(v)) || 0;
    const hd = document.querySelector('header');
    const top = hd && getComputedStyle(hd).position === 'sticky' ? hd.getBoundingClientRect().height : 0;
    const vv = window.visualViewport;
    return { w: vv ? vv.width : window.innerWidth, h: vv ? vv.height : window.innerHeight, top, dock: n('--tabs-h') + n('--mini-h') };
  }

  /// Мапа 3:2 має влізти у вікно разом зі статусом і кнопками під нею (як у «Юрми»: міряємо, де сцена починається
  /// і скільки займає все під нею до низу картки, плюс смужка пада на Деку). Не вужче 420.
  function sizeStage(st, mode) {
    const el = st.stageEl;
    if (!el || !el.isConnected) return;
    if (st.land) {                               // лежачи: уся висота смуги — мапі, без нижньої межі 480
      const f = fitNow();
      const lw = clamp(Math.floor((f.h - f.top - f.dock - 12) * 1.5), 240, 960);
      if (Math.abs((parseFloat(el.style.maxWidth) || 0) - lw) >= 3) el.style.maxWidth = lw + 'px';
      return;
    }
    if (mode === 'port') { if (el.style.maxWidth) el.style.maxWidth = ''; return; }
    const r = el.getBoundingClientRect();
    const card = el.closest('.gtable');
    const below = Math.max(48, card ? card.getBoundingClientRect().bottom - r.bottom : 92) + 22;
    const h = window.innerHeight - (r.top + (window.scrollY || 0)) - below - st.padH;
    // толока менша за інші мапи (768 од.): на екрані з DPR 1 ширша за 768 px мапа розтягувалась і мила ніки й людей —
    // тож не ширше, ніж дає роздільність канваса (на Retina й телефонах — як і було, до 960)
    const w = clamp(Math.floor(h * 1.5), 420, Math.min(960, Math.round(WW * Math.max(1, window.devicePixelRatio || 1))));
    const cur = parseFloat(el.style.maxWidth) || 0;
    if (Math.abs(cur - w) >= 3) el.style.maxWidth = w + 'px';
  }

  function fit(root, st) {
    wake(st);
    padStrip(st);
    const cw = root.clientWidth;
    const land = phoneLand(), turned = land !== !!st.land;
    if (turned) { st.land = land; root.classList.toggle('potato-land', land); st.fitFor = ''; }
    let mode = st.mode;
    if (land) mode = 'full';
    else if (cw) mode = cw < 560 ? 'port' : cw > 600 ? 'full' : st.mode;
    sizeStage(st, mode);
    if (!st.cv || mode !== st.mode) {
      st.mode = mode;
      st.cv = HGames.ui.canvas(st.stageEl, mode === 'port' ? { w: PW, h: PH, cls: 'potato-board potato-port' } : { w: WW, h: WH, cls: 'potato-board' });
      st.stageEl.classList.toggle('potato-portmode', mode === 'port');
      placeSum(root, st);
      placePad(root, st);
      placeSeats(root, st);
    } else st.cv.resize();
    if (turned) setTimeout(() => fitPhone(st), 80);
    const css = st.cv.el.clientWidth;
    if (css) st.cssK = css / (mode === 'port' ? PW : WW);
  }

  /// Телефон: шапка столу з вісьмома місцями штовхала мапу вниз, і кнопки опинялись під нижнім меню — видно було
  /// або мапу, або кнопки. Раз на партію (room.startedAt), коли вона пішла, прокручуємо сторінку так, щоб рядок стану
  /// гри став під шапку сайту: тоді мапа й кнопки вміщаються разом. Якщо й так усе видно — не чіпаємо.
  function fitPhone(st) {
    const ctx = st.ctx, padEl = st.padEl;
    if (!ctx || !ctx.mine || !ctx.playing || !ctx.room || !st.hudEl || !padEl || !HGames.ui.coarse()) return;
    const key = ctx.room.startedAt || '';
    if (st.fitFor === key) return;
    // лежачи хрестовина й кнопки стоять обабіч мапи, тож у кадр треба саму мапу
    const a = (st.land ? st.stageEl : st.hudEl).getBoundingClientRect(), b = (st.land ? st.stageEl : padEl).getBoundingClientRect();
    if (!a.height || !b.height) return;              // картку чи кнопки зараз не видно — спробуємо на наступному виді
    st.fitFor = key;
    const head = document.querySelector('header');
    let top = (head ? head.getBoundingClientRect().bottom : 0) + 4;
    const tabs = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--tabs-h')) || 0;
    // кнопки мають стати над нижнім меню й над плаваючою кнопкою балачки столу («💬 Стіл»)
    const fab = document.querySelector('.tchat.drawer:not(.open) .tc-head');
    const fr = fab && fab.getBoundingClientRect();
    let limit = (fr && fr.height ? Math.min(fr.top, innerHeight - tabs) : innerHeight - tabs) - 6;
    if (HGames.ui.fit) { const f = HGames.ui.fit(); top = f.top + 4; limit = f.h - f.dock - 6; }
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
      if (!st.visible || (!live && now - st.wakeAt > IDLE_MS)) { st.raf = 0; st.lastDraw = 0; return; }
      if (!document.hidden) draw(st);
      else st.lastDraw = 0;
      st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  HGames.register({
    id: 'potato',
    icon: ICON,
    seatNames: SEAT_NAMES,
    seatClass: ['x', 'o', 'c', 'd', 'potato-b', 'potato-p', 'potato-v', 'potato-r'],
    pad: {
      dirs: true,
      a: 'Space',
      x: 'KeyE',
      on(btn, ctx) {
        if (btn === 'lb' || btn === 'rb') { if (ctx && ctx._potato) peek(ctx._potato); return true; }
        return false;
      },
      hint: '{dpad} іти · {a} передай горщик · {x} ляпас · {lb} де я?',
    },
    added: '2026-09-27',          // нова гра: «🆕» у лобі два тижні тим, хто ще не грав
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Гарячий горщик',
      items: [
        '🔥 Толокою ходить горщик із жаром — і от-от рвоне. Коли — не знає ніхто, лише дим густішає',
        '👥 Гравці — такі самі селяни, як усі на толоці. Ніхто не знає, хто з юрми живий',
        '🤲 Горщик у тебе? Підійди впритул і тисни пробіл (Ⓐ) — або клікни по селянину',
        '✋ E (Ⓧ) — ляпас: гравця оглушиш на 3 с (саме час підкинути горщик!), а ляснувши бота — сам отетерієш',
        '🏆 Рвонуло в руках — вибув на раунд. Раунд бере останній на ногах, а на час — хто найменше тримав горщик',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      root.classList.add('potato-body');
      st.hudEl = document.createElement('div');
      st.hudEl.className = 'potato-hud';
      st.hudEl.innerHTML = '<span class="potato-chip potato-clock">⏱ —</span><span class="potato-chips"></span>';
      st.clockEl = st.hudEl.querySelector('.potato-clock');
      st.seatsEl = document.createElement('div');
      st.seatsEl.className = 'potato-seats';
      st.hudEl.appendChild(st.seatsEl);
      st.hudEl.addEventListener('click', (e) => {
        if (!e.target.closest('.potato-mute')) return;
        st.mute = !st.mute;
        try { localStorage.setItem('potatoMute', st.mute ? '1' : ''); } catch { /* приватне вікно */ }
        if (!st.mute) unlock(st);
        hud(st);
      });
      st.newsEl = document.createElement('div');
      st.newsEl.className = 'potato-news';
      st.newsEl.setAttribute('aria-live', 'polite');
      st.sumEl = document.createElement('div');
      st.sumEl.className = 'potato-sum';
      st.sumEl.hidden = true;
      st.stageEl = document.createElement('div');
      st.stageEl.className = 'potato-stage';
      st.stageEl.appendChild(st.newsEl);
      root.append(st.hudEl, st.stageEl, st.sumEl);
      st.cv = HGames.ui.canvas(st.stageEl, { w: WW, h: WH, cls: 'potato-board' });
      wireCanvas(st);
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => fit(root, st));
        st.ro.observe(root);
      }
      st.onResize = () => fit(root, st);
      window.addEventListener('resize', st.onResize);
      // поворот, ⛶ і шторка міняють місце під мапу без resize вікна — каркас кличе нас сам
      if (HGames.ui.onFit) HGames.ui.onFit(root, () => fit(root, st));
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
      // F5 посеред партії: стрілка, затиснута до перезавантаження, не має вести в тин
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
        if (st.padEl.classList.contains('potato-off') !== off) st.padEl.classList.toggle('potato-off', off);
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
      const st = ctx._potato;
      if (!st || !ctx.mine || !ctx.playing) return false;
      unlock(st);
      const d = dirOf(e);
      if (d !== undefined) {
        if (!st.keys.includes(d)) st.keys.push(d);
        else if (st.keys[st.keys.length - 1] !== d) { st.keys.splice(st.keys.indexOf(d), 1); st.keys.push(d); }
        want(st);
        return true;
      }
      if (isPass(e)) { if (!e.repeat) pass(st, null); return true; }
      if (isSlap(e)) { if (!e.repeat) slap(st, null); return true; }
      if (isPeek(e)) { peek(st); return true; }
      return false;
    },

    status(ctx) {
      const st = ctx._potato;
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f) return '';
      const ph = f.ph || f.phase;
      if (ph === 'start') return ctx.mine ? 'Роздивись: стрілка показує тебе' : 'Толока збирається…';
      if (ph === 'reveal') {
        const v = st && st.view, s = Math.max(1, Math.ceil(((f.left | 0) * TICK_MS) / 1000));
        return v && v.round >= v.of ? 'Підсумок партії за ' + s + ' с' : 'Наступний раунд за ' + s + ' с';
      }
      if (ph !== 'go') return '';
      if (!ctx.mine) return HGames.ui.coarse() ? 'Вгадуй разом із гравцями, хто з толоки живий · тягни мапу пальцем' : 'Вгадуй разом із гравцями, хто з толоки живий';
      if (st && st.me && !st.me.alive) return 'Тебе рознесло — дивись, хто кого';
      if (st && myPot(st) >= 0) {
        return window.HPad && window.HPad.on ? '🔥 Горщик у тебе! Впритул до когось і Ⓐ' : HGames.ui.coarse()
          ? '🔥 Горщик у тебе! Впритул до когось і 🔥 (чи тиць по ньому)' : '🔥 Горщик у тебе! Впритул до когось і пробіл (чи клік по ньому)';
      }
      if (window.HPad && window.HPad.on) return 'Стік — іти · Ⓐ передай горщик · Ⓧ ляпас · LB — де я?';
      return HGames.ui.coarse()
        ? 'Хрестовина — іти · 🔥 передай · ✋ ляпас · 👁 де я? · тиць по селянину'
        : 'Стрілки/WASD — іти · пробіл — передай горщик · E — ляпас · Q — де я? · клік по селянину';
    },

    unmount(root) {
      const st = root._potato;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (st.ro) st.ro.disconnect();
      if (st.io) st.io.disconnect();
      if (st.audio) { try { st.audio.close(); } catch { /* уже закритий */ } }
      root._potato = null;
    },
  });
})();
