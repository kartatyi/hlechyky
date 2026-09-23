/*
  Ерудит. Дошка 15×15, стійка на сім фішок, слова з українських літер.

  Правила живуть на сервері (Impl/Scrabble.cs) — тут лише намір: обрав фішку на стійці, тицьнув у
  клітинку, натиснув «Готово». Поки «Готово» не натиснуто, викладене існує тільки в цьому браузері,
  тому нічого підкрутити з консолі не вийде: сервер однаково перевірить і стійку, і слова, і очки.

  Вид (Impl/Scrabble.cs): { board, bonuses, turn, players, scores, racks, rack, bag, bagTotal, last,
                            passes, smallDict, canChallenge, moves, result }.
  Дії: play { tiles: [{ cell, letter, blank }] }, pass, swap { letters }, challenge.

  Ще два способи класти, крім «фішка → клітинка» (оновлення 24.09.2026):
  - клік по порожній клітинці без обраної фішки ставить туди курсор (→ або ↓, повторний клік обертає),
    і далі слово просто друкується з клавіатури — літери беруться зі стійки, курсор іде далі сам;
  - під дошкою видно, які слова складаються і скільки це приблизно дасть очок. Рахунок той самий, що
    на сервері (ScrabbleBoard.Check), але вирішує однаково сервер — тому «≈».
*/
(() => {
  const SIZE = 15;
  const CELLS = SIZE * SIZE;
  const CENTRE = 7 * SIZE + 7;
  const RACK = 7;
  const BINGO = 50;

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.2" y="1.2" width="13.6" height="13.6" rx="2.6" fill="none" stroke="var(--accent)" stroke-width="1.6"/>'
    + '<path d="M5 11.4 8 4.6l3 6.8M6 9.4h4" fill="none" stroke="var(--clay)" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>';

  // Очки за літери — та сама таблиця, що в ScrabbleBag на сервері. Тут вона для підпису на фішці й
  // для попереднього «≈ +N»: остаточно хід рахує сервер.
  const VALUES = {
    'о': 1, 'а': 1, 'и': 1, 'н': 1, 'і': 1, 'е': 1, 'т': 1, 'в': 1, 'р': 1, 'с': 1,
    'л': 2, 'к': 2, 'м': 2, 'д': 2, 'п': 2, 'у': 2,
    'й': 3, 'ь': 3, 'з': 3, 'б': 3, 'я': 3, 'г': 3,
    'ч': 4, 'ш': 4, 'ж': 5, 'ц': 5, 'х': 5, 'є': 6, 'ю': 6, 'ї': 6, 'щ': 8, 'ф': 8, 'ґ': 10,
  };
  const ALPHABET = 'абвгґдеєжзиіїйклмнопрстуфхцчшщьюя';
  // Бонусні клітинки: символ із view.bonuses → [клас, підпис].
  const BONUS = {
    d: ['bl2', 'л2'], t: ['bl3', 'л3'],
    D: ['bw2', '×2'], T: ['bw3', '×3'], '*': ['bc', '★'],
  };

  const value = (ch) => VALUES[String(ch).toLowerCase()] || 0;
  const isBlank = (ch) => ch === '*';
  const upper = (ch) => ch !== ch.toLowerCase();

  /// «1 очко», «3 очки», «12 очок».
  const points = (n) => n + ' ' + (n % 100 >= 11 && n % 100 <= 14 ? 'очок'
    : n % 10 === 1 ? 'очко' : n % 10 >= 2 && n % 10 <= 4 ? 'очки' : 'очок');

  function state(root, ctx) {
    if (!root._scr) root._scr = { sel: null, pend: [], mode: 'play', swap: [], blank: null, sig: '', order: null, rackSig: '', cur: null, dir: 1 };
    ctx._scr = root._scr;                 // status() і onKey бачать лише ctx, а намір живе тут
    return root._scr;
  }

  /// Намір скидаємо, щойно партія рушила далі: чужий хід, наш власний або новий раунд.
  function sync(st, v) {
    const rackSig = (v.rack || []).join('');
    if (st.rackSig !== rackSig) { st.rackSig = rackSig; st.order = null; }
    const sig = [v.board || '', v.turn, (v.moves || []).length, rackSig].join('|');
    if (st.sig === sig) return;
    st.sig = sig;
    st.pend = [];
    st.sel = null;
    st.mode = 'play';
    st.swap = [];
    st.blank = null;
    st.cur = null;
  }

  const pendAt = (st, cell) => st.pend.findIndex((p) => p.cell === cell);
  const used = (st, i) => st.pend.some((p) => p.ri === i);

  // ---------------------------------------------------------------- попередній підрахунок

  /// Дзеркало ScrabbleBoard.Check: або { words: [{ word, score }], total }, або { error }.
  function preview(board, pend) {
    if (!pend.length) return null;
    const bonuses = board.bonuses;
    const now = board.cells.split('');
    const fresh = new Set();
    for (const p of pend) { now[p.cell] = p.blank ? p.letter.toUpperCase() : p.letter; fresh.add(p.cell); }
    const rows = new Set(pend.map((p) => Math.floor(p.cell / SIZE)));
    const cols = new Set(pend.map((p) => p.cell % SIZE));
    if (rows.size > 1 && cols.size > 1) return { error: 'Фішки мають лягти в один рядок або стовпець' };
    const horizontal = rows.size === 1;
    const step = horizontal ? 1 : SIZE;
    const from = Math.min(...pend.map((p) => p.cell));
    const to = Math.max(...pend.map((p) => p.cell));
    for (let c = from; c <= to; c += step) if (now[c] === '.') return { error: 'У слові дірка' };
    const empty = board.cells.split('').every((c) => c === '.');
    if (empty && !fresh.has(CENTRE)) return { error: 'Перше слово кладуть через ★ у центрі' };
    const around = (c) => [c % SIZE > 0 ? c - 1 : -1, c % SIZE < SIZE - 1 ? c + 1 : -1, c - SIZE, c + SIZE].filter((n) => n >= 0 && n < CELLS);
    if (!empty && !pend.some((p) => around(p.cell).some((n) => board.cells[n] !== '.'))) return { error: 'Слово має торкатись того, що вже на дошці' };

    const words = [];
    const collect = (cell, hor) => {
      const st = hor ? 1 : SIZE;
      let start = cell;
      while (true) {
        const back = start - st;
        if (back < 0 || (hor && Math.floor(back / SIZE) !== Math.floor(start / SIZE)) || now[back] === '.') break;
        start = back;
      }
      let text = '', sum = 0, mult = 1;
      for (let c = start; c < CELLS; c += st) {
        if ((hor && Math.floor(c / SIZE) !== Math.floor(start / SIZE)) || now[c] === '.') break;
        text += now[c].toLowerCase();
        let val = upper(now[c]) ? 0 : value(now[c]);
        if (fresh.has(c)) {
          const b = bonuses[c];
          if (b === 'd') val *= 2; else if (b === 't') val *= 3; else if (b === 'D' || b === '*') mult *= 2; else if (b === 'T') mult *= 3;
        }
        sum += val;
      }
      if (text.length >= 2) words.push({ word: text, score: sum * mult });
    };
    if (pend.length === 1) { collect(pend[0].cell, true); collect(pend[0].cell, false); } else {
      collect(pend[0].cell, horizontal);
      for (const p of pend) collect(p.cell, !horizontal);
    }
    if (!words.length) return { error: 'Слово має бути щонайменше з двох літер' };
    return { words, total: words.reduce((a, w) => a + w.score, 0) + (pend.length === RACK ? BINGO : 0) };
  }

  // ---------------------------------------------------------------- шапка з рахунком

  function head(host, ctx) {
    const v = ctx.view || {};
    const scores = v.scores || [];
    const racks = v.racks || [];
    const chips = [];
    for (let i = 0; i < (ctx.room.seats ? ctx.room.seats.length : 0); i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      const on = v.turn === i && ctx.playing;
      chips.push('<span class="scr-p' + (on ? ' on' : '') + (i === ctx.seat ? ' me' : '') + '">'
        + '<i>' + ctx.esc(nick) + '</i><b>' + (scores[i] || 0) + '</b>'
        + '<u>' + (racks[i] || 0) + '</u></span>');
    }
    const quick = v.bagTotal && v.bagTotal < 104 ? ' · швидка' : '';
    chips.push('<span class="scr-bag" title="Фішок у мішку">🎒 ' + (v.bag || 0) + quick + '</span>');
    if (v.smallDict) chips.push('<span class="scr-warn" title="Великого словника нема: слова приймаються, але їх можна оскаржити">малий словник</span>');
    const html = chips.join('');
    if (host.innerHTML !== html) host.innerHTML = html;
  }

  // ---------------------------------------------------------------- дошка

  function board(host, ctx, st) {
    const v = ctx.view || {};
    const cells = v.board || '';
    const bonuses = v.bonuses || '';
    const last = (v.last && v.last.cells) || [];
    const mine = ctx.myTurn && st.mode === 'play';

    HGames.ui.grid(host, {
      cols: SIZE,
      rows: SIZE,
      cls: 'sboard',
      cell: (i) => {
        const ch = cells[i] || '.';
        if (ch !== '.') {
          const cls = ['tile', last.includes(i) ? 'last' : '', upper(ch) ? 'wild' : ''];
          return { html: mark(ch), cls: cls.filter(Boolean).join(' '), disabled: true };
        }
        const p = pendAt(st, i);
        if (p >= 0) return { html: mark(st.pend[p].letter, st.pend[p].blank), cls: 'tile pend', disabled: false };
        const b = BONUS[bonuses[i]];
        const cur = mine && st.cur === i;
        return {
          html: cur ? '<i class="arrow">' + (st.dir === 1 ? '→' : '↓') + '</i>' : b ? '<i>' + b[1] + '</i>' : '',
          cls: [b ? b[0] : '', cur ? 'cur' : ''].filter(Boolean).join(' '),
          disabled: !mine,
        };
      },
      onCell: (i) => place(host.parentNode, ctx, st, i),
    });
  }

  /// Фішка на дошці: ВЕЛИКА літера — це порожня фішка, вона нічого не коштує.
  function mark(ch, blank) {
    const wild = blank || upper(ch);
    const letter = String(ch).toLowerCase();
    return '<b>' + letter + '</b>' + (wild ? '' : '<u>' + value(letter) + '</u>');
  }

  function place(root, ctx, st, cell) {
    if (!ctx.myTurn || st.mode !== 'play') return;
    const already = pendAt(st, cell);
    if (already >= 0) { st.pend.splice(already, 1); return repaint(root, ctx); }
    const rack = (ctx.view && ctx.view.rack) || [];
    if (st.sel == null || !rack[st.sel]) {
      // Фішки не обрано — ставимо курсор: далі слово можна просто надрукувати. Другий клік — обертає.
      if (st.cur === cell) st.dir = st.dir === 1 ? SIZE : 1; else st.cur = cell;
      return repaint(root, ctx);
    }
    if (isBlank(rack[st.sel])) { st.blank = { cell, ri: st.sel }; return repaint(root, ctx); }
    st.pend.push({ cell, ri: st.sel, letter: rack[st.sel], blank: false });
    st.sel = null;
    st.cur = null;
    repaint(root, ctx);
  }

  /// Друк із клавіатури: літеру беремо зі стійки (або порожню фішку, якщо такої літери нема), кладемо під
  /// курсор і ведемо курсор далі, перестрибуючи зайняті клітинки. Повертає, чи вдалося.
  function typeLetter(root, ctx, st, ch) {
    const v = ctx.view || {};
    const rack = v.rack || [];
    if (st.cur == null || st.mode !== 'play') return false;
    let ri = rack.findIndex((r, i) => r === ch && !used(st, i));
    let blank = false;
    if (ri < 0) { ri = rack.findIndex((r, i) => isBlank(r) && !used(st, i)); blank = ri >= 0; }
    if (ri < 0) { ctx.toast('Літери «' + ch + '» на стійці нема', 'err'); return true; }
    const cells = v.board || '';
    let c = st.cur;
    while (c < CELLS && (cells[c] !== '.' || pendAt(st, c) >= 0)) c = advance(c, st.dir);
    if (c < 0 || c >= CELLS) return true;
    st.pend.push({ cell: c, ri, letter: ch, blank });
    st.sel = null;
    let next = advance(c, st.dir);
    while (next >= 0 && next < CELLS && cells[next] !== '.') next = advance(next, st.dir);
    st.cur = next >= 0 && next < CELLS ? next : null;
    repaint(root, ctx);
    return true;
  }

  /// Наступна клітинка в напрямку; -1 — вийшли за край рядка чи дошки.
  function advance(cell, dir) {
    if (cell < 0) return -1;
    if (dir === 1) return cell % SIZE === SIZE - 1 ? -1 : cell + 1;
    return cell + SIZE < CELLS ? cell + SIZE : -1;
  }

  // ---------------------------------------------------------------- стійка

  function rackRow(host, ctx, st) {
    const rack = (ctx.view && ctx.view.rack) || [];
    const order = st.order && st.order.length === rack.length ? st.order : rack.map((_, i) => i);
    const items = order.map((ri) => {
      const ch = rack[ri];
      return {
        ri,
        html: '<b>' + (isBlank(ch) ? '·' : ch) + '</b>' + (isBlank(ch) ? '' : '<u>' + value(ch) + '</u>'),
        cls: (st.mode === 'swap' ? st.swap.includes(ri) : st.sel === ri) ? 'sel' : '',
        disabled: st.mode === 'play' ? used(st, ri) : false,
      };
    });
    HGames.ui.hand(host, items, {
      render: (it) => it.html,
      onItem: (it) => pick(host.parentNode, ctx, st, it.ri),
    });
  }

  function pick(root, ctx, st, i) {
    if (!ctx.myTurn) return;
    if (st.mode === 'swap') {
      const at = st.swap.indexOf(i);
      if (at >= 0) st.swap.splice(at, 1); else st.swap.push(i);
    } else {
      st.sel = st.sel === i ? null : i;
      // Фішка в руці — курсор не потрібен: наступний клік по клітинці її покладе.
      if (st.sel != null) st.cur = null;
    }
    repaint(root, ctx);
  }

  // ---------------------------------------------------------------- підказка під дошкою

  /// Рядок статусу каркаса перемальовується лише на подію 'room', а намір живе тут і міняється від
  /// кожного кліку — тому свою підказку малюємо самі, а каркасу лишаємо «Твій хід» / «Ходить X».
  function hint(host, ctx, st) {
    const v = ctx.view || {};
    let html = '';
    const firstMove = !(v.board || '').replace(/\./g, '').length;
    if (v.result && !ctx.playing && ctx.room.status === 'finished') html = verdict(ctx, v.result);
    else if (st.blank) html = 'Обери літеру для порожньої фішки';
    else if (st.mode === 'swap') html = 'Познач фішки, які здаєш у мішок, і тисни «Обміняти»';
    else if (ctx.myTurn && st.pend.length) {
      const p = preview({ cells: v.board || '', bonuses: v.bonuses || '' }, st.pend);
      html = p && p.error ? '<span class="scr-bad">' + ctx.esc(p.error) + '</span>'
        : p ? 'Складено: <b>' + p.words.map((w) => ctx.esc(w.word)).join(', ') + '</b> — ≈ +' + points(p.total)
          + (st.pend.length === RACK ? ' (з бінго!)' : '') + '. Тисни «Готово»'
          : '';
    } else if (ctx.myTurn && ctx.playing) {
      html = firstMove
        ? 'Перше слово має пройти через ★. Візьми фішку зі стійки і тицьни в клітинку — або тицьни клітинку й друкуй'
        : 'Візьми фішку зі стійки і тицьни в клітинку — або тицьни клітинку й друкуй слово';
    } else if (v.last) html = 'Останнє слово: ' + v.last.words.map((w) => ctx.esc(w.word)).join(', ') + ' +' + v.last.total;
    if (host.innerHTML !== html) host.innerHTML = html;
  }

  /// Підсумок партії під дошкою: хто переміг, рахунок усіх і чому партія скінчилась.
  function verdict(ctx, r) {
    const seats = (ctx.room && ctx.room.seats) || [];
    const rows = seats.filter((x) => x.nick).map((x) => ({ i: x.i, nick: x.nick, score: (r.scores || [])[x.i] || 0 }))
      .sort((a, b) => b.score - a.score)
      .map((x) => (x.i === r.winner ? '🏆 ' : '') + ctx.esc(x.nick) + ' <b>' + x.score + '</b>').join(' · ');
    const why = r.reason === 'out' ? 'мішок порожній і хтось виклав усі фішки — йому очки чужих стійок'
      : r.reason === 'passes' ? 'шість пасів поспіль — кожному мінус те, що лишилось на стійці'
        : 'за столом лишився один';
    return '<span class="scr-end">' + (r.winner == null ? 'Нічия! ' : '') + rows + '</span><br><span>' + why + '</span>';
  }

  // ---------------------------------------------------------------- кнопки

  function buttons(host, ctx, st) {
    const v = ctx.view || {};
    const out = [];
    if (ctx.myTurn && st.mode === 'play') {
      out.push('<button class="primary" data-go' + (st.pend.length ? '' : ' disabled') + '>Готово</button>');
      if (st.pend.length) out.push('<button class="ghost" data-reset>Скинути</button>');
      else out.push('<button class="ghost" data-mix title="Перемішати фішки на стійці — інколи так легше побачити слово">🔀</button>');
      out.push('<button class="ghost" data-pass>Пас</button>');
      if ((v.bag || 0) >= 7) out.push('<button class="ghost" data-swap>Обмін</button>');
      // Вікно оскарження живе на сервері (його закриває будь-який пас чи обмін), тому кнопку
      // показуємо строго за його прапорцем, а не за здогадкою «останній хід був не мій».
      if (v.canChallenge) out.push('<button class="ghost warn" data-challenge>Не слово</button>');
    } else if (ctx.myTurn && st.mode === 'swap') {
      out.push('<button class="primary" data-doswap' + (st.swap.length ? '' : ' disabled') + '>Обміняти ' + (st.swap.length || '') + '</button>');
      out.push('<button class="ghost" data-cancel>Скасувати</button>');
    } else if (ctx.mine && ctx.playing && (v.rack || []).length > 1) {
      // Думати над словом можна й не у свій хід — перемішати стійку не заважає нікому.
      out.push('<button class="ghost" data-mix title="Перемішати фішки на стійці">🔀</button>');
    }
    const html = out.join('');
    if (host.innerHTML === html) return;
    host.innerHTML = html;

    const root = host.parentNode;
    const on = (sel, fn) => { const b = host.querySelector(sel); if (b) b.onclick = fn; };
    on('[data-go]', () => submit(root, ctx, st));
    on('[data-reset]', () => { st.pend = []; st.sel = null; st.cur = null; repaint(root, ctx); });
    on('[data-mix]', () => { mix(ctx, st); repaint(root, ctx); });
    on('[data-pass]', () => ctx.act('pass'));
    on('[data-swap]', () => { st.mode = 'swap'; st.pend = []; st.sel = null; st.swap = []; st.cur = null; repaint(root, ctx); });
    on('[data-challenge]', () => ctx.act('challenge'));
    on('[data-cancel]', () => { st.mode = 'play'; st.swap = []; repaint(root, ctx); });
    on('[data-doswap]', () => {
      const rack = (ctx.view && ctx.view.rack) || [];
      const letters = st.swap.map((i) => rack[i]).filter(Boolean);
      ctx.act('swap', { letters }).then(() => { st.mode = 'play'; st.swap = []; repaint(root, ctx); });
    });
  }

  function submit(root, ctx, st) {
    const tiles = st.pend.map((p) => ({ cell: p.cell, letter: p.letter, blank: !!p.blank }));
    if (!tiles.length) return;
    ctx.act('play', { tiles }).then((r) => { if (r && r.ok) { st.pend = []; st.sel = null; st.cur = null; repaint(root, ctx); } });
  }

  /// Перемішати стійку лише в цьому браузері: сервер про порядок фішок нічого не знає й знати не мусить.
  function mix(ctx, st) {
    const n = ((ctx.view && ctx.view.rack) || []).length;
    const order = st.order && st.order.length === n ? st.order.slice() : Array.from({ length: n }, (_, i) => i);
    for (let i = n - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [order[i], order[j]] = [order[j], order[i]]; }
    st.order = order;
  }

  // ---------------------------------------------------------------- попап літери для порожньої фішки

  function blankBox(host, ctx, st) {
    if (!st.blank) { if (host.innerHTML) host.innerHTML = ''; return; }
    host.innerHTML = '<div class="scr-pick"><span class="muted small">Яка це літера?</span><div class="scr-letters">'
      + ALPHABET.split('').map((ch) => '<button type="button" data-ch="' + ch + '">' + ch + '</button>').join('')
      + '</div><button class="ghost" data-cancel>Скасувати</button></div>';
    const root = host.parentNode;
    host.querySelectorAll('[data-ch]').forEach((b) => b.onclick = () => {
      st.pend.push({ cell: st.blank.cell, ri: st.blank.ri, letter: b.dataset.ch, blank: true });
      st.blank = null;
      st.sel = null;
      repaint(root, ctx);
    });
    host.querySelector('[data-cancel]').onclick = () => { st.blank = null; repaint(root, ctx); };
  }

  // ---------------------------------------------------------------- легенда бонусів

  /// Поки дошка майже порожня, новачкові треба знати, що значать кольорові клітинки. Далі — ховаємо.
  function legend(host, ctx) {
    const v = ctx.view || {};
    const placed = (v.board || '').replace(/\./g, '').length;
    const html = placed < 12 && ctx.playing
      ? '<span><i class="bl2">л2</i> літера ×2</span><span><i class="bl3">л3</i> літера ×3</span>'
        + '<span><i class="bw2">×2</i> слово ×2</span><span><i class="bw3">×3</i> слово ×3</span>'
        + '<span>сім фішок за хід — <b>+50</b></span>'
      : '';
    if (host.innerHTML !== html) host.innerHTML = html;
  }

  // ---------------------------------------------------------------- журнал ходів

  function log(host, ctx) {
    const moves = (ctx.view && ctx.view.moves) || [];
    const html = moves.slice(-6).reverse()
      .map((m) => '<div class="scr-row"><i>' + ctx.esc(ctx.nickOf(m.seat) || ctx.seatName(m.seat)) + '</i>'
        + '<span>' + ctx.esc(m.text) + '</span></div>').join('');
    if (host.innerHTML !== html) host.innerHTML = html;
  }

  // ---------------------------------------------------------------- складання картки

  function repaint(root, ctx) {
    const st = state(root, ctx);
    head(root.querySelector('.scr-top'), ctx);
    board(root.querySelector('.scr-board'), ctx, st);
    legend(root.querySelector('.scr-legend'), ctx);
    rackRow(root.querySelector('.scr-rack'), ctx, st);
    blankBox(root.querySelector('.scr-blank'), ctx, st);
    hint(root.querySelector('.scr-hint'), ctx, st);
    buttons(root.querySelector('.scr-btns'), ctx, st);
    log(root.querySelector('.scr-log'), ctx);
  }

  const rootOf = (ctx) => document.querySelector('.gtable[data-room="' + ctx.room.id + '"] .gbody');

  HGames.register({
    id: 'scrabble',
    icon: ICON,
    seatNames: ['перший', 'другий', 'третій', 'четвертий'],
    seatClass: ['x', 'o', 'c', 'd'],
    news: {
      v: '2026-09-24',
      title: 'Ерудит: друкуй слова й бач очки наперед',
      items: [
        '⌨️ Тицьни порожню клітинку — з\'явиться стрілка, і слово можна просто надрукувати (другий клік обертає ↓)',
        '🧮 Поки складаєш, під дошкою видно, які слова виходять і скільки це приблизно дасть очок',
        '⚡ Нова опція столу: швидка партія — у мішку вдвічі менше фішок',
        '🔀 Кнопка, що перемішує стійку, — інколи так слово видно одразу',
      ],
    },

    mount(root, ctx) {
      // Дошка 15×15 у колонці на 300 px не читається — просимо картці цілий ряд.
      const card = root.closest('.gtable');
      if (card) card.classList.add('scr-table');
      root.innerHTML = '<div class="scr-top"></div><div class="scr-board"></div><div class="scr-legend"></div>'
        + '<div class="scr-rack"></div><div class="scr-blank"></div><div class="scr-hint"></div>'
        + '<div class="scr-btns"></div><div class="scr-log"></div>';
      state(root, ctx);
      repaint(root, ctx);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      sync(st, ctx.view || {});
      repaint(root, ctx);
    },

    onKey(e, ctx) {
      const st = ctx._scr;
      if (!st || !ctx.myTurn) return false;
      const root = rootOf(ctx);
      if (!root) return false;
      if (e.key === 'Enter' && st.pend.length) { submit(root, ctx, st); return true; }
      if (e.key === 'Escape' && (st.pend.length || st.blank || st.mode === 'swap' || st.cur != null)) {
        st.pend = [];
        st.sel = null;
        st.blank = null;
        st.mode = 'play';
        st.swap = [];
        st.cur = null;
        repaint(root, ctx);
        return true;
      }
      if (e.key === 'Backspace' && st.pend.length) {
        const gone = st.pend.pop();
        // Друкували — курсор вертається туди, звідки забрали літеру.
        if (st.cur != null || gone) st.cur = gone.cell;
        repaint(root, ctx);
        return true;
      }
      if (st.cur != null && !e.ctrlKey && !e.metaKey && !e.altKey) {
        const arrows = { ArrowRight: [1, 1], ArrowLeft: [1, -1], ArrowDown: [SIZE, SIZE], ArrowUp: [SIZE, -SIZE] };
        if (arrows[e.key]) {
          const [dir, delta] = arrows[e.key];
          const to = st.cur + delta;
          st.dir = dir;
          if (to >= 0 && to < CELLS && (dir === SIZE || Math.floor(to / SIZE) === Math.floor(st.cur / SIZE))) st.cur = to;
          repaint(root, ctx);
          return true;
        }
        const ch = (e.key || '').toLowerCase();
        if (ch.length === 1 && ALPHABET.includes(ch)) return typeLetter(root, ctx, st, ch);
      }
      return false;
    },

    // status() навмисно нема: «Твій хід» / «Ходить X» / підсумок каркас пише сам, а те, що
    // залежить від наміру, живе в .scr-hint — його ми перемальовуємо на кожен клік.

    unmount(root) { root._scr = null; },
  });
})();
