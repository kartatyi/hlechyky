/*
  Реверсі (Отелло). Один модуль на обидва режими: стіл удвох (reversi) і соло з Глеком (reversi-glek,
  Client: "reversi"). Правила — лише на сервері; тут малюємо позицію, анімуємо переворот і шлемо act('move', { cell }).
  Клієнтський підрахунок перевернутих — тільки для прев'ю під мишею/курсором.

  Вид (docs/games/specs/reversi.md): { board: 64 символи ('.'|'b'|'w', 0 = a1 угорі ліворуч), turn, toMove, legal,
  last, flipped, pass, count: {b, w}, moves, clock, result: {winner, reason, b, w}, series, me?, glek? }.
  У Глека turn/pass/winner — місця (людина 0, Глек 1), колір людини — view.me; за столом удвох місце = колір (0 чорні).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="5.6" cy="10.4" r="4.6" fill="var(--clay)"/>'
    + '<circle cx="10.4" cy="5.6" r="4.6" fill="var(--text)" stroke="var(--bg)" stroke-width="1"/></svg>';

  const kindOf = (ctx) => (ctx.room && ctx.room.game === 'reversi-glek' ? 'glek' : 'table');
  const nameOf = (i) => String.fromCharCode(97 + (i & 7)) + ((i >> 3) + 1);
  const DOT = { b: '⚫', w: '⚪' };
  const COLOR = { b: 'чорні', w: 'білі' };

  const REASON = {
    count: 'Фішки пораховано', wipe: 'Дошку витерто — фішок не лишилось',
    left: 'Хтось встав з-за столу', time: 'Упав прапорець — час вийшов',
  };
  const LEVELS = [['easy', '🙂 Легкий'], ['medium', '🧐 Звичайний'], ['hard', '😈 Сильний']];
  const LEVEL_NAME = { easy: 'легкий', medium: 'звичайний', hard: 'сильний' };
  const COLORS = [['black', '⚫ Чорними'], ['white', '⚪ Білими'], ['turn', '⇄ По черзі']];

  const PREFS = 'reversiGlekPrefs';
  const HINTS = 'reversiHints';
  const load = (k) => { try { return localStorage.getItem(k); } catch { return null; } };
  const save = (k, v) => { try { localStorage.setItem(k, v); } catch { /* приватне вікно */ } };
  const loadPrefs = () => { try { return JSON.parse(load(PREFS) || 'null'); } catch { return null; } };

  const FLIP_MS = 350, STEP_MS = 40, POP_MS = 220;
  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const mouseHover = () => window.matchMedia && window.matchMedia('(hover: hover)').matches;

  const DIRS = [[-1, -1], [-1, 0], [-1, 1], [0, -1], [0, 1], [1, -1], [1, 0], [1, 1]];
  /// Що перевернеться, якщо колір c стане на поле i. Лише для прев'ю: правила перевіряє сервер.
  function flipsOf(board, i, c) {
    if (board[i] !== '.') return [];
    const o = c === 'b' ? 'w' : 'b';
    const r0 = i >> 3, c0 = i & 7, out = [];
    for (const [dr, dc] of DIRS) {
      let r = r0 + dr, cc = c0 + dc;
      const run = [];
      while (r >= 0 && r < 8 && cc >= 0 && cc < 8 && board[r * 8 + cc] === o) { run.push(r * 8 + cc); r += dr; cc += dc; }
      if (run.length && r >= 0 && r < 8 && cc >= 0 && cc < 8 && board[r * 8 + cc] === c) out.push(...run);
    }
    return out;
  }

  function state(root) {
    if (!root._rv) root._rv = { lastKey: undefined, passKey: undefined, animUntil: 0, anim: null, sure: '', cur: 27, kbd: false, hover: -1, prefsRound: null, t: 0 };
    return root._rv;
  }

  function strip(root, cls, top) {
    let el = root.querySelector(':scope > .' + cls);
    if (!el) {
      el = document.createElement('div');
      el.className = cls;
      if (top) root.insertBefore(el, root.firstChild); else root.appendChild(el);
    }
    return el;
  }
  /// Порівнюємо з рядком, який клали самі: серіалізація innerHTML браузером майже ніколи не збігається.
  function setHtml(el, html) { if (el._h !== html) { el._h = html; el.innerHTML = html; } }

  /// Місце гравця, що грає кольором c.
  function seatOfColor(ctx, c) {
    const v = ctx.view || {};
    if (kindOf(ctx) === 'glek') return c === (v.me || 'b') ? 0 : 1;
    return c === 'b' ? 0 : 1;
  }
  /// Колір того, хто сидить на місці seat.
  function colorOfSeat(ctx, seat) {
    const v = ctx.view || {};
    if (kindOf(ctx) === 'glek') { const me = v.me || 'b'; return seat === 0 ? me : (me === 'b' ? 'w' : 'b'); }
    return seat === 0 ? 'b' : 'w';
  }
  function whoName(ctx, seat) {
    if (kindOf(ctx) === 'glek') {
      if (seat === 1) { const g = (ctx.view || {}).glek || {}; return '🤖 Глек' + (g.level ? ' (' + (LEVEL_NAME[g.level] || g.level) + ')' : ''); }
      return ctx.nickOf(0) || 'ти';
    }
    return ctx.nickOf(seat) || ctx.seatName(seat);
  }
  const hintsOn = () => load(HINTS) !== '0';
  const myColor = (ctx) => (ctx.mine ? colorOfSeat(ctx, ctx.seat) : null);

  function paint(root, ctx) {
    const v = ctx.view || {};
    const board = typeof v.board === 'string' && v.board.length === 64 ? v.board : '.'.repeat(64);
    const st = state(root);
    st.ctx = ctx;
    ctx._rvRoot = root;
    const legal = new Set(v.legal || []);
    const kind = kindOf(ctx);

    // Анімація — лише коли змінився останній хід (вид приходить і на думки Глека, і на годинник).
    const lastKey = v.last == null ? '' : v.last + ':' + (v.moves || 0);
    if (st.lastKey !== undefined && lastKey !== st.lastKey && v.last != null && !reduced()) {
      const d = {};
      let max = 0;
      for (const f of v.flipped || []) {
        const dist = Math.max(Math.abs((f >> 3) - (v.last >> 3)), Math.abs((f & 7) - (v.last & 7)));
        d[f] = (dist - 1) * STEP_MS;
        if (d[f] > max) max = d[f];
      }
      st.anim = { pop: v.last, d };
      st.animUntil = performance.now() + Math.max(POP_MS, FLIP_MS + max) + 30;
    }
    if (lastKey !== st.lastKey) st.sure = '';
    st.lastKey = lastKey;
    const animating = st.anim && performance.now() < st.animUntil;
    if (!animating) st.anim = null;

    // Пас: тост лише на зміну (pass тримається до наступного ходу).
    const passKey = lastKey + '|' + (v.pass == null ? '' : v.pass);
    if (st.passKey !== undefined && passKey !== st.passKey && v.pass != null && !v.result) ctx.toast(passText(ctx, v.pass));
    st.passKey = passKey;

    // Шапка: рахунок, хто яким кольором, чия черга.
    const cnt = v.count || {};
    const side = (c) => {
      const seat = seatOfColor(ctx, c);
      const on = v.toMove === c && ctx.playing;
      const nm = '<span class="nm">' + ctx.esc(whoName(ctx, seat)) + (ctx.mine && seat === ctx.seat && kind === 'table' ? ' <i>(ти)</i>' : '') + '</span>';
      const num = '<b>' + (cnt[c] != null ? cnt[c] : 2) + '</b>';
      return '<span class="rvside ' + c + (on ? ' on' : '') + '" title="' + COLOR[c] + '"><i class="rvd ' + c + '"></i>'
        + (c === 'b' ? nm + num : num + nm) + '</span>';
    };
    setHtml(strip(root, 'rvbar', true), side('b') + '<span class="rvsep">:</span>' + side('w'));

    const hints = hintsOn() && ctx.myTurn;
    const anim = animating ? st.anim : null;
    const el = HGames.ui.grid(root, {
      cols: 8,
      rows: 8,
      cls: 'rv',
      cell: (i) => {
        const ch = board[i];
        const cls = [];
        if (i === v.last) cls.push('last');
        if (ctx.myTurn && legal.has(i)) cls.push('ok');
        if (st.kbd && ctx.mine && ctx.playing && i === st.cur) cls.push('cur');
        let html = '';
        if (ch === 'b' || ch === 'w') {
          let a = '';
          if (anim && anim.pop === i) a = ' pop';
          else if (anim && anim.d[i] != null) a = ' flip" style="--d:' + anim.d[i] + 'ms';
          html = '<i class="rvd ' + ch + a + '"></i>';
        } else if (hints && legal.has(i)) html = '<i class="rvdot ' + (v.toMove || 'b') + '"></i>';
        return { html, cls: cls.join(' '), disabled: !(ctx.myTurn && ch === '.') };
      },
      onCell: (i) => { st.kbd = false; st.cur = i; if (legal.has(i)) move(root, ctx, i); },
    });
    hookHover(root, el);
    preview(root);

    if (kind === 'table') clockPlates(root, ctx, el, el, ctx.seat === 1 ? 0 : 1);
    else stopClock(root);

    acts(root, ctx);
  }

  function passText(ctx, seat) {
    if (kindOf(ctx) === 'glek') {
      return seat === 1 ? '🤖 Глеку ходити нікуди — пас, знову твій хід' : 'Тобі ходити нікуди — пас, ходить Глек';
    }
    const c = colorOfSeat(ctx, seat);
    if (ctx.mine && seat === ctx.seat) return DOT[c] + ' Тобі ходити нікуди — пас';
    return DOT[c] + ' ' + whoName(ctx, seat) + ' пасує — ходити нікуди';
  }

  function move(root, ctx, i) {
    if (!ctx.myTurn) return;
    const st = state(root);
    st.sure = '';
    st.hover = -1;
    ctx.act('move', { cell: i });
  }

  // ---- прев'ю під мишею й під курсором клавіатури/пада ----------------------------------------------

  function hookHover(root, el) {
    if (el._rvHook) return;
    el._rvHook = true;
    el.addEventListener('pointerover', (e) => {
      if (e.pointerType !== 'mouse' || !mouseHover()) return;
      const b = e.target.closest('.cell');
      const st = root._rv;
      if (!b || !st) return;
      st.hover = +b.dataset.i;
      if (st.kbd) { st.kbd = false; b.parentNode.querySelectorAll('.cur').forEach((c) => c.classList.remove('cur')); }
      preview(root);
    });
    el.addEventListener('pointerleave', () => { if (root._rv) { root._rv.hover = -1; preview(root); } });
  }

  function preview(root) {
    const st = root._rv;
    const el = root.querySelector(':scope > .board.rv');
    if (!st || !el) return;
    el.querySelectorAll('.gh-b, .gh-w, .will').forEach((c) => c.classList.remove('gh-b', 'gh-w', 'will'));
    const ctx = st.ctx;
    const v = (ctx && ctx.view) || {};
    if (!ctx || !ctx.myTurn || !v.toMove || typeof v.board !== 'string') return;
    const at = st.hover >= 0 ? st.hover : st.kbd ? st.cur : -1;
    if (at < 0 || (v.legal || []).indexOf(at) < 0) return;
    const kids = el.children;
    kids[at].classList.add('gh-' + v.toMove);
    for (const f of flipsOf(v.board, at, v.toMove)) kids[f].classList.add('will');
  }

  // ---- кнопки під дошкою ----------------------------------------------------------------------------

  function chips(name, list, cur) {
    return '<span class="rv-chips" role="group">' + list.map(([k, t]) => '<button type="button" class="ghost' + (k === cur ? ' on' : '')
      + '" data-set="' + name + '" data-v="' + k + '"' + (k === cur ? ' aria-pressed="true"' : '') + '>' + t + '</button>').join('') + '</span>';
  }

  function endHtml(ctx) {
    const v = ctx.view || {};
    const r = v.result;
    if (!r || ctx.room.status !== 'finished') return '';
    const score = '⚫ ' + (r.b != null ? r.b : (v.count || {}).b) + ' : ' + (r.w != null ? r.w : (v.count || {}).w) + ' ⚪';
    let head;
    if (kindOf(ctx) === 'glek') head = r.winner === 0 ? '🎉 Глека обіграно!' : r.winner === 1 ? '🤖 Глек узяв гору' : '🤝 Нічия';
    else if (r.winner == null) head = '🤝 Нічия';
    else head = '🏆 Виграє ' + ctx.esc(whoName(ctx, r.winner)) + ' ' + DOT[colorOfSeat(ctx, r.winner)];
    let why = REASON[r.reason] || '';
    if (r.reason === 'resign' && r.winner != null) {
      why = kindOf(ctx) === 'glek' ? '🏳 Ти здався' : '🏳 ' + ctx.esc(whoName(ctx, 1 - r.winner)) + ' здається';
    }
    return '<div class="rvend' + (ctx.mine && r.winner === ctx.seat ? ' win' : '') + '"><b>' + head + '</b>'
      + '<span class="rvscore">' + score + '</span>' + (why ? '<span class="muted small">' + why + '</span>' : '') + '</div>';
  }

  function acts(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const el = strip(root, 'rvacts', false);
    const kind = kindOf(ctx);
    const over = ctx.room.status === 'finished';
    const mine = ctx.mine;
    let html = endHtml(ctx);
    let row = '';
    if (mine && ctx.playing) {
      row += '<button type="button" class="ghost danger" data-rv="' + (st.sure === 'resign' ? 'resign' : 'ask-resign') + '">'
        + (st.sure === 'resign' ? 'Точно здатись?' : 'Здатись') + '</button>';
    }
    if (kind === 'glek' && mine) {
      const fresh = !over && (v.moves || 0) < 2;
      row += '<button type="button" class="' + (over ? 'primary' : 'ghost') + '" data-rv="'
        + (over || fresh || st.sure === 'new' ? 'new' : 'ask-new') + '">'
        + (st.sure === 'new' ? 'Точно заново?' : '🔄 Нова партія') + '</button>';
    }
    if (mine && ctx.playing) {
      row += '<button type="button" class="ghost rvhint' + (hintsOn() ? ' on' : '') + '" data-rv="hints" aria-pressed="' + hintsOn()
        + '" title="Крапки на полях, куди можна поставити">💡 підказки</button>';
    }
    if (row) html += '<div class="rvrow">' + row + '</div>';
    if (kind === 'glek' && mine) {
      const g = v.glek || {};
      const coarse = HGames.ui.coarse ? HGames.ui.coarse() : false;
      const open = st.setOpen != null ? st.setOpen : !coarse;
      html += '<details class="rv-set"' + (open ? ' open' : '') + '><summary>⚙️ Налаштування · ' + (LEVEL_NAME[g.level] || '') + '</summary>'
        + '<div class="rv-glek">' + chips('level', LEVELS, g.level) + chips('color', COLORS, g.color) + '</div>'
        + '<span class="muted small">' + (over ? 'Обране — на наступну партію' : 'Зміна — нова партія. Без рейтингу й черепків.') + '</span></details>';
    }
    if (kind === 'table') html += seriesHtml(ctx);
    setHtml(el, html);
    const det = el.querySelector('details.rv-set');
    if (det) det.ontoggle = () => { st.setOpen = det.open; };
    el.querySelectorAll('button').forEach((b) => b.onclick = () => click(root, ctx, b));
  }

  function click(root, ctx, b) {
    const st = state(root);
    const v = ctx.view || {};
    const what = b.dataset.rv;
    if (what === 'hints') { save(HINTS, hintsOn() ? '0' : '1'); paint(root, ctx); return; }
    // Здача й скидання партії незворотні — у два дотики. Скидає питання будь-який хід.
    if (what === 'ask-resign') { st.sure = 'resign'; paint(root, ctx); return; }
    if (what === 'ask-new') { st.sure = 'new'; paint(root, ctx); return; }
    st.sure = '';
    if (what === 'resign') { ctx.act('resign'); return; }
    if (what === 'new') {
      // Після кінця каркас відмовляє на будь-яку дію, тож нова партія — це «Ану ще раз»; обране (prefs) докине glekPrefs.
      if (ctx.room.status === 'finished') HGames.call('Rematch', ctx.room.id);
      else ctx.act('set', { level: (v.glek || {}).level || 'medium' });
      return;
    }
    if (b.dataset.set) {
      const g = v.glek || {};
      const p = Object.assign({ level: g.level, color: g.color }, loadPrefs() || {});
      p[b.dataset.set] = b.dataset.v;
      save(PREFS, JSON.stringify(p));
      if (ctx.room.status !== 'finished') ctx.act('set', { [b.dataset.set]: b.dataset.v });
      else { ctx.toast('Запам\'ятав — так і зіграємо наступну партію'); paint(root, ctx); }
    }
  }

  /// Нова партія з Глеком (перший показ чи «Ану ще раз») — поставити те, що людина обирала минулого разу.
  function glekPrefs(root, ctx) {
    const st = state(root);
    if (kindOf(ctx) !== 'glek' || !ctx.playing || st.prefsRound === ctx.room.round) return;
    st.prefsRound = ctx.room.round;
    const p = loadPrefs();
    const v = ctx.view || {};
    const g = v.glek || {};
    if (!p || (v.moves || 0) > 1) return;
    const diff = {};
    if (p.level && p.level !== g.level) diff.level = p.level;
    if (p.color && p.color !== g.color) diff.color = p.color;
    if (Object.keys(diff).length) ctx.act('set', diff);
  }

  // ---- годинник (той самий, що в шашках: спільного файла каркас не вантажить) ----------------------

  function fmtMs(ms) {
    ms = Math.max(0, ms);
    if (ms < 10000) return (Math.floor(ms / 100) / 10).toFixed(1);
    const s = Math.ceil(ms / 1000);
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  function clockPlates(root, ctx, topAnchor, bottomAnchor, topSeat) {
    const c = (ctx.view || {}).clock;
    const st = root._clk || (root._clk = { key: '', base: null, at: 0, timer: 0, flagged: '' });
    if (!c) { stopClock(root); return; }
    const key = JSON.stringify(c);
    if (key !== st.key) { st.key = key; st.base = c; st.at = performance.now(); }
    for (const [anchor, pos, seat] of [[topAnchor, 'top', topSeat], [bottomAnchor, 'bottom', 1 - topSeat]]) {
      let el = root.querySelector(':scope > .rv-clock.' + pos);
      if (!el) {
        el = document.createElement('div');
        el.className = 'rv-clock ' + pos;
        anchor.insertAdjacentElement(pos === 'top' ? 'beforebegin' : 'afterend', el);
      }
      el.dataset.seat = String(seat);
    }
    tickClock(root, ctx);
    if (ctx.playing && c.running != null) { if (!st.timer) st.timer = setInterval(() => { if (!document.hidden) tickClock(root, ctx); }, 200); }
    else if (st.timer) { clearInterval(st.timer); st.timer = 0; }
  }

  function tickClock(root, ctx) {
    const st = root._clk;
    if (!st || !st.base) return;
    const c = st.base;
    const now = performance.now();
    root.querySelectorAll(':scope > .rv-clock').forEach((el) => {
      const seat = +el.dataset.seat;
      const run = ctx.playing && c.running === seat;
      const left = (c.ms[seat] || 0) - (run ? now - st.at : 0);
      setHtml(el, '<span class="who">' + DOT[colorOfSeat(ctx, seat)] + ' ' + ctx.esc(whoName(ctx, seat))
        + (seat === ctx.seat ? ' <i>(ти)</i>' : '') + '</span><b>' + fmtMs(left) + '</b>');
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
      ctx.act('flag').then((r) => { if (r && !r.ok && root._clk) setTimeout(() => { if (root._clk) root._clk.flagged = ''; }, 1000); });
    }, seat === ctx.seat ? 2500 : 350);
  }

  function stopClock(root) {
    if (root._clk) clearInterval(root._clk.timer);
    root._clk = null;
    root.querySelectorAll(':scope > .rv-clock').forEach((el) => el.remove());
  }

  function seriesHtml(ctx) {
    const s = (ctx.view || {}).series;
    if (!s || !s.wins) return '';
    const parts = [0, 1].map((i) => ctx.esc(ctx.nickOf(i) || ctx.seatName(i)) + ' <b>' + (s.wins[i] || 0) + '</b>');
    return '<span class="rv-serie" title="Скільки партій виграв кожен за цим столом">Серія: ' + parts.join(' : ')
      + (s.draws ? ' · нічиїх <b>' + s.draws + '</b>' : '') + '</span>';
  }

  // ---- модуль ---------------------------------------------------------------------------------------

  function update(root, ctx) {
    paint(root, ctx);
    glekPrefs(root, ctx);
    const st = state(root);
    clearTimeout(st.t);
    // Коли переворот доіграв — перемалювати без класів анімації, щоб наступний вид її не повторив.
    if (st.anim) st.t = setTimeout(() => { if (root._rv) paint(root, root._rv.ctx); }, Math.max(0, st.animUntil - performance.now()) + 10);
  }

  const MOD = {
    id: 'reversi',
    icon: ICON,
    seatClass: ['d', 'c'],
    added: '2026-09-30',
    mount(root, ctx) { update(root, ctx); },
    update,
    unmount(root) { if (root._rv) clearTimeout(root._rv.t); root._rv = null; stopClock(root); },

    onKey(e, ctx) {
      const root = ctx._rvRoot;
      if (!root || !root._rv || !ctx.mine || !ctx.playing) return false;
      const st = root._rv;
      const k = e.key;
      const d = { ArrowUp: -8, ArrowDown: 8, ArrowLeft: -1, ArrowRight: 1 }[k];
      if (d != null) {
        if (!st.kbd) {
          st.kbd = true;
          const lg = ctx.myTurn ? (ctx.view || {}).legal || [] : [];
          if (lg.length && lg.indexOf(st.cur) < 0) st.cur = lg[0];
        } else {
          const r = st.cur >> 3, c = st.cur & 7;
          if (d === -1 && c > 0) st.cur--;
          else if (d === 1 && c < 7) st.cur++;
          else if (d === -8 && r > 0) st.cur -= 8;
          else if (d === 8 && r < 7) st.cur += 8;
        }
        st.hover = -1;
        paint(root, ctx);
        return true;
      }
      if (k === 'Enter' || k === ' ' || e.code === 'Space') {
        const t = e.target;
        if (t && t.closest && t.closest('button, summary, a, input, textarea, [role=button]') && !t.closest('.board.rv')) return false;
        if (!st.kbd) { st.kbd = true; paint(root, ctx); return true; }
        if (!ctx.myTurn) return true;
        if (((ctx.view || {}).legal || []).indexOf(st.cur) < 0) { ctx.toast('Сюди не можна — нічого не перевернеться'); return true; }
        move(root, ctx, st.cur);
        return true;
      }
      if (k === 'Escape' && st.sure) { st.sure = ''; paint(root, ctx); return true; }
      return false;
    },

    pad: { dirs: true, a: 'Enter', hint: '{dpad} клітинка · {a} поставити' },

    status(ctx) {
      const v = ctx.view || {};
      if (!ctx.playing || !v.toMove) return '';
      const c = v.toMove;
      let s;
      if (kindOf(ctx) === 'glek') {
        s = v.glek && v.glek.thinking ? '🤖 Глек думає…' : 'Твій хід ' + DOT[c];
        if (v.pass === 1) s += ' — Глек пасує';
        else if (v.pass === 0) s += ' — ти пасуєш, ходити нікуди';
        return s;
      }
      const seat = seatOfColor(ctx, c);
      s = ctx.myTurn ? 'Твій хід ' + DOT[c] : 'Ходить ' + ctx.esc(whoName(ctx, seat)) + ' ' + DOT[c];
      if (v.pass != null) s += ' — ' + (ctx.mine && v.pass === ctx.seat ? 'ти пасуєш' : ctx.esc(whoName(ctx, v.pass)) + ' пасує') + ', ходити нікуди';
      return s;
    },
  };
  HGames.register(MOD);
  // Той самий модуль малює соло з Глеком (Client: "reversi"): каркас шукає модуль за Id гри.
  HGames.register(Object.assign({}, MOD, { id: 'reversi-glek' }));
})();
