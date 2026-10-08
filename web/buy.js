/*
  Черепки за гривні — window.HBuy. Як гроші в Падельні (08.10.2026), три вкладки:

  ➕ Купити — обираєш пакет, бачиш картку й банку продавця (його банки з профілю Падельні), скидаєш гроші й тиснеш
  «✓ Скинув». Продавець бачить замовлення в касі й тисне «✓ Отримав» — черепки падають одразу (тост гаманця шле сервер) —
  або «✕ Не прийшло» з приміткою. Можна купити другові.

  💸 Продати — купує сайт, керує адмін. Виставляєш «N 🏺 за X грн» — черепки одразу відкладаються; адмін бачить твою
  картку (ті самі банки з Падельні; нема — впишеш тут же, і вони збережуться в Падельні), скидає гроші й тисне «✓ Скинув»
  або «✕ Не куплю» (черепки назад). Ти тиснеш «✓ Отримав» — продано; «✕ Не прийшло» — заявка знову в адміна. Поки адмін
  не скинув — можна скасувати.

  🧾 Каса — продавцю купівлі (оплати, що чекають) і адміну (оплати й заявки на продаж).

  Сервер — ShardShop.cs: GET /api/shards; купівля: POST /api/shards/check { uah, for } («Далі» — перевірка до реквізитів),
  /paid { uah, for }, /{id}/ok, /{id}/no { note }, /{id}/cancel; продаж: /sell { uah }, /sale/{id}/cancel, /sale/{id}/ok,
  /sale/{id}/missing { note } — гравець; /sale/{id}/paid, /sale/{id}/no { note } — адмін. Банки — PUT /api/padel/banks.
  Події хаба: shardOrders { count, sales } — скільки чекає (кружечок на гаманці в шапці продавця й адміна); shardSale —
  моя заявка змінилась.
*/
(() => {
  'use strict';

  let o = null;                        // що дає app.js
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  let data = null;                     // останнє GET /api/shards
  let count = 0;                       // скільки оплат чекає продавця
  let sales = 0;                       // скільки заявок на продаж чекає адміна
  let wrap = null;                     // відкрите вікно
  let tab = 'buy';                     // buy — купити, sell — продати, desk — каса
  let pack = 0;                        // обраний пакет чи своя сума, грн
  let own = false;                     // своя сума, а не пакет
  let gift = false;                    // купуємо другові
  let to = '';                         // кому (нік друга)
  let step = 'pick';                   // pick — пакет і кому, pay — реквізити й «✓ Скинув»
  let refusing = 0;                    // замовлення, для якого відкрите «чому не прийшло»
  let note = '';
  let sellUah = 0;                     // скільки гривень хочу за черепки
  let sellOwn = false;                 // своя сума, а не пакет
  let bankForm = false;                // відкрита форма «додати картку»
  let bankKind = 'mono';
  let saleNote = 0;                    // заявка, для якої відкрите поле примітки («не прийшло» гравця чи «не куплю» адміна)
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
    count = data && data.waiting ? data.waiting.length : 0;
    sales = data && data.sales ? data.sales.waiting.length : 0;
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

  /// Мої заявки, де адмін уже скинув гроші, а я ще не сказав «✓ Отримав».
  const myPaid = () => (data && data.sell && data.sell.mine ? data.sell.mine.filter((x) => x.status === 'paid').length : 0);
  /// Що чекає в касі: оплати — продавцю й адміну, заявки на продаж — адміну.
  const deskCount = () => (data && data.canConfirm ? count : 0) + (data && data.admin ? sales : 0);

  /// Кружечок на 🏺 у шапці: продавцю й адміну — скільки чекає в касі; гравцю — скільки грошей уже скинуто на перевірку.
  function paintBadge() {
    const w = document.getElementById('hdrWallet');
    if (!w) return;
    let b = w.querySelector('.by-badge');
    const desk = deskCount();
    const paid = myPaid();
    const n = desk + paid;
    if (!n) { if (b) b.remove(); return; }
    if (!b) {
      b = document.createElement('span'); b.className = 'chip badge by-badge'; w.appendChild(b);
      // Кружечок сидить у посиланні на профіль, але веде просто у вікно: у касу, а гравцю — до скинутих грошей
      b.onclick = (e) => { e.preventDefault(); e.stopPropagation(); open({ tab: deskCount() ? 'desk' : 'sell' }); };
    }
    b.textContent = n;
    b.title = [desk ? 'Чекають у касі: ' + desk : '', paid ? 'Адмін скинув гроші — перевір і натисни «✓ Отримав»: ' + paid : ''].filter(Boolean).join(' · ');
  }

  // ---------------------------------------------------------------- вікно

  async function open(opts) {
    const x = opts || {};
    if (!o) return;
    close();
    pack = 0; own = false; gift = !!x.for; to = x.for || ''; step = 'pick'; refusing = 0; note = '';
    sellUah = 0; sellOwn = false; bankForm = false; saleNote = 0;
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
    if (data && data.packs && x.need) {
      const p = data.packs.find((q) => q.shards >= x.need) || data.packs[data.packs.length - 1];
      if (p) pack = p.uah;
    }
    paint();
  }

  function tabs(d) {
    const t = [];
    if (!d.isSeller) t.push('buy');
    t.push('sell');
    if (d.canConfirm || d.admin) t.push('desk');
    return t;
  }

  /// Яку вкладку відкрити: просили — ту; щось чекає в касі — касу; адмін скинув мені гроші — продаж; інакше — купити.
  function firstTab(want) {
    const d = data;
    if (!d) return 'buy';
    const t = tabs(d);
    if (want && t.includes(want)) return want;
    if (deskCount() && t.includes('desk')) return 'desk';
    if (myPaid()) return 'sell';
    return t[0];
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
    else {
      const t = tabs(d);
      if (!t.includes(tab)) tab = t[0];
      const title = { buy: '➕ Купити', sell: '💸 Продати', desk: '🧾 Каса' };
      const badge = { buy: 0, sell: myPaid(), desk: deskCount() };
      h += '<div class="by-tabs" role="tablist">' + t.map((k) => '<button type="button" role="tab" data-tab="' + k + '" class="' + (k === tab ? 'on' : '')
        + '" aria-selected="' + (k === tab) + '">' + title[k] + (badge[k] ? ' <span class="chip badge">' + badge[k] + '</span>' : '') + '</button>').join('') + '</div>';
      if (tab === 'buy') h += buyerHtml(d);
      else if (tab === 'sell') h += sellHtml(d);
      else {
        // Каса: спершу той розділ, де щось чекає (заявки на продаж — коли оплат купівлі нема)
        const orders = d.canConfirm && (d.open || count) ? sellerHtml(d) : '';
        const sold = d.admin ? deskSalesHtml(d) : '';
        h += sales && !count ? sold + orders : orders + sold;
      }
    }
    box.innerHTML = h;
    wire(box);
  }

  // ---------------------------------------------------------------- купити

  function buyerHtml(d) {
    if (!d.open) {
      return '<div class="gempty glek">Купівля черепків ще не відкрита.'
        + (d.admin ? '<br><span class="muted small">Задай продавця: ShardShop → Seller в appsettings.Local.json.</span>' : '') + '</div>';
    }
    if (!d.account) return lockHtml('купують');
    const p = d.packs.find((x) => x.uah === pack) || (own && pack ? { uah: pack, shards: pack * d.rate } : null);
    return (step === 'pay' && p ? payHtml(d, p) : pickHtml(d)) + mineHtml(d);
  }

  function lockHtml(verb) {
    return '<div class="by-lock">🔒 Черепки ' + verb + ' лише акаунти: гостьовий нік може зайняти хтось інший, і черепки пропали б.</div>'
      + '<div class="row"><button class="primary" type="button" data-acc>Закріпити нік</button></div>';
  }

  function pickHtml(d) {
    const people = online();
    return '<div class="muted small">1 грн = ' + num(d.rate) + ' 🏺. Скидаєш гроші ' + esc(dat(d.seller.nick))
      + ' на картку чи в банку — і щойно гроші прийдуть, черепки впадуть.</div>'
      + '<div class="by-packs">' + d.packs.map((x) => '<button type="button" class="by-pack' + (x.uah === pack ? ' on' : '') + '" data-pack="' + x.uah
        + '" aria-pressed="' + (x.uah === pack) + '"><b>' + num(x.shards) + ' 🏺</b><span>' + x.uah + ' грн</span></button>').join('') + '</div>'
      + (d.custom ? ownHtml(d) : '')
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
  function ownHtml(d) {
    const uah = own && pack ? pack : '';
    return '<div class="by-own' + (own ? ' on' : '') + '"><span class="muted small">Або своя сума — від ' + d.custom.min + ' до ' + num(d.custom.max) + ' грн</span>'
      + pairHtml(d.custom.min, d.custom.max, d.rate, uah) + '</div>';
  }

  /// Поля «грн = 🏺» (і для купівлі, і для продажу).
  function pairHtml(min, max, rate, uah) {
    return '<div class="by-own-row"><label class="by-own-f"><input type="number" inputmode="numeric" min="' + min + '" max="' + max
      + '" step="1" placeholder="' + min + '" aria-label="Скільки гривень" data-own="uah" value="' + uah + '"><span>грн</span></label>'
      + '<span class="by-eq">=</span>'
      + '<label class="by-own-f"><input type="number" inputmode="numeric" min="0" step="' + rate + '" placeholder="' + num(min * rate).replace(/\s/g, '')
      + '" aria-label="Скільки черепків" data-own="shards" value="' + (uah ? uah * rate : '') + '"><span>🏺</span></label></div>';
  }

  function payHtml(d, p) {
    const banks = d.seller.banks || [];
    const who = gift && to ? esc(to.trim()) + ' отримає' : 'отримаєш';
    return '<div class="by-sum">Скинь <b>' + p.uah + ' грн</b> ' + esc(dat(d.seller.nick)) + ' — ' + who + ' <b>' + num(p.shards) + ' 🏺</b></div>'
      + (banks.length ? '<div class="by-banks">' + banks.map((b) => bankRow(b, p.uah, 'Глечики: ' + (o.me.nick || ''))).join('') + '</div>'
        : '<div class="by-lock">Картки чи банки продавця тут ще нема — спитай ' + esc(gen(d.seller.nick)) + ', куди скинути.</div>')
      + '<div class="muted small">У коментарі до переказу напиши свій нік — так ' + esc(d.seller.nick) + ' швидше знайде платіж.</div>'
      + '<div class="row"><button type="button" class="primary" data-paid>✓ Скинув</button><button type="button" class="ghost" data-back>← Інший пакет</button></div>'
      + '<div class="muted small">Тисни «Скинув», коли гроші вже пішли. Черепки впадуть, щойно ' + esc(d.seller.nick) + ' побачить переказ.</div>';
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

  function statusChip(x) {
    if (x.status === 'wait') return '<span class="chip warn">⏳ чекає ' + esc(gen(data.seller ? data.seller.nick : 'продавця')) + '</span>';
    if (x.status === 'done') return '<span class="chip ok">✓ зараховано</span>';
    if (x.status === 'no') return '<span class="chip err">✕ не прийшло</span>';
    return '<span class="chip">скасовано</span>';
  }

  function mineHtml(d) {
    const list = d.mine || [];
    if (!list.length) return '';
    return '<h4>Мої покупки</h4><div class="by-list">' + list.slice(0, 8).map((x) => {
      const mine = same(x.buyer, o.me.nick);
      const who = x.gift ? (mine ? '🎁 ' + esc(dat(x.for)) : '🎁 від ' + esc(gen(x.buyer))) : '';
      return '<div class="by-ord st-' + esc(x.status) + '"><div class="by-ord-h"><b>' + num(x.shards) + ' 🏺</b><span>' + x.uah + ' грн</span>'
        + (who ? '<span class="muted small">' + who + '</span>' : '') + statusChip(x) + '</div>'
        + '<div class="muted small">' + esc(o.dayTime(x.at)) + (x.status === 'no' && x.note ? ' · «' + esc(x.note) + '»' : '') + '</div>'
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
    if (!s || !s.open) return '<div class="gempty glek">Продаж черепків зараз закритий.</div>' + (s ? mySalesHtml(s) : '');
    if (!d.account) return lockHtml('продають');
    const max = Math.floor(s.balance / s.rate);
    let h = '<div class="muted small">1 грн = ' + num(s.rate) + ' 🏺. Виставляєш черепки — адмін скидає гроші тобі на картку чи в банку, '
      + 'а ти тиснеш «✓ Отримав». Поки адмін не скинув, можна скасувати — черепки повернуться.</div>'
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
      + '<h4>Куди скинути гроші</h4>' + myBanksHtml(s)
      + '<div class="row"><button type="button" class="primary" data-sell' + (sellUah && (s.banks || []).length ? '' : ' disabled') + '>'
      + (sellUah ? 'Продати ' + num(sellUah * s.rate) + ' 🏺 за ' + num(sellUah) + ' грн' : 'Продати') + '</button></div>';
    return h + mySalesHtml(s);
  }

  /// Мої банки (з профілю Падельні) і форма «додати картку» — сюди адмін скине гроші.
  function myBanksHtml(s) {
    const banks = s.banks || [];
    let h = banks.length
      ? '<div class="by-banks">' + banks.map((b) => bankRow(b, 0, '')).join('') + '</div>'
      : '<div class="by-lock">Впиши картку чи банку — сюди адмін скине гроші. Збережеться й у Падельні (👤 Я → Мої банки).</div>';
    if (bankForm || !banks.length) {
      h += '<div class="by-bform"><div class="by-chips">' + Object.keys(BANKS).map((k) => '<button type="button" class="chip' + (k === bankKind ? ' on' : '')
        + '" data-bk="' + k + '" aria-pressed="' + (k === bankKind) + '">' + esc(BANKS[k]) + '</button>').join('') + '</div>'
        + '<input type="text" inputmode="numeric" autocomplete="off" maxlength="19" placeholder="Номер картки — 16 цифр" aria-label="Номер картки" data-bcard>'
        + '<input type="text" inputmode="url" maxlength="300" autocomplete="off" placeholder="або посилання на банку: https://send.monobank.ua/jar/…" aria-label="Посилання на банку" data-blink>'
        + '<div class="row"><button type="button" class="primary" data-bsave>Зберегти</button>'
        + (banks.length ? '<button type="button" class="ghost" data-bcancel>Скасувати</button>' : '') + '</div></div>';
    } else {
      h += '<div class="row"><button type="button" class="ghost by-small" data-badd>+ Інша картка</button>'
        + '<a class="by-small muted" href="/padel/" target="_blank" rel="noopener">змінити в Падельні ↗</a></div>';
    }
    return h;
  }

  function saleChip(x) {
    if (x.status === 'wait') return '<span class="chip warn">⏳ чекає адміна</span>';
    if (x.status === 'paid') return '<span class="chip warn">💸 адмін скинув</span>';
    if (x.status === 'done') return '<span class="chip ok">✓ продано</span>';
    if (x.status === 'no') return '<span class="chip err">✕ не куплено</span>';
    return '<span class="chip">скасовано</span>';
  }

  function noteHtml(id, placeholder, label, act) {
    return '<div class="by-refuse"><input type="text" maxlength="100" placeholder="' + placeholder + '" aria-label="' + label + '" data-snote value="' + esc(note) + '">'
      + '<div class="row"><button type="button" class="primary" ' + act + '="' + id + '">' + label + '</button><button type="button" class="ghost" data-sunnote>Назад</button></div></div>';
  }

  function mySalesHtml(s) {
    const list = s.mine || [];
    if (!list.length) return '';
    return '<h4>Мої продажі</h4><div class="by-list">' + list.slice(0, 8).map((x) => {
      let tail = '';
      if (x.status === 'wait') {
        tail = (x.note ? '<div class="muted small">ти написав: «' + esc(x.note) + '» — адмін перевіряє</div>' : '')
          + (x.paidAt ? '' : '<button type="button" class="ghost by-cancel" data-scancel="' + x.id + '">Скасувати</button>');
      } else if (x.status === 'paid') {
        tail = '<div class="by-sum">Глянь у банк: прийшло <b>' + num(x.uah) + ' грн</b>?</div>'
          + (saleNote === x.id
            ? noteHtml(x.id, 'Що не так? Можна не писати', '✕ Не прийшло', 'data-smissing')
            : '<div class="row"><button type="button" class="primary" data-sok="' + x.id + '">✓ Отримав</button>'
              + '<button type="button" class="ghost" data-snoteopen="' + x.id + '">✕ Не прийшло</button></div>');
      }
      return '<div class="by-ord st-' + esc(x.status) + '"><div class="by-ord-h"><b>' + num(x.shards) + ' 🏺</b><span>' + num(x.uah) + ' грн</span>' + saleChip(x) + '</div>'
        + '<div class="muted small">' + esc(o.dayTime(x.paidAt || x.at)) + (x.status === 'no' && x.note ? ' · «' + esc(x.note) + '»' : '') + '</div>'
        + tail + '</div>';
    }).join('') + '</div>';
  }

  // ---------------------------------------------------------------- каса: купівля (продавець і адмін)

  function sellerHtml(d) {
    const w = d.waiting || [];
    const banks = (d.seller && d.seller.banks) || [];
    let h = '<h4 class="by-sect">🛒 Купують' + (d.isSeller || !d.seller ? '' : ' в ' + esc(gen(d.seller.nick))) + '</h4>';
    if (d.isSeller) {
      h += banks.length
        ? '<div class="muted small">Покупці бачать твої банки з Падельні (' + esc(banks.map((b) => BANKS[b.bank] || BANKS.other).join(', '))
          + '). Звір переказ у банку — і тисни «✓ Отримав».</div>'
        : '<div class="by-lock">Покупці не бачать, куди скидати: додай картку чи банку в <a href="/padel/" target="_blank" rel="noopener">Падельні → 👤 Я</a>.</div>';
    }
    h += '<h4>Чекають підтвердження' + (w.length ? ' <span class="chip badge">' + w.length + '</span>' : '') + '</h4>'
      + (w.length ? '<div class="by-list">' + w.map(waitRow).join('') + '</div>' : '<div class="gempty small">Ніхто нічого не скидав — тихо, як у глечику.</div>');
    const r = d.recent || [];
    if (r.length) {
      h += '<h4>Розглянуті</h4><div class="by-list by-recent">' + r.slice(0, 10).map((x) => '<div class="by-ord st-' + esc(x.status) + '"><div class="by-ord-h"><b>'
        + esc(x.buyer) + (x.gift ? ' → ' + esc(x.for) : '') + '</b><span>' + x.uah + ' грн</span>' + statusChip(x) + '</div>'
        + '<div class="muted small">' + esc(o.dayTime(x.doneAt || x.at)) + (x.note ? ' · «' + esc(x.note) + '»' : '') + '</div></div>').join('') + '</div>';
    }
    if (d.monthUah) h += '<div class="muted small">Цього місяця зараховано: <b>' + num(d.monthUah) + ' грн</b></div>';
    return h;
  }

  function waitRow(x) {
    return '<div class="by-ord st-wait"><div class="by-ord-h"><b>' + esc(x.buyer) + (x.gift ? ' → ' + esc(x.for) : '') + '</b>'
      + '<span class="by-uah">' + x.uah + ' грн</span><span class="muted small">' + num(x.shards) + ' 🏺</span></div>'
      + '<div class="muted small">натиснуто «Скинув» ' + esc(o.dayTime(x.at)) + '</div>'
      + (refusing === x.id
        ? '<div class="by-refuse"><input type="text" maxlength="100" placeholder="Чому? Можна не писати" aria-label="Чому оплата не прийшла" data-note value="' + esc(note) + '">'
          + '<div class="row"><button type="button" class="primary" data-no="' + x.id + '">✕ Не прийшло</button><button type="button" class="ghost" data-unrefuse>Назад</button></div></div>'
        : '<div class="row"><button type="button" class="primary" data-ok="' + x.id + '">✓ Отримав</button><button type="button" class="ghost" data-refuse="' + x.id + '">✕ Не прийшло</button></div>')
      + '</div>';
  }

  // ---------------------------------------------------------------- каса: продаж (адмін)

  function deskSalesHtml(d) {
    const s = d.sales || {};
    const w = s.waiting || [];
    let h = '<h4 class="by-sect">💰 Продають — скинь гроші' + (w.length ? ' <span class="chip badge">' + w.length + '</span>' : '') + '</h4>';
    h += w.length ? '<div class="by-list">' + w.map(saleWaitRow).join('') + '</div>' : '<div class="gempty small">Ніхто нічого не продає.</div>';
    const p = s.paid || [];
    if (p.length) {
      h += '<h4>Скинуто — чекають «✓ Отримав»</h4><div class="by-list by-recent">' + p.map((x) => '<div class="by-ord st-paid"><div class="by-ord-h"><b>' + esc(x.seller)
        + '</b><span>' + num(x.uah) + ' грн</span><span class="muted small">' + num(x.shards) + ' 🏺</span>' + saleChip(x) + '</div>'
        + '<div class="muted small">скинуто ' + esc(o.dayTime(x.paidAt || x.at)) + (x.paidBy ? ' · ' + esc(x.paidBy) : '') + '</div></div>').join('') + '</div>';
    }
    const r = s.recent || [];
    if (r.length) {
      h += '<h4>Закриті</h4><div class="by-list by-recent">' + r.slice(0, 10).map((x) => '<div class="by-ord st-' + esc(x.status) + '"><div class="by-ord-h"><b>'
        + esc(x.seller) + '</b><span>' + num(x.uah) + ' грн</span>' + saleChip(x) + '</div>'
        + '<div class="muted small">' + esc(o.dayTime(x.doneAt || x.at)) + (x.note ? ' · «' + esc(x.note) + '»' : '') + '</div></div>').join('') + '</div>';
    }
    if (s.monthUah) h += '<div class="muted small">Цього місяця виплачено: <b>' + num(s.monthUah) + ' грн</b></div>';
    return h;
  }

  function saleWaitRow(x) {
    const banks = x.banks || [];
    return '<div class="by-ord st-wait"><div class="by-ord-h"><b>' + esc(x.seller) + '</b>'
      + '<span class="by-uah">' + num(x.uah) + ' грн</span><span class="muted small">' + num(x.shards) + ' 🏺</span></div>'
      + '<div class="muted small">виставлено ' + esc(o.dayTime(x.at)) + (x.paidAt ? ' · скинуто ' + esc(o.dayTime(x.paidAt)) + (x.paidBy ? ' (' + esc(x.paidBy) + ')' : '') : '') + '</div>'
      + (x.note ? '<div class="by-warn">⚠ ' + esc(x.seller) + ': «' + esc(x.note) + '» — звір переказ: скинь ще раз або «Не куплю»</div>' : '')
      + (banks.length ? '<div class="by-banks">' + banks.map((b) => bankRow(b, x.uah, 'Глечики: за ' + num(x.shards) + ' черепків')).join('') + '</div>'
        : '<div class="by-lock">Картки в ' + esc(gen(x.seller)) + ' уже нема — спитай, куди скинути.</div>')
      + (saleNote === x.id
        ? noteHtml(x.id, 'Чому? Можна не писати', '✕ Не куплю', 'data-srefuse')
        : '<div class="row"><button type="button" class="primary" data-spaid="' + x.id + '">✓ Скинув</button>'
          + '<button type="button" class="ghost" data-snoteopen="' + x.id + '">✕ Не куплю</button></div>')
      + '</div>';
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
    on('[data-tab]', (b) => { tab = b.dataset.tab; refusing = 0; saleNote = 0; note = ''; paint(); });
    on('[data-acc]', () => { close(); o.askNick(true, 'register', String(o.me.nick || '').replace(/^гість\s*/i, '')); });
    on('[data-copy]', (b) => copy(b.dataset.copy));
    if (tab === 'buy') wireBuy(box, on);
    else if (tab === 'sell') wireSell(box, on);
    else wireDesk(box, on);
    const n = box.querySelector('[data-snote]');
    if (n) n.oninput = () => { note = n.value; };
    on('[data-snoteopen]', (b) => {
      saleNote = +b.dataset.snoteopen; note = '';
      paint();
      const f = box.querySelector('[data-snote]');
      if (f) f.focus();
    });
    on('[data-sunnote]', () => { saleNote = 0; paint(); });
  }

  function wireBuy(box, on) {
    on('[data-pack]', (b) => { pack = +b.dataset.pack; own = false; paint(); });
    wirePair(box, () => data.rate, (u) => {
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
    on('[data-bk]', (b) => {
      bankKind = b.dataset.bk;
      box.querySelectorAll('[data-bk]').forEach((c) => { c.classList.toggle('on', c === b); c.setAttribute('aria-pressed', String(c === b)); });
    });
    const cardIn = box.querySelector('[data-bcard]');
    if (cardIn) cardIn.oninput = () => { const v = card(cardIn.value).slice(0, 19); if (v !== cardIn.value) cardIn.value = v; };
    on('[data-badd]', () => { bankForm = true; paint(); const c = box.querySelector('[data-bcard]'); if (c) c.focus(); });
    on('[data-bcancel]', () => { bankForm = false; paint(); });
    on('[data-bsave]', (b) => saveBank(box, b));
    on('[data-scancel]', (b) => act(b, 'скасовую…', '/api/shards/sale/' + b.dataset.scancel + '/cancel'));
    on('[data-sok]', (b) => act(b, 'записую…', '/api/shards/sale/' + b.dataset.sok + '/ok'));
    on('[data-smissing]', (b) => act(b, 'позначаю…', '/api/shards/sale/' + b.dataset.smissing + '/missing', { note: note.trim() }, (r) => {
      if (r && r.ok) { saleNote = 0; note = ''; }
    }));
  }

  /// Нова картка чи банка — у профіль Падельні (там само її бачать боржники), до тих, що вже є.
  async function saveBank(box, btn) {
    const digits = (box.querySelector('[data-bcard]').value || '').replace(/\D/g, '');
    const link = (box.querySelector('[data-blink]').value || '').trim();
    if (!digits && !link) { o.toast('Впиши номер картки або посилання на банку', 'err'); return; }
    const list = (data.sell.banks || []).map((b) => ({ bank: b.bank, title: b.title || '', card: b.card || null, link: b.link || null }));
    list.push({ bank: bankKind, title: '', card: digits || null, link: link || null });
    await o.busy(btn, 'зберігаю…', async () => {
      try {
        await o.api('PUT', '/api/padel/banks', { banks: list });
        o.toast('Картку збережено — адмін бачитиме її біля заявки', 'ok');
        bankForm = false;
      } catch (e) { o.toast(e.message, 'err'); return; }
      await load();
      paint();
    });
  }

  function wireDesk(box, on) {
    on('[data-ok]', (b) => act(b, 'зараховую…', '/api/shards/' + b.dataset.ok + '/ok'));
    on('[data-refuse]', (b) => {
      refusing = +b.dataset.refuse; note = '';
      paint();
      const n = box.querySelector('[data-note]');
      if (n) n.focus();
    });
    on('[data-unrefuse]', () => { refusing = 0; paint(); });
    const n = box.querySelector('[data-note]');
    if (n) n.oninput = () => { note = n.value; };
    on('[data-no]', (b) => act(b, 'позначаю…', '/api/shards/' + b.dataset.no + '/no', { note: note.trim() }, (r) => {
      if (r && r.ok) { refusing = 0; note = ''; }
    }));
    on('[data-spaid]', (b) => act(b, 'записую…', '/api/shards/sale/' + b.dataset.spaid + '/paid'));
    on('[data-srefuse]', (b) => act(b, 'позначаю…', '/api/shards/sale/' + b.dataset.srefuse + '/no', { note: note.trim() }, (r) => {
      if (r && r.ok) { saleNote = 0; note = ''; }
    }));
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

  /// «Далі — до оплати»: сервер спершу каже, чи можна (друг — акаунт, оплат не забагато), і лише тоді — реквізити:
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
    /// Після /api/me: кружечок у шапці одразу, а не з першим відкриттям вікна (каса, а гравцю — скинуті гроші).
    ready() { if (o && (o.me.account || o.me.role === 'admin')) load(); },
    attach(conn) {
      conn.on('shardOrders', (m) => { count = (m && m.count) || 0; sales = (m && m.sales) || 0; paintBadge(); if (wrap) soon(); });
      // Адмін скинув гроші чи не купив — кружечок і вікно свіжі без F5, навіть коли вікно закрите
      conn.on('shardSale', () => soon());
      // Зарахували (собі чи подарунок), відклали чи повернули — вікно й «У глечику» в Лавці свіжі без F5
      conn.on('wallet', (w) => {
        if (!w || !/^(buy|sell)/.test(String(w.reason || ''))) return;
        if (wrap) soon();
        if (window.HLavka && HLavka.refresh) HLavka.refresh();
      });
      // Відмова чи підтвердження подарунка приходять тостом — відкрите вікно теж перечитаємо
      conn.on('toast', () => { if (wrap) soon(); });
    },
    open,
    /// Продавцю купувати в себе нема чого: «Докупити» в Лавці йому не показуємо.
    isSeller: () => !!(data && data.isSeller),
    /// Підпис кнопки в профілі: продавцю й адміну — «Каса» з тим, скільки чекає; решті — «Купити».
    label() {
      if (data && (data.isSeller || data.admin)) { const n = deskCount(); return '🧾 Каса' + (n ? ' · ' + n : ''); }
      return '➕ Купити';
    },
    /// Куди веде перша кнопка профілю: продавцю й адміну — у касу, решті — купити.
    mainTab: () => (data && (data.isSeller || data.admin) ? 'desk' : 'buy'),
    /// Друга кнопка профілю: «Продати» — з тим, скільки грошей адмін уже скинув на перевірку.
    sellLabel() { const n = myPaid(); return '💸 Продати' + (n ? ' · ' + n : ''); },
  };
})();
