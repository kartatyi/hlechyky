/*
  Глекомети. Артилерія хатами: ходять по черзі, але політ — реалтайм. Сервер (Impl/Glekomet.cs + GlekometCore.cs)
  сам літає, рве землю й рахує шкоду; ми лише малюємо те, що він прислав, і ніколи не рахуємо вирву самі.

  Вид (один на всіх, spec §4.1): { phase, turn, round, endsAt, turnMs, startIn, wind, water, waterFrom, w, hgt, step,
    h[250], teams, huts[6]{seat,nick,x,y,hp,alive,team,poison,fuel,skips,reason}, inv[6][6], aim[a,p,w], shells[[x,y,k]],
    last{by,w,hits,text}, log[], stats[6], wins[6], result{winners,reason,best}, turnNo }
  Кадр (лише те, що змінилось, §4.2): { t, ph, sh?[[x,y,k]], hx?[[seat,x,y]], ex?[[x,y,r,kind]], dh?[[c0,h…]], hp?[6],
    aim?[a,p,w], si?, wl? }
  Ходи: act('fire', {a, p, w}), act('move', {dir}), act('skip'); косметичний приціл — input('aim', {a, p, w}) ≤ 10/с.

  Малюємо в одиницях світу (1000 × 500 u, y угору), канвас — рівно в розмір на екрані × DPR. Небо й земля — в
  offscreen-канвасі, який перемальовується лише тоді, коли прийшла нова земля; щокадру — один drawImage і рухоме.
*/
(() => {
  const W = 1000, HGT = 500, COLS = 250, STEP = 4, TICK_MS = 40;
  const HUT_HALF = 20, LAUNCH = 40;
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="3" y="9" width="8" height="5" rx="1" fill="var(--text)"/>'
    + '<path d="M2 9.5 7 5l5 4.5z" fill="var(--clay)"/>'
    + '<path d="M8 4 Q11 -1 14.5 5" fill="none" stroke="var(--accent)" stroke-width="1.2" stroke-dasharray="1.5 1.2"/>'
    + '<circle cx="14.4" cy="5.6" r="1.4" fill="var(--accent)"/></svg>';

  /// Комора: той самий порядок, що GlekometCore.Weapons на сервері (індекс w летить у fire).
  const WEAPONS = [
    { key: 'pot', name: 'Глек', chip: 'Глек', icon: '🏺', short: 'глек', tip: 'Глек: −35 у хату, вирва 22' },
    { key: 'shards', name: 'Розсипний глек', chip: 'Розсипний', icon: '💥', short: 'розсипний', tip: 'Розсипний: на верхівці — чотири скалки по −16' },
    { key: 'varenyk', name: 'Вареник-бомба', chip: 'Вареник', icon: '🥟', short: 'вареник', tip: 'Вареник-бомба: −55, велика вирва, вітру майже не чує' },
    { key: 'hay', name: 'Копа сіна', chip: 'Копа', icon: '🌾', short: 'копа', tip: 'Копа сіна: насипає горб-укриття там, де впаде' },
    { key: 'stork', name: 'Лелека', chip: 'Лелека', icon: '🕊', short: 'лелека', tip: 'Лелека: переносить твою хату туди, де сяде' },
    { key: 'khrin', name: 'Хрін', chip: 'Хрін', icon: '🌿', short: 'хрін', tip: 'Хрін: −12 і отрута на три ходи по −8 усім поруч' },
    // прикольна комора (опція столу «З приколами», прохід №3)
    { key: 'rooster', name: 'Півень', chip: 'Півень', icon: '🐓', short: 'півень', tip: 'Півень: летить майже прямо, та вітер зносить його вдвічі; −30' },
    { key: 'honey', name: 'Мед', chip: 'Мед', icon: '🍯', short: 'мед', tip: 'Мед: −10, і всі поруч прилипають — два ходи не рушать' },
    { key: 'twister', name: 'Смерч', chip: 'Смерч', icon: '🌪', short: 'смерч', tip: 'Смерч: розкидає хати довкола — хто з кручі, той і впаде' },
    { key: 'horseshoe', name: 'Підкова', chip: 'Підкова', icon: '🧲', short: 'підкова', tip: 'Підкова-магніт: де ляже, туди тягне чужі снаряди, аж до твого наступного пострілу' },
  ];
  /// Колір хвоста снаряда за видом; решта — глина.
  const TAILC = { 2: '#fff4df', 3: '#e8c25a', 5: '#8fd46a', 6: '#f2d27a', 7: '#e8a93a', 8: '#cfd6de', 9: '#aab4bd' };
  /// Підкова тягне чужі снаряди в цьому радіусі (як MagR на сервері).
  const MAG_R = 170;
  /// Мапи (опція «Погода й мапа»): свої кольори неба й землі поверх звичайних.
  const MAPS = {
    winter: { sky1: '#6f8fae', sky2: '#e6eef5', sun: '#fff8e8', far: '#9fb3c4', soil: '#6d5d52', soil2: '#3e342e', grass: '#f7fbff', water: '#8fc3e0' },
    night: { sky1: '#050a18', sky2: '#1b2742', sun: '#e9eef7', far: '#1d2b40', soil: '#3a2a1c', soil2: '#1d140c', grass: '#35573a', water: '#28496a' },
  };
  const SEATS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--gk-g', '#b8b8b8'], ['--gk-b', '#6fb3e8'], ['--gk-p', '#e88ac0']];
  const TEAM_FLAG = ['#e25b4a', '#4a8fe2'];
  const TEAM_TEXT = ['#ffa194', '#a3c8ff'];                        // ніки на полі в командах — світлі червоний і синій
  const SHORT_PRESS = 150, CHARGE_MS = 1500, MOVE_GAP = 170, AIM_GAP = 100, HURRY_MS = 5000;
  /// Перші 0,6 с свого ходу пробіл/Enter/Ⓐ не стріляють: після «Ще раз» Ⓐ, натиснутий по кнопці, якої вже нема,
  /// летів у гру й одразу стріляв 45°/60 «мимо».
  const TURN_GRACE = 600;
  const AWAY_MS = 200, CALM_MS = 50;   // цикл малювання: схована картка / тихе лобі й підсумок (spin)
  /// Емоції над хатою (прохід №3): поки ходить інший — 1–4, Ⓧ або кнопки пульта; сервер пускає одну на 1,5 с.
  const EMOS = ['😂', '😱', '😡', '👏'], EMO_MS = 2000, EMO_GAP = 1500;
  /// Камера за снарядом (прохід №3): на вузькому полі (телефон, < 700 px) у польоті плавно наближаємо снаряд
  /// і вибух (до ×2), потім назад. Приціл завжди на всю ширину — рогатка міряє лише рух пальця, не світ.
  const CAM_MAX_W = 700, CAM_Z = 2, CAM_BOOM_MS = 1100;

  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
  const lerp = (a, b, t) => a + (b - a) * t;
  const col = (x) => clamp(Math.floor(x / STEP), 0, COLS - 1);
  const sy = (y) => HGT - y;
  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : v; } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* приватне вікно — не біда */ } },
  };
  const calm = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const padOn = () => !!(window.HPad && (window.HPad.on || window.HPad.deck));
  const windText = (w) => (!w ? 'штиль' : w < 0 ? '⟵ ' + -w : w + ' ⟶');

  // =============================================================================================
  // Стан картки
  // =============================================================================================

  function newHut(i) {
    return { seat: i, x: 0, y: 0, hp: 0, alive: false, plays: false, team: i % 2, poison: 0, fuel: 0, skips: 0, reason: '',
      dx: 0, dy: 0, anim: null, ruined: false };
  }

  function state(root, ctx) {
    if (!root._gk) {
      const PN = 260;
      root._gk = {
        root, ctx, cv: null, g: null, cssW: 0, dpr: 1, s: 1, pw: 0, ph: 0,
        skyC: null, bgC: null, bgDirty: true, bgLo: -1, bgHi: -1, skyDirty: true, fonts: null, pal: null,
        view: null, phase: 'lobby', turn: null, round: 0, turnNo: 0, endsAt: null, turnMs: 30000, startIn: 0,
        wind: 0, waterFrom: 6, teams: false, t: 0,
        h: new Int16Array(COLS), hReady: false,
        wat: { from: 20, to: 20, t0: 0 },
        huts: [0, 1, 2, 3, 4, 5].map(newHut), nicks: ['', '', '', '', '', ''],
        inv: [], stats: [], wins: [0, 0, 0, 0, 0, 0], last: null, log: [], result: null,
        aimTurn: [45, 60, 0], my: { a: 45, p: 60, w: 0 }, myTurnNo: -1, preW: null, fired: -1,
        // снаряди: два останні кадри для екстраполяції й хвости
        shA: new Float32Array(72), shB: new Float32Array(72), shN: 0, shSmooth: false, shAt: 0, shReal: 0, gap: TICK_MS,
        tails: new Float32Array(24 * 12), tailN: new Uint8Array(24), tailAt: 0,
        volley: false, ready: [false, false, false, false, false, false], map: 'plain', kinds: 6, mag: [],
        trails: [[], [], [], [], [], []], shooter: -1,
        fresh: [],                                   // [c0, c1, t0] — свіжа земля у вирвах
        // частинки: пул без алокацій
        PN, pi: 0, px: new Float32Array(PN), py: new Float32Array(PN), pvx: new Float32Array(PN), pvy: new Float32Array(PN),
        pl: new Float32Array(PN), pm: new Float32Array(PN), pc: new Uint8Array(PN), ps: new Float32Array(PN), pg: new Float32Array(PN),
        rings: [], floats: [], shakeUntil: 0, banner: null, stork: null, smokeAt: 0,
        charge: null, holds: { a: null, p: null }, lastMove: 0, aimDirty: false, aimSent: '', aimSentAt: 0,
        drag: null, skipArm: 0, emos: [null, null, null, null, null, null], emoAt: 0, emoNext: 0,
        raf: 0, lastT: 0, keyup: null, ro: null, perf: new Float32Array(300), perfN: 0,
        sound: store.get('glekomet.sound', '0') === '1', ac: null, noise: null,
        els: null, calm: calm(),
      };
    }
    root._gk.ctx = ctx;
    ctx._gk = root._gk;
    return root._gk;
  }

  const me = (st) => (st.ctx && st.ctx.mine ? st.ctx.seat : null);
  const nickOf = (st, i) => (st.ctx && st.ctx.nickOf(i)) || st.nicks[i] || (st.ctx ? st.ctx.seatName(i) : '');
  function myTurn(st) {
    const c = st.ctx;
    const s = me(st);
    // «Залп»: цілиться кожна жива хата, поки не зарядила постріл
    const mine = st.volley ? !st.ready[s] : st.turn === s;
    return !!c && c.playing && s != null && st.phase === 'aim' && mine && st.huts[s].alive && st.fired !== st.turnNo;
  }
  /// Хід щойно почався — клавіші й Ⓐ ще не стріляють (див. TURN_GRACE).
  const early = (st) => performance.now() - (st.turnAt || 0) < TURN_GRACE;
  const invOf = (st, seat) => (seat != null && st.inv[seat]) || [-1, 0, 0, 0, 0, 0];

  // =============================================================================================
  // Палітра й шрифти (у світових одиницях, але розміром у справжніх пікселях)
  // =============================================================================================

  function palette(st) {
    const c = st.ctx.css;
    return {
      sky1: c('--gk-sky1', '#20405a'), sky2: c('--gk-sky2', '#e9b872'), sun: c('--gk-sun', '#ffe3a1'),
      far: c('--gk-far', '#3d5f55'), soil: c('--gk-soil', '#6b4a2e'), soil2: c('--gk-soil2', '#3f2a19'),
      grass: c('--gk-grass', '#6fae4f'), water: c('--gk-water', '#3f7fb0'), wall: c('--gk-wall', '#f1ece0'),
      ink: c('--gk-ink', '#1b1510'), text: c('--text', '#ecf1ea'), muted: c('--muted', '#9db3a5'),
      accent: c('--accent', '#f4c542'), danger: c('--danger', '#e57373'), ok: c('--ok', '#7bd389'), clay: c('--clay', '#c5763a'),
      seats: SEATS.map(([n, f]) => c(n, f)),
      // кольори частинок за індексом (pc): 0 глина, 1 земля, 2 тісто, 3 хрін, 4 солома, 5 вода, 6 уламки, 7 дим, 8 пір'я
      parts: [c('--clay', '#c5763a'), c('--gk-soil', '#6b4a2e'), '#fff4df', '#8fd46a', '#e8c25a', '#9fd0f0', '#2a211b', 'rgba(215,215,215,1)', '#ffffff'],
      ...(MAPS[st.map] || {}),
    };
  }

  function fonts(st) {
    const k = 1 / st.s;
    const f = (w, px) => w + ' ' + (px * k).toFixed(2) + 'px system-ui, -apple-system, "Segoe UI", sans-serif';
    const small = st.cssW < 520;
    return {
      nick: f(600, small ? 9 : 11), label: f(600, small ? 9 : 11), dmg: f(800, small ? 12 : 15),
      big: f(800, small ? 16 : 22), count: f(800, small ? 40 : 64), wind: f(700, small ? 9 : 12), tiny: f(500, small ? 8 : 10),
      // рядок пострілу на телефоні — дрібніше й у два рядки: 16 px в один рядок ширшали за поле й накривали його
      banner: f(800, small ? 12 : 22), emo: f(400, small ? 20 : 28), small,
      k,
    };
  }

  // =============================================================================================
  // Небо (раз на розмір) і земля (лише коли змінилась)
  // =============================================================================================

  function paintSky(st) {
    const c = st.skyC, g = c.getContext('2d'), pal = st.pal;
    g.setTransform(c.width / W, 0, 0, c.height / HGT, 0, 0);
    const grd = g.createLinearGradient(0, 0, 0, HGT);
    grd.addColorStop(0, pal.sky1);
    grd.addColorStop(0.72, pal.sky2);
    grd.addColorStop(1, pal.sky2);
    g.fillStyle = grd;
    g.fillRect(0, 0, W, HGT);
    // сонце над обрієм: м'яке сяйво й диск (уночі — місяць без сяйва)
    if (st.map !== 'night') {
      const sun = g.createRadialGradient(170, sy(330), 6, 170, sy(330), 90);
      sun.addColorStop(0, 'rgba(255, 236, 180, .95)');
      sun.addColorStop(0.25, 'rgba(255, 220, 150, .45)');
      sun.addColorStop(1, 'rgba(255, 220, 150, 0)');
      g.fillStyle = sun;
      g.fillRect(60, sy(440), 220, 220);
    }
    g.fillStyle = pal.sun;
    g.beginPath();
    g.arc(170, sy(330), 22, 0, Math.PI * 2);
    g.fill();
    // три пасма далеких пагорбів — чим ближче, тим темніше
    const hills = [[250, 34, 0.0031, 1.3, 0.28], [215, 28, 0.0047, 4.1, 0.42], [180, 22, 0.0069, 2.2, 0.58]];
    g.fillStyle = pal.far;
    for (const [base, amp, fr, ph, alpha] of hills) {
      g.globalAlpha = alpha;
      g.beginPath();
      g.moveTo(0, HGT);
      for (let x = 0; x <= W; x += 20) g.lineTo(x, sy(base + amp * Math.sin(x * fr * 2 * Math.PI / 2 + ph) + amp * 0.4 * Math.sin(x * fr * 5.3 + ph * 2)));
      g.lineTo(W, HGT);
      g.closePath();
      g.fill();
    }
    g.globalAlpha = 1;
    st.skyDirty = false;
    dirtyAll(st);
  }

  /// Сталий «шум» для камінців і трави: від номера колонки, щоб при кожному перемальовуванні лягали там само.
  const hash = (n) => { let x = (n * 374761393 + 668265263) | 0; x = (x ^ (x >>> 13)) * 1274126177 | 0; return ((x ^ (x >>> 16)) >>> 0) / 4294967296; };

  function surfacePath(g, h, dy) {
    g.moveTo(-2, sy(h[0] - dy));
    for (let c = 0; c < COLS; c++) g.lineTo(c * STEP + 2, sy(h[c] - dy));
    g.lineTo(W + 2, sy(h[COLS - 1] - dy));
  }

  /// Земля змінилась у колонках lo..hi: перемалюємо лише цю смугу (вирва — десяток колонок, а не все поле).
  function dirtyGround(st, lo, hi) {
    if (!st.bgDirty) { st.bgDirty = true; st.bgLo = lo; st.bgHi = hi; return; }
    if (st.bgLo < 0) return;
    st.bgLo = Math.min(st.bgLo, lo);
    st.bgHi = Math.max(st.bgHi, hi);
  }
  const dirtyAll = (st) => { st.bgDirty = true; st.bgLo = -1; };

  function paintGround(st) {
    const c = st.bgC, g = c.getContext('2d'), pal = st.pal, h = st.h;
    g.save();
    g.setTransform(1, 0, 0, 1, 0, 0);
    if (st.bgLo >= 0) {
      // смуга з запасом на товщину трави, шарів і кущиків
      const x0 = Math.max(0, Math.floor(((st.bgLo - 3) * STEP * c.width) / W));
      const x1 = Math.min(c.width, Math.ceil(((st.bgHi + 4) * STEP * c.width) / W));
      g.beginPath();
      g.rect(x0, 0, x1 - x0, c.height);
      g.clip();
      g.drawImage(st.skyC, x0, 0, x1 - x0, c.height, x0, 0, x1 - x0, c.height);
    } else g.drawImage(st.skyC, 0, 0);
    g.setTransform(c.width / W, 0, 0, c.height / HGT, 0, 0);
    paintTerrain(st, g, pal, h);
    g.restore();
    st.bgDirty = false;
    st.bgLo = st.bgHi = -1;
  }

  function paintTerrain(st, g, pal, h) {
    if (!st.hReady) return;
    // земля: заливка від поверхні до дна з темнішим низом
    const soil = g.createLinearGradient(0, sy(340), 0, HGT);
    soil.addColorStop(0, pal.soil);
    soil.addColorStop(1, pal.soil2);
    g.beginPath();
    surfacePath(g, h, 0);
    g.lineTo(W + 2, HGT + 2);
    g.lineTo(-2, HGT + 2);
    g.closePath();
    g.fillStyle = soil;
    g.fill();
    // шари ґрунту на глибині 12 і 30 u
    g.lineJoin = 'round';
    g.strokeStyle = 'rgba(0, 0, 0, .16)';
    g.lineWidth = 4;
    g.beginPath(); surfacePath(g, h, 12); g.stroke();
    g.strokeStyle = 'rgba(0, 0, 0, .13)';
    g.lineWidth = 7;
    g.beginPath(); surfacePath(g, h, 32); g.stroke();
    // камінці — на сталих місцях світу; вирва, що до них дійшла, їх просто «вибрала»
    g.fillStyle = 'rgba(30, 20, 12, .45)';
    for (let i = 0; i < 70; i++) {
      const x = hash(i * 3 + 1) * W, y = 20 + hash(i * 3 + 2) * 300, r = 1.4 + hash(i * 3 + 3) * 2.6;
      if (y + r + 6 > h[col(x)]) continue;
      g.beginPath();
      g.ellipse(x, sy(y), r * 1.4, r, 0, 0, Math.PI * 2);
      g.fill();
    }
    // трава по гребеню й кущики
    g.strokeStyle = pal.grass;
    g.lineWidth = 3.2;
    g.lineCap = 'round';
    g.beginPath(); surfacePath(g, h, 1); g.stroke();
    g.strokeStyle = 'rgba(255, 255, 255, .16)';
    g.lineWidth = 1;
    g.beginPath(); surfacePath(g, h, -0.8); g.stroke();
    g.strokeStyle = pal.grass;
    g.lineWidth = 1.3;
    g.beginPath();
    for (let cc = 3; cc < COLS - 3; cc += 7) {
      const r = hash(cc + 991);
      if (r < 0.35 || Math.abs(h[cc + 1] - h[cc - 1]) > 6) continue;
      const x = cc * STEP + 2, y = h[cc];
      g.moveTo(x - 2, sy(y)); g.lineTo(x - 3.5, sy(y + 5 + r * 3));
      g.moveTo(x, sy(y)); g.lineTo(x + 0.5, sy(y + 7 + r * 3));
      g.moveTo(x + 2, sy(y)); g.lineTo(x + 3.8, sy(y + 5 + r * 2));
    }
    g.stroke();
    g.lineCap = 'butt';
    if (st.map === 'fair') paintFair(g, h);
  }

  /// Ярмарок: два стовпи над берегами ставка й гірлянда прапорців між ними.
  function paintFair(g, h) {
    const xa = W / 2 - 104, xb = W / 2 + 104;
    const ya = h[col(xa)] + 72, yb = h[col(xb)] + 72;
    g.strokeStyle = '#6b4a2e';
    g.lineWidth = 3;
    g.beginPath();
    g.moveTo(xa, sy(h[col(xa)])); g.lineTo(xa, sy(ya));
    g.moveTo(xb, sy(h[col(xb)])); g.lineTo(xb, sy(yb));
    g.stroke();
    const cx = W / 2, cy = Math.min(ya, yb) - 34;
    g.strokeStyle = 'rgba(40, 30, 20, .8)';
    g.lineWidth = 1.2;
    g.beginPath(); g.moveTo(xa, sy(ya)); g.quadraticCurveTo(cx, sy(cy), xb, sy(yb)); g.stroke();
    const flags = ['#e25b4a', '#f4c542', '#4a8fe2', '#7bd389'];
    for (let k = 1; k < 14; k++) {
      const t = k / 14, u = 1 - t;
      const x = u * u * xa + 2 * u * t * cx + t * t * xb, y = u * u * ya + 2 * u * t * cy + t * t * yb;
      g.fillStyle = flags[k % 4];
      g.beginPath(); g.moveTo(x - 5, sy(y)); g.lineTo(x + 5, sy(y)); g.lineTo(x, sy(y - 11)); g.closePath(); g.fill();
    }
  }

  // =============================================================================================
  // Канвас у розмір екрана
  // =============================================================================================

  function fit(st) {
    const el = st.cv && st.cv.el;
    if (!el) return;
    const cssW = Math.round(el.getBoundingClientRect().width);
    if (cssW < 40) return;                                          // картка схована — розміру нема
    // Щільність рахуємо самі, зі стелею 2, і порівнюємо пікселі, а не DPR. Раніше розмір ставив ui.canvas (стеля 3),
    // а порівнювали з min(2, DPR): на телефонах із DPR 2,6–3 це не сходилось ніколи, і кожен update() (кожен вид,
    // кожна подія лобі, кожен сигнал ResizeObserver) наново виділяв небо й землю й малював їх з нуля — ривок саме
    // тоді, коли всі стежать за глеком. Тепер буфери перевиділяються лише тоді, коли справді змінився розмір.
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const pw = Math.round(cssW * dpr), ph = Math.round((cssW * dpr) / 2);
    if (pw === st.pw && ph === st.ph && st.bgC) return;
    st.cssW = cssW;
    if (el.width !== pw) el.width = pw;
    if (el.height !== ph) el.height = ph;
    st.pw = pw;
    st.ph = ph;
    st.dpr = pw / cssW;
    st.s = cssW / W;
    st.fits = (st.fits || 0) + 1;                                   // для живої перевірки: скільки разів перевиділяли
    if (!st.skyC) { st.skyC = document.createElement('canvas'); st.bgC = document.createElement('canvas'); }
    st.skyC.width = st.bgC.width = pw;
    st.skyC.height = st.bgC.height = ph;
    st.pal = palette(st);
    st.fonts = fonts(st);
    st.skyDirty = true;
    dirtyAll(st);
  }

  /// Поле так, щоб уся картка (смужка, поле, комора, пульт, статус, кнопки) влізла в екран: запас під решту
  /// міряємо, а не вгадуємо — на шістьох чіпи місць ідуть у два рядки, і сталий запас CSS уже не рятує.
  /// Телефон і низьке вікно — не чіпаємо: там гортати сторінку нормально.
  function fitHeight(st) {
    const el = st.cv && st.cv.el;
    const card = el && el.closest('.gtable');
    if (!card || !el.offsetParent) return;
    const vh = window.innerHeight;
    if (vh < 460 || window.innerWidth < 700) { if (el.style.maxWidth) el.style.maxWidth = ''; return; }
    const cr = el.getBoundingClientRect();
    const cardBottom = card.getBoundingClientRect().bottom;
    let bottom = cardBottom;
    // Що сторінка має ще під карткою (нижній відступ main тощо): міряємо, коли сторінка вже вилазить за екран, —
    // тоді scrollHeight задає вміст. Сталі «+10» не вистачало: на Deck і на Full HD у підсумку лишалось 8–11 px прокрутки.
    const doc = document.documentElement;
    if (doc.scrollHeight > vh + 1) st.tail = clamp(doc.scrollHeight - (cardBottom + window.scrollY), 0, 60);
    const tail = st.tail != null ? st.tail : 16;
    // Підказку про телефон не рахуємо (вона на хвилинку). Підсумок рахуємо, але поле заради нього меншає не
    // більше ніж до 60 %: на Full HD «Ще раз» тоді видно без прокрутки, а на Deck поле не стискається в марку
    // (там таблицю на шістьох трохи догортаємо).
    const tip = st.els.tip, sum = st.els.sum;
    if (tip && !tip.hidden) bottom -= tip.getBoundingClientRect().height + 8;
    const sumH = sum && !sum.hidden ? sum.getBoundingClientRect().height + 8 : 0;
    const other = bottom + window.scrollY - cr.height + Math.ceil(tail) + 4;
    const full = clamp((vh - other + sumH) * 2, 480, 1200);
    const want = Math.floor(sumH ? Math.max(clamp((vh - other) * 2, 480, 1200), full * 0.6) : full);
    const cur = parseFloat(el.style.maxWidth) || 0;
    if (Math.abs(want - cur) >= 6) el.style.maxWidth = want + 'px';
  }

  /// gk-grab (touch-action: none) — лише у свій хід: у чужий хід і після руїни свайп по полю гортає сторінку.
  const canvasCls = (st) => 'gk-canvas' + (st.ctx && myTurn(st) ? ' gk-grab' : '') + (st.drag ? ' gk-pull' : '');
  function syncCanvasCls(st) {
    if (!st.cv) return;
    const want = 'gcanvas ' + canvasCls(st);
    if (st.cv.el.className !== want) st.cv.el.className = want;
  }

  // =============================================================================================
  // Частинки, кільця, цифри шкоди
  // =============================================================================================

  function spawn(st, x, y, vx, vy, life, c, size, grav) {
    if (st.calm) return;
    const i = st.pi;
    st.pi = (i + 1) % st.PN;
    st.px[i] = x; st.py[i] = y; st.pvx[i] = vx; st.pvy[i] = vy;
    st.pl[i] = life; st.pm[i] = life; st.pc[i] = c; st.ps[i] = size; st.pg[i] = grav;
  }

  function burst(st, x, y, n, c, speed, up, life, size, grav) {
    for (let k = 0; k < n; k++) {
      const a = Math.random() * Math.PI * (up ? 1 : 2);
      const v = speed * (0.35 + Math.random() * 0.65);
      spawn(st, x, y, Math.cos(a) * v, Math.sin(a) * v * (up ? 1 : 0.8) + (up ? 20 : 0), life * (0.6 + Math.random() * 0.5), c, size * (0.6 + Math.random() * 0.7), grav);
    }
  }

  function ring(st, x, y, r, color, dur) {
    if (st.rings.length > 10) st.rings.shift();
    st.rings.push({ x, y, r: Math.max(8, r), color, t0: performance.now(), dur: dur || 260 });
  }

  function float(st, x, y, text, color) {
    const now = performance.now();
    // дві скалки за край — один напис, а не два один на одному
    for (const f of st.floats) if (f.text === text && now - f.t0 < 600 && Math.abs(f.x - x) < 160) return;
    if (st.floats.length > 12) st.floats.shift();
    st.floats.push({ x, y, text, color, t0: now });
  }

  function shake(st, ms) { if (!st.calm) st.shakeUntil = Math.max(st.shakeUntil, performance.now() + ms); }

  /// Вибух із кадру: що полетіло, те й бахнуло — кольори й шум за снарядом.
  function blast(st, x, y, r, kind) {
    const pal = st.pal;
    if (kind !== 'out' && kind !== 'cloud') st.camBoom = { x, y, t0: performance.now() };
    switch (kind) {
      case 'pot':
        ring(st, x, y, r, pal.clay);
        burst(st, x, y, 14, 0, 170, true, 0.9, 3.2, 320);
        burst(st, x, y, 10, 1, 120, true, 0.8, 2.4, 260);
        sfx(st, 'boom', 0.8);
        break;
      case 'shards':
        ring(st, x, y, r, pal.clay, 200);
        burst(st, x, y, 8, 0, 150, true, 0.7, 2.6, 320);
        burst(st, x, y, 6, 1, 100, true, 0.6, 2, 260);
        sfx(st, 'boom', 0.5);
        break;
      case 'varenyk':
        ring(st, x, y, r, '#fff4df', 320);
        st.rings.push({ x, y, r: r * 0.9, color: '#fffaf0', t0: performance.now(), dur: 220, flash: true });
        burst(st, x, y, 16, 2, 210, true, 1.1, 4, 300);
        burst(st, x, y, 12, 1, 160, true, 0.9, 3, 260);
        shake(st, 180);
        sfx(st, 'boom', 1.4);
        break;
      case 'rooster':
        ring(st, x, y, r, '#f2d27a');
        burst(st, x, y, 10, 0, 150, true, 0.8, 3, 320);
        burst(st, x, y, 12, 8, 110, true, 1.3, 3, 50);
        sfx(st, 'boom', 0.7);
        break;
      case 'honey':
        ring(st, x, y, 50, '#e8a93a', 420);
        for (let k = 0; k < 14; k++) spawn(st, x + (Math.random() - 0.5) * 40, y + 6, (Math.random() - 0.5) * 60, 30 + Math.random() * 40, 1.1, 4, 3.4, 260);
        sfx(st, 'thud', 0.6);
        break;
      case 'twister':
        ring(st, x, y, 110, '#cfd6de', 520);
        for (let k = 0; k < 24; k++) {
          const an = (k / 24) * Math.PI * 2;
          spawn(st, x + Math.cos(an) * 16, y + k * 3, -Math.sin(an) * 120, 60 + Math.random() * 60, 1.2, 7, 5, -10);
        }
        shake(st, 140);
        sfx(st, 'boom', 0.5);
        break;
      case 'horseshoe':
        ring(st, x, y, 24, '#aab4bd', 300);
        burst(st, x, y, 8, 1, 90, true, 0.6, 2, 260);
        sfx(st, 'thud', 0.5);
        break;
      case 'khrin':
        ring(st, x, y, 40, '#8fd46a', 420);
        for (let k = 0; k < 12; k++) spawn(st, x + (Math.random() - 0.5) * 30, y + Math.random() * 16, (Math.random() - 0.5) * 30, 8 + Math.random() * 14, 1.2 + Math.random() * 0.4, 3, 9 + Math.random() * 7, 0);
        sfx(st, 'boom', 0.45);
        break;
      case 'hay':
        for (let k = 0; k < 20; k++) spawn(st, x + (Math.random() - 0.5) * 40, y + 20 + Math.random() * 30, (Math.random() - 0.5) * 50, Math.random() * 40, 1 + Math.random() * 0.6, 4, 3.5, 90);
        sfx(st, 'thud', 0.6);
        break;
      case 'stork':
        for (let k = 0; k < 6; k++) spawn(st, x + (Math.random() - 0.5) * 16, y + 10, (Math.random() - 0.5) * 40, 20 + Math.random() * 20, 1.4, 8, 3.2, 40);
        sfx(st, 'stork', 0.7);
        break;
      case 'splash':
        burst(st, x, y, 12, 5, 120, true, 0.8, 2.6, 300);
        ring(st, x, y, 14, pal.water, 300);
        sfx(st, 'splash', 0.7);
        break;
      case 'out':
        float(st, clamp(x, 60, W - 60), clamp(y, 40, HGT - 40), 'у сусіднє село ↗', pal.text);
        break;
      case 'cloud':
        float(st, clamp(x, 60, W - 60), HGT - 40, 'у хмари…', pal.text);
        break;
      default:
    }
  }

  function ruin(st, i, why) {
    const hut = st.huts[i];
    if (hut.ruined) return;
    hut.ruined = true;
    if (why === 'drown') { blast(st, hut.x, st.wat.to, 20, 'splash'); return; }
    if (why === 'left' || why === 'afk') { burst(st, hut.x, hut.y + 14, 10, 7, 40, true, 1.6, 6, -10); return; }
    burst(st, hut.x, hut.y + 16, 20, 6, 160, true, 1.1, 3.4, 300);
    for (let k = 0; k < 10; k++) spawn(st, hut.x + (Math.random() - 0.5) * 30, hut.y + 20, (Math.random() - 0.5) * 16, 14 + Math.random() * 16, 2 + Math.random(), 7, 8 + Math.random() * 6, -4);
    shake(st, 180);
  }

  // =============================================================================================
  // Звук: лише синтез, тихо, після жесту, типово вимкнено
  // =============================================================================================

  function audio(st) {
    if (!st.sound) return null;
    if (!st.ac) {
      try { st.ac = new (window.AudioContext || window.webkitAudioContext)(); } catch { return null; }
    }
    if (st.ac.state === 'suspended') st.ac.resume().catch(() => {});
    return st.ac;
  }

  function noiseBuf(ac, st) {
    if (st.noise) return st.noise;
    const b = ac.createBuffer(1, ac.sampleRate * 0.4, ac.sampleRate);
    const d = b.getChannelData(0);
    for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
    st.noise = b;
    return b;
  }

  function sfx(st, kind, k) {
    const ac = audio(st);
    if (!ac) return;
    const t = ac.currentTime, out = ac.createGain();
    out.connect(ac.destination);
    const vol = Math.min(0.25, 0.14 * (k || 1));
    const tone = (type, f0, f1, dur, v, at) => {
      const o = ac.createOscillator(), gn = ac.createGain();
      o.type = type;
      o.frequency.setValueAtTime(f0, t + (at || 0));
      o.frequency.exponentialRampToValueAtTime(f1, t + (at || 0) + dur);
      gn.gain.setValueAtTime(v, t + (at || 0));
      gn.gain.exponentialRampToValueAtTime(0.0001, t + (at || 0) + dur);
      o.connect(gn); gn.connect(out);
      o.start(t + (at || 0)); o.stop(t + (at || 0) + dur + 0.02);
    };
    const hiss = (freq, dur, v, type) => {
      const s = ac.createBufferSource(), f = ac.createBiquadFilter(), gn = ac.createGain();
      s.buffer = noiseBuf(ac, st);
      f.type = type || 'bandpass';
      f.frequency.value = freq;
      gn.gain.setValueAtTime(v, t);
      gn.gain.exponentialRampToValueAtTime(0.0001, t + dur);
      s.connect(f); f.connect(gn); gn.connect(out);
      s.start(t); s.stop(t + dur + 0.02);
    };
    switch (kind) {
      case 'launch': hiss(1500, 0.12, vol * 0.8); break;
      case 'boom': tone('sine', 90, 40, 0.25 * Math.max(1, k), vol); hiss(400, 0.18, vol * 0.5, 'lowpass'); break;
      case 'thud': tone('sine', 120, 70, 0.12, vol * 0.7); break;
      case 'splash': hiss(800, 0.18, vol, 'lowpass'); break;
      case 'stork': tone('triangle', 900, 880, 0.07, vol * 0.6); tone('triangle', 1100, 1080, 0.07, vol * 0.6, 0.11); break;
      case 'step': tone('square', 600, 480, 0.04, vol * 0.2); break;
      case 'turn': tone('sine', 660, 880, 0.12, vol * 0.5); break;
      case 'tick': tone('square', 1250, 1150, 0.035, vol * 0.25); break;
      default:
    }
  }

  // =============================================================================================
  // Вид і кадр
  // =============================================================================================

  function applyView(st, v) {
    st.view = v;
    const was = st.phase;
    const newGame = (v.phase === 'start' && was !== 'start') || (v.turnNo || 0) < st.turnNo;
    st.phase = v.phase;
    st.turn = v.turn;
    st.round = v.round || 0;
    st.endsAt = v.endsAt;
    st.endsMs = v.endsAt ? Date.parse(v.endsAt) || 0 : 0;
    st.turnMs = v.turnMs || 30000;
    st.startIn = v.startIn || 0;
    st.wind = v.wind || 0;
    st.waterFrom = v.waterFrom;
    st.teams = !!v.teams;
    st.inv = v.inv || [];
    st.stats = v.stats || [];
    st.wins = v.wins || st.wins;
    st.log = v.log || [];
    st.result = v.result || null;
    st.last = v.last || null;
    if (v.aim) st.aimTurn = v.aim;
    st.volley = v.mode === 'volley';
    st.kinds = v.kinds || 6;
    st.mag = v.mag || [];
    if (v.ready) for (let i = 0; i < 6; i++) st.ready[i] = !!v.ready[i];
    const map = v.map === 'mix' ? 'plain' : v.map || 'plain';
    if (map !== st.map) { st.map = map; if (st.pal) { st.pal = palette(st); st.skyDirty = true; } }
    if (newGame) {
      st.trails = [[], [], [], [], [], []];
      st.fired = -1;
      st.myTurnNo = -1;
      st.preW = null;
      st.banner = null;
      st.stork = null;
      st.shN = 0;
      st.best = null;
      st.bestTurn = -1;
      st.replay = null;
      st.replayChecked = false;
      st.shotTurn = -1;
      for (const hut of st.huts) { hut.ruined = false; hut.anim = null; }
      if (me(st) != null) requestAnimationFrame(() => bringIntoView(st));
    }
    st.turnNo = v.turnNo || 0;

    // земля: повна карта з виду — істина; кадри між видами лише дописують зміни
    const h = v.h || [];
    let lo = COLS, hi = -1;
    for (let c = 0; c < COLS && c < h.length; c++) if (st.h[c] !== h[c]) { st.h[c] = h[c]; if (c < lo) lo = c; hi = c; }
    if (!st.hReady || newGame) dirtyAll(st);
    else if (hi >= 0) dirtyGround(st, lo, hi);
    if (h.length === COLS) st.hReady = true;

    // вода: піднялась між ходами — підіймаємо плавно
    const water = v.water != null ? v.water : 20;
    if (water !== st.wat.to) {
      const now = performance.now();
      st.wat = { from: newGame || st.calm ? water : waterNow(st, now), to: water, t0: now };
    }

    for (let i = 0; i < 6; i++) {
      const src = (v.huts || [])[i];
      const hut = st.huts[i];
      if (!src) continue;
      const nick = st.ctx.nickOf(i) || src.nick;
      if (nick) st.nicks[i] = nick;
      hut.plays = src.alive || !!src.reason;
      if (hut.alive && !src.alive && hut.plays && st.phase !== 'lobby' && !newGame) ruin(st, i, src.reason);
      if (!src.alive && src.reason && newGame) hut.ruined = true;
      hut.hp = src.hp; hut.alive = src.alive; hut.team = src.team; hut.poison = src.poison;
      hut.fuel = src.fuel; hut.skips = src.skips; hut.reason = src.reason;
      hut.honey = src.honey || 0; hut.stuck = !!src.stuck;
      if (!hut.alive && hut.plays && src.hp === 0) hut.ruined = true;
      if (hut.x !== src.x || hut.y !== src.y) {
        hut.x = src.x; hut.y = src.y;
        if (!hut.anim && !(st.stork && st.stork.seat === i)) { hut.dx = src.x; hut.dy = src.y; }
      }
      if (newGame) { hut.dx = src.x; hut.dy = src.y; }
    }

    // снаряди з виду (F5 посеред польоту)
    if (st.phase === 'fly' && st.shotTurn !== st.turnNo) startShot(st);   // за номером ходу: той самий стрілець двічі поспіль теж новий слід
    if (st.phase === 'fly' && v.shells && v.shells.length && !st.shN) takeShells(st, v.shells, false);
    if (st.phase !== 'fly') st.shN = 0;

    // рядок про постріл — великим на полі, поки пауза
    const text = v.last && v.last.text;
    if (text && st.phase === 'settle' && (!st.banner || st.banner.text !== text || st.banner.turnNo !== st.turnNo))
      st.banner = { text, t0: performance.now(), turnNo: st.turnNo, w: 0 };
    // найкращий постріл партії: запам'ятовуємо його слід (точки кадрів), щоб у підсумку програти ще раз
    if (st.phase === 'settle' && v.last && v.last.w >= 0 && st.bestTurn !== st.turnNo) {
      st.bestTurn = st.turnNo;
      const by = v.last.by;
      let dmg = 0;
      for (const hit of v.last.hits || []) if (hit.kind === 'hit' && hit.seat !== by && !(st.teams && hit.seat % 2 === by % 2)) dmg += hit.dmg;
      const tr = st.trails[by];
      if (dmg > 0 && (!st.best || dmg > st.best.dmg) && tr && tr.length >= 4) st.best = { seat: by, dmg, pts: tr.slice() };
    }
    // фаза over приходить кадром раніше за вид, тож «was !== over» тут не спрацює — прапорець на партію
    if (st.phase === 'over' && st.result && !st.replayChecked) {
      st.replayChecked = true;
      const b = st.result.best, mine = st.best;
      st.replay = b && mine && b.seat === mine.seat && b.dmg === mine.dmg ? { pts: mine.pts, seat: mine.seat, dmg: mine.dmg, t0: performance.now() + 700, boom: false } : null;
    }

    // мій хід: приціл — з минулого пострілу, снаряд — наперед обраний у коморі, якщо ще є
    const s = me(st);
    // «Залп»: сервер чужих (і наших) прицілів не шле — беремо свій минулий, а в першому залпі — у бік села
    if (st.phase === 'aim' && s != null && (st.volley ? st.huts[s].alive && !st.ready[s] : st.turn === s) && st.turnNo !== st.myTurnNo) {
      const a = v.aim || (st.myTurnNo < 0 ? [st.huts[s].x < W / 2 ? 45 : 135, 60, 0] : [st.my.a, st.my.p, st.my.w]);
      st.myTurnNo = st.turnNo;
      st.turnAt = performance.now();
      st.my = { a: a[0], p: a[1], w: a[2] };
      const inv = invOf(st, s);
      if (st.preW != null && inv[st.preW] !== 0 && st.preW !== st.my.w) { st.my.w = st.preW; st.aimDirty = true; }
      if (inv[st.my.w] === 0) { st.my.w = 0; st.aimDirty = true; }
      st.preW = null;
      st.charge = null;
      st.holds.a = st.holds.p = null;
      st.aimSent = st.my.a + '|' + st.my.p + '|' + st.my.w;
      if (was !== 'lobby') sfx(st, 'turn', 1);
      requestAnimationFrame(() => bringIntoView(st));
    }
  }

  /// Телефон боком: між липкою шапкою сайту й плеєром із вкладками внизу лишається смуга якраз на поле (CSS), але
  /// саме поле нижче шапки картки й чіпів — видно було смужку 50 px. На початку партії й свого ходу підкручуємо
  /// сторінку, щоб поле було в кадрі цілком (межі — scroll-margin канваса). Решта екранів — не чіпаємо.
  function bringIntoView(st) {
    const el = st.cv && st.cv.el;
    if (!el || !el.offsetParent || window.innerHeight > 480 || window.innerWidth <= window.innerHeight) return;
    const r = el.getBoundingClientRect(), cs = getComputedStyle(el);
    const top = parseFloat(cs.scrollMarginTop) || 0, bottom = parseFloat(cs.scrollMarginBottom) || 0;
    if (r.top >= top - 2 && r.bottom <= window.innerHeight - bottom + 2) return;
    el.scrollIntoView({ block: r.height <= window.innerHeight - top - bottom ? 'end' : 'start', behavior: st.calm ? 'auto' : 'smooth' });
  }

  function waterNow(st, now) {
    const w = st.wat;
    if (w.from === w.to) return w.to;
    const k = clamp((now - w.t0) / 1000, 0, 1);
    return lerp(w.from, w.to, k * (2 - k));
  }

  function startShot(st) {
    st.shotTurn = st.turnNo;
    st.shooter = st.volley ? -1 : st.turn;
    if (st.volley) st.trails = [[], [], [], [], [], []];
    else if (st.shooter != null && st.shooter >= 0) st.trails[st.shooter] = [];
    st.tailN.fill(0);
    sfx(st, 'launch', 1);
  }

  function takeShells(st, sh, smooth) {
    const n = Math.min(24, sh.length);
    if (smooth && n === st.shN) st.shA.set(st.shB);
    for (let i = 0; i < n; i++) { st.shB[i * 3] = sh[i][0]; st.shB[i * 3 + 1] = sh[i][1]; st.shB[i * 3 + 2] = sh[i][2]; }
    st.shSmooth = smooth && n === st.shN;
    if (n !== st.shN) st.tailN.fill(0);
    st.shN = n;
    // Справжній проміжок між кадрами: годинник сервера на Windows тикає рідше за 25/с (≈ 19/с), і
    // екстраполяція на сталі 40 мс доганяла б кадр і стояла — снаряд смикався б.
    const now = performance.now(), gap = now - st.shReal;
    if (st.shSmooth && gap > 15 && gap < 160) st.gap += (gap - st.gap) * 0.2;
    st.shReal = now;
    // Кадр — на рівну сітку часу (попередній + середній проміжок), а не «щойно прийшов»: кадри летять із тремтінням
    // 15–63 мс, і снаряд, що від кожного кадру рушав наново, то стояв, то стрибав (прохід 28.09: зупинок і ривків
    // на кадр екрана було 7–8 %). Сітка не відходить від справжнього приходу далі ніж на один проміжок.
    st.shAt = st.shSmooth ? clamp(st.shAt + st.gap, now - st.gap, now + st.gap) : now;
    // слід пострілу: точки кадрів, блідим пунктиром до наступного пострілу цього гравця
    // у «Залпі» кожен снаряд несе, чий він (4-те число), — і кожен стрілець має свій слід
    if (st.volley) {
      for (let i = 0; i < n; i++) {
        const t2 = st.trails[sh[i][3]];
        if (t2 && t2.length < 1600) t2.push(sh[i][0], sh[i][1]);
      }
      return;
    }
    const tr = st.shooter >= 0 ? st.trails[st.shooter] : null;
    if (tr && tr.length < 1600) for (let i = 0; i < n; i++) tr.push(sh[i][0], sh[i][1]);
  }

  function applyFrame(st, f) {
    const now = performance.now();
    st.t = f.t;
    const ph = f.ph;
    if (ph && ph !== st.phase) {
      if (ph === 'fly' && st.phase !== 'fly' && st.shotTurn !== st.turnNo) startShot(st);
      if (ph !== 'fly') st.shN = 0;
      st.phase = ph;
    }
    if (f.si != null) st.startIn = f.si;
    if (f.dh) {
      for (const run of f.dh) {
        const c0 = run[0];
        for (let k = 1; k < run.length; k++) if (c0 + k - 1 < COLS) st.h[c0 + k - 1] = run[k];
        if (st.fresh.length > 12) st.fresh.shift();
        st.fresh.push([c0, c0 + run.length - 2, now]);
        dirtyGround(st, c0, c0 + run.length - 2);
      }
    }
    const storkNow = f.ex && f.ex.some((e) => e[3] === 'stork');
    if (f.hx) {
      for (const [i, x, y] of f.hx) {
        const hut = st.huts[i];
        if (!hut) continue;
        const fromX = hut.anim ? hut.dx : hut.x, fromY = hut.anim ? hut.dy : hut.y;
        if (st.phase === 'aim' && (i === st.turn || st.volley) && x !== hut.x) { hut.fuel = Math.max(0, hut.fuel - 8); sfx(st, 'step', 1); }
        if (storkNow && (i === st.shooter || st.volley) && !st.calm) {
          st.stork = { seat: i, x0: hut.x, y0: hut.y, x1: x, y1: y, t0: now };
        } else if (!st.calm) {
          const drop = fromY - y;
          const dur = drop > 2 ? Math.min(600, 150 + drop * 5) : drop < -2 ? 300 : 140;
          hut.anim = { x0: fromX, y0: fromY, t0: now, dur, fall: drop > 2 };
        }
        hut.x = x; hut.y = y;
        if (st.calm || !hut.anim) { hut.dx = x; hut.dy = y; }
      }
    }
    if (f.ex) for (const e of f.ex) blast(st, e[0], e[1], e[2], e[3]);
    if (f.hp) {
      const tint = f.ex ? st.pal.danger : f.hx ? '#f0a35a' : '#8fd46a';
      for (let i = 0; i < 6 && i < f.hp.length; i++) {
        const hut = st.huts[i];
        const d = hut.hp - f.hp[i];
        if (d > 0 && hut.plays) float(st, hut.x, hut.y + 50 + 34 * st.fonts.k, '−' + d, tint);
        hut.hp = f.hp[i];
        if (hut.hp === 0 && hut.plays && hut.alive) { ruin(st, i, 'hit'); hut.alive = false; }
      }
    }
    if (f.aim) st.aimTurn = f.aim;
    if (f.em) for (const [i, e] of f.em) if (i >= 0 && i < 6 && EMOS[e]) st.emos[i] = { e, t0: now };
    if (f.rd != null) for (let i = 0; i < 6; i++) st.ready[i] = !!(f.rd & (1 << i));
    if (f.wl != null && f.wl !== st.wat.to) st.wat = { from: st.calm ? f.wl : waterNow(st, now), to: f.wl, t0: now };
    if (f.sh) takeShells(st, f.sh, true);
    else if (st.phase !== 'fly') st.shN = 0;
  }

  // =============================================================================================
  // Малювання
  // =============================================================================================

  function hutPos(st, hut, now) {
    const a = hut.anim;
    if (!a) { hut.dx = hut.x; hut.dy = hut.y; return; }
    const k = clamp((now - a.t0) / a.dur, 0, 1);
    const e = a.fall ? k * k : k * (2 - k);
    hut.dx = lerp(a.x0, hut.x, e);
    hut.dy = lerp(a.y0, hut.y, e);
    if (k >= 1) {
      hut.anim = null;
      if (a.fall && a.y0 - hut.y > 24) { burst(st, hut.x, hut.y, 10, 1, 90, true, 0.6, 2.4, 260); shake(st, 120); }
    }
  }

  /// Хата: колеса, біла мазанка, стріха кольору місця, димар, катапульта на даху (кут a).
  function drawHut(st, g, i, x, y, a, now) {
    const pal = st.pal, hut = st.huts[i], color = pal.seats[i];
    const Y = sy(y);
    if (!hut.alive) {
      // руїна: обгорілі стіни, уламки даху
      g.fillStyle = pal.ink;
      g.beginPath();
      g.moveTo(x - 17, Y - 2); g.lineTo(x - 17, Y - 14); g.lineTo(x - 11, Y - 18); g.lineTo(x - 6, Y - 11);
      g.lineTo(x + 1, Y - 16); g.lineTo(x + 8, Y - 9); g.lineTo(x + 14, Y - 15); g.lineTo(x + 17, Y - 10); g.lineTo(x + 17, Y - 2);
      g.closePath();
      g.fill();
      g.fillStyle = color;
      g.globalAlpha = 0.55;
      g.beginPath(); g.moveTo(x - 22, Y - 1); g.lineTo(x - 12, Y - 7); g.lineTo(x - 6, Y - 1); g.closePath(); g.fill();
      g.beginPath(); g.moveTo(x + 6, Y - 1); g.lineTo(x + 18, Y - 6); g.lineTo(x + 23, Y - 1); g.closePath(); g.fill();
      g.globalAlpha = 1;
      g.fillStyle = '#3a2d25';
      g.beginPath(); g.arc(x - 12, Y - 3, 3.5, 0, Math.PI * 2); g.fill();
      return;
    }
    if (st.teams) {
      // команда видна здалеку: прапор на димарі й смуга кольору команди під колесами (плюс колір ніка в табличці)
      g.strokeStyle = pal.ink; g.lineWidth = 1.2;
      g.beginPath(); g.moveTo(x + 8, Y - 36); g.lineTo(x + 8, Y - 53); g.stroke();
      g.fillStyle = TEAM_FLAG[i % 2];
      g.beginPath(); g.moveTo(x + 8, Y - 53); g.lineTo(x + 21, Y - 49); g.lineTo(x + 8, Y - 45); g.closePath(); g.fill();
      g.globalAlpha = 0.9;
      g.fillRect(x - 23, Y - 0.5, 46, 3.5);
      g.globalAlpha = 1;
    }
    // колеса
    g.fillStyle = pal.ink;
    g.beginPath(); g.arc(x - 12, Y - 5, 5, 0, Math.PI * 2); g.arc(x + 12, Y - 5, 5, 0, Math.PI * 2); g.fill();
    g.fillStyle = '#8a6a48';
    g.beginPath(); g.arc(x - 12, Y - 5, 1.8, 0, Math.PI * 2); g.arc(x + 12, Y - 5, 1.8, 0, Math.PI * 2); g.fill();
    // стіна
    g.fillStyle = pal.wall;
    g.fillRect(x - 17, Y - 23, 34, 17);
    g.strokeStyle = 'rgba(0, 0, 0, .35)';
    g.lineWidth = 0.8;
    g.strokeRect(x - 17, Y - 23, 34, 17);
    g.fillStyle = '#7a5230';
    g.fillRect(x - 12, Y - 19, 7, 13);                       // двері
    g.fillStyle = '#ffd66b';
    g.fillRect(x + 3, Y - 19, 9, 8);                         // віконце з вогником
    g.strokeStyle = '#6b4a2e';
    g.beginPath(); g.moveTo(x + 7.5, Y - 19); g.lineTo(x + 7.5, Y - 11); g.moveTo(x + 3, Y - 15); g.lineTo(x + 12, Y - 15); g.stroke();
    // димар
    g.fillStyle = '#a0522d';
    g.fillRect(x + 7, Y - 36, 4, 9);
    // стріха
    g.fillStyle = color;
    g.beginPath();
    g.moveTo(x - 21, Y - 22); g.lineTo(x + 21, Y - 22); g.lineTo(x + 12, Y - 33); g.lineTo(x - 12, Y - 33);
    g.closePath();
    g.fill();
    g.strokeStyle = 'rgba(0, 0, 0, .3)';
    g.beginPath(); g.moveTo(x - 16, Y - 26); g.lineTo(x + 16, Y - 26); g.moveTo(x - 13, Y - 30); g.lineTo(x + 13, Y - 30); g.stroke();
    // катапульта: важіль із ковшиком
    const r = (a * Math.PI) / 180;
    const bx = x - 2 + Math.cos(r) * 11, by = Y - 33 - Math.sin(r) * 11;
    g.strokeStyle = '#6b4222';
    g.lineWidth = 2.2;
    g.lineCap = 'round';
    g.beginPath(); g.moveTo(x - 2, Y - 33); g.lineTo(bx, by); g.stroke();
    g.lineCap = 'butt';
    g.fillStyle = '#6b4222';
    g.beginPath(); g.arc(bx, by, 2.6, 0, Math.PI * 2); g.fill();
    if (st.phase === 'aim' && st.turn === i) {
      g.fillStyle = pal.clay;
      g.beginPath(); g.arc(bx, by - 1.5, 2.2, 0, Math.PI * 2); g.fill();
    }
    if (hut.poison > 0) {
      g.fillStyle = '#8fd46a';
      g.globalAlpha = 0.22 + 0.13 * Math.sin(now / 220);
      g.beginPath(); g.ellipse(x, Y - 18, 28, 22, 0, 0, Math.PI * 2); g.fill();
      g.globalAlpha = 1;
    }
  }

  /// Нік, смужка здоров'я, «ти» — над хатою, розміром у справжніх пікселях (на телефоні теж читається).
  function drawTag(st, g, i, x, y, now, mine, lift, tw) {
    const hut = st.huts[i], F = st.fonts, k = F.k, pal = st.pal;
    // нік біля краю поля не обрізаємо: зсуваємо всередину (смужка здоров'я лишається над хатою)
    const lx = tw ? clamp(x, tw / 2 + 3 * k, W - tw / 2 - 3 * k) : x;
    const Y = sy(y);
    const top = Y - 50 - (lift || 0);
    if (hut.alive) {
      const bw = 30, bh = Math.max(3.5, 3 * k);
      g.fillStyle = 'rgba(0, 0, 0, .55)';
      g.fillRect(x - bw / 2 - 0.8, top - bh - 0.8, bw + 1.6, bh + 1.6);
      g.fillStyle = hut.hp > 60 ? pal.ok : hut.hp > 30 ? pal.accent : pal.danger;
      g.fillRect(x - bw / 2, top - bh, (bw * clamp(hut.hp, 0, 100)) / 100, bh);
    }
    const name = nickOf(st, i);
    g.font = F.nick;
    g.textAlign = 'center';
    g.textBaseline = 'bottom';
    g.lineJoin = 'round';
    g.lineWidth = 3 * k;
    g.strokeStyle = 'rgba(10, 16, 12, .85)';
    const ty = top - (hut.alive ? 5.5 * k : 0);
    const label = tagLabel(st, name);
    g.strokeText(label, lx, ty);
    g.fillStyle = hut.alive ? (st.turn === i && st.phase !== 'over' ? pal.accent : st.teams ? TEAM_TEXT[i % 2] : pal.text) : pal.muted;
    g.fillText(label, lx, ty);
    if (mine) {
      const ay = ty - 15 * k + (st.calm ? 0 : Math.sin(now / 260) * 1.5 * k);
      g.fillStyle = pal.text;
      g.beginPath();
      g.moveTo(x - 5 * k, ay - 5 * k); g.lineTo(x + 5 * k, ay - 5 * k); g.lineTo(x, ay + 1 * k);
      g.closePath();
      g.fill();
      if (st.phase === 'start') {
        g.font = F.label;
        g.strokeText('ти', x, ay - 6 * k);
        g.fillText('ти', x, ay - 6 * k);
        g.strokeStyle = pal.text;
        g.lineWidth = 1.5 * k;
        g.strokeRect(x - 26, Y - 40, 52, 42);
      }
    }
  }

  /// На телефоні табличка вузька: «гість » відкидаємо (решта ніка й так його), довше 9 — трикрапка.
  function tagLabel(st, name) {
    const small = st.fonts && st.fonts.small;
    const n = small ? name.replace(/^гість\s+/i, '') : name;
    const max = small ? 9 : 12;
    return n.length > max ? n.slice(0, max - 1) + '…' : n;
  }

  /// Хати стоять впритул (лелека, крок) — таблички налазили одна на одну. Живі ставимо першими, а кожну
  /// наступну, що налазить на вже поставлену, піднімаємо на рядок. Ширини — з кешу, measureText раз на нік.
  function placeTags(st, g, now) {
    const F = st.fonts, k = F.k, out = st.tagBuf || (st.tagBuf = [0, 1, 2, 3, 4, 5].map((i) => ({ i, x: 0, top: 0, w: 0, lift: 0, on: false })));
    const cache = st.tagW || (st.tagW = {});
    g.font = F.nick;
    for (const t of out) {
      const hut = st.huts[t.i];
      t.on = hut.plays && !(st.stork && st.stork.seat === t.i && now - st.stork.t0 < 1000);
      // руїну, на якій (чи впритул до якої) стоїть жива хата, не підписуємо: два ніки зливались в один
      if (t.on && !hut.alive) for (let j = 0; j < 6; j++) if (j !== t.i && st.huts[j].alive && Math.abs(st.huts[j].dx - hut.dx) < 44) { t.on = false; break; }
      if (!t.on) continue;
      const label = tagLabel(st, nickOf(st, t.i));
      const key = F.nick + '|' + label;
      if (cache[t.i] !== key) { cache[t.i] = key; cache['w' + t.i] = g.measureText(label).width; }
      t.w = Math.max(30, cache['w' + t.i]);
      t.x = hut.dx;
      t.top = sy(hut.dy) - 50;
      t.lift = 0;
    }
    const lineH = 17 * k;
    for (let pass = 0; pass < 2; pass++) {
      for (let a = 0; a < 6; a++) {
        const A = out[st.tagOrder[a]];
        if (!A.on) continue;
        for (let b = 0; b < a; b++) {
          const B = out[st.tagOrder[b]];
          if (!B.on) continue;
          if (Math.abs(A.x - B.x) >= (A.w + B.w) / 2 + 4 * k || Math.abs((A.top - A.lift) - (B.top - B.lift)) >= lineH) continue;
          const need = A.top - (B.top - B.lift) + lineH;          // рівно на рядок над сусідкою
          if (need > A.lift) A.lift = need;
        }
      }
    }
    return out;
  }

  function drawStork(st, g, x, y, now, flip) {
    const fl = st.calm ? 0.5 : (Math.sin(now / 26) + 1) / 2;       // крила махають ~6 Гц
    const Y = sy(y);
    g.save();
    g.translate(x, Y);
    if (flip) g.scale(-1, 1);
    g.fillStyle = '#ffffff';
    g.beginPath(); g.ellipse(0, 0, 9, 4, 0, 0, Math.PI * 2); g.fill();
    g.beginPath(); g.moveTo(8, -1); g.lineTo(13, -6); g.lineTo(14, -5); g.lineTo(10, 1); g.fill();   // шия
    g.fillStyle = '#e2572e';
    g.beginPath(); g.moveTo(13, -6); g.lineTo(19, -4.5); g.lineTo(13.5, -4.8); g.fill();              // дзьоб
    const wy = -3 - fl * 9;
    g.fillStyle = '#f4f4f4';
    g.beginPath(); g.moveTo(-3, -2); g.lineTo(-10, wy); g.lineTo(3, -2); g.fill();
    g.fillStyle = '#1b1510';
    g.beginPath(); g.moveTo(-8, wy + 2); g.lineTo(-10, wy); g.lineTo(-6, wy + 1); g.fill();
    g.strokeStyle = '#e2572e'; g.lineWidth = 1;
    g.beginPath(); g.moveTo(-7, 2); g.lineTo(-13, 5); g.stroke();
    g.restore();
  }

  function drawShell(st, g, kind, x, y, vx, vy, now, n) {
    const Y = sy(y);
    if (kind >= 6) {
      // приколи — емодзі: півень дивиться, куди летить, смерч крутиться
      g.save();
      g.translate(x, Y);
      if (kind === 8 && !st.calm) g.rotate(now / 90);
      if (kind === 6 && vx > 0) g.scale(-1, 1);
      g.font = st.fonts.dmg; g.textAlign = 'center'; g.textBaseline = 'middle';
      g.fillText(WEAPONS[kind].icon, 0, 0);
      g.restore();
      return;
    }
    const z = Math.max(1, 5.5 / (st.s * 8));
    g.save();
    g.translate(x, Y);
    g.scale(z, z);
    const ang = Math.atan2(-vy, vx);
    switch (kind) {
      case 1:
        if (n === 1) { potShape(g, st.pal.clay, ang, true); break; }
        g.rotate(ang + now / 60);
        g.fillStyle = st.pal.clay;
        g.beginPath(); g.moveTo(-3.5, 2.5); g.lineTo(3.5, 2); g.lineTo(0, -3.5); g.closePath(); g.fill();
        break;
      case 2:
        g.rotate(ang);
        g.fillStyle = '#fff4df';
        g.beginPath(); g.arc(0, 1, 6, Math.PI, 0); g.closePath(); g.fill();
        g.strokeStyle = 'rgba(70, 48, 24, .8)';                      // обвідка: на світлій хмарі білий вареник губився
        g.lineWidth = 0.9;
        g.stroke();
        g.fillStyle = '#e3cfa6';
        for (let k = -4; k <= 4; k += 2) { g.beginPath(); g.arc(k, 1, 0.8, 0, Math.PI * 2); g.fill(); }
        break;
      case 3:
        g.rotate(ang * 0.3);
        g.fillStyle = '#e8c25a';
        g.fillRect(-3.5, -5, 7, 10);
        g.strokeStyle = '#9c7a2a'; g.lineWidth = 1;
        g.beginPath(); g.moveTo(-3.5, 0); g.lineTo(3.5, 0); g.moveTo(-1.5, -5); g.lineTo(-2, -8); g.moveTo(1.5, -5); g.lineTo(2.5, -8); g.stroke();
        break;
      case 4:
        g.restore();
        drawStork(st, g, x, y, now, vx < 0);
        return;
      case 5:
        g.rotate(ang);
        g.fillStyle = '#d9c9a0';
        g.beginPath(); g.moveTo(-6, 0); g.quadraticCurveTo(0, -3, 5, 0); g.quadraticCurveTo(0, 3, -6, 0); g.fill();
        g.fillStyle = '#6fbf4a';
        g.beginPath(); g.ellipse(-6, -2, 3.5, 1.4, -0.6, 0, Math.PI * 2); g.ellipse(-6, 2, 3.5, 1.4, 0.6, 0, Math.PI * 2); g.fill();
        break;
      default:
        potShape(g, st.pal.clay, ang, false);
    }
    g.restore();
  }

  function potShape(g, clay, ang, cracked) {
    g.rotate(ang * 0.25);
    g.fillStyle = clay;
    g.beginPath(); g.ellipse(0, 0.8, 4.6, 5.2, 0, 0, Math.PI * 2); g.fill();
    g.fillRect(-2.2, -6.2, 4.4, 2.4);
    g.strokeStyle = 'rgba(0, 0, 0, .45)'; g.lineWidth = 0.9;
    g.beginPath(); g.arc(3.8, -1.5, 2.2, -1.2, 1.3); g.stroke();
    if (cracked) { g.beginPath(); g.moveTo(-2, -3); g.lineTo(0, 0); g.lineTo(-1.5, 3); g.moveTo(0, 0); g.lineTo(2.5, 1.5); g.stroke(); }
  }

  function drawParticles(st, g, dt) {
    const n = st.PN, pal = st.pal;
    let any = false;
    for (let i = 0; i < n; i++) {
      if (st.pl[i] <= 0) continue;
      any = true;
      st.pl[i] -= dt;
      st.pvy[i] -= st.pg[i] * dt;
      st.px[i] += st.pvx[i] * dt + (st.pc[i] === 7 ? st.wind * 3 * dt : 0);
      st.py[i] += st.pvy[i] * dt;
    }
    if (!any) return;
    for (let c = 0; c < pal.parts.length; c++) {
      g.fillStyle = pal.parts[c];
      for (let i = 0; i < n; i++) {
        if (st.pl[i] <= 0 || st.pc[i] !== c) continue;
        const k = st.pl[i] / st.pm[i];
        const s = st.ps[i] * (c === 7 || c === 3 ? 1.6 - k * 0.6 : 1);
        g.globalAlpha = c === 7 ? k * 0.45 : c === 3 ? k * 0.55 : Math.min(1, k * 1.6);
        if (c === 7 || c === 3) { g.beginPath(); g.arc(st.px[i], sy(st.py[i]), s / 2, 0, Math.PI * 2); g.fill(); }
        else g.fillRect(st.px[i] - s / 2, sy(st.py[i]) - s / 2, s, s);
      }
    }
    g.globalAlpha = 1;
  }

  function drawClouds(st, g, now) {
    const t = now / 1000;
    const drift = st.calm ? 0 : st.wind * 7 + 1.5;
    if (st.map === 'winter') {
      // сніжок: сталі пластівці, що падають і зносяться вітром
      g.fillStyle = 'rgba(255, 255, 255, .8)';
      for (let i = 0; i < 46; i++) {
        const x = ((((hash(i + 300) * W + t * (drift * 3 + 8 * (hash(i + 400) - 0.5))) % W) + W) % W);
        const y = HGT - ((hash(i + 500) * HGT + (st.calm ? 0 : t * (18 + hash(i + 600) * 22))) % HGT);
        g.fillRect(x, y, 2.2, 2.2);
      }
    }
    g.fillStyle = st.map === 'night' ? 'rgba(90, 100, 130, .35)' : 'rgba(255, 250, 240, .55)';
    for (let i = 0; i < 5; i++) {
      const base = hash(i + 7) * 1200;
      const x = ((((base + t * drift * (0.7 + hash(i + 20) * 0.6)) % 1250) + 1250) % 1250) - 125;
      const y = sy(400 + hash(i + 40) * 80);
      const w = 30 + hash(i + 60) * 26;
      g.beginPath();
      g.ellipse(x, y, w, w * 0.32, 0, 0, Math.PI * 2);
      g.ellipse(x - w * 0.45, y + 3, w * 0.55, w * 0.25, 0, 0, Math.PI * 2);
      g.ellipse(x + w * 0.35, y - 4, w * 0.5, w * 0.3, 0, 0, Math.PI * 2);
      g.fill();
    }
  }

  function drawWater(st, g, now) {
    const lvl = waterNow(st, now);
    const pal = st.pal;
    g.fillStyle = pal.water;
    g.globalAlpha = 0.78;
    g.beginPath();
    g.moveTo(0, HGT);
    const t = st.calm ? 0 : now / 600;
    for (let i = 0; i <= 60; i++) {
      const x = (i / 60) * W;
      g.lineTo(x, sy(lvl + Math.sin(x / 26 + t) * 1.6 + Math.sin(x / 11 - t * 1.7) * 0.6));
    }
    g.lineTo(W, HGT);
    g.closePath();
    g.fill();
    g.globalAlpha = 0.35;
    g.strokeStyle = '#d6ecff';
    g.lineWidth = 1.2;
    g.beginPath();
    for (let i = 0; i <= 60; i++) {
      const x = (i / 60) * W;
      const yy = sy(lvl + Math.sin(x / 26 + t) * 1.6 + Math.sin(x / 11 - t * 1.7) * 0.6);
      if (i) g.lineTo(x, yy); else g.moveTo(x, yy);
    }
    g.stroke();
    g.globalAlpha = 1;
  }

  function drawAim(st, g, i, a, p, w) {
    const hut = st.huts[i], pal = st.pal, F = st.fonts;
    const x0 = hut.dx, y0 = hut.dy + LAUNCH;
    const r = (a * Math.PI) / 180, len = 40 + 1.6 * p;
    const x1 = x0 + Math.cos(r) * len, y1 = y0 + Math.sin(r) * len;
    g.strokeStyle = pal.seats[i];
    g.lineWidth = Math.max(1.6, 2.2 * F.k);
    g.setLineDash([len / 24, len / 24]);
    g.beginPath(); g.moveTo(x0, sy(y0)); g.lineTo(x1, sy(y1)); g.stroke();
    g.setLineDash([]);
    g.fillStyle = pal.seats[i];
    g.beginPath(); g.arc(x1, sy(y1), Math.max(2.4, 3 * F.k), 0, Math.PI * 2); g.fill();
    const lx = clamp(x1 + Math.cos(r) * 14 * F.k, 30 * F.k, W - 30 * F.k), ly = clamp(y1 + Math.sin(r) * 10 * F.k + 4 * F.k, 14 * F.k, HGT - 6 * F.k);
    const txt = a + '° · ' + p + ' ' + WEAPONS[w || 0].icon;
    g.font = F.label;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineJoin = 'round';
    g.lineWidth = 3 * F.k;
    g.strokeStyle = 'rgba(10, 16, 12, .85)';
    g.strokeText(txt, lx, sy(ly));
    g.fillStyle = pal.text;
    g.fillText(txt, lx, sy(ly));
  }

  function drawTrail(st, g, i) {
    const tr = st.trails[i];
    if (!tr || tr.length < 4) return;
    g.fillStyle = st.pal.seats[i];
    g.globalAlpha = 0.38;
    const r = Math.max(1.3, 1.6 * st.fonts.k);
    for (let k = 0; k < tr.length; k += 4) {
      const y = tr[k + 1];
      if (y > HGT) continue;
      g.fillRect(tr[k] - r / 2, sy(y) - r / 2, r, r);
    }
    g.globalAlpha = 1;
  }

  /// roundRect є не скрізь (старий Safari) — без нього табличка просто з прямими кутами.
  function rrect(g, x, y, w, h, r) {
    if (g.roundRect) g.roundRect(x, y, w, h, r);
    else g.rect(x, y, w, h);
  }

  /// Рядок, ширший за поле, ділимо на два — біля середини, краще після «:» чи перед «→». Кеш на один рядок:
  /// щокадру малюється лише одна табличка, а measureText на кожен кадр не потрібен.
  function bannerLines(st, g, text, font) {
    const c = st.bannerCache;
    if (c && c.text === text && c.font === font) return c;
    g.font = font;
    const full = g.measureText(text).width;
    let lines = [text], widths = [full];
    if (full > W - 60 && text.length > 12) {
      let cut = -1, best = Infinity;
      for (let i = 1; i < text.length - 1; i++) {
        if (text[i] !== ' ') continue;
        const pref = text[i - 1] === ':' || text[i + 1] === '→' ? 0.6 : 1;
        const d = Math.abs(i - text.length / 2) * pref;
        if (d < best) { best = d; cut = i; }
      }
      if (cut > 0) {
        lines = [text.slice(0, cut), text.slice(cut + 1)];
        widths = lines.map((l) => g.measureText(l).width);
      }
    }
    st.bannerCache = { text, font, lines, w: Math.max(...widths) };
    return st.bannerCache;
  }

  function drawBanner(st, g, text, alpha, y, font) {
    const F = st.fonts;
    font = font || F.banner;
    const b = bannerLines(st, g, text, font);
    g.font = font;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    const lh = (F.small ? 15 : 26) * F.k, n = b.lines.length;
    const w = Math.min(W - 20, b.w + 24 * F.k), hgt = lh * n + (F.small ? 8 : 10) * F.k;
    const top = sy(y) - hgt / 2;
    g.globalAlpha = alpha * 0.82;
    g.fillStyle = 'rgba(12, 20, 16, 1)';
    g.beginPath();
    rrect(g, W / 2 - w / 2, top, w, hgt, (F.small ? 7 : 10) * F.k);
    g.fill();
    g.globalAlpha = alpha;
    g.fillStyle = st.pal.text;
    for (let i = 0; i < n; i++) g.fillText(b.lines[i], W / 2, top + (F.small ? 4 : 5) * F.k + lh * (i + 0.5) + 1 * F.k, W - 30);
    g.globalAlpha = 1;
  }

  function drawWind(st, g) {
    if (st.phase === 'lobby') return;
    const F = st.fonts, w = st.wind;
    const x = W / 2, y = 16 * F.k;                                  // екранна y: під верхнім краєм поля
    g.font = F.wind;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineJoin = 'round';
    g.lineWidth = 3 * F.k;
    g.strokeStyle = 'rgba(10, 16, 12, .7)';
    const label = w ? 'вітер ' + Math.abs(w) : 'штиль';
    g.strokeText(label, x, y);
    g.fillStyle = st.pal.text;
    g.fillText(label, x, y);
    if (!w) return;
    const len = (14 + Math.abs(w) * 9) * F.k, dir = Math.sign(w);
    const ax = x + dir * 38 * F.k, ay = y;
    g.strokeStyle = st.pal.accent;
    g.lineWidth = 2.4 * F.k;
    g.beginPath();
    g.moveTo(ax, ay); g.lineTo(ax + dir * len, ay);
    g.moveTo(ax + dir * len, ay); g.lineTo(ax + dir * (len - 6 * F.k), ay - 4.5 * F.k);
    g.moveTo(ax + dir * len, ay); g.lineTo(ax + dir * (len - 6 * F.k), ay + 4.5 * F.k);
    g.stroke();
  }

  function draw(st, now, dt) {
    if (!st.cv || !st.g) return;
    const g = st.g;
    if (st.skyDirty) paintSky(st);
    if (st.bgDirty) paintGround(st);
    const pal = st.pal, F = st.fonts;
    const K = st.dpr * st.s;
    const cam = camera(st, now, dt), z = cam.z;
    g.setTransform(z, 0, 0, z, -cam.x0 * K * z, -cam.y0 * K * z);
    g.drawImage(st.bgC, 0, 0);
    let ox = 0, oy = 0;
    if (now < st.shakeUntil) { ox = (Math.random() - 0.5) * 6 * F.k; oy = (Math.random() - 0.5) * 6 * F.k; }
    g.setTransform(K * z, 0, 0, K * z, (ox - cam.x0 * z) * K, (oy - cam.y0 * z) * K);
    drawClouds(st, g, now);

    // свіжа земля у вирвах — темніша смуга по гребеню, поки не «підсохне»
    if (st.fresh.length) {
      g.lineJoin = 'round';
      for (let n = st.fresh.length - 1; n >= 0; n--) {
        const [c0, c1, t0] = st.fresh[n];
        const k = 1 - (now - t0) / 1500;
        if (k <= 0) { st.fresh.splice(n, 1); continue; }
        g.globalAlpha = k * 0.85;
        g.strokeStyle = '#2e1d10';
        g.lineWidth = 4;
        g.beginPath();
        for (let c = Math.max(0, c0 - 1); c <= Math.min(COLS - 1, c1 + 1); c++) {
          const X = c * STEP + 2, Y = sy(st.h[c] - 1.5);
          if (c === Math.max(0, c0 - 1)) g.moveTo(X, Y); else g.lineTo(X, Y);
        }
        g.stroke();
      }
      g.globalAlpha = 1;
    }

    const lvl = st.wat.to;
    if (lvl > 0) drawWater(st, g, now);

    const phase = st.phase;
    const mine = me(st);
    const aiming = phase === 'aim' && st.turn != null && st.turn >= 0;
    // «Залп»: свій приціл бачиш лише ти; чужих нема — на те й залп
    const vAim = st.volley && phase === 'aim' && mine != null && st.ctx.playing && st.huts[mine].alive;
    if (aiming) drawTrail(st, g, st.turn);
    else if (vAim) drawTrail(st, g, mine);
    if (st.mag.length) drawMagnets(st, g, now);

    // хати (і лелека, що несе хату): спершу руїни, потім живі — хата, що стала на руїну, не ховається під нею
    for (let n = 0; n < 12; n++) {
      const i = n % 6;
      const hut = st.huts[i];
      if (!hut.plays || (n < 6) === hut.alive) continue;
      hutPos(st, hut, now);
      let x = hut.dx, y = hut.dy;
      const sk = st.stork;
      let carried = false;
      if (sk && sk.seat === i) {
        const e = now - sk.t0;
        if (e >= 1200) { st.stork = null; hut.dx = hut.x; hut.dy = hut.y; x = hut.x; y = hut.y; }
        else if (e >= 400 && e < 1000) {
          const k = (e - 400) / 600, kk = k * k * (3 - 2 * k);
          x = lerp(sk.x0, sk.x1, kk);
          y = lerp(sk.y0, sk.y1, kk) + Math.sin(k * Math.PI) * 90;
          carried = true;
        } else if (e < 400) { x = sk.x0; y = sk.y0; } else { x = sk.x1; y = sk.y1; }
        hut.dx = x; hut.dy = y;
      }
      const a = vAim && i === mine ? st.my.a
        : st.turn === i && (phase === 'aim' || phase === 'fly' || phase === 'settle')
          ? (i === mine && phase === 'aim' ? st.my.a : st.aimTurn[0])
          : hut.x < W / 2 ? 60 : 120;
      const lit = st.volley ? phase === 'aim' && hut.alive && !st.ready[i] : st.turn === i && hut.alive && phase === 'aim';
      if (lit) {
        g.fillStyle = pal.seats[i];
        g.globalAlpha = 0.22 + (st.calm ? 0 : 0.1 * Math.sin(now / 200));
        g.beginPath(); g.ellipse(x, sy(y) - 2, 30, 7, 0, 0, Math.PI * 2); g.fill();
        g.globalAlpha = 1;
      }
      drawHut(st, g, i, x, y, a, now);
      if (carried) drawStork(st, g, x, y + 48, now, sk.x1 < sk.x0);
      if (hut.alive && (hut.honey > 0 || hut.stuck)) {
        g.font = F.label; g.textAlign = 'center'; g.textBaseline = 'middle';
        g.fillText('🍯', x - 26, sy(y + 8));
      }
      // дим із димаря
      if (hut.alive && !st.calm && now - st.smokeAt > 700) spawn(st, x + 9, y + 37, 0, 10, 2.4, 7, 4, -2);
    }
    if (now - st.smokeAt > 700) st.smokeAt = now;
    // лелека прилітає й відлітає
    const sk = st.stork;
    if (sk) {
      const e = now - sk.t0;
      if (e < 400) drawStork(st, g, sk.x0, lerp(HGT + 20, sk.y0 + 48, e / 400), now, sk.x1 < sk.x0);
      else if (e >= 1000 && e < 1200) drawStork(st, g, sk.x1, lerp(sk.y1 + 48, HGT + 20, (e - 1000) / 200), now, sk.x1 < sk.x0);
    }

    // ніч: темрява поверх села й хат, а приціл, снаряди й вибухи — уже поверх неї
    if (st.map === 'night' && phase !== 'lobby') drawNight(st, g, now, K * z, (ox - cam.x0 * z) * K, (oy - cam.y0 * z) * K);

    // приціл того, хто ходить (свій — миттєво, чужий — з кадрів)
    if (aiming && st.huts[st.turn].alive) {
      const s = st.turn;
      if (s === mine && st.ctx.playing) drawAim(st, g, s, st.my.a, st.my.p, st.my.w);
      else drawAim(st, g, s, st.aimTurn[0], st.aimTurn[1], st.aimTurn[2]);
    } else if (vAim) {
      if (st.ready[mine] || st.fired === st.turnNo) g.globalAlpha = 0.45;   // заряджено — приціл блідий
      drawAim(st, g, mine, st.my.a, st.my.p, st.my.w);
      g.globalAlpha = 1;
    }

    // снаряди: екстраполяція на пів кадру вперед, але не під землю; хвіст з останніх положень
    if (phase === 'fly' && st.shN) {
      const k = st.shSmooth ? clamp((now - st.shAt) / st.gap, -1, 1.5) : 0;
      const sample = now - st.tailAt > 28;
      if (sample) st.tailAt = now;
      for (let i = 0; i < st.shN; i++) {
        const bx = st.shB[i * 3], by = st.shB[i * 3 + 1], kind = st.shB[i * 3 + 2];
        const vx = st.shSmooth ? bx - st.shA[i * 3] : 0, vy = st.shSmooth ? by - st.shA[i * 3 + 1] : -1;
        const x = bx + vx * k;
        let y = by + vy * k;
        const gr = st.h[col(x)];
        if (y < gr) y = gr;
        // хвіст
        const tb = i * 12;
        let tn = st.tailN[i];
        if (sample) {
          for (let q = Math.min(tn, 5); q > 0; q--) { st.tails[tb + q * 2] = st.tails[tb + (q - 1) * 2]; st.tails[tb + q * 2 + 1] = st.tails[tb + (q - 1) * 2 + 1]; }
          st.tails[tb] = x; st.tails[tb + 1] = y;
          tn = st.tailN[i] = Math.min(6, tn + 1);
        }
        if (kind !== 4) {
          g.fillStyle = TAILC[kind] || pal.clay;
          for (let q = 1; q < tn; q++) {
            g.globalAlpha = 0.45 * (1 - q / 6);
            const ty = st.tails[tb + q * 2 + 1];
            if (ty > HGT) continue;
            g.beginPath(); g.arc(st.tails[tb + q * 2], sy(ty), 2.4 - q * 0.3, 0, Math.PI * 2); g.fill();
          }
          g.globalAlpha = 1;
        }
        if (y > HGT + 4) {
          // вище неба: маркер під верхнім краєм
          g.fillStyle = pal.accent;
          g.beginPath();
          g.moveTo(x, 2 * F.k); g.lineTo(x - 6 * F.k, 11 * F.k); g.lineTo(x + 6 * F.k, 11 * F.k);
          g.closePath(); g.fill();
        } else drawShell(st, g, kind, x, y, vx || 1, vy, now, st.shN);
      }
    }

    // кільця вибухів і спалахи
    for (let n = st.rings.length - 1; n >= 0; n--) {
      const r = st.rings[n];
      const k = (now - r.t0) / r.dur;
      if (k >= 1) { st.rings.splice(n, 1); continue; }
      g.globalAlpha = 1 - k;
      if (r.flash) {
        g.fillStyle = r.color;
        g.beginPath(); g.arc(r.x, sy(r.y), r.r * (0.6 + k * 0.6), 0, Math.PI * 2); g.fill();
      } else {
        g.strokeStyle = r.color;
        g.lineWidth = 3 * (1 - k) + 1;
        g.beginPath(); g.arc(r.x, sy(r.y), r.r * (1 + 0.8 * k), 0, Math.PI * 2); g.stroke();
      }
    }
    g.globalAlpha = 1;
    drawParticles(st, g, dt);

    // таблички над хатами — поверх усього, щоб дим і вибухи їх не ховали; живі — першими
    st.tagOrder = st.tagOrder || [0, 1, 2, 3, 4, 5];
    st.tagOrder.sort((a, b) => (st.huts[b].alive - st.huts[a].alive) || a - b);
    const tags = placeTags(st, g, now);
    for (const t of tags) if (t.on) drawTag(st, g, t.i, st.huts[t.i].dx, st.huts[t.i].dy, now, t.i === mine, Math.max(0, t.lift), t.w);
    drawEmos(st, g, now, tags);
    // «Залп»: хто вже зарядив — зелена галочка біля хати
    if (st.volley && phase === 'aim') {
      g.font = F.big; g.textAlign = 'center'; g.textBaseline = 'middle'; g.fillStyle = pal.ok;
      for (let i = 0; i < 6; i++) {
        const hut = st.huts[i];
        if (hut.alive && st.ready[i]) g.fillText('✓', clamp(hut.dx + 30, 12, W - 12), sy(hut.dy + 26));
      }
    }

    // цифри шкоди
    if (st.floats.length) {
      g.font = F.dmg;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.lineJoin = 'round';
      g.lineWidth = 3 * F.k;
      for (let n = st.floats.length - 1; n >= 0; n--) {
        const fl = st.floats[n];
        const k = (now - fl.t0) / 1100;
        if (k >= 1) { st.floats.splice(n, 1); continue; }
        const y = sy(fl.y + k * 26 * F.k * 1.4);
        if (fl.w == null) fl.w = g.measureText(fl.text).width;
        const x = clamp(fl.x, fl.w / 2 + 4 * F.k, W - fl.w / 2 - 4 * F.k);
        g.globalAlpha = k < 0.7 ? 1 : (1 - k) / 0.3;
        g.strokeStyle = 'rgba(10, 16, 12, .9)';
        g.strokeText(fl.text, x, y);
        g.fillStyle = fl.color;
        g.fillText(fl.text, x, y);
      }
      g.globalAlpha = 1;
    }

    // написи поверх поля — без наближення камери
    if (z !== 1) g.setTransform(K, 0, 0, K, ox * K, oy * K);
    drawWind(st, g);

    // останні 5 секунд ходу — велика червона цифра біля хати, що ходить (видно всім, і глядачам теж)
    const hurrySeat = aiming ? st.turn : vAim && myTurn(st) ? mine : -1;
    if (st.hurry && hurrySeat >= 0 && st.huts[hurrySeat].alive) {
      const hut = st.huts[hurrySeat];
      const sec = Math.max(1, Math.ceil((st.endsMs - Date.now()) / 1000));
      const x = hut.dx + (hut.dx < W / 2 ? 1 : -1) * 42, y = sy(hut.dy + 20);
      g.font = F.big;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.lineJoin = 'round';
      g.lineWidth = 3.5 * F.k;
      g.strokeStyle = 'rgba(10, 16, 12, .9)';
      g.strokeText(String(sec), x, y);
      g.fillStyle = pal.danger;
      g.fillText(String(sec), x, y);
    }

    // заряд сили на пробілі/кнопці — дуга біля своєї хати
    if (st.charge && mine != null && phase === 'aim') {
      const hut = st.huts[mine];
      const p = chargePower(st, now);
      g.strokeStyle = pal.accent;
      g.lineWidth = 3.5 * F.k;
      g.beginPath();
      g.arc(hut.dx, sy(hut.dy + 18), 30 * Math.max(1, F.k * 0.8), -Math.PI / 2, -Math.PI / 2 + (Math.PI * 2 * p) / 100);
      g.stroke();
    }

    // написи на полі: відлік, рядок пострілу, підсумок
    if (phase === 'start') {
      g.fillStyle = 'rgba(12, 20, 16, .42)';
      g.fillRect(0, 0, W, HGT);
      const n = Math.max(1, Math.ceil((st.startIn * TICK_MS) / 1000));
      g.font = F.count;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillStyle = pal.text;
      g.fillText(String(n), W / 2, HGT / 2 - 10 * F.k);
      g.font = F.big;
      g.fillText('Готуйсь', W / 2, HGT / 2 + 40 * F.k);
    } else if (phase === 'over' && st.result) {
      g.fillStyle = 'rgba(12, 20, 16, .35)';
      g.fillRect(0, 0, W, HGT);
      if (st.replay) drawReplay(st, g, now);
      // угорі, над пагорбами: посередині табличка ховала ніки хат, що стоять на гребені
      drawBanner(st, g, overText(st), 1, HGT - 64 * F.k);
    } else if (phase === 'lobby') {
      drawBanner(st, g, 'Чекаємо на гравців · господар тисне «Почати»', 0.9, HGT / 2 + 30, F.label);
    } else if (st.banner) {
      const e = now - st.banner.t0;
      if (e > 2600) st.banner = null;
      else drawBanner(st, g, st.banner.text, e < 2200 ? 1 : 1 - (e - 2200) / 400, HGT - 62 * F.k);
    }
    g.setTransform(1, 0, 0, 1, 0, 0);
  }

  /// Підкови-магніти: пульсує коло, куди вони тягнуть чужі снаряди, і сама підкова кольору власника.
  function drawMagnets(st, g, now) {
    const F = st.fonts;
    for (const [x, y, o] of st.mag) {
      const k = st.calm ? 0.5 : 0.5 + 0.5 * Math.sin(now / 260);
      g.strokeStyle = st.pal.seats[o] || st.pal.accent;
      g.globalAlpha = 0.18 + 0.14 * k;
      g.lineWidth = 2 * F.k;
      g.setLineDash([6 * F.k, 6 * F.k]);
      g.beginPath(); g.arc(x, sy(y + 12), MAG_R, 0, Math.PI * 2); g.stroke();
      g.setLineDash([]);
      g.globalAlpha = 1;
      g.font = F.dmg; g.textAlign = 'center'; g.textBaseline = 'middle';
      g.fillText('🧲', x, sy(y + 8));
    }
  }

  /// Ніч: поле темне, видно лише вікна й димарі хат, снаряди, спалахи вибухів (ще з секунду після) і свою хату
  /// трохи ширше. Маска — окремий канвас: темрява мінус «дірки» світла, поверх світу, але під табличками.
  function drawNight(st, g, now, k, tx, ty) {
    let c = st.nightC;
    if (!c) c = st.nightC = document.createElement('canvas');
    if (c.width !== st.pw || c.height !== st.ph) { c.width = st.pw; c.height = st.ph; }
    const n = c.getContext('2d');
    n.setTransform(1, 0, 0, 1, 0, 0);
    n.globalCompositeOperation = 'source-over';
    n.clearRect(0, 0, c.width, c.height);
    n.fillStyle = 'rgba(3, 6, 16, .86)';
    n.fillRect(0, 0, c.width, c.height);
    n.setTransform(k, 0, 0, k, tx, ty);
    n.globalCompositeOperation = 'destination-out';
    const hole = (x, y, r, a) => {
      if (a <= 0.01) return;
      const gr = n.createRadialGradient(x, y, 0, x, y, r);
      gr.addColorStop(0, 'rgba(0, 0, 0, ' + a + ')');
      gr.addColorStop(1, 'rgba(0, 0, 0, 0)');
      n.fillStyle = gr;
      n.beginPath(); n.arc(x, y, r, 0, Math.PI * 2); n.fill();
    };
    const mine = me(st);
    for (let i = 0; i < 6; i++) {
      const hut = st.huts[i];
      if (!hut.plays || !hut.alive) continue;
      hole(hut.dx, sy(hut.dy + 14), i === mine ? 70 : 32, i === mine ? 0.8 : 0.7);
      hole(hut.dx + 9, sy(hut.dy + 40), 12, 0.9);
    }
    if (st.phase === 'fly') for (let i = 0; i < st.shN; i++) hole(st.shB[i * 3], sy(st.shB[i * 3 + 1]), 30, 0.95);
    for (const r of st.rings) hole(r.x, sy(r.y), r.r * 3 + 50, 0.95 * (1 - clamp((now - r.t0) / r.dur, 0, 1)));
    const b = st.camBoom;
    if (b && now - b.t0 < 1500) hole(b.x, sy(b.y), 160, 0.9 * (1 - (now - b.t0) / 1500));
    for (const [x, y] of st.mag) hole(x, sy(y + 8), 22, 0.8);
    n.globalCompositeOperation = 'source-over';
    const K0 = g.getTransform();
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.drawImage(c, 0, 0);
    g.setTransform(K0);
    // зорі — поверх темряви, лише над землею
    g.fillStyle = 'rgba(235, 240, 255, .75)';
    for (let i = 0; i < 40; i++) {
      const x = hash(i + 700) * W, y = 300 + hash(i + 800) * 195;
      if (y > st.h[col(x)] + 30) g.fillRect(x, sy(y), 1.6, 1.6);
    }
  }

  /// Камера: ціль — рамка навколо снарядів у польоті, далі місце вибуху, інакше все поле; рух згладжений.
  /// Повертає {z, x0, y0} — наближення й лівий верхній кут видимого шматка (у світових одиницях, y — згори).
  function camera(st, now, dt) {
    const c = st.cam || (st.cam = { x: W / 2, y: HGT / 2, z: 1, x0: 0, y0: 0 });
    const on = !st.calm && st.cssW > 0 && st.cssW < CAM_MAX_W && st.phase !== 'lobby' && st.phase !== 'over';
    let tx = W / 2, ty = HGT / 2, tz = 1;
    const boom = st.camBoom;
    if (on && st.phase === 'fly' && st.shN) {
      let x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
      for (let i = 0; i < st.shN; i++) {
        const x = st.shB[i * 3], y = clamp(sy(st.shB[i * 3 + 1]), 0, HGT);
        if (x < x0) x0 = x;
        if (x > x1) x1 = x;
        if (y < y0) y0 = y;
        if (y > y1) y1 = y;
      }
      tz = clamp(Math.min(W / (x1 - x0 + 380), HGT / (y1 - y0 + 230)), 1, CAM_Z);
      tx = (x0 + x1) / 2;
      ty = (y0 + y1) / 2;
    } else if (on && boom && now - boom.t0 < CAM_BOOM_MS && (st.phase === 'fly' || st.phase === 'settle')) {
      tx = boom.x; ty = sy(boom.y); tz = CAM_Z * 0.9;
    }
    if (!on) { c.z = 1; c.x = W / 2; c.y = HGT / 2; }
    else {
      const d = Math.min(0.1, dt || 0.016);
      c.z += (tz - c.z) * (1 - Math.exp(-d * (tz > c.z ? 3.5 : st.phase === 'aim' ? 7 : 2.5)));
      c.x += (tx - c.x) * (1 - Math.exp(-d * 7));
      c.y += (ty - c.y) * (1 - Math.exp(-d * 7));
      if (Math.abs(c.z - 1) < 0.004 && tz === 1) c.z = 1;
    }
    const hw = W / (2 * c.z), hh = HGT / (2 * c.z);
    c.x0 = clamp(c.x, hw, W - hw) - hw;
    c.y0 = clamp(c.y, hh, HGT - hh) - hh;
    return c;
  }

  /// Емоції над хатами: вискакує, трохи пливе вгору й тане за 2 с — над табличкою з ніком, щоб її не ховати.
  function drawEmos(st, g, now, tags) {
    const F = st.fonts;
    let any = false;
    for (let i = 0; i < 6; i++) {
      const em = st.emos[i];
      if (!em) continue;
      const e = now - em.t0;
      const hut = st.huts[i];
      if (e > EMO_MS || !hut.plays) { st.emos[i] = null; continue; }
      if (!any) { any = true; g.textAlign = 'center'; g.textBaseline = 'middle'; g.font = F.emo; }
      const t = tags.find((q) => q.i === i);
      const lift = t && t.on ? Math.max(0, t.lift) + 16 * F.k : 0;
      const k = st.calm ? 1 : Math.min(1, e / 160);
      const sc = 0.55 + 0.45 * k * (2 - k);
      const x = clamp(hut.dx, 18 * F.k, W - 18 * F.k);
      const y = Math.max(16 * F.k, sy(hut.dy) - 50 - lift - 20 * F.k - (st.calm ? 0 : (e / EMO_MS) * 10 * F.k));
      g.globalAlpha = e > EMO_MS - 400 ? (EMO_MS - e) / 400 : 1;
      g.save();
      g.translate(x, y);
      g.scale(sc, sc);
      g.fillText(EMOS[em.e], 0, 0);
      g.restore();
    }
    g.globalAlpha = 1;
  }

  /// Емоція від мене (1–4, Ⓧ, кнопки пульта): лише за столом посеред партії; частіше за 1,5 с не шлемо.
  function emote(st, e) {
    const s = me(st);
    if (s == null || !st.ctx.playing || st.phase === 'lobby' || st.phase === 'over' || !EMOS[e]) return false;
    const now = performance.now();
    if (now - st.emoAt < EMO_GAP) return true;
    st.emoAt = now;
    st.emoNext = (e + 1) % EMOS.length;
    st.ctx.input('emo', { e });
    return true;
  }

  /// Підсумок: найкращий постріл партії ще раз — пунктир росте за 1,6 с кольором стрільця, на кінці — вибух і «🏆 −55».
  function drawReplay(st, g, now) {
    const r = st.replay, pts = r.pts, n = pts.length >> 1, F = st.fonts;
    const k = st.calm ? 1 : clamp((now - r.t0) / 1600, 0, 1);
    if (k <= 0 || !n) return;
    const upto = Math.max(1, Math.floor(n * k));
    const rad = Math.max(1.8, 2.4 * F.k);
    g.fillStyle = st.pal.seats[r.seat];
    for (let i = 0; i < upto; i++) {
      const y = pts[i * 2 + 1];
      if (y > HGT) continue;
      g.fillRect(pts[i * 2] - rad / 2, sy(y) - rad / 2, rad, rad);
    }
    const lx = pts[(upto - 1) * 2], ly = Math.min(HGT - 4, pts[(upto - 1) * 2 + 1]);
    if (k < 1) {
      g.beginPath(); g.arc(lx, sy(ly), rad * 1.9, 0, Math.PI * 2); g.fill();
      return;
    }
    if (!r.boom) {
      r.boom = true;
      ring(st, lx, ly, 42, st.pal.accent, 520);
      burst(st, lx, ly, 16, 0, 160, true, 0.9, 3, 300);
      sfx(st, 'boom', 0.8);
    }
    const txt = '🏆 −' + r.dmg;
    g.font = F.dmg;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineJoin = 'round';
    g.lineWidth = 3 * F.k;
    const tx = clamp(lx, 40 * F.k, W - 40 * F.k), ty = sy(Math.min(HGT - 90 * F.k, ly + 40 + 30 * F.k));   // над табличкою хати
    g.strokeStyle = 'rgba(10, 16, 12, .9)';
    g.strokeText(txt, tx, ty);
    g.fillStyle = st.pal.accent;
    g.fillText(txt, tx, ty);
  }

  function overText(st) {
    const r = st.result;
    if (!r || !r.winners || !r.winners.length) return 'Нічия';
    const names = r.winners.map((i) => nickOf(st, i)).join(' + ');
    if (r.reason === 'left') return '🏆 ' + names + ' — решта встали з-за столу';
    return '🏆 ' + names + (r.winners.length > 1 ? ' — останні хати' : ' — остання хата');
  }

  // =============================================================================================
  // Керування
  // =============================================================================================

  function chargePower(st, now) {
    const c = st.charge;
    if (!c) return st.my.p;
    const e = now - c.t0;
    if (e < SHORT_PRESS) return st.my.p;
    return clamp(Math.round((e / CHARGE_MS) * 100), 5, 100);
  }

  function setAim(st, a, p, w) {
    const na = clamp(Math.round(a), 0, 180), np = clamp(Math.round(p), 5, 100);
    if (na === st.my.a && np === st.my.p && (w == null || w === st.my.w)) return;
    st.my.a = na; st.my.p = np;
    if (w != null) st.my.w = w;
    st.aimDirty = true;
    paintCtl(st);
  }

  function nudge(st, what, dir, big) {
    if (!myTurn(st) || st.charge) return;
    if (what === 'a') setAim(st, st.my.a + dir * (big ? 5 : 1), st.my.p);
    else setAim(st, st.my.a, st.my.p + dir * (big ? 5 : 1));
  }

  function hold(st, what, dir, big) {
    const now = performance.now();
    st.holds[what] = { dir, big, since: now, next: now + 330 };
    nudge(st, what, dir, big);
  }

  function release(st, what) { st.holds[what] = null; }

  function fire(st, p) {
    if (!myTurn(st)) return;
    const s = me(st);
    const w = st.my.w;
    if (invOf(st, s)[w] === 0) { st.ctx.toast('Цього вже не лишилось', 'err'); return; }
    const power = clamp(Math.round(p == null ? st.my.p : p), 5, 100);
    st.my.p = power;
    st.fired = st.turnNo;
    st.charge = null;
    st.holds.a = st.holds.p = null;
    const turnNo = st.turnNo;
    st.ctx.act('fire', { a: st.my.a, p: power, w }).then((r) => { if (!r || !r.ok) { if (st.fired === turnNo) st.fired = -1; paintCtl(st); } });
    paintCtl(st);
  }

  function move(st, dir) {
    if (!myTurn(st) || st.charge) return;
    const now = performance.now();
    if (now - st.lastMove < MOVE_GAP) return;
    st.lastMove = now;
    st.ctx.act('move', { dir });
  }

  function pickWeapon(st, w) {
    if (w < 0 || w >= st.kinds) return;
    const s = me(st);
    if (s == null) return;
    if (invOf(st, s)[w] === 0) { st.ctx.toast('Цього вже не лишилось', 'err'); return; }
    if (myTurn(st)) setAim(st, st.my.a, st.my.p, w);
    else st.preW = w;
    paintArms(st);
  }

  function cycleWeapon(st, dir) {
    const s = me(st);
    if (s == null) return;
    const inv = invOf(st, s);
    let w = myTurn(st) ? st.my.w : st.preW != null ? st.preW : st.my.w;
    for (let k = 0; k < st.kinds; k++) {
      w = (w + dir + st.kinds) % st.kinds;
      if (inv[w] !== 0) { pickWeapon(st, w); return; }
    }
  }

  function startCharge(st, src) {
    if (!myTurn(st) || st.charge) return;
    st.charge = { t0: performance.now(), src };
  }

  function endCharge(st) {
    const c = st.charge;
    if (!c) return;
    const p = chargePower(st, performance.now());
    st.charge = null;
    fire(st, p);
  }

  /// Щокадру: автоповтор утримань (12/с, після секунди — 30/с), заряд до сотні сам стріляє, приціл — на сервер ≤ 10/с.
  function tickInput(st, now) {
    for (const what of ['a', 'p']) {
      const h = st.holds[what];
      if (!h || now < h.next) continue;
      nudge(st, what, h.dir, h.big);
      h.next = now + (now - h.since > 1000 ? 33 : 83);
    }
    if (st.charge) {
      if (!myTurn(st)) st.charge = null;
      else {
        const p = chargePower(st, now);
        if (now - st.charge.t0 >= SHORT_PRESS && p !== st.my.p) { st.my.p = p; st.aimDirty = true; paintCtl(st); }
        if (now - st.charge.t0 >= CHARGE_MS) endCharge(st);          // перетримав — летить сам
      }
    }
    paintCharge(st, now);
    // останні 5 секунд ходу: червона дуга всім, а тому, хто ходить, — ще й тихе «тік» щосекунди (якщо звук увімкнено)
    const left = st.phase === 'aim' && st.endsMs ? st.endsMs - Date.now() : Infinity;
    const hurry = left > 0 && left <= HURRY_MS;
    if (hurry !== st.hurry) { st.hurry = hurry; if (st.els) st.els.clock.classList.toggle('gk-hurry', hurry); }
    if (hurry && myTurn(st)) {
      const sec = Math.ceil(left / 1000);
      if (sec !== st.tickSec) { st.tickSec = sec; sfx(st, 'tick', 1); }
    } else st.tickSec = 0;
    if (st.aimDirty && now - st.aimSentAt >= AIM_GAP) {
      st.aimDirty = false;
      if (myTurn(st)) {
        const key = st.my.a + '|' + st.my.p + '|' + st.my.w;
        if (key !== st.aimSent) {
          st.aimSent = key;
          st.aimSentAt = now;
          st.ctx.input('aim', { a: st.my.a, p: st.my.p, w: st.my.w });
        }
      }
    }
  }

  /// Рогатка: тягнеш від точки натиску, постріл — у протилежний бік, сила — за довжиною.
  function pointerDown(st, e) {
    if (!myTurn(st) || st.drag || (e.pointerType === 'mouse' && e.button !== 0)) return;
    e.preventDefault();
    try { st.cv.el.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
    const r = st.cv.el.getBoundingClientRect();
    st.drag = { id: e.pointerId, x0: e.clientX, y0: e.clientY, len: 0, a0: st.my.a, p0: st.my.p, full: Math.max(0.14 * r.width, 110) };
    syncCanvasCls(st);
  }

  function pointerMove(st, e) {
    const d = st.drag;
    if (!d || d.id !== e.pointerId) return;
    e.preventDefault();
    const dx = e.clientX - d.x0, dy = e.clientY - d.y0;
    d.len = Math.hypot(dx, dy);
    if (d.len < 12) { setAim(st, d.a0, d.p0); return; }
    let a = (Math.atan2(dy, -dx) * 180) / Math.PI;
    if (a < 0) a = -dx >= 0 ? 0 : 180;
    setAim(st, a, clamp(Math.round((d.len / d.full) * 100), 5, 100));
  }

  function pointerUp(st, e, cancel) {
    const d = st.drag;
    if (!d || d.id !== e.pointerId) return;
    st.drag = null;
    syncCanvasCls(st);
    if (cancel || d.len < 12) { setAim(st, d.a0, d.p0); return; }
    fire(st);
  }

  // =============================================================================================
  // Смужка над полем, комора, пульт, підсумок (DOM — лише коли щось змінилось)
  // =============================================================================================

  function build(root, st) {
    const top = document.createElement('div');
    top.className = 'gk-top';
    top.innerHTML = '<div class="gk-chips"></div><div class="gk-side"><span class="gk-wind"></span>'
      + '<div class="gk-clock" hidden><span class="gk-who small muted"></span></div>'
      + '<button type="button" class="gk-sound" data-pad-skip title="Звук">🔇</button></div>';
    const arms = document.createElement('div');
    arms.className = 'gk-arms';
    arms.innerHTML = WEAPONS.map((w, i) => '<button type="button" class="gk-arm" data-w="' + i + '" title="' + w.tip + '">'
      + '<span class="gk-emo">' + w.icon + '</span><span class="gk-name">' + w.chip + '</span><span class="gk-left"></span>'
      + '<span class="gk-key">' + ((i + 1) % 10) + '</span></button>').join('');
    const ctl = document.createElement('div');
    ctl.className = 'gk-ctl';
    ctl.innerHTML = '<span class="gk-grp gk-move"><button type="button" class="gk-sq" data-k="m" data-d="-1" aria-label="посунути хату ліворуч">◀</button>'
      + '<span class="gk-fuel">⛽ 120</span><button type="button" class="gk-sq" data-k="m" data-d="1" aria-label="посунути хату праворуч">▶</button></span>'
      // Кут — стрілками обертання, як клавіші ← →: ↺ крутить приціл проти годинникової (лівіше, число більшає),
      // ↻ — за годинниковою. Колишні «− / +» працювали навпаки числу, і новачок, що тиснув «+» «щоб вище», стріляв нижче.
      + '<span class="gk-grp"><span class="gk-lbl">кут</span><button type="button" class="gk-sq gk-rot" data-k="a" data-d="1" title="Повернути приціл лівіше (←)" aria-label="кут лівіше">↺</button>'
      + '<b class="gk-a">45°</b><button type="button" class="gk-sq gk-rot" data-k="a" data-d="-1" title="Повернути приціл правіше (→)" aria-label="кут правіше">↻</button></span>'
      + '<span class="gk-grp"><span class="gk-lbl">сила</span><button type="button" class="gk-sq" data-k="p" data-d="-1" aria-label="слабше">−</button>'
      + '<b class="gk-p">60</b><button type="button" class="gk-sq" data-k="p" data-d="1" aria-label="сильніше">+</button></span>'
      + '<span class="gk-shoot"><button type="button" class="primary gk-fire" data-k="f"><i class="gk-charge"></i><span>💥 Постріл</span></button>'
      + '<button type="button" class="gk-skip" data-k="s">Пропустити</button></span>'
      + '<span class="gk-grp gk-emos" title="Емоція над своєю хатою (1–4)">' + EMOS.map((m, i) => '<button type="button" class="gk-sq gk-emo-b" data-k="e" data-e="' + i + '" aria-label="емоція ' + m + '">' + m + '</button>').join('') + '</span>';
    const tip = document.createElement('div');
    tip.className = 'gk-tip';
    tip.hidden = true;
    const sum = document.createElement('div');
    sum.className = 'gk-sum';
    sum.hidden = true;
    root.appendChild(top);
    root.appendChild(arms);
    root.appendChild(ctl);
    root.appendChild(tip);
    root.appendChild(sum);
    st.els = {
      top, chips: top.querySelector('.gk-chips'), wind: top.querySelector('.gk-wind'), clock: top.querySelector('.gk-clock'),
      who: top.querySelector('.gk-who'), sound: top.querySelector('.gk-sound'), arms, ctl, tip, sum,
      fuel: ctl.querySelector('.gk-fuel'), a: ctl.querySelector('.gk-a'), p: ctl.querySelector('.gk-p'),
      fire: ctl.querySelector('.gk-fire'), chargeBar: ctl.querySelector('.gk-charge'), skip: ctl.querySelector('.gk-skip'),
      sig: {},
    };

    st.els.sound.onclick = () => {
      st.sound = !st.sound;
      store.set('glekomet.sound', st.sound ? '1' : '0');
      paintSound(st);
      if (st.sound) sfx(st, 'turn', 1);
    };
    arms.addEventListener('click', (e) => {
      const b = e.target.closest('.gk-arm');
      if (b && !b.disabled) pickWeapon(st, +b.dataset.w);
    });
    // пульт: утримання «−/+» і «◀/▶» повторює, «Постріл» — затиснути й відпустити
    ctl.addEventListener('pointerdown', (e) => {
      const b = e.target.closest('button');
      if (!b || b.disabled) return;
      const k = b.dataset.k;
      if (k === 's' || k === 'e') return;
      e.preventDefault();
      try { b.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
      b._pid = e.pointerId;
      if (k === 'f') startCharge(st, 'btn');
      else if (k === 'm') { move(st, +b.dataset.d); b._rep = setInterval(() => move(st, +b.dataset.d), MOVE_GAP + 10); }
      else hold(st, k, +b.dataset.d, e.shiftKey);
    });
    const up = (e, cancel) => {
      const b = e.target.closest('button');
      if (!b || b._pid !== e.pointerId) return;
      b._pid = null;
      const k = b.dataset.k;
      if (b._rep) { clearInterval(b._rep); b._rep = 0; }
      if (k === 'f') { if (cancel) st.charge = null; else endCharge(st); paintCharge(st, 0); }
      else if (k === 'a' || k === 'p') release(st, k);
    };
    ctl.addEventListener('pointerup', (e) => up(e, false));
    ctl.addEventListener('pointercancel', (e) => up(e, true));
    ctl.addEventListener('click', (e) => {
      const b = e.target.closest('button');
      if (!b || b.disabled) return;
      const k = b.dataset.k;
      if (k === 's') {
        // «Пропустити» — з підтвердженням: прогавлений хід не повернеш
        const now = performance.now();
        if (now - st.skipArm < 2500) { st.skipArm = 0; st.ctx.act('skip'); b.textContent = 'Пропустити'; return; }
        st.skipArm = now;
        b.textContent = 'Точно пропустити?';
        setTimeout(() => { if (performance.now() - st.skipArm >= 2400 && b.isConnected) b.textContent = 'Пропустити'; }, 2600);
        return;
      }
      if (k === 'e') { emote(st, +b.dataset.e); return; }
      if (e.detail !== 0) return;                   // мишу й палець уже обробив pointerdown
      if (k === 'f') fire(st);
      else if (k === 'm') move(st, +b.dataset.d);
      else if (k === 'a' || k === 'p') nudge(st, k, +b.dataset.d, false);
    });
    tip.addEventListener('click', (e) => {
      if (!e.target.closest('button')) return;
      store.set('glekomet.turnHint', '1');
      tip.hidden = true;
    });
  }

  function paintSound(st) {
    const b = st.els && st.els.sound;
    if (!b) return;
    const t = st.sound ? '🔊' : '🔇';
    if (b.textContent !== t) b.textContent = t;
    b.title = st.sound ? 'Звук увімкнено' : 'Звук вимкнено';
  }

  function paintTop(st) {
    const E = st.els, ctx = st.ctx;
    const mine = me(st);
    let html = '';
    for (let i = 0; i < 6; i++) {
      const hut = st.huts[i];
      const nick = ctx.nickOf(i) || (hut.plays && st.phase !== 'lobby' ? st.nicks[i] : '');
      if (!nick) continue;
      const alive = st.phase === 'lobby' || hut.alive;
      const hp = st.phase === 'lobby' ? 100 : hut.hp;
      const bar = hp > 60 ? '' : hp > 30 ? ' mid' : ' low';
      const turn = st.volley ? st.phase === 'aim' && hut.alive && !st.ready[i] : st.turn === i && (st.phase === 'aim' || st.phase === 'fly' || st.phase === 'settle');
      // На вузькому екрані ніки в чіпах сховані (їх і так показують пігулки місць над столом і таблички на полі),
      // лишається колір, ❤ і «ти» на своєму — інакше над полем було шість рядів про те саме.
      html += '<span class="gk-chip' + (alive ? '' : ' out') + (turn ? ' turn' : '') + (i === mine ? ' me' : '') + '" title="' + ctx.esc(nick) + ' · ' + ctx.esc(ctx.seatName(i)) + ' хата'
        + (st.teams ? ' · команда ' + (i % 2 ? 'непарних' : 'парних') : '') + '">'
        + '<i class="gk-dot" style="background:' + st.pal.seats[i] + '"></i>'
        + (st.teams ? '<span>' + (i % 2 ? '🔵' : '🔴') + '</span>' : '')
        + '<span class="gk-nick">' + ctx.esc(nick) + '</span>'
        + (i === mine ? '<span class="gk-you">ти</span>' : '')
        + (alive ? '<span class="gk-hp">❤' + hp + '</span>' : '<span>⛔</span>')
        + (alive && hut.poison > 0 ? '<span title="отруєна хроном">🌿</span>' : '')
        + (alive && (hut.honey > 0 || hut.stuck) ? '<span title="у меду — не рушить">🍯</span>' : '')
        + (alive && st.volley && st.phase === 'aim' && st.ready[i] ? '<span class="gk-rd" title="постріл заряджено">✓</span>' : '')
        + (st.wins[i] ? '<span class="gk-star">★' + st.wins[i] + '</span>' : '')
        + (alive ? '<i class="gk-bar' + bar + '" style="width:' + clamp(hp, 0, 100) + '%"></i>' : '')
        + '</span>';
    }
    if (E.sig.chips !== html) { E.sig.chips = html; E.chips.innerHTML = html; }
    const wind = st.phase === 'lobby' ? '' : 'вітер <b class="gk-arrow">' + windText(st.wind) + '</b>';
    if (E.sig.wind !== wind) {
      E.sig.wind = wind;
      E.wind.innerHTML = wind;
      E.wind.hidden = !wind;
      E.wind.classList.toggle('calm', !st.wind);
    }
    const timed = !!(st.phase === 'aim' && st.endsAt && ctx.playing);
    // Місце під дугу тримаємо всю партію (невидимою поза прицілом): дуга, що з'являлась лише в прицілі, робила
    // смужку вищою на 12 px, і поле смикалось двічі за хід саме тоді, коли всі дивляться на політ.
    const keep = !!ctx.playing && st.phase !== 'lobby' && st.phase !== 'over';
    if (keep && !E.clock.querySelector('.garc')) HGames.ui.timerArc(E.clock, new Date().toISOString(), 1000).stop();
    if (timed) {
      HGames.ui.timerArc(E.clock, st.endsAt, st.turnMs);
      const who = st.volley ? 'залп' : st.turn === mine ? 'твій хід' : nickOf(st, st.turn);
      if (E.who.textContent !== who) E.who.textContent = who;
    } else {
      const arc = E.clock.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
    }
    if (E.clock.hidden === keep) E.clock.hidden = !keep;
    E.clock.classList.toggle('gk-idle', !timed);
    paintSound(st);
  }

  function paintArms(st) {
    const E = st.els;
    if (!E) return;
    // руїна дивиться на комору як глядач: чим стріляє той, хто ходить
    const seat = me(st);
    const mine = seat != null && (st.phase === 'lobby' || st.phase === 'start' || st.huts[seat].alive) ? seat : null;
    const who = mine != null ? mine : st.turn;
    const inv = invOf(st, who != null && who >= 0 ? who : null);
    let sel = mine != null ? (myTurn(st) ? st.my.w : st.preW != null ? st.preW : st.my.w)
      : !st.volley && (st.phase === 'aim' || st.phase === 'fly') ? st.aimTurn[2] : -1;
    // останній вареник вистрілено — між ходами підсвічуємо вже глек, а не «Вареник ×0»
    if (mine != null && sel >= 0 && inv[sel] === 0) sel = 0;
    const ro = mine == null || !st.ctx.playing;
    const hide = st.phase === 'over';
    if (E.arms.hidden !== hide) E.arms.hidden = hide;
    const sig = inv.join(',') + '|' + sel + '|' + ro + '|' + st.kinds;
    if (E.sig.arms === sig) return;
    E.sig.arms = sig;
    E.arms.querySelectorAll('.gk-arm').forEach((b) => {
      const w = +b.dataset.w, left = inv[w] == null ? 0 : inv[w];
      const off = w >= st.kinds;
      if (b.hidden !== off) b.hidden = off;
      b.classList.toggle('on', w === sel);
      b.classList.toggle('ro', ro);
      b.disabled = left === 0 || ro;
      b.querySelector('.gk-left').textContent = left < 0 ? '∞' : '×' + left;
    });
  }

  function paintCtl(st) {
    const E = st.els;
    if (!E) return;
    // пульт є тим, хто сидить, поки йде партія; у лобі й після кінця він лише заважав би підсумку
    // руїні ходити вже нічим — пульт ховаємо, лишається комора й підсумок
    // руїні лишаються тільки емоції (клас gk-dead ховає решту)
    const mine0 = me(st);
    const show = !!st.ctx.mine && st.ctx.playing && st.phase !== 'over' && mine0 != null;
    if (E.ctl.hidden === show) E.ctl.hidden = !show;
    if (!show) return;
    const mine = me(st);
    const can = myTurn(st);
    const dead = st.phase !== 'start' && !st.huts[mine].alive;
    // не мій хід — замість «Постріл / Пропустити» кнопки емоцій (той самий рядок, поле не смикається)
    if (E.ctl.classList.contains('gk-wait') === can) E.ctl.classList.toggle('gk-wait', !can);
    if (E.ctl.classList.contains('gk-dead') !== dead) E.ctl.classList.toggle('gk-dead', dead);
    const a = st.my;
    const fuel = st.huts[mine] ? st.huts[mine].fuel : 0;
    const t1 = a.a + '°', t2 = String(a.p), t3 = '⛽ ' + fuel;
    if (E.a.textContent !== t1) E.a.textContent = t1;
    if (E.p.textContent !== t2) E.p.textContent = t2;
    if (E.fuel.textContent !== t3) E.fuel.textContent = t3;
    const sig = can + '|' + fuel;
    if (E.sig.ctl === sig) return;
    E.sig.ctl = sig;
    E.ctl.querySelectorAll('button').forEach((b) => {
      const k = b.dataset.k;
      const off = k !== 'e' && (!can || (k === 'm' && fuel < 8));
      if (b.disabled !== off) b.disabled = off;
    });
    if (!can && E.skip.textContent !== 'Пропустити') E.skip.textContent = 'Пропустити';
  }

  function paintCharge(st, now) {
    const bar = st.els && st.els.chargeBar;
    if (!bar) return;
    const w = st.charge && now - st.charge.t0 >= SHORT_PRESS ? chargePower(st, now) + '%' : '0%';
    if (bar.style.width !== w) bar.style.width = w;
  }

  function paintTip(st) {
    const E = st.els;
    const want = HGames.ui.coarse() && window.innerWidth < window.innerHeight && st.ctx.playing && store.get('glekomet.turnHint', '') !== '1';
    if (want && !E.tip.innerHTML) E.tip.innerHTML = '📱 Поверни телефон боком — поле більше (а з ⛶ — ще більше) <button type="button">ясно</button>';
    if (E.tip.hidden === want) E.tip.hidden = !want;
  }

  function paintSum(st) {
    const E = st.els, ctx = st.ctx;
    const show = st.phase === 'over' && !!st.result;
    if (E.sum.hidden === show) E.sum.hidden = !show;
    if (!show) { E.sig.sum = ''; return; }
    const r = st.result;
    const rows = [];
    // у командах — спершу парні 🔴, потім непарні 🔵, щоб було видно, хто з ким
    let odd = false;
    for (const i of st.teams ? [0, 2, 4, 1, 3, 5] : [0, 1, 2, 3, 4, 5]) {
      const hut = st.huts[i];
      if (!hut.plays) continue;
      const s = st.stats[i] || {};
      const win = (r.winners || []).includes(i);
      const split = st.teams && i % 2 === 1 && !odd;                  // риска між командами
      if (split) odd = true;
      const cls = (win ? 'win' : '') + (split ? ' gk-team2' : '');
      rows.push('<tr' + (cls ? ' class="' + cls.trim() + '"' : '') + '><td><i class="gk-dot" style="background:' + st.pal.seats[i] + '"></i>'
        + (st.teams ? (i % 2 ? '🔵 ' : '🔴 ') : '')
        + ctx.esc(nickOf(st, i)) + (win ? ' 🏆' : '') + (st.wins[i] ? ' <span class="gk-star">★' + st.wins[i] + '</span>' : '') + '</td>'
        + '<td>' + (s.dmg || 0) + '</td><td>' + (s.hits || 0) + '/' + (s.shots || 0) + '</td><td>' + (s.kills || 0) + '</td><td>' + (s.self || 0) + '</td></tr>');
    }
    const b = r.best;
    const best = b && b.dmg > 0
      ? '🏆 Найкращий постріл: ' + ctx.esc(nickOf(st, b.seat)) + (b.to != null && b.to >= 0 ? ' → ' + ctx.esc(nickOf(st, b.to)) : '') + ' ' + b.dmg
        + ' (' + (WEAPONS[b.w] ? WEAPONS[b.w].icon + ' ' + WEAPONS[b.w].short : '') + ')'
      : 'Цього разу обійшлось без влучань';
    const why = { last: 'остання хата в селі', team: 'остання команда в селі', draw: 'нічия', idle: 'ніхто не стріляв — розійшлись', left: 'партію не дограли' }[r.reason] || '';
    const html = '<table><tr><th>хата</th><th>шкода</th><th title="влучних пострілів із усіх">влучно</th><th>руїн</th><th title="шкода своїй хаті й союзникам">по своїх</th></tr>' + rows.join('') + '</table>'
      + '<div class="gk-best">' + best + '</div>' + (why ? '<div class="gk-rs">' + why + ' · кіл: ' + st.round + '</div>' : '')
      + seriesLine(st);
    if (E.sig.sum !== html) { E.sig.sum = html; E.sum.innerHTML = html; }
  }

  /// Підсумок серії (з другої партії): шкода за всі «Ще раз», найвлучніший, руйнівник і постріл серії.
  /// Перемоги вечора тут не рахуємо — їх показує каркас.
  function seriesLine(st) {
    const ser = st.view && st.view.series, ctx = st.ctx;
    if (!ser || ser.games < 2) return '';
    const rows = [];
    for (let i = 0; i < 6; i++) if (ser.rows && ser.rows[i]) rows.push({ i, r: ser.rows[i] });
    if (!rows.length) return '';
    const name = (i) => ctx.esc(nickOf(st, i));
    rows.sort((a, b) => b.r.dmg - a.r.dmg || a.i - b.i);
    // суми шкоди всіх уже видно в «Вечорі» каркаса — тут лише лідер
    const dmg = rows[0].r.dmg > 0 ? ' · 👑 найбільше шкоди: ' + name(rows[0].i) + ' ' + rows[0].r.dmg : '';
    const acc = rows.filter((x) => x.r.shots >= 3).sort((a, b) => b.r.hits / b.r.shots - a.r.hits / a.r.shots)[0];
    const kil = rows.slice().sort((a, b) => b.r.kills - a.r.kills)[0];
    const n = ser.games, word = n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14) ? 'партії' : 'партій';
    const b = ser.best, w = b && WEAPONS[b.w];
    return '<div class="gk-ser"><b>📜 Серія: ' + n + ' ' + word + '</b>' + dmg
      + (acc ? ' · 🎯 найвлучніший: ' + name(acc.i) + ' ' + Math.round((100 * acc.r.hits) / acc.r.shots) + '%' : '')
      + (kil && kil.r.kills > 0 ? ' · 🏚 руйнівник: ' + name(kil.i) + ' ×' + kil.r.kills : '')
      + (b && b.dmg > 0 ? '<br>🏆 Постріл серії: ' + ctx.esc(b.nick) + (b.to ? ' → ' + ctx.esc(b.to) : '') + ' ' + b.dmg
        + (w ? ' (' + w.icon + ' ' + w.short + ', партія ' + b.game + ')' : '') : '')
      + '</div>';
  }

  function paintAll(st) {
    if (!st.els || !st.pal) return;
    paintTop(st);
    paintArms(st);
    paintCtl(st);
    paintTip(st);
    paintSum(st);
    syncCanvasCls(st);
  }

  /// Лобі столу на 2+: хто сидить, бачить, чи вже можна рушати (каркас пише «Чекаємо, хто підсяде», навіть коли
  /// господареві досить натиснути «Почати»). Глядач — без статусу, як і було.
  function lobbyLine(ctx) {
    const r = ctx.room;
    if (!r || r.status !== 'lobby' || !ctx.mine) return '';
    const seated = (r.seats || []).filter((s) => s.nick).length;
    const host = String(r.host || '').toLowerCase() === String((ctx.me && ctx.me.nick) || '').toLowerCase();
    if (seated < (r.minPlayers || 2)) return host ? 'Чекаємо, хто підсяде: гукни когось за стіл 📣' : 'Чекаємо, хто підсяде';
    return host ? 'Гайда: тисни «Почати» — або зачекай, хто ще підсяде' : 'Чекаємо, поки господар тисне «Почати»';
  }

  // =============================================================================================
  // Цикл
  // =============================================================================================

  /// Лобі чи підсумок без разових анімацій (лелека, вибухи, цифри шкоди, свіжа земля, повтор найкращого пострілу):
  /// рухаються лише хмари й дим — їм досить 20 кадрів на секунду.
  function calmNow(st, now) {
    if (st.phase !== 'lobby' && st.phase !== 'over') return false;
    if (st.stork || st.rings.length || st.floats.length || st.fresh.length || now < st.shakeUntil) return false;
    return !(st.replay && !st.replay.boom);
  }

  /// rAF — лише поки треба (прохід 28.09): схована картка чи вкладка — перевірка раз на 0,2 с без rAF; тихе лобі й
  /// підсумок — 20 кадрів/с; вид чи розмір будять одразу (wake). Було 60 повних кадрів/с усюди, і в схованій картці теж.
  function spin(st) {
    if (st.raf || st.idleT) return;
    st.lastT = performance.now();
    const loop = () => {
      st.raf = 0;
      const el = st.cv && st.cv.el;
      if (!el || !el.isConnected) return;
      const shown = !document.hidden && !!el.offsetParent;
      st.away = !shown;
      const now = performance.now();
      if (shown) {
        const dt = Math.min(0.1, (now - st.lastT) / 1000);
        st.lastT = now;
        if (!st.bgC) fit(st);
        if (st.bgC) {
          tickInput(st, now);
          const t0 = performance.now();
          draw(st, now, dt);
          st.perf[st.perfN++ % st.perf.length] = performance.now() - t0;
        }
      }
      const gap = !shown ? AWAY_MS : calmNow(st, now) ? CALM_MS : 0;
      if (gap) st.idleT = setTimeout(() => { st.idleT = 0; st.raf = requestAnimationFrame(loop); }, gap);
      else st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  /// Цикл дрімає — розбудити зараз; force — і тоді, коли картка була схована (кадри в схованій будити не мусять).
  function wake(st, force) {
    if (!st || !st.idleT || (st.away && !force)) return;
    clearTimeout(st.idleT);
    st.idleT = 0;
    spin(st);
  }

  HGames.register({
    id: 'glekomet',
    added: '2026-09-27',
    icon: ICON,
    seatNames: ['жовта', 'зелена', 'руда', 'сіра', 'синя', 'рожева'],
    seatClass: ['x', 'o', 'c', 'gk-g', 'gk-b', 'gk-p'],
    pad: {
      dirs: true,
      a: 'Space',
      x: 'KeyE',
      on(btn, ctx) {
        const st = ctx._gk;
        if (st && btn === 'x' && !myTurn(st)) return emote(st, st.emoNext);   // чужий хід: Ⓧ — емоція по колу
        if (!st || (btn !== 'lb' && btn !== 'rb')) return false;
        move(st, btn === 'lb' ? -1 : 1);
        return true;
      },
      // Ⓐ коротко — постріл із тією силою, що виставив стіком; тримати — заряд із нуля (сила — скільки тримав)
      hint: '{dpad} кут і сила · {a} постріл, тримай — заряд · {x} снаряд (чужий хід — емоція) · {lb}{rb} посунути хату',
    },
    news: {
      v: '2026-09-29',
      title: 'Глекомети: залп, приколи й погода',
      items: [
        '💥 Опція «Хід: Залп» — усі цілять разом, чужих прицілів не видно, і снаряди летять одночасно',
        '🐓 «Комора з приколами»: півень, мед (хата прилипає), смерч (розкидає хати) і підкова-магніт',
        '❄ «Погода й мапа»: зима — хати ковзають, ніч — видно лише вогні й спалахи, ярмарок зі ставком',
        '😂 Поки ходить інший — емоції над своєю хатою: 1–4, Ⓧ або кнопки внизу',
        '📜 Після «Ще раз» — підсумок серії; на телефоні камера летить за снарядом',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      build(root, st);
      st.cv = HGames.ui.canvas(root, { w: 1000, h: 500, cls: canvasCls(st) });
      root.insertBefore(st.cv.el, st.els.arms);
      st.g = st.cv.ctx;
      st.pal = palette(st);
      const el = st.cv.el;
      el.addEventListener('pointerdown', (e) => pointerDown(st, e));
      el.addEventListener('pointermove', (e) => pointerMove(st, e));
      el.addEventListener('pointerup', (e) => pointerUp(st, e, false));
      el.addEventListener('pointercancel', (e) => pointerUp(st, e, true));
      el.addEventListener('wheel', (e) => {
        if (!myTurn(st)) return;
        e.preventDefault();
        const d = e.deltaY < 0 ? 1 : -1;
        if (e.shiftKey) setAim(st, st.my.a + d * 2, st.my.p); else setAim(st, st.my.a, st.my.p + d * 2);
      }, { passive: false });
      st.keyup = (e) => {
        const code = e.code;
        if (code === 'ArrowLeft' || code === 'ArrowRight') release(st, 'a');
        else if (code === 'ArrowUp' || code === 'ArrowDown' || code === 'KeyW' || code === 'KeyS') release(st, 'p');
        else if ((code === 'Space' || e.key === ' ') && st.charge && st.charge.src === 'key') endCharge(st);
      };
      document.addEventListener('keyup', st.keyup);
      if (window.ResizeObserver) {
        // висоту поля — у наступному кадрі: зміна розміру просто в колбеку давала «ResizeObserver loop» у консолі
        st.ro = new ResizeObserver(() => { fit(st); if (!st.fitQ) st.fitQ = requestAnimationFrame(() => { st.fitQ = 0; if (st.cv) fitHeight(st); }); wake(st, true); });
        st.ro.observe(el);
        const card = root.closest('.gtable');
        if (card) st.ro.observe(card);
      }
      st.onResize = () => { fitHeight(st); wake(st, true); };
      window.addEventListener('resize', st.onResize);
      fit(st);
      spin(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      if (!st.cv) return;
      if (ctx.view && ctx.view !== st.view) applyView(st, ctx.view);
      fit(st);
      paintAll(st);
      fitHeight(st);
      wake(st, true);
      spin(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!st.cv || !f || !st.pal) return;
      const phase = st.phase;
      applyFrame(st, f);
      if (f.hp || f.hx || f.ph !== phase || f.aim || f.rd != null) paintAll(st);
      wake(st);
    },

    onKey(e, ctx) {
      const st = ctx._gk;
      if (!st || !ctx.mine || !ctx.playing) return false;
      const code = e.code;
      const mineNow = myTurn(st);
      switch (code) {
        case 'ArrowLeft': case 'ArrowRight':
          if (!e.repeat && mineNow) hold(st, 'a', code === 'ArrowLeft' ? 1 : -1, e.shiftKey);
          return true;
        case 'ArrowUp': case 'ArrowDown': case 'KeyW': case 'KeyS':
          if (!e.repeat && mineNow) hold(st, 'p', code === 'ArrowUp' || code === 'KeyW' ? 1 : -1, e.shiftKey);
          return true;
        case 'Space':
          if (!e.repeat && mineNow && !early(st)) startCharge(st, 'key');
          return true;
        case 'Enter': case 'NumpadEnter':
          if (!mineNow) return false;
          if (!e.repeat && !early(st)) fire(st);
          return true;
        case 'KeyA': case 'KeyD':
          if (mineNow) move(st, code === 'KeyA' ? -1 : 1);
          return true;
        case 'KeyQ': case 'KeyE':
          cycleWeapon(st, code === 'KeyQ' ? -1 : 1);
          return true;
        default: {
          const m = /^(?:Digit|Numpad)([0-9])$/.exec(code || '');
          // чужий хід: 1–4 — емоції над хатою, свій — снаряди (0 — десятий, підкова)
          if (m && !mineNow && +m[1] >= 1 && +m[1] <= EMOS.length) { if (!e.repeat) emote(st, +m[1] - 1); return true; }
          if (m) { pickWeapon(st, (+m[1] + 9) % 10); return true; }
          if (e.key === ' ') { if (!e.repeat && mineNow && !early(st)) startCharge(st, 'key'); return true; }
          return false;
        }
      }
    },

    status(ctx) {
      const st = ctx._gk;
      if (ctx.room && ctx.room.status === 'lobby') return lobbyLine(ctx);
      if (!st || !ctx.playing) return '';
      const s = me(st);
      switch (st.phase) {
        case 'start': return 'Готуйсь…';
        case 'aim': {
          if (st.volley) {
            let alive = 0, rd = 0;
            for (let i = 0; i < 6; i++) if (st.huts[i].alive) { alive++; if (st.ready[i]) rd++; }
            const tail = ' · готові ' + rd + '/' + alive + ' · вітер ' + windText(st.wind);
            if (s == null) return '💥 Залп: усі цілять разом' + tail;
            if (!st.huts[s].alive) return 'Твоя хата — руїна · залп' + tail;
            if (st.ready[s] || st.fired === st.turnNo) return '✓ Заряджено — чекаємо решту' + tail;
            if (padOn()) return '💥 Залп! Цілься стіком, Ⓐ — зарядити' + tail;
            return '💥 Залп! Цілься — чужих прицілів не видно, летить усе разом' + tail;
          }
          const who = nickOf(st, st.turn);
          const wind = 'вітер ' + windText(st.wind);
          if (s == null) return 'Дивишся збоку · ходить ' + who + ' · ' + wind;
          if (!st.huts[s].alive) return 'Твоя хата — руїна · ходить ' + who;
          if (st.turn !== s) return 'Ходить ' + who + ' · ' + wind;
          if (st.fired === st.turnNo) return 'Летить…';
          if (padOn()) return 'Твій хід · стік — кут і сила, Ⓐ — постріл (тримай — заряд із нуля)';
          if (HGames.ui.coarse()) return 'Твій хід · потягни по полю, як рогатку, і відпусти';
          return 'Твій хід · ← → кут, ↑ ↓ сила, пробіл — постріл, A/D — посунутись';
        }
        case 'fly': return 'Летить…';
        case 'settle': return (st.last && st.last.text) || (st.view && st.view.last && st.view.last.text) || '';
        default: return '';
      }
    },

    unmount(root) {
      const st = root._gk;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      cancelAnimationFrame(st.fitQ || 0);
      st.raf = 0;
      clearTimeout(st.idleT);
      st.idleT = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.ro) st.ro.disconnect();
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (st.ac) st.ac.close().catch(() => {});
      if (st.els) st.els.ctl.querySelectorAll('button').forEach((b) => { if (b._rep) clearInterval(b._rep); });
      root._gk = null;
    },
  });
})();
