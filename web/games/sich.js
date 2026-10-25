/*
  🏆 Січ щоденних — тижневий залік усіх щоденних головоломок і вічний рейтинг зі званнями. Панель розділу ігор
  (#games/x:sich, вкладка — тиждень: #games/x:sich/2026-10-19). Рахує сервер (Games/Economy/Sich.cs), тут — показ.

  GET /api/games/sich?week= → { week, from, to, live, daysLeft, players, rows: [{ nick, place, points, solved, byDay[7], elo,
    rank, rankIcon, eloToday }], noms: [{ id, icon, title, hint, nicks, value }], eternal: [{ nick, elo, rank, rankIcon, days,
    today }], crown: [nick], weeks: [понеділок], rules: { prizes, nomPrize, minPlayers, tiers: [{ icon, title, min }] },
    me: { place, points, elo, rank, rankIcon, nextRank, nextNeed, duels: [{ nick, w, l, allW, allL }], whatIf: [{ game, title,
    gain, place }] } }
  GET /api/games/sich/marks → { crown: [nick], ranks: { нік: { icon, title, elo } } } — для 👑 і звання біля ніка (app.js, people.js).
*/
(() => {
  const DOW = ['пн', 'вт', 'ср', 'чт', 'пт', 'сб', 'нд'];
  const MEDALS = ['🥇', '🥈', '🥉'];
  let host = null, ctx = null, data = null, week = '', at = 0;

  const esc = (s) => (ctx ? ctx.esc(s) : String(s == null ? '' : s));
  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const num = (n) => Number(n || 0).toLocaleString('uk-UA');
  const nick = (n) => (window.HPeople && HPeople.nickLink ? HPeople.nickLink(n, 'sich-nick who-n') : '<b>' + esc(n) + '</b>');
  const plural = (n, one, few, many) => {
    const t = Math.abs(n) % 100, o = t % 10;
    return t > 10 && t < 20 ? many : o === 1 ? one : o >= 2 && o <= 4 ? few : many;
  };
  const pts = (n) => n + ' ' + plural(n, 'очко', 'очки', 'очок');
  const ddmm = (day) => { const p = String(day || '').split('-'); return p.length === 3 ? p[2] + '.' + p[1] : day; };
  const range = (monday) => {
    const a = new Date(monday + 'T12:00:00'), b = new Date(a.getTime() + 6 * 864e5);
    const two = (x) => String(x).padStart(2, '0');
    return a.getMonth() === b.getMonth() ? two(a.getDate()) + '–' + two(b.getDate()) + '.' + two(b.getMonth() + 1)
      : two(a.getDate()) + '.' + two(a.getMonth() + 1) + '–' + two(b.getDate()) + '.' + two(b.getMonth() + 1);
  };
  const delta = (d) => (d > 0 ? '<span class="sich-up">▲' + d + '</span>' : d < 0 ? '<span class="sich-down">▼' + (-d) + '</span>' : '');

  function load(force) {
    if (!force && data && data.week === week && Date.now() - at < 30000) { paint(); return; }
    const q = week ? '?week=' + encodeURIComponent(week) : '';
    ctx.api('GET', '/api/games/sich' + q).then((d) => { data = d; at = Date.now(); paint(); })
      .catch(() => { if (host) host.innerHTML = '<div class="gempty">Січ не відповідає — спробуй трохи згодом.</div>'; });
  }

  function head(d) {
    const live = d.live;
    const left = !live ? 'підсумок' : d.daysLeft > 1 ? 'ще ' + d.daysLeft + ' ' + plural(d.daysLeft, 'день', 'дні', 'днів') + ' і сьогодні'
      : d.daysLeft === 1 ? 'завтра останній день' : 'останній день — опівночі 👑';
    const chips = [['', 'Цей тиждень']].concat((d.weeks || []).map((w) => [w, range(w)]))
      .map(([w, t]) => '<button type="button" class="chip' + ((w || '') === (live ? '' : d.week) ? ' on' : '') + '" data-week="' + esc(w) + '">' + esc(t) + '</button>').join('');
    const r = d.rules || {};
    const prizes = (r.prizes || []).filter((x) => x > 0).map((x, i) => MEDALS[i] + ' ' + num(x)).join(' · ');
    return '<div class="sich-head"><div><h3>🏆 Січ щоденних <span class="muted small">· ' + esc(range(d.week)) + ' · ' + esc(left) + '</span></h3>'
      + '<p class="small sich-prize">👑 Гетьман тижня носить корону весь наступний тиждень'
      + (prizes ? ' · ' + prizes + ' 🏺' : '') + (r.nomPrize ? ' · номінації по ' + num(r.nomPrize) + ' 🏺' : '')
      + '<span class="muted"> · коли в заліку щонайменше ' + (r.minPlayers || 3) + '</span></p></div>'
      + ((d.weeks || []).length ? '<div class="sich-weeks">' + chips + '</div>' : '') + '</div>'
      + ((d.crown || []).length ? '<p class="sich-crown">👑 Корону цього тижня носить ' + d.crown.map(nick).join(', ') + '</p>' : '');
  }

  function meCard(d) {
    const m = d.me;
    if (!m) return '';
    const place = m.place ? '<b class="sich-big">' + (MEDALS[m.place - 1] || m.place + '.') + ' ' + m.place + ' місце</b> · ' + pts(m.points)
      : '<b class="sich-big">Ще без очок</b> · закрий будь-яку щоденну — і ти в Січі';
    const rank = m.rankIcon + ' <b>' + esc(m.rank) + '</b> · ' + m.elo + ' Ело'
      + (m.nextRank ? ' <span class="muted">· до звання «' + esc(m.nextRank) + '» ще ' + m.nextNeed + '</span>' : ' <span class="muted">· вище нема куди</span>');
    const ifs = (m.whatIf || []).map((x) => '<button type="button" class="sich-if" data-solo="' + esc(x.game) + '" title="Ще не грано сьогодні — грати">'
      + (HGames.iconOf ? HGames.iconOf(x.game) : '') + '<span>Закрий «' + esc(x.title) + '» першим — <b>+' + x.gain + '</b>'
      + (m.place !== x.place ? ' і будеш <b>' + x.place + '-м</b>' : x.place === 1 ? ' і втримаєш перше' : '') + '</span></button>').join('');
    return '<section class="sich-me"><div class="sich-me-l"><div>' + place + '</div><div class="small">' + rank + '</div></div>'
      + (ifs ? '<div class="sich-ifs"><span class="muted small">💡 Що, якби сьогодні:</span>' + ifs + '</div>' : '') + '</section>';
  }

  function table(d) {
    const rows = d.rows || [];
    if (!rows.length) return '<div class="gempty glek">Цього тижня ще ніхто не розгадав жодної щоденної. Будь першим — і ти лідер Січі!</div>';
    const today = d.live ? (7 - 1 - d.daysLeft) : -1;
    const max = Math.max(1, ...rows.map((r) => Math.max(...r.byDay)));
    const days = '<span class="sich-days">' + DOW.map((x, i) => '<i class="' + (i === today ? 'today' : '') + '">' + x + '</i>').join('') + '</span>';
    const body = rows.map((r) => {
      const me = d.me && same(r.nick, d.me.nick);
      const crown = (d.crown || []).some((c) => same(c, r.nick)) ? ' <span title="Гетьман минулого тижня">👑</span>' : '';
      const bars = '<span class="sich-days">' + r.byDay.map((v, i) => '<i class="' + (i === today ? 'today' : '') + (v ? ' on' : '') + '" style="--v:'
        + (v / max).toFixed(2) + '" title="' + DOW[i] + ': ' + pts(v) + '">' + (v || '') + '</i>').join('') + '</span>';
      return '<div class="sich-row' + (me ? ' me' : '') + (r.place <= 3 && r.points > 0 ? ' p' + r.place : '') + '">'
        + '<span class="sich-pl">' + (r.points > 0 && MEDALS[r.place - 1] ? MEDALS[r.place - 1] : r.place) + '</span>'
        + '<span class="sich-who">' + nick(r.nick) + crown + '</span>' + bars
        + '<b class="sich-pts" title="' + r.solved + ' ' + plural(r.solved, 'розв\'язок', 'розв\'язки', 'розв\'язків') + '">' + r.points + '</b>'
        + '<span class="sich-rank" title="' + esc(r.rank + ' · ' + r.elo + ' Ело') + '">' + r.rankIcon + ' ' + r.elo + delta(r.eloToday) + '</span></div>';
    }).join('');
    return '<section class="sich-table"><div class="sich-row sich-th muted small"><span></span><span>хто</span>' + days
      + '<span title="Очки тижня">очки</span><span>звання</span></div>' + body + '</section>';
  }

  function noms(d) {
    const list = d.noms || [];
    if (!list.length) return '';
    const word = { early: ['раз першим', 'рази першим', 'разів першим'], sniper: ['влучання', 'влучання', 'влучань'],
      fast: ['раз найшвидший', 'рази найшвидший', 'разів найшвидший'], iron: ['день', 'дні', 'днів'] };
    return '<h4>🎖 Номінації тижня' + (d.rules && d.rules.nomPrize ? ' <span class="muted small">· по ' + num(d.rules.nomPrize) + ' 🏺</span>' : '') + '</h4>'
      + '<div class="sich-noms">' + list.map((n) => {
        const w = word[n.id] || ['', '', ''];
        return '<div class="sich-nom" title="' + esc(n.hint) + '"><span class="sich-nom-i">' + n.icon + '</span><div><b>' + esc(n.title) + '</b>'
          + '<span class="small">' + n.nicks.map(nick).join(', ') + '</span><span class="muted small">' + n.value + ' ' + plural(n.value, w[0], w[1], w[2]) + ' · ' + esc(n.hint) + '</span></div></div>';
      }).join('') + '</div>';
  }

  function duels(d) {
    const list = (d.me && d.me.duels) || [];
    if (!list.length) return '';
    const score = (w, l) => '<b class="' + (w > l ? 'sich-up' : w < l ? 'sich-down' : '') + '">' + w + ':' + l + '</b>';
    return '<h4>⚔ Твої дуелі <span class="muted small">· скільки разів ти обійшов і тебе обійшли в одній головоломці</span></h4>'
      + '<div class="sich-duels">' + list.map((x) => '<div class="sich-duel">' + nick(x.nick)
        + '<span class="small">тиждень ' + score(x.w, x.l) + '</span><span class="small muted">усього ' + score(x.allW, x.allL) + '</span></div>').join('') + '</div>';
  }

  function eternal(d) {
    const list = d.eternal || [];
    if (!list.length) return '';
    const tiers = ((d.rules && d.rules.tiers) || []).map((t) => '<span class="chip" title="від ' + t.min + ' Ело">' + t.icon + ' ' + esc(t.title) + '</span>').join('<span class="muted">→</span>');
    return '<details class="sich-eternal"' + (d.me && !d.me.place ? ' open' : '') + '><summary><h4>🏺 Вічний рейтинг і звання</h4></summary>'
      + '<p class="muted small">Ело за всі щоденні з початку: кожна головоломка дня — партія з усіма, хто її грав. Обійти сильного дає більше, ніж слабкого.</p>'
      + '<div class="sich-tiers">' + tiers + '</div>'
      + list.map((e, i) => '<div class="sich-row sich-et' + (d.me && same(e.nick, d.me.nick) ? ' me' : '') + '"><span class="sich-pl">' + (i + 1) + '</span>'
        + '<span class="sich-who">' + nick(e.nick) + '</span><span class="sich-rank">' + e.rankIcon + ' ' + esc(e.rank) + '</span>'
        + '<b class="sich-pts">' + e.elo + delta(e.today) + '</b><span class="muted small">' + e.days + ' ' + plural(e.days, 'щоденна', 'щоденні', 'щоденних') + '</span></div>').join('')
      + '</details>';
  }

  const RULES = '<details class="sich-rules"><summary class="small">Як рахуються очки</summary><ul class="small">'
    + '<li>У кожній щоденній за день: розгадав — <b>1 очко</b> і ще по очку за кожного, кого обійшов (менше спроб, тоді швидше; де гра дає очки — більше очок).</li>'
    + '<li>Взявся й не вийшло — 0, і тебе обходять усі, хто розгадав. Однаковий результат — спільне місце.</li>'
    + '<li>Що більше людей грає, то дорожча перемога: закрити всі щоденні — найвигідніше.</li>'
    + '<li>Тиждень — з понеділка до неділі. Опівночі в неділю — підсумок: 👑, черепки місцям і номінації. Рівні очки — вище той, хто більше розгадав.</li>'
    + '<li>Лише акаунти: гості й аі-агенти в Січі не рахуються.</li></ul></details>';

  function paint() {
    if (!host || !host.isConnected || !data) return;
    host.innerHTML = '<div class="sich">' + head(data) + (data.live ? meCard(data) : '') + table(data) + noms(data)
      + (data.live ? duels(data) : '') + eternal(data) + RULES + '</div>';
    host.querySelectorAll('[data-week]').forEach((b) => b.onclick = () => {
      const w = b.dataset.week;
      if (window.HGames && location.hash.indexOf('#games/x:sich') === 0) history.replaceState(null, '', '#games/x:sich' + (w ? '/' + w : ''));
      week = w;
      load(true);
    });
    host.querySelectorAll('[data-solo]').forEach((b) => b.onclick = () => HGames.openSolo(b.dataset.solo));
  }

  const panel = {
    id: 'sich',
    title: 'Січ щоденних',
    icon: '🏆',
    mount(h, c) {
      host = h;
      ctx = c;
      h.classList.add('sich-host');
      week = /^\d{4}-\d{2}-\d{2}$/.test(c.sub || '') ? c.sub : '';
      if (data && data.week && (week ? data.week === week : data.live)) paint();
      else h.innerHTML = '<div class="gempty">Рахую Січ…</div>';
      load(false);
    },
    route(sub) { week = /^\d{4}-\d{2}-\d{2}$/.test(sub || '') ? sub : ''; load(true); },
  };

  // 👑 Гетьмана тижня і звання для ніків по всьому сайту — раз на 10 хвилин (оновлюється раз на тиждень і раз на ніч)
  let marks = { crown: [], ranks: {} }, marksAt = 0;
  function loadMarks() {
    if (Date.now() - marksAt < 10 * 60000) return Promise.resolve(marks);
    marksAt = Date.now();
    return fetch('/api/games/sich/marks').then((r) => (r.ok ? r.json() : null)).then((j) => {
      if (j) marks = { crown: (j.crown || []).map((n) => String(n).toLowerCase()), ranks: j.ranks || {} };
      return marks;
    }).catch(() => marks);
  }
  loadMarks();
  setInterval(loadMarks, 10 * 60000 + 5000);

  window.HSich = {
    /// Чи носить нік корону Гетьмана тижня.
    crowned: (n) => marks.crown.includes(String(n || '').toLowerCase()),
    /// Звання Січі: { icon, title, elo } або null (ще не грав щоденних).
    rankOf(n) {
      const key = Object.keys(marks.ranks).find((k) => same(k, n));
      return key ? marks.ranks[key] : null;
    },
    loadMarks,
    /// Свіжі цифри для відкритої панелі (зіграв щоденну й повернувся).
    refresh() { if (host && host.isConnected) load(true); },
  };

  if (window.HGames) HGames.registerPanel(panel);
})();
