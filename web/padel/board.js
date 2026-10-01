'use strict';
// Табло Падельні: живі й недавні матчі, «+ Новий матч», саме табло (3 теми, повний екран, клавіші й пад, хвиля матчу,
// бульбашка Глека, голос на цьому пристрої, годинник оренди, статистика після кінця). Рахунок, подачу, бік, підказки
// й тексти дій рахує сервер (PadelScore) — тут лише показуємо його вид: так два телефони біля корту не розійдуться.
(function () {
  const P = window.Padel, esc = P.esc;
  const pickOne = (a) => a[Math.floor(Math.random() * a.length)];
  const THEMES = [['glek', 'Глечики'], ['court', 'Корт'], ['led', 'LED']];
  const SIDE = { right: 'подає справа', left: 'подає зліва', choice: 'бік обирають приймаючі' };
  // Жарти Глека з макета — на події кроку (гейм із брейком, сет, матч, вирішальне, тайбрейк, серія), не на кожне очко
  const GL = {
    decider: ['Золоте очко! Хто зараз злякається — той миє глеки.', 'Одне очко на все. Дихайте, пательні.', 'Приймаючі, обирайте бік — і не кажіть потім, що сонце заважало.'],
    brk: ['Брейк! Подача — це не гарантія, а пропозиція.', 'Ого, взяли чужу подачу. Глек аплодує ручками.', 'Брейк! Хтось на подачі замріявся про шаурму.'],
    set: ['Сет! Глек наливає компоту переможцям.', 'Сет закрито. Переможені — по воду.', 'Сет! Записую в літопис Глечиків.'],
    match: ['Матч! {w} — сьогодні ви королі пательні.', 'Кінець! {l}, реванш у суботу — Глек уже записав.', 'Матч! {w}, з вас історія для балачок.'],
    streak: ['{w}: {n} очок поспіль. Хтось їх зупиніть.', '{n} підряд! Це вже не падел, це конвеєр.'],
    bagel: ['{l}, на горизонті «бублик». Глек тримає за вас кулачки.'],
    tb: ['Тайбрейк. Нерви — на стіл.', 'Тайбрейк! Тепер кожне очко — як останнє.'],
    clock10: ['Десять хвилин оренди. Останній гейм — грайте так, ніби пательня гаряча.', 'Годинник цокає: ще гейм — і віддаємо корт.'],
    clock0: ['Оренда скінчилась. Глек уже чує, як адміністратор дзвенить ключами.', 'Час вийшов. Дограєте — лише якщо корт ніхто не чекає.'],
  };

  let host = null;
  let mode = 'list';          // list | board
  let curId = null, cur = null, seq = 0;
  let lists = { live: [], recent: [] };
  let tickT = 0, msgT = 0, statsFor = '';
  let gpRun = false;
  const gpPrev = {};

  const $ = (s) => host && host.querySelector(s);
  const visible = () => !!(P.current && P.current.tab && P.current.tab.id === 'board');
  const teamName = (m, t) => ((m.teams && m.teams[t]) || []).map((p) => p.name).join(' і ');
  const slotName = (m, k) => { const p = m.teams[k[0] === 'A' ? 0 : 1][+k[1]]; return p ? p.name : '?'; };
  const canCtl = (m) => !!m && (P.me.admin || (!!P.me.pid && (m.ctl || []).includes(P.me.pid)));
  const fill = (m, s, w, n) => s.replace('{w}', teamName(m, w)).replace('{l}', teamName(m, 1 - w)).replace('{n}', n == null ? '' : n);

  // ------------------------------------------------------------------ список

  async function loadLists() {
    try {
      const [l, r] = await Promise.all([P.api('matches?live=1'), P.api('matches?recent=12')]);
      lists = { live: l.matches || [], recent: r.matches || [] };
    } catch { /* тост уже був */ }
    P.badge('board', lists.live.length || '');
    if (mode === 'list' && host) renderList();
  }

  function cardHtml(m) {
    const over = m.status !== 'live', s = m.state || {};
    const tm = (t) => '<div class="bd-tm' + (over && s.winner === t ? ' w' : '') + '"><span class="tdot ' + (t ? 'b' : 'a') + '"></span><span>'
      + esc(teamName(m, t)) + '</span>' + (over && s.winner === t ? ' 🏆' : '') + '</div>';
    const meta = m.status === 'live'
      ? '<span class="bd-dot"></span>наживо' + (m.startedAt ? ' · ' + P.span(Date.now() - new Date(m.startedAt).getTime()) : ' · ще не почали')
      : m.status === 'abandoned' ? 'скасовано' : (s.winner < 0 ? 'нічия' : 'дограли') + (m.endedAt ? ' · ' + P.when(m.endedAt) : '');
    return '<button type="button" class="bd-card' + (over ? '' : ' live') + '" data-id="' + esc(m.id) + '">' + tm(0) + tm(1)
      + '<div class="bd-sc">' + esc(m.score || '0:0') + '</div><div class="bd-meta">' + meta
      + (m.tour ? ' · 🏆 турнір, корт ' + m.tour.court : '') + (m.by ? ' · веде ' + esc(m.by) : '') + '</div></button>';
  }

  function renderList() {
    const { live, recent } = lists;
    host.innerHTML = '<div class="bd-head"><h2>🎾 Табло</h2><button class="btn pri" data-new>+ Новий матч</button></div>'
      + '<div class="bd-sec"><h3>Зараз на корті</h3>' + (live.length ? '<div class="bd-list">' + live.map(cardHtml).join('') + '</div>'
        : '<div class="card empty"><span class="e">🍳</span>На корті тихо. Глек гріє пательню — заводь матч, а рахунок він покаже великими цифрами.</div>')
      + '</div>'
      + (recent.length ? '<div class="bd-sec"><h3>Нещодавно</h3><div class="bd-list">' + recent.map(cardHtml).join('') + '</div></div>' : '');
  }

  // ------------------------------------------------------------------ новий матч

  function seg(key, opts, val) {
    return '<div class="seg" data-k="' + key + '">' + opts.map(([v, l]) => '<button type="button" data-v="' + esc(v) + '" class="'
      + (String(v) === String(val) ? 'on' : '') + '">' + esc(l) + '</button>').join('') + '</div>';
  }
  /// «19:30» → ISO; час, що вже давно минув, — це завтра (оренда за північ)
  function untilIso(hhmm) {
    if (!hhmm) return null;
    const [h, mi] = hhmm.split(':').map(Number);
    const d = new Date(); d.setHours(h, mi, 0, 0);
    if (d.getTime() < Date.now() - 6 * 3600e3) d.setDate(d.getDate() + 1);
    return d.toISOString();
  }
  const hhmm = (iso) => iso ? new Intl.DateTimeFormat('uk-UA', { timeZone: 'Europe/Kyiv', hour: '2-digit', minute: '2-digit' }).format(new Date(iso)) : '';

  async function openNew(gid) {
    if (!P.me.account && !P.me.admin) { P.toast('Вести рахунок можуть лише акаунти — увійди на головній'); return; }
    const saved = P.pref('rules') || {};
    const R = { mode: saved.mode || 'match', deuce: saved.deuce || 'adv', sets: saved.sets || '1', setTo: saved.setTo || 6, total: saved.total === undefined ? 24 : saved.total };
    let gath = [], g = null, pre = [], first = 'A0';
    try { gath = (await P.api('gatherings')).upcoming || []; } catch { /* без зборів — теж можна */ }
    if (gid) {
      try { g = await P.api('gatherings/' + encodeURIComponent(gid)); } catch { g = null; }
      if (g) { pre = (g.going || []).map((x) => x.pid).slice(0, 4); if (!gath.some((x) => x.id === g.id)) gath.unshift(g); }
    } else {
      // найближчий збір, якщо він сьогодні-зараз — матч, найпевніше, з нього
      const n = gath[0];
      if (n && new Date(n.start).getTime() - Date.now() < 6 * 3600e3) g = n;
    }
    const gOpt = (x) => '<option value="' + esc(x.id) + '"' + (g && g.id === x.id ? ' selected' : '') + '>' + esc(P.when(x.start) + ' · ' + x.place) + '</option>';
    const sh = P.sheet('🎾 Новий матч', '<div class="bd-new">'
      + '<div class="field"><label>Хто грає</label><div id="bnPick"></div></div>'
      + '<div class="bd-teams" id="bnTeams"></div>'
      + '<div class="field" id="bnFirstF"><label>Хто подає першим</label><div id="bnFirst"></div></div>'
      + '<div class="field"><label>Режим</label>' + seg('mode', [['match', 'Матч 15‑30‑40'], ['points', 'Очки (американо)']], R.mode) + '</div>'
      + '<div class="bd-rules">'
      + '<div class="field m-only"><label>При 40:40</label>' + seg('deuce', [['adv', 'Більше‑менше'], ['golden', 'Золоте очко'], ['star', 'Star point']], R.deuce) + '</div>'
      + '<div class="field m-only"><label>Сетів</label>' + seg('sets', [['1', '1'], ['3', '2 з 3'], ['3s', '2 з 3 + СТБ'], ['free', 'Скільки влізе']], R.sets) + '</div>'
      + '<div class="field m-only"><label>Сет до</label>' + seg('setTo', [[6, '6 геймів'], [4, '4 (швидкий)']], R.setTo) + '</div>'
      + '<div class="field p-only"><label>Гра до</label>' + seg('total', [[16, '16'], [21, '21'], [24, '24'], [32, '32'], ['', '⏱ на час']], R.total == null ? '' : R.total) + '</div>'
      + '</div>'
      + '<div class="bd-rules"><div class="field"><label>Збір</label><select class="inp" id="bnG"><option value="">без збору</option>' + gath.map(gOpt).join('') + '</select></div>'
      + '<div class="field"><label>Корт до</label><input class="inp" type="time" id="bnUntil"><span class="muted small" id="bnUntilH"></span></div></div>'
      + '<div class="bd-go"><button type="button" class="btn pri big" id="bnGo">Поїхали →</button></div></div>',
    () => { if (location.hash.startsWith('#board/new')) history.replaceState(null, '', '#board'); });
    const el = sh.el, q = (s) => el.querySelector(s);
    let pids = pre.slice();
    const vis = () => {
      for (const x of el.querySelectorAll('.m-only')) x.hidden = R.mode !== 'match';
      for (const x of el.querySelectorAll('.p-only')) x.hidden = R.mode !== 'points';
      const gs = gath.find((x) => x.id === q('#bnG').value);
      q('#bnUntilH').textContent = gs && !q('#bnUntil').value ? 'як у зборі — до ' + hhmm(gs.until) : '';
    };
    const teams = async () => {
      if (pids.some((p) => P.name(p) === '?')) await P.players.load(true);
      const nm = (i) => pids[i] ? esc(P.name(pids[i])) : '<span class="muted">…</span>';
      q('#bnTeams').innerHTML = '<span class="tdot a"></span>' + nm(0) + ' і ' + nm(1) + '<span class="vs">проти</span><span class="tdot b"></span>' + nm(2) + ' і ' + nm(3);
      const slots = ['A0', 'A1', 'B0', 'B1'], idx = { A0: 0, A1: 1, B0: 2, B1: 3 };
      q('#bnFirstF').hidden = pids.length < 4;
      if (pids.length === 4) {
        q('#bnFirst').innerHTML = '<div class="seg">' + slots.map((s) => '<button type="button" data-first="' + s + '" class="' + (s === first ? 'on' : '') + '">'
          + esc(P.name(pids[idx[s]])) + '</button>').join('') + '</div>';
      }
    };
    const pk = await P.pick(q('#bnPick'), { value: pids, max: 4, numbered: true, hint: 'Перші двоє — одна команда, наступні двоє — друга.', onChange: (v) => { pids = v; teams(); } });
    el.addEventListener('click', (e) => {
      const b = e.target.closest('.seg[data-k] button');
      if (b) {
        const k = b.parentElement.dataset.k, v = b.dataset.v;
        R[k] = k === 'setTo' ? +v : k === 'total' ? (v === '' ? null : +v) : v;
        for (const x of b.parentElement.children) x.classList.toggle('on', x === b);
        vis(); return;
      }
      const f = e.target.closest('[data-first]');
      if (f) { first = f.dataset.first; teams(); }
    });
    q('#bnG').addEventListener('change', vis);
    q('#bnUntil').addEventListener('input', vis);
    q('#bnGo').addEventListener('click', async () => {
      const v = pk.get();
      if (v.length !== 4) { P.toast('Треба четверо: перші двоє — одна команда'); return; }
      P.pref('rules', R);
      const rules = R.mode === 'points' ? { mode: 'points', total: R.total } : { mode: 'match', deuce: R.deuce, sets: R.sets, setTo: +R.setTo };
      const body = { teams: [[v[0], v[1]], [v[2], v[3]]], rules, first };
      if (q('#bnG').value) body.gathering = q('#bnG').value;
      const u = untilIso(q('#bnUntil').value);
      if (u) body.courtUntil = u;
      const go = q('#bnGo'); go.disabled = true;
      let r; try { r = await P.api('matches', body); } catch { go.disabled = false; return; }
      sh.close();
      P.go('board', r.match.id);
    });
    vis(); teams();
  }

  // ------------------------------------------------------------------ табло

  function shell() {
    const th = P.pref('theme') || 'glek';
    host.innerHTML = '<div class="bd-bar"><a class="btn sm ghost" href="#board">← Матчі</a><span class="grow bd-title" id="bdTitle"></span>'
      + '<span class="bd-watch" id="bdWatch" hidden>👀 дивишся</span>'
      + '<div class="seg" id="bdTheme">' + THEMES.map(([v, l]) => '<button type="button" data-theme="' + v + '" class="' + (v === th ? 'on' : '') + '">' + l + '</button>').join('') + '</div>'
      + '<button type="button" class="btn sm" data-act="voice" id="bdVoice"></button>'
      + '<button type="button" class="btn sm" data-act="fs" title="На весь екран — для планшета біля корту">⛶</button></div>'
      + '<div class="board theme-' + esc(th) + '" id="bdBoard">'
      + '<div class="b-top"><div id="bTab"></div><span class="b-status" id="bStatus"></span>'
      + '<span class="b-right"><span class="b-clock" id="bClock"></span><span class="b-time" id="bTime">0:00</span></span></div>'
      + '<div class="halves"><div class="half a" data-t="0"><div class="tn" id="tn0"></div><div class="pts" id="pts0">0</div><div class="gms" id="gm0"></div></div>'
      + '<div class="net"></div>'
      + '<div class="half b" data-t="1"><div class="tn" id="tn1"></div><div class="pts" id="pts1">0</div><div class="gms" id="gm1"></div></div></div>'
      + '<div class="b-msg" id="bMsg"></div><div class="b-bar" id="bBar"></div></div>'
      + '<div class="under"><div class="card glek"><div class="glek-av">🏺</div><div><b>Дядько Глек</b><div id="bdGlek" class="bd-glek muted"></div></div></div>'
      + '<div class="card"><div class="mom-h"><b>Хвиля матчу</b><span id="bdStreak" class="muted small"></span></div><div id="bdMom" class="mom-bars"></div></div></div>'
      + '<div id="bdAfter"></div>';
    paintVoice();
  }

  function paintVoice() {
    const b = $('#bdVoice'); if (!b) return;
    const on = !!P.pref('voice');
    b.textContent = on ? '🔊 Глек каже' : '🔇 Глек мовчить';
    b.title = on ? 'Рахунок уголос на цьому пристрої — тиць, щоб вимкнути' : 'Озвучувати рахунок на цьому пристрої (голосом Остапа)';
  }

  function paint(flash) {
    const m = cur, r = m.rules, s = m.state, pts = r.mode === 'points', log = m.log || [];
    const ctl = canCtl(m), board = $('#bdBoard');
    board.classList.toggle('ro', !ctl);
    $('#bdWatch').hidden = ctl;
    $('#bdTitle').innerHTML = m.tour
      ? '<a href="#tour/' + esc(m.tour.id) + '">🏆 Турнір · раунд ' + m.tour.round + ' · корт ' + m.tour.court + '</a>'
      : '<span class="muted">веде</span> ' + esc(m.by || '');
    let tab;
    if (pts) tab = '<div class="b-info">' + (r.total ? 'Гра до ' + r.total : 'Гра на час') + ' · подача — кожні 4 очки</div>';
    else {
      const open = !s.over;
      const head = s.sets.map((x, i) => '<th>' + (x.stb ? 'СТБ' : 'С' + (i + 1)) + '</th>').join('') + (open ? '<th>' + (s.superTb ? 'СТБ' : 'С' + (s.sets.length + 1)) + '</th>' : '');
      const row = (t) => '<tr><td class="tl"><span class="tdot ' + (t ? 'b' : 'a') + '"></span>' + esc(teamName(m, t)) + '</td>'
        + s.sets.map((x) => '<td>' + x.g[t] + (x.tb ? '<sup>' + x.tb[t] + '</sup>' : '') + '</td>').join('')
        + (open ? '<td class="cur">' + (s.superTb ? s.pts[t] : s.games[t]) + '</td>' : '') + '</tr>';
      tab = '<table class="b-tab"><tr><th></th>' + head + '</tr>' + row(0) + row(1) + '</table>';
    }
    $('#bTab').innerHTML = tab;
    let st = '', sc = '';
    if (m.status === 'abandoned') st = 'Скасовано';
    else if (s.over) st = s.winner < 0 ? 'Нічия' : '🏆 Кінець';
    else if (!log.length) st = ctl ? 'Хто подає? Тиць на ім’я' : 'Зараз почнуть';
    else if (m.hint) { st = m.hint.text; sc = m.hint.team === 0 ? 'ta' : m.hint.team === 1 ? 'tb' : ''; }
    else if (!pts && m.label.includes('AD')) { const t = m.label.indexOf('AD'); st = 'Перевага'; sc = t ? 'tb' : 'ta'; }
    else if (s.superTb) st = 'Супертайбрейк';
    else if (s.tb) st = 'Тайбрейк';
    const bs = $('#bStatus'); bs.textContent = st; bs.className = 'b-status ' + sc;
    const srv = m.server;
    for (const t of [0, 1]) {
      const ks = t ? ['B0', 'B1'] : ['A0', 'A1'];
      $('#tn' + t).innerHTML = ks.map((k) => {
        const on = !!srv && srv.slot === k;
        return '<span class="pn' + (on ? ' srv' : '') + '" data-slot="' + k + '"' + (ctl ? ' title="Тиць — цей подає"' : '') + '>'
          + (on ? '<i class="ball"></i>' : '') + '<span>' + esc(slotName(m, k)) + '</span></span>';
      }).join('') + (srv && srv.slot[0] === (t ? 'B' : 'A') ? '<span class="side-txt">' + (SIDE[srv.side] || '') + '</span>' : '');
      const el = $('#pts' + t), old = el.textContent;
      el.textContent = m.label[t];
      if (flash && old !== m.label[t]) { el.classList.remove('flash'); void el.offsetWidth; el.classList.add('flash'); }
      $('#gm' + t).innerHTML = pts
        ? '<span class="lbl">' + (r.total ? 'очок із ' + r.total : 'очок') + '</span>'
        : '<span class="lbl">гейми</span><b>' + s.games[t] + '</b><span class="dots">' + '●'.repeat(s.won[t] || 0) + '</span>';
      board.querySelector('.half.' + (t ? 'b' : 'a')).classList.toggle('won', s.over && s.winner === t);
    }
    const btn = (a, l, cls) => '<button type="button" class="btn sm' + (cls ? ' ' + cls : '') + '" data-act="' + a + '">' + l + '</button>';
    let bar = '';
    if (ctl && m.status === 'live') bar = btn('undo', '↶ Скасувати') + btn('finish', '🏁 Завершити') + btn('until', '⏳ Оренда') + btn('abandon', '✕ Скасувати матч', 'bad');
    else if (ctl && m.status === 'done') bar = btn('undo', '↶ Повернути матч') + (m.tour ? '' : btn('again', '↺ Ще раз'));
    $('#bBar').innerHTML = bar + '<button type="button" class="btn sm b-fs-only" data-act="fs">⛶ Вийти</button>';
    paintMom();
    clock();
    after();
  }

  function paintMom() {
    const log = cur.log || [], L = log.slice(-110);
    $('#bdMom').innerHTML = L.length ? L.map((t) => '<i class="' + (t ? 'b' : 'a') + '"></i>').join('')
      : '<span class="muted small">тут видно, хто тисне: жовті вгору, блакитні вниз</span>';
    let n = 0; const last = log[log.length - 1];
    for (let i = log.length - 1; i >= 0 && log[i] === last; i--) n++;
    $('#bdStreak').textContent = log.length ? 'серія ' + n + ' · ' + teamName(cur, last) : '';
  }

  /// Секундомір матчу й годинник оренди — раз на секунду, поки табло на екрані
  function clock() {
    if (!cur || mode !== 'board') return;
    const t = $('#bTime'), c = $('#bClock');
    if (!t) return;
    const a = cur.startedAt ? new Date(cur.startedAt).getTime() : 0;
    if (!a) t.textContent = '0:00';
    else {
      const sec = Math.max(0, Math.floor(((cur.endedAt ? new Date(cur.endedAt).getTime() : Date.now()) - a) / 1000)), p = (x) => String(x).padStart(2, '0');
      t.textContent = sec >= 3600 ? Math.floor(sec / 3600) + ':' + p(Math.floor(sec / 60) % 60) + ':' + p(sec % 60) : Math.floor(sec / 60) + ':' + p(sec % 60);
    }
    if (cur.courtUntil && cur.status === 'live') {
      const left = new Date(cur.courtUntil).getTime() - Date.now();
      c.textContent = left > 0 ? '⏳ до кінця оренди ' + P.span(left) : '⏰ оренда скінчилась';
      c.className = 'b-clock' + (left <= 0 ? ' over' : left <= 10 * 60000 ? ' soon' : '');
    } else { c.textContent = ''; c.className = 'b-clock'; }
  }

  function boardMsg(text, ms) {
    const el = $('#bMsg'); if (!el || !text) return;
    el.textContent = text; el.classList.add('show');
    clearTimeout(msgT); msgT = setTimeout(() => el.classList.remove('show'), ms || 2300);
  }
  function glek(text) {
    const el = $('#bdGlek'); if (!el) return;
    el.textContent = text; el.classList.remove('pop'); void el.offsetWidth; el.classList.add('pop');
  }
  function glekHello() {
    const m = cur;
    glek(m.status === 'abandoned' ? 'Цей матч скасували. Буває: пательня ще не розігрілась.'
      : m.state.over ? 'Матч скінчено. Цифри — нижче, висновки — кожен сам.'
        : !(m.log || []).length ? (canCtl(m) ? 'Хто подає першим? Тицни на ім’я на табло — і поїхали.' : 'Зараз почнуть. Сідай зручніше — рахунок оновлюється сам.')
          : canCtl(m) ? 'Рахую разом з вами. Тиць по половині — очко їй.' : 'Дивишся наживо — рахунок оновлюється сам.');
  }

  /// Нова дія (last.seq більший за бачений): повідомлення на табло, жарт Глека, голос
  function onLast(m, initial) {
    const l = m.last;
    if (!l) return;
    if (initial || l.seq <= seq) { seq = Math.max(seq, l.seq); return; }
    seq = l.seq;
    const ev = l.events || [];
    if (ev.length && l.text) {
      const long = ev.includes('match') || ev.includes('clock10') || ev.includes('clock0') || ev.includes('abandon');
      boardMsg(l.text, long ? 4200 : ev.includes('undo') ? 1200 : 2300);
    }
    react(m, ev, l.text || '');
    speak(l);
  }

  function react(m, ev, text) {
    const s = m.state, log = m.log || [], lt = log.length ? log[log.length - 1] : -1;
    const has = (k) => ev.includes(k);
    let gl = null;
    if (has('start')) gl = 'Нова гра. Хто подає першим? Тиць на ім’я.';
    else if (has('abandon')) gl = 'Матч скасовано. Глек нічого не бачив і в літопис не пише.';
    else if (has('clock0')) gl = pickOne(GL.clock0);
    else if (has('clock10')) gl = pickOne(GL.clock10);
    else if (has('server')) {
      const o = s.order || [], i = s.srv || 0;
      gl = text + '. Далі по колу: ' + o.slice(i).concat(o.slice(0, i)).map((k) => slotName(m, k)).join(' → ') + '.';
    } else if (has('match')) gl = s.winner < 0 ? 'Нічия. Глек задоволений: ніхто не плаче.' : fill(m, pickOne(GL.match), s.winner);
    else if (has('set') && lt >= 0) gl = fill(m, pickOne(GL.set), lt);
    else if (has('game') && lt >= 0 && /брейк/i.test(text)) gl = fill(m, pickOne(GL.brk), lt);
    if (!gl && has('deuce') && /золоте|star/i.test(text)) gl = pickOne(GL.decider);
    if (!gl && (has('tb') || has('stb'))) gl = pickOne(GL.tb);
    if (!gl && lt >= 0 && !has('undo') && !has('until')) {
      let n = 0; for (let i = log.length - 1; i >= 0 && log[i] === lt; i--) n++;
      if (n === 6 || n === 10) gl = fill(m, pickOne(GL.streak), lt, n);
    }
    if (!gl && has('game') && lt >= 0 && m.rules.mode === 'match' && !s.tb && s.games[1 - lt] === 0 && s.games[lt] === m.rules.setTo - 1) gl = fill(m, pickOne(GL.bagel), lt);
    if (!gl && log.length === 1 && !has('undo')) gl = 'Поїхали! Рахую разом з вами — тиць по половині, і очко її.';
    if (gl) glek(gl);
  }

  /// Голос на цьому пристрої: лише готовий кліп сервера (Остап); без кліпа — мовчимо
  let audio = null;
  function speak(l) {
    if (!P.pref('voice') || !l.say || !l.say.clip) return;
    try { if (audio) audio.pause(); audio = new Audio(l.say.clip); const p = audio.play(); if (p && p.catch) p.catch(() => {}); } catch { /* браузер не дав */ }
  }

  function apply(m) {
    if (!cur || m.id !== cur.id) return;
    if (cur.last && m.last && m.last.seq < cur.last.seq) return;   // запізнілий старий вид
    cur = m;
    paint(true);
    onLast(m, false);
  }

  async function act(a, extra) {
    if (!cur || !canCtl(cur)) return null;
    try { const r = await P.api('matches/' + encodeURIComponent(cur.id) + '/act', Object.assign({ a }, extra || {})); apply(r.match); return r.match; }
    catch (e) { if (inFs()) boardMsg(e.message, 2600); return null; }
  }
  function point(t) {
    if (!cur || !canCtl(cur)) return;
    if (cur.status !== 'live' || cur.state.over) { boardMsg('Гру скінчено: ↶ повернути або ← до матчів'); return; }
    act('point', { t });
  }

  const inFs = () => !!document.fullscreenElement || !!($('#bdBoard') && $('#bdBoard').classList.contains('fake-fs'));
  async function toggleFs() {
    const b = $('#bdBoard'); if (!b) return;
    if (document.fullscreenElement) { try { await document.exitFullscreen(); } catch { /* уже вийшли */ } return; }
    if (b.classList.contains('fake-fs')) { b.classList.remove('fake-fs'); P.wake(false); return; }
    try { if (!b.requestFullscreen) throw new Error('нема'); await b.requestFullscreen(); }
    catch { b.classList.add('fake-fs'); }   // айфон і вбудовані вікна — табло просто на весь екран сторінки
    P.wake(true);
  }
  async function leaveFs() {
    if (document.fullscreenElement) { try { await document.exitFullscreen(); } catch { /* */ } }
    const b = $('#bdBoard'); if (b) b.classList.remove('fake-fs');
  }

  function confirmSheet(title, text, ok, fn) {
    leaveFs();
    const sh = P.sheet(title, '<p style="margin-top:0">' + text + '</p><div class="bd-go"><button type="button" class="btn" data-no>Ні</button>'
      + '<button type="button" class="btn pri" data-yes>' + esc(ok) + '</button></div>');
    sh.el.querySelector('[data-no]').onclick = sh.close;
    sh.el.querySelector('[data-yes]').onclick = () => { sh.close(); fn(); };
  }

  function untilSheet() {
    leaveFs();
    const sh = P.sheet('⏳ Оренда корту', '<div class="stack"><div class="field"><label>Корт до</label><input class="inp" type="time" id="buT" value="' + esc(hhmm(cur.courtUntil)) + '"></div>'
      + '<p class="muted small" style="margin:0">За 10 хвилин до кінця табло скаже «останній гейм», а на нулі — що оренда скінчилась.</p>'
      + '<div class="bd-go"><button type="button" class="btn" data-off>Вимкнути</button><button type="button" class="btn pri" data-ok>Зберегти</button></div></div>');
    sh.el.querySelector('[data-off]').onclick = () => { sh.close(); act('until', { until: null }); };
    sh.el.querySelector('[data-ok]').onclick = () => { const u = untilIso(sh.el.querySelector('#buT').value); sh.close(); act('until', { until: u }); };
  }

  async function again() {
    const m = cur;
    const body = { teams: m.teams.map((t) => t.map((p) => p.pid)), rules: m.rules };
    if (m.gathering) body.gathering = m.gathering;
    if (m.courtUntil && new Date(m.courtUntil).getTime() > Date.now()) body.courtUntil = m.courtUntil;
    let r; try { r = await P.api('matches', body); } catch { return; }
    P.go('board', r.match.id);
  }

  /// Після кінця — статистика матчу з журналу (сервер рахує): подача/прийом, серії, брейки, вирішальні
  async function after() {
    const box = $('#bdAfter'); if (!box) return;
    if (!cur || cur.status !== 'done') { box.innerHTML = ''; statsFor = ''; return; }
    const key = cur.id + ':' + (cur.last ? cur.last.seq : 0);
    if (statsFor === key) return;
    statsFor = key;
    let st; try { st = await P.api('matches/' + encodeURIComponent(cur.id) + '/stats'); } catch { return; }
    if (!cur || cur.id + ':' + (cur.last ? cur.last.seq : 0) !== key) return;
    const m = cur, pct = (x) => x && x.of ? x.won + ' з ' + x.of + ' <span class="muted small">(' + Math.round(100 * x.won / x.of) + '%)</span>' : '–';
    const val = (x) => typeof x === 'object' ? (x && x.of ? x.won / x.of : -1) : x;
    const row = (label, arr, f) => {
      const a = arr && arr[0], b = arr && arr[1];
      return '<tr><td>' + label + '</td><td class="' + (val(a) > val(b) ? 'w' : '') + '">' + f(a) + '</td><td class="' + (val(b) > val(a) ? 'w' : '') + '">' + f(b) + '</td></tr>';
    };
    const num = (x) => x == null ? '–' : String(x);
    box.innerHTML = '<div class="card bd-stats"><div class="mom-h"><b>📊 Статистика матчу</b><span class="muted small">' + esc(m.score || '') + '</span></div>'
      + '<table class="tbl"><thead><tr><th></th><th><span class="tdot a"></span> ' + esc(teamName(m, 0)) + '</th><th><span class="tdot b"></span> ' + esc(teamName(m, 1)) + '</th></tr></thead><tbody>'
      + row('Взяли на своїй подачі', st.serve, pct) + row('Взяли на прийомі', st.recv, pct) + row('Найдовша серія', st.streak, num)
      + (m.rules.mode === 'match' ? row('Брейки', st.breaks, num) + row('Вирішальні очки', st.golden, num) : '')
      + '</tbody></table></div>';
  }

  async function openMatch(id) {
    mode = 'board';
    if (curId !== id) { curId = id; cur = null; seq = 0; statsFor = ''; }
    shell();
    let m;
    try { m = await P.api('matches/' + encodeURIComponent(id)); }
    catch { host.innerHTML = '<div class="card empty"><span class="e">🤷</span>Такого матчу нема — може, його вже скасували.<br><a href="#board">← До матчів</a></div>'; return; }
    if (curId !== id || mode !== 'board') return;
    cur = m;
    paint(false);
    onLast(m, true);
    glekHello();
    clearInterval(tickT); tickT = setInterval(clock, 1000);
  }

  function onClick(e) {
    if (e.target.closest('[data-new]')) { openNew(); return; }
    const card = e.target.closest('.bd-card[data-id]');
    if (card) { P.go('board', card.dataset.id); return; }
    const th = e.target.closest('[data-theme]');
    if (th) {
      P.pref('theme', th.dataset.theme);
      const b = $('#bdBoard'); if (b) b.className = b.className.replace(/theme-\w+/, 'theme-' + th.dataset.theme);
      for (const x of th.parentElement.children) x.classList.toggle('on', x === th);
      return;
    }
    const a = e.target.closest('[data-act]');
    if (a) {
      const k = a.dataset.act;
      if (k === 'fs') toggleFs();
      else if (k === 'voice') { P.pref('voice', !P.pref('voice')); paintVoice(); P.toast(P.pref('voice') ? '🔊 Глек оголошує рахунок на цьому пристрої' : '🔇 Глек мовчить'); }
      else if (k === 'undo') act('undo');
      else if (k === 'until') untilSheet();
      else if (k === 'again') again();
      else if (k === 'finish') confirmSheet('🏁 Завершити матч?', 'Рахунок ляже як є: перемагає той, у кого більше сетів, далі — геймів (у грі на очки — більше очок). Нічия теж буває. Передумаєте — «↶ Повернути матч».', '🏁 Завершити', () => act('finish'));
      else if (k === 'abandon') confirmSheet('✕ Скасувати матч?', 'Без результату: у рейтинг і статистику він не піде. Це — для матчу, заведеного помилково.', 'Скасувати матч', () => act('abandon'));
      return;
    }
    if (mode !== 'board' || !cur || !canCtl(cur)) return;
    const pn = e.target.closest('.pn[data-slot]');
    if (pn) { if (cur.status === 'live') act('serve', { slot: pn.dataset.slot }); return; }
    const half = e.target.closest('.half[data-t]');
    if (half) point(+half.dataset.t);
  }

  // Клавіші (кліккер для презентацій — це теж клавіатура): ←/PageUp — першим, →/PageDown — другим, Z — скасувати
  document.addEventListener('keydown', (e) => {
    if (mode !== 'board' || !cur || !visible() || e.repeat) return;
    if (e.key === 'Escape') { const b = $('#bdBoard'); if (b && b.classList.contains('fake-fs')) { b.classList.remove('fake-fs'); P.wake(false); } return; }
    if (document.querySelector('.pd-sheet-bg') || (e.target.closest && e.target.closest('input,textarea,select'))) return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.key === 'ArrowLeft' || e.key === 'PageUp') { e.preventDefault(); point(0); }
    else if (e.key === 'ArrowRight' || e.key === 'PageDown') { e.preventDefault(); point(1); }
    else if (e.code === 'KeyZ') { e.preventDefault(); act('undo'); }
  });
  document.addEventListener('fullscreenchange', () => { if (!document.fullscreenElement) P.wake(false); });

  // Геймпад: LB — очко першим, RB — другим (і хрестовина ←/→), B — скасувати
  function gpLoop() {
    const pads = [...(navigator.getGamepads ? navigator.getGamepads() : [])].filter(Boolean);
    if (!pads.length) { gpRun = false; return; }
    const map = [[4, 0], [5, 1], [14, 0], [15, 1], [1, 'undo']];
    for (const p of pads) {
      for (const [i, a] of map) {
        const pr = !!(p.buttons[i] && p.buttons[i].pressed), k = p.index + ':' + i;
        if (pr && !gpPrev[k] && mode === 'board' && visible() && !document.querySelector('.pd-sheet-bg')) { if (a === 'undo') act('undo'); else point(a); }
        gpPrev[k] = pr;
      }
    }
    requestAnimationFrame(gpLoop);
  }
  window.addEventListener('gamepadconnected', () => { if (!gpRun) { gpRun = true; requestAnimationFrame(gpLoop); } });

  P.on('match', (m) => {
    if (!m || !m.id) return;
    const up = (arr) => { const i = arr.findIndex((x) => x.id === m.id); if (i >= 0) arr[i] = m; else arr.unshift(m); };
    if (m.status === 'live') { lists.recent = lists.recent.filter((x) => x.id !== m.id); up(lists.live); }
    else { lists.live = lists.live.filter((x) => x.id !== m.id); up(lists.recent); }
    P.badge('board', lists.live.length || '');
    if (mode === 'list' && host && visible()) renderList();
    if (mode === 'board' && cur && m.id === cur.id) apply(m);
  });
  P.on('reconnected', () => { if (mode === 'board' && curId && visible()) openMatch(curId); else loadLists(); });

  P.tab({
    id: 'board', icon: '🎾', title: 'Табло', order: 1,
    mount(h) { host = h; host.addEventListener('click', onClick); },
    show(h, arg) {
      host = h;
      if (!arg || arg.startsWith('new')) {
        mode = 'list'; clearInterval(tickT);
        renderList(); loadLists();
        if (arg && arg.startsWith('new')) openNew(arg.startsWith('new:') ? arg.slice(4) : null);
      } else openMatch(arg);
    },
    hide() { mode = 'list'; clearInterval(tickT); leaveFs(); P.wake(false); },
  });
  // Позначка «скільки зараз на корті» — одразу, навіть якщо відкрили іншу вкладку
  setTimeout(loadLists, 0);
})();
