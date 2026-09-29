/*
  Шахи: класика, шахи Фішера (960) і піддавки — одна дошка на три варіанти, бо правила живуть на сервері,
  а тут лише рендер і наміри. Клацнув фігуру — сервер уже прислав список її легальних ходів у view.legal,
  тож підсвітити їх можна не рахуючи нічого: жодних правил у браузері нема і бути не повинно.

  Вид із сервера (Impl/Chess.cs):
    { variant, board: 64 символи a8..h1, fen, turn, toMove, legal: [{from,to,promo,castle}],
      lastMove, check, captured: {w,b}, moves: SAN[], halfmove, fullmove, drawOffer, result }
  Хід: act('move', { from: 'e2', to: 'e4', promo?: 'q', castle?: 'K'|'Q' }).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M8 1.2v3.2M6.4 2.6h3.2" stroke="var(--accent)" stroke-width="1.5" stroke-linecap="round" fill="none"/>'
    + '<path d="M8 4.4 4.6 7.9c0 1.9 1.3 3 1.3 3h4.2s1.3-1.1 1.3-3L8 4.4z" fill="var(--accent)"/>'
    + '<path d="M4.6 11.6h6.8l.8 2.6H3.8l.8-2.6z" fill="var(--ok)"/></svg>';

  // Суцільні гліфи беремо для обох кольорів: контурні ♔ на темному тлі просто зникають, а колір
  // і обведення роблять різницю між білими й чорними видною навіть на телефоні.
  const GLYPH = { k: '♚', q: '♛', r: '♜', b: '♝', n: '♞', p: '♟' };
  const NAMES = { q: 'ферзь', r: 'тура', b: 'слон', n: 'кінь', k: 'король' };
  const FILES = 'abcdefgh';

  const nameOf = (sq) => FILES[sq % 8] + (8 - (sq / 8 | 0));
  const sqOf = (nm) => (8 - +nm[1]) * 8 + FILES.indexOf(nm[0]);
  const SLIDE_MS = 260;
  const reduced = () => window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  /// Чим скінчилась партія — людськими словами (view.result.reason). Каркас сам пише лише «Перемога: X».
  const REASON = {
    mate: 'Мат!', stalemate: 'Пат — нічия', resign: 'Хтось здався', material: 'Нічия: матувати нічим',
    fifty: 'Нічия: 50 ходів без взяття й без пішаків', repetition: 'Нічия: тричі та сама позиція',
    agreed: 'Нічия за згодою', left: 'Хтось встав з-за столу', time: 'Упав прапорець — час вийшов',
    'time-material': 'Прапорець упав, але матувати нічим — нічия',
    'anti-nopieces': 'Піддавки: фігур не лишилось — це перемога', 'anti-nomoves': 'Піддавки: ходити нічим — це перемога',
    solved: 'Мат! Задачу розв\'язано', reveal: 'Розв\'язок підглянуто — ключ підсвічено',
  };
  /// Шахи з Глеком і задача дня малюються цим самим модулем (Client: "chess" у ChessGlek.cs / ChessDaily.cs).
  const kindOf = (ctx) => (ctx.room && ctx.room.game === 'chess-glek' ? 'glek' : ctx.room && ctx.room.game === 'chess-daily' ? 'daily' : 'table');
  const LEVELS = [['easy', '🙂 Легкий'], ['medium', '🧐 Середній']];
  const COLORS = [['turn', '⇄ По черзі'], ['white', '♔ Білими'], ['black', '♚ Чорними']];
  const VARIANTS = [['classic', 'Класика'], ['960', 'Фішера'], ['anti', 'Піддавки']];
  const PREFS = 'chessGlekPrefs';
  const loadPrefs = () => { try { return JSON.parse(localStorage.getItem(PREFS) || 'null'); } catch { return null; } };
  const savePrefs = (p) => { try { localStorage.setItem(PREFS, JSON.stringify(p)); } catch { /* приватне вікно */ } };
  const dark = (sq) => ((sq % 8) + (sq / 8 | 0)) % 2 === 1;

  function state(root) {
    // resign — не прапорець, а FEN тієї позиції, у якій кнопку звели: щойно на дошці щось змінилось,
    // перепитування знімається саме собою. Інакше один випадковий клік лишав би кнопку зведеною до кінця
    // партії, і через десять ходів наступний дотик віддав би її без жодного питання.
    if (!root._chess) root._chess = { sel: null, promo: null, resign: null, lastKey: undefined, slideUntil: 0 };
    return root._chess;
  }

  /// Куди поставити елемент, якого ще нема. Порядок задає перший малюнок: смуга — дошка — решта.
  function ensure(root, cls, tag) {
    let el = root.querySelector(':scope > ' + cls.trim().split(/\s+/).map((c) => '.' + c).join(''));
    if (!el) {
      el = document.createElement(tag || 'div');
      el.className = cls;
      root.appendChild(el);
    }
    return el;
  }

  /// Порівнюємо з тим рядком, що клали самі: innerHTML браузер серіалізує по-своєму (&#39; → ', data-x → data-x=""),
  /// і порівняння з ним майже ніколи не каже «однаково» — DOM перебудовувався б на кожен вид.
  const setHtml = (el, html) => { if (el._h !== html) { el._h = html; el.innerHTML = html; } };

  /// Поле короля того, хто зараз ходить — щоб підсвітити шах червоним.
  function kingSquare(board, toMove) {
    const want = toMove === 'b' ? 'k' : 'K';
    return board.indexOf(want);
  }

  function pieces(str, cls) {
    if (!str) return '<span class="muted small">—</span>';
    return [...str].map((ch) => '<i class="' + cls + '">' + GLYPH[ch.toLowerCase()] + '</i>').join('');
  }

  function movesHtml(sans) {
    let html = '';
    for (let i = 0; i < sans.length; i += 2) {
      html += '<span><b>' + (i / 2 + 1) + '.</b> ' + sans[i] + (sans[i + 1] ? ' ' + sans[i + 1] : '') + '</span>';
    }
    return html;
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const board = v.board || '.'.repeat(64);
    const legal = v.legal || [];
    // Дошку повертають лише чорні; глядач і білі дивляться однаково — знизу білі. З Глеком і в задачі
    // колір людини каже сам вид (me): людина там завжди на місці 0, а грати може й чорними.
    const flip = v.me ? v.me === 'b' && ctx.seat === 0 : ctx.seat === 1;
    const at = (i) => (flip ? 63 - i : i);

    // Куди можна піти з вибраної фігури. Рокіровка приходить двома записами (поле тури й поле короля),
    // тож клацнути можна по будь-якому з них.
    const targets = {};
    if (st.sel) for (const m of legal) if (m.from === st.sel && m.to !== m.from) (targets[m.to] = targets[m.to] || []).push(m);

    const last = v.lastMove || null;
    // Задачу закрито — ключовий хід підсвічено, щоб було видно, як треба було.
    const key = v.puzzle && v.puzzle.key;
    const checkSq = v.check ? kingSquare(board, v.toMove) : -1;
    // Фігура, що щойно походила, доїжджає зі свого старого поля. Першу картинку не анімуємо.
    const lastKey = last ? last.from + last.to + (v.moves || []).length : '';
    if (st.lastKey !== undefined && last && lastKey !== st.lastKey && !reduced()) st.slideUntil = performance.now() + SLIDE_MS;
    st.lastKey = lastKey;
    let slide = null;
    if (last && performance.now() < st.slideUntil) {
      const a = sqOf(last.from), b = sqOf(last.to);
      const da = flip ? 63 - a : a, db = flip ? 63 - b : b;
      slide = { to: last.to, dx: (da % 8) - (db % 8), dy: (da >> 3) - (db >> 3) };
    }

    const cap = v.captured || { w: '', b: '' };
    // Смугу «що я взяв» малюємо з мого боку дошки, а чужу — навпроти.
    setHtml(ensure(root, 'chesscap top'), pieces(flip ? cap.w : cap.b, flip ? 'bp' : 'wp'));

    HGames.ui.grid(root, {
      cols: 8,
      rows: 8,
      cls: 'chessb',
      cell: (i) => {
        const sq = at(i);
        const ch = board[sq] || '.';
        const nm = nameOf(sq);
        const cls = [dark(sq) ? 'dk' : 'lt'];
        if (ch !== '.') cls.push(ch === ch.toUpperCase() ? 'wp' : 'bp');
        if (nm === st.sel) cls.push('sel');
        if (last && (nm === last.from || nm === last.to)) cls.push('lm');
        if (sq === checkSq) cls.push('chk');
        if (targets[nm]) cls.push(ch === '.' ? 'dot' : 'cap');
        if (key && (nm === key.from || nm === key.to)) cls.push('chs-key');
        // Координати по краю дошки: букви в нижньому ряду, цифри в лівому стовпчику (з боку того, хто дивиться).
        let co = '';
        if (i >= 56) co += '<span class="co f">' + nm[0] + '</span>';
        if (i % 8 === 0) co += '<span class="co r">' + nm[1] + '</span>';
        let pc = '';
        if (ch !== '.') {
          const mv = slide && slide.to === nm ? ' slide" style="--dx:' + slide.dx + ';--dy:' + slide.dy : '';
          pc = '<span class="pc' + mv + '">' + GLYPH[ch.toLowerCase()] + '</span>';
        }
        return {
          html: pc + co,
          cls: cls.join(' '),
          disabled: !ctx.myTurn,
        };
      },
      onCell: (i) => tap(root, ctx, nameOf(at(i))),
    });

    const capBottom = ensure(root, 'chesscap bottom');
    setHtml(capBottom, pieces(flip ? cap.b : cap.w, flip ? 'wp' : 'bp'));
    clockPlates(root, ctx, root.querySelector(':scope > .chesscap.top'), capBottom, flip ? 0 : 1);
    promoBar(root, ctx);
    // Список ходів вищий за своє віконце вже з десятого ходу, тож після кожного нового ходу дотягуємо
    // прокрутку донизу: цікавий рівно останній рядок, а не початок партії.
    const mv = ensure(root, 'chessmoves');
    const moves = movesHtml(v.moves || []);
    if (mv._h !== moves) {
      setHtml(mv, moves);
      // Прокрутку — у наступному кадрі: читати scrollHeight одразу після нового вмісту означало синхронну
      // розкладку всієї сторінки посеред update — ≈2 мс на кожен хід (заміряно 28.09), а в кадрі вона й так буде.
      cancelAnimationFrame(mv._raf);
      mv._raf = requestAnimationFrame(() => { mv.scrollTop = mv.scrollHeight; });
    }
    buttons(root, ctx);
  }

  /// Клац по клітинці: або хід (якщо фігуру вже вибрано і туди можна), або новий вибір.
  function tap(root, ctx, nm) {
    const st = state(root);
    if (st.promo) return;                       // спершу скажи, у кого перетворити
    const legal = (ctx.view && ctx.view.legal) || [];
    const here = st.sel ? legal.filter((m) => m.from === st.sel && m.to === nm && m.to !== m.from) : [];

    if (here.length) {
      const promos = here.filter((m) => m.promo);
      if (promos.length > 1) {
        st.promo = { from: st.sel, to: nm, opts: promos };
        st.sel = null;
      } else {
        const payload = { from: st.sel, to: nm };
        // Рокіровку називаємо словом: у 960 пара полів сама по собі буває неоднозначною, а з castle
        // серверу нема чого вгадувати. Ставимо тільки тоді, коли на це поле інших сенсів нема.
        if (here.every((m) => m.castle)) payload.castle = here[0].castle;
        if (promos.length === 1) payload.promo = promos[0].promo;
        st.sel = null;
        ctx.act('move', payload);
      }
    } else if (legal.some((m) => m.from === nm)) {
      st.sel = nm;
    } else {
      st.sel = null;
    }
    paint(root, ctx);
  }

  function promoBar(root, ctx) {
    const st = state(root);
    const el = ensure(root, 'chesspromo');
    if (!st.promo) { setHtml(el, ''); el.hidden = true; return; }
    el.hidden = false;
    setHtml(el, '<span class="muted small">У кого перетворити?</span>'
      + st.promo.opts.map((m) => '<button type="button" data-p="' + m.promo + '" title="' + NAMES[m.promo] + '">'
        + GLYPH[m.promo] + '</button>').join(''));
    el.querySelectorAll('button').forEach((b) => b.onclick = () => {
      const want = st.promo;
      st.promo = null;
      paint(root, ctx);
      ctx.act('move', { from: want.from, to: want.to, promo: b.dataset.p });
    });
  }

  function buttons(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const el = ensure(root, 'chessacts');
    const kind = kindOf(ctx);
    if (kind === 'glek') { glekBar(root, ctx, el); return; }
    if (kind === 'daily') { dailyBar(root, ctx, el); return; }
    if (!ctx.mine || !ctx.playing) {
      st.resign = null;
      let res = v.result && REASON[v.result.reason];
      if (v.result && v.result.reason === 'resign' && v.result.winner != null) {
        res = '🏳 ' + ctx.esc(ctx.nickOf(1 - v.result.winner) || ctx.seatName(1 - v.result.winner)) + ' здається';
      }
      setHtml(el, (res && ctx.room.status === 'finished' ? '<span class="chessres">' + res + '</span>' : '') + seriesHtml(ctx));
      return;
    }
    const fen = v.fen || '';
    const armed = st.resign !== null && st.resign === fen;   // звели в цій самій позиції — питання ще живе

    const offer = v.drawOffer;
    let html;
    if (offer != null && offer !== ctx.seat) {
      st.resign = null;                       // кнопки зникли — зведене питання разом із ними
      html = '<span class="muted small">Пропонують нічию</span>'
        + '<button type="button" class="primary" data-act="draw">Згода</button>'
        + '<button type="button" class="ghost" data-act="decline">Ні</button>';
    } else {
      html = '<button type="button" class="ghost" data-act="draw"' + (offer === ctx.seat ? ' disabled' : '') + '>'
        + (offer === ctx.seat ? 'Нічию запропоновано' : 'Нічия?') + '</button>'
        // Здатись з одного кліку — надто легко втратити партію мізинцем: питаємо ще раз.
        + '<button type="button" class="ghost danger" data-act="' + (armed ? 'resign' : 'ask') + '">'
        + (armed ? 'Точно здатись?' : 'Здатись') + '</button>';
    }
    setHtml(el, html + seriesHtml(ctx));
    el.querySelectorAll('button').forEach((b) => b.onclick = () => {
      if (b.dataset.act === 'ask') { st.resign = fen; paint(root, ctx); return; }
      st.resign = null;
      ctx.act(b.dataset.act);
    });
  }

  // ---- Шахи з Глеком ----------------------------------------------------------------------------
  // Рівень, колір і варіант — прямо під дошкою: соло відкривається без лобі, тож опцій столу тут нема.
  // Будь-який вибір — нова партія (дія set); обране пам'ятаємо в браузері й наступного разу ставимо самі.

  function chips(name, list, cur) {
    return '<span class="chs-chips" role="group">' + list.map(([k, t]) => '<button type="button" class="ghost' + (k === cur ? ' on' : '')
      + '" data-set="' + name + '" data-v="' + k + '"' + (k === cur ? ' aria-pressed="true"' : '') + '>' + t + '</button>').join('') + '</span>';
  }

  function glekBar(root, ctx, el) {
    const v = ctx.view || {};
    const st = state(root);
    const g = v.glek || {};
    const fen = v.fen || '';
    const armed = st.resign !== null && st.resign === fen;
    const over = ctx.room.status === 'finished';
    let top;
    if (over) {
      const r = v.result || {};
      const who = r.winner === 0 ? '🎉 Глека обіграно! ' : r.winner === 1 ? '🤖 Глек узяв гору. ' : '';
      top = '<span class="chessres">' + who + (r.reason === 'resign' ? '' : REASON[r.reason] || '') + '</span>';
    } else {
      top = '<button type="button" class="ghost danger" data-act="' + (armed ? 'resign' : 'ask') + '">' + (armed ? 'Точно здатись?' : 'Здатись') + '</button>';
    }
    // На телефоні три ряди чіпів (по 44 px) виштовхували «Закрити» під нижнє меню — там вони згорнуті
    // в «⚙️» (розгорнуто — пам'ятаємо до кінця показу); на ПК, як і були, відкриті.
    if (st.setOpen == null) st.setOpen = !(HGames.ui.coarse && HGames.ui.coarse());
    const lvl = (LEVELS.find(([k]) => k === g.level) || [])[1] || '';
    const html = top + '<details class="chs-set"' + (st.setOpen ? ' open' : '') + '><summary>⚙️ Налаштування'
      + (lvl ? ' <span class="muted">· ' + lvl + '</span>' : '') + '</summary>'
      + '<div class="chs-glek">' + chips('level', LEVELS, g.level) + chips('color', COLORS, g.color)
      + chips('variant', VARIANTS, v.variant) + '</div>'
      + (over ? '<span class="muted small">Обране діє з наступної партії — тисни «Ану ще раз»</span>'
        : '<span class="muted small">Зміна — нова партія. Без рейтингу й черепків.</span>') + '</details>';
    setHtml(el, html);
    const det = el.querySelector('details.chs-set');
    if (det) det.ontoggle = () => { st.setOpen = det.open; };
    el.querySelectorAll('button').forEach((b) => b.onclick = () => {
      if (b.dataset.act === 'ask') { st.resign = fen; paint(root, ctx); return; }
      if (b.dataset.act) { st.resign = null; ctx.act(b.dataset.act); return; }
      const p = { level: g.level, color: g.color, variant: v.variant };
      p[b.dataset.set] = b.dataset.v;
      savePrefs(p);
      if (!over) ctx.act('set', { [b.dataset.set]: b.dataset.v });
      else ctx.toast('Запам\'ятав — так і зіграємо наступну партію');
    });
  }

  /// Відкрили стіл з Глеком уперше за цей показ — поставити те, що людина обирала минулого разу (якщо партія ще не йде).
  function glekPrefs(root, ctx) {
    const st = state(root);
    if (kindOf(ctx) !== 'glek' || !ctx.playing || st.prefsRound === ctx.room.round) return;
    st.prefsRound = ctx.room.round;
    const p = loadPrefs();
    const v = ctx.view || {};
    const g = v.glek || {};
    if (!p || (v.moves || []).length > 1) return;
    const diff = {};
    if (p.level && p.level !== g.level) diff.level = p.level;
    if (p.color && p.color !== g.color) diff.color = p.color;
    if (p.variant && p.variant !== v.variant) diff.variant = p.variant;
    if (Object.keys(diff).length) ctx.act('set', diff);
  }

  // ---- Задача дня -------------------------------------------------------------------------------

  function mmss(ms) {
    const s = Math.max(0, Math.round(ms / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  function dailyBar(root, ctx, el) {
    const v = ctx.view || {};
    const st = state(root);
    const p = v.puzzle || {};
    const armed = st.reveal === p.attempts;
    let html = '';
    if (p.solved) html += '<span class="chessres">🏆 Мат! ' + mmss(p.ms) + (p.attempts > 1 ? ' · спроб ' + p.attempts : ' з першої спроби') + '</span>';
    else if (p.gaveUp) html += '<span class="chessres">👀 Ключ: <b>' + ctx.esc((p.key && p.key.san) || '') + '</b> — завтра нова задача</span>';
    else html += '<button type="button" class="ghost" data-act="' + (armed ? 'reveal' : 'ask') + '">'
      + (armed ? 'Точно підглянути? Результату не буде' : '🏳 Показати розв\'язок') + '</button>';
    if (p.solved && p.key) html += '<span class="muted small">Ключ: <b>' + ctx.esc(p.key.san) + '</b></span>';
    html += dayBoardHtml(ctx);
    setHtml(el, html);
    el.querySelectorAll('button').forEach((b) => b.onclick = () => {
      if (b.dataset.act === 'ask') { st.reveal = p.attempts; paint(root, ctx); return; }
      st.reveal = null;
      ctx.act(b.dataset.act);
    });
  }

  /// Табло дня: хто розв'язав, за скільки й з якої спроби, 🔥 — днів поспіль (DailyCard, як у Сапера дня).
  function dayBoardHtml(ctx) {
    const b = (ctx.view || {}).daily;
    if (!b) return '';
    const me = String((ctx.nickOf && ctx.nickOf(0)) || '').trim().toLowerCase();
    const rows = b.rows || [];
    const fire = (n) => (n >= 2 ? ' <span title="Днів поспіль">🔥' + n + '</span>' : '');
    const cell = (r, i) => '<span class="chs-dr' + ((r.n || '').trim().toLowerCase() === me ? ' me' : '') + '">' + (i === 0 ? '🥇 ' : '')
      + '<i>' + ctx.esc(r.n) + '</i> ' + mmss(r.ms) + (r.a > 1 ? '<small> ×' + r.a + '</small>' : '') + fire(r.st) + '</span>';
    let html = rows.length ? '<b>Сьогодні:</b> ' + rows.slice(0, 6).map(cell).join(' · ') + (rows.length > 6 ? ' <small>і ще ' + (rows.length - 6) + '</small>' : '')
      : 'Сьогодні ще ніхто не розв\'язав — будь першим';
    const wait = (b.wait || []).filter((w) => (w.n || '').trim().toLowerCase() !== me);
    if (wait.length) html += '<br><b>Ще не пробували:</b> ' + wait.map((w) => '<i>' + ctx.esc(w.n) + '</i>' + fire(w.st)).join(', ');
    return '<div class="chs-day">' + html + '</div>';
  }

  /// Смуга над дошкою: номер задачі, «мат у N», хто починає, час і спроба. Цокає раз на секунду, лише поки задача відкрита.
  function puzzleHead(root, ctx) {
    const v = ctx.view || {};
    const p = v.puzzle;
    let el = root.querySelector(':scope > .chs-puz');
    const st = state(root);
    clearInterval(st.puzTimer);
    st.puzTimer = 0;
    if (!p) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'chs-puz';
      root.insertBefore(el, root.firstChild);
    }
    const over = p.solved || p.gaveUp;
    // Не той хід: сервер скидає дошку й додає спробу. Тост про це легко проґавити (на телефоні — тим паче),
    // тож кажемо прямо над дошкою, поки не зробиш наступного ходу, і коротко струшуємо дошку.
    if (st.seenAtt != null && p.attempts > st.seenAtt && !over) {
      st.missAtt = p.attempts;
      const bd = root.querySelector('.board.chessb');
      if (bd) { bd.classList.remove('chs-shake'); void bd.offsetWidth; bd.classList.add('chs-shake'); setTimeout(() => bd.classList.remove('chs-shake'), 400); }
    }
    st.seenAtt = p.attempts;
    const miss = !over && st.missAtt === p.attempts && p.left === p.n
      ? '<br><span class="chs-miss">✗ Не той хід — Глек викрутився, дошка знову на початку</span>' : '';
    const k = p.startedAt + '|' + p.attempts;
    if (st.puzKey !== k) { st.puzKey = k; st.puzAt = performance.now() - (p.elapsedMs || 0); }
    const draw = () => {
      const ms = over ? p.ms || p.elapsedMs : performance.now() - st.puzAt;
      setHtml(el, '🧩 <b>Задача №' + p.no + '</b> · мат у ' + p.n + ' · ' + (v.me === 'b' ? 'ходять чорні' : 'ходять білі')
        + (over ? '' : ' · ходів лишилось: <b>' + p.left + '</b>')
        + ' · <span class="chs-t">⏱ ' + mmss(ms) + '</span>' + (p.attempts > 1 ? ' · спроба ' + p.attempts : '')
        + (v.streak >= 2 ? ' · 🔥' + v.streak : '') + miss);
    };
    draw();
    if (!over && ctx.playing) st.puzTimer = setInterval(() => { if (!document.hidden && root._chess) draw(); }, 1000);
  }

  // ---- годинник ---------------------------------------------------------------------------------
  // Той самий шматок живе і в checkers.js: спільного файла для двох модулів каркас не вантажить.
  // Сервер шле, скільки в кого лишилось на момент виду (clock.ms) і чий час іде (clock.running);
  // решту відлічуємо тут самі. Коли в когось упав прапорець, браузер каже серверу flag — той звіряє
  // зі своїм годинником. Суперник заявляє одразу, сам прострочений — трохи згодом (раптом суперник пішов).

  function fmtMs(ms) {
    ms = Math.max(0, ms);
    if (ms < 10000) return (Math.floor(ms / 100) / 10).toFixed(1);
    const s = Math.ceil(ms / 1000);
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }

  function clockPlates(root, ctx, topAnchor, bottomAnchor, topSeat) {
    const c = (ctx.view || {}).clock;
    const st = root._clk || (root._clk = { key: '', base: null, at: 0, timer: 0, flagged: '' });
    if (!c) {
      root.querySelectorAll(':scope > .chs-clock').forEach((el) => el.remove());
      clearInterval(st.timer);
      st.timer = 0;
      return;
    }
    const key = JSON.stringify(c);
    if (key !== st.key) { st.key = key; st.base = c; st.at = performance.now(); }
    for (const [anchor, pos, seat] of [[topAnchor, 'top', topSeat], [bottomAnchor, 'bottom', 1 - topSeat]]) {
      let el = root.querySelector(':scope > .chs-clock.' + pos);
      if (!el) {
        el = document.createElement('div');
        el.className = 'chs-clock ' + pos;
        anchor.insertAdjacentElement(pos === 'top' ? 'beforebegin' : 'afterend', el);
      }
      el.dataset.seat = String(seat);
    }
    tickClock(root, ctx);
    // Цокає лише посеред партії: після кінця стрілки стоять, і таймер на 200 мс нема чого тримати.
    if (ctx.playing && c.running != null) { if (!st.timer) st.timer = setInterval(() => { if (!document.hidden) tickClock(root, ctx); }, 200); }
    else if (st.timer) { clearInterval(st.timer); st.timer = 0; }
  }

  function tickClock(root, ctx) {
    const st = root._clk;
    if (!st || !st.base) return;
    const c = st.base;
    const now = performance.now();
    root.querySelectorAll(':scope > .chs-clock').forEach((el) => {
      const seat = +el.dataset.seat;
      const run = ctx.playing && c.running === seat;
      const left = (c.ms[seat] || 0) - (run ? now - st.at : 0);
      const nick = ctx.nickOf(seat) || ctx.seatName(seat);
      const html = '<span class="who">' + ctx.esc(nick) + (seat === ctx.seat ? ' <i>(ти)</i>' : '') + '</span>'
        + '<b>' + fmtMs(left) + '</b>';
      setHtml(el, html);
      el.classList.toggle('run', run);
      el.classList.toggle('low', run && left < 20000);
      el.classList.toggle('out', left <= 0);
      if (run && left <= 0 && ctx.mine) claimFlag(root, ctx, seat);
    });
  }

  function claimFlag(root, ctx, seat) {
    const st = root._clk;
    if (st.flagged === st.key) return;
    st.flagged = st.key;
    setTimeout(() => {
      if (!ctx.playing || !root._clk || root._clk.key !== st.flagged) return;
      // Сервер каже «Час ще є», якщо наш відлік забіг уперед, — тоді спробуємо ще раз за секунду.
      ctx.act('flag').then((r) => { if (r && !r.ok && root._clk) setTimeout(() => { root._clk.flagged = ''; }, 1000); });
    }, seat === ctx.seat ? 2500 : 350);
  }

  function stopClock(root) {
    if (root._clk) clearInterval(root._clk.timer);
    root._clk = null;
  }

  /// Рахунок серії «Ще раз» тим самим складом: хто скільки виграв за цим столом.
  function seriesHtml(ctx) {
    const s = (ctx.view || {}).series;
    if (!s || !s.wins) return '';
    const parts = [0, 1].map((i) => ctx.esc(ctx.nickOf(i) || ctx.seatName(i)) + ' <b>' + (s.wins[i] || 0) + '</b>');
    return '<span class="chs-serie" title="Скільки партій виграв кожен за цим столом">Серія: ' + parts.join(' : ')
      + (s.draws ? ' · нічиїх <b>' + s.draws + '</b>' : '') + '</span>';
  }

  const MOD = {
    id: 'chess',
    icon: ICON,
    seatNames: ['білі', 'чорні'],
    seatClass: ['x', 'd'],

    mount(root, ctx) { state(root); paint(root, ctx); puzzleHead(root, ctx); glekPrefs(root, ctx); },

    update(root, ctx) {
      const st = state(root);
      // Партія скінчилась або пішов чужий хід — недовибраний намір тримати нема сенсу.
      if (!ctx.myTurn) { st.sel = null; st.promo = null; }
      paint(root, ctx);
      puzzleHead(root, ctx);
      glekPrefs(root, ctx);
      // Коли фігура доїде — перемалювати без класу slide, щоб наступний кадр її вже не смикав.
      clearTimeout(st.t);
      if (performance.now() < st.slideUntil) st.t = setTimeout(() => root._chess && paint(root, ctx), SLIDE_MS + 30);
    },

    status(ctx) {
      const v = ctx.view || {};
      const kind = kindOf(ctx);
      if (kind === 'glek' && ctx.playing) {
        if (v.glek && v.glek.thinking) return '🤖 Глек думає…';
        return 'Твій хід' + (v.check ? ' — шах!' : '') + (v.variant === 'anti' ? ' · піддавки: віддавай фігури' : '');
      }
      if (kind === 'daily' && ctx.playing && v.puzzle && !v.puzzle.solved && !v.puzzle.gaveUp) {
        return v.puzzle.left === 1 ? 'Мат одним ходом — знайди його!' : 'Знайди хід, після якого мат неминучий за ' + v.puzzle.left;
      }
      if (!ctx.playing || !v.check) return '';   // порожньо — каркас напише «Твій хід» сам
      const base = ctx.myTurn ? 'Твій хід' : 'Ходить ' + (ctx.nickOf(v.turn) || ctx.seatName(v.turn));
      return base + ' — шах!';
    },

    unmount(root) {
      if (root._chess) { clearTimeout(root._chess.t); clearInterval(root._chess.puzTimer); }
      root._chess = null;
      stopClock(root);
    },

    news: {
      v: '2026-09-29',
      title: 'Шахи: Дядько Глек і задача дня',
      items: [
        '🤖 «Шахи з Глеком» — нема з ким? Дядько Глек сяде навпроти: легкий або середній, білими, чорними чи по черзі. Без рейтингу й черепків',
        '🧩 «Шахова задача дня» — мат у 2–3 ходи, одна на всіх на добу; табло за часом і спробами, 🔥 днів поспіль',
        '🙃 Глек уміє й піддавки та шахи Фішера — перемикається просто під дошкою',
      ],
    },
  };
  HGames.register(MOD);
  // Той самий модуль малює й соло-столи (Client: "chess"): каркас шукає модуль за Id гри, тож реєструємо ще двічі.
  // «Що нового» в них нема — це нові ігри (added), новину про них каже класичний стіл.
  const solo = (id) => Object.assign({}, MOD, { id, news: undefined, added: '2026-09-29' });
  HGames.register(solo('chess-glek'));
  HGames.register(solo('chess-daily'));
})();
