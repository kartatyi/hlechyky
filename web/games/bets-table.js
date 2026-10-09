/*
  🎲 Ставки на столі — window.HTableBets (bets-contract §4, §6 «Стіл»). Панель у .gextra картки столу (core.js кличе
  paint() з refreshCard, коли стіл змінився): ринки з кефами, сума, «можливий виграш», мої ставки й ставки інших,
  підсумок щойно дограної партії. Згортається (пам'ятаємо в браузері), щоб не заважати грі.

  Правила й гроші — на сервері (Bets/TableBets.cs), тут лише малюємо. Хаб: BetsOfTable(roomId) → дані панелі,
  BetOnTable(roomId, market, option, stake, odds, key) → { ok, message, status, data }; подія tableBets { room } —
  ставки столу змінились (core.js пересилає в changed()). key — проти подвійного кліку: той самий ключ — та сама ставка.
*/
(() => {
  'use strict';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const num = (n) => Number(n || 0).toLocaleString('uk-UA');
  const kef = (o) => '×' + String(Math.round(Number(o || 0) * 100) / 100).replace('.', ',');
  const signed = (n) => (n > 0 ? '+' : n < 0 ? '−' : '') + num(Math.abs(n));
  const payout = (stake, odds) => Math.max(stake, Math.floor(stake * odds + 1e-9));
  const newKey = () => Math.random().toString(36).slice(2, 12) + Date.now().toString(36);

  const OPEN_KEY = 'tbOpen';
  let open = true;
  try { open = localStorage.getItem(OPEN_KEY) !== '0'; } catch { /* приватне вікно — розгорнуто */ }

  /// roomId → { host, data, sig, pick: {m, o}, stake, key, busy, pending, retried, timer }
  const tables = {};

  const call = (...a) => (window.HGames && HGames.call ? HGames.call(...a) : Promise.resolve({ ok: false }));

  // ---------------------------------------------------------------- дані

  function schedule(id, ms) {
    const t = tables[id];
    if (!t) return;
    clearTimeout(t.timer);
    t.timer = setTimeout(() => load(id), ms);
  }

  async function load(id) {
    const t = tables[id];
    if (!t || !t.host || !t.host.isConnected) return;
    const d = await call('BetsOfTable', id);
    if (!tables[id] || !d || d.ok === false) return;
    t.data = d;
    // Картку щойно відкрили: сервер міг ще не знати, що це з'єднання дивиться стіл (WatchRoom летить поруч) — ще раз.
    if (!d.can && /підійди/.test(d.why || '') && !t.retried) { t.retried = true; schedule(id, 1500); }
    render(id);
  }

  // ---------------------------------------------------------------- малювання

  function render(id) {
    const t = tables[id];
    if (!t || !t.host) return;
    const d = t.data;
    if (!d || !d.on || !d.show) {
      if (t.host.querySelector('.tb')) t.host.querySelector('.tb').remove();
      return;
    }
    // Людина вводить суму — не перемальовуємо з-під пальців: допишемо, щойно вийде з поля.
    const input = t.host.querySelector('[data-tb-stake]');
    if (input && document.activeElement === input) { t.pending = true; return; }
    t.pending = false;

    const markets = d.markets || [];
    const bets = (d.bets || []).filter((b) => b.status !== 'back');
    const mine = bets.filter((b) => b.mine), others = bets.filter((b) => !b.mine);
    const pot = bets.reduce((s, b) => s + b.stake, 0);
    if (t.pick && !optionOf(d, t.pick)) t.pick = null;   // склад змінився — такого варіанта вже нема

    let body = '';
    if (d.why) body += '<div class="tb-why">' + esc(d.why) + '</div>';
    if (markets.length && d.account) body += markets.map((m) => marketHtml(m, t, d.can)).join('');
    else if (markets.length) body += '<div class="tb-ro">' + markets.map(roHtml).join('') + '</div>';
    if (t.pick && d.can) body += formHtml(d, t);
    if (mine.length) body += listHtml(d.status === 'playing' ? 'Мої ставки на цю партію' : 'Мої ставки', mine, false);
    if (others.length) body += listHtml('Ставки інших', others, true);
    if (d.done && d.done.bets && d.done.bets.length) body += doneHtml(d.done);

    const sum = bets.length ? ' · ' + bets.length + ' ' + plural(bets.length, 'ставка', 'ставки', 'ставок') + ' · ' + num(pot) + ' 🏺' : '';
    const html = '<div class="tb' + (open ? ' open' : '') + '">'
      + '<button type="button" class="tb-head" data-tb-toggle aria-expanded="' + open + '"><span class="tb-t">🎲 Ставки</span>'
      + '<span class="tb-sum">' + esc(sum) + '</span><span class="tb-chev" aria-hidden="true">▾</span></button>'
      + '<div class="tb-body">' + body + '</div></div>';
    t.host.innerHTML = html;
    wire(id);
  }

  function marketHtml(m, t, can) {
    return '<div class="tb-mk"><div class="tb-mk-t">' + esc(m.title) + '</div><div class="tb-opts">'
      + m.options.map((o) => {
        const on = t.pick && t.pick.m === m.key && t.pick.o === o.key;
        const off = !can || !!o.no;
        return '<button type="button" class="tb-opt' + (on ? ' on' : '') + '" data-m="' + esc(m.key) + '" data-o="' + esc(o.key) + '"'
          + (off ? ' disabled' : '') + (o.no ? ' title="' + esc(o.no) + '"' : '') + '>'
          + '<span class="tb-n">' + esc(o.title) + '</span><b class="tb-k">' + kef(o.odds) + '</b></button>';
      }).join('')
      + '</div>' + (m.options.some((o) => o.no) && can ? '<div class="tb-no">' + esc(m.options.find((o) => o.no).no) + '</div>' : '')
      + '</div>';
  }

  /// Гостю — кефи текстом, без кнопок: ставити можна лише з акаунтом.
  function roHtml(m) {
    return '<div class="tb-ro-m"><span class="tb-mk-t">' + esc(m.title) + ':</span> '
      + m.options.map((o) => esc(o.title) + ' <b>' + kef(o.odds) + '</b>').join(' · ') + '</div>';
  }

  function formHtml(d, t) {
    const o = optionOf(d, t.pick);
    const left = d.max > 0 ? Math.max(0, d.max - (d.staked || 0)) : 0;
    const max = Math.max(d.min || 1, Math.min(d.balance || 0, left || Infinity));
    const stake = t.stake || '';
    const win = stake ? payout(Number(stake), o.odds) : 0;
    const chips = [10, 50, 100].filter((x) => x <= max && x >= (d.min || 1));
    return '<div class="tb-form">'
      + '<div class="tb-pick">на «' + esc(o.phrase || o.title) + '» <b>' + kef(o.odds) + '</b></div>'
      + '<div class="tb-row"><label class="tb-f"><input type="number" inputmode="numeric" min="' + (d.min || 1) + '" max="' + max
      + '" step="1" placeholder="' + (d.min || 1) + '" aria-label="Скільки черепків" data-tb-stake value="' + esc(stake) + '"><span>🏺</span></label>'
      + '<button type="button" class="primary tb-go" data-tb-go' + (stake ? '' : ' disabled') + '>Поставити</button></div>'
      + (chips.length ? '<div class="tb-chips">' + chips.map((x) => '<button type="button" class="chip" data-tb-chip="' + x + '">' + x + '</button>').join('')
        + '<button type="button" class="tb-x" data-tb-cancel>✕</button></div>' : '')
      + '<div class="tb-win" data-tb-win>' + winText(win, d, left) + '</div></div>';
  }

  function winText(win, d, left) {
    return (win ? 'можливий виграш <b>' + num(win) + ' 🏺</b> · ' : '') + 'у глечику ' + num(d.balance)
      + (d.max > 0 ? ' · ще можна ' + num(left) : '');
  }

  function listHtml(title, list, who) {
    return '<div class="tb-list"><div class="tb-lt">' + esc(title) + '</div>' + list.map((b) =>
      '<div class="tb-bet st-' + esc(b.status) + '">' + (who ? '<b class="tb-who">' + esc(b.nick) + '</b>' : '')
      + '<span class="tb-l">' + esc(b.label) + '</span><span class="tb-s">' + num(b.stake) + ' ' + kef(b.odds) + '</span></div>').join('') + '</div>';
  }

  function doneHtml(done) {
    const mark = { won: '✅', lost: '❌', back: '↩' };
    const real = done.bets.filter((b) => b.status !== 'back');
    const head = real.length ? 'Минула партія · Глек ' + signed(done.glek) : 'Минула партія · ставки повернуто';
    return '<details class="tb-done"><summary>' + esc(head) + '</summary>' + done.bets.map((b) =>
      '<div class="tb-bet st-' + esc(b.status) + '"><b class="tb-who">' + esc(b.nick) + '</b><span class="tb-l">' + (mark[b.status] || '') + ' ' + esc(b.label)
      + '</span><span class="tb-s">' + (b.status === 'won' ? '+' + num(b.payout - b.stake) : b.status === 'lost' ? '−' + num(b.stake) : num(b.stake))
      + '</span></div>').join('') + '</details>';
  }

  function optionOf(d, pick) {
    const m = (d.markets || []).find((x) => x.key === pick.m);
    return m && m.options.find((x) => x.key === pick.o && !x.no);
  }

  function plural(n, one, few, many) {
    const a = n % 10, b = n % 100;
    return a === 1 && b !== 11 ? one : a >= 2 && a <= 4 && (b < 12 || b > 14) ? few : many;
  }

  // ---------------------------------------------------------------- дії

  function wire(id) {
    const t = tables[id], h = t.host;
    const tb = h.querySelector('.tb');
    h.querySelector('[data-tb-toggle]').onclick = () => {
      open = !open;
      try { localStorage.setItem(OPEN_KEY, open ? '1' : '0'); } catch { /* не страшно */ }
      tb.classList.toggle('open', open);
      h.querySelector('[data-tb-toggle]').setAttribute('aria-expanded', String(open));
    };
    h.querySelectorAll('.tb-opt[data-m]').forEach((b) => b.onclick = () => {
      const same = t.pick && t.pick.m === b.dataset.m && t.pick.o === b.dataset.o;
      t.pick = same ? null : { m: b.dataset.m, o: b.dataset.o };
      t.key = newKey();
      render(id);
      const inp = h.querySelector('[data-tb-stake]');
      // на телефоні клавіатура сама не вилазить — лише на комп'ютері ставимо курсор у суму
      if (inp && !(window.HGames && HGames.ui && HGames.ui.coarse && HGames.ui.coarse())) inp.focus();
    });
    const inp = h.querySelector('[data-tb-stake]');
    if (inp) {
      inp.oninput = () => {
        const v = Math.max(0, Math.floor(Number(inp.value) || 0));
        t.stake = v || '';
        t.key = newKey();
        const d = t.data, o = optionOf(d, t.pick);
        const left = d.max > 0 ? Math.max(0, d.max - (d.staked || 0)) : 0;
        h.querySelector('[data-tb-win]').innerHTML = winText(o && v ? payout(v, o.odds) : 0, d, left);
        h.querySelector('[data-tb-go]').disabled = !v;
      };
      inp.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); place(id); } };
      inp.onblur = () => { if (t.pending) setTimeout(() => render(id), 0); };
    }
    h.querySelectorAll('[data-tb-chip]').forEach((b) => b.onclick = () => {
      t.stake = Number(b.dataset.tbChip);
      t.key = newKey();
      render(id);
    });
    const cancel = h.querySelector('[data-tb-cancel]');
    if (cancel) cancel.onclick = () => { t.pick = null; render(id); };
    const go = h.querySelector('[data-tb-go]');
    if (go) go.onclick = () => place(id);
  }

  async function place(id) {
    const t = tables[id];
    if (!t || t.busy || !t.pick || !t.stake) return;
    const o = optionOf(t.data, t.pick);
    if (!o) return;
    t.busy = true;
    const btn = t.host.querySelector('[data-tb-go]');
    if (btn) { btn.disabled = true; btn.innerHTML = '<span class="spin"></span> …'; }
    try {
      const r = await call('BetOnTable', id, t.pick.m, t.pick.o, Number(t.stake), o.odds, t.key || newKey());
      if (r && r.ok) { t.pick = null; t.stake = ''; }
      t.key = newKey();
    } finally {
      t.busy = false;
      t.pending = false;
      if (document.activeElement && t.host.contains(document.activeElement)) document.activeElement.blur();
      load(id);
    }
  }

  // ---------------------------------------------------------------- що кличе core.js

  window.HTableBets = {
    /// Картку столу перемальовано (стіл змінився): host — .gextra, rv — { room, seat, view }.
    paint(host, rv) {
      const r = rv && rv.room;
      if (!host || !r || r.maxPlayers < 2) return;
      const id = r.id;
      const t = tables[id] || (tables[id] = { host, data: null, sig: '', pick: null, stake: '', key: newKey() });
      t.host = host;
      const sig = JSON.stringify([r.status, r.round, (r.seats || []).map((s) => s && (s.nick || s.bot) || null), r.options, rv.seat]);
      if (sig !== t.sig) {
        t.sig = sig;
        t.retried = false;
        schedule(id, t.data ? 150 : 300);
      } else if (t.data && !host.querySelector('.tb')) render(id);
    },
    /// Хаб: ставки цього столу змінились (хтось поставив, повернуло, розрахувало).
    changed(id) { if (id && tables[id]) schedule(id, 120); },
    /// Картку прибрали.
    drop(id) { if (tables[id]) { clearTimeout(tables[id].timer); delete tables[id]; } },
  };
})();
