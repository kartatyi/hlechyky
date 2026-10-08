/*
  Цех Гончарного кола (docs/games/specs/clicker-v7.md §4 B5, дев'яте оновлення — clicker-v9.md §E;
  як зроблено — clicker-v7-guild.md і clicker-v9-guild.md). Частина ядра clicker.js.

  Вкладка «Село»:
  0) плашка діючих бафів від друзів (підмайстер у гостях, похвала) з відліком;
  1) віз дня — великий SVG-віз, що наповнюється виробами, смуга з порогами бронза/срібло/золото, підцілі, внески
     друзів (за абеткою, без місць), пай («кожні 12 виробів — ще один пай, до трьох»), «Покласти на віз» і «Забрати»;
  2) «Помогти другові» — гостинець (10/30/60 хв свого пасиву), підмайстер у гості, похвала;
  3) дарунки — скільки ще сьогодні, вибір друга й виробу, полиця дарунків «від Миколи»;
  4) ранг — шлях учень → челядник → майстер → цехмістр → старійшина, що бракує, майстерштук, перки, вимикач автогорна;
  5) гончарі цеху (GET /api/games/clicker/guild) і «Зазирнути в хату» (GET /api/games/clicker/house) — хата в overlay
     з альбомом, виставкою, станом горна, дивовижами й кнопками допомоги;
  6) «Похвалитись» виробом у Журнал.
  Правила рахує сервер (Clicker.ActGuild / ClickerGuildService); тут — лише малюнок і дії guild { op, … }.
  Звуки: api.sfx('wagon' | 'gift' | 'rank-up' | 'brag').
*/
(() => {
  /// Натиск на джойстику — теж людина, просто не мишею: шар пада (web/static/pad.js) ставить
  /// своїм подіям позначку, а ui.human() її впізнає. Скрізь, де Око майстра питало `isTrusted`,
  /// тепер стоїть human() — скрипт зі сторони від цього ближче не став.
  const human = HGames.ui.human;

  const ROSTER_MS = 60000;
  const RANKS = ['Учень', 'Челядник', 'Майстер', 'Цехмістр', 'Старійшина'];
  const RANK_ICON = ['🧑‍🎓', '🛠️', '🏅', '🎖️', '🧓'];
  const TIER = ['', 'бронза', 'срібло', 'золото'];
  const TIER_ICON = ['', '🥉', '🥈', '🥇'];
  const TIER_COLOR = ['#8a6a4a', '#c07a3a', '#c9ced6', '#f4c542'];
  const QUALITY = ['', 'звичайний', 'добрий', 'дзвінкий', 'розкішний'];
  /// Скільки видів виробів показує рядок «на возі» (решта — «+N»).
  const WAGON_KINDS = 6;
  const STARS = ['', '★', '★★', '★★★', '👑'];
  // Допомога другові (§E.2): що це, як зветься й чим пахне.
  const HELP = {
    treat: { icon: '🎁', name: 'Гостинець', what: 'віддаєш свої хвилини пасиву — друг дістає вдвічі більше своїх' },
    lend: { icon: '🧑‍🎓', name: 'Підмайстер у гості', what: 'добу в друга ліплять удвічі швидше' },
    cheer: { icon: '👏', name: 'Похвала', what: 'годину в друга все йде на 10 % краще' },
  };
  const TOOL_ICON = { paddle: '🪵', string: '🧵', sponge: '🧽', ribs: '🦴', lantern: '🏮', apron: '🥼', bucket: '🪣', whistle: '🎶', scales: '⚖️', iron: '🔖' };
  const TIER_EMBLEM = { workshop: '🏠', fair: '🎪', artel: '🤝', chumaks: '🐂', pit: '⛏️', school: '📜', chaika: '⛵', museum: '🏛️', tsar: '👑' };

  // ---------- дрібниці ----------

  /// «1», «1,25», «3» — пай із двома знаками й українською комою.
  /// Форматер один на всю частину: toLocaleString будує новий Intl.NumberFormat на кожен виклик (v10 §10).
  const PAI = new Intl.NumberFormat('uk-UA', { maximumFractionDigits: 2 });
  const pai = (n) => PAI.format(Math.round(n * 100) / 100);

  const cat = (st) => (st.catalog && st.catalog.guild) || null;
  const rankName = (st, r) => {
    const c = cat(st);
    return (c && c.ranks && c.ranks[r] && c.ranks[r].name) || RANKS[r] || '';
  };
  const wareName = (st, key) => {
    const list = (st.craft && st.craft.wares) || (st.catalog && st.catalog.wares) || [];
    const w = list.find((x) => x.key === key);
    return w ? w.name : key;
  };
  const styleName = (st, key) => {
    if (!key) return 'простий';
    const s = (st.styleList || []).find((x) => x.key === key) || ((st.catalog && st.catalog.styles) || []).find((x) => x.key === key);
    return s ? s.name : key;
  };
  const myNick = (st) => (st.ctx && st.ctx.me && st.ctx.me.nick) || '';
  const headers = (st) => ({ 'X-Nick': encodeURIComponent(myNick(st)) });
  const itemsOf = (st) => (st.craft && st.craft.items) || [];

  /// «3 дн 4 год», «5 год 12 хв», «12:04».
  function left(api, ms) {
    if (ms <= 0) return 'ось-ось';
    const m = Math.floor(ms / 60000);
    const d = Math.floor(m / 1440);
    const h = Math.floor((m % 1440) / 60);
    if (d > 0) return d + ' дн ' + h + ' год';
    if (h > 0) return h + ' год ' + (m % 60) + ' хв';
    return api.mmss(ms);
  }

  function act(st, api, payload, sound) {
    return api.act(st, 'guild', payload).then((r) => {
      if (r && r.ok && sound) api.sfx(sound);
      return r;
    });
  }

  // ---------- віз ----------

  /// Що покласти в купу на возі з count місць: справжні вироби воза (w.items — вид, розпис, якість) у своїх
  /// пропорціях, найбільших ліпших залишків; кожен вид — хоч одним, поки місць вистачає. Старий сервер без items —
  /// як було: підцілі у своїх пропорціях, решта — горщики.
  function pileKinds(w, count) {
    const items = (w.items || []).filter((x) => x.n > 0);
    if (!items.length) {
      const kinds = [];
      for (const s of w.subs) for (let i = 0; i < Math.min(s.have, s.need); i++) kinds.push({ ware: s.ware, style: '', q: 1 });
      const rest = Math.max(0, w.total - kinds.length);
      for (let i = 0; i < rest; i++) kinds.splice(Math.floor((i * (kinds.length + 1)) / (rest + 1)) + i % 2, 0, { ware: 'pot', style: '', q: 1 });
      while (kinds.length < Math.max(count, 1)) kinds.push({ ware: 'pot', style: '', q: 1 });
      const step = kinds.length / Math.max(1, count);
      return Array.from({ length: count }, (_, i) => kinds[Math.floor(i * step)] || kinds[0]);
    }
    const sum = items.reduce((a, x) => a + x.n, 0);
    const seats = items.map((x) => Math.min(x.n, Math.floor((x.n / sum) * count)));
    // Вид, що не дістав місця через дрібну частку, — хоч один, поки є вільні; далі — найбільші залишки.
    for (let i = 0; i < items.length && seats.reduce((a, b) => a + b, 0) < count; i++) if (!seats[i]) seats[i] = 1;
    const order = items.map((x, i) => i).sort((a, b) => ((items[b].n / sum) * count - seats[b]) - ((items[a].n / sum) * count - seats[a]));
    for (let k = 0; seats.reduce((a, b) => a + b, 0) < count && k < order.length * 4; k++) {
      const i = order[k % order.length];
      if (seats[i] < items[i].n) seats[i]++;
    }
    // Упереміш, а не смугами: кожен вид розкладаємо рівними кроками по всій купі.
    const pile = [];
    items.forEach((x, i) => { for (let j = 0; j < seats[i]; j++) pile.push({ at: (j + 0.5) / seats[i] + i * 1e-3, x }); });
    return pile.sort((a, b) => a.at - b.at).slice(0, count).map((p) => ({ ware: p.x.ware, style: p.x.style || '', q: p.x.q || 1 }));
  }

  /// Віз: віл у ярмі, дерев'яна платформа з бортами, два колеса, купа виробів (до 28), прапорець рівня. viewBox 360×200.
  function wagonSvg(st, api, w) {
    const goal = Math.max(1, w.goal);
    const fill = Math.min(1, w.total / (goal * 2));
    // Хоч щось на возі — хоч один виріб у купі: інакше перші покладені «зникали» до 1/56 цілі.
    const count = w.total > 0 ? Math.max(1, Math.min(w.total, Math.round(fill * 28))) : 0;
    const kinds = pileKinds(w, count);
    const K = 0.5;
    let pile = '';
    for (let i = 0; i < count; i++) {
      const it = kinds[i] || { ware: 'pot', style: '', q: 1 };
      const row = Math.floor(i / 7);
      const col = i % 7;
      const x = 124 + col * 34 + (row % 2) * 17 - 50 * K;
      // Низ виробу — трохи за переднім бортом (y 104): і миску видно, і лежить «у возі».
      const y = 106 - row * 20 - 86 * K;
      // Анімація «впав на віз» — лише для щойно покладених (внутрішня <g>: CSS-transform перебив би атрибут зовнішньої).
      const fresh = st.guildPile != null && i >= st.guildPile;
      pile += '<g transform="translate(' + x.toFixed(1) + ' ' + y.toFixed(1) + ') scale(' + K + ')"><g' + (fresh ? ' class="clkg-ware" style="--d:' + ((i - st.guildPile) * 60) + 'ms"' : '') + '>'
        + api.wareSvg(it.ware, { style: it.style, quality: it.q, slot: 'wg-' + i, wrap: false }) + '</g></g>';
    }
    st.guildPile = count;
    const wheel = (cx) => '<g class="clkg-wheel" style="transform-origin:' + cx + 'px 152px">'
      + '<circle cx="' + cx + '" cy="152" r="28" fill="none" stroke="#5c3b1e" stroke-width="7"/>'
      + '<circle cx="' + cx + '" cy="152" r="6" fill="#3a2412"/>'
      + [0, 45, 90, 135].map((a) => '<path d="M' + cx + ' 126v52" stroke="#6b4423" stroke-width="3" transform="rotate(' + a + ' ' + cx + ' 152)"/>').join('')
      + '</g>';
    const ox = '<g class="clkg-ox">'
      + '<path d="M30 150v28M40 152v26M66 152v26M76 150v28" stroke="#4a3426" stroke-width="6" stroke-linecap="round"/>'
      + '<ellipse cx="54" cy="134" rx="34" ry="22" fill="#6d5140"/>'
      + '<path d="M86 128q10-2 12 10" stroke="#4a3426" stroke-width="3" fill="none"/>'
      + '<ellipse cx="18" cy="124" rx="13" ry="11" fill="#5d4435"/>'
      + '<path d="M10 115q-8-10-2-16M26 115q8-10 2-16" stroke="#e8dcc0" stroke-width="3" fill="none" stroke-linecap="round"/>'
      + '<circle cx="13" cy="122" r="1.8" fill="#111"/><ellipse cx="10" cy="131" rx="5" ry="3.4" fill="#8a6a58"/>'
      + '<path d="M34 112h8v16h-8z" fill="#8b5a2b"/>'
      + '</g>';
    const flag = w.tier > 0
      ? '<g class="clkg-flag"><path d="M344 56v48" stroke="#5c3b1e" stroke-width="3"/><path d="M342 56h-40l10 12-10 12h40z" fill="' + TIER_COLOR[w.tier] + '"/>'
        + '<text x="324" y="73" font-size="12" text-anchor="middle">' + TIER_ICON[w.tier] + '</text></g>'
      : '';
    // Поки виробів на два ряди — небо над возом обрізаємо, щоб порожній віз не був високою порожньою картинкою.
    const top = count > 14 ? 0 : 44;
    return '<svg class="clkg-wagon" viewBox="0 ' + top + ' 360 ' + (200 - top) + '" role="img" aria-label="Віз цеху, повний на ' + Math.floor(w.pct) + ' %">'
      + '<path d="M0 182h360" stroke="rgba(255,255,255,.12)" stroke-width="2"/>'
      + ox + flag
      + '<path d="M104 118L40 114" stroke="#6b4423" stroke-width="5" stroke-linecap="round"/>'
      + pile
      + '<path d="M100 104h250l-8 30H108z" fill="#8f6d4b" stroke="#5c3b1e" stroke-width="2"/>'
      + '<path d="M104 114h242M106 124h238" stroke="#6b4a2c" stroke-width="1.4"/>'
      + '<path d="M100 104v-24M350 104v-24M225 104v-14" stroke="#6b4423" stroke-width="4" stroke-linecap="round"/>'
      + wheel(150) + wheel(300)
      + '</svg>';
  }

  /// Смуга до 200 %: пороги бронзи, срібла й золота, власний внесок — окремим відтінком.
  function barHtml(w) {
    const goal = Math.max(1, w.goal);
    const pct = Math.min(100, (w.total / (goal * 2)) * 100);
    const mine = Math.min(pct, (w.mine / (goal * 2)) * 100);
    // Відсотки під порогами прибрано: гравцеві досить бачити, де бронза, срібло й золото.
    const marks = [1, 2, 3].map((t) => '<span class="clkg-mark t' + t + (w.tier >= t ? ' on' : '') + '" style="left:' + ([0, 50, 75, 100][t]) + '%">'
      + '<b>' + TIER_ICON[t] + '</b></span>').join('');
    return '<div class="clkg-bar"><div class="clkg-fill" style="width:' + pct.toFixed(1) + '%"></div>'
      + '<div class="clkg-mine" style="width:' + mine.toFixed(1) + '%"></div>' + marks + '</div>';
  }

  /// Рядок «на возі»: до шести найчисленніших виробів мініатюрами з кількістю, решта — «+N». Той самий виріб у тому
  /// самому розписі різної якості — однією мініатюрою (на возі їх однаково не розрізнити), сяє найкращий.
  function loadHtml(st, api, w) {
    const byKind = new Map();
    for (const x of w.items || []) {
      if (!(x.n > 0)) continue;
      const k = x.ware + '|' + (x.q ? x.style || '' : '?');
      const was = byKind.get(k);
      if (was) { was.n += x.n; was.q = Math.max(was.q, x.q || 0); } else byKind.set(k, { ware: x.ware, style: x.style || '', q: x.q || 0, n: x.n });
    }
    const items = [...byKind.values()].sort((a, b) => b.n - a.n);
    if (!items.length) return '';
    const esc = (x) => api.esc(st, x);
    const show = items.slice(0, WAGON_KINDS);
    const shownN = show.reduce((a, x) => a + x.n, 0);
    const more = Math.max(0, w.total - shownN);
    const kindsMore = Math.max(0, (w.kinds || items.length) - show.length);
    return '<div class="clkg-load"><span class="muted small">на возі:</span>'
      + show.map((x, i) => '<span class="clkg-lcell q' + (x.q || 1) + '" title="' + esc(wareName(st, x.ware)
        + (x.q ? ' · ' + styleName(st, x.style) : '') + ' × ' + x.n) + '">'
        + api.wareSvg(x.ware, { style: x.style || '', quality: x.q || 1, cls: 'clkg-lico', slot: 'wl-' + i })
        + '<b>' + api.count(x.n) + '</b></span>').join('')
      + (more > 0 ? '<span class="clkg-lmore" title="' + esc('ще ' + kindsMore + ' ' + api.plural(kindsMore, 'вид', 'види', 'видів')
        + ' виробів') + '">+' + api.count(more) + '</span>' : '')
      + '</div>';
  }

  /// «🛒 Усе на віз · N» — уся комора однією дією, крім відкладеного під замовлення (правило й числа — із сервера).
  function giveAllBtn(st, api, v, cls) {
    const a = v.all || { n: 0, keep: 0 };
    if (!st.mine || !(a.n > 0)) return '';
    return '<button type="button" class="' + (cls || 'primary') + ' clkg-giveall" title="'
      + api.esc(st, 'Покласти на віз усе з комори' + (a.keep > 0 ? ', крім ' + a.keep + ' ' + api.plural(a.keep, 'виробу', 'виробів', 'виробів')
        + ' під замовлення гостей і сіл та майстерштук' : '')) + '">🛒 Усе на віз · ' + api.count(a.n) + '</button>';
  }

  function wagonHtml(st, api, v) {
    const esc = (x) => api.esc(st, x);
    const w = v.day;
    const c = cat(st);
    const minGive = (c && c.minGive) || 5;
    const subs = w.subs.map((s) => '<div class="clkg-sub' + (s.have >= s.need ? ' done' : '') + '">'
      + api.wareSvg(s.ware, { quality: 1, cls: 'clkg-ico', slot: 'wsub-' + s.ware })
      + '<span>' + esc(wareName(st, s.ware)) + '</span><b>' + Math.min(s.have, s.need) + '/' + s.need + '</b></div>').join('');
    const givers = w.givers.length
      ? '<div class="clkg-givers">' + w.givers.map((g) => '<span class="clkg-chip' + (g.nick === myNick(st) ? ' me' : '') + '">'
        + esc(g.nick) + ' · ' + g.n + '</span>').join('') + '</div>'
      : '<div class="muted small">Віз ще порожній — хтось має покласти перший горщик.</div>';
    const per = (c && c.perPotter) || 12;
    const shareMax = (c && c.shareMax) || 3;
    const claims = (v.claims || []).map((cl) => '<button type="button" class="primary clkg-claim" data-claim="' + esc(cl.day) + '">'
      + '🛒 Забрати: ' + TIER_ICON[cl.tier] + ' ' + TIER[cl.tier] + (cl.day !== w.id ? ' (вчорашній віз)' : '')
      + ' · +' + api.potsShort(cl.pots) + ' <i class="clkg-pai">×' + pai(cl.share) + ' паю</i></button>').join('');
    // Пай (§E.1): що більше поклав — то більша нагорода. Показуємо і скільки маєш зараз, і скільки до наступного паю.
    // Поки не поклав і п'яти — нагороди не буде взагалі, тож і «×0,5 паю» обіцяти нема чого.
    const share = w.mine >= minGive ? Math.min(shareMax, Math.max(0.5, w.mine / per)) : 0;
    const toNext = w.mine < per * shareMax ? per - (w.mine % per) : 0;
    const paiNote = '<div class="clkg-pai-box">' + (share > 0 ? '<b>×' + pai(share) + '</b> твого паю' : '<b>пай</b> почнеться з ' + minGive + ' виробів')
      + (toNext ? ' <span class="muted small">· ще ' + toNext + ' ' + api.plural(toNext, 'виріб', 'вироби', 'виробів') + ' — і пай більший</span>'
        : ' <span class="muted small">· більше вже не буває</span>')
      + info('Нагорода воза множиться на твій пай: кожні ' + per + ' виробів — ще один пай, до ' + shareMax
        + '. Поклав ' + per + ' — повний пай, ' + (per * shareMax) + ' — три, п\'ять — половина. Рівень воза (бронза/срібло/золото) '
        + 'цех бере разом, а платня — за внесок.') + '</div>';
    const mineNote = w.mine >= minGive
      ? 'ти поклав(ла) ' + w.mine + ' — нагорода твоя, щойно віз дійде до порогу'
      : 'поклади хоч ' + minGive + ' (' + w.mine + ' є) — і нагорода віза буде й твоя';
    const prev = v.prev
      ? '<div class="muted small">Учора: ' + (v.prev.tier ? TIER_ICON[v.prev.tier] + ' ' + TIER[v.prev.tier] : 'до бронзи не дотягли — не біда') + ', разом ' + v.prev.total + '</div>'
      : '';
    return '<section class="clkg-card clkg-day">'
      + '<div class="clk-sub">🐴 Віз цеху на ярмарок · <span class="muted small">від\'їде за <span class="clkg-cd" data-at="' + Date.parse(w.endsAt) + '"></span></span>'
      + info('Село — це всі гончарі сайту разом: один віз на день, дарунки одне одному, хати в гості. Бронза — ціль і всі '
        + 'підцілі; срібло — півтори цілі; золото — дві. Ціль росте з кількістю вчорашніх гончарів (' + w.potters + '). '
        + 'Пропущений день нічого не забирає.') + '</div>'
      + wagonSvg(st, api, w)
      + loadHtml(st, api, w)
      + '<div class="clkg-line"><b>' + w.total + '</b> з ' + w.goal + ' <span class="muted small">· '
      + (w.tier ? TIER_ICON[w.tier] + ' ' + TIER[w.tier] : 'ще до бронзи') + '</span></div>'
      + barHtml(w)
      + '<div class="clkg-subs">' + subs + '</div>'
      + givers
      + paiNote
      + '<div class="clkg-btns">' + giveAllBtn(st, api, v, 'ghost')
      + '<button type="button" class="ghost clkg-give"' + (st.mine && itemsOf(st).length ? '' : ' disabled') + '>🧺 Вибрати, що покласти</button>'
      + claims + '</div>'
      + (v.all && v.all.keep > 0 && v.all.n > 0 ? '<div class="muted small">' + api.count(v.all.keep) + ' ' + api.plural(v.all.keep, 'виріб', 'вироби', 'виробів')
        + ' лишиться: відкладено під замовлення гостей і сіл та майстерштук</div>' : '')
      + '<div class="muted small">' + mineNote + '</div>'
      + prev
      + '</section>';
  }

  // ---------- дарунки ----------

  function giftsHtml(st, api, v) {
    const esc = (x) => api.esc(st, x);
    const c = cat(st);
    const per = (c && c.giftsPerDay) || 3;
    const shelf = v.shelf.length
      ? '<div class="clkg-shelf">' + v.shelf.map((g, i) => '<span class="clkg-gift q' + g.q + '" title="' + esc(QUALITY[g.q] + ' ' + wareName(st, g.ware).toLowerCase()
        + ' · ' + styleName(st, g.style) + ' · від ' + g.from) + '">'
        + api.wareSvg(g.ware, { style: g.style, quality: g.q, cls: 'clkg-mid', slot: 'gsh-' + i })
        + '<i>від ' + esc(g.from) + '</i></span>').join('') + '</div>'
      : '<div class="muted small">Полиця дарунків порожня.</div>';
    return '<section class="clkg-card">'
      + '<div class="clk-sub">🎁 Дарунки <span class="muted small">· сьогодні ще ' + v.gifts.left + ' з ' + per + ' · подаровано ' + v.gifts.sent
      + ' · отримано ' + v.gifts.got + '</span></div>'
      + '<div class="clkg-btns"><button type="button" class="ghost clkg-gifting"' + (st.mine && v.gifts.left > 0 && itemsOf(st).length ? '' : ' disabled') + '>🎁 Подарувати виріб другові</button>'
      + '<button type="button" class="ghost clkg-bragging"' + (st.mine && itemsOf(st).length ? '' : ' disabled') + '>🏺 Похвалитись</button>'
      + '<span class="muted small clkg-bragcd" data-at="' + (Date.parse(v.bragAt) || 0) + '"></span></div>'
      + '<div class="clk-sub small">Полиця дарунків'
      + info('Дарунок від друга стане тут із підписом — продати його не можна, це пам\'ять.') + '</div>' + shelf
      + '</section>';
  }

  // ---------- допомога другові (§E.2) ----------

  /// Плашка діючих бафів: що зараз гріє, від кого й доки. Порожньо — нічого не малюємо.
  function buffsHtml(st, api, v) {
    const esc = (x) => api.esc(st, x);
    const rows = [];
    const row = (kind, b, text) => '<span class="clkg-buff ' + kind + '"><b>' + HELP[kind].icon + '</b>'
      + '<i>' + text + ' <span class="muted">від ' + esc(b.from || 'друга') + '</span></i>'
      + '<u class="clkg-bufcd" data-at="' + (Date.parse(b.until) || 0) + '"></u></span>';
    if (v.buffs.lend) rows.push(row('lend', v.buffs.lend, 'підмайстер у гостях — ліплення ×2'));
    if (v.buffs.cheer) rows.push(row('cheer', v.buffs.cheer, 'похвала — +10 % до всього'));
    return rows.length ? '<div class="clkg-buffs">' + rows.join('') + '</div>' : '';
  }

  function helpHtml(st, api, v) {
    const c = cat(st);
    const cap = (c && c.treatCap) || 120;
    const h = v.help;
    const esc = (x) => api.esc(st, x);
    const r = st.guildRoster;
    const list = r ? sortedFriends(st, api) : [];
    let friends;
    if (!r) friends = '<div class="muted small">Завантажую…</div>';
    else if (!list.length) friends = '<div class="muted small">У цеху поки нікого, крім тебе, — помагати нікому.</div>';
    else {
      friends = '<div class="clkg-flist">' + list.slice(0, FRIENDS_INLINE).map((x) => friendBtn(st, api, x)).join('') + '</div>'
        + (list.length > FRIENDS_INLINE ? '<button type="button" class="ghost small clkg-choose">🔎 Усі друзі · ' + list.length + '</button>' : '');
    }
    return '<section class="clkg-card">'
      + '<div class="clk-sub">🤝 Помогти другові'
      + info('Село тримається на тому, що сильніший підставляє плече. Гостинець: платиш своїми хвилинами пасиву, '
        + 'а друг дістає вдвічі більше хвилин СВОГО — тож твоя гора його гру не зламає, але день-два росту дасть '
        + '(не більше ' + cap + ' хв на день на одного). Підмайстер — раз на день, і в друга добу ліплять удвічі швидше; '
        + 'другий підмайстер (від іншого друга чи завтра) продовжує першого, до 72 год наперед. '
        + 'Похвала — раз на день на друга: година +10 % до всього, похвали складаються до 4 год.') + '</div>'
      + '<p class="muted small clk-note">Тапни друга — побачиш, що можеш для нього зробити просто зараз.'
      + ' <span class="clkg-hint">' + esc((h.lendLeft ? '🧑‍🎓 підмайстер вільний' : '🧑‍🎓 підмайстер сьогодні вже в гостях')
        + ' · 🎁 гостинців подаровано ' + (h.treats || 0)) + '</span></p>'
      + friends
      + '<div class="muted small">Тобі сьогодні ще можуть принести ' + h.treatLeft + ' хв гостинців із ' + cap + '.</div>'
      + '</section>';
  }

  // ---------- картка друга, «Хто чекає», стрічка цеху (12-те дошліфування) ----------

  const FRIENDS_INLINE = 12;
  const KIND = {
    treat: { icon: '🎁', word: 'гостинець' }, lend: { icon: '🧑‍🎓', word: 'підмайстер' }, cheer: { icon: '👏', word: 'похвала' },
    gift: { icon: '🎁', word: 'дарунок' }, toloka: { icon: '🤝', word: 'толока' }, thanks: { icon: '💛', word: 'дякую' },
  };
  const ms = (x) => (typeof x === 'number' ? x : Date.parse(x) || 0);
  const hhmm = (t) => new Date(t).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' });
  /// «18:40», «завтра 18:40», «12 жовт. 18:40» — до котрої гріє баф.
  function at(t, now) {
    const d = new Date(t);
    const n = new Date(now);
    if (d.toDateString() === n.toDateString()) return hhmm(t);
    if (d.toDateString() === new Date(now + 864e5).toDateString()) return 'завтра ' + hhmm(t);
    return d.toLocaleDateString('uk-UA', { day: 'numeric', month: 'short' }) + ' ' + hhmm(t);
  }
  /// «щойно», «12 хв тому», «3 год тому», «учора», «5 дн тому».
  function ago(t, now) {
    const m = Math.max(0, Math.round((now - t) / 60000));
    if (m < 2) return 'щойно';
    if (m < 60) return m + ' хв тому';
    if (m < 1440) return Math.floor(m / 60) + ' год тому';
    return m < 2880 ? 'учора' : Math.floor(m / 1440) + ' дн тому';
  }
  const ava = (nick, cls) => (window.HPeople && HPeople.ava ? HPeople.ava(nick, 'ava ' + (cls || 'sm')) : '');
  const fitsNeed = (n, it) => it.ware === n.ware && it.q >= n.q && (!n.style || it.style === n.style);
  const friendsOf = (st) => ((st.guildRoster && st.guildRoster.potters) || []).filter((p) => !p.me);

  /// Чому цьому другові варто помогти саме зараз — чипи «Хто чекає». Рахує лише з картки (look) і моєї комори.
  function reasons(st, api, p) {
    const L = p.look;
    const v = st.guild;
    if (!L || !v) return [];
    const out = [];
    const t = L.toloka;
    if (t && Array.isArray(t.needs)) {
      const mine = itemsOf(st);
      const laid = ms(t.endsAt) > api.serverNow(st);
      if (!laid) {
        const n = t.needs.find((x) => x.left > 0 && mine.some((it) => fitsNeed(x, it)));
        if (n) out.push({ k: 'toloka', text: 'толока: бракує ' + n.left + ' × ' + wareName(st, n.ware).toLowerCase() + ' — у тебе є' });
      } else if ((t.helpers || []).length < 3 && !(t.helpers || []).some((h) => h.toLowerCase() === myNick(st).toLowerCase())
        && t.needs.some((x) => mine.some((it) => fitsNeed(x, it)))) out.push({ k: 'toloka', text: 'толока будується — твій виріб скоротить' });
    }
    if (v.help.lendLeft && L.lend && !L.lend.until && !L.lend.full) out.push({ k: 'lend', text: 'ще без підмайстра' });
    if (L.cheer && L.cheer.can && !L.cheer.full) out.push({ k: 'cheer', text: 'похвали сьогодні ще не було' });
    if (L.thank) out.push({ k: 'thanks', text: 'помагав(ла) тобі — подякуй' });
    return out;
  }

  /// Друзі в порядку «кому помогти»: онлайн зараз → кому ще можна щось від тебе → з ким нещодавно взаємодіяв → абетка.
  function sortedFriends(st, api) {
    const key = (x) => [x.p.look && x.p.look.online ? 0 : 1, x.why.length ? 0 : 1, -ms(x.p.look && x.p.look.lastAt)];
    return friendsOf(st).map((p) => ({ p, why: reasons(st, api, p) })).sort((a, b) => {
      const A = key(a);
      const B = key(b);
      for (let i = 0; i < A.length; i++) if (A[i] !== B[i]) return A[i] - B[i];
      return a.p.nick.localeCompare(b.p.nick, 'uk');
    });
  }

  function friendBtn(st, api, x, chips) {
    const esc = (s) => api.esc(st, s);
    const on = x.p.look && x.p.look.online;
    return '<button type="button" class="ghost clkg-fbtn" data-card="' + esc(x.p.nick) + '">' + ava(x.p.nick)
      + '<span class="clkg-fname">' + (on ? '<i class="clkg-on" title="зараз на сайті"></i>' : '') + esc(x.p.nick) + '</span>'
      + (chips && x.why.length ? '<span class="clkg-chips">' + x.why.map((w) => '<span class="clkg-chip k-' + w.k + '">' + esc(w.text) + '</span>').join('') + '</span>'
        : x.why.length ? '<span class="clkg-dot" title="' + esc(x.why.map((w) => w.text).join('; ')) + '">' + x.why.length + '</span>' : '')
      + '</button>';
  }

  /// «Хто чекає допомоги» нагорі «Села»: до шести друзів, кому можна помогти зараз, з причинами; під ним — стрічка цеху.
  function waitHtml(st, api) {
    const r = st.guildRoster;
    if (!r) return '<section class="clkg-card clkg-wait"><div class="clk-sub">🙋 Хто чекає допомоги</div><div class="muted small">Завантажую…</div></section>';
    const list = sortedFriends(st, api).filter((x) => x.why.length).slice(0, 6);
    const feed = feedHtml(st, api, r.feed || []);
    if (!list.length && !feed) return '';
    return '<section class="clkg-card clkg-wait">'
      + (list.length ? '<div class="clk-sub">🙋 Хто чекає допомоги</div><div class="clkg-wlist">' + list.map((x) => friendBtn(st, api, x, true)).join('') + '</div>' : '')
      + feed + '</section>';
  }

  /// Стрічка цеху за сьогодні: «Smaug → Микола: підмайстер». Згортається (пам'ятаємо в localStorage).
  function feedHtml(st, api, feed) {
    if (!feed.length) return '';
    const esc = (x) => api.esc(st, x);
    const now = api.serverNow(st);
    const open = api.storeGet('clk.guild.feedOpen', '1') !== '0';
    return '<details class="clkg-feed"' + (open ? ' open' : '') + '><summary class="small">📜 Стрічка цеху сьогодні · ' + feed.length + '</summary>'
      + feed.map((d) => '<div class="clkg-frow small">' + (KIND[d.kind] ? KIND[d.kind].icon : '•') + ' <b>' + esc(d.from) + '</b> → <b>' + esc(d.to) + '</b>: '
        + esc(KIND[d.kind] ? KIND[d.kind].word : d.kind) + ' <span class="muted">· ' + esc(ago(ms(d.at), now)) + '</span></div>').join('')
      + '</details>';
  }

  /// «Тобі допомогли»: що прийшло за дві доби — з кнопкою «Подякувати» (похвалою, якщо сьогодні можна, інакше словом).
  function gotHtml(st, api, v) {
    const now = api.serverNow(st);
    const thanked = new Set((v.thanked || []).map((x) => String(x).toLowerCase()));
    const list = (v.got || []).filter((g) => now - ms(g.at) < 48 * 3600e3).slice(0, 5);
    if (!list.length) return '';
    const esc = (x) => api.esc(st, x);
    const cheered = new Set((v.help.cheered || []).map((x) => String(x).toLowerCase()));
    const seen = new Set();
    return '<section class="clkg-card clkg-got"><div class="clk-sub">💌 Тобі допомогли</div>' + list.map((g) => {
      const who = String(g.from || '').toLowerCase();
      // Кнопка — одна на друга (на найсвіжішому рядку), і не для «дякую» й не для безіменного «друга».
      const can = st.mine && g.kind !== 'thanks' && g.from !== 'друг' && !thanked.has(who) && !seen.has(who);
      seen.add(who);
      return '<div class="clkg-grow small">' + (KIND[g.kind] ? KIND[g.kind].icon : '•') + ' <b>' + esc(g.from) + '</b>: '
        + esc(KIND[g.kind] ? KIND[g.kind].word : g.kind) + (g.what ? ' · ' + esc(g.what) : '') + ' <span class="muted">· ' + esc(ago(ms(g.at), now)) + '</span>'
        + (can ? ' <button type="button" class="ghost small clkg-thank" data-thank="' + esc(g.from) + '">'
          + (cheered.has(who) ? '💛 Подякувати' : '👏 Подякувати похвалою') + '</button>' : '')
        + '</div>';
    }).join('') + '</section>';
  }

  function thank(st, api, nick, btn) {
    if (btn) btn.disabled = true;
    act(st, api, { op: 'thank', to: nick }, 'brag').then((r) => {
      if (r && r.message) api.toast(st, r.message, r.ok ? 'ok' : '');
      if (r && r.ok) loadRoster(st, api, true);
      else if (btn) btn.disabled = false;
    });
  }

  /// Вибір друга, коли їх більше дванадцяти: пошук за ніком, той самий порядок.
  function openChooser(st, api, focus) {
    st.guildChoose = { focus: focus || '', q: '' };
    loadRoster(st, api, true);
    renderChooser(st, api);
  }

  function renderChooser(st, api) {
    const c = st.guildChoose;
    if (!c) return;
    const list = sortedFriends(st, api);
    const head = '<div class="clk-sub">' + (c.focus === 'gift' ? '🎁 Кому подарувати?' : '🤝 Кому помогти?') + '</div>'
      + (list.length > FRIENDS_INLINE ? '<input type="search" class="clkg-search" placeholder="Пошук за ніком" value="' + api.esc(st, c.q) + '">' : '');
    const rows = () => {
      const q = c.q.trim().toLowerCase();
      const shown = list.filter((x) => !q || x.p.nick.toLowerCase().includes(q));
      return !st.guildRoster ? '<div class="muted small">Завантажую…</div>'
        : shown.length ? shown.map((x) => friendBtn(st, api, x, true)).join('')
          : '<div class="muted small">' + (list.length ? 'Нікого з таким ніком' : 'У цеху поки нікого, крім тебе.') + '</div>';
    };
    let body = st.guildChooseBody;
    if (!(api.overlayOpen(st) && body && body.isConnected)) {
      body = api.overlay(st, head + '<div class="clkg-wlist clkg-clist"></div>', { cls: 'clkg-ov', onClose: () => { if (st.guildChoose === c) { st.guildChoose = null; st.guildChooseBody = null; } } });
      st.guildChooseBody = body;
      const input = body.querySelector('.clkg-search');
      if (input) input.oninput = () => { c.q = input.value; draw(); };
    }
    const box = body.querySelector('.clkg-clist');
    const draw = () => {
      const html = rows();
      if (box._sig === html) return;
      box._sig = html;
      box.innerHTML = html;
      for (const b of box.querySelectorAll('[data-card]')) b.onclick = () => openCard(st, api, b.dataset.card, c.focus);
    };
    draw();
  }

  /// Картка друга: усе, що можу для нього зробити, в одному вікні. look — з цеху (залишок стелі гостинця, до котрої
  /// продовжиться підмайстер і похвала), толока — з його хати. Після дії картка лишається: рядок результату й свіжа доступність.
  function openCard(st, api, nick, focus) {
    const p = friendsOf(st).find((x) => x.nick.toLowerCase() === String(nick).toLowerCase());
    const c = { nick: p ? p.nick : nick, rank: p ? p.rank : null, look: p ? p.look : null, house: null, loaded: false,
      gift: focus === 'gift', msg: '', ok: true, arm: '', armAt: 0, busy: false };
    st.guildCard = c;
    const body = api.overlay(st, '<div class="clkg-cmain"></div><div class="clkg-ctol"></div>', {
      cls: 'clkg-ov clkg-cardov', onClose: () => { if (st.guildCard === c) { st.guildCard = null; st.guildCardBody = null; } },
    });
    st.guildCardBody = body;
    body.onclick = (e) => cardClick(st, api, c, e);
    renderCard(st, api);
    refreshCard(st, api, c);
  }

  function refreshCard(st, api, c) {
    fetch('/api/games/clicker/house?nick=' + encodeURIComponent(c.nick), { headers: headers(st) })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (st.guildCard !== c) return;
        c.loaded = true;
        if (d) {
          c.house = d;
          c.nick = d.nick || c.nick;
          if (d.rank != null) c.rank = d.rank;
          if (d.look) c.look = d.look;
        }
        renderCard(st, api);
        // Толока друга — той самий розділ, що й у хаті (clicker-toloka.js): поточний етап, «Піднести», наступний.
        const tol = st.guildCardBody && st.guildCardBody.querySelector('.clkg-ctol');
        if (tol && d && typeof api.tolokaHouse === 'function') api.tolokaHouse(st, tol, d);
      })
      .catch(() => { if (st.guildCard === c) { c.loaded = true; renderCard(st, api); } });
  }

  function cardHtml(st, api, c) {
    const v = st.guild;
    const esc = (x) => api.esc(st, x);
    const now = api.serverNow(st);
    const L = c.look;
    const name = esc(c.nick);
    let status = '';
    if (L && L.online) status = '<span class="clkg-on"></span> зараз на сайті';
    else if (L && L.seenAt) status = 'був(ла) ' + esc(ago(ms(L.seenAt), now));
    let html = '<div class="clkg-chead">' + ava(c.nick, 'xl') + '<div><div class="clk-sub">' + name
      + (c.rank != null ? ' <span class="muted small">· ' + RANK_ICON[c.rank] + ' ' + esc(rankName(st, c.rank)) + '</span>' : '') + '</div>'
      + '<div class="muted small">' + status + '</div></div>'
      + '<button type="button" class="ghost small clkg-chouse" data-house="' + name + '">🏠 Хата</button></div>';
    if (c.msg) html += '<div class="clkg-cmsg small' + (c.ok ? ' ok' : ' bad') + '" role="status">' + esc(c.msg) + '</div>';
    if (!v || !v.enabled) return html + '<div class="muted small">Цех зараз зачинений.</div>';
    if (!L) return html + '<div class="muted small">' + (c.loaded ? 'Цей гончар давно не заходив — картка порожня.' : 'Завантажую…') + '</div>';
    html += '<div class="clk-sub small">Що можу зробити</div>';
    const arm = (id) => c.arm === id && Date.now() - c.armAt < CONFIRM_MS;
    // 🎁 гостинець
    const sizes = (v.help.sizes || []).length ? v.help.sizes : [{ minutes: 10 }, { minutes: 30 }, { minutes: 60 }];
    html += '<div class="clkg-crow"><div class="clkg-ctitle">🎁 Гостинець <span class="muted small">· ' + name + ' сьогодні ще влізе ' + L.treatLeft + ' хв</span></div>'
      + '<div class="clkg-sizes">' + sizes.map((s) => {
        const give = s.minutes * 2;
        const why = !st.mine ? 'лише у своєму колі' : give > L.treatLeft ? 'не влізе: ще ' + L.treatLeft + ' хв'
          : s.pots != null && (st.shown || 0) < s.pots ? 'бракує глеків' : '';
        const id = 'treat:' + s.minutes;
        return '<button type="button" class="ghost clkg-size' + (arm(id) ? ' arm' : '') + '" data-csend="' + id + '"' + (why || c.busy ? ' disabled' : '') + '>'
          + (arm(id) ? '<b>Так, надіслати</b><i>−' + esc(api.potsShort(s.pots || 0)) + '</i><u>натисни ще раз</u>'
            : '<b>' + s.minutes + ' хв</b><i>' + (s.pots != null ? '−' + esc(api.potsShort(s.pots)) : '') + '</i><u>' + (why || 'другові +' + give + ' хв його пасиву') + '</u>')
          + '</button>';
      }).join('') + '</div></div>';
    // 🧑‍🎓 підмайстер і 👏 похвала — безплатні, один тап.
    const free = (kind, b, can, cap) => {
      let note;
      let ok = st.mine && can && !b.full;
      if (!can) note = kind === 'lend' ? 'сьогодні твій підмайстер уже в гостях' : 'сьогодні ' + name + ' уже чув(ла) від тебе добре слово';
      else if (b.full) note = 'у ' + name + ' вже на ' + cap + ' наперед — пізніше';
      else if (b.until) note = 'у ' + name + ' вже є до ' + at(ms(b.until), now) + ' — твій продовжить до ' + at(ms(b.after), now);
      else note = (kind === 'lend' ? 'добу ліплення ×2' : 'година +10 % до всього') + ' — до ' + at(ms(b.after), now);
      if (c.busy) ok = false;
      return '<div class="clkg-crow"><button type="button" class="ghost clkg-cbtn" data-csend="' + kind + '"' + (ok ? '' : ' disabled') + '>'
        + HELP[kind].icon + ' ' + HELP[kind].name + '</button><span class="muted small">' + esc(note) + '</span></div>';
    };
    html += free('lend', L.lend, v.help.lendLeft, '72 год');
    html += free('cheer', L.cheer, L.cheer.can, '4 год');
    // 🎁 виріб із комори
    const items = itemsOf(st);
    const left = v.gifts.left;
    html += '<div class="clkg-crow"><button type="button" class="ghost clkg-cbtn clkg-cgift"' + (st.mine && left > 0 && items.length ? '' : ' disabled') + '>🎁 Подарувати виріб</button>'
      + '<span class="muted small">' + (left > 0 ? 'ще ' + left + ' сьогодні' : 'сьогодні вже всі три') + (items.length ? '' : ' · комора порожня') + '</span></div>';
    if (c.gift && left > 0 && items.length) {
      html += '<div class="clkw-items clkg-pick">' + items.map((it, i) => itemRow(st, api, it, i,
        '<button type="button" class="ghost small' + (arm('gift:' + it.key) ? ' arm' : '') + '" data-csend="gift:' + esc(it.key) + '"' + (c.busy ? ' disabled' : '') + '>'
        + (arm('gift:' + it.key) ? 'Так, подарувати' : 'Подарувати') + '</button>')).join('') + '</div>';
    }
    if (L.thank) {
      html += '<div class="clkg-crow"><button type="button" class="ghost clkg-cbtn" data-thank="' + name + '"' + (c.busy ? ' disabled' : '') + '>'
        + (L.cheer.can && !L.cheer.full ? '👏 Подякувати похвалою' : '💛 Подякувати') + '</button><span class="muted small">' + name + ' тобі помагав(ла)</span></div>';
    }
    return html;
  }

  const CONFIRM_MS = 4000;

  function renderCard(st, api) {
    const c = st.guildCard;
    const body = st.guildCardBody;
    if (!c || !body || !body.isConnected) return;
    const main = body.querySelector('.clkg-cmain');
    const html = cardHtml(st, api, c);
    if (main._sig === html) return;
    main._sig = html;
    main.innerHTML = html;
  }

  function cardClick(st, api, c, e) {
    const house = e.target.closest('.clkg-chouse');
    if (house) { openHouse(st, api, c.nick); return; }
    const th = e.target.closest('[data-thank]');
    if (th && th.closest('.clkg-cmain')) {
      // Подвійний тап не шле двох подяк: поки перша в дорозі, кнопка вимкнена (як «Подарувати» нижче).
      if (!human(e) || c.busy) return;
      c.busy = true;
      renderCard(st, api);
      act(st, api, { op: 'thank', to: c.nick }, 'brag').then((r) => cardDone(st, api, c, r, null));
      return;
    }
    if (e.target.closest('.clkg-cgift')) { c.gift = !c.gift; renderCard(st, api); return; }
    const b = e.target.closest('[data-csend]');
    if (!b || b.disabled || !human(e) || c.busy) return;
    const id = b.dataset.csend;
    const paid = id.startsWith('treat:') || id.startsWith('gift:');
    // Платне — з підтвердженням: перший тап озброює кнопку на кілька секунд, другий — шле.
    if (paid && !(c.arm === id && Date.now() - c.armAt < CONFIRM_MS)) {
      c.arm = id;
      c.armAt = Date.now();
      renderCard(st, api);
      setTimeout(() => { if (st.guildCard === c && c.arm === id) { c.arm = ''; renderCard(st, api); } }, CONFIRM_MS + 50);
      return;
    }
    c.arm = '';
    c.busy = true;
    renderCard(st, api);
    const L = c.look || {};
    let payload;
    let ok;
    if (id.startsWith('treat:')) payload = { op: 'treat', to: c.nick, minutes: +id.slice(6) };
    else if (id.startsWith('gift:')) payload = { op: 'gift', nick: c.nick, key: id.slice(5) };
    else {
      payload = { op: id, to: c.nick };
      const b2 = L[id];
      if (b2 && b2.after) ok = '✓ ' + (id === 'lend' ? 'Підмайстер у ' : 'Похвала гріє ') + c.nick + ' до ' + at(ms(b2.after), api.serverNow(st));
    }
    act(st, api, payload, id === 'lend' ? 'wagon' : id === 'cheer' ? 'brag' : 'gift').then((r) => cardDone(st, api, c, r, ok));
  }

  function cardDone(st, api, c, r, ok) {
    c.busy = false;
    if (st.guildCard !== c) return;
    c.ok = !!(r && r.ok);
    c.msg = c.ok ? ok || '✓ ' + String((r && r.message) || 'Готово').replace(/^\S+\s/, '') : (r && r.message) || 'Не вийшло — спробуй ще';
    renderCard(st, api);
    if (c.ok) { loadRoster(st, api, true); refreshCard(st, api, c); }
  }

  // ---------- ранг ----------

  function rankHtml(st, api, v) {
    const esc = (x) => api.esc(st, x);
    const c = cat(st);
    const path = RANKS.map((_, r) => '<span class="clkg-step' + (v.rank === r ? ' on' : v.rank > r ? ' done' : '') + '">'
      + '<b>' + RANK_ICON[r] + '</b><i>' + esc(rankName(st, r)) + '</i></span>').join('<span class="clkg-dash"></span>');
    const perks = [];
    if (v.rank >= 1) {
      perks.push('<label class="clkg-auto"><input type="checkbox" class="clkg-autobox"' + (v.autoOff ? '' : ' checked') + (st.mine ? '' : ' disabled') + '> '
        + '🔥 Автогорно: підмайстри самі палять повну сушарню (якість звичайна)</label>');
    }
    if (v.rank >= 2) perks.push('<div class="small">🏺 +' + v.kilnSlots + ' ' + api.plural(v.kilnSlots, 'місце', 'місця', 'місць')
      + ' у горні · вироби відкриваються на ' + (v.rank - 1) + ' ' + api.plural(v.rank - 1, 'щабель', 'щаблі', 'щаблів') + ' раніше</div>');
    if (v.rank >= 3) perks.push('<div class="small">🐴 Нагорода воза ×' + api.dec(v.wagonMult || 1.5)
      + ' · титул «' + esc(rankName(st, v.rank)) + '» у похвалах і в хаті</div>');
    const perkBox = perks.length ? '<div class="clkg-perks">' + perks.join('') + '</div>' : '';

    // Цехмістр: вище нема куди — самий рядок і перки.
    if (!v.next) {
      return '<section class="clkg-card">'
        + '<div class="clk-sub">' + RANK_ICON[v.rank] + ' Ранг: ' + esc(rankName(st, v.rank)) + '</div>'
        + '<div class="muted small">🎖️ Вище в селі лише небо.</div>' + perkBox + '</section>';
    }

    const n = v.next;
    const p = n.piece;
    const share = (have, need) => (need > 0 ? Math.min(1, have / need) : 1);
    // Смужка — по найвідсталішій умові: саме вона й тримає ранг.
    const done = Math.min(share(n.fired.have, n.fired.need), share(n.given.have, n.given.need),
      n.styles.need ? share(n.styles.have, n.styles.need) : 1, p.have ? 1 : 0.999);
    const row = (label, have, need) => '<div class="clkg-req' + (have >= need ? ' ok' : '') + '"><span>' + label + '</span>'
      + '<div class="clkg-rbar"><i style="width:' + (share(have, need) * 100).toFixed(1) + '%"></i></div>'
      + '<b>' + api.count(Math.min(have, need)) + '/' + api.count(need) + '</b></div>';
    // Прикметник якості, узгоджений з родом виробу (розкішний лев — майстерштук старійшини).
    const gender = ['bowl', 'makitra', 'tile'].includes(p.ware) ? 1 : p.ware === 'barrel' ? 2 : 0;
    const adj = [['дзвінкий', 'дзвінка', 'дзвінке'], ['розкішний', 'розкішна', 'розкішне']][(p.q || 3) >= 4 ? 1 : 0][gender];
    const pieceText = adj + ' ' + wareName(st, p.ware).toLowerCase()
      + (p.style ? ' — ' + ((c && c.styleWords && c.styleWords[p.style]) || styleName(st, p.style).toLowerCase()) : ' у будь-якому розписі');
    const perk = c && c.ranks && c.ranks[n.rank] ? c.ranks[n.rank].perk : '';
    const ready = n.ready && p.have;
    // Одним рядком: хто ти зараз, куди йдеш і як далеко зайшов. Умови — за «що потрібно ▾».
    return '<section class="clkg-card">'
      + '<div class="clk-sub">' + RANK_ICON[v.rank] + ' Ранг: ' + esc(rankName(st, v.rank))
      + '<span class="muted small"> · далі «' + esc(rankName(st, n.rank)) + '»</span>'
      + info('Ранг у селі росте від роботи: обпалені вироби, покладене на віз, розписи в колекції — і майстерштук, '
        + 'який треба виліпити, обпалити дзвінким і принести. Кожен ранг дає перк назавжди.') + '</div>'
      + '<div class="clkg-bar sm"><div class="clkg-fill" style="width:' + (done * 100).toFixed(1) + '%"></div></div>'
      + (ready
        ? '<button type="button" class="primary clkg-master">🎓 Здати майстерштук — ти готовий(а)</button>'
        : '<div class="muted small">' + (p.have ? 'майстерштук уже в коморі — лишились умови' : 'треба ще ' + esc(pieceText)) + '</div>')
      + '<details class="clkg-need"><summary>що потрібно</summary>'
      + '<div class="clkg-path">' + path + '</div>'
      + row('обпалено виробів', n.fired.have, n.fired.need)
      + row('покладено на вози', n.given.have, n.given.need)
      + (n.styles.need ? row('розписів у колекції', n.styles.have, n.styles.need) : '')
      + '<div class="clkg-piece' + (p.have ? ' have' : '') + '">'
      + api.wareSvg(p.ware, { style: p.style || 'kosiv', quality: Math.min(3, p.q || 3), cls: 'clkg-big', slot: 'piece' })
      + '<div><b>Майстерштук</b><span class="small">' + esc(pieceText) + '</span>'
      + '<span class="muted small">' + (p.have ? '✓ лежить у коморі' : 'виліпи, обпали ' + ((p.q || 3) >= 4 ? 'розкішним' : 'дзвінким') + ' — і принеси селу') + '</span></div></div>'
      + (perk ? '<div class="muted small">Перк: ' + esc(perk) + '</div>' : '')
      + (ready ? '' : '<button type="button" class="ghost clkg-master" disabled>🎓 Здати майстерштук</button>')
      + '</details>'
      + perkBox
      + '</section>';
  }

  // ---------- гончарі й хата ----------

  function rosterHtml(st, api) {
    const esc = (x) => api.esc(st, x);
    const r = st.guildRoster;
    if (!r) return '<section class="clkg-card"><div class="clk-sub">👥 Гончарі цеху</div><div class="muted small">Кличемо гончарів…</div></section>';
    // Значки звань (clicker-titles.md): до трьох біля ніка, «Перший гончар округи» — золотим ніком із короною.
    const badges = (p) => {
      // Корона вже стоїть перед золотим ніком — вдруге серед значків її не треба.
      const list = (p.badges || []).filter((b) => !(p.first && b.key === 'first'));
      return list.length ? ' <span class="clkg-badges">' + list.map((b) => '<span title="' + esc(b.name) + '">' + b.icon + '</span>').join('') + '</span>' : '';
    };
    const rows = r.potters.length
      ? r.potters.map((p) => '<div class="clkg-potter' + (p.me ? ' me' : '') + (p.first ? ' first' : '') + '">'
        + '<span class="clkg-pico">' + RANK_ICON[p.rank] + '</span>'
        + '<div class="clkg-pname"><b>' + esc(p.nick) + (p.me ? ' <span class="muted small">(ти)</span>' : '') + badges(p) + '</b>'
        + '<span class="muted small">' + esc(rankName(st, p.rank)) + (p.gave ? ' · на возі ' + p.gave : '') + '</span></div>'
        + (p.me ? '' : '<button type="button" class="ghost small" data-card="' + esc(p.nick) + '" title="Що можу зробити">🤝</button>')
        + '<button type="button" class="ghost small" data-house="' + esc(p.nick) + '">🏠 Зазирнути в хату</button></div>').join('')
      : '<div class="muted small">Поки нікого — зайди пізніше.</div>';
    return '<section class="clkg-card"><div class="clk-sub">👥 Гончарі цеху <span class="muted small">· ' + r.potters.length + '</span></div>' + rows + '</section>';
  }

  function loadRoster(st, api, force) {
    if (!force && Date.now() - (st.guildRosterAt || 0) < ROSTER_MS) return;
    st.guildRosterAt = Date.now();
    fetch('/api/games/clicker/guild', { headers: headers(st) })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (!d || !Array.isArray(d.potters) || !st.guildPane) return;
        st.guildRoster = d;
        paint(st, api);
        // Свіжа картка друга зі списку — лише поки своєї (з хати) ще нема.
        const c = st.guildCard;
        if (c && !c.house) {
          const p = d.potters.find((x) => !x.me && x.nick.toLowerCase() === c.nick.toLowerCase());
          if (p && p.look) c.look = p.look;
        }
        if (c) renderCard(st, api);
        if (st.guildChoose) renderChooser(st, api);
      })
      .catch(() => { /* без списку просто не буде кому дарувати — спробуємо за хвилину */ });
  }

  /// Хата друга: стіни, піч, полиця з найкращими виробами, полиця дарунків, прикраси, знаряддя, емблеми драбини.
  function friendHouseSvg(st, api, h) {
    const esc = (x) => api.esc(st, x);
    const ware = (w, style, q, x, y, s, slot) => '<g transform="translate(' + x + ' ' + y + ') scale(' + s + ')">'
      + api.wareSvg(w, { style, quality: q, slot, wrap: false }) + '</g>';
    let s = '<rect width="360" height="300" fill="#2a1d14"/>'
      + '<rect y="236" width="360" height="64" fill="#3a2a1c"/><path d="M0 236h360" stroke="#5c4530" stroke-width="3"/>'
      + '<path d="M0 0h360v10H0z" fill="#1d140e"/>';
    // Вікно з калиною, якщо є; інакше — просто вікно.
    s += '<rect x="138" y="26" width="84" height="62" rx="4" fill="#0c1a2b" stroke="#6b4423" stroke-width="4"/><path d="M180 26v62M138 57h84" stroke="#6b4423" stroke-width="3"/>';
    if (h.decor.includes('window')) s += '<g fill="#d7372b"><circle cx="150" cy="80" r="3"/><circle cx="156" cy="84" r="3"/><circle cx="153" cy="76" r="2.6"/></g>';
    if (h.decor.includes('icon')) s += '<rect x="20" y="24" width="34" height="42" rx="3" fill="#5a3a1a" stroke="#d9a92f" stroke-width="1.5"/><circle cx="37" cy="40" r="7" fill="#f4c542" opacity=".9"/>';
    if (h.decor.includes('towel')) s += '<path d="M232 20h40v46l-20 8-20-8z" fill="#f4efe3"/><path d="M236 46h32M236 52h32" stroke="#d7372b" stroke-width="2"/>';
    // Полиця найкращих виробів.
    s += '<path d="M16 150h150" stroke="#6b4423" stroke-width="5"/>';
    (h.best || []).forEach((b, i) => { s += ware(b.ware, b.style, b.q, 20 + i * 48, 150 - 86 * 0.5, 0.5, 'fhb-' + i); });
    if (!(h.best || []).length) s += '<text x="90" y="140" font-size="10" fill="#b8a38a" text-anchor="middle">комора порожня</text>';
    // Полиця дарунків.
    s += '<path d="M190 150h156" stroke="#6b4423" stroke-width="5"/>';
    (h.gifts || []).slice(0, 6).forEach((g, i) => { s += ware(g.ware, g.style, g.q, 192 + i * 26, 150 - 86 * 0.3, 0.3, 'fhg-' + i); });
    s += '<text x="268" y="164" font-size="9" fill="#b8a38a" text-anchor="middle">полиця дарунків' + ((h.gifts || []).length ? ' · ' + h.gifts.length : '') + '</text>';
    // Піч і коло.
    s += '<path d="M280 236v-50q0-18 30-18t30 18v50z" fill="#8f6d4b" stroke="#5c4530" stroke-width="1.5"/><rect x="298" y="200" width="24" height="26" rx="4" fill="#2a1508"/>'
      + '<path class="clkg-fire" d="M310 224c-7-6-5-14 0-19 2 5 5 5 3 11 3-3 4-6 3-11 6 6 5 15-6 19z" fill="#ff8a3d"/>';
    s += '<ellipse cx="180" cy="232" rx="42" ry="8" fill="#1b1310"/><rect x="176" y="200" width="8" height="30" fill="#3a2a1c"/>';
    s += ware('jug', h.wear || '', 1, 180 - 50 * 0.55, 200 - 86 * 0.55 + 6, 0.55, 'fhw');
    if (h.decor.includes('rooster')) s += '<text x="40" y="228" font-size="22">🐓</text>';
    if (h.decor.includes('dog')) s += '<text x="226" y="262" font-size="22">🐕</text>';
    if (h.decor.includes('chest')) s += '<rect x="16" y="244" width="54" height="34" rx="5" fill="#8b3a22" stroke="#4a1e10"/><circle cx="43" cy="262" r="4" fill="#f2c230"/>';
    // Знаряддя на стіні.
    (h.tools || []).forEach((t, i) => { s += '<text x="' + (18 + (i % 5) * 24) + '" y="' + (96 + Math.floor(i / 5) * 24) + '" font-size="16">' + (TOOL_ICON[t] || '🔧') + '</text>'; });
    // Емблеми драбини праворуч.
    (h.ladder || []).filter((l) => TIER_EMBLEM[l.key]).forEach((l, i) => {
      s += '<text x="' + (244 + (i % 5) * 22) + '" y="' + (100 + Math.floor(i / 5) * 24) + '" font-size="15"><title>' + esc(l.name + ' · ' + l.level) + '</title>' + TIER_EMBLEM[l.key] + '</text>';
    });
    // Табличка з рангом.
    const title = (h.rank >= 3 ? 'Цехмістр ' : '') + h.nick;
    s += '<rect x="96" y="276" width="168" height="20" rx="4" fill="#6b4423"/><text x="180" y="290" font-size="11" fill="#f4efe3" text-anchor="middle">'
      + RANK_ICON[h.rank] + ' ' + esc(title) + '</text>';
    return '<svg class="clkg-house" viewBox="0 0 360 300" role="img" aria-label="Хата гончаря ' + esc(h.nick) + '">' + s + '</svg>';
  }

  /// Під справжньою хатою (api.houseSvg живої хати) — лише полиці: найкращі вироби, дарунки й табличка з рангом.
  function friendShelvesSvg(st, api, h) {
    const esc = (x) => api.esc(st, x);
    const ware = (w, style, q, x, y, s, slot) => '<g transform="translate(' + x + ' ' + y + ') scale(' + s + ')">'
      + api.wareSvg(w, { style, quality: q, slot, wrap: false }) + '</g>';
    let s = '<rect width="360" height="112" rx="10" fill="#2a1d14"/>';
    s += '<path d="M16 70h150" stroke="#6b4423" stroke-width="5"/>';
    (h.best || []).forEach((b, i) => { s += ware(b.ware, b.style, b.q, 20 + i * 48, 70 - 86 * 0.5, 0.5, 'fsb-' + i); });
    s += '<text x="90" y="86" font-size="9" fill="#b8a38a" text-anchor="middle">' + ((h.best || []).length ? 'найкраще в коморі' : 'комора порожня') + '</text>';
    s += '<path d="M190 70h156" stroke="#6b4423" stroke-width="5"/>';
    (h.gifts || []).slice(0, 6).forEach((g, i) => { s += ware(g.ware, g.style, g.q, 192 + i * 26, 70 - 86 * 0.3, 0.3, 'fsg-' + i); });
    s += '<text x="268" y="86" font-size="9" fill="#b8a38a" text-anchor="middle">полиця дарунків' + ((h.gifts || []).length ? ' · ' + h.gifts.length : '') + '</text>';
    const title = (h.rank >= 3 ? 'Цехмістр ' : '') + h.nick;
    s += '<rect x="96" y="92" width="168" height="18" rx="4" fill="#6b4423"/><text x="180" y="105" font-size="11" fill="#f4efe3" text-anchor="middle">'
      + RANK_ICON[h.rank] + ' ' + esc(title) + '</text>';
    return '<svg class="clkg-house clkg-shelves" viewBox="0 0 360 112" role="img" aria-label="Полиці гончаря ' + esc(h.nick) + '">' + s + '</svg>';
  }

  /// «📌 Виставка» друга (§C.4): до трьох клітинок альбому, які він сам поставив на видноту.
  function showHtml(st, api, d) {
    if (!d.show || !d.show.length) return '';
    return '<div class="clkg-show"><span class="muted small">📌 На видноті</span><div class="clkg-showrow">'
      + d.show.map((x, i) => '<span class="clkg-showcell q' + x.q + '" title="' + api.esc(st, QUALITY[x.q] + ' '
        + wareName(st, x.ware).toLowerCase() + ' · ' + styleName(st, x.style)) + '">'
        + api.wareSvg(x.ware, { style: x.style, quality: x.q, cls: 'clkg-mid', slot: 'fshow-' + i }) + '</span>').join('')
      + '</div></div>';
  }

  /// Стан горна друга: чи щось зараз пече і скільки партій він уже обпалив.
  function kilnHtml(st, api, d) {
    const k = d.kiln;
    if (!k) return '';
    const now = api.serverNow(st);
    const cool = Date.parse(k.coolUntil) || 0;
    const lit = Date.parse(k.litAt) || 0;
    let text;
    if (cool > now) text = '❄️ горно холоне після обпалу';
    else if (lit && k.batch) text = '🔥 у горні горить: ' + k.batch + ' ' + api.plural(k.batch, 'виріб', 'вироби', 'виробів');
    else if (k.batch) text = '🏺 у горні складено ' + k.batch + ' ' + api.plural(k.batch, 'виріб', 'вироби', 'виробів')
      + (k.style ? ' · ' + styleName(st, k.style).toLowerCase() + ', краса ' + k.beauty : '');
    else text = '🧱 горно холодне й порожнє';
    return '<div class="muted small clkg-kiln">' + api.esc(st, text) + (k.batches ? ' · партій за весь час: ' + api.count(k.batches) : '') + '</div>';
  }

  /// Стіна звань у хаті друга (clicker-titles.md): що він має, найрідкісніші спершу, і пам'ятний глечик «Округа».
  function wallHtml(st, api, d) {
    const list = d.titles || [];
    if (!list.length && !d.keepsake) return '';
    const esc = (x) => api.esc(st, x);
    return '<div class="clkg-wall"><span class="muted small">🎖 Стіна звань</span><div class="clkg-wallrow">'
      + list.map((t) => '<span class="clkg-wallt k-' + esc(t.kind) + '" title="' + esc(t.desc) + '"><b>' + t.icon + '</b>' + esc(t.name) + '</span>').join('')
      + (d.keepsake ? '<span class="clkg-wallt k-gift" title="Подарунок округи за оновлення зі званнями"><b>🏺</b>Пам\'ятний глечик «Округа»</span>' : '')
      + '</div></div>';
  }

  /// Допомога просто в хаті друга — одна кнопка на його картку (там усе: гостинець, підмайстер, похвала, дарунок).
  function helpButtons(st, d) {
    if (!st.mine || !st.guild || !st.guild.enabled) return '';
    return '<div class="clkg-btns clkg-hhelp"><button type="button" class="primary" data-hcard="1">🤝 Що можу зробити для '
      + HClicker.api.esc(st, d.nick) + '</button></div>';
  }

  function openHouse(st, api, nick) {
    const body = api.overlay(st, '<div class="clk-sub">🏠 Хата: ' + api.esc(st, nick) + '</div><div class="muted small">Відчиняємо двері…</div>', { cls: 'clkg-ov' });
    fetch('/api/games/clicker/house?nick=' + encodeURIComponent(nick), { headers: headers(st) })
      .then((r) => r.json().catch(() => null).then((d) => ({ ok: r.ok, d })))
      .then(({ ok, d }) => {
        if (!body.isConnected) return;
        if (!ok || !d) { body.innerHTML = '<div class="clk-sub">🏠 ' + api.esc(st, nick) + '</div><div class="muted">' + api.esc(st, (d && d.message) || 'Хата зачинена') + '</div>'; return; }
        const esc = (x) => api.esc(st, x);
        const stat = (label, val) => '<span class="clkg-stat"><b>' + val + '</b><i>' + label + '</i></span>';
        const mine = d.nick.toLowerCase() === myNick(st).toLowerCase();
        const album = d.album != null && d.albumSize
          ? stat('альбом', Math.round((d.album / d.albumSize) * 100) + ' %' + (d.stars ? ' <span class="clkg-stars">★' + d.stars + '</span>' : ''))
          : '';
        body.innerHTML = '<div class="clk-sub">🏠 ' + esc(d.houseName || 'Хата') + ': ' + esc(d.nick)
          + ' <span class="muted small">· ' + RANK_ICON[d.rank] + ' ' + esc(rankName(st, d.rank)) + '</span></div>'
          + (api.houseSvg ? '<div class="clkg-fhouse">' + api.houseSvg(st, d) + '</div>' + friendShelvesSvg(st, api, d) : friendHouseSvg(st, api, d))
          + showHtml(st, api, d)
          + kilnHtml(st, api, d)
          + '<div class="clkg-stats">'
          + stat('глеків за весь час', api.potsShort(d.total))
          + stat('виліплено', api.count(d.formed))
          + stat('обпалено', api.count(d.fired))
          + stat('розписів', d.styles.length)
          + album
          + (d.tiles != null ? stat('кахлів у печі', d.tiles) : '')
          + (d.wonders != null ? stat('дивовиж знайдено', d.wonders) : '')
          + stat('клейм', api.count(d.stamps || 0))
          + '</div>'
          + (d.gifts.length ? '<div class="muted small">Дарунки від: ' + esc([...new Set(d.gifts.map((g) => g.from))].join(', ')) + '</div>' : '')
          + wallHtml(st, api, d)
          + (mine ? '' : helpButtons(st, d))
          + (mine ? '' : '<div class="muted small">Хата — зі збереження; живе коло друга може бути трохи новішим.</div>');
        for (const b of body.querySelectorAll('[data-hcard]')) b.onclick = (e) => { if (human(e)) openCard(st, api, d.nick); };
        // Толока друга (v11, clicker-toloka.js): його будова й «🤝 Піднести на толоку».
        if (!mine && typeof api.tolokaHouse === 'function') api.tolokaHouse(st, body, d);
      })
      .catch((e) => { console.error('[clicker:guild] хата друга', e); if (body.isConnected) body.innerHTML = '<div class="muted">Не вийшло зазирнути — спробуй ще</div>'; });
  }

  // ---------- вибір виробу (віз, дарунок, похвала) ----------

  function itemRow(st, api, it, i, buttons) {
    const esc = (x) => api.esc(st, x);
    return '<div class="clkw-item q' + it.q + '">'
      + api.wareSvg(it.ware, { style: it.style, quality: it.q, cls: 'clkw-mid', slot: 'gp-' + i })
      + '<div class="clkw-itxt"><b>' + esc(wareName(st, it.ware)) + ' <span class="clkw-n">×' + it.n + '</span></b>'
      + '<span class="muted small">' + esc(styleName(st, it.style)) + ' · <span class="clkw-q">' + STARS[it.q] + ' ' + QUALITY[it.q] + '</span></span></div>'
      + '<div class="clkw-btns">' + buttons + '</div></div>';
  }

  function openPicker(st, api, mode) {
    st.guildOv = mode;
    renderPicker(st, api);
  }

  function renderPicker(st, api) {
    const mode = st.guildOv;
    const v = st.guild;
    if (!mode || !v) return;
    const esc = (x) => api.esc(st, x);
    const items = itemsOf(st);
    let head;
    let rows;
    if (mode === 'give') {
      const w = v.day;
      const a = v.all || { n: 0, keep: 0 };
      head = '<div class="clk-sub">🧺 Покласти на віз</div><p class="muted small clk-note">Потрібні: '
        + w.subs.map((s) => esc(wareName(st, s.ware)) + ' ' + Math.min(s.have, s.need) + '/' + s.need).join(' · ')
        + '. Будь-який розпис і якість рахуються; на воза краще класти простіші — дзвінкі згодяться для майстерштука.</p>'
        + (st.mine && (a.n > 0 || a.keep > 0)
          ? '<div class="clkg-btns clkg-allrow">' + giveAllBtn(st, api, v, 'primary')
            + (a.keep > 0 ? '<button type="button" class="ghost clkg-giveall" data-keep="0" title="Перед обпалом: комора однаково згорить, '
              + 'а на возі вироби ще щось дадуть">і відкладені теж · ' + api.count(a.n + a.keep) + '</button>'
              + '<span class="muted small">' + api.count(a.keep) + ' ' + api.plural(a.keep, 'виріб', 'вироби', 'виробів')
              + ' відкладено під замовлення гостей і сіл та майстерштук</span>' : '')
            + '</div>'
          : '');
      rows = items.map((it, i) => itemRow(st, api, it, i,
        '<button type="button" class="ghost small" data-give="' + esc(it.key) + '" data-n="1">+1</button>'
        + (it.n >= 5 ? '<button type="button" class="ghost small" data-give="' + esc(it.key) + '" data-n="5">+5</button>' : '')
        + (it.n > 1 ? '<button type="button" class="ghost small" data-give="' + esc(it.key) + '" data-n="' + it.n + '">усі ' + it.n + '</button>' : ''))).join('');
    } else if (mode === 'brag') {
      const wait = (Date.parse(v.bragAt) || 0) - api.serverNow(st);
      head = '<div class="clk-sub">🏺 Похвалитись у Журнал</div><p class="muted small clk-note">Усе село прочитає, чим ти пишаєшся. '
        + 'Раз на 15 хвилин; виріб лишається в тебе.' + (wait > 0 ? ' Наступна похвала за ' + api.mmss(wait) + '.' : '') + '</p>';
      const best = items.slice().sort((a, b) => b.q - a.q || (b.style ? 1 : 0) - (a.style ? 1 : 0));
      rows = best.map((it, i) => itemRow(st, api, it, i, '<button type="button" class="ghost small" data-brag="' + esc(it.key) + '"' + (wait > 0 ? ' disabled' : '') + '>Похвалитись</button>')).join('');
    } else return;
    const html = head + (items.length ? '<div class="clkw-items clkg-pick">' + rows + '</div>' : '<div class="clk-teaser muted small">Комора порожня — спершу обпали щось у горні.</div>');
    let body;
    if (api.overlayOpen(st) && st.guildOvBody && st.guildOvBody.isConnected) {
      body = st.guildOvBody;
      if (body._sig === html) return;
      body._sig = html;
      body.innerHTML = html;
    } else {
      body = api.overlay(st, html, { cls: 'clkg-ov', onClose: () => { st.guildOv = null; st.guildOvBody = null; } });
      body._sig = html;
      st.guildOvBody = body;
    }
    for (const b of body.querySelectorAll('[data-give]')) {
      b.onclick = (e) => {
        if (!human(e)) return;
        act(st, api, { op: 'give', key: b.dataset.give, n: +b.dataset.n }, 'wagon').then((r) => { if (r && r.ok) st.guildRoll = Date.now(); });
      };
    }
    for (const b of body.querySelectorAll('.clkg-giveall')) b.onclick = (e) => giveAll(st, api, e, b);
    for (const b of body.querySelectorAll('[data-brag]')) {
      b.onclick = (e) => { if (human(e)) act(st, api, { op: 'brag', key: b.dataset.brag }, 'brag').then((r) => { if (r && r.ok) api.closeOverlay(st); }); };
    }
  }

  /// «🛒 Усе на віз» — одна дія на сервері (guild { op: giveAll }), а не N натисків «+1». data-keep="0" — і відкладене теж.
  function giveAll(st, api, e, b) {
    if (!human(e) || b.disabled) return;
    b.disabled = true;
    const payload = b.dataset.keep === '0' ? { op: 'giveAll', keep: false } : { op: 'giveAll' };
    act(st, api, payload, 'wagon').then((r) => {
      if (r && r.ok) { st.guildRoll = Date.now(); if (st.guildOv === 'give') api.closeOverlay(st); } else b.disabled = false;
    });
  }

  /// Перед обпалом (вкладка «Клейма»): комора згорить — нагадати й дати «🛒 Усе на віз» просто там
  /// (записка Smaug: «кнопку покласти всі вироби на віз — зручно перед клеймом»).
  function paintFireWagon(st, api) {
    const pane = st.panes && st.panes.fire;
    if (!pane) return;
    let slot = st.guildFireSlot;
    if (!slot || !slot.isConnected || slot.parentElement !== pane) {
      slot = document.createElement('div');
      slot.className = 'clkg-firewagon';
      const box = pane.querySelector('.clk-firebox');
      if (box) box.after(slot); else pane.prepend(slot);
      st.guildFireSlot = slot;
    }
    const v = st.guild;
    const a = v && v.enabled && v.all;
    const html = a && a.n + a.keep > 0 && st.mine
      ? '<span class="small">🛒 У коморі ' + api.count(a.n + a.keep) + ' ' + api.plural(a.n + a.keep, 'виріб', 'вироби', 'виробів')
        + ' — обпал спалить комору разом із глеками. На возі цеху вони ще дадуть нагороду.</span>'
        + '<div class="clkg-btns">' + giveAllBtn(st, api, v, 'ghost')
        + (a.keep > 0 ? '<button type="button" class="ghost clkg-giveall" data-keep="0">і відкладені теж · ' + api.count(a.n + a.keep) + '</button>' : '')
        + '</div>'
      : '';
    if (!api.swap(slot, html)) return;
    slot.hidden = !html;
    for (const b of slot.querySelectorAll('.clkg-giveall')) b.onclick = (e) => giveAll(st, api, e, b);
  }

  // ---------- вкладка ----------

  const info = (text) => HClicker.api.info(text);

  function paint(st, api) {
    const v = st.guild;
    if (!st.guildBody || !v) return;
    if (!v.enabled) {
      api.swap(st.guildBody, '<div class="clk-teaser muted">🔒 Цех зараз зачинений. Твій ранг — ' + api.esc(st, rankName(st, v.rank || 0)) + '.</div>');
      return;
    }
    const html = buffsHtml(st, api, v) + gotHtml(st, api, v) + waitHtml(st, api) + wagonHtml(st, api, v) + helpHtml(st, api, v)
      + giftsHtml(st, api, v) + rankHtml(st, api, v) + rosterHtml(st, api);
    if (!api.swap(st.guildBody, html)) return;
    const q = (sel) => st.guildBody.querySelector(sel);
    const give = q('.clkg-give');
    if (give) give.onclick = () => openPicker(st, api, 'give');
    for (const b of st.guildBody.querySelectorAll('.clkg-giveall')) b.onclick = (e) => giveAll(st, api, e, b);
    for (const b of st.guildBody.querySelectorAll('[data-claim]')) {
      b.onclick = (e) => {
        if (!human(e)) return;
        b.disabled = true;
        act(st, api, { op: 'claim', day: b.dataset.claim }, 'wagon').then((r) => {
          if (r && r.ok) api.sparks(st, null, 16, true, 50, 40);
          else b.disabled = false;
        });
      };
    }
    const gifting = q('.clkg-gifting');
    if (gifting) gifting.onclick = () => openChooser(st, api, 'gift');
    const bragging = q('.clkg-bragging');
    if (bragging) bragging.onclick = () => openPicker(st, api, 'brag');
    for (const b of st.guildBody.querySelectorAll('[data-card]')) b.onclick = () => openCard(st, api, b.dataset.card);
    const choose = q('.clkg-choose');
    if (choose) choose.onclick = () => openChooser(st, api, '');
    for (const b of st.guildBody.querySelectorAll('[data-thank]')) b.onclick = (e) => { if (human(e)) thank(st, api, b.dataset.thank, b); };
    const feed = q('.clkg-feed');
    if (feed) feed.ontoggle = () => api.storeSet('clk.guild.feedOpen', feed.open ? '1' : '0');
    // Кнопка майстерштука тепер буває і в рядку рангу, і всередині «що потрібно» — вішаємо на обидві.
    for (const master of st.guildBody.querySelectorAll('.clkg-master')) {
      master.onclick = (e) => { if (human(e)) act(st, api, { op: 'masterpiece' }, null); };
    }
    const auto = q('.clkg-autobox');
    if (auto) auto.onchange = () => act(st, api, { op: 'auto', on: auto.checked }, null);
    for (const b of st.guildBody.querySelectorAll('[data-house]')) b.onclick = () => openHouse(st, api, b.dataset.house);
    st.guildCds = [...st.guildBody.querySelectorAll('.clkg-cd, .clkg-bragcd, .clkg-bufcd')];
    // Щойно поклали — колеса віза крутнуться.
    if (Date.now() - (st.guildRoll || 0) < 4000) {
      const svg = q('.clkg-wagon');
      if (svg) svg.classList.add('roll');
    }
    countdowns(st, api);
  }

  function countdowns(st, api) {
    const now = api.serverNow(st);
    for (const el of st.guildCds || []) {
      const at = +el.dataset.at;
      let t;
      if (el.classList.contains('clkg-bragcd')) t = at > now ? 'похвала за ' + api.mmss(at - now) : '';
      else if (el.classList.contains('clkg-bufcd')) t = at > now ? 'ще ' + left(api, at - now) : '';
      else t = left(api, at - now);
      if (el.textContent !== t) el.textContent = t;
    }
  }

  /// Нові дарунки на полиці й новий ранг — тост, звук, іскри (раз: пам'ятаємо в localStorage).
  function celebrate(st, api, v) {
    const seen = +api.storeGet('clk.guild.giftAt', '0') || 0;
    const fresh = v.shelf.filter((g) => (Date.parse(g.at) || 0) > seen);
    if (fresh.length) {
      api.storeSet('clk.guild.giftAt', String(Math.max(...fresh.map((g) => Date.parse(g.at) || 0))));
      // Перший вид після встановлення — не тостимо всю полицю, лише запам'ятовуємо. Сервер, що вже знає «got»,
      // скаже про дарунок нижче разом з усією іншою допомогою — двічі не тостимо.
      if (seen > 0 && !v.gotKnown) {
        const g = fresh[0];
        api.toast(st, '🎁 Дарунок від ' + g.from + ': ' + QUALITY[g.q] + ' ' + wareName(st, g.ware).toLowerCase()
          + (fresh.length > 1 ? ' і ще ' + (fresh.length - 1) : ''), 'ok');
        api.sfx('gift');
      }
    }
    if (st.guildRank != null && v.rank > st.guildRank) {
      api.sfx('rank-up');
      api.sparks(st, null, 24, true, 50, 45);
      api.popAt(st, RANK_ICON[v.rank] + ' ' + rankName(st, v.rank) + '!', 'big', 50, 30);
    }
    st.guildRank = v.rank;

    // Нова допомога від друга — стрічка, тост і звук. «Нова» — та, про яку ще не казали: мить пам'ятається в
    // localStorage, як і для дарунків, тож F5 нічого не повторює, а баф, що прилетів за секунду до входу, не губиться.
    const news = (key, at, text) => {
      const was = +api.storeGet(key, '0') || 0;
      if (!(at > was)) return;
      api.storeSet(key, String(at));
      if (was === 0) return;                 // перший вид після встановлення — лише запам'ятовуємо
      api.toast(st, text, 'ok');
      api.feed(st, text);
      api.sfx('gift');
    };
    if (v.gotKnown) {
      // Усе, що прийшло від друзів (12-те дошліфування): гостинець, підмайстер, похвала, дарунок, толока, «дякую».
      const was = +api.storeGet('clk.guild.gotAt', '0') || 0;
      const fresh2 = v.got.filter((g) => ms(g.at) > was);
      if (fresh2.length) {
        api.storeSet('clk.guild.gotAt', String(Math.max(...fresh2.map((g) => ms(g.at)))));
        if (was > 0) {
          for (const g of fresh2.slice(0, 3).reverse()) {
            const text = GOT_TEXT[g.kind] ? GOT_TEXT[g.kind](g) : '';
            if (!text) continue;
            api.toast(st, text + (g.kind !== 'thanks' && g.from !== 'друг' ? ' — подякувати можна в «Селі»' : ''), 'ok');
            api.feed(st, text);
          }
          api.sfx('gift');
        }
      }
      return;
    }
    for (const kind of ['lend', 'cheer']) {
      const b = v.buffs[kind];
      if (!b) continue;
      news('clk.guild.' + kind + 'At', Date.parse(b.until) || 0, HELP[kind].icon + ' ' + (b.from || 'Друг') + ' помагає: '
        + (kind === 'lend' ? 'підмайстер у гостях, ліплення вдвічі швидше' : '+10 % до всього на годину'));
    }
    // Гостинець — подія, а не баф: він не триває, але сказати про нього треба так само.
    const t = v.buffs.treat;
    if (t) news('clk.guild.treatAt', Date.parse(t.at) || 0, '🎁 ' + (t.from || 'Друг') + ' прислав гостинець: +' + api.potsShort(t.pots));
  }

  const GOT_TEXT = {
    treat: (g) => '🎁 ' + g.from + ' прислав(ла) гостинець' + (g.what ? ': ' + g.what : ''),
    lend: (g) => '🧑‍🎓 ' + g.from + ' прислав(ла) підмайстра — ліплення вдвічі швидше',
    cheer: (g) => '👏 ' + g.from + ' хвалить твою роботу: +10 % до всього на годину',
    gift: (g) => '🎁 Дарунок від ' + g.from + (g.what ? ': ' + g.what : ''),
    toloka: (g) => '🤝 ' + g.from + ' підніс(ла) на твою толоку' + (g.what ? ': ' + g.what : ''),
    thanks: (g) => '💛 ' + g.from + ' дякує тобі за допомогу',
  };

  /// ✨ на «Селі»: є допомога, якої ще не бачив у вкладці (будь-який вид). Відкрив «Село» — побачив.
  function gotNote(st, api) {
    const v = st.guild;
    const newest = v && v.got.length ? Math.max(...v.got.map((g) => ms(g.at))) : 0;
    if (st.tab === 'guild' && newest) api.storeSet('clk.guild.gotSeen', String(newest));
    const seen = +api.storeGet('clk.guild.gotSeen', '0') || 0;
    // Пріоритет 1, як у готових гостей і віза: свіжа допомога — подія, що гасне, щойно «Село» відкрили, а гості
    // нікуди не дінуться; з пріоритетом 2 гравець із гостями ✨ так і не бачив (QA clk12).
    api.tabNote(st, 'guild', 'buff', newest > seen || (!v.gotKnown && (v.buffs.lend || v.buffs.cheer)) ? '✨' : '', 1);
  }

  HClicker.part({
    id: 'guild',
    order: 60,

    mount(st, api) {
      st.guildPane = api.tab(st, 'guild', '🤝 Село', 40);
      // Село відкривається, коли з ним уже є про що говорити: двадцять обпалених виробів або цех, що вже щось дав.
      api.showWhen(st, 'guild', (st2, v) => {
        const g = v.guild;
        // Сервер сам каже, чи «Село» відчинене (з першого обпалу); старий сервер — як було.
        if (g && typeof g.open === 'boolean') return g.open;
        if (g && (g.rank > 0 || (g.claims && g.claims.length) || (g.shelf && g.shelf.length) || g.given > 0)) return true;
        const m = v.market;
        if (m && m.delivered > 0) return true;
        return !!st2.craft && st2.craft.fired >= 20;
      });
      st.guildBody = document.createElement('div');
      st.guildBody.className = 'clkg';
      st.guildPane.appendChild(st.guildBody);
      // Шана сіл живе тут (її малює ярмарок, clicker-fair.js): село — це про людей навколо.
      const rep = document.createElement('div');
      rep.dataset.slot = 'rep';
      st.guildPane.appendChild(rep);
      st.guild = null;
      st.guildRank = null;
      st.guildRoster = null;
      st.guildRosterAt = 0;
      st.guildCds = [];
      st.guildOv = null;
      st.guildCard = null;
      st.guildChoose = null;
      // Дзвоник цеху (core.js → hgames:clkMail): друг щось надіслав — питаємо пошту дією, а не чекаємо свого кліку.
      // Дзвоники ближче 1,5 с один до одного зливаються в один хвостовий запит (не губляться: другий друг, що
      // надіслав за секунду після першого, інакше чекав би до наступної дії гончаря).
      st.guildMailAt = 0;
      st.guildMailT = 0;
      st.guildMail = () => {
        if (!st.guildPane || !st.mine || st.guildMailT) return;
        const wait = st.guildMailAt + 1500 - Date.now();
        const go = () => {
          st.guildMailT = 0;
          if (!st.guildPane || !st.mine) return;
          st.guildMailAt = Date.now();
          api.act(st, 'guild', { op: 'mail' });
        };
        if (wait > 0) st.guildMailT = setTimeout(go, wait);
        else go();
      };
      document.addEventListener('hgames:clkMail', st.guildMail);
    },

    update(st, v, api) {
      const g = v.guild;
      if (!g) return;
      st.guild = {
        enabled: !!g.enabled, rank: g.rank || 0, day: g.day || null, prev: g.prev || null, claims: g.claims || [],
        gifts: g.gifts || { left: 0, sent: 0, got: 0 }, shelf: g.shelf || [], given: g.given || 0, next: g.next || null,
        autoKiln: !!g.autoKiln, autoOff: !!g.autoOff, kilnSlots: g.kilnSlots || 0, bragAt: g.bragAt || null,
        wagonMult: g.wagonMult || 1,
        all: g.all || { n: 0, keep: 0 },
        help: g.help || { treatLeft: 0, lendLeft: false, cheered: [], sizes: [], treats: 0 },
        buffs: g.buffs || { lend: null, cheer: null, treat: null },
        got: g.got || [], gotKnown: Array.isArray(g.got), thanked: g.thanked || [],
      };
      if (st.guild.enabled && !st.guild.day) st.guild.enabled = false;
      if (st.guild.enabled) celebrate(st, api, st.guild);
      api.tabNote(st, 'guild', 'claim', st.guild.claims.length ? '🛒' : '', 1);
      gotNote(st, api);
      paint(st, api);
      paintFireWagon(st, api);
      if (st.guildOv) renderPicker(st, api);
      if (st.guildCard) renderCard(st, api);
    },

    slow(st, api) {
      if (st.tab !== 'guild' || !st.guild || !st.guild.enabled) return;
      gotNote(st, api);
      loadRoster(st, api, false);
      countdowns(st, api);
    },

    unmount(st) {
      st.guildPane = null;
      st.guildBody = null;
      st.guildFireSlot = null;
      st.guildOv = null;
      st.guildOvBody = null;
      st.guildCard = null;
      st.guildCardBody = null;
      st.guildChoose = null;
      st.guildChooseBody = null;
      if (st.guildMail) document.removeEventListener('hgames:clkMail', st.guildMail);
      clearTimeout(st.guildMailT);
      st.guildMailT = 0;
      st.guildMail = null;
    },
  });
})();
