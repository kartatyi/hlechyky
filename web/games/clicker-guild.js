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
  const pai = (n) => (Math.round(n * 100) / 100).toLocaleString('uk-UA', { maximumFractionDigits: 2 });

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

  /// Віз: віл у ярмі, дерев'яна платформа з бортами, два колеса, купа виробів (до 28), прапорець рівня. viewBox 360×200.
  function wagonSvg(st, api, w) {
    const goal = Math.max(1, w.goal);
    const fill = Math.min(1, w.total / (goal * 2));
    const count = Math.round(fill * 28);
    // Що лежить на возі: підцілі у своїх пропорціях, решта — горщики.
    const kinds = [];
    for (const s of w.subs) for (let i = 0; i < Math.min(s.have, s.need); i++) kinds.push(s.ware);
    const rest = Math.max(0, w.total - kinds.length);
    for (let i = 0; i < rest; i++) kinds.splice(Math.floor((i * (kinds.length + 1)) / (rest + 1)) + i % 2, 0, 'pot');
    while (kinds.length < Math.max(count, 1)) kinds.push('pot');
    const step = kinds.length / Math.max(1, count);
    const K = 0.5;
    let pile = '';
    for (let i = 0; i < count; i++) {
      const ware = kinds[Math.floor(i * step)] || 'pot';
      const row = Math.floor(i / 7);
      const col = i % 7;
      const x = 124 + col * 34 + (row % 2) * 17 - 50 * K;
      // Низ виробу — трохи за переднім бортом (y 104): і миску видно, і лежить «у возі».
      const y = 106 - row * 20 - 86 * K;
      // Анімація «впав на віз» — лише для щойно покладених (внутрішня <g>: CSS-transform перебив би атрибут зовнішньої).
      const fresh = st.guildPile != null && i >= st.guildPile;
      pile += '<g transform="translate(' + x.toFixed(1) + ' ' + y.toFixed(1) + ') scale(' + K + ')"><g' + (fresh ? ' class="clkg-ware" style="--d:' + ((i - st.guildPile) * 60) + 'ms"' : '') + '>'
        + api.wareSvg(ware, { quality: 1, slot: 'wg-' + i, wrap: false }) + '</g></g>';
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
      + '<div class="clkg-line"><b>' + w.total + '</b> з ' + w.goal + ' <span class="muted small">· '
      + (w.tier ? TIER_ICON[w.tier] + ' ' + TIER[w.tier] : 'ще до бронзи') + '</span></div>'
      + barHtml(w)
      + '<div class="clkg-subs">' + subs + '</div>'
      + givers
      + paiNote
      + '<div class="clkg-btns"><button type="button" class="ghost clkg-give"' + (st.mine && itemsOf(st).length ? '' : ' disabled') + '>🧺 Покласти на віз</button>'
      + claims + '</div>'
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
    const can = (kind) => st.mine && (kind !== 'lend' || h.lendLeft);
    const btn = (kind, note) => '<button type="button" class="ghost clkg-help" data-help="' + kind + '"' + (can(kind) ? '' : ' disabled') + '>'
      + HELP[kind].icon + ' ' + HELP[kind].name + (note ? ' <span class="muted small">· ' + note + '</span>' : '') + '</button>';
    return '<section class="clkg-card">'
      + '<div class="clk-sub">🤝 Помогти другові'
      + info('Село тримається на тому, що сильніший підставляє плече. Гостинець: платиш своїми хвилинами пасиву, '
        + 'а друг дістає вдвічі більше хвилин СВОГО — тож твоя гора його гру не зламає, але день-два росту дасть '
        + '(не більше ' + cap + ' хв на день на одного). Підмайстер — раз на день, і в друга добу ліплять удвічі швидше. '
        + 'Похвала — раз на день на друга, і в нього годину все йде на 10 % краще.') + '</div>'
      + '<div class="clkg-btns">'
      + btn('treat', 'подаровано ' + (h.treats || 0))
      + btn('lend', h.lendLeft ? 'один на день' : 'сьогодні вже пішов')
      + btn('cheer', 'раз на день на друга')
      + '</div>'
      + '<div class="muted small">Тобі сьогодні ще можуть принести ' + h.treatLeft + ' хв гостинців із ' + cap + '.</div>'
      + '</section>';
  }

  /// Вікно допомоги: обрати друга, а для гостинця — ще й розмір (і одразу видно, скільки це твоїх глеків).
  function renderHelp(st, api) {
    const kind = st.guildHelp;
    const v = st.guild;
    if (!kind || !v) return;
    const esc = (x) => api.esc(st, x);
    const roster = (st.guildRoster && st.guildRoster.potters) || [];
    const friends = roster.filter((p) => !p.me);
    if (!st.guildHelpTo || !friends.some((f) => f.nick === st.guildHelpTo)) st.guildHelpTo = friends.length ? friends[0].nick : '';
    const cheered = (v.help.cheered || []).map((x) => String(x).toLowerCase());
    const busy = kind === 'cheer' && cheered.includes(st.guildHelpTo.trim().toLowerCase());
    let html = '<div class="clk-sub">' + HELP[kind].icon + ' ' + HELP[kind].name + '</div>'
      + '<p class="muted small clk-note">' + HELP[kind].what + '.</p>';
    html += friends.length
      ? '<div class="clkg-friends">' + friends.map((f) => '<button type="button" class="ghost small' + (f.nick === st.guildHelpTo ? ' active' : '')
        + '" data-hto="' + esc(f.nick) + '">' + RANK_ICON[f.rank] + ' ' + esc(f.nick) + '</button>').join('') + '</div>'
      : '<div class="muted">У цеху поки нікого, крім тебе, — помагати нікому.</div>';
    if (!friends.length) html += '';
    else if (kind === 'treat') {
      const sizes = (v.help.sizes || []).length ? v.help.sizes : [{ minutes: 10 }, { minutes: 30 }, { minutes: 60 }];
      html += '<div class="clkg-sizes">' + sizes.map((s) => '<button type="button" class="ghost clkg-size" data-min="' + s.minutes + '">'
        + '<b>' + s.minutes + ' хв</b><i>' + (s.pots != null ? '−' + api.potsShort(s.pots) : '') + '</i>'
        + '<u>другові +' + s.minutes * 2 + ' хв його пасиву</u></button>').join('') + '</div>';
    } else {
      html += '<div class="clkg-btns"><button type="button" class="primary clkg-send"' + (busy ? ' disabled' : '') + '>'
        + HELP[kind].icon + ' ' + (kind === 'lend' ? 'Відпустити підмайстра' : 'Сказати добре слово') + '</button>'
        + (busy ? '<span class="muted small">сьогодні цього друга вже хвалив(ла)</span>' : '') + '</div>';
    }
    let body;
    if (api.overlayOpen(st) && st.guildHelpBody && st.guildHelpBody.isConnected) {
      body = st.guildHelpBody;
      if (body._sig === html) return;
      body._sig = html;
      body.innerHTML = html;
    } else {
      body = api.overlay(st, html, { cls: 'clkg-ov', onClose: () => { st.guildHelp = null; st.guildHelpBody = null; } });
      body._sig = html;
      st.guildHelpBody = body;
    }
    for (const b of body.querySelectorAll('[data-hto]')) b.onclick = () => { st.guildHelpTo = b.dataset.hto; renderHelp(st, api); };
    const send = (payload, sound) => act(st, api, payload, sound).then((r) => { if (r && r.ok) api.closeOverlay(st); });
    for (const b of body.querySelectorAll('.clkg-size')) {
      b.onclick = (e) => { if (human(e) && st.guildHelpTo) send({ op: 'treat', to: st.guildHelpTo, minutes: +b.dataset.min }, 'gift'); };
    }
    const one = body.querySelector('.clkg-send');
    if (one) one.onclick = (e) => { if (human(e) && st.guildHelpTo) send({ op: kind, to: st.guildHelpTo }, kind === 'lend' ? 'wagon' : 'brag'); };
  }

  function openHelp(st, api, kind) {
    st.guildHelp = kind;
    loadRoster(st, api, true);
    renderHelp(st, api);
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
      + '<b>' + api.short(Math.min(have, need)) + '/' + api.short(need) + '</b></div>';
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
        if (st.guildOv === 'gift') renderPicker(st, api);
        if (st.guildHelp) renderHelp(st, api);
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
    return '<div class="muted small clkg-kiln">' + api.esc(st, text) + (k.batches ? ' · партій за весь час: ' + api.short(k.batches) : '') + '</div>';
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

  /// Три кнопки допомоги просто в хаті друга — саме там, де хочеться щось для нього зробити.
  function helpButtons(st, d) {
    if (!st.mine || !st.guild || !st.guild.enabled) return '';
    return '<div class="clkg-btns clkg-hhelp">'
      + ['treat', 'lend', 'cheer'].map((k) => '<button type="button" class="ghost" data-hhelp="' + k + '">'
        + HELP[k].icon + ' ' + HELP[k].name + '</button>').join('')
      + '</div>';
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
          + stat('виліплено', api.short(d.formed))
          + stat('обпалено', api.short(d.fired))
          + stat('розписів', d.styles.length)
          + album
          + (d.tiles != null ? stat('кахлів у печі', d.tiles) : '')
          + (d.wonders != null ? stat('дивовиж знайдено', d.wonders) : '')
          + stat('клейм', d.stamps)
          + '</div>'
          + (d.gifts.length ? '<div class="muted small">Дарунки від: ' + esc([...new Set(d.gifts.map((g) => g.from))].join(', ')) + '</div>' : '')
          + wallHtml(st, api, d)
          + (mine ? '' : helpButtons(st, d))
          + (mine ? '' : '<div class="muted small">Хата — зі збереження; живе коло друга може бути трохи новішим.</div>');
        for (const b of body.querySelectorAll('[data-hhelp]')) {
          b.onclick = (e) => {
            if (!human(e)) return;
            st.guildHelpTo = d.nick;
            openHelp(st, api, b.dataset.hhelp);
          };
        }
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
      head = '<div class="clk-sub">🧺 Покласти на віз</div><p class="muted small clk-note">Потрібні: '
        + w.subs.map((s) => esc(wareName(st, s.ware)) + ' ' + Math.min(s.have, s.need) + '/' + s.need).join(' · ')
        + '. Будь-який розпис і якість рахуються; на воза краще класти простіші — дзвінкі згодяться для майстерштука.</p>';
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
    } else {
      const roster = (st.guildRoster && st.guildRoster.potters) || [];
      const friends = roster.filter((p) => !p.me);
      if (!st.guildGiftTo || !friends.some((f) => f.nick === st.guildGiftTo)) st.guildGiftTo = friends.length ? friends[0].nick : '';
      head = '<div class="clk-sub">🎁 Дарунок другові</div><p class="muted small clk-note">Виріб поїде на полицю дарунків у хаті друга з підписом від тебе. '
        + 'Сьогодні ще ' + v.gifts.left + '.</p>'
        + (friends.length
          ? '<div class="clkg-friends">' + friends.map((f) => '<button type="button" class="ghost small' + (f.nick === st.guildGiftTo ? ' active' : '') + '" data-to="' + esc(f.nick) + '">'
            + RANK_ICON[f.rank] + ' ' + esc(f.nick) + '</button>').join('') + '</div>'
          : '<div class="muted">У цеху поки нікого, крім тебе, — дарувати нікому.</div>');
      rows = friends.length
        ? items.map((it, i) => itemRow(st, api, it, i, '<button type="button" class="ghost small" data-gift="' + esc(it.key) + '"' + (v.gifts.left > 0 ? '' : ' disabled') + '>Подарувати</button>')).join('')
        : '';
    }
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
    for (const b of body.querySelectorAll('[data-brag]')) {
      b.onclick = (e) => { if (human(e)) act(st, api, { op: 'brag', key: b.dataset.brag }, 'brag').then((r) => { if (r && r.ok) api.closeOverlay(st); }); };
    }
    for (const b of body.querySelectorAll('[data-to]')) b.onclick = () => { st.guildGiftTo = b.dataset.to; renderPicker(st, api); };
    for (const b of body.querySelectorAll('[data-gift]')) {
      b.onclick = (e) => {
        if (!human(e) || !st.guildGiftTo) return;
        act(st, api, { op: 'gift', nick: st.guildGiftTo, key: b.dataset.gift }, 'gift');
      };
    }
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
    const html = buffsHtml(st, api, v) + wagonHtml(st, api, v) + helpHtml(st, api, v)
      + giftsHtml(st, api, v) + rankHtml(st, api, v) + rosterHtml(st, api);
    if (!api.swap(st.guildBody, html)) return;
    const q = (sel) => st.guildBody.querySelector(sel);
    const give = q('.clkg-give');
    if (give) give.onclick = () => openPicker(st, api, 'give');
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
    if (gifting) gifting.onclick = () => { loadRoster(st, api, true); openPicker(st, api, 'gift'); };
    const bragging = q('.clkg-bragging');
    if (bragging) bragging.onclick = () => openPicker(st, api, 'brag');
    for (const b of st.guildBody.querySelectorAll('[data-help]')) b.onclick = () => openHelp(st, api, b.dataset.help);
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
      // Перший вид після встановлення — не тостимо всю полицю, лише запам'ятовуємо.
      if (seen > 0) {
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

  HClicker.part({
    id: 'guild',
    order: 60,

    mount(st, api) {
      st.guildPane = api.tab(st, 'guild', '🤝 Село', 40);
      // Село відкривається, коли з ним уже є про що говорити: двадцять обпалених виробів або цех, що вже щось дав.
      api.showWhen(st, 'guild', (st2, v) => {
        const g = v.guild;
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
      st.guildHelp = null;
      st.guildHelpTo = '';
    },

    update(st, v, api) {
      const g = v.guild;
      if (!g) return;
      st.guild = {
        enabled: !!g.enabled, rank: g.rank || 0, day: g.day || null, prev: g.prev || null, claims: g.claims || [],
        gifts: g.gifts || { left: 0, sent: 0, got: 0 }, shelf: g.shelf || [], given: g.given || 0, next: g.next || null,
        autoKiln: !!g.autoKiln, autoOff: !!g.autoOff, kilnSlots: g.kilnSlots || 0, bragAt: g.bragAt || null,
        wagonMult: g.wagonMult || 1,
        help: g.help || { treatLeft: 0, lendLeft: false, cheered: [], sizes: [], treats: 0 },
        buffs: g.buffs || { lend: null, cheer: null, treat: null },
      };
      if (st.guild.enabled && !st.guild.day) st.guild.enabled = false;
      if (st.guild.enabled) celebrate(st, api, st.guild);
      api.tabNote(st, 'guild', 'claim', st.guild.claims.length ? '🛒' : '', 1);
      api.tabNote(st, 'guild', 'buff', st.guild.buffs.lend || st.guild.buffs.cheer ? '✨' : '', 2);
      paint(st, api);
      if (st.guildOv) renderPicker(st, api);
      if (st.guildHelp) renderHelp(st, api);
    },

    slow(st, api) {
      if (st.tab !== 'guild' || !st.guild || !st.guild.enabled) return;
      loadRoster(st, api, false);
      countdowns(st, api);
    },

    unmount(st) {
      st.guildPane = null;
      st.guildBody = null;
      st.guildOv = null;
      st.guildOvBody = null;
      st.guildHelp = null;
      st.guildHelpBody = null;
    },
  });
})();
