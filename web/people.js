/*
  Люди й цифри — window.HPeople.

  - Колір ніка й аватарка: сталий відтінок із хешу ніка (hue/ava). Той самий у балачках, «хто онлайн», черзі,
    таблицях і профілі — людину впізнаєш оком, не читаючи.
  - Картка людини: клік по будь-якому елементу з data-who="нік" (ловимо на всій сторінці). Хто, де зараз, трохи цифр
    і що з ним можна зробити: відкрити профіль, згадати в балачках, покликати за свій стіл, підсісти до нього.
  - Профіль #who/<нік>: і свій («Я» — з акаунтом і гаманцем по поличках), і будь-чий.
  - «📊 Хто скільки» #stats/<music|games|time>: музика, ігри й час з ОДНИМ перемикачем періоду
    (день · тиждень · місяць · весь час). Таблиці ігор і «⏱ Час» переїхали сюди з розділу «Ігри».

  app.js кличе init() на старті (дає api, рядки треків, дні й час — ті самі, що в Бібліотеці), show()/hide()
  при зміні маршруту й statsHash() для кнопки розділу. Каркас ігор (HGames) дає назви, іконки й столи.
*/
(() => {
  'use strict';

  let o = null;                       // що дає app.js
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const ls = (k, d) => { try { return localStorage.getItem(k) || d; } catch { return d; } };
  const lsSet = (k, v) => { try { localStorage.setItem(k, v); } catch { /* приватне вікно — не запам'ятаємо */ } };
  const G = () => window.HGames || null;
  const iconOf = (id) => (G() && G().iconOf ? G().iconOf(id) : '<span class="gemo">🎲</span>');
  const titleOf = (id) => (G() && G().titleOf ? G().titleOf(id) : id);

  // =============================================================================================
  // Колір ніка й аватарка
  // =============================================================================================

  /// Шістнадцять відтінків, рознесених по колу: ближчі за ~20° око на темному тлі не розрізняє.
  /// Жовтий (45°) пропускаємо — ним на сайті світяться кнопки й «мій хід».
  const HUES = [0, 18, 32, 62, 85, 110, 135, 158, 178, 195, 212, 228, 248, 270, 292, 318];
  /// FNV-1a від ніка, відтінок — зі СТАРШИХ бітів: молодші в FNV перемішуються погано, і за «% 16» половина
  /// друзів ставала одного кольору. Зерно підібране так, щоб у найбалакучіших (на 26.09.2026) кольори не збігались.
  const HUE_SEED = 30308;
  function hueRaw(nick) {
    const s = String(nick || '').toLowerCase().trim();
    let h = HUE_SEED;
    for (const ch of s) { h ^= ch.codePointAt(0); h = Math.imul(h, 16777619) >>> 0; }
    return HUES[Math.floor(h / 4294967296 * HUES.length)];
  }
  /// Що людина вдягла в Лавці Дядька Глека (web/lavka.js): значок, рамку, колір ніка, титул, тло профілю.
  const lookOf = (nick) => (window.HLavka && nick ? window.HLavka.look(nick) : null);
  /// Колір ніка: куплений у Лавці, а як нема — сталий із хешу. «Веселка» переливається класом rainbow,
  /// а --h лишається своїм — ним фарбуються смужки в таблицях.
  function hue(nick) {
    const l = lookOf(nick);
    return l && typeof l.color === 'number' ? l.color : hueRaw(nick);
  }
  const nickCls = (nick) => { const l = lookOf(nick); return l && l.color === 'rainbow' ? ' rainbow' : ''; };
  /// Значок як HTML. Прапор малюємо самі: Windows прапорів-емодзі не має — показав би «UA».
  const emo = (icon) => (icon === '🇺🇦' ? '<i class="fl-ua" role="img" aria-label="прапор України"></i>' : esc(icon));
  /// Своя фотка з Лавки. lazy — у довгих списках («Хто скільки», балачки) фото тягнуться, лише коли доїхали до екрана.
  const photoImg = (url) => '<img src="' + esc(url) + '" alt="" loading="lazy" decoding="async" draggable="false">';
  /// Куплений значок — маленьким перед ніком у балачках; є своя фотка — фото (значок у такому кружечку вже не розгледіти).
  const badge = (nick) => {
    const l = lookOf(nick);
    if (l && l.photo) return '<i class="nico ph" aria-hidden="true">' + photoImg(l.photo) + '</i>';
    return l && l.icon ? '<i class="nico" aria-hidden="true">' + emo(l.icon) + '</i>' : '';
  };
  /// Кружечок аватарки з вигляду l: фото (рамка — поверх, значок — маленьким кружечком у куточку), значок або перша
  /// літера ніка («гість Вася» — це «В», а не «Г»). who — нік для data-ava: за ним Лавка перемальовує аватарку на льоту
  /// (без нього — вітрина: «як виглядав би», перемальовувати нема чого).
  /// Клас blank — без літери: порожній кружечок у кольорі ніка (стос слухачів у шапці, там імена — у підказці).
  /// Лавка перемальовує з тими самими класами, тож порожнім він лишається й після перевдягання.
  function avaHtml(nick, l, cls, id, who) {
    const n = String(nick || '').replace(/^гість\s+/i, '').trim();
    const ph = l && l.photo;
    const blank = /(^|\s)blank(\s|$)/.test(cls || '');
    const ch = ph ? photoImg(ph) + (l.icon ? '<i class="ava-ic">' + emo(l.icon) + '</i>' : '')
      : l && l.icon ? emo(l.icon) : blank ? '' : esc(n ? [...n][0].toUpperCase() : '?');
    const h = l && typeof l.color === 'number' ? l.color : hueRaw(nick);
    return '<span' + (id ? ' id="' + id + '"' : '') + ' class="' + (cls || 'ava') + (ph ? ' ph' : l && l.icon ? ' ico' : '')
      + (l && l.frame ? ' fr fr-' + esc(l.frame) : '') + (l && l.color === 'rainbow' ? ' rainbow' : '') + '"'
      + (who != null ? ' data-ava="' + esc(who) + '"' : '') + ' style="--h:' + h + '" aria-hidden="true">' + ch + '</span>';
  }
  /// Аватарка людини так, як її бачать усі: значок, рамка, колір і фото — з Лавки.
  const ava = (nick, cls, id) => avaHtml(nick, lookOf(nick), cls, id, nick);
  const nickLink = (n, cls) => '<span class="' + (cls || 'who-n') + ' who-n' + nickCls(n) + '" data-who="' + esc(n) + '" style="--h:' + hue(n) + '">' + esc(n) + '</span>';
  /// Титул — під ніком у картці й у профілі.
  const titleChip = (nick) => { const l = lookOf(nick); return l && l.title ? '<span class="lv-titlechip">' + esc(l.title) + '</span>' : ''; };
  /// Гостьовий нік (з приставкою «гість ») — не акаунт: у Лавці йому нічого не купиш і не подаруєш.
  const isGuestNick = (n) => /^гість\s/i.test(String(n || ''));

  // =============================================================================================
  // Час і великі числа (переїхали з core.js разом із «⏱ Час» і таблицями)
  // =============================================================================================

  /// «2 год 5 хв», «45 хв», «<1 хв».
  function dur(sec) {
    const s = Math.max(0, Math.round(sec || 0));
    if (s < 60) return s ? '<1 хв' : '0 хв';
    const m = Math.round(s / 60);
    if (m < 60) return m + ' хв';
    const h = Math.floor(m / 60);
    const rest = m % 60;
    return h + ' год' + (rest && h < 100 ? ' ' + rest + ' хв' : '');
  }

  /// З якого дня пишеться час («2026-09-26» → «26 вересня»; рік — лише коли вже не цей).
  function sinceWord(day) {
    if (!day) return '';
    const d = new Date(day + 'T12:00:00');
    if (isNaN(d)) return '';
    const opts = { day: 'numeric', month: 'long' };
    if (d.getFullYear() !== new Date().getFullYear()) opts.year = 'numeric';
    return d.toLocaleDateString('uk-UA', opts);
  }

  // [поле відповіді, підпис, клас кольору]
  const TIME_PARTS = [['play', 'у грі', 'gt-play'], ['watch', 'глядачем', 'gt-watch'],
    ['lobby', 'лобі ігор', 'gt-lobby'], ['page', 'радіо й балачки', 'gt-page']];
  const timeTotal = (x) => Math.max(x.site || 0, TIME_PARTS.reduce((s, [k]) => s + (x[k] || 0), 0));
  const pct = (v, max) => (100 * (v || 0) / max).toFixed(2) + '%';

  function timeBar(x, max) {
    return '<div class="gt-bar">' + TIME_PARTS.map(([k, label, cls]) => (x[k] > 0
      ? '<i class="' + cls + '" style="width:' + pct(x[k], max) + '" title="' + esc(label + ': ' + dur(x[k])) + '"></i>' : '')).join('') + '</div>';
  }

  /// Рядок гри: назва, смужка (грав + дивився), скільки грав; <tail> — що ще дописати праворуч.
  function timeGameRow(g, max, tail) {
    return '<div class="gt-grow"><span class="gt-gname">' + iconOf(g.game) + esc(g.title || titleOf(g.game)) + '</span>'
      + '<div class="gt-bar thin">' + (g.play > 0 ? '<i class="gt-play" style="width:' + pct(g.play, max) + '"></i>' : '')
      + (g.watch > 0 ? '<i class="gt-watch" style="width:' + pct(g.watch, max) + '"></i>' : '') + '</div>'
      + '<b class="gt-gsum">' + (g.play > 0 ? dur(g.play) : '—') + '</b>'
      + (g.watch > 0 ? '<span class="gt-eye" title="глядачем за чужими столами">👀 ' + dur(g.watch) + '</span>' : '<span class="gt-eye"></span>')
      + (tail || '') + '</div>';
  }

  /// Число в клітинці таблиці. Від мільйона — коротко, як у Гончарному колі: «3,6 трлн», від квадрильйона — у гривнях
  /// («5,93 млн ₴»), від 10²⁷ — у червоних золотих (десяте оновлення кола, docs/games/specs/clicker-v10.md §6; ті самі
  /// пороги, що Clicker.Short). Інакше глеки гончарів стояли б у таблиці як «3.601004441162112e+21».
  const LB_BIG = ['млн', 'млрд', 'трлн'];
  function lbCount(n) {
    if (Math.abs(n) < 1e6) return n % 1 && Math.abs(n) < 1000 ? (Math.round(n * 10) / 10).toLocaleString('uk-UA') : Math.trunc(n * (1 + 1e-12)).toLocaleString('uk-UA');
    const i = Math.floor(Math.log10(Math.abs(n)) / 3) - 2;
    if (i >= LB_BIG.length) {
      let e = Math.floor(Math.log10(Math.abs(n)));
      let m = Math.round((n / Math.pow(10, e)) * 10) / 10;
      if (Math.abs(m) >= 10) { m /= 10; e += 1; }
      return m.toLocaleString('uk-UA', { maximumFractionDigits: 1 }) + 'e' + e;
    }
    const v = n / Math.pow(1000, i + 2);
    const digits = v < 10 ? 2 : v < 100 ? 1 : 0;
    const k = Math.pow(10, digits);
    return (Math.trunc(v * k * (1 + 1e-12)) / k).toLocaleString('uk-UA', { maximumFractionDigits: digits }) + ' ' + LB_BIG[i];
  }
  function lbNum(n) {
    if (typeof n !== 'number' || !Number.isFinite(n) || Math.abs(n) < 1e6) return n;
    const a = Math.abs(n);
    if (a < 1e15) return lbCount(n);
    if (a < 1e27) return lbCount(n / 1e15) + ' ₴';
    const g = n / 1e27;
    const shown = Math.abs(g) >= 1000 ? Math.trunc(g * (1 + 1e-12)) : Math.round(g * 10) / 10;
    const word = Math.abs(g) >= 1e6 ? 'золотих' : shown % 1 ? 'золотого'
      : shown % 100 >= 11 && shown % 100 <= 14 ? 'золотих' : shown % 10 === 1 ? 'золотий' : shown % 10 >= 2 && shown % 10 <= 4 ? 'золоті' : 'золотих';
    return lbCount(g) + ' ' + word;
  }
  const secs = (ms) => (ms == null ? '' : (ms / 1000).toFixed(ms < 10000 ? 1 : 0) + ' с');
  const shards = (n) => {
    const a = Math.abs(n) % 100, b = Math.abs(n) % 10;
    return n + ' ' + (a >= 11 && a <= 14 ? 'черепків' : b === 1 ? 'черепок' : b >= 2 && b <= 4 ? 'черепки' : 'черепків');
  };

  // =============================================================================================
  // Спільний перемикач періоду
  // =============================================================================================

  const PERIODS = [['day', 'день'], ['week', 'тиждень'], ['month', 'місяць'], ['all', 'весь час']];
  const PERIOD_WORD = { day: 'за день', week: 'за тиждень', month: 'за місяць', all: 'за весь час' };
  const PERIOD_DAYS = { day: 1, week: 7, month: 30, all: 3650 };
  let period = ls('statsPeriod', 'week');
  if (!PERIOD_WORD[period]) period = 'week';

  function periodSeg() {
    return '<div class="stper" role="tablist" aria-label="Період">' + PERIODS.map(([k, l]) =>
      '<button type="button" data-p="' + k + '"' + (k === period ? ' class="on"' : '') + '>' + l + '</button>').join('') + '</div>';
  }

  // =============================================================================================
  // Де людина зараз (для картки й профілю)
  // =============================================================================================

  function whereIs(nick) {
    const st = o && o.state ? o.state() : null;
    const online = !!st && (st.online || []).some((n) => same(n, nick));
    const listening = !!st && (st.listeningNicks || []).some((n) => same(n, nick));
    const g = G();
    const room = g && g.roomOf ? g.roomOf(nick) : null;
    const solo = g && g.soloOf ? g.soloOf(nick) : null;
    return { online, listening, room, solo };
  }
  function whereHtml(nick) {
    const w = whereIs(nick);
    const bits = [];
    if (w.room) bits.push('за столом ' + iconOf(w.room.game) + '<b>' + esc(titleOf(w.room.game)) + '</b> <span class="muted">· ' + esc(w.room.state) + '</span>');
    else if (w.solo) bits.push('грає в ' + iconOf(w.solo) + '<b>' + esc(titleOf(w.solo)) + '</b>');
    if (w.listening) bits.push('🎧 слухає ефір');
    if (!bits.length) bits.push(w.online ? 'тусить на сайті' : '<span class="muted">не на сайті</span>');
    return '<span class="wdot' + (w.online ? ' on' : '') + '"></span>' + bits.join(' · ');
  }

  /// Мій стіл, що чекає гравців і має вільне місце — туди можна кликати інших.
  const myWaitingRoom = () => { const g = G(); return g && g.myWaitingRoom ? g.myWaitingRoom() : null; };

  /// Кнопки дій над людиною — однакові в картці й у профілі.
  function actionsHtml(nick, full) {
    const me = o.me;
    if (!me.nick || same(nick, me.nick)) return full ? '' : '<button type="button" class="primary" data-pa="profile">👤 Мій профіль</button>';
    const w = whereIs(nick);
    const out = [];
    if (!full) out.push('<button type="button" class="primary" data-pa="profile">👤 Профіль</button>');
    out.push('<button type="button" data-pa="mention" title="Гукнути в балачках — почує дзінь">@ Гукнути</button>');
    const mine = myWaitingRoom();
    if (mine && w.online && !(w.room && w.room.id === mine.id)) out.push('<button type="button" data-pa="invite" title="Гукнути за мій стіл">📣 Гукнути</button>');
    if (w.room && w.room.canSit) out.push('<button type="button" data-pa="sit" title="Сісти за той самий стіл">🎲 Підсісти</button>');
    if (me.account && !isGuestNick(nick)) {
      out.push('<button type="button" data-pa="gift" title="Подарувати щось із Лавки Дядька Глека — лишиться назавжди">🎁 Подарувати</button>');
      // 😈 прокльон (docs/games/specs/flair.md §1.5): на трьох програшах людини звучить твій звук; від кого — секрет
      out.push('<button type="button" data-pa="curse" title="Наслати прокльон із Лавки: звучатиме на трьох програшах цієї людини, від кого — секрет">😈 Прокльон</button>');
    }
    // адміну — 🔇 / 🚫 / 🧹 (web/moder.js)
    if (window.HModer) out.push(HModer.cardHtml(nick));
    return out.join('');
  }
  function wireActions(box, nick, after) {
    box.querySelectorAll('[data-pa]').forEach((b) => b.onclick = async (e) => {
      const a = b.dataset.pa;
      const g = G();
      if (a === 'profile') o.go('#who/' + encodeURIComponent(nick));
      else if (a === 'gift') o.go('#lavka/gift/' + encodeURIComponent(nick));
      else if (a === 'curse') o.go('#lavka/curse/' + encodeURIComponent(nick));
      else if (a === 'mention') o.mention(nick);
      else if (a === 'invite') {
        const room = myWaitingRoom();
        if (room && g) await o.busy(e.currentTarget, 'кличу…', () => g.call('InviteTo', room.id, nick));
      } else if (a === 'sit') {
        const w = whereIs(nick);
        if (w.room && g) await g.sitAt(w.room.id, e.currentTarget);
      }
      if (after) after(a);
    });
    if (window.HModer) HModer.wireCard(box, nick, () => { if (after) after('mod'); });
  }

  // =============================================================================================
  // Картка людини — по кліку на нік
  // =============================================================================================

  let cardEl = null;
  const cache = new Map();            // нік → { at, data } — профіль із /api/games/profile, живе хвилину
  async function profileOf(nick) {
    const k = String(nick).toLowerCase();
    const c = cache.get(k);
    if (c && Date.now() - c.at < 60000) return c.data;
    const data = await o.api('GET', '/api/games/profile?nick=' + encodeURIComponent(nick));
    cache.set(k, { at: Date.now(), data });
    return data;
  }

  function closeCard() {
    if (!cardEl) return;
    cardEl.remove();
    cardEl = null;
  }

  function placeCard(el, anchor) {
    // На телефоні — шторка знизу на всю ширину, на широкому — поруч із ніком, у межах вікна.
    if (o.isMobile()) { el.classList.add('sheet'); return; }
    const r = anchor.getBoundingClientRect();
    const w = el.offsetWidth, h = el.offsetHeight;
    let x = Math.min(Math.max(8, r.left), window.innerWidth - w - 8);
    let y = r.bottom + 6;
    if (y + h > window.innerHeight - 8) y = Math.max(8, r.top - h - 6);
    el.style.left = x + 'px';
    el.style.top = y + 'px';
  }

  async function card(nick, anchor) {
    if (!o || !nick) return;
    if (cardEl && same(cardEl.dataset.nick, nick)) { closeCard(); return; }
    closeCard();
    const el = document.createElement('div');
    el.className = 'pcard';
    el.dataset.nick = nick;
    el.setAttribute('role', 'dialog');
    el.innerHTML = '<div class="pc-head">' + ava(nick, 'ava xl') + '<div class="pc-who"><b class="' + nickCls(nick).trim() + '" style="--h:' + hue(nick) + '">' + esc(nick) + '</b>'
      + titleChip(nick) + '<div class="pc-where">' + whereHtml(nick) + '</div></div>'
      + '<button type="button" class="ghost icon pc-x" title="Закрити" aria-label="Закрити">✕</button></div>'
      + '<div class="pc-stats"><span class="spin"></span> <span class="muted small">мить…</span></div>'
      + '<div class="pc-acts">' + actionsHtml(nick, false) + '</div>';
    document.body.appendChild(el);
    cardEl = el;
    placeCard(el, anchor);
    el.querySelector('.pc-x').onclick = closeCard;
    wireActions(el, nick, (a) => { if (a !== 'invite') closeCard(); });
    let p = null;
    try { p = await profileOf(nick); } catch { /* цифр нема — картка однаково корисна */ }
    if (cardEl !== el) return;
    const box = el.querySelector('.pc-stats');
    if (!p) { box.innerHTML = '<span class="muted small">Цифр про цю людину ще нема.</span>'; return; }
    const w = p.wallet || {};
    const t = p.time || null;
    const achs = (p.achievements || []).length;
    const fav = t && (t.games || []).filter((g) => g.play > 0)[0];
    box.innerHTML = '<span class="chip" title="Черепків зараз">🏺 ' + (w.balance != null ? lbNum(w.balance) : '—') + '</span>'
      + (t ? '<span class="chip" title="' + esc('Скільки часу тусить на сайті — ' + (p.timeSince ? 'з ' + sinceWord(p.timeSince) : 'за весь час')) + '">⏱ ' + dur(timeTotal(t)) + '</span>' : '')
      + '<span class="chip" title="Ачівки">🏅 ' + achs + '</span>'
      + (fav ? '<span class="chip" title="У що найбільше грає">' + iconOf(fav.game) + esc(fav.title || titleOf(fav.game)) + '</span>' : '');
    placeCard(el, anchor);
  }

  document.addEventListener('click', (e) => {
    const w = e.target.closest('[data-who]');
    if (w && !e.defaultPrevented && w.dataset.who) {
      e.preventDefault();
      card(w.dataset.who, w);
      return;
    }
    if (cardEl && !e.target.closest('.pcard')) closeCard();
  });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && cardEl) { closeCard(); e.stopPropagation(); } }, true);
  window.addEventListener('hashchange', closeCard);
  window.addEventListener('resize', closeCard);

  // =============================================================================================
  // «📊 Хто скільки»
  // =============================================================================================

  const STATS_TABS = [['music', '🎵 Музика'], ['games', '🎮 Ігри'], ['time', '⏱ Час']];
  let statsTab = ls('statsTab', 'music');
  if (!STATS_TABS.some(([k]) => k === statsTab)) statsTab = 'music';
  let token = 0;                       // щоб запізніла відповідь не малювала поверх свіжішої вкладки
  const stale = (t) => t !== token;

  function renderStats(tail) {
    const want = STATS_TABS.some(([k]) => k === tail) ? tail : statsTab;
    if (want !== tail) history.replaceState(null, '', '#stats/' + want);
    statsTab = want;
    lsSet('statsTab', statsTab);
    const root = document.getElementById('stats');
    root.innerHTML = '<div class="sthead panel-lite">'
      + '<nav class="sttabs" id="statsTabs" aria-label="Що рахуємо">' + STATS_TABS.map(([k, l]) =>
        '<button type="button" data-t="' + k + '"' + (k === statsTab ? ' class="on"' : '') + '>' + l + '</button>').join('') + '</nav>'
      + periodSeg() + '</div><div class="stbody"></div>';
    root.querySelectorAll('.sttabs [data-t]').forEach((b) => b.onclick = () => o.go('#stats/' + b.dataset.t));
    root.querySelectorAll('.stper [data-p]').forEach((b) => b.onclick = () => {
      period = b.dataset.p;
      lsSet('statsPeriod', period);
      root.querySelectorAll('.stper [data-p]').forEach((x) => x.classList.toggle('on', x === b));
      drawStatsBody();
    });
    drawStatsBody();
  }
  function drawStatsBody() {
    const body = document.querySelector('#stats .stbody');
    if (!body) return;
    const t = ++token;
    body.innerHTML = '<div class="gwait"><span class="spin"></span> рахую…</div>';
    const run = statsTab === 'games' ? statsGames : statsTab === 'time' ? statsTime : statsMusic;
    run(body, t).catch((e) => { if (!stale(t)) body.innerHTML = '<div class="gempty">Ой-йой, не порахувалось: ' + esc(e.message) + '</div>'; });
  }

  // ---------- музика: хто закидає і що крутили ----------
  const ratingSort = () => ls('ratingSort', 'plays');
  async function statsMusic(body, t) {
    const days = PERIOD_DAYS[period];
    const sort = ratingSort();
    const [top, rating] = await Promise.all([
      o.api('GET', '/api/top?period=' + period + '&days=' + days),
      o.api('GET', '/api/rating?period=' + period + '&days=' + days + '&sort=' + encodeURIComponent(sort)),
    ]);
    if (stale(t)) return;
    // Автодиджей закидає за всіх, поки ніхто нічого не ставить, — у людському топі він лише заважав би. Старий
    // сервер віддає його серед людей, новий — окремим полем dj.
    const djName = o.dj();
    const people = (top.requesters || []).filter((x) => !same(x.nick, djName));
    const djRow = top.dj || (top.requesters || []).find((x) => same(x.nick, djName)) || null;
    const max = Math.max(1, ...people.map((x) => x.count));
    const peopleHtml = people.length
      ? '<div class="stpeople">' + people.map((x, i) => '<div class="stp' + (same(x.nick, o.me.nick) ? ' me' : '') + '">'
        + '<span class="n">' + (i + 1) + '</span>' + ava(x.nick, 'ava sm') + nickLink(x.nick, 'stp-nick')
        + '<div class="stp-bar"><i style="width:' + Math.round(x.count / max * 100) + '%;--h:' + hue(x.nick) + '"></i></div>'
        + '<b>' + x.count + '</b></div>').join('') + '</div>'
      : '<div class="gempty glek">' + PERIOD_WORD[period][0].toUpperCase() + PERIOD_WORD[period].slice(1) + ' ніхто нічого не закидав — усе крутив ' + esc(djName) + '.</div>';
    const djLine = djRow && djRow.count ? '<div class="muted small stdj">🏺 А ' + esc(djName) + ' ' + PERIOD_WORD[period] + ' поставив сам ' + djRow.count + ' — коли черга порожніла.</div>' : '';

    const sortSeg = '<div class="tabs seg stsort">' + [['plays', 'частіше грали'], ['completion', 'дослуховують'], ['listeners', 'більше слухачів'], ['likes', 'вподобайки']]
      .map(([v, l]) => '<button type="button" data-s="' + v + '" class="' + (sort === v ? 'on' : '') + '">' + l + '</button>').join('') + '</div>';
    const row = (x) => {
      const bits = ['▶ ' + x.plays];
      if (x.completion != null) bits.push('<span title="у середньому дослуховують">до кінця ' + x.completion + '%</span>');
      if (x.listeners) bits.push('<span title="' + esc((x.listenerNicks || []).join(', ')) + '">🎧 ' + x.listeners + '</span>');
      if (x.streamPeak > x.listeners) bits.push('<span title="найбільше підключень до потоку разом з ETS2 і VLC">потік ' + x.streamPeak + '</span>');
      if (x.likes) bits.push('❤ ' + x.likes);
      if (x.skips) bits.push('скіп ' + x.skips);
      return o.trackRow(x.track, bits.join(' · '));
    };
    const c = rating.cache;
    const gb = (b) => (b / 1024 ** 3).toLocaleString('uk-UA', { maximumFractionDigits: 1 });
    const cacheLine = c && o.me.role === 'admin'
      ? '<div class="muted small stcache">Кеш треків: ' + gb(c.bytes) + (c.limitBytes ? ' з ' + gb(c.limitBytes) : '') + ' ГБ · ' + c.files + ' файлів (це бачить лише адмін)</div>' : '';
    body.innerHTML = '<div class="stgrid">'
      + '<section class="panel stbox"><h3>🙋 Хто закидає <span class="muted small">' + PERIOD_WORD[period] + '</span></h3>' + peopleHtml + djLine + '</section>'
      + '<section class="panel stbox wide"><div class="stbox-head"><h3>🎶 Що крутили</h3>'
      + '<input class="stfind" type="search" placeholder="знайти трек" autocomplete="off"></div>' + sortSeg
      + '<ul class="list strating">' + ((rating.tracks || []).map(row).join('') || '<li class="empty">за цей час — тиша: нічого не грало</li>') + '</ul>'
      + '<div class="muted small">🎧 — скільки людей слухало на сайті (рахується з 13.09).</div>' + cacheLine + '</section></div>';
    o.wireRows(body);
    body.querySelectorAll('.stsort [data-s]').forEach((b) => b.onclick = () => { lsSet('ratingSort', b.dataset.s); drawStatsBody(); });
    const find = body.querySelector('.stfind');
    find.oninput = () => {
      const q = find.value.trim().toLowerCase();
      body.querySelectorAll('.strating > li').forEach((li) => { li.hidden = !!q && !li.textContent.toLowerCase().includes(q); });
    };
  }

  // ---------- ігри: таблиці ----------
  // ключі — як їх називає Leaderboards.cs: rated → elo/wins/losses/draws/games/streak,
  // solo → best/tries, daily → attempts/ms, shards → balance/earned
  const LB_COLS = [['elo', 'Ело'], ['wins', 'В'], ['losses', 'П'], ['draws', 'Н'], ['games', 'партій'],
    ['streak', 'серія'], ['score', 'результат'], ['best', 'рекорд'], ['attempts', 'спроб'],
    ['tries', 'спроб'], ['ms', 'час'], ['balance', '🏺 є зараз'], ['earned', 'зароблено'], ['count', 'разів']];
  const LB_TIPS = { elo: 'рейтинг Ело', wins: 'перемог', losses: 'поразок', draws: 'нічиїх', balance: 'скільки черепків у глечику зараз' };
  let lbGame = ls('gamesLbGame', 'shards');
  const asList = (r) => (Array.isArray(r) ? r : (r && (r.rows || r.top || r.items || r.list)) || []);
  const GROUP_TITLES = { board: '♟ Настільні', live: '⚡ Швидкі', party: '🎉 Компанія', solo: '🏺 Соло' };

  async function statsGames(body, t) {
    const g = G();
    if (g && g.ready) await g.ready().catch(() => {});
    if (stale(t)) return;
    const games = (g && g.catalog && g.catalog.games) || [];
    if (lbGame !== 'shards' && !games.some((x) => x.id === lbGame)) lbGame = 'shards';
    const groups = ['board', 'live', 'party', 'solo'].map((k) => [k, games.filter((x) => x.group === k)]).filter(([, l]) => l.length);
    body.innerHTML = '<section class="panel stbox"><div class="glbbar">'
      + '<select class="glbgame" aria-label="Яка таблиця"><option value="shards"' + (lbGame === 'shards' ? ' selected' : '') + '>🏺 Черепки — хто скільки заробив</option>'
      + groups.map(([k, list]) => '<optgroup label="' + esc(GROUP_TITLES[k] || k) + '">' + list.map((x) =>
        '<option value="' + esc(x.id) + '"' + (x.id === lbGame ? ' selected' : '') + '>' + esc(x.title) + '</option>').join('') + '</optgroup>').join('')
      + '</select></div><div class="glbbox"><div class="gwait"><span class="spin"></span> рахую…</div></div></section>';
    body.querySelector('.glbgame').onchange = (e) => { lbGame = e.target.value; lsSet('gamesLbGame', lbGame); drawStatsBody(); };
    const box = body.querySelector('.glbbox');
    if (lbGame === 'dice') seasonDice(body, t);   // «Під глеком»: смішні звання партій за період (прохід №3, №130)
    const r = await o.api('GET', '/api/games/leaderboard?game=' + encodeURIComponent(lbGame) + '&period=' + encodeURIComponent(period));
    if (stale(t) || !box.isConnected) return;
    const rows = asList(r);
    if (!rows.length) { box.innerHTML = '<div class="gempty">' + PERIOD_WORD[period][0].toUpperCase() + PERIOD_WORD[period].slice(1) + ' тут ще ніхто не відзначився.</div>'; return; }
    const cols = LB_COLS.filter(([k]) => rows.some((x) => x[k] != null))
      .map(([k, l]) => [k, k === 'earned' ? 'зароблено ' + PERIOD_WORD[period] : l]);
    box.innerHTML = '<div class="glb wide"><div class="glbrow head"><span>#</span><span>хто</span>'
      + cols.map(([k, l]) => '<span' + (LB_TIPS[k] ? ' title="' + esc(LB_TIPS[k]) + '"' : '') + '>' + esc(l) + '</span>').join('') + '</div>'
      + rows.map((x, i) => '<div class="glbrow' + (same(x.nick, o.me.nick) ? ' me' : '') + '"><span class="n">' + (i + 1) + '</span>'
        + '<span class="glb-who">' + ava(x.nick || '', 'ava sm') + nickLink(x.nick || '') + '</span>'
        + cols.map(([k]) => '<span>' + esc(k === 'ms' ? secs(x[k]) : (x[k] == null ? '—' : lbNum(x[k]))) + '</span>').join('')
        + '</div>').join('') + '</div>';
  }

  /// «Блефер сезону» — звання партій «Під глеком» (сервер: /api/games/dice/season). «Сьогодні» рахуємо як тиждень.
  async function seasonDice(body, t) {
    const d = await o.api('GET', '/api/games/dice/season?period=' + (period === 'month' || period === 'all' ? period : 'week')).catch(() => null);
    const titles = (d && d.titles) || [];
    if (stale(t) || !titles.length || !body.isConnected) return;
    const sec = document.createElement('section');
    sec.className = 'panel stbox';
    sec.innerHTML = '<h4>🏆 Сезон під глеком — ' + esc(d.period === 'all' ? 'за весь час' : d.period === 'month' ? 'за 30 днів' : 'за 7 днів') + '</h4>'
      + '<div class="glb wide"><div class="glbrow head"><span>звання</span><span>хто</span><span>скільки</span></div>'
      + titles.map((x) => x.rows.map((r, i) => '<div class="glbrow' + (same(r.nick, o.me.nick) ? ' me' : '') + '"><span>' + (i ? '' : esc(x.icon + ' ' + x.title)) + '</span>'
        + '<span class="glb-who">' + ava(r.nick, 'ava sm') + nickLink(r.nick) + '</span><span>×' + r.n + '</span></div>').join('')).join('')
      + '</div>';
    body.appendChild(sec);
  }

  // ---------- час ----------
  async function statsTime(body, t) {
    const g = G();
    // Іконки ігор живуть у модулях: чекаємо їх разом із цифрами, а не малюємо 🎲.
    const [r] = await Promise.all([o.api('GET', '/api/games/time?period=' + encodeURIComponent(period)), g && g.ready ? g.ready().catch(() => {}) : null]);
    if (stale(t)) return;
    const people = (r && r.people) || [];
    const games = (r && r.games) || [];
    const max = Math.max(1, ...people.map(timeTotal));
    const gmax = Math.max(1, ...games.map((x) => (x.play || 0) + (x.watch || 0)));
    const since = sinceWord(r && r.since);
    const sum = (k) => people.reduce((s, x) => s + (x[k] || 0), 0);
    const all = people.reduce((s, x) => s + timeTotal(x), 0);
    const sinceLine = since ? '<div class="gt-since">🗓 Час пишемо з <b>' + esc(since) + '</b> — що було раніше, не записано.</div>' : '';
    const note = '<div class="muted small">Лише поки вкладка на екрані й людина щось робить; радіо — поки грає плеєр.</div>';
    if (!people.length) {
      body.innerHTML = '<section class="panel stbox">' + sinceLine + '<div class="gempty glek">' + PERIOD_WORD[period][0].toUpperCase() + PERIOD_WORD[period].slice(1)
        + ' ще нічого не натікало. Хвилини пишуться, поки вкладка на екрані й ти щось робиш.</div>' + note + '</section>';
      return;
    }
    body.innerHTML = '<section class="panel stbox gtime">'
      + sinceLine
      + '<div class="gt-sum4">'
      + '<div><b>' + dur(all) + '</b><span>уся тусня разом на сайті</span></div>'
      + '<div><b>' + dur(sum('play')) + '</b><span>в іграх</span></div>'
      + '<div><b>' + dur(sum('listen')) + '</b><span>📻 грало радіо</span></div>'
      + (games[0] ? '<div><b>' + iconOf(games[0].game) + esc(games[0].title) + '</b><span>найдовше грали · ' + dur(games[0].play) + '</span></div>' : '')
      + '</div>'
      + '<h4>Хто скільки</h4>'
      + '<div class="gt-legend">' + TIME_PARTS.map(([, l, cls]) => '<span><i class="gt-dot ' + cls + '"></i>' + l + '</span>').join('')
      + '<span class="muted">натисни на рядок — розкладу по іграх</span></div>'
      + '<div class="gt-people">' + people.map((x, i) => {
        const mine = same(x.nick, o.me.nick);
        const gl = x.games || [];
        const pmax = Math.max(1, ...gl.map((y) => (y.play || 0) + (y.watch || 0)));
        return '<details class="gt-person' + (mine ? ' me' : '') + '"' + (mine ? ' open' : '') + '><summary>'
          + '<span class="n">' + (i + 1) + '</span><span class="gt-nick">' + ava(x.nick, 'ava sm') + nickLink(x.nick) + '</span>'
          + timeBar(x, max)
          + '<b class="gt-total">' + dur(timeTotal(x)) + '</b>'
          + '<span class="gt-radio" title="скільки грав плеєр радіо">' + (x.listen > 0 ? '📻 ' + dur(x.listen) : '') + '</span>'
          + '</summary><div class="gt-detail">'
          + '<div class="gt-chips">' + TIME_PARTS.filter(([k]) => x[k] > 0).map(([k, l, cls]) =>
            '<span class="chip"><i class="gt-dot ' + cls + '"></i>' + l + ' ' + dur(x[k]) + '</span>').join('') + '</div>'
          + (gl.length ? '<div class="gt-games">' + gl.map((y) => timeGameRow(y, pmax)).join('') + '</div>'
            : '<div class="gempty">За цей час — жодної гри.</div>')
          + '</div></details>';
      }).join('') + '</div>'
      + (games.length
        ? '<h4>Ігри</h4><div class="gt-games wide">' + games.map((y) => timeGameRow(y, gmax,
          '<span class="gt-who">' + (y.people || []).slice(0, 3).map((p) => '<span' + (same(p.nick, o.me.nick) ? ' class="me"' : '') + '>'
            + nickLink(p.nick) + ' <span class="muted">' + dur(p.sec) + '</span></span>').join('') + '</span>')).join('') + '</div>'
        : '')
      + note + '</section>';
  }

  // =============================================================================================
  // Профіль #who/<нік>
  // =============================================================================================

  let whoNick = null;
  const OUTCOME = { win: 'перемога', loss: 'поразка', draw: 'нічия', solo: 'соло' };
  /// «за 1 спробу», «за 3 спроби», «за 6 спроб».
  const tries = (n) => { const t = n % 100, u = n % 10; return n + (t > 10 && t < 20 ? ' спроб' : u === 1 ? ' спробу' : u >= 2 && u <= 4 ? ' спроби' : ' спроб'); };
  /// «1 раз», «3 рази», «12 разів».
  const razy = (n) => { const t = n % 100, u = n % 10; return n + (t > 10 && t < 20 ? ' разів' : u === 1 ? ' раз' : u >= 2 && u <= 4 ? ' рази' : ' разів'); };
  /// Підпис під числом ❤: «вподобайка», «вподобайки», «вподобайок».
  const likesWord = (n) => { const t = n % 100, u = n % 10; return t > 10 && t < 20 ? 'вподобайок' : u === 1 ? 'вподобайка' : u >= 2 && u <= 4 ? 'вподобайки' : 'вподобайок'; };

  async function renderWho(tail) {
    const nick = decodeURIComponent(tail || '').trim();
    whoNick = nick;
    const root = document.getElementById('who');
    const mine = same(nick, o.me.nick);
    const t = ++token;
    root.innerHTML = headHtml(nick, mine, null) + '<div class="gwait"><span class="spin"></span> дивлюсь…</div>';
    wireHead(root, nick, mine);
    const g = G();
    const opt = (p) => p.catch(() => null);
    const [p, ppl, led, wk, anth] = await Promise.all([
      opt(o.api('GET', '/api/games/profile?nick=' + encodeURIComponent(nick))),
      opt(o.api('GET', '/api/people/' + encodeURIComponent(nick))),
      mine ? opt(o.api('GET', '/api/games/ledger?limit=15')) : null,
      opt(o.api('GET', '/api/games/time?period=week')),
      // 🎺 гімн переможця з Лавки: 404 — гімну нема (або нічого не вдягнуто), рядка теж нема
      opt(o.api('GET', '/api/lavka/anthem/of/' + encodeURIComponent(nick))),
      g && g.ready ? g.ready().catch(() => {}) : null,
    ]);
    if (stale(t) || whoNick !== nick) return;
    if (p) cache.set(nick.toLowerCase(), { at: Date.now(), data: p });
    const week = wk && (wk.people || []).find((x) => same(x.nick, nick));
    const known = !!(p && ((p.wallet && (p.wallet.earned || p.wallet.balance)) || (p.achievements || []).length || p.time || (p.recent || []).length))
      || !!(ppl && ppl.music && (ppl.music.requests.all || ppl.music.likes.count));
    root.innerHTML = headHtml(nick, mine, ppl, anth)
      + (known || mine ? '<div class="who-grid">'
        + walletCard(p, led, mine)
        + timeCard(p, week)
        + musicCard(ppl, nick, mine)
        + gamesCard(p, mine)
        + achCard(p, mine)
        + '</div>'
        : '<section class="panel"><div class="gempty glek">Про ' + esc(nick) + ' тут поки нічого не знають — ні пісень, ні ігор. Схоже, усе ще попереду.</div></section>');
    wireHead(root, nick, mine);
    o.wireRows(root);
    root.querySelectorAll('[data-go]').forEach((b) => b.onclick = () => o.go(b.dataset.go));
    root.querySelectorAll('[data-buy]').forEach((b) => b.onclick = () => window.HBuy && HBuy.open({ tab: HBuy.mainTab() }));
    root.querySelectorAll('[data-sell]').forEach((b) => b.onclick = () => window.HBuy && HBuy.open({ tab: 'sell' }));
  }

  /// «🎺 Гімн: Трембіта ▶» — ▶ грає тим самим плеєром, що й за столом (app.js), друге натискання зупиняє.
  function anthemLine(a) {
    if (!a || !a.url) return '';
    return '<div class="wh-anth"><span>🎺 Гімн: <b>' + esc(a.title || 'Свій трек') + '</b></span>'
      + '<button type="button" class="ghost" data-anth="' + esc(a.url) + '" title="Послухати гімн" aria-label="Послухати гімн">▶</button></div>';
  }

  function headHtml(nick, mine, ppl, anth) {
    const me = o.me;
    const acct = mine ? (me.account ? (me.google ? 'акаунт · Google прив\'язано' : 'акаунт з паролем') : 'гість — нік ще не закріплений')
      : ppl ? (ppl.account ? 'акаунт' : 'гість') : '';
    const btns = mine
      ? (me.account
        ? '<button type="button" class="primary" data-go="#lavka" title="Значки, рамки, колір ніка, титули, тло — назавжди, за черепки">🛍 Лавка</button>'
          + '<button type="button" data-acc="me" title="Пароль, Google, вийти">⚙ Акаунт</button>'
        : '<button type="button" class="primary" data-acc="register" title="Закріпити нік паролем — під ним ніхто інший не напише">✍ Закріпити нік</button>'
          + '<button type="button" data-acc="login">🔑 Зайти в акаунт</button><button type="button" class="ghost" data-acc="guest">✏ Інше ім\'я</button>')
      : actionsHtml(nick, true);
    const l = lookOf(nick);
    return '<section class="panel who-head' + (l && l.bg ? ' lv-bgd bg-' + esc(l.bg) : '') + '">' + ava(nick, 'ava xxl')
      + '<div class="wh-main"><h2 style="--h:' + hue(nick) + '"><span class="' + nickCls(nick).trim() + '">' + esc(nick) + '</span>'
      + (mine ? ' <span class="muted small">· це ти</span>' : '') + '</h2>'
      + titleChip(nick)
      + anthemLine(anth)
      + '<div class="wh-where">' + whereHtml(nick) + '</div>'
      + (acct ? '<div class="muted small">' + esc(acct) + '</div>' : '') + '</div>'
      + '<div class="wh-acts">' + btns + '</div></section>';
  }
  function wireHead(root, nick, mine) {
    root.querySelectorAll('[data-acc]').forEach((b) => b.onclick = () => {
      const m = b.dataset.acc;
      if (m === 'register') o.askNick(true, 'register', String(o.me.nick || '').replace(/^гість\s*/i, ''));
      else o.askNick(true, m);
    });
    if (!mine) wireActions(root.querySelector('.who-head'), nick, () => {});
    root.querySelectorAll('.wh-anth [data-anth]').forEach((b) => b.onclick = () => { if (o.toggleAnthem) o.toggleAnthem({ url: b.dataset.anth }); });
    if (o.paintAnthemBtns) o.paintAnthemBtns(root);
    root.querySelectorAll('.who-head [data-go]').forEach((b) => b.onclick = () => o.go(b.dataset.go));
  }

  function walletCard(p, led, mine) {
    const w = (p && p.wallet) || {};
    let body = '<div class="wc-big">🏺 ' + (w.balance != null ? lbNum(w.balance) : '—') + '</div>'
      + '<div class="muted small">зароблено ' + (w.earned != null ? lbNum(w.earned) : '—') + ' · витрачено ' + (w.spent != null ? lbNum(w.spent) : '—') + '</div>';
    if (mine && led) {
      const month = led.month || [];
      if (month.length) {
        body += '<h4>Цього місяця</h4><div class="wc-cats">' + month.map((c) => '<span class="chip' + (c.spent > c.earned ? ' minus' : '') + '">'
          + esc(c.title) + ' ' + (c.earned ? '<b class="plus">+' + lbNum(c.earned) + '</b>' : '') + (c.spent ? ' <b class="minus">−' + lbNum(c.spent) + '</b>' : '') + '</span>').join('') + '</div>';
      }
      const items = led.items || [];
      if (items.length) {
        body += '<h4>Останнє</h4><div class="wc-led">' + items.slice(0, 12).map((x) => '<div><b class="' + (x.delta < 0 ? 'minus' : 'plus') + '">'
          + (x.delta > 0 ? '+' : '−') + lbNum(Math.abs(x.delta)) + '</b><span>' + esc(String(x.text || x.reason).replace(/^[+−-]\d+\s+\S+:\s*/, '')) + '</span>'
          + '<span class="muted small">' + esc(o.dayTime(x.at)) + '</span></div>').join('') + '</div>';
      }
    }
    // Купити й продати за гривні — кожне можна вимкнути в конфігу (ShardShop:Buy / Sell); адміну «Заявки» лишаються
    const buyOn = !!(window.HBuy && HBuy.buyOn());
    const sellOn = !!(window.HBuy && HBuy.sellOn());
    const trade = buyOn && sellOn ? ' Можна купити й продати за гривні.' : buyOn ? ' Можна докупити за гривні.' : sellOn ? ' Можна продати за гривні.' : '';
    if (mine) body += '<div class="muted small wc-how">Черепки капають за радіо (увімкнений плеєр), партії, щоденний глек і ачівки. '
      + 'Витрачаються в Лавці Дядька Глека (усе там — назавжди), на бан треку й викуп із бану.' + trade + '</div>';
    return '<section class="panel wcard"><h3>🏺 Черепки' + (mine && o.me.account
      ? (window.HBuy ? (buyOn || o.me.role === 'admin' ? ' <button type="button" class="ghost wc-more" data-buy>' + esc(HBuy.label()) + '</button>' : '')
          + (sellOn ? ' <button type="button" class="ghost wc-more" data-sell title="Продати черепки за гривні">' + esc(HBuy.sellLabel()) + '</button>' : '') : '')
        + ' <button type="button" class="ghost wc-more" data-go="#lavka">🛍 Лавка →</button>'
      : '') + '</h3>' + body + '</section>';
  }

  function timeCard(p, week) {
    const t = p && p.time;
    if (!t && !week) return '<section class="panel wcard"><h3>⏱ Час</h3><div class="gempty">Ще не натікало — хвилини пишуться, поки вкладка на екрані й людина щось робить.</div></section>';
    const top = ((t && t.games) || []).filter((g) => g.play > 0).slice(0, 5);
    const max = Math.max(1, ...top.map((g) => g.play + (g.watch || 0)));
    return '<section class="panel wcard"><h3>⏱ Час <button type="button" class="ghost wc-more" data-go="#stats/time">усі →</button></h3>'
      + '<div class="wc-pair">'
      + (week ? '<div><b>' + dur(timeTotal(week)) + '</b><span>за тиждень</span></div>' : '')
      + (t ? '<div><b>' + dur(timeTotal(t)) + '</b><span>' + (p.timeSince ? 'з ' + esc(sinceWord(p.timeSince)) : 'за весь час') + '</span></div>' : '')
      + (t && t.listen > 0 ? '<div><b>' + dur(t.listen) + '</b><span>📻 радіо грало</span></div>' : '')
      + '</div>'
      + (top.length ? '<div class="gt-games">' + top.map((g) => timeGameRow(g, max)).join('') + '</div>' : '')
      + '</section>';
  }

  function musicCard(ppl, nick, mine) {
    const m = ppl && ppl.music;
    if (!m) return '<section class="panel wcard"><h3>🎵 Музика</h3><div class="gempty">Музичного сліду ще нема — жодної закинутої пісні й жодної вподобайки.</div></section>';
    const r = m.requests || {};
    const top = (m.top || []).map((x) => o.trackRow(x.track, 'закинуто ' + razy(x.count))).join('');
    const likes = ((m.likes && m.likes.recent) || []).map((x) => o.trackRow(x.track, '❤ ' + esc(o.dayTime(x.at)))).join('');
    const pls = (m.playlists || []);
    return '<section class="panel wcard"><h3>🎵 Музика</h3>'
      + '<div class="wc-pair"><div><b>' + (r.week || 0) + '</b><span>закинуто за тиждень</span></div><div><b>' + (r.month || 0) + '</b><span>за місяць</span></div>'
      + '<div><b>' + (r.all || 0) + '</b><span>усього</span></div><div><b>' + ((m.likes && m.likes.count) || 0) + '</b><span>❤ ' + likesWord((m.likes && m.likes.count) || 0) + '</span></div></div>'
      + (top ? '<h4>Закидає найчастіше</h4><ul class="list">' + top + '</ul>' : '')
      + (likes ? '<h4>Останні вподобайки' + (mine ? ' <button type="button" class="ghost wc-more" data-go="#lib/likes">усе →</button>' : '') + '</h4><ul class="list">' + likes + '</ul>' : '')
      + (pls.length ? '<h4>Плейлисти</h4><div class="wc-pls">' + pls.map((x) => '<button type="button" class="chip" data-go="#lib/playlists">📂 ' + esc(x.name) + ' · ' + x.count + '</button>').join('') + '</div>' : '')
      + (!top && !likes ? '<div class="gempty">' + (mine ? 'Від тебе ще жодної пісні й жодної вподобайки — усе попереду.' : 'Тут ще порожньо: жодної закинутої пісні й жодної вподобайки.') + '</div>' : '')
      + '</section>';
  }

  function gamesCard(p, mine) {
    const ratings = (p && p.ratings) || [];
    const recent = ((p && (p.recent || p.games)) || []).slice(0, 8);
    const daily = ((p && p.daily) || []).filter((d) => d.streak);
    if (!ratings.length && !recent.length && !daily.length) return '<section class="panel wcard"><h3>🎮 Ігри</h3><div class="gempty">Ще нічого не зіграно' + (mine ? ' — гайда за стіл!' : '.') + '</div></section>';
    return '<section class="panel wcard"><h3>🎮 Ігри <button type="button" class="ghost wc-more" data-go="#stats/games">таблиці →</button></h3>'
      + (daily.length ? '<div class="wc-cats">' + daily.map((d) => '<span class="chip">' + iconOf(d.game) + esc(d.title || titleOf(d.game)) + ' 🔥 ' + d.streak + ' дн. поспіль</span>').join('') + '</div>' : '')
      + (ratings.length ? '<h4>Рейтинги</h4><div class="glb">' + ratings.map((r) => '<div class="glbrow"><span>' + iconOf(r.game) + esc(titleOf(r.game)) + '</span>'
        + '<b title="рейтинг Ело">' + (r.elo != null ? r.elo : '—') + '</b>'
        + '<span class="muted small">' + (r.wins || 0) + '/' + (r.losses || 0) + '/' + (r.draws || 0) + ' · ' + (r.games || 0) + ' парт.</span></div>').join('') + '</div>' : '')
      + (recent.length ? '<h4>Останні партії</h4><div class="glb">' + recent.map((x) => '<div class="glbrow"><span>' + iconOf(x.game) + esc(titleOf(x.game)) + '</span>'
        + '<b class="o-' + esc(x.outcome || '') + '">' + esc(OUTCOME[x.outcome] || x.outcome || '') + '</b>'
        + '<span class="muted small">' + esc(x.opponents || '') + (x.score != null ? ' · ' + x.score : '') + '</span></div>').join('') + '</div>' : '')
      + '</section>';
  }

  function achCard(p, mine) {
    const achs = (p && p.achievements) || [];
    return '<section class="panel wcard wide"><h3>🏅 Ачівки <span class="muted small">' + achs.length + '</span></h3>'
      + (achs.length
        ? '<div class="gachs">' + achs.map((a) => '<div class="gach" title="' + esc(a.text || '') + '">'
          + '<span class="gicon">' + esc(a.icon || '🏅') + '</span><b>' + esc(a.title || a.key) + '</b>'
          + '<span class="muted small">' + esc(a.text || '') + '</span>'
          + (a.reward ? '<span class="chip">🏺 ' + a.reward + '</span>' : '') + '</div>').join('') + '</div>'
        : '<div class="gempty glek">' + (mine ? 'Ачівок ще нема. Вони приходять самі — за перемоги, серії й дрібні дурниці.' : 'Ачівок поки нема.') + '</div>')
      + '</section>';
  }

  // =============================================================================================
  // Публічне
  // =============================================================================================

  let shownKind = null;
  window.HPeople = {
    init(opts) {
      o = opts;
      if (o.esc) esc = o.esc;
    },
    hue, hueRaw, nickCls, badge, emo, ava, avaHtml, nickLink, dur, lbNum, shards,
    /// Куди веде кнопка «📊 Хто скільки»: на вкладку, де людина була востаннє.
    statsHash: () => '#stats/' + statsTab,
    show(kind, tail) {
      closeCard();
      shownKind = kind;
      if (kind === 'stats') renderStats(tail);
      else if (kind === 'who') renderWho(tail);
    },
    hide() {
      if (!shownKind) return;
      shownKind = null;
      token++;
    },
    card,
    closeCard,
    /// Щось змінилось у людях (онлайн, столи) — перемалювати рядок «де зараз» на відкритому профілі.
    refreshWhere() {
      if (shownKind !== 'who' || !whoNick) return;
      const el = document.querySelector('#who .wh-where');
      if (el) el.innerHTML = whereHtml(whoNick);
    },
  };
})();
