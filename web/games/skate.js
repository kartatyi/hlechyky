/*
  Ковзанка (skate) — Hidden in Plain Sight на замерзлому ставку. Реалтайм 25 Гц: сервер тикає раз на 40 мс, рахує
  цілочисельну фізику ковзання для всіх однаково й шле кадр; ми згладжуємо його й малюємо. Усіх селян — і ботів, і
  гравців, і себе — малюємо однаково, з кадрів через інтерполяцію: свого не видасть ні лаг, ні «підсмикування».
  Передбачаємо лише косметику свого: поворот і «поштовх» на keydown.

  Кадр (Impl/Skate.cs): { t, ph: 'start'|'go'|'reveal'|'over', left, v: [x, y, d, s] × N, h: [x, y, r, k] × ополонки,
    it: [x, y, вид] × 12, ev: [[1, a, b, обидва] | [2, id, місце] | [3, id, слот, вид] | [5, k] | [6, k] | [7, id] | [8, id]] }
    s: 0 котиться, 1 відштовхується, 2 лежить, 3 у воді, 4 вибув (на березі), 5 гальмує; d: 0 праворуч, за годинниковою.
  Вид (Hidden, свій кожному): { phase, round, of, left, t, n, map, kinds, looks, names, v, h, it, seats, dead, me, reveal, result }
    me — лише своєму місцю: { id, list, got, alive }.

  Ввід: Input('move', { dir }) лише на зміну: −1 відпустив, 0…7 напрямок, 8 гальмо. Більше дій нема — решта фізика.
*/
(() => {
  // в'юпорт телефона — 4:3, як у Юрми: на вузькому екрані вища мапа важить більше, ніж ширша
  const TICK_MS = 40, CELL = 32, WW = 960, WH = 640, PW = 480, PH = 360;
  const BODY_R = 10, PICK_R = 16, WATER_TICKS = 100, ITEMS = 12, FALL_TICKS = 50;
  const DX8 = [1, 0.7071, 0, -0.7071, -1, -0.7071, 0, 0.7071], DY8 = [0, 0.7071, 1, 0.7071, 0, -0.7071, -1, -0.7071];
  const PEEK_MS = 1500, NEWS_MS = 6000, LOCAL_MS = 250, STARS_MS = 900, SPLASH_MS = 900, PICK_MS = 700, CREDIT_MS = 3000;
  // стіл стоїть (лобі, партію зіграно): стільки ще малюємо після останньої події — довше за найдовшу анімацію
  const IDLE_MS = 3200;
  // затиснутий напрямок підтверджуємо раз на секунду: сервер відпускає його сам, якщо 3 с не чув (обрив зв'язку)
  const HOLD_MS = 1000, F5_PEEK_MS = 3000;
  const TAU = Math.PI * 2;

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M4.5 2.5h4v5.2l3.4 1.2c1 .35 1.6 1.1 1.6 2V12h-9z" fill="var(--accent)"/>'
    + '<path d="M6 4.5h2M6 6.2h2" stroke="var(--bg)" stroke-width="1"/>'
    + '<path d="M2.5 14.2h11.5" stroke="var(--text)" stroke-width="1.4" stroke-linecap="round"/>'
    + '<path d="M6 12v2.2M11.5 12v2.2" stroke="var(--text)" stroke-width="1.1"/></svg>';

  const SEAT_NAMES = ['жовтий', 'зелений', 'рудий', 'сірий', 'синій', 'рожевий', 'фіолетовий', 'червоний'];
  const SEAT_VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'],
    ['--skate-blue', '#6fb3e8'], ['--skate-pink', '#e88ac0'], ['--skate-violet', '#b48ae8'], ['--skate-red', '#e86a6a']];
  // Одяг — палітра зимового села (не тема): червоний, синій, зелений, жовтий, білий, фіолетовий, брунатний, чорний.
  const CLOTH = ['#c0392b', '#2f6db5', '#3f9142', '#e8c33a', '#f2efe6', '#7d4fa8', '#8a5a33', '#2a2a2a'];
  const SKIN = ['#f3d0b0', '#e2b48c', '#c48a5e', '#8d5a3b'];
  const CLOTH_F = ['червоній', 'синій', 'зеленій', 'жовтій', 'білій', 'фіолетовій', 'брунатній', 'чорній'];
  const CLOTH_M = ['червоному', 'синьому', 'зеленому', 'жовтому', 'білому', 'фіолетовому', 'брунатному', 'чорному'];
  const HAT_WORD = ['хустці', 'вушанці', 'шапці з помпоном', 'смушевій кучмі'];
  const FEMALE = new Set(['Параска', 'Ганна', 'Одарка', 'Марічка', 'Оксана', 'Домаха', 'Соломія', 'Мотря', 'Ярина', 'Христя',
    'Марта', 'Устя', 'Килина', 'Наталка', 'Пріська', 'Гафія', 'Софійка', 'Орися', 'Настя', 'Явдоха', 'Меланка', 'Феся', 'Текля',
    'Зоряна', 'Уляна', 'Люба', 'Дарина', 'Олеся', 'Марійка', 'Ганнуся', 'Варка', 'Катря']);
  const EMOJI_FONT = '"Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';

  // Клавіші: чотири «стрілки» можна тримати по дві — вісім напрямків; e.key — фолбек для ЦФІВ.
  const ARROWS = { ArrowRight: 'r', KeyD: 'r', ArrowDown: 'd', KeyS: 'd', ArrowLeft: 'l', KeyA: 'l', ArrowUp: 'u', KeyW: 'u' };
  const KEY_ARROWS = { d: 'r', 'в': 'r', s: 'd', 'і': 'd', 'ы': 'd', a: 'l', 'ф': 'l', w: 'u', 'ц': 'u' };
  const arrowOf = (e) => ARROWS[e.code] || (!e.code ? KEY_ARROWS[String(e.key || '').toLowerCase()] : undefined);
  const isBrake = (e) => e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar' || e.code === 'ShiftLeft' || e.code === 'ShiftRight'
    || e.code === 'KeyX' || (!e.code && ['x', 'ч'].includes(String(e.key || '').toLowerCase()));
  const isPeek = (e) => e.code === 'KeyQ' || e.code === 'Tab' || (!e.code && ['q', 'й'].includes(String(e.key || '').toLowerCase()));

  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const readMute = () => { try { return localStorage.getItem('skateMute') === '1'; } catch { return false; } };
  /// Вектор → один із восьми напрямків (як SkateCore.Dir8: межі під 22,5°).
  const dir8 = (x, y) => {
    const ax = Math.abs(x), ay = Math.abs(y);
    if (ay * 256 <= ax * 106) return x >= 0 ? 0 : 4;
    if (ax * 256 <= ay * 106) return y >= 0 ? 2 : 6;
    return x >= 0 ? (y >= 0 ? 1 : 7) : (y >= 0 ? 3 : 5);
  };

  // ---------------------------------------------------------------------------------------------
  // Палітра і статика (сніг, лід, хати, ялини, очерет…) — один раз в offscreen-канвас
  // ---------------------------------------------------------------------------------------------

  function palette() {
    const c = HGames.ui.css;
    return {
      ice: c('--skate-ice', '#bfe1ee'),
      ice2: c('--skate-ice2', '#a3d0e4'),
      snow: c('--skate-snow', '#f1f5f8'),
      snow2: c('--skate-snow2', '#d7e2ea'),
      water: c('--skate-water', '#123a52'),
      wood: c('--skate-wood', '#7a4f2c'),
      text: c('--text', '#ecf1ea'),
      accent: c('--accent', '#f4c542'),
      danger: c('--danger', '#e57373'),
      ink: '#1b2530',
      shadow: 'rgba(40, 70, 95, .28)',
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
    const ice = (x, y) => at(x, y) === '.';
    const rnd = lcg(1227);

    // сніг: рівне біле поле з м'якими горбиками й блискітками
    g.fillStyle = pal.snow;
    g.fillRect(0, 0, WW, WH);
    for (let i = 0; i < 260; i++) {
      g.fillStyle = rnd() < 0.5 ? 'rgba(190, 210, 225, .25)' : 'rgba(255, 255, 255, .6)';
      g.beginPath(); g.ellipse(rnd() * WW, rnd() * WH, 6 + rnd() * 16, 3 + rnd() * 6, rnd() * 3, 0, TAU); g.fill();
    }

    // лід: клітинки, під ним — легкий градієнт глибини
    const grad = g.createLinearGradient(0, 0, WW, WH);
    grad.addColorStop(0, pal.ice);
    grad.addColorStop(0.5, pal.ice2);
    grad.addColorStop(1, pal.ice);
    g.fillStyle = grad;
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (ice(x, y)) g.fillRect(x * CELL, y * CELL, CELL, CELL);
    // прожилки й бульбашки в льоду
    g.save();
    g.beginPath();
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++)
        if (ice(x, y)) g.rect(x * CELL, y * CELL, CELL, CELL);
    g.clip();
    g.strokeStyle = 'rgba(255, 255, 255, .35)';
    g.lineWidth = 1;
    for (let i = 0; i < 70; i++) {
      const x = rnd() * WW, y = rnd() * WH, a = rnd() * TAU, l = 20 + rnd() * 60;
      g.beginPath(); g.moveTo(x, y);
      g.quadraticCurveTo(x + Math.cos(a) * l * 0.5 + rnd() * 10, y + Math.sin(a) * l * 0.5 + rnd() * 10, x + Math.cos(a) * l, y + Math.sin(a) * l);
      g.stroke();
    }
    g.fillStyle = 'rgba(255, 255, 255, .45)';
    for (let i = 0; i < 90; i++) { g.beginPath(); g.arc(rnd() * WW, rnd() * WH, 0.8 + rnd() * 1.6, 0, TAU); g.fill(); }
    g.fillStyle = 'rgba(20, 70, 100, .08)';
    for (let i = 0; i < 26; i++) { g.beginPath(); g.ellipse(rnd() * WW, rnd() * WH, 20 + rnd() * 50, 8 + rnd() * 20, rnd() * 3, 0, TAU); g.fill(); }
    g.restore();

    // берег: заметілі хвилями заходять на лід — спершу тінь, потім сніг
    const drifts = [];
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++) {
        if (ice(x, y)) continue;
        const cx = x * CELL, cy = y * CELL;
        // кучугури різного розміру, трохи вглиб і назовні — щоб край не був намистом
        // (ex, ey) — початок ребра, (ix, iy) — уздовж нього, (nx, ny) — у бік льоду
        const lump = (ex, ey, ix, iy, nx, ny) => {
          for (let k = 0; k < 4; k++) {
            const t = (k + 0.2 + rnd() * 0.6) / 4, r = 6 + rnd() * 9, sink = -4 + rnd() * 7;
            drifts.push([ex + ix * t * CELL + nx * sink, ey + iy * t * CELL + ny * sink, r]);
          }
        };
        if (ice(x, y + 1)) lump(cx, cy + CELL, 1, 0, 0, 1);
        if (ice(x, y - 1)) lump(cx, cy, 1, 0, 0, -1);
        if (ice(x + 1, y)) lump(cx + CELL, cy, 0, 1, 1, 0);
        if (ice(x - 1, y)) lump(cx, cy, 0, 1, -1, 0);
      }
    g.fillStyle = 'rgba(60, 100, 130, .22)';
    for (const [x, y, r] of drifts) { g.beginPath(); g.arc(x + 2, y + 3, r, 0, TAU); g.fill(); }
    g.fillStyle = pal.snow;
    for (const [x, y, r] of drifts) { g.beginPath(); g.arc(x, y, r, 0, TAU); g.fill(); }
    g.fillStyle = 'rgba(255, 255, 255, .8)';
    for (const [x, y, r] of drifts) { g.beginPath(); g.arc(x - r * 0.3, y - r * 0.35, r * 0.35, 0, TAU); g.fill(); }

    // оздоби берега й льоду
    const first = (ch) => {
      for (let y = 0; y < map.length; y++) { const x = map[y].indexOf(ch); if (x >= 0) return [x, y]; }
      return null;
    };
    const island = first('I');
    if (island) islandAt(g, (island[0] + 0.5) * CELL + 16, (island[1] + 1.5) * CELL, pal);
    const boat = first('K');
    if (boat) boatAt(g, boat[0] * CELL + CELL, boat[1] * CELL + 16, pal);
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++) {
        const ch = at(x, y), cx = x * CELL + 16, cy = y * CELL + 16;
        if (ch === 'R') reeds(g, cx, cy, rnd);
        else if (ch === 'T') fir(g, cx, cy, rnd);
      }
    // хати й лавка — парами клітинок
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++) {
        if (at(x, y) === 'H' && at(x - 1, y) !== 'H') hut(g, x * CELL + CELL, y * CELL + 16, pal, rnd);
        if (at(x, y) === 'B' && at(x - 1, y) !== 'B') bench(g, x * CELL + CELL, y * CELL + 16, pal);
      }
    return c;
  }

  function fir(g, x, y, rnd) {
    g.fillStyle = 'rgba(40, 70, 90, .22)';
    g.beginPath(); g.ellipse(x + 5, y + 12, 14, 6, 0, 0, TAU); g.fill();
    g.fillStyle = '#5b3a1e';
    g.fillRect(x - 2, y + 6, 4, 7);
    const tiers = [[16, 13], [12, 3], [8, -6]];
    for (const [w, ty] of tiers) {
      g.fillStyle = '#2f5a3a';
      g.beginPath(); g.moveTo(x - w, y + ty + 6); g.lineTo(x + w, y + ty + 6); g.lineTo(x, y + ty - 10); g.closePath(); g.fill();
      g.fillStyle = '#f7fafc';
      g.beginPath(); g.moveTo(x - w * 0.7, y + ty + 1); g.lineTo(x + w * 0.55, y + ty + 2 + rnd() * 2); g.lineTo(x, y + ty - 8); g.closePath(); g.fill();
    }
  }

  function reeds(g, x, y, rnd) {
    g.lineWidth = 1.4;
    for (let i = 0; i < 7; i++) {
      const bx = x - 12 + rnd() * 24, by = y + 8 + rnd() * 6, h = 14 + rnd() * 14, lean = (rnd() - 0.5) * 8;
      g.strokeStyle = rnd() < 0.5 ? '#a58b52' : '#8a7040';
      g.beginPath(); g.moveTo(bx, by); g.quadraticCurveTo(bx + lean * 0.3, by - h * 0.6, bx + lean, by - h); g.stroke();
      if (rnd() < 0.6) {
        g.fillStyle = '#6b4a2a';
        g.beginPath(); g.ellipse(bx + lean, by - h - 3, 1.8, 4, lean * 0.05, 0, TAU); g.fill();
      }
    }
  }

  function hut(g, x, y, pal, rnd) {
    // біла хата під стріхою, засипаною снігом; віконце світиться
    g.fillStyle = 'rgba(40, 70, 90, .25)';
    g.fillRect(x - 26, y + 6, 58, 12);
    g.fillStyle = '#efe9dc';
    g.fillRect(x - 24, y - 6, 48, 20);
    g.strokeStyle = 'rgba(80, 60, 40, .4)';
    g.lineWidth = 1;
    g.strokeRect(x - 24, y - 6, 48, 20);
    g.fillStyle = '#ffd87a';
    g.fillRect(x - 15, y - 1, 8, 7);
    g.fillRect(x + 7, y - 1, 8, 7);
    g.strokeStyle = pal.wood;
    g.strokeRect(x - 15, y - 1, 8, 7);
    g.strokeRect(x + 7, y - 1, 8, 7);
    g.fillStyle = pal.wood;
    g.fillRect(x - 3, y + 2, 6, 12);
    g.fillStyle = '#b08a4e';
    g.beginPath(); g.moveTo(x - 30, y - 5); g.lineTo(x + 30, y - 5); g.lineTo(x + 18, y - 20); g.lineTo(x - 18, y - 20); g.closePath(); g.fill();
    g.fillStyle = '#f7fafc';
    g.beginPath(); g.moveTo(x - 28, y - 8); g.quadraticCurveTo(x, y - 12 - rnd() * 2, x + 28, y - 8); g.lineTo(x + 17, y - 21); g.lineTo(x - 17, y - 21); g.closePath(); g.fill();
    g.fillStyle = '#8c8c8c';
    g.fillRect(x + 8, y - 26, 5, 8);
  }

  function bench(g, x, y, pal) {
    // лавка й вогнище, де гріють руки (полум'я домальовує живий шар)
    g.fillStyle = pal.wood;
    g.fillRect(x - 28, y - 10, 22, 5);
    g.fillRect(x - 26, y - 5, 3, 7);
    g.fillRect(x - 11, y - 5, 3, 7);
    g.fillStyle = '#5b3a1e';
    g.beginPath(); g.ellipse(x + 12, y + 4, 12, 5, 0, 0, TAU); g.fill();
    g.strokeStyle = '#4a2e16';
    g.lineWidth = 3;
    g.beginPath(); g.moveTo(x + 3, y + 6); g.lineTo(x + 21, y + 1); g.moveTo(x + 3, y + 1); g.lineTo(x + 21, y + 6); g.stroke();
  }

  function islandAt(g, x, y, pal) {
    g.fillStyle = 'rgba(60, 100, 130, .25)';
    g.beginPath(); g.ellipse(x + 3, y + 5, 62, 44, 0, 0, TAU); g.fill();
    g.fillStyle = pal.snow;
    g.beginPath(); g.ellipse(x, y, 60, 42, 0, 0, TAU); g.fill();
    g.fillStyle = 'rgba(190, 210, 225, .5)';
    g.beginPath(); g.ellipse(x + 10, y + 12, 40, 20, 0, 0, TAU); g.fill();
    // верба: стовбур і звислі голі гілки
    g.fillStyle = '#5b3a1e';
    g.fillRect(x - 3, y - 8, 6, 16);
    g.strokeStyle = '#6d4a2a';
    g.lineWidth = 1.2;
    for (let i = 0; i < 14; i++) {
      const a = -Math.PI / 2 + (i - 7) * 0.2, sx = x + Math.cos(a) * 6, sy = y - 10 + Math.sin(a) * 6;
      g.beginPath(); g.moveTo(x, y - 8);
      g.quadraticCurveTo(sx + Math.cos(a) * 20, sy + Math.sin(a) * 18, sx + Math.cos(a) * 26, sy + 16);
      g.stroke();
    }
  }

  function boatAt(g, x, y, pal) {
    g.fillStyle = 'rgba(40, 70, 90, .25)';
    g.beginPath(); g.ellipse(x + 3, y + 5, 32, 12, 0, 0, TAU); g.fill();
    g.fillStyle = pal.wood;
    g.beginPath(); g.moveTo(x - 32, y); g.quadraticCurveTo(x, y - 18, x + 32, y); g.quadraticCurveTo(x, y + 18, x - 32, y); g.fill();
    g.fillStyle = '#9a6a3a';
    g.beginPath(); g.moveTo(x - 26, y); g.quadraticCurveTo(x, y - 12, x + 26, y); g.quadraticCurveTo(x, y + 12, x - 26, y); g.fill();
    g.fillStyle = pal.wood;
    g.fillRect(x - 12, y - 8, 3, 16);
    g.fillRect(x + 8, y - 8, 3, 16);
    g.fillStyle = '#f7fafc';
    g.beginPath(); g.ellipse(x - 2, y - 2, 14, 5, 0, 0, TAU); g.fill();
  }

  // ---------------------------------------------------------------------------------------------
  // Селяни на ковзанах
  // ---------------------------------------------------------------------------------------------

  /// Селянин у виді 3/4: ковзани, кожушок із хутряною лиштвою, шарф, що має за спиною, голова з рум'янцем і шапка.
  /// (x, y) — точка на льоду між ковзанами; фігурка стоїть над нею. Без save/restore і без алокацій.
  function skater(g, x, y, d, s, look, now, id, ink) {
    if (s === 2) return lying(g, x, y, d, look, id, ink);
    if (s === 3) return swimming(g, x, y, look, now, id, ink);
    if (s === 4) return wrapped(g, x, y, look, now, id, ink);
    const hat = look[0], hc = look[1], coat = look[2], scarf = look[3];
    const fx = DX8[d] || 0, fy = DY8[d] || 0;
    const side = fx > 0.3 ? 1 : fx < -0.3 ? -1 : 0;
    const lean = s === 1 ? 2.2 : s === 5 ? -1.6 : 0;          // відштовхується — подався вперед, гальмує — відхилився
    const ph = Math.sin(now * 0.016 + id * 1.3);                // який ковзан зараз відштовхує
    // ковзани: чоботи з лезами
    const px = -fy, py = fx;                                     // поперек руху
    const boot = (bx, by, ang) => {
      g.save();
      g.translate(bx, by);
      g.rotate(ang);
      g.fillStyle = '#3a2618';
      g.beginPath(); g.ellipse(0, 0, 4, 2.3, 0, 0, TAU); g.fill();
      g.strokeStyle = '#d5dde3';
      g.lineWidth = 1;
      g.beginPath(); g.moveTo(-4.5, 2.2); g.lineTo(4.8, 2.2); g.stroke();
      g.restore();
    };
    const ang = Math.atan2(fy, fx) * (side || fy ? 1 : 0);
    if (s === 5) {
      boot(x + px * 4, y + py * 2, ang + 0.7);
      boot(x - px * 4, y - py * 2, ang - 0.7);
    } else if (s === 1) {
      const back = ph > 0 ? 1 : -1;
      boot(x + px * 3 * back - fx * 5, y + py * 2 * back - fy * 3, ang + back * 0.5);
      boot(x - px * 3 * back, y - py * 2 * back, ang);
    } else {
      boot(x + px * 3, y + py * 1.5, ang);
      boot(x - px * 3, y - py * 1.5, ang);
    }
    const bx = x + fx * lean, by = y - 2;
    // кожушок із хутряною лиштвою знизу й коміром
    g.fillStyle = CLOTH[coat] || CLOTH[0];
    g.beginPath(); g.roundRect(bx - 8, by - 15, 16, 14, 5); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.stroke();
    g.fillStyle = '#f4ecdc';
    g.beginPath(); g.roundRect(bx - 8.5, by - 3.5, 17, 3.2, 1.6); g.fill();
    // руки в рукавицях: на поштовху махають, на гальмі — розставлені
    const mit = CLOTH[scarf] || CLOTH[0];
    g.fillStyle = mit;
    g.beginPath();
    if (s === 5) { g.arc(bx - 10, by - 11, 2.4, 0, TAU); g.arc(bx + 10, by - 11, 2.4, 0, TAU); }
    else if (side) g.arc(bx - side * 1 + side * 4 * ph, by - 7, 2.4, 0, TAU);
    else { g.arc(bx - 9, by - 7 + ph, 2.4, 0, TAU); g.arc(bx + 9, by - 7 - ph, 2.4, 0, TAU); }
    g.fill();
    // голова з рум'янцем
    const hx = bx + side * 1.5 + fx * lean * 0.6, hy = by - 20;
    g.fillStyle = SKIN[id % 4] || SKIN[0];
    g.beginPath(); g.arc(hx, hy, 6.2, 0, TAU); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 0.9;
    g.stroke();
    if (fy > 0.3 && !side) {
      g.fillStyle = ink;
      g.fillRect(hx - 2.8, hy - 0.4, 1.6, 1.8);
      g.fillRect(hx + 1.2, hy - 0.4, 1.6, 1.8);
      g.fillStyle = 'rgba(230, 90, 90, .45)';
      g.beginPath(); g.arc(hx - 3.4, hy + 2.4, 1.5, 0, TAU); g.arc(hx + 3.4, hy + 2.4, 1.5, 0, TAU); g.fill();
    } else if (side) {
      g.fillStyle = ink;
      g.fillRect(hx + side * 2.4 - 0.8, hy - 0.6, 1.6, 1.8);
      g.fillStyle = 'rgba(230, 90, 90, .45)';
      g.beginPath(); g.arc(hx + side * 2.8, hy + 2.4, 1.5, 0, TAU); g.fill();
    }
    // шарф: пов'язка на шиї й кінець, що має за спиною (проти руху), — на ходу ширше
    g.fillStyle = mit;
    g.beginPath(); g.roundRect(bx - 6.5, by - 16.5, 13, 3.4, 1.6); g.fill();
    const flap = s === 1 ? 7 + 2 * Math.sin(now * 0.03 + id) : 4;
    const tx = bx - fx * flap - (side ? 0 : 3), ty = by - 14 - fy * flap + 3;
    g.strokeStyle = mit;
    g.lineWidth = 2.6;
    g.lineCap = 'round';
    g.beginPath(); g.moveTo(bx + (side ? -side * 2 : 3), by - 15); g.quadraticCurveTo((bx + tx) / 2, ty - 2, tx, ty); g.stroke();
    g.lineCap = 'butt';
    const hcol = CLOTH[hc] || CLOTH[0];
    switch (hat) {
      case 0: // хустка з горошком: накриває голову, вузлик під підборіддям
        g.fillStyle = hcol;
        g.beginPath();
        if (fy < -0.3 && !side) g.arc(hx, hy, 6.9, 0, TAU);
        else if (side) g.ellipse(hx - side * 1.6, hy - 1.4, 6.5, 6.3, 0, Math.PI * 0.95, Math.PI * 2.05);
        else g.arc(hx, hy - 0.4, 6.9, Math.PI * 1.02, Math.PI * 1.98);
        g.fill();
        g.fillStyle = 'rgba(255, 255, 255, .5)';
        g.fillRect(hx - 3, hy - 4.5, 1.3, 1.3); g.fillRect(hx + 1.5, hy - 3.4, 1.3, 1.3);
        break;
      case 1: // вушанка: хутряна, вуха звисають
        g.fillStyle = '#8a6a4a';
        g.beginPath(); g.roundRect(hx - 8, hy - 3, 3.2, 8, 1.5); g.roundRect(hx + 4.8, hy - 3, 3.2, 8, 1.5); g.fill();
        g.fillStyle = hcol;
        g.beginPath(); g.ellipse(hx, hy - 4.2, 6.8, 4.6, 0, Math.PI, TAU); g.closePath(); g.fill();
        g.fillStyle = '#b89a7a';
        g.fillRect(hx - 7, hy - 5, 14, 2.4);
        break;
      case 2: // плетена шапка з помпоном
        g.fillStyle = hcol;
        g.beginPath(); g.ellipse(hx, hy - 3.6, 6.6, 5, 0, Math.PI, TAU); g.closePath(); g.fill();
        g.fillStyle = 'rgba(255, 255, 255, .55)';
        g.fillRect(hx - 6.4, hy - 5.2, 12.8, 1.4);
        g.fillStyle = '#f7f3ea';
        g.beginPath(); g.arc(hx, hy - 9.6, 2.6, 0, TAU); g.fill();
        break;
      default: // смушева кучма: висока, темна, зі стрічкою кольору шапки
        g.fillStyle = '#2b2522';
        g.beginPath(); g.roundRect(hx - 6.2, hy - 13, 12.4, 10, 3); g.fill();
        g.fillStyle = hcol;
        g.fillRect(hx - 6.2, hy - 5, 12.4, 1.8);
    }
  }

  /// Лежить на животі, руки-ноги врозкид, шапка поруч.
  function lying(g, x, y, d, look, id, ink) {
    const fx = DX8[d] || 1, fy = DY8[d] || 0;
    const a = Math.atan2(fy, fx);
    g.save();
    g.translate(x, y - 3);
    g.rotate(a);
    g.fillStyle = '#3a2618';
    g.beginPath(); g.ellipse(-12, -4, 3.4, 2, 0.4, 0, TAU); g.ellipse(-12, 4, 3.4, 2, -0.4, 0, TAU); g.fill();
    g.fillStyle = CLOTH[look[2]] || CLOTH[0];
    g.beginPath(); g.roundRect(-9, -6, 17, 12, 5); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.stroke();
    g.fillStyle = CLOTH[look[3]] || CLOTH[0];
    g.beginPath(); g.arc(4, -9, 2.4, 0, TAU); g.arc(4, 9, 2.4, 0, TAU); g.fill();
    g.fillStyle = SKIN[id % 4] || SKIN[0];
    g.beginPath(); g.arc(12, 0, 5.8, 0, TAU); g.fill();
    g.stroke();
    g.fillStyle = CLOTH[look[1]] || CLOTH[0];
    g.beginPath(); g.ellipse(21, 5, 4.6, 3, 0.5, 0, TAU); g.fill();
    g.restore();
  }

  /// У воді: з ополонки — голова й руки вгору, довкола бризки.
  function swimming(g, x, y, look, now, id, ink) {
    const bob = Math.sin(now * 0.012 + id) * 1.2;
    g.strokeStyle = 'rgba(220, 240, 255, .8)';
    g.lineWidth = 1.5;
    g.beginPath(); g.ellipse(x, y, 13 + bob, 5, 0, 0, TAU); g.stroke();
    g.fillStyle = CLOTH[look[3]] || CLOTH[0];
    g.beginPath(); g.arc(x - 10, y - 11 + bob, 2.6, 0, TAU); g.arc(x + 10, y - 12 - bob, 2.6, 0, TAU); g.fill();
    g.fillStyle = SKIN[id % 4] || SKIN[0];
    g.beginPath(); g.arc(x, y - 5 + bob, 6, 0, TAU); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 0.9;
    g.stroke();
    g.fillStyle = ink;
    g.beginPath(); g.arc(x, y - 3 + bob, 1.6, 0, TAU); g.fill();          // «О!»
    g.fillStyle = CLOTH[look[1]] || CLOTH[0];
    g.beginPath(); g.ellipse(x, y - 10 + bob, 6.4, 3.4, 0, Math.PI, TAU); g.fill();
  }

  /// Вибув: сидить на краю ополонки, укутаний у кожух, труситься, з рота — пара.
  function wrapped(g, x, y, look, now, id, ink) {
    const j = Math.sin(now * 0.09 + id) * 0.6;
    g.fillStyle = '#8a6a4a';
    g.beginPath(); g.moveTo(x - 11 + j, y); g.quadraticCurveTo(x + j, y - 26, x + 11 + j, y); g.closePath(); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.stroke();
    g.fillStyle = SKIN[id % 4] || SKIN[0];
    g.beginPath(); g.arc(x + j, y - 20, 5.6, 0, TAU); g.fill();
    g.stroke();
    g.fillStyle = CLOTH[look[1]] || CLOTH[0];
    g.beginPath(); g.ellipse(x + j, y - 23, 6, 3.4, 0, Math.PI, TAU); g.fill();
    const p = (now / 1400 + id * 0.37) % 1;
    g.fillStyle = 'rgba(255, 255, 255, ' + (0.5 * (1 - p)).toFixed(2) + ')';
    g.beginPath(); g.arc(x + 6 + p * 8, y - 20 - p * 10, 2 + p * 3, 0, TAU); g.fill();
  }

  // ---------------------------------------------------------------------------------------------
  // Стан модуля
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    let st = root._skate;
    if (!st) {
      st = root._skate = {
        root, ctx: null, cv: null, interp: HGames.ui.Interp(), mode: 'full', S: 1,
        view: null, n: 0, looks: [], names: [], map: null, mapKey: '', kinds: [], meId: -1, me: null,
        round: -1, vphase: '', last: null, lastAt: 0, fph: '', holes: [], items: [],
        px: new Float64Array(64), py: new Float64Array(64), pd: new Int8Array(64), ps: new Int8Array(64), order: [],
        mx: new Float64Array(64), my: new Float64Array(64),
        stat: null, statKey: '', mini: null, pal: null, palAt: -1e9, marks: null, marksKey: '', fadeAt: 0,
        cam: { x: WW / 2, y: WH / 2 }, box: [0, 0, WW, WH], drag: false, down: null,
        keys: [], brakeKey: false, touchDir: -1, touchBrake: false, padDir: -1, padSeen: 0, mouse: null,
        dir: -1, localDir: -1, localUntil: 0, peekUntil: 0, sentAt: 0, autoPeek: false,
        splashes: [], picks: [], stars: new Map(), knockedBy: new Map(), snow: [], snowAt: 0,
        raf: 0, keyup: null, blur: null, ro: null, io: null, onResize: null, visible: true,
        padOn: false, padH: 0, padAt: 0, lab: [],
        hudEl: null, clockEl: null, newsEl: null, sumEl: null, padEl: null, stageEl: null, seatsEl: null,
        audio: null, mute: readMute(),
        perf: { sum: 0, n: 0, max: 0 }, wakeAt: 0,
      };
    }
    st.ctx = ctx;
    ctx._skate = st;
    return st;
  }

  const seatOf = (st, i) => (st.view && st.view.seats || []).find((s) => s.seat === i) || null;
  const nickOfSeat = (st, i) => { const s = seatOf(st, i); return (s && s.nick) || (st.ctx && (st.ctx.nameOf || st.ctx.nickOf)(i)) || SEAT_NAMES[i] || '?'; };
  const lookOf = (st, id) => { const l = st.looks; return [l[id * 4] | 0, l[id * 4 + 1] | 0, l[id * 4 + 2] | 0, l[id * 4 + 3] | 0]; };
  const nameOf = (st, id) => (st.names && st.names[id]) || 'селянин';
  const alive = (st) => !!(st.me && st.me.alive);
  const kindOf = (st, k) => (st.kinds && st.kinds[k]) || { emoji: '?', what: 'щось', name: '' };
  /// Фаза для малювання: кадри несуть її щотика, але коли партія скінчилась посеред раунду, кадрів уже нема.
  const phaseOf = (st) => (st.vphase === 'over' ? 'over' : st.fph || st.vphase);

  /// «у червоному кожушку, у синій вушанці» — щоб у новинах описати селянина так, як його видно.
  function looksLike(st, id) {
    const [hat, hc, coat] = lookOf(st, id);
    return 'у ' + CLOTH_M[coat] + ' кожушку й ' + CLOTH_F[hc] + ' ' + HAT_WORD[hat];
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
      const noise = (dur, vol, lp) => {
        const len = Math.floor(A.sampleRate * dur);
        const buf = A.createBuffer(1, len, A.sampleRate);
        const ch = buf.getChannelData(0);
        for (let i = 0; i < len; i++) ch[i] = (Math.random() * 2 - 1) * (1 - i / len);
        const src = A.createBufferSource();
        src.buffer = buf;
        const f = A.createBiquadFilter();
        f.type = 'lowpass';
        f.frequency.value = lp;
        const gn = A.createGain();
        gn.gain.value = vol;
        src.connect(f); f.connect(gn); gn.connect(out);
        src.start(t);
      };
      if (kind === 'knock') tone(140, 70, 0.08, 0.07, 'sine');
      else if (kind === 'splash') { noise(0.35, 0.09, 900); tone(300, 120, 0.2, 0.03, 'sine'); }
      else if (kind === 'pick') { tone(990, 990, 0.08, 0.025, 'sine'); tone(1480, 1480, 0.08, 0.02, 'sine', 0.06); }
      else if (kind === 'crack') { noise(0.06, 0.06, 3000); noise(0.05, 0.04, 2500); }
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
      if (key !== st.mapKey) { st.mapKey = key; st.map = v.map; st.statKey = ''; }
    }
    st.kinds = v.kinds || st.kinds;
    st.n = v.n | 0;
    st.looks = v.looks || [];
    st.names = v.names || [];
    st.me = v.me || null;
    st.meId = st.me ? st.me.id : -1;
    // новий раунд — і коли змінився номер, і коли після розкриття/кінця знову «роздивись» (рематч на 1 раунд)
    if (v.round !== st.round || (v.phase === 'start' && st.vphase && st.vphase !== 'start')) {
      st.round = v.round;
      st.camRound = -1;
      st.interp.reset();
      st.last = null;
      st.splashes.length = 0;
      st.picks.length = 0;
      st.stars.clear();
      st.knockedBy.clear();
      st.marksKey = '';          // сліди ковзанів минулого раунду — геть
    }
    if (st.autoPeek && st.me) {
      if (v.phase === 'go' && st.me.alive) { st.peekUntil = performance.now() + F5_PEEK_MS; st.autoPeek = false; }
      else if (v.phase === 'start') st.autoPeek = false;
    }
    if (v.v && v.v.length) {
      const f = { t: v.t | 0, ph: v.phase, left: v.left | 0, v: v.v, h: v.h || [], it: v.it || [], ev: [] };
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
    st.holes = f.h || st.holes;
    st.items = f.it || st.items;
    if (fromFrame) st.lastAt = performance.now();
  }

  function onFrame(st, f) {
    if (!f || !f.v) return;
    const now = performance.now();
    push(st, f, true);
    if (f.ev && f.ev.length) events(st, f, now);
    const ride = (f.ph === 'start' || f.ph === 'go') && st.dir >= 0 && st.ctx && st.ctx.mine && st.ctx.playing;
    // фаза змінилась: сервер скидає ввід на розкритті, тож затиснуте нагадуємо знову
    if (f.ph !== st.fph) {
      if (ride) send(st, st.dir, now);
      st.fph = f.ph;
    }
    if (ride && now - st.sentAt > HOLD_MS) send(st, st.dir, now);
    if (st.localUntil && st.meId >= 0 && f.v[st.meId * 4 + 2] === st.localDir) st.localUntil = 0;
    paintClock(st, f);
  }

  function events(st, f, now) {
    const esc = st.ctx.esc;
    for (const e of f.ev) {
      switch (e[0]) {
        case 1: {        // таран
          const a = e[1], b = e[2];
          st.stars.set(b, now);
          if (e[3]) st.stars.set(a, now);
          st.knockedBy.set(b, { by: a, at: now });
          const na = nameOf(st, a), nb = nameOf(st, b);
          // імена — лише в називному: «збив Меланка» різало б вухо, а відмінювати всі 64 імені заради рядка не варто
          news(st, e[3]
            ? '💥 Лоб у лоб: ' + esc(na) + ' і ' + esc(nb) + ' лежать'
            : '💥 ' + esc(nb) + ' на льоду — ' + (FEMALE.has(na) ? 'налетіла ' : 'налетів ') + esc(na));
          sfx(st, 'knock');
          break;
        }
        case 2: {        // шубовсть
          const id = e[1], seat = e[2], x = f.v[id * 4], y = f.v[id * 4 + 1];
          st.splashes.push({ x, y, at: now });
          if (st.splashes.length > 10) st.splashes.shift();
          const name = nameOf(st, id), she = FEMALE.has(name);
          if (seat >= 0) {
            const k = st.knockedBy.get(id);
            const who = k && now - k.at < CREDIT_MS ? (k.by === st.meId ? ' Штовхнув ти' : ' Останнім штовхнув хтось ' + looksLike(st, k.by)) : '';
            news(st, '🌊 <b class="skate-s' + seat + '">' + esc(nickOfSeat(st, seat)) + '</b> шубовснув' + (she ? 'а' : '') + ' — це ' + (she ? 'була ' : 'був ')
              + esc(name) + '!' + who);
            sfx(st, 'out');
          } else news(st, '🌊 Шубовсть! ' + esc(name) + ' в ополонці — зараз вилізе й обтруситься');
          sfx(st, 'splash');
          break;
        }
        case 3: {        // підхопив ласощі
          const id = e[1], k = kindOf(st, e[3]);
          st.picks.push({ x: f.v[id * 4], y: f.v[id * 4 + 1], at: now, emoji: k.emoji });
          if (st.picks.length > 10) st.picks.shift();
          news(st, k.emoji + ' Хтось підхопив ' + esc(k.what));
          sfx(st, 'pick');
          break;
        }
        case 5:
          news(st, '⚠️ Лід тріщить! Там, де тріщини, за мить буде вода');
          sfx(st, 'crack');
          break;
        case 6:
          news(st, '🕳 Лід провалився — нова ополонка');
          sfx(st, 'crack');
          break;
        case 7: {
          const name = nameOf(st, e[1]);
          st.stars.set(e[1], now);
          news(st, '🙃 ' + esc(name) + (FEMALE.has(name) ? ' впала' : ' впав') + ' на рівному місці');
          break;
        }
        default:
      }
    }
  }

  function news(st, html) {
    const el = st.newsEl;
    if (!el) return;
    const item = document.createElement('div');
    item.className = 'skate-ni';
    item.innerHTML = html;
    el.insertBefore(item, el.firstChild);
    while (el.children.length > 2) el.lastChild.remove();
    setTimeout(() => item.remove(), NEWS_MS);
  }

  // ---------------------------------------------------------------------------------------------
  // Позиції, камера
  // ---------------------------------------------------------------------------------------------

  function grow(st, n) {
    if (st.px.length >= n) return;
    const m = Math.max(n, st.px.length * 2);
    st.px = new Float64Array(m); st.py = new Float64Array(m);
    st.pd = new Int8Array(m); st.ps = new Int8Array(m);
    st.mx = new Float64Array(m); st.my = new Float64Array(m);
    st.marksKey = '';
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
      const jump = Math.abs(x1 - x0) > 24 || Math.abs(y1 - y0) > 24;     // виліз із води на край — стрибок
      px[i] = jump ? x1 : x0 + (x1 - x0) * k;
      py[i] = jump ? y1 : y0 + (y1 - y0) * k;
      pd[i] = vb[j + 2];
      ps[i] = vb[j + 3];
      if (st.vphase === 'over' && (ps[i] === 1 || ps[i] === 5)) ps[i] = 0;
    }
    // косметика свого: миттєвий поворот і поштовх, поки сервер не підтвердив
    const me = st.meId;
    if (me >= 0 && me < n && now < st.localUntil && (ps[me] <= 1 || ps[me] === 5)) {
      if (st.dir >= 0 && st.dir < 8) { pd[me] = st.dir; ps[me] = 1; }
      else if (st.dir === 8) ps[me] = 5;
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

  /// Лівий верхній кут в'юпорта в одиницях світу (у режимі всієї мапи — нуль).
  function camera(st, n) {
    const box = st.box;
    if (st.mode !== 'port') { box[0] = 0; box[1] = 0; box[2] = WW; box[3] = WH; return box; }
    const me = st.meId;
    const follow = me >= 0 && me < n && !st.drag && st.ctx && st.ctx.mine && (alive(st) || st.vphase === 'start');
    if (follow) {
      // м'яко, а не впритул: на ковзанах свій селянин трохи «пливе» в кадрі — так видно, що котишся
      st.cam.x += (st.px[me] - st.cam.x) * 0.25;
      st.cam.y += (st.py[me] - st.cam.y) * 0.25;
    } else if (!st.drag && st.vphase === 'reveal' && st.view && st.view.reveal && st.camRound !== st.round) {
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
    // тема й DPR міняються рідко: перевіряємо раз на секунду, а не склеюємо ключ щокадру
    if (st.stat && st.statKey && now - st.palAt <= 1000) return true;
    st.pal = palette();
    st.palAt = now;
    if (!st.map) return false;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const S = Math.min(2, dpr);
    const key = S + '|' + st.mapKey.length + '|' + st.pal.ice + st.pal.snow + st.pal.ice2;
    if (st.stat && st.statKey === key) return true;
    st.S = S;
    st.stat = drawStatic(st.map, st.pal, S);
    st.statKey = key;
    st.marksKey = '';
    const m = document.createElement('canvas');
    m.width = Math.round(96 * dpr);
    m.height = Math.round(64 * dpr);
    const mg = m.getContext('2d');
    mg.imageSmoothingQuality = 'high';
    mg.drawImage(st.stat, 0, 0, m.width, m.height);
    st.mini = m;
    // вогнище — біля лавки (перша клітинка B): там, де його поклала статика
    st.fireAt = null;
    for (let y = 0; y < st.map.length && !st.fireAt; y++) {
      const x = st.map[y].indexOf('B');
      if (x >= 0) st.fireAt = [x * CELL + CELL + 12, y * CELL + 18];
    }
    return true;
  }

  /// Сліди ковзанів: окремий прозорий шар, куди щокадру дописуємо відрізки від минулої позиції; раз на 0,4 с він
  /// трохи витирається (destination-out) — сліди поволі тануть. Скидається щораунду.
  function scratch(st, n, now) {
    const S = st.S;
    const key = S + '|' + st.statKey + '|' + st.round;
    if (!st.marks || st.marksKey !== key) {
      if (!st.marks) st.marks = document.createElement('canvas');
      st.marks.width = Math.round(WW * S);
      st.marks.height = Math.round(WH * S);
      st.marksKey = key;
      for (let i = 0; i < n; i++) { st.mx[i] = st.px[i]; st.my[i] = st.py[i]; }
      st.fadeAt = now;
    }
    const g = st.marks.getContext('2d');
    g.setTransform(S, 0, 0, S, 0, 0);
    if (now - st.fadeAt > 400) {
      st.fadeAt = now;
      g.globalCompositeOperation = 'destination-out';
      g.fillStyle = 'rgba(0, 0, 0, .045)';
      g.fillRect(0, 0, WW, WH);
      g.globalCompositeOperation = 'source-over';
    }
    g.lineCap = 'round';
    for (let i = 0; i < n; i++) {
      const x = st.px[i], y = st.py[i], x0 = st.mx[i], y0 = st.my[i];
      const dx = x - x0, dy = y - y0, d2 = dx * dx + dy * dy;
      const s = st.ps[i];
      if (d2 < 2.2 && s !== 5) continue;
      if (d2 < 900 && (s <= 1 || s === 5 || s === 2)) {
        const l = Math.sqrt(d2) || 1, nx = -dy / l, ny = dx / l;
        if (s === 5) {
          g.strokeStyle = 'rgba(255, 255, 255, .42)';
          g.lineWidth = 3.2;
          g.beginPath(); g.moveTo(x0 + nx * 3, y0 + ny * 3); g.lineTo(x + nx * 3, y + ny * 3);
          g.moveTo(x0 - nx * 3, y0 - ny * 3); g.lineTo(x - nx * 3, y - ny * 3); g.stroke();
        } else if (s === 2) {
          g.strokeStyle = 'rgba(255, 255, 255, .3)';
          g.lineWidth = 7;
          g.beginPath(); g.moveTo(x0, y0 - 2); g.lineTo(x, y - 2); g.stroke();
        } else {
          g.strokeStyle = 'rgba(255, 255, 255, .5)';
          g.lineWidth = 0.9;
          g.beginPath(); g.moveTo(x0 + nx * 2.6, y0 + ny * 2.6 + 1); g.lineTo(x + nx * 2.6, y + ny * 2.6 + 1);
          g.moveTo(x0 - nx * 2.6, y0 - ny * 2.6 + 1); g.lineTo(x - nx * 2.6, y - ny * 2.6 + 1); g.stroke();
        }
      }
      st.mx[i] = x;
      st.my[i] = y;
    }
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
    const phase = phaseOf(st);

    if (n && phase !== 'reveal' && phase !== 'over') scratch(st, n, now);
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.imageSmoothingEnabled = true;
    g.drawImage(st.stat, cx * S, cy * S, vw * S, vh * S, 0, 0, bw, bh);
    if (st.marks) g.drawImage(st.marks, cx * S, cy * S, vw * S, vh * S, 0, 0, bw, bh);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);

    const playing = !!(st.ctx && st.ctx.playing);
    const mine = !!(st.ctx && st.ctx.mine) && st.meId >= 0 && st.meId < n;

    fire(st, g, now);
    holes(st, g, pal, now);
    items(st, g, pal, now, phase, mine);
    trails(st, g, pal, phase);

    const peek = mine && (phase === 'start' || (phase === 'go' && now < st.peekUntil));
    if (peek) ring(g, st.px[st.meId], st.py[st.meId], pal, now);

    // тіні пачкою, потім селяни за y
    g.fillStyle = pal.shadow;
    g.beginPath();
    for (let i = 0; i < n; i++) {
      const x = st.px[i], y = st.py[i];
      if (st.ps[i] === 3 || x < cx - 24 || x > cx + vw + 24 || y < cy - 24 || y > cy + vh + 30) continue;
      g.moveTo(x + 11, y + 5);
      g.ellipse(x + 1, y + 5, 10, 4, 0, 0, TAU);
    }
    g.fill();
    const look = [0, 0, 0, 0];
    for (let o = 0; o < n; o++) {
      const i = st.order[o];
      const x = st.px[i], y = st.py[i];
      if (x < cx - 24 || x > cx + vw + 24 || y < cy - 24 || y > cy + vh + 30) continue;
      look[0] = st.looks[i * 4] | 0; look[1] = st.looks[i * 4 + 1] | 0; look[2] = st.looks[i * 4 + 2] | 0; look[3] = st.looks[i * 4 + 3] | 0;
      skater(g, x, y + 4, st.pd[i], st.ps[i], look, now, i, pal.ink);
      if (st.ps[i] === 5) spray(g, x, y + 4, st.pd[i], now, i);
    }
    stars(st, g, now, n);
    splashes(st, g, now);
    picks(st, g, now);

    g.setTransform(k, 0, 0, k, 0, 0);
    snowfall(st, g, cv.w, cv.h, now);
    shade(st, g, pal, cv.w, cv.h, cx, cy, phase, playing, mine, now);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);
    // ніки — поверх плашки розкриття: хто стоїть угорі ставка, не ховається під заголовком
    labels(st, g, pal, n, phase);
    if (peek) meArrow(g, st.px[st.meId], st.py[st.meId], pal, true);
    if (mine && st.mouse && canAct(st)) aim(st, g, pal);
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

  /// Вогнище біля лавки на березі — живе полум'я (лавка — у статиці на клітинці B).
  function fire(st, g, now) {
    const at = st.fireAt;
    if (!at) return;
    const x = at[0], y = at[1];
    for (let i = 0; i < 3; i++) {
      const f = Math.sin(now * 0.012 + i * 2.1);
      g.fillStyle = i === 0 ? 'rgba(255, 140, 40, .85)' : i === 1 ? 'rgba(255, 200, 70, .8)' : 'rgba(255, 245, 190, .75)';
      g.beginPath();
      g.moveTo(x - 7 + i * 2.5, y);
      g.quadraticCurveTo(x - 4 + i * 2, y - 10 - f * 2 - (2 - i) * 3, x + (i - 1) * 1.5, y - 16 - f * 3 - (2 - i) * 3);
      g.quadraticCurveTo(x + 4 - i * 2, y - 10 + f * 2, x + 7 - i * 2.5, y);
      g.fill();
    }
  }

  function holes(st, g, pal, now) {
    const h = st.holes;
    if (!h) return;
    for (let i = 0; i + 3 < h.length; i += 4) {
      const x = h[i], y = h[i + 1], r = h[i + 2], open = h[i + 3] === 1;
      if (x < -500) continue;
      const rnd = lcg(i * 7919 + 17);
      if (!open) {
        // тріщини: зигзаги від центру, що блимають; пунктир — де буде вода
        const a = 0.55 + 0.35 * Math.sin(now / 140);
        g.strokeStyle = 'rgba(255, 255, 255, ' + a.toFixed(2) + ')';
        g.lineWidth = 1.6;
        g.beginPath();
        for (let k = 0; k < 9; k++) {
          let ang = (k / 9) * TAU + rnd(), px = x, py = y;
          g.moveTo(px, py);
          for (let s = 0; s < 4; s++) {
            ang += (rnd() - 0.5) * 0.9;
            px += Math.cos(ang) * r * 0.3;
            py += Math.sin(ang) * r * 0.3;
            g.lineTo(px, py);
          }
        }
        g.stroke();
        g.strokeStyle = 'rgba(30, 80, 120, ' + (a * 0.6).toFixed(2) + ')';
        g.setLineDash([5, 5]);
        g.lineWidth = 1.5;
        g.beginPath(); g.arc(x, y, r, 0, TAU); g.stroke();
        g.setLineDash([]);
        continue;
      }
      // вода: темне коло з глибиною, рваний край криги, брижі
      g.fillStyle = 'rgba(255, 255, 255, .85)';
      g.beginPath();
      for (let k = 0; k <= 18; k++) {
        const ang = (k / 18) * TAU, rr = r + 3 + rnd() * 5;
        const px = x + Math.cos(ang) * rr, py = y + Math.sin(ang) * rr;
        if (k === 0) g.moveTo(px, py); else g.lineTo(px, py);
      }
      g.fill();
      const grad = g.createRadialGradient(x - r * 0.3, y - r * 0.3, r * 0.1, x, y, r);
      grad.addColorStop(0, '#2b6a8c');
      grad.addColorStop(1, pal.water);
      g.fillStyle = grad;
      g.beginPath();
      for (let k = 0; k <= 18; k++) {
        const ang = (k / 18) * TAU, rr = r - 1 + rnd() * 3;
        const px = x + Math.cos(ang) * rr, py = y + Math.sin(ang) * rr;
        if (k === 0) g.moveTo(px, py); else g.lineTo(px, py);
      }
      g.fill();
      const p = (now / 2200 + i * 0.13) % 1;
      g.strokeStyle = 'rgba(200, 235, 255, ' + (0.35 * (1 - p)).toFixed(2) + ')';
      g.lineWidth = 1.2;
      g.beginPath(); g.ellipse(x, y, r * (0.25 + 0.6 * p), r * (0.2 + 0.5 * p), 0, 0, TAU); g.stroke();
      g.fillStyle = 'rgba(255, 255, 255, .7)';
      for (let k = 0; k < 3; k++) {
        const ang = rnd() * TAU + now / 9000, rr = r * (0.3 + rnd() * 0.45);
        g.beginPath(); g.ellipse(x + Math.cos(ang) * rr, y + Math.sin(ang) * rr, 3 + rnd() * 3, 2, ang, 0, TAU); g.fill();
      }
    }
  }

  /// Ласощі на льоду: емодзі з тінню; мої ще не взяті зі списку — ледь помітний золотий пульс (бачу лише я).
  function items(st, g, pal, now, phase, mine) {
    const it = st.items;
    if (!it) return;
    const want = mine && st.me && alive(st) && (phase === 'go' || phase === 'start') ? st.me : null;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.font = '17px ' + EMOJI_FONT;
    for (let i = 0; i + 2 < it.length; i += 3) {
      const kind = it[i + 2];
      if (kind < 0) continue;
      const x = it[i], y = it[i + 1];
      const bob = Math.sin(now / 420 + i) * 0.8;
      if (want) {
        const j = want.list.indexOf(kind);
        if (j >= 0 && !want.got[j]) {
          g.strokeStyle = pal.accent;
          g.lineWidth = 2;
          g.globalAlpha = 0.45 + 0.3 * Math.sin(now / 260);
          g.setLineDash([4, 3]);
          g.beginPath(); g.arc(x, y, PICK_R, 0, TAU); g.stroke();
          g.setLineDash([]);
          g.globalAlpha = 1;
        }
      }
      // світла латочка під ласощами — щоб їх було видно на блискучому льоду й серед юрми
      g.fillStyle = 'rgba(40, 70, 95, .28)';
      g.beginPath(); g.ellipse(x + 1, y + 8, 9, 3, 0, 0, TAU); g.fill();
      g.fillStyle = 'rgba(255, 252, 240, .6)';
      g.beginPath(); g.arc(x, y + bob, 10.5, 0, TAU); g.fill();
      g.fillText(kindOf(st, kind).emoji, x, y + bob + 1);
    }
  }

  function ring(g, x, y, pal, now) {
    g.strokeStyle = pal.accent;
    g.lineWidth = 2.5;
    g.setLineDash([5, 4]);
    g.lineDashOffset = -now / 60;
    g.beginPath(); g.ellipse(x, y + 5, 16, 8, 0, 0, TAU); g.stroke();
    g.setLineDash([]);
    g.lineDashOffset = 0;
  }

  function meArrow(g, x, y, pal, big) {
    const top = y - (big ? 36 : 30);
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

  /// Мишею/пальцем по мапі: пунктир від мене до точки, куди котимось (бачу лише я).
  function aim(st, g, pal) {
    const m = st.mouse, me = st.meId;
    if (!m || m.wx == null) return;
    g.strokeStyle = pal.accent;
    g.globalAlpha = 0.55;
    g.lineWidth = 1.5;
    g.setLineDash([4, 4]);
    g.beginPath(); g.moveTo(st.px[me], st.py[me]); g.lineTo(m.wx, m.wy); g.stroke();
    g.setLineDash([]);
    g.globalAlpha = 1;
  }

  /// Гальмо «плугом» — з-під ковзанів летить крижана крихта.
  function spray(g, x, y, d, now, id) {
    if (reduced()) return;
    const fx = DX8[d] || 0, fy = DY8[d] || 0;
    g.fillStyle = 'rgba(255, 255, 255, .85)';
    for (let k = 0; k < 5; k++) {
      const p = ((now / 260 + k / 5 + id * 0.1) % 1);
      const side = k % 2 ? 1 : -1;
      g.beginPath();
      g.arc(x + fx * (6 + p * 8) - fy * side * p * 7, y + fy * (6 + p * 8) * 0.6 + fx * side * p * 4 - p * 3, 1.4 * (1 - p) + 0.4, 0, TAU);
      g.fill();
    }
  }

  function stars(st, g, now, n) {
    if (!st.stars.size) return;
    g.font = '10px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillStyle = '#ffe28a';
    for (const [id, t] of st.stars) {
      const age = now - t;
      if (age > STARS_MS || id >= n) { st.stars.delete(id); continue; }
      if (st.ps[id] !== 2) continue;
      const x = st.px[id], y = st.py[id] - 12;
      for (let s = 0; s < 3; s++) {
        const a = now / 120 + (s * TAU) / 3;
        g.fillText('✦', x + Math.cos(a) * 10, y + Math.sin(a) * 4);
      }
    }
  }

  function splashes(st, g, now) {
    const list = st.splashes;
    for (let i = list.length - 1; i >= 0; i--) if (now - list[i].at > SPLASH_MS) list.splice(i, 1);
    for (const s of list) {
      const p = (now - s.at) / SPLASH_MS;
      g.fillStyle = 'rgba(210, 240, 255, ' + (0.9 * (1 - p)).toFixed(2) + ')';
      for (let k = 0; k < 9; k++) {
        const a = (k / 9) * TAU + 0.3;
        const r = 6 + p * 22;
        g.beginPath(); g.arc(s.x + Math.cos(a) * r, s.y - 4 + Math.sin(a) * r * 0.5 - Math.sin(p * Math.PI) * 14, 2.4 * (1 - p) + 0.6, 0, TAU); g.fill();
      }
      g.strokeStyle = 'rgba(210, 240, 255, ' + (0.7 * (1 - p)).toFixed(2) + ')';
      g.lineWidth = 2;
      g.beginPath(); g.ellipse(s.x, s.y, 8 + p * 26, 4 + p * 12, 0, 0, TAU); g.stroke();
    }
  }

  function picks(st, g, now) {
    const list = st.picks;
    for (let i = list.length - 1; i >= 0; i--) if (now - list[i].at > PICK_MS) list.splice(i, 1);
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    for (const p of list) {
      const q = (now - p.at) / PICK_MS;
      g.globalAlpha = 1 - q;
      g.font = '13px ' + EMOJI_FONT;
      g.fillText(p.emoji, p.x, p.y - 20 - q * 16);
      g.fillStyle = '#fff6b0';
      for (let k = 0; k < 4; k++) {
        const a = (k / 4) * TAU + q * 3;
        g.beginPath(); g.arc(p.x + Math.cos(a) * (6 + q * 10), p.y - 8 + Math.sin(a) * (6 + q * 10) * 0.6, 1.4, 0, TAU); g.fill();
      }
      g.globalAlpha = 1;
    }
  }

  /// Сліди гравців за останні ≈ 20 с раунду — лише на розкритті: «я ж котився просто за тобою!».
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
      g.strokeStyle = 'rgba(15, 30, 45, .5)';
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

  /// Ніки над гравцями: над тими, хто шубовснув, — завжди, над усіма — на розкритті. ⭐ — переможцю раунду, 🏆 —
  /// переможцю партії.
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
      g.beginPath(); g.ellipse(st.px[id], st.py[id] + 5, 15, 7.5, 0, 0, TAU); g.stroke();
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
      a.y = st.py[id] - (st.ps[id] >= 2 && st.ps[id] <= 3 ? 20 : 38);
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
      g.fillStyle = 'rgba(15, 25, 35, .82)';
      g.beginPath(); g.roundRect(a.x - a.w / 2, a.y - h / 2, a.w, h, h / 2); g.fill();
      g.fillStyle = pal.seats[a.seat] || pal.text;
      g.fillText(a.text, a.x, a.y + 0.5);
    }
  }

  /// Сніжок: легкі пластівці в екранних координатах (reduced-motion — без них).
  function snowfall(st, g, w, h, now) {
    if (reduced()) return;
    const flakes = st.snow;
    if (!flakes.length) {
      const rnd = lcg(42);
      for (let i = 0; i < 70; i++) flakes.push({ x: rnd() * w, y: rnd() * h, r: 0.8 + rnd() * 1.8, v: 0.25 + rnd() * 0.5, ph: rnd() * TAU });
      st.snowAt = now;
    }
    const dt = Math.min(64, now - (st.snowAt || now)) / 16.7;
    st.snowAt = now;
    g.fillStyle = 'rgba(255, 255, 255, .85)';
    g.beginPath();
    for (const f of flakes) {
      f.y += f.v * dt;
      f.x += Math.sin(now / 1300 + f.ph) * 0.25 * dt;
      if (f.y > h + 4) { f.y = -4; f.x = (f.x * 7.31 + 13) % w; }
      if (f.x < -4) f.x += w + 8; else if (f.x > w + 4) f.x -= w + 8;
      g.moveTo(f.x + f.r, f.y);
      g.arc(f.x, f.y, f.r, 0, TAU);
    }
    g.fill();
  }

  function shade(st, g, pal, w, h, cx, cy, phase, playing, mine, now) {
    const v = st.view;
    if (!v) return;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const room = st.ctx && st.ctx.room;
    if (!playing && room && room.status === 'lobby') {
      g.fillStyle = 'rgba(15, 25, 35, .42)';
      g.fillRect(0, 0, w, h);
      if (st.mode === 'port') {
        fitFont(g, 'щойно господар натисне «Почати»', w * 0.9, 22, 700);
        outlined(g, 'Ковзанка відкриється,', w / 2, h / 2 - 14, pal.text, pal.ink);
        outlined(g, 'щойно господар натисне «Почати»', w / 2, h / 2 + 14, pal.text, pal.ink);
      } else {
        const msg = 'Ковзанка відкриється, щойно господар натисне «Почати»';
        fitFont(g, msg, w * 0.9, Math.round(w / 34), 700);
        outlined(g, msg, w / 2, h / 2, pal.text, pal.ink);
      }
      return;
    }
    const minPx = 14 / (st.cssK || 1);
    if (phase === 'start') {
      if (mine) spotlight(st, g, w, h, cx, cy, 0.42);
      const left = st.last ? st.last.left : v.left;
      g.font = '800 ' + Math.round(h / 5) + 'px system-ui, sans-serif';
      outlined(g, String(Math.max(1, Math.ceil((left * TICK_MS) / 1000))), w / 2, h / 2, pal.text, pal.ink);
      const sub = mine ? 'Роздивись: ти — під стрілкою' : 'Лід відкривається…';
      const px = Math.round(Math.max(h / 26, minPx));
      fitFont(g, sub, w * 0.92, px, 700);
      outlined(g, sub, w / 2, h / 2 + h / 7, pal.text, pal.ink);
      if (mine && st.me) {
        const list = (st.me.list || []).map((k2) => kindOf(st, k2).emoji).join('');
        const how = st.mode === 'port' ? 'Збери ' + list + ' · не видай себе' : 'Збери ' + list + ' (тихо, на малому ходу) · тарань підозрілих до ополонки';
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
      g.fillStyle = 'rgba(15, 25, 35, .26)';
      g.fillRect(0, 0, w, h);
      const title = phase === 'reveal' ? revealTitle(st, st.mode === 'port') : overTitle(st);
      const port = st.mode === 'port', left = port ? w - 108 : w, mid = left / 2;
      fitFont(g, title, left - (port ? 24 : 40), Math.round(Math.max(minPx, h / 22)), 800);
      const tw = g.measureText(title).width + 28;
      const th = Math.max(24, h / 13), ty = port ? 8 : Math.round(h * 0.07);
      g.fillStyle = 'rgba(15, 25, 35, .78)';
      g.beginPath(); g.roundRect(mid - tw / 2, ty, tw, th, 10); g.fill();
      g.fillStyle = pal.text;
      g.fillText(title, mid, ty + th / 2);
    }
  }

  function spotlight(st, g, w, h, cx, cy, alpha) {
    const x = st.px[st.meId] - cx, y = st.py[st.meId] - cy - 8;
    g.fillStyle = 'rgba(15, 25, 35, ' + alpha.toFixed(3) + ')';
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
      case 'list': return head + '🧺 Кошик повний — ' + who;
      case 'last': return head + '⛸ На льоду лише ' + who;
      case 'time': return head + '⏱ Смеркне — найповніший кошик у ' + who;
      default: return head + '⏱ Смеркне — ніхто не взяв раунду';
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
    // партія з «🤖 + бот»: бот у переможцях — лише в заголовку, нагород йому (і за нього) нема
    if (v.bot != null && res.winners.includes(v.bot)) {
      const human = (v.seats || []).find((s) => s.seat !== v.bot);
      return '🤖 Бот переміг ' + (res.totals[v.bot] | 0) + ':' + (human ? res.totals[human.seat] | 0 : 0);
    }
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
    const h = st.holes || [];
    for (let i = 0; i + 3 < h.length; i += 4) {
      if (h[i] < -500) continue;
      g.fillStyle = h[i + 3] === 1 ? pal.water : 'rgba(255, 255, 255, .9)';
      g.beginPath(); g.arc(x0 + h[i] * sx, y0 + h[i + 1] * sy, Math.max(1.5, h[i + 2] * sx), 0, TAU); g.fill();
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
    const text = ph === 'go' ? '⏱ ' + clock(left) : ph === 'start' ? '⏱ ' + clock(2250) : '⏱ —';
    if (el.textContent !== text) el.textContent = text;
    const hot = ph === 'go' && left * TICK_MS <= 10000;
    if (el.classList.contains('hot') !== hot) el.classList.toggle('hot', hot);
  }

  function hud(st) {
    const ctx = st.ctx, v = st.view, el = st.hudEl;
    if (!el || !v) return;
    const me = v.me;
    let html = '<span class="skate-chip">Раунд ' + Math.max(1, v.round | 0) + '/' + (v.of | 0) + '</span>';
    if (me) {
      html += '<span class="skate-chip skate-list" title="Твій кошик: збери ці ласощі"><small>Кошик:</small>';
      me.list.forEach((k, i) => {
        const s = kindOf(st, k);
        html += '<i' + (me.got[i] ? ' class="done"' : '') + ' title="' + ctx.esc(s.name || '') + '">' + (s.emoji || '?') + (me.got[i] ? '✓' : '') + '</i>';
      });
      html += '</span>';
    }
    const seats = v.seats || [];
    const active = seats.filter((s) => !s.out);
    if (v.phase !== 'lobby') html += '<span class="skate-chip">на льоду ' + active.filter((s) => s.alive).length + '/' + active.length + '</span>';
    html += '<button type="button" class="skate-mute" data-pad-skip title="Звук">' + (st.mute ? '🔇' : '🔊') + '</button>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.querySelector('.skate-chips').innerHTML = html;
    }
    let row = '';
    for (const s of seats) {
      row += '<span class="skate-seat skate-s' + s.seat + (s.alive ? '' : ' dead') + (s.out ? ' out' : '') + (s.seat === ctx.seat ? ' me' : '') + '">'
        + '<i></i>' + ctx.esc(s.nick) + ' <b>' + (s.total | 0) + '</b>' + (s.got != null ? ' <small>🧺' + (s.got | 0) + '</small>' : '') + '</span>';
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

  /// Підсумок — фішки за очками партії: разом, а дрібно — що дав раунд (ласощі, зіпхнуті).
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
      html = rows.map((x) => '<span class="skate-sc skate-s' + x.seat + (champ.includes(x.seat) ? ' win' : '') + '">'
        + (champ.includes(x.seat) ? (over ? '🏆 ' : '⭐ ') : '') + '<b>' + st.ctx.esc(nickOfSeat(st, x.seat)) + '</b> '
        + '<em>' + total(x.seat) + '</em> <small title="за раунд: ласощі зі списку, зіпхнуті в ополонку">+' + x.pts
        + ' · 🧺' + x.got + (x.kills ? ' · 🌊' + x.kills : '') + '</small></span>').join('');
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      el.hidden = !html;
    }
    if (st.stageEl) st.stageEl.classList.toggle('skate-open', open);
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

  /// Надіслати, що тримаю: −1 нічого, 0…7 напрямок, 8 гальмо.
  function send(st, d, now) {
    st.ctx.input('move', { dir: d });
    st.sentAt = now;
  }

  /// З клавіш: дві затиснуті стрілки — діагональ; протилежні — перемагає пізніша; гальмо важить більше за напрямок.
  function keyDir(st) {
    if (st.brakeKey) return 8;
    let dx = 0, dy = 0;
    for (const k of st.keys) {
      if (k === 'r') dx = 1; else if (k === 'l') dx = -1;
      else if (k === 'd') dy = 1; else if (k === 'u') dy = -1;
    }
    return dx || dy ? dir8(dx, dy) : -1;
  }

  /// Мишею чи пальцем по мапі: котимось до точки; біля самого себе — гальмо.
  function mouseDir(st) {
    const m = st.mouse, me = st.meId;
    if (!m || m.wx == null || me < 0) return -1;
    const dx = m.wx - st.px[me], dy = m.wy - st.py[me];
    if (dx * dx + dy * dy < 14 * 14) return 8;
    return dir8(dx, dy);
  }

  /// Куди я зараз тисну: палець на джойстику → пад → миша на мапі → клавіатура.
  function want(st) {
    const d = st.touchBrake ? 8 : st.touchDir >= 0 ? st.touchDir : st.padDir >= 0 ? st.padDir : st.mouse ? mouseDir(st) : keyDir(st);
    if (d === st.dir) return;
    st.dir = d;
    if (d >= 0) { st.localDir = d; st.localUntil = performance.now() + LOCAL_MS; }
    const ctx = st.ctx;
    if (ctx && ctx.mine && ctx.playing) send(st, d, performance.now());
  }

  function peek(st) { st.peekUntil = performance.now() + PEEK_MS; wake(st); }

  function canAct(st) {
    const ctx = st.ctx, ph = phaseOf(st);
    return !!(ctx && ctx.mine && ctx.playing && alive(st) && (ph === 'go' || ph === 'start'));
  }

  /// Пад: стік і хрестовина — усі вісім напрямків (шар пада сайту дає грі лише чотири стрілки, тож читаємо пад
  /// самі; його стрілки тоді ігноруємо). Ⓐ — гальмо (пробіл від шару пада), LB/RB — «де я?».
  function pollPad(st) {
    const pad = window.HPad;
    if (!pad || !pad.on || !canAct(st) || !navigator.getGamepads) { if (st.padDir !== -1) { st.padDir = -1; want(st); } return; }
    let list;
    try { list = pad.list ? pad.list() : (navigator.getGamepads() || []); } catch { list = []; }   // без керма й лише у фокусі
    let dx = 0, dy = 0, ax = 0, ay = 0, seen = false;
    for (const p of list) {
      if (!p || !p.connected) continue;
      seen = true;
      const b = p.buttons || [];
      if (b[12] && b[12].pressed) dy = -1;
      if (b[13] && b[13].pressed) dy = 1;
      if (b[14] && b[14].pressed) dx = -1;
      if (b[15] && b[15].pressed) dx = 1;
      const a = p.axes || [];
      if (Math.hypot(a[0] || 0, a[1] || 0) > Math.hypot(ax, ay)) { ax = a[0] || 0; ay = a[1] || 0; }
    }
    if (seen) st.padSeen = performance.now();
    let d = -1;
    if (dx || dy) d = dir8(dx, dy);
    else if (Math.hypot(ax, ay) > (st.padDir >= 0 ? 0.35 : 0.5)) d = ((Math.round(Math.atan2(ay, ax) / (Math.PI / 4)) % 8) + 8) % 8;
    if (d !== st.padDir) { st.padDir = d; want(st); }
  }

  /// Точка на канвасі → світ.
  function toWorld(st, e) {
    const r = st.cv.el.getBoundingClientRect();
    const [cx, cy, vw, vh] = st.box;
    return [cx + ((e.clientX - r.left) / r.width) * vw, cy + ((e.clientY - r.top) / r.height) * vh, r];
  }

  function wireCanvas(st) {
    const el = st.cv.el;
    el.addEventListener('pointerdown', (e) => {
      wake(st);
      unlock(st);
      if (canAct(st) && (e.pointerType !== 'mouse' || e.button === 0)) {
        // гравець: тримаєш — котишся туди, де вказівник
        e.preventDefault();
        try { el.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
        const [wx, wy] = toWorld(st, e);
        st.mouse = { id: e.pointerId, x: e.clientX, y: e.clientY, wx, wy };
        want(st);
        return;
      }
      st.down = { id: e.pointerId, x: e.clientX, y: e.clientY, moved: false, cam: { x: st.cam.x, y: st.cam.y } };
      if (st.mode === 'port') {
        // мінімапа: тиць — туди й камера (глядачеві й вибулому)
        const [, , r] = toWorld(st, e);
        const lx = ((e.clientX - r.left) / r.width) * PW, ly = ((e.clientY - r.top) / r.height) * PH;
        if (lx >= PW - 102 && ly <= 72) {
          st.cam.x = ((lx - (PW - 102)) / 96) * WW;
          st.cam.y = ((ly - 6) / 64) * WH;
          st.down.moved = true;
        }
      }
    });
    el.addEventListener('pointermove', (e) => {
      wake(st);
      const m = st.mouse;
      if (m && m.id === e.pointerId) {
        m.x = e.clientX;
        m.y = e.clientY;
        return;
      }
      const d = st.down;
      if (d && d.id === e.pointerId) {
        const dx = e.clientX - d.x, dy = e.clientY - d.y;
        if (Math.abs(dx) + Math.abs(dy) > 6) d.moved = true;
        if (st.mode === 'port' && d.moved) {
          const r = el.getBoundingClientRect();
          st.drag = true;
          st.cam.x = clamp(d.cam.x - (dx / r.width) * PW, PW / 2, WW - PW / 2);
          st.cam.y = clamp(d.cam.y - (dy / r.height) * PH, PH / 2, WH - PH / 2);
        }
      }
    });
    const up = (e) => {
      if (st.mouse && st.mouse.id === e.pointerId) { st.mouse = null; want(st); return; }
      const d = st.down;
      if (!d || d.id !== e.pointerId) return;
      st.down = null;
      st.drag = false;
    };
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
    el.addEventListener('lostpointercapture', up);
    el.addEventListener('contextmenu', (e) => { if (canAct(st)) e.preventDefault(); });
  }

  /// Вказівник стоїть, а я котюсь — світова точка під ним міняється; перераховуємо щокадру.
  function trackMouse(st) {
    const m = st.mouse;
    if (!m || !st.cv) return;
    const [wx, wy] = toWorld(st, { clientX: m.x, clientY: m.y });
    m.wx = wx;
    m.wy = wy;
    if (!canAct(st)) { st.mouse = null; }
    want(st);
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
    el.className = 'skate-pad';
    // Джойстик — одне коло: напрямок за кутом пальця від центру (вісім секторів), палець «їздить», не відриваючись.
    el.innerHTML = '<div class="skate-stick" role="group" aria-label="джойстик: тримай і веди пальцем"><span class="skate-knob"></span></div>'
      + '<div class="skate-acts">'
      + '<button type="button" data-act="brake" aria-label="гальмо">🛑</button>'
      + '<button type="button" data-act="peek" aria-label="де я">👁</button></div>';
    const stick = el.querySelector('.skate-stick');
    const knob = el.querySelector('.skate-knob');
    const aimAt = (e) => {
      const r = stick.getBoundingClientRect();
      const dx = e.clientX - (r.left + r.width / 2), dy = e.clientY - (r.top + r.height / 2);
      const lim = r.width * 0.36, l = Math.hypot(dx, dy) || 1, k = Math.min(1, lim / l);
      knob.style.transform = 'translate(' + (dx * k).toFixed(1) + 'px, ' + (dy * k).toFixed(1) + 'px)';
      if (l < r.width * 0.12) return -1;
      return dir8(dx, dy);
    };
    el.addEventListener('pointerdown', (e) => {
      const b = e.target.closest('button');
      unlock(st);
      if (b) {
        e.preventDefault();
        if (b.dataset.act === 'peek') return peek(st);
        if (b.dataset.act === 'brake') {
          try { b.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
          st.touchBrake = true;
          b.classList.add('on');
          want(st);
        }
        return;
      }
      if (!e.target.closest('.skate-stick')) return;
      e.preventDefault();
      try { stick.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
      st.touchPid = e.pointerId;
      st.touchDir = aimAt(e);
      want(st);
    });
    const brakeUp = (e) => {
      const b = e.target.closest && e.target.closest('button[data-act="brake"]');
      if (!b || !st.touchBrake) return;
      st.touchBrake = false;
      b.classList.remove('on');
      want(st);
    };
    el.addEventListener('pointerup', brakeUp);
    el.addEventListener('pointercancel', brakeUp);
    el.addEventListener('lostpointercapture', brakeUp);
    stick.addEventListener('pointermove', (e) => {
      if (st.touchPid !== e.pointerId) return;
      const d = aimAt(e);
      if (d === st.touchDir) return;
      st.touchDir = d;
      want(st);
    });
    const release = (e) => {
      if (st.touchPid !== e.pointerId) return;
      st.touchPid = null;
      st.touchDir = -1;
      knob.style.transform = '';
      want(st);
    };
    stick.addEventListener('pointerup', release);
    stick.addEventListener('pointercancel', release);
    stick.addEventListener('lostpointercapture', release);
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

  /// Телефон лежачи: мапа посередині за висотою екрана, хрестовина ліворуч, кнопки праворуч (клас skate-land).
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
    return { w: vv ? vv.width : window.innerWidth, h: vv ? vv.height : window.innerHeight, top, dock: n('--gdock-h') || 64 };   // --tabs-h — calc(58px + safe-area), parseFloat дає NaN
  }

  /// Мапа 3:2 має влізти у вікно разом зі статусом і кнопками під нею (як у Юрми).
  function sizeStage(st, mode) {
    const el = st.stageEl;
    if (!el || !el.isConnected) return;
    if (st.land) {                               // лежачи: уся висота смуги — мапі, без нижньої межі 480
      const f = fitNow();
      // g-land: картка столу рівно на екран, над мапою — рядок «статус · Встати» (css), тож мінус її відступи, і той рядок
      const lw = clamp(Math.floor((f.h - f.top - f.dock - (f.land ? 104 : 12)) * 1.5), 240, 960);
      if (Math.abs((parseFloat(el.style.maxWidth) || 0) - lw) >= 3) el.style.maxWidth = lw + 'px';
      return;
    }
    if (mode === 'port') { if (el.style.maxWidth) el.style.maxWidth = ''; return; }
    const r = el.getBoundingClientRect();
    const card = el.closest('.gtable');
    const below = Math.max(48, card ? card.getBoundingClientRect().bottom - r.bottom : 92) + 22;
    const h = window.innerHeight - (r.top + (window.scrollY || 0)) - below - st.padH;
    const w = clamp(Math.floor(h * 1.5), 480, 960);
    const cur = parseFloat(el.style.maxWidth) || 0;
    if (Math.abs(cur - w) >= 3) el.style.maxWidth = w + 'px';
  }

  /// Режим камери — за шириною картки: < 600 px — в'юпорт за своїм, > 640 — уся мапа (між ними — як було).
  function fit(root, st) {
    wake(st);
    padStrip(st);
    const cw = root.clientWidth;
    const land = phoneLand(), turned = land !== !!st.land;
    if (turned) { st.land = land; root.classList.toggle('skate-land', land); st.fitFor = ''; }
    let mode = st.mode;
    if (land) mode = 'full';
    else if (cw) mode = cw < 600 ? 'port' : cw > 640 ? 'full' : st.mode;
    sizeStage(st, mode);
    if (!st.cv || mode !== st.mode) {
      st.mode = mode;
      st.cv = HGames.ui.canvas(st.stageEl, mode === 'port' ? { w: PW, h: PH, cls: 'skate-board skate-port' } : { w: WW, h: WH, cls: 'skate-board' });
      st.stageEl.classList.toggle('skate-portmode', mode === 'port');
      st.snow.length = 0;
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
    // лежачи хрестовина й кнопки стоять обабіч мапи, тож у кадр треба всю сітку (мапа, рядок гравців, кнопки)
    const box = st.stageEl.parentElement || st.stageEl;
    const a = (st.land ? box : st.hudEl).getBoundingClientRect(), b = (st.land ? box : padEl).getBoundingClientRect();
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
      if (!st.visible || (!live && now - st.wakeAt > IDLE_MS)) { st.raf = 0; return; }
      pollPad(st);
      if (st.mouse) trackMouse(st);
      if (!document.hidden) draw(st);
      st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  HGames.register({
    id: 'skate',
    icon: ICON,
    seatNames: SEAT_NAMES,
    seatClass: ['x', 'o', 'c', 'd', 'skate-b', 'skate-p', 'skate-v', 'skate-r'],
    pad: {
      dirs: true,
      a: 'Space',
      on(btn, ctx) {
        if (btn === 'lb' || btn === 'rb') { if (ctx && ctx._skate) peek(ctx._skate); return true; }
        return false;
      },
      hint: '{dpad} котитись (вісім боків) · {a} гальмо · {lb} де я?',
    },
    added: '2026-09-27',
    news: {
      v: '2026-09-30',
      title: 'Ковзанка: можна й самому — з 🤖 ботом',
      items: [
        '🤖 Сам за столом? Тисни «🤖 + бот» — на лід вийде гравець-бот і загубиться серед ковзанярів',
        '🥯 Він збирає свій кошик і таранить підозрілих біля ополонки — вистеж і зіпхни його сам: шубовснув — вибув на раунд',
        '🎚️ Рівень бота — в опціях столу: легкий, звичайний чи сильний. Партія з ботом — без нагород',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      root.classList.add('skate-body');
      st.hudEl = document.createElement('div');
      st.hudEl.className = 'skate-hud';
      st.hudEl.innerHTML = '<span class="skate-chip skate-clock">⏱ —</span><span class="skate-chips"></span>';
      st.clockEl = st.hudEl.querySelector('.skate-clock');
      st.seatsEl = document.createElement('div');
      st.seatsEl.className = 'skate-seats';
      st.hudEl.appendChild(st.seatsEl);
      st.hudEl.addEventListener('click', (e) => {
        if (!e.target.closest('.skate-mute')) return;
        st.mute = !st.mute;
        try { localStorage.setItem('skateMute', st.mute ? '1' : ''); } catch { /* приватне вікно */ }
        if (!st.mute) unlock(st);
        hud(st);
      });
      st.newsEl = document.createElement('div');
      st.newsEl.className = 'skate-news';
      st.newsEl.setAttribute('aria-live', 'polite');
      st.sumEl = document.createElement('div');
      st.sumEl.className = 'skate-sum';
      st.sumEl.hidden = true;
      st.stageEl = document.createElement('div');
      st.stageEl.className = 'skate-stage';
      st.stageEl.appendChild(st.newsEl);
      root.append(st.hudEl, st.stageEl, st.sumEl);
      st.cv = HGames.ui.canvas(st.stageEl, { w: WW, h: WH, cls: 'skate-board' });
      wireCanvas(st);
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => { if (!st.roQ) st.roQ = requestAnimationFrame(() => { st.roQ = 0; if (root.isConnected) fit(root, st); }); });   // через rAF: синхронна зміна розміру в колбеку RO давала «ResizeObserver loop completed…»
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
        if (e.hpad && st.padDir >= 0 && arrowOf(e)) return;
        const a = arrowOf(e);
        if (a) {
          const i = st.keys.indexOf(a);
          if (i >= 0) { st.keys.splice(i, 1); want(st); }
          return;
        }
        if (isBrake(e) && st.brakeKey) { st.brakeKey = false; want(st); }
      };
      st.blur = () => {
        st.keys.length = 0;
        st.brakeKey = false;
        st.touchDir = -1;
        st.touchBrake = false;
        st.mouse = null;
        want(st);
      };
      document.addEventListener('keyup', st.keyup);
      window.addEventListener('blur', st.blur);
      // F5 посеред партії: напрямок, затиснутий до перезавантаження, не має везти в сніг
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
        if (st.padEl.classList.contains('skate-off') !== off) st.padEl.classList.toggle('skate-off', off);
        if (off && (st.touchDir >= 0 || st.touchBrake)) { st.touchDir = -1; st.touchPid = null; st.touchBrake = false; want(st); }
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
      const st = ctx._skate;
      if (!st || !ctx.mine || !ctx.playing) return false;
      unlock(st);
      const a = arrowOf(e);
      if (a) {
        // стрілки від шару пада — лише чотири боки; коли стік читаємо самі (вісім), їх ковтаємо
        if (e.hpad && st.padDir >= 0) return true;
        const i = st.keys.indexOf(a);
        if (i >= 0) st.keys.splice(i, 1);
        st.keys.push(a);
        want(st);
        return true;
      }
      if (isBrake(e)) { if (!st.brakeKey) { st.brakeKey = true; want(st); } return true; }
      if (isPeek(e)) { peek(st); return true; }
      return false;
    },

    status(ctx) {
      const st = ctx._skate;
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f) return '';
      const ph = f.ph || f.phase;
      if (ph === 'start') return ctx.mine ? 'Роздивись: стрілка показує тебе' : 'Лід відкривається…';
      if (ph === 'reveal') {
        const v = st && st.view, s = Math.max(1, Math.ceil(((f.left | 0) * TICK_MS) / 1000));
        return v && v.round >= v.of ? 'Підсумок партії за ' + s + ' с' : 'Наступний раунд за ' + s + ' с';
      }
      if (ph !== 'go') return '';
      if (!ctx.mine) return HGames.ui.coarse() ? 'Вгадуй разом із гравцями, хто з юрми живий · тягни мапу пальцем' : 'Вгадуй разом із гравцями, хто з юрми живий';
      if (st && st.me && !st.me.alive) return 'Ти шубовснув — грійся й дивись, хто кого';
      if (window.HPad && window.HPad.on) return 'Стік — котитись (вісім боків) · Ⓐ гальмо · LB — де я? Ласощі бери на малому ходу';
      return HGames.ui.coarse()
        ? 'Джойстик — котитись · 🛑 гальмо · 👁 де я? · або тримай палець на мапі'
        : 'Стрілки/WASD (по дві — навскоси) · пробіл — гальмо · Q — де я? · або тримай мишу на мапі';
    },

    unmount(root) {
      const st = root._skate;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (st.ro) st.ro.disconnect();
      if (st.io) st.io.disconnect();
      if (st.audio) { try { st.audio.close(); } catch { /* уже закритий */ } }
      root._skate = null;
    },
  });
})();
