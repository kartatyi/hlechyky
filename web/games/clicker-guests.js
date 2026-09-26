/*
  Заморські гості Гончарного кола — «Гостинний двір» (десяте оновлення, docs/games/specs/clicker-v10.md §7; як зроблено —
  docs/games/specs/clicker-v10-c.md). Частина ядра clicker.js.

  Розділ «🏛 Гостинний двір» угорі вкладки «🤝 Село» (саму вкладку робить цех, clicker-guild.js); видно з першим гостем:
  1) замовлення — хто, що хоче, скільки вже є в коморі, скільки заплатить і скільки шани, скільки ще чекатиме;
     «Віддати» — guests { do: 'give', id }, «Відпустити» (з підтвердженням другим натиском) — guests { do: 'skip', id };
  2) картки гостей — емодзі, хто, що любить, рівень шани зі смужкою, що дасть наступний рівень, пільга просто зараз;
  3) хто в дорозі далі — і з яким щаблем драбини приїде.
  Нове замовлення — нотатка на ярлику «Село», звук і гість на сцені (api.sceneGuest малює пакет сцени; без неї — нічого).
  Новий гість — тост, рядок у стрічці, іскри (раз: пам'ятаємо в localStorage, тож F5 не повторює).
  Правила рахує сервер (Impl/ClickerGuests.cs); тут лише малюнок і дії.
  Дані: свій вид — view.guests = null | { list: [{key,level,pts,need,at}], orders: [{id,guest,who,ware,style,q,n,have,pay,
    mult,rep,until}], next, slots, allMult, delivered, map }; тексти — st.catalog.guests.
  Звуки: guest (нове замовлення), rare (прибув гість), deal + coin (віддав), rep-up (шана виросла), tap і soft (відпустив).
*/
(() => {
  /// Натиск на джойстику — теж людина (див. clicker.js): скрізь, де потрібна справжня рука, — human(), а не isTrusted.
  const human = HGames.ui.human;

  const SEEN_KEY = 'clk.guests.met';
  const FRESH_MS = 30 * 60 * 1000;        // прибуття, давніше за пів години, — уже не новина (інший пристрій, F5 наступного дня)
  const REACT_MS = 4500;                  // скільки висить репліка гостя після «Віддати»
  const ARM_MS = 3000;                    // «Відпустити» питає вдруге — стільки чекає на підтвердження
  /// Скільки нове замовлення «припливає» (довжина анімації clkdsail): довше клас тримати не можна — кожна перемальовка
  /// двору (а вид приходить щопачки кліків) пускала б анімацію наново, і картка смикалась би.
  const FRESH_CARD_MS = 700;
  /// Якість, якої просять, — у множині: «(добрі й кращі · «Косівська»)».
  const Q_REQ = ['', '', 'добрі й кращі', 'дзвінкі й кращі', 'лише розкішні'];
  const PERK_ICON = { value: '🧺', golden: '🎨', pay: '🤝', fair: '🎪', offline: '🌙', fall: '🤲' };

  // ---------- дрібниці ----------

  const cat = (st) => (st.catalog && st.catalog.guests) || null;
  const def = (st, key) => { const c = cat(st); return (c && c.list.find((g) => g.key === key)) || null; };
  const wareName = (st, key) => {
    const list = (st.craft && st.craft.wares) || (st.catalog && st.catalog.wares) || [];
    const w = list.find((x) => x.key === key);
    return w ? w.name : key;
  };
  const styleName = (st, key) => {
    const s = (st.styleList || []).find((x) => x.key === key) || ((st.catalog && st.catalog.styles) || []).find((x) => x.key === key);
    return s ? s.name : key;
  };
  const ms = (t) => { const x = Date.parse(t); return Number.isFinite(x) ? x : 0; };
  const myNick = (st) => (st.ctx && st.ctx.me && st.ctx.me.nick) || '';

  /// «4 год 12 хв», «12:04» — гість чекає годинами, тож лише хвилини тут не годяться.
  function left(api, msLeft) {
    if (msLeft <= 0) return '0:00';
    const m = Math.floor(msLeft / 60000);
    if (m >= 60) return Math.floor(m / 60) + ' год ' + (m % 60) + ' хв';
    return api.mmss(msLeft);
  }

  /// Пільга гостя на цьому рівні — словами й числом: «вироби +15 %», «коло без тебе +1 год 40 хв».
  function perkNow(api, d, level) {
    const x = d.per * level;
    const pct = api.dec(x * 100) + ' %';
    switch (d.kind) {
      case 'value': return 'вироби дорожчі на ' + pct;
      case 'golden': return 'розписний глек на ' + pct + ' швидше';
      case 'pay': return 'купці й села платять +' + pct;
      case 'fair': return 'ярмарок +' + api.dec(x) + ' до множника';
      case 'offline': {
        const h = Math.floor(x / 60);
        return 'коло без тебе +' + (h > 0 ? h + ' год ' : '') + (x % 60 ? (x % 60) + ' хв' : '');
      }
      case 'fall': return 'глек з полиці +' + pct;
      default: return '';
    }
  }

  // ---------- малюнок ----------

  function infoText(st) {
    const c = cat(st);
    const tiers = c ? c.list.map((g) => '«' + g.tierName + '»').join(', ') : '';
    const k = st.guests;
    return 'Гості приїжджають разом зі щаблями драбини (' + tiers + ') і лишаються назавжди — обпал їх не проганяє. '
      + 'Кожен рівень шани будь-якого гостя — +3 % до всього, а ще своя пільга. Замовлення платять ×8 від ціни виробу '
      + '(гончарі з усього світу — ×12) і ще +10 % за кожен рівень шани самого гостя. На дворі стільки місць, скільки '
      + 'гостей, але не більше трьох' + (k && k.v && k.v.map ? ' (із «Заморською картою» — ще одне)' : '')
      + '; новий гість приходить кожні 20–40 хв і чекає 3–6 годин.';
  }

  function orderCard(st, api, o) {
    const k = st.guests;
    const esc = (x) => api.esc(st, x);
    const d = def(st, o.guest);
    const who = (d && d.people[o.who]) || (d && d.name) || o.guest;
    const ready = o.have >= o.n;
    const req = [];
    if (Q_REQ[o.q]) req.push(Q_REQ[o.q]);
    if (o.style) req.push('«' + esc(styleName(st, o.style)) + '»');
    const share = Math.min(100, Math.round((Math.min(o.have, o.n) / o.n) * 100));
    const armed = k.armedId === o.id && Date.now() < k.armed;
    // 0 — нове, ще не намальоване: анімація піде з першим малюнком (навіть якщо «Село» відкрили за годину).
    const f = k.fresh[o.id];
    if (f === 0) k.fresh[o.id] = Date.now();
    const fresh = f === 0 || (f > 0 && Date.now() - f < FRESH_CARD_MS);
    const hint = o.q >= 4 ? ' — розкішні бувають лише з розписаної партії' : ' — виліпи й обпали';
    // Одне речення: хто, що хоче, скільки заплатить і скільки шани; множник — у чипі з поясненням. «просить: миска ×2»,
    // а не «хоче миска ×2»: назва виробу стоїть у називному, і з двокрапкою це не різатиме око на мисках і макітрах.
    const line = '<b>' + esc(who) + '</b> просить: <b>' + esc(wareName(st, o.ware).toLowerCase()) + ' ×' + o.n + '</b>'
      + (req.length ? ' <span class="clkd-oreq">(' + req.join(' · ') + ')</span>' : '')
      + ' · ≈' + api.potsShort(o.pay) + ' · шана +' + o.rep
      + ' <span class="clkd-omult" title="Стільки гість платить понад ціну виробу: ×8 (гончарі з усього світу — ×12)'
      + ' і +10 % за кожен рівень його шани.">×' + api.dec(o.mult) + '</span>';
    return '<div class="clkd-order' + (ready ? ' ready' : '') + (fresh ? ' fresh' : '') + '" data-order="' + o.id + '">'
      + '<div class="clkd-oart">' + api.wareSvg(o.ware, { style: o.style, quality: o.q, cls: 'clkd-oware', slot: 'gst-' + o.id })
      + '<span class="clkd-on">×' + o.n + '</span><span class="clkd-oemo" aria-hidden="true">' + ((d && d.emoji) || '🏛') + '</span></div>'
      + '<div class="clkd-obody">'
      + '<div class="clkd-oline">' + line + '</div>'
      + '<div class="clkd-obar"><i style="width:' + share + '%"></i><span>є ' + Math.min(o.have, 999) + ' з ' + o.n + '</span></div>'
      + '<div class="clkd-obtns">'
      + '<button type="button" class="primary small clkd-give" data-give="' + o.id + '"' + (st.mine && ready ? '' : ' disabled') + '>🤝 Віддати</button>'
      + '<button type="button" class="ghost small clkd-skip' + (armed ? ' armed' : '') + '" data-skip="' + o.id + '"' + (st.mine ? '' : ' disabled')
      // «Точно?» — не довше за «Відпустити»: інакше кнопка переносилась у новий рядок і тікала з-під пальця на другому натиску.
      + ' title="' + (armed ? 'Натисни ще раз — гість поїде' : 'Гість поїде без образи — місце на дворі звільниться') + '">'
      + (armed ? 'Точно?' : 'Відпустити') + '</button>'
      + '<span class="clkd-otime small muted" title="Скільки ще гість чекатиме">⏳ <i class="clkd-cd" data-at="' + o.until + '" data-done="відплив"></i></span>'
      + '</div>'
      + (ready ? '' : '<div class="clkd-ohint muted small">бракує ' + (o.n - o.have) + hint + '</div>')
      + '</div></div>';
  }

  function guestCard(st, api, g) {
    const esc = (x) => api.esc(st, x);
    const c = cat(st);
    const d = def(st, g.key);
    if (!c || !d) return '';
    const levels = c.levels || [0, 5, 15, 35, 70, 120, 190, 280, 400, 550, 750];
    const top = levels.length - 1;
    const from = levels[g.level];
    const to = levels[Math.min(top, g.level + 1)];
    const p = g.level >= top ? 100 : Math.max(0, Math.min(100, Math.round(((g.pts - from) / Math.max(1, to - from)) * 100)));
    const next = g.level >= top
      ? '<span class="clkd-top">👑 шана найвища — вище нема куди</span>'
      : 'ще <b>' + api.count(g.need) + '</b> до ' + (g.level + 1) + '-го рівня: +3 % до всього, ' + esc(d.step);
    // Пільга: на нульовому рівні — словами (числа вже в рядку «ще N до 1-го»), далі — скільки дає просто зараз.
    const perk = (PERK_ICON[d.kind] || '✨') + ' '
      + (g.level > 0 ? 'зараз: ' + esc(perkNow(api, d, g.level)) : esc(String(d.perk).split(':')[0]));
    const likes = esc(d.likesText) + (Q_REQ[d.quality] ? ' · ' + Q_REQ[d.quality] : '');
    return '<div class="clkd-g l' + g.level + '" data-guest="' + esc(g.key) + '" title="' + esc(d.perk) + '">'
      + '<div class="clkd-gtop"><span class="clkd-gemo" aria-hidden="true">' + d.emoji + '</span>'
      + '<b class="clkd-gname">' + esc(d.name) + '</b>'
      + '<span class="clkd-stars" title="Рівень шани">★<b>' + g.level + '</b><i>/' + top + '</i></span></div>'
      + '<div class="muted small clkd-likes">любить: ' + likes + '</div>'
      + '<div class="clkd-rbar"><i style="width:' + p + '%"></i></div>'
      + '<div class="muted small clkd-rnext">' + next + '</div>'
      + '<div class="small clkd-perk">' + perk + '</div></div>';
  }

  function paint(st, api) {
    const k = st.guests;
    if (!k || !k.el) return;
    k.dirty = false;
    if (!k.v) { if (!k.el.hidden) k.el.hidden = true; return; }
    if (k.el.hidden) k.el.hidden = false;
    const c = cat(st);
    const esc = (x) => api.esc(st, x);
    if (!c) {
      api.swap(k.el, '<div class="clk-sub">🏛 Гостинний двір</div><div class="muted small">Гості з\'являються на порозі…</div>');
      return;
    }
    const v = k.v;
    const sn = api.serverNow(st);
    const live = v.orders.filter((o) => o.until > sn);
    const head = '<div class="clk-sub clkd-title">🏛 Гостинний двір<span class="muted small"> · на дворі ' + live.length + ' з ' + v.slots
      + (v.allMult > 1.0001 ? ' · шана гостей +' + api.dec((v.allMult - 1) * 100) + ' % до всього' : '') + '</span>'
      + api.info(esc(infoText(st))) + '</div>';
    const react = k.react && Date.now() - k.react.at < REACT_MS
      ? '<div class="clkd-react"><span class="clkd-remoji">' + esc(k.react.emoji) + '</span><span>' + esc(k.react.text) + '</span></div>'
      : '';
    let orders;
    if (live.length) {
      orders = '<div class="clkd-orders">' + live.map((o) => orderCard(st, api, o)).join('') + '</div>';
      if (live.length >= v.slots) orders += '<div class="muted small clkd-next">двір повний — новий гість зачекає, поки звільниться місце</div>';
      else if (v.next > sn) orders += '<div class="muted small clkd-next">наступний гість — через <i class="clkd-cd" data-at="' + v.next + '" data-done="ось-ось"></i></div>';
    } else {
      orders = '<div class="clk-teaser muted small">Гостей із замовленнями поки нема — наступний прибуде через '
        + '<i class="clkd-cd" data-at="' + v.next + '" data-done="ось-ось"></i></div>';
    }
    const met = new Set(v.list.map((g) => g.key));
    const soon = c.list.find((g) => !met.has(g.key));
    const html = head + react + orders
      + '<div class="clkd-guests">' + v.list.map((g) => guestCard(st, api, g)).join('') + '</div>'
      + (soon ? '<div class="clkd-soon muted small">⛵ Хто далі: ' + soon.emoji + ' <b>' + esc(soon.name) + '</b> · щабель «'
        + esc(soon.tierName) + '»</div>' : '');
    if (api.swap(k.el, html)) k.cds = [...k.el.querySelectorAll('.clkd-cd')];
    countdowns(st, api);
  }

  function countdowns(st, api) {
    const k = st.guests;
    if (!k || !k.cds) return;
    const sn = api.serverNow(st);
    for (const el of k.cds) {
      const at = +el.dataset.at;
      const t = at > sn ? left(api, at - sn) : (el.dataset.done || '0:00');
      if (el.textContent !== t) el.textContent = t;
    }
  }

  // ---------- події: прибуття, нові замовлення, шана ----------

  /// Новий гість — тост, рядок у стрічці, іскри й гість на сцені. «Новий» — якого ще не бачив цей нік на цьому пристрої
  /// і який прибув щойно (давніше за пів години — не новина: зайшов з іншого пристрою чи наступного дня).
  function arrivals(st, api) {
    const k = st.guests;
    // Перший вид після входу приходить без каталогу (сервер шле його на прохання look { catalog: true }): без нього
    // нема ні імені гостя, ні рядка про корабель — вітаємо наступним видом, а «бачив» поки не ставимо.
    if (!cat(st)) return new Set();
    const key = SEEN_KEY + '.' + myNick(st);
    if (!k.seen || k.seenKey !== key) {
      let seen = null;
      try { seen = JSON.parse(api.storeGet(key, 'null')); } catch (e) { seen = null; }
      k.seen = new Set(Array.isArray(seen) ? seen : []);
      k.seenKey = key;
    }
    const sn = api.serverNow(st);
    const fresh = k.v.list.filter((g) => !k.seen.has(g.key));
    if (!fresh.length) return new Set();
    for (const g of fresh) k.seen.add(g.key);
    api.storeSet(key, JSON.stringify([...k.seen]));
    const now = fresh.filter((g) => sn - g.at < FRESH_MS);
    if (!now.length || !st.mine) return new Set();
    // Кожен гість — своїм тостом (разом прибувають хіба що зі старого збереження), а звук, іскри, рядок у стрічці
    // й корабель на сцені — раз, для першого: два кораблі одночасно — це вже не свято, а затор у порту.
    for (const g of now) {
      const d = def(st, g.key);
      api.toast(st, d ? d.arrive : '🏛 До Гостинного двору прибули заморські гості', 'ok');
    }
    const first = def(st, now[0].key);
    api.feed(st, first ? first.arrive : '🏛 До Гостинного двору прибули заморські гості');
    api.sfx('rare');
    api.sparks(st, null, 24, true, 50, 40);
    api.popAt(st, ((first && first.emoji) || '🏛') + ' ' + ((first && first.name) || 'Гості'), 'big', 50, 28);
    if (typeof api.sceneGuest === 'function') api.sceneGuest(st, now[0].key);
    return new Set(now.map((x) => x.key));
  }

  /// Нове замовлення на дворі — звук, нотатка на ярлику й гість на сцені. Перший вид лише запам'ятовує.
  function newOrders(st, api, arrived) {
    const k = st.guests;
    const max = k.v.orders.reduce((m, o) => Math.max(m, o.id), 0);
    if (k.lastId == null) { k.lastId = max; return; }
    if (max <= k.lastId) return;
    const fresh = k.v.orders.filter((o) => o.id > k.lastId);
    k.lastId = max;
    for (const o of fresh) k.fresh[o.id] = 0;
    // Прибулий гість уже відгримів своє — його «привітальне» замовлення вдруге не дзвенить.
    const other = fresh.filter((o) => !arrived.has(o.guest));
    if (!other.length || !st.mine) return;
    api.sfx('guest');
    if (typeof api.sceneGuest === 'function') api.sceneGuest(st, other[other.length - 1].guest);
  }

  /// Шана виросла — зірочка й дзвін (перший вид лише запам'ятовує).
  function levelUps(st, api) {
    const k = st.guests;
    const levels = {};
    for (const g of k.v.list) levels[g.key] = g.level;
    if (k.levels && st.mine) {
      for (const g of k.v.list) {
        if (k.levels[g.key] == null || g.level <= k.levels[g.key]) continue;
        const d = def(st, g.key);
        api.sfx('rep-up');
        api.popAt(st, '⭐ ' + ((d && d.name) || '') + ' · шана ' + g.level, 'big', 50, 22);
        api.sparks(st, st.fx, 20, true, 50, 28);
      }
    }
    k.levels = levels;
  }

  // ---------- дії ----------

  function giveOrder(st, api, ev, id) {
    const k = st.guests;
    if (!human(ev) || !st.mine || !k || k.busy) return;
    k.busy = true;
    for (const b of k.el.querySelectorAll('[data-give="' + id + '"], [data-skip="' + id + '"]')) b.disabled = true;
    api.act(st, 'guests', { do: 'give', id }).then((r) => {
      const kk = st.guests;
      if (!kk) return;                       // картку вже закрили, поки летіла відповідь
      kk.busy = false;
      if (r && r.ok) {
        const text = r.message || '';
        kk.react = { at: Date.now(), emoji: text.split(' ')[0] || '🏛', text: (text.match(/«([^»]+)»/) || [])[1] || '' };
        api.sfx('deal');
        setTimeout(() => api.sfx('coin'), 180);
        const pay = (text.match(/»\s\+(.+?)\s·\sшана/) || [])[1];
        if (pay) api.popAt(st, '+' + pay.trim(), 'big', 50, 36);
        api.sparks(st, st.fx, 18, true, 50, 44);
        setTimeout(() => { if (st.guests) paint(st, api); }, REACT_MS + 50);
      }
      paint(st, api);
    });
  }

  /// «Відпустити» — двома натисками: гість поїде без образи, але замовлення вже не вернеш.
  function skipOrder(st, api, ev, id) {
    const k = st.guests;
    if (!human(ev) || !st.mine || !k) return;
    if (k.armedId !== id || Date.now() > k.armed) {
      k.armedId = id;
      k.armed = Date.now() + ARM_MS;
      api.sfx('tap');
      paint(st, api);
      return;
    }
    k.armed = 0;
    k.armedId = 0;
    for (const b of k.el.querySelectorAll('[data-give="' + id + '"], [data-skip="' + id + '"]')) b.disabled = true;
    api.act(st, 'guests', { do: 'skip', id }).then((r) => {
      if (r && r.ok) api.sfx('soft');
      if (st.guests) paint(st, api);
    });
  }

  function onClick(st, api, e) {
    const give = e.target.closest('[data-give]');
    if (give) { giveOrder(st, api, e, +give.dataset.give); return; }
    const skip = e.target.closest('[data-skip]');
    if (skip) skipOrder(st, api, e, +skip.dataset.skip);
  }

  // ---------- місце у «Селі» ----------

  /// Розділ — угорі вкладки «Село». Вкладку робить цех, і він міг змонтуватись пізніше за нас — тож шукаємо щоразу.
  function mountBlock(st, api) {
    const k = st.guests;
    if (!k || !st.guildPane) return;
    if (!k.el) {
      k.el = document.createElement('section');
      k.el.className = 'clkg-card clkd';
      k.el.hidden = true;
      k.el.addEventListener('click', (e) => onClick(st, api, e));
    }
    if (k.el.parentElement !== st.guildPane || st.guildPane.firstChild !== k.el) st.guildPane.insertBefore(k.el, st.guildPane.firstChild);
  }

  /// «Село» відкривається, коли з ним є про що говорити (гейт цеху). Гості — теж привід: хто дійшов до порту, той побачить
  /// свій двір, навіть якщо цех і село йому досі байдужі.
  function gate(st, api) {
    const prev = st.gates && st.gates.guild;
    if (!prev || prev._guests) return;
    const fn = (st2, v) => !!(v && v.guests) || prev(st2, v);
    fn._guests = true;
    api.showWhen(st, 'guild', fn);
  }

  /// Смуга «Далі» (clicker-scene.js goalOf): коли в коморі вже є все для замовлення гостя — підказати віддати його.
  /// Пріоритет 1, як «уже можна купити»: гостя чекати не варто — він відпливе, а шана росте лише з віддачі.
  (window.HClicker.goals = window.HClicker.goals || []).push((st, v, api) => {
    const k = st.guests;
    if (!k || !k.v || !st.mine) return null;
    const sn = api.serverNow(st);
    const o = k.v.orders.find((x) => x.until > sn && x.have >= x.n);
    if (!o) return null;
    const d = def(st, o.guest);
    const who = (d && d.people[o.who]) || (d && d.name) || o.guest;
    const emoji = (d && d.emoji) || '🏛';
    return {
      icon: '<text x="16" y="23" font-size="19" text-anchor="middle">' + emoji + '</text>',
      text: who + ' чекає: ' + wareName(st, o.ware).toLowerCase() + ' ×' + o.n,
      sub: 'усе є в коморі — віддай за ≈' + api.potsShort(o.pay) + ' і шану +' + o.rep,
      pct: 100, eta: 0, tab: 'guild', prio: 1,
    };
  });

  HClicker.part({
    id: 'guests',
    order: 62,

    mount(st, api) {
      st.guests = {
        v: null, el: null, cds: [], dirty: false, lastId: null, fresh: {}, levels: null, seen: null, seenKey: '',
        react: null, armed: 0, armedId: 0, busy: false, looked: 0,
      };
      mountBlock(st, api);
      gate(st, api);
    },

    update(st, v, api) {
      const k = st.guests;
      if (!k) return;
      mountBlock(st, api);
      gate(st, api);
      const g = v.guests;
      if (!g) {
        k.v = null;
        api.tabNote(st, 'guild', 'guests', '', 5);
        if (k.el && !k.el.hidden) k.el.hidden = true;
        return;
      }
      k.v = {
        list: (g.list || []).map((x) => ({ key: x.key, level: x.level || 0, pts: x.pts || 0, need: x.need || 0, at: ms(x.at) })),
        orders: (g.orders || []).map((o) => Object.assign({}, o, { until: ms(o.until) })),
        next: ms(g.next), slots: g.slots || 0, allMult: g.allMult || 1, delivered: g.delivered || 0, map: !!g.map,
      };
      const arrived = arrivals(st, api);
      newOrders(st, api, arrived);
      levelUps(st, api);
      const sn = api.serverNow(st);
      const live = k.v.orders.filter((o) => o.until > sn);
      const ready = live.some((o) => o.have >= o.n);
      // «🏛2» на ярлику «Село»: замовлення чекають; готове віддати — вище за інші нотатки вкладки.
      api.tabNote(st, 'guild', 'guests', live.length ? '🏛' + live.length : '', ready ? 1 : 3);
      k.dirty = true;
      if (st.tab === 'guild') paint(st, api);
    },

    slow(st, api, sn) {
      const k = st.guests;
      if (!k || !k.v) return;
      if (st.tab === 'guild') {
        // Перемальовуємо, лише коли щось змінилось: новий вид, замовлення щойно відпливло, згасла репліка чи «Точно?».
        const gone = k.v.orders.some((o) => o.until <= sn && o.until > sn - 400);
        const faded = k.react && Date.now() - k.react.at > REACT_MS && Date.now() - k.react.at < REACT_MS + 400;
        const disarmed = k.armedId && Date.now() > k.armed;
        if (disarmed) k.armedId = 0;
        if (k.dirty || gone || faded || disarmed) paint(st, api);
        else countdowns(st, api);
      }
      // Час нового гостя настав, а гончар лише дивиться: раз питаємо свіжий вид (лише коли на дворі є місце).
      const live = k.v.orders.filter((o) => o.until > sn).length;
      if (st.mine && api.visible(st) && k.v.next && live < k.v.slots && sn > k.v.next + 1500 && k.looked !== k.v.next) {
        k.looked = k.v.next;
        api.act(st, 'look');
      }
    },

    unmount(st) {
      const k = st.guests;
      if (k && k.el) k.el.remove();
      st.guests = null;
    },
  });
})();
