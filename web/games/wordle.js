/*
  Глек-слово. Правила, слово дня і оцінка спроб — на сервері (Impl/Wordle.cs); тут лише малювання
  дошки й наміри: набрав п'ять літер, натиснув Enter — пішов Act('guess', { word }).

  Вид із сервера: { day, no, rows: [{ word, marks }], attempts, max, solved, failed,
                    answer, keys: { 'а': 'G'|'Y'|'B' }, share, noWords }.
  marks — рядок із п'яти літер: G (на місці), Y (є, але не тут), B (нема).
*/
(() => {
  const LEN = 5;
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1" y="2.4" width="4.2" height="4.2" rx="1.2" fill="var(--ok)"/>'
    + '<rect x="5.9" y="2.4" width="4.2" height="4.2" rx="1.2" fill="none" stroke="var(--muted)" stroke-width="1.2"/>'
    + '<rect x="10.8" y="2.4" width="4.2" height="4.2" rx="1.2" fill="none" stroke="var(--muted)" stroke-width="1.2"/>'
    + '<rect x="1" y="9.4" width="4.2" height="4.2" rx="1.2" fill="none" stroke="var(--muted)" stroke-width="1.2"/>'
    + '<rect x="5.9" y="9.4" width="4.2" height="4.2" rx="1.2" fill="var(--accent)"/>'
    + '<rect x="10.8" y="9.4" width="4.2" height="4.2" rx="1.2" fill="none" stroke="var(--muted)" stroke-width="1.2"/></svg>';

  /// Українська абетка без апострофа: рівно те, що приймає Words.Normalize на сервері.
  const ALPHABET = 'абвгґдеєжзиіїйклмнопрстуфхцчшщьюя';
  const CLS = { G: 'g', Y: 'y', B: 'b' };

  /// «за 1 спробу», «за 3 спроби», «за 6 спроб» — інакше рядок статусу читається як телеграма.
  function tries(n) {
    const t = n % 100, o = n % 10;
    if (t > 10 && t < 20) return n + ' спроб';
    if (o === 1) return n + ' спробу';
    if (o >= 2 && o <= 4) return n + ' спроби';
    return n + ' спроб';
  }

  /// Скільки лишилось до київської півночі. Зона береться з браузера; якщо він її не знає
  /// (старий движок без бази зон) — рахуємо як UTC+3, це та сама підстраховка, що й на сервері.
  function msToMidnight() {
    const now = new Date();
    let h, m, s;
    try {
      const parts = new Intl.DateTimeFormat('en-GB', {
        timeZone: 'Europe/Kyiv', hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit',
      }).formatToParts(now);
      const at = (type) => +((parts.find((p) => p.type === type) || {}).value || 0);
      h = at('hour') % 24; m = at('minute'); s = at('second');
    } catch (e) {
      const k = new Date(now.getTime() + 3 * 3600e3);
      h = k.getUTCHours(); m = k.getUTCMinutes(); s = k.getUTCSeconds();
    }
    return (24 * 3600 - (h * 3600 + m * 60 + s)) * 1000;
  }

  function nextWordIn() {
    const left = msToMidnight();
    const h = Math.floor(left / 3600e3);
    const m = Math.floor((left % 3600e3) / 60e3);
    return 'Наступне слово через ' + (h ? h + ' год ' + m + ' хв' : Math.max(1, m) + ' хв');
  }

  /// Копіювання без clipboard API теж має працювати: сайт відкривають і по локальній адресі,
  /// а там navigator.clipboard браузер не дає.
  function copy(text, ctx) {
    const done = () => ctx.toast('Скопійовано', 'ok');
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done, () => fallback(text, ctx));
      return;
    }
    fallback(text, ctx);
  }
  function fallback(text, ctx) {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.opacity = '0';
    document.body.appendChild(ta);
    ta.select();
    let ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    ta.remove();
    ctx.toast(ok ? 'Скопійовано' : 'Не вийшло скопіювати', ok ? 'ok' : 'err');
  }

  /// Тіло картки за id кімнати: onKey приходить від каркаса з ctx, а не з DOM, і корінь треба звідкись узяти.
  const roots = {};

  function state(root) {
    // flipFrom — з якого ряду починається переворот; клас віддаємо самій сітці, а не чіпляємо
    // поверх неї (див. paint)
    if (!root._wordle) {
      // locked(v) — чи можна зараз набирати; repaint — чим перемалювати картку. Щоденна гра і гра
      // наввипередки ділять увесь ввід (набір, Enter, хитання), а малюють кожна своє.
      root._wordle = { draft: '', flipFrom: null, bad: false, sending: false, timer: 0, ctx: null,
        locked: (v) => !!(v.noWords || over(v)), repaint: (r, c) => paint(r, c) };
    }
    return root._wordle;
  }

  const over = (v) => !!(v && (v.solved || v.failed));

  // ------------------------------------------------------------------------------------ малювання

  /// Дошка 6×5: відкриті ряди з кольорами, а в першому порожньому — те, що людина зараз набирає.
  function drawBoard(host, ctx, st, rows, max, typing) {
    HGames.ui.grid(host, {
      cols: LEN,
      rows: max,
      cls: 'wtiles',
      cell: (i) => {
        const r = Math.floor(i / LEN), c = i % LEN;
        const row = rows[r];
        if (row) {
          return {
            html: ctx.esc(row.word[c] || ''),
            cls: (CLS[(row.marks || '')[c]] || 'b') + (r >= st.flipFrom ? ' flip' : ''),
            disabled: true,
          };
        }
        const drafting = r === rows.length && typing;
        const ch = drafting ? st.draft[c] : '';
        return { html: ctx.esc(ch || ''), cls: (ch ? 'typed' : '') + (drafting && st.bad ? ' bad' : ''), disabled: true };
      },
    });
  }

  function paint(root, ctx) {
    const st = state(root);
    st.ctx = ctx;
    if (ctx.room) roots[ctx.room.id] = root;
    const v = ctx.view || {};
    const rows = v.rows || [];
    const max = v.max || 6;
    // перший малюнок нічого не перевертає: інакше після F5 уся дошка робила б сальто. Усе, що
    // відкрилось уже на наших очах, переворот дістає — і лишає клас назавжди: анімація одноразова, а
    // ui.grid переписує className, коли той не збігається з бажаним, тож клас, доданий поверх сітки,
    // злітав би з першої ж наступної набраної літери й обривав переворот на середині.
    if (st.flipFrom === null) st.flipFrom = rows.length;

    drawBoard(root, ctx, st, rows, max, !over(v) && ctx.mine);
    HGames.ui.keyboardUa(root, (k) => press(root, k), v.keys || {});
    foot(root, ctx, v);
  }

  /// Підвал картки: коли день дограно — «Скопіювати результат» і скільки чекати нового слова.
  function foot(root, ctx, v) {
    let el = root.querySelector(':scope > .wfoot');
    if (!el) {
      el = document.createElement('div');
      el.className = 'wfoot';
      root.appendChild(el);
    }
    if (v.noWords) {
      const html = '<div class="muted small">Словника на цьому сервері нема — сьогодні без слова.</div>';
      if (el.innerHTML !== html) el.innerHTML = html;
      return;
    }
    if (!over(v)) {
      if (el.innerHTML !== '') el.innerHTML = '';
      return;
    }
    const race = raceInCatalog();
    const html = (v.share ? '<button class="ghost" data-copy>Скопіювати результат</button>' : '')
      + '<div class="muted small wnext">' + ctx.esc(nextWordIn()) + '</div>'
      + (race ? '<button class="ghost wracego" data-race title="Інші слова, не слово дня">🏁 Ще слово — наввипередки з друзями</button>' : '');
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      const b = el.querySelector('[data-copy]');
      if (b) b.onclick = () => copy(v.share || '', ctx);
      const g = el.querySelector('[data-race]');
      if (g) g.onclick = () => openRace(ctx, g);
    }
  }

  const raceInCatalog = () => ((HGames.catalog && HGames.catalog.games) || []).some((g) => g.id === 'wordle-race');

  /// Дограв слово дня — одразу стіл наввипередки: хочеться ще, а щоденне дає одне слово на добу.
  function openRace(ctx, btn) {
    btn.disabled = true;
    // відмову («ти вже за столом») каркас покаже тостом сам
    HGames.call('CreateRoom', 'wordle-race', {}).then((r) => {
      btn.disabled = false;
      if (r && r.ok && r.roomId) location.hash = '#games/room/' + encodeURIComponent(r.roomId);
    }, () => { btn.disabled = false; });
  }

  // -------------------------------------------------------------------------------------- ввід

  /// Повертає true, лише якщо клавіша справді щось зробила: за цим каркас вирішує, гасити подію чи ні
  /// (див. onKey), а гасити зайве не можна — Enter на дограному дні має тиснути «Скопіювати результат».
  function press(root, key) {
    const st = state(root);
    const ctx = st.ctx;
    if (!ctx || !ctx.mine) return false;
    const v = ctx.view || {};
    if (st.locked(v)) return false;

    if (key === 'Enter') return submit(root);
    if (key === 'Backspace') {
      if (!st.draft) return false;
      st.draft = st.draft.slice(0, -1);
      st.repaint(root, ctx);
      return true;
    }
    const ch = String(key || '').toLowerCase();
    if (ch.length !== 1 || ALPHABET.indexOf(ch) < 0) return false;
    if (st.draft.length >= LEN) return false;
    st.draft += ch;
    st.repaint(root, ctx);
    return true;
  }

  function submit(root) {
    const st = state(root);
    const ctx = st.ctx;
    if (!ctx || st.sending || !st.draft) return false;
    if (st.draft.length < LEN) { shake(root); ctx.toast('Треба п\'ять літер', 'err'); return true; }
    const word = st.draft;
    st.sending = true;
    ctx.act('guess', { word }).then((r) => {
      st.sending = false;
      if (r && r.ok) st.draft = '';
      else shake(root);
      st.repaint(root, st.ctx || ctx);
    }, () => { st.sending = false; });
    return true;
  }

  function shake(root) {
    const st = state(root);
    if (st.bad) return;
    st.bad = true;
    st.repaint(root, st.ctx);
    setTimeout(() => {
      st.bad = false;
      if (st.ctx) st.repaint(root, st.ctx);
    }, 420);
  }

  // ---------------------------------------------------------------------------------- реєстрація

  function onKey(e, ctx) {
    // літери читаємо з e.key, а не з e.code: розкладка тут і є змістом гри
    const root = ctx.room && roots[ctx.room.id];
    if (!root || !ctx.mine) return false;
    // віддаємо рівно те, що сталось: на true каркас робить preventDefault, а він гасить і Enter
    // на сфокусованій кнопці картки
    if (e.key === 'Enter' || e.key === 'Backspace') return press(root, e.key);
    const ch = String(e.key || '').toLowerCase();
    if (ch.length !== 1 || ALPHABET.indexOf(ch) < 0) return false;
    return press(root, ch);
  }

  HGames.register({
    id: 'wordle',
    icon: ICON,
    seatNames: ['слово'],
    seatClass: ['x'],

    mount(root, ctx) {
      const st = state(root);
      st.flipFrom = null;   // те, що вже стоїть на дошці, після F5 сальто не робить
      paint(root, ctx);
      // відлік до нового слова тікає сам; хвилини вистачає — година й хвилини й так змінюються повільно
      st.timer = setInterval(() => {
        const el = root.querySelector(':scope > .wfoot .wnext');
        if (el) el.textContent = nextWordIn();
      }, 30000);
    },

    update(root, ctx) { paint(root, ctx); },

    onKey,

    status(ctx) {
      const v = ctx.view;
      if (!v) return '';
      if (v.noWords) return 'Словника нема — сьогодні без слова';
      if (v.solved) return 'Слово дня взято за ' + tries(v.attempts);
      if (v.failed) return 'Слово було: ' + String(v.answer || '').toUpperCase();
      if (!ctx.mine) return 'Дивишся збоку';
      return 'Спроба ' + Math.min(v.attempts + 1, v.max) + ' з ' + v.max;
    },

    news: {
      v: '2026-09-24',
      title: 'Глек-слово: тепер і наввипередки',
      items: [
        '🏁 Нова гра в «Компанії» — «Глек-слово наввипередки»: одне слово на всіх, від двох до шести гравців',
        '🟩 Суперників видно кольорами без літер, очки — за швидкість і за менше спроб',
        '🫙 Слово дня лишається як було — одне на добу, серія не зламається',
      ],
    },

    unmount(root, ctx) {
      const st = root._wordle;
      if (st && st.timer) clearInterval(st.timer);
      if (ctx && ctx.room) delete roots[ctx.room.id];
      root._wordle = null;
    },
  });
  // =================================================================================================
  // Глек-слово наввипередки (Impl/WordleRace.cs). Те саме слово для всіх за столом, чужі спроби —
  // самими кольорами. Вид: { phase: 'play'|'reveal'|'done', round, rounds, seconds, revealSeconds,
  //   endsAt, max, answer, answers, me: { rows, keys, solved, failed, attempts } | null,
  //   players: [{ seat, nick, marks[], words[]|null, attempts, solved, failed, ms, first, gained, total, solvedWords, gone }] }
  // =================================================================================================

  const RACE_ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1" y="2" width="3.4" height="3.4" rx="1" fill="var(--ok)"/>'
    + '<rect x="5" y="2" width="3.4" height="3.4" rx="1" fill="var(--ok)"/>'
    + '<rect x="9" y="2" width="3.4" height="3.4" rx="1" fill="var(--ok)"/>'
    + '<rect x="1" y="6.8" width="3.4" height="3.4" rx="1" fill="var(--accent)"/>'
    + '<rect x="5" y="6.8" width="3.4" height="3.4" rx="1.7" fill="var(--accent)"/>'
    + '<path d="M13.2 1.5v13M13.2 2h2.3v3.2h-2.3" fill="none" stroke="var(--muted)" stroke-width="1.1"/>'
    + '<rect x="1" y="11.6" width="3.4" height="3.4" rx="1" fill="none" stroke="var(--muted)" stroke-width="1"/></svg>';

  const MEDALS = ['🥇', '🥈', '🥉'];

  /// «7 очок», «1 очко», «3 очки».
  function points(n) {
    const t = Math.abs(n) % 100, o = Math.abs(n) % 10;
    if (t > 10 && t < 20) return n + ' очок';
    if (o === 1) return n + ' очко';
    if (o >= 2 && o <= 4) return n + ' очки';
    return n + ' очок';
  }

  function secsText(ms) {
    if (ms == null) return '';
    const s = Math.round(ms / 1000);
    return s < 60 ? s + ' с' : Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  function secsLabel(raw) {
    const n = +raw || 180;
    return n % 60 ? Math.floor(n / 60) + '½ хв' : n / 60 + ' хв';
  }

  const raceLocked = (v) => !(v && v.phase === 'play' && v.me && !v.me.solved && !v.me.failed);

  /// Одна вкладена частина картки — створюємо раз і далі лише наповнюємо.
  function part(host, cls, tag) {
    let el = host.querySelector(':scope > .' + cls);
    if (!el) {
      el = document.createElement(tag || 'div');
      el.className = cls;
      host.appendChild(el);
    }
    return el;
  }

  function setHtml(el, html) {
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
  }

  /// Мала дошка суперника: 6×5 кольорових клітинок. Літери — лише коли раунд позаду (words від сервера).
  function mini(ctx, p, max) {
    let cells = '';
    for (let r = 0; r < max; r++) {
      const m = (p.marks || [])[r] || '';
      const w = (p.words || [])[r] || '';
      for (let c = 0; c < LEN; c++) {
        const k = m[c];
        cells += '<i class="' + (k ? CLS[k] || 'b' : '') + '">' + (k && w ? ctx.esc(w[c] || '') : '') + '</i>';
      }
    }
    return '<div class="wrmini' + ((p.words || []).length ? ' open' : '') + '">' + cells + '</div>';
  }

  function badge(p, max) {
    if (p.gone) return '<span class="wrst gone">пішов</span>';
    if (p.solved) return '<span class="wrst ok">✓ ' + p.attempts + '/' + max + (p.first ? ' ⚡' : '') + '</span>';
    if (p.failed) return '<span class="wrst bad">✗</span>';
    return '<span class="wrst">' + p.attempts + '/' + max + '</span>';
  }

  function rivals(host, ctx, v, st) {
    const el = part(host, 'wrrivals');
    const max = v.max || 6;
    const list = (v.players || []).filter((p) => p.seat !== ctx.seat);
    const html = list.map((p) => '<div class="wrp' + (p.solved ? ' solved' : '') + (p.failed ? ' failed' : '')
      + (p.gone ? ' gone' : '') + (st.flash[p.seat] ? ' flash' : '') + '" data-seat="' + p.seat + '">'
      + mini(ctx, p, max)
      + '<div class="wrpname"><b title="' + ctx.esc(p.nick || '') + '">' + ctx.esc(p.nick || '—') + '</b></div>'
      + '<div class="wrptot" title="Спроби й очки за партію">' + badge(p, max) + ' · ' + points(p.total)
      + (p.gained && v.phase !== 'play' ? ' <em>+' + p.gained + '</em>' : '') + '</div>'
      + '</div>').join('');
    setHtml(el, html);
  }

  /// Шапка: раунд, дуга часу й мої очки.
  function raceHead(host, ctx, v, st) {
    const el = part(host, 'wrhead');
    const me = (v.players || []).find((p) => p.seat === ctx.seat);
    const text = part(el, 'wrround', 'b');
    const t = v.phase === 'done' ? 'Партію зіграно' : 'Раунд ' + v.round + ' з ' + v.rounds
      + (v.phase === 'reveal' ? ' · зараз нове слово' : '');
    if (text.textContent !== t) text.textContent = t;
    const mine = part(el, 'wrmine', 'span');
    const m = me ? 'у тебе ' + points(me.total) : '';
    if (mine.textContent !== m) mine.textContent = m;
    if (v.phase !== 'done' && v.endsAt) {
      st.arc = HGames.ui.timerArc(el, v.endsAt, (v.phase === 'reveal' ? v.revealSeconds : v.seconds) * 1000);
    } else if (st.arc) {
      st.arc.stop();
      st.arc.el.remove();
      st.arc = null;
    }
  }

  /// Підвал: між раундами — слово і хто скільки взяв; наприкінці — таблиця партії.
  function raceFoot(host, ctx, v) {
    const el = part(host, 'wrfoot');
    const players = (v.players || []).slice();
    if (v.phase === 'play') { setHtml(el, ''); return; }
    const word = '<div class="wrword"><span>Слово було:</span> '
      + String(v.answer || '').split('').map((ch) => '<i>' + ctx.esc(ch) + '</i>').join('') + '</div>';
    if (v.phase === 'reveal') {
      const got = players.filter((p) => p.gained > 0).sort((a, b) => b.gained - a.gained);
      setHtml(el, word + '<div class="wrgot">' + (got.length
        ? got.map((p) => '<span class="chip">' + ctx.esc(p.nick || '') + ' <b>+' + p.gained + '</b>'
          + (p.first ? ' ⚡ ' + secsText(p.ms) : '') + '</span>').join('')
        : '<span class="muted">Цього разу слово не далось нікому</span>') + '</div>');
      return;
    }
    players.sort((a, b) => b.total - a.total || b.solvedWords - a.solvedWords);
    const best = players.length ? players[0].total : 0;
    const rows = players.map((p) => {
      const place = 1 + players.filter((x) => x.total > p.total).length;
      return '<div class="wrrow' + (p.seat === ctx.seat ? ' me' : '') + (p.gone ? ' gone' : '') + '">'
        + '<span class="n">' + (best > 0 && place <= 3 ? MEDALS[place - 1] : place + '.') + '</span>'
        + '<span class="nick">' + ctx.esc(p.nick || '—') + '</span>'
        + '<span class="muted small">слів: ' + p.solvedWords + ' з ' + v.rounds + '</span>'
        + '<b>' + points(p.total) + '</b></div>';
    }).join('');
    const words = (v.answers || []).length > 1
      ? '<div class="muted small">Слова партії: ' + v.answers.map((w) => ctx.esc(String(w).toUpperCase())).join(' · ') + '</div>' : '';
    setHtml(el, word + '<div class="wrtable">' + rows + '</div>' + words);
  }

  function paintRace(root, ctx) {
    const st = state(root);
    st.ctx = ctx;
    st.locked = raceLocked;
    st.repaint = paintRace;
    if (!st.flash) st.flash = {};
    if (ctx.room) roots[ctx.room.id] = root;
    const v = ctx.view || {};
    const me = v.me;
    const rows = (me && me.rows) || [];

    // Новий раунд — чиста чернетка, і все, що відкриється, перевертається. Перший малюнок (F5 посеред
    // раунду) не перевертає нічого, як і в щоденному.
    // «Ще раз» знову починає з раунду 1 — тож ключ раунду включає й номер партії за столом
    const key = (ctx.room ? ctx.room.round : 0) + ':' + v.round;
    if (st.round !== key) {
      st.flipFrom = st.round == null ? rows.length : 0;
      if (st.round != null) st.draft = '';
      st.round = key;
      st.solvedSeen = {};
    }
    // Хтось щойно вгадав — його дошка на мить спалахує: без літер це єдиний спосіб помітити, що суперник уже все.
    (v.players || []).forEach((p) => {
      if (p.solved && !st.solvedSeen[p.seat]) {
        st.solvedSeen[p.seat] = true;
        if (st.primed && p.seat !== ctx.seat) {
          st.flash[p.seat] = true;
          setTimeout(() => { delete st.flash[p.seat]; if (st.ctx) paintRace(root, st.ctx); }, 1200);
        }
      }
    });
    st.primed = true;

    const wrap = part(root, 'wr');
    wrap.classList.toggle('spect', !me);
    if (!v.phase || v.phase === 'lobby') {
      // до старту — правила: без них новачок бачить порожню картку і не розуміє, у що сідає
      if (st.arc) { st.arc.stop(); st.arc = null; }
      setHtml(wrap, '<div class="wrrules"><b>Як грати</b><ul>'
        + '<li>Слово одне на всіх — п’ять літер, у кожного шість спроб.</li>'
        + '<li>🟩 літера на місці, 🟨 є, але не тут, ⬛ нема зовсім.</li>'
        + '<li>Чужі спроби видно самими кольорами, без літер — видно, хто вже близько.</li>'
        + '<li>Вгадав з першої — 6 очок, з шостої — 1; хто вгадав першим, бере ще +1.</li>'
        + '<li>Раундів: ' + (ctx.room && ctx.room.options && ctx.room.options.rounds || 3) + ', на слово — '
        + ctx.esc(secsLabel(ctx.room && ctx.room.options && ctx.room.options.seconds)) + '.</li></ul>'
        + '<div class="muted small">Господар тисне «Почати», коли всі сіли. Грати можна вдвох — і до шести.</div></div>');
      return;
    }
    if (wrap.querySelector(':scope > .wrrules')) { wrap.innerHTML = ''; delete wrap.dataset.sig; }
    raceHead(wrap, ctx, v, st);
    const main = part(wrap, 'wrmain');
    rivals(main, ctx, v, st);
    const mine = part(main, 'wrme');
    if (me) {
      drawBoard(mine, ctx, st, rows, v.max || 6, !raceLocked(v));
      HGames.ui.keyboardUa(mine, (k) => press(root, k), me.keys || {});
      mine.hidden = false;
    } else {
      mine.hidden = true;
    }
    raceFoot(wrap, ctx, v);
  }

  HGames.register({
    id: 'wordle-race',
    icon: RACE_ICON,
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o'],

    mount(root, ctx) {
      const st = state(root);
      st.flipFrom = null;
      st.round = null;
      st.primed = false;
      paintRace(root, ctx);
    },

    update(root, ctx) { paintRace(root, ctx); },

    onKey,

    status(ctx) {
      const v = ctx.view;
      if (!v || !v.phase) return '';
      if (v.phase === 'lobby') return 'Чекаємо, поки господар натисне «Почати»';
      if (v.phase === 'done') {
        const ps = (v.players || []).filter((p) => !p.gone);
        const best = Math.max(0, ...ps.map((p) => p.total));
        const win = best > 0 ? ps.filter((p) => p.total === best) : [];
        return win.length ? '🏆 ' + win.map((p) => p.nick).join(' і ') + ' — ' + points(best) : 'Нічия: слова перемогли всіх';
      }
      if (v.phase === 'reveal') return 'Раунд ' + (v.round + 1) + ' з ' + v.rounds + ' — за кілька секунд';
      if (!v.me) return 'Дивишся збоку: літер не видно, лише кольори';
      if (v.me.solved) return 'Вгадав! Дивись, як мучаться інші';
      if (v.me.failed) return 'Спроби скінчились — чекаємо на інших';
      return 'Спроба ' + Math.min(v.me.attempts + 1, v.max) + ' з ' + v.max + ' · слово в усіх те саме';
    },

    unmount(root, ctx) {
      const st = root._wordle;
      if (st && st.arc) st.arc.stop();
      if (ctx && ctx.room) delete roots[ctx.room.id];
      root._wordle = null;
    },

    news: {
      v: '2026-09-24',
      title: '🏁 Глек-слово наввипередки',
      items: [
        '👥 Одне слово на всіх за столом — від двох до шести гравців',
        '🟩 Чужі спроби видно кольорами, але без літер: видно, хто вже близько',
        '⚡ Менше спроб — більше очок, а перший, хто вгадав, бере ще +1',
        '🔁 Кілька раундів поспіль і таймер на слово — обираєш, коли ставиш стіл',
      ],
    },
  });
})();
