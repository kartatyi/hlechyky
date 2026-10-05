/*
  Склей глек — збери розбиту картинку з черепків (Impl/Sklei.cs, SkleiCut.cs; spec docs/games/specs/sklei.md).

  Вид: { ph: lobby|ready|go|pause|over, level, n, rotate, hint, party, pics, picNo, pic: {key, kind, url, title, by},
    cut: {n, v, eh, ev, tray}, until, leftMs, goAt, me: {placed, done, bad} | null, phone,
    players: [{seat, name, bot, placed, of, done, place, pts, total, gone}], history: [{pic, rows}], winners, … }.
  Кадр: { ph, picNo, leftMs, p: [[seat, placed, of, doneMs | −1], …] } — лише прогрес усіх.
  Дії: put {k, c, r, t} — лише правильне (сервер перевіряє кут за кількістю тапів), dev {phone}.

  Усе поле — один канвас у «світових» одиницях cu (клітинка рамки = 100 cu): ліворуч (на телефоні — згори) рамка
  N×N, поруч стіл-рушник із черепками. Черепок — свій знімок (картинка, обрізана його контуром, з обідком) і тінь,
  тож кадр — лише кілька drawImage; рамку з прирослими черепками кешуємо шаром і перемальовуємо, коли щось приросло.
  Канвас малюється, лише поки щось рухається (rAF гасне сам).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M3.2 3h9.6l-.7 2.2c1.3 1.2 2 2.8 2 4.4C14.1 12.8 11.4 15 8 15S1.9 12.8 1.9 9.6c0-1.6.7-3.2 2-4.4Z" fill="var(--clay)"/>'
    + '<path d="M8 15 7 11.4l2-2.2-1.6-2.6L8.6 3" stroke="var(--accent)" stroke-width="1.2" fill="none" stroke-linejoin="round"/>'
    + '<path d="M4.4 9.6 7 11.4M9 9.2l3.4.6" stroke="var(--accent)" stroke-width="1" fill="none"/></svg>';

  const CELL = 100;          // клітинка рамки в cu
  const BOX = 172;           // сторона знімка черепка в cu: клітинка + вузли ±16 + вигини ±13 + обідок
  const GAP = 26;            // між рамкою і столом
  const PAD = 10;            // поле довкола світу, щоб черепок біля краю не різало
  const INSET = 34;          // центри черепків на рушнику — не ближче за стільки cu до його краю (вигини не вилазять)
  const MAG = 35, MAG_PAD = 48;   // магніт: центр ближче за стільки cu до свого місця (пад — щедріше)
  const SHATTER_MS = 700;
  const KINDS = { svg: '🏺', geo: '📷', art: '🖍', own: '🖼' };
  const PALETTE = [         // Піктіонарі: та сама палітра, що в pictionary.js (малюнки альбому)
    '#ffffff', '#000000', '#7f7f7f', '#c3c3c3', '#5d4037', '#8d5a2b', '#e53935', '#fb8c00', '#fdd835', '#fff59d',
    '#43a047', '#a5d6a7', '#00897b', '#1e88e5', '#90caf9', '#3949ab', '#8e24aa', '#f48fb1', '#e8b48a', '#ff7043',
  ];
  const PH_MAX = 300 * 1000, PH_OUT = 768, PH_ZOOM = 5;

  const live = new Set();
  const stOf = (ctx) => [...live].find((s) => s.ctx === ctx) || null;
  const held = {};           // стрілки, які зараз тримають (клавіатура чи стік пада)
  document.addEventListener('keyup', (e) => {
    if (held[e.code]) { held[e.code] = false; }
  });
  window.addEventListener('blur', () => { for (const k in held) held[k] = false; });

  const coarse = () => !!(HGames.ui.coarse && HGames.ui.coarse());
  const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
  const fmt = (ms) => {
    const s = Math.max(0, Math.floor(ms / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const seatColor = (i) => HGames.ui.css('--skl-s' + (i % 8), ['#5aa9ff', '#d9825b', '#7bd389', '#f4c542', '#b48cf2', '#6fd6c2', '#f08cb8', '#b7c2bd'][i % 8]);

  // =========================================================================================
  // Геометрія черепків (spec §3)
  // =========================================================================================

  /// Детермінований «шум» 0..1 — дрібні зубці тріщини однакові в обох сусідів шва.
  function hash(a, b) {
    let h = Math.imul(a, 374761393) + Math.imul(b, 668265263) | 0;
    h = Math.imul(h ^ (h >>> 13), 1274126177);
    return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
  }

  function node(cut, i, j) {
    const o = (j * (cut.n + 1) + i) * 2;
    return [i * CELL + (cut.v[o] || 0), j * CELL + (cut.v[o + 1] || 0)];
  }

  /// Шов від вузла до вузла (рівно: ламана через дві точки вигину з cut, між ними — дрібний зубець). vert — вертикальний
  /// (i, j) → (i, j+1), інакше горизонтальний (i, j) → (i+1, j). Зовнішній край — пряма.
  function seam(cut, vert, i, j) {
    const N = cut.n;
    const a = node(cut, i, j), b = vert ? node(cut, i, j + 1) : node(cut, i + 1, j);
    const pts = [a[0], a[1]];
    if (vert ? i <= 0 || i >= N : j <= 0 || j >= N) { pts.push(b[0], b[1]); return pts; }
    const arr = vert ? cut.ev : cut.eh;
    const o = vert ? ((i - 1) * N + j) * 2 : ((j - 1) * N + i) * 2;
    const kp = [a];
    for (let q = 0; q < 2; q++) {
      const t = (q + 1) / 3, d = arr[o + q] || 0;
      const x = a[0] + (b[0] - a[0]) * t, y = a[1] + (b[1] - a[1]) * t;
      kp.push(vert ? [x + d, y] : [x, y + d]);
    }
    kp.push(b);
    const id = (vert ? 7000 : 3000) + j * 41 + i;
    for (let q = 0; q < 3; q++) {
      const p0 = kp[q], p1 = kp[q + 1];
      const dx = p1[0] - p0[0], dy = p1[1] - p0[1], len = Math.hypot(dx, dy) || 1;
      for (let z = 1; z <= 2; z++) {        // два зубці на відрізок: «як тріснуло», а не «як вирізали»
        const t = z / 3, off = (hash(id, q * 3 + z) - 0.5) * 8;
        pts.push(p0[0] + dx * t - dy / len * off, p0[1] + dy * t + dx / len * off);
      }
      pts.push(p1[0], p1[1]);
    }
    return pts;
  }

  function reversed(p) {
    const out = [];
    for (let i = p.length - 2; i >= 0; i -= 2) out.push(p[i], p[i + 1]);
    return out;
  }

  /// Контур черепка k у світі (cu): верх → право → низ (назад) → ліво (назад).
  function outline(cut, k) {
    const N = cut.n, r = Math.floor(k / N), c = k % N;
    const parts = [seam(cut, false, c, r), seam(cut, true, c + 1, r), reversed(seam(cut, false, c, r + 1)), reversed(seam(cut, true, c, r))];
    const pts = [];
    for (const p of parts) for (let i = pts.length ? 2 : 0; i < p.length; i++) pts.push(p[i]);
    pts.length -= 2;   // останній вузол = перший
    return pts;
  }

  function pathOf(pts, dx, dy) {
    const p = new Path2D();
    p.moveTo(pts[0] - dx, pts[1] - dy);
    for (let i = 2; i < pts.length; i += 2) p.lineTo(pts[i] - dx, pts[i + 1] - dy);
    p.closePath();
    return p;
  }

  // =========================================================================================
  // Картинка: svg / фото / своя — <img>; малюнок друга — штрихи Піктіонарі на канвас
  // =========================================================================================

  function loadImg(url) {
    return new Promise((done, fail) => {
      const img = new Image();
      img.decoding = 'async';
      img.onload = () => (img.naturalWidth ? done(img) : fail(new Error('порожня')));
      img.onerror = () => fail(new Error('не завантажилась'));
      img.src = url;
    });
  }

  function unpack(z) {     // SketchWire, як у pictionary.js
    if (!z) return [];
    let b;
    try {
      const bin = atob(z);
      b = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) b[i] = bin.charCodeAt(i);
    } catch { return []; }
    let at = 0;
    const v = () => {
      let x = 0, sh = 0, c;
      do { c = b[at++] | 0; x += (c & 127) * 2 ** sh; sh += 7; } while (c >= 128 && at < b.length);
      return x;
    };
    const zz = (u) => (u % 2 ? -(u + 1) / 2 : u / 2);
    const ops = [];
    while (at < b.length) {
      const kind = b[at++], stroke = v(), color = b[at++] | 0, width = b[at++] | 0, n = v();
      let x = v(), y = v();
      const op = [kind, stroke, color, width, x, y];
      for (let k = 1; k < n; k++) { x += zz(v()); y += zz(v()); op.push(x, y); }
      ops.push(op);
    }
    return ops;
  }

  function flood(c, W, H, x0, y0, color) {
    x0 = clamp(x0 | 0, 0, W - 1);
    y0 = clamp(y0 | 0, 0, H - 1);
    const img = c.getImageData(0, 0, W, H), d = img.data;
    const at = (y0 * W + x0) * 4, tr = d[at], tg = d[at + 1], tb = d[at + 2];
    const n = parseInt(color.slice(1), 16), fr = (n >> 16) & 255, fg = (n >> 8) & 255, fb = n & 255;
    if (Math.abs(tr - fr) + Math.abs(tg - fg) + Math.abs(tb - fb) < 12) return;
    const same = (i) => Math.abs(d[i] - tr) + Math.abs(d[i + 1] - tg) + Math.abs(d[i + 2] - tb) <= 90;
    const seen = new Uint8Array(W * H), stack = [x0, y0];
    while (stack.length) {
      const y = stack.pop(), x = stack.pop();
      let l = x;
      while (l >= 0 && !seen[y * W + l] && same((y * W + l) * 4)) l--;
      l++;
      let r = x;
      while (r < W && !seen[y * W + r] && same((y * W + r) * 4)) r++;
      for (let i = l; i < r; i++) {
        const p = y * W + i;
        seen[p] = 1;
        d[p * 4] = fr; d[p * 4 + 1] = fg; d[p * 4 + 2] = fb; d[p * 4 + 3] = 255;
        if (y > 0 && !seen[p - W] && same((p - W) * 4)) stack.push(i, y - 1);
        if (y < H - 1 && !seen[p + W] && same((p + W) * 4)) stack.push(i, y + 1);
      }
    }
    c.putImageData(img, 0, 0);
  }

  /// Малюнок альбому (1000×750) → квадрат довкола того, що намальовано: так черепки — малюнок, а не біле поле.
  function drawArt(z) {
    const W = 1000, H = 750, ops = unpack(z);
    const big = document.createElement('canvas');
    big.width = W; big.height = H;
    const g = big.getContext('2d', { willReadFrequently: true });
    g.fillStyle = '#ffffff';
    g.fillRect(0, 0, W, H);
    let x0 = W, y0 = H, x1 = 0, y1 = 0;
    for (const op of ops) {
      if (!op || op.length < 6) continue;
      const color = PALETTE[op[2]] || '#000';
      if (op[0] === 1) { flood(g, W, H, op[4], op[5], color); continue; }
      g.strokeStyle = g.fillStyle = color;
      g.lineWidth = op[3];
      g.lineCap = g.lineJoin = 'round';
      g.beginPath();
      g.moveTo(op[4], op[5]);
      if (op.length === 6) { g.arc(op[4], op[5], op[3] / 2, 0, Math.PI * 2); g.fill(); }
      else { for (let i = 6; i + 1 < op.length; i += 2) g.lineTo(op[i], op[i + 1]); g.stroke(); }
      for (let i = 4; i + 1 < op.length; i += 2) {
        x0 = Math.min(x0, op[i]); x1 = Math.max(x1, op[i]); y0 = Math.min(y0, op[i + 1]); y1 = Math.max(y1, op[i + 1]);
      }
    }
    if (x1 <= x0) { x0 = 0; x1 = W; y0 = 0; y1 = H; }
    const side = clamp(Math.max(x1 - x0, y1 - y0) + 80, 420, W);
    const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
    const out = document.createElement('canvas');
    out.width = out.height = 900;
    const o = out.getContext('2d');
    o.fillStyle = '#ffffff';
    o.fillRect(0, 0, 900, 900);
    const k = 900 / side;
    o.drawImage(big, (side / 2 - cx) * k, (side / 2 - cy) * k, W * k, H * k);
    return out;
  }

  /// Запасна картинка, коли справжня не прийшла: яскраві смуги з номерами — скласти можна, хоч і не так гарно.
  function fallbackPic() {
    const c = document.createElement('canvas');
    c.width = c.height = 600;
    const g = c.getContext('2d');
    const cols = ['#c5763a', '#f4c542', '#7bd389', '#5aa9ff', '#b48cf2', '#e57373'];
    for (let i = 0; i < 6; i++) { g.fillStyle = cols[i]; g.fillRect(0, i * 100, 600, 100); }
    g.fillStyle = 'rgba(255,255,255,.85)';
    g.beginPath(); g.arc(300, 300, 170, 0, Math.PI * 2); g.fill();
    g.fillStyle = '#2a1e03';
    g.font = 'bold 150px system-ui, sans-serif';
    g.textAlign = 'center'; g.textBaseline = 'middle';
    g.fillText('🏺', 300, 310);
    return c;
  }

  function wantPic(st, pic) {
    if (!pic) return;
    if (st.picKey === pic.key && st.picUrl === pic.url) return;
    st.picKey = pic.key;
    st.picUrl = pic.url;
    st.src = null;
    st.picCv = null;
    const my = ++st.picSeq;
    const take = (src) => {
      if (my !== st.picSeq || !live.has(st)) return;
      st.src = src;
      st.picCv = null;
      resetBitmaps(st);
      st.layerOk = false;
      kick(st);
    };
    const go = pic.kind === 'art'
      ? fetch(pic.url, { credentials: 'same-origin' }).then((r) => (r.ok ? r.json() : Promise.reject(new Error('HTTP ' + r.status)))).then((d) => drawArt(d && d.z))
      : loadImg(pic.url);
    go.then(take, () => take(fallbackPic()));
  }

  /// Картинка в розмір рамки на екрані (квадрат; фото — середина).
  function picCanvas(st) {
    if (!st.src) return null;
    const B = st.n * CELL, P = Math.min(1600, Math.max(64, Math.round(B * st.s * st.dpr)));
    if (st.picCv && st.picCv.width === P) return st.picCv;
    const c = document.createElement('canvas');
    c.width = c.height = P;
    const g = c.getContext('2d');
    g.imageSmoothingQuality = 'high';
    const sw = st.src.naturalWidth || st.src.width, sh = st.src.naturalHeight || st.src.height;
    const side = Math.min(sw, sh);
    g.drawImage(st.src, (sw - side) / 2, (sh - side) / 2, side, side, 0, 0, P, P);
    st.picCv = c;
    return c;
  }

  // =========================================================================================
  // Стан і розклад
  // =========================================================================================

  function state(root, ctx) {
    let st = root._sklei;
    if (!st) {
      st = root._sklei = {
        root, ctx, pieces: [], z: [], n: 4, cut: null, cutKey: '', s: 1, dpr: 1, picSeq: 0, picKey: '', picUrl: '',
        src: null, picCv: null, layer: null, layerOk: false, anims: [], raf: 0, drag: null, cur: null, padOn: false,
        ph: '', skew: 0, deadline: 0, frameP: null, devSent: null, lay: null, deck: null, deckAt: 0, fx: [],
      };
    }
    st.ctx = ctx;
    live.add(st);
    return st;
  }

  function shell(root, st) {
    if (st.el && st.el.isConnected) return;
    root.innerHTML = '<div class="skl">'
      + '<div class="skl-top"><span class="skl-pic"></span><span class="skl-clock"></span></div>'
      + '<div class="skl-race"></div>'
      + '<div class="skl-stage"><canvas class="skl-cv" role="img" aria-label="Рамка для картинки й рушник із черепками"></canvas><div class="skl-ov" hidden></div></div>'
      + '<div class="skl-tip muted small"></div>'
      + '<div class="skl-more" hidden></div></div>';
    st.el = root.firstChild;
    st.top = st.el.querySelector('.skl-pic');
    st.clock = st.el.querySelector('.skl-clock');
    st.race = st.el.querySelector('.skl-race');
    st.stage = st.el.querySelector('.skl-stage');
    st.cv = st.el.querySelector('.skl-cv');
    st.g = st.cv.getContext('2d');
    st.ov = st.el.querySelector('.skl-ov');
    st.tip = st.el.querySelector('.skl-tip');
    st.more = st.el.querySelector('.skl-more');
    wire(st);
  }

  /// Скільки місця: ширина — з root (вечірка вкладає нас у свій блок), висота — з того, що лишив каркас сайту.
  function layout(st) {
    const N = st.n, B = N * CELL;
    const W = Math.max(240, Math.floor(st.stage.clientWidth || st.root.clientWidth || 360));
    let H = window.innerHeight || 700;
    try { const f = HGames.ui.fit && HGames.ui.fit(); if (f) H = f.h - f.top - f.dock; } catch { /* без каркаса — вікно */ }
    // над полем — шапка картки, рядок картинки й перегони; під ним — підказка: усе разом має влізти в екран
    const card = st.root.closest('.gtable') || st.root;
    const off = Math.max(0, st.stage.getBoundingClientRect().top - card.getBoundingClientRect().top);
    const availH = Math.max(260, H - Math.min(off, 260) - 52);
    const side = W >= 560;
    let tw = B, th = B, ww, wh, s;
    if (side) { tw = Math.round(1.15 * B); th = Math.round(1.08 * B); }   // рушник трохи більший за рамку: черепки не налазять
    if (st.demo) {
      ww = wh = B;
      s = Math.min(W / (B + 2 * PAD), 260 / B);
    } else if (side) {
      ww = B + GAP + tw; wh = th;
      s = Math.min(W / (ww + 2 * PAD), availH / (wh + 2 * PAD));
    } else {
      ww = B; wh = 2 * B + GAP;
      s = Math.min(W / (ww + 2 * PAD), availH / (wh + 2 * PAD));
      const th2 = Math.round(0.72 * B), s2 = Math.min(W / (ww + 2 * PAD), availH / (B + th2 + GAP + 2 * PAD));
      if (s < s2 * 0.9) { th = th2; wh = B + GAP + th; s = s2; }
    }
    if (!st.demo) s = clamp(s, 210 / B, 560 / B);
    const tray = side ? { x: B + GAP, y: 0, w: tw, h: th } : { x: 0, y: B + GAP, w: tw, h: th };
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const key = [N, side, th, s.toFixed(4), dpr, !!st.demo].join('|');
    if (st.lay && st.lay.key === key) return false;
    const old = st.lay;
    st.lay = { key, side, B, tray, ww, wh };
    // На вузькому телефоні рушник нижчий за рамку: черепки на ньому дрібніші (як у телефонних пазлах) і
    // виростають до справжнього розміру, щойно береш у руку. Інакше 16 черепків лягали б один на одного.
    st.traySc = th < B ? clamp(th / B * 0.85, 0.55, 1) : 0.9;
    st.s = s;
    st.dpr = dpr;
    const cw = Math.round((ww + 2 * PAD) * s), ch = Math.round((wh + 2 * PAD) * s);
    st.cv.style.width = cw + 'px';
    st.cv.style.height = ch + 'px';
    st.cv.width = Math.round(cw * dpr);
    st.cv.height = Math.round(ch * dpr);
    st.el.classList.toggle('side', side);
    if (old) for (const p of st.pieces) if (!p.placed && p.zone === 'tray') placeInTray(st, p);
    resetBitmaps(st);
    st.picCv = null;
    st.layerOk = false;
    return true;
  }

  function placeInTray(st, p) {
    const t = st.lay.tray;
    const ins = INSET * (st.traySc || 1);
    p.x = t.x + ins + p.fx * (t.w - 2 * ins);
    p.y = t.y + ins + p.fy * (t.h - 2 * ins);
  }

  function resetBitmaps(st) { for (const p of st.pieces) { p.bmp = null; p.sh = null; } }

  /// Нова картинка (чи інше N, чи «Ще раз») — черепки з нуля: на столі за tray, з поворотом r0.
  function takeCut(st, v) {
    const cut = v.cut;
    const key = cut ? [v.picNo, v.pic && v.pic.key, cut.n, cut.tray.join(','), st.ctx.room && st.ctx.room.id].join('|') : '';
    if (key === st.cutKey) return false;
    st.cutKey = key;
    st.cut = cut;
    st.drag = null;
    st.anims = [];
    st.doneFx = false;
    if (!cut) { st.pieces = []; st.z = []; return true; }
    st.n = cut.n;
    st.lay = null;
    const N = cut.n;
    st.pieces = [];
    for (let k = 0; k < N * N; k++) {
      const pts = outline(cut, k), r = Math.floor(k / N), c = k % N;
      const cx = c * CELL + CELL / 2, cy = r * CELL + CELL / 2;
      const r0 = cut.tray[k * 3 + 2] | 0;
      st.pieces.push({
        k, r, c, cx, cy, pts, path: pathOf(pts, cx, cy), world: pathOf(pts, 0, 0),
        fx: cut.tray[k * 3] / 1000, fy: cut.tray[k * 3 + 1] / 1000, zone: 'tray', x: cx, y: cy,
        r0, taps: 0, ang: r0, sc: 1, placed: false, pending: false, bmp: null, sh: null,
      });
    }
    st.z = st.pieces.map((p) => p.k);
    return true;
  }

  const rotOf = (p) => (p.r0 + p.taps) % 4;

  // =========================================================================================
  // Малювання
  // =========================================================================================

  function pieceBitmaps(st, p) {
    if (p.bmp) return;
    const pc = picCanvas(st);
    if (!pc) return;
    const B = st.n * CELL, M = Math.ceil(BOX * st.s * st.dpr), k = st.s * st.dpr;
    const mk = () => { const c = document.createElement('canvas'); c.width = c.height = M; return c; };
    const bmp = mk(), g = bmp.getContext('2d');
    g.setTransform(k, 0, 0, k, M / 2, M / 2);
    g.save();
    g.clip(p.path);
    g.drawImage(pc, -p.cx, -p.cy, B, B);
    // ледь світліший край угорі-ліворуч і темніший унизу: черепок має товщину, а не вирізаний ножицями
    const gr = g.createLinearGradient(-60, -60, 60, 60);
    gr.addColorStop(0, 'rgba(255,248,230,.16)');
    gr.addColorStop(0.5, 'rgba(255,255,255,0)');
    gr.addColorStop(1, 'rgba(40,20,5,.18)');
    g.fillStyle = gr;
    g.fillRect(-90, -90, 180, 180);
    g.restore();
    g.lineJoin = 'round';
    g.strokeStyle = 'rgba(30,15,5,.75)';
    g.lineWidth = 2.6 / st.s;
    g.stroke(p.path);
    g.strokeStyle = 'rgba(255,240,215,.75)';
    g.lineWidth = 1 / st.s;
    g.stroke(p.path);
    const sh = mk(), h = sh.getContext('2d');
    h.setTransform(k, 0, 0, k, M / 2, M / 2);
    h.shadowColor = 'rgba(0,0,0,.55)';
    h.shadowBlur = 6 * st.dpr;
    h.fillStyle = 'rgba(0,0,0,.4)';
    h.fill(p.path);
    p.bmp = bmp;
    p.sh = sh;
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

  /// Що показати в рамці: 'full' — картинку цілою (Готуйсь, підсумок, глядач), 'play' — підказку й прирослі.
  function boardMode(st) {
    const v = st.ctx.view || {};
    if (!st.cut || v.ph === 'lobby') return 'demo';
    if (v.ph === 'ready') return 'ready';
    if (v.ph === 'pause' || v.ph === 'over' || !v.me || st.ctx.room.status === 'finished') return 'full';
    return 'play';
  }

  /// Шар під черепками: рамка, рушник, підказка й усе, що вже приросло (золоті шви — як кінцуґі).
  function buildLayer(st) {
    const L = st.lay, k = st.s * st.dpr;
    if (!st.layer) st.layer = document.createElement('canvas');
    const c = st.layer;
    if (c.width !== st.cv.width || c.height !== st.cv.height) { c.width = st.cv.width; c.height = st.cv.height; }
    const g = c.getContext('2d');
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.clearRect(0, 0, c.width, c.height);
    g.setTransform(k, 0, 0, k, PAD * k, PAD * k);
    const B = L.B, v = st.ctx.view || {}, mode = boardMode(st);
    const css = HGames.ui.css;
    // рушник під черепками: льон і червона вишита смужка
    const t = L.tray;
    if (!st.demo) {
    g.fillStyle = css('--skl-linen', '#e8dcc2');
    roundRect(g, t.x - 4, t.y - 4, t.w + 8, t.h + 8, 10);
    g.fill();
    g.strokeStyle = css('--skl-stitch', '#b8322a');
    g.lineWidth = 2.2;
    g.setLineDash([5, 4]);
    roundRect(g, t.x + 4, t.y + 4, t.w - 8, t.h - 8, 6);
    g.stroke();
    g.setLineDash([]);
    g.fillStyle = css('--skl-stitch', '#b8322a');
    for (let x = t.x + 18; x < t.x + t.w - 12; x += 22) {     // ромбики вишивки вздовж краю
      for (const y of [t.y + 13, t.y + t.h - 13]) {
        g.beginPath(); g.moveTo(x, y - 4); g.lineTo(x + 4, y); g.lineTo(x, y + 4); g.lineTo(x - 4, y); g.closePath(); g.fill();
      }
    }
    }
    // рамка
    g.fillStyle = css('--skl-frame', '#5a3a22');
    roundRect(g, -7, -7, B + 14, B + 14, 9);
    g.fill();
    g.fillStyle = css('--skl-board', '#1e2f26');
    g.fillRect(0, 0, B, B);
    const pc = picCanvas(st);
    if (mode === 'demo') {
      // лобі: розбитий глек трохи розсунутими черепками
      if (pc && st.pieces.length) for (const p of st.pieces) {
        pieceBitmaps(st, p);
        if (!p.bmp) continue;
        const dx = (p.cx - B / 2) * 0.12, dy = (p.cy - B / 2) * 0.12;
        g.drawImage(p.sh, p.cx + dx - BOX / 2 + 2, p.cy + dy - BOX / 2 + 3, BOX, BOX);
        g.drawImage(p.bmp, p.cx + dx - BOX / 2, p.cy + dy - BOX / 2, BOX, BOX);
      }
    } else if (pc && (mode === 'ready' || mode === 'full')) {
      g.drawImage(pc, 0, 0, B, B);
      if (mode === 'full' && st.pieces.length) {
        const mine = v.me && v.me.done != null;
        g.strokeStyle = mine ? css('--accent', '#f4c542') : 'rgba(255,255,255,.35)';
        g.globalAlpha = 0.85;
        g.lineWidth = (mine ? 1.3 : 1) / st.s;
        for (const p of st.pieces) g.stroke(p.world);
        g.globalAlpha = 1;
      }
      if (!v.me && v.ph === 'go') { g.fillStyle = 'rgba(15,31,24,.45)'; g.fillRect(0, 0, B, B); }
    } else if (pc) {
      if (v.hint) {
        g.globalAlpha = 0.2;
        g.drawImage(pc, 0, 0, B, B);
        g.globalAlpha = 1;
        g.strokeStyle = 'rgba(255,255,255,.14)';
        g.lineWidth = 1 / st.s;
        for (const p of st.pieces) if (!p.placed) g.stroke(p.world);
      } else {
        g.strokeStyle = 'rgba(255,255,255,.07)';
        g.lineWidth = 1 / st.s;
        for (let i = 1; i < st.n; i++) {
          g.beginPath(); g.moveTo(i * CELL, 0); g.lineTo(i * CELL, B); g.moveTo(0, i * CELL); g.lineTo(B, i * CELL); g.stroke();
        }
      }
      const placed = st.pieces.filter((p) => p.placed);
      if (placed.length) {
        g.save();
        const u = new Path2D();
        for (const p of placed) u.addPath(p.world);
        g.clip(u);
        g.drawImage(pc, 0, 0, B, B);
        g.restore();
        g.strokeStyle = css('--accent', '#f4c542');
        g.globalAlpha = 0.85;
        g.lineWidth = 1.6 / st.s;
        for (const p of placed) g.stroke(p.world);
        g.globalAlpha = 1;
      }
    } else {
      g.fillStyle = 'rgba(255,255,255,.55)';
      g.font = '600 ' + Math.round(22) + 'px system-ui, sans-serif';
      g.textAlign = 'center';
      g.fillText('Несемо картинку…', B / 2, B / 2);
    }
    st.layerOk = true;
  }

  function drawPiece(g, st, p, lift) {
    pieceBitmaps(st, p);
    if (!p.bmp) return;
    g.save();
    g.translate(p.x, p.y);
    const sc = (lift ? 1.06 : 1) * (p.sc || 1);
    g.save();
    g.translate(lift ? 5 : 2, lift ? 8 : 3);
    g.rotate(p.ang * Math.PI / 2);
    g.scale(sc, sc);
    g.drawImage(p.sh, -BOX / 2, -BOX / 2, BOX, BOX);
    g.restore();
    g.rotate(p.ang * Math.PI / 2);
    g.scale(sc, sc);
    g.drawImage(p.bmp, -BOX / 2, -BOX / 2, BOX, BOX);
    g.restore();
  }

  function draw(st) {
    if (!st.lay || !st.cv.isConnected) return;
    if (!st.layerOk) buildLayer(st);
    const g = st.g, k = st.s * st.dpr;
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.clearRect(0, 0, st.cv.width, st.cv.height);
    g.drawImage(st.layer, 0, 0);
    g.setTransform(k, 0, 0, k, PAD * k, PAD * k);
    const now = performance.now();
    const mode = boardMode(st);
    if (mode === 'play') {
      for (const id of st.z) {
        const p = st.pieces[id];
        if (p.placed || (st.drag && st.drag.p === p)) continue;
        drawPiece(g, st, p, false);
      }
      if (st.drag) drawPiece(g, st, st.drag.p, true);
    }
    // спалахи: черепок приріс — золотий обідок і кілька іскорок
    st.fx = st.fx.filter((f) => now - f.t0 < f.dur);
    for (const f of st.fx) {
      const t = (now - f.t0) / f.dur;
      if (f.kind === 'snap') {
        const p = f.p;
        g.save();
        g.globalAlpha = 1 - t;
        g.fillStyle = 'rgba(255,240,190,.45)';
        g.fill(p.world);
        g.strokeStyle = HGames.ui.css('--accent', '#f4c542');
        g.lineWidth = (4 - 3 * t) / st.s;
        g.stroke(p.world);
        g.fillStyle = HGames.ui.css('--accent', '#f4c542');
        for (let i = 0; i < 7; i++) {
          const a = i / 7 * Math.PI * 2 + p.k, d = 30 + 50 * t;
          g.beginPath(); g.arc(p.cx + Math.cos(a) * d, p.cy + Math.sin(a) * d, 3.2 * (1 - t) + 0.5, 0, Math.PI * 2); g.fill();
        }
        g.restore();
      } else if (f.kind === 'done') {
        // склав — світла смуга пробігає по цілій картинці
        const B = st.lay.B, x = -B * 0.5 + t * B * 2;
        g.save();
        g.beginPath(); g.rect(0, 0, B, B); g.clip();
        const gr = g.createLinearGradient(x - 120, 0, x + 120, B * 0.4);
        gr.addColorStop(0, 'rgba(255,255,255,0)');
        gr.addColorStop(0.5, 'rgba(255,248,210,.55)');
        gr.addColorStop(1, 'rgba(255,255,255,0)');
        g.fillStyle = gr;
        g.fillRect(0, 0, B, B);
        g.restore();
      }
    }
    if (st.padOn && st.cur && mode === 'play') {
      const c = st.cur;
      g.strokeStyle = HGames.ui.css('--accent', '#f4c542');
      g.lineWidth = 3 / st.s;
      g.beginPath(); g.arc(c.x, c.y, 16, 0, Math.PI * 2); g.stroke();
      g.fillStyle = HGames.ui.css('--accent', '#f4c542');
      g.beginPath(); g.arc(c.x, c.y, 4, 0, Math.PI * 2); g.fill();
    }
  }

  function kick(st) {
    if (st.raf || !live.has(st)) return;
    st.raf = requestAnimationFrame((t) => loop(st, t));
  }

  /// Один крок: польоти, повороти, курсор пада. Повертає, чи ще щось рухається.
  function step(st, now) {
    const dt = Math.min(0.05, ((now - (st.lastT || now)) / 1000) || 0.016);
    st.lastT = now;
    let more = st.fx.length > 0;
    st.anims = st.anims.filter((a) => {
      const t = clamp((now - a.t0) / a.dur, 0, 1);
      if (t <= 0) { more = true; return true; }
      const e = 1 - Math.pow(1 - t, 3);
      a.p.x = a.x0 + (a.x1 - a.x0) * e;
      a.p.y = a.y0 + (a.y1 - a.y0) * e;
      if (a.a1 != null) a.p.ang = a.a0 + (a.a1 - a.a0) * e;
      if (t >= 1) { if (a.end) a.end(); return false; }
      more = true;
      return true;
    });
    for (const p of st.pieces) {
      const sw = p.placed || p.zone !== 'tray' || (st.drag && st.drag.p === p) || p.pending ? 1 : (st.traySc || 1);
      if (Math.abs(sw - p.sc) > 0.003) { p.sc += (sw - p.sc) * Math.min(1, dt * 14); more = true; } else p.sc = sw;
      if (st.anims.some((a) => a.p === p && a.a1 != null)) continue;
      const want = p.r0 + p.taps;
      if (Math.abs(want - p.ang) > 0.001) { p.ang += (want - p.ang) * Math.min(1, dt * 16); if (Math.abs(want - p.ang) < 0.01) p.ang = want; more = true; }
    }
    // курсор пада: стік — рука; що довше тримаєш, то швидше
    const dx = (held.ArrowRight ? 1 : 0) - (held.ArrowLeft ? 1 : 0), dy = (held.ArrowDown ? 1 : 0) - (held.ArrowUp ? 1 : 0);
    if ((dx || dy) && st.cur && st.lay) {
      st.holdT = (st.holdT || 0) + dt;
      const sp = 220 + Math.min(1, st.holdT / 0.8) * 380;
      const L = st.lay, len = Math.hypot(dx, dy);
      st.cur.x = clamp(st.cur.x + dx / len * sp * dt, -PAD, L.ww + PAD);
      st.cur.y = clamp(st.cur.y + dy / len * sp * dt, -PAD, L.wh + PAD);
      if (st.drag) { st.drag.p.x = st.cur.x; st.drag.p.y = st.cur.y; }
      more = true;
    } else st.holdT = 0;
    return more;
  }

  function loop(st, now) {
    st.raf = 0;
    if (!live.has(st)) return;
    const more = step(st, now);
    draw(st);
    if (more) kick(st);
  }

  // =========================================================================================
  // Ввід: мишка/палець, колесо, правий клік, клавіші й пад
  // =========================================================================================

  const canPlay = (st) => {
    const v = st.ctx.view || {};
    return v.ph === 'go' && !!v.me && v.me.done == null && st.ctx.room && st.ctx.room.status === 'playing' && !!st.cut;
  };

  function toWorld(st, e) {
    const r = st.cv.getBoundingClientRect();
    return { x: (e.clientX - r.left) / st.s - PAD, y: (e.clientY - r.top) / st.s - PAD };
  }

  function hit(st, x, y) {
    const g = st.g;
    g.save();
    g.setTransform(1, 0, 0, 1, 0, 0);
    let found = null;
    for (let i = st.z.length - 1; i >= 0; i--) {
      const p = st.pieces[st.z[i]];
      if (p.placed) continue;
      const a = -p.ang * Math.PI / 2, dx = x - p.x, dy = y - p.y;
      const k = p.sc || 1;
      const lx = (dx * Math.cos(a) - dy * Math.sin(a)) / k, ly = (dx * Math.sin(a) + dy * Math.cos(a)) / k;
      if (Math.abs(lx) > 85 || Math.abs(ly) > 85) continue;
      if (g.isPointInPath(p.path, lx, ly)) { found = p; break; }
    }
    g.restore();
    return found;
  }

  function nearest(st, x, y, max) {
    let best = null, bd = max * max;
    for (const p of st.pieces) {
      if (p.placed) continue;
      const d = (p.x - x) ** 2 + (p.y - y) ** 2;
      if (d < bd) { bd = d; best = p; }
    }
    return best;
  }

  function toTop(st, p) {
    st.z = st.z.filter((k) => k !== p.k);
    st.z.push(p.k);
  }

  function rotate(st, p) {
    if (!p || p.placed || !(st.ctx.view || {}).rotate) return;
    p.taps++;
    toTop(st, p);
    st.anims = st.anims.filter((a) => a.p !== p || a.a1 == null);
    kick(st);
  }

  function grab(st, p, x, y) {
    st.anims = st.anims.filter((a) => a.p !== p);
    if (Math.abs(p.ang - (p.r0 + p.taps)) > 0.5) p.ang = p.r0 + p.taps;
    st.drag = { p, ox: x - p.x, oy: y - p.y, sx: x, sy: y, t0: performance.now(), moved: 0 };
    toTop(st, p);
    kick(st);
  }

  /// Відпустили черепок: поруч із його місцем і рівно — приростає (анімація, put); інакше лежить, де кинули.
  function drop(st, p, mag) {
    const L = st.lay, B = L.B;
    p.x = clamp(p.x, -PAD + 20, L.ww + PAD - 20);
    p.y = clamp(p.y, -PAD + 20, L.wh + PAD - 20);
    const near = Math.hypot(p.x - p.cx, p.y - p.cy) <= mag;
    if (near && rotOf(p) === 0 && canPlay(st)) { snap(st, p); return; }
    if (p.x >= -20 && p.x <= B + 20 && p.y >= -20 && p.y <= B + 20) p.zone = 'board';
    else {
      const t = L.tray;
      p.zone = 'tray';
      const ins = INSET * (st.traySc || 1);
      p.fx = clamp((p.x - t.x - ins) / (t.w - 2 * ins), 0, 1);
      p.fy = clamp((p.y - t.y - ins) / (t.h - 2 * ins), 0, 1);
      const x0 = p.x, y0 = p.y;
      placeInTray(st, p);
      const x1 = p.x, y1 = p.y;
      p.x = x0; p.y = y0;
      if (Math.hypot(x1 - p.x, y1 - p.y) > 1) st.anims.push({ p, x0: p.x, y0: p.y, x1, y1, t0: performance.now(), dur: 180 });
    }
    kick(st);
  }

  function snap(st, p) {
    const was = { x: p.x, y: p.y, zone: p.zone };
    p.pending = true;
    st.anims.push({
      p, x0: p.x, y0: p.y, x1: p.cx, y1: p.cy, t0: performance.now(), dur: 110,
      end: () => {
        p.placed = true;
        st.layerOk = false;
        st.fx.push({ kind: 'snap', p, t0: performance.now(), dur: 650 });
        try { if (coarse() && navigator.vibrate) navigator.vibrate(12); } catch { /* без вібро */ }
        hudCount(st);
        kick(st);
      },
    });
    kick(st);
    const r = st.ctx.act('put', { k: p.k, c: p.k, r: 0, t: p.taps });
    Promise.resolve(r).then((res) => {
      p.pending = false;
      if (res && res.ok === false && !serverHas(st, p.k)) {
        // сервер не прийняв (картинка якраз скінчилась чи розійшлись лічильники) — черепок назад, де лежав
        st.anims = st.anims.filter((a) => a.p !== p);
        p.placed = false;
        p.x = was.x; p.y = was.y; p.zone = was.zone;
        st.layerOk = false;
        hudCount(st);
        kick(st);
      }
    }, () => { p.pending = false; });
  }

  const serverHas = (st, k) => { const me = (st.ctx.view || {}).me; return !!(me && me.placed && me.placed.indexOf(k) >= 0); };

  function wire(st) {
    const cv = st.cv;
    cv.addEventListener('pointerdown', (e) => {
      if (st.drag || !canPlay(st) || (e.button != null && e.button > 0)) return;
      st.padOn = false;
      const w = toWorld(st, e), p = hit(st, w.x, w.y);
      if (!p) return;
      try { cv.setPointerCapture(e.pointerId); } catch { /* палець уже відпустили */ }
      grab(st, p, w.x, w.y);
      st.drag.id = e.pointerId;
      e.preventDefault();
    });
    cv.addEventListener('pointermove', (e) => {
      const d = st.drag;
      if (!d || d.id !== e.pointerId) return;
      const w = toWorld(st, e);
      d.moved = Math.max(d.moved, Math.hypot(w.x - d.sx, w.y - d.sy) * st.s);
      d.p.x = w.x - d.ox;
      d.p.y = w.y - d.oy;
      kick(st);
    });
    const up = (e) => {
      const d = st.drag;
      if (!d || d.id !== e.pointerId) return;
      st.drag = null;
      const tap = d.moved < 7 && performance.now() - d.t0 < 400;
      if (tap) { rotate(st, d.p); return; }
      drop(st, d.p, MAG);
    };
    cv.addEventListener('pointerup', up);
    cv.addEventListener('pointercancel', (e) => { if (st.drag && st.drag.id === e.pointerId) { const p = st.drag.p; st.drag = null; drop(st, p, MAG); } });
    cv.addEventListener('wheel', (e) => {
      if (!canPlay(st)) return;
      const w = toWorld(st, e), p = st.drag ? st.drag.p : hit(st, w.x, w.y);
      if (!p) return;
      e.preventDefault();
      const now = performance.now();
      if (now - (st.wheelAt || 0) < 160) return;     // тачпад шле десятки подій на один жест
      st.wheelAt = now;
      rotate(st, p);
    }, { passive: false });
    cv.addEventListener('contextmenu', (e) => {
      if (!canPlay(st)) return;
      e.preventDefault();
      const w = toWorld(st, e), p = st.drag ? st.drag.p : hit(st, w.x, w.y);
      rotate(st, p);
    });
  }

  function padStart(st) {
    if (!st.lay) return;
    if (!st.cur) {
      const p = st.pieces.find((q) => !q.placed);
      st.cur = p ? { x: p.x, y: p.y } : { x: st.lay.tray.x + st.lay.tray.w / 2, y: st.lay.tray.y + st.lay.tray.h / 2 };
    }
    st.padOn = true;
  }

  function padGrab(st) {
    padStart(st);
    const c = st.cur;
    if (st.drag) {
      const p = st.drag.p;
      st.drag = null;
      drop(st, p, MAG_PAD);
      return;
    }
    const p = hit(st, c.x, c.y) || nearest(st, c.x, c.y, 70);
    if (!p) return;
    grab(st, p, c.x, c.y);
    st.drag.ox = 0; st.drag.oy = 0;
    p.x = c.x; p.y = c.y;
  }

  function padJump(st, dir) {
    padStart(st);
    const loose = st.pieces.filter((p) => !p.placed && (!st.drag || st.drag.p !== p));
    if (!loose.length) return;
    st.jump = ((st.jump || 0) + dir + loose.length) % loose.length;
    const p = loose[st.jump];
    if (st.drag) return;
    st.cur.x = p.x; st.cur.y = p.y;
    kick(st);
  }

  // =========================================================================================
  // Над полем: що за картинка, годинник, перегони, накладки фаз
  // =========================================================================================

  function picLine(v) {
    const p = v.pic;
    if (!p) return '🏺 Склей глек';
    const no = v.pics > 1 ? '<span class="muted">' + v.picNo + ' / ' + v.pics + '</span> ' : '';
    const by = p.by ? ' <span class="muted">· від ' + escH(p.by) + '</span>' : '';
    return no + (KINDS[p.kind] || '🏺') + ' <b>' + escH(p.title || 'Картинка') + '</b>' + by;
  }

  function escH(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

  function syncClock(st, leftMs) {
    st.deadline = performance.now() + (leftMs || 0);
  }

  function clockText(st) {
    const v = st.ctx.view || {};
    const left = Math.max(0, st.deadline - performance.now());
    if (v.ph === 'ready') return '⏳ ' + Math.ceil(left / 1000);
    if (v.ph === 'go') {
      if (v.me && v.me.done != null) return '✅ ' + fmt(v.me.done);
      const since = v.goAt ? Date.now() + st.skew - Date.parse(v.goAt) : 0;
      return '⏱ ' + fmt(since) + ' <span class="muted">· ще ' + fmt(left) + '</span>';
    }
    if (v.ph === 'pause') return '<span class="muted">далі за ' + Math.ceil(left / 1000) + ' с</span>';
    return '';
  }

  const hudCount = (st) => renderRace(st);

  function raceRows(st) {
    const v = st.ctx.view || {};
    const rows = (v.players || []).map((p) => Object.assign({}, p));
    const f = st.frameP;
    if (f && f.picNo === v.picNo) for (const q of f.p || []) {
      const r = rows.find((x) => x.seat === q[0]);
      if (r) { r.placed = Math.max(r.placed, q[1]); r.of = q[2]; if (q[3] >= 0) r.done = q[3]; }
    }
    // свій рядок — з того, що приросло тут (сервер підтвердить з наступним тиком)
    const me = rows.find((x) => x.seat === st.ctx.seat);
    if (me && v.me && v.ph === 'go' && st.pieces.length === me.of) me.placed = Math.max(me.placed, st.pieces.filter((p) => p.placed).length);
    return rows;
  }

  function renderRace(st) {
    const v = st.ctx.view || {};
    if (v.ph === 'lobby' || !v.players || !v.players.length) { st.race.innerHTML = ''; st.raceKey = ''; return; }
    const rows = raceRows(st);
    const mySeat = st.ctx.seat;
    const key = rows.map((r) => [r.seat, r.placed, r.of, r.done, r.total, r.gone, r.name].join(':')).join('|') + v.ph;
    if (key === st.raceKey) return;
    st.raceKey = key;
    st.race.innerHTML = rows.map((r) => {
      const done = r.done != null;
      const pct = done ? 100 : r.of ? Math.round(r.placed / r.of * 100) : 0;
      const tot = v.pics > 1 && r.total ? ' <span class="skl-tot" title="очки за партію">' + r.total + '</span>' : '';
      return '<div class="skl-run' + (r.seat === mySeat ? ' me' : '') + (done ? ' done' : '') + (r.gone ? ' gone' : '') + '" style="--c:' + seatColor(r.seat) + '">'
        + '<b>' + (r.bot && !/🤖/.test(r.name || '') ? '🤖 ' : '') + escH(r.name || '…') + tot + '</b><span class="skl-bar"><s style="width:' + pct + '%"></s></span>'
        + '<em>' + (done ? '✓ ' + fmt(r.done) : r.placed + '/' + r.of) + '</em></div>';
    }).join('');
  }

  function overlay(st) {
    const v = st.ctx.view || {};
    const ov = st.ov;
    let html = '', cls = '';
    const lobby = v.ph === 'lobby' || !st.ctx.room || st.ctx.room.status === 'lobby';
    if (lobby) {
      html = '';
    } else if (v.ph === 'ready') {
      cls = 'ready';
      html = '<div class="skl-big">Запам’ятовуй!</div><div class="muted">'
        + (v.hint ? 'зараз розіб’ється' : 'зараз розіб’ється — і підказки в рамці не буде') + '</div>';
    } else if (v.ph === 'go' && v.me && v.me.done != null) {
      cls = 'note';
      html = '✅ Склав за <b>' + fmt(v.me.done) + '</b>' + (v.party ? '' : ' — чекаємо інших');
    } else if (v.ph === 'go' && !v.me) {
      cls = 'note';
      html = '👀 Дивишся, хто швидше склеїть';
    } else if (v.ph === 'pause' && !v.party) {
      cls = 'card';
      html = picTable(st, v.history && v.history[v.history.length - 1]);
    } else if (v.ph === 'over' && !v.party) {
      cls = 'card';
      html = finalTable(st, v);
    }
    placeOverlay(st, cls);
    const k = cls + '|' + html;
    if (k === st.ovKey) return;
    st.ovKey = k;
    ov.hidden = !html;
    ov.className = 'skl-ov ' + cls;
    ov.innerHTML = html;
  }

  /// Накладка — над рушником, а не над рамкою: картинку (запам'ятати, роздивитись склеєне) нічим не закриваємо.
  /// Глядачу й тому, хто склав, рушник порожній — там і підсумки.
  function placeOverlay(st, cls) {
    const ov = st.ov, L = st.lay;
    if (!L || st.demo) { ov.style.left = ov.style.top = ''; return; }
    const t = L.tray, x0 = st.cv.offsetLeft, y0 = st.cv.offsetTop;
    const x = x0 + (t.x + t.w / 2 + PAD) * st.s;
    const y = y0 + (t.y + PAD) * st.s + (cls === 'ready' ? t.h * st.s * 0.3 : 10);
    ov.style.left = Math.round(x) + 'px';
    ov.style.top = Math.round(y) + 'px';
  }

  function nameOf(st, seat) {
    const p = ((st.ctx.view || {}).players || []).find((x) => x.seat === seat);
    return p ? (p.bot && !/🤖/.test(p.name) ? '🤖 ' : '') + p.name : st.ctx.nameOf ? st.ctx.nameOf(seat) : '';
  }

  function picTable(st, h) {
    if (!h) return '';
    const rows = h.rows.slice().sort((a, b) => a.place - b.place || a.seat - b.seat);
    return '<h4>' + (KINDS[h.pic && h.pic.kind] || '🏺') + ' ' + escH(h.pic && h.pic.title) + '</h4><table class="skl-tbl"><tbody>'
      + rows.map((r) => '<tr' + (r.seat === st.ctx.seat ? ' class="me"' : '') + '><td>' + (r.place === 1 && r.ms >= 0 ? '🏆' : r.place) + '</td><td>'
        + escH(nameOf(st, r.seat)) + '</td><td class="muted">' + (r.ms >= 0 ? fmt(r.ms) : r.placed + '/' + r.of) + '</td><td class="num">+' + r.pts + '</td></tr>').join('')
      + '</tbody></table>';
  }

  function finalTable(st, v) {
    const hist = v.history || [];
    const rows = (v.players || []).slice().sort((a, b) => b.total - a.total || a.seat - b.seat);
    const win = v.winners || [];
    return '<h4>' + (win.length ? '🏆 ' + win.map((s) => escH(nameOf(st, s))).join(', ') : rows.length && rows[0].bot ? '🤖 Бот склеїв швидше' : 'Нічия') + '</h4>'
      + '<table class="skl-tbl"><thead><tr><th></th><th></th>' + hist.map((h, i) => '<th title="' + escH(h.pic && h.pic.title) + '">' + (KINDS[h.pic && h.pic.kind] || '') + (i + 1) + '</th>').join('')
      + '<th>Σ</th></tr></thead><tbody>'
      + rows.map((r, i) => '<tr' + (r.seat === st.ctx.seat ? ' class="me"' : '') + '><td>' + (i + 1) + '</td><td>' + escH(nameOf(st, r.seat)) + '</td>'
        + hist.map((h) => { const x = h.rows.find((q) => q.seat === r.seat); return '<td class="num muted">' + (x ? x.pts : '') + '</td>'; }).join('')
        + '<td class="num"><b>' + r.total + '</b></td></tr>').join('') + '</tbody></table>';
  }

  function tipText(st) {
    const v = st.ctx.view || {};
    if (!st.ctx.room || v.ph === 'lobby' || st.ctx.room.status === 'lobby') return '';
    if (v.ph === 'go' && v.me && v.me.done == null) {
      const rot = v.rotate ? (st.padOn ? ' · Ⓧ — поворот' : coarse() ? ' · тап — поворот на 90°' : ' · клік, колесо чи правий клік — поворот') : '';
      return (st.padOn ? 'Стік — рука, Ⓐ — взяти/покласти, RB — до наступного' : coarse() ? 'Тягни черепок пальцем на його місце' : 'Тягни черепок мишкою на його місце')
        + rot + (v.hint ? '' : ' · без підказки');
    }
    return '';
  }

  // =========================================================================================
  // Лобі: правила й колода (свої картинки за черепки)
  // =========================================================================================

  async function api(st, method, url, body) {
    const ctx = st.ctx;
    const h = { 'X-Nick': encodeURIComponent((ctx.me && ctx.me.nick) || '') };
    let b;
    if (body instanceof Blob) { h['Content-Type'] = body.type || 'application/octet-stream'; b = body; }
    else if (body) { h['Content-Type'] = 'application/json'; b = JSON.stringify(body); }
    const r = await fetch(url, { method, headers: h, body: b, credentials: 'same-origin' });
    let d = null;
    try { d = await r.json(); } catch { /* без тіла */ }
    if (!r.ok) throw new Error((d && d.message) || 'HTTP ' + r.status);
    return d || {};
  }

  async function loadDeck(st, force) {
    if (st.deckBusy || (!force && st.deck && Date.now() - st.deckAt < 60000)) return;
    st.deckBusy = true;
    try {
      const [deck, mine] = await Promise.all([api(st, 'GET', '/api/games/sklei/deck'), api(st, 'GET', '/api/games/sklei/mine').catch(() => null)]);
      st.deck = deck;
      st.mine = mine;
      st.deckAt = Date.now();
    } catch { st.deck = st.deck || { builtin: [], own: [], photos: 0, arts: 0 }; }
    st.deckBusy = false;
    st.moreKey = '';
    if (live.has(st)) renderMore(st);
  }

  function renderMore(st) {
    const ctx = st.ctx, v = ctx.view || {};
    const show = !ctx.embedded && !v.party && ctx.room && ctx.room.status !== 'playing';
    st.more.hidden = !show;
    if (!show) return;
    if (!st.deck) { loadDeck(st); }
    const d = st.deck, m = st.mine;
    const key = JSON.stringify([d && d.own && d.own.length, d && d.balance, m && m.items && m.items.map((x) => x.id + (x.hidden ? 'h' : '')), st.deckOpen]);
    if (key === st.moreKey) return;
    st.moreKey = key;
    const rules = '<div class="skl-rules"><b>Як грати.</b> Картинку розбито — перетягни кожен черепок на його місце в рамці'
      + ' й поверни рівно: тап (на ПК ще колесо чи правий клік) — +90°. Ліг поруч і рівно — приріс сам. Три картинки поспіль,'
      + ' за місце в кожній — очки: 10 / 7 / 5 / 4… Хто не склав до кінця — половина.</div>';
    if (!d) { st.more.innerHTML = rules + '<p class="muted small">Колода завантажується…</p>'; return; }
    const own = d.own || [];
    const sum = [(d.builtin || []).length + ' розписів', d.photos + ' фото «Де це?»', d.arts + ' малюнків з альбому Піктіонарі', own.length + ' своїх'];
    const thumb = (x, mineList) => '<figure class="skl-th' + (x.hidden ? ' hid' : '') + '"><img loading="lazy" src="' + escH(x.url) + '" alt="">'
      + '<figcaption>' + (mineList ? (x.hidden ? 'схована' : 'у колоді') : 'від ' + escH(x.by)) + (x.solved ? ' · склали ' + x.solved : '') + '</figcaption>'
      + (mineList ? '<button type="button" class="ghost small" data-hide="' + x.id + '" data-h="' + (x.hidden ? 0 : 1) + '">' + (x.hidden ? '↩ повернути' : '🙈 сховати') + '</button>' : '')
      + (!mineList && d.admin ? '<button type="button" class="ghost small skl-del" data-del="' + x.id + '" title="Зняти з колоди">🗑</button>' : '') + '</figure>';
    const buy = d.account
      ? '<button type="button" class="primary" data-buy>📷 Своя картинка — ' + d.price + ' 🏺</button> <span class="muted small">у тебе ' + d.balance + ' 🏺 · хтось склав твою — тобі +1 🏺</span>'
      : '<span class="muted small">📷 Свою картинку в колоду можна купити за ' + d.price + ' 🏺 — лише з акаунта</span>';
    const mine = m && m.items && m.items.length
      ? '<div class="skl-sub">Мої картинки</div><div class="skl-grid">' + m.items.map((x) => thumb(x, true)).join('') + '</div>' : '';
    st.more.innerHTML = rules + '<details class="skl-deck"' + (st.deckOpen ? ' open' : '') + '><summary>🖼 Колода: ' + sum.join(' · ') + '</summary>'
      + '<div class="skl-buy">' + buy + '</div>' + mine
      + (own.length ? '<div class="skl-sub">Свої картинки друзів</div><div class="skl-grid">' + own.slice(0, 60).map((x) => thumb(x, false)).join('') + '</div>' : '')
      + '</details>';
    const det = st.more.querySelector('details');
    det.addEventListener('toggle', () => { st.deckOpen = det.open; });
    const b = st.more.querySelector('[data-buy]');
    if (b) b.onclick = () => pickFile(st);
    for (const x of st.more.querySelectorAll('[data-hide]')) x.onclick = async () => {
      try {
        const r = await api(st, 'POST', '/api/games/sklei/pic/hide', { id: +x.dataset.hide, hidden: x.dataset.h === '1' });
        if (r.message) ctx.toast(r.message, 'ok');
      } catch (e) { ctx.toast(e.message, 'err'); }
      loadDeck(st, true);
    };
    for (const x of st.more.querySelectorAll('[data-del]')) x.onclick = async () => {
      if (!window.confirm('Зняти цю картинку з колоди назавжди?')) return;
      try {
        const r = await api(st, 'POST', '/api/games/sklei/pic/remove', { id: +x.dataset.del });
        if (r.message) ctx.toast(r.message, 'ok');
      } catch (e) { ctx.toast(e.message, 'err'); }
      loadDeck(st, true);
    };
  }

  function pickFile(st) {
    const inp = document.createElement('input');
    inp.type = 'file';
    inp.accept = 'image/*';
    inp.onchange = () => {
      const f = inp.files && inp.files[0];
      if (!f) return;
      const url = URL.createObjectURL(f);
      loadImg(url).then((img) => cropper(st, { img, w: img.naturalWidth, h: img.naturalHeight, url }),
        () => { URL.revokeObjectURL(url); st.ctx.toast('Цей файл браузер не відкриває як картинку — спробуй JPEG чи PNG', 'err'); });
    };
    inp.click();
  }

  /// Шматок фото (квадрат) → canvas out×out; зменшуємо вдвічі за крок, щоб не було «піску» (як у Лавці).
  function shrink(src, sx, sy, side, out) {
    let from = src.img, fx = sx, fy = sy, fs = side;
    while (fs / 2 >= out * 1.4) {
      const n = Math.round(fs / 2), t = document.createElement('canvas');
      t.width = t.height = n;
      const g = t.getContext('2d');
      g.imageSmoothingQuality = 'high';
      g.drawImage(from, fx, fy, fs, fs, 0, 0, n, n);
      from = t; fx = 0; fy = 0; fs = n;
    }
    const c = document.createElement('canvas');
    c.width = c.height = out;
    const g = c.getContext('2d');
    g.fillStyle = '#ffffff';
    g.fillRect(0, 0, out, out);
    g.imageSmoothingQuality = 'high';
    g.drawImage(from, fx, fy, fs, fs, 0, 0, out, out);
    return c;
  }

  const toBlob = (c, type, q) => new Promise((done) => c.toBlob(done, type, q));
  async function encode(c) {
    const probe = await toBlob(c, 'image/webp', 0.85);
    const webp = !!probe && probe.type === 'image/webp';
    for (const q of [0.85, 0.75, 0.6, 0.45]) {
      const b = webp && q === 0.85 ? probe : await toBlob(c, webp ? 'image/webp' : 'image/jpeg', q);
      if (b && b.size <= PH_MAX) return b;
    }
    return null;
  }

  /// Квадратна обрізка своєї картинки: тягнути, збільшувати (коліщатко, щипок, повзунок), «Купити».
  function cropper(st, src) {
    const d = st.deck || {};
    const wrap = document.createElement('div');
    wrap.className = 'modal skl-crop';
    wrap.innerHTML = '<div class="card" role="dialog" aria-modal="true" aria-label="Своя картинка: обрізати"><h3>📷 Своя картинка</h3>'
      + '<div class="skl-crop-stage" tabindex="0" aria-label="Картинка. Стрілки — посунути, плюс і мінус — збільшити"><canvas></canvas></div>'
      + '<label class="skl-crop-zoom"><span aria-hidden="true">🔍−</span><input type="range" min="1" max="' + PH_ZOOM + '" step="0.01" value="1" aria-label="Збільшення"><span aria-hidden="true">+</span></label>'
      + '<div class="muted small">Посунь і збільш так, щоб у квадраті було головне. Яскраве й з великими деталями склеювати найцікавіше.'
      + ' <b>Картинку побачать усі, хто гратиме.</b> Коштує ' + (d.price || 400) + ' 🏺 (у тебе ' + (d.balance == null ? '?' : d.balance) + ').</div>'
      + '<div class="row"><button class="primary" type="button" data-yes>Купити за ' + (d.price || 400) + ' 🏺</button>'
      + '<button class="ghost" type="button" data-no>Скасувати</button></div></div>';
    document.body.appendChild(wrap);
    st.cropEl = wrap;
    const stage = wrap.querySelector('.skl-crop-stage'), cv = stage.querySelector('canvas'), zoom = wrap.querySelector('input[type=range]');
    const S = stage.clientWidth || 280, dpr = Math.min(3, window.devicePixelRatio || 1);
    cv.width = cv.height = Math.round(S * dpr);
    const g = cv.getContext('2d');
    const base = S / Math.min(src.w, src.h);
    let z = 1, ox = (S - src.w * base) / 2, oy = (S - src.h * base) / 2;
    const k = () => base * z;
    const fixPos = () => { ox = Math.min(0, Math.max(S - src.w * k(), ox)); oy = Math.min(0, Math.max(S - src.h * k(), oy)); };
    let raf = 0;
    const paint = () => { raf = 0; g.fillStyle = '#111'; g.fillRect(0, 0, cv.width, cv.height); g.drawImage(src.img, ox * dpr, oy * dpr, src.w * k() * dpr, src.h * k() * dpr); };
    const redraw = () => { if (!raf) raf = requestAnimationFrame(paint); };
    const zoomTo = (nz, fx, fy) => {
      nz = clamp(nz, 1, PH_ZOOM);
      const r = nz / z;
      ox = fx - (fx - ox) * r; oy = fy - (fy - oy) * r; z = nz;
      fixPos(); zoom.value = String(z); redraw();
    };
    fixPos(); paint();
    const pts = new Map();
    let pinch = null;
    const pinchNow = () => { const [a, b] = [...pts.values()]; return { d: Math.hypot(a.x - b.x, a.y - b.y) || 1, x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }; };
    stage.addEventListener('pointerdown', (e) => {
      if (pts.size >= 2) return;
      try { stage.setPointerCapture(e.pointerId); } catch { /* без захоплення */ }
      pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
      pinch = pts.size === 2 ? pinchNow() : null;
      e.preventDefault();
    });
    stage.addEventListener('pointermove', (e) => {
      const p = pts.get(e.pointerId);
      if (!p) return;
      const dx = e.clientX - p.x, dy = e.clientY - p.y;
      p.x = e.clientX; p.y = e.clientY;
      if (pts.size === 1) { ox += dx; oy += dy; fixPos(); redraw(); return; }
      if (!pinch) return;
      const now = pinchNow(), r = stage.getBoundingClientRect();
      ox += now.x - pinch.x; oy += now.y - pinch.y;
      zoomTo(z * now.d / pinch.d, now.x - r.left, now.y - r.top);
      pinch = now;
    });
    const lift = (e) => { pts.delete(e.pointerId); pinch = pts.size === 2 ? pinchNow() : null; };
    stage.addEventListener('pointerup', lift);
    stage.addEventListener('pointercancel', lift);
    stage.addEventListener('wheel', (e) => {
      e.preventDefault();
      const r = stage.getBoundingClientRect();
      zoomTo(z * Math.exp(-e.deltaY * 0.0015), e.clientX - r.left, e.clientY - r.top);
    }, { passive: false });
    zoom.oninput = () => zoomTo(+zoom.value, S / 2, S / 2);
    stage.addEventListener('keydown', (e) => {
      const s = e.shiftKey ? 40 : 10;
      const mv = { ArrowLeft: [-s, 0], ArrowRight: [s, 0], ArrowUp: [0, -s], ArrowDown: [0, s] }[e.key];
      if (mv) { ox += mv[0]; oy += mv[1]; fixPos(); redraw(); }
      else if (e.key === '+' || e.key === '=') zoomTo(z * 1.1, S / 2, S / 2);
      else if (e.key === '-') zoomTo(z / 1.1, S / 2, S / 2);
      else return;
      e.preventDefault();
    });
    const close = () => {
      wrap.remove();
      st.cropEl = null;
      document.removeEventListener('keydown', onEsc, true);
      URL.revokeObjectURL(src.url);
    };
    const onEsc = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
    document.addEventListener('keydown', onEsc, true);
    st.cropClose = close;
    wrap.addEventListener('click', (e) => { if (e.target === wrap) close(); });
    wrap.querySelector('[data-no]').onclick = close;
    const yes = wrap.querySelector('[data-yes]');
    yes.onclick = async () => {
      yes.disabled = true;
      yes.textContent = 'Склеюю…';
      try {
        const side = S / k();
        const sx = clamp(-ox / k(), 0, src.w - side), sy = clamp(-oy / k(), 0, src.h - side);
        const out = clamp(Math.round(side), 64, PH_OUT);
        const blob = await encode(shrink(src, sx, sy, side, out));
        if (!blob) throw new Error('Не вдалось стиснути — спробуй іншу картинку');
        const r = await api(st, 'POST', '/api/games/sklei/pic', blob);
        close();
        st.ctx.toast(r.message || 'Картинка в колоді!', 'ok');
        st.deckOpen = true;
        loadDeck(st, true);
      } catch (e) {
        st.ctx.toast(e.message, 'err');
        yes.disabled = false;
        yes.textContent = 'Купити за ' + (d.price || 400) + ' 🏺';
      }
    };
    stage.focus();
  }

  // =========================================================================================
  // Лобі-демо: розбитий глек (своя розбивка, не з сервера)
  // =========================================================================================

  function demoCut(N) {
    const rnd = ((a) => () => { a = Math.imul(a ^ (a >>> 15), 2246822519) + 0x9e3779b9 | 0; return ((a >>> 0) % 10000) / 10000; })(20261006);
    const v = [];
    for (let j = 0; j <= N; j++) for (let i = 0; i <= N; i++) {
      const inner = i > 0 && i < N && j > 0 && j < N;
      v.push(i > 0 && i < N ? Math.round((rnd() - 0.5) * 30) : 0, j > 0 && j < N ? Math.round((rnd() - 0.5) * 30) : 0);
      if (!inner) { v[v.length - 2] = i > 0 && i < N ? v[v.length - 2] : 0; v[v.length - 1] = j > 0 && j < N ? v[v.length - 1] : 0; }
    }
    const e = () => { const a = []; for (let q = 0; q < (N - 1) * N * 2; q++) a.push(Math.round((rnd() - 0.5) * 24)); return a; };
    const tray = [];
    for (let k = 0; k < N * N; k++) tray.push(500, 500, 0);
    return { n: N, v, eh: e(), ev: e(), tray };
  }

  // =========================================================================================
  // Оновлення
  // =========================================================================================

  function render(root, ctx) {
    const st = state(root, ctx);
    shell(root, st);
    const v = ctx.view || {};
    const lobby = !ctx.room || ctx.room.status === 'lobby' || v.ph === 'lobby' || !v.cut;
    if (v.until && v.leftMs != null) st.skew = Date.parse(v.until) - v.leftMs - Date.now();
    syncClock(st, v.leftMs);
    // пристрій: телефон — не більше 16 черепків (з наступної картинки)
    sendDev(st);
    let fresh = false;
    st.demo = lobby;
    if (lobby) {
      const key = 'demo';
      if (st.cutKey !== key) {
        st.cutKey = key;
        st.cut = demoCut(4);
        st.n = 4;
        st.lay = null;
        st.pieces = [];
        const cut = st.cut;
        for (let k = 0; k < 16; k++) {
          const pts = outline(cut, k), r = Math.floor(k / 4), c = k % 4, cx = c * CELL + 50, cy = r * CELL + 50;
          st.pieces.push({ k, r, c, cx, cy, pts, path: pathOf(pts, cx, cy), world: pathOf(pts, 0, 0), fx: 0.5, fy: 0.5, zone: 'tray', x: cx, y: cy, r0: 0, taps: 0, ang: 0, placed: true, bmp: null, sh: null });
        }
        st.z = st.pieces.map((p) => p.k);
        wantPic(st, { key: 'demo', kind: 'svg', url: '/games/sklei/glek-syniy.svg' });
        fresh = true;
      }
    } else if (takeCut(st, v)) {
      fresh = true;
      wantPic(st, v.pic);
    } else wantPic(st, v.pic);
    // з сервера: що вже приросло (F5, повернення) — ставимо на місце без анімації
    if (!lobby && v.me && v.me.placed) {
      for (const k of v.me.placed) {
        const p = st.pieces[k];
        if (p && !p.placed) { p.placed = true; p.x = p.cx; p.y = p.cy; st.anims = st.anims.filter((a) => a.p !== p); if (st.drag && st.drag.p === p) st.drag = null; st.layerOk = false; }
      }
    }
    const relaid = layout(st);
    if (fresh && !lobby) {
      for (const p of st.pieces) if (!p.placed) placeInTray(st, p);
      // «Розбилось»: на початку складання черепки летять з рамки на рушник, кожен зі своїм поворотом
      if (v.ph === 'go' && st.ph === 'ready') shatter(st);
    }
    if (st.ph !== v.ph) {
      if (v.ph === 'go' && st.ph === 'ready' && !fresh) shatter(st);
      st.ph = v.ph;
      st.layerOk = false;
    }
    if (v.me && v.me.done != null && !st.doneFx && v.ph === 'go') {
      st.doneFx = true;
      for (const p of st.pieces) p.placed = true;
      st.fx.push({ kind: 'done', t0: performance.now(), dur: 1100 });
    }
    if (relaid || fresh || true) st.layerOk = false;    // вид міг змінити hint/фазу/прирослі — шар дешевий
    st.top.innerHTML = lobby ? '🏺 <b>Склей глек</b> <span class="muted">· ' + levelName(v.level) + '</span>' : picLine(v);
    st.clock.innerHTML = clockText(st);
    renderRace(st);
    overlay(st);
    st.tip.textContent = tipText(st);
    st.cv.classList.toggle('play', canPlay(st));
    renderMore(st);
    if (!st.timer) st.timer = setInterval(() => {
      if (!live.has(st) || !st.el.isConnected) return;
      st.clock.innerHTML = clockText(st);
      const tip = tipText(st);
      if (tip !== st.tip.textContent) st.tip.textContent = tip;
      sendDev(st);
      // місце над полем могло змінитись без resize (шапка картки, перегони в два рядки, шторка) — перерахуй
      if (!st.drag && layout(st)) { st.layerOk = false; kick(st); }
    }, 250);
    if (!st.ro && window.ResizeObserver) {
      st.ro = new ResizeObserver(() => { if (live.has(st) && layout(st)) { st.layerOk = false; kick(st); } });
      st.ro.observe(st.stage);
    }
    if (HGames.ui.onFit) HGames.ui.onFit(root, () => { if (live.has(st) && layout(st)) { st.layerOk = false; kick(st); } });
    kick(st);
  }

  /// Пристрій: телефон — не більше 16 черепків (сервер бере з наступної картинки). Шлемо, лише коли змінився.
  function sendDev(st) {
    const ctx = st.ctx, v = ctx.view;
    if (!ctx.mine || !v || !ctx.room || ctx.room.status === 'finished') return;
    const ph = coarse();
    if (v.phone !== ph && st.devSent !== ph) { st.devSent = ph; ctx.act('dev', { phone: ph }); }
  }

  function levelName(l) { return { easy: 'легко · 9 черепків', normal: 'звично · 16', hard: 'важко · 25 без підказки', master: 'майстер · 36' }[l] || ''; }

  function shatter(st) {
    const t0 = performance.now();
    const order = st.pieces.filter((p) => !p.placed);
    order.forEach((p, i) => {
      const x1 = p.x, y1 = p.y;
      p.x = p.cx; p.y = p.cy; p.ang = 0;
      st.anims.push({ p, x0: p.cx, y0: p.cy, x1, y1, a0: 0, a1: p.r0 + p.taps, t0: t0 + (i * 211 % order.length) * (260 / order.length), dur: SHATTER_MS });
    });
  }

  function statusText(ctx) {
    const v = ctx.view || {};
    if (!ctx.room || ctx.room.status !== 'playing') return '';
    if (v.ph === 'ready') return '🏺 Запам’ятовуй картинку — зараз розіб’ється';
    if (v.ph === 'go') {
      if (!v.me) return '👀 Дивишся, хто швидше склеїть';
      if (v.me.done != null) return '✅ Склав за ' + fmt(v.me.done) + (v.party ? '' : ' — чекаємо інших');
      const st = stOf(ctx);
      const n = st ? st.pieces.filter((p) => p.placed).length : (v.me.placed || []).length;
      const of = st && st.pieces.length ? st.pieces.length : (v.n || 4) ** 2;
      return 'Склей картинку: ' + n + ' з ' + of + ' черепків';
    }
    if (v.ph === 'pause') return 'Картинку склеєно — далі наступна';
    return '';
  }

  HGames.register({
    id: 'sklei',
    added: '2026-10-06',
    icon: ICON,
    pad: {
      dirs: true, a: 'Space', x: 'KeyX',
      hint: '{dpad} рука · {a} взяти/покласти · {x} поворот · {rb} до наступного черепка',
      on(btn, ctx) {
        const st = stOf(ctx);
        if (!st || !canPlay(st)) return false;
        if (btn === 'rb' || btn === 'lb') { padJump(st, btn === 'rb' ? 1 : -1); return true; }
        return false;
      },
      when(ctx) { const st = stOf(ctx); return !!st && canPlay(st); },
    },

    mount(root, ctx) { render(root, ctx); },
    update(root, ctx) { render(root, ctx); },

    frame(root, ctx, f) {
      const st = root._sklei;
      if (!st || !f) return;
      st.frameP = f;
      if (f.leftMs != null) syncClock(st, f.leftMs);
      renderRace(st);
    },

    onKey(e, ctx) {
      const st = stOf(ctx);
      if (!st || !canPlay(st)) return false;
      const arrows = ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'];
      if (arrows.includes(e.code)) {
        held[e.code] = true;
        padStart(st);
        st.tip.textContent = tipText(st);
        kick(st);
        return true;
      }
      if (e.code === 'Space') { if (!e.repeat) { padGrab(st); kick(st); } return true; }
      if (e.code === 'KeyX' || e.code === 'KeyR') {
        if (e.repeat) return true;
        padStart(st);
        rotate(st, st.drag ? st.drag.p : hit(st, st.cur.x, st.cur.y) || nearest(st, st.cur.x, st.cur.y, 60));
        return true;
      }
      if (e.code === 'Tab' && st.padOn) { padJump(st, e.shiftKey ? -1 : 1); return true; }
      return false;
    },

    status: statusText,

    unmount(root) {
      const st = root._sklei;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      clearInterval(st.timer);
      st.timer = 0;
      if (st.ro) st.ro.disconnect();
      if (HGames.ui.onFit) HGames.ui.onFit(root, null);
      if (st.cropClose) st.cropClose();
      live.delete(st);
      st.pieces = [];
      st.layer = null;
      st.picCv = null;
      st.src = null;
      root._sklei = null;
    },
  });
})();
