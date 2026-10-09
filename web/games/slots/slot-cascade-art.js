/* Розбиті глеки — арт (slot-cascade). Звичайний скрипт: реєструє SlotArt['slot-cascade'].
 * Символи — viewBox 0 0 100 100, посилаються на спільні градієнти з extras.defs (id з префіксом sc-).
 * Палітра — об'єкт P нижче (одне місце для кольорів SVG); CSS-змінні рамки/лічильника — у slot-cascade-art.css.
 * Анімовані групи: .a-bob (увесь символ: стрибок), .a-shadow (тінь), .a-glint (зірочка блиску),
 * .a-lid (кришка горщика), .a-stir (макогін), .a-fire/.a-fire2 (полум'я горна), .a-aura/.a-rays (сяйво), .a-ring (дзвін). */
(function () {
  'use strict';
  const O = '#3b1b10';                       // обведення — темна глина
  const P = {                                // світло / основа / тінь
    blue: ['#9cc8ff', '#2f6fe0', '#163c94'],
    green: ['#b4f29a', '#34a852', '#14603a'],
    yellow: ['#fff3b0', '#f7bd24', '#b06a08'],
    red: ['#ffa48e', '#e03b2c', '#861818'],
    cream: ['#ffffff', '#f8ecd4', '#cfae80'],
    terra: ['#ffb685', '#d2672f', '#7a2e14'],
    brown: ['#eaa066', '#a9521f', '#57210c'],
    honey: ['#ffd98a', '#ec8d1f', '#8a420e'],
    gold: ['#fff8c8', '#f9c73a', '#a5620a'],
    egg: ['#ff9a84', '#d8262a', '#780e16'],
    violet: ['#e2b8ff', '#8e44d6', '#4a1a86'],
    dark: ['#6a4a5a', '#2c1622', '#120810'],
    dgreen: ['#3f8a52', '#1d5a34', '#0b2a18'],
    wood: ['#f2c58e', '#c3864c', '#7a4620'],
  };
  const CREAM = '#fff6e2';
  const ST = 'stroke="' + O + '" stroke-width="3" stroke-linejoin="round"';
  const st = (w, c) => 'stroke="' + (c || O) + '" stroke-width="' + w + '" stroke-linejoin="round" stroke-linecap="round"';
  const FONT = 'font-family="Onest, system-ui, sans-serif" font-weight="900"';

  // ---------- спільні градієнти ----------
  const rg = (id, c) => '<radialGradient id="sc-r-' + id + '" cx=".38" cy=".32" r=".8" fx=".3" fy=".22">'
    + '<stop offset="0" stop-color="' + c[0] + '"/><stop offset=".5" stop-color="' + c[1] + '"/><stop offset="1" stop-color="' + c[2] + '"/></radialGradient>';
  const lg = (id, c) => '<linearGradient id="sc-l-' + id + '" x1="0" y1="0" x2="0" y2="1">'
    + '<stop offset="0" stop-color="' + c[0] + '"/><stop offset=".55" stop-color="' + c[1] + '"/><stop offset="1" stop-color="' + c[2] + '"/></linearGradient>';

  const PATH = {
    pot: 'M30 32 C22 40 12 48 12 62 C12 78 26 90 38 90 L62 90 C74 90 88 78 88 62 C88 48 78 40 70 32 Z',
    glek: 'M40 14 L60 14 C60 21 58 25 58 30 C76 36 88 50 86 64 C84 80 72 90 62 90 L38 90 C28 90 16 80 14 64 C12 50 24 36 42 30 C42 25 40 21 40 14 Z',
    egg: 'M50 8 C69 8 84 34 84 58 C84 78 69 92 50 92 C31 92 16 78 16 58 C16 34 31 8 50 8 Z',
    bowl: 'M9 46 C11 70 29 84 50 84 C71 84 89 70 91 46 Z',
    mak: 'M8 42 A42 12 0 0 0 92 42 L76 86 C70 92 30 92 24 86 Z',
    dome: 'M12 90 L12 58 C12 32 29 17 50 17 C71 17 88 32 88 58 L88 90 Z',
  };

  let defsInner = '';
  for (const k in P) defsInner += rg(k, P[k]) + lg(k, P[k]);
  defsInner += '<radialGradient id="sc-shadow"><stop offset="0" stop-color="#2a0f06" stop-opacity=".55"/><stop offset="1" stop-color="#2a0f06" stop-opacity="0"/></radialGradient>'
    + '<linearGradient id="sc-hl" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".95"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient>'
    + '<radialGradient id="sc-fire" cx=".5" cy=".85" r=".85"><stop offset="0" stop-color="#fffbd6"/><stop offset=".3" stop-color="#ffd83a"/><stop offset=".65" stop-color="#ff7a1a"/><stop offset="1" stop-color="#c8261a"/></radialGradient>'
    + '<radialGradient id="sc-aura"><stop offset="0" stop-color="#ffe27a" stop-opacity=".95"/><stop offset=".55" stop-color="#ff9a2a" stop-opacity=".5"/><stop offset="1" stop-color="#ff6a1a" stop-opacity="0"/></radialGradient>'
    + '<radialGradient id="sc-gaura"><stop offset="0" stop-color="#fff6b0" stop-opacity=".95"/><stop offset=".5" stop-color="#ffd23a" stop-opacity=".45"/><stop offset="1" stop-color="#ffb000" stop-opacity="0"/></radialGradient>'
    + '<radialGradient id="sc-maura"><stop offset="0" stop-color="#fff" stop-opacity=".7"/><stop offset=".55" stop-color="#ffe9a8" stop-opacity=".25"/><stop offset="1" stop-color="#ffe9a8" stop-opacity="0"/></radialGradient>'
    + '<clipPath id="sc-c-pot"><path d="' + PATH.pot + '"/></clipPath>'
    + '<clipPath id="sc-c-glek"><path d="' + PATH.glek + '"/></clipPath>'
    + '<clipPath id="sc-c-egg"><path d="' + PATH.egg + '"/></clipPath>'
    + '<clipPath id="sc-c-bowl"><path d="' + PATH.bowl + '"/></clipPath>'
    + '<clipPath id="sc-c-mak"><path d="' + PATH.mak + '"/></clipPath>'
    + '<clipPath id="sc-c-dome"><path d="' + PATH.dome + '"/></clipPath>';
  const DEFS = '<svg xmlns="http://www.w3.org/2000/svg" width="0" height="0" style="position:absolute" aria-hidden="true"><defs>' + defsInner + '</defs></svg>';

  // ---------- дрібні помічники ----------
  const shadow = (rx, cy) => '<ellipse class="a-shadow" cx="50" cy="' + (cy || 91) + '" rx="' + rx + '" ry="6" fill="url(#sc-shadow)"/>';
  const glint = (x, y) => '<g transform="translate(' + x + ' ' + y + ')"><g class="a-glint" opacity="0">'
    + '<path d="M0 -11 C1 -3 3 -1 11 0 C3 1 1 3 0 11 C-1 3 -3 1 -11 0 C-3 -1 -1 -3 0 -11 Z" fill="#fff"/><circle r="2.6" fill="#fff"/></g></g>';
  // крапля-пелюстка вістрям угору від (0,0)
  const petal = (w, l) => 'M0 0 C' + (-w) + ' ' + (-l * 0.35) + ' ' + (-w * 0.7) + ' ' + (-l * 0.85) + ' 0 ' + (-l)
    + ' C' + (w * 0.7) + ' ' + (-l * 0.85) + ' ' + w + ' ' + (-l * 0.35) + ' 0 0 Z';
  const star = (cx, cy, ro, ri, n) => {
    let s = '';
    for (let i = 0; i < n * 2; i++) {
      const r = i % 2 ? ri : ro, a = Math.PI * i / n - Math.PI / 2;
      s += (i ? ' ' : '') + (cx + r * Math.cos(a)).toFixed(1) + ',' + (cy + r * Math.sin(a)).toFixed(1);
    }
    return s;
  };
  const wrap = (key, body) => '<svg viewBox="0 0 100 100" class="sym sc-' + key + '" xmlns="http://www.w3.org/2000/svg">' + body + '</svg>';

  // ---------- кахлі ----------
  function tile(pal, motif, frame) {
    const c = P[pal];
    return shadow(33, 91)
      + '<g class="a-bob"><g transform="translate(50 52) scale(.92) translate(-50 -52)">'
      + '<rect x="11" y="15" width="78" height="76" rx="15" fill="' + c[2] + '" ' + ST + '/>'
      + '<rect x="11" y="8" width="78" height="76" rx="15" fill="url(#sc-r-' + pal + ')" ' + ST + '/>'
      + (frame || '<rect x="18.5" y="15.5" width="63" height="61" rx="9" fill="none" stroke="' + CREAM + '" stroke-width="2.4" opacity=".8"/>')
      + motif
      + '<path d="M17 30 C17 19 21 13 31 13 L50 13 C36 16 25 21 17 34 Z" fill="url(#sc-hl)" opacity=".75"/>'
      + glint(27, 22)
      + '</g></g>';
  }
  // k1 — синя: розетка з восьми пелюсток
  let k1m = '<g stroke="#10306e" stroke-width="1.8" stroke-linejoin="round">';
  for (let i = 0; i < 8; i++) k1m += '<path d="' + petal(7.5, 21) + '" transform="translate(50 46) rotate(' + (i * 45) + ')" fill="' + CREAM + '"/>';
  for (let i = 0; i < 8; i++) k1m += '<circle cx="50" cy="20.5" r="2.4" transform="rotate(' + (i * 45 + 22.5) + ' 50 46)" fill="#ffd23a"/>';
  k1m += '<circle cx="50" cy="46" r="7.5" fill="#ffd23a"/><circle cx="50" cy="46" r="3" fill="#e03b2c" stroke="none"/></g>';
  const k1f = '<circle cx="50" cy="46" r="31" fill="none" stroke="' + CREAM + '" stroke-width="2.2" stroke-dasharray="3 4" opacity=".7"/>';
  // k2 — зелена: петриківська гілочка
  let k2m = '<g stroke="#0e4a28" stroke-width="1.7" stroke-linejoin="round">'
    + '<path d="M50 77 C46 66 54 56 50 44 C47 35 50 28 50 24" fill="none" stroke="' + CREAM + '" stroke-width="3.4" stroke-linecap="round"/>';
  [[50.5, 68, 62, 17], [49.5, 55, 55, 15], [50, 41, 48, 12]].forEach(([x, y, a, l]) => {
    k2m += '<path d="' + petal(6, l) + '" transform="translate(' + x + ' ' + y + ') rotate(' + a + ')" fill="' + CREAM + '"/>'
      + '<path d="' + petal(6, l) + '" transform="translate(' + x + ' ' + y + ') rotate(' + (-a) + ')" fill="' + CREAM + '"/>';
  });
  k2m += '<path d="' + petal(7, 15) + '" transform="translate(50 33)" fill="#ffd23a"/>'
    + '<circle cx="50" cy="19" r="3.4" fill="#ffd23a"/></g>';
  const k2f = '<path d="M50 15 L82 46 L50 77 L18 46 Z" fill="none" stroke="' + CREAM + '" stroke-width="2" opacity=".35"/>'
    + '<g fill="' + CREAM + '" opacity=".85"><circle cx="21" cy="18" r="2.6"/><circle cx="79" cy="18" r="2.6"/><circle cx="21" cy="74" r="2.6"/><circle cx="79" cy="74" r="2.6"/></g>';
  // k3 — жовта: восьмираменна зоря
  const k3m = '<polygon points="' + star(50, 46, 29, 13, 8) + '" fill="#d1361f" stroke="#5e1206" stroke-width="2" stroke-linejoin="round"/>'
    + '<polygon points="' + star(50, 46, 15, 7, 8) + '" fill="' + CREAM + '" stroke="#5e1206" stroke-width="1.5" stroke-linejoin="round"/>'
    + '<circle cx="50" cy="46" r="3.2" fill="#2f6fe0"/>';
  const k3f = '<rect x="18.5" y="15.5" width="63" height="61" rx="5" fill="none" stroke="#9a4a06" stroke-width="2.4" opacity=".55"/>';
  // k4 — червона: тюльпан
  const k4m = '<g stroke="#5e0c0c" stroke-width="1.8" stroke-linejoin="round">'
    + '<path d="M50 60 C50 66 50 70 50 76" fill="none" stroke="' + CREAM + '" stroke-width="3.4" stroke-linecap="round"/>'
    + '<path d="M50 74 C42 74 33 70 29 61 C38 61 46 66 50 74 Z" fill="#7fdc7a"/>'
    + '<path d="M50 74 C58 74 67 70 71 61 C62 61 54 66 50 74 Z" fill="#7fdc7a"/>'
    + '<path d="M50 62 C36 62 28 50 29 33 C38 37 45 46 50 62 Z" fill="' + CREAM + '"/>'
    + '<path d="M50 62 C64 62 72 50 71 33 C62 37 55 46 50 62 Z" fill="' + CREAM + '"/>'
    + '<path d="M50 62 C42 54 41 38 50 24 C59 38 58 54 50 62 Z" fill="#ffd23a"/>'
    + '<circle cx="50" cy="46" r="2.6" fill="#e03b2c" stroke="none"/></g>';
  const k4f = '<path d="M20 77 L20 36 C20 22 34 15.5 50 15.5 C66 15.5 80 22 80 36 L80 77" fill="none" stroke="' + CREAM + '" stroke-width="2.4" opacity=".7"/>';

  // ---------- миска ----------
  let bowlDots = '';
  for (let i = 0; i < 7; i++) {
    const x = 20 + i * 10, y = 60 + Math.sin((i / 6) * Math.PI) * 9;
    bowlDots += '<circle cx="' + x + '" cy="' + y.toFixed(1) + '" r="3" fill="#ffe27a" stroke="#0e4a28" stroke-width="1.2"/>';
  }
  const BOWL = shadow(38, 91)
    + '<g class="a-bob"><g transform="translate(50 88) scale(1.1) translate(-50 -88)">'
    + '<path d="M35 80 L37 89 L63 89 L65 80 Z" fill="' + P.green[2] + '" ' + ST + '/>'
    + '<path d="' + PATH.bowl + '" fill="url(#sc-r-green)"/>'
    + '<g clip-path="url(#sc-c-bowl)"><path d="M6 50 Q22 62 50 62 Q78 62 94 50" fill="none" stroke="' + CREAM + '" stroke-width="4"/>'
    + bowlDots + '<path d="M6 76 Q50 92 94 76" fill="none" stroke="#ffe27a" stroke-width="3"/></g>'
    + '<path d="' + PATH.bowl + '" fill="none" ' + ST + '/>'
    + '<ellipse cx="50" cy="46" rx="41" ry="13" fill="url(#sc-l-yellow)" ' + ST + '/>'
    + '<ellipse cx="50" cy="47" rx="34" ry="9" fill="url(#sc-l-cream)" stroke="#9a6a2a" stroke-width="1.5"/>'
    + '<g stroke="#8a1a12" stroke-width="1.2" stroke-linejoin="round">'
    + '<ellipse cx="50" cy="48.5" rx="7" ry="3.2" fill="#e03b2c"/><ellipse cx="39" cy="48" rx="6" ry="2.6" fill="#34a852" transform="rotate(-10 39 48)"/>'
    + '<ellipse cx="61" cy="48" rx="6" ry="2.6" fill="#34a852" transform="rotate(10 61 48)"/><circle cx="50" cy="48.5" r="1.6" fill="#ffd23a"/></g>'
    + '<path d="M16 54 C18 64 24 71 31 75 C24 73 17 66 15 57 Z" fill="#fff" opacity=".55"/>'
    + '<path d="M18 40 C24 36 34 34 42 34" fill="none" stroke="#fff" stroke-width="2.6" stroke-linecap="round" opacity=".75"/>'
    + glint(24, 40)
    + '</g></g>';

  // ---------- горщик з кришкою ----------
  let potDots = '';
  for (let i = 0; i < 6; i++) potDots += '<circle cx="' + (22 + i * 11.2) + '" cy="' + (i % 2 ? 62 : 58) + '" r="2.8" fill="#e03b2c" stroke="#5e0c0c" stroke-width="1"/>';
  const POT = shadow(32, 92)
    + '<g class="a-bob">'
    + '<path d="' + PATH.pot + '" fill="url(#sc-r-brown)"/>'
    + '<g clip-path="url(#sc-c-pot)">'
    + '<path d="M0 50 Q50 58 100 50 L100 70 Q50 78 0 70 Z" fill="url(#sc-l-cream)" stroke="' + O + '" stroke-width="2"/>'
    + '<path d="M6 60 q5 -5 10 0 t10 0 t10 0 t10 0 t10 0 t10 0 t10 0 t10 0 t10 0" fill="none" stroke="#34a852" stroke-width="2.4" stroke-linecap="round"/>'
    + potDots
    + '<g fill="#ffd9a8" opacity=".9"><circle cx="30" cy="40" r="1.8"/><circle cx="40" cy="38" r="1.8"/><circle cx="50" cy="37.5" r="1.8"/><circle cx="60" cy="38" r="1.8"/><circle cx="70" cy="40" r="1.8"/></g>'
    + '</g>'
    + '<path d="' + PATH.pot + '" fill="none" ' + ST + '/>'
    + '<rect x="25" y="27" width="50" height="9" rx="4.5" fill="url(#sc-l-brown)" ' + ST + '/>'
    + '<g class="a-lid"><path d="M27 29 C28 19 38 14.5 50 14.5 C62 14.5 72 19 73 29 Z" fill="url(#sc-r-terra)" ' + ST + '/>'
    + '<ellipse cx="50" cy="13" rx="6" ry="4.5" fill="url(#sc-r-terra)" ' + ST + '/>'
    + '<path d="M33 24 C36 19 42 17.5 47 17.5" fill="none" stroke="#fff" stroke-width="2.2" stroke-linecap="round" opacity=".7"/></g>'
    + '<path d="M19 52 C19 45 24 40 31 38 C26 45 24 52 24 61 Z" fill="#fff" opacity=".55"/>'
    + glint(27, 44)
    + '</g>';

  // ---------- макітра з макогоном ----------
  const MAKITRA = shadow(38, 92)
    + '<g class="a-bob">'
    + '<path d="' + PATH.mak + '" fill="url(#sc-r-terra)"/>'
    + '<g clip-path="url(#sc-c-mak)">'
    + '<path d="M0 30 L100 30 L100 52 Q96 60 92 53 Q88 64 83 55 Q78 62 74 57 Q66 68 60 58 Q54 64 48 59 Q40 68 35 57 Q29 63 25 55 Q19 64 15 53 Q10 59 6 51 L0 52 Z" fill="url(#sc-l-green)" stroke="' + O + '" stroke-width="2"/>'
    + '<path d="M14 70 Q50 82 86 70" fill="none" stroke="' + P.terra[2] + '" stroke-width="2.4" opacity=".55"/>'
    + '<path d="M19 80 Q50 90 81 80" fill="none" stroke="' + P.terra[2] + '" stroke-width="2.4" opacity=".55"/>'
    + '</g>'
    + '<path d="' + PATH.mak + '" fill="none" ' + ST + '/>'
    + '<ellipse cx="50" cy="42" rx="42" ry="12" fill="url(#sc-l-green)" ' + ST + '/>'
    + '<ellipse cx="50" cy="43" rx="35" ry="8.5" fill="url(#sc-l-dgreen)" stroke="' + P.dgreen[2] + '" stroke-width="1.5"/>'
    + '<g class="a-stir">'
    + '<path d="M54 45 L80 7" stroke="' + O + '" stroke-width="12" stroke-linecap="round"/>'
    + '<path d="M54 45 L80 7" stroke="url(#sc-l-wood)" stroke-width="7" stroke-linecap="round"/>'
    + '<path d="M66 23 L77 7.5" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".6"/>'
    + '<ellipse cx="81.5" cy="5.5" rx="6" ry="4.5" transform="rotate(-55 81.5 5.5)" fill="url(#sc-r-wood)" ' + ST + '/></g>'
    + '<path d="M8 42 A42 12 0 0 0 92 42 L85 43 A35 8.5 0 0 1 15 43 Z" fill="' + P.green[1] + '" stroke="' + O + '" stroke-width="2"/>'
    + '<path d="M19 35.5 C25 32.6 33 31.2 42 30.8" fill="none" stroke="#fff" stroke-width="2.2" stroke-linecap="round" opacity=".8"/>'
    + '<path d="M18 60 C20 68 23 75 28 81 C23 78 19 71 16 62 Z" fill="#fff" opacity=".45"/>'
    + glint(22, 38)
    + '</g>';

  // ---------- куманець (глек-бублик) ----------
  let kumOrn = '';
  for (let i = 0; i < 8; i++) {
    const a = i * 45 + 22.5;
    kumOrn += '<ellipse cx="50" cy="36.5" rx="3.6" ry="6.5" transform="rotate(' + a + ' 50 58)" fill="#34a852" stroke="#0e4a28" stroke-width="1.2"/>'
      + '<circle cx="50" cy="35" r="2.4" transform="rotate(' + (a + 22.5) + ' 50 58)" fill="' + CREAM + '" stroke="#8a420e" stroke-width="1"/>';
  }
  const RING = 'M50 26 A32 32 0 1 1 49.99 26 Z M50 46 A12 12 0 1 0 50.01 46 Z';
  const KUMANETS = shadow(32, 93)
    + '<g class="a-bob"><g transform="translate(50 91) scale(1.07) translate(-50 -91)">'
    + '<path d="M38 84 L36 91 L64 91 L62 84 Z" fill="' + P.honey[2] + '" ' + ST + '/>'
    + '<path d="M60 31 C72 20 82 26 78 38" fill="none" ' + st(9) + '/><path d="M60 31 C72 20 82 26 78 38" fill="none" ' + st(4.5, P.honey[1]) + '/>'
    + '<path d="M42 31 L43 13 L57 13 L58 31 Z" fill="url(#sc-l-honey)" ' + ST + '/>'
    + '<path fill-rule="evenodd" d="' + RING + '" fill="url(#sc-r-honey)" ' + ST + '/>'
    + kumOrn
    + '<circle cx="50" cy="58" r="12" fill="none" stroke="' + P.honey[2] + '" stroke-width="3.5" opacity=".7"/>'
    + '<circle cx="50" cy="58" r="12" fill="none" ' + ST + '/>'
    + '<rect x="39" y="8" width="22" height="8" rx="4" fill="url(#sc-l-green)" ' + ST + '/>'
    + '<path d="M24 50 A27 27 0 0 1 42 32.5" fill="none" stroke="#fff" stroke-width="4" stroke-linecap="round" opacity=".65"/>'
    + '<path d="M58 66 A9 9 0 0 1 50 69" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".5"/>'
    + glint(27, 42)
    + '</g></g>';

  // ---------- розписаний глек (найдорожчий) ----------
  let glekFlower = '<g stroke="#6e0c0c" stroke-width="1.5" stroke-linejoin="round">';
  [-62, -31, 0, 31, 62].forEach((a, i) => {
    glekFlower += '<path d="' + petal(7, i === 2 ? 23 : 19) + '" transform="translate(50 68) rotate(' + a + ')" fill="url(#sc-r-red)"/>'
      + '<circle cx="0" cy="' + (i === 2 ? -21 : -17) + '" r="2.4" transform="translate(50 68) rotate(' + a + ')" fill="#ffd23a" stroke-width="1"/>';
  });
  glekFlower += '</g><g stroke="#10306e" stroke-width="1.4" stroke-linejoin="round">'
    + '<path d="M45 72 C35 76 24 72 18 60 C29 61 38 64 45 72 Z" fill="url(#sc-r-blue)"/>'
    + '<path d="M55 72 C65 76 76 72 82 60 C71 61 62 64 55 72 Z" fill="url(#sc-r-blue)"/>'
    + '<path d="M44 76 C36 82 27 82 21 77 C29 74 37 74 44 76 Z" fill="#34a852"/>'
    + '<path d="M56 76 C64 82 73 82 79 77 C71 74 63 74 56 76 Z" fill="#34a852"/></g>'
    + '<path d="M40 70 A10 10 0 0 1 60 70 Z" fill="#ffd23a" stroke="#8a420e" stroke-width="1.4"/>'
    + '<g fill="#2f6fe0"><circle cx="24" cy="50" r="2.2"/><circle cx="29" cy="45" r="2.2"/><circle cx="76" cy="50" r="2.2"/><circle cx="71" cy="45" r="2.2"/></g>';
  const GLEK = shadow(30, 93)
    + '<g class="a-bob">'
    + '<path d="M58 22 C84 16 92 42 74 56" fill="none" ' + st(11) + '/>'
    + '<path d="M58 22 C84 16 92 42 74 56" fill="none" stroke="url(#sc-l-blue)" stroke-width="5.5" stroke-linecap="round"/>'
    + '<path d="' + PATH.glek + '" fill="url(#sc-r-cream)"/>'
    + '<g clip-path="url(#sc-c-glek)">'
    + '<rect x="0" y="0" width="100" height="34" fill="url(#sc-l-blue)"/>'
    + '<path d="M0 34 L100 34" stroke="#ffd23a" stroke-width="3"/>'
    + '<path d="M0 37.5 L100 37.5" stroke="#e03b2c" stroke-width="1.6"/>'
    + '<g fill="' + CREAM + '"><circle cx="45" cy="22" r="1.6"/><circle cx="50" cy="25" r="1.6"/><circle cx="55" cy="22" r="1.6"/></g>'
    + '<rect x="0" y="85" width="100" height="10" fill="url(#sc-l-blue)"/>'
    + glekFlower
    + '</g>'
    + '<path d="' + PATH.glek + '" fill="none" ' + ST + '/>'
    + '<path d="M36 13 L27 10 L35 18 Z" fill="url(#sc-l-gold)" ' + st(2.4) + '/>'
    + '<rect x="35" y="7.5" width="30" height="8" rx="4" fill="url(#sc-l-gold)" ' + ST + '/>'
    + '<path d="M21 54 C22 46 28 40 35 37 C30 44 27 51 26 60 Z" fill="#fff" opacity=".75"/>'
    + '<circle cx="27" cy="64" r="2" fill="#fff" opacity=".7"/>'
    + '<path d="M39 10.5 L53 10.5" stroke="#fff" stroke-width="1.8" stroke-linecap="round" opacity=".8"/>'
    + glint(29, 47)
    + '</g>';

  // ---------- горно (скатер) ----------
  let rays = '<g class="a-rays">';
  for (let i = 0; i < 12; i++) rays += '<path d="' + (i % 2 ? 'M50 56 L47 10 L53 10 Z' : 'M50 56 L45.5 1 L54.5 1 Z') + '" transform="rotate(' + (i * 30 + 15) + ' 50 56)" fill="' + (i % 2 ? '#ffd23a' : '#ff9a1e') + '" opacity=".9"/>';
  rays += '</g>';
  const FLAME_BIG = 'M50 89 C36 89 33 78 37 70 C39 76 42 77 43 74 C40 66 44 58 49 52 C49 60 54 62 56 58 C58 64 64 68 63 76 C66 74 67 70 67 68 C70 78 64 89 50 89 Z';
  const FLAME_IN = 'M50 89 C43 89 41 83 43 78 C45 81 47 81 47 78 C46 73 49 69 51 66 C52 71 55 72 56 70 C58 75 59 80 57 84 C55 88 53 89 50 89 Z';
  const FURNACE = '<g class="a-aura"><circle cx="50" cy="56" r="48" fill="url(#sc-aura)"/></g>' + rays
    + shadow(38, 93)
    + '<g class="a-bob">'
    + '<rect x="62" y="6" width="13" height="18" rx="2" fill="url(#sc-l-brown)" ' + ST + '/>'
    + '<g class="a-fire2"><path d="M68.5 7 C63 3 66 -3 68 -6 C69 -2 72 -2 72 -5 C76 0 74 5 68.5 7 Z" fill="url(#sc-fire)" stroke="#8a1e0a" stroke-width="1.4"/></g>'
    + '<rect x="59" y="4" width="19" height="6" rx="3" fill="url(#sc-l-brown)" ' + ST + '/>'
    + '<path d="' + PATH.dome + '" fill="url(#sc-r-terra)"/>'
    + '<g clip-path="url(#sc-c-dome)" fill="none" stroke="' + P.terra[2] + '" stroke-width="1.8" opacity=".55">'
    + '<path d="M10 34 H90 M10 48 H90 M10 62 H90 M10 76 H90"/>'
    + '<path d="M38 20 V34 M62 20 V34 M28 34 V48 M50 34 V48 M72 34 V48 M20 48 V62 M80 48 V62 M20 62 V76 M80 62 V76 M24 76 V90 M76 76 V90"/></g>'
    + '<path d="' + PATH.dome + '" fill="none" ' + ST + '/>'
    + '<path d="M25 90 L25 67 C25 52 36 42 50 42 C64 42 75 52 75 67 L75 90" fill="none" stroke="' + O + '" stroke-width="10"/>'
    + '<path d="M25 90 L25 67 C25 52 36 42 50 42 C64 42 75 52 75 67 L75 90" fill="none" stroke="url(#sc-l-yellow)" stroke-width="5.5"/>'
    + '<path d="M30 90 L30 67 C30 55 39 47 50 47 C61 47 70 55 70 67 L70 90 Z" fill="#2a0c06"/>'
    + '<ellipse cx="50" cy="86" rx="22" ry="10" fill="#ff7a1a" opacity=".6"/>'
    + '<g class="a-fire"><path d="' + FLAME_BIG + '" fill="url(#sc-fire)" stroke="#8a1e0a" stroke-width="1.4"/>'
    + '<path d="' + FLAME_IN + '" fill="#fff6c0"/></g>'
    + '<path d="M30 90 L70 90" ' + st(3) + '/>'
    + '<path d="M19 50 C21 38 29 29 39 25" fill="none" stroke="#fff" stroke-width="3" stroke-linecap="round" opacity=".6"/>'
    + '<g fill="#ffe27a"><circle class="a-spark" cx="40" cy="56" r="1.8"/><circle class="a-spark" cx="58" cy="54" r="1.5" style="animation-delay:-.4s"/><circle class="a-spark" cx="50" cy="50" r="1.3" style="animation-delay:-.8s"/></g>'
    + glint(24, 34)
    + '</g>';

  // ---------- писанка ----------
  function eggOrnament(kind) {
    // kind: base | simple | rich | gold
    if (kind === 'simple') {
      return '<path d="M0 52 L100 52 L100 66 L0 66 Z" fill="rgba(0,0,0,.22)"/>'
        + '<path d="M10 52 L100 52 M10 66 L100 66" stroke="' + CREAM + '" stroke-width="2.2"/>'
        + '<g fill="' + CREAM + '" opacity=".9"><circle cx="50" cy="20" r="3"/><circle cx="40" cy="28" r="2.4"/><circle cx="60" cy="28" r="2.4"/><circle cx="50" cy="84" r="3"/><circle cx="38" cy="80" r="2.4"/><circle cx="62" cy="80" r="2.4"/></g>';
    }
    const ink = kind === 'gold' ? '#7a1a10' : '#1e0b08';
    const line = kind === 'gold' ? '#e03b2c' : '#ffd23a';
    let s = '<rect x="0" y="50" width="100" height="16" fill="' + ink + '"/>'
      + '<path d="M0 50 H100 M0 66 H100" stroke="' + CREAM + '" stroke-width="2.2"/>'
      + '<path d="M8 58 q4.5 -6 9 0 t9 0 t9 0 t9 0 t9 0 t9 0 t9 0 t9 0 t9 0 t9 0" fill="none" stroke="' + line + '" stroke-width="2.6" stroke-linecap="round"/>'
      + '<path d="M50 8 V50 M50 66 V92" stroke="' + ink + '" stroke-width="2.4"/>';
    let tri = '';
    for (let i = 0; i < 7; i++) tri += '<path d="M' + (20 + i * 10) + ' 68 l5 9 l5 -9 Z"/>';
    s += '<g fill="' + (kind === 'gold' ? '#2f6fe0' : CREAM) + '" stroke="' + ink + '" stroke-width="1">' + tri + '</g>'
      + '<polygon points="' + star(38, 33, 9, 4, 8) + '" fill="' + CREAM + '" stroke="' + ink + '" stroke-width="1.2"/>'
      + '<polygon points="' + star(62, 33, 9, 4, 8) + '" fill="' + CREAM + '" stroke="' + ink + '" stroke-width="1.2"/>'
      + '<circle cx="38" cy="33" r="2" fill="' + line + '"/><circle cx="62" cy="33" r="2" fill="' + line + '"/>'
      + '<g fill="' + line + '"><circle cx="50" cy="16" r="2.4"/><circle cx="36" cy="84" r="2"/><circle cx="64" cy="84" r="2"/><circle cx="50" cy="87" r="2"/></g>';
    return s;
  }
  function egg(pal, kind, extra) {
    return '<path d="' + PATH.egg + '" fill="url(#sc-r-' + pal + ')"/>'
      + '<g clip-path="url(#sc-c-egg)">' + eggOrnament(kind) + '</g>'
      + '<path d="' + PATH.egg + '" fill="none" ' + ST + '/>'
      + (extra || '')
      + '<ellipse cx="34" cy="30" rx="6" ry="12" transform="rotate(28 34 30)" fill="#fff" opacity=".6"/>'
      + '<circle cx="28" cy="48" r="2.2" fill="#fff" opacity=".6"/>';
  }
  const PYSANKA = '<g class="a-aura"><circle cx="50" cy="52" r="48" fill="url(#sc-gaura)"/></g>'
    + shadow(26, 94)
    + '<g class="a-bob">' + egg('egg', 'base') + glint(34, 22) + '</g>';

  // ---------- символи ----------
  const BODY = {
    k1: tile('blue', k1m, k1f),
    k2: tile('green', k2m, k2f),
    k3: tile('yellow', k3m, k3f),
    k4: tile('red', k4m, k4f),
    bowl: BOWL, pot: POT, makitra: MAKITRA, kumanets: KUMANETS, glek: GLEK,
    furnace: FURNACE, pysanka: PYSANKA,
  };
  const NAMES = {
    k1: ['Синя кахля', 'low'], k2: ['Зелена кахля', 'low'], k3: ['Жовта кахля', 'low'], k4: ['Червона кахля', 'low'],
    bowl: ['Миска', 'high'], pot: ['Горщик', 'high'], makitra: ['Макітра', 'high'], kumanets: ['Куманець', 'high'],
    glek: ['Розписаний глек', 'high'], furnace: ['Горно', 'scatter'], pysanka: ['Писанка', 'special'],
  };
  const symbols = {};
  for (const k in BODY) symbols[k] = { name: NAMES[k][0], tier: NAMES[k][1], svg: wrap(k, BODY[k]) };
  const item = (k, x, y, s) => '<svg x="' + x + '" y="' + y + '" width="' + s + '" height="' + s + '" viewBox="0 0 100 100" overflow="visible">' + BODY[k] + '</svg>';

  // ---------- черепки ----------
  const SHARD = [
    [[4, 6], [30, 2], [36, 14], [22, 20], [26, 34], [8, 28]],
    [[6, 4], [34, 10], [28, 22], [36, 36], [12, 30], [14, 18]],
    [[2, 20], [18, 2], [38, 8], [30, 26], [14, 38]],
    [[10, 2], [30, 6], [38, 30], [20, 24], [4, 36]],
    [[4, 10], [24, 4], [36, 20], [28, 36], [6, 30], [14, 22]],
    [[8, 4], [36, 4], [26, 16], [32, 34], [4, 26]],
  ];
  const SHARD_COL = {
    k1: ['blue', CREAM, 4], k2: ['green', CREAM, 4], k3: ['yellow', '#d1361f', 4], k4: ['red', CREAM, 4],
    bowl: ['green', '#ffe27a', 5], pot: ['brown', CREAM, 5], makitra: ['terra', '#34a852', 5], kumanets: ['honey', '#34a852', 5],
    glek: ['cream', '#e03b2c', 6], furnace: ['terra', '#ffd23a', 5], pysanka: ['egg', '#ffd23a', 4],
  };
  function shards(key) {
    const [pal, acc, n] = SHARD_COL[key] || SHARD_COL.pot;
    const c = P[pal], out = [];
    for (let i = 0; i < n; i++) {
      const pts = SHARD[i], id = 'sc-sh-' + key + '-' + i;
      const cx = pts.reduce((s, p) => s + p[0], 0) / pts.length, cy = pts.reduce((s, p) => s + p[1], 0) / pts.length;
      const pp = pts.map((p) => p.join(',')).join(' ');
      const a = pts[0], b = pts[1];
      const ins = (p, t) => [(p[0] + (cx - p[0]) * t).toFixed(1), (p[1] + (cy - p[1]) * t).toFixed(1)];
      const h1 = ins(a, 0.3), h2 = ins(b, 0.3);
      const acc2 = key === 'glek' && i % 2 ? '#2f6fe0' : acc;
      out.push('<svg viewBox="-2 -2 44 44" class="sc-shard" xmlns="http://www.w3.org/2000/svg"><defs><linearGradient id="' + id + '" x1="0" y1="0" x2="1" y2="1">'
        + '<stop offset="0" stop-color="' + c[0] + '"/><stop offset=".5" stop-color="' + c[1] + '"/><stop offset="1" stop-color="' + c[2] + '"/></linearGradient></defs>'
        + '<polygon points="' + pp + '" transform="translate(2 3)" fill="#c46a3a" stroke="' + O + '" stroke-width="1.8" stroke-linejoin="round"/>'
        + '<polygon points="' + pp + '" fill="url(#' + id + ')" stroke="' + O + '" stroke-width="1.8" stroke-linejoin="round"/>'
        + (i % 2 ? '<path d="M' + (cx - 7).toFixed(1) + ' ' + (cy + 2).toFixed(1) + ' q3.5 -5 7 0 t7 0" fill="none" stroke="' + acc2 + '" stroke-width="2.4" stroke-linecap="round"/>'
          : '<circle cx="' + cx.toFixed(1) + '" cy="' + cy.toFixed(1) + '" r="3.6" fill="' + acc2 + '" stroke="' + O + '" stroke-width="1"/>')
        + '<path d="M' + h1.join(' ') + ' L' + h2.join(' ') + '" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".75"/>'
        + '</svg>');
    }
    return out;
  }

  // ---------- писанка-множник ----------
  function multiplier(label) {
    label = String(label == null ? '' : label);
    const n = parseFloat(label.replace(/[^\d.]/g, ''));
    let pal = 'egg', kind = 'simple', aura = '', extra = '';
    if (!isFinite(n) || n < 10) {
      pal = n >= 5 ? 'violet' : n >= 3 ? 'blue' : 'green';
    } else if (n < 50) {
      pal = n >= 25 ? 'dark' : 'egg'; kind = 'rich';
      aura = '<g class="a-aura"><circle cx="50" cy="52" r="48" fill="url(#sc-maura)"/></g>';
    } else {
      pal = 'gold'; kind = 'gold';
      let r = '<g class="a-rays">';
      for (let i = 0; i < 16; i++) r += '<path d="M50 52 L46.5 0 L53.5 0 Z" transform="rotate(' + (i * 22.5) + ' 50 52)" fill="#fff3a0" opacity=".6"/>';
      aura = '<g class="a-aura"><circle cx="50" cy="52" r="50" fill="url(#sc-gaura)"/></g>' + r + '</g>';
      extra = '<g fill="#fff">' + ['M12 20', 'M86 26', 'M84 84'].map((m, i) => '<path transform="translate(' + m.slice(1).replace(' ', ' ') + ') scale(' + (i ? 0.6 : 0.8) + ')" d="M0 -9 C1 -2 2 -1 9 0 C2 1 1 2 0 9 C-1 2 -2 1 -9 0 C-2 -1 -1 -2 0 -9 Z"/>').join('') + '</g>';
    }
    const m = label.match(/^\s*[×x*]\s*(.+)$/i);
    const num = m ? m[1] : label;
    const len = num.length;
    const fs = m ? (len <= 1 ? 50 : len === 2 ? 42 : len === 3 ? 33 : 26) : (len <= 3 ? 30 : 20);
    const fill = kind === 'gold' ? '#fff' : '#fff';
    const stroke = kind === 'gold' ? '#7a2e08' : O;
    const text = '<text x="50" y="' + (60 + fs * 0.36).toFixed(1) + '" text-anchor="middle" ' + FONT + ' font-size="' + fs + '" fill="' + fill
      + '" stroke="' + stroke + '" stroke-width="' + (fs * 0.2).toFixed(1) + '" stroke-linejoin="round" paint-order="stroke" letter-spacing="-1">'
      + (m ? '<tspan font-size="' + Math.round(fs * 0.6) + '" dy="-1">×</tspan><tspan dy="1">' + num + '</tspan>' : num) + '</text>';
    return '<svg viewBox="0 0 100 100" class="sym sc-pysanka sc-mult sc-mult-' + kind + '" xmlns="http://www.w3.org/2000/svg">'
      + aura + shadow(26, 94) + '<g class="a-bob">' + egg(pal, kind, extra) + text + glint(34, 22) + '</g></svg>';
  }

  // ---------- лічильник множника: дощечка ----------
  // viewBox 0 0 240 80. Місце під текст — темна вставка x 70..226, y 17..63
  // (у відсотках від коробки: left 29.2%, top 21%, width 65%, height 57.5%), текст по центру,
  // колір #ffe27a з обведенням #3b1b10. Ліворуч — писанка-значок.
  const COUNTER = '<svg viewBox="0 0 240 80" class="sc-counter" xmlns="http://www.w3.org/2000/svg">'
    + '<ellipse cx="120" cy="76" rx="112" ry="5" fill="url(#sc-shadow)"/>'
    + '<rect x="4" y="10" width="232" height="64" rx="16" fill="' + P.wood[2] + '" ' + ST + '/>'
    + '<rect x="4" y="5" width="232" height="64" rx="16" fill="url(#sc-l-wood)" ' + ST + '/>'
    + '<g fill="none" stroke="' + P.wood[2] + '" stroke-width="1.6" opacity=".45" stroke-linecap="round">'
    + '<path d="M18 20 C40 16 52 24 66 20"/><path d="M14 56 C30 60 44 52 64 58"/><path d="M200 14 C214 18 222 14 228 18"/></g>'
    + '<path d="M14 12 H226" stroke="#fff" stroke-width="2.4" stroke-linecap="round" opacity=".5"/>'
    + '<rect x="70" y="17" width="156" height="46" rx="11" fill="#2a120a" stroke="' + O + '" stroke-width="2.4"/>'
    + '<rect x="72" y="19" width="152" height="8" rx="4" fill="#000" opacity=".3"/>'
    + '<path d="M80 61 H216" stroke="#ffb46a" stroke-width="1.6" opacity=".35" stroke-linecap="round"/>'
    + '<g fill="#7a4620" stroke="' + O + '" stroke-width="1.2"><circle cx="230" cy="37" r="2.6"/></g>'
    + '<svg x="8" y="2" width="58" height="72" viewBox="12 4 76 92">' + egg('egg', 'rich') + '</svg>'
    + '</svg>';

  // ---------- рамка поля 6×5: стелаж ----------
  // viewBox 0 0 640 560, самодостатня (свої градієнти sc-fr-*). Вікно поля — x 20..620, y 34..534:
  // клітинка 100×100, ряд r стоїть на полиці (верх полиці = 34 + 100r + 90). Кладіть рамку ПІД сітку,
  // сітку — у вікно: left 3.125%, top 6.07%, width 93.75%, height 89.29%.
  function frame() {
    let s = '<svg viewBox="0 0 640 560" class="sc-frame-svg" xmlns="http://www.w3.org/2000/svg"><defs>'
      + '<linearGradient id="sc-fr-w" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#d89a5e"/><stop offset=".5" stop-color="#a8642e"/><stop offset="1" stop-color="#6a3816"/></linearGradient>'
      + '<linearGradient id="sc-fr-v" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#7a4420"/><stop offset=".35" stop-color="#c4844a"/><stop offset="1" stop-color="#6a3816"/></linearGradient>'
      + '<linearGradient id="sc-fr-back" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#6e3c1e"/><stop offset="1" stop-color="#4a2612"/></linearGradient>'
      + '<linearGradient id="sc-fr-s" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#e8b47a"/><stop offset=".35" stop-color="#b8763c"/><stop offset="1" stop-color="#6e3a18"/></linearGradient>'
      + '</defs>'
      + '<rect x="6" y="14" width="628" height="540" rx="18" fill="#2a120a" opacity=".35"/>'
      + '<rect x="20" y="34" width="600" height="500" fill="url(#sc-fr-back)"/>';
    for (let i = 1; i < 6; i++) s += '<path d="M' + (20 + i * 100) + ' 34 V534" stroke="#2a1408" stroke-width="2" opacity=".5"/>';
    for (let i = 0; i < 12; i++) s += '<path d="M' + (40 + i * 50) + ' 40 v' + (20 + (i * 37) % 30) + '" stroke="#6e3e1e" stroke-width="2" opacity=".35" stroke-linecap="round"/>';
    for (let r = 0; r < 5; r++) {
      const y = 34 + r * 100;
      s += '<rect x="20" y="' + (y + 2) + '" width="600" height="26" fill="#000" opacity=".18"/>'
        + '<rect x="20" y="' + (y + 88) + '" width="600" height="12" fill="url(#sc-fr-s)" stroke="' + O + '" stroke-width="2"/>'
        + '<path d="M22 ' + (y + 90.5) + ' H618" stroke="#ffe0b0" stroke-width="1.4" opacity=".6"/>';
    }
    s += '<rect x="2" y="22" width="22" height="532" rx="8" fill="url(#sc-fr-v)" ' + ST + '/>'
      + '<rect x="616" y="22" width="22" height="532" rx="8" fill="url(#sc-fr-v)" ' + ST + '/>'
      + '<rect x="0" y="530" width="640" height="26" rx="10" fill="url(#sc-fr-w)" ' + ST + '/>'
      + '<path d="M0 30 C0 14 10 6 26 6 L614 6 C630 6 640 14 640 30 L640 40 L0 40 Z" fill="url(#sc-fr-w)" ' + ST + '/>'
      + '<path d="M18 12 H622" stroke="#fff" stroke-width="2.4" stroke-linecap="round" opacity=".45"/>';
    // розпис на карнизі: хвилька з крапками
    s += '<path d="M40 24 q10 -9 20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0 t20 0" fill="none" stroke="#2f6fe0" stroke-width="3" stroke-linecap="round"/>';
    for (let i = 0; i < 29; i++) s += '<circle cx="' + (50 + i * 20) + '" cy="' + (i % 2 ? 29 : 19) + '" r="2.4" fill="' + (i % 3 === 0 ? '#e03b2c' : '#ffd23a') + '"/>';
    s += '<g fill="#3b1b10">' + [[13, 60], [13, 520], [627, 60], [627, 520]].map(([x, y]) => '<circle cx="' + x + '" cy="' + y + '" r="3.5"/>').join('') + '</g>';
    return s + '</svg>';
  }
  const FRAME = frame();

  // ---------- сцени ----------
  const SDEFS = '<defs>'
    + '<linearGradient id="sc-s-wall" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f9e6c2"/><stop offset="1" stop-color="#e2b07a"/></linearGradient>'
    + '<linearGradient id="sc-s-walln" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2a1610"/><stop offset="1" stop-color="#5a2e1a"/></linearGradient>'
    + '<linearGradient id="sc-s-floor" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#b9774a"/><stop offset="1" stop-color="#6e3d20"/></linearGradient>'
    + '<linearGradient id="sc-s-floorn" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#6a3418"/><stop offset="1" stop-color="#2c140a"/></linearGradient>'
    + '<linearGradient id="sc-s-sky" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#5fb8ff"/><stop offset="1" stop-color="#d6f0ff"/></linearGradient>'
    + '<linearGradient id="sc-s-skyn" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#141c48"/><stop offset="1" stop-color="#4a3a7a"/></linearGradient>'
    + '<radialGradient id="sc-s-sun"><stop offset="0" stop-color="#fffbe0"/><stop offset=".35" stop-color="#ffe680" stop-opacity=".9"/><stop offset="1" stop-color="#ffe680" stop-opacity="0"/></radialGradient>'
    + '<linearGradient id="sc-s-ray" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff6c8" stop-opacity=".75"/><stop offset=".7" stop-color="#fff0b0" stop-opacity=".25"/><stop offset="1" stop-color="#fff0b0" stop-opacity="0"/></linearGradient>'
    + '<radialGradient id="sc-s-heat" cx=".5" cy=".86" r=".62"><stop offset="0" stop-color="#ffb04a" stop-opacity=".7"/><stop offset=".4" stop-color="#ff6a1a" stop-opacity=".22"/><stop offset="1" stop-color="#ff6a1a" stop-opacity="0"/></radialGradient>'
    + '<radialGradient id="sc-s-vig" cx=".5" cy=".6" r=".75"><stop offset=".4" stop-color="#140806" stop-opacity="0"/><stop offset="1" stop-color="#140806" stop-opacity=".85"/></radialGradient>'
    + '<linearGradient id="sc-s-plank" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#e2a86c"/><stop offset=".4" stop-color="#b06e36"/><stop offset="1" stop-color="#6a3816"/></linearGradient>'
    + '</defs>';
  function shelf(x, y, w) {
    return '<path d="M' + (x + 30) + ' ' + (y + 18) + ' l0 40 l34 -40 Z M' + (x + w - 30) + ' ' + (y + 18) + ' l0 40 l-34 -40 Z" fill="#7a4420" ' + st(4) + '/>'
      + '<rect x="' + x + '" y="' + y + '" width="' + w + '" height="20" rx="5" fill="url(#sc-s-plank)" ' + st(4) + '/>'
      + '<path d="M' + (x + 8) + ' ' + (y + 5) + ' H' + (x + w - 8) + '" stroke="#ffe0b0" stroke-width="2.4" opacity=".55" stroke-linecap="round"/>';
  }
  function shelves() {
    return shelf(30, 330, 520) + item('pot', 50, 215, 120) + item('k1', 180, 250, 86) + item('kumanets', 270, 210, 124) + item('bowl', 400, 230, 110)
      + shelf(30, 580, 520) + item('makitra', 40, 460, 128) + item('k3', 172, 500, 86) + item('k4', 250, 500, 86) + item('glek', 340, 444, 142) + item('k2', 460, 500, 86)
      + shelf(1050, 330, 520) + item('bowl', 1066, 230, 110) + item('glek', 1176, 196, 140) + item('k2', 1316, 250, 86) + item('pot', 1400, 215, 120)
      + shelf(1050, 580, 520) + item('kumanets', 1060, 456, 126) + item('k1', 1186, 500, 86) + item('makitra', 1272, 460, 128) + item('k4', 1408, 500, 86) + item('k3', 1478, 500, 86);
  }
  function windowSvg(night) {
    let s = '<path d="M592 136 L800 66 L1008 136 L1008 506 L592 506 Z" fill="' + (night ? '#a89a8a' : '#fbf6ec') + '" ' + st(5) + '/>'
      + '<path d="M650 124 L800 84 L950 124" fill="none" stroke="#2f6fe0" stroke-width="7" stroke-linecap="round"/>'
      + '<g fill="#e03b2c"><circle cx="800" cy="104" r="8"/><circle cx="740" cy="116" r="5"/><circle cx="860" cy="116" r="5"/></g>'
      + '<rect x="626" y="140" width="348" height="352" rx="6" fill="#2f6fe0" ' + st(5) + '/>'
      + '<rect x="646" y="160" width="308" height="312" fill="url(#' + (night ? 'sc-s-skyn' : 'sc-s-sky') + ')"/>';
    if (night) {
      s += '<g class="sc-twinkle" fill="#fff8d0"><circle cx="690" cy="200" r="3"/><circle cx="760" cy="260" r="2.2"/><circle cx="910" cy="300" r="2.6"/><circle cx="700" cy="330" r="2"/><circle cx="830" cy="190" r="2.4"/></g>'
        + '<circle cx="880" cy="225" r="40" fill="#fff4c8"/><circle cx="898" cy="212" r="36" fill="#28305e"/>'
        + '<path d="M646 420 Q720 370 800 404 T954 390 L954 472 L646 472 Z" fill="#1c2a3a"/>'
        + '<path d="M646 446 Q760 420 860 446 T954 440 L954 472 L646 472 Z" fill="#101a26"/>'
        + '<rect x="700" y="404" width="26" height="20" fill="#ffd27a"/>';
    } else {
      s += '<circle class="sc-halo" cx="880" cy="230" r="96" fill="url(#sc-s-sun)"/><circle cx="880" cy="230" r="34" fill="#fff6b8"/>'
        + '<g fill="#fff" opacity=".95"><ellipse cx="722" cy="222" rx="40" ry="16"/><ellipse cx="748" cy="208" rx="26" ry="16"/><ellipse cx="700" cy="214" rx="18" ry="11"/></g>'
        + '<path d="M646 412 Q720 362 800 396 T954 382 L954 472 L646 472 Z" fill="#8fd06a"/>'
        + '<path d="M646 440 Q760 410 860 440 T954 432 L954 472 L646 472 Z" fill="#5aa84a"/>'
        + '<path d="M714 398 l18 -16 l18 16 v18 h-36 Z" fill="#fff" stroke="#3b1b10" stroke-width="2"/><path d="M710 400 l22 -20 l22 20" fill="none" stroke="#c46a3a" stroke-width="5"/>';
    }
    s += '<rect x="794" y="160" width="12" height="312" fill="#2f6fe0" ' + st(3) + '/><rect x="646" y="304" width="308" height="12" fill="#2f6fe0" ' + st(3) + '/>'
      + '<path d="M660 290 L740 170 M680 300 L750 196" stroke="#fff" stroke-width="7" opacity="' + (night ? '.08' : '.3') + '" stroke-linecap="round"/>'
      + '<rect x="580" y="492" width="440" height="24" rx="6" fill="url(#sc-s-plank)" ' + st(4) + '/>';
    // рушник: драпування й кінці з вишивкою
    s += '<path d="M600 146 C680 196 920 196 1000 146 L1000 130 C920 176 680 176 600 130 Z" fill="#fbf4e6" ' + st(4) + '/>'
      + '<path d="M612 150 C690 190 910 190 988 150" fill="none" stroke="#c4202a" stroke-width="4" stroke-dasharray="6 6"/>';
    [[586, 128], [968, 128]].forEach(([x, y]) => {
      s += '<path d="M' + x + ' ' + y + ' h46 v232 l-8 14 l-8 -14 l-8 14 l-8 -14 l-8 14 l-6 -14 Z" fill="#fbf4e6" ' + st(4) + '/>';
      for (let r = 0; r < 3; r++) {
        const yy = y + 196 + r * 14;
        s += '<path d="M' + (x + 6) + ' ' + yy + ' l6 6 l6 -6 l6 6 l6 -6 l6 6 l6 -6" fill="none" stroke="' + (r === 1 ? '#1e0b08' : '#c4202a') + '" stroke-width="3.4"/>';
      }
    });
    // соняшники в глечику на підвіконні
    s += '<path d="M680 470 C676 430 678 410 682 390 M692 470 C698 440 710 420 720 410" stroke="#2f7a3a" stroke-width="5" fill="none"/>'
      + [[682, 384, 22], [722, 404, 17]].map(([x, y, r]) => {
        let p = '<g>';
        for (let i = 0; i < 12; i++) p += '<ellipse cx="' + x + '" cy="' + (y - r * 0.75) + '" rx="' + (r * 0.28) + '" ry="' + (r * 0.55) + '" transform="rotate(' + (i * 30) + ' ' + x + ' ' + y + ')" fill="#ffc81e" stroke="#3b1b10" stroke-width="1.6"/>';
        return p + '<circle cx="' + x + '" cy="' + y + '" r="' + (r * 0.45) + '" fill="#6a3416" stroke="#3b1b10" stroke-width="2"/></g>';
      }).join('')
      + item('glek', 648, 410, 90);
    return s;
  }
  function wheel() {
    return '<g>'
      + '<ellipse cx="300" cy="944" rx="150" ry="20" fill="#2a0f06" opacity=".35"/>'
      + '<path d="M196 940 L230 838 M404 940 L370 838" stroke="#7a4420" stroke-width="16" stroke-linecap="round"/>'
      + '<path d="M196 940 L230 838 M404 940 L370 838" stroke="' + O + '" stroke-width="22" stroke-linecap="round" opacity=".0"/>'
      + '<ellipse cx="300" cy="924" rx="124" ry="24" fill="#6a3816" ' + st(4) + '/><ellipse cx="300" cy="918" rx="124" ry="24" fill="url(#sc-s-plank)" ' + st(4) + '/>'
      + '<rect x="290" y="826" width="20" height="94" fill="#5a3018" ' + st(4) + '/>'
      + '<ellipse cx="300" cy="832" rx="92" ry="17" fill="#6a3816" ' + st(4) + '/><ellipse cx="300" cy="826" rx="92" ry="17" fill="#c58a52" ' + st(4) + '/>'
      + '<path d="M252 826 C254 780 274 756 300 754 C326 756 346 780 348 826 Z" fill="url(#sc-r-terra)" ' + st(4) + '/>'
      + '<g fill="none" stroke="#7a2e14" stroke-width="2.4" opacity=".5"><path d="M262 806 Q300 814 338 806"/><path d="M268 788 Q300 795 332 788"/></g>'
      + '<ellipse cx="300" cy="756" rx="20" ry="5" fill="#7a2e14" ' + st(3) + '/>'
      + '<path d="M266 812 C266 790 276 774 290 766" stroke="#fff" stroke-width="5" opacity=".5" fill="none" stroke-linecap="round"/>'
      + '</g>';
  }
  function kiln(lit) {
    let s = '<g>'
      + '<g fill="#8a4a24" ' + st(4) + '>'
      + '<circle cx="520" cy="900" r="26"/><circle cx="566" cy="900" r="26"/><circle cx="543" cy="858" r="26"/></g>'
      + '<g fill="#e8b47a"><circle cx="520" cy="900" r="13"/><circle cx="566" cy="900" r="13"/><circle cx="543" cy="858" r="13"/></g>'
      + '<rect x="868" y="486" width="64" height="120" rx="6" fill="url(#sc-l-brown)" ' + st(5) + '/>'
      + '<rect x="856" y="476" width="88" height="24" rx="10" fill="url(#sc-l-brown)" ' + st(5) + '/>';
    if (lit) s += '<g class="sc-flame sc-fl-c"><path d="M900 478 C872 448 888 410 900 390 C904 414 920 412 918 396 C944 428 932 462 900 478 Z" fill="url(#sc-fire)" stroke="#8a1e0a" stroke-width="3"/></g>';
    s += '<path d="M620 930 L620 720 C620 610 700 550 800 550 C900 550 980 610 980 720 L980 930 Z" fill="url(#sc-r-terra)" ' + st(6) + '/>'
      + '<g fill="none" stroke="#7a2e14" stroke-width="3" opacity=".45">'
      + '<path d="M628 680 H972 M622 760 H978 M622 840 H978 M670 610 H930"/>'
      + '<path d="M760 556 V610 M840 556 V610 M700 610 V680 M800 610 V680 M900 610 V680 M660 680 V760 M940 680 V760 M650 760 V840 M950 760 V840 M660 840 V930 M940 840 V930"/></g>'
      + '<path d="M650 700 C660 630 710 584 770 570" stroke="#fff" stroke-width="10" opacity=".35" fill="none" stroke-linecap="round"/>'
      + '<path d="M690 930 L690 800 C690 735 740 700 800 700 C860 700 910 735 910 800 L910 930" fill="none" stroke="' + O + '" stroke-width="26"/>'
      + '<path d="M690 930 L690 800 C690 735 740 700 800 700 C860 700 910 735 910 800 L910 930" fill="none" stroke="' + (lit ? '#ffcf5a' : '#e8a060') + '" stroke-width="16"/>'
      + '<path d="M706 930 L706 802 C706 750 748 718 800 718 C852 718 894 750 894 802 L894 930 Z" fill="#1e0804" ' + st(4) + '/>';
    if (lit) {
      s += '<ellipse cx="800" cy="900" rx="90" ry="40" fill="#ff8a1a" opacity=".8"/>'
        + '<g class="sc-flame sc-fl-a"><path d="M800 928 C730 928 716 880 738 840 C744 868 760 868 762 852 C750 806 772 772 796 740 C796 786 820 790 826 770 C846 802 864 830 858 870 C872 860 874 846 872 836 C890 880 866 928 800 928 Z" fill="url(#sc-fire)" stroke="#8a1e0a" stroke-width="3"/></g>'
        + '<g class="sc-flame sc-fl-b"><path d="M800 928 C764 928 756 900 768 878 C774 892 782 892 782 880 C780 856 792 836 804 820 C806 846 818 850 822 838 C836 860 842 884 834 904 C828 920 816 928 800 928 Z" fill="#fff4b8"/></g>'
        + '<path d="M706 930 H894" ' + st(4) + '/>';
    } else {
      s += '<g fill="#3a1a10" ' + st(3) + '><rect x="740" y="890" width="120" height="18" rx="9" transform="rotate(-8 800 899)"/><rect x="744" y="900" width="116" height="18" rx="9" transform="rotate(10 800 909)"/></g>'
        + '<ellipse class="sc-ember" cx="800" cy="918" rx="60" ry="12" fill="#ff7a1a" opacity=".55"/>'
        + '<g fill="#ffb04a"><circle cx="772" cy="914" r="4"/><circle cx="812" cy="920" r="3"/><circle cx="832" cy="912" r="3.5"/></g>';
    }
    s += '<rect x="600" y="924" width="400" height="30" rx="8" fill="#8a4a24" ' + st(5) + '/>';
    return s + '</g>';
  }
  function cat(sleep) {
    // рудий кіт: спить калачиком на сонці / сидить і дивиться на вогонь
    if (sleep) {
      return '<g transform="translate(1100 918)">'
        + '<ellipse cx="0" cy="22" rx="86" ry="10" fill="#2a0f06" opacity=".3"/>'
        + '<g class="sc-tail"><path d="M60 14 C96 10 104 -14 86 -26" fill="none" stroke="' + O + '" stroke-width="22" stroke-linecap="round"/>'
        + '<path d="M60 14 C96 10 104 -14 86 -26" fill="none" stroke="#f0a050" stroke-width="13" stroke-linecap="round"/></g>'
        + '<path d="M-70 18 C-76 -20 -40 -44 6 -42 C50 -40 76 -16 70 18 Z" fill="#f0a050" ' + st(5) + '/>'
        + '<g fill="none" stroke="#c46a20" stroke-width="6" stroke-linecap="round"><path d="M-10 -40 C-6 -28 -6 -20 -10 -10"/><path d="M14 -40 C18 -28 18 -20 14 -10"/><path d="M38 -32 C40 -22 40 -16 36 -8"/></g>'
        + '<path d="M-96 18 C-100 -6 -86 -22 -64 -22 C-42 -22 -30 -6 -34 18 Z" fill="#f6b468" ' + st(5) + '/>'
        + '<path d="M-90 -12 L-92 -40 L-70 -22 Z M-50 -22 L-36 -42 L-38 -12 Z" fill="#f0a050" ' + st(4) + '/>'
        + '<path d="M-82 0 q6 5 12 0 M-58 0 q6 5 12 0" fill="none" ' + st(3.5) + '/>'
        + '<path d="M-67 9 l3 3 l3 -3" fill="#e0607a" ' + st(2.4) + '/>'
        + '</g>';
    }
    return '<g transform="translate(1086 900)">'
      + '<ellipse cx="0" cy="40" rx="70" ry="10" fill="#140806" opacity=".45"/>'
      + '<path d="M40 34 C90 36 100 0 76 -12" fill="none" stroke="' + O + '" stroke-width="20" stroke-linecap="round"/><path d="M40 34 C90 36 100 0 76 -12" fill="none" stroke="#c87a3a" stroke-width="11" stroke-linecap="round"/>'
      + '<path d="M-50 40 C-56 -10 -36 -50 0 -52 C36 -50 56 -10 50 40 Z" fill="#c87a3a" ' + st(5) + '/>'
      + '<path d="M-36 -34 C-44 -70 -28 -96 0 -96 C28 -96 44 -70 36 -34 C24 -24 -24 -24 -36 -34 Z" fill="#d88a46" ' + st(5) + '/>'
      + '<path d="M-34 -70 L-36 -108 L-10 -90 Z M34 -70 L36 -108 L10 -90 Z" fill="#d88a46" ' + st(4) + '/>'
      + '<path d="M-50 10 C-30 -10 -30 -30 -40 -40" stroke="#ffb04a" stroke-width="5" fill="none" opacity=".6" stroke-linecap="round"/>'
      + '</g>';
  }
  function hang(x) {
    // в'язка червоного перцю з балки: гроно стручків, зелені хвостики, мотузка
    const pod = 'M0 0 C9 3 12 22 3 50 C-5 34 -8 12 0 0 Z';
    let g = '<path d="M' + x + ' 40 V118" stroke="#8a6a3a" stroke-width="4"/>';
    [[-14, 112, 28], [14, 112, -28], [-8, 136, 14], [10, 138, -14], [0, 160, 4], [-16, 160, 30], [16, 162, -26]].forEach(([dx, y, a], i) => {
      g += '<g transform="translate(' + (x + dx) + ' ' + y + ') rotate(' + a + ')"><path d="' + pod + '" fill="' + (i % 3 === 2 ? '#ff4a2a' : '#d4202a') + '" stroke="#3b1b10" stroke-width="3" stroke-linejoin="round"/>'
        + '<path d="M-2 8 C-2 18 -1 28 1 36" stroke="#fff" stroke-width="2.4" opacity=".45" fill="none" stroke-linecap="round"/>'
        + '<path d="M0 2 l-3 -9" stroke="#2f7a3a" stroke-width="5" stroke-linecap="round"/></g>';
    });
    return g + '<path d="M' + (x - 10) + ' 108 Q' + x + ' 118 ' + (x + 10) + ' 108" fill="none" stroke="#8a6a3a" stroke-width="5" stroke-linecap="round"/>';
  }
  function scene(night) {
    let s = '<svg viewBox="0 0 1600 1000" preserveAspectRatio="xMidYMid slice" class="sc-scene ' + (night ? 'sc-night' : 'sc-day') + '" xmlns="http://www.w3.org/2000/svg">'
      + SDEFS
      + '<rect width="1600" height="1000" fill="url(#' + (night ? 'sc-s-walln' : 'sc-s-wall') + ')"/>'
      + '<g fill="' + (night ? '#000' : '#c48a50') + '" opacity=".08"><ellipse cx="240" cy="160" rx="160" ry="60"/><ellipse cx="1340" cy="120" rx="200" ry="70"/><ellipse cx="1180" cy="760" rx="220" ry="40"/></g>'
      + '<rect x="0" y="0" width="1600" height="46" fill="#5a3018"/><rect x="0" y="40" width="1600" height="10" fill="#3b1b10"/>'
      + hang(150) + hang(470) + hang(1130) + hang(1450)
      + '<rect x="0" y="690" width="1600" height="70" fill="' + (night ? '#4a2a5a' : '#2f6fe0') + '" opacity="' + (night ? '.6' : '.85') + '"/>'
      + '<path d="M0 696 H1600" stroke="#ffd23a" stroke-width="5" opacity=".8"/>'
      + windowSvg(night)
      + shelves()
      + '<rect x="0" y="760" width="1600" height="240" fill="url(#' + (night ? 'sc-s-floorn' : 'sc-s-floor') + ')"/>'
      + '<path d="M0 760 H1600" stroke="#3b1b10" stroke-width="5"/>'
      + wheel() + kiln(night) + cat(!night);
    if (night) {
      s += '<rect width="1600" height="1000" fill="url(#sc-s-vig)"/>'
        + '<rect class="sc-heat" width="1600" height="1000" fill="url(#sc-s-heat)"/>'
        + '<g fill="#ffd25a" stroke="#ff7a1a" stroke-width="2">'
        + [[770, 7, -0.2, 2.8], [812, 5, -1.1, 3.4], [840, 6.5, -2, 2.6], [790, 4.5, -1.6, 3.1], [826, 6, -0.7, 3.6], [905, 5.5, -1.3, 2.4]]
          .map(([x, r, d, t], i) => '<circle class="sc-spark" cx="' + x + '" cy="' + (i === 5 ? 470 : 720) + '" r="' + r + '" style="animation-delay:' + d + 's;animation-duration:' + t + 's"/>').join('')
        + '</g>';
    } else {
      s += '<path class="sc-ray" d="M650 160 L960 160 L880 1000 L300 1000 Z" fill="url(#sc-s-ray)"/>'
        + '<ellipse cx="600" cy="905" rx="300" ry="54" fill="#fff2c0" opacity=".3"/>'
        + '<g fill="#fffbe6">'
        + [[700, 330, 7, -1, 7], [790, 420, 5, -3, 8], [640, 520, 8, -2, 9], [740, 600, 5, -5, 7.5], [560, 690, 7, -4, 8.5], [680, 760, 5, -6, 6.5], [500, 830, 7, -2.5, 9.5], [620, 880, 5, -7, 7]]
          .map(([x, y, r, d, t]) => '<circle class="sc-mote" cx="' + x + '" cy="' + y + '" r="' + r + '" style="animation-delay:' + d + 's;animation-duration:' + t + 's"/>').join('')
        + '</g>';
    }
    return s + '</svg>';
  }

  // ---------- логотип ----------
  function logo() {
    const T = 'text-anchor="middle" ' + FONT;
    const L1 = 'x="280" y="84" font-size="72" letter-spacing="3"', L2 = 'x="280" y="198" font-size="132" letter-spacing="-2"';
    const fl = (x, y, s) => {
      let p = '<g transform="translate(' + x + ' ' + y + ') scale(' + s + ')">';
      for (let i = 0; i < 5; i++) p += '<path d="' + petal(6, 16) + '" transform="rotate(' + (i * 72) + ')" fill="#e03b2c" stroke="#2a1009" stroke-width="2"/>';
      return p + '<circle r="5" fill="#ffd23a" stroke="#2a1009" stroke-width="2"/></g>';
    };
    const leaf = (x, y, a) => '<path d="' + petal(6, 22) + '" transform="translate(' + x + ' ' + y + ') rotate(' + a + ')" fill="#34a852" stroke="#2a1009" stroke-width="2"/>';
    const sh = (pts, x, y, r) => '<g transform="translate(' + x + ' ' + y + ') rotate(' + r + ')"><polygon points="' + pts + '" transform="translate(2 3)" fill="#7a2410" stroke="#2a1009" stroke-width="3" stroke-linejoin="round"/>'
      + '<polygon points="' + pts + '" fill="url(#sc-lg-t)" stroke="#2a1009" stroke-width="3" stroke-linejoin="round"/></g>';
    return '<svg viewBox="0 0 560 230" class="sc-logo" xmlns="http://www.w3.org/2000/svg"><defs>'
      + '<linearGradient id="sc-lg-t" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffe08a"/><stop offset=".38" stop-color="#ff8a3a"/><stop offset=".72" stop-color="#d0451c"/><stop offset="1" stop-color="#8a2410"/></linearGradient>'
      + '<linearGradient id="sc-lg-b" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#d4ecff"/><stop offset=".4" stop-color="#5a9cff"/><stop offset=".8" stop-color="#1f4fc0"/><stop offset="1" stop-color="#143380"/></linearGradient>'
      + '<linearGradient id="sc-lg-h" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".85"/><stop offset=".42" stop-color="#fff" stop-opacity=".15"/><stop offset=".43" stop-color="#fff" stop-opacity="0"/></linearGradient>'
      + '<clipPath id="sc-lg-c"><text ' + T + ' ' + L2 + '>ГЛЕКИ</text></clipPath>'
      + '</defs>'
      + leaf(78, 58, -120) + leaf(84, 66, -60) + fl(66, 50, 1) + leaf(482, 58, 120) + leaf(476, 66, 60) + fl(494, 50, 1)
      + '<g ' + T + '>'
      + '<text ' + L1 + ' transform="translate(0 7)" fill="#2a1009" stroke="#2a1009" stroke-width="18" stroke-linejoin="round">РОЗБИТІ</text>'
      + '<text ' + L1 + ' fill="url(#sc-lg-b)" stroke="#2a1009" stroke-width="11" stroke-linejoin="round" paint-order="stroke">РОЗБИТІ</text>'
      + '<text ' + L1 + ' fill="url(#sc-lg-h)">РОЗБИТІ</text>'
      + '<text ' + L2 + ' transform="translate(0 10)" fill="#2a1009" stroke="#2a1009" stroke-width="22" stroke-linejoin="round">ГЛЕКИ</text>'
      + '<text ' + L2 + ' fill="url(#sc-lg-t)" stroke="#2a1009" stroke-width="13" stroke-linejoin="round" paint-order="stroke">ГЛЕКИ</text>'
      + '<text ' + L2 + ' fill="url(#sc-lg-h)">ГЛЕКИ</text>'
      + '</g>'
      + '<g clip-path="url(#sc-lg-c)" fill="none" stroke-linejoin="round" stroke-linecap="round">'
      + '<path d="M236 92 L248 122 L232 140 L252 162 L240 200" stroke="#2a1009" stroke-width="5"/>'
      + '<path d="M239 92 L251 122 L235 140 L255 162 L243 200" stroke="#ffe7c0" stroke-width="1.6"/>'
      + '<path d="M248 122 L268 128 M252 162 L270 170" stroke="#2a1009" stroke-width="3.5"/>'
      + '<path d="M396 92 L388 116 L404 132 L396 150" stroke="#2a1009" stroke-width="4.5"/>'
      + '<path d="M399 92 L391 116 L407 132 L399 150" stroke="#ffe7c0" stroke-width="1.4"/>'
      + '<path d="M474 90 L460 108 L470 120" stroke="#2a1009" stroke-width="4"/>'
      + '</g>'
      + '<g stroke="#fff" stroke-width="3" stroke-linecap="round" opacity=".85"><path d="M478 96 L492 86"/><path d="M482 116 L500 114"/><path d="M470 80 L474 66"/></g>'
      + sh('0,0 18,-6 22,8 8,16', 498, 74, 20) + sh('0,0 14,-4 12,12', 508, 108, -30) + sh('0,0 10,-8 14,4 6,10', 476, 48, 40)

      + '</svg>';
  }
  const LOGO = logo();

  // ---------- афіша ----------
  function poster() {
    const C = [104, 170];                       // центр вибуху глека
    let rays = '';
    for (let i = 0; i < 20; i++) {
      const a1 = i * Math.PI / 10, a2 = a1 + 0.13;
      rays += '<path d="M' + C[0] + ' ' + C[1] + ' L' + (C[0] + 460 * Math.cos(a1)).toFixed(0) + ' ' + (C[1] + 460 * Math.sin(a1)).toFixed(0)
        + ' L' + (C[0] + 460 * Math.cos(a2)).toFixed(0) + ' ' + (C[1] + 460 * Math.sin(a2)).toFixed(0) + ' Z"/>';
    }
    // черепки різних виробів: [ключ, № черепка, x, y, кут, розмір]
    const fl = [['glek', 0, 10, 124, -20, 30], ['k1', 1, 170, 106, 25, 32], ['k4', 2, 0, 178, 40, 30], ['glek', 3, 182, 154, -35, 30],
      ['k3', 0, 52, 92, 60, 26], ['glek', 5, 180, 204, 15, 24], ['k2', 3, 30, 214, -60, 22], ['bowl', 1, 140, 96, 80, 20], ['k1', 2, 2, 96, 30, 20]];
    let streaks = '<g stroke="#fff8d8" stroke-width="2.6" stroke-linecap="round" opacity=".75">', flying = '';
    fl.forEach(([k, i, x, y, r, sz]) => {
      const mx = x + sz / 2, my = y + sz / 2, dx = mx - C[0], dy = my - C[1];
      streaks += '<path d="M' + (mx - dx * 0.4).toFixed(0) + ' ' + (my - dy * 0.4).toFixed(0) + ' L' + (mx - dx * 0.12).toFixed(0) + ' ' + (my - dy * 0.12).toFixed(0) + '"/>';
      flying += '<g transform="rotate(' + r + ' ' + mx + ' ' + my + ')">' + (function (a) { return a[i % a.length]; })(shards(k)).replace('<svg ', '<svg x="' + x + '" y="' + y + '" width="' + sz + '" height="' + sz + '" ') + '</g>';
    });
    streaks += '</g>';
    const CR = 'M50 31 L45 45 L57 54 L43 66 L53 78 L46 90 M57 54 L73 49 L82 60 M43 66 L27 61 L20 71 M45 45 L32 40';
    const crack = '<g fill="none" stroke-linejoin="round" stroke-linecap="round">'
      + '<path d="' + CR + '" stroke="#ffe680" stroke-width="9" opacity=".45"/>'
      + '<path d="' + CR + '" stroke="#3b1b10" stroke-width="4"/>'
      + '<path d="' + CR + '" stroke="#fff3a0" stroke-width="1.8"/></g>';
    let spark = '';
    [[205, 70, 1], [348, 88, .7], [262, 226, .6], [196, 128, .55], [26, 72, .6]].forEach(([x, y, k]) => {
      spark += '<path transform="translate(' + x + ' ' + y + ') scale(' + k + ')" d="M0 -10 C1 -3 3 -1 10 0 C3 1 1 3 0 10 C-1 3 -3 1 -10 0 C-3 -1 -1 -3 0 -10 Z" fill="#fff"/>';
    });
    return '<svg viewBox="0 0 360 240" class="sc-poster" xmlns="http://www.w3.org/2000/svg">'
      + '<defs>' + defsInner
      + '<radialGradient id="sc-p-bg" cx=".3" cy=".7" r=".85"><stop offset="0" stop-color="#ffefb0"/><stop offset=".3" stop-color="#f8a845"/><stop offset=".7" stop-color="#c2481c"/><stop offset="1" stop-color="#5a1608"/></radialGradient>'
      + '<radialGradient id="sc-p-burst"><stop offset="0" stop-color="#fffbe0"/><stop offset=".45" stop-color="#ffe27a" stop-opacity=".85"/><stop offset="1" stop-color="#ffb04a" stop-opacity="0"/></radialGradient>'
      + '<radialGradient id="sc-p-vig" cx=".5" cy=".5" r=".72"><stop offset=".6" stop-color="#2a0a04" stop-opacity="0"/><stop offset="1" stop-color="#2a0a04" stop-opacity=".55"/></radialGradient>'
      + '</defs>'
      + '<rect width="360" height="240" fill="url(#sc-p-bg)"/>'
      + '<g fill="#fff6c8" opacity=".2">' + rays + '</g>'
      + '<rect x="0" y="220" width="360" height="20" fill="url(#sc-l-wood)" stroke="#3b1b10" stroke-width="3"/>'
      + '<path d="M0 224 H360" stroke="#fff" stroke-width="2" opacity=".45"/>'
      + '<circle cx="' + C[0] + '" cy="' + C[1] + '" r="84" fill="url(#sc-p-burst)"/>'
      + streaks
      + '<svg x="22" y="86" width="164" height="164" viewBox="0 0 100 100" overflow="visible"><g transform="rotate(-8 50 60)">' + GLEK + crack + '</g></svg>'
      + flying
      + multiplier('×100').replace('<svg ', '<svg x="214" y="84" width="146" height="146" ')
      + '<rect width="360" height="240" fill="url(#sc-p-vig)"/>'
      + LOGO.replace('<svg ', '<svg x="50" y="-3" width="260" height="107" ')
      + spark
      + '<rect x="1.5" y="1.5" width="357" height="237" rx="3" fill="none" stroke="#3b1b10" stroke-width="3"/>'
      + '</svg>';
  }

  window.SlotArt = window.SlotArt || {};
  window.SlotArt['slot-cascade'] = {
    title: 'Розбиті глеки',
    symbols,
    scene: { base: scene(false), bonus: scene(true) },
    logo: LOGO,
    poster: poster(),
    extras: {
      defs: DEFS,
      shards,                 // (key) → масив 3–6 SVG-рядків (viewBox -2 -2 44 44), самодостатні (свої градієнти)
      multiplier,             // ('×5') → писанка з числом; ×2–×5 проста, ×10–×25 розписна, ×50+ золота з сяйвом
      counter: COUNTER,       // дощечка лічильника (див. коментар над COUNTER)
      frame: FRAME,           // стелаж 6×5 (див. коментар над frame())
    },
  };
})();
