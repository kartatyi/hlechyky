/*
  Бомбер. Реалтайм: сервер тикає раз на 60 мс і шле кадр, ми його малюємо й трохи згладжуємо.

  Кадр (Impl/Bomber.cs):
    { t, p: [{x, y, alive, bombs, range, boots, mv?, g?, k?, cu?}], b: [{x, y, fuse, rv?}], f: int[] (клітинки полум'я),
      boxes: int[], pw: [{x, y, kind}], wins: int[], phase, startIn, sh? (скільки клітинок забрало стискання),
      ev? ([id, як, хто, кого] — свіжі події) }
  Координати бомберів — у дванадцятих частках клітинки (SUB), бомб і бонусів — у цілих клітинках.
  mv — куди йде крок (для передбачення свого руху), g — привид (1 — помста ще є, 2 — кинута), k — рукавиця,
  cu — прокляття ('rev' | 'slow' | 'bombs').
  Стіни не міняються за партію, тому їх шле лише вид: { width, height, sub, walls, need, round, map,
  shrinkAt?, ghosts?, chaos?, teams?, ff?, note?, sum? }. Стискання клієнт домальовує сам: та сама спіраль, що й
  на сервері (BomberCore.Spiral), перші sh клітинок.

  Ввід: Input('move', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору, -1 стоп (напрямок «тримають»,
  тому на відпускання клавіші шлемо -1); Input('bomb') — покласти бомбу під себе (привидом — помсту).

  Прохід №3 (29.09): свій бомбер рушає одразу — модуль сам веде його тими самими правилами кроку, що й сервер,
  на «затримку зв'язку» вперед (її міряємо відлунням власного move), а кадр лише м'яко підправляє.
*/
(() => {
  const PX = 26, SUB = 12, TICK_MS = 60;
  const DELTA = [[1, 0], [0, 1], [-1, 0], [0, -1]];
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
  const FEED_MS = 6000;
  const clock = (ticks) => {
    const s = Math.max(0, Math.ceil((ticks * TICK_MS) / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const GLYPH = { range: '🔥', bomb: '💣', boots: '👟', kick: '🧤', skull: '💀' };
  const TEAMS = ['🥒 Огірки', '🍅 Помідори'];
  const CURSE = { rev: 'керування навпаки', slow: 'повзеш, як равлик', bombs: 'сиплеш бомби' };

  /// Розмір поля — з виду (15×13 або 19×15); до першого виду — класика.
  const dims = (st) => [st.W || 15, st.H || 13];
  const at = (st, cell) => [(cell % st.W) * PX, Math.floor(cell / st.W) * PX];
  /// На звичайному моніторі (DPR 1) поле на Full HD розтягується в півтора раза й милиться — малюємо
  /// вдвічі щільніше. На телефонах із DPR ≥ 2 це вже зробив каркас.
  const scale = () => ((window.devicePixelRatio || 1) >= 2 ? 1 : 2);
  const lerp = (a, b, t) => a + (b - a) * t;

  // ---------------------------------------------------------------------------------------------
  // Поле: стіни, стискання, що заважає ходити
  // ---------------------------------------------------------------------------------------------

  /// Та сама спіраль, що й BomberCore.Spiral: від краю всередину, минаючи стіни рамки, стовпів і лабіринту.
  function spiral(st) {
    const [W, H] = dims(st);
    const wall = new Uint8Array(W * H);
    for (const c of st.walls) wall[c] = 1;
    const out = [];
    let l = 1, t = 1, r = W - 2, b = H - 2;
    const add = (x, y) => { const c = y * W + x; if (!wall[c]) out.push(c); };
    while (l <= r && t <= b) {
      for (let x = l; x <= r; x++) add(x, t);
      for (let y = t + 1; y <= b; y++) add(r, y);
      if (b > t) for (let x = r - 1; x >= l; x--) add(x, b);
      if (r > l) for (let y = b - 1; y > t; y--) add(l, y);
      l++; t++; r--; b--;
    }
    return out;
  }

  /// Стіни стискання на цей кадр (перші sh клітинок спіралі).
  function shrunk(st, f) {
    const n = (f && f.sh) || 0;
    if (!n) return null;
    if (!st.spiral || st.spiralWalls !== st.walls) { st.spiral = spiral(st); st.spiralWalls = st.walls; }
    return st.spiral.slice(0, n);
  }

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
      ok: st.css('--ok', '#7bd389'),
      shade: st.css('--gshade', 'rgba(15, 31, 24, .62)'),
      text: st.css('--text', '#ecf1ea'),
      revenge: st.css('--bm-rv', '#9b6fd8'),
      squeeze: st.css('--bm-sq', '#5d4a3a'),
      seats: SEATS.map(([name, fallback]) => st.css(name, fallback)),
    };
  }

  function drawWalls(st, pal, g, walls, fill) {
    for (const cell of walls) {
      const [x, y] = at(st, cell);
      box(g, x, y, 0, 3, fill || pal.wall);
      g.fillStyle = pal.edge;
      g.fillRect(x, y, PX, 3);
    }
  }

  function drawBoxes(st, pal, g, cells) {
    const clay = pal.clay;
    const dark = pal.dark;
    for (const cell of cells || []) {
      const [x, y] = at(st, cell);
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
      box(g, x, y, 4, 6, d.kind === 'skull' ? pal.revenge : pal.panel);
      g.font = '13px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(GLYPH[d.kind] || '?', x + PX / 2, y + PX / 2 + 1);
    }
  }

  function drawFlame(st, pal, g, cells, now) {
    // Червоне по краю й жовте всередині: інакше полум'я на цьому полі плутається з глиняними ящиками.
    const outer = pal.danger;
    const inner = pal.accent;
    const beat = 0.88 + 0.12 * Math.sin(now / 70);
    g.globalAlpha = beat;
    for (const cell of cells || []) {
      const [x, y] = at(st, cell);
      box(g, x, y, 1, 5, outer);
      box(g, x, y, 5, 4, inner);
    }
    g.globalAlpha = 1;
  }

  function drawBombs(pal, g, bombs, now) {
    const spark = pal.accent;
    for (const b of bombs || []) {
      const cx = b.x * PX + PX / 2, cy = b.y * PX + PX / 2;
      // що менше лишилось запалу, то швидше бомба «дихає» — це єдина підказка про час
      const beat = 1 + 0.13 * Math.sin(now / (60 + Math.max(0, b.fuse) * 4));
      g.fillStyle = b.rv ? pal.revenge : pal.bomb;
      g.beginPath();
      g.arc(cx, cy + 1, (PX * 0.36) * beat, 0, Math.PI * 2);
      g.fill();
      if (b.rv) {
        // помста привида — фіолетова, з очима: видно здалеку, що це не звичайна
        g.fillStyle = pal.text;
        g.beginPath();
        g.arc(cx - 3, cy, 1.6, 0, Math.PI * 2);
        g.arc(cx + 3, cy, 1.6, 0, Math.PI * 2);
        g.fill();
      }
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
  function drawMen(pal, g, men, me, start, teams, now) {
    // спершу привиди (під живими), потім живі
    for (let pass = 0; pass < 2; pass++) {
      for (let i = 0; i < men.length; i++) {
        const m = men[i];
        if (!m) continue;
        const ghost = !m.alive && m.g;
        if (pass === 0 ? !ghost : !m.alive) continue;
        const cx = (m.x / SUB) * PX + PX / 2, cy = (m.y / SUB) * PX + PX / 2;
        const r = PX * 0.38;
        if (ghost) { drawGhost(pal, g, m, i, cx, cy, r, now); continue; }
        g.fillStyle = pal.bg;
        g.beginPath();
        g.ellipse(cx, cy + r * 0.85, r * 0.8, r * 0.3, 0, 0, Math.PI * 2);   // тінь під ногами
        g.globalAlpha = 0.35;
        g.fill();
        g.globalAlpha = 1;
        if (teams && teams[i] >= 0) {
          // команда — кільцем довкола: зелене в «Огірків», червоне в «Помідорів»
          g.strokeStyle = teams[i] === 0 ? pal.ok : pal.danger;
          g.lineWidth = 2.5;
          g.beginPath();
          g.arc(cx, cy, r + 2, 0, Math.PI * 2);
          g.stroke();
        }
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
        if (m.cu) {
          // прокляття — черепок над головою, що блимає
          g.globalAlpha = 0.6 + 0.4 * Math.sin(now / 90);
          g.font = '11px system-ui, sans-serif';
          g.textBaseline = 'bottom';
          g.fillText('💀', cx + r * 0.9, cy - r * 0.5);
          g.globalAlpha = 1;
        }
        if (i === me) drawMine(pal, g, cx, cy, r, start);
      }
    }
  }

  function drawMine(pal, g, cx, cy, r, start) {
    // Своя стрілочка над головою: на повному полі «де я?» — перше питання кожного раунду.
    const top = cy - r - 2;
    g.fillStyle = pal.text;
    g.beginPath();
    g.moveTo(cx - 4, top - 5);
    g.lineTo(cx + 4, top - 5);
    g.lineTo(cx, top);
    g.closePath();
    g.fill();
    if (!start) return;
    // На відліку ще й кільце довкола — щоб знайти себе за дві секунди, а не за пів раунду.
    g.strokeStyle = pal.text;
    g.lineWidth = 1.5;
    g.beginPath();
    g.arc(cx, cy, r + 3, 0, Math.PI * 2);
    g.stroke();
    g.font = '700 10px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'bottom';
    g.fillText('ти', cx, top - 6);
  }

  /// Привид: напівпрозорий, з хвилястим низом, трохи гойдається. Поки помста не кинута — фіолетова іскра.
  function drawGhost(pal, g, m, i, cx, cy, r, now) {
    const bob = Math.sin(now / 260 + i) * 1.5;
    const y = cy + bob;
    g.globalAlpha = 0.45;
    g.fillStyle = pal.seats[i] || SEATS[i][1];
    g.beginPath();
    g.arc(cx, y - r * 0.15, r * 0.85, Math.PI, 0);
    const base = y + r * 0.7;
    g.lineTo(cx + r * 0.85, base);
    for (let k = 0; k < 3; k++) {
      const x1 = cx + r * 0.85 - (k + 0.5) * (r * 1.7 / 3), x2 = cx + r * 0.85 - (k + 1) * (r * 1.7 / 3);
      g.quadraticCurveTo(x1, base - r * 0.35, x2, base);
    }
    g.closePath();
    g.fill();
    g.globalAlpha = 0.8;
    g.fillStyle = pal.bg;
    g.beginPath();
    g.arc(cx - r * 0.3, y - r * 0.2, r * 0.15, 0, Math.PI * 2);
    g.arc(cx + r * 0.3, y - r * 0.2, r * 0.15, 0, Math.PI * 2);
    g.fill();
    if (m.g === 1) {
      g.globalAlpha = 0.7 + 0.3 * Math.sin(now / 120);
      g.fillStyle = pal.revenge;
      g.beginPath();
      g.arc(cx + r * 0.8, y - r * 0.8, 3, 0, Math.PI * 2);
      g.fill();
    }
    g.globalAlpha = 1;
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

  /// Хто взяв раунд: один живий — він; у командах — команда, якщо живі лише її.
  function roundWinner(st, f) {
    const alive = (f.p || []).map((m, i) => (m && m.alive ? i : -1)).filter((i) => i >= 0);
    const teams = st.teams;
    if (teams && alive.length) {
      const t = teams[alive[0]];
      return alive.every((i) => teams[i] === t) ? { team: t } : null;
    }
    return alive.length === 1 ? { seat: alive[0] } : null;
  }

  function drawShade(st, pal, g, c, f, waiting) {
    if (f.phase === 'go' || (f.phase === 'start' && waiting)) return;
    g.fillStyle = pal.shade;
    g.fillRect(0, 0, c.w, c.h);
    if (f.phase === 'pause' || f.phase === 'over') {
      // Хто взяв раунд (чи всю партію) — великими літерами просто на полі, а не лише дрібним рядком під ним.
      const over = f.phase === 'over';
      const res = over && st.ctx && st.ctx.room && st.ctx.room.result;
      let who = null;
      if (over) {
        const ws = (res && res.winners) || [];
        if (ws.length && st.teams && st.teams[ws[0]] >= 0) who = { team: st.teams[ws[0]] };
        else if (ws.length === 1) who = { seat: ws[0] };
      } else who = roundWinner(st, f);
      let title = 'Нічия', color = pal.text;
      if (who && who.team !== undefined) { title = '🏆 ' + TEAMS[who.team]; color = who.team === 0 ? pal.ok : pal.danger; }
      else if (who && st.ctx) { title = '🏆 ' + (st.ctx.nickOf(who.seat) || st.ctx.seatName(who.seat)); color = pal.seats[who.seat] || pal.text; }
      g.fillStyle = pal.bomb;
      g.globalAlpha = 0.85;
      g.beginPath();
      g.roundRect(20, c.h / 2 - 34, c.w - 40, 66, 14);
      g.fill();
      g.globalAlpha = 1;
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.font = '700 22px system-ui, sans-serif';
      g.fillStyle = color;
      g.fillText(title, c.w / 2, c.h / 2 - 8, c.w - 24);
      g.font = '13px system-ui, sans-serif';
      g.fillStyle = pal.text;
      const plural = who && who.team !== undefined;
      g.fillText(over ? (who ? (plural ? 'беруть партію!' : 'бере партію!') : 'партія внічию')
        : (who ? (plural ? 'беруть раунд' : 'бере раунд') : 'цей раунд — нікому'), c.w / 2, c.h / 2 + 16);
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
  /// Свого живого бомбера — не інтерполяцією (вона на кадр позаду), а передбаченням (див. predict).
  function men(st, now) {
    const cur = st.interp.at();
    const f = (cur && cur.b) || st.last;
    if (!f) return null;
    const a = cur && cur.a;
    // t падає на новому раунді, фаза міняється на паузі — у таких стрибках згладжувати нема чого
    const smooth = a && a !== f && a.p && a.t <= f.t && a.phase === f.phase;
    const k = cur ? cur.t : 1;
    const list = (f.p || []).map((m, i) => {
      const was = smooth ? a.p[i] : null;
      return !was || (!was.alive && !was.g) || !!was.alive !== !!m.alive ? m
        : Object.assign({}, m, { x: lerp(was.x, m.x, k), y: lerp(was.y, m.y, k) });
    });
    const me = mySeat(st);
    if (me >= 0) {
      const pos = predicted(st, now);
      if (pos && list[me]) list[me] = Object.assign({}, list[me], pos);
    }
    return { f, men: list };
  }

  const sameCells = (a, b) => {
    if (a === b) return true;
    if (!a || !b || a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
  };

  /// Тло, стіни й ящики — в окремому канвасі: стіни не міняються за партію, ящики — лише коли щось
  /// розлетілось, стискання — клітинка раз на кілька тиків. Перемальовуємо його тоді, а щокадру кладемо
  /// однією картинкою (раніше — сотня roundRect).
  function staticLayer(st, pal, f) {
    const [W, H] = dims(st);
    const boxes = f ? f.boxes : null;
    const sh = (f && f.sh) || 0;
    const el = st.cv.el;
    const L = st.layer || (st.layer = document.createElement('canvas'));
    if (L.width === el.width && L.height === el.height && st.layerPal === pal && st.layerWalls === st.walls
      && st.layerSh === sh && sameCells(st.layerBoxes, boxes)) return L;
    if (L.width !== el.width) L.width = el.width;
    if (L.height !== el.height) L.height = el.height;
    const g = L.getContext('2d');
    const k = el.width / (W * PX);
    g.setTransform(k, 0, 0, k, 0, 0);
    g.fillStyle = pal.bg2;
    g.fillRect(0, 0, W * PX, H * PX);
    drawWalls(st, pal, g, st.walls);
    drawBoxes(st, pal, g, boxes);
    const squeezed = shrunk(st, f);
    if (squeezed) drawWalls(st, pal, g, squeezed, pal.squeeze);
    st.layerPal = pal;
    st.layerWalls = st.walls;
    st.layerSh = sh;
    st.layerBoxes = boxes ? boxes.slice() : [];
    return L;
  }

  function draw(st, waiting) {
    const c = st.cv;
    if (!c) return;
    const now = performance.now();
    const shot = men(st, now);
    const g = c.ctx;
    const pal = palOf(st);
    const [W, H] = dims(st);
    // Малюємо в логічних одиницях поля, а канвас щільніший у K разів (див. scale()).
    const area = { w: W * PX, h: H * PX };
    g.save();
    g.scale(st.K, st.K);
    g.drawImage(staticLayer(st, pal, shot ? shot.f : null), 0, 0, area.w, area.h);
    if (shot) {
      const f = shot.f;
      drawDrops(pal, g, f.pw);
      drawBombs(pal, g, f.b, now);
      const me = st.ctx && st.ctx.mine ? st.ctx.seat : null;
      drawMen(pal, g, shot.men, me, f.phase === 'start' && !waiting, st.teams, now);
      drawFlame(st, pal, g, f.f, now);
      drawShade(st, pal, g, area, f, waiting);
      drawBooms(pal, g, st.booms, now);   // поверх тіні: останній вибух раунду теж має бути видно
    }
    g.restore();
  }

  // ---------------------------------------------------------------------------------------------
  // Передбачення свого бомбера (п. 40)
  // ---------------------------------------------------------------------------------------------

  function mySeat(st) {
    const ctx = st.ctx;
    return ctx && ctx.mine && ctx.playing && ctx.seat != null ? ctx.seat : -1;
  }

  /// Що заважає ходити в цьому кадрі: стіни (з тими, що наросли стисканням), ящики й бомби.
  function blockOf(st, f) {
    const [W, H] = dims(st);
    const b = new Uint8Array(W * H);
    for (const c of st.walls) b[c] = 1;
    const sq = shrunk(st, f);
    if (sq) for (const c of sq) b[c] = 1;
    for (const c of f.boxes || []) b[c] = 1;
    for (const x of f.b || []) b[x.y * W + x.x] = 1;
    return b;
  }

  /// Стан свого бомбера з кадру: клітинка, звідки йде крок, напрямок і скільки дванадцятих позаду.
  function stateOf(m) {
    const mv = m.mv == null ? -1 : m.mv;
    if (mv < 0) return { cx: Math.round(m.x / SUB), cy: Math.round(m.y / SUB), mv: -1, step: 0 };
    const [dx, dy] = DELTA[mv];
    const cx = dx > 0 ? Math.floor(m.x / SUB) : dx < 0 ? Math.ceil(m.x / SUB) : Math.round(m.x / SUB);
    const cy = dy > 0 ? Math.floor(m.y / SUB) : dy < 0 ? Math.ceil(m.y / SUB) : Math.round(m.y / SUB);
    return { cx, cy, mv, step: Math.abs(m.x - cx * SUB) + Math.abs(m.y - cy * SUB) };
  }

  /// Той самий крок, що BomberCore.Walk: напрямок міняється лише на межі клітинок, у заблоковане не йдемо.
  function walkTick(st, s, want, speed, block) {
    const W = st.W;
    if (s.mv < 0) {
      if (want < 0) return;
      const [dx, dy] = DELTA[want];
      const nx = s.cx + dx, ny = s.cy + dy;
      if (nx < 0 || ny < 0 || nx >= st.W || ny >= st.H || block[ny * W + nx]) return;
      s.mv = want;
      s.step = 0;
    }
    s.step += speed;
    if (s.step < SUB) return;
    s.cx += DELTA[s.mv][0];
    s.cy += DELTA[s.mv][1];
    s.step = 0;
    s.mv = -1;
  }

  /// Де мій бомбер «зараз»: останній кадр, прокручений уперед на час від нього плюс затримку зв'язку, з тим
  /// напрямком, який я тримаю саме зараз. null — не передбачаємо (не граю, привид, не фаза гри, кадр застарів).
  function project(st, f, from, now) {
    const me = mySeat(st);
    const m = me >= 0 && f && f.phase === 'go' && f.p ? f.p[me] : null;
    if (!m || !m.alive || now - from > 400) return null;
    if (!st.block || st.blockFor !== f) { st.block = blockOf(st, f); st.blockFor = f; }
    const s = stateOf(m);
    const speed = m.cu === 'slow' ? 2 : m.boots ? 4 : 3;
    let want = st.held;
    if (m.cu === 'rev' && want >= 0) want = (want + 2) % 4;
    const ticks = Math.max(0, (now - from + st.lat) / TICK_MS);
    const whole = Math.min(8, Math.floor(ticks));
    for (let i = 0; i < whole; i++) walkTick(st, s, want, speed, st.block);
    // дробова частина тика — лише для картинки
    let part = 0, dir = s.mv;
    const frac = ticks - Math.floor(ticks);
    if (s.mv >= 0) part = Math.min(SUB - 1, s.step + speed * frac);
    else if (want >= 0) {
      const probe = { cx: s.cx, cy: s.cy, mv: -1, step: 0 };
      walkTick(st, probe, want, speed, st.block);
      if (probe.mv >= 0) { dir = probe.mv; part = speed * frac; }
    }
    const [dx, dy] = dir >= 0 ? DELTA[dir] : [0, 0];
    return { x: s.cx * SUB + dx * part, y: s.cy * SUB + dy * part };
  }

  /// Передбачене місце з м'якою поправкою: коли кадр каже інше, різниця тане за ~100 мс, а не стрибає.
  function predicted(st, now) {
    const pos = project(st, st.last, st.seenAt, now);
    if (!pos) { st.corr = null; return null; }
    const dt = Math.min(100, now - (st.corrAt || now));
    st.corrAt = now;
    if (st.corr) {
      const k = Math.exp(-dt / 60);
      st.corr.x *= k;
      st.corr.y *= k;
      pos.x += st.corr.x;
      pos.y += st.corr.y;
    }
    return pos;
  }

  /// Новий кадр: де мій бомбер був за старим кадром і де він за новим — різницю запам'ятовуємо як поправку.
  function correct(st, f, now) {
    const old = st.last && project(st, st.last, st.seenAt, now);
    // відлуння наміру: скільки йшов мій move до сервера й назад (з очікуванням тика) — на стільки й ведемо
    const me = mySeat(st);
    const m = me >= 0 && f.p ? f.p[me] : null;
    if (st.echo && m && m.mv === st.echo.d) {
      const sample = Math.min(300, now - st.echo.at);
      st.lat = st.lat * 0.7 + sample * 0.3;
      st.echo = null;
    } else if (st.echo && now - st.echo.at > 1000) st.echo = null;
    const neu = project(st, f, now, now);
    if (!old || !neu) { st.corr = null; return; }
    const cx = (st.corr ? st.corr.x : 0) + old.x - neu.x, cy = (st.corr ? st.corr.y : 0) + old.y - neu.y;
    // більше півтори клітинки — це вже не похибка, а справжній стрибок (стіна, раунд): не згладжуємо
    st.corr = Math.abs(cx) + Math.abs(cy) > SUB * 1.5 ? null : { x: cx, y: cy };
    st.corrAt = now;
  }

  // ---------------------------------------------------------------------------------------------
  // Рядок над полем: раунди й апгрейди кожного
  // ---------------------------------------------------------------------------------------------

  function chipOf(ctx, i, m, wins, teams) {
    const nick = ctx.nickOf(i);
    const ghost = !m.alive && m.g;
    const ups = ghost ? '👻' : '💣' + (m.bombs || 1) + ' 🔥' + (m.range || 2) + (m.boots ? ' 👟' : '') + (m.k ? ' 🧤' : '') + (m.cu ? ' 💀' : '');
    const team = teams && teams[i] >= 0 ? ' bm-t' + teams[i] : '';
    return '<span class="bchip s' + i + (m.alive === false ? ' out' : '') + (i === ctx.seat ? ' me' : '') + team + '">'
      + '<i>' + (i + 1) + '</i><span class="bnick">' + ctx.esc(nick) + '</span>' + (teams ? '' : ' <b>' + (wins[i] || 0) + '</b>')
      + ' <span class="bups">' + ups + '</span></span>';
  }

  function hud(root, ctx, f, st) {
    let el = root.querySelector(':scope > .bhud');
    if (!el) {
      el = document.createElement('div');
      el.className = 'bhud';
      root.insertBefore(el, root.firstChild);
    }
    const wins = (f && f.wins) || [];
    const men = (f && f.p) || [];
    const teams = st.teams;
    let html = '';
    if (teams) {
      // у командах рахунок один на команду — окремим чіпом спереду
      const score = [0, 1].map((t) => { const s = teams.indexOf(t); return s >= 0 ? (wins[s] || 0) : 0; });
      html += '<span class="bchip bm-score"><span class="bm-t0n">' + TEAMS[0] + ' <b>' + score[0] + '</b></span> : <span class="bm-t1n"><b>'
        + score[1] + '</b> ' + TEAMS[1] + '</span></span>';
    }
    for (let i = 0; i < SEATN; i++) {
      if (!ctx.nickOf(i)) continue;
      html += chipOf(ctx, i, men[i] || {}, wins, teams);
    }
    if (f && f.phase === 'go') {
      const v = ctx.view || {};
      if (v.shrinkAt && (f.t || 0) < v.shrinkAt) {
        // зі стисканням годинник рахує до стін, а не до нічиєї: саме це й треба знати
        const left = v.shrinkAt - (f.t || 0);
        html += '<span class="bchip bmclock' + (left * TICK_MS <= 10000 ? ' hot' : '') + '">🧱 ' + clock(left) + '</span>';
      } else if (v.shrinkAt) {
        html += '<span class="bchip bmclock hot">🧱 стіни сходяться!</span>';
      } else {
        // Годинник раунду: за дві хвилини раунд нічий, і краще бачити це заздалегідь, ніж дивуватись.
        const limit = v.limit || 2000;
        const left = Math.max(0, limit - (f.t || 0));
        html += '<span class="bchip bmclock' + (left * TICK_MS <= 15000 ? ' hot' : '') + '">⏱ ' + clock(left) + '</span>';
      }
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Стрічка подій і підсумок партії (п. 38)
  // ---------------------------------------------------------------------------------------------

  function who(ctx, i) {
    return '<b class="bm-n s' + i + '">' + ctx.esc(ctx.nickOf(i) || ctx.seatName(i)) + '</b>';
  }

  function eventHtml(ctx, e) {
    const how = e[1], a = e[2], b = e[3];
    switch (how) {
      case 0: return '💥 ' + who(ctx, a) + ' → ' + who(ctx, b);
      case 1: return '😵 ' + who(ctx, b) + ' сам себе';
      case 2: return '🤦 ' + who(ctx, a) + ' → свого ' + who(ctx, b);
      case 3: return '👻 помста ' + who(ctx, a) + ' → ' + who(ctx, b);
      default: return '🧱 ' + who(ctx, b) + ' притисло стіною';
    }
  }

  /// Нові події з кадру (кадри зливаються, тож подія їде в кількох — беремо за id раз).
  function takeEvents(st, ctx, f) {
    if (!f || !f.ev) return;
    const now = performance.now();
    for (const e of f.ev) {
      if (e[0] <= st.evSeen) continue;
      st.evSeen = e[0];
      st.feed.push({ html: eventHtml(ctx, e), at: now });
    }
    if (st.feed.length > 3) st.feed = st.feed.slice(-3);
  }

  function feed(root, ctx, st) {
    let el = root.querySelector(':scope > .bm-feed');
    if (!el) {
      el = document.createElement('div');
      el.className = 'bm-feed';
      el.setAttribute('aria-live', 'polite');
      st.cv.el.insertAdjacentElement('afterend', el);   // одразу під полем, над хрестовиною
    }
    // рядок тримає висоту, поки йде партія (щоб поле не стрибало від кожної події), а в лобі не займає місця
    el.classList.toggle('bm-live', !!ctx.playing);
    const now = performance.now();
    st.feed = st.feed.filter((x) => now - x.at < FEED_MS);
    const html = st.feed.map((x) => '<span>' + x.html + '</span>').join('');
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
  }

  const TITLES = {
    long: ['🏃', 'Найдовше вижив'],
    kills: ['💥', 'Головний підривник'],
    revenge: ['👻', 'Месник з того світу'],
    boxes: ['📦', 'Гроза ящиків'],
    self: ['😵', 'Самопідривник партії'],
    team: ['🤦', 'Свій серед чужих'],
  };

  function summary(root, ctx, st) {
    let el = root.querySelector(':scope > .bm-sum');
    const v = ctx.view || {};
    const show = v.phase === 'over' && v.sum && v.sum.length;
    if (!show) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'bm-sum';
      const after = root.querySelector(':scope > .bm-feed') || st.cv.el;
      after.insertAdjacentElement('afterend', el);
    }
    let html = '';
    for (const s of v.sum) {
      const t = TITLES[s.k];
      if (!t) continue;
      const n = s.k === 'long' ? clock(s.n) : '×' + s.n;
      html += '<div>' + t[0] + ' <span class="bm-sumt">' + t[1] + '</span> — ' + s.s.map((i) => who(ctx, i)).join(', ') + ' <span class="bm-sumn">' + n + '</span></div>';
    }
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
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
    // привидом кнопка кидає помсту — хай і виглядає інакше
    const f = st.last;
    const m = f && f.p && ctx.seat != null ? f.p[ctx.seat] : null;
    const glyph = m && !m.alive && m.g ? '👻' : '💣';
    const bb = el.querySelector('.bbomb');
    if (bb && bb.textContent !== glyph) bb.textContent = glyph;
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
        keys: [], touch: -1, seenAt: 0, W: 15, H: 13, teams: null,
        feed: [], evSeen: 0, lat: 90, echo: null, corr: null, corrAt: 0,
        raf: 0, keyup: null, blur: null, phase: '', round: 0, css: ctx.css, K: scale(),
      };
    }
    root._bomber.ctx = ctx;
    ctx._bomber = root._bomber;
    return root._bomber;
  }

  /// Канвас під розмір поля: 15×13 чи 19×15 (опція столу). Ширшому полю — ширша стеля (bm-big у bomber.css).
  function board(root, st) {
    const [W, H] = dims(st);
    const cls = 'bboard' + (W > 15 ? ' bm-big' : '');
    if (st.cv && st.cvW === W && st.cvH === H) return;
    st.cvW = W;
    st.cvH = H;
    st.cv = HGames.ui.canvas(root, { w: W * PX * st.K, h: H * PX * st.K, cls });
    st.layer = null;
    if (st.io) { st.io.disconnect(); st.io.observe(st.cv.el); }
  }

  /// Куди бігти зараз: палець на хрестовині головніший, далі — остання із затиснутих клавіш. Затиснув →,
  /// додав ↑ і відпустив ↑ — бомбер знову біжить праворуч, а не стоїть (раніше зупинявся, хоч → іще тиснули).
  /// Серверу шлемо лише зміну.
  function steer(st, ctx) {
    const d = st.touch >= 0 ? st.touch : (st.keys.length ? st.keys[st.keys.length - 1] : -1);
    if (st.held === d) return;
    st.held = d;
    if (ctx && ctx.mine && ctx.playing) {
      ctx.input('move', { dir: d });
      // відлуння для затримки: сервер покаже цей напрямок у mv, щойно бомбер рушить (прокляття — навпаки)
      const f = st.last, m = f && f.p ? f.p[ctx.seat] : null;
      if (d >= 0 && !st.echo && m && m.alive) st.echo = { d: m.cu === 'rev' ? (d + 2) % 4 : d, at: performance.now() };
    }
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
      v: '2026-09-29',
      title: 'Бомбер: мапи, привиди, команди й хаос',
      items: [
        '🗺️ Нові опції столу: мапи «Відкрита» й «Лабіринт», просторе поле 19×15, стискання з 90-ї секунди',
        '👻 Підірвали — літай привидом крізь стіни й раз на раунд кинь повільну помсту; 🧤 копняк і 💀 прокляття в «хаосі»',
        '🥒🍅 Команди 2×2 і 3×3: Огірки проти Помідорів, свої бомби своїх не ранять (або дружній вогонь — опцією)',
        '📰 Під полем — хто кого підірвав, а наприкінці — звання: найдовше вижив, самопідривник партії…',
        '⚡ Свій бомбер рушає одразу, без затримки зв\'язку',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      st.interp = HGames.ui.Interp();
      board(root, st);
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
      if (v && v.width && v.height && (v.width !== st.W || v.height !== st.H)) {
        st.W = v.width;
        st.H = v.height;
        board(root, st);
      }
      if (v && v.walls && v.walls.length) st.walls = v.walls;
      st.teams = (v && v.teams) || null;
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
        if (jump) { st.interp.reset(); st.corr = null; }
        st.interp.push(v);
        takeEvents(st, ctx, v);
      }
      pad(root, ctx, st);
      hud(root, ctx, st.last, st);
      feed(root, ctx, st);
      summary(root, ctx, st);
      fitPhone(root, st, ctx, '.bhud', '.bpad');
      syncHeld(st);
      st.cv.resize();
      spin(st);        // якщо картку колись перемонтують — цикл малювання не загубиться
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      st.ctx = ctx;
      if (!st.cv || !f) return;
      const now = performance.now();
      noteBooms(st, f);
      correct(st, f, now);
      st.last = f;
      st.seenAt = now;
      st.interp.push(f);
      takeEvents(st, ctx, f);
      hud(root, ctx, f, st);
      feed(root, ctx, st);
      pad(root, ctx, st);
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
      const v = ctx.view || {};
      if (!ctx.playing || !f || !f.phase) return '';
      if (f.phase === 'start') {
        if (v.note && v.round === 1) return v.note;
        const teams = v.teams, me = ctx.mine ? ctx.seat : -1;
        if (teams && me >= 0 && teams[me] >= 0) return 'Готуйсь… ти за ' + TEAMS[teams[me]] + (v.ff ? ' (дружній вогонь!)' : '');
        return 'Готуйсь…';
      }
      if (f.phase === 'pause') {
        const st = ctx._bomber;
        const w = st ? roundWinner(st, f) : null;
        if (w && w.team !== undefined) return 'Раунд беруть ' + TEAMS[w.team];
        return w ? 'Раунд бере ' + (ctx.nickOf(w.seat) || ctx.seatName(w.seat)) : 'Раунд нічий';
      }
      if (f.phase === 'over') return '';
      if (!ctx.mine) return 'Дивишся збоку';
      const m = f.p && f.p[ctx.seat];
      if (m && !m.alive && m.g === 1) return '👻 Ти привид: літай крізь стіни, ' + (HGames.ui.coarse() ? '👻' : 'пробіл') + ' — кинути повільну помсту (одна на раунд)';
      if (m && !m.alive && m.g === 2) return '👻 Помсту кинуто — лети дивитись, чи влучила';
      if (m && m.cu) return '💀 Прокляття: ' + CURSE[m.cu] + '! Торкнись когось — перейде на нього';
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
