/*
  Толока Гончарного кола — одинадцяте оновлення (docs/games/specs/clicker-v11.md §2; як зроблено —
  docs/games/specs/clicker-v11-a.md). Частина ядра clicker.js.

  Розділ «🏗 Толока» у вкладці «🤝 Село», одразу під Гостинним двором (clicker-guests.js):
  1) до гривні за весь час — один рядок «Толока відкриється на 1 ₴ за весь час» (вид toloka = null);
  2) рядок дванадцяти будов: готові світяться (підказка — нагорода), поточна пульсує, решта — тіні;
  3) поточна будова: нагорода, смуга етапів, вимоги рядками (виріб, скільки, якість, розпис, «у коморі · піднесли
     друзі», галочка), гроші, години будови (з урахуванням друзів і реліквії) і «Закласти етап» — toloka { do: 'lay' };
     закладений етап — живий відлік до endsAt і «🤝 На толоці були: …»;
  4) фестиваль (будова 11) — «🎉 Почати фестиваль» (toloka { do: 'festival' }), відлік години, «наступний за …».
  Новий готовий етап (приріст done) — тост, звук, рядок у стрічці, нотатка на ярлику «Село» й подія для сцени
  api.sceneToloka(building, stage) (малює пакет сцени; без неї — нічого).

  Друзі на толоці: у хаті друга (clicker-guild.js кличе api.tolokaHouse) — його будова й «🤝 Піднести на толоку»:
  вироби з моєї комори, що підходять до вимог, по 1/5/10 — guild { op: 'toloka', to, key, n }.

  Правила рахує сервер (Impl/ClickerToloka.cs, Impl/ClickerTolokaHelp.cs); тут — лише малюнок і дії.
  Дані: view.toloka = null | { built: [key], stage: null | { building, index, pay, hours, laidAt, endsAt, cut,
    needs: [{ ware, n, q, style, got, have }], helpers: [nick] }, waitBig, done, helped, festival: null | { until,
    next, mult }, allMult }; тексти — st.catalog.toloka { buildings: [{ key, name, icon, big, reward, stages: [{ name,
    pay, hours }] }], helperCut, helpersMax, cutMax, allBonus, smallFrom, bigFrom }.
  Звуки: buy (заклав етап), done (етап готовий), rare (будову збудовано, почався фестиваль), gift (друг прийшов на
  толоку, піднесли другові), tap (розгорнути вибір виробів).

  v12 (docs/games/specs/clicker-v12.md §2): план до кінця, склад і своя праця.
  - План — решта етапів поточної будови й уся наступна (view.toloka.plan), кожен рядок «є · на складі · бракує»
    з «📦 +5 / усе» (toloka { do: 'stock', b, s, i, n }) і «↩ забрати» ({ do: 'unstock', b, s, i }); далі — згорнутий
    підсумок «до кінця Толоки» з totals («треба / на складі / у коморі»), розкішні — окремо й одразу числом.
  - «📦 Віднести все, що підходить» ({ do: 'stockAll' }) питає двічі й каже скільки: те саме правило, що й сервер
    (найсуворіші рядки першими, у кожен — найгірша придатна якість), рахуємо тут же з комори.
  - Своя праця: на закладеному етапі смуга stage.work { n, of, cut }; між видами доростає на кліки, що ще в дорозі
    (st.hands, st.inflight), тож живе з кожною пачкою, а не стрибає раз на вид. Відлік будови — той самий рахунок.
  - «Ліпити X» ставить і розпис рядка (дія wear); «Далі» в простої — найбільша нестача на наступні етапи.
  Дані v12: stage.needs[].depot, stage.work, plan [{ building, index, needs [{ ware, n, q, style, depot }] }],
    totals [{ ware, q, style, n, depot, have }], depot; каталог — workCut, workKeep, workBars, workPerHour.
*/
(() => {
  const human = HGames.ui.human;

  /// Якість, якої просять, — у множині: «(добрі й кращі · «Косівська»)», як у гостей.
  const Q_REQ = ['', '', 'добрі й кращі', 'дзвінкі й кращі', 'лише розкішні'];
  const LOOK_GRACE_MS = 1500;             // годинник етапу вибив — стільки чекаємо, перш ніж спитати свіжий вид
  const ERR_MS = 5000;
  const ARM_MS = 3000;                    // «Віднести все» — друге натискання протягом цього часу (як «Продати все»)
  const STEP = 5;                         // «📦 +5» у рядку плану
  // «Далі»: нестача Толоки — стан довгий (розкішних бракує днями), тож поступається одноразовим діям з тим самим
  // пріоритетом 1 (гість чекає — eta 0, корабель — −0,5, «уже можна купити» — 0) і стає наступною, щойно їх нема.
  // Коли горно працює саме, «Далі» (clicker-craft.js, goalStep) показує саме цілі частин — там вона й живе (QA-2 21.10).
  const LACK_ETA = 0.1;
  // Звичайний темп ручних кліків, кліків за хвилину: «30 000 кліків ≈ 1,5–2 год гри» (v12 §2) — для «≈ N гри до повної».
  const PACE = 300;

  // ---------- дрібниці ----------

  const cat = (st) => (st.catalog && st.catalog.toloka) || null;
  const bdef = (st, key) => { const c = cat(st); return (c && c.buildings.find((b) => b.key === key)) || null; };
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
  const itemsOf = (st) => (st.craft && st.craft.items) || [];
  const fits = (need, it) => it.ware === need.ware && it.q >= need.q && (!need.style || it.style === need.style);

  /// «1 год 24 хв», «40 хв» — години будови словами (дробові — з хвилинами).
  function hrs(h) {
    const m = Math.max(1, Math.round(h * 60));
    const hh = Math.floor(m / 60);
    const mm = m % 60;
    return hh > 0 ? hh + ' год' + (mm ? ' ' + mm + ' хв' : '') : mm + ' хв';
  }

  /// Зекономлений годинник: «40 с», «19 хв», «1 год 57 хв». Униз (QA-3: «3 хв» при 2,5 хв — обіцянка, якої нема).
  function dur(msx) {
    const sec = Math.max(0, Math.floor(msx / 1000));
    if (sec < 60) return sec + ' с';
    const m = Math.floor(sec / 60);
    return m < 60 ? m + ' хв' : Math.floor(m / 60) + ' год' + (m % 60 ? ' ' + (m % 60) + ' хв' : '');
  }

  /// Відлік: «2 дн 4 год», «5 год 12 хв», «12:04».
  function left(api, msLeft) {
    if (msLeft <= 0) return 'ось-ось';
    const m = Math.floor(msLeft / 60000);
    const d = Math.floor(m / 1440);
    const h = Math.floor((m % 1440) / 60);
    if (d > 0) return d + ' дн ' + h + ' год';
    if (h > 0) return h + ' год ' + (m % 60) + ' хв';
    return api.mmss(msLeft);
  }

  /// «глечик ×20 (добрі й кращі · «Косівська»)».
  function needText(st, api, n) {
    const esc = (x) => api.esc(st, x);
    const req = [];
    if (Q_REQ[n.q]) req.push(Q_REQ[n.q]);
    if (n.style) req.push('«' + esc(styleName(st, n.style)) + '»');
    return '<b>' + esc(wareName(st, n.ware).toLowerCase()) + ' ×' + n.n + '</b>'
      + (req.length ? ' <span class="clkl-req">(' + req.join(' · ') + ')</span>' : '');
  }

  const cutPct = (cut) => Math.round((cut || 0) * 100);

  function infoText(st) {
    const c = cat(st);
    const per = Math.round(((c && c.helperCut) || 0.15) * 100);
    const max = (c && c.helpersMax) || 3;
    const cap = Math.round(((c && c.cutMax) || 0.6) * 100);
    const bonus = Math.round(((c && c.allBonus) || 0.05) * 100);
    return 'Толока — будуємо всім селом. Етап закладають глеками й виробами з комори (беремо спершу найгіршу якість, '
      + 'що підходить), а далі він будується годинами — навіть коли тебе нема, і ніякі глеки цього не пришвидшать. '
      + 'Кожен друг із цеху, що підніс на етап хоч один виріб, скорочує його на ' + per + ' % (до ' + max + ' друзів); '
      + '«Родова толока» зі скарбниці — ще до −30 %; разом не більше −' + cap + ' %. Кожна будова — +' + bonus
      + ' % до всього й вічна нагорода; обпал будов не чіпає. Мала толока відкривається з гривні, велика — з червоного золотого. '
      + 'Вироби на будь-який етап наперед можна віднести на склад толоки — він поза коморою: місце в коморі звільняється, '
      + 'лад і обпал склад не чіпають, а закладка бере спершу зі складу. Поки етап будується, кожен твій клік біля кола — '
      + 'цеглина в смугу своєї праці: перша повна смуга скорочує етап ще на ' + pctT(workCutOf(c, 1) * 100)
      + ' %, а далі йдуть наступні — кожна дає менше (дві — −' + pctT(workCutOf(c, 2) * 100) + ' %, '
      + workW(c).bars + ' — −' + pctT(workCutOf(c, workW(c).bars) * 100) + ' %): до −' + Math.round(workW(c).max * 100)
      + ' % лише наблизитись.';
  }

  /// Своя праця — спадні смуги (сервер Clicker.TolokaWorkCutOf, 23.10): max × (1 − keep^смуг), не більше bars смуг.
  const workW = (c) => ({ max: (c && c.workCut) || 0.5, keep: (c && c.workKeep) || 0.7, bars: (c && c.workBars) || 10 });
  function workCutOf(c, bars) {
    const w = workW(c);
    return w.max * (1 - Math.pow(w.keep, Math.max(0, Math.min(w.bars, bars))));
  }
  /// Відсоток для тексту: «15», «25,5», «48,6».
  const pctT = (p) => String(Math.round(p * 10) / 10).replace('.', ',');

  // ---------- малюнок ----------

  /// Рядок дванадцяти будов: готові — яскраві, поточна — пульсує, далі — тіні з назвою в підказці.
  function builtRow(st, api) {
    const c = cat(st);
    const t = st.toloka.v;
    const esc = (x) => api.esc(st, x);
    const built = new Set(t.built);
    const cur = t.stage ? t.stage.building : '';
    return '<div class="clkl-row" role="list">' + c.buildings.map((b) => {
      const on = built.has(b.key);
      const now = b.key === cur;
      const tip = on ? b.name + ' — ' + b.reward : now ? 'Будуємо: ' + b.name : (b.big ? 'Велика толока: ' : 'Далі: ') + b.name;
      return '<span role="listitem" class="clkl-bi' + (on ? ' on' : now ? ' cur' : '') + (b.big ? ' big' : '') + '" title="' + esc(tip) + '">'
        + '<b>' + b.icon + '</b></span>';
    }).join('') + '</div>';
  }

  /// Смуга етапів поточної будови: готові — повні, поточний — світиться (закладений — іншим кольором).
  function stepsHtml(st, api, b, s) {
    const esc = (x) => api.esc(st, x);
    return '<div class="clkl-steps">' + b.stages.map((x, i) => '<span class="clkl-step' + (i < s.index ? ' done' : i === s.index ? (s.endsAt ? ' laid' : ' cur') : '')
      + '" title="' + esc((i + 1) + '. ' + x.name) + '"><i></i></span>').join('') + '</div>';
  }

  const selfFire = (api) => api.storeGet('clk.kiln.self', '0') === '1';

  /// Звідки береться якість рядка: ліпленням її не обереш, вона від горна (і розпису) — кажемо під кнопкою «Ліпити».
  function qHint(api, q) {
    if (q >= 4) return selfFire(api) ? 'розкішні — лише з розписаної партії' : 'розкішні — лише з розписної партії, коли палиш сам';
    if (q === 3 && !selfFire(api)) return 'дзвінкі — лише якщо палиш сам';
    return '';
  }

  /// «🏺 Ліпити макітру → «Косівська»» під вимогою, якої бракує: ставить виріб на коло, а розпис рядка — на партію в
  /// горні (form { ware, style }, 23.10; горно палає — на наступну). Виріб ще не відкритий — кнопки нема; розпису ще
  /// нема — так і кажемо (без нього рядок не прийме); виріб на колі й розпис той самий — лише позначка з розписом.
  /// need — рядок вимоги або ключ виробу.
  function formBtn(st, api, need) {
    const n = typeof need === 'string' ? { ware: need, q: 1, style: '' } : need;
    const c = st.craft;
    if (!st.mine || !c || !c.wares) return '';
    const w = c.wares.find((x) => x.key === n.ware);
    if (!w || !w.open) return '';
    const esc = (x) => api.esc(st, x);
    const name = wareName(st, n.ware).toLowerCase();
    const sname = n.style ? '«' + styleName(st, n.style) + '»' : '';
    const sty = n.style ? (st.styleList || []).find((x) => x.key === n.style) : null;
    if (n.style && !(sty && sty.owned)) {
      return '<span class="small clkl-onwheel clkl-nostyle">🎨 розпису ' + esc(sname) + ' ще нема — без нього рядок не прийме</span>';
    }
    // Звідки якість — кажемо, щойно виріб став на коло (а не під кожним із двадцяти рядків плану): саме тоді гончар
    // і йде палити.
    const hint = qHint(api, n.q);
    if (api.onWheel(st, n.ware, n.style)) {
      return '<span class="small clkl-onwheel">🏺 ' + esc(name) + ' уже на колі' + (sname ? ' · розпис ' + esc(sname) : '')
        + (hint ? '<span class="clkl-qhint"> · ' + esc(hint) + '</span>' : '') + '</span>';
    }
    return '<button type="button" class="ghost small clkl-form" data-lform="' + esc(n.ware) + '" data-lstyle="' + esc(n.style || '') + '"'
      + (hint ? ' title="' + esc(hint) + '"' : '') + '>' + esc(formLabel(st, n)) + '</button>';
  }

  /// «🏺 Ліпити макітру → «Косівська»»: знахідний відмінок жіночого роду (решта виробів його не міняє). Виріб уже на
  /// колі, бракує лише розпису — «🎨 Розпис «Косівська» на партію».
  function formLabel(st, n) {
    if (n.style && st.craft && st.craft.ware === n.ware) return '🎨 Розпис «' + styleName(st, n.style) + '» на партію';
    const acc = wareName(st, n.ware).toLowerCase().split('-').map((p) => p.replace(/а$/, 'у').replace(/я$/, 'ю')).join('-');
    return '🏺 Ліпити ' + acc + (n.style ? ' → «' + styleName(st, n.style) + '»' : '');
  }

  /// «Ліпити X» з рядка: виріб на коло, розпис — на партію в горні (form { ware, style }).
  function formIt(st, api, ware, style) {
    api.act(st, 'form', style ? { ware, style } : { ware });
  }

  // ---------- склад і план (v12) ----------

  /// Скільки з цієї штуки в коморі вільне для складу: відкладене під замовлення сіл, гостей і майстерштук склад не
  /// бере (SparePlan на сервері); під саму Толоку — бере. kept — з виду комори (v12 D1), без нього — уся штука.
  const freeOf = (it) => Math.max(0, it.n - (it.kept ? (it.kept[1] || 0) + (it.kept[2] || 0) : 0));

  /// Чи досяжна будова для складу: мала — з відкриття Толоки, велика — з червоного золотого (чи вже почата).
  function reachable(st, b) {
    const c = cat(st);
    if (!b.big) return true;
    const built = new Set(st.toloka.v.built);
    return (st.total || 0) >= ((c && c.bigFrom) || Infinity) || c.buildings.some((x) => x.big && built.has(x.key));
  }

  /// Підсумок до кінця (totals) з урахуванням піднесеного друзями на поточний незакладений етап: room — скільки ще
  /// можна віднести в цю групу рядків.
  function groups(st) {
    const t = st.toloka.v;
    const s = t.stage;
    const gotCur = (g) => (s && !s.endsAt ? s.needs.filter((n) => n.ware === g.ware && n.q === g.q && n.style === g.style).reduce((a, n) => a + n.got, 0) : 0);
    return t.totals.map((g) => Object.assign({}, g, { room: Math.max(0, g.n - g.depot - gotCur(g)) }));
  }

  /// «Віднести все, що підходить» тим самим правилом, що й сервер (TolokaStockAll): спершу найсуворіші групи —
  /// розкішні з розписом, розкішні, з розписом, решта, — у кожну найгірша придатна якість з вільного в коморі.
  /// Повертає, скільки поїде на склад (n, з них розкішних lux), і чого після цього ще бракуватиме кожній групі (lack).
  function simulate(st) {
    const pool = itemsOf(st).map((it) => ({ ware: it.ware, style: it.style || '', q: it.q, n: freeOf(it) }))
      .sort((a, b) => a.q - b.q);
    const list = groups(st).sort((a, b) => b.q - a.q || (b.style ? 1 : 0) - (a.style ? 1 : 0));
    let n = 0;
    let lux = 0;
    for (const g of list) {
      let want = g.room;
      for (const it of pool) {
        if (want <= 0) break;
        if (it.n <= 0 || !fits(g, it)) continue;
        const take = Math.min(want, it.n);
        it.n -= take;
        want -= take;
        n += take;
        if (g.q >= 4) lux += take;
      }
      g.lack = want;
    }
    return { n, lux, groups: list };
  }

  /// Скільки в моїй коморі того, що підходить рядку (усе, не лише вільне — «є» як у поточному етапі).
  const haveOf = (st, need) => itemsOf(st).filter((it) => fits(need, it)).reduce((a, it) => a + it.n, 0);

  /// Рядок вимоги: «є 77 · на складі 20 · бракує 3», «📦 +5 / усе», «↩ забрати», «Ліпити …». at — куди нести
  /// ({ b, s, i }); n — { ware, n, q, style, got, depot, have }. Склад рахується як піднесене.
  function rowHtml(st, api, n, at, slot) {
    const esc = (x) => api.esc(st, x);
    const got = n.got || 0;
    const depot = n.depot || 0;
    const room = Math.max(0, n.n - got - depot);
    const lack = Math.max(0, room - n.have);
    const ok = lack === 0;
    const where = ['є ' + api.count(n.have)];
    if (got) where.push('піднесли друзі ' + api.count(got));
    if (depot) where.push('на складі ' + api.count(depot));
    if (lack) where.push('<b class="clkl-lack">бракує ' + api.count(lack) + '</b>');
    const ref = esc(at.b + '|' + at.s + '|' + at.i);
    const btns = [];
    if (st.mine && room > 0 && n.have > 0) {
      if (room > STEP && n.have > STEP) btns.push('<button type="button" class="ghost small clkl-stock" data-tst="' + ref + '" data-n="' + STEP + '" title="На склад толоки">📦 +' + STEP + '</button>');
      btns.push('<button type="button" class="ghost small clkl-stock" data-tst="' + ref + '" data-n="' + room + '" title="На склад толоки — скільки підходить">📦 усе</button>');
    }
    if (st.mine && depot > 0) btns.push('<button type="button" class="ghost small clkl-unstock" data-tun="' + ref + '" title="Зі складу назад у комору">↩ забрати</button>');
    return '<div class="clkl-need' + (ok ? ' ok' : '') + (room === 0 ? ' full' : '') + (n.q >= 4 ? ' lux' : '') + '">'
      + '<span class="clkl-nart">' + api.wareSvg(n.ware, { style: n.style, quality: Math.max(1, n.q), cls: 'clkl-ware', slot }) + '</span>'
      + '<div class="clkl-ntxt">' + needText(st, api, n)
      + (n.q >= 4 ? '<span class="small clkl-luxnote">👑 лише з розписної партії вручну</span>' : '')
      + '<span class="muted small">' + where.join(' · ') + '</span>'
      + (btns.length || !ok ? '<span class="clkl-sbtns">' + btns.join('') + (ok ? '' : formBtn(st, api, n)) + '</span>' : '') + '</div>'
      + '<span class="clkl-mark">' + (ok ? '✓' : 'ще ' + api.count(lack)) + '</span></div>';
  }

  /// Дві найближчі будови плану (сервер шле їх поетапно повністю; далі — лише етапи зі складом).
  const nearKeys = (t) => new Set([...new Set(t.plan.map((p) => p.building))].slice(0, 2));

  /// План: решта етапів поточної будови й уся наступна — по будові на згортку. Довге згорнуте одразу (на телефоні це
  /// сотні пікселів), а в заголовку — головне: скільки етапів і скільки ще бракує.
  function planHtml(st, api) {
    const t = st.toloka.v;
    const s = t.stage;
    const esc = (x) => api.esc(st, x);
    const near = nearKeys(t);
    const byB = [];
    for (const p of t.plan) {
      // Поточний незакладений етап уже намальований угорі — з піднесеним друзями й кнопкою «Закласти».
      if (s && !s.endsAt && p.building === s.building && p.index === s.index) continue;
      let g = byB.find((x) => x.key === p.building);
      if (!g) byB.push(g = { key: p.building, list: [] });
      g.list.push(p);
    }
    return byB.map((g, gi) => {
      const b = bdef(st, g.key);
      if (!b) return '';
      let lack = 0;
      let rows = 0;
      const body = g.list.map((p) => {
        const sd = b.stages[p.index] || { name: '', pay: 0, hours: 0 };
        const html = p.needs.map((n, i) => {
          const r = { ware: n.ware, n: n.n, q: n.q, style: n.style || '', got: 0, depot: n.depot || 0, have: haveOf(st, n) };
          lack += Math.max(0, r.n - r.depot - r.have);
          rows++;
          return rowHtml(st, api, r, { b: g.key, s: p.index, i }, 'tp-' + g.key + '-' + p.index + '-' + i);
        }).join('');
        return '<div class="clkl-pst"><div class="small clkl-psth">етап ' + (p.index + 1) + ' з ' + b.stages.length + ' — <b>' + esc(sd.name) + '</b>'
          + '<span class="muted"> · 💰 ' + esc(api.potsShort(sd.pay)) + ' · ⏳ ' + hrs(sd.hours) + '</span></div>'
          + '<div class="clkl-needs">' + html + '</div></div>';
      }).join('');
      const same = s && g.key === s.building;
      // Далі за двома будовами сервер шле лише етапи, де вже щось лежить на складі (QA-3), — щоб «↩ забрати» було звідки.
      const far = !near.has(g.key);
      const stored = far ? g.list.reduce((a, p) => a + p.needs.reduce((x, n) => x + (n.depot || 0), 0), 0) : 0;
      const head = far
        ? '📦 «' + esc(b.name) + '»<span class="muted small"> · на складі ' + api.count(stored) + ' — ' + g.list.length + ' '
          + api.plural(g.list.length, 'етап', 'етапи', 'етапів') + ' наперед</span>'
        : (same ? '⏭ «' + esc(b.name) + '»: ще ' : b.icon + ' Потім «' + esc(b.name) + '»: ')
          + g.list.length + ' ' + api.plural(g.list.length, 'етап', 'етапи', 'етапів') + '<span class="muted small">'
          + (lack ? ' · <b class="clkl-lack">бракує ' + api.count(lack) + '</b>' : ' · усе є') + '</span>';
      // Перша будова плану — розгорнута, поки коротка; наступна, далекі й довгі — згорнуті (вибір гравця пам'ятається).
      const shut = gi > 0 || rows > 6;
      const key = far ? 'far.' + g.key : same ? 'cur' : 'next';
      return '<section class="clkl-plan"' + api.fold('g.tp.' + key, shut) + '>' + api.foldHead(head, 'clkl-ph') + body + '</section>';
    }).join('');
  }

  /// Підсумок «до кінця Толоки»: за кожною групою рядків — треба / на складі / у коморі; розкішні — окремо й першими,
  /// а їхнє число видно й у згорнутому заголовку. Решта будов (поза планом) — одним рядком.
  function sumHtml(st, api, sim) {
    const t = st.toloka.v;
    const c = cat(st);
    const esc = (x) => api.esc(st, x);
    if (!sim.groups.length) return '';
    const built = new Set(t.built);
    const inPlan = nearKeys(t);
    const rest = c.buildings.filter((b) => !built.has(b.key) && !inPlan.has(b.key));
    const far = rest.filter((b) => reachable(st, b));
    const shut = rest.length > far.length;
    // У межах якості — за нестачею: першим те, чого бракує найбільше (QA-3), далі — уже закрите.
    const order = (a, b) => b.q - a.q || b.lack - a.lack || a.ware.localeCompare(b.ware);
    const list = sim.groups.slice().sort(order);
    const lux = list.filter((g) => g.q >= 4);
    const luxN = lux.reduce((a, g) => a + g.n, 0);
    // «Є» до розкішних — склад, піднесене й вільне з комори після «Віднести все» (те саме число, що й у «Далі»).
    const luxHas = lux.reduce((a, g) => a + g.n - g.lack, 0);
    const all = list.reduce((a, g) => a + g.n, 0);
    const row = (g, i) => {
      const r = '<span class="muted small">треба ' + api.count(g.n) + ' · на складі ' + api.count(g.depot) + ' · у коморі ' + api.count(g.have)
        + (g.lack > 0 ? ' · <b class="clkl-lack">бракує ' + api.count(g.lack) + '</b>' : '') + '</span>';
      return '<div class="clkl-need' + (g.lack > 0 ? '' : ' ok') + (g.q >= 4 ? ' lux' : '') + '">'
        + '<span class="clkl-nart">' + api.wareSvg(g.ware, { style: g.style, quality: Math.max(1, g.q), cls: 'clkl-ware', slot: 'ts-' + i }) + '</span>'
        + '<div class="clkl-ntxt">' + needText(st, api, g) + r + (g.lack > 0 ? '<span class="clkl-sbtns">' + formBtn(st, api, g) + '</span>' : '') + '</div>'
        + '<span class="clkl-mark">' + (g.lack > 0 ? 'ще ' + api.count(g.lack) : '✓') + '</span></div>';
    };
    const luxHead = luxN ? '<span class="clkl-luxsum">👑 розкішних: ' + api.count(luxHas) + ' з ' + api.count(luxN) + '</span>' : '';
    const head = '📋 До кінця ' + (shut ? 'малої толоки' : 'Толоки') + '<span class="muted small"> · ' + api.count(all) + ' '
      + api.plural(all, 'виріб', 'вироби', 'виробів') + '</span>' + (luxHead ? ' ' + luxHead : '');
    let body = '';
    if (far.length) {
      body += '<div class="muted small clkl-far">Ще будови: ' + far.map((b) => b.icon + ' ' + esc(b.name) + ' (' + b.stages.length + ' '
        + api.plural(b.stages.length, 'етап', 'етапи', 'етапів') + ')').join(', ') + ' — їхні вимоги вже тут</div>';
    }
    if (shut) body += '<div class="muted small clkl-far">Велика толока — з червоного золотого; її вимог тут ще нема</div>';
    if (lux.length) {
      body += '<div class="small clkl-luxhead">👑 Розкішні · ' + api.count(luxHas) + ' з ' + api.count(luxN) + ' — лише з розписної партії вручну</div>'
        + '<div class="clkl-needs">' + lux.map(row).join('') + '</div>';
    }
    const plain = list.filter((g) => g.q < 4);
    if (plain.length) {
      body += (lux.length ? '<div class="small clkl-luxhead plain">Решта</div>' : '')
        + '<div class="clkl-needs">' + plain.map((g, i) => row(g, lux.length + i)).join('') + '</div>';
    }
    return '<section class="clkl-plan clkl-sum"' + api.fold('g.tp.sum', true) + '>' + api.foldHead(head, 'clkl-ph clkl-sumh') + body + '</section>';
  }

  /// Склад толоки: скільки там уже лежить і «Віднести все, що підходить» (два натиски, з числом).
  function depotHtml(st, api, sim) {
    const k = st.toloka;
    const t = k.v;
    const armed = k.armAt > Date.now();
    const n = sim.n;
    const btn = '<button type="button" class="ghost clkl-stockall' + (armed ? ' armed' : '') + '"' + (st.mine && n > 0 && !k.busy ? '' : ' disabled') + '>'
      + (armed ? '📦 Точно? Віднести ' + api.count(n) + ' ' + api.plural(n, 'виріб', 'вироби', 'виробів') + ' — ще раз'
        : '📦 Віднести все, що підходить' + (n > 0 ? ' · ' + api.count(n) : '')) + '</button>';
    const note = n > 0
      ? 'вільне з комори — на етапи наперед' + (sim.lux ? ', з них розкішних ' + api.count(sim.lux) : '')
      : 'у коморі нема вільного, що підходить етапам наперед';
    const info = 'Склад — поза коморою: місце в коморі звільняється, лад зі складу не продає, обпал склад не чіпає. Закладка '
      + 'етапу бере спершу зі складу, потім з комори. «Усе, що підходить» несе спершу в найсуворіші рядки (розкішні з '
      + 'розписом, розкішні, з розписом, решта), у кожен — найгіршу придатну якість; відкладеного під замовлення не бере. '
      + '«↩ забрати» повертає рядок у комору, якщо там є місце.';
    // QA 21.10: «на складі 59» читалось як «59 на цей етап», а 44 з них лежали на наступних будовах — кажемо, скільки
    // на найближчий етап (той, що закладатимеш наступним), а скільки далі.
    const s = t.stage;
    const nearP = s && !s.endsAt ? s : t.plan[0];
    const near = nearP ? nearP.needs.reduce((a, x) => a + (x.depot || 0), 0) : 0;
    const word = s && !s.endsAt ? 'на цей етап' : 'на наступний етап';
    const split = !t.depot ? '' : near === t.depot ? ' <span class="muted">(усе — ' + word + ')</span>'
      : ' <span class="muted">(' + (near ? api.count(near) + ' — ' + word + ', ' : '') + api.count(t.depot - near) + ' — далі)</span>';
    return '<div class="clkl-depot"><div class="small clkl-dline">📦 Склад толоки · ' + (t.depot ? 'на складі <b>' + api.count(t.depot) + '</b>' + split : 'порожній')
      + api.info(api.esc(st, info)) + '</div>'
      + '<div class="clkl-btns">' + btn + '<span class="muted small clkl-miss">' + note + '</span></div>'
      + (k.went ? '<div class="small clkl-went">' + api.esc(st, k.went) + '</div>' : '') + '</div>';
  }

  /// Своя праця на закладеному етапі: числа й смугу ставить work() — щопачки, без перемальовування розділу.
  function workHtml(st, api, s) {
    const c = cat(st);
    return '<div class="clkl-work"><div class="clkl-wline">🧱 Своя праця · смуга <b class="clkl-wbn"></b> з ' + workW(c).bars
      + ' · <b class="clkl-wn"></b> з ' + api.count(s.work.of) + ' · <b class="clkl-wcut"></b></div><div class="clkl-wbar"><i></i></div>'
      + '<div class="clkl-wsave">⏱ <span class="clkl-wsv"></span></div>'
      + '<div class="muted small">кожен клік біля кола — цеглина; кожна наступна смуга скорочує менше — до −'
      + Math.round(workW(c).max * 100) + ' % лише наблизитись · <span class="clkl-wnext"></span></div></div>';
  }

  /// Своя праця зараз: з виду плюс кліки, що вже полетіли чи ще чекають пачки (сервер їх зарахує тим самим spin).
  /// Поки пачка в дорозі, смуга назад не відступає — інакше між пачкою й видом вона б тремтіла.
  function workNow(st) {
    const k = st.toloka;
    const s = k.v && k.v.stage;
    const w = s && s.endsAt && s.work;
    if (!w) return null;
    const pend = ((st.hands && st.hands.length) || 0) + (st.inflight || 0);
    const key = s.building + ':' + s.index;
    const c = cat(st);
    const W = workW(c);
    const top = w.of * W.bars;
    let n = Math.min(top, w.n + pend);
    if (k.wKey === key && pend > 0 && n < k.wShown) n = k.wShown;
    k.wKey = key;
    k.wShown = n;
    // Смуга, що зараз наповнюється (1…bars), і скільки в ній; остання повна — так і лишається повною.
    const done = n >= top;
    const bar = done ? W.bars : Math.floor(n / w.of) + 1;
    const inBar = done ? w.of : n - (bar - 1) * w.of;
    const frac = Math.min(1, inBar / w.of);
    const cut = workCutOf(c, n / w.of);
    // Відлік будови — з тим самим доростом: endsAt з виду вже має w.n, додаємо лише різницю.
    const hours = s.hours * 3600000 * (1 - (s.cut || 0));
    const ends = s.endsAt - hours * (cut - workCutOf(c, w.n / w.of));
    // saved — скільки годинника своя праця вже зрізала (після друзів і реліквії, як і на сервері); barEnd — скільки буде
    // зрізано, коли наповниться ця смуга; next — що додасть ця смуга понад попередні.
    const barEnd = hours * workCutOf(c, bar);
    const next = hours * (workCutOf(c, bar) - workCutOf(c, bar - 1));
    return { n: inBar, of: w.of, bar, bars: W.bars, done, frac, cut, ends, saved: hours * cut, barEnd, next,
      nextPct: (workCutOf(c, bar) - workCutOf(c, bar - 1)) * 100 };
  }

  function work(st, api) {
    const k = st.toloka;
    const el = k.workEl;
    if (!el || !el.isConnected) return;
    const w = workNow(st);
    if (!w) return;
    const tn = api.count(Math.floor(w.n));
    // Перші сотні кліків — частки відсотка («−0,4 %», «−0,03 %»): «−0 %» після хвилини праці виглядало б, наче вона не йде.
    // Униз, як і хвилини: «−15 %» лише на повній смузі, а не вже на 35 634 з 36 000 (QA-3).
    const pct = w.cut * 100 + 1e-9;
    const any = w.bar > 1 || w.n > 0;
    const tp = (pct >= 10 || !any ? String(Math.floor(pct)) : pct >= 0.1 ? String(Math.floor(pct * 10) / 10)
      : String(Math.max(0.01, Math.floor(pct * 100) / 100))).replace('.', ',');
    const tc = (any ? '−' : '') + tp + ' %';
    // QA 21.10: «4 581 клік — і що?» Відсоток не відчувається, хвилини — так: скільки годинника вже зрізано й скільки дасть смуга;
    // QA-3 — і скільки ще клікати до повної.
    const play = '≈ ' + dur(((w.of - w.n) / PACE) * 60000) + ' гри до кінця смуги';
    const ts = w.done ? 'зекономлено ' + dur(w.saved) + ' — усі смуги повні'
      : (any ? 'зекономлено ' + dur(w.saved) + ', з цією смугою — ' + dur(w.barEnd) : 'перша смуга зріже ' + dur(w.barEnd) + ' будови') + ' · ' + play;
    const tx = w.done ? 'більше праця не скоротить' : 'ця смуга: ще −' + pctT(w.nextPct) + ' % (' + dur(w.next) + ')';
    const tb = String(w.bar);
    if (k.wn && k.wn.textContent !== tn) k.wn.textContent = tn;
    if (k.wc && k.wc.textContent !== tc) k.wc.textContent = tc;
    if (k.ws && k.ws.textContent !== ts) k.ws.textContent = ts;
    if (k.wx && k.wx.textContent !== tx) k.wx.textContent = tx;
    if (k.wbn && k.wbn.textContent !== tb) k.wbn.textContent = tb;
    if (k.wb) {
      const tr = 'scaleX(' + w.frac.toFixed(4) + ')';
      if (k.wb.style.transform !== tr) k.wb.style.transform = tr;
    }
    el.classList.toggle('full', w.done);
  }

  function needsHtml(st, api, s) {
    const esc = (x) => api.esc(st, x);
    // v12: склад рахується як піднесене — «є 77 · на складі 20 · бракує 3».
    const rows = s.needs.map((n, i) => rowHtml(st, api, n, { b: s.building, s: s.index, i }, 'tn-' + i));
    const rich = (st.shown || 0) >= s.pay;
    rows.push('<div class="clkl-need' + (rich ? ' ok' : '') + '"><span class="clkl-nart clkl-emo">💰</span>'
      + '<div class="clkl-ntxt"><b>' + esc(api.potsShort(s.pay)) + '</b><span class="muted small">гроші на етап</span></div>'
      + '<span class="clkl-mark">' + (rich ? '✓' : '') + '</span></div>');
    const cut = cutPct(s.cut);
    rows.push('<div class="clkl-need hours"><span class="clkl-nart clkl-emo">⏳</span>'
      + '<div class="clkl-ntxt"><b>' + hrs(s.hours * (1 - (s.cut || 0))) + '</b><span class="muted small">'
      + (cut > 0 ? 'будується замість ' + hrs(s.hours) + ' — друзі й рід скоротили на ' + cut + ' %' : 'будується годинником — глеками не пришвидшити')
      + ' · смугами своїх кліків під час будови — ще до −' + Math.round(workW(cat(st)).max * 100) + ' %'
      + '</span></div><span class="clkl-mark"></span></div>');
    return '<div class="clkl-needs">' + rows.join('') + '</div>';
  }

  /// Чого бракує, щоб закласти етап (порожньо — можна).
  function lacks(st, api, s) {
    const out = [];
    for (const n of s.needs) {
      const want = Math.max(0, n.n - n.got - (n.depot || 0));
      if (n.have < want) out.push(wareName(st, n.ware).toLowerCase() + ' — ще ' + (want - n.have));
    }
    if ((st.shown || 0) < s.pay) out.push('грошей — ще ' + api.potsShort(s.pay - (st.shown || 0)));
    return out;
  }

  function helpersLine(st, api, s, laid) {
    const esc = (x) => api.esc(st, x);
    const c = cat(st);
    const per = ((c && c.helperCut) || 0.15) * 100;
    const max = (c && c.helpersMax) || 3;
    if (!s.helpers.length) {
      return '<div class="small clkl-help clkl-hint">🤝 Друзі з цеху можуть піднести вироби зі своїх комор — кожен скоротить '
        + (laid ? 'будову' : 'етап') + ' на <b>' + Math.round(per) + ' %</b> (до ' + max + ' друзів)</div>';
    }
    const off = Math.round(Math.min(max, s.helpers.length) * per);
    return '<div class="small clkl-help">🤝 На толоці ' + (laid ? 'були' : 'вже були') + ': <b>' + esc(s.helpers.join(', ')) + '</b> '
      + '<span class="clkl-cut">(−' + off + ' %)</span></div>';
  }

  function stageHtml(st, api) {
    const t = st.toloka.v;
    const s = t.stage;
    const esc = (x) => api.esc(st, x);
    const b = bdef(st, s.building);
    if (!b) return '';
    const sd = b.stages[s.index] || { name: '' };
    const c = cat(st);
    const bonus = Math.round(((c && c.allBonus) || 0.05) * 100);
    let html = '<div class="clkl-cur">'
      + '<span class="clkl-ico" aria-hidden="true">' + b.icon + '</span>'
      + '<div class="clkl-cbody"><b class="clkl-name">' + esc(b.name) + '</b>'
      + '<span class="muted small clkl-reward">🎁 ' + esc(b.reward) + ' · +' + bonus + ' % до всього</span></div></div>'
      + stepsHtml(st, api, b, s)
      + '<div class="clkl-stage">етап ' + (s.index + 1) + ' з ' + b.stages.length + ' — <b>' + esc(sd.name) + '</b></div>';
    if (s.endsAt) {
      // Кінець будови рухає своя праця щопачки — тож відлік і смуга часу беруть його з k.v (data-live), а не з
      // розмітки: інакше кожна пачка кліків перемальовувала б увесь розділ.
      html += '<div class="clkl-build"><div class="clkl-bline">🔨 Будується · ще <i class="clkl-cd" data-live="ends" data-done="ось-ось"></i></div>'
        + '<div class="clkl-tbar"><i data-live="bar"></i></div>'
        + (s.work ? workHtml(st, api, s) : '') + '</div>'
        + helpersLine(st, api, s, true);
      if (!b.stages[s.index + 1]) {
        html += '<div class="muted small">Це останній етап: достроїться — і «' + esc(b.name) + '» стоятиме назавжди.</div>';
      }
    } else {
      const miss = lacks(st, api, s);
      html += needsHtml(st, api, s)
        + helpersLine(st, api, s, false)
        + '<div class="clkl-btns"><button type="button" class="primary clkl-lay"' + (st.mine && !miss.length ? '' : ' disabled') + '>🏗 Закласти етап</button>'
        + (miss.length ? '<span class="muted small clkl-miss">бракує: ' + esc(miss.join('; ')) + '</span>' : '<span class="small clkl-ready">усе є — закладай!</span>')
        + '</div>';
    }
    // v12: склад і план до кінця — і на закладеному етапі, і на тому, що ще збирається.
    if (t.totals.length) {
      const sim = simulate(st);
      html += depotHtml(st, api, sim) + planHtml(st, api) + sumHtml(st, api, sim);
    }
    return html;
  }

  function festivalHtml(st, api) {
    const f = st.toloka.v.festival;
    if (!f) return '';
    const sn = api.serverNow(st);
    const mult = api.dec(f.mult || 2);
    if (f.until > sn) {
      return '<div class="clkl-fest on">🎉 <b>Фестиваль гуляє!</b> ×' + mult + ' до всього ще <i class="clkl-cd" data-at="' + f.until + '" data-done="ось-ось"></i></div>';
    }
    if (f.next > sn) {
      return '<div class="clkl-fest muted small">🎉 Наступний фестиваль — за <i class="clkl-cd" data-at="' + f.next + '" data-done="ось-ось"></i></div>';
    }
    return '<div class="clkl-fest"><button type="button" class="primary clkl-festgo"' + (st.mine ? '' : ' disabled') + '>🎉 Почати фестиваль</button>'
      + '<span class="muted small">година ×' + mult + ' до всього, раз на добу</span></div>';
  }

  function paint(st, api) {
    const k = st.toloka;
    if (!k || !k.el) return;
    k.dirty = false;
    const c = cat(st);
    const esc = (x) => api.esc(st, x);
    if (!k.v) {
      // До гривні — лише рядок-обіцянка (і лише там, де «Село» вже відкрите: розділ живе в його панелі).
      const from = (c && c.smallFrom) || 1e15;
      api.swap(k.el, '<div class="clk-teaser muted small clkl-soon">🏗 Толока відкриється на ' + esc(api.potsShort(from)) + ' за весь час'
        + (/₴/.test(api.potsShort(from)) ? ' <span class="clkl-hrn">(₴ — гривня: тисяча трильйонів глеків)</span>' : '') + '</div>');
      k.el.classList.add('closed');
      delete k.el.dataset.fold;                // обіцянка без заголовка — згортати нема чого (і нема за що взятись)
      k.cds = [];
      return;
    }
    k.el.classList.remove('closed');
    k.el.dataset.fold = 'g.toloka';
    if (!c) {
      api.swap(k.el, api.foldHead('🏗 Толока') + '<div class="muted small">Село сходиться на толоку…</div>');
      return;
    }
    const t = k.v;
    const bonus = Math.round((t.allMult - 1) * 100);
    let html = '<div class="clk-sub clk-fh clkl-title" tabindex="0">🏗 Толока<span class="muted small"> · збудовано ' + t.built.length + ' з ' + c.buildings.length
      + (bonus > 0 ? ' · +' + bonus + ' % до всього' : '') + '</span>' + api.info(esc(infoText(st))) + '</div>'
      + builtRow(st, api);
    if (t.stage) html += stageHtml(st, api);
    else if (t.waitBig) html += '<div class="clk-teaser small clkl-wait">⚓ Мала толока збудована. Велика толока почнеться з червоного золотого</div>';
    else if (t.built.length >= c.buildings.length) html += '<div class="clkl-all">🏺 Усі ' + c.buildings.length + ' будов стоять — ти Толочанин світу!</div>';
    html += festivalHtml(st, api);
    if (k.err && Date.now() - k.err.at < ERR_MS) html += '<div class="small clkl-err">' + esc(k.err.text) + '</div>';
    if (api.swap(k.el, html)) {
      k.cds = [...k.el.querySelectorAll('.clkl-cd, .clkl-tbar i')];
      k.workEl = k.el.querySelector('.clkl-work');
      k.wn = k.workEl && k.workEl.querySelector('.clkl-wn');
      k.wc = k.workEl && k.workEl.querySelector('.clkl-wcut');
      k.wb = k.workEl && k.workEl.querySelector('.clkl-wbar i');
      k.ws = k.workEl && k.workEl.querySelector('.clkl-wsv');
      k.wx = k.workEl && k.workEl.querySelector('.clkl-wnext');
      k.wbn = k.workEl && k.workEl.querySelector('.clkl-wbn');
      // Нова розмітка — смуга стає на своє місце одразу, без наїзду з нуля (плавно вона лише доростає).
      if (k.wb) { k.wb.style.transition = 'none'; work(st, api); void k.wb.offsetWidth; k.wb.style.transition = ''; }
    }
    work(st, api);
    countdowns(st, api);
    // Бачив новий етап — нотатка на ярлику більше не кличе.
    if (st.tab === 'guild' && k.unseen) { k.unseen = false; note(st, api); }
  }

  function countdowns(st, api) {
    const k = st.toloka;
    if (!k || !k.cds) return;
    const sn = api.serverNow(st);
    const s = k.v && k.v.stage;
    const w = workNow(st);
    const ends = w ? w.ends : (s && s.endsAt) || 0;
    for (const el of k.cds) {
      if (el.dataset.live === 'bar') {
        const p = s && ends > s.laidAt ? Math.max(0, Math.min(1, (sn - s.laidAt) / (ends - s.laidAt))) : 1;
        el.style.transform = 'scaleX(' + p.toFixed(4) + ')';
        continue;
      }
      if (el.dataset.live === 'ends') {
        const tx = ends > sn ? left(api, ends - sn) : (el.dataset.done || '');
        if (el.textContent !== tx) el.textContent = tx;
        continue;
      }
      if (el.dataset.from) {
        const from = +el.dataset.from;
        const to = +el.dataset.to;
        const p = to > from ? Math.max(0, Math.min(1, (sn - from) / (to - from))) : 1;
        el.style.transform = 'scaleX(' + p.toFixed(4) + ')';
        continue;
      }
      const at = +el.dataset.at;
      const tx = at > sn ? left(api, at - sn) : (el.dataset.done || '');
      if (el.textContent !== tx) el.textContent = tx;
    }
  }

  // ---------- ярлик, події ----------

  function canLay(st, api) {
    const s = st.toloka && st.toloka.v && st.toloka.v.stage;
    return !!s && !s.endsAt && !lacks(st, api, s).length;
  }

  function festReady(st, api) {
    const f = st.toloka && st.toloka.v && st.toloka.v.festival;
    if (!f) return false;
    const sn = api.serverNow(st);
    return f.until <= sn && f.next <= sn;
  }

  function note(st, api) {
    const k = st.toloka;
    let text = '';
    let prio = 5;
    if (k.v) {
      // Щойно готовий етап — одноразова новина: вище за постійні нотатки (гості, віз), доки гончар не глянув у «Село».
      if (k.unseen) { text = '🏗✓'; prio = 0; }
      else if (canLay(st, api)) { text = '🏗'; prio = 2; }
      else if (festReady(st, api)) { text = '🎉'; prio = 2; }
    }
    if (k.noteText === text) return;
    k.noteText = text;
    api.tabNote(st, 'guild', 'toloka', text, prio);
  }

  /// Новий готовий етап (приріст done) — тост, звук, стрічка, нотатка й сцена. Перший вид лише запам'ятовує.
  function stageDone(st, api, prev) {
    const k = st.toloka;
    const t = k.v;
    if (k.done == null || t.done <= k.done) { k.done = t.done; return; }
    k.done = t.done;
    if (!st.mine || !prev) return;
    const b = bdef(st, prev.building);
    if (!b) return;
    const sd = b.stages[prev.index] || { name: '' };
    const whole = t.built.includes(b.key);
    const text = whole
      ? b.icon + ' Толока: «' + b.name + '» збудовано! ' + b.reward
      : b.icon + ' Толока: «' + sd.name + '» готово — можна закладати «' + ((b.stages[prev.index + 1] || {}).name || '') + '»';
    api.toast(st, text, 'ok');
    api.feed(st, text);
    api.sfx(whole ? 'rare' : 'done');
    api.sparks(st, null, whole ? 28 : 16, true, 50, 40);
    if (whole) api.popAt(st, b.icon + ' ' + b.name, 'big', 50, 28);
    k.unseen = true;
    if (typeof api.sceneToloka === 'function') {
      try { api.sceneToloka(b.key, prev.index); } catch (e) { console.error('[clicker:toloka] сцена', e); }
    }
  }

  /// Хтось із друзів прийшов на толоку (новий нік у помічниках того самого етапу) — тост і дзвіночок.
  function helpersCame(st, api) {
    const k = st.toloka;
    const s = k.v.stage;
    const sig = s ? s.building + ':' + s.index : '';
    const list = s ? s.helpers : [];
    if (k.helpSig === sig && k.helpers && st.mine) {
      const fresh = list.filter((x) => !k.helpers.includes(x));
      // Цех (clicker-guild.js) уже каже «🤝 X підніс(ла) на твою толоку…» тостом, стрічкою й дзвіночком — тоді тут
      // мовчимо (QA-3: на одну поміч — два тости). Самі — лише коли вид цеху ще не знає, хто що приніс.
      if (fresh.length && !(st.guild && st.guild.gotKnown)) {
        const c = cat(st);
        const per = Math.round(((c && c.helperCut) || 0.15) * 100);
        const text = '🤝 ' + fresh.join(', ') + ' ' + (fresh.length > 1 ? 'прийшли' : 'прийшов(ла)') + ' на толоку — будова на ' + per * fresh.length + ' % коротша';
        api.toast(st, text, 'ok');
        api.feed(st, text);
        api.sfx('gift');
      }
    }
    k.helpSig = sig;
    k.helpers = list.slice();
  }

  // ---------- дії ----------

  function lay(st, api, e) {
    const k = st.toloka;
    if (!human(e) || !st.mine || !k || k.busy) return;
    k.busy = true;
    const btn = k.el.querySelector('.clkl-lay');
    if (btn) btn.disabled = true;
    api.act(st, 'toloka', { do: 'lay' }).then((r) => {
      const kk = st.toloka;
      if (!kk) return;
      kk.busy = false;
      if (r && r.ok) {
        api.sfx('buy');
        api.sparks(st, st.fx, 18, true, 50, 44);
        kk.err = null;
      } else if (r && r.message) kk.err = { at: Date.now(), text: r.message };
      paint(st, api);
    });
  }

  function festival(st, api, e) {
    const k = st.toloka;
    if (!human(e) || !st.mine || !k || k.busy) return;
    k.busy = true;
    api.act(st, 'toloka', { do: 'festival' }).then((r) => {
      const kk = st.toloka;
      if (!kk) return;
      kk.busy = false;
      if (r && r.ok) {
        api.sfx('rare');
        api.sparks(st, null, 30, true, 50, 36);
        api.popAt(st, '🎉 Гуляй, майдане!', 'big', 50, 28);
      }
      paint(st, api);
    });
  }

  /// Наступний етап, поки поточний будується (лише показати — піднести наперед не можна). own — своя толока: «у тебе є»
  /// дає сервер; у друга — рахуємо з моєї комори тим самим правилом, що й «Піднести».
  function nextHtml(st, api, nx, cur, own) {
    const esc = (x) => api.esc(st, x);
    const b = bdef(st, nx.building);
    if (!b) return '';
    const mine = itemsOf(st);
    const rows = (nx.needs || []).map((n, i) => {
      const have = own && n.have != null ? n.have : mine.filter((it) => fits(n, it)).reduce((a, it) => a + it.n, 0);
      // QA-3: у друга дещо вже лежить на складі під цей рядок (next.needs[].depot) — «бракує» без нього брехало.
      const depot = n.depot || 0;
      const lack = Math.max(0, n.n - depot - have);
      const ok = lack === 0;
      return '<div class="clkl-need' + (ok ? ' ok' : '') + '">'
        + '<span class="clkl-nart">' + api.wareSvg(n.ware, { style: n.style, quality: Math.max(1, n.q), cls: 'clkl-ware', slot: 'tx-' + (own ? 'o' : 'f') + i }) + '</span>'
        + '<div class="clkl-ntxt">' + needText(st, api, n) + '<span class="muted small">'
        + (depot ? (own ? 'на складі ' : 'у друга на складі ') + api.count(depot) + ' · ' : '') + (own ? 'у тебе є ' : 'у твоїй коморі ') + api.count(have)
        + (ok ? '' : ' · <b class="clkl-lack">бракує ' + api.count(lack) + '</b>') + '</span>'
        + (own && !ok ? formBtn(st, api, n) : '') + '</div>'
        + '<span class="clkl-mark">' + (ok ? '✓' : 'ще ' + api.count(lack)) + '</span></div>';
    });
    const rich = own && (st.shown || 0) >= nx.pay;
    rows.push('<div class="clkl-need' + (rich ? ' ok' : '') + '"><span class="clkl-nart clkl-emo">💰</span>'
      + '<div class="clkl-ntxt"><b>' + esc(api.potsShort(nx.pay)) + '</b><span class="muted small">' + (own ? 'гроші на етап' : 'гроші на етап — його') + '</span></div>'
      + '<span class="clkl-mark">' + (rich ? '✓' : '') + '</span></div>');
    rows.push('<div class="clkl-need hours"><span class="clkl-nart clkl-emo">⏳</span>'
      + '<div class="clkl-ntxt"><b>' + hrs(nx.hours) + '</b><span class="muted small">будується (друзі й рід скоротять)</span></div><span class="clkl-mark"></span></div>');
    const head = (b.key !== (cur && cur.key) ? b.icon + ' ' + esc(b.name) + ' — ' : '') + 'етап ' + (nx.index + 1) + ' з ' + b.stages.length + ' «' + esc(nx.name) + '»';
    // У друга (B8): це лише показ — зараз етап приймає вимоги поточного, а не ці. Назва — у згортці над цим.
    return '<div class="clkl-next">' + (own ? '<div class="clk-sub small">⏭ Наступний етап: ' + head + '</div>'
      : '<div class="muted small">' + (b.key !== (cur && cur.key) ? b.icon + ' ' + esc(b.name) + ' · ' : '')
        + 'ці вироби приймуть, коли друг почне збирати цей етап</div>')
      + '<div class="clkl-needs">' + rows.join('') + '</div></div>';
  }

  /// Скільки лежить на складі під кожну будову: план містить усі етапи, де щось лежить (ближні — повністю).
  function depotBy(t) {
    const out = new Map();
    for (const p of t.plan) {
      const n = p.needs.reduce((a, x) => a + (x.depot || 0), 0);
      if (n) out.set(p.building, (out.get(p.building) || 0) + n);
    }
    return out;
  }

  /// «Віднести все» поїхало — кажемо, куди (QA-3: «287 на склад» — а де вони?): «44 — «Глек на майдані», …».
  function wentWhere(st, api) {
    const k = st.toloka;
    const was = k.wentFrom;
    if (!was || k.v.depot === was.total) return;
    k.wentFrom = null;
    const now = depotBy(k.v);
    const parts = [];
    for (const [key, n] of now) {
      const d = n - (was.by.get(key) || 0);
      const b = bdef(st, key);
      if (d > 0 && b) parts.push({ d, text: api.count(d) + ' — «' + b.name + '»' });
    }
    k.went = parts.length ? '📦 Віднесли: ' + parts.sort((a, b) => b.d - a.d).map((x) => x.text).join(', ') : '';
  }

  /// Склад толоки: віднести / забрати / усе, що підходить. Тост дає сервер (core.js показує message відповіді) —
  /// тут лише звук, блок кнопок на час дії й свіжий малюнок.
  function depotAct(st, api, e, payload) {
    const k = st.toloka;
    if (!human(e) || !st.mine || !k || k.busy) return;
    k.busy = true;
    k.went = '';
    // Склад до «Віднести все» — щоб, коли прийде новий вид, сказати, куди що пішло (вид і відповідь — різними кадрами).
    k.wentFrom = payload.do === 'stockAll' ? { total: k.v.depot, by: depotBy(k.v) } : null;
    for (const b of k.el.querySelectorAll('.clkl-stock, .clkl-unstock, .clkl-stockall')) b.disabled = true;
    api.act(st, 'toloka', payload).then((r) => {
      const kk = st.toloka;
      if (!kk) return;
      kk.busy = false;
      kk.armAt = 0;
      if (r && r.ok) api.sfx(payload.do === 'unstock' ? 'tap' : 'buy');
      else kk.wentFrom = null;
      kk.dirty = true;
      paint(st, api);
    });
  }

  function onClick(st, api, e) {
    if (e.target.closest('.clkl-lay')) { lay(st, api, e); return; }
    const f = e.target.closest('[data-lform]');
    if (f) { if (human(e) && st.mine) formIt(st, api, f.dataset.lform, f.dataset.lstyle || ''); return; }
    const put = e.target.closest('[data-tst]');
    const back = put ? null : e.target.closest('[data-tun]');
    if (put || back) {
      const [b, s, i] = (put || back).dataset[put ? 'tst' : 'tun'].split('|');
      depotAct(st, api, e, put ? { do: 'stock', b, s: +s, i: +i, n: +put.dataset.n } : { do: 'unstock', b, s: +s, i: +i });
      return;
    }
    if (e.target.closest('.clkl-stockall')) {
      const k = st.toloka;
      if (!human(e) || !st.mine || !k || k.busy) return;
      // Два натиски, як «Продати все»: перший лише питає й каже скільки.
      if (!(k.armAt > Date.now())) {
        api.sfx('tap');
        k.armAt = Date.now() + ARM_MS;
        paint(st, api);
        setTimeout(() => { const kk = st.toloka; if (kk && kk.armAt && Date.now() >= kk.armAt) { kk.armAt = 0; paint(st, api); } }, ARM_MS + 50);
        return;
      }
      depotAct(st, api, e, { do: 'stockAll' });
      return;
    }
    if (e.target.closest('.clkl-festgo')) festival(st, api, e);
  }

  // ---------- друзі на толоці: хата друга ----------

  /// Хата друга (clicker-guild.js): його толока й «🤝 Піднести на толоку». d — знімок хати з полем toloka.
  function friendHtml(st, api, d, open, err, nextOpen) {
    const t = d.toloka;
    const esc = (x) => api.esc(st, x);
    const b = bdef(st, t.building);
    if (!b) return '';
    const sd = b.stages[t.stage] || { name: '' };
    const sn = api.serverNow(st);
    const ends = ms(t.endsAt);
    const laid = ends > sn;
    const can = st.mine && st.guild && st.guild.enabled;
    let html = '<div class="clk-sub">🏗 Толока: ' + b.icon + ' ' + esc(b.name)
      + ' <span class="muted small">· етап ' + (t.stage + 1) + ' з ' + b.stages.length + ' — «' + esc(sd.name) + '»</span></div>';
    // Відлік цокає щосекунди (tolokaHouse), як у своїй Толоці: застиглий «ще 42:10» за хвилину-дві розходився з господарем (QA-4).
    if (laid) html += '<div class="small">🔨 Будується · ще <i class="clkl-fcd" data-at="' + ends + '">' + esc(left(api, ends - sn)) + '</i></div>';
    if (t.helpers.length) html += '<div class="small">🤝 На толоці ' + (laid ? 'були' : 'вже були') + ': <b>' + esc(t.helpers.join(', ')) + '</b></div>';
    const still = t.needs.filter((n) => n.left > 0);
    const hmax = (cat(st) && cat(st).helpersMax) || 3;
    const been = t.helpers.some((h) => h.toLowerCase() === myNick(st).toLowerCase());
    // QA-3: поки етап будується, «Піднести» приймає вимоги цього етапу, а не наступного — тож угорі вони, з «у тебе N»,
    // а наступний — згорнутим «лише глянути».
    if (laid && !been) {
      const mineL = itemsOf(st);
      html += '<div class="clkl-fcur small"><div class="muted">Зараз приймають (етап «' + esc(sd.name) + '»):</div>' + t.needs.map((n) => {
        const have = mineL.filter((it) => fits(n, it)).reduce((a, it) => a + it.n, 0);
        return '<div>' + needText(st, api, n) + ' <span class="' + (have ? 'clkl-cut' : 'muted') + '">· у тебе ' + api.count(have) + '</span></div>';
      }).join('') + '</div>';
    }
    if (laid && t.next) {
      html += '<details class="clkl-fnext"' + (nextOpen ? ' open' : '') + '><summary class="small">⏭ Наступний етап «' + esc(t.next.name) + '» — лише глянути</summary>'
        + nextHtml(st, api, t.next, b, false) + '</details>';
    }
    // B8: вимоги закриті, а помічників менше трьох — прийти все одно поміч (−15 %), вироби ляжуть у комору друга.
    const extra = !laid && !still.length && !been && t.helpers.length < hmax;
    if (!laid && !still.length) {
      html += '<div class="muted small">' + (extra
        ? 'Усе, що просили, вже є — але прийти на толоку ще можна: ти скоротиш будову на 15 % (помічників ' + t.helpers.length + ' з ' + hmax + '), вироби ляжуть у комору друга.'
        : 'Усе, що просили, вже є' + (been ? ', і ти на цьому етапі вже був(ла)' : ', і помічників уже ' + hmax) + ' — другові лишилось закласти етап.') + '</div>';
    }
    // Гостинець толоки: хвилини гри за виріб, лише за перші treatCap виробів на етап друга (13.10).
    const cap = t.treatCap || 20;
    const treat = t.treatLeft == null ? 'за кожен виріб — гостинець толоки'
      : t.treatLeft > 0 ? 'гостинець толоки — ' + api.count(t.treat || 2) + ' хв гри за виріб, ще за ' + t.treatLeft + ' із ' + cap + ' на цей етап'
        : 'гостинець за цей етап уже взято (' + cap + ' виробів) — далі поміч без гостинця';
    // QA 21.10: етап будується, а я на ньому вже був(ла) — сервер відмовить (раз на етап), тож і кнопка не кличе, і
    // відкритий вибір виробів (з «гостинець — ще за 15 із 20», QA-3) після посилки згортається назад.
    const again = laid && been;
    if (!open || again) {
      // Кнопка жива, лише коли в моїй коморі є хоч щось, що цей етап іще приймає.
      const mine0 = itemsOf(st);
      const any = !again && t.needs.some((n) => (laid || extra || n.left > 0) && mine0.some((it) => fits(n, it)));
      const asks = t.needs.filter((n) => laid || extra || n.left > 0).map((n) => needText(st, api, n).replace(/<[^>]+>/g, '')).join('; ');
      return html + '<div class="clkg-btns"><button type="button" class="primary clkl-fgo"' + (can && any ? '' : ' disabled') + '>🤝 Піднести на толоку</button>'
        + '<span class="muted small">' + (again ? 'ти на цьому етапі вже був(ла) — неси на наступний, щойно друг почне його збирати'
          : any ? treat : !(laid || extra || still.length) ? ''
          : 'у твоїй коморі нема того, що просить цей етап: ' + esc(asks)) + '</span></div>'
        + (err ? '<div class="small clkl-err">' + esc(err) + '</div>' : '');
    }
    html += laid || extra
      ? '<p class="muted small clk-note">' + (laid ? 'Етап уже будується: приймають лише те, що просив цей етап (не наступний),' : 'Вимоги вже закриті:')
        + ' вироби ляжуть у комору друга, а твоя поміч скоротить будову на 15 %. Раз на етап.</p>'
      : '<p class="muted small clk-note">Вироби йдуть у вимоги етапу; беремо не більше, ніж ще бракує. До ' + (t.max || 10) + ' за раз.</p>';
    html += '<p class="muted small clk-note">🎁 ' + treat.charAt(0).toUpperCase() + treat.slice(1) + '.</p>';
    const mine = itemsOf(st);
    html += '<div class="clkl-fneeds">' + t.needs.map((n, i) => {
      const room = laid || extra ? (t.max || 10) : n.left;
      const mineFit = mine.filter((it) => fits(n, it));
      const head = '<div class="clkl-fhead">' + needText(st, api, n)
        + '<span class="muted small">' + (laid || extra ? '' : n.left > 0 ? ' · бракує ' + n.left : ' · досить') + '</span></div>';
      if (room <= 0) return '<div class="clkl-fneed done">' + head + '</div>';
      const rows = mineFit.length
        ? mineFit.map((it, j) => {
          const top = Math.min(it.n, room, t.max || 10);
          const nums = [1, 5, 10].filter((x) => x < top).concat([top]);
          return '<div class="clkl-fitem">' + api.wareSvg(it.ware, { style: it.style, quality: it.q, cls: 'clkl-fware', slot: 'tf-' + i + '-' + j })
            + '<span class="small clkl-fn">×' + it.n + (it.style ? ' · ' + esc(styleName(st, it.style)) : '') + '</span>'
            + '<span class="clkl-fbtns">' + [...new Set(nums)].map((x) => '<button type="button" class="ghost small" data-tsend="' + esc(it.key) + '" data-n="' + x + '"'
              + (can ? '' : ' disabled') + '>+' + x + '</button>').join('') + '</span></div>';
        }).join('')
        : '<div class="muted small">у твоїй коморі такого нема</div>';
      return '<div class="clkl-fneed">' + head + rows + '</div>';
    }).join('') + '</div>';
    return html + (err ? '<div class="small clkl-err">' + esc(err) + '</div>' : '');
  }

  const myNick = (st) => (st.ctx && st.ctx.me && st.ctx.me.nick) || '';

  function tolokaHouse(st, api, body, d) {
    if (!body || !d) return;
    let box = body.querySelector('.clkl-friend');
    if (!d.toloka || !cat(st)) { if (box) box.remove(); return; }
    if (!box) {
      box = document.createElement('section');
      box.className = 'clkl-friend';
      const anchor = body.querySelector('.clkg-hhelp');
      if (anchor) anchor.before(box); else body.appendChild(box);
    }
    const draw = () => { if (box.isConnected) box.innerHTML = friendHtml(st, api, d, box._open, box._err, box._next); };
    // Згортка наступного етапу переживає перемальовування після посилки.
    if (!box._tog) { box._tog = true; box.addEventListener('toggle', (e) => { if (e.target.classList && e.target.classList.contains('clkl-fnext')) box._next = e.target.open; }, true); }
    draw();
    if (!box._tick) {
      box._tick = setInterval(() => {
        if (!box.isConnected) { clearInterval(box._tick); box._tick = 0; return; }
        const sn = api.serverNow(st);
        for (const el of box.querySelectorAll('.clkl-fcd')) {
          const at = +el.dataset.at;
          const tx = at > sn ? left(api, at - sn) : 'ось-ось';
          if (el.textContent !== tx) el.textContent = tx;
        }
      }, 1000);
    }
    box.onclick = (e) => {
      if (e.target.closest('.clkl-fgo')) {
        if (!human(e)) return;
        api.sfx('tap');
        box._open = true;
        box._err = '';
        draw();
        return;
      }
      const send = e.target.closest('[data-tsend]');
      if (!send || !human(e) || box._busy) return;
      box._busy = true;
      for (const b of box.querySelectorAll('[data-tsend]')) b.disabled = true;
      api.act(st, 'guild', { op: 'toloka', to: d.nick, key: send.dataset.tsend, n: +send.dataset.n }).then((r) => {
        if (r && r.ok) {
          api.sfx('gift');
          box._err = '';
          if (r.message) api.feed(st, r.message);
        } else box._err = (r && r.message) || '';
        // Свіжа толока друга: посилка вже в дорозі, і «бракує» її враховує.
        return fetch('/api/games/clicker/house?nick=' + encodeURIComponent(d.nick), { headers: { 'X-Nick': encodeURIComponent(myNick(st)) } })
          .then((x) => (x.ok ? x.json() : null))
          .then((fresh) => { if (fresh) d.toloka = fresh.toloka; })
          .catch(() => { /* лишаємо старий знімок */ });
      }).then(() => { box._busy = false; draw(); });
    };
  }

  // ---------- місце у «Селі» ----------

  /// Розділ — одразу під Гостинним двором (або вгорі «Села», поки двору нема). Вкладку робить цех, двір — гості,
  /// і обидва могли змонтуватись пізніше за нас — тож місце перевіряємо щоразу.
  function mountBlock(st, api) {
    const k = st.toloka;
    if (!k || !st.guildPane) return;
    if (!k.el) {
      k.el = document.createElement('section');
      k.el.className = 'clkg-card clkl';
      k.el.dataset.sec = 'toloka';           // якір рядка переходів «Села» (clicker-guild.js)
      k.el.addEventListener('click', (e) => onClick(st, api, e));
    }
    const pane = st.guildPane;
    const guests = st.guests && st.guests.el && st.guests.el.parentElement === pane ? st.guests.el : null;
    const placed = k.el.parentElement === pane && (guests ? k.el.previousElementSibling === guests : pane.firstElementChild === k.el);
    if (!placed) pane.insertBefore(k.el, guests ? guests.nextSibling : pane.firstChild);
  }

  /// «Село» відкривається й тоді, коли вже є гривня за весь час: толока — теж привід зайти в село.
  function gate(st, api) {
    const prev = st.gates && st.gates.guild;
    if (!prev || prev._toloka) return;
    const fn = (st2, v) => !!(v && v.toloka) || prev(st2, v);
    fn._toloka = true;
    api.showWhen(st, 'guild', fn);
  }

  /// «Далі» в простої (v12 §2): етап будується — нестача найближчого етапу, що ще чогось просить. QA-4: не найбільша на
  /// всю Толоку (глечик ×99 для далекого «Глеку на майдані», коли «Вернісаж» просить плесканець і полумисок; у Миколи —
  /// барило на всю Толоку, коли наступний етап просить глечик і макітру): ліпити треба те, що закладатимеш наступним.
  function lackGoal(st, api) {
    const k = st.toloka;
    const s = k.v.stage;
    if (s && !s.endsAt) return stageGoal(st, api, s);
    if (!s) return null;
    const near = nearKeys(k.v);
    for (const p of k.v.plan) {
      if (!near.has(p.building)) break;       // далі за двома будовами — лише етапи зі складом, не черга
      const b = bdef(st, p.building);
      if (!b) continue;
      const rows = p.needs.map((n) => Object.assign({}, n, { lack: Math.max(0, n.n - (n.depot || 0) - haveOf(st, n)) }));
      const g = needGoal(st, api, b, p.index, rows, false);
      if (g) return Object.assign(g, { row: '[data-sec="toloka"] .clkl-plan[data-fold="g.tp.' + (p.building === s.building ? 'cur' : 'next') + '"] > .clk-fh' });
    }
    return null;
  }

  /// Дія цілі «Далі» (QA-3): не «Глянути», а одразу «🎨 Ліпити плесканець → розпис» — виріб на коло й розпис у тост
  /// (form { ware, style }). Перший рядок за нестачею, який уже можна ліпити (виріб відкритий, розпис куплений); він
  /// уже на колі чи такого нема — лишається «Глянути». need — рядок, про який кнопка (для підпису цілі).
  function goalAct(st, api, list) {
    const c = st.craft;
    if (!c || !c.wares) return {};
    const owned = (key) => !key || (st.styleList || []).some((x) => x.key === key && x.owned);
    const n = list.find((x) => (c.wares.find((w) => w.key === x.ware) || {}).open && owned(x.style));
    // Найбільша нестача вже на колі — гончар робить саме те, що треба: не перемикати, лише «Глянути».
    if (!n) return {};
    if (c.ware === n.ware) return { need: n };
    const btn = n.q >= 4 ? formLabel(st, n).replace('🏺', '🎨') + (n.style ? '' : ' → розпис') : formLabel(st, n);
    return { need: n, btn, run: () => formIt(st, api, n.ware, n.style || '') };
  }

  /// Етап ще не закладений, і бракує виробів (не грошей — ті прийдуть самі): годинник будови стоїть, доки не донесеш.
  function stageGoal(st, api, s) {
    const b = bdef(st, s.building);
    if (!b) return null;
    const rows = s.needs.map((n) => Object.assign({}, n, { lack: Math.max(0, n.n - n.got - (n.depot || 0) - n.have) }));
    const g = needGoal(st, api, b, s.index, rows, true);
    return g && Object.assign(g, { row: '[data-sec="toloka"] .clkl-needs' });
  }

  /// Ціль «Далі» з рядків одного етапу: «Толока: на «Вернісаж» бракує 100 виробів», найбільша нестача (розкішні,
  /// потім дзвінкі — першими: їх найдовше робити) і кнопка, що ліпитиме саме її («🎨 Ліпити плесканець → розпис»).
  function needGoal(st, api, b, index, all, cur) {
    // Розкішні й дзвінкі — першими (їх дає лише ручне горно), звичайні й добрі — за нестачею: вони однаково йдуть з горна.
    const cls = (q) => (q >= 4 ? 2 : q === 3 ? 1 : 0);
    const rows = all.filter((n) => n.lack > 0).sort((a, x) => cls(x.q) - cls(a.q) || x.lack - a.lack);
    if (!rows.length) return null;
    const n = rows.reduce((a, x) => a + x.lack, 0);
    const of = all.reduce((a, x) => a + x.n, 0);
    const act = goalAct(st, api, rows);
    // Підпис — про той рядок, який ліпитиме кнопка (перший, що вже можна ліпити), інакше — про найбільший.
    const top = act.need || rows[0];
    const sd = b.stages[index] || { name: '' };
    const hint = qHint(api, top.q);
    const icon = top.q >= 4 ? '🎨' : top.q === 3 ? '🔥' : b.icon;
    return Object.assign({
      icon: '<text x="16" y="23" font-size="19" text-anchor="middle">' + icon + '</text>',
      text: 'Толока: на «' + sd.name + '» бракує ' + api.count(n) + ' ' + api.plural(n, 'виробу', 'виробів', 'виробів'),
      sub: wareName(st, top.ware).toLowerCase() + (top.style ? ' «' + styleName(st, top.style) + '»' : '') + ' ×' + api.count(top.lack)
        + (hint ? ' · ' + hint : '') + (cur ? ' · годинник стоїть, доки не закладеш' : ' · наступний етап, поки цей будується'),
      pct: of > 0 ? ((of - n) / of) * 100 : 0,
      eta: LACK_ETA, tab: 'guild', prio: 1,
    }, { btn: act.btn, run: act.run });
  }

  /// Смуга «Далі»: усе для етапу є — підказати закласти (пріоритет 1: годинник будови не цокає, доки не заклав);
  /// етап будується — нестача найближчого етапу; не закладений і бракує виробів — нестача цього етапу.
  (window.HClicker.goals = window.HClicker.goals || []).push((st, v, api) => {
    const k = st.toloka;
    if (!k || !k.v || !st.mine) return null;
    if (!canLay(st, api)) return lackGoal(st, api);
    const s = k.v.stage;
    const b = bdef(st, s.building);
    if (!b) return null;
    const sd = b.stages[s.index] || { name: '' };
    return {
      icon: '<text x="16" y="23" font-size="19" text-anchor="middle">' + b.icon + '</text>',
      text: 'Толока: можна закласти «' + sd.name + '»',
      sub: b.name + ' · будується ' + hrs(s.hours * (1 - (s.cut || 0))),
      // QA-4: на телефоні кнопка «Закласти» — на ~850 px нижче; «Глянути» веде просто до неї.
      pct: 100, eta: 0, tab: 'guild', row: '[data-sec="toloka"] .clkl-lay', prio: 1,
    };
  });

  HClicker.part({
    id: 'toloka',
    order: 63,

    mount(st, api) {
      st.toloka = {
        v: null, el: null, cds: [], dirty: false, done: null, unseen: false, noteText: null, busy: false, looked: 0,
        err: null, helpSig: '', helpers: null, rich: null, fest: null,
        armAt: 0, workEl: null, wn: null, wc: null, wb: null, ws: null, wKey: '', wShown: 0, went: '', wentFrom: null,
      };
      // Хата друга кличе це з clicker-guild.js (кнопка «🤝 Піднести на толоку»).
      api.tolokaHouse = (st2, body, d) => {
        try { tolokaHouse(st2, api, body, d); } catch (e) { console.error('[clicker:toloka] хата друга', e); }
      };
      mountBlock(st, api);
      gate(st, api);
    },

    update(st, v, api) {
      const k = st.toloka;
      if (!k) return;
      mountBlock(st, api);
      gate(st, api);
      const t = v.toloka;
      if (!t) {
        k.v = null;
        note(st, api);
        paint(st, api);
        return;
      }
      const prev = k.v && k.v.stage ? { building: k.v.stage.building, index: k.v.stage.index } : null;
      const s = t.stage;
      k.v = {
        built: t.built || [],
        stage: s ? {
          building: s.building, index: s.index || 0, pay: s.pay || 0, hours: s.hours || 0, cut: s.cut || 0,
          laidAt: s.laidAt ? ms(s.laidAt) : 0, endsAt: s.endsAt ? ms(s.endsAt) : 0,
          needs: (s.needs || []).map((n) => ({ ware: n.ware, n: n.n || 0, q: n.q || 1, style: n.style || '', got: n.got || 0, depot: n.depot || 0, have: n.have || 0 })),
          helpers: s.helpers || [],
          // v12: своя праця — лише на закладеному етапі.
          work: s.work ? { n: s.work.n || 0, of: Math.max(1, s.work.of || 1), cut: s.work.cut || 0 } : null,
        } : null,
        waitBig: !!t.waitBig, done: t.done || 0, helped: t.helped || 0,
        festival: t.festival ? { until: ms(t.festival.until), next: ms(t.festival.next), mult: t.festival.mult || 2 } : null,
        allMult: t.allMult || 1,
        // Поки етап будується — що треба на наступний (сервер дає лише тоді; have — з моєї комори).
        next: t.next || null,
        // v12: план (поточна й наступна будова поетапно), підсумок до кінця й скільки всього на складі.
        plan: (t.plan || []).map((p) => ({ building: p.building, index: p.index || 0,
          needs: (p.needs || []).map((n) => ({ ware: n.ware, n: n.n || 0, q: n.q || 1, style: n.style || '', depot: n.depot || 0 })) })),
        totals: (t.totals || []).map((g) => ({ ware: g.ware, q: g.q || 1, style: g.style || '', n: g.n || 0, depot: g.depot || 0, have: g.have || 0 })),
        depot: t.depot || 0,
      };
      wentWhere(st, api);
      stageDone(st, api, prev);
      helpersCame(st, api);
      k.rich = canLay(st, api);
      note(st, api);
      k.dirty = true;
      if (st.tab === 'guild') paint(st, api);
    },

    slow(st, api, sn) {
      const k = st.toloka;
      if (!k) return;
      if (!k.v) { if (k.dirty && st.tab === 'guild') paint(st, api); return; }
      // Гроші ростуть і без виду (живий лічильник): «Закласти етап» оживає, щойно глеків досить.
      const rich = canLay(st, api);
      const fest = festReady(st, api) + ':' + (k.v.festival ? k.v.festival.until > sn : '');
      if (rich !== k.rich || fest !== k.fest) { k.rich = rich; k.fest = fest; k.dirty = true; note(st, api); }
      if (st.tab === 'guild') {
        if (k.dirty || (k.err && Date.now() - k.err.at > ERR_MS && Date.now() - k.err.at < ERR_MS + 400)) paint(st, api);
        else { work(st, api); countdowns(st, api); }
      }
      // Годинник етапу вибив, а гончар лише дивиться: раз питаємо свіжий вид — сервер зведе етап.
      const s = k.v.stage;
      if (s && s.endsAt && st.mine && api.visible(st) && sn > s.endsAt + LOOK_GRACE_MS && k.looked !== s.endsAt) {
        k.looked = s.endsAt;
        api.act(st, 'look');
      }
    },

    unmount(st) {
      const k = st.toloka;
      if (k && k.el) k.el.remove();
      st.toloka = null;
    },
  });
})();
