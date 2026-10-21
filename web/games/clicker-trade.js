/*
  Заморська торгівля Гончарного кола (v12, docs/games/specs/clicker-v12.md §3–4). Частина ядра clicker.js — пакет A.

  Розділ «🚢 Заморська торгівля» у вкладці «🤝 Село», одразу після Толоки (корінь — data-sec="trade"; коли «Село» має
  місце data-slot="ships", розділ живе в ньому):
  1) поки нема Пристані (view.trade = null) — один рядок «🚢 Кораблі прийдуть до Пристані на Ворсклі»;
  2) Слава у світі зі смугою до наступної заморської майстерні (trade.gate);
  3) картки кораблів: звідки, що просить (виріб, якість, розпис, «з них K розкішних»), «завантажено N з M», скільки ще
     стоїть, що дасть за виріб; «Завантажити все (can)» і «+5» — trade { do: 'loadAll' | 'load', id, n };
  4) «наступний корабель — за 12 хв» або «Пристань повна».
  Майстерня відчинилась (gate змінився) — тост, звук, нотатка на ярлику «Майстерня» й кнопка «→ у Майстерню» в розділі.
  Смуга «Далі» (HClicker.goals): корабель, на який є що вантажити, і щойно відчинена майстерня без жодного рівня.

  Правила рахує сервер (Impl/ClickerTrade.cs); тут — лише малюнок і дії.
  Дані: view.trade = null | { slava, next, full, gate: null | { key, name, need }, ships: [{ id, port, from, ware, style,
    q, n, lux, loaded, loadedLux, until, first, can, minutes, slavaPer }] }; пороги — st.shopCat.slavaGates [{ key, name,
    need }]. Звуки: paid (вантаж), rare (повний корабель), unlock (майстерня), tap.
*/
(() => {
  if (!window.HClicker || !window.HClicker.part) return;
  const human = HGames.ui.human;

  /// Якість, якої просять, — у множині: «(дзвінкі й кращі · «Делфт»)», як у гостей і Толоки.
  const Q_REQ = ['', '', 'добрі й кращі', 'дзвінкі й кращі', 'лише розкішні'];
  const LOOK_GRACE_MS = 1500;             // прийшов час корабля — стільки чекаємо, перш ніж спитати свіжий вид
  const ARM_MS = 4500;                    // «Точно?» на «Відпустити корабель» чекає другого натиску стільки
  const GAP_MIN = 40;                     // новий корабель — раз на стільки хвилин (Clicker.TradeGap)
  /// Плата корабля за виріб (індекс — якість) — сервер шле її у виді (trade.pay, Clicker.TradePlayQ*, SlavaQ*); це — лише
  /// на випадок старого сервера. Потрібна тільки для підсумку перед «Завантажити все»: платить сервер.
  const PAY = { minutes: [0, 0, 2, 3, 8], slava: [0, 0, 1, 2, 5], style: 1.5, styleSlava: 1, full: 0.1, fullSlava: 10 };
  const OPEN_MS = 10 * 60 * 1000;         // «→ у Майстерню» в розділі — до натиску, але не довше
  const SAID_MS = 4000;                   // сервер уже сказав «відкрито …» у тості вантажу — свого тосту не дублюємо
  const PLUS = 5;
  const FRESH_MS = 900;                   // корабель «припливає» анімацією лише в перший малюнок, а не з кожним видом
  /// Одноколірні вимпели портів (правило v10 — без державних прапорців): лише відтінок, щоб кораблі різнились на око.
  const HUE = {
    jingdezhen: 355, iznik: 178, delft: 215, meissen: 262, sevres: 312, kyoto: 8, athens: 28, faenza: 48,
    lisbon: 196, england: 232, korea: 150, talavera: 40,
  };

  // ---------- дрібниці ----------

  const ms = (t) => { const x = Date.parse(t); return Number.isFinite(x) ? x : 0; };
  const wareList = (st) => (st.craft && st.craft.wares) || (st.catalog && st.catalog.wares) || [];
  const wareName = (st, key) => { const w = wareList(st).find((x) => x.key === key); return w ? w.name : key; };
  const styleName = (st, key) => {
    const s = (st.styleList || []).find((x) => x.key === key) || ((st.catalog && st.catalog.styles) || []).find((x) => x.key === key);
    return s ? s.name : key;
  };
  const gates = (st) => (st.shopCat && st.shopCat.slavaGates) || [];
  /// Розпис світу — гончарів світу (shopCat.gates) чи заморський (slavaGates): за нього корабель платить ×1,5 і Слави +1.
  const worldStyle = (st, key) => !!key && ((st.shopCat && st.shopCat.gates && key in st.shopCat.gates) || gates(st).some((g) => g.key === key));
  const hueOf = (port) => (port in HUE ? HUE[port] : [...String(port)].reduce((h, c) => (h * 31 + c.charCodeAt(0)) % 360, 7));

  /// «~7 днів», «7 год 12 хв», «12 хв», «0:40» — скільки ще стоїть корабель і коли прийде наступний (перший стоїть тиждень).
  function left(api, msLeft) {
    if (msLeft <= 0) return '0:00';
    if (msLeft > 48 * 3600e3) { const d = Math.round(msLeft / 86400e3); return '~' + d + ' ' + api.plural(d, 'день', 'дні', 'днів'); }
    const m = Math.ceil(msLeft / 60000);
    if (m > 60) { const h = Math.floor(m / 60); return h + ' год' + (m % 60 ? ' ' + (m % 60) + ' хв' : ''); }
    return msLeft >= 60000 ? m + ' хв' : api.mmss(msLeft);
  }

  /// Підходить до замовлення — те саме правило, що Clicker.ShipMatch на сервері (без «відкладеного»: його знає лише сервер).
  const fits = (s, it, lux) => (!s.ware || it.ware === s.ware) && it.q >= (lux ? 4 : s.q) && (!s.style || it.style === s.style);

  /// Чому вантажити нічого — тим самим правилом, що Clicker.ShipLack на сервері. Натиск на порожнє більше не питає
  /// сервер: його відмова приходила тостом і ще раз червоним рядком під карткою (QA-3) — тепер картка каже одне й дає дію.
  function lack(st, api, s) {
    const luxLeft = Math.max(0, s.lux - s.loadedLux);
    const rest = s.n - s.loaded;
    if (luxLeft > 0 && luxLeft === rest) return 'Лишились місця для розкішних (' + luxLeft + ')';
    const what = s.ware ? wareName(st, s.ware).toLowerCase() : 'добрих і кращих виробів';
    const items = (st.craft && st.craft.items) || [];
    if (items.some((it) => it.n > 0 && fits(s, it, false))) return 'Вільних нема: ' + what + ' в коморі відкладено під Толоку чи замовлення — корабель їх не бере';
    return 'У коморі нема: ' + what + (s.style ? ' «' + styleName(st, s.style) + '»' : '') + ', ' + (s.q >= 3 ? 'дзвінкі й кращі' : 'добрі й кращі');
  }

  /// Звідки якість, якої просить корабель: ліпленням її не обереш — вона від горна. Дзвінкі дає й підмайстер (QA-4),
  /// а розкішні — лише розписана партія з рук.
  const qHint = (api, q) => (q >= 4 ? 'розкішні — лише з розписаної партії, яку палиш сам' : '');

  /// Скільки з цієї штуки в коморі вільне (вид комори v12: kept = [толока, замовлення, майстерштук]) — корабель бере лише його.
  const freeOf = (it) => Math.max(0, it.n - (it.kept ? (it.kept[0] || 0) + (it.kept[1] || 0) + (it.kept[2] || 0) : 0));

  /// Що візьме «Завантажити все» (want штук) — тим самим порядком, що ShipLoad на сервері: спершу розкішні в рядок
  /// «з них K», далі решта, найгірша придатна якість першою, а в межах якості — розпис світу першим.
  function loadSum(st, s, want) {
    const items = ((st.craft && st.craft.items) || []).filter((it) => it.n > 0);
    const free = new Map(items.map((it) => [it, freeOf(it)]));
    const later = (it) => (worldStyle(st, it.style) ? 0 : 1);
    const pick = (lux, n) => {
      const out = [];
      for (const it of items.filter((x) => fits(s, x, lux)).sort((a, b) => a.q - b.q || later(a) - later(b))) {
        if (n <= 0) break;
        const take = Math.min(free.get(it), n);
        if (take <= 0) continue;
        free.set(it, free.get(it) - take);
        out.push([it, take]);
        n -= take;
      }
      return out;
    };
    const luxLeft = Math.max(0, s.lux - s.loadedLux);
    const plan = pick(true, Math.min(want, luxLeft));
    const luxN = plan.reduce((a, x) => a + x[1], 0);
    plan.push(...pick(false, Math.min(want - luxN, s.n - s.loaded - luxLeft)));
    const pay = (st.trade.v && st.trade.v.pay) || PAY;
    const sum = { n: 0, minutes: 0, slava: 0, q4: 0 };
    for (const [it, c] of plan) {
      const q = Math.min(4, it.q);
      const wd = worldStyle(st, it.style);
      sum.n += c;
      sum.minutes += (pay.minutes[q] || 0) * (wd ? pay.style : 1) * c;
      sum.slava += ((pay.slava[q] || 0) + (wd ? pay.styleSlava : 0)) * c;
      if (q >= 4) sum.q4 += c;
    }
    // Сервер знає більше (інше вікно щойно щось узяло) — решту рахуємо за ціною замовлення.
    if (sum.n < want) { sum.minutes += (want - sum.n) * s.minutes; sum.slava += (want - sum.n) * s.slavaPer; sum.n = want; }
    if (s.loaded + sum.n >= s.n) { sum.minutes *= 1 + pay.full; sum.slava += Math.ceil((sum.slava * pay.fullSlava) / 100); }
    return sum;
  }

  /// «+49 · ≈216 хв гри · Слава +136 · з них 4 розкішні» — що дасть «Завантажити все».
  const sumText = (api, x) => '+' + x.n + ' · ≈' + api.num(Math.round(x.minutes)) + ' хв гри · Слава +' + api.count(x.slava)
    + (x.q4 ? ' · з них ' + x.q4 + ' ' + api.plural(x.q4, 'розкішний', 'розкішні', 'розкішних') : '');

  function infoText() {
    return 'Кораблі приходять до Пристані на Ворсклі раз на 40 хвилин (і без тебе), біля неї чекає до трьох, кожен стоїть 8 годин. '
      + 'Вантажити можна частинами. Корабель бере лише вільне — те, що відкладене під Толоку, замовлення сіл і гостей чи майстерштук, '
      + 'лишається в коморі. За кожен виріб — хвилини гри й Слава у світі: що краща якість і розпис світу, то більше; повний корабель — ще +10 %. '
      + 'Слава не витрачається й не згорає при обпалі: вона відчиняє шість заморських майстерень у «Майстерні».';
  }

  // ---------- малюнок ----------

  function pennant(port) {
    return '<svg class="clkt-flag" viewBox="0 0 24 30" aria-hidden="true" style="--h:' + hueOf(port) + '">'
      + '<path d="M3 2v27" stroke="currentColor" stroke-width="2" stroke-linecap="round"/>'
      + '<path d="M4 3h17l-5 5.5 5 5.5H4z" class="clkt-flagc"/></svg>';
  }

  function shipCard(st, api, s) {
    const k = st.trade;
    const esc = (x) => api.esc(st, x);
    const rest = s.n - s.loaded;
    const req = [Q_REQ[s.q] || ''];
    if (s.style) req.push('«' + esc(styleName(st, s.style)) + '»');
    if (s.lux > 0) req.push('з них ' + s.lux + ' ' + api.plural(s.lux, 'розкішний', 'розкішні', 'розкішних'));
    const what = s.ware ? esc(wareName(st, s.ware).toLowerCase()) : 'будь-які вироби';
    const share = Math.min(100, Math.round((s.loaded / Math.max(1, s.n)) * 100));
    const first = gates(st)[0];
    const firstText = s.first
      ? '<div class="clkt-firstb">⭐ Перший корабель' + (k.v.gate && first && k.v.gate.key === first.key ? ' — відчинить першу майстерню' : '') + '</div>'
      : '';
    const can = st.mine ? s.can : 0;
    const all = Math.min(can, rest);
    const plus = Math.min(PLUS, all);
    const off = (n) => (n > 0 && !k.busy ? '' : ' off" aria-disabled="true');
    const art = s.ware || ((wareList(st)[0] || {}).key) || 'jug';
    if (!(s.id in k.seen)) k.seen[s.id] = Date.now();
    const fresh = Date.now() - k.seen[s.id] < FRESH_MS;
    return '<div class="clkt-ship' + (s.first ? ' first' : '') + (all ? ' ready' : '') + (fresh ? ' fresh' : '') + '" data-ship="' + s.id + '">'
      + '<div class="clkt-art">' + api.wareSvg(art, { style: s.style, quality: Math.max(1, s.q), cls: 'clkt-ware', slot: 'tr-' + s.id })
      + pennant(s.port) + '<span class="clkt-n">×' + s.n + '</span></div>'
      + '<div class="clkt-body">' + firstText
      + '<div class="clkt-head"><b>Корабель з ' + esc(s.from) + '</b>'
      + '<span class="clkt-time small muted" title="Скільки ще корабель стоятиме біля Пристані">⏳ <i class="clkt-cd" data-at="' + s.until + '" data-done="відпливає"></i></span></div>'
      + '<div class="clkt-line">просить: <b>' + what + ' ×' + s.n + '</b> <span class="clkt-req">(' + req.filter(Boolean).join(' · ') + ')</span></div>'
      + '<div class="clkt-pay small">за виріб: <b>' + api.dec(s.minutes) + ' хв гри</b> · <b>🌍 +' + s.slavaPer + '</b> Слави' + worldPay(st, api, s) + '</div>'
      + '<div class="clkt-bar"><i style="width:' + share + '%"></i><span>завантажено ' + s.loaded + ' з ' + s.n
      + (s.lux > 0 ? ' · розкішних ' + s.loadedLux + ' з ' + s.lux : '') + '</span></div>'
      + (!st.mine ? '' : all > 0
        ? '<div class="clkt-sum small">' + esc(sumText(api, loadSum(st, s, all))) + '</div><div class="clkt-btns">'
          + '<button type="button" class="primary small clkt-load' + off(all) + '" data-load="' + s.id + '" data-all="1">🚢 Завантажити все (' + all + ')</button>'
          // «+5» — лише коли вантажити є більше за п'ять: при «Завантажити все (3)» він світився й обіцяв те, чого нема.
          + (all > PLUS ? '<button type="button" class="ghost small clkt-plus' + off(plus) + '" data-load="' + s.id + '" data-n="' + PLUS + '">+' + PLUS + '</button>' : '') + '</div>'
        : stuckHtml(st, api, s))
      + '</div></div>';
  }

  /// Корабель, якого нема чим завантажити, стояв 8 год і тримав Пристань повною (QA-3). Картка каже чому й дає дію:
  /// «🏺 Ліпити …» — виріб (і розпис) на коло, звідки якість — підказкою, і «⚓ Відпустити корабель» (крім першого:
  /// сервер його не відпускає — він відчиняє першу майстерню).
  function stuckHtml(st, api, s) {
    const k = st.trade;
    const esc = (x) => api.esc(st, x);
    const luxLeft = Math.max(0, s.lux - s.loadedLux);
    const hint = qHint(api, luxLeft > 0 && luxLeft === s.n - s.loaded ? 4 : s.q);
    const armed = k.arm && k.arm.id === s.id && Date.now() - k.arm.at < ARM_MS;
    const rel = s.first ? ''
      : '<button type="button" class="ghost small clkt-rel' + (armed ? ' armed' : '') + (k.busy ? ' off" aria-disabled="true' : '') + '" data-rel="' + s.id + '"'
        + ' title="Корабель відпливе з тим, що вже на борту (за нього заплачено), а новий прийде за ' + GAP_MIN + ' хв">'
        + (armed ? '⚓ Точно відпустити?' : '⚓ Відпустити корабель') + '</button>';
    const btns = (s.ware ? formBtn(st, api, s) : '') + rel;
    return '<div class="clkt-why small">' + esc(lack(st, api, s)) + (hint ? ' · ' + esc(hint) : '') + '</div>'
      + (btns ? '<div class="clkt-btns">' + btns + '</div>' : '');
  }

  /// «🏺 Ліпити миску (Межигірський фаянс)»: виріб на коло (дія form) і розпис замовлення (дія wear). Уже на колі — позначка.
  function formBtn(st, api, s) {
    const c = st.craft;
    const w = c && c.wares && c.wares.find((x) => x.key === s.ware);
    if (!w || !w.open) return '';
    const esc = (x) => api.esc(st, x);
    const name = wareName(st, s.ware).toLowerCase();
    // Знахідний відмінок жіночого роду: «миску», «макітру», «кахлю» (решта виробів його не міняє).
    const acc = name.split('-').map((p) => p.replace(/а$/, 'у').replace(/я$/, 'ю')).join('-');
    const sty = s.style ? (st.styleList || []).find((x) => x.key === s.style && x.owned) : null;
    const sname = sty ? styleName(st, s.style) : '';
    if (c.ware === s.ware && (!sty || st.wear === s.style)) {
      return '<span class="small clkt-onwheel">🏺 ' + esc(name + (sname ? ' «' + sname + '»' : '')) + ' уже на колі</span>';
    }
    const label = c.ware === s.ware ? '🎨 Поставити розпис «' + sname + '»' : '🏺 Ліпити ' + acc + (sname ? ' (' + sname + ')' : '');
    return '<button type="button" class="ghost small clkt-form" data-form="' + esc(s.ware) + '" data-fstyle="' + esc(sty ? s.style : '') + '">' + esc(label) + '</button>';
  }

  /// Виріб на коло, а коли замовлення просить розпис — і розпис (без нього корабель такого не прийме). Розпис іде й у
  /// form: тоді тост сервера каже «— з розписом «…»» (QA-4), а wear лише мовчки ставить його на коло.
  function formIt(st, api, ware, style) {
    api.sfx('tap');
    api.act(st, 'form', style ? { ware, style } : { ware }).then((r) => {
      if (r && r.ok && style && st.wear !== style) api.act(st, 'wear', { key: style });
    });
  }

  /// Корабель без розпису бере будь-який, а виріб у розписі світу платить більше (Clicker.TradeMinutesFor / SlavaFor) —
  /// кажемо це, лише коли такий розпис у гравця є (QA-2: «+1 Слави», а прийшло +2).
  function worldPay(st, api, s) {
    if (s.style || !(st.styleList || []).some((x) => x.owned && worldStyle(st, x.key))) return '';
    const pay = (st.trade.v && st.trade.v.pay) || PAY;
    return ' <span class="clkt-world">(розпис світу: ' + api.dec(s.minutes * pay.style) + ' хв · +' + (s.slavaPer + pay.styleSlava) + ')</span>';
  }

  function slavaHtml(st, api) {
    const t = st.trade.v;
    const esc = (x) => api.esc(st, x);
    const g = t.gate;
    if (!g) return '<div class="clkt-slava all"><div class="clkt-sline">🌍 Слава <b>' + api.count(t.slava) + '</b> — усі заморські майстерні відчинені</div></div>';
    const p = Math.max(0, Math.min(100, (t.slava / Math.max(1, g.need)) * 100));
    return '<div class="clkt-slava" title="Слава у світі: кожен виріб на кораблі додає її, і вона ніколи не згорає. На ' + api.count(g.need)
      + ' відчиниться «' + esc(g.name) + '» у «Майстерні»">'
      + '<div class="clkt-sline">🌍 Слава <b>' + api.count(t.slava) + '</b> / ' + api.count(g.need) + ' — ' + esc(g.name) + '</div>'
      + '<div class="clkt-sbar"><i style="width:' + p.toFixed(1) + '%"></i></div></div>';
  }

  function openedHtml(st, api) {
    const o = st.trade.opened;
    if (!o || Date.now() - o.at > OPEN_MS) return '';
    return '<div class="clkt-open' + (Date.now() - o.at < FRESH_MS ? ' fresh' : '') + '"><span>🌍 Відчинено «<b>' + api.esc(st, o.name) + '</b>» — заморська майстерня в «Майстерні»</span>'
      + '<button type="button" class="primary small clkt-goshop" data-goshop="' + api.esc(st, o.key) + '">→ у Майстерню</button></div>';
  }

  function paint(st, api) {
    const k = st.trade;
    if (!k || !k.el) return;
    k.dirty = false;
    const esc = (x) => api.esc(st, x);
    // Нема й Толоки — нема й обіцянки: рядок «Кораблі прийдуть» мав би сенс лише поруч із будовами.
    const hide = !k.v && !k.toloka;
    if (k.el.hidden !== hide) k.el.hidden = hide;
    if (hide) return;
    if (!k.v) {
      api.swap(k.el, '<div class="clk-teaser muted small clkt-soon">🚢 Кораблі прийдуть до Пристані на Ворсклі</div>');
      k.el.classList.add('closed');
      delete k.el.dataset.fold;
      k.cds = [];
      return;
    }
    k.el.classList.remove('closed');
    k.el.dataset.fold = 'g.trade';
    const t = k.v;
    const sn = api.serverNow(st);
    const live = t.ships.filter((s) => s.until > sn);
    let html = '<div class="clk-sub clk-fh clkt-title" tabindex="0">🚢 Заморська торгівля<span class="muted small"> · біля Пристані '
      + live.length + ' з 3</span>' + api.info(esc(infoText())) + '</div>'
      + openedHtml(st, api) + slavaHtml(st, api);
    if (live.length) html += '<div class="clkt-ships">' + live.map((s) => shipCard(st, api, s)).join('') + '</div>';
    if (t.next) {
      html += '<div class="muted small clkt-next' + (live.length ? '' : ' empty') + '">' + (live.length ? '' : 'Біля Пристані порожньо · ')
        + 'наступний корабель — за <i class="clkt-cd" data-at="' + t.next + '" data-done="ось-ось"></i></div>';
    } else if (live.length) {
      html += '<div class="muted small clkt-next">Пристань повна — новий прийде за ~' + GAP_MIN + ' хв після того, як котрийсь відпливе чи відпустиш</div>';
    }
    if (api.swap(k.el, html)) k.cds = [...k.el.querySelectorAll('.clkt-cd')];
    countdowns(st, api);
    // Банер «→ у Майстерню» — угорі розділу, а гравець натискав на картку нижче (і повний корабель ще й зникає з-під
    // пальця) — тож раз підгортаємо до банера. Без плавності: наступна перемальовка (відповідь на вантаж) її обриває.
    const o = k.opened;
    if (o && !o.shown && st.tab === 'guild') {
      o.shown = true;
      requestAnimationFrame(() => { const b = k.el && k.el.querySelector('.clkt-open'); if (b) clearNav(st, b); });
    }
  }

  /// Показати елемент у «Селі» одразу під липким рядком переходів (D3, .clkg-nav): scrollIntoView ставить його впритул
  /// до верху, а рядок там і висить (QA-4). Досуваємо на висоту рядка — гортається панель (стіл в один екран) чи сторінка.
  function clearNav(st, el) {
    el.scrollIntoView({ block: 'start' });
    const nav = st.guildNav;
    if (!nav || nav.hidden) return;
    const d = nav.getBoundingClientRect().bottom + 6 - el.getBoundingClientRect().top;
    if (d <= 0) return;
    const pane = el.closest('.clk-pane');
    if (pane && pane.scrollHeight > pane.clientHeight + 1 && /auto|scroll/.test(getComputedStyle(pane).overflowY)) pane.scrollTop -= d;
    else window.scrollBy(0, -d);
  }

  function countdowns(st, api) {
    const k = st.trade;
    if (!k || !k.cds) return;
    const sn = api.serverNow(st);
    for (const el of k.cds) {
      const at = +el.dataset.at;
      const t = at > sn ? left(api, at - sn) : (el.dataset.done || '0:00');
      if (el.textContent !== t) el.textContent = t;
    }
  }

  /// Нотатки на ярликах: «🚢2» на «Селі» — стільки кораблів можна вантажити (перший — гучніше); «🌍» на «Майстерні» —
  /// відчинилась майстерня, а гравець туди ще не заглянув.
  function note(st, api) {
    const k = st.trade;
    const sn = api.serverNow(st);
    const ships = k.v && st.mine ? k.v.ships.filter((s) => s.until > sn && s.can > 0) : [];
    const text = ships.length ? '🚢' + ships.length : '';
    const prio = ships.some((s) => s.first) ? 1 : 3;
    if (k.noteText !== text + prio) { k.noteText = text + prio; api.tabNote(st, 'guild', 'trade', text, prio); }
    const shop = k.opened && !k.opened.seen ? '🌍' : '';
    if (k.shopNote !== shop) { k.shopNote = shop; api.tabNote(st, 'shop', 'trade', shop, 0); }
  }

  // ---------- події ----------

  /// Гейт Слави змінився — відчинилась майстерня, що була гейтом до того. Перший вид лише запам'ятовує.
  function gateMoved(st, api, prev) {
    const k = st.trade;
    const now = k.v.gate ? k.v.gate.key : '';
    if (!prev || prev.key === now) return;
    k.opened = { key: prev.key, name: prev.name, at: Date.now(), seen: st.tab === 'shop' };
    const text = '🌍 Відчинено «' + prev.name + '» — заморська майстерня в «Майстерні»';
    // Вид з новим гейтом приходить раніше за відповідь на вантаж, а чи сказав сервер «відкрито» сам (лише в тості повного
    // корабля), видно тільки з неї — тож поки вантаж летить, тост відкладаємо до відповіді (QA-2: був дубль).
    if (k.busy) k.openToast = text;
    else if (Date.now() - k.saidAt > SAID_MS) api.toast(st, text, 'ok');
    api.feed(st, 'відчинено заморську майстерню «' + prev.name + '»');
    api.sfx('unlock');
    api.sparks(st, null, 26, true, 50, 40);
    api.popAt(st, '🌍 ' + prev.name, 'big', 50, 28);
  }

  /// Новий корабель біля Пристані — тихий тост (без звуку): нотатку «🚢» на ярлику «Села» часто перекриває нотатка гостей.
  /// Перший вид лише запам'ятовує, що вже стоїть (що прийшло без тебе, каже «Поки тебе не було»).
  function arrivals(st, api) {
    const k = st.trade;
    const ids = new Set(k.v.ships.map((s) => s.id));
    if (k.known && st.mine) {
      for (const s of k.v.ships) {
        if (k.known.has(s.id) || s.first) continue;
        const all = Math.min(s.can, s.n - s.loaded);
        const what = (s.ware ? wareName(st, s.ware).toLowerCase() : 'вироби') + (s.style ? ' «' + styleName(st, s.style) + '»' : '');
        api.toast(st, '🚢 До Пристані прийшов корабель з ' + s.from + ': ' + what + ' ×' + s.n
          + (all > 0 ? ' — можна завантажити ' + all : ' — поки нема чим завантажити'), 'ok');
      }
    }
    k.known = ids;
  }

  /// Розпис заморської майстерні привозять з її першим рівнем — тихо з'являвся в «Розписах світу» (QA-3). Тост — у мить
  /// першого рівня; ціль «Далі» — поки розпис не куплено й на нього є гроші (styleGoal).
  function styleCame(st, api) {
    const k = st.trade;
    const ups = st.ups || {};
    if (!gates(st).length) return;
    const levels = {};
    for (const g of gates(st)) levels[g.key] = (ups[g.key] && ups[g.key].level) || 0;
    if (k.levels && st.mine) {
      for (const key of Object.keys(levels)) {
        if (!(levels[key] > 0) || k.levels[key] > 0) continue;
        const sty = (st.styleList || []).find((x) => x.tier === key && !x.owned);
        if (sty) api.toast(st, '🎨 Привезли розпис «' + sty.name + '» — +5 % до всього («Майстерня» → «Розписи світу»)', 'ok');
      }
    }
    k.levels = levels;
  }

  /// Перший рівень щойно відчиненої майстерні — режимом ×1, як ★ (у «макс» перший натиск питав «Точно? −89 %»).
  function buyOne(st) {
    const b = st.modes && st.modes.querySelector('[data-mode="1"]');
    if (b && !b.classList.contains('active')) b.click();
  }

  function goShop(st, api, key) {
    const k = st.trade;
    if (k && k.opened) { k.opened.at = 0; k.opened.seen = true; }
    api.sfx('tap');
    api.showTab(st, 'shop');
    buyOne(st);
    const row = st.el && st.el.querySelector('[data-buy="' + key + '"]');
    if (row && api.lookAt) api.lookAt(st, row);
    else if (row) {
      row.scrollIntoView({ block: 'center', behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
      row.classList.remove('clkt-flash');
      void row.offsetWidth;
      row.classList.add('clkt-flash');
    }
    if (k) { note(st, api); paint(st, api); }
  }

  function load(st, api, btn, e) {
    const k = st.trade;
    if (!human(e) || !st.mine || !k || !k.v || k.busy) return;
    const id = +btn.dataset.load;
    const s = k.v.ships.find((x) => x.id === id);
    if (!s) return;
    const rest = s.n - s.loaded;
    const want = btn.dataset.all ? rest : Math.min(PLUS, rest);
    if (!s.can) return;                    // сіра лише поки летить попередній вантаж
    const full = s.can >= rest && want >= rest;
    k.busy = true;
    paint(st, api);
    api.act(st, 'trade', btn.dataset.all ? { do: 'loadAll', id } : { do: 'load', id, n: PLUS }).then((r) => {
      const kk = st.trade;
      if (!kk) return;
      kk.busy = false;
      const said = !!(r && r.ok && /відкрито/.test(r.message || ''));
      if (said) kk.saidAt = Date.now();
      if (kk.openToast) { if (!said) api.toast(st, kk.openToast, 'ok'); kk.openToast = null; }
      if (r && r.ok) {
        api.sfx(full ? 'rare' : 'paid');
        api.sparks(st, null, full ? 22 : 10, true, 50, 44);
      }
      kk.dirty = true;
      paint(st, api);
    });
  }

  /// «⚓ Відпустити корабель»: перший натиск — «Точно?», другий за ARM_MS — trade { do: 'release' }.
  function release(st, api, btn, e) {
    const k = st.trade;
    if (!human(e) || !st.mine || !k || k.busy) return;
    const id = +btn.dataset.rel;
    if (!(k.arm && k.arm.id === id && Date.now() - k.arm.at < ARM_MS)) {
      k.arm = { id, at: Date.now() };
      api.sfx('tap');
      paint(st, api);
      return;
    }
    k.arm = null;
    k.busy = true;
    paint(st, api);
    api.act(st, 'trade', { do: 'release', id }).then(() => {
      const kk = st.trade;
      if (!kk) return;
      kk.busy = false;
      kk.dirty = true;
      paint(st, api);
    });
  }

  function onClick(st, api, e) {
    const go = e.target.closest('[data-goshop]');
    if (go) { if (human(e)) goShop(st, api, go.dataset.goshop); return; }
    const f = e.target.closest('[data-form]');
    if (f) { if (human(e) && st.mine) formIt(st, api, f.dataset.form, f.dataset.fstyle || ''); return; }
    const rel = e.target.closest('[data-rel]');
    if (rel) { release(st, api, rel, e); return; }
    const b = e.target.closest('[data-load]');
    if (b) load(st, api, b, e);
  }

  // ---------- місце в «Селі» ----------

  /// Розділ — у місці data-slot="ships", якщо «Село» його має (рядок переходів шукає кнопці ціль там); інакше одразу
  /// під Толокою (або під Гостинним двором, поки Толоки нема). Вкладку робить цех, а Толоку й гостей — свої частини,
  /// і кожна могла змонтуватись пізніше, тож місце перевіряємо щоразу.
  function mountBlock(st, api) {
    const k = st.trade;
    if (!k || !st.guildPane) return;
    if (!k.el) {
      k.el = document.createElement('section');
      k.el.className = 'clkg-card clkt';
      k.el.dataset.sec = 'trade';
      k.el.hidden = true;
      k.el.addEventListener('click', (e) => onClick(st, api, e));
    }
    const pane = st.guildPane;
    const slot = pane.querySelector(':scope > [data-slot="ships"]');
    if (slot) { if (k.el.parentElement !== slot) slot.appendChild(k.el); return; }
    const mine = (x) => (x && x.el && x.el.parentElement === pane ? x.el : null);
    const after = mine(st.toloka) || mine(st.guests);
    const placed = k.el.parentElement === pane && (after ? k.el.previousElementSibling === after : pane.firstElementChild === k.el);
    if (!placed) pane.insertBefore(k.el, after ? after.nextSibling : pane.firstChild);
  }

  // ---------- «Далі» ----------

  /// Корабель, на який є що вантажити: перший — найперше (він відчиняє майстерню), решта — поперед покупки верстата
  /// (eta < 0 серед пріоритету 1): глеків у пізній грі завжди на щось вистачає, а виріб з рук — і хвилини гри, і Слава.
  /// Щойно відчинена майстерня без жодного рівня — «Далі» веде в «Майстерню» (v12 §3, «Перший корабель»).
  (window.HClicker.goals = window.HClicker.goals || []).push((st, v, api) => {
    const k = st.trade;
    if (!k || !k.v || !st.mine) return null;
    const ups = st.ups || {};
    const fresh = gates(st).map((g) => g.key).find((key) => ups[key] && ups[key].open !== false && !ups[key].level);
    if (fresh && ups[fresh].price <= Math.max(0, st.shown || 0)) {
      return {
        icon: '<text x="16" y="23" font-size="19" text-anchor="middle">🌍</text>',
        text: 'Відчинено «' + ups[fresh].name + '» — купи перший рівень',
        sub: 'заморська майстерня · ' + (ups[fresh].desc || ''),
        pct: 100, eta: -0.5, tab: 'shop', row: '[data-buy="' + fresh + '"]', prio: 0.5, mode: '1',
      };
    }
    const sn = api.serverNow(st);
    const ships = k.v.ships.filter((s) => s.until > sn && s.can > 0);
    if (!ships.length) return styleGoal(st, api, ups);
    // Перший — найперше; далі той, що відпливає протягом години; інакше — куди влізе найбільше (не «завантаж 2» поруч із 40).
    const fit = (x) => Math.min(x.can, x.n - x.loaded);
    const soon = (x) => (x.until - sn < 3600e3 ? 0 : 1);
    const s = ships.find((x) => x.first) || ships.slice().sort((a, b) => soon(a) - soon(b) || fit(b) - fit(a))[0];
    const n = fit(s);
    return {
      icon: '<text x="16" y="23" font-size="19" text-anchor="middle">🚢</text>',
      text: 'Корабель з ' + s.from + ': завантаж ' + n,
      sub: s.first ? 'перший корабель — відчинить першу майстерню'
        : sumText(api, loadSum(st, s, n)) + ' · відпливе за ' + left(api, s.until - sn),
      pct: (s.loaded / Math.max(1, s.n)) * 100, eta: -0.5, tab: 'guild', row: '[data-ship="' + s.id + '"]', prio: s.first ? 0.5 : 1,
    };
  });

  /// Розпис заморської майстерні, який уже привезли (є перший рівень), а гроші на нього є: +5 % до всього за один натиск.
  function styleGoal(st, api, ups) {
    const pots = Math.max(0, st.shown || 0);
    const sty = (st.styleList || []).find((x) => x.tier && !x.owned && gates(st).some((g) => g.key === x.tier)
      && ups[x.tier] && ups[x.tier].level > 0 && x.price <= pots);
    if (!sty) return null;
    return {
      icon: '<text x="16" y="23" font-size="19" text-anchor="middle">🎨</text>',
      text: 'Розпис «' + sty.name + '» — +5 % до всього',
      sub: 'привезли з «' + ((ups[sty.tier] && ups[sty.tier].name) || sty.tier) + '» · ' + api.potsShort(sty.price),
      pct: 100, eta: -0.4, tab: 'shop', row: '[data-style="' + sty.key + '"]', prio: 1,
    };
  }

  window.HClicker.part({
    id: 'trade',
    order: 64,

    mount(st, api) {
      st.trade = {
        v: null, el: null, cds: [], dirty: false, busy: false, arm: null, seen: {}, opened: null, saidAt: 0, openToast: null, gate: null,
        known: null, levels: null,
        toloka: false, noteText: null, shopNote: null, looked: 0,
      };
      mountBlock(st, api);
    },

    update(st, v, api) {
      const k = st.trade;
      if (!k) return;
      mountBlock(st, api);
      k.toloka = !!v.toloka;
      const t = v.trade;
      if (!t) {
        k.v = null;
      } else {
        k.v = {
          slava: t.slava || 0, next: t.next ? ms(t.next) : 0, full: t.full || 0, gate: t.gate || null, pay: t.pay || null,
          ships: (t.ships || []).map((s) => ({
            id: s.id, port: s.port, from: s.from || s.port, ware: s.ware || '', style: s.style || '', q: s.q || 2, n: s.n || 0,
            lux: s.lux || 0, loaded: s.loaded || 0, loadedLux: s.loadedLux || 0, until: ms(s.until), first: !!s.first,
            can: s.can || 0, minutes: s.minutes || 0, slavaPer: s.slavaPer || 0,
          })),
        };
        gateMoved(st, api, k.gate);
        k.gate = k.v.gate;
        arrivals(st, api);
      }
      styleCame(st, api);
      note(st, api);
      k.dirty = true;
      if (st.tab === 'guild') paint(st, api);
    },

    slow(st, api, sn) {
      const k = st.trade;
      if (!k) return;
      if (k.opened && !k.opened.seen && st.tab === 'shop') { k.opened.seen = true; note(st, api); }
      if (st.tab === 'guild') {
        const armOld = k.arm && Date.now() - k.arm.at > ARM_MS;
        if (armOld) k.arm = null;
        if (k.dirty || armOld) paint(st, api);
        else countdowns(st, api);
      }
      if (!k.v) return;
      // Прийшов час нового корабля чи котрийсь відплив, а гончар лише дивиться: раз питаємо свіжий вид — сервер зведе Пристань.
      const due = Math.min(k.v.next || Infinity, ...k.v.ships.map((s) => s.until));
      if (Number.isFinite(due) && st.mine && api.visible(st) && sn > due + LOOK_GRACE_MS && k.looked !== due) {
        k.looked = due;
        api.act(st, 'look');
      }
    },

    unmount(st, api) {
      const k = st.trade;
      if (k && k.el) k.el.remove();
      if (k) { api.tabNote(st, 'guild', 'trade', '', 5); api.tabNote(st, 'shop', 'trade', '', 5); }
      st.trade = null;
    },
  });
})();
