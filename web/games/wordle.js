/*
  Глек-слово. Правила, слово дня і оцінка спроб — на сервері (Impl/Wordle.cs); тут лише малювання
  дошки й наміри: набрав п'ять літер, натиснув Enter — пішов Act('guess', { word }).

  Вид із сервера: { day, no, rows: [{ word, marks }], attempts, max, solved, failed,
                    answer, keys: { 'а': 'G'|'Y'|'B' }, share, noWords }.
  marks — рядок із п'яти літер: G (на місці), Y (є, але не тут), B (нема).
  Дограний день показує серію й розподіл спроб — GET /api/games/wordle/stats (Impl/WordleSetup.cs).
*/
(() => {
  const LEN = 5;
  /// Довжина слова: щоденне — завжди п'ять, наввипередки — 4/5/6 (опція столу, v.len).
  const lenOf = (v) => (v && v.len) || LEN;
  const lettersSpelled = (n) => (n === 4 ? 'чотири літери' : n === 6 ? 'шість літер' : 'п\'ять літер');
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

  /// Фізична клавіша → українська літера (ЙЦУКЕН). Потрібно, коли в системі ввімкнена латинка чи російська:
  /// раніше «q» чи «ы» мовчки нічого не робили, і людина не розуміла, чому дошка не пише. Ґ — на клавіші «\»,
  /// як у розширеній українській розкладці Windows.
  const CODE_UA = {
    KeyQ: 'й', KeyW: 'ц', KeyE: 'у', KeyR: 'к', KeyT: 'е', KeyY: 'н', KeyU: 'г', KeyI: 'ш', KeyO: 'щ', KeyP: 'з',
    BracketLeft: 'х', BracketRight: 'ї', KeyA: 'ф', KeyS: 'і', KeyD: 'в', KeyF: 'а', KeyG: 'п', KeyH: 'р', KeyJ: 'о',
    KeyK: 'л', KeyL: 'д', Semicolon: 'ж', Quote: 'є', Backslash: 'ґ', KeyZ: 'я', KeyX: 'ч', KeyC: 'с', KeyV: 'м',
    KeyB: 'и', KeyN: 'т', KeyM: 'ь', Comma: 'б', Period: 'ю',
  };

  /// Літера з натиску: українська розкладка — як є, будь-яка інша — за місцем клавіші.
  function letterOf(e) {
    const ch = String(e.key || '').toLowerCase();
    if (ch.length !== 1) return '';
    if (ALPHABET.indexOf(ch) >= 0) return ch;
    return CODE_UA[e.code] || '';
  }

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
    const done = () => ctx.toast('Є! Скопійовано', 'ok');
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
    ctx.toast(ok ? 'Є! Скопійовано' : 'Халепа: не вийшло скопіювати', ok ? 'ok' : 'err');
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

  /// Дошка 6×N: відкриті ряди з кольорами, а в першому порожньому — те, що людина зараз набирає.
  /// hints — 💡 підказані літери: блідо стоять на своїх місцях у рядку, який зараз набирається.
  function drawBoard(host, ctx, st, rows, max, typing, len, hints) {
    const n = len || LEN;
    const hint = {};
    (hints || []).forEach((h) => { hint[h.i] = h.ch; });
    HGames.ui.grid(host, {
      cols: n,
      rows: max,
      cls: 'wtiles' + (n !== LEN ? ' wl' + n : ''),
      cell: (i) => {
        const r = Math.floor(i / n), c = i % n;
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
        if (!ch && drafting && hint[c]) return { html: ctx.esc(hint[c]), cls: 'whint', disabled: true };
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
    // день дограно — друкувати нікуди, а клавіатура штовхала серію й «Скопіювати» за нижній край телефона
    HGames.ui.keyboardUa(root, (k) => press(root, k), v.keys || {}).hidden = over(v);
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
      setHtml(el, '<div class="muted small">Словника на цьому сервері нема — сьогодні без слова.</div>');
      return;
    }
    if (!over(v)) {
      setHtml(el, '');
      return;
    }
    const race = raceInCatalog();
    wantStats(root, ctx);
    const html = statsHtml(state(root).stats, v)
      + (v.share ? '<button class="ghost" data-copy>Скопіювати результат</button>' : '')
      + '<div class="muted small wnext">' + ctx.esc(nextWordIn()) + '</div>'
      + (race ? '<button class="ghost wracego" data-race title="Інші слова, не слово дня">🏁 Ану ще слово — наввипередки з друзями</button>' : '');
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      const b = el.querySelector('[data-copy]');
      if (b) b.onclick = () => copy(v.share || '', ctx);
      const g = el.querySelector('[data-race]');
      if (g) g.onclick = () => openRace(ctx, g);
    }
  }

  /// Серія й розподіл спроб — раз на картку, коли день дограно. Не прийшло — підвал просто без них.
  function wantStats(root, ctx) {
    const st = state(root);
    if (st.stats || st.statsAsk || !ctx.mine || typeof fetch !== 'function') return;
    st.statsAsk = true;
    fetch('/api/games/wordle/stats', { credentials: 'same-origin' })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        if (root._wordle !== st) return;   // картку вже закрили
        st.stats = d || { none: true };
        if (st.ctx) paint(root, st.ctx);
      }, () => {});
  }

  /// «Серія 🔥 12 · найдовша 20» і стовпчики 1…6, як у класиці; сьогоднішній рядок підсвічено.
  /// Результат дня пишеться в базу подією, тож запит міг його випередити — тоді докладаємо сьогодні самі.
  function statsHtml(s, v) {
    if (!s || s.none) return '';
    const dist = (s.dist || []).slice(0, 6);
    while (dist.length < 6) dist.push(0);
    let streak = s.streak | 0, best = s.best | 0, solved = s.solved | 0;
    const today = v.solved ? v.attempts : 0;
    if (today && !s.today) {
      dist[today - 1]++;
      solved++;
      streak++;
      best = Math.max(best, streak);
    }
    if (!solved) return '';
    const top = Math.max(1, ...dist);
    const bars = dist.map((n, i) => '<div class="wdrow' + (today === i + 1 ? ' now' : '') + '"><span>' + (i + 1) + '</span>'
      + '<i style="width:' + Math.max(8, Math.round(n / top * 100)) + '%">' + n + '</i></div>').join('');
    return '<div class="wstats">'
      + '<div class="wstreak">Серія <b>🔥 ' + streak + '</b> · найдовша ' + best + ' · узято днів: ' + solved + '</div>'
      + '<div class="wdist" title="За скільки спроб узято слово дня">' + bars + '</div></div>';
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
    if (st.draft.length >= lenOf(v)) return false;
    st.draft += ch;
    st.repaint(root, ctx);
    return true;
  }

  function submit(root) {
    const st = state(root);
    const ctx = st.ctx;
    if (!ctx || st.sending || !st.draft) return false;
    const need = lenOf(ctx.view);
    if (st.draft.length < need) { shake(root); ctx.toast('Ану-но, треба ' + lettersSpelled(need), 'err'); return true; }
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
    const ch = letterOf(e);
    if (!ch) return false;
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
      if (v.solved) return 'Є! Слово дня взято за ' + tries(v.attempts);
      if (v.failed) return 'Слово було: ' + String(v.answer || '').toUpperCase();
      if (!ctx.mine) return 'Дивишся збоку';
      return 'Спроба ' + Math.min(v.attempts + 1, v.max) + ' з ' + v.max;
    },

    news: {
      v: '2026-09-29',
      title: 'Глек-слово: серія на картці',
      items: [
        '🔥 Дограв день — на картці серія, найдовша серія і стовпчики «за скільки спроб», як у класиці',
        '🏁 Наввипередки з друзями: хто вгадав — бачить чужі дошки з літерами, а ще спринт «кожен своє слово» і слова на 4 чи 6 літер',
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
  // самими кольорами (хто вже вгадав чи відмучився — з літерами). Вид: { phase: 'play'|'reveal'|'done',
  //   mode: 'same'|'sprint', len, target, hint, maxHints, winners (спринт), round, rounds, seconds, revealSeconds,
  //   endsAt, max, answer, answers, me: { rows, keys, solved, failed, attempts, hints: [{i, ch}], played } | null,
  //   players: [{ seat, nick, marks[], words[]|null, attempts, solved, failed, ms, first, gained, total, solvedWords,
  //   gone, hints, played, left }] }
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

  /// Мала дошка суперника: 6×N кольорових клітинок. Літери — коли раунд позаду або я своє вже відгадав
  /// (words від сервера).
  function mini(ctx, p, max, len) {
    const n = len || LEN;
    let cells = '';
    for (let r = 0; r < max; r++) {
      const m = (p.marks || [])[r] || '';
      const w = (p.words || [])[r] || '';
      for (let c = 0; c < n; c++) {
        const k = m[c];
        cells += '<i class="' + (k ? CLS[k] || 'b' : '') + '">' + (k && w ? ctx.esc(w[c] || '') : '') + '</i>';
      }
    }
    return '<div class="wrmini' + ((p.words || []).length ? ' open' : '') + '" style="--wn:' + n + '">' + cells + '</div>';
  }

  function badge(p, max, sprint) {
    if (p.gone) return '<span class="wrst gone">поза грою</span>';
    if (sprint) return '<span class="wrst' + (p.solvedWords ? ' ok' : '') + '">✓' + p.solvedWords + '</span> · ' + p.attempts + '/' + max;
    if (p.solved) return '<span class="wrst ok">✓ ' + p.attempts + '/' + max + (p.first ? ' ⚡' : '') + '</span>';
    if (p.failed) return '<span class="wrst bad">✗</span>';
    return '<span class="wrst">' + p.attempts + '/' + max + '</span>';
  }

  function rivals(host, ctx, v, st) {
    const el = part(host, 'wrrivals');
    const max = v.max || 6;
    const list = (v.players || []).filter((p) => p.seat !== ctx.seat);
    // По картці на суперника: спроба одного перемальовує лише його дошку, а не всі п'ять (на шістьох це
    // 2,1 мс на кожен вид проти ~0,5 — на телефоні вчетверо більше, а види летять на кожну чужу спробу).
    const keep = new Set();
    list.forEach((p, i) => {
      let c = el.querySelector(':scope > [data-seat="' + p.seat + '"]');
      if (!c) { c = document.createElement('div'); c.dataset.seat = p.seat; }
      if (el.children[i] !== c) el.insertBefore(c, el.children[i] || null);
      const cls = 'wrp' + (p.solved ? ' solved' : '') + (p.failed ? ' failed' : '') + (p.gone ? ' gone' : '') + (st.flash[p.seat] ? ' flash' : '');
      if (c.className !== cls) c.className = cls;
      const sprint = v.mode === 'sprint';
      setHtml(c, mini(ctx, p, max, lenOf(v))
        + '<div class="wrpname"><b title="' + ctx.esc(p.nick || '') + '">' + ctx.esc(p.nick || '—') + '</b>'
        + (p.hints ? '<span class="wrst" title="Узяв підказок">💡' + p.hints + '</span>' : '') + '</div>'
        + (sprint
          ? '<div class="wrptot" title="Вгадано слів і спроби над теперішнім">' + badge(p, max, true) + '</div>'
          : '<div class="wrptot" title="Спроби й очки за партію">' + badge(p, max) + ' · ' + points(p.total)
            + (p.gained && v.phase !== 'play' ? ' <em>+' + p.gained + '</em>' : '') + '</div>'));
      keep.add(c);
    });
    [...el.children].forEach((c) => { if (!keep.has(c)) c.remove(); });
  }

  /// Шапка: раунд, дуга часу й мої очки.
  function raceHead(host, ctx, v, st) {
    const el = part(host, 'wrhead');
    const me = (v.players || []).find((p) => p.seat === ctx.seat);
    const text = part(el, 'wrround', 'b');
    const sprint = v.mode === 'sprint';
    const t = v.phase === 'done' ? 'Партію зіграно'
      : sprint ? '🏃 Спринт до ' + v.target + ' слів'
      : 'Раунд ' + v.round + ' з ' + v.rounds + (v.phase === 'reveal' ? ' · зараз нове слово' : '');
    if (text.textContent !== t) text.textContent = t;
    const mine = part(el, 'wrmine', 'span');
    const m = !me ? '' : sprint ? 'у тебе ' + me.solvedWords + ' з ' + v.target : 'у тебе ' + points(me.total);
    if (mine.textContent !== m) mine.textContent = m;
    if (v.phase !== 'done' && v.endsAt) {
      const whole = v.phase === 'reveal' ? v.revealSeconds : sprint ? v.seconds * (v.target || 1) : v.seconds;
      st.arc = HGames.ui.timerArc(el, v.endsAt, whole * 1000);
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
    if (v.mode === 'sprint') { sprintFoot(el, ctx, v, players); return; }
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

  /// Спринт позаду: хто скільки вгадав, його слова (✓/✗) і що лишилось недогаданим.
  function sprintFoot(el, ctx, v, players) {
    const win = new Set(v.winners || []);
    players.sort((a, b) => (win.has(b.seat) - win.has(a.seat)) || b.solvedWords - a.solvedWords);
    const rows = players.map((p, i) => {
      const words = (p.played || []).map((x) => '<span class="' + (x.ok ? 'ok' : 'miss') + '">' + (x.ok ? '✓ ' : '✗ ')
        + ctx.esc(String(x.w).toUpperCase()) + '</span>').join(' ')
        + (p.left ? ' <span class="left" title="Не встиг">… ' + ctx.esc(String(p.left).toUpperCase()) + '</span>' : '');
      return '<div class="wrrow' + (p.seat === ctx.seat ? ' me' : '') + (p.gone ? ' gone' : '') + '">'
        + '<span class="n">' + (win.has(p.seat) ? '🏆' : (i + 1) + '.') + '</span>'
        + '<span class="nick">' + ctx.esc(p.nick || '—') + '<small class="wrwords">' + words + '</small></span>'
        + '<b>' + p.solvedWords + ' з ' + v.target + '</b></div>';
    }).join('');
    setHtml(el, '<div class="wrtable">' + rows + '</div>');
  }

  /// Під моєю дошкою: 💡 підказка (коли стіл її дозволив) і в спринті — мої вже зіграні слова.
  function raceTools(mine, ctx, v, me) {
    const el = part(mine, 'wrtools');
    const kb = mine.querySelector(':scope > .gkbd');
    if (kb && el.nextSibling !== kb) mine.insertBefore(el, kb);
    let html = '';
    if (v.phase === 'play' && v.hint && !me.solved && !me.failed) {
      const left = (v.maxHints || 0) - (me.hints || []).length;
      html += '<button class="ghost wrhint" data-hint' + (left > 0 ? '' : ' disabled')
        + ' title="Відкриває одну літеру на своєму місці; з виграшу за слово — мінус очко">💡 Літера за очко'
        + (left > 0 ? ' <small>(ще ' + left + ')</small>' : ' <small>(усе)</small>') + '</button>';
    }
    if (v.mode === 'sprint' && (me.played || []).length) {
      html += '<div class="wrplayed">' + me.played.map((x) => '<span class="' + (x.ok ? 'ok' : 'miss') + '">'
        + (x.ok ? '✓ ' : '✗ ') + ctx.esc(String(x.w).toUpperCase()) + '</span>').join('') + '</div>';
    }
    if (el.dataset.sig !== html) {
      el.dataset.sig = html;
      el.innerHTML = html;
      const b = el.querySelector('[data-hint]');
      if (b) b.onclick = () => {
        b.disabled = true;
        ctx.act('hint', {}).then((r) => { if (!(r && r.ok)) b.disabled = false; }, () => { b.disabled = false; });
      };
    }
    el.hidden = !html;
    mine.classList.toggle('wtools', !!html);
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
    // у спринті нове слово приходить кожному своє — ключ і на кількість уже зіграних моїх слів
    const key = (ctx.room ? ctx.room.round : 0) + ':' + v.round + (v.mode === 'sprint' && me ? ':' + (me.played || []).length : '');
    let fresh = false;
    if (st.round !== key) {
      st.flipFrom = st.round == null ? rows.length : 0;
      if (st.round != null) st.draft = '';
      const sameMatch = st.round != null && st.round.split(':')[0] === key.split(':')[0] && v.mode === 'sprint';
      st.round = key;
      if (!sameMatch) st.solvedSeen = {};
      fresh = true;
    }
    // Хтось щойно вгадав — його дошка на мить спалахує: без літер це єдиний спосіб помітити, що суперник уже все.
    (v.players || []).forEach((p) => {
      // у спринті «вгадав» — це ще одне слово в копилці, а не прапорець раунду
      const got = v.mode === 'sprint' ? p.solvedWords : (p.solved ? 1 : 0);
      if (got > (st.solvedSeen[p.seat] || 0)) {
        st.solvedSeen[p.seat] = got;
        if (st.primed && p.seat !== ctx.seat) {
          st.flash[p.seat] = true;
          setTimeout(() => { delete st.flash[p.seat]; if (st.ctx) paintRace(root, st.ctx); }, 1200);
        }
      }
    });
    st.primed = true;

    const wrap = part(root, 'wr');
    // Своє вгадав (чи відмучився), а раунд ще йде — друкувати нікуди, зате є на що дивитись: чужі дошки вже з
    // літерами. Клавіатура ховається, а суперники стають великими картками, як у глядача (на телефоні малі
    // картки по 60 px літер не вміщають).
    const peeking = !!(me && v.phase === 'play' && v.mode !== 'sprint' && (me.solved || me.failed));
    wrap.classList.toggle('spect', !me || peeking);
    // Партію зіграно — підсумкова таблиця піднімається під шапку (wordle.css): на 1280×800 вона ховалась
    // під порожньою дошкою й клавіатурою, нижче згину.
    wrap.classList.toggle('done', v.phase === 'done');
    if (!v.phase || v.phase === 'lobby') {
      // до старту — правила: без них новачок бачить порожню картку і не розуміє, у що сідає
      if (st.arc) { st.arc.stop(); st.arc = null; }
      setHtml(wrap, rulesHtml(ctx));
      return;
    }
    if (wrap.querySelector(':scope > .wrrules')) { wrap.innerHTML = ''; delete wrap.dataset.sig; }
    raceHead(wrap, ctx, v, st);
    const main = part(wrap, 'wrmain');
    rivals(main, ctx, v, st);
    const mine = part(main, 'wrme');
    if (me) {
      drawBoard(mine, ctx, st, rows, v.max || 6, !raceLocked(v), lenOf(v), me.hints);
      // підказана літера на клавіатурі — зелена: вона точно є і вже відомо де
      const keys = Object.assign({}, me.keys || {});
      (me.hints || []).forEach((h) => { keys[h.ch] = 'G'; });
      // Між раундами й після партії друкувати нікуди — клавіатура лише штовхала слово раунду й таблицю
      // під нижній край (на телефоні — за екран).
      HGames.ui.keyboardUa(mine, (k) => press(root, k), keys).hidden = v.phase !== 'play' || peeking;
      raceTools(mine, ctx, v, me);
      mine.hidden = false;
    } else {
      mine.hidden = true;
    }
    raceFoot(wrap, ctx, v);
    if (fresh && me && v.phase === 'play') raceInView(wrap);
  }

  /// Правила до старту — під опції столу: без них новачок бачить порожню картку і не розуміє, у що сідає.
  function rulesHtml(ctx) {
    const o = (ctx.room && ctx.room.options) || {};
    const n = +o.len || LEN;
    const letters = lettersSpelled(n).replace('\'', '’');
    const time = ctx.esc(secsLabel(o.seconds));
    const li = [];
    if (o.mode === 'sprint') {
      li.push('🏃 Спринт: у кожного своє слово на ' + letters + ', шість спроб на кожне.');
      li.push('Вгадав — одразу наступне слово; шість промахів — теж наступне, але не зараховане.');
      li.push('Хто перший вгадає 3 слова — переміг. На все про все — ' + ctx.esc(secsLabel((+o.seconds || 180) * 3))
        + '; вийде час — перемагає, у кого більше слів.');
      li.push('🟩 літера на місці, 🟨 є, але не тут, ⬛ нема зовсім. Суперників видно кольорами, а слова — після партії.');
    } else {
      li.push('Слово одне на всіх — ' + letters + ', у кожного шість спроб.');
      li.push('🟩 літера на місці, 🟨 є, але не тут, ⬛ нема зовсім.');
      li.push('Чужі спроби видно кольорами, а вгадав (чи спроби скінчились) — бачиш їх уже з літерами.');
      li.push('Вгадав з першої — 6 очок, з шостої — 1; хто вгадав першим, бере ще +1.');
      if (o.hint === 'on') li.push('💡 Підказка відкриває літеру на своєму місці — мінус очко з виграшу (до двох на слово).');
      li.push('Раундів: ' + (o.rounds || 3) + ', на слово — ' + time + '.');
    }
    return '<div class="wrrules"><b>Як грати</b><ul><li>' + li.join('</li><li>') + '</li></ul>'
      + '<div class="muted small">Господар тисне «Почати», коли всі сіли. Грати можна вдвох — і до шести.</div></div>';
  }

  /// Новий раунд — моя дошка й клавіатура мають бути в полі зору. На телефоні з п'ятьма суперниками дошка
  /// починалась нижче згину, і друкувати доводилось наосліп або прокручуючи туди-сюди. Раз на раунд і лише коли
  /// низ клавіатури справді схований під нижніми вкладками: підкручуємо рівно настільки, щоб він виринув, але не
  /// далі, ніж шапка раунду (з таймером) доїде до шапки сайту.
  function raceInView(wrap) {
    requestAnimationFrame(() => {
      const head = wrap.querySelector('.wrhead');
      const kbd = wrap.querySelector('.wrme:not([hidden]) .gkbd');
      if (!head || !kbd || !kbd.offsetParent) return;
      const cs = getComputedStyle(document.documentElement);
      const bars = parseFloat(cs.getPropertyValue('--tabs-h')) || 0;
      const site = document.querySelector('header');
      const top = site ? Math.max(0, site.getBoundingClientRect().bottom) : 0;
      const over = kbd.getBoundingClientRect().bottom - (innerHeight - bars - 8);
      const room = head.getBoundingClientRect().top - top - 6;
      const dy = Math.min(over, room);
      if (over > 1 && dy > 1) {
        const calm = matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
        window.scrollBy({ top: dy, behavior: calm ? 'auto' : 'smooth' });
      }
    });
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
      if (v.phase === 'done' && v.mode === 'sprint') {
        const win = (v.players || []).filter((p) => (v.winners || []).indexOf(p.seat) >= 0);
        if (!win.length) return 'Нічия: слова перемогли всіх';
        const yes = win.some((p) => p.seat === ctx.seat && ctx.seat != null) ? 'Є! ' : '';
        return yes + '🏆 ' + win.map((p) => p.nick).join(' і ') + ' — ' + win[0].solvedWords + ' з ' + v.target;
      }
      if (v.phase === 'done') {
        const ps = (v.players || []).filter((p) => !p.gone);
        const best = Math.max(0, ...ps.map((p) => p.total));
        const win = best > 0 ? ps.filter((p) => p.total === best) : [];
        const yes = win.some((p) => p.seat === ctx.seat && ctx.seat != null) ? 'Є! ' : '';
        return win.length ? yes + '🏆 ' + win.map((p) => p.nick).join(' і ') + ' — ' + points(best) : 'Нічия: слова перемогли всіх';
      }
      if (v.phase === 'reveal') return 'Раунд ' + (v.round + 1) + ' з ' + v.rounds + ' — за кілька секунд';
      if (!v.me) return 'Дивишся збоку: літер не видно, лише кольори';
      if (v.mode === 'sprint') {
        const mp = (v.players || []).find((p) => p.seat === ctx.seat);
        return 'Слово №' + ((v.me.played || []).length + 1) + ' · вгадано ' + (mp ? mp.solvedWords : 0) + ' з ' + v.target
          + ' · спроба ' + Math.min(v.me.attempts + 1, v.max) + ' з ' + v.max;
      }
      if (v.me.solved) return 'Є! Вгадано — тепер тобі видно чужі літери';
      if (v.me.failed) return 'Спроби скінчились — зате видно чужі літери';
      return 'Спроба ' + Math.min(v.me.attempts + 1, v.max) + ' з ' + v.max + ' · слово в усіх те саме';
    },

    unmount(root, ctx) {
      const st = root._wordle;
      if (st && st.arc) st.arc.stop();
      if (ctx && ctx.room) delete roots[ctx.room.id];
      root._wordle = null;
    },

    news: {
      v: '2026-09-29',
      title: 'Глек-слово наввипередки: спринт і чужі літери',
      items: [
        '👀 Вгадав (чи спроби скінчились) — бачиш чужі дошки вже з літерами: дивись, як сусід утретє пише ГРОЗА по-різному',
        '🏃 Режим «Спринт»: у кожного своє слово, хто перший вгадає три — переміг. Удвох — чистий азарт без чекання',
        '🔤 Опція «Довжина слова»: 4, 5 чи 6 літер',
        '💡 Опція «Підказки»: літера на своєму місці за очко з виграшу',
      ],
    },
  });
})();
