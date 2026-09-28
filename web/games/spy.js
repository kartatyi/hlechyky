/*
  Шпигун. Уся гра — розмова в балачці столу, а картка тримає те, чого в чаті не зробиш: мою таємну картку,
  покажчик «хто зараз питає», кнопки підозри й голосування, колоду локацій і розкриття раунду. Модуль нічого
  не додумує: чого нема у виді для мого місця (гра Hidden), того нема й на екрані.

  Вид із сервера (Impl/Spy.cs):
    { phase, round, of, endsAt, phaseMs, phaseLeftMs, clock: {endsAt, leftMs, paused, totalMs}, rules: {..., each},
      players: [{seat, nick, here, score, accused, ready}], asker, askedBy, askGrace,
      vote: {suspect, accuser, votes, need, endsAt}|null, blame: {votes, need}|null, deck: [[id, назва, емодзі]],
      me: {spy, loc, role}|null, reveal: {spy, loc, roles, how, gained, guess, suspect, accuser, pointed}|null,
      history: [{round, spy, loc, how, gained}], result: {winners, folded}|null, names: {місце: нік}|null }
  Кадр (публічний, лише на зміну фази): { phase, round, of, endsAt, phaseMs, clockLeftMs, paused, asker } —
  ним живе тільки status(); усе решта — з виду, який приходить у тому ж тику.

  Швидкодія й чуйність: DOM без канвасу (гра кнопкова й розмовна). Кожен шматок перемальовується лише коли
  змінився його HTML-підпис, і шматки дрібні: рядок гравця — це п'ять слотів (бейджі, позначки, очки, кнопки…),
  «що робити» — три (порада, кнопки, дрібний рядок). Тож чужий голос міняє бейдж «✓ так» у рядку, а не кнопки
  «Так/Ні» в тебе під пальцем: вони не блимають, клік і фокус не губляться. Годинник і відлік — один rAF-цикл,
  що чіпає тільки textContent і кілька класів, стоїть, коли вкладку сховано, і вмирає в unmount.
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
  const HOW_ICON = { caught: '🔦', wrong: '😬', guessed: '🎯', misguess: '❌', timeout: '⏳', left: '🚪', fold: '🤝' };
  const HOW_SHORT = { caught: 'спіймали', wrong: 'засудили невинного', guessed: 'локацію вгадано', misguess: 'хибна здогадка', timeout: 'не спіймали', left: 'утеча з-за столу', fold: 'не дограли' };
  /// Хто виграв раунд: село чи шпигун. Від цього — колір банера для кожного свій.
  const VILLAGE_WINS = { caught: 1, misguess: 1, left: 1 };
  const SPY_WINS = { wrong: 1, guessed: 1, timeout: 1 };
  const CONFIRM_MS = 3000;
  /// Скільки розгорнута картка ролі висить на початку раунду, перш ніж згорнутись у чіп.
  const CARD_PLAY_MS = 8000;
  /// Скільки після зміни фази дотик по колоді не рахується: верстка під пальцем щойно з'їхала.
  const SHIFT_GUARD_MS = 450;
  /// Скільки висить рядок «підозра не пройшла» (Глек про неї мовчить — картка каже сама).
  const LAST_VOTE_MS = 9000;

  /// Підказки для тих, хто не знає, що спитати: загальні, щоб не видати локацію, але відсікали зайве.
  const TIPS = [
    'Що тут зазвичай чутно?', 'У чому сюди краще приходити?', 'Скільки тут зазвичай людей?',
    'Ти тут частіше вдень чи ввечері?', 'Чим тут пахне найсильніше?', 'Сюди ходять із дітьми?',
    'Тут треба платити?', 'Що звідси варто взяти додому?', 'Тут можна поїсти?', 'Як довго тут зазвичай сидять?',
    'Тобі тут весело чи нудно?', 'Тут холодно чи тепло?', 'Що тут найчастіше ламається?', 'Хто тут головний?',
    'Тут бувають черги?', 'Сюди приїжджають чи приходять пішки?', 'Що тут не можна робити?', 'Тут голосно?',
    'Що в тебе зараз у руках?', 'Тут є де сісти?', 'Буваєш тут щотижня?', 'Сюди пускають із собакою?',
    'Тут потрібен особливий одяг?', 'Що тут найдорожче?', 'Бабусі тут було б затишно?', 'Тут можна заснути?',
    'Сюди беруть фотоапарат?', 'Що тут роблять, коли йде дощ?', 'Тут легко загубитись?',
    'Про що тут найчастіше сперечаються?', 'Тут грає музика?', 'Що тебе тут найбільше дратує?',
    'Сюди ходять парами чи гуртом?', 'Тут важливо не спізнюватись?', 'Що тут поцупив би шпигун?', 'Тут є вайфай?',
  ];

  /// «Як грати» в лобі. За перший день на проді — жодної партії: одним рядком «усі, крім шпигуна, знають, де ви»
  /// новачок не розумів, що робити далі, і що питати можна й уголос (друзі часто сидять у дзвінку чи поруч).
  const HOW = '<ul class="spy-how">'
    + '<li>🃏 Кожен бачить свою картку: де ви й хто ти там. Один із вас — шпигун, він місця не знає</li>'
    + '<li>💬 Питайте по черзі одне одного — у балачці столу або вголос, якщо ви поруч чи в дзвінку. Картка підкаже, чия черга</li>'
    + '<li>🤫 Відповідай так, щоб свої зрозуміли, а шпигун — ні. Шпигун підіграє й слухає</li>'
    + '<li>👉 Підозрюєш когось — «Підозра»: якщо всі згодні, картки на стіл</li>'
    + '<li>🎯 Шпигун будь-коли може назвати місце з колоди: вгадав — йому 4 очки, схибив чи спіймали — селу по очку</li>'
    + '</ul>';

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
  /// «Оля», «Оля й Петро», «Оля, Петро й Ганна».
  const joinNames = (all) => (all.length > 1 ? all.slice(0, -1).join(', ') + ' й ' + all[all.length - 1] : all[0] || '');

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
  /// Ім'я того, хто грав партію: після неї на його звільнене місце вже може сісти новачок (players — про новачка),
  /// а хроніка й переможці мусять лишитись при своїх іменах — для цього вид після партії несе names.
  const matchNick = (v, seat) => (v.names && v.names[seat]) || nickOf(v, seat);
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
  // Балачка столу: після «Спитати» — одразу туди, з ніком того, кого питаєш
  // ---------------------------------------------------------------------------------------

  /// Поле вводу балачки столу (каркас, app.js). Нема — то й нема: гра без цього теж грається.
  const chatInput = () => { try { return document.querySelector('.tchat .tc-form input'); } catch { return null; } };
  /// Порожнє поле або поле, де лише «Петро, » від минулого «Спитати» — його можна переписати; чужу чернетку — ні.
  const PREFIX = /^[^,\n]{1,40}, $/;

  function draftTo(st, nick) {
    const inp = chatInput();
    if (!inp) return;
    const cur = inp.value;
    const own = !!st.draft && cur === st.draft.value;          // там наша ж підкинута ідея — переносимо її
    if (cur && !own && !PREFIX.test(cur)) return;
    const rest = own ? cur.slice((st.draft.base || '').length) : '';
    const base = nick + ', ';
    inp.value = rest ? base + rest.charAt(0).toLowerCase() + rest.slice(1) : base;
    st.draft = rest ? { base, value: inp.value } : null;
  }

  /// Підкинута ідея питання — у поле балачки: після «Петро, » з малої літери. Наступний 💡 міняє свою ж ідею,
  /// а написане людиною не чіпає. Повертає, чи вдалось.
  function draftTip(st, q) {
    const inp = chatInput();
    if (!inp) return false;
    const cur = inp.value;
    const base = st.draft && cur === st.draft.value ? st.draft.base : cur;
    if (base && !PREFIX.test(base)) return false;
    const value = base ? base + q.charAt(0).toLowerCase() + q.slice(1) : q;
    inp.value = value;
    st.draft = { base, value };
    return true;
  }

  function openTable() { try { HGames.openTable(); } catch { /* каркас без балачки */ } }

  // ---------------------------------------------------------------------------------------
  // Каркас картки
  // ---------------------------------------------------------------------------------------

  function build(root, ctx) {
    const el = document.createElement('div');
    el.className = 'spy';
    el.innerHTML = '<div class="spy-grid"><div class="spy-main">'
      + '<div class="spy-top">'
      + '<div class="spy-clockbox"><b class="spy-clock">–:––</b><span class="spy-phase"></span></div>'
      + '<div class="spy-arc"></div>'
      + '<span class="spy-grow"></span>'
      + '<button type="button" class="spy-snd ghost" data-sp="sound" data-pad-skip></button>'
      + '</div>'
      + '<div class="spy-reveal" hidden></div>'
      + '<div class="spy-act"><div class="spy-say"></div><div class="spy-btns"></div><div class="spy-note muted small"></div></div>'
      + '<div class="spy-players"></div>'
      + '<details class="spy-hist" hidden><summary class="muted small">Хроніка партії</summary><div class="spy-hlist"></div></details>'
      + '</div>'
      + '<div class="spy-side">'
      + '<button type="button" class="spy-me" data-sp="card"></button>'
      + '<div class="spy-card" hidden></div>'
      + '<div class="spy-deck">'
      + '<div class="spy-dhead"><b>Локації в колоді</b><span class="spy-dcount muted small"></span></div>'
      + '<div class="spy-dhint muted small"></div>'
      + '<div class="spy-locs"></div>'
      + '<div class="spy-guess" hidden></div>'
      + '</div></div></div>';
    root.appendChild(el);

    const st = {
      ctx, view: null, key: '', raf: 0,
      phaseEnd: 0, clockEnd: 0, clockLeft: 0, clockRun: false, clockTxt: '', hot: null, last: null,
      busy: null, busyHold: false, busyT: 0, confirm: null, guessMode: false, pick: null,
      cardOpen: null, cardUntil: 0, paused: null, shiftAt: 0, lastVote: null, tip: -1, padKey: '',
      strikes: new Set(), strikeKey: '', lastTick: -1,
      q: {
        grid: el.querySelector('.spy-grid'),
        clock: el.querySelector('.spy-clock'), clockBox: el.querySelector('.spy-clockbox'), phase: el.querySelector('.spy-phase'),
        arc: el.querySelector('.spy-arc'), me: el.querySelector('.spy-me'), snd: el.querySelector('.spy-snd'),
        card: el.querySelector('.spy-card'), reveal: el.querySelector('.spy-reveal'), act: el.querySelector('.spy-act'),
        say: el.querySelector('.spy-say'), btns: el.querySelector('.spy-btns'), note: el.querySelector('.spy-note'),
        players: el.querySelector('.spy-players'), hist: el.querySelector('.spy-hist'), hlist: el.querySelector('.spy-hlist'),
        dcount: el.querySelector('.spy-dcount'), dhint: el.querySelector('.spy-dhint'), locs: el.querySelector('.spy-locs'),
        guess: el.querySelector('.spy-guess'), deck: el.querySelector('.spy-deck'),
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

  /// Хід на сервер. Кнопка «зайнята», доки не прийде свіжий вид: гра реалтаймова, і вид після ходу приносить
  /// найближчий тик (≤ 250 мс) — якщо відпустити кнопку вже з відповіддю, другий дотик по старому виду дав би
  /// зайву відмову («Зараз питає Оля»). Відмова ж відпускає кнопку одразу; секунда — запобіжник, якщо вид загубився.
  async function send(el, key, action, payload) {
    const st = el._sp;
    if (st.busy) return null;
    st.busy = key;
    st.busyHold = false;
    paint(el);
    let r = null;
    try { r = await st.ctx.act(action, payload); } catch { r = null; }
    if (st.busy === key) {
      if (r && r.ok) {
        st.busyHold = true;
        clearTimeout(st.busyT);
        st.busyT = setTimeout(() => {
          if (st.busy !== key) return;
          st.busy = null;
          st.busyHold = false;
          if (el.isConnected) paint(el);
        }, 1000);
      } else st.busy = null;
    }
    if (el.isConnected) paint(el);
    return r;
  }

  function onClick(el, b) {
    const st = el._sp, v = st.view || {};
    const seat = b.dataset.seat != null ? +b.dataset.seat : null;
    switch (b.dataset.sp) {
      case 'ask': {
        const nick = nickOf(v, seat);
        send(el, 'ask:' + seat, 'ask', { seat }).then((r) => {
          // Спитав — одразу в балачку з «Петро, » у полі: питання пишуть там, і це на крок менше (на телефоні — два).
          if (r && r.ok && el.isConnected) { draftTo(st, nick); openTable(); }
        });
        break;
      }
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
      case 'chat': openTable(); break;
      case 'tip': {
        let i = Math.floor(Math.random() * TIPS.length);
        if (i === st.tip) i = (i + 1) % TIPS.length;
        st.tip = i;
        st.tipIn = draftTip(st, TIPS[i]);
        paint(el);
        break;
      }
      case 'guess-mode':
        st.guessMode = !st.guessMode;
        st.pick = null;
        paint(el);
        // На телефоні колода — у самому низу: одразу туди, щоб не шукати.
        if (st.guessMode) {
          try { st.q.deck.scrollIntoView({ block: 'start', behavior: reduced() ? 'auto' : 'smooth' }); } catch { /* старий браузер */ }
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
        // Фаза щойно змінилась — чипи під пальцем з'їхали, і дотик влучив би не туди, куди цілились.
        if (performance.now() - st.shiftAt < SHIFT_GUARD_MS) break;
        const id = b.dataset.loc;
        if (guessingNow(st, v)) {
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

  const guessingNow = (st, v) => st.guessMode && v.me && v.me.spy && (v.phase === 'play' || v.phase === 'final');

  function cardVisible(st, v) {
    if (!v.me) return false;
    if (st.cardOpen != null) return st.cardOpen && !!RUNNING[v.phase];
    return v.phase === 'deal' || (v.phase === 'play' && performance.now() < st.cardUntil);
  }

  /// Новий вид (а не той самий, перекликаний подією rooms): переставляємо відліки, ловимо зміну фази.
  function adopt(el, v) {
    const st = el._sp, now = performance.now();
    const prev = st.view;
    // Хід дійшов (чи будь-що інше змінилось) — кнопку, що чекала на вид, відпускаємо.
    if (st.busyHold) { st.busy = null; st.busyHold = false; clearTimeout(st.busyT); }
    const key = v.round + ':' + v.phase;
    const fresh = key !== st.key;
    if (fresh) {
      const was = st.key.split(':')[1];
      st.key = key;
      st.confirm = null;
      st.shiftAt = now;
      st.tip = -1;
      // Вгадування голосування лише відкладає: після нього шпигун повертається туди ж, з тим самим вибором.
      if (!RUNNING[v.phase]) { st.guessMode = false; st.pick = null; }
      else if (v.phase === 'vote' && st.guessMode && v.me && v.me.spy) {
        try { st.ctx.toast('Вгадування відкладено: іде голосування', ''); } catch { /* без тосту */ }
      }
      if (v.phase === 'deal') st.cardOpen = null;
      if (v.phase === 'play' && was === 'deal') st.cardUntil = now + CARD_PLAY_MS;
      // Підозра не пройшла: Глек про це мовчить (балачка — для питань), тож скаже картка.
      st.lastVote = was === 'vote' && v.phase === 'play' && prev && prev.vote && prev.round === v.round
        ? { suspect: prev.vote.suspect, until: now + LAST_VOTE_MS } : null;
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
    // Закреслення — свої на кожен раунд, живуть у localStorage (F5 їх не губить). У ключі ще й номер партії
    // столу (room.round): після «Ще раз» раунди знову з першого, і чужі закреслення з минулої партії не воскреснуть.
    const room = st.ctx.room;
    const pre = room ? 'spy:strike:' + room.id + ':' + (room.round || 0) + ':' : '';
    const sk = pre ? pre + v.round : '';
    if (sk !== st.strikeKey) {
      st.strikeKey = sk;
      st.strikes = new Set();
      try {
        const raw = sk && lsGet(sk);
        if (raw) for (const id of JSON.parse(raw)) st.strikes.add(String(id));
        // Прибрати за собою: минулі партії цього столу вже нікому не потрібні, а чужі столи — лише коли їх
        // назбиралось забагато (два столи на екрані одночасно не мають витирати одне одному закреслення).
        const mine = room ? 'spy:strike:' + room.id + ':' : '';
        const keys = [];
        for (let i = 0; i < localStorage.length; i++) {
          const k = localStorage.key(i);
          if (k && k.startsWith('spy:strike:') && !k.startsWith(pre)) keys.push(k);
        }
        for (const k of keys) if (k.startsWith(mine) || keys.length > 40) localStorage.removeItem(k);
      } catch { /* приватне вікно чи биті дані — просто без закреслень */ }
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
    st.q.arc.classList.toggle('spy-red', v.phase === 'final' || v.phase === 'vote');
    st.view = v;
  }

  // ---------------------------------------------------------------------------------------
  // Малювання — за підписами
  // ---------------------------------------------------------------------------------------

  function set(node, html) {
    if (node.dataset.sig !== html) { node.dataset.sig = html; node.innerHTML = html; }
  }
  function cls(node, c) { if (node.className !== c) node.className = c; }

  /// Рядки гравців: вузол на місце, у ньому п'ять слотів. Міняються лише ті слоти, чий HTML змінився: чужий голос
  /// перемальовує бейдж «✓ так», але не кнопки поруч; «✔ готово» — позначку, але не «+4», що вже відскакав.
  const SLOTS = ['.spy-n', '.spy-nick', '.spy-tags', '.spy-marks', '.spy-score', '.spy-dos'];
  const ROW = '<div><span class="spy-n"></span><span class="spy-who"><span class="spy-nick"></span><span class="spy-tags"></span>'
    + '<span class="spy-marks"></span></span><span class="spy-score"></span><span class="spy-dos"></span></div>';

  function rows(node, items) {
    const order = items.map((r) => r.seat).join(',');
    if (node.dataset.sig !== order || node.children.length !== items.length) {
      node.dataset.sig = order;
      node.innerHTML = ROW.repeat(items.length);
      for (const kid of node.children) kid._slots = SLOTS.map((q) => kid.querySelector(q));
    }
    for (let i = 0; i < items.length; i++) {
      const kid = node.children[i], r = items[i];
      cls(kid, r.cls);
      const parts = [r.n, r.nick, r.tags, r.marks, r.score, r.dos];
      for (let j = 0; j < parts.length; j++) {
        const slot = kid._slots[j];
        if (slot._sig !== parts[j]) { slot._sig = parts[j]; slot.innerHTML = parts[j]; }
      }
      const nc = 'spy-n spy-c' + (r.seat % 10);
      cls(kid._slots[0], nc);
    }
  }

  /// Чипи колоди: міняємо лише той, чий HTML змінився (закреслення — один чип із двадцяти чотирьох).
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
    if (!v.phase) { set(st.q.say, '<div class="gwait">чекаю на стіл…</div>'); return; }
    if (v !== st.view) adopt(el, v);
    const esc = ctx.esc;
    const mySeat = ctx.seat == null ? null : ctx.seat;
    const me = v.me;
    const inGame = playing(v, mySeat);

    // ---- шапка ----
    const ph = phaseLabel(v);
    if (st.q.phase.textContent !== ph) st.q.phase.textContent = ph;
    const snd = soundOn() ? '🔈' : '🔇';
    if (st.q.snd.textContent !== snd) { st.q.snd.textContent = snd; st.q.snd.title = soundOn() ? 'Звук увімкнено — вимкнути' : 'Звук вимкнено — увімкнути'; }
    st.q.clockBox.classList.toggle('spy-idle', !RUNNING[v.phase]);
    // На розкритті й після партії годинник раунду нічого не каже — лишаємо тільки назву фази.
    st.q.clock.hidden = !RUNNING[v.phase] && v.phase !== 'lobby';

    // ---- «хто я»: чіп або велика картка (на широкій картці — над колодою, на вузькій — під годинником) ----
    const showCard = cardVisible(st, v);
    let chip, chipCls = 'spy-me';
    if (me && me.spy) { chip = '🕵️ Ти — шпигун'; chipCls += ' spy-me-spy'; }
    else if (me && me.loc) { const l = locOf(v, me.loc); chip = l[2] + ' ' + l[1] + ' · ти — ' + (me.role || ''); }
    else chip = mySeat == null || v.phase !== 'lobby' ? '👀 Дивишся збоку' : '';
    const flip = me && RUNNING[v.phase];
    if (flip) chip += '\u00a0▾';   // нерозривний пробіл: стрілка не лишається сама в другому рядку
    if (st.q.me.textContent !== chip) st.q.me.textContent = chip;
    cls(st.q.me, chipCls);
    st.q.me.hidden = !chip || showCard || v.phase === 'done';
    st.q.me.disabled = !flip;
    st.q.me.title = flip ? 'Показати свою картку' : '';

    let card = '';
    if (showCard && me) {
      // Перевертання — усю роздачу: інакше чужий натиск посеред анімації перемалював би картку без неї.
      const first = v.phase === 'deal';
      if (me.spy) {
        card = '<button type="button" class="spy-cardin spy-spycard' + (first ? ' spy-flipin' : '') + '" data-sp="card" title="Сховати картку"'
          + (v.phase === 'deal' ? ' data-pad-first' : '') + '><div class="spy-cico" aria-hidden="true">🕵️</div>'
          + '<div class="spy-cbody"><b>Ти — ШПИГУН</b><span>Де всі — не знаєш. Слухай, підігравай і вгадай локацію з колоди.</span>'
          + '<span class="muted small">Вгадав — 4 очки. Спіймали — село святкує.</span></div></button>';
      } else {
        const l = locOf(v, me.loc);
        card = '<button type="button" class="spy-cardin' + (first ? ' spy-flipin' : '') + '" data-sp="card" title="Сховати картку"'
          + (v.phase === 'deal' ? ' data-pad-first' : '') + '><div class="spy-cico" aria-hidden="true">' + esc(l[2]) + '</div>'
          + '<div class="spy-cbody"><b>' + esc(l[1]) + '</b><span>Ти — ' + esc(me.role || '') + '</span>'
          + '<span class="muted small">Відповідай так, щоб свої зрозуміли, а шпигун — ні.</span></div></button>';
      }
    }
    st.q.card.hidden = !card;
    set(st.q.card, card);

    // ---- розкриття ----
    const rev = revealHtml(v, ctx, mySeat);
    st.q.reveal.hidden = !rev;
    set(st.q.reveal, rev);

    // ---- що робити зараз: порада, кнопки, дрібний рядок — кожне своїм слотом ----
    const a = actParts(v, ctx, st, mySeat, inGame);
    cls(st.q.say, 'spy-say' + (a.hi ? ' ' + a.hi : ''));
    set(st.q.say, a.say || '');
    set(st.q.btns, a.btns || '');
    set(st.q.note, a.note || '');
    st.q.act.hidden = !a.say && !a.btns && !a.note;
    if (v.phase === 'vote' && v.vote) {
      // Кнопки голосу будуються раз на голосування; «мій вибір» і «летить» — лише класами.
      const mine = v.vote.votes ? v.vote.votes[mySeat] : undefined;
      for (const b of st.q.btns.querySelectorAll('[data-sp="vote"]')) {
        const yes = b.dataset.yes === '1';
        b.classList.toggle('on', mine === yes);
        const busy = st.busy === 'vote:' + b.dataset.yes;
        b.disabled = busy;
        if (busy) b.setAttribute('aria-busy', 'true'); else b.removeAttribute('aria-busy');
      }
    }
    const cdNode = st.q.say.querySelector('.spy-cd');
    if (cdNode !== st.q.cd) { st.q.cd = cdNode; st.cdTxt = ''; }

    // ---- гравці ----
    const items = playerRows(v, ctx, st, mySeat, inGame);
    if (items.length) rows(st.q.players, items);
    else { st.q.players.dataset.sig = ''; st.q.players.innerHTML = '<div class="gwait">за столом поки нікого</div>'; }

    // ---- хроніка ----
    const hist = (v.history || []).map((h) => {
      const l = locOf(v, h.loc);
      return '<div>Раунд ' + h.round + ' · ' + esc(l[2]) + ' ' + esc(l[1]) + ' · шпигун — ' + esc(matchNick(v, h.spy)) + ' · '
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
    // Нова партія («Ще раз») — хроніка знову згорнута: посеред гри вона лише відсуває колоду.
    const matchKey = st.ctx.room ? st.ctx.room.id + ':' + st.ctx.room.round : '';
    if (v.phase !== 'done' && matchKey !== st.matchKey) { if (st.matchKey) st.q.hist.open = false; st.matchKey = matchKey; }

    // ---- колода (після партії вона ні до чого — ховаємо, і «Ще раз» каркаса видно без прокрутки) ----
    paintDeck(v, ctx, st, me);
    st.q.grid.classList.toggle('spy-solo', v.phase === 'done');

    padFocus(el, v, mySeat, inGame);
  }

  /// Пад: на новій фазі (і коли черга питати стала моєю) кільце стає на головну кнопку фази, а не на найближчу
  /// до того місця, де щойно зникла кнопка. Поле вводу балачки не чіпаємо — людина друкує.
  function padFocus(el, v, mySeat, inGame) {
    const st = el._sp;
    const mine = v.phase === 'play' && inGame && (v.asker === mySeat || v.askGrace);
    const key = v.round + ':' + v.phase + ':' + (mine ? 'a' : '');
    if (key === st.padKey) return;
    st.padKey = key;
    const pad = window.HPad;
    if (!pad || !pad.on || typeof pad.focus !== 'function') return;
    requestAnimationFrame(() => {
      const t = document.activeElement;
      if (t && t.matches && t.matches('input, textarea, [contenteditable="true"]')) return;
      const f = el.querySelector('[data-pad-first]');
      if (f && f.isConnected && !f.disabled && f.getClientRects().length) { try { pad.focus(f); } catch { /* старий pad.js */ } }
    });
  }

  function actParts(v, ctx, st, mySeat, inGame) {
    const esc = ctx.esc;
    const me = v.me;
    const chat = (first) => '<button type="button" class="ghost" data-sp="chat"' + (first ? ' data-pad-first' : '') + '>💬 До розмови</button>';
    const busy = (k) => (st.busy === k ? ' disabled aria-busy="true"' : '');
    const r = v.rules || {};
    const rounds = r.each ? 'кожен по разу шпигун' : r.rounds === 1 ? 'один раунд' : r.rounds + ' раунди';
    const rules = v.rules ? r.minutes + ' хв · ' + rounds
      + ' · локації: ' + (r.sets || []).map((s) => ({ all: 'усі', ua: 'наші', classic: 'класика' }[s] || s)).join(' + ') : '';
    const guessBtn = inGame && me && me.spy
      ? '<button type="button" class="spy-go' + (st.guessMode ? ' on' : '') + '" data-sp="guess-mode">🎯 ' + (st.guessMode ? 'Скасувати' : 'Назвати локацію') + '</button>' : '';
    switch (v.phase) {
      case 'lobby': {
        const n = (v.players || []).length;
        return {
          say: n >= 3 ? 'Можна починати: хазяїн столу тисне «Почати».' : 'Треба щонайменше троє — клич друзів у балачках.',
          note: esc(rules) + '.' + HOW,
        };
      }
      case 'deal':
        return {
          say: (me ? 'Запам\'ятай картку' : 'Роздають картки') + ' — раунд почнеться за <b class="spy-cd"></b> с.',
          note: 'Питання й відповіді — у балачці столу або вголос. Картка підкаже, чия черга.',
        };
      case 'play': {
        const asker = v.asker, by = v.askedBy;
        let say, hi = '';
        const myTurn = inGame && asker === mySeat;
        if (!inGame) say = 'Дивишся збоку: локацію тобі не скажуть. Читай розмову й вгадуй сам 😉';
        else if (myTurn) {
          hi = 'spy-hi';
          say = by != null
            ? 'Тебе питає ' + esc(nickOf(v, by)) + ' — відповідай у балачці, а тоді питай сам: обери кого.'
            : 'Ти питаєш першим — обери кого й пиши питання в балачці.';
        } else if (by === mySeat) say = 'Питання пішло — відповідає ' + esc(nickOf(v, asker)) + '. Чекай відповіді в балачці.';
        else if (v.askGrace) { hi = 'spy-hi'; say = 'Уже пів хвилини мовчить ' + esc(nickOf(v, asker)) + ' — перехоплюй слово: спитай будь-кого.'; }
        else say = (by != null ? 'Питає ' + esc(nickOf(v, by)) + ' — відповідає ' + esc(nickOf(v, asker)) + '. ' : 'Питає ' + esc(nickOf(v, asker)) + '. ') + 'Слухай і придивляйся.';
        // Головна кнопка для пада: «Спитати» в рядку, коли моя черга; інакше — «До розмови» (безпечна).
        const askFirst = inGame && (myTurn || v.askGrace);
        const tip = inGame ? '<button type="button" class="ghost spy-tipb" data-sp="tip" title="Підкинути питання, що не видає локацію">💡 Ідея питання</button>' : '';
        const notes = [];
        const now = performance.now();
        if (st.lastVote && now < st.lastVote.until) notes.push('↩ Не одностайно — підозрюваний (' + esc(nickOf(v, st.lastVote.suspect)) + ') грає далі.');
        if (st.tip >= 0) notes.push('💡 Наприклад: «' + esc(TIPS[st.tip]) + '»' + (st.tipIn ? ' — уже в полі балачки' : ''));
        return { say, hi, btns: guessBtn + chat(!askFirst) + tip, note: notes.join('<br>') };
      }
      case 'vote': {
        const vt = v.vote;
        if (!vt) return {};
        const n = Object.keys(vt.votes || {}).length;
        const tally = 'Проголосували ' + n + ' з ' + vt.need + ' · судимо лише одностайно, мовчання — «ні»';
        const head = 'Підозрює ' + esc(nickOf(v, vt.accuser)) + ': ' + esc(nickOf(v, vt.suspect)) + ' — шпигун?';
        if (!inGame) return { say: head, note: tally };
        if (mySeat === vt.suspect) return { say: 'Тебе підозрюють — переконуй у балачці!', hi: 'spy-hot2', btns: chat(true), note: tally };
        return {
          say: head,
          btns: '<div class="spy-vote">'
            + '<button type="button" class="spy-big spy-yes" data-sp="vote" data-yes="1" data-pad-first>Так, це шпигун</button>'
            + '<button type="button" class="spy-big spy-no" data-sp="vote" data-yes="0">Ні</button></div>',
          note: tally,
        };
      }
      case 'final': {
        const need = v.blame ? v.blame.need : 0;
        const n = v.blame ? Object.keys(v.blame.votes || {}).length : 0;
        return {
          say: inGame ? 'Час вийшов. Покажи на шпигуна — засуджує більшість (' + need + ').' : 'Час вийшов: усі показують на шпигуна.',
          hi: 'spy-hot2',
          btns: inGame ? guessBtn + chat(false) : '',
          note: 'Показали ' + n + ' з ' + present(v).length + ' · передумати можна до кінця · засудять шпигуна — хто показав саме на нього, бере ще +1',
        };
      }
      case 'reveal': {
        const last = v.round >= v.of;
        if (!inGame) return { note: last ? 'Підсумок партії — щойно всі натиснуть «Готово».' : 'Наступний раунд — щойно всі натиснуть «Готово».' };
        const mine = (v.players || []).find((p) => p.seat === mySeat);
        const wait = present(v).filter((p) => !p.ready).length;
        if (mine && mine.ready) return { say: '✔ Чекаємо решту: ' + wait };
        return {
          btns: '<button type="button" class="primary spy-big" data-sp="ready" data-pad-first' + busy('ready') + '>'
            + (last ? 'Готово — до підсумку' : 'Готово — наступний раунд') + '</button>',
        };
      }
      case 'done': {
        const res = v.result || {};
        const w = res.winners || [];
        const top = w.length ? (v.players || []).find((p) => p.seat === w[0]) : null;
        const who = joinNames(w.map((s) => esc(matchNick(v, s))));
        const win = w.length
          ? '🏆 ' + (w.length === 1 ? 'Партію бере ' + who : 'Перемогу ділять ' + who)
            + (top && !res.folded ? ' — ' + (w.length > 1 ? 'по ' : '') + pts(top.score) : '')
          : '🤝 Нічия';
        const few = present(v).length < 3;
        return {
          say: '<div class="spy-final">' + win + '</div>',
          note: (res.folded ? 'За столом лишилось двоє — партію згорнули. ' : '')
            + (few ? 'Щоб зіграти ще, треба третій — клич друзів.' : ''),
        };
      }
    }
    return {};
  }

  function playerRows(v, ctx, st, mySeat, inGame) {
    const esc = ctx.esc;
    const run = !!RUNNING[v.phase];
    const vt = v.phase === 'vote' ? v.vote : null;
    const bl = v.phase === 'final' && v.blame ? v.blame.votes || {} : null;
    const blamed = {};
    if (bl) for (const k in bl) blamed[bl[k]] = (blamed[bl[k]] || 0) + 1;
    const myBlame = bl && mySeat != null ? bl[mySeat] : undefined;
    const myAccused = inGame && (v.players || []).some((p) => p.seat === mySeat && p.accused);
    const myTurn = v.phase === 'play' && inGame && v.asker === mySeat;
    const grace = v.phase === 'play' && inGame && !myTurn && v.askGrace;
    // Ролі й «+очки» — лише на розкритті; після партії в рядку лишаються підсумок і кубок.
    const rv = v.phase === 'reveal' ? v.reveal : null;
    const res = v.phase === 'done' ? v.result : null;
    const winners = res ? res.winners || [] : [];
    const now = performance.now();
    const conf = st.confirm && now < st.confirm.until ? st.confirm.seat : null;
    const busy = (k) => (st.busy === k ? ' disabled aria-busy="true"' : '');
    let firstMarked = false;
    let list = v.players || [];
    // Після партії — за очками: одразу видно, хто взяв (рівні — за місцем).
    if (v.phase === 'done') list = list.slice().sort((a, b) => (b.score - a.score) || (a.seat - b.seat));

    return list.map((p, i) => {
      const other = p.seat !== mySeat;
      const tags = [];
      if (run && v.asker === p.seat && v.phase !== 'final') {
        // Покажчик — на тому, чия черга: спершу він відповідає, тоді питає. Бейдж каже те саме, що й порада.
        const t = v.phase === 'deal' ? 'питає першим' : v.askedBy != null ? 'відповідає → питає' : 'питає';
        tags.push('<span class="spy-b spy-b-ask">' + t + '</span>');
      }
      if (run && v.askedBy === p.seat && v.phase === 'play') tags.push('<span class="spy-b spy-b-by" title="Це питання зараз відповідають">❓ питає</span>');
      if (run && p.accused && v.phase !== 'vote') tags.push('<span class="spy-b spy-b-acc" title="Уже висували підозру цього раунду">підозра ✓</span>');
      if (vt) {
        if (p.seat === vt.suspect) tags.push('<span class="spy-b spy-b-sus">під підозрою</span>');
        else if (p.here) {
          const x = vt.votes ? vt.votes[p.seat] : undefined;
          tags.push(x === true ? '<span class="spy-b spy-b-yes">✓ так</span>' : x === false ? '<span class="spy-b spy-b-no">✗ ні</span>' : '<span class="spy-b spy-b-wait">…</span>');
        }
      }
      if (bl && blamed[p.seat]) tags.push('<span class="spy-b spy-b-point" title="Скільки показали на цього гравця">👉 ' + blamed[p.seat] + '</span>');
      if (rv) {
        if (rv.spy === p.seat) tags.push('<span class="spy-b spy-b-spy">🕵️ шпигун</span>');
        else if (rv.roles && rv.roles[p.seat]) tags.push('<span class="spy-b spy-b-role">' + esc(rv.roles[p.seat]) + '</span>');
        const g = rv.gained ? rv.gained[p.seat] : undefined;
        if (g > 0) tags.push('<span class="spy-b spy-b-plus" style="--n:' + i + '">+' + g + '</span>');
        const to = rv.pointed ? rv.pointed[p.seat] : undefined;
        if (to != null) tags.push('<span class="spy-b spy-b-to' + (to === rv.spy ? ' spy-b-hit' : '') + '" title="На кого показав у фіналі">👉 ' + esc(nickOf(v, to)) + '</span>');
      }
      const marks = [];
      if (v.phase === 'reveal' && p.ready && p.here) marks.push('<span class="spy-b spy-b-ready">✔ готово</span>');
      if (winners.includes(p.seat)) marks.push('<span class="spy-b spy-b-win">🏆 переможець</span>');
      if (!p.here && v.phase !== 'lobby') marks.push('<span class="spy-b spy-b-gone">встав</span>');

      const btns = [];
      if (inGame && p.here && other) {
        // Своя черга — питаю будь-кого, крім того, хто щойно питав мене. Перехоплене слово (мовчун 30 с) — будь-кого,
        // крім самого мовчуна: питати його — не перехопити слово, а смикнути.
        const canAsk = (myTurn && p.seat !== v.askedBy) || (grace && p.seat !== v.asker);
        if (canAsk) {
          const first = !firstMarked;
          firstMarked = true;
          btns.push('<button type="button" class="spy-do spy-ask" data-sp="ask" data-seat="' + p.seat + '"' + (first ? ' data-pad-first' : '')
            + busy('ask:' + p.seat) + '>Спитати</button>');
        }
        if (v.phase === 'play') {
          if (myAccused) btns.push('<button type="button" class="spy-do ghost" disabled title="Ти вже висував підозру цього раунду">Підозра</button>');
          else btns.push('<button type="button" class="spy-do ghost spy-acc' + (conf === p.seat ? ' spy-sure' : '') + '" data-sp="accuse" data-seat="' + p.seat + '"'
            + busy('accuse:' + p.seat) + '>' + (conf === p.seat ? 'Точно? Так' : 'Підозра') + '</button>');
        }
        if (v.phase === 'final') {
          const first = !firstMarked && myBlame == null;
          if (first) firstMarked = true;
          btns.push('<button type="button" class="spy-do spy-blame' + (myBlame === p.seat ? ' on' : '') + '" data-sp="blame" data-seat="' + p.seat + '"'
            + (first || myBlame === p.seat ? ' data-pad-first' : '') + busy('blame:' + p.seat) + '>' + (myBlame === p.seat ? '👉 Шпигун!' : 'Це шпигун') + '</button>');
        }
      }
      const c = 'spy-p' + (p.seat === mySeat ? ' me' : '') + (!p.here ? ' gone' : '')
        + (vt && vt.suspect === p.seat ? ' sus' : '') + (run && v.asker === p.seat && v.phase === 'play' ? ' asking' : '')
        + (rv && rv.spy === p.seat ? ' wasspy' : '');
      return {
        seat: p.seat,
        cls: c,
        n: String(p.seat + 1),
        nick: esc(p.nick || ('гравець ' + (p.seat + 1))) + (p.seat === mySeat ? ' <i>(ти)</i>' : ''),
        tags: tags.join(''),
        marks: marks.join(''),
        score: v.phase !== 'lobby' ? String(p.score) : '',
        dos: btns.join(''),
      };
    });
  }

  function revealHtml(v, ctx, mySeat) {
    const rv = v.reveal;
    const res = v.result || {};
    // На розкритті — банер раунду. Після партії його вже бачили; повторюємо лише коли партію згорнули посеред
    // раунду (розкриття тоді не було) — таємницю берегти вже нема від кого.
    if (!rv || !(v.phase === 'reveal' || (v.phase === 'done' && res.folded))) return '';
    const esc = ctx.esc;
    const l = locOf(v, rv.loc);
    const spy = esc(matchNick(v, rv.spy));
    const place = esc(l[2]) + ' ' + esc(l[1]);
    let text;
    switch (rv.how) {
      case 'caught': text = 'Шпигуна спіймали!'; break;
      case 'wrong': text = 'Засудили невинного — ' + esc(matchNick(v, rv.suspect)) + ' не шпигун. Шпигунові +4.'; break;
      case 'guessed': text = 'Шпигун вгадує локацію. Шпигунові +4.'; break;
      case 'misguess': { const g = locOf(v, rv.guess); text = 'Шпигун ставить на «' + esc(g[1]) + '» — і мимо. Селу по очку.'; break; }
      case 'timeout': text = 'Час вийшов, а шпигуна не засудили — шпигунові +2.'; break;
      case 'left': text = 'Шпигун утік з-за столу — селу по очку.'; break;
      case 'fold': text = 'Раунд не дограли: за столом лишилось двоє.'; break;
      default: text = '';
    }
    // Колір — від мене: шпигунові його «вгадав» — перемога, селу — поразка; глядачеві — без кольору.
    const wasSpy = mySeat != null && rv.spy === mySeat;
    const wasHere = mySeat != null && rv.gained && rv.gained[mySeat] !== undefined;
    let tone = '';
    if (wasSpy) tone = SPY_WINS[rv.how] ? ' spy-good' : VILLAGE_WINS[rv.how] ? ' spy-bad' : '';
    else if (wasHere) tone = VILLAGE_WINS[rv.how] ? ' spy-good' : SPY_WINS[rv.how] ? ' spy-bad' : '';
    // Велика картка шпигуна перевертається в усіх одночасно: той самий момент «ааа, так і знав!».
    return '<div class="spy-banner' + tone + '">'
      + '<div class="spy-rcard spy-flipin"><span class="spy-rico" aria-hidden="true">🕵️</span><span class="muted small">шпигун</span><b>' + spy + '</b></div>'
      + '<div class="spy-rbody"><span class="spy-rhow"><span aria-hidden="true">' + (HOW_ICON[rv.how] || '🕵️') + '</span> ' + text + '</span>'
      + '<span class="spy-rloc">Ви були: <b>' + place + '</b></span></div></div>';
  }

  function paintDeck(v, ctx, st, me) {
    const esc = ctx.esc;
    const deck = v.deck || [];
    const q = st.q;
    q.deck.hidden = !deck.length || v.phase === 'done';
    const count = deck.length ? '· ' + deck.length : '';
    if (q.dcount.textContent !== count) q.dcount.textContent = count;
    const guessing = guessingNow(st, v);
    const rv = v.phase === 'reveal' ? v.reveal : null;
    let hint;
    if (guessing) hint = 'Обери локацію й підтвердь — це кінець раунду, спроба одна.';
    else if (st.guessMode && me && me.spy && v.phase === 'vote') hint = 'Вгадування чекає: іде голосування. Після нього продовжиш.';
    else if (me && me.spy && RUNNING[v.phase]) hint = 'Тап — закреслити те, що точно не підходить. Готовий — «🎯 Назвати локацію».';
    else if (rv) hint = 'Зелена рамка — де всі були насправді.';
    else hint = 'Довідник для всіх: питай так, щоб відсікти зайве. Тап — закреслити для себе.';
    if (q.dhint.textContent !== hint) q.dhint.textContent = hint;
    q.deck.classList.toggle('spy-guessing', !!guessing);

    const html = deck.map((d) => {
      const id = d[0];
      let c = 'spy-loc';
      if (st.strikes.has(id) && !rv) c += ' struck';
      if (guessing && st.pick === id) c += ' pick';
      if (rv && rv.loc === id) c += ' true';
      if (rv && rv.how === 'misguess' && rv.guess === id) c += ' false';
      if (!rv && me && me.loc === id && RUNNING[v.phase]) c += ' here';
      return '<button type="button" class="' + c + '" data-sp="loc" data-loc="' + esc(id) + '"'
        + (guessing && st.pick === id ? ' aria-pressed="true"' : '') + '>'
        + '<span class="spy-li" aria-hidden="true">' + esc(d[2]) + '</span><span class="spy-lt">' + esc(d[1]) + '</span></button>';
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
    if (hot !== st.hot) { st.hot = hot; st.q.clockBox.classList.toggle('spy-hot', hot); }
    if (last !== st.last) { st.last = last; st.q.clockBox.classList.toggle('spy-last', last); }
    // Відлік роздачі словами: «раунд почнеться за 5 с».
    if (st.q.cd) {
      const cd = String(Math.max(0, Math.ceil((st.phaseEnd - now) / 1000)));
      if (cd !== st.cdTxt) { st.cdTxt = cd; st.q.cd.textContent = cd; }
    }
    const paused = !st.clockRun && !!RUNNING[v.phase];
    if (paused !== st.paused) { st.paused = paused; st.q.clockBox.classList.toggle('spy-paused', paused); }
    // Двокрокова «Підозра» гасне сама; розгорнута на старті картка згортається в чіп; рядок про невдалу підозру зникає.
    let again = false;
    if (st.confirm && now > st.confirm.until) { st.confirm = null; again = true; }
    if (st.cardOpen == null && v.phase === 'play' && !st.q.card.hidden && now > st.cardUntil) again = true;
    if (st.lastVote && now > st.lastVote.until) { st.lastVote = null; again = true; }
    if (again) paint(el);
  }

  // ---------------------------------------------------------------------------------------

  HGames.register({
    id: 'spy',
    added: '2026-09-27',
    icon: ICON,
    // Розмова тут і є гра: балачку столу каркас розгортає сам (ПК), на телефоні — кнопка «💬 До розмови».
    talk: 'main',
    seatNames: (i) => String(i + 1),
    seatClass: ['spy-s0', 'spy-s1', 'spy-s2', 'spy-s3', 'spy-s4', 'spy-s5', 'spy-s6', 'spy-s7', 'spy-s8', 'spy-s9'],
    // Нова гра: «🆕» у лобі два тижні тим, хто ще не грав (core.js, isNewGame). Без цього поля плитка губилась серед
    // шести десятків ігор — за перший день на проді ні однієї партії.
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Шпигун',
      items: [
        '🕵️ Усі знають, де вони, — крім одного. Шпигун мусить вгадати локацію, село — вгадати шпигуна',
        '💬 Питайте одне одного в балачці столу; картка підказує, чия черга питати, а 💡 підкине питання',
        '👉 Раз за раунд можна висунути підозру: якщо всі згодні — картки на стіл',
        '🎯 Шпигун будь-коли може зупинити гру й назвати локацію: вгадав — 4 очки',
        '🏆 Кілька раундів (або «кожен по разу»), у кожному новий шпигун; хто набрав більше очок — той і взяв',
      ],
    },
    // pad не оголошуємо: гра кнопкова, кільце фокуса шару пада ходить по кнопках саме (PROTOCOL §3), а на кожній
    // фазі головна кнопка позначена data-pad-first — туди кільце й стає (padFocus).

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
      clearTimeout(st.busyT);
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
      // Після партії каркас пише «Перемога: …» з місць за столом — а хто вже встав, у нього стає номером
      // («Перемога: 1, Оля»), а новачок на звільненому місці — чужим переможцем. Імена партії вид тримає сам.
      if (phase === 'done' && ctx.room && ctx.room.status === 'finished' && v.result) {
        const w = v.result.winners || [];
        return w.length ? 'Перемога: ' + w.map((s) => matchNick(v, s)).join(', ') : 'Нічия';
      }
      if (!phase || phase === 'lobby' || phase === 'done') return '';
      const r = (v.of || src.of) > 1 ? 'Раунд ' + (src.round || v.round) + ' із ' + (src.of || v.of) : 'Один раунд';
      switch (phase) {
        case 'deal': return 'Роздано — дивись картку';
        case 'play': {
          const a = v.asker != null ? v.asker : src.asker;
          if (a == null) return r;
          return r + ' · ' + (a === ctx.seat && v.me ? 'твоя черга питати' : 'черга — ' + nickOf(v, a));
        }
        case 'vote': return v.vote ? 'Голосування: ' + nickOf(v, v.vote.suspect) + ' — шпигун?' : 'Голосування';
        case 'final': return 'Час вийшов — хто шпигун?';
        case 'reveal': return 'Розкриття';
      }
      return '';
    },
  });
})();
