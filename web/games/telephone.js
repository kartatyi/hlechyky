/*
  Зіпсований телефон. Кроки, таймери, ланцюжки й ❤ живуть на сервері (Impl/Telephone.cs); модуль показує
  кожному його завдання, малює полотно й гортає показ.

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { phase: 'step'|'reveal'|'done', step, steps, until, totalMs,
      task: null | { kind: 'phrase'|'draw'|'describe', chain, prompt: null | { kind: 'text'|'drawing', text, ops },
                     ready, text, n, ops, ideas: string[] },
      ready: seat[], waiting: seat[],
      reveal: null | { chain, owner, no, chains, shown, total,
                       entries: [{ index, seat, kind: 'text'|'drawing', text, ops, likes, liked }] },
      likes: number[], left: seat[], result }
  Кадр (подія 'frame') — лише на показі, коли хтось ставить чи знімає ❤: { chain, likes: [скільки ❤ у записів 0..shown-1] };
  повний вид заради лічильника ❤ більше не летить (прохід 28.09). Своє «❤ стоїть» модуль пам'ятає сам (p.liked).
  Ходи: Input('text', { text }) — чернетка; Act('done', { text } | { n }), Act('edit');
  Input('draw', { s, c, w, p }), Input('fill', { s, c, x, y }), Input('undo'), Input('clear');
  Act('next'), Act('like', { chain, index }).

  Малюнок — як у Піктіонарі: операції [вид, штрих, колір, товщина, x0, y0, …] на полотні 1000 × 750.
  Тут художник один на своєму полотні, тож правда про малюнок — у браузері, а на сервер летить копія.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M3 2.5h3l1 3-2 1.2a8 8 0 0 0 4.3 4.3l1.2-2 3 1v3a1.5 1.5 0 0 1-1.6 1.5A11.5 11.5 0 0 1 1.5 4.1 1.5 1.5 0 0 1 3 2.5z" fill="none" stroke="var(--accent)" stroke-width="1.4" stroke-linejoin="round"/>'
    + '<path d="M10 2.5c1.8.4 3.1 1.7 3.5 3.5" stroke="var(--clay)" stroke-width="1.4" fill="none" stroke-linecap="round"/></svg>';

  const W = 1000, H = 750;
  /// Та сама палітра, що в pictionary.js (Sketch.Colors на сервері).
  const PALETTE = [
    '#ffffff', '#000000', '#7f7f7f', '#c3c3c3', '#5d4037', '#8d5a2b', '#e53935', '#fb8c00', '#fdd835', '#fff59d',
    '#43a047', '#a5d6a7', '#00897b', '#1e88e5', '#90caf9', '#3949ab', '#8e24aa', '#f48fb1', '#e8b48a', '#ff7043',
  ];
  const SIZES = [3, 8, 16, 32];
  const FLUSH_MS = 80;
  const CHUNK_MAX = 200;
  const DRAFT_MS = 600;
  /// Спрощення штриха перед відправкою — як у pictionary.js (миша на 1000 Гц слала ~500 точок на секунду,
  /// а малюнок на сервері має межу в 30 000 точок: активні півтори хвилини — і «Полотно переповнене»).
  const SIMPLIFY = 0.9;
  /// Скільки готових картинок показу тримати (кожна — полотно 1000 × 750, ~3 МБ).
  const RENDER_KEEP = 8;

  // =========================================================================================
  // Малювання операцій
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

  function flood(c, x0, y0, color) {
    x0 = Math.max(0, Math.min(W - 1, x0 | 0));
    y0 = Math.max(0, Math.min(H - 1, y0 | 0));
    const img = c.getImageData(0, 0, W, H);
    const d = img.data;
    const at = (y0 * W + x0) * 4;
    const tr = d[at], tg = d[at + 1], tb = d[at + 2];
    const n = parseInt(color.slice(1), 16);
    const fr = (n >> 16) & 255, fg = (n >> 8) & 255, fb = n & 255;
    if (Math.abs(tr - fr) + Math.abs(tg - fg) + Math.abs(tb - fb) < 12) return;
    const same = (i) => Math.abs(d[i] - tr) + Math.abs(d[i + 1] - tg) + Math.abs(d[i + 2] - tb) <= 90;
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

  /// Спростити шматок штриха (Рамер — Дуглас — Пекер, відстань до відрізка) — див. pictionary.js.
  function simplify(q, eps) {
    const n = q.length / 2;
    if (n <= 2) return q;
    const keep = new Uint8Array(n);
    keep[0] = keep[n - 1] = 1;
    const e2 = eps * eps;
    const stack = [0, n - 1];
    while (stack.length) {
      const b = stack.pop(), a = stack.pop();
      const ax = q[a * 2], ay = q[a * 2 + 1], dx = q[b * 2] - ax, dy = q[b * 2 + 1] - ay, len2 = dx * dx + dy * dy;
      let best = -1, far = e2;
      for (let i = a + 1; i < b; i++) {
        const px = q[i * 2] - ax, py = q[i * 2 + 1] - ay;
        const t = len2 ? Math.max(0, Math.min(1, (px * dx + py * dy) / len2)) : 0;
        const ex = px - t * dx, ey = py - t * dy, d2 = ex * ex + ey * ey;
        if (d2 > far) { far = d2; best = i; }
      }
      if (best >= 0) { keep[best] = 1; stack.push(a, best, best, b); }
    }
    const out = [];
    for (let i = 0; i < n; i++) if (keep[i]) out.push(q[i * 2], q[i * 2 + 1]);
    return out;
  }

  /// Підпис малюнка за вмістом: кожен вид приносить нові масиви, тож кеш за самим масивом (як було) не влучав
  /// ніколи — і кожне ❤ чи «Далі» перемальовувало всі малюнки ланцюжка з заливками наново (прохід 28.09).
  const sigs = new WeakMap();
  function sigOf(ops) {
    if (!ops) return '0';
    let sig = sigs.get(ops);
    if (sig) return sig;
    let h = 2166136261, n = 0;
    for (const op of ops) { for (let i = 0; i < op.length; i++) h = Math.imul(h ^ op[i], 16777619); n += op.length; }
    sig = n + ':' + (h >>> 0);
    sigs.set(ops, sig);
    return sig;
  }

  /// Готова картинка з операцій: canvas 1000 × 750. Кешуємо за підписом і тримаємо лише кілька останніх:
  /// раніше альбом тримав полотно на кожен малюнок партії (на десятьох — під півсотні по 3 МБ).
  const rendered = new Map();
  function picture(ops) {
    const key = sigOf(ops);
    let cv = rendered.get(key);
    if (cv) { rendered.delete(key); rendered.set(key, cv); return cv; }
    cv = document.createElement('canvas');
    cv.width = W; cv.height = H;
    const c = cv.getContext('2d', { willReadFrequently: true });
    c.fillStyle = '#fff';
    c.fillRect(0, 0, W, H);
    for (const op of ops || []) drawOp(c, op);
    rendered.set(key, cv);
    while (rendered.size > RENDER_KEEP) rendered.delete(rendered.keys().next().value);
    return cv;
  }

  /// Показати малюнок у <canvas> будь-якого розміру.
  function show(el, ops) {
    const rect = el.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const pw = Math.max(1, Math.round(rect.width * dpr)), ph = Math.max(1, Math.round(rect.height * dpr));
    if (el.width !== pw || el.height !== ph) { el.width = pw; el.height = ph; }
    el.getContext('2d').drawImage(picture(ops), 0, 0, pw, ph);
  }

  // =========================================================================================
  // Своє полотно
  // =========================================================================================

  function pad(root) {
    if (!root._tp) {
      const buf = document.createElement('canvas');
      buf.width = W; buf.height = H;
      root._tp = {
        buf, bctx: buf.getContext('2d', { willReadFrequently: true }),
        ops: [], key: '', tool: 'pen', color: 1, size: 1, stroke: 1,
        cur: null, flushTimer: 0, draftTimer: 0, raf: 0, timer: 0, idea: 0,
        seen: new Map(),        // chain → entries, які вже показали: з них гортаємо альбом після партії
        liked: new Set(),       // 'chain:index' записів, яким я поставив ❤ (вид каже правду, між видами — ми самі)
        mark: null,             // знімок полотна після останньої заливки: «↶» не перезаливає все наново
      };
      wipe(root._tp);
    }
    return root._tp;
  }

  function wipe(p) {
    p.bctx.fillStyle = '#fff';
    p.bctx.fillRect(0, 0, W, H);
  }

  /// Знімок свого полотна одразу після заливки — див. pictionary.js (keepMark).
  function keepMark(p, i = p.ops.length - 1) {
    if (!p.mark) {
      const c = document.createElement('canvas');
      c.width = W; c.height = H;
      p.mark = { c, g: c.getContext('2d'), n: 0, op: null };
    }
    p.mark.g.drawImage(p.buf, 0, 0);
    p.mark.n = i + 1;
    p.mark.op = p.ops[i];
  }

  function rebuild(p) {
    const m = p.mark;
    let from = 0;
    if (m && m.n > 0 && m.n <= p.ops.length && p.ops[m.n - 1] === m.op) {
      p.bctx.drawImage(m.c, 0, 0);
      from = m.n;
    } else {
      if (m) m.n = 0;
      wipe(p);
    }
    for (let i = from; i < p.ops.length; i++) {
      drawOp(p.bctx, p.ops[i]);
      if (p.ops[i][0] === 1) keepMark(p, i);
    }
  }

  function paintSoon(root) {
    const p = pad(root);
    if (p.raf) return;
    p.raf = requestAnimationFrame(() => { p.raf = 0; paintPad(root); });
  }

  function paintPad(root) {
    const p = pad(root);
    const el = root.querySelector('.tpcanvas');
    if (!el || el.offsetParent === null) return;
    const rect = el.getBoundingClientRect();
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const pw = Math.max(1, Math.round(rect.width * dpr)), ph = Math.max(1, Math.round(rect.height * dpr));
    if (el.width !== pw || el.height !== ph) { el.width = pw; el.height = ph; }
    const c = el.getContext('2d');
    c.setTransform(1, 0, 0, 1, 0, 0);
    c.drawImage(p.buf, 0, 0, pw, ph);
    if (p.cur && p.cur.p.length >= 2) {
      c.setTransform(pw / W, 0, 0, ph / H, 0, 0);
      drawOp(c, [0, p.cur.s, p.cur.c, p.cur.w, ...p.cur.p]);
    }
  }

  function canDraw(ctx) {
    const t = (ctx.view || {}).task;
    return !!ctx.playing && !!t && t.kind === 'draw' && !t.ready;
  }

  function point(el, e) {
    const r = el.getBoundingClientRect();
    return [
      Math.round(Math.max(0, Math.min(W, (e.clientX - r.left) / r.width * W))),
      Math.round(Math.max(0, Math.min(H, (e.clientY - r.top) / r.height * H))),
    ];
  }

  /// Відправити накопичений шматок штриха. Локально він одразу стає операцією — правда тут, у браузері.
  function flush(root, final) {
    const p = pad(root);
    const ctx = root._ctx;
    clearTimeout(p.flushTimer);
    p.flushTimer = 0;
    const cur = p.cur;
    if (!cur || !ctx) return;
    // один-єдиний «хвіст» із попереднього шматка слати нема чого; крапку (клік без руху) — так
    if (cur.p.length > 2 || (cur.fresh && final)) {
      const q = simplify(cur.p, SIMPLIFY);
      const op = [0, cur.s, cur.c, cur.w, ...q];
      p.ops.push(op);
      drawOp(p.bctx, op);
      ctx.input('draw', { s: cur.s, c: cur.c, w: cur.w, p: q });
      const lx = cur.p[cur.p.length - 2], ly = cur.p[cur.p.length - 1];
      cur.p = [lx, ly];
      cur.fresh = false;
    }
    if (final) p.cur = null;
    paintSoon(root);
  }

  function bindCanvas(root) {
    const el = root.querySelector('.tpcanvas');
    if (!el || el._bound) return;
    el._bound = true;
    const p = pad(root);

    el.addEventListener('pointerdown', (e) => {
      const ctx = root._ctx;
      if (!ctx || !canDraw(ctx)) return;
      // Другий палець (долоня на Steam Deck, щипок) і права кнопка миші штриха не починають.
      if (!e.isPrimary || (e.pointerType === 'mouse' && e.button !== 0)) return;
      e.preventDefault();
      if (p.cur) flush(root, true);
      const [x, y] = point(el, e);
      if (p.tool === 'fill') {
        const op = [1, p.stroke++, p.color, 0, x, y];
        p.ops.push(op);
        drawOp(p.bctx, op);
        keepMark(p);
        ctx.input('fill', { s: op[1], c: op[2], x, y });
        paintSoon(root);
        return;
      }
      try { el.setPointerCapture(e.pointerId); } catch { /* старі браузери */ }
      const eraser = p.tool === 'eraser';
      p.cur = { s: p.stroke++, c: eraser ? 0 : p.color, w: SIZES[p.size] * (eraser ? 2 : 1), p: [x, y], fresh: true, id: e.pointerId };
      paintSoon(root);
    });

    el.addEventListener('pointermove', (e) => {
      if (!p.cur || e.pointerId !== p.cur.id) return;
      const events = e.getCoalescedEvents ? e.getCoalescedEvents() : [];
      for (const ev of events.length ? events : [e]) {
        const [x, y] = point(el, ev);
        const q = p.cur.p;
        const dx = x - q[q.length - 2], dy = y - q[q.length - 1];
        if (dx * dx + dy * dy < 4) continue;
        q.push(x, y);
      }
      if (p.cur.p.length / 2 >= CHUNK_MAX) flush(root, false);
      else if (!p.flushTimer) p.flushTimer = setTimeout(() => flush(root, false), FLUSH_MS);
      paintSoon(root);
    });

    const end = (e) => { if (p.cur && (!e || e.pointerId === p.cur.id)) flush(root, true); };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
    el.addEventListener('lostpointercapture', end);
    new ResizeObserver(() => paintSoon(root)).observe(el);
  }

  function undo(root) {
    const p = pad(root);
    const ctx = root._ctx;
    if (!ctx || !canDraw(ctx)) return;
    if (p.cur) flush(root, true);
    if (!p.ops.length) return;
    const s = p.ops[p.ops.length - 1][1];
    while (p.ops.length && p.ops[p.ops.length - 1][1] === s) p.ops.pop();
    rebuild(p);
    ctx.input('undo');
    paintSoon(root);
  }

  function tools(root) {
    const p = pad(root);
    const bar = root.querySelector('.tptools');
    if (!bar.firstChild) {
      bar.innerHTML = '<div class="pcpal">'
        + PALETTE.map((c, i) => '<button type="button" class="pcsw" data-c="' + i + '" style="background:' + c + '" title="Колір"></button>').join('')
        + '</div><div class="pcrow">'
        + SIZES.map((z, i) => '<button type="button" class="pcsz" data-z="' + i + '" title="Товщина ' + z + '"><i style="width:' + Math.max(4, z * .7) + 'px;height:' + Math.max(4, z * .7) + 'px"></i></button>').join('')
        + '<span class="pcsep"></span>'
        + '<button type="button" class="pct" data-t="pen" title="Пензель">✏️</button>'
        + '<button type="button" class="pct" data-t="eraser" title="Гумка">🧽</button>'
        + '<button type="button" class="pct" data-t="fill" title="Заливка">🪣</button>'
        + '<span class="pcsep"></span>'
        + '<button type="button" class="pca" data-a="undo" title="Скасувати (Ctrl+Z)">↶</button>'
        + '<button type="button" class="pca" data-a="clear" title="Очистити все">🗑</button>'
        + '</div>';
      bar.addEventListener('click', (e) => {
        const b = e.target.closest('button');
        const ctx = root._ctx;
        if (!b || !ctx) return;
        if (b.dataset.c != null) { p.color = +b.dataset.c; if (p.tool === 'eraser') p.tool = 'pen'; }
        else if (b.dataset.z != null) p.size = +b.dataset.z;
        else if (b.dataset.t) p.tool = b.dataset.t;
        else if (b.dataset.a === 'undo') undo(root);
        else if (b.dataset.a === 'clear' && canDraw(ctx) && confirm('Стерти весь малюнок?')) {
          p.ops = [];
          p.cur = null;
          rebuild(p);
          ctx.input('clear');
          paintSoon(root);
        }
        marks(root);
      });
    }
    marks(root);
  }

  function marks(root) {
    const p = pad(root);
    root.querySelectorAll('.tptools .pcsw').forEach((b) => b.classList.toggle('on', +b.dataset.c === p.color && p.tool !== 'eraser'));
    root.querySelectorAll('.tptools .pcsz').forEach((b) => b.classList.toggle('on', +b.dataset.z === p.size));
    root.querySelectorAll('.tptools .pct').forEach((b) => b.classList.toggle('on', b.dataset.t === p.tool));
    const el = root.querySelector('.tpcanvas');
    if (el) el.dataset.tool = p.tool;
  }

  // =========================================================================================
  // Розмітка фаз
  // =========================================================================================

  const nick = (ctx, i) => ctx.esc(ctx.nickOf(i) || 'хтось');
  const blank = (ops) => !ops || !ops.length;

  function timer(root, ctx) {
    const v = ctx.view || {};
    const box = root.querySelector('.pctime');
    if (!box) return;
    const live = ctx.playing && v.phase === 'step';
    box.style.visibility = live ? 'visible' : 'hidden';
    if (!live) return;
    const left = Math.max(0, new Date(v.until).getTime() - Date.now());
    const bar = box.querySelector('i');
    bar.style.width = Math.max(0, Math.min(100, left / (v.totalMs || 1) * 100)) + '%';
    bar.classList.toggle('hot', left < 10000);
    const t = String(Math.ceil(left / 1000));
    const num = box.querySelector('.pcsec');
    if (num.textContent !== t) num.textContent = t;
  }

  function stepScreen(root, ctx, v) {
    const t = v.task;
    const lobby = !!ctx.room && ctx.room.status === 'lobby';
    const title = !t ? (lobby ? 'Збираємо стіл — грати можна вже вдвох' : ctx.mine ? 'Чекаємо, поки всі здадуть…' : 'Гравці працюють…')
      : t.kind === 'phrase' ? 'Тяпни фразу, яку намалює сусід'
        : t.kind === 'draw' ? (v.duo && v.step === 1 ? 'Глек загадав — намалюй, а сусід угадає' : 'Намалюй це')
          : 'Опиши, що бачиш на малюнку';
    const key = [v.step, t ? t.kind + ':' + t.chain : 'none'].join('|');
    const body = root.querySelector('.tpbody');

    if (body.dataset.key !== key) {
      body.dataset.key = key;
      const p = pad(root);
      let html = '<div class="tptitle">' + ctx.esc(title) + '</div>';
      if (t && t.prompt && t.prompt.kind === 'text') html += '<div class="tpprompt">«' + ctx.esc(t.prompt.text) + '»</div>';
      // Порожнє полотно (сусід не встиг) — краще сказати словами, ніж показувати білий аркуш і гадати, чи він довантажиться.
      if (t && t.prompt && t.prompt.kind === 'drawing') {
        html += blank(t.prompt.ops) ? '<div class="tpprompt tpempty">🤷 Сусідові забракло часу — полотно порожнє. Вигадай, що там мало бути!</div>'
          : '<canvas class="tpshow big"></canvas>';
      }
      if (t && t.kind === 'draw') html += '<canvas class="tpcanvas"></canvas><div class="tptools"></div>';
      if (t && t.kind !== 'draw') {
        html += '<form class="tpform"><input type="text" maxlength="80" autocomplete="off" spellcheck="false" enterkeyhint="done" placeholder="'
          + (t.kind === 'phrase' ? 'кіт їде на велосипеді…' : 'що це таке?') + '">'
          + (t.kind === 'phrase' ? '<button type="button" class="ghost tproll" title="Підкинути ідею">🎲</button>' : '')
          + '</form>';
      }
      if (t) html += '<div class="tpact"></div>';
      html += '<div class="tpwho"></div>';
      body.innerHTML = html;

      if (t && t.kind === 'draw') {
        // Нове завдання — чисте полотно. Після F5 посеред кроку беремо копію з сервера.
        p.ops = (t.ops || []).map((o) => o.slice());
        p.stroke = p.ops.reduce((m, o) => Math.max(m, o[1]), 0) + 1;
        p.cur = null;
        if (p.mark) p.mark.n = 0;          // знімок чужого (попереднього) малюнка сюди не годиться
        rebuild(p);
        bindCanvas(root);
        tools(root);
        paintSoon(root);
      }
      if (t && t.kind !== 'draw') {
        const input = body.querySelector('input');
        input.value = t.text || '';
        input.addEventListener('input', () => {
          clearTimeout(p.draftTimer);
          p.draftTimer = setTimeout(() => { const c = root._ctx; if (c) c.input('text', { text: input.value }); }, DRAFT_MS);
        });
        body.querySelector('form').onsubmit = (e) => { e.preventDefault(); submit(root); };
        const roll = body.querySelector('.tproll');
        if (roll) roll.onclick = () => {
          const c = root._ctx;
          const ideas = (c && c.view && c.view.task && c.view.task.ideas) || [];
          if (!ideas.length) return;
          input.value = ideas[p.idea++ % ideas.length];
          input.dispatchEvent(new Event('input'));
        };
      }
      if (t && t.prompt && t.prompt.kind === 'drawing' && !blank(t.prompt.ops)) {
        const el = body.querySelector('.tpshow');
        requestAnimationFrame(() => show(el, t.prompt.ops));
        new ResizeObserver(() => show(el, t.prompt.ops)).observe(el);
      }
    }

    // Те, що міняється без нового завдання: здано / змінити, хто ще працює.
    if (t) {
      const act = body.querySelector('.tpact');
      const html = t.ready
        ? '<span class="tpok">✅ Здано</span><button type="button" class="ghost" data-do="edit">Змінити</button>'
        : '<button type="button" class="primary" data-do="done">Готово</button>';
      if (act._html !== html) {
        act._html = html;
        act.innerHTML = html;
        act.querySelectorAll('[data-do]').forEach((b) => b.onclick = () => {
          if (b.dataset.do === 'done') submit(root);
          else { const c = root._ctx; if (c) c.act('edit'); }
        });
      }
      const input = body.querySelector('.tpform input');
      if (input) input.disabled = !!t.ready;
      const tb = body.querySelector('.tptools');
      if (tb) tb.hidden = !!t.ready;
      const cv = body.querySelector('.tpcanvas');
      if (cv) cv.classList.toggle('can', !t.ready);
    }
    const waiting = (v.waiting || []).map((i) => nick(ctx, i));
    const who = waiting.length ? 'Ще працюють: ' + waiting.join(', ')
      : lobby ? 'Гайда за стіл — господар тисне «Почати»' : 'Усі здали — гайда далі';
    const whoEl = body.querySelector('.tpwho');
    if (whoEl._html !== who) { whoEl._html = who; whoEl.innerHTML = who; }
  }

  function submit(root) {
    const ctx = root._ctx;
    const t = ctx && ctx.view && ctx.view.task;
    if (!t || t.ready) return;
    const p = pad(root);
    if (t.kind === 'draw') {
      if (p.cur) flush(root, true);
      ctx.act('done', { n: p.ops.length }).then((r) => {
        // сервер отримав не все — довіряємо його копії, людина подивиться й здасть ще раз
        if (r && !r.ok && /не цілком/.test(r.message || '')) {
          const body = root.querySelector('.tpbody');
          if (body) body.dataset.key = '';
        }
      });
      return;
    }
    const input = root.querySelector('.tpform input');
    clearTimeout(p.draftTimer);
    ctx.act('done', { text: input ? input.value : '' });
  }

  function entryHtml(ctx, e, chain, last, liked) {
    // seat -1 — фраза від Глека (партія на двох): її автор не гравець, і ❤ їй не ставлять
    const jug = e.seat < 0;
    const who = '<div class="tpby">' + (jug ? '🏺 Глек загадав:' : nick(ctx, e.seat) + (e.kind === 'drawing' ? ' малює:' : e.index === 0 ? ' починає:' : ' бачить:')) + '</div>';
    const body = e.kind === 'drawing'
      ? (blank(e.ops) ? '<div class="tptext tpempty">🤷 полотно лишилось порожнім</div>'
        : '<canvas class="tpshow" data-chain="' + chain + '" data-index="' + e.index + '"></canvas>')
      : '<div class="tptext">«' + ctx.esc(e.text || '') + '»</div>';
    const own = e.seat === ctx.seat;
    const on = liked == null ? !!e.liked : liked;
    const like = jug ? '' : '<button type="button" class="tplike' + (on ? ' on' : '') + '" data-chain="' + chain + '" data-index="' + e.index + '"'
      + ' title="' + (own || !ctx.mine || !ctx.playing ? 'Вподобайки' : on ? 'Забрати вподобайку' : 'Поставити вподобайку') + '"'
      + (own || !ctx.mine || !ctx.playing ? ' disabled' : '') + '>❤ ' + (e.likes || 0) + '</button>';
    return '<div class="tpentry' + (last ? ' fresh' : '') + '">' + who + body + like + '</div>';
  }

  /// Вид каже правду про мої ❤ у показаних записах — запам'ятовуємо, щоб між видами (❤ летять кадрами) не губити.
  function syncLiked(p, chain, entries) {
    for (const e of entries || []) {
      const k = chain + ':' + e.index;
      if (e.liked) p.liked.add(k); else p.liked.delete(k);
    }
  }

  /// Лічильники й свої ❤ — на місці, без перебудови ланцюжка: інакше кожне ❤ заново вставляло всі малюнки
  /// й наново програвало появу останнього запису (прохід 28.09).
  function paintLikes(root, chain) {
    const p = pad(root);
    const seen = p.seen.get(chain);
    root.querySelectorAll('.tplike[data-chain="' + chain + '"]').forEach((b) => {
      const e = seen && seen.entries.find((x) => x.index === +b.dataset.index);
      if (!e) return;
      const on = p.liked.has(chain + ':' + e.index);
      const text = '❤ ' + (e.likes || 0);
      if (b.textContent !== text) b.textContent = text;
      b.classList.toggle('on', on);
      if (!b.disabled) b.title = on ? 'Забрати вподобайку' : 'Поставити вподобайку';
    });
  }

  function revealScreen(root, ctx, v) {
    const r = v.reveal;
    const p = pad(root);
    if (r) {
      syncLiked(p, r.chain, r.entries);
      p.seen.set(r.chain, { owner: r.owner, entries: r.entries.map((e) => Object.assign({}, e)) });
    }
    const body = root.querySelector('.tpbody');
    const key = 'reveal|' + (r ? r.chain + ':' + r.shown + ':' + (ctx.mine ? 'm' : '') + (ctx.playing ? 'p' : '') : '');
    if (body.dataset.key === key) { if (r) paintLikes(root, r.chain); return; }
    const scrollToEnd = !r || body.dataset.chain !== String(r.chain) || +body.dataset.shown < r.shown;
    body.dataset.key = key;
    if (!r) { body.innerHTML = ''; return; }
    body.dataset.chain = String(r.chain);
    body.dataset.shown = String(r.shown);
    const more = r.shown < r.total ? 'Гортай далі ▸' : r.no < r.chains ? 'Наступний ланцюжок ▸' : 'Підсумки ▸';
    body.innerHTML = '<div class="tptitle">Ланцюжок ' + r.no + ' з ' + r.chains + ' · від ' + nick(ctx, r.owner) + '</div>'
      + '<div class="tpchain">' + r.entries.map((e, i) => entryHtml(ctx, e, r.chain, i === r.entries.length - 1, p.liked.has(r.chain + ':' + e.index))).join('') + '</div>'
      + (ctx.mine ? '<div class="tpact"><button type="button" class="primary" data-do="next">' + more + '</button></div>'
        : '<div class="tpwho">Гравці гортають ланцюжок</div>');
    wireChain(root, body, r.entries, r.chain);
    const next = body.querySelector('[data-do="next"]');
    if (next) next.onclick = () => { const c = root._ctx; if (c) c.act('next'); };
    if (scrollToEnd) {
      const last = body.querySelector('.tpentry.fresh');
      if (last) requestAnimationFrame(() => last.scrollIntoView({ block: 'nearest', behavior: 'smooth' }));
    }
  }

  function wireChain(root, body, entries, chain) {
    body.querySelectorAll('canvas.tpshow').forEach((el) => {
      const e = entries.find((x) => x.index === +el.dataset.index);
      if (!e) return;
      requestAnimationFrame(() => show(el, e.ops));
    });
    body.querySelectorAll('.tplike').forEach((b) => b.onclick = () => {
      const c = root._ctx;
      if (!c) return;
      const index = +b.dataset.index;
      c.act('like', { chain, index }).then((res) => {
        if (!res || !res.ok) return;
        // Сервер ❤ перемкнув; лічильник приїде кадром, а своє «стоїть / не стоїть» знаємо й так.
        const p = pad(root);
        const k = chain + ':' + index;
        if (p.liked.has(k)) p.liked.delete(k); else p.liked.add(k);
        paintLikes(root, chain);
      });
    });
  }

  function doneScreen(root, ctx, v) {
    const p = pad(root);
    const body = root.querySelector('.tpbody');
    const likes = v.likes || [];
    const rows = [];
    for (let i = 0; i < (ctx.room && ctx.room.seats ? ctx.room.seats.length : 10); i++) if (ctx.nickOf(i)) rows.push({ i, n: likes[i] || 0 });
    rows.sort((a, b) => b.n - a.n);
    const chains = [...p.seen.keys()].sort((a, b) => a - b);
    const pick = p.album != null && p.seen.has(p.album) ? p.album : chains[0];
    const key = 'done|' + likes.join(',') + '|' + pick + '|' + chains.join(',');
    if (body.dataset.key === key) return;
    body.dataset.key = key;
    const medal = ['🥇', '🥈', '🥉'];
    let html = '<div class="tptitle">Партію зіграно</div><div class="pcfinal">'
      + rows.map((r, k) => '<div><span>' + (medal[k] || (k + 1) + '.') + ' ' + nick(ctx, r.i) + '</span><b>❤ ' + r.n + '</b></div>').join('')
      + '</div>';
    if (chains.length) {
      html += '<div class="tpalbum">' + chains.map((c) => '<button type="button" class="' + (c === pick ? 'primary' : 'ghost') + '" data-album="' + c + '">'
        + nick(ctx, p.seen.get(c).owner) + '</button>').join('') + '</div>';
      const ch = p.seen.get(pick);
      html += '<div class="tpchain">' + ch.entries.map((e) => entryHtml(ctx, e, pick, false, p.liked.has(pick + ':' + e.index))).join('') + '</div>';
    }
    body.innerHTML = html;
    body.querySelectorAll('[data-album]').forEach((b) => b.onclick = () => { p.album = +b.dataset.album; const c = root._ctx; if (c) doneScreen(root, c, c.view || {}); });
    if (chains.length) wireChain(root, body, p.seen.get(pick).entries, pick);
  }

  /// Де на сторінці починається полотно чи малюнок кроку — з цього CSS рахує їхню ширину, щоб «Готово» влізло в екран.
  function fitStep(root) {
    const wrap = root.querySelector('.tpwrap');
    const el = root.querySelector('.tpbody > .tpcanvas, .tpbody > .tpshow.big');
    if (!wrap || !el || !el.isConnected) return;
    const top = Math.round(el.getBoundingClientRect().top + window.scrollY) + 'px';
    if (wrap.style.getPropertyValue('--tptop') !== top) wrap.style.setProperty('--tptop', top);
  }

  function render(root, ctx) {
    root._ctx = ctx;
    ctx.tpRoot = root;
    const v = ctx.view || {};
    root.querySelector('.tpwrap').classList.toggle('live', !!ctx.playing);   // на телефоні в партії чіпи місць ховаються
    const head = root.querySelector('.tphead');
    // У лобі вид теж у фазі step, але кроків ще нема — «Крок 0 з 0» нічого не каже.
    const text = v.phase === 'step' ? (v.steps ? 'Крок ' + v.step + ' з ' + v.steps : '')
      : v.phase === 'reveal' ? 'Показ' : v.phase === 'done' ? 'Альбом' : '';
    if (head.textContent !== text) head.textContent = text;
    if (v.phase === 'step') { stepScreen(root, ctx, v); fitStep(root); }
    else if (v.phase === 'reveal') revealScreen(root, ctx, v);
    else if (v.phase === 'done') doneScreen(root, ctx, v);
    timer(root, ctx);
  }

  HGames.register({
    id: 'telephone',
    added: '2026-09-17',          // нова гра: «🆕» у лобі два тижні тим, хто ще не грав (core.js, isNewGame)
    icon: ICON,
    news: {
      v: '2026-09-24',
      title: 'Зіпсований телефон: тепер і вдвох',
      items: [
        '👫 Грати можна вже вдвох: фразу кожному загадує Глек, ти малюєш — сусід угадує, і навпаки',
        '🏺 На показі видно, що саме Глек загадав і що з того вийшло',
        '✋ Малювати пальцем надійніше: другий дотик чи долоня більше не черкають лінію через усе полотно',
      ],
    },
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o', 'c', 'd', 'x', 'o'],

    mount(root, ctx) {
      root.innerHTML = '<div class="tpwrap">'
        + '<div class="tptop"><div class="tphead muted small"></div><div class="pctime"><i></i><span class="pcsec"></span></div></div>'
        + '<div class="tpbody"></div></div>';
      const p = pad(root);
      p.timer = setInterval(() => { if (root._ctx) timer(root, root._ctx); }, 250);
      // інша ширина вікна — інакше лягають чіпи місць і заголовок, і полотно починається деінде
      new ResizeObserver(() => fitStep(root)).observe(root.querySelector('.tpwrap'));
      render(root, ctx);
    },

    update(root, ctx) { render(root, ctx); },

    /// Кадр лише на показі: хтось поставив чи зняв ❤ — латаємо лічильники, ланцюжок не чіпаємо.
    frame(root, ctx, f) {
      if (!f || !Array.isArray(f.likes)) return;
      const p = pad(root);
      const seen = p.seen.get(f.chain);
      if (!seen) return;
      f.likes.forEach((n, i) => { const e = seen.entries.find((x) => x.index === i); if (e) e.likes = n; });
      paintLikes(root, f.chain);
    },

    unmount(root) {
      const p = root._tp;
      if (!p) return;
      clearInterval(p.timer);
      clearTimeout(p.flushTimer);
      clearTimeout(p.draftTimer);
      if (p.raf) cancelAnimationFrame(p.raf);
    },

    onKey(e, ctx) {
      if ((e.ctrlKey || e.metaKey) && e.code === 'KeyZ' && ctx.tpRoot && canDraw(ctx)) { undo(ctx.tpRoot); return true; }
      return false;
    },

    status(ctx) {
      const v = ctx.view || {};
      if (v.phase === 'done' || !ctx.playing) return v.phase === 'done' ? 'Гортай ланцюжки — кнопки з іменами' : '';
      if (v.phase === 'reveal') return ctx.mine ? 'Став ❤ вподобайки смішним записам і гортай далі' : 'Дивишся збоку';
      if (!ctx.mine) return 'Дивишся збоку';
      const t = v.task;
      if (!t) return 'Чекаємо на інших';
      if (t.ready) return 'Здано — чекаємо на інших';
      return t.kind === 'phrase' ? 'Тяпни фразу' : t.kind === 'draw' ? 'Малюй!' : 'Опиши малюнок';
    },
  });
})();
