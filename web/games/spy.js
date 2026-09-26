/*
  Шпигун. Уся гра — розмова в балачці столу, а картка тримає те, чого в чаті не зробиш: мою таємну картку,
  покажчик «хто зараз питає», кнопки підозри й голосування, колоду локацій і розкриття раунду. Модуль нічого
  не додумує: чого нема у виді для мого місця (гра Hidden), того нема й на екрані.

  Вид із сервера (Impl/Spy.cs):
    { phase, round, of, endsAt, phaseMs, phaseLeftMs, clock: {endsAt, leftMs, paused, totalMs}, rules: {...},
      players: [{seat, nick, here, score, accused, ready}], asker, askedBy, askGrace,
      vote: {suspect, accuser, votes, need, endsAt}|null, blame: {votes, need}|null, deck: [[id, назва, емодзі]],
      me: {spy, loc, role}|null, reveal: {spy, loc, roles, how, gained, guess, suspect, accuser}|null,
      history: [{round, spy, loc, how, gained}], result: {winners}|null }
  Кадр (публічний, лише на зміну фази): { phase, round, of, endsAt, phaseMs, clockLeftMs, paused, asker } —
  ним живе тільки status(); усе решта — з виду, який приходить у тому ж тику.

  Швидкодія: DOM без канвасу (гра кнопкова й розмовна). Кожен блок перемальовується лише коли змінився його
  HTML-підпис; годинник і відлік — один rAF-цикл, що чіпає тільки textContent і кілька класів, стоїть,
  коли вкладку сховано, і вмирає в unmount. Відлік рахується від phaseLeftMs/clock.leftMs у момент приходу
  виду (performance.now), а не від годинника телефона, який буває «не той» на кілька секунд.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.5 6.5h11" stroke="var(--accent)" stroke-width="1.6" stroke-linecap="round" fill="none"/>'
    + '<path d="M5 6.5V4.3c0-.8.6-1.3 1.3-1.3h3.4c.7 0 1.3.5 1.3 1.3v2.2z" fill="var(--accent)"/>'
    + '<circle cx="5.4" cy="10.6" r="2.1" fill="none" stroke="var(--clay)" stroke-width="1.4"/>'
    + '<circle cx="10.6" cy="10.6" r="2.1" fill="none" stroke="var(--clay)" stroke-width="1.4"/>'
    + '<path d="M7.5 10.6h1" stroke="var(--clay)" stroke-width="1.4"/>'
    + '</svg>';

  const RUNNING = { deal: 1, play: 1, vote: 1, final: 1 };
  const HOW_ICON = { caught: '🔦', wrong: '😬', guessed: '🎯', misguess: '❌', timeout: '⏳', left: '🚪' };
  const HOW_SHORT = { caught: 'спіймали', wrong: 'засудили невинного', guessed: 'шпигун вгадав', misguess: 'шпигун схибив', timeout: 'не спіймали', left: 'шпигун утік' };
  const CONFIRM_MS = 3000;
  /// Скільки розгорнута картка ролі висить на початку раунду, перш ніж згорнутись у чіп.
  const CARD_PLAY_MS = 8000;

  const lsGet = (k) => { try { return localStorage.getItem(k); } catch { return null; } };
  const lsSet = (k, v) => { try { if (v == null) localStorage.removeItem(k); else localStorage.setItem(k, v); } catch { /* приватне вікно */ } };
  const reduced = () => { try { return matchMedia('(prefers-reduced-motion: reduce)').matches; } catch { return false; } };

  function mmss(ms) {
    const s = Math.max(0, Math.ceil(ms / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  }
  const pts = (n) => {
    const a = Math.abs(n) % 100, d = a % 10;
    return n + ' ' + (a >= 11 && a <= 14 ? 'очок' : d === 1 ? 'очко' : d >= 2 && d <= 4 ? 'очки' : 'очок');
  };

  // ---------------------------------------------------------------------------------------
  // Звук: лише синтез WebAudio, тихо, і лише після першого жесту людини на сторінці.
  // ---------------------------------------------------------------------------------------

  let actx = null;
  let heard = false;
  const wake = () => { heard = true; };
  try {
    addEventListener('pointerdown', wake, { once: true, capture: true });
    addEventListener('keydown', wake, { once: true, capture: true });
  } catch { /* старий браузер */ }

  const soundOn = () => lsGet('spy:sound') !== '0';

  function tone(freq, ms, at, gain) {
    if (!heard || !soundOn()) return;
    try {
      if (!actx) actx = new (window.AudioContext || window.webkitAudioContext)();
      if (actx.state === 'suspended') actx.resume().catch(() => { /* ще не можна */ });
      const t0 = actx.currentTime + (at || 0);
      const o = actx.createOscillator(), g = actx.createGain();
      o.type = 'sine';
      o.frequency.value = freq;
      g.gain.setValueAtTime(0.0001, t0);
      g.gain.exponentialRampToValueAtTime(gain || 0.12, t0 + 0.01);
      g.gain.exponentialRampToValueAtTime(0.0001, t0 + ms / 1000);
      o.connect(g).connect(actx.destination);
      o.start(t0);
      o.stop(t0 + ms / 1000 + 0.02);
    } catch { /* без звуку теж можна грати */ }
  }
  const tick = () => tone(880, 40, 0, 0.06);
  const bell = () => { tone(660, 120, 0); tone(880, 160, 0.13); };
  const chord = () => { tone(523, 180, 0); tone(659, 180, 0.09); tone(784, 260, 0.18); };

  // ---------------------------------------------------------------------------------------
  // Дрібниці про вид
  // ---------------------------------------------------------------------------------------

  const nickOf = (v, seat) => {
    const p = (v.players || []).find((x) => x.seat === seat);
    return (p && p.nick) || ('гравець ' + (seat + 1));
  };
  const locOf = (v, id) => (v.deck || []).find((d) => d[0] === id) || [id, id, '📍'];
  const present = (v) => (v.players || []).filter((p) => p.here);
  /// Чи я грав у цьому раунді й досі за столом.
  const playing = (v, seat) => !!v.me && seat != null && (v.players || []).some((p) => p.seat === seat && p.here);

  function phaseLabel(v) {
    const r = v.of > 1 ? 'Раунд ' + v.round + ' із ' + v.of : 'Один раунд';
    switch (v.phase) {
      case 'lobby': return 'Збираємось';
      case 'deal': return r + ' · роздача';
      case 'play': return r + ' · питаємо';
      case 'vote': return 'Голосування';
      case 'final': return 'Час вийшов — хто шпигун?';
      case 'reveal': return r + ' · розкриття';
      case 'done': return 'Кінець партії';
      default: return '';
    }
  }

  // ---------------------------------------------------------------------------------------
  // Каркас картки
  // ---------------------------------------------------------------------------------------

  function build(root, ctx) {
    const el = document.createElement('div');
    el.className = 'spy';
    el.innerHTML = '<div class="sp-grid"><div class="sp-main">'
      + '<div class="sp-top">'
      + '<div class="sp-clockbox"><b class="sp-clock">–:––</b><span class="sp-phase"></span></div>'
      + '<div class="sp-arc"></div>'
      + '<span class="sp-grow"></span>'
      + '<button type="button" class="sp-snd ghost" data-sp="sound" data-pad-skip></button>'
      + '</div>'
      + '<button type="button" class="sp-me" data-sp="card"></button>'
      + '<div class="sp-card" hidden></div>'
      + '<div class="sp-reveal" hidden></div>'
      + '<div class="sp-act"></div>'
      + '<div class="sp-players"></div>'
      + '<details class="sp-hist" hidden><summary class="muted small">Хроніка партії</summary><div class="sp-hlist"></div></details>'
      + '</div>'
      + '<div class="sp-side"><div class="sp-deck">'
      + '<div class="sp-dhead"><b>Локації в колоді</b><span class="sp-dcount muted small"></span></div>'
      + '<div class="sp-dhint muted small"></div>'
      + '<div class="sp-locs"></div>'
      + '<div class="sp-guess" hidden></div>'
      + '</div></div></div>';
    root.appendChild(el);

    const st = {
      ctx, view: null, key: '', raf: 0,
      phaseEnd: 0, clockEnd: 0, clockLeft: 0, clockRun: false, clockTxt: '', hot: null, last: null,
      busy: null, confirm: null, guessMode: false, pick: null,
      cardOpen: null, cardUntil: 0, paused: null,
      strikes: new Set(), strikeKey: '', lastTick: -1,
      q: {
        clock: el.querySelector('.sp-clock'), clockBox: el.querySelector('.sp-clockbox'), phase: el.querySelector('.sp-phase'),
        arc: el.querySelector('.sp-arc'), me: el.querySelector('.sp-me'), snd: el.querySelector('.sp-snd'),
        card: el.querySelector('.sp-card'), reveal: el.querySelector('.sp-reveal'), act: el.querySelector('.sp-act'),
        players: el.querySelector('.sp-players'), hist: el.querySelector('.sp-hist'), hlist: el.querySelector('.sp-hlist'),
        dcount: el.querySelector('.sp-dcount'), dhint: el.querySelector('.sp-dhint'), locs: el.querySelector('.sp-locs'),
        guess: el.querySelector('.sp-guess'), deck: el.querySelector('.sp-deck'),
      },
    };
    el._sp = st;
    // Шов для замірів (docs/games/dev/spy-perf.js): один крок rAF-циклу без самого rAF.
    st.step = () => frameStep(el);

    // Слухач один на картку, а свіжий ctx лежить у стані: інакше клік назавжди пішов би в перший.
    el.addEventListener('click', (e) => {
      const b = e.target.closest('[data-sp]');
      if (!b || b.disabled || !el.contains(b)) return;
      onClick(el, b);
    });
    st.onVis = () => { if (!document.hidden) loop(el); };
    document.addEventListener('visibilitychange', st.onVis);
    return el;
  }

  async function send(el, key, action, payload) {
    const st = el._sp;
    if (st.busy) return;
    st.busy = key;
    paint(el);
    try { await st.ctx.act(action, payload); } finally {
      st.busy = null;
      if (el.isConnected) paint(el);
    }
  }

  function onClick(el, b) {
    const st = el._sp, v = st.view || {};
    const seat = b.dataset.seat != null ? +b.dataset.seat : null;
    switch (b.dataset.sp) {
      case 'ask': send(el, 'ask:' + seat, 'ask', { seat }); break;
      case 'accuse': {
        // Двокроково: палець не має закінчити раунд випадково.
        const now = performance.now();
        if (st.confirm && st.confirm.seat === seat && now < st.confirm.until) {
          st.confirm = null;
          send(el, 'accuse:' + seat, 'accuse', { seat });
        } else {
          st.confirm = { seat, until: now + CONFIRM_MS };
          paint(el);
        }
        break;
      }
      case 'blame': send(el, 'blame:' + seat, 'blame', { seat }); break;
      case 'vote': send(el, 'vote:' + b.dataset.yes, 'vote', { yes: b.dataset.yes === '1' }); break;
      case 'ready': send(el, 'ready', 'ready', {}); break;
      case 'chat': HGames.openTable(); break;
      case 'guess-mode':
        st.guessMode = !st.guessMode;
        st.pick = null;
        paint(el);
        // На телефоні колода — у самому низу: одразу туди, щоб не шукати.
        if (st.guessMode) {
          try { st.q.deck.scrollIntoView({ block: 'nearest', behavior: reduced() ? 'auto' : 'smooth' }); } catch { /* старий браузер */ }
        }
        break;
      case 'guess-yes':
        if (st.pick) {
          const loc = st.pick;
          st.guessMode = false;
          st.pick = null;
          send(el, 'guess', 'guess', { loc });
        }
        break;
      case 'guess-no': st.guessMode = false; st.pick = null; paint(el); break;
      case 'loc': {
        const id = b.dataset.loc;
        if (st.guessMode && v.me && v.me.spy && (v.phase === 'play' || v.phase === 'final')) {
          st.pick = st.pick === id ? null : id;
        } else {
          if (st.strikes.has(id)) st.strikes.delete(id); else st.strikes.add(id);
          if (st.strikeKey) lsSet(st.strikeKey, st.strikes.size ? JSON.stringify([...st.strikes]) : null);
        }
        paint(el);
        break;
      }
      case 'card': st.cardOpen = !cardVisible(st, v); paint(el); break;
      case 'sound': lsSet('spy:sound', soundOn() ? '0' : '1'); heard = true; paint(el); if (soundOn()) tick(); break;
    }
  }

  function cardVisible(st, v) {
    if (!v.me) return false;
    if (st.cardOpen != null) return st.cardOpen && !!RUNNING[v.phase];
    return v.phase === 'deal' || (v.phase === 'play' && performance.now() < st.cardUntil);
  }

  /// Новий вид (а не той самий, перекликаний подією rooms): переставляємо відліки, ловимо зміну фази.
  function adopt(el, v) {
    const st = el._sp, now = performance.now();
    const key = v.round + ':' + v.phase;
    const fresh = key !== st.key;
    if (fresh) {
      const was = st.key.split(':')[1];
      st.key = key;
      st.confirm = null;
      if (v.phase !== 'play' && v.phase !== 'final') { st.guessMode = false; st.pick = null; }
      if (v.phase === 'deal') st.cardOpen = null;
      if (v.phase === 'play' && was === 'deal') st.cardUntil = now + CARD_PLAY_MS;
      if (was) {
        if (v.phase === 'vote' || v.phase === 'final') bell();
        else if (v.phase === 'reveal') chord();
      }
    }
    st.phaseEnd = now + (v.phaseLeftMs != null ? v.phaseLeftMs : Math.max(0, Date.parse(v.endsAt) - Date.now()));
    const c = v.clock || {};
    const run = !c.paused;
    const end = now + (c.leftMs || 0);
    // Той самий годинник, що й був, — не смикаємо його на затримку мережі (кілька десятків мс).
    if (fresh || run !== st.clockRun || Math.abs((run ? end : c.leftMs) - (run ? st.clockEnd : st.clockLeft)) > 400) {
      st.clockRun = run;
      st.clockEnd = end;
      st.clockLeft = c.leftMs || 0;
    }
    // Закреслення — свої на кожен раунд, живуть у localStorage (F5 їх не губить).
    const sk = st.ctx.room ? 'spy:strike:' + st.ctx.room.id + ':' + v.round : '';
    if (sk !== st.strikeKey) {
      st.strikeKey = sk;
      st.strikes = new Set();
      try { const raw = sk && lsGet(sk); if (raw) for (const id of JSON.parse(raw)) st.strikes.add(String(id)); } catch { /* биті — забули */ }
    }
    // Дуга фази: від залишку, а не від чужого годинника; у play головний — годинник раунду.
    if ((RUNNING[v.phase] && v.phase !== 'play') || v.phase === 'reveal') {
      st.q.arc.hidden = false;
      HGames.ui.timerArc(st.q.arc, new Date(Date.now() + (st.phaseEnd - now)).toISOString(), v.phaseMs || 1000);
    } else {
      st.q.arc.hidden = true;
      const a = st.q.arc.querySelector(':scope > .garc');
      if (a && a._arc) a._arc.stop();
    }
    st.q.arc.classList.toggle('sp-red', v.phase === 'final' || v.phase === 'vote');
    st.view = v;
  }

  // ---------------------------------------------------------------------------------------
  // Малювання — за підписами
  // ---------------------------------------------------------------------------------------

  function set(node, html) {
    if (node.dataset.sig !== html) { node.dataset.sig = html; node.innerHTML = html; }
  }

  /// Список однотипних вузлів (рядки гравців, чипи колоди): міняємо лише ті, чий HTML змінився. Питання
  /// в черзі чіпає два-три рядки з десяти, закреслення — один чип із двадцяти чотирьох.
  const tpl = document.createElement('template');
  function list(node, items) {
    const kids = node.children;
    if (node.dataset.sig !== '' + items.length || kids.length !== items.length) {
      node.dataset.sig = '' + items.length;
      node.innerHTML = items.join('');
      for (let i = 0; i < items.length; i++) kids[i]._sig = items[i];
      return;
    }
    for (let i = 0; i < items.length; i++) {
      if (kids[i]._sig === items[i]) continue;
      tpl.innerHTML = items[i];
      const fresh = tpl.content.firstElementChild;
      fresh._sig = items[i];
      node.replaceChild(fresh, kids[i]);
    }
  }

  function paint(el) {
    const st = el._sp, ctx = st.ctx;
    const v = ctx.view || {};
    if (!v.phase) { set(st.q.players, '<div class="gwait">чекаю на стіл…</div>'); return; }
    if (v !== st.view) adopt(el, v);
    const esc = ctx.esc;
    const mySeat = ctx.seat == null ? null : ctx.seat;
    const me = v.me;
    const inGame = playing(v, mySeat);

    // ---- шапка ----
    const ph = phaseLabel(v);
    if (st.q.phase.textContent !== ph) st.q.phase.textContent = ph;
    const showCard = cardVisible(st, v);
    // Чіп «хто я» — поки велика картка згорнута; сама картка теж кнопка й згортається дотиком.
    let chip, chipCls = 'sp-me';
    if (me && me.spy) { chip = '🕵️ Ти — шпигун'; chipCls += ' sp-me-spy'; }
    else if (me && me.loc) { const l = locOf(v, me.loc); chip = l[2] + ' ' + l[1] + ' · ' + (me.role || ''); }
    else chip = mySeat == null || v.phase !== 'lobby' ? '👀 Дивишся збоку' : '';
    const flip = me && RUNNING[v.phase];
    if (flip) chip += '  ▾';
    if (st.q.me.textContent !== chip) st.q.me.textContent = chip;
    if (st.q.me.className !== chipCls) st.q.me.className = chipCls;
    st.q.me.hidden = !chip || showCard || v.phase === 'done';
    st.q.me.disabled = !flip;
    st.q.me.title = flip ? 'Показати свою картку' : '';
    const snd = soundOn() ? '🔈' : '🔇';
    if (st.q.snd.textContent !== snd) { st.q.snd.textContent = snd; st.q.snd.title = soundOn() ? 'Звук увімкнено — вимкнути' : 'Звук вимкнено — увімкнути'; }
    st.q.clockBox.classList.toggle('sp-idle', !RUNNING[v.phase]);
    // На розкритті й після партії годинник раунду нічого не каже — лишаємо тільки назву фази.
    st.q.clock.hidden = !RUNNING[v.phase] && v.phase !== 'lobby';

    // ---- велика картка ----
    let card = '';
    if (showCard && me) {
      // Перевертання — усю роздачу: інакше чужий натиск посеред анімації перемалював би картку без неї.
      const first = v.phase === 'deal';
      if (me.spy) {
        card = '<button type="button" class="sp-cardin sp-spycard' + (first ? ' sp-flip' : '') + '" data-sp="card" title="Сховати картку"><div class="sp-cico" aria-hidden="true">🕵️</div>'
          + '<div class="sp-cbody"><b>Ти — ШПИГУН</b><span>Де всі — не знаєш. Слухай, підігравай і вгадай локацію з колоди.</span>'
          + '<span class="muted small">Вгадав — 4 очки. Спіймали — село святкує.</span></div></button>';
      } else {
        const l = locOf(v, me.loc);
        card = '<button type="button" class="sp-cardin' + (first ? ' sp-flip' : '') + '" data-sp="card" title="Сховати картку"><div class="sp-cico" aria-hidden="true">' + esc(l[2]) + '</div>'
          + '<div class="sp-cbody"><b>' + esc(l[1]) + '</b><span>Ти — ' + esc(me.role || '') + '</span>'
          + '<span class="muted small">Відповідай так, щоб свої зрозуміли, а шпигун — ні.</span></div></button>';
      }
    }
    st.q.card.hidden = !card;
    set(st.q.card, card);

    // ---- розкриття ----
    const rev = revealHtml(v, ctx);
    st.q.reveal.hidden = !rev;
    set(st.q.reveal, rev);

    // ---- що робити зараз ----
    set(st.q.act, actHtml(v, ctx, st, mySeat, inGame));

    // ---- гравці ----
    const rows = playersHtml(v, ctx, st, mySeat, inGame);
    if (rows.length) list(st.q.players, rows); else set(st.q.players, '<div class="gwait">за столом поки нікого</div>');

    // ---- хроніка ----
    const hist = (v.history || []).map((h) => {
      const l = locOf(v, h.loc);
      return '<div>Раунд ' + h.round + ' · ' + esc(l[2]) + ' ' + esc(l[1]) + ' · шпигун — ' + esc(nickOf(v, h.spy)) + ' · '
        + (HOW_SHORT[h.how] || h.how) + '</div>';
    }).join('');
    st.q.hist.hidden = !hist || v.phase === 'lobby';
    set(st.q.hlist, hist);
    const summary = v.phase === 'done' ? 'Як це було' : 'Хроніка партії';
    const sum = st.q.hist.firstElementChild;
    if (sum.textContent !== summary) sum.textContent = summary;
    // Партію зіграно — хроніку розгортаємо самі, але раз: що людина згорнула руками, те й лишається.
    const endKey = v.phase === 'done' ? (st.ctx.room ? st.ctx.room.id + ':' + st.ctx.room.round : 'done') : '';
    if (endKey && st.histOpened !== endKey) { st.histOpened = endKey; st.q.hist.open = true; }

    // ---- колода ----
    paintDeck(v, ctx, st, me);
  }

  function actHtml(v, ctx, st, mySeat, inGame) {
    const esc = ctx.esc;
    const me = v.me;
    const chat = '<button type="button" class="ghost" data-sp="chat">💬 До розмови</button>';
    const busy = (k) => (st.busy === k ? ' disabled aria-busy="true"' : '');
    const rules = v.rules ? v.rules.minutes + ' хв · ' + (v.rules.rounds === 1 ? 'один раунд' : v.rules.rounds + ' раунди')
      + ' · локації: ' + (v.rules.sets || []).map((s) => ({ all: 'усі', ua: 'наші', classic: 'класика' }[s] || s)).join(' + ') : '';
    switch (v.phase) {
      case 'lobby':
        return '<div class="sp-say">Треба щонайменше троє — клич друзів у балачках.</div>'
          + '<div class="muted small">' + esc(rules) + '. Усі, крім шпигуна, знають, де ви; шпигун — лише колоду локацій.</div>';
      case 'deal':
        return '<div class="sp-say">' + (me ? 'Запам\'ятай картку — раунд ось-ось почнеться.' : 'Роздають картки…') + '</div>'
          + '<div class="muted small">Питання й відповіді — у балачці столу. Картка підкаже, чия черга.</div>';
      case 'play': {
        const asker = v.asker, by = v.askedBy;
        let say;
        if (!inGame) say = '<div class="sp-say">Дивишся збоку: локацію тобі не скажуть. Читай розмову й вгадуй сам 😉</div>';
        else if (asker === mySeat) {
          say = '<div class="sp-say sp-hi">' + (by != null
            ? 'Тебе питає ' + esc(nickOf(v, by)) + ' — відповідай у балачці, а тоді питай сам: обери кого.'
            : 'Ти питаєш першим — обери кого й пиши питання в балачці.') + '</div>';
        } else if (by === mySeat) {
          say = '<div class="sp-say">Твоє питання — для ' + esc(nickOf(v, asker)) + '. Чекай відповіді в балачці.</div>';
        } else if (v.askGrace) {
          say = '<div class="sp-say">' + esc(nickOf(v, asker)) + ' мовчить уже пів хвилини — слово можна перехопити.</div>';
        } else {
          say = '<div class="sp-say">' + (by != null ? esc(nickOf(v, by)) + ' питає ' + esc(nickOf(v, asker)) + '. ' : 'Питає ' + esc(nickOf(v, asker)) + '. ')
            + 'Слухай і придивляйся.</div>';
        }
        const guess = inGame && me && me.spy
          ? '<button type="button" class="sp-go' + (st.guessMode ? ' on' : '') + '" data-sp="guess-mode">🎯 ' + (st.guessMode ? 'Скасувати' : 'Назвати локацію') + '</button>' : '';
        return say + '<div class="sp-btns">' + guess + chat + '</div>';
      }
      case 'vote': {
        const vt = v.vote;
        if (!vt) return '';
        const n = Object.keys(vt.votes || {}).length;
        const tally = '<div class="muted small">Проголосували ' + n + ' з ' + vt.need + ' · судимо лише одностайно, мовчання — «ні»</div>';
        const head = esc(nickOf(v, vt.accuser)) + ' підозрює: ' + esc(nickOf(v, vt.suspect)) + ' — шпигун?';
        if (!inGame) return '<div class="sp-say">' + head + '</div>' + tally;
        if (mySeat === vt.suspect) return '<div class="sp-say sp-hot2">Тебе підозрюють — переконуй у балачці!</div>' + tally + '<div class="sp-btns">' + chat + '</div>';
        const mine = vt.votes ? vt.votes[mySeat] : undefined;
        return '<div class="sp-say">' + head + '</div>'
          + '<div class="sp-btns sp-vote">'
          + '<button type="button" class="sp-big sp-yes' + (mine === true ? ' on' : '') + '" data-sp="vote" data-yes="1" data-pad-first' + busy('vote:1') + '>Так, це шпигун</button>'
          + '<button type="button" class="sp-big sp-no' + (mine === false ? ' on' : '') + '" data-sp="vote" data-yes="0"' + busy('vote:0') + '>Ні</button>'
          + '</div>' + tally;
      }
      case 'final': {
        const need = v.blame ? v.blame.need : 0;
        const guess = inGame && me && me.spy
          ? '<button type="button" class="sp-go' + (st.guessMode ? ' on' : '') + '" data-sp="guess-mode">🎯 ' + (st.guessMode ? 'Скасувати' : 'Назвати локацію') + '</button>' : '';
        const n = v.blame ? Object.keys(v.blame.votes || {}).length : 0;
        return '<div class="sp-say sp-hot2">' + (inGame ? 'Час вийшов. Покажи на шпигуна — засуджує більшість (' + need + ').' : 'Час вийшов: усі показують на шпигуна.') + '</div>'
          + '<div class="muted small">Показали ' + n + ' з ' + present(v).length + '. Передумати можна до кінця.</div>'
          + (guess ? '<div class="sp-btns">' + guess + chat + '</div>' : '');
      }
      case 'reveal': {
        if (!inGame) return '<div class="muted small">Наступний раунд — щойно всі натиснуть «Готово».</div>';
        const mine = (v.players || []).find((p) => p.seat === mySeat);
        const wait = present(v).filter((p) => !p.ready).length;
        if (mine && mine.ready) return '<div class="sp-say">✔ Чекаємо решту: ' + wait + '</div>';
        const last = v.round >= v.of;
        return '<div class="sp-btns"><button type="button" class="primary sp-big" data-sp="ready" data-pad-first' + busy('ready') + '>'
          + (last ? 'Готово — до підсумку' : 'Готово — наступний раунд') + '</button></div>';
      }
      case 'done': {
        const w = (v.result && v.result.winners) || [];
        if (!w.length) return '<div class="sp-final">🤝 Нічия</div>';
        const top = (v.players || []).find((p) => p.seat === w[0]);
        const who = w.map((s) => esc(nickOf(v, s))).join(' й ');
        return '<div class="sp-final">🏆 ' + (w.length === 1 ? 'Партію бере ' + who : 'Перемогу ділять ' + who)
          + (top ? ' — ' + (w.length > 1 ? 'по ' : '') + pts(top.score) : '') + '</div>';
      }
    }
    return '';
  }

  function playersHtml(v, ctx, st, mySeat, inGame) {
    const esc = ctx.esc;
    const run = !!RUNNING[v.phase];
    const vt = v.phase === 'vote' ? v.vote : null;
    const bl = v.phase === 'final' && v.blame ? v.blame.votes || {} : null;
    const blamed = {};
    if (bl) for (const k in bl) blamed[bl[k]] = (blamed[bl[k]] || 0) + 1;
    const myBlame = bl && mySeat != null ? bl[mySeat] : undefined;
    const myAccused = inGame && (v.players || []).some((p) => p.seat === mySeat && p.accused);
    const myTurn = v.phase === 'play' && inGame && v.asker === mySeat;
    const canAsk = v.phase === 'play' && inGame && (myTurn || v.askGrace);
    // Ролі й «+очки» — лише на розкритті; після партії в рядку лишаються підсумок і кубок.
    const rv = v.phase === 'reveal' ? v.reveal : null;
    const winners = v.phase === 'done' && v.result ? v.result.winners || [] : [];
    const now = performance.now();
    const conf = st.confirm && now < st.confirm.until ? st.confirm.seat : null;
    const busy = (k) => (st.busy === k ? ' disabled aria-busy="true"' : '');
    let firstMarked = false;

    const rows = (v.players || []).map((p, i) => {
      const other = p.seat !== mySeat;
      const tags = [];
      if (run && v.asker === p.seat && v.phase !== 'final') tags.push('<span class="sp-b sp-b-ask">' + (v.phase === 'deal' ? 'питає першим' : 'питає') + '</span>');
      if (run && v.askedBy === p.seat && v.phase === 'play') tags.push('<span class="sp-b sp-b-by">щойно питав</span>');
      if (run && p.accused && v.phase !== 'vote') tags.push('<span class="sp-b sp-b-acc" title="Уже висував підозру цього раунду">підозра ✓</span>');
      if (vt) {
        if (p.seat === vt.suspect) tags.push('<span class="sp-b sp-b-sus">під підозрою</span>');
        else if (p.here) {
          const x = vt.votes ? vt.votes[p.seat] : undefined;
          tags.push(x === true ? '<span class="sp-b sp-b-yes">✓ так</span>' : x === false ? '<span class="sp-b sp-b-no">✗ ні</span>' : '<span class="sp-b sp-b-wait">…</span>');
        }
      }
      if (bl && blamed[p.seat]) tags.push('<span class="sp-b sp-b-point" title="Скільки показали на нього">👉 ' + blamed[p.seat] + '</span>');
      if (rv) {
        if (rv.spy === p.seat) tags.push('<span class="sp-b sp-b-spy">🕵️ шпигун</span>');
        else if (rv.roles && rv.roles[p.seat]) tags.push('<span class="sp-b sp-b-role">' + esc(rv.roles[p.seat]) + '</span>');
        const g = rv.gained ? rv.gained[p.seat] : undefined;
        if (g > 0) tags.push('<span class="sp-b sp-b-plus" style="--n:' + i + '">+' + g + '</span>');
      }
      if (v.phase === 'reveal' && p.ready && p.here) tags.push('<span class="sp-b sp-b-ready">✔ готово</span>');
      if (winners.includes(p.seat)) tags.push('<span class="sp-b sp-b-win">🏆 переможець</span>');
      if (!p.here && v.phase !== 'lobby') tags.push('<span class="sp-b sp-b-gone">встав</span>');

      const btns = [];
      if (inGame && p.here && other) {
        if (canAsk && !(myTurn && p.seat === v.askedBy)) {
          const first = !firstMarked && myTurn;
          if (first) firstMarked = true;
          btns.push('<button type="button" class="sp-do sp-ask" data-sp="ask" data-seat="' + p.seat + '"' + (first ? ' data-pad-first' : '')
            + busy('ask:' + p.seat) + '>' + (myTurn ? 'Спитати' : 'Перехопити') + '</button>');
        }
        if (v.phase === 'play') {
          if (myAccused) btns.push('<button type="button" class="sp-do ghost" disabled title="Ти вже висував підозру цього раунду">Підозра</button>');
          else btns.push('<button type="button" class="sp-do ghost sp-acc' + (conf === p.seat ? ' sp-sure' : '') + '" data-sp="accuse" data-seat="' + p.seat + '"'
            + busy('accuse:' + p.seat) + '>' + (conf === p.seat ? 'Точно? Так' : 'Підозра') + '</button>');
        }
        if (v.phase === 'final') {
          btns.push('<button type="button" class="sp-do sp-blame' + (myBlame === p.seat ? ' on' : '') + '" data-sp="blame" data-seat="' + p.seat + '"'
            + busy('blame:' + p.seat) + '>' + (myBlame === p.seat ? '👉 Це він' : 'Це він') + '</button>');
        }
      }
      const cls = 'sp-p' + (p.seat === mySeat ? ' me' : '') + (!p.here ? ' gone' : '')
        + (vt && vt.suspect === p.seat ? ' sus' : '') + (run && v.asker === p.seat && v.phase === 'play' ? ' asking' : '')
        + (rv && rv.spy === p.seat ? ' wasspy' : '');
      return '<div class="' + cls + '">'
        + '<span class="sp-n sp-c' + (p.seat % 10) + '">' + (p.seat + 1) + '</span>'
        + '<span class="sp-who"><span class="sp-nick">' + esc(p.nick || ('гравець ' + (p.seat + 1))) + (p.seat === mySeat ? ' <i>(ти)</i>' : '') + '</span>'
        + (tags.length ? '<span class="sp-tags">' + tags.join('') + '</span>' : '') + '</span>'
        + (v.phase !== 'lobby' ? '<span class="sp-score" title="Очки партії">' + p.score + '</span>' : '')
        + (btns.length ? '<span class="sp-dos">' + btns.join('') + '</span>' : '')
        + '</div>';
    });
    return rows;
  }

  function revealHtml(v, ctx) {
    const rv = v.reveal;
    // Після партії останній раунд уже бачили на розкритті: тепер головне — хто взяв і «як це було» (хроніка).
    if (!rv || v.phase !== 'reveal') return '';
    const esc = ctx.esc;
    const l = locOf(v, rv.loc);
    const spy = esc(nickOf(v, rv.spy));
    const place = esc(l[2]) + ' ' + esc(l[1]);
    let text;
    switch (rv.how) {
      case 'caught': text = 'Шпигуна спіймали! Це ' + spy + '. Ви були: ' + place; break;
      case 'wrong': text = 'Засудили невинного — ' + esc(nickOf(v, rv.suspect)) + ' не шпигун. Шпигун — ' + spy + ', +4. Ви були: ' + place; break;
      case 'guessed': text = spy + ' — шпигун і вгадує: ' + place + '. Шпигунові +4'; break;
      case 'misguess': { const g = locOf(v, rv.guess); text = 'Шпигун ' + spy + ' ставить на «' + esc(g[1]) + '». А це ' + place + ' — селу по очку'; break; }
      case 'timeout': text = 'Час вийшов, шпигун (' + spy + ') не спійманий — +2. Ви були: ' + place; break;
      case 'left': text = 'Шпигун (' + spy + ') утік — селу по очку. Ви були: ' + place; break;
      default: text = place;
    }
    const good = rv.how === 'caught' || rv.how === 'misguess' || rv.how === 'left';
    return '<div class="sp-banner ' + (good ? 'sp-good' : 'sp-bad') + '"><span class="sp-bico" aria-hidden="true">' + (HOW_ICON[rv.how] || '🕵️') + '</span>'
      + '<span>' + text + '</span></div>';
  }

  function paintDeck(v, ctx, st, me) {
    const esc = ctx.esc;
    const deck = v.deck || [];
    const q = st.q;
    q.deck.hidden = !deck.length;
    const count = deck.length ? '· ' + deck.length : '';
    if (q.dcount.textContent !== count) q.dcount.textContent = count;
    const guessing = st.guessMode && me && me.spy && (v.phase === 'play' || v.phase === 'final');
    const rv = (v.phase === 'reveal' || v.phase === 'done') ? v.reveal : null;
    let hint;
    if (guessing) hint = 'Обери локацію й підтвердь — це кінець раунду, спроба одна.';
    else if (me && me.spy && RUNNING[v.phase]) hint = 'Тап — закреслити те, що точно не підходить. Готовий — «🎯 Назвати локацію».';
    else if (rv) hint = 'Зелена рамка — де всі були насправді.';
    else hint = 'Довідник для всіх: питай так, щоб відсікти зайве. Тап — закреслити для себе.';
    if (q.dhint.textContent !== hint) q.dhint.textContent = hint;
    q.deck.classList.toggle('sp-guessing', !!guessing);

    const html = deck.map((d) => {
      const id = d[0];
      let cls = 'sp-loc';
      if (st.strikes.has(id) && !rv) cls += ' struck';
      if (guessing && st.pick === id) cls += ' pick';
      if (rv && rv.loc === id) cls += ' true';
      if (rv && rv.how === 'misguess' && rv.guess === id) cls += ' false';
      if (!rv && me && me.loc === id && RUNNING[v.phase]) cls += ' here';
      return '<button type="button" class="' + cls + '" data-sp="loc" data-loc="' + esc(id) + '"'
        + (guessing && st.pick === id ? ' aria-pressed="true"' : '') + '>'
        + '<span class="sp-li" aria-hidden="true">' + esc(d[2]) + '</span><span class="sp-lt">' + esc(d[1]) + '</span></button>';
    });
    list(q.locs, html);

    let bar = '';
    if (guessing) {
      bar = st.pick
        ? '<span>Назвати «' + esc(locOf(v, st.pick)[1]) + '»? Це кінець раунду.</span>'
          + '<button type="button" class="primary" data-sp="guess-yes" data-pad-first' + (st.busy === 'guess' ? ' disabled' : '') + '>Так, це тут</button>'
          + '<button type="button" class="ghost" data-sp="guess-no">Ні, ще подумаю</button>'
        : '<span>Обери локацію в колоді.</span><button type="button" class="ghost" data-sp="guess-no">Скасувати</button>';
    }
    q.guess.hidden = !bar;
    set(q.guess, bar);
  }

  // ---------------------------------------------------------------------------------------
  // Годинник: один rAF на картку, лише textContent і класи
  // ---------------------------------------------------------------------------------------

  function loop(el) {
    const st = el._sp;
    if (!st || st.raf || document.hidden || !el.isConnected) return;
    const step = () => {
      st.raf = 0;
      if (!el.isConnected || document.hidden) return;
      frameStep(el);
      st.raf = requestAnimationFrame(step);
    };
    st.raf = requestAnimationFrame(step);
  }

  function frameStep(el) {
    const st = el._sp, v = st.view;
    if (!v) return;
    const now = performance.now();
    const left = st.clockRun ? st.clockEnd - now : st.clockLeft;
    const idle = !RUNNING[v.phase];
    const txt = (idle ? '' : st.clockRun ? '' : '⏸ ') + (v.phase === 'lobby' ? mmss((v.rules && v.rules.minutes * 60000) || 0) : mmss(left));
    if (txt !== st.clockTxt) {
      st.clockTxt = txt;
      st.q.clock.textContent = txt;
      // «тік» щосекунди на останніх десяти — лише поки годинник справді йде
      const sec = Math.ceil(left / 1000);
      if (st.clockRun && v.phase === 'play' && sec <= 10 && sec > 0 && sec !== st.lastTick) { st.lastTick = sec; tick(); }
    }
    const hot = st.clockRun && left < 60000;
    const last = st.clockRun && left <= 10000;
    if (hot !== st.hot) { st.hot = hot; st.q.clockBox.classList.toggle('sp-hot', hot); }
    if (last !== st.last) { st.last = last; st.q.clockBox.classList.toggle('sp-last', last); }
    const paused = !st.clockRun && !!RUNNING[v.phase];
    if (paused !== st.paused) { st.paused = paused; st.q.clockBox.classList.toggle('sp-paused', paused); }
    // Двокрокова «Підозра» гасне сама; розгорнута на старті картка згортається в чіп.
    let again = false;
    if (st.confirm && now > st.confirm.until) { st.confirm = null; again = true; }
    if (st.cardOpen == null && v.phase === 'play' && !st.q.card.hidden && now > st.cardUntil) again = true;
    if (again) paint(el);
  }

  // ---------------------------------------------------------------------------------------

  HGames.register({
    id: 'spy',
    icon: ICON,
    // Розмова тут і є гра: балачку столу каркас розгортає сам (ПК), на телефоні — кнопка «💬 До розмови».
    talk: 'main',
    seatNames: (i) => String(i + 1),
    seatClass: ['sp-s0', 'sp-s1', 'sp-s2', 'sp-s3', 'sp-s4', 'sp-s5', 'sp-s6', 'sp-s7', 'sp-s8', 'sp-s9'],
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Шпигун',
      items: [
        '🕵️ Усі знають, де вони, — крім одного. Шпигун мусить вгадати локацію, село — вгадати шпигуна',
        '💬 Питайте одне одного в балачці столу; картка підказує, чия черга питати',
        '👉 Раз за раунд можна висунути підозру: якщо всі згодні — картки на стіл',
        '🎯 Шпигун будь-коли може зупинити гру й назвати локацію: вгадав — 4 очки',
        '🏆 Три раунди, у кожному новий шпигун; хто набрав більше очок — той і взяв',
      ],
    },
    // pad не оголошуємо: гра кнопкова, кільце фокуса шару пада ходить по кнопках саме (PROTOCOL §3).

    mount(root, ctx) {
      const el = build(root, ctx);
      paint(el);
      loop(el);
    },

    update(root, ctx) {
      const el = root.querySelector(':scope > .spy');
      if (!el) return;
      el._sp.ctx = ctx;
      paint(el);
      loop(el);
    },

    /// Кадр летить лише на зміну фази, і вид іде в тому самому тику — тут лише шапка, щоб не чекати.
    frame(root, ctx, f) {
      const el = root.querySelector(':scope > .spy');
      if (!el || !f || !f.phase || !ctx.view) return;
      const ph = phaseLabel(Object.assign({}, ctx.view, { phase: f.phase, round: f.round, of: f.of }));
      if (el._sp.q.phase.textContent !== ph) el._sp.q.phase.textContent = ph;
    },

    unmount(root) {
      const el = root.querySelector(':scope > .spy');
      if (!el) return;
      const st = el._sp;
      if (st.raf) cancelAnimationFrame(st.raf);
      st.raf = 0;
      document.removeEventListener('visibilitychange', st.onVis);
      const a = st.q.arc.querySelector(':scope > .garc');
      if (a && a._arc) a._arc.stop();
    },

    status(ctx) {
      // Фазу беремо з кадра, поки кімната грає; після «Ще раз» кадр ще з минулої партії, а партію, яку
      // закрив чийсь вихід, завершує не тик — тоді віримо виду (так само, як мафія).
      const f = ctx.frame || {};
      const over = !ctx.room || ctx.room.status !== 'playing';
      const v = ctx.view || {};
      const src = f.phase && !over && !(ctx.playing && f.phase === 'done') && f.round === v.round ? f : v;
      const phase = src.phase;
      if (!phase || phase === 'lobby' || phase === 'done') return '';
      const r = (v.of || src.of) > 1 ? 'Раунд ' + (src.round || v.round) + ' із ' + (src.of || v.of) : 'Один раунд';
      switch (phase) {
        case 'deal': return 'Роздано — дивись картку';
        case 'play': {
          const a = v.asker != null ? v.asker : src.asker;
          if (a == null) return r;
          return r + ' · ' + (a === ctx.seat && v.me ? 'твоя черга питати' : 'питає ' + nickOf(v, a));
        }
        case 'vote': return v.vote ? 'Голосування: ' + nickOf(v, v.vote.accuser) + ' проти ' + nickOf(v, v.vote.suspect) : 'Голосування';
        case 'final': return 'Час вийшов — хто шпигун?';
        case 'reveal': return 'Розкриття';
      }
      return '';
    },
  });
})();
