/*
  «Майстер дня» Гончарного кола (v12, docs/games/specs/clicker-v12.md §5). Частина ядра clicker.js — пакет C.
  Табло дня в «Селі» й чип у рядку статусу кола. Дані — view.day (ClickerDay.cs): day, ends, prizes, minPoints,
  minPlayers, person, mine {place, points}, board [{place, nick, points, me}], players, last {day, line}.
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
  function place(st) {
    const pane = st.guildPane, el = st.dayEl;
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

  function boardHtml(st, api, d) {
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
      return '<div class="muted small">' + (d.person ? 'Сьогодні ще ніхто не набрав очок — перше дзвінке горно виведе тебе нагору.'
        : 'Сьогодні ще ніхто не набрав очок.') + '</div>';
    }
    const more = d.players - d.board.length - (outside ? 1 : 0);
    return '<ol class="clkmd-board">' + rows.join('') + '</ol>'
      + (more > 0 ? '<div class="muted small clkmd-more">і ще ' + more + ' ' + api.plural(more, 'майстер', 'майстри', 'майстрів') + ' нижче</div>' : '');
  }

  /// Призи й поріг одним рядком, з тим, скільки людей поріг уже взяли (з першої десятки — нижче за неї поріг і так не взяти).
  function prizeLine(st, api, d) {
    const prizes = d.prizes.filter((p) => p > 0);
    if (!prizes.length) return '';
    const list = prizes.map((p, i) => (MEDAL[i] || (i + 1) + '.') + ' ' + api.num(p)).join(' · ') + ' 🏺';
    const need = Math.max(1, d.minPlayers || 1);
    const min = d.minPoints || 0;
    const have = d.board.filter((r) => r.points >= min).length;
    const who = need <= 1 ? 'якщо набрати від ' + min + ' ' + pointsWord(api, min)
      : 'якщо від ' + min + ' ' + pointsWord(api, min) + ' наберуть хоча б ' + (FEW[need] || need + ' гравців');
    return '<div class="small clkmd-prize">Призи о ' + d.at + ': ' + list + ', ' + who
      + (need > 1 ? ' <span class="muted">(зараз — ' + have + ')</span>' : '') + '</div>';
  }

  function lastLine(st, api, d) {
    const l = d.last;
    if (!l || !l.line) return '';
    // У Балачках рядок починається з «🏆 Майстер дня в Гончарному колі:» — тут, під тим самим заголовком, це повтор.
    const text = String(l.line).replace(/^🏆\s*Майстер дня(?: в Гончарному колі)?:\s*/, '');
    const day = String(l.day || '');
    const when = day === dayBefore(d.day) ? 'Учора' : day.slice(8, 10) + '.' + day.slice(5, 7);
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
    }
    place(st);
    const head = d.person && d.mine ? ' <span class="muted small">· місце ' + d.mine.place + ' · ' + api.num(d.mine.points) + '</span>' : '';
    // Чуже коло (глядач): «твоє місце» тут — місце господаря, тож цього рядка глядачеві не пишемо.
    const me = !st.mine ? '' : '<div class="clkmd-me' + (d.person ? '' : ' guest') + '">' + (!d.person
      ? '<span>Табло — для тих, хто увійшов. Увійди, щоб змагатись.</span>'
      : d.mine ? '<span>Твоє місце: <b>' + d.mine.place + '</b> · <b>' + api.num(d.mine.points) + '</b> ' + pointsWord(api, d.mine.points) + '</span>'
        : '<span>Ти сьогодні ще без очок</span>')
      + '<span class="clkmd-left muted small"></span></div>';
    const html = api.foldHead('🏆 Майстер дня' + head)
      + '<div class="clkmd-body">' + me + boardHtml(st, api, d) + prizeLine(st, api, d)
      + '<div class="small muted clkmd-how">Як набирати: ручне горно — дзвінкий 1, розкішний 3; палій і підмайстри не рахуються.</div>'
      + lastLine(st, api, d) + '</div>';
    if (api.swap(st.dayEl, html) || !st.dayLeftEl) { st.dayLeftEl = st.dayEl.querySelector('.clkmd-left'); st.dayLeftKey = ''; }
  }

  /// Відлік — лише текстом і лише коли змінилась хвилина: усе інше в розділі живе з видом.
  function tick(st, api) {
    const d = st.day;
    const ms = d ? d.endsMs - api.serverNow(st) : 0;
    const at = d ? d.at : '';
    if (d && st.dayLeftEl) {
      const t = ms > 0 ? 'до ' + at + ' — ' + leftText(ms, true) : 'підбиваємо підсумок…';
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
    },

    update(st, v, api) {
      // Старий сервер виду day не шле (undefined) — лишаємо, що було; null — Майстра дня вимкнено в конфігу.
      if (v.day !== undefined) {
        const d = v.day;
        st.day = d && d.ends ? {
          // Кінець доби й «23:30» — раз на вид: відлік у slow кличеться чотири рази на секунду.
          day: d.day || '', ends: d.ends, endsMs: Date.parse(d.ends), at: closeAt(d.ends),
          prizes: Array.isArray(d.prizes) ? d.prizes : [], minPoints: d.minPoints || 0, minPlayers: d.minPlayers || 0,
          person: !!d.person, mine: d.mine || null, board: Array.isArray(d.board) ? d.board : [], players: d.players || 0,
          last: d.last || null,
        } : null;
      }
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
      if (!st.day && !(st.dayChip && !st.dayChip.hidden)) return;
      tick(st, api);
      markReveal(st, api);
    },

    unmount(st) {
      if (st.dayRo) { st.dayRo.disconnect(); st.dayRo = null; }
      if (st.dayChip) st.dayChip.remove();
      if (st.dayEl) st.dayEl.remove();
      st.dayChip = null;
      st.dayEl = null;
      st.dayLeftEl = null;
      st.day = null;
    },
  });
})();
