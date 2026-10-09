/* Однорукий Глек (slot-glek) — арт автомата. Звичайний скрипт, реєструє SlotArt['slot-glek'].
 *
 * Символи — <svg viewBox="0 0 100 100" class="sym sg sg-<ключ>">, градієнти — з extras.defs (id з префіксом sg-).
 * Анімовані групи: .a-sway .a-bob .a-wob .a-jump .a-ring .a-clap .a-spin .a-pulse .a-flash .a-wink .a-brow .a-stache
 * .a-rays .a-aura .a-glint — вмикаються класом .win / .glint на предку (див. slot-glek-art.css).
 *
 * CABINET (extras.cabinet) — viewBox 0 0 420 640 (усе нижче — у цих координатах):
 *   вікно барабанів 3×3 — x 56…364, y 182…490 (308×308, прозоре, кути r=10); комірка ≈ 102,7.
 *     по краю вікна всередині — нічого не намальовано; бронзова рамка — зовні вікна (42…378, 168…504).
 *     маркери рядків (ромбики на рамці): y 233, 336, 439 при x 49 і 371.
 *   табло виграшу — x 76…344, y 522…564 (268×42, темне скло), бронзова оправа 64…356, 512…574.
 *   місце під логотип — x 44…376, y 20…140 (332×120, оксамит; логотип 560×200 вписується з полями).
 *   гнізда лампочок — extras.bulbSpots: 67 точок [x, y] (центри, r≈7) по периметру за годинниковою стрілкою
 *     (лівий бік знизу → дуга → правий бік донизу) — зручно для «біжучих» вогнів; кіт ставить туди extras.bulb ~18 px.
 *   ручку (extras.handle) — справа від корпуса: точка кріплення ручки (30,172 у її viewBox 60×200)
 *     ставити на x≈424, y≈340 корпуса (поруч бронзова накладка 398…416, 312…368); ручка в масштабі 1:1.
 *   extras.layout — ці самі числа об'єктом.
 */
