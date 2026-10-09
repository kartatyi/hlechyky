/* Арт автомата slot-cluster «Цвіт папороті» (купальська ніч). Звичайний скрипт, реєструє SlotArt['slot-cluster'].
   Символи посилаються на градієнти з extras.defs (кіт вставляє раз на сторінку); сцени, лого, афіша, рамка, шкала,
   русалка, світлячок та іскра — самодостатні (свої <defs> з перейменованими id). */
(function () {
  'use strict';
  // ── Назва: міняється тут — лого й афіша самі перерозкладуться (слова → рядки, кегль за довжиною)
  const TITLE = 'Цвіт папороті';

  // ── Палітра (CSS-дзеркало — змінні --scl-* у slot-cluster-art.css)
  const INK = '#1e1046';          // обведення: глибокий синьо-фіолетовий
  const FLAMEINK = '#7a1d1a';     // обведення вогню
  const FONT = "Onest, system-ui, sans-serif";

  const f = (n) => Math.round(n * 100) / 100;
  const rep = (n, fn) => { let s = ''; for (let i = 0; i < n; i++) s += fn(i); return s; };
  const rng = (seed) => { let s = seed >>> 0; return () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; }; };
  const stops = (a) => a.map(([o, c, op]) => `<stop offset="${o}" stop-color="${c}"${op != null ? ` stop-opacity="${op}"` : ''}/>`).join('');
  const lg = (id, a, x1 = 0, y1 = 0, x2 = 0, y2 = 1, ex = '') => `<linearGradient id="${id}" x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}"${ex}>${stops(a)}</linearGradient>`;
  const rg = (id, a, cx = 0.5, cy = 0.5, r = 0.5, fx, fy, ex = '') => `<radialGradient id="${id}" cx="${cx}" cy="${cy}" r="${r}"${fx != null ? ` fx="${fx}" fy="${fy}"` : ''}${ex}>${stops(a)}</radialGradient>`;
  const glow = (id, c, k = 1) => rg(id, [[0, c, 0.85 * k], [0.32, c, 0.42 * k], [0.66, c, 0.12 * k], [1, c, 0]]);

  // ── Спільні градієнти
  const DEFS_IN = [
    glow('scl-h-w', '#dce6ff'), glow('scl-h-b', '#4f8dff'), glow('scl-h-r', '#ff4434'), glow('scl-h-y', '#ffc21f'),
    glow('scl-h-g', '#ffd25a'), glow('scl-h-o', '#ff6a12'), glow('scl-h-aq', '#4ff0e2'), glow('scl-h-fern', '#86ff6e'),
    glow('scl-h-m', '#ff4fa8'), glow('scl-h-fly', '#e8ff6a'), glow('scl-h-wh', '#ffffff'),
    lg('scl-daisy', [[0, '#ffffff'], [0.55, '#f3f6ff'], [1, '#b4c0f0']]),
    lg('scl-daisy2', [[0, '#e6ebff'], [1, '#8d9be0']]),
    rg('scl-dc', [[0, '#fff7b8'], [0.5, '#ffc928'], [1, '#e2760c']], 0.5, 0.5, 0.6, 0.35, 0.3),
    lg('scl-corn', [[0, '#c4e4ff'], [0.35, '#5b90ff'], [1, '#2333ad']]),
    lg('scl-corn2', [[0, '#7499ee'], [1, '#1a2580']]),
    lg('scl-poppy', [[0, '#ff9064'], [0.45, '#f63c2d'], [1, '#a3112b']]),
    lg('scl-poppy2', [[0, '#e24a36'], [1, '#760b25']]),
    rg('scl-pc', [[0, '#e3efbe'], [0.6, '#8fa676'], [1, '#4d6142']], 0.5, 0.5, 0.5, 0.38, 0.32),
    lg('scl-sunp', [[0, '#fff7a6'], [0.5, '#ffcc1f'], [1, '#ec860c']]),
    lg('scl-sunp2', [[0, '#ffae2a'], [1, '#b8560a']]),
    rg('scl-sunc', [[0, '#bb7430'], [0.6, '#6e3614'], [1, '#3d1a07']], 0.5, 0.5, 0.5, 0.4, 0.35),
    lg('scl-leaf', [[0, '#a2f283'], [0.5, '#36b45c'], [1, '#13603a']]),
    lg('scl-fern', [[0, '#f2ffb0'], [0.45, '#86f56a'], [1, '#14a058']]),
    rg('scl-badge', [[0, '#16704f'], [0.7, '#0a3a2e'], [1, '#05221c']], 0.5, 0.42, 0.58),
    lg('scl-gold', [[0, '#fff4b4'], [0.45, '#ffc93a'], [1, '#bd7610']]),
    lg('scl-wax', [[0, '#d39a48'], [0.28, '#fff6de'], [0.62, '#f6d38a'], [1, '#c08033']], 0, 0, 1, 0),
    rg('scl-fo', [[0, '#fff8c8'], [0.35, '#ffd23a'], [0.7, '#ff7a1a'], [1, '#e2361b']], 0.5, 0.8, 0.8, 0.5, 0.88),
    rg('scl-fm', [[0, '#fffde8'], [0.5, '#ffe45a'], [1, '#ffa21f']], 0.5, 0.8, 0.8),
    rg('scl-fi', [[0, '#ffffff'], [0.5, '#fff6b8'], [1, '#ffd23a']], 0.5, 0.75, 0.7),
    lg('scl-wood', [[0, '#bb7843'], [0.5, '#7c4421'], [1, '#4a2511']]),
    lg('scl-nacre', [[0, '#ffffff'], [0.3, '#c6f6f8'], [0.55, '#a6d9ff'], [0.8, '#ffcdea'], [1, '#f2fbff']], 0, 0, 1, 1),
    rg('scl-shell', [[0, '#ffb3dc'], [0.45, '#ffe0f2'], [0.75, '#c9f4fa'], [1, '#8fdcf0']], 0.5, 1, 1),
    rg('scl-pearl', [[0, '#ffffff'], [0.45, '#ffe2f1'], [1, '#d08cbd']], 0.5, 0.5, 0.55, 0.35, 0.3),
    lg('scl-bp', [[0, '#ff3d8a'], [0.4, '#ff5c2a'], [0.75, '#ffbf3a'], [1, '#fff3b0']]),
    lg('scl-bp2', [[0, '#ffc52a'], [1, '#fffbe2']]),
    rg('scl-bcore', [[0, '#ffffff'], [0.5, '#fff6c0'], [1, '#ffc23a']]),
    lg('scl-ray', [[0, '#fff3b0', 0], [0.6, '#ffe066', 0.5], [1, '#fff6c8', 0.95]]),
    lg('scl-hair', [[0, '#4fd6be'], [0.5, '#17837b'], [1, '#0b4552']]),
    rg('scl-skin', [[0, '#fff3ea'], [0.7, '#ffdccb'], [1, '#f2bfae']], 0.45, 0.4, 0.6),
    lg('scl-shirt', [[0, '#ffffff'], [1, '#d7deff']]),
    lg('scl-tail', [[0, '#8ff5e4'], [0.5, '#2aa6b8'], [1, '#5b3fa8']]),
    lg('scl-water', [[0, '#2b7fd0', 0.96], [1, '#0b2560', 0.98]]),
    lg('scl-flyb', [[0, '#fbffc0'], [1, '#b8f03a']]),
  ].join('');
  const DEFS = `<svg width="0" height="0" style="position:absolute" aria-hidden="true" focusable="false"><defs>${DEFS_IN}</defs></svg>`;

  // самодостатній SVG: свої копії градієнтів з унікальним префіксом
  const own = (pfx, attrs, extraDefs, body) => `<svg ${attrs}><defs>${DEFS_IN}${extraDefs}</defs>${body}</svg>`
    .replace(/(id="|url\(#)scl-/g, `$1${pfx}-`);

  // ── Спільні шматки символів
  const SH = (cy = 92, rx = 30) => `<ellipse cx="50" cy="${cy}" rx="${rx}" ry="4.5" fill="${INK}" opacity=".38"/>`;
  const HALO = (id, r = 48, cx = 50, cy = 50) => `<circle class="a-halo" cx="${cx}" cy="${cy}" r="${r}" fill="url(#${id})"/>`;
  const STAR4 = 'M0,-11 C1,-3 3,-1 11,0 C3,1 1,3 0,11 C-1,3 -3,1 -11,0 C-3,-1 -1,-3 0,-11Z';
  const GLINT = (x = 28, y = 26, s = 1) => `<g transform="translate(${x} ${y}) scale(${s})"><g class="a-glint"><path d="${STAR4}" fill="#fff"/><circle r="2.4" fill="#fff"/></g></g>`;
  const twk = (x, y, s, c = '#fff3a0') => `<g transform="translate(${x} ${y}) scale(${s})"><g class="a-twk"><path d="${STAR4}" fill="${c}"/></g></g>`;
  const sym = (key, body) => `<svg viewBox="0 0 100 100" class="sym scl scl-${key}" aria-hidden="true">${body}</svg>`;
  const under = (shapes, w) => `<g fill="${INK}" stroke="${INK}" stroke-width="${w}" stroke-linejoin="round">${shapes}</g>`;

  // ── Квіти (малюються навколо (0,0))
  const dPet = 'M0,-5 C-8,-11 -9,-31 0,-40 C9,-31 8,-11 0,-5Z';
  const pPet = 'M0,0 C-20,-2 -30,-22 -22,-36 C-16,-46 -6,-44 0,-40 C6,-44 16,-46 22,-36 C30,-22 20,-2 0,0Z';
  const sPet = 'M0,-17 C-6,-24 -7,-35 0,-46 C7,-35 6,-24 0,-17Z';
  function floret(L) {
    const p = [[-3, 0], [-8, -0.7 * L], [-12.5, -0.95 * L], [-6.5, -0.83 * L], [-3.6, -L], [0, -0.86 * L], [3.6, -L], [6.5, -0.83 * L], [12.5, -0.95 * L], [8, -0.7 * L], [3, 0]];
    return 'M' + p.map(([x, y]) => f(x) + ',' + f(y)).join(' L') + 'Z';
  }
  const at = (x, y, s, r, body) => `<g transform="translate(${f(x)} ${f(y)})${r ? ` rotate(${f(r)})` : ''}${s !== 1 ? ` scale(${f(s)})` : ''}">${body}</g>`;

  function miniDaisy() {
    const p = rep(10, (i) => `<path d="M0,-3 C-5,-7 -5.5,-18 0,-23 C5.5,-18 5,-7 0,-3Z" transform="rotate(${i * 36})"/>`);
    return under(p, 5) + `<g fill="url(#scl-daisy)">${p}</g><circle r="6.5" fill="url(#scl-dc)" stroke="${INK}" stroke-width="2.2"/><circle cx="-2" cy="-2.2" r="1.8" fill="#fff" opacity=".7"/>`;
  }
  function miniPoppy() {
    const p = rep(4, (i) => `<path d="${pPet}" transform="rotate(${i * 90 + 45}) scale(.56)"/>`);
    return under(p, 5) + `<g fill="url(#scl-poppy)">${p}</g><circle r="7" fill="#1c0d2c" stroke="${INK}" stroke-width="1.6"/><circle r="3.2" fill="#8fa676"/><path d="M-14,-6 C-12,-12 -8,-15 -3,-16" stroke="#ffc0a8" stroke-width="2.6" fill="none" stroke-linecap="round" opacity=".8"/>`;
  }
  function miniCorn() {
    const p = rep(8, (i) => `<path d="${floret(24)}" transform="rotate(${i * 45})"/>`);
    return under(p, 5) + `<g fill="url(#scl-corn)">${p}</g><circle r="5.5" fill="#5a2aa0" stroke="${INK}" stroke-width="2"/><circle cx="-1.5" cy="-1.5" r="1.6" fill="#d9c2ff"/>`;
  }
  function miniSun() {
    const p = rep(12, (i) => `<path d="M0,-8 C-4.5,-12 -5,-19 0,-25 C5,-19 4.5,-12 0,-8Z" transform="rotate(${i * 30})"/>`);
    return under(p, 5) + `<g fill="url(#scl-sunp)">${p}</g><circle r="10" fill="url(#scl-sunc)" stroke="${INK}" stroke-width="2.2"/><circle cx="-3" cy="-3" r="2.6" fill="#fff" opacity=".25"/>`;
  }
  const MINI = { d: miniDaisy(), p: miniPoppy(), c: miniCorn(), s: miniSun() };
  function leaf(x, y, L, ang, fill = 'url(#scl-leaf)', sw = 2) {
    return `<g transform="translate(${f(x)} ${f(y)}) rotate(${f(ang)})"><path d="M0,0 C${f(0.3 * L)},${f(-0.32 * L)} ${f(0.75 * L)},${f(-0.25 * L)} ${f(L)},0 C${f(0.75 * L)},${f(0.25 * L)} ${f(0.3 * L)},${f(0.32 * L)} 0,0Z" fill="${fill}" stroke="${INK}" stroke-width="${sw}" stroke-linejoin="round"/><path d="M${f(L * 0.08)},0 L${f(L * 0.8)},0" stroke="#0f5a34" stroke-width="${f(sw * 0.6)}" opacity=".55"/></g>`;
  }

  // ── Полум'я (символ свічки) — координати символу 0..100
  const FL_OUT = 'M50,6 C58,18 64,27 60,35 C57,41 43,41 40,35 C36,27 42,18 50,6Z';
  const FL_IN = 'M50,17 C54,24 56,29 54,33 C52,37 48,37 46,33 C44,29 46,24 50,17Z';
  const flame = () => `<path d="${FL_OUT}" fill="url(#scl-fo)" stroke="${FLAMEINK}" stroke-width="2.2" stroke-linejoin="round"/><path d="${FL_IN}" fill="url(#scl-fi)"/><path d="M47,16 C44,22 42,27 43,31" stroke="#fffbe0" stroke-width="2" fill="none" stroke-linecap="round" opacity=".8"/>`;

  // ── Вогнище (символ fire)
  const FIRE1 = 'M22,82 C12,66 20,50 29,42 C29,50 32,55 36,56 C33,42 40,24 50,6 C55,22 68,30 63,48 C68,46 72,40 71,33 C80,46 84,66 76,82 Z';
  const FIRE2 = 'M32,82 C26,70 32,60 38,54 C38,60 41,63 44,63 C42,52 46,40 52,28 C55,40 64,48 61,60 C64,58 66,55 66,51 C71,60 72,72 66,82Z';
  const FIRE3 = 'M40,82 C37,74 41,68 46,63 C46,68 48,70 50,70 C49,64 51,58 54,52 C58,60 62,68 58,82Z';
  const fireFlames = (cls = true) => `<g${cls ? ' class="a-fl1"' : ''}><path d="${FIRE1}" fill="url(#scl-fo)" stroke="${FLAMEINK}" stroke-width="2.6" stroke-linejoin="round"/><path d="M44,20 C39,30 37,40 38,48" stroke="#fff4c0" stroke-width="2.6" fill="none" stroke-linecap="round" opacity=".7"/></g>`
    + `<g${cls ? ' class="a-fl2"' : ''}><path d="${FIRE2}" fill="url(#scl-fm)"/></g><g${cls ? ' class="a-fl3"' : ''}><path d="${FIRE3}" fill="#fffbea"/></g>`;
  const log = (rot, endX) => `<g transform="translate(50 82) rotate(${rot})"><rect x="-35" y="-6.5" width="70" height="13" rx="6.5" fill="url(#scl-wood)" stroke="${INK}" stroke-width="3"/><path d="M-26,-1 L-6,-1 M4,2 L22,2" stroke="#4a2511" stroke-width="1.6" stroke-linecap="round" opacity=".6"/><ellipse cx="${endX}" cy="0" rx="4.4" ry="6.2" fill="#f0bf86" stroke="${INK}" stroke-width="2"/><ellipse cx="${endX}" cy="0" rx="1.8" ry="2.8" fill="none" stroke="#a8683a" stroke-width="1.2"/></g>`;
  const fireLogs = () => log(-17, 31) + log(17, -31) + `<ellipse cx="50" cy="80" rx="12" ry="4" fill="#ff8a2a" opacity=".85"/><ellipse cx="50" cy="79" rx="6" ry="2" fill="#fff0a0"/>`;

  // ── Гребінь (символ comb, координати 0..100)
  function combBody() {
    let s = '';
    s += rep(9, (i) => { const a = (-162 + i * 18) * Math.PI / 180; return `<circle cx="${f(50 + 44 * Math.cos(a))}" cy="${f(54 + 41 * Math.sin(a))}" r="4" fill="url(#scl-gold)" stroke="${INK}" stroke-width="2"/>`; });
    s += rep(10, (i) => { const x = 14 + i * 8; return `<path d="M${x - 3},57 L${x - 3},83 Q${x},90 ${x + 3},83 L${x + 3},57Z" fill="url(#scl-nacre)" stroke="${INK}" stroke-width="2.4" stroke-linejoin="round"/>`; });
    s += `<path d="M7,55 C5,30 27,12 50,12 C73,12 95,30 93,55 Z" fill="url(#scl-shell)" stroke="${INK}" stroke-width="3" stroke-linejoin="round"/>`;
    s += rep(7, (i) => { const a = (-150 + i * 20) * Math.PI / 180; return `<path d="M50,50 L${f(50 + 40 * Math.cos(a))},${f(52 + 37 * Math.sin(a))}" stroke="${i % 2 ? '#e58ac0' : '#5fc4dc'}" stroke-width="2.2" stroke-linecap="round" opacity=".9"/>`; });
    s += `<path d="M17,44 C17,30 28,19 42,16" stroke="#fff" stroke-width="3.2" fill="none" stroke-linecap="round" opacity=".85"/>`;
    s += `<rect x="4" y="49" width="92" height="11" rx="5.5" fill="url(#scl-gold)" stroke="${INK}" stroke-width="3"/>`;
    s += rep(8, (i) => `<circle cx="${f(14 + i * 10.3)}" cy="54.5" r="2.4" fill="url(#scl-pearl)" stroke="${INK}" stroke-width="1"/>`);
    s += `<ellipse cx="25" cy="42" rx="5" ry="3.2" fill="#36d6c8" stroke="${INK}" stroke-width="2"/><ellipse cx="75" cy="42" rx="5" ry="3.2" fill="#36d6c8" stroke="${INK}" stroke-width="2"/>`;
    s += `<circle cx="50" cy="34" r="12" fill="url(#scl-gold)" stroke="${INK}" stroke-width="2.6"/>`;
    s += `<g class="a-pearl"><circle cx="50" cy="34" r="8.4" fill="url(#scl-pearl)" stroke="${INK}" stroke-width="2"/><circle cx="47" cy="31" r="2.6" fill="#fff"/></g>`;
    return s;
  }

  // ── Листок папороті (frond) — генерується вздовж кривої
  function frond(P0, P1, P2, size, opt = {}) {
    const fill = opt.fill || 'url(#scl-fern)', ink = opt.ink || INK, rib = opt.rib || '#e2ff9a', uw = opt.uw || 4;
    const B = (t) => [(1 - t) * (1 - t) * P0[0] + 2 * (1 - t) * t * P1[0] + t * t * P2[0], (1 - t) * (1 - t) * P0[1] + 2 * (1 - t) * t * P1[1] + t * t * P2[1]];
    const T = (t) => { const x = 2 * (1 - t) * (P1[0] - P0[0]) + 2 * t * (P2[0] - P1[0]); const y = 2 * (1 - t) * (P1[1] - P0[1]) + 2 * t * (P2[1] - P1[1]); return Math.atan2(y, x) * 180 / Math.PI; };
    let leaves = '', veins = '';
    for (let k = 0; k < 9; k++) {
      for (const sd of [-1, 1]) {
        const t = 0.1 + k * 0.092 + (sd > 0 ? 0.04 : 0);
        if (t > 0.94) continue;
        const [x, y] = B(t), L = size * (1 - 0.7 * t), w = L * 0.3, a = T(t) + sd * 68;
        const d = `M0,0 C${f(0.3 * L)},${f(-w)} ${f(0.75 * L)},${f(-0.8 * w)} ${f(L)},0 C${f(0.75 * L)},${f(0.8 * w)} ${f(0.3 * L)},${f(w)} 0,0Z`;
        leaves += `<path d="${d}" transform="translate(${f(x)} ${f(y)}) rotate(${f(a)})"/>`;
        veins += `<path d="M1,0 L${f(0.8 * L)},0" transform="translate(${f(x)} ${f(y)}) rotate(${f(a)})"/>`;
      }
    }
    const [ex, ey] = P2, a2 = T(1) * Math.PI / 180, ca = Math.cos(a2), sa = Math.sin(a2);
    const pt = (u, v) => `${f(ex + u * ca - v * sa)},${f(ey + u * sa + v * ca)}`;
    const s = size / 22;
    const curl = `M${f(ex)},${f(ey)} C${pt(8 * s, 0)} ${pt(12 * s, 8 * s)} ${pt(6 * s, 10 * s)} C${pt(2 * s, 10 * s)} ${pt(1 * s, 5 * s)} ${pt(5 * s, 5 * s)}`;
    const rib0 = `M${f(P0[0])},${f(P0[1])} Q${f(P1[0])},${f(P1[1])} ${f(P2[0])},${f(P2[1])}`;
    return `<g fill="none" stroke="${ink}" stroke-linecap="round"><path d="${rib0}" stroke-width="${uw + 2.5}"/><path d="${curl}" stroke-width="${uw + 2.5}"/></g>`
      + `<g fill="${ink}" stroke="${ink}" stroke-width="${uw}" stroke-linejoin="round">${leaves}</g>`
      + `<g fill="${fill}">${leaves}</g>`
      + `<g stroke="${opt.vein || '#127a44'}" stroke-width="1" opacity=".55">${veins}</g>`
      + `<g fill="none" stroke="${rib}" stroke-linecap="round"><path d="${rib0}" stroke-width="2.6"/><path d="${curl}" stroke-width="3.2"/></g>`;
  }

  // ── Цвіт папороті (символ bloom) — центр (50,47)
  function bloomBody() {
    let s = '';
    s += `<g transform="translate(50 47)"><g class="a-rays">` + rep(12, (i) => i % 2
      ? `<path d="M-1.8,-15 L0,-37 L1.8,-15Z" fill="url(#scl-ray)" transform="rotate(${i * 30 + 15})"/>`
      : `<path d="M-2.4,-15 L0,-50 L2.4,-15Z" fill="url(#scl-ray)" transform="rotate(${i * 30 + 15})"/>`) + `</g></g>`;
    s += `<circle cx="50" cy="47" r="30" fill="url(#scl-h-g)"/>`;
    s += leaf(48, 70, 30, 155) + leaf(52, 70, 30, 25);
    const op = rep(6, (i) => `<path d="M0,-4 C-10,-12 -12,-27 0,-43 C12,-27 10,-12 0,-4Z" transform="rotate(${i * 60})"/>`);
    const ip = rep(6, (i) => `<path d="M0,-3 C-6.5,-9 -7.5,-18 0,-27 C7.5,-18 6.5,-9 0,-3Z" transform="rotate(${i * 60 + 30})"/>`);
    s += `<g transform="translate(50 47)"><g class="a-pet">${under(op, 5.5)}<g fill="url(#scl-bp)" stroke="${INK}" stroke-width="1.4" stroke-opacity=".5">${op}</g>`
      + `<g stroke="#ffe9b0" stroke-width="1.6" opacity=".6" stroke-linecap="round">${rep(6, (i) => `<path d="M0,-12 L0,-33" transform="rotate(${i * 60})"/>`)}</g>`
      + `<g fill="url(#scl-bp2)" stroke="${INK}" stroke-width="2" stroke-linejoin="round">${ip}</g></g>`
      + `<g class="a-core"><circle r="14" fill="url(#scl-h-wh)"/>`
      + rep(6, (i) => `<g transform="rotate(${i * 60 + 30})"><path d="M0,-6 L0,-16" stroke="#fff3a0" stroke-width="1.7" stroke-linecap="round"/><circle cy="-16.5" r="2" fill="#fff" stroke="#ff9a2a" stroke-width=".8"/></g>`)
      + `<circle r="7.5" fill="url(#scl-bcore)" stroke="${INK}" stroke-width="2"/><circle cx="-2.2" cy="-2.4" r="2.2" fill="#fff"/></g></g>`;
    s += twk(18, 22, 0.5) + twk(84, 30, 0.42) + twk(80, 78, 0.36, '#ffd0ef');
    return s;
  }

  // ── Символи
  const SYM = {};
  SYM.f1 = { name: 'Ромашка', tier: 'low', svg: sym('f1', (() => {
    const back = rep(12, (i) => `<path d="${dPet}" transform="rotate(${i * 30 + 15}) scale(.86)"/>`);
    const front = rep(12, (i) => `<path d="${dPet}" transform="rotate(${i * 30})"/>`);
    return HALO('scl-h-w') + SH(93, 26) + `<g transform="translate(50 50)"><g class="a-pet">${under(back + front, 6)}`
      + `<g fill="url(#scl-daisy2)">${back}</g><g fill="url(#scl-daisy)" stroke="#9aa6dc" stroke-width="1.2">${front}</g>`
      + `<g stroke="#b3bfee" stroke-width="1.5" stroke-linecap="round">${rep(12, (i) => `<path d="M0,-15 L0,-31" transform="rotate(${i * 30})"/>`)}</g></g>`
      + `<g class="a-core"><circle r="13.5" fill="url(#scl-dc)" stroke="${INK}" stroke-width="3"/>`
      + rep(10, (i) => { const a = i * 36 * Math.PI / 180; return `<circle cx="${f(8 * Math.cos(a))}" cy="${f(8 * Math.sin(a))}" r="1.3" fill="#c25a08" opacity=".7"/>`; })
      + `<ellipse cx="-4" cy="-5" rx="5" ry="3" fill="#fff" opacity=".7" transform="rotate(-30 -4 -5)"/></g></g>` + GLINT(26, 24);
  })()) };

  SYM.f2 = { name: 'Волошка', tier: 'low', svg: sym('f2', (() => {
    const fa = [-78, -52, -26, 0, 26, 52, 78], fl = [30, 35, 39, 42, 39, 35, 30];
    const ba = [-65, -39, -13, 13, 39, 65], bl = [32, 37, 40, 40, 37, 32];
    const front = fa.map((a, i) => `<path d="${floret(fl[i])}" transform="rotate(${a})"/>`).join('');
    const back = ba.map((a, i) => `<path d="${floret(bl[i])}" transform="rotate(${a})"/>`).join('');
    return HALO('scl-h-b') + SH(94, 18) + `<g class="a-fan"><g transform="translate(50 56)">${under(back + front, 6)}`
      + `<g fill="url(#scl-corn2)" stroke="${INK}" stroke-width="1.2" stroke-opacity=".6">${back}</g>`
      + `<g fill="url(#scl-corn)" stroke="${INK}" stroke-width="1.4" stroke-opacity=".7">${front}</g>`
      + `<g stroke="#2a3cb0" stroke-width="1.4" opacity=".45">${fa.map((a, i) => `<path d="M0,-5 L0,${-0.8 * fl[i]}" transform="rotate(${a})"/>`).join('')}</g>`
      + `<path d="M-6,-37 L-4,-32" stroke="#e8f4ff" stroke-width="2.4" stroke-linecap="round"/>`
      + `<circle r="9.5" fill="#5a2aa0" stroke="${INK}" stroke-width="2.5"/>`
      + rep(7, (i) => { const a = (i * 51) * Math.PI / 180; return `<circle cx="${f(4.6 * Math.cos(a))}" cy="${f(4.6 * Math.sin(a) - 1)}" r="1.9" fill="#c9a8ff"/>`; }) + `</g></g>`
      + `<path d="M50,86 L50,96" stroke="${INK}" stroke-width="6" stroke-linecap="round"/><path d="M50,86 L50,95" stroke="#2f9a52" stroke-width="2.8" stroke-linecap="round"/>`
      + `<path d="M38,66 C37,80 44,88 50,88 C56,88 63,80 62,66 C57,62 43,62 38,66Z" fill="url(#scl-leaf)" stroke="${INK}" stroke-width="3" stroke-linejoin="round"/>`
      + `<g fill="none" stroke="#0e4a2a" stroke-width="1.3" opacity=".65">` + [[44, 70], [50, 70], [56, 70], [47, 76], [53, 76], [50, 82]].map(([x, y]) => `<path d="M${x - 3},${y} Q${x},${y + 3.5} ${x + 3},${y}"/>`).join('') + `</g>`
      + `<ellipse cx="44" cy="70" rx="2.6" ry="4" fill="#d6ffb0" opacity=".55"/>` + GLINT(30, 22);
  })()) };

  SYM.f3 = { name: 'Мак', tier: 'low', svg: sym('f3', (() => {
    const back = [45, 225].map((a) => `<path d="${pPet}" transform="rotate(${a}) scale(.96)"/>`).join('');
    const front = [135, 315].map((a) => `<path d="${pPet}" transform="rotate(${a})"/>`).join('');
    const veins = [45, 135, 225, 315].map((a) => `<g transform="rotate(${a})"><path d="M0,-8 C-4,-18 -10,-27 -15,-34 M0,-8 L0,-38 M0,-8 C4,-18 10,-27 15,-34"/></g>`).join('');
    const blot = [45, 135, 225, 315].map((a) => `<path d="M-10,-7 C-9,-16 9,-16 10,-7 C5,-3 -5,-3 -10,-7Z" transform="rotate(${a})"/>`).join('');
    return HALO('scl-h-r') + SH(94, 28) + `<g transform="translate(50 50)"><g class="a-pet">${under(back + front, 6)}`
      + `<g fill="url(#scl-poppy2)">${back}</g><g fill="url(#scl-poppy)" stroke="${INK}" stroke-width="1.6" stroke-opacity=".55">${front}</g>`
      + `<g fill="none" stroke="#9e1026" stroke-width="1.4" opacity=".45">${veins}</g><g fill="#2a0a22" opacity=".85">${blot}</g>`
      + `<path d="M-31,-14 C-31,-24 -25,-31 -16,-34" stroke="#ffd2bc" stroke-width="3.6" fill="none" stroke-linecap="round" opacity=".8"/></g>`
      + `<g class="a-core"><circle r="15" fill="#1c0d2c" stroke="${INK}" stroke-width="2"/>`
      + rep(18, (i) => `<ellipse cx="0" cy="-12.2" rx="1.5" ry="2.8" fill="#4a2470" transform="rotate(${i * 20})"/>`)
      + rep(9, (i) => `<circle cx="0" cy="-10.5" r="1.1" fill="#9b7ad0" transform="rotate(${i * 40 + 10})"/>`)
      + `<circle r="8.5" fill="url(#scl-pc)" stroke="${INK}" stroke-width="2"/>`
      + `<g stroke="#3a1a4a" stroke-width="1.3" stroke-linecap="round">${rep(8, (i) => `<path d="M0,0 L0,-7.5" transform="rotate(${i * 45})"/>`)}</g><circle r="2" fill="#5a3a6a"/></g></g>` + GLINT(26, 26);
  })()) };

  SYM.f4 = { name: 'Соняшник', tier: 'low', svg: sym('f4', (() => {
    const back = rep(16, (i) => `<path d="${sPet}" transform="rotate(${i * 22.5 + 11.25}) scale(.93)"/>`);
    const front = rep(16, (i) => `<path d="${sPet}" transform="rotate(${i * 22.5})"/>`);
    let seeds = '';
    for (let i = 2; i < 80; i++) { const r = 2.45 * Math.sqrt(i); if (r > 19) break; const a = i * 137.508 * Math.PI / 180; seeds += `<circle cx="${f(r * Math.cos(a))}" cy="${f(r * Math.sin(a))}" r="1.25"/>`; }
    return HALO('scl-h-y') + SH(94, 28) + `<g transform="translate(50 50) scale(.96)"><g class="a-pet">${under(back + front, 5.5)}`
      + `<g fill="url(#scl-sunp2)">${back}</g><g fill="url(#scl-sunp)" stroke="${INK}" stroke-width="1.2" stroke-opacity=".5">${front}</g>`
      + `<g stroke="#fff8c0" stroke-width="1.5" opacity=".75" stroke-linecap="round">${rep(16, (i) => `<path d="M0,-23 L0,-38" transform="rotate(${i * 22.5})"/>`)}</g></g>`
      + `<g class="a-core"><circle r="22" fill="url(#scl-sunc)" stroke="${INK}" stroke-width="3"/><circle r="18.5" fill="none" stroke="#a8622a" stroke-width="1.5" opacity=".6"/>`
      + `<g fill="#2a1206">${seeds}</g><ellipse cx="-7" cy="-8" rx="8" ry="5" fill="#fff" opacity=".22" transform="rotate(-35 -7 -8)"/></g></g>` + GLINT(24, 24);
  })()) };

  SYM.wreath = { name: 'Вінок', tier: 'high', svg: sym('wreath', (() => {
    const rib = [['M44,64 C39,76 46,82 39,95', '#e8323a'], ['M48,66 C48,78 52,84 48,96', '#ffc93a'], ['M53,66 C55,77 51,85 57,96', '#3a7bff'], ['M57,64 C63,74 58,82 64,93', '#36b45c']];
    let s = HALO('scl-h-g', 48, 50, 46) + SH(95, 24);
    s += `<g class="a-rib">` + rib.map(([d]) => `<path d="${d}" stroke="${INK}" stroke-width="7.5" fill="none" stroke-linecap="round"/>`).join('') + rib.map(([d, c]) => `<path d="${d}" stroke="${c}" stroke-width="4.4" fill="none" stroke-linecap="round"/>`).join('') + `</g>`;
    let ring = `<circle cx="50" cy="44" r="27" fill="none" stroke="${INK}" stroke-width="19"/><circle cx="50" cy="44" r="27" fill="none" stroke="#1d6a3e" stroke-width="14"/>`;
    ring += rep(20, (i) => { const a = i * 18 * Math.PI / 180, R = 27 + (i % 2 ? 4 : -3.5); const x = 50 + R * Math.cos(a), y = 44 + R * Math.sin(a); return `<ellipse cx="${f(x)}" cy="${f(y)}" rx="9" ry="4.2" transform="rotate(${f(i * 18 + 90 + (i % 2 ? 28 : -28))} ${f(x)} ${f(y)})" fill="url(#scl-leaf)" stroke="${INK}" stroke-width="1.8"/>`; });
    const fls = [[-90, 'p', 0.42], [-128, 'd', 0.36], [-52, 'd', 0.36], [-160, 'c', 0.33], [-20, 'c', 0.33], [160, 's', 0.28], [20, 's', 0.28], [90, 'p', 0.34]];
    ring += fls.map(([a, k, sc]) => at(50 + 27 * Math.cos(a * Math.PI / 180), 44 + 27 * Math.sin(a * Math.PI / 180), sc, a + 90, MINI[k])).join('');
    s += `<circle cx="50" cy="44" r="21" fill="#2a4aa8"/><circle cx="50" cy="44" r="21" fill="url(#scl-h-b)" opacity=".8"/><circle cx="50" cy="46" r="17" fill="url(#scl-h-g)" opacity=".75"/><ellipse cx="44" cy="37" rx="6" ry="3" fill="#ffe9a8" opacity=".55" transform="rotate(-20 44 37)"/><path d="M41,53 Q50,50 59,53" stroke="#9cc0ff" stroke-width="1.4" fill="none" stroke-linecap="round" opacity=".45"/>`;
    s += `<g class="a-ring">${ring}</g>` + GLINT(24, 20);
    return s;
  })()) };

  SYM.candle = { name: 'Свічка', tier: 'high', svg: sym('candle', (() => {
    let s = HALO('scl-h-g', 46, 50, 32) + SH(95, 28);
    const ell = (i, n) => { const a = i / n * Math.PI * 2; return [50 + 26 * Math.cos(a), 87 + 7 * Math.sin(a), a]; };
    let back = '', front = '';
    for (let i = 0; i < 14; i++) { const [x, y, a] = ell(i, 14); const l = `<ellipse cx="${f(x)}" cy="${f(y)}" rx="8" ry="3.6" transform="rotate(${f(a * 180 / Math.PI + 90 + (i % 2 ? 30 : -30))} ${f(x)} ${f(y)})" fill="url(#scl-leaf)" stroke="${INK}" stroke-width="1.6"/>`; if (Math.sin(a) < 0) back += l; else front += l; }
    s += back;
    s += `<path d="M37,46 L37,84 C37,89 63,89 63,84 L63,46 Z" fill="url(#scl-wax)" stroke="${INK}" stroke-width="3" stroke-linejoin="round"/>`;
    s += `<rect x="37" y="72" width="26" height="5" fill="#d8313a" stroke="${INK}" stroke-width="1.4"/>` + rep(5, (i) => `<rect x="${39.6 + i * 5}" y="73.4" width="2.2" height="2.2" fill="#fff4d0" transform="rotate(45 ${40.7 + i * 5} 74.5)"/>`);
    s += `<g transform="translate(50 64)">` + [-42, 0, 42].map((r) => `<path d="M0,0 C-3,-3 -2.5,-7.5 0,-9 C2.5,-7.5 3,-3 0,0Z" transform="rotate(${r})" fill="#e23a3a" stroke="${INK}" stroke-width=".9"/>`).join('')
      + `<path d="M-2,2 C-6,2 -9,0 -10,-2 M2,2 C6,2 9,0 10,-2" stroke="#2f9a52" stroke-width="2" fill="none" stroke-linecap="round"/><circle r="1.6" fill="#ffd23a"/></g>`;
    s += `<rect x="40.5" y="51" width="3.6" height="30" rx="1.8" fill="#fff" opacity=".6"/>`;
    s += `<ellipse cx="50" cy="46" rx="13" ry="4.5" fill="#fff3d2" stroke="${INK}" stroke-width="2.5"/><ellipse cx="50" cy="46.6" rx="8.5" ry="2.4" fill="#ffcf72" opacity=".75"/>`;
    s += `<path d="M37,46 C37,52 37,60 39.5,60 C42,60 42,55 42,50 C43,53 45,53 45,49 Z" fill="#fff5dc" stroke="${INK}" stroke-width="2" stroke-linejoin="round"/>`;
    s += `<path d="M57,48.5 C57,54 58,58 60.2,58 C62.5,58 63,53 63,46 Z" fill="#fff0cc" stroke="${INK}" stroke-width="2" stroke-linejoin="round"/>`;
    s += front + at(36, 92, 0.24, 0, MINI.p) + at(64, 92, 0.22, 0, MINI.c) + at(50, 95, 0.2, 0, MINI.d);
    s += `<path d="M50,44 L50,37" stroke="#3a2210" stroke-width="2.4" stroke-linecap="round"/>`;
    s += `<g class="a-flick">${flame()}</g>` + GLINT(28, 52, 0.8);
    return s;
  })()) };

  SYM.fire = { name: 'Купальський вогник', tier: 'high', svg: sym('fire', HALO('scl-h-o', 48, 50, 54) + SH(94, 34)
    + fireFlames() + fireLogs()
    + `<g class="a-sp" fill="#ffe27a"><circle cx="28" cy="30" r="2.1"/><circle cx="73" cy="22" r="1.7"/><circle cx="64" cy="10" r="1.3"/><circle cx="34" cy="14" r="1.5"/></g>` + GLINT(30, 40, 0.8)) };

  SYM.comb = { name: 'Русалчин гребінь', tier: 'high', svg: sym('comb', HALO('scl-h-aq') + `<circle cx="50" cy="36" r="24" fill="url(#scl-h-m)" opacity=".6"/>` + SH(94, 30)
    + `<g class="a-comb"><g transform="rotate(-9 50 52)">${combBody()}</g></g>` + twk(84, 18, 0.45, '#e9fbff') + GLINT(26, 22)) };

  SYM.fern = { name: 'Листок папороті', tier: 'wild', svg: sym('fern', (() => {
    let s = `<circle class="a-halo" cx="50" cy="48" r="50" fill="url(#scl-h-fern)"/>`;
    s += `<circle cx="50" cy="48" r="41" fill="url(#scl-badge)" stroke="${INK}" stroke-width="6"/><circle cx="50" cy="48" r="41" fill="none" stroke="url(#scl-gold)" stroke-width="3"/>`;
    s += `<circle cx="50" cy="48" r="35.5" fill="none" stroke="#3fd17a" stroke-width="1.2" opacity=".4"/><circle cx="50" cy="46" r="30" fill="url(#scl-h-fern)" opacity=".7"/>`;
    s += `<g class="a-frond">${frond([50, 78], [34, 50], [54, 20], 22)}</g>`;
    s += twk(25, 30, 0.42) + twk(77, 38, 0.36) + twk(72, 64, 0.3);
    s += `<path d="M12,77 L24,75 L24,90 L12,92 L17,84.5Z" fill="#b07c10" stroke="${INK}" stroke-width="2" stroke-linejoin="round"/><path d="M88,77 L76,75 L76,90 L88,92 L83,84.5Z" fill="#b07c10" stroke="${INK}" stroke-width="2" stroke-linejoin="round"/>`;
    s += `<path d="M20,73 C36,77 64,77 80,73 L80,88 C64,92 36,92 20,88Z" fill="url(#scl-gold)" stroke="${INK}" stroke-width="2.6" stroke-linejoin="round"/>`;
    s += `<text x="50" y="86.2" text-anchor="middle" font-family="${FONT}" font-weight="900" font-size="11.5" fill="#4a2508" letter-spacing=".6">ДИКИЙ</text>`;
    return s + GLINT(30, 26, 0.9);
  })()) };

  SYM.bloom = { name: 'Цвіт папороті', tier: 'special', svg: sym('bloom', `<circle class="a-halo" cx="50" cy="47" r="50" fill="url(#scl-h-m)"/><g transform="translate(50 48) scale(1.1) translate(-50 -47)">` + bloomBody() + `</g>` + GLINT(30, 30, 0.8)) };

  // ── Плаваючий вінок зі свічкою (для сцен і афіші), центр — (0,0) на воді
  function wreathFloat() {
    let back = '', front = '';
    for (let i = 0; i < 18; i++) {
      const a = i / 18 * Math.PI * 2, x = 44 * Math.cos(a), y = 14 * Math.sin(a);
      const l = `<ellipse cx="${f(x)}" cy="${f(y)}" rx="11" ry="4.6" transform="rotate(${f(Math.atan2(14 * Math.cos(a), -44 * Math.sin(a)) * 180 / Math.PI + (i % 2 ? 24 : -24))} ${f(x)} ${f(y)})" fill="url(#scl-leaf)" stroke="${INK}" stroke-width="2"/>`;
      if (y < 0) back += l; else front += l;
    }
    const fl = (deg, k, sc) => at(44 * Math.cos(deg * Math.PI / 180), 14 * Math.sin(deg * Math.PI / 180), sc, 0, MINI[k]);
    back += fl(-120, 's', 0.26) + fl(-60, 'd', 0.26) + fl(-90, 'c', 0.24);
    front += fl(180, 'c', 0.3) + fl(0, 'c', 0.3) + fl(140, 'd', 0.34) + fl(40, 'd', 0.34) + fl(90, 'p', 0.42);
    const candle = `<path d="M-8,-44 L-8,-2 C-8,3 8,3 8,-2 L8,-44Z" fill="url(#scl-wax)" stroke="${INK}" stroke-width="2.4"/><rect x="-8" y="-20" width="16" height="4" fill="#d8313a"/>`
      + `<ellipse cx="0" cy="-44" rx="8" ry="2.8" fill="#fff3d2" stroke="${INK}" stroke-width="1.8"/><path d="M0,-46 L0,-50" stroke="#3a2210" stroke-width="2"/>`
      + `<g transform="translate(0 -48) scale(.62) translate(-50 -38)">${flame()}</g>`;
    return `<ellipse cx="0" cy="26" rx="56" ry="12" fill="url(#scl-h-g)" opacity=".8"/><ellipse cx="0" cy="6" rx="58" ry="20" fill="#071030" opacity=".35"/>`
      + `<circle cx="0" cy="-60" r="58" fill="url(#scl-h-g)"/>` + back + candle + front;
  }

  // ── Ялинка-силует
  function fir(x, by, h, w) {
    const n = 4, top = by - h, H = h * 0.86, R = [], Lf = [];
    for (let i = 1; i <= n; i++) {
      const y = top + H * i / n, hw = w / 2 * (0.32 + 0.68 * i / n);
      R.push([x + hw, y]); Lf.push([x - hw, y]);
      if (i < n) { R.push([x + hw * 0.42, y - h * 0.035]); Lf.push([x - hw * 0.42, y - h * 0.035]); }
    }
    const tw = w * 0.06, yl = top + H;
    const pts = [[x, top], ...R, [x + tw, yl], [x + tw, by], [x - tw, by], [x - tw, yl], ...Lf.reverse()];
    return 'M' + pts.map((p) => f(p[0]) + ',' + f(p[1])).join(' L') + 'Z';
  }
  function forest(seed, x0, x1, by, hmin, hmax, step, jitterY = 0) {
    const r = rng(seed); let d = '';
    for (let x = x0; x < x1; x += step * (0.6 + r() * 0.6)) { const h = hmin + r() * (hmax - hmin); d += fir(x, by + r() * jitterY, h, h * (0.42 + r() * 0.1)); }
    return d;
  }
  function stars(seed, n, w, h, rmax = 1.8) {
    const r = rng(seed); let s = '';
    for (let i = 0; i < n; i++) s += `<circle cx="${f(r() * w)}" cy="${f(r() * h)}" r="${f(0.6 + r() * rmax)}" opacity="${f(0.35 + r() * 0.6)}"/>`;
    return `<g fill="#fff6dc">${s}</g>`;
  }
  const ffly = (x, y, s = 1) => `<g transform="translate(${f(x)} ${f(y)}) scale(${s})"><circle r="9" fill="url(#scl-h-fly)"/><circle r="2.2" fill="#f8ffc4"/></g>`;
  const moon = (x, y, r) => `<circle cx="${x}" cy="${y}" r="${r * 4.2}" fill="url(#scl-moonh)"/><circle cx="${x}" cy="${y}" r="${r}" fill="url(#scl-moon)"/>`
    + `<g fill="#e8c878" opacity=".45"><circle cx="${f(x - r * 0.3)}" cy="${f(y - r * 0.2)}" r="${f(r * 0.18)}"/><circle cx="${f(x + r * 0.28)}" cy="${f(y + r * 0.25)}" r="${f(r * 0.24)}"/><circle cx="${f(x + r * 0.35)}" cy="${f(y - r * 0.35)}" r="${f(r * 0.1)}"/><circle cx="${f(x - r * 0.25)}" cy="${f(y + r * 0.45)}" r="${f(r * 0.09)}"/></g>`;
  const MOON_DEFS = rg('scl-moon', [[0, '#fffdf0'], [0.7, '#fff0b8'], [1, '#f2cf72']], 0.42, 0.4, 0.6)
    + rg('scl-moonh', [[0, '#ffe9a8', 0.55], [0.25, '#ffe9a8', 0.22], [0.6, '#c8a8ff', 0.08], [1, '#c8a8ff', 0]]);
  // очерет
  function reeds(seed, x0, x1, by, col) {
    const r = rng(seed); let s = '';
    for (let x = x0; x < x1; x += 14 + r() * 18) {
      const h = 90 + r() * 140, lean = (r() - 0.5) * 40;
      s += `<path d="M${f(x)},${by} Q${f(x + lean * 0.3)},${f(by - h * 0.6)} ${f(x + lean)},${f(by - h)}" stroke="${col}" stroke-width="${f(3 + r() * 2)}" fill="none"/>`;
      if (r() < 0.45) s += `<rect x="${f(x + lean * 0.8 - 5)}" y="${f(by - h * 0.92)}" width="10" height="${f(28 + r() * 14)}" rx="5" fill="#2a1630" transform="rotate(${f(lean * 0.3)} ${f(x + lean * 0.8)} ${f(by - h * 0.8)})"/>`;
      else s += `<path d="M${f(x)},${by} Q${f(x + lean)},${f(by - h * 0.5)} ${f(x + lean * 2.2)},${f(by - h * 0.7)} Q${f(x + lean * 0.6)},${f(by - h * 0.4)} ${f(x + 6)},${by}Z" fill="${col}"/>`;
    }
    return s;
  }
  function lily(x, y, s) {
    return at(x, y, s, 0, `<path d="M0,0 L22,-6 A26,9 0 1 1 16,6 Z" fill="#1f6a4a" stroke="${INK}" stroke-width="2"/><path d="M-14,-2 C-6,-5 6,-5 14,-2" stroke="#3fae6a" stroke-width="1.5" fill="none" opacity=".6"/>`)
  }
  function waterLily(x, y, s) {
    const p = rep(8, (i) => `<path d="M0,0 C-5,-4 -4,-14 0,-17 C4,-14 5,-4 0,0Z" transform="rotate(${-80 + i * 22.8}) scale(1 .7)"/>`);
    return at(x, y, s, 0, `<ellipse cx="0" cy="3" rx="26" ry="7" fill="#1f6a4a" stroke="${INK}" stroke-width="2"/>` + under(p, 4) + `<g fill="url(#scl-daisy)">${p}</g><circle cy="-3" r="3.5" fill="#ffd23a"/><circle cy="-2" r="16" fill="url(#scl-h-w)" opacity=".5"/>`);
  }

  // ── Сцена: купальська ніч
  const SCENE_BASE = (() => {
    const defs = MOON_DEFS
      + lg('scl-sky', [[0, '#04051a'], [0.42, '#151448'], [0.72, '#33205f'], [1, '#5a2a6c']])
      + lg('scl-riv', [[0, '#3a3a86'], [0.25, '#1b2c70'], [1, '#08113a']])
      + lg('scl-bank', [[0, '#1a1546'], [1, '#090722']])
      + lg('scl-mist', [[0, '#7a5ab8', 0], [0.5, '#8a6ac8', 0.35], [1, '#7a5ab8', 0]])
      + glow('scl-fglow', '#ff7a2a') + lg('scl-mref', [[0, '#fff1c0', 0.95], [1, '#ffe9a8', 0.15]]);
    let b = `<rect width="1600" height="900" fill="url(#scl-sky)"/>` + stars(11, 120, 1600, 470);
    b += `<g class="sc-tw" fill="#fff8e0">${[[640, 70], [1060, 110], [1240, 60], [420, 140], [1400, 210], [560, 260], [1120, 300], [300, 60]].map(([x, y], i) => at(x, y, 0.45 + (i % 3) * 0.15, 0, `<path d="${STAR4}"/>`)).join('')}</g>`;
    b += moon(820, 160, 62);
    b += `<g fill="#6a4aa8" opacity=".28"><ellipse cx="700" cy="215" rx="190" ry="12"/><ellipse cx="960" cy="240" rx="230" ry="10"/><ellipse cx="1300" cy="150" rx="160" ry="9"/><ellipse cx="300" cy="190" rx="200" ry="11"/></g>`;
    b += `<path d="${forest(3, -30, 1640, 528, 40, 110, 30, 10)}" fill="#251c5a"/>`;
    b += `<rect x="0" y="470" width="1600" height="90" fill="url(#scl-mist)"/>`;
    b += `<path d="${forest(7, -40, 650, 600, 150, 280, 44, 30)}" fill="#160f3c"/><path d="${forest(8, 1000, 1660, 600, 150, 280, 44, 30)}" fill="#160f3c"/>`;
    b += `<path d="M0,560 L700,522 C640,620 420,770 250,900 L0,900Z" fill="url(#scl-bank)"/><path d="M930,522 L1600,560 L1600,900 L1410,900 C1240,770 1010,640 930,522Z" fill="url(#scl-bank)"/>`;
    b += `<path d="M700,522 L930,522 C1010,640 1240,770 1410,900 L250,900 C420,770 640,620 700,522Z" fill="url(#scl-riv)"/>`;
    b += `<path d="M700,522 C640,620 420,770 250,900" stroke="#5b4ab0" stroke-width="3" fill="none" opacity=".5"/><path d="M930,522 C1010,640 1240,770 1410,900" stroke="#5b4ab0" stroke-width="3" fill="none" opacity=".5"/>`;
    b += `<ellipse cx="820" cy="700" rx="120" ry="200" fill="url(#scl-moonh)" opacity=".7"/>`;
    b += `<g class="sc-moonr" fill="#ffe6a0">` + rep(10, (i) => { const y = 538 + i * i * 3.4 + i * 8, w = 30 + i * 15 + (i % 3) * 10; return `<ellipse cx="${f(820 + (i % 2 ? 12 : -12) + (i % 3 - 1) * 6)}" cy="${f(y)}" rx="${f(w / 2)}" ry="${f(1.6 + i * 0.5)}" opacity="${f(0.9 - i * 0.065)}"/>`; }) + `</g>`;
    const cub = (P, t) => [0, 1].map((k) => (1 - t) ** 3 * P[0][k] + 3 * (1 - t) ** 2 * t * P[1][k] + 3 * (1 - t) * t * t * P[2][k] + t ** 3 * P[3][k]);
    const rr = rng(91); let tufts = '';
    for (const E of [[[700, 522], [640, 620], [420, 770], [250, 900]], [[930, 522], [1010, 640], [1240, 770], [1410, 900]]]) {
      for (let t = 0.02; t < 1; t += 0.035 + rr() * 0.03) {
        const [x, y] = cub(E, t), h = 8 + t * 34 + rr() * 10, w = 3 + t * 6;
        tufts += `<path d="M${f(x - w * 2)},${f(y + 2)} L${f(x - w * 1.2)},${f(y - h * 0.7)} L${f(x - w * 0.4)},${f(y)} L${f(x + w * 0.2)},${f(y - h)} L${f(x + w * 0.9)},${f(y)} L${f(x + w * 1.6)},${f(y - h * 0.6)} L${f(x + w * 2.2)},${f(y + 2)}Z"/>`;
      }
    }
    b += `<g fill="#0d0a2c">${tufts}</g>`;
    b += `<ellipse cx="560" cy="760" rx="160" ry="40" fill="url(#scl-fglow)" opacity=".45"/>`;
    b += `<g class="sc-rip" stroke="#8fa8ff" stroke-width="2" opacity=".35" stroke-linecap="round">` + [[620, 640, 80], [1020, 610, 60], [560, 760, 120], [1120, 740, 100], [880, 850, 140]].map(([x, y, w]) => `<path d="M${x},${y} l${w},0"/>`).join('') + `</g>`;
    b += lily(560, 820, 1.1) + lily(1250, 820, 1.2) + lily(1180, 760, 0.8) + waterLily(500, 860, 1.3) + waterLily(1310, 870, 1.1);
    // вогнище на лівому березі
    b += `<g class="sc-fglow"><ellipse cx="360" cy="690" rx="260" ry="120" fill="url(#scl-fglow)"/></g>`;
    b += `<g fill="#2a2050" stroke="${INK}" stroke-width="3">` + [[300, 700], [330, 712], [370, 715], [410, 710], [436, 698]].map(([x, y]) => `<ellipse cx="${x}" cy="${y}" rx="18" ry="11"/>`).join('') + `</g>`;
    b += `<g transform="translate(250 524) scale(2.2)"><g class="sc-fire">${fireFlames(false)}</g>${fireLogs()}</g>`;
    b += `<g class="sc-spk" fill="#ffd56a">` + [[340, 470], [392, 430], [360, 380], [410, 340], [330, 300], [380, 255]].map(([x, y], i) => `<circle cx="${x}" cy="${y}" r="${3 - i * 0.3}"/>`).join('') + `</g>`;
    // вінки на воді
    b += `<g transform="translate(680 596) scale(.5)"><g class="sc-w1">${wreathFloat()}</g></g>`;
    b += `<g transform="translate(960 690) scale(.78)"><g class="sc-w2">${wreathFloat()}</g></g>`;
    b += `<g transform="translate(700 815) scale(1.15)"><g class="sc-w3">${wreathFloat()}</g></g>`;
    b += `<g fill="#0a0820">${reeds(21, -10, 260, 905, '#0c0a26')}${reeds(22, 1360, 1620, 905, '#0c0a26')}</g>`;
    b += `<path d="${forest(9, -60, 150, 940, 640, 820, 60)}" fill="#07061a"/><path d="${forest(10, 1480, 1680, 940, 640, 820, 60)}" fill="#07061a"/>`;
    b += [[[480, 420], [530, 470], [450, 520]], [[1080, 430], [1150, 480], [1100, 380]], [[620, 360], [1000, 520], [880, 400]]].map((g, i) => `<g class="sc-ff${i + 1}">${g.map(([x, y]) => ffly(x, y, 1.1)).join('')}</g>`).join('');
    return own('sclS', 'viewBox="0 0 1600 900" preserveAspectRatio="xMidYMid slice" class="scl-scene scl-scene-base" aria-hidden="true"', defs, b);
  })();

  // ── Бонусна сцена: Цвіт папороті в лісовій гущі
  const SCENE_BONUS = (() => {
    const defs = MOON_DEFS
      + lg('scl-bsky', [[0, '#03040f'], [0.5, '#120e38'], [1, '#2a1150']])
      + rg('scl-bglow', [[0, '#fff2b0', 0.9], [0.12, '#ffd04a', 0.6], [0.35, '#ff4fa0', 0.28], [0.7, '#7a3aff', 0.08], [1, '#3a1aa0', 0]])
      + lg('scl-bray', [[0, '#fff3b0', 0], [1, '#ffe08a', 0.4]])
      + lg('scl-floor', [[0, '#1a1240'], [1, '#05040f']])
      + lg('scl-fog', [[0, '#9a6ad8', 0], [0.5, '#b07ae8', 0.3], [1, '#9a6ad8', 0]])
      + lg('scl-trunk', [[0, '#0d0a28'], [0.5, '#1d1650'], [1, '#0a0820']], 0, 0, 1, 0);
    let b = `<rect width="1600" height="900" fill="url(#scl-bsky)"/>` + stars(31, 50, 1600, 300, 1.2);
    b += moon(1220, 110, 34);
    const r = rng(5); let tr = '';
    for (let i = 0; i < 26; i++) { const x = r() * 1600, w = 14 + r() * 22; tr += `<path d="M${f(x - w / 2)},900 L${f(x - w * 0.35)},0 L${f(x + w * 0.35)},0 L${f(x + w / 2)},900Z"/>`; }
    b += `<g fill="#2a2068" opacity=".75">${tr}</g>`;
    b += `<path d="${forest(41, -40, 1640, 600, 160, 300, 40, 20)}" fill="#1e1752"/>`;
    b += `<rect x="0" y="420" width="1600" height="200" fill="url(#scl-fog)"/>`;
    b += `<g class="sb-glow"><circle cx="800" cy="480" r="620" fill="url(#scl-bglow)"/></g>`;
    b += `<g transform="translate(800 480)"><g class="sb-rays">` + rep(18, (i) => `<path d="M-14,-70 L0,-760 L14,-70Z" fill="url(#scl-bray)" transform="rotate(${i * 20}) scale(1 -1)"/>`) + `</g></g>`;
    let tr2 = '';
    for (const [x, w] of [[120, 70], [330, 50], [520, 40], [1080, 44], [1280, 56], [1490, 80]]) tr2 += `<path d="M${x - w / 2},900 C${x - w * 0.3},600 ${x - w * 0.35},300 ${x - w * 0.3},0 L${x + w * 0.3},0 C${x + w * 0.35},300 ${x + w * 0.3},600 ${x + w / 2},900Z"/>`;
    b += `<g fill="url(#scl-trunk)">${tr2}</g>`;
    b += `<path d="M0,690 C300,650 600,670 800,660 C1000,650 1300,660 1600,690 L1600,900 L0,900Z" fill="url(#scl-floor)"/>`;
    // папороть навколо
    const dk = { fill: '#123a30', ink: '#060818', rib: '#2a6a4a', vein: '#0a2a20', uw: 3 };
    const lt = { fill: 'url(#scl-fern)', ink: INK, rib: '#e2ff9a', uw: 3 };
    let ferns = '';
    for (const [x, y, s, a, o] of [[80, 900, 9, -30, dk], [240, 900, 8, 20, dk], [1350, 900, 9, 25, dk], [1520, 900, 8, -15, dk], [420, 900, 6, -10, dk], [1180, 900, 6, 15, dk]]) ferns += at(x, y, s, a, frond([0, 0], [-6, -14], [2, -30], 9, o));
    b += ferns;
    for (const [a, s] of [[-58, 6], [-30, 7], [0, 7.4], [30, 7], [58, 6]]) b += at(800, 820, s, a, frond([0, 0], [-6, -14], [2, -30], 9, lt));
    b += `<path d="M800,820 C796,720 804,640 800,560" stroke="${INK}" stroke-width="12" fill="none" stroke-linecap="round"/><path d="M800,820 C796,720 804,640 800,560" stroke="#7df06a" stroke-width="6" fill="none" stroke-linecap="round"/>`;
    b += `<g transform="translate(800 480)"><g class="sb-flower"><g transform="scale(3.4) translate(-50 -47)">${bloomBody()}</g></g></g>`;
    const ffg = [[[200, 520], [260, 600], [330, 470], [150, 380]], [[1400, 520], [1330, 600], [1460, 430], [1260, 470]], [[560, 380], [640, 300], [700, 580], [520, 620]], [[1000, 330], [930, 620], [1090, 580], [1040, 420]], [[400, 760], [1220, 760], [620, 720], [980, 740]]];
    b += ffg.map((g, i) => `<g class="sb-ff${i + 1}">${g.map(([x, y]) => ffly(x, y, 1.2)).join('')}</g>`).join('');
    b += `<g class="sb-m1" fill="#fff3b0">${[[760, 600], [840, 640], [720, 700], [880, 560], [790, 720]].map(([x, y]) => `<circle cx="${x}" cy="${y}" r="3"/>`).join('')}</g>`;
    b += `<g class="sb-m2" fill="#ffc0e8">${[[700, 620], [900, 680], [820, 600], [760, 660]].map(([x, y]) => `<circle cx="${x}" cy="${y}" r="2.4"/>`).join('')}</g>`;
    b += `<g fill="#05040f"><path d="M0,0 L1600,0 L1600,60 C1400,120 1200,40 1000,90 C800,40 600,110 400,70 C250,40 120,110 0,80Z"/></g>`;
    return own('sclB', 'viewBox="0 0 1600 900" preserveAspectRatio="xMidYMid slice" class="scl-scene scl-scene-bonus" aria-hidden="true"', defs, b);
  })();

  // ── Текст назви: слова → рядки, кегль за довжиною
  function titleLines(maxLines = 2) {
    const w = TITLE.trim().split(/\s+/);
    if (w.length <= 1 || maxLines < 2) return [TITLE.trim()];
    // ділимо так, щоб рядки були якомога рівніші
    let best = null;
    for (let k = 1; k < w.length; k++) { const a = w.slice(0, k).join(' '), b = w.slice(k).join(' '); const d = Math.max(a.length, b.length); if (!best || d < best.d) best = { a, b, d }; }
    return [best.a, best.b];
  }
  // рядок тексту «смачними літерами»: глоу + обведення + градієнт + блік; сам вписується в ширину w
  // шари: м'яке сяйво (еліпс) → темний «бортик» униз (об'єм) → кольоровий обідок → тонке обведення → градієнт → блік
  function tline(txt, cx, y, maxSize, w, grad, glowId, rim, ink = INK) {
    const est = txt.length * 0.62;                       // середня ширина літери Onest 900 у кеглях
    const size = Math.min(maxSize, w / est), tw = Math.min(w, est * size);
    const fit = est * size > w * 0.98 ? ` textLength="${f(w)}" lengthAdjust="spacingAndGlyphs"` : '';
    const t = (extra) => `<text x="${cx}" y="${y}" text-anchor="middle" font-family="${FONT}" font-weight="900" font-size="${f(size)}"${fit} ${extra}>${txt}</text>`;
    return `<ellipse cx="${cx}" cy="${f(y - size * 0.36)}" rx="${f(tw * 0.66)}" ry="${f(size * 0.85)}" fill="url(#${glowId})"/>`
      + t(`fill="${ink}" stroke="${ink}" stroke-width="${f(size * 0.26)}" stroke-linejoin="round" transform="translate(0 ${f(size * 0.06)})"`)
      + t(`fill="${rim}" stroke="${rim}" stroke-width="${f(size * 0.17)}" stroke-linejoin="round"`)
      + t(`fill="${ink}" stroke="${ink}" stroke-width="${f(size * 0.075)}" stroke-linejoin="round"`)
      + t(`fill="url(#${grad})"`)
      + t(`fill="none" stroke="#fff" stroke-width="${f(size * 0.02)}" opacity=".6" transform="translate(${f(-size * 0.01)} ${f(-size * 0.018)})"`);
  }
  const LOGO_DEFS = lg('scl-t1', [[0, '#fffbe2'], [0.46, '#ffe066'], [0.5, '#ffb52a'], [1, '#ff5a1a']])
    + lg('scl-t2', [[0, '#f4fff0'], [0.46, '#b8ff9a'], [0.5, '#5fe07a'], [1, '#1fa868']])
    + rg('scl-lg', [[0, '#ffcf5a', 0.4], [0.4, '#ff4fa0', 0.16], [1, '#7a3aff', 0]])
    + glow('scl-lg1', '#ff8a2a', 0.55) + glow('scl-lg2', '#4fff8a', 0.4);

  function logoBody(W, H) {
    const L = titleLines(2);
    let b = `<ellipse cx="${W / 2}" cy="${H / 2}" rx="${W * 0.5}" ry="${H * 0.5}" fill="url(#scl-lg)"/>`;
    if (L.length === 2) {
      // ліворуч від першого рядка — цвіт, по боках другого — листки папороті
      const s1 = Math.min(H * 0.42, (W * 0.66) / (L[0].length * 0.62)), w1 = L[0].length * 0.62 * s1;
      const bx = W / 2 - w1 / 2 - H * 0.05, cx1 = W / 2 + H * 0.12;
      b += at(bx, H * 0.27, H * 0.0058, -12, `<g transform="translate(-50 -47)">${bloomBody()}</g>`);
      const fr = frond([0, 0], [-6, -14], [2, -30], 9, { uw: 2.6 });
      b += at(W * 0.07, H * 1.0, H * 0.0105, -28, fr) + at(W * 0.93, H * 1.0, H * 0.0105, 28, `<g transform="scale(-1 1)">${fr}</g>`);
      b += tline(L[0], cx1, H * 0.46, H * 0.42, W * 0.64, 'scl-t1', 'scl-lg1', '#ffd25a');
      b += tline(L[1], W / 2, H * 0.88, H * 0.36, W * 0.8, 'scl-t2', 'scl-lg2', '#c8ffd8');
    } else {
      b += at(W * 0.5, H * 0.2, H * 0.004, 0, `<g transform="translate(-50 -47)">${bloomBody()}</g>`);
      b += tline(L[0], W / 2, H * 0.78, H * 0.5, W * 0.92, 'scl-t1', 'scl-lg1', '#ffd25a');
    }
    b += twk(W * 0.86, H * 0.18, 0.8) + twk(W * 0.95, H * 0.42, 0.5) + twk(W * 0.04, H * 0.5, 0.55, '#c8ffb0');
    return b;
  }
  const LOGO = own('sclL', 'viewBox="0 0 560 240" class="scl-logo" role="img" aria-label="' + TITLE + '"', LOGO_DEFS, logoBody(560, 240));

  // ── Дівчина, що пускає вінок (афіша). Початок — коліно на землі, лицем праворуч
  function girl() {
    let s = '';
    // стрічки з віночка
    s += [['M6,-104 C-10,-102 -22,-92 -30,-74', '#e23a3a'], ['M4,-102 C-8,-96 -14,-84 -22,-66', '#ffc93a'], ['M6,-100 C-2,-92 -6,-82 -12,-62', '#3a7bff']].map(([d, c]) => `<path d="${d}" stroke="${INK}" stroke-width="5.5" fill="none" stroke-linecap="round"/><path d="${d}" stroke="${c}" stroke-width="3.2" fill="none" stroke-linecap="round"/>`).join('');
    // коса
    s += rep(6, (i) => { const t = i / 5; return `<ellipse cx="${f(-2 - 12 * t)}" cy="${f(-88 + 44 * t)}" rx="${f(5.6 - t)}" ry="6.2" fill="#6a3418" stroke="${INK}" stroke-width="1.8"/>`; }) + `<path d="M-14,-42 L-18,-34 L-10,-34Z" fill="#e23a3a" stroke="${INK}" stroke-width="1.5"/>`;
    // чобіт
    s += `<path d="M-40,0 C-42,-7 -36,-10 -27,-9 L-22,0Z" fill="#b0202e" stroke="${INK}" stroke-width="2"/>`;
    // плахта
    s += `<path d="M-33,0 C-35,-16 -27,-36 -11,-43 L12,-43 C22,-31 28,-15 30,0 Z" fill="#d32f3a" stroke="${INK}" stroke-width="2.4" stroke-linejoin="round"/>`;
    s += `<g stroke="#8e1424" stroke-width="1.4" opacity=".7">${rep(5, (i) => `<path d="M${-28 + i * 10},-2 L${-18 + i * 10},-36"/>`)}</g>`;
    s += `<path d="M12,-43 C20,-31 25,-15 27,0 L14,0 C12,-15 8,-31 4,-43Z" fill="#203a8c" stroke="${INK}" stroke-width="1.8"/>` + [[14, -30], [17, -18], [20, -7]].map(([x, y]) => `<circle cx="${x}" cy="${y}" r="1.8" fill="#ffd23a"/>`).join('');
    s += `<path d="M-33,0 L30,0 L29.6,-5 L-33.3,-5Z" fill="#ffd23a" stroke="${INK}" stroke-width="1.4"/>`;
    // тулуб, нахилений уперед
    s += `<g transform="rotate(22 0 -42)">`;
    s += `<path d="M-11,-41 C-13,-57 -7,-73 4,-77 C14,-79 20,-71 18,-59 L13,-41 Z" fill="url(#scl-shirt)" stroke="${INK}" stroke-width="2.4" stroke-linejoin="round"/>`;
    s += `<rect x="-12" y="-46" width="26" height="5" rx="2" fill="#ffc93a" stroke="${INK}" stroke-width="1.4"/>`;
    s += `<g fill="#d8313a">${rep(5, (i) => `<rect x="${-1 + i * 3}" y="${-75 + i * 0.6}" width="2" height="2" transform="rotate(45 ${i * 3} ${-74 + i * 0.6})"/>`)}${rep(4, (i) => `<rect x="${1 + (i % 2) * 2}" y="${-70 + i * 5}" width="2" height="2"/>`)}</g>`;
    s += `<rect x="1" y="-82" width="7" height="7" fill="url(#scl-skin)"/>`;
    // голова
    s += `<circle cx="6" cy="-91" r="12.5" fill="url(#scl-skin)" stroke="${INK}" stroke-width="2.4"/>`;
    s += `<path d="M17.4,-94 Q21.5,-90 17.6,-87.6" fill="url(#scl-skin)" stroke="${INK}" stroke-width="1.8"/>`;
    s += `<path d="M-6,-88 C-9,-102 4,-108 13,-104 C17,-102 19,-99 18.6,-96 C12,-99 7,-97 4,-92 C2,-88 -2,-84 -6,-80Z" fill="#6a3418" stroke="${INK}" stroke-width="2" stroke-linejoin="round"/>`;
    s += `<path d="M9.5,-92 Q12,-89.6 14.6,-92" stroke="${INK}" stroke-width="1.6" fill="none" stroke-linecap="round"/><ellipse cx="11.5" cy="-86.6" rx="2.8" ry="1.7" fill="#ff8fa0" opacity=".75"/><path d="M14,-83.6 Q16.2,-82.6 17.4,-85" stroke="#a33a4a" stroke-width="1.4" fill="none" stroke-linecap="round"/>`;
    s += [[-6, -98, 'p'], [-1, -104, 'c'], [6, -106.5, 'd'], [13, -104, 's'], [17.5, -99, 'c']].map(([x, y, k]) => at(x, y, 0.2, 0, MINI[k])).join('');
    // руки
    const arm = 'M10,-68 C26,-60 40,-44 52,-29';
    s += `<path d="${arm}" stroke="${INK}" stroke-width="10" fill="none" stroke-linecap="round"/><path d="${arm}" stroke="#f4f6ff" stroke-width="7" fill="none" stroke-linecap="round"/><path d="M45.5,-36 L50.5,-31" stroke="#d8313a" stroke-width="6.6" stroke-linecap="butt"/>`;
    s += `<circle cx="54.5" cy="-26.5" r="4.8" fill="url(#scl-skin)" stroke="${INK}" stroke-width="1.8"/>`;
    s += `</g>`;
    return s;
  }

  const POSTER = (() => {
    const defs = MOON_DEFS + LOGO_DEFS
      + lg('scl-psky', [[0, '#05061e'], [0.55, '#1c1650'], [1, '#4a246a']])
      + lg('scl-priv', [[0, '#3a3a8a'], [0.3, '#1a2a6c'], [1, '#08113a']])
      + lg('scl-mref', [[0, '#fff1c0', 0.95], [1, '#ffe9a8', 0.15]]);
    let b = `<rect width="360" height="240" fill="url(#scl-psky)"/>` + stars(77, 50, 360, 140, 1.1);
    b += moon(300, 46, 24);
    b += `<path d="${forest(55, -10, 370, 150, 18, 44, 13, 4)}" fill="#22195a"/>`;
    b += `<rect x="0" y="146" width="360" height="94" fill="url(#scl-priv)"/>`;
    b += rep(7, (i) => { const y = 152 + i * i * 1.5 + i * 3, w = 14 + i * 7; return `<rect x="${f(300 - w / 2 + (i % 2 ? 4 : -4))}" y="${f(y)}" width="${f(w)}" height="${f(1.6 + i * 0.4)}" rx="1.5" fill="url(#scl-mref)" opacity="${f(0.85 - i * 0.08)}"/>`; });
    // берег з дівчиною
    b += `<path d="M0,182 C40,178 90,184 128,198 C140,204 146,214 150,240 L0,240Z" fill="#120d34"/><path d="M0,182 C40,178 90,184 128,198 C140,204 146,214 150,240" stroke="#4a3a9a" stroke-width="1.5" fill="none" opacity=".6"/>`;
    b += `<g fill="#0c0a26">${reeds(61, -4, 16, 244, '#0c0a26').replace(/stroke-width="[\d.]+"/g, 'stroke-width="2"')}</g>`;
    b += at(80, 214, 0.86, 0, girl());
    // вінки
    b += `<ellipse cx="148" cy="222" rx="40" ry="6" fill="none" stroke="#9ab4ff" stroke-width="1.2" opacity=".4"/><ellipse cx="150" cy="222" rx="60" ry="9" fill="none" stroke="#9ab4ff" stroke-width="1" opacity=".25"/>`;
    b += at(146, 217, 0.54, 0, wreathFloat());
    b += at(240, 178, 0.22, 0, wreathFloat()) + at(206, 162, 0.14, 0, wreathFloat()) + at(196, 196, 0.3, 0, wreathFloat());
    // цвіт папороті праворуч унизу
    b += `<circle cx="318" cy="182" r="80" fill="url(#scl-h-m)"/><circle cx="318" cy="182" r="40" fill="url(#scl-h-g)"/>`;
    const lt = { uw: 3 };
    for (const [a, s] of [[-50, 2.2], [-22, 2.6], [8, 2.6], [36, 2.2]]) b += at(318, 250, s, a, frond([0, 0], [-6, -14], [2, -30], 9, lt));
    b += `<path d="M318,248 C316,224 320,206 318,190" stroke="${INK}" stroke-width="5" fill="none"/><path d="M318,248 C316,224 320,206 318,190" stroke="#7df06a" stroke-width="2.4" fill="none"/>`;
    b += at(318, 180, 0.7, 0, `<g transform="translate(-50 -47)">${bloomBody()}</g>`);
    b += [[250, 120], [276, 150], [120, 150], [200, 132], [346, 120]].map(([x, y]) => ffly(x, y, 0.45)).join('');
    // логотип зверху ліворуч
    b += at(4, 2, 0.43, 0, logoBody(560, 240));
    return own('sclP', 'viewBox="0 0 360 240" preserveAspectRatio="xMidYMid slice" class="scl-poster" role="img" aria-label="' + TITLE + '"', defs, b);
  })();

  // ── Extras
  // Рамка поля 7×7: поле — квадрат 0..700, рамка виступає на 44 з кожного боку
  const FRAME = (() => {
    let b = `<rect x="0" y="0" width="700" height="700" rx="18" fill="#120a35" fill-opacity=".62"/>`;
    b += `<g stroke="#7d6bff" stroke-opacity=".12" stroke-width="1.5">${rep(6, (i) => `<path d="M${(i + 1) * 100},6 L${(i + 1) * 100},694 M6,${(i + 1) * 100} L694,${(i + 1) * 100}"/>`)}</g>`;
    b += `<rect x="3" y="3" width="694" height="694" rx="16" fill="none" stroke="#9a88ff" stroke-opacity=".3" stroke-width="3"/>`;
    b += `<rect x="-20" y="-20" width="740" height="740" rx="36" fill="none" stroke="#ffd25a" stroke-opacity=".16" stroke-width="52"/>`;
    b += `<rect x="-20" y="-20" width="740" height="740" rx="36" fill="none" stroke="${INK}" stroke-width="36"/>`;
    b += `<rect x="-20" y="-20" width="740" height="740" rx="36" fill="none" stroke="#1f5a3a" stroke-width="29"/>`;
    b += `<rect x="-20" y="-20" width="740" height="740" rx="36" fill="none" stroke="#2f8a4f" stroke-width="14" stroke-dasharray="26 14"/>`;
    b += `<rect x="-20" y="-20" width="740" height="740" rx="36" fill="none" stroke="#7ee07a" stroke-width="3.5" stroke-dasharray="14 26" stroke-dashoffset="6" opacity=".7"/>`;
    let leaves = '';
    const side = (k, x, y, dir) => { for (let i = 0; i < 16; i++) { const t = 30 + i * 42.5, sd = i % 2 ? 1 : -1; const px = x + Math.cos(dir * Math.PI / 180) * t, py = y + Math.sin(dir * Math.PI / 180) * t; leaves += leaf(px, py, 32, dir + sd * 40, 'url(#scl-leaf)', 2.4); } };
    side(0, -20, -20, 0); side(1, 720, -20, 90); side(2, 720, 720, 180); side(3, -20, 720, 270);
    b += leaves;
    const fl = [];
    for (const [x, y] of [[-20, -20], [720, -20], [720, 720], [-20, 720]]) { fl.push(at(x, y, 1.25, 0, MINI.p)); fl.push(at(x + (x < 0 ? 30 : -30), y + (y < 0 ? 8 : -8), 0.7, 0, MINI.c)); fl.push(at(x + (x < 0 ? 6 : -6), y + (y < 0 ? 32 : -32), 0.68, 0, MINI.d)); }
    const mids = ['d', 'c', 's', 'p', 'd'];
    for (let i = 0; i < 5; i++) { const t = 117 + i * 116.5, k = mids[i], sc = i === 2 ? 0.82 : 0.6; fl.push(at(t, -20, sc, 0, MINI[k]), at(720, t, sc, 0, MINI[mids[(i + 1) % 5]]), at(t, 720, sc, 0, MINI[mids[(i + 2) % 5]]), at(-20, t, sc, 0, MINI[mids[(i + 3) % 5]])); }
    b += fl.join('');
    b += [[-34, 260], [734, 420], [300, -36], [470, 736]].map(([x, y]) => ffly(x, y, 1.2)).join('');
    return own('sclF', 'viewBox="-44 -44 788 788" class="scl-frame" aria-hidden="true"', '', b);
  })();

  // Шкала папороті: стебло-полілінія знизу вгору, рівні на 1/3, 2/3, 1 довжини
  const MP = (() => {
    const n = 56, pts = [];
    for (let i = 0; i <= n; i++) { const u = i / n; pts.push([45 + 7 * Math.sin(u * Math.PI * 2.4), 300 - u * 262]); }
    const L = [0];
    for (let i = 1; i < pts.length; i++) L.push(L[i - 1] + Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]));
    return { pts, L, total: L[L.length - 1], d: 'M' + pts.map((p) => f(p[0]) + ',' + f(p[1])).join(' L') };
  })();
  function mAt(fr) {
    const tg = Math.max(0, Math.min(1, fr)) * MP.total, { pts, L } = MP; let i = 1;
    while (i < L.length - 1 && L[i] < tg) i++;
    const t = (tg - L[i - 1]) / ((L[i] - L[i - 1]) || 1);
    return [pts[i - 1][0] + (pts[i][0] - pts[i - 1][0]) * t, pts[i - 1][1] + (pts[i][1] - pts[i - 1][1]) * t, Math.atan2(pts[i][1] - pts[i - 1][1], pts[i][0] - pts[i - 1][0]) * 180 / Math.PI];
  }
  const LVL = [1 / 3, 2 / 3, 1];
  const ICONS = [
    `<circle r="9" fill="url(#scl-h-fly)"/><ellipse cx="-3.4" cy="-2.6" rx="3.6" ry="2" fill="#dff4ff" opacity=".8" transform="rotate(-30 -3.4 -2.6)"/><ellipse cx="3.4" cy="-2.6" rx="3.6" ry="2" fill="#dff4ff" opacity=".8" transform="rotate(30 3.4 -2.6)"/><circle r="3" fill="#f2ffa0" stroke="${INK}" stroke-width="1"/>`,
    `<g transform="scale(.2) translate(-50 -52)">${combBody()}</g>`,
    '',
  ];
  function meter(level) {
    let lv = Number(level); if (!isFinite(lv)) lv = 0.55; lv = Math.max(0, Math.min(1, lv));
    let b = `<rect class="m-panel" x="4" y="4" width="82" height="312" rx="41" fill="#120a35" fill-opacity=".6" stroke="#8a78ff" stroke-opacity=".35" stroke-width="2"/>`;
    let lv2 = '';
    for (let u = 0.05, k = 0; u < 0.97; u += 0.052, k++) {
      if (LVL.some((q) => Math.abs(q - u) < 0.06)) continue;
      const [x, y, a] = mAt(u), sd = k % 2 ? 1 : -1, Ln = 17 - u * 6;
      lv2 += `<path class="m-leaf${u <= lv ? ' on' : ''}" data-u="${f(u)}" d="M0,0 C${f(0.3 * Ln)},${f(-0.34 * Ln)} ${f(0.75 * Ln)},${f(-0.26 * Ln)} ${f(Ln)},0 C${f(0.75 * Ln)},${f(0.26 * Ln)} ${f(0.3 * Ln)},${f(0.34 * Ln)} 0,0Z" transform="translate(${f(x)} ${f(y)}) rotate(${f(a + sd * 62)})" stroke="${INK}" stroke-width="1.8"/>`;
    }
    b += lv2;
    const off = f(100 - 100 * lv);
    b += `<path d="${MP.d}" fill="none" stroke="${INK}" stroke-width="15" stroke-linecap="round" stroke-linejoin="round"/>`;
    b += `<path d="${MP.d}" fill="none" stroke="#1a1446" stroke-width="10" stroke-linecap="round" stroke-linejoin="round"/>`;
    b += `<path class="m-glow" d="${MP.d}" pathLength="100" fill="none" stroke="#9aff7a" stroke-opacity=".25" stroke-width="22" stroke-linecap="round" stroke-linejoin="round" stroke-dasharray="100 100" stroke-dashoffset="${off}"/>`;
    b += `<path class="m-fill" d="${MP.d}" pathLength="100" fill="none" stroke="url(#scl-mg)" stroke-width="7.5" stroke-linecap="round" stroke-linejoin="round" stroke-dasharray="100 100" stroke-dashoffset="${off}"/>`;
    b += `<path class="m-core" d="${MP.d}" pathLength="100" fill="none" stroke="#fffbe0" stroke-opacity=".7" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" stroke-dasharray="100 100" stroke-dashoffset="${off}"/>`;
    LVL.forEach((q, i) => {
      const [x, y] = mAt(q), big = i === 2 ? 1.35 : 1, open = lv >= q - 1e-6;
      const isx = x > 45 ? -27 : 27;
      const icon = ICONS[i] ? `<g transform="translate(${f(x + isx)} ${f(y)})"><circle r="11" fill="#1b1046" stroke="url(#scl-gold)" stroke-width="2"/>${ICONS[i]}</g>` : '';
      b += icon + `<g class="bud b${i + 1}${open ? ' open' : ''}" transform="translate(${f(x)} ${f(y)}) scale(${big})">`
        + `<circle class="bud-h" r="24" fill="url(#scl-h-m)"/>`
        + `<g class="bud-c"><path d="M0,-14 C7,-8 8,2 0,8 C-8,2 -7,-8 0,-14Z" fill="#2f8a4f" stroke="${INK}" stroke-width="2.2"/><path d="M0,-12 C3,-7 3,-2 0,2" stroke="#ff6a9a" stroke-width="2.4" fill="none" stroke-linecap="round"/><circle cy="-4" r="2" fill="#ffd25a" opacity=".85"/></g>`
        + `<g class="bud-o"><g transform="scale(.36) translate(-50 -47)">${bloomBody()}</g></g></g>`;
    });
    const [hx, hy] = mAt(lv);
    b += `<g class="m-headpos" transform="translate(${f(hx)} ${f(hy)})"><g class="m-head"><circle r="11" fill="url(#scl-h-fern)"/><circle r="4" fill="#f6ffd0" stroke="${INK}" stroke-width="1.2"/></g></g>`;
    const defs = lg('scl-mg', [[0, '#fff3a0'], [0.5, '#9aff6a'], [1, '#1fb86a']], 0, 0, 0, 1, ' gradientUnits="userSpaceOnUse" x1="0" y1="20" x2="0" y2="300"').replace(' x1="0" y1="0" x2="0" y2="1"', '');
    return own('sclM', `viewBox="0 0 90 320" class="scl-meter" style="--lvl:${f(lv)}" role="img" aria-label="шкала папороті"`, defs, b);
  }
  // оновити шкалу без перерисовки (плавно): el — <svg class="scl-meter">
  function meterSet(el, level) {
    if (!el || !el.style) return;
    const lv = Math.max(0, Math.min(1, Number(level) || 0));
    el.style.setProperty('--lvl', lv);
    el.querySelectorAll('.m-fill,.m-core,.m-glow').forEach((p) => p.setAttribute('stroke-dashoffset', f(100 - 100 * lv)));
    el.querySelectorAll('.m-leaf').forEach((p) => p.classList.toggle('on', Number(p.dataset.u) <= lv));
    el.querySelectorAll('.bud').forEach((g, i) => g.classList.toggle('open', lv >= LVL[i] - 1e-6));
    const [x, y] = mAt(lv), h = el.querySelector('.m-headpos'); if (h) h.setAttribute('transform', `translate(${f(x)} ${f(y)})`);
  }

  const FIREFLY = own('sclY', 'viewBox="0 0 40 40" class="scl-firefly" aria-hidden="true"', '',
    `<g class="ff-glow"><circle cx="20" cy="22" r="19" fill="url(#scl-h-fly)"/></g>`
    + `<g class="ff-wl"><ellipse cx="14" cy="15" rx="7.5" ry="4" fill="#dff4ff" fill-opacity=".75" stroke="${INK}" stroke-width="1" transform="rotate(-35 14 15)"/></g>`
    + `<g class="ff-wr"><ellipse cx="26" cy="15" rx="7.5" ry="4" fill="#dff4ff" fill-opacity=".75" stroke="${INK}" stroke-width="1" transform="rotate(35 26 15)"/></g>`
    + `<path d="M18,10 C16,6 14,5 12,5 M22,10 C24,6 26,5 28,5" stroke="${INK}" stroke-width="1.2" fill="none" stroke-linecap="round"/>`
    + `<circle cx="20" cy="12.5" r="3.6" fill="#3a2a7a" stroke="${INK}" stroke-width="1.2"/><ellipse cx="20" cy="18" rx="4" ry="3.4" fill="#4a3a8a" stroke="${INK}" stroke-width="1.2"/>`
    + `<ellipse cx="20" cy="25.5" rx="5.2" ry="6.2" fill="url(#scl-flyb)" stroke="${INK}" stroke-width="1.4"/><ellipse cx="18.4" cy="23.5" rx="1.6" ry="2.2" fill="#fff" opacity=".8"/>`);

  const SPARK = own('sclK', 'viewBox="0 0 20 20" class="scl-spark" aria-hidden="true"', '',
    `<circle cx="10" cy="10" r="10" fill="url(#scl-h-g)"/><g transform="translate(10 10) scale(.7)"><path d="${STAR4}" fill="#fff6c8"/></g><circle cx="10" cy="10" r="1.8" fill="#fff"/>`);

  const MERMAID = (() => {
    let rise = '';
    // волосся ззаду
    rise += `<g class="m-hair"><path d="M68,66 C60,36 82,32 96,34 C114,32 132,44 126,70 C130,96 138,120 134,156 L58,156 C54,120 62,96 68,66Z" fill="url(#scl-hair)" stroke="${INK}" stroke-width="3" stroke-linejoin="round"/>`
      + `<g stroke="#5fe0c8" stroke-width="1.8" fill="none" opacity=".55" stroke-linecap="round"><path d="M66,90 C62,110 64,130 62,150"/><path d="M128,92 C132,112 132,132 130,150"/></g>`
      + [['M122,52 C136,62 140,84 146,104', '#e23a3a'], ['M120,56 C130,70 132,90 134,110', '#ffc93a']].map(([d, c]) => `<path d="${d}" stroke="${INK}" stroke-width="6" fill="none" stroke-linecap="round"/><path d="${d}" stroke="${c}" stroke-width="3.6" fill="none" stroke-linecap="round"/>`).join('') + `</g>`;
    // тулуб
    rise += `<rect x="90" y="84" width="12" height="14" fill="url(#scl-skin)"/>`;
    rise += `<path d="M70,156 C68,126 72,106 84,98 C90,95 102,95 108,98 C120,106 124,126 122,156 Z" fill="url(#scl-shirt)" stroke="${INK}" stroke-width="3" stroke-linejoin="round"/>`;
    rise += `<g fill="#d8313a">${rep(7, (i) => `<rect x="${84 + i * 4}" y="99.5" width="2.6" height="2.6" transform="rotate(45 ${85.3 + i * 4} 100.8)"/>`)}${rep(5, (i) => `<rect x="94.7" y="${106 + i * 6}" width="2.6" height="2.6" transform="rotate(45 96 ${107.3 + i * 6})"/>`)}</g>`;
    rise += `<path d="M86,101 Q96,107 106,101" stroke="#1e1046" stroke-width="1" fill="none" opacity=".5"/>`;
    // ліва рука
    rise += `<path d="M78,106 C68,118 66,134 70,150" stroke="${INK}" stroke-width="13" fill="none" stroke-linecap="round"/><path d="M78,106 C68,118 66,134 70,150" stroke="#f4f6ff" stroke-width="9" fill="none" stroke-linecap="round"/><path d="M67.5,136 L68.5,144" stroke="#d8313a" stroke-width="9"/>`;
    // голова
    rise += `<circle cx="96" cy="64" r="24" fill="url(#scl-skin)" stroke="${INK}" stroke-width="3"/>`;
    rise += `<path d="M72,64 C70,40 86,36 96,38 C110,36 124,44 120,64 C114,54 106,50 98,53 C92,57 82,59 72,64Z" fill="url(#scl-hair)" stroke="${INK}" stroke-width="2.4" stroke-linejoin="round"/>`;
    rise += `<path d="M72,62 C66,84 70,110 78,126 C80,108 78,86 80,70Z" fill="url(#scl-hair)" stroke="${INK}" stroke-width="2.4" stroke-linejoin="round"/><path d="M120,62 C126,86 122,104 116,118 C115,100 113,84 112,70Z" fill="url(#scl-hair)" stroke="${INK}" stroke-width="2.4" stroke-linejoin="round"/>`;
    rise += `<g fill="#2a1a5e"><ellipse cx="87" cy="68" rx="4" ry="5.4"/><ellipse cx="105" cy="68" rx="4" ry="5.4"/></g><g fill="#fff"><circle cx="85.6" cy="66" r="1.7"/><circle cx="103.6" cy="66" r="1.7"/></g>`;
    rise += `<path d="M82,62 Q86,59.5 90,61 M102,61 Q106,59.5 110,62" stroke="${INK}" stroke-width="1.6" fill="none" stroke-linecap="round"/>`;
    rise += `<ellipse cx="81.5" cy="76" rx="4.5" ry="2.6" fill="#ff8fa0" opacity=".6"/><ellipse cx="110.5" cy="76" rx="4.5" ry="2.6" fill="#ff8fa0" opacity=".6"/>`;
    rise += `<path d="M91,79 Q96,84 101,79" stroke="#a33a4a" stroke-width="2" fill="none" stroke-linecap="round"/><path d="M96,71 Q97.4,73.4 95.4,74.2" stroke="#d99080" stroke-width="1.4" fill="none" stroke-linecap="round"/>`;
    // віночок
    let wr = rep(9, (i) => { const a = (-170 + i * 20) * Math.PI / 180; const x = 96 + 25 * Math.cos(a), y = 58 + 20 * Math.sin(a); return `<ellipse cx="${f(x)}" cy="${f(y)}" rx="6" ry="2.8" transform="rotate(${f(i * 20 - 80 + (i % 2 ? 30 : -30))} ${f(x)} ${f(y)})" fill="url(#scl-leaf)" stroke="${INK}" stroke-width="1.2"/>`; });
    wr += [[-160, 'c'], [-135, 'd'], [-110, 'p'], [-90, 's'], [-70, 'p'], [-45, 'd'], [-20, 'c']].map(([a, k]) => at(96 + 25 * Math.cos(a * Math.PI / 180), 58 + 20 * Math.sin(a * Math.PI / 180), k === 'p' ? 0.3 : 0.24, 0, MINI[k])).join('');
    rise += wr;
    // права рука з гребенем
    rise += `<g class="m-arm"><path d="M114,106 C126,96 134,80 139,64" stroke="${INK}" stroke-width="13" fill="none" stroke-linecap="round"/><path d="M114,106 C126,96 134,80 139,64" stroke="#f4f6ff" stroke-width="9" fill="none" stroke-linecap="round"/><path d="M136,73 L138.5,66" stroke="#d8313a" stroke-width="9"/>`
      + `<g transform="translate(124 38) rotate(14 17 17)"><g transform="scale(.32)">${combBody()}</g></g>`
      + `<circle cx="140" cy="58" r="6.6" fill="url(#scl-skin)" stroke="${INK}" stroke-width="2.4"/></g>`;
    let b = `<g clip-path="url(#scl-mmc)"><g class="m-tail"><path d="M150,156 C152,138 160,126 170,120 C164,108 168,96 178,100 C182,110 184,118 180,124 C190,118 198,124 196,132 C188,132 178,134 172,138 C168,144 166,150 166,156Z" fill="url(#scl-tail)" stroke="${INK}" stroke-width="3" stroke-linejoin="round"/><path d="M170,120 C174,124 176,128 178,128 M176,108 C178,114 180,118 180,124 M186,124 C184,128 180,130 176,132" stroke="#c8fff6" stroke-width="1.5" fill="none" opacity=".7"/></g>`
      + `<g class="m-rise">${rise}</g></g>`;
    b += `<g class="m-ring"><ellipse cx="100" cy="153" rx="62" ry="8" fill="none" stroke="#bff4ff" stroke-width="2" opacity=".7"/></g>`;
    b += `<path d="M0,150 C20,144 40,156 60,150 C80,144 100,156 120,150 C140,144 160,156 180,150 C190,147 196,148 200,150 L200,200 L0,200Z" fill="url(#scl-water)"/>`;
    b += `<path d="M0,150 C20,144 40,156 60,150 C80,144 100,156 120,150 C140,144 160,156 180,150 C190,147 196,148 200,150" stroke="#bff4ff" stroke-width="3" fill="none" opacity=".8"/>`;
    b += `<g stroke="#8fd0ff" stroke-width="1.6" opacity=".45" stroke-linecap="round"><path d="M30,170 l30,0 M120,176 l40,0 M70,188 l36,0"/></g>`;
    b += waterLily(30, 172, 0.7);
    b += `<g class="m-splash" fill="#cff6ff" stroke="${INK}" stroke-width="1">${[[160, 104, 3.4], [186, 94, 2.6], [150, 120, 2.4], [196, 112, 2]].map(([x, y, r]) => `<circle cx="${x}" cy="${y}" r="${r}"/>`).join('')}</g>`;
    return own('sclR', 'viewBox="0 0 200 200" class="scl-mermaid" aria-hidden="true"', `<clipPath id="scl-mmc"><rect x="-40" y="-60" width="280" height="214"/></clipPath>`, b);
  })();

  window.SlotArt = window.SlotArt || {};
  window.SlotArt['slot-cluster'] = {
    title: TITLE,
    symbols: SYM,
    scene: { base: SCENE_BASE, bonus: SCENE_BONUS },
    logo: LOGO,
    poster: POSTER,
    extras: {
      defs: DEFS,
      meter: meter,           // (level 0..1) → SVG шкали; бутони .bud.b1/.b2/.b3 мають .open на рівнях 1/3, 2/3, 1
      meterSet: meterSet,     // (svgEl, level) — плавно оновити наявну шкалу без перерисовки
      firefly: FIREFLY,
      mermaid: MERMAID,       // клас .go на <svg> — виринає й махає гребенем
      frame: FRAME,           // .scl-frame — під клітинками, поле = квадрат 0..700, рамка виступає на 44/700
      spark: SPARK,
    },
  };
})();
