/*
  «Майстер дня» Гончарного кола (v12, docs/games/specs/clicker-v12.md §5). Частина ядра clicker.js — пакет C.
  Табло дня в «Селі» й чип у рядку статусу кола. Дані — view.day (ClickerDay.cs): day, ends, prizes, feast [хв «Свята»],
  minPoints, minPlayers, person, mine {place, points}, board [{place, nick, points, me}], players, last {day, line}.
  v13 Г2: поруч із «Майстром» — вкладка «🧺 Ловець дня»: view.day.catcher {prizes, minutes [хв «Кошика»], minPoints,
  minPlayers, mine, board, players, last} — та сама форма табло (null — Ловця вимкнено).
  v13 Г3, Г7: окрема картка «📜 Доручення Глека» (view.chores, див. нижче) і вікно «✨ Глек з бажанням».
  Вид стислий (межа розміру виду, ClickerSpeedTests): призи, хвилини, пороги, вітрила — у каталозі (catalog.day, catalog.week),
  рядки табло — масиви ([нік, очки]; тиждень — [нік, ріст, 1/0]), mine — [місце, очки], гість — guest: true; trackOf/growOf
  розгортають їх у ті самі об'єкти, що малює решта файлу.
  v13 Г4, Г5: третя вкладка «⛵ Тиждень»: view.week (ClickerWeek.cs) {ends, person, grow {prizes, sails, days, mine {place, x, days, ok},
  board [{nick, x, ok, me}], last}, coop {ware, q, vol, done, mine, min, fin, pct, people}}; чинні нагороди тижня — view.buffs.week
  {sails {pct, until}, coop {pct, until}}. «Довезти» — дія guild { op: 'coop', n }.
  Правила рахує сервер; тут лише малюємо, а очки за горно — та сама формула, що в KilnFinish (лише для відгуку).
*/
(() => {
  if (!window.HClicker || !window.HClicker.part) return;

  // Ті самі числа, що DayPointsQ3/DayPointsQ4 у ClickerTrade.cs: лише щоб сказати «+5» одразу у вікні горна.
  const PTS_Q3 = 1, PTS_Q4 = 3;
  const MEDAL = ['🥇', '🥈', '🥉'];
  const FEW = { 2: 'двоє', 3: 'троє', 4: 'четверо', 5: 'п\'ятеро' };

  let hhmm = null;
  /// «23:30» беремо з самого ends (київський час), а не пишемо руками: межу доби тримає сервер.
  function closeAt(ends) {
    if (!hhmm) {
      try { hhmm = new Intl.DateTimeFormat('uk-UA', { hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Kyiv' }); }
      catch { hhmm = new Intl.DateTimeFormat('uk-UA', { hour: '2-digit', minute: '2-digit' }); }
    }
    return hhmm.format(new Date(ends));
  }

  /// «3 год 12 хв» (full) чи «3 год» — до кінця доби; менше години — хвилинами.
  function leftText(ms, full) {
    const m = Math.floor(Math.max(0, ms) / 60000);
    if (m >= 60) return Math.floor(m / 60) + ' год' + (full && m % 60 ? ' ' + (m % 60) + ' хв' : '');
    return m >= 1 ? m + ' хв' : 'менше хвилини';
  }

  const pointsWord = (api, n) => api.plural(n, 'очко', 'очки', 'очок');

  /// Очки за партію з виду горна: лише ручне горно (палій і підмайстри — ні), дзвінкий 1, розкішний 3.
  function kilnPoints(l) {
    if (!l || l.helper || !Array.isArray(l.items)) return 0;
    let n = 0;
    for (const it of l.items) n += it[1] >= 4 ? PTS_Q4 : it[1] === 3 ? PTS_Q3 : 0;
    return n;
  }

  /// День перед d («2026-10-21» → «2026-10-20»): учорашній підсумок кличемо «учора», давніший — датою.
  function dayBefore(d) {
    const t = Date.parse(d + 'T12:00:00Z');
    return Number.isFinite(t) ? new Date(t - 864e5).toISOString().slice(0, 10) : '';
  }

  // ---------- розділ у «Селі» ----------

  /// Місце розділу — перед возом цеху (st.guildBody), а якщо є кораблі — одразу за ними: такий порядок у рядку переходів
  /// «Села» (Гості · Толока · Кораблі · Майстер дня · Віз). Перевіряємо лише порядок, а не сусідство: інакше дві частини,
  /// що обидві хочуть стояти «перед возом», переставляли б одна одну на кожному виді.
  function place(st, card) {
    const pane = st.guildPane, el = card || st.dayEl;
    if (!pane || !el) return;
    const body = st.guildBody && st.guildBody.parentElement === pane ? st.guildBody : null;
    const trade = pane.querySelector(':scope > [data-sec="trade"]');
    const before = (a, b) => !!(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);
    const ok = el.parentElement === pane && (!body || before(el, body)) && (!trade || before(trade, el));
    if (ok) return;
    if (trade && (!body || before(trade, body))) trade.after(el);
    else if (body) pane.insertBefore(el, body);
    else pane.appendChild(el);
  }

  function boardHtml(st, api, d, empty) {
    const esc = (x) => api.esc(st, x);
    const rows = d.board.map((r) => '<li class="clkmd-row' + (r.me ? ' me' : '') + '">'
      + '<span class="clkmd-pl">' + (MEDAL[r.place - 1] || r.place + '.') + '</span>'
      + '<span class="clkmd-nick">' + esc(r.nick) + '</span>' + (r.me ? '<span class="clkmd-you">ти</span>' : '')
      + '<b class="clkmd-pts">' + api.num(r.points) + '</b></li>');
    // Я поза першою десяткою — окремим рядком під табло: своє місце видно завжди.
    const outside = !!d.mine && !d.board.some((r) => r.me);
    if (outside) {
      rows.push('<li class="clkmd-gap" aria-hidden="true">⋯</li><li class="clkmd-row me"><span class="clkmd-pl">' + d.mine.place + '.</span>'
        + '<span class="clkmd-nick">ти</span><b class="clkmd-pts">' + api.num(d.mine.points) + '</b></li>');
    }
    if (!rows.length) {
      return '<div class="muted small">' + (d.person ? empty : 'Сьогодні ще ніхто не набрав очок.') + '</div>';
    }
    const more = d.players - d.board.length - (outside ? 1 : 0);
    return '<ol class="clkmd-board">' + rows.join('') + '</ol>'
      + (more > 0 ? '<div class="muted small clkmd-more">і ще ' + more + ' ' + api.plural(more, 'майстер', 'майстри', 'майстрів') + ' нижче</div>' : '');
  }

  /// Призи й поріг одним рядком, з тим, скільки людей поріг уже взяли (з першої десятки — нижче за неї поріг і так не взяти).
  /// mins/name — хвилини бафа за ті самі місця («🎉 Свято» 60 · 30 · 15 хв): призи до кола лягають у запас, гончар вмикає сам.
  function prizeLine(st, api, d, mins, name) {
    const prizes = d.prizes.filter((p) => p > 0);
    if (!prizes.length) return '';
    const bonus = (mins || []).some((m) => m > 0) ? ' + ' + name + ' ' + mins.filter((m) => m > 0).join(' · ') + ' хв' : '';
    const list = prizes.map((p, i) => (MEDAL[i] || (i + 1) + '.') + ' ' + api.num(p)).join(' · ') + ' 🏺' + bonus;
    const need = Math.max(1, d.minPlayers || 1);
    const min = d.minPoints || 0;
    const have = d.board.filter((r) => r.points >= min).length;
    const who = need <= 1 ? 'якщо набрати від ' + min + ' ' + pointsWord(api, min)
      : 'якщо від ' + min + ' ' + pointsWord(api, min) + ' наберуть хоча б ' + (FEW[need] || need + ' гравців');
    return '<div class="small clkmd-prize">Призи о ' + d.at + ': ' + list + ', ' + who
      + (need > 1 ? ' <span class="muted">(зараз — ' + have + ')</span>' : '') + '</div>';
  }

  /// Табло дошки дня з виду: рядок — [нік, очки], місце — номер у списку, «я» — рядок із моїм місцем; mine — [місце, очки].
  /// (Старий вигляд — об'єкти {place, nick, points, me} — теж приймаємо.)
  function trackOf(t) {
    const m = t.mine;
    const mine = Array.isArray(m) ? { place: m[0], points: m[1] } : m || null;
    const board = (Array.isArray(t.board) ? t.board : []).map((r, i) => (Array.isArray(r)
      ? { place: i + 1, nick: r[0], points: r[1], me: !!mine && mine.place === i + 1 } : r));
    return { mine, board, players: t.players || 0, last: t.last || null };
  }

  /// Табло росту тижня: рядок — [нік, ріст, у заліку 1/0]; mine — [місце, ріст, дні, у заліку 1/0]; призи й вітрила — з каталогу.
  function growOf(g, cat) {
    const m = g.mine;
    const mine = Array.isArray(m) ? { place: m[0], x: m[1], days: m[2], ok: !!m[3] } : m || null;
    const board = (Array.isArray(g.board) ? g.board : []).map((r, i) => (Array.isArray(r)
      ? { nick: r[0], x: r[1], ok: !!r[2], me: !!mine && mine.place === i + 1 } : r));
    return {
      prizes: Array.isArray(cat.prizes) ? cat.prizes : [], sails: Array.isArray(cat.sails) ? cat.sails : [],
      days: cat.days || 3, mine, board, last: g.last || null,
    };
  }

  // ---------- вкладка «⛵ Тиждень» (v13 Г4, Г5) ----------

  // Ті самі числа, що GrowMinMinutes/GrowMinTotal у ClickerWeekOptions: лише щоб сказати умову словами.
  const WEEK_MIN = 5, WEEK_TOTAL = '1e30';
  const fx = (x) => (x >= 100 ? String(Math.round(x)) : x.toFixed(1).replace('.', ','));
  const dm = (iso) => { const t = new Date(iso); return String(t.getDate()).padStart(2, '0') + '.' + String(t.getMonth() + 1).padStart(2, '0'); };

  const wareNameOf = (st, key) => {
    const list = (st.craft && st.craft.wares) || (st.catalog && st.catalog.wares) || [];
    const w = list.find((x) => x.key === key);
    return w ? w.name : key;
  };

  /// Скільки з комори можна довезти: придатні вироби мінус відкладене (кожен ключ несе kept = [Толока, замовлення, майстерштук]).
  function coopFree(st, c) {
    let free = 0;
    for (const it of ((st.craft && st.craft.items) || [])) {
      if (it.ware !== c.ware || it.q < c.q) continue;
      free += Math.max(0, it.n - (Array.isArray(it.kept) ? it.kept.reduce((a, b) => a + b, 0) : 0));
    }
    return free;
  }

  function weekGrowHtml(st, api, w) {
    const g = w.grow;
    const esc = (x) => api.esc(st, x);
    const rows = g.board.map((r, i) => '<li class="clkmd-row' + (r.me ? ' me' : '') + (r.ok ? '' : ' dim') + '"'
      + (r.ok ? '' : ' title="Ще не виконав умову: ' + g.days + ' дні гри і total від ' + WEEK_TOTAL + '"') + '>'
      + '<span class="clkmd-pl">' + (r.ok ? (MEDAL[i] || (i + 1) + '.') : '·') + '</span>'
      + '<span class="clkmd-nick">' + esc(r.nick) + '</span>' + (r.me ? '<span class="clkmd-you">ти</span>' : '')
      + '<b class="clkmd-pts">×' + fx(r.x) + '</b></li>');
    const m = g.mine;
    const me = !st.mine ? '' : !w.person ? '<div class="small muted">Змагання — для тих, хто увійшов.</div>'
      : !m ? '<div class="small">Ти ще без росту цього тижня — зайди в гру, і табло тебе побачить.</div>'
        : '<div class="small">Твій ріст: <b>×' + fx(m.x) + '</b> · дні гри: <b>' + m.days + '</b> з ' + g.days
          + (m.ok ? ' ✓ <span class="muted">— ти в заліку</span>' : ' <span class="muted">— в залік: ' + g.days + ' дні гри по ' + WEEK_MIN + ' хв і total від ' + WEEK_TOTAL + '</span>') + '</div>';
    const prizes = (g.prizes || []).map((p, i) => (MEDAL[i] || (i + 1) + '.') + ' ' + api.num(p)).join(' · ');
    const sails = (g.sails || []).map((p) => '+' + p).join(' · ');
    return '<div class="clkmd-wk"><div class="clkmd-wh">⛵ Хто більше виріс</div>'
      + '<div class="small muted">Очки — у скільки разів виріс твій загальний заробіток за тиждень.</div>' + me
      + (rows.length ? '<ol class="clkmd-board">' + rows.join('') + '</ol>' : '<div class="muted small">Ніхто ще не набрав росту цього тижня.</div>')
      + '<div class="small clkmd-prize">Призи в неділю о ' + w.at + ': ' + prizes + ' 🏺 + <b>⛵ Вітрила тижня</b> ' + sails + ' % до всього на 7 діб і вимпел на хаті.</div>'
      + (g.last ? '<div class="small clkmd-last"><span class="muted">Минулий тиждень:</span> ' + esc(String(g.last).replace(/^⛵\s*Хто більше виріс за тиждень у Гончарному колі:\s*/, '')) + '</div>' : '')
      + '</div>';
  }

  function weekCoopHtml(st, api, w) {
    const c = w.coop;
    const name = wareNameOf(st, c.ware).toLowerCase();
    const pct = Math.min(100, Math.floor(100 * c.done / Math.max(1, c.vol)));
    const free = coopFree(st, c);
    const left = Math.max(0, c.vol - c.done);
    const can = st.mine && w.person && !c.fin;
    const room = c.max > 0 ? Math.max(0, c.max - c.mine) : left;
    const send = Math.min(left, free, room);
    const mineLine = !w.person ? '' : c.mine >= c.min
      ? '<div class="small">Твій внесок: <b>' + api.num(c.mine) + '</b> ✓ <span class="muted">— на бонус вистачає</span></div>'
      : '<div class="small">Твій внесок: <b>' + api.num(c.mine) + '</b> <span class="muted">— до бонусу ще ' + api.num(c.min - c.mine) + ' (від ' + api.num(c.min) + ', це 5 %)'
        + (free + c.mine >= c.min ? ', у коморі вистачає' : free > 0 ? ', у коморі не вистачає' : '') + '</span></div>';
    const btn = !can ? '' : '<div class="clkmd-coop"><button type="button" class="clkmd-coopbtn" data-coop="' + send + '"' + (send > 0 ? '' : ' disabled') + '>'
      + (send > 0 ? '🤝 Довезти ' + api.num(send) : room <= 0 ? 'Стелю довезено' : 'Нема вільних') + '</button>'
      + '<span class="small muted">вільних у коморі: ' + api.num(free) + ' (відкладене під замовлення й Толоку не чіпаю)'
      + (c.max > 0 ? ' · твоя стеля ' + api.num(c.max) + ' (25 % обсягу), ще можна ' + api.num(room) : '') + '</span></div>';
    return '<div class="clkmd-wk"><div class="clkmd-wh">🤝 Спільне замовлення округи</div>'
      + '<div>' + api.esc(st, name.charAt(0).toUpperCase() + name.slice(1)) + ' якості ' + c.q + '+ — <b>' + api.num(c.vol) + '</b> шт. Везе кожен зі своєї комори, платить як село.</div>'
      + '<div class="clkmd-bar" role="progressbar" aria-valuemin="0" aria-valuemax="' + c.vol + '" aria-valuenow="' + c.done + '"><i style="width:' + pct + '%"></i></div>'
      + '<div class="small">Довезли: <b>' + api.num(c.done) + '</b> з ' + api.num(c.vol) + ' · ' + c.people + ' ' + api.plural(c.people, 'гончар', 'гончарі', 'гончарів') + '</div>'
      + (c.fin ? '<div class="small clkmd-ok">🎉 Округа виконала замовлення! Бонус +' + c.pct + ' % — після підсумку тижня о ' + w.at + ', тим, хто довіз від ' + api.num(c.min) + '.</div>'
        : '<div class="small muted">Виконаємо до неділі ' + w.at + ' — кожен, хто довіз від ' + api.num(c.min) + ' (5 %), дістане +' + c.pct + ' % до всього на 7 діб.</div>')
      + mineLine + btn + '</div>';
  }

  /// Чинні нагороди тижня: що вже тримає множник і до коли.
  function weekBuffsHtml(st) {
    const b = st.weekBuffs;
    if (!b) return '';
    const out = [];
    if (b.sails && Date.parse(b.sails.until) > Date.now()) out.push('⛵ Вітрила тижня +' + b.sails.pct + ' % до ' + dm(b.sails.until));
    if (b.coop && Date.parse(b.coop.until) > Date.now()) out.push('🤝 Спільне замовлення +' + b.coop.pct + ' % до ' + dm(b.coop.until));
    return out.length ? '<div class="small clkmd-act">Діє зараз: ' + out.join(' · ') + '</div>' : '';
  }

  function weekHtml(st, api) {
    const w = st.week;
    return '<div class="clkmd-me"><span class="muted small">Тиждень — до неділі о ' + w.at + '</span><span class="clkmd-left muted small"></span></div>'
      + weekBuffsHtml(st) + (w.grow ? weekGrowHtml(st, api, w) : '') + (w.coop ? weekCoopHtml(st, api, w) : '');
  }

  function lastLine(st, api, d) {
    const l = d.last;
    if (!l || !l.line) return '';
    // У Балачках рядок починається з «🏆 Майстер дня в Гончарному колі:» — тут, під тим самим заголовком, це повтор.
    const text = String(l.line).replace(/^(?:🏆|🧺)\s*(?:Майстер|Ловець) дня(?: в Гончарному колі)?:\s*/, '');
    const day = String(l.day || '');
    const when = day === dayBefore(d.day || '') ? 'Учора' : day.slice(8, 10) + '.' + day.slice(5, 7);
    return '<div class="small clkmd-last"><span class="muted">' + when + ':</span> ' + api.esc(st, text) + '</div>';
  }

  function paint(st, api) {
    const d = st.day;
    if (!d || !st.guildPane) {
      if (st.dayEl && st.dayEl.parentElement) st.dayEl.remove();
      return;
    }
    if (!st.dayEl) {
      st.dayEl = document.createElement('section');
      st.dayEl.className = 'clkg-card clkmd';
      st.dayEl.dataset.sec = 'day';
      st.dayEl.dataset.fold = 'd.day';
      // Вкладки «Майстер / Ловець»: делегування на самій картці — вміст під нею перемальовується щоразу.
      st.dayEl.addEventListener('click', (e) => {
        // v13 Г5: «Довезти» — зі своєї комори у спільне замовлення (сервер сам бере лише вільне й платить як село).
        const coop = e.target.closest('.clkmd-coopbtn');
        if (coop) {
          const n = parseInt(coop.dataset.coop, 10) || 0;
          if (n > 0 && !coop.disabled) {
            coop.disabled = true;
            api.act(st, 'guild', { op: 'coop', n }).then((r) => { if (r && r.ok) api.sfx('gift'); });
          }
          return;
        }
        const b = e.target.closest('.clkmd-tab');
        if (!b || !st.day) return;
        const t = b.dataset.t === 'c' ? 'c' : b.dataset.t === 'w' ? 'w' : 'm';
        if (st.dayTab === t) return;
        api.sfx('tap');
        st.dayTab = t;
        paint(st, api);
        tick(st, api);
      });
    }
    place(st);
    const c = d.catcher;
    const wk = st.week;
    if (!c && st.dayTab === 'c') st.dayTab = 'm';
    if (!wk && st.dayTab === 'w') st.dayTab = 'm';
    const onWeek = !!wk && st.dayTab === 'w';
    // Вкладка: «Майстер», «Ловець» (v13 Г2) чи «Тиждень» (Г4, Г5). Ловця й тижня нема — вкладок нема, усе як було.
    const tab = c && st.dayTab === 'c' && !onWeek ? c : d;
    const head = d.person && d.mine ? ' <span class="muted small">· місце ' + d.mine.place + ' · ' + api.num(d.mine.points) + '</span>' : '';
    const headC = d.person && c && c.mine ? ' <span class="muted small">· 🧺 ' + c.mine.place + '-й</span>' : '';
    // Чуже коло (глядач): «твоє місце» тут — місце господаря, тож цього рядка глядачеві не пишемо.
    const me = !st.mine ? '' : '<div class="clkmd-me' + (d.person ? '' : ' guest') + '">' + (!d.person
      ? '<span>Табло — для тих, хто увійшов. Увійди, щоб змагатись.</span>'
      : tab.mine ? '<span>Твоє місце: <b>' + tab.mine.place + '</b> · <b>' + api.num(tab.mine.points) + '</b> ' + pointsWord(api, tab.mine.points) + '</span>'
        : '<span>Ти сьогодні ще без очок</span>')
      + '<span class="clkmd-left muted small"></span></div>';
    const tabs = !c && !wk ? '' : '<div class="clkmd-tabs" role="tablist">'
      + '<button type="button" role="tab" class="clkmd-tab' + (!onWeek && tab === d ? ' on' : '') + '" data-t="m" aria-selected="' + (!onWeek && tab === d) + '">🏆 Майстер'
      + (d.person && d.mine ? ' · ' + d.mine.place + '-й' : '') + '</button>'
      + (c ? '<button type="button" role="tab" class="clkmd-tab' + (!onWeek && tab === c ? ' on' : '') + '" data-t="c" aria-selected="' + (!onWeek && tab === c) + '">🧺 Ловець'
        + (d.person && c.mine ? ' · ' + c.mine.place + '-й' : '') + '</button>' : '')
      + (wk ? '<button type="button" role="tab" class="clkmd-tab' + (onWeek ? ' on' : '') + '" data-t="w" aria-selected="' + onWeek + '">⛵ Тиждень'
        + (wk.person && wk.grow && wk.grow.mine ? ' · ×' + fx(wk.grow.mine.x) : '') + '</button>' : '')
      + '</div>';
    const body = onWeek ? weekHtml(st, api) : tab === d
      ? boardHtml(st, api, d, 'Сьогодні ще ніхто не набрав очок — перше дзвінке горно виведе тебе нагору.') + prizeLine(st, api, d, d.feast, '🎉 Свято')
        + '<div class="small muted clkmd-how">Як набирати: ручне горно — дзвінкий 1, розкішний 3; палій і підмайстри не рахуються.</div>'
      : boardHtml(st, api, c, 'Сьогодні ще ніхто не ловив — перший глек з полиці, спійманий руками, виведе тебе нагору.') + prizeLine(st, api, c, c.minutes, '🧺 Кошик')
        + '<div class="small muted clkmd-how">Як набирати: кожен глек з полиці, спійманий руками, — одне очко; комора, кіт і підмайстри не рахуються.</div>';
    const title = c && wk ? '🏆 Майстер, Ловець і Тиждень' : c ? '🏆 Майстер і Ловець дня' : wk ? '🏆 Майстер дня і Тиждень' : '🏆 Майстер дня';
    const html = api.foldHead(title + head + headC)
      + '<div class="clkmd-body">' + tabs + (onWeek ? '' : me) + body + (onWeek ? '' : lastLine(st, api, tab)) + '</div>';
    if (api.swap(st.dayEl, html) || !st.dayLeftEl) { st.dayLeftEl = st.dayEl.querySelector('.clkmd-left'); st.dayLeftKey = ''; }
  }

  /// Відлік — лише текстом і лише коли змінилась хвилина: усе інше в розділі живе з видом.
  function tick(st, api) {
    const d = st.day;
    const ms = d ? d.endsMs - api.serverNow(st) : 0;
    const at = d ? d.at : '';
    if (d && st.dayLeftEl) {
      // Вкладка «Тиждень» лічить до неділі, решта — до кінця доби (чип у рядку статусу завжди про добу).
      const onWeek = !!st.week && st.dayTab === 'w';
      const wms = onWeek ? st.week.endsMs - api.serverNow(st) : 0;
      const t = onWeek ? (wms > 0 ? 'до неділі ' + st.week.at + ' — ' + leftText(wms, true) : 'підбиваємо підсумок тижня…')
        : ms > 0 ? 'до ' + at + ' — ' + leftText(ms, true) : 'підбиваємо підсумок…';
      if (st.dayLeftKey !== t) { st.dayLeftKey = t; st.dayLeftEl.textContent = t; }
    }
    paintChip(st, api, ms, at);
  }

  // ---------- чип у рядку статусу кола ----------

  /// Чип живе в рядку «швидкість · суперник» над сценою (.clk-rr): там коло вже каже, де ти серед інших. Ядро той
  /// рядок перемальовує разом із карткою — тож елемент перевіряємо й ставимо на місце щоразу, а не раз при монтуванні.
  function chipEl(st, api) {
    const rr = st.el && st.el.querySelector('.clk-rr');
    if (!rr) return null;
    let c = st.dayChip;
    if (!c) {
      c = st.dayChip = document.createElement('button');
      c.type = 'button';
      c.className = 'clkmd-chip small';
      c.hidden = true;
      c.innerHTML = '<span class="clkmd-ch"></span><span class="clkmd-ct"></span><span class="clkmd-cs"></span>';
      c.addEventListener('click', () => go(st, api));
    }
    if (c.parentElement !== rr) {
      rr.appendChild(c);
      c._sig = '';
      // Рядок змінив ширину (вікно, ⛶, балачки згорнули), а тексти ті самі — переміряти в найближчому slow.
      if (st.dayRo) st.dayRo.disconnect();
      if (window.ResizeObserver) {
        st.dayRo = new ResizeObserver(() => { if (st.dayChip) st.dayChip._sig = ''; });
        st.dayRo.observe(rr);
      }
    }
    return c;
  }

  /// Видно, коли є за що змагатись: людина (не гість), коло своє, «Село» вже відчинене (тап веде туди), і на табло хтось є.
  function chipOn(st) {
    const d = st.day;
    if (!d || !d.person || !st.mine || !st.dayEl || !st.dayEl.isConnected) return false;
    if (!d.mine && !d.players) return false;
    const tab = st.tabs && st.tabs.querySelector('[data-tab="guild"]');
    return !!tab && !tab.hidden;
  }

  function paintChip(st, api, ms, at) {
    const on = chipOn(st);
    const c = on ? chipEl(st, api) : st.dayChip;
    if (!c) return;
    const rr = c.parentElement;
    if (!on) {
      if (!c.hidden) { c.hidden = true; if (rr) rr.classList.remove('clkmd-on'); }
      return;
    }
    const d = st.day;
    const headT = d.mine ? '🏆 ' + d.mine.place + '-й · ' + api.num(d.mine.points) : '🏆 Майстер дня';
    const tailT = ms > 0 ? ' · до ' + at + ' — ' + leftText(ms, false) : ' · підсумок';
    const shortT = d.mine && ms > 0 ? ' · ' + leftText(ms, false) : '';
    const sig = headT + '|' + tailT + '|' + shortT + '|' + st.el.className;
    const rival = st.el.querySelector('.clk-rival');
    const rivalT = rival && !rival.hidden ? rival.textContent : '';
    if (c._sig === sig && c._rival === rivalT && !c.hidden) return;
    c._sig = sig;
    c._rival = rivalT;
    const [h, t, s] = c.children;
    h.textContent = headT;
    t.textContent = tailT;
    s.textContent = shortT;
    c.title = (d.mine ? 'Майстер дня: місце ' + d.mine.place + ', ' + d.mine.points + ' ' + pointsWord(api, d.mine.points) : 'Майстер дня: ти сьогодні ще без очок')
      + (ms > 0 ? ' · до ' + at + ' — ' + leftText(ms, true) : '') + '. Табло — у «Селі»';
    c.setAttribute('aria-label', c.title);
    if (c.hidden) c.hidden = false;
    rr.classList.add('clkmd-on');
    fitChip(st, api, c, rr, rival, rivalT);
  }

  /// Чип і суперник ділять один рядок: чип ніколи не ріжемо, а суперник бере, що лишилось (--clkmd-w у css). Не влазять
  /// обоє повністю — чип коротшає до «🏆 2-й · 46 · 3 год». Міряємо лише тоді, коли змінився текст чипа чи суперника
  /// (суперник — не частіше разу на секунду), а не щокадру. Телефон лежачи кладе чип окремим рядком — там міряти нічого.
  function fitChip(st, api, c, rr, rival, rivalT) {
    c.classList.remove('short');
    if (st.el.classList.contains('land')) { rr.style.removeProperty('--clkmd-w'); return; }
    // Дробові пікселі — угору: offsetWidth округлює вниз, і пів пікселя зайвого переносило чип на третій рядок.
    // Під масштабом 2K/4K getBoundingClientRect міряє видимі пікселі, а css чекає пікселів картки — ділимо на zoom.
    const width = () => Math.ceil(c.getBoundingClientRect().width / (api.zoom(st) || 1));
    const full = width();
    const short = !!rivalT && rival.scrollWidth + full + 10 > rr.clientWidth;
    if (short) c.classList.add('short');
    rr.style.setProperty('--clkmd-w', (short ? width() : full) + 'px');
  }

  /// Тап по чипу — у «Село», до розділу. Є рядок переходів «Села» (clicker-guild.js) — тиснемо його кнопку: він
  /// уміє розгорнути згорнуте й стати під липким рядком. Нема — самі: вкладка, розгорнути, підгорнути.
  function go(st, api) {
    api.sfx('tap');
    api.showTab(st, 'guild');
    const nav = st.guildPane && st.guildPane.querySelector('[data-nav="day"]');
    if (nav) { nav.click(); return; }
    const el = st.dayEl;
    if (!el || !el.isConnected) return;
    api.unfold(st, el);
    requestAnimationFrame(() => {
      if (!el.isConnected) return;
      const pane = el.closest('.clk-pane');
      if (pane && pane.scrollHeight > pane.clientHeight + 1 && getComputedStyle(pane).overflowY !== 'visible') {
        pane.scrollTop += el.getBoundingClientRect().top - pane.getBoundingClientRect().top - 8;
        return;
      }
      // Гортається сторінка (телефон): липка шапка сайту й плашка кола (з рядом вкладок) закривають верх — стаємо під
      // ними. Плашка з'являється, лише коли коло пішло з екрана, тож міряємо ще раз після першого кроку.
      const fix = () => {
        if (!el.isConnected) return;
        const hdr = document.querySelector('body > header');
        const pin = st.el.querySelector('.clk-pin');
        let top = hdr && /sticky|fixed/.test(getComputedStyle(hdr).position) ? hdr.getBoundingClientRect().bottom : 0;
        if (pin && !pin.hidden) for (const x of pin.children) if (x.getClientRects().length && x.offsetHeight > 0) top = Math.max(top, x.getBoundingClientRect().bottom);
        const d = el.getBoundingClientRect().top - top - 8;
        if (Math.abs(d) > 2) window.scrollBy(0, d);
      };
      fix();
      setTimeout(fix, 120);
    });
  }

  // ---------- відгук після ручного горна ----------

  /// «+5 до Майстра дня» — рядком у самому вікні відкриття горна, під підсумком партії (clicker-kiln.js, .clkk-rv-tally),
  /// а не ще одним тостом: плейтест 21.10 скаржився на стоси тостів (clicker-v12-ux.md §2.7). Вікно горно відкриває
  /// то одразу, то пізніше (поки відкритий розпис), і ще раз — кнопкою «Показати відкриття»: дивимось, поки воно відкрите.
  function markReveal(st, api) {
    const k = st.dayKiln;
    if (!k || k.pts <= 0 || !st.day || !st.day.person || !api.overlayOpen(st)) return;
    const box = st.el.querySelector('.clk-overlay:not([hidden]) .clkk-reveal');
    if (!box || box.querySelector('.clkmd-plus')) return;
    const tally = box.querySelector('.clkk-rv-tally');
    const line = document.createElement('div');
    line.className = 'clkmd-plus small';
    // Місце — лише для партії з цієї доби: повтор учорашнього горна не має казати сьогоднішнє місце.
    const today = st.day.mine && k.at > st.day.endsMs - 864e5;
    line.textContent = '🏆 +' + k.pts + ' до Майстра дня' + (today ? ' · місце ' + st.day.mine.place : '');
    if (tally) tally.after(line);
    else box.prepend(line);
  }

  // ---------- v13 Г3: «📜 Доручення Глека» (ClickerChores.cs) ----------
  //
  // view.chores — масив (межа розміру виду): [0] рядок «код+зроблено» («c12g1f0»), [1] серія днів, [2] скриню забрано (1/0),
  // [3] чарівний глек і бажання (див. нижче) чи відсутнє, [4] черепки скрині [звичайної, сьомого дня] — лише коли можна забирати.
  // Цілі й тексти знає клієнт за кодом: ті самі, що ChoreCatalog / ChoreCodes на сервері.

  const CH = {
    c: { ic: '🤲', tx: 'Спіймай 30 глеків з полиці', g: 30 },
    g: { ic: '🎪', tx: 'Спіймай 3 розписні глеки', g: 3 },
    f: { ic: '🔥', tx: 'Обпали руками розписну партію', g: 1 },
    s: { ic: '🚢', tx: 'Завантаж корабель до повного', g: 1 },
    v: { ic: '📜', tx: 'Відвези замовлення в село', g: 1 },
    u: { ic: '🧳', tx: 'Віддай гостю замовлення', g: 1 },
    t: { ic: '🏗', tx: 'Поклади 20 виробів на Толоку', g: 20 },
    i: { ic: '🎁', tx: 'Подаруй дарунок другові', g: 1 },
    e: { ic: '🎉', tx: 'Влаштуй фестиваль', g: 1 },
    r: { ic: '❓', tx: 'Відповідай на загадку Глека', g: 1 },
  };
  const STREAK_MAX = 7;

  function parseChores(st, v, api) {
    if (v.trade !== undefined) st.choresTrade = !!(v.trade && typeof v.trade === 'object');
    if (v.chores === undefined) return;
    const c = v.chores;
    const prev = st.chores;
    if (!Array.isArray(c)) { st.chores = null; st.wish = null; return; }
    const tasks = [];
    const re = /([a-z])(\d+)/g;
    let m;
    while ((m = re.exec(String(c[0] || ''))) && tasks.length < 3) {
      const d = CH[m[1]];
      if (d) tasks.push({ code: m[1], d, n: Math.min(d.g, parseInt(m[2], 10) || 0) });
    }
    const taken = c[2] === 1;
    const ready = tasks.length > 0 && !taken && tasks.every((t) => t.n >= t.d.g);
    st.chores = { tasks, streak: c[1] | 0, taken, ready, rw: Array.isArray(c[4]) ? c[4] : null };
    st.wish = c[3] && typeof c[3] === 'object' ? c[3] : null;
    // Тост «виконано» — за зміною прогресу між видами (ті самі доручення того ж дня); сервер у повідомлення дій їх не пише.
    if (prev && st.mine && prev.tasks.map((t) => t.code).join() === tasks.map((t) => t.code).join()) {
      if (ready && !prev.ready && !prev.taken) api.toast(st, '📜 Усі доручення Глека виконано — забирай скриню!', 'ok');
      else {
        const t = tasks.find((x, i) => x.n >= x.d.g && prev.tasks[i].n < x.d.g);
        if (t) api.toast(st, '📜 Доручення виконано: ' + t.d.tx.toLowerCase(), 'ok');
      }
    }
  }

  function chestLine(st) {
    const c = st.chores;
    const next = c.taken ? c.streak : c.streak + 1;
    const big = !c.taken && next === STREAK_MAX;
    const bits = ['30 хв гри грошима', st.choresTrade ? 'Слава +200' : 'ще 15 хв гри'];
    if (c.rw) bits.push(c.rw[0] + (big ? ' + ' + c.rw[1] : '') + ' 🏺');
    else bits.push('черепки' + (big ? ' (і ще за сьомий день)' : ''));
    if (big) bits.push('🎉 «Свято» 60 хв');
    return (big ? '👑 Велика скриня сьомого дня: ' : '📦 У скрині: ') + bits.join(' · ');
  }

  function paintChores(st, api) {
    const c = st.chores;
    if (!c || !c.tasks.length || !st.guildPane || !st.mine) {
      if (st.choresEl && st.choresEl.parentElement) st.choresEl.remove();
      return;
    }
    if (!st.choresEl) {
      st.choresEl = document.createElement('section');
      st.choresEl.className = 'clkg-card clkmd clkch';
      st.choresEl.dataset.sec = 'chores';
      st.choresEl.dataset.fold = 'd.chores';
      st.choresEl.addEventListener('click', (e) => {
        const claim = e.target.closest('.clkch-claim');
        if (claim && !claim.disabled) {
          claim.disabled = true;
          api.act(st, 'chores', { op: 'claim' }).then((r) => { if (r && r.ok) api.sfx('gift'); });
          return;
        }
        if (e.target.closest('.clkch-wishbtn')) { api.sfx('tap'); openWish(st, api); }
      });
    }
    place(st, st.choresEl);
    const esc = (x) => api.esc(st, x);
    const done = c.tasks.filter((t) => t.n >= t.d.g).length;
    const rows = c.tasks.map((t) => {
      const ok = t.n >= t.d.g;
      return '<div class="clkch-row' + (ok ? ' done' : '') + '"><span class="clkch-ic">' + t.d.ic + '</span>'
        + '<div class="clkch-tx"><div>' + esc(t.d.tx) + '</div><div class="clkch-bar"><i style="width:' + Math.round(100 * t.n / t.d.g) + '%"></i></div></div>'
        + '<span class="clkch-n">' + (ok ? '✓' : t.n + '/' + t.d.g) + '</span></div>';
    }).join('');
    // Сім кружечків серії: пройдені залиті, наступний — кільцем, сьомий — корона.
    const next = c.taken ? 0 : c.streak + 1;
    let dots = '';
    for (let i = 1; i <= STREAK_MAX; i++) {
      dots += '<span class="clkch-dot' + (i <= c.streak ? ' on' : i === next ? ' next' : '') + (i === STREAK_MAX ? ' big' : '') + '" title="День '
        + i + (i === STREAK_MAX ? ' — велика скриня' : '') + '">' + (i === STREAK_MAX ? '👑' : '') + '</span>';
    }
    const offer = st.wish && Array.isArray(st.wish.offer) && st.wish.offer.length;
    const btn = c.taken
      ? '<button type="button" class="clkmd-coopbtn clkch-claim" disabled>✓ Скриню забрано — завтра нові доручення</button>'
      : '<button type="button" class="clkmd-coopbtn clkch-claim"' + (c.ready ? '' : ' disabled') + '>📦 Забрати скриню</button>';
    const html = api.foldHead('📜 Доручення Глека <span class="muted small">· ' + done + '/' + c.tasks.length + (c.streak ? ' · серія ' + c.streak : '') + '</span>')
      + '<div class="clkmd-body">' + rows
      + '<div class="clkch-streak" aria-label="Серія днів">' + dots + '<span class="muted small">серія днів, пропустиш — з першого</span></div>'
      + '<div class="small muted clkch-chest">' + esc(chestLine(st)) + '</div>' + btn
      + (offer ? '<button type="button" class="clkmd-coopbtn clkch-wishbtn">✨ Чарівний глек: обери бажання</button>' : '') + '</div>';
    api.swap(st.choresEl, html);
  }

  // ---------- v13 Г7: «✨ Глек з бажанням» (ClickerWish.cs) ----------
  //
  // view.chores[3] = { magic, offer: [{k, icon, name, text, can}], until, stoker, cat } (offer — лише поки вибір чекає). Світіння
  // чарівного глека на полиці — клас .magic на .clk-fall / .clk-fly (st.fallMagic, його читає paintFall в clicker.js).
  // Вікно: три картки; «Взяти собі» — wish { pick }, «🎁 Подарувати другові» — вибір друга з округи й wish { pick, gift }.

  function openWish(st, api) {
    const w = st.wish;
    if (!w || !Array.isArray(w.offer) || !w.offer.length || !st.ctx || !st.mine) return false;
    if (api.overlayOpen(st)) return false;
    const esc = (x) => api.esc(st, x);
    const left = w.until ? Math.max(0, Date.parse(w.until) - api.serverNow(st)) : 0;
    const hours = Math.ceil(left / 36e5);
    const cards = w.offer.map((c) => '<div class="clkw-card' + (c.can ? '' : ' off') + '">'
      + '<div class="clkw-ic">' + esc(c.icon) + '</div><div class="clkw-tx"><b>' + esc(c.name) + '</b><div class="small muted">' + esc(c.text) + '</div></div>'
      + '<div class="clkw-btns"><button type="button" class="primary" data-pick="' + esc(c.k) + '"' + (c.can ? '' : ' disabled title="Тобі це зараз нікуди прикласти"') + '>Взяти собі</button>'
      + '<button type="button" class="ghost" data-gift="' + esc(c.k) + '">🎁 Подарувати другові</button></div></div>').join('');
    const html = '<div class="clk-news clkw"><h3 style="padding-right:30px">✨ Глек з бажанням</h3>'
      + '<p class="small muted">Чарівний глек у твоїх руках! Обери одне бажання' + (hours ? ' — воно чекатиме ще ' + hours + ' год' : '') + '.</p>'
      + cards + '<div class="clkw-friends" hidden></div><div class="clkw-msg small" role="status"></div></div>';
    const body = api.overlay(st, html, { cls: 'clk-newsbox' });
    api.sfx('rare');
    const msg = body.querySelector('.clkw-msg');
    const box = body.querySelector('.clkw-friends');
    let busy = false;
    const send = (payload) => {
      if (busy) return;
      busy = true;
      api.act(st, 'wish', payload).then((r) => {
        busy = false;
        if (r && r.ok) { api.sfx('gift'); api.closeOverlay(st); } else if (msg) msg.textContent = (r && r.message) || 'Не вийшло — спробуй ще';
      });
    };
    body.addEventListener('click', (e) => {
      const pick = e.target.closest('[data-pick]');
      if (pick && !pick.disabled) { send({ pick: pick.dataset.pick }); return; }
      const gift = e.target.closest('[data-gift]');
      if (gift) { showFriends(st, api, box, gift.dataset.gift, send); return; }
      const f = e.target.closest('[data-friend]');
      if (f) send({ pick: f.dataset.pick, gift: f.dataset.friend });
    });
    return true;
  }

  /// Друзі для подарунка: гончарі з округи, крім мене (список цеху вже може бути в st.guildRoster; ні — питаємо).
  function showFriends(st, api, box, pick, send) {
    if (!box) return;
    const esc = (x) => api.esc(st, x);
    const draw = (list) => {
      box.hidden = false;
      box.innerHTML = list.length
        ? '<div class="small muted">Кому подарувати? Бажання спрацює в нього, щойно прийме, а ти отримаєш 10 хв гри.</div>'
          + list.map((p) => '<button type="button" class="ghost small" data-pick="' + esc(pick) + '" data-friend="' + esc(p.nick) + '">' + esc(p.nick) + '</button>').join(' ')
        : '<div class="small muted">Поки нікому дарувати: у цеху ще нема інших гончарів.</div>';
    };
    const mine = (st.ctx && st.ctx.me && st.ctx.me.nick) || '';
    const have = st.guildRoster && Array.isArray(st.guildRoster.potters) ? st.guildRoster.potters : null;
    const people = (d) => d.filter((p) => p && !p.me && p.nick && p.nick !== mine);
    if (have) { draw(people(have)); return; }
    box.hidden = false;
    box.textContent = 'Кличемо гончарів…';
    fetch('/api/games/clicker/guild', { headers: { 'X-Nick': encodeURIComponent(mine) } })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => draw(d && Array.isArray(d.potters) ? people(d.potters) : []))
      .catch(() => { box.textContent = 'Не вдалося покликати гончарів — спробуй ще.'; });
  }

  /// Вікно виринає само раз на пропозицію (ключ — її термін); закрив не вибравши — лишається кнопка в картці доручень.
  function maybeWish(st, api) {
    const w = st.wish;
    if (!w || !Array.isArray(w.offer) || !w.offer.length || st.wishShown === w.until) return;
    if (openWish(st, api)) st.wishShown = w.until;
  }

  window.HClicker.part({
    id: 'day',
    order: 64.5,

    mount(st) {
      st.day = null;
      st.dayEl = null;
      st.dayLeftEl = null;
      st.dayLeftKey = '';
      st.dayChip = null;
      st.dayKiln = null;
      st.dayRo = null;
      st.dayTab = 'm';
      st.week = null;
      st.weekBuffs = null;
      st.chores = null;
      st.choresEl = null;
      st.choresTrade = false;
      st.wish = null;
      st.wishShown = '';
      st.fallMagic = false;
    },

    update(st, v, api) {
      // v13 Г3, Г7: доручення Глека й чарівний глек (view.chores) — окрема картка в «Селі» та вікно бажань.
      parseChores(st, v, api);
      st.fallMagic = !!(st.wish && st.wish.magic);
      paintChores(st, api);
      maybeWish(st, api);
      // Старий сервер виду day не шле (undefined) — лишаємо, що було; null — Майстра дня вимкнено в конфігу.
      if (v.day !== undefined) {
        const d = v.day;
        const cat = (st.catalog && st.catalog.day) || {};
        const person = !!d && !d.guest;
        st.day = d && d.ends ? {
          // Кінець доби й «23:30» — раз на вид: відлік у slow кличеться чотири рази на секунду.
          day: d.day || '', ends: d.ends, endsMs: Date.parse(d.ends), at: closeAt(d.ends),
          // Призи, хвилини й пороги — з каталогу (catalog.day): вид летить щопачки кліків і їх не несе.
          prizes: Array.isArray(cat.prizes) ? cat.prizes : [], minPoints: cat.minPoints || 0, minPlayers: cat.minPlayers || 0,
          person, ...trackOf(d), feast: Array.isArray(cat.feast) ? cat.feast : [],
          // v13 Г2: «Ловець дня» — табло тієї самої форми; at/day/person ділить із «Майстром».
          catcher: d.catcher ? {
            day: d.day || '', at: closeAt(d.ends), person, ...trackOf(d.catcher),
            prizes: Array.isArray((cat.catcher || {}).prizes) ? cat.catcher.prizes : [],
            minutes: Array.isArray((cat.catcher || {}).minutes) ? cat.catcher.minutes : [],
            minPoints: (cat.catcher || {}).minPoints || 0, minPlayers: (cat.catcher || {}).minPlayers || 0,
          } : null,
        } : null;
      }
      // v13 Г4, Г5: тиждень — той самий принцип: undefined лишає, null — змагань нема.
      if (v.week !== undefined) {
        const w = v.week;
        const wcat = (st.catalog && st.catalog.week) || {};
        st.week = w && w.ends ? {
          ends: w.ends, endsMs: Date.parse(w.ends), at: closeAt(w.ends), person: !w.guest,
          // Призи, вітрила, поріг днів і відсоток бонусу замовлення — з каталогу (catalog.week); у виді лише табло.
          grow: w.grow ? growOf(w.grow, wcat) : null,
          coop: w.coop ? {
            ware: w.coop.ware, q: w.coop.q || 3, vol: w.coop.vol || 0, done: w.coop.done || 0, mine: w.coop.mine || 0, min: w.coop.min || 0, max: w.coop.max || 0,
            fin: !!w.coop.fin, pct: wcat.pct || 0, people: w.coop.people || 0,
          } : null,
        } : null;
      }
      if (v.buffs !== undefined) st.weekBuffs = (v.buffs && v.buffs.week) || null;
      const l = v.kiln && v.kiln.last;
      if (l && l.at) {
        const at = Date.parse(l.at);
        if (!st.dayKiln || st.dayKiln.at !== at) st.dayKiln = { at, pts: kilnPoints(l) };
      }
      paint(st, api);
      tick(st, api);
      markReveal(st, api);
    },

    slow(st, api) {
      maybeWish(st, api);
      if (!st.day && !(st.dayChip && !st.dayChip.hidden)) return;
      tick(st, api);
      markReveal(st, api);
    },

    unmount(st) {
      if (st.dayRo) { st.dayRo.disconnect(); st.dayRo = null; }
      if (st.dayChip) st.dayChip.remove();
      if (st.dayEl) st.dayEl.remove();
      if (st.choresEl) st.choresEl.remove();
      st.choresEl = null;
      st.chores = null;
      st.wish = null;
      st.fallMagic = false;
      st.dayChip = null;
      st.dayEl = null;
      st.dayLeftEl = null;
      st.day = null;
      st.week = null;
      st.weekBuffs = null;
    },
  });
})();
