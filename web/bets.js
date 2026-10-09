/*
  «🎲 Ставки» — window.HBets (09.10.2026, контракт D:/or-wt/_tools/bets-contract.md §6). Банкує Дядько Глек: кеф
  фіксується в мить ставки, зіграло — Глек платить ставка × кеф, ні — ставка згорає. Тут лише сторінка подій;
  ставки на столах живуть у картці столу (web/games/bets-table.js).

  Сторінка — панель розділу ігор (HGames.registerPanel, рішення 09.10): у каталозі «🎰 Азарт» → тема «🎲 Ставки» стоїть
  плитка «Ставки на події» (скільки відкритих подій, адміну — кружечок пропозицій), а сама сторінка — #games/x:bets.
  Вкладки: «Події» (#games/x:bets), «Мої ставки» (…/mine), «Глек у мінусі» (…/glek), «💡 Запропонувати» (…/suggest),
  адміну — «🛠 Керування» (…/admin/<pm|list|inbox>). Старі адреси #bets/… app.js перекидає сюди.

  Сервер — src/Hlechyky/Bets/ (BetEvents.cs): GET /api/bets (голий об'єкт), /api/bets/glek (голий), /api/bets/mine і
  /api/bets/admin ({ok,message,data}); POST /api/bets/events/{id}/bet { option, stake, odds, key } — odds = кеф, який
  людина бачила (змінився → 409 з data.odds), key — повтор того самого кліку не спише вдруге; адмін: POST/PUT
  /api/bets/events, /{id}/status|settle|cancel, GET /{id}/pm (свіжі ціни); огляд Polymarket — /api/bets/pm/feed і
  /api/bets/pm/event/{slug}; пропозиції — /api/bets/suggest, /suggest/{id}/add|reject. Хаб: betEvents {} — події
  змінились, betMine { text } — щось про мої ставки чи пропозиції, betSuggest { count } — адміну, скільки пропозицій чекає.
  Правила й гроші — на сервері; тут лише малюємо й питаємо.
*/
(() => {
  'use strict';

  let o = null;                        // що дає app.js
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  let data = null;                     // останнє GET /api/bets
  let mine = null;                     // останнє GET /api/bets/mine (data)
  let glek = null;                     // останнє GET /api/bets/glek
  let adm = null;                      // останнє GET /api/bets/admin (data)
  let hostEl = null;                   // куди змонтувала панель core.js (.gxpanel)
  let tab = 'events';
  let admTab = 'pm';
  let pending = 0;                     // пропозицій чекає (адміну)
  let pick = null;                     // { ev, opt, stake, key } — відкрита панель ставки
  let late = false;                    // свіже прийшло, поки людина вписувала число: перемалюємо, щойно відпустить поле
  let reloadT = 0;
  const news = [];                     // останні рядки betMine — над подіями, щоб не загубились після тосту
  const feeds = {};                    // огляд Polymarket: 'adm' (керування) і 'sug' (пропозиції) — у кожного свій
  let ed = null;                       // редактор події (вікно)

  const TABS = [['events', '🎲 Події'], ['mine', '🧾 Мої ставки'], ['glek', '📉 Глек у мінусі'], ['suggest', '💡 Запропонувати'], ['admin', '🛠 Керування']];
  const ADM_TABS = [['pm', '🔭 Polymarket'], ['list', '📋 Події'], ['inbox', '📥 Пропозиції']];
  const STATUS = {
    draft: ['📝 чернетка', ''], open: ['🟢 приймає ставки', 'ok'], paused: ['⏸ призупинено', 'warn'],
    closed: ['🔒 чекає результату', 'warn'], settled: ['🏁 розраховано', ''], cancelled: ['✕ скасовано', 'err'],
  };

  // ---------------------------------------------------------------- дрібниці

  const num = (n) => Number(n || 0).toLocaleString('uk-UA');
  const kef = (x) => '×' + String(Math.round(Number(x || 0) * 100) / 100).replace('.', ',');
  const pct = (p) => (p == null ? '' : p > 0 && p < 0.01 ? '<1%' : p < 1 && p > 0.99 ? '>99%' : Math.round(p * 100) + '%');
  /// Як BetMath.Payout на сервері: униз до цілого, але не менше ставки.
  const payout = (stake, k) => Math.max(stake, Math.floor(stake * k + 1e-9));
  const signed = (n) => (n > 0 ? '+' + num(n) : n < 0 ? '−' + num(-n) : '0');
  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const plural = (n, one, few, many) => {
    const t = Math.abs(n) % 100, u = t % 10;
    return t >= 11 && t <= 14 ? many : u === 1 ? one : u >= 2 && u <= 4 ? few : many;
  };
  const newKey = () => (window.crypto && crypto.randomUUID ? crypto.randomUUID() : Date.now().toString(36) + Math.random().toString(36).slice(2));
  const pad2 = (n) => String(n).padStart(2, '0');

  /// «12.10 о 21:00» (рік — лише коли не цей).
  function when(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (isNaN(d)) return '';
    const y = d.getFullYear() !== new Date().getFullYear() ? '.' + d.getFullYear() : '';
    return pad2(d.getDate()) + '.' + pad2(d.getMonth() + 1) + y + ' о ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  }
  /// «ще 3 год», «ще 2 дні», «ще 15 хв» — скільки лишилось до кінця прийому.
  function left(iso) {
    const ms = Date.parse(iso) - Date.now();
    if (!(ms > 0)) return 'час вийшов';
    const m = Math.round(ms / 60000);
    if (m < 60) return 'ще ' + Math.max(1, m) + ' хв';
    const h = Math.round(m / 60);
    if (h < 48) return 'ще ' + h + ' год';
    const d = Math.round(h / 24);
    return 'ще ' + d + ' ' + plural(d, 'день', 'дні', 'днів');
  }
  /// Для поля datetime-local — місцевий час без поясу.
  function localInput(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (isNaN(d)) return '';
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) + 'T' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  }
  function usd(v) {
    v = Number(v || 0);
    return v >= 1e6 ? (v / 1e6).toFixed(1).replace('.', ',') + ' млн $' : v >= 1e3 ? Math.round(v / 1e3) + ' тис $' : Math.round(v) + ' $';
  }
  const lim = () => (data && data.limits) || { min: 1, maxEvent: 0, minOdds: 1.05, maxOdds: 100, margin: 0.08 };
  const clampOdds = (x) => Math.min(lim().maxOdds, Math.max(lim().minOdds, Math.round(x * 100) / 100));
  const oddsOf = (p) => (p > 0 ? clampOdds((1 - lim().margin) / p) : lim().maxOdds);
  const parseNum = (s) => { const v = parseFloat(String(s || '').replace(',', '.').replace(/[^\d.]/g, '')); return isNaN(v) ? null : v; };
  const isAdmin = () => !!(data ? data.admin : o && o.me.role === 'admin');
  const isAcc = () => !!(data ? data.account : o && o.me.account);
  const HREF = '#games/x:bets';
  const root = () => (hostEl && hostEl.isConnected ? hostEl : null);
  /// Сторінку видно: панель змонтована й розділ ігор відкритий (core.js панелі не каже, що її сховали).
  const isShown = () => !!root() && document.body.classList.contains('route-games');
  const eventsOn = () => !!(o && o.me.bets && o.me.bets.events);

  async function get(path) {
    const r = await o.api('GET', path);
    return r && r.ok !== undefined && r.data !== undefined ? r.data : r;
  }

  // ---------------------------------------------------------------- завантаження

  async function loadMain() { data = await get('/api/bets'); if (data.pendingSuggestions != null) setPending(data.pendingSuggestions); paintTile(); }
  async function loadTab() {
    if (tab === 'mine' && isAcc()) mine = await get('/api/bets/mine?limit=200');
    else if (tab === 'glek') glek = await get('/api/bets/glek');
    else if (tab === 'admin' && isAdmin()) { adm = await get('/api/bets/admin'); setPending((adm.suggestions || []).length); }
  }
  async function reload() {
    try { await loadMain(); await loadTab(); } catch (e) { if (!data) { fail(e); return; } }
    paint(true);
  }
  /// Подія хаба сиплеться пачкою (ставка — одразу кілька людей) — перечитуємо раз, коли вщухне.
  function soon() {
    clearTimeout(reloadT);
    // сторінки не видно — однаково перечитуємо: плитка в каталозі показує, скільки подій відкрито
    reloadT = setTimeout(() => { if (isShown()) reload(); else if (eventsOn()) loadMain().catch(() => {}); else data = null; }, 400);
  }
  function fail(e) {
    const r = root();
    if (r) r.innerHTML = '<section class="panel"><div class="gempty glek">Глек розгубив записи: ' + esc(e.message) + '</div></section>';
  }

  // ---------------------------------------------------------------- сторінка

  async function render(tail) {
    const parts = String(tail || '').split('/').map(decodeURIComponent);
    const want = TABS.some(([k]) => k === parts[0]) ? parts[0] : 'events';
    tab = want;
    if (tab === 'admin' && ADM_TABS.some(([k]) => k === parts[1])) admTab = parts[1];
    const r = root();
    if (!r) return;
    if (o.me.bets && !eventsOn() && !isAdmin()) { r.innerHTML = '<section class="panel"><div class="gempty glek">Ставки на події зараз вимкнено.</div></section>'; return; }
    if (data) paint(); else r.innerHTML = '<div class="gwait"><span class="spin"></span> Глек гортає зошит зі ставками…</div>';
    // головне — щоразу свіже (баланс, нові події), але поки летить, людина бачить те, що вже було
    try { await loadMain(); await loadTab(); } catch (e) { if (!data) { fail(e); return; } o.toast(e.message, 'err'); }
    if (!isShown()) return;
    if (tab === 'admin' && !isAdmin()) { o.go(HREF); return; }
    paint();
    if (tab === 'suggest' && isAcc() && !feedState('sug').asked) loadFeed('sug');
    if (tab === 'admin' && admTab === 'pm' && !feedState('adm').asked) loadFeed('adm');
  }

  function typing() {
    const a = document.activeElement, r = root();
    return !!(a && r && r.contains(a) && a.matches('input, textarea, select'));
  }

  /// soft — свіже з хаба: поки людина вписує суму чи питання, сторінку під пальцями не перемальовуємо (і фокус не губимо),
  /// а лише запам'ятовуємо й домальовуємо, щойно відпустить поле. Те, що зробила сама людина, малюємо одразу.
  function paint(soft) {
    const r = root();
    if (!r || !data || !isShown()) return;
    if (soft && typing()) { late = true; return; }
    late = false;
    const head = '<section class="panel bt-head">'
      + '<div class="bt-top"><div class="bt-sign"><img src="/static/glek.svg" alt=""><div><h2>Ставки в Дядька Глека</h2>'
      + '<div class="muted small">Кеф фіксується, щойно ставиш. Зіграло — Глек платить ставка × кеф, ні — черепки лишаються в глечику.</div></div></div>'
      + (data.account ? '<div class="bt-bal">У глечику <b>' + num(data.balance) + ' 🏺</b></div>' : '') + '</div>'
      + (news.length ? '<div class="bt-news">' + news.map((x) => '<div>' + esc(x.text) + ' <span class="muted small">' + pad2(x.at.getHours()) + ':' + pad2(x.at.getMinutes()) + '</span></div>').join('') + '</div>' : '')
      + '<nav class="bt-tabs" aria-label="Ставки">' + TABS.filter(([k]) => k !== 'admin' || data.admin).map(([k, l]) =>
        '<button type="button" data-go="' + HREF + (k === 'events' ? '' : '/' + k) + '"' + (k === tab ? ' class="on"' : '') + '>' + l
        + (k === 'admin' && pending ? ' <span class="chip badge">' + pending + '</span>' : '') + '</button>').join('') + '</nav>'
      + '</section>';
    const body = tab === 'mine' ? mineHtml() : tab === 'glek' ? glekHtml() : tab === 'suggest' ? suggestHtml()
      : tab === 'admin' ? adminHtml() : eventsHtml();
    r.innerHTML = head + body;
    wire(r);
  }

  // ---------------------------------------------------------------- «Події»

  function lockHtml(what) {
    return '<div class="bt-lock">🔒 ' + what + ' — для акаунтів: гостьовий нік може зайняти хтось інший, і черепки пропали б.'
      + ' <button type="button" class="primary" data-acc>Закріпити нік</button></div>';
  }

  function eventsHtml() {
    const list = (data.events || []).filter((e) => e.status !== 'draft');
    const done = data.done || [];
    let h = '<section class="panel bt-sect">';
    if (!data.on.events) h += '<div class="bt-lock">Ставки на події зараз вимкнено.</div>';
    if (!data.account) h += lockHtml('Ставки');
    h += list.length ? '<div class="bt-evs">' + list.map(eventCard).join('') + '</div>'
      : '<div class="gempty glek">Поки нема на що ставити — Глек ще не вигадав подій. Маєш ідею? Глянь «💡 Запропонувати».</div>';
    h += '</section>';
    if (done.length) {
      h += '<section class="panel bt-sect"><h3>🏁 Нещодавно розраховані</h3><div class="bt-done">' + done.map((e) => {
        const win = (e.options || []).find((x) => x.key === e.winner);
        const my = (e.mine || []).filter((b) => b.status !== 'back');
        const myNet = my.reduce((s, b) => s + (b.status === 'won' ? b.payout - b.stake : b.status === 'lost' ? -b.stake : 0), 0);
        return '<div class="bt-donerow"><div class="bt-dn-t"><b>' + esc(e.title) + '</b>'
          + (e.status === 'cancelled' ? '<span class="chip err">скасовано</span>' : win ? '<span class="chip ok">зіграло: ' + esc(win.title) + '</span>' : '') + '</div>'
          + '<div class="muted small">' + num(e.bets) + ' ' + plural(e.bets, 'ставка', 'ставки', 'ставок')
          + (e.glek != null ? ' · Глек ' + signed(e.glek) + ' 🏺' : '') + (my.length ? ' · твоє: <b class="' + (myNet >= 0 ? 'bt-plus' : 'bt-minus') + '">' + signed(myNet) + ' 🏺</b>' : '')
          + (e.settledAt ? ' · ' + when(e.settledAt) : '') + '</div></div>';
      }).join('') + '</div></section>';
    }
    return h;
  }

  function eventCard(e) {
    const acc = data.account;
    const st = STATUS[e.status] || ['', ''];
    const timeChip = e.accepting
      ? (e.closesAt ? '<span class="chip" title="Ставки приймаються до ' + esc(when(e.closesAt)) + '">⏳ до ' + esc(when(e.closesAt)) + ' · ' + left(e.closesAt) + '</span>' : '<span class="chip ok">приймає ставки</span>')
      : '<span class="chip ' + st[1] + '">' + (e.status === 'open' ? '⌛ прийом закрито' : st[0]) + '</span>';
    const p = pick && pick.ev === e.id ? pick : null;
    const opts = '<div class="bt-opts">' + (e.options || []).map((x) =>
      '<button type="button" class="bt-opt' + (p && p.opt === x.key ? ' on' : '') + '" data-ev="' + e.id + '" data-opt="' + esc(x.key) + '"'
      + (e.accepting ? '' : ' disabled') + ' aria-pressed="' + !!(p && p.opt === x.key) + '">'
      + '<span class="bt-ot">' + esc(x.title) + '</span><b class="bt-odds">' + kef(x.odds) + '</b>'
      + (x.worldP != null ? '<span class="bt-world" title="Polymarket: ймовірність, коли адмін брав ціни">світ думає ' + pct(x.worldP) + '</span>' : '<span class="bt-world"></span>')
      + '</button>').join('') + '</div>';
    const myBets = (e.mine || []);
    const mineHtml = myBets.length ? '<div class="bt-mybets"><span class="muted small">Мої ставки:</span>' + myBets.map((b) => {
      const t = (e.options.find((x) => x.key === b.option) || {}).title || b.option;
      return '<span class="bt-mb st-' + esc(b.status) + '">' + num(b.stake) + ' 🏺 на «' + esc(t) + '» ' + kef(b.odds)
        + (b.status === 'open' ? ' → ' + num(b.payout) : b.status === 'won' ? ' ✓ +' + num(b.payout) : b.status === 'back' ? ' · повернуто' : ' · не зіграло') + '</span>';
    }).join('') + '</div>' : '';
    return '<article class="bt-ev" data-evcard="' + e.id + '">'
      + '<div class="bt-ev-h"><h3>' + esc(e.title) + '</h3>' + timeChip + '</div>'
      + (e.description ? '<div class="bt-desc muted small" data-desc title="Розгорнути">' + esc(e.description) + '</div>' : '')
      + opts
      + (p && acc ? stakeHtml(e, p) : '')
      + mineHtml
      + '<div class="bt-ev-f muted small"><span>' + (e.bets ? num(e.bets) + ' ' + plural(e.bets, 'ставка', 'ставки', 'ставок') + ' · ' + num(e.staked) + ' 🏺 у грі' : 'ще ніхто не ставив — будь першим')
      + '</span>' + (e.pmUrl ? '<a href="' + esc(e.pmUrl) + '" target="_blank" rel="noopener noreferrer">Polymarket ↗</a>' : '') + '</div>'
      + '</article>';
  }

  /// Скільки ще можна поставити на цю подію: стеля на людину мінус уже поставлене (і не більше, ніж є в глечику).
  function room(e) {
    const L = lim();
    const already = (e.mine || []).filter((b) => b.status === 'open').reduce((s, b) => s + b.stake, 0);
    const cap = L.maxEvent > 0 ? Math.max(0, L.maxEvent - already) : Infinity;
    return Math.min(cap, data.balance || 0);
  }

  function stakeHtml(e, p) {
    const x = (e.options || []).find((y) => y.key === p.opt);
    if (!x) return '';
    const L = lim();
    const max = room(e);
    const stake = +p.stake || 0;
    p.shown = x.odds;   // саме цей кеф людина бачить — його й шлемо: змінився на сервері → 409 і питаємо ще раз
    const quick = [10, 50, 100, 500].filter((v) => v <= max && v >= L.min);
    const capNote = L.maxEvent > 0 ? ' · на цю подію — до ' + num(L.maxEvent) + ' 🏺 разом' : '';
    return '<div class="bt-stake" data-stake-ev="' + e.id + '">'
      + '<div class="bt-pickline">На «<b>' + esc(x.title) + '</b>» ' + kef(x.odds) + (p.seen != null && Math.abs(p.seen - x.odds) > 0.004 ? ' <span class="chip warn" title="Кеф змінився, поки ти думав">було ' + kef(p.seen) + '</span>' : '') + '</div>'
      + (max < L.min
        ? '<div class="bt-lock">' + (data.balance < L.min ? 'Халепа: у глечику замало черепків. Вони капають за радіо, партії й щоденний глек.' : 'На цю подію ти вже поставив по стелю — Глек більше не приймає.') + '</div>'
        : '<div class="by-own-row"><label class="by-own-f"><input type="text" inputmode="numeric" autocomplete="off" data-stake placeholder="' + L.min + '" aria-label="Скільки черепків" value="' + (stake || '') + '"><span>🏺</span></label></div>'
          + '<div class="bt-quick">' + quick.map((v) => '<button type="button" class="chip" data-q="' + v + '">' + num(v) + '</button>').join('')
          + (max !== Infinity && max > 0 ? '<button type="button" class="chip" data-q="' + max + '">усе — ' + num(max) + '</button>' : '') + '</div>'
          + '<div class="bt-win" data-win>' + winText(stake, x.odds) + '</div>'
          + '<div class="muted small">Від ' + num(L.min) + ' 🏺' + capNote + '. Скасувати ставку не можна — кеф уже твій.</div>'
          + '<div class="bt-row"><button type="button" class="primary" data-place>🎲 Бахнути ставку</button><button type="button" class="ghost" data-unpick>Передумав</button></div>')
      + '</div>';
  }
  const winText = (stake, k) => (stake > 0 ? 'Можливий виграш <b>' + num(payout(stake, k)) + ' 🏺</b> <span class="muted small">(чистими +' + num(payout(stake, k) - stake) + ')</span>' : 'Впиши, скільки ставиш');

  async function place(btn) {
    if (!pick) return;
    const e = (data.events || []).find((x) => x.id === pick.ev);
    const x = e && e.options.find((y) => y.key === pick.opt);
    if (!x) return;
    const stake = Math.floor(+pick.stake || 0);
    const L = lim();
    if (stake < L.min) { o.toast('Ставка — від ' + num(L.min) + ' 🏺', 'err'); return; }
    await o.busy(btn, 'ставлю…', async () => {
      try {
        const r = await o.api('POST', '/api/bets/events/' + e.id + '/bet', { option: x.key, stake, odds: pick.shown != null ? pick.shown : x.odds, key: pick.key });
        if (r && r.data && r.data.balance != null) { data.balance = r.data.balance; paintWallet(r.data.balance); }
        pick = null;
        await reload();
        // Ставку бахнули з клавіатури (Enter у полі суми) — фокус лишився в полі, і reload відклав перемальовку до blur.
        // Своя ставка — не чужа подія хаба: показуємо новий баланс і «мої ставки» одразу.
        paint();
      } catch (err) {
        const d = err.data && err.data.data;
        if (d && d.odds != null) {
          // Адмін якраз переписав кеф — ставка не пішла. Показуємо новий і питаємо ще раз (ключ той самий: ставки ж не було).
          const was = pick.shown != null ? pick.shown : x.odds;
          x.odds = d.odds;
          if (pick.seen == null) pick.seen = was;
          paint();
          const yes = await ask('Кеф змінився', 'Глек переглянув кеф на «<b>' + esc(x.title) + '</b>»: було ' + kef(was) + ', тепер <b>' + kef(d.odds) + '</b>.'
            + ' Ставимо ' + num(stake) + ' 🏺 за новим? Можливий виграш — ' + num(payout(stake, d.odds)) + ' 🏺.', 'Поставити за ' + kef(d.odds));
          if (yes) { const b = root() && root().querySelector('[data-place]'); if (b) place(b); }
          return;
        }
        o.toast(err.message, 'err');
      }
    });
  }

  /// Шапка сайту — одразу, не чекаючи події гаманця з хаба.
  function paintWallet(n) {
    const el = document.querySelector('#hdrWallet b');
    if (el && n != null) el.textContent = String(n);
  }

  // ---------------------------------------------------------------- «Мої ставки»

  function betRow(b, title) {
    const st = b.status === 'open' ? '<span class="chip warn">чекає · можна ' + num(b.payout) + '</span>'
      : b.status === 'won' ? '<span class="chip ok">зіграло +' + num(b.payout - b.stake) + '</span>'
      : b.status === 'lost' ? '<span class="chip err">не зіграло −' + num(b.stake) + '</span>'
      : '<span class="chip">повернуто</span>';
    const src = b.source === 'table' ? '🎮' : '🎲';
    return '<div class="bt-bet st-' + esc(b.status) + '"><div class="bt-bet-h"><span class="bt-src" title="' + (b.source === 'table' ? 'стіл' : 'подія') + '">' + src + '</span>'
      + '<span class="bt-bet-t">' + esc(b.label || title || '') + '</span>' + st + '</div>'
      + '<div class="muted small">' + num(b.stake) + ' 🏺 ' + kef(b.odds) + ' · ' + when(b.at) + (b.note ? ' · ' + esc(b.note) : '') + '</div></div>';
  }

  function mineHtml() {
    if (!data.account) return '<section class="panel bt-sect">' + lockHtml('Ставки') + '</section>';
    if (!mine) return '<section class="panel bt-sect"><div class="gwait"><span class="spin"></span> мить…</div></section>';
    const open = mine.open || [], hist = mine.history || [];
    const net = mine.net || 0;
    const inPlay = open.reduce((s, x) => s + x.bet.stake, 0);
    return '<section class="panel bt-sect"><div class="bt-net"><div><div class="muted small">Чисто проти Глека</div><b class="bt-big ' + (net >= 0 ? 'bt-plus' : 'bt-minus') + '">' + signed(net) + ' 🏺</b></div>'
      + '<div><div class="muted small">Зараз у грі</div><b class="bt-big">' + num(inPlay) + ' 🏺</b></div>'
      + '<div class="muted small bt-net-say">' + (net > 0 ? 'Глек на тебе косо поглядає.' : net < 0 ? 'Глек тобі вдячний — глечик повніший.' : hist.length ? 'Вийшов у нуль — Глек чухає потилицю.' : 'Ще не ставив — Глек чекає.') + '</div></div>'
      + '<h3>⏳ Чекають результату <span class="count">' + (open.length || '') + '</span></h3>'
      + (open.length ? '<div class="bt-bets">' + open.map((x) => betRow(x.bet, x.eventTitle)).join('') + '</div>' : '<div class="gempty">Нічого не чекає. <a href="' + HREF + '">Глянь події</a>.</div>')
      + '<h3>📜 Історія</h3>'
      + (hist.length ? '<div class="bt-bets">' + hist.map((x) => betRow(x.bet, x.eventTitle)).join('') + '</div>' : '<div class="gempty">Порожньо.</div>')
      + '</section>';
  }

  // ---------------------------------------------------------------- «Глек у мінусі»

  function glekSay(t) {
    if (t < -1000) return 'Ой-йой! Мене обдирають до нитки. Хто це робить — дивіться нижче, я все записав.';
    if (t < 0) return 'Отакої — я в мінусі. Нічого, ще відіграюсь.';
    if (t > 1000) return 'Хе-хе. Черепки самі котяться в глечик — дякую, любі.';
    if (t > 0) return 'Поки в плюсі. Але ви не розслабляйтесь — і я теж.';
    return 'Поки нуль. Ніхто не наважився обіграти Глека?';
  }

  function glekHtml() {
    if (!glek) return '<section class="panel bt-sect"><div class="gwait"><span class="spin"></span> Глек рахує черепки…</div></section>';
    const s = glek.saldo || {};
    const tile = (k, l) => {
      const v = s[k] || { total: 0, events: 0, tables: 0 };
      return '<div class="bt-tile' + (v.total < 0 ? ' neg' : v.total > 0 ? ' pos' : '') + '"><div class="muted small">' + l + '</div>'
        + '<b class="bt-huge">' + signed(v.total) + '</b><div class="muted small">події ' + signed(v.events) + ' · столи ' + signed(v.tables) + '</div></div>';
    };
    const all = (s.all && s.all.total) || 0;
    const people = (list, cls, empty) => list && list.length
      ? '<ol class="bt-top10">' + list.map((x) => '<li><span class="bt-nick">' + esc(x.nick) + '</span><b class="' + cls + '">' + signed(x.net) + ' 🏺</b><span class="muted small">' + num(x.bets) + ' ' + plural(x.bets, 'ставка', 'ставки', 'ставок') + '</span></li>').join('') + '</ol>'
      : '<div class="gempty">' + empty + '</div>';
    return '<section class="panel bt-sect bt-glek">'
      + '<div class="bt-glek-h"><img src="/static/glek.svg" alt=""><div><h3>' + (all < 0 ? 'Дядько Глек у мінусі на ' + num(-all) + ' 🏺' : all > 0 ? 'Дядько Глек поки в плюсі на ' + num(all) + ' 🏺' : 'Дядько Глек при своїх')
      + '</h3><div class="muted">«' + glekSay(all) + '»</div></div></div>'
      + '<div class="bt-tiles">' + tile('all', 'За весь час') + tile('month', 'За місяць') + tile('week', 'За тиждень') + '</div>'
      + '<div class="muted small">Плюс — Глек у плюсі, мінус — гравці загнали його в мінус. Рахується все: події й ставки на столах.</div>'
      + '</section>'
      + '<div class="bt-two">'
      + '<section class="panel bt-sect"><h3>😎 Хто обіграв Глека</h3>' + people(glek.beat, 'bt-plus', 'Ще ніхто. Глек непереможний… поки що.') + '</section>'
      + '<section class="panel bt-sect"><h3>🍯 Хто годує Глека</h3>' + people(glek.lost, 'bt-minus', 'Ніхто нічого не програв — Глек голодний.') + '</section>'
      + '</div>'
      + '<section class="panel bt-sect"><h3>💰 Найбільші виграші</h3>' + ((glek.biggest || []).length
        ? '<div class="bt-bets">' + glek.biggest.map((b) => '<div class="bt-bet st-won"><div class="bt-bet-h"><span class="bt-src">' + (b.source === 'table' ? '🎮' : '🎲') + '</span>'
          + '<span class="bt-bet-t"><b>' + esc(b.nick) + '</b> — ' + esc(b.label) + '</span><span class="chip ok">+' + num(b.net) + '</span></div>'
          + '<div class="muted small">' + num(b.stake) + ' 🏺 ' + kef(b.odds) + ' → ' + num(b.payout) + ' 🏺 · ' + when(b.at) + '</div></div>').join('') + '</div>'
        : '<div class="gempty">Ще ніхто не зірвав куш.</div>') + '</section>'
      + '<section class="panel bt-sect"><h3>📋 Події</h3>' + ((glek.events || []).length
        ? '<div class="bt-done">' + glek.events.map((e) => '<div class="bt-donerow"><div class="bt-dn-t"><b>' + esc(e.title) + '</b><span class="chip ' + ((STATUS[e.status] || [])[1] || '') + '">' + ((STATUS[e.status] || [e.status])[0]) + '</span></div>'
          + '<div class="muted small">' + num(e.bets) + ' ' + plural(e.bets, 'ставка', 'ставки', 'ставок') + ' · ' + num(e.staked) + ' 🏺'
          + (e.glek != null ? ' · Глек <b class="' + (e.glek >= 0 ? 'bt-plus' : 'bt-minus') + '">' + signed(e.glek) + '</b>' : '') + '</div></div>').join('') + '</div>'
        : '<div class="gempty">Подій зі ставками ще не було.</div>') + '</section>';
  }

  // ---------------------------------------------------------------- огляд Polymarket (і пропозиції, і керування)

  function feedState(ctx) { return feeds[ctx] || (feeds[ctx] = { cat: 'top', q: '', items: [], more: false, loading: false, err: '' }); }

  async function loadFeed(ctx, more) {
    const f = feedState(ctx);
    if (f.loading) return;
    f.loading = true; f.err = ''; f.asked = true;
    if (!more) { f.items = []; f.more = false; }
    paint();
    try {
      const qs = '?cat=' + encodeURIComponent(f.cat) + (f.q ? '&q=' + encodeURIComponent(f.q) : '') + '&offset=' + (more ? f.items.length : 0);
      const r = await get('/api/bets/pm/feed' + qs);
      f.cats = r.cats || f.cats;
      f.items = more ? f.items.concat(r.items || []) : (r.items || []);
      f.more = !!r.more;
    } catch (e) { f.err = e.message; }
    f.loading = false;
    paint();
  }

  function feedHtml(ctx) {
    const f = feedState(ctx);
    const cats = f.cats || (adm && adm.cats) || [];
    const btn = ctx === 'adm' ? '➕ Додати' : '💡 Запропонувати';
    return '<div class="bt-feed" data-feed="' + ctx + '">'
      + '<nav class="bt-cats">' + cats.map((c) => '<button type="button" data-cat="' + esc(c.id) + '"' + (!f.q && c.id === f.cat ? ' class="on"' : '') + '>' + esc(c.title) + '</button>').join('') + '</nav>'
      + '<form class="bt-find" data-find><input type="search" placeholder="знайти на Polymarket (англійською: Ukraine, Messi…)" autocomplete="off" value="' + esc(f.q) + '" aria-label="Пошук на Polymarket"><button type="submit">🔍</button></form>'
      + (f.err ? '<div class="gwait err">' + esc(f.err) + '</div>' : '')
      + (f.items.length ? '<div class="bt-pms">' + f.items.map((c) => '<div class="bt-pm">'
        + (c.image ? '<img src="' + esc(c.image) + '" alt="" loading="lazy" referrerpolicy="no-referrer">' : '<span class="bt-pm-noimg">🎲</span>')
        + '<div class="bt-pm-b"><a class="bt-pm-t" href="' + esc(c.url) + '" target="_blank" rel="noopener noreferrer">' + esc(c.title) + '</a>'
        + '<div class="muted small">' + (c.endDate ? 'до ' + when(c.endDate) + ' · ' : '') + 'обіг ' + usd(c.volume) + (c.markets > 1 ? ' · ринків: ' + c.markets : '') + '</div>'
        + ((c.top || []).length ? '<div class="bt-pm-top">' + c.top.slice(0, 4).map((x) => '<span>' + esc(x.title) + ' <b>' + pct(x.p) + '</b></span>').join('') + '</div>' : '')
        + '</div><button type="button" class="' + (ctx === 'adm' ? 'primary' : '') + ' bt-pm-go" data-pm="' + esc(c.slug) + '" data-pmtitle="' + esc(c.title) + '">' + btn + '</button></div>').join('') + '</div>'
        : f.loading ? '' : f.err ? '' : '<div class="gempty">Нічого не знайшлось.</div>')
      + (f.loading ? '<div class="gwait"><span class="spin"></span> Глек зазирає на Polymarket…</div>'
        : f.more ? '<div class="bt-row"><button type="button" data-more>Гортнути ще</button></div>' : '')
      + '</div>';
  }

  function wireFeed(r) {
    r.querySelectorAll('[data-feed]').forEach((box) => {
      const ctx = box.dataset.feed, f = feedState(ctx);
      box.querySelectorAll('[data-cat]').forEach((b) => b.onclick = () => { f.cat = b.dataset.cat; f.q = ''; loadFeed(ctx); });
      const form = box.querySelector('[data-find]');
      if (form) form.onsubmit = (e) => { e.preventDefault(); f.q = form.querySelector('input').value.trim(); loadFeed(ctx); };
      const more = box.querySelector('[data-more]');
      if (more) more.onclick = () => loadFeed(ctx, true);
      box.querySelectorAll('[data-pm]').forEach((b) => b.onclick = () => ctx === 'adm' ? fromPm(b.dataset.pm, null, b) : suggestPm(b.dataset.pm, b.dataset.pmtitle, b));
    });
  }

  // ---------------------------------------------------------------- «💡 Запропонувати»

  function suggestHtml() {
    if (!data.account) return '<section class="panel bt-sect">' + lockHtml('Пропонувати') + '</section>';
    const mineS = data.suggestions || [];
    const waiting = mineS.filter((x) => x.status === 'new').length;
    const max = data.suggestPending || 5;
    return '<section class="panel bt-sect"><h3>💡 Своє питання</h3>'
      + '<div class="muted small">Про що поставити? Матч, вибори, хто перший дограє Гончарне коло — пиши, адмін гляне й додасть. Чекати відповіді можуть до ' + max + ' пропозицій.</div>'
      + '<textarea class="bt-ta" data-sugtext maxlength="300" rows="3" placeholder="Напр.: Чи виграє Динамо в суботу?"></textarea>'
      + '<div class="bt-row"><span class="muted small" data-sugn>0/300</span><button type="button" class="primary" data-sugsend' + (waiting >= max ? ' disabled title="Уже ' + waiting + ' чекають"' : '') + '>💡 Запропонувати</button></div>'
      + '</section>'
      + '<section class="panel bt-sect"><h3>🔭 Або обери з Polymarket</h3>' + feedHtml('sug') + '</section>'
      + '<section class="panel bt-sect"><h3>📮 Мої пропозиції</h3>' + (mineS.length ? '<div class="bt-bets">' + mineS.map((s) =>
        '<div class="bt-bet st-' + esc(s.status) + '"><div class="bt-bet-h"><span class="bt-bet-t">' + esc(s.text || s.pmSlug) + '</span>'
        + (s.status === 'new' ? '<span class="chip warn">⏳ чекає</span>' : s.status === 'added' ? '<span class="chip ok">✓ додано</span>' : '<span class="chip err">відхилено</span>') + '</div>'
        + '<div class="muted small">' + when(s.at) + (s.pmUrl ? ' · <a href="' + esc(s.pmUrl) + '" target="_blank" rel="noopener noreferrer">Polymarket ↗</a>' : '')
        + (s.status === 'rejected' && s.reason ? ' · ' + esc(s.reason) : '') + (s.status === 'added' && s.eventId ? ' · <a href="' + HREF + '">до подій</a>' : '') + '</div></div>').join('') + '</div>'
        : '<div class="gempty">Ще нічого не пропонував.</div>') + '</section>';
  }

  async function suggest(text, slug, btn) {
    await o.busy(btn, 'передаю…', async () => {
      try {
        const r = await o.api('POST', '/api/bets/suggest', { text: text || null, pmSlug: slug || null });
        o.toast('💡 ' + (r.message || 'Передано Глекові'), 'ok');
        await loadMain();
        paint();
      } catch (e) { o.toast(e.message, 'err'); }
    });
  }
  async function suggestPm(slug, title, btn) {
    const yes = await ask('💡 Запропонувати', 'Запропонувати адміну подію «<b>' + esc(title) + '</b>»?', 'Запропонувати');
    if (yes) suggest('', slug, btn);
  }

  // ---------------------------------------------------------------- «🛠 Керування» (адмін)

  function adminHtml() {
    if (!adm) return '<section class="panel bt-sect"><div class="gwait"><span class="spin"></span> мить…</div></section>';
    const sub = '<nav class="bt-cats bt-adm-tabs">' + ADM_TABS.map(([k, l]) => '<button type="button" data-go="' + HREF + '/admin/' + k + '"' + (k === admTab ? ' class="on"' : '') + '>' + l
      + (k === 'inbox' && pending ? ' <span class="chip badge">' + pending + '</span>' : '') + '</button>').join('')
      + '<button type="button" class="primary bt-own" data-own>➕ Своя подія</button></nav>';
    let body;
    if (admTab === 'list') body = admListHtml();
    else if (admTab === 'inbox') body = inboxHtml();
    else body = '<div class="muted small">Обери подію — «➕ Додати» відкриє редактор з чернеткою: назву перекладеш, варіанти й кефи підправиш, і лише тоді вона піде людям.</div>' + feedHtml('adm');
    return '<section class="panel bt-sect">' + sub + body + '</section>';
  }

  function admListHtml() {
    const list = adm.events || [];
    if (!list.length) return '<div class="gempty glek">Подій ще нема. Додай з Polymarket або «➕ Своя подія».</div>';
    const order = { open: 0, paused: 1, closed: 2, draft: 3, settled: 4, cancelled: 5 };
    const sorted = list.slice().sort((a, b) => (order[a.status] - order[b.status]) || (b.id - a.id));
    return '<div class="bt-adm">' + sorted.map((e) => {
      const st = STATUS[e.status] || ['', ''];
      const fin = e.status === 'settled' || e.status === 'cancelled';
      const btns = [];
      if (!fin) btns.push('<button type="button" data-edit="' + e.id + '">✏ Правити</button>');
      if (e.status === 'draft' || e.status === 'paused' || e.status === 'closed') btns.push('<button type="button" data-st="open" data-id="' + e.id + '">▶ ' + (e.status === 'closed' ? 'Відкрити знову' : 'Відкрити') + '</button>');
      if (e.status === 'open') btns.push('<button type="button" data-st="paused" data-id="' + e.id + '">⏸ Призупинити</button>');
      if (e.status === 'open' || e.status === 'paused') btns.push('<button type="button" data-st="closed" data-id="' + e.id + '">🔒 Закрити прийом</button>');
      if (e.status === 'open' || e.status === 'paused' || e.status === 'closed') btns.push('<button type="button" class="primary" data-settle="' + e.id + '">🏁 Розрахувати</button>');
      if (!fin) btns.push('<button type="button" class="ghost danger" data-cancel="' + e.id + '">✕ Скасувати</button>');
      const win = (e.options || []).find((x) => x.key === e.winner);
      return '<div class="bt-adm-ev st-' + esc(e.status) + '">'
        + '<div class="bt-ev-h"><b>' + esc(e.title) + '</b><span class="chip ' + st[1] + '">' + (e.status === 'open' && !e.accepting ? '⌛ час вийшов' : st[0]) + '</span></div>'
        + '<div class="muted small">#' + e.id + (e.closesAt ? ' · до ' + when(e.closesAt) : ' · без терміну') + ' · ' + num(e.bets) + ' ' + plural(e.bets, 'ставка', 'ставки', 'ставок') + ' · ' + num(e.staked) + ' 🏺'
        + (e.glek != null ? ' · Глек ' + signed(e.glek) : '') + (win ? ' · зіграло «' + esc(win.title) + '»' : '') + (e.note ? ' · ' + esc(e.note) : '')
        + (e.pmUrl ? ' · <a href="' + esc(e.pmUrl) + '" target="_blank" rel="noopener noreferrer">Polymarket ↗</a>' : '') + '</div>'
        + '<div class="bt-adm-opts">' + (e.options || []).slice(0, 12).map((x) => '<span class="' + (x.key === e.winner ? 'win' : '') + '">' + esc(x.title) + ' <b>' + kef(x.odds) + '</b>'
          + (x.n ? ' <span class="muted">· ' + x.n + ' на ' + num(x.staked) + ' 🏺' + (x.liability ? ', віддати ' + num(x.liability) : '') + '</span>' : '') + '</span>').join('')
        + ((e.options || []).length > 12 ? '<span class="muted">… ще ' + (e.options.length - 12) + '</span>' : '') + '</div>'
        + (btns.length ? '<div class="bt-row bt-adm-btns">' + btns.join('') + '</div>' : '') + '</div>';
    }).join('') + '</div>';
  }

  function inboxHtml() {
    const fresh = adm.suggestions || [];
    const old = (adm.recentSuggestions || []).filter((s) => s.status !== 'new');
    const row = (s, live) => '<div class="bt-bet st-' + esc(s.status) + '"><div class="bt-bet-h"><span class="bt-bet-t"><b>' + esc(s.nick) + '</b>: ' + esc(s.text || s.pmSlug) + '</span>'
      + (live ? '' : s.status === 'added' ? '<span class="chip ok">додано</span>' : '<span class="chip err">відхилено</span>') + '</div>'
      + '<div class="muted small">' + when(s.at) + (s.pmUrl ? ' · <a href="' + esc(s.pmUrl) + '" target="_blank" rel="noopener noreferrer">Polymarket ↗</a>' : '') + (s.reason ? ' · ' + esc(s.reason) : '') + '</div>'
      + (live ? '<div class="bt-row"><button type="button" class="primary" data-sadd="' + s.id + '">➕ Додати</button><button type="button" class="ghost danger" data-srej="' + s.id + '">Відхилити</button></div>' : '') + '</div>';
    return '<h3>📥 Чекають <span class="count">' + (fresh.length || '') + '</span></h3>'
      + (fresh.length ? '<div class="bt-bets">' + fresh.map((s) => row(s, true)).join('') + '</div>' : '<div class="gempty">Ніхто нічого не пропонує.</div>')
      + (old.length ? '<h3>Розглянуті</h3><div class="bt-bets">' + old.map((s) => row(s, false)).join('') + '</div>' : '');
  }

  async function admReload() {
    try { adm = await get('/api/bets/admin'); setPending((adm.suggestions || []).length); } catch (e) { o.toast(e.message, 'err'); }
    try { await loadMain(); } catch { /* сторінка подій оновиться з хаба */ }
    paint();
  }

  async function setStatus(id, st, btn) {
    const e = adm && adm.events.find((x) => x.id === id);
    if (st === 'open' && e && e.status === 'draft') {
      const yes = await ask('▶ Відкрити?', 'Подія «<b>' + esc(e.title) + '</b>» піде людям — і на неї почнуть ставити.', 'Відкрити');
      if (!yes) return;
    }
    await o.busy(btn, 'мить…', async () => {
      try { const r = await o.api('POST', '/api/bets/events/' + id + '/status', { status: st }); o.toast(r.message || 'Є!', 'ok'); await admReload(); }
      catch (err) { o.toast(err.message, 'err'); }
    });
  }

  function settleDialog(id) {
    const e = adm && adm.events.find((x) => x.id === id);
    if (!e) return;
    const total = (e.options || []).reduce((s, x) => s + (x.staked || 0), 0);
    modal('🏁 Розрахувати', '<div class="muted small">«' + esc(e.title) + '» — обери, що зіграло. Ставки на нього отримають ставка × кеф, решта згорить. Назад не відкрутиш.</div>'
      + '<div class="bt-settle">' + e.options.map((x) => {
        const g = total - (x.liability || 0);
        return '<label class="bt-radio"><input type="radio" name="bt-win" value="' + esc(x.key) + '"><span>' + esc(x.title) + ' <b>' + kef(x.odds) + '</b></span>'
          + '<span class="muted small">' + (x.n ? x.n + ' на ' + num(x.staked) + ' 🏺 · ' : '') + 'Глек ' + signed(g) + '</span></label>';
      }).join('') + '</div>', '🏁 Розрахувати', async (box, btn) => {
      const v = box.querySelector('input[name="bt-win"]:checked');
      if (!v) { o.toast('Обери варіант, що зіграв', 'err'); return false; }
      const t = e.options.find((x) => x.key === v.value);
      const sure = await ask('Точно «' + t.title + '»?', 'Розраховуємо «<b>' + esc(e.title) + '</b>»: зіграло «<b>' + esc(t.title) + '</b>».', 'Так, розрахувати');
      if (!sure) return false;
      return o.busy(btn, 'рахую…', async () => {
        try { const r = await o.api('POST', '/api/bets/events/' + id + '/settle', { winner: v.value }); o.toast('🏁 ' + (r.message || 'Розраховано'), 'ok'); await admReload(); return true; }
        catch (err) { o.toast(err.message, 'err'); return false; }
      });
    });
  }

  function cancelDialog(id) {
    const e = adm && adm.events.find((x) => x.id === id);
    if (!e) return;
    modal('✕ Скасувати подію', '<div class="muted small">«' + esc(e.title) + '» — усі ставки (' + num(e.bets) + ') повернуться людям. Назад не відкрутиш.</div>'
      + '<input type="text" data-reason maxlength="200" placeholder="Причина — необов\'язково (матч перенесли…)" aria-label="Причина">', 'Скасувати подію', async (box, btn) =>
      o.busy(btn, 'скасовую…', async () => {
        try { const r = await o.api('POST', '/api/bets/events/' + id + '/cancel', { reason: box.querySelector('[data-reason]').value.trim() || null }); o.toast(r.message || 'Скасовано', 'ok'); await admReload(); return true; }
        catch (err) { o.toast(err.message, 'err'); return false; }
      }), true);
  }

  function rejectDialog(id) {
    const s = adm && adm.suggestions.find((x) => x.id === id);
    if (!s) return;
    modal('Відхилити пропозицію', '<div class="muted small">' + esc(s.nick) + ': «' + esc(s.text || s.pmSlug) + '». Автор отримає рядок із причиною.</div>'
      + '<input type="text" data-reason maxlength="200" placeholder="Причина — необов\'язково" aria-label="Причина">', 'Відхилити', async (box, btn) =>
      o.busy(btn, 'мить…', async () => {
        try { const r = await o.api('POST', '/api/bets/suggest/' + id + '/reject', { reason: box.querySelector('[data-reason]').value.trim() || null }); o.toast(r.message || 'Відхилено', 'ok'); await admReload(); return true; }
        catch (err) { o.toast(err.message, 'err'); return false; }
      }), true);
  }

  async function addSuggestion(id, btn) {
    const s = adm && adm.suggestions.find((x) => x.id === id);
    if (!s) return;
    if (s.pmSlug) { await fromPm(s.pmSlug, null, btn, s.id, s.text); return; }
    const p = 0.5;
    openEditor({ title: s.text.slice(0, 200), description: '', options: [{ title: 'Так', worldP: null, odds: oddsOf(p) }, { title: 'Ні', worldP: null, odds: oddsOf(p) }], suggestion: s.id });
  }

  /// «➕ Додати» з огляду чи з пропозиції: чернетка з Polymarket → редактор.
  async function fromPm(slug, market, btn, suggestion, text) {
    const go = async () => {
      try {
        const d = await get('/api/bets/pm/event/' + encodeURIComponent(slug) + (market ? '?market=' + encodeURIComponent(market) : ''));
        if (!d.options || !d.options.length) { o.toast('Халепа: у цієї події нема живих варіантів', 'err'); return; }
        openEditor({
          title: d.title, description: d.description || '', closesAt: d.closesAt, pmSlug: d.slug, pmUrl: d.url, pmEnd: d.closesAt,
          markets: d.markets || [], market: d.market, suggestion: suggestion || null, note: text && text !== d.title ? text : '',
          options: d.options.map((x) => ({ title: x.title, worldP: x.worldP, odds: x.odds })),
        });
      } catch (e) { o.toast(e.message, 'err'); }
    };
    if (btn) await o.busy(btn, 'мить…', go); else await go();
  }

  // ---------------------------------------------------------------- редактор події

  function openEditor(init) {
    closeEditor();
    ed = Object.assign({ id: null, title: '', description: '', closesAt: null, options: [], markets: [], market: null, pmSlug: null, pmUrl: null, suggestion: null, fresh: false }, init);
    ed.close = localInput(ed.closesAt);
    ed.options = ed.options.map((x) => Object.assign({ key: null, title: '', odds: null, worldP: null, freshP: null, n: 0, sel: false }, x));
    const wrap = document.createElement('div');
    wrap.className = 'modal bt-modal';
    wrap.innerHTML = '<div class="card bt-ed" role="dialog" aria-modal="true"></div>';
    wrap.addEventListener('click', (e) => { if (e.target === wrap) askClose(); });
    ed.wrap = wrap;
    ed.onKey = (e) => { if (e.key === 'Escape' && !document.querySelector('.lv-ask, .bt-ask')) { e.stopPropagation(); askClose(); } };
    document.addEventListener('keydown', ed.onKey, true);
    document.body.appendChild(wrap);
    edPaint();
    const t = wrap.querySelector('[data-ed-title]');
    if (t) t.focus();
  }
  async function askClose() {
    if (!ed) return;
    if (await ask('Закрити редактор?', 'Незбережене пропаде.', 'Закрити')) closeEditor();
  }
  function closeEditor() {
    if (!ed) return;
    ed.wrap.remove();
    document.removeEventListener('keydown', ed.onKey, true);
    ed = null;
  }

  /// Переписати в ed те, що вже вписано в поля, — перед кожним перемальовуванням.
  function edSync() {
    if (!ed) return;
    const c = ed.wrap;
    const v = (s) => { const el = c.querySelector(s); return el ? el.value : null; };
    if (v('[data-ed-title]') != null) ed.title = v('[data-ed-title]');
    if (v('[data-ed-desc]') != null) ed.description = v('[data-ed-desc]');
    if (v('[data-ed-close]') != null) ed.close = v('[data-ed-close]');
    c.querySelectorAll('[data-oi]').forEach((row) => {
      const x = ed.options[+row.dataset.oi];
      if (!x) return;
      x.title = row.querySelector('[data-ot]').value;
      const k = parseNum(row.querySelector('[data-oo]').value);
      x.odds = k;
      x.sel = row.querySelector('[data-os]').checked;
    });
  }

  function edPaint() {
    if (!ed) return;
    const card = ed.wrap.querySelector('.bt-ed');
    const L = lim();
    const anyFresh = ed.options.some((x) => x.freshP != null);
    const sel = ed.options.filter((x) => x.sel).length;
    const quick = [['+1 год', 1], ['+3 год', 3], ['сьогодні 23:59', 'eod'], ['+1 день', 24], ['+7 днів', 168]];
    card.innerHTML = '<div class="bt-ed-h"><h3>' + (ed.id ? '✏ Подія #' + ed.id : '➕ Нова подія') + '</h3><button type="button" class="icon ghost" data-ed-x aria-label="Закрити">✕</button></div>'
      + (ed.pmUrl ? '<div class="muted small">З Polymarket: <a href="' + esc(ed.pmUrl) + '" target="_blank" rel="noopener noreferrer">' + esc(ed.pmSlug) + ' ↗</a>' + (ed.suggestion ? ' · з пропозиції' : '') + '</div>' : ed.suggestion ? '<div class="muted small">З пропозиції гравця</div>' : '')
      + (ed.note ? '<div class="bt-lock small">Гравець пише: «' + esc(ed.note) + '»</div>' : '')
      + (ed.markets && ed.markets.length > 1 && !ed.id ? '<label class="bt-fl"><span>Ринок — у цієї події їх кілька</span><select data-ed-market>' + ed.markets.map((m) =>
        '<option value="' + esc(m.id) + '"' + (m.id === ed.market ? ' selected' : '') + '>' + esc(m.question) + '</option>').join('') + '</select></label>' : '')
      + '<label class="bt-fl"><span>Назва українською</span><input type="text" data-ed-title maxlength="200" value="' + esc(ed.title) + '" placeholder="Хто виграє Лігу чемпіонів?"></label>'
      + '<label class="bt-fl"><span>Опис — необов\'язково (правила, що вважаємо перемогою)</span><textarea class="bt-ta" data-ed-desc maxlength="2000" rows="2">' + esc(ed.description) + '</textarea></label>'
      + '<div class="bt-fl"><span>Ставки приймаються до</span><div class="bt-close-row"><input type="datetime-local" data-ed-close value="' + esc(ed.close || '') + '">'
      + '<button type="button" class="ghost" data-close-clear title="Без терміну — закриєш руками">без терміну</button></div>'
      + '<div class="bt-quick">' + quick.map(([l, v]) => '<button type="button" class="chip" data-close-q="' + v + '">' + l + '</button>').join('')
      + (ed.pmEnd ? '<button type="button" class="chip" data-close-pm>як на Polymarket (' + esc(when(ed.pmEnd)) + ')</button>' : '') + '</div></div>'
      + '<div class="bt-fl"><span>Варіанти <span class="muted">(' + ed.options.length + ') · кеф від ' + kef(L.minOdds) + ' до ' + kef(L.maxOdds) + ', маржа Глека ' + Math.round(L.margin * 100) + '%</span></span>'
      + '<div class="bt-eopts">' + ed.options.map((x, i) => '<div class="bt-eopt" data-oi="' + i + '">'
        + '<input type="checkbox" data-os aria-label="Позначити, щоб злити"' + (x.sel ? ' checked' : '') + '>'
        + '<input type="text" data-ot maxlength="120" value="' + esc(x.title) + '" placeholder="Варіант" aria-label="Назва варіанта">'
        + '<label class="bt-kf"><span>×</span><input type="text" inputmode="decimal" data-oo value="' + (x.odds != null ? String(x.odds).replace('.', ',') : '') + '" aria-label="Кеф"></label>'
        + '<span class="bt-p" title="Світ думає (Polymarket)">' + (x.worldP != null ? pct(x.worldP) : '—')
        + (x.freshP != null ? ' <b class="' + (x.freshP > (x.worldP || 0) ? 'bt-up' : x.freshP < (x.worldP || 0) ? 'bt-down' : '') + '" title="Свіжа ціна">→ ' + pct(x.freshP) + '</b>' : '') + '</span>'
        + '<button type="button" class="icon ghost" data-odel="' + i + '" aria-label="Прибрати варіант"' + (x.n ? ' disabled title="На нього вже ставили"' : '') + '>✕</button>'
        + '</div>').join('') + '</div>'
      + '<div class="bt-row bt-ed-tools"><button type="button" data-oadd>➕ Варіант</button>'
      + '<button type="button" data-omerge' + (sel < 2 ? ' disabled' : '') + ' title="Позначені галочкою стануть одним варіантом «Інший»">🧩 Злити позначені в «Інший»' + (sel ? ' (' + sel + ')' : '') + '</button>'
      + (ed.options.length > 12 ? '<button type="button" data-otop>✂ Лишити 10, решту — в «Інший»</button>' : '')
      + '<button type="button" data-oprice title="Кеф = (1 − маржа) / ймовірність' + (anyFresh ? ', зі свіжих цін' : '') + '">⚖ Кефи з цін</button>'
      + (ed.pmSlug ? '<button type="button" data-ofresh>🔄 Оновити ціни з Polymarket</button>' : '') + '</div></div>'
      + '<div class="bt-row bt-ed-foot">' + (ed.id
        ? '<button type="button" class="primary" data-esave>💾 Зберегти</button>'
        : '<button type="button" data-esave="draft">💾 Зберегти чернетку</button><button type="button" class="primary" data-esave="open">▶ Зберегти й відкрити</button>')
      + '<button type="button" class="ghost" data-ed-x>Не треба</button></div>';
    wireEditor(card);
  }

  function wireEditor(card) {
    const re = () => { edSync(); edPaint(); };
    card.querySelectorAll('[data-ed-x]').forEach((b) => b.onclick = askClose);
    card.querySelectorAll('[data-os]').forEach((b) => b.onchange = re);
    const mk = card.querySelector('[data-ed-market]');
    if (mk) mk.onchange = async () => {
      edSync();
      const keep = { title: ed.title, description: ed.description, close: ed.close, suggestion: ed.suggestion, note: ed.note };
      try {
        const d = await get('/api/bets/pm/event/' + encodeURIComponent(ed.pmSlug) + '?market=' + encodeURIComponent(mk.value));
        ed.market = d.market;
        ed.options = (d.options || []).map((x) => ({ key: null, title: x.title, worldP: x.worldP, odds: x.odds, freshP: null, n: 0, sel: false }));
        // назву ринку беремо за підказку, якщо адмін ще нічого свого не вписав
        const m = (ed.markets || []).find((y) => y.id === d.market);
        Object.assign(ed, keep);
        if (m && (!ed.title || ed.title === d.title)) ed.title = m.question;
      } catch (e) { o.toast(e.message, 'err'); }
      edPaint();
    };
    card.querySelector('[data-close-clear]').onclick = () => { edSync(); ed.close = ''; edPaint(); };
    card.querySelectorAll('[data-close-q]').forEach((b) => b.onclick = () => {
      edSync();
      const v = b.dataset.closeQ;
      const d = new Date();
      if (v === 'eod') d.setHours(23, 59, 0, 0); else { d.setSeconds(0, 0); d.setTime(d.getTime() + (+v) * 3600000); }
      ed.close = localInput(d.toISOString());
      edPaint();
    });
    const cpm = card.querySelector('[data-close-pm]');
    if (cpm) cpm.onclick = () => { edSync(); ed.close = localInput(ed.pmEnd); edPaint(); };
    card.querySelectorAll('[data-odel]').forEach((b) => b.onclick = () => { edSync(); ed.options.splice(+b.dataset.odel, 1); edPaint(); });
    card.querySelector('[data-oadd]').onclick = () => {
      edSync();
      ed.options.push({ key: null, title: '', odds: oddsOf(1 / Math.max(2, ed.options.length + 1)), worldP: null, freshP: null, n: 0, sel: false });
      edPaint();
      const rows = card.querySelectorAll('[data-ot]');
      if (rows.length) rows[rows.length - 1].focus();
    };
    card.querySelector('[data-omerge]').onclick = () => { edSync(); merge(ed.options.filter((x) => x.sel)); edPaint(); };
    const top = card.querySelector('[data-otop]');
    if (top) top.onclick = () => { edSync(); merge(ed.options.slice(10)); edPaint(); };
    card.querySelector('[data-oprice]').onclick = () => {
      edSync();
      let n = 0;
      for (const x of ed.options) {
        const p = x.freshP != null ? x.freshP : x.worldP;
        if (!(p > 0)) continue;
        x.worldP = p; x.freshP = null; x.odds = oddsOf(p); n++;
      }
      o.toast(n ? '⚖ Кефи з цін: ' + n : 'Нема цін — кефи впиши руками', n ? 'ok' : 'err');
      edPaint();
    };
    const fr = card.querySelector('[data-ofresh]');
    if (fr) fr.onclick = () => o.busy(fr, 'питаю…', async () => {
      edSync();
      try {
        let fresh;
        if (ed.id) {
          const r = await get('/api/bets/events/' + ed.id + '/pm' + (ed.market ? '?market=' + encodeURIComponent(ed.market) : ''));
          fresh = new Map((r.options || []).filter((x) => x.freshP != null).map((x) => [x.key, x.freshP]));
          const byTitle = new Map(((r.draft && r.draft.options) || []).map((x) => [x.title.toLowerCase(), x.worldP]));
          ed.options.forEach((x) => { x.freshP = x.key && fresh.has(x.key) ? fresh.get(x.key) : byTitle.has(x.title.trim().toLowerCase()) ? byTitle.get(x.title.trim().toLowerCase()) : null; });
        } else {
          const d = await get('/api/bets/pm/event/' + encodeURIComponent(ed.pmSlug) + (ed.market ? '?market=' + encodeURIComponent(ed.market) : ''));
          const byTitle = new Map((d.options || []).map((x) => [x.title.toLowerCase(), x.worldP]));
          ed.options.forEach((x) => { const k = x.title.trim().toLowerCase(); x.freshP = byTitle.has(k) ? byTitle.get(k) : null; });
        }
        const n = ed.options.filter((x) => x.freshP != null).length;
        o.toast(n ? '🔄 Свіжі ціни поруч — «⚖ Кефи з цін» перерахує' : 'Polymarket не знає таких варіантів (злиті й перейменовані — не порівняти)', n ? 'ok' : 'err');
      } catch (e) { o.toast(e.message, 'err'); }
      edPaint();
    });
    card.querySelectorAll('[data-esave]').forEach((b) => b.onclick = () => save(b, b.dataset.esave || null));
  }

  /// Злити кілька варіантів в один «Інший»: ймовірності складаються, кеф — з суми (або найменший із тих, що були).
  function merge(list) {
    if (list.length < 2) return;
    const busy = list.find((x) => x.n);
    if (busy) { o.toast('Халепа: на «' + busy.title + '» уже ставили — його не зіллєш', 'err'); return; }
    let other = ed.options.find((x) => !list.includes(x) && /^інш(ий|е)$/i.test(x.title.trim()));
    const all = other ? list.concat(other) : list;
    const withP = all.every((x) => x.worldP != null);
    const p = withP ? Math.min(1, all.reduce((s, x) => s + (x.freshP != null ? x.freshP : x.worldP), 0)) : null;
    const odds = p != null ? oddsOf(p) : Math.min(...all.map((x) => x.odds || lim().maxOdds));
    const at = ed.options.indexOf(list[0]);
    if (other) { other.worldP = p; other.odds = odds; other.freshP = null; }
    ed.options = ed.options.filter((x) => !list.includes(x));
    if (!other) ed.options.splice(Math.min(at, ed.options.length), 0, { key: null, title: 'Інший', worldP: p, odds, freshP: null, n: 0, sel: false });
    ed.options.forEach((x) => { x.sel = false; });
  }

  async function save(btn, status) {
    edSync();
    const L = lim();
    const title = ed.title.trim();
    if (!title) { o.toast('Подія без назви', 'err'); return; }
    for (const x of ed.options) {
      if (!x.title.trim()) { o.toast('У кожного варіанта мусить бути назва', 'err'); return; }
      if (x.odds == null || x.odds < L.minOdds || x.odds > L.maxOdds) { o.toast('Кеф «' + x.title + '» — від ' + kef(L.minOdds) + ' до ' + kef(L.maxOdds), 'err'); return; }
    }
    let closesAt = null;
    if (ed.close) {
      const d = new Date(ed.close);
      if (isNaN(d)) { o.toast('Дивна дата', 'err'); return; }
      closesAt = d.toISOString();
      if (status === 'open' && d.getTime() < Date.now()) { o.toast('Час прийому вже минув — постав пізніший', 'err'); return; }
    }
    const body = {
      title, description: ed.description.trim(), closesAt,
      options: ed.options.map((x) => ({ key: x.key || null, title: x.title.trim(), odds: Math.round(x.odds * 100) / 100, worldP: x.worldP })),
    };
    if (!ed.id) Object.assign(body, { pmSlug: ed.pmSlug || null, status: status || 'draft', suggestion: ed.suggestion || null });
    await o.busy(btn, 'зберігаю…', async () => {
      try {
        const r = ed.id ? await o.api('PUT', '/api/bets/events/' + ed.id, body) : await o.api('POST', '/api/bets/events', body);
        o.toast((r.message || 'Збережено'), 'ok');
        closeEditor();
        if (tab === 'admin' && admTab !== 'list') o.go(HREF + '/admin/list'); else await admReload();
      } catch (e) { o.toast(e.message, 'err'); }
    });
  }

  function editEvent(id) {
    const e = adm && adm.events.find((x) => x.id === id);
    if (!e) return;
    openEditor({
      id: e.id, title: e.title, description: e.description || '', closesAt: e.closesAt, pmSlug: e.pmSlug, pmUrl: e.pmUrl,
      options: (e.options || []).map((x) => ({ key: x.key, title: x.title, odds: x.odds, worldP: x.worldP, n: x.n })),
    });
  }

  // ---------------------------------------------------------------- вікна

  /// «Точно?» — те саме вікно, що в Лавці (web/lavka.js), щоб сайт питав однаково.
  function ask(title, html, okText) {
    if (window.HLavka && HLavka.ask) return HLavka.ask(title, html, okText);
    return Promise.resolve(confirm(title));
  }

  /// Вікно з полями й дією: act(box, btn) → true — закрити.
  function modal(title, html, okText, act, danger) {
    const wrap = document.createElement('div');
    wrap.className = 'modal bt-ask';
    wrap.innerHTML = '<div class="card"><h3>' + esc(title) + '</h3>' + html
      + '<div class="row"><button class="' + (danger ? 'danger' : 'primary') + '" type="button" data-yes>' + esc(okText) + '</button><button class="ghost" type="button" data-no>Не треба</button></div></div>';
    const close = () => { wrap.remove(); document.removeEventListener('keydown', onKey, true); };
    const onKey = (e) => { if (e.key === 'Escape' && !document.querySelector('.lv-ask')) { e.stopPropagation(); close(); } };
    wrap.addEventListener('click', (e) => { if (e.target === wrap) close(); });
    const yes = wrap.querySelector('[data-yes]');
    yes.onclick = async () => { if (await act(wrap, yes)) close(); };
    wrap.querySelector('[data-no]').onclick = close;
    document.body.appendChild(wrap);
    document.addEventListener('keydown', onKey, true);
    const f = wrap.querySelector('input[type=text]');
    (f || yes).focus();
  }

  // ---------------------------------------------------------------- дроти

  function wire(r) {
    r.querySelectorAll('[data-go]').forEach((b) => b.onclick = () => o.go(b.dataset.go));
    r.querySelectorAll('[data-acc]').forEach((b) => b.onclick = () => o.askNick(true, 'register', String(o.me.nick || '').replace(/^гість\s*/i, '')));
    // вибір варіанта: акаунту — панель ставки, гостю — нагадування
    r.querySelectorAll('.bt-opt[data-opt]').forEach((b) => b.onclick = () => {
      if (!data.account) { o.toast('🔒 Ставки — для акаунтів. Закріпи нік — і бахай', 'err'); return; }
      const ev = +b.dataset.ev, opt = b.dataset.opt;
      if (pick && pick.ev === ev && pick.opt === opt) pick = null;
      else {
        const x = data.events.find((y) => y.id === ev).options.find((y) => y.key === opt);
        pick = { ev, opt, stake: pick && pick.ev === ev ? pick.stake : '', key: newKey(), seen: x ? x.odds : null };
      }
      paint();
      const inp = r.querySelector('[data-stake]');
      if (inp && !matchMedia('(pointer: coarse)').matches) inp.focus();
    });
    const stake = r.querySelector('[data-stake]');
    if (stake) {
      const upd = () => {
        const v = String(stake.value).replace(/\D/g, '');
        if (v !== stake.value) stake.value = v;
        pick.stake = v;
        const e = data.events.find((x) => x.id === pick.ev);
        const x = e && e.options.find((y) => y.key === pick.opt);
        const w = r.querySelector('[data-win]');
        if (w && x) w.innerHTML = winText(+v || 0, pick.shown != null ? pick.shown : x.odds);
      };
      stake.oninput = upd;
      stake.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); const b = r.querySelector('[data-place]'); if (b) place(b); } };
      stake.onblur = () => { if (late) setTimeout(() => { if (!typing()) paint(); }, 0); };
    }
    r.querySelectorAll('[data-desc]').forEach((d) => d.onclick = () => d.classList.toggle('open'));
    r.querySelectorAll('[data-q]').forEach((b) => b.onclick = () => { if (!pick) return; pick.stake = b.dataset.q; paint(); });
    const pl = r.querySelector('[data-place]');
    if (pl) pl.onclick = () => place(pl);
    const un = r.querySelector('[data-unpick]');
    if (un) un.onclick = () => { pick = null; paint(); };
    // пропозиції
    const ta = r.querySelector('[data-sugtext]');
    if (ta) {
      const n = r.querySelector('[data-sugn]');
      ta.oninput = () => { n.textContent = ta.value.length + '/300'; };
      ta.onblur = () => { if (late) setTimeout(() => { if (!typing()) paint(); }, 0); };
      r.querySelector('[data-sugsend]').onclick = (e) => {
        const t = ta.value.trim();
        if (!t) { o.toast('Напиши питання — або обери подію з огляду', 'err'); ta.focus(); return; }
        suggest(t, null, e.currentTarget);
      };
    }
    wireFeed(r);
    // керування
    const own = r.querySelector('[data-own]');
    if (own) own.onclick = () => openEditor({ options: [{ title: 'Так', odds: oddsOf(0.5) }, { title: 'Ні', odds: oddsOf(0.5) }] });
    r.querySelectorAll('[data-edit]').forEach((b) => b.onclick = () => editEvent(+b.dataset.edit));
    r.querySelectorAll('[data-st]').forEach((b) => b.onclick = () => setStatus(+b.dataset.id, b.dataset.st, b));
    r.querySelectorAll('[data-settle]').forEach((b) => b.onclick = () => settleDialog(+b.dataset.settle));
    r.querySelectorAll('[data-cancel]').forEach((b) => b.onclick = () => cancelDialog(+b.dataset.cancel));
    r.querySelectorAll('[data-sadd]').forEach((b) => b.onclick = () => addSuggestion(+b.dataset.sadd, b));
    r.querySelectorAll('[data-srej]').forEach((b) => b.onclick = () => rejectDialog(+b.dataset.srej));
    r.querySelectorAll('input[type=search]').forEach((i) => { i.onblur = () => { if (late) setTimeout(() => { if (!typing()) paint(); }, 0); }; });
  }

  /// Кружечок на плитці «🎲 Ставки на події» і на вкладці «🛠 Керування» — адміну, скільки пропозицій чекає.
  function setPending(n) {
    pending = n || 0;
    document.querySelectorAll('[data-bets-count]').forEach((el) => { el.textContent = pending; el.hidden = !pending || !isAdmin(); });
  }

  // ---------------------------------------------------------------- плитка в каталозі ігор

  const openCount = () => (data ? (data.events || []).filter((e) => e.accepting).length : null);
  function openText(n) {
    if (n == null) return 'Глек гортає зошит…';
    return n ? n + ' ' + plural(n, 'подія відкрита', 'події відкриті', 'подій відкрито') : 'поки нема на що ставити';
  }
  function tileLive() {
    return '<span data-bets-open>' + esc(openText(openCount())) + '</span>'
      + ' <span class="chip badge" data-bets-count title="Пропозицій чекає"' + (pending && isAdmin() ? '' : ' hidden') + '>' + pending + '</span>';
  }
  /// Число на плитці — на місці, без перемальовування лобі (там людина може гортати чи шукати).
  function paintTile() {
    const t = openText(openCount());
    document.querySelectorAll('[data-bets-open]').forEach((el) => { if (el.textContent !== t) el.textContent = t; });
  }

  const panel = {
    id: 'bets',
    title: 'Ставки',
    icon: '🎲',
    bare: true,       // свої секції-панелі, як у лобі
    visible: eventsOn,
    tile: {
      group: 'azart', theme: 'bets', icon: '🎲', title: 'Ставки на події',
      hint: 'Хто виграє, що станеться: Глек дає кеф, ставиш черепки. Події від адміна й з Polymarket',
      live: tileLive,
    },
    mount(host, ctx) {
      hostEl = host;
      host.classList.add('bets');
      pick = null;
      render(ctx.sub);
    },
    /// Інша вкладка тієї самої сторінки (#games/x:bets/<вкладка>) — без перемонтування.
    route(sub) { pick = null; render(sub); },
  };
  if (window.HGames) HGames.registerPanel(panel);

  window.HBets = {
    init(opts) { o = opts; if (o.esc) esc = o.esc; },
    /// Після /api/me: адміну — лічильник пропозицій одразу, а не з першим відкриттям сторінки.
    /// Після /api/me: плитка в каталозі з'являється чи зникає (Bets:Events), а число подій і адміну лічильник
    /// пропозицій — одразу, а не з першим відкриттям сторінки.
    async ready() {
      if (!o) return;
      if (window.HGames && HGames.panelTileChanged) HGames.panelTileChanged();
      if (isShown()) { render(String(location.hash).split('x:bets/')[1] || ''); return; }
      if (!eventsOn()) return;
      try { await loadMain(); } catch { /* нема — то й нема */ }
    },
    attach(conn) {
      conn.on('betEvents', () => { if (isShown() || data || eventsOn()) soon(); });
      conn.on('betMine', (m) => {
        if (!m || !m.text) return;
        news.unshift({ text: m.text, at: new Date() });
        news.length = Math.min(news.length, 3);
        mine = null;
        soon();
      });
      conn.on('betSuggest', (m) => {
        setPending((m && m.count) || 0);
        if (isShown() && tab === 'admin') soon(); else if (isShown()) paint(true);
      });
    },
  };
})();
