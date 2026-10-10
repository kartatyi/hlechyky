/*
  «Двадцять одно в Глека» — блекджек проти Дядька Глека. Дві гри, один модуль: `blackjack` (стіл на 5, Глек роздає за
  розкладом, 15 с на рішення) і `blackjack-solo` (сам на сам, без таймера). Правила, карти, гроші — на сервері
  (Impl/Blackjack*.cs); тут лише сукно, кнопки й наміри. Форма виду й дій — docs/games/specs/blackjack.md §5–§6, клієнт — §8.

  Вид: { mode, phase: idle|bets|play|glek|result, until, leftMs, phaseMs, turn, round, hash, limits: { min, max }, on, closed,
         dealer: { cards: [code|null], total, soft, bj } | null,
         boxes: [{ nick, seat, color, here, mine, bet, ready, staked, paid, hands: [{ cards, bet, doubled, split, done, total,
                   soft, bj, bust, active, outcome, ret }] }],
         last: { round, seed, hash, dealer, dealerBj, total, drawn, big, results: [{ nick, color, staked, paid, net, hands, five }] } | null,
         records: { win: { nick, n } | null, streak: { nick, n } | null, five: { nick, n } | null, fives },
         glek: { mood, say, seq },
         me: { wallet, bet, ready, free, staked, canBet, canDeal, canRebet, rebet, actions: { hit, stand, double, split, cost } | null,
               hint: { move, text } | null, streak, note } | null }
  Наміри: act('bet', { amount }), act('clear'), act('rebet'), act('deal', { amount? }), act('hit'|'stand'|'double'|'split').

  Карти — як у покері («As», «Td»). Час — від приходу виду (leftMs). Анімації — CSS на вставці, жодного rAF. Звуку нема.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.6" y="3.2" width="7.6" height="10.6" rx="1.4" fill="var(--panel2)" stroke="var(--muted)" stroke-width="1" transform="rotate(-12 5.4 8.5)"/>'
    + '<rect x="6.6" y="2.2" width="7.6" height="10.6" rx="1.4" fill="var(--panel2)" stroke="var(--accent)" stroke-width="1.3" transform="rotate(10 10.4 7.5)"/>'
    + '<text x="10.5" y="10.3" text-anchor="middle" font-size="5.4" font-weight="800" fill="var(--accent)" transform="rotate(10 10.4 7.5)">21</text></svg>';

  const SUIT = { s: '♠', h: '♥', d: '♦', c: '♣' };
  const CODE = /^[2-9TJQKA][shdc]$/;
  const CHIPS = [10, 25, 50, 100, 500, 'max'];
  const OUT = { bj: 'Блекджек!', win: 'Виграш', push: 'Нічия', lose: 'Програш', bust: 'Перебір' };
  const BADGE = { idle: '👋', hurry: '⏳', deal: '🃏', flip: '👀', bj: '😎', dance: '💃', bust: '💥', clap: '👏', rake: '🪵', sigh: '😮‍💨', doze: '💤' };
  const SAY_MS = 4000;

  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : v; } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* приватне вікно — не біда */ } },
  };
  const hintOn = () => store.get('blackjack_hint', '1') === '1';
  const fmt = (n) => (+n || 0).toLocaleString('uk-UA');
  const signed = (n) => (n > 0 ? '+' + fmt(n) : n < 0 ? '−' + fmt(-n) : '0');
  const roots = new WeakMap();   // ctx → root: onKey і pad приходять із ctx

  function card(code, cls) {
    const extra = cls ? ' ' + cls : '';
    if (!code || !CODE.test(code)) return '<span class="bjc back' + extra + '"></span>';
    const red = code[1] === 'h' || code[1] === 'd';
    return '<span class="bjc' + (red ? ' red' : '') + extra + '"><b>' + (code[0] === 'T' ? '10' : code[0]) + '</b><i>' + SUIT[code[1]] + '</i></span>';
  }

  function setHtml(el, html) {
    if (el._sig !== html) { el._sig = html; el.innerHTML = html; return true; }
    return false;
  }

  function state(root) {
    if (!root._bj) {
      let chip = store.get('blackjack_chip', '25');
      if (!CHIPS.some((c) => String(c) === chip)) chip = '25';
      root._bj = { ctx: null, el: null, chip, busy: {}, seen: new Set(), round: null, seq: undefined, sayT: 0, untilKey: '', untilAt: 0,
        arcIso: '', showHint: false, hintKey: '', tick: 0, fair: '' };
    }
    return root._bj;
  }

  function skeleton(root) {
    const st = state(root);
    if (st.el && root.contains(st.el.box)) return st.el;
    const box = document.createElement('div');
    box.className = 'bj';
    box.innerHTML = '<div class="bj-top"></div>'
      + '<div class="bj-felt">'
      + '<div class="bj-stage">'
      + '<div class="bj-glek" data-mood="idle"><img src="/static/glek.svg" alt="Дядько Глек" draggable="false"><span class="bj-badge"></span></div>'
      + '<div class="bj-say" aria-live="polite"></div>'
      + '<div class="bj-dealer"><div class="bj-cards bj-dcards"></div><div class="bj-dtot"></div></div>'
      + '</div>'
      + '<div class="bj-rules">Блекджек 1:1 · Глек добирає на м’яких 17 · ×2 на 9–11 · спліт до 3 рук</div>'
      + '<div class="bj-boxes"></div>'
      + '</div>'
      + '<div class="bj-panel"></div>'
      + '<div class="bj-foot"></div>'
      + '<div class="bj-fair" hidden></div>';
    root.appendChild(box);
    const q = (s) => box.querySelector(s);
    st.el = { box, top: q('.bj-top'), glek: q('.bj-glek'), badge: q('.bj-badge'), say: q('.bj-say'), dcards: q('.bj-dcards'),
      dtot: q('.bj-dtot'), boxes: q('.bj-boxes'), panel: q('.bj-panel'), foot: q('.bj-foot'), fair: q('.bj-fair') };
    box.addEventListener('click', (e) => onClick(root, e));
    return st.el;
  }

  // ---------- час ----------

  function syncTime(st, v) {
    const key = (v.phase || '') + '|' + (v.until || '') + '|' + (v.turn == null ? '' : v.turn);
    if (key !== st.untilKey) {
      st.untilKey = key;
      st.untilAt = v.leftMs != null ? Date.now() + Math.max(0, v.leftMs) : 0;
      st.arcIso = st.untilAt ? new Date(st.untilAt).toISOString() : '';
    }
  }
  const leftMs = (st) => (st.untilAt ? Math.max(0, st.untilAt - Date.now()) : 0);

  function canBet(ctx, v) {
    const me = v && v.me;
    if (!ctx.mine || !ctx.playing || !me || v.closed || !me.canBet) return false;
    return true;
  }
  const myTurn = (ctx, v) => !!(ctx.mine && ctx.playing && v && v.phase === 'play' && v.me && v.me.actions);

  // ---------- малювання ----------

  function paint(root, ctx) {
    if (!ctx) return;
    const st = state(root);
    st.ctx = ctx;
    const el = skeleton(root);
    const v = ctx.view;
    if (!v || !v.mode) { setHtml(el.panel, '<div class="bj-info muted">Глек тасує колоду…</div>'); return; }
    if (st.round !== v.round) { st.round = v.round; st.seen = new Set(); st.showHint = false; }
    el.box.classList.toggle('solo', v.mode === 'solo');
    syncTime(st, v);
    syncGlek(root, st, ctx, v);
    paintTop(st, ctx, v);
    paintDealer(st, v);
    paintBoxes(st, ctx, v);
    paintPanel(st, ctx, v);
    paintFoot(st, ctx, v);
    if (st.fair) paintFair(st, ctx, v);
  }

  function paintTop(st, ctx, v) {
    const lim = v.limits || {};
    const parts = ['<span class="bj-chip">Ставка <b>' + fmt(lim.min) + (lim.max > 0 ? '–' + fmt(lim.max) : '+') + '</b> 🏺</span>'];
    if (v.me) parts.push('<span class="bj-chip">Гаманець <b>' + fmt(v.me.wallet) + '</b></span>');
    if (v.me && v.me.streak > 1) parts.push('<span class="bj-chip">🔥 <b>' + v.me.streak + '</b> поспіль</span>');
    setHtml(st.el.top, parts.join(''));
  }

  /// Нова карта (ще не бачена в цій роздачі) — з анімацією; відкрита закрита — переворот.
  function cardOnce(st, key, code) {
    if (reduced()) return card(code);
    const seenKey = key + ':' + (code || '?');
    if (st.seen.has(seenKey)) return card(code);
    const wasBack = st.seen.has(key + ':?');
    st.seen.add(seenKey);
    return card(code, code && wasBack ? 'flip' : 'in');
  }

  function paintDealer(st, v) {
    const d = v.dealer;
    if (!d) {
      setHtml(st.el.dcards, '<span class="bj-slot"></span><span class="bj-slot"></span>');
      setHtml(st.el.dtot, '');
      return;
    }
    setHtml(st.el.dcards, d.cards.map((c, i) => cardOnce(st, 'd' + i, c)).join(''));
    const tot = d.bj ? 'Блекджек' : d.total > 21 ? d.total + ' — перебір' : String(d.total || '');
    setHtml(st.el.dtot, tot ? '<span class="bj-tot' + (d.total > 21 ? ' bust' : d.bj ? ' bj' : '') + '">' + tot + '</span>' : '');
  }

  function paintBoxes(st, ctx, v) {
    const boxes = v.boxes || [];
    if (!boxes.length) {
      const msg = v.phase === 'idle' ? 'Глек дрімає — постав, і він роздасть'
        : ctx.mine ? (v.mode === 'solo' ? 'Обери ставку й тисни «Роздати»' : 'Постав черепки — Глек роздасть, щойно всі готові')
          : 'Чекаємо на ставки';
      setHtml(st.el.boxes, '<div class="bj-empty">' + ctx.esc(msg) + '</div>');
      return;
    }
    const html = boxes.map((b, bi) => {
      const cls = ['bj-box', 'p' + (b.color % 5)];
      if (b.mine) cls.push('mine');
      if (!b.here) cls.push('away');
      if (b.hands.some((h) => h.active)) cls.push('turn');
      const net = b.paid != null ? b.paid - b.staked : null;
      if (net != null) cls.push(net > 0 ? 'won' : net < 0 ? 'lost' : 'even');
      const head = '<div class="bj-bhead"><span class="bj-dot"></span><span class="bj-name">' + ctx.esc(b.nick) + '</span>'
        + (b.hands.length ? '' : '<span class="bj-bet">' + fmt(b.bet) + ' 🏺' + (b.ready ? ' ✋' : '') + '</span>')
        + (net != null ? '<span class="bj-net">' + signed(net) + '</span>' : '') + '</div>';
      const hands = b.hands.map((h, hi) => {
        const hc = ['bj-hand'];
        if (h.active) hc.push('active');
        if (h.outcome) hc.push('o-' + h.outcome);
        const tot = h.bj ? '21 ✦' : h.bust ? h.total + ' ✕' : (h.soft && h.total < 21 ? (h.total - 10) + '/' : '') + h.total;
        return '<div class="' + hc.join(' ') + '"><div class="bj-cards">'
          + h.cards.map((c, ci) => cardOnce(st, 'b' + bi + 'h' + hi + 'c' + ci, c)).join('') + '</div>'
          + '<div class="bj-hmeta"><span class="bj-tot">' + tot + '</span><span class="bj-stake">' + fmt(h.bet * (h.doubled ? 2 : 1))
          + (h.doubled ? ' ×2' : '') + '</span>' + (h.outcome ? '<span class="bj-out">' + OUT[h.outcome] + '</span>' : '') + '</div></div>';
      }).join('');
      return '<div class="' + cls.join(' ') + '">' + head + hands + '</div>';
    }).join('');
    setHtml(st.el.boxes, html);
  }

  function chipAmount(st, v) {
    const me = v.me || {};
    const lim = v.limits || {};
    if (st.chip === 'max') {
      const cap = lim.max > 0 ? Math.min(lim.max, me.wallet || 0) : me.wallet || 0;
      return Math.max(0, cap - (me.bet || 0));
    }
    return +st.chip;
  }

  function freeSeats(ctx) {
    const r = ctx.room || {};
    const seats = r.seats || [];
    let free = 0;
    for (let i = 0; i < (seats.length || r.maxPlayers || 0); i++) {
      const s = seats[i];
      if (!(typeof s === 'string' ? s : s && s.nick)) free++;
    }
    return free;
  }

  function paintPanel(st, ctx, v) {
    const el = st.el;
    const solo = v.mode === 'solo';
    const me = v.me;
    const btn = (act, text, on, cls, title) => '<button type="button" class="' + (cls || 'ghost') + '" data-act="' + act + '"'
      + (on && !st.busy[act] ? '' : ' disabled') + (title ? ' title="' + title + '"' : '') + '>' + text + '</button>';
    let html = '';
    if (!ctx.mine || !me) {
      html = '<div class="bj-info">' + (solo ? 'Це чужий стіл' : 'Грають ті, хто сидить. Глядачам — найкращі місця') + '</div>'
        + (solo ? '' : freeSeats(ctx) > 0 && ctx.playing
          ? '<div class="bj-btns"><button type="button" class="primary" data-do="sit">🪑 Сісти за стіл</button></div>'
          : '<div class="bj-info muted">Місць нема — дивись</div>');
    } else if (v.closed) {
      html = '<div class="bj-info">Каса зачинена — спробуй трохи згодом</div>';
    } else if (myTurn(ctx, v)) {
      const a = me.actions;
      const clock = !solo && st.arcIso ? '<div class="bj-clock"></div>' : '';
      const hint = me.hint && hintOn()
        ? (st.showHint ? '<div class="bj-hint">💡 Глек підморгує: <b>' + ctx.esc(me.hint.text) + '</b></div>'
          : '<button type="button" class="ghost bj-hintbtn" data-do="hint" title="Підказка (?)">💡 Підказка</button>')
        : '';
      html = '<div class="bj-row">' + clock + '<div class="bj-info"><b>Твій хід.</b> ' + ctx.esc(handLine(v)) + '</div></div>'
        + '<div class="bj-btns bj-moves">'
        + btn('hit', '🃏 Беру', a.hit, 'primary', 'Беру (H, Пробіл)')
        + btn('stand', '✋ Стою', a.stand, 'primary', 'Стою (S, Enter)')
        + btn('double', '×2 Подвоїти · ' + fmt(a.cost), a.double, 'ghost', 'Подвоїти (D)')
        + btn('split', '✂ Розбити · ' + fmt(a.cost), a.split, 'ghost', 'Розбити пару (P)')
        + '</div>' + hint;
    } else if (v.phase === 'play') {
      const who = (v.boxes || []).find((b) => b.hands.some((h) => h.active));
      html = '<div class="bj-info">' + (who ? 'Ходить <b>' + ctx.esc(who.nick) + '</b>…' : 'Хід переходить…')
        + (me.staked ? ' Твої руки свого дочекались — стоять.' : ' Ставки — на наступну роздачу.') + '</div>';
    } else if (v.phase === 'glek') {
      html = '<div class="bj-info">Глек відкриває свої карти…</div>';
    } else if (v.phase === 'result') {
      const r = v.last && (v.last.results || []).find((x) => ctx.me && x.nick === ctx.me.nick);
      html = '<div class="bj-info">' + (r ? 'Ти <b class="' + (r.net > 0 ? 'ok' : r.net < 0 ? 'bad' : '') + '">' + signed(r.net) + '</b> 🏺 · ' : '')
        + 'Нова роздача за мить</div>';
    } else if (!v.on) {
      html = '<div class="bj-info">Глек відпочиває — ставок зараз не приймаю</div>';
    } else {
      // ставки (стіл: вікно 15 с; соло — без таймера)
      const can = canBet(ctx, v);
      let chips = '';
      for (const c of CHIPS) {
        const amt = c === 'max' ? chipAmount({ chip: 'max' }, v) : c;
        const fits = amt > 0 && (me.bet || 0) + amt <= (me.wallet || 0) && !(v.limits && v.limits.max > 0 && (me.bet || 0) + amt > v.limits.max);
        const sel = String(c) === st.chip;
        chips += '<button type="button" class="bj-chipbtn c' + c + (sel ? ' sel' : '') + '" data-chip="' + c + '"' + (can && fits ? '' : ' disabled')
          + ' title="' + (c === 'max' ? 'Найбільше, що можна' : '+' + c + ' 🏺') + '"><b>' + (c === 'max' ? 'Макс' : c) + '</b></button>';
      }
      const bet = me.bet || 0;
      const clock = !solo && v.phase === 'bets' && st.arcIso ? '<div class="bj-clock"></div>' : '';
      const deal = solo
        ? btn('deal', bet > 0 ? '🃏 Роздати · ' + fmt(bet) : me.rebet > 0 ? '🃏 Роздати · ' + fmt(me.rebet) : '🃏 Роздати', can && me.canDeal, 'primary bj-go', 'Роздати (Enter)')
        : me.ready ? '<button type="button" class="ghost bj-go" disabled>✋ Чекаю інших…</button>'
          : btn('deal', bet > 0 ? '🃏 Роздавай!' : me.rebet > 0 ? '🃏 Як минулого · ' + fmt(me.rebet) : '🃏 Роздавай!', can && me.canDeal, 'primary bj-go', 'Роздавай! (Enter)');
      html = '<div class="bj-row">' + clock + '<div class="bj-info">Ставка <b>' + fmt(bet) + '</b> 🏺 · вільних <b>' + fmt(me.free) + '</b></div></div>'
        + '<div class="bj-chiprow" role="group" aria-label="Фішки">' + chips + '</div>'
        + '<div class="bj-btns">'
        + btn('clear', '✕ Зняти', can && bet > 0, 'ghost', 'Зняти ставку (Backspace)')
        + btn('rebet', '🔁 Як минулого' + (me.rebet ? ' · ' + fmt(me.rebet) : ''), can && me.canRebet && me.rebet !== bet, 'ghost', 'Ставка минулої роздачі (R)')
        + deal + '</div>';
    }
    if (me && me.note) html += '<div class="bj-note">' + ctx.esc(me.note) + '</div>';
    setHtml(el.panel, html);
    const host = el.panel.querySelector('.bj-clock');
    if (host && st.arcIso) ctx.ui.timerArc(host, st.arcIso, v.phaseMs || 15000);
  }

  function handLine(v) {
    const box = (v.boxes || []).find((b) => b.mine && b.hands.some((h) => h.active));
    const h = box && box.hands.find((x) => x.active);
    if (!h) return '';
    const up = v.dealer && v.dealer.cards[0];
    const upTxt = up ? (up[0] === 'A' ? 'туза' : up[0] === 'T' || 'JQK'.includes(up[0]) ? 'десятки' : up[0]) : '';
    const mine = (h.soft && h.total < 21 ? 'м’які ' : '') + h.total;
    return mine + (upTxt ? ' проти ' + upTxt + ' Глека' : '') + (box.hands.length > 1 ? ' · рука ' + (box.hands.indexOf(h) + 1) + ' з ' + box.hands.length : '');
  }

  function paintFoot(st, ctx, v) {
    const r = v.records || {};
    const rec = [];
    if (r.win) rec.push('🏆 ' + ctx.esc(r.win.nick) + ' +' + fmt(r.win.n));
    if (r.streak) rec.push('🔥 ' + ctx.esc(r.streak.nick) + ' — ' + r.streak.n + ' поспіль');
    if (r.fives) rec.push('💯 21 з п’яти: ' + r.fives + (r.five ? ' (' + ctx.esc(r.five.nick) + ')' : ''));
    const title = v.mode === 'solo' ? 'Твої рекорди' : 'Рекорди столу';
    setHtml(st.el.foot, (rec.length ? '<div class="bj-rec"><span class="muted">' + title + ':</span> ' + rec.join(' · ') + '</div>' : '')
      + '<div class="bj-tools">'
      + '<button type="button" class="ghost bj-small" data-do="hinttoggle" aria-pressed="' + hintOn() + '">💡 Підказки: ' + (hintOn() ? 'увімк' : 'вимк') + '</button>'
      + '<button type="button" class="ghost bj-small" data-do="fair" title="Перевірити, що колоду не підмінили">🔒 Чесна колода</button>'
      + '</div>');
  }

  // ---------- Глек ----------

  function syncGlek(root, st, ctx, v) {
    const g = v.glek || {};
    const mood = BADGE[g.mood] != null ? g.mood : 'idle';
    const el = st.el;
    if (el.glek.dataset.mood !== mood) el.glek.dataset.mood = mood;
    if (el.badge.textContent !== BADGE[mood]) el.badge.textContent = BADGE[mood];
    if (g.seq === st.seq) return;
    const first = st.seq === undefined;
    st.seq = g.seq;
    const key = 'blackjack_seq:' + ((ctx.room && ctx.room.id) || '');
    let seen = false;
    try { seen = first && sessionStorage.getItem(key) === String(g.seq); sessionStorage.setItem(key, String(g.seq)); } catch { /* нема — то й нема */ }
    if (seen) return;   // F5: минулу хмарку й рух не повторюємо
    if (g.say) {
      el.say.textContent = g.say;
      el.say.classList.add('on');
      clearTimeout(st.sayT);
      st.sayT = setTimeout(() => { if (root._bj) el.say.classList.remove('on'); }, SAY_MS);
    }
    if (reduced()) return;
    el.glek.classList.remove('go');
    void el.glek.offsetWidth;
    el.glek.classList.add('go');
  }

  // ---------- чесна колода (ⓘ) ----------

  function paintFair(st, ctx, v) {
    const l = v.last;
    let html = '<div class="bj-fairbox"><button type="button" class="ghost bj-x" data-do="fairclose" aria-label="Закрити">✕</button>'
      + '<h4>🔒 Чесна колода</h4>'
      + '<p>Перед ставками Глек тасує колоду (6 колод, 312 карт) з таємного <code>seed</code> і одразу показує його відбиток '
      + '<code>sha256(seed)</code>. Після роздачі — сам <code>seed</code>: порядок колоди — карти, упорядковані за '
      + '<code>sha256(seed + ":" + i)</code>, карта i — i-та з 52 (A23456789TJQK × ♠♥♦♣).</p>'
      + '<p>Відбиток наступної роздачі: <code class="bj-hash">' + ctx.esc(v.hash || '—') + '</code></p>';
    if (l) {
      html += '<p>Роздача №' + l.round + ': seed <code class="bj-hash">' + ctx.esc(l.seed) + '</code><br>відбиток <code class="bj-hash">'
        + ctx.esc(l.hash) + '</code><br>карт роздано: ' + (l.drawn || []).length + '</p>'
        + '<button type="button" class="primary" data-do="verify">Перевірити роздачу №' + l.round + '</button>'
        + '<p class="bj-verdict">' + ctx.esc(st.fairOut || '') + '</p>';
    } else html += '<p class="muted">Перевіряти ще нічого — зіграй роздачу.</p>';
    html += '</div>';
    setHtml(st.el.fair, html);
    st.el.fair.hidden = false;
  }

  async function sha256(s) {
    const b = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(s));
    return Array.from(new Uint8Array(b), (x) => x.toString(16).padStart(2, '0')).join('');
  }

  async function shoeOf(seed) {
    const R = 'A23456789TJQK', S = 'shdc';
    const keys = await Promise.all(Array.from({ length: 312 }, (_, i) => sha256(seed + ':' + i).then((h) => ({ h, i }))));
    keys.sort((a, b) => (a.h < b.h ? -1 : a.h > b.h ? 1 : a.i - b.i));
    return keys.map(({ i }) => R[(i % 52) % 13] + S[Math.floor((i % 52) / 13)]);
  }

  async function verify(root) {
    const st = root._bj;
    const v = st && st.ctx && st.ctx.view;
    const l = v && v.last;
    if (!l) return;
    if (!window.crypto || !crypto.subtle) { st.fairOut = 'Цей браузер не рахує sha256 (потрібен https)'; paint(root, st.ctx); return; }
    st.fairOut = 'Рахую…';
    paint(root, st.ctx);
    try {
      const h = await sha256(l.seed);
      const shoe = await shoeOf(l.seed);
      const drawn = l.drawn || [];
      const same = drawn.every((c, i) => shoe[i] === c);
      st.fairOut = h !== l.hash ? '✕ Відбиток не збігається з seed!'
        : same ? '✔ Відбиток збігається, і всі ' + drawn.length + ' карт роздачі йшли рівно з цієї колоди'
          : '✕ Карти роздачі не збігаються з колодою з seed!';
    } catch (e) { st.fairOut = 'Не вийшло порахувати: ' + (e && e.message); }
    if (root._bj) paint(root, st.ctx);
  }

  // ---------- дії ----------

  function act(root, ctx, name, payload) {
    const st = state(root);
    if (!ctx || st.busy[name]) return;
    st.busy[name] = true;
    paint(root, ctx);
    const done = () => { st.busy[name] = false; if (root._bj) paint(root, st.ctx); };
    Promise.resolve(ctx.act(name, payload || {})).then(done, done);
  }

  function placeChip(root, ctx, chip) {
    const st = state(root);
    const v = ctx.view;
    if (!v || !canBet(ctx, v)) return;
    st.chip = String(chip);
    store.set('blackjack_chip', st.chip);
    const amount = chipAmount(st, v);
    if (amount < 1) { ctx.toast('Більше не влізе', 'err'); paint(root, ctx); return; }
    act(root, ctx, 'bet', { amount });
  }

  function stepChip(root, ctx, d) {
    const st = state(root);
    const i = CHIPS.findIndex((c) => String(c) === st.chip);
    st.chip = String(CHIPS[(i + d + CHIPS.length) % CHIPS.length]);
    store.set('blackjack_chip', st.chip);
    paint(root, ctx);
  }

  function onClick(root, e) {
    const t = e.target.closest('button');
    const st = root._bj;
    if (!t || t.disabled || !st || !st.ctx) return;
    const ctx = st.ctx;
    if (t.dataset.chip) { placeChip(root, ctx, t.dataset.chip); return; }
    if (t.dataset.act) { act(root, ctx, t.dataset.act); return; }
    switch (t.dataset.do) {
      case 'sit':
        if (ctx.room) { t.disabled = true; HGames.call('JoinRoom', ctx.room.id).finally(() => { t.disabled = false; }); }
        return;
      case 'hint': st.showHint = true; paint(root, ctx); return;
      case 'hinttoggle': store.set('blackjack_hint', hintOn() ? '0' : '1'); paint(root, ctx); return;
      case 'fair': st.fair = 'on'; st.fairOut = ''; paint(root, ctx); return;
      case 'fairclose': st.fair = ''; st.el.fair.hidden = true; return;
      case 'verify': verify(root); return;
      default:
    }
  }

  /// Головна дія: мій хід — «Беру», ставки — «Роздавай!».
  function primary(root, ctx, which) {
    const v = ctx.view;
    if (!v) return false;
    if (myTurn(ctx, v)) {
      const a = v.me.actions;
      if (which === 'hit' && a.hit) act(root, ctx, 'hit');
      else if (which === 'stand' && a.stand) act(root, ctx, 'stand');
      else if (which === 'double' && a.double) act(root, ctx, 'double');
      else if (which === 'split' && a.split) act(root, ctx, 'split');
      else if (which === 'hint' && v.me.hint && hintOn()) { state(root).showHint = true; paint(root, ctx); }
      else return false;
      return true;
    }
    if (!canBet(ctx, v)) return false;
    if (which === 'deal' && v.me.canDeal && !v.me.ready) act(root, ctx, 'deal');
    else if (which === 'chip') placeChip(root, ctx, state(root).chip);
    else if (which === 'clear' && v.me.bet > 0) act(root, ctx, 'clear');
    else if (which === 'rebet' && v.me.canRebet) act(root, ctx, 'rebet');
    else return false;
    return true;
  }

  function onKey(e, ctx) {
    const root = roots.get(ctx);
    if (!root || !ctx.mine || e.repeat) return false;
    const c = e.code || '';
    const v = ctx.view;
    if (myTurn(ctx, v)) {
      if (c === 'KeyH' || c === 'Space') return primary(root, ctx, 'hit');
      if (c === 'KeyS' || c === 'Enter' || c === 'NumpadEnter') return primary(root, ctx, 'stand');
      if (c === 'KeyD') return primary(root, ctx, 'double');
      if (c === 'KeyP') return primary(root, ctx, 'split');
      if (c === 'Slash' || e.key === '?') return primary(root, ctx, 'hint');
      return false;
    }
    const dg = /^(?:Digit|Numpad)([1-6])$/.exec(c);
    if (dg && canBet(ctx, v)) { placeChip(root, ctx, CHIPS[+dg[1] - 1]); return true; }
    if (c === 'Enter' || c === 'NumpadEnter') return primary(root, ctx, 'deal');
    if (c === 'Space') return primary(root, ctx, 'chip');
    if (c === 'Backspace' || c === 'KeyX') return primary(root, ctx, 'clear');
    if (c === 'KeyR') return primary(root, ctx, 'rebet');
    return false;
  }

  // ---------- статус ----------

  function statusText(ctx) {
    const v = ctx.view;
    if (!v || !v.mode) return '';
    const root = roots.get(ctx);
    const st = root && root._bj;
    const left = Math.ceil((st ? leftMs(st) : (v.leftMs || 0)) / 1000);
    if (v.closed) return 'Каса зачинена';
    const solo = v.mode === 'solo';
    switch (v.phase) {
      case 'idle': return 'Глек дрімає — постав, і він роздасть';
      case 'bets':
        if (solo) return v.me && v.me.bet > 0 ? 'Ставка ' + v.me.bet + ' — тисни «Роздати»' : 'Обери ставку й тисни «Роздати»';
        if (!ctx.mine) return (left > 0 ? 'Ставки ще ' + left + ' с' : 'Роздаю…') + ' · сядь, щоб грати';
        return (left > 0 ? 'Ставки ще ' + left + ' с' : 'Роздаю…') + (v.me && v.me.bet > 0 ? ' · твоя ставка ' + v.me.bet : ' · постав черепки');
      case 'play': {
        if (myTurn(ctx, v)) return 'Твій хід: ' + handLine(v) + (!solo && left > 0 ? ' · ще ' + left + ' с' : '');
        const who = (v.boxes || []).find((b) => b.hands.some((h) => h.active));
        return who ? 'Ходить ' + who.nick + '…' : '';
      }
      case 'glek': return 'Глек відкриває карти…';
      case 'result': {
        const r = v.last && ctx.me ? (v.last.results || []).find((x) => x.nick === ctx.me.nick) : null;
        const d = v.last ? (v.last.dealerBj ? 'У Глека блекджек' : v.last.total > 21 ? 'Глек перебрав' : 'У Глека ' + v.last.total) : '';
        return r ? d + ' · ти ' + signed(r.net) : d;
      }
      default: return '';
    }
  }

  function tick(root) {
    const st = root._bj;
    if (!st || !st.ctx || !st.ctx.view) return;
    const v = st.ctx.view;
    if (v.mode !== 'table' || !(v.phase === 'bets' || v.phase === 'play')) return;
    const cardEl = root.closest('.gtable');
    const se = cardEl && cardEl.querySelector('.gstatus');
    const text = statusText(st.ctx);
    if (se && text && se.textContent !== text) se.textContent = text;
  }

  // ---------- реєстрація: дві гри, один набір функцій ----------

  const api = {
    icon: ICON,
    added: '2026-10-10',
    seatNames: (i) => 'місце ' + (i + 1),
    seatClass: ['x', 'o', 'c', 'd', 'x'],
    pad: {
      dirs: false,
      hint: '{a} беру · {b} стою · {x} ×2 · {y} розбити · {lt} підказка — у свій хід; ставки: {lb}{rb} фішка · {a} покласти · {y} роздавай · {x} зняти',
      when: (ctx) => ctx.mine && ctx.playing,
      on(btn, ctx) {
        const root = roots.get(ctx);
        if (!root) return false;
        const v = ctx.view;
        if (myTurn(ctx, v)) {
          if (btn === 'a') return primary(root, ctx, 'hit') || true;
          if (btn === 'b') return primary(root, ctx, 'stand') || true;   // Ⓑ — «стою» лише у свій хід, решту часу — назад
          if (btn === 'x') return primary(root, ctx, 'double') || true;
          if (btn === 'y') return primary(root, ctx, 'split') || true;
          if (btn === 'lt') return primary(root, ctx, 'hint') || true;
          return false;
        }
        if (!canBet(ctx, v)) return false;
        if (btn === 'lb') { stepChip(root, ctx, -1); return true; }
        if (btn === 'rb') { stepChip(root, ctx, 1); return true; }
        if (btn === 'a') return primary(root, ctx, 'chip') || true;
        if (btn === 'y') return primary(root, ctx, 'deal') || true;
        if (btn === 'x') return primary(root, ctx, 'clear') || true;
        return false;
      },
    },

    mount(root, ctx) {
      roots.set(ctx, root);
      const st = state(root);
      st.ctx = ctx;
      skeleton(root);
      st.tick = setInterval(() => tick(root), 1000);
      paint(root, ctx);
    },

    update(root, ctx) {
      roots.set(ctx, root);
      paint(root, ctx);
    },

    onKey,
    status: statusText,

    unmount(root) {
      const st = root._bj;
      if (!st) return;
      clearInterval(st.tick);
      clearTimeout(st.sayT);
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      root._bj = null;
    },

    // Для перевірки (qa): колода з seed і sha256 — без DOM.
    qa: { sha256, shoeOf, card },
  };

  HGames.register(Object.assign({ id: 'blackjack' }, api));
  HGames.register(Object.assign({ id: 'blackjack-solo' }, api));
})();
