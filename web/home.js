// 🏠 Головна — «Зараз у Глечиках» (#home і порожня адреса): що в ефірі, хто тут і чим зайнятий, де грають, що з
// щоденними, останні живі репліки й заголовок вісника. Раніше сайт одразу відкривав «Ефір»: новенький не розумів,
// куди потрапив, а свій, щоб побачити, де зараз грають і хто тут, обходив три розділи.
// Нових даних сторінка не просить: стан ефіру й «хто на сайті» дає app.js (o.state()), столи — HGames.rooms,
// щоденні й вісник — ті самі /api/games/daily і /api/stats/gazette, що малюють лобі та «Хто скільки».
(function () {
  'use strict';
  let o = null, esc = (s) => String(s == null ? '' : s);
  let shown = false, root = null;
  let daily = null, paper = null, loadedAt = 0;
  const sig = { air: '', people: '', tables: '', daily: '', chat: '', paper: '', hero: '', lead: '' };

  const G = () => window.HGames, P = () => window.HPeople;
  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const plural = (n, one, few, many) => {
    const d = n % 10, h = n % 100;
    return n + ' ' + (d === 1 && h !== 11 ? one : d >= 2 && d <= 4 && (h < 12 || h > 14) ? few : many);
  };
  const el = (id) => root && root.querySelector('[data-hm="' + id + '"]');
  /// Перемалювати шматок лише коли його розмітка справді змінилась: стан ефіру приходить щосекунди-дві.
  function put(id, html) {
    if (sig[id] === html) return false;
    sig[id] = html;
    const box = el(id);
    if (box) box.innerHTML = html;
    return true;
  }

  // ---------------------------------------------------------------------------------------------
  // шматки
  // ---------------------------------------------------------------------------------------------

  /// Гостю — хто ми й що тут робити: одна велика кнопка «Слухати» й «Назватись».
  function heroHtml() {
    if (o.me.nick) return '';
    return '<section class="hm-hero"><div class="hm-hero-t"><h1>Радіо, яке ставимо самі</h1>'
      + '<p>Одна черга на всіх: закидаєш пісню — її чують усі, хто тут. Поруч балачки й столи з іграми на компанію. Реєстрації немає — назвись і заходь.</p>'
      + '<div class="hm-hero-b"><button type="button" class="primary hm-listen" data-act="listen">▶ Слухати зараз</button>'
      + '<button type="button" class="tonal" data-act="nick">Назватись</button></div></div>'
      + '<div class="hm-glek"><img src="/static/glek.svg" alt=""><div class="hm-say">Здоров! Закинь пісню — поставлю в чергу.</div></div></section>';
  }

  /// «Троє тут, за одним столом грають, в ефірі Lynyrd Skynyrd» — усе живе одним реченням.
  function leadText(st) {
    const online = (st.online || []).length;
    const playing = rooms().filter((r) => r.status === 'playing').length;
    const waiting = rooms().filter((r) => r.status === 'lobby').length;
    const bits = [];
    bits.push(online ? (online === 1 ? 'Одна душа тут' : plural(online, 'душа', 'душі', 'душ') + ' тут') : 'Зараз тут тихо');
    if (playing) bits.push('за ' + plural(playing, 'столом', 'столами', 'столами') + ' грають');
    else if (waiting) bits.push(plural(waiting, 'стіл чекає', 'столи чекають', 'столів чекають') + ' гравців');
    const n = st.now;
    if (n && n.track && o.onAir(n)) bits.push('в ефірі ' + (n.track.artist || n.track.title));
    return bits.join(', ') + '.';
  }

  function airHtml(st) {
    const n = st.now || {};
    const live = n.track && o.onAir(n);
    const t = n.track || {};
    const likes = (n.likers || []).length;
    const liked = (n.likers || []).some((x) => same(x, o.me.nick));
    const who = (st.listeningNicks || []);
    const by = !live ? '' : n.source === 'user' ? 'закинув ' + esc(n.requestedBy || 'хтось') : 'ставить ' + esc(o.dj());
    const hear = who.length ? 'слухають ' + who.slice(0, 3).map(esc).join(', ') + (who.length > 3 ? ' і ще ' + (who.length - 3) : '') : '';
    const pos = live ? o.trackPos() : { elapsed: 0, pct: 0 };
    return '<a class="hm-cover" href="#efir" title="В Ефір: черга, поради, закинути пісню">'
      + (live ? o.cover(t) : '<img class="dj" src="/static/glek.svg" alt="">') + '</a>'
      + '<div class="hm-air-t"><a class="hm-title" href="#efir">' + esc(live ? t.title : 'Тиша') + '</a>'
      + '<div class="hm-artist">' + esc(live ? t.artist : o.dj() + ' думає, що б його врубити…') + '</div>'
      + ([by, hear].filter(Boolean).length ? '<div class="hm-by">' + [by, hear].filter(Boolean).join(' · ') + '</div>' : '')
      + '<div class="hm-ctl"><button type="button" class="bigplay"></button>'
      + (live ? '<div class="hm-prog"><div class="progress' + (n.skipPending ? ' pending' : '') + '"><div data-hm="bar" style="width:' + pos.pct.toFixed(2) + '%"></div></div>'
          + '<span class="hm-time"><span data-hm="el">' + o.fmt(pos.elapsed) + '</span> / ' + o.fmt(n.durationSec) + '</span></div>'
          + '<button type="button" data-act="like" class="' + (liked ? 'active' : '') + '" title="Вподобати">❤ ' + likes + '</button>'
          + '<button type="button" data-act="skip"' + (n.skipPending ? ' disabled' : '') + ' title="Перемкнути на наступний трек">⏭</button>'
        : '<div class="hm-prog"></div>')
      + '</div></div>';
  }

  /// Хто на сайті: спершу ті, хто за столом (до них можна підсісти), далі слухачі, далі решта.
  function peopleHtml(st) {
    const g = G();
    const listens = (n) => (st.listeningNicks || []).some((x) => same(x, n));
    const rows = (st.online || []).map((nick) => {
      const room = g && g.roomOf ? g.roomOf(nick) : null;
      const solo = g && g.soloOf ? g.soloOf(nick) : null;
      const what = room ? 'за столом «' + esc(g.titleOf(room.game)) + '»' + (room.state ? ' · ' + esc(room.state) : '')
        : solo ? 'грає в «' + esc(g.titleOf(solo)) + '»'
          : listens(nick) ? 'слухає ефір' : 'тусить на сайті';
      const rank = room ? 0 : solo ? 1 : listens(nick) ? 2 : 3;
      const btn = room && room.canSit && !same(nick, o.me.nick)
        ? '<button type="button" class="tonal" data-act="sit" data-room="' + esc(room.id) + '">Підсісти</button>' : '';
      return { rank, html: '<div class="hm-row"><button type="button" class="hm-who" data-who="' + esc(nick) + '">'
        + (P() ? P().ava(nick, 'ava') : '') + '<span><b>' + esc(nick) + '</b><small>' + what + '</small></span></button>' + btn + '</div>' };
    }).sort((a, b) => a.rank - b.rank);
    const n = rows.length;
    return '<h2>Хто тут' + (n ? ' <span class="count">' + n + '</span>' : '') + '</h2>'
      + (n ? '<div class="hm-list">' + rows.slice(0, 7).map((r) => r.html).join('') + '</div>'
          + (n > 7 ? '<div class="hm-more muted small">і ще ' + (n - 7) + ' — усі в Балачках</div>' : '')
        : '<div class="empty glek">Ні душі. Заходь першим — решта підтягнеться на музику.</div>');
  }

  const rooms = () => { const g = G(); return ((g && g.rooms) || []).filter((r) => r.maxPlayers > 1 && r.status !== 'finished'); };
  function tablesHtml() {
    const g = G();
    if (o.me.games === false || !g) return '';
    const list = rooms().map((r) => ({ r, l: g.roomLink(r.id) })).filter((x) => x.l)
      .sort((a, b) => (b.l.canSit - a.l.canSit) || (b.l.mine - a.l.mine));
    const rows = list.slice(0, 4).map(({ r, l }) => '<div class="hm-row"><span class="hm-gic">' + (g.artIcon ? g.artIcon(r.game) : l.icon) + '</span>'
      + '<span class="hm-rt"><b>' + esc(g.titleOf(r.game)) + '</b><small>' + esc(l.who) + '</small></span>'
      + '<button type="button" class="' + (l.canSit ? 'tonal' : 'ghost') + '" data-act="' + (l.canSit ? 'sit' : 'open') + '" data-room="' + esc(r.id) + '">' + esc(l.label) + '</button></div>').join('');
    return '<h2>За столами' + (list.length ? ' <span class="count">' + list.length + '</span>' : '') + '</h2>'
      + (rows ? '<div class="hm-list">' + rows + '</div>' : '<div class="empty">Столи порожні. Відкрий свій — підсядуть.</div>')
      + '<a class="hm-all" href="#games">усі ігри →</a>';
  }

  function dailyHtml() {
    if (o.me.games === false) return '';
    const all = (daily && daily.puzzles) || [];
    if (!daily) return '<h2>Сьогодні</h2><div class="empty"><span class="spin"></span> рахую щоденні…</div>';
    if (!all.length) return '';
    const done = all.filter((p) => p.me).length;
    const left = daily.money && daily.money.left > 0 ? 'ще можна близько ' + daily.money.left + ' 🏺' : done >= all.length ? 'усе зіграно — до завтра' : '';
    const next = all.find((p) => !p.me);
    const g = G();
    return '<h2>Сьогодні' + (daily.no ? ' <span class="muted small">щоденний глек № ' + daily.no + '</span>' : '') + '</h2>'
      + '<div class="hm-row"><span class="hm-rt"><b>' + (o.me.nick ? 'Розгадано ' + done + ' з ' + all.length : plural(all.length, 'щоденна загадка', 'щоденні загадки', 'щоденних загадок')) + '</b>'
      + (left ? '<small>' + esc(left) + '</small>' : '') + '</span>'
      + '<button type="button" class="tonal" data-act="daily">' + (done && done < all.length ? 'Далі' : done ? 'Глянути' : 'Грати') + '</button></div>'
      + '<div class="hm-dots">' + all.map((p) => '<i class="' + (p.me ? (p.me.solved ? 'on' : 'miss') : '') + '"></i>').join('') + '</div>'
      + (next && g ? '<div class="hm-next muted small">далі на черзі: ' + esc(String(next.title || g.titleOf(next.game)).replace(/\s+дня$/i, '')) + '</div>' : '');
  }

  function chatHtml() {
    const tail = o.chatTail();
    const rows = tail.map((m) => '<div class="hm-msg"><b class="who-n' + (P() ? P().nickCls(m.nick) : '') + '" style="--h:' + (P() ? P().hue(m.nick) : 200) + '">'
      + esc(m.nick) + '</b>' + esc(String(m.text || '').slice(0, 140)) + '</div>').join('');
    return '<h2>Балачки</h2>' + (rows || '<div class="empty">Тихо. Тяпни щось першим.</div>')
      + '<button type="button" class="hm-say-btn" data-act="chat">Тяпнути щось…</button>';
  }

  /// Заголовок вісника: частини — рядки й { n: нік }; тут — просто текст, без карток.
  const parts = (list) => (list || []).map((x) => (typeof x === 'string' ? esc(x) : '<b>' + esc(x.n) + '</b>')).join('');
  function paperHtml() {
    const lead = paper && paper.lead;
    const ver = window.HHistory && HHistory.version;
    return '<a class="hm-paper" href="#stats/overview"><b>📰 Глечицький вісник</b><span>'
      + (lead ? parts(lead.parts) : 'свіжий випуск складається, щойно щось станеться') + '</span></a>'
      + (ver ? '<a class="hm-ver" href="#history">що нового: ' + esc(ver) + '</a>' : '');
  }

  // ---------------------------------------------------------------------------------------------
  // малювання
  // ---------------------------------------------------------------------------------------------

  function mount() {
    root = document.getElementById('home');
    if (!root || root.dataset.ready) return;
    root.dataset.ready = '1';
    root.innerHTML = '<div data-hm="hero"></div>'
      + '<div class="hm-top"><h1 data-hm="h1">Зараз у Глечиках</h1><p class="hm-lead" data-hm="lead"></p></div>'
      + '<div class="hm-g2"><section class="panel hm-air" data-hm="air"></section><section class="panel hm-people" data-hm="people"></section></div>'
      + '<div class="hm-g3"><section class="panel" data-hm="tables"></section><section class="panel" data-hm="daily"></section><section class="panel hm-chat" data-hm="chat"></section></div>'
      + '<div class="hm-strip" data-hm="paper"></div>';
    root.addEventListener('click', onClick);
  }

  function onClick(e) {
    const b = e.target.closest('[data-act]');
    if (!b) return;
    const act = b.dataset.act, g = G();
    if (act === 'listen') o.listen();
    else if (act === 'nick') o.askNick(true);
    else if (act === 'like') o.like();
    else if (act === 'skip') o.skip(b);
    else if (act === 'sit' && g) { if (!o.me.nick) o.askNick(true); else g.sitAt(b.dataset.room); }
    else if (act === 'open' && g) g.openAt(b.dataset.room);
    else if (act === 'daily') o.go('#games');
    else if (act === 'chat') o.openChat();
  }

  function paint() {
    if (!shown || !root) return;
    const st = o.state();
    if (!st) return;
    if (put('hero', heroHtml())) root.classList.toggle('guest', !o.me.nick);
    put('lead', esc(leadText(st)));
    if (put('air', airHtml(st))) o.paintPlay();
    put('people', peopleHtml(st));
    for (const [id, html] of [['tables', tablesHtml()], ['daily', dailyHtml()]]) {
      put(id, html);
      const box = el(id);
      if (box) box.hidden = !html;
    }
    put('chat', chatHtml());
    put('paper', paperHtml());
  }

  /// Смужка й час треку — раз на секунду, без перемальовування картки (app.js кличе з tick()).
  function tick() {
    if (!shown || !root) return;
    const st = o.state();
    if (!st || !st.now || !st.now.track) return;
    const pos = o.trackPos();
    const bar = el('bar'), t = el('el');
    if (bar) bar.style.width = pos.pct.toFixed(2) + '%';
    if (t) { const s = o.fmt(pos.elapsed); if (t.textContent !== s) t.textContent = s; }
  }

  /// Щоденні й вісник — із сервера, не частіше ніж раз на хвилину; решта — з того, що й так уже є на сторінці.
  function load(force) {
    if (!force && Date.now() - loadedAt < 60000) return;
    loadedAt = Date.now();
    if (o.me.games !== false) o.api('GET', '/api/games/daily').then((d) => { daily = d; paint(); }).catch(() => { daily = daily || { puzzles: [] }; paint(); });
    o.api('GET', '/api/stats/gazette').then((d) => { paper = d; paint(); }).catch(() => { /* без вісника сторінка теж ціла */ });
  }

  window.HHome = {
    init(opts) { o = opts; if (o.esc) esc = o.esc; },
    show() {
      mount();
      shown = true;
      // Каталог ігор (назви й значки) може ще їхати: домалюємо, коли приїде.
      const g = G();
      if (g && g.ready) g.ready().then(paint).catch(() => {});
      load(false);
      paint();
    },
    hide() { shown = false; },
    /// Щось змінилось: стан ефіру, столи, балачки — app.js кличе з render() і з подій хаба.
    update() { paint(); },
    tick,
  };
})();