(function () {
  'use strict';
  const P = {
    ink: '#2b1420', // обведення — темна слива сцени
    red: '#e3122a', gold: '#f5c33b', cream: '#fff6e0', wood: '#6e3a1c', brass: '#e8b83e',
  };
  const I = P.ink;
  const r2 = (n) => Math.round(n * 100) / 100;

  /* ---------- помічники ---------- */
  const stops = (a) => a.map((s) => '<stop offset="' + s[0] + '" stop-color="' + s[1] + '"' + (s[2] != null ? ' stop-opacity="' + s[2] + '"' : '') + '/>').join('');
  const lg = (id, x1, y1, x2, y2, s) => '<linearGradient id="' + id + '" x1="' + x1 + '" y1="' + y1 + '" x2="' + x2 + '" y2="' + y2 + '">' + stops(s) + '</linearGradient>';
  const rg = (id, cx, cy, r, s, fx, fy) => '<radialGradient id="' + id + '" cx="' + cx + '" cy="' + cy + '" r="' + r + '"' + (fx != null ? ' fx="' + fx + '" fy="' + fy + '"' : '') + '>' + stops(s) + '</radialGradient>';
  const rr = (x, y, w, h, r) => 'M' + (x + r) + ' ' + y + 'H' + (x + w - r) + 'A' + r + ' ' + r + ' 0 0 1 ' + (x + w) + ' ' + (y + r) + 'V' + (y + h - r) + 'A' + r + ' ' + r + ' 0 0 1 ' + (x + w - r) + ' ' + (y + h) + 'H' + (x + r) + 'A' + r + ' ' + r + ' 0 0 1 ' + x + ' ' + (y + h - r) + 'V' + (y + r) + 'A' + r + ' ' + r + ' 0 0 1 ' + (x + r) + ' ' + y + 'Z';

  /* вишивка хрестиком: rows — рядки візерунка, cols — символ→колір ('.' — порожньо) */
  function stitch(x0, y0, s, rows, cols) {
    const by = {};
    rows.forEach((row, j) => {
      for (let i = 0; i < row.length; i++) {
        const c = cols[row[i]]; if (!c) continue;
        const x = x0 + i * s, y = y0 + j * s, a = s * 0.16, b = s * 0.68;
        (by[c] = by[c] || []).push('M' + r2(x + a) + ' ' + r2(y + a) + 'l' + r2(b) + ' ' + r2(b) + 'M' + r2(x + a + b) + ' ' + r2(y + a) + 'l' + r2(-b) + ' ' + r2(b));
      }
    });
    return Object.keys(by).map((c) => '<path d="' + by[c].join('') + '" stroke="' + c + '" stroke-width="' + r2(s * 0.34) + '" stroke-linecap="round" fill="none"/>').join('');
  }
  const ROMB = ['...x...', '..xox..', '.xo.ox.', 'xo.x.ox', '.xo.ox.', '..xox..', '...x...'];
  const ROMB5 = ['..x..', '.xox.', 'xo.ox', '.xox.', '..x..'];

  /* зірочка-блиск (для .glint) */
  const spark = (x, y, r) => '<g transform="translate(' + x + ' ' + y + ')"><g class="a-glint"><path d="M0 ' + (-r) + 'C' + r2(r * .12) + ' ' + r2(-r * .12) + ' ' + r2(r * .12) + ' ' + r2(-r * .12) + ' ' + r + ' 0C' + r2(r * .12) + ' ' + r2(r * .12) + ' ' + r2(r * .12) + ' ' + r2(r * .12) + ' 0 ' + r + 'C' + r2(-r * .12) + ' ' + r2(r * .12) + ' ' + r2(-r * .12) + ' ' + r2(r * .12) + ' ' + (-r) + ' 0C' + r2(-r * .12) + ' ' + r2(-r * .12) + ' ' + r2(-r * .12) + ' ' + r2(-r * .12) + ' 0 ' + (-r) + 'Z" fill="#fff"/><circle r="' + r2(r * .28) + '" fill="#fff"/></g></g>';
  const shadow = (cx, cy, rx, ry) => '<ellipse cx="' + cx + '" cy="' + cy + '" rx="' + rx + '" ry="' + ry + '" fill="url(#sg-shadow)"/>';
  const sym = (k, inner) => '<svg viewBox="0 0 100 100" class="sym sg sg-' + k + '" aria-hidden="true">' + inner + '</svg>';
  const stem = (d, w) => '<path d="' + d + '" fill="none" stroke="' + I + '" stroke-width="' + (w + 3.4) + '" stroke-linecap="round"/><path d="' + d + '" fill="none" stroke="#7a5222" stroke-width="' + w + '" stroke-linecap="round"/>';

  /* петриківська квітка: пелюстки-краплі, серединка, листочки, зернятка */
  function petal(cx, cy, ang, len, w, col, sw) {
    return '<path transform="translate(' + r2(cx) + ' ' + r2(cy) + ') rotate(' + r2(ang) + ')" d="M0 0C' + r2(w) + ' ' + r2(-len * .35) + ' ' + r2(w * .7) + ' ' + r2(-len) + ' 0 ' + r2(-len) + 'C' + r2(-w * .7) + ' ' + r2(-len) + ' ' + r2(-w) + ' ' + r2(-len * .35) + ' 0 0Z" fill="' + col + '" stroke="' + I + '" stroke-width="' + sw + '" stroke-linejoin="round"/>';
  }
  function petryk(cx, cy, k, flip) {
    const f = flip ? -1 : 1; let h = '';
    h += petal(cx - 10 * k * f, cy + 6 * k, -60 * f, 22 * k, 7 * k, '#3f9a3a', 1.6 * k);
    h += petal(cx + 12 * k * f, cy + 6 * k, 64 * f, 20 * k, 6.5 * k, '#3f9a3a', 1.6 * k);
    for (let i = 0; i < 5; i++) h += petal(cx, cy, -70 + i * 35, 15 * k, 6 * k, i % 2 ? '#f5c33b' : '#e3122a', 1.6 * k);
    h += '<circle cx="' + cx + '" cy="' + cy + '" r="' + r2(4.5 * k) + '" fill="#f5c33b" stroke="' + I + '" stroke-width="' + r2(1.6 * k) + '"/>';
    for (let i = 0; i < 4; i++) h += '<circle cx="' + r2(cx + f * (20 + i * 7) * k) + '" cy="' + r2(cy - (8 + i * 4) * k) + '" r="' + r2((2.6 - i * .5) * k) + '" fill="#e3122a"/>';
    for (let i = 0; i < 4; i++) h += '<circle cx="' + r2(cx - f * (19 + i * 7) * k) + '" cy="' + r2(cy - (6 + i * 4) * k) + '" r="' + r2((2.4 - i * .45) * k) + '" fill="#f5c33b"/>';
    return h;
  }

  /* ---------- спільні градієнти ---------- */
  const GRADS =
    rg('sg-shadow', .5, .5, .5, [[0, '#000', .42], [.6, '#000', .18], [1, '#000', 0]]) +
    rg('sg-red', .36, .3, .78, [[0, '#ffb3a6'], [.2, '#ff4a52'], [.62, '#d10f2c'], [1, '#6e061b']]) +
    lg('sg-leaf', 0, 0, 1, 1, [[0, '#c4ef6a'], [.45, '#5cb83c'], [1, '#2a6a2a']]) +
    rg('sg-pear', .36, .42, .78, [[0, '#fffbc4'], [.28, '#eaea62'], [.7, '#a9cc3a'], [1, '#5f8c1f']]) +
    rg('sg-plum', .34, .3, .8, [[0, '#e6c8ff'], [.22, '#a861ea'], [.65, '#5f27ad'], [1, '#2c1062']]) +
    rg('sg-rind', .38, .3, .8, [[0, '#a7ea6a'], [.45, '#45a83a'], [1, '#1c5a24']]) +
    rg('sg-flesh', .5, .05, .95, [[0, '#ffb0a8'], [.35, '#ff4a5a'], [1, '#c4102f']]) +
    lg('sg-gold', 0, 0, 1, 0, [[0, '#a8650c'], [.22, '#f7c843'], [.34, '#fff7cc'], [.46, '#ffd75a'], [.78, '#d18b16'], [1, '#8a520a']]) +
    lg('sg-gold-v', 0, 0, 0, 1, [[0, '#fff6c0'], [.4, '#f9cd4a'], [.8, '#d08a17'], [1, '#93560c']]) +
    lg('sg-steel', 0, 0, 1, 0, [[0, '#4a5a86'], [.22, '#cfe0fa'], [.36, '#ffffff'], [.58, '#9fb6e0'], [.82, '#6a80b0'], [1, '#3a4670']]) +
    lg('sg-seven', 0, 0, 0, 1, [[0, '#ff8f7c'], [.4, '#f21c34'], [1, '#9c0820']]) +
    rg('sg-clay', .36, .32, .82, [[0, '#ffd6a8'], [.3, '#f59c56'], [.72, '#cc6a30'], [1, '#8a3e1c']]) +
    lg('sg-clay-l', 0, 0, 0, 1, [[0, '#f6b47a'], [1, '#bf6630']]) +
    rg('sg-medal', .5, .32, .7, [[0, '#8fd0ff'], [.5, '#2f74d6'], [1, '#14306e']]) +
    rg('sg-glow', .5, .5, .5, [[0, '#fff6c0', .95], [.42, '#ffd23a', .6], [1, '#ff9a1a', 0]]) +
    lg('sg-ribbon', 0, 0, 0, 1, [[0, '#ff6a5a'], [.5, '#d0142e'], [1, '#8a0a1e']]) +
    lg('sg-wood', 0, 0, 1, 1, [[0, '#a8622e'], [.45, '#7a4020'], [1, '#4e2612']]) +
    lg('sg-wood2', 0, 0, 0, 1, [[0, '#c27a40'], [1, '#7a4220']]) +
    lg('sg-wood3', 0, 0, 0, 1, [[0, '#5a2c14'], [1, '#341608']]) +
    lg('sg-brass', 0, 0, 0, 1, [[0, '#fff2b0'], [.3, '#efc24a'], [.62, '#b4791c'], [.82, '#f2cc62'], [1, '#8a5a12']]) +
    lg('sg-brass-h', 0, 0, 1, 0, [[0, '#9a6414'], [.3, '#f6d470'], [.45, '#fff4c0'], [.7, '#d9a032'], [1, '#8a5a12']]) +
    rg('sg-velvet', .5, .35, .75, [[0, '#b02a40'], [.7, '#6a0c22'], [1, '#3e0614']]) +
    rg('sg-glass', .4, .35, .7, [[0, '#ffffff'], [.3, '#fff4a6'], [.7, '#ffc21a'], [1, '#ff8a00']]) +
    rg('sg-glass-off', .4, .35, .7, [[0, '#9a7a56'], [1, '#4a2e1c']]) +
    rg('sg-halo', .5, .5, .5, [[0, '#fff2a0', .95], [.5, '#ffbe2a', .45], [1, '#ff9a00', 0]]) +
    rg('sg-skin', .42, .36, .75, [[0, '#ffe6cc'], [.6, '#f6bd92'], [1, '#d68a5e']]) +
    rg('sg-scarf', .4, .3, .85, [[0, '#ff6a58'], [.55, '#d4182e'], [1, '#7e0a1e']]) +
    lg('sg-card', 0, 0, 0, 1, [[0, '#fffdf6'], [1, '#efe2c4']]) +
    lg('sg-cardback', 0, 0, 1, 1, [[0, '#b81c34'], [1, '#5a0a1c']]) +
    lg('sg-spade', 0, 0, 0, 1, [[0, '#5e4c6a'], [.5, '#2e2236'], [1, '#140c18']]) +
    lg('sg-tablo', 0, 0, 0, 1, [[0, '#0c0608'], [1, '#2c1418']]) +
    lg('sg-teal', 0, 0, 0, 1, [[0, '#2f7280'], [1, '#14343e']]) +
    rg('sg-ternova', .5, .3, .8, [[0, '#6a2a4c'], [.55, '#341028'], [1, '#1c0612']]) +
    lg('sg-shirt', 0, 0, 0, 1, [[0, '#fffdf6'], [1, '#e6d9be']]) +
    lg('sg-vest', 0, 0, 1, 1, [[0, '#3a2a52'], [1, '#160e24']]);
  const PATS =
    '<pattern id="sg-cardpat" width="12" height="12" patternUnits="userSpaceOnUse">' + stitch(1.5, 1.5, 1.8, ROMB5, { x: '#f5c33b', o: '#ffe9a8' }) + '</pattern>' +
    '<clipPath id="sg-clip-melon"><ellipse rx="34" ry="27"/></clipPath>';
  const DEFS_INNER = GRADS + PATS;
  const defs = '<svg width="0" height="0" style="position:absolute" aria-hidden="true" focusable="false"><defs>' + DEFS_INNER + '</defs></svg>';

  /* ---------- символи ---------- */
  const cherry = sym('cherry',
    shadow(50, 92, 34, 6) +
    '<g class="a-sway">' +
    stem('M53 16C46 30 37 40 33 47', 3.6) + stem('M53 16C58 31 65 42 68 51', 3.6) +
    '<path d="M53 16C60 3 80 1 91 9C83 22 64 26 53 16Z" fill="url(#sg-leaf)" stroke="' + I + '" stroke-width="3" stroke-linejoin="round"/>' +
    '<path d="M57 15C67 11 77 10 86 10" fill="none" stroke="#2a6a2a" stroke-width="1.8" stroke-linecap="round" opacity=".7"/>' +
    '<circle cx="53" cy="16" r="4.2" fill="#7a5222" stroke="' + I + '" stroke-width="2.6"/>' +
    '<g class="a-c1"><circle cx="31" cy="67" r="21" fill="url(#sg-red)" stroke="' + I + '" stroke-width="3"/>' +
    '<path d="M27 48.5q4-3 8 0" fill="none" stroke="#6e061b" stroke-width="2.6" stroke-linecap="round"/>' +
    '<ellipse cx="23" cy="58" rx="7" ry="4.4" transform="rotate(-38 23 58)" fill="#fff" opacity=".88"/><circle cx="17.5" cy="68" r="2.2" fill="#fff" opacity=".6"/></g>' +
    '<g class="a-c2"><circle cx="68" cy="71" r="21" fill="url(#sg-red)" stroke="' + I + '" stroke-width="3"/>' +
    '<path d="M64 52.5q4-3 8 0" fill="none" stroke="#6e061b" stroke-width="2.6" stroke-linecap="round"/>' +
    '<ellipse cx="60" cy="62" rx="7" ry="4.4" transform="rotate(-38 60 62)" fill="#fff" opacity=".88"/><circle cx="54.5" cy="72" r="2.2" fill="#fff" opacity=".6"/></g>' +
    '</g>' + spark(22, 57, 9));

  const pear = sym('pear',
    shadow(50, 93, 28, 5.5) +
    '<g class="a-bob"><g transform="rotate(9 50 58)">' +
    stem('M50 21C50 13 53 8 58 5', 3.6) +
    '<path d="M55 12C63 3 78 5 83 11C75 20 62 20 55 12Z" fill="url(#sg-leaf)" stroke="' + I + '" stroke-width="2.6" stroke-linejoin="round"/>' +
    '<path d="M50 19C41 19 38 30 38 38C38 47 21 55 21 71C21 86 35 94 50 94C65 94 79 86 79 71C79 55 62 47 62 38C62 30 59 19 50 19Z" fill="url(#sg-pear)" stroke="' + I + '" stroke-width="3" stroke-linejoin="round"/>' +
    '<path d="M65 50C77 58 81 74 73 84C67 90 59 93 52 93C66 86 74 72 65 50Z" fill="#4e7a12" opacity=".32"/>' +
    '<ellipse cx="65" cy="72" rx="9" ry="8" fill="#ff7a3a" opacity=".28"/>' +
    '<ellipse cx="32" cy="68" rx="5" ry="11" transform="rotate(16 32 68)" fill="#fff" opacity=".75"/>' +
    '<ellipse cx="45" cy="31" rx="2.4" ry="5" transform="rotate(12 45 31)" fill="#fff" opacity=".6"/>' +
    '<g fill="#6a7a14" opacity=".55"><circle cx="57" cy="78" r="1.2"/><circle cx="62" cy="62" r="1.1"/><circle cx="44" cy="84" r="1.1"/><circle cx="52" cy="58" r="1"/><circle cx="68" cy="82" r="1"/></g>' +
    '</g></g>' + spark(31, 63, 9));

  const plum = sym('plum',
    shadow(50, 92, 30, 5.5) +
    '<g class="a-wob">' +
    stem('M51 27C51 18 54 12 59 8', 3.6) +
    '<path d="M55 15C49 4 32 2 24 9C31 20 46 22 55 15Z" fill="url(#sg-leaf)" stroke="' + I + '" stroke-width="2.6" stroke-linejoin="round"/>' +
    '<path d="M31 11C40 12 47 14 54 15" fill="none" stroke="#2a6a2a" stroke-width="1.6" stroke-linecap="round" opacity=".7"/>' +
    '<path d="M50 28C55 23 65 24 72 28C85 36 87 56 81 71C73 89 56 92 46 90C29 88 15 76 15 58C15 40 29 26 41 26C45 26 48 27 50 28Z" fill="url(#sg-plum)" stroke="' + I + '" stroke-width="3" stroke-linejoin="round"/>' +
    '<path d="M50 30C43 43 43 66 52 87" fill="none" stroke="#2c1062" stroke-width="2.6" stroke-linecap="round" opacity=".55"/>' +
    '<ellipse cx="66" cy="64" rx="12" ry="17" fill="#f0e0ff" opacity=".16"/>' +
    '<ellipse cx="31" cy="47" rx="6" ry="11" transform="rotate(28 31 47)" fill="#fff" opacity=".78"/><circle cx="26" cy="64" r="2.4" fill="#fff" opacity=".5"/>' +
    '</g>' + spark(30, 45, 9));

  const melon = sym('melon',
    shadow(50, 92, 36, 5.5) +
    '<g class="a-jump">' +
    // цілий кавун позаду
    '<g transform="translate(60 37) rotate(-18)">' +
    '<ellipse rx="34" ry="27" fill="url(#sg-rind)"/>' +
    '<g clip-path="url(#sg-clip-melon)" fill="none" stroke="#1c5a24" stroke-width="5.5" stroke-linecap="round" stroke-linejoin="round">' +
    '<path d="M-26 -30l4 8-4 8 4 8-4 8 4 8-4 8 4 8"/><path d="M-10 -30l4 8-4 8 4 8-4 8 4 8-4 8 4 8"/><path d="M6 -30l4 8-4 8 4 8-4 8 4 8-4 8 4 8"/><path d="M22 -30l4 8-4 8 4 8-4 8 4 8-4 8 4 8"/></g>' +
    '<ellipse rx="34" ry="27" fill="none" stroke="' + I + '" stroke-width="3"/>' +
    '<ellipse cx="-14" cy="-13" rx="9" ry="4.5" transform="rotate(-20 -14 -13)" fill="#fff" opacity=".55"/></g>' +
    // скибка спереду
    '<g transform="rotate(14 40 60)">' +
    '<path d="M4 54A37 37 0 0 0 78 54Z" fill="#2f8a2f" stroke="' + I + '" stroke-width="3" stroke-linejoin="round"/>' +
    '<path d="M8.5 54A32.5 32.5 0 0 0 73.5 54Z" fill="#e9f8c4"/>' +
    '<path d="M12 54A29 29 0 0 0 70 54Z" fill="url(#sg-flesh)"/>' +
    '<path d="M4 54H78" stroke="' + I + '" stroke-width="3" stroke-linecap="round"/>' +
    '<path d="M14 57H68" stroke="#ffc8c0" stroke-width="2.4" stroke-linecap="round" opacity=".75"/>' +
    '<g fill="' + I + '">' + [[26, 63], [41, 67], [56, 63], [33, 74], [49, 75], [41, 81]].map((p) =>
      '<path transform="translate(' + p[0] + ' ' + p[1] + ')" d="M0 -3.6C2.6 -.4 2.3 3.4 0 3.4C-2.3 3.4 -2.6 -.4 0 -3.6Z"/>').join('') + '</g>' +
    '<path d="M10 66A34 34 0 0 0 26 84" fill="none" stroke="#c8f5a0" stroke-width="2.2" stroke-linecap="round" opacity=".8"/>' +
    '</g></g>' + spark(46, 22, 8));

  const bell = sym('bell',
    shadow(50, 94, 32, 5) +
    '<g class="a-ring">' +
    '<circle cx="50" cy="11" r="6" fill="none" stroke="' + I + '" stroke-width="6.4"/><circle cx="50" cy="11" r="6" fill="none" stroke="url(#sg-gold-v)" stroke-width="3"/>' +
    '<g class="a-clap"><circle cx="50" cy="85" r="8" fill="url(#sg-gold-v)" stroke="' + I + '" stroke-width="3"/><circle cx="47" cy="82" r="2.2" fill="#fff" opacity=".7"/></g>' +
    '<path d="M50 16C32 16 27 32 27 48C27 62 22 70 13 76C11 78 12 82 16 82H84C88 82 89 78 87 76C78 70 73 62 73 48C73 32 68 16 50 16Z" fill="url(#sg-gold)" stroke="' + I + '" stroke-width="3" stroke-linejoin="round"/>' +
    '<path d="M28 47C40 44 60 44 72 47L73 58C60 55 40 55 27 58Z" fill="#d81b2c" stroke="' + I + '" stroke-width="2.2" stroke-linejoin="round"/>' +
    stitch(31.5, 48.2, 3.4, ['.x...x...x..', 'xox.xox.xox.', '.x...x...x..'].map((r) => r), { x: '#fff2c8', o: I }) +
    '<path d="M12 77Q50 68 88 77Q90 83 84 85Q50 78 16 85Q10 83 12 77Z" fill="url(#sg-gold-v)" stroke="' + I + '" stroke-width="2.6" stroke-linejoin="round"/>' +
    '<path d="M37 24C32 32 31 44 31 52" fill="none" stroke="#fff" stroke-width="4.2" stroke-linecap="round" opacity=".75"/>' +
    '<path d="M31 62C30 66 28 70 25 73" fill="none" stroke="#fff" stroke-width="3" stroke-linecap="round" opacity=".5"/>' +
    '<path d="M21 79Q30 76 40 75.5" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".6"/>' +
    '</g>' + spark(37, 27, 9));

  const shoeD = 'M24 24V52A26 26 0 0 0 76 52V24';
  const horseshoe = sym('horseshoe',
    shadow(50, 93, 30, 5) +
    '<g class="a-spin">' +
    '<path d="' + shoeD + '" fill="none" stroke="' + I + '" stroke-width="24"/>' +
    '<path d="' + shoeD + '" fill="none" stroke="url(#sg-steel)" stroke-width="17.5"/>' +
    '<path d="M18 30V52A32 32 0 0 0 34 80" fill="none" stroke="#fff" stroke-width="2.6" stroke-linecap="round" opacity=".8"/>' +
    '<path d="M31 30V52A19 19 0 0 0 40 68" fill="none" stroke="#3c4660" stroke-width="2" stroke-linecap="round" opacity=".45"/>' +
    '<g fill="url(#sg-gold-v)" stroke="' + I + '" stroke-width="1.3">' + [[24, 33], [76, 33], [25.6, 59], [74.4, 59], [37, 72.5], [63, 72.5]].map((p) =>
      '<circle cx="' + p[0] + '" cy="' + p[1] + '" r="2.9"/>').join('') + '</g>' +
    stitch(21, 41.5, 2.4, ['xox', '.x.'], { x: '#e3122a', o: I }) + stitch(73, 41.5, 2.4, ['xox', '.x.'], { x: '#e3122a', o: I }) +
    stitch(43.75, 74.6, 2.5, ['.x.x.', 'xoxox', '.x.x.'], { x: '#e3122a', o: I }) +
    '<rect x="12" y="13" width="24" height="12" rx="3.5" fill="url(#sg-steel)" stroke="' + I + '" stroke-width="3"/>' +
    '<rect x="64" y="13" width="24" height="12" rx="3.5" fill="url(#sg-steel)" stroke="' + I + '" stroke-width="3"/>' +
    '<path d="M16 16.5H24" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".85"/><path d="M68 16.5H76" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".85"/>' +
    '</g>' + spark(19, 40, 9));

  const sevenD = 'M16 12H86V28C70 44 58 64 54 90H30C34 66 46 46 62 30H32V38H16Z';
  const seven = sym('seven',
    shadow(48, 93, 30, 5) +
    '<g class="a-pulse">' +
    '<path d="' + sevenD + '" transform="translate(3.5 4)" fill="#6e0614" stroke="' + I + '" stroke-width="7" stroke-linejoin="round"/>' +
    '<path d="' + sevenD + '" fill="none" stroke="' + I + '" stroke-width="10" stroke-linejoin="round"/>' +
    '<path d="' + sevenD + '" fill="none" stroke="#ffd23a" stroke-width="5" stroke-linejoin="round"/>' +
    '<path d="' + sevenD + '" fill="url(#sg-seven)"/>' +
    stitch(22.5, 17.2, 5.4, ['x.x.x.x.x.x'], { x: '#ffe27a' }) +
    '<path d="M21 15.2H81" stroke="#ffd4cc" stroke-width="1.6" stroke-linecap="round" opacity=".8"/>' +
    '<path d="M59 34C48 47 40 64 37 84" fill="none" stroke="#ffc0b6" stroke-width="2.8" stroke-linecap="round" opacity=".65"/>' +
    '<path class="a-flash" d="' + sevenD + '" fill="#fff" opacity="0"/>' +
    '</g>' + spark(22, 16, 9));

  /* Дядько Глек у координатах 128×128 (як static/glek.svg, але соковитіше) */
  function glekBody(o) {
    o = o || {};
    let h = '';
    if (o.handle !== false) {
      h += '<path d="M92 44C118 44 120 88 94 96" fill="none" stroke="' + I + '" stroke-width="15" stroke-linecap="round"/>' +
        '<path d="M92 44C118 44 120 88 94 96" fill="none" stroke="url(#sg-clay-l)" stroke-width="8.5" stroke-linecap="round"/>' +
        '<path d="M100 51C110 56 111 72 106 82" fill="none" stroke="#ffd8b4" stroke-width="2.4" stroke-linecap="round" opacity=".7"/>';
    }
    h += '<path d="M46 22h36v10c14 6 24 22 24 42c0 22-14 40-42 40c-28 0-42-18-42-40c0-20 10-36 24-42z" fill="url(#sg-clay)" stroke="' + I + '" stroke-width="4.5" stroke-linejoin="round"/>' +
      '<path d="M96 56c6 10 6 26 0 36c-5 9-14 16-26 18c16-8 28-26 26-54z" fill="#6e2c10" opacity=".28"/>' +
      '<path d="M34.7 40H93.3Q96.5 45 98.6 50.5H29.4Q31.5 45 34.7 40Z" fill="#f6ead0" stroke="' + I + '" stroke-width="2.2" stroke-linejoin="round"/>' +
      stitch(35, 41, 3, ['.x...x...x...x...x..', 'xox.xox.xox.xox.xox.', '.x...x...x...x...x..'], { x: '#e3122a', o: I }) +
      '<rect x="41" y="14" width="46" height="14" rx="6" fill="url(#sg-clay-l)" stroke="' + I + '" stroke-width="4.5"/>' +
      '<ellipse cx="64" cy="19.5" rx="16" ry="3.4" fill="#4a1e0c" opacity=".75"/>' +
      '<path d="M60 16c-4-11-19-14-28-5c9-4 17-2 21 7z" fill="#2b1a0e" stroke="' + I + '" stroke-width="2" stroke-linejoin="round"/>' +
      '<path d="M37 10c6-3 12-2 16 2" fill="none" stroke="#7a5a4a" stroke-width="1.6" stroke-linecap="round"/>' +
      '<path d="M33 64c0-12 6-22 14-27" fill="none" stroke="#ffe0c0" stroke-width="4.5" stroke-linecap="round" opacity=".65"/>' +
      '<ellipse cx="40" cy="96" rx="3" ry="5" transform="rotate(30 40 96)" fill="#ffe0c0" opacity=".35"/>';
    if (o.phones !== false) {
      h += '<path d="M25 73C21 37 107 37 103 73" fill="none" stroke="' + I + '" stroke-width="9" stroke-linecap="round"/>' +
        '<path d="M25 73C21 37 107 37 103 73" fill="none" stroke="#33463e" stroke-width="5" stroke-linecap="round"/>' +
        '<rect x="13" y="64" width="18" height="29" rx="7" fill="#2a3a33" stroke="' + I + '" stroke-width="3"/><rect x="17" y="68" width="10" height="21" rx="4" fill="none" stroke="#f4c542" stroke-width="2"/>' +
        '<rect x="97" y="64" width="18" height="29" rx="7" fill="#2a3a33" stroke="' + I + '" stroke-width="3"/><rect x="101" y="68" width="10" height="21" rx="4" fill="none" stroke="#f4c542" stroke-width="2"/>';
    }
    h += '<g class="a-wink"><ellipse cx="52" cy="70" rx="8.6" ry="9.2" fill="#fffaf0" stroke="' + I + '" stroke-width="2.4"/><circle cx="53.4" cy="70" r="4.6" fill="#1a0e12"/><circle cx="55.2" cy="68" r="1.7" fill="#fff"/></g>' +
      '<g><ellipse cx="76" cy="70" rx="8.6" ry="9.2" fill="#fffaf0" stroke="' + I + '" stroke-width="2.4"/><circle cx="74.6" cy="70" r="4.6" fill="#1a0e12"/><circle cx="76.4" cy="68" r="1.7" fill="#fff"/></g>' +
      '<g class="a-brow"><path d="M42 59Q49 52 59 56" fill="none" stroke="#2b1a0e" stroke-width="4.6" stroke-linecap="round"/><path d="M86 59Q79 52 69 56" fill="none" stroke="#2b1a0e" stroke-width="4.6" stroke-linecap="round"/></g>' +
      '<circle cx="41" cy="85" r="5.5" fill="#ff7a5a" opacity=".55"/><circle cx="87" cy="85" r="5.5" fill="#ff7a5a" opacity=".55"/>' +
      '<path d="M51 99Q64 116 77 99Q64 103 51 99Z" fill="#6a1a10" stroke="' + I + '" stroke-width="2.4" stroke-linejoin="round"/><path d="M58 106Q64 112 70 106Q64 104 58 106Z" fill="#ff6a5a"/>' +
      '<g class="a-stache"><path d="M64 88c-6-6-18-4-24 4c-4 6-8 10-12 8c4-2 6-8 10-12c8-8 22-8 26 0c4-8 18-8 26 0c4 4 6 10 10 12c-4 2-8-2-12-8c-6-8-18-10-24-4z" fill="#2b1a0e" stroke="' + I + '" stroke-width="1.6" stroke-linejoin="round"/>' +
      '<path d="M46 84c4-2 9-2 13 0" fill="none" stroke="#6a4a3a" stroke-width="1.4" stroke-linecap="round"/></g>' +
      '<ellipse cx="64" cy="82" rx="5.4" ry="4.2" fill="#a34e22" stroke="' + I + '" stroke-width="1.8"/><circle cx="62.4" cy="80.6" r="1.4" fill="#ffd0a8"/>';
    return h;
  }

  let rays = '';
  for (let i = 0; i < 12; i++) rays += '<path d="M50 48L' + r2(50 + 52 * Math.cos((i * 30 - 5) * Math.PI / 180)) + ' ' + r2(48 + 52 * Math.sin((i * 30 - 5) * Math.PI / 180)) + 'L' + r2(50 + 52 * Math.cos((i * 30 + 5) * Math.PI / 180)) + ' ' + r2(48 + 52 * Math.sin((i * 30 + 5) * Math.PI / 180)) + 'Z"/>';
  const glek = sym('glek',
    '<g class="a-aura"><circle cx="50" cy="48" r="49" fill="url(#sg-glow)"/></g>' +
    '<g class="a-rays" fill="#ffc93a" opacity=".6">' + rays + '</g>' +
    '<circle cx="50" cy="47" r="40" fill="url(#sg-medal)" stroke="' + I + '" stroke-width="7"/><circle cx="50" cy="47" r="40" fill="none" stroke="url(#sg-gold-v)" stroke-width="3.6"/>' +
    '<path d="M22 34A32 32 0 0 1 44 15.5" fill="none" stroke="#fff" stroke-width="3" stroke-linecap="round" opacity=".35"/>' +
    '<g class="a-jump"><g transform="translate(-0.5 -2) scale(.79)">' + glekBody() + '</g></g>' +
    '<path d="M5 81H20V95H5L9 88Z" fill="#8a0a1e" stroke="' + I + '" stroke-width="2.2" stroke-linejoin="round"/>' +
    '<path d="M95 81H80V95H95L91 88Z" fill="#8a0a1e" stroke="' + I + '" stroke-width="2.2" stroke-linejoin="round"/>' +
    '<path d="M14 79H86V93H14Z" fill="url(#sg-ribbon)" stroke="' + I + '" stroke-width="2.4" stroke-linejoin="round"/>' +
    '<text x="50" y="90.4" text-anchor="middle" font-family="Onest, system-ui, sans-serif" font-weight="900" font-size="10.5" letter-spacing=".6" fill="#ffe27a" stroke="' + I + '" stroke-width="2.2" paint-order="stroke" stroke-linejoin="round">ДИКИЙ</text>' +
    spark(30, 30, 10));

  /* ---------- extras ---------- */
  const handle = '<svg viewBox="0 0 60 200" class="sg sg-handle" aria-hidden="true">' +
    '<path d="' + rr(-6, 158, 34, 28, 7) + '" fill="url(#sg-brass)" stroke="' + I + '" stroke-width="3"/>' +
    '<circle cx="2" cy="172" r="2.6" fill="' + I + '"/>' +
    '<g class="arm">' +
    '<path d="M30 172V40" stroke="' + I + '" stroke-width="12" stroke-linecap="round"/>' +
    '<path d="M30 172V40" stroke="url(#sg-steel)" stroke-width="6.5" stroke-linecap="round"/>' +
    '<path d="' + rr(23, 140, 14, 12, 4) + '" fill="url(#sg-brass-h)" stroke="' + I + '" stroke-width="2.6"/>' +
    '<g class="knob"><circle cx="30" cy="27" r="19" fill="url(#sg-red)" stroke="' + I + '" stroke-width="3.2"/>' +
    '<ellipse cx="23" cy="19" rx="6.5" ry="4.2" transform="rotate(-35 23 19)" fill="#fff" opacity=".9"/><circle cx="19" cy="29" r="2" fill="#fff" opacity=".55"/></g>' +
    '</g>' +
    '<circle cx="30" cy="172" r="15" fill="url(#sg-brass)" stroke="' + I + '" stroke-width="3"/><circle cx="30" cy="172" r="9" fill="none" stroke="#8a5a12" stroke-width="1.6"/><circle cx="30" cy="172" r="3.6" fill="' + I + '"/><path d="M21 164Q26 159 33 159" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".7"/>' +
    '</svg>';

  const bulb = '<svg viewBox="0 0 20 20" class="sg sg-bulb" aria-hidden="true">' +
    '<circle cx="10" cy="10" r="8.6" fill="url(#sg-brass)" stroke="' + I + '" stroke-width="1.4"/>' +
    '<circle cx="10" cy="10" r="5.8" fill="url(#sg-glass-off)" stroke="' + I + '" stroke-width="1"/>' +
    '<g class="lit"><circle cx="10" cy="10" r="10" fill="url(#sg-halo)"/><circle cx="10" cy="10" r="5.8" fill="url(#sg-glass)"/><circle cx="8.2" cy="8" r="1.8" fill="#fff"/></g>' +
    '</svg>';

  /* корпус */
  const L = {
    w: 420, h: 640,
    window: { x: 56, y: 182, w: 308, h: 308, r: 10 },
    tablo: { x: 76, y: 522, w: 268, h: 42 },
    logo: { x: 44, y: 20, w: 332, h: 120 },
    handle: { x: 424, y: 340 }, // куди лягає точка кріплення ручки (30,172 у її viewBox)
  };
  /* гнізда по периметру за годинниковою: лівий бік знизу вгору → дуга корони → правий бік згори вниз */
  const bulbSpots = [];
  (function () {
    const q = (a, b, c, t) => (1 - t) * (1 - t) * a + 2 * (1 - t) * t * b + t * t * c;
    for (let i = 15; i >= 0; i--) bulbSpots.push([26, 198 + i * 25]);
    bulbSpots.push([27, 172], [27, 146], [27, 122]);
    for (let i = 0; i <= 13; i++) { const t = i / 14; bulbSpots.push([r2(q(27, 28, 210, t)), r2(q(104, 13, 11, t))]); }
    bulbSpots.push([210, 11]);
    for (let i = 13; i >= 0; i--) { const t = i / 14; bulbSpots.push([r2(q(393, 392, 210, t)), r2(q(104, 13, 11, t))]); }
    bulbSpots.push([393, 122], [393, 146], [393, 172]);
    for (let i = 0; i < 16; i++) bulbSpots.push([394, 198 + i * 25]);
  })();
  const W = L.window;
  let grain = '';
  [[18, 3], [24, -2], [33, 2.5], [386, -3], [395, 2], [402, -2.5]].forEach((g) => {
    grain += '<path d="M' + g[0] + ' 150C' + (g[0] + g[1]) + ' 260 ' + (g[0] - g[1]) + ' 380 ' + g[0] + ' 500S' + (g[0] + g[1]) + ' 600 ' + g[0] + ' 630"/>';
  });
  [530, 545, 560].forEach((y, i) => { grain += '<path d="M14 ' + y + 'C30 ' + (y - 4) + ' 46 ' + (y + 5) + ' 62 ' + (y + i) + '"/><path d="M358 ' + y + 'C374 ' + (y + 4) + ' 390 ' + (y - 5) + ' 406 ' + (y - i) + '"/>'; });
  const cabinetInner =
    '<ellipse cx="210" cy="634" rx="206" ry="9" fill="url(#sg-shadow)"/>' +
    // корпус з вікном (evenodd)
    '<path d="' + rr(10, 140, 400, 492, 26) + rr(W.x, W.y, W.w, W.h, W.r) + '" fill="url(#sg-wood)" fill-rule="evenodd" stroke="' + I + '" stroke-width="4"/>' +
    '<g fill="none" stroke="#2a1006" stroke-width="1.6" opacity=".35">' + grain + '</g>' +
    // бокові інкрустації під лампочки
    '<path d="' + rr(14, 184, 24, 404, 11) + '" fill="url(#sg-wood3)" stroke="' + I + '" stroke-width="2"/>' +
    '<path d="' + rr(382, 184, 24, 404, 11) + '" fill="url(#sg-wood3)" stroke="' + I + '" stroke-width="2"/>' +
    // корона
    '<path d="M10 176V96Q10 4 210 2Q410 4 410 96V176Z" fill="url(#sg-wood2)" stroke="' + I + '" stroke-width="4" stroke-linejoin="round"/>' +
    '<path d="M19 172V96Q20 13 210 11Q400 13 401 96V172" fill="none" stroke="url(#sg-brass)" stroke-width="3" opacity=".9"/>' +
    '<path d="M44 140V92Q46 24 210 20Q374 24 376 92V140Z" fill="url(#sg-velvet)" stroke="' + I + '" stroke-width="3.4" stroke-linejoin="round"/>' +
    '<path d="M50 136V93Q52 30 210 26Q368 30 370 93V136" fill="none" stroke="#f2cc62" stroke-width="1.6" opacity=".7"/>' +
    '<path d="M70 70Q110 40 210 36" fill="none" stroke="#fff" stroke-width="3" stroke-linecap="round" opacity=".12"/>' +
    // рамка вікна
    '<path d="' + rr(40, 166, 340, 340, 24) + rr(W.x, W.y, W.w, W.h, W.r) + '" fill="url(#sg-brass)" fill-rule="evenodd" stroke="' + I + '" stroke-width="3.4"/>' +
    '<path d="' + rr(46, 172, 328, 328, 19) + '" fill="none" stroke="#fff6c8" stroke-width="1.4" opacity=".6"/>' +
    [[48, 174], [372, 174], [48, 498], [372, 498]].map((p) => '<circle cx="' + p[0] + '" cy="' + p[1] + '" r="3.4" fill="url(#sg-brass-h)" stroke="' + I + '" stroke-width="1.6"/>').join('') +
    [233, 336, 439].map((y) => '<path d="M43 ' + y + 'l6 -6 6 6 -6 6Z" fill="#e3122a" stroke="' + I + '" stroke-width="1.6"/><path d="M365 ' + y + 'l6 -6 6 6 -6 6Z" fill="#e3122a" stroke="' + I + '" stroke-width="1.6"/>').join('') +
    // табло
    '<path d="' + rr(64, 512, 292, 62, 14) + '" fill="url(#sg-brass)" stroke="' + I + '" stroke-width="3.4"/>' +
    '<path d="' + rr(76, 522, 268, 42, 8) + '" fill="url(#sg-tablo)" stroke="' + I + '" stroke-width="2.4"/>' +
    '<path d="M86 528H334" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".1"/>' +
    // цоколь
    '<path d="' + rr(18, 584, 384, 48, 16) + '" fill="url(#sg-wood3)" stroke="' + I + '" stroke-width="3.4"/>' +
    '<path d="' + rr(160, 592, 100, 32, 10) + '" fill="url(#sg-brass)" stroke="' + I + '" stroke-width="3"/>' +
    '<path d="' + rr(170, 600, 80, 18, 6) + '" fill="#1a0a0c" stroke="' + I + '" stroke-width="2"/>' +
    '<path d="M196 609H224" stroke="#f2cc62" stroke-width="3" stroke-linecap="round"/>' +
    petryk(92, 610, .72, false) + petryk(328, 610, .72, true) +
    // накладка під ручку
    '<path d="' + rr(398, 312, 18, 56, 6) + '" fill="url(#sg-brass-h)" stroke="' + I + '" stroke-width="2.6"/>' +
    // гнізда лампочок
    '<g>' + bulbSpots.map((p) => '<circle cx="' + p[0] + '" cy="' + p[1] + '" r="7" fill="#24100c" stroke="#c99a3a" stroke-width="2"/><circle cx="' + p[0] + '" cy="' + p[1] + '" r="4" fill="url(#sg-glass-off)"/>').join('') + '</g>';
  const cabinet = '<svg viewBox="0 0 420 640" class="sg sg-cabinet" aria-hidden="true">' + cabinetInner + '</svg>';

  /* скло над вікном (необов'язкове, у координатах вікна 308×308) */
  const glass = '<svg viewBox="0 0 308 308" class="sg sg-glass" preserveAspectRatio="none" aria-hidden="true">' +
    '<defs><linearGradient id="sg-gl-sh" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#000" stop-opacity=".5"/><stop offset=".12" stop-color="#000" stop-opacity="0"/><stop offset=".88" stop-color="#000" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".5"/></linearGradient></defs>' +
    '<rect width="308" height="308" rx="10" fill="url(#sg-gl-sh)"/>' +
    '<path d="M0 120L120 0H170L0 170Z" fill="#fff" opacity=".07"/><path d="M0 200L200 0H218L0 218Z" fill="#fff" opacity=".05"/>' +
    '</svg>';

  /* карти Ворожки */
  const heartD = 'M35 74C19 62 10 52 10 41C10 32 17 25 25 25C30 25 33 28 35 31C37 28 40 25 45 25C53 25 60 32 60 41C60 52 51 62 35 74Z';
  const spadeD = 'M35 22C45 34 60 42 60 55C60 63 54 68 47 68C42 68 39 65 37 62C38 69 40 74 45 78H25C30 74 32 69 33 62C31 65 28 68 23 68C16 68 10 63 10 55C10 42 25 34 35 22Z';
  function card(suit) {
    const head = '<svg viewBox="0 0 70 100" class="sg sg-card sg-card-' + (suit || 'back') + '" aria-hidden="true">' +
      '<path d="' + rr(3.5, 5, 65, 94, 8) + '" fill="#000" opacity=".28"/>';
    if (suit !== 'r' && suit !== 'b') {
      return head + '<path d="' + rr(1.5, 1.5, 65, 95, 8) + '" fill="url(#sg-cardback)" stroke="' + I + '" stroke-width="3"/>' +
        '<path d="' + rr(7, 7, 54, 84, 5) + '" fill="url(#sg-cardpat)" stroke="#f5c33b" stroke-width="1.6"/>' +
        '<circle cx="34" cy="49" r="15" fill="#7a0a1e" stroke="#f5c33b" stroke-width="2"/>' +
        stitch(24.2, 39.2, 2.8, ROMB, { x: '#ffe27a', o: '#fff6e0' }) +
        '<path d="M8 12Q20 6 34 8" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".25"/></svg>';
    }
    const red = suit === 'r', col = red ? '#d81b2c' : '#2b1a2e';
    const mini = (x, y, s, rot) => '<path transform="translate(' + x + ' ' + y + ') rotate(' + rot + ') scale(' + s + ') translate(-35 -50)" d="' + (red ? heartD : spadeD) + '" fill="' + col + '"/>';
    return head + '<path d="' + rr(1.5, 1.5, 65, 95, 8) + '" fill="url(#sg-card)" stroke="' + I + '" stroke-width="3"/>' +
      stitch(9, 6.5, 2.6, ['x.x.x.x.x.x.x.x.x.x'], { x: col }) + stitch(9, 88.4, 2.6, ['x.x.x.x.x.x.x.x.x.x'], { x: col }) +
      mini(12, 17, .22, 0) + mini(56, 82, .22, 180) +
      '<path d="' + (red ? heartD : spadeD) + '" transform="translate(0 2)" fill="' + (red ? 'url(#sg-red)' : 'url(#sg-spade)') + '" stroke="' + I + '" stroke-width="2.6" stroke-linejoin="round"/>' +
      stitch(25.5, red ? 33.5 : 37.5, 2.7, ROMB, red ? { x: '#fff6e0', o: '#ffd23a' } : { x: '#e3122a', o: '#fff6e0' }) +
      '<ellipse cx="' + (red ? 20 : 19) + '" cy="' + (red ? 38 : 56) + '" rx="3.4" ry="5.5" transform="rotate(25 20 45)" fill="#fff" opacity=".55"/></svg>';
  }

  /* ворожка: тернова хустка (темне тло, троянди, торочки), вузол під підборіддям; біла вишита сорочка, керсетка, коралі з дукачем */
  const q2 = (a, b, c, t) => (1 - t) * (1 - t) * a + 2 * (1 - t) * t * b + t * t * c;
  function strand(x0, y0, cx, cy, x1, y1, n, r) {
    let h = '';
    for (let i = 0; i <= n; i++) {
      const t = i / n, x = r2(q2(x0, cx, x1, t)), y = r2(q2(y0, cy, y1, t));
      h += '<circle cx="' + x + '" cy="' + y + '" r="' + r + '" fill="#e3122a" stroke="' + I + '" stroke-width="1.1"/><circle cx="' + r2(x - r * .3) + '" cy="' + r2(y - r * .35) + '" r="' + r2(r * .32) + '" fill="#fff" opacity=".8"/>';
    }
    return h;
  }
  /* троянда з листям: червоно-рожеві пелюстки по колу, завиток усередині */
  function trRose(x, y, r, rot) {
    let h = '<g transform="translate(' + x + ' ' + y + ') rotate(' + (rot || 0) + ')">';
    h += '<path d="M' + r2(-r * .9) + ' ' + r2(r * .5) + 'C' + r2(-r * 1.9) + ' ' + r2(r * .6) + ' ' + r2(-r * 2.1) + ' ' + r2(r * 1.4) + ' ' + r2(-r * 2.2) + ' ' + r2(r * 1.6) + 'C' + r2(-r * 1.5) + ' ' + r2(r * 1.8) + ' ' + r2(-r * .9) + ' ' + r2(r * 1.2) + ' ' + r2(-r * .9) + ' ' + r2(r * .5) + 'Z" fill="#3f9a3a"/>';
    h += '<path d="M' + r2(r * .9) + ' ' + r2(-r * .4) + 'C' + r2(r * 1.8) + ' ' + r2(-r * .8) + ' ' + r2(r * 2.1) + ' ' + r2(-r * 1.5) + ' ' + r2(r * 2.2) + ' ' + r2(-r * 1.8) + 'C' + r2(r * 1.4) + ' ' + r2(-r * 1.8) + ' ' + r2(r * .8) + ' ' + r2(-r * 1.2) + ' ' + r2(r * .9) + ' ' + r2(-r * .4) + 'Z" fill="#2f7a2e"/>';
    for (let i = 0; i < 6; i++) { const a = i * 60 * Math.PI / 180; h += '<circle cx="' + r2(Math.cos(a) * r * .55) + '" cy="' + r2(Math.sin(a) * r * .55) + '" r="' + r2(r * .55) + '" fill="' + (i % 2 ? '#e2335a' : '#d01c40') + '"/>'; }
    h += '<circle r="' + r2(r * .62) + '" fill="#ff6a8e"/>';
    h += '<path d="M' + r2(-r * .45) + ' 0A' + r2(r * .45) + ' ' + r2(r * .45) + ' 0 1 1 ' + r2(r * .2) + ' ' + r2(r * .4) + 'M' + r2(-r * .15) + ' ' + r2(-r * .1) + 'A' + r2(r * .2) + ' ' + r2(r * .2) + ' 0 1 1 ' + r2(r * .1) + ' ' + r2(r * .2) + '" fill="none" stroke="#9a0a2c" stroke-width="' + r2(Math.max(.9, r * .14)) + '" stroke-linecap="round"/>';
    return h + '</g>';
  }
  /* торочки: короткі нитки донизу вздовж відрізка */
  function fringe(x0, y0, x1, y1, len, step) {
    const n = Math.max(1, Math.round(Math.hypot(x1 - x0, y1 - y0) / step));
    let d = '';
    for (let i = 0; i <= n; i++) { const x = x0 + (x1 - x0) * i / n, y = y0 + (y1 - y0) * i / n; d += 'M' + r2(x) + ' ' + r2(y) + 'l' + r2((i % 2 ? .6 : -.6)) + ' ' + len; }
    return '<path d="' + d + '" stroke="#5a1028" stroke-width="1.5" stroke-linecap="round" fill="none"/>';
  }
  const SCARF = 'url(#sg-ternova)';
  const diamondD = 'M35 20L60 50L35 80L10 50Z';
  const clubD = 'M35 18A13 13 0 0 1 46 39A13 13 0 1 1 40 61L44 80H26L30 61A13 13 0 1 1 24 39A13 13 0 0 1 35 18Z';
  /* карта лицем: біле поле, велика масть угорі (низ тримають руки), хрестик-вишивка в масті */
  const faceCard = (rot, suit) => {
    const red = suit === 'h' || suit === 'd', d = { h: heartD, d: diamondD, s: spadeD, c: clubD }[suit];
    // масть — у лівій верхній частині: її не закриває наступна карта віяла
    return '<g transform="rotate(' + rot + ' 80 156)"><path d="' + rr(67.5, 101, 28, 47, 3.6) + '" fill="' + I + '" opacity=".4"/><path d="' + rr(66, 99, 28, 47, 3.6) + '" fill="url(#sg-card)" stroke="' + I + '" stroke-width="2.4"/>' +
      '<path transform="translate(75 113) scale(.32) translate(-35 -50)" d="' + d + '" fill="' + (red ? 'url(#sg-red)' : 'url(#sg-spade)') + '" stroke="' + I + '" stroke-width="5" stroke-linejoin="round"/>' +
      stitch(72.3, 110.6, 1.8, ['.x.', 'x.x', '.x.'], { x: red ? '#fff6e0' : '#e3122a' }) + '</g>';
  };
  const fortune = '<svg viewBox="0 0 160 160" class="sg sg-fortune" aria-hidden="true">' +
    '<ellipse cx="80" cy="156" rx="64" ry="6" fill="url(#sg-shadow)"/>' +
    '<g class="a-sway">' +
    // хустка ззаду (спадає на потилицю, з торочками)
    '<path d="M80 18C53 18 39 42 41 68C42 84 43 96 48 106H112C117 96 118 84 119 68C121 42 107 18 80 18Z" fill="' + SCARF + '" stroke="' + I + '" stroke-width="2.6" stroke-linejoin="round"/>' +
    fringe(49, 106, 111, 106, 6, 3) +
    trRose(46, 94, 5, 20) + trRose(114, 92, 5, -30) +
    // сорочка
    '<path d="M20 160C22 128 46 112 80 112C114 112 138 128 140 160Z" fill="url(#sg-shirt)" stroke="' + I + '" stroke-width="2.6" stroke-linejoin="round"/>' +
    // вишиті рукави
    stitch(24, 136, 3, ['.x.x.', 'xoxox', '.x.x.', 'xoxox', '.x.x.'], { x: '#d81b2c', o: I }) +
    stitch(121, 136, 3, ['.x.x.', 'xoxox', '.x.x.', 'xoxox', '.x.x.'], { x: '#d81b2c', o: I }) +
    // керсетка
    '<path d="M34 160C35 138 46 122 64 115L74 160Z" fill="url(#sg-vest)" stroke="' + I + '" stroke-width="2.2" stroke-linejoin="round"/>' +
    '<path d="M126 160C125 138 114 122 96 115L86 160Z" fill="url(#sg-vest)" stroke="' + I + '" stroke-width="2.2" stroke-linejoin="round"/>' +
    '<path d="M39 158C40 140 49 128 64 120M121 158C120 140 111 128 96 120" fill="none" stroke="#f5c33b" stroke-width="1.4" stroke-dasharray="2.4 2" opacity=".85"/>' +
    // комір сорочки з вишивкою
    stitch(68.5, 112.5, 2.6, ['xoxoxoxox'], { x: '#d81b2c', o: I }) +
    // коралі, дукач
    strand(62, 116, 80, 132, 98, 116, 11, 2.4) +
    strand(60, 118, 80, 140, 100, 118, 13, 2.5) +
    '<path d="M80 129v4" stroke="#b07a1c" stroke-width="1.4"/><circle cx="80" cy="137" r="5.2" fill="url(#sg-gold-v)" stroke="' + I + '" stroke-width="1.4"/><circle cx="80" cy="137" r="2.6" fill="none" stroke="#b07a1c" stroke-width="1"/>' +
    '<g class="a-head">' +
    '<ellipse cx="80" cy="72" rx="26" ry="29" fill="url(#sg-skin)" stroke="' + I + '" stroke-width="2.6"/>' +
    // сиве волосся ледь з-під хустки
    '<path d="M60 58C66 51 73 50 80 50C87 50 94 51 100 58C94 55 87 54 80 54C73 54 66 55 60 58Z" fill="#e2dcea" stroke="' + I + '" stroke-width="1.2"/>' +
    // хустка спереду: м'яко облягає голову, край над чолом без обідка
    '<path d="M51 94C43 78 43 50 57 36C65 28 73 24 80 24C87 24 95 28 103 36C117 50 117 78 109 94C108 80 105 66 99 58C93 51 87 50 80 50C73 50 67 51 61 58C55 66 52 80 51 94Z" fill="' + SCARF + '" stroke="' + I + '" stroke-width="2.4" stroke-linejoin="round"/>' +
    '<path d="M52 92C52 80 55 66 61 58C67 51 73 50 80 50C87 50 93 51 99 58C105 66 108 80 108 92" fill="none" stroke="#d0305a" stroke-width="1.6" stroke-dasharray="3 1.6" stroke-linecap="round"/>' +
    '<path d="M60 34C68 28 74 26 80 26" fill="none" stroke="#8a3a60" stroke-width="2.4" stroke-linecap="round" opacity=".8"/>' +
    '<path d="M74 26C78 34 79 42 80 49" fill="none" stroke="#120408" stroke-width="1.6" stroke-linecap="round" opacity=".6"/>' +
    trRose(66, 38, 7, -15) + trRose(104, 52, 6, 40) + trRose(49, 70, 4.2, 70) + trRose(90, 31, 4.2, 10) +
    '<g fill="#e2335a" opacity=".9"><circle cx="58" cy="52" r="1.3"/><circle cx="112" cy="72" r="1.3"/><circle cx="96" cy="40" r="1.2"/><circle cx="47" cy="84" r="1.2"/></g>' +
    // очі хитрі
    '<path d="M61 71Q69 65 77 71Q69 75 61 71Z" fill="#fff" stroke="' + I + '" stroke-width="1.6"/>' +
    '<path d="M84 71Q92 65 100 71Q92 75 84 71Z" fill="#fff" stroke="' + I + '" stroke-width="1.6"/>' +
    '<g class="a-peek"><circle cx="71" cy="70.4" r="2.9" fill="' + I + '"/><circle cx="94" cy="70.4" r="2.9" fill="' + I + '"/><circle cx="72" cy="69.4" r="1" fill="#fff"/><circle cx="95" cy="69.4" r="1" fill="#fff"/></g>' +
    '<path d="M60 70Q69 62 78 70" fill="none" stroke="' + I + '" stroke-width="2.8" stroke-linecap="round"/>' +
    '<path d="M83 70Q92 63 101 70" fill="none" stroke="' + I + '" stroke-width="2.8" stroke-linecap="round"/>' +
    '<path d="M62 62Q68 57 76 60" fill="none" stroke="#8a7a90" stroke-width="3" stroke-linecap="round"/>' +
    '<path d="M85 63Q93 62 99 64" fill="none" stroke="#8a7a90" stroke-width="3" stroke-linecap="round"/>' +
    '<path d="M57 75q3 2 5 1M103 75q-3 2-5 1" fill="none" stroke="#b06a4a" stroke-width="1.2" stroke-linecap="round" opacity=".7"/>' +
    '<circle cx="64" cy="84" r="6" fill="#ff7a6a" opacity=".45"/><circle cx="97" cy="84" r="6" fill="#ff7a6a" opacity=".45"/>' +
    '<ellipse cx="81" cy="81" rx="5.2" ry="4.6" fill="#ec9c74" stroke="' + I + '" stroke-width="1.8"/><circle cx="79.4" cy="79.6" r="1.3" fill="#fff" opacity=".7"/>' +
    '<path d="M69 91Q80 98 92 89" fill="none" stroke="' + I + '" stroke-width="2.4" stroke-linecap="round"/>' +
    '<rect x="83" y="92" width="3.6" height="3.4" rx=".8" fill="#ffd23a" stroke="' + I + '" stroke-width=".9"/>' +
    // хустка під підборіддям, вузол і два кінці з торочками
    '<path d="M52 92C57 102 67 107 80 107C93 107 103 102 108 92C108 104 96 113 80 113C64 113 52 104 52 92Z" fill="' + SCARF + '" stroke="' + I + '" stroke-width="2.2" stroke-linejoin="round"/>' +
    '<path d="M77 111L60 128L68 132L80 114Z" fill="' + SCARF + '" stroke="' + I + '" stroke-width="2" stroke-linejoin="round"/>' + fringe(60, 128, 68, 132, 5, 2.2) +
    '<path d="M83 111L101 126L94 131L80 114Z" fill="' + SCARF + '" stroke="' + I + '" stroke-width="2" stroke-linejoin="round"/>' + fringe(94, 131, 101, 126, 5, 2.2) +
    trRose(67, 124, 3.2, 0) + trRose(94, 123, 3.2, 30) +
    '<ellipse cx="80" cy="111" rx="6.5" ry="5.2" fill="#6a2448" stroke="' + I + '" stroke-width="2"/><path d="M76 109q4-2 8 0" fill="none" stroke="#b0507a" stroke-width="1.4" stroke-linecap="round"/>' +
    '</g>' +
    faceCard(-40, 'h') + faceCard(-20, 's') + faceCard(0, 'd') + faceCard(20, 'c') + faceCard(40, 'h') +
    '<ellipse cx="70" cy="148" rx="9.5" ry="7.6" fill="url(#sg-skin)" stroke="' + I + '" stroke-width="2.2"/>' +
    '<ellipse cx="90" cy="148" rx="9.5" ry="7.6" fill="url(#sg-skin)" stroke="' + I + '" stroke-width="2.2"/>' +
    '<path d="M65 144q4-2 8 0M87 144q4-2 8 0" fill="none" stroke="#b06a4a" stroke-width="1.2" stroke-linecap="round"/>' +
    '<ellipse cx="76" cy="141" rx="3.2" ry="4.4" transform="rotate(-20 76 141)" fill="url(#sg-skin)" stroke="' + I + '" stroke-width="1.6"/>' +
    '<ellipse cx="84" cy="141" rx="3.2" ry="4.4" transform="rotate(20 84 141)" fill="url(#sg-skin)" stroke="' + I + '" stroke-width="1.6"/>' +
    '</g></svg>';

  /* ---------- сцена: ярмарок увечері (1600×900) ---------- */
  function garland(x0, y0, x1, y1, sag, n, cls, cols, rad) {
    const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2 + sag * 2;
    const q = (a, b, c, t) => (1 - t) * (1 - t) * a + 2 * (1 - t) * t * b + t * t * c;
    let wire = '<path d="M' + x0 + ' ' + y0 + 'Q' + cx + ' ' + cy + ' ' + x1 + ' ' + y1 + '" fill="none" stroke="#1a0c1c" stroke-width="2.4"/>';
    let ga = '', gb = '', cores = '';
    for (let i = 1; i < n; i++) {
      const t = i / n, x = r2(q(x0, cx, x1, t)), y = r2(q(y0, cy, y1, t)) + 6, c = cols[i % cols.length];
      const glow = '<circle cx="' + x + '" cy="' + y + '" r="' + rad * 2.6 + '" fill="' + c + '" opacity=".28"/>';
      if (i % 2) ga += glow; else gb += glow;
      cores += '<circle cx="' + x + '" cy="' + y + '" r="' + rad + '" fill="' + c + '" stroke="#1a0c1c" stroke-width="1.2"/><circle cx="' + r2(x - rad * .3) + '" cy="' + r2(y - rad * .3) + '" r="' + r2(rad * .35) + '" fill="#fff" opacity=".85"/>';
    }
    return wire + '<g class="' + cls + 'a">' + ga + '</g><g class="' + cls + 'b">' + gb + '</g>' + cores;
  }
  function bunting(x0, y0, x1, y1, sag, n, cls) {
    const cx = (x0 + x1) / 2, cy = (y0 + y1) / 2 + sag * 2;
    const q = (a, b, c, t) => (1 - t) * (1 - t) * a + 2 * (1 - t) * t * b + t * t * c;
    const cols = ['#e3122a', '#f5c33b', '#2f7fd0', '#3f9a3a', '#fff2d8'];
    let h = '<g class="' + cls + '"><path d="M' + x0 + ' ' + y0 + 'Q' + cx + ' ' + cy + ' ' + x1 + ' ' + y1 + '" fill="none" stroke="#1a0c1c" stroke-width="2"/>';
    for (let i = 0; i < n; i++) {
      const ta = (i + .15) / n, tb = (i + .85) / n;
      const ax = q(x0, cx, x1, ta), ay = q(y0, cy, y1, ta), bx = q(x0, cx, x1, tb), by = q(y0, cy, y1, tb);
      h += '<path d="M' + r2(ax) + ' ' + r2(ay) + 'L' + r2(bx) + ' ' + r2(by) + 'L' + r2((ax + bx) / 2) + ' ' + r2((ay + by) / 2 + 34) + 'Z" fill="' + cols[i % cols.length] + '" stroke="#1a0c1c" stroke-width="1.6" stroke-linejoin="round" opacity=".92"/>';
    }
    return h + '</g>';
  }
  function awning(x, y, w, n, c1, c2) {
    const sw2 = w / n; let h = '';
    for (let i = 0; i < n; i++) {
      const xx = x + i * sw2;
      h += '<path d="M' + r2(xx) + ' ' + y + 'H' + r2(xx + sw2) + 'V' + (y + 44) + 'A' + r2(sw2 / 2) + ' ' + r2(sw2 / 2.4) + ' 0 0 1 ' + r2(xx) + ' ' + (y + 44) + 'Z" fill="' + (i % 2 ? c2 : c1) + '"/>';
    }
    return h + '<path d="M' + x + ' ' + y + 'H' + (x + w) + '" stroke="#1a0c1c" stroke-width="4"/><path d="M' + (x - 10) + ' ' + (y - 18) + 'L' + x + ' ' + y + 'H' + (x + w) + 'L' + (x + w + 10) + ' ' + (y - 18) + 'Z" fill="' + c1 + '" stroke="#1a0c1c" stroke-width="3" stroke-linejoin="round"/>';
  }
  const miniJug = (x, y, s, c) => '<g transform="translate(' + x + ' ' + y + ') scale(' + s + ')"><path d="M-8 -30h16v6c10 4 16 14 16 26c0 14-10 22-24 22s-24-8-24-22c0-12 6-22 16-26z" fill="' + c + '" stroke="#1a0c1c" stroke-width="3"/><path d="M-14 -4h28" stroke="#f5c33b" stroke-width="3"/><path d="M-14 2h28" stroke="#e3122a" stroke-width="2.4"/></g>';
  function stall(x, flip, c1, c2) {
    // ятка шириною 320, від x
    let h = '<g>';
    h += '<rect x="' + (x + 10) + '" y="520" width="300" height="200" fill="#1e0f1c"/>';
    h += '<rect x="' + (x + 10) + '" y="520" width="300" height="200" fill="url(#sgs-stall)" opacity=".9"/>';
    // полиця з глеками
    h += '<rect x="' + (x + 24) + '" y="610" width="272" height="8" fill="#5a2c14" stroke="#1a0c1c" stroke-width="2"/>';
    for (let i = 0; i < 6; i++) h += miniJug(x + 46 + i * 46, 586, .78, ['#c96a32', '#e08a48', '#a8522a'][i % 3]);
    // рушник
    h += '<rect x="' + (x + (flip ? 230 : 40)) + '" y="528" width="44" height="78" fill="#f6ead0" stroke="#1a0c1c" stroke-width="2"/>' + stitch(x + (flip ? 233 : 43), 532, 5.4, ['.x.x.x.', 'xox.xox', '.x.x.x.', '.......', '..xox..', '.xo.ox.', '..xox..', '.......', '.x.x.x.', 'xox.xox', '.x.x.x.', '.......', '...x...'], { x: '#e3122a', o: '#1a0c1c' });
    // бублики на шворці
    h += '<path d="M' + (x + (flip ? 110 : 150)) + ' 528V600" stroke="#c99a5a" stroke-width="2"/>';
    for (let i = 0; i < 4; i++) h += '<circle cx="' + (x + (flip ? 110 : 150)) + '" cy="' + (546 + i * 16) + '" r="9" fill="none" stroke="#c98a3a" stroke-width="6"/><circle cx="' + (x + (flip ? 110 : 150)) + '" cy="' + (546 + i * 16) + '" r="9" fill="none" stroke="#1a0c1c" stroke-width="1.2" opacity=".6"/>';
    // прилавок
    h += '<rect x="' + x + '" y="650" width="320" height="130" rx="6" fill="#7a4020" stroke="#1a0c1c" stroke-width="4"/>';
    h += '<rect x="' + x + '" y="650" width="320" height="16" fill="#a8622e" stroke="#1a0c1c" stroke-width="3"/>';
    for (let i = 0; i < 4; i++) h += '<path d="M' + (x + 8) + ' ' + (690 + i * 22) + 'H' + (x + 312) + '" stroke="#4e2612" stroke-width="2" opacity=".6"/>';
    h += petryk(x + 160, 724, 1.15, flip);
    // гарбузи, кошик яблук
    h += '<ellipse cx="' + (x + (flip ? 60 : 250)) + '" cy="636" rx="26" ry="18" fill="#f08a1a" stroke="#1a0c1c" stroke-width="3"/><path d="M' + (x + (flip ? 60 : 250)) + ' 620v32M' + (x + (flip ? 48 : 238)) + ' 621q-6 15 0 30M' + (x + (flip ? 72 : 262)) + ' 621q6 15 0 30" stroke="#b85a0a" stroke-width="2.4" fill="none"/><path d="M' + (x + (flip ? 60 : 250)) + ' 620l4-8" stroke="#3f6a1a" stroke-width="4" stroke-linecap="round"/>';
    h += '<path d="M' + (x + (flip ? 140 : 160)) + ' 650a30 14 0 0 1 60 0Z" fill="#8a5a2a" stroke="#1a0c1c" stroke-width="3"/>';
    for (let i = 0; i < 5; i++) h += '<circle cx="' + (x + (flip ? 150 : 170) + i * 10) + '" cy="' + (642 - (i % 2) * 5) + '" r="6" fill="' + (i % 2 ? '#e3122a' : '#9ad13a') + '" stroke="#1a0c1c" stroke-width="1.6"/>';
    // стовпи
    h += '<rect x="' + (x - 4) + '" y="480" width="14" height="300" fill="#5a2c14" stroke="#1a0c1c" stroke-width="3"/><rect x="' + (x + 310) + '" y="480" width="14" height="300" fill="#5a2c14" stroke="#1a0c1c" stroke-width="3"/>';
    h += awning(x - 6, 494, 332, 8, c1, c2);
    // ліхтар
    const lx = x + (flip ? 40 : 280);
    h += '<path d="M' + lx + ' 540v14" stroke="#1a0c1c" stroke-width="2"/><g class="sg-flick"><circle cx="' + lx + '" cy="566" r="46" fill="url(#sgs-lamp)"/></g><rect x="' + (lx - 7) + '" y="554" width="14" height="20" rx="4" fill="#ffd36a" stroke="#1a0c1c" stroke-width="2.4"/>';
    return h + '</g>';
  }
  let stars = '', twinkle = '';
  [[60, 40], [180, 90], [260, 30], [380, 70], [520, 40], [610, 110], [700, 30], [820, 70], [1060, 40], [1150, 100], [1240, 30], [1380, 60], [1470, 110], [1550, 40], [140, 160], [460, 150], [1300, 170], [760, 150], [980, 170], [350, 200]].forEach((s, i) => {
    const c = '<circle cx="' + s[0] + '" cy="' + s[1] + '" r="' + (i % 3 ? 1.6 : 2.4) + '" fill="#fff"/>';
    if (i % 4 === 0) twinkle += c; else stars += c;
  });
  let wheel = '<g class="sg-wheel">';
  for (let i = 0; i < 16; i++) { const a = i * Math.PI / 8; wheel += '<path d="M0 0L' + r2(110 * Math.cos(a)) + ' ' + r2(110 * Math.sin(a)) + '" stroke="#ffd97a" stroke-width="1.6" opacity=".55"/><circle cx="' + r2(110 * Math.cos(a)) + '" cy="' + r2(110 * Math.sin(a)) + '" r="' + (i % 2 ? 4 : 6) + '" fill="' + (i % 2 ? '#ff8a6a' : '#ffe27a') + '"/>'; }
  wheel += '<circle r="110" fill="none" stroke="#ffd97a" stroke-width="3" opacity=".7"/><circle r="94" fill="none" stroke="#ffd97a" stroke-width="1.4" opacity=".45"/><circle r="10" fill="#ffd97a"/></g>';
  let canopy = '';
  for (let i = 0; i < 8; i++) canopy += '<path d="M400 480L' + (300 + i * 25) + ' 540H' + (325 + i * 25) + 'Z" fill="' + (i % 2 ? '#fff2d8' : '#e3122a') + '"/>';
  let horses = '';
  for (let i = 0; i < 4; i++) horses += '<g transform="translate(' + (322 + i * 52) + ' 580)"><path d="M-14 4C-12 -6 6 -8 10 -4L16 -14L20 -10L16 2L12 14H8L6 6H-6L-8 14H-12Z" fill="#fff2d8" stroke="#1a0c1c" stroke-width="2"/><path d="M0 -40V20" stroke="#f5c33b" stroke-width="2.4"/></g>';
  let tent = '<rect x="-130" y="600" width="260" height="110" fill="#2a1226" stroke="#1a0c1c" stroke-width="3"/>' +
    '<path d="M-44 710V650Q0 612 44 650V710Z" fill="#ffb04a" opacity=".85"/><path d="M-30 710V660Q0 636 30 660V710Z" fill="#ffe08a" opacity=".7"/>' +
    '<g class="sg-flick"><circle cx="0" cy="670" r="110" fill="url(#sgs-lamp)"/></g>';
  for (let i = 0; i < 8; i++) tent += '<path d="M0 500L' + (-150 + i * 37.5) + ' 600H' + (-150 + (i + 1) * 37.5) + 'Z" fill="' + (i % 2 ? '#fff2d8' : '#c8102e') + '"/>';
  tent += '<path d="M-150 600H150" stroke="#1a0c1c" stroke-width="4"/><path d="M0 500L-150 600M0 500L150 600" stroke="#1a0c1c" stroke-width="3" fill="none"/><path d="M0 500V470" stroke="#1a0c1c" stroke-width="3"/><path d="M0 470L22 477L0 484Z" fill="#3f9a3a" stroke="#1a0c1c" stroke-width="2"/>';
  for (let i = 0; i <= 8; i++) tent += '<circle cx="' + (-150 + i * 37.5) + '" cy="604" r="4" fill="' + (i % 2 ? '#ffe27a' : '#ff8a6a') + '"/>';
  let crowd = '';
  [[520, 1, 0], [575, .9, 1], [640, 1.08, 2], [700, .82, 0], [760, 1, 1], [930, .95, 2], [990, 1.1, 0], [1050, .86, 1], [1110, 1, 2], [370, .9, 1], [1220, .95, 0]].forEach((c) => {
    const x = c[0], k = c[1], y = 800;
    crowd += '<path d="M' + (x - 22 * k) + ' ' + y + 'C' + (x - 22 * k) + ' ' + (y - 50 * k) + ' ' + (x + 22 * k) + ' ' + (y - 50 * k) + ' ' + (x + 22 * k) + ' ' + y + 'Z"/>';
    crowd += '<circle cx="' + x + '" cy="' + r2(y - 58 * k) + '" r="' + r2(12 * k) + '"/>';
    if (c[2] === 1) crowd += '<ellipse cx="' + x + '" cy="' + r2(y - 66 * k) + '" rx="' + r2(22 * k) + '" ry="' + r2(4.5 * k) + '"/><rect x="' + r2(x - 10 * k) + '" y="' + r2(y - 80 * k) + '" width="' + r2(20 * k) + '" height="' + r2(14 * k) + '" rx="4"/>';
    if (c[2] === 2) crowd += '<path d="M' + r2(x - 16 * k) + ' ' + r2(y - 50 * k) + 'Q' + x + ' ' + r2(y - 86 * k) + ' ' + r2(x + 16 * k) + ' ' + r2(y - 50 * k) + 'L' + x + ' ' + r2(y - 40 * k) + 'Z"/>';
  });
  const sceneDefs = '<defs>' +
    lg('sgs-sky', 0, 0, 0, 1, [[0, '#110a30'], [.28, '#2c1754'], [.46, '#5e2a66'], [.56, '#b04a5e'], [.63, '#ec8250'], [.7, '#f6b060']]) +
    lg('sgs-ground', 0, 0, 0, 1, [[0, '#3c1e2c'], [1, '#160a12']]) +
    lg('sgs-hill', 0, 0, 0, 1, [[0, '#3a1a4a'], [1, '#1e0e28']]) +
    lg('sgs-stall', 0, 0, 0, 1, [[0, '#4a2420'], [1, '#1e0f1c']]) +
    rg('sgs-lamp', .5, .5, .5, [[0, '#ffe08a', .75], [.4, '#ffb03a', .3], [1, '#ff8a00', 0]]) +
    rg('sgs-pool', .5, .5, .5, [[0, '#ffb04a', .45], [1, '#ff8a00', 0]]) +
    rg('sgs-moon', .5, .5, .5, [[0, '#fff2c0', .5], [1, '#fff2c0', 0]]) +
    '</defs>';
  const scene = '<svg viewBox="0 0 1600 900" preserveAspectRatio="xMidYMid slice" class="sg sg-scene" aria-hidden="true">' + sceneDefs +
    '<rect width="1600" height="900" fill="url(#sgs-sky)"/>' +
    '<g opacity=".8">' + stars + '</g><g class="sg-twinkle">' + twinkle + '</g>' +
    '<circle cx="880" cy="150" r="120" fill="url(#sgs-moon)"/>' +
    '<path d="M868 98A54 54 0 1 0 932 172A46 46 0 1 1 868 98Z" fill="#fff0c0"/>' +
    // далеко: пагорби, церква, колесо огляду
    '<path d="M0 600C160 540 320 560 470 520C600 488 720 540 860 530C1000 520 1120 470 1300 500C1430 520 1520 500 1600 520V900H0Z" fill="url(#sgs-hill)"/>' +
    '<g fill="#1e0e28"><rect x="700" y="452" width="70" height="80"/><rect x="722" y="420" width="26" height="40"/><path d="M735 380C720 400 718 414 722 420H748C752 414 750 400 735 380Z"/><path d="M735 366v16M729 372h12" stroke="#1e0e28" stroke-width="3"/><path d="M688 470C680 480 680 488 684 492H710C714 488 714 480 706 470C702 462 694 462 688 470Z"/><rect x="686" y="490" width="22" height="42"/><path d="M766 470C758 480 758 488 762 492H788C792 488 792 480 784 470C780 462 772 462 766 470Z"/><rect x="764" y="490" width="22" height="42"/></g>' +
    '<g transform="translate(1260 400)"><path d="M-60 140L0 0L60 140" fill="none" stroke="#24123a" stroke-width="10"/>' + wheel + '</g>' +
    // каруселька
    '<g opacity=".95"><path d="M400 452V480" stroke="#1a0c1c" stroke-width="3"/><g class="sg-cflag"><path d="M400 452L424 460L400 468Z" fill="#f5c33b" stroke="#1a0c1c" stroke-width="2"/></g>' +
    '<rect x="290" y="560" width="220" height="40" fill="#2a1430"/>' + '<g class="sg-horses">' + horses + '</g>' +
    canopy + '<path d="M300 540H500" stroke="#1a0c1c" stroke-width="4"/><path d="M296 540H504V552H296Z" fill="#f5c33b" stroke="#1a0c1c" stroke-width="2.4"/>' +
    [310, 340, 370, 400, 430, 460, 490].map((x) => '<circle cx="' + x + '" cy="546" r="3.4" fill="#fff6c0"/>').join('') +
    '<path d="M280 600H520L510 616H290Z" fill="#5a2c14" stroke="#1a0c1c" stroke-width="3"/></g>' +
    // тополі
    '<g fill="#160a20"><path d="M140 600C120 520 128 420 150 360C172 420 180 520 160 600Z"/><path d="M200 610C186 540 190 460 206 410C222 460 226 540 214 610Z"/><path d="M1420 600C1400 520 1408 430 1430 370C1452 430 1460 520 1440 600Z"/><path d="M560 560C548 510 552 450 564 410C576 450 580 510 570 560Z"/></g>' +
    '<path d="M0 700C200 664 400 690 600 672C800 654 1000 690 1200 662C1400 640 1500 668 1600 660V900H0Z" fill="#26112e"/>' +
    // шатро в центрі
    '<g transform="translate(860 0)">' + tent + '</g>' +
    // земля
    '<rect y="760" width="1600" height="140" fill="url(#sgs-ground)"/>' +
    '<g fill="#fff" opacity=".05">' + [[120, 820], [300, 860], [520, 800], [700, 870], [900, 820], [1100, 860], [1300, 810], [1480, 860], [420, 880], [1000, 885]].map((p) => '<ellipse cx="' + p[0] + '" cy="' + p[1] + '" rx="40" ry="8"/>').join('') + '</g>' +
    '<ellipse cx="190" cy="800" rx="260" ry="50" fill="url(#sgs-pool)"/><ellipse cx="1410" cy="800" rx="260" ry="50" fill="url(#sgs-pool)"/><ellipse cx="800" cy="790" rx="380" ry="60" fill="url(#sgs-pool)" opacity=".7"/>' +
    '<g fill="#140a14">' + crowd + '</g>' +
    // ятки ближче
    stall(30, false, '#e3122a', '#fff2d8') + stall(1250, true, '#f5c33b', '#3f9a3a') +
    // прапорці й гірлянди
    bunting(-20, 330, 1620, 350, 70, 26, 'sg-flagsA') +
    bunting(-20, 420, 700, 460, 40, 11, 'sg-flagsB') +
    garland(-20, 60, 1620, 80, 90, 34, 'sg-ga', ['#ffd23a', '#ff5a4a', '#7ad0ff', '#9aea6a', '#ffb0e0'], 7) +
    garland(-20, 200, 820, 230, 60, 16, 'sg-ga', ['#ff5a4a', '#ffd23a', '#9aea6a'], 6.5) +
    garland(780, 230, 1620, 190, 60, 16, 'sg-ga', ['#7ad0ff', '#ffd23a', '#ff5a4a'], 6.5) +
    '</svg>';

  /* ---------- логотип (560×200) ---------- */
  let lbA = '', lbB = '', lbCore = '';
  const lbulb = (x, y, i) => { const g = '<circle cx="' + x + '" cy="' + y + '" r="11" fill="url(#sgl-halo)"/>'; if (i % 2) lbA += g; else lbB += g; lbCore += '<circle cx="' + x + '" cy="' + y + '" r="5.2" fill="url(#sgl-bulb)" stroke="' + I + '" stroke-width="1.6"/>'; };
  let li = 0;
  for (let x = 54; x <= 506; x += 22.6) { lbulb(r2(x), 17, li++); }
  for (let y = 50; y <= 150; y += 25) { lbulb(544, y, li++); }
  for (let x = 506; x >= 54; x -= 22.6) { lbulb(r2(x), 183, li++); }
  for (let y = 150; y >= 50; y -= 25) { lbulb(16, y, li++); }
  const T = 'font-family="Onest, system-ui, sans-serif" font-weight="900" text-anchor="middle"';
  const star4 = (x, y, r) => '<path transform="translate(' + x + ' ' + y + ')" d="M0 ' + (-r) + 'Q' + r2(r * .18) + ' ' + r2(-r * .18) + ' ' + r + ' 0Q' + r2(r * .18) + ' ' + r2(r * .18) + ' 0 ' + r + 'Q' + r2(-r * .18) + ' ' + r2(r * .18) + ' ' + (-r) + ' 0Q' + r2(-r * .18) + ' ' + r2(-r * .18) + ' 0 ' + (-r) + 'Z" fill="#fff6c0" stroke="' + I + '" stroke-width="1.6"/>';
  const logoInner = '<defs>' +
    lg('sgl-gold', 0, 0, 0, 1, [[0, '#fffbe0'], [.35, '#ffe27a'], [.6, '#f5b82a'], [1, '#c4700e']]) +
    lg('sgl-red', 0, 0, 0, 1, [[0, '#ff8a7a'], [.45, '#f01c32'], [1, '#9a0820']]) +
    rg('sgl-plaque', .5, .4, .8, [[0, '#7a1a34'], [.7, '#40081c'], [1, '#26040f']]) +
    rg('sgl-bulb', .4, .35, .7, [[0, '#ffffff'], [.35, '#fff3a0'], [1, '#ffaa10']]) +
    rg('sgl-halo', .5, .5, .5, [[0, '#fff2a0', .9], [.5, '#ffbe2a', .35], [1, '#ff9a00', 0]]) +
    '</defs>' +
    '<path d="' + rr(5, 5, 550, 190, 46) + '" fill="url(#sgl-plaque)" stroke="' + I + '" stroke-width="6"/>' +
    '<path d="' + rr(16, 17, 528, 166, 36) + '" fill="none" stroke="url(#sgl-gold)" stroke-width="3.4"/>' +
    '<g class="sg-lbA">' + lbA + '</g><g class="sg-lbB">' + lbB + '</g>' + lbCore +
    stitch(196, 92, 7, ['x.o.x.o.x.o.x.o.x.o.x'].map((r) => r.slice(0, 24)), { x: '#e3122a', o: '#f5c33b' }) +
    '<text x="280" y="84" ' + T + ' font-size="56" letter-spacing="3" fill="#5a0a14" stroke="#5a0a14" stroke-width="12" stroke-linejoin="round" transform="translate(0 5)">ОДНОРУКИЙ</text>' +
    '<text x="280" y="84" ' + T + ' font-size="56" letter-spacing="3" fill="url(#sgl-gold)" stroke="' + I + '" stroke-width="10" stroke-linejoin="round" paint-order="stroke">ОДНОРУКИЙ</text>' +
    '<text x="280" y="178" ' + T + ' font-size="104" letter-spacing="6" fill="#4a0612" stroke="#4a0612" stroke-width="16" stroke-linejoin="round" transform="translate(0 6)">ГЛЕК</text>' +
    '<text x="280" y="178" ' + T + ' font-size="104" letter-spacing="6" fill="none" stroke="' + I + '" stroke-width="16" stroke-linejoin="round">ГЛЕК</text>' +
    '<text x="280" y="178" ' + T + ' font-size="104" letter-spacing="6" fill="url(#sgl-red)" stroke="url(#sgl-gold)" stroke-width="6" stroke-linejoin="round" paint-order="stroke">ГЛЕК</text>' +
    star4(118, 128, 14) + star4(442, 128, 14) + star4(96, 98, 7) + star4(464, 98, 7);
  const logo = '<svg viewBox="0 0 560 200" class="sg sg-logo" aria-hidden="true">' + logoInner + '</svg>';

  /* ---------- афіша 360×240 ---------- */
  const nest = (svgStr, x, y, w, h) => svgStr.replace('<svg ', '<svg x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" ');
  const SYM = { cherry, pear, plum, melon, bell, horseshoe, seven, glek };
  const cs = 158 / 640, mx = 64, my = 96;
  const wx = mx + 56 * cs, wy = my + 182 * cs, cell = 308 * cs / 3;
  const grid = [['cherry', 'bell', 'plum'], ['seven', 'seven', 'seven'], ['melon', 'glek', 'pear']];
  let gridSvg = '<rect x="' + r2(wx) + '" y="' + r2(wy) + '" width="' + r2(cell * 3) + '" height="' + r2(cell * 3) + '" fill="#fff8ec"/>';
  grid.forEach((row, j) => row.forEach((k, i) => { gridSvg += nest(SYM[k], r2(wx + i * cell + 1.5), r2(wy + j * cell + 1.5), r2(cell - 3), r2(cell - 3)); }));
  gridSvg += '<rect x="' + r2(wx) + '" y="' + r2(wy + cell * 1.5 - 1) + '" width="' + r2(cell * 3) + '" height="2" fill="#e3122a" opacity=".8"/>';
  let burst = '';
  for (let i = 0; i < 18; i++) { const a1 = (i * 20) * Math.PI / 180, a2 = (i * 20 + 9) * Math.PI / 180; burst += '<path d="M0 0L' + r2(300 * Math.cos(a1)) + ' ' + r2(300 * Math.sin(a1)) + 'L' + r2(300 * Math.cos(a2)) + ' ' + r2(300 * Math.sin(a2)) + 'Z"/>'; }
  let coins = '';
  [[96, 232, -20], [112, 236, 10], [128, 230, 30], [86, 222, 60], [140, 220, -40], [150, 236, 0], [74, 236, 20]].forEach((c) => {
    coins += '<g transform="translate(' + c[0] + ' ' + c[1] + ') rotate(' + c[2] + ')"><ellipse rx="7" ry="5" fill="url(#sg-gold-v)" stroke="' + I + '" stroke-width="1.4"/><ellipse rx="3.6" ry="2.4" fill="none" stroke="#b07a1c" stroke-width="1"/></g>';
  });
  const poster = '<svg viewBox="0 0 360 240" class="sg sg-poster" aria-hidden="true"><defs>' + DEFS_INNER +
    lg('sgp-sky', 0, 0, 0, 1, [[0, '#160c3a'], [.5, '#4a2062'], [.82, '#b2465c'], [1, '#f08a4e']]) +
    rg('sgp-glow', .5, .5, .5, [[0, '#fff2a0', .85], [.4, '#ffb43a', .35], [1, '#ff7a00', 0]]) +
    '</defs>' +
    '<rect width="360" height="240" fill="url(#sgp-sky)"/>' +
    '<g transform="translate(112 168)" fill="#ffd36a" opacity=".16">' + burst + '</g>' +
    '<circle cx="112" cy="168" r="120" fill="url(#sgp-glow)"/>' +
    '<g transform="translate(330 150) scale(.42)" opacity=".8">' + wheel + '</g>' +
    '<path d="M0 205C60 190 120 200 180 196C240 192 300 186 360 196V240H0Z" fill="#1e0e28"/>' +
    garland(-10, 6, 370, 10, 14, 15, 'sg-pga', ['#ffd23a', '#ff5a4a', '#7ad0ff', '#9aea6a'], 3.2) +
    nest('<svg viewBox="0 0 420 640">' + cabinetInner + '</svg>', mx, my, r2(420 * cs), 158) + gridSvg +
    // ручка, яку тягне Глек
    '<path d="M168 202L176 132" stroke="' + I + '" stroke-width="7" stroke-linecap="round"/><path d="M168 202L176 132" stroke="url(#sg-steel)" stroke-width="3.6" stroke-linecap="round"/>' +
    '<circle cx="168" cy="200" r="6" fill="url(#sg-brass)" stroke="' + I + '" stroke-width="2"/>' +
    '<circle cx="177" cy="125" r="10" fill="url(#sg-red)" stroke="' + I + '" stroke-width="2.2"/><ellipse cx="173.5" cy="121" rx="3.4" ry="2.2" transform="rotate(-35 173.5 121)" fill="#fff" opacity=".9"/>' +
    // Глек (дзеркально) з рукою-ручкою
    '<path d="M240 192C214 192 196 168 186 132" fill="none" stroke="' + I + '" stroke-width="13" stroke-linecap="round"/>' +
    '<path d="M240 192C214 192 196 168 186 132" fill="none" stroke="url(#sg-clay-l)" stroke-width="7.6" stroke-linecap="round"/>' +
    '<g transform="translate(340 103) scale(-1.04 1.04)">' + glekBody({ handle: false }) + '</g>' +
    '<circle cx="186" cy="131" r="8.4" fill="url(#sg-clay)" stroke="' + I + '" stroke-width="2.4"/><path d="M181 128q4 3 8 1M181 133q4 3 8 1" fill="none" stroke="#7a3518" stroke-width="1.3" stroke-linecap="round"/>' +
    coins +
    [[34, 196, 20], [22, 172, -30], [48, 150, 50], [30, 124, 10]].map((c) => '<g transform="translate(' + c[0] + ' ' + c[1] + ') rotate(' + c[2] + ')"><ellipse rx="8" ry="6" fill="url(#sg-gold-v)" stroke="' + I + '" stroke-width="1.5"/><ellipse rx="4" ry="2.8" fill="none" stroke="#b07a1c" stroke-width="1.1"/><path d="M-4 -3.4Q-1 -5 2 -4.4" stroke="#fff" stroke-width="1.2" fill="none" opacity=".8"/></g>').join('') +
    '<g transform="translate(316 118) rotate(6)"><path d="' + rr(-30, -13, 60, 26, 13) + '" fill="#fff6e0" stroke="' + I + '" stroke-width="2.4"/><path d="M-14 11L-24 24L-4 12Z" fill="#fff6e0" stroke="' + I + '" stroke-width="2.4" stroke-linejoin="round"/><path d="M-15 10.5H-3" stroke="#fff6e0" stroke-width="3"/>' +
    '<text x="0" y="4.6" ' + T + ' font-size="13" fill="#c8102e">смикни!</text></g>' +
    star4(36, 150, 7) + star4(332, 96, 6) + star4(196, 104, 5) +
    nest(logo, 46, 4, 268, 96) +
    '</svg>';

  window.SlotArt = window.SlotArt || {};
  window.SlotArt['slot-glek'] = {
    title: 'Однорукий Глек',
    symbols: {
      cherry: { name: 'Вишні', tier: 'low', svg: cherry },
      pear: { name: 'Груша', tier: 'low', svg: pear },
      plum: { name: 'Слива', tier: 'low', svg: plum },
      melon: { name: 'Кавун', tier: 'low', svg: melon },
      bell: { name: 'Дзвоник', tier: 'high', svg: bell },
      horseshoe: { name: 'Підкова', tier: 'high', svg: horseshoe },
      seven: { name: 'Сімка', tier: 'high', svg: seven },
      glek: { name: 'Дядько Глек', tier: 'wild', svg: glek },
    },
    scene: { base: scene, bonus: null },
    logo: logo,
    poster: poster,
    extras: {
      defs: defs,
      handle: handle,
      bulb: bulb,
      cabinet: cabinet,
      glass: glass,
      card: card,
      fortune: fortune,
      bulbSpots: bulbSpots,
      layout: L,
    },
  };
})();
