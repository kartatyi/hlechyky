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
*/
(() => {
  const human = HGames.ui.human;

  /// Якість, якої просять, — у множині: «(добрі й кращі · «Косівська»)», як у гостей.
  const Q_REQ = ['', '', 'добрі й кращі', 'дзвінкі й кращі', 'лише розкішні'];
  const LOOK_GRACE_MS = 1500;             // годинник етапу вибив — стільки чекаємо, перш ніж спитати свіжий вид
  const ERR_MS = 5000;

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
      + ' % до всього й вічна нагорода; обпал будов не чіпає. Мала толока відкривається з гривні, велика — з червоного золотого.';
  }

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

  function needsHtml(st, api, s) {
    const esc = (x) => api.esc(st, x);
    const rows = s.needs.map((n, i) => {
      const want = Math.max(0, n.n - n.got);
      const ok = n.have >= want;
      const where = 'у коморі ' + api.count(n.have) + (n.got ? ' · піднесли друзі ' + api.count(n.got) : '');
      return '<div class="clkl-need' + (ok ? ' ok' : '') + '">'
        + '<span class="clkl-nart">' + api.wareSvg(n.ware, { style: n.style, quality: Math.max(1, n.q), cls: 'clkl-ware', slot: 'tn-' + i }) + '</span>'
        + '<div class="clkl-ntxt">' + needText(st, api, n) + '<span class="muted small">' + where + '</span></div>'
        + '<span class="clkl-mark">' + (ok ? '✓' : 'ще ' + (want - n.have)) + '</span></div>';
    });
    const rich = (st.shown || 0) >= s.pay;
    rows.push('<div class="clkl-need' + (rich ? ' ok' : '') + '"><span class="clkl-nart clkl-emo">💰</span>'
      + '<div class="clkl-ntxt"><b>' + esc(api.potsShort(s.pay)) + '</b><span class="muted small">гроші на етап</span></div>'
      + '<span class="clkl-mark">' + (rich ? '✓' : '') + '</span></div>');
    const cut = cutPct(s.cut);
    rows.push('<div class="clkl-need hours"><span class="clkl-nart clkl-emo">⏳</span>'
      + '<div class="clkl-ntxt"><b>' + hrs(s.hours * (1 - (s.cut || 0))) + '</b><span class="muted small">'
      + (cut > 0 ? 'будується замість ' + hrs(s.hours) + ' — друзі й рід скоротили на ' + cut + ' %' : 'будується годинником — глеками не пришвидшити')
      + '</span></div><span class="clkl-mark"></span></div>');
    return '<div class="clkl-needs">' + rows.join('') + '</div>';
  }

  /// Чого бракує, щоб закласти етап (порожньо — можна).
  function lacks(st, api, s) {
    const out = [];
    for (const n of s.needs) {
      const want = Math.max(0, n.n - n.got);
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
      return '<div class="muted small clkl-help">🤝 Друзі з цеху можуть піднести вироби зі своїх комор — кожен скоротить '
        + (laid ? 'будову' : 'етап') + ' на ' + Math.round(per) + ' %</div>';
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
      html += '<div class="clkl-build"><div class="clkl-bline">🔨 Будується · ще <i class="clkl-cd" data-at="' + s.endsAt + '" data-done="ось-ось"></i></div>'
        + '<div class="clkl-tbar"><i data-from="' + s.laidAt + '" data-to="' + s.endsAt + '"></i></div></div>'
        + helpersLine(st, api, s, true);
      const next = b.stages[s.index + 1];
      if (t.next) html += nextHtml(st, api, t.next, b, true);
      html += '<div class="muted small">' + (next ? 'Вироби на наступний етап можна збирати в комору заздалегідь.'
        : 'Це останній етап: достроїться — і «' + esc(b.name) + '» стоятиме назавжди.') + '</div>';
      return html;
    }
    const miss = lacks(st, api, s);
    html += needsHtml(st, api, s)
      + helpersLine(st, api, s, false)
      + '<div class="clkl-btns"><button type="button" class="primary clkl-lay"' + (st.mine && !miss.length ? '' : ' disabled') + '>🏗 Закласти етап</button>'
      + (miss.length ? '<span class="muted small clkl-miss">бракує: ' + esc(miss.join('; ')) + '</span>' : '<span class="small clkl-ready">усе є — закладай!</span>')
      + '</div>';
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
      api.swap(k.el, '<div class="clk-teaser muted small clkl-soon">🏗 Толока відкриється на ' + esc(api.potsShort(from)) + ' за весь час</div>');
      k.el.classList.add('closed');
      k.cds = [];
      return;
    }
    k.el.classList.remove('closed');
    if (!c) {
      api.swap(k.el, '<div class="clk-sub">🏗 Толока</div><div class="muted small">Село сходиться на толоку…</div>');
      return;
    }
    const t = k.v;
    const bonus = Math.round((t.allMult - 1) * 100);
    let html = '<div class="clk-sub clkl-title">🏗 Толока<span class="muted small"> · збудовано ' + t.built.length + ' з ' + c.buildings.length
      + (bonus > 0 ? ' · +' + bonus + ' % до всього' : '') + '</span>' + api.info(esc(infoText(st))) + '</div>'
      + builtRow(st, api);
    if (t.stage) html += stageHtml(st, api);
    else if (t.waitBig) html += '<div class="clk-teaser small clkl-wait">⚓ Мала толока збудована. Велика толока почнеться з червоного золотого</div>';
    else if (t.built.length >= c.buildings.length) html += '<div class="clkl-all">🏺 Усі ' + c.buildings.length + ' будов стоять — ти Толочанин світу!</div>';
    html += festivalHtml(st, api);
    if (k.err && Date.now() - k.err.at < ERR_MS) html += '<div class="small clkl-err">' + esc(k.err.text) + '</div>';
    if (api.swap(k.el, html)) k.cds = [...k.el.querySelectorAll('.clkl-cd, .clkl-tbar i')];
    countdowns(st, api);
    // Бачив новий етап — нотатка на ярлику більше не кличе.
    if (st.tab === 'guild' && k.unseen) { k.unseen = false; note(st, api); }
  }

  function countdowns(st, api) {
    const k = st.toloka;
    if (!k || !k.cds) return;
    const sn = api.serverNow(st);
    for (const el of k.cds) {
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
      if (fresh.length) {
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
      const ok = have >= n.n;
      return '<div class="clkl-need' + (ok ? ' ok' : '') + '">'
        + '<span class="clkl-nart">' + api.wareSvg(n.ware, { style: n.style, quality: Math.max(1, n.q), cls: 'clkl-ware', slot: 'tx-' + (own ? 'o' : 'f') + i }) + '</span>'
        + '<div class="clkl-ntxt">' + needText(st, api, n) + '<span class="muted small">у тебе є ' + api.count(have) + '</span></div>'
        + '<span class="clkl-mark">' + (ok ? '✓' : 'ще ' + (n.n - have)) + '</span></div>';
    });
    const rich = own && (st.shown || 0) >= nx.pay;
    rows.push('<div class="clkl-need' + (rich ? ' ok' : '') + '"><span class="clkl-nart clkl-emo">💰</span>'
      + '<div class="clkl-ntxt"><b>' + esc(api.potsShort(nx.pay)) + '</b><span class="muted small">' + (own ? 'гроші на етап' : 'гроші на етап — його') + '</span></div>'
      + '<span class="clkl-mark">' + (rich ? '✓' : '') + '</span></div>');
    rows.push('<div class="clkl-need hours"><span class="clkl-nart clkl-emo">⏳</span>'
      + '<div class="clkl-ntxt"><b>' + hrs(nx.hours) + '</b><span class="muted small">будується (друзі й рід скоротять)</span></div><span class="clkl-mark"></span></div>');
    const head = (b.key !== (cur && cur.key) ? b.icon + ' ' + esc(b.name) + ' — ' : '') + 'етап ' + (nx.index + 1) + ' з ' + b.stages.length + ' «' + esc(nx.name) + '»';
    return '<div class="clkl-next"><div class="clk-sub small">⏭ Наступний етап: ' + head + '</div>'
      + '<div class="clkl-needs">' + rows.join('') + '</div></div>';
  }

  function onClick(st, api, e) {
    if (e.target.closest('.clkl-lay')) { lay(st, api, e); return; }
    if (e.target.closest('.clkl-festgo')) festival(st, api, e);
  }

  // ---------- друзі на толоці: хата друга ----------

  /// Хата друга (clicker-guild.js): його толока й «🤝 Піднести на толоку». d — знімок хати з полем toloka.
  function friendHtml(st, api, d, open, err) {
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
    if (laid) html += '<div class="small">🔨 Будується · ще ' + esc(left(api, ends - sn)) + '</div>';
    if (t.helpers.length) html += '<div class="small">🤝 На толоці ' + (laid ? 'були' : 'вже були') + ': <b>' + esc(t.helpers.join(', ')) + '</b></div>';
    // Поки етап будується — що другові треба на наступний і скільки такого є в мене (лише показати).
    if (laid && t.next) html += nextHtml(st, api, t.next, b, false);
    const still = t.needs.filter((n) => n.left > 0);
    if (!laid && !still.length) html += '<div class="muted small">Усе, що просили, вже є — другові лишилось закласти етап.</div>';
    if (!open) {
      // Кнопка жива, лише коли в моїй коморі є хоч щось, що цей етап іще приймає.
      const mine0 = itemsOf(st);
      const any = t.needs.some((n) => (laid || n.left > 0) && mine0.some((it) => fits(n, it)));
      return html + '<div class="clkg-btns"><button type="button" class="primary clkl-fgo"' + (can && any ? '' : ' disabled') + '>🤝 Піднести на толоку</button>'
        + '<span class="muted small">' + (any || !(laid || still.length) ? 'за кожен виріб — гостинець толоки: ' + (t.treat || 10) + ' хв твого пасиву'
          : 'у твоїй коморі нема того, що просить цей етап') + '</span></div>'
        + (err ? '<div class="small clkl-err">' + esc(err) + '</div>' : '');
    }
    html += laid
      ? '<p class="muted small clk-note">Етап уже будується: вироби ляжуть у комору друга на наступні етапи, а твоя поміч скоротить будову. Раз на етап.</p>'
      : '<p class="muted small clk-note">Вироби йдуть у вимоги етапу; беремо не більше, ніж ще бракує. До ' + (t.max || 10) + ' за раз.</p>';
    const mine = itemsOf(st);
    html += '<div class="clkl-fneeds">' + t.needs.map((n, i) => {
      const room = laid ? (t.max || 10) : n.left;
      const mineFit = mine.filter((it) => fits(n, it));
      const head = '<div class="clkl-fhead">' + needText(st, api, n)
        + '<span class="muted small">' + (laid ? '' : n.left > 0 ? ' · бракує ' + n.left : ' · досить') + '</span></div>';
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
    const draw = () => { if (box.isConnected) box.innerHTML = friendHtml(st, api, d, box._open, box._err); };
    draw();
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

  /// Смуга «Далі»: усе для етапу є — підказати закласти (пріоритет 1: годинник будови не цокає, доки не заклав).
  (window.HClicker.goals = window.HClicker.goals || []).push((st, v, api) => {
    const k = st.toloka;
    if (!k || !k.v || !st.mine || !canLay(st, api)) return null;
    const s = k.v.stage;
    const b = bdef(st, s.building);
    if (!b) return null;
    const sd = b.stages[s.index] || { name: '' };
    return {
      icon: '<text x="16" y="23" font-size="19" text-anchor="middle">' + b.icon + '</text>',
      text: 'Толока: можна закласти «' + sd.name + '»',
      sub: b.name + ' · будується ' + hrs(s.hours * (1 - (s.cut || 0))),
      pct: 100, eta: 0, tab: 'guild', prio: 1,
    };
  });

  HClicker.part({
    id: 'toloka',
    order: 63,

    mount(st, api) {
      st.toloka = {
        v: null, el: null, cds: [], dirty: false, done: null, unseen: false, noteText: null, busy: false, looked: 0,
        err: null, helpSig: '', helpers: null, rich: null, fest: null,
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
          needs: (s.needs || []).map((n) => ({ ware: n.ware, n: n.n || 0, q: n.q || 1, style: n.style || '', got: n.got || 0, have: n.have || 0 })),
          helpers: s.helpers || [],
        } : null,
        waitBig: !!t.waitBig, done: t.done || 0, helped: t.helped || 0,
        festival: t.festival ? { until: ms(t.festival.until), next: ms(t.festival.next), mult: t.festival.mult || 2 } : null,
        allMult: t.allMult || 1,
        // Поки етап будується — що треба на наступний (сервер дає лише тоді; have — з моєї комори).
        next: t.next || null,
      };
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
        else countdowns(st, api);
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
