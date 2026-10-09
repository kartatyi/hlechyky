/*
  Лелека — crash на всіх (Азарт → 📈 Швидкі). Стіл веде Дядько Глек за розкладом: ставки → політ → «шубовсть» →
  пауза. Правила, гроші й точка падіння — на сервері (Impl/Lelka.cs, docs/games/specs/lelka.md); тут сцена й наміри.

  Кадр (frame, ~10/с) і вид (room): { phase: 'bets'|'flight'|'crash'|'pause', round, now, until, startAt, k, m,
    crash, hash, seed, bets: [{ nick, amount, auto, out, win }], history: [{ round, crash }] }
  Вид ще: mine: { amount, auto, out, win } | null, wallet, limits: { min, max }.
  Наміри: act('bet', { amount, auto }), act('cancel'), act('cash'), act('auto', { x }).

  Множник між кадрами — локально: m = e^(k·t), t від startAt за годинником сервера (поправка з `now`).
  rAF — лише в польоті; у спокої сцена жива самими CSS-анімаціями (≤ 12). Звук — WebAudio-синт, за замовчуванням
  вимкнений (кнопка 🔈 на сцені).
*/
(() => {
  'use strict';

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M1.6 14.2C6 13.6 9.6 10.6 12.2 4.6" fill="none" stroke="var(--accent)" stroke-width="1.7" stroke-linecap="round"/>'
    + '<path d="M10.6 5.2l2.8-2.6 1.4 1.2" fill="none" stroke="var(--clay)" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"/>'
    + '<path d="M11.4 7.4h2.2l.4 1.2c1 .5 1.3 1.6 1 2.6-.4 1.3-1.4 1.9-2.5 1.9s-2.1-.6-2.5-1.9c-.3-1 0-2.1 1-2.6z" fill="var(--clay)"/></svg>';

  const INK = '#24142e';
  const PH = { bets: 0, flight: 1, crash: 2, pause: 3 };
  const K_DEF = 0.075;
  const QUICK = [10, 50, 100, 500];
  const AUTOQ = [1.5, 2, 3, 10];
  const MILES = [[2, '×2!'], [5, '×5!'], [10, '×10 — оце летить!'], [50, '×50 — лелека в космосі!'],
    [100, '×100!!'], [200, '×200 — повз Місяць!'], [500, '×500 — куди ти, лелеко?!']];
  let uid = 0;

  // ---------------------------------------------------------------------------------------------
  // Дрібниці
  // ---------------------------------------------------------------------------------------------
  const r1 = (n) => Math.round(n * 10) / 10;
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const tms = (x) => (typeof x === 'number' ? x : x ? Date.parse(x) : NaN);
  const fmtM = (m) => (m >= 1000 ? Math.floor(m).toString() : (Math.floor(m * 100) / 100).toFixed(2)).replace('.', ',');
  const fmtN = (n) => Math.round(n).toString().replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  const parseX = (s) => { const x = parseFloat(String(s).replace(',', '.').replace(/[^\d.]/g, '')); return isFinite(x) ? x : NaN; };
  const store = {
    get(k, d) { try { const v = localStorage.getItem('lelka_' + k); return v == null ? d : JSON.parse(v); } catch (e) { return d; } },
    set(k, v) { try { localStorage.setItem('lelka_' + k, JSON.stringify(v)); } catch (e) { /* приватне вікно */ } },
  };
  function hex2rgb(h) { const n = parseInt(h.slice(1), 16); return [n >> 16, (n >> 8) & 255, n & 255]; }
  function mix(stops, x) {   // stops: [[x, '#rrggbb'], …] за зростанням x
    if (x <= stops[0][0]) return stops[0][1];
    for (let i = 1; i < stops.length; i++) {
      if (x <= stops[i][0]) {
        const a = hex2rgb(stops[i - 1][1]), b = hex2rgb(stops[i][1]);
        const t = (x - stops[i - 1][0]) / (stops[i][0] - stops[i - 1][0]);
        return 'rgb(' + a.map((v, j) => Math.round(v + (b[j] - v) * t)).join(',') + ')';
      }
    }
    return stops[stops.length - 1][1];
  }
  // колір множника за ln(m): білий → золотий → помаранчевий → червоний
  const M_COL = [[0, '#ffffff'], [Math.log(2), '#ffe27a'], [Math.log(5), '#ffb02e'], [Math.log(20), '#ff6a2a'], [Math.log(100), '#ff3048']];
  const SKY_TOP = [[0, '#3a2466'], [0.9, '#2b2a72'], [1.8, '#16194d'], [2.8, '#090b26'], [3.8, '#040311']];
  const SKY_BOT = [[0, '#ff9550'], [0.9, '#d66c8c'], [1.8, '#40407e'], [2.8, '#1b1d55'], [3.8, '#120a2c']];

  // ---------------------------------------------------------------------------------------------
  // Арт (SVG). Координати сцени = CSS-пікселі сцени (viewBox = W×H), тож оверлеї кладуться тими ж числами.
  // ---------------------------------------------------------------------------------------------
  function rng(seed) { let s = seed % 2147483647 || 1; return () => ((s = s * 16807 % 2147483647) - 1) / 2147483646; }

  function defs(p) {
    const lg = (id, x2, y2, st) => '<linearGradient id="' + p + id + '" x1="0" y1="0" x2="' + x2 + '" y2="' + y2 + '">'
      + st.map((s) => '<stop offset="' + s[0] + '" stop-color="' + s[1] + '"' + (s[2] != null ? ' stop-opacity="' + s[2] + '"' : '') + '/>').join('') + '</linearGradient>';
    const rg = (id, st, fx, fy) => '<radialGradient id="' + p + id + '"' + (fx != null ? ' fx="' + fx + '" fy="' + fy + '"' : '') + '>'
      + st.map((s) => '<stop offset="' + s[0] + '" stop-color="' + s[1] + '"' + (s[2] != null ? ' stop-opacity="' + s[2] + '"' : '') + '/>').join('') + '</radialGradient>';
    return '<defs>'
      + '<linearGradient id="' + p + 'sky" x1="0" y1="0" x2="0" y2="1"><stop class="lk-s0" offset="0" stop-color="' + SKY_TOP[0][1] + '"/><stop class="lk-s1" offset="1" stop-color="' + SKY_BOT[0][1] + '"/></linearGradient>'
      + rg('sun', [[0, '#fff7c2'], [0.45, '#ffd25a'], [1, '#ff8a3a']])
      + rg('glow', [[0, '#ffc46a', 0.55], [1, '#ff8a3a', 0]])
      + lg('hill1', 0, 1, [[0, '#7a3f74'], [1, '#43264f']])
      + lg('hill2', 0, 1, [[0, '#3b2246'], [1, '#1c1226']])
      + lg('wall', 0, 1, [[0, '#ffe6cc'], [1, '#d9b49a']])
      + lg('roof', 0, 1, [[0, '#e3b25a'], [1, '#9c6a2c']])
      + rg('win', [[0, '#fff2b0'], [0.6, '#ffc94d'], [1, '#f08a2a']])
      + rg('wglow', [[0, '#ffcf5a', 0.5], [1, '#ffcf5a', 0]])
      + rg('clay', [[0, '#ffb98a'], [0.35, '#e8773d'], [0.8, '#a9461f'], [1, '#6b2812']], '.32', '.3')
      + lg('body', 0, 1, [[0, '#ffffff'], [0.7, '#eef1f7'], [1, '#c9cfdc']])
      + lg('wing', 1, 0, [[0, '#ffffff'], [0.5, '#e9edf5'], [0.56, '#2a2236'], [1, '#120c1c']])
      + lg('trail', 0, 1, [[0, '#ffd75a', 0.38], [1, '#ffd75a', 0]])
      + rg('moon', [[0, '#fffbe8'], [0.7, '#e8e2c8'], [1, '#b9b294']], '.38', '.35')
      + rg('planet', [[0, '#9ff0e0'], [0.5, '#3aa6b8'], [1, '#1b3f6e']], '.35', '.3')
      + rg('cloud', [[0, '#fff3f6'], [1, '#e7b9cf']], '.4', '.3')
      + '</defs>';
  }

  // Глек: точка підвісу (0,0) — мотузок; тіло нижче, висота ~58.
  function potArt(p) {
    return '<path d="M0 0V8" stroke="#6b4a2a" stroke-width="2.2"/>'
      + '<path d="M11 15C25 13 26 33 13 34" fill="none" stroke="' + INK + '" stroke-width="7" stroke-linecap="round"/>'
      + '<path d="M11 15C25 13 26 33 13 34" fill="none" stroke="#c45a2a" stroke-width="3.6" stroke-linecap="round"/>'
      + '<path d="M-7 9H7L8 15C20 19 24 31 22 41C20 53 10 59 0 59C-10 59-20 53-22 41C-24 31-20 19-8 15Z" fill="url(#' + p + 'clay)" stroke="' + INK + '" stroke-width="2.6" stroke-linejoin="round"/>'
      + '<rect x="-10" y="6" width="20" height="5.5" rx="2.4" fill="#e07a3e" stroke="' + INK + '" stroke-width="2"/>'
      + '<path d="M-21.5 37C-10 41.5 10 41.5 21.5 37" stroke="#fff0d2" stroke-width="4.4" fill="none"/>'
      + '<circle cx="-12" cy="39.6" r="1.6" fill="#e3122a"/><circle cx="0" cy="41" r="1.8" fill="#e3122a"/><circle cx="12" cy="39.6" r="1.6" fill="#e3122a"/>'
      + '<circle cx="-6" cy="40.6" r="1.1" fill="#3f9a3a"/><circle cx="6" cy="40.6" r="1.1" fill="#3f9a3a"/>'
      + '<ellipse cx="-6" cy="25" rx="3.6" ry="4" fill="#fff"/><ellipse cx="6" cy="25" rx="3.6" ry="4" fill="#fff"/>'
      + '<circle cx="-5.2" cy="25.8" r="1.8" fill="' + INK + '"/><circle cx="6.8" cy="25.8" r="1.8" fill="' + INK + '"/>'
      + '<path d="M0 30.5C-3 29-8 29-11 32.5C-7 32-4 33 0 32C4 33 7 32 11 32.5C8 29 3 29 0 30.5Z" fill="#3a1a0c"/>'
      + '<ellipse cx="-12" cy="27" rx="2.6" ry="6.5" fill="#fff" opacity=".42" transform="rotate(18 -12 27)"/>';
  }

  // Лелека дивиться праворуч; (0,0) — середина тулуба; дзьоб тримає мотузок у точці BEAK.
  const BEAK = [94, -11];
  function storkArt(p) {
    const wing = (cls, d, w) => '<g class="' + cls + '"><path d="' + d + '" fill="url(#' + p + 'wing)" stroke="' + INK + '" stroke-width="' + w + '" stroke-linejoin="round"/>'
      + '<path d="M2 -14C10 -30 22 -42 36 -50" fill="none" stroke="#c9cfdc" stroke-width="1.6" opacity=".8"/></g>';
    return '<g class="lk-legs"><path d="M-28 8L-82 15M-28 12L-80 22" stroke="' + INK + '" stroke-width="6.4" stroke-linecap="round"/>'
      + '<path d="M-28 8L-82 15M-28 12L-80 22" stroke="#ef5a2c" stroke-width="3.4" stroke-linecap="round"/></g>'
      + wing('lk-w2', 'M-6 -8C-12 -44 14 -70 46 -66C40 -54 34 -40 26 -30C16 -18 8 -10 6 -6Z', 2.6)
      + '<path d="M-36 -4L-60 -12L-58 8L-36 6Z" fill="#1d1628" stroke="' + INK + '" stroke-width="2.4" stroke-linejoin="round"/>'
      + '<ellipse cx="0" cy="0" rx="40" ry="17" fill="url(#' + p + 'body)" stroke="' + INK + '" stroke-width="3"/>'
      + '<path d="M28 -6C40 -14 50 -24 64 -20" fill="none" stroke="' + INK + '" stroke-width="15" stroke-linecap="round"/>'
      + '<path d="M28 -6C40 -14 50 -24 64 -20" fill="none" stroke="#fff" stroke-width="9.6" stroke-linecap="round"/>'
      + '<path d="M68 -24L104 -12L68 -14Z" fill="#ff6a33" stroke="' + INK + '" stroke-width="2.6" stroke-linejoin="round"/>'
      + '<path d="M70 -19L100 -12.6" stroke="#b8361a" stroke-width="1.4"/>'
      + '<circle cx="64" cy="-20" r="10" fill="#fff" stroke="' + INK + '" stroke-width="2.8"/>'
      + '<circle class="lk-eye" cx="67" cy="-22" r="2.6" fill="' + INK + '"/><circle cx="67.8" cy="-22.8" r=".9" fill="#fff"/>'
      + '<path d="M60 -26C62 -28 66 -28.6 69 -27.6" stroke="#ef5a2c" stroke-width="1.6" fill="none" stroke-linecap="round"/>'
      + wing('lk-w1', 'M-10 -6C-20 -46 6 -80 44 -80C38 -64 32 -48 22 -34C12 -20 4 -10 2 -4Z', 3);
  }

  function cloud(x, y, s, o) {
    return '<g transform="translate(' + r1(x) + ' ' + r1(y) + ') scale(' + r1(s * 100) / 100 + ')" opacity="' + o + '">'
      + '<ellipse cx="0" cy="10" rx="70" ry="16" fill="#c792b8"/>'
      + '<circle cx="-34" cy="0" r="22" fill="url(#P)"/><circle cx="-4" cy="-12" r="30" fill="url(#P)"/>'
      + '<circle cx="30" cy="-2" r="24" fill="url(#P)"/><circle cx="52" cy="6" r="15" fill="url(#P)"/>'
      + '<rect x="-56" y="0" width="112" height="16" rx="8" fill="url(#P)"/></g>';
  }

  function hata(x, gy, s, smoke) {
    let h = '<g transform="translate(' + r1(x) + ' ' + r1(gy) + ') scale(' + r1(s * 100) / 100 + ')">'
      + '<circle cx="32" cy="-30" r="30" fill="url(#Pwglow)"/><circle cx="80" cy="-30" r="30" fill="url(#Pwglow)"/>'
      + '<rect x="6" y="-56" width="98" height="56" rx="4" fill="url(#Pwall)" stroke="' + INK + '" stroke-width="3"/>'
      + '<path d="M6 -6H104" stroke="#a56a4a" stroke-width="5" opacity=".5"/>'
      + '<rect x="20" y="-42" width="22" height="20" rx="2" fill="url(#Pwin)" stroke="' + INK + '" stroke-width="2.4"/>'
      + '<path d="M31 -42V-22M20 -32H42" stroke="' + INK + '" stroke-width="1.6"/>'
      + '<rect x="12" y="-43" width="7" height="22" fill="#3a6fb0" stroke="' + INK + '" stroke-width="1.6"/><rect x="43" y="-43" width="7" height="22" fill="#3a6fb0" stroke="' + INK + '" stroke-width="1.6"/>'
      + '<rect x="70" y="-42" width="22" height="20" rx="2" fill="url(#Pwin)" stroke="' + INK + '" stroke-width="2.4"/>'
      + '<path d="M81 -42V-22M70 -32H92" stroke="' + INK + '" stroke-width="1.6"/>'
      + '<rect x="53" y="-36" width="13" height="36" rx="2" fill="#7a4524" stroke="' + INK + '" stroke-width="2.2"/>'
      + '<path d="M-8 -50L24 -100H86L118 -50Z" fill="url(#Proof)" stroke="' + INK + '" stroke-width="3" stroke-linejoin="round"/>'
      + '<path d="M4 -58L30 -96M22 -56L42 -96M42 -55L56 -96M64 -55L70 -96M86 -56L84 -96M104 -58L96 -94" stroke="#8a5a22" stroke-width="1.5" opacity=".7"/>'
      + '<path d="M-8 -50H118" stroke="' + INK + '" stroke-width="3"/>'
      + '<circle cx="18" cy="-48" r="2.4" fill="#e3122a"/><circle cx="55" cy="-48" r="2.6" fill="#f5c33b"/><circle cx="92" cy="-48" r="2.4" fill="#e3122a"/>'
      + '<rect x="74" y="-114" width="12" height="22" fill="#d9b49a" stroke="' + INK + '" stroke-width="2.4"/>';
    if (smoke) h += '<g class="lk-smoke"><circle cx="80" cy="-124" r="7" fill="#c8b6d6" opacity=".55"/><circle cx="86" cy="-140" r="9" fill="#c8b6d6" opacity=".4"/><circle cx="80" cy="-160" r="11" fill="#c8b6d6" opacity=".25"/></g>';
    return h + '</g>';
  }

  function sunflower(x, gy, s, h) {
    const t = gy - h * s;
    return '<path d="M' + r1(x) + ' ' + r1(gy) + 'V' + r1(t) + '" stroke="#2f5a26" stroke-width="' + r1(3 * s) + '"/>'
      + '<path d="M' + r1(x) + ' ' + r1(gy - h * s * .45) + 'q' + r1(9 * s) + ' ' + r1(-8 * s) + ' ' + r1(14 * s) + ' ' + r1(-2 * s) + 'q' + r1(-8 * s) + ' ' + r1(6 * s) + ' ' + r1(-14 * s) + ' ' + r1(2 * s) + 'z" fill="#3f8a32"/>'
      + '<circle cx="' + r1(x) + '" cy="' + r1(t) + '" r="' + r1(9 * s) + '" fill="none" stroke="#ffc927" stroke-width="' + r1(8 * s) + '" stroke-dasharray="' + r1(3.4 * s) + ' ' + r1(1.6 * s) + '"/>'
      + '<circle cx="' + r1(x) + '" cy="' + r1(t) + '" r="' + r1(6.5 * s) + '" fill="#5a2e14" stroke="' + INK + '" stroke-width="' + r1(1.4 * s) + '"/>';
  }

  function scene(g, p) {
    const { W, H, gy, sc, x0, y0 } = g;
    const R = rng(1234 + Math.round(W));
    const P = (s) => s.replace(/url\(#P/g, 'url(#' + p);
    let s = defs(p) + '<rect width="' + W + '" height="' + H + '" fill="url(#' + p + 'sky)"/>';
    // зорі (екранні, з'являються вище хмар) — 6 мерехтять
    s += '<g class="lk-stars" opacity="0">';
    for (let i = 0; i < 90; i++) s += '<circle cx="' + r1(R() * W) + '" cy="' + r1(R() * H) + '" r="' + r1(0.6 + R() * 1.5) + '" fill="#fff" opacity="' + r1(0.4 + R() * 0.6) + '"/>';
    for (let i = 0; i < 6; i++) {
      const x = r1(R() * W), y = r1(R() * H * 0.8), r = r1(4 + R() * 4);
      s += '<path class="lk-tw" style="animation-delay:-' + r1(R() * 3) + 's" transform="translate(' + x + ' ' + y + ')" d="M0 -' + r + 'Q0 0 ' + r + ' 0Q0 0 0 ' + r + 'Q0 0 -' + r + ' 0Q0 0 0 -' + r + 'Z" fill="#fff"/>';
    }
    s += '</g>';
    // космос: супутник, планета, комета, Місяць — на своїх «висотах» (alt = ln m)
    const S6 = g.S * 0.6, sy = (fa) => H * 0.45 - fa * S6;
    s += '<g class="lk-space" opacity="0">'
      + '<g transform="translate(' + r1(W * 0.82) + ' ' + r1(sy(3.5)) + ') scale(' + sc + ')"><path d="M-10 -6L-46 -30M-8 4L-50 6M6 8L30 40" stroke="#cfd6e4" stroke-width="2"/>'
      + '<circle r="14" fill="#d8dee9" stroke="' + INK + '" stroke-width="2.6"/><ellipse cx="-4" cy="-5" rx="5" ry="3.4" fill="#fff" opacity=".7"/></g>'
      + '<g transform="translate(' + r1(W * 0.16) + ' ' + r1(sy(4.1)) + ') scale(' + sc + ')">'
      + '<ellipse rx="92" ry="18" fill="none" stroke="#f5c33b" stroke-width="7" opacity=".55" transform="rotate(-14)"/>'
      + '<circle r="52" fill="url(#' + p + 'planet)" stroke="' + INK + '" stroke-width="3"/>'
      + '<path d="M-48 -10C-20 -2 20 -18 50 -6M-44 18C-14 26 18 12 46 20" stroke="#bff7ee" stroke-width="5" fill="none" opacity=".35"/>'
      + '<path d="M-88 8A92 18 0 0 0 88 -32" transform="rotate(-2)" fill="none" stroke="#f5c33b" stroke-width="7" opacity=".85"/></g>'
      + '<g transform="translate(' + r1(W * 0.55) + ' ' + r1(sy(4.7)) + ') scale(' + sc + ') rotate(-24)"><path d="M0 0L-160 -6L-160 6Z" fill="#9ff0e0" opacity=".35"/><circle r="9" fill="#e9fffb"/></g>'
      + '<g transform="translate(' + r1(W * 0.74) + ' ' + r1(sy(5.3)) + ') scale(' + r1(sc * 1.25 * 100) / 100 + ')">'
      + '<circle r="110" fill="#fffbe8" opacity=".08"/><circle r="84" fill="url(#' + p + 'moon)" stroke="' + INK + '" stroke-width="3"/>'
      + '<circle cx="-26" cy="-20" r="16" fill="#cfc7a6"/><circle cx="24" cy="18" r="22" fill="#d6cfb0"/><circle cx="-12" cy="40" r="10" fill="#cfc7a6"/><circle cx="36" cy="-34" r="8" fill="#cfc7a6"/></g>'
      + '</g>';
    // сонце
    s += '<g class="lk-sun"><circle cx="' + r1(W * 0.6) + '" cy="' + r1(gy - H * 0.12) + '" r="' + r1(H * 0.42) + '" fill="url(#' + p + 'glow)"/>'
      + '<circle cx="' + r1(W * 0.6) + '" cy="' + r1(gy - H * 0.12) + '" r="' + r1(H * 0.15) + '" fill="url(#' + p + 'sun)"/></g>';
    // хмари: дві копії поруч для безшовного зсуву
    let cl = '';
    const C = rng(77);
    for (let i = 0; i < 11; i++) {
      const fa = 0.3 + i * 0.22, x = C() * W, y = H * 0.45 - fa * g.S * 1.2 + (C() - 0.5) * H * 0.1;
      cl += cloud(x, y, sc * (0.7 + C() * 0.7), r1(0.75 + C() * 0.25));
    }
    cl = cl.replace(/url\(#P\)/g, 'url(#' + p + 'cloud)');
    s += '<g class="lk-clouds"><g>' + cl + '</g><g transform="translate(' + W + ' 0)">' + cl + '</g></g>';
    // далекі пагорби з церквою
    const hy = gy - H * 0.11;
    s += '<g class="lk-hills"><path d="M-40 ' + r1(hy) + 'Q' + r1(W * 0.15) + ' ' + r1(hy - H * 0.13) + ' ' + r1(W * 0.34) + ' ' + r1(hy - H * 0.02) + 'T' + r1(W * 0.7) + ' ' + r1(hy - H * 0.04) + 'T' + r1(W + 60) + ' ' + r1(hy - H * 0.08) + 'V' + r1(H * 1.6) + 'H-40Z" fill="url(#' + p + 'hill1)"/>';
    if (W > 640) {
      const cx = W * 0.86, cy = hy - H * 0.07, k = sc * 0.8;
      s += '<g transform="translate(' + r1(cx) + ' ' + r1(cy) + ') scale(' + r1(k * 100) / 100 + ')" fill="#2c1838">'
        + '<rect x="-30" y="-40" width="60" height="44"/><rect x="-14" y="-62" width="28" height="24"/>'
        + '<path d="M-16 -62C-16 -78 0 -80 0 -96C0 -80 16 -78 16 -62Z" fill="#f5c33b" stroke="' + INK + '" stroke-width="2"/>'
        + '<path d="M0 -96V-110M-5 -104H5" stroke="#f5c33b" stroke-width="2.4"/>'
        + '<path d="M-38 -40C-38 -50 -28 -52 -28 -60C-28 -52 -18 -50 -18 -40Z" fill="#f5c33b" stroke="' + INK + '" stroke-width="2"/>'
        + '<path d="M18 -40C18 -50 28 -52 28 -60C28 -52 38 -50 38 -40Z" fill="#f5c33b" stroke="' + INK + '" stroke-width="2"/></g>';
    }
    s += '</g>';
    // село: земля, хати, тин із глечиками, соняшники, гніздо на колесі
    let v = '<path d="M-60 ' + r1(gy - 10) + 'Q' + r1(W * 0.25) + ' ' + r1(gy - 26) + ' ' + r1(W * 0.5) + ' ' + r1(gy - 12) + 'T' + r1(W + 60) + ' ' + r1(gy - 14) + 'V' + r1(gy + H) + 'H-60Z" fill="url(#' + p + 'hill2)"/>';
    const hs = sc * 0.9, hw = 124 * hs;
    const n = Math.max(1, Math.floor((W - x0 - 120 * sc) / (hw * 1.9)));
    for (let i = 0; i < n; i++) {
      const hx = x0 + 90 * sc + (i + 0.4) * ((W - x0 - 90 * sc) / n) + (R() - 0.5) * hw * 0.3;
      v += hata(hx, gy - 6, hs * (0.85 + R() * 0.2), i === 0);
    }
    // тин
    const fy = gy + 4, fh = 26 * sc;
    let fence = '';
    for (let x = -10; x < W + 20; x += 34 * sc) fence += '<path d="M' + r1(x) + ' ' + r1(fy) + 'V' + r1(fy - fh - 6 * sc) + '" stroke="#4a2a1a" stroke-width="' + r1(3.4 * sc) + '"/>';
    for (let j = 0; j < 3; j++) {
      const yy = fy - fh * (0.25 + j * 0.3);
      fence += '<path d="M-10 ' + r1(yy) + 'Q' + r1(W * 0.25) + ' ' + r1(yy - 3) + ' ' + r1(W * 0.5) + ' ' + r1(yy) + 'T' + r1(W + 20) + ' ' + r1(yy) + '" stroke="#7a4a26" stroke-width="' + r1(3 * sc) + '" fill="none"/>';
    }
    for (let x = x0 + 150 * sc; x < W; x += 34 * sc * 5) {
      fence += '<g transform="translate(' + r1(x) + ' ' + r1(fy - fh - 6 * sc) + ') scale(' + r1(sc * 0.42 * 100) / 100 + ') rotate(180)">' + potArt(p) + '</g>';
    }
    v += '<g opacity=".95">' + fence + '</g>';
    for (let i = 0; i < Math.max(3, Math.floor(W / 140)); i++) v += sunflower(x0 + 60 * sc + R() * (W - x0), gy + 14 * sc + R() * 10 * sc, sc * (0.8 + R() * 0.4), 48 + R() * 20);
    // гніздо: стовп, колесо, гніздо
    v += '<rect x="' + r1(x0 - 4 * sc) + '" y="' + r1(y0) + '" width="' + r1(8 * sc) + '" height="' + r1(gy - y0 + 20) + '" fill="#5a341c" stroke="' + INK + '" stroke-width="' + r1(2 * sc) + '"/>'
      + '<ellipse cx="' + r1(x0) + '" cy="' + r1(y0 + 2 * sc) + '" rx="' + r1(46 * sc) + '" ry="' + r1(7 * sc) + '" fill="#6b4224" stroke="' + INK + '" stroke-width="' + r1(2.4 * sc) + '"/>'
      + '<path d="M' + r1(x0 - 50 * sc) + ' ' + r1(y0 - 4 * sc) + 'Q' + r1(x0) + ' ' + r1(y0 + 14 * sc) + ' ' + r1(x0 + 50 * sc) + ' ' + r1(y0 - 4 * sc) + 'L' + r1(x0 + 44 * sc) + ' ' + r1(y0 - 14 * sc) + 'Q' + r1(x0) + ' ' + r1(y0 - 2 * sc) + ' ' + r1(x0 - 44 * sc) + ' ' + r1(y0 - 14 * sc) + 'Z" fill="#8a5a2c" stroke="' + INK + '" stroke-width="' + r1(2.6 * sc) + '"/>'
      + '<path d="M' + r1(x0 - 42 * sc) + ' ' + r1(y0 - 8 * sc) + 'l' + r1(20 * sc) + ' ' + r1(6 * sc) + 'M' + r1(x0 - 10 * sc) + ' ' + r1(y0 - 4 * sc) + 'l' + r1(26 * sc) + ' ' + r1(-4 * sc) + 'M' + r1(x0 + 18 * sc) + ' ' + r1(y0 - 2 * sc) + 'l' + r1(22 * sc) + ' ' + r1(-8 * sc) + '" stroke="#c58a4a" stroke-width="' + r1(2 * sc) + '"/>';
    s += '<g class="lk-village">' + P(v) + '</g>';
    // слід, лінія автозабору, крапки тих, хто забрав
    s += '<g class="lk-trail"><path class="lk-tf" fill="url(#' + p + 'trail)"/><path class="lk-tg" fill="none" stroke="#ffd75a" stroke-width="' + r1(12 * sc) + '" stroke-linecap="round" stroke-linejoin="round" opacity=".22"/>'
      + '<path class="lk-tl" fill="none" stroke="#ffe9a0" stroke-width="' + r1(Math.max(2.4, 4 * sc)) + '" stroke-linecap="round" stroke-linejoin="round"/>'
      + '<g class="lk-auto" style="display:none"><path class="lk-al" stroke="#9be7a6" stroke-width="1.6" stroke-dasharray="7 6"/><text class="lk-at" fill="#9be7a6" font-size="' + r1(Math.max(11, 14 * sc)) + '" font-weight="700"></text></g>'
      + '<g class="lk-dots"></g></g>';
    // лелека з Глеком
    s += '<g class="lk-stork"><g class="lk-sk">' + storkArt(p) + '<g class="lk-hic" opacity="0"><path d="M58 -64h44a10 10 0 0 1 10 10v10a10 10 0 0 1 -10 10h-26l-12 10 2 -10h-8a10 10 0 0 1 -10 -10v-10a10 10 0 0 1 10 -10z" fill="#fff" stroke="' + INK + '" stroke-width="2.4"/>'
      + '<text x="80" y="-42" text-anchor="middle" font-size="18" font-weight="900" fill="' + INK + '">ік!</text></g></g></g>'
      + '<g class="lk-pot"><g class="lk-pi">' + potArt(p) + '</g></g>';
    // падіння: Глек летить униз, черепки
    s += '<g class="lk-drop" style="display:none"><g class="lk-di">' + potArt(p) + '</g></g><g class="lk-shards"></g>';
    return s;
  }

  function shardsHtml(sc) {
    const R = rng(Date.now() % 100000 + 7);
    const cols = ['#e8773d', '#c45a2a', '#a9461f', '#ffb98a', '#d9703a', '#fff0d2'];
    let h = '<circle class="lk-dust" r="' + r1(40 * sc) + '" fill="#f3d9c0" opacity="0"/>';
    for (let i = 0; i < 12; i++) {
      const a = (R() - 0.5) * Math.PI * 0.95, d = (40 + R() * 120) * sc;
      const sx = Math.sin(a) * d * 1.6, up = (40 + R() * 90) * sc;
      const z = (6 + R() * 10) * sc;
      const pts = [[0, -z], [z * (0.6 + R() * 0.5), z * 0.3], [-z * (0.5 + R() * 0.5), z * (0.2 + R() * 0.5)]];
      h += '<g class="lk-sh" style="--sx:' + r1(sx) + 'px;--up:' + r1(-up) + 'px;--hx:' + r1(sx * 0.5) + 'px;--r:' + Math.round((R() - 0.5) * 720) + 'deg;animation-delay:' + Math.round(R() * 60) + 'ms">'
        + '<path d="M' + pts.map((q) => r1(q[0]) + ' ' + r1(q[1])).join('L') + 'Z" fill="' + cols[i % cols.length] + '" stroke="' + INK + '" stroke-width="' + r1(1.6 * sc) + '" stroke-linejoin="round"/></g>';
    }
    return h;
  }

  const FEATHER = '<svg viewBox="0 0 24 48" aria-hidden="true"><path d="M12 46C12 32 12.5 16 13 3" stroke="#8b93a8" stroke-width="1.6" fill="none"/>'
    + '<path d="M13 3C23 12 22 30 12 40C3 30 4 12 13 3Z" fill="#fff" stroke="' + INK + '" stroke-width="1.4"/>'
    + '<path d="M12.6 12l6 -3M12.4 20l7 -3M12.2 28l6 -2M12.6 14l-5 -3M12.4 22l-6 -3" stroke="#c9cfdc" stroke-width="1"/></svg>';

  // ---------------------------------------------------------------------------------------------
  // Звук (WebAudio-синт; лише коли гравець увімкнув)
  // ---------------------------------------------------------------------------------------------
  function audio(st) {
    if (!st.sound) return null;
    if (!st.ac) {
      const AC = window.AudioContext || window.webkitAudioContext;
      if (!AC) return null;
      st.ac = new AC();
      st.master = st.ac.createGain(); st.master.gain.value = 0.5; st.master.connect(st.ac.destination);
    }
    if (st.ac.state === 'suspended') st.ac.resume();
    return st.ac;
  }
  function toneStart(st) {
    const ac = audio(st); if (!ac || st.tone) return;
    const o = ac.createOscillator(), o2 = ac.createOscillator(), g = ac.createGain();
    o.type = 'triangle'; o2.type = 'sine'; o.frequency.value = 160; o2.frequency.value = 240;
    g.gain.value = 0; g.gain.linearRampToValueAtTime(0.05, ac.currentTime + 0.3);
    o.connect(g); o2.connect(g); g.connect(st.master); o.start(); o2.start();
    st.tone = { o, o2, g };
  }
  function toneSet(st, m) {
    if (!st.tone || !st.ac) return;
    const f = Math.min(1500, 160 * Math.pow(m, 0.55)), t = st.ac.currentTime;
    st.tone.o.frequency.setTargetAtTime(f, t, 0.05);
    st.tone.o2.frequency.setTargetAtTime(f * 1.5 + Math.sin(t * 9) * 6, t, 0.05);
    st.tone.g.gain.setTargetAtTime(Math.min(0.09, 0.04 + Math.log(m) * 0.012), t, 0.1);
  }
  function toneStop(st) {
    if (!st.tone || !st.ac) { st.tone = null; return; }
    const { o, o2, g } = st.tone, t = st.ac.currentTime;
    g.gain.cancelScheduledValues(t); g.gain.setTargetAtTime(0, t, 0.04);
    o.stop(t + 0.3); o2.stop(t + 0.3); st.tone = null;
  }
  function ping(st, freqs, dur, vol) {
    const ac = audio(st); if (!ac) return;
    freqs.forEach((f, i) => {
      const o = ac.createOscillator(), g = ac.createGain(), t = ac.currentTime + i * 0.07;
      o.type = 'sine'; o.frequency.value = f;
      g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(vol || 0.18, t + 0.01); g.gain.exponentialRampToValueAtTime(0.001, t + (dur || 0.6));
      o.connect(g); g.connect(st.master); o.start(t); o.stop(t + (dur || 0.6) + 0.05);
    });
  }
  function crack(st) {
    const ac = audio(st); if (!ac) return;
    const len = Math.floor(ac.sampleRate * 0.5), buf = ac.createBuffer(1, len, ac.sampleRate), d = buf.getChannelData(0);
    for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, 3) * (i % 900 < 300 ? 1 : 0.4);
    const n = ac.createBufferSource(), bp = ac.createBiquadFilter(), g = ac.createGain(), t = ac.currentTime;
    n.buffer = buf; bp.type = 'bandpass'; bp.frequency.value = 2200; bp.Q.value = 0.8; g.gain.value = 0.5;
    n.connect(bp); bp.connect(g); g.connect(st.master); n.start(t);
    const o = ac.createOscillator(), og = ac.createGain();
    o.frequency.setValueAtTime(140, t); o.frequency.exponentialRampToValueAtTime(40, t + 0.3);
    og.gain.setValueAtTime(0.35, t); og.gain.exponentialRampToValueAtTime(0.001, t + 0.35);
    o.connect(og); og.connect(st.master); o.start(t); o.stop(t + 0.4);
  }

  // ---------------------------------------------------------------------------------------------
  // Стан: що зараз (кадр чи вид — новіший), моє, годинник
  // ---------------------------------------------------------------------------------------------
  const key = (s) => (s ? (s.round || 0) * 10 + (PH[s.phase] || 0) : -1);
  function cur(st) {
    const c = st.ctx || {}, f = c.frame, v = c.view;
    const s = f && f.phase && key(f) >= key(v) ? f : v && v.phase ? v : f && f.phase ? f : null;
    return s;
  }
  function mineOf(st, s) {
    const c = st.ctx, v = c.view || {}, nick = c.me && c.me.nick;
    const row = nick && s && (s.bets || []).find((b) => b.nick === nick);
    if (row) return { amount: row.amount, auto: row.auto, out: row.out, win: row.win };
    if (v.mine && v.round === s.round && v.mine.amount) return v.mine;
    return null;
  }
  function syncClock(st, s) {
    const n = tms(s.now);
    if (!isFinite(n)) return;
    const sample = n - Date.now();
    // найменша затримка = найбільший зсув; повільно відпускаємо, щоб пережити зміну годинника
    st.off = st.off == null ? sample : Math.max(sample, st.off - 3);
  }
  const snow = (st) => Date.now() + (st.off || 0);
  function localM(st, s) {
    if (!s) return 1;
    if (s.phase === 'crash' || s.phase === 'pause') return s.crash || s.m || 1;
    if (s.phase !== 'flight') return 1;
    const k = s.k || K_DEF, a = tms(s.startAt);
    let m = isFinite(a) ? Math.exp(k * Math.max(0, snow(st) - a) / 1000) : (s.m || 1);
    if (s.m && m < s.m) m = s.m;
    return m;
  }

  // ---------------------------------------------------------------------------------------------
  // Каркас DOM
  // ---------------------------------------------------------------------------------------------
  function skeleton(root, st) {
    const amt = store.get('amt', 50), ax = store.get('ax', 2), aon = store.get('aon', false);
    st.amount = amt; st.autoX = ax; st.autoOn = aon;
    root.innerHTML = '<div class="lk">'
      + '<div class="lk-stage"><svg class="lk-svg" xmlns="http://www.w3.org/2000/svg" preserveAspectRatio="none"></svg>'
      + '<div class="lk-ov"><div class="lk-hist"></div>'
      + '<div class="lk-tools"><button type="button" class="lk-tb lk-snd" title="Звук">🔈</button><button type="button" class="lk-tb lk-ib" title="Правила й чесність">ⓘ</button></div>'
      + '<div class="lk-big"><div class="lk-cap"></div><div class="lk-m">×1,00</div><div class="lk-sub"></div><div class="lk-bar"><i></i></div></div>'
      + '<div class="lk-mile"></div><div class="lk-fx"></div></div></div>'
      + '<div class="lk-panel">'
      + '<div class="lk-box lk-amt"><div class="lk-lab">ставка, 🏺</div><div class="lk-row"><button type="button" class="lk-pm" data-a="minus">−</button>'
      + '<input class="lk-in" inputmode="numeric" autocomplete="off" aria-label="Сума ставки"><button type="button" class="lk-pm" data-a="plus">+</button></div>'
      + '<div class="lk-q">' + QUICK.map((q) => '<button type="button" data-q="' + q + '">' + q + '</button>').join('')
      + '<button type="button" data-a="half">½</button><button type="button" data-a="dbl">×2</button></div></div>'
      + '<div class="lk-box lk-au"><label class="lk-lab lk-sw"><input type="checkbox" class="lk-aon"> <span>автозабрати на</span></label>'
      + '<div class="lk-row"><span class="lk-x">×</span><input class="lk-ax" inputmode="decimal" autocomplete="off" aria-label="Автозабір на множнику"></div>'
      + '<div class="lk-q">' + AUTOQ.map((q) => '<button type="button" data-x="' + q + '">×' + String(q).replace('.', ',') + '</button>').join('') + '</div></div>'
      + '<button type="button" class="lk-go"><span class="lk-gt"></span><small class="lk-gs"></small></button>'
      + '<div class="lk-foot"><span class="lk-wal"></span><button type="button" class="lk-hash" title="Перевірка чесності"></button></div>'
      + '</div>'
      + '<div class="lk-bets"><div class="lk-bh"><b>Ставки столу</b><span class="lk-bn"></span></div><div class="lk-bl"></div></div>'
      + '<div class="lk-info" hidden></div>'
      + '</div>';
    const q = (s) => root.querySelector(s);
    st.el = {
      box: q('.lk'), stage: q('.lk-stage'), svg: q('.lk-svg'), hist: q('.lk-hist'), big: q('.lk-big'), cap: q('.lk-cap'),
      m: q('.lk-m'), sub: q('.lk-sub'), bar: q('.lk-bar i'), mile: q('.lk-mile'), fx: q('.lk-fx'),
      amt: q('.lk-in'), aon: q('.lk-aon'), ax: q('.lk-ax'), go: q('.lk-go'), gt: q('.lk-gt'), gs: q('.lk-gs'),
      wal: q('.lk-wal'), hash: q('.lk-hash'), bn: q('.lk-bn'), bl: q('.lk-bl'), info: q('.lk-info'), snd: q('.lk-snd'),
    };
    st.el.amt.value = amt; st.el.ax.value = fmtM(ax); st.el.aon.checked = aon;
    wire(root, st);
  }

  function wire(root, st) {
    const el = st.el;
    const on = (n, ev, fn) => { n.addEventListener(ev, fn); st.offs.push(() => n.removeEventListener(ev, fn)); };
    on(el.go, 'click', () => primary(root, st));
    on(root.querySelector('.lk-amt'), 'click', (e) => {
      const b = e.target.closest('button'); if (!b) return;
      const L = limits(st);
      let a = st.amount;
      if (b.dataset.q) a = +b.dataset.q;
      else if (b.dataset.a === 'half') a = Math.floor(a / 2);
      else if (b.dataset.a === 'dbl') a = a * 2;
      else if (b.dataset.a === 'plus') a = a + (a < 100 ? 10 : a < 1000 ? 50 : 100);
      else if (b.dataset.a === 'minus') a = a - (a <= 100 ? 10 : a <= 1000 ? 50 : 100);
      setAmount(st, a, L);
    });
    on(el.amt, 'change', () => setAmount(st, parseInt(el.amt.value.replace(/\D/g, ''), 10) || 0, limits(st)));
    on(el.amt, 'keydown', (e) => { if (e.key === 'Enter') { el.amt.blur(); } });
    on(root.querySelector('.lk-au'), 'click', (e) => {
      const b = e.target.closest('button[data-x]'); if (!b) return;
      setAuto(root, st, +b.dataset.x, true);
    });
    on(el.ax, 'change', () => setAuto(root, st, parseX(el.ax.value), st.autoOn));
    on(el.ax, 'keydown', (e) => { if (e.key === 'Enter') el.ax.blur(); });
    on(el.aon, 'change', () => setAuto(root, st, st.autoX, el.aon.checked));
    on(el.snd, 'click', () => {
      st.sound = !st.sound; store.set('snd', st.sound);
      el.snd.textContent = st.sound ? '🔊' : '🔈';
      if (st.sound) { audio(st); if (st.live) toneStart(st); } else toneStop(st);
    });
    on(root.querySelector('.lk-ib'), 'click', () => info(root, st, true));
    on(el.hash, 'click', () => info(root, st, true));
    on(el.info, 'click', (e) => {
      if (e.target === el.info || e.target.closest('.lk-x0')) info(root, st, false);
      if (e.target.closest('.lk-chk')) verify(root, st);
    });
  }

  function limits(st) {
    const v = (st.ctx && st.ctx.view) || {};
    const L = v.limits || {};
    return { min: L.min || 10, max: L.max == null ? 2000 : L.max, wallet: typeof v.wallet === 'number' ? v.wallet : null };
  }
  function setAmount(st, a, L) {
    a = Math.max(L.min, Math.round(a) || 0);
    if (L.max > 0) a = Math.min(a, L.max);
    if (L.wallet != null && a > L.wallet) a = Math.max(L.min, Math.floor(L.wallet));
    st.amount = a; st.el.amt.value = a; store.set('amt', a);
    paintPanel(st, cur(st), true);
  }
  function setAuto(root, st, x, onOff) {
    if (!isFinite(x) || x < 1.01) x = 1.01;
    if (x > 1000) x = 1000;
    x = Math.round(x * 100) / 100;
    const changed = x !== st.autoX || onOff !== st.autoOn;
    st.autoX = x; st.autoOn = !!onOff;
    st.el.ax.value = fmtM(x); st.el.aon.checked = st.autoOn;
    store.set('ax', x); store.set('aon', st.autoOn);
    const s = cur(st), mine = s && mineOf(st, s);
    if (changed && mine && !mine.out && s && (s.phase === 'bets' || s.phase === 'flight') && st.ctx.mine) {
      st.ctx.act('auto', { x: st.autoOn ? x : null });
    }
    paintPanel(st, s, true);
    if (s) drawStatic(st, s);
  }

  // ---------------------------------------------------------------------------------------------
  // Дії
  // ---------------------------------------------------------------------------------------------
  function primary(root, st) {
    const s = cur(st), c = st.ctx;
    if (!s || !c || !c.mine || st.busy) return false;
    const mine = mineOf(st, s);
    let p = null;
    if (s.phase === 'bets') {
      if (mine && mine.amount) p = c.act('cancel');
      else p = c.act('bet', { amount: st.amount, auto: st.autoOn ? st.autoX : null });
    } else if (s.phase === 'flight' && mine && !mine.out) {
      st.cashing = s.round;
      p = c.act('cash');
      paintPanel(st, s, true);
    }
    if (!p) return false;
    st.busy = true;
    const done = () => { st.busy = false; paintPanel(st, cur(st), true); };
    Promise.resolve(p).then((r) => { if (r && r.ok === false) st.cashing = null; done(); }, () => { st.cashing = null; done(); });
    return true;
  }

  // ---------------------------------------------------------------------------------------------
  // Геометрія сцени
  // ---------------------------------------------------------------------------------------------
  function geom(st) {
    const r = st.el.stage.getBoundingClientRect();
    const W = Math.max(280, Math.round(r.width)), H = Math.max(200, Math.round(r.height));
    const narrow = W < 640;
    const sc = clamp(Math.min(H / 480, W / 640), 0.5, 1.3);
    const gy = H * 0.94, x0 = Math.max(70 * sc + 10, W * 0.09), y0 = gy - 96 * sc;
    return {
      W, H, sc, gy, x0, y0, S: H * 0.55, narrow,
      ox: x0 + 72 * sc, oy: y0 - 30 * sc,                   // початок сліду — дзьоб у гнізді
      x1: W * (narrow ? 0.72 : 0.7), y1: H * (narrow ? 0.52 : 0.36),
    };
  }

  function build(root, st) {
    const g = geom(st);
    if (st.g && st.g.W === g.W && st.g.H === g.H) return false;
    st.g = g;
    st.p = 'lk' + (++uid) + '-';
    const svg = st.el.svg;
    svg.setAttribute('viewBox', '0 0 ' + g.W + ' ' + g.H);
    svg.innerHTML = scene(g, st.p);
    const q = (s) => svg.querySelector(s);
    st.sv = {
      s0: q('.lk-s0'), s1: q('.lk-s1'), stars: q('.lk-stars'), space: q('.lk-space'), sun: q('.lk-sun'), clouds: q('.lk-clouds'),
      hills: q('.lk-hills'), village: q('.lk-village'), tf: q('.lk-tf'), tg: q('.lk-tg'), tl: q('.lk-tl'),
      auto: q('.lk-auto'), al: q('.lk-al'), at: q('.lk-at'), dots: q('.lk-dots'),
      stork: q('.lk-stork'), sk: q('.lk-sk'), hic: q('.lk-hic'), pot: q('.lk-pot'), pi: q('.lk-pi'), drop: q('.lk-drop'), di: q('.lk-di'), shards: q('.lk-shards'),
    };
    return true;
  }

  const tf = (n, x, y) => { const v = 'translate(' + r1(x) + 'px,' + r1(y) + 'px)'; if (n.style.transform !== v) n.style.transform = v; };

  /// Камера й лелека для множника m (у польоті) або спокою (m=1, cam=0).
  function draw(st, m, fly, s) {
    const g = st.g, v = st.sv;
    if (!g || !v) return;
    const k = (s && s.k) || K_DEF;
    const cam = fly ? Math.log(m) : (st.camHold || 0);
    const t = fly ? Math.log(m) / k : 0;
    // небо
    const top = mix(SKY_TOP, cam), bot = mix(SKY_BOT, cam);
    if (v.s0._c !== top) { v.s0.setAttribute('stop-color', top); v.s0._c = top; }
    if (v.s1._c !== bot) { v.s1.setAttribute('stop-color', bot); v.s1._c = bot; }
    const S = g.S;
    tf(v.village, -Math.min(t * 10, g.W * 0.2), cam * S * 1.5);
    tf(v.hills, -Math.min(t * 4, g.W * 0.08), cam * S * 1.1);
    tf(v.sun, 0, cam * S * 0.9);
    tf(v.clouds, -((t * 46) % g.W), cam * S * 1.2);
    tf(v.stars, 0, cam * S * 0.06);
    tf(v.space, 0, cam * S * 0.6);
    const so = r1(clamp((cam - 1.5) / 1.0, 0, 1)), po = r1(clamp((cam - 2.6) / 0.7, 0, 1));
    if (v.stars._o !== so) { v.stars.setAttribute('opacity', so); v.stars._o = so; }
    if (v.space._o !== po) { v.space.setAttribute('opacity', po); v.space._o = po; }
    // слід
    const ox = g.ox, oy = g.oy;
    if (!fly || m <= 1.0001) {
      v.tl.setAttribute('d', ''); v.tg.setAttribute('d', ''); v.tf.setAttribute('d', '');
      v.auto.style.display = 'none';
      st.tip = [ox, oy]; st.ang = 0;
      if (!st.crashing) placeStork(st, ox, oy, 0, true);
      return;
    }
    const xmax = Math.max(7, t), ymax = Math.max(1.6, m);
    const fx = (u) => ox + (u / xmax) * (g.x1 - ox);
    const fy = (mm) => oy - ((mm - 1) / (ymax - 1)) * (oy - g.y1);
    const N = 36;
    let d = '';
    for (let i = 0; i <= N; i++) {
      const u = (t * i) / N;
      d += (i ? 'L' : 'M') + r1(fx(u)) + ' ' + r1(fy(Math.exp(k * u)));
    }
    const px = fx(t), py = fy(m);
    v.tl.setAttribute('d', d); v.tg.setAttribute('d', d);
    v.tf.setAttribute('d', d + 'L' + r1(px) + ' ' + g.H + 'L' + r1(ox) + ' ' + g.H + 'Z');
    // лінія автозабору
    const mine = s && mineOf(st, s);
    const ax = mine && !mine.out ? mine.auto : null;
    if (ax && ax <= ymax * 1.8 && ax > 1) {
      const ay = fy(ax);
      if (ay > 8) {
        v.auto.style.display = '';
        v.al.setAttribute('d', 'M' + r1(ox) + ' ' + r1(ay) + 'H' + r1(g.W - 8));
        v.at.setAttribute('x', r1(g.W - 10)); v.at.setAttribute('y', r1(ay - 5)); v.at.setAttribute('text-anchor', 'end');
        const tx = 'твій автозабір ×' + fmtM(ax);
        if (v.at.textContent !== tx) v.at.textContent = tx;
      } else v.auto.style.display = 'none';
    } else v.auto.style.display = 'none';
    // крапки на сліді — хто де забрав
    if (s) {
      const outs = (s.bets || []).filter((b) => b.out);
      const sig = outs.map((b) => b.nick + b.out).join('|') + '@' + Math.round(ymax * 50) + ':' + Math.round(xmax * 10);
      if (sig !== st.dotSig) {
        st.dotSig = sig;
        v.dots.innerHTML = outs.map((b) => {
          const u = Math.log(b.out) / k;
          return '<circle cx="' + r1(fx(u)) + '" cy="' + r1(fy(b.out)) + '" r="' + r1(Math.max(3, 5 * g.sc)) + '" fill="#9be7a6" stroke="' + INK + '" stroke-width="1.6"/>';
        }).join('');
      }
    }
    // кут — дотична до кривої
    const dx = (g.x1 - ox) / xmax, dy = -(k * m / (ymax - 1)) * (oy - g.y1);
    const ang = clamp(Math.atan2(dy, dx) * 180 / Math.PI, -58, -4);
    st.tip = [px, py]; st.ang = ang;
    placeStork(st, px, py, ang, false, t);
  }

  function placeStork(st, px, py, ang, rest, t) {
    const v = st.sv, sc = st.g.sc;
    const a = (ang * 0.75) * Math.PI / 180;
    const bx = BEAK[0] * sc, by = BEAK[1] * sc;
    const sx = px - (bx * Math.cos(a) - by * Math.sin(a)), sy = py - (bx * Math.sin(a) + by * Math.cos(a));
    const tr = 'translate(' + r1(sx) + ' ' + r1(sy) + ') rotate(' + r1(ang * 0.75) + ') scale(' + sc + ')';
    if (v.stork._t !== tr) { v.stork.setAttribute('transform', tr); v.stork._t = tr; }
    const sw = rest ? 0 : Math.sin((t || 0) * 5) * 7;
    const pt = 'translate(' + r1(px) + ' ' + r1(py) + ') scale(' + sc + ') rotate(' + r1(sw) + ')';
    if (v.pot._t !== pt) { v.pot.setAttribute('transform', pt); v.pot._t = pt; }
    st.potAt = [px, py];
  }

  // ---------------------------------------------------------------------------------------------
  // Фази
  // ---------------------------------------------------------------------------------------------
  function setPhaseClass(st, ph) {
    const b = st.el.box;
    ['bets', 'flight', 'crash', 'pause'].forEach((p) => b.classList.toggle('P-' + p, p === ph));
  }

  function enter(root, st, s, prev) {
    setPhaseClass(st, s.phase);
    const v = st.sv, el = st.el;
    if (s.phase === 'bets' || s.phase === 'pause') {
      if (s.phase === 'bets') {
        st.crashing = false; st.camHold = 0;
        el.stage.classList.add('lk-ease');
        v.sk.classList.remove('gone', 'hic'); v.sk.classList.add('rest', 'land');
        v.pot.style.display = ''; v.drop.style.display = 'none'; v.shards.innerHTML = ''; v.dots.innerHTML = ''; st.dotSig = '';
        st.miles = 0;
        timer(st, () => { el.stage.classList.remove('lk-ease'); v.sk.classList.remove('land'); }, 1300);
        draw(st, 1, false, s);
        bar(st, s);
      }
    }
    if (s.phase === 'flight') {
      el.stage.classList.remove('lk-ease');
      v.sk.classList.remove('rest', 'gone', 'hic', 'land');
      v.pot.style.display = ''; v.drop.style.display = 'none'; v.shards.innerHTML = '';
      st.crashing = false;
      if (st.sound) toneStart(st);
    }
    if (s.phase === 'crash' || (s.phase === 'pause' && prev === 'flight')) {
      toneStop(st);
      const m = s.crash || s.m || 1;
      st.camHold = Math.log(m);
      if (prev === 'flight') boom(root, st, s, m);
      else if (!st.crashing) { draw(st, m, m > 1.0001, s); v.sk.classList.add('gone'); v.pot.style.display = 'none'; st.crashing = true; }
      const mine = mineOf(st, s);
      if (mine && mine.amount && !mine.out && st.lostRound !== s.round) {
        st.lostRound = s.round;
        pop(st, '−' + fmtN(mine.amount) + ' 🏺', 'lost', 0.5, 0.62);
      }
    }
  }

  function boom(root, st, s, m) {
    const v = st.sv, el = st.el, g = st.g;
    st.crashing = true;
    draw(st, m, m > 1.0001, s);
    // лелека гикає
    v.sk.classList.add('hic');
    const [px, py] = st.potAt || [g.ox, g.oy];
    v.pot.style.display = 'none';
    v.drop.style.display = '';
    v.drop.setAttribute('transform', 'translate(' + r1(px) + ' ' + r1(py) + ') scale(' + g.sc + ')');
    const gy = g.H - 16 * g.sc;
    v.di.style.setProperty('--dy', r1((gy - py - 50 * g.sc) / g.sc) + 'px');
    v.di.classList.remove('go'); void v.di.getBBox(); v.di.classList.add('go');
    st.sx = px;
    timer(st, () => {
      v.drop.style.display = 'none';
      v.shards.setAttribute('transform', 'translate(' + r1(px) + ' ' + r1(gy) + ')');
      v.shards.innerHTML = shardsHtml(g.sc);
      el.stage.classList.remove('shake'); void el.stage.offsetWidth; el.stage.classList.add('shake');
      pop(st, 'ДЗЕНЬ!', 'dzen', px / g.W, (gy - 40 * g.sc) / g.H);
      crack(st);
    }, 620);
    timer(st, () => { v.sk.classList.remove('hic'); v.sk.classList.add('gone'); }, 700);
  }

  function bar(st, s) {
    const u = tms(s.until), i = st.el.bar;
    if (!isFinite(u)) { i.style.transition = 'none'; i.style.width = '0%'; return; }
    const left = Math.max(0, u - snow(st)), total = st.betMs || 8000;
    i.style.transition = 'none';
    i.style.width = clamp(left / total * 100, 0, 100) + '%';
    void i.offsetWidth;
    i.style.transition = 'width ' + left + 'ms linear';
    i.style.width = '0%';
  }

  // ---------------------------------------------------------------------------------------------
  // Великий напис, кнопка, панель, списки
  // ---------------------------------------------------------------------------------------------
  function big(st, s, m) {
    const el = st.el;
    let cap = '', main = '', sub = '', col = '#fff', cls = '';
    const left = Math.max(0, tms(s.until) - snow(st));
    if (s.phase === 'bets') {
      cap = 'ставки';
      main = isFinite(left) ? 'злітаємо за ' + Math.ceil(left / 1000) : 'ставки';
      sub = 'роби ставку — Глек уже в дзьобі';
      cls = 'B-wait';
    } else if (s.phase === 'flight') {
      main = '×' + fmtM(m);
      col = mix(M_COL, Math.log(m));
      sub = m >= 200 ? 'повз Місяць!' : m >= 50 ? 'лелека в космосі!' : m >= 10 ? 'вище хмар!' : '';
      cls = 'B-fly';
    } else {
      const c = s.crash || s.m || 1;
      cap = s.phase === 'crash' ? 'шубовсть!' : 'пролетіли на';
      main = '×' + fmtM(c);
      sub = s.phase === 'crash' ? 'пролетіли!' : (isFinite(left) ? 'наступний політ за ' + Math.ceil(left / 1000) + ' с' : 'наступний політ скоро');
      col = '#ff5a4a';
      cls = 'B-boom';
    }
    if (el.cap.textContent !== cap) el.cap.textContent = cap;
    if (el.m.textContent !== main) el.m.textContent = main;
    if (el.sub.textContent !== sub) el.sub.textContent = sub;
    if (el.m._c !== col) { el.m.style.color = col; el.m._c = col; }
    if (el.big._cls !== cls) {
      el.big.classList.remove('B-wait', 'B-fly', 'B-boom'); el.big.classList.add(cls); el.big._cls = cls;
    }
    const hot = s.phase === 'flight' ? (m >= 10 ? 3 : m >= 5 ? 2 : m >= 2 ? 1 : 0) : 0;
    if (el.big._h !== hot) { el.big.dataset.hot = hot; el.big._h = hot; }
  }

  function btnState(st, s, m) {
    const c = st.ctx;
    if (!c.mine) return { mode: 'sit', t: 'Сядь за стіл, щоб ставити', s: 'дивишся як глядач' };
    const mine = mineOf(st, s), v = c.view || {};
    if (s.phase === 'bets' && v.on === false && !(mine && mine.amount)) return { mode: 'wait', t: 'Ставки вимкнено', s: 'Глек відпочиває — політ без ставок' };
    if (s.phase === 'bets') {
      if (mine && mine.amount) return { mode: 'cancel', t: 'Скасувати ставку', s: fmtN(mine.amount) + ' 🏺' + (mine.auto ? ' · авто ×' + fmtM(mine.auto) : '') + ' · чекаємо зльоту' };
      return { mode: 'bet', t: 'Поставити ' + fmtN(st.amount) + ' 🏺', s: st.autoOn ? 'автозабір на ×' + fmtM(st.autoX) : 'пробіл — теж «Поставити»' };
    }
    if (mine && mine.out) return { mode: 'won', t: 'Забрав на ×' + fmtM(mine.out), s: '+' + fmtN(mine.win != null ? mine.win : mine.amount * mine.out) + ' 🏺' };
    if (s.phase === 'flight' && mine && mine.amount) {
      if (st.cashing === s.round) return { mode: 'cash busy', t: 'Забираю…', s: '×' + fmtM(m) };
      return { mode: 'cash', t: 'Забрати ×' + fmtM(m), s: '= ' + fmtN(Math.floor(mine.amount * m)) + ' 🏺' };
    }
    if ((s.phase === 'crash' || s.phase === 'pause') && mine && mine.amount) return { mode: 'lost', t: 'Пролетів…', s: '−' + fmtN(mine.amount) + ' 🏺 · наступний політ скоро' };
    return { mode: 'wait', t: 'Чекаю наступний політ', s: s.phase === 'flight' ? 'цей уже в небі — ставки після падіння' : 'ставки відкриються за мить' };
  }

  function paintBtn(st, s, m) {
    const b = btnState(st, s, m), el = st.el;
    if (el.go._m !== b.mode) {
      el.go.className = 'lk-go M-' + b.mode.replace(' ', ' M-');
      el.go._m = b.mode;
    }
    const dis = !/^(bet|cancel|cash)$/.test(b.mode) || (st.busy && b.mode !== 'cash');
    if (el.go.disabled !== dis) el.go.disabled = dis;
    if (el.gt.textContent !== b.t) el.gt.textContent = b.t;
    if (el.gs.textContent !== b.s) el.gs.textContent = b.s;
    const pulse = b.mode === 'cash' ? (m >= 5 ? '3' : m >= 2 ? '2' : '1') : '';
    if (el.go.dataset.p !== pulse) el.go.dataset.p = pulse;
  }

  function paintPanel(st, s, force) {
    if (!s) return;
    const L = limits(st), el = st.el;
    paintBtn(st, s, localM(st, s));
    const note = st.ctx.view && st.ctx.view.note;
    const wal = 'баланс <b>' + (L.wallet == null ? '—' : fmtN(L.wallet)) + '</b> 🏺 · ставка ' + L.min + '–' + (L.max > 0 ? fmtN(L.max) : '∞')
      + (note ? ' · <span class="lk-note">' + st.ctx.esc(note) + '</span>' : '');
    if (force || el.wal._h !== wal) { el.wal.innerHTML = wal; el.wal._h = wal; }
    const h = s.hash ? '🔒 #' + (s.round || '') + ' ' + String(s.hash).slice(0, 10) + '…' : '🔒 перевірка чесності';
    if (el.hash.textContent !== h) el.hash.textContent = h;
  }

  function paintBets(st, s) {
    const c = st.ctx, me = c.me && c.me.nick;
    const bets = (s.bets || []).slice().sort((a, b) => (b.nick === me) - (a.nick === me) || (b.amount - a.amount));
    const sig = s.phase + '|' + bets.map((b) => b.nick + ':' + b.amount + ':' + b.auto + ':' + b.out).join(',');
    if (sig === st.betSig) return;
    st.betSig = sig;
    const total = bets.reduce((a, b) => a + (b.amount || 0), 0);
    st.el.bn.textContent = bets.length ? bets.length + ' · ' + fmtN(total) + ' 🏺' : '';
    const done = s.phase === 'crash' || s.phase === 'pause';
    st.el.bl.innerHTML = bets.length ? bets.map((b) => {
      let cls = 's-fly', txt;
      if (b.out) { cls = 's-out'; txt = '×' + fmtM(b.out) + ' <b>+' + fmtN(b.win != null ? b.win : b.amount * b.out) + '</b>'; }
      else if (done) { cls = 's-lost'; txt = 'пролетів'; }
      else if (s.phase === 'bets') { cls = 's-wait'; txt = b.auto ? 'авто ×' + fmtM(b.auto) : 'чекає зльоту'; }
      else txt = 'летить…' + (b.auto ? ' <i>авто ×' + fmtM(b.auto) + '</i>' : '');
      return '<div class="lk-br ' + cls + (b.nick === me ? ' me' : '') + '"><span class="nk">' + c.esc(b.nick) + '</span>'
        + '<span class="am">' + fmtN(b.amount) + '</span><span class="st">' + txt + '</span></div>';
    }).join('') : '<div class="lk-empty">' + (s.phase === 'bets' ? 'Ще ніхто не ставив — будь першим' : 'У цьому польоті ставок нема') + '</div>';
  }

  function paintHist(st, s) {
    const h = (s.history || []).slice().sort((a, b) => (b.round || 0) - (a.round || 0)).slice(0, 20);
    const sig = h.map((x) => x.round + ':' + x.crash).join(',');
    if (sig === st.histSig) return;
    const fresh = st.histSig != null;
    st.histSig = sig;
    st.el.hist.innerHTML = h.map((x, i) => '<span class="lk-h ' + (x.crash >= 10 ? 'c2' : x.crash >= 2 ? 'c1' : 'c0') + (i === 0 && fresh ? ' new' : '')
      + '" title="політ #' + x.round + '">' + fmtM(x.crash) + '</span>').join('');
  }

  // ---------------------------------------------------------------------------------------------
  // Ефекти: пір'їнки, вигуки, віхи
  // ---------------------------------------------------------------------------------------------
  function timer(st, fn, ms) { const id = setTimeout(() => { st.timers = st.timers.filter((x) => x !== id); fn(); }, ms); st.timers.push(id); return id; }

  function feather(st, nick, out, me) {
    const fx = st.el.fx;
    while (fx.querySelectorAll('.lk-fe').length > 9) fx.querySelector('.lk-fe').remove();
    const [x, y] = st.tip || [st.g.ox, st.g.oy];
    const d = document.createElement('div');
    d.className = 'lk-fe' + (me ? ' me' : '');
    d.style.left = r1(x - 10 + (Math.random() - 0.5) * 30) + 'px';
    d.style.top = r1(y - 10) + 'px';
    d.style.setProperty('--fall', r1(Math.min(st.g.H * 0.55, st.g.H - y + 20)) + 'px');
    d.innerHTML = '<i>' + FEATHER + '</i><span>' + st.ctx.esc(nick) + ' <b>×' + fmtM(out) + '</b></span>';
    fx.appendChild(d);
    timer(st, () => d.remove(), 6200);
  }

  function pop(st, text, cls, fx, fy) {
    const d = document.createElement('div');
    d.className = 'lk-pop ' + cls;
    d.style.left = r1(fx * 100) + '%'; d.style.top = r1(fy * 100) + '%';
    d.textContent = text;
    st.el.fx.appendChild(d);
    timer(st, () => d.remove(), cls === 'lost' ? 2600 : 1800);
  }

  function coins(st) {
    const fx = st.el.fx, [x, y] = st.tip || [st.g.W / 2, st.g.H / 2];
    for (let i = 0; i < 14; i++) {
      const c = document.createElement('i');
      c.className = 'lk-coin';
      c.style.left = r1(x) + 'px'; c.style.top = r1(y) + 'px';
      const a = Math.random() * Math.PI * 2, d = 50 + Math.random() * 110;
      c.style.setProperty('--cx', r1(Math.cos(a) * d) + 'px');
      c.style.setProperty('--cy', r1(Math.sin(a) * d * 0.7 - 40) + 'px');
      c.style.animationDelay = Math.round(Math.random() * 120) + 'ms';
      fx.appendChild(c);
      timer(st, () => c.remove(), 1400);
    }
  }

  function outs(st, s, quiet) {
    const me = st.ctx.me && st.ctx.me.nick;
    (s.bets || []).forEach((b) => {
      if (!b.out) return;
      const id = s.round + ':' + b.nick;
      if (st.seen.has(id)) return;
      st.seen.add(id);
      if (quiet) return;
      feather(st, b.nick, b.out, b.nick === me);
      if (b.nick === me) {
        st.cashing = null;
        const win = b.win != null ? b.win : Math.floor(b.amount * b.out);
        pop(st, '+' + fmtN(win) + ' 🏺', 'win', 0.5, 0.6);
        coins(st);
        ping(st, [1320, 1760, 2093], 0.7, 0.16);
      } else ping(st, [1568], 0.35, 0.06);
    });
  }

  function miles(st, m) {
    let n = st.miles || 0;
    const was = n;
    while (n < MILES.length && m >= MILES[n][0]) n++;
    if (n === was) return;
    st.miles = n;
    if (m > MILES[n - 1][0] * 1.25) return;     // підсіли посеред польоту — старі віхи не кричимо
    const el = st.el.mile;
    el.textContent = MILES[n - 1][1];
    el.classList.remove('on'); void el.offsetWidth; el.classList.add('on');
    ping(st, [880 * Math.pow(1.12, n)], 0.25, 0.05);
  }

  // ---------------------------------------------------------------------------------------------
  // ⓘ — правила й перевірка
  // ---------------------------------------------------------------------------------------------
  function info(root, st, open) {
    const el = st.el.info;
    if (!open) { el.hidden = true; return; }
    const s = cur(st) || {}, p = st.prev, c = st.ctx, L = limits(st);
    el.innerHTML = '<div class="lk-card"><button type="button" class="lk-x0" aria-label="Закрити">✕</button>'
      + '<h3>Лелека — як грати</h3><ul>'
      + '<li>Глек веде стіл сам: <b>8 с ставки</b> → політ → «шубовсть» 2 с → пауза 3 с. Підсісти можна будь-коли; встав — ставка летить далі (автозабір спрацює й без тебе).</li>'
      + '<li>Під час ставок поставив ' + L.min + '–' + (L.max > 0 ? fmtN(L.max) : '∞') + ' 🏺 (скасувати — лише поки приймають ставки).</li>'
      + '<li>Лелека злітає, множник росте: <b>m = e<sup>0,075·t</sup></b> (×2 ≈ 9 с, ×10 ≈ 31 с, ×100 ≈ 61 с).</li>'
      + '<li><b>Забрати</b> будь-коли до падіння: виграш = ставка × множник у мить, коли сервер отримав натиск. Пробіл чи A на паді — теж.</li>'
      + '<li><b>Автозабрати на ×X</b> (×1,01 … ×1000) — сервер забере сам рівно на ×X. Міняти можна й посеред польоту.</li>'
      + '<li>Не встиг до падіння — ставка пролетіла. Перевага дому 4 %: P(падіння ≥ x) = 0,96 / x, стеля ×1000; ≈5 % польотів падають одразу на ×1,00.</li></ul>'
      + '<h3>Чесно наперед</h3>'
      + '<p>Точку падіння Глек вирішує на початку раунду й одразу показує її відбиток <code>hash = sha256(seed)</code>. Після падіння — сам <code>seed</code>: перевір, що відбиток збігається, а точка падіння виходить з формули.</p>'
      + '<p class="lk-f">' + FORMULA + '</p>'
      + '<div class="lk-kv"><span>зараз</span><b>#' + c.esc(s.round || '—') + '</b><code>' + c.esc(s.hash || '—') + '</code></div>'
      + (p ? '<div class="lk-kv"><span>минулий</span><b>#' + c.esc(p.round) + ' · ×' + fmtM(p.crash) + '</b><code>hash ' + c.esc(p.hash || '—') + '</code><code>seed ' + c.esc(p.seed || '—') + '</code></div>'
        + '<button type="button" class="lk-chk">Перевірити минулий політ</button><div class="lk-res"></div>' : '<p class="muted">Минулого польоту ще не бачили — перевірка з’явиться після першого падіння.</p>')
      + '</div>';
    el.hidden = false;
  }

  // Формула — docs/games/specs/lelka.md §2 (сервер — LelkaCore.cs), точно на BigInt.
  const FORMULA = '<code>hash = sha256(seed)</code> — SHA-256 від UTF-8 байтів рядка seed (64 hex). Точка падіння: '
    + '<code>n</code> = ціле з перших 13 hex-символів seed (52 біти); <code>cents = ⌊96 · 2<sup>52</sup> / (2<sup>52</sup> − n)⌋</code>, '
    + 'обмежене 100 … 100000; <code>crash = cents / 100</code>. Звідси P(падіння ≥ x) = 0,96 / x: за будь-якого автозабору гравцям вертається 96 %.';
  async function crashOf(seed) {
    const n = BigInt('0x' + String(seed).slice(0, 13)), two52 = 1n << 52n;
    let cents = 96n * two52 / (two52 - n);
    if (cents < 100n) cents = 100n;
    if (cents > 100000n) cents = 100000n;
    return Number(cents) / 100;
  }
  async function sha256(s) {
    const b = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(s));
    return [...new Uint8Array(b)].map((x) => x.toString(16).padStart(2, '0')).join('');
  }
  async function verify(root, st) {
    const p = st.prev, out = st.el.info.querySelector('.lk-res');
    if (!p || !out) return;
    if (!window.crypto || !crypto.subtle) { out.textContent = 'Цей браузер не рахує sha256 (потрібен https)'; return; }
    try {
      const h = await sha256(p.seed);
      const okH = h === String(p.hash).toLowerCase();
      const c = await crashOf(String(p.seed).toLowerCase());
      const okC = Math.abs(c - p.crash) < 0.005;
      out.innerHTML = (okH ? '✅ відбиток збігся' : '❌ відбиток НЕ збігся') + '<br>' + (okC ? '✅' : '⚠') + ' за формулою ×' + fmtM(c) + ', на столі ×' + fmtM(p.crash);
    } catch (e) { out.textContent = 'Не вийшло перевірити: ' + e.message; }
  }

  // ---------------------------------------------------------------------------------------------
  // Головний цикл
  // ---------------------------------------------------------------------------------------------
  function sync(root, st) {
    const s = cur(st);
    if (!s || !st.g) return;
    syncClock(st, s);
    if (s.phase === 'bets' && isFinite(tms(s.until)) && st.betRound !== s.round) {
      st.betRound = s.round;
      st.betMs = 8000;
    }
    if (s.round !== st.round) {
      if (st.round != null) st.seen.clear();
      st.round = s.round;
      if (st.phase === s.phase && s.phase === 'bets') enter(root, st, s, 'pause');
    }
    if (s.seed && s.crash && (!st.prev || st.prev.round !== s.round)) st.prev = { round: s.round, hash: s.hash, seed: s.seed, crash: s.crash };
    const first = st.phase == null;
    if (s.phase !== st.phase) {
      const prev = st.phase;
      st.phase = s.phase;
      enter(root, st, s, first ? null : prev);
    }
    outs(st, s, first);
    paintHist(st, s);
    paintBets(st, s);
    paintPanel(st, s);
    const live = s.phase === 'flight' && st.shown !== false;
    if (!live) { big(st, s, localM(st, s)); if (s.phase !== 'flight') stopLoop(st); }
    else startLoop(st);
    ticker(st, s.phase === 'bets' || s.phase === 'pause' || s.phase === 'crash');
  }

  function drawStatic(st, s) {
    if (s.phase === 'flight') return;
    if (s.phase === 'bets') draw(st, 1, false, s);
  }

  function startLoop(st) {
    if (st.live) return;
    st.live = true;
    if (st.sound) toneStart(st);
    const step = () => {
      st.raf = 0;
      if (!st.live) return;
      const s = cur(st);
      if (!s || s.phase !== 'flight') { st.live = false; return; }
      const m = localM(st, s);
      draw(st, m, true, s);
      big(st, s, m);
      paintBtn(st, s, m);
      miles(st, m);
      toneSet(st, m);
      st.raf = requestAnimationFrame(step);
    };
    st.raf = requestAnimationFrame(step);
  }
  function stopLoop(st) {
    st.live = false;
    if (st.raf) cancelAnimationFrame(st.raf);
    st.raf = 0;
  }
  function ticker(st, on) {
    if (on && !st.iv) {
      st.iv = setInterval(() => {
        const s = cur(st);
        if (!s) return;
        big(st, s, 1);
        paintBtn(st, s, 1);
        const left = tms(s.until) - snow(st);
        if (s.phase === 'bets' && left > 0 && left < 3200) {
          const sec = Math.ceil(left / 1000);
          if (st.tickSec !== sec) { st.tickSec = sec; ping(st, [660], 0.08, 0.05); }
        }
        const card = st.root && st.root.closest('.gtable'), se = card && card.querySelector('.gstatus'), t = status(st.ctx);
        if (se && t && se.textContent !== t) se.textContent = t;
      }, 250);
    } else if (!on && st.iv) { clearInterval(st.iv); st.iv = 0; }
  }

  function layout(root, st) {
    const w = root.getBoundingClientRect().width || window.innerWidth;
    const lay = w >= 900 ? 'wide' : 'vert';
    if (lay !== st.layout) {
      st.layout = lay;
      st.el.box.classList.toggle('L-wide', lay === 'wide');
      st.el.box.classList.toggle('L-vert', lay !== 'wide');
    }
    if (build(root, st)) {
      st.phase = null; st.betSig = null;
      sync(root, st);
      const s = cur(st);
      if (s && s.phase === 'flight') draw(st, localM(st, s), true, s);
    }
  }

  function status(ctx) {
    const st = ctx && ctx._lk, s = st && cur(st);
    if (!s) return 'Лелека';
    const left = Math.max(0, tms(s.until) - snow(st));
    if (s.phase === 'bets') return 'Ставки: ще ' + (isFinite(left) ? Math.ceil(left / 1000) : '…') + ' с';
    if (s.phase === 'flight') return 'Летить ×' + fmtM(localM(st, s));
    return 'Пролетіли на ×' + fmtM(s.crash || s.m || 1);
  }

  function setup(root, ctx) {
    let st = root._lk;
    if (!st) {
      st = root._lk = { root, timers: [], offs: [], seen: new Set(), sound: store.get('snd', false), shown: ctx.shown !== false };
      ctx._lk = st;
      st.ctx = ctx;
      skeleton(root, st);
      st.el.snd.textContent = st.sound ? '🔊' : '🔈';
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => { clearTimeout(st.roT); st.roT = setTimeout(() => layout(root, st), 80); });
        st.ro.observe(root);
      }
    }
    st.ctx = ctx; ctx._lk = st;
    return st;
  }

  HGames.register({
    id: 'lelka',
    added: '2026-10-09',
    icon: ICON,
    seatNames: (i) => 'місце ' + (i + 1),
    pad: {
      a: 'Space',
      hint: '{a} поставити / забрати',
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
    frame(root, ctx) {
      const st = root._lk;
      if (!st || !st.g) return;
      st.ctx = ctx;
      sync(root, st);
    },
    visible(root, ctx, on) {
      const st = root._lk;
      if (!st) return;
      st.shown = on;
      if (on) layout(root, st);
      if (!on) stopLoop(st);
      sync(root, st);
    },
    onKey(e, ctx) {
      if (e.repeat || !(e.code === 'Space' || e.key === ' ')) return false;
      const st = ctx._lk;
      if (!st) return false;
      primary(st.root, st);
      return true;
    },
    status,
    unmount(root) {
      const st = root._lk;
      if (!st) return;
      stopLoop(st);
      ticker(st, false);
      st.timers.forEach(clearTimeout); st.timers = [];
      clearTimeout(st.roT);
      if (st.ro) st.ro.disconnect();
      st.offs.forEach((f) => f()); st.offs = [];
      toneStop(st);
      if (st.ac) { try { st.ac.close(); } catch (e) { /* уже закритий */ } }
      if (st.ctx) st.ctx._lk = null;
      root._lk = null;
    },
    // для перевірок: формула й форматування без DOM
    qa: { fmtM, crashOf, sha256, localM },
  });
})();
