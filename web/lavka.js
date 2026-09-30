/*
  Лавка Дядька Глека — window.HLavka. Кастомізація за черепки: значок і рамка аватарки, колір ніка, титул, тло профілю
  і вміння (присвята в ефір, феєрверк). Рішення власника (26.09.2026): усе куплене — НАЗАВЖДИ (жодної оренди й жодних
  витрат «за раз» — вміння теж вічні, лише з перервою); купують і вдягають лише акаунти; дарувати можна.

  Сервер (Lavka.cs): GET /api/lavka (вітрина + моє), GET /api/lavka/looks (хто як виглядає — публічно),
  POST /api/lavka/buy { item, for? }, /wear { slot, item }, /dedicate { to, phrase }; хаб Fireworks();
  події look { nick, look } і fireworks { nick }.

  Хто як виглядає, знає цей модуль: web/people.js питає look(nick) для кольору, значка й рамки — тому куплене видно
  скрізь, де є нік: балачки, черга, столи, таблиці, картка, профіль.
*/
(() => {
  'use strict';

  let o = null;                        // що дає app.js
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const key = (n) => String(n || '').toLowerCase().trim();

  let data = null;                     // останнє GET /api/lavka
  const looks = new Map();             // нік (нижній регістр) → { icon, frame, color, title, bg }
  let shown = false;
  let tab = 'icon';
  let giftTo = null;                   // режим подарунка: кому
  const preview = {};                  // слот → id речі, яку приміряємо (лише на екрані, нічого не купує)

  const TABS = [['icon', '🏺 Значки'], ['frame', '⭕ Рамки'], ['color', '🎨 Колір ніка'], ['title', '🏷 Титули'],
    ['bg', '🖼 Тло'], ['perk', '✨ Вміння'], ['photo', '📷 Своя фотка'], ['roast', '🔥 Прожарка'], ['mine', '👜 Моя шафа']];
  /// Полиці, яких у подарунку нема: шафа — своя, а прожарку не дарують (її замовляють самі, web/liveads.js).
  const NO_GIFT = ['mine', 'roast'];
  const TIER = { 1: 'звичайний', 2: 'рідкісний', 3: 'особливий' };
  const PERK_TEXT = {
    dedication: 'Перед твоїм треком Дядько Глек скаже в ефір: «Цю пісню Оля присвячує Петрові — на удачу». Раз на 3 години.',
    fireworks: 'Кнопка 🎆 біля реакцій: бахнути феєрверк над обкладинкою в усіх — і рядок у балачках. Раз на 10 хвилин.',
    photo: 'Своє фото замість літери чи значка — у профілі, балачках, списках і за столами. Рамка лишається поверх, значок сідає в куточок. Міняти — безкоштовно, раз на добу.',
  };

  // =============================================================================================
  // Відмінки ніка — той самий порядок, що й NickCases.cs на сервері: «дарує Петрові», «для Олі», «гостю Васі».
  // Тут — лише для підписів у вітрині й віконці присвяти, щоб вони казали те саме, що скаже Глек.
  // =============================================================================================

  const VOW = 'аеєиіїоуюяыэё';
  const SIB = 'жчшщ';
  const SPECIAL = { 'ігор': ['ігореві', 'ігоря'], 'федір': ['федорові', 'федора'], 'сидір': ['сидорові', 'сидора'],
    'кіт': ['котові', 'кота'], 'любов': ['любові', 'любові'], 'гість': ['гостю', 'гостя'] };
  const isCyr = (ch) => /[\u0400-\u04FF]/.test(ch) && /\p{L}/u.test(ch);
  const loud = (w) => w.length > 1 && w === w.toUpperCase() && w !== w.toLowerCase();
  const like = (w, f) => (loud(w) ? f.toUpperCase() : w[0] !== w[0].toLowerCase() ? f[0].toUpperCase() + f.slice(1) : f);
  function word(wd, dat) {
    if (wd.length < 2 || ![...wd].every((ch) => isCyr(ch) || "'’ʼ".includes(ch))) return wd;
    const w = wd.toLowerCase();
    if (SPECIAL[w]) return like(wd, SPECIAL[w][dat ? 0 : 1]);
    const last = w[w.length - 1];
    const prev = w[w.length - 2];
    const cut = (n) => wd.slice(0, -n);
    let r;
    if (last === 'а' && w.length >= 3 && !VOW.includes(prev)) {
      r = SIB.includes(prev) ? cut(1) + 'і' : !dat ? cut(1) + 'и'
        : prev === 'г' ? cut(2) + 'зі' : prev === 'к' ? cut(2) + 'ці' : prev === 'х' ? cut(2) + 'сі' : cut(1) + 'і';
    } else if (last === 'я' && w.length >= 3) r = cut(1) + (VOW.includes(prev) ? 'ї' : 'і');
    else if (last === 'о' && w.length >= 3 && !VOW.includes(prev)) r = cut(1) + (dat ? 'ові' : 'а');
    else if (last === 'й' && w.length >= 3) r = prev === 'и' ? cut(2) + (dat ? 'ому' : 'ого') : cut(1) + (dat ? 'єві' : 'я');
    else if (last === 'ь' && w.length >= 3) r = cut(1) + (dat ? 'еві' : 'я');
    else {
      if (VOW.includes(last) || !/\p{L}/u.test(last) || 'аяойь'.includes(last)) return wd;
      r = wd + (dat ? (SIB.includes(last) ? 'еві' : 'ові') : 'а');
    }
    return loud(wd) ? r.toUpperCase() : r;
  }
  /// Відмінюється перше слово («миколі ( справжній )»), а в гостя — обидва: «гостю Васі».
  function decline(nick, dat) {
    const s = String(nick || '').trim();
    const i = s.indexOf(' ');
    if (!s || i < 0) return s ? word(s, dat) : s;
    const first = s.slice(0, i);
    const rest = s.slice(i + 1);
    return word(first, dat) + ' ' + (first.toLowerCase() === 'гість' ? decline(rest, dat) : rest);
  }
  const dative = (n) => decline(n, true);
  const genitive = (n) => decline(n, false);

  // =============================================================================================
  // Хто як виглядає
  // =============================================================================================

  const look = (nick) => looks.get(key(nick)) || null;

  async function loadLooks() {
    try {
      const r = await fetch('/api/lavka/looks').then((x) => (x.ok ? x.json() : null));
      looks.clear();
      for (const [n, l] of Object.entries((r && r.looks) || {})) if (l) looks.set(key(n), l);
      repaintNicks();
    } catch { /* старий сервер — усі в своїх кольорах, і це не біда */ }
  }

  /// Хтось щось вдягнув: перефарбувати вже намальовані ніки й аватарки цієї людини, не чекаючи нових рядків.
  function onLook(ev) {
    if (!ev || !ev.nick) return;
    if (ev.look) looks.set(key(ev.nick), ev.look); else looks.delete(key(ev.nick));
    repaintNicks(ev.nick);
    if (o && o.me && same(ev.nick, o.me.nick) && o.onMine) o.onMine();
  }
  function repaintNicks(only) {
    const P = window.HPeople;
    if (!P) return;
    const mine = (n) => !only || same(n, only);
    document.querySelectorAll('[data-who]').forEach((el) => {
      const n = el.dataset.who;
      if (!mine(n)) return;
      el.style.setProperty('--h', P.hue(n));
      el.classList.toggle('rainbow', P.nickCls(n) !== '');
      // значок перед ніком — лише там, де його просили (data-nb: балачки)
      if (el.dataset.nb) {
        const old = el.querySelector(':scope > .nico');
        if (old) old.remove();
        el.insertAdjacentHTML('afterbegin', P.badge(n));
      }
    });
    // Аватарку малюємо наново тим самим розміром: значок, рамка й колір — усе з вбрання.
    document.querySelectorAll('[data-ava]').forEach((el) => {
      const n = el.dataset.ava;
      if (!mine(n)) return;
      const base = [...el.classList].filter((c) => !/^(ico|ph|fr|fr-.+|rainbow)$/.test(c)).join(' ');
      el.outerHTML = P.ava(n, base, el.id || undefined);
    });
    // Решту (хто онлайн) app.js перемальовує сам — там ніки з приставками, і простіше намалювати наново.
    if (o && o.onLooks) o.onLooks();
  }
  /// Моє вбрання знаю й сам — з /api/lavka: не чекаємо події look, якої без зв'язку з хабом може й не бути.
  function syncMine() {
    const me = o && o.me && o.me.nick;
    if (!me || !data || !data.account) return;
    const l = lookWith();
    if (Object.values(l).some((v) => v != null)) looks.set(key(me), l); else looks.delete(key(me));
    repaintNicks(me);
  }

  // =============================================================================================
  // Малювання речей
  // =============================================================================================

  const item = (id) => ((data && data.items) || []).find((x) => x.id === id) || null;
  const emo = (icon) => (window.HPeople && window.HPeople.emo ? window.HPeople.emo(icon) : esc(icon));
  /// Як виглядала б людина, якби вдягла ще й те, що зараз приміряє. Своя фотка не приміряється — вона або стоїть, або ні.
  function lookWith(extra) {
    const w = Object.assign({}, (data && data.worn) || {}, extra || {});
    const art = (slot) => { const it = item(w[slot]); return it ? it.art : null; };
    const ph = data && data.perks && data.perks.photo;
    return { icon: art('icon'), frame: art('frame'), color: art('color'), title: art('title'), bg: art('bg'), photo: (ph && ph.url) || null };
  }
  function avaOf(nick, l, cls) {
    // той самий кружечок, що й скрізь на сайті (people.js), лише без data-ava: це вітрина, а не людина
    if (window.HPeople && window.HPeople.avaHtml) return window.HPeople.avaHtml(nick, l, cls);
    const n = String(nick || '').replace(/^гість\s+/i, '').trim();
    const letter = n ? [...n][0].toUpperCase() : '?';
    const hue = l && typeof l.color === 'number' ? l.color : window.HPeople ? window.HPeople.hueRaw(nick) : 200;
    return '<span class="' + (cls || 'ava') + (l && l.frame ? ' fr fr-' + esc(l.frame) : '') + (l && l.icon ? ' ico' : '')
      + (l && l.color === 'rainbow' ? ' rainbow' : '') + '" style="--h:' + hue + '" aria-hidden="true">' + (l && l.icon ? emo(l.icon) : esc(letter)) + '</span>';
  }
  function nickOf(nick, l) {
    const hue = l && typeof l.color === 'number' ? l.color : window.HPeople ? window.HPeople.hueRaw(nick) : 200;
    return '<b class="lv-nick' + (l && l.color === 'rainbow' ? ' rainbow' : '') + '" style="--h:' + hue + '">' + esc(nick) + '</b>';
  }
  /// Маленька вітрина речі: як вона виглядатиме саме на тобі.
  function artOf(it) {
    const me = (o && o.me && o.me.nick) || 'Ти';
    switch (it.kind) {
      case 'icon': return '<span class="lv-emoji">' + emo(it.art) + '</span>';
      case 'frame': return avaOf(me, Object.assign(lookWith(), { frame: it.art }), 'ava xxl');
      case 'color': return nickOf(me, { color: it.art });
      case 'title': return '<span class="lv-titlechip">' + esc(it.art) + '</span>';
      case 'bg': return '<span class="lv-bg bg-' + esc(it.art) + '"></span>';
      case 'perk': return '<span class="lv-emoji">' + (it.id === 'fireworks' ? '🎆' : it.id === 'photo' ? '📷' : '💌') + '</span>';
    }
    return '';
  }
  const season = (s) => (s ? (s.open ? '🗓 лише до ' + s.to.split('-').reverse().join('.') : '🗓 повернеться ' + s.from.split('-').reverse().join('.')) : '');
  const readyIn = (iso) => {
    const ms = Date.parse(iso) - Date.now();
    if (!(ms > 0)) return '';
    const m = Math.ceil(ms / 60000);
    return m < 60 ? 'ще ' + m + ' хв' : 'ще ' + Math.floor(m / 60) + ' год' + (m % 60 ? ' ' + (m % 60) + ' хв' : '');
  };

  function cardHtml(it) {
    const acc = data.account;
    const gift = !!giftTo;
    let state = '', btns = '';
    const perk = it.kind === 'perk' ? (data.perks || {})[it.id] : null;
    if (it.earned) {
      state = it.owned ? (it.worn && !gift ? '✓ вдягнуто' : '✓ здобуто') : '🔒 за ачівку «' + esc(it.earned.achTitle || it.earned.ach) + '»';
      if (it.owned && !gift) btns = it.worn ? '<button class="ghost" data-off="' + it.kind + '">Зняти</button>' : '<button class="primary" data-wear="' + it.id + '">Вдягти</button>';
    } else if (gift) {
      state = it.price + ' 🏺' + (it.season ? ' · ' + season(it.season) : '');
      const short = it.price - (data.balance || 0);
      btns = it.season && !it.season.open ? '<button disabled>Не сезон</button>'
        : short > 0 ? '<button disabled>Бракує ' + short + ' 🏺</button>'
          : '<button class="primary" data-gift="' + it.id + '">🎁 Подарувати</button>';
    } else if (it.owned && it.id === 'photo') {
      // фотка — не «готове / ще N хв», а своя полиця: там її ставлять, міняють і прибирають
      state = '✓ твоє' + (perk && perk.url ? ' · фото стоїть' : '');
      btns = '<button class="primary" data-go="#lavka/photo">📷 ' + (perk && perk.url ? 'Фото' : 'Поставити фото') + '</button>';
    } else if (it.owned) {
      if (it.kind === 'perk') state = '✓ твоє' + (perk && perk.readyAt && readyIn(perk.readyAt) ? ' · ' + readyIn(perk.readyAt) : ' · готове');
      else {
        state = it.worn ? '✓ вдягнуто' : '✓ твоє';
        btns = it.worn ? '<button class="ghost" data-off="' + it.kind + '">Зняти</button>' : '<button class="primary" data-wear="' + it.id + '">Вдягти</button>';
      }
    } else {
      state = it.price + ' 🏺' + (it.season ? ' · ' + season(it.season) : '');
      const short = it.price - (data.balance || 0);
      btns = !acc ? '<button disabled title="Лише для акаунтів">Купити</button>'
        : it.season && !it.season.open ? '<button disabled>Не сезон</button>'
          : short > 0 ? '<button disabled>Бракує ' + short + ' 🏺</button>'
            : '<button class="primary" data-buy="' + it.id + '">Купити</button>';
    }
    const tryBtn = it.kind !== 'perk' && !gift && !it.worn ? '<button class="ghost" data-try="' + it.id + '" title="Подивитись на собі — нічого не купує">Приміряти</button>' : '';
    // У подарунку «моє / вдягнуто» ні до чого: річ вибирають для іншої людини.
    const mine = !gift;
    return '<div class="lv-item' + (mine && it.owned ? ' owned' : '') + (mine && it.worn ? ' worn' : '') + (it.earned && !it.owned ? ' locked' : '')
      + (preview[it.kind] === it.id ? ' trying' : '') + '" data-id="' + esc(it.id) + '">'
      + '<div class="lv-art">' + artOf(it) + '</div>'
      + '<div class="lv-name">' + esc(it.title) + (it.tier ? ' <span class="muted small">· ' + TIER[it.tier] + '</span>' : '') + '</div>'
      + (it.kind === 'perk' ? '<div class="muted small lv-desc">' + esc(PERK_TEXT[it.id] || '') + '</div>' : '')
      + '<div class="lv-state">' + state + '</div>'
      + '<div class="lv-btns">' + tryBtn + btns + '</div></div>';
  }

  function previewHtml() {
    const me = (o && o.me && o.me.nick) || 'Ти';
    const l = lookWith(preview);
    const trying = Object.keys(preview).length;
    return '<div class="lv-preview' + (l.bg ? ' bg-' + esc(l.bg) : '') + '">'
      + avaOf(me, l, 'ava xxl')
      + '<div class="lv-pv-who">' + nickOf(me, l) + (l.title ? '<span class="lv-titlechip">' + esc(l.title) + '</span>' : '')
      + '<span class="muted small">' + (trying ? 'Приміряєш — нічого ще не куплено' : 'Так тебе бачать інші') + '</span></div>'
      + (trying ? '<button class="ghost lv-pv-reset" type="button">✕ зняти приміряне</button>' : '')
      + '</div>';
  }

  // =============================================================================================
  // Сторінка
  // =============================================================================================

  async function load() {
    data = await o.api('GET', '/api/lavka');
    syncMine();
    return data;
  }

  async function render(tail) {
    const root = document.getElementById('lavka');
    if (!root) return;
    const parts = String(tail || '').split('/').map(decodeURIComponent);
    const wasGift = giftTo;
    giftTo = parts[0] === 'gift' && parts[1] ? parts[1] : null;
    // Подарунок починаємо зі значків — найдешевшого й найзрозумілішого, а не з полиці, де був сам.
    if (giftTo && !same(giftTo, wasGift)) tab = 'icon';
    if (!giftTo && TABS.some(([k]) => k === parts[0])) tab = parts[0];
    if (giftTo && NO_GIFT.includes(tab)) tab = 'icon';
    // Полиці перемикаємо одразу з того, що вже знаємо, а свіже (баланс, шафа) домальовуємо, щойно прийде.
    if (data) paint(); else root.innerHTML = '<div class="gwait"><span class="spin"></span> відчиняю лавку…</div>';
    try { await load(); } catch (e) { if (!data) root.innerHTML = '<section class="panel"><div class="gempty">Лавка зачинена: ' + esc(e.message) + '</div></section>'; return; }
    if (!shown) return;
    paint();
  }

  function paint() {
    const root = document.getElementById('lavka');
    if (!root || !data) return;
    const acc = data.account;
    const items = data.items || [];
    // Титули за ачівки не дарують і не купують — у подарунковому режимі їх не показуємо зовсім.
    const list = tab === 'mine' ? items.filter((x) => x.owned) : items.filter((x) => x.kind === tab && !(giftTo && x.earned));
    // Сезонне, що продається саме зараз (🎃 восени, 🎄 на свята), — першим: воно ненадовго, і в кінці полиці його не видно.
    const now = (x) => (x.season && x.season.open ? 0 : 1);
    if (tab !== 'mine') list.sort((a, b) => now(a) - now(b));
    // Сезонних на весь рік два десятки: поза сезоном (і ще не своє) воно не займає полицю кнопками «Не сезон» —
    // лише рядок «ще будуть» під полицею, найближче першим.
    const later = tab !== 'mine' ? list.filter((x) => x.season && !x.season.open && !x.owned) : [];
    const shelf = later.length ? list.filter((x) => !later.includes(x)) : list;
    const d = new Date();
    const today = String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
    const nextKey = (x) => (x.season.from > today ? '0' : '1') + x.season.from;
    later.sort((a, b) => (nextKey(a) < nextKey(b) ? -1 : 1));
    const soon = later.length
      ? '<div class="lv-soon"><div class="muted small">🗓 Ще будуть у Лавці</div><div class="lv-soon-list">'
        + later.map((x) => '<span class="lv-soon-it" title="' + esc(x.title) + ' · ' + x.price + ' 🏺"><span class="lv-soon-art">'
          + (x.kind === 'icon' ? emo(x.art) : '🗓') + '</span><span>' + esc(x.title) + '</span><span class="muted small">з '
          + x.season.from.split('-').reverse().join('.') + '</span></span>').join('')
        + '</div></div>'
      : '';
    const head = '<section class="panel lv-head">'
      + '<div class="lv-top"><div class="lv-sign"><img src="/static/glek.svg" alt=""><div><h2>Лавка Дядька Глека</h2>'
      + '<div class="muted small">Усе, що купиш, — твоє назавжди. Черепки капають за радіо, партії й щоденний глек.</div></div></div>'
      + '<div class="lv-bal">У глечику <b>' + (data.balance != null ? data.balance : '—') + ' 🏺</b></div></div>'
      + (giftTo ? '<div class="lv-gift"><span>🎁 Подарунок для <b>' + esc(genitive(giftTo)) + '</b> — обери річ. Подарувати можна лише те, чого в людини ще нема.</span>'
        + '<button class="ghost" data-go="#lavka">✕ скасувати</button></div>' : '')
      + (!acc ? '<div class="lv-guest"><span>🔒 Лавка — для акаунтів: гостьовий нік може зайняти хтось інший, і куплене пропало б.</span>'
        + '<button class="primary" data-acc>Закріпити нік</button></div>' : '')
      + (!giftTo ? previewHtml() : '')
      + '</section>';
    const tabs = '<nav class="lv-tabs" aria-label="Полиці лавки">' + TABS.filter(([k]) => !(giftTo && NO_GIFT.includes(k))).map(([k, l]) =>
      '<button type="button" data-tab="' + k + '"' + (k === tab ? ' class="on"' : '') + '>' + l + '</button>').join('') + '</nav>';
    const body = tab === 'photo' ? photoPanel()
      : tab === 'roast' ? '<div class="la-host" data-la-host></div>'
      : shelf.length
      ? '<div class="lv-grid">' + shelf.map(cardHtml).join('') + '</div>' + soon
      : '<div class="gempty glek">' + (tab === 'mine' ? 'Шафа ще порожня. Обери щось на полицях — і воно лишиться з тобою назавжди.' : 'Тут поки порожньо.') + '</div>';
    root.innerHTML = head + '<section class="panel lv-shelf">' + tabs + body + '</section>';
    wire(root);
    // 🔥 Прожарка в ефірі — свій модуль (web/liveads.js): малює себе сам і сам себе перечитує.
    const la = root.querySelector('[data-la-host]');
    if (la && window.HLiveAds) window.HLiveAds.mount(la);
  }

  function wire(root) {
    root.querySelectorAll('[data-tab]').forEach((b) => b.onclick = () => {
      // У подарунку адреса одна (#lavka/gift/<нік>) — полиці гортаємо на місці.
      if (giftTo) { tab = b.dataset.tab; paint(); } else o.go('#lavka/' + b.dataset.tab);
    });
    root.querySelectorAll('[data-go]').forEach((b) => b.onclick = () => o.go(b.dataset.go));
    const acc = root.querySelector('[data-acc]');
    if (acc) acc.onclick = () => o.askNick(true, 'register', String(o.me.nick || '').replace(/^гість\s*/i, ''));
    root.querySelectorAll('[data-try]').forEach((b) => b.onclick = () => {
      const it = item(b.dataset.try);
      if (!it) return;
      if (preview[it.kind] === it.id) delete preview[it.kind]; else preview[it.kind] = it.id;
      paint();
    });
    const reset = root.querySelector('.lv-pv-reset');
    if (reset) reset.onclick = () => { for (const k of Object.keys(preview)) delete preview[k]; paint(); };
    root.querySelectorAll('[data-buy]').forEach((b) => b.onclick = (e) => buy(b.dataset.buy, null, e.currentTarget));
    root.querySelectorAll('[data-gift]').forEach((b) => b.onclick = (e) => buy(b.dataset.gift, giftTo, e.currentTarget));
    root.querySelectorAll('[data-wear]').forEach((b) => b.onclick = (e) => wear(item(b.dataset.wear).kind, b.dataset.wear, e.currentTarget));
    root.querySelectorAll('[data-off]').forEach((b) => b.onclick = (e) => wear(b.dataset.off, null, e.currentTarget));
    // Файл вибираємо просто в обробнику кліку: Safari відкриває вибір фото лише з живого натиску.
    root.querySelectorAll('[data-ph-pick]').forEach((b) => b.onclick = () => pickPhoto());
    root.querySelectorAll('[data-ph-off]').forEach((b) => b.onclick = (e) => removePhoto(e.currentTarget));
  }

  /// «Точно?» своїм віконцем: покупка назавжди, і черепки назад не повертаються.
  function ask(title, html, okText) {
    return new Promise((done) => {
      const wrap = document.createElement('div');
      wrap.className = 'modal lv-ask';
      wrap.innerHTML = '<div class="card"><h3>' + esc(title) + '</h3><div class="muted">' + html + '</div>'
        + '<div class="row"><button class="primary" type="button" data-yes>' + esc(okText) + '</button><button class="ghost" type="button" data-no>Не треба</button></div></div>';
      const close = (v) => { wrap.remove(); document.removeEventListener('keydown', onKey, true); done(v); };
      const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(false); } };
      wrap.addEventListener('click', (e) => { if (e.target === wrap) close(false); });
      wrap.querySelector('[data-yes]').onclick = () => close(true);
      wrap.querySelector('[data-no]').onclick = () => close(false);
      document.body.appendChild(wrap);
      document.addEventListener('keydown', onKey, true);
      wrap.querySelector('[data-yes]').focus();
    });
  }

  async function buy(id, forNick, btn) {
    const it = item(id);
    if (!it) return;
    const yes = await ask(forNick ? '🎁 Подарунок' : 'Купити?',
      forNick
        ? 'Подарувати <b>' + esc(dative(forNick)) + '</b> «' + esc(it.title) + '» за <b>' + it.price + ' 🏺</b>? Подарунок лишиться в людини назавжди'
          + (it.kind === 'perk' ? '.' : ', а якщо це місце в неї порожнє — одразу вдягнеться.')
        : '<b>' + esc(it.title) + '</b> за <b>' + it.price + ' 🏺</b>. Річ лишиться твоєю назавжди' + (it.kind === 'perk' ? '.' : ' — і одразу вдягнеться.'),
      forNick ? 'Подарувати' : 'Купити');
    if (!yes) return;
    await o.busy(btn, forNick ? 'дарую…' : 'купую…', async () => {
      try {
        const r = await o.api('POST', '/api/lavka/buy', forNick ? { item: id, for: forNick } : { item: id });
        o.toast(r.message || (forNick ? 'Є! Подаровано' : 'Лови — твоє!'), 'ok');
        delete preview[it.kind];
        await load();
        paint();
        if (o.onMine) o.onMine();
      } catch (e) { o.toast(e.message, 'err'); }
    });
  }

  async function wear(slot, id, btn) {
    await o.busy(btn, '…', async () => {
      try {
        await o.api('POST', '/api/lavka/wear', { slot, item: id });
        delete preview[slot];
        await load();
        paint();
        if (o.onMine) o.onMine();
      } catch (e) { o.toast(e.message, 'err'); }
    });
  }

  // =============================================================================================
  // 📷 Своя фотка (записка Назара, 28.09.2026). Купується раз назавжди; фото обрізає й стискає браузер — кружечок
  // 256×256, WebP (або JPEG там, де canvas WebP не вміє) на 20–80 КБ, — а сервер лише перевіряє й кладе файлом.
  // Міняти — раз на добу, прибрати — будь-коли. Рамка з Лавки лягає поверх фото, значок сідає в куточок.
  // =============================================================================================

  const PH_OUT = 256;                  // сторона картинки, яку шлемо на сервер
  const PH_MAX = 190 * 1024;           // сервер бере до 200 КБ — лишаємо запас
  const PH_ZOOM = 4;                   // найбільше збільшення в обрізці

  function photoPanel() {
    const it = item('photo');
    if (!it) return '<div class="gempty">Фотки в цій Лавці ще нема — онови сторінку.</div>';   // сервер старіший за сторінку
    const p = (data.perks && data.perks.photo) || {};
    const me = (o && o.me && o.me.nick) || 'Ти';
    const short = it.price - (data.balance || 0);
    let state, btns;
    if (giftTo) {
      state = it.price + ' 🏺 · назавжди — фото людина обере сама';
      btns = short > 0 ? '<button disabled>Бракує ' + short + ' 🏺</button>' : '<button class="primary" data-gift="photo">🎁 Подарувати</button>';
    } else if (!p.owned) {
      state = it.price + ' 🏺 · назавжди';
      btns = !data.account ? '<button disabled title="Лише для акаунтів">Купити</button>'
        : short > 0 ? '<button disabled>Бракує ' + short + ' 🏺</button>'
          : '<button class="primary" data-buy="photo">Купити</button>';
    } else {
      const wait = p.readyAt && Date.parse(p.readyAt) > Date.now() ? readyIn(p.readyAt) : '';
      state = p.url ? '✓ фото стоїть' : '✓ твоє — фото ще не обрано';
      if (wait) state += ' · нове — ' + wait;
      btns = '<button class="primary" type="button" data-ph-pick' + (wait ? ' disabled title="Нове фото — раз на добу"' : '') + '>📷 ' + (p.url ? 'Змінити фото' : 'Обрати фото') + '</button>'
        + (p.url ? '<button class="ghost" type="button" data-ph-off>Прибрати фото</button>' : '');
    }
    return '<div class="lv-photo">'
      + '<div class="lv-ph-art">' + avaOf(giftTo || me, giftTo ? null : lookWith(preview), 'ava xxl') + '</div>'
      + '<div class="lv-ph-main"><div class="lv-name">📷 Своя фотка</div>'
      + '<div class="muted small lv-desc">' + esc(PERK_TEXT.photo) + '</div>'
      + '<div class="lv-state">' + state + '</div>'
      + '<div class="lv-btns">' + btns + '</div>'
      + '<div class="muted small">👀 Фото бачать усі на сайті.</div></div></div>';
  }

  let phInput = null;
  /// Вибір файла. На телефоні accept="image/*" дає галерею чи камеру, а Safari сам перетворює HEIC на JPEG.
  function pickPhoto() {
    if (!phInput) {
      phInput = document.createElement('input');
      phInput.type = 'file';
      phInput.accept = 'image/*';
      phInput.hidden = true;
      phInput.onchange = async () => {
        const f = phInput.files && phInput.files[0];
        phInput.value = '';                        // те саме фото вдруге теж має спрацювати
        if (!f) return;
        let src;
        try { src = await loadImage(f); } catch (e) { o.toast(e.message, 'err'); return; }
        cropper(src);
      };
      document.body.appendChild(phInput);
    }
    phInput.click();
  }

  /// Файл як картинка. Сучасні браузери малюють <img> уже поверненим за EXIF (image-orientation: from-image — типово з
  /// 2020-го; naturalWidth/Height теж повернені), тож знімок з телефона в обрізці не ляже боком.
  function loadImage(file) {
    return new Promise((done, fail) => {
      const url = URL.createObjectURL(file);
      const img = new Image();
      img.onload = () => {
        if (!img.naturalWidth || !img.naturalHeight) { URL.revokeObjectURL(url); fail(new Error('Порожня картинка — обери інше фото')); return; }
        done({ img, w: img.naturalWidth, h: img.naturalHeight, url });
      };
      img.onerror = () => { URL.revokeObjectURL(url); fail(new Error('Цей файл браузер не відкриває як фото — спробуй JPEG чи PNG')); };
      img.src = url;
    });
  }

  /// Шматок src (квадрат sx, sy, side у пікселях фото) → canvas out×out. Зменшуємо вдвічі за крок: одним махом із
  /// 4000 px до 256 браузер (особливо Safari) дає «пісок» замість обличчя.
  function shrink(src, sx, sy, side, out) {
    let from = src.img, fx = sx, fy = sy, fs = side;
    while (fs / 2 >= out * 1.4) {
      const n = Math.round(fs / 2);
      const t = document.createElement('canvas');
      t.width = t.height = n;
      const g = t.getContext('2d');
      g.imageSmoothingQuality = 'high';
      g.drawImage(from, fx, fy, fs, fs, 0, 0, n, n);
      from = t; fx = 0; fy = 0; fs = n;
    }
    const c = document.createElement('canvas');
    c.width = c.height = out;
    const g = c.getContext('2d');
    g.fillStyle = '#1b211d';                   // JPEG прозорості не знає: прозоре тло PNG стане темним, а не чорним
    g.fillRect(0, 0, out, out);
    g.imageSmoothingQuality = 'high';
    g.drawImage(from, fx, fy, fs, fs, 0, 0, out, out);
    return c;
  }

  const toBlob = (c, type, q) => new Promise((done) => c.toBlob(done, type, q));
  /// WebP — якщо canvas справді дав WebP (старий Safari мовчки віддає PNG), інакше JPEG; якість — поки не влізе.
  async function encode(c) {
    const probe = await toBlob(c, 'image/webp', 0.85);
    const webp = !!probe && probe.type === 'image/webp';
    for (const q of [0.85, 0.75, 0.6, 0.45]) {
      const b = webp && q === 0.85 ? probe : await toBlob(c, webp ? 'image/webp' : 'image/jpeg', q);
      if (b && b.size <= PH_MAX) return b;
    }
    return null;
  }

  /// Кругле вікно обрізки: тягнути (миша, палець), збільшувати (коліщатко, щипок, повзунок, клавіші), «Зберегти».
  function cropper(src) {
    const wrap = document.createElement('div');
    wrap.className = 'modal lv-crop';
    wrap.innerHTML = '<div class="card" role="dialog" aria-modal="true" aria-label="Своя фотка: обрізати"><h3>📷 Своя фотка</h3>'
      + '<div class="lv-crop-stage" tabindex="0" aria-label="Фото. Стрілки — посунути, плюс і мінус — збільшити"><canvas></canvas><i class="lv-crop-ring"></i></div>'
      + '<label class="lv-crop-zoom"><span aria-hidden="true">🔍−</span><input type="range" min="1" max="' + PH_ZOOM + '" step="0.01" value="1" aria-label="Збільшення"><span aria-hidden="true">+</span></label>'
      + '<div class="muted small">Посунь фото мишкою чи пальцем, збільш коліщатком, щипком або повзунком. <b>Фото бачать усі на сайті.</b></div>'
      + '<div class="row"><button class="primary" type="button" data-yes>Зберегти</button><button class="ghost" type="button" data-other>Інше фото</button>'
      + '<button class="ghost" type="button" data-no>Скасувати</button></div></div>';
    document.body.appendChild(wrap);
    const stage = wrap.querySelector('.lv-crop-stage');
    const cv = stage.querySelector('canvas');
    const zoom = wrap.querySelector('input[type=range]');
    const S = stage.clientWidth || 280;        // сторона вікна в CSS-пікселях
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    cv.width = cv.height = Math.round(S * dpr);
    const ctx = cv.getContext('2d');
    ctx.imageSmoothingQuality = 'high';
    const base = S / Math.min(src.w, src.h);  // коротша сторона фото якраз заповнює вікно
    let z = 1;
    let ox = (S - src.w * base) / 2, oy = (S - src.h * base) / 2;   // де лівий верхній кут фото відносно вікна
    const k = () => base * z;
    const clamp = () => {
      ox = Math.min(0, Math.max(S - src.w * k(), ox));
      oy = Math.min(0, Math.max(S - src.h * k(), oy));
    };
    let raf = 0;
    const draw = () => {
      raf = 0;
      ctx.fillStyle = '#111';
      ctx.fillRect(0, 0, cv.width, cv.height);
      ctx.drawImage(src.img, ox * dpr, oy * dpr, src.w * k() * dpr, src.h * k() * dpr);
    };
    const redraw = () => { if (!raf) raf = requestAnimationFrame(draw); };
    /// Збільшити до nz так, щоб точка (fx, fy) вікна лишилась під пальцем.
    const zoomTo = (nz, fx, fy) => {
      nz = Math.min(PH_ZOOM, Math.max(1, nz));
      const r = nz / z;
      ox = fx - (fx - ox) * r;
      oy = fy - (fy - oy) * r;
      z = nz;
      clamp();
      zoom.value = String(z);
      redraw();
    };
    clamp();
    draw();

    const pts = new Map();
    let pinch = null;
    const pinchNow = () => {
      const [a, b] = [...pts.values()];
      return { d: Math.hypot(a.x - b.x, a.y - b.y) || 1, x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
    };
    stage.addEventListener('pointerdown', (e) => {
      if (pts.size >= 2) return;
      try { stage.setPointerCapture(e.pointerId); } catch { /* палець уже відпустили — тягнемо й без захоплення */ }
      pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
      pinch = pts.size === 2 ? pinchNow() : null;
      e.preventDefault();
    });
    stage.addEventListener('pointermove', (e) => {
      const p = pts.get(e.pointerId);
      if (!p) return;
      const dx = e.clientX - p.x, dy = e.clientY - p.y;
      p.x = e.clientX; p.y = e.clientY;
      if (pts.size === 1) { ox += dx; oy += dy; clamp(); redraw(); return; }
      if (!pinch) return;
      const now = pinchNow();
      const r = stage.getBoundingClientRect();
      ox += now.x - pinch.x;
      oy += now.y - pinch.y;
      zoomTo(z * now.d / pinch.d, now.x - r.left, now.y - r.top);
      pinch = now;
    });
    const lift = (e) => { pts.delete(e.pointerId); pinch = pts.size === 2 ? pinchNow() : null; };
    stage.addEventListener('pointerup', lift);
    stage.addEventListener('pointercancel', lift);
    stage.addEventListener('wheel', (e) => {
      e.preventDefault();
      const r = stage.getBoundingClientRect();
      zoomTo(z * Math.exp(-e.deltaY * 0.0015), e.clientX - r.left, e.clientY - r.top);
    }, { passive: false });
    zoom.oninput = () => zoomTo(+zoom.value, S / 2, S / 2);
    stage.addEventListener('keydown', (e) => {
      const step = e.shiftKey ? 40 : 10;
      const moves = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };   // фото їде туди, куди стрілка
      if (moves[e.key]) { ox += moves[e.key][0]; oy += moves[e.key][1]; clamp(); redraw(); }
      else if (e.key === '+' || e.key === '=') zoomTo(z * 1.1, S / 2, S / 2);
      else if (e.key === '-') zoomTo(z / 1.1, S / 2, S / 2);
      else return;
      e.preventDefault();
    });

    const close = () => {
      wrap.remove();
      document.removeEventListener('keydown', onKey, true);
      URL.revokeObjectURL(src.url);
    };
    const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
    document.addEventListener('keydown', onKey, true);
    wrap.addEventListener('click', (e) => { if (e.target === wrap) close(); });
    wrap.querySelector('[data-no]').onclick = close;
    wrap.querySelector('[data-other]').onclick = () => { close(); pickPhoto(); };
    wrap.querySelector('[data-yes]').onclick = (e) => o.busy(e.currentTarget, 'зберігаю…', async () => {
      const side = S / k();
      const sx = Math.max(0, Math.min(src.w - side, -ox / k()));
      const sy = Math.max(0, Math.min(src.h - side, -oy / k()));
      const blob = await encode(shrink(src, sx, sy, side, PH_OUT));
      if (!blob) { o.toast('Не вдалось стиснути фото — спробуй інше', 'err'); return; }
      try {
        const r = await upload(blob);
        close();
        o.toast(r.message || 'Фото стоїть', 'ok');
        await load();
        paint();
        if (o.onMine) o.onMine();
      } catch (err) { o.toast(err.message, 'err'); }
    });
    stage.focus();
  }

  /// Тіло запиту — сама картинка: так найменше байтів і нічого розбирати на сервері, крім магічних байтів.
  async function upload(blob) {
    const r = await fetch('/api/lavka/photo', {
      method: 'POST',
      headers: { 'Content-Type': blob.type || 'application/octet-stream', 'X-Nick': encodeURIComponent((o.me && o.me.nick) || '') },
      body: blob,
    });
    let d = null;
    try { d = await r.json(); } catch { /* без тіла */ }
    if (!r.ok) throw new Error((d && d.message) || 'HTTP ' + r.status);
    return d || {};
  }

  async function removePhoto(btn) {
    const p = (data && data.perks && data.perks.photo) || {};
    const yes = await ask('Прибрати фото?', 'Замість фото знову буде значок чи літера. Поставити нове можна буде '
      + (p.readyAt && Date.parse(p.readyAt) > Date.now() ? '<b>' + esc(readyIn(p.readyAt)) + '</b> — раз на добу.' : 'будь-коли.'), 'Прибрати');
    if (!yes) return;
    await o.busy(btn, 'прибираю…', async () => {
      try {
        const r = await o.api('DELETE', '/api/lavka/photo');
        o.toast(r.message || 'Фото прибрано', 'ok');
        await load();
        paint();
        if (o.onMine) o.onMine();
      } catch (e) { o.toast(e.message, 'err'); }
    });
  }

  // ---------- адміну: усі фотки з кнопкою «Зняти» (вкладка «📷 Фото» в Бібліотеці, поруч із «Пропозиціями») ----------

  const when = (iso) => new Date(iso).toLocaleString('uk-UA', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });

  async function adminPhotos(box) {
    if (!o || !o.me || o.me.role !== 'admin') { box.innerHTML = '<div class="empty">Це бачить лише розробник</div>'; return; }
    const r = await o.api('GET', '/api/lavka/photos');
    const items = (r && r.items) || [];
    const P = window.HPeople;
    const who = (n) => (P && P.nickLink ? P.nickLink(n, 'rnick') : '<b>' + esc(n) + '</b>');
    box.innerHTML = '<div class="muted small lv-adm-note">Свої фотки людей з Лавки, свіжі згори. «Зняти» прибирає фото (файл — геть), '
      + 'а вміння лишається: людина одразу може поставити інше.</div>'
      + '<ul class="list lv-adm">' + (items.map((x) => '<li class="lv-adm-row">'
        + '<a href="' + esc(x.url) + '" target="_blank" rel="noopener" title="Відкрити фото"><img class="lv-adm-img" src="' + esc(x.url) + '" alt="фото ' + esc(x.nick) + '" loading="lazy" decoding="async"></a>'
        + '<div class="lv-adm-who">' + who(x.nick) + '<span class="muted small">' + esc(when(x.at)) + ' · ' + Math.max(1, Math.round((x.bytes || 0) / 1024)) + ' КБ</span></div>'
        + '<button class="ghost" type="button" data-down="' + esc(x.nick) + '">Зняти</button></li>').join('')
        || '<li class="empty glek">Ще ніхто не поставив своє фото.</li>') + '</ul>';
    box.querySelectorAll('[data-down]').forEach((b) => b.onclick = async (e) => {
      const nick = b.dataset.down;
      const btn = e.currentTarget;
      if (!await ask('Зняти фото?', 'Фото <b>' + esc(genitive(nick)) + '</b> зникне звідусіль, файл видалиться. Вміння лишиться — людина зможе одразу поставити інше.', 'Зняти')) return;
      await o.busy(btn, 'знімаю…', async () => {
        try {
          const res = await o.api('POST', '/api/lavka/photos/remove', { nick });
          o.toast(res.message || 'Знято', 'ok');
          await adminPhotos(box);
        } catch (err) { o.toast(err.message, 'err'); }
      });
    });
  }

  // =============================================================================================
  // Вміння: чи є і коли знову можна
  // =============================================================================================

  /// Моє вміння: { owned, readyAt } або null — без сторінки Лавки теж (кнопки 🎆 і 💌 питають це в app.js).
  const perk = (id) => (data && data.perks && data.perks[id]) || null;
  const perkReady = (id) => { const p = perk(id); return !!(p && p.owned && !(p.readyAt && Date.parse(p.readyAt) > Date.now())); };
  /// Після вдалого вміння — перерва: ставимо її тут, не чекаючи, поки сервер відповість на новий GET.
  function usedPerk(id, minutes) {
    const p = perk(id);
    if (p) p.readyAt = new Date(Date.now() + minutes * 60000).toISOString();
  }

  window.HLavka = {
    init(opts) {
      o = opts;
      if (o.esc) esc = o.esc;
    },
    look,
    loadLooks,
    onLook,
    /// Своє з сервера (вітрина, шафа, вміння) — без сторінки: щоб app.js знав, чи показувати 🎆 і 💌.
    async loadMine() { if (!o || !o.me || !o.me.nick) return null; try { await load(); } catch { /* нема — то й нема */ } return data; },
    perk,
    perkReady,
    usedPerk,
    readyIn,
    dative,
    genitive,
    phrases: () => (data && data.phrases) || [],
    /// «Точно?» віконцем Лавки — ним питає й прожарка (web/liveads.js).
    ask,
    /// Після витрати поза вітриною (прожарка): свіжий баланс у «У глечику» й шапці.
    async refresh() { try { await load(); } catch { return; } if (shown) paint(); },
    avaOf,
    /// Адмінська вкладка «📷 Фото» в Бібліотеці: app.js дає контейнер, решту малює Лавка.
    adminPhotos,
    show(tail) { shown = true; render(tail); },
    hide() { shown = false; },
    tabOf: () => tab,
  };
})();
