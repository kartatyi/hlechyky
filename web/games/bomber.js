/*
  Бомбер. Реалтайм: сервер тикає раз на 60 мс і шле кадр, ми його малюємо й трохи згладжуємо.

  Кадр (Impl/Bomber.cs):
    { t, p: [{x, y, alive, bombs, range, boots}], b: [{x, y, fuse}], f: int[] (клітинки полум'я),
      boxes: int[], pw: [{x, y, kind}], wins: int[], phase, startIn }
  Координати бомберів — у дванадцятих частках клітинки (SUB), бомб і бонусів — у цілих клітинках.
  Стіни не міняються за партію, тому їх шле лише вид: { width, height, sub, walls, need, round, ... }.

  Ввід: Input('move', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору, -1 стоп (напрямок «тримають»,
  тому на відпускання клавіші шлемо -1); Input('bomb') — покласти бомбу під себе.
*/
(() => {
  const W = 15, H = 13, PX = 26, SUB = 12, TICK_MS = 60;
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="7" cy="10" r="5" fill="var(--accent)"/>'
    + '<path d="M10.6 5.4 12.6 3.4" stroke="var(--clay)" stroke-width="1.8" stroke-linecap="round" fill="none"/>'
    + '<circle cx="13.4" cy="2.6" r="1.7" fill="var(--ok)"/></svg>';

  // Розкладконезалежно читаємо e.code, але терпимо й e.key: синтетичний keydown із панелі браузера
  // приходить без code, а на українській розкладці WASD — це ЦФІВ.
  const DIRS = {
    ArrowRight: 0, KeyD: 0, d: 0, в: 0,
    ArrowDown: 1, KeyS: 1, s: 1, і: 1,
    ArrowLeft: 2, KeyA: 2, a: 2, ф: 2,
    ArrowUp: 3, KeyW: 3, w: 3, ц: 3,
  };
  const dirOf = (e) => {
    const byCode = DIRS[e.code];
    if (byCode !== undefined) return byCode;
    return DIRS[String(e.key || '').toLowerCase()];
  };
  const isBomb = (e) => e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar';

  // П'ятий і шостий кольори — свої змінні з bomber.css: у теми сайту акцентів на шістьох не вистачає.
  const SEATS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#c5763a'], ['--muted', '#9db3a5'],
    ['--bblue', '#6fb3e8'], ['--bpink', '#e88ac0']];
  const SEATN = SEATS.length;
  const BOOM_MS = 520;
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const GLYPH = { range: '🔥', bomb: '💣', boots: '👟' };

  const at = (cell) => [(cell % W) * PX, Math.floor(cell / W) * PX];
  /// На звичайному моніторі (DPR 1) поле на Full HD розтягується в півтора раза й милиться — малюємо
  /// вдвічі щільніше. На телефонах із DPR ≥ 2 це вже зробив каркас.
  const scale = () => ((window.devicePixelRatio || 1) >= 2 ? 1 : 2);
  const lerp = (a, b, t) => a + (b - a) * t;

  // ---------------------------------------------------------------------------------------------
  // Малювання
  // ---------------------------------------------------------------------------------------------

  function box(g, x, y, pad, r, fill) {
    g.fillStyle = fill;
    g.beginPath();
    g.roundRect(x + pad, y + pad, PX - pad * 2, PX - pad * 2, r);
    g.fill();
  }

  // Кожен ctx.css — це getComputedStyle(:root), а малюємо ми на rAF. Тому палітру збираємо один раз і
  // тримаємо, поки не зміниться тема (атрибут data-theme) або не прийде новий вид (подія 'room' — рідко):
  // раніше її збирали щокадру — два десятки getComputedStyle 60 разів на секунду.
  function palOf(st) {
    const sig = document.documentElement.getAttribute('data-theme') || '';
    if (!st.pal || st.palSig !== sig) { st.pal = palette(st); st.palSig = sig; }
    return st.pal;
  }

  function palette(st) {
    return {
      bg: st.css('--bg', '#0f1f18'),
      bg2: st.css('--bg2', '#16291f'),
      panel: st.css('--panel', '#1c3328'),
      wall: st.css('--panel3', '#2b4c3c'),
      edge: st.css('--line', '#2f4d3d'),
      clay: st.css('--clay', '#c5763a'),
      dark: st.css('--bbox', '#8f5527'),
      bomb: st.css('--bbomb', '#12211a'),
      accent: st.css('--accent', '#f4c542'),
      danger: st.css('--danger', '#e57373'),
      shade: st.css('--gshade', 'rgba(15, 31, 24, .62)'),
      text: st.css('--text', '#ecf1ea'),
      seats: SEATS.map(([name, fallback]) => st.css(name, fallback)),
    };
  }

  function drawWalls(pal, g, walls) {
    for (const cell of walls) {
      const [x, y] = at(cell);
      box(g, x, y, 0, 3, pal.wall);
      g.fillStyle = pal.edge;
      g.fillRect(x, y, PX, 3);
    }
  }

  function drawBoxes(pal, g, cells) {
    const clay = pal.clay;
    const dark = pal.dark;
    for (const cell of cells || []) {
      const [x, y] = at(cell);
      box(g, x, y, 2, 4, clay);
      // дві дошки навхрест — щоб ящик читався ящиком, а не просто плиткою
      g.strokeStyle = dark;
      g.lineWidth = 2;
      g.beginPath();
      g.moveTo(x + 4, y + PX / 2);
      g.lineTo(x + PX - 4, y + PX / 2);
      g.moveTo(x + PX / 2, y + 4);
      g.lineTo(x + PX / 2, y + PX - 4);
      g.stroke();
    }
  }

  function drawDrops(pal, g, drops) {
    for (const d of drops || []) {
      const x = d.x * PX, y = d.y * PX;
      box(g, x, y, 4, 6, pal.panel);
      g.font = '13px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(GLYPH[d.kind] || '?', x + PX / 2, y + PX / 2 + 1);
    }
  }

  function drawFlame(pal, g, cells, now) {
    // Червоне по краю й жовте всередині: інакше полум'я на цьому полі плутається з глиняними ящиками.
    const outer = pal.danger;
    const inner = pal.accent;
    const beat = 0.88 + 0.12 * Math.sin(now / 70);
    g.globalAlpha = beat;
    for (const cell of cells || []) {
      const [x, y] = at(cell);
      box(g, x, y, 1, 5, outer);
      box(g, x, y, 5, 4, inner);
    }
    g.globalAlpha = 1;
  }

  function drawBombs(pal, g, bombs, now) {
    const body = pal.bomb;
    const spark = pal.accent;
    for (const b of bombs || []) {
      const cx = b.x * PX + PX / 2, cy = b.y * PX + PX / 2;
      // що менше лишилось запалу, то швидше бомба «дихає» — це єдина підказка про час
      const beat = 1 + 0.13 * Math.sin(now / (60 + Math.max(0, b.fuse) * 4));
      g.fillStyle = body;
      g.beginPath();
      g.arc(cx, cy + 1, (PX * 0.36) * beat, 0, Math.PI * 2);
      g.fill();
      g.strokeStyle = pal.clay;
      g.lineWidth = 1.6;
      g.beginPath();
      g.moveTo(cx + 3, cy - PX * 0.3);
      g.lineTo(cx + 6, cy - PX * 0.45);
      g.stroke();
      g.fillStyle = spark;
      g.beginPath();
      g.arc(cx + 7, cy - PX * 0.5, 2.1, 0, Math.PI * 2);
      g.fill();
    }
  }

  /// me — моє місце (або null для глядача), start — іде відлік «Готуйсь», тоді «ти» видно здалеку.
  function drawMen(pal, g, men, me, start) {
    for (let i = 0; i < men.length; i++) {
      const m = men[i];
      if (!m || !m.alive) continue;
      const cx = (m.x / SUB) * PX + PX / 2, cy = (m.y / SUB) * PX + PX / 2;
      const r = PX * 0.38;
      g.fillStyle = pal.bg;
      g.beginPath();
      g.ellipse(cx, cy + r * 0.85, r * 0.8, r * 0.3, 0, 0, Math.PI * 2);   // тінь під ногами
      g.globalAlpha = 0.35;
      g.fill();
      g.globalAlpha = 1;
      g.fillStyle = pal.seats[i] || SEATS[i][1];
      g.beginPath();
      g.arc(cx, cy, r, 0, Math.PI * 2);
      g.fill();
      g.fillStyle = pal.bg;
      g.beginPath();
      g.arc(cx - r * 0.32, cy - r * 0.2, r * 0.17, 0, Math.PI * 2);
      g.arc(cx + r * 0.32, cy - r * 0.2, r * 0.17, 0, Math.PI * 2);
      g.fill();
      // Номер місця на пузі: на шістьох кольори близькі, а цифру не сплутає і той, хто кольорів не розрізняє.
      g.font = '700 8px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(String(i + 1), cx, cy + r * 0.45);
      if (i === me) {
        // Своя стрілочка над головою: на повному полі «де я?» — перше питання кожного раунду.
        const top = cy - r - 2;
        g.fillStyle = pal.text;
        g.beginPath();
        g.moveTo(cx - 4, top - 5);
        g.lineTo(cx + 4, top - 5);
        g.lineTo(cx, top);
        g.closePath();
        g.fill();
        if (start) {
          // На відліку ще й кільце довкола — щоб знайти себе за дві секунди, а не за пів раунду.
          g.strokeStyle = pal.text;
          g.lineWidth = 1.5;
          g.beginPath();
          g.arc(cx, cy, r + 3, 0, Math.PI * 2);
          g.stroke();
          g.font = '700 10px system-ui, sans-serif';
          g.textBaseline = 'bottom';
          g.fillText('ти', cx, top - 6);
        }
      }
    }
  }

  /// Підірваний бомбер не зникає мовчки: хмарка його кольору розлітається й тане.
  function drawBooms(pal, g, booms, now) {
    for (const b of booms) {
      const k = (now - b.at) / BOOM_MS;
      if (k < 0 || k >= 1) continue;
      g.globalAlpha = 1 - k;
      g.fillStyle = pal.seats[b.i] || pal.danger;
      for (let j = 0; j < 6; j++) {
        const a = (Math.PI * 2 * j) / 6 + b.i;
        const d = PX * (0.15 + 0.55 * k);
        g.beginPath();
        g.arc(b.x + Math.cos(a) * d, b.y + Math.sin(a) * d, PX * (0.2 - 0.1 * k), 0, Math.PI * 2);
        g.fill();
      }
      g.strokeStyle = pal.text;
      g.lineWidth = 2;
      g.beginPath();
      g.arc(b.x, b.y, PX * (0.25 + 0.45 * k), 0, Math.PI * 2);
      g.stroke();
      g.globalAlpha = 1;
    }
  }

  /// Хто щойно злетів у повітря: живий у попередньому кадрі, мертвий у цьому — того самого раунду.
  /// Останнього в раунді ловимо теж: його кадр приходить уже з фазою 'pause'.
  function noteBooms(st, f) {
    const was = st.last;
    const now = performance.now();
    if (was && was.p && f && f.p && was.phase === 'go' && (f.phase === 'go' || f.phase === 'pause' || f.phase === 'over')
      && f.t >= (was.t || 0)) {
      for (let i = 0; i < f.p.length; i++) {
        const a = was.p[i], b = f.p[i];
        if (a && a.alive && b && !b.alive)
          st.booms.push({ i, x: (a.x / SUB) * PX + PX / 2, y: (a.y / SUB) * PX + PX / 2, at: now });
      }
    }
    st.booms = st.booms.filter((b) => now - b.at < BOOM_MS);
  }

  function drawShade(pal, g, c, f, waiting, st) {
    if (f.phase === 'go' || (f.phase === 'start' && waiting)) return;
    g.fillStyle = pal.shade;
    g.fillRect(0, 0, c.w, c.h);
    if (f.phase === 'pause' || f.phase === 'over') {
      // Хто взяв раунд (чи всю партію) — великими літерами просто на полі, а не лише дрібним рядком під ним.
      const over = f.phase === 'over';
      const res = over && st.ctx && st.ctx.room && st.ctx.room.result;
      const alive = over ? ((res && res.winners) || []) : (f.p || []).map((m, i) => (m && m.alive ? i : -1)).filter((i) => i >= 0);
      const who = alive.length === 1 ? alive[0] : -1;
      const nick = who >= 0 && st.ctx ? (st.ctx.nickOf(who) || st.ctx.seatName(who)) : '';
      g.fillStyle = pal.bomb;
      g.globalAlpha = 0.85;
      g.beginPath();
      g.roundRect(20, c.h / 2 - 34, c.w - 40, 66, 14);
      g.fill();
      g.globalAlpha = 1;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.font = '700 22px system-ui, sans-serif';
      g.fillStyle = who >= 0 ? (pal.seats[who] || pal.text) : pal.text;
      g.fillText(who >= 0 ? '🏆 ' + nick : 'Нічия', c.w / 2, c.h / 2 - 8, c.w - 24);
      g.font = '13px system-ui, sans-serif';
      g.fillStyle = pal.text;
      g.fillText(over ? (who >= 0 ? 'бере партію!' : 'партія внічию') : (who >= 0 ? 'бере раунд' : 'цей раунд — нікому'), c.w / 2, c.h / 2 + 16);
      return;
    }
    if (f.phase !== 'start' || waiting) return;
    g.fillStyle = pal.text;
    g.font = '700 46px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(Math.ceil((f.startIn * TICK_MS) / 1000)), c.w / 2, c.h / 2);
  }

  /// Кадри приходять 16 разів на секунду — між ними бомберів ведемо лінійно, інакше рух смикається.
  function men(st) {
    const cur = st.interp.at();
    const f = (cur && cur.b) || st.last;
    if (!f) return null;
    const a = cur && cur.a;
    // t падає на новому раунді, фаза міняється на паузі — у таких стрибках згладжувати нема чого
    const smooth = a && a !== f && a.p && a.t <= f.t && a.phase === f.phase;
    if (!smooth) return { f, men: f.p || [] };
    const k = cur.t;
    return {
      f,
      men: (f.p || []).map((m, i) => {
        const was = a.p[i];
        return !was || !was.alive ? m : Object.assign({}, m, { x: lerp(was.x, m.x, k), y: lerp(was.y, m.y, k) });
      }),
    };
  }

  const sameCells = (a, b) => {
    if (a === b) return true;
    if (!a || !b || a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
  };

  /// Тло, стіни й ящики — в окремому канвасі: стіни не міняються за партію, ящики — лише коли щось
  /// розлетілось. Перемальовуємо його тоді, а щокадру кладемо однією картинкою (раніше — сотня roundRect).
  function staticLayer(st, pal, boxes) {
    const el = st.cv.el;
    const L = st.layer || (st.layer = document.createElement('canvas'));
    if (L.width === el.width && L.height === el.height && st.layerPal === pal && st.layerWalls === st.walls
      && sameCells(st.layerBoxes, boxes)) return L;
    if (L.width !== el.width) L.width = el.width;
    if (L.height !== el.height) L.height = el.height;
    const g = L.getContext('2d');
    const k = el.width / (W * PX);
    g.setTransform(k, 0, 0, k, 0, 0);
    g.fillStyle = pal.bg2;
    g.fillRect(0, 0, W * PX, H * PX);
    drawWalls(pal, g, st.walls);
    drawBoxes(pal, g, boxes);
    st.layerPal = pal;
    st.layerWalls = st.walls;
    st.layerBoxes = boxes ? boxes.slice() : [];
    return L;
  }

  function draw(st, waiting) {
    const c = st.cv;
    if (!c) return;
    const now = performance.now();
    const shot = men(st);
    const g = c.ctx;
    const pal = palOf(st);
    // Малюємо в логічних одиницях поля, а канвас щільніший у K разів (див. scale()).
    const box = { w: W * PX, h: H * PX };
    g.save();
    g.scale(st.K, st.K);
    g.drawImage(staticLayer(st, pal, shot ? shot.f.boxes : null), 0, 0, box.w, box.h);
    if (shot) {
      const f = shot.f;
      drawDrops(pal, g, f.pw);
      drawBombs(pal, g, f.b, now);
      const me = st.ctx && st.ctx.mine ? st.ctx.seat : null;
      drawMen(pal, g, shot.men, me, f.phase === 'start' && !waiting);
      drawFlame(pal, g, f.f, now);
      drawShade(pal, g, box, f, waiting, st);
      drawBooms(pal, g, st.booms, now);   // поверх тіні: останній вибух раунду теж має бути видно
    }
    g.restore();
  }

  // ---------------------------------------------------------------------------------------------
  // Рядок над полем: раунди й апгрейди кожного
  // ---------------------------------------------------------------------------------------------

  function hud(root, ctx, f) {
    let el = root.querySelector(':scope > .bhud');
    if (!el) {
      el = document.createElement('div');
      el.className = 'bhud';
      root.insertBefore(el, root.firstChild);
    }
    const wins = (f && f.wins) || [];
    const men = (f && f.p) || [];
    let html = '';
    for (let i = 0; i < SEATN; i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      const m = men[i] || {};
      const ups = '💣' + (m.bombs || 1) + ' 🔥' + (m.range || 2) + (m.boots ? ' 👟' : '');
      html += '<span class="bchip s' + i + (m.alive === false ? ' out' : '') + (i === ctx.seat ? ' me' : '') + '">'
        + '<i>' + (i + 1) + '</i><span class="bnick">' + ctx.esc(nick) + '</span> <b>' + (wins[i] || 0) + '</b> <span class="bups">' + ups + '</span></span>';
    }
    // Годинник раунду: за дві хвилини раунд нічий, і краще бачити це заздалегідь, ніж дивуватись.
    if (f && f.phase === 'go') {
      const limit = (ctx.view && ctx.view.limit) || 2000;
      const left = Math.max(0, limit - (f.t || 0));
      html += '<span class="bchip bmclock' + (left * TICK_MS <= 15000 ? ' hot' : '') + '">⏱ ' + clock(left) + '</span>';
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Керування пальцем
  // ---------------------------------------------------------------------------------------------

  function pad(root, ctx, st) {
    let el = root.querySelector(':scope > .bpad');
    // Хрестовина лише поки йде партія: у лобі й після кінця вона штовхала «Почати» / «Ану ще раз» під нижнє меню телефона.
    if (!ctx.mine || !ctx.playing) {
      if (el) { el.remove(); st.touch = -1; st.pid = null; }
      return;
    }
    if (!el) {
      el = document.createElement('div');
      el.className = 'bpad';
      const label = { 0: '→', 1: '↓', 2: '←', 3: '↑' };
      const aria = { 0: 'праворуч', 1: 'вниз', 2: 'ліворуч', 3: 'вгору' };
      el.innerHTML = '<div class="bdirs">'
        + [3, 2, 0, 1].map((d) => '<button type="button" data-dir="' + d + '" aria-label="' + aria[d] + '">' + label[d] + '</button>').join('')
        + '</div><button type="button" class="bbomb" aria-label="бомба">💣</button>';
      // Напрямок «тримають», тому слухаємо саме натиск і відпускання, а не клік.
      el.addEventListener('pointerdown', (e) => {
        const b = e.target.closest('button');
        if (!b) return;
        e.preventDefault();
        if (b.classList.contains('bbomb')) { el._bomberCtx.input('bomb'); return; }
        // Захоплюємо вказівник: інакше палець (чи миша в мобільному вигляді), з'їхавши з кнопки,
        // забирає з собою pointerup — і бомбер біг би далі, поки не впреться.
        try { b.setPointerCapture(e.pointerId); } catch (_) { /* старий браузер — переживемо */ }
        st.pid = e.pointerId;
        st.touch = +b.dataset.dir;
        steer(st, el._bomberCtx);
      });
      const release = (e) => {
        if (st.pid !== e.pointerId) return;
        st.pid = null;
        st.touch = -1;
        steer(st, el._bomberCtx);
      };
      el.addEventListener('pointerup', release);
      el.addEventListener('pointercancel', release);
      root.appendChild(el);
    }
    el._bomberCtx = ctx;      // колбеки завжди з останнього update, а не з першого
  }

  /// Телефон: на шістьох шапка столу з місцями й рядок гравців штовхали поле вниз, а хрестовина з «💣»
  /// опинялась під нижнім меню — видно було або поле, або кнопки. Раз на партію (room.startedAt), коли
  /// партія пішла, прокручуємо сторінку так, щоб рядок гравців став під шапку сайту: тоді поле й кнопки
  /// вміщаються разом. Якщо й так усе видно — не чіпаємо.
  function fitPhone(root, st, ctx, hudSel, padSel) {
    if (!ctx.mine || !ctx.playing || !ctx.room || !HGames.ui.coarse()) return;
    const key = ctx.room.startedAt || '';
    if (st.fitFor === key) return;
    const hudEl = root.querySelector(':scope > ' + hudSel), padEl = root.querySelector(':scope > ' + padSel);
    if (!hudEl || !padEl) return;
    const a = hudEl.getBoundingClientRect(), b = padEl.getBoundingClientRect();
    if (!a.height || !b.height) return;              // картку зараз не видно — спробуємо на наступному виді
    st.fitFor = key;
    const head = document.querySelector('header');
    const top = (head ? head.getBoundingClientRect().bottom : 0) + 4;
    const tabs = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--tabs-h')) || 0;
    // кнопки мають стати над нижнім меню й над плаваючою кнопкою балачки столу («💬 Стіл»)
    const fab = document.querySelector('.tchat.drawer:not(.open) .tc-head');
    const fr = fab && fab.getBoundingClientRect();
    const limit = (fr && fr.height ? Math.min(fr.top, innerHeight - tabs) : innerHeight - tabs) - 6;
    const lo = b.bottom - limit, hi = a.top - top;   // на скільки прокрутити: не менше lo, не більше hi
    const dy = lo <= hi ? Math.min(Math.max(0, lo), hi) : lo;   // не влазить усе — кнопки важливіші за рядок гравців
    if (Math.abs(dy) < 2) return;
    const calm = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    window.scrollBy({ top: dy, behavior: calm ? 'auto' : 'smooth' });
  }

  // ---------------------------------------------------------------------------------------------
  // Модуль
  // ---------------------------------------------------------------------------------------------

  /// Стан картки живе на її ж корені, але onKey отримує лише ctx — тому кладемо посилання і туди.
  function state(root, ctx) {
    if (!root._bomber) {
      root._bomber = {
        cv: null, walls: [], last: null, held: -1, pid: null, bombDown: false, booms: [],
        keys: [], touch: -1, seenAt: 0,
        raf: 0, keyup: null, blur: null, phase: '', round: 0, css: ctx.css, K: scale(),
      };
    }
    root._bomber.ctx = ctx;
    ctx._bomber = root._bomber;
    return root._bomber;
  }

  /// Куди бігти зараз: палець на хрестовині головніший, далі — остання із затиснутих клавіш. Затиснув →,
  /// додав ↑ і відпустив ↑ — бомбер знову біжить праворуч, а не стоїть (раніше зупинявся, хоч → іще тиснули).
  /// Серверу шлемо лише зміну.
  function steer(st, ctx) {
    const d = st.touch >= 0 ? st.touch : (st.keys.length ? st.keys[st.keys.length - 1] : -1);
    if (st.held === d) return;
    st.held = d;
    if (ctx && ctx.mine && ctx.playing) ctx.input('move', { dir: d });
  }

  /// Сервер бере новий раунд із чистими бомберами, тож напрямок, який людина досі тримає, треба
  /// нагадати рівно на переході у «go» — інакше вона стоїть, поки не перетисне клавішу.
  function syncHeld(st) {
    const phase = (st.last && st.last.phase) || '';
    if (phase === 'go' && st.phase !== 'go' && st.held >= 0 && st.ctx && st.ctx.mine && st.ctx.playing)
      st.ctx.input('move', { dir: st.held });
    st.phase = phase;
  }

  /// Цикл малювання живе, лише поки є що рухати: йдуть кадри (плюс ще три тики — інтерполяція відстає на
  /// один) або догорає хмарка підірваного. Стіл у лобі, дограна партія, картка під іншою вкладкою
  /// (#colGames — display:none, offsetParent зникає) — жодного rAF: новий кадр чи вид розбудять цикл самі.
  /// Раніше rAF крутився 60 разів на секунду завжди, поки картка існувала, навіть на порожньому столі.
  const LIVE_MS = TICK_MS * 3;
  function spin(st) {
    if (st.raf || !st.cv) return;
    st.raf = requestAnimationFrame(() => loop(st));
  }
  function loop(st) {
    st.raf = 0;
    if (!st.cv || !st.cv.el.isConnected || !st.cv.el.offsetParent) return;
    draw(st, !(st.ctx && st.ctx.playing));
    const now = performance.now();
    if (st.booms.length) st.booms = st.booms.filter((b) => now - b.at < BOOM_MS);
    if (now - st.seenAt < LIVE_MS || st.booms.length) spin(st);
  }

  HGames.register({
    id: 'bomber',
    icon: ICON,
    seatNames: ['жовтий', 'зелений', 'рудий', 'сірий', 'синій', 'рожевий'],
    seatClass: ['x', 'o', 'c', 'd', 'bb', 'bp'],
    pad: { dirs: true, a: 'Space', anyBtn: true, hint: '{dpad} бігати · {a} бахнути бомбу (будь-яка кнопка)' },
    news: {
      v: '2026-09-28',
      title: 'Бомбер: керування слухняніше',
      items: [
        '🎮 Затиснув →, додав ↑ і відпустив ↑ — бомбер знову біжить праворуч, а не стає; Alt+Tab більше не лишає його бігти самого',
        '📱 На телефоні з початком партії поле й хрестовина стають в екран разом, а в лобі хрестовина не заважає',
        '🔍 На ноутбуці й Деці поле більше, а годинник раунду — знову маленький чіп, а не смуга на всю ширину',
        '🧹 Бомбери більше не смикаються на мить, коли хтось сідає за інший стіл',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      st.interp = HGames.ui.Interp();
      st.cv = HGames.ui.canvas(root, { w: W * PX * st.K, h: H * PX * st.K, cls: 'bboard' });
      // Каркас віддає модулю лише keydown, а напрямок тут тримають — відпускання ловимо самі.
      st.keyup = (e) => {
        if (isBomb(e)) { st.bombDown = false; return; }
        const d = dirOf(e);
        if (d === undefined || st.keys.indexOf(d) < 0) return;
        st.keys = st.keys.filter((k) => k !== d);
        steer(st, st.ctx);
      };
      // Вікно втратило фокус (Alt+Tab, клік у балачки іншої програми) — keyup уже не прийде, і бомбер
      // біг би, поки не впреться. Відпускаємо все.
      st.blur = () => {
        st.keys = [];
        st.touch = -1;
        st.bombDown = false;
        steer(st, st.ctx);
      };
      document.addEventListener('keyup', st.keyup);
      window.addEventListener('blur', st.blur);
      // Картку сховали (лобі, інша вкладка сайту) й показали знову: rAF поки спав, а подія 'room' могла
      // прийти, поки її не було видно. Щойно канвас знову на екрані — малюємо свіже.
      if (window.IntersectionObserver) {
        st.io = new IntersectionObserver((es) => { if (es[es.length - 1].isIntersecting) spin(st); });
        st.io.observe(st.cv.el);
      }
      // Після F5 сервер може пам'ятати напрямок, якого свіжий клієнт уже не тримає — скидаємо.
      if (ctx.mine && ctx.playing) ctx.input('move', { dir: -1 });
      spin(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      st.ctx = ctx;
      if (!st.cv) return;
      st.pal = null;   // тема могла змінитись — палітру зберемо заново (подія 'room' рідка)
      const v = ctx.view;
      if (v && v.walls && v.walls.length) st.walls = v.walls;
      // Вид накладаємо рівно раз. Каркас кличе update() і на кожну подію лобі (хтось сів за інший стіл),
      // причому з тим самим, давно збереженим видом — а вид бомбера приходить лише на межі раундів. Раніше
      // такий старий вид ставав «останнім кадром»: бомбери на мить відскакували туди, де стояли на початку
      // раунду, і рахунок над полем блимав старим.
      if (v && v.p && v !== st.seenView) {
        st.seenView = v;
        // Інтерполяцію рвемо лише тоді, коли світ справді стрибнув: новий раунд або тики пішли назад.
        const jump = !st.last || v.round !== st.round || v.t < (st.last.t || 0);
        if (!jump) noteBooms(st, v);
        st.last = v;
        st.round = v.round;
        if (jump) st.interp.reset();
        st.interp.push(v);
      }
      pad(root, ctx, st);
      hud(root, ctx, st.last);
      fitPhone(root, st, ctx, '.bhud', '.bpad');
      syncHeld(st);
      st.cv.resize();
      spin(st);        // якщо картку колись перемонтують — цикл малювання не загубиться
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      st.ctx = ctx;
      if (!st.cv || !f) return;
      noteBooms(st, f);
      st.last = f;
      st.seenAt = performance.now();
      st.interp.push(f);
      hud(root, ctx, f);
      syncHeld(st);
      spin(st);
    },

    onKey(e, ctx) {
      const st = ctx._bomber;
      if (!st || !ctx.mine || !ctx.playing) return false;
      if (isBomb(e)) {
        // Автоповтор клавіші дав би десяток Input на секунду дарма — бомба ставиться один раз на натиск.
        if (!st.bombDown) { st.bombDown = true; ctx.input('bomb'); }
        return true;
      }
      const dir = dirOf(e);
      if (dir === undefined) return false;
      // Остання натиснута — головна; автоповтор тієї самої нічого не міняє й нічого не шле.
      if (st.keys[st.keys.length - 1] !== dir) st.keys = st.keys.filter((k) => k !== dir).concat(dir);
      steer(st, ctx);
      return true;   // інакше стрілки гортали б сторінку
    },

    status(ctx) {
      const f = ctx.frame || ctx.view;
      if (!ctx.playing || !f || !f.phase) return '';
      if (f.phase === 'start') return 'Готуйсь…';
      if (f.phase === 'pause') {
        const alive = ((f.p || []).map((m, i) => (m && m.alive ? i : -1))).filter((i) => i >= 0);
        return alive.length === 1 ? 'Раунд бере ' + (ctx.nickOf(alive[0]) || ctx.seatName(alive[0])) : 'Раунд нічий';
      }
      if (f.phase === 'over') return '';
      if (!ctx.mine) return 'Дивишся збоку';
      return HGames.ui.coarse() ? 'Хрестовина — бігти, 💣 — бахнути бомбу' : 'Стрілки або WASD, пробіл — бахнути бомбу';
    },

    unmount(root) {
      const st = root._bomber;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.io) st.io.disconnect();
      st.cv = null;
      root._bomber = null;
    },
  });
})();
