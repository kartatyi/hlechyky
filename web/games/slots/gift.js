/* 🎁 Безкоштовні прокрути (25.10.2026, SlotFreeSpins.cs): смуга над автоматом і режим «безкоштовно».
   Сервер дає view.free: {bet, total, left, person} (null — вимкнено). «Крутити безкоштовно» — кіт перемикається на
   ставку прокруту й не знімає її з балансу (SlotKit ctx.setFree), оберт — act('spin', {bet, seq, free: true}),
   лишок — у script.free. Прокрути спільні на всі автомати й згоряють опівночі. Під час турніру смуги нема.
   slot.js кличе SlotGift.mount/update/unmount і питає st.gmode. */
(function () {
  'use strict';
  const fmt = (n) => Math.round(n || 0).toLocaleString('uk-UA').replace(/,/g, ' ');
  const fv = (st) => (st.ctx && st.ctx.view && st.ctx.view.free) || null;

  function render(st) {
    const G = st.gift;
    if (!G) return;
    const f = fv(st);
    if (!f || st.tmode) { G.bar.hidden = true; if (st.gmode) leave(st); return; }
    G.bar.hidden = false;
    let html;
    if (!f.person) {
      html = '<span class="st-ico">🎁</span><span class="st-t muted">' + f.total + ' безкоштовних прокрутів щодня — лише для акаунтів</span>';
    } else if (st.gmode) {
      html = '<span class="st-ico">🎁</span><b class="st-t">Безкоштовно</b><span class="st-chips">лишилось ' + G.left + ' з ' + f.total + '</span>'
        + '<span class="muted">по ' + fmt(f.bet) + ' 🏺</span><button class="ghost st-btn" data-g="exit">вийти</button>';
    } else if (f.left > 0) {
      html = '<span class="st-ico">🎁</span><span class="st-t">Безкоштовні прокрути: <b>' + f.left + '</b> з ' + f.total + ' по ' + fmt(f.bet) + ' 🏺</span>'
        + '<span class="muted">згорають опівночі</span><button class="primary st-btn" data-g="enter">крутити безкоштовно</button>';
    } else {
      html = '<span class="st-ico">🎁</span><span class="st-t muted">Безкоштовні прокрути на сьогодні скручено — нові ' + f.total + ' опівночі</span>';
    }
    G.bar.innerHTML = '<div class="st-row">' + html + '</div>';
  }

  function enter(st) {
    const f = fv(st), k = st.inst && st.inst.ctx;
    if (!f || !f.person || f.left <= 0 || !k || k.busy || st.tmode) return;
    st.gmode = true;
    st.gift.left = f.left;
    k.auto = 0;
    k.setFree({ bet: f.bet, left: f.left });
    k.say('🎁 ' + f.left + ' безкоштовних по ' + fmt(f.bet) + ' — гаманець не чіпаємо', 3000);
    render(st);
  }

  function leave(st) {
    const k = st.inst && st.inst.ctx;
    st.gmode = false;
    if (k) { k.auto = 0; k.setFree(null); }
    render(st);
  }

  // кінець оберту: лишок — від сервера (script.free); відмова (скінчились, не акаунт) — правда у виді
  function afterSpin(st, script) {
    if (!st.gmode) return;
    const k = st.inst.ctx, x = script && script.free;
    if (!x) {
      const f = fv(st);
      if (!f || f.left <= 0) leave(st);
      return;
    }
    st.gift.left = x.left;
    if (x.left <= 0) {
      k.auto = 0;
      k.say('🎁 Безкоштовні на сьогодні скінчились — завтра нові', 4000);
      st.gift.timer = setTimeout(() => { if (st.gmode && !k.busy) leave(st); }, 1500);
    } else k.setFree({ bet: x.bet, left: x.left });
    render(st);
  }

  function mount(st) {
    if (st.gift || !st.wrap || !st.wrap.parentNode) return;
    const bar = document.createElement('div');
    bar.className = 'slot-tour slot-gift';
    bar.hidden = true;
    st.wrap.parentNode.insertBefore(bar, st.wrap);
    st.gift = { bar, left: 0, timer: 0 };
    bar.addEventListener('click', (e) => {
      const b = e.target.closest && e.target.closest('[data-g]');
      if (!b) return;
      if (b.dataset.g === 'enter') enter(st);
      else if (b.dataset.g === 'exit') { if (!(st.inst && st.inst.ctx.busy)) leave(st); }
    });
    if (st.inst) st.inst.ctx.on('spinEnd', (script) => afterSpin(st, script));
    render(st);
    // прийшли з 🎁 у шапці чи з картки в «Сьогодні» (HGames.openGift) — одразу в режим «безкоштовно»
    let want = false;
    try { want = sessionStorage.getItem('slots.gift') === '1'; sessionStorage.removeItem('slots.gift'); } catch (e) { /* приватне вікно */ }
    if (want) setTimeout(() => enter(st), 300);
  }

  function update(st) {
    if (!st.gift) return;
    if (st.gmode && st.inst && (st.inst.ctx.busy || st.waitSpin)) return;   // лічильник оновить afterSpin
    render(st);
  }

  function unmount(st) {
    if (st.gift && st.gift.timer) clearTimeout(st.gift.timer);
    if (st.gift && st.gift.bar) st.gift.bar.remove();
    st.gift = null;
    st.gmode = false;
  }

  window.SlotGift = { mount, update, unmount, leave };
})();
