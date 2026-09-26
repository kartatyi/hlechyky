/*
  «Під глеком» — брехливі кості (Perudo) на 2–6. Правила, час і перемога живуть на сервері (Impl/Dice.cs,
  Impl/DiceCore.cs); модуль лише малює те, що прийшло, і шле наміри. Гра покрокова, тож малюємо DOM+CSS без
  канвасу й без свого циклу кадрів: update() перемальовує лише ті блоки, чий HTML справді змінився.

  Вид (подія 'room', свій кожному місцю — гра Hidden; чужі грані є лише в reveal.dice):
    { turn, phase: 'shake'|'bid'|'reveal'|'done', round, endsAt, phaseMs,
      rules: { dice, turnMs, exact, palifico }, palifico, wild, starter, total,
      players: [{ seat, nick, dice, alive, left, palificoUsed, wasAtOne }],
      my: number[]|null, bid: { seat, q, f, auto }|null, history: [{ seat, q, f, auto }],
      canExact, ready: number[], note: string|null,
      reveal: null | { kind: 'liar'|'exact'|'timeout', caller, bid, count, jokers, dice: number[][6],
                       loser, gainer, out, next, say },
      result: null | { winner, places, rounds, say } }
  Кадр { ph, turn, endsAt, n } летить лише разом із видами — модуль його не читає.

  Наміри: act('bid', { q, f }), act('liar', { q, f }), act('exact', { q, f }), act('ready').
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.5 9.5h6l-.6 4.5H3.1z" fill="var(--clay)"/>'
    + '<path d="M3.2 9.5c0-2.4 1-3.6 2.3-3.6s2.3 1.2 2.3 3.6" fill="none" stroke="var(--clay)" stroke-width="1.3"/>'
    + '<rect x="9" y="7.5" width="5.5" height="5.5" rx="1.2" fill="none" stroke="var(--accent)" stroke-width="1.3"/>'
    + '<circle cx="10.6" cy="9.1" r=".75" fill="var(--accent)"/><circle cx="12.9" cy="9.1" r=".75" fill="var(--accent)"/>'
    + '<circle cx="11.75" cy="10.25" r=".75" fill="var(--accent)"/>'
    + '<circle cx="10.6" cy="11.4" r=".75" fill="var(--accent)"/><circle cx="12.9" cy="11.4" r=".75" fill="var(--accent)"/></svg>';

  const SEATS = ['перший', 'другий', 'третій', 'четвертий', 'п’ятий', 'шостий'];
  // Позначка місця — і колір, і форма: гравців розрізнить і той, хто кольорів не бачить.
  const MARKS = ['●', '▲', '■', '◆', '★', '✚'];
  const GLYPH = ['', '⚀', '⚁', '⚂', '⚃', '⚄', '⚅'];
  /// Захист від подвійного натиску: палець, що добивав «+», не має сказати «Брешеш!».
  const GUARD_MS = 600;
  /// Кісточка падає через стільки після підняття глеків (dice.css — той самий delay).
  const FALL_MS = 1200;

  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : v; } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* приватне вікно — не біда */ } },
  };
  const hintOn = () => store.get('dice_hint', '1') !== '0';
  const soundOn = () => store.get('dice_sound', '0') === '1';

  // =============================================================================================
  // Правила й імовірність
  // =============================================================================================

  /// Найменша законна кількість для грані f після ставки prev; 0 — цю грань зараз не можна.
  /// Дублікат DiceCore.MinQ (сервер — суддя; збіг тримає знімок tests/…/DiceRuleTable.json і qa-скрипт).
  function minQ(prev, f, pal, myDice) {
    if (!(f >= 1 && f <= 6)) return 0;
    if (!prev) return !pal && f === 1 ? 0 : 1;
    if (pal) {
      if (myDice > 1) return f === prev.f ? prev.q + 1 : 0;
      return f > prev.f ? prev.q : prev.q + 1;
    }
    if (prev.f !== 1) return f === 1 ? Math.floor((prev.q + 1) / 2) : f > prev.f ? prev.q : prev.q + 1;
    return f === 1 ? prev.q + 1 : 2 * prev.q + 1;
  }

  /// Біноміальна: P(X ≥ m) або P(X = m) для X ~ Bin(n, p). n ≤ 30 — цикл миттєвий.
  function binom(n, p, m, exact) {
    if (m < 0) m = 0;
    if (m > n) return 0;
    if (!exact && m === 0) return 1;
    let term = Math.pow(1 - p, n), sum = 0;
    const r = p / (1 - p);
    for (let k = 0; k <= n; k++) {
      if (exact ? k === m : k >= m) sum += term;
      term = term * (n - k) / (k + 1) * r;
    }
    return Math.min(1, sum);
  }

  /// Шанс, що на столі щонайменше (або рівно) q кісточок грані f — з моїми кісточками в руці.
  function chance(v, q, f, exact) {
    const my = v.my || [];
    const wild = v.wild && f !== 1;
    let k = 0;
    for (let i = 0; i < my.length; i++) if (my[i] === f || (wild && my[i] === 1)) k++;
    const unknown = Math.max(0, (v.total || 0) - my.length);
    return { p: binom(unknown, wild ? 1 / 3 : 1 / 6, q - k, exact), sure: !exact && q - k <= 0 };
  }

  const pct = (p) => (p >= 0.995 ? '> 99 %' : p > 0 && p < 0.01 ? '< 1 %' : '≈ ' + Math.round(p * 100) + ' %');

  function plural(n, one, few, many) {
    const d = n % 10, h = n % 100;
    return d === 1 && h !== 11 ? one : d >= 2 && d <= 4 && (h < 12 || h > 14) ? few : many;
  }

  // =============================================================================================
  // Малюнки
  // =============================================================================================

  const PIP9 = '<i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i>';
  /// Кісточка: дев'ять клітинок 3×3, які крапки видно — вирішує клас грані (dice.css). Грань 1 — глечик.
  const die = (f, cls, style) => '<span class="di-die f' + f + (cls ? ' ' + cls : '') + '"'
    + (style ? ' style="' + style + '"' : '') + '>' + PIP9 + '</span>';
  const bidHtml = (b, cls) => '<b class="di-q">' + b.q + '</b><span class="di-x">×</span>' + die(b.f, cls || 'sm');

  /// Перевернутий глечик — той, під яким трусять. Малюємо одним SVG і для чіпів, і для свого глека.
  const CUP = '<svg class="di-cupsvg" viewBox="0 0 64 70" aria-hidden="true">'
    + '<path class="di-cb" d="M22 4C10 10 4 22 8 32c3 10 12 16 15 22l-4 8c0 3 2 4 5 4h16c3 0 5-1 5-4l-4-8c3-6 12-12 15-22 4-10-2-22-14-28z"/>'
    + '<path class="di-cg" d="M9 26c7 3 15 4 23 4s16-1 23-4" />'
    + '<path class="di-cz" d="M11 33l5-4 5 4 5-4 6 4 6-4 5 4 5-4 5 4"/>'
    + '<ellipse class="di-cl" cx="32" cy="63" rx="12" ry="2.6"/></svg>';

  function cup(cls) { return '<span class="di-cup' + (cls ? ' ' + cls : '') + '">' + CUP + '</span>'; }

  function nameOf(ctx, v, seat) {
    const p = (v.players || []).find((x) => x.seat === seat);
    return (p && p.nick) || ctx.nickOf(seat) || SEATS[seat] || ('гравець ' + (seat + 1));
  }

  const nick = (ctx, v, seat) => '<span class="di-nk s' + seat + '"><u>' + MARKS[seat] + '</u>' + ctx.esc(nameOf(ctx, v, seat)) + '</span>';

  // =============================================================================================
  // Стан модуля
  // =============================================================================================

  function state(root) {
    if (!root._dice) {
      root._dice = {
        f: 2, q: 1, key: '', guardUntil: 0, guardTimer: 0, busy: false, lockKey: '', lockTimer: 0,
        prev: { phase: '', round: -1, hist: -1, reveal: '' }, timers: [], rep: null, lastEnds: '', arcUntil: '',
      };
    }
    return root._dice;
  }

  function setHtml(el, html) {
    if (el && el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
  }

  const alive = (v, seat) => seat != null && (v.players || []).some((p) => p.seat === seat && p.alive);
  const lobby = (ctx) => !ctx.playing && !(ctx.room && ctx.room.status === 'finished');

  /// Скелет ставимо раз: далі кожна частина перемальовується окремо, щоб клік не гасив :hover сусідів.
  function skeleton(root) {
    let el = root.querySelector(':scope > .dice');
    if (el) return el;
    el = document.createElement('div');
    el.className = 'dice';
    const faces = [1, 2, 3, 4, 5, 6].map((f) => '<button type="button" class="di-face" data-f="' + f + '"'
      + ' aria-label="' + (f === 1 ? 'глечики' : 'грань ' + f) + '">' + die(f) + '<small></small></button>').join('');
    el.innerHTML = '<div class="di-lobby" hidden></div>'
      + '<div class="di-table">'
      + '<div class="di-others"></div>'
      + '<div class="di-center"><div class="di-arcbox"></div><div class="di-bid"></div></div>'
      + '<div class="di-pal" hidden></div>'
      + '<div class="di-hist"></div>'
      + '<div class="di-reveal" hidden></div>'
      + '<div class="di-win" hidden></div>'
      + '<div class="di-bottom">'
      + '<div class="di-me"></div>'
      + '<div class="di-act" hidden>'
      + '<div class="di-faces">' + faces + '</div>'
      + '<div class="di-qrow"><button type="button" class="di-minus" aria-label="менше">−</button>'
      + '<b class="di-qv">1</b><button type="button" class="di-plus" aria-label="більше">+</button>'
      + '<span class="di-qp"></span></div>'
      + '<button type="button" class="primary di-go" data-pad-first></button>'
      + '<div class="di-row2"><button type="button" class="danger di-liar">Брешеш!</button>'
      + '<button type="button" class="ghost di-exact">Точно!</button></div>'
      + '</div>'
      + '</div>'
      + '</div>'
      + '<div class="di-foot"></div>';
    root.appendChild(el);
    return el;
  }

  // =============================================================================================
  // Блоки
  // =============================================================================================

  /// Гравці по колу, починаючи з мене (глядачеві — з першого місця): так «наступний» справді праворуч.
  function ring(ctx, v) {
    const list = (v.players || []).slice().sort((a, b) => a.seat - b.seat);
    const me = list.findIndex((p) => p.seat === ctx.seat);
    return me > 0 ? list.slice(me).concat(list.slice(0, me)) : list;
  }

  function lastBidOf(v, seat) {
    const h = v.history || [];
    for (let i = h.length - 1; i >= 0; i--) if (h[i].seat === seat) return h[i];
    return null;
  }

  function paintOthers(el, ctx, v) {
    const phase = v.phase;
    const res = v.result;
    const lift = phase === 'reveal' || phase === 'done';
    const html = ring(ctx, v).map((p) => {
      const cls = ['di-p', 's' + p.seat];
      if (p.seat === ctx.seat) cls.push('me');
      if (!p.alive) cls.push('out');
      if (phase === 'bid' && v.turn === p.seat) cls.push('turn');
      if (res && res.winner === p.seat) cls.push('champ');
      const last = phase === 'bid' || phase === 'reveal' ? lastBidOf(v, p.seat) : null;
      const n = Math.max(0, p.dice | 0);
      const body = p.alive
        ? cup(lift ? 'lift' : phase === 'shake' ? 'shaking' : '') + '<span class="di-backs">' + '<i></i>'.repeat(n) + '</span><b>' + n + '</b>'
        : '<em>' + (p.left ? 'встав' : '✕ вибув') + '</em>';
      const bubble = last
        ? '<span class="di-pb' + (last.auto ? ' auto' : '') + '">' + (last.auto ? '⏰' : '') + bidHtml(last, 'xs') + '</span>'
        : '<span class="di-pb none"></span>';
      return '<div class="' + cls.join(' ') + '">'
        + '<span class="di-pn"><u>' + MARKS[p.seat] + '</u>' + ctx.esc(nameOf(ctx, v, p.seat))
        + (p.seat === ctx.seat ? '<small>ти</small>' : '') + '</span>'
        + '<span class="di-pc">' + body + '</span>' + bubble + '</div>';
    }).join('');
    setHtml(el, html);
  }

  function probLine(v, ctx, q, f) {
    if (!hintOn() || !ctx.mine || !alive(v, ctx.seat) || !v.my) return '';
    const c = chance(v, q, f, false);
    return c.sure ? 'є напевно — бачу свої' : pct(c.p) + ', що є';
  }

  function paintBid(el, ctx, v) {
    let html = '';
    const phase = v.phase;
    if (phase === 'shake') {
      html = '<div class="di-note">' + ctx.esc(v.note || 'Трусимо глеки…') + '</div>'
        + '<div class="di-sub muted">🎲 Трусимо глеки…</div>';
    } else if (phase === 'bid' && !v.bid) {
      const mine = v.turn === ctx.seat;
      html = '<div class="di-note">' + (mine ? 'Твій хід — відкривай раунд' : 'Ставок ще нема — починає ' + nick(ctx, v, v.turn)) + '</div>'
        + '<div class="di-sub muted">На всьому столі ' + v.total + ' ' + plural(v.total, 'кісточка', 'кісточки', 'кісточок') + '</div>';
    } else if (v.bid) {
      const b = (phase === 'reveal' || phase === 'done') && v.reveal ? v.reveal.bid : v.bid;
      const live = phase === 'bid';
      const p = live ? probLine(v, ctx, b.q, b.f) : '';
      html = '<div class="di-who">' + nick(ctx, v, b.seat) + (b.auto ? ' <span class="muted">⏰ за нього годинник</span>' : ' каже:') + '</div>'
        + '<div class="di-val' + (live ? ' pop' : '') + '">' + bidHtml(b, 'md') + '</div>'
        + (p ? '<div class="di-prob">' + p + '</div>' : '')
        + (live ? '' : '<div class="di-sub muted">на столі тепер ' + v.total + ' ' + plural(v.total, 'кісточка', 'кісточки', 'кісточок') + '</div>');
    } else if (phase === 'done') {
      html = '<div class="di-note">Партію зіграно</div>';
    }
    setHtml(el, html);
  }

  function paintHist(el, ctx, v) {
    const h = v.phase === 'bid' || v.phase === 'reveal' ? (v.history || []) : [];
    const n = h.length;
    const html = n < 2 ? '' : h.map((b, i) => '<span class="di-h s' + b.seat + (i === n - 1 ? ' new' : '') + (b.auto ? ' auto' : '')
      + '" style="--age:' + Math.min(6, n - 1 - i) + '"><u>' + MARKS[b.seat] + '</u>' + (b.auto ? '⏰' : '') + bidHtml(b, 'xs') + '</span>').join('<i class="di-arrow">›</i>');
    setHtml(el, html);
  }

  function paintPal(el, ctx, v) {
    const on = v.palifico && (v.phase === 'shake' || v.phase === 'bid');
    let html = '';
    if (on) {
      const me = (v.players || []).find((p) => p.seat === ctx.seat);
      const tail = me && me.alive ? (me.dice > 1 ? ' У тебе більше однієї — грань не міняєш, лише кількість.' : ' У тебе одна — грань міняй як хочеш.') : '';
      html = '🏺 <b>Паліфіко</b>: глечики — звичайні одиниці, не джокери.' + tail;
    }
    el.hidden = !on;
    setHtml(el, html);
  }

  function counts(v, d, f) {
    return d === f || (v.wild && f !== 1 && d === 1);
  }

  function paintReveal(el, ctx, v) {
    const r = v.reveal;
    const show = !!r && (v.phase === 'reveal' || v.phase === 'done');
    el.hidden = !show;
    if (!show) { setHtml(el, ''); return; }
    const b = r.bid;
    const what = r.kind === 'exact' ? '«Точно!»' : r.kind === 'timeout' ? '⏰ час вийшов — «Брешеш!»' : '«Брешеш!»';
    const head = '<div class="di-rhead">' + nick(ctx, v, r.caller) + ': <b>' + what + '</b> на ' + nick(ctx, v, b.seat)
      + ' ' + bidHtml(b, 'xs') + '</div>';
    const rows = [];
    let row = 0;
    for (let s = 0; s < 6; s++) {
      const hand = (r.dice || [])[s] || [];
      if (!hand.length) continue;
      const dice = hand.map((d, i) => {
        let cls = counts(v, d, b.f) ? 'hit' : 'miss';
        if (d === 1 && !v.wild) cls += ' plain';
        if (r.loser === s && i === hand.length - 1) cls += ' fall';
        return die(d, cls);
      }).join('') + (r.gainer === s ? die(0, 'back rise') : '');
      rows.push('<div class="di-row s' + s + (r.loser === s ? ' lose' : '') + (r.gainer === s ? ' gain' : '') + '" style="--n:' + row++ + '">'
        + nick(ctx, v, s) + '<span class="di-rdice">' + dice + '</span></div>');
    }
    const jugs = r.jokers ? ' <span class="muted">(з них ' + r.jokers + ' ' + plural(r.jokers, 'глечик', 'глечики', 'глечиків') + ')</span>' : '';
    const sum = '<div class="di-rsum">На столі <b class="di-big">' + r.count + '</b><span class="di-x">×</span>' + die(b.f, 'sm')
      + jugs + ' — ставили <b>' + b.q + '</b> ' + (r.kind === 'exact' ? (r.count === b.q ? '✓ рівно' : '✗ не рівно') : r.count >= b.q ? '✓ правда' : '✗ брехня') + '</div>';
    let verdict = '';
    if (r.loser != null) verdict = nick(ctx, v, r.loser) + ' губить кісточку' + (r.out ? ' — і вибуває' : '');
    if (r.gainer != null) verdict = nick(ctx, v, r.gainer) + ' повертає кісточку 🎯';
    const say = r.say ? '<div class="di-say"><img src="/static/glek.svg" alt=""><span>' + ctx.esc(r.say) + '</span></div>' : '';
    let next = '';
    if (v.phase === 'reveal' && ctx.playing && ctx.mine && alive(v, ctx.seat)) {
      const living = (v.players || []).filter((p) => p.alive).length;
      const ready = (v.ready || []).length;
      const mine = (v.ready || []).indexOf(ctx.seat) >= 0;
      next = '<button type="button" class="primary di-next"' + (mine ? ' disabled' : '') + '>Далі ▸ ' + ready + '/' + living + '</button>';
    }
    setHtml(el, head + '<div class="di-rows">' + rows.join('') + '</div>' + sum
      + (verdict ? '<div class="di-verdict">' + verdict + '</div>' : '') + say + next);
  }

  function paintWin(el, ctx, v) {
    const res = v.result;
    const show = !!res && !!ctx.room && ctx.room.status === 'finished';
    el.hidden = !show;
    if (!show) { setHtml(el, ''); return; }
    const podium = [res.winner].concat((res.places || []).slice().reverse());
    const medals = ['🥇', '🥈', '🥉'];
    const list = podium.map((s, i) => '<li class="s' + s + '">' + (medals[i] || (i + 1) + '.') + ' ' + nick(ctx, v, s) + '</li>').join('');
    setHtml(el, '<div class="di-champ">🏆 ' + nick(ctx, v, res.winner) + '</div>'
      + (res.say ? '<div class="di-say"><img src="/static/glek.svg" alt=""><span>' + ctx.esc(res.say) + '</span></div>' : '')
      + '<ol class="di-podium">' + list + '</ol>'
      + '<div class="muted small">' + res.rounds + ' ' + plural(res.rounds, 'раунд', 'раунди', 'раундів') + '</div>');
  }

  function paintMe(el, ctx, v) {
    let html;
    const my = v.my;
    if (!ctx.mine || my == null) {
      html = '<div class="di-melabel muted">👁 Дивишся збоку — кісточки видно лише після розкриття</div>';
    } else if (!my.length) {
      html = '<div class="di-melabel muted">Ти без кісточок — дивись і вболівай</div>';
    } else {
      const phase = v.phase;
      const r = v.reveal;
      const shaking = phase === 'shake';
      const lifted = phase === 'reveal' || phase === 'done';
      const dice = my.map((d, i) => {
        let cls = shaking ? 'tumble' : '';
        if (lifted && r) {
          cls += counts(v, d, r.bid.f) ? ' hit' : ' miss';
          if (r.loser === ctx.seat && i === my.length - 1) cls += ' fall';
        }
        return die(d, cls.trim(), '--i:' + i);
      }).join('') + (lifted && r && r.gainer === ctx.seat ? die(0, 'back rise') : '');
      const label = shaking ? 'Трусимо…' : 'Твої кісточки · ' + my.length + (v.palifico && my.length === 1 ? ' · паліфіко' : '');
      html = '<div class="di-mecup">' + cup(shaking ? 'shaking big' : lifted ? 'lift big' : 'peek big') + '</div>'
        + '<div class="di-mine">' + dice + '</div>'
        + '<div class="di-melabel">' + label + '</div>';
    }
    setHtml(el, html);
  }

  function paintLobby(el, ctx) {
    const o = (ctx.room && ctx.room.options) || {};
    const n = o.dice === '3' ? 'три' : 'п’ять';
    const bits = ['⏱ ' + (o.turn || '30') + ' с на хід'];
    bits.push(o.exact === 'off' ? 'без «Точно!»' : '🎯 «Точно!» є');
    bits.push(o.palifico === 'off' ? 'без паліфіко' : '🏺 паліфіко є');
    setHtml(el, '<div class="di-how">'
      + '<div class="di-howcup">' + cup('shaking big') + '<span class="di-mine">' + die(1) + die(3) + die(5) + '</span></div>'
      + '<ol>'
      + '<li>У кожного ' + n + ' кісточок під глеком — бачиш лише свої.</li>'
      + '<li>По колу кажи, скільки кісточок із такою гранню на <b>всьому</b> столі. Кожна ставка — вища за попередню.</li>'
      + '<li>' + die(1, 'xs') + ' Глечики — джокери. На глечики — від половини, з глечиків — удвоє плюс один.</li>'
      + '<li>Не віриш — «Брешеш!». Глеки догори: хто помилився — губить кісточку. Останній із кісточками виграє.</li>'
      + '</ol>'
      + '<div class="di-rules muted small">' + bits.join(' · ') + '</div>'
      + '<div class="gwait">Господар тисне «Почати»</div></div>');
  }

  function paintFoot(el, ctx) {
    const html = '<button type="button" class="ghost di-tg" data-tg="hint" aria-pressed="' + hintOn() + '">🎲 підказка: '
      + (hintOn() ? 'увімк' : 'вимк') + '</button>'
      + '<button type="button" class="ghost di-tg" data-tg="sound" aria-pressed="' + soundOn() + '">' + (soundOn() ? '🔊' : '🔈')
      + ' звук: ' + (soundOn() ? 'увімк' : 'вимк') + '</button>';
    setHtml(el, html);
  }

  // =============================================================================================
  // Конструктор ставки
  // =============================================================================================

  function myDice(v, ctx) {
    const p = (v.players || []).find((x) => x.seat === ctx.seat);
    return p ? p.dice : 0;
  }

  /// Найнижча законна ставка на обраній грані; не лізе в стіл — найнижча серед граней.
  function lowestFor(v, ctx, prefer) {
    const md = myDice(v, ctx);
    const pick = (f) => { const m = minQ(v.bid, f, v.palifico, md); return m > 0 && m <= v.total ? m : 0; };
    if (prefer && pick(prefer)) return { f: prefer, q: pick(prefer) };
    let best = null;
    for (const f of [2, 3, 4, 5, 6, 1]) {
      const m = pick(f);
      if (m && (!best || m < best.q)) best = { f, q: m };
    }
    return best;
  }

  function canBidNow(ctx, v, st) {
    return ctx.playing && ctx.mine && v.phase === 'bid' && v.turn === ctx.seat && !st.busy && st.lockKey !== st.key;
  }

  function guarded(st) { return performance.now() < st.guardUntil; }

  function paintAct(box, ctx, v, st) {
    const show = ctx.playing && ctx.mine && v.phase === 'bid' && alive(v, ctx.seat);
    box.hidden = !show;
    if (!show) return;
    const md = myDice(v, ctx);
    const mine = canBidNow(ctx, v, st);
    const faces = box.querySelectorAll('.di-face');
    for (let i = 0; i < faces.length; i++) {
      const b = faces[i];
      const f = i + 1;
      const m = minQ(v.bid, f, v.palifico, md);
      const ok = m > 0 && m <= v.total;
      b.disabled = !mine || !ok;
      b.classList.toggle('on', st.f === f);
      b.setAttribute('aria-pressed', String(st.f === f));
      const t = ok ? 'від ' + m : '—';
      const small = b.querySelector('small');
      const label = f === 1 ? (ok ? 'глечики ' + m + '+' : 'глечики') : t;
      if (small.textContent !== label) small.textContent = label;
    }
    const m = minQ(v.bid, st.f, v.palifico, md);
    const legal = m > 0 && st.q >= m && st.q <= v.total;
    box.querySelector('.di-qv').textContent = String(st.q);
    box.querySelector('.di-minus').disabled = !mine || st.q <= Math.max(1, m);
    box.querySelector('.di-plus').disabled = !mine || st.q >= v.total;
    const go = box.querySelector('.di-go');
    go.disabled = !mine || !legal;
    const goHtml = st.busy && st.sent === 'bid' ? '…' : 'Ставлю ' + bidHtml({ q: st.q, f: st.f }, 'sm');
    if (go.dataset.sig !== goHtml) { go.dataset.sig = goHtml; go.innerHTML = goHtml; }
    const qp = box.querySelector('.di-qp');
    const pt = mine && hintOn() ? probText(v, st) : '';
    if (qp.textContent !== pt) qp.textContent = pt;
    const g = guarded(st);
    const liar = box.querySelector('.di-liar');
    liar.disabled = !mine || !v.bid || g;
    liar.textContent = st.busy && st.sent === 'liar' ? '…' : 'Брешеш!';
    const ex = box.querySelector('.di-exact');
    const exactRule = !v.rules || v.rules.exact !== false;
    ex.hidden = !exactRule;
    ex.disabled = !v.canExact || g || st.busy;
    ex.textContent = st.busy && st.sent === 'exact' ? '…' : 'Точно!';
    ex.title = v.bid && v.canExact && hintOn() ? 'Рівно ' + v.bid.q + ': ' + pct(chance(v, v.bid.q, v.bid.f, true).p)
      : v.bid && exactRule && md >= ((v.rules && v.rules.dice) || 5) ? 'З повним глеком «Точно!» нічого не дасть' : '';
    box.classList.toggle('wait', !mine);
  }

  function probText(v, st) {
    const c = chance(v, st.q, st.f, false);
    return c.sure ? 'напевно' : pct(c.p);
  }

  /// Моя черга щойно настала (або змінилась ставка): конструктор — на найнижчу законну ставку.
  function prefill(ctx, v, st) {
    const want = v.bid ? v.bid.f : v.palifico ? 1 : 2;
    const best = lowestFor(v, ctx, want);
    if (best) { st.f = best.f; st.q = best.q; } else if (v.bid) { st.f = v.bid.f; st.q = Math.min(v.total, v.bid.q); }
  }

  function pickFace(root, ctx, f) {
    const v = ctx.view || {};
    const st = state(root);
    if (!canBidNow(ctx, v, st)) return;
    const m = minQ(v.bid, f, v.palifico, myDice(v, ctx));
    if (!(m > 0 && m <= v.total)) return;
    st.f = f;
    if (st.q < m) st.q = m;
    paintAct(root.querySelector('.di-act'), ctx, v, st);
  }

  function stepFace(root, ctx, d) {
    const v = ctx.view || {};
    const st = state(root);
    if (!canBidNow(ctx, v, st)) return;
    const md = myDice(v, ctx);
    for (let k = 1; k <= 6; k++) {
      const f = ((st.f - 1 + d * k) % 6 + 6) % 6 + 1;
      const m = minQ(v.bid, f, v.palifico, md);
      if (m > 0 && m <= v.total) { pickFace(root, ctx, f); return; }
    }
  }

  function stepQ(root, ctx, d) {
    const v = ctx.view || {};
    const st = state(root);
    if (!canBidNow(ctx, v, st)) return;
    const m = Math.max(1, minQ(v.bid, st.f, v.palifico, myDice(v, ctx)));
    const q = Math.max(m, Math.min(v.total, st.q + d));
    if (q === st.q) return;
    st.q = q;
    paintAct(root.querySelector('.di-act'), ctx, v, st);
  }

  async function send(root, ctx, action, payload) {
    const st = state(root);
    if (st.busy) return;
    st.busy = true;
    st.sent = action;
    paintAct(root.querySelector('.di-act'), ctx, ctx.view || {}, st);
    let r = null;
    try { r = await ctx.act(action, payload); } catch { r = null; }
    st.busy = false;
    st.sent = '';
    // Прийнято — кнопки мовчать, поки не приїде свіжий вид (≤ 250 мс); не приїхав за 1,5 с — оживають самі.
    if (r && r.ok) {
      st.lockKey = st.key;
      clearTimeout(st.lockTimer);
      st.lockTimer = setTimeout(() => { st.lockKey = ''; if (root._dice) paint(root, ctx); }, 1500);
    }
    if (root._dice) paint(root, ctx);
  }

  function bid(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    if (!canBidNow(ctx, v, st)) return;
    const m = minQ(v.bid, st.f, v.palifico, myDice(v, ctx));
    if (!(m > 0 && st.q >= m && st.q <= v.total)) return;
    send(root, ctx, 'bid', { q: st.q, f: st.f });
  }

  function liar(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    if (!canBidNow(ctx, v, st) || !v.bid || guarded(st)) return;
    send(root, ctx, 'liar', { q: v.bid.q, f: v.bid.f });
  }

  function exact(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    if (!ctx.playing || !v.canExact || !v.bid || guarded(st) || st.busy) return;
    send(root, ctx, 'exact', { q: v.bid.q, f: v.bid.f });
  }

  function ready(root, ctx) {
    const v = ctx.view || {};
    if (!ctx.playing || v.phase !== 'reveal' || !alive(v, ctx.seat) || (v.ready || []).indexOf(ctx.seat) >= 0) return;
    send(root, ctx, 'ready');
  }

  function toggle(root, ctx, what) {
    if (what === 'hint') store.set('dice_hint', hintOn() ? '0' : '1');
    if (what === 'sound') {
      store.set('dice_sound', soundOn() ? '0' : '1');
      if (soundOn()) { audio(); sound(root, 'bid'); }
    }
    paint(root, ctx);
  }

  // =============================================================================================
  // Звук (WebAudio-синтез, тихо, лише після жесту — вмикається кліком по 🔊)
  // =============================================================================================

  let actx = null;
  function audio() {
    if (!actx) { try { actx = new (window.AudioContext || window.webkitAudioContext)(); } catch { actx = null; } }
    if (actx && actx.state === 'suspended') actx.resume().catch(() => {});
    return actx;
  }

  function sound(root, kind) {
    if (!soundOn() || !actx) return;
    const a = actx, t = a.currentTime, g = a.createGain();
    g.connect(a.destination);
    try {
      if (kind === 'shake') {
        const len = Math.floor(a.sampleRate * 0.3);
        const buf = a.createBuffer(1, len, a.sampleRate);
        const d = buf.getChannelData(0);
        for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * Math.abs(Math.sin(i / len * Math.PI * 4));
        const src = a.createBufferSource();
        const lp = a.createBiquadFilter();
        lp.type = 'lowpass';
        lp.frequency.value = 900;
        src.buffer = buf;
        src.connect(lp);
        lp.connect(g);
        g.gain.value = 0.12;
        src.start(t);
        return;
      }
      const o = a.createOscillator();
      o.connect(g);
      if (kind === 'bid') {
        o.frequency.value = 1200;
        g.gain.setValueAtTime(0.08, t);
        g.gain.exponentialRampToValueAtTime(0.001, t + 0.03);
        o.start(t); o.stop(t + 0.035);
      } else if (kind === 'whoosh') {
        o.frequency.setValueAtTime(200, t);
        o.frequency.exponentialRampToValueAtTime(60, t + 0.25);
        g.gain.setValueAtTime(0.14, t);
        g.gain.exponentialRampToValueAtTime(0.001, t + 0.25);
        o.start(t); o.stop(t + 0.26);
      } else if (kind === 'thud') {
        o.frequency.value = 90;
        g.gain.setValueAtTime(0.15, t);
        g.gain.exponentialRampToValueAtTime(0.001, t + 0.08);
        o.start(t); o.stop(t + 0.09);
      }
    } catch { /* звук — прикраса */ }
  }

  // =============================================================================================
  // Перемальовка
  // =============================================================================================

  /// Дуга ходу: сервер дає endsAt, але годинник клієнта може брехати. Щойно endsAt змінився на наших очах,
  /// фаза почалась щойно — рахуємо від приходу виду; інакше (F5) — від ISO, але в межах phaseMs.
  function arcUntil(st, v) {
    const now = Date.now();
    let until;
    if (st.lastEnds && st.lastEnds !== v.endsAt) until = now + (v.phaseMs || 0) - 120;
    else if (st.lastEnds === v.endsAt && st.arcUntil) return st.arcUntil;
    else until = now + Math.max(0, Math.min(v.phaseMs || 0, (Date.parse(v.endsAt) || now) - now));
    st.lastEnds = v.endsAt;
    st.arcUntil = new Date(until).toISOString();
    return st.arcUntil;
  }

  function paintArc(root, ctx, v, st) {
    const host = root.querySelector('.di-arcbox');
    const on = ctx.playing && (v.phase === 'bid' || v.phase === 'reveal') && v.endsAt;
    if (on) {
      HGames.ui.timerArc(host, arcUntil(st, v), v.phaseMs);
      host.classList.toggle('hurry', v.phase === 'bid' && v.turn === ctx.seat);
    } else {
      const arc = host.querySelector(':scope > .garc');
      if (arc) { if (arc._arc) arc._arc.stop(); arc.remove(); }
      st.lastEnds = v.endsAt || '';
      st.arcUntil = '';
    }
  }

  /// Події між видами: новий раунд, нова ставка, моя черга, розкриття — для префілу, захисту й звуків.
  function transitions(root, ctx, v, st) {
    const p = st.prev;
    const hist = (v.history || []).length;
    const revKey = v.reveal ? v.reveal.kind + ':' + v.reveal.caller + ':' + v.round : '';
    st.key = v.round + ':' + hist + ':' + v.phase + ':' + (v.turn === ctx.seat ? 1 : 0);
    const myTurn = ctx.playing && v.phase === 'bid' && v.turn === ctx.seat;
    const fresh = p.phase !== v.phase || p.round !== v.round || p.hist !== hist;
    if (fresh) {
      if (myTurn) prefill(ctx, v, st);
      // Нова ставка або моя черга: «Брешеш!»/«Точно!» мовчать 600 мс.
      if (v.phase === 'bid' && hist !== p.hist) {
        st.guardUntil = performance.now() + GUARD_MS;
        clearTimeout(st.guardTimer);
        st.guardTimer = setTimeout(() => { if (root._dice) paintAct(root.querySelector('.di-act'), ctx, ctx.view || {}, st); }, GUARD_MS + 20);
      }
      if (v.phase === 'shake' && p.phase !== 'shake') sound(root, 'shake');
      else if (v.phase === 'bid' && hist > p.hist && p.round === v.round) sound(root, 'bid');
    }
    if (revKey && revKey !== p.reveal && v.phase === 'reveal') {
      sound(root, 'whoosh');
      if (v.reveal.loser != null && !reduced()) st.timers.push(setTimeout(() => sound(root, 'thud'), FALL_MS));
      if (v.reveal.kind !== 'exact' && !reduced()) {
        const box = root.querySelector('.dice');
        box.classList.remove('di-shudder');
        void box.offsetWidth;   // рестарт анімації — раз на розкриття
        box.classList.add('di-shudder');
      }
    }
    st.prev = { phase: v.phase, round: v.round, hist, reveal: revKey || p.reveal };
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const box = skeleton(root);
    const idle = lobby(ctx);
    const lob = box.querySelector('.di-lobby');
    const table = box.querySelector('.di-table');
    lob.hidden = !idle;
    table.hidden = idle;
    if (idle) {
      // Після reopen у виді лежить минула партія — у лобі її не показуємо (грабля доміно).
      paintLobby(lob, ctx);
      paintArc(root, ctx, {}, st);
      paintFoot(box.querySelector('.di-foot'), ctx);
      return;
    }
    transitions(root, ctx, v, st);
    paintOthers(box.querySelector('.di-others'), ctx, v);
    paintArc(root, ctx, v, st);
    paintBid(box.querySelector('.di-bid'), ctx, v);
    paintPal(box.querySelector('.di-pal'), ctx, v);
    paintHist(box.querySelector('.di-hist'), ctx, v);
    paintReveal(box.querySelector('.di-reveal'), ctx, v);
    paintWin(box.querySelector('.di-win'), ctx, v);
    paintMe(box.querySelector('.di-me'), ctx, v);
    paintAct(box.querySelector('.di-act'), ctx, v, st);
    paintFoot(box.querySelector('.di-foot'), ctx);
    box.dataset.phase = v.phase || '';
  }

  // =============================================================================================
  // Керування
  // =============================================================================================

  const roots = new WeakMap();   // ctx → root: пад віддає лише ctx

  function wire(root, ctx) {
    const box = skeleton(root);
    box.addEventListener('click', (e) => {
      const t = e.target.closest('button');
      if (!t || t.disabled || !box.contains(t)) return;
      if (t.classList.contains('di-face')) pickFace(root, ctx, +t.dataset.f);
      else if (t.classList.contains('di-minus')) stepQ(root, ctx, -1);
      else if (t.classList.contains('di-plus')) stepQ(root, ctx, 1);
      else if (t.classList.contains('di-go')) bid(root, ctx);
      else if (t.classList.contains('di-liar')) liar(root, ctx);
      else if (t.classList.contains('di-exact')) exact(root, ctx);
      else if (t.classList.contains('di-next')) ready(root, ctx);
      else if (t.dataset.tg) toggle(root, ctx, t.dataset.tg);
    });
    // Колесо над кількістю: вгору — більше. Сторінку гортаємо лише тоді, коли колесо нічого не зробило.
    box.querySelector('.di-qrow').addEventListener('wheel', (e) => {
      const v = ctx.view || {};
      if (!canBidNow(ctx, v, state(root))) return;
      e.preventDefault();
      stepQ(root, ctx, e.deltaY < 0 ? 1 : -1);
    }, { passive: false });
    // Стік пада тримають, а шар пада шле один keydown на вхід у напрямок — кількість крутимо самі, поки тримають.
    const up = (e) => {
      const st = root._dice;
      if (st && st.rep && e.code === st.rep.code) { clearTimeout(st.rep.t); st.rep = null; }
    };
    document.addEventListener('keyup', up);
    root._diceUp = up;
  }

  function holdRepeat(root, ctx, e, d) {
    const st = state(root);
    if (!e.hpad || e.repeat) return;
    if (st.rep) clearTimeout(st.rep.t);
    const tick = (delay) => {
      st.rep = { code: e.code, t: setTimeout(() => { if (!root._dice || !st.rep) return; stepQ(root, ctx, d); tick(110); }, delay) };
    };
    tick(380);
  }

  function onKey(e, ctx) {
    const root = roots.get(ctx);
    if (!root) return false;
    const v = ctx.view || {};
    if (!ctx.playing || !ctx.mine || !alive(v, ctx.seat) || (v.phase !== 'bid' && v.phase !== 'reveal')) return false;
    const code = e.code || '';
    const key = e.key || '';
    if (v.phase === 'reveal') {
      if (code === 'Enter' || code === 'NumpadEnter' || code === 'Space') { if (!e.repeat) ready(root, ctx); return true; }
      return /^(Arrow|Digit|Numpad|Backspace|Key[WASD])/.test(code);
    }
    if (code === 'ArrowLeft' || code === 'KeyA') { stepFace(root, ctx, -1); return true; }
    if (code === 'ArrowRight' || code === 'KeyD') { stepFace(root, ctx, 1); return true; }
    if (code === 'ArrowUp' || code === 'KeyW' || code === 'Equal' || code === 'NumpadAdd') { stepQ(root, ctx, 1); holdRepeat(root, ctx, e, 1); return true; }
    if (code === 'ArrowDown' || code === 'KeyS' || code === 'Minus' || code === 'NumpadSubtract') { stepQ(root, ctx, -1); holdRepeat(root, ctx, e, -1); return true; }
    const digit = /^(?:Digit|Numpad)([1-6])$/.exec(code);
    if (digit) { pickFace(root, ctx, +digit[1]); return true; }
    if (code === 'Enter' || code === 'NumpadEnter' || code === 'Space') { if (!e.repeat) bid(root, ctx); return true; }
    if (code === 'Backspace' || /^[бБbB]$/.test(key)) { if (!e.repeat) liar(root, ctx); return true; }
    if (/^[тТtT]$/.test(key)) { if (!e.repeat) exact(root, ctx); return true; }
    if (code === 'KeyH') { toggle(root, ctx, 'hint'); return true; }
    return /^(Digit|Numpad)/.test(code);
  }

  HGames.register({
    id: 'dice',
    icon: ICON,
    seatNames: SEATS,
    // Чотири кольори каркаса + свої для четвертого, п'ятого й шостого (dice.css): сірий «d» каркаса
    // тут читався б як «вибув».
    seatClass: ['x', 'o', 'c', 'div', 'dib', 'dip'],
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Під глеком',
      items: [
        '🏺 У кожного п’ять кісточок під глеком — бачиш лише свої',
        '🗣 По колу кажи, скільки кісточок із такою гранню на всьому столі: кожна наступна ставка — вища',
        '⚀ Одиниці — глечики-джокери: рахуються за будь-яку грань. На глечики — від половини, з глечиків — удвоє плюс один',
        '🤥 Не віриш — тисни «Брешеш!»: глеки піднімаються, хто помилився — губить кісточку',
        '🎯 «Точно!» можна крикнути поза чергою: вгадав рівно — повернув кісточку. Хто лишився з кісточками — переміг',
      ],
    },
    pad: {
      dirs: true,
      a: 'Enter',
      x: 'Backspace',
      on(btn, ctx) {
        const root = roots.get(ctx);
        if (!root) return false;
        if (btn === 'y') { exact(root, ctx); return true; }
        if (btn === 'rb') { toggle(root, ctx, 'hint'); return true; }
        return false;
      },
      hint: '{dpad} грань і кількість · {a} ставка / далі · {x} Брешеш! · {y} Точно! · {rb} підказка',
      when: (ctx) => ctx.mine && ctx.playing && !!ctx.view && alive(ctx.view, ctx.seat)
        && (ctx.view.phase === 'bid' || ctx.view.phase === 'reveal'),
    },

    mount(root, ctx) {
      root._dice = null;
      roots.set(ctx, root);
      skeleton(root);
      wire(root, ctx);
      paint(root, ctx);
    },

    update(root, ctx) {
      roots.set(ctx, root);
      paint(root, ctx);
    },

    onKey,

    status(ctx) {
      if (!ctx.playing) return '';
      const v = ctx.view || {};
      if (v.phase === 'shake') return 'Трусимо глеки…';
      if (v.phase === 'reveal') return 'Глеки підняли!';
      if (v.phase !== 'bid') return '';
      if (!ctx.mine) return 'Дивишся збоку';
      if (!alive(v, ctx.seat)) return 'Ти без кісточок — дивись і вболівай';
      if (v.turn === ctx.seat) return 'Твій хід: став вище або кажи «Брешеш!»';
      return 'Думає ' + nameOf(ctx, v, v.turn) + '…';
    },

    unmount(root) {
      const st = root._dice;
      if (st) {
        clearTimeout(st.guardTimer);
        clearTimeout(st.lockTimer);
        if (st.rep) clearTimeout(st.rep.t);
        st.timers.forEach(clearTimeout);
      }
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      if (root._diceUp) document.removeEventListener('keyup', root._diceUp);
      root._diceUp = null;
      root._dice = null;
    },

    // Для живої перевірки (qa): те саме правило, що й на сервері, і біноміальна підказка.
    rules: { minQ, binom },
  });
})();
