/*
  Покер — техаський холдем без ліміту на 2–8. Правила, гроші й колода — на сервері (Impl/Poker.cs); тут лише стіл і наміри.
  Форма виду й дій — docs/games/specs/poker.md §4.5–4.6. Пастки:
    - view.turn — КІМНАТНЕ місце (для «Твій хід» каркаса); позиція за столом — view.turnPos і view.me;
    - карти на дроті — рядки «As», «Td»; raise { to } — сума «ДО» в цьому колі, не «на»;
    - встати — звичайний LeaveRoom каркаса (у кеші — з виплатою стеку).
  Анімації — лише CSS (переходи й ключові кадри на вставці), жодного rAF. Годинник шапки — таймер раз на секунду.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="8" cy="8" r="6.6" fill="var(--panel2)" stroke="var(--accent)" stroke-width="1.6" stroke-dasharray="2.6 1.85"/>'
    + '<circle cx="8" cy="8" r="3.9" fill="none" stroke="var(--muted)" stroke-width="1"/>'
    + '<path d="M8 5.6c1.1 1.2 2 1.9 2 2.8a1 1 0 0 1-1.8.6l.4 1.2H7.4l.4-1.2A1 1 0 0 1 6 8.4c0-.9.9-1.6 2-2.8Z" fill="var(--text)"/></svg>';

  const SUIT = { s: '♠', h: '♥', d: '♦', c: '♣' };
  const STREET = ['Префлоп', 'Флоп', 'Терн', 'Рівер', ''];
  const N = 8;
  const CODE = /^[2-9TJQKA][shdc]$/;
  const fmt = (n) => (+n || 0).toLocaleString('uk-UA');
  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  let active = null;   // корінь картки, де зараз мій хід: пад і клавіші діють сюди

  function card(code, cls) {
    const extra = cls ? ' ' + cls : '';
    if (!code || !CODE.test(code)) return '<span class="pkc back' + extra + '"></span>';
    const red = code[1] === 'h' || code[1] === 'd';
    return '<span class="pkc' + (red ? ' red' : '') + extra + '" data-c="' + code + '"><b>' + (code[0] === 'T' ? '10' : code[0])
      + '</b><i>' + SUIT[code[1]] + '</i></span>';
  }

  function setHtml(el, html) {
    if (el._sig !== html) { el._sig = html; el.innerHTML = html; return true; }
    return false;
  }

  function state(root) {
    if (!root._pk) root._pk = { bets: [], amt: 0, amtKey: '', pre: null, preHand: -1, lastShown: null, holeSig: [], busy: false, leaveArm: 0 };
    return root._pk;
  }

  function skeleton(root, ctx) {
    let el = root.querySelector(':scope > .pk');
    if (el) return el;
    el = document.createElement('div');
    el.className = 'pk';
    let seats = '';
    for (let p = 0; p < N; p++) {
      seats += '<div class="pk-seat" data-pos="' + p + '" hidden><div class="pk-hole"></div><div class="pk-box">'
        + '<span class="pk-arc"></span><span class="pk-dealer" hidden>D</span><span class="pk-blind" hidden></span>'
        + '<div class="pk-name"></div><div class="pk-stack"></div><div class="pk-tag"></div></div></div>'
        + '<div class="pk-bet" data-pos="' + p + '" hidden><i></i><b></b></div>';
    }
    el.innerHTML = '<div class="pk-top"></div>'
      + '<div class="pk-table"><div class="pk-felt"></div><div class="pk-ring">'
      + '<div class="pk-center"><div class="pk-pot"></div><div class="pk-board">'
      + '<span class="pk-slot"></span><span class="pk-slot"></span><span class="pk-slot"></span><span class="pk-slot"></span><span class="pk-slot"></span>'
      + '</div><div class="pk-msg" aria-live="polite"></div></div>' + seats + '</div></div>'
      + '<div class="pk-mine"><div class="pk-cards"></div><div class="pk-combo"></div></div>'
      + '<div class="pk-panel"></div><div class="pk-btns"></div>';
    root.appendChild(el);
    el.addEventListener('click', (e) => onClick(root, e));
    el.addEventListener('input', (e) => {
      if (!e.target.matches('.pk-range')) return;
      const st = state(root);
      st.amt = +e.target.value;
      showAmt(root);
    });
    el.addEventListener('change', (e) => {
      // після повзунка клавіші F/C/R/A мають знову йти грі: каркас не шле onKey, поки фокус у полі
      if (e.target.matches('.pk-range')) e.target.blur();
      if (e.target.matches('[data-pre]')) {
        const st = state(root);
        st.pre = e.target.checked ? e.target.dataset.pre : null;
        st.preHand = ((st.ctx || ctx).view || {}).hand;
        paintPanel(root, st.ctx || ctx);
      }
    });
    return el;
  }

  // ---------- розкладка: я внизу, решта за годинниковою, рівно по овалу ----------

  function layout(v) {
    const seats = v.seats || [];
    const base = v.me != null ? v.me : 0;
    const shown = [];
    for (let k = 0; k < N; k++) {
      const p = (base + k) % N;
      if (seats[p] && seats[p].state !== 'empty') shown.push(p);
    }
    if (v.me != null && shown[0] !== v.me) shown.unshift(v.me);
    const at = {};
    shown.forEach((p, k) => {
      // кут від низу (90°) за годинниковою на екрані (y донизу): низ → ліво → верх → право
      const a = Math.PI / 2 + k * 2 * Math.PI / shown.length;
      at[p] = { x: Math.cos(a), y: Math.sin(a), k };
    });
    return at;
  }

  const pct = (u) => (50 + 50 * u).toFixed(2) + '%';

  // ---------- малювання ----------

  function paint(root, ctx) {
    const st = state(root);
    st.ctx = ctx;
    const el = skeleton(root, ctx);
    const v = ctx.view;
    const lobby = !ctx.room || ctx.room.status === 'lobby' || !v || !v.seats;
    el.classList.toggle('lobby', lobby);
    if (lobby) { paintLobby(el, ctx); return; }
    const winSet = winning(v);
    paintTop(el, ctx, v);
    paintSeats(root, el, ctx, v, winSet);
    paintCenter(root, el, ctx, v, winSet);
    paintMine(el, ctx, v, winSet);
    paintPanel(root, ctx);
    paintBtns(root, el, ctx, v);
    runPre(root, ctx);
    active = v.actions ? root : (active === root ? null : active);
  }

  function paintLobby(el, ctx) {
    const o = (ctx.room && ctx.room.options) || {};
    const f = o.format || 'fun';
    const stakes = { 1: '1/2 · викуп 100', 2: '2/5 · викуп 250', 5: '5/10 · викуп 500' }[o.stakes || '1'];
    const line = f === 'cash' ? '💰 Кеш-стіл: сліпі ' + stakes + ' черепків. 1 фішка = 1 черепок, Глек нічого не бере.'
      : f === 'tour' ? '💰 Турнір на черепки: внесок ' + (o.buyin || 50) + ' з кожного, усі з 1000 фішок, банк внесків — переможцям.'
      : '🎲 Турнір на інтерес: усі з 1000 фішок, сліпі ростуть що ' + (o.pace === '3' ? '3' : '6') + ' хв.'
        + (+o.bots ? ' 🤖 Глек підсідає на порожні місця.' : '');
    setHtml(el.querySelector('.pk-top'), '<span class="pk-chip">' + ctx.esc(line) + '</span>');
    setHtml(el.querySelector('.pk-msg'), ctx.esc(ctx.mine ? 'Господар роздає, коли всі сядуть' : 'Сідай — і чекаємо роздачі'));
    for (const s of el.querySelectorAll('.pk-seat, .pk-bet')) s.hidden = true;
    setHtml(el.querySelector('.pk-pot'), '');
    el.querySelectorAll('.pk-slot').forEach((s) => setHtml(s, ''));
    setHtml(el.querySelector('.pk-cards'), '');
    setHtml(el.querySelector('.pk-combo'), '');
    setHtml(el.querySelector('.pk-panel'), '');
    setHtml(el.querySelector('.pk-btns'), '');
  }

  /// Виграшна п'ятірка останньої роздачі (лише поки її видно, до наступної): що підсвітити на дошці й у руках.
  function winning(v) {
    const last = v.last;
    if (v.live || !last || last.hand !== v.hand || !last.wins || !last.wins.length) return null;
    const w = last.wins.find((x) => x.cards && x.cards.length) || null;
    return w ? new Set(w.cards) : null;
  }

  function untilText(iso) {
    const ms = Date.parse(iso) - Date.now();
    if (!(ms > 0)) return '0:00';
    const s = Math.ceil(ms / 1000);
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  function paintTop(el, ctx, v) {
    const b = v.blinds || {};
    const chips = [];
    if (v.format === 'cash') {
      chips.push('💰 Кеш-стіл · сліпі <b>' + fmt(b.sb) + '/' + fmt(b.bb) + '</b>');
      const me = v.me != null ? v.seats[v.me] : null;
      if (me && v.cash) chips.push('за столом твоїх: <b>' + fmt(me.stack) + '</b> (вніс ' + fmt(v.cash.bought) + ')');
      else if (v.cash) chips.push('викуп ' + fmt(v.cash.buyin));
    } else {
      chips.push((v.format === 'tour' ? '💰 Турнір на черепки' : '🎲 Турнір') + ' · сліпі <b>' + fmt(b.sb) + '/' + fmt(b.bb) + '</b>'
        + (v.level ? ' · рівень ' + v.level : ''));
      if (v.nextBlinds && v.levelAt && !v.over)
        chips.push('далі ' + fmt(v.nextBlinds.sb) + '/' + fmt(v.nextBlinds.bb) + ' через <b data-until="' + ctx.esc(v.levelAt) + '">'
          + untilText(v.levelAt) + '</b>');
      if (v.tour) chips.push('банк внесків <b>' + fmt(v.tour.pool) + '</b> · ' + (v.tour.prizes.length > 1 ? 'призи ' : 'приз ') + v.tour.prizes.map(fmt).join(' / '));
    }
    if (v.hand) chips.push('роздача №' + v.hand);
    setHtml(el.querySelector('.pk-top'), chips.map((c) => '<span class="pk-chip">' + c + '</span>').join(''));
  }

  function tagOf(x, v) {
    if (x.gone) return '🚪 пішов';
    if (x.away) return '☕ відійшов';
    switch (x.state) {
      case 'fold': return 'пас';
      case 'allin': return 'олл-ін';
      case 'sitout': return 'не в роздачі';
      case 'wait': return 'з наступної';
      case 'out': return v.format === 'cash' ? 'без фішок' : 'вибув';
      case 'bust': return x.place ? 'вибув · ' + x.place + '-е' : 'вибув';
      case 'watch': return 'дивиться';
      default: return '';
    }
  }

  function paintSeats(root, el, ctx, v, winSet) {
    const st = state(root);
    const at = layout(v);
    const ring = el.querySelector('.pk-ring');
    const won = {};
    if (!v.live && v.last && v.last.hand === v.hand) for (const w of v.last.wins || []) won[w.pos] = (won[w.pos] || 0) + w.amount;
    let rect = null;
    const newHand = st.hand !== v.hand;
    for (let p = 0; p < N; p++) {
      const x = v.seats[p] || {};
      const s = ring.querySelector('.pk-seat[data-pos="' + p + '"]');
      const bet = ring.querySelector('.pk-bet[data-pos="' + p + '"]');
      const pos = at[p];
      s.hidden = !pos;
      if (!pos) { bet.hidden = true; st.bets[p] = 0; continue; }
      const L = pct(pos.x), T = pct(pos.y);
      if (s.style.left !== L) s.style.left = L;
      if (s.style.top !== T) s.style.top = T;
      const me = p === v.me;
      s.classList.toggle('me', me);
      s.classList.toggle('turn', v.turnPos === p);
      s.classList.toggle('fold', x.state === 'fold' || x.state === 'bust' || x.state === 'out');
      s.classList.toggle('away', !!(x.away || x.gone));
      s.classList.toggle('won', won[p] > 0);
      s.classList.toggle('top', pos.y < -0.3);
      setHtml(s.querySelector('.pk-name'), (x.bot && !/🤖/.test(x.name || '') ? '🤖 ' : '') + ctx.esc(x.name || '…'));
      setHtml(s.querySelector('.pk-stack'), won[p] > 0 ? fmt(x.stack) + ' <em>+' + fmt(won[p]) + '</em>' : fmt(x.stack));
      setHtml(s.querySelector('.pk-tag'), ctx.esc(tagOf(x, v)));
      s.querySelector('.pk-dealer').hidden = !x.dealer;
      const bl = s.querySelector('.pk-blind');
      bl.hidden = !(x.sb || x.bb);
      if (!bl.hidden) bl.textContent = x.sb ? 'МС' : 'ВС';
      // Карти в руках: свої — великі внизу (pk-mine), тут — лише в чужих: сорочки або відкриті на шоудауні.
      const hole = s.querySelector('.pk-hole');
      let html = '';
      if (!me && x.cards && x.cards.length) html = x.cards.map((c) => card(c, 'sm' + (winSet ? (winSet.has(c) ? ' win' : ' dim') : ''))).join('');
      else if (!me && x.hasCards) html = card(null, 'sm') + card(null, 'sm');
      const was = st.holeSig[p] || '';
      if (setHtml(hole, html) && html && !reduced()) {
        // нова роздача — сорочки «прилітають» по черзі; сорочки → лиця — карти перевертаються
        const kind = /back/.test(was) && !/back/.test(html) ? 'flip' : (!was ? 'deal' : '');
        if (kind) hole.querySelectorAll('.pkc').forEach((c, i) => { c.classList.add(kind); c.style.animationDelay = (kind === 'deal' ? pos.k * 70 + i * 40 : i * 90) + 'ms'; });
      }
      st.holeSig[p] = html;
      // таймер-дуга над тим, хто ходить
      const arcHost = s.querySelector('.pk-arc');
      if (v.turnPos === p && v.turnUntil) ctx.ui.timerArc(arcHost, v.turnUntil, v.turnMs || 30000);
      else stopArc(arcHost);
      // ставка цього кола: фішки перед гравцем; кінець кола — їдуть у банк
      const amt = x.bet || 0, prev = newHand ? 0 : (st.bets[p] || 0);
      const BL = pct(pos.x * 0.56), BT = pct(pos.y * 0.56);
      if (amt > 0) {
        clearTimeout(bet._t);
        bet.classList.remove('topot');
        bet.style.left = BL; bet.style.top = BT;
        bet.hidden = false;
        if (setHtml(bet.querySelector('b'), fmt(amt)) && !reduced()) { bet.classList.remove('pop'); void bet.offsetWidth; bet.classList.add('pop'); }
      } else if (prev > 0 && !bet.hidden && !reduced()) {
        rect = rect || ring.getBoundingClientRect();
        bet.style.setProperty('--tx', (-pos.x * 0.28 * rect.width).toFixed(0) + 'px');
        bet.style.setProperty('--ty', (-pos.y * 0.28 * rect.height).toFixed(0) + 'px');
        bet.classList.add('topot');
        clearTimeout(bet._t);
        bet._t = setTimeout(() => { bet.hidden = true; bet.classList.remove('topot'); }, 480);
      } else if (!(bet.classList.contains('topot'))) bet.hidden = true;
      st.bets[p] = amt;
    }
    // Банк летить до переможця: лише на свіжий результат, не на перемальовку після F5.
    if (v.last && !v.live && v.last.hand === v.hand && st.lastShown !== v.last.hand) {
      if (st.lastShown != null && !reduced()) {
        rect = rect || ring.getBoundingClientRect();
        for (const w of v.last.wins || []) {
          const pos = at[w.pos];
          if (!pos) continue;
          const f = document.createElement('div');
          f.className = 'pk-fly';
          f.innerHTML = '<i></i><b>+' + fmt(w.amount) + '</b>';
          f.style.setProperty('--dx', (pos.x * 0.5 * rect.width).toFixed(0) + 'px');
          f.style.setProperty('--dy', (pos.y * 0.5 * rect.height).toFixed(0) + 'px');
          ring.appendChild(f);
          setTimeout(() => f.remove(), 1300);
        }
      }
      st.lastShown = v.last.hand;
    } else if (st.lastShown == null) st.lastShown = v.last ? v.last.hand : 0;
    st.hand = v.hand;
  }

  function stopArc(host) {
    const a = host.querySelector(':scope > .garc');
    if (a) { if (a._arc) a._arc.stop(); a.remove(); }
  }

  function paintCenter(root, el, ctx, v, winSet) {
    // банк: усе в роздачі; побічні — окремо, коли їх більше одного
    let pot = '';
    if (v.live && v.potTotal > 0) {
      pot = '<span class="pk-potsum">Банк <b>' + fmt(v.potTotal) + '</b></span>';
      const pots = v.pots || [];
      if (pots.length > 1) pot += '<span class="pk-side">' + pots.map((x, i) => (i ? 'побічний ' : 'головний ') + fmt(x.amount)).join(' · ') + '</span>';
    }
    setHtml(el.querySelector('.pk-pot'), pot);
    const board = v.board || [];
    el.querySelectorAll('.pk-slot').forEach((slot, i) => {
      const c = board[i];
      const cls = c ? (winSet ? (winSet.has(c) ? 'win' : 'dim') : '') : '';
      const html = c ? card(c, cls) : '';
      const was = slot._sig || '';
      if (setHtml(slot, html) && c && !reduced() && !was.includes('data-c="' + c + '"')) {
        const k = slot.firstChild;
        k.classList.add('in');
        k.style.animationDelay = (v.street === 1 && i < 3 ? i * 110 : 0) + 'ms';
      }
    });
    let msg = '';
    if (v.over) {
      const places = v.seats.filter((x) => x.place).sort((a, b) => a.place - b.place);
      msg = places.length ? '🏆 ' + places.slice(0, 3).map((x) => x.place + '. ' + ctx.esc(x.name)).join(' · ')
        : v.format === 'cash' ? '💰 Стіл розійшовся' : '🏁 Турнір скінчився';
    } else if (v.waiting && !v.live) msg = ctx.esc(v.waiting);
    else if (v.runout) msg = '🔥 Олл-ін — відкриваємо дошку';
    else if (!v.live && v.last && v.last.text) {
      msg = '<b>' + ctx.esc(v.last.text) + '</b>';
      if (v.nextHandAt) msg += '<small>далі за <span data-until="' + ctx.esc(v.nextHandAt) + '" data-sec>' + secsLeft(v.nextHandAt) + '</span> с</small>';
    } else if (v.live) msg = '<small>' + STREET[v.street] + '</small>';
    else if (v.nextHandAt) msg = 'Роздаємо…';
    setHtml(el.querySelector('.pk-msg'), msg);
  }

  const secsLeft = (iso) => Math.max(0, Math.ceil((Date.parse(iso) - Date.now()) / 1000));

  function paintMine(el, ctx, v, winSet) {
    const me = v.me != null ? v.seats[v.me] : null;
    const cards = me && me.cards ? me.cards : [];
    const host = el.querySelector('.pk-cards');
    const was = host._sig || '';
    const html = cards.map((c) => card(c, 'lg' + (winSet ? (winSet.has(c) ? ' win' : ' dim') : ''))).join('');
    if (setHtml(host, html) && html && !reduced() && !/data-c/.test(was)) host.querySelectorAll('.pkc').forEach((c, i) => { c.classList.add('deal'); c.style.animationDelay = i * 90 + 'ms'; });
    el.querySelector('.pk-mine').classList.toggle('folded', !!me && me.state === 'fold');
    let combo = '';
    if (v.myHand) combo = 'у тебе: <b>' + ctx.esc(v.myHand) + '</b>';
    else if (me && me.state === 'fold' && v.live) combo = 'ти скинув — чекай наступної';
    setHtml(el.querySelector('.pk-combo'), combo);
  }

  // ---------- панель ходу ----------

  function quick(a, myBet, f) {
    const to = Math.round(myBet + a.call + f * (a.pot + a.call));
    return Math.max(a.raiseMin, Math.min(a.raiseMax, to));
  }

  function paintPanel(root, ctx) {
    const st = state(root);
    const el = root.querySelector(':scope > .pk');
    const panel = el.querySelector('.pk-panel');
    const v = ctx.view || {};
    const a = v.actions;
    const me = v.me != null && v.seats ? v.seats[v.me] : null;
    if (!a) {
      stopArc(panel.querySelector('.pk-parc') || panel);
      // Наперед: я в роздачі, не мій хід, ще маю що ставити.
      if (me && v.live && !v.runout && me.state === 'play' && !me.away && me.hasCards && v.turnPos != null) {
        if (st.pre && st.preHand !== v.hand) st.pre = null;
        const box = (k, t) => '<label class="pk-pre"><input type="checkbox" data-pre="' + k + '"' + (st.pre === k ? ' checked' : '') + '> ' + t + '</label>';
        setHtml(panel, '<div class="pk-pres"><span class="muted small">Наперед:</span>' + box('cf', 'Чек/фолд') + box('ca', 'Колл будь-що') + '</div>');
      } else { st.pre = null; setHtml(panel, ''); }
      panel.classList.remove('on');
      return;
    }
    const myBet = me ? me.bet || 0 : 0;
    const canRaise = a.raiseMax > 0 && a.raiseMax >= a.raiseMin;
    const key = v.hand + ':' + v.street + ':' + a.raiseMin + ':' + a.raiseMax;
    if (st.amtKey !== key) { st.amtKey = key; st.amt = a.raiseMin; }
    st.amt = Math.max(a.raiseMin, Math.min(a.raiseMax, st.amt || a.raiseMin));
    const bet = a.check;   // ніхто не ставив — це «бет», інакше «рейз до»
    let html = '<div class="pk-acts">'
      + '<button type="button" class="pk-b fold" data-act="fold" title="F">Фолд</button>'
      + (a.check ? '<button type="button" class="pk-b call" data-act="check" title="C">Чек</button>'
        : '<button type="button" class="pk-b call" data-act="call" title="C">Колл ' + fmt(a.call) + '</button>')
      + (canRaise ? '<button type="button" class="pk-b raise primary" data-act="raise" title="R">' + (bet ? 'Бет ' : 'Рейз до ') + '<span class="pk-amt">' + fmt(st.amt) + '</span></button>' : '')
      + '<button type="button" class="pk-b allin" data-act="allin" title="A">Олл-ін ' + fmt(a.allIn) + '</button>'
      + '<span class="pk-parc"></span></div>';
    if (canRaise && a.raiseMax > a.raiseMin) {
      const qs = [['½', 0.5], ['¾', 0.75], ['банк', 1]].map(([t, f]) => [t, quick(a, myBet, f)]);
      html += '<div class="pk-raise"><input type="range" class="pk-range" min="' + a.raiseMin + '" max="' + a.raiseMax + '" step="1" value="' + st.amt + '" aria-label="Сума ставки">'
        + '<span class="pk-qs">' + qs.map(([t, n]) => '<button type="button" class="ghost small" data-q="' + n + '">' + t + '</button>').join('')
        + '<button type="button" class="ghost small" data-q="' + a.raiseMax + '">макс</button></span></div>';
    }
    setHtml(panel, html);
    const range = panel.querySelector('.pk-range');
    if (range && +range.value !== st.amt && document.activeElement !== range) range.value = st.amt;
    showAmt(root);
    panel.classList.add('on');
    if (v.turnUntil) ctx.ui.timerArc(panel.querySelector('.pk-parc'), v.turnUntil, v.turnMs || 30000);
  }

  function showAmt(root) {
    const st = state(root);
    const t = root.querySelector('.pk-amt');
    if (t) t.textContent = fmt(st.amt);
    const r = root.querySelector('.pk-range');
    if (r && +r.value !== st.amt) r.value = st.amt;
  }

  // ---------- кнопки під столом ----------

  function paintBtns(root, el, ctx, v) {
    const st = state(root);
    const me = v.me != null ? v.seats[v.me] : null;
    const out = [];
    if (ctx.mine && ctx.playing && me && !v.over) {
      const done = me.state === 'bust' || (v.format !== 'cash' && me.state === 'out');
      if (!done) out.push(me.away ? '<button type="button" class="primary" data-act="back">🙋 Я тут</button>'
        : '<button type="button" class="ghost" data-act="away">☕ Відійти</button>');
      if (v.cash && v.cash.canRebuy)
        out.push('<button type="button" class="ghost" data-act="rebuy">' + (me.stack > 0 ? 'Докупити до ' + fmt(v.cash.buyin) : 'Знову за стіл: ' + fmt(v.cash.buyin))
          + ' · −' + fmt(v.cash.rebuyCost) + '</button>');
      if (v.canShow) out.push('<button type="button" class="ghost" data-act="show">🃏 Показати карти</button>');
      if (v.format === 'cash') {
        const armed = st.leaveArm > Date.now();
        out.push('<button type="button" class="' + (armed ? 'danger' : 'ghost') + '" data-leave>'
          + (armed ? 'Точно встати? +' + fmt(me.stack) + ' черепків у гаманець' : 'Встати й забрати ' + fmt(me.stack) + ' черепків') + '</button>');
      }
    }
    setHtml(el.querySelector('.pk-btns'), out.join(''));
  }

  // ---------- дії ----------

  function act(root, a, p) {
    const st = state(root);
    const ctx = st.ctx;
    if (!ctx || st.busy) return;
    st.busy = true;
    Promise.resolve(ctx.act(a, p)).catch(() => {}).then(() => { st.busy = false; });
  }

  function doMove(root, kind) {
    const st = state(root);
    const a = ((st.ctx || {}).view || {}).actions;
    if (!a) return false;
    if (kind === 'fold') act(root, 'fold');
    else if (kind === 'call') act(root, a.check ? 'check' : 'call');
    else if (kind === 'allin') act(root, 'allin');
    else if (kind === 'raise') {
      if (!(a.raiseMax > 0)) return false;
      act(root, 'raise', { to: Math.max(a.raiseMin, Math.min(a.raiseMax, st.amt || a.raiseMin)) });
    } else return false;
    return true;
  }

  /// Наперед-прапорець спрацьовує, щойно дійшла моя черга в тій самій роздачі.
  function runPre(root, ctx) {
    const st = state(root);
    const v = ctx.view || {};
    if (!st.pre || !v.actions) return;
    if (st.preHand !== v.hand) { st.pre = null; return; }
    const pre = st.pre;
    st.pre = null;
    if (pre === 'cf') act(root, v.actions.check ? 'check' : 'fold');
    else if (pre === 'ca') act(root, v.actions.check ? 'check' : 'call');
  }

  function step(root, dir) {
    const st = state(root);
    const v = (st.ctx || {}).view || {};
    const a = v.actions;
    if (!a || !(a.raiseMax > a.raiseMin)) return false;
    const me = v.seats[v.me] || {};
    if (dir === 'up' || dir === 'down') {
      // ↑↓ — сходинки: мінімум → ½ → ¾ → банк → олл-ін
      const marks = [a.raiseMin, quick(a, me.bet || 0, 0.5), quick(a, me.bet || 0, 0.75), quick(a, me.bet || 0, 1), a.raiseMax]
        .filter((x, i, arr) => arr.indexOf(x) === i).sort((x, y) => x - y);
      st.amt = dir === 'up' ? (marks.find((x) => x > st.amt) || a.raiseMax) : ([...marks].reverse().find((x) => x < st.amt) || a.raiseMin);
    } else {
      const bb = (v.blinds && v.blinds.bb) || 1;
      st.amt = Math.max(a.raiseMin, Math.min(a.raiseMax, st.amt + (dir === 'right' ? bb : -bb)));
    }
    showAmt(root);
    return true;
  }

  function onClick(root, e) {
    const st = state(root);
    const ctx = st.ctx;
    if (!ctx) return;
    const b = e.target.closest('button');
    if (!b) return;
    if (b.dataset.q) { st.amt = +b.dataset.q; showAmt(root); return; }
    if (b.hasAttribute('data-leave')) {
      if (st.leaveArm > Date.now()) {
        st.leaveArm = 0;
        if (HGames.call && ctx.room) HGames.call('LeaveRoom', ctx.room.id);
        return;
      }
      st.leaveArm = Date.now() + 4000;
      paintBtns(root, root.querySelector(':scope > .pk'), ctx, ctx.view || {});
      setTimeout(() => { if (root._pk && st.ctx) paintBtns(root, root.querySelector(':scope > .pk'), st.ctx, st.ctx.view || {}); }, 4100);
      return;
    }
    const a = b.dataset.act;
    if (!a) return;
    if (a === 'fold' || a === 'allin') doMove(root, a);
    else if (a === 'check' || a === 'call') doMove(root, 'call');
    else if (a === 'raise') doMove(root, 'raise');
    else act(root, a);
  }

  /// Годинник шапки й «далі за N с» — раз на секунду, без перемальовки.
  function clock(root) {
    root.querySelectorAll('[data-until]').forEach((x) => {
      const t = x.hasAttribute('data-sec') ? String(secsLeft(x.dataset.until)) : untilText(x.dataset.until);
      if (x.textContent !== t) x.textContent = t;
    });
  }

  function keyOf(e) {
    const k = (e.key || '').toLowerCase();
    const c = e.code || '';
    if (k === 'f' || k === 'а' || c === 'KeyF') return 'fold';
    if (k === 'c' || k === 'с' || c === 'KeyC') return 'call';
    if (k === 'r' || k === 'к' || c === 'KeyR') return 'raise';
    if (k === 'a' || k === 'ф' || c === 'KeyA') return 'allin';
    if (c === 'ArrowUp' || k === 'arrowup') return 'up';
    if (c === 'ArrowDown' || k === 'arrowdown') return 'down';
    if (c === 'ArrowLeft' || k === 'arrowleft') return 'left';
    if (c === 'ArrowRight' || k === 'arrowright') return 'right';
    return '';
  }

  HGames.register({
    id: 'poker',
    icon: ICON,
    added: '2026-10-06',

    pad: {
      dirs: true,
      hint: '{a} чек / колл · {x} фолд · {y} рейз · {dpad} сума (↑↓ сходинки, ←→ на ВС)',
      when: (ctx) => ctx.mine && ctx.playing && !!(ctx.view && ctx.view.actions),
      on(btn) {
        if (!active) return false;
        if (btn === 'a') return doMove(active, 'call');
        if (btn === 'x') return doMove(active, 'fold');
        if (btn === 'y') return doMove(active, 'raise');
        return false;
      },
    },

    mount(root, ctx) {
      paint(root, ctx);
      const st = state(root);
      clearInterval(st.clock);
      st.clock = setInterval(() => clock(root), 1000);
    },
    update(root, ctx) { paint(root, ctx); },
    unmount(root) {
      const st = root._pk;
      if (st) clearInterval(st.clock);
      root.querySelectorAll('.pk-arc, .pk-parc').forEach(stopArc);
      if (active === root) active = null;
      root._pk = null;
    },

    onKey(e, ctx) {
      if (!ctx.mine || !ctx.playing || !(ctx.view && ctx.view.actions)) return false;
      const root = active;
      if (!root) return false;
      const k = keyOf(e);
      if (!k) return false;
      if (k === 'up' || k === 'down' || k === 'left' || k === 'right') return step(root, k);
      if (e.repeat) return true;
      return doMove(root, k);
    },

    status(ctx) {
      const v = ctx.view;
      const room = ctx.room || {};
      if (room.status === 'lobby') {
        const o = room.options || {};
        if (o.format === 'cash') return 'Кеш-стіл: на старті кожен платить викуп — ' + ({ 2: 250, 5: 500 }[o.stakes] || 100) + ' черепків';
        if (o.format === 'tour') return 'Турнір на черепки: внесок ' + (o.buyin || 50) + ' з кожного';
        return '';
      }
      if (!v || !v.seats) return '';
      if (v.over) {
        const w = v.seats.find((x) => x.place === 1);
        if (v.format === 'cash') return '💰 Стіл розійшовся';
        return w ? (v.me != null && v.seats[v.me] === w ? '🏆 Ти забрав усі фішки!' : '🏆 Переможець — ' + w.name) : '🏁 Турнір скінчився';
      }
      if (v.me == null) return ctx.mine ? 'Сідаєш з наступної роздачі' : 'Дивишся збоку';
      const me = v.seats[v.me];
      if (v.actions) return v.actions.check ? 'Твій хід: чек або бет' : 'Твій хід: колл ' + fmt(v.actions.call) + ' — чи рейз, чи пас';
      if (me.away) return '☕ Ти відійшов — гра скидає за тебе. Повернешся — «Я тут»';
      if (v.waiting && !v.live) return v.waiting;
      if (v.runout) return '🔥 Олл-ін — відкриваємо дошку';
      if (v.turnPos != null) return (v.seats[v.turnPos].name || '…') + ' думає…';
      if (!v.live && v.last && v.last.text) return v.last.text;
      return '';
    },
  });
})();
