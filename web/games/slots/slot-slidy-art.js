/* Сліди на полиці — арт (slot-slidy). Звичайний скрипт: реєструє SlotArt['slot-slidy'].
 * Посуд, горно, черепки й сцени — ті самі, що в «Розбитих глеках» (SlotArt['slot-cascade'], slot.js вантажить його
 * першим); тут — свої лого, афіша, рамка-полиця 6×6 і сліди від глини (extras.trace). */
(function () {
  'use strict';
  const A = window.SlotArt && window.SlotArt['slot-cascade'];
  if (!A) return;
  const KEYS = ['k1', 'k2', 'k3', 'pot', 'makitra', 'kumanets', 'glek', 'furnace'];
  const symbols = {};
  KEYS.forEach((k) => { if (A.symbols[k]) symbols[k] = Object.assign({}, A.symbols[k]); });
  if (symbols.k1) symbols.k1.name = 'Синя кахля';
  const FONT = 'font-family="Onest, system-ui, sans-serif" font-weight="900"';
  const INK = '#2a1009';
  const defsInner = (String(A.extras && A.extras.defs || '').match(/<defs>([\s\S]*)<\/defs>/) || [])[1] || '';

  // ---------- слід від глини: v = 1 (мокре коло) або 2…128 ----------
  // Колір — від сирої глини до золотої поливи. viewBox 0 0 100 100, самодостатній (свої градієнти з префіксом sl-t).
  const TONES = {
    2: ['#d9a77a', '#a8683c', '#5e3216'], 4: ['#f0a070', '#c85a2c', '#6e2410'], 8: ['#ffb070', '#e2702a', '#7a3010'],
    16: ['#ffd27a', '#ec9a28', '#8a4a0e'], 32: ['#ffe9a0', '#f2bc32', '#9a5e08'], 64: ['#fff4c0', '#f9d040', '#a86a08'],
    128: ['#ffffff', '#ffe066', '#b8740a'],
  };
  function trace(v) {
    v = +v || 0;
    if (v <= 0) return '';
    if (v === 1) {
      return '<svg viewBox="0 0 100 100" class="sl-trace sl-t1" xmlns="http://www.w3.org/2000/svg">'
        + '<ellipse cx="50" cy="54" rx="40" ry="36" fill="#3a1a0c" opacity=".38"/>'
        + '<ellipse cx="50" cy="54" rx="34" ry="30" fill="none" stroke="#8a5a36" stroke-width="5" opacity=".55" stroke-dasharray="14 7"/>'
        + '<path d="M28 44 q8 -10 20 -10" fill="none" stroke="#e8c7a0" stroke-width="3" stroke-linecap="round" opacity=".35"/></svg>';
    }
    const t = TONES[v] || TONES[128], id = 'sl-t' + v;
    const txt = '×' + v, fs = txt.length <= 2 ? 44 : txt.length === 3 ? 36 : 29;
    let rays = '';
    if (v >= 64) {
      rays = '<g class="sl-t-rays" opacity=".75">';
      for (let i = 0; i < 12; i++) rays += '<path d="M50 54 L47 6 L53 6 Z" transform="rotate(' + (i * 30) + ' 50 54)" fill="#fff3a0"/>';
      rays += '</g>';
    }
    return '<svg viewBox="0 0 100 100" class="sl-trace sl-tv" xmlns="http://www.w3.org/2000/svg"><defs>'
      + '<radialGradient id="' + id + '" cx=".38" cy=".3" r=".85"><stop offset="0" stop-color="' + t[0] + '"/><stop offset=".55" stop-color="' + t[1] + '"/><stop offset="1" stop-color="' + t[2] + '"/></radialGradient></defs>'
      + rays
      + '<ellipse cx="50" cy="58" rx="42" ry="36" fill="' + INK + '" opacity=".35"/>'
      + '<ellipse cx="50" cy="54" rx="42" ry="36" fill="url(#' + id + ')" stroke="' + INK + '" stroke-width="3"/>'
      + '<ellipse cx="50" cy="54" rx="31" ry="26" fill="none" stroke="#fff" stroke-width="2" opacity=".3"/>'
      + '<path d="M22 42 q10 -14 28 -15" fill="none" stroke="#fff" stroke-width="4" stroke-linecap="round" opacity=".45"/>'
      + '<text x="50" y="' + (54 + fs * 0.36).toFixed(1) + '" text-anchor="middle" ' + FONT + ' font-size="' + fs + '" fill="#fff" stroke="' + INK + '" stroke-width="5" paint-order="stroke" stroke-linejoin="round">' + txt + '</text>'
      + '</svg>';
  }

  // ---------- лого ----------
  function logo() {
    const T = 'text-anchor="middle" ' + FONT;
    const L1 = 'x="280" y="112" font-size="118" letter-spacing="2"', L2 = 'x="280" y="194" font-size="64" letter-spacing="4"';
    let dots = '';
    [[34, 146, 9, '#a8683c'], [58, 174, 6, '#c85a2c'], [526, 146, 9, '#f2bc32'], [502, 174, 6, '#ffe066']].forEach(([x, y, r, c]) => {
      dots += '<ellipse cx="' + x + '" cy="' + y + '" rx="' + r * 1.3 + '" ry="' + r + '" fill="' + c + '" stroke="' + INK + '" stroke-width="3"/>';
    });
    return '<svg viewBox="0 0 560 230" class="sl-logo" xmlns="http://www.w3.org/2000/svg"><defs>'
      + '<linearGradient id="sl-lg-t" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff2b0"/><stop offset=".4" stop-color="#f9c73a"/><stop offset=".75" stop-color="#d07a1c"/><stop offset="1" stop-color="#8a420e"/></linearGradient>'
      + '<linearGradient id="sl-lg-c" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffd2a8"/><stop offset=".45" stop-color="#d2672f"/><stop offset="1" stop-color="#7a2e14"/></linearGradient>'
      + '<linearGradient id="sl-lg-h" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".85"/><stop offset=".42" stop-color="#fff" stop-opacity=".15"/><stop offset=".43" stop-color="#fff" stop-opacity="0"/></linearGradient>'
      + '</defs><g ' + T + '>'
      + '<text ' + L1 + ' transform="translate(0 7)" fill="' + INK + '" stroke="' + INK + '" stroke-width="20" stroke-linejoin="round">СЛІДИ</text>'
      + '<text ' + L1 + ' fill="url(#sl-lg-t)" stroke="' + INK + '" stroke-width="12" stroke-linejoin="round" paint-order="stroke">СЛІДИ</text>'
      + '<text ' + L1 + ' fill="url(#sl-lg-h)">СЛІДИ</text>'
      + '<text ' + L2 + ' transform="translate(0 5)" fill="' + INK + '" stroke="' + INK + '" stroke-width="14" stroke-linejoin="round">НА ПОЛИЦІ</text>'
      + '<text ' + L2 + ' fill="url(#sl-lg-c)" stroke="' + INK + '" stroke-width="9" stroke-linejoin="round" paint-order="stroke">НА ПОЛИЦІ</text>'
      + '</g>' + dots + '</svg>';
  }
  const LOGO = logo();

  // ---------- рамка: полиця 6×6 ----------
  // viewBox 0 0 600 600, самодостатня. Вікно поля — x 18..582, y 30..594 (564×564): клітинка 94, кожен ряд стоїть на
  // своїй полиці (верх полиці = 30 + 94r + 86). Сітку класти у вікно: left 3%, top 5%, width 94%, height 94%.
  function frame() {
    let s = '<svg viewBox="0 0 600 600" class="sl-frame-svg" xmlns="http://www.w3.org/2000/svg" preserveAspectRatio="none"><defs>'
      + '<linearGradient id="sl-fr-w" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#e8b47a"/><stop offset=".5" stop-color="#b8763c"/><stop offset="1" stop-color="#6e3a18"/></linearGradient>'
      + '<linearGradient id="sl-fr-b" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#5a3018"/><stop offset="1" stop-color="#3a1c0c"/></linearGradient>'
      + '</defs>'
      + '<rect x="4" y="10" width="592" height="588" rx="16" fill="url(#sl-fr-w)" stroke="' + INK + '" stroke-width="4"/>'
      + '<rect x="18" y="30" width="564" height="564" rx="4" fill="url(#sl-fr-b)" stroke="' + INK + '" stroke-width="2"/>';
    for (let i = 1; i < 6; i++) s += '<path d="M' + (18 + i * 94) + ' 30 V594" stroke="#1e0c04" stroke-width="2" opacity=".45"/>';
    for (let r = 0; r < 6; r++) {
      const y = 30 + r * 94 + 84;
      s += '<rect x="18" y="' + y + '" width="564" height="10" fill="#b8763c" stroke="' + INK + '" stroke-width="1.5"/>'
        + '<rect x="18" y="' + y + '" width="564" height="3" fill="#f2c58e" opacity=".8"/>';
    }
    s += '<path d="M30 20 q10 -8 20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0" fill="none" stroke="#2f6fe0" stroke-width="3" stroke-linecap="round"/>';
    return s + '</svg>';
  }
  const FRAME = frame();

  // ---------- афіша ----------
  function poster() {
    const sym = (k, x, y, s) => (symbols[k] ? symbols[k].svg.replace('<svg ', '<svg x="' + x + '" y="' + y + '" width="' + s + '" height="' + s + '" ') : '');
    const tr = (v, x, y, s) => trace(v).replace('<svg ', '<svg x="' + x + '" y="' + y + '" width="' + s + '" height="' + s + '" ');
    let rays = '';
    for (let i = 0; i < 18; i++) {
      const a1 = i * Math.PI / 9, a2 = a1 + 0.12;
      rays += '<path d="M250 150 L' + (250 + 420 * Math.cos(a1)).toFixed(0) + ' ' + (150 + 420 * Math.sin(a1)).toFixed(0)
        + ' L' + (250 + 420 * Math.cos(a2)).toFixed(0) + ' ' + (150 + 420 * Math.sin(a2)).toFixed(0) + ' Z"/>';
    }
    return '<svg viewBox="0 0 360 240" class="sl-poster" xmlns="http://www.w3.org/2000/svg">'
      + '<defs>' + defsInner
      + '<radialGradient id="sl-p-bg" cx=".7" cy=".62" r=".9"><stop offset="0" stop-color="#ffe7a0"/><stop offset=".35" stop-color="#e8903a"/><stop offset=".75" stop-color="#8a3a14"/><stop offset="1" stop-color="#3a1206"/></radialGradient>'
      + '<radialGradient id="sl-p-vig" cx=".5" cy=".5" r=".72"><stop offset=".6" stop-color="#2a0a04" stop-opacity="0"/><stop offset="1" stop-color="#2a0a04" stop-opacity=".55"/></radialGradient>'
      + '</defs>'
      + '<rect width="360" height="240" fill="url(#sl-p-bg)"/>'
      + '<g fill="#fff6c8" opacity=".18">' + rays + '</g>'
      // полиця зі слідами
      + '<rect x="0" y="196" width="360" height="14" fill="#b8763c" stroke="' + INK + '" stroke-width="3"/>'
      + '<rect x="0" y="210" width="360" height="30" fill="#5a3018"/>'
      + tr(1, 14, 150, 52) + tr(2, 70, 150, 52) + tr(8, 126, 146, 58) + tr(32, 186, 140, 66) + tr(128, 258, 128, 88)
      + sym('glek', 268, 70, 78) + sym('kumanets', 8, 96, 60) + sym('furnace', 176, 74, 64)
      + '<rect width="360" height="240" fill="url(#sl-p-vig)"/>'
      + LOGO.replace('<svg ', '<svg x="40" y="-6" width="240" height="98" ')
      + '<rect x="1.5" y="1.5" width="357" height="237" rx="3" fill="none" stroke="' + INK + '" stroke-width="3"/>'
      + '</svg>';
  }

  window.SlotArt['slot-slidy'] = {
    title: 'Сліди на полиці',
    symbols,
    scene: A.scene,
    logo: LOGO,
    poster: poster(),
    extras: {
      defs: A.extras && A.extras.defs,   // градієнти посуду (id sc-*) — ті самі, що в «Розбитих глеках»
      shards: A.extras && A.extras.shards,
      trace,                             // (v) → слід: 1 — мокре коло, 2…128 — випалений з числом
      frame: FRAME,                      // полиця 6×6 (див. коментар над frame())
    },
  };
})();
