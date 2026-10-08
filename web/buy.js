/*
  Купити черепки — window.HBuy. Як гроші в Падельні (08.10.2026): обираєш пакет, бачиш картку й банку продавця (його банки
  з профілю Падельні), скидаєш гроші й тиснеш «✓ Скинув». Продавець бачить замовлення в «Чекають підтвердження» і тисне
  «✓ Отримав» — черепки падають одразу (тост гаманця шле сервер) — або «✕ Не прийшло» з приміткою. Можна купити другові.
  Черепки назад у гривні не міняються.

  Сервер — ShardShop.cs: GET /api/shards, POST /api/shards/check { uah, for } («Далі» — перевірка до реквізитів),
  /paid { uah, for }, /{id}/ok, /{id}/no { note }, /{id}/cancel;
  подія хаба shardOrders { count } — скільки чекає (кружечок на гаманці в шапці продавця й адміна).
*/
(() => {
  'use strict';

  let o = null;                        // що дає app.js
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  let data = null;                     // останнє GET /api/shards
  let count = 0;                       // скільки оплат чекає продавця
  let wrap = null;                     // відкрите вікно
  let pack = 0;                        // обраний пакет, грн
  let gift = false;                    // купуємо другові
  let to = '';                         // кому (нік друга)
  let step = 'pick';                   // pick — пакет і кому, pay — реквізити й «✓ Скинув»
  let refusing = 0;                    // замовлення, для якого відкрите «чому не прийшло»
  let note = '';
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

  /// Кружечок на 🏺 у шапці — продавцю й адміну: скільки оплат чекає «✓ Отримав».
  function paintBadge() {
    const w = document.getElementById('hdrWallet');
    if (!w) return;
    let b = w.querySelector('.by-badge');
    const n = data && data.canConfirm ? count : 0;
    if (!n) { if (b) b.remove(); return; }
    if (!b) { b = document.createElement('span'); b.className = 'chip badge by-badge'; w.appendChild(b); }
    b.textContent = n;
    b.title = 'Чекають підтвердження оплати: ' + n;
  }

  // ---------------------------------------------------------------- вікно

  async function open(opts) {
    const x = opts || {};
    if (!o) return;
    close();
    pack = 0; gift = !!x.for; to = x.for || ''; step = 'pick'; refusing = 0; note = '';
    wrap = document.createElement('div');
    wrap.className = 'modal by-modal';
    wrap.innerHTML = '<div class="card by-card" role="dialog" aria-modal="true" aria-label="Купити черепки"><div class="gwait"><span class="spin"></span> дивлюсь…</div></div>';
    wrap.addEventListener('click', (e) => { if (e.target === wrap) close(); });
    document.addEventListener('keydown', onKey, true);
    document.body.appendChild(wrap);
    await load();
    if (!wrap) return;
    // Прийшли з «бракує N»: одразу найменший пакет, якого вистачить
    if (data && data.packs && x.need) {
      const p = data.packs.find((q) => q.shards >= x.need) || data.packs[data.packs.length - 1];
      if (p) pack = p.uah;
    }
    paint();
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
    let h = '<h3><img src="/static/glek.svg" alt=""><span>' + (d && d.isSeller ? 'Продаж черепків' : 'Купити черепки') + '</span>'
      + '<button type="button" class="ghost by-x" data-close title="Закрити — Esc" aria-label="Закрити">✕</button></h3>';
    if (!d) h += '<div class="gempty">Сервер не відповів — спробуй трохи згодом.</div>';
    else if (!d.open) {
      h += '<div class="gempty glek">Купівля черепків ще не відкрита.'
        + (o.me.role === 'admin' ? '<br><span class="muted small">Задай продавця: ShardShop → Seller в appsettings.Local.json.</span>' : '') + '</div>';
    } else {
      if (!d.isSeller) h += buyerHtml(d);
      if (d.canConfirm) h += sellerHtml(d);
    }
    box.innerHTML = h;
    wire(box);
  }

  // ---------------------------------------------------------------- покупець

  function buyerHtml(d) {
    if (!d.account) {
      return '<div class="by-lock">🔒 Черепки купують лише акаунти: гостьовий нік може зайняти хтось інший, і куплене пропало б.</div>'
        + '<div class="row"><button class="primary" type="button" data-acc>Закріпити нік</button></div>';
    }
    const p = d.packs.find((x) => x.uah === pack);
    return (step === 'pay' && p ? payHtml(d, p) : pickHtml(d)) + mineHtml(d);
  }

  function pickHtml(d) {
    const people = online();
    return '<div class="muted small">1 грн = ' + num(d.rate) + ' 🏺. Скидаєш гроші ' + esc(dat(d.seller.nick))
      + ' на картку чи в банку — і щойно гроші прийдуть, черепки впадуть.</div>'
      + '<div class="by-packs">' + d.packs.map((x) => '<button type="button" class="by-pack' + (x.uah === pack ? ' on' : '') + '" data-pack="' + x.uah
        + '" aria-pressed="' + (x.uah === pack) + '"><b>' + num(x.shards) + ' 🏺</b><span>' + x.uah + ' грн</span></button>').join('') + '</div>'
      + '<div class="by-for"><span class="muted small">Кому</span><div class="by-seg">'
      + '<button type="button" data-to="me" class="' + (gift ? '' : 'on') + '" aria-pressed="' + !gift + '">Собі</button>'
      + '<button type="button" data-to="friend" class="' + (gift ? 'on' : '') + '" aria-pressed="' + gift + '">🎁 Другові</button></div>'
      + (gift
        ? '<input type="text" class="by-to" maxlength="40" autocomplete="off" placeholder="Нік друга — лише акаунт" aria-label="Нік друга" value="' + esc(to) + '">'
          + (people.length ? '<div class="by-chips">' + people.map((n) => '<button type="button" class="chip' + (same(n, to) ? ' on' : '') + '" data-who="'
            + esc(n) + '">' + esc(n) + '</button>').join('') + '</div>' : '')
        : '')
      + '</div>'
      + '<div class="row"><button type="button" class="primary" data-next' + (pack ? '' : ' disabled') + '>Далі — до оплати</button></div>'
      + '<div class="muted small">Черепки назад у гривні не міняються.</div>';
  }

  function payHtml(d, p) {
    const banks = d.seller.banks || [];
    const who = gift && to ? esc(to.trim()) + ' отримає' : 'отримаєш';
    return '<div class="by-sum">Скинь <b>' + p.uah + ' грн</b> ' + esc(dat(d.seller.nick)) + ' — ' + who + ' <b>' + num(p.shards) + ' 🏺</b></div>'
      + (banks.length ? '<div class="by-banks">' + banks.map((b) => bankRow(b, p.uah)).join('') + '</div>'
        : '<div class="by-lock">Картки чи банки продавця тут ще нема — спитай ' + esc(gen(d.seller.nick)) + ', куди скинути.</div>')
      + '<div class="muted small">У коментарі до переказу напиши свій нік — так ' + esc(d.seller.nick) + ' швидше знайде платіж.</div>'
      + '<div class="row"><button type="button" class="primary" data-paid>✓ Скинув</button><button type="button" class="ghost" data-back>← Інший пакет</button></div>'
      + '<div class="muted small">Тисни «Скинув», коли гроші вже пішли. Черепки впадуть, щойно ' + esc(d.seller.nick) + ' побачить переказ.</div>';
  }

  /// Банка mono сама підставить суму й коментар, якщо передати їх у посиланні (a — сума, t — коментар).
  function jarLink(link, uah) {
    try {
      const u = new URL(link);
      if (u.hostname !== 'send.monobank.ua') return link;
      u.searchParams.set('a', String(uah));
      u.searchParams.set('t', 'Глечики: ' + (o.me.nick || ''));
      return u.toString();
    } catch { return link; }
  }

  function bankRow(b, uah) {
    return '<div class="by-bank"><span class="by-bk by-' + esc(b.bank) + '">' + esc(BANKS[b.bank] || BANKS.other) + '</span>'
      + (b.title ? '<span class="by-btitle">' + esc(b.title) + '</span>' : '')
      + (b.card ? '<span class="by-cardno">' + esc(card(b.card)) + '</span><button type="button" class="by-copy" data-copy="' + esc(b.card) + '">📋 Скопіювати</button>' : '')
      + (b.link ? '<a class="by-jar" href="' + esc(jarLink(b.link, uah)) + '" target="_blank" rel="noopener noreferrer">🫙 Банка ↗</a>' : '')
      + '</div>';
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

  // ---------------------------------------------------------------- продавець

  function sellerHtml(d) {
    const w = d.waiting || [];
    const banks = (d.seller && d.seller.banks) || [];
    let h = d.isSeller ? '' : '<h4>Продаж — ти адмін</h4>';
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
    on('[data-acc]', () => { close(); o.askNick(true, 'register', String(o.me.nick || '').replace(/^гість\s*/i, '')); });
    on('[data-pack]', (b) => { pack = +b.dataset.pack; paint(); });
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
      if (r && r.ok) { step = 'pick'; pack = 0; gift = false; to = ''; }
    }));
    on('[data-copy]', (b) => copy(b.dataset.copy));
    on('[data-cancel]', (b) => act(b, 'скасовую…', '/api/shards/' + b.dataset.cancel + '/cancel'));
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
    /// Після /api/me: продавцю й адміну — кружечок «чекають» у шапці одразу, а не з першим відкриттям вікна.
    ready() { if (o && (o.me.account || o.me.role === 'admin')) load(); },
    attach(conn) {
      conn.on('shardOrders', (m) => { count = (m && m.count) || 0; paintBadge(); if (wrap) soon(); });
      // Зарахували (собі чи подарунок) — вікно й «У глечику» в Лавці свіжі без F5
      conn.on('wallet', (w) => {
        if (!w || !/^buy/.test(String(w.reason || ''))) return;
        if (wrap) soon();
        if (window.HLavka && HLavka.refresh) HLavka.refresh();
      });
      // Відмова чи підтвердження подарунка приходять тостом — відкрите вікно теж перечитаємо
      conn.on('toast', () => { if (wrap) soon(); });
    },
    open,
    /// Продавцю купувати в себе нема чого: «Докупити» в Лавці йому не показуємо.
    isSeller: () => !!(data && data.isSeller),
    /// Підпис кнопки в профілі: продавцю — «Продаж» з тим, скільки чекає.
    label() { return data && data.isSeller ? '🛒 Продаж' + (count ? ' · ' + count : '') : '➕ Купити'; },
  };
})();
