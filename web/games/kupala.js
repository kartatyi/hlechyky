/*
  Купальська ніч (kupala) — Unspottable у темряві. Реалтайм 25 Гц: сервер тикає раз на 40 мс і шле кадр, ми його
  згладжуємо й малюємо. У кадрі — лише ті селяни, що стоять у світлі; решти для нас нема зовсім, і свого теж: у
  темряві ти не знаєш, де ти, — пам'ятаєш. Ми лише пам'ятаємо, де тебе бачили востаннє (це знає й сам гравець).

  Кадр (Impl/Kupala.cs): { t, ph: 'start'|'go'|'reveal'|'over', left, sky, v: [id, x, y, d, s] × у світлі (за id),
    l: [вид, x, y, r] × світло (0 вогнище, 1 світлячок, 2 головешка, 3 вінок, 4 папороть), ev: події тика }
    ev: [1, хто|-1, кого|-1, 0 бот|1 гравець|2 повз, місце, x, y] · [2, кладка] · [3, хто|-1, x0, y0, x1, y1, мокро]
        · [4, хто|-1] папороть зірвали · [5] папороть зацвіла.
  Вид (Hidden, свій кожному): { phase, round, of, left, t, n, map, spots, fires, looks, names, v, l, sky, seats, dead,
    me, reveal, result } — me лише своєму місцю: { id, list, done, torches, cool, launchCool, stun, busy, fern, alive },
    без жодних координат.

  Ввід: Input('move', { dir }) лише на зміну; Act('slap', {} | { id }); Act('launch', {} | { spot }); Act('torch', {}).
*/
(() => {
  const TICK_MS = 40, CELL = 32, WW = 960, WH = 640, PW = 480, PH = 360;
  const DX = [1, 0, -1, 0], DY = [0, 1, 0, -1];
  const SLAP_REACH = 40, SLAP_MAX = 56, CONE = 250;
  const SLAP_COOL_MS = 1000, LAUNCH_COOL_MS = 2000, LAUNCH_MS = 1000, STUN_MS = 2000, TORCH_MS = 6000;
  const ROUND_TICKS = 2250;
  const FADE_MS = 160, PEEK_MS = 1500, NEWS_MS = 6000, LOCAL_MS = 250, HOLD_MS = 1000, TIP_MS = 1800, F5_PEEK_MS = 3000;
  const FX_MS = 700, FLY_MS = 320, SPLASH_MS = 900, NIGHT_MS = 1600, DAWN_MS = 1200;
  // темрява: майже чорна з синявою; у сутінках «роздивись» — ледь-ледь
  const NIGHT_A = 0.9, DUSK_A = 0.34, LOBBY_A = 0.3;
  const TAU = Math.PI * 2;
  const WREATH_Y = 492;

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7" fill="var(--bg2)"/>'
    + '<path d="M8 13c-2.6 0-4-1.6-4-3.6 0-2 1.6-2.9 2.3-4.9.9 1.2 1.1 2.1 1.1 2.9.7-.8 1.2-2.2 1.1-3.9 2 1.4 3.5 3.5 3.5 5.9 0 2-1.4 3.6-4 3.6z" fill="var(--clay)"/>'
    + '<path d="M8 13c-1.2 0-1.9-.8-1.9-1.8 0-1 .8-1.6 1.1-2.4.8.6 1.1 1.2 1.1 1.8.4-.3.6-.8.6-1.3.8.7 1.1 1.3 1.1 2 0 1-.8 1.7-2 1.7z" fill="var(--accent)"/>'
    + '<circle cx="3" cy="3.5" r=".9" fill="var(--ok)"/><circle cx="13" cy="4.5" r=".7" fill="var(--ok)"/></svg>';

  const SEAT_NAMES = ['жовтий', 'зелений', 'рудий', 'сірий', 'синій', 'рожевий', 'фіолетовий', 'червоний'];
  const SEAT_VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'],
    ['--kupala-blue', '#6fb3e8'], ['--kupala-pink', '#e88ac0'], ['--kupala-violet', '#b48ae8'], ['--kupala-red', '#e86a6a']];
  // одяг: вишиванки й сорочки — червоний, синій, зелений, жовтий, білий, фіолетовий, помаранчевий, чорний
  const CLOTH = ['#c0392b', '#2f6db5', '#3f9142', '#e8c33a', '#f2efe6', '#7d4fa8', '#e07b2a', '#2a2a2a'];
  const HAIR = ['#2b1d14', '#4a2f1d', '#7a4b27', '#c9a15a', '#8c4a2f', '#3b2a20', '#6b4a2e', '#1a1410'];
  const SKIN = ['#f3d0b0', '#e2b48c', '#c48a5e', '#8d5a3b'];
  const CLOTH_F = ['червоній', 'синій', 'зеленій', 'жовтій', 'білій', 'фіолетовій', 'помаранчевій', 'чорній'];
  const CLOTH_M = ['червоному', 'синьому', 'зеленому', 'жовтому', 'білому', 'фіолетовому', 'помаранчевому', 'чорному'];
  const FEMALE = new Set(['Параска', 'Ганна', 'Одарка', 'Марічка', 'Оксана', 'Домаха', 'Соломія', 'Мотря', 'Ярина', 'Христя',
    'Марта', 'Устя', 'Килина', 'Наталка', 'Пріська', 'Гафія', 'Софійка', 'Орися', 'Настя', 'Явдоха', 'Меланка', 'Феся', 'Текля',
    'Зоряна', 'Уляна', 'Люба', 'Дарина', 'Олеся', 'Марійка', 'Ганнуся', 'Варка', 'Катря']);

  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  const KEY_DIRS = { d: 0, 'в': 0, s: 1, 'і': 1, 'ы': 1, a: 2, 'ф': 2, w: 3, 'ц': 3 };
  const low = (e) => String(e.key || '').toLowerCase();
  const dirOf = (e) => {
    const d = DIRS[e.code];
    return d !== undefined ? d : KEY_DIRS[low(e)];
  };
  const isSlap = (e) => e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar';
  const isLaunch = (e) => e.code === 'KeyE' || e.code === 'KeyX' || e.code === 'Enter' || e.code === 'NumpadEnter'
    || (!e.code && ['e', 'x', 'у', 'ч', 'enter'].includes(low(e)));
  const isTorch = (e) => e.code === 'KeyR' || e.code === 'KeyF' || (!e.code && ['r', 'f', 'к', 'а'].includes(low(e)));
  const isPeek = (e) => e.code === 'KeyQ' || e.code === 'Tab' || (!e.code && ['q', 'й'].includes(low(e)));

  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const readMute = () => { try { return localStorage.getItem('kupalaMute') === '1'; } catch { return false; } };

  // ---------------------------------------------------------------------------------------------
  // Палітра, штампи світла й статика (луг, ліс, берег, річка) — один раз в offscreen-канвас
  // ---------------------------------------------------------------------------------------------

  function palette() {
    const c = HGames.ui.css;
    return {
      grass: c('--kupala-grass', '#4f7d3c'),
      grass2: c('--kupala-grass2', '#436d33'),
      forest: c('--kupala-forest', '#2f4f2a'),
      bank: c('--kupala-bank', '#b39463'),
      water: c('--kupala-water', '#2a5877'),
      water2: c('--kupala-water2', '#1b3b55'),
      night: c('--kupala-night', 'rgb(6, 10, 26)'),
      shadow: 'rgba(10, 18, 10, .35)',
      text: c('--text', '#ecf1ea'),
      accent: c('--accent', '#f4c542'),
      danger: c('--danger', '#e57373'),
      ink: '#161b14',
      seats: SEAT_VARS.map(([n, f]) => c(n, f)),
    };
  }

  /// Кругла пляма: біла в середині, прозора на краю. Нею «вирізаємо» світло в темряві й малюємо сяйво.
  function stamp(r, g, b, stops) {
    const c = document.createElement('canvas');
    c.width = c.height = 128;
    const x = c.getContext('2d');
    const gr = x.createRadialGradient(64, 64, 0, 64, 64, 64);
    for (const [at, a] of stops) gr.addColorStop(at, 'rgba(' + r + ',' + g + ',' + b + ',' + a + ')');
    x.fillStyle = gr;
    x.fillRect(0, 0, 128, 128);
    return c;
  }

  function lcg(seed) {
    let s = seed >>> 0;
    return () => ((s = (Math.imul(s, 1103515245) + 12345) >>> 0) / 4294967296);
  }

  function drawStatic(map, spots, pal, S, labelPx) {
    const c = document.createElement('canvas');
    c.width = Math.round(WW * S);
    c.height = Math.round(WH * S);
    const g = c.getContext('2d');
    g.scale(S, S);
    const at = (x, y) => (map[y] && map[y][x]) || '#';
    const rnd = lcg(2406);

    // луг
    g.fillStyle = pal.grass;
    g.fillRect(0, 0, WW, WH);
    g.fillStyle = pal.grass2;
    for (let i = 0; i < 600; i++) {
      g.beginPath();
      g.ellipse(rnd() * WW, rnd() * 15 * CELL, 3 + rnd() * 7, 2 + rnd() * 3, rnd() * 3, 0, TAU);
      g.fill();
    }
    // ліс: темніша земля й папороть
    g.fillStyle = pal.forest;
    g.fillRect(0, 0, WW, 5 * CELL - 6);
    g.beginPath(); g.ellipse(WW / 2, 5 * CELL - 6, WW / 2 + 30, 10, 0, 0, Math.PI); g.fill();
    g.strokeStyle = 'rgba(120, 170, 90, .35)';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = 0; i < 90; i++) {
      const x = rnd() * WW, y = 20 + rnd() * (4 * CELL);
      for (let k = -2; k <= 2; k++) { g.moveTo(x, y); g.lineTo(x + k * 4, y - 7 + Math.abs(k)); }
    }
    g.stroke();
    // квіточки на лузі
    for (let i = 0; i < 160; i++) {
      g.fillStyle = ['#f2efe6', '#e8c33a', '#c86bd0', '#6fb3e8'][i % 4];
      g.beginPath(); g.arc(rnd() * WW, 5 * CELL + rnd() * 8 * CELL, 1.3, 0, TAU); g.fill();
    }

    // берег
    const by = 14 * CELL;
    g.fillStyle = pal.bank;
    g.fillRect(0, by - 2, WW, CELL + 4);
    g.fillStyle = 'rgba(90, 60, 30, .25)';
    for (let i = 0; i < 120; i++) { g.beginPath(); g.arc(rnd() * WW, by + rnd() * CELL, 0.8 + rnd() * 1.5, 0, TAU); g.fill(); }
    // річка: темнішає до середини, світла смужка під берегом
    const wy = 15 * CELL, wh = 4 * CELL;
    const wg = g.createLinearGradient(0, wy, 0, wy + wh);
    wg.addColorStop(0, pal.water);
    wg.addColorStop(1, pal.water2);
    g.fillStyle = wg;
    g.fillRect(0, wy, WW, wh);
    g.fillStyle = 'rgba(200, 230, 255, .10)';
    g.fillRect(0, wy, WW, 3);
    // той берег — очерет
    g.fillStyle = '#3f5a2c';
    g.fillRect(0, 19 * CELL, WW, CELL);
    g.strokeStyle = '#6d8a3e';
    g.lineWidth = 1.4;
    g.beginPath();
    for (let x = 2; x < WW; x += 5) {
      const h = 10 + rnd() * 14;
      g.moveTo(x, WH); g.lineTo(x + (rnd() - 0.5) * 5, 19 * CELL + 16 - h);
    }
    g.stroke();
    g.fillStyle = '#5a3b22';
    for (let x = 12; x < WW; x += 37 + rnd() * 30) { g.beginPath(); g.ellipse(x, 19 * CELL + 6 - rnd() * 6, 2, 5, 0, 0, TAU); g.fill(); }

    // кладки над водою + що біля кожної
    for (const s of spots || []) spot(g, s, pal, labelPx);

    // хаща по краю: суцільні крони
    for (let y = 0; y < 15; y++)
      for (let x = 0; x < 30; x++)
        if (at(x, y) === '#') edge(g, x * CELL + 16, y * CELL + 16, rnd);
    // дерева, кущі, копиці, верби, Марена, вогнища (кострище; полум'я — наживо)
    for (let y = 0; y < map.length; y++)
      for (let x = 0; x < map[y].length; x++) {
        const ch = at(x, y), cx = x * CELL + 16, cy = y * CELL + 16;
        if (ch === 'T') tree(g, cx, cy, rnd);
        else if (ch === 'b') bush(g, cx, cy, rnd);
        else if (ch === 'Y') hay(g, cx, cy);
        else if (ch === 'V') willow(g, cx, cy, rnd);
        else if (ch === 'M') marena(g, cx, cy);
        else if (ch === 'F') hearth(g, cx, cy);
      }
    return c;
  }

  function edge(g, x, y, rnd) {
    g.fillStyle = '#1f3a1d';
    g.beginPath(); g.arc(x, y, 19, 0, TAU); g.fill();
    g.fillStyle = '#2a4a26';
    g.beginPath(); g.arc(x - 4 + rnd() * 8, y - 4 + rnd() * 6, 12, 0, TAU); g.fill();
  }

  function tree(g, x, y, rnd) {
    g.fillStyle = 'rgba(5, 15, 5, .3)';
    g.beginPath(); g.ellipse(x + 5, y + 7, 17, 12, 0, 0, TAU); g.fill();
    g.fillStyle = '#5b3a1f';
    g.beginPath(); g.arc(x, y + 2, 4, 0, TAU); g.fill();
    const greens = ['#24552a', '#2f6630', '#3b7a38'];
    for (let i = 0; i < 3; i++) {
      g.fillStyle = greens[i];
      g.beginPath();
      g.arc(x + (i - 1) * 5 + rnd() * 2, y - 3 + (i === 1 ? -4 : 0), 12 - i * 2, 0, TAU);
      g.fill();
    }
  }

  function bush(g, x, y, rnd) {
    g.fillStyle = 'rgba(5, 15, 5, .28)';
    g.beginPath(); g.ellipse(x + 3, y + 6, 14, 8, 0, 0, TAU); g.fill();
    g.fillStyle = '#35682f';
    for (let i = 0; i < 4; i++) { g.beginPath(); g.arc(x - 7 + i * 5, y - 2 + (i % 2) * 3, 8, 0, TAU); g.fill(); }
    // калина
    g.fillStyle = '#d33a2c';
    for (let i = 0; i < 7; i++) { g.beginPath(); g.arc(x - 8 + rnd() * 16, y - 6 + rnd() * 10, 1.6, 0, TAU); g.fill(); }
  }

  function hay(g, x, y) {
    g.fillStyle = 'rgba(20, 20, 0, .25)';
    g.beginPath(); g.ellipse(x + 3, y + 6, 15, 10, 0, 0, TAU); g.fill();
    g.fillStyle = '#c9a444';
    g.beginPath(); g.ellipse(x, y, 14, 11, 0, 0, TAU); g.fill();
    g.strokeStyle = '#98782a';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = -10; i <= 10; i += 4) { g.moveTo(x + i, y - 8); g.lineTo(x + i * 0.7, y + 8); }
    g.stroke();
  }

  function willow(g, x, y, rnd) {
    g.fillStyle = 'rgba(5, 15, 5, .3)';
    g.beginPath(); g.ellipse(x + 4, y + 8, 20, 12, 0, 0, TAU); g.fill();
    g.fillStyle = '#5b3a1f';
    g.beginPath(); g.arc(x, y + 4, 4, 0, TAU); g.fill();
    g.fillStyle = '#4c7a36';
    g.beginPath(); g.arc(x, y - 4, 16, 0, TAU); g.fill();
    // віття звисає до води
    g.strokeStyle = '#7fa451';
    g.lineWidth = 1.2;
    g.beginPath();
    for (let i = 0; i < 14; i++) {
      const a = Math.PI * (0.05 + (0.9 * i) / 13), sx = x + Math.cos(a) * 15, sy = y - 4 - Math.sin(a) * 6;
      g.moveTo(sx, sy); g.quadraticCurveTo(sx + (rnd() - 0.5) * 4, sy + 10, sx + (rnd() - 0.5) * 3, sy + 20 + rnd() * 6);
    }
    g.stroke();
  }

  /// Марена — солом'яне опудало у вінку зі стрічками.
  function marena(g, x, y) {
    g.fillStyle = 'rgba(0, 0, 0, .25)';
    g.beginPath(); g.ellipse(x + 3, y + 12, 12, 5, 0, 0, TAU); g.fill();
    g.strokeStyle = '#6b4424';
    g.lineWidth = 2;
    g.beginPath(); g.moveTo(x, y + 12); g.lineTo(x, y - 14); g.moveTo(x - 10, y - 4); g.lineTo(x + 10, y - 4); g.stroke();
    g.fillStyle = '#d9b44a';
    g.beginPath(); g.moveTo(x - 9, y + 12); g.lineTo(x + 9, y + 12); g.lineTo(x + 3, y - 6); g.lineTo(x - 3, y - 6); g.closePath(); g.fill();
    g.fillStyle = '#f2efe6';
    g.beginPath(); g.arc(x, y - 12, 4.5, 0, TAU); g.fill();
    const flowers = ['#c0392b', '#e8c33a', '#2f6db5', '#c86bd0'];
    for (let i = 0; i < 6; i++) { g.fillStyle = flowers[i % 4]; g.beginPath(); g.arc(x - 5 + i * 2, y - 16 + Math.abs(i - 2.5) * 0.6, 1.6, 0, TAU); g.fill(); }
    g.strokeStyle = '#c0392b'; g.lineWidth = 1.2;
    g.beginPath(); g.moveTo(x - 3, y - 12); g.quadraticCurveTo(x - 9, y - 4, x - 7, y + 2); g.stroke();
    g.strokeStyle = '#2f6db5';
    g.beginPath(); g.moveTo(x + 3, y - 12); g.quadraticCurveTo(x + 9, y - 4, x + 8, y + 3); g.stroke();
  }

  /// Кострище: кільце каменів і перехрещені поліна (полум'я — наживо, поверх темряви).
  function hearth(g, x, y) {
    g.fillStyle = 'rgba(0, 0, 0, .35)';
    g.beginPath(); g.ellipse(x, y + 3, 15, 10, 0, 0, TAU); g.fill();
    g.fillStyle = '#7d7a73';
    for (let i = 0; i < 9; i++) {
      const a = (i / 9) * TAU;
      g.beginPath(); g.ellipse(x + Math.cos(a) * 12, y + 2 + Math.sin(a) * 8, 3.4, 2.6, a, 0, TAU); g.fill();
    }
    g.strokeStyle = '#4a2c16';
    g.lineWidth = 3.5;
    g.lineCap = 'round';
    g.beginPath(); g.moveTo(x - 8, y + 6); g.lineTo(x + 7, y - 3); g.moveTo(x + 8, y + 6); g.lineTo(x - 7, y - 3); g.stroke();
    g.lineCap = 'butt';
  }

  /// Кладка: дошки з берега у воду, а поруч — з чим її впізнати (верба, човен, брід, млинок…) і табличка.
  function spot(g, s, pal, labelPx) {
    const x = s.x * CELL, y = s.y * CELL;
    g.fillStyle = 'rgba(0, 0, 0, .25)';
    g.fillRect(x + 7, y + 20, 20, 26);
    g.fillStyle = '#8a5a33';
    g.fillRect(x + 5, y + 16, 22, 28);
    g.strokeStyle = 'rgba(40, 20, 5, .6)';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = 1; i < 4; i++) { g.moveTo(x + 5, y + 16 + i * 7); g.lineTo(x + 27, y + 16 + i * 7); }
    g.stroke();
    g.fillStyle = '#5b3a1f';
    g.fillRect(x + 5, y + 42, 3, 8); g.fillRect(x + 24, y + 42, 3, 8);
    g.font = '15px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(s.emoji, x + 16, y + 63);
    g.font = '600 ' + labelPx + 'px system-ui, sans-serif';
    const tw = g.measureText(s.name).width + labelPx * 0.9, th = labelPx + 4, ly = y + 63 + 9 + th / 2;
    g.fillStyle = 'rgba(250, 244, 226, .9)';
    g.beginPath(); g.roundRect(x + 16 - tw / 2, ly - th / 2, tw, th, 3); g.fill();
    g.fillStyle = '#3a2412';
    g.fillText(s.name, x + 16, ly + 0.5);
  }

  // ---------------------------------------------------------------------------------------------
  // Селяни — на Купала: вінки, брилі, хустки, чуби
  // ---------------------------------------------------------------------------------------------

  /// Фігурка 3/4 (як у Юрмі), (x, y) — земля між ногами. hat: 0 вінок зі стрічками, 1 бриль, 2 хустка, 3 чуб.
  function villager(g, x, y, d, s, look, now, id, ink) {
    if (s >= 2) return lying(g, x, y, d, look, ink);
    const hat = look[0], hc = look[1], shirt = look[2], skin = look[3];
    const side = d === 0 ? 1 : d === 2 ? -1 : 0;
    let bob = 0, step = 0;
    if (s === 1) {
      const ph = Math.sin(now * 0.0503 + id * 1.7);
      bob = ph > 0 ? -1 : 0;
      step = ph > 0 ? 1 : -1;
    }
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
    const cloth = CLOTH[shirt] || CLOTH[0], skinC = SKIN[skin] || SKIN[0];
    g.fillStyle = cloth;
    g.beginPath(); g.roundRect(x - 7, y - 14, 14, 13, 4); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 1;
    g.stroke();
    g.fillStyle = 'rgba(0, 0, 0, .22)';
    g.fillRect(x - 7, y - 6, 14, 2);
    if (d === 1) {            // вишитий комір
      g.fillStyle = shirt === 0 ? '#f2efe6' : '#c0392b';
      g.fillRect(x - 1, y - 13, 2, 5);
    }
    g.fillStyle = skinC;
    g.beginPath();
    if (side) g.arc(x - side * 1 + side * 3 * step, y - 5, 2.2, 0, TAU);
    else { g.arc(x - 8, y - 6 + step, 2.2, 0, TAU); g.arc(x + 8, y - 6 - step, 2.2, 0, TAU); }
    g.fill();
    const hx = x + side * 1.5, hy = y - 19;
    // волосся/коса під вінком і хусткою не видно — лише голова
    g.beginPath(); g.arc(hx, hy, 6.2, 0, TAU); g.fill();
    g.strokeStyle = ink;
    g.lineWidth = 0.9;
    g.stroke();
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
      case 0: { // вінок: волосся, коло квіток і стрічки
        g.fillStyle = HAIR[hc] || HAIR[0];
        g.beginPath();
        if (d === 3) g.arc(hx, hy, 6.3, 0, TAU);
        else g.arc(hx, hy - 0.8, 6.3, Math.PI * 1.05, Math.PI * 1.95);
        g.fill();
        if (d === 3) {         // спиною — стрічки донизу
          g.strokeStyle = hcol; g.lineWidth = 1.6;
          g.beginPath(); g.moveTo(hx - 2, hy + 2); g.lineTo(hx - 3, hy + 12); g.moveTo(hx + 2, hy + 2); g.lineTo(hx + 3, hy + 11); g.stroke();
        }
        const petals = ['#e03b3b', '#f2c53a', hcol, '#f2efe6', '#e03b3b'];
        for (let i = 0; i < 5; i++) {
          g.fillStyle = petals[i];
          g.beginPath(); g.arc(hx - 5.2 + i * 2.6, hy - 5.2 + Math.abs(i - 2) * 0.9, 1.9, 0, TAU); g.fill();
        }
        break;
      }
      case 1: // бриль
        g.fillStyle = '#d8bf73';
        g.beginPath(); g.ellipse(hx, hy - 3.5, 10, 3.6, 0, 0, TAU); g.fill();
        g.strokeStyle = 'rgba(0, 0, 0, .35)';
        g.lineWidth = 0.8;
        g.stroke();
        g.beginPath(); g.ellipse(hx, hy - 6, 5.4, 4.2, 0, Math.PI, TAU); g.closePath(); g.fill();
        g.fillStyle = hcol;
        g.fillRect(hx - 5.4, hy - 6.2, 10.8, 1.8);
        break;
      case 2: // хустка з горошком
        g.fillStyle = hcol;
        g.beginPath();
        if (d === 3) g.arc(hx, hy, 6.9, 0, TAU);
        else if (side) g.ellipse(hx - side * 1.6, hy - 1.6, 6.4, 6.2, 0, Math.PI * 0.95, Math.PI * 2.05);
        else g.arc(hx, hy - 0.4, 6.9, Math.PI * 1.02, Math.PI * 1.98);
        g.fill();
        if (side) { g.beginPath(); g.arc(hx - side * 6.4, hy + 1.6, 2, 0, TAU); g.fill(); }
        g.fillStyle = 'rgba(255, 255, 255, .45)';
        g.fillRect(hx - 3, hy - 4.5, 1.3, 1.3); g.fillRect(hx + 1.5, hy - 3.2, 1.3, 1.3);
        break;
      default: { // чуб
        g.fillStyle = HAIR[hc] || HAIR[0];
        g.beginPath();
        if (d === 3) g.arc(hx, hy, 6.3, 0, TAU);
        else if (side) g.ellipse(hx - side * 1.4, hy - 1.8, 6, 5.4, 0, Math.PI * 0.9, Math.PI * 2.1);
        else g.arc(hx, hy - 0.8, 6.3, Math.PI * 1.05, Math.PI * 1.95);
        g.fill();
      }
    }
  }

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
    if (look[0] !== 3) {      // вінок/бриль/хустка відлетіли поруч
      g.fillStyle = look[0] === 1 ? '#d8bf73' : CLOTH[look[1]] || CLOTH[0];
      g.beginPath(); g.ellipse(hx + dir * 9, hy + 6, 5, 3, 0.4 * dir, 0, TAU); g.fill();
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Стан модуля
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    let st = root._kupala;
    if (!st) {
      st = root._kupala = {
        root, ctx: null, cv: null, interp: HGames.ui.Interp(), mode: 'full', S: 1,
        view: null, n: 0, looks: [], names: [], map: null, mapKey: '', spots: [], meId: -1, me: null,
        round: -1, vphase: '', last: null, lastAt: 0, fph: '',
        // хто зараз у кадрі, коли з'явився/зник і де зник — для м'якої появи й згасання
        pres: new Uint8Array(64), inAt: new Float64Array(64), outAt: new Float64Array(64),
        gx: new Float64Array(64), gy: new Float64Array(64), gd: new Int8Array(64), gs: new Int8Array(64),
        px: new Float64Array(64), py: new Float64Array(64), pd: new Int8Array(64), ps: new Int8Array(64), pa: new Float64Array(64),
        order: [], cap: 64,
        stat: null, statKey: '', mini: null, pal: null, palAt: -1e9, dark: null, darkG: null,
        stamps: null,
        cam: { x: WW / 2, y: WH / 2 }, box: [0, 0, WW, WH], drag: null, hover: false,
        keys: [], touchDir: -1, dir: -1, localDir: -1, localUntil: 0, peekUntil: 0, sentAt: 0,
        slapAt: -1e9, launchAt: -1e9, busyUntil: 0, stunUntil: 0, tip: null, tipUntil: 0, autoPeek: false,
        seen: null, phaseAt: 0,
        fx: [], launches: [],
        raf: 0, keyup: null, blur: null, ro: null, io: null, onResize: null, visible: true,
        padOn: false, padH: 0, padAt: 0, labelPx: 9, lab: [],
        hudEl: null, clockEl: null, newsEl: null, sumEl: null, padEl: null, stageEl: null, seatsEl: null,
        audio: null, mute: readMute(),
        perf: { sum: 0, n: 0, max: 0 },
      };
    }
    st.ctx = ctx;
    ctx._kupala = st;
    return st;
  }

  const seatOf = (st, i) => (st.view && st.view.seats || []).find((s) => s.seat === i) || null;
  const nickOfSeat = (st, i) => { const s = seatOf(st, i); return (s && s.nick) || (st.ctx && st.ctx.nickOf(i)) || SEAT_NAMES[i] || '?'; };
  const lookOf = (st, id) => { const l = st.looks; return [l[id * 4] | 0, l[id * 4 + 1] | 0, l[id * 4 + 2] | 0, l[id * 4 + 3] | 0]; };
  const nameOf = (st, id) => (st.names && st.names[id]) || 'селянин';
  const alive = (st) => !!(st.me && st.me.alive);
  const phaseOf = (st) => (st.vphase === 'over' ? 'over' : st.fph || st.vphase);
  /// Мій селянин зараз у кадрі (у світлі)?
  const meLit = (st) => st.meId >= 0 && st.meId < st.cap && st.pres[st.meId] === 1;

  function refuse(st, text, toasted) {
    if (!text) return;
    if (!toasted && st.ctx) st.ctx.toast(text, 'err');
    st.tip = text;
    st.tipUntil = performance.now() + TIP_MS;
  }

  function looksLike(st, id) {
    const [hat, hc, shirt] = lookOf(st, id);
    if (hat === 0) return 'у вінку, у ' + CLOTH_F[shirt] + ' сорочці';
    if (hat === 1) return 'у брилі, у ' + CLOTH_F[shirt] + ' сорочці';
    if (hat === 2) return 'у ' + CLOTH_F[hc] + ' хустці';
    return 'з чубом, у ' + CLOTH_F[shirt] + ' сорочці';
  }

  /// Стою на якій кладці (лише коли бачу себе; сервер однаково перевірить сам).
  function myspot(st) {
    if (!meLit(st)) return -1;
    const cx = Math.floor(st.px[st.meId] / CELL), cy = Math.floor(st.py[st.meId] / CELL);
    for (const s of st.spots) if (s.x === cx && s.y === cy) return s.i;
    return -1;
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
      const tone = (f0, f1, dur, vol, type, delay) => {
        const o = A.createOscillator();
        const t0 = t + (delay || 0);
        o.type = type || 'sine';
        o.frequency.setValueAtTime(f0, t0);
        if (f1 !== f0) o.frequency.exponentialRampToValueAtTime(f1, t0 + dur);
        const gn = A.createGain();
        gn.gain.setValueAtTime(vol, t0);
        gn.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
        o.connect(gn); gn.connect(out);
        o.start(t0); o.stop(t0 + dur + 0.02);
      };
      const noise = (dur, vol, hp) => {
        const len = Math.floor(A.sampleRate * dur);
        const buf = A.createBuffer(1, len, A.sampleRate);
        const ch = buf.getChannelData(0);
        for (let i = 0; i < len; i++) ch[i] = (Math.random() * 2 - 1) * (1 - i / len);
        const src = A.createBufferSource();
        src.buffer = buf;
        const f = A.createBiquadFilter();
        f.type = 'highpass';
        f.frequency.value = hp || 200;
        const gn = A.createGain();
        gn.gain.value = vol;
        src.connect(f); f.connect(gn); gn.connect(out);
        src.start(t);
      };
      if (kind === 'slap') { noise(0.06, 0.09, 900); tone(220, 120, 0.05, 0.04, 'triangle'); }
      else if (kind === 'whiff') noise(0.16, 0.03, 2400);
      else if (kind === 'torch') noise(0.3, 0.025, 500);
      else if (kind === 'hiss') noise(0.5, 0.03, 3000);
      else if (kind === 'wreath') { tone(420, 180, 0.12, 0.04, 'sine'); tone(660, 660, 0.08, 0.015, 'sine', 0.05); }
      else if (kind === 'fern') { tone(1320, 1320, 0.2, 0.025, 'sine'); tone(1760, 1760, 0.25, 0.02, 'sine', 0.08); }
      else if (kind === 'out') tone(420, 110, 0.2, 0.05, 'triangle');
    } catch { /* без звуку гра повна */ }
  }

  // ---------------------------------------------------------------------------------------------
  // Вид і кадри
  // ---------------------------------------------------------------------------------------------

  function grow(st, n) {
    if (st.cap >= n) return;
    const m = Math.max(n, st.cap * 2);
    const f64 = (a) => { const b = new Float64Array(m); b.set(a); return b; };
    const i8 = (a) => { const b = new Int8Array(m); b.set(a); return b; };
    const u8 = new Uint8Array(m); u8.set(st.pres); st.pres = u8;
    st.inAt = f64(st.inAt); st.outAt = f64(st.outAt); st.gx = f64(st.gx); st.gy = f64(st.gy); st.gd = i8(st.gd); st.gs = i8(st.gs);
    st.px = new Float64Array(m); st.py = new Float64Array(m); st.pd = new Int8Array(m); st.ps = new Int8Array(m); st.pa = new Float64Array(m);
    st.cap = m;
  }

  /// Кадр із дроту → щільні масиви за id: хто є (has) і де (p: x, y, d, s).
  function decode(st, f) {
    const v = f.v || [];
    let n = st.n | 0;
    for (let i = 0; i < v.length; i += 5) if (v[i] + 1 > n) n = v[i] + 1;
    grow(st, n);
    const has = new Uint8Array(n), p = new Int16Array(n * 4);
    for (let i = 0; i < v.length; i += 5) {
      const id = v[i], j = id * 4;
      has[id] = 1;
      p[j] = v[i + 1]; p[j + 1] = v[i + 2]; p[j + 2] = v[i + 3]; p[j + 3] = v[i + 4];
    }
    return { t: f.t | 0, ph: f.ph, left: f.left | 0, sky: f.sky | 0, n, has, p, l: f.l || [], ev: f.ev || [] };
  }

  /// Хто з'явився у світлі, а хто зник у темряві: з'явився — проступає за 160 мс, зник — там само згасає.
  function presence(st, f, now) {
    const pres = st.pres;
    for (let id = 0; id < st.cap; id++) {
      const was = pres[id] === 1, is = id < f.n && f.has[id] === 1;
      if (is && !was) st.inAt[id] = now;
      else if (!is && was) {
        st.outAt[id] = now;
        st.gx[id] = st.px[id]; st.gy[id] = st.py[id]; st.gd[id] = st.pd[id]; st.gs[id] = st.ps[id];
      }
      pres[id] = is ? 1 : 0;
    }
    // себе пам'ятаємо там, де бачили востаннє
    if (st.meId >= 0 && st.meId < f.n && f.has[st.meId]) st.seen = { x: f.p[st.meId * 4], y: f.p[st.meId * 4 + 1], at: now };
  }

  function resetPresence(st) {
    st.pres.fill(0);
    st.inAt.fill(0);
    st.outAt.fill(0);
    st.interp.reset();
    st.last = null;
  }

  function applyView(st, v) {
    if (!v) return;
    st.view = v;
    if (v.map) {
      const key = v.map.join('');
      if (key !== st.mapKey) { st.mapKey = key; st.map = v.map; st.spots = v.spots || []; st.statKey = ''; }
    }
    st.n = v.n | 0;
    grow(st, st.n);
    st.looks = v.looks || [];
    st.names = v.names || [];
    st.me = v.me || null;
    st.meId = st.me ? st.me.id : -1;
    if (v.round !== st.round || (v.phase === 'start' && st.vphase && st.vphase !== 'start')) {
      st.round = v.round;
      st.camRound = -1;
      resetPresence(st);
      st.fx.length = 0;
      st.launches.length = 0;
      st.slapAt = st.launchAt = -1e9;
      st.busyUntil = st.stunUntil = 0;
      st.seen = null;
    }
    const now = performance.now();
    if (st.me && st.me.busy > 0) st.busyUntil = Math.max(st.busyUntil, now + st.me.busy * TICK_MS);
    if (st.me && st.me.stun > 0) st.stunUntil = Math.max(st.stunUntil, now + st.me.stun * TICK_MS);
    if (st.autoPeek && st.me) {
      if (v.phase === 'go' && st.me.alive) { st.peekUntil = now + F5_PEEK_MS; st.autoPeek = false; }
      else if (v.phase === 'start') st.autoPeek = false;
    }
    if (v.v) {
      const f = decode(st, { t: v.t, ph: v.phase, left: v.left, sky: v.sky, v: v.v, l: v.l });
      const last = st.last;
      if (!last || f.t > last.t || (f.t < last.t && f.ph === 'start') || v.phase === 'over' || v.phase === 'lobby') {
        push(st, f, false);
        presence(st, f, now);
      }
    }
    if (v.phase !== st.vphase) st.phaseAt = now;
    st.vphase = v.phase;
  }

  function push(st, f, fromFrame) {
    const last = st.last;
    if (last && (f.t < last.t || last.n !== f.n)) st.interp.reset();
    st.interp.push(f);
    st.last = f;
    if (fromFrame) st.lastAt = performance.now();
  }

  function onFrame(st, raw) {
    if (!raw || !raw.v) return;
    const now = performance.now();
    if (st.last && raw.t < st.last.t && raw.ph === 'start') resetPresence(st);
    const f = decode(st, raw);
    push(st, f, true);
    presence(st, f, now);
    if (f.ev.length) events(st, f, now);
    const walk = (f.ph === 'start' || f.ph === 'go') && st.dir >= 0 && st.ctx && st.ctx.mine && st.ctx.playing;
    if (f.ph !== st.fph) {
      if (walk) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
      st.fph = f.ph;
      st.phaseAt = now;
    }
    if (walk && now - st.sentAt > HOLD_MS) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
    if (st.localUntil && st.meId >= 0 && f.has[st.meId] && f.p[st.meId * 4 + 2] === st.localDir) st.localUntil = 0;
    paintClock(st, f);
  }

  function events(st, f, now) {
    const quiet = reduced();
    const esc = st.ctx.esc;
    for (const e of f.ev) {
      if (e[0] === 1) {
        const a = e[1], b = e[2], kind = e[3], seat = e[4];
        st.fx.push({ k: kind === 2 ? 'whiff' : 'slap', x: e[5], y: e[6], at: now, a, b });
        if (kind === 0 && a >= 0) st.fx.push({ k: 'stun', id: a, at: now });
        if (kind === 1) {
          const name = nameOf(st, b), she = FEMALE.has(name);
          const who = a === st.meId && a >= 0 ? 'Твій ляпас!' : a >= 0 ? 'Ляснув хтось ' + looksLike(st, a) : 'Ляснули з темряви';
          news(st, '💥 <b class="kupala-s' + seat + '">' + esc(nickOfSeat(st, seat)) + '</b> вибуває — це ' + (she ? 'була ' : 'був ') + esc(name) + '! ' + who);
          sfx(st, 'slap'); sfx(st, 'out');
        } else if (kind === 0) {
          if (b >= 0) {
            const name = nameOf(st, b), she = FEMALE.has(name);
            news(st, '✋ Ляп! ' + esc(name) + (she ? ' впала — просто селянка. ' : ' впав — просто селянин. ')
              + (a >= 0 && a === st.meId ? 'Тепер ти отетерів — і тебе бачили' : a >= 0 ? 'Ляснув хтось ' + looksLike(st, a) : 'Хто ляснув — не видно'));
          } else news(st, '✋ Ляп! Хтось у темряві роздає ляпаси');
          sfx(st, 'slap');
        } else {
          news(st, a >= 0 && a !== st.meId ? '💨 Махнув рукою хтось ' + looksLike(st, a) : '💨 Хтось махнув рукою в темряві');
          sfx(st, 'whiff');
        }
      } else if (e[0] === 2) {
        const s = st.spots[e[1]];
        st.launches.push({ k: e[1], at: now });
        if (st.launches.length > 12) st.launches.shift();
        if (s) news(st, '🌼 Хтось пустив вінок ' + esc(s.where));
        sfx(st, 'wreath');
      } else if (e[0] === 3) {
        const wet = e[6] === 1;
        st.fx.push({ k: 'torch', x0: e[2], y0: e[3], x1: e[4], y1: e[5], wet, at: now, land: now + (quiet ? 0 : FLY_MS) });
        news(st, wet ? '💦 Головешка шипить у річці' : e[1] >= 0 && e[1] !== st.meId ? '🔥 Головешку шпурнув хтось ' + looksLike(st, e[1]) : '🔥 Хтось шпурнув головешку');
        sfx(st, wet ? 'hiss' : 'torch');
      } else if (e[0] === 4) {
        news(st, '🌺 Хтось зірвав цвіт папороті!');
        sfx(st, 'fern');
      } else if (e[0] === 5) {
        news(st, '🌺 Десь у лісі зацвіла папороть!');
        sfx(st, 'fern');
      }
    }
    if (st.fx.length > 24) st.fx.splice(0, st.fx.length - 24);
  }

  function news(st, html) {
    const el = st.newsEl;
    if (!el) return;
    const item = document.createElement('div');
    item.className = 'kupala-ni';
    item.innerHTML = html;
    el.insertBefore(item, el.firstChild);
    while (el.children.length > 2) el.lastChild.remove();
    setTimeout(() => item.remove(), NEWS_MS);
  }

  // ---------------------------------------------------------------------------------------------
  // Позиції, камера, ляпас
  // ---------------------------------------------------------------------------------------------

  /// Інтерпольовані позиції тих, хто у світлі, + тих, хто щойно згас. pa — прозорість (0 — не малювати).
  function positions(st, now) {
    const cur = st.interp.at();
    const b = (cur && cur.b) || st.last;
    if (!b) return 0;
    let a = cur && cur.a;
    if (!a || a.n !== b.n) a = b;
    const k = cur ? cur.t : 1;
    const n = Math.min(b.n, st.cap);
    const { px, py, pd, ps, pa } = st;
    const still = reduced();
    for (let i = 0; i < n; i++) {
      const j = i * 4;
      if (b.has[i]) {
        if (a.has[i]) {
          const x0 = a.p[j], y0 = a.p[j + 1], x1 = b.p[j], y1 = b.p[j + 1];
          const jump = Math.abs(x1 - x0) > 12 || Math.abs(y1 - y0) > 12;
          px[i] = jump ? x1 : x0 + (x1 - x0) * k;
          py[i] = jump ? y1 : y0 + (y1 - y0) * k;
        } else { px[i] = b.p[j]; py[i] = b.p[j + 1]; }
        pd[i] = b.p[j + 2];
        ps[i] = b.p[j + 3] === 1 && st.vphase === 'over' ? 0 : b.p[j + 3];
        pa[i] = still ? 1 : clamp((now - st.inAt[i]) / FADE_MS, 0, 1);
      } else {
        const out = now - st.outAt[i];
        if (st.outAt[i] > 0 && out < FADE_MS && !still) {
          px[i] = st.gx[i]; py[i] = st.gy[i]; pd[i] = st.gd[i]; ps[i] = st.gs[i];
          pa[i] = 1 - out / FADE_MS;
        } else pa[i] = 0;
      }
    }
    const me = st.meId;
    if (me >= 0 && me < n && b.has[me] && now < st.localUntil && ps[me] < 2) {
      pd[me] = st.localDir;
      if (st.dir >= 0) ps[me] = 1;
    }
    const order = st.order;
    order.length = 0;
    for (let i = 0; i < n; i++) if (pa[i] > 0) order.push(i);
    for (let i = 1; i < order.length; i++) {
      const id = order[i], y = py[id];
      let j = i - 1;
      while (j >= 0 && py[order[j]] > y) { order[j + 1] = order[j]; j--; }
      order[j + 1] = id;
    }
    return n;
  }

  /// Той самий конус, що й на сервері (KupalaCore.Cone), — але лише серед тих, кого я бачу, і лише коли бачу себе.
  function coneTarget(st, n) {
    const me = st.meId;
    if (!meLit(st) || me >= n) return -1;
    const d = st.pd[me], fx = DX[d] || 0, fy = DY[d] || 0, x = st.px[me], y = st.py[me];
    let best = -1, bestD = Infinity;
    for (let i = 0; i < n; i++) {
      if (i === me || !st.pres[i] || st.ps[i] >= 2) continue;
      const dx = st.px[i] - x, dy = st.py[i] - y, d2 = dx * dx + dy * dy;
      if (d2 > SLAP_REACH * SLAP_REACH) continue;
      const dot = dx * fx + dy * fy;
      if (dot <= 0 || dot * dot * 1000 < CONE * d2) continue;
      if (d2 < bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  /// Камера телефона: за мною, поки мене видно; у темряві — стоїть там, де бачили востаннє (тягни пальцем).
  function camera(st, n) {
    const box = st.box;
    if (st.mode !== 'port') { box[0] = 0; box[1] = 0; box[2] = WW; box[3] = WH; return box; }
    const mine = st.ctx && st.ctx.mine && st.meId >= 0;
    if (mine && !st.drag && (alive(st) || st.vphase === 'start') && meLit(st)) {
      st.cam.x += (st.px[st.meId] - st.cam.x) * 0.25;
      st.cam.y += (st.py[st.meId] - st.cam.y) * 0.25;
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
    if (st.stat && st.statKey && now - st.palAt <= 1000) return true;
    st.pal = palette();
    st.palAt = now;
    if (!st.map) return false;
    if (!st.stamps) {
      st.stamps = {
        hole: stamp(0, 0, 0, [[0, 1], [0.55, 1], [0.8, 0.62], [1, 0]]),
        warm: stamp(255, 160, 60, [[0, 0.9], [0.35, 0.45], [1, 0]]),
        fern: stamp(255, 70, 140, [[0, 1], [0.4, 0.5], [1, 0]]),
        fly: stamp(210, 255, 120, [[0, 1], [0.25, 0.5], [1, 0]]),
        candle: stamp(255, 210, 120, [[0, 0.9], [0.4, 0.35], [1, 0]]),
      };
      st.dark = document.createElement('canvas');
      st.dark.width = WW / 2;
      st.dark.height = WH / 2;
      st.darkG = st.dark.getContext('2d');
    }
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const S = Math.min(2, dpr);
    const key = S + '|' + st.labelPx + '|' + st.mapKey.length + '|' + st.pal.grass + st.pal.water + st.pal.bank;
    if (st.stat && st.statKey === key) return true;
    st.S = S;
    st.stat = drawStatic(st.map, st.spots, st.pal, S, st.labelPx);
    st.statKey = key;
    const m = document.createElement('canvas');
    m.width = Math.round(96 * dpr);
    m.height = Math.round(64 * dpr);
    const mg = m.getContext('2d');
    mg.imageSmoothingQuality = 'high';
    mg.drawImage(st.stat, 0, 0, m.width, m.height);
    mg.fillStyle = 'rgba(6, 10, 26, .55)';
    mg.fillRect(0, 0, m.width, m.height);
    st.mini = m;
    return true;
  }

  /// Скільки темряви зараз: сутінки на «роздивись», ніч, зарниця, світанок на розкритті.
  function darkness(st, now, phase, sky) {
    const since = now - st.phaseAt;
    if (phase === 'go') {
      const night = DUSK_A + (NIGHT_A - DUSK_A) * clamp(since / NIGHT_MS, 0, 1);
      return sky > 0 ? night * (1 - clamp(sky / 10, 0, 1)) + 0.04 : night;
    }
    if (phase === 'start') return DUSK_A;
    if (phase === 'reveal' || phase === 'over') return Math.max(0, 0.5 * (1 - since / DAWN_MS));
    return LOBBY_A;
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
    const cur = st.last;
    const lights = (cur && cur.l) || [];
    const sky = (cur && cur.sky) | 0;

    g.setTransform(1, 0, 0, 1, 0, 0);
    g.globalAlpha = 1;
    g.globalCompositeOperation = 'source-over';
    g.imageSmoothingEnabled = true;
    g.drawImage(st.stat, cx * S, cy * S, vw * S, vh * S, 0, 0, bw, bh);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);

    const phase = phaseOf(st);
    const playing = !!(st.ctx && st.ctx.playing);
    const mine = !!(st.ctx && st.ctx.mine) && st.meId >= 0 && st.meId < n;

    ripples(g, now, cx, vw);
    wreaths(g, lights, now);
    torchesOnGround(g, lights, now);
    listMarks(st, g, pal, now, phase, mine);
    trails(st, g, pal, phase);

    // тіні пачкою, потім селяни за y (з прозорістю появи/згасання)
    g.fillStyle = pal.shadow;
    for (const i of st.order) {
      const x = st.px[i], y = st.py[i];
      if (x < cx - 20 || x > cx + vw + 20 || y < cy - 20 || y > cy + vh + 20) continue;
      g.globalAlpha = st.pa[i];
      g.beginPath(); g.ellipse(x + 1, y + 5, 10, 4, 0, 0, TAU); g.fill();
    }
    const look = [0, 0, 0, 0];
    for (const i of st.order) {
      const x = st.px[i], y = st.py[i];
      if (x < cx - 20 || x > cx + vw + 20 || y < cy - 20 || y > cy + vh + 20) continue;
      g.globalAlpha = st.pa[i];
      look[0] = st.looks[i * 4] | 0; look[1] = st.looks[i * 4 + 1] | 0; look[2] = st.looks[i * 4 + 2] | 0; look[3] = st.looks[i * 4 + 3] | 0;
      villager(g, x, y + 4, st.pd[i], st.ps[i], look, now, i, pal.ink);
    }
    g.globalAlpha = 1;

    // темрява з вирізаними плямами світла, потім тепле сяйво й полум'я поверх
    const A = darkness(st, now, phase, sky);
    if (A > 0.01) night(st, g, lights, A, now, cx, cy, vw, vh, bw, bh, k);
    glow(st, g, lights, now, A);
    if (sky > 0) {
      g.setTransform(1, 0, 0, 1, 0, 0);
      g.fillStyle = 'rgba(200, 215, 255, ' + (0.28 * clamp(sky / 12, 0, 1)).toFixed(3) + ')';
      g.fillRect(0, 0, bw, bh);
      g.setTransform(k, 0, 0, k, -cx * k, -cy * k);
    }

    effects(st, g, pal, now, n);
    const lit = meLit(st);
    if (mine && alive(st) && phase === 'go' && playing && lit) {
      const t = coneTarget(st, n);
      if (t >= 0) chevron(g, st.px[t], st.py[t] - 27, pal);
      if (now < st.busyUntil) busyRing(g, st.px[st.meId], st.py[st.meId], pal, 1 - (st.busyUntil - now) / LAUNCH_MS);
    }
    labels(st, g, pal, n, phase);
    // «де я?»: у світлі — іскорка й стрілка над собою; у темряві — лише де тебе бачили востаннє
    const peek = mine && (phase === 'start' || (phase === 'go' && now < st.peekUntil && alive(st)));
    if (peek) {
      if (lit) { sparkle(g, st.px[st.meId], st.py[st.meId], pal, now); meArrow(g, st.px[st.meId], st.py[st.meId], pal, true); }
      else if (st.seen && phase === 'go') ghost(g, st.seen.x, st.seen.y, pal, now);
    }
    if (mine && now < st.tipUntil && st.tip) tipPlate(g, st, lit ? st.px[st.meId] : st.cam.x, lit ? st.py[st.meId] : cy + vh * 0.72, pal);

    g.setTransform(k, 0, 0, k, 0, 0);
    shade(st, g, pal, cv.w, cv.h, phase, playing, mine, now);
    if (st.mode === 'port') minimap(st, g, pal, cv.w, cx, cy, vw, vh, now, mine && lit);
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

  /// Темрява: шар у пів роздільності (плями світла м'які, тож не видно), заливка ночі й «дірки» на кожне світло.
  function night(st, g, lights, A, now, cx, cy, vw, vh, bw, bh, k) {
    const d = st.darkG, hole = st.stamps.hole;
    d.globalCompositeOperation = 'source-over';
    d.setTransform(1, 0, 0, 1, 0, 0);
    d.clearRect(0, 0, WW / 2, WH / 2);
    d.globalAlpha = A;
    d.fillStyle = st.pal.night;
    d.fillRect(0, 0, WW / 2, WH / 2);
    d.globalAlpha = 1;
    d.globalCompositeOperation = 'destination-out';
    d.setTransform(0.5, 0, 0, 0.5, 0, 0);
    for (let i = 0; i < lights.length; i += 4) {
      const kind = lights[i], x = lights[i + 1], y = lights[i + 2], r = lights[i + 3];
      // мерехтіння — лише косметика краю; хто у світлі, вирішив сервер
      const wob = kind === 0 ? Math.sin(now / 70 + x) * 2 + Math.sin(now / 43 + y) * 1.5 : kind === 1 ? Math.sin(now / 90 + x) * 2 : 0;
      const R = r * 1.25 + wob;
      d.drawImage(hole, x - R, y - R, 2 * R, 2 * R);
    }
    d.globalCompositeOperation = 'source-over';
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.drawImage(st.dark, cx / 2, cy / 2, vw / 2, vh / 2, 0, 0, bw, bh);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);
  }

  /// Тепле сяйво й полум'я — поверх темряви (вони самі світять).
  function glow(st, g, lights, now, A) {
    const sp = st.stamps;
    const warmth = 0.25 + 0.45 * A;
    g.globalCompositeOperation = 'lighter';
    for (let i = 0; i < lights.length; i += 4) {
      const kind = lights[i], x = lights[i + 1], y = lights[i + 2], r = lights[i + 3];
      const s = kind === 4 ? sp.fern : kind === 1 ? sp.fly : kind === 3 ? sp.candle : sp.warm;
      const a = kind === 0 ? 0.55 + 0.1 * Math.sin(now / 60 + x) : kind === 1 ? 0.5 : kind === 4 ? 0.7 : 0.5;
      const R = kind === 1 ? 16 : kind === 3 ? r * 0.55 : r * 0.8;
      g.globalAlpha = a * warmth;
      g.drawImage(s, x - R, y - R, 2 * R, 2 * R);
    }
    g.globalAlpha = 1;
    g.globalCompositeOperation = 'source-over';
    for (let i = 0; i < lights.length; i += 4) {
      const kind = lights[i], x = lights[i + 1], y = lights[i + 2];
      if (kind === 0) flame(g, x, y + 2, now, 1);
      else if (kind === 2) flame(g, x, y - 2, now, 0.55);
      else if (kind === 3) { g.fillStyle = '#fff6c8'; g.beginPath(); g.ellipse(x, y - 5 + Math.sin(now / 80 + x) * 0.6, 1.6, 3, 0, 0, TAU); g.fill(); }
      else if (kind === 1) { g.fillStyle = '#e8ff9a'; g.beginPath(); g.arc(x, y, 1.8, 0, TAU); g.fill(); }
      else if (kind === 4) fern(g, x, y, now);
    }
  }

  function flame(g, x, y, now, s) {
    const f = Math.sin(now / 55 + x) * 1.5, f2 = Math.sin(now / 37 + y) * 1.2;
    g.fillStyle = '#ff7a1a';
    g.beginPath();
    g.moveTo(x - 9 * s, y);
    g.quadraticCurveTo(x - 7 * s, y - 12 * s, x + f * s, y - (22 + f2) * s);
    g.quadraticCurveTo(x + 7 * s, y - 12 * s, x + 9 * s, y);
    g.closePath(); g.fill();
    g.fillStyle = '#ffd24a';
    g.beginPath();
    g.moveTo(x - 5 * s, y);
    g.quadraticCurveTo(x - 3 * s, y - 8 * s, x - f * 0.6 * s, y - (14 + f2) * s);
    g.quadraticCurveTo(x + 4 * s, y - 8 * s, x + 5 * s, y);
    g.closePath(); g.fill();
  }

  /// Цвіт папороті: червоно-золота квітка, що пульсує.
  function fern(g, x, y, now) {
    const p = 1 + 0.12 * Math.sin(now / 160);
    for (let i = 0; i < 6; i++) {
      const a = (i / 6) * TAU + now / 1400;
      g.fillStyle = i % 2 ? '#ff3b6b' : '#ffb13b';
      g.beginPath(); g.ellipse(x + Math.cos(a) * 5 * p, y + Math.sin(a) * 5 * p, 4.5 * p, 2.2 * p, a, 0, TAU); g.fill();
    }
    g.fillStyle = '#fff3b0';
    g.beginPath(); g.arc(x, y, 2.4 * p, 0, TAU); g.fill();
  }

  function ripples(g, now, cx, vw) {
    g.strokeStyle = 'rgba(190, 225, 255, .16)';
    g.lineWidth = 1.2;
    g.beginPath();
    for (let row = 0; row < 7; row++) {
      const y = 15 * CELL + 10 + row * 17;
      const off = (now / (40 + row * 6)) % 90;
      for (let x = -90 + off + row * 23; x < WW; x += 90) {
        if (x < cx - 40 || x > cx + vw + 10) continue;
        g.moveTo(x, y); g.quadraticCurveTo(x + 8, y - 2, x + 16, y);
      }
    }
    g.stroke();
  }

  /// Вінки на воді: коло квіток зі свічкою посередині.
  function wreaths(g, lights, now) {
    const colors = ['#e03b3b', '#f2c53a', '#f2efe6', '#6fb3e8', '#c86bd0'];
    for (let i = 0; i < lights.length; i += 4) {
      if (lights[i] !== 3) continue;
      const x = lights[i + 1], y = lights[i + 2] + Math.sin(now / 400 + lights[i + 1] / 30) * 1.2;
      g.strokeStyle = '#3f7a34';
      g.lineWidth = 3;
      g.beginPath(); g.ellipse(x, y, 9, 5, 0, 0, TAU); g.stroke();
      for (let j = 0; j < 8; j++) {
        const a = (j / 8) * TAU;
        g.fillStyle = colors[(j + (x | 0)) % colors.length];
        g.beginPath(); g.arc(x + Math.cos(a) * 9, y + Math.sin(a) * 5, 1.9, 0, TAU); g.fill();
      }
      g.fillStyle = '#f7ecd0';
      g.fillRect(x - 1, y - 4, 2, 4);
    }
  }

  function torchesOnGround(g, lights) {
    g.strokeStyle = '#4a2c16';
    g.lineWidth = 3;
    g.lineCap = 'round';
    g.beginPath();
    for (let i = 0; i < lights.length; i += 4) {
      if (lights[i] !== 2) continue;
      const x = lights[i + 1], y = lights[i + 2];
      g.moveTo(x - 7, y + 4); g.lineTo(x + 3, y - 1);
    }
    g.stroke();
    g.lineCap = 'butt';
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

  /// Мої кладки, куди ще не пустив вінок, — пунктир (бачу лише я). Над темрявою не малюємо: це мапа, не люди.
  function listMarks(st, g, pal, now, phase, mine) {
    if (!mine || !st.me || !alive(st) || (phase !== 'go' && phase !== 'start')) return;
    g.strokeStyle = pal.accent;
    g.lineWidth = 2;
    g.globalAlpha = 0.45 + 0.3 * Math.sin(now / 260);
    g.setLineDash([5, 4]);
    g.beginPath();
    const list = st.me.list, done = st.me.done;
    for (let i = 0; i < list.length; i++) {
      const s = st.spots[list[i]];
      if (s && !done[i]) g.roundRect(s.x * CELL - 2, s.y * CELL - 2, CELL + 4, 2 * CELL + 4, 7);
    }
    g.stroke();
    g.setLineDash([]);
    g.globalAlpha = 1;
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
    g.lineCap = 'butt';
  }

  /// Ляпаси, помахи, головешки в польоті, зірочки над отетерілим, бризки вінків — поверх темряви: це звук і вогонь.
  function effects(st, g, pal, now, n) {
    const list = st.fx;
    for (let i = list.length - 1; i >= 0; i--) {
      const f = list[i];
      const life = f.k === 'stun' ? STUN_MS : f.k === 'torch' ? FLY_MS + SPLASH_MS : FX_MS;
      if (now - f.at > life) list.splice(i, 1);
    }
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    for (const f of list) {
      const p = (now - f.at) / FX_MS;
      if (f.k === 'slap' || f.k === 'whiff') {
        if (p > 1) continue;
        g.globalAlpha = 1 - p;
        g.font = '900 ' + Math.round(12 + p * 6) + 'px system-ui, sans-serif';
        if (f.k === 'slap') {
          g.fillStyle = '#ffe28a';
          g.beginPath();
          for (let j = 0; j < 10; j++) {
            const a = (j / 10) * TAU, r = j % 2 ? 7 : 15 + p * 6;
            g.lineTo(f.x + Math.cos(a) * r, f.y - 14 + Math.sin(a) * r * 0.7);
          }
          g.closePath(); g.fill();
          outlined(g, 'ЛЯП!', f.x, f.y - 14, '#c0392b', '#fff3c4');
        } else {
          g.strokeStyle = 'rgba(230, 240, 255, .8)';
          g.lineWidth = 2;
          g.beginPath(); g.arc(f.x, f.y - 12, 10 + p * 8, -2.4, -0.6); g.stroke();
          g.beginPath(); g.arc(f.x, f.y - 12, 5 + p * 8, -2.4, -0.6); g.stroke();
          outlined(g, 'ш-ш', f.x, f.y - 30 - p * 6, '#eef3ff', '#1a2233');
        }
      } else if (f.k === 'stun') {
        const id = f.id;
        if (id < 0 || id >= n || !st.pres[id]) continue;
        g.globalAlpha = 1;
        g.font = '10px system-ui, sans-serif';
        g.fillStyle = '#ffe28a';
        for (let s = 0; s < 3; s++) {
          const a = now / 140 + (s * TAU) / 3;
          g.fillText('✦', st.px[id] + Math.cos(a) * 9, st.py[id] - 30 + Math.sin(a) * 3);
        }
      } else if (f.k === 'torch') {
        if (now < f.land) {
          const q = (now - f.at) / Math.max(1, f.land - f.at);
          const x = f.x0 + (f.x1 - f.x0) * q, y = f.y0 - 10 + (f.y1 - f.y0 + 10) * q - Math.sin(q * Math.PI) * 26;
          g.globalAlpha = 1;
          g.strokeStyle = 'rgba(255, 170, 60, .55)';
          g.lineWidth = 2;
          g.beginPath(); g.moveTo(f.x0, f.y0 - 10); g.lineTo(x, y); g.stroke();
          flame(g, x, y + 4, now, 0.45);
        } else if (f.wet) {
          const q = (now - f.land) / SPLASH_MS;
          g.globalAlpha = 1 - q;
          g.strokeStyle = '#cfe8ff';
          g.lineWidth = 1.5;
          g.beginPath(); g.ellipse(f.x1, f.y1, 6 + q * 18, 3 + q * 8, 0, 0, TAU); g.stroke();
          g.fillStyle = 'rgba(220, 230, 240, .5)';
          g.beginPath(); g.arc(f.x1, f.y1 - 8 - q * 16, 5 + q * 6, 0, TAU); g.fill();
        }
      }
    }
    g.globalAlpha = 1;
    // вінок щойно пішов на воду — кільця по воді біля кладки
    for (let i = st.launches.length - 1; i >= 0; i--) {
      const l = st.launches[i], q = (now - l.at) / SPLASH_MS;
      if (q > 1) { st.launches.splice(i, 1); continue; }
      const s = st.spots[l.k];
      if (!s) continue;
      g.globalAlpha = 1 - q;
      g.strokeStyle = '#ffe9a8';
      g.lineWidth = 2;
      g.beginPath(); g.ellipse(s.x * CELL + 16, WREATH_Y, 10 + q * 26, 5 + q * 10, 0, 0, TAU); g.stroke();
    }
    g.globalAlpha = 1;
  }

  function sparkle(g, x, y, pal, now) {
    g.strokeStyle = pal.accent;
    g.lineWidth = 2.5;
    g.setLineDash([5, 4]);
    g.lineDashOffset = -now / 60;
    g.beginPath(); g.ellipse(x, y + 5, 15, 7.5, 0, 0, TAU); g.stroke();
    g.setLineDash([]);
    g.lineDashOffset = 0;
    g.fillStyle = '#fff6c0';
    for (let s = 0; s < 4; s++) {
      const a = now / 300 + (s * TAU) / 4;
      g.beginPath(); g.arc(x + Math.cos(a) * 16, y - 10 + Math.sin(a) * 14, 1.6, 0, TAU); g.fill();
    }
  }

  /// Де тебе бачили востаннє: блідий пунктир і підпис. Не де ти є — цього у темряві не знає ніхто.
  function ghost(g, x, y, pal, now) {
    g.globalAlpha = 0.55 + 0.2 * Math.sin(now / 200);
    g.strokeStyle = pal.text;
    g.lineWidth = 1.5;
    g.setLineDash([3, 4]);
    g.beginPath(); g.ellipse(x, y + 5, 15, 7.5, 0, 0, TAU); g.stroke();
    g.setLineDash([]);
    g.font = '700 11px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'bottom';
    outlined(g, 'тут тебе бачили', x, y - 10, pal.text, pal.ink);
    g.globalAlpha = 1;
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

  function busyRing(g, x, y, pal, p) {
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
    g.fillText('🌼', cx, cy + 0.5);
  }

  function tipPlate(g, st, x, y, pal) {
    const px = Math.max(12, 13 * (st.cssK ? 1 / st.cssK : 1));
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const w = g.measureText(st.tip).width + px, h = px + 8;
    const vx = st.box[0], vw = st.box[2];
    const tx = clamp(x, vx + w / 2 + 4, vx + vw - w / 2 - 4), ty = Math.max(st.box[1] + h, y - 52);
    g.fillStyle = 'rgba(12, 16, 26, .9)';
    g.beginPath(); g.roundRect(tx - w / 2, ty - h / 2, w, h, h / 2); g.fill();
    g.strokeStyle = pal.danger;
    g.lineWidth = 1.5;
    g.stroke();
    g.fillStyle = pal.text;
    g.fillText(st.tip, tx, ty + 0.5);
  }

  function chevron(g, x, y, pal) {
    g.fillStyle = pal.danger;
    g.strokeStyle = pal.ink;
    g.lineWidth = 1.5;
    g.beginPath();
    g.moveTo(x - 6, y - 6); g.lineTo(x + 6, y - 6); g.lineTo(x, y + 1); g.closePath();
    g.fill(); g.stroke();
  }

  /// Ніки: над вибитими — коли їх видно, над усіма гравцями — на розкритті (світанок: видно всіх).
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
    for (const r of rows) {
      const id = r.id;
      if (id < 0 || id >= n || !st.pa[id]) continue;
      g.globalAlpha = st.pa[id];
      g.strokeStyle = pal.seats[r.seat] || pal.text;
      g.beginPath(); g.ellipse(st.px[id], st.py[id] + 5, 14, 7, 0, 0, TAU); g.stroke();
    }
    const px = st.mode === 'port' ? 15 : 13, h = px + 5;
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const lab = st.lab, pool = st.labPool || (st.labPool = []);
    lab.length = 0;
    for (const r of rows) {
      const id = r.id;
      if (id < 0 || id >= n || !st.pa[id]) continue;
      const a = pool[lab.length] || (pool[lab.length] = { seat: 0, text: '', x: 0, y: 0, w: 0, al: 1 });
      a.seat = r.seat;
      a.text = (win.includes(r.seat) ? mark : '') + nickOfSeat(st, r.seat);
      a.x = st.px[id];
      a.y = st.py[id] - (st.ps[id] >= 2 ? 16 : 34);
      a.w = g.measureText(a.text).width + 12;
      a.al = st.pa[id];
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
      g.globalAlpha = a.al;
      g.fillStyle = 'rgba(12, 16, 26, .84)';
      g.beginPath(); g.roundRect(a.x - a.w / 2, a.y - h / 2, a.w, h, h / 2); g.fill();
      g.fillStyle = pal.seats[a.seat] || pal.text;
      g.fillText(a.text, a.x, a.y + 0.5);
    }
    g.globalAlpha = 1;
  }

  function shade(st, g, pal, w, h, phase, playing, mine, now) {
    const v = st.view;
    if (!v) return;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const room = st.ctx && st.ctx.room;
    if (!playing && room && room.status === 'lobby') {
      g.fillStyle = 'rgba(6, 10, 26, .35)';
      g.fillRect(0, 0, w, h);
      if (st.mode === 'port') {
        fitFont(g, 'щойно господар натисне «Почати»', w * 0.9, 22, 700);
        outlined(g, 'Сонце сяде,', w / 2, h / 2 - 14, pal.text, pal.ink);
        outlined(g, 'щойно господар натисне «Почати»', w / 2, h / 2 + 14, pal.text, pal.ink);
      } else {
        const msg = 'Сонце сяде, щойно господар натисне «Почати»';
        fitFont(g, msg, w * 0.9, Math.round(w / 34), 700);
        outlined(g, msg, w / 2, h / 2, pal.text, pal.ink);
      }
      return;
    }
    const minPx = 14 / (st.cssK || 1);
    if (phase === 'start') {
      const left = st.last ? st.last.left : v.left;
      g.font = '800 ' + Math.round(h / 5) + 'px system-ui, sans-serif';
      outlined(g, String(Math.max(1, Math.ceil((left * TICK_MS) / 1000))), w / 2, h / 2, pal.text, pal.ink);
      const sub = mine ? 'Запам\'ятай, де ти, — зараз стемніє' : 'Сонце сідає…';
      const px = Math.round(Math.max(h / 26, minPx));
      fitFont(g, sub, w * 0.92, px, 700);
      outlined(g, sub, w / 2, h / 2 + h / 7, pal.text, pal.ink);
      if (mine && st.me) {
        const list = (st.me.list || []).map((k2) => (st.spots[k2] || {}).emoji || '').join(' ');
        const how = st.mode === 'port' ? 'Пусти вінки: ' + list : 'Пусти вінки з обведених кладок ' + list + ' · ✋ — вистежуй';
        fitFont(g, how, w * 0.92, Math.round(px * 0.92), 600);
        outlined(g, how, w / 2, h / 2 + h / 7 + px * 1.5, pal.accent, pal.ink);
      }
      return;
    }
    if (phase === 'reveal' || phase === 'over' || (!playing && v.phase === 'over')) {
      const title = phase === 'reveal' ? revealTitle(st, st.mode === 'port') : overTitle(st);
      const port = st.mode === 'port', left = port ? w - 108 : w, mid = left / 2;
      fitFont(g, title, left - (port ? 24 : 40), Math.round(Math.max(minPx, h / 22)), 800);
      const tw = g.measureText(title).width + 28;
      const th = Math.max(24, h / 13), ty = port ? 8 : Math.round(h * 0.4 - th / 2);
      g.fillStyle = 'rgba(10, 14, 26, .78)';
      g.beginPath(); g.roundRect(mid - tw / 2, ty, tw, th, 10); g.fill();
      g.fillStyle = pal.text;
      g.fillText(title, mid, ty + th / 2);
    }
  }

  function revealTitle(st, short) {
    const v = st.view, r = v && v.reveal;
    if (!r) return '';
    const head = short ? '' : 'Раунд ' + v.round + ' з ' + v.of + ' · ';
    const who = (r.winners || []).map((s) => nickOfSeat(st, s)).join(' і ');
    switch (r.why) {
      case 'list': return head + '🌼 Усі три вінки на воді — ' + who;
      case 'last': return head + '✋ На ногах лише ' + who;
      case 'time': return head + '🌅 Світає — найбільше вінків у ' + who;
      default: return head + '🌅 Світає — ніхто не взяв раунду';
    }
  }

  function overTitle(st) {
    const v = st.view, res = v && v.result;
    if (!res) return 'Ніч минула';
    if (res.why === 'left') {
      return res.winners.length ? '🚪 Суперники розійшлись — перемога: ' + res.winners.map((s) => nickOfSeat(st, s)).join(', ')
        : '🚪 Усі розійшлись';
    }
    if (!res.winners.length) return '🤝 Нічия';
    return '🏆 Перемога: ' + res.winners.map((s) => nickOfSeat(st, s) + ' ' + (res.totals[s] | 0)).join(', ');
  }

  function minimap(st, g, pal, w, cx, cy, vw, vh, now, showMe) {
    if (!st.mini) return;
    const mw = 96, mh = 64, x0 = w - mw - 6, y0 = 6;
    g.globalAlpha = 0.92;
    g.drawImage(st.mini, x0, y0, mw, mh);
    g.globalAlpha = 1;
    g.strokeStyle = 'rgba(0, 0, 0, .6)';
    g.lineWidth = 1;
    g.strokeRect(x0 - 0.5, y0 - 0.5, mw + 1, mh + 1);
    const sx = mw / WW, sy = mh / WH;
    const l = (st.last && st.last.l) || [];
    for (let i = 0; i < l.length; i += 4) {
      g.fillStyle = l[i] === 4 ? '#ff4f8a' : l[i] === 1 ? '#d8ff8a' : '#ffb24a';
      g.beginPath(); g.arc(x0 + l[i + 1] * sx, y0 + l[i + 2] * sy, l[i] === 0 ? 3 : 1.6, 0, TAU); g.fill();
    }
    g.strokeStyle = '#fff';
    g.lineWidth = 1.2;
    g.strokeRect(x0 + cx * sx, y0 + cy * sy, vw * sx, vh * sy);
    if (showMe) {
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
    // у світлі я чи в темряві — це бачу лише я (і лише про себе)
    const lamp = st.lampEl;
    if (lamp) {
      const on = ph === 'go' && st.ctx && st.ctx.mine && alive(st);
      const txt = !on ? '' : meLit(st) ? '🔥 тебе видно' : '🌑 ти в темряві';
      if (lamp.textContent !== txt) { lamp.textContent = txt; lamp.hidden = !txt; lamp.classList.toggle('lit', meLit(st)); }
    }
  }

  function hud(st) {
    const ctx = st.ctx, v = st.view, el = st.hudEl;
    if (!el || !v) return;
    const me = v.me;
    let html = '<span class="kupala-chip">Раунд ' + Math.max(1, v.round | 0) + '/' + (v.of | 0) + '</span>';
    if (me) {
      html += '<button type="button" class="kupala-chip kupala-torch" data-pad-skip title="Головешки: R — шпурнути вперед">';
      for (let i = 0; i < 2; i++) html += '<i' + (i < me.torches ? '' : ' class="used"') + '>🔥</i>';
      html += '</button><span class="kupala-chip kupala-list" title="Твої кладки: пусти з кожної вінок"><small>Вінки:</small>';
      me.list.forEach((k, i) => {
        const s = st.spots[k] || {};
        html += '<i' + (me.done[i] ? ' class="done"' : '') + ' title="' + ctx.esc(s.where || '') + '">' + (s.emoji || '?') + ' ' + ctx.esc(s.name || '') + (me.done[i] ? ' ✓' : '') + '</i>';
      });
      html += '</span>';
    }
    const seats = v.seats || [];
    const active = seats.filter((s) => !s.out);
    if (v.phase !== 'lobby') html += '<span class="kupala-chip">на ногах ' + active.filter((s) => s.alive).length + '/' + active.length + '</span>';
    html += '<button type="button" class="kupala-mute" data-pad-skip title="Звук">' + (st.mute ? '🔇' : '🔊') + '</button>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.querySelector('.kupala-chips').innerHTML = html;
    }
    let row = '';
    for (const s of seats) {
      row += '<span class="kupala-seat kupala-s' + s.seat + (s.alive ? '' : ' dead') + (s.out ? ' out' : '') + (s.seat === ctx.seat ? ' me' : '') + '">'
        + '<i></i>' + ctx.esc(s.nick) + ' <b>' + (s.total | 0) + '</b>' + (s.wreaths != null ? ' <small>🌼' + (s.wreaths | 0) + '</small>' : '') + '</span>';
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
      html = rows.map((x) => '<span class="kupala-sc kupala-s' + x.seat + (champ.includes(x.seat) ? ' win' : '') + '">'
        + (champ.includes(x.seat) ? (over ? '🏆 ' : '⭐ ') : '') + '<b>' + st.ctx.esc(nickOfSeat(st, x.seat)) + '</b> '
        + '<em>' + total(x.seat) + '</em> <small title="за раунд: вінки зі списку, вибиті гравці, папороть">+' + x.pts
        + ' · 🌼' + x.wreaths + (x.kills ? ' · ✋' + x.kills : '') + (x.fern ? ' · 🌺' : '') + '</small></span>').join('');
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      el.hidden = !html;
    }
    if (st.stageEl) st.stageEl.classList.toggle('kupala-open', open);
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

  /// Спільні відмови клієнта, щоб не смикати сервер зайвий раз (він однаково перевірить сам).
  function busyNow(st, now) {
    const me = st.me;
    if (me && !me.alive) { refuse(st, 'Тебе вже вибили — дивись, хто кого'); return true; }
    if (now < st.stunUntil) { refuse(st, 'Ти ще отетерілий — постій'); return true; }
    return false;
  }

  function slap(st, id) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    if (busyNow(st, now)) return;
    if (now - st.slapAt < SLAP_COOL_MS) { refuse(st, 'Рука ще не відійшла'); return; }
    ctx.act('slap', id == null ? {} : { id }).then((r) => {
      if (r && r.ok) st.slapAt = performance.now();
      else if (r) refuse(st, r.message, true);
    });
  }

  function torch(st) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    if (busyNow(st, now)) return;
    if (st.me && st.me.torches <= 0) { refuse(st, 'Головешки скінчились'); return; }
    if (now - st.slapAt < SLAP_COOL_MS) { refuse(st, 'Рука ще не відійшла'); return; }
    ctx.act('torch', {}).then((r) => {
      if (r && r.ok) st.slapAt = performance.now();
      else if (r) refuse(st, r.message, true);
    });
  }

  function launch(st, spot) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    if (now < st.busyUntil) return;
    if (busyNow(st, now)) return;
    ctx.act('launch', spot == null ? {} : { spot }).then((r) => {
      if (r && r.ok) { st.launchAt = performance.now(); st.busyUntil = st.launchAt + LAUNCH_MS; }
      else if (r) refuse(st, r.message, true);
    });
  }

  function peek(st) { st.peekUntil = performance.now() + PEEK_MS; if (!meLit(st) && phaseOf(st) === 'go') refuse(st, 'Ти в темряві — пам\'ятай, куди йшов', true); }

  function toWorld(st, e) {
    const r = st.cv.el.getBoundingClientRect();
    const [cx, cy, vw, vh] = st.box;
    return [cx + ((e.clientX - r.left) / r.width) * vw, cy + ((e.clientY - r.top) / r.height) * vh, r];
  }

  function pickVillager(st, x, y, reach) {
    const n = st.last ? Math.min(st.last.n, st.cap) : 0;
    let best = -1, bestD = reach * reach;
    for (let i = 0; i < n; i++) {
      if (i === st.meId || !st.pres[i] || st.ps[i] >= 2) continue;
      const dx = st.px[i] - x, dy = st.py[i] - 7 - y, d2 = dx * dx + dy * dy;
      if (d2 <= bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  /// Кладка під курсором: сама кладка й дошки у воду.
  function pickSpot(st, x, y) {
    for (const s of st.spots) if (x >= s.x * CELL - 4 && x <= s.x * CELL + CELL + 4 && y >= s.y * CELL - 4 && y <= s.y * CELL + 2 * CELL) return s.i;
    return -1;
  }

  /// Клік чи тап: по дошках кладки над водою — завжди вінок (там людей нема), по самій кладці — вінок, якщо я на ній
  /// стою (там стоять і боти, і ляпас замість вінка видав би мене), інакше — ляпас тому, хто під курсором.
  function clickAt(st, x, y, reach) {
    const k = pickSpot(st, x, y);
    if (k >= 0 && (y > st.spots[k].y * CELL + CELL || k === myspot(st))) return { spot: k, id: -1 };
    const id = pickVillager(st, x, y, reach);
    if (id >= 0) return { spot: -1, id };
    return { spot: k, id: -1 };
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
      const d = st.down;
      if (d && d.id === e.pointerId) {
        const dx = e.clientX - d.x, dy = e.clientY - d.y;
        if (Math.abs(dx) + Math.abs(dy) > 6) d.moved = true;
        // камеру тягнуть усі: у темряві своя камера стоїть там, де тебе бачили востаннє
        if (st.mode === 'port' && d.moved) {
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
      let on = hit.spot >= 0;
      if (hit.id >= 0) {
        const me = st.meId;
        if (meLit(st)) { const dx = st.px[hit.id] - st.px[me], dy = st.py[hit.id] - st.py[me]; on = dx * dx + dy * dy <= SLAP_MAX * SLAP_MAX; }
        else on = true;
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
      if (hit.spot >= 0) launch(st, hit.spot);
      else if (hit.id >= 0) slap(st, hit.id);
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
    el.className = 'kupala-pad';
    const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
    el.innerHTML = '<div class="kupala-dirs" role="group" aria-label="хрестовина: тримай і веди пальцем">'
      + [3, 2, 0, 1].map((d) => '<span class="kupala-arr" data-dir="' + d + '">' + label[d] + '</span>').join('')
      + '<button type="button" class="kupala-peek" data-act="peek" aria-label="де я">👁</button>'
      + '</div><div class="kupala-acts">'
      + '<button type="button" data-act="slap" aria-label="ляпас">✋</button>'
      + '<button type="button" data-act="launch" aria-label="пустити вінок">🌼</button>'
      + '<button type="button" data-act="torch" aria-label="головешка">🔥</button></div>';
    const dirs = el.querySelector('.kupala-dirs');
    const arrows = dirs.querySelectorAll('.kupala-arr');
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
        if (b.dataset.act === 'slap') return slap(st, null);
        if (b.dataset.act === 'launch') return launch(st, null);
        if (b.dataset.act === 'torch') return torch(st);
        if (b.dataset.act === 'peek') return peek(st);
        return;
      }
      if (!e.target.closest('.kupala-dirs')) return;
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

  /// Мапа 3:2 має влізти у вікно разом зі статусом і кнопками під нею (як у Юрми): не вужче 480.
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

  function fit(root, st) {
    padStrip(st);
    const cw = root.clientWidth;
    let mode = st.mode;
    if (cw) mode = cw < 600 ? 'port' : cw > 640 ? 'full' : st.mode;
    sizeStage(st, mode);
    if (!st.cv || mode !== st.mode) {
      st.mode = mode;
      st.cv = HGames.ui.canvas(st.stageEl, mode === 'port' ? { w: PW, h: PH, cls: 'kupala-board kupala-port' } : { w: WW, h: WH, cls: 'kupala-board' });
      st.stageEl.classList.toggle('kupala-portmode', mode === 'port');
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

  function spin(st) {
    if (st.raf) return;
    const loop = (now) => {
      if (!st.cv || !st.cv.el.isConnected) { st.raf = 0; return; }
      if (now - st.padAt > 1000) { st.padAt = now; if (padStrip(st)) fit(st.root, st); }
      if (!document.hidden && st.visible) { draw(st); paintClock(st, st.last); }
      st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  HGames.register({
    id: 'kupala',
    icon: ICON,
    seatNames: SEAT_NAMES,
    seatClass: ['x', 'o', 'c', 'd', 'kupala-b', 'kupala-p', 'kupala-v', 'kupala-r'],
    pad: {
      dirs: true,
      a: 'Space',
      x: 'KeyE',
      on(btn, ctx) {
        const st = ctx && ctx._kupala;
        if (btn === 'lb' || btn === 'lt') { if (st) peek(st); return true; }
        if (btn === 'rb' || btn === 'rt') { if (st) torch(st); return true; }
        return false;
      },
      hint: '{dpad} іти · {a} ляпас · {x} вінок · {rb} головешка · {lb} де я?',
    },
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Купальська ніч',
      items: [
        '🔥 Ніч на Івана Купала: біля вогнищ світло, а в темряві не видно нікого — навіть себе',
        '🌼 Пусти три вінки на воду з кладок у своєму списку: стань на кладку й натисни E (Ⓧ) — вінок попливе для всіх',
        '✋ Пробіл (Ⓐ) — ляпас тому, хто перед тобою. Влучив у бота — отетерів, і всі чули, де ляснуло',
        '🔥 Дві головешки: R (RB) — шпурнути вперед і освітити місце на 6 с. Звідки летіла — бачать усі',
        '⚡ Зарниця на пів секунди показує всіх, а в лісі десь зацвіте папороть — +2 тому, хто зірве',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      root.classList.add('kupala-body');
      st.hudEl = document.createElement('div');
      st.hudEl.className = 'kupala-hud';
      st.hudEl.innerHTML = '<span class="kupala-chip kupala-clock">⏱ —</span><span class="kupala-chip kupala-lamp" hidden></span><span class="kupala-chips"></span>';
      st.clockEl = st.hudEl.querySelector('.kupala-clock');
      st.lampEl = st.hudEl.querySelector('.kupala-lamp');
      st.seatsEl = document.createElement('div');
      st.seatsEl.className = 'kupala-seats';
      st.hudEl.appendChild(st.seatsEl);
      st.hudEl.addEventListener('click', (e) => {
        if (e.target.closest('.kupala-torch')) { unlock(st); torch(st); return; }
        if (!e.target.closest('.kupala-mute')) return;
        st.mute = !st.mute;
        try { localStorage.setItem('kupalaMute', st.mute ? '1' : ''); } catch { /* приватне вікно */ }
        if (!st.mute) unlock(st);
        hud(st);
      });
      st.newsEl = document.createElement('div');
      st.newsEl.className = 'kupala-news';
      st.newsEl.setAttribute('aria-live', 'polite');
      st.sumEl = document.createElement('div');
      st.sumEl.className = 'kupala-sum';
      st.sumEl.hidden = true;
      st.stageEl = document.createElement('div');
      st.stageEl.className = 'kupala-stage';
      st.stageEl.appendChild(st.newsEl);
      root.append(st.hudEl, st.stageEl, st.sumEl);
      st.cv = HGames.ui.canvas(st.stageEl, { w: WW, h: WH, cls: 'kupala-board' });
      wireCanvas(st);
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => fit(root, st));
        st.ro.observe(root);
      }
      st.onResize = () => fit(root, st);
      window.addEventListener('resize', st.onResize);
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
      if (st.padEl) {
        const ph = st.vphase, off = !(alive(st) && (ph === 'start' || ph === 'go'));
        if (st.padEl.classList.contains('kupala-off') !== off) st.padEl.classList.toggle('kupala-off', off);
        if (off && st.touchDir >= 0) { st.touchDir = -1; st.touchPid = null; want(st); }
      }
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
      const st = ctx._kupala;
      if (!st || !ctx.mine || !ctx.playing) return false;
      unlock(st);
      const d = dirOf(e);
      if (d !== undefined) {
        if (!st.keys.includes(d)) st.keys.push(d);
        else if (st.keys[st.keys.length - 1] !== d) { st.keys.splice(st.keys.indexOf(d), 1); st.keys.push(d); }
        want(st);
        return true;
      }
      if (isSlap(e)) { if (!e.repeat) slap(st, null); return true; }
      if (isLaunch(e)) { if (!e.repeat) launch(st, null); return true; }
      if (isTorch(e)) { if (!e.repeat) torch(st); return true; }
      if (isPeek(e)) { if (!e.repeat) peek(st); return true; }
      return false;
    },

    status(ctx) {
      const st = ctx._kupala;
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f) return '';
      const ph = f.ph || f.phase;
      if (ph === 'start') return ctx.mine ? 'Роздивись, поки не стемніло: ти — під стрілкою' : 'Сонце сідає…';
      if (ph === 'reveal') {
        const v = st && st.view, s = Math.max(1, Math.ceil(((f.left | 0) * TICK_MS) / 1000));
        return v && v.round >= v.of ? 'Підсумок ночі за ' + s + ' с' : 'Наступна ніч за ' + s + ' с';
      }
      if (ph !== 'go') return '';
      if (!ctx.mine) return HGames.ui.coarse() ? 'Вгадуй, хто з селян живий · тягни мапу пальцем' : 'Вгадуй разом із гравцями, хто з селян живий';
      if (st && st.me && !st.me.alive) return 'Тебе вибили — дивись, хто кого';
      const now = performance.now();
      if (st && now < st.busyUntil) return '🌼 Пускаєш вінок… стій, не тікай';
      if (st && now < st.stunUntil) return '💫 Отетерів — ще мить постій';
      if (window.HPad && window.HPad.on) return 'Стік — іти · Ⓐ ляпас · Ⓧ вінок · RB головешка · LB де я? Вінок пускають, стоячи на кладці';
      return HGames.ui.coarse()
        ? 'Хрестовина — іти · ✋ ляпас · 🌼 вінок · 🔥 головешка · 👁 де я?'
        : 'Стрілки/WASD — іти · пробіл — ляпас · E — вінок · R — головешка · Q — де я? · клік по селянину чи кладці';
    },

    unmount(root) {
      const st = root._kupala;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (st.ro) st.ro.disconnect();
      if (st.io) st.io.disconnect();
      if (st.audio) { try { st.audio.close(); } catch { /* уже закритий */ } }
      root._kupala = null;
    },
  });
})();
