/* Слоти «🎰 Азарт» на сайті: один модуль на всі автомати (сервер дає Client: "slot").
   Програвач і HUD — SlotKit (web/games/slots/kit.js), автомат — slots/<id>.js + арт <id>-art.js; усе вантажимо
   раз на сторінку. Правду каже сервер (docs/games/specs/slots.md): оберт — act('spin', {bet, seq}) і вид з новим
   last.seq, Ворожка — act('gamble', {pick}) / act('collect'), баланс — view.balance. Старий last при відкритті не
   програємо — лише ставимо його поле. Скарбничку й «заноси тижня» в тікері опитуємо /api/slots/feed раз на 7 с,
   поки автомат відкритий і вкладку видно. Тости гаманця «slot-…» поки відкритий автомат — тихі (свято робить кіт). */
(function () {
  'use strict';
  const BASE = '/games/slots/';
  const IDS = ['slot-glek', 'slot-cascade', 'slot-hold', 'slot-cluster'];
  const TITLES = { 'slot-glek': 'Однорукий Глек', 'slot-cascade': 'Розбиті глеки', 'slot-hold': 'Козацький скарб', 'slot-cluster': 'Цвіт папороті' };
  const FEED_MS = 7000;
  const roots = new Map();   // root картки → стан
  const files = {};          // url → Promise

  // ---------- підвантаження kit + арт + автомат ----------
  const ver = () => {
    const c = window.HGames && HGames.catalog;
    const v = c && c.files && c.files['games/slot.js'];
    return v ? '?v=' + encodeURIComponent(v) : '';
  };
  function css(name) {
    const url = BASE + name + ver();
    if (!files[url]) files[url] = new Promise((res) => {
      const l = document.createElement('link'); l.rel = 'stylesheet'; l.href = url;
      l.onload = l.onerror = () => res(); document.head.appendChild(l);
    });
    return files[url];
  }
  function js(name) {
    const url = BASE + name + ver();
    if (!files[url]) files[url] = new Promise((res, rej) => {
      const s = document.createElement('script'); s.src = url; s.async = false;
      s.onload = () => res(); s.onerror = () => { delete files[url]; rej(new Error('не завантажився ' + name)); };
      document.head.appendChild(s);
    });
    return files[url];
  }
  async function load(id) {
    css('kit.css'); css(id + '-art.css'); const c = css(id + '.css');
    await js('kit.js');
    await Promise.all([js(id + '-art.js'), js(id + '.js'), c]);
    if (!window.SlotKit || !SlotKit.machines || !SlotKit.machines[id]) throw new Error('нема автомата ' + id);
    return SlotKit.machines[id];
  }

  // ---------- тихий гаманець ----------
  function quiet(on) { if (window.HGames && HGames.quietWallet) HGames.quietWallet('slot-', on); }
  function release() { if (window.HGames && HGames.releaseWallet) HGames.releaseWallet(); }

  // ---------- Скарбничка й заноси (тікер у автоматі) ----------
  let feedT = 0;
  const fmt = (n) => Math.round(n || 0).toLocaleString('uk-UA').replace(/,/g, ' ');
  const fmtX = (x) => (Math.round((x || 0) * 10) / 10).toLocaleString('uk-UA', { maximumFractionDigits: 1 }).replace(/\s/g, ' ');
  function feedLines(f) {
    const out = (f.wins || []).slice(0, 12).map((w) => w.jackpot
      ? 'Скарбничка Глека — до ' + w.nick + ': ' + fmt(w.jackpot) + ' 🏺'
      : w.nick + ' — +' + fmt(w.win) + ' 🏺 у «' + (TITLES[w.game] || w.game) + '» · ×' + fmtX(w.mult));
    out.push('Глек каже: крутіть, черепки самі не розіб’ються');
    return out;
  }
  async function pollFeed() {
    if (document.hidden || !roots.size || !window.SlotKit) return;
    // Автомат схований за лобі (картка лишається змонтованою) — тікер ніхто не бачить, не питаємо.
    let seen = false;
    roots.forEach((st, root) => { if (root.offsetParent) seen = true; });
    if (!seen) return;
    try {
      const r = await fetch('/api/slots/feed', { cache: 'no-store' });
      if (!r.ok) return;
      const f = await r.json();
      SlotKit.setLive({ jackpot: f.jackpot, mustHit: f.mustHit || 0, lines: feedLines(f), on: f.on !== false });
    } catch (e) { /* мережа — наступного разу */ }
  }
  function feedOn() { if (!feedT) { feedT = setInterval(pollFeed, FEED_MS); pollFeed(); } }
  function feedOff() { if (feedT && !roots.size) { clearInterval(feedT); feedT = 0; } }
  const onVis = () => { if (!document.hidden && roots.size) pollFeed(); };
  // Повернулись із лобі до автомата — тікер свіжий одразу, а не за 7 с.
  window.addEventListener('hashchange', () => setTimeout(() => { if (roots.size) pollFeed(); }, 50));
  document.addEventListener('visibilitychange', onVis);
  // Подія гаманця сайту (core.js): ачівки й решта приходів теж у балансі автомата. Під час оберту не чіпаємо —
  // кіт сам знімає ставку й додає виграш, а по кінці (spinEnd) ставимо останнє від гаманця.
  let walletBal = null;
  // живий баланс шапки: остання подія гаманця або HGames.wallet; view.balance — лише на мить останньої дії автомата
  // (після перезавантаження міг устаріти: Скарбничка, ачівки, інші вкладки)
  const liveBal = () => (walletBal != null ? walletBal : window.HGames && HGames.wallet != null ? HGames.wallet : null);
  document.addEventListener('hgames:wallet', (e) => {
    const b = e.detail && e.detail.balance;
    if (b == null) return;
    walletBal = b;
    roots.forEach((st) => { if (st.inst && !st.inst.busy && !st.waitSpin) st.inst.ctx.setBalance(b); });
  });

  // ---------- сервер замість моку ----------
  function makeApi(st) {
    return {
      spin(bet, state, kctx) {
        const v = st.ctx.view || {};
        const seq = v.last ? v.last.seq : 0;
        return new Promise((res) => {
          let t = 0;
          const done = (script) => {
            clearTimeout(t); st.waitSpin = null;
            if (!script) { if (kctx) kctx.auto = 0; res({ bet, steps: [], win: 0, state: state || {} }); return; }
            res(script);
          };
          t = setTimeout(() => done(null), 12000);
          st.waitSpin = { seq, done };
          st.ctx.act('spin', { bet, seq }).then((r) => { if (!r || r.ok === false) done(null); else check(st); });
        });
      },
      gamble(pick) {
        const g0 = (st.ctx.view && st.ctx.view.gamble) || {};
        return new Promise((res) => {
          let t = 0;
          const done = (g) => { clearTimeout(t); st.waitG = null; res(g); setTimeout(release, 1200); };   // карта відкрилась — тоді й шапка
          t = setTimeout(() => done({ open: g0.open !== false }), 9000);
          st.waitG = { n: g0.n || 0, done };
          st.ctx.act('gamble', { pick }).then((r) => {
            if (!r || r.ok === false) { const g = st.ctx.view && st.ctx.view.gamble; done({ open: !!(g && g.open) }); } else check(st);
          });
        });
      },
      collect() { return st.ctx.act('collect'); },
    };
  }
  // свіжий вид: дочекані оберт / хід Ворожки, баланс, Скарбничка
  function check(st) {
    const v = st.ctx.view || {};
    if (v.jackpot != null && window.SlotKit && SlotKit.setLive) SlotKit.setLive({ jackpot: v.jackpot });
    const L = v.last;
    if (st.waitSpin && L && L.seq > st.waitSpin.seq) { st.seenSeq = L.seq; st.waitSpin.done(L.script); }
    else if (L && L.seq > st.seenSeq) st.seenSeq = L.seq;   // оберт з іншої вкладки — не програємо
    const g = v.gamble;
    if (st.waitG && g && (g.n || 0) > st.waitG.n) st.waitG.done(Object.assign({}, g, { balance: v.balance }));
    if (st.inst && !st.inst.busy && !st.waitSpin) { const b = liveBal() != null ? liveBal() : v.balance; if (b != null) st.inst.ctx.setBalance(b); }
  }

  // ---------- висота: автомат на всю ігрову зону ----------
  function fitH(st) {
    const r = st.wrap.getBoundingClientRect();
    if (!r.width) return;
    const top = Math.max(0, r.top + window.scrollY);
    st.wrap.style.setProperty('--slot-top', Math.round(Math.min(top, 260)) + 'px');
  }

  async function mount(root, ctx) {
    root.classList.add('slot-card');
    root.innerHTML = '<div class="slotwrap"><div class="slot-load muted">Глек протирає барабани…</div></div>';
    const st = { ctx, wrap: root.firstChild, inst: null, seenSeq: (ctx.view && ctx.view.last && ctx.view.last.seq) || 0, dead: false, waitSpin: null, waitG: null, placed: false };
    roots.set(root, st);
    quiet(true);
    st.onResize = () => fitH(st);
    window.addEventListener('resize', st.onResize);
    requestAnimationFrame(() => fitH(st));
    const id = ctx.room.game;
    let machine;
    try { machine = await load(id); } catch (e) {
      console.warn('[slot]', e);
      if (!st.dead) st.wrap.innerHTML = '<div class="gempty">Ой-йой, автомат не завантажився. Онови сторінку.</div>';
      return;
    }
    if (st.dead) return;
    const v = st.ctx.view || {};
    // ⓘ і табло — з view.table сервера (кожен автомат має _setPay), стеля для банера — machine.table.cap
    if (v.table) { machine.table = v.table; if (v.table.pay && machine._setPay) machine._setPay(v.table.pay, v.table); }
    st.wrap.innerHTML = '';
    if (v.jackpot != null) SlotKit.setLive({ jackpot: v.jackpot });
    if (!SlotKit.live.lines) SlotKit.setLive({ lines: [] });
    st.inst = SlotKit.mount(st.wrap, id, { balance: liveBal() != null ? liveBal() : v.balance != null ? v.balance : 0, bet: v.bet, bets: v.bets, api: makeApi(st) });
    // поле останнього оберту — без програвання
    const sp = v.last && v.last.script && (v.last.script.steps || []).find((s) => s.t === 'spin' || s.t === 'set');
    const place = () => {
      if (st.placed || !sp || !st.inst || !st.inst.ctx.reels) return;
      st.placed = true;
      try { st.inst.ctx.reels.set(sp); } catch (e) { /* інше поле — лишаємо випадкове */ }
    };
    place();
    st.inst.ctx.on('resize', place);
    // Ворожка лишилась відкритою (перезавантаження посеред неї) — кнопка знову з тією сумою
    if (v.gamble && v.gamble.open && machine._resumeGamble) machine._resumeGamble(st.inst.ctx, v.gamble);
    st.inst.ctx.on('spinEnd', () => { release(); const b = liveBal() != null ? liveBal() : st.ctx.view && st.ctx.view.balance; if (b != null) st.inst.ctx.setBalance(b); });
    fitH(st);
    feedOn();
  }

  function update(root, ctx) {
    const st = roots.get(root);
    if (!st) return;
    st.ctx = ctx;
    check(st);
  }

  function unmount(root) {
    const st = roots.get(root);
    if (!st) return;
    st.dead = true;
    if (st.waitSpin) st.waitSpin.done(null);
    if (st.waitG) st.waitG.done({ open: false });
    window.removeEventListener('resize', st.onResize);
    try { if (st.inst) st.inst.destroy(); } catch (e) { console.warn('[slot] destroy', e); }
    roots.delete(root);
    if (!roots.size) quiet(false);
    feedOff();
  }

  function status(ctx) {
    const v = ctx.view || {};
    if (v.on === false) return 'Автомати на перерві';
    return v.jackpot != null ? '🏺 Скарбничка ' + fmt(v.jackpot) : '';
  }

  const svg = (body) => '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">' + body + '</svg>';
  const ICONS = {
    // ретро-автомат з ручкою
    'slot-glek': svg('<rect x="1.6" y="3" width="10.4" height="11" rx="1.8" fill="none" stroke="var(--accent)" stroke-width="1.4"/>'
      + '<rect x="3.4" y="6" width="6.8" height="4" rx=".6" fill="var(--clay)"/><path d="M5.7 6v4M7.9 6v4" stroke="var(--accent)" stroke-width=".7"/>'
      + '<path d="M13.2 4.5v5.2" stroke="var(--accent)" stroke-width="1.2" stroke-linecap="round"/><circle cx="13.2" cy="3.6" r="1.4" fill="var(--clay)"/>'),
    // розбитий глек з черепками
    'slot-cascade': svg('<path d="M5 2.4h6l-.6 2c2 1.2 3 3 3 5 0 2.8-2.4 4.6-5.4 4.6S2.6 12.2 2.6 9.4c0-2 1-3.8 3-5z" fill="none" stroke="var(--accent)" stroke-width="1.4" stroke-linejoin="round"/>'
      + '<path d="M8 5.4 6.8 8.2l2.1 1.2-1 3" fill="none" stroke="var(--clay)" stroke-width="1.1" stroke-linecap="round" stroke-linejoin="round"/>'),
    // скриня з дукатом
    'slot-hold': svg('<path d="M2 7h12v6.4H2zM2 7c0-2.6 2.4-4 6-4s6 1.4 6 4" fill="none" stroke="var(--accent)" stroke-width="1.4" stroke-linejoin="round"/>'
      + '<circle cx="8" cy="9.6" r="2" fill="var(--clay)"/><path d="M2 9.6h4M10 9.6h4" stroke="var(--accent)" stroke-width=".9"/>'),
    // квітка папороті
    'slot-cluster': svg('<g fill="var(--clay)"><circle cx="8" cy="3.6" r="2"/><circle cx="12.2" cy="6.8" r="2"/><circle cx="10.6" cy="11.8" r="2"/>'
      + '<circle cx="5.4" cy="11.8" r="2"/><circle cx="3.8" cy="6.8" r="2"/></g><circle cx="8" cy="8" r="2.1" fill="var(--accent)"/>'),
  };

  const api = { mount, update, unmount, status, added: '2026-10-09' };
  if (window.HGames) for (const id of IDS) HGames.register(Object.assign({ id, icon: ICONS[id] }, api));
})();
