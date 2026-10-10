/*
  Кавуни на ярмарку — соло-crash з ножем (Азарт → 📈 Швидкі). Дядько Глек підкидає з воза овочі, ти ріжеш: кожен
  розрізаний додає свій приріст до множника; гнилий гарбуз — кінець. Правила, послідовність і гроші — на сервері
  (Impl/Kavuny.cs, KavunyCore.cs, docs/games/specs/kavuny.md); тут сцена, різання й наміри.

  Вид (room): { phase: 'idle'|'fly', now, on, hash, limits: { min, max }, cap, wallet, note,
    round: null | { no, stake, auto, autocut, t0, m, win, cap, cuts, tier, strip: [inc], fruits: [{ id, k, inc, at, x0, x1, h, spin, cut }] },
    last: null | { no, hash, seed, stake, m, win, why, cap, potential, codes, seen, cuts, t0, endAt, rot },
    history: [{ x, win, why }], best: { x, win }, k: { lead, wave, stagger, fly, lag, autoCut } }
  Наміри: act('start', { amount, auto, autocut }), input('cut', { ids }), act('cash'), act('auto', { x }), act('autocut', { on }).

  Овочі летять за годинником сервера (зсув — з `now` у виді); сервер показує лише ті, що вже вилетіли. Розрізане
  малюємо одразу (половинки, сік), номер іде пачкою не частіше раз на 80 мс; множник — з виду сервера плюс
  ще не підтверджене. rAF — лише поки є що малювати. Звуку нема.
*/
(() => {
  'use strict';

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="8" cy="9" r="6.2" fill="var(--ok)"/>'
    + '<path d="M3 5.6c1.4 1.6 1.4 5.6 0 7.4M8 2.8c1.2 2 1.2 10.4 0 12.4M13 5.6c-1.4 1.6-1.4 5.6 0 7.4" fill="none" stroke="#1d4a26" stroke-width="1.1"/>'
    + '<path d="M2.2 3.2l11.6 9.6" stroke="var(--accent)" stroke-width="1.6" stroke-linecap="round"/></svg>';

  // Таблиця — як у KavunyCore.Kinds (код, назва, приріст ×1, вага); r — радіус у частках поля, flesh — колір м'якуша.
  const KINDS = {
    s: { name: 'слива', inc: 2, w: 230, r: 0.056, flesh: '#f2c14a', juice: '#7b2a8c' },
    b: { name: 'буряк', inc: 3, w: 170, r: 0.06, flesh: '#c2185b', juice: '#a0103e' },
    a: { name: 'яблуко', inc: 4, w: 200, r: 0.062, flesh: '#fff1c8', juice: '#ffe9a8' },
    g: { name: 'груша', inc: 5, w: 150, r: 0.064, flesh: '#fbf3c4', juice: '#e8eb9a' },
    d: { name: 'диня', inc: 8, w: 110, r: 0.078, flesh: '#ffe98a', juice: '#ffd54a' },
    p: { name: 'гарбуз', inc: 15, w: 80, r: 0.085, flesh: '#ffa23a', juice: '#ff8a1a' },
    k: { name: 'кавун', inc: 25, w: 50, r: 0.095, flesh: '#ff3d55', juice: '#ff2a48' },
    K: { name: 'Глеків кавун', inc: 100, w: 10, r: 0.108, flesh: '#ff3d55', juice: '#ff2a48' },
    x: { name: 'гнилий гарбуз', inc: 0, w: 0, r: 0.085, flesh: '#4a3a1a', juice: '#3e4a1a' },
  };
  const ORDER = 'sbagdpkK';
  const QUICK = [10, 50, 100, 500];
  const AUTOQ = [1.2, 1.5, 2, 5];
  const DEF_K = { lead: 1200, wave: 850, stagger: 150, fly: 2400, lag: 700, autoCut: 100 };
  const SAY = {
    idle: ['Став черепки — і до воза!', 'Свіженькі з городу! Хто різатиме?', 'Серп нагострив? Поїхали!'],
    start: ['Ану лови!', 'Тримай серп міцніше!', 'Перший пішов!', 'Не проґав гнилячка!'],
    kavun: ['Кавунчик — як мед!', 'Оце кавун! Золотий!', 'З мого городу — найкращий!'],
    glek: ['Мій кавун! ×2 — ріж!', 'Глеків кавун! Бережи, як глек!'],
    tier: ['Ярмарок розгулявся — овочі важчі!', 'Бери більший мішок!', 'Віз аж скрипить — далі важче!'],
    cash: ['Розумно! Черепки в кишені', 'Забрав — і молодець', 'Ну й хитрун!', 'Ото ярмарок удався!'],
    rot: ['А гнилий-то я й не помітив!', 'Ой, гнилячок проскочив… Хе-хе!', 'Фу-у! Гнилий гарбуз — усе пропало', 'Хто ж знав, що він гнилий!'],
    empty: ['Віз порожній! Забирай усе', 'Ти вигріб увесь ярмарок!'],
    void: ['Сайт перезапускався — раунд не рахується'],
  };
  const pick = (a) => a[Math.floor(Math.random() * a.length)];

  // ---------------------------------------------------------------------------------------------
  // Дрібниці
  // ---------------------------------------------------------------------------------------------
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const tms = (x) => (typeof x === 'number' ? x : x ? Date.parse(x) : NaN);
  const fmtX = (m) => (Math.floor(m * 100 + 1e-6) / 100).toFixed(2).replace('.', ',');
  const fmtN = (n) => Math.round(n).toString().replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  const incX = (inc) => '×' + fmtX(1 + inc / 100);
  const parseX = (s) => { const x = parseFloat(String(s).replace(',', '.').replace(/[^\d.]/g, '')); return isFinite(x) ? x : NaN; };
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const store = {
    get(k, d) { try { const v = localStorage.getItem('kavuny_' + k); return v == null ? d : JSON.parse(v); } catch (e) { return d; } },
    set(k, v) { try { localStorage.setItem('kavuny_' + k, JSON.stringify(v)); } catch (e) { /* приватне вікно */ } },
  };

  // ---------------------------------------------------------------------------------------------
  // Чесність: та сама формула, що KavunyCore.Generate (spec §3.3), на BigInt
  // ---------------------------------------------------------------------------------------------
  async function sha256(s) {
    const buf = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(s));
    return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
  }
  async function kavunyCheck(seed, hash, cap) {
    const W = [230, 170, 200, 150, 110, 80, 50, 10], I = [2, 3, 4, 5, 8, 15, 25, 100];
    const T32 = 1n << 32n;
    let p = 100, codes = '';
    for (let k = 1; k <= 5000; k++) {
      const h = await sha256(seed + ':' + k);
      const roll = BigInt('0x' + h.slice(0, 8)), kind = BigInt('0x' + h.slice(8, 16)), edge = BigInt('0x' + h.slice(16, 24));
      const f = p < 300 ? 1 : p < 1000 ? 2 : p < 3000 ? 5 : 10, P = BigInt(p);
      if ((k === 1 && edge * 100n < 3n * T32) || roll * (1000n * P + BigInt(f * 6850)) >= P * 1000n * T32) { codes += 'x'; break; }
      let pk = Number((kind * 1000n) >> 32n), i = 0;
      while (i < 7 && pk >= W[i]) pk -= W[i++];
      codes += ORDER[i]; p += f * I[i];
      if (p >= cap) break;
    }
    return { ok: (await sha256(seed)) === hash, codes, potential: p };
  }

  // ---------------------------------------------------------------------------------------------
  // Малювання овочів (центр 0,0; радіус r)
  // ---------------------------------------------------------------------------------------------
  function radial(g, r, stops, fx, fy) {
    const gr = g.createRadialGradient(r * (fx == null ? -0.35 : fx), r * (fy == null ? -0.4 : fy), r * 0.1, 0, 0, r * 1.1);
    stops.forEach((s, i) => gr.addColorStop(i / (stops.length - 1), s));
    return gr;
  }
  function stem(g, r, color, len, bend) {
    g.strokeStyle = color; g.lineWidth = Math.max(2, r * 0.12); g.lineCap = 'round';
    g.beginPath(); g.moveTo(0, -r * 0.82); g.quadraticCurveTo(r * (bend || 0.15), -r * (1.0 + len * 0.5), r * (bend || 0.15) * 1.6, -r * (1 + len)); g.stroke();
  }
  function leaf(g, x, y, w, h, rot, color) {
    g.save(); g.translate(x, y); g.rotate(rot);
    g.fillStyle = color; g.beginPath(); g.ellipse(0, 0, w, h, 0, 0, Math.PI * 2); g.fill();
    g.strokeStyle = 'rgba(0,0,0,.25)'; g.lineWidth = 1; g.beginPath(); g.moveTo(-w * 0.9, 0); g.lineTo(w * 0.9, 0); g.stroke();
    g.restore();
  }
  function shine(g, r, a) {
    g.fillStyle = 'rgba(255,255,255,' + (a || 0.35) + ')';
    g.beginPath(); g.ellipse(-r * 0.38, -r * 0.42, r * 0.22, r * 0.13, -0.7, 0, Math.PI * 2); g.fill();
  }
  function outline(g, r) { g.lineWidth = Math.max(1.5, r * 0.06); g.strokeStyle = 'rgba(25,12,6,.55)'; }

  const DRAW = {
    s(g, r) {   // слива
      g.fillStyle = radial(g, r, ['#c79ae6', '#7a3aa8', '#3d1458']); outline(g, r);
      g.beginPath(); g.ellipse(0, 0, r * 0.88, r, 0, 0, Math.PI * 2); g.fill(); g.stroke();
      g.strokeStyle = 'rgba(40,10,60,.45)'; g.lineWidth = r * 0.07; g.beginPath(); g.moveTo(r * 0.1, -r * 0.9); g.quadraticCurveTo(r * 0.45, 0, r * 0.1, r * 0.92); g.stroke();
      g.fillStyle = 'rgba(220,210,255,.18)'; g.beginPath(); g.ellipse(-r * 0.2, -r * 0.1, r * 0.55, r * 0.75, 0, 0, Math.PI * 2); g.fill();
      stem(g, r, '#5a3a1a', 0.3, 0.1); shine(g, r, 0.3);
    },
    b(g, r) {   // буряк з бадиллям
      leaf(g, -r * 0.25, -r * 1.15, r * 0.22, r * 0.5, -0.35, '#3f8a3a');
      leaf(g, r * 0.25, -r * 1.2, r * 0.22, r * 0.55, 0.3, '#4f9a44');
      g.strokeStyle = '#b0204a'; g.lineWidth = r * 0.1; g.beginPath(); g.moveTo(0, -r * 0.8); g.lineTo(-r * 0.2, -r * 1.1); g.moveTo(0, -r * 0.8); g.lineTo(r * 0.2, -r * 1.12); g.stroke();
      g.fillStyle = radial(g, r, ['#e0507a', '#9a1238', '#4d0518']); outline(g, r);
      g.beginPath(); g.moveTo(0, -r * 0.85);
      g.bezierCurveTo(r * 1.05, -r * 0.85, r * 1.0, r * 0.55, r * 0.1, r * 0.95);
      g.lineTo(0, r * 1.35); g.lineTo(-r * 0.1, r * 0.95);
      g.bezierCurveTo(-r * 1.0, r * 0.55, -r * 1.05, -r * 0.85, 0, -r * 0.85); g.fill(); g.stroke();
      shine(g, r, 0.25);
    },
    a(g, r) {   // яблуко
      g.fillStyle = radial(g, r, ['#ff9a7a', '#e2261e', '#8a0e10']); outline(g, r);
      g.beginPath(); g.moveTo(0, -r * 0.7);
      g.bezierCurveTo(r * 0.6, -r * 1.15, r * 1.25, -r * 0.4, r * 0.9, r * 0.45);
      g.bezierCurveTo(r * 0.7, r * 1.0, r * 0.2, r * 1.05, 0, r * 0.9);
      g.bezierCurveTo(-r * 0.2, r * 1.05, -r * 0.7, r * 1.0, -r * 0.9, r * 0.45);
      g.bezierCurveTo(-r * 1.25, -r * 0.4, -r * 0.6, -r * 1.15, 0, -r * 0.7); g.fill(); g.stroke();
      stem(g, r, '#5a3a1a', 0.35, 0.12); leaf(g, r * 0.32, -r * 1.05, r * 0.3, r * 0.14, -0.5, '#4caf50'); shine(g, r, 0.4);
    },
    g(g, r) {   // груша
      g.fillStyle = radial(g, r, ['#f6f7b0', '#c2d24a', '#7a8a1a'], -0.3, 0); outline(g, r);
      g.beginPath(); g.moveTo(0, -r * 1.0);
      g.bezierCurveTo(r * 0.45, -r * 1.0, r * 0.45, -r * 0.3, r * 0.8, r * 0.2);
      g.bezierCurveTo(r * 1.1, r * 0.7, r * 0.6, r * 1.05, 0, r * 1.02);
      g.bezierCurveTo(-r * 0.6, r * 1.05, -r * 1.1, r * 0.7, -r * 0.8, r * 0.2);
      g.bezierCurveTo(-r * 0.45, -r * 0.3, -r * 0.45, -r * 1.0, 0, -r * 1.0); g.fill(); g.stroke();
      g.fillStyle = 'rgba(200,120,40,.22)'; g.beginPath(); g.ellipse(r * 0.35, r * 0.4, r * 0.35, r * 0.3, 0, 0, Math.PI * 2); g.fill();
      g.save(); g.translate(0, -r * 0.2); stem(g, r, '#5a3a1a', 0.3, 0.2); g.restore(); shine(g, r, 0.3);
    },
    d(g, r) {   // диня: жовтий овал з сіточкою
      g.fillStyle = radial(g, r, ['#fff3a8', '#f0c23a', '#b07a10']); outline(g, r);
      g.beginPath(); g.ellipse(0, 0, r * 1.18, r * 0.8, 0, 0, Math.PI * 2); g.fill(); g.stroke();
      g.save(); g.beginPath(); g.ellipse(0, 0, r * 1.15, r * 0.77, 0, 0, Math.PI * 2); g.clip();
      g.strokeStyle = 'rgba(255,250,220,.55)'; g.lineWidth = Math.max(1, r * 0.04);
      for (let i = -4; i <= 4; i++) {
        g.beginPath(); g.moveTo(i * r * 0.32 - r * 0.6, -r); g.lineTo(i * r * 0.32 + r * 0.6, r); g.stroke();
        g.beginPath(); g.moveTo(i * r * 0.32 + r * 0.6, -r); g.lineTo(i * r * 0.32 - r * 0.6, r); g.stroke();
      }
      g.restore();
      g.fillStyle = '#6a4a1a'; g.beginPath(); g.arc(-r * 1.15, 0, r * 0.08, 0, Math.PI * 2); g.fill();
      shine(g, r, 0.3);
    },
    p(g, r) { pumpkin(g, r, ['#ffd08a', '#f58a1e', '#a8460a'], '#3f6a1a', false); },
    x(g, r, t) {   // гнилий: темний, у плямах, з мухами
      pumpkin(g, r, ['#7a6a4a', '#4a3e26', '#1e170c'], '#2a2a14', true);
      g.fillStyle = 'rgba(70,90,30,.75)';
      [[-0.35, -0.1, 0.22], [0.3, 0.3, 0.18], [0.1, -0.45, 0.12], [-0.15, 0.45, 0.14]].forEach(([x, y, s]) => { g.beginPath(); g.ellipse(x * r, y * r, s * r, s * r * 0.7, x, 0, Math.PI * 2); g.fill(); });
      g.fillStyle = '#111';
      for (let i = 0; i < 4; i++) {
        const a = (t || 0) * (3 + i) + i * 1.7, rr = r * (1.25 + 0.15 * Math.sin(t * 5 + i));
        g.beginPath(); g.arc(Math.cos(a) * rr, Math.sin(a) * rr * 0.7 - r * 0.4, Math.max(1.6, r * 0.05), 0, Math.PI * 2); g.fill();
      }
    },
    k(g, r, t) { kavun(g, r, t, false); },
    K(g, r, t) { kavun(g, r, t, true); },
  };
  function pumpkin(g, r, cols, stemCol, rotten) {
    outline(g, r);
    const ribs = [[-0.62, 0.5], [0.62, 0.5], [-0.32, 0.62], [0.32, 0.62], [0, 0.66]];
    ribs.forEach(([x, w]) => {
      g.fillStyle = radial(g, r, cols, x * 0.5 - 0.2, -0.3);
      g.beginPath(); g.ellipse(x * r, 0, w * r, r * 0.86, 0, 0, Math.PI * 2); g.fill(); g.stroke();
    });
    g.fillStyle = stemCol; g.beginPath();
    g.moveTo(-r * 0.1, -r * 0.75); g.lineTo(-r * 0.06, -r * 1.12); g.lineTo(r * 0.16, -r * 1.16); g.lineTo(r * 0.12, -r * 0.75); g.fill();
    if (!rotten) shine(g, r, 0.28);
  }
  function kavun(g, r, t, glek) {
    // золотистий блиск (рідкісний овоч)
    const a = 0.25 + 0.2 * Math.sin((t || 0) * 6);
    const gl = g.createRadialGradient(0, 0, r * 0.8, 0, 0, r * 1.5);
    gl.addColorStop(0, 'rgba(255,215,90,' + (glek ? a + 0.25 : a) + ')'); gl.addColorStop(1, 'rgba(255,215,90,0)');
    g.fillStyle = gl; g.beginPath(); g.arc(0, 0, r * 1.5, 0, Math.PI * 2); g.fill();
    g.fillStyle = radial(g, r, ['#9ee07a', '#3a9a3a', '#14501c']); outline(g, r);
    g.beginPath(); g.ellipse(0, 0, r * 1.05, r * 0.95, 0, 0, Math.PI * 2); g.fill();
    g.save(); g.clip();
    g.strokeStyle = '#123f16'; g.lineWidth = r * 0.16; g.lineJoin = 'round';
    for (let i = -2; i <= 2; i++) {
      g.beginPath();
      for (let j = 0; j <= 8; j++) {
        const y = -r + j * r * 0.25, x = i * r * 0.42 + (j % 2 ? r * 0.08 : -r * 0.08) * (1 - Math.abs(i) * 0.2);
        if (j) g.lineTo(x, y); else g.moveTo(x, y);
      }
      g.stroke();
    }
    if (glek) {   // вишитий пояс Глека
      g.fillStyle = '#fff6e0'; g.fillRect(-r * 1.1, -r * 0.16, r * 2.2, r * 0.32);
      g.strokeStyle = '#d4122a'; g.lineWidth = Math.max(1.2, r * 0.05);
      for (let x = -r * 1.05; x < r * 1.05; x += r * 0.18) {
        g.beginPath(); g.moveTo(x, -r * 0.1); g.lineTo(x + r * 0.12, r * 0.1); g.moveTo(x + r * 0.12, -r * 0.1); g.lineTo(x, r * 0.1); g.stroke();
      }
    }
    g.restore();
    g.beginPath(); g.ellipse(0, 0, r * 1.05, r * 0.95, 0, 0, Math.PI * 2); g.stroke();
    stem(g, r, '#5a3a1a', 0.18, 0.2); shine(g, r, 0.35);
    if (glek) {
      g.font = '900 ' + Math.round(r * 0.62) + 'px ' + 'Onest, system-ui, sans-serif';
      g.textAlign = 'center'; g.textBaseline = 'middle'; g.lineWidth = r * 0.12; g.strokeStyle = '#24142e'; g.fillStyle = '#ffe08a';
      g.strokeText('×2', 0, r * 0.55); g.fillText('×2', 0, r * 0.55);
    }
  }
  function drawFruit(g, code, r, t) { (DRAW[code] || DRAW.a)(g, r, t); }

  /// Зріз половинки: м'якуш еліпсом уздовж лінії розрізу.
  function face(g, code, r) {
    const K = KINDS[code] || KINDS.a;
    g.fillStyle = K.flesh;
    g.beginPath(); g.ellipse(0, 0, r * 0.92, r * 0.34, 0, 0, Math.PI * 2); g.fill();
    if (code === 'k' || code === 'K') {
      g.fillStyle = '#1a1010';
      for (let i = -3; i <= 3; i++) { g.beginPath(); g.ellipse(i * r * 0.22, (i % 2 ? 1 : -1) * r * 0.08, r * 0.04, r * 0.07, 0.4, 0, Math.PI * 2); g.fill(); }
    } else if (code === 'p' || code === 'd') {
      g.fillStyle = 'rgba(255,250,220,.85)';
      for (let i = -2; i <= 2; i++) { g.beginPath(); g.ellipse(i * r * 0.25, 0, r * 0.06, r * 0.03, 0, 0, Math.PI * 2); g.fill(); }
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Тло: дерев'яний прилавок, рушник із вишивкою, віз
  // ---------------------------------------------------------------------------------------------
  function paintBack(st) {
    const { W, H, dpr } = st.g;
    const c = st.back || (st.back = document.createElement('canvas'));
    c.width = Math.round(W * dpr); c.height = Math.round(H * dpr);
    const g = c.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    const bg = g.createLinearGradient(0, 0, 0, H);
    bg.addColorStop(0, '#5a3418'); bg.addColorStop(0.6, '#45260f'); bg.addColorStop(1, '#2a1607');
    g.fillStyle = bg; g.fillRect(0, 0, W, H);
    // дошки
    const n = Math.max(5, Math.round(W / 70));
    for (let i = 0; i <= n; i++) {
      const x = (i / n) * W;
      g.fillStyle = i % 2 ? 'rgba(255,220,170,.035)' : 'rgba(0,0,0,.06)'; g.fillRect(x, 0, W / n, H);
      g.strokeStyle = 'rgba(20,8,2,.55)'; g.lineWidth = 2; g.beginPath(); g.moveTo(x, 0); g.lineTo(x, H); g.stroke();
      g.strokeStyle = 'rgba(255,220,170,.08)'; g.lineWidth = 1;
      for (let k = 0; k < 3; k++) {
        const y0 = ((i * 97 + k * 211) % 100) / 100 * H;
        g.beginPath(); g.moveTo(x + 8, y0); g.bezierCurveTo(x + W / n * 0.4, y0 + 20, x + W / n * 0.6, y0 - 16, x + W / n - 8, y0 + 6); g.stroke();
      }
    }
    // рушник угорі: біле полотно, червоно-чорна вишивка
    const top = Math.round(Math.min(18, H * 0.05)), band = Math.round(clamp(H * 0.075, 16, 30));
    g.fillStyle = '#f4ead2'; g.fillRect(0, top, W, band);
    g.fillStyle = 'rgba(0,0,0,.18)'; g.fillRect(0, top + band, W, 3);
    const s = band / 6;
    for (let x = 0, i = 0; x < W + s * 6; x += s * 6, i++) {
      for (let dy = 0; dy < 5; dy++) {
        const w = [0, 1, 2, 1, 0][dy];
        for (let dx = -w; dx <= w; dx++) {
          const cx = x + s * 3 + dx * s, cy = top + s * 0.5 + dy * s;
          g.strokeStyle = (Math.abs(dx) === w) ? '#c4122a' : (i % 2 ? '#1a0e0a' : '#c4122a');
          g.lineWidth = Math.max(1, s * 0.3);
          g.beginPath(); g.moveTo(cx - s * 0.32, cy - s * 0.32 + s * 0.5); g.lineTo(cx + s * 0.32, cy + s * 0.32 + s * 0.5);
          g.moveTo(cx + s * 0.32, cy - s * 0.32 + s * 0.5); g.lineTo(cx - s * 0.32, cy + s * 0.32 + s * 0.5); g.stroke();
        }
      }
    }
    // прилавок унизу
    const cy = H - Math.round(clamp(H * 0.07, 14, 34));
    const cg = g.createLinearGradient(0, cy, 0, H);
    cg.addColorStop(0, '#8a5a2a'); cg.addColorStop(0.25, '#6a3e18'); cg.addColorStop(1, '#2e1808');
    g.fillStyle = cg; g.fillRect(0, cy, W, H - cy);
    g.fillStyle = 'rgba(255,230,180,.25)'; g.fillRect(0, cy, W, 2);
    // віз ліворуч унизу (Глек — картинкою поверх)
    const u = st.g.u, wx = u * 0.2, wy = H - u * 0.03;
    g.fillStyle = '#7a4a1e'; g.strokeStyle = '#24142e'; g.lineWidth = 3;
    g.beginPath(); g.moveTo(wx - u * 0.18, wy - u * 0.2); g.lineTo(wx + u * 0.2, wy - u * 0.2); g.lineTo(wx + u * 0.16, wy - u * 0.08); g.lineTo(wx - u * 0.15, wy - u * 0.08); g.closePath(); g.fill(); g.stroke();
    g.fillStyle = '#4caf50'; g.beginPath(); g.arc(wx - u * 0.06, wy - u * 0.22, u * 0.05, Math.PI, 0); g.fill();
    g.fillStyle = '#f58a1e'; g.beginPath(); g.arc(wx + u * 0.06, wy - u * 0.22, u * 0.045, Math.PI, 0); g.fill();
    [wx - u * 0.1, wx + u * 0.1].forEach((x) => {
      g.fillStyle = '#5a3414'; g.beginPath(); g.arc(x, wy - u * 0.06, u * 0.055, 0, Math.PI * 2); g.fill(); g.stroke();
      g.strokeStyle = '#c99a5a'; g.lineWidth = 2; g.beginPath(); g.moveTo(x - u * 0.05, wy - u * 0.06); g.lineTo(x + u * 0.05, wy - u * 0.06); g.moveTo(x, wy - u * 0.11); g.lineTo(x, wy - u * 0.01); g.stroke();
      g.strokeStyle = '#24142e'; g.lineWidth = 3;
    });
    const gk = st.el.glek;
    if (gk) { gk.style.width = Math.round(u * 0.26) + 'px'; gk.style.left = Math.round(wx - u * 0.13) + 'px'; gk.style.bottom = Math.round(u * 0.2) + 'px'; }
  }

  // ---------------------------------------------------------------------------------------------
  // Каркас сторінки
  // ---------------------------------------------------------------------------------------------
  function skeleton(root, st) {
    st.amount = store.get('amt', 50); st.autoOn = store.get('aon', false); st.autoX = store.get('ax', 2); st.knife = store.get('knife', false);
    root.innerHTML = '<div class="kv">'
      + '<div class="kv-stage"><canvas class="kv-cv"></canvas>'
      + '<img class="kv-glek" src="/static/glek.svg" alt="" draggable="false">'
      + '<div class="kv-ov">'
      + '<div class="kv-top"><div class="kv-strip"></div><div class="kv-tot"><span>разом</span><b class="kv-m">×1,00</b></div></div>'
      + '<div class="kv-side"><span class="kv-wal"></span><span class="kv-win"></span></div>'
      + '<button type="button" class="kv-ib" title="Правила й чесність" data-pad-skip>ⓘ</button>'
      + '<div class="kv-say"></div><div class="kv-big"></div><div class="kv-fx"></div></div></div>'
      + '<div class="kv-panel">'
      + '<div class="kv-box kv-amt"><div class="kv-lab">ставка, 🏺</div><div class="kv-row"><button type="button" class="kv-pm" data-a="minus">−</button>'
      + '<input class="kv-in" inputmode="numeric" autocomplete="off" aria-label="Сума ставки"><button type="button" class="kv-pm" data-a="plus">+</button></div>'
      + '<div class="kv-q">' + QUICK.map((q) => '<button type="button" data-q="' + q + '">' + q + '</button>').join('')
      + '<button type="button" data-a="dbl">×2</button></div></div>'
      + '<button type="button" class="kv-go" data-pad-first><span class="kv-gt"></span><small class="kv-gs"></small></button>'
      + '<div class="kv-box kv-opt">'
      + '<label class="kv-sw"><input type="checkbox" class="kv-aon"> <span>автозабір на</span></label>'
      + '<div class="kv-row"><span class="kv-x">×</span><input class="kv-ax" inputmode="decimal" autocomplete="off" aria-label="Автозабір на множнику"></div>'
      + '<div class="kv-q">' + AUTOQ.map((q) => '<button type="button" data-x="' + q + '">×' + String(q).replace('.', ',') + '</button>').join('') + '</div>'
      + '<label class="kv-sw kv-knife"><input type="checkbox" class="kv-cut"> <span>🔪 авторізання</span></label></div>'
      + '<div class="kv-foot"><span class="kv-hist"></span><button type="button" class="kv-hash" title="Перевірка чесності" data-pad-skip></button></div>'
      + '</div><div class="kv-info" hidden></div></div>';
    const q = (s) => root.querySelector(s);
    st.el = {
      box: q('.kv'), stage: q('.kv-stage'), cv: q('.kv-cv'), glek: q('.kv-glek'), strip: q('.kv-strip'), m: q('.kv-m'), wal: q('.kv-wal'),
      win: q('.kv-win'), say: q('.kv-say'), big: q('.kv-big'), fx: q('.kv-fx'), amt: q('.kv-in'), go: q('.kv-go'), gt: q('.kv-gt'),
      gs: q('.kv-gs'), aon: q('.kv-aon'), ax: q('.kv-ax'), cut: q('.kv-cut'), hist: q('.kv-hist'), hash: q('.kv-hash'), info: q('.kv-info'),
      ib: q('.kv-ib'),
    };
    st.el.amt.value = st.amount; st.el.ax.value = fmtX(st.autoX); st.el.aon.checked = st.autoOn; st.el.cut.checked = st.knife;
    wire(root, st);
  }

  function wire(root, st) {
    const el = st.el;
    const on = (n, ev, fn, o) => { n.addEventListener(ev, fn, o); st.offs.push(() => n.removeEventListener(ev, fn, o)); };
    on(el.go, 'click', () => { el.go.blur(); primary(st, true); });   // без фокуса: пробіл — ніж, а не «клік» по кнопці
    on(root.querySelector('.kv-amt'), 'click', (e) => {
      const b = e.target.closest('button'); if (!b) return;
      let a = st.amount;
      if (b.dataset.q) a = +b.dataset.q;
      else if (b.dataset.a === 'dbl') a = a * 2;
      else if (b.dataset.a === 'plus') a = a + (a < 100 ? 10 : a < 1000 ? 50 : 100);
      else if (b.dataset.a === 'minus') a = a - (a <= 100 ? 10 : a <= 1000 ? 50 : 100);
      setAmount(st, a);
    });
    on(el.amt, 'change', () => setAmount(st, parseInt(el.amt.value.replace(/\D/g, ''), 10) || 0));
    on(el.amt, 'keydown', (e) => { if (e.key === 'Enter') el.amt.blur(); });
    on(root.querySelector('.kv-opt'), 'click', (e) => {
      const b = e.target.closest('button[data-x]'); if (!b) return;
      setAuto(st, +b.dataset.x, true);
    });
    on(el.ax, 'change', () => setAuto(st, parseX(el.ax.value), st.autoOn));
    on(el.ax, 'keydown', (e) => { if (e.key === 'Enter') el.ax.blur(); });
    on(el.aon, 'change', () => setAuto(st, st.autoX, el.aon.checked));
    on(el.cut, 'change', () => {
      st.knife = el.cut.checked; store.set('knife', st.knife);
      const v = cur(st);
      if (v && v.phase === 'fly' && v.round && !!v.round.autocut !== st.knife) st.ctx.act('autocut', { on: st.knife });
    });
    on(el.ib, 'click', () => info(st, true));
    on(el.hash, 'click', () => info(st, true));
    on(el.info, 'click', (e) => {
      if (e.target === el.info || e.target.closest('.kv-x0')) info(st, false);
      else if (e.target.closest('.kv-chk')) check(st);
    });
    // різання: миша (з натиснутою кнопкою), палець, перо
    const cv = el.cv;
    on(cv, 'pointerdown', (e) => {
      if (e.button != null && e.button > 0) return;
      try { cv.setPointerCapture(e.pointerId); } catch (x) { /* не всі вміють */ }
      const p = pt(st, e);
      st.ptr = { id: e.pointerId, x: p.x, y: p.y, touch: e.pointerType !== 'mouse' };
      st.trail.push({ x: p.x, y: p.y, at: performance.now() });
      slash(st, p.x, p.y, p.x, p.y, st.ptr.touch ? 16 : 6);
      loop(st);
      e.preventDefault();
    });
    on(cv, 'pointermove', (e) => {
      if (!st.ptr || st.ptr.id !== e.pointerId) return;
      const p = pt(st, e);
      slash(st, st.ptr.x, st.ptr.y, p.x, p.y, st.ptr.touch ? 10 : 4);
      st.ptr.x = p.x; st.ptr.y = p.y;
      st.trail.push({ x: p.x, y: p.y, at: performance.now() });
      loop(st);
    });
    const up = (e) => { if (st.ptr && st.ptr.id === e.pointerId) st.ptr = null; };
    on(cv, 'pointerup', up); on(cv, 'pointercancel', up); on(cv, 'lostpointercapture', up);
    on(cv, 'contextmenu', (e) => e.preventDefault());
  }
  function pt(st, e) { const b = st.el.cv.getBoundingClientRect(); return { x: e.clientX - b.left, y: e.clientY - b.top }; }

  function setAmount(st, a) {
    const v = cur(st), L = (v && v.limits) || { min: 10, max: 2000 };
    a = Math.max(L.min || 1, Math.floor(a) || 0);
    if (L.max > 0) a = Math.min(a, L.max);
    st.amount = a; st.el.amt.value = a; store.set('amt', a);
    paint(st);
  }
  function setAuto(st, x, on) {
    const v = cur(st), cap = (v && v.cap) || 100;
    if (!isFinite(x)) x = st.autoX;
    x = clamp(Math.floor(x * 100 + 1e-6) / 100, 1.01, cap);
    st.autoX = x; st.autoOn = !!on;
    st.el.ax.value = fmtX(x); st.el.aon.checked = st.autoOn;
    store.set('ax', x); store.set('aon', st.autoOn);
    if (v && v.phase === 'fly' && v.round) st.ctx.act('auto', { x: st.autoOn ? x : null });
    paint(st);
  }

  // ---------------------------------------------------------------------------------------------
  // Стан і годинник
  // ---------------------------------------------------------------------------------------------
  const cur = (st) => st.ctx && st.ctx.view;
  const K = (st) => { const v = cur(st); return (v && v.k) || DEF_K; };
  function syncClock(st, v) {
    const s = tms(v.now);
    if (!isFinite(s)) return;
    const d = s - Date.now();
    st.offs2.push({ d, at: Date.now() });
    while (st.offs2.length > 16 || (st.offs2.length && Date.now() - st.offs2[0].at > 60000)) st.offs2.shift();
    st.off = Math.max(...st.offs2.map((o) => o.d));   // найменша затримка — найближче до правди
  }
  const snow = (st) => Date.now() + (st.off || 0);

  /// Позиція овоча в мить сервера T (CSS-пікселі поля); null — ще не вилетів чи давно впав.
  function where(st, f, T) {
    const k = K(st), s = (T - f.t0 - f.at) / k.fly;
    if (s < 0 || s > 1.12) return null;
    const { W, H } = st.g;
    const y = -0.1 + (f.h + 0.1) * 4 * s * (1 - s);
    return { x: (f.x0 + (f.x1 - f.x0) * s) * W, y: H - y * H, s, rot: f.spin * Math.PI * 2 * s };
  }

  /// Злити вид сервера з локальним: нові овочі, розрізане сервером (авторізання), кінець раунду.
  function merge(st, v) {
    const r = v.round;
    if (v.phase === 'idle' && v.hash) st.nextHash = v.hash;
    if (r) {
      if (st.rno !== r.no) {
        st.rno = r.no; st.fruits = new Map(); st.local = []; st.pending.clear(); st.endShown = null; st.tier = 0; st.dead = 0; st.goAt = 0; st.cashAt = 0;
        if (st.nextHash) st.pre[r.no] = st.nextHash;
        say(st, pick(SAY.start));
        big(st, '');
      }
      const t0 = tms(r.t0);
      for (const f of r.fruits || []) {
        let o = st.fruits.get(f.id);
        if (!o) {
          o = { id: f.id, k: f.k, inc: f.inc, at: f.at, x0: f.x0, x1: f.x1, h: f.h, spin: f.spin, t0, cut: false, srv: false };
          st.fruits.set(f.id, o);
          if (f.k === 'k') say(st, pick(SAY.kavun));
          if (f.k === 'K') say(st, pick(SAY.glek));
          if (f.at % (K(st).wave) === 0) throwGlek(st);
        }
        // розрізане сервером (авторізання): множник уже враховано, а серп показуємо, коли овоч трохи злетить
        if (f.cut && !o.srv) {
          o.srv = true;
          if (!o.cut) o.later = true;
        }
      }
      if ((r.tier || 0) > st.tier) { st.tier = r.tier; say(st, pick(SAY.tier)); }
      st.srvM = r.m;
    }
    const l = v.last;
    if (v.phase === 'idle' && l && st.endShown !== l.no && (st.rno === l.no || st.rno == null)) {
      st.endShown = l.no;
      if (st.rno === l.no) ended(st, v, l);
      else if (l.why === 'void') big(st, '<b>Раунд перервано</b><small>' + esc(v.note || '') + '</small>', 'void');
      else showLast(st, l);
    }
  }

  function ended(st, v, l) {
    st.dead = performance.now(); st.cashAt = 0;
    if (l.why === 'rot') {
      if (l.rot) {
        const t0 = tms(l.t0);
        st.fruits.set(l.rot.id, { id: l.rot.id, k: 'x', inc: 0, at: l.rot.at, x0: l.rot.x0, x1: l.rot.x1, h: l.rot.h, spin: l.rot.spin, t0, cut: false, rot: true });
      }
      st.el.stage.classList.remove('shake'); void st.el.stage.offsetWidth; st.el.stage.classList.add('shake');
      say(st, pick(SAY.rot));
      st.rotUntil = performance.now() + 1600;
      big(st, '<b>Гнилий!</b><small>−' + fmtN(l.stake) + ' 🏺</small>', 'lost');
    } else if (l.why === 'cash' || l.why === 'empty') {
      say(st, pick(l.why === 'cash' ? SAY.cash : SAY.empty));
      big(st, '<b>×' + fmtX(l.m) + '</b><small>+' + fmtN(l.win) + ' 🏺</small>', 'won');
      pop(st, '+' + fmtN(l.win) + ' 🏺', st.g.W / 2, st.g.H * 0.45, 'win');
    } else if (l.why === 'void') big(st, '<b>Раунд перервано</b>', 'void');
    loop(st);
  }
  function showLast(st, l) {
    if (l.why === 'rot') big(st, '<b>Гнилий</b><small>минулого разу: −' + fmtN(l.stake) + ' 🏺</small>', 'lost');
    else if (l.why === 'cash' || l.why === 'empty') big(st, '<b>×' + fmtX(l.m) + '</b><small>минулого разу: +' + fmtN(l.win) + ' 🏺</small>', 'won');
  }

  // ---------------------------------------------------------------------------------------------
  // Різання
  // ---------------------------------------------------------------------------------------------
  function live(st) { const v = cur(st); return v && v.phase === 'fly' && v.round && v.round.no === st.rno; }

  /// Відрізок (ax,ay)→(bx,by) у пікселях поля: що перетнув — розрізано.
  function slash(st, ax, ay, bx, by, slack) {
    if (!live(st) || !st.g) return;
    const T = snow(st), u = st.g.u;
    for (const f of st.fruits.values()) {
      if (f.cut || f.srv || f.rot || f.k === 'x') continue;
      const p = where(st, f, T);
      if (!p || p.s > 1.05) continue;
      const r = (KINDS[f.k] || KINDS.a).r * u + slack;
      if (segDist(p.x, p.y, ax, ay, bx, by) <= r) cutFx(st, f, { dx: bx - ax, dy: by - ay }, false);
    }
  }
  function segDist(px, py, ax, ay, bx, by) {
    const dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
    let t = L ? ((px - ax) * dx + (py - ay) * dy) / L : 0;
    t = clamp(t, 0, 1);
    const x = ax + dx * t - px, y = ay + dy * t - py;
    return Math.sqrt(x * x + y * y);
  }

  /// Ⓐ / пробіл: розрізати найнижчий овоч (той, що от-от упаде).
  function cutLowest(st) {
    if (!live(st)) return;
    const T = snow(st);
    let best = null, by = -1;
    for (const f of st.fruits.values()) {
      if (f.cut || f.srv || f.rot || f.k === 'x') continue;
      const p = where(st, f, T);
      if (!p || p.s > 1.02) continue;
      if (p.y > by) { by = p.y; best = { f, p }; }
    }
    if (!best) return;
    const r = (KINDS[best.f.k] || KINDS.a).r * st.g.u;
    const now = performance.now();
    st.trail.push({ x: best.p.x - r * 1.6, y: best.p.y - r * 0.4, at: now - 60 }, { x: best.p.x + r * 1.6, y: best.p.y + r * 0.4, at: now });
    cutFx(st, best.f, { dx: 1, dy: 0.25 }, false);
    loop(st);
  }

  /// Розріз: половинки, сік, пляма, «×1,04»; свій — номер у пачку до сервера.
  function cutFx(st, f, dir, server) {
    if (f.cut) return;
    f.cut = true;
    const T = snow(st), p = where(st, f, T) || { x: st.g.W * f.x1, y: st.g.H * 0.6, rot: 0 };
    const K0 = KINDS[f.k] || KINDS.a, r = K0.r * st.g.u, sc = st.g.H / 500;
    const ang = dir ? Math.atan2(dir.dy, dir.dx) : Math.random() * Math.PI;
    const nx = -Math.sin(ang), ny = Math.cos(ang), now = performance.now();
    for (const side of [1, -1]) {
      st.halves.push({ k: f.k, x: p.x, y: p.y, vx: nx * side * 90 * sc + (Math.random() - 0.5) * 40, vy: ny * side * 90 * sc - 160 * sc, rot0: p.rot, spin: 0, vr: side * (2 + Math.random() * 2), ang, side, r, born: now });
    }
    for (let i = 0; i < 14; i++) {
      const a = Math.random() * Math.PI * 2, sp = (80 + Math.random() * 260) * sc;
      st.drops.push({ x: p.x, y: p.y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp - 120 * sc, r: r * (0.06 + Math.random() * 0.08), c: K0.juice, born: now, life: 500 + Math.random() * 400 });
    }
    st.stains.push({ x: p.x, y: p.y, r: r * 1.3, c: K0.juice, born: now });
    pop(st, incX(f.inc), p.x, p.y - r, f.k === 'K' ? 'glek' : f.k === 'k' ? 'gold' : '');
    st.local.push(f.inc);
    if (!server) {
      st.pending.add(f.id);
      flush(st);
    }
    paint(st);
    loop(st);
  }
  function flush(st) {
    if (!st.pending.size || st.flushT) return;
    const wait = Math.max(0, 80 - (performance.now() - (st.sentAt || -1e9)));
    st.flushT = setTimeout(() => {
      st.flushT = 0;
      if (!st.pending.size || !live(st)) { st.pending.clear(); return; }
      const ids = [...st.pending]; st.pending.clear();
      st.sentAt = performance.now();
      for (const id of ids) { const f = st.fruits.get(id); if (f) f.sent = true; }
      try { st.ctx.input('cut', { ids }); } catch (e) { /* старий каркас */ }
    }, wait);
  }

  /// Множник на екрані: сервер + розрізане тут, чого сервер ще не підтвердив.
  function shownM(st) {
    const v = cur(st);
    if (!live(st)) return v && v.last ? v.last.m : 1;
    let m = v.round.m;
    for (const f of st.fruits.values()) if (f.cut && !f.srv && !f.rot) m += f.inc / 100;
    return m;
  }

  // ---------------------------------------------------------------------------------------------
  // Кнопки, панель, тексти
  // ---------------------------------------------------------------------------------------------
  function mode(st) {
    const v = cur(st);
    if (!v) return { m: 'wait', t: '…', s: '' };
    const now = performance.now();
    if (st.rotUntil && now < st.rotUntil) return { m: 'lost', t: '✕', s: 'гнилий гарбуз' };
    // відповідь на дію вже є, а вид із тіка — ще ні: не даємо натиснути вдруге
    if (live(st) && st.cashAt && now - st.cashAt < 1500) return { m: 'wait', t: 'Забрано!', s: 'Глек рахує черепки' };
    if (!live(st) && st.goAt && now - st.goAt < 1500) return { m: 'wait', t: 'Глек замахується…', s: 'зараз полетить' };
    if (live(st)) {
      const m = shownM(st), r = v.round;
      if (m <= 1.0001) return { m: 'wait', t: 'Ріж!', s: 'забрати — після першого розрізу' };
      return { m: 'cash', t: 'Забрати ' + fmtN(Math.floor(r.stake * Math.min(m, r.cap) + 1e-6)) + ' 🏺', s: '×' + fmtX(m) + (r.auto ? ' · авто ×' + fmtX(r.auto) : '') };
    }
    if (v.on === false) return { m: 'off', t: 'Ярмарок зачинено', s: 'Глек поїхав по кавуни' };
    return { m: 'go', t: 'Поїхали! ' + fmtN(st.amount) + ' 🏺', s: (st.autoOn ? 'авто ×' + fmtX(st.autoX) : 'забираєш сам') + (st.knife ? ' · 🔪 ріже Глек' : ' · ріжеш сам') };
  }

  function paint(st) {
    const v = cur(st);
    if (!v || !st.el) return;
    const md = mode(st), el = st.el;
    if (el.go._m !== md.m) { el.go._m = md.m; el.go.className = 'kv-go M-' + md.m; }
    el.gt.textContent = md.t; el.gs.textContent = md.s;
    el.go.disabled = md.m === 'off' || md.m === 'lost' || md.m === 'wait' || !!st.busy;
    const flying = live(st);
    el.box.classList.toggle('P-fly', !!flying);
    if (el.amt.disabled !== !!flying) {
      el.amt.disabled = !!flying;
      el.box.querySelectorAll('.kv-amt button').forEach((x) => { x.disabled = !!flying; });
    }
    const m = shownM(st);
    el.m.textContent = '×' + fmtX(flying ? m : (v.last && st.endShown === v.last.no && st.rno === v.last.no ? v.last.m : 1));
    el.m.parentNode.dataset.hot = flying ? (m >= 5 ? 3 : m >= 2 ? 2 : m > 1 ? 1 : 0) : 0;
    el.wal.innerHTML = v.wallet != null ? '🏺 <b>' + fmtN(v.wallet) + '</b>' : '';
    el.win.textContent = flying ? 'виграш: ' + fmtN(Math.floor(v.round.stake * Math.min(m, v.round.cap) + 1e-6)) : (v.last && v.last.win ? 'виграш: ' + fmtN(v.last.win) : '');
    const strip = flying ? st.local.slice(-8) : [];
    const sig = strip.join(',') + '|' + (flying ? 1 : 0);
    if (el.strip._sig !== sig) {
      el.strip._sig = sig;
      el.strip.innerHTML = strip.map((inc) => '<i class="' + (inc >= 100 ? 'c3' : inc >= 25 ? 'c2' : inc >= 8 ? 'c1' : '') + '">' + incX(inc) + '</i>').join('')
        || (flying ? '<i class="c0">ріж серпом!</i>' : '');
    }
    const hs = (v.history || []).map((h) => h.x + h.why).join(',');
    if (el.hist._sig !== hs) {
      el.hist._sig = hs;
      el.hist.innerHTML = (v.history || []).slice(0, 8).map((h) => '<i class="' + (h.why === 'rot' ? 'r' : h.why === 'void' ? 'v' : 'w') + '" title="' + (h.why === 'rot' ? 'гнилий' : h.why === 'void' ? 'перервано' : '+' + h.win + ' 🏺') + '">×' + fmtX(h.x) + '</i>').join('');
    }
    el.hash.textContent = v.hash ? '🔒 ' + v.hash.slice(0, 10) + '…' : '';
    el.hash.title = flying ? 'Відбиток цього раунду — seed відкриється, коли скінчиться' : 'Відбиток наступного раунду — його seed уже вирішено';
    if (!flying && v.note && st.noteShown !== v.note) { st.noteShown = v.note; try { st.ctx.toast(v.note, 'wait'); } catch (e) { /* без тостів */ } }
    if (!flying && !st.dead && !el.big.innerHTML && !st.saidIdle) { st.saidIdle = true; say(st, pick(SAY.idle)); }
  }

  function primary(st, click) {
    const v = cur(st);
    if (!v || st.busy) return;
    if (live(st)) {
      if (click) cash(st); else cutLowest(st);
      return;
    }
    if (st.rotUntil && performance.now() < st.rotUntil) return;
    if (st.goAt && performance.now() - st.goAt < 1500) return;
    if (v.on === false) return;
    st.busy = true; paint(st);
    st.ctx.act('start', { amount: st.amount, auto: st.autoOn ? st.autoX : null, autocut: !!st.knife })
      .catch(() => null).then((r) => { st.busy = false; if (r && r.ok && !live(st)) st.goAt = performance.now(); paint(st); setTimeout(() => paint(st), 1600); });
  }
  function cash(st) {
    if (!live(st) || st.busy) return;
    if (shownM(st) <= 1.0001 || (st.cashAt && performance.now() - st.cashAt < 1500)) return;
    // ще не надіслане — спершу (інакше «Забрати» обжене свої ж розрізи)
    if (st.pending.size) {
      clearTimeout(st.flushT); st.flushT = 0;
      const ids = [...st.pending]; st.pending.clear();
      try { st.ctx.input('cut', { ids }); } catch (e) { /* старий каркас */ }
    }
    st.busy = true; paint(st);
    st.ctx.act('cash').catch(() => null).then((r) => { st.busy = false; if (r && r.ok && live(st)) st.cashAt = performance.now(); paint(st); });
  }

  function say(st, text) {
    const el = st.el.say;
    if (!el || !text) return;
    el.textContent = text;
    el.classList.remove('on'); void el.offsetWidth; el.classList.add('on');
    clearTimeout(st.sayT); st.sayT = setTimeout(() => el.classList.remove('on'), 2600);
  }
  function big(st, html, cls) {
    st.el.big.innerHTML = html || '';
    st.el.big.className = 'kv-big' + (cls ? ' B-' + cls : '');
  }
  function pop(st, text, x, y, cls) {
    const fx = st.el.fx;
    if (fx.childElementCount > 18) fx.firstChild.remove();
    const d = document.createElement('div');
    d.className = 'kv-pop ' + (cls || '');
    d.textContent = text;
    d.style.left = Math.round(x) + 'px'; d.style.top = Math.round(y) + 'px';
    fx.appendChild(d);
    setTimeout(() => d.remove(), 1300);
  }
  function throwGlek(st) {
    const g = st.el.glek; if (!g) return;
    g.classList.remove('throw'); void g.offsetWidth; g.classList.add('throw');
  }

  // ---------------------------------------------------------------------------------------------
  // ⓘ: правила й перевірка чесності
  // ---------------------------------------------------------------------------------------------
  function dots(codes) {
    return [...(codes || '')].map((c) => '<i class="kv-dot d-' + (c === 'K' ? 'G' : c) + '" title="' + esc((KINDS[c] || {}).name || c) + '"></i>').join('');
  }
  function info(st, open) {
    const el = st.el.info;
    if (!open) { el.hidden = true; return; }
    const v = cur(st) || {}, l = v.last;
    const rows = ORDER.split('').map((c) => '<tr><td>' + dots(c) + ' ' + esc(KINDS[c].name) + '</td><td>' + incX(KINDS[c].inc) + '</td><td>' + (KINDS[c].w / 10) + ' %</td></tr>').join('');
    el.innerHTML = '<div class="kv-card"><button type="button" class="kv-x0" aria-label="Закрити">✕</button>'
      + '<h3>Кавуни на ярмарку</h3><ul>'
      + '<li>Став черепки й тисни «Поїхали!». Глек кидає з воза овочі — ріж серпом: розчерк мишкою, свайп чи тап пальцем, Ⓐ/пробіл — найнижчий.</li>'
      + '<li>Кожен розрізаний додає свій приріст: ×1 + 0,09 + 0,04 = <b>×1,13</b>. Пропустив — нічого не губиш.</li>'
      + '<li><b>Гнилий гарбуз</b> (темний, з мухами) — кінець: щойно він вилетів, «Забрати» гасне, ставка пропала.</li>'
      + '<li>«Забрати» — будь-коли після першого розрізу: ставка × множник. Автозабір — сам, щойно множник ≥ X.</li>'
      + '<li>🔪 Авторізання: Глек ріже все сам — тоді це чистий crash. Стеля — ×' + fmtX(v.cap || 100) + ': далі «віз порожній», раунд платить сам.</li>'
      + '<li>Ярмарок розгулюється: від ×3, ×10 і ×30 овочі важчі (прирости ×2, ×5, ×10) і летять густіше.</li>'
      + '<li>Повернення — 97 % для того, хто ріже все, хоч коли б забирав.</li></ul>'
      + '<h3>Овочі</h3><table class="kv-tab"><tr><th></th><th>на овочі</th><th>шанс</th></tr>' + rows + '</table>'
      + '<h3>Чесно наперед</h3><div class="kv-f">Уся послідовність вирішена на старті з seed. Його відбиток (sha256) ти бачиш <b>до</b> ставки, сам seed — після раунду. '
      + 'Овоч №k — з sha256("seed:k"); формула — у правилах гри (docs/games/specs/kavuny.md §3.3).</div>'
      + '<div class="kv-kv"><span>' + (live(st) ? 'цей раунд' : 'наступний раунд') + '</span><code>' + esc(v.hash || '—') + '</code></div>'
      + (l && l.seed ? '<div class="kv-kv"><span>раунд №' + l.no + ', seed</span><code>' + esc(l.seed) + '</code><span>відбиток</span><code>' + esc(l.hash) + '</code></div>'
        + '<div class="kv-seq">' + dots(l.codes.slice(0, l.seen)) + (l.seen < l.codes.length ? '<span class="kv-next">далі було б:</span>' + dots(l.codes.slice(l.seen, l.seen + 40)) + (l.codes.length - l.seen > 40 ? '…' : '') : '') + '</div>'
        + '<button type="button" class="kv-chk">Перевірити</button><div class="kv-res"></div>' : '')
      + '</div>';
    el.hidden = false;
  }
  async function check(st) {
    const v = cur(st), l = v && v.last, out = st.el.info.querySelector('.kv-res');
    if (!l || !out) return;
    out.textContent = 'Рахую…';
    try {
      const pre = st.pre[l.no];
      const r = await kavunyCheck(l.seed, pre || l.hash, Math.round(l.cap * 100));
      const same = r.codes === l.codes;
      out.innerHTML = (r.ok ? '✅ sha256(seed) збігся з відбитком' + (pre ? ', який ти бачив до ставки' : ' (до ставки тебе тут не було)') : '❌ відбиток НЕ збігся')
        + '<br>' + (same ? '✅' : '❌') + ' послідовність за формулою: ' + r.codes.length + ' овоч(ів), ' + (r.codes.endsWith('x') ? 'гнилий — №' + r.codes.length : 'віз порожній на ×' + fmtX(r.potential / 100))
        + (same ? ' — як на прилавку' : ' — НЕ як на прилавку');
    } catch (e) { out.textContent = 'Не вийшло перевірити: ' + e.message; }
  }

  // ---------------------------------------------------------------------------------------------
  // Полотно й цикл
  // ---------------------------------------------------------------------------------------------
  function layout(root, st) {
    const b = st.el.stage.getBoundingClientRect();
    const W = Math.max(200, Math.round(b.width)), H = Math.max(180, Math.round(b.height));
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    if (st.g && st.g.W === W && st.g.H === H && st.g.dpr === dpr) return;
    st.g = { W, H, dpr, u: Math.min(W * 0.8, H) };
    const cv = st.el.cv;
    cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr);
    cv.style.width = W + 'px'; cv.style.height = H + 'px';
    st.c2 = cv.getContext('2d');
    paintBack(st);
    draw(st);
  }

  function draw(st) {
    const g = st.c2, G = st.g;
    if (!g || !G) return;
    g.setTransform(G.dpr, 0, 0, G.dpr, 0, 0);
    g.clearRect(0, 0, G.W, G.H);
    if (st.back) g.drawImage(st.back, 0, 0, G.W, G.H);
    const now = performance.now(), T = snow(st), sc = G.H / 500;
    // плями соку на дошках
    st.stains = st.stains.filter((s) => now - s.born < 1400);
    for (const s of st.stains) {
      g.globalAlpha = 0.35 * (1 - (now - s.born) / 1400);
      g.fillStyle = s.c; g.beginPath(); g.ellipse(s.x, s.y, s.r, s.r * 0.7, 0.3, 0, Math.PI * 2); g.fill();
    }
    g.globalAlpha = 1;
    // цілі овочі
    let alive = false;
    for (const [id, f] of st.fruits) {
      const p = where(st, f, T);
      if (!p) { if ((T - f.t0 - f.at) > K(st).fly * 1.2) st.fruits.delete(id); continue; }
      alive = true;
      if (f.later && !f.cut && p.s >= 0.26) { f.later = false; cutFx(st, f, { dx: 1, dy: -0.35 }, true); }
      if (f.cut) continue;
      const r = (KINDS[f.k] || KINDS.a).r * G.u;
      g.save(); g.translate(p.x, p.y); g.rotate(p.rot);
      if (!live(st) && !f.rot) g.globalAlpha = 0.55;
      drawFruit(g, f.k, r, now / 1000);
      g.restore();
      if (f.rot && p.s > 0.98 && !f.splat) { f.splat = true; st.stains.push({ x: p.x, y: G.H - 8, r: r * 2, c: '#3e4a1a', born: now }); }
    }
    // половинки
    st.halves = st.halves.filter((h) => now - h.born < 1600 && h.y < G.H + 80);
    for (const h of st.halves) {
      const dt = Math.min(0.05, (now - (h.t || h.born)) / 1000); h.t = now;
      h.vy += 1500 * sc * dt; h.x += h.vx * dt; h.y += h.vy * dt; h.spin += h.vr * dt;
      // кадр розрізу: лінія розрізу — вісь x; половинка — по свій бік від неї й крутиться як ціла
      g.save(); g.translate(h.x, h.y); g.rotate(h.spin + h.ang);
      g.beginPath(); g.rect(-h.r * 2, h.side > 0 ? 0 : -h.r * 2.4, h.r * 4, h.r * 2.4); g.clip();
      g.save(); g.rotate(h.rot0 - h.ang); drawFruit(g, h.k, h.r, now / 1000); g.restore();
      face(g, h.k, h.r);
      g.restore();
    }
    // краплі
    st.drops = st.drops.filter((d) => now - d.born < d.life);
    for (const d of st.drops) {
      const dt = Math.min(0.05, (now - (d.t || d.born)) / 1000); d.t = now;
      d.vy += 1200 * sc * dt; d.x += d.vx * dt; d.y += d.vy * dt;
      g.globalAlpha = 1 - (now - d.born) / d.life; g.fillStyle = d.c;
      g.beginPath(); g.arc(d.x, d.y, Math.max(1, d.r), 0, Math.PI * 2); g.fill();
    }
    g.globalAlpha = 1;
    // слід серпа
    st.trail = st.trail.filter((p) => now - p.at < 140);
    if (st.trail.length > 1) {
      g.lineCap = 'round'; g.lineJoin = 'round';
      for (let i = 1; i < st.trail.length; i++) {
        const a = st.trail[i - 1], b = st.trail[i], k = i / st.trail.length;
        g.strokeStyle = 'rgba(255,248,220,' + (0.25 + 0.7 * k) + ')'; g.lineWidth = 1 + 6 * k;
        g.beginPath(); g.moveTo(a.x, a.y); g.lineTo(b.x, b.y); g.stroke();
      }
      g.strokeStyle = 'rgba(255,200,90,.5)'; g.lineWidth = 1.5;
      g.beginPath(); st.trail.forEach((p, i) => (i ? g.lineTo(p.x, p.y) : g.moveTo(p.x, p.y))); g.stroke();
    }
    return alive || st.halves.length || st.drops.length || st.trail.length || st.stains.length || live(st);
  }

  function loop(st) {
    if (st.raf || !st.shown) return;
    const step = () => {
      st.raf = 0;
      if (!st.shown || !st.g) return;
      const more = draw(st);
      if (live(st) || (st.rotUntil && performance.now() < st.rotUntil + 300)) paint(st);
      if (more) st.raf = requestAnimationFrame(step);
      else paint(st);
    };
    st.raf = requestAnimationFrame(step);
  }
  function stop(st) { if (st.raf) cancelAnimationFrame(st.raf); st.raf = 0; }

  function sync(root, st) {
    const v = cur(st);
    if (!v || !st.el) return;
    syncClock(st, v);
    merge(st, v);
    paint(st);
    if (live(st) || st.fruits.size) loop(st);
    else draw(st);
    const card = st.root && st.root.closest('.gtable'), se = card && card.querySelector('.gstatus'), t = status(st.ctx);
    if (se && t && se.textContent !== t) se.textContent = t;
  }

  function status(ctx) {
    const st = ctx && ctx._kv, v = st && cur(st);
    if (!v) return 'Кавуни на ярмарку';
    if (live(st)) return 'Ріж! Разом ×' + fmtX(shownM(st));
    if (v.last && v.last.why === 'rot') return 'Гнилий гарбуз — ще раз?';
    if (v.last && v.last.win) return 'Забрав ×' + fmtX(v.last.m) + ': +' + fmtN(v.last.win) + ' 🏺';
    return 'Став черепки — і до воза';
  }

  function setup(root, ctx) {
    let st = root._kv;
    if (!st) {
      st = root._kv = {
        root, offs: [], offs2: [], off: 0, fruits: new Map(), halves: [], drops: [], stains: [], trail: [], local: [], pending: new Set(),
        pre: {}, shown: ctx.shown !== false, tier: 0,
      };
      st.ctx = ctx; ctx._kv = st;
      skeleton(root, st);
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => { clearTimeout(st.roT); st.roT = setTimeout(() => layout(root, st), 60); });
        st.ro.observe(st.el.stage);
      }
    }
    st.ctx = ctx; ctx._kv = st;
    return st;
  }

  HGames.register({
    id: 'kavuny',
    added: '2026-10-10',
    icon: ICON,
    seatNames: () => 'ярмарок',
    pad: {
      a: 'Space',
      x: 'KeyC',
      hint: '{a} різати найнижчий / поїхали · {x} забрати',
      when: (ctx) => ctx.mine,
    },
    mount(root, ctx) {
      const st = setup(root, ctx);
      layout(root, st);
      sync(root, st);
    },
    update(root, ctx) {
      const st = setup(root, ctx);
      if (!st.g) layout(root, st);
      sync(root, st);
    },
    visible(root, ctx, on) {
      const st = root._kv;
      if (!st) return;
      st.shown = on;
      if (on) { layout(root, st); sync(root, st); } else stop(st);
    },
    onKey(e, ctx) {
      const st = ctx._kv;
      if (!st || e.repeat) return false;
      const tag = e.target && e.target.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA') return false;
      if (e.code === 'Space' || e.key === ' ') { primary(st, false); return true; }
      if (e.code === 'KeyC' || e.code === 'Enter' || e.key === 'Enter') {
        if (live(st)) cash(st); else primary(st, false);
        return true;
      }
      return false;
    },
    status,
    unmount(root) {
      const st = root._kv;
      if (!st) return;
      stop(st);
      clearTimeout(st.roT); clearTimeout(st.sayT); clearTimeout(st.flushT);
      if (st.ro) st.ro.disconnect();
      st.offs.forEach((f) => f()); st.offs = [];
      if (st.ctx) st.ctx._kv = null;
      root._kv = null;
    },
    // для перевірок: формула й форматування без DOM
    qa: { kavunyCheck, fmtX, where },
  });
})();
