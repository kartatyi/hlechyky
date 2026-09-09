/*
  Гончарне коло. Соло-клікер: тиснеш на коло — ліпиш глеки, купуєш верстати, міняєш глеки на черепки.

  Правила рахує сервер (Impl/Clicker.cs). Клієнт понад малювання робить рівно дві речі:
  1) батчить кліки — рахує їх локально й шле Act('spin', { n }) раз на 700 мс, а не двадцять разів за секунду;
  2) доліковує лічильник між подіями 'room' — за view.perSecond, зі стелею 8 год, як на сервері. Простій
     беремо серверний (view.now − view.lastSync) і додаємо лише те, що натікало ВІД отримання виду, — так
     збитий годинник у гравця не малює неіснуючих глеків. Сервер лишається джерелом правди: прийшов новий
     вид — беремо його число, а не своє.

  Вид (Impl/Clicker.cs): { pots, total, perClick, perSecond, upgrades: { key: { level, price, name, desc, max } },
                           canSellToday, soldToday, cap, rate, lastSync, now }.
  Дії: spin { n }, buy { key }, sell { pots }.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<ellipse cx="8" cy="12.2" rx="6.2" ry="2.3" fill="none" stroke="var(--muted)" stroke-width="1.3"/>'
    + '<path d="M5.6 10.8V7.4c0-1 .8-1.3.8-2.1V3.6h3.2v1.7c0 .8.8 1.1.8 2.1v3.4z" fill="var(--clay)"/></svg>';

  const BATCH_MS = 700;                   // як часто злітає накопичена пачка кліків
  const MAX_BATCH = 12;                   // рівно стільки сервер приймає за секунду
  const OFFLINE_CAP_MS = 8 * 3600 * 1000; // та сама стеля офлайну, що й на сервері
  const ORDER = ['wheel', 'apprentice', 'kiln', 'clay'];

  const num = (n) => Math.round(n).toLocaleString('uk-UA');
  /// «0,5» замість «0.5»: десяткова кома в нас усюди українська.
  const dec = (n) => (Math.round(n * 10) / 10).toLocaleString('uk-UA', { maximumFractionDigits: 1 });
  const shards = (n) => (n % 100 >= 11 && n % 100 <= 14 ? 'черепків'
    : n % 10 === 1 ? 'черепок' : n % 10 >= 2 && n % 10 <= 4 ? 'черепки' : 'черепків');

  function state(root) {
    if (!root._clk) {
      root._clk = {
        el: null, count: null, rate: null, wheel: null, pops: null, one: null, all: null, left: null, shop: null, buys: [],
        base: 0, idleMs: 0, recvAt: Date.now(), perClick: 1, perSecond: 0, rateOf: 100, canSell: 0, mine: false,
        unsent: 0, inflight: 0, tokens: MAX_BATCH, tokensAt: Date.now(), shown: -1, raf: 0, timer: 0, ctx: null,
      };
    }
    return root._clk;
  }

  /// Пасив від мітки сервера, округлений УНИЗ: у сервера ще лежить дробовий залишок, тож це чесна нижня межа.
  /// Простій = серверний (на момент виду) плюс те, що натікало на нашому годиннику ВІД отримання виду:
  /// різниця годинників браузера й сервера в розрахунок не входить.
  function passive(st) {
    const idle = Math.min(Math.max(0, st.idleMs + (Date.now() - st.recvAt)), OFFLINE_CAP_MS);
    return Math.floor((idle / 1000) * st.perSecond);
  }

  /// Те, що сервер уже точно має: його число плюс пасив. Від нього рахуємо продаж.
  const firm = (st) => st.base + passive(st);

  // ---------- малювання ----------

  /// Кличеться на кожен кадр: і число, і кнопки мусять оживати самі, поки коло крутиться без кліків.
  function paint(st) {
    // Підтверджене число рахуємо один раз: від нього і лічильник (з нашими ще не відправленими кліками),
    // і кнопки прилавка (уже без них).
    const sure = firm(st);
    let n = sure + (st.unsent + st.inflight) * st.perClick;
    // Дрібний відкат — це не витрата, а різниця округлень між нашим доліком і сервером: не смикаємо число.
    if (st.shown >= 0 && n < st.shown && st.shown - n <= 2) n = st.shown;
    if (n !== st.shown) {
      st.shown = n;
      st.count.textContent = num(n);
    }
    for (const b of st.buys) {
      const off = b.dataset.maxed === '1' || !st.mine || n < +b.dataset.price;
      if (b.disabled !== off) b.disabled = off;
    }
    // Продаж — від підтвердженого числа, а не від намальованого: у st.unsent може лежати хвіст кліків,
    // які цієї миті ще не долетіли, і кнопка обіцяла б сервером не наліплені глеки.
    const ready = Math.floor(sure / st.rateOf);
    const many = Math.min(ready, st.canSell);
    const one = !(st.mine && ready >= 1 && st.canSell >= 1);
    if (st.one.disabled !== one) st.one.disabled = one;
    const all = !(st.mine && many > 1);
    if (st.all.disabled !== all) st.all.disabled = all;
    const pots = String(many * st.rateOf);
    if (st.all.dataset.pots !== pots) st.all.dataset.pots = pots;
    const label = 'Усе (' + num(many) + ' 🏺)';
    if (st.all.textContent !== label) st.all.textContent = label;
  }

  function loop(st) {
    if (!st.el || !st.el.isConnected) { st.raf = 0; return; }
    paint(st);
    st.raf = requestAnimationFrame(() => loop(st));
  }

  /// «+12», що злітає над колом. Живе рівно доти, доки триває анімація.
  function pop(st, amount) {
    if (st.pops.childElementCount > 12) return;   // палець швидший за око: більше однаково не роздивитись
    const el = document.createElement('span');
    el.className = 'clk-pop';
    el.textContent = '+' + num(amount);
    el.style.left = (32 + Math.random() * 36) + '%';
    el.addEventListener('animationend', () => el.remove());
    // У фоновій вкладці анімації не крутяться, а отже й animationend не прилетить — прибираємо і за часом,
    // інакше «+N» назбирувались би там сотнями до самого повернення.
    setTimeout(() => el.remove(), 2000);
    st.pops.appendChild(el);
  }

  // ---------- дії ----------

  function flush(st) {
    if (!st.unsent || !st.ctx) return;
    const n = Math.min(st.unsent, MAX_BATCH);
    st.unsent -= n;
    st.inflight += n;
    const back = () => { st.inflight = Math.max(0, st.inflight - n); };
    // Кліки, що вже полетіли, знімає з рахунку сам вид (див. update): вид і відповідь приходять різними
    // кадрами вебсокета, і якби ми чекали відповіді, між ними лічильник встигав би показати їх двічі.
    // Лишається тільки невдача: тоді виду не буде взагалі, і порахувати назад мусимо ми.
    st.ctx.act('spin', { n }).then((r) => { if (!r || !r.ok) back(); }, back);
  }

  function spin(st) {
    if (!st.ctx || !st.mine) return;
    // Те саме відро дозволів, що й на сервері: понад дванадцять кліків за секунду він однаково не візьме,
    // тож і малювати їх не варто — інакше лічильник обіцяв би те, чого потім не дорахується.
    const now = Date.now();
    st.tokens = Math.min(MAX_BATCH, st.tokens + ((now - st.tokensAt) / 1000) * MAX_BATCH);
    st.tokensAt = now;
    if (st.tokens < 1) return;
    st.tokens -= 1;
    st.unsent++;
    pop(st, st.perClick);
    st.wheel.classList.remove('hit');
    void st.wheel.offsetWidth;         // перезапуск анімації «стуку»: без цього другий клік поспіль її не покаже
    st.wheel.classList.add('hit');
    paint(st);
  }

  /// Покупка й продаж рахуються від того, що вже долетіло до сервера, тож накопичені кліки шлемо першими.
  function order(st, action, payload) {
    if (!st.ctx || !st.mine) return;
    flush(st);
    st.ctx.act(action, payload);
  }

  function shop(st, ctx) {
    const ups = (ctx.view && ctx.view.upgrades) || {};
    const html = ORDER.filter((k) => ups[k]).map((k) => {
      const u = ups[k];
      const maxed = u.max > 0 && u.level >= u.max ? 1 : 0;
      return '<button type="button" class="clk-up" data-buy="' + ctx.esc(k) + '"'
        + ' data-price="' + (u.price || 0) + '" data-maxed="' + maxed + '" disabled>'
        + '<b>' + ctx.esc(u.name) + '</b>'
        + '<span class="clk-lvl">' + (u.level ? 'рівень ' + u.level : 'ще не куплено')
        + (u.max > 0 ? ' з ' + u.max : '') + '</span>'
        + '<span class="muted small">' + ctx.esc(u.desc) + '</span>'
        + '<span class="clk-price">' + (maxed ? 'досить' : num(u.price)) + '</span>'
        + '</button>';
    }).join('');
    if (st.shop.dataset.sig === html) return;
    st.shop.dataset.sig = html;
    st.shop.innerHTML = html;
    // Кнопки тримаємо списком: paint() ходить по них щокадру, і шукати їх заново шістдесят разів на секунду нема за що.
    st.buys = [...st.shop.querySelectorAll('[data-buy]')];
    st.buys.forEach((b) => b.onclick = () => order(st, 'buy', { key: b.dataset.buy }));
  }

  // ---------- модуль ----------

  HGames.register({
    id: 'clicker',
    icon: ICON,
    seatNames: ['гончар'],
    seatClass: ['c'],

    mount(root, ctx) {
      const st = state(root);
      root.innerHTML = '<div class="clk">'
        + '<div class="clk-head"><b class="clk-count">0</b><span class="muted small">глеків</span></div>'
        + '<div class="clk-rate muted small"></div>'
        + '<div class="clk-wheelbox"><div class="clk-pops"></div>'
        + '<button type="button" class="clk-wheel" aria-label="Крутити коло">'
        // Крутиться сам круг із борознами й цяткою (без неї обертання ідеального кола не видно),
        // а глек стоїть рівно: гончар його тримає.
        + '<svg viewBox="0 0 100 100" aria-hidden="true">'
        + '<g class="clk-turn"><circle class="clk-disc" cx="50" cy="50" r="46"/>'
        + '<circle class="clk-ring" cx="50" cy="50" r="35"/>'
        + '<circle class="clk-ring" cx="50" cy="50" r="24"/>'
        + '<circle class="clk-speck" cx="50" cy="12" r="2.6"/></g>'
        + '<path class="clk-jug" d="M40 64V45c0-5 4-6 4-10v-9h12v9c0 4 4 5 4 10v19z"/>'
        + '</svg></button></div>'
        + '<div class="clk-sell"><button type="button" class="primary clk-one" disabled></button>'
        + '<button type="button" class="ghost clk-all" data-pots="0" disabled></button></div>'
        + '<div class="clk-left muted small"></div><div class="clk-shop"></div></div>';
      st.el = root.querySelector('.clk');
      st.count = root.querySelector('.clk-count');
      st.rate = root.querySelector('.clk-rate');
      st.wheel = root.querySelector('.clk-wheel');
      st.pops = root.querySelector('.clk-pops');
      st.one = root.querySelector('.clk-one');
      st.all = root.querySelector('.clk-all');
      st.left = root.querySelector('.clk-left');
      st.shop = root.querySelector('.clk-shop');
      st.ctx = ctx;
      ctx.clk = st;                     // щоб onKey дістався до стану: там є лише ctx
      st.wheel.addEventListener('click', () => spin(st));
      st.one.onclick = () => order(st, 'sell', { pots: st.rateOf });
      st.all.onclick = () => order(st, 'sell', { pots: +st.all.dataset.pots });
      st.timer = setInterval(() => flush(st), BATCH_MS);
      if (!st.raf) loop(st);
    },

    update(root, ctx) {
      const st = state(root);
      if (!st.el) return;
      st.ctx = ctx;
      ctx.clk = st;
      st.mine = !!ctx.mine;
      const v = ctx.view;
      if (v && v.pots != null) {
        // Сервер — джерело правди: беремо його число і його мітку часу, від них доліковуємо далі.
        // Усе, що вже полетіло, у цьому числі вже враховано — свій запас відпущених кліків обнуляємо.
        st.inflight = 0;
        st.base = v.pots;
        const sync = Date.parse(v.lastSync);
        const now = Date.parse(v.now);
        st.idleMs = Number.isFinite(sync) && Number.isFinite(now) ? Math.max(0, now - sync) : 0;
        st.recvAt = Date.now();
        st.perClick = v.perClick || 1;
        st.perSecond = v.perSecond || 0;
        st.rateOf = v.rate || 100;
        st.canSell = v.canSellToday || 0;
      }
      // Швидкість обертання — від пасиву: коло без підмайстрів стоїть, з піччю крутиться помітно.
      st.wheel.style.setProperty('--clk-spin', (st.perSecond > 0 ? Math.max(1.1, 9 / st.perSecond) : 0) + 's');
      const rate = 'за клік +' + num(st.perClick)
        + (st.perSecond > 0 ? ' · без тебе +' + dec(st.perSecond) + ' за секунду' : ' · підмайстрів ще нема');
      if (st.rate.textContent !== rate) st.rate.textContent = rate;
      const one = 'Продати ' + num(st.rateOf) + ' → 🏺1';
      if (st.one.textContent !== one) st.one.textContent = one;
      const left = st.canSell > 0
        ? 'сьогодні ще ' + num(st.canSell) + ' ' + shards(st.canSell) + ', по ' + num(st.rateOf) + ' глеків за черепок'
        : 'на сьогодні черепки скінчились, приходь завтра';
      if (st.left.textContent !== left) st.left.textContent = left;
      shop(st, ctx);
      paint(st);
    },

    onKey(e, ctx) {
      if (e.code !== 'Space' || !ctx.mine || !ctx.clk) return false;
      // Фокус на будь-якій кнопці картки — пробіл належить їй: на колі він і так порахується (інакше клік
      // пішов би двічі), а на верстаті чи прилавку ми б крутили коло замість покупки й продажу.
      const on = document.activeElement;
      if (on && on.tagName === 'BUTTON' && ctx.clk.el && ctx.clk.el.contains(on)) return false;
      spin(ctx.clk);
      return true;
    },

    status(ctx) {
      const v = ctx.view || {};
      if (v.total == null) return '';
      return 'усього наліплено ' + num(v.total) + ' · обміняно сьогодні ' + num(v.soldToday || 0);
    },

    unmount(root) {
      const st = root._clk;
      if (!st) return;
      clearInterval(st.timer);
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      st.el = null;
      root._clk = null;
    },
  });
})();
