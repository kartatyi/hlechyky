/* Турнір тижня в слотах (пакет 5 «Азарт чесно», docs/games/specs/slot-tourney.md): смуга над автоматом і режим фішок.
   Сервер дає view.tourney: {game, title, here, open, ends, chips, spins, bet, prizes, person, my, mine, players, board}.
   В автоматі тижня — «Грати в турнір»: кіт перемикається на фішки (SlotKit ctx.setTourney), оберт — act('tspin'),
   гаманець не рухається. В інших автоматах — табличка «Турнір тижня — у …» з переходом. slot.js кличе
   SlotTour.mount/update/unmount і питає st.tmode. */
(function () {
  'use strict';
  const fmt = (n) => Math.round(n || 0).toLocaleString('uk-UA').replace(/,/g, ' ');
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const tv = (st) => (st.ctx && st.ctx.view && st.ctx.view.tourney) || null;

  function endsText(t) {
    if (!t.open) return 'підбито';
    const ms = new Date(t.ends) - Date.now();
    if (ms <= 0) return 'підбиваємо';
    if (ms < 3600e3) return 'ще ' + Math.max(1, Math.round(ms / 60e3)) + ' хв';
    if (ms < 24 * 3600e3) return 'ще ' + Math.round(ms / 3600e3) + ' год';
    return 'до неділі 21:00';
  }

  function boardHtml(t) {
    const rows = (t.board || []).map((r) => '<li class="' + (r.me ? 'me' : '') + '"><span class="st-p">' + r.place + '</span><b>' + esc(r.nick)
      + '</b><span class="st-b">' + fmt(r.best) + ' 🎟</span><span class="muted st-n">спроб: ' + r.tries + '</span></li>').join('');
    const prizes = (t.prizes || []).map(fmt).join(' / ');
    return '<div class="st-panel">'
      + (rows ? '<ol class="st-board">' + rows + '</ol>' : '<div class="muted">Табло порожнє — перша спроба буде першою й у таблиці</div>')
      + (t.mine && !(t.board || []).some((r) => r.me) ? '<div class="st-mine">Ти — ' + t.mine.place + '-е місце: ' + fmt(t.mine.best) + ' 🎟</div>' : '')
      + '<div class="muted small">Щодня одна спроба: ' + fmt(t.chips) + ' фішок, ' + t.spins + ' обертів по ' + t.bet
      + '. У залік — найкраща. Призи в неділю о 21:00: ' + prizes + ' 🏺 від Дядька Глека (коли грає хоч ' + (t.minPlayers || 2) + ')'
      + (t.person === false ? '. Ти гість — пограти можна, але в табло й призи йдуть лише акаунти' : '') + '. Черепки з гаманця не беруться.</div></div>';
  }

  function render(st) {
    const T = st.tour;
    if (!T) return;
    const t = tv(st);
    if (!t) { T.bar.hidden = true; if (st.tmode) leave(st); return; }
    T.bar.hidden = false;
    const my = t.my, lead = t.board && t.board[0];
    const leadTxt = lead ? '<span class="st-lead muted">лідер: ' + esc(lead.nick) + ' ' + fmt(lead.best) + '</span>' : '';
    const bBtn = '<button class="ghost st-btn" data-t="board">' + (T.open ? 'сховати' : 'табло') + '</button>';
    let html;
    if (!t.here) {
      html = '<span class="st-ico">🏆</span><span class="st-t">Турнір тижня — у «' + esc(t.title) + '»</span>' + leadTxt
        + '<button class="primary st-btn" data-t="go">перейти →</button>' + bBtn;
    } else if (st.tmode) {
      html = '<span class="st-ico">🏆</span><b class="st-t">Турнір</b><span class="st-chips">🎟 ' + fmt(T.chips) + '</span>'
        + '<span class="muted">оберт ' + T.spin + ' / ' + t.spins + '</span>' + bBtn + '<button class="ghost st-btn" data-t="exit">вийти</button>';
    } else if (!t.open) {
      html = '<span class="st-ico">🏆</span><span class="st-t">Турнір тижня підбито — новий з понеділка</span>' + leadTxt + bBtn;
    } else if (my && my.done) {
      html = '<span class="st-ico">🏆</span><span class="st-t">Сьогоднішня спроба: <b>' + fmt(my.chips) + '</b> 🎟'
        + (t.mine ? ' · ' + t.mine.place + '-е місце' : '') + ' · завтра нова</span><span class="muted">' + endsText(t) + '</span>' + bBtn;
    } else {
      html = '<span class="st-ico">🏆</span><span class="st-t">Турнір тижня: ' + fmt(t.chips) + ' фішок, ' + t.spins + ' обертів</span>'
        + leadTxt + '<span class="muted">' + endsText(t) + '</span>'
        + '<button class="primary st-btn" data-t="enter">' + (my ? 'продовжити (' + my.spin + ' / ' + my.of + ')' : 'грати в турнір') + '</button>' + bBtn;
    }
    T.bar.innerHTML = '<div class="st-row">' + html + '</div>' + (T.open ? boardHtml(t) : '');
  }

  function enter(st) {
    const t = tv(st), k = st.inst && st.inst.ctx;
    if (!t || !k || k.busy || !t.here || !t.open) return;
    const my = t.my;
    if (my && my.done) return;
    st.tmode = true;
    st.tour.chips = my ? my.chips : t.chips;
    st.tour.spin = my ? my.spin : 0;
    k.auto = 0;
    if (st.machine) st.machine.buy = null;
    k.setTourney({ chips: st.tour.chips, spin: st.tour.spin, of: t.spins, bet: t.bet });
    k.say('Турнір: ' + fmt(st.tour.chips) + ' фішок, ставка ' + t.bet + ' — черепки не чіпаємо', 3000);
    render(st);
  }

  function leave(st) {
    const k = st.inst && st.inst.ctx;
    st.tmode = false;
    if (!k) return;
    k.auto = 0;
    k.setTourney(null);
    const v = st.ctx.view || {};
    if (st.machine) st.machine.buy = v.buy || null;
    const b = window.HGames && HGames.wallet != null ? HGames.wallet : v.balance;
    if (b != null) k.setBalance(b);
    render(st);
  }

  // кінець турнірного оберту: фішки й лічильник — від сервера (script.tourney), спроба скінчилась — з режиму геть
  function afterSpin(st, script) {
    if (!st.tmode) return;
    const k = st.inst.ctx, t = tv(st), x = script && script.tourney;
    if (!x) {
      // відмова (спробу зіграно, тиждень підбито): правда — у виді
      const my = t && t.my;
      if (!t || !t.here || !t.open || (my && my.done)) { leave(st); return; }
      if (my) { st.tour.chips = my.chips; st.tour.spin = my.spin; k.setTourney({ chips: my.chips, spin: my.spin, of: t.spins, bet: t.bet }); }
      return;
    }
    st.tour.chips = x.chips; st.tour.spin = x.spin;
    if (x.done) {
      k.auto = 0;
      k.say('Спробу зараховано: ' + fmt(x.chips) + ' фішок. Завтра — нова', 4500);
      st.timer = setTimeout(() => { if (st.tmode && !k.busy) leave(st); }, 1500);
    } else k.setTourney({ chips: x.chips, spin: x.spin, of: x.of, bet: k.bet });
    render(st);
  }

  function go(game) {
    if (!window.HGames) return;
    if (HGames.openSolo) { HGames.openSolo(game); return; }
    Promise.resolve(HGames.call('OpenSolo', game, null)).then((r) => {
      if (r && r.ok && r.roomId) location.hash = '#games/room/' + encodeURIComponent(r.roomId);
    });
  }

  function mount(st) {
    if (st.tour || !st.wrap || !st.wrap.parentNode) return;
    const bar = document.createElement('div');
    bar.className = 'slot-tour';
    bar.hidden = true;
    st.wrap.parentNode.insertBefore(bar, st.wrap);
    st.tour = { bar, open: false, chips: 0, spin: 0 };
    bar.addEventListener('click', (e) => {
      const b = e.target.closest && e.target.closest('[data-t]');
      if (!b) return;
      const a = b.dataset.t, t = tv(st);
      if (a === 'board') { st.tour.open = !st.tour.open; render(st); }
      else if (a === 'enter') enter(st);
      else if (a === 'exit') { if (!(st.inst && st.inst.ctx.busy)) leave(st); }
      else if (a === 'go' && t) go(t.game);
    });
    if (st.inst) st.inst.ctx.on('spinEnd', (script) => afterSpin(st, script));
    render(st);
  }

  function update(st) {
    if (!st.tour) return;
    // поки крутиться — смугу не чіпаємо (лічильник оновить afterSpin)
    if (st.tmode && st.inst && (st.inst.ctx.busy || st.waitSpin)) return;
    render(st);
  }

  function unmount(st) {
    if (st.timer) clearTimeout(st.timer);
    if (st.tour && st.tour.bar) st.tour.bar.remove();
    st.tour = null;
    st.tmode = false;
  }

  window.SlotTour = { mount, update, unmount };
})();
