/*
  Віселиця. Правила й слово живуть на сервері; тут — риски, шибениця й дві кнопки.

  Вид (Impl/Hangman.cs):
  { round, of, mask: 'к_р__а', wrong: string[], right: string[], errors, maxErrors,
    scores: number[], out: number[], phase: 'play'|'between'|'done', nextIn, revealed, result }

  Слова у виді немає, поки раунд не скінчився, — тож підглянути в консолі нічого не вийде.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2 14h6 M4 14V2h7 M11 2v2.4" fill="none" stroke="var(--clay)" stroke-width="1.6" stroke-linecap="round"/>'
    + '<circle cx="11" cy="6.6" r="2.2" fill="none" stroke="var(--accent)" stroke-width="1.6"/></svg>';

  const ALPHABET = 'абвгґдеєжзиіїйклмнопрстуфхцчшщьюя';

  // Вісім промахів — вісім ліній, рівно в тому порядку, що в spec: стовп, перекладина, мотузка,
  // голова, тулуб, руки, ноги і насамкінець обличчя.
  const PARTS = [
    '<path d="M6 58h26 M14 58V7"/>',
    '<path d="M14 7h28"/>',
    '<path d="M42 7v7"/>',
    '<circle cx="42" cy="20" r="6"/>',
    '<path d="M42 26v16"/>',
    '<path d="M42 30l-9 8 M42 30l9 8"/>',
    '<path d="M42 42l-8 11 M42 42l8 11"/>',
    '<path class="hface" d="M38.4 17.6l2.2 2.2 M40.6 17.6l-2.2 2.2 M43.4 17.6l2.2 2.2 M45.6 17.6l-2.2 2.2"/>',
  ];

  const seatsOf = (ctx) => (ctx.room && ctx.room.seats ? ctx.room.seats.length : 6);

  /// Шибениця. Перемальовуємо лише коли додався промах: інакше SVG блимав би на кожен тик.
  function gallows(root, v) {
    const el = root.querySelector('.hgal');
    const n = Math.max(0, Math.min(PARTS.length, v.errors || 0));
    if (el.dataset.n === String(n)) return;
    el.dataset.n = String(n);
    // Бліда шибениця цілком лежить під сподом із самого початку: так видно, скільки ще лишилось
    // домалювати, а порожня картка не виглядає зламаною.
    el.innerHTML = '<svg viewBox="0 0 64 64" aria-hidden="true">'
      + '<g class="hghost">' + PARTS.join('') + '</g>' + PARTS.slice(0, n).join('') + '</svg>';
  }

  /// Риски. Після раунду показуємо саме слово, а якщо його так і не відкрили — підсвічуємо те,
  /// чого не вистачило. Слово, назване цілком, підсвічувати нема чого: воно ж узяте.
  function mask(root, v) {
    const shown = v.phase === 'play' ? (v.mask || '') : (v.revealed || v.mask || '');
    const lost = v.phase !== 'play' && (v.mask || '').indexOf('_') >= 0;
    const open = (v.right || []).join('');
    const html = shown.split('').map((c) => {
      if (c === '_') return '<span class="hl gap"></span>';
      return '<span class="hl' + (lost && open.indexOf(c) < 0 ? ' miss' : '') + '">' + c + '</span>';
    }).join('');
    const el = root.querySelector('.hmask');
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  /// Рахунок по місцях. Вибулих із цього слова притінюємо — видно, кому ще є що казати.
  function scores(root, ctx, v) {
    const out = v.out || [];
    let html = '';
    for (let i = 0; i < seatsOf(ctx); i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      html += '<span class="chip hsc' + (out.indexOf(i) >= 0 ? ' off' : '') + (i === ctx.seat ? ' me' : '') + '">'
        + ctx.esc(nick) + ' <b>' + ((v.scores || [])[i] || 0) + '</b></span>';
    }
    const el = root.querySelector('.hscores');
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  /// Рядок над рисками: котре це слово і що зараз відбувається.
  function note(root, ctx, v) {
    const text = v.phase === 'done' ? 'Партію зіграно'
      : v.phase === 'between' ? (v.revealed ? 'Слово було: ' + v.revealed : 'Слово втекло')
        : 'Промахи: ' + (v.errors || 0) + ' з ' + (v.maxErrors || 8);
    const el = root.querySelector('.hnote');
    if (el.textContent !== text) el.textContent = text;
    // до «Почати» раунду ще нема — не пишемо «Слово 0 з 5»
    const round = v.round ? 'Слово ' + v.round + ' з ' + (v.of || 5) : 'Господар тисне «Почати»';
    const r = root.querySelector('.hround');
    if (r.textContent !== round) r.textContent = round;
  }

  /// Поле «назвати слово». Живе лише в того, хто грає і ще не вибув із цього слова.
  function wordBox(root, ctx, v, playable) {
    const box = root.querySelector('.hword');
    box.hidden = !playable;
    if (!playable) return;
    const input = box.querySelector('input');
    box.onsubmit = (e) => {
      e.preventDefault();
      const text = input.value.trim();
      if (!text) return;
      input.value = '';
      ctx.act('word', { text });
    };
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const out = v.out || [];
    const playable = !!ctx.mine && !!ctx.playing && v.phase === 'play' && out.indexOf(ctx.seat) < 0;

    gallows(root, v);
    mask(root, v);
    note(root, ctx, v);
    scores(root, ctx, v);
    wordBox(root, ctx, v, playable);

    if (playable) {
      // Позначки на клавіатурі: зелена — літера в слові, притінена — промах.
      const state = {};
      (v.right || []).forEach((c) => state[c] = 'G');
      (v.wrong || []).forEach((c) => state[c] = 'B');
      HGames.ui.keyboardUa(root, (k) => {
        if (k === 'Enter') { const f = root.querySelector('.hword'); if (f) f.requestSubmit(); return; }
        if (k === 'Backspace') { const i = root.querySelector('.hword input'); if (i) i.value = i.value.slice(0, -1); return; }
        ctx.act('guess', { letter: k });
      }, state);
    } else {
      const kbd = root.querySelector(':scope > .gkbd');
      if (kbd) kbd.remove();
    }
  }

  HGames.register({
    id: 'hangman',
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o'],

    mount(root, ctx) {
      // Скелет ставимо один раз і в потрібному порядку: екранну клавіатуру каркас допише в кінець сам.
      root.innerHTML = '<div class="hwrap">'
        + '<div class="hgal"></div>'
        + '<div class="hside"><div class="hround"></div><div class="hmask"></div><div class="hnote muted small"></div></div>'
        + '</div>'
        + '<div class="hscores"></div>'
        + '<form class="hword" hidden><input type="text" maxlength="24" placeholder="ціле слово" autocomplete="off" '
        + 'spellcheck="false" enterkeyhint="send"><button class="primary" type="submit">Назвати</button></form>';
      paint(root, ctx);
    },

    update(root, ctx) { paint(root, ctx); },

    onKey(e, ctx) {
      const v = ctx.view || {};
      if (!ctx.mine || !ctx.playing || v.phase !== 'play') return false;
      if ((v.out || []).indexOf(ctx.seat) >= 0) return false;
      // літери читаємо з e.key: розкладка тут якраз і потрібна, це не WASD
      const ch = String(e.key || '').toLowerCase();
      if (ch.length !== 1 || ALPHABET.indexOf(ch) < 0) return false;
      ctx.act('guess', { letter: ch });
      return true;
    },

    status(ctx) {
      const v = ctx.view || {};
      if (v.phase === 'between') return 'Наступне слово через ' + (v.nextIn || 0) + '…';
      if (!ctx.playing) return '';
      if (!ctx.mine) return 'Дивишся збоку';
      if ((v.out || []).indexOf(ctx.seat) >= 0) return 'Це слово вже без тебе — чекай наступне';
      return 'Тисни літери або назви слово цілком';
    },
  });
})();
