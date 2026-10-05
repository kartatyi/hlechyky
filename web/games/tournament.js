/*
  Турнір на вечір — панель «Турнір» у розділі «Ігри». Правила й столи — на сервері (Games/Tournament.cs); тут лише
  показ і кнопки.

  Стан приходить подією 'tournament' (app.js віддає його сюди через HTournament.update):
    { active, id, host, stage: 'gathering'|'playing'|'between'|'done', index,
      games: [{ id, title }], players: [nick], online: [nick],
      standings: [{ nick, points }], results: [{ game, title, skipped, places: [{ nick, place, points, score }] }],
      room: null | { id, status, seats: [nick] }, prev: id столу щойно дограної гри | null,
      nextIn: мс до наступного столу (відлік у перерві) | null, nextOf: повний відлік, мс,
      held: господар поставив перерву на паузу, note: чому наступна гра сама не почалась | null,
      champions: [nick], crown: [nick] }
  Хаб: TournamentCreate(games[]), TournamentEdit(games[] — увесь список разом із зіграним), TournamentJoin(),
       TournamentLeave(), TournamentNext() (і «Зараз» під час відліку), TournamentPause(), TournamentSkip(),
       TournamentCancel() — кожен повертає текст відмови або null.

  Відлік перерви видно і тут, і просто на столі щойно дограної гри: core.js питає HTournament.roomBar(id) і
  ставить смужку замість «Ану ще раз» — нова партія за тим самим столом посадила б усіх знову, і турнір не зміг
  би поставити наступну гру.
*/
(() => {
  const SUGGESTED = ['pictionary', 'melody', 'telephone'];
  const MEDALS = ['🥇', '🥈', '🥉'];
  const MAX = 6;

  let state = null;
  let until = 0;               // коли (за годинником браузера) сервер поставить наступний стіл; 0 — відліку нема
  let picked = null;           // вибір ігор у формі «зібрати турнір» — живе між перемальовуваннями
  let editing = null;          // господар править ігри: { id турніру, picked — ще не зіграні }
  let composing = false;       // після дограного турніру показуємо підсумок, поки не натиснуть «Ану ще турнір»
  let invoke = () => Promise.reject(new Error('нема зв\'язку'));

  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const isLead = (s, nick) => {
    const mine = (s.players || []).some((p) => same(p, nick));
    return same(s.host, nick) || (mine && !(s.online || []).some((n) => same(n, s.host)));
  };

  /// Каталог ігор. Панель може відкритись раніше, ніж «Ігри» його завантажили (пряме посилання, F5) — тоді беремо самі.
  let ownCatalog = null;
  let fetching = null;
  function eligible(catalog, repaint) {
    const games = (catalog && catalog.games && catalog.games.length ? catalog : ownCatalog || {}).games || [];
    if (!games.length && !fetching) {
      fetching = fetch('/api/games/catalog').then((r) => r.json()).then((c) => {
        ownCatalog = c;
        if (picked && !picked.length) picked = null;   // підказаний набір — щойно стало з чого підказувати
        repaint();
      }).catch(() => { fetching = null; });
    }
    return games.filter((g) => g.maxPlayers >= 2 && !g.private);
  }

  function run(ctx, method, ...args) {
    return invoke(method, ...args)
      .then((err) => { if (err) ctx.toast(err, 'err'); return !err; })
      .catch((e) => { ctx.toast('Ой-йой, не вийшло: ' + e.message, 'err'); return false; });
  }

  function standings(ctx, s) {
    if (!s.standings || !s.standings.length) return '';
    return '<div class="trtable">' + s.standings.map((r, i) => {
      const place = 1 + s.standings.filter((x) => x.points > r.points).length;
      return '<div class="trrow' + (same(r.nick, ctx.me.nick) ? ' me' : '') + '">'
        + '<span class="trplace">' + (s.stage === 'done' && place <= 3 ? MEDALS[place - 1] : place + '.') + '</span>'
        + '<span class="trnick">' + ctx.esc(r.nick) + ((s.online || []).some((n) => same(n, r.nick)) ? '' : ' <i class="muted small">не на сайті</i>') + '</span>'
        + '<b>' + r.points + '</b></div>';
    }).join('') + '</div>';
  }

  function results(ctx, s) {
    if (!s.results || !s.results.length) return '';
    return '<div class="trresults">' + s.results.slice().reverse().map((r) => '<div class="trres"><b>' + ctx.esc(r.title) + '</b> '
      + (r.skipped ? '<span class="muted small">пропущено</span>'
        : r.places.map((p) => '<span class="chip">' + p.place + '. ' + ctx.esc(p.nick) + ' <b>+' + p.points + '</b></span>').join(' '))
      + '</div>').join('') + '</div>';
  }

  function gamesLine(ctx, s) {
    return '<ol class="trgames">' + s.games.map((g, i) => '<li class="'
      + (i < s.index ? 'past' : i === s.index && s.stage !== 'done' ? 'now' : '') + '">' + ctx.esc(g.title) + '</li>').join('') + '</ol>';
  }

  function crownLine(ctx, s) {
    const crown = (s && s.crown) || [];
    return crown.length ? '<div class="trcrown">👑 Чинний чемпіон: <b>' + crown.map(ctx.esc).join(', ') + '</b></div>' : '';
  }

  /// Вибір ігор кнопками, номер — порядок гри. Той самий і при зборі, і при правці.
  function pickerHtml(ctx, games, list, offset) {
    return '<div class="trpick">' + games.map((g) => {
      const n = list.indexOf(g.id);
      return '<button type="button" class="gpick' + (n >= 0 ? ' on' : '') + '" data-g="' + ctx.esc(g.id) + '">'
        + (n >= 0 ? '<b>' + (offset + n + 1) + '</b> ' : '') + ctx.esc(g.title) + ' <span class="muted small">' + g.minPlayers + '–' + g.maxPlayers + '</span></button>';
    }).join('') + '</div>';
  }

  function bindPicker(host, list, room, repaint) {
    host.querySelectorAll('[data-g]').forEach((b) => b.onclick = () => {
      const id = b.dataset.g;
      const i = list.indexOf(id);
      if (i >= 0) list.splice(i, 1); else if (list.length < room) list.push(id);
      repaint();
    });
  }

  function form(host, ctx) {
    const games = eligible(ctx.catalog, () => { if (host.isConnected) form(host, ctx); });
    if (!picked && games.length) picked = SUGGESTED.filter((id) => games.some((g) => g.id === id));
    if (!picked) picked = [];
    host.innerHTML = '<section class="trbox">'
      + '<h3>🏆 Турнір на вечір</h3>'
      + '<p class="muted">Кілька ігор поспіль: за кожну — очки за місце (утрьох це 3, 2 і 1), хто набрав більше — чемпіон і 👑 біля ніка до наступного турніру. Столи ставить сайт сам — лишається грати.</p>'
      + crownLine(ctx, state)
      + '<div class="muted small">Обери 2–6 ігор у тому порядку, у якому гратимете:</div>'
      + pickerHtml(ctx, games, picked, 0)
      + '<div class="tract"><button type="button" class="primary" data-do="create"' + (picked.length < 2 ? ' disabled' : '') + '>Зібрати турнір</button>'
      + (state && state.stage === 'done' ? '<button type="button" class="ghost" data-do="back">Назад до підсумків</button>' : '') + '</div>'
      + '</section>';
    bindPicker(host, picked, MAX, () => form(host, ctx));
    const create = host.querySelector('[data-do="create"]');
    create.onclick = () => ctx.busy(create, 'збираю…', async () => { if (await run(ctx, 'TournamentCreate', picked)) composing = false; });
    const back = host.querySelector('[data-do="back"]');
    if (back) back.onclick = () => { composing = false; paint(host, ctx); };
  }

  /// Правка ігор (записка #20): зіграні лишаються як є, решту — той самий вибір, що й при зборі.
  function editor(host, ctx, s) {
    const games = eligible(ctx.catalog, () => { if (host.isConnected) paint(host, ctx); });
    const played = s.games.slice(0, s.index);
    const list = editing.picked;
    const need = Math.max(1, 2 - played.length);
    host.innerHTML = '<section class="trbox">'
      + '<h3>✏️ Ігри турніру</h3>'
      + (played.length ? '<div class="muted small">Уже зіграно — це лишається:</div><ol class="trgames">'
        + played.map((g) => '<li class="past">' + ctx.esc(g.title) + '</li>').join('') + '</ol>' : '')
      + '<div class="muted small">' + (played.length ? 'Далі — обери' : 'Обери 2–6 ігор') + ' у тому порядку, у якому гратимете:</div>'
      + pickerHtml(ctx, games, list, played.length)
      + '<div class="tract"><button type="button" class="primary" data-do="save"' + (list.length < need ? ' disabled' : '') + '>Зберегти</button>'
      + '<button type="button" class="ghost" data-do="close">Скасувати</button></div>'
      + '</section>';
    bindPicker(host, list, MAX - played.length, () => paint(host, ctx));
    const save = host.querySelector('[data-do="save"]');
    save.onclick = () => ctx.busy(save, 'зберігаю…', async () => {
      if (await run(ctx, 'TournamentEdit', played.map((g) => g.id).concat(list))) { editing = null; paint(host, ctx); }
    });
    host.querySelector('[data-do="close"]').onclick = () => { editing = null; paint(host, ctx); };
  }

  /// Скільки секунд до наступного столу, як показувати.
  function leftText() {
    const left = Math.ceil((until - Date.now()) / 1000);
    return left > 0 ? String(left) : '0';
  }

  /// Що з наступною грою в перерві: відлік, пауза чи чому не вийшло. Спільне для панелі й смужки на столі.
  function breakLine(ctx, s, lead) {
    const next = s.games[s.index];
    const title = '<b>' + ctx.esc(next ? next.title : '') + '</b>';
    if (s.nextIn != null && until) return '<span class="trleft">Наступна гра — ' + title + ' через <b class="trsec" data-trleft>' + leftText() + '</b>…</span>';
    if (s.note) return '<span class="trnote">' + ctx.esc(s.note) + '</span>';
    if (s.held) return '<span>⏸ Пауза. Далі — ' + title + (lead ? '' : ', як ' + ctx.esc(s.host) + ' натисне «Далі»') + '</span>';
    return lead ? '' : '<span class="muted">Чекаємо, поки ' + ctx.esc(s.host) + ' запустить наступну гру</span>';
  }

  function paint(host, ctx) {
    const s = state;
    if (!s || !s.id || (s.stage === 'done' && composing)) { form(host, ctx); return; }
    const mine = (s.players || []).some((p) => same(p, ctx.me.nick));
    const lead = isLead(s, ctx.me.nick);
    if (editing && (editing.id !== s.id || !lead || (s.stage !== 'gathering' && s.stage !== 'between'))) editing = null;
    if (editing) { editor(host, ctx, s); return; }
    const next = s.games[s.index];
    const edit = lead ? '<button type="button" class="ghost" data-edit="1" title="Додати, прибрати чи переставити ще не зіграні ігри">✏️ Змінити ігри</button>' : '';
    let html = '<section class="trbox">';
    if (s.stage === 'gathering') {
      html += '<h3>🏆 Турнір збирається</h3>'
        + '<div class="muted small">Збирає ' + ctx.esc(s.host) + '. Ігри:</div>' + gamesLine(ctx, s)
        + '<div class="trplayers">' + s.players.map((p) => '<span class="chip' + ((s.online || []).some((n) => same(n, p)) ? ' on' : '') + '">' + ctx.esc(p) + '</span>').join('') + '</div>'
        + '<div class="tract">'
        + (mine ? '' : '<button type="button" class="primary" data-do="TournamentJoin">Я в турнірі</button>')
        + (lead ? '<button type="button" class="primary" data-do="TournamentNext">Почати: ' + ctx.esc(next.title) + ' ▸</button>' : '')
        + edit
        + (mine ? '<button type="button" class="ghost" data-do="TournamentLeave">Вийти</button>' : '')
        + (lead ? '<button type="button" class="ghost" data-do="TournamentSkip" title="Якщо в цю гру нинішній склад не влазить">Пропустити «' + ctx.esc(next.title) + '»</button>' : '')
        + (lead ? '<button type="button" class="ghost" data-do="TournamentCancel">Скасувати</button>' : '')
        + '</div>'
        + (lead ? '<div class="muted small">Коли всі зайшли — тисни «Почати»: сайт сам поставить стіл і посадить учасників, які зараз на сайті. Після кожної гри наступна почнеться сама за ' + Math.round((s.nextOf || 10000) / 1000) + ' с.</div>' : '');
    } else if (s.stage === 'playing') {
      html += '<h3>🏆 Гра ' + (s.index + 1) + ' з ' + s.games.length + ': ' + ctx.esc(next.title) + '</h3>'
        + gamesLine(ctx, s)
        + '<div class="tract">'
        + (s.room ? '<button type="button" class="primary" data-room="' + ctx.esc(s.room.id) + '">Гайда до столу ▸</button>' : '')
        + (lead ? '<button type="button" class="ghost" data-do="TournamentSkip" title="Якщо гра зависла чи всі розбіглись">Пропустити гру</button>' : '')
        + (mine ? '' : '<button type="button" class="ghost" data-do="TournamentJoin">Я теж (з наступної гри)</button>')
        + '</div>'
        + standings(ctx, s) + results(ctx, s);
    } else if (s.stage === 'between') {
      const counting = s.nextIn != null && until;
      html += '<h3>🏆 Після гри ' + s.index + ' з ' + s.games.length + '</h3>'
        + gamesLine(ctx, s)
        + '<div class="trbreak">' + breakLine(ctx, s, lead) + '</div>'
        + '<div class="tract">'
        + (lead ? '<button type="button" class="primary" data-do="TournamentNext">' + (counting ? 'Зараз ▸' : 'Гайда далі: ' + ctx.esc(next.title) + ' ▸') + '</button>' : '')
        + (lead && counting ? '<button type="button" class="ghost" data-do="TournamentPause" title="Зупинити відлік — далі вручну, коли всі будуть готові">⏸ Пауза</button>' : '')
        + edit
        + (mine ? '' : '<button type="button" class="ghost" data-do="TournamentJoin">Я теж</button>')
        + (lead ? '<button type="button" class="ghost" data-do="TournamentSkip" title="Якщо в цю гру нинішній склад не влазить">Пропустити «' + ctx.esc(next.title) + '»</button>' : '')
        + (lead ? '<button type="button" class="ghost" data-do="TournamentCancel">Завершити зараз</button>' : '')
        + '</div>'
        + standings(ctx, s) + results(ctx, s);
    } else {
      const champ = s.champions || [];
      html += '<h3>🏆 Турнір закінчено</h3>'
        + (champ.length ? '<div class="trchamp">👑 ' + champ.map(ctx.esc).join(' і ') + '</div>' : '<div class="muted">Без чемпіона</div>')
        + standings(ctx, s) + results(ctx, s)
        + '<div class="tract"><button type="button" class="primary" data-do="new">Ану ще турнір</button></div>';
    }
    html += '</section>';
    host.innerHTML = html;
    host.querySelectorAll('[data-do]').forEach((b) => b.onclick = () => {
      if (b.dataset.do === 'new') { composing = true; picked = null; paint(host, ctx); return; }
      if (b.dataset.do === 'TournamentCancel' && !confirm(s.results && s.results.length ? 'Завершити турнір зараз? Чемпіон — хто попереду.' : 'Скасувати турнір?')) return;
      ctx.busy(b, '…', () => run(ctx, b.dataset.do));
    });
    const e = host.querySelector('[data-edit]');
    if (e) e.onclick = () => {
      editing = { id: s.id, picked: s.games.slice(s.index).map((g) => g.id) };
      // Правка під час відліку: без паузи стіл поставився б просто з-під рук, а господаря перекинуло б за нього.
      if (s.stage === 'between' && s.nextIn != null) invoke('TournamentPause').catch(() => { /* не вийшло — правка однаково ляже або скаже чому */ });
      paint(host, ctx);
    };
    host.querySelectorAll('[data-room]').forEach((b) => b.onclick = () => { location.hash = '#games/room/' + encodeURIComponent(b.dataset.room); });
  }

  // ---------------------------------------------------------------------------------------------
  // Смужка на столі щойно дограної гри (core.js: btnsHtml) і секунди відліку
  // ---------------------------------------------------------------------------------------------

  let barCtx = null;

  function compactTable(ctx, s) {
    return (s.standings || []).slice(0, 4).map((r) => '<span class="chip' + (same(r.nick, ctx.me.nick) ? ' on' : '') + '">'
      + ctx.esc(r.nick) + ' <b>' + r.points + '</b></span>').join('');
  }

  /// Смужка для столу roomId: { html, hold } або null. hold — сховати «Ану ще раз» (перерва турніру).
  function roomBar(roomId, ctx) {
    const s = state;
    if (!s || !s.id || !s.prev || s.prev !== roomId || (s.stage !== 'between' && s.stage !== 'done')) return null;
    barCtx = ctx;
    const link = '<button type="button" class="ghost" data-trgo="1">Таблиця</button>';
    if (s.stage === 'done') {
      const champ = s.champions || [];
      return { hold: false, html: '<div class="trbar"><span class="trbarline">🏆 Турнір закінчено'
        + (champ.length ? ' — 👑 <b>' + champ.map(ctx.esc).join(' і ') + '</b>' : '') + '</span>'
        + '<span class="trbarbtns">' + link.replace('Таблиця', 'Підсумок ▸') + '</span></div>' };
    }
    const lead = isLead(s, ctx.me.nick);
    const counting = s.nextIn != null && until;
    return { hold: true, html: '<div class="trbar"><span class="trbarline">🏆 ' + (breakLine(ctx, s, lead) || 'Перерва турніру') + '</span>'
      + '<span class="trbarscore">' + compactTable(ctx, s) + '</span>'
      + '<span class="trbarbtns">'
      + (lead ? '<button type="button" class="primary" data-tr="TournamentNext">' + (counting ? 'Зараз ▸' : 'Далі ▸') + '</button>' : '')
      + (lead && counting ? '<button type="button" class="ghost" data-tr="TournamentPause">⏸ Пауза</button>' : '')
      + link + '</span></div>' };
  }

  /// Підпис смужки для core.js: картку столу перемальовуємо, лише коли змінилось те, що на ній видно.
  function barSig(roomId) {
    const s = state;
    if (!s || s.prev !== roomId) return '';
    return JSON.stringify([s.id, s.stage, s.index, s.nextIn != null && !!until, s.held, s.note, s.host, s.online, s.standings, s.champions]);
  }

  document.addEventListener('click', (e) => {
    const go = e.target.closest && e.target.closest('.trbar [data-trgo]');
    if (go) { location.hash = '#games/x:tournament'; return; }
    const b = e.target.closest && e.target.closest('.trbar [data-tr]');
    if (!b || !barCtx) return;
    barCtx.busy(b, '…', () => run(barCtx, b.dataset.tr));
  });

  // Секунди відліку тікають тут, а не перемальовуванням: щосекунди перебудовувати стіл чи панель — зайве.
  let ticker = 0;
  function tickLeft() {
    if (!until) { clearInterval(ticker); ticker = 0; return; }
    const els = document.querySelectorAll('[data-trleft]');
    const t = leftText();
    els.forEach((el) => { if (el.textContent !== t) el.textContent = t; });
  }

  /// Відкрита зараз панель. core.js не перемальовує вже відкриту панель на повторний registerPanel, тож свіжий стан
  /// малюємо самі — поки host у документі.
  let mounted = null;

  const panel = {
    id: 'tournament',
    title: 'Турнір',
    icon: '👑',
    mount(host, ctx) { mounted = { host, ctx }; paint(host, ctx); },
  };

  window.HTournament = {
    /// app.js дає свій conn.invoke — панель сама до хаба не ходить.
    connect(fn) { invoke = fn; },
    get state() { return state; },
    update(s) {
      state = s || null;
      // Скільки лишилось — від моменту, коли знімок прийшов: годинник браузера може бути кривим, а різниця — ні.
      until = state && state.stage === 'between' && state.nextIn != null ? Date.now() + state.nextIn : 0;
      if (state && state.stage !== 'done') composing = false;
      if (mounted && mounted.host.isConnected) paint(mounted.host, mounted.ctx);
      if (window.HGames && HGames.tournamentChanged) HGames.tournamentChanged();
      if (until && !ticker) ticker = setInterval(tickLeft, 250);
    },
    roomBar,
    barSig,
    /// Чи носить нік корону.
    crowned(nick) { return !!state && (state.crown || []).some((n) => same(n, nick)); },
  };

  if (window.HGames) HGames.registerPanel(panel);
})();
