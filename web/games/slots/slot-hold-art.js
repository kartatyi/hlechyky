/* Козацький скарб (slot-hold) — арт: символи, дукати, скриня, свічки, рамка, сцени, логотип, афіша.
   Звичайний скрипт. Реєструє SlotArt['slot-hold'] за контрактом _azart/VISUAL.md §4.
   Спільні градієнти — у extras.defs (id з префіксом sh-), символи на них посилаються.
   Анімовані групи мають класи a-*; CSS — у slot-hold-art.css (усе під .slot-slot-hold). */
(function () {
  'use strict';

  /* ---------- палітра (одне місце) ---------- */
  const O = '#2b1509';                 // обведення — темний горіх, не чорний
  const GOLD = '#ffd447', GOLD_D = '#c0700c', RED_D = '#8e1418', CREAM = '#fff3c0';
  const FONT = 'font-family="Onest, system-ui, sans-serif" font-weight="900"';

  const f1 = (v) => Math.round(v * 10) / 10;
  const S = (cls, body, vb, extra) => '<svg viewBox="' + (vb || '0 0 100 100') + '" class="' + cls + '" xmlns="http://www.w3.org/2000/svg"' + (extra || '') + '>' + body + '</svg>';
  const shadow = (cx, cy, rx, ry) => `<ellipse cx="${cx}" cy="${cy}" rx="${rx}" ry="${ry}" fill="url(#sh-shadow)"/>`;
  const star4 = (x, y, r, fill, op) => `<path d="M${x} ${f1(y - r)}Q${f1(x + r * .16)} ${f1(y - r * .16)} ${f1(x + r)} ${y}Q${f1(x + r * .16)} ${f1(y + r * .16)} ${x} ${f1(y + r)}Q${f1(x - r * .16)} ${f1(y + r * .16)} ${f1(x - r)} ${y}Q${f1(x - r * .16)} ${f1(y - r * .16)} ${x} ${f1(y - r)}Z" fill="${fill || '#fff'}"${op != null ? ` opacity="${op}"` : ''}/>`;
  const glint = (x, y, r) => `<g class="a-glint">${star4(x, y, r)}<circle cx="${x}" cy="${y}" r="${f1(r * .3)}" fill="#fff"/></g>`;
  function rays(cx, cy, R, n, fill, op, a0, a1) {
    let d = ''; const from = a0 == null ? 0 : a0, to = a1 == null ? Math.PI * 2 : a1;
    const full = a0 == null, step = (to - from) / (full ? n : n - 1), w = Math.abs(step) * .26;
    for (let i = 0; i < n; i++) {
      const a = from + i * step;
      d += `M${cx} ${cy}L${f1(cx + R * Math.cos(a - w))} ${f1(cy + R * Math.sin(a - w))}L${f1(cx + R * Math.cos(a + w))} ${f1(cy + R * Math.sin(a + w))}Z`;
    }
    return `<path d="${d}" fill="${fill}" opacity="${op}"/>`;
  }
  function rng(seed) { let s = seed >>> 0; return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296); }

  /* ---------- спільні градієнти ---------- */
  const st = (stops) => stops.map(([o, c, a]) => `<stop offset="${o}" stop-color="${c}"${a != null ? ` stop-opacity="${a}"` : ''}/>`).join('');
  const lg = (id, stops, x2, y2, x1, y1) => `<linearGradient id="${id}" x1="${x1 || 0}" y1="${y1 || 0}" x2="${x2 == null ? 0 : x2}" y2="${y2 == null ? 1 : y2}">${st(stops)}</linearGradient>`;
  const rg = (id, stops, cx, cy, r) => `<radialGradient id="${id}" cx="${cx == null ? .5 : cx}" cy="${cy == null ? .5 : cy}" r="${r == null ? .5 : r}">${st(stops)}</radialGradient>`;
  const BLADE = 'M29 64Q62 44 92 6Q74 50 38 77Z';

  const DEFS = '<svg xmlns="http://www.w3.org/2000/svg" width="0" height="0" style="position:absolute" aria-hidden="true"><defs>'
    + lg('sh-gold', [[0, '#fff7c4'], [.3, '#ffd84d'], [.65, '#f0a823'], [1, '#b5660b']])
    + lg('sh-goldh', [[0, '#9a5608'], [.28, '#f3b52c'], [.5, '#fff3b8'], [.72, '#f0b02a'], [1, '#8a4c06']], 1, 0)
    + lg('sh-goldrim', [[0, '#ffeb8f'], [.45, '#e3a128'], [1, '#7d4506']])
    + lg('sh-goldtxt', [[0, '#fffbe0'], [.3, '#ffe066'], [.52, '#ffc22e'], [.53, '#f29a18'], [1, '#c4620a']])
    + rg('sh-gold-r', [[0, '#fff6c0'], [.4, '#ffd447'], [.8, '#ee9f1e'], [1, '#c0700c']], .4, .35, .7)
    + rg('sh-silver-r', [[0, '#ffffff'], [.45, '#e2e8f1'], [.85, '#9aa7bb'], [1, '#77839a']], .4, .35, .7)
    + lg('sh-silverrim', [[0, '#ffffff'], [.5, '#a9b4c6'], [1, '#4f5a70']])
    + rg('sh-ruby-r', [[0, '#ffa3b1'], [.4, '#e8304d'], [.85, '#97102b'], [1, '#6a0719']], .4, .35, .7)
    + rg('sh-emer-r', [[0, '#b4ffd6'], [.4, '#2fbf78'], [.85, '#0e6e40'], [1, '#084a2b']], .4, .35, .7)
    + rg('sh-sapph-r', [[0, '#c6e0ff'], [.4, '#3c80ec'], [.85, '#173c9a'], [1, '#0d2468']], .4, .35, .7)
    + lg('sh-red', [[0, '#ff7d5e'], [.45, '#dc3129'], [1, '#8e1418']])
    + lg('sh-ruby', [[0, '#ff8798'], [.5, '#cc1f3e'], [1, '#6e0818']])
    + lg('sh-silver', [[0, '#ffffff'], [.5, '#cfd7e3'], [1, '#7d899e']])
    + lg('sh-wood', [[0, '#d39459'], [.5, '#a2622f'], [1, '#663817']])
    + lg('sh-woodd', [[0, '#7e4a24'], [1, '#3f220e']])
    + lg('sh-steel', [[0, '#ffffff'], [.35, '#dfe6ee'], [.6, '#94a3b5'], [1, '#e2e8ef']], 1, 1)
    + rg('sh-skin', [[0, '#ffe8cc'], [.6, '#f7bf92'], [1, '#d9895c']], .42, .36, .66)
    + lg('sh-hair', [[0, '#6b4630'], [1, '#22130a']])
    + rg('sh-shadow', [[0, '#120818', .55], [1, '#120818', 0]])
    + rg('sh-glow', [[0, '#fff6c8', 1], [.35, '#ffd247', .75], [1, '#ff9a1a', 0]])
    + lg('sh-linen', [[0, '#fff6e2'], [.6, '#efdcb6'], [1, '#d5b887']])
    + lg('sh-clay', [[0, '#8e3514'], [.22, '#e0763c'], [.42, '#f9a466'], [.75, '#c4552a'], [1, '#7a2a0e']], 1, 0)
    + lg('sh-horse', [[0, '#dc934f'], [.55, '#a95b2b'], [1, '#6e3315']])
    + lg('sh-horsen', [[0, '#1c1428'], [.55, '#3e2430'], [1, '#a65a2a']], 1, 0)
    + lg('sh-mane', [[0, '#3a2418'], [1, '#120a08']])
    + lg('sh-foam', [[0, '#ffffff'], [1, '#efe2c4']])
    + lg('sh-hairw', [[0, '#c3cad8'], [.45, '#ffffff'], [1, '#b2b9cc']], 1, 0)
    + lg('sh-gloss', [[0, '#fff', .8], [.48, '#fff', .12], [.5, '#fff', 0], [1, '#fff', 0]])
    + lg('sh-wax', [[0, '#d2b680'], [.4, '#fff6dc'], [1, '#c4a066']], 1, 0)
    + lg('sh-waxoff', [[0, '#8e7a58'], [.4, '#c9b48c'], [1, '#86704c']], 1, 0)
    + rg('sh-flame', [[0, '#fffbe0'], [.35, '#ffe04a'], [.75, '#ff8a1a'], [1, '#e8341a']], .5, .78, .75)
    + rg('sh-fglow', [[0, '#ff9a3a', .8], [.5, '#ff6a1a', .25], [1, '#ff4a10', 0]])
    + lg('sh-sky', [[0, '#070b28'], [.42, '#141a52'], [.72, '#312a6c'], [.88, '#5b3a74'], [1, '#86506e']])
    + lg('sh-skyb', [[0, '#03040f'], [.5, '#0b0c30'], [.8, '#1c1640'], [1, '#3a2433']])
    + rg('sh-moon', [[0, '#fff3c8', .5], [1, '#fff3c8', 0]])
    + lg('sh-ground', [[0, '#1f2b46'], [.35, '#152036'], [1, '#080c18']])
    + lg('sh-groundb', [[0, '#131628'], [1, '#04050b']])
    + lg('sh-hill', [[0, '#2b2c62'], [1, '#1a1a40']])
    + lg('sh-hill2', [[0, '#232452'], [1, '#141530']])
    + rg('sh-cglow', [[0, '#ffe9a0', .95], [.3, '#ffc23a', .5], [1, '#ff9a1a', 0]])
    + `<radialGradient id="sh-crays" gradientUnits="userSpaceOnUse" cx="800" cy="880" r="760">${st([[0, '#ffe58a', .55], [.45, '#ffd86a', .16], [1, '#ffd86a', 0]])}</radialGradient>`
    + `<radialGradient id="sh-chray" gradientUnits="userSpaceOnUse" cx="100" cy="74" r="130">${st([[0, '#fff3b0', .95], [.5, '#ffd447', .45], [1, '#ffb020', 0]])}</radialGradient>`
    + `<radialGradient id="sh-jray" gradientUnits="userSpaceOnUse" cx="300" cy="66" r="160">${st([[0, '#ffe27a', .7], [.6, '#ffd447', .2], [1, '#ffd447', 0]])}</radialGradient>`
    + rg('sh-milky', [[0, '#c8c8ff', .2], [1, '#c8c8ff', 0]])
    + lg('sh-recess', [[0, '#05040b'], [.55, '#130d1e'], [1, '#261a34']])
    + lg('sh-panel', [[0, '#0f0a1e', .9], [.5, '#1b1232', .88], [1, '#120c24', .92]])
    + `<clipPath id="sh-coin-clip"><circle cx="50" cy="50" r="43"/></clipPath>`
    + `<clipPath id="sh-blade-clip"><path d="${BLADE}"/></clipPath>`
    + `<pattern id="sh-orn" width="16" height="12" patternUnits="userSpaceOnUse"><rect width="16" height="12" fill="#a51c22"/><path d="M8 1L14 6L8 11L2 6Z" fill="#1c0c08"/><path d="M8 3.6L10.4 6L8 8.4L5.6 6Z" fill="#ffcf4a"/><path d="M0 6L1.5 4.5L3 6L1.5 7.5Z M16 6L14.5 4.5L13 6L14.5 7.5Z" fill="#ffcf4a"/></pattern>`
    + '</defs></svg>';

  /* ---------- низькі: вишиті знаки-масті (хрестиком) ---------- */
  const STITCH = {
    r: { d: '#8c1020', b: '#ee2a3c', h: '#ffb0a0' },   // червоний
    k: { d: '#0c0a12', b: '#3a3346', h: '#9c93ad' },   // чорний
    u: { d: '#0f2a6a', b: '#2f7bf0', h: '#b6d8ff' },   // синій
    g: { d: '#0b4a20', b: '#22ac4a', h: '#b4f5a6' },   // зелений
    y: { d: '#9a6a08', b: '#ffcb3a', h: '#fff4b0' },   // золотий
    w: { d: '#8f9ab4', b: '#ffffff', h: '#ffffff' },   // білий
  };
  function stitch(rows, map, size) {
    const h = rows.length, w = Math.max.apply(null, rows.map((r) => r.length));
    const c = size / Math.max(w, h), x0 = 50 - w * c / 2, y0 = 52 - h * c / 2, p = c * .2;
    const sq = {}, xs = {}, hs = {}; let all = '';
    rows.forEach((r, j) => [...r].forEach((ch, i) => {
      if (!map[ch]) return;
      const x = f1(x0 + i * c), y = f1(y0 + j * c), cc = f1(c);
      const s = `M${x} ${y}h${cc}v${cc}h${-cc}Z`;
      all += s; sq[ch] = (sq[ch] || '') + s;
      xs[ch] = (xs[ch] || '') + `M${f1(x + p)} ${f1(y + p)}L${f1(x + c - p)} ${f1(y + c - p)}M${f1(x + c - p)} ${f1(y + p)}L${f1(x + p)} ${f1(y + c - p)}`;
      hs[ch] = (hs[ch] || '') + `M${f1(x + c - p - .4)} ${f1(y + p - .2)}L${f1(x + p + c * .25)} ${f1(y + c * .55)}`;
    }));
    let out = `<path d="${all}" fill="${O}" stroke="${O}" stroke-width="${f1(c * .9)}" stroke-linejoin="round"/>`;
    for (const ch in sq) {
      const k = STITCH[map[ch]];
      out += `<path d="${sq[ch]}" fill="${k.d}"/>`
        + `<path d="${xs[ch]}" stroke="${k.b}" stroke-width="${f1(c * .34)}" stroke-linecap="round" fill="none"/>`
        + `<path d="${hs[ch]}" stroke="${k.h}" stroke-width="${f1(c * .12)}" stroke-linecap="round" fill="none" opacity=".9"/>`;
    }
    return out;
  }
  function lowSym(cls, rows, map, border) {
    const k = STITCH[border];
    return S('sym sh-sym sh-low ' + cls,
      shadow(50, 93, 36, 4.5)
      + `<g class="a-pop">`
      + `<rect x="9" y="9" width="82" height="82" rx="16" fill="url(#sh-linen)" stroke="${O}" stroke-width="3"/>`
      + `<rect x="15" y="15" width="70" height="70" rx="11" fill="none" stroke="${k.b}" stroke-width="2.2" stroke-dasharray="3.2 2.6" opacity=".9"/>`
      + `<path d="M18 22Q18 14 26 14H60" stroke="#fff" stroke-width="3" stroke-linecap="round" fill="none" opacity=".75"/>`
      + stitch(rows, map, 62)
      + `</g>` + glint(26, 26, 8));
  }
  const S1 = lowSym('sh-s1', [
    '.XXX....XXX.',
    'XXXXX..XXXXX',
    'XXXXXXXXXXXX',
    'XXXXXooXXXXX',
    'XXXXooooXXXX',
    '.XXXXooXXXX.',
    '..XXXXXXXX..',
    '...XXXXXX...',
    '....XXXX....',
    '.....XX.....'], { X: 'r', o: 'y' }, 'r');
  const S2 = lowSym('sh-s2', [
    '.....X.....',
    '....XXX....',
    '...XXXXX...',
    '..XXXXXXX..',
    '.XXXXoXXXX.',
    'XXXXoooXXXX',
    'XXXXXoXXXXX',
    'XXXXXXXXXXX',
    '.XXX.X.XXX.',
    '.....X.....',
    '....XXX....',
    '...XXXXX...'], { X: 'k', o: 'r' }, 'k');
  const S3 = lowSym('sh-s3', [
    '.....X.....',
    '....XXX....',
    '...XXXXX...',
    '..XXXoXXX..',
    '.XXXoXoXXX.',
    'XXXoXXXoXXX',
    '.XXXoXoXXX.',
    '..XXXoXXX..',
    '...XXXXX...',
    '....XXX....',
    '.....X.....'], { X: 'u', o: 'w' }, 'u');
  const S4 = lowSym('sh-s4', [
    '....XXX....',
    '...XXXXX...',
    '...XXoXX...',
    '...XXXXX...',
    '.XXX.X.XXX.',
    'XXXXXXXXXXX',
    'XXoXXXXXoXX',
    'XXXXXXXXXXX',
    '.XXX.X.XXX.',
    '.....X.....',
    '....XXX....',
    '...XXXXX...'], { X: 'g', o: 'y' }, 'g');

  /* ---------- петриківська квітка ---------- */
  function flower(x, y, s, c1, c2, n) {
    n = n || 5; let h = '';
    for (let i = 0; i < n; i++) {
      const a = i * 360 / n;
      h += `<path transform="rotate(${a} ${x} ${y})" d="M${x} ${y}C${f1(x - 4 * s)} ${f1(y - 4 * s)} ${f1(x - 4.4 * s)} ${f1(y - 10 * s)} ${x} ${f1(y - 13 * s)}C${f1(x + 4.4 * s)} ${f1(y - 10 * s)} ${f1(x + 4 * s)} ${f1(y - 4 * s)} ${x} ${y}Z" fill="${c1}" stroke="${O}" stroke-width="${f1(.9 * s)}"/>`;
    }
    return h + `<circle cx="${x}" cy="${y}" r="${f1(3.4 * s)}" fill="${c2}" stroke="${O}" stroke-width="${f1(.9 * s)}"/><circle cx="${f1(x - s)}" cy="${f1(y - s)}" r="${f1(1 * s)}" fill="#fff" opacity=".8"/>`;
  }

  /* ---------- козак ---------- */
  function cossackInner() {
    return shadow(50, 96, 36, 4)
      + `<g class="a-body">`
      + `<path d="M7 101C8 84 22 74 38 72H62C78 74 92 84 93 101Z" fill="url(#sh-red)" stroke="${O}" stroke-width="3" stroke-linejoin="round"/>`
      + `<path d="M12 92C14 84 22 78 30 76" stroke="#ffb09a" stroke-width="2.4" stroke-linecap="round" fill="none" opacity=".7"/>`
      + `<path d="M28 85h11M28 92h11M61 85h11M61 92h11" stroke="${O}" stroke-width="4.6" stroke-linecap="round"/>`
      + `<path d="M28 85h11M28 92h11M61 85h11M61 92h11" stroke="${GOLD}" stroke-width="2.4" stroke-linecap="round"/>`
      + `<path d="M39 70L50 88L61 70Z" fill="#fff8ec" stroke="${O}" stroke-width="2.4" stroke-linejoin="round"/>`
      + `<path d="M42.5 73.5L50 85L57.5 73.5" stroke="#d42a2a" stroke-width="2.2" stroke-dasharray="2 1.5" fill="none"/>`
      + `<path d="M41 60V73Q50 79 59 73V60Z" fill="#e7a377" stroke="${O}" stroke-width="2.4"/>`
      + `</g>`
      + `<g class="a-head">`
      + `<circle cx="24.5" cy="46" r="6.4" fill="url(#sh-skin)" stroke="${O}" stroke-width="2.6"/>`
      + `<circle cx="75.5" cy="46" r="6.4" fill="url(#sh-skin)" stroke="${O}" stroke-width="2.6"/>`
      + `<path d="M24 47.5a2.6 2.6 0 0 1 2-3" stroke="#c97a50" stroke-width="1.4" fill="none"/>`
      + `<ellipse cx="50" cy="44" rx="25.5" ry="27" fill="url(#sh-skin)" stroke="${O}" stroke-width="3"/>`
      + `<path d="M27 37C28 22 40 17 50 17C60 17 72 22 73 37C66 29 58 26 50 26C42 26 34 29 27 37Z" fill="#7a5a60" opacity=".12"/>`
      + `<ellipse cx="62" cy="25" rx="7" ry="3.4" transform="rotate(25 62 25)" fill="#fff" opacity=".55"/>`
      + `<g class="a-brow"><path d="M32.5 35.5Q39 29.5 46 33.5M54 33.5Q61 29.5 67.5 35.5" stroke="#3a2010" stroke-width="3.8" stroke-linecap="round" fill="none"/></g>`
      + `<ellipse cx="40" cy="42" rx="4.8" ry="5.6" fill="#fff" stroke="${O}" stroke-width="1.8"/><circle cx="41" cy="43" r="3" fill="#2a1408"/><circle cx="42.2" cy="41.6" r="1.1" fill="#fff"/>`
      + `<g class="a-wink"><ellipse cx="60" cy="42" rx="4.8" ry="5.6" fill="#fff" stroke="${O}" stroke-width="1.8"/><circle cx="61" cy="43" r="3" fill="#2a1408"/><circle cx="62.2" cy="41.6" r="1.1" fill="#fff"/></g>`
      + `<circle cx="32" cy="54" r="5.5" fill="#ff5b5b" opacity=".32"/><circle cx="68" cy="54" r="5.5" fill="#ff5b5b" opacity=".32"/>`
      + `<path d="M42 63Q50 72 58 63Q50 66.5 42 63Z" fill="#8a1a14" stroke="${O}" stroke-width="1.8" stroke-linejoin="round"/>`
      + `<g class="a-twirl-l"><path d="M50 54.5C43 51.5 32 52.5 24 59.5C20 63 15 62 15 57.5C11.5 64 17 70.5 25.5 67C33.5 64 42 62 50 61Z" fill="url(#sh-hair)" stroke="${O}" stroke-width="2.4" stroke-linejoin="round"/><path d="M45 56C38 55 31 57 26 61" stroke="#9a7458" stroke-width="1.3" fill="none" stroke-linecap="round"/></g>`
      + `<g class="a-twirl-r"><path d="M50 54.5C57 51.5 68 52.5 76 59.5C80 63 85 62 85 57.5C88.5 64 83 70.5 74.5 67C66.5 64 58 62 50 61Z" fill="url(#sh-hair)" stroke="${O}" stroke-width="2.4" stroke-linejoin="round"/><path d="M55 56C62 55 69 57 74 61" stroke="#9a7458" stroke-width="1.3" fill="none" stroke-linecap="round"/></g>`
      + `<ellipse cx="50" cy="50.5" rx="6.6" ry="5.6" fill="#f08e74" stroke="${O}" stroke-width="2.2"/><circle cx="47.8" cy="48.6" r="1.7" fill="#fff" opacity=".75"/>`
      + `<g class="a-chub"><path d="M61 22C62 6 44 -2 29 4C17 9 10 21 11 33C11.5 41 17 46 23.5 43.5C27 42 26.5 37.5 23 37C19.5 36.5 18.5 33 19 30C20 21 26 14.5 34 13C40.5 12 45 15 42 22.5Z" fill="url(#sh-hair)" stroke="${O}" stroke-width="2.6" stroke-linejoin="round"/><path d="M30 8.5C23 12 17 20 15.5 30M40 7C48 7 55 11 56 18" stroke="#b08a6a" stroke-width="1.8" fill="none" stroke-linecap="round"/></g>`
      + `<circle cx="24" cy="55" r="3.6" fill="none" stroke="${O}" stroke-width="4"/><circle cx="24" cy="55" r="3.6" fill="none" stroke="${GOLD}" stroke-width="2"/>`
      + `</g>`;
  }
  const COSSACK = S('sym sh-sym sh-high sh-cossack', cossackInner() + glint(64, 22, 9));

  /* ---------- кінь ---------- */
  function horseInner(o) {
    o = o || {};
    const body = o.body || 'url(#sh-horse)', far = o.far || '#7a3c18', mane = o.mane || 'url(#sh-mane)', ol = o.ol || O;
    const leg = (d, c, w) => `<path d="${d}" stroke="${ol}" stroke-width="${w + 3}" stroke-linecap="round" stroke-linejoin="round" fill="none"/><path d="${d}" stroke="${c}" stroke-width="${w}" stroke-linecap="round" stroke-linejoin="round" fill="none"/>`;
    const hoof = (x, y) => `<path d="M${x - 4.4} ${y}h8.8l-.8 4.2h-7.2Z" fill="#2a1a12" stroke="${ol}" stroke-width="1.8" stroke-linejoin="round"/>`;
    return `<g class="a-rear">`
      // дальні ноги
      + leg('M41 60L38 75L40 88', far, 6.2) + hoof(40, 88)
      + `<g class="a-fleg2">` + leg('M60 60L58 75L59 88', far, 6.2) + hoof(59, 88) + `</g>`
      // хвіст
      + `<g class="a-tail"><path d="M24 47C12 44 5 56 9 70C11 77 7 83 4 86C14 85 21 76 19 65C18 58 21 53 27 51Z" fill="${mane}" stroke="${ol}" stroke-width="2.4" stroke-linejoin="round"/><path d="M19 50C12 54 11 63 13 71" stroke="#6b4a36" stroke-width="1.3" fill="none" stroke-linecap="round"/></g>`
      // ближні ноги
      + leg('M31 58L26 73L29 88', body === 'url(#sh-horse)' ? '#9a5226' : body, 7) + hoof(29, 88)
      + `<g class="a-fleg">` + leg('M66 59L67 74L66 88', body === 'url(#sh-horse)' ? '#a95b2b' : body, 7) + hoof(66, 88) + `</g>`
      // тулуб, шия, голова
      + `<path d="M24 46C30 38 50 40 60 40C64 32 68 24 73 17.5L76 11.5L80 17.5C84 20 90 26 93.5 32.5C95.5 37 93 41.5 88.5 40.5C84.5 39.5 80 37 76.5 35.5C73 40 70 46 70 52C70 58 66 64 60 65.5L36 66.5C28 66.5 21 60 21 53C21 50 22 48 24 46Z" fill="${body}" stroke="${ol}" stroke-width="2.8" stroke-linejoin="round"/>`
      + `<path d="M34 63C44 66 56 65 64 61" stroke="#f0b47a" stroke-width="2" fill="none" opacity=".55" stroke-linecap="round"/>`
      + `<path d="M28 47C34 42 44 42 52 43" stroke="#ffd3a0" stroke-width="2.2" fill="none" opacity=".55" stroke-linecap="round"/>`
      // попона й сідло
      + `<path d="M38 42.5Q48 40 58 41.5L57.5 57Q47.5 59 38.5 57Z" fill="url(#sh-red)" stroke="${ol}" stroke-width="2.2" stroke-linejoin="round"/>`
      + `<path d="M39.5 55Q48 56.8 56.5 55" stroke="${GOLD}" stroke-width="1.8" fill="none"/>`
      + `<path d="M44 48l3 3m0-3l-3 3M50.5 48l3 3m0-3l-3 3" stroke="#fff3c0" stroke-width="1.3" stroke-linecap="round"/>`
      + `<path d="M41 42Q48 36 56 41.5Q48 39.5 41 42Z" fill="#5a2c12" stroke="${ol}" stroke-width="2" stroke-linejoin="round"/>`
      // грива
      + `<g class="a-mane"><path d="M74 15.5C67 15 62.5 21 58.5 25.5L63 26C58.5 30.5 55.5 34.5 51.5 38.5L57 38C54 41 52 44 49.5 45.5L61 41.5C64 33 68.5 25 75.5 19Z" fill="${mane}" stroke="${ol}" stroke-width="2.2" stroke-linejoin="round"/><path d="M76 17C79 20 81 22 81.5 26C78.5 24 77 22 76 17Z" fill="${mane}" stroke="${ol}" stroke-width="1.6" stroke-linejoin="round"/></g>`
      // морда
      + `<path d="M81.5 21L91.5 33" stroke="#fff6e8" stroke-width="2.6" stroke-linecap="round" opacity=".95"/>`
      + `<path d="M77 23.5L86 36.5M77 23.5L89 24" stroke="${RED_D}" stroke-width="1.8" stroke-linecap="round" fill="none"/>`
      + `<circle cx="77" cy="23.5" r="1.6" fill="${GOLD}" stroke="${ol}" stroke-width=".8"/>`
      + `<ellipse cx="82.5" cy="25.5" rx="2.2" ry="2.6" fill="#1a0d05"/><circle cx="83.2" cy="24.6" r=".8" fill="#fff"/>`
      + `<ellipse cx="91" cy="36" rx="1.4" ry="1" fill="#2a1408"/>`
      + `</g>`;
  }
  const HORSE = S('sym sh-sym sh-high sh-horse', shadow(48, 93.5, 36, 4) + horseInner() + glint(58, 34, 7));

  /* ---------- люлька ---------- */
  const PIPE_STEM = 'M60 65C46 73 27 66 15.5 36';
  const PIPE = S('sym sh-sym sh-high sh-pipe',
    shadow(52, 93, 34, 4.5)
    + `<g class="a-pipe">`
    + `<path d="${PIPE_STEM}" stroke="${O}" stroke-width="13" stroke-linecap="round" fill="none"/>`
    + `<path d="${PIPE_STEM}" stroke="#8a4f24" stroke-width="8.6" stroke-linecap="round" fill="none"/>`
    + `<path d="M57 64C45 69 30 63 19 39" stroke="#e0a066" stroke-width="1.8" stroke-linecap="round" fill="none" opacity=".8"/>`
    + `<circle cx="46.6" cy="68.7" r="4.4" fill="${GOLD}" stroke="${O}" stroke-width="2"/><circle cx="28.2" cy="57.2" r="4.2" fill="${GOLD}" stroke="${O}" stroke-width="2"/>`
    + `<path d="M15.5 36L11 25" stroke="${O}" stroke-width="10" stroke-linecap="round"/><path d="M15.5 36L11 25" stroke="#2c1d18" stroke-width="6" stroke-linecap="round"/>`
    + `<g transform="translate(69 56) scale(1.18) translate(-69 -56)">`
    + `<path d="M53 37H85L82 66Q69 81 57 66Z" fill="url(#sh-wood)" stroke="${O}" stroke-width="3" stroke-linejoin="round"/>`
    + `<path d="M54 47.5H84" stroke="${O}" stroke-width="5"/><path d="M54 47.5H84" stroke="${GOLD}" stroke-width="2.6"/>`
    + flower(69, 60, .55, '#ffcf4a', '#d42a2a', 6)
    + `<path d="M58 51L60 66" stroke="#fff" stroke-width="2.6" stroke-linecap="round" opacity=".4"/>`
    + `<ellipse cx="69" cy="37" rx="16" ry="4.8" fill="#3a1a0a" stroke="${O}" stroke-width="2.4"/>`
    + `<ellipse class="a-ember" cx="69" cy="37.6" rx="11" ry="2.8" fill="#ff7a1a"/><ellipse cx="67" cy="37.2" rx="5" ry="1.2" fill="#ffe27a"/></g>`
    + `</g>`
    + `<path class="a-wisp" d="M70 31C64 25 75 21 69 13" stroke="#e4def0" stroke-width="3" fill="none" opacity=".55" stroke-linecap="round"/>`
    + `<circle class="a-puff a-puff1" cx="70" cy="26" r="5.5" fill="#efeaf6"/><circle class="a-puff a-puff2" cx="64" cy="16" r="7" fill="#efeaf6"/><circle class="a-puff a-puff3" cx="73" cy="8" r="6" fill="#efeaf6"/>`
    + glint(62, 42, 7));

  /* ---------- кухоль ---------- */
  const MUG = S('sym sh-sym sh-high sh-mug',
    shadow(48, 94, 32, 4.5)
    + `<g class="a-mug">`
    + `<path d="M68 45C87 43 89 73 66 73" stroke="${O}" stroke-width="13" stroke-linecap="round" fill="none"/>`
    + `<path d="M68 45C87 43 89 73 66 73" stroke="#c8562a" stroke-width="7.6" stroke-linecap="round" fill="none"/>`
    + `<path d="M73 47C82 50 83 62 78 67" stroke="#f9a466" stroke-width="2" stroke-linecap="round" fill="none"/>`
    + `<path d="M21 33H73L68 85Q47 93 26 85Z" fill="url(#sh-clay)" stroke="${O}" stroke-width="3" stroke-linejoin="round"/>`
    + `<path d="M23.5 44Q47 50 70.5 44" stroke="#fff3d6" stroke-width="5" fill="none"/><path d="M24 44Q47 50 70 44" stroke="#d42a2a" stroke-width="2.4" stroke-dasharray="2.4 2.4" fill="none"/>`
    + `<path d="M26.5 79Q47 86 67.5 79" stroke="#6a240c" stroke-width="2.6" fill="none"/>`
    + flower(47, 63, .72, '#ffe27a', '#d42a2a', 5)
    + `<path d="M36 70Q31 74 33 79M58 70Q63 74 61 79" stroke="#2f8a3a" stroke-width="2.4" stroke-linecap="round" fill="none"/>`
    + `<path d="M29.5 50L31 78" stroke="#fff" stroke-width="3.6" stroke-linecap="round" opacity=".45"/>`
    + `<g class="a-foam"><path d="M17 35C13 26 22 19 29 23C31 13 45 12 48 20C52 12 66 14 67 22C74 18 81 27 76 34C74 40 66 39 63 37L61.5 47C61.5 52 55 52 55 47L54.5 38C46 41 36 41 30 38C23 41 17 40 17 35Z" fill="url(#sh-foam)" stroke="${O}" stroke-width="2.6" stroke-linejoin="round"/>`
    + `<circle cx="36" cy="27" r="2.4" fill="#fff" stroke="#d8c8a6" stroke-width="1"/><circle cx="56" cy="24" r="1.8" fill="#fff" stroke="#d8c8a6" stroke-width="1"/><circle cx="44" cy="31" r="1.4" fill="#e9dcbc"/>`
    + `<path d="M24 28C27 23 33 22 36 24" stroke="#fff" stroke-width="2" fill="none" stroke-linecap="round"/></g>`
    + `</g>` + glint(32, 22, 7));

  /* ---------- шабля ---------- */
  const SABRE = S('sym sh-sym sh-high sh-sabre',
    shadow(50, 93, 34, 4)
    + `<g class="a-sabre">`
    + `<path d="M13 88Q6 92 9 98" stroke="${O}" stroke-width="3.4" fill="none" stroke-linecap="round"/><path d="M13 88Q6 92 9 98" stroke="#d42a2a" stroke-width="1.8" fill="none" stroke-linecap="round"/>`
    + `<path d="M5 94L13 94L12 100L6 100Z" fill="#d42a2a" stroke="${O}" stroke-width="1.8" stroke-linejoin="round"/>`
    + `<path d="${BLADE}" fill="url(#sh-steel)" stroke="${O}" stroke-width="2.6" stroke-linejoin="round"/>`
    + `<path d="M35.5 67Q64 47.5 85 17" stroke="#7d8a9c" stroke-width="1.5" fill="none"/>`
    + `<path d="M39 72Q70 50 88 14" stroke="#fff" stroke-width="1.3" fill="none" opacity=".9"/>`
    + `<g clip-path="url(#sh-blade-clip)"><path class="a-shine" d="M22 -4L32 -4L12 104L2 104Z" fill="#fff" opacity=".9"/></g>`
    + `<path d="M23.5 60L44.5 81" stroke="${O}" stroke-width="8.4" stroke-linecap="round"/>`
    + `<path d="M23.5 60L44.5 81" stroke="#f3b52c" stroke-width="4.8" stroke-linecap="round"/>`
    + `<path d="M25 60.5L43 78.5" stroke="#fff3b8" stroke-width="1.4" stroke-linecap="round"/>`
    + `<circle cx="23.5" cy="60" r="3.6" fill="url(#sh-gold)" stroke="${O}" stroke-width="2"/><circle cx="44.5" cy="81" r="3.6" fill="url(#sh-gold)" stroke="${O}" stroke-width="2"/>`
    + `<path d="M32 72L17 87" stroke="${O}" stroke-width="9.6" stroke-linecap="round"/><path d="M32 72L17 87" stroke="#7a1a14" stroke-width="6" stroke-linecap="round"/>`
    + `<path d="M32 72L17 87" stroke="${GOLD}" stroke-width="6" stroke-dasharray="1.2 2.4"/>`
    + `<path d="M19 84.5Q11 86 10.5 95Q16 93.5 21.5 89.5Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="2" stroke-linejoin="round"/>`
    + `<circle cx="33.6" cy="70.4" r="3.6" fill="#d42a2a" stroke="${O}" stroke-width="1.8"/><circle cx="32.8" cy="69.4" r="1" fill="#fff"/>`
    + `</g>` + glint(78, 24, 8));

  /* ---------- бунчук (дикий) ---------- */
  const WILD = S('sym sh-sym sh-wild',
    `<g class="a-rays">` + rays(50, 50, 52, 16, GOLD, .5) + `</g>`
    + `<circle cx="50" cy="48" r="40" fill="url(#sh-glow)" opacity=".85"/>`
    + shadow(50, 96, 26, 3.5)
    + `<g class="a-wildbody">`
    + `<rect x="47.4" y="12" width="5.2" height="86" rx="2.2" fill="url(#sh-wood)" stroke="${O}" stroke-width="2"/>`
    + `<g class="a-hair">`
    + [[44, 22, 72, 9, '#c8202a'], [56, 78, 70, 9, '#c8202a'], [46, 31, 77, 8, 'url(#sh-hairw)'], [54, 70, 76, 8, 'url(#sh-hairw)'], [50, 52, 80, 10, 'url(#sh-hairw)']]
      .map(([cx, tx, ty, w, c]) => `<path d="M${cx - 5} 26C${cx - 9} 40 ${tx - w} ${ty - 22} ${tx} ${ty}C${tx + w} ${ty - 22} ${cx + 9} 40 ${cx + 5} 26Z" fill="${c}" stroke="${O}" stroke-width="2.2" stroke-linejoin="round"/>`
        + `<path d="M${cx} 30C${cx - 1} 44 ${tx - w * .2} ${ty - 18} ${tx} ${ty - 5}" stroke="${c === '#c8202a' ? '#ff8a7a' : '#aeb6ca'}" stroke-width="1.3" fill="none" stroke-linecap="round"/>`).join('')
    + `</g>`
    + `<path d="M37 21Q50 29 63 21L61 26.5Q50 33.5 39 26.5Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="2" stroke-linejoin="round"/>`
    + `<circle cx="50" cy="14.5" r="7.6" fill="url(#sh-gold-r)" stroke="${O}" stroke-width="2.4"/><circle cx="47.5" cy="12" r="2" fill="#fff" opacity=".9"/>`
    + `<path d="M50 0L54 8.5H46Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="2" stroke-linejoin="round"/>`
    + `</g>`
    + `<g class="a-ribbon">`
    + `<path d="M17 75L3 77L9 85L2 93L19 91Z" fill="${RED_D}" stroke="${O}" stroke-width="2.2" stroke-linejoin="round"/>`
    + `<path d="M83 75L97 77L91 85L98 93L81 91Z" fill="${RED_D}" stroke="${O}" stroke-width="2.2" stroke-linejoin="round"/>`
    + `<path d="M13 73Q50 65 87 73L87 91Q50 83 13 91Z" fill="url(#sh-red)" stroke="${O}" stroke-width="2.6" stroke-linejoin="round"/>`
    + `<path d="M15 76Q50 68.5 85 76M15 88Q50 80.5 85 88" stroke="${GOLD}" stroke-width="1.4" fill="none"/>`
    + `<text x="50" y="86.5" text-anchor="middle" font-size="15.5" letter-spacing=".4" ${FONT} fill="${CREAM}" stroke="${O}" stroke-width="3.2" stroke-linejoin="round" paint-order="stroke">ДИКИЙ</text>`
    + `</g>` + glint(36, 18, 8));

  /* ---------- дукати ---------- */
  const COIN = {
    gold: { rim: 'url(#sh-goldrim)', face: 'url(#sh-gold-r)', line: '#9a5a06', bead: '#fff1a8', txt: '#fffdf0', ts: '#6a3304' },
    silver: { rim: 'url(#sh-silverrim)', face: 'url(#sh-silver-r)', line: '#5d6a80', bead: '#ffffff', txt: '#ffffff', ts: '#26304a' },
    ruby: { rim: 'url(#sh-goldrim)', face: 'url(#sh-ruby-r)', line: '#5e0616', bead: '#fff1a8', txt: '#fff1b0', ts: '#4a0410' },
    emer: { rim: 'url(#sh-goldrim)', face: 'url(#sh-emer-r)', line: '#0a4a2a', bead: '#fff1a8', txt: '#fffdf0', ts: '#08351f' },
    sapph: { rim: 'url(#sh-goldrim)', face: 'url(#sh-sapph-r)', line: '#10286a', bead: '#fff1a8', txt: '#fffdf0', ts: '#0c1e52' },
  };
  function coinBody(k, rich) {
    const c = COIN[k]; let h = '';
    if (rich >= 2) h += `<circle cx="50" cy="50" r="50" fill="url(#sh-glow)" opacity="${rich >= 3 ? .9 : .6}"/>`
      + `<g class="a-rays">` + rays(50, 50, 54, rich >= 3 ? 16 : 12, 'url(#sh-glow)', rich >= 3 ? 1 : .75) + `</g>`;
    h += shadow(50, 94, 30, 4.5) + `<g class="a-coin">`;
    h += `<circle cx="50" cy="50" r="43" fill="${c.rim}" stroke="${O}" stroke-width="3"/>`;
    if (rich >= 1) {
      for (let i = 0; i < 28; i++) { const a = i / 28 * Math.PI * 2; h += `<circle cx="${f1(50 + 39 * Math.cos(a))}" cy="${f1(50 + 39 * Math.sin(a))}" r="1.5" fill="${c.bead}" opacity=".95"/>`; }
    } else h += `<circle cx="50" cy="50" r="39" fill="none" stroke="#fff" stroke-opacity=".4" stroke-width="1.4"/>`;
    h += `<circle cx="50" cy="50" r="35" fill="${c.face}" stroke="${c.line}" stroke-width="2"/>`;
    h += `<circle cx="50" cy="50" r="31.5" fill="none" stroke="${c.line}" stroke-opacity=".35" stroke-width="1.2"/>`;
    if (rich >= 3) for (let i = 0; i < 4; i++) {
      const a = i * Math.PI / 2 - Math.PI / 2, x = f1(50 + 39 * Math.cos(a)), y = f1(50 + 39 * Math.sin(a));
      h += `<path d="M${x} ${y - 4.6}L${x + 4} ${y}L${x} ${y + 4.6}L${x - 4} ${y}Z" fill="#e8304d" stroke="${O}" stroke-width="1.4"/><circle cx="${x - 1}" cy="${y - 1.4}" r="1" fill="#fff"/>`;
    }
    h += `<path d="M19 42A32 32 0 0 1 44 15.5" stroke="#fff" stroke-opacity=".75" stroke-width="3.4" stroke-linecap="round" fill="none"/>`;
    return h;
  }
  const coinEnd = (x, y, r) => `<g clip-path="url(#sh-coin-clip)"><path class="a-shine" d="M18 -4L32 -4L10 104L-4 104Z" fill="#fff" opacity=".55"/></g></g>` + glint(x || 30, y || 26, r || 8);
  function label(txt, c, cy, maxW, fsMax) {
    const t = String(txt); let units = 0;
    for (const ch of t) units += ch === '×' ? .4 : /\d/.test(ch) ? .62 : /[ІI1]/.test(ch) ? .34 : .74;
    const fs = f1(Math.min(fsMax, maxW / units)), y = f1(cy + fs * .36), sw = f1(Math.max(2.4, fs * .16));
    const body = t.charAt(0) === '×' ? `<tspan font-size="${f1(fs * .66)}" dy="${f1(-fs * .04)}">×</tspan><tspan dy="${f1(fs * .04)}">${t.slice(1)}</tspan>` : t;
    return `<text x="50" y="${f1(+y + 2)}" text-anchor="middle" font-size="${fs}" ${FONT} fill="${c.ts}">${body}</text>`
      + `<text x="50" y="${y}" text-anchor="middle" font-size="${fs}" ${FONT} fill="${c.txt}" stroke="${c.ts}" stroke-width="${sw}" stroke-linejoin="round" paint-order="stroke">${body}</text>`;
  }
  function coin(lbl) {
    const t = lbl == null ? '' : String(lbl).trim(), low = t.toLowerCase();
    if (!t) {   // базовий вигляд: дукат із розеткою
      const c = COIN.gold; let h = coinBody('gold', 1);
      for (let i = 0; i < 8; i++) h += `<path transform="rotate(${i * 45} 50 50)" d="M50 50C45 44 45 30 50 24C55 30 55 44 50 50Z" fill="#e39a1e" stroke="${c.line}" stroke-width="1.4"/>`;
      h += `<circle cx="50" cy="50" r="7" fill="url(#sh-gold)" stroke="${c.line}" stroke-width="1.6"/><circle cx="48" cy="48" r="2" fill="#fff" opacity=".8"/>`;
      for (let i = 0; i < 8; i++) { const a = (i + .5) * Math.PI / 4; h += `<circle cx="${f1(50 + 22 * Math.cos(a))}" cy="${f1(50 + 22 * Math.sin(a))}" r="2" fill="#fff3b0" stroke="${c.line}" stroke-width=".8"/>`; }
      return S('sym sh-sym sh-coin sh-coin-base', h + coinEnd());
    }
    let k = 'gold', rich = 1, txt = t;
    if (low.startsWith('мін') || low === 'mini') { k = 'silver'; rich = 1; txt = 'МІНІ'; }
    else if (low.startsWith('маж') || low === 'major') { k = 'ruby'; rich = 2; txt = 'МАЖОР'; }
    else if (low.startsWith('гет') || low === 'grand') { k = 'gold'; rich = 3; txt = 'ГЕТЬМАН'; }
    else {
      const n = parseFloat(t.replace(/[^\d.,]/g, '').replace(',', '.'));
      rich = !(n >= 3) ? 0 : n < 10 ? 1 : n < 25 ? 2 : 3;
      if (/^\d/.test(t)) txt = '×' + t;
    }
    const c = COIN[k];
    let h = coinBody(k, rich);
    if (k !== 'gold') h += `<path d="M38 26L42 20L46 25L50 18L54 25L58 20L62 26Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="1.4" stroke-linejoin="round"/>`;
    h += label(txt, c, k !== 'gold' ? 54 : 50, 58, 44);
    return S('sym sh-sym sh-coin sh-coin-' + k + ' sh-coin-r' + rich, h + coinEnd());
  }

  /* булава й пірнач (у локальних координатах: вертикально, голова вгорі, центр 0,0) */
  function bulava() {
    return `<rect x="-3.6" y="-9" width="7.2" height="46" rx="3.6" fill="url(#sh-goldh)" stroke="${O}" stroke-width="2.2"/>`
      + `<path d="M-3 14h6M-3 20h6M-3 26h6" stroke="${O}" stroke-width="1.4"/>`
      + `<circle cx="0" cy="38" r="4.4" fill="url(#sh-gold)" stroke="${O}" stroke-width="2"/>`
      + `<ellipse cx="0" cy="-7" rx="8" ry="3.4" fill="url(#sh-gold)" stroke="${O}" stroke-width="2"/>`
      + `<ellipse cx="0" cy="-21" rx="15" ry="14" fill="url(#sh-gold-r)" stroke="${O}" stroke-width="2.6"/>`
      + `<path d="M-6 -33Q-12 -21 -6 -9M6 -33Q12 -21 6 -9M0 -35V-7" stroke="#b56a0a" stroke-width="1.4" fill="none"/>`
      + `<circle cx="0" cy="-21" r="3.6" fill="#e8304d" stroke="${O}" stroke-width="1.4"/><circle cx="-9.5" cy="-21" r="2.6" fill="#3c80ec" stroke="${O}" stroke-width="1.2"/><circle cx="9.5" cy="-21" r="2.6" fill="#3c80ec" stroke="${O}" stroke-width="1.2"/>`
      + `<circle cx="-1" cy="-22.2" r="1" fill="#fff"/>`
      + `<path d="M-7 -28Q-4 -32 1 -33" stroke="#fff" stroke-width="2" fill="none" stroke-linecap="round" opacity=".8"/>`
      + `<path d="M0 -42L3 -35H-3Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="1.6" stroke-linejoin="round"/>`;
  }
  function pirnachW() {
    const vane = (sx, c) => `<path d="M0 -8C${8 * sx} -12 ${14 * sx} -22 ${13 * sx} -32C${7 * sx} -30 ${2 * sx} -34 0 -38Z" fill="${c}" stroke="${O}" stroke-width="2" stroke-linejoin="round"/>`;
    return `<rect x="-3.6" y="-9" width="7.2" height="46" rx="3.6" fill="url(#sh-goldh)" stroke="${O}" stroke-width="2.2"/>`
      + `<path d="M-3 14h6M-3 20h6M-3 26h6" stroke="${O}" stroke-width="1.4"/>`
      + `<circle cx="0" cy="38" r="4.4" fill="url(#sh-gold)" stroke="${O}" stroke-width="2"/>`
      + vane(-.62, '#d08a14') + vane(.62, '#d08a14') + vane(-1, 'url(#sh-gold)') + vane(1, 'url(#sh-gold)')
      + `<path d="M0 -6C-5 -16 -5 -30 0 -40C5 -30 5 -16 0 -6Z" fill="url(#sh-gold-r)" stroke="${O}" stroke-width="2" stroke-linejoin="round"/>`
      + `<path d="M-1.5 -12C-3.2 -20 -3 -28 -.6 -34" stroke="#fff" stroke-width="1.6" fill="none" stroke-linecap="round" opacity=".85"/>`
      + `<ellipse cx="0" cy="-6" rx="8" ry="3.2" fill="url(#sh-gold)" stroke="${O}" stroke-width="2"/>`
      + `<circle cx="0" cy="-43" r="3.6" fill="url(#sh-gold-r)" stroke="${O}" stroke-width="1.6"/>`;
  }
  function plate(txt, c) {
    const t = String(txt); let units = 0;
    for (const ch of t) units += ch === '×' ? .4 : /\d/.test(ch) ? .62 : .74;
    const fs = f1(Math.min(22, 46 / units));
    const body = t.charAt(0) === '×' ? `<tspan font-size="${f1(fs * .7)}">×</tspan>${t.slice(1)}` : t;
    return `<rect x="22" y="58" width="56" height="22" rx="9" fill="#1d0c04" stroke="${O}" stroke-width="2.4"/><rect x="23.6" y="59.6" width="52.8" height="18.8" rx="7.6" fill="none" stroke="${GOLD}" stroke-width="1.4"/>`
      + `<text x="50" y="${f1(69 + fs * .36)}" text-anchor="middle" font-size="${fs}" ${FONT} fill="${c}">${body}</text>`;
  }
  function weaponCoin(kind, lbl, isPir) {
    const t = lbl == null ? '' : String(lbl).trim(), cls = isPir ? 'sh-pirnach' : 'sh-mace';
    let h = coinBody(kind, 2);
    const W = isPir ? pirnachW() : bulava();
    if (!t) h += `<g transform="translate(50 52) rotate(32) scale(.82)"><g class="a-weapon">${W}</g></g>`;
    else h += `<g transform="translate(50 35) rotate(-58) scale(.6)"><g class="a-weapon">${W}</g></g>` + plate(/^\d/.test(t) ? '×' + t : t, isPir ? '#bfe0ff' : '#c4ffd8');
    return S('sym sh-sym sh-coin ' + cls, h + coinEnd(28, 24, 7));
  }
  const mace = (lbl) => weaponCoin('emer', lbl, false);
  const pirnach = (lbl) => weaponCoin('sapph', lbl == null ? '×2' : lbl, true);

  /* ---------- порожнє гніздо ---------- */
  const EMPTY = S('sym sh-empty',
    `<rect x="3" y="3" width="94" height="94" rx="14" fill="url(#sh-woodd)" stroke="${O}" stroke-width="3"/>`
    + `<path d="M6 30H94M6 64H94" stroke="#2a160a" stroke-width="1.4" opacity=".6"/>`
    + `<rect x="11" y="11" width="78" height="78" rx="11" fill="url(#sh-recess)" stroke="#120804" stroke-width="2.4"/>`
    + `<path d="M14 24Q14 14 24 14H76Q86 14 86 24" stroke="#000" stroke-opacity=".6" stroke-width="5" fill="none"/>`
    + `<path d="M17 86H83" stroke="#9a7ab0" stroke-opacity=".35" stroke-width="1.6" stroke-linecap="round"/>`
    + `<circle cx="50" cy="51" r="24" fill="none" stroke="#3d2e52" stroke-width="2.2" stroke-dasharray="3.4 3.4"/>`
    + `<circle cx="50" cy="51" r="17" fill="#ffd447" opacity=".04"/>`
    + [[8, 8], [92, 8], [8, 92], [92, 92]].map(([x, y]) => `<circle cx="${x}" cy="${y}" r="2.6" fill="url(#sh-gold)" stroke="${O}" stroke-width="1"/>`).join(''));

  /* ---------- лічильник респінів: три свічки, горять n ---------- */
  function respins(n) {
    let k = typeof n === 'number' ? n : parseInt(String(n == null ? '' : n).replace(/\D/g, ''), 10);
    if (!(k >= 0)) k = 3; k = Math.max(0, Math.min(3, k));
    let h = `<rect x="4" y="88" width="172" height="15" rx="6" fill="url(#sh-wood)" stroke="${O}" stroke-width="3"/><path d="M10 92H170" stroke="#e8b07a" stroke-width="1.6" opacity=".6"/>`;
    [40, 90, 140].forEach((x, i) => {
      const lit = i < k;
      h += `<g class="cnd${lit ? ' lit' : ''}">`;
      if (lit) h += `<circle class="a-halo" cx="${x}" cy="30" r="30" fill="url(#sh-glow)" opacity=".6"/>`;
      h += `<ellipse cx="${x}" cy="89" rx="20" ry="5.5" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.4"/>`
        + `<path d="M${x - 11} 48V86Q${x} 90 ${x + 11} 86V48Z" fill="url(#${lit ? 'sh-wax' : 'sh-waxoff'})" stroke="${O}" stroke-width="2.4" stroke-linejoin="round"/>`
        + `<path d="M${x - 11} 48Q${x - 8} 58 ${x - 6} 52Q${x - 4} 62 ${x - 1} 50Q${x + 4} 56 ${x + 6} 49" fill="${lit ? '#fff6dc' : '#c9b48c'}" stroke="${O}" stroke-width="1.6" stroke-linejoin="round"/>`
        + `<ellipse cx="${x}" cy="48" rx="11" ry="3.2" fill="${lit ? '#fffaea' : '#d8c7a2'}" stroke="${O}" stroke-width="2"/>`
        + `<path d="M${x - 6.5} 56V82" stroke="#fff" stroke-width="2.4" stroke-linecap="round" opacity="${lit ? .6 : .25}"/>`
        + `<path d="M${x} 48V${lit ? 40 : 41}" stroke="#2a1408" stroke-width="2" stroke-linecap="round"/>`;
      if (lit) h += `<g class="a-flame"><path d="M${x} 45C${x - 9} 40 ${x - 8} 27 ${x} 12C${x + 8} 27 ${x + 9} 40 ${x} 45Z" fill="url(#sh-flame)" stroke="#b8360f" stroke-width="1.4"/><path d="M${x} 43C${x - 4} 40 ${x - 4} 33 ${x} 26C${x + 4} 33 ${x + 4} 40 ${x} 43Z" fill="#fffbe0"/></g>`;
      else h += `<circle cx="${x}" cy="40.6" r="1.4" fill="#ff6a1a" opacity=".8"/><path class="a-smoke" d="M${x} 38C${x - 6} 32 ${x + 6} 28 ${x} 20C${x - 5} 15 ${x + 3} 11 ${x} 6" stroke="#b8b2c8" stroke-width="2.4" fill="none" stroke-linecap="round" opacity=".45"/>`;
      h += `</g>`;
    });
    return S('sh-respins sh-respins-' + k, h, '0 0 180 108');
  }

  /* ---------- скриня ---------- */
  function chestInner(open) {
    const coins = [[58, 66, 8], [80, 58, 8.5], [104, 54, 9.5], [127, 59, 8.5], [146, 69, 7.5], [70, 75, 7], [118, 72, 7.5], [93, 70, 8]]
      .map(([x, y, r]) => `<circle cx="${x}" cy="${y}" r="${r}" fill="url(#sh-gold-r)" stroke="${O}" stroke-width="2"/><circle cx="${x}" cy="${y}" r="${f1(r * .62)}" fill="none" stroke="#b56a0a" stroke-width="1.2"/><circle cx="${f1(x - r * .35)}" cy="${f1(y - r * .35)}" r="${f1(r * .2)}" fill="#fff" opacity=".85"/>`).join('');
    const glowOp = open ? '1' : '0';
    return `<g class="glow" opacity="${glowOp}"><g class="g-rays">` + rays(100, 74, 130, 9, 'url(#sh-chray)', 1, -Math.PI * .92, -Math.PI * .08) + `</g>`
      + `<ellipse cx="100" cy="70" rx="86" ry="56" fill="url(#sh-glow)"/></g>`
      + shadow(100, 156, 84, 9)
      + `<g class="body">`
      + `<g class="lid-in" opacity="${glowOp}"><path d="M30 70L20 24Q100 8 180 24L170 70Z" fill="url(#sh-woodd)" stroke="${O}" stroke-width="3" stroke-linejoin="round"/>`
      + `<path d="M26 38Q100 25 174 38M28 54Q100 44 172 54" stroke="#2a160a" stroke-width="1.6" fill="none"/>`
      + `<path d="M22 28Q100 13 178 28" stroke="${GOLD}" stroke-width="3" fill="none"/></g>`
      + `<path d="M28 82L36 66H164L172 82Z" fill="#2a1206" stroke="${O}" stroke-width="3" stroke-linejoin="round"/>`
      + `<path d="M34 82Q60 50 100 49Q140 50 166 82Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.4"/>`
      + coins
      + `<path d="M86 60L91 55L96 60L91 66Z" fill="#e8304d" stroke="${O}" stroke-width="1.4"/><path d="M134 62l4-4 4 4-4 5Z" fill="#3c80ec" stroke="${O}" stroke-width="1.4"/>`
      + star4(112, 46, 7, '#fff') + star4(66, 56, 5, '#fff')
      + `<ellipse class="in-light" cx="100" cy="66" rx="64" ry="18" fill="#ffe27a" opacity="${open ? .3 : 0}"/>`
      + `<rect x="26" y="80" width="148" height="68" rx="6" fill="url(#sh-wood)" stroke="${O}" stroke-width="3"/>`
      + `<path d="M28 102H172M28 125H172" stroke="#5c3216" stroke-width="2"/><path d="M28 104H172M28 127H172" stroke="#e8b07a" stroke-width="1" opacity=".5"/>`
      + `<rect x="46" y="80" width="13" height="68" fill="#3c3548" stroke="${O}" stroke-width="2"/><rect x="141" y="80" width="13" height="68" fill="#3c3548" stroke="${O}" stroke-width="2"/>`
      + [90, 112, 136].map((y) => `<circle cx="52.5" cy="${y}" r="2" fill="${GOLD}"/><circle cx="147.5" cy="${y}" r="2" fill="${GOLD}"/>`).join('')
      + `<path d="M48 84V144M143 84V144" stroke="#8a84a0" stroke-width="1.4" opacity=".7"/>`
      + `<path d="M26 128V142Q26 148 32 148H44V136Q34 136 34 128Z M174 128V142Q174 148 168 148H156V136Q166 136 166 128Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="2" stroke-linejoin="round"/>`
      + `<rect x="23" y="77" width="154" height="9" rx="3" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.4"/>`
      + `<path d="M86 92H114V112Q100 124 86 112Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.4" stroke-linejoin="round"/>`
      + `<circle cx="100" cy="101" r="3.4" fill="#2a1206"/><path d="M98.4 102.5L97 110H103L101.6 102.5Z" fill="#2a1206"/>`
      + `<path d="M34 92V140" stroke="#fff" stroke-width="2.4" opacity=".25" stroke-linecap="round"/>`
      + `</g>`
      + (open ? '' : `<g class="lid">`
        + `<path d="M23 82V60C23 30 177 30 177 60V82Z" fill="url(#sh-wood)" stroke="${O}" stroke-width="3" stroke-linejoin="round"/>`
        + `<path d="M25 60Q100 45 175 60M30 46Q100 33 170 46" stroke="#5c3216" stroke-width="2" fill="none"/>`
        + `<path d="M46 82V43Q52 41 59 40V82Z M141 82V40Q148 41 154 43V82Z" fill="#3c3548" stroke="${O}" stroke-width="2"/>`
        + [52.5, 147.5].map((x) => `<circle cx="${x}" cy="56" r="2" fill="${GOLD}"/><circle cx="${x}" cy="70" r="2" fill="${GOLD}"/>`).join('')
        + `<path d="M40 44Q70 34 104 34" stroke="#fff" stroke-width="3" stroke-linecap="round" fill="none" opacity=".35"/>`
        + `<rect x="21" y="75" width="158" height="9" rx="3" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.4"/>`
        + `<path d="M92 72H108V96Q100 102 92 96Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.2" stroke-linejoin="round"/><circle cx="100" cy="90" r="2.4" fill="#e8304d" stroke="${O}" stroke-width="1"/>`
        + `</g>`);
  }
  const CHEST = S('sh-chest', chestInner(false), '0 0 200 170');

  /* ---------- табличка джекпотів ---------- */
  function jpPlate(x, y, w, h, fill, title, tc, cls, val, big) {
    const cx = x + w / 2, tfs = big ? 17 : 19, vfs = big ? 34 : 26;
    return `<g class="jp ${cls}">`
      + `<rect x="${x}" y="${y}" width="${w}" height="${h}" rx="18" fill="${fill}" stroke="${O}" stroke-width="4"/>`
      + `<rect x="${x + 6}" y="${y + 6}" width="${w - 12}" height="${h - 12}" rx="13" fill="none" stroke="#fff" stroke-opacity=".45" stroke-width="1.6"/>`
      + `<path d="M${x + 16} ${y + 9}H${x + w * .55}" stroke="#fff" stroke-width="3" stroke-linecap="round" opacity=".6"/>`
      + `<text x="${cx}" y="${y + (big ? 31 : 29)}" text-anchor="middle" font-size="${tfs}" letter-spacing="1" ${FONT} fill="#fff" stroke="${tc}" stroke-width="4" stroke-linejoin="round" paint-order="stroke"${big ? ` textLength="${w - 30}" lengthAdjust="spacingAndGlyphs"` : ''}>${title}</text>`
      + `<rect x="${x + 12}" y="${y + h * .44}" width="${w - 24}" height="${h * .44}" rx="10" fill="#1a0c06" stroke="${O}" stroke-width="2"/>`
      + `<rect x="${x + 14}" y="${y + h * .44 + 2}" width="${w - 28}" height="${h * .44 - 4}" rx="8.5" fill="none" stroke="${GOLD}" stroke-width="1.4" opacity=".8"/>`
      + `<text class="jp-v jp-v-${cls.slice(3)}" x="${cx}" y="${f1(y + h * .66 + vfs * .36)}" text-anchor="middle" font-size="${vfs}" ${FONT} fill="#ffe27a">${val}</text>`
      + `</g>`;
  }
  /* Місця під суми: <text class="jp-v jp-v-mini|jp-v-major|jp-v-grand"> — кіт міняє textContent
     (зараз там множники ставки ×20 / ×100 / ×1000). Групи .jp.jp-mini / .jp-major / .jp-grand — для підсвітки. */
  const JACKPOTS = S('sh-jackpots',
    `<g class="a-rays">` + rays(300, 66, 150, 14, 'url(#sh-jray)', 1, Math.PI * 1.02, Math.PI * 1.98) + `</g>`
    + jpPlate(6, 24, 172, 92, 'url(#sh-silver)', 'МІНІ', '#3a4660', 'jp-mini', '×20')
    + jpPlate(422, 24, 172, 92, 'url(#sh-ruby)', 'МАЖОР', '#5e0616', 'jp-major', '×100')
    + jpPlate(190, 4, 220, 122, 'url(#sh-gold)', 'ГЕТЬМАНСЬКИЙ СКАРБ', '#7a3c06', 'jp-grand', '×1000', true)
    + star4(196, 12, 9) + star4(404, 118, 7) + star4(586, 30, 6),
    '0 0 600 130');

  /* ---------- рамка поля 5×3 ---------- */
  /* viewBox 0 0 540 340, вікно під поле — x 20 y 20, 500×300 (клітинка = 100).
     Накладати поверх поля: left −4 %, top −6,667 %, width 108 %, height 113,333 % від коробки поля; pointer-events:none.
     frameBack — те саме вікно темною панеллю з роздільниками барабанів, класти ПІД символи. */
  const RR = (x, y, w, h, r) => `M${x + r} ${y}H${x + w - r}Q${x + w} ${y} ${x + w} ${y + r}V${y + h - r}Q${x + w} ${y + h} ${x + w - r} ${y + h}H${x + r}Q${x} ${y + h} ${x} ${y + h - r}V${y + r}Q${x} ${y} ${x + r} ${y}Z`;
  const corner = `<path d="M2 58V16Q2 2 16 2H58L50 11H18Q11 11 11 18V50Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.4" stroke-linejoin="round"/><circle cx="20" cy="20" r="5" fill="#e8304d" stroke="${O}" stroke-width="1.8"/><circle cx="18.6" cy="18.6" r="1.4" fill="#fff"/><circle cx="36" cy="6.6" r="1.8" fill="${O}"/><circle cx="6.6" cy="36" r="1.8" fill="${O}"/>`;
  const FRAME = S('sh-frame-svg',
    `<path d="${RR(0, 0, 540, 340, 20)}${RR(20, 20, 500, 300, 4)}" fill-rule="evenodd" fill="url(#sh-wood)" stroke="${O}" stroke-width="3"/>`
    + `<path d="M24 3.5H516M24 336.5H516" stroke="#ffd9a8" stroke-width="1.4" opacity=".45"/>`
    + `<rect x="60" y="5" width="420" height="10" fill="url(#sh-orn)" stroke="${O}" stroke-width="1.4"/>`
    + `<rect x="60" y="325" width="420" height="10" fill="url(#sh-orn)" stroke="${O}" stroke-width="1.4"/>`
    + `<rect x="5" y="66" width="10" height="208" fill="url(#sh-orn)" stroke="${O}" stroke-width="1.4"/>`
    + `<rect x="525" y="66" width="10" height="208" fill="url(#sh-orn)" stroke="${O}" stroke-width="1.4"/>`
    + `<path d="${RR(17, 17, 506, 306, 6)}" fill="none" stroke="${O}" stroke-width="5"/>`
    + `<path d="${RR(17, 17, 506, 306, 6)}" fill="none" stroke="url(#sh-gold)" stroke-width="3"/>`
    + corner
    + `<g transform="translate(540 0) scale(-1 1)">${corner}</g><g transform="translate(0 340) scale(1 -1)">${corner}</g><g transform="translate(540 340) scale(-1 -1)">${corner}</g>`
    + `<path d="M232 0H308L300 16Q270 24 240 16Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.4" stroke-linejoin="round"/>`
    + `<path d="M270 3L275 9L270 15L265 9Z" fill="#e8304d" stroke="${O}" stroke-width="1.4"/>`
    + `<path d="M232 340H308L300 324Q270 316 240 324Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.4" stroke-linejoin="round"/>`
    + `<path d="M270 337L275 331L270 325L265 331Z" fill="#3c80ec" stroke="${O}" stroke-width="1.4"/>`,
    '0 0 540 340', ' preserveAspectRatio="none"');
  const FRAME_BACK = S('sh-frameback-svg',
    `<rect x="20" y="20" width="500" height="300" fill="url(#sh-panel)"/>`
    + `<path d="M120 20V320M220 20V320M320 20V320M420 20V320" stroke="#000" stroke-opacity=".45" stroke-width="3"/>`
    + `<path d="M122 20V320M222 20V320M322 20V320M422 20V320" stroke="#c9a6ff" stroke-opacity=".08" stroke-width="1.4"/>`
    + `<path d="M20 120H520M20 220H520" stroke="#fff" stroke-opacity=".03" stroke-width="1"/>`,
    '0 0 540 340', ' preserveAspectRatio="none"');

  /* ---------- сцена: степ уночі ---------- */
  function campfire(x, y) {
    let h = `<ellipse class="a-fglow" cx="${x}" cy="${y - 10}" rx="420" ry="150" fill="url(#sh-fglow)"/>`;
    // триніжок і казанок
    h += `<path d="M${x - 78} ${y + 14}L${x} ${y - 168}L${x + 78} ${y + 14}M${x} ${y - 168}L${x + 18} ${y + 18}" stroke="#1a0e0a" stroke-width="7" stroke-linecap="round" fill="none"/>`
      + `<path d="M${x - 74} ${y + 6}L${x - 2} ${y - 160}" stroke="#d28a52" stroke-width="2" opacity=".5"/>`
      + `<path d="M${x} ${y - 166}V${y - 112}" stroke="#2a2030" stroke-width="3"/>`
      + `<path d="M${x - 34} ${y - 104}Q${x} ${y - 132} ${x + 34} ${y - 104}" stroke="#2a2030" stroke-width="3" fill="none"/>`;
    // каміння й дрова
    for (let i = 0; i < 9; i++) {
      const a = Math.PI * (i / 8), sx = f1(x - 74 * Math.cos(a)), sy = f1(y + 10 + 8 * Math.sin(a));
      h += `<ellipse cx="${sx}" cy="${sy}" rx="17" ry="11" fill="#3a3448" stroke="#120c18" stroke-width="3"/><path d="M${sx - 10} ${sy - 4}q10 -6 18 0" stroke="#ffb070" stroke-width="2" fill="none" opacity=".55"/>`;
    }
    h += `<path d="M${x - 62} ${y + 6}L${x + 50} ${y - 22}" stroke="#120804" stroke-width="20" stroke-linecap="round"/><path d="M${x - 62} ${y + 6}L${x + 50} ${y - 22}" stroke="#6a3a1a" stroke-width="14" stroke-linecap="round"/>`
      + `<path d="M${x + 62} ${y + 6}L${x - 50} ${y - 22}" stroke="#120804" stroke-width="20" stroke-linecap="round"/><path d="M${x + 62} ${y + 6}L${x - 50} ${y - 22}" stroke="#7a4420" stroke-width="14" stroke-linecap="round"/>`
      + `<ellipse cx="${x}" cy="${y - 10}" rx="34" ry="9" fill="#ffb03a" opacity=".9"/>`;
    // полум'я
    h += `<g transform="translate(${x} ${y - 6})">`
      + `<g class="a-fl1"><path d="M-48 0C-56 -40 -22 -52 -27 -92C-12 -72 -2 -64 2 -116C20 -84 30 -74 28 -52C38 -62 42 -72 38 -84C58 -52 54 -20 48 0Z" fill="#ff5a1a" opacity=".95"/></g>`
      + `<g class="a-fl2"><path d="M-32 0C-38 -30 -12 -40 -14 -70C-4 -54 4 -50 6 -84C18 -60 26 -50 22 -32C30 -40 32 -48 30 -56C42 -34 38 -14 32 0Z" fill="#ffc23a"/></g>`
      + `<path d="M-16 0C-18 -18 -4 -24 -2 -42C6 -28 14 -22 14 -10C16 -12 18 -16 18 -20C24 -8 20 -2 16 0Z" fill="#fff6c8"/>`
      + `</g>`;
    // казанок поверх вогню
    h += `<path d="M${x - 34} ${y - 104}Q${x - 38} ${y - 66} ${x} ${y - 62}Q${x + 38} ${y - 66} ${x + 34} ${y - 104}Z" fill="#241c2c" stroke="#0e0a12" stroke-width="3"/>`
      + `<ellipse cx="${x}" cy="${y - 104}" rx="36" ry="7" fill="#3a3046" stroke="#0e0a12" stroke-width="3"/>`
      + `<path d="M${x - 26} ${y - 72}Q${x} ${y - 64} ${x + 26} ${y - 72}" stroke="#ff9a3a" stroke-width="3" fill="none" opacity=".7"/>`;
    // іскри й дим
    h += `<circle class="a-sp1" cx="${x - 16}" cy="${y - 120}" r="3.4" fill="#ffe27a"/><circle class="a-sp2" cx="${x + 20}" cy="${y - 130}" r="2.8" fill="#ffb03a"/><circle class="a-sp3" cx="${x + 2}" cy="${y - 140}" r="2.4" fill="#fff3b0"/>`
      + `<g class="a-smoke"><ellipse cx="${x + 4}" cy="${y - 150}" rx="30" ry="16" fill="#8a86a8" opacity=".22"/><ellipse cx="${x + 16}" cy="${y - 182}" rx="38" ry="18" fill="#8a86a8" opacity=".15"/><ellipse cx="${x + 34}" cy="${y - 218}" rx="44" ry="20" fill="#8a86a8" opacity=".09"/></g>`;
    return h;
  }
  function tuft(x, y, s, col, op, lean) {
    let d = '';
    const L = lean == null ? 1 : lean;
    [[-10, 54], [-4, 70], [2, 62], [8, 76], [14, 50], [-14, 40]].forEach(([dx, hh]) => {
      const x0 = f1(x + dx * s * .3);
      d += `M${x0} ${y}Q${f1(x0 + (dx * .2 + 3 * L) * s)} ${f1(y - hh * s * .95)} ${f1(x0 + (dx * .5 + hh * .36 * L) * s)} ${f1(y - hh * s * .82)}`;
    });
    return `<path d="${d}" stroke="${col}" stroke-width="${f1(2.2 * s)}" fill="none" stroke-linecap="round" opacity="${op}"/>`;
  }
  function scene(bonus) {
    const R = rng(20261009);
    let h = `<rect width="1600" height="1000" fill="url(#${bonus ? 'sh-skyb' : 'sh-sky'})"/>`;
    h += `<ellipse cx="800" cy="250" rx="820" ry="110" transform="rotate(-12 800 250)" fill="url(#sh-milky)"/>`;
    // зорі
    let stars = '', tw1 = '', tw2 = '';
    for (let i = 0; i < 150; i++) {
      const x = f1(R() * 1600), y = f1(Math.pow(R(), 1.4) * 640), r = f1((.7 + R() * 1.6) * (bonus ? 1.35 : 1)), o = f1(.45 + R() * .55);
      const c = `<circle cx="${x}" cy="${y}" r="${r}" fill="${R() < .2 ? '#ffe8b0' : '#fff'}" opacity="${o}"/>`;
      if (i % 11 === 0) tw1 += c; else if (i % 13 === 0) tw2 += c; else stars += c;
    }
    h += stars;
    const big = [[620, 90, 9], [1040, 300, 7], [740, 380, 6], [300, 160, 8], [1400, 120, 8], [880, 60, 6]];
    big.forEach(([x, y, r], i) => { const s = star4(x, y, bonus ? r * 1.5 : r, '#fff') + `<circle cx="${x}" cy="${y}" r="${bonus ? 3 : 2}" fill="#fff"/>`; if (i % 2) tw1 += s; else tw2 += s; });
    h += `<g class="a-tw1">${tw1}</g><g class="a-tw2">${tw2}</g>`;
    // місяць
    h += `<circle cx="960" cy="150" r="${bonus ? 110 : 150}" fill="url(#sh-moon)" opacity="${bonus ? .5 : 1}"/>`
      + `<path d="M960 98A52 52 0 1 0 1004 178A42 42 0 1 1 960 98Z" fill="#fff4d0" opacity="${bonus ? .7 : 1}"/>`
      + `<circle cx="935" cy="160" r="6" fill="#e8d8a8" opacity=".6"/><circle cx="948" cy="186" r="4" fill="#e8d8a8" opacity=".6"/>`;
    // далекі пагорби й кургани
    h += `<path d="M0 640Q120 600 260 612Q420 540 560 600Q700 620 820 610Q980 590 1100 620Q1260 560 1420 600Q1520 615 1600 600V760H0Z" fill="url(#sh-hill2)"/>`
      + `<path d="M120 690Q220 600 330 560Q380 548 430 560Q560 600 660 690Z" fill="url(#sh-hill)"/>`
      + `<path d="M330 560Q380 548 430 560" stroke="#8a8ad8" stroke-width="3" fill="none" opacity=".5"/>`
      + `<path d="M368 556V530Q368 516 380 512Q372 506 374 496Q376 484 388 484Q400 484 402 496Q404 506 396 512Q408 516 408 530V556Z" fill="#141432" stroke="#6a6ab8" stroke-width="2" stroke-opacity=".6"/>`
      + `<path d="M1120 690Q1260 620 1380 632Q1500 650 1600 690Z" fill="url(#sh-hill)"/>`
      // вітряк на дальньому пагорбі
      + `<g fill="#141432" stroke="#6a6ab8" stroke-opacity=".5" stroke-width="2"><path d="M1352 640L1360 578H1396L1404 640Z"/><path d="M1354 580L1378 556L1402 580Z"/>`
      + `<path d="M1378 572L1336 520L1330 526L1372 578ZM1378 572L1430 530L1436 537L1384 579ZM1378 572L1420 626L1414 632L1372 578ZM1378 572L1326 614L1320 607L1372 566Z"/></g>`
      + `<rect x="1373" y="604" width="10" height="14" fill="#ffc23a" opacity=".8"/>`;
    // степ
    h += `<path d="M0 668Q300 650 600 664Q900 678 1200 660Q1420 650 1600 662V1000H0Z" fill="url(#sh-${bonus ? 'groundb' : 'ground'})"/>`;
    let grass = '';
    const G = rng(77);
    for (let i = 0; i < 46; i++) {
      const x = f1(G() * 1600), y = f1(690 + Math.pow(G(), .8) * 290), s = f1(.6 + (y - 680) / 300 * 1.4);
      grass += tuft(x, y, s, bonus ? '#6a76a0' : '#9aaad4', f1(.12 + G() * .2), .6 + G() * .8);
    }
    h += grass;
    h += `<g class="a-sway1">` + tuft(560, 960, 2.6, '#c8d4f0', .32, 1) + tuft(1110, 955, 2.4, '#c8d4f0', .3, 1.2) + `</g>`
      + `<g class="a-sway2">` + tuft(480, 990, 3, '#b8c6ea', .3, .8) + tuft(1180, 990, 2.8, '#b8c6ea', .28, 1) + tuft(240, 940, 2.4, '#b8c6ea', .25, 1) + tuft(1380, 950, 2.4, '#b8c6ea', .25, .9) + `</g>`;
    // золоте сяйво бонусу
    if (bonus) h += `<ellipse class="a-cglow" cx="800" cy="880" rx="640" ry="460" fill="url(#sh-cglow)"/>`
      + `<g class="a-crays">` + rays(800, 880, 1000, 11, 'url(#sh-crays)', .9, -Math.PI * .95, -Math.PI * .05) + `</g>`;
    // вогнище
    h += campfire(bonus ? 600 : 680, 900);
    // кінь
    h += `<g transform="translate(1062 692) scale(-2.4 2.4)" opacity="${bonus ? .85 : 1}">${shadow(48, 93.5, 34, 3.5)}${horseInner({ body: 'url(#sh-horsen)', far: '#1a1222', mane: '#0c0810', ol: '#0a0610' })}</g>`;
    if (bonus) h += `<g transform="translate(686 804) scale(1.14)">${chestInner(true)}</g>`;
    // передній план
    h += `<path d="M0 1000V930Q60 900 120 940Q200 880 260 950Q330 910 380 960Q420 940 460 1000Z" fill="#05070f" opacity=".85"/>`
      + `<path d="M1600 1000V920Q1540 890 1480 935Q1400 880 1340 945Q1270 905 1220 960Q1180 940 1140 1000Z" fill="#05070f" opacity=".85"/>`;
    return S('sh-scene sh-scene-' + (bonus ? 'bonus' : 'base'), h, '0 0 1600 1000', ' preserveAspectRatio="xMidYMid slice"');
  }

  /* ---------- логотип ---------- */
  function logoInner() {
    const big = (dx, dy, fill, extra) => `<text x="${260 + dx}" y="${196 + dy}" text-anchor="middle" font-size="128" letter-spacing="2" ${FONT} fill="${fill}"${extra || ''}>СКАРБ</text>`;
    return star4(64, 120, 12, '#fff3c0', .9)
      // шабля під написом
      + `<g transform="translate(0 4)">`
      + `<path d="M118 206Q300 226 506 186Q320 236 120 222Z" fill="url(#sh-steel)" stroke="${O}" stroke-width="5" stroke-linejoin="round"/>`
      + `<path d="M130 214Q310 230 490 192" stroke="#fff" stroke-width="2.4" fill="none" opacity=".9"/>`
      + `<path d="M108 194L118 236" stroke="${O}" stroke-width="11" stroke-linecap="round"/><path d="M108 194L118 236" stroke="#f3b52c" stroke-width="6.5" stroke-linecap="round"/>`
      + `<path d="M108 214L44 210" stroke="${O}" stroke-width="13" stroke-linecap="round"/><path d="M108 214L44 210" stroke="#7a1a14" stroke-width="8.5" stroke-linecap="round"/><path d="M104 214L48 210.4" stroke="${GOLD}" stroke-width="8.5" stroke-dasharray="1.6 3.4"/>`
      + `<path d="M48 202Q30 204 26 222Q38 218 48 220Z" fill="url(#sh-gold)" stroke="${O}" stroke-width="3.5" stroke-linejoin="round"/>`
      + `<circle cx="108" cy="194" r="5" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.6"/><circle cx="118" cy="236" r="5" fill="url(#sh-gold)" stroke="${O}" stroke-width="2.6"/><circle cx="111" cy="214" r="5.6" fill="#e8304d" stroke="${O}" stroke-width="2.4"/>`
      + `</g>`
      // стрічка
      + `<path d="M120 31L66 34L84 54L68 76L124 72Z" fill="${RED_D}" stroke="${O}" stroke-width="4.5" stroke-linejoin="round"/>`
      + `<path d="M400 31L454 34L436 54L452 76L396 72Z" fill="${RED_D}" stroke="${O}" stroke-width="4.5" stroke-linejoin="round"/>`
      + `<path d="M106 24Q260 4 414 24L408 74Q260 56 112 74Z" fill="url(#sh-red)" stroke="${O}" stroke-width="4.5" stroke-linejoin="round"/>`
      + `<path d="M112 32Q260 13 408 32M113 66Q260 48 407 66" stroke="${GOLD}" stroke-width="2.6" fill="none"/>`
      + `<text x="260" y="58" text-anchor="middle" font-size="35" letter-spacing="3" ${FONT} fill="${CREAM}" stroke="${O}" stroke-width="6" stroke-linejoin="round" paint-order="stroke">КОЗАЦЬКИЙ</text>`
      // СКАРБ: тінь, об'єм, золото, блік
      + big(0, 11, O) + big(0, 6, '#8a4a08', ` stroke="${O}" stroke-width="10" stroke-linejoin="round" paint-order="stroke"`)
      + big(0, 0, 'url(#sh-goldtxt)', ` stroke="${O}" stroke-width="9" stroke-linejoin="round" paint-order="stroke"`)
      + big(0, 0, 'url(#sh-gloss)')
      + star4(432, 104, 15, '#fff') + star4(118, 192, 9, '#fff') + star4(452, 200, 8, '#fff3c0');
  }
  const LOGO = S('sh-logo', logoInner(), '0 0 520 250');

  /* ---------- афіша 360×240 ---------- */
  function poster() {
    const R = rng(5);
    let h = `<rect width="360" height="240" fill="url(#sh-skyb)"/>`;
    for (let i = 0; i < 60; i++) h += `<circle cx="${f1(R() * 360)}" cy="${f1(R() * 170)}" r="${f1(.4 + R() * 1.1)}" fill="#fff" opacity="${f1(.4 + R() * .6)}"/>`;
    h += `<ellipse cx="262" cy="170" rx="230" ry="180" fill="url(#sh-cglow)"/>`
      + rays(262, 165, 300, 13, '#ffe680', .22, -Math.PI * .98, -Math.PI * .02)
      + `<path d="M0 200Q90 188 180 196Q270 204 360 192V240H0Z" fill="#0b0d1c"/>`
      + tuft(30, 236, 1.2, '#7a86b0', .5, 1) + tuft(330, 236, 1.1, '#7a86b0', .5, -.6);
    // козак
    h += `<g transform="translate(-4 80) scale(1.62)">${cossackInner()}</g>`;
    // скриня з дукатами
    h += `<g transform="translate(166 92) scale(.94)">${chestInner(true)}</g>`;
    // дукати летять
    const fc = (x, y, r, rot) => `<g transform="translate(${x} ${y}) rotate(${rot})"><ellipse rx="${r}" ry="${f1(r * .62)}" fill="#9a5a06" stroke="${O}" stroke-width="1.6" transform="translate(0 ${f1(r * .14)})"/><ellipse rx="${r}" ry="${f1(r * .62)}" fill="url(#sh-gold-r)" stroke="${O}" stroke-width="1.6"/><ellipse rx="${f1(r * .6)}" ry="${f1(r * .36)}" fill="none" stroke="#b56a0a" stroke-width="1"/><ellipse cx="${f1(-r * .35)}" cy="${f1(-r * .2)}" rx="${f1(r * .22)}" ry="${f1(r * .12)}" fill="#fff" opacity=".85"/></g>`;
    h += fc(196, 118, 9, -20) + fc(226, 98, 7, 25) + fc(318, 108, 8, 15) + fc(296, 84, 6, -30) + fc(170, 150, 7, 40) + fc(342, 140, 7, -10);
    h += star4(214, 132, 6) + star4(330, 96, 5) + star4(250, 112, 7);
    // логотип
    h += `<g transform="translate(148 2) scale(.405)">${logoInner()}</g>`;
    return S('sh-poster', h, '0 0 360 240');
  }

  window.SlotArt = window.SlotArt || {};
  window.SlotArt['slot-hold'] = {
    title: 'Козацький скарб',
    symbols: {
      s1: { name: 'Вишите серце (червоне)', tier: 'low', svg: S1 },
      s2: { name: 'Вишита вина (чорна)', tier: 'low', svg: S2 },
      s3: { name: 'Вишитий ромб (синій)', tier: 'low', svg: S3 },
      s4: { name: 'Вишита трефа (зелена)', tier: 'low', svg: S4 },
      pipe: { name: 'Люлька', tier: 'high', svg: PIPE },
      mug: { name: 'Кухоль', tier: 'high', svg: MUG },
      sabre: { name: 'Шабля', tier: 'high', svg: SABRE },
      horse: { name: 'Кінь', tier: 'high', svg: HORSE },
      cossack: { name: 'Козак', tier: 'high', svg: COSSACK },
      wild: { name: 'Бунчук', tier: 'wild', svg: WILD },
      coin: { name: 'Дукат', tier: 'special', svg: coin() },
      mace: { name: 'Булава', tier: 'special', svg: mace() },
      pirnach: { name: 'Пірнач', tier: 'special', svg: weaponCoin('sapph', '', true) },
    },
    scene: { base: scene(false), bonus: scene(true) },
    logo: LOGO,
    poster: poster(),
    extras: {
      defs: DEFS,
      coin: coin,            // (label) '×1'…'×25', 'Міні', 'Мажор' (і 'Гетьман') → svg
      mace: mace,            // (label) → булава на смарагдовій монеті, число на табличці; без label — лише булава
      pirnach: pirnach,      // (label='×2') → пірнач на сапфіровій монеті
      empty: EMPTY,          // порожнє гніздо під час «Утримуй і вигравай»
      respins: respins,      // (n 0..3) → три свічки, горять n (класи .cnd.lit; .reset на предку — спалах)
      chest: CHEST,          // скриня: .lid / .body / .glow (+ .lid-in, .in-light); відчиняється під .open
      jackpots: JACKPOTS,    // .jp-v-mini / .jp-v-major / .jp-v-grand — тексти сум
      frame: FRAME,          // рамка 5×3, див. коментар над FRAME
      frameBack: FRAME_BACK, // панель під символи з роздільниками барабанів
    },
  };
})();
