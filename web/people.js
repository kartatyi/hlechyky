/*
  Люди й цифри — window.HPeople.

  - Колір ніка й аватарка: сталий відтінок із хешу ніка (hue/ava). Той самий у балачках, «хто онлайн», черзі,
    таблицях і профілі — людину впізнаєш оком, не читаючи.
  - Картка людини: клік по будь-якому елементу з data-who="нік" (ловимо на всій сторінці). Хто, де зараз, трохи цифр
    і що з ним можна зробити: відкрити профіль, згадати в балачках, покликати за свій стіл, підсісти до нього.
  - Профіль #who/<нік>: і свій («Я» — з акаунтом і гаманцем по поличках), і будь-чий.
  - «📊 Хто скільки» #stats/<overview|music|games|time>: огляд, музика, ігри й час з ОДНИМ перемикачем періоду
    (день · тиждень · місяць · весь час). Таблиці ігор і «⏱ Час» переїхали сюди з розділу «Ігри». «✨ Огляд» (звання,
    хто кого, коли тусимо, перл, рідкісні ачівки), «🎮 Усі ігри», виконавці й слухачі та «✨ Цікавинки» в профілі
    рахує сервер — Litopys.cs, /api/stats/*.

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

  /// Для плиток: від десяти годин — без хвилин («67 год»), щоб велике число влазило в телефон.
  const durShort = (sec) => (sec >= 36000 ? Math.floor(sec / 3600) + ' год' : dur(sec));
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
  /// «12 345» — ціле з пробілами, як усі числа сайту.
  const num = (n) => Math.round(n || 0).toLocaleString('uk-UA');
  /// Відмінок за числом: plural(3, 'партія', 'партії', 'партій') → «партії».
  const plural = (n, one, few, many) => { const t = Math.abs(Math.round(n || 0)) % 100, u = t % 10; return t > 10 && t < 20 ? many : u === 1 ? one : u >= 2 && u <= 4 ? few : many; };
  /// «3 партії», «12 345 пісень».
  const cnt = (n, one, few, many) => num(n) + ' ' + plural(n, one, few, many);
  /// Велике число: до мільйона — з пробілами, далі — коротко («3,6 трлн»), як у таблицях.
  const big = (n) => (Math.abs(n || 0) < 1e6 ? num(n) : lbNum(n));

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

  const STATS_TABS = [['overview', '✨ Огляд'], ['music', '🎵 Музика'], ['games', '🎮 Ігри'], ['time', '⏱ Час']];
  /// Вкладки з інших файлів (stats-*.js, HPeople.statsTab): { key, label, run, after } — стають одразу за after.
  const extraTabs = [];
  /// Позначки біля назв вкладок (HPeople.statsBadge): ключ → html, напр. «нове».
  const tabBadges = new Map();
  /// Що ще малювати вгорі «✨ Огляду» (HPeople.overviewTop): fn(el, t) — сама ходить по дані й сама заповнює el.
  const overviewTops = [];
  function allTabs() {
    const list = STATS_TABS.map(([k, l]) => ({ key: k, label: l }));
    extraTabs.forEach((x) => {
      const i = list.findIndex((y) => y.key === x.after);
      list.splice(i < 0 ? list.length : i + 1, 0, x);
    });
    return list;
  }
  const knownTab = (k) => allTabs().some((x) => x.key === k);
  let statsTab = ls('statsTab', 'overview');
  let token = 0;                       // щоб запізніла відповідь не малювала поверх свіжішої вкладки
  const stale = (t) => t !== token;
  const cap = (s) => s[0].toUpperCase() + s.slice(1);

  function renderStats(tail) {
    const want = knownTab(tail) ? tail : knownTab(statsTab) ? statsTab : 'overview';
    if (want !== tail) history.replaceState(null, '', '#stats/' + want);
    statsTab = want;
    lsSet('statsTab', statsTab);
    if (statsTab === 'overview') lsSet('statsOverviewSeen', '1');
    // «нове» біля Огляду — доки людина хоч раз туди не зазирнула
    const fresh = ls('statsOverviewSeen', '') !== '1';
    const root = document.getElementById('stats');
    root.innerHTML = '<div class="sthead panel-lite">'
      + '<nav class="sttabs" id="statsTabs" aria-label="Що рахуємо">' + tabsHtml(fresh) + '</nav>'
      + periodSeg() + '</div><div class="stbody"></div>';
    wireTabs(root);
    root.querySelectorAll('.stper [data-p]').forEach((b) => b.onclick = () => {
      period = b.dataset.p;
      lsSet('statsPeriod', period);
      root.querySelectorAll('.stper [data-p]').forEach((x) => x.classList.toggle('on', x === b));
      drawStatsBody();
    });
    drawStatsBody();
  }
  function tabsHtml(fresh) {
    return allTabs().map(({ key: k, label: l }) =>
      '<button type="button" data-t="' + k + '"' + (k === statsTab ? ' class="on"' : '') + '>' + l
      + (tabBadges.get(k) || (k === 'overview' && fresh ? ' <span class="stnew">нове</span>' : '')) + '</button>').join('');
  }
  function wireTabs(root) {
    root.querySelectorAll('.sttabs [data-t]').forEach((b) => b.onclick = () => o.go('#stats/' + b.dataset.t));
  }
  /// Перемалювати лише рядок вкладок (змінились позначки), не чіпаючи відкритого вмісту.
  function repaintTabs() {
    const nav = document.getElementById('statsTabs');
    if (!nav || shownKind !== 'stats') return;
    nav.innerHTML = tabsHtml(ls('statsOverviewSeen', '') !== '1');
    wireTabs(nav.parentNode);
  }
  function drawStatsBody() {
    const body = document.querySelector('#stats .stbody');
    if (!body) return;
    const t = ++token;
    body.innerHTML = '<div class="gwait"><span class="spin"></span> рахую…</div>';
    const extra = extraTabs.find((x) => x.key === statsTab);
    const run = extra ? (b, tt) => Promise.resolve(extra.run(b, tt)) : { overview: statsOverview, games: statsGames, time: statsTime, music: statsMusic }[statsTab] || statsOverview;
    run(body, t).catch((e) => { if (!stale(t)) body.innerHTML = '<div class="gempty">Ой-йой, не порахувалось: ' + esc(e.message) + '</div>'; });
  }

  /// Кілька ніків одним рядком: «Оля», «Оля і Петро», «Оля, Петро і Яся».
  const nickList = (list, withAva) => {
    const one = (n) => (withAva ? '<span class="nk">' + ava(n, 'ava sm') + nickLink(n) + '</span>' : nickLink(n));
    const l = list || [];
    return l.length < 2 ? l.map(one).join('') : l.slice(0, -1).map(one).join('<span class="muted">, </span>') + '<span class="muted"> і </span>' + one(l[l.length - 1]);
  };
  /// Нік кольором, але без картки: усередині кнопки клік має робити своє, а не відкривати людину.
  const nickTxt = (n) => '<span class="who-t' + nickCls(n) + '" style="--h:' + hue(n) + '">' + esc(n) + '</span>';
  const medal = (i) => (i < 3 ? ['🥇', '🥈', '🥉'][i] : String(i + 1));

  // ---------- ✨ огляд: підсумок, звання, хто кого, коли тусимо, перл, рідкісні ачівки ----------
  const DOW = ['пн', 'вт', 'ср', 'чт', 'пт', 'сб', 'нд'];
  const DOW_LONG = ['понеділок', 'вівторок', 'середа', 'четвер', 'п\'ятниця', 'субота', 'неділя'];
  const DOW_IN = ['у понеділок', 'у вівторок', 'у середу', 'у четвер', 'у п\'ятницю', 'у суботу', 'у неділю'];
  const PERIOD_HEAD = { day: 'Сьогодні в Глечиках', week: 'Тиждень у Глечиках', month: 'Місяць у Глечиках', all: 'Глечики за весь час' };
  const PEARL_OF = { day: 'дня', week: 'тижня', month: 'місяця', all: 'усіх часів' };
  /// «опівночі», «о 7:00», «о 22:00».
  const atHour = (h) => (h === 0 ? 'опівночі' : 'о ' + h + ':00');

  /// Підпис стовпчика розкладки: «14:00», «пт, 3 жовт.», «тиждень з 29 вер.».
  function bucketLabel(unit, at) {
    if (unit === 'hour') return at + ':00';
    const d = new Date(at + 'T12:00:00');
    const s = d.toLocaleDateString('uk-UA', { day: 'numeric', month: 'short' });
    return unit === 'week' ? 'тиждень з ' + s : DOW[(d.getDay() + 6) % 7] + ', ' + s;
  }
  /// Міні-стовпчики під числом плитки: кожен — день (за день — година), висота від найбільшого.
  function spark(series, key, fmt) {
    const b = (series && series.buckets) || [];
    const max = Math.max(0, ...b.map((x) => x[key] || 0));
    if (b.length < 2 || !max) return '';
    return '<div class="ov-spark">' + b.map((x) => '<i' + (x[key] ? '' : ' class="z"') + ' style="height:' + Math.max(6, Math.round(100 * (x[key] || 0) / max)) + '%"'
      + ' title="' + esc(bucketLabel(series.unit, x.at) + ': ' + fmt(x[key] || 0)) + '"></i>').join('') + '</div>';
  }

  function ovTiles(d) {
    const t = d.totals || {};
    const s = d.series;
    const tile = (icon, big, label, sp) => '<div class="ov-tile"><span class="ov-ti" aria-hidden="true">' + icon + '</span><b>' + big + '</b><span class="ov-tl">' + label + '</span>' + (sp || '') + '</div>';
    const likes = (t.likes || 0) + (t.chatLikes || 0);
    return '<div class="ov-tiles">'
      + tile('🎲', num(t.rounds), plural(t.rounds, 'партія', 'партії', 'партій') + ' за столами' + (t.solo ? ' · ще ' + num(t.solo) + ' соло' : ''),
        spark(s, 'rounds', (v) => cnt(v, 'партія', 'партії', 'партій')))
      + tile('🎵', num(t.songs), plural(t.songs, 'пісню закинули', 'пісні закинули', 'пісень закинули'), spark(s, 'songs', (v) => cnt(v, 'пісня', 'пісні', 'пісень')))
      + tile('💬', num(t.messages), plural(t.messages, 'репліка', 'репліки', 'реплік') + ' в Балачках', spark(s, 'messages', (v) => cnt(v, 'репліка', 'репліки', 'реплік')))
      + tile('⏱', durShort(t.timeSec), 'уся тусня разом на сайті', s && s.unit !== 'hour' ? spark(s, 'timeSec', dur) : '')
      + tile('📻', durShort(t.listenSec), 'грало радіо в плеєрах')
      + tile('🏺', big(t.shards), plural(t.shards || 0, 'черепок', 'черепки', 'черепків') + ' накапало')
      + tile('👥', num(t.people), plural(t.people, 'людина заходила', 'людини заходили', 'людей заходило'))
      + tile('❤', num(likes), plural(likes, 'вподобайка', 'вподобайки', 'вподобайок') + ' — трекам і реплікам')
      + '</div>';
  }

  function titleCard(x) {
    const mine = (x.nicks || []).some((n) => same(n, o.me.nick));
    return '<div class="ovt' + (x.roast ? ' roast' : '') + (mine ? ' me' : '') + '">'
      + '<span class="ovt-ic" aria-hidden="true">' + esc(x.icon) + '</span>'
      + '<div class="ovt-b"><span class="ovt-name">' + esc(x.title) + (mine ? ' <span class="ovt-you">це ти!</span>' : '') + '</span>'
      + '<span class="ovt-who">' + nickList(x.nicks, true) + '</span>'
      + '<b class="ovt-val">' + esc(x.text) + '</b>'
      + '<span class="ovt-what">' + esc(x.what) + '</span>'
      + (x.second ? '<span class="ovt-next">далі ' + nickLink(x.second.nick) + ' — ' + esc(x.second.text) + '</span>' : '')
      + '</div></div>';
  }

  /// Одна пара «Хто кого»: лідер ліворуч, смужка ділиться кольорами ніків, під нею — де найчастіше стикаються.
  function rivalRow(r) {
    const tot = Math.max(1, r.aw + r.bw);
    const hot = Math.abs(r.aw - r.bw) <= 1 && r.aw + r.bw >= 6;
    const mine = same(r.a, o.me.nick) || same(r.b, o.me.nick);
    return '<div class="ovr' + (mine ? ' me' : '') + '">'
      + '<span class="ovr-a">' + ava(r.a, 'ava sm') + nickLink(r.a) + '</span>'
      + '<b class="ovr-s">' + r.aw + '<span class="muted"> : </span>' + r.bw + (hot ? ' <span title="рахунок майже рівний — тут гаряче">🔥</span>' : '') + '</b>'
      + '<span class="ovr-b">' + nickLink(r.b) + ava(r.b, 'ava sm') + '</span>'
      + '<div class="ovr-bar"><i style="width:' + pct(r.aw, tot) + ';--h:' + hue(r.a) + '"></i><i style="width:' + pct(r.bw, tot) + ';--h:' + hue(r.b) + '"></i></div>'
      + (r.games && r.games.length ? '<div class="ovr-g">' + r.games.map((g) => '<span>' + iconOf(g.game) + esc(g.title || titleOf(g.game)) + '</span>').join('') + '</div>' : '')
      + '</div>';
  }

  /// «🕐 Коли ми тусимо»: тиждень × доба, клітинка тим яскравіша, чим більше в ній дій (корінь — щоб і тихі години було видно).
  function heatHtml(h) {
    const cells = (h && h.cells) || [];
    const max = (h && h.max) || 0;
    if (!max || cells.length !== 7) return '<div class="gempty">Ще не видно, коли тут жваво: дій замало.</div>';
    const hours = Array.from({ length: 24 }, (_, i) => i);
    const head = '<span></span>' + hours.map((i) => '<span class="hh">' + (i % 3 ? '' : i) + '</span>').join('');
    const rows = cells.map((row, d) => '<span class="hd">' + DOW[d] + '</span>' + row.map((v, hr) => '<i' + (v ? '' : ' class="z"')
      + ' style="--a:' + (v ? (0.18 + 0.82 * Math.sqrt(v / max)).toFixed(3) : 0) + '"'
      + ' title="' + esc(DOW_LONG[d] + ', ' + hr + ':00–' + (hr + 1) + ':00 — ' + cnt(v, 'дія', 'дії', 'дій')) + '"></i>').join('')).join('');
    const byDow = cells.map((r) => r.reduce((s, v) => s + v, 0));
    const byHour = hours.map((i) => cells.reduce((s, r) => s + (r[i] || 0), 0));
    const dBest = byDow.indexOf(Math.max(...byDow));
    const hBest = byHour.indexOf(Math.max(...byHour));
    const quiet = byHour.indexOf(Math.min(...byHour));
    const p = h.peak || { dow: dBest, hour: hBest };
    return '<div class="heat" role="img" aria-label="Коли на сайті найжвавіше: дні тижня по годинах">' + head + rows + '</div>'
      + '<div class="heat-cap">Найгарячіше — <b>' + DOW_IN[p.dow] + ' ' + atHour(p.hour) + '</b>. Найлюдніший день — <b>' + DOW_LONG[dBest]
      + '</b>, година — <b>' + hBest + ':00</b>; найтихіше ' + atHour(quiet) + '.</div>';
  }

  function pearlHtml(p) {
    if (!p) return '<div class="gempty">' + cap(PERIOD_WORD[period]) + ' жодна репліка не зібрала ❤ — тяпайте смішніше 😉</div>';
    const f = p.file;
    const media = f && f.type === 'image' && f.url ? '<a class="pearl-a" href="' + esc(f.url) + '" target="_blank" rel="noopener"><img class="pearl-img" src="' + esc(f.url) + '" alt="' + esc(f.name || 'картинка') + '" loading="lazy" decoding="async"></a>'
      : f ? '<div class="muted small">📎 ' + esc(f.name || 'файл') + '</div>' : '';
    return '<figure class="pearl"><blockquote>' + (p.text ? '<p>' + esc(p.text) + '</p>' : '') + media + '</blockquote>'
      + '<figcaption>' + ava(p.nick, 'ava sm') + nickLink(p.nick) + '<span class="muted small">' + esc(o.dayTime(p.at)) + '</span>'
      + '<b class="pearl-l" title="' + esc('❤ від: ' + (p.likers || []).join(', ')) + '">❤ ' + p.likes + '</b></figcaption>'
      + (p.likers && p.likers.length ? '<div class="muted small pearl-by">❤ від: ' + p.likers.map((n) => nickLink(n)).join(', ') + '</div>' : '')
      + '</figure>';
  }

  function rareHtml(list) {
    if (!list || !list.length) return '<div class="gempty">Рідкісних ачівок поки нема — кожну мають щонайменше троє.</div>';
    return '<div class="ov-rare">' + list.map((a) => '<div class="ovra"><span class="gicon" aria-hidden="true">' + esc(a.icon || '🏅') + '</span>'
      + '<div class="ovra-b"><b>' + esc(a.title) + '</b><span class="muted small">' + esc(a.text || '') + '</span>'
      + '<span class="ovra-who">' + (a.holders.length === 1 ? '🦄 лише в ' : '🦄 лише у двох: ') + nickList(a.holders) + '</span></div></div>').join('') + '</div>';
  }

  async function statsOverview(body, t) {
    const g = G();
    const [d] = await Promise.all([o.api('GET', '/api/stats/overview?period=' + encodeURIComponent(period)), g && g.ready ? g.ready().catch(() => {}) : null]);
    if (stale(t)) return;
    const titles = d.titles || [];
    const good = titles.filter((x) => !x.roast);
    const roast = titles.filter((x) => x.roast);
    const since = d.period === 'all' ? (d.first ? 'з ' + sinceWord(d.first) : '') : d.period !== 'day' && d.since ? 'з ' + sinceWord(d.since) : '';
    const rivals = (d.rivals || []).length
      ? '<div class="ov-rivals">' + d.rivals.map(rivalRow).join('') + '</div>'
      : '<div class="gempty">' + cap(PERIOD_WORD[period]) + ' ніхто ще нікого не обіграв за одним столом. Гайда за стіл!</div>';
    body.innerHTML = '<div class="ov">' + (overviewTops.length ? '<div class="ov-top"></div>' : '')
      + '<section class="panel stbox ov-hero"><h3>' + esc(PERIOD_HEAD[period]) + (since ? ' <span class="muted small">' + esc(since) + '</span>' : '') + '</h3>' + ovTiles(d) + '</section>'
      + '<section class="panel stbox"><h3>🏅 Звання <span class="muted small">' + PERIOD_WORD[period] + '</span></h3>'
      + (good.length ? '<div class="ov-tgrid">' + good.map(titleCard).join('') + '</div>'
        : '<div class="gempty glek">' + cap(PERIOD_WORD[period]) + ' ще ніхто нічим не відзначився — звання чекають.</div>')
      + (roast.length ? '<h4 class="ov-roast-h">😏 А тепер — по-дружньому</h4><div class="ov-tgrid">' + roast.map(titleCard).join('') + '</div>' : '')
      + '</section>'
      + '<div class="ov-two">'
      + '<section class="panel stbox"><h3>⚔ Хто кого <span class="muted small">' + PERIOD_WORD[period] + '</span></h3>' + rivals
      + '<div class="muted small">Скільки разів один обіграв другого за одним столом; нічиї не рахуються.</div></section>'
      + '<section class="panel stbox"><h3>🕐 Коли ми тусимо <span class="muted small">' + PERIOD_WORD[period] + '</span></h3>' + heatHtml(d.heat)
      + '<div class="muted small">Партії, репліки, закинуті пісні й вподобайки — за київським часом.</div></section>'
      + '</div><div class="ov-two">'
      + '<section class="panel stbox"><h3>💎 Перл ' + PEARL_OF[period] + '</h3>' + pearlHtml(d.pearl) + '</section>'
      + '<section class="panel stbox"><h3>🦄 Рідкісні ачівки <span class="muted small">за весь час</span></h3>' + rareHtml(d.rare) + '</section>'
      + '</div></div>';
    // картинку з перла вже могли прибрати з диска (ChatFiles:MaxGb) — як у Балачках, кажемо про це словами
    const top = body.querySelector('.ov-top');
    overviewTops.forEach((fn) => {
      const el = document.createElement('div');
      top.appendChild(el);
      try { Promise.resolve(fn(el, t)).catch((e) => console.warn('ov-top', e)); } catch (e) { console.warn('ov-top', e); }
    });
    const img = body.querySelector('.pearl-img');
    if (img) img.onerror = () => { const s = document.createElement('div'); s.className = 'muted small'; s.textContent = '🗑 картинку вже прибрано'; img.closest('.pearl-a').replaceWith(s); };
  }

  // ---------- музика: хто закидає, виконавці, хто слухає, хто лайкає і що крутили ----------
  const ratingSort = () => ls('ratingSort', 'plays');
  /// Рядок «людина — смужка — число»: «Хто закидає», «Хто слухає», «Хто лайкає».
  const personBar = (x, i, max, val, shown, tip) => '<div class="stp' + (same(x.nick, o.me.nick) ? ' me' : '') + '"' + (tip ? ' title="' + esc(tip) + '"' : '') + '>'
    + '<span class="n">' + (i + 1) + '</span>' + ava(x.nick, 'ava sm') + nickLink(x.nick, 'stp-nick')
    + '<div class="stp-bar"><i style="width:' + Math.round(val / max * 100) + '%;--h:' + hue(x.nick) + '"></i></div>'
    + '<b>' + shown + '</b></div>';

  async function statsMusic(body, t) {
    const days = PERIOD_DAYS[period];
    const sort = ratingSort();
    const [top, rating, more] = await Promise.all([
      o.api('GET', '/api/top?period=' + period + '&days=' + days),
      o.api('GET', '/api/rating?period=' + period + '&days=' + days + '&sort=' + encodeURIComponent(sort)),
      o.api('GET', '/api/stats/music?period=' + period).catch(() => null),
    ]);
    if (stale(t)) return;
    // Автодиджей закидає за всіх, поки ніхто нічого не ставить, — у людському топі він лише заважав би. Старий
    // сервер віддає його серед людей, новий — окремим полем dj.
    const djName = o.dj();
    const people = (top.requesters || []).filter((x) => !same(x.nick, djName));
    const djRow = top.dj || (top.requesters || []).find((x) => same(x.nick, djName)) || null;
    const max = Math.max(1, ...people.map((x) => x.count));
    const peopleHtml = people.length
      ? '<div class="stpeople">' + people.map((x, i) => personBar(x, i, max, x.count, x.count)).join('') + '</div>'
      : '<div class="gempty glek">' + cap(PERIOD_WORD[period]) + ' ніхто нічого не закидав — усе крутив ' + esc(djName) + '.</div>';
    const djLine = djRow && djRow.count ? '<div class="muted small stdj">🏺 А ' + esc(djName) + ' ' + PERIOD_WORD[period] + ' поставив сам ' + djRow.count + ' — коли черга порожніла.</div>' : '';

    const artists = (more && more.artists) || [];
    const amax = Math.max(1, ...artists.map((x) => x.n));
    const artistsHtml = artists.length
      ? '<div class="starts">' + artists.map((a, i) => '<div class="sta"><span class="n">' + (i + 1) + '</span>'
        + '<div class="sta-m"><b class="sta-name" title="' + esc(a.artist) + '">' + esc(a.artist) + '</b>'
        + '<span class="sta-fans">' + a.fans.map((f) => nickLink(f.nick) + ' <span class="muted">' + f.n + '</span>').join('<span class="muted"> · </span>')
        + (a.people > a.fans.length ? '<span class="muted"> · ще ' + (a.people - a.fans.length) + '</span>' : '') + '</span></div>'
        + '<div class="stp-bar"><i style="width:' + Math.round(a.n / amax * 100) + '%;--h:44"></i></div><b>' + a.n + '</b></div>').join('') + '</div>'
      : '<div class="gempty">' + cap(PERIOD_WORD[period]) + ' нікого не закидали — лише Глек сам собі діджей.</div>';
    const listeners = (more && more.listeners) || [];
    const lmax = Math.max(1, ...listeners.map((x) => x.sec || x.tracks));
    const listenHtml = listeners.length
      ? '<div class="stpeople long">' + listeners.map((x, i) => personBar(x, i, lmax, x.sec || x.tracks, x.sec ? dur(x.sec) : cnt(x.tracks, 'трек', 'треки', 'треків'),
        x.tracks ? 'застав ' + cnt(x.tracks, 'трек', 'треки', 'треків') : '')).join('') + '</div>'
      : '<div class="gempty">' + cap(PERIOD_WORD[period]) + ' плеєр ні в кого не грав.</div>';
    const likers = (more && more.likers) || [];
    const kmax = Math.max(1, ...likers.map((x) => x.n));
    const likersHtml = likers.length
      ? '<div class="stpeople">' + likers.map((x, i) => personBar(x, i, kmax, x.n, x.n)).join('') + '</div>'
      : '<div class="gempty">' + cap(PERIOD_WORD[period]) + ' — жодної ❤ трекам.</div>';

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
      + '<div class="stcol">'
      + '<section class="panel stbox"><h3>🙋 Хто закидає <span class="muted small">' + PERIOD_WORD[period] + '</span></h3>' + peopleHtml + djLine + '</section>'
      + (more ? '<section class="panel stbox"><h3>🎤 Виконавці <span class="muted small">кого закидали</span></h3>' + artistsHtml + '</section>'
        + '<section class="panel stbox"><h3>🎧 Хто слухає <span class="muted small">поки грав плеєр</span></h3>' + listenHtml + '</section>'
        + '<section class="panel stbox"><h3>❤ Хто лайкає <span class="muted small">треки</span></h3>' + likersHtml + '</section>' : '')
      + '</div>'
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

  // ---------- ігри: «Усі ігри» і таблиця гри ----------
  // ключі — як їх називає Leaderboards.cs: rated → elo/wins/losses/draws/games/streak,
  // solo → best/tries, daily → attempts/ms, shards → balance/earned
  const LB_COLS = [['elo', 'Ело'], ['wins', 'В'], ['losses', 'П'], ['draws', 'Н'], ['games', 'партій'],
    ['streak', 'серія'], ['score', 'результат'], ['best', 'рекорд'], ['attempts', 'спроб'],
    ['tries', 'спроб'], ['ms', 'час'], ['balance', '🏺 є зараз'], ['earned', 'зароблено'], ['count', 'разів']];
  const LB_TIPS = { elo: 'рейтинг Ело', wins: 'перемог', losses: 'поразок', draws: 'нічиїх', balance: 'скільки черепків у глечику зараз' };
  let lbGame = ls('gamesLbGame', 'shards');
  let boardToken = 0;
  const asList = (r) => (Array.isArray(r) ? r : (r && (r.rows || r.top || r.items || r.list)) || []);
  const GROUP_TITLES = { board: '♟ Настільні', live: '⚡ Швидкі', party: '🎉 Компанія', solo: '🏺 Соло' };

  /// Час у мілісекундах: «9,5 с» до хвилини, далі «1:19,8».
  function msText(ms) {
    const v = Math.max(0, Math.round(ms || 0));
    if (v < 60000) return (v / 1000).toLocaleString('uk-UA', { minimumFractionDigits: 1, maximumFractionDigits: 1 }) + ' с';
    return Math.floor(v / 60000) + ':' + String(Math.floor(v % 60000 / 1000)).padStart(2, '0') + ',' + Math.floor(v % 1000 / 100);
  }
  /// Що значить «рекорд» у кожній соло-грі (сервер пише голе число): мілісекунди, секунди, метри, очки, спроби…
  const SCORE_FMT = {
    'mines-daily': msText, 'chess-daily': msText, 'bricks-daily': msText,
    'bricks-sprint': (s) => msText(s * 1000),
    clicker: (n) => (n < 1e15 ? lbNum(n) + ' ' + plural(Math.trunc(n), 'глек', 'глеки', 'глеків') : lbNum(n)),
    'dino-daily': (n) => num(n) + ' м',
    'geo-solo': (n) => cnt(n, 'очко', 'очки', 'очок'),
    'geo-daily': (n) => cnt(n, 'очко', 'очки', 'очок'),
    roulette: (n) => '+' + shards(Math.round(n)),
    'roulette-solo': (n) => '+' + shards(Math.round(n)),
    'snake-coop': (n) => 'довжина ' + num(n),
    'typerace-solo': (n) => num(n) + ' зн/хв',
    wordle: (n) => 'за ' + tries(Math.round(n)),
  };
  /// Щоденні ігри, що пишуть у загальну таблицю нуль-заглушку: їхні очки живуть у власній денній таблиці.
  const NO_SCORE = new Set(['geese-daily', 'tyr-daily', 'skilky-daily']);
  const scoreText = (game, v) => (v == null ? '—' : SCORE_FMT[game] ? SCORE_FMT[game](v) : String(lbNum(v)));
  /// «спроб» у щоденних — це дні (одна спроба на день); у Колі — збереження, яких ніхто не рахує.
  const triesLabel = (game) => (game === 'clicker' ? null : /-daily$/.test(game) || game === 'wordle' ? 'днів' : 'спроб');

  /// Відсоток перемог зі смужкою.
  const winPct = (w, n) => {
    if (!n) return '<span class="muted">—</span>';
    const p = Math.round(100 * w / n);
    return '<span class="lbt-pct" title="' + esc(w + ' перемог з ' + n) + '"><i style="width:' + p + '%"></i><span>' + p + ' %</span></span>';
  };

  async function statsGames(body, t) {
    const g = G();
    if (g && g.ready) await g.ready().catch(() => {});
    if (stale(t)) return;
    const games = (g && g.catalog && g.catalog.games) || [];
    if (lbGame !== 'shards' && !games.some((x) => x.id === lbGame)) lbGame = 'shards';
    const groups = ['board', 'live', 'party', 'solo'].map((k) => [k, games.filter((x) => x.group === k)]).filter(([, l]) => l.length);
    body.innerHTML = '<div class="stgrid">'
      + '<section class="panel stbox glall-box"><h3>🎮 Усі ігри <span class="muted small">' + PERIOD_WORD[period] + '</span></h3>'
      + '<div class="glall"><div class="gwait"><span class="spin"></span> рахую…</div></div></section>'
      + '<section class="panel stbox wide glsel"><div class="glbbar">'
      + '<select class="glbgame" aria-label="Яка таблиця"><option value="shards"' + (lbGame === 'shards' ? ' selected' : '') + '>🏺 Черепки — хто скільки заробив</option>'
      + groups.map(([k, list]) => '<optgroup label="' + esc(GROUP_TITLES[k] || k) + '">' + list.map((x) =>
        '<option value="' + esc(x.id) + '"' + (x.id === lbGame ? ' selected' : '') + '>' + esc(x.title) + '</option>').join('') + '</optgroup>').join('')
      + '</select></div><div class="glbnote muted small"></div><div class="glbbox"></div><div class="glseason"></div></section>'
      + '</div>';
    const sel = body.querySelector('.glbgame');
    const pick = (id, scroll) => {
      if (![...sel.options].some((x) => x.value === id)) return;
      lbGame = id;
      sel.value = id;
      lsSet('gamesLbGame', lbGame);
      body.querySelectorAll('.gla').forEach((b) => b.classList.toggle('on', b.dataset.g === id));
      drawBoard(body);
      // таблиця не на виду (довгий список прогорнули, вузький екран) — їдемо до неї
      const box = body.querySelector('.glsel');
      const top = box.getBoundingClientRect().top;
      if (scroll && (top < 0 || top > window.innerHeight * 0.6)) box.scrollIntoView({ behavior: 'smooth', block: 'start' });
    };
    sel.onchange = () => pick(sel.value, false);
    drawBoard(body);
    allGames(body, t, pick);
  }

  /// «🎮 Усі ігри»: кожна гра за період — скільки партій, скільки людей, хто чемпіон; клік — її таблиця.
  async function allGames(body, t, pick) {
    const box = body.querySelector('.glall');
    const r = await o.api('GET', '/api/stats/games?period=' + encodeURIComponent(period)).catch(() => null);
    if (stale(t) || !box || !box.isConnected) return;
    const list = (r && r.games) || [];
    if (!list.length) { box.innerHTML = '<div class="gempty">' + cap(PERIOD_WORD[period]) + ' ще ні в що не грали. Гайда за стіл!</div>'; return; }
    const max = Math.max(1, ...list.map((x) => x.rounds));
    box.innerHTML = '<div class="muted small">' + cnt(r.rounds, 'партія', 'партії', 'партій') + ' у ' + cnt(list.length, 'грі', 'іграх', 'іграх') + ' · натисни — покажу таблицю</div>'
      + list.map((x) => {
        const champ = x.champ ? '🏆 ' + nickTxt(x.champ.nick) + ' <span class="muted">×' + x.champ.wins + '</span>'
          : x.best && !NO_SCORE.has(x.game) ? '🥇 ' + nickTxt(x.best.nick) + ' <span class="muted">' + esc(scoreText(x.game, x.best.score)) + '</span>'
            : '🎲 ' + nickTxt(x.top.nick) + ' <span class="muted">×' + x.top.n + '</span>';
        return '<button type="button" class="gla' + (x.game === lbGame ? ' on' : '') + '" data-g="' + esc(x.game) + '">'
          + '<span class="gla-t">' + iconOf(x.game) + esc(x.title || titleOf(x.game)) + '</span>'
          + '<span class="gla-n">' + cnt(x.rounds, x.solo ? 'захід' : 'партія', x.solo ? 'заходи' : 'партії', x.solo ? 'заходів' : 'партій') + ' · 👥 ' + x.players + '</span>'
          + '<span class="gla-c">' + champ + '</span>'
          + '<span class="gla-bar"><i style="width:' + pct(x.rounds, max) + '"></i></span></button>';
      }).join('');
    box.querySelectorAll('.gla').forEach((b) => b.onclick = () => pick(b.dataset.g, true));
  }

  /// Таблиця вибраної гри — справжня таблиця, щоб стовпці стояли рівно в кожному рядку.
  async function drawBoard(body) {
    const bt = ++boardToken;
    const box = body.querySelector('.glbbox');
    const note = body.querySelector('.glbnote');
    const season = body.querySelector('.glseason');
    if (!box) return;
    const game = lbGame;
    box.innerHTML = '<div class="gwait"><span class="spin"></span> рахую…</div>';
    note.textContent = '';
    season.innerHTML = '';
    if (game === 'dice') seasonDice(season, bt);   // «Під глеком»: смішні звання партій за період (прохід №3, №130)
    const r = await o.api('GET', '/api/games/leaderboard?game=' + encodeURIComponent(game) + '&period=' + encodeURIComponent(period)).catch((e) => ({ error: e }));
    if (bt !== boardToken || !box.isConnected) return;
    if (r && r.error) { box.innerHTML = '<div class="gempty">Ой-йой, не порахувалось: ' + esc(r.error.message) + '</div>'; return; }
    const rows = asList(r);
    const kind = r && r.kind;
    const notes = [];
    if (kind === 'rated') notes.push('Ело й рахунок — за весь час: рейтинг періоду не має.');
    if (kind === 'solo' && r.order === 'lower' && !NO_SCORE.has(game)) notes.push('⬇ Тут менше — краще.');
    if (NO_SCORE.has(game)) notes.push('Очки цієї щоденної гри — у її власній таблиці дня; тут — скільки днів грали.');
    note.textContent = notes.join(' ');
    if (!rows.length) { box.innerHTML = '<div class="gempty">' + cap(PERIOD_WORD[period]) + ' тут ще ніхто не відзначився.</div>'; return; }
    const games = (x) => (x.games != null ? x.games : (x.wins || 0) + (x.losses || 0) + (x.draws || 0));
    // [заголовок, клітинка, підказка, клас: opt — ховаємо на телефоні, main — головне число]
    let cols;
    if (kind === 'shards') {
      cols = [['🏺 є зараз', (x) => lbNum(x.balance), LB_TIPS.balance, period === 'all' ? 'main' : ''],
        ['зароблено ' + PERIOD_WORD[period], (x) => lbNum(x.earned), 'черепки, що прийшли за період (без обміну на гривні)', period === 'all' ? '' : 'main']];
    } else if (kind === 'rated') {
      cols = [['Ело', (x) => x.elo, LB_TIPS.elo, 'main'], ['партій', (x) => games(x), 'усього партій', 'opt'],
        ['В', (x) => x.wins, LB_TIPS.wins, 'opt'], ['Н', (x) => x.draws, LB_TIPS.draws, 'opt'], ['П', (x) => x.losses, LB_TIPS.losses, 'opt'],
        ['% перемог', (x) => winPct(x.wins, games(x)), 'частка перемог', 'pct'],
        ['серія', (x) => (x.streak >= 2 ? '🔥 ' + x.streak : x.streak || '—'), 'перемог поспіль просто зараз', 'opt']];
    } else if (kind === 'wins') {
      cols = [['В', (x) => x.wins, LB_TIPS.wins, 'main'], ['Н', (x) => x.draws, LB_TIPS.draws, 'opt'], ['П', (x) => x.losses, LB_TIPS.losses, 'opt'],
        ['партій', (x) => games(x), 'усього партій', ''], ['% перемог', (x) => winPct(x.wins, games(x)), 'частка перемог', 'pct']];
    } else if (kind === 'solo') {
      const tl = triesLabel(game);
      cols = (NO_SCORE.has(game) ? [] : [['рекорд', (x) => esc(scoreText(game, x.best)), r.order === 'lower' ? 'найкраще — найменше' : 'найкращий результат', 'main']])
        .concat(tl ? [[tl, (x) => num(x.tries), tl === 'днів' ? 'скільки днів грали' : 'скільки разів пробували', NO_SCORE.has(game) ? 'main' : '']] : []);
    } else {
      cols = LB_COLS.filter(([k]) => rows.some((x) => x[k] != null))
        .map(([k, l]) => [k === 'earned' ? 'зароблено ' + PERIOD_WORD[period] : l, (x) => esc(k === 'ms' ? secs(x[k]) : (x[k] == null ? '—' : lbNum(x[k]))), LB_TIPS[k] || '', '']);
    }
    box.innerHTML = '<div class="lbt-wrap"><table class="lbt"><thead><tr><th class="n">#</th><th class="who">хто</th>'
      + cols.map(([h, , tip, cls]) => '<th' + (cls ? ' class="' + cls + '"' : '') + (tip ? ' title="' + esc(tip) + '"' : '') + '>' + esc(h) + '</th>').join('') + '</tr></thead><tbody>'
      + rows.map((x, i) => '<tr class="' + (i < 3 ? 'top' + (i + 1) : '') + (same(x.nick, o.me.nick) ? ' me' : '') + '"><td class="n">' + medal(i) + '</td>'
        + '<td class="who"><span class="glb-who">' + ava(x.nick || '', 'ava sm') + nickLink(x.nick || '') + '</span></td>'
        + cols.map(([, cell, , cls]) => '<td' + (cls ? ' class="' + cls + '"' : '') + '>' + cell(x) + '</td>').join('') + '</tr>').join('')
      + '</tbody></table></div>';
  }

  /// «Блефер сезону» — звання партій «Під глеком» (сервер: /api/games/dice/season). «Сьогодні» рахуємо як тиждень.
  async function seasonDice(box, bt) {
    const d = await o.api('GET', '/api/games/dice/season?period=' + (period === 'month' || period === 'all' ? period : 'week')).catch(() => null);
    const titles = (d && d.titles) || [];
    if (bt !== boardToken || !titles.length || !box.isConnected) return;
    box.innerHTML = '<h4>🏆 Сезон під глеком — ' + esc(d.period === 'all' ? 'за весь час' : d.period === 'month' ? 'за 30 днів' : 'за 7 днів') + '</h4>'
      + '<div class="lbt-wrap"><table class="lbt season"><thead><tr><th class="t">звання</th><th class="who">хто</th><th class="main">скільки</th></tr></thead><tbody>'
      + titles.map((x) => x.rows.map((r, i) => '<tr class="' + (i ? '' : 'first') + (same(r.nick, o.me.nick) ? ' me' : '') + '">'
        + (i ? '' : '<td class="t" rowspan="' + x.rows.length + '">' + esc(x.icon + ' ' + x.title) + '</td>')
        + '<td class="who"><span class="glb-who">' + ava(r.nick, 'ava sm') + nickLink(r.nick) + '</span></td><td class="main">×' + r.n + '</td></tr>').join('')).join('')
      + '</tbody></table></div>';
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
    const [p, ppl, led, wk, anth, fun] = await Promise.all([
      opt(o.api('GET', '/api/games/profile?nick=' + encodeURIComponent(nick))),
      opt(o.api('GET', '/api/people/' + encodeURIComponent(nick))),
      mine ? opt(o.api('GET', '/api/games/ledger?limit=15')) : null,
      opt(o.api('GET', '/api/games/time?period=week')),
      // 🎺 гімн переможця з Лавки: 404 — гімну нема (або нічого не вдягнуто), рядка теж нема
      opt(o.api('GET', '/api/lavka/anthem/of/' + encodeURIComponent(nick))),
      // ✨ цікавинки (Litopys.cs): 404 — людини ніде не бачили, картки нема
      opt(o.api('GET', '/api/stats/person/' + encodeURIComponent(nick))),
      g && g.ready ? g.ready().catch(() => {}) : null,
    ]);
    if (stale(t) || whoNick !== nick) return;
    if (p) cache.set(nick.toLowerCase(), { at: Date.now(), data: p });
    const week = wk && (wk.people || []).find((x) => same(x.nick, nick));
    const known = !!(p && ((p.wallet && (p.wallet.earned || p.wallet.balance)) || (p.achievements || []).length || p.time || (p.recent || []).length))
      || !!(ppl && ppl.music && (ppl.music.requests.all || ppl.music.likes.count)) || !!fun;
    root.innerHTML = headHtml(nick, mine, ppl, anth)
      + (known || mine ? '<div class="who-grid">'
        + funCard(fun, mine)
        + walletCard(p, led, mine)
        + timeCard(p, week)
        + musicCard(ppl, nick, mine)
        + gamesCard(p, mine)
        + achCard(p, mine, fun)
        + '</div>'
        : '<section class="panel"><div class="gempty glek">Про ' + esc(nick) + ' тут поки нічого не знають — ні пісень, ні ігор. Схоже, усе ще попереду.</div></section>');
    wireHead(root, nick, mine);
    o.wireRows(root);
    root.querySelectorAll('[data-go]').forEach((b) => b.onclick = () => o.go(b.dataset.go));
    if (window.HBuy) HBuy.wireTrade(root);
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
    // Купити / продати за гривні (адміну ще «Заявки») — великим рядом під балансом, а не дрібно в заголовку
    if (mine && o.me.account && window.HBuy) body += HBuy.tradeHtml();
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
      ? ' <button type="button" class="ghost wc-more" data-go="#lavka">🛍 Лавка →</button>'
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

  /// Стовпчики «коли буває»: 24 години або 7 днів, найвищий — підсвічений.
  function whenBars(vals, labels, every, tip) {
    const max = Math.max(0, ...vals);
    if (!max) return '';
    return '<div class="fun-bars" style="--n:' + vals.length + '">' + vals.map((v, i) => '<span class="fb' + (v === max ? ' top' : '') + '" title="' + esc(tip(i, v)) + '">'
      + '<i style="height:' + Math.max(v ? 8 : 0, Math.round(100 * v / max)) + '%"></i><em>' + (i % every ? '' : labels[i]) + '</em></span>').join('') + '</div>';
  }

  /// «✨ Цікавинки»: з якого дня тут, улюблена гра, кривдник і жертва, коли буває, суперники, рідкісні ачівки
  /// (сервер: /api/stats/person/<нік>, Litopys.cs). Нічого не знаємо — картки нема.
  function funCard(f, mine) {
    if (!f) return '';
    const t = f.totals || {};
    const dec = (t.wins || 0) + (t.losses || 0) + (t.draws || 0);
    const facts = [];
    const fact = (icon, label, val, sub) => facts.push('<div class="fun-f"><span class="fun-i" aria-hidden="true">' + icon + '</span>'
      + '<span class="fun-l">' + label + '</span><b>' + val + '</b>' + (sub ? '<span class="fun-s">' + sub + '</span>' : '') + '</div>');
    if (f.first) fact('🗓', 'на сайті', 'з ' + esc(sinceWord(f.first)));
    if (f.fav) fact('💘', 'улюблена гра', iconOf(f.fav.game) + esc(f.fav.title || titleOf(f.fav.game)),
      f.fav.sec ? dur(f.fav.sec) + ' у грі' : cnt(f.fav.n, 'партія', 'партії', 'партій'));
    if (dec) fact('🎯', 'перемог за столами', Math.round(100 * t.wins / dec) + ' %', t.wins + ' з ' + cnt(dec, 'партії', 'партій', 'партій'));
    if (f.streak >= 2) fact('🔥', 'найдовша серія', cnt(f.streak, 'перемога', 'перемоги', 'перемог'), 'поспіль, без жодної поразки');
    if (f.nemesis) fact('😈', mine ? 'твій кривдник' : 'кривдник', nickLink(f.nemesis.nick), 'рахунок ' + f.nemesis.w + ' : ' + f.nemesis.l);
    if (f.victim) fact('🍖', mine ? 'твоя улюблена жертва' : 'улюблена жертва', nickLink(f.victim.nick), 'рахунок ' + f.victim.w + ' : ' + f.victim.l);
    if (f.artist) fact('🎤', 'улюблений виконавець', esc(f.artist.artist), 'закинуто ' + razy(f.artist.n));
    const hours = f.hours || [];
    const dows = f.dows || [];
    const hMax = Math.max(0, ...hours);
    const dMax = Math.max(0, ...dows);
    if (hMax) fact('🕐', 'найчастіше тут', DOW_IN[dows.indexOf(dMax)], 'найжвавіша година — ' + hours.indexOf(hMax) + ':00');
    if (t.messages) fact('💬', 'реплік у Балачках', num(t.messages), t.chatLikes ? '❤ від інших: ' + t.chatLikes : '');
    if (t.songs) fact('🎵', 'пісень закинуто', num(t.songs), t.likes ? '❤ трекам: ' + t.likes : '');
    if (t.listenSec >= 60) fact('🎧', 'грало радіо', dur(t.listenSec), '');
    if (t.games) fact('🎮', 'різних ігор', num(t.games), cnt(t.rounds, 'партія', 'партії', 'партій') + (t.solo ? ', з них ' + num(t.solo) + ' соло' : ''));
    const rivals = f.rivals || [];
    const titles = f.titles || [];
    const rare = f.rare || [];
    const when = hMax ? '<div class="fun-when"><div><h4>Коли буває <span class="muted small">години</span></h4>'
      + whenBars(hours, hours.map((_, i) => i), 6, (i, v) => i + ':00–' + (i + 1) + ':00 — ' + cnt(v, 'дія', 'дії', 'дій')) + '</div>'
      + '<div><h4>&nbsp;<span class="muted small">дні тижня</span></h4>' + whenBars(dows, DOW, 1, (i, v) => DOW_LONG[i] + ' — ' + cnt(v, 'дія', 'дії', 'дій')) + '</div></div>' : '';
    return '<section class="panel wcard wide fun"><h3>✨ Цікавинки <span class="muted small">за весь час</span></h3>'
      + (titles.length ? '<div class="fun-titles"><span class="muted small">звання цього тижня:</span>' + titles.map((x) => '<span class="chip' + (x.roast ? ' roast' : '') + '" title="' + esc(x.text) + '">'
        + esc(x.icon + ' ' + x.title) + '</span>').join('') + '<button type="button" class="ghost wc-more" data-go="#stats/overview">усі звання →</button></div>' : '')
      + (facts.length ? '<div class="fun-grid">' + facts.join('') + '</div>' : '')
      + when
      + (rivals.length ? '<h4>⚔ Суперники <span class="muted small">хто кого скільки разів обіграв</span></h4><div class="fun-rivals">' + rivals.map((r) => {
        const n = Math.max(1, r.w + r.l);
        return '<div class="fun-r">' + ava(r.nick, 'ava sm') + nickLink(r.nick) + '<b>' + r.w + '<span class="muted"> : </span>' + r.l + '</b>'
          + '<div class="ovr-bar"><i style="width:' + pct(r.w, n) + ';--h:' + hue(f.nick) + '"></i><i style="width:' + pct(r.l, n) + ';--h:' + hue(r.nick) + '"></i></div></div>';
      }).join('') + '</div>' : '')
      + (rare.length ? '<h4>🦄 Рідкісні ачівки</h4><div class="fun-rare">' + rare.map((a) => '<span class="chip">' + esc(a.icon + ' ' + a.title) + ' <span class="muted">· '
        + (a.holders === 1 ? (mine ? 'лише в тебе' : 'більше ні в кого') : a.holders === 2 ? 'лише у двох' : 'лише в трьох') + '</span></span>').join('') + '</div>' : '')
      + '</section>';
  }

  function achCard(p, mine, fun) {
    const achs = (p && p.achievements) || [];
    const rare = new Map(((fun && fun.rare) || []).map((x) => [x.key, x.holders]));
    return '<section class="panel wcard wide"><h3>🏅 Ачівки <span class="muted small">' + achs.length + '</span></h3>'
      + (achs.length
        ? '<div class="gachs">' + achs.map((a) => '<div class="gach" title="' + esc(a.text || '') + '">'
          + '<span class="gicon">' + esc(a.icon || '🏅') + '</span><b>' + esc(a.title || a.key) + '</b>'
          + '<span class="muted small">' + esc(a.text || '') + '</span>'
          + (a.reward ? '<span class="chip">🏺 ' + a.reward + '</span>' : '')
          + (rare.has(a.key) ? '<span class="chip rare" title="таку ачівку мають лише ' + rare.get(a.key) + '">🦄 рідкісна</span>' : '') + '</div>').join('') + '</div>'
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
    /// Помічники для stats-*.js — ті самі, що малюють «Хто скільки» тут, щоб ніки, числа й періоди були однакові.
    kit: {
      api: (m, u, b) => o.api(m, u, b), me: () => o.me, go: (h) => o.go(h),
      esc: (s) => esc(s), same, ls, lsSet, hue, ava, nickLink, nickTxt, nickList, medal, cap,
      num, plural, cnt, big, dur, durShort, shards, lbNum, iconOf, titleOf, bucketLabel, spark,
      period: () => period, PERIOD_WORD, PERIOD_HEAD, DOW, DOW_LONG, DOW_IN, atHour,
      /// Відповідь запізнилась — вкладку вже перемкнули чи період змінили: не малювати.
      stale: (t) => stale(t),
    },
    /// Нова вкладка «Хто скільки»: run(body, t) малює в body (t — для kit.stale). after — за якою стати.
    statsTab(key, label, run, after) {
      if (knownTab(key)) return;
      extraTabs.push({ key, label, run, after: after || 'overview' });
      repaintTabs();
    },
    /// Позначка біля вкладки (html, напр. ' <span class="stnew">нове</span>'); null — прибрати.
    statsBadge(key, html) {
      if (html) tabBadges.set(key, html); else tabBadges.delete(key);
      repaintTabs();
    },
    /// Блок угорі «✨ Огляду»: fn(el, t) сам ходить по дані й заповнює el.
    overviewTop(fn) { overviewTops.push(fn); },
    /// Чи відкрито зараз «Хто скільки» і яку вкладку.
    statsShown: () => (shownKind === 'stats' ? statsTab : null),
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
