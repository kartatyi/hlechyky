/*
  Замри! (freeze) — «червоне світло — зелене світло» по-сільськи. Довгий луг: зліва тин і старт, справа хата Баби
  Параски з глеком. Реалтайм 25 Гц: сервер тикає раз на 40 мс і шле кадр, ми згладжуємо його й малюємо. Усіх селян — і
  ботів, і гравців, і себе — малюємо однаково, з кадрів через інтерполяцію: так свого не видасть ні лаг, ні «підсмикування».
  Бабу ж малюємо з НАЙСВІЖІШОГО кадру, без інтерполяції: «Замри!» мусить з'явитися на екрані якомога раніше.

  Кадр (Impl/Freeze.cs): { t, ph: 'start'|'go'|'reveal'|'over', left, b: 0 співає|1 обертається|2 дивиться|3 озирається,
    bl: скільки ще дивитиметься (лише коли b 1/2), v: [x, y, d, s] × N, ev: [[1, a, b, упав] | [2, id] | [3, id]] }
    s: 0 стоїть, 1 іде, 2 лежить, 3 впійманий, 4 отетерів; d: 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору.
  Вид (Hidden, свій кожному): { phase, round, of, left, t, w, h, finish, n, looks, names, v, b, bl, seats, me, reveal, result }
    me — лише своєму місцю: { id, cool, caught }.

  Ввід: Input('move', { dir }) лише на зміну (-1 — відпустив); Act('push', {} | { id }).
*/
(() => {
  const TICK_MS = 40, WW = 1200, WH = 360, FINISH_X = 1080, START_X = 72, MIN_Y = 100, MAX_Y = 340;
  const PUSH_RANGE = 32, PUSH_MAX = 44, PUSH_COOL_MS = 3000, GRACE = 11, ROUND_TICKS = 2250;
  // в'юпорт телефона: 480 одиниць лугу завширшки, уся висота — луг тягнеться вбік, за своїм селянином
  const PW = 480, PH = 360;
  const DX = [1, 0, -1, 0], DY = [0, 1, 0, -1];
  const PEEK_MS = 1500, NEWS_MS = 6000, LOCAL_MS = 250, HOLD_MS = 1000, TIP_MS = 1800, F5_PEEK_MS = 3000;
  // стіл стоїть (лобі, партію зіграно): стільки ще малюємо після останньої події — довше за найдовшу анімацію
  const IDLE_MS = 2000;
  const BANNER_MS = 900, FINGER_MS = 1100, POOF_MS = 450, BUMP_MS = 380, NOTE_MS = 330;
  const JUG_X = 1104, JUG_Y = 232, BABA_X = 1152, BABA_Y = 262;
  const TAU = Math.PI * 2;

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M6 1.8h4v1.6l1.9 2.7v5.6a2.3 2.3 0 0 1-2.3 2.3H6.4a2.3 2.3 0 0 1-2.3-2.3V6.1L6 3.4z" fill="var(--clay)"/>'
    + '<path d="M11.9 7.2c1.6 0 1.8 2.8 0 3" fill="none" stroke="var(--clay)" stroke-width="1.2"/>'
    + '<path d="M8 6.8v5M5.9 8.1l4.2 2.4M10.1 8.1l-4.2 2.4" stroke="var(--text)" stroke-width="1.1" stroke-linecap="round"/></svg>';

  const SEAT_NAMES = ['жовтий', 'зелений', 'рудий', 'сірий', 'синій', 'рожевий', 'фіолетовий', 'червоний'];
  const SEAT_VARS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'],
    ['--freeze-blue', '#6fb3e8'], ['--freeze-pink', '#e88ac0'], ['--freeze-violet', '#b48ae8'], ['--freeze-red', '#e86a6a']];
  // одяг — палітра села, не тема: червоний, синій, зелений, жовтий, білий, фіолетовий, помаранчевий, чорний
  const CLOTH = ['#c0392b', '#2f6db5', '#3f9142', '#e8c33a', '#f2efe6', '#7d4fa8', '#e07b2a', '#2a2a2a'];
  const HAIR = ['#2b1d14', '#4a2f1d', '#7a4b27', '#c9a15a', '#8c4a2f', '#3b2a20', '#6b4a2e', '#1a1410'];
  const SKIN = ['#f3d0b0', '#e2b48c', '#c48a5e', '#8d5a3b'];
  const FEMALE = new Set(['Параска', 'Ганна', 'Одарка', 'Марічка', 'Оксана', 'Домаха', 'Соломія', 'Мотря', 'Ярина', 'Христя',
    'Марта', 'Устя', 'Килина', 'Наталка', 'Пріська', 'Гафія', 'Софійка', 'Орися', 'Настя', 'Явдоха', 'Меланка', 'Феся', 'Текля',
    'Зоряна', 'Уляна', 'Люба', 'Дарина', 'Олеся']);
  // що кричить Баба, коли когось упіймала (за ім'ям селянина — однаково, бот це чи гравець)
  const CATCH_SAY = ['Бачу, {n}! Назад!', 'Ага, {n}! Ворухнувся!', '{n}, геть на початок!', 'Я все бачу, {n}!'];
  const SONG = [392, 440, 494, 440, 392, 330, 392, 440, 494, 523, 494, 440];

  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  const KEY_DIRS = { d: 0, 'в': 0, s: 1, 'і': 1, 'ы': 1, a: 2, 'ф': 2, w: 3, 'ц': 3 };
  const dirOf = (e) => {
    const d = DIRS[e.code];
    return d !== undefined ? d : KEY_DIRS[String(e.key || '').toLowerCase()];
  };
  const isPush = (e) => e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar';
  const isPeek = (e) => e.code === 'KeyQ' || e.code === 'Tab' || (!e.code && ['q', 'й'].includes(String(e.key || '').toLowerCase()));

  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const readMute = () => { try { return localStorage.getItem('freezeMute') === '1'; } catch { return false; } };

  // ---------------------------------------------------------------------------------------------
  // Палітра і статика: небо, тин, луг, стежка, фінішна смуга, хата — один раз в offscreen-канвас
  // ---------------------------------------------------------------------------------------------

  function palette() {
    const c = HGames.ui.css;
    return {
      grass: c('--freeze-grass', '#78a552'),
      grass2: c('--freeze-grass2', '#669443'),
      sky: c('--freeze-sky', '#bcd9ea'),
      wood: c('--freeze-wood', '#8a5a33'),
      cold: c('--freeze-cold', 'rgba(110, 170, 255, .30)'),
      shadow: 'rgba(20, 30, 15, .30)',
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

  function drawStatic(pal, S) {
    const c = document.createElement('canvas');
    c.width = Math.round(WW * S);
    c.height = Math.round(WH * S);
    const g = c.getContext('2d');
    g.scale(S, S);
    const rnd = lcg(2709);

    // небо й далекий гай
    const sky = g.createLinearGradient(0, 0, 0, 70);
    sky.addColorStop(0, pal.sky);
    sky.addColorStop(1, '#e6efe0');
    g.fillStyle = sky;
    g.fillRect(0, 0, WW, 70);
    g.fillStyle = '#5d8a4a';
    g.beginPath();
    g.moveTo(0, 60);
    for (let x = 0; x <= WW; x += 30) g.lineTo(x, 44 + Math.sin(x / 70) * 6 + rnd() * 8);
    g.lineTo(WW, 72); g.lineTo(0, 72); g.closePath(); g.fill();
    for (let i = 0; i < 16; i++) {
      const x = rnd() * 1060, y = 44 + rnd() * 10;
      g.fillStyle = i % 2 ? '#3f6f35' : '#4c7d3d';
      g.beginPath(); g.arc(x, y, 9 + rnd() * 8, 0, TAU); g.fill();
    }
    // хмарки
    g.fillStyle = 'rgba(255, 255, 255, .8)';
    for (const [x, y] of [[180, 16], [520, 12], [860, 20]]) {
      g.beginPath(); g.ellipse(x, y, 26, 7, 0, 0, TAU); g.ellipse(x + 18, y - 4, 16, 7, 0, 0, TAU); g.fill();
    }

    // луг
    g.fillStyle = pal.grass;
    g.fillRect(0, 70, WW, WH - 70);
    g.fillStyle = pal.grass2;
    for (let i = 0; i < 900; i++) {
      g.beginPath();
      g.ellipse(rnd() * WW, 72 + rnd() * (WH - 72), 3 + rnd() * 8, 2 + rnd() * 3, rnd() * 3, 0, TAU);
      g.fill();
    }
    g.strokeStyle = 'rgba(255, 255, 220, .16)';
    g.lineWidth = 1;
    g.beginPath();
    for (let i = 0; i < 520; i++) {
      const x = rnd() * WW, y = 80 + rnd() * (WH - 80);
      g.moveTo(x, y); g.lineTo(x - 1.5, y - 4);
      g.moveTo(x + 2, y); g.lineTo(x + 3, y - 3.5);
    }
    g.stroke();
    // ромашки й маки на лугу
    for (let i = 0; i < 160; i++) {
      const x = 90 + rnd() * 980, y = 96 + rnd() * 256;
      g.fillStyle = rnd() < 0.8 ? '#fbfaf2' : '#d9412f';
      g.beginPath(); g.arc(x, y, 1.6, 0, TAU); g.fill();
      g.fillStyle = '#f2c230';
      g.beginPath(); g.arc(x, y, 0.6, 0, TAU); g.fill();
    }
    // утоптана стежечка вздовж лугу — туди всі й ідуть
    g.fillStyle = 'rgba(201, 171, 122, .35)';
    g.beginPath(); g.ellipse(560, 224, 520, 34, 0, 0, TAU); g.fill();

    // тин угорі: кілки й плетиво, за ним соняхи й мальви
    for (let x = 20; x < 1100; x += 46) {
      g.strokeStyle = '#3d6b2e'; g.lineWidth = 2;
      g.beginPath(); g.moveTo(x + 10, 84); g.lineTo(x + 12, 46); g.stroke();
      g.fillStyle = '#f2b928';
      g.beginPath(); g.arc(x + 12, 44, 7, 0, TAU); g.fill();
      g.fillStyle = '#5a3a1a';
      g.beginPath(); g.arc(x + 12, 44, 3, 0, TAU); g.fill();
    }
    wattleH(g, 0, 70, 1100, 20, pal);

    // тин ліворуч — звідти старт
    wattleV(g, 0, 70, 22, WH - 70, pal);
    g.strokeStyle = 'rgba(255, 255, 255, .55)';
    g.setLineDash([6, 6]);
    g.lineWidth = 2;
    g.beginPath(); g.moveTo(START_X + 10, 96); g.lineTo(START_X + 10, WH - 6); g.stroke();
    g.setLineDash([]);

    // фінішна смуга: біла крейда й ромашки — поріг двору
    g.fillStyle = 'rgba(255, 255, 255, .55)';
    g.fillRect(FINISH_X - 2, 92, 4, WH - 92);
    for (let y = 98; y < WH; y += 14) {
      g.fillStyle = '#fbfaf2';
      for (let k = 0; k < 5; k++) { g.beginPath(); g.arc(FINISH_X + Math.cos(k * 1.26) * 3, y + Math.sin(k * 1.26) * 3, 1.8, 0, TAU); g.fill(); }
      g.fillStyle = '#f2c230';
      g.beginPath(); g.arc(FINISH_X, y, 1.6, 0, TAU); g.fill();
    }

    // двір: пісок, хата з солом'яною стріхою, лава, вишня
    g.fillStyle = '#cfb784';
    g.fillRect(FINISH_X + 4, 70, WW - FINISH_X - 4, WH - 70);
    g.fillStyle = 'rgba(120, 90, 50, .15)';
    for (let i = 0; i < 60; i++) { g.beginPath(); g.arc(FINISH_X + 8 + rnd() * 110, 80 + rnd() * 270, 1 + rnd() * 1.5, 0, TAU); g.fill(); }
    house(g, 1100, 8, pal);
    // лава під хатою
    g.fillStyle = '#7a4f2a';
    g.fillRect(1112, 176, 76, 6);
    g.fillRect(1116, 182, 4, 10); g.fillRect(1180, 182, 4, 10);
    // стільчик під глеком
    g.fillStyle = '#6b4424';
    g.fillRect(JUG_X - 12, JUG_Y + 2, 24, 5);
    g.fillRect(JUG_X - 10, JUG_Y + 7, 3, 10); g.fillRect(JUG_X + 7, JUG_Y + 7, 3, 10);
    // вишня в куточку
    g.fillStyle = 'rgba(0, 0, 0, .22)';
    g.beginPath(); g.ellipse(1176, 350, 24, 8, 0, 0, TAU); g.fill();
    g.fillStyle = '#6b4424';
    g.fillRect(1172, 316, 6, 34);
    g.fillStyle = '#3b7d33';
    g.beginPath(); g.arc(1175, 306, 20, 0, TAU); g.fill();
    g.fillStyle = '#b8202a';
    for (let i = 0; i < 9; i++) { g.beginPath(); g.arc(1162 + rnd() * 26, 294 + rnd() * 24, 2, 0, TAU); g.fill(); }
    return c;
  }

  function wattleH(g, x0, y0, w, h, pal) {
    g.fillStyle = 'rgba(0, 0, 0, .18)';
    g.fillRect(x0, y0 + h - 2, w, 5);
    g.fillStyle = pal.wood;
    for (let x = x0 + 6; x < x0 + w; x += 24) g.fillRect(x, y0 - 4, 4, h + 6);
    g.strokeStyle = '#a9794a';
    g.lineWidth = 2.2;
    for (let r = 0; r < 3; r++) {
      g.beginPath();
      for (let x = x0; x <= x0 + w; x += 12) g.lineTo(x, y0 + 4 + r * 5 + ((x / 12 + r) % 2 ? 1.5 : -1.5));
      g.stroke();
    }
  }

  function wattleV(g, x0, y0, w, h, pal) {
    g.fillStyle = 'rgba(0, 0, 0, .2)';
    g.fillRect(x0 + w, y0, 4, h);
    g.fillStyle = pal.wood;
    for (let y = y0 + 8; y < y0 + h; y += 26) g.fillRect(x0 + 3, y - 20, 4, 30);
    g.strokeStyle = '#a9794a';
    g.lineWidth = 2.2;
    for (let c = 0; c < 3; c++) {
      g.beginPath();
      for (let y = y0; y <= y0 + h; y += 12) g.lineTo(x0 + 6 + c * 5 + ((y / 12 + c) % 2 ? 1.5 : -1.5), y);
      g.stroke();
    }
  }

  function house(g, x0, y0, pal) {
    g.fillStyle = 'rgba(0, 0, 0, .25)';
    g.fillRect(x0 + 6, y0 + 150, 96, 12);
    g.fillStyle = '#f4f0e4';
    g.fillRect(x0 + 8, y0 + 56, 92, 104);
    g.strokeStyle = 'rgba(80, 60, 40, .35)';
    g.lineWidth = 1;
    g.strokeRect(x0 + 8.5, y0 + 56.5, 91, 103);
    // призьба синя
    g.fillStyle = '#4f7fb3';
    g.fillRect(x0 + 8, y0 + 150, 92, 10);
    // стріха
    g.fillStyle = '#c9a24a';
    g.beginPath(); g.moveTo(x0 - 4, y0 + 62); g.lineTo(x0 + 26, y0); g.lineTo(x0 + 92, y0); g.lineTo(x0 + 112, y0 + 62); g.closePath(); g.fill();
    g.strokeStyle = '#9d7a2c';
    g.beginPath();
    for (let x = x0 + 2; x < x0 + 110; x += 7) { g.moveTo(x, y0 + 60); g.lineTo(x + 8, y0 + 8); }
    g.stroke();
    // вікно з віконницями й двері
    g.fillStyle = '#7fb6d8';
    g.fillRect(x0 + 22, y0 + 80, 22, 22);
    g.strokeStyle = '#6b4424'; g.lineWidth = 2;
    g.strokeRect(x0 + 22, y0 + 80, 22, 22);
    g.beginPath(); g.moveTo(x0 + 33, y0 + 80); g.lineTo(x0 + 33, y0 + 102); g.moveTo(x0 + 22, y0 + 91); g.lineTo(x0 + 44, y0 + 91); g.stroke();
    g.fillStyle = '#3f6fa8';
    g.fillRect(x0 + 14, y0 + 80, 7, 22); g.fillRect(x0 + 45, y0 + 80, 7, 22);
    g.fillStyle = pal.wood;
    g.fillRect(x0 + 66, y0 + 92, 22, 58);
    g.fillStyle = '#e2b93b';
    g.beginPath(); g.arc(x0 + 84, y0 + 122, 1.8, 0, TAU); g.fill();
    // мальви під стіною
    for (let i = 0; i < 4; i++) {
      g.fillStyle = '#3d6b2e';
      g.fillRect(x0 + 10 + i * 5, y0 + 116, 1.5, 36);
      g.fillStyle = ['#e0508a', '#f08fb8', '#c02f5e', '#f4c542'][i];
      for (let k = 0; k < 3; k++) { g.beginPath(); g.arc(x0 + 11 + i * 5, y0 + 120 + k * 10, 2.6, 0, TAU); g.fill(); }
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Селяни й Баба
  // ---------------------------------------------------------------------------------------------

  /// Селянин у виді 3/4: ноги на ходу, сорочка з крайкою, руки, голова з обличчям у бік погляду і шапка. (x, y) — точка
  /// на землі між ногами. Без save/restore і без алокацій.
  function villager(g, x, y, d, s, look, now, id, ink) {
    if (s === 2) return lying(g, x, y, d, look, ink);
    const hat = look[0], hc = look[1], shirt = look[2], skin = look[3];
    const side = d === 0 ? 1 : d === 2 ? -1 : 0;
    let bob = 0, step = 0;
    if (s === 1) {
      const ph = Math.sin(now * 0.0503 + id * 1.7);
      bob = ph > 0 ? -1 : 0;
      step = ph > 0 ? 1 : -1;
    }
    if (s === 4) x += Math.sin(now / 90 + id) * 1.6;           // отетерів — хитається
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
    if (d === 1) {
      g.fillStyle = shirt === 0 ? '#f2efe6' : '#c0392b';
      g.fillRect(x - 1, y - 13, 2, 5);
    }
    g.fillStyle = skinC;
    g.beginPath();
    if (side) g.arc(x - side * 1 + side * 3 * step, y - 5, 2.2, 0, TAU);
    else { g.arc(x - 8, y - 6 + step, 2.2, 0, TAU); g.arc(x + 8, y - 6 - step, 2.2, 0, TAU); }
    g.fill();
    const hx = x + side * 1.5, hy = y - 19;
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

  /// Лежить після штурхана: навзнак упоперек, очі ✕✕, шапка відлетіла.
  function lying(g, x, y, d, look, ink) {
    const dir = d === 2 ? 1 : -1;       // штурхнули — падає назад
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

  /// Баба Параска біля глека: спиною до лугу (співає), озирається через плече, або лицем до лугу (дивиться) з рукою над
  /// очима. mode: 0 спиною, 3 озирається, 1/2 лицем. (x, y) — земля.
  function drawBaba(g, x, y, mode, now, pal, since, point) {
    const k = 1.55;
    const face = mode === 1 || mode === 2;
    const spin = mode === 1 && since < 180;                     // обертається — мить боком
    g.fillStyle = 'rgba(0, 0, 0, .28)';
    g.beginPath(); g.ellipse(x, y + 3, 16, 5, 0, 0, TAU); g.fill();
    const sway = mode === 0 ? Math.sin(now / 260) * 1.2 : 0;
    const bx = x + sway;
    // спідниця-плахта
    g.fillStyle = '#7b2a2a';
    g.beginPath(); g.moveTo(bx - 10 * k, y); g.lineTo(bx - 7 * k, y - 14 * k); g.lineTo(bx + 7 * k, y - 14 * k); g.lineTo(bx + 10 * k, y); g.closePath(); g.fill();
    g.fillStyle = '#2d5a3a';
    g.fillRect(bx - 9 * k, y - 5 * k, 18 * k, 2 * k);
    if (face && !spin) { g.fillStyle = '#f2efe6'; g.fillRect(bx - 4 * k, y - 14 * k, 8 * k, 12 * k); }    // фартух
    // сорочка
    g.fillStyle = '#f4f0e4';
    g.beginPath(); g.roundRect(bx - 7 * k, y - 25 * k, 14 * k, 12 * k, 4 * k); g.fill();
    g.strokeStyle = pal.ink; g.lineWidth = 1; g.stroke();
    g.fillStyle = '#c0392b';
    g.fillRect(bx - 7 * k, y - 21 * k, 2 * k, 5 * k); g.fillRect(bx + 5 * k, y - 21 * k, 2 * k, 5 * k);
    // руки: співає — руки в боки; дивиться — одна над очима, друга тицяє пальцем (коли когось упіймала)
    g.fillStyle = SKIN[1];
    g.beginPath();
    if (face && !spin) {
      g.arc(bx - 6 * k, y - 34 * k, 2.2 * k, 0, TAU);         // долоня над очима
      if (point) g.arc(bx - 12 * k, y - 22 * k, 2.2 * k, 0, TAU);
      else g.arc(bx + 8 * k, y - 16 * k, 2.2 * k, 0, TAU);
    } else { g.arc(bx - 8 * k, y - 17 * k, 2.2 * k, 0, TAU); g.arc(bx + 8 * k, y - 17 * k, 2.2 * k, 0, TAU); }
    g.fill();
    // голова в червоній хустці
    const hx = bx + (mode === 3 ? -1.5 * k : 0), hy = y - 30 * k;
    g.fillStyle = SKIN[1];
    g.beginPath(); g.arc(hx, hy, 6 * k, 0, TAU); g.fill();
    g.strokeStyle = pal.ink; g.lineWidth = 1; g.stroke();
    g.fillStyle = '#c62828';
    g.beginPath();
    if (mode === 0 || spin) g.arc(hx, hy, 6.8 * k, 0, TAU);                              // потилиця — уся в хустці
    else if (mode === 3) g.ellipse(hx + 1.6 * k, hy - 1.6 * k, 6.4 * k, 6.2 * k, 0, Math.PI * 0.95, Math.PI * 2.05);
    else g.arc(hx, hy - 0.4 * k, 6.9 * k, Math.PI * 1.02, Math.PI * 1.98);
    g.fill();
    g.fillStyle = 'rgba(255, 255, 255, .6)';
    g.fillRect(hx - 3 * k, hy - 4.5 * k, 1.4 * k, 1.4 * k); g.fillRect(hx + 1.5 * k, hy - 3 * k, 1.4 * k, 1.4 * k);
    g.fillStyle = pal.ink;
    if (mode === 3) {
      // через плече: одне око й ніс ліворуч
      g.fillRect(hx - 3 * k, hy, 1.6 * k, 1.8 * k);
      g.fillStyle = SKIN[1];
      g.beginPath(); g.arc(hx - 6 * k, hy + 1.4 * k, 1.5 * k, 0, TAU); g.fill();
    } else if (face && !spin) {
      // примружені очі — дивиться пильно
      g.fillRect(hx - 3 * k, hy + 0.4 * k, 2.2 * k, 1 * k);
      g.fillRect(hx + 0.8 * k, hy + 0.4 * k, 2.2 * k, 1 * k);
      g.fillStyle = '#9b3b3b';
      g.fillRect(hx - 1.5 * k, hy + 3.2 * k, 3 * k, 0.8 * k);
    }
    // співає — нотки летять
    if (mode === 0) {
      g.font = '600 13px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      for (let i = 0; i < 3; i++) {
        const p = ((now / 1400 + i / 3) % 1);
        g.globalAlpha = 1 - p;
        g.fillStyle = pal.ink;
        g.fillText(i % 2 ? '♫' : '♪', hx - 10 - p * 40 + Math.sin(p * 6 + i) * 4, hy - 10 - p * 30);
      }
      g.globalAlpha = 1;
    }
  }

  function jug(g, x, y, now, glow) {
    if (glow) {
      g.globalAlpha = 0.25 + 0.15 * Math.sin(now / 300);
      g.fillStyle = '#ffe28a';
      g.beginPath(); g.arc(x, y - 10, 20, 0, TAU); g.fill();
      g.globalAlpha = 1;
    }
    g.fillStyle = '#b8652f';
    g.beginPath(); g.ellipse(x, y - 8, 10, 11, 0, 0, TAU); g.fill();
    g.fillRect(x - 4, y - 24, 8, 8);
    g.beginPath(); g.ellipse(x, y - 24, 5.5, 2, 0, 0, TAU); g.fill();
    g.strokeStyle = '#8a4520';
    g.lineWidth = 2;
    g.beginPath(); g.arc(x + 9, y - 13, 4.5, -1.2, 1.3); g.stroke();
    g.strokeStyle = '#f2d8a0';
    g.lineWidth = 1.4;
    g.beginPath(); g.moveTo(x - 8, y - 9); g.quadraticCurveTo(x, y - 4, x + 8, y - 9); g.stroke();
    g.fillStyle = 'rgba(255, 255, 255, .35)';
    g.beginPath(); g.ellipse(x - 4, y - 12, 2, 4, 0.3, 0, TAU); g.fill();
  }

  // ---------------------------------------------------------------------------------------------
  // Стан модуля
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    let st = root._freeze;
    if (!st) {
      st = root._freeze = {
        root, ctx: null, cv: null, interp: HGames.ui.Interp(), mode: 'full', S: 1,
        view: null, n: 0, looks: [], names: [], meId: -1, me: null,
        round: -1, vphase: '', last: null, lastAt: 0, fph: '',
        b: 2, bl: 0, bAt: 0, turnAt: -1e9, singAt: 0, noteAt: 0, noteI: 0,
        px: new Float64Array(64), py: new Float64Array(64), pd: new Int8Array(64), ps: new Int8Array(64), order: [],
        stat: null, statKey: '', pal: null, palAt: -1e9,
        cam: { x: WW / 2 }, box: [0, 0, WW, WH], drag: null, hover: false, camRound: -1,
        keys: [], touchDir: -1, dir: -1, localDir: -1, localUntil: 0, peekUntil: 0, sentAt: 0,
        pushAt: -1e9, caughtN: 0, tip: null, tipUntil: 0, autoPeek: false,
        fingers: [], poofs: [], bumps: [], say: null, sayUntil: 0,
        raf: 0, keyup: null, blur: null, ro: null, io: null, onResize: null, visible: true,
        padOn: false, padH: 0, padAt: 0, lab: [], labPool: [],
        hudEl: null, clockEl: null, newsEl: null, sumEl: null, padEl: null, stageEl: null, seatsEl: null,
        audio: null, mute: readMute(),
        perf: { sum: 0, n: 0, max: 0 }, wakeAt: 0,
      };
    }
    st.ctx = ctx;
    ctx._freeze = st;
    return st;
  }

  const seatOf = (st, i) => (st.view && st.view.seats || []).find((s) => s.seat === i) || null;
  const nickOfSeat = (st, i) => { const s = seatOf(st, i); return (s && s.nick) || (st.ctx && st.ctx.nickOf(i)) || SEAT_NAMES[i] || '?'; };
  const nameOf = (st, id) => (st.names && st.names[id]) || 'селянин';
  const phaseOf = (st) => (st.vphase === 'over' ? 'over' : st.fph || st.vphase);
  const mineNow = (st) => !!(st.ctx && st.ctx.mine) && st.meId >= 0;
  const pushReady = (now, st) => now - st.pushAt >= PUSH_COOL_MS;

  function refuse(st, text, toasted) {
    if (!text) return;
    if (!toasted && st.ctx) st.ctx.toast(text, 'err');
    st.tip = text;
    st.tipUntil = performance.now() + TIP_MS;
    wake(st);
  }

  // ---------------------------------------------------------------------------------------------
  // Звуки (WebAudio, тихо, лише після першого жесту): пісня Баби, «Замри!», штурхан, «ой», глек
  // ---------------------------------------------------------------------------------------------

  function unlock(st) {
    if (st.audio || st.mute) return;
    try {
      const A = window.AudioContext || window.webkitAudioContext;
      if (A) st.audio = new A();
    } catch { st.audio = null; }
  }

  function sfx(st, kind, arg) {
    const A = st.audio;
    if (!A || st.mute || document.hidden) return;
    try {
      const t = A.currentTime;
      const tone = (f0, f1, dur, vol, type, at) => {
        const o = A.createOscillator();
        const t0 = t + (at || 0);
        o.type = type || 'sine';
        o.frequency.setValueAtTime(f0, t0);
        if (f1 !== f0) o.frequency.exponentialRampToValueAtTime(f1, t0 + dur);
        const gn = A.createGain();
        gn.gain.setValueAtTime(vol, t0);
        gn.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
        o.connect(gn); gn.connect(A.destination);
        o.start(t0); o.stop(t0 + dur + 0.02);
      };
      if (kind === 'note') tone(arg, arg, 0.28, 0.018, 'triangle');
      else if (kind === 'freeze') { tone(988, 988, 0.12, 0.05, 'square'); tone(659, 494, 0.22, 0.05, 'square', 0.12); }
      else if (kind === 'caught') tone(620, 180, 0.28, 0.045, 'triangle');
      else if (kind === 'push') tone(130, 60, 0.09, 0.08, 'sine');
      else if (kind === 'daze') { tone(300, 620, 0.12, 0.03, 'sine'); tone(620, 280, 0.16, 0.03, 'sine', 0.12); }
      else if (kind === 'back') tone(900, 300, 0.14, 0.02, 'sawtooth');
      else if (kind === 'jug') { tone(523, 523, 0.14, 0.04, 'triangle'); tone(659, 659, 0.14, 0.04, 'triangle', 0.12); tone(784, 784, 0.3, 0.04, 'triangle', 0.24); }
    } catch { /* без звуку гра повна */ }
  }

  // ---------------------------------------------------------------------------------------------
  // Вид і кадри
  // ---------------------------------------------------------------------------------------------

  function applyView(st, v) {
    if (!v) return;
    const prevPhase = st.vphase;
    st.view = v;
    st.n = v.n | 0;
    st.looks = v.looks || [];
    st.names = v.names || [];
    st.me = v.me || null;
    st.meId = st.me ? st.me.id : -1;
    if (v.round !== st.round || (v.phase === 'start' && prevPhase && prevPhase !== 'start')) {
      st.round = v.round;
      st.camRound = -1;
      st.interp.reset();
      st.last = null;
      st.fingers.length = 0;
      st.poofs.length = 0;
      st.bumps.length = 0;
      st.caughtN = 0;
      st.pushAt = -1e9;
    }
    if (st.me) {
      st.caughtN = Math.max(st.caughtN, st.me.caught | 0);
      // після F5 руки «відходять» стільки, скільки каже сервер
      if (st.me.cool > 0) st.pushAt = Math.max(st.pushAt, performance.now() - PUSH_COOL_MS + st.me.cool * TICK_MS);
    }
    if (st.autoPeek && st.me) {
      if (v.phase === 'go') { st.peekUntil = performance.now() + F5_PEEK_MS; st.autoPeek = false; }
      else if (v.phase === 'start') st.autoPeek = false;
    }
    if (v.v && v.v.length) {
      const f = { t: v.t | 0, ph: v.phase, left: v.left | 0, b: v.b | 0, bl: v.bl | 0, v: v.v, ev: [] };
      const last = st.last;
      if (!last || last.v.length !== f.v.length || f.t > last.t || (f.t < last.t && f.ph === 'start')) push(st, f, false);
      if (!last) babaState(st, f, performance.now(), true);
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

  /// Баба — з найсвіжішого кадру, одразу: банер «Замри!» не чекає інтерполяції (кожні 40 мс тут — чиясь реакція).
  function babaState(st, f, now, quiet) {
    const b = f.b | 0;
    if (b !== st.b) {
      if (b === 1 && f.ph === 'go') {
        st.turnAt = now;
        if (!quiet) sfx(st, 'freeze');
      }
      if (b === 0) st.singAt = now;
      st.b = b;
      st.bAt = now;
    }
    st.bl = f.bl | 0;
  }

  function onFrame(st, f) {
    if (!f || !f.v) return;
    const now = performance.now();
    push(st, f, true);
    babaState(st, f, now, false);
    if (f.ev && f.ev.length) events(st, f, now);
    // затиснуту стрілку підтверджуємо раз на секунду (сервер відпускає її сам, якщо 3 с не чув), а на зміну фази — одразу
    const walk = (f.ph === 'start' || f.ph === 'go') && st.dir >= 0 && st.ctx && st.ctx.mine && st.ctx.playing;
    if (f.ph !== st.fph) {
      if (walk) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
      st.fph = f.ph;
    }
    if (walk && now - st.sentAt > HOLD_MS) { st.ctx.input('move', { dir: st.dir }); st.sentAt = now; }
    if (st.localUntil && st.meId >= 0 && f.v[st.meId * 4 + 2] === st.localDir) st.localUntil = 0;
    paintClock(st, f);
  }

  function events(st, f, now) {
    const esc = st.ctx.esc;
    for (const e of f.ev) {
      const id = e[1];
      if (e[0] === 1) {
        const b = e[2], fell = e[3] === 1;
        const a = nameOf(st, id), vb = nameOf(st, b), she = FEMALE.has(a), her = FEMALE.has(vb);
        st.bumps.push({ x: f.v[b * 4], y: f.v[b * 4 + 1], at: now });
        if (st.bumps.length > 10) st.bumps.shift();
        if (fell) {
          news(st, '👊 ' + esc(a) + (she ? ' штурхнула ' : ' штурхнув ') + esc(vb) + ' — ' + (her ? 'та впала' : 'той упав') + '!'
            + (b === st.meId ? ' Це ж тебе!' : ''));
          sfx(st, 'push');
        } else {
          news(st, '💫 ' + esc(a) + (she ? ' штурхнула ' : ' штурхнув ') + esc(vb) + ', а ' + (her ? 'та' : 'той') + ' й не хитнувся — '
            + (id === st.meId ? 'ти отетерів!' : esc(a) + (she ? ' отетеріла' : ' отетерів')));
          sfx(st, 'daze');
        }
      } else if (e[0] === 2) {
        st.fingers.push({ id, at: now });
        if (st.fingers.length > 8) st.fingers.shift();
        const nm = nameOf(st, id);
        st.say = CATCH_SAY[(id + st.round) % CATCH_SAY.length].replace('{n}', nm);
        st.sayUntil = now + FINGER_MS + 300;
        if (id === st.meId) st.caughtN++;
        news(st, '👵 ' + esc(nm) + (FEMALE.has(nm) ? ' ворухнулась' : ' ворухнувся') + ' — назад до тину!' + (id === st.meId ? ' Ой, це ж ти…' : ''));
        sfx(st, 'caught');
      } else if (e[0] === 3) {
        st.poofs.push({ x: f.v[id * 4], y: f.v[id * 4 + 1], at: now });
        if (st.poofs.length > 10) st.poofs.shift();
        if (id === st.meId) sfx(st, 'back');
      }
    }
  }

  function news(st, html) {
    const el = st.newsEl;
    if (!el) return;
    const item = document.createElement('div');
    item.className = 'freeze-ni';
    item.innerHTML = html;
    el.insertBefore(item, el.firstChild);
    while (el.children.length > 2) el.lastChild.remove();
    setTimeout(() => item.remove(), NEWS_MS);
  }

  // ---------------------------------------------------------------------------------------------
  // Позиції, камера, ціль штурхана
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
    // косметика свого: миттєвий поворот і хода, поки сервер не підтвердив
    const me = st.meId;
    if (me >= 0 && me < n && now < st.localUntil && ps[me] === 0) {
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

  /// Той самий вибір, що й на сервері (FreezeCore.Nearest): найближчий на ногах попереду (півплощина погляду) на 32.
  function pushTarget(st, n) {
    const me = st.meId;
    if (me < 0 || me >= n) return -1;
    const d = st.pd[me], fx = DX[d] || 0, fy = DY[d] || 0, x = st.px[me], y = st.py[me];
    let best = -1, bestD = Infinity;
    for (let i = 0; i < n; i++) {
      if (i === me || st.ps[i] === 2 || st.ps[i] === 3) continue;
      const dx = st.px[i] - x, dy = st.py[i] - y, d2 = dx * dx + dy * dy;
      if (d2 > PUSH_RANGE * PUSH_RANGE || dx * fx + dy * fy <= 0) continue;
      if (d2 < bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  /// Лівий край в'юпорта в одиницях світу (у режимі всього лугу — нуль). По висоті луг видно цілий завжди.
  function camera(st, n) {
    const box = st.box;
    if (st.mode !== 'port') { box[0] = 0; box[1] = 0; box[2] = WW; box[3] = WH; return box; }
    const me = st.meId;
    const phase = phaseOf(st);
    if (!st.drag) {
      if (mineNow(st) && me < n && phase !== 'reveal' && phase !== 'over') st.cam.x = st.px[me];
      else if ((phase === 'reveal' || phase === 'over') && st.view && st.view.reveal && st.camRound !== st.round) {
        // на розкритті камера сама їде до переможця раунду (або до першого з гравців)
        st.camRound = st.round;
        const r = st.view.reveal, w = (r.winners || [])[0];
        const hit = (r.ids || []).find((p) => p.seat === w) || (r.ids || [])[0];
        if (hit && hit.id >= 0 && hit.id < n) st.cam.x = st.px[hit.id];
      } else if (!mineNow(st) && phase === 'go') {
        // глядачеві — за головою юрми, плавно
        let lead = 0;
        for (let i = 0; i < n; i++) if (st.ps[i] !== 3 && st.px[i] > lead) lead = st.px[i];
        st.cam.x += (lead - 120 - st.cam.x) * 0.05;
      }
    }
    box[0] = clamp(st.cam.x - PW / 2, 0, WW - PW);
    box[1] = 0;
    box[2] = PW;
    box[3] = PH;
    return box;
  }

  // ---------------------------------------------------------------------------------------------
  // Малювання
  // ---------------------------------------------------------------------------------------------

  function ensureStatic(st, now) {
    if (st.stat && st.statKey && now - st.palAt <= 1000) return true;
    st.pal = palette();
    st.palAt = now;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const S = Math.min(2, dpr * (st.mode === 'port' ? 1.4 : 1));
    const key = S + '|' + st.pal.grass + st.pal.sky + st.pal.wood;
    if (st.stat && st.statKey === key) return true;
    st.S = S;
    st.stat = drawStatic(st.pal, S);
    st.statKey = key;
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
    const phase = phaseOf(st);
    const playing = !!(st.ctx && st.ctx.playing);
    const mine = mineNow(st) && st.meId < n;
    const freezing = phase === 'go' && (st.b === 1 || st.b === 2);
    // «Замри!»: коротко трусне екран (без руху — якщо людина просила менше руху)
    let shake = 0;
    if (phase === 'go' && now - st.turnAt < 200 && !reduced()) shake = Math.sin((now - st.turnAt) / 18) * 3 * (1 - (now - st.turnAt) / 200);

    g.setTransform(1, 0, 0, 1, 0, 0);
    g.imageSmoothingEnabled = true;
    g.drawImage(st.stat, cx * S, cy * S, vw * S, vh * S, shake * k, 0, bw, bh);
    g.setTransform(k, 0, 0, k, (-cx + shake) * k, -cy * k);

    // пісня Баби: нотка раз на третину секунди, поки співає (замовкла — небезпека!)
    if (phase === 'go' && st.b === 0 && playing && now >= st.noteAt) {
      sfx(st, 'note', SONG[st.noteI++ % SONG.length]);
      st.noteAt = now + NOTE_MS;
    }

    jug(g, JUG_X, JUG_Y, now, phase === 'go' || phase === 'start');
    trails(st, g, pal, phase);
    const peek = mine && (phase === 'start' || (phase === 'go' && now < st.peekUntil));
    if (peek) ring(g, st.px[st.meId], st.py[st.meId], pal, now);

    // тіні пачкою, потім селяни за y; Баба — серед них за своєю y
    g.fillStyle = pal.shadow;
    g.beginPath();
    for (let i = 0; i < n; i++) {
      const x = st.px[i], y = st.py[i];
      if (x < cx - 20 || x > cx + vw + 20) continue;
      g.moveTo(x + 11, y + 4);
      g.ellipse(x + 1, y + 4, 10, 4, 0, 0, TAU);
    }
    g.fill();
    const look = [0, 0, 0, 0];
    const finger = st.fingers.length && now - st.fingers[st.fingers.length - 1].at < FINGER_MS;
    let babaDrawn = false;
    for (let o = 0; o < n; o++) {
      const i = st.order[o];
      const x = st.px[i], y = st.py[i];
      if (!babaDrawn && y > BABA_Y) { drawBaba(g, BABA_X, BABA_Y, babaMode(st, phase), now, pal, now - st.turnAt, finger); babaDrawn = true; }
      if (x < cx - 20 || x > cx + vw + 20) continue;
      look[0] = st.looks[i * 4] | 0; look[1] = st.looks[i * 4 + 1] | 0; look[2] = st.looks[i * 4 + 2] | 0; look[3] = st.looks[i * 4 + 3] | 0;
      if (st.ps[i] === 3) caughtMark(g, x, y, now);
      villager(g, x, y + 3, st.pd[i], st.ps[i], look, now, i, pal.ink);
      if (st.ps[i] === 4) dazeStars(g, x, y - 24, now);
    }
    if (!babaDrawn) drawBaba(g, BABA_X, BABA_Y, babaMode(st, phase), now, pal, now - st.turnAt, finger);
    fingers(st, g, pal, now, n);
    effects(st, g, now);

    if (mine && phase === 'go' && playing && (st.b === 0 || st.b === 3) && pushReady(now, st) && st.ps[st.meId] < 2) {
      const t = pushTarget(st, n);
      if (t >= 0) chevron(g, st.px[t], st.py[t] - 30, pal);
    }
    // свій іде, а Баба вже кричить — «Стій!» над собою (бачу лише я)
    if (mine && phase === 'go' && freezing && st.ps[st.meId] === 1) warn(g, st.px[st.meId], st.py[st.meId] - 40, now);
    labels(st, g, pal, n, phase);

    g.setTransform(k, 0, 0, k, 0, 0);
    if (freezing) cold(st, g, pal, cv.w, cv.h, now);
    shade(st, g, pal, cv.w, cv.h, cx, phase, playing, mine, now);
    g.setTransform(k, 0, 0, k, -cx * k, -cy * k);
    if (peek) meArrow(g, st.px[st.meId], st.py[st.meId], pal, true);
    if (mine && now < st.tipUntil && st.tip) tipPlate(g, st, st.px[st.meId], st.py[st.meId], pal);
    if (now < st.sayUntil && st.say) sayBubble(g, st, pal);
    g.setTransform(k, 0, 0, k, 0, 0);
    if (st.mode === 'port') strip(st, g, pal, cv.w, n, mine, phase);
    if (playing && st.lastAt && now - st.lastAt > 600 && phase !== 'over') {
      g.font = '600 12px system-ui, sans-serif';
      g.textAlign = 'left';
      g.textBaseline = 'top';
      outlined(g, 'з\'єднання…', 8, st.mode === 'port' ? 30 : 8, pal.text, pal.ink);
    }

    const dt = performance.now() - t0;
    st.perf.sum += dt;
    st.perf.n++;
    if (dt > st.perf.max) st.perf.max = dt;
  }

  /// Як малювати Бабу: на «роздивись» і в кінці — лицем до лугу, у грі — як каже кадр.
  function babaMode(st, phase) {
    if (phase === 'go') return st.b;
    return 2;
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
    g.beginPath(); g.ellipse(x, y + 4, 15, 7.5, 0, 0, TAU); g.stroke();
    g.setLineDash([]);
    g.lineDashOffset = 0;
  }

  function meArrow(g, x, y, pal, big) {
    const top = y - (big ? 34 : 30);
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

  function chevron(g, x, y, pal) {
    g.fillStyle = pal.danger;
    g.strokeStyle = pal.ink;
    g.lineWidth = 1.5;
    g.beginPath();
    g.moveTo(x - 6, y - 6); g.lineTo(x + 6, y - 6); g.lineTo(x, y + 1); g.closePath();
    g.fill(); g.stroke();
  }

  function warn(g, x, y, now) {
    g.font = '900 15px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.globalAlpha = 0.6 + 0.4 * Math.sin(now / 50);
    outlined(g, 'СТІЙ!', x, y, '#ff6b5e', '#2a0a08');
    g.globalAlpha = 1;
  }

  /// Впійманий: червоне кільце під ногами й «!» над головою — Баба тицяє саме в нього.
  function caughtMark(g, x, y, now) {
    g.strokeStyle = 'rgba(230, 60, 50, .9)';
    g.lineWidth = 2.5;
    g.beginPath(); g.ellipse(x, y + 4, 15, 7, 0, 0, TAU); g.stroke();
    g.font = '900 16px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    outlined(g, '!', x, y - 42 + Math.sin(now / 90) * 1.5, '#ff5a4a', '#2a0a08');
  }

  function dazeStars(g, x, y, now) {
    g.font = '10px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillStyle = '#ffe28a';
    for (let s = 0; s < 3; s++) {
      const a = now / 140 + (s * TAU) / 3;
      g.fillText('✦', x + Math.cos(a) * 9, y + Math.sin(a) * 3.5);
    }
  }

  /// Палець Баби: лінія від руки до впійманого, поки він стоїть під її поглядом.
  function fingers(st, g, pal, now, n) {
    const list = st.fingers;
    for (let i = list.length - 1; i >= 0; i--) if (now - list[i].at > FINGER_MS) list.splice(i, 1);
    for (const f of list) {
      if (f.id >= n) continue;
      const p = (now - f.at) / FINGER_MS;
      const x0 = BABA_X - 19, y0 = BABA_Y - 34, x1 = st.px[f.id], y1 = st.py[f.id] - 14;
      g.globalAlpha = 1 - p * 0.6;
      g.strokeStyle = 'rgba(255, 90, 74, .85)';
      g.lineWidth = 2;
      g.setLineDash([8, 5]);
      g.lineDashOffset = -now / 30;
      g.beginPath(); g.moveTo(x0, y0); g.lineTo(x1, y1); g.stroke();
      g.setLineDash([]);
      g.lineDashOffset = 0;
      g.globalAlpha = 1;
    }
  }

  /// Пил від штурхана й «пуф» там, куди повернули впійманого.
  function effects(st, g, now) {
    for (const [list, ms, col] of [[st.bumps, BUMP_MS, '#e8dcc0'], [st.poofs, POOF_MS, '#ffffff']]) {
      for (let i = list.length - 1; i >= 0; i--) if (now - list[i].at > ms) list.splice(i, 1);
      for (const b of list) {
        const p = (now - b.at) / ms;
        g.globalAlpha = 0.7 * (1 - p);
        g.fillStyle = col;
        for (let j = 0; j < 4; j++) {
          const a = j * 1.57 + 0.4;
          g.beginPath(); g.arc(b.x + Math.cos(a) * 10 * p, b.y - 8 + Math.sin(a) * 6 * p, 3 + 6 * p, 0, TAU); g.fill();
        }
        g.globalAlpha = 1;
      }
    }
  }

  /// Хмарка з реплікою Баби (кого впіймала) — над її головою.
  function sayBubble(g, st, pal) {
    const text = st.say;
    g.font = '700 13px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const w = g.measureText(text).width + 16, h = 22;
    const x = clamp(BABA_X - 40, st.box[0] + w / 2 + 4, st.box[0] + st.box[2] - w / 2 - 4), y = BABA_Y - 84;
    g.fillStyle = 'rgba(255, 252, 240, .96)';
    g.strokeStyle = pal.ink;
    g.lineWidth = 1.2;
    g.beginPath(); g.roundRect(x - w / 2, y - h / 2, w, h, 10); g.fill(); g.stroke();
    g.fillStyle = '#7b1f1f';
    g.fillText(text, x, y + 0.5);
  }

  function tipPlate(g, st, x, y, pal) {
    const px = Math.max(12, 13 * (st.cssK ? 1 / st.cssK : 1));
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const w = g.measureText(st.tip).width + px, h = px + 8;
    const vx = st.box[0], vw = st.box[2];
    const tx = clamp(x, vx + w / 2 + 4, vx + vw - w / 2 - 4), ty = Math.max(h, y - 54);
    g.fillStyle = 'rgba(12, 22, 14, .88)';
    g.beginPath(); g.roundRect(tx - w / 2, ty - h / 2, w, h, h / 2); g.fill();
    g.strokeStyle = pal.danger;
    g.lineWidth = 1.5;
    g.stroke();
    g.fillStyle = pal.text;
    g.fillText(st.tip, tx, ty + 0.5);
  }

  /// Сліди гравців за останні ≈ 30 с — лише на розкритті. Повернення на старт рве лінію (стрибок — не хода).
  function trails(st, g, pal, phase) {
    const v = st.view, r = v && v.reveal;
    if (!r || !r.trails || (phase !== 'reveal' && phase !== 'over')) return;
    g.lineJoin = 'round';
    g.lineCap = 'round';
    for (const t of r.trails) {
      const p = t.pts || [];
      if (p.length < 4) continue;
      const col = pal.seats[t.seat] || pal.text;
      for (const [w, style, dash] of [[5, 'rgba(12, 22, 14, .5)', false], [2.5, col, true]]) {
        g.strokeStyle = style;
        g.lineWidth = w;
        if (dash) g.setLineDash([6, 5]);
        g.beginPath();
        g.moveTo(p[0], p[1]);
        for (let i = 2; i < p.length; i += 2) {
          if (Math.abs(p[i] - p[i - 2]) > 60) g.moveTo(p[i], p[i + 1]);
          else g.lineTo(p[i], p[i + 1]);
        }
        g.stroke();
        g.setLineDash([]);
      }
    }
  }

  function labels(st, g, pal, n, phase) {
    const v = st.view;
    if (!v || !v.reveal || !(phase === 'reveal' || phase === 'over' || v.phase === 'reveal' || v.phase === 'over')) return;
    const rows = v.reveal.ids || [];
    const over = phase === 'over' || v.phase === 'over';
    const win = over ? (v.result && v.result.winners) || [] : v.reveal.winners || [];
    const mark = over ? '🏆 ' : v.reveal.why === 'jug' ? '🏺 ' : '⭐ ';
    g.lineWidth = 2.5;
    for (const r of rows) {
      if (r.id < 0 || r.id >= n) continue;
      g.strokeStyle = pal.seats[r.seat] || pal.text;
      g.beginPath(); g.ellipse(st.px[r.id], st.py[r.id] + 4, 14, 7, 0, 0, TAU); g.stroke();
    }
    const px = st.mode === 'port' ? 15 : 13, h = px + 5;
    g.font = '700 ' + px + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const lab = st.lab, pool = st.labPool;
    lab.length = 0;
    for (const r of rows) {
      if (r.id < 0 || r.id >= n) continue;
      const a = pool[lab.length] || (pool[lab.length] = { seat: 0, text: '', x: 0, y: 0, w: 0 });
      a.seat = r.seat;
      a.text = (win.includes(r.seat) ? mark : '') + nickOfSeat(st, r.seat);
      a.x = st.px[r.id];
      a.y = st.py[r.id] - (st.ps[r.id] === 2 ? 16 : 36);
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
      a.x = clamp(a.x, st.box[0] + a.w / 2 + 2, st.box[0] + st.box[2] - a.w / 2 - 2);
      g.fillStyle = 'rgba(12, 22, 14, .82)';
      g.beginPath(); g.roundRect(a.x - a.w / 2, a.y - h / 2, a.w, h, h / 2); g.fill();
      g.fillStyle = pal.seats[a.seat] || pal.text;
      g.fillText(a.text, a.x, a.y + 0.5);
    }
  }

  /// «Замри!»: екран холоне — синюватий іній по краях, банер, що вискакує, і відлік, скільки ще Баба дивитиметься.
  function cold(st, g, pal, w, h, now) {
    const since = now - st.turnAt;
    const a = clamp(since / 140, 0, 1);
    g.globalAlpha = a;
    g.fillStyle = pal.cold;
    g.fillRect(0, 0, w, h);
    const vg = g.createRadialGradient(w / 2, h / 2, Math.min(w, h) * 0.3, w / 2, h / 2, Math.max(w, h) * 0.7);
    vg.addColorStop(0, 'rgba(180, 220, 255, 0)');
    vg.addColorStop(1, 'rgba(200, 235, 255, .55)');
    g.fillStyle = vg;
    g.fillRect(0, 0, w, h);
    g.globalAlpha = 1;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const port = st.mode === 'port';
    const secs = Math.max(1, Math.ceil((st.bl * TICK_MS) / 1000));
    if (since < BANNER_MS) {
      // вискакує великим і осідає
      const p = since / BANNER_MS;
      const scale = p < 0.15 ? 0.6 + p / 0.15 * 0.6 : 1.2 - Math.min(0.2, (p - 0.15) * 0.4);
      const px = Math.round((port ? 58 : 72) * scale);
      g.font = '900 ' + px + 'px system-ui, sans-serif';
      outlined(g, 'ЗАМРИ!', w / 2, h * 0.42, '#eaf6ff', '#0d2a44');
    } else {
      g.font = '900 ' + (port ? 26 : 30) + 'px system-ui, sans-serif';
      outlined(g, '🧊 ЗАМРИ! ' + secs, w / 2, port ? 58 : 34, '#eaf6ff', '#0d2a44');
    }
  }

  function shade(st, g, pal, w, h, cx, phase, playing, mine, now) {
    const v = st.view;
    if (!v) return;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const room = st.ctx && st.ctx.room;
    const minPx = 14 / (st.cssK || 1);
    if (!playing && room && room.status === 'lobby') {
      g.fillStyle = 'rgba(10, 20, 12, .45)';
      g.fillRect(0, 0, w, h);
      const msg = st.mode === 'port' ? 'Чекаємо, поки господар натисне «Почати»' : 'Баба Параска чекає, поки господар натисне «Почати»';
      fitFont(g, msg, w * 0.9, Math.round(Math.max(minPx, h / 14)), 700);
      outlined(g, msg, w / 2, h / 2, pal.text, pal.ink);
      return;
    }
    if (phase === 'start') {
      if (mine) spotlight(st, g, w, h, cx, 0.42);
      const left = st.last ? st.last.left : v.left;
      g.font = '800 ' + Math.round(h / 4) + 'px system-ui, sans-serif';
      outlined(g, String(Math.max(1, Math.ceil((left * TICK_MS) / 1000))), w / 2, h / 2, pal.text, pal.ink);
      const sub = mine ? 'Роздивись: ти — під стрілкою' : 'Селяни збираються біля тину…';
      const px = Math.round(Math.max(h / 18, minPx));
      fitFont(g, sub, w * 0.92, px, 700);
      outlined(g, sub, w / 2, h / 2 + h / 6, pal.text, pal.ink);
      if (mine) {
        const how = st.mode === 'port' ? 'Співає — іди до глека 🏺 · «Замри!» — стій' : 'Баба співає — іди до глека 🏺 · крикнула «Замри!» — стій, як укопаний · 👊 штурхан';
        fitFont(g, how, w * 0.92, Math.round(px * 0.9), 600);
        outlined(g, how, w / 2, h / 2 + h / 6 + px * 1.5, pal.accent, pal.ink);
      }
      return;
    }
    if (phase === 'go' && mine && now < st.peekUntil) spotlight(st, g, w, h, cx, 0.46 * Math.min(1, (st.peekUntil - now) / 300));
    if (phase === 'go' && st.b === 3) {
      // озирнулась — лише шепіт над Бабою, без банера: це може бути обманка
      const x = BABA_X - cx - 18, y = BABA_Y - 92;
      if (x > 10 && x < w - 10) {
        g.font = '800 18px system-ui, sans-serif';
        outlined(g, '👀 …', x, y, '#fff6d8', pal.ink);
      }
    }
    if (phase === 'reveal' || phase === 'over' || (!playing && v.phase === 'over')) {
      g.fillStyle = 'rgba(10, 20, 12, .28)';
      g.fillRect(0, 0, w, h);
      const port = st.mode === 'port';
      const title = phase === 'reveal' ? revealTitle(st, port) : overTitle(st);
      const left = port ? w : w - 130, mid = left / 2;
      fitFont(g, title, left - 30, Math.round(Math.max(minPx, h / 16)), 800);
      const tw = g.measureText(title).width + 28;
      const th = Math.max(26, h / 10), ty = port ? 40 : Math.round(h * 0.34 - th / 2);
      g.fillStyle = 'rgba(10, 20, 12, .78)';
      g.beginPath(); g.roundRect(mid - tw / 2, ty, tw, th, 10); g.fill();
      g.fillStyle = pal.text;
      g.fillText(title, mid, ty + th / 2);
    }
  }

  function spotlight(st, g, w, h, cx, alpha) {
    const x = st.px[st.meId] - cx, y = st.py[st.meId] - 10;
    g.fillStyle = 'rgba(10, 20, 12, ' + alpha.toFixed(3) + ')';
    g.beginPath();
    g.rect(0, 0, w, h);
    g.arc(x, y, 56, 0, TAU, true);
    g.fill('evenodd');
  }

  function revealTitle(st, short) {
    const v = st.view, r = v && v.reveal;
    if (!r) return '';
    const head = short ? '' : 'Раунд ' + v.round + ' з ' + v.of + ' · ';
    const who = (r.winners || []).map((s) => nickOfSeat(st, s)).join(' і ');
    switch (r.why) {
      case 'jug': return head + '🏺 До глека перш' + ((r.winners || []).length > 1 ? 'і — ' : 'ий — ') + who;
      case 'time': return head + '⏱ Час вийшов — найдалі зайш' + ((r.winners || []).length > 1 ? 'ли ' : 'ов ') + who;
      default: return head + '⏱ Час вийшов';
    }
  }

  function overTitle(st) {
    const v = st.view, res = v && v.result;
    if (!res) return 'Партію зіграно';
    if (res.why === 'left') {
      return res.winners.length ? '🚪 Суперники розійшлись — перемога: ' + res.winners.map((s) => nickOfSeat(st, s)).join(', ') : '🚪 Усі розійшлись';
    }
    if (!res.winners.length) return '🤝 Нічия';
    return '🏆 Перемога: ' + res.winners.map((s) => nickOfSeat(st, s) + ' ' + (res.totals[s] | 0)).join(', ');
  }

  /// Телефон: смужка поступу вгорі — весь луг від тину до глека, крапки всіх селян (публічне), свій — жовтим (лише мені),
  /// рамка в'юпорта і значок Баби: що вона зараз робить, навіть коли її не видно.
  function strip(st, g, pal, w, n, mine, phase) {
    const x0 = 8, x1 = w - 40, y = 12, sx = (x1 - x0) / WW;
    g.fillStyle = 'rgba(12, 22, 14, .62)';
    g.beginPath(); g.roundRect(x0 - 4, y - 7, x1 - x0 + 8, 14, 7); g.fill();
    g.strokeStyle = 'rgba(255, 255, 255, .8)';
    g.lineWidth = 1;
    g.strokeRect(x0 + st.box[0] * sx, y - 6, st.box[2] * sx, 12);
    g.fillStyle = 'rgba(255, 255, 255, .7)';
    for (let i = 0; i < n; i++) { if (i !== st.meId) g.fillRect(x0 + st.px[i] * sx - 0.75, y - 2, 1.5, 4); }
    g.fillStyle = '#fbfaf2';
    g.fillRect(x0 + FINISH_X * sx - 1, y - 6, 2, 12);
    if (mine) {
      g.fillStyle = pal.accent;
      g.beginPath(); g.arc(x0 + st.px[st.meId] * sx, y, 3.4, 0, TAU); g.fill();
    }
    // значок Баби праворуч
    const bx = w - 20, by = 13;
    const face = phase === 'go' ? st.b : 2;
    g.fillStyle = face === 1 || face === 2 ? '#cfe8ff' : face === 3 ? '#fff0c0' : '#f4f0e4';
    g.beginPath(); g.arc(bx, by, 11, 0, TAU); g.fill();
    g.strokeStyle = pal.ink; g.lineWidth = 1.2; g.stroke();
    g.font = '13px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillStyle = pal.ink;
    g.fillText(face === 0 ? '🎵' : face === 3 ? '👀' : '👵', bx, by + 1);
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
    let html = '<span class="freeze-chip">Раунд ' + Math.max(1, v.round | 0) + '/' + (v.of | 0) + '</span>';
    if (v.me) {
      const ready = pushReady(performance.now(), st);
      html += '<span class="freeze-chip freeze-hand' + (ready ? '' : ' cool') + '" title="Штурхан: пробіл, Ⓐ або тиць по сусідові">👊</span>';
      html += '<span class="freeze-chip" title="Скільки разів Баба тебе впіймала цього раунду">😵 ' + (st.caughtN | 0) + '</span>';
    }
    html += '<button type="button" class="freeze-mute" data-pad-skip title="Звук">' + (st.mute ? '🔇' : '🔊') + '</button>';
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.querySelector('.freeze-chips').innerHTML = html;
    }
    let row = '';
    for (const s of v.seats || []) {
      row += '<span class="freeze-seat freeze-s' + s.seat + (s.out ? ' out' : '') + (s.seat === ctx.seat ? ' me' : '') + '">'
        + '<i></i>' + ctx.esc(s.nick) + ' <b>' + (s.total | 0) + '</b></span>';
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

  /// Підсумок — фішками за очками партії; дрібно — що дав раунд: 🏺 дійшов, 😵 скільки впіймали, 👊 скількох звалив.
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
      const rows = (r.rows || []).slice().sort((a, b) => total(b.seat) - total(a.seat) || b.x - a.x || a.seat - b.seat);
      const dist = FINISH_X - START_X;
      html = rows.map((x) => '<span class="freeze-sc freeze-s' + x.seat + (champ.includes(x.seat) ? ' win' : '') + '">'
        + (champ.includes(x.seat) ? (over ? '🏆 ' : '⭐ ') : '') + '<b>' + st.ctx.esc(nickOfSeat(st, x.seat)) + '</b> '
        + '<em>' + total(x.seat) + '</em> <small title="за раунд: очки, пройдений шлях, скільки впіймала Баба, скількох звалив">+' + x.pts
        + ' · ' + (x.x >= FINISH_X ? '🏺' : Math.round(clamp((x.x - START_X) / dist, 0, 1) * 100) + '%')
        + (x.caught ? ' · 😵' + x.caught : '') + (x.hits ? ' · 👊' + x.hits : '') + '</small></span>').join('');
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      el.hidden = !html;
    }
    if (st.stageEl) st.stageEl.classList.toggle('freeze-open', open);
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

  /// Куди я зараз іду: палець на хрестовині важить більше, далі — остання затиснута клавіша.
  function want(st) {
    const d = st.touchDir >= 0 ? st.touchDir : st.keys.length ? st.keys[st.keys.length - 1] : -1;
    if (d === st.dir) return;
    st.dir = d;
    if (d >= 0) { st.localDir = d; st.localUntil = performance.now() + LOCAL_MS; }
    const ctx = st.ctx;
    if (ctx && ctx.mine && ctx.playing) { ctx.input('move', { dir: d }); st.sentAt = performance.now(); }
  }

  function shove(st, id) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const now = performance.now();
    if (!pushReady(now, st)) { refuse(st, 'Руки ще не відійшли'); return; }
    if (id == null) {
      const n = positions(st, now);
      const t = pushTarget(st, n);
      id = t >= 0 ? t : null;
    }
    ctx.act('push', id == null ? {} : { id }).then((r) => {
      if (r && r.ok) { st.pushAt = performance.now(); hud(st); }
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
      const dx = st.px[i] - x, dy = st.py[i] - 10 - y, d2 = dx * dx + dy * dy;
      if (d2 <= bestD) { bestD = d2; best = i; }
    }
    return best;
  }

  function canAct(st) {
    const ctx = st.ctx;
    return !!(ctx && ctx.mine && ctx.playing && phaseOf(st) === 'go' && st.meId >= 0);
  }

  /// Чи дотягнуся до селянина (з допуском, як у сервера для кліку).
  function reachable(st, id) {
    const me = st.meId;
    if (me < 0 || id < 0) return false;
    const dx = st.px[id] - st.px[me], dy = st.py[id] - st.py[me];
    return dx * dx + dy * dy <= PUSH_MAX * PUSH_MAX;
  }

  function wireCanvas(st) {
    const el = st.cv.el;
    el.addEventListener('pointerdown', (e) => {
      wake(st);
      unlock(st);
      st.down = { id: e.pointerId, x: e.clientX, y: e.clientY, moved: false, cam: st.cam.x };
    });
    el.addEventListener('pointermove', (e) => {
      wake(st);
      const d = st.down;
      if (d && d.id === e.pointerId) {
        const dx = e.clientX - d.x;
        if (Math.abs(dx) + Math.abs(e.clientY - d.y) > 6) d.moved = true;
        // глядачеві на телефоні — тягни луг пальцем
        if (st.mode === 'port' && d.moved && !mineNow(st)) {
          const r = el.getBoundingClientRect();
          st.drag = true;
          st.cam.x = clamp(d.cam - (dx / r.width) * PW, PW / 2, WW - PW / 2);
        }
        return;
      }
      if (e.pointerType !== 'mouse' || !canAct(st)) { if (st.hover) { st.hover = false; el.style.cursor = ''; } return; }
      const [x, y] = toWorld(st, e);
      const id = pickVillager(st, x, y, 20);
      const on = id >= 0 && reachable(st, id);
      if (on !== st.hover) { st.hover = on; el.style.cursor = on ? 'pointer' : ''; }
    });
    const up = (e) => {
      const d = st.down;
      if (!d || d.id !== e.pointerId) return;
      st.down = null;
      if (!d.moved) st.drag = false;
      if (d.moved || e.type === 'pointercancel' || !canAct(st)) return;
      const [x, y] = toWorld(st, e);
      const id = pickVillager(st, x, y, e.pointerType === 'mouse' ? 20 : 26);
      if (id >= 0 && reachable(st, id)) shove(st, id);
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
    el.className = 'freeze-pad';
    const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
    el.innerHTML = '<div class="freeze-dirs" role="group" aria-label="хрестовина: тримай і веди пальцем">'
      + [3, 2, 0, 1].map((d) => '<span class="freeze-arr" data-dir="' + d + '">' + label[d] + '</span>').join('')
      + '<button type="button" class="freeze-peek" data-act="peek" aria-label="де я">👁</button>'
      + '</div><div class="freeze-acts">'
      + '<button type="button" data-act="push" aria-label="штурхан">👊</button></div>';
    const dirs = el.querySelector('.freeze-dirs');
    const arrows = dirs.querySelectorAll('.freeze-arr');
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
        if (b.dataset.act === 'push') return shove(st, null);
        if (b.dataset.act === 'peek') return peek(st);
        return;
      }
      if (!e.target.closest('.freeze-dirs')) return;
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
    // палець відпустив — зупинка негайно (це найважливіший рух у грі)
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

  /// Луг 10:3 має влізти у вікно разом зі статусом і кнопками під ним: ширину рахуємо від того, де сцена справді
  /// починається, і скільки займає все під нею до низу картки, плюс смужка пада на Деку.
  function sizeStage(st, mode) {
    const el = st.stageEl;
    if (!el || !el.isConnected) return;
    if (mode === 'port') { if (el.style.maxWidth) el.style.maxWidth = ''; return; }
    const r = el.getBoundingClientRect();
    const card = el.closest('.gtable');
    const below = Math.max(48, card ? card.getBoundingClientRect().bottom - r.bottom : 92) + 22;
    const h = window.innerHeight - (r.top + (window.scrollY || 0)) - below - st.padH;
    const wantW = clamp(Math.floor((h * WW) / WH), 600, WW);
    const cur = parseFloat(el.style.maxWidth) || 0;
    if (Math.abs(cur - wantW) >= 3) el.style.maxWidth = wantW + 'px';
  }

  /// Режим камери — за шириною картки: вузько (телефон) — в'юпорт, що стежить за своїм, інакше — увесь луг.
  function fit(root, st) {
    wake(st);
    padStrip(st);
    const cw = root.clientWidth;
    let mode = st.mode;
    if (cw) mode = cw < 600 ? 'port' : cw > 640 ? 'full' : st.mode;
    sizeStage(st, mode);
    if (!st.cv || mode !== st.mode) {
      st.mode = mode;
      st.statKey = '';
      st.cv = HGames.ui.canvas(st.stageEl, mode === 'port' ? { w: PW, h: PH, cls: 'freeze-board freeze-port' } : { w: WW, h: WH, cls: 'freeze-board' });
      st.stageEl.classList.toggle('freeze-portmode', mode === 'port');
      placeSum(root, st);
      placePad(root, st);
      placeSeats(root, st);
    } else st.cv.resize();
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
    const a = st.hudEl.getBoundingClientRect(), b = padEl.getBoundingClientRect();
    if (!a.height || !b.height) return;              // картку чи кнопки зараз не видно — спробуємо на наступному виді
    st.fitFor = key;
    const head = document.querySelector('header');
    const top = (head ? head.getBoundingClientRect().bottom : 0) + 4;
    const tabs = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--tabs-h')) || 0;
    // кнопки мають стати над нижнім меню й над плаваючою кнопкою балачки столу («💬 Стіл»)
    const fab = document.querySelector('.tchat.drawer:not(.open) .tc-head');
    const fr = fab && fab.getBoundingClientRect();
    const limit = (fr && fr.height ? Math.min(fr.top, innerHeight - tabs) : innerHeight - tabs) - 6;
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
      if (now - st.padAt > 1000) {
        st.padAt = now;
        if (padStrip(st)) fit(st.root, st);
        hud(st);                  // 👊 відійшли руки — фішка знову яскрава
      }
      const live = !!(st.ctx && st.ctx.playing) && phaseOf(st) !== 'over';
      if (!st.visible || (!live && now - st.wakeAt > IDLE_MS)) { st.raf = 0; return; }
      if (!document.hidden) draw(st);
      st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  HGames.register({
    id: 'freeze',
    added: '2026-09-27',
    icon: ICON,
    seatNames: SEAT_NAMES,
    seatClass: ['x', 'o', 'c', 'd', 'freeze-b', 'freeze-p', 'freeze-v', 'freeze-r'],
    pad: {
      dirs: true,
      a: 'Space',
      x: 'KeyQ',
      on(btn, ctx) {
        if (btn === 'lb' || btn === 'rb') { if (ctx && ctx._freeze) peek(ctx._freeze); return true; }
        return false;
      },
      hint: '{dpad} іти · {a} штурхан · {x} де я?',
    },
    added: '2026-09-27',
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Замри!',
      items: [
        '🎵 Баба Параска співає «Море хвилюється» — юрма селян іде до її глека. Ти — один із них, і ніхто не знає, хто з селян живий',
        '🧊 Обернулась і крикнула «Замри!» — за мить стій як укопаний. Ворухнувся — Баба вертає тебе до тину',
        '👀 Інколи вона лише озирається через плече — хто злякався й став, той втратив час',
        '👊 Пробіл (Ⓐ) — штурхан сусідові: гравець упаде, а штурхнеш бота — сам отетерієш на очах у всіх',
        '🏺 Раунд бере перший, хто торкнувся глека; як вийшов час — хто зайшов найдалі',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      root.classList.add('freeze-body');
      st.hudEl = document.createElement('div');
      st.hudEl.className = 'freeze-hud';
      st.hudEl.innerHTML = '<span class="freeze-chip freeze-clock">⏱ —</span><span class="freeze-chips"></span>';
      st.clockEl = st.hudEl.querySelector('.freeze-clock');
      st.seatsEl = document.createElement('div');
      st.seatsEl.className = 'freeze-seats';
      st.hudEl.appendChild(st.seatsEl);
      st.hudEl.addEventListener('click', (e) => {
        if (!e.target.closest('.freeze-mute')) return;
        st.mute = !st.mute;
        try { localStorage.setItem('freezeMute', st.mute ? '1' : ''); } catch { /* приватне вікно */ }
        if (!st.mute) unlock(st);
        hud(st);
      });
      st.newsEl = document.createElement('div');
      st.newsEl.className = 'freeze-news';
      st.newsEl.setAttribute('aria-live', 'polite');
      st.sumEl = document.createElement('div');
      st.sumEl.className = 'freeze-sum';
      st.sumEl.hidden = true;
      st.stageEl = document.createElement('div');
      st.stageEl.className = 'freeze-stage';
      st.stageEl.appendChild(st.newsEl);
      root.append(st.hudEl, st.stageEl, st.sumEl);
      st.cv = HGames.ui.canvas(st.stageEl, { w: WW, h: WH, cls: 'freeze-board' });
      wireCanvas(st);
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => fit(root, st));
        st.ro.observe(root);
      }
      st.onResize = () => fit(root, st);
      window.addEventListener('resize', st.onResize);
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
      // вікно втратило фокус — ніхто не тримає клавіш (інакше «залиплий» крок упіймали б на «Замри!»)
      st.blur = () => {
        st.keys.length = 0;
        st.touchDir = -1;
        want(st);
      };
      document.addEventListener('keyup', st.keyup);
      window.addEventListener('blur', st.blur);
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
        const ph = st.vphase, off = !(ph === 'start' || ph === 'go');
        if (st.padEl.classList.contains('freeze-off') !== off) st.padEl.classList.toggle('freeze-off', off);
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
      const st = ctx._freeze;
      if (!st || !ctx.mine || !ctx.playing) return false;
      unlock(st);
      const d = dirOf(e);
      if (d !== undefined) {
        if (!st.keys.includes(d)) st.keys.push(d);
        else if (st.keys[st.keys.length - 1] !== d) { st.keys.splice(st.keys.indexOf(d), 1); st.keys.push(d); }
        want(st);
        return true;
      }
      if (isPush(e)) { if (!e.repeat) shove(st, null); return true; }
      if (isPeek(e)) { peek(st); return true; }
      return false;
    },

    status(ctx) {
      const st = ctx._freeze;
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f) return '';
      const ph = f.ph || f.phase;
      if (ph === 'start') return ctx.mine ? 'Роздивись: стрілка показує тебе. Баба заспіває — іди' : 'Селяни збираються біля тину…';
      if (ph === 'reveal') {
        const v = st && st.view, s = Math.max(1, Math.ceil(((f.left | 0) * TICK_MS) / 1000));
        return v && v.round >= v.of ? 'Підсумок партії за ' + s + ' с' : 'Наступний раунд за ' + s + ' с';
      }
      if (ph !== 'go') return '';
      if (!ctx.mine) return HGames.ui.coarse() ? 'Вгадуй, хто з юрми живий · тягни луг пальцем' : 'Вгадуй разом із гравцями, хто з юрми живий';
      const b = st ? st.b : 0;
      if (b === 1 || b === 2) return '🧊 Замри! Не ворушись, доки Баба дивиться';
      if (b === 3) return '👀 Баба озирається… іти чи стояти?';
      if (window.HPad && window.HPad.on) return '🎵 Співає — іди! Стік — іти · Ⓐ штурхан · Ⓧ де я?';
      return HGames.ui.coarse()
        ? '🎵 Співає — іди! Хрестовина — іти · 👊 штурхан · 👁 де я?'
        : '🎵 Співає — іди! Стрілки/WASD — іти · пробіл — штурхан · Q — де я?';
    },

    unmount(root) {
      const st = root._freeze;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (st.ro) st.ro.disconnect();
      if (st.io) st.io.disconnect();
      if (st.audio) { try { st.audio.close(); } catch { /* уже закритий */ } }
      root._freeze = null;
    },
  });
})();
