/*
  Юрма (crowd) — Hidden in Plain Sight на сільському ярмарку. Реалтайм 25 Гц: сервер тикає раз на 40 мс і шле кадр,
  ми згладжуємо його й малюємо. Усіх селян — і ботів, і гравців, і себе — малюємо однаково, з кадрів через інтерполяцію:
  так свого не видасть ні лаг, ні «підсмикування». Передбачаємо лише косметику свого: поворот і анімацію ходи на keydown.

  Кадр (Impl/Crowd.cs): { t, ph: 'start'|'go'|'reveal'|'over', left, v: [x, y, d, s] × N, ev: [[1, a, b, kind, seat] | [2, stall]] }
    s: 0 стоїть, 1 іде, 2 лежить бот, 3 мертвий гравець; d: 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.
  Вид (Hidden, свій кожному): { phase, round, of, left, t, n, map, stalls, looks, names, v, seats, dead, me, reveal, result }
    me — лише своєму місцю: { id, list, done, stones, cool, buyCool, haggle, alive }.

  Ввід: Input('move', { dir }) лише на зміну (-1 — відпустив); Act('shoot', {} | { id }); Act('buy', {} | { stall }).
*/
(() => {
  // в'юпорт телефона — 4:3: на вузькому екрані вища мапа важить більше, ніж ширша
  const TICK_MS = 40, CELL = 32, WW = 960, WH = 640, PW = 480, PH = 360;
  const DX = [1, 0, -1, 0], DY = [0, 1, 0, -1];
  const SHOT_RANGE = 160, SHOT_MAX = 190, CONE = 671;
  const PEEK_MS = 1500, FLASH_MS = 1500, STONE_MS = 160, DUST_MS = 400, STARS_MS = 600, NEWS_MS = 6000, LOCAL_MS = 250;
  const SHOT_COOL_MS = 1000, BUY_COOL_MS = 2000, HAGGLE_MS = 1000;
  // затиснуту стрілку підтверджуємо раз на секунду: сервер відпускає її сам, якщо 3 с не чув (обрив зв'язку)
  const HOLD_MS = 1000, TIP_MS = 1800, F5_PEEK_MS = 3000;
  const TAU = Math.PI * 2;

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true"><circle cx="4" cy="6" r="2.2" fill="var(--clay)"/>'
    + '<circle cx="8" cy="5" r="2.2" fill="var(--accent)"/><circle cx="12" cy="6" r="2.2" fill="var(--ok)"/>'
    + '<path d="M2 14c0-3 1-4 2-4s2 1 2 4M6 13c0-3 1-4 2-4s2 1 2 4M10 14c0-3 1-4 2-4s2 1 2 4" fill="none" stroke="var(--text)" stroke-width="1.4" stroke-linecap="round"/></svg>';

  const SEAT_NAMES = ['жовтий', 'зелений', 'рудий', 'сірий', 'синій', 'рожевий', 'фіолетовий', 'червоний'];
  const SEAT_VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'],
    ['--crowd-blue', '#6fb3e8'], ['--crowd-pink', '#e88ac0'], ['--crowd-violet', '#b48ae8'], ['--crowd-red', '#e86a6a']];
  // Одяг — це не тема, а палітра ярмарку: червоний, синій, зелений, жовтий, білий, фіолетовий, помаранчевий, чорний.
  const CLOTH = ['#c0392b', '#2f6db5', '#3f9142', '#e8c33a', '#f2efe6', '#7d4fa8', '#e07b2a', '#2a2a2a'];
  const HAIR = ['#2b1d14', '#4a2f1d', '#7a4b27', '#c9a15a', '#8c4a2f', '#3b2a20', '#6b4a2e', '#1a1410'];
  const SKIN = ['#f3d0b0', '#e2b48c', '#c48a5e', '#8d5a3b'];
  const CLOTH_F = ['червоній', 'синій', 'зеленій', 'жовтій', 'білій', 'фіолетовій', 'помаранчевій', 'чорній'];
  const CLOTH_M = ['червоному', 'синьому', 'зеленому', 'жовтому', 'білому', 'фіолетовому', 'помаранчевому', 'чорному'];
  const HAT_WORD = ['хустці', 'капелюсі', 'картузі'];
  const FEMALE = new Set(['Параска', 'Ганна', 'Одарка', 'Марічка', 'Оксана', 'Домаха', 'Соломія', 'Мотря', 'Ярина', 'Христя',
    'Марта', 'Устя', 'Килина', 'Наталка', 'Пріська', 'Гафія', 'Софійка', 'Орися', 'Настя', 'Явдоха', 'Меланка', 'Феся', 'Текля',
    'Зоряна', 'Уляна', 'Люба', 'Дарина', 'Олеся', 'Марійка', 'Ганнуся', 'Варка', 'Катря']);
  // навіси лотків A…L: теракота, червоний, мед, пшениця, яблуко, сир, пряник, вишивка, дерево, писанка, ковбаса, соняшник
  const AWNING = ['#c5763a', '#c8413b', '#e2a93b', '#d4a95e', '#5a9e4b', '#d8c05a', '#9a6a3a', '#d64545', '#a8773f', '#8a5bb0', '#9b3b3b', '#f0b429'];

  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  const KEY_DIRS = { d: 0, 'в': 0, s: 1, 'і': 1, a: 2, 'ф': 2, w: 3, 'ц': 3 };
  const dirOf = (e) => {
    const d = DIRS[e.code];
    return d !== undefined ? d : KEY_DIRS[String(e.key || '').toLowerCase()];
  };
  const isShoot = (e) => e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar';
  const isBuy = (e) => e.code === 'KeyE' || e.code === 'KeyX' || e.code === 'Enter' || e.code === 'NumpadEnter'
    || (!e.code && ['e', 'x', 'у', 'ч', 'Enter'].includes(String(e.key || '').toLowerCase()));
  const isPeek = (e) => e.code === 'KeyQ' || e.code === 'Tab' || (!e.code && ['q', 'й'].includes(String(e.key || '').toLowerCase()));

  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const readMute = () => { try { return localStorage.getItem('crowdMute') === '1'; } catch { return false; } };

  // ---------------------------------------------------------------------------------------------
  // Палітра і статика (трава, стежки, лотки, карусель…) — один раз в offscreen-канвас
  // ---------------------------------------------------------------------------------------------

  function palette() {
    const c = HGames.ui.css;
    return {
      grass: c('--crowd-grass', '#6f9a4e'),
      grass2: c('--crowd-grass2', '#5f8a42'),
      path: c('--crowd-path', '#c9ab7a'),
      wood: c('--crowd-wood', '#8a5a33'),
      canvas: c('--crowd-canvas', '#f4ecd8'),
      shadow: c('--crowd-shadow', 'rgba(20, 30, 15, .32)'),
      text: c('--text', '#ecf1ea'),
      accent: c('--accent', '#f4c542'),
      danger: c('--danger', '#e57373'),
      shade: c('--gshade', 'rgba(15, 31, 24, .62)'),
      ink: '#1d241c',
      seats: SEAT_VARS.map(([n, f]) => c(n, f)),
    };
  }

  function lcg(seed) {
    let s = seed >>> 0;
    return () => ((s = (Math.imul(s, 1103515245) + 12345) >>> 0) / 4294967296);
  }

  function drawStatic(map, stalls, pal, S, labelPx) {
    const c = document.createElement('canvas');
    c.width = Math.round(WW * S);
    c.height = Math.round(WH * S);
    const g = c.getContext('2d');
    g.scale(S, S);
    const at = (x, y) => (map[y] && map[y][x]) || '#';
    const rnd = lcg(2709);

    // трава з плямами й кущиками
    g.fillStyle = pal.grass;
    g.fillRect(0, 0, WW, WH);
    g.fillStyle = pal.grass2;
    for (let i = 0; i < 700; i++) {
      g.beginPath();
      g.ellipse(rnd() * WW, rnd() * WH, 3 + rnd() * 7, 2 + rnd() * 3, rnd() * 3, 0, TAU);
      g.fill();
    }
    g.strokeStyle = 'rgba(255, 255, 220, .16)';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = 0; i < 420; i++) {
      const x = rnd() * WW, y = rnd() * WH;
      g.moveTo(x, y); g.lineTo(x - 1.5, y - 4);
      g.moveTo(x + 2, y); g.lineTo(x + 3, y - 3.5);
    }
    g.stroke();

    // стежки: утоптана земля з м'якими краями й камінцями
    g.fillStyle = pal.path;
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (at(x, y) === '=') {
          g.beginPath();
          g.roundRect(x * CELL - 1, y * CELL - 1, CELL + 2, CELL + 2, 7);
          g.fill();
        }
    g.fillStyle = 'rgba(80, 55, 30, .22)';
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (at(x, y) === '=')
          for (let k = 0; k < 3; k++) {
            g.beginPath();
            g.arc(x * CELL + 4 + rnd() * 24, y * CELL + 4 + rnd() * 24, 0.8 + rnd() * 1.4, 0, TAU);
            g.fill();
          }

    // паркан по краю
    g.fillStyle = 'rgba(0, 0, 0, .18)';
    g.fillRect(0, 0, WW, CELL); g.fillRect(0, WH - CELL, WW, CELL); g.fillRect(0, 0, CELL, WH); g.fillRect(WW - CELL, 0, CELL, WH);
    g.fillStyle = pal.wood;
    g.fillRect(CELL - 9, CELL - 9, WW - 2 * CELL + 18, 5);
    g.fillRect(CELL - 9, WH - CELL + 4, WW - 2 * CELL + 18, 5);
    g.fillRect(CELL - 9, CELL - 9, 5, WH - 2 * CELL + 18);
    g.fillRect(WW - CELL + 4, CELL - 9, 5, WH - 2 * CELL + 18);
    g.fillStyle = '#5e3b20';
    for (let x = CELL - 10; x <= WW - CELL + 4; x += CELL) { g.fillRect(x, CELL - 11, 7, 9); g.fillRect(x, WH - CELL + 2, 7, 9); }
    for (let y = CELL - 10; y <= WH - CELL + 4; y += CELL) { g.fillRect(CELL - 11, y, 9, 7); g.fillRect(WW - CELL + 2, y, 9, 7); }

    // дерева й копиці
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++) {
        const ch = at(x, y), cx = x * CELL + 16, cy = y * CELL + 16;
        if (ch === 'T') tree(g, cx, cy, rnd);
        else if (ch === 'Y') hay(g, cx, cy);
      }

    // криниця, карусель, сцена — за першою клітинкою блоку
    const first = (ch) => {
      for (let y = 0; y < map.length; y++) { const x = map[y].indexOf(ch); if (x >= 0) return [x, y]; }
      return null;
    };
    const w = first('W'); if (w) well(g, w[0] * CELL + 32, w[1] * CELL + 32, pal);
    const o = first('O'); if (o) carousel(g, o[0] * CELL + 64, o[1] * CELL + 48, pal);
    const s = first('S'); if (s) stage(g, s[0] * CELL, s[1] * CELL, pal);

    for (const st of stalls || []) stall(g, st, pal, labelPx || 9);
    return c;
  }

  function tree(g, x, y, rnd) {
    g.fillStyle = 'rgba(10, 25, 5, .28)';
    g.beginPath(); g.ellipse(x + 5, y + 7, 17, 12, 0, 0, TAU); g.fill();
    g.fillStyle = '#6b4424';
    g.beginPath(); g.arc(x, y + 2, 4, 0, TAU); g.fill();
    const greens = ['#2f6b2c', '#3b7d33', '#4c9142'];
    for (let i = 0; i < 3; i++) {
      g.fillStyle = greens[i];
      g.beginPath();
      g.arc(x + (i - 1) * 5 + rnd() * 2, y - 3 + (i === 1 ? -4 : 0), 11 - i * 2, 0, TAU);
      g.fill();
    }
    g.fillStyle = 'rgba(255, 255, 200, .18)';
    g.beginPath(); g.arc(x - 4, y - 8, 4, 0, TAU); g.fill();
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

  function carousel(g, x, y, pal) {
    g.fillStyle = 'rgba(0, 0, 0, .25)';
    g.beginPath(); g.arc(x + 4, y + 6, 50, 0, TAU); g.fill();
    g.fillStyle = '#b7894f';
    g.beginPath(); g.arc(x, y, 48, 0, TAU); g.fill();
    g.strokeStyle = pal.wood;
    g.lineWidth = 2;
    g.beginPath(); g.arc(x, y, 47, 0, TAU); g.stroke();
    // шість конячок по колу
    for (let i = 0; i < 6; i++) {
      const a = (i / 6) * TAU + 0.26, hx = x + Math.cos(a) * 41, hy = y + Math.sin(a) * 41;
      g.fillStyle = i % 2 ? '#f4efe3' : '#7a4a2a';
      g.beginPath(); g.ellipse(hx, hy, 6.5, 3.5, a + Math.PI / 2, 0, TAU); g.fill();
      g.beginPath(); g.arc(hx + Math.cos(a + Math.PI / 2) * 6, hy + Math.sin(a + Math.PI / 2) * 6, 2.6, 0, TAU); g.fill();
    }
    // смугастий дашок
    for (let i = 0; i < 12; i++) {
      g.fillStyle = i % 2 ? '#f4ecd8' : '#c8413b';
      g.beginPath(); g.moveTo(x, y); g.arc(x, y, 32, (i / 12) * TAU, ((i + 1) / 12) * TAU); g.closePath(); g.fill();
    }
    g.strokeStyle = 'rgba(0, 0, 0, .25)';
    g.lineWidth = 1;
    g.beginPath(); g.arc(x, y, 32, 0, TAU); g.stroke();
    g.fillStyle = '#e2b93b';
    g.beginPath(); g.arc(x, y, 5, 0, TAU); g.fill();
  }

  function stage(g, x0, y0, pal) {
    const w = 3 * CELL, h = 2 * CELL;
    g.fillStyle = 'rgba(0, 0, 0, .25)';
    g.fillRect(x0 + 4, y0 + 6, w, h);
    g.fillStyle = '#a57443';
    g.fillRect(x0 + 1, y0 + 1, w - 2, h - 2);
    g.strokeStyle = 'rgba(60, 35, 15, .45)';
    g.lineWidth = 1;
    g.beginPath();
    for (let yy = y0 + 9; yy < y0 + h; yy += 9) { g.moveTo(x0 + 2, yy); g.lineTo(x0 + w - 2, yy); }
    g.stroke();
    g.strokeStyle = pal.wood;
    g.lineWidth = 2;
    g.strokeRect(x0 + 1, y0 + 1, w - 2, h - 2);
    // двоє музик: скрипаль і цимбаліст
    const man = (cx, cy, shirt, hat) => {
      g.fillStyle = shirt; g.beginPath(); g.arc(cx, cy, 8, 0, TAU); g.fill();
      g.fillStyle = hat; g.beginPath(); g.arc(cx, cy - 1, 5, 0, TAU); g.fill();
    };
    man(x0 + 28, y0 + 30, '#f2efe6', '#2a2a2a');
    g.fillStyle = '#7a3e14';
    g.beginPath(); g.ellipse(x0 + 38, y0 + 36, 7, 3.5, -0.6, 0, TAU); g.fill();
    g.strokeStyle = '#e8d9b0'; g.lineWidth = 1;
    g.beginPath(); g.moveTo(x0 + 32, y0 + 44); g.lineTo(x0 + 46, y0 + 30); g.stroke();
    man(x0 + 66, y0 + 26, '#c0392b', '#3b2a20');
    g.fillStyle = '#8a5a33';
    g.fillRect(x0 + 56, y0 + 38, 22, 10);
    g.strokeStyle = '#e8d9b0';
    g.beginPath();
    for (let i = 0; i < 5; i++) { g.moveTo(x0 + 57, y0 + 40 + i * 2); g.lineTo(x0 + 77, y0 + 40 + i * 2); }
    g.stroke();
    g.font = '11px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText('🎵', x0 + 48, y0 + 12);
  }

  function stall(g, s, pal, labelPx) {
    const x = s.x * CELL, y = s.y * CELL, w = 2 * CELL, h = CELL;
    const color = AWNING[s.i] || pal.wood;
    const toward = s.face === 3 ? 1 : -1;       // прилавок нижче корпусу (3) чи вище (1)
    g.fillStyle = 'rgba(0, 0, 0, .28)';
    g.fillRect(x + 3, y + (toward > 0 ? 5 : -2), w, h);
    // смугастий навіс
    for (let i = 0; i < 8; i++) {
      g.fillStyle = i % 2 ? pal.canvas : color;
      g.fillRect(x + i * 8, y + 1, 8, h - 2);
    }
    g.fillStyle = 'rgba(0, 0, 0, .18)';
    g.fillRect(x, toward > 0 ? y + h - 5 : y + 1, w, 4);
    g.strokeStyle = 'rgba(40, 20, 5, .55)';
    g.lineWidth = 1.2;
    g.strokeRect(x + 0.5, y + 1.5, w - 1, h - 3);
    // фестони на краю навісу з боку прилавка
    g.fillStyle = color;
    const ey = toward > 0 ? y + h - 1 : y + 1;
    for (let i = 0; i < 8; i++) {
      g.beginPath(); g.arc(x + i * 8 + 4, ey, 4, toward > 0 ? 0 : Math.PI, toward > 0 ? Math.PI : TAU); g.fill();
    }
    // товар
    g.font = '20px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(s.emoji, x + w / 2, y + h / 2 + 1);
    // табличка з назвою — з боку, протилежного прилавку: там не стоять покупці, і назву ніхто не затуляє. На малій
    // мапі (Дек, телефон) шрифт більший — модуль рахує його від справжньої ширини мапи.
    g.font = '600 ' + labelPx + 'px system-ui, sans-serif';
    const label = s.name;
    const tw = g.measureText(label).width + labelPx * 0.9, th = labelPx + 4;
    const ly = toward > 0 ? y - th / 2 + 1 : y + h + th / 2 - 1;
    g.fillStyle = 'rgba(250, 244, 226, .94)';
    g.beginPath(); g.roundRect(x + w / 2 - tw / 2, ly - th / 2, tw, th, 3); g.fill();
    g.strokeStyle = 'rgba(58, 36, 18, .35)';
    g.lineWidth = 1;
    g.stroke();
    g.fillStyle = '#3a2412';
    g.fillText(label, x + w / 2, ly + 0.5);
  }

  // ---------------------------------------------------------------------------------------------
  // Селяни
  // ---------------------------------------------------------------------------------------------

  /// Селянин у виді 3/4 (як ляльки на ярмарку): ноги на ходу, сорочка з крайкою, руки, голова з обличчям у бік
  /// погляду і шапка. (x, y) — точка на землі між ногами; фігурка стоїть над нею. Без save/restore і без алокацій.
  function villager(g, x, y, d, s, look, now, id, ink) {
    if (s >= 2) return lying(g, x, y, d, look, ink);
    const hat = look[0], hc = look[1], shirt = look[2], skin = look[3];
    const side = d === 0 ? 1 : d === 2 ? -1 : 0;     // праворуч / ліворуч / анфас чи спиною
    let bob = 0, step = 0;
    if (s === 1) {
      const ph = Math.sin(now * 0.0503 + id * 1.7);
      bob = ph > 0 ? -1 : 0;
      step = ph > 0 ? 1 : -1;
    }
    // ноги: на ходу по черзі виносяться вперед
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
    // сорочка з крайкою
    const cloth = CLOTH[shirt] || CLOTH[0], skinC = SKIN[skin] || SKIN[0];
    g.fillStyle = cloth;
    g.beginPath(); g.roundRect(x - 7, y - 14, 14, 13, 4); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.stroke();
    g.fillStyle = 'rgba(0, 0, 0, .22)';
    g.fillRect(x - 7, y - 6, 14, 2);
    if (d === 1) {            // вишитий комір спереду
      g.fillStyle = shirt === 0 ? '#f2efe6' : '#c0392b';
      g.fillRect(x - 1, y - 13, 2, 5);
    }
    // руки: збоку — одна махає вперед-назад, анфас — обидві обабіч
    g.fillStyle = skinC;
    g.beginPath();
    if (side) g.arc(x - side * 1 + side * 3 * step, y - 5, 2.2, 0, TAU);
    else { g.arc(x - 8, y - 6 + step, 2.2, 0, TAU); g.arc(x + 8, y - 6 - step, 2.2, 0, TAU); }
    g.fill();
    // голова
    const hx = x + side * 1.5, hy = y - 19;
    g.beginPath(); g.arc(hx, hy, 6.2, 0, TAU); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 0.9;
    g.stroke();
    // обличчя: анфас — двоє очей, збоку — одне й ніс, спиною — нічого
    g.fillStyle = ink;
    if (d === 1) {
      g.fillRect(hx - 2.8, hy - 0.2, 1.6, 1.8);
      g.fillRect(hx + 1.2, hy - 0.2, 1.6, 1.8);
    } else if (side) {
      g.fillRect(hx + side * 2.2 - 0.8, hy - 0.4, 1.6, 1.8);
      g.fillStyle = skinC;
      g.beginPath(); g.arc(hx + side * 5.8, hy + 1.2, 1.5, 0, TAU); g.fill();
    }
    const hcol = CLOTH[hc] || CLOTH[0];
    switch (hat) {
      case 0: // хустка: вкриває маківку й потилицю, вузлик під підборіддям (анфас) чи ззаду
        g.fillStyle = hcol;
        g.beginPath();
        if (d === 3) g.arc(hx, hy, 6.9, 0, TAU);
        else if (side) g.ellipse(hx - side * 1.6, hy - 1.6, 6.4, 6.2, 0, Math.PI * 0.95, Math.PI * 2.05);
        else g.arc(hx, hy - 0.4, 6.9, Math.PI * 1.02, Math.PI * 1.98);
        g.fill();
        if (side) { g.beginPath(); g.arc(hx - side * 6.4, hy + 1.6, 2, 0, TAU); g.fill(); }
        else if (d === 3) { g.beginPath(); g.arc(hx, hy + 6.4, 2.2, 0, TAU); g.fill(); }
        g.fillStyle = 'rgba(255, 255, 255, .45)';           // горошок
        g.fillRect(hx - 3, hy - 4.5, 1.3, 1.3); g.fillRect(hx + 1.5, hy - 3.2, 1.3, 1.3);
        break;
      case 1: // бриль із широкими крисами
        g.fillStyle = hcol;
        g.beginPath(); g.ellipse(hx, hy - 3.5, 10, 3.6, 0, 0, TAU); g.fill();
        g.strokeStyle = 'rgba(0, 0, 0, .35)';
        g.lineWidth = 0.8;
        g.stroke();
        g.beginPath(); g.ellipse(hx, hy - 6, 5.4, 4.2, 0, Math.PI, TAU); g.closePath(); g.fill();
        g.fillStyle = 'rgba(0, 0, 0, .28)';
        g.fillRect(hx - 5.4, hy - 6.2, 10.8, 1.6);
        break;
      case 2: // картуз: тулія й чорний козирок у бік погляду
        g.fillStyle = hcol;
        g.beginPath(); g.ellipse(hx, hy - 4.2, 6.6, 3.8, 0, Math.PI, TAU); g.closePath(); g.fill();
        g.fillRect(hx - 6.6, hy - 4.6, 13.2, 1.8);
        g.fillStyle = '#1e1e1e';
        g.beginPath();
        if (side) g.ellipse(hx + side * 6.6, hy - 3, 3.6, 1.3, 0, 0, TAU);
        else if (d === 1) g.ellipse(hx, hy - 2.5, 5.2, 1.6, 0, 0, TAU);
        g.fill();
        break;
      default: { // без шапки: чуб (анфас), потилиця (спиною), зачіска збоку
        g.fillStyle = HAIR[hc] || HAIR[0];
        g.beginPath();
        if (d === 3) g.arc(hx, hy, 6.3, 0, TAU);
        else if (side) g.ellipse(hx - side * 1.4, hy - 1.8, 6, 5.4, 0, Math.PI * 0.9, Math.PI * 2.1);
        else g.arc(hx, hy - 0.8, 6.3, Math.PI * 1.05, Math.PI * 1.95);
        g.fill();
      }
    }
  }

  /// Лежить: навзнак упоперек, голова вбік, очі ✕✕, шапка відлетіла поруч.
  function lying(g, x, y, d, look, ink) {
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
  // Стан модуля
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    let st = root._crowd;
    if (!st) {
      st = root._crowd = {
        root, ctx: null, cv: null, interp: HGames.ui.Interp(), mode: 'full', S: 1,
        view: null, n: 0, looks: [], names: [], map: null, mapKey: '', stalls: [], meId: -1, me: null,
        round: -1, vphase: '', last: null, lastAt: 0, fph: '',
        px: new Float64Array(64), py: new Float64Array(64), pd: new Int8Array(64), ps: new Int8Array(64), order: [],
        stat: null, statKey: '', mini: null, pal: null, palAt: -1e9,
        cam: { x: WW / 2, y: WH / 2 }, box: [0, 0, WW, WH], drag: null, hover: false,
        keys: [], touchDir: -1, dir: -1, localDir: -1, localUntil: 0, peekUntil: 0, sentAt: 0,
        shotAt: -1e9, buyAt: -1e9, haggleUntil: 0, tip: null, tipUntil: 0, autoPeek: false,
        flashes: [], stones: [], hitAt: new Map(), fallAt: new Map(),
        raf: 0, keyup: null, blur: null, ro: null, io: null, onResize: null, visible: true,
        padOn: false, padH: 0, padAt: 0, labelPx: 9, lab: [],
        hudEl: null, clockEl: null, newsEl: null, sumEl: null, padEl: null, stageEl: null, seatsEl: null,
        audio: null, mute: readMute(),
        perf: { sum: 0, n: 0, max: 0 },
      };
    }
    st.ctx = ctx;
    ctx._crowd = st;
    return st;
  }

  const seatOf = (st, i) => (st.view && st.view.seats || []).find((s) => s.seat === i) || null;
  const nickOfSeat = (st, i) => { const s = seatOf(st, i); return (s && s.nick) || (st.ctx && st.ctx.nickOf(i)) || SEAT_NAMES[i] || '?'; };
  const lookOf = (st, id) => { const l = st.looks; return [l[id * 4] | 0, l[id * 4 + 1] | 0, l[id * 4 + 2] | 0, l[id * 4 + 3] | 0]; };
  const nameOf = (st, id) => (st.names && st.names[id]) || 'селянин';
  const alive = (st) => !!(st.me && st.me.alive);
  /// Фаза для малювання: кадри її несуть щотика, але коли партія скінчилась посеред раунду (хтось пішов), кадрів
  /// більше нема — тоді правду каже вид.
  const phaseOf = (st) => (st.vphase === 'over' ? 'over' : st.fph || st.vphase);

  /// Відмова — тостом і підписом над своїм селянином: на Деку тости ховаються під смужкою підказок пада. Відмову
  /// сервера каркас уже показав тостом сам — тоді лише підпис.
  function refuse(st, text, toasted) {
    if (!text) return;
    if (!toasted && st.ctx) st.ctx.toast(text, 'err');
    st.tip = text === 'Підійди до лотка ближче' ? 'Стань на стежку перед лотком' : text;
    st.tipUntil = performance.now() + TIP_MS;
  }

  /// Стою на прилавку якого лотка (за інтерпольованою позицією; сервер однаково перевірить сам).
  function myCounter(st) {
    const me = st.meId;
    if (me < 0 || !st.last || me * 4 >= st.last.v.length) return -1;
    const cx = Math.floor(st.px[me] / CELL), cy = Math.floor(st.py[me] / CELL);
    for (const s of st.stalls) {
      const ry = s.face === 3 ? s.y + 1 : s.y - 1;
      if (cy === ry && (cx === s.x || cx === s.x + 1)) return s.i;
    }
    return -1;
  }

  /// «у синьому картузі» / «без шапки, у жовтій сорочці» — щоб у рядку новин описати стрільця так, як його видно.
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
      out.gain.value = 1;
      if (kind === 'shot') {
        const len = Math.floor(A.sampleRate * 0.08);
        const buf = A.createBuffer(1, len, A.sampleRate);
        const ch = buf.getChannelData(0);
        for (let i = 0; i < len; i++) ch[i] = (Math.random() * 2 - 1) * (1 - i / len);
        const src = A.createBufferSource();
        src.buffer = buf;
        const gn = A.createGain();
        gn.gain.value = 0.05;
        src.connect(gn); gn.connect(out);
        src.start(t);
      } else if (kind === 'fall') tone(110, 60, 0.07, 0.07, 'sine');
      else if (kind === 'buy') { tone(880, 880, 0.09, 0.03, 'sine'); tone(1320, 1320, 0.09, 0.025, 'sine'); }
      else if (kind === 'out') tone(420, 110, 0.2, 0.05, 'triangle');
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
      if (key !== st.mapKey) { st.mapKey = key; st.map = v.map; st.stalls = v.stalls || []; st.statKey = ''; }
    }
    st.n = v.n | 0;
    st.looks = v.looks || [];
    st.names = v.names || [];
    st.me = v.me || null;
    st.meId = st.me ? st.me.id : -1;
    // новий раунд — і коли змінився номер, і коли після розкриття/кінця знову «роздивись» (рематч на 1 раунд
    // лишає номер той самий)
    if (v.round !== st.round || (v.phase === 'start' && st.vphase && st.vphase !== 'start')) {
      st.round = v.round;
      st.camRound = -1;
      st.interp.reset();
      st.last = null;
      st.flashes.length = 0;
      st.stones.length = 0;
      st.hitAt.clear();
      st.fallAt.clear();
      st.shotAt = st.buyAt = -1e9;
      st.haggleUntil = 0;
    }
    if (st.me && st.me.haggle > 0) st.haggleUntil = Math.max(st.haggleUntil, performance.now() + st.me.haggle * TICK_MS);
    // повернувся посеред раунду (F5, реконект) — сам підсвічуємо, де ти, як на відліку
    if (st.autoPeek && st.me) {
      if (v.phase === 'go' && st.me.alive) { st.peekUntil = performance.now() + F5_PEEK_MS; st.autoPeek = false; }
      else if (v.phase === 'start') st.autoPeek = false;
    }
    if (v.v && v.v.length) {
      const f = { t: v.t | 0, ph: v.phase, left: v.left | 0, v: v.v, ev: [] };
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
    if (fromFrame) st.lastAt = performance.now();
  }

  function onFrame(st, f) {
    if (!f || !f.v) return;
    const now = performance.now();
    push(st, f, true);
    if (f.ev && f.ev.length) events(st, f, now);
    // фаза змінилась: сервер скидає «куди йти» на розкритті, тож затиснуту стрілку нагадуємо знову
    const walk = (f.ph === 'start' || f.ph === 'go') && st.dir >= 0 && st.ctx && st.ctx.mine && st.ctx.playing;
    if (f.ph !== st.fph) {
      if (walk) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
      st.fph = f.ph;
    }
    // затиснуту стрілку підтверджуємо раз на секунду: не чувши 3 с, сервер відпускає її сам (так він помічає обрив
    // зв'язку, і селянин не тисне 20 с у паркан)
    if (walk && now - st.sentAt > HOLD_MS) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
    if (st.localUntil && st.meId >= 0 && f.v[st.meId * 4 + 2] === st.localDir) st.localUntil = 0;
    paintClock(st, f);
  }

  function events(st, f, now) {
    const quiet = reduced();
    for (const e of f.ev) {
      if (e[0] === 1) {
        const a = e[1], b = e[2], kind = e[3], seat = e[4];
        const land = now + (quiet ? 0 : STONE_MS);
        st.stones.push({ ax: f.v[a * 4], ay: f.v[a * 4 + 1] - 9, bx: f.v[b * 4], by: f.v[b * 4 + 1] - 8, at: now, land });
        if (st.stones.length > 12) st.stones.shift();
        st.hitAt.set(b, land);
        st.fallAt.set(b, land);
        const name = nameOf(st, b), she = FEMALE.has(name);
        const from = 'камінець від когось ' + looksLike(st, a);
        if (kind === 1) {
          news(st, '💥 <b class="crowd-s' + seat + '">' + st.ctx.esc(nickOfSeat(st, seat)) + '</b> вибуває — це ' + (she ? 'була ' : 'був ')
            + st.ctx.esc(name) + '! ' + (a === st.meId ? 'Твій камінець' : 'Влучив хтось ' + looksLike(st, a)));
          sfx(st, 'out');
        } else {
          news(st, '🪨 ' + st.ctx.esc(name) + (she ? ' впала' : ' впав') + ' — це ' + (she ? 'просто селянка' : 'просто селянин') + '. '
            + (a === st.meId ? 'Мимо, і тебе бачили' : from[0].toUpperCase() + from.slice(1)));
          sfx(st, 'fall');
        }
        sfx(st, 'shot');       // і стрільцеві теж, з того самого кадру: мовчазний ноут за столом видав би, хто стріляв
      } else if (e[0] === 2) {
        const s = st.stalls[e[1]];
        st.flashes.push({ k: e[1], at: now });
        if (st.flashes.length > 12) st.flashes.shift();
        if (s) news(st, s.emoji + ' Хтось купив ' + st.ctx.esc(s.what));
        sfx(st, 'buy');
      }
    }
  }

  function news(st, html) {
    const el = st.newsEl;
    if (!el) return;
    const item = document.createElement('div');
    item.className = 'crowd-ni';
    item.innerHTML = html;
    el.insertBefore(item, el.firstChild);
    while (el.children.length > 2) el.lastChild.remove();
    setTimeout(() => item.remove(), NEWS_MS);
  }

  // ---------------------------------------------------------------------------------------------
  // Позиції, камера, конус
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
      ps[i] = vb[j + 3] === 1 && st.vphase === 'over' ? 0 : vb[j + 3];     // партію зіграно — ніхто не дріботить на місці
    }
    // косметика свого: миттєвий поворот і хода, поки сервер не підтвердив
    const me = st.meId;
    if (me >= 0 && me < n && now < st.localUntil && ps[me] < 2) {
      pd[me] = st.localDir;
      if (st.dir >= 0) ps[me] = 1;
    }
    // камінець ще летить — ціль ще на ногах
    if (st.hitAt.size)
      for (const [id, t] of st.hitAt) {
        if (now >= t) { st.hitAt.delete(id); continue; }
        if (id < n && ps[id] >= 2) ps[id] = 0;
      }
    // порядок малювання — за y (нижні поверх верхніх); вставками, бо юрма майже впорядкована з минулого кадру
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

  /// Той самий конус, що й на сервері (CrowdCore.Cone): найближчий стоячий у ±35° на 160 перед мною.
  function coneTarget(st, n) {
    const me = st.meId;
    if (me < 0 || me >= n) return -1;
    const d = st.pd[me], fx = DX[d] || 0, fy = DY[d] || 0, x = st.px[me], y = st.py[me];
    let best = -1, bestD = Infinity;
    for (let i = 0; i < n; i++) {
      if (i === me || st.ps[i] >= 2) continue;
      const dx = st.px[i] - x, dy = st.py[i] - y, d2 = dx * dx + dy * dy;
      if (d2 > SHOT_RANGE * SHOT_RANGE) continue;
      const dot = dx * fx + dy * fy;
      if (dot <= 0 || dot * dot * 1000 < CONE * d2) continue;
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
      // глядачеві й збитому — на розкритті камера сама їде до переможця (або до першого з гравців)
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
    // тема, DPR і розмір підписів міняються рідко: перевіряємо раз на секунду, а не склеюємо ключ щокадру
    if (st.stat && st.statKey && now - st.palAt <= 1000) return true;
    st.pal = palette();
    st.palAt = now;
    if (!st.map) return false;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const S = Math.min(2, dpr);
    const key = S + '|' + st.labelPx + '|' + st.mapKey.length + '|' + st.pal.grass + st.pal.path + st.pal.wood + st.pal.canvas;
    if (st.stat && st.statKey === key) return true;
    st.S = S;
    st.stat = drawStatic(st.map, st.stalls, st.pal, S, st.labelPx);
    st.statKey = key;
    // мінімапа для телефона — зменшена статика
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
    const bw = cv.el.width, bh = cv.el.height, k = bw / cv.w;     // пікселів канваса на одиницю
    const S = st.S;

    g.setTransform(1, 0, 0, 1, 0, 0);
    g.imageSmoothingEnabled = true;
    g.drawImage(st.stat, cx * S, cy * S, vw * S, vh * S, 0, 0, bw, bh);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);

    const phase = phaseOf(st);
    const playing = !!(st.ctx && st.ctx.playing);
    const mine = !!(st.ctx && st.ctx.mine) && st.meId >= 0 && st.meId < n;

    listMarks(st, g, pal, now, phase, mine);
    flashes(st, g, pal, now);
    trails(st, g, pal, phase);

    // «де я»: кільце під своїм — на відліку завжди, у грі — після «підглянути» (і сам після F5)
    const peek = mine && (phase === 'start' || (phase === 'go' && now < st.peekUntil));
    if (peek) ring(g, st.px[st.meId], st.py[st.meId], pal, now);

    // тіні пачкою, потім селяни за y
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
      villager(g, x, y + 4, st.pd[i], st.ps[i], look, now, i, pal.ink);
    }
    stars(st, g, pal, now, n);
    stones(st, g, pal, now);

    if (mine && alive(st) && phase === 'go' && playing) {
      const t = coneTarget(st, n);
      if (t >= 0) chevron(g, st.px[t], st.py[t] - 27, pal);
      if (now < st.haggleUntil) haggleRing(g, st.px[st.meId], st.py[st.meId], pal, 1 - (st.haggleUntil - now) / HAGGLE_MS);
    }
    labels(st, g, pal, n, phase);

    g.setTransform(k, 0, 0, k, 0, 0);
    shade(st, g, pal, cv.w, cv.h, cx, cy, phase, playing, mine, now);
    // стрілка «ти» — поверх шторки, щоб на відліку й після «де я?» її не пригасило
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);
    if (peek) meArrow(g, st.px[st.meId], st.py[st.meId], pal, true);
    if (mine && now < st.tipUntil && st.tip) tipPlate(g, st, st.px[st.meId], st.py[st.meId], pal, k);
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

  /// Шрифт розміром px, але не ширший за maxW: довгий напис на вузькому канвасі зменшуємо, а не обрізаємо.
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

  /// Мої лотки зі списку, ще не куплені, — ледь помітний пульс (бачу лише я).
  function listMarks(st, g, pal, now, phase, mine) {
    if (!mine || !st.me || !alive(st) || (phase !== 'go' && phase !== 'start')) return;
    const a = 0.35 + 0.25 * Math.sin(now / 260);
    g.strokeStyle = pal.accent;
    g.lineWidth = 2;
    g.globalAlpha = a;
    g.setLineDash([5, 4]);
    g.beginPath();
    const list = st.me.list, done = st.me.done;
    for (let i = 0; i < list.length; i++) {
      const s = st.stalls[list[i]];
      // корпус разом із прилавком: видно, куди стати, щоб поторгуватись
      if (s && !done[i]) g.roundRect(s.x * CELL - 3, (s.face === 3 ? s.y : s.y - 1) * CELL - 2, 2 * CELL + 6, 2 * CELL + 4, 7);
    }
    g.stroke();
    g.setLineDash([]);
    g.globalAlpha = 1;
  }

  function flashes(st, g, pal, now) {
    const list = st.flashes;
    for (let i = list.length - 1; i >= 0; i--) if (now - list[i].at > FLASH_MS) list.splice(i, 1);
    for (const f of list) {
      const s = st.stalls[f.k];
      if (!s) continue;
      const p = (now - f.at) / FLASH_MS;
      const x = s.x * CELL + CELL, y = s.y * CELL + CELL / 2;
      g.globalAlpha = (1 - p) * 0.55;
      g.fillStyle = '#ffe28a';
      g.beginPath(); g.roundRect(s.x * CELL - 8, Math.min(s.y * CELL, s.fy - 16) - 8, 2 * CELL + 16, 2 * CELL + 16, 12); g.fill();
      g.globalAlpha = 1 - p;
      g.strokeStyle = '#ffd24a';
      g.lineWidth = 3;
      g.beginPath(); g.arc(x, y + (s.face === 3 ? 16 : -16), 20 + p * 70, 0, TAU); g.stroke();
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

  /// Торгуюсь: кільце над собою заповнюється за секунду — бачу лише я (іншим — просто селянин, що стоїть біля лотка).
  function haggleRing(g, x, y, pal, p) {
    const cx = x, cy = y - 42;
    g.fillStyle = 'rgba(12, 22, 14, .72)';
    g.beginPath(); g.arc(cx, cy, 12, 0, TAU); g.fill();
    g.lineWidth = 3.5;
    g.strokeStyle = pal.accent;
    g.beginPath(); g.arc(cx, cy, 12, -Math.PI / 2, -Math.PI / 2 + TAU * clamp(p, 0, 1)); g.stroke();
    g.font = '12px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillStyle = pal.text;
    g.fillText('🧺', cx, cy + 0.5);
  }

  /// Підпис-відмова над своїм селянином (бачу лише я): «Стань на стежку перед лотком», «Камінці скінчились»…
  function tipPlate(g, st, x, y, pal, k) {
    const px = Math.max(12, 13 * (st.cssK ? 1 / st.cssK : 1));
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const w = g.measureText(st.tip).width + px, h = px + 8;
    const vx = st.box[0], vw = st.box[2];
    const tx = clamp(x, vx + w / 2 + 4, vx + vw - w / 2 - 4), ty = Math.max(st.box[1] + h, y - 52);
    g.fillStyle = 'rgba(12, 22, 14, .88)';
    g.beginPath(); g.roundRect(tx - w / 2, ty - h / 2, w, h, h / 2); g.fill();
    g.strokeStyle = pal.danger;
    g.lineWidth = 1.5;
    g.stroke();
    g.fillStyle = pal.text;
    g.fillText(st.tip, tx, ty + 0.5);
  }

  /// Сліди гравців за останні ≈ 20 с раунду — лише на розкритті: «я ж ішов просто за тобою!».
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
      // звідки почав: кружечок
      g.fillStyle = col;
      g.beginPath(); g.arc(p[0], p[1], 3.5, 0, TAU); g.fill();
      g.globalAlpha = 1;
    }
  }

  function chevron(g, x, y, pal) {
    g.fillStyle = pal.danger;
    g.strokeStyle = pal.ink;
    g.lineWidth = 1.5;
    g.beginPath();
    g.moveTo(x - 6, y - 6); g.lineTo(x + 6, y - 6); g.lineTo(x, y + 1); g.closePath();
    g.fill(); g.stroke();
  }

  function stars(st, g, pal, now, n) {
    if (!st.fallAt.size) return;
    g.font = '10px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillStyle = '#ffe28a';
    for (const [id, t] of st.fallAt) {
      const age = now - t;
      if (age > STARS_MS || id >= n) { if (age > STARS_MS) st.fallAt.delete(id); continue; }
      if (age < 0 || st.ps[id] !== 2) continue;
      const x = st.px[id], y = st.py[id] - 12;
      for (let s = 0; s < 3; s++) {
        const a = now / 120 + (s * TAU) / 3;
        g.fillText('✦', x + Math.cos(a) * 9, y + Math.sin(a) * 4);
      }
    }
  }

  function stones(st, g, pal, now) {
    const list = st.stones;
    for (let i = list.length - 1; i >= 0; i--) if (now - list[i].land > DUST_MS) list.splice(i, 1);
    for (const s of list) {
      if (now < s.land) {
        const p = Math.max(0, (now - s.at) / Math.max(1, s.land - s.at));
        const x = s.ax + (s.bx - s.ax) * p, y = s.ay + (s.by - s.ay) * p;
        g.strokeStyle = 'rgba(255, 255, 255, .45)';
        g.lineWidth = 1.5;
        g.beginPath(); g.moveTo(s.ax, s.ay); g.lineTo(x, y); g.stroke();
        g.fillStyle = '#5b5b5b';
        g.beginPath(); g.arc(x, y - 6 * Math.sin(p * Math.PI), 3, 0, TAU); g.fill();
      } else {
        const p = (now - s.land) / DUST_MS;
        g.globalAlpha = 0.6 * (1 - p);
        g.fillStyle = '#d8c7a4';
        for (let j = 0; j < 3; j++) {
          g.beginPath(); g.arc(s.bx + (j - 1) * 7 * p, s.by - 4 - 6 * p + j, 4 + 7 * p, 0, TAU); g.fill();
        }
        g.globalAlpha = 1;
      }
    }
  }

  /// Ніки над гравцями: над мертвими — завжди, над усіма — на розкритті. Зірочка ⭐ — переможцю раунду, а коли
  /// партію зіграно — 🏆 переможцю партії (саме тому, кого називає плашка, а не тому, хто взяв останній раунд).
  function labels(st, g, pal, n, phase) {
    const v = st.view;
    if (!v) return;
    const open = v.reveal && (phase === 'reveal' || phase === 'over' || v.phase === 'reveal' || v.phase === 'over');
    const rows = open ? v.reveal.ids || [] : v.dead || [];
    if (!rows.length) return;
    const over = phase === 'over' || v.phase === 'over';
    const win = !open ? [] : over ? (v.result && v.result.winners) || [] : v.reveal.winners || [];
    const mark = over ? '🏆 ' : '⭐ ';
    // кільце кольору місця під ногами — щоб на розкритті одразу було видно всіх «живих» у юрмі
    g.lineWidth = 2.5;
    for (let r = 0; r < rows.length; r++) {
      const id = rows[r].id, seat = rows[r].seat;
      if (id < 0 || id >= n) continue;
      g.strokeStyle = pal.seats[seat] || pal.text;
      g.beginPath(); g.ellipse(st.px[id], st.py[id] + 5, 14, 7, 0, 0, TAU); g.stroke();
    }
    // нік на темній плашці: читається і на траві, і на стежці, і на телефоні
    const px = st.mode === 'port' ? 15 : 13, h = px + 5;
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    // Двоє стоять поруч — плашки не лягають одна на одну: ідемо знизу вгору й піднімаємо ту, що наїхала на вже
    // поставлену (ніки — найсмішніший момент гри, «я ж стояв поруч!»).
    const lab = st.lab, pool = st.labPool || (st.labPool = []);
    lab.length = 0;
    for (let r = 0; r < rows.length; r++) {
      const id = rows[r].id, seat = rows[r].seat;
      if (id < 0 || id >= n) continue;
      const a = pool[lab.length] || (pool[lab.length] = { seat: 0, text: '', x: 0, y: 0, w: 0 });
      a.seat = seat;
      a.text = (win.includes(seat) ? mark : '') + nickOfSeat(st, seat);
      a.x = st.px[id];
      a.y = st.py[id] - (st.ps[id] >= 2 ? 16 : 34);
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

  function shade(st, g, pal, w, h, cx, cy, phase, playing, mine, now) {
    const v = st.view;
    if (!v) return;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const room = st.ctx && st.ctx.room;
    if (!playing && room && room.status === 'lobby') {
      g.fillStyle = 'rgba(10, 20, 12, .45)';
      g.fillRect(0, 0, w, h);
      if (st.mode === 'port') {   // вузько — двома рядками, щоб не дрібнити шрифт
        fitFont(g, 'щойно господар натисне «Почати»', w * 0.9, 22, 700);
        outlined(g, 'Ярмарок відчиниться,', w / 2, h / 2 - 14, pal.text, pal.ink);
        outlined(g, 'щойно господар натисне «Почати»', w / 2, h / 2 + 14, pal.text, pal.ink);
      } else {
        const msg = 'Ярмарок відчиниться, щойно господар натисне «Почати»';
        fitFont(g, msg, w * 0.9, Math.round(w / 34), 700);
        outlined(g, msg, w / 2, h / 2, pal.text, pal.ink);
      }
      return;
    }
    // підписи — не дрібніше за 14 CSS-пікселів, хоч би яка мала була мапа (на телефоні було ≈ 8)
    const minPx = 14 / (st.cssK || 1);
    if (phase === 'start') {
      if (mine) spotlight(st, g, w, h, cx, cy, 0.42);
      const left = st.last ? st.last.left : v.left;
      g.font = '800 ' + Math.round(h / 5) + 'px system-ui, sans-serif';
      outlined(g, String(Math.max(1, Math.ceil((left * TICK_MS) / 1000))), w / 2, h / 2, pal.text, pal.ink);
      const sub = mine ? 'Роздивись: ти — під стрілкою' : 'Ярмарок відчиняється…';
      const px = Math.round(Math.max(h / 26, minPx));
      fitFont(g, sub, w * 0.92, px, 700);
      outlined(g, sub, w / 2, h / 2 + h / 7, pal.text, pal.ink);
      if (mine && st.me) {
        // що робити за ці три секунди — новачок міг закрити «що нового»
        const list = (st.me.list || []).map((k2) => (st.stalls[k2] || {}).emoji || '').join('');
        const how = st.mode === 'port' ? 'Обійди ' + list + ' · не видай себе' : 'Обійди обведені лотки ' + list + ' і не видай себе · 🪨 — полюй';
        fitFont(g, how, w * 0.92, Math.round(px * 0.92), 600);
        outlined(g, how, w / 2, h / 2 + h / 7 + px * 1.5, pal.accent, pal.ink);
      }
      return;
    }
    // «де я?» у грі — так само, як на відліку: усе довкола пригасає, свій — у плямі світла (лише на моєму екрані)
    if (phase === 'go' && mine && now < st.peekUntil) {
      const left = st.peekUntil - now;
      spotlight(st, g, w, h, cx, cy, 0.46 * Math.min(1, left / 300));
    }
    if (phase === 'reveal' || phase === 'over' || (!playing && v.phase === 'over')) {
      g.fillStyle = 'rgba(10, 20, 12, .28)';
      g.fillRect(0, 0, w, h);
      const title = phase === 'reveal' ? revealTitle(st, st.mode === 'port') : overTitle(st);
      // уся мапа — плашка над каруселлю й сценою (там людей найменше, а ніки гравців не сховаються під неї);
      // в'юпорт телефона стежить за мною в центрі — там плашка вгорі, ліворуч від мінімапи
      const port = st.mode === 'port', left = port ? w - 108 : w, mid = left / 2;
      fitFont(g, title, left - (port ? 24 : 40), Math.round(Math.max(minPx, h / 22)), 800);
      const tw = g.measureText(title).width + 28;
      const th = Math.max(24, h / 13), ty = port ? 8 : Math.round(h * 0.36 - th / 2);
      g.fillStyle = 'rgba(10, 20, 12, .76)';
      g.beginPath(); g.roundRect(mid - tw / 2, ty, tw, th, 10); g.fill();
      g.fillStyle = pal.text;
      g.fillText(title, mid, ty + th / 2);
    }
  }

  /// Затемнення довкола свого селянина (відлік і «де я?»).
  function spotlight(st, g, w, h, cx, cy, alpha) {
    const x = st.px[st.meId] - cx, y = st.py[st.meId] - cy - 8;
    g.fillStyle = 'rgba(10, 20, 12, ' + alpha.toFixed(3) + ')';
    g.beginPath();
    g.rect(0, 0, w, h);
    g.arc(x, y, 60, 0, TAU, true);
    g.fill('evenodd');
  }

  /// short — без «Раунд N з M» (на телефоні раунд і так у фішках, а плашка вузька).
  function revealTitle(st, short) {
    const v = st.view, r = v && v.reveal;
    if (!r) return '';
    const head = short ? '' : 'Раунд ' + v.round + ' з ' + v.of + ' · ';
    const who = (r.winners || []).map((s) => nickOfSeat(st, s)).join(' і ');
    switch (r.why) {
      case 'list': return head + '🧺 Кошик повний — ' + who;
      case 'last': return head + '🪨 На ногах лише ' + who;
      case 'time': return head + '⏱ Ярмарок закрився — найповніший кошик у ' + who;
      default: return head + '⏱ Ярмарок закрився — ніхто не взяв раунду';
    }
  }

  function overTitle(st) {
    const v = st.view, res = v && v.result;
    if (!res) return 'Партію зіграно';
    const who = res.winners.map((s) => nickOfSeat(st, s) + ' ' + (res.totals[s] | 0)).join(', ');
    if (res.why === 'left') {
      // хтось пішов, і грати лишилось нікому: пояснюємо, чому все скінчилось посеред раунду
      return res.winners.length ? '🚪 Суперники розійшлись — ярмарок за ' + res.winners.map((s) => nickOfSeat(st, s)).join(', ')
        : '🚪 Усі розійшлись';
    }
    if (!res.winners.length) return '🤝 Нічия';
    return '🏆 Перемога: ' + who;
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
    for (const f of st.flashes) {
      const s = st.stalls[f.k];
      if (!s) continue;
      g.fillStyle = '#ffd24a';
      g.globalAlpha = 1 - (now - f.at) / FLASH_MS;
      g.beginPath(); g.arc(x0 + (s.x * CELL + CELL) * sx, y0 + (s.y * CELL + 16) * sy, 4, 0, TAU); g.fill();
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
    const ph = st.vphase === 'over' ? 'over' : f ? f.ph : st.vphase;     // кінець посеред раунду — годинник не завмирає на «1:20»
    const left = f ? f.left : (st.view && st.view.left) || 0;
    const text = ph === 'go' ? '⏱ ' + clock(left) : ph === 'start' ? '⏱ ' + clock(2250) : '⏱ —';
    if (el.textContent !== text) el.textContent = text;
    const hot = ph === 'go' && left * TICK_MS <= 10000;
    if (el.classList.contains('hot') !== hot) el.classList.toggle('hot', hot);
  }

  function hud(st) {
    const ctx = st.ctx, v = st.view, el = st.hudEl;
    if (!el || !v) return;
    const me = v.me;
    let html = '<span class="crowd-chip">Раунд ' + Math.max(1, v.round | 0) + '/' + (v.of | 0) + '</span>';
    if (me) {
      html += '<span class="crowd-chip crowd-stones" title="Камінці в рогатці">';
      for (let i = 0; i < 3; i++) html += '<i' + (i < me.stones ? '' : ' class="used"') + '>🪨</i>';
      html += '</span><span class="crowd-chip crowd-list" title="Твій список: обійди ці лотки"><small>Список:</small>';
      me.list.forEach((k, i) => {
        const s = st.stalls[k] || {};
        html += '<i' + (me.done[i] ? ' class="done"' : '') + ' title="' + ctx.esc(s.name || '') + '">' + (s.emoji || '?') + (me.done[i] ? '✓' : '') + '</i>';
      });
      html += '</span>';
    }
    const seats = v.seats || [];
    const active = seats.filter((s) => !s.out);
    if (v.phase !== 'lobby') html += '<span class="crowd-chip">живих ' + active.filter((s) => s.alive).length + '/' + active.length + '</span>';
    html += '<button type="button" class="crowd-mute" data-pad-skip title="Звук">' + (st.mute ? '🔇' : '🔊') + '</button>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.querySelector('.crowd-chips').innerHTML = html;
    }
    // фішки місць: рахунок, хто вибув; покупки раунду — лише на розкритті (сервер до того їх і не шле: «+1» комусь
    // одразу після спалаху назвав би покупця)
    let row = '';
    for (const s of seats) {
      row += '<span class="crowd-seat crowd-s' + s.seat + (s.alive ? '' : ' dead') + (s.out ? ' out' : '') + (s.seat === ctx.seat ? ' me' : '') + '">'
        + '<i></i>' + ctx.esc(s.nick) + ' <b>' + (s.total | 0) + '</b>' + (s.bought != null ? ' <small>🧺' + (s.bought | 0) + '</small>' : '') + '</span>';
    }
    const se = st.seatsEl;
    if (se && se.dataset.sig !== row) {
      se.dataset.sig = row;
      se.innerHTML = row;
    }
  }

  /// Фішки місць: на широкому — у тому ж рядку, що й стан гри; на телефоні — під керуванням, щоб мапа
  /// й хрестовина вміщались на екрані разом (хто за столом, і так видно в шапці картки).
  function placeSeats(root, st) {
    const se = st.seatsEl;
    if (!se) return;
    if (st.mode === 'port') {
      if (se.parentNode !== root || se.nextSibling) root.appendChild(se);
    } else if (se.parentNode !== st.hudEl) st.hudEl.appendChild(se);
  }

  /// Підсумок — рядком фішок, упорядкованих за очками ПАРТІЇ: хто веде, той і перший. У кожній — разом, а дрібно —
  /// що дав раунд. На розкритті ⭐ — переможцю раунду, після останнього — 🏆 переможцю партії (як і плашка на мапі).
  /// На широкому фішки лежать поверх низу мапи (замість новин), тож «Ще раз» не тікає за край екрана.
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
      html = rows.map((x) => '<span class="crowd-sc crowd-s' + x.seat + (champ.includes(x.seat) ? ' win' : '') + '">'
        + (champ.includes(x.seat) ? (over ? '🏆 ' : '⭐ ') : '') + '<b>' + st.ctx.esc(nickOfSeat(st, x.seat)) + '</b> '
        + '<em>' + total(x.seat) + '</em> <small title="за раунд: покупки зі списку, збиті гравці">+' + x.pts
        + ' · 🧺' + x.buy + (x.kills ? ' · 🎯' + x.kills : '') + '</small></span>').join('');
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      el.hidden = !html;
    }
    if (st.stageEl) st.stageEl.classList.toggle('crowd-open', open);
  }

  /// Підсумок: на широкому — поверх низу мапи, на телефоні — під мапою (мапа там і так крихітна).
  function placeSum(root, st) {
    const el = st.sumEl;
    if (!el) return;
    if (st.mode === 'port') { if (el.parentNode !== root || el.previousSibling !== st.stageEl) st.stageEl.after(el); }
    else if (el.parentNode !== st.stageEl) st.stageEl.appendChild(el);
  }

  /// Хрестовина — одразу під мапою або під підсумком (на телефоні).
  function placePad(root, st) {
    const el = st.padEl;
    if (!el) return;
    const after = st.sumEl && st.sumEl.parentNode === root ? st.sumEl : st.stageEl;
    if (el.previousSibling !== after) after.after(el);
  }

  // ---------------------------------------------------------------------------------------------
  // Керування
  // ---------------------------------------------------------------------------------------------

  /// Куди я зараз іду: палець на хрестовині важить більше, далі — остання затиснута клавіша.
  function want(st) {
    const d = st.touchDir >= 0 ? st.touchDir : st.keys.length ? st.keys[st.keys.length - 1] : -1;
    if (d === st.dir) return;
    st.dir = d;
    if (d >= 0) { st.localDir = d; st.localUntil = performance.now() + LOCAL_MS; }
    const ctx = st.ctx;
    if (ctx && ctx.mine && ctx.playing) { ctx.input('move', { dir: d }); st.sentAt = performance.now(); }
  }

  function shoot(st, id) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    const me = st.me;
    if (me && !me.alive) { refuse(st, 'Тебе вже збили — дивись, хто кого'); return; }
    if (me && me.stones <= 0) { refuse(st, 'Камінці скінчились'); return; }
    if (now - st.shotAt < SHOT_COOL_MS) { refuse(st, 'Рогатка ще натягується'); return; }
    if (id == null) {
      const n = positions(st, now);
      const t = coneTarget(st, n);
      id = t >= 0 ? t : null;
    }
    ctx.act('shoot', id == null ? {} : { id }).then((r) => {
      if (r && r.ok) st.shotAt = performance.now();
      else if (r) refuse(st, r.message, true);
    });
  }

  function buy(st, stall) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    if (now < st.haggleUntil) return;
    if (now - st.buyAt < BUY_COOL_MS + HAGGLE_MS) { refuse(st, 'Продавець ще рахує решту'); return; }
    ctx.act('buy', stall == null ? {} : { stall }).then((r) => {
      if (r && r.ok) { st.buyAt = performance.now(); st.haggleUntil = st.buyAt + HAGGLE_MS; }
      else if (r) refuse(st, r.message, true);
    });
  }

  function peek(st) { st.peekUntil = performance.now() + PEEK_MS; }

  /// Точка на канвасі → світ.
  function toWorld(st, e) {
    const r = st.cv.el.getBoundingClientRect();
    const [cx, cy, vw, vh] = st.box;
    return [cx + ((e.clientX - r.left) / r.width) * vw, cy + ((e.clientY - r.top) / r.height) * vh, r];
  }

  function pickVillager(st, x, y, reach) {
    const n = st.last ? st.last.v.length >> 2 : 0;
    let best = -1, bestD = reach * reach;
    for (let i = 0; i < n; i++) {
      if (i === st.meId || st.ps[i] >= 2) continue;
      const dx = st.px[i] - x, dy = st.py[i] - 7 - y, d2 = dx * dx + dy * dy;
      if (d2 <= bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  function pickStall(st, x, y) {
    for (const s of st.stalls) {
      const top = Math.min(s.y * CELL, s.fy - 16) - 16, bottom = Math.max(s.y * CELL + CELL, s.fy + 16) + 16;
      if (x >= s.x * CELL - 16 && x <= s.x * CELL + 2 * CELL + 16 && y >= top && y <= bottom) return s.i;
    }
    return -1;
  }

  /// Клік прямо в навіс/корпус лотка (не в прилавок перед ним).
  const onBody = (s, x, y) => x >= s.x * CELL - 2 && x <= s.x * CELL + 2 * CELL + 2 && y >= s.y * CELL - 2 && y <= s.y * CELL + CELL + 2;

  /// Клік чи тап по мапі: лоток чи селянин? У корпус лотка — завжди купівля, як і клік по «своєму» прилавку, на
  /// якому я стою: там стоять боти, і постріл замість покупки коштував би камінця й видав би мене всім.
  function clickAt(st, x, y, reach) {
    const k = pickStall(st, x, y);
    if (k >= 0 && (onBody(st.stalls[k], x, y) || k === myCounter(st))) return { stall: k, id: -1 };
    const id = pickVillager(st, x, y, reach);
    if (id >= 0) return { stall: -1, id };
    return { stall: k, id: -1 };
  }

  function canAct(st) {
    const ctx = st.ctx;
    return !!(ctx && ctx.mine && ctx.playing && alive(st) && phaseOf(st) === 'go');
  }

  function wireCanvas(st) {
    const el = st.cv.el;
    el.addEventListener('pointerdown', (e) => {
      unlock(st);
      st.down = { id: e.pointerId, x: e.clientX, y: e.clientY, moved: false, cam: { x: st.cam.x, y: st.cam.y } };
      if (st.mode === 'port') {
        // мінімапа: тиць — туди й камера (глядачеві й мертвому)
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
      const hit = clickAt(st, x, y, 20);
      const me = st.meId;
      let on = hit.stall >= 0;
      if (hit.id >= 0 && me >= 0) {
        const dx = st.px[hit.id] - st.px[me], dy = st.py[hit.id] - st.py[me];
        on = dx * dx + dy * dy <= SHOT_MAX * SHOT_MAX;
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
      const hit = clickAt(st, x, y, e.pointerType === 'mouse' ? 20 : 24);
      if (hit.stall >= 0) buy(st, hit.stall);
      else if (hit.id >= 0) shoot(st, hit.id);
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
    el.className = 'crowd-pad';
    const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
    // Хрестовина — одна зона: напрямок рахуємо від центру за пальцем, тож палець «переїжджає» з → на ↓, не
    // відриваючись. У центрі — 👁 «де я?» (праворуч унизу її накривала плаваюча кнопка балачки).
    el.innerHTML = '<div class="crowd-dirs" role="group" aria-label="хрестовина: тримай і веди пальцем">'
      + [3, 2, 0, 1].map((d) => '<span class="crowd-arr" data-dir="' + d + '">' + label[d] + '</span>').join('')
      + '<button type="button" class="crowd-peek" data-act="peek" aria-label="де я">👁</button>'
      + '</div><div class="crowd-acts">'
      + '<button type="button" data-act="shoot" aria-label="постріл">🪨</button>'
      + '<button type="button" data-act="buy" aria-label="купити">🧺</button></div>';
    const dirs = el.querySelector('.crowd-dirs');
    const arrows = dirs.querySelectorAll('.crowd-arr');
    const light = (d) => arrows.forEach((a) => a.classList.toggle('on', +a.dataset.dir === d));
    /// Куди показує палець: за довшою віссю від центру хрестовини; біля самого центру — лишаємо, що було.
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
        if (b.dataset.act === 'shoot') return shoot(st, null);
        if (b.dataset.act === 'buy') return buy(st, null);
        if (b.dataset.act === 'peek') return peek(st);
        return;
      }
      if (!e.target.closest('.crowd-dirs')) return;
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

  /// Скільки місця внизу вікна забирає смужка підказок пада (Дек). Читаємо лише атрибут hidden — без перерахунку
  /// розкладки; висоту міряємо, лише коли смужка з'явилась.
  function padStrip(st) {
    const hints = document.querySelector('.padhints');
    const on = !!(hints && !hints.hidden);
    if (on === st.padOn) return false;
    st.padOn = on;
    st.padH = on ? hints.offsetHeight + 8 : 0;
    return true;
  }

  /// Мапа 3:2 має влізти у вікно разом зі статусом і кнопками під нею. Ширину сцени рахуємо від того, де вона
  /// справді починається (фішок на вісьмох — два рядки), і від того, скільки справді займає все під нею до низу
  /// картки (статус, «Ще раз», поле), плюс смужка пада на Деку. Не вужче 480 — далі вже краще трохи прокрутити.
  function sizeStage(st, mode) {
    const el = st.stageEl;
    if (!el || !el.isConnected) return;
    if (mode === 'port') { if (el.style.maxWidth) el.style.maxWidth = ''; return; }
    const r = el.getBoundingClientRect();
    const card = el.closest('.gtable');
    const below = Math.max(48, card ? card.getBoundingClientRect().bottom - r.bottom : 92) + 22;
    const h = window.innerHeight - (r.top + (window.scrollY || 0)) - below - st.padH;
    const want = clamp(Math.floor(h * 1.5), 480, 960);
    const cur = parseFloat(el.style.maxWidth) || 0;
    if (Math.abs(cur - want) >= 3) el.style.maxWidth = want + 'px';
  }

  /// Режим камери — за шириною картки, а не мапи: низьке вікно ноута (1280×720, 1366×768 із вкладками) дає меншу
  /// мапу, але цілу, а в'юпорт 480×360 за своїм — лише справді вузьким екранам (телефон). Поріг із гістерезисом, щоб
  /// на межі режим не смикався туди-сюди.
  function fit(root, st) {
    padStrip(st);
    const cw = root.clientWidth;
    let mode = st.mode;
    if (cw) mode = cw < 600 ? 'port' : cw > 640 ? 'full' : st.mode;
    sizeStage(st, mode);
    if (!st.cv || mode !== st.mode) {
      st.mode = mode;
      st.cv = HGames.ui.canvas(st.stageEl, mode === 'port' ? { w: PW, h: PH, cls: 'crowd-board crowd-port' } : { w: WW, h: WH, cls: 'crowd-board' });
      st.stageEl.classList.toggle('crowd-portmode', mode === 'port');
      placeSum(root, st);
      placePad(root, st);
      placeSeats(root, st);
    } else st.cv.resize();
    // скільки CSS-пікселів на одиницю світу: від цього — розмір підписів лотків і відмов
    const css = st.cv.el.clientWidth;
    if (css) {
      st.cssK = css / (mode === 'port' ? PW : WW);
      const lp = clamp(Math.round(10.5 / st.cssK), 9, 16);
      if (lp !== st.labelPx) { st.labelPx = lp; st.statKey = ''; }
    }
  }

  function spin(st) {
    if (st.raf) return;
    const loop = (now) => {
      if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
      // раз на секунду — чи не з'явилась (зникла) смужка пада: це лише атрибут, розкладку не чіпаємо
      if (now - st.padAt > 1000) { st.padAt = now; if (padStrip(st)) fit(st.root, st); }
      if (!document.hidden && st.visible) draw(st);
      st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  HGames.register({
    id: 'crowd',
    icon: ICON,
    seatNames: SEAT_NAMES,
    seatClass: ['x', 'o', 'c', 'd', 'crowd-b', 'crowd-p', 'crowd-v', 'crowd-r'],
    pad: {
      dirs: true,
      a: 'Space',
      x: 'KeyE',
      on(btn, ctx) {
        if (btn === 'lb' || btn === 'rb') { if (ctx && ctx._crowd) peek(ctx._crowd); return true; }
        return false;
      },
      hint: '{dpad} іти · {a} рогатка · {x} купити · {lb} де я?',
    },
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Юрма',
      items: [
        '👥 Ярмарок повен селян — і ти один із них. Ніхто не знає, хто з юрми живий',
        '🧺 Обійди 4 лотки зі свого списку (вони пульсують на мапі): підійди й натисни E (Ⓧ) — лоток спалахне для всіх',
        '🪨 Три камінці в рогатці: пробіл (Ⓐ) — постріл у того, хто перед тобою, або клік по селянину. Влучив у бота — видав себе',
        '👁 Забув, хто ти? Q (LB) підсвітить тебе на півтори секунди. Тільки тобі',
        '🏆 Раунд бере перший, хто скупився, або останній живий; партія — з трьох раундів',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      root.classList.add('crowd-body');
      st.hudEl = document.createElement('div');
      st.hudEl.className = 'crowd-hud';
      st.hudEl.innerHTML = '<span class="crowd-chip crowd-clock">⏱ —</span><span class="crowd-chips"></span>';
      st.clockEl = st.hudEl.querySelector('.crowd-clock');
      st.seatsEl = document.createElement('div');
      st.seatsEl.className = 'crowd-seats';
      st.hudEl.appendChild(st.seatsEl);
      st.hudEl.addEventListener('click', (e) => {
        if (!e.target.closest('.crowd-mute')) return;
        st.mute = !st.mute;
        try { localStorage.setItem('crowdMute', st.mute ? '1' : ''); } catch { /* приватне вікно */ }
        if (!st.mute) unlock(st);
        hud(st);
      });
      st.newsEl = document.createElement('div');
      st.newsEl.className = 'crowd-news';
      st.newsEl.setAttribute('aria-live', 'polite');
      st.sumEl = document.createElement('div');
      st.sumEl.className = 'crowd-sum';
      st.sumEl.hidden = true;
      // сцена: мапа, а новини — смужкою поверх її нижнього краю (не штовхають мапу вниз і не з'їдають висоту)
      st.stageEl = document.createElement('div');
      st.stageEl.className = 'crowd-stage';
      st.stageEl.appendChild(st.newsEl);
      root.append(st.hudEl, st.stageEl, st.sumEl);
      st.cv = HGames.ui.canvas(st.stageEl, { w: WW, h: WH, cls: 'crowd-board' });
      wireCanvas(st);
      // розкладку перераховуємо лише на зміну: ширина картки (ResizeObserver), висота вікна (resize), смужка пада
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => fit(root, st));
        st.ro.observe(root);
      }
      st.onResize = () => fit(root, st);
      window.addEventListener('resize', st.onResize);
      // мапу не видно (інша вкладка сайту, прокрутили геть) — не малюємо; стан приймаємо однаково
      if (window.IntersectionObserver) {
        st.io = new IntersectionObserver((es) => { for (const e of es) st.visible = e.isIntersecting; });
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
      spin(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      applyView(st, ctx.view);
      fit(root, st);
      pad(root, st);
      placeSum(root, st);
      placePad(root, st);
      placeSeats(root, st);
      hud(st);
      summary(st);
      paintClock(st, st.last && st.last.ph ? st.last : null);
      spin(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      onFrame(st, f);
      spin(st);
    },

    onKey(e, ctx) {
      const st = ctx._crowd;
      if (!st || !ctx.mine || !ctx.playing) return false;
      unlock(st);
      const d = dirOf(e);
      if (d !== undefined) {
        if (!st.keys.includes(d)) st.keys.push(d);
        else if (st.keys[st.keys.length - 1] !== d) { st.keys.splice(st.keys.indexOf(d), 1); st.keys.push(d); }
        want(st);
        return true;
      }
      if (isShoot(e)) { if (!e.repeat) shoot(st, null); return true; }
      if (isBuy(e)) { if (!e.repeat) buy(st, null); return true; }
      if (isPeek(e)) { peek(st); return true; }
      return false;
    },

    status(ctx) {
      const st = ctx._crowd;
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f) return '';
      const ph = f.ph || f.phase;
      if (ph === 'start') return ctx.mine ? 'Роздивись: стрілка показує тебе' : 'Ярмарок відчиняється…';
      if (ph === 'reveal') {
        // заголовок уже на плашці над мапою — тут корисніше, скільки чекати
        const v = st && st.view, s = Math.max(1, Math.ceil(((f.left | 0) * TICK_MS) / 1000));
        return v && v.round >= v.of ? 'Підсумок партії за ' + s + ' с' : 'Наступний раунд за ' + s + ' с';
      }
      if (ph !== 'go') return '';
      if (!ctx.mine) return HGames.ui.coarse() ? 'Вгадуй разом із гравцями, хто з юрми живий · тягни мапу пальцем' : 'Вгадуй разом із гравцями, хто з юрми живий';
      if (st && st.me && !st.me.alive) return 'Тебе збили — дивись, хто кого';
      if (st && performance.now() < st.haggleUntil) return '🧺 Торгуєшся… ще мить — стій, не тікай';
      if (window.HPad && window.HPad.on) return 'Стік — іти · Ⓐ рогатка · Ⓧ купити · LB — де я? Купують, стоячи на стежці перед лотком';
      return HGames.ui.coarse()
        ? 'Хрестовина — іти · 🪨 постріл · 🧺 купити · 👁 де я? · тиць по селянину чи лотку'
        : 'Стрілки/WASD — іти · пробіл — рогатка · E — купити · Q — де я? · клік по селянину чи лотку';
    },

    unmount(root) {
      const st = root._crowd;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (st.ro) st.ro.disconnect();
      if (st.io) st.io.disconnect();
      if (st.audio) { try { st.audio.close(); } catch { /* уже закритий */ } }
      root._crowd = null;
    },
  });
})();
