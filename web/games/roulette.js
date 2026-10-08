/*
  Рулетка — європейське колесо (0–36, одне зеро) і Дядько Глек за ним. Дві гри, один модуль:
  `roulette` (спільний стіл, Глек крутить за розкладом) і `roulette-solo` (своє колесо, «Крутити»).
  Правила, гроші й число — на сервері (Impl/Roulette*.cs); тут лише стіл, фішки й наміри.
  Форма виду й дій — docs/games/specs/roulette.md §5–§6, розкладка й анімації — §8.

  Вид: { mode, phase, until, leftMs, phaseMs, spin: { no, n, c, until, leftMs, ms } | null, history: [{ n, c }],
         players: [{ nick, seat, color, here, mine, total, bets: [{ spot, amount }] }], onTable,
         last: { no, n, c, staked, paid, big, results: [{ nick, color, staked, paid, net, hits }] } | null,
         glek: { mood, say, seq }, me: { wallet, onTable, free, canUndo, canRepeat, repeatCost, canDouble, note } | null,
         closed }
  Наміри: act('bet', { spot, amount }), act('undo'), act('clear'), act('repeat'), act('double'), act('spin', { again? }).

  Час — від приходу виду (leftMs), а не з годинника сервера. Анімації — CSS (transition/keyframes), жодного rAF;
  колесо й кулька крутяться переходом transform і лягають рівно на spin.n (§8.5).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="8" cy="8" r="6.6" fill="none" stroke="var(--accent)" stroke-width="1.5"/>'
    + '<path d="M8 2.6v10.8M2.6 8h10.8M4.2 4.2l7.6 7.6M11.8 4.2l-7.6 7.6" stroke="var(--clay)" stroke-width=".9" stroke-linecap="round"/>'
    + '<circle cx="8" cy="8" r="1.7" fill="var(--accent)"/>'
    + '<circle cx="12.4" cy="3.6" r="1.35" fill="var(--clay)" stroke="var(--accent)" stroke-width=".5"/></svg>';

  // ---------------------------------------------------------------------------------------------
  // Колесо й поле (те саме, що RouletteCore на сервері)
  // ---------------------------------------------------------------------------------------------

  const WHEEL = [0, 32, 15, 19, 4, 21, 2, 25, 17, 34, 6, 27, 13, 36, 11, 30, 8, 23, 10, 5, 24, 16, 33, 1, 20, 14,
    31, 9, 22, 18, 29, 7, 28, 12, 35, 3, 26];
  const REDS = new Set([1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36]);
  const colorOf = (n) => (n === 0 ? 'g' : REDS.has(n) ? 'r' : 'b');
  const COLOR_WORD = { r: 'червоне', b: 'чорне', g: 'зеро' };
  const STEP = 360 / 37;
  const PAYS = { straight: 35, split: 17, street: 11, corner: 8, line: 5, column: 2, dozen: 2,
    red: 1, black: 1, even: 1, odd: 1, low: 1, high: 1 };
  const CHIPS = [1, 5, 25, 100, 500, 'all'];
  /// Зовнішні поля в порядку курсора (§8.7): дюжини, рівні гроші, колонки «2:1».
  const OUTSIDE = ['dozen:1', 'dozen:2', 'dozen:3', 'low', 'even', 'red', 'black', 'odd', 'high', 'column:1', 'column:2', 'column:3'];
  const EVEN = ['low', 'even', 'red', 'black', 'odd', 'high'];
  const DIA = '<i class="dia">♦</i>';
  const EVEN_LABEL = { low: '1–18', even: 'Парне', red: DIA + ' Червоне', black: DIA + ' Чорне', odd: 'Непарне', high: '19–36' };
  const EVEN_SHORT = { low: '1–18', even: 'Пар', red: DIA, black: DIA, odd: 'Непар', high: '19–36' };
  /// Кулька: радіус доріжки й радіус кишеньок (частки радіуса колеса) — те саме, що в roulette.css (top 9 %, спуск 12,75 %).
  const TRACK = 0.82, REST = 0.565;
  const SPIN_EASE = 'cubic-bezier(.15,.65,.2,1)';
  const DROP_MS = 1200;
  const SAY_MS = 4000;
  const GHOST_MS = 2600;   // соло: скільки видно стоси минулого кола після того, як кулька лягла
  const MOVE_PX = 12;      // зсув пальця, після якого це вже гортання, а не ставка

  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : v; } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* приватне вікно — не біда */ } },
  };
  const session = {
    get(k) { try { return sessionStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { sessionStorage.setItem(k, v); } catch { /* і так добре */ } },
  };
  const soundOn = () => store.get('roulette_sound', '0') === '1';
  const mod = (a, m) => ((a % m) + m) % m;
  const range = (a, b) => { const out = []; for (let i = a; i <= b; i++) out.push(i); return out; };
  const short = (n) => {
    n = +n || 0;
    if (n >= 100000) return Math.round(n / 1000) + 'к';
    if (n >= 1000) return (Math.round(n / 100) / 10).toString().replace('.', ',') + 'к';
    return String(n);
  };
  const signed = (n) => (n > 0 ? '+' + n : n < 0 ? '−' + Math.abs(n) : '0');

  function typeOf(spot) { return String(spot || '').split(':')[0]; }
  function numsOf(spot) {
    const rest = String(spot || '').split(':')[1];
    return rest ? rest.split('-').map(Number) : [];
  }

  /// Які числа покриває поле.
  function covers(spot) {
    const t = typeOf(spot);
    const k = +numsOf(spot)[0];
    switch (t) {
      case 'column': return range(1, 36).filter((n) => (n - 1) % 3 === k - 1);
      case 'dozen': return range(12 * (k - 1) + 1, 12 * k);
      case 'red': return range(1, 36).filter((n) => REDS.has(n));
      case 'black': return range(1, 36).filter((n) => !REDS.has(n));
      case 'even': return range(1, 36).filter((n) => n % 2 === 0);
      case 'odd': return range(1, 36).filter((n) => n % 2 === 1);
      case 'low': return range(1, 18);
      case 'high': return range(19, 36);
      default: return numsOf(spot);
    }
  }

  /// Людська назва поля (як RouletteCore.Label).
  function label(spot) {
    const t = typeOf(spot), ns = numsOf(spot);
    switch (t) {
      case 'straight': return ns[0] === 0 ? 'Зеро' : 'Число ' + ns[0];
      case 'split': return 'Спліт ' + ns.join('·');
      case 'street': return 'Вулиця ' + ns.join('·');
      case 'corner': return spot === 'corner:0-1-2-3' ? 'Перші чотири' : 'Кут ' + ns.join('·');
      case 'line': return 'Лінія ' + ns[0] + '–' + ns[ns.length - 1];
      case 'column': return 'Колонка ' + ns[0];
      case 'dozen': return 'Дюжина ' + (12 * (ns[0] - 1) + 1) + '–' + 12 * ns[0];
      case 'red': return 'Червоне';
      case 'black': return 'Чорне';
      case 'even': return 'Парне';
      case 'odd': return 'Непарне';
      case 'low': return '1–18';
      case 'high': return '19–36';
      default: return spot;
    }
  }
  const tipOf = (spot) => label(spot) + ' — ' + (PAYS[typeOf(spot)] || 0) + ':1';

  /// Решітка напівкроків §8.3: x 0..5 (колонки), y 0..23 (ряди), y = −1 — клітинка зеро. → ключ поля або null.
  function spotAt(x, y) {
    if (y < 0) return 'straight:0';
    if (x < 0 || x > 5 || y > 23) return null;
    if (y === 0) {
      if (x === 0) return 'corner:0-1-2-3';
      if (x % 2 === 1) return 'split:0-' + ((x - 1) / 2 + 1);
      return x === 2 ? 'street:0-1-2' : 'street:0-2-3';
    }
    const yOdd = y % 2 === 1;
    const r = yOdd ? (y - 1) / 2 : (y - 2) / 2;
    if (x === 0) {
      if (yOdd) return 'street:' + [3 * r + 1, 3 * r + 2, 3 * r + 3].join('-');
      return 'line:' + range(3 * r + 1, 3 * r + 6).join('-');
    }
    const xOdd = x % 2 === 1;
    const c = xOdd ? (x - 1) / 2 : (x - 2) / 2;
    const n = 3 * r + c + 1;
    if (xOdd && yOdd) return 'straight:' + n;
    if (!xOdd && yOdd) return 'split:' + n + '-' + (n + 1);
    if (xOdd && !yOdd) return 'split:' + n + '-' + (n + 3);
    return 'corner:' + [n, n + 1, n + 3, n + 4].join('-');
  }

  /// Точка решітки внутрішнього поля (де лежить стос); зовнішні — null.
  function pointOf(spot) {
    const t = typeOf(spot), ns = numsOf(spot);
    const cell = (n) => ({ c: (n - 1) % 3, r: Math.floor((n - 1) / 3) });
    if (!ns.length || ns.some((n) => !(n >= 0 && n <= 36))) return null;
    if (t === 'straight') {
      if (ns[0] === 0) return { x: 3, y: -1 };
      const { c, r } = cell(ns[0]);
      return { x: 2 * c + 1, y: 2 * r + 1 };
    }
    if (t === 'split') {
      const [a, b] = ns;
      if (a === 0) return { x: 2 * (b - 1) + 1, y: 0 };
      const { c, r } = cell(a);
      return b === a + 1 ? { x: 2 * c + 2, y: 2 * r + 1 } : { x: 2 * c + 1, y: 2 * r + 2 };
    }
    if (t === 'street') {
      if (ns[0] === 0) return { x: ns[2] === 2 ? 2 : 4, y: 0 };
      return { x: 0, y: 2 * cell(ns[0]).r + 1 };
    }
    if (t === 'corner') {
      if (ns[0] === 0) return { x: 0, y: 0 };
      const { c, r } = cell(ns[0]);
      return { x: 2 * c + 2, y: 2 * r + 2 };
    }
    if (t === 'line') return { x: 0, y: 2 * cell(ns[0]).r + 2 };
    return null;
  }

  /// Точка решітки → відсотки всередині шару «зеро + 12 рядів» (обидві орієнтації, §8.3).
  function placeOf(pt, vert) {
    const along = (1 + pt.y / 2) / 13 * 100;   // вісь рядів: зеро — перша 1/13
    const across = pt.x / 6 * 100;             // вісь колонок: 0 — край з боку дюжин/вулиць
    return vert ? { left: across, top: along } : { left: along, top: 100 - across };
  }

  /// Дотик у шарі поля → точка решітки. u — вздовж рядів (0..13, перша одиниця — зеро), v — поперек (0..3).
  function latticeAt(u, v) {
    if (u < 1) return { x: 3, y: -1 };
    const c = Math.max(0, Math.min(2, Math.floor(v)));
    const r = Math.max(0, Math.min(11, Math.floor(u - 1)));
    const fc = v - c, fr = u - 1 - r;
    let x = 2 * c + 1 + (fc < 0.22 ? -1 : fc > 0.78 ? 1 : 0);
    let y = 2 * r + 1 + (fr < 0.22 ? -1 : fr > 0.78 ? 1 : 0);
    if (x > 5) x = 5;     // зовнішній край третьої колонки — ставки нема, лишаємось на числі
    if (y > 23) y = 23;   // край біля «2:1» — так само
    return { x, y };
  }

  /// Кути колеса й кульки для нового кола (§8.5): колесо за годинниковою, кулька проти, кулька над кишенькою n.
  function landAngles(W0, B0, n, delta) {
    const i = WHEEL.indexOf(n);
    const W1 = W0 + 720 + delta;
    const base = B0 - 1800;
    const target = mod(W1 + i * STEP, 360);
    const B1 = base - mod(base - target, 360);
    return { W1, B1, i };
  }

  // ---------------------------------------------------------------------------------------------
  // Стан картки
  // ---------------------------------------------------------------------------------------------

  const roots = new WeakMap();   // ctx → root: onKey і pad приходять із ctx

  function state(root) {
    if (!root._rl) {
      let chip = store.get('roulette_chip', '5');
      if (!CHIPS.some((c) => String(c) === chip)) chip = '5';
      root._rl = {
        ctx: null, el: null, layout: '', W: 0, B: 0, spinNo: null, animNo: null, landAt: 0, landedNo: null,
        timers: [], untilKey: '', untilAt: 0, arcIso: '', pending: [], pid: 0, hover: null, press: null,
        cur: { x: 1, y: 1 }, curOn: false, out: -1, chip, seq: undefined, sayT: 0, snap: null, ghost: null,
        rakedNo: null, lastNo: undefined, busy: {}, nodes: [], tick: 0, ro: null, sitAsked: 0,
      };
    }
    return root._rl;
  }

  function setHtml(el, html) {
    if (el._sig !== html) { el._sig = html; el.innerHTML = html; return true; }
    return false;
  }

  function later(st, fn, ms) {
    const t = setTimeout(() => { st.timers = st.timers.filter((x) => x !== t); fn(); }, Math.max(0, ms));
    st.timers.push(t);
    return t;
  }

  // ---------------------------------------------------------------------------------------------
  // Каркас DOM
  // ---------------------------------------------------------------------------------------------

  function skeleton(root, ctx) {
    const st = state(root);
    if (st.el && root.contains(st.el.box)) return st.el;
    const box = document.createElement('div');
    box.className = 'rl';
    let nums = '';
    for (let n = 1; n <= 36; n++) nums += '<div class="rl-n ' + colorOf(n) + '" data-n="' + n + '"><span>' + n + '</span></div>';
    let outs = '';
    for (let k = 1; k <= 3; k++) outs += '<button type="button" class="rl-o rl-col" data-spot="column:' + k + '"><span>2:1</span><i class="rl-oc"></i></button>';
    for (let d = 1; d <= 3; d++) outs += '<button type="button" class="rl-o rl-doz" data-spot="dozen:' + d + '"><span>' + (12 * (d - 1) + 1) + '–' + 12 * d + '</span><i class="rl-oc"></i></button>';
    for (const e of EVEN) outs += '<button type="button" class="rl-o rl-even rl-' + e + '" data-spot="' + e + '"><span class="lg">' + EVEN_LABEL[e] + '</span><span class="sm">' + EVEN_SHORT[e] + '</span><i class="rl-oc"></i></button>';
    let conf = '';
    for (let i = 0; i < 12; i++) conf += '<i></i>';
    box.innerHTML = ''
      + '<div class="rl-stage">'
      + '<div class="rl-glek" data-mood="idle"><img src="/static/glek.svg" alt="Дядько Глек" draggable="false"><span class="rl-badge"></span><span class="rl-rake" aria-hidden="true">🪵</span></div>'
      + '<div class="rl-say" aria-live="polite"></div>'
      + '<div class="rl-wheelbox"><canvas class="rl-wheel" aria-hidden="true"></canvas>'
      + '<div class="rl-ballrot hide"><div class="rl-drop"><span class="rl-ball"></span></div></div>'
      + '<div class="rl-conf" aria-hidden="true">' + conf + '</div></div>'
      + '<div class="rl-big" aria-live="polite"></div>'
      + '<div class="rl-hist" aria-label="Останні числа"></div>'
      + '</div>'
      + '<div class="rl-main">'
      + '<div class="rl-closed" hidden>Каса зачинена — спробуй трохи згодом</div>'
      + '<div class="rl-board">'
      + '<div class="rl-zero g" data-n="0"><span>0</span></div>' + nums + outs
      + '<div class="rl-hit" aria-label="Поле ставок"></div>'
      + '<div class="rl-chips" aria-hidden="true"></div>'
      + '</div>'
      + '<div class="rl-panel"></div>'
      + '<div class="rlt-legend"></div>'
      + '</div>';
    root.appendChild(box);
    const q = (s) => box.querySelector(s);
    st.el = {
      box, stage: q('.rl-stage'), glek: q('.rl-glek'), gimg: q('.rl-glek img'), badge: q('.rl-badge'), say: q('.rl-say'),
      wheelbox: q('.rl-wheelbox'), wheel: q('.rl-wheel'), ballrot: q('.rl-ballrot'), drop: q('.rl-drop'), conf: q('.rl-conf'),
      big: q('.rl-big'), hist: q('.rl-hist'), closed: q('.rl-closed'), board: q('.rl-board'), hit: q('.rl-hit'),
      chips: q('.rl-chips'), panel: q('.rl-panel'), legend: q('.rlt-legend'),
      cells: {}, outs: {},
    };
    box.querySelectorAll('[data-n]').forEach((e) => { st.el.cells[e.dataset.n] = e; });
    box.querySelectorAll('.rl-o').forEach((e) => { st.el.outs[e.dataset.spot] = e; });
    drawWheel(st.el.wheel, ctx);
    wire(root, st);
    fitLayout(root, st, true);
    if (window.ResizeObserver) {
      // через таймер: синхронна перемальовка в колбеку RO дає «ResizeObserver loop completed…»
      st.ro = new ResizeObserver(() => {
        clearTimeout(st.roT);
        st.roT = setTimeout(() => { if (root._rl && fitLayout(root, st)) paint(root, st.ctx); }, 0);
      });
      st.ro.observe(root);
    }
    return st.el;
  }

  /// Розкладка за шириною картки (§8.2): ≥ 900 — широко, 640…900 — середньо, < 640 — телефон (поле вертикальне).
  function fitLayout(root, st, force) {
    const w = root.getBoundingClientRect().width || root.clientWidth || window.innerWidth;
    if (!w && !force) return false;
    const lay = w >= 900 ? 'wide' : w >= 640 ? 'mid' : 'vert';
    if (lay === st.layout) return false;
    st.layout = lay;
    const box = st.el.box;
    box.classList.toggle('L-wide', lay === 'wide');
    box.classList.toggle('L-mid', lay === 'mid');
    box.classList.toggle('L-vert', lay === 'vert');
    placeBoard(st, lay === 'vert');
    return true;
  }

  function placeBoard(st, vert) {
    const el = st.el;
    el.board.classList.toggle('v', vert);
    el.board.classList.toggle('h', !vert);
    const area = (e, a) => { if (e.style.gridArea !== a) e.style.gridArea = a; };
    area(el.cells[0], vert ? '1 / 2 / 2 / 5' : '1 / 1 / 4 / 2');
    for (let n = 1; n <= 36; n++) {
      const c = (n - 1) % 3, r = Math.floor((n - 1) / 3);
      area(el.cells[n], vert ? (2 + r) + ' / ' + (2 + c) + ' / ' + (3 + r) + ' / ' + (3 + c)
        : (3 - c) + ' / ' + (2 + r) + ' / ' + (4 - c) + ' / ' + (3 + r));
    }
    for (let k = 1; k <= 3; k++) {
      const c = k - 1;
      area(el.outs['column:' + k], vert ? '14 / ' + (2 + c) + ' / 15 / ' + (3 + c) : (3 - c) + ' / 14 / ' + (4 - c) + ' / 15');
      const d0 = 2 + 4 * (k - 1);
      area(el.outs['dozen:' + k], vert ? d0 + ' / 1 / ' + (d0 + 4) + ' / 2' : '4 / ' + d0 + ' / 5 / ' + (d0 + 4));
    }
    EVEN.forEach((e, i) => {
      const s0 = 2 + 2 * i;
      area(el.outs[e], vert ? s0 + ' / 5 / ' + (s0 + 2) + ' / 6' : '5 / ' + s0 + ' / 6 / ' + (s0 + 2));
    });
    area(el.hit, vert ? '1 / 2 / 14 / 5' : '1 / 1 / 4 / 14');
    area(el.chips, vert ? '1 / 2 / 14 / 5' : '1 / 1 / 4 / 14');
  }

  // ---------------------------------------------------------------------------------------------
  // Колесо: статичний малюнок раз на картку (петриківка на ободі й у чаші, глечик-маточина)
  // ---------------------------------------------------------------------------------------------

  function hexRgb(hex) {
    const m = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(String(hex || '').trim());
    if (!m) return [128, 128, 128];
    let h = m[1];
    if (h.length === 3) h = h.split('').map((x) => x + x).join('');
    return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
  }
  /// k > 0 — світліше до білого, k < 0 — темніше до чорного.
  function shade(hex, k) {
    const [r, g, b] = hexRgb(hex);
    const f = (x) => Math.round(k >= 0 ? x + (255 - x) * k : x * (1 + k));
    return 'rgb(' + f(r) + ',' + f(g) + ',' + f(b) + ')';
  }

  /// Зернятко петриківки: кругла голівка в (0,0), хвостик уздовж +x із загином угору.
  function seed(c, x, y, rot, len, w, fill) {
    c.save();
    c.translate(x, y);
    c.rotate(rot);
    c.beginPath();
    c.moveTo(len, -w * 0.7);
    c.quadraticCurveTo(len * 0.45, -w * 1.35, 0, -w);
    c.arc(0, 0, w, -Math.PI / 2, Math.PI / 2, true);
    c.quadraticCurveTo(len * 0.5, w * 0.95, len, -w * 0.7);
    c.closePath();
    c.fillStyle = fill;
    c.fill();
    c.restore();
  }
  function curl(c, x, y, rot, r, stroke, lw) {
    c.save();
    c.translate(x, y);
    c.rotate(rot);
    c.beginPath();
    for (let t = 0; t <= Math.PI * 2.6; t += 0.2) {
      const rr = r * (1 - t / (Math.PI * 3.2));
      const px = Math.cos(t) * rr, py = Math.sin(t) * rr;
      if (t === 0) c.moveTo(px, py); else c.lineTo(px, py);
    }
    c.strokeStyle = stroke;
    c.lineWidth = lw;
    c.lineCap = 'round';
    c.stroke();
    c.restore();
  }
  function dot(c, x, y, r, fill) { c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fillStyle = fill; c.fill(); }
  function ring(c, r0, r1, fill) {
    c.beginPath();
    c.arc(0, 0, r1, 0, Math.PI * 2);
    c.arc(0, 0, r0, 0, Math.PI * 2, true);
    c.fillStyle = fill;
    c.fill('evenodd');
  }

  function drawWheel(cv, ctx) {
    const css = (n, d) => (ctx && ctx.css ? ctx.css(n, d) : d) || d;
    const col = {
      red: css('--rl-red', '#b3261e'), black: css('--rl-black', '#1d1a18'), green: css('--rl-green', '#2f7d3a'),
      wood: css('--rl-wood', '#7a4a26'), o1: css('--rl-orn1', '#c8352b'), o2: css('--rl-orn2', '#e8b83a'),
      o3: css('--rl-orn3', '#4f8a3a'), clay: css('--clay', '#c5763a'), gold: css('--rl-gold', '#d9a441'),
      cream: css('--rl-cream', '#efe3c6'),
    };
    const S = 320, R = S / 2;
    const dpr = Math.min(3, Math.max(1, window.devicePixelRatio || 1));
    const px = Math.round(S * dpr);
    if (cv.width !== px) { cv.width = px; cv.height = px; }
    const c = cv.getContext('2d');
    if (!c) return;
    c.setTransform(dpr, 0, 0, dpr, 0, 0);
    c.clearRect(0, 0, S, S);
    c.translate(R, R);
    const A = Math.PI * 2 / 37;
    const ang = (i) => -Math.PI / 2 + i * A;

    // 1. Дерев'яний обід
    let g = c.createRadialGradient(0, 0, R * 0.84, 0, 0, R);
    g.addColorStop(0, shade(col.wood, -0.3));
    g.addColorStop(0.4, shade(col.wood, 0.12));
    g.addColorStop(0.8, col.wood);
    g.addColorStop(1, shade(col.wood, -0.45));
    dot(c, 0, 0, R * 0.995, g);

    // 2. Чорна смуга з петриківкою: зернята, листочки, ягідки й завитки
    ring(c, R * 0.885, R * 0.972, '#1b1410');
    const rm = R * 0.928;
    const MOTIFS = 16;
    for (let k = 0; k < MOTIFS; k++) {
      const th = k * Math.PI * 2 / MOTIFS;
      c.save();
      c.rotate(th);
      c.translate(0, -rm);   // на смузі, локальна x — уздовж кола (за годинниковою)
      seed(c, -12, 0.6, -0.05, 15, 3.1, col.o1);
      seed(c, 3, -2.2, -0.42, 9, 1.9, col.o2);
      seed(c, 3.5, 2.6, 0.45, 9, 1.8, col.o3);
      dot(c, 14.5, 0, 1.6, col.o2);
      dot(c, 18.5, -2.2, 1, col.o1);
      dot(c, 18.5, 2.2, 1, col.o1);
      c.restore();
    }
    c.lineWidth = 1;
    c.strokeStyle = col.gold;
    c.beginPath(); c.arc(0, 0, R * 0.972, 0, Math.PI * 2); c.stroke();
    c.beginPath(); c.arc(0, 0, R * 0.885, 0, Math.PI * 2); c.stroke();

    // 3. Доріжка кульки — світла полива з ромбиками-відбійниками
    g = c.createRadialGradient(0, 0, R * 0.76, 0, 0, R * 0.885);
    g.addColorStop(0, shade(col.cream, -0.18));
    g.addColorStop(0.55, col.cream);
    g.addColorStop(1, shade(col.cream, -0.12));
    ring(c, R * 0.76, R * 0.885, g);
    for (let k = 0; k < 8; k++) {
      c.save();
      c.rotate(k * Math.PI / 4 + Math.PI / 8);
      c.translate(0, -R * 0.795);
      c.beginPath();
      c.moveTo(0, -4); c.lineTo(2.6, 0); c.lineTo(0, 4); c.lineTo(-2.6, 0); c.closePath();
      c.fillStyle = col.gold;
      c.fill();
      c.restore();
    }

    // 4. Кільце чисел і 5. кишеньки
    const fill = (n) => (n === 0 ? col.green : REDS.has(n) ? col.red : col.black);
    for (let i = 0; i < 37; i++) {
      const a0 = ang(i) - A / 2, a1 = ang(i) + A / 2;
      c.beginPath();
      c.arc(0, 0, R * 0.76, a0, a1);
      c.arc(0, 0, R * 0.635, a1, a0, true);
      c.closePath();
      c.fillStyle = fill(WHEEL[i]);
      c.fill();
      c.beginPath();
      c.arc(0, 0, R * 0.635, a0, a1);
      c.arc(0, 0, R * 0.5, a1, a0, true);
      c.closePath();
      const pg = c.createRadialGradient(0, 0, R * 0.5, 0, 0, R * 0.635);
      pg.addColorStop(0, shade(WHEEL[i] === 0 ? col.green : REDS.has(WHEEL[i]) ? col.red : col.black, -0.55));
      pg.addColorStop(1, shade(WHEEL[i] === 0 ? col.green : REDS.has(WHEEL[i]) ? col.red : col.black, -0.2));
      c.fillStyle = pg;
      c.fill();
    }
    // перегородки кишеньок
    c.strokeStyle = shade(col.gold, 0.25);
    c.lineWidth = 1.1;
    for (let i = 0; i < 37; i++) {
      const a = ang(i) - A / 2;
      c.beginPath();
      c.moveTo(Math.cos(a) * R * 0.5, Math.sin(a) * R * 0.5);
      c.lineTo(Math.cos(a) * R * 0.76, Math.sin(a) * R * 0.76);
      c.stroke();
    }
    c.lineWidth = 1.2;
    c.strokeStyle = col.gold;
    c.beginPath(); c.arc(0, 0, R * 0.76, 0, Math.PI * 2); c.stroke();
    c.beginPath(); c.arc(0, 0, R * 0.635, 0, Math.PI * 2); c.stroke();
    c.beginPath(); c.arc(0, 0, R * 0.5, 0, Math.PI * 2); c.stroke();
    // числа — радіально, низом до центру
    c.fillStyle = '#fff';
    c.textAlign = 'center';
    c.textBaseline = 'middle';
    c.font = '700 11.5px system-ui, -apple-system, "Segoe UI", Roboto, Arial, sans-serif';
    for (let i = 0; i < 37; i++) {
      c.save();
      c.rotate(i * A);
      c.translate(0, -R * 0.698);
      c.fillText(String(WHEEL[i]), 0, 0.5);
      c.restore();
    }

    // 6. Чаша: дерево й петриківська розетка
    g = c.createRadialGradient(-R * 0.12, -R * 0.15, R * 0.05, 0, 0, R * 0.5);
    g.addColorStop(0, shade(col.wood, 0.28));
    g.addColorStop(0.7, col.wood);
    g.addColorStop(1, shade(col.wood, -0.4));
    dot(c, 0, 0, R * 0.5, g);
    const PET = 10;
    for (let k = 0; k < PET; k++) {
      const th = k * Math.PI * 2 / PET;
      c.save();
      c.rotate(th);
      // велика червона пелюстка-зернятко від центру назовні, жовта — поміж, зелений листок і ягідка на краю
      seed(c, R * 0.17, 0, 0, R * 0.22, 4.4, col.o1);
      c.rotate(Math.PI / PET);
      seed(c, R * 0.2, 0, 0.06, R * 0.14, 2.6, col.o2);
      seed(c, R * 0.36, -3, -0.5, R * 0.07, 1.8, col.o3);
      dot(c, R * 0.44, 0, 1.5, col.o2);
      c.restore();
    }
    for (let k = 0; k < 5; k++) curl(c, Math.cos(k * 1.2566 + 0.3) * R * 0.33, Math.sin(k * 1.2566 + 0.3) * R * 0.33, k, 3.6, col.o2, 0.9);

    // 7. Глечик-маточина (згори): тулуб, вінця, ручка, відблиск
    c.save();
    c.beginPath();
    c.ellipse(R * 0.13, 0, R * 0.07, R * 0.035, 0, 0, Math.PI * 2);
    c.lineWidth = 3.4;
    c.strokeStyle = shade(col.clay, -0.45);
    c.stroke();
    c.lineWidth = 2;
    c.strokeStyle = col.clay;
    c.stroke();
    g = c.createRadialGradient(-R * 0.05, -R * 0.05, 1, 0, 0, R * 0.15);
    g.addColorStop(0, shade(col.clay, 0.35));
    g.addColorStop(0.7, col.clay);
    g.addColorStop(1, shade(col.clay, -0.4));
    dot(c, 0, 0, R * 0.15, g);
    c.lineWidth = 1.5;
    c.strokeStyle = shade(col.clay, -0.55);
    c.beginPath(); c.arc(0, 0, R * 0.15, 0, Math.PI * 2); c.stroke();
    // вишита стрічка на тулубі
    c.setLineDash([2.2, 2.2]);
    c.lineWidth = 1.6;
    c.strokeStyle = col.o2;
    c.beginPath(); c.arc(0, 0, R * 0.115, 0, Math.PI * 2); c.stroke();
    c.setLineDash([]);
    dot(c, 0, 0, R * 0.075, shade(col.clay, -0.2));
    c.lineWidth = 1.4;
    c.strokeStyle = shade(col.clay, -0.55);
    c.beginPath(); c.arc(0, 0, R * 0.075, 0, Math.PI * 2); c.stroke();
    dot(c, 0, 0, R * 0.05, '#2a160a');
    c.globalAlpha = 0.45;
    dot(c, -R * 0.06, -R * 0.07, R * 0.025, '#fff3df');
    c.restore();
  }

  // ---------------------------------------------------------------------------------------------
  // Час і фази
  // ---------------------------------------------------------------------------------------------

  function syncTime(st, v) {
    const key = (v.phase || '') + '|' + (v.until || '');
    if (key !== st.untilKey) {
      st.untilKey = key;
      st.untilAt = v.leftMs != null ? Date.now() + Math.max(0, v.leftMs) : 0;
      st.arcIso = st.untilAt ? new Date(st.untilAt).toISOString() : '';
    }
  }
  const leftMs = (st) => (st.untilAt ? Math.max(0, st.untilAt - Date.now()) : 0);

  function myRow(v) { return (v.players || []).find((p) => p.mine) || null; }

  /// Мої ставки цього кола разом із ще не підтвердженими фішками (щоб палець бачив фішку одразу).
  function myBets(st, v) {
    const row = myRow(v);
    const map = new Map();
    for (const b of (row && row.bets) || []) map.set(b.spot, (map.get(b.spot) || 0) + b.amount);
    for (const p of st.pending) map.set(p.spot, (map.get(p.spot) || 0) + p.amount);
    return map;
  }
  const pendingSum = (st) => st.pending.reduce((s, p) => s + p.amount, 0);
  function freeOf(st, v) { return v.me ? Math.max(0, (v.me.free || 0) - pendingSum(st)) : 0; }
  function onTableOf(st, v) { return v.me ? (v.me.onTable || 0) + pendingSum(st) : 0; }

  function canBet(ctx, st, v) {
    if (!ctx.mine || !ctx.playing || !v || v.closed || !v.me) return false;
    if (v.mode === 'solo') return v.phase === 'bets';
    return v.phase === 'idle' || (v.phase === 'bets' && leftMs(st) > 0);
  }

  function landed(st, v) {
    const sp = v && v.spin;
    if (!sp) return false;
    if (st.animNo === sp.no && Date.now() < st.landAt - 40) return false;
    return v.phase !== 'spin' || st.landedNo === sp.no;
  }

  // ---------------------------------------------------------------------------------------------
  // Колесо й кулька: синхрон із сервером
  // ---------------------------------------------------------------------------------------------

  function setRot(el, deg, ms) {
    el.style.transition = ms > 0 ? 'transform ' + Math.round(ms) + 'ms ' + SPIN_EASE : 'none';
    el.style.transform = 'rotate(' + deg.toFixed(3) + 'deg)';
  }

  function restBall(st, n, down) {
    const i = WHEEL.indexOf(n);
    if (i < 0) return;
    st.B = st.W + i * STEP;
    setRot(st.el.wheel, st.W, 0);
    setRot(st.el.ballrot, st.B, 0);
    st.el.drop.style.animation = 'none';
    st.el.drop.classList.toggle('down', down !== false);
    st.el.ballrot.classList.remove('hide');
  }

  function syncWheel(root, st, ctx, v) {
    const sp = v.spin;
    if (!sp) {
      // Нове коло на спільному столі: кулька лежить, де лягла; свіжа картка — у кишеньці останнього числа.
      if (st.spinNo == null && st.restN == null && v.history && v.history.length) { st.restN = v.history[0].n; restBall(st, st.restN); }
      return;
    }
    if (sp.no === st.spinNo) return;
    st.spinNo = sp.no;
    const T = Math.max(0, +sp.leftMs || 0);
    const live = v.phase === 'spin' && T >= 800 && !reduced();
    if (!live) {
      // F5, реконект, пізній вид, «менше руху»: одразу кінцеве положення
      st.animNo = null;
      st.landedNo = sp.no;
      restBall(st, sp.n);
      return;
    }
    const { W1, B1 } = landAngles(st.W, st.B, sp.n, Math.random() * 360);
    st.W = W1;
    st.B = B1;
    st.animNo = sp.no;
    st.landAt = Date.now() + T;
    const el = st.el;
    el.ballrot.classList.remove('hide');
    el.drop.classList.remove('down');
    el.drop.style.animation = 'none';
    void el.drop.offsetWidth;   // перезапуск ключових кадрів на кожне коло
    const dropMs = Math.min(DROP_MS, T);
    el.drop.style.animation = 'rl-drop ' + dropMs + 'ms ease-out ' + Math.max(0, T - dropMs) + 'ms both';
    setRot(el.wheel, W1, T);
    setRot(el.ballrot, B1, T);
    if (ctx.mine) sndRattle(st, T);
    const no = sp.no, n = sp.n;
    later(st, () => {
      if (!root._rl) return;
      st.landedNo = no;
      el.drop.classList.add('down');
      if (st.ctx && st.ctx.mine) { sound(st, 'tsok'); if (n === 0) later(st, () => sound(st, 'bom'), 140); }
      paint(root, st.ctx);
    }, T);
  }

  // ---------------------------------------------------------------------------------------------
  // Глек-круп'є
  // ---------------------------------------------------------------------------------------------

  const BADGE = { idle: '👋', hurry: '⏳', call: '', dance: '💃', laugh: '😂', clap: '👏', rake: '', sigh: '😮‍💨', doze: '💤' };

  function syncGlek(root, st, ctx, v) {
    const g = v.glek || {};
    // розрахунок уже на дроті, а кулька ще котиться — настрій і «17, червоне!» Глек скаже, коли вона ляже
    if (v.spin && v.phase !== 'spin' && st.animNo === v.spin.no && !landed(st, v)) return;
    const mood = BADGE[g.mood] != null ? g.mood : 'idle';
    const el = st.el;
    if (el.glek.dataset.mood !== mood) el.glek.dataset.mood = mood;
    const b = BADGE[mood];
    if (el.badge.textContent !== b) el.badge.textContent = b;
    if (g.seq === st.seq) return;
    const first = st.seq === undefined;
    st.seq = g.seq;
    const key = 'roulette_seq:' + ((ctx.room && ctx.room.id) || '');
    const seen = first && session.get(key) === String(g.seq);
    session.set(key, String(g.seq));
    if (seen) return;   // F5: минулу анімацію й хмарку не повторюємо
    // хмарка — 4 с від нового seq
    if (g.say) {
      el.say.textContent = g.say;
      el.say.classList.add('on');
      clearTimeout(st.sayT);
      st.sayT = setTimeout(() => { if (root._rl) el.say.classList.remove('on'); }, SAY_MS);
    }
    if (reduced()) return;
    // одноразова анімація настрою — раз на seq
    el.glek.classList.remove('go');
    void el.glek.offsetWidth;
    el.glek.classList.add('go');
    if (mood === 'dance') {
      const R = (el.wheelbox.clientWidth || 200) / 2;
      el.conf.querySelectorAll('i').forEach((p, i) => {
        const a = (i / 12) * Math.PI * 2 + Math.random() * 0.4;
        const d = R * (0.45 + Math.random() * 0.5);
        p.style.setProperty('--dx', Math.round(Math.cos(a) * d) + 'px');
        p.style.setProperty('--dy', Math.round(Math.sin(a) * d - R * 0.25) + 'px');
        p.style.setProperty('--r', Math.round(Math.random() * 720 - 360) + 'deg');
        p.style.animationDelay = (i % 4) * 90 + 'ms';
        p.className = 'g' + [1, 5, 25, 100, 500][i % 5];
      });
      el.conf.classList.remove('on');
      void el.conf.offsetWidth;
      el.conf.classList.add('on');
      later(st, () => el.conf.classList.remove('on'), 3400);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Малювання
  // ---------------------------------------------------------------------------------------------

  function paint(root, ctx) {
    if (!ctx) return;
    const st = state(root);
    st.ctx = ctx;
    const el = skeleton(root, ctx);
    const v = ctx.view;
    if (!v || !v.mode) {
      setHtml(el.panel, '<div class="rl-info muted">Глек розкладає сукно…</div>');
      return;
    }
    el.box.classList.toggle('solo', v.mode === 'solo');
    el.box.classList.toggle('watch', !ctx.mine);
    syncTime(st, v);
    syncWheel(root, st, ctx, v);
    syncGlek(root, st, ctx, v);
    syncGhost(root, st, v);
    el.closed.hidden = !v.closed;
    paintStage(st, ctx, v);
    paintBoard(root, st, ctx, v);
    paintPanel(st, ctx, v);
    paintLegend(st, ctx, v);
    chime(st, ctx, v);
  }

  /// Соло: розрахунок прибирає ставки зі столу разом із фазою — стоси минулого кола показуємо ще мить самі.
  function syncGhost(root, st, v) {
    if (v.mode !== 'solo') { st.ghost = null; return; }
    if (v.phase === 'spin' && v.spin) {
      const row = myRow(v);
      st.snap = { no: v.spin.no, players: row ? [row] : [] };
      return;
    }
    if (st.snap && v.last && v.last.no === st.snap.no && st.animNo === st.snap.no) {
      const until = Math.max(Date.now(), st.landAt) + GHOST_MS;
      st.ghost = { no: st.snap.no, players: st.snap.players, until };
      st.snap = null;
      later(st, () => { if (root._rl) paint(root, st.ctx); }, until - Date.now() + 30);
    }
  }

  /// Що показати як розрахунок на полі: { no, n, players, hits: Map(nick → Set(spot)) } або null.
  function resultOf(st, v) {
    const last = v.last;
    if (!last || !landed(st, v)) return null;
    let players = null;
    if (v.mode === 'table' && v.phase === 'result' && v.spin && last.no === v.spin.no) players = v.players || [];
    else if (v.mode === 'solo' && st.ghost && st.ghost.no === last.no && Date.now() < st.ghost.until && !st.pending.length
      && !((myRow(v) || {}).bets || []).length) players = st.ghost.players;
    if (!players) return null;
    const hits = new Map();
    for (const r of last.results || []) hits.set(r.nick, new Set(r.hits || []));
    return { no: last.no, n: last.n, players, hits };
  }

  function numBall(n, c, cls) {
    return '<b class="rl-num ' + (c || colorOf(n)) + (cls ? ' ' + cls : '') + '">' + n + '</b>';
  }

  function paintStage(st, ctx, v) {
    const el = st.el;
    // історія: 20 кружечків, найновіше ліворуч
    let hist = (v.history || []).slice(0, 20);
    // вид розрахунку прийшов, а кулька ще котиться (≤ тик) — нове число не спойлеримо
    if (v.spin && v.phase !== 'spin' && st.animNo === v.spin.no && !landed(st, v) && hist.length && hist[0].n === v.spin.n) hist = hist.slice(1);
    setHtml(el.hist, hist.length ? hist.map((h, i) => numBall(h.n, h.c, i === 0 ? 'new' : '')).join('')
      : '<span class="muted small">Тут будуть числа, що випали</span>');
    // велике число — коли кулька лягла
    let big = '';
    const sp = v.spin;
    const show = sp && landed(st, v) && (v.phase === 'result' || v.mode === 'solo');
    if (show) {
      const word = sp.n === 0 ? 'зеро!' : COLOR_WORD[sp.c || colorOf(sp.n)];
      big = '<div class="rl-bigline">' + numBall(sp.n, sp.c, 'xl') + '<span>' + word + '</span></div>';
      const last = v.last && v.last.no === sp.no ? v.last : null;
      if (last && (last.results || []).length) {
        const rows = last.results.slice(0, v.mode === 'solo' ? 1 : 6).map((r) => {
          const me = ctx.me && r.nick === ctx.me.nick;
          return '<span class="rl-res ' + (r.net > 0 ? 'up' : r.net < 0 ? 'down' : '') + (me ? ' me' : '') + '">'
            + '<i class="rl-dot" style="--pc:var(--rl-p' + ((r.color | 0) & 7) + ')"></i>'
            + (v.mode === 'solo' ? 'ти' : ctx.esc(r.nick)) + ' <b>' + signed(r.net) + '</b></span>';
        });
        big += '<div class="rl-sum">' + rows.join('') + '</div>';
      }
    }
    setHtml(el.big, big);
  }

  function chipCls(amount) {
    return amount >= 500 ? 'g500' : amount >= 100 ? 'g100' : amount >= 25 ? 'g25' : amount >= 5 ? 'g5' : 'g1';
  }
  function shardHtml(amount, depth) {
    const g = chipCls(amount);
    let s = '';
    for (let i = depth - 1; i >= 0; i--) s += '<i class="rl-sh ' + g + '" style="--d:' + i + '"></i>';
    return s;
  }

  function paintBoard(root, st, ctx, v) {
    const el = st.el;
    const vert = st.layout === 'vert';
    const res = resultOf(st, v);
    // стоси: поле → [{ color, amount, cls }]
    const bySpot = new Map();
    const add = (spot, item) => { if (!bySpot.has(spot)) bySpot.set(spot, []); bySpot.get(spot).push(item); };
    const players = res ? res.players : (v.players || []);
    for (const p of players) {
      const mine = !!p.mine;
      let bets = p.bets || [];
      if (mine && !res) bets = Array.from(myBets(st, v), ([spot, amount]) => ({ spot, amount }));
      const hits = res ? (res.hits.get(p.nick) || new Set()) : null;
      for (const b of bets) {
        if (!(b.amount > 0)) continue;
        add(b.spot, {
          color: (p.color | 0) & 7, amount: b.amount, mine, gone: p.here === false,
          win: hits ? hits.has(b.spot) : false, lose: hits ? !hits.has(b.spot) : false,
        });
      }
    }
    if (ctx.mine && !res && !myRow(v) && st.pending.length) {
      for (const p of st.pending) add(p.spot, { color: 0, amount: p.amount, mine: true });
    }
    for (const list of bySpot.values()) list.sort((a, b) => (b.mine - a.mine));
    const raked = res && st.rakedNo === res.no;
    let inner = '';
    const outer = {};
    for (const [spot, list] of bySpot) {
      const pt = pointOf(spot);
      let html = '';
      const shown = list.slice(0, 4);
      shown.forEach((it, k) => {
        let ox = 0, oy = 0;
        if (shown.length > 1) {
          const a = -Math.PI / 2 + k * Math.PI * 2 / shown.length;
          ox = Math.round(Math.cos(a) * 6); oy = Math.round(Math.sin(a) * 6);
        }
        const depth = it.amount >= 100 ? 3 : it.amount >= 10 ? 2 : 1;
        const cls = 'rl-stk' + (it.mine ? ' mine' : it.gone ? ' gone' : ' other') + (it.win ? ' win' : '')
          + (it.lose ? (raked ? ' lose raked' : ' lose') : '');
        const pos = pt ? placeOf(pt, vert) : null;
        const at = (pos ? 'left:' + pos.left.toFixed(3) + '%;top:' + pos.top.toFixed(3) + '%;' : '')
          + '--ox:' + ox + 'px;--oy:' + oy + 'px';
        html += '<span class="' + cls + '" style="' + at + ';--pc:var(--rl-p' + it.color + ')">' + shardHtml(it.amount, depth)
          + '<b>' + short(it.amount) + '</b></span>';
        if (it.win) {
          // стос виплати поруч: чистий виграш (ставка × k), сама ставка теж повертається
          const k2 = PAYS[typeOf(spot)] || 0;
          html += '<span class="rl-stk pay" style="' + at + '">' + shardHtml(it.amount * k2, 2) + '<b>+' + short(it.amount * k2) + '</b></span>';
        }
      });
      if (list.length > 4) {
        const pos = pt ? placeOf(pt, vert) : null;
        html += '<span class="rl-more" style="' + (pos ? 'left:' + pos.left.toFixed(3) + '%;top:' + pos.top.toFixed(3) + '%' : '') + '">+' + (list.length - 4) + '</span>';
      }
      if (pt) inner += html; else outer[spot] = html;
    }
    // наведення / дотик / курсор: точка прицілу на решітці
    const aim = st.press ? st.press.spot : st.hover;
    const curSpot = st.curOn ? (st.out >= 0 ? OUTSIDE[st.out] : spotAt(st.cur.x, st.cur.y)) : null;
    for (const [spot, cls] of [[aim, 'rl-aim'], [curSpot, 'rl-cur']]) {
      const pt = spot && pointOf(spot);
      if (!pt) continue;
      const pos = placeOf(pt, vert);
      inner += '<span class="' + cls + '" style="left:' + pos.left.toFixed(3) + '%;top:' + pos.top.toFixed(3) + '%"></span>';
    }
    setHtml(el.chips, inner);
    for (const spot of OUTSIDE) {
      const o = el.outs[spot];
      setHtml(o.querySelector('.rl-oc'), outer[spot] || '');
      o.classList.toggle('cur', curSpot === spot);
    }
    // підсвітка покритих чисел і зовнішнього поля
    const lit = new Set(aim ? covers(aim) : (curSpot ? covers(curSpot) : []));
    const winN = res ? res.n : (v.mode === 'solo' && v.spin && landed(st, v) && !st.pending.length && !((myRow(v) || {}).bets || []).length ? v.spin.n : null);
    for (let n = 0; n <= 36; n++) {
      const c = el.cells[n];
      c.classList.toggle('hl', lit.has(n));
      c.classList.toggle('win', winN === n);
    }
    for (const spot of OUTSIDE) el.outs[spot].classList.toggle('hl', aim === spot);
    el.board.classList.toggle('off', !canBet(ctx, st, v));
    // результат: програні стоси їдуть до Глека (раз на коло)
    if (res && !raked && st.rakeQ !== res.no && !reduced()) rake(root, st, res.no);
  }

  function rake(root, st, no) {
    st.rakeQ = no;
    const el = st.el;
    later(st, () => {
      if (!root._rl) return;
      st.rakedNo = no;   // далі перемальовка ставить «raked» — стос уже в Глека
      const gr = el.gimg.getBoundingClientRect();
      const gx = gr.left + gr.width / 2, gy = gr.top + gr.height * 0.6;
      el.chips.querySelectorAll('.rl-stk.lose').forEach((s) => {
        const r = s.getBoundingClientRect();
        s.style.setProperty('--rx', Math.round(gx - (r.left + r.width / 2)) + 'px');
        s.style.setProperty('--ry', Math.round(gy - (r.top + r.height / 2)) + 'px');
        s.classList.add('raking');
      });
      el.box.querySelectorAll('.rl-oc .rl-stk.lose').forEach((s) => {
        const r = s.getBoundingClientRect();
        s.style.setProperty('--rx', Math.round(gx - (r.left + r.width / 2)) + 'px');
        s.style.setProperty('--ry', Math.round(gy - (r.top + r.height / 2)) + 'px');
        s.classList.add('raking');
      });
    }, 900);
  }

  function chipAmount(st, v) {
    const free = freeOf(st, v);
    if (st.chip === 'all') return free;
    const d = +st.chip || 1;
    if (d <= free) return d;
    // обрана фішка завелика — найбільша, що влазить
    for (let i = CHIPS.length - 2; i >= 0; i--) if (CHIPS[i] <= free) return CHIPS[i];
    return 0;
  }

  function paintPanel(st, ctx, v) {
    const el = st.el;
    const solo = v.mode === 'solo';
    let html = '';
    if (!ctx.mine) {
      // глядач: «Сісти за стіл» (каркас посеред партії своєї кнопки не дає)
      const r = ctx.room || {};
      const seats = r.seats || [];
      let free = 0;
      for (let i = 0; i < (seats.length || r.maxPlayers || 0); i++) {
        const s = seats[i];
        if (!(typeof s === 'string' ? s : s && s.nick)) free++;
      }
      html = '<div class="rl-tip">' + (st.press || st.hover ? ctx.esc(tipOf((st.press && st.press.spot) || st.hover)) : 'Ставлять ті, хто сидить. Глядачам — найкращі місця') + '</div>'
        + '<div class="rl-sit">' + (solo ? '' : free > 0 && ctx.playing
          ? '<button type="button" class="primary" data-do="sit">🪑 Сісти за стіл</button>'
          : '<span class="muted">Місць нема — дивись</span>') + '</div>';
      setHtml(el.panel, html);
      el.panel.classList.remove('on');
      return;
    }
    const me = v.me || {};
    const can = canBet(ctx, st, v);
    const free = freeOf(st, v), onTable = onTableOf(st, v);
    const flash = st.flash && Date.now() < st.flash.until ? st.flash.text : '';
    const tip = st.press ? tipOf(st.press.spot) : st.hover ? tipOf(st.hover) : flash ? flash
      : st.curOn ? tipOf(st.out >= 0 ? OUTSIDE[st.out] : spotAt(st.cur.x, st.cur.y) || 'straight:0')
        : v.closed ? 'Каса зачинена — спробуй трохи згодом'
          : can ? (ctx.ui.coarse() ? 'Торкнись поля — фішка ляже. Край клітинки — спліт, ріжок — кут' : 'Клікни поле — фішка ляже. Край клітинки — спліт, ріжок — кут')
            : v.phase === 'spin' ? 'Ставки зроблено — колесо крутиться' : v.phase === 'result' ? 'Глек рахує виграші — ставки за мить' : '';
    let chips = '';
    for (const c of CHIPS) {
      const amt = c === 'all' ? free : c;
      const off = !can || (c === 'all' ? free < 1 : c > free);
      const sel = String(c) === st.chip;
      chips += '<button type="button" class="rlt-chip' + (sel ? ' sel' : '') + '" data-chip="' + c + '"' + (off ? ' disabled' : '')
        + ' aria-pressed="' + sel + '" title="' + (c === 'all' ? 'Усе вільне: ' + free : c + (c === 1 ? ' черепок' : ' черепків')) + '">'
        + '<i class="rl-sh ' + (c === 'all' ? 'gall' : chipCls(c)) + '"></i><b>' + (c === 'all' ? 'Усе' : short(amt)) + '</b></button>';
    }
    const b = (act, text, on, cls, title) => '<button type="button" class="' + (cls || 'ghost') + '" data-act="' + act + '"'
      + (on && !st.busy[act] ? '' : ' disabled') + (title ? ' title="' + title + '"' : '') + '>' + text + '</button>';
    const mine = onTable > 0;
    let btns = b('undo', '↶ <span class="lg">Відміна</span>', can && (me.canUndo || st.pending.length > 0), 'ghost', 'Відміна (Z)')
      + b('clear', '✕ <span class="lg">Зняти все</span>', can && mine, 'ghost', 'Зняти все (X)')
      + b('repeat', '🔁 <span class="lg">Повторити</span>' + (me.repeatCost ? ' · ' + me.repeatCost : ''), can && me.canRepeat, 'ghost', 'Повторити минуле коло (R)')
      + b('double', '×2', can && (me.canDouble || mine), 'ghost', 'Подвоїти (D)');
    if (solo) {
      const again = !mine && me.canRepeat;
      btns += again
        ? b('again', '🔁 Ще раз і крутити · ' + (me.repeatCost || 0), can, 'primary rl-go', 'Повторити й крутити (S)')
        : b('spin', '🎡 Крутити', can && mine && !st.pending.length, 'primary rl-go', 'Крутити (S)');
    }
    const clock = !solo && v.phase === 'bets' && st.untilAt ? '<div class="rl-clock"></div>' : '';
    let info = 'На столі <b>' + onTable + '</b> · вільних <b>' + free + '</b>';
    if (!solo && mine) info += '<span class="rl-stay">Твої ставки зіграють, навіть якщо встанеш</span>';
    html = '<div class="rl-tip">' + ctx.esc(tip) + '</div>'
      + '<div class="rl-row">' + clock + '<div class="rl-chiprow" role="radiogroup" aria-label="Фішка">' + chips + '</div></div>'
      + '<div class="rl-btns">' + btns + '</div>'
      + '<div class="rl-info"><span>' + info + '</span>' + soundBtn() + '</div>'
      + (me.note ? '<div class="rlt-note">' + ctx.esc(me.note) + '</div>' : '');
    setHtml(el.panel, html);
    el.panel.classList.add('on');
    const host = el.panel.querySelector('.rl-clock');
    if (host && st.arcIso) ctx.ui.timerArc(host, st.arcIso, v.phaseMs || 25000);
  }

  function soundBtn() {
    const on = soundOn();
    return '<button type="button" class="ghost rl-snd" data-do="sound" aria-pressed="' + on + '">' + (on ? '🔊' : '🔈')
      + ' звук: ' + (on ? 'увімк' : 'вимк') + '</button>';
  }

  function paintLegend(st, ctx, v) {
    const nets = new Map();
    if (v.last) for (const r of v.last.results || []) nets.set(r.nick, r.net);
    const showNet = !(v.spin && v.last && v.last.no === v.spin.no && !landed(st, v));
    const rows = (v.players || []).map((p) => {
      const me = p.mine;
      const net = nets.get(p.nick);
      const total = me ? onTableOf(st, v) : p.total || 0;
      return '<li class="' + (me ? 'me' : '') + (p.here === false ? ' gone' : '') + '">'
        + '<i class="rl-dot" style="--pc:var(--rl-p' + ((p.color | 0) & 7) + ')"></i>'
        + '<span class="nick">' + ctx.esc(p.nick) + (me ? ' <small>(ти)</small>' : '') + (p.here === false ? ' <small>встав</small>' : '') + '</span>'
        + '<span class="on">на столі ' + total + '</span>'
        + (showNet && net != null ? '<span class="net ' + (net > 0 ? 'up' : net < 0 ? 'down' : '') + '" title="минуле коло">' + signed(net) + '</span>' : '<span class="net"></span>')
        + '</li>';
    });
    setHtml(st.el.legend, v.mode === 'solo' || !rows.length ? '' : '<ul>' + rows.join('') + '</ul>');
  }

  // ---------------------------------------------------------------------------------------------
  // Наміри
  // ---------------------------------------------------------------------------------------------

  function bet(root, ctx, spot) {
    const st = state(root);
    const v = ctx.view;
    if (!spot || !v) return;
    if (!ctx.mine) {
      if (Date.now() - st.sitAsked > 4000) { st.sitAsked = Date.now(); ctx.toast(v.mode === 'solo' ? 'Це не твоє колесо' : 'Сядь за стіл, щоб ставити', 'wait'); }
      return;
    }
    if (!canBet(ctx, st, v)) return;
    const amount = chipAmount(st, v);
    if (amount < 1) { ctx.toast('Бракує черепків: вільних ' + freeOf(st, v), 'err'); return; }
    const p = { id: ++st.pid, spot, amount, acked: false };
    st.pending.push(p);
    // на дотику наведення нема — мітка поставленого поля висить ще мить
    st.flash = { text: tipOf(spot) + ' · +' + amount, until: Date.now() + 1800 };
    later(st, () => { if (root._rl) paint(root, st.ctx); }, 1850);
    sound(st, 'chip');
    paint(root, ctx);
    Promise.resolve(ctx.act('bet', { spot, amount })).then((r) => {
      if (!root._rl) return;
      if (r && r.ok === false) st.pending = st.pending.filter((x) => x !== p);
      else p.acked = true;
      paint(root, st.ctx);
    }, () => { st.pending = st.pending.filter((x) => x !== p); if (root._rl) paint(root, st.ctx); });
  }

  function command(root, ctx, act) {
    const st = state(root);
    const v = ctx.view;
    if (!v || !ctx.mine || st.busy[act]) return;
    if (!canBet(ctx, st, v)) return;
    if (act === 'undo' && st.pending.length && !st.pending[st.pending.length - 1].acked) return;   // фішка ще летить
    const name = act === 'again' ? 'spin' : act;
    const payload = act === 'again' ? { again: true } : act === 'spin' ? {} : undefined;
    st.busy[act] = true;
    paint(root, ctx);
    const done = () => { st.busy[act] = false; if (root._rl) paint(root, st.ctx); };
    Promise.resolve(ctx.act(name, payload)).then(done, done);
  }

  function pickChip(root, ctx, c) {
    const st = state(root);
    st.chip = String(c);
    store.set('roulette_chip', st.chip);
    paint(root, ctx);
  }
  function stepChip(root, ctx, d) {
    const st = state(root);
    const i = CHIPS.findIndex((c) => String(c) === st.chip);
    pickChip(root, ctx, CHIPS[mod((i < 0 ? 1 : i) + d, CHIPS.length)]);
  }

  // ---------------------------------------------------------------------------------------------
  // Дотик, миша, клавіші
  // ---------------------------------------------------------------------------------------------

  function spotAtPointer(st, e) {
    const r = st.el.hit.getBoundingClientRect();
    if (!r.width || !r.height) return null;
    const vert = st.layout === 'vert';
    const u = vert ? (e.clientY - r.top) / r.height * 13 : (e.clientX - r.left) / r.width * 13;
    const vv = vert ? (e.clientX - r.left) / r.width * 3 : (r.bottom - e.clientY) / r.height * 3;
    if (u < 0 || u > 13 || vv < 0 || vv > 3) return null;
    const p = latticeAt(u, vv);
    return spotAt(p.x, p.y);
  }

  function wire(root, st) {
    const el = st.el;
    const ctx = () => st.ctx;
    // Звук увімкнули ще до F5 — AudioContext оживає з першим жестом на столі.
    el.box.addEventListener('pointerdown', () => { if (soundOn()) audio(); }, { passive: true });
    el.box.addEventListener('click', (e) => {
      const t = e.target.closest('button');
      if (!t || t.disabled || !el.box.contains(t)) return;
      const c = ctx();
      if (!c) return;
      if (t.dataset.spot) { bet(root, c, t.dataset.spot); return; }
      if (t.dataset.chip) { pickChip(root, c, t.dataset.chip); return; }
      if (t.dataset.act) { command(root, c, t.dataset.act); return; }
      if (t.dataset.do === 'sound') {
        store.set('roulette_sound', soundOn() ? '0' : '1');
        if (soundOn()) { audio(); sound(st, 'chip'); }
        paint(root, c);
        return;
      }
      if (t.dataset.do === 'sit' && c.room) { t.disabled = true; HGames.call('JoinRoom', c.room.id).finally(() => { t.disabled = false; }); }
    });
    // зовнішні поля: наведення — підсвітка й мітка
    el.box.addEventListener('pointerover', (e) => {
      const o = e.target.closest && e.target.closest('.rl-o');
      if (!o || e.pointerType !== 'mouse') return;
      st.hover = o.dataset.spot;
      paint(root, ctx());
    });
    el.box.addEventListener('pointerout', (e) => {
      const o = e.target.closest && e.target.closest('.rl-o');
      if (!o || (e.relatedTarget && o.contains(e.relatedTarget))) return;
      if (st.hover === o.dataset.spot) { st.hover = null; paint(root, ctx()); }
    });
    // внутрішнє поле: решітка влучань
    const hit = el.hit;
    hit.addEventListener('pointerdown', (e) => {
      if (e.button > 0) return;
      const spot = spotAtPointer(st, e);
      if (!spot) return;
      st.press = { id: e.pointerId, x: e.clientX, y: e.clientY, spot };
      paint(root, ctx());
    });
    hit.addEventListener('pointermove', (e) => {
      if (st.press && st.press.id === e.pointerId) {
        if (Math.hypot(e.clientX - st.press.x, e.clientY - st.press.y) > MOVE_PX) { st.press = null; paint(root, ctx()); }
        return;
      }
      if (e.pointerType !== 'mouse') return;
      const spot = spotAtPointer(st, e);
      if (spot !== st.hover) { st.hover = spot; paint(root, ctx()); }
    });
    hit.addEventListener('pointerup', (e) => {
      const p = st.press;
      if (!p || p.id !== e.pointerId) return;
      st.press = null;
      if (Math.hypot(e.clientX - p.x, e.clientY - p.y) <= MOVE_PX && HGames.ui.human(e)) bet(root, ctx(), p.spot);
      else paint(root, ctx());
    });
    const cancel = () => { if (st.press) { st.press = null; paint(root, ctx()); } };
    hit.addEventListener('pointercancel', cancel);
    hit.addEventListener('pointerleave', (e) => {
      if (e.pointerType === 'mouse' && (st.hover || st.press)) { st.hover = null; st.press = null; paint(root, ctx()); }
    });
  }

  /// Стрілка → зсув курсора по решітці з урахуванням орієнтації поля.
  function moveCursor(root, ctx, code) {
    const st = state(root);
    const vert = st.layout === 'vert';
    if (!st.curOn) { st.curOn = true; paint(root, ctx); return; }
    if (st.out >= 0) {
      if (code === 'ArrowLeft') st.out = mod(st.out - 1, OUTSIDE.length);
      else if (code === 'ArrowRight') st.out = mod(st.out + 1, OUTSIDE.length);
      else if (code === 'ArrowUp') st.out = -1;
      paint(root, ctx);
      return;
    }
    let { x, y } = st.cur;
    // поле → дx/дy за напрямком екрана
    const d = vert
      ? { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[code]
      : { ArrowLeft: [0, -1], ArrowRight: [0, 1], ArrowUp: [1, 0], ArrowDown: [-1, 0] }[code];
    if (!d) return;
    // з краю поля вниз — у ряд зовнішніх (вертикально — з останнього ряду, горизонтально — з нижнього краю)
    if (code === 'ArrowDown' && ((vert && y >= 23) || (!vert && x <= 0))) { st.out = 0; paint(root, ctx); return; }
    if (y < 0) { if (d[1] > 0) y = 0; }
    else {
      x = Math.max(0, Math.min(5, x + d[0]));
      y = Math.max(-1, Math.min(23, y + d[1]));
    }
    st.cur = { x, y };
    paint(root, ctx);
  }

  function cursorSpot(st) {
    if (!st.curOn) return null;
    return st.out >= 0 ? OUTSIDE[st.out] : spotAt(st.cur.x, st.cur.y);
  }

  function onKey(e, ctx) {
    const root = roots.get(ctx);
    if (!root || !root._rl) return false;
    const st = root._rl;
    const v = ctx.view;
    if (!v || !ctx.mine) return false;
    const code = e.code || '';
    if (soundOn() && !actx) audio();
    if (/^Arrow/.test(code)) { moveCursor(root, ctx, code); return true; }
    if (code === 'Enter' || code === 'NumpadEnter' || code === 'Space') {
      if (!e.repeat) { if (!st.curOn) { st.curOn = true; paint(root, ctx); } else bet(root, ctx, cursorSpot(st)); }
      return true;
    }
    const dg = /^(?:Digit|Numpad)([1-6])$/.exec(code);
    if (dg) { pickChip(root, ctx, CHIPS[+dg[1] - 1]); return true; }
    if (e.repeat) return /^Key[ZXRDS]$|^Backspace$/.test(code);
    if (code === 'Backspace' || code === 'KeyZ') { command(root, ctx, 'undo'); return true; }
    if (code === 'KeyX') { command(root, ctx, 'clear'); return true; }
    if (code === 'KeyR') { command(root, ctx, 'repeat'); return true; }
    if (code === 'KeyD') { command(root, ctx, 'double'); return true; }
    if (code === 'KeyS' && v.mode === 'solo') { soloGo(root, ctx); return true; }
    return false;
  }

  function soloGo(root, ctx) {
    const st = state(root);
    const v = ctx.view || {};
    const mine = onTableOf(st, v) > 0;
    command(root, ctx, !mine && v.me && v.me.canRepeat ? 'again' : 'spin');
  }

  // ---------------------------------------------------------------------------------------------
  // Звук (WebAudio-синтез, тихо, типово вимкнено; контекст — лише після жесту)
  // ---------------------------------------------------------------------------------------------

  let actx = null;
  function audio() {
    if (!actx) { try { actx = new (window.AudioContext || window.webkitAudioContext)(); } catch { actx = null; } }
    if (actx && actx.state === 'suspended') actx.resume().catch(() => {});
    return actx;
  }

  function bus(st, a, gain) {
    const g = a.createGain();
    g.gain.value = gain;
    g.connect(a.destination);
    st.nodes.push(g);
    if (st.nodes.length > 40) st.nodes.splice(0, st.nodes.length - 40);
    return g;
  }

  function tone(a, out, f, t, dur, vol, type) {
    const o = a.createOscillator(), g = a.createGain();
    o.type = type || 'sine';
    o.frequency.setValueAtTime(f, t);
    g.gain.setValueAtTime(0.0001, t);
    g.gain.exponentialRampToValueAtTime(vol, t + 0.006);
    g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
    o.connect(g); g.connect(out);
    o.start(t); o.stop(t + dur + 0.02);
    return o;
  }

  function sound(st, kind) {
    if (!soundOn() || !actx || !st) return;
    const a = actx, t = a.currentTime;
    try {
      const out = bus(st, a, 1);
      if (kind === 'chip') {
        // клац черепка: два глиняні цокання
        tone(a, out, 1500, t, 0.035, 0.05, 'triangle');
        tone(a, out, 980, t + 0.03, 0.04, 0.035, 'triangle');
      } else if (kind === 'tsok') {
        tone(a, out, 1900, t, 0.03, 0.06, 'square');
        tone(a, out, 620, t + 0.005, 0.08, 0.05, 'sine');
      } else if (kind === 'win') {
        [660, 880, 1175].forEach((f, i) => tone(a, out, f, t + i * 0.11, 0.22, 0.06, 'sine'));
      } else if (kind === 'bom') {
        const o = tone(a, out, 110, t, 0.6, 0.08, 'sine');
        o.frequency.exponentialRampToValueAtTime(70, t + 0.5);
      }
    } catch { /* звук — прикраса */ }
  }

  /// Торохтіння кульки: шум крізь смуговий фільтр і клаци, що рідшають до кінця.
  function sndRattle(st, T) {
    if (!soundOn() || !actx) return;
    const a = actx, t0 = a.currentTime, dur = T / 1000;
    try {
      const out = bus(st, a, 1);
      const len = Math.max(1, Math.floor(a.sampleRate * Math.min(dur, 10)));
      const buf = a.createBuffer(1, len, a.sampleRate);
      const d = buf.getChannelData(0);
      for (let i = 0; i < len; i++) d[i] = Math.random() * 2 - 1;
      const src = a.createBufferSource();
      src.buffer = buf;
      const bp = a.createBiquadFilter();
      bp.type = 'bandpass';
      bp.frequency.setValueAtTime(3200, t0);
      bp.frequency.exponentialRampToValueAtTime(1400, t0 + dur);
      bp.Q.value = 0.9;
      const g = a.createGain();
      g.gain.setValueAtTime(0.0001, t0);
      g.gain.exponentialRampToValueAtTime(0.03, t0 + 0.25);
      g.gain.exponentialRampToValueAtTime(0.004, t0 + dur);
      src.connect(bp); bp.connect(g); g.connect(out);
      src.start(t0); src.stop(t0 + dur);
      for (let t = 0.12; t < dur - 0.2;) {
        const k = t / dur;
        tone(a, out, 2100 + Math.random() * 600, t0 + t, 0.015, 0.035 * (1 - k) + 0.01, 'square');
        t += 0.05 + 0.32 * k * k;
      }
      st.nodes.push(src);
    } catch { /* прикраса */ }
  }

  function hush(st) {
    for (const n of st.nodes) { try { if (n.stop) n.stop(); n.disconnect(); } catch { /* уже тихо */ } }
    st.nodes = [];
  }

  /// Дзвін на свій виграш і перше знайомство з last (без звуку на F5).
  function chime(st, ctx, v) {
    const last = v.last;
    const no = last ? last.no : null;
    if (st.lastNo === undefined) { st.lastNo = no; return; }
    if (no === st.lastNo) return;
    st.lastNo = no;
    if (!ctx.mine || !last || !ctx.me) return;
    const r = (last.results || []).find((x) => x.nick === ctx.me.nick);
    if (!r || !(r.net > 0)) return;
    const wait = st.animNo === last.no ? st.landAt - Date.now() + 250 : 0;
    later(st, () => sound(st, 'win'), wait);
  }

  // ---------------------------------------------------------------------------------------------
  // Статус і тикер
  // ---------------------------------------------------------------------------------------------

  function statusText(ctx) {
    const v = ctx.view;
    if (!v || !v.mode) return '';
    const root = roots.get(ctx);
    const st = root && root._rl;
    if (v.closed) return 'Каса зачинена';
    const me = v.me;
    const onTable = st && me ? onTableOf(st, v) : (me ? me.onTable : 0);
    if (v.mode === 'solo') {
      if (v.phase === 'spin') return 'Крутиться…';
      return onTable > 0 ? 'На столі ' + onTable + ' — тисни «Крутити»' : 'Став черепки на поле';
    }
    switch (v.phase) {
      case 'bets': {
        const left = st ? leftMs(st) : (v.leftMs || 0);
        const s = Math.ceil(left / 1000);
        const head = s > 0 ? 'Ставки ще ' + s + ' с' : 'Ставки зроблено';
        if (!ctx.mine) return head + ' · сядь, щоб ставити';
        return head + ' · ' + (onTable > 0 ? 'твоїх на столі ' + onTable : 'став черепки');
      }
      case 'spin': return 'Ставки зроблено — крутиться…';
      case 'result': {
        const sp = v.spin, last = v.last;
        if (!sp || (st && !landed(st, v))) return 'Ставки зроблено — крутиться…';
        const head = 'Випало ' + (sp.n === 0 ? 'зеро' : sp.n + ' ' + COLOR_WORD[sp.c || colorOf(sp.n)]);
        const r = last && last.no === sp.no && ctx.me ? (last.results || []).find((x) => x.nick === ctx.me.nick) : null;
        return r ? head + ' · ти ' + signed(r.net) : head;
      }
      case 'idle': return 'Глек дрімає — постав, і він прокинеться';
      default: return '';
    }
  }

  function tick(root) {
    const st = root._rl;
    if (!st || !st.ctx || !st.ctx.view) return;
    const v = st.ctx.view;
    if (v.mode !== 'table' || v.phase !== 'bets') return;
    const card = root.closest('.gtable');
    const se = card && card.querySelector('.gstatus');
    const text = statusText(st.ctx);
    if (se && text && se.textContent !== text) se.textContent = text;
    // дедлайн ставок минув на клієнті — фішки гасимо, не чекаючи сервера
    const off = !canBet(st.ctx, st, v);
    if (off !== st.el.board.classList.contains('off')) paint(root, st.ctx);
  }

  // ---------------------------------------------------------------------------------------------
  // Реєстрація: дві гри, один набір функцій
  // ---------------------------------------------------------------------------------------------

  const api = {
    icon: ICON,
    added: '2026-10-08',
    seatNames: (i) => 'місце ' + (i + 1),
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o', 'c', 'd'],
    pad: {
      dirs: true,
      a: 'Enter',
      x: 'KeyZ',
      hint: '{dpad} поле · {a} поставити · {x} відміна · {lb}{rb} фішка · {y} крутити/повторити',
      on(btn, ctx) {
        const root = roots.get(ctx);
        if (!root) return false;
        if (btn === 'lb') { stepChip(root, ctx, -1); return true; }
        if (btn === 'rb') { stepChip(root, ctx, 1); return true; }
        if (btn === 'y') {
          if (ctx.view && ctx.view.mode === 'solo') soloGo(root, ctx); else command(root, ctx, 'repeat');
          return true;
        }
        return false;
      },
      when: (ctx) => ctx.mine && ctx.playing,
    },

    mount(root, ctx) {
      roots.set(ctx, root);
      const st = state(root);
      st.ctx = ctx;
      skeleton(root, ctx);
      st.tick = setInterval(() => tick(root), 1000);
      paint(root, ctx);
    },

    update(root, ctx) {
      roots.set(ctx, root);
      const st = state(root);
      // новий вид після підтвердження — підтверджені фішки вже в ньому
      st.pending = st.pending.filter((p) => !p.acked);
      paint(root, ctx);
    },

    visible(root, ctx, on) {
      if (on && root._rl) { fitLayout(root, root._rl); paint(root, ctx); }
    },

    onKey,
    status: statusText,

    unmount(root) {
      const st = root._rl;
      if (!st) return;
      clearInterval(st.tick);
      clearTimeout(st.sayT);
      clearTimeout(st.roT);
      st.timers.forEach(clearTimeout);
      st.timers = [];
      if (st.ro) st.ro.disconnect();
      hush(st);
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      root._rl = null;
    },

    // Для перевірки (qa): решітка, покриття й кути колеса — без DOM.
    geom: { WHEEL, colorOf, spotAt, pointOf, latticeAt, covers, label, landAngles, placeOf },
  };

  HGames.register(Object.assign({ id: 'roulette' }, api));
  HGames.register(Object.assign({ id: 'roulette-solo' }, api));
})();
