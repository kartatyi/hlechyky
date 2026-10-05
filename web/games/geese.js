// «Порахуй гусей» (geese) і «Гуси дня» (geese-daily): через двір біжить парад живності, потім Дядько Глек питає
// «скільки?». Правила, склад параду й час — на сервері (Impl/Geese.cs, spec docs/games/specs/geese.md §4): клієнт лише
// малює той самий список тварин від `el` (мс від початку фази), тож у всіх за столом парад іде однаково.
// Модуль живе і за столом, і всередині «Глечикової вечірки» (HGames.embed): нічого поза ctx і своїм root.
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M4.6 3.3c0-1.3 1.7-1.7 2.3-.6.4.8.1 1.7-.3 2.7-.5 1.3-.2 2.4 1 2.9h4.2c1.3 0 2.1 1 1.8 2.1-.4 1.6-2 2.8-4.4 2.8H7.3c-2.2 0-3.7-1.4-3.7-3.1 0-1.2.7-2 1.3-2.9.4-.8.3-1.6-.3-2.4z" fill="var(--text)"/>'
    + '<path d="M4.5 3 2.4 3.8l2.2.5z" fill="var(--accent)"/>'
    + '<path d="M6.6 13.4v1.4M9.2 13.4v1.4" stroke="var(--clay)" stroke-width="1.3" stroke-linecap="round"/></svg>';

  const EMOJI_FONT = '"Apple Color Emoji","Segoe UI Emoji","Noto Color Emoji","Twemoji Mozilla",sans-serif';
  // Імена — як у GeeseParade.Kinds; емодзі гуски підмінимо лебедем, якщо система 🪿 ще не знає (див. gooseEmo).
  const KINDS = {
    goose: { emo: '🪿', one: 'гуска', many: 'гусей' },
    hen: { emo: '🐔', one: 'курка', many: 'курей' },
    duck: { emo: '🦆', one: 'качка', many: 'качок' },
    goat: { emo: '🐐', one: 'коза', many: 'кіз' },
    pig: { emo: '🐖', one: 'порося', many: 'поросят' },
    cat: { emo: '🐈', one: 'кіт', many: 'котів' },
  };
  const TRAITS = {
    hustka: 'коза в хустці', hlechyk: 'гусак із глечиком', chorna: 'чорна курка', bant: 'порося з бантиком', rudyi: 'рудий кіт',
  };
  const SEAT = ['#5aa9ff', '#d9825b', '#7bd389', '#f4c542', '#b48cf2', '#6fd6c2', '#f08cb8', '#b7c2bd'];
  const W = 720;                       // логічна ширина двору; висота — від ширини картки (fitH)

  let gooseEmoCache = null;
  /// 🪿 — Emoji 15 (2022): старий телефон замість неї малює порожній квадрат. Пробуємо раз: є кольорові пікселі — є гуска.
  function gooseEmo() {
    if (gooseEmoCache) return gooseEmoCache;
    gooseEmoCache = '🦢';
    try {
      const c = document.createElement('canvas');
      c.width = c.height = 28;
      const g = c.getContext('2d', { willReadFrequently: true });
      g.font = '22px ' + EMOJI_FONT;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText('🪿', 14, 15);
      const d = g.getImageData(0, 0, 28, 28).data;
      let col = 0;
      for (let i = 0; i < d.length; i += 4) {
        if (d[i + 3] > 120 && (Math.abs(d[i] - d[i + 1]) > 40 || Math.abs(d[i + 1] - d[i + 2]) > 40)) col++;
      }
      if (col > 6) gooseEmoCache = '🪿';
    } catch (e) { /* лишаємо лебедя */ }
    return gooseEmoCache;
  }
  const emo = (k) => (k === 'goose' ? gooseEmo() : (KINDS[k] || {}).emo || '❔');

  // ---------- стан столу ----------

  function state(root, ctx) {
    const st = root._geese || (root._geese = {
      base: 0, key: '', round: -1, val: null, sel: -1, sending: false, raf: 0, lastDraw: 0,
      sprites: new Map(), bg: null, bgKey: '', prep: null, prepKey: '', answered: [], pulse: 0, lastCount: 0,
      repT: 0, lastKeyAt: 0, htmlCache: new Map(),
    });
    st.ctx = ctx;
    st.root = root;
    return st;
  }

  const view = (st) => (st.ctx && st.ctx.view) || {};

  /// Годинник фази: base = performance.now() − el. Новий раунд чи фаза — стрибок, інакше легке підтягування
  /// (кадр приходить з мережею, і різкий стрибок смикав би тварин назад-вперед).
  function sync(st, ph, round, el) {
    if (el == null) return;
    const key = round + ':' + ph;
    const want = performance.now() - el;
    if (st.key !== key) { st.key = key; st.base = want; return; }
    const d = want - st.base;
    // Мережа лише додає запізнення, тож найточніший кадр — той, що прийшов найшвидше (найменший base). Йому
    // віримо швидко, а пізнім кадрам — ледь-ледь: так годинник тримається найменшої затримки, а не середньої,
    // і все ж поволі відпускає, якщо годинники справді розійшлись.
    if (Math.abs(d) > 250) st.base = want;
    else if (d < 0) st.base += d * 0.6;
    else st.base += d * 0.05;
  }

  const phaseT = (st) => performance.now() - st.base;

  // ---------- двір: розміри ----------

  /// Широка картка — двір 2:1; телефон — 3:2, щоб три доріжки не злипались у смужки.
  function fitH(root) {
    const w = root.clientWidth || 600;
    return w < 560 ? 480 : 360;
  }

  function geom(H, lanes) {
    const back = Math.round(H * 0.2);
    const laneH = (H - back - 8) / lanes;
    const S = Math.min(lanes === 1 ? 84 : 92, laneH * 0.74);
    return { H, back, laneH, S, lanes };
  }
  const laneMid = (g, l) => g.back + 4 + g.laneH * (l + 0.5);

  // ---------- малювання: тло ----------

  function rng(seed) {
    let s = (seed >>> 0) || 1;
    return () => { s ^= s << 13; s >>>= 0; s ^= s >>> 17; s ^= s << 5; s >>>= 0; return s / 4294967296; };
  }

  /// Тло рахуємо раз на розмір/кількість доріжок: небо, хата, тин, трава, стежки.
  function background(st, H, lanes, dpr) {
    const key = H + ':' + lanes + ':' + dpr;
    if (st.bg && st.bgKey === key) return st.bg;
    const c = document.createElement('canvas');
    c.width = Math.round(W * dpr);
    c.height = Math.round(H * dpr);
    const g = c.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    const G = geom(H, lanes);
    const r = rng(7 + lanes * 31);
    // небо
    const sky = g.createLinearGradient(0, 0, 0, G.back);
    sky.addColorStop(0, '#8fd0f2');
    sky.addColorStop(1, '#d9f0e6');
    g.fillStyle = sky;
    g.fillRect(0, 0, W, G.back + 2);
    g.fillStyle = '#ffe680';
    g.beginPath(); g.arc(W * 0.1, G.back * 0.38, G.back * 0.22, 0, 7); g.fill();
    // хата під стріхою праворуч
    const hx = W * 0.74, hb = G.back - 2, hw = W * 0.16, hh = G.back * 0.5;
    g.fillStyle = '#f6f1e4';
    g.fillRect(hx, hb - hh, hw, hh);
    g.fillStyle = '#5d8fc4';
    g.fillRect(hx + hw * 0.18, hb - hh * 0.72, hw * 0.16, hh * 0.38);
    g.fillRect(hx + hw * 0.62, hb - hh * 0.72, hw * 0.16, hh * 0.38);
    g.fillStyle = '#c49a3c';
    g.beginPath();
    g.moveTo(hx - hw * 0.1, hb - hh + 2); g.lineTo(hx + hw * 0.5, hb - hh - G.back * 0.42); g.lineTo(hx + hw * 1.1, hb - hh + 2);
    g.closePath(); g.fill();
    g.strokeStyle = 'rgba(120,84,30,.45)';
    g.lineWidth = 1;
    for (let i = 0; i < 9; i++) {
      g.beginPath(); g.moveTo(hx + hw * 0.5, hb - hh - G.back * 0.4); g.lineTo(hx - hw * 0.08 + i * hw * 0.15, hb - hh + 1); g.stroke();
    }
    // соняхи за тином
    for (const sx of [W * 0.3, W * 0.36, W * 0.58]) {
      g.strokeStyle = '#4f8a3a'; g.lineWidth = 2.5;
      g.beginPath(); g.moveTo(sx, G.back); g.lineTo(sx, G.back * 0.35); g.stroke();
      g.fillStyle = '#f2c230';
      g.beginPath(); g.arc(sx, G.back * 0.33, G.back * 0.13, 0, 7); g.fill();
      g.fillStyle = '#6b4320';
      g.beginPath(); g.arc(sx, G.back * 0.33, G.back * 0.06, 0, 7); g.fill();
    }
    // трава
    g.fillStyle = '#7fb95a';
    g.fillRect(0, G.back, W, H - G.back);
    // задній тин уздовж неба
    wattle(g, 0, G.back - G.back * 0.34, W, G.back + 3, r);
    // кущики трави
    g.strokeStyle = '#5c9a3e';
    g.lineWidth = 1.6;
    for (let i = 0; i < 70; i++) {
      const x = r() * W, y = G.back + 6 + r() * (H - G.back - 8);
      g.beginPath(); g.moveTo(x - 3, y); g.lineTo(x - 1, y - 6); g.moveTo(x, y); g.lineTo(x + 1, y - 8); g.moveTo(x + 3, y); g.lineTo(x + 4, y - 5); g.stroke();
    }
    // стежки
    for (let l = 0; l < lanes; l++) {
      const y = laneMid(G, l);
      const ph = G.laneH * 0.62;
      g.fillStyle = '#d7b98a';
      g.beginPath();
      g.moveTo(0, y - ph * 0.42);
      for (let x = 0; x <= W; x += 40) g.lineTo(x, y - ph * 0.42 + Math.sin(x * 0.02 + l) * 3);
      g.lineTo(W, y + ph * 0.55);
      for (let x = W; x >= 0; x -= 40) g.lineTo(x, y + ph * 0.55 + Math.sin(x * 0.017 + l * 2) * 3);
      g.closePath();
      g.fill();
      g.fillStyle = 'rgba(140,100,55,.22)';
      for (let i = 0; i < 40; i++) g.fillRect(r() * W, y - ph * 0.3 + r() * ph * 0.8, 3 + r() * 5, 1.5);
    }
    st.bg = c;
    st.bgKey = key;
    return c;
  }

  /// Плетений тин: кілки й лозини хвилею.
  function wattle(g, x0, y0, x1, y1, r) {
    const h = y1 - y0;
    g.fillStyle = '#7a5530';
    for (let x = x0 + 6; x < x1; x += 22) g.fillRect(x, y0 - h * 0.12, 4, h * 1.1);
    const rows = Math.max(3, Math.round(h / 7));
    for (let i = 0; i < rows; i++) {
      const y = y0 + (i + 0.5) * h / rows;
      g.strokeStyle = i % 2 ? '#9a7040' : '#b58552';
      g.lineWidth = h / rows * 0.8;
      g.beginPath();
      for (let x = x0; x <= x1; x += 11) {
        const yy = y + Math.sin((x - x0) / 11 * Math.PI + i * Math.PI) * h / rows * 0.18;
        if (x === x0) g.moveTo(x, yy); else g.lineTo(x, yy);
      }
      g.stroke();
    }
    if (r) {
      g.fillStyle = 'rgba(60,35,15,.25)';
      for (let i = 0; i < (x1 - x0) / 12; i++) g.fillRect(x0 + r() * (x1 - x0), y0 + r() * h, 2, 2);
    }
  }

  /// Віз із сіном: ящик, сіно горбом, два колеса.
  function cart(g, x0, y0, x1, y1) {
    const w = x1 - x0, h = y1 - y0;
    g.fillStyle = '#e9c860';
    g.beginPath();
    g.moveTo(x0 + 2, y0 + h * 0.18);
    for (let i = 0; i <= 8; i++) g.quadraticCurveTo(x0 + w * (i + 0.5) / 8, y0 - h * 0.28, x0 + w * (i + 1) / 8, y0 + h * 0.15);
    g.lineTo(x1 - 2, y0 + h * 0.3);
    g.closePath();
    g.fill();
    g.fillStyle = '#8b5a2b';
    g.fillRect(x0, y0 + h * 0.2, w, h * 0.5);
    g.strokeStyle = '#6a421c';
    g.lineWidth = 2;
    for (let i = 1; i < 3; i++) { g.beginPath(); g.moveTo(x0, y0 + h * (0.2 + i * 0.166)); g.lineTo(x1, y0 + h * (0.2 + i * 0.166)); g.stroke(); }
    for (const wx of [x0 + w * 0.22, x1 - w * 0.22]) {
      const rr = Math.min(h * 0.3, w * 0.18);
      const wy = y1 - rr;
      g.fillStyle = '#5a3a1a';
      g.beginPath(); g.arc(wx, wy, rr, 0, 7); g.fill();
      g.fillStyle = '#c99a5b';
      g.beginPath(); g.arc(wx, wy, rr * 0.78, 0, 7); g.fill();
      g.strokeStyle = '#5a3a1a';
      g.lineWidth = 2;
      for (let k = 0; k < 6; k++) { const a = k * Math.PI / 3; g.beginPath(); g.moveTo(wx, wy); g.lineTo(wx + Math.cos(a) * rr * 0.78, wy + Math.sin(a) * rr * 0.78); g.stroke(); }
    }
  }

  // ---------- малювання: тварини ----------

  /// Спрайт тварини (обличчям ліворуч, як майже всі емодзі): емодзі + прикмета. Полотно 1,2S × 1,4S, тварина
  /// внизу посередині — над головою місце для глечика.
  function sprite(st, k, tr, S, dpr) {
    const key = k + '|' + (tr || '') + '|' + Math.round(S * dpr);
    let c = st.sprites.get(key);
    if (c) return c;
    const px = S * dpr;
    c = document.createElement('canvas');
    c.width = Math.ceil(px * 1.2);
    c.height = Math.ceil(px * 1.4);
    const g = c.getContext('2d');
    const cx = px * 0.6, cy = px * 0.85;
    g.font = Math.round(px * 0.9) + 'px ' + EMOJI_FONT;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(emo(k), cx, cy);
    // Тінування лише по самій тварині: source-atop фарбує непрозоре й не чіпає тло.
    const tint = (color, a) => {
      g.save(); g.globalCompositeOperation = 'source-atop'; g.globalAlpha = a; g.fillStyle = color;
      g.fillRect(0, 0, c.width, c.height); g.restore();
    };
    if (k === 'cat') tint(tr === 'rudyi' ? '#ff7a12' : '#8a8f99', tr === 'rudyi' ? 0.62 : 0.55);   // звичайний кіт сірий, щоб рудого не сплутати
    if (tr === 'chorna') {
      tint('#1d1a1a', 0.72);
      // Чорна курка на траві — темна пляма: світлий обвід повертає їй силует курки (гребінь просвічує крізь тон).
      const src = document.createElement('canvas');
      src.width = c.width;
      src.height = c.height;
      src.getContext('2d').drawImage(c, 0, 0);
      g.clearRect(0, 0, c.width, c.height);
      const o = Math.max(1, px * 0.035);
      for (let a = 0; a < 8; a++) g.drawImage(src, Math.cos(a * Math.PI / 4) * o, Math.sin(a * Math.PI / 4) * o);
      tint('#f3ead2', 1);
      g.drawImage(src, 0, 0);
    }
    const u = px;   // одиниця — розмір тварини
    const hx = cx - u * 0.22, hy = cy - u * 0.28;   // приблизно голова (ліва верхня чверть)
    if (tr === 'hustka') {
      g.fillStyle = '#d8283a';
      g.beginPath();
      g.moveTo(hx - u * 0.16, hy - u * 0.08);
      g.quadraticCurveTo(hx, hy - u * 0.26, hx + u * 0.18, hy - u * 0.06);
      g.lineTo(hx + u * 0.08, hy + u * 0.16);
      g.closePath();
      g.fill();
      g.fillStyle = '#fff';
      for (const [dx, dy] of [[-0.05, -0.1], [0.06, -0.12], [0.04, 0.02]]) { g.beginPath(); g.arc(hx + u * dx, hy + u * dy, u * 0.022, 0, 7); g.fill(); }
      g.fillStyle = '#a81828';
      g.beginPath(); g.arc(hx + u * 0.17, hy - u * 0.04, u * 0.045, 0, 7); g.fill();
    } else if (tr === 'hlechyk') {
      const px0 = hx + u * 0.02, py0 = cy - u * 0.5;
      g.fillStyle = '#b8652d';
      g.beginPath();
      g.moveTo(px0 - u * 0.07, py0 - u * 0.2);
      g.lineTo(px0 + u * 0.07, py0 - u * 0.2);
      g.quadraticCurveTo(px0 + u * 0.06, py0 - u * 0.14, px0 + u * 0.13, py0 - u * 0.06);
      g.quadraticCurveTo(px0 + u * 0.15, py0 + u * 0.04, px0 + u * 0.06, py0 + u * 0.06);
      g.lineTo(px0 - u * 0.06, py0 + u * 0.06);
      g.quadraticCurveTo(px0 - u * 0.15, py0 + u * 0.04, px0 - u * 0.13, py0 - u * 0.06);
      g.quadraticCurveTo(px0 - u * 0.06, py0 - u * 0.14, px0 - u * 0.07, py0 - u * 0.2);
      g.fill();
      g.fillStyle = '#8a4520';
      g.fillRect(px0 - u * 0.085, py0 - u * 0.23, u * 0.17, u * 0.04);
      g.strokeStyle = '#f2d9a6';
      g.lineWidth = u * 0.02;
      g.beginPath(); g.moveTo(px0 - u * 0.11, py0 - u * 0.04); g.lineTo(px0 + u * 0.11, py0 - u * 0.04); g.stroke();
    } else if (tr === 'bant') {
      const bx = hx + u * 0.12, by = cy - u * 0.24;
      g.fillStyle = '#2f7de0';
      g.beginPath(); g.moveTo(bx, by); g.lineTo(bx - u * 0.15, by - u * 0.1); g.lineTo(bx - u * 0.15, by + u * 0.1); g.closePath(); g.fill();
      g.beginPath(); g.moveTo(bx, by); g.lineTo(bx + u * 0.15, by - u * 0.1); g.lineTo(bx + u * 0.15, by + u * 0.1); g.closePath(); g.fill();
      g.fillStyle = '#1d5bb0';
      g.beginPath(); g.arc(bx, by, u * 0.045, 0, 7); g.fill();
    }
    if (st.sprites.size > 120) st.sprites.clear();
    st.sprites.set(key, c);
    return c;
  }

  /// Підготовка раунду: тварини за доріжками (дальні першими), номери «влучних» у порядку появи.
  function prep(st, p) {
    const key = p.seed + ':' + p.ms + ':' + p.animals.length;
    if (st.prep && st.prepKey === key) return st.prep;
    const m = p.m || 0.08;
    const lanes = [];
    for (let l = 0; l < p.lanes; l++) lanes.push({ animals: [], covers: [] });
    p.animals.forEach((a, i) => {
      const enter = a.t0 + (m / a.v) * 1000;   // мить, коли тварина виходить на видиму частину двору
      const o = Object.assign({ i, enter, hit: 0, ph: ((p.seed >>> 0) % 997) * 0.013 + i * 1.71 }, a);
      (lanes[a.l] || lanes[0]).animals.push(o);
    });
    for (const L of lanes) L.animals.sort((a, b) => a.y - b.y);
    for (const c of p.covers || []) (lanes[c.l] || lanes[0]).covers.push(c);
    st.prep = { lanes, all: lanes.flatMap((L) => L.animals), hitsKey: '' };
    st.prepKey = key;
    return st.prep;
  }

  function markHits(P, hits) {
    const key = (hits || []).join(',');
    if (P.hitsKey === key) return;
    P.hitsKey = key;
    const set = new Set(hits || []);
    const order = P.all.filter((a) => set.has(a.i)).sort((a, b) => a.enter - b.enter || a.i - b.i);
    for (const a of P.all) a.hit = 0;
    order.forEach((a, n) => { a.hit = n + 1; });
    P.hitOrder = order;
  }

  function drawAnimal(g, st, a, x, y, S, t, dpr, alpha) {
    const spr = sprite(st, a.k, a.tr, S, dpr);
    const step = t / 1000 * (3 + a.v * 9) + a.ph;
    const bob = Math.abs(Math.sin(step * Math.PI)) * S * 0.09;
    const rot = Math.sin(step * Math.PI) * 0.08;
    g.globalAlpha = alpha * 0.28;
    g.fillStyle = '#3d2a14';
    g.beginPath(); g.ellipse(x, y + S * 0.42, S * 0.3 * (1 - bob / S), S * 0.07, 0, 0, 7); g.fill();
    // пил з-під ніг
    g.globalAlpha = alpha * 0.25 * (0.5 + 0.5 * Math.sin(step * 2.3));
    g.fillStyle = '#c9a877';
    g.beginPath(); g.arc(x - a.d * S * 0.42, y + S * 0.36, S * 0.07, 0, 7); g.fill();
    g.globalAlpha = alpha;
    g.save();
    g.translate(x, y - bob);
    g.rotate(rot * a.d);
    if (a.d > 0) g.scale(-1, 1);
    g.drawImage(spr, -0.6 * S, -0.85 * S, 1.2 * S, 1.4 * S);
    g.restore();
    g.globalAlpha = 1;
  }

  function drawCover(g, G, c) {
    const y = laneMid(G, c.l);
    const x0 = c.x0 * W, x1 = c.x1 * W;
    if (c.kind === 'viz') cart(g, x0, y - G.laneH * 0.12, x1, y + G.laneH * 0.48);
    else wattle(g, x0, y - G.laneH * 0.02, x1, y + G.laneH * 0.44, null);
  }

  function badge(g, x, y, n, r) {
    g.fillStyle = '#ffd23f';
    g.strokeStyle = '#6b4a00';
    g.lineWidth = 2;
    g.beginPath(); g.arc(x, y, r, 0, 7); g.fill(); g.stroke();
    g.fillStyle = '#2a1e03';
    g.font = '700 ' + Math.round(r * 1.2) + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(n), x, y + 1);
  }

  /// Парад на мить t (мс від початку параду). reveal — підсвітка влучних і їхні номери.
  function drawParade(g, st, p, G, t, dpr, reveal) {
    const P = prep(st, p);
    const m = p.m || 0.08;
    let count = 0;
    for (let l = 0; l < P.lanes.length; l++) {
      const L = P.lanes[l];
      const yl = laneMid(G, l);
      const badges = [];
      for (const a of L.animals) {
        const pos = (t - a.t0) / 1000 * a.v;
        if (pos < 0) continue;
        const x = a.d > 0 ? -m + pos : 1 + m - pos;
        if (x < -m || x > 1 + m) { if (reveal && a.hit && t >= a.enter) count++; continue; }
        const px = x * W, py = yl + a.y * G.laneH * 0.55;
        let alpha = 1;
        if (reveal) {
          if (a.hit) {
            if (t >= a.enter) count++;
            const glow = g.createRadialGradient(px, py, G.S * 0.1, px, py, G.S * 0.7);
            glow.addColorStop(0, 'rgba(255,226,90,.75)');
            glow.addColorStop(1, 'rgba(255,226,90,0)');
            g.fillStyle = glow;
            g.beginPath(); g.arc(px, py, G.S * 0.7, 0, 7); g.fill();
            if (t >= a.enter) badges.push([px, py - G.S * 0.62, a.hit]);
          } else alpha = 0.3;
        }
        drawAnimal(g, st, a, px, py, G.S, t, dpr, alpha);
      }
      for (const c of L.covers) drawCover(g, G, c);
      // номери — поверх тину: інакше схованого за возом не порахуєш
      for (const b of badges) badge(g, b[0], Math.max(b[1], G.back * 0.5), b[2], Math.max(10, G.S * 0.2));
    }
    return count;
  }

  /// Лобі й підсумок: кілька тварин неспішно ходять двором — щоб двір не стояв мертвий.
  function drawIdle(g, st, G, now, dpr) {
    const kinds = ['goose', 'hen', 'duck', 'goat', 'pig', 'cat'];
    const v = 0.07, m = 0.08, span = (1 + 2 * m) / v * 1000;
    for (let i = 0; i < kinds.length; i++) {
      const d = i % 2 ? -1 : 1;
      const tt = (now + i * span / kinds.length) % span;
      const pos = tt / 1000 * v;
      const x = d > 0 ? -m + pos : 1 + m - pos;
      const l = i % G.lanes;
      drawAnimal(g, st, { k: kinds[i], tr: i === 3 ? 'hustka' : i === 0 ? 'hlechyk' : null, d, v, ph: i * 1.3 }, x * W,
        laneMid(G, l) + (i % 3 - 1) * G.laneH * 0.12, G.S * 0.9, now, dpr, 1);
    }
  }

  function bigText(g, text, x, y, size, fill) {
    g.font = '800 ' + size + 'px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineWidth = Math.max(3, size * 0.12);
    g.strokeStyle = 'rgba(30,20,8,.75)';
    g.strokeText(text, x, y);
    g.fillStyle = fill || '#fff';
    g.fillText(text, x, y);
  }

  function draw(st) {
    const cv = st.cv;
    if (!cv) return;
    const v = view(st);
    const g = cv.ctx;
    const H = cv.h;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const ph = v.ph || 'lobby';
    const p = v.parade;
    const lanes = p ? p.lanes : 2;
    const G = geom(H, lanes);
    const now = performance.now();
    const t = phaseT(st);
    g.drawImage(background(st, H, lanes, dpr), 0, 0, W, H);

    if (ph === 'parade' && p) {
      drawParade(g, st, p, G, Math.min(t, p.ms), dpr, false);
    } else if (ph === 'reveal' && p && v.reveal) {
      markHits(prep(st, p), v.reveal.hits);
      const rt = Math.min(t * (p.rx || 2.5), p.ms);
      const n = drawParade(g, st, p, G, rt, dpr, true);
      if (n !== st.lastCount) { st.lastCount = n; st.pulse = now; }
      const done = rt >= p.ms;
      const sc = 1 + Math.max(0, 1 - (now - st.pulse) / 220) * 0.35;
      const label = revealIcon(v) + ' ' + n;
      const fs = Math.round(40 * sc);
      g.fillStyle = 'rgba(255,255,255,.85)';
      roundRect(g, W - 190, 10, 178, 60, 14);
      g.fill();
      bigText(g, label, W - 101, 41, fs, '#ffd23f');
      if (done) {
        g.fillStyle = 'rgba(20,14,4,.45)';
        g.fillRect(0, H * 0.36, W, H * 0.28);
        bigText(g, 'Правильно: ' + answerText(v), W / 2, H * 0.5, Math.round(H * 0.12), '#ffe680');
      }
      st.revealDone = done;
    } else if (ph === 'lobby' || ph === 'done') {
      drawIdle(g, st, G, now, dpr);
    } else if (ph === 'ready') {
      const left = Math.max(0, (v.dur || 3000) - t);
      const n = Math.ceil(left / 1000);
      const fr = 1 - (left / 1000 - Math.floor(left / 1000));
      if (n > 0) bigText(g, String(n), W / 2, H * 0.52, Math.round(H * (0.32 + 0.1 * (1 - fr))), '#ffe680');
      bigText(g, v.pre ? 'Рахуй, що спитали' : 'Дивись уважно — питання буде потім', W / 2, H * 0.84, Math.round(H * 0.058));
    } else if (ph === 'answer') {
      g.fillStyle = 'rgba(20,14,4,.28)';
      g.fillRect(0, 0, W, H);
      bigText(g, '?', W / 2, H * 0.52, Math.round(H * 0.42), '#ffe680');
    }
    // час фази — тонка смужка вгорі
    if (ph !== 'lobby' && ph !== 'done' && v.dur) {
      const fr = Math.max(0, Math.min(1, 1 - t / v.dur));
      g.fillStyle = 'rgba(0,0,0,.25)';
      g.fillRect(0, 0, W, 6);
      g.fillStyle = ph === 'answer' && fr < 0.3 ? '#ff6b5a' : '#ffd23f';
      g.fillRect(0, 0, W * fr, 6);
    }
    st.lastDraw = now;
  }

  function roundRect(g, x, y, w, h, r) {
    g.beginPath();
    g.moveTo(x + r, y);
    g.arcTo(x + w, y, x + w, y + h, r);
    g.arcTo(x + w, y + h, x, y + h, r);
    g.arcTo(x, y + h, x, y, r);
    g.arcTo(x, y, x + w, y, r);
    g.closePath();
  }

  function revealIcon(v) {
    const q = v.q || {};
    if (q.type === 'dir') return '⬅';
    if (q.type === 'most' && v.reveal && v.reveal.kind) return emo(v.reveal.kind);
    return (q.kinds || []).map(emo).join('');
  }

  function answerText(v) {
    const r = v.reveal || {};
    const q = v.q || {};
    if (q.opts && r.kind) return emo(r.kind) + ' ' + ((KINDS[r.kind] || {}).one || '');
    return String(r.answer);
  }

  // ---------- цикл ----------

  /// Парад і повтор — 60 к/с; лобі й підсумок — 20 к/с (там лише прогулянка); прихована картка — не малюємо.
  function spin(st) {
    if (st.raf) return;
    const loop = () => {
      st.raf = 0;
      if (!st.cv || !st.cv.el.isConnected) return;
      st.raf = requestAnimationFrame(loop);
      if (document.hidden) return;
      const ph = view(st).ph || 'lobby';
      if (ph === 'answer') paintTimer(st);
      if (!st.cv.el.offsetParent) return;
      const busy = ph === 'parade' || ph === 'reveal' || ph === 'ready';
      const now = performance.now();
      if (!busy && now - st.lastDraw < (ph === 'answer' ? 100 : 50)) return;
      draw(st);
      if (ph === 'reveal' && st.revealDone !== st.revealShown) paintReveal(st);
    };
    st.raf = requestAnimationFrame(loop);
  }

  // ---------- DOM ----------

  function setHtml(st, el, html) {
    if (!el) return;
    if (st.htmlCache.get(el) === html) return;
    st.htmlCache.set(el, html);
    el.innerHTML = html;
  }

  const nameOf = (ctx, i) => ctx.nameOf(i) || ctx.seatName(i);
  const dot = (i) => '<i class="gsdot" style="background:' + SEAT[i % 8] + '"></i>';

  function qHtml(ctx, v) {
    const ph = v.ph || 'lobby';
    if (ph === 'lobby') return '';
    if (ph === 'done') return '';
    const q = v.q;
    if (!q) return '<div class="gsq gsnote">👀 ' + ctx.esc(v.note || 'Дивись уважно — питання буде потім') + '</div>';
    const icons = q.type === 'dir' ? '⬅' : q.type === 'most' ? '🏆' : (q.kinds || []).map(emo).join('');
    return '<div class="gsq"><span class="gsqi">' + icons + '</span><span>' + ctx.esc(q.text) + '</span></div>';
  }

  function headHtml(ctx, v) {
    const ph = v.ph || 'lobby';
    const d = v.daily;
    if (ph === 'lobby') {
      return '<div class="gsrules">🪿 Через двір біжить парад живності — рахуй, бо потім Дядько Глек спитає «скільки?». '
        + 'Точно — <b>3</b> очки, мимо на одну — <b>1</b>, найшвидшому з точних <b>+1</b>. Перші три паради — питання наперед, далі — на пам\'ять.</div>';
    }
    const no = v.round ? (d ? '☀ Гуси дня №' + d.no + ' · ' : '') + 'Парад ' + v.round + ' з ' + v.rounds : '';
    const lvl = v.round ? ' · ' + '🔥'.repeat(Math.min(3, 1 + Math.floor((v.level || 0) / 2))) : '';
    return ph === 'done' ? '' : '<div class="gsno muted small">' + no + lvl + '</div>';
  }

  function playersHtml(ctx, v) {
    const ph = v.ph || 'lobby';
    const pl = v.players || [];
    // у лобі гравців показує шапка картки, на підсумку — таблиця
    if (ph === 'lobby' || ph === 'done' || !pl.length || (pl.length < 2 && !v.daily)) return '';
    if (v.daily) return '';
    const answered = new Set(ph === 'answer' ? (answeredOf(ctx, v)) : []);
    const rows = v.reveal && ph === 'reveal' ? new Map(v.reveal.rows.map((r) => [r.s, r])) : null;
    const st = ctx && ctx._gsst;
    const showPts = rows && st && st.revealDone;
    return pl.map((s) => {
      const me = s === ctx.seat && ctx.mine;
      let tail = '';
      if (ph === 'answer') tail = answered.has(s) ? '<span class="gsok">✓</span>' : '<span class="gsdots">…</span>';
      else if (rows) {
        const r = rows.get(s);
        const a = r && r.a != null ? (v.q && v.q.opts ? emo((v.q.opts[r.a] || {}).k) : r.a) : '—';
        tail = '<span class="gsa">' + a + '</span>' + (showPts && r ? '<span class="gspts p' + Math.min(4, r.pts) + '">+' + r.pts + (r.fast ? '⚡' : '') + '</span>' : '');
      }
      return '<span class="gsp' + (me ? ' me' : '') + '">' + dot(s) + '<span class="gsn">' + ctx.esc(nameOf(ctx, s)) + '</span>'
        + '<b>' + (v.scores ? v.scores[s] || 0 : 0) + '</b>' + tail + '</span>';
    }).join('');
  }

  function answeredOf(ctx, v) {
    const f = ctx.frame;
    if (f && f.ph === 'answer' && f.round === v.round && Array.isArray(f.answered) && f.answered.length >= (v.answered || []).length) return f.answered;
    return v.answered || [];
  }

  /// Панель відповіді: число (табло, −/+, цифри) або вибір кнопками.
  function ansHtml(ctx, v, st) {
    if (v.ph !== 'answer' || !ctx.mine || !ctx.playing || !(v.players || []).includes(ctx.seat)) return '';
    const q = v.q || {};
    const timer = '<div class="gstimer muted small"><i class="gstbar"><b></b></i><span></span></div>';
    if (v.mine != null) {
      const a = q.opts ? emo((q.opts[v.mine] || {}).k) + ' ' + ctx.esc((q.opts[v.mine] || {}).name || '') : String(v.mine);
      return '<div class="gsdone1">✓ Твоя відповідь: <b>' + a + '</b><span class="muted small"> — чекаємо решту</span></div>';
    }
    if (q.opts) {
      return timer + '<div class="gsopts n' + q.opts.length + '">' + q.opts.map((o, i) => '<button type="button" class="gsopt' + (st.sel === i ? ' on' : '')
        + '" data-opt="' + i + '"><span class="gse">' + emo(o.k) + '</span><span>' + ctx.esc(o.name) + '</span><kbd>' + (i + 1) + '</kbd></button>').join('')
        + '</div><button type="button" class="primary gsgo" data-go' + (st.sel < 0 ? ' disabled' : '') + '>Відповісти ✓</button>';
    }
    const val = st.val == null ? '?' : String(st.val);
    const keys = [1, 2, 3, 4, 5, 6, 7, 8, 9].map((n) => '<button type="button" data-dig="' + n + '">' + n + '</button>').join('')
      + '<button type="button" data-back aria-label="Стерти">⌫</button><button type="button" data-dig="0">0</button>'
      + '<button type="button" class="primary" data-go' + (st.val == null ? ' disabled' : '') + ' aria-label="Відповісти">✓</button>';
    return timer + '<div class="gsnum"><button type="button" data-d="-1" aria-label="Мінус один">−</button><output class="gsval' + (st.val == null ? ' empty' : '')
      + '">' + val + '</output><button type="button" data-d="1" aria-label="Плюс один">+</button></div>'
      + '<div class="gskeys">' + keys + '</div>';
  }

  function revealHtml(ctx, v, st) {
    if (v.ph !== 'reveal' || !v.reveal) return '';
    if (!st.revealDone) return '<div class="gsrevh muted">Повтор ×' + String((v.parade || {}).rx || 2.5).replace('.', ',') + ' — рахуємо разом…</div>';
    const r = v.reveal;
    const mine = ctx.mine ? r.rows.find((x) => x.s === ctx.seat) : null;
    let msg = '';
    if (mine) {
      const off = mine.a == null ? null : (v.q && v.q.opts ? (mine.a === r.answer ? 0 : 9) : Math.abs(mine.a - r.answer));
      msg = mine.a == null ? '🙈 Ти не відповів' : off === 0 ? '🎯 Точно! +' + mine.pts + (mine.fast ? ' — і найшвидше ⚡' : '')
        : off === 1 ? '🟨 Мимо на одну: +1' : v.q && v.q.opts ? '⬛ Не той' : '⬛ Мимо на ' + off;
    }
    return '<div class="gsrevh"><span>Правильно: <b>' + answerText(v) + '</b></span>' + (msg ? '<span class="gsmsg">' + msg + '</span>' : '') + '</div>';
  }

  function doneHtml(ctx, v) {
    if (v.ph !== 'done') return '';
    const pl = (v.players || []).slice().sort((a, b) => (v.scores[b] || 0) - (v.scores[a] || 0) || a - b);
    const win = new Set(v.winners || []);
    const medal = ['🥇', '🥈', '🥉'];
    let place = 0, prev = null;
    const rows = v.daily ? '' : pl.map((s, i) => {
      if (v.scores[s] !== prev) { place = i; prev = v.scores[s]; }
      return '<div class="gsrow' + (s === ctx.seat && ctx.mine ? ' me' : '') + (win.has(s) ? ' win' : '') + '"><span>' + (medal[place] || place + 1 + '.') + '</span>'
        + dot(s) + '<span class="gsn">' + ctx.esc(nameOf(ctx, s)) + '</span><b>' + (v.scores[s] || 0) + '</b></div>';
    }).join('');
    const title = v.daily ? '☀ Гуси дня №' + v.daily.no + ': ' + (v.scores[0] || 0) + ' з ' + v.daily.max
      : win.size ? '🏆 Головний пастух: ' + [...win].map((s) => ctx.esc(nameOf(ctx, s))).join(', ') : 'Нічия — худоба перерахувала вас';
    const recap = (v.recap || []).map((r) => '<li><span>' + ctx.esc(r.q) + '</span><span class="muted">правильно ' + ctx.esc(r.answer)
      + (r.mine != null ? ', ти — ' + ctx.esc(r.mine) : ', без відповіді') + '</span><b class="p' + Math.min(4, r.pts) + '">+' + r.pts + '</b></li>').join('');
    return '<div class="gstitle">' + title + '</div>' + (rows ? '<div class="gstable">' + rows + '</div>' : '')
      + (recap ? '<details class="gsrecap"' + (v.daily ? ' open' : '') + '><summary>Як рахував ти</summary><ol>' + recap + '</ol></details>' : '');
  }

  function dailyHtml(ctx, v) {
    const d = v.daily;
    if (!d || v.ph !== 'done' || !d.board) return '';
    const rows = d.board.map((r, i) => '<div class="gsrow' + (d.place === i + 1 ? ' me' : '') + '"><span>' + (i + 1) + '.</span><span class="gsn">'
      + ctx.esc(r.nick) + '</span><span class="gsmarks">' + ctx.esc(r.marks || '') + '</span><b>' + r.points + '</b></div>').join('');
    return '<div class="gsno muted small">Таблиця дня' + (d.place ? ' · ти ' + d.place + '-й з ' + d.players : '') + '</div>'
      + '<div class="gstable">' + rows + '</div>'
      + (d.share ? '<button type="button" class="ghost" data-share>📋 Скопіювати результат для Балачок</button>' : '')
      + '<div class="muted small">Нові паради — завтра опівночі</div>';
  }

  function paintReveal(st) {
    const ctx = st.ctx;
    const v = view(st);
    st.revealShown = st.revealDone;
    setHtml(st, st.el.rev, revealHtml(ctx, v, st));
    setHtml(st, st.el.ppl, playersHtml(ctx, v));
  }

  function paintTimer(st) {
    const box = st.root.querySelector('.gstimer');
    if (!box) return;
    const v = view(st);
    const left = Math.max(0, (v.dur || 0) - phaseT(st));
    const s = Math.ceil(left / 1000);
    const coarse = st.ctx.ui.coarse();
    const how = v.q && v.q.opts
      ? (coarse ? 'тисни варіант і «Відповісти»' : 'цифри 1–' + v.q.opts.length + ' чи ←→, Enter')
      : (coarse ? 'тисни цифри й ✓' : 'цифри, ↑↓ ±1, ←→ ±5, Enter');
    const txt = 'Лишилось ' + s + ' с · ' + how;
    const el = box.querySelector('span');
    if (el.textContent !== txt) el.textContent = txt;
    const bar = box.querySelector('b');
    bar.style.width = (v.dur ? left / v.dur * 100 : 0).toFixed(1) + '%';
    bar.classList.toggle('low', left < 3000);
  }

  function paint(root, st) {
    const ctx = st.ctx;
    const v = ctx.view || {};
    ctx._gsst = st;
    if (st.round !== v.round) { st.round = v.round; st.val = null; st.sel = -1; st.revealDone = false; st.revealShown = undefined; st.lastCount = 0; }
    if (v.ph !== 'reveal') { st.revealDone = false; st.revealShown = undefined; }
    const e = st.el;
    setHtml(st, e.head, headHtml(ctx, v));
    setHtml(st, e.q, qHtml(ctx, v));
    setHtml(st, e.ppl, playersHtml(ctx, v));
    setHtml(st, e.ans, ansHtml(ctx, v, st));
    setHtml(st, e.rev, revealHtml(ctx, v, st));
    setHtml(st, e.done, doneHtml(ctx, v));
    setHtml(st, e.daily, dailyHtml(ctx, v));
    st.revealShown = st.revealDone;
    // Відповідаєш — двір із «?» лише з'їдав би місце під цифри (на телефоні табло падало за край екрана).
    root.classList.toggle('gsanswering', !!e.ans.querySelector('.gskeys, .gsopts'));
    if (v.ph === 'answer') paintTimer(st);
  }

  // ---------- ввід ----------

  function bump(st, d) {
    const v = view(st);
    if (v.ph !== 'answer' || v.mine != null) return;
    const q = v.q || {};
    if (q.opts) {
      const n = q.opts.length;
      st.sel = st.sel < 0 ? (d > 0 ? 0 : n - 1) : (st.sel + (d > 0 ? 1 : -1) + n) % n;
    } else {
      const max = q.max || 60;
      st.val = Math.max(0, Math.min(max, (st.val == null ? 0 : st.val) + d));
    }
    st.fresh = false;
    paint(st.root, st);
  }

  function digit(st, n) {
    const v = view(st);
    if (v.ph !== 'answer' || v.mine != null) return;
    const q = v.q || {};
    if (q.opts) { if (n >= 1 && n <= q.opts.length) { st.sel = n - 1; paint(st.root, st); } return; }
    const max = q.max || 60;
    const next = st.val == null || st.val === 0 ? n : st.val * 10 + n;
    st.val = next > max ? n : next;
    paint(st.root, st);
  }

  function back(st) {
    if (st.val == null) return;
    st.val = st.val >= 10 ? Math.floor(st.val / 10) : null;
    paint(st.root, st);
  }

  function submit(st) {
    const v = view(st);
    const ctx = st.ctx;
    if (v.ph !== 'answer' || v.mine != null || st.sending || !ctx.mine) return;
    const q = v.q || {};
    let p;
    if (q.opts) { if (st.sel < 0) { ctx.toast('Обери одного з варіантів'); return; } p = { c: st.sel }; }
    else { if (st.val == null) { ctx.toast('Набери число'); return; } p = { n: st.val }; }
    st.sending = true;
    Promise.resolve(ctx.act('answer', p)).then(() => { st.sending = false; }, () => { st.sending = false; });
  }

  function onClick(st, e) {
    const b = e.target.closest('button');
    if (!b || !st.root.contains(b) || b.disabled) return;
    if (b.dataset.dig != null) digit(st, +b.dataset.dig);
    else if (b.dataset.d != null) bump(st, +b.dataset.d);
    else if (b.hasAttribute('data-back')) back(st);
    else if (b.dataset.opt != null) {
      const i = +b.dataset.opt;
      if (st.sel === i) submit(st);   // другий дотик по тій самій — відповідь
      else { st.sel = i; paint(st.root, st); }
    } else if (b.hasAttribute('data-go')) submit(st);
    else if (b.hasAttribute('data-share')) {
      const text = ((view(st)).daily || {}).share || '';
      const done = () => st.ctx.toast('Скопійовано — встав у Балачки');
      if (navigator.clipboard) navigator.clipboard.writeText(text).then(done, () => st.ctx.toast(text));
      else st.ctx.toast(text);
    }
  }

  function canType(st) {
    const v = view(st);
    const ctx = st.ctx;
    return ctx && ctx.mine && ctx.playing && v.ph === 'answer' && v.mine == null && (v.players || []).includes(ctx.seat);
  }

  function key(st, e) {
    if (!canType(st)) return false;
    const t = e.target;
    if (t && /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName)) return false;
    if (e.ctrlKey || e.metaKey || e.altKey) return false;
    // Пад і затиснута стрілка сиплють повтори — не частіше ніж раз на 110 мс, інакше число злітає до 60.
    const now = performance.now();
    if (e.repeat && now - st.lastKeyAt < 110) return true;
    const k = e.key;
    let used = true;
    if (/^[0-9]$/.test(k)) digit(st, +k);
    else if (k === 'ArrowUp' || k === '+' || k === '=') bump(st, 1);
    else if (k === 'ArrowDown' || k === '-') bump(st, -1);
    else if (k === 'ArrowRight') bump(st, (view(st).q || {}).opts ? 1 : 5);
    else if (k === 'ArrowLeft') bump(st, (view(st).q || {}).opts ? -1 : -5);
    else if (k === 'Backspace') back(st);
    else if (k === 'Enter' || k === ' ') { if (!e.repeat) submit(st); }
    else used = false;
    if (used) st.lastKeyAt = now;
    return used;
  }

  // ---------- модуль ----------

  function build(root, st) {
    if (st.el && st.el.wrap.isConnected) return;
    root.innerHTML = '<div class="gsw"><div class="gshead"></div><div class="gsqbox"></div><div class="gsyard"></div>'
      + '<div class="gsppl"></div><div class="gsrev"></div><div class="gsans"></div><div class="gsdone"></div><div class="gsdaily"></div></div>';
    const q = (s) => root.querySelector(s);
    st.el = { wrap: q('.gsw'), head: q('.gshead'), q: q('.gsqbox'), yard: q('.gsyard'), ppl: q('.gsppl'), rev: q('.gsrev'), ans: q('.gsans'), done: q('.gsdone'), daily: q('.gsdaily') };
    st.htmlCache = new Map();
    st.el.wrap.addEventListener('click', (e) => onClick(st, e));
  }

  function canvasFit(root, st) {
    const H = fitH(root);
    if (!st.cv || st.cv.h !== H) {
      st.cv = HGames.ui.canvas(st.el.yard, { w: W, h: H, cls: 'gsboard' });
      st.bg = null;
    } else st.cv.resize();
  }

  function takeView(st) {
    const v = view(st);
    if (v.ph) sync(st, v.ph, v.round, v.el);
  }

  const MOD = {
    id: 'geese',
    added: '2026-10-06',
    icon: ICON,
    seatClass: ['gs0', 'gs1', 'gs2', 'gs3', 'gs4', 'gs5', 'gs6', 'gs7'],
    pad: {
      dirs: true,
      a: 'Enter',
      hint: '{dpad} ↑↓ ±1, ←→ ±5 (чи вибір) · {a} відповісти',
      when: (ctx) => {
        const st = ctx._gsst;
        return !!(st && canType(st));
      },
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      build(root, st);
      canvasFit(root, st);
      takeView(st);
      if (typeof ResizeObserver === 'function') {
        st.ro = new ResizeObserver(() => { if (root._geese) { canvasFit(root, st); draw(st); } });
        st.ro.observe(root);
      }
      paint(root, st);
      draw(st);
      spin(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      build(root, st);
      canvasFit(root, st);
      takeView(st);
      paint(root, st);
      spin(st);
    },

    /// Кадр — лише годинник фази й хто вже відповів; фазу міняє вид (сервер шле його на кожну зміну).
    frame(root, ctx, f) {
      const st = root._geese;
      if (!st || !f) return;
      st.ctx = ctx;
      const v = ctx.view || {};
      if (f.ph === v.ph && f.round === v.round) {
        sync(st, f.ph, f.round, f.el);
        if (f.ph === 'answer') setHtml(st, st.el.ppl, playersHtml(ctx, v));
      }
      spin(st);
    },

    onKey(e, ctx) {
      const st = ctx._gsst;
      if (!st || !st.root || !st.root.isConnected) return false;
      return key(st, e);
    },

    status(ctx) {
      const v = ctx.view || {};
      const ph = v.ph || 'lobby';
      if (ph === 'lobby') {
        const host = ctx.room && ctx.me && String(ctx.room.host || '').toLowerCase() === String(ctx.me.nick || '').toLowerCase();
        return host ? 'Тисни «Почати» — можна й самому, а хто встигне, підсяде' : 'Чекаємо, поки господар почне парад';
      }
      if (ph === 'ready') return v.pre && v.q ? 'Готуйсь: ' + v.q.text : 'Готуйсь — питання буде після параду';
      if (ph === 'parade') return v.q ? 'Рахуй! ' + v.q.text : 'Дивись уважно — рахуй усіх';
      if (ph === 'answer') {
        if (!ctx.mine || !(v.players || []).includes(ctx.seat)) return 'Гравці відповідають…';
        if (v.mine != null) return 'Відповідь є — чекаємо решту';
        return v.q && v.q.opts ? 'Обери й тисни «Відповісти»' : ctx.ui.coarse() ? 'Набери число й тисни ✓' : 'Цифри чи ↑↓, тоді Enter';
      }
      if (ph === 'reveal') return 'Повтор: рахуємо разом';
      if (ph === 'done') {
        const w = v.winners || [];
        if (v.daily) return 'Гуси дня: ' + (v.scores ? v.scores[0] : 0) + ' з ' + v.daily.max;
        return w.length ? 'Пастух: ' + w.map((s) => nameOf(ctx, s)).join(', ') : 'Нічия';
      }
      return '';
    },

    unmount(root) {
      const st = root._geese;
      if (!st) return;
      if (st.raf) cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.ro) st.ro.disconnect();
      root.classList.remove('gsanswering');
      st.sprites.clear();
      st.bg = null;
      st.cv = null;
      if (st.ctx && st.ctx._gsst === st) st.ctx._gsst = null;
      root._geese = null;
    },
  };

  HGames.register(MOD);
  // «Гуси дня» — той самий модуль (сервер: Client = "geese").
  HGames.register(Object.assign({}, MOD, {
    id: 'geese-daily',
    icon: '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="3.2" fill="var(--accent)"/>'
      + '<path d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4" stroke="var(--text)" stroke-width="1.5" stroke-linecap="round"/></svg>',
  }));
})();
