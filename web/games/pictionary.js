/*
  Піктіонарі. Правила, слово, час і очки живуть на сервері (Impl/Pictionary.cs); модуль малює полотно,
  шле штрихи художника і здогадки решти.

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { phase: 'pick'|'draw'|'reveal'|'done', turn, drawer, turnNo, turns, round, rounds,
      choices: string[]|null (лише художникові, поки обирає), word: string|null (художнику, тим, хто вгадав,
      і всім після ходу), mask: 'к_т', until, totalMs, scores[], gained[], guessed[], left[],
      drawing: { ver, n, z }, feed: [{ id, seat, kind: 'guess'|'ok'|'word'|'skip'|'pass'|'left', text }], result,
      // прохід №3 (29.09):
      mode: 'party'|'duo', reroll, choicesHome: bool[]|null, homeBy, home: { n, mine[] }, reacts: [😂, 🔥, 🤯],
      duo: { count, until, totalMs, best, record }|null, gallery: [{ t, seat, nick, word, home, re, hearts }]|null,
      myVote, votes, artists[], pinned[] }   // phase ще й 'vote' — галерея з ❤ після останнього малюнка
  Кадр (подія 'frame', лише коли щось змінилось; слова в ньому нема):
    { ph, drawer, ver, from, n, z, mask, until, guessed, feed, re: [реакції]|null, dc: скільки вгадано удвох }
  Малюнок на дроті — рядок z (unpack нижче, Impl/SketchWire.cs); старий сервер слав ops масивами — теж читаємо.

  Операція малюнка — масив цілих: [вид, штрих, колір, товщина, x0, y0, x1, y1, …]; вид 0 — лінія,
  1 — заливка (x0, y0 — точка). Полотно логічне: 1000 × 750.
  Ходи: Act('pick', { i }), Act('guess', { text });
        Input('draw', { s, c, w, p: [x, y, …] }), Input('fill', { s, c, x, y }), Input('undo'), Input('clear'), Input('sync').
        Act('reroll') — три нові слова (раз за партію; удвох — пропустити слово), Input('react', { e }) — 😂🔥🤯,
        Act('vote', { t }) — ❤ малюнку, Act('home', { text }) / Act('unhome', { text }) — слова компанії в лобі.
  HTTP: POST /api/games/pictionary/react { room, e } (глядач), POST …/pin { room, t } (📌 після партії),
        GET …/art?room&t (малюнок ходу, якого не застав), GET …/pairs (рекорди пар). Публічний альбом — /pictionary-album/.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.5 13.5c1.6 0 2.6-.6 3-2l6.8-6.8a1.5 1.5 0 0 0-2.1-2.1L3.4 9.4c-1.3.5-1.9 1.6-.9 4.1z" fill="none" stroke="var(--accent)" stroke-width="1.5" stroke-linejoin="round"/>'
    + '<path d="M9.4 3.4l2.2 2.2" stroke="var(--clay)" stroke-width="1.5"/></svg>';

  const W = 1000, H = 750;
  /// Та сама кількість, що Pictionary.Colors на сервері. 0 — білий, ним же стирає гумка.
  const PALETTE = [
    '#ffffff', '#000000', '#7f7f7f', '#c3c3c3', '#5d4037', '#8d5a2b', '#e53935', '#fb8c00', '#fdd835', '#fff59d',
    '#43a047', '#a5d6a7', '#00897b', '#1e88e5', '#90caf9', '#3949ab', '#8e24aa', '#f48fb1', '#e8b48a', '#ff7043',
  ];
  const SIZES = [3, 8, 16, 32];
  /// Шматок штриха летить щонайменше так часто (і не частіше: хаб бере 30 Input на секунду).
  const FLUSH_MS = 60;
  const CHUNK_MAX = 200;
  const SYNC_MS = 2000;
  /// Точка, що лежить ближче за стільки логічних одиниць до прямої між сусідками, малюнку нічого не дає, лише
  /// байти (прохід 28.09: миша на 1000 Гц слала ~500 точок на секунду, пряма лінія — сотні точок; зі спрощенням — у рази менше).
  const SIMPLIFY = 0.9;
  /// Шматок чужого штриха домальовується за стільки мілісекунд (кадр — раз на 100 мс), а не з'являється стрибком.
  const SMOOTH_MS = 100;
  const REACTS = ['😂', '🔥', '🤯'];
  const ALBUM_URL = '/pictionary-album/';

  const seatsOf = (ctx) => (ctx.room && ctx.room.seats ? ctx.room.seats.length : 10);

  // =========================================================================================
  // Стан картки
  // =========================================================================================

  function st(root) {
    if (!root._pc) {
      const buf = document.createElement('canvas');
      buf.width = W; buf.height = H;
      root._pc = {
        buf, bctx: buf.getContext('2d', { willReadFrequently: true }),
        ver: -1, ops: [], n: 0,
        tool: 'pen', color: 1, size: 1,
        stroke: 1, cur: null, flushAt: 0, flushTimer: 0,
        local: [],          // шматки, які художник уже намалював, а сервер ще не повернув: [{ s, i, op }]
        feed: new Map(),
        lastSync: 0, dirty: true, raf: 0, timer: 0,
        album: new Map(),   // turnNo → { word, drawer, url } — мініатюри малюнків партії для альбому наприкінці
        albumRound: 0,
        anim: null,         // чужі лінії з останнього кадру, що домальовуються: { ops, pts, t0 }
        shape: null,        // пряма чи овал, яку художник саме тягне: { tool, c, w, x0, y0, x1, y1, id, round }
        fetching: new Set(),
        pairs: null,
        phase: '',
      };
      clearBuf(root._pc);
    }
    return root._pc;
  }

  function clearBuf(s) {
    s.bctx.globalCompositeOperation = 'source-over';
    s.bctx.fillStyle = '#ffffff';
    s.bctx.fillRect(0, 0, W, H);
  }

  // =========================================================================================
  // Малювання операцій у буфер 1000 × 750
  // =========================================================================================

  function drawOp(c, op) {
    if (!op || op.length < 6) return;
    const color = PALETTE[op[2]] || '#000';
    if (op[0] === 1) { flood(c, op[4], op[5], color); return; }
    c.strokeStyle = color;
    c.fillStyle = color;
    c.lineWidth = op[3];
    c.lineCap = 'round';
    c.lineJoin = 'round';
    if (op.length === 6) {
      c.beginPath();
      c.arc(op[4], op[5], op[3] / 2, 0, Math.PI * 2);
      c.fill();
      return;
    }
    c.beginPath();
    c.moveTo(op[4], op[5]);
    for (let i = 6; i + 1 < op.length; i += 2) c.lineTo(op[i], op[i + 1]);
    c.stroke();
  }

  function hex(color) {
    const n = parseInt(color.slice(1), 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }

  /// Заливка рядками. Допуск — щоб згладжені краї ліній не лишали білої бахроми всередині.
  function flood(c, x0, y0, color) {
    x0 = Math.max(0, Math.min(W - 1, x0 | 0));
    y0 = Math.max(0, Math.min(H - 1, y0 | 0));
    const img = c.getImageData(0, 0, W, H);
    const d = img.data;
    const at = (y0 * W + x0) * 4;
    const tr = d[at], tg = d[at + 1], tb = d[at + 2];
    const [fr, fg, fb] = hex(color);
    if (Math.abs(tr - fr) + Math.abs(tg - fg) + Math.abs(tb - fb) < 12) return;
    const TOL = 90;
    const same = (i) => Math.abs(d[i] - tr) + Math.abs(d[i + 1] - tg) + Math.abs(d[i + 2] - tb) <= TOL;
    const seen = new Uint8Array(W * H);
    const stack = [x0, y0];
    while (stack.length) {
      const y = stack.pop(), x = stack.pop();
      let l = x;
      while (l >= 0 && !seen[y * W + l] && same((y * W + l) * 4)) l--;
      l++;
      let r = x;
      while (r < W && !seen[y * W + r] && same((y * W + r) * 4)) r++;
      for (let i = l; i < r; i++) {
        const p = y * W + i;
        seen[p] = 1;
        d[p * 4] = fr; d[p * 4 + 1] = fg; d[p * 4 + 2] = fb; d[p * 4 + 3] = 255;
        if (y > 0 && !seen[p - W] && same((p - W) * 4)) stack.push(i, y - 1);
        if (y < H - 1 && !seen[p + W] && same((p + W) * 4)) stack.push(i, y + 1);
      }
    }
    c.putImageData(img, 0, 0);
  }

  /// Спростити шматок штриха (Рамер — Дуглас — Пекер, відстань до відрізка): лишаються кінці й ті точки, без яких
  /// лінія відхилилась би більше ніж на eps. Кінці не чіпаємо — з останньої точки починається наступний шматок.
  function simplify(p, eps) {
    const n = p.length / 2;
    if (n <= 2) return p;
    const keep = new Uint8Array(n);
    keep[0] = keep[n - 1] = 1;
    const e2 = eps * eps;
    const stack = [0, n - 1];
    while (stack.length) {
      const b = stack.pop(), a = stack.pop();
      const ax = p[a * 2], ay = p[a * 2 + 1], dx = p[b * 2] - ax, dy = p[b * 2 + 1] - ay, len2 = dx * dx + dy * dy;
      let best = -1, far = e2;
      for (let i = a + 1; i < b; i++) {
        const px = p[i * 2] - ax, py = p[i * 2 + 1] - ay;
        const t = len2 ? Math.max(0, Math.min(1, (px * dx + py * dy) / len2)) : 0;
        const ex = px - t * dx, ey = py - t * dy, d2 = ex * ex + ey * ey;
        if (d2 > far) { far = d2; best = i; }
      }
      if (best >= 0) { keep[best] = 1; stack.push(a, best, best, b); }
    }
    const out = [];
    for (let i = 0; i < n; i++) if (keep[i]) out.push(p[i * 2], p[i * 2 + 1]);
    return out;
  }

  /// Малюнок на дроті рядком (прохід №3, Impl/SketchWire.cs): байти в base64 — вид, штрих (varint), колір, товщина,
  /// скільки точок (varint), перша точка x, y (varint), далі різниці з попередньою (зиґзаґ-varint). У ~3 рази менше,
  /// ніж масиви чисел у JSON.
  function unpack(z) {
    if (!z) return [];
    let b;
    try {
      const bin = atob(z);
      b = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) b[i] = bin.charCodeAt(i);
    } catch { return []; }
    let at = 0;
    const v = () => {
      let x = 0, sh = 0, c;
      do { c = b[at++] | 0; x += (c & 127) * 2 ** sh; sh += 7; } while (c >= 128 && at < b.length);
      return x;
    };
    const zz = (u) => (u % 2 ? -(u + 1) / 2 : u / 2);
    const ops = [];
    while (at < b.length) {
      const kind = b[at++], stroke = v(), color = b[at++] | 0, width = b[at++] | 0, n = v();
      let x = v(), y = v();
      const op = [kind, stroke, color, width, x, y];
      for (let k = 1; k < n; k++) { x += zz(v()); y += zz(v()); op.push(x, y); }
      ops.push(op);
    }
    return ops;
  }
  const opsOf = (d) => (d.ops ? d.ops.slice() : unpack(d.z));

  /// Знімок буфера одразу після останньої заливки. Заливка — найдорожче (прохід 28.09: ~35 мс на великій площі
  /// в ноуті, на телефоні в рази довше), а «↶» після неї перемальовував усе з нуля — кожну заливку наново.
  function keepMark(s, i) {
    if (!s.mark) {
      const c = document.createElement('canvas');
      c.width = W; c.height = H;
      s.mark = { c, g: c.getContext('2d'), n: 0, op: null };
    }
    s.mark.g.drawImage(s.buf, 0, 0);
    s.mark.n = i + 1;
    s.mark.op = s.ops[i];
  }

  const sameOp = (a, b) => !!a && !!b && a.length === b.length && a.every((x, i) => x === b[i]);

  /// Перемалювати малюнок цілком. «↶» і повний кадр після нього приносять той самий початок малюнка, тож
  /// починаємо зі знімка після останньої заливки, якщо вона на місці, і домальовуємо лише лінії після неї.
  function redrawAll(s) {
    s.anim = null;   // малюємо все одразу — домальовувати нема чого
    const m = s.mark;
    let from = 0;
    if (m && m.n > 0 && m.n <= s.ops.length && sameOp(s.ops[m.n - 1], m.op)) {
      s.bctx.globalCompositeOperation = 'source-over';
      s.bctx.drawImage(m.c, 0, 0);
      from = m.n;
    } else {
      if (m) m.n = 0;
      clearBuf(s);
    }
    for (let i = from; i < s.ops.length; i++) {
      drawOp(s.bctx, s.ops[i]);
      if (s.ops[i][0] === 1) keepMark(s, i);
    }
    s.dirty = true;
  }

  // =========================================================================================
  // Синхронізація малюнка: вид — повний, кадр — дописане або цілком нова версія
  // =========================================================================================

  function reset(root, s, drawing) {
    s.ver = drawing.ver;
    s.ops = opsOf(drawing);
    s.n = s.ops.length;
    // шматки, яких сервер уже не має (очистка, новий хід), художникові малювати більше не треба
    s.local = s.local.filter((l) => countOf(s, l.s) <= l.i);
    let maxStroke = 0;
    for (const op of s.ops) if (op[1] > maxStroke) maxStroke = op[1];
    if (s.stroke <= maxStroke) s.stroke = maxStroke + 1;
    redrawAll(s);
    paintSoon(root);
  }

  /// Дописати операції з кадру. Чужі лінії (smooth) лягають у буфер не одразу: paint домальовує їх точка за точкою
  /// за SMOOTH_MS — «дивимось, як малює», а не стрибок раз на 100 мс (прохід №3, п. 30). Заливку — одразу.
  function append(root, s, ops, smooth) {
    commitAnim(s);
    let lines = [], pts = 0;
    for (const op of ops) {
      s.ops.push(op);
      if (smooth && op[0] === 0 && op.length > 6) { lines.push(op); pts += (op.length - 4) / 2; continue; }
      for (const l of lines) drawOp(s.bctx, l);
      lines = []; pts = 0;
      drawOp(s.bctx, op);
      if (op[0] === 1) keepMark(s, s.ops.length - 1);
    }
    if (lines.length) s.anim = { ops: lines, pts, t0: performance.now() };
    s.n = s.ops.length;
    s.local = s.local.filter((l) => countOf(s, l.s) <= l.i);
    s.dirty = true;
    paintSoon(root);
  }

  /// Домалювати в буфер усе, що ще «пливе».
  function commitAnim(s) {
    if (!s.anim) return;
    for (const op of s.anim.ops) drawOp(s.bctx, op);
    s.anim = null;
    s.dirty = true;
  }

  /// Скільки шматків штриха stroke уже є на сервері.
  function countOf(s, stroke) {
    let k = 0;
    for (const op of s.ops) if (op[1] === stroke && op[0] === 0) k++;
    return k;
  }

  function fromView(root, ctx, v) {
    const s = st(root);
    const d = v.drawing;
    if (!d) return;
    // Вид міг скластися раніше за кадри, які вже прийшли: старішим малюнком новіший не затираємо.
    if (d.ver > s.ver || (d.ver === s.ver && d.n > s.n)) reset(root, s, d);
  }

  function fromFrame(root, ctx, f) {
    const s = st(root);
    if (f.ver < s.ver) return;          // запізнілий кадр старої версії
    if (f.ver > s.ver) {
      if (f.from === 0) reset(root, s, { ver: f.ver, ops: opsOf(f) });
      else askSync(ctx, s);
      return;
    }
    if (f.from > s.n) { askSync(ctx, s); return; }
    // художник своє бачить одразу (s.local), йому домальовувати нема чого
    if (f.n > s.n) append(root, s, opsOf(f).slice(s.n - f.from), !(ctx.mine && ctx.seat === f.drawer));
  }

  function askSync(ctx, s) {
    if (!ctx.mine) return;
    const now = Date.now();
    if (now - s.lastSync < SYNC_MS) return;
    s.lastSync = now;
    ctx.input('sync');
  }

  // =========================================================================================
  // Екранне полотно
  // =========================================================================================

  function paintSoon(root) {
    const s = st(root);
    if (s.raf) return;
    s.raf = requestAnimationFrame(() => { s.raf = 0; paint(root); });
  }

  function paint(root) {
    const s = st(root);
    const el = root.querySelector('.pccanvas');
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const pw = Math.max(1, Math.round(rect.width * dpr)), ph = Math.max(1, Math.round(rect.height * dpr));
    if (el.width !== pw || el.height !== ph) { el.width = pw; el.height = ph; }
    const c = el.getContext('2d');
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.imageSmoothingEnabled = true;
    c.drawImage(s.buf, 0, 0, pw, ph);
    let again = false;
    if (s.anim) {
      const p = (performance.now() - s.anim.t0) / SMOOTH_MS;
      if (p >= 1 || document.hidden) {
        commitAnim(s);
        c.drawImage(s.buf, 0, 0, pw, ph);
      } else {
        // перші p·100 % точок шматка — по черзі, як їх вів художник
        c.setTransform(pw / W, 0, 0, ph / H, 0, 0);
        let budget = Math.max(2, Math.round(s.anim.pts * p));
        for (const op of s.anim.ops) {
          if (budget <= 0) break;
          const n = (op.length - 4) / 2;
          drawOp(c, budget >= n ? op : op.slice(0, 4 + 2 * Math.max(2, budget)));
          budget -= n;
        }
        c.setTransform(1, 0, 0, 1, 0, 0);
        again = true;
      }
    }
    // Своє, ще не підтверджене сервером, — поверх: художник бачить лінію одразу, а не за тик.
    // шматок, який сервер так і не повернув (відмовив), за кілька секунд зникає сам
    const stale = Date.now() - 4000;
    s.local = s.local.filter((l) => l.t > stale);
    const pending = s.local.map((l) => l.op);
    if (s.cur && s.cur.p.length >= 2) pending.push([0, s.cur.s, s.cur.c, s.cur.w, ...s.cur.p]);
    if (s.shape) pending.push([0, 0, s.shape.c, s.shape.w, ...shapePoints(s.shape)]);   // тінь прямої чи овалу
    if (pending.length) {
      c.setTransform(pw / W, 0, 0, ph / H, 0, 0);
      for (const op of pending) drawOp(c, op);
    }
    s.dirty = false;
    if (again) paintSoon(root);
  }

  // =========================================================================================
  // Пряма й овал (прохід №3, п. 31): тягнеш — бачиш тінь, відпускаєш — лягає одним штрихом
  // =========================================================================================

  const clampX = (x) => Math.max(0, Math.min(W, Math.round(x)));
  const clampY = (y) => Math.max(0, Math.min(H, Math.round(y)));

  /// Точки фігури: пряма — два кінці; овал — вписаний у прямокутник від точки натиску до пальця (Shift — рівне коло).
  function shapePoints(sh) {
    if (sh.tool === 'line') return [sh.x0, sh.y0, sh.x1, sh.y1];
    let rx = Math.abs(sh.x1 - sh.x0) / 2, ry = Math.abs(sh.y1 - sh.y0) / 2;
    if (sh.round) rx = ry = Math.max(rx, ry);
    if (rx < 2 && ry < 2) return [sh.x0, sh.y0];
    const cx = sh.x0 + (sh.x1 >= sh.x0 ? rx : -rx), cy = sh.y0 + (sh.y1 >= sh.y0 ? ry : -ry);
    const n = Math.max(16, Math.min(96, Math.round(Math.PI * (rx + ry) / 10)));
    const p = [];
    for (let i = 0; i <= n; i++) {
      const a = i / n * Math.PI * 2;
      p.push(clampX(cx + rx * Math.cos(a)), clampY(cy + ry * Math.sin(a)));
    }
    return p;
  }

  function finishShape(root, ctx) {
    const s = st(root);
    const sh = s.shape;
    s.shape = null;
    if (!sh) return;
    const p = shapePoints(sh);
    const stroke = s.stroke++;
    ctx.input('draw', { s: stroke, c: sh.c, w: sh.w, p });
    s.local.push({ s: stroke, i: 0, t: Date.now(), op: [0, stroke, sh.c, sh.w, ...p] });
    paintSoon(root);
  }

  // =========================================================================================
  // Художник: вказівник → шматки штриха
  // =========================================================================================

  function canDraw(ctx) {
    const v = ctx.view || {};
    return !!ctx.mine && !!ctx.playing && v.phase === 'draw' && v.drawer === ctx.seat;
  }

  function point(el, e) {
    const r = el.getBoundingClientRect();
    return [
      Math.round(Math.max(0, Math.min(W, (e.clientX - r.left) / r.width * W))),
      Math.round(Math.max(0, Math.min(H, (e.clientY - r.top) / r.height * H))),
    ];
  }

  function flush(root, ctx, final) {
    const s = st(root);
    clearTimeout(s.flushTimer);
    s.flushTimer = 0;
    const cur = s.cur;
    if (!cur || cur.p.length < 2) { if (final) s.cur = null; return; }
    if (cur.sent === cur.p.length && !final) return;
    const p = simplify(cur.p, SIMPLIFY);
    if (p.length >= 2) {
      ctx.input('draw', { s: cur.s, c: cur.c, w: cur.w, p });
      s.local.push({ s: cur.s, i: cur.chunks, t: Date.now(), op: [0, cur.s, cur.c, cur.w, ...p] });
      cur.chunks++;
    }
    // наступний шматок починається з останньої точки цього — лінія не рветься
    const lx = p[p.length - 2], ly = p[p.length - 1];
    if (final) s.cur = null;
    else { cur.p = [lx, ly]; cur.sent = 2; }
    paintSoon(root);
  }

  function bindCanvas(root, ctx) {
    const el = root.querySelector('.pccanvas');
    if (el._bound) return;
    el._bound = true;
    const s = st(root);

    // Довге натискання пальцем (Steam Deck / тач) інакше відкриває меню картинки
    el.addEventListener('contextmenu', (e) => e.preventDefault());

    el.addEventListener('pointerdown', (e) => {
      const c = root._ctx;
      if (!c || !canDraw(c)) return;
      // Другий палець (долоня на Steam Deck, щипок) і права кнопка миші штриха не починають.
      if (!e.isPrimary || (e.pointerType === 'mouse' && e.button !== 0)) return;
      e.preventDefault();
      // Штрих, що лишився недомальованим (палець зірвався без pointerup), закриваємо — а не губимо.
      if (s.cur) flush(root, c, true);
      const [x, y] = point(el, e);
      if (s.tool === 'fill') {
        c.input('fill', { s: s.stroke++, c: s.color, x, y });
        return;
      }
      if (s.tool === 'line' || s.tool === 'oval') {
        try { el.setPointerCapture(e.pointerId); } catch { /* старі браузери */ }
        s.shape = { tool: s.tool, c: s.color, w: SIZES[s.size], x0: x, y0: y, x1: x, y1: y, id: e.pointerId, round: e.shiftKey };
        paintSoon(root);
        return;
      }
      try { el.setPointerCapture(e.pointerId); } catch { /* старі браузери */ }
      const color = s.tool === 'eraser' ? 0 : s.color;
      const w = SIZES[s.size] * (s.tool === 'eraser' ? 2 : 1);
      s.cur = { s: s.stroke++, c: color, w, p: [x, y], sent: 0, chunks: 0, id: e.pointerId };
      paintSoon(root);
    });

    el.addEventListener('pointermove', (e) => {
      const c = root._ctx;
      if (s.shape && e.pointerId === s.shape.id) {
        const [x, y] = point(el, e);
        s.shape.x1 = x; s.shape.y1 = y; s.shape.round = e.shiftKey;
        paintSoon(root);
        return;
      }
      // лише той палець, що почав штрих: інакше другий дотик малював би зиґзаґи через усе полотно
      if (!s.cur || !c || e.pointerId !== s.cur.id) return;
      const events = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
      for (const ev of events.length ? events : [e]) {
        const [x, y] = point(el, ev);
        const p = s.cur.p;
        const dx = x - p[p.length - 2], dy = y - p[p.length - 1];
        if (dx * dx + dy * dy < 4) continue;
        p.push(x, y);
      }
      if (s.cur.p.length / 2 >= CHUNK_MAX) flush(root, c, false);
      else if (!s.flushTimer) s.flushTimer = setTimeout(() => flush(root, c, false), FLUSH_MS);
      paintSoon(root);
    });

    const end = (e) => {
      const c = root._ctx;
      if (s.shape && c && (!e || e.pointerId === s.shape.id)) { finishShape(root, c); return; }
      if (!s.cur || !c || (e && e.pointerId !== s.cur.id)) return;
      flush(root, c, true);
    };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
    el.addEventListener('lostpointercapture', end);

    new ResizeObserver(() => paintSoon(root)).observe(el);
    // інша ширина вікна — інакше лягають чіпи місць над карткою, і полотно починається деінде
    // У наступному кадрі, а не просто в колбеку: fitStage міняє --pctop, від якого залежить висота .pcwrap, і зміна
    // розміру всередині спостерігача давала «ResizeObserver loop completed…» і зайвий перерахунок у тому ж кадрі.
    new ResizeObserver(() => requestAnimationFrame(() => fitStage(root))).observe(root.querySelector('.pcwrap'));
  }

  // =========================================================================================
  // Розмітка
  // =========================================================================================

  function toolbar(root, ctx) {
    const s = st(root);
    const bar = root.querySelector('.pctools');
    const on = canDraw(ctx);
    bar.hidden = !on;
    if (!on) return;
    if (!bar.firstChild) {
      bar.innerHTML = '<div class="pcpal">'
        + PALETTE.map((c, i) => '<button type="button" class="pcsw" data-c="' + i + '" style="background:' + c + '" title="Колір"></button>').join('')
        + '</div><div class="pcrow">'
        + SIZES.map((z, i) => '<button type="button" class="pcsz" data-z="' + i + '" title="Товщина ' + z + '"><i style="width:' + Math.max(4, z * .7) + 'px;height:' + Math.max(4, z * .7) + 'px"></i></button>').join('')
        + '<span class="pcsep"></span>'
        + '<button type="button" class="pct" data-t="pen" title="Пензель">✏️</button>'
        + '<button type="button" class="pct" data-t="eraser" title="Гумка">🧽</button>'
        + '<button type="button" class="pct" data-t="fill" title="Заливка">🪣</button>'
        + '<button type="button" class="pct" data-t="line" title="Пряма: тягни від кінця до кінця">╱</button>'
        + '<button type="button" class="pct" data-t="oval" title="Овал: тягни навскіс (Shift — рівне коло)">◯</button>'
        + '<span class="pcsep"></span>'
        + '<button type="button" class="pca" data-a="undo" title="Скасувати (Ctrl+Z)">↶</button>'
        + '<button type="button" class="pca" data-a="clear" title="Очистити все">🗑</button>'
        + '<button type="button" class="pca pcskip" data-a="skip" title="Не йде — пропустити слово" hidden>⏭</button>'
        + '</div>';
      bar.addEventListener('click', (e) => {
        const b = e.target.closest('button');
        const c = root._ctx;
        if (!b || !c) return;
        if (b.dataset.c != null) { s.color = +b.dataset.c; if (s.tool === 'eraser') s.tool = 'pen'; }
        else if (b.dataset.z != null) s.size = +b.dataset.z;
        else if (b.dataset.t) s.tool = b.dataset.t;
        else if (b.dataset.a === 'undo') undo(root, c);
        else if (b.dataset.a === 'clear') { if (confirm('Стерти весь малюнок?')) { s.local = []; c.input('clear'); } }
        else if (b.dataset.a === 'skip') { s.local = []; c.act('reroll'); }
        marks(root);
      });
    }
    const skip = bar.querySelector('.pcskip');
    const v = ctx.view || {};
    skip.hidden = !(v.mode === 'duo' && v.reroll);
    marks(root);
  }

  function undo(root, ctx) {
    const s = st(root);
    if (s.cur) flush(root, ctx, true);
    const last = s.local.length ? s.local[s.local.length - 1].s : null;
    if (last != null) s.local = s.local.filter((l) => l.s !== last);
    ctx.input('undo');
    paintSoon(root);
  }

  function marks(root) {
    const s = st(root);
    root.querySelectorAll('.pcsw').forEach((b) => b.classList.toggle('on', +b.dataset.c === s.color && s.tool !== 'eraser'));
    root.querySelectorAll('.pcsz').forEach((b) => b.classList.toggle('on', +b.dataset.z === s.size));
    root.querySelectorAll('.pct').forEach((b) => b.classList.toggle('on', b.dataset.t === s.tool));
    const el = root.querySelector('.pccanvas');
    if (el) el.dataset.tool = s.tool;
  }

  /// Найсвіжіший кадр цієї ж фази — або нічого.
  function fresh(ctx, v) {
    const f = ctx.frame;
    return f && f.ph === v.phase && f.drawer === v.drawer ? f : null;
  }

  function untilOf(ctx, v) {
    const f = fresh(ctx, v);
    return new Date((f && f.until) || v.until || 0).getTime();
  }

  /// Стіл ще збирається: вид у гри вже є (фаза «вибір», художника нема), але партії ще нема.
  const lobby = (ctx) => !!ctx.room && ctx.room.status === 'lobby';

  function wordLine(root, ctx, v) {
    const f = fresh(ctx, v);
    const el = root.querySelector('.pcword');
    let html;
    if (lobby(ctx)) {
      html = '';
    } else if (v.phase === 'pick') {
      html = v.drawer === ctx.seat && ctx.mine
        ? '<span class="muted">Обери слово</span>'
        : '<span class="muted">' + ctx.esc(ctx.nickOf(v.drawer) || 'Художник') + ' обирає слово…</span>';
    } else if (v.word && v.phase === 'draw') {
      html = '<b class="pcreal">' + ctx.esc(v.word) + '</b>';
    } else if (v.phase === 'draw') {
      const mask = (f && f.mask) || v.mask || '';
      const parts = mask.split(/([ \-])/);
      html = parts.map((p) => {
        if (p === ' ') return '<span class="pcgap"></span>';
        if (p === '-') return '<span class="pcl">-</span>';
        return '<span class="pcw">' + p.split('').map((ch) => '<span class="pcl' + (ch === '_' ? ' hid' : '') + '">'
          + (ch === '_' ? '' : ctx.esc(ch)) + '</span>').join('') + '<sub>' + p.length + '</sub></span>';
      }).join('');
    } else if (v.word) {
      html = '<span class="muted">Слово:</span> <b class="pcreal">' + ctx.esc(v.word) + '</b>';
    } else {
      html = '';
    }
    if (el._html !== html) { el._html = html; el.innerHTML = html; }
  }

  function head(root, ctx, v) {
    const el = root.querySelector('.pchead');
    const f = fresh(ctx, v);
    const duo = v.duo;
    const text = lobby(ctx) ? ''
      : v.phase === 'done' ? 'Партію зіграно'
      : v.phase === 'vote' ? '❤ Галерея партії'
        : duo ? 'Удвох · вгадано ' + ((f && f.dc != null) ? f.dc : duo.count) + (duo.best ? ' · рекорд пари ' + duo.best : '') + ' · малює ' + (ctx.nickOf(v.drawer) || '—')
          : v.turns ? 'Коло ' + (v.round || 1) + ' з ' + (v.rounds || 1) + ' · малює ' + (ctx.nickOf(v.drawer) || '—') : '';
    if (el.textContent !== text) el.textContent = text;
  }

  function timer(root, ctx) {
    const v = ctx.view || {};
    const bar = root.querySelector('.pctime i');
    const num = root.querySelector('.pcsec');
    const live = ctx.playing && (v.phase === 'draw' || v.phase === 'pick' || v.phase === 'reveal' || v.phase === 'vote');
    root.querySelector('.pctime').style.visibility = live ? 'visible' : 'hidden';
    if (!live) { num.textContent = ''; return; }
    // удвох годинник один на всю партію: смужка показує, скільки лишилось із трьох хвилин
    const duo = v.duo;
    const total = (duo ? duo.totalMs : v.totalMs) || 1;
    const left = Math.max(0, (duo ? new Date(duo.until).getTime() : untilOf(ctx, v)) - Date.now());
    bar.style.width = Math.max(0, Math.min(100, left / total * 100)) + '%';
    bar.classList.toggle('hot', v.phase === 'draw' && left < 10000);
    const t = String(Math.ceil(left / 1000));
    if (num.textContent !== t) num.textContent = t;
  }

  function scores(root, ctx, v) {
    const f = fresh(ctx, v);
    const guessed = (f && f.guessed) || v.guessed || [];
    const left = v.left || [];
    const rows = [];
    for (let i = 0; i < seatsOf(ctx); i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      rows.push({ i, nick, score: (v.scores || [])[i] || 0, gained: (v.gained || [])[i] || 0 });
    }
    rows.sort((a, b) => b.score - a.score);
    const html = rows.map((r) => {
      const drawing = (v.phase === 'draw' || v.phase === 'pick') && r.i === v.drawer;
      const ok = v.phase !== 'done' && guessed.indexOf(r.i) >= 0;   // після партії галочки останнього ходу ні до чого
      const mark = drawing ? '✏️' : ok ? '✅' : '';
      const plus = v.phase === 'reveal' && r.gained ? '<em>+' + r.gained + '</em>' : '';
      return '<div class="pcsc' + (ok ? ' ok' : '') + (drawing ? ' drw' : '') + (r.i === ctx.seat ? ' me' : '')
        + (left.indexOf(r.i) >= 0 ? ' off' : '') + '">'
        + '<span class="pcn">' + ctx.esc(r.nick) + '</span><span class="pcm">' + mark + '</span>' + plus
        + '<b>' + r.score + '</b></div>';
    }).join('');
    const el = root.querySelector('.pcscores');
    if (el._html !== html) { el._html = html; el.innerHTML = html; }
  }

  function mergeFeed(s, items, replace) {
    if (!items) return;
    if (replace) {
      // порожня стрічка у виді — нова партія: старе прибираємо все
      const max = items.length ? items.reduce((m, x) => Math.max(m, x.id), 0) : Infinity;
      for (const id of [...s.feed.keys()]) if (id <= max) s.feed.delete(id);
    }
    for (const it of items) s.feed.set(it.id, it);
    const ids = [...s.feed.keys()].sort((a, b) => a - b);
    while (ids.length > 40) s.feed.delete(ids.shift());
  }

  function feed(root, ctx) {
    const s = st(root);
    const list = [...s.feed.values()].sort((a, b) => a.id - b.id);
    const who = (i) => ctx.esc(ctx.nickOf(i) || 'хтось');
    const html = list.map((it) => {
      switch (it.kind) {
        case 'ok': return '<div class="pcf ok">✅ <b>' + who(it.seat) + '</b> вгадує!</div>';
        case 'word': return '<div class="pcf word">Слово було: <b>' + ctx.esc(it.text || '') + '</b></div>';
        case 'skip': return '<div class="pcf word">Хід пропущено</div>';
        case 'pass': return '<div class="pcf muted">⏭ Пропущено: <b>' + ctx.esc(it.text || '') + '</b></div>';
        case 'left': return '<div class="pcf muted">' + who(it.seat) + ' встає з-за столу</div>';
        default: return '<div class="pcf"><b>' + who(it.seat) + ':</b> ' + ctx.esc(it.text || '') + '</div>';
      }
    }).join('') || '<div class="pcf muted">Поки тиша — тут з\'являться здогадки</div>';
    const el = root.querySelector('.pcfeed');
    if (el._html !== html) {
      el._html = html;
      const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 30;
      el.innerHTML = html;
      if (atBottom || !el._seen) el.scrollTop = el.scrollHeight;
      el._seen = true;
    }
  }

  function guessBox(root, ctx, v) {
    const f = fresh(ctx, v);
    const guessed = (f && f.guessed) || v.guessed || [];
    const form = root.querySelector('.pcguess');
    const input = form.querySelector('input');
    const can = !!ctx.mine && !!ctx.playing && v.phase === 'draw' && v.drawer !== ctx.seat && guessed.indexOf(ctx.seat) < 0;
    const drawer = ctx.mine && v.drawer === ctx.seat && v.phase === 'draw';
    input.disabled = !can;
    form.querySelector('button').disabled = !can;
    input.placeholder = lobby(ctx) ? 'Партія ще не почалась' : can ? 'Тяпни здогадку…'
      : drawer ? 'Ти малюєш — вгадують інші'
        : guessed.indexOf(ctx.seat) >= 0 && v.phase === 'draw' ? 'Є! Вгадано — чекаємо інших'
          : !ctx.mine ? 'Дивишся збоку' : 'Зараз не вгадують';
    form.onsubmit = (e) => {
      e.preventDefault();
      const text = input.value.trim();
      const c = root._ctx;
      if (!text || !c) return;
      c.act('guess', { text }).then((r) => {
        if (!r || r.ok) input.value = '';
        input.focus();
      });
    };
  }

  const albumLink = () => '<a class="pcalink" href="' + ALBUM_URL + '" target="_blank" rel="noopener">📖 Альбом Піктіонарі</a>';

  /// Слова компанії в лобі (п. 25): поле, свої слова чіпами з ✕, скільки всього в торбі (чужих слів не видно).
  function homeBox(ctx, v) {
    const h = v.home || { n: 0, mine: [] };
    const mine = h.mine || [];
    let html = '<div class="pchome"><div><b>🏠 Слова компанії</b> <span class="muted small">— докинь до трьох своїх («кумів трактор»): '
      + 'малюватимуть інші, не ти</span></div>';
    if (ctx.mine && mine.length < 3) {
      html += '<form class="pchomef"><input class="pchomein" type="text" maxlength="30" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" placeholder="Своє слово…" enterkeyhint="done">'
        + '<button type="submit">＋</button></form>';
    }
    if (mine.length) html += '<div class="pchomes">' + mine.map((w) => '<span class="chip">' + ctx.esc(w) + ' <button type="button" data-unhome="' + ctx.esc(w) + '" title="Прибрати">✕</button></span>').join('') + '</div>';
    html += '<div class="muted small">У торбі: ' + (h.n || 0) + '</div></div>';
    return html;
  }

  function pairsBox(root, s) {
    if (!s.pairs) {
      s.pairs = [];
      fetch('/api/games/pictionary/pairs').then((r) => r.json()).then((d) => {
        s.pairs = (d && d.rows) || [];
        const c = root._ctx;
        if (c && s.pairs.length) overlay(root, c, c.view || {});
      }).catch(() => {});
    }
    if (!s.pairs.length) return '';
    return '<div class="pcpairs"><b>🏆 Найкращі пари</b>' + s.pairs.slice(0, 5).map((p, k) => '<div><span>' + (k + 1) + '. '
      + escHtml(p.nicks) + '</span><b>' + p.best + '</b></div>').join('') + '</div>';
  }

  function escHtml(t) {
    return String(t == null ? '' : t).replace(/[&<>"']/g, (ch) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]);
  }

  function overlay(root, ctx, v) {
    const el = root.querySelector('.pcover');
    let html = '';
    if (lobby(ctx)) {
      // Раніше тут до старту висіло «Художник обирає слово…» — хоча партії ще нема й художника теж (прохід 28.09).
      const n = ctx.room && ctx.room.seats ? ctx.room.seats.filter((x) => x.nick).length : 0;
      const duo = v.mode === 'duo';
      html = '<div class="pcbox pcrules"><div class="pctitle">✏️ Піктіонарі' + (duo ? ' удвох: скільки встигнемо' : '') + '</div>'
        + (duo
          ? '<div>Три хвилини на двох: малюєте по черзі, слово йде одразу, вгадав — наступне малює інший. '
            + 'Не йде — ⏭ пропусти. Рахунок спільний, рекорд пари — в таблиці.</div>'
          : '<div>Художник малює слово — решта вгадує, пишучи здогадки в поле. Хто вгадав швидше, тому більше очок, '
            + 'художник бере частку від усіх, хто вгадав. Малюють по черзі, наприкінці — ❤ найкращому малюнку.</div>')
        + '<div class="muted small">' + (n < 2 ? (duo ? 'Чекаємо на пару: удвох — рівно двоє' : 'Чекаємо, хто підсяде: треба щонайменше двоє')
          : duo && n > 2 ? 'Удвох — рівно двоє: за столом ' + n
            : 'За столом ' + n + ' — господар тисне «Почати»') + '</div>'
        + (v.phase ? homeBox(ctx, v) : '')
        + (duo ? pairsBox(root, st(root)) : '')
        + '<div>' + albumLink() + '</div></div>';
    } else if (v.phase === 'pick' && ctx.mine && v.drawer === ctx.seat && v.choices) {
      const home = v.choicesHome || [];
      html = '<div class="pcbox"><div class="pctitle">Що малюватимеш?</div><div class="pcchoices">'
        + v.choices.map((w, i) => '<button type="button" class="primary" data-i="' + i + '">' + (home[i] ? '🏠 ' : '') + ctx.esc(w) + '</button>').join('')
        + '</div>'
        + (home.indexOf(true) >= 0 ? '<div class="muted small">🏠 — слово від когось із компанії</div>' : '')
        + (v.reroll ? '<button type="button" class="pcreroll" data-reroll="1">🎲 Інші три слова <span class="muted small">(раз за партію, годинник іде)</span></button>' : '')
        + '</div>';
    } else if (v.phase === 'pick') {
      html = '<div class="pcbox"><div class="pctitle">✏️ ' + ctx.esc(ctx.nickOf(v.drawer) || 'Художник') + ' обирає слово…</div></div>';
    } else if (v.phase === 'reveal') {
      const got = [];
      for (let i = 0; i < seatsOf(ctx); i++) if ((v.gained || [])[i] > 0) got.push({ i, g: v.gained[i] });
      got.sort((a, b) => b.g - a.g);
      const re = v.reacts || [];
      const reLine = re.some((x) => x > 0) ? '<div class="pcre">' + re.map((x, i) => (x ? REACTS[i] + ' ' + x : '')).filter(Boolean).join(' · ') + '</div>' : '';
      html = '<div class="pcbox"><div class="muted small">Слово було</div><div class="pcbig">' + ctx.esc(v.word || '—') + '</div>'
        + (v.homeBy ? '<div class="muted small">🏠 слово від ' + ctx.esc(v.homeBy) + '</div>' : '')
        + (v.duo ? (got.length ? '<div class="pcgot"><span class="chip">✅ +1 · разом ' + v.duo.count + '</span></div>' : '')
          : got.length
            ? '<div class="pcgot">' + got.map((x) => '<span class="chip">' + ctx.esc(ctx.nickOf(x.i) || '') + ' <b>+' + x.g + '</b></span>').join('') + '</div>'
            : '<div class="muted">Отакої — ніхто не вгадав</div>')
        + reLine + '</div>';
    } else if (v.phase === 'vote') {
      html = '<div class="pcbox"><div class="pctitle">❤ Галерея партії</div>'
        + '<div>' + (ctx.mine ? (v.myVote != null ? 'Серце віддано — можна передумати, поки йде час' : 'Віддай ❤ найкращому чужому малюнку — галерея під полотном')
          : 'Гравці вибирають найкращий малюнок') + '</div>'
        + '<div class="muted small">Проголосували: ' + (v.votes || 0) + '</div></div>';
    } else if (v.phase === 'done' && v.duo) {
      const d = v.duo;
      html = '<div class="pcbox"><div class="pctitle">Удвох встигли</div><div class="pcbig">' + d.count + '</div>'
        + '<div>' + (d.record ? '🏆 Рекорд пари!' : d.best ? 'Рекорд пари — ' + d.best : 'Перша спроба цієї пари') + '</div>'
        + '<div>' + albumLink() + '</div></div>';
    } else if (v.phase === 'done') {
      const rows = [];
      for (let i = 0; i < seatsOf(ctx); i++) if (ctx.nickOf(i)) rows.push({ i, s: (v.scores || [])[i] || 0 });
      rows.sort((a, b) => b.s - a.s);
      const medal = ['🥇', '🥈', '🥉'];
      const artists = (v.artists || []).map((i) => ctx.esc(ctx.nickOf(i) || '')).filter(Boolean);
      html = '<div class="pcbox"><div class="pctitle">Партію зіграно</div><div class="pcfinal">'
        + rows.map((r, k) => '<div><span>' + (medal[k] || (k + 1) + '.') + ' ' + ctx.esc(ctx.nickOf(r.i)) + '</span><b>' + r.s + '</b></div>').join('')
        + '</div>'
        + (artists.length ? '<div>🎨 Митець партії — <b>' + artists.join(' і ') + '</b></div>' : '')
        + '<div>' + albumLink() + '</div></div>';
    }
    if (el._html !== html) {
      // поле свого слова переживає перебудову: вид приходить, поки людина друкує
      const old = el.querySelector('.pchomein');
      const val = old ? old.value : '';
      const focused = !!old && document.activeElement === old;
      el._html = html;
      el.innerHTML = html;
      el.hidden = !html;
      el.classList.toggle('pclobby', lobby(ctx));
      el.querySelectorAll('[data-i]').forEach((b) => b.onclick = () => {
        const c = root._ctx;
        if (c) c.act('pick', { i: +b.dataset.i });
      });
      const rr = el.querySelector('[data-reroll]');
      if (rr) rr.onclick = () => { const c = root._ctx; if (c) c.act('reroll'); };
      const input = el.querySelector('.pchomein');
      if (input) {
        input.value = val;
        if (focused) input.focus();
        el.querySelector('.pchomef').onsubmit = (e) => {
          e.preventDefault();
          const c = root._ctx;
          const text = input.value.trim();
          if (!c || !text) return;
          c.act('home', { text }).then((r) => { if (r && r.ok) input.value = ''; });
        };
      }
      el.querySelectorAll('[data-unhome]').forEach((b) => b.onclick = () => {
        const c = root._ctx;
        if (c) c.act('unhome', { text: b.dataset.unhome });
      });
    }
  }

  // =========================================================================================
  // Реакції (прохід №3, п. 27): хто вже вгадав і глядачі тиснуть 😂🔥🤯 — емодзі пливуть над полотном
  // =========================================================================================

  function canReact(ctx, v) {
    if (!ctx.playing) return false;
    if (v.phase === 'reveal') return true;
    if (v.phase !== 'draw') return false;
    if (!ctx.mine) return true;
    const f = fresh(ctx, v);
    const guessed = (f && f.guessed) || v.guessed || [];
    return v.drawer !== ctx.seat && guessed.indexOf(ctx.seat) >= 0;
  }

  function reactBar(root, ctx, v) {
    const bar = root.querySelector('.pcreact');
    const on = canReact(ctx, v);
    if (bar.hidden === on) bar.hidden = !on;
  }

  function sendReact(root, e) {
    const c = root._ctx;
    if (!c || !c.room) return;
    if (c.mine) { c.input('react', { e }); return; }
    fetch('/api/games/pictionary/react', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent((c.me && c.me.nick) || '') },
      body: JSON.stringify({ room: c.room.id, e }),
    }).then((r) => r.json()).then((d) => { if (d && !d.ok && d.message && c.toast) c.toast(d.message); }).catch(() => {});
  }

  function floatReacts(root, list) {
    const layer = root.querySelector('.pcfloat');
    if (!layer || document.hidden) return;
    for (const e of list.slice(-8)) {
      if (layer.childElementCount > 24) break;
      const i = document.createElement('i');
      i.textContent = REACTS[e] || '';
      i.style.left = (6 + Math.random() * 84) + '%';
      i.style.animationDelay = Math.round(Math.random() * 160) + 'ms';
      i.addEventListener('animationend', () => i.remove());
      layer.appendChild(i);
    }
  }

  // =========================================================================================
  // Альбом партії: кожен малюнок на розкритті знімаємо мініатюрою, наприкінці показуємо всі разом
  // =========================================================================================

  const THUMB_W = 240, THUMB_H = 180;

  function snap(ctx, s, v) {
    const round = (ctx.room && ctx.room.round) || 0;
    if (s.albumRound !== round) { s.album.clear(); s.fetching.clear(); s.albumRound = round; }
    if (!v.word || !v.turnNo || !s.ops.length) return;
    commitAnim(s);
    const c = document.createElement('canvas');
    c.width = THUMB_W; c.height = THUMB_H;
    c.getContext('2d').drawImage(s.buf, 0, 0, THUMB_W, THUMB_H);
    let url = '';
    try { url = c.toDataURL('image/jpeg', 0.82); } catch { return; }
    s.album.set(v.turnNo, { word: v.word, drawer: ctx.nickOf(v.drawer) || '', url });
  }

  /// Мініатюра з малюнка штрихами (галерея тим, хто хід не застав): буфер 1000 × 750 → JPEG 240 × 180.
  function thumbOf(ops) {
    const big = document.createElement('canvas');
    big.width = W; big.height = H;
    const g = big.getContext('2d', { willReadFrequently: true });
    g.fillStyle = '#ffffff';
    g.fillRect(0, 0, W, H);
    for (const op of ops) drawOp(g, op);
    const c = document.createElement('canvas');
    c.width = THUMB_W; c.height = THUMB_H;
    c.getContext('2d').drawImage(big, 0, 0, THUMB_W, THUMB_H);
    try { return c.toDataURL('image/jpeg', 0.82); } catch { return ''; }
  }

  function fetchArt(root, ctx, it) {
    const s = st(root);
    if (s.fetching.has(it.t) || !ctx.room) return;
    s.fetching.add(it.t);
    fetch('/api/games/pictionary/art?room=' + encodeURIComponent(ctx.room.id) + '&t=' + it.t)
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (!d || !d.z) return;
        s.album.set(it.t, { word: it.word, drawer: it.nick, url: thumbOf(unpack(d.z)) });
        const c = root._ctx;
        if (c) album(root, c, c.view || {});
      }).catch(() => {});
  }

  /// Альбом партії → галерея (п. 26): на «vote» ❤ найкращому чужому малюнку, після партії — ❤, 🎨 і «📌 в альбом».
  function album(root, ctx, v) {
    const s = st(root);
    const el = root.querySelector('.pcalbum');
    const g = (v.phase === 'vote' || v.phase === 'done') && v.gallery && v.gallery.length ? v.gallery : null;
    const items = g || (v.phase === 'done'
      ? [...s.album.entries()].sort((a, b) => a[0] - b[0]).map(([t, a]) => ({ t, word: a.word, nick: a.drawer, seat: -1 }))
      : []);
    el.hidden = !items.length;
    if (!items.length) { if (el._sig) { el._sig = ''; el.innerHTML = ''; } return; }
    if (g) for (const it of items) if (!s.album.has(it.t)) fetchArt(root, ctx, it);
    const voting = v.phase === 'vote';
    const pinned = v.pinned || [];
    const sig = [v.phase, v.myVote, v.votes, pinned.join('.'), ctx.mine ? 1 : 0,
      items.map((it) => it.t + ':' + (it.hearts == null ? '' : it.hearts) + (s.album.has(it.t) ? 'i' : '')).join(',')].join('|');
    if (el._sig === sig) return;
    el._sig = sig;
    const max = Math.max(0, ...items.map((it) => it.hearts || 0));
    const artists = (v.artists || []).map((i) => ctx.esc(ctx.nickOf(i) || '')).filter(Boolean);
    const head = voting
      ? '<div class="pctitle">❤ Віддай серце найкращому чужому малюнку <span class="muted small">· проголосували ' + (v.votes || 0) + '</span></div>'
      : '<div class="pctitle">🖼 Альбом партії' + (artists.length ? ' · 🎨 Митець партії — ' + artists.join(' і ') : '') + '</div>'
        + (g && ctx.mine ? '<div class="muted small">📌 — закинути малюнок у публічний ' + albumLink() + ' (до трьох на людину)</div>' : '<div>' + albumLink() + '</div>');
    el.innerHTML = head + '<div class="pcthumbs">' + items.map((it) => {
      const a = s.album.get(it.t);
      const own = ctx.mine && it.seat === ctx.seat;
      const best = !voting && max > 0 && it.hearts === max;
      let bar = '';
      if (voting && ctx.mine) {
        bar = own ? '<span class="muted small">твій</span>'
          : '<button type="button" class="pcheart' + (v.myVote === it.t ? ' on' : '') + '" data-vote="' + it.t + '">❤</button>';
      } else if (!voting && g) {
        bar = '<span class="pchearts">' + (it.hearts ? '❤ ' + it.hearts : '') + (best ? ' 🎨' : '') + '</span>'
          + (pinned.indexOf(it.t) >= 0 ? '<span class="muted small">📌 в альбомі</span>'
            : ctx.mine ? '<button type="button" class="pcpin" data-pin="' + it.t + '" title="У публічний Альбом Піктіонарі">📌</button>' : '');
      }
      return '<figure class="' + (best ? 'best' : '') + (v.myVote === it.t ? ' voted' : '') + '">'
        + (a && a.url ? '<img alt="" src="' + a.url + '">' : '<div class="pcph"></div>')
        + '<figcaption><b>' + (it.home ? '🏠 ' : '') + ctx.esc(it.word) + '</b>'
        + '<span class="muted small">' + ctx.esc(it.nick || (a && a.drawer) || '') + '</span></figcaption>'
        + (bar ? '<div class="pcfigbar">' + bar + '</div>' : '') + '</figure>';
    }).join('') + '</div>';
  }

  function albumClick(root, e) {
    const b = e.target.closest('button');
    const c = root._ctx;
    if (!b || !c) return;
    if (b.dataset.vote) { c.act('vote', { t: +b.dataset.vote }); return; }
    if (b.dataset.pin) {
      b.disabled = true;
      fetch('/api/games/pictionary/pin', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent((c.me && c.me.nick) || '') },
        body: JSON.stringify({ room: c.room.id, t: +b.dataset.pin }),
      }).then((r) => r.json()).then((d) => {
        if (d && d.message && c.toast) c.toast(d.message);
        if (!d || !d.ok) b.disabled = false;
        else b.outerHTML = '<span class="muted small">📌 в альбомі</span>';
      }).catch(() => { b.disabled = false; });
    }
  }

  /// Де на сторінці починається полотно — з цього CSS рахує, якої ширини йому бути, щоб інструменти під ним
  /// влізли в екран (pictionary.css, .pcmain). Міряємо на кожен вид: чіпи місць над карткою то в рядок, то в два.
  function fitStage(root) {
    const wrap = root.querySelector('.pcwrap');
    const main = root.querySelector('.pcmain');
    if (!wrap || !main || !main.isConnected) return;
    const top = Math.round(main.getBoundingClientRect().top + window.scrollY) + 'px';
    if (wrap.style.getPropertyValue('--pctop') !== top) wrap.style.setProperty('--pctop', top);
  }

  function render(root, ctx) {
    root._ctx = ctx;
    ctx.pcRoot = root;
    const v = ctx.view || {};
    const s = st(root);
    if (v.phase) {
      if (v.phase !== 'draw' && s.mark) s.mark.n = 0;   // новий хід — знімок старого малюнка ні до чого
      fromView(root, ctx, v);
      mergeFeed(s, v.feed, true);
      if (v.phase === 'reveal') snap(ctx, s, v);
      // галерея під полотном: коли почалось голосування, підкрутити до неї (на телефоні й ноуті вона нижче краю)
      if (v.phase === 'vote' && s.phase !== 'vote' && ctx.mine) {
        setTimeout(() => { const a = root.querySelector('.pcalbum'); if (a && !a.hidden) a.scrollIntoView({ behavior: 'smooth', block: 'nearest' }); }, 120);
      }
      s.phase = v.phase;
    }
    album(root, ctx, v);
    head(root, ctx, v);
    wordLine(root, ctx, v);
    timer(root, ctx);
    scores(root, ctx, v);
    feed(root, ctx);
    guessBox(root, ctx, v);
    overlay(root, ctx, v);
    toolbar(root, ctx);
    reactBar(root, ctx, v);
    const el = root.querySelector('.pccanvas');
    el.classList.toggle('can', canDraw(ctx));
    const wrap = root.querySelector('.pcwrap');
    wrap.classList.toggle('drw', canDraw(ctx));   // на телефоні художнику поле здогадки ні до чого
    wrap.classList.toggle('live', !!ctx.playing);  // на телефоні в партії чіпи місць ховаються (pictionary.css)
    wrap.classList.toggle('lobby', lobby(ctx));    // у лобі полотно ні до чого — правила й слова компанії стоять на його місці
    if (!canDraw(ctx)) { s.cur = null; s.local = []; s.shape = null; }
    fitStage(root);
    paintSoon(root);
  }

  HGames.register({
    id: 'pictionary',
    added: '2026-09-17',          // нова гра: «🆕» у лобі два тижні тим, хто ще не грав (core.js, isNewGame)
    icon: ICON,
    news: {
      v: '2026-09-29',
      title: 'Піктіонарі: ❤ шедеврам, свої слова й удвох',
      items: [
        '❤ Після партії — галерея: серце найкращому чужому малюнку, 🎨 Митець партії бере черепки, а найкраще — 📌 у публічний Альбом Піктіонарі',
        '🏠 У лобі докидай свої слова («кумів трактор») — їх малюватимуть інші; 🎲 непосильні три слова раз за партію міняються',
        '👫 Режим «Удвох: скільки встигнемо» — три хвилини, малюєте по черзі, рекорд пари в таблиці',
        '╱ ◯ Пряма й овал, 😂🔥🤯 реакції над полотном, а чужий штрих тепер тече плавно, а не стрибками',
      ],
    },
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o', 'c', 'd', 'x', 'o'],

    mount(root, ctx) {
      root.innerHTML = '<div class="pcwrap">'
        + '<div class="pctop"><div class="pchead muted small"></div><div class="pcword"></div>'
        + '<div class="pctime"><i></i><span class="pcsec"></span></div></div>'
        + '<div class="pcmain">'
        + '<div class="pcstage"><canvas class="pccanvas"></canvas><div class="pcfloat" aria-hidden="true"></div><div class="pcover" hidden></div>'
        + '<div class="pcreact" hidden>' + REACTS.map((r, i) => '<button type="button" data-e="' + i + '" title="Реакція">' + r + '</button>').join('') + '</div></div>'
        + '<div class="pcside"><div class="pcscores"></div><div class="pcfeed"></div>'
        + '<form class="pcguess"><input type="text" maxlength="40" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" enterkeyhint="send">'
        + '<button class="primary" type="submit">➤</button></form></div>'
        + '<div class="pctools" hidden></div>'
        + '</div><div class="pcalbum" hidden></div></div>';
      bindCanvas(root, ctx);
      root.querySelector('.pcreact').addEventListener('click', (e) => {
        const b = e.target.closest('button[data-e]');
        if (b) sendReact(root, +b.dataset.e);
      });
      root.querySelector('.pcalbum').addEventListener('click', (e) => albumClick(root, e));
      const s = st(root);
      s.timer = setInterval(() => { if (root._ctx) timer(root, root._ctx); }, 200);
      render(root, ctx);
    },

    update(root, ctx) { render(root, ctx); },

    frame(root, ctx, f) {
      if (!f) return;
      root._ctx = ctx;
      const s = st(root);
      fromFrame(root, ctx, f);
      mergeFeed(s, f.feed, false);
      if (f.re) floatReacts(root, f.re);
      const v = ctx.view || {};
      reactBar(root, ctx, v);
      if (v.duo) head(root, ctx, v);
      wordLine(root, ctx, v);
      scores(root, ctx, v);
      feed(root, ctx);
      guessBox(root, ctx, v);
    },

    unmount(root) {
      const s = root._pc;
      if (!s) return;
      clearInterval(s.timer);
      clearTimeout(s.flushTimer);
      if (s.raf) cancelAnimationFrame(s.raf);
    },

    onKey(e, ctx) {
      if (!canDraw(ctx)) return false;
      if ((e.ctrlKey || e.metaKey) && e.code === 'KeyZ') {
        if (ctx.pcRoot) { undo(ctx.pcRoot, ctx); return true; }
      }
      return false;
    },

    status(ctx) {
      const v = ctx.view || {};
      if (!ctx.playing) return v.phase === 'done' ? 'Партію зіграно' : '';
      // Секунд тут нема: статус перемальовується лише з кадрами, а відлік живе на смужці таймера.
      if (v.phase === 'pick') return v.drawer === ctx.seat && ctx.mine ? 'Обери слово' : 'Художник обирає слово…';
      if (v.phase === 'vote') return !ctx.mine ? 'Гравці вибирають найкращий малюнок' : v.myVote != null ? 'Серце віддано — чекаємо решту' : 'Віддай ❤ найкращому чужому малюнку';
      if (v.phase === 'reveal') return v.duo ? 'Далі — наступне слово!' : v.turnNo >= v.turns ? 'Глек рахує очки…' : 'Зараз малюватиме наступний…';
      if (v.phase !== 'draw') return '';
      if (!ctx.mine) return 'Дивишся збоку — тисни 😂🔥🤯';
      if (v.drawer === ctx.seat) return v.duo ? 'Малюй! Не йде — ⏭ пропусти' : 'Малюй! Вгадують інші';
      const f = fresh(ctx, v);
      const guessed = (f && f.guessed) || v.guessed || [];
      return guessed.indexOf(ctx.seat) >= 0 ? 'Є! Вгадано — тисни 😂🔥🤯, поки решта думає' : 'Вгадуй, що малюють';
    },

    /// Малювання для публічного альбому (web/pictionary-album/): та сама палітра, заливка й розбір дроту.
    art: { unpack, drawOp, W, H },
  });
})();
