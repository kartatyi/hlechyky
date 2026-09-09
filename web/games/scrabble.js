/*
  Ерудит. Дошка 15×15, стійка на сім фішок, слова з українських літер.

  Правила живуть на сервері (Impl/Scrabble.cs) — тут лише намір: обрав фішку на стійці, тицьнув у
  клітинку, натиснув «Готово». Поки «Готово» не натиснуто, викладене існує тільки в цьому браузері,
  тому нічого підкрутити з консолі не вийде: сервер однаково перевірить і стійку, і слова, і очки.

  Вид (Impl/Scrabble.cs): { board, bonuses, turn, players, scores, racks, rack, bag, last,
                            passes, smallDict, moves, result }.
  Дії: play { tiles: [{ cell, letter, blank }] }, pass, swap { letters }, challenge.
*/
(() => {
  const SIZE = 15;
  const CELLS = SIZE * SIZE;

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.2" y="1.2" width="13.6" height="13.6" rx="2.6" fill="none" stroke="var(--accent)" stroke-width="1.6"/>'
    + '<path d="M5 11.4 8 4.6l3 6.8M6 9.4h4" fill="none" stroke="var(--clay)" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>';

  // Очки за літери — та сама таблиця, що в ScrabbleBag на сервері. Тут вона лише для підпису на
  // фішці: рахує хід і виставляє очки все одно сервер, клієнт нічого не підсумовує.
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

  function state(root, ctx) {
    if (!root._scr) root._scr = { sel: null, pend: [], mode: 'play', swap: [], blank: null, sig: '' };
    ctx._scr = root._scr;                 // status() бачить лише ctx, а намір живе тут
    return root._scr;
  }

  /// Намір скидаємо, щойно партія рушила далі: чужий хід, наш власний або новий раунд.
  function sync(st, v) {
    const sig = [v.board || '', v.turn, (v.moves || []).length, (v.rack || []).join('')].join('|');
    if (st.sig === sig) return;
    st.sig = sig;
    st.pend = [];
    st.sel = null;
    st.mode = 'play';
    st.swap = [];
    st.blank = null;
  }

  const pendAt = (st, cell) => st.pend.findIndex((p) => p.cell === cell);
  const used = (st, i) => st.pend.some((p) => p.ri === i);

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
    chips.push('<span class="scr-bag" title="Фішок у мішку">🎒 ' + (v.bag || 0) + '</span>');
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

    HGames.ui.grid(host, {
      cols: SIZE,
      rows: SIZE,
      cls: 'sboard',
      cell: (i) => {
        const ch = cells[i] || '.';
        if (ch !== '.') {
          const cls = ['tile', last.includes(i) ? 'last' : '', ch === ch.toUpperCase() ? 'wild' : ''];
          return { html: mark(ch), cls: cls.filter(Boolean).join(' '), disabled: true };
        }
        const p = pendAt(st, i);
        if (p >= 0) return { html: mark(st.pend[p].letter, st.pend[p].blank), cls: 'tile pend', disabled: false };
        const b = BONUS[bonuses[i]];
        return {
          html: b ? '<i>' + b[1] + '</i>' : '',
          cls: b ? b[0] : '',
          disabled: !(ctx.myTurn && st.mode === 'play'),
        };
      },
      onCell: (i) => place(host.parentNode, ctx, st, i),
    });
  }

  /// Фішка на дошці: ВЕЛИКА літера — це порожня фішка, вона нічого не коштує.
  function mark(ch, blank) {
    const wild = blank || (ch === ch.toUpperCase() && ch !== ch.toLowerCase());
    const letter = String(ch).toLowerCase();
    return '<b>' + letter + '</b>' + (wild ? '' : '<u>' + value(letter) + '</u>');
  }

  function place(root, ctx, st, cell) {
    if (!ctx.myTurn || st.mode !== 'play') return;
    const already = pendAt(st, cell);
    if (already >= 0) { st.pend.splice(already, 1); return repaint(root, ctx); }
    const rack = (ctx.view && ctx.view.rack) || [];
    if (st.sel == null || !rack[st.sel]) { ctx.toast('Спершу візьми фішку зі стійки', ''); return; }
    if (isBlank(rack[st.sel])) { st.blank = { cell, ri: st.sel }; return repaint(root, ctx); }
    st.pend.push({ cell, ri: st.sel, letter: rack[st.sel], blank: false });
    st.sel = null;
    repaint(root, ctx);
  }

  // ---------------------------------------------------------------- стійка

  function rackRow(host, ctx, st) {
    const rack = (ctx.view && ctx.view.rack) || [];
    const items = rack.map((ch, i) => ({
      html: '<b>' + (isBlank(ch) ? '·' : ch) + '</b>' + (isBlank(ch) ? '' : '<u>' + value(ch) + '</u>'),
      cls: (st.mode === 'swap' ? st.swap.includes(i) : st.sel === i) ? 'sel' : '',
      disabled: st.mode === 'play' ? used(st, i) : false,
    }));
    HGames.ui.hand(host, items, {
      render: (it) => it.html,
      onItem: (it, i) => pick(host.parentNode, ctx, st, i),
    });
  }

  function pick(root, ctx, st, i) {
    if (!ctx.myTurn) return;
    if (st.mode === 'swap') {
      const at = st.swap.indexOf(i);
      if (at >= 0) st.swap.splice(at, 1); else st.swap.push(i);
    } else {
      st.sel = st.sel === i ? null : i;
    }
    repaint(root, ctx);
  }

  // ---------------------------------------------------------------- підказка під дошкою

  /// Рядок статусу каркаса перемальовується лише на подію 'room', а намір живе тут і міняється від
  /// кожного кліку — тому свою підказку малюємо самі, а каркасу лишаємо «Твій хід» / «Ходить X».
  function hint(host, ctx, st) {
    const v = ctx.view || {};
    let text = '';
    if (st.blank) text = 'Обери літеру для порожньої фішки';
    else if (st.mode === 'swap') text = 'Познач фішки, які здаєш у мішок, і тисни «Обміняти»';
    else if (ctx.myTurn && st.pend.length) text = 'Складено фішок: ' + st.pend.length + '. Тисни «Готово»';
    else if (ctx.myTurn && ctx.playing) text = 'Візьми фішку зі стійки і тицьни в клітинку';
    else if (v.last) text = 'Останнє слово: ' + v.last.words.map((w) => w.word).join(', ') + ' +' + v.last.total;
    if (host.textContent !== text) host.textContent = text;
  }

  // ---------------------------------------------------------------- кнопки

  function buttons(host, ctx, st) {
    const v = ctx.view || {};
    const out = [];
    if (ctx.myTurn && st.mode === 'play') {
      out.push('<button class="primary" data-go' + (st.pend.length ? '' : ' disabled') + '>Готово</button>');
      if (st.pend.length) out.push('<button class="ghost" data-reset>Скинути</button>');
      out.push('<button class="ghost" data-pass>Пас</button>');
      if ((v.bag || 0) >= 7) out.push('<button class="ghost" data-swap>Обмін</button>');
      if (v.smallDict && v.last && v.last.seat !== ctx.seat)
        out.push('<button class="ghost warn" data-challenge>Не слово</button>');
    } else if (ctx.myTurn && st.mode === 'swap') {
      out.push('<button class="primary" data-doswap' + (st.swap.length ? '' : ' disabled') + '>Обміняти ' + (st.swap.length || '') + '</button>');
      out.push('<button class="ghost" data-cancel>Скасувати</button>');
    }
    const html = out.join('');
    if (host.innerHTML === html) return;
    host.innerHTML = html;

    const root = host.parentNode;
    const on = (sel, fn) => { const b = host.querySelector(sel); if (b) b.onclick = fn; };
    on('[data-go]', () => {
      const tiles = st.pend.map((p) => ({ cell: p.cell, letter: p.letter, blank: !!p.blank }));
      ctx.act('play', { tiles }).then((r) => { if (r && r.ok) { st.pend = []; st.sel = null; repaint(root, ctx); } });
    });
    on('[data-reset]', () => { st.pend = []; st.sel = null; repaint(root, ctx); });
    on('[data-pass]', () => ctx.act('pass'));
    on('[data-swap]', () => { st.mode = 'swap'; st.pend = []; st.sel = null; st.swap = []; repaint(root, ctx); });
    on('[data-challenge]', () => ctx.act('challenge'));
    on('[data-cancel]', () => { st.mode = 'play'; st.swap = []; repaint(root, ctx); });
    on('[data-doswap]', () => {
      const rack = (ctx.view && ctx.view.rack) || [];
      const letters = st.swap.map((i) => rack[i]).filter(Boolean);
      ctx.act('swap', { letters }).then(() => { st.mode = 'play'; st.swap = []; repaint(root, ctx); });
    });
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
    rackRow(root.querySelector('.scr-rack'), ctx, st);
    blankBox(root.querySelector('.scr-blank'), ctx, st);
    hint(root.querySelector('.scr-hint'), ctx, st);
    buttons(root.querySelector('.scr-btns'), ctx, st);
    log(root.querySelector('.scr-log'), ctx);
  }

  HGames.register({
    id: 'scrabble',
    icon: ICON,
    seatNames: ['перший', 'другий', 'третій', 'четвертий'],
    seatClass: ['x', 'o', 'c', 'd'],

    mount(root, ctx) {
      // Дошка 15×15 у колонці на 300 px не читається — просимо картці цілий ряд.
      const card = root.closest('.gtable');
      if (card) card.classList.add('scr-table');
      root.innerHTML = '<div class="scr-top"></div><div class="scr-board"></div>'
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
      const root = document.querySelector('.gtable[data-room="' + ctx.room.id + '"] .gbody');
      if (!root) return false;
      if (e.key === 'Enter' && st.pend.length) {
        const tiles = st.pend.map((p) => ({ cell: p.cell, letter: p.letter, blank: !!p.blank }));
        ctx.act('play', { tiles });
        return true;
      }
      if (e.key === 'Escape' && (st.pend.length || st.blank || st.mode === 'swap')) {
        st.pend = [];
        st.sel = null;
        st.blank = null;
        st.mode = 'play';
        st.swap = [];
        repaint(root, ctx);
        return true;
      }
      if (e.key === 'Backspace' && st.pend.length) {
        st.pend.pop();
        repaint(root, ctx);
        return true;
      }
      return false;
    },

    // status() навмисно нема: «Твій хід» / «Ходить X» / підсумок каркас пише сам, а те, що
    // залежить від наміру, живе в .scr-hint — його ми перемальовуємо на кожен клік.

    unmount(root) { root._scr = null; },
  });
})();
