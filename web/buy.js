/*
  Черепки за гривні — window.HBuy (08.10.2026). Купити й продати за однією схемою, обома керує адмін:
  людина створює заявку — вона «⏳ в обробці», і її можна скасувати; адмін підтверджує — статус міняється, «Скасувати»
  зникає. І все.

  ➕ Купити — обираєш пакет чи свою суму (і кому — собі чи другові), бачиш картку чи банку сайту, скидаєш гроші й тиснеш
  «✓ Скинув». Адмін бачить переказ і тисне «✓ Підтвердити» — черепки падають (тост гаманця шле сервер).
  💸 Продати — виставляєш «N 🏺 за X грн», черепки одразу відкладаються; адмін бачить твою картку (банки з профілю
  Падельні; нема — впишеш тут же, і вони збережуться в Падельні), переказує гроші й тисне «✓ Підтвердити» — продано.
  📋 Заявки — лише адміну: що в обробці (купівлі й продажі), картки сайту для покупців, закриті, суми за місяць.

  Сервер — ShardShop.cs: GET /api/shards; купити: POST /api/shards/check { uah, for } («Далі» — перевірка до реквізитів),
  /paid { uah, for }, /{id}/cancel, адмін — /{id}/ok; продати: /sell { uah }, /sale/{id}/cancel, адмін — /sale/{id}/ok;
  картки сайту — PUT /api/shards/banks, мої банки — PUT /api/padel/banks. Події хаба: shardOrders { count, sales } —
  скільки в обробці (кружечок на гаманці в шапці адміна); shardMine — моя заявка змінилась.
*/
(() => {
  'use strict';

  let o = null;                        // що дає app.js
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  let data = null;                     // останнє GET /api/shards
  let count = 0;                       // купівель в обробці (адміну)
  let sales = 0;                       // продажів в обробці (адміну)
  let wrap = null;                     // відкрите вікно
  let tab = 'buy';                     // buy — купити, sell — продати, desk — заявки (адміну)
  let pack = 0;                        // обраний пакет чи своя сума, грн
  let own = false;                     // своя сума, а не пакет
  let gift = false;                    // купуємо другові
  let to = '';                         // кому (нік друга)
  let step = 'pick';                   // pick — пакет і кому, pay — реквізити й «✓ Скинув»
  let sellUah = 0;                     // скільки гривень хочу за черепки
  let sellOwn = false;                 // своя сума, а не пакет
  let bankForm = '';                   // відкрита форма картки: mine — моя (продаж), shop — сайту (заявки адміна)
  let bankKind = 'mono';
  let loadT = 0;

  const BANKS = { mono: 'monobank', privat: 'ПриватБанк', pumb: 'ПУМБ', abank: 'А-Банк', sense: 'Sense Bank', izi: 'izibank', other: 'Інший банк' };
  const num = (n) => Number(n || 0).toLocaleString('uk-UA');
  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const dat = (n) => (window.HLavka && HLavka.dative ? HLavka.dative(n) : n);
  const gen = (n) => (window.HLavka && HLavka.genitive ? HLavka.genitive(n) : n);
  const card = (d) => String(d || '').replace(/\D/g, '').replace(/(\d{4})(?=\d)/g, '$1 ');

  // ---------------------------------------------------------------- дані

  async function load() {
    try { data = await o.api('GET', '/api/shards'); } catch { data = null; }
    count = data && data.desk ? data.desk.orders.length : 0;
    sales = data && data.desk ? data.desk.sales.length : 0;
    paintBadge();
    return data;
  }

  /// Хаб сказав, що щось змінилось: перечитати. Поки людина друкує в полі вікна — лише дані, без перемальовування.
  function soon() {
    clearTimeout(loadT);
    loadT = setTimeout(async () => {
      await load();
      if (!wrap) return;
      const a = document.activeElement;
      if (a && wrap.contains(a) && a.tagName === 'INPUT') return;
      paint();
    }, 250);
  }

  const isAdmin = () => !!(data && data.admin);
  /// Скільки заявок в обробці чекає адміна.
  const deskCount = () => (isAdmin() ? count + sales : 0);

  /// Кружечок на 🏺 у шапці — адміну: скільки заявок в обробці. Клік веде просто в «Заявки».
  function paintBadge() {
    const w = document.getElementById('hdrWallet');
    if (!w) return;
    let b = w.querySelector('.by-badge');
    const n = deskCount();
    if (!n) { if (b) b.remove(); return; }
    if (!b) {
      b = document.createElement('span'); b.className = 'chip badge by-badge'; w.appendChild(b);
      // Кружечок сидить у посиланні на профіль, але веде у вікно
      b.onclick = (e) => { e.preventDefault(); e.stopPropagation(); open({ tab: 'desk' }); };
    }
    b.textContent = n;
    b.title = 'Заявки в обробці: ' + n;
  }

  // ---------------------------------------------------------------- вікно

  async function open(opts) {
    const x = opts || {};
    if (!o) return;
    close();
    pack = 0; own = false; gift = !!x.for; to = x.for || ''; step = 'pick'; sellUah = 0; sellOwn = false; bankForm = '';
    wrap = document.createElement('div');
    wrap.className = 'modal by-modal';
    wrap.innerHTML = '<div class="card by-card" role="dialog" aria-modal="true" aria-label="Черепки за гривні"><div class="gwait"><span class="spin"></span> дивлюсь…</div></div>';
    wrap.addEventListener('click', (e) => { if (e.target === wrap) close(); });
    document.addEventListener('keydown', onKey, true);
    document.body.appendChild(wrap);
    await load();
    if (!wrap) return;
    tab = firstTab(x.tab);
    // Прийшли з «бракує N»: одразу найменший пакет, якого вистачить
    if (data && x.need) {
      const p = data.buy.packs.find((q) => q.shards >= x.need) || data.buy.packs[data.buy.packs.length - 1];
      if (p) pack = p.uah;
    }
    paint();
  }

  /// Увімкнено в конфігу (ShardShop:Buy / Sell): свіже з /api/shards, а до нього — з /api/me. Не знаємо — увімкнено.
  const switchOn = (k) => (data ? !!data[k].on : !(o && o.me.shards && o.me.shards[k] === false));
  const buyOn = () => switchOn('buy');
  const sellOn = () => switchOn('sell');

  /// Вимкнену вкладку ще видно, поки в ній є мої заявки: свою «в обробці» людина мусить бачити й могти скасувати.
  const tabs = () => [
    buyOn() || (data && data.buy.mine.length) ? 'buy' : null,
    sellOn() || (data && data.sell.mine.length) ? 'sell' : null,
    isAdmin() ? 'desk' : null,
  ].filter(Boolean);

  /// Яку вкладку відкрити: просили — ту; адміну, коли щось в обробці, — «Заявки»; інакше — купити.
  function firstTab(want) {
    const t = tabs();
    if (want && t.includes(want)) return want;
    if (deskCount()) return 'desk';
    return 'buy';
  }

  function onKey(e) { if (e.key === 'Escape') { e.stopPropagation(); close(); } }

  function close() {
    if (wrap) wrap.remove();
    wrap = null;
    document.removeEventListener('keydown', onKey, true);
  }

  function paint() {
    if (!wrap) return;
    const box = wrap.querySelector('.by-card');
    const d = data;
    let h = '<h3><img src="/static/glek.svg" alt=""><span>Черепки за гривні</span>'
      + '<button type="button" class="ghost by-x" data-close title="Закрити — Esc" aria-label="Закрити">✕</button></h3>';
    if (!d) h += '<div class="gempty">Сервер не відповів — спробуй трохи згодом.</div>';
    else if (!tabs().length) h += '<div class="gempty glek">Обмін черепків на гривні на цьому сайті вимкнено.</div>';
    else {
      const t = tabs();
      if (!t.includes(tab)) tab = t[0];
      const title = { buy: '➕ Купити', sell: '💸 Продати', desk: '📋 Заявки' };
      h += '<div class="by-tabs" role="tablist">' + t.map((k) => '<button type="button" role="tab" data-tab="' + k + '" class="' + (k === tab ? 'on' : '')
        + '" aria-selected="' + (k === tab) + '">' + title[k] + (k === 'desk' && deskCount() ? ' <span class="chip badge">' + deskCount() + '</span>' : '')
        + '</button>').join('') + '</div>';
      if (tab === 'buy') h += buyHtml(d);
      else if (tab === 'sell') h += sellHtml(d);
      else h += deskHtml(d);
    }
    box.innerHTML = h;
    wire(box);
  }

  function lockHtml(verb) {
    return '<div class="by-lock">🔒 Черепки ' + verb + ' лише акаунти: гостьовий нік може зайняти хтось інший, і черепки пропали б.</div>'
      + '<div class="row"><button class="primary" type="button" data-acc>Закріпити нік</button></div>';
  }

  /// Статус заявки для того, хто її створив: в обробці — з «Скасувати», підтверджено — уже без.
  function statusChip(x, doneText) {
    if (x.status === 'wait') return '<span class="chip warn">⏳ в обробці</span>';
    if (x.status === 'done') return '<span class="chip ok">✓ ' + doneText + '</span>';
    if (x.status === 'no') return '<span class="chip err">✕ відхилено</span>';
    return '<span class="chip">скасовано</span>';
  }

  // ---------------------------------------------------------------- купити

  function buyHtml(d) {
    const b = d.buy;
    if (!b.on) return '<div class="gempty glek">Купівлю черепків вимкнено.</div>' + (d.account ? mineHtml(b) : '');
    if (!b.open) {
      return '<div class="gempty glek">Купівля черепків ще не відкрита.'
        + (d.admin ?'<br><span class="muted small">Впиши картку, куди покупцям скидати гроші, — у вкладці «📋 Заявки».</span>' : '') + '</div>'
        + (d.account ? mineHtml(b) : '');
    }
    if (!d.account) return lockHtml('купують');
    const p = b.packs.find((x) => x.uah === pack) || (own && pack ? { uah: pack, shards: pack * b.rate } : null);
    return (step === 'pay' && p ? payHtml(b, p) : pickHtml(b)) + mineHtml(b);
  }

  function pickHtml(b) {
    const people = online();
    return '<div class="muted small">1 грн = ' + num(b.rate) + ' 🏺. Скидаєш гроші на картку сайту — щойно адмін побачить переказ, черепки впадуть.</div>'
      + '<div class="by-packs">' + b.packs.map((x) => '<button type="button" class="by-pack' + (x.uah === pack ? ' on' : '') + '" data-pack="' + x.uah
        + '" aria-pressed="' + (x.uah === pack) + '"><b>' + num(x.shards) + ' 🏺</b><span>' + x.uah + ' грн</span></button>').join('') + '</div>'
      + (b.custom ? ownHtml(b) : '')
      + '<div class="by-for"><span class="muted small">Кому</span><div class="by-seg">'
      + '<button type="button" data-to="me" class="' + (gift ? '' : 'on') + '" aria-pressed="' + !gift + '">Собі</button>'
      + '<button type="button" data-to="friend" class="' + (gift ? 'on' : '') + '" aria-pressed="' + gift + '">🎁 Другові</button></div>'
      + (gift
        ? '<input type="text" class="by-to" maxlength="40" autocomplete="off" placeholder="Нік друга — лише акаунт" aria-label="Нік друга" value="' + esc(to) + '">'
          + (people.length ? '<div class="by-chips">' + people.map((n) => '<button type="button" class="chip' + (same(n, to) ? ' on' : '') + '" data-who="'
            + esc(n) + '">' + esc(n) + '</button>').join('') + '</div>' : '')
        : '')
      + '</div>'
      + '<div class="row"><button type="button" class="primary" data-next' + (pack ? '' : ' disabled') + '>Далі — до оплати</button></div>';
  }

  /// Своя сума: два пов'язані поля — гривні й черепки. Вписав одне — друге рахується саме; черепки округлюються вгору
  /// до цілої гривні (2 550 🏺 → 26 грн → 2 600 🏺), бо платять цілими гривнями.
  function ownHtml(b) {
    const uah = own && pack ? pack : '';
    return '<div class="by-own' + (own ? ' on' : '') + '"><span class="muted small">Або своя сума — від ' + b.custom.min + ' до ' + num(b.custom.max) + ' грн</span>'
      + pairHtml(b.custom.min, b.custom.max, b.rate, uah) + '</div>';
  }

  /// Поля «грн = 🏺» (і для купівлі, і для продажу).
  function pairHtml(min, max, rate, uah) {
    return '<div class="by-own-row"><label class="by-own-f"><input type="number" inputmode="numeric" min="' + min + '" max="' + max
      + '" step="1" placeholder="' + min + '" aria-label="Скільки гривень" data-own="uah" value="' + uah + '"><span>грн</span></label>'
      + '<span class="by-eq">=</span>'
      + '<label class="by-own-f"><input type="number" inputmode="numeric" min="0" step="' + rate + '" placeholder="' + num(min * rate).replace(/\s/g, '')
      + '" aria-label="Скільки черепків" data-own="shards" value="' + (uah ? uah * rate : '') + '"><span>🏺</span></label></div>';
  }

  function payHtml(b, p) {
    const banks = b.banks || [];
    const who = gift && to ? esc(to.trim()) + ' отримає' : 'отримаєш';
    return '<div class="by-sum">Скинь <b>' + p.uah + ' грн</b> на картку нижче — ' + who + ' <b>' + num(p.shards) + ' 🏺</b></div>'
      + '<div class="by-banks">' + banks.map((x) => bankRow(x, p.uah, 'Глечики: ' + (o.me.nick || ''))).join('') + '</div>'
      + '<div class="muted small">У коментарі до переказу напиши свій нік — так адмін швидше знайде платіж.</div>'
      + '<div class="row"><button type="button" class="primary" data-paid>✓ Скинув</button><button type="button" class="ghost" data-back>← Інший пакет</button></div>'
      + '<div class="muted small">Тисни «Скинув», коли гроші вже пішли. Поки адмін не підтвердив — можна скасувати.</div>';
  }

  /// Банка mono сама підставить суму й коментар, якщо передати їх у посиланні (a — сума, t — коментар).
  function jarLink(link, uah, comment) {
    try {
      const u = new URL(link);
      if (u.hostname !== 'send.monobank.ua') return link;
      u.searchParams.set('a', String(uah));
      u.searchParams.set('t', comment);
      return u.toString();
    } catch { return link; }
  }

  /// Рядок банку: назва, картка з «📋», банка ↗ (з сумою, якщо її знаємо). tail — що дописати в кінець рядка.
  function bankRow(b, uah, comment, tail) {
    return '<div class="by-bank"><span class="by-bk by-' + esc(b.bank) + '">' + esc(BANKS[b.bank] || BANKS.other) + '</span>'
      + (b.title ? '<span class="by-btitle">' + esc(b.title) + '</span>' : '')
      + (b.card ? '<span class="by-cardno">' + esc(card(b.card)) + '</span><button type="button" class="by-copy" data-copy="' + esc(b.card) + '">📋 Скопіювати</button>' : '')
      + (b.link ? '<a class="by-jar" href="' + esc(uah ? jarLink(b.link, uah, comment) : b.link) + '" target="_blank" rel="noopener noreferrer">🫙 Банка ↗</a>' : '')
      + (tail || '') + '</div>';
  }

  function mineHtml(b) {
    const list = b.mine || [];
    if (!list.length) return '';
    return '<h4>Мої покупки</h4><div class="by-list">' + list.slice(0, 8).map((x) => {
      const mine = same(x.buyer, o.me.nick);
      const who = x.gift ? (mine ? '🎁 ' + esc(dat(x.for)) : '🎁 від ' + esc(gen(x.buyer))) : '';
      return '<div class="by-ord st-' + esc(x.status) + '"><div class="by-ord-h"><b>' + num(x.shards) + ' 🏺</b><span>' + x.uah + ' грн</span>'
        + (who ? '<span class="muted small">' + who + '</span>' : '') + statusChip(x, 'зараховано') + '</div>'
        + '<div class="muted small">' + esc(o.dayTime(x.doneAt || x.at)) + '</div>'
        + (x.status === 'wait' && mine ? '<button type="button" class="ghost by-cancel" data-cancel="' + x.id + '">Скасувати</button>' : '')
        + '</div>';
    }).join('') + '</div>';
  }

  /// Кому подарувати: хто зараз на сайті, без гостей і без мене (сервер однаково перевірить, що це акаунт).
  function online() {
    const seen = new Set();
    return ((o.online && o.online()) || []).filter((n) => {
      const k = String(n || '').toLowerCase();
      if (!n || same(n, o.me.nick) || /^гість\s/i.test(String(n)) || seen.has(k)) return false;
      seen.add(k);
      return true;
    });
  }

  // ---------------------------------------------------------------- продати

  function sellHtml(d) {
    const s = d.sell;
    if (!s.open) return '<div class="gempty glek">Продаж черепків зараз закритий.</div>' + (d.account ? mySalesHtml(s) : '');
    if (!d.account) return lockHtml('продають');
    const max = Math.floor(s.balance / s.rate);
    let h = '<div class="muted small">1 грн = ' + num(s.rate) + ' 🏺. Виставляєш черепки — адмін переказує гроші тобі на картку чи в банку й підтверджує. '
      + 'Поки не підтвердив, можна скасувати — черепки повернуться.</div>'
      + '<div class="by-sum">У глечику <b>' + num(s.balance) + ' 🏺</b>' + (max >= s.min ? ' — це до <b>' + num(max) + ' грн</b>' : '') + '</div>';
    if (max < s.min) {
      h += '<div class="by-lock">Поки замало: продати можна від ' + num(s.min * s.rate) + ' 🏺 (' + s.min + ' грн).</div>';
      return h + mySalesHtml(s);
    }
    const packs = s.packs.filter((x) => x.uah <= max);
    const uah = sellOwn && sellUah ? sellUah : '';
    h += (packs.length ? '<div class="by-packs">' + packs.map((x) => '<button type="button" class="by-pack' + (!sellOwn && x.uah === sellUah ? ' on' : '')
        + '" data-spack="' + x.uah + '" aria-pressed="' + (!sellOwn && x.uah === sellUah) + '"><b>' + num(x.shards) + ' 🏺</b><span>' + x.uah + ' грн</span></button>').join('') + '</div>' : '')
      + '<div class="by-own' + (sellOwn ? ' on' : '') + '"><span class="muted small">' + (packs.length ? 'Або своя сума' : 'Скільки') + ' — від ' + s.min + ' до ' + num(max)
      + ' грн <button type="button" class="ghost by-all" data-sall="' + max + '">усе — ' + num(max) + ' грн</button></span>'
      + pairHtml(s.min, max, s.rate, uah) + '</div>'
      + '<h4>Куди переказати гроші</h4>' + banksHtml(s.banks || [], 'mine')
      + '<div class="row"><button type="button" class="primary" data-sell' + (sellUah && (s.banks || []).length ? '' : ' disabled') + '>'
      + (sellUah ? 'Продати ' + num(sellUah * s.rate) + ' 🏺 за ' + num(sellUah) + ' грн' : 'Продати') + '</button></div>';
    return h + mySalesHtml(s);
  }

  /// Банки й форма «додати картку». mine — мої з профілю Падельні (куди адмін перекаже), shop — сайту (куди скидають
  /// покупці; адмін їх і прибирає).
  function banksHtml(banks, whose) {
    const shop = whose === 'shop';
    let h = banks.length
      ? '<div class="by-banks">' + banks.map((b, i) => bankRow(b, 0, '', shop
        ? '<button type="button" class="ghost by-bdel" data-bdel="' + i + '" title="Прибрати" aria-label="Прибрати">✕</button>' : '')).join('') + '</div>'
      : '<div class="by-lock">' + (shop
        ? 'Карток сайту ще нема — покупці не знають, куди скидати, і купівля закрита. Впиши картку чи банку.'
        : 'Впиши картку чи банку — сюди адмін перекаже гроші. Збережеться й у Падельні (👤 Я → Мої банки).') + '</div>';
    if (bankForm === whose || !banks.length) {
      h += '<div class="by-bform" data-bform="' + whose + '"><div class="by-chips">' + Object.keys(BANKS).map((k) => '<button type="button" class="chip' + (k === bankKind ? ' on' : '')
        + '" data-bk="' + k + '" aria-pressed="' + (k === bankKind) + '">' + esc(BANKS[k]) + '</button>').join('') + '</div>'
        + '<input type="text" inputmode="numeric" autocomplete="off" maxlength="19" placeholder="Номер картки — 16 цифр" aria-label="Номер картки" data-bcard>'
        + '<input type="text" inputmode="url" maxlength="300" autocomplete="off" placeholder="або посилання на банку: https://send.monobank.ua/jar/…" aria-label="Посилання на банку" data-blink>'
        + '<div class="row"><button type="button" class="primary" data-bsave="' + whose + '">Зберегти</button>'
        + (banks.length ? '<button type="button" class="ghost" data-bcancel>Скасувати</button>' : '') + '</div></div>';
    } else {
      h += '<div class="row"><button type="button" class="ghost by-small" data-badd="' + whose + '">+ Ще картка</button>'
        + (shop ? '' : '<a class="by-small muted" href="/padel/" target="_blank" rel="noopener">змінити в Падельні ↗</a>') + '</div>';
    }
    return h;
  }

  function mySalesHtml(s) {
    const list = s.mine || [];
    if (!list.length) return '';
    return '<h4>Мої продажі</h4><div class="by-list">' + list.slice(0, 8).map((x) => '<div class="by-ord st-' + esc(x.status) + '"><div class="by-ord-h"><b>'
      + num(x.shards) + ' 🏺</b><span>' + num(x.uah) + ' грн</span>' + statusChip(x, 'продано') + '</div>'
      + '<div class="muted small">' + esc(o.dayTime(x.doneAt || x.at)) + (x.status === 'done' ? ' · гроші переказано' : '')
      + (x.status !== 'done' && x.note ? ' · «' + esc(x.note) + '»' : '') + '</div>'
      + (x.status === 'wait' ? '<button type="button" class="ghost by-cancel" data-scancel="' + x.id + '">Скасувати</button>' : '')
      + '</div>').join('') + '</div>';
  }

  // ---------------------------------------------------------------- заявки (адмін)

  function deskHtml(d) {
    const k = d.desk;
    if (!k) return '';
    const orders = k.orders || [];
    const sold = k.sales || [];
    let h = '';
    const buyBlock = '<h4 class="by-sect">🛒 Купують — перевір, чи прийшли гроші' + (orders.length ? ' <span class="chip badge">' + orders.length + '</span>' : '') + '</h4>'
      + (orders.length ? '<div class="by-list">' + orders.map(orderRow).join('') + '</div>' : '<div class="gempty small">Ніхто нічого не купує.</div>');
    const sellBlock = '<h4 class="by-sect">💰 Продають — перекажи гроші' + (sold.length ? ' <span class="chip badge">' + sold.length + '</span>' : '') + '</h4>'
      + (sold.length ? '<div class="by-list">' + sold.map(saleRow).join('') + '</div>' : '<div class="gempty small">Ніхто нічого не продає.</div>');
    // Спершу той розділ, де щось в обробці
    h += sold.length && !orders.length ? sellBlock + buyBlock : buyBlock + sellBlock;
    h += '<h4 class="by-sect">💳 Куди покупцям скидати гроші</h4>' + banksHtml(d.buy.banks || [], 'shop');
    if (!k.buyOn) h += '<div class="muted small">Купівлю вимкнено в налаштуваннях (ShardShop → Buy).</div>';
    if (!k.sellOn) h += '<div class="muted small">Продаж вимкнено в налаштуваннях (ShardShop → Sell).</div>';
    const closed = [].concat(
      (k.recentOrders || []).map((x) => ({ at: x.doneAt || x.at, html: closedRow('🛒 ' + esc(x.buyer) + (x.gift ? ' → ' + esc(x.for) : ''), x, 'зараховано') })),
      (k.recentSales || []).map((x) => ({ at: x.doneAt || x.at, html: closedRow('💰 ' + esc(x.seller), x, 'продано') })),
    ).sort((a, b) => String(b.at).localeCompare(String(a.at))).slice(0, 10);
    if (closed.length) h += '<h4>Закриті</h4><div class="by-list by-recent">' + closed.map((x) => x.html).join('') + '</div>';
    if (k.monthIn || k.monthOut) {
      h += '<div class="muted small">Цього місяця: прийшло за куплене <b>' + num(k.monthIn) + ' грн</b>, виплачено за продане <b>' + num(k.monthOut) + ' грн</b></div>';
    }
    return h;
  }

  function closedRow(who, x, doneText) {
    return '<div class="by-ord st-' + esc(x.status) + '"><div class="by-ord-h"><b>' + who + '</b><span>' + num(x.uah) + ' грн</span>' + statusChip(x, doneText) + '</div>'
      + '<div class="muted small">' + esc(o.dayTime(x.doneAt || x.at)) + (x.status === 'done' && x.doneBy ? ' · ' + esc(x.doneBy) : '') + '</div></div>';
  }

  function orderRow(x) {
    return '<div class="by-ord st-wait"><div class="by-ord-h"><b>' + esc(x.buyer) + (x.gift ? ' → ' + esc(x.for) : '') + '</b>'
      + '<span class="by-uah">' + num(x.uah) + ' грн</span><span class="muted small">' + num(x.shards) + ' 🏺</span></div>'
      + '<div class="muted small">скинув ' + esc(o.dayTime(x.at)) + ' — звір переказ у банку</div>'
      + '<div class="row"><button type="button" class="primary" data-ok="' + x.id + '">✓ Підтвердити</button></div></div>';
  }

  function saleRow(x) {
    const banks = x.banks || [];
    return '<div class="by-ord st-wait"><div class="by-ord-h"><b>' + esc(x.seller) + '</b>'
      + '<span class="by-uah">' + num(x.uah) + ' грн</span><span class="muted small">' + num(x.shards) + ' 🏺</span></div>'
      + '<div class="muted small">виставлено ' + esc(o.dayTime(x.at)) + ' — перекажи ' + num(x.uah) + ' грн, тоді підтверди</div>'
      + (banks.length ? '<div class="by-banks">' + banks.map((b) => bankRow(b, x.uah, 'Глечики: за ' + num(x.shards) + ' черепків')).join('') + '</div>'
        : '<div class="by-lock">Картки в ' + esc(gen(x.seller)) + ' уже нема — спитай, куди переказати.</div>')
      + '<div class="row"><button type="button" class="primary" data-sok="' + x.id + '">✓ Підтвердити</button></div></div>';
  }

  // ---------------------------------------------------------------- дії

  async function act(btn, label, path, body, after) {
    await o.busy(btn, label, async () => {
      try {
        const r = await o.api('POST', path, body);
        if (r && r.message) o.toast(r.message, 'ok');
        if (after) after(r);
      } catch (e) { o.toast(e.message, 'err'); }
      await load();
      paint();
    });
  }

  async function copy(text) {
    try { await navigator.clipboard.writeText(text); }
    catch {
      // http без безпечного контексту — старим способом
      const t = document.createElement('textarea');
      t.value = text; t.style.position = 'fixed'; t.style.opacity = '0';
      document.body.appendChild(t); t.select();
      try { document.execCommand('copy'); } catch { /* ну й гаразд */ }
      t.remove();
    }
    o.toast('📋 Номер картки скопійовано', 'ok');
  }

  function wire(box) {
    const on = (sel, fn) => box.querySelectorAll(sel).forEach((b) => { b.onclick = (e) => fn(b, e); });
    on('[data-close]', () => close());
    on('[data-tab]', (b) => { tab = b.dataset.tab; bankForm = ''; paint(); });
    on('[data-acc]', () => { close(); o.askNick(true, 'register', String(o.me.nick || '').replace(/^гість\s*/i, '')); });
    on('[data-copy]', (b) => copy(b.dataset.copy));
    wireBanks(box, on);
    if (tab === 'buy') wireBuy(box, on);
    else if (tab === 'sell') wireSell(box, on);
    else {
      on('[data-ok]', (b) => act(b, 'підтверджую…', '/api/shards/' + b.dataset.ok + '/ok'));
      on('[data-sok]', (b) => act(b, 'підтверджую…', '/api/shards/sale/' + b.dataset.sok + '/ok'));
    }
  }

  function wireBuy(box, on) {
    on('[data-pack]', (b) => { pack = +b.dataset.pack; own = false; paint(); });
    wirePair(box, () => data.buy.rate, (u) => {
      own = u > 0; pack = u;
      box.querySelectorAll('[data-pack]').forEach((b) => { b.classList.remove('on'); b.setAttribute('aria-pressed', 'false'); });
      box.querySelector('.by-own').classList.toggle('on', own);
      const nb = box.querySelector('[data-next]');
      if (nb) nb.disabled = !pack;
    }, Math.ceil, () => next(box, box.querySelector('[data-next]')));
    on('[data-to]', (b) => {
      gift = b.dataset.to === 'friend';
      paint();
      const inp = box.querySelector('.by-to');
      if (inp) inp.focus();
    });
    const inp = box.querySelector('.by-to');
    if (inp) {
      inp.oninput = () => {
        to = inp.value;
        box.querySelectorAll('[data-who]').forEach((c) => c.classList.toggle('on', same(c.dataset.who, to.trim())));
      };
      inp.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); next(box, box.querySelector('[data-next]')); } };
    }
    on('[data-who]', (b) => { to = b.dataset.who; paint(); });
    on('[data-next]', (b) => next(box, b));
    on('[data-back]', () => { step = 'pick'; paint(); });
    on('[data-paid]', (b) => act(b, 'записую…', '/api/shards/paid', { uah: pack, for: gift ? to.trim() : null }, (r) => {
      if (r && r.ok) { step = 'pick'; pack = 0; own = false; gift = false; to = ''; }
    }));
    on('[data-cancel]', (b) => act(b, 'скасовую…', '/api/shards/' + b.dataset.cancel + '/cancel'));
  }

  function wireSell(box, on) {
    const s = data.sell;
    const max = Math.floor(s.balance / s.rate);
    const sellBtn = () => {
      const b = box.querySelector('[data-sell]');
      if (!b) return;
      b.disabled = !sellUah || !(s.banks || []).length;
      b.textContent = sellUah ? 'Продати ' + num(sellUah * s.rate) + ' 🏺 за ' + num(sellUah) + ' грн' : 'Продати';
    };
    on('[data-spack]', (b) => { sellUah = +b.dataset.spack; sellOwn = false; paint(); });
    on('[data-sall]', (b) => { sellUah = +b.dataset.sall; sellOwn = true; paint(); });
    // Продаю цілими гривнями й не більше, ніж є: черепки округлюються вниз (2 550 🏺 → 25 грн → 2 500 🏺)
    wirePair(box, () => s.rate, (u) => {
      sellUah = u > max ? 0 : u; sellOwn = u > 0;
      box.querySelectorAll('[data-spack]').forEach((b) => { b.classList.remove('on'); b.setAttribute('aria-pressed', 'false'); });
      box.querySelector('.by-own').classList.toggle('on', sellOwn);
      sellBtn();
    }, Math.floor, () => { const b = box.querySelector('[data-sell]'); if (b && !b.disabled) b.click(); });
    on('[data-sell]', (b) => {
      if (!sellUah || sellUah < s.min) { o.toast('Продати можна від ' + s.min + ' грн', 'err'); return; }
      act(b, 'виставляю…', '/api/shards/sell', { uah: sellUah }, (r) => { if (r && r.ok) { sellUah = 0; sellOwn = false; } });
    });
    on('[data-scancel]', (b) => act(b, 'скасовую…', '/api/shards/sale/' + b.dataset.scancel + '/cancel'));
  }

  /// Форма картки (і моя, і сайту): вибір банку, номер по 4 цифри, зберегти; картку сайту адмін ще й прибирає ✕.
  function wireBanks(box, on) {
    on('[data-bk]', (b) => {
      bankKind = b.dataset.bk;
      box.querySelectorAll('[data-bk]').forEach((c) => { c.classList.toggle('on', c === b); c.setAttribute('aria-pressed', String(c === b)); });
    });
    const cardIn = box.querySelector('[data-bcard]');
    if (cardIn) cardIn.oninput = () => { const v = card(cardIn.value).slice(0, 19); if (v !== cardIn.value) cardIn.value = v; };
    on('[data-badd]', (b) => { bankForm = b.dataset.badd; paint(); const c = box.querySelector('[data-bcard]'); if (c) c.focus(); });
    on('[data-bcancel]', () => { bankForm = ''; paint(); });
    on('[data-bsave]', (b) => saveBank(box, b, b.dataset.bsave));
    on('[data-bdel]', (b) => {
      const list = (data.buy.banks || []).filter((x, i) => i !== +b.dataset.bdel);
      if (!confirm(list.length ? 'Прибрати цю картку?' : 'Прибрати останню картку? Купівля закриється, поки не впишеш нову.')) return;
      putBanks(b, 'shop', list);
    });
  }

  const plain = (b) => ({ bank: b.bank, title: b.title || '', card: b.card || null, link: b.link || null });

  /// Нова картка чи банка — до тих, що вже є: моя — у профіль Падельні, сайту — у /api/shards/banks.
  async function saveBank(box, btn, whose) {
    const digits = (box.querySelector('[data-bcard]').value || '').replace(/\D/g, '');
    const link = (box.querySelector('[data-blink]').value || '').trim();
    if (!digits && !link) { o.toast('Впиши номер картки або посилання на банку', 'err'); return; }
    const was = whose === 'shop' ? data.buy.banks : data.sell.banks;
    await putBanks(btn, whose, (was || []).concat([{ bank: bankKind, title: '', card: digits || null, link: link || null }]));
  }

  async function putBanks(btn, whose, list) {
    await o.busy(btn, 'зберігаю…', async () => {
      try {
        const r = await o.api('PUT', whose === 'shop' ? '/api/shards/banks' : '/api/padel/banks', { banks: list.map(plain) });
        o.toast(whose === 'shop' ? (r && r.message) || 'Збережено' : 'Картку збережено — адмін бачитиме її біля заявки', 'ok');
        bankForm = '';
      } catch (e) { o.toast(e.message, 'err'); return; }
      await load();
      paint();
    });
  }

  /// Поля «грн = 🏺»: пишемо в одне — друге й вибір оновлюються на місці, без перемальовування (інакше губився б курсор).
  /// round — як черепки стають гривнями: купівля вгору (платять цілими гривнями), продаж униз (не більше, ніж є).
  function wirePair(box, rate, take, round, enter) {
    const ou = box.querySelector('[data-own="uah"]');
    const os = box.querySelector('[data-own="shards"]');
    if (!ou || !os) return;
    ou.oninput = () => { const u = Math.max(0, Math.floor(+ou.value) || 0); os.value = u ? u * rate() : ''; take(u); };
    os.oninput = () => { const sh = Math.max(0, Math.floor(+os.value) || 0); const u = sh ? round(sh / rate()) : 0; ou.value = u || ''; take(u); };
    os.onblur = () => { const u = Math.max(0, Math.floor(+ou.value) || 0); if (u) os.value = u * rate(); };
    for (const i of [ou, os]) i.onkeydown = (e) => { if (e.key === 'Enter') { e.preventDefault(); enter(); } };
  }

  /// «Далі — до оплати»: сервер спершу каже, чи можна (друг — акаунт, заявок не забагато), і лише тоді — реквізити:
  /// дізнатись про «не акаунт» після того, як гроші пішли, — найгірше, що тут може статись.
  async function next(box, btn) {
    if (!pack || !btn) return;
    if (gift && !to.trim()) {
      o.toast('Кому купуємо? Впиши нік друга', 'err');
      const inp = box.querySelector('.by-to');
      if (inp) inp.focus();
      return;
    }
    await o.busy(btn, 'перевіряю…', async () => {
      try {
        const r = await o.api('POST', '/api/shards/check', { uah: pack, for: gift ? to.trim() : null });
        if (r && r.order && gift) to = r.order.for;
        step = 'pay';
        paint();
      } catch (e) { o.toast(e.message, 'err'); }
    });
  }

  window.HBuy = {
    init(opts) { o = opts; if (o.esc) esc = o.esc; },
    /// Після /api/me: адміну — кружечок у шапці одразу, а не з першим відкриттям вікна.
    ready() { if (o && (o.me.account || o.me.role === 'admin')) load(); },
    attach(conn) {
      conn.on('shardOrders', (m) => { count = (m && m.count) || 0; sales = (m && m.sales) || 0; paintBadge(); if (wrap) soon(); });
      // Адмін підтвердив мою заявку — вікно свіже без F5
      conn.on('shardMine', () => { if (wrap) soon(); });
      // Зарахували, відклали чи повернули — вікно й «У глечику» в Лавці свіжі без F5
      conn.on('wallet', (w) => {
        if (!w || !/^(buy|sell)/.test(String(w.reason || ''))) return;
        if (wrap) soon();
        if (window.HLavka && HLavka.refresh) HLavka.refresh();
      });
    },
    open,
    buyOn,
    sellOn,
    /// Підпис першої кнопки в профілі: адміну — «Заявки» з тим, скільки в обробці; решті — «Купити».
    label() {
      if (isAdmin()) { const n = deskCount(); return '📋 Заявки' + (n ? ' · ' + n : ''); }
      return '➕ Купити';
    },
    /// Куди веде перша кнопка профілю: адміну — у «Заявки», решті — купити.
    mainTab: () => (isAdmin() ? 'desk' : 'buy'),
    /// Друга кнопка профілю.
    sellLabel: () => '💸 Продати',
  };
})();
