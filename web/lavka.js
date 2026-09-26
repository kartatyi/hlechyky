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
    ['bg', '🖼 Тло'], ['perk', '✨ Вміння'], ['mine', '👜 Моя шафа']];
  const TIER = { 1: 'звичайний', 2: 'рідкісний', 3: 'особливий' };
  const PERK_TEXT = {
    dedication: 'Перед твоїм треком Дядько Глек скаже в ефір: «Цю пісню Оля присвячує Петрові — на удачу». Раз на 3 години.',
    fireworks: 'Кнопка 🎆 біля реакцій: феєрверк над обкладинкою в усіх і рядок у балачках. Раз на 10 хвилин.',
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
      const base = [...el.classList].filter((c) => !/^(ico|fr|fr-.+|rainbow)$/.test(c)).join(' ');
      el.outerHTML = P.ava(n, base, el.id || undefined);
    });
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
  /// Як виглядала б людина, якби вдягла ще й те, що зараз приміряє.
  function lookWith(extra) {
    const w = Object.assign({}, (data && data.worn) || {}, extra || {});
    const art = (slot) => { const it = item(w[slot]); return it ? it.art : null; };
    return { icon: art('icon'), frame: art('frame'), color: art('color'), title: art('title'), bg: art('bg') };
  }
  function avaOf(nick, l, cls) {
    const n = String(nick || '').replace(/^гість\s+/i, '').trim();
    const letter = n ? [...n][0].toUpperCase() : '?';
    const hue = l && typeof l.color === 'number' ? l.color : window.HPeople ? window.HPeople.hueRaw(nick) : 200;
    return '<span class="' + (cls || 'ava') + (l && l.frame ? ' fr fr-' + esc(l.frame) : '') + (l && l.icon ? ' ico' : '')
      + (l && l.color === 'rainbow' ? ' rainbow' : '') + '" style="--h:' + hue + '" aria-hidden="true">' + esc(l && l.icon ? l.icon : letter) + '</span>';
  }
  function nickOf(nick, l) {
    const hue = l && typeof l.color === 'number' ? l.color : window.HPeople ? window.HPeople.hueRaw(nick) : 200;
    return '<b class="lv-nick' + (l && l.color === 'rainbow' ? ' rainbow' : '') + '" style="--h:' + hue + '">' + esc(nick) + '</b>';
  }
  /// Маленька вітрина речі: як вона виглядатиме саме на тобі.
  function artOf(it) {
    const me = (o && o.me && o.me.nick) || 'Ти';
    switch (it.kind) {
      case 'icon': return '<span class="lv-emoji">' + esc(it.art) + '</span>';
      case 'frame': return avaOf(me, Object.assign(lookWith(), { frame: it.art }), 'ava xl');
      case 'color': return nickOf(me, { color: it.art });
      case 'title': return '<span class="lv-titlechip">' + esc(it.art) + '</span>';
      case 'bg': return '<span class="lv-bg bg-' + esc(it.art) + '"></span>';
      case 'perk': return '<span class="lv-emoji">' + (it.id === 'fireworks' ? '🎆' : '💌') + '</span>';
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
      state = it.owned ? '✓ здобуто' : '🔒 за ачівку «' + esc(it.earned.achTitle || it.earned.ach) + '»';
      if (it.owned && !gift) btns = it.worn ? '<button class="ghost" data-off="' + it.kind + '">Зняти</button>' : '<button class="primary" data-wear="' + it.id + '">Вдягти</button>';
    } else if (gift) {
      state = it.price + ' 🏺' + (it.season ? ' · ' + season(it.season) : '');
      const short = it.price - (data.balance || 0);
      btns = short > 0 ? '<button disabled>Бракує ' + short + ' 🏺</button>'
        : '<button class="primary" data-gift="' + it.id + '"' + (it.season && !it.season.open ? ' disabled' : '') + '>🎁 Подарувати</button>';
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
    return '<div class="lv-item' + (it.owned ? ' owned' : '') + (it.worn ? ' worn' : '') + (it.earned && !it.owned ? ' locked' : '')
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
    giftTo = parts[0] === 'gift' && parts[1] ? parts[1] : null;
    if (!giftTo && TABS.some(([k]) => k === parts[0])) tab = parts[0];
    if (giftTo && tab === 'mine') tab = 'icon';
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
    const head = '<section class="panel lv-head">'
      + '<div class="lv-top"><div class="lv-sign"><img src="/static/glek.svg" alt=""><div><h2>Лавка Дядька Глека</h2>'
      + '<div class="muted small">Усе, що купиш, — твоє назавжди. Черепки капають за радіо, партії й щоденний глек.</div></div></div>'
      + '<div class="lv-bal">У глечику <b>' + (data.balance != null ? data.balance : '—') + ' 🏺</b></div></div>'
      + (giftTo ? '<div class="lv-gift">🎁 Подарунок для <b>' + esc(genitive(giftTo)) + '</b> — обери річ. Подарувати можна лише те, чого в людини ще нема.'
        + ' <button class="ghost" data-go="#lavka">✕ скасувати</button></div>' : '')
      + (!acc ? '<div class="lv-guest">🔒 Лавка — для акаунтів: гостьовий нік може зайняти хтось інший, і куплене пропало б. '
        + '<button class="primary" data-acc>Закріпити нік</button></div>' : '')
      + (!giftTo ? previewHtml() : '')
      + '</section>';
    const tabs = '<nav class="lv-tabs" aria-label="Полиці лавки">' + TABS.filter(([k]) => !(giftTo && k === 'mine')).map(([k, l]) =>
      '<button type="button" data-tab="' + k + '"' + (k === tab ? ' class="on"' : '') + '>' + l + '</button>').join('') + '</nav>';
    const body = list.length
      ? '<div class="lv-grid">' + list.map(cardHtml).join('') + '</div>'
      : '<div class="gempty glek">' + (tab === 'mine' ? 'Шафа ще порожня. Обери щось на полицях — і воно лишиться з тобою назавжди.' : 'Тут поки порожньо.') + '</div>';
    root.innerHTML = head + '<section class="panel lv-shelf">' + tabs + body + '</section>';
    wire(root);
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
  }

  /// «Точно?» своїм віконцем: покупка назавжди, і черепки назад не повертаються.
  function ask(title, html, okText) {
    return new Promise((done) => {
      const wrap = document.createElement('div');
      wrap.className = 'modal lv-ask';
      wrap.innerHTML = '<div class="card"><h3>' + esc(title) + '</h3><div class="muted">' + html + '</div>'
        + '<div class="row"><button class="primary" type="button" data-yes>' + esc(okText) + '</button><button class="ghost" type="button" data-no>Передумав</button></div></div>';
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
    const yes = await ask(forNick ? '🎁 Подарувати?' : 'Купити?',
      '<b>' + esc(it.title) + '</b> ' + (forNick ? '<b>' + esc(dative(forNick)) + '</b> ' : '') + 'за <b>' + it.price + ' 🏺</b>. '
      + (forNick ? 'Подарунок лишиться в людини назавжди.' : 'Річ лишиться твоєю назавжди' + (it.kind === 'perk' ? '.' : ' — і одразу вдягнеться.')),
      forNick ? 'Подарувати' : 'Купити');
    if (!yes) return;
    await o.busy(btn, forNick ? 'дарую…' : 'купую…', async () => {
      try {
        const r = await o.api('POST', '/api/lavka/buy', forNick ? { item: id, for: forNick } : { item: id });
        o.toast(r.message || (forNick ? 'Подаровано!' : 'Твоє!'), 'ok');
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
    avaOf,
    show(tail) { shown = true; render(tail); },
    hide() { shown = false; },
    tabOf: () => tab,
  };
})();
