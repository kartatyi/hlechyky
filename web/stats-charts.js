/*
  «📊 Хто скільки» → «📈 Графіки» (🏁 Гонка, 📈 Біржа черепків, 📊 Ело в часі) і «📖 Рекорди» (Книга рекордів Глечиків).
  Рахує сервер (LitopysCharts.cs: /api/stats/race, /bourse, /elo, /records), тут лише малюємо через HPeople.kit і HChart.line.
*/
(() => {
  'use strict';
  const P = window.HPeople;
  if (!P || !P.statsTab || !window.HChart) return;
  const kit = P.kit;
  const esc = (s) => kit.esc(s);
  const myNick = () => (kit.me() && kit.me().nick) || '';
  const mine = (n) => !!myNick() && kit.same(n, myNick());

  const RACE_HEAD = { day: 'Гонка дня', week: 'Гонка тижня', month: 'Гонка місяця', all: 'Гонка за весь час' };
  /// Скільки рядків Гонки видно одразу (свій рядок — завжди, навіть нижче).
  const RACE_ROWS = 12;

  const sign = (n) => (n > 0 ? '+' : n < 0 ? '−' : '') + kit.num(Math.abs(n));
  const dayText = (d) => (d ? new Date(d + 'T12:00:00').toLocaleDateString('uk-UA', { day: 'numeric', month: 'long' }) : '');
  const cap1 = (s) => (s ? s[0].toUpperCase() + s.slice(1) : '');
  const wait = '<div class="gwait"><span class="spin"></span> рахую…</div>';
  const fail = '<div class="gempty">Глек загубив рахівницю. Спробуй ще раз трохи згодом.</div>';

  // =============================================================================================
  // 📈 Графіки
  // =============================================================================================

  async function charts(body, t) {
    const period = kit.period();
    body.innerHTML = '<div class="sc">'
      + '<section class="panel stbox sc-race"><h3>🏁 ' + RACE_HEAD[period] + ' <span class="muted small">очки за всі ігри</span></h3><div class="sc-slot">' + wait + '</div></section>'
      + '<section class="panel stbox sc-bourse"><h3>📈 Біржа черепків <span class="muted small">' + kit.PERIOD_WORD[period] + '</span></h3><div class="sc-slot">' + wait + '</div></section>'
      + '<section class="panel stbox sc-elo"><h3>📊 Ело в часі</h3><div class="sc-slot">' + wait + '</div></section>'
      + '</div>';
    const slot = (cls) => body.querySelector('.' + cls + ' .sc-slot');
    const nick = myNick();
    const fill = (cls, url, draw) => kit.api('GET', url).then(
      (d) => { if (!kit.stale(t)) draw(slot(cls), d, period); },
      () => { if (!kit.stale(t)) slot(cls).innerHTML = fail; });
    await Promise.all([
      fill('sc-race', '/api/stats/race?period=' + period, race),
      fill('sc-bourse', '/api/stats/bourse?period=' + period + (nick ? '&nick=' + encodeURIComponent(nick) : ''), bourse),
      elo(slot('sc-elo'), t),
    ]);
  }

  // ---------- 🏁 Гонка ----------

  function moveCell(x, word) {
    if (x.fresh) return '<span class="sc-new" title="' + esc(word + ' тому в гонці ще не був') + '">🆕</span>';
    if (x.move == null) return '';
    if (!x.move) return '<span class="muted" title="' + esc('те саме місце, що й ' + word + ' тому') + '">—</span>';
    const up = x.move > 0;
    return '<span class="' + (up ? 'sc-up' : 'sc-dn') + '" title="' + esc((up ? 'піднявся на ' : 'опустився на ') + kit.cnt(Math.abs(x.move), 'місце', 'місця', 'місць') + ' ' + word) + '">'
      + (up ? '▲' : '▼') + ' ' + Math.abs(x.move) + '</span>';
  }

  function prevLine(d) {
    const p = d.prev;
    if (!p || !p.nicks || !p.nicks.length) return '';
    const verb = p.nicks.length > 1 ? 'поділили першість' : 'виграв';
    return '<div class="sc-note">🏆 ' + esc(cap1(p.word)) + ' ' + verb + ' ' + P.kit.nickList(p.nicks) + ' — ' + kit.cnt(p.points, 'очко', 'очки', 'очок') + '.</div>';
  }

  function formula(p) {
    if (!p) return '';
    return '<div class="muted small sc-formula">Як рахуємо: партія за столом — ' + kit.cnt(p.play, 'очко', 'очки', 'очок') + ', перемога — ' + p.win
      + ', нічия — ' + p.draw + '; розгадана щоденка — ще ' + p.daily + '. Соло-рекорди в гонку не йдуть. Однакові очки — однакове місце.</div>';
  }

  function race(el, d, period) {
    const rows = d.rows || [];
    if (!rows.length) {
      el.innerHTML = '<div class="gempty glek">' + (period === 'day' ? 'Сьогодні ще ніхто не сів за стіл. Перша партія — і ти вже лідер гонки.' : 'Ще нікого на старті. Зіграй партію — і гонка почнеться.') + '</div>'
        + prevLine(d);
      return;
    }
    const meIdx = rows.findIndex((x) => mine(x.nick));
    const shown = rows.slice(0, RACE_ROWS);
    if (meIdx >= RACE_ROWS) shown.push(rows[meIdx]);
    const word = d.moveWord || 'за добу';
    const hasMoves = rows.some((x) => x.move != null || x.fresh);
    el.innerHTML = '<div class="lbt-wrap"><table class="lbt sc-rt"><thead><tr><th class="n">#</th><th class="who">хто</th>'
      + '<th class="main" title="очки гонки — як рахуємо, написано під таблицею">очки</th>'
      + '<th class="opt" title="перемоги з партій за столами">перемоги</th>'
      + '<th class="opt" title="розгадані щоденки">🧩</th>'
      + (hasMoves ? '<th class="sc-mv" title="' + esc('як змінилось місце ' + word) + '">' + esc(word) + '</th>' : '')
      + '</tr></thead><tbody>'
      + shown.map((x) => '<tr class="' + (x.place <= 3 ? 'top' + x.place : '') + (mine(x.nick) ? ' me' : '') + '">'
        + '<td class="n">' + kit.medal(x.place - 1) + '</td>'
        + '<td class="who"><span class="glb-who">' + kit.ava(x.nick, 'ava sm') + kit.nickLink(x.nick) + '</span></td>'
        + '<td class="main">' + kit.num(x.points) + '</td>'
        + '<td class="opt">' + kit.num(x.wins) + '<span class="muted"> / ' + kit.num(x.games) + '</span></td>'
        + '<td class="opt">' + (x.dailies ? kit.num(x.dailies) : '<span class="muted">—</span>') + '</td>'
        + (hasMoves ? '<td class="sc-mv">' + moveCell(x, word) + '</td>' : '')
        + '</tr>').join('')
      + '</tbody></table></div>'
      + (rows.length > shown.length ? '<div class="muted small sc-more">і ще ' + kit.cnt(rows.length - shown.length, 'учасник', 'учасники', 'учасників') + ' нижче</div>' : '')
      + '<div class="sc-notes">'
      + (d.climb ? '<div class="sc-note">🚀 ' + esc(cap1(word)) + ' найвище злетів ' + kit.nickLink(d.climb.nick) + ' — на ' + kit.cnt(d.climb.by, 'місце', 'місця', 'місць') + ' вгору.</div>' : '')
      + prevLine(d)
      + '</div>'
      + formula(d.points);
  }

  // ---------- 📈 Біржа ----------

  /// Що Глек каже про те, на чому людина розбагатіла чи спустила.
  const DOWN_QUIP = { '🎡 рулетка': 'Глек каже «дякую» 🙏', '🎰 слоти': 'автомати ситі', '🛍 Лавка': 'зате тепер гарний', '🚫 бани треків': 'тиша дорого коштує',
    '🔥 прожарки': 'зате смішно було', '🃏 столи на черепки': 'стіл не пробачає', '🪙 ставки за столом': 'азарт — річ дорога', '💵 продав за гривні': 'черепки пішли в люди' };
  const UP_QUIP = { '🎡 рулетка': 'фортуна підморгнула', '🎰 слоти': 'автомат розщедрився', '📻 слухав радіо': 'вуха — теж капітал', '🏺 гончарне коло': 'глеки не брешуть',
    '🎲 партії': 'чесно виграно', '🧩 щоденки': 'голова варить', '💵 купив за гривні': 'гроші до грошей', '🏅 ачівки': 'за заслуги' };

  function mover(x, up) {
    const quip = (up ? UP_QUIP : DOWN_QUIP)[x.topic];
    return '<div class="sc-mover' + (mine(x.nick) ? ' me' : '') + '">' + kit.ava(x.nick, 'ava sm') + '<div class="sc-mover-b">'
      + '<div class="sc-mover-h">' + kit.nickLink(x.nick) + '<b class="' + (up ? 'sc-up' : 'sc-dn') + '">' + sign(x.net) + ' 🏺</b></div>'
      + '<span class="muted small">найбільше — ' + esc(x.topic) + ' (' + sign(x.topicNet) + ')' + (quip ? ' · ' + esc(quip) : '') + '</span></div></div>';
  }

  function bourse(el, d, period) {
    const series = d.series || [];
    if (!series.length) {
      el.innerHTML = '<div class="gempty glek">На біржі тиша: ' + kit.PERIOD_WORD[period] + ' черепки нікуди не ходили.</div>';
      return;
    }
    const col = (head, list, isUp, empty) => '<div class="sc-mcol"><h4>' + head + '</h4>'
      + (list.length
        ? mover(list[0], isUp) + (list.length > 1 ? '<div class="sc-mrest small">' + list.slice(1).map((x) => kit.nickLink(x.nick) + ' <span class="' + (isUp ? 'sc-up' : 'sc-dn') + '">' + sign(x.net) + '</span>').join('<span class="muted"> · </span>') + '</div>' : '')
        : '<div class="muted small">' + empty + '</div>') + '</div>';
    const top = series.filter((s) => !mine(s.nick)).length;
    el.innerHTML = '<div class="sc-chart"></div>'
      + '<div class="muted small sc-cap">Скільки черепків у гаманці в кожну мить: топ-' + top + ' за тим, що є зараз' + (series.some((s) => mine(s.nick)) ? ', і ти' : '')
      + '. Глека тут нема — у нього не гаманець, а бездонний глек.</div>'
      + '<div class="sc-movers">'
      + col('📈 Найбільше розбагатів', d.up || [], true, 'Ніхто не пішов у плюс.')
      + col('📉 Найбільше спустив', d.down || [], false, 'Ніхто нічого не спустив — дивина.')
      + '</div>';
    window.HChart.line(el.querySelector('.sc-chart'), {
      series: series.map((s) => ({ name: s.nick, h: kit.hue(s.nick), pts: s.pts, me: mine(s.nick) })),
      step: true, zero: true, height: 230, label: 'Баланс черепків у часі',
    });
  }

  // ---------- 📊 Ело ----------

  const ELO_PERIODS = [['all', 'увесь час'], ['month', '30 днів'], ['week', '7 днів']];

  async function elo(el, t) {
    let game = kit.ls('scEloGame', '');
    let per = kit.ls('scEloPer', 'all');
    if (!ELO_PERIODS.some(([k]) => k === per)) per = 'all';
    async function load() {
      const d = await kit.api('GET', '/api/stats/elo?period=' + per + (game ? '&game=' + encodeURIComponent(game) : '')).catch(() => null);
      if (kit.stale(t) || !el.isConnected) return;
      if (!d) { el.innerHTML = fail; return; }
      draw(d);
    }
    function draw(d) {
      const games = d.games || [];
      if (!games.length || !d.game) {
        el.innerHTML = '<div class="gempty glek">Рейтингових партій на двох ще нема — Ело росте лише в шахах, шашках, хрестиках і схожих дуелях.</div>';
        return;
      }
      const rows = d.rows || [];
      el.innerHTML = '<div class="sc-bar"><div class="sc-chips" role="group" aria-label="Гра">'
        + games.map((g) => '<button type="button" class="sc-chip' + (g.id === d.game ? ' on' : '') + '" data-g="' + esc(g.id) + '" title="' + esc(kit.cnt(g.rounds, 'партія', 'партії', 'партій') + ', ' + kit.cnt(g.people, 'людина', 'людини', 'людей')) + '">'
          + (kit.iconOf(g.id) || '🎲') + ' ' + esc(g.title) + '</button>').join('')
        + '</div><div class="sc-chips sc-per" role="group" aria-label="За який час">'
        + ELO_PERIODS.map(([k, l]) => '<button type="button" class="sc-chip' + (k === d.period ? ' on' : '') + '" data-p="' + k + '">' + l + '</button>').join('')
        + '</div></div>'
        + (rows.length
          ? '<div class="sc-chart"></div>'
            + '<div class="lbt-wrap"><table class="lbt sc-et"><thead><tr><th class="n">#</th><th class="who">хто</th><th class="main">Ело</th>'
            + '<th title="як змінилось за вибраний час">зміна</th><th class="opt" title="найвище Ело за весь час">пік</th>'
            + '<th class="opt" title="перемоги / нічиї / поразки за вибраний час">В / Н / П</th></tr></thead><tbody>'
            + rows.map((x, i) => '<tr class="' + (i < 3 ? 'top' + (i + 1) : '') + (mine(x.nick) ? ' me' : '') + '"><td class="n">' + kit.medal(i) + '</td>'
              + '<td class="who"><span class="glb-who">' + kit.ava(x.nick, 'ava sm') + kit.nickLink(x.nick) + '</span></td>'
              + '<td class="main">' + x.elo + '</td>'
              + '<td><span class="' + (x.delta > 0 ? 'sc-up' : x.delta < 0 ? 'sc-dn' : 'muted') + '">' + (x.delta ? sign(x.delta) : '0') + '</span></td>'
              + '<td class="opt">' + x.peak + '</td>'
              + '<td class="opt">' + x.wins + ' / ' + x.draws + ' / ' + x.losses + '</td></tr>').join('')
            + '</tbody></table></div>'
          : '<div class="gempty glek">' + esc(d.title || 'Тут') + ': за цей час ніхто не грав на рейтинг.</div>')
        + '<div class="muted small sc-formula">Ело відтворене з усіх записаних партій тими самими правилами, що й таблиця гри: старт 1000, перші 10 партій K=48, далі K=32.</div>';
      el.querySelectorAll('[data-g]').forEach((b) => b.addEventListener('click', () => { game = b.dataset.g; kit.lsSet('scEloGame', game); load(); }));
      el.querySelectorAll('[data-p]').forEach((b) => b.addEventListener('click', () => { per = b.dataset.p; kit.lsSet('scEloPer', per); load(); }));
      const box = el.querySelector('.sc-chart');
      if (box) {
        window.HChart.line(box, {
          series: (d.series || []).map((s) => ({ name: s.nick, h: kit.hue(s.nick), pts: s.pts, me: mine(s.nick) })),
          step: true, height: 220, label: 'Ело в часі — ' + (d.title || ''),
        });
      }
    }
    await load();
  }

  // =============================================================================================
  // 📖 Рекорди
  // =============================================================================================

  function recCard(r) {
    const me = (r.nicks || []).some(mine);
    const who = r.site ? '<span class="sc-day">📅 ' + esc(dayText(r.day)) + '</span>' : kit.nickList(r.nicks, true);
    const prev = r.prev
      ? '<div class="sc-prev muted small">до цього — ' + (r.site ? esc(dayText(r.prev.day)) : kit.nickList(r.prev.nicks) + ' (' + esc(dayText(r.prev.day)) + ')')
        + ': ' + esc(r.prev.text) + '</div>'
      : '';
    const note = !r.note ? '' : r.key === 'pearl' ? '<blockquote class="sc-quote">«' + esc(r.note) + '»</blockquote>' : '<div class="sc-rnote muted small">' + esc(r.note) + '</div>';
    return '<article class="sc-rec' + (r.fresh ? ' fresh' : '') + (me ? ' me' : '') + '">'
      + '<div class="sc-rh"><span class="sc-ri" aria-hidden="true">' + esc(r.icon) + '</span><span class="sc-rtt" title="' + esc(r.what) + '">' + esc(r.title) + '</span>'
      + (r.fresh ? '<span class="sc-fresh">🆕 новий</span>' : '') + '</div>'
      + '<div class="sc-who">' + who + (me ? ' <span class="ovt-you">це ти!</span>' : '') + '</div>'
      + '<b class="sc-val">' + esc(r.text) + '</b>'
      + note
      + (r.site ? '' : '<div class="sc-when muted small">' + esc(dayText(r.day)) + '</div>')
      + prev
      + '</article>';
  }

  async function records(body, t) {
    const d = await kit.api('GET', '/api/stats/records');
    if (kit.stale(t)) return;
    const recs = (d && d.records) || [];
    const fresh = recs.filter((r) => r.fresh).length;
    body.innerHTML = '<section class="panel stbox sc-book"><h3>📖 Книга рекордів Глечиків <span class="muted small">за весь час</span></h3>'
      + '<p class="muted sc-intro">Глек усе записує й нічого не забуває. Хочеш сюди — перебий.'
      + (fresh ? ' <b class="sc-freshn">' + kit.cnt(fresh, 'рекорд', 'рекорди', 'рекордів') + ' — ' + (fresh === 1 ? 'свіжий' : 'свіжі') + ', за останні ' + kit.cnt((d && d.freshDays) || 3, 'день', 'дні', 'днів') + '.</b>' : '') + '</p>'
      + (recs.length ? '<div class="sc-recs">' + recs.map(recCard).join('') + '</div>' : '<div class="gempty glek">Книга ще порожня — бери ручку, пиши першим.</div>')
      + '</section>';
  }

  P.statsTab('charts', '📈 Графіки', charts, 'glek');
  P.statsTab('records', '📖 Рекорди', records, 'charts');
})();
