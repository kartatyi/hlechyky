/*
  Віселиця. Правила й слово живуть на сервері; тут — риски, шибениця й дві кнопки.

  Вид (Impl/Hangman.cs):
  { round, of, mask: 'к_р__а', wrong: string[], right: string[], errors, maxErrors,
    scores: number[], out: number[], phase: 'play'|'between'|'done', nextIn, revealed, result,
    mode: 'race'|'turns', easy, turn: seat|null, turnUntil: ISO|null, turnMs,
    last: { no, seat, kind: 'hit'|'miss'|'word'|'wrong'|'timeout', text, n } | null }
  Поля mode/turn/last — з оновлення 24.09; старий вид без них малюється як «наввипередки».

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

  // Сервер бере одну дію на місце на секунду (Impl/Hangman.cs, ActEveryMs) і кожну зайву відбиває
  // текстом, який каркас малює червоним тостом. Набираючи літери в звичайному темпі, гравець зібрав
  // би три-чотири такі тости підряд, тож те саме вікно клієнт тримає й сам: поки воно триває, до хаба
  // нічого не летить, а клавіатура тьмяніє й сама повертається. Запас — на дорогу до сервера.
  const COOL_MS = 1000, COOL_LAG = 120;

  /// Хід із оглядкою на власний кулдаун. Повертає, чи справді пішло на сервер.
  /// Стан живе на ctx (він у каркаса один на кімнату), тож два столи поруч не заважають одне одному.
  function tryAct(ctx, action, payload) {
    // По черзі сервер ліміту не тримає (чужий хід і так не пройде) — не гальмуємо й тут.
    if ((ctx.view || {}).mode === 'turns') { ctx.act(action, payload); return true; }
    const now = Date.now();
    const left = (ctx.hangmanCool || 0) - now;
    if (left > 0) { cool(ctx, left); return false; }
    // Сервер лічить вікно від будь-якої дії, навіть відмовленої («Уже було»), — лічимо так само.
    ctx.hangmanCool = now + COOL_MS + COOL_LAG;
    cool(ctx, COOL_MS + COOL_LAG);
    ctx.act(action, payload);
    return true;
  }

  /// Видимий кулдаун: анімація на стільки, скільки лишилось. Саме анімація, а не таймер, — тоді
  /// нема чого чистити, коли картку приберуть посеред очікування.
  function cool(ctx, ms) {
    const root = ctx.hangmanRoot;
    const kbd = root && root.querySelector(':scope > .gkbd');
    if (!kbd) return;
    kbd.classList.remove('hcool');
    void kbd.offsetWidth;                 // перезапуск анімації на повторному натиску
    kbd.style.animationDuration = Math.max(120, Math.round(ms)) + 'ms';
    kbd.classList.add('hcool');
  }

  /// Скільки ліній шибениці малювати. Промахів на слово буває 6, 8 або 10 (складність), а ліній завжди
  /// вісім: на «важко» стовп із перекладиною стоять одразу (класика), на «легко» лінія додається не щоразу.
  function partsFor(v) {
    const max = v.maxErrors || 8, e = v.errors || 0;
    if (max <= PARTS.length) return Math.min(PARTS.length, e + (PARTS.length - max));
    return Math.min(PARTS.length, Math.floor(e * PARTS.length / max));
  }

  /// Шибениця. Перемальовуємо лише коли додався промах: інакше SVG блимав би на кожен тик.
  function gallows(root, v) {
    const el = root.querySelector('.hgal');
    const n = Math.max(0, partsFor(v));
    if (el.dataset.n === String(n)) return;
    // новий промах — шибениця здригається (не на першому малюванні і не при новому слові)
    const grew = el.dataset.n != null && n > +el.dataset.n;
    el.dataset.n = String(n);
    el.classList.remove('hshake');
    if (grew) { void el.offsetWidth; el.classList.add('hshake'); }
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
    const el = root.querySelector('.hmask');
    // Щойно відкриті літери підскакують: так видно, що саме відкрилось, навіть коли дивишся на клавіатуру.
    const before = el.dataset.mask || '';
    const sameWord = before.length === shown.length && v.round === +el.dataset.round;
    const html = shown.split('').map((c, i) => {
      if (c === '_') return '<span class="hl gap"></span>';
      const fresh = sameWord && before[i] === '_' && v.phase === 'play' ? ' pop' : '';
      return '<span class="hl' + (lost && open.indexOf(c) < 0 ? ' miss' : '') + fresh + '">' + c + '</span>';
    }).join('');
    el.dataset.mask = shown;
    el.dataset.round = String(v.round || 0);
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  /// Рахунок по місцях. Вибулих із цього слова притінюємо — видно, кому ще є що казати.
  function scores(root, ctx, v) {
    const out = v.out || [];
    let html = '';
    for (let i = 0; i < seatsOf(ctx); i++) {
      const nick = ctx.nickOf(i);
      if (!nick) continue;
      const turn = v.turn === i && v.phase === 'play';
      html += '<span class="chip hsc' + (out.indexOf(i) >= 0 ? ' off' : '') + (i === ctx.seat ? ' me' : '') + (turn ? ' turn' : '') + '">'
        + (turn ? '👉 ' : '') + ctx.esc(nick) + ' <b>' + ((v.scores || [])[i] || 0) + '</b></span>';
    }
    const el = root.querySelector('.hscores');
    if (el.innerHTML !== html) el.innerHTML = html;
  }

  /// Хто що щойно зробив: «Оля: «О» ×2 +2». У грі на кількох без цього не видно, чиї це літери.
  function lastLine(ctx, v) {
    const e = v.last;
    if (!e) return '';
    const who = e.seat === ctx.seat ? 'Ти' : (ctx.nickOf(e.seat) || 'хтось');
    const L = String(e.text || '').toUpperCase();
    switch (e.kind) {
      case 'hit': return who + ': «' + L + '»' + (e.n > 1 ? ' ×' + e.n : '') + ' ✓ +' + e.n;
      case 'miss': return who + ': «' + L + '» — нема ✗';
      case 'word': return who + ' називає слово цілком! +' + e.n;
      case 'wrong': return who + ': «' + e.text + '» — ні, мінус очко';
      case 'timeout': return who + ' задумався — хід далі';
      default: return '';
    }
  }

  /// Рядок над рисками: котре це слово і що зараз відбувається.
  function note(root, ctx, v) {
    const text = v.phase === 'done' ? 'Партію зіграно'
      : v.phase === 'between' ? (v.revealed ? 'Слово було: ' + v.revealed : 'Слово втекло')
        : 'Промахи: ' + (v.errors || 0) + ' з ' + (v.maxErrors || 8);
    const el = root.querySelector('.hnote');
    if (el.textContent !== text) el.textContent = text;
    const ev = root.querySelector('.hlast');
    const line = v.phase === 'play' ? lastLine(ctx, v) : '';
    if (ev.textContent !== line) {
      ev.textContent = line;
      ev.className = 'hlast small ' + (v.last ? 'k-' + v.last.kind : '');
    }
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
      // Слово теж під лімітом сервера. Поки кулдаун — набране не забираємо: хай спробує ще раз.
      if (tryAct(ctx, 'word', { text })) input.value = '';
    };
  }

  /// По черзі: чи зараз мій хід. Наввипередки (або старий вид без mode) — завжди.
  const myGo = (ctx, v) => v.mode !== 'turns' || v.turn === ctx.seat;

  /// Дуга-таймер ходу — лише по черзі.
  function turnArc(root, v) {
    const host = root.querySelector('.harc');
    const on = v.mode === 'turns' && v.phase === 'play' && v.turnUntil;
    host.hidden = !on;
    if (on) HGames.ui.timerArc(host, v.turnUntil, v.turnMs || 25000);
    else { const a = host.querySelector(':scope > .garc'); if (a && a._arc) a._arc.stop(); }
  }

  function paint(root, ctx) {
    ctx.hangmanRoot = root;          // onKey приходить без картки — беремо її з ctx
    const v = ctx.view || {};
    const out = v.out || [];
    const inWord = !!ctx.mine && !!ctx.playing && v.phase === 'play' && out.indexOf(ctx.seat) < 0;
    // По черзі клавіатура лишається на місці й чекає твого ходу притушеною: інакше вона зникала б і
    // з'являлась щоходу, і вся картка стрибала б.
    const playable = inWord && myGo(ctx, v);
    turnArc(root, v);

    gallows(root, v);
    mask(root, v);
    note(root, ctx, v);
    scores(root, ctx, v);
    wordBox(root, ctx, v, inWord);
    const wb = root.querySelector('.hword');
    wb.classList.toggle('hwait', inWord && !playable);
    wb.querySelectorAll('input, button').forEach((x) => { x.disabled = inWord && !playable; });

    if (inWord) {
      // Позначки на клавіатурі: зелена — літера в слові, притінена — промах.
      const state = {};
      (v.right || []).forEach((c) => state[c] = 'G');
      (v.wrong || []).forEach((c) => state[c] = 'B');
      HGames.ui.keyboardUa(root, (k) => {
        if (k === 'Enter') { const f = root.querySelector('.hword'); if (f) f.requestSubmit(); return; }
        if (k === 'Backspace') { const i = root.querySelector('.hword input'); if (i) i.value = i.value.slice(0, -1); return; }
        if (!myGo(ctx, ctx.view || {})) return;
        tryAct(ctx, 'guess', { letter: k });
      }, state);
      const kbd = root.querySelector(':scope > .gkbd');
      if (kbd) kbd.classList.toggle('hwait', !playable);
    } else {
      const kbd = root.querySelector(':scope > .gkbd');
      if (kbd) kbd.remove();
    }
  }

  HGames.register({
    id: 'hangman',
    icon: ICON,
    news: {
      v: '2026-09-24',
      title: 'Віселиця: по черзі і під настрій',
      items: [
        '👉 Новий режим «По черзі»: влучив — ходиш ще, промахнувся — хід сусідові, 25 секунд на роздуми',
        '🎚 Складність: легко (10 промахів і відкриті крайні літери), звичайно або важко (лише 6 промахів)',
        '⏱ Партія на 3, 5 чи 7 слів',
        '✨ Видно, хто яку літеру назвав, нові літери підстрибують, а шибениця здригається від промаху',
      ],
    },
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o'],

    mount(root, ctx) {
      // Скелет ставимо один раз і в потрібному порядку: екранну клавіатуру каркас допише в кінець сам.
      root.innerHTML = '<div class="hwrap">'
        + '<div class="hgal"></div>'
        + '<div class="hside"><div class="hround"></div><div class="hmask"></div><div class="hnote muted small"></div>'
        + '<div class="hlast small"></div></div>'
        + '<div class="harc" hidden></div>'
        + '</div>'
        + '<div class="hscores"></div>'
        + '<form class="hword" hidden><input type="text" maxlength="24" placeholder="ціле слово" autocomplete="off" '
        + 'spellcheck="false" enterkeyhint="send"><button class="primary" type="submit">Назвати</button></form>';
      paint(root, ctx);
    },

    update(root, ctx) { paint(root, ctx); },

    unmount(root) {
      const a = root.querySelector('.harc > .garc');
      if (a && a._arc) a._arc.stop();
    },

    onKey(e, ctx) {
      const v = ctx.view || {};
      if (!ctx.mine || !ctx.playing || v.phase !== 'play') return false;
      if ((v.out || []).indexOf(ctx.seat) >= 0) return false;
      if (!myGo(ctx, v)) return false;
      // літери читаємо з e.key: розкладка тут якраз і потрібна, це не WASD
      const ch = String(e.key || '').toLowerCase();
      if (ch.length !== 1 || ALPHABET.indexOf(ch) < 0) return false;
      // У кулдауні клавішу не з'їдаємо: хай сторінка робить із нею, що звикла.
      return tryAct(ctx, 'guess', { letter: ch });
    },

    status(ctx) {
      const v = ctx.view || {};
      // Після останнього слова наступного вже не буде: там рахунок, а не відлік.
      if (v.phase === 'between') {
        return v.round >= (v.of || 5) ? 'Рахуємо очки…' : 'Наступне слово через ' + (v.nextIn || 0) + '…';
      }
      if (!ctx.playing) return '';
      if (!ctx.mine) return 'Дивишся збоку';
      if ((v.out || []).indexOf(ctx.seat) >= 0) return 'Це слово вже без тебе — чекай наступне';
      if (v.mode === 'turns') {
        return v.turn === ctx.seat ? 'Твій хід: літера або слово цілком. Влучиш — ходиш ще'
          : 'Ходить ' + (ctx.nickOf(v.turn) || '…') + ' — чекай свою чергу';
      }
      return 'Тисни літери або назви слово цілком';
    },
  });
})();
