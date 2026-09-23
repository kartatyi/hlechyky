/*
  Шашки (російські). Дошка 8×8, правила — тільки на сервері: тут ми лише малюємо позицію
  й збираємо намір гравця. Клік по своїй шашці підсвічує перші кроки, клік далі добудовує
  ланцюг; щойно він збігся з повним легальним ходом — летить act('move', { path }).

  Вид із сервера (Impl/Checkers.cs):
  { board: 64 символи a8..h1 ('w'|'W'|'b'|'B'|'.'|' '), turn, toMove, legal: string[][],
    mustCapture, lastPath, count: {w,b}, drawOffer, result }
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="6" cy="10" r="4.4" fill="var(--clay)"/>'
    + '<circle cx="10.4" cy="5.6" r="4.4" fill="var(--accent)" stroke="var(--bg)" stroke-width="1"/></svg>';

  /// Поле 0..63 → «c3»: 0 — a8, 63 — h1, як у рядку board.
  const nameOf = (i) => String.fromCharCode(97 + (i % 8)) + (8 - Math.floor(i / 8));
  /// Темні поля — ті, де сума ряду й колонки непарна (a1 темне).
  const dark = (i) => ((Math.floor(i / 8) + (i % 8)) % 2) === 1;

  function pieceHtml(ch, slide) {
    if (ch !== 'w' && ch !== 'W' && ch !== 'b' && ch !== 'B') return '';
    const mv = slide ? ' slide" style="--dx:' + slide.dx + ';--dy:' + slide.dy : '';
    return '<i class="ckp ' + (ch === 'w' || ch === 'W' ? 'w' : 'b') + (ch === 'W' || ch === 'B' ? ' k' : '') + mv + '"></i>';
  }

  const SLIDE_MS = 300;
  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  /// «c3» → 0..63 (0 — a8), як у рядку board.
  const idxOf = (nm) => (8 - +nm[1]) * 8 + (nm.charCodeAt(0) - 97);

  /// Чим скінчилась партія — людськими словами (view.result.reason).
  const REASON = {
    nopieces: 'Шашок не лишилось', nomoves: 'Ходити нічим — замкнули', agreed: 'Нічия за згодою',
    repetition: 'Нічия: тричі та сама позиція', kings15: 'Нічия: 15 ходів самими дамками', left: 'Хтось встав з-за столу',
    time: 'Упав прапорець — час вийшов',
  };

  function state(root) {
    // sure — «Здатись» натиснули раз і чекаємо на підтвердження: здача незворотна й коштує партії.
    if (!root._ck) root._ck = { path: [], sig: '', sure: false, lastKey: undefined, slideUntil: 0 };
    return root._ck;
  }

  /// Смужка над дошкою і кнопки під нею: створюємо один раз, далі лише переписуємо вміст.
  function strip(root, cls, top) {
    let el = root.querySelector(':scope > .' + cls);
    if (!el) {
      el = document.createElement('div');
      el.className = cls;
      if (top) root.insertBefore(el, root.firstChild); else root.appendChild(el);
    }
    return el;
  }

  function setHtml(el, html) { if (el.innerHTML !== html) el.innerHTML = html; }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const board = v.board || '';
    const legal = v.legal || [];
    const last = v.lastPath || [];
    const st = state(root);
    ctx._ckRoot = root;                      // щоб Escape із onKey знав, де лежить вибір

    // Позиція змінилась (хтось походив) — недобудований ланцюг більше ні до чого.
    const sig = board + '|' + v.turn + '|' + last.join('');
    if (st.sig !== sig) { st.sig = sig; st.path = []; st.sure = false; }

    // Ланцюги, що починаються з уже набраного шляху; з них і беремо, куди можна далі.
    const fit = legal.filter((c) => st.path.every((s, i) => c[i] === s));
    const next = new Set();
    if (ctx.myTurn) for (const c of fit) if (c.length > st.path.length) next.add(c[st.path.length]);
    // Поки нічого не вибрано — показати, якими шашками взагалі можна ходити (особливо коли треба бити).
    const can = new Set();
    if (ctx.myTurn && !st.path.length) for (const c of legal) can.add(c[0]);
    const taken = new Set(v.lastTaken || []);
    // Шашка, що щойно походила, доїжджає зі свого старого поля. Першу картинку не анімуємо.
    const lastKey = last.join('-');
    if (st.lastKey !== undefined && last.length > 1 && lastKey !== st.lastKey && !reduced()) st.slideUntil = performance.now() + SLIDE_MS;
    st.lastKey = lastKey;

    const count = v.count || {};
    setHtml(strip(root, 'ckbar', true),
      '<span class="gscore"><i class="ckp w"></i><b>' + (count.w != null ? count.w : 12) + '</b>'
      + ' : <b>' + (count.b != null ? count.b : 12) + '</b><i class="ckp b"></i></span>'
      + (v.mustCapture && !v.result ? '<span class="ckmust">Бити обов\'язково</span>' : ''));

    // Чорним показуємо дошку з їхнього боку: поворот на 180° — це дзеркало обох осей, тобто 63 - i.
    const flip = ctx.seat === 1;
    let slide = null;
    if (last.length > 1 && performance.now() < st.slideUntil) {
      const a = idxOf(last[0]), b = idxOf(last[last.length - 1]);
      const da = flip ? 63 - a : a, db = flip ? 63 - b : b;
      slide = { to: last[last.length - 1], dx: (da % 8) - (db % 8), dy: (da >> 3) - (db >> 3) };
    }
    const moverCls = last.length ? ((board[idxOf(last[last.length - 1])] || '').toLowerCase() === 'w' ? 'b' : 'w') : '';
    const board$ = HGames.ui.grid(root, {
      cols: 8,
      rows: 8,
      cls: 'ck',
      cell: (i) => {
        const idx = flip ? 63 - i : i;
        const name = nameOf(idx);
        const cls = [dark(idx) ? 'dark' : 'light'];
        if (st.path.indexOf(name) >= 0) cls.push('sel');
        if (next.has(name)) cls.push('pick');
        if (last.indexOf(name) >= 0) cls.push('last');
        if (can.has(name)) cls.push(v.mustCapture ? 'can must' : 'can');
        // Звідки щойно зняли побиті — ледь видимий слід шашки кольору того, кого били.
        const ghost = taken.has(name) && board[idx] === '.' ? '<i class="ckp ghost ' + moverCls + '"></i>' : '';
        const html = pieceHtml(board[idx], slide && slide.to === name ? slide : null) || ghost;
        return { html, cls: cls.join(' '), disabled: !ctx.myTurn || !dark(idx) };
      },
      onCell: (i) => tap(root, ctx, nameOf(flip ? 63 - i : i)),
    });

    clockPlates(root, ctx, board$, board$, flip ? 0 : 1);

    const acts = strip(root, 'ckacts', false);
    const offer = v.drawOffer;
    const mine = ctx.mine && ctx.playing;
    let html = '';
    if (mine && st.path.length) html += '<button class="ghost" data-ck="reset">Скинути вибір</button>';
    if (mine && offer != null && offer !== ctx.seat) {
      html += '<span class="muted small">Пропонують нічию</span>'
        + '<button class="primary" data-ck="draw">Згода</button><button class="ghost" data-ck="decline">Ні</button>';
    } else if (mine) {
      html += '<button class="ghost" data-ck="draw"' + (offer === ctx.seat ? ' disabled' : '') + '>Нічия?</button>'
        + '<button class="' + (st.sure ? 'primary' : 'ghost') + '" data-ck="resign">'
        + (st.sure ? 'Точно здатись?' : 'Здатись') + '</button>';
    }
    if (!mine) {
      let res = v.result && ctx.room.status === 'finished' ? REASON[v.result.reason] : '';
      if (v.result && v.result.reason === 'resign' && v.result.winner != null) {
        res = ctx.esc(ctx.nickOf(1 - v.result.winner) || ctx.seatName(1 - v.result.winner)) + ' здався';
      }
      if (res) html += '<span class="ckres">' + res + '</span>';
    }
    setHtml(acts, html + seriesHtml(ctx));
    acts.querySelectorAll('[data-ck]').forEach((b) => b.onclick = () => {
      const what = b.dataset.ck;
      if (what === 'reset') { st.path = []; st.sure = false; paint(root, ctx); return; }
      // Здача — єдина незворотна дія модуля, тож у два дотики. Без таймерів: прапорець знімає
      // будь-який хід, клік по дошці, «Скинути вибір» або Escape.
      if (what === 'resign' && !st.sure) { st.sure = true; paint(root, ctx); return; }
      st.sure = false;
      ctx.act(what);
    });
  }

  /// Клік по полю: добудовуємо ланцюг, а коли він збігся з повним легальним ходом — шлемо його.
  function tap(root, ctx, name) {
    if (!ctx.myTurn) return;
    const st = state(root);
    st.sure = false;                         // рука пішла на дошку — «Точно здатись?» більше не висить
    const legal = (ctx.view && ctx.view.legal) || [];
    const fit = legal.filter((c) => st.path.every((s, i) => c[i] === s));
    if (fit.some((c) => c[st.path.length] === name)) {
      st.path.push(name);
      // Повний ланцюг ніколи не є початком іншого повного, тож збіг довжини — це кінець ходу.
      const done = legal.some((c) => c.length === st.path.length && c.every((s, i) => s === st.path[i]));
      if (done) {
        const path = st.path.slice();
        st.path = [];
        paint(root, ctx);
        ctx.act('move', { path });
        return;
      }
      paint(root, ctx);
      return;
    }
    // Клік повз ланцюг: тицьнув у вибрану шашку вдруге — знімаємо вибір (так поводиться будь-яка
    // дошка, і на телефоні це єдиний спосіб передумати без кнопки); інша своя — вибір із неї.
    st.path = st.path.length === 1 && st.path[0] === name
      ? []
      : legal.some((c) => c[0] === name) ? [name] : [];
    paint(root, ctx);
  }

  // ---- годинник ---------------------------------------------------------------------------------
  // Той самий шматок живе і в checkers.js: спільного файла для двох модулів каркас не вантажить.
  // Сервер шле, скільки в кого лишилось на момент виду (clock.ms) і чий час іде (clock.running);
  // решту відлічуємо тут самі. Коли в когось упав прапорець, браузер каже серверу flag — той звіряє
  // зі своїм годинником. Суперник заявляє одразу, сам прострочений — трохи згодом (раптом суперник пішов).

  function fmtMs(ms) {
    ms = Math.max(0, ms);
    if (ms < 10000) return (Math.floor(ms / 100) / 10).toFixed(1);
    const s = Math.ceil(ms / 1000);
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  function clockPlates(root, ctx, topAnchor, bottomAnchor, topSeat) {
    const c = (ctx.view || {}).clock;
    const st = root._clk || (root._clk = { key: '', base: null, at: 0, timer: 0, flagged: '' });
    if (!c) {
      root.querySelectorAll(':scope > .bclock').forEach((el) => el.remove());
      clearInterval(st.timer);
      st.timer = 0;
      return;
    }
    const key = JSON.stringify(c);
    if (key !== st.key) { st.key = key; st.base = c; st.at = performance.now(); }
    for (const [anchor, pos, seat] of [[topAnchor, 'top', topSeat], [bottomAnchor, 'bottom', 1 - topSeat]]) {
      let el = root.querySelector(':scope > .bclock.' + pos);
      if (!el) {
        el = document.createElement('div');
        el.className = 'bclock ' + pos;
        anchor.insertAdjacentElement(pos === 'top' ? 'beforebegin' : 'afterend', el);
      }
      el.dataset.seat = String(seat);
    }
    tickClock(root, ctx);
    if (!st.timer) st.timer = setInterval(() => tickClock(root, ctx), 200);
  }

  function tickClock(root, ctx) {
    const st = root._clk;
    if (!st || !st.base) return;
    const c = st.base;
    const now = performance.now();
    root.querySelectorAll(':scope > .bclock').forEach((el) => {
      const seat = +el.dataset.seat;
      const run = ctx.playing && c.running === seat;
      const left = (c.ms[seat] || 0) - (run ? now - st.at : 0);
      const nick = ctx.nickOf(seat) || ctx.seatName(seat);
      const html = '<span class="who">' + ctx.esc(nick) + (seat === ctx.seat ? ' <i>(ти)</i>' : '') + '</span>'
        + '<b>' + fmtMs(left) + '</b>';
      if (el.innerHTML !== html) el.innerHTML = html;
      el.classList.toggle('run', run);
      el.classList.toggle('low', run && left < 20000);
      el.classList.toggle('out', left <= 0);
      if (run && left <= 0 && ctx.mine) claimFlag(root, ctx, seat);
    });
  }

  function claimFlag(root, ctx, seat) {
    const st = root._clk;
    if (st.flagged === st.key) return;
    st.flagged = st.key;
    setTimeout(() => {
      if (!ctx.playing || !root._clk || root._clk.key !== st.flagged) return;
      // Сервер каже «Час ще є», якщо наш відлік забіг уперед, — тоді спробуємо ще раз за секунду.
      ctx.act('flag').then((r) => { if (r && !r.ok && root._clk) setTimeout(() => { root._clk.flagged = ''; }, 1000); });
    }, seat === ctx.seat ? 2500 : 350);
  }

  function stopClock(root) {
    if (root._clk) clearInterval(root._clk.timer);
    root._clk = null;
  }

  /// Рахунок серії «Ще раз» тим самим складом: хто скільки виграв за цим столом.
  function seriesHtml(ctx) {
    const s = (ctx.view || {}).series;
    if (!s || !s.wins) return '';
    const parts = [0, 1].map((i) => ctx.esc(ctx.nickOf(i) || ctx.seatName(i)) + ' <b>' + (s.wins[i] || 0) + '</b>');
    return '<span class="gserie" title="Скільки партій виграв кожен за цим столом">Серія: ' + parts.join(' : ')
      + (s.draws ? ' · нічиїх <b>' + s.draws + '</b>' : '') + '</span>';
  }

  HGames.register({
    id: 'checkers',
    icon: ICON,
    seatNames: ['білі', 'чорні'],
    seatClass: ['c', 'd'],
    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) {
      paint(root, ctx);
      const st = state(root);
      clearTimeout(st.t);
      if (performance.now() < st.slideUntil) st.t = setTimeout(() => root._ck && paint(root, ctx), SLIDE_MS + 30);
    },
    onKey(e, ctx) {
      if (e.key !== 'Escape' || !ctx._ckRoot) return false;
      const st = state(ctx._ckRoot);
      if (!st.path.length && !st.sure) return false;
      st.path = [];
      st.sure = false;
      paint(ctx._ckRoot, ctx);
      return true;
    },
    unmount(root) { if (root._ck) clearTimeout(root._ck.t); root._ck = null; stopClock(root); },

    news: {
      v: '2026-09-24',
      title: 'Шашки: годинник, підказки і рахунок серії',
      items: [
        '⏱ Можна грати з годинником: 3, 5 або 10 хвилин із надбавкою за хід — обирається, коли ставиш стіл',
        '💡 Шашки, якими можна ходити, підсвічені одразу — а коли треба бити, то яскравіше',
        '👣 Шашка доїжджає на нове поле, а на місці побитих лишається ледь видимий слід',
        '🤝 «Нічия?» більше не зникає від власного ходу, а під дошкою — чим скінчилось і рахунок серії',
      ],
    },
  });
})();
