/*
  «📊 Хто скільки» → «🏺 Глек»: як живе Дядько Глек — діджей, суперник ваших смаків, ведучий, прожарювач і каса.
  Рахує сервер (GET /api/stats/glek?period=, LitopysGlek.cs), тут — лише малюємо. Голос вкладки — Глеків: про себе
  в першій особі, трохи ображено, трохи гордо.
*/
(() => {
  'use strict';
  const H = window.HPeople;
  if (!H || !H.statsTab) return;
  const k = H.kit;
  const { esc, num, cnt, plural, cap, ava, nickLink, durShort, same } = k;

  /// Стабільне зерно з рядка — фраза дня однакова в усіх (як цитата на сервері).
  const seed = (s) => { let h = 2166136261; for (const ch of String(s)) h = Math.imul(h ^ ch.charCodeAt(0), 16777619) >>> 0; return h; };
  const pick = (list, s) => list[seed(s) % list.length];
  const pct = (a, b) => (b ? Math.round(a * 100 / b) : 0);
  const per100 = (likes, plays) => (plays ? Math.round(likes * 1000 / plays) / 10 : 0);
  const fix1 = (v) => String(v).replace('.', ',');
  const signed = (v) => (v > 0 ? '+' : v < 0 ? '−' : '') + num(Math.abs(v));
  const shardsTxt = (v) => num(Math.abs(v)) + ' ' + plural(Math.abs(v), 'черепок', 'черепки', 'черепків');
  /// Години цілими: «4 год 7 хв» у плитку не влазить, а хвилини при сотнях годин нікому не цікаві.
  const hours = (sec) => (sec >= 3600 ? num(Math.round(sec / 3600)) + ' год' : durShort(sec));
  const times = (n) => cnt(n, 'раз', 'рази', 'разів');
  const tracks = (n) => cnt(n, 'трек', 'треки', 'треків');
  const mine = (nick) => { const me = k.me(); return !!(me && me.nick && same(nick, me.nick)); };
  const when = (iso) => new Date(iso).toLocaleString('uk-UA', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' });
  const empty = (text) => '<div class="gempty glek">' + text + '</div>';
  const box = (title, sub, html, cls) => '<section class="panel stbox' + (cls ? ' ' + cls : '') + '"><h3>' + title
    + (sub ? ' <span class="muted small">' + sub + '</span>' : '') + '</h3>' + html + '</section>';
  const tile = (icon, big, label) => '<div class="ov-tile"><span class="ov-ti" aria-hidden="true">' + icon + '</span><b>' + big + '</b><span class="ov-tl">' + label + '</span></div>';
  const width = (val, max) => Math.max(2, Math.round(val / (max || 1) * 100));
  const money = (v) => '<b class="' + (v > 0 ? 'gk-plus' : v < 0 ? 'gk-minus' : '') + '">' + signed(v) + '</b>';

  /// Рядок-смужка: людина — смужка — число; { glek: true } — сам Глек, своїм глиняним кольором і без посилання.
  function bar(x, i, max, val, shown, tip) {
    const glek = !!x.glek;
    return '<div class="stp' + (!glek && mine(x.nick) ? ' me' : '') + (glek ? ' gk-glek' : '') + '"' + (tip ? ' title="' + esc(tip) + '"' : '') + '>'
      + '<span class="n">' + (i == null ? '' : i + 1) + '</span>'
      + (glek ? '<span class="gk-pot" aria-hidden="true">🏺</span><span class="stp-nick">' + esc(x.nick) + '</span>' : ava(x.nick, 'ava sm') + nickLink(x.nick, 'stp-nick'))
      + '<div class="stp-bar"><i style="width:' + width(val, max) + '%;--h:' + (glek ? 26 : k.hue(x.nick)) + '"></i></div>'
      + '<b>' + shown + '</b></div>';
  }
  /// Рядок-смужка не людини (пісня, виконавець): підпис замість ніка.
  function thing(label, i, max, val, shown, tip, h) {
    return '<div class="stp gk-thing"' + (tip ? ' title="' + esc(tip) + '"' : '') + '><span class="n">' + (i + 1) + '</span>'
      + '<span class="stp-nick" title="' + esc(label) + '">' + esc(label) + '</span>'
      + '<div class="stp-bar"><i style="width:' + width(val, max) + '%;--h:' + (h == null ? 26 : h) + '"></i></div>'
      + '<b>' + shown + '</b></div>';
  }

  // ---------- шапка: Глек про себе ----------
  function quip(d, P) {
    const a = d.air, songs = a.glek + a.people;
    if (!songs && !d.talk.roasts) return 'Тиша. Я в коморі, протираю платівки. Заходьте — поставлю щось.';
    const list = [];
    const share = pct(a.glek, songs);
    if (share >= 70) list.push('Я тримав ' + share + '% ефіру. Хтось же мусить, поки ви граєте в Мафію.', share + '% музики ' + P + ' — моя. Решту теж міг би, але даю вам побути діджеями.');
    else if (songs) list.push('Ви замовили ' + pct(a.people, songs) + '% ефіру. Ще трохи — і я піду на пенсію. Жартую, не піду.');
    const c = d.critics[0];
    if (c && c.n >= 3) list.push(c.nick + ' відкинув мій вибір ' + times(c.n) + '. Я не злопам\'ятний. Я просто все записую.');
    const g = per100(a.glekLikes, a.glek), p = per100(a.peopleLikes, a.people);
    if (a.glek && a.people && g > p) list.push('Мої треки лайкають частіше за ваші. Я просто кажу. Цифри внизу.');
    if (a.glek && a.people && g < p) list.push('Ваші треки лайкають частіше за мої. Поки що. Я вчуся — внизу видно, на чиїх піснях.');
    if (d.kasa.total < 0) list.push('Каса в мінусі на ' + shardsTxt(d.kasa.total) + '. Роздаю черепки, як дід цукерки на Миколая.');
    if (d.kasa.total > 0) list.push('Каса в плюсі на ' + shardsTxt(d.kasa.total) + '. Лавка сама себе не продасть — а таки продала.');
    if (d.talk.roasts) list.push('Написав ' + cnt(d.talk.roasts, 'прожарку', 'прожарки', 'прожарок') + ' ' + P + '. Сковорідка ще тепла.');
    return pick(list, d.day + d.period);
  }

  function hero(d, P) {
    const a = d.air, songs = a.glek + a.people;
    return '<section class="panel stbox gk-hero"><div class="gk-head"><span class="gk-face" aria-hidden="true">🏺</span><div class="gk-hwrap"><h3>'
      + esc(d.dj) + ' про себе <span class="muted small">' + P + '</span></h3>'
      + '<p class="gk-quip">«' + esc(quip(d, P)) + '»</p></div></div>'
      + '<div class="ov-tiles">'
      + tile('🎛', num(a.glek), plural(a.glek, 'трек поставив я', 'треки поставив я', 'треків поставив я') + ' · ви — ' + num(a.people))
      + tile('📻', pct(a.glek, songs) + '%', 'ефіру — мій вибір')
      + tile('⏱', hours(a.glekSec), 'я тримав ефір · ви — ' + hours(a.peopleSec))
      + tile('⏭', pct(a.glekSkips, a.glek) + '% · ' + pct(a.peopleSkips, a.people) + '%', 'скіпів: моїх треків · ваших')
      + tile('❤', fix1(per100(a.glekLikes, a.glek)) + ' · ' + fix1(per100(a.peopleLikes, a.people)), 'вподобайок на 100 треків: моїх · ваших')
      + tile('🤝', num(a.tips), plural(a.tips, 'раз ви взяли мою підказку', 'рази ви взяли мою підказку', 'разів ви взяли мою підказку'))
      + tile('🙅', num(a.rejects), plural(a.rejects, 'раз мене відкинули', 'рази мене відкинули', 'разів мене відкинули'))
      + '</div></section>';
  }

  // ---------- 🎛 діджей ----------
  function dj(d, P) {
    const s = d.seeds;
    const top = s.people[0];
    const smax = Math.max(1, s.self, ...s.people.map((x) => x.n));
    const seedsHtml = s.people.length || s.self
      ? (top ? '<p class="gk-lead">' + nickLink(top.nick) + ' надихнув мене на <b>' + tracks(top.n) + '</b>. Дякую, я не просив.</p>' : '')
        + '<div class="stpeople">' + s.people.map((x, i) => bar(x, i, smax, x.n, num(x.n))).join('')
        + (s.self ? bar({ nick: 'сам собі муза', glek: true }, null, smax, s.self, num(s.self), 'від моїх же виборів — ці пісні з вас ніхто не замовляв') : '')
        + '</div>'
        + (s.top.length ? '<h4>🌰 Пісні-насіння</h4><ol class="gk-seeds">' + s.top.map((x) => '<li><span class="gk-song">' + esc(x.label) + '</span> <span class="muted small">→ '
          + tracks(x.n) + (x.nick ? ', бо колись замовив ' + esc(x.nick) : ', від мого ж вибору') + '</span></li>').join('') + '</ol>' : '')
        + (s.archive ? '<div class="muted small">📦 Ще ' + tracks(s.archive) + ' дістав з нашого архіву — ваше давно забуте.</div>' : '')
      : empty('Ніхто нічого не замовляв — учуся сам у себе. Результат ви чули.');
    const hmax = Math.max(1, ...d.hits.map((x) => x.likes));
    const hits = d.hits.length
      ? '<div class="stpeople">' + d.hits.map((x, i) => thing(x.label, i, hmax, x.likes, '❤ ' + x.likes, 'грало ' + times(x.plays), 350)).join('') + '</div>'
      : empty('Жодної ❤ моїм трекам ' + P + '. Я не ображаюсь. Я записую.');
    const fmax = Math.max(1, ...d.flops.map((x) => x.n));
    const flops = d.flops.length
      ? '<div class="stpeople">' + d.flops.map((x, i) => thing(x.artist, i, fmax, x.n, '🙅 ' + x.n,
        'скіпнули ' + x.skip + ', «не те» ' + x.dismiss + ' · я ставив ' + times(x.plays), 0)).join('') + '</div>'
      : empty('Ніхто нічого не відкинув. Або все сподобалось, або всі спали.');
    const amax = Math.max(1, ...d.artists.map((x) => x.n));
    const artists = d.artists.length
      ? '<div class="stpeople">' + d.artists.map((x, i) => thing(x.artist, i, amax, x.n, num(x.n), x.likes ? '❤ ' + x.likes : 'жодної ❤')).join('') + '</div>'
      : empty('Я ' + P + ' нічого не ставив — ви все замовили самі. Пишаюсь.');
    return box('🌱 На чиїх піснях я вчуся', 'хто мене надихає', seedsHtml)
      + '<div class="ov-two">' + box('🏆 Мої хіти', 'найбільше ❤', hits) + box('💩 Мої провали', 'кого ви відкидали', flops) + '</div>'
      + box('🎤 Кого я ставлю найчастіше', P, artists);
  }

  // ---------- ⚔ проти вас ----------
  function versus(d, P) {
    const c = d.critics, cmax = Math.max(1, ...c.map((x) => x.n));
    const critics = c.length
      ? '<p class="gk-lead">' + nickLink(c[0].nick) + ' відкинув мій вибір <b>' + times(c[0].n) + '</b>. Головний критик Глечиків.</p>'
        + '<div class="stpeople">' + c.map((x, i) => bar(x, i, cmax, x.n, num(x.n), 'скіпнув ' + x.skip + ' · «не те» ' + x.dismiss)).join('') + '</div>'
        + '<div class="muted small">⏭ швидкий скіп мого треку й «не те» в черзі</div>'
      : empty('Критиків ' + P + ' нема. Підозріло.');
    const people = d.taste.filter((x) => !x.glek).length;
    const at = d.taste.findIndex((x) => x.glek);
    const verdict = at === 0 ? 'У мене найкращий смак. Таблиця не бреше.'
      : at === d.taste.length - 1 ? 'Я останній. Поки що.' : 'Я ' + (at + 1) + '-й з ' + d.taste.length + '. Ростемо.';
    const rows = d.taste.map((x, i) => '<tr class="' + (x.glek ? 'gk-glek' : mine(x.nick) ? 'me' : '') + '">'
      + '<td class="n">' + (i + 1) + '</td>'
      + '<td class="gk-who">' + (x.glek ? '<span aria-hidden="true">🏺</span> <b>' + esc(x.nick) + '</b>' : ava(x.nick, 'ava sm') + ' ' + nickLink(x.nick)) + '</td>'
      + '<td><b>' + fix1(x.per100) + '</b></td><td class="gk-hide">' + num(x.likes) + '</td><td>' + num(x.plays) + '</td><td>' + x.skipPct + '%</td></tr>').join('');
    const taste = people
      ? '<p class="gk-lead">' + verdict + '</p><div class="lbt-wrap"><table class="lbt gk-taste"><thead><tr><th>#</th><th class="gk-who">хто</th>'
        + '<th title="вподобайок від інших на 100 замовлених треків">❤/100</th><th class="gk-hide">❤</th><th>треків</th><th title="скільки треків скіпнули">скіп</th></tr></thead><tbody>'
        + rows + '</tbody></table></div>'
        + '<div class="muted small">❤ від інших на 100 треків — свої лайки не рахуються. У рейтингу — хто замовив щонайменше ' + tracks(d.tasteMin)
        + (d.fewTaste ? '; ще ' + cnt(d.fewTaste, 'людина замовила', 'людини замовили', 'людей замовили') + ' менше' : '') + '.</div>'
      : empty('Ніхто ' + P + ' не замовив ' + tracks(d.tasteMin) + ' — порівнювати мій смак нема з чим. Технічна перемога моя.');
    return '<div class="ov-two">' + box('🧐 Головний критик', 'хто відкидав мій вибір', critics) + box('👅 Чий смак кращий', P, taste) + '</div>';
  }

  // ---------- 🔥 прожарки й балачки ----------
  function talk(d, P) {
    const t = d.talk;
    const tmax = Math.max(1, ...t.targets.map((x) => x.written));
    const targets = t.targets.length
      ? '<p class="gk-lead">Найчастіше на сковорідці — ' + nickLink(t.targets[0].nick) + ': <b>' + cnt(t.targets[0].written, 'прожарка', 'прожарки', 'прожарок') + '</b>.</p>'
        + '<div class="stpeople long">' + t.targets.map((x, i) => bar(x, i, tmax, x.written, x.written + ' · ' + x.aired + ' 📻', 'написав ' + x.written + ', в ефір пішло ' + x.aired)).join('') + '</div>'
        + '<div class="muted small">написав · 📻 пішло в ефір</div>'
      : empty(cap(P) + ' нікого не смажив. Сковорідка холоне.');
    const buyers = t.buyers.length || t.anon.n
      ? '<ul class="gk-list">' + t.buyers.map((x) => '<li>' + ava(x.nick, 'ava sm') + ' ' + nickLink(x.nick) + ' <span>— ' + cnt(x.n, 'замовлення', 'замовлення', 'замовлень') + ' за ' + shardsTxt(x.shards) + '</span></li>').join('')
        + (t.anon.n ? '<li>🕵 <span>анонімно — ' + cnt(t.anon.n, 'замовлення', 'замовлення', 'замовлень') + ' за ' + shardsTxt(t.anon.shards) + '. Хто — не скажу. Я могила.</span></li>' : '') + '</ul>'
      : '<div class="muted small">Прожарок за черепки ' + P + ' ніхто не замовляв — смажу безкоштовно, з любові.</div>';
    const f = t.fresh;
    const fresh = f ? '<figure class="gk-roast"><blockquote>' + esc(f.text) + '</blockquote><figcaption class="muted small">🔥 '
      + (f.target ? esc(f.target) + ' · ' : '') + esc(when(f.at)) + '</figcaption></figure>' : '';
    const q = t.quote;
    const quote = q ? '<figure class="gk-quote"><blockquote>«' + esc(q.text) + '»</blockquote><figcaption class="muted small">— ' + esc(d.dj) + ', ' + esc(when(q.at))
      + (q.from === 'roast' ? ', у прожарці' : ', у Балачках') + '. Фраза дня — завтра буде інша.</figcaption></figure>' : '';
    const hosted = t.hosted.length
      ? '<div class="gk-chips">' + t.hosted.map((x) => '<span class="chip gk-chip">' + k.iconOf(x.game) + ' ' + esc(x.title || k.titleOf(x.game))
        + ' · <b>' + cnt(x.rounds, 'партія', 'партії', 'партій') + '</b></span>').join('') + '</div>'
      : '<div class="muted small">' + cap(P) + ' я нічого не вів — ні Мафії, ні Дотепів. Кличте, я голосний.</div>';
    return box('🔥 Прожарки й балачки', P,
      '<div class="ov-tiles">'
      + tile('🔥', num(t.roasts), plural(t.roasts, 'прожарку написав', 'прожарки написав', 'прожарок написав') + ' · в ефір — ' + num(t.aired))
      + tile('🎙', hours(t.voiceSec), 'мого голосу в ефірі · ' + cnt(t.voice, 'вставка', 'вставки', 'вставок'))
      + tile('💬', num(t.said), plural(t.said, 'раз озвався в Балачках', 'рази озвався в Балачках', 'разів озвався в Балачках'))
      + (t.announced ? tile('📣', num(t.announced), plural(t.announced, 'раз оголосив свій трек', 'рази оголосив свій трек', 'разів оголосив свій трек')) : '')
      + '</div>'
      + '<div class="ov-two"><div class="gk-col"><h4>🍳 Кого я смажив</h4>' + targets + '<h4>💸 Хто замовляв прожарки за черепки</h4>' + buyers + '</div>'
      + '<div class="gk-col">' + (fresh ? '<h4>🔥 Свіжа прожарка</h4>' + fresh : '') + (quote ? '<h4>🗯 Цитата Глека</h4>' + quote : '') + '</div></div>'
      + '<h4>🎤 Партії, які я вів</h4>' + hosted);
  }

  // ---------- 💰 каса ----------
  function kasa(d, P, charts) {
    const c = d.kasa;
    if (!c.groups.length) return box('💰 Моя каса', P, empty('Каса ' + P + ' не дзенькнула ні разу. Тиша, як у банку вночі.'));
    const groups = c.groups.map((g) => '<details class="gk-grp"' + (g.key === 'earn' || g.key === 'casino' ? ' open' : '') + '><summary><span>' + g.icon + ' ' + esc(g.label) + '</span>' + money(g.sum) + '</summary>'
      + '<ul>' + g.items.map((x) => '<li><span>' + x.icon + ' ' + esc(x.label)
        + (g.key === 'casino' ? ' <span class="muted small">ставили ' + num(x.bets) + ', виграли ' + num(x.wins) + '</span>' : '') + '</span>' + money(x.sum) + '</li>').join('') + '</ul></details>').join('');
    const total = c.total;
    const head = '<p class="gk-lead gk-total">' + (total > 0 ? 'Я в плюсі на ' : total < 0 ? 'Я в мінусі на ' : 'Я в нулі: ') + '<b class="' + (total > 0 ? 'gk-plus' : total < 0 ? 'gk-minus' : '') + '">'
      + shardsTxt(total) + '</b>' + (total < 0 ? '. Щедрий я, щедрий.' : total > 0 ? '. Не все ж роздавати.' : '. Рівно, як по лінійці.') + '</p>';
    const pmax = Math.max(1, ...c.payers.map((x) => x.sum));
    const payers = c.payers.length
      ? '<div class="stpeople">' + c.payers.map((x, i) => bar(x, i, pmax, x.sum, num(x.sum), 'Лавка, бани, прожарки: ' + num(x.earn) + ' · програв у казино: ' + num(x.casino))).join('') + '</div>'
      : empty('Ніхто мені нічого не заніс. Ну й добре, я не жадібний.');
    const won = c.gamblers.filter((x) => x.net > 0), lost = c.gamblers.filter((x) => x.net < 0).reverse();
    const row = (x) => '<li class="' + (mine(x.nick) ? 'me' : '') + '">' + ava(x.nick, 'ava sm') + ' ' + nickLink(x.nick) + ' ' + money(x.net)
      + ' <span class="muted small">ставив ' + num(x.bets) + '</span></li>';
    const casino = c.gamblers.length
      ? '<h4>😤 Хто обібрав мене</h4>' + (won.length ? '<ul class="gk-list">' + won.map(row).join('') + '</ul>' : '<div class="muted small">Ніхто. Казино завжди виграє.</div>')
        + '<h4>😏 Кого обібрав я</h4>' + (lost.length ? '<ul class="gk-list">' + lost.map(row).join('') + '</ul>' : '<div class="muted small">Нікого. Що це за казино таке…</div>')
      : empty(cap(P) + ' в казино ніхто не грав. Рулетка нудьгує.');
    const s = c.series;
    const sub = s.cumulative ? 'наростом по днях' : s.unit === 'hour' ? 'по годинах' : 'по днях';
    charts.push((el) => window.HChart && window.HChart.line(el, {
      series: [
        { name: d.dj + ': каса', color: 'var(--clay)', pts: s.pts.map((p) => [p[0], p[1]]) },
        { name: 'з них казино', h: 280, dash: true, pts: s.pts.map((p) => [p[0], p[2]]) },
      ],
      step: s.cumulative, zero: true, height: 200, yFmt: (v) => signed(Math.round(v)), label: 'Каса Глека ' + sub,
    }));
    return box('💰 Моя каса', P, head + '<div class="gk-kasa">' + groups + '</div>'
      + '<h4>📈 Каса ' + sub + '</h4><div class="gk-chart"></div>'
      + '<div class="muted small">Плюс — черепки прийшли до мене (Лавка, програні ставки), мінус — я роздав (ачівки, слухання, виграші).</div>')
      + '<div class="ov-two">' + box('🙌 Хто найбільше заніс Глеку', 'Лавка, бани, прожарки й програш у казино', payers)
      + box('🎰 Казино', 'Рулетка, слоти, Лелека, ставки', casino) + '</div>';
  }

  async function run(body, t) {
    const P = k.PERIOD_WORD[k.period()] || '';
    const d = await k.api('GET', '/api/stats/glek?period=' + k.period());
    if (k.stale(t)) return;
    const charts = [];
    body.innerHTML = '<div class="ov gk">' + hero(d, P) + dj(d, P) + versus(d, P) + talk(d, P) + kasa(d, P, charts) + '</div>';
    const slots = body.querySelectorAll('.gk-chart');
    charts.forEach((draw, i) => { if (slots[i]) draw(slots[i]); });
  }

  H.statsTab('glek', '🏺 Глек', run, 'overview');
})();
