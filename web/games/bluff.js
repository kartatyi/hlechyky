/*
  «Байкарі» — як Fibbage. Питання з пропуском і дивною правдою; кожен вписує правдоподібну брехню, потім усі шукають
  правду серед карток. Правила, час, очки й перевірка «ти випадково написав правду» живуть на сервері
  (Impl/Bluff.cs, BluffText.cs); модуль лише малює вид і шле наміри. Гра покрокова з таймерами, тож малюємо DOM без
  свого циклу кадрів (дуга відліку — каркасова): update() чіпає лише те, що справді змінилось, а картки будуються раз
  на питання й далі лише перефарбовуються — інакше кожен вид губив би фокус кільця пада й :hover.

  Вид (подія 'room', свій кожному місцю — гра Hidden):
    { phase: 'read'|'write'|'pick'|'reveal'|'score'|'done', q, of, final, endsAt, phaseMs, cat, catLabel,
      text,                          // питання з ___ ('' у лобі й у done)
      nicks[8], present[8], wrote[8], picked[8],
      my: null | { lie, auto, pick, likes: number[] },
      options: null | [{ i, text, mine, by: number[]|null, picks: number[]|null, truth: bool|null, decoy: bool|null, likes }],
      revealed: number[], note, quip, scores[8], delta[8], truthDelta[8], likeDelta[8], victims[8],
      result: null | { winners, left, scores, best: null | { q, text, by, victims, likes, hlek },
                       recap: [{ q, text, answer, note, best: null | { text, by, victims, hlek } }] } }
  Кадрів гра не шле (тик — лише види), тож frame-хука тут нема.

  Наміри: act('lie', { text }), act('lie', { auto: true }) — «🎲 Хай Глек збреше», act('pick', { i }), act('like', { i }).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="6.5" cy="8" r="5" fill="none" stroke="var(--accent)" stroke-width="1.8"/>'
    + '<circle cx="5" cy="7" r="1" fill="var(--accent)"/>'
    + '<path d="M9.5 8h5.5" stroke="var(--clay)" stroke-width="2" stroke-linecap="round"/></svg>';

  /// Усі вісім кольорів — свої: каркасове «d» (місце 4) сіре й читалось би як «вийшов».
  const SEAT_CLASS = ['bluff-s0', 'bluff-s1', 'bluff-s2', 'bluff-s3', 'bluff-s4', 'bluff-s5', 'bluff-s6', 'bluff-s7'];
  /// Числа — ті самі, що Bluff.MaxLie / TruthPts / FooledPts / LikePts на сервері (для правил).
  const MAX_LIE = 40;
  const TRUTH = 1000;
  const FOOLED = 500;
  const LIKE = 100;
  /// Скільки чекаємо, поки вид підтвердить мій вибір картки, перш ніж повірити виду, а не натиску.
  const OPTIMISTIC_MS = 1500;
  /// Останні секунди фази, коли тим, хто ще не написав чи не обрав, тихо цокає.
  const TICK_LAST_MS = 5000;
  /// Написане, але не надіслане (не встиг «Готово», на телефоні закрив клавіатуру її ж кнопкою) — за стільки до кінця
  /// часу піде саме: інакше брехня згоріла б разом із фазою, а людина певна, що її картка на столі.
  const RESCUE_MS = 2000;
  const HEART = '\u2764\ufe0f';

  const RULES = [
    '🤥 Питання з пропуском і дивна, але справжня відповідь. Кожен вписує свою правдоподібну брехню',
    '🔍 Потім усі бачать картки впереміш і шукають правду — свою брехню обрати не можна',
    '💰 Вгадав правду — +' + TRUTH + ', кожен, кого надурила твоя брехня, — +' + FOOLED + ' тобі, ' + HEART + ' — +' + LIKE,
    '⏱ Останнє питання — подвійне. Нема ідей — «🎲 Хай Глек збреше»',
  ];

  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : v; } catch { return d; } },
    set(k, v) { try { localStorage.setItem(k, v); } catch { /* приватне вікно — не біда */ } },
  };
  const muted = () => store.get('bluffMute', '0') === '1';

  // =============================================================================================
  // Звук: WebAudio-синтез, тихо, лише після першого жесту людини
  // =============================================================================================

  let actx = null;
  let gestured = false;

  function audio() {
    if (!gestured || muted()) return null;
    if (!actx) {
      try { actx = new (window.AudioContext || window.webkitAudioContext)(); } catch { actx = null; }
    }
    if (actx && actx.state === 'suspended') actx.resume().catch(() => {});
    return actx;
  }

  function tone(ac, type, f1, f2, ms, delay, vol) {
    const t0 = ac.currentTime + (delay || 0);
    const o = ac.createOscillator();
    const g = ac.createGain();
    o.type = type;
    o.frequency.setValueAtTime(f1, t0);
    if (f2 !== f1) o.frequency.exponentialRampToValueAtTime(f2, t0 + ms / 1000);
    g.gain.setValueAtTime(vol || 0.08, t0);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + ms / 1000);
    o.connect(g).connect(ac.destination);
    o.start(t0);
    o.stop(t0 + ms / 1000 + 0.02);
  }

  function sound(name) {
    const ac = audio();
    if (!ac) return;
    try {
      if (name === 'open') tone(ac, 'square', 220, 220, 40);
      else if (name === 'truth') { tone(ac, 'sine', 440, 440, 90); tone(ac, 'sine', 660, 660, 90, 0.1); }
      else if (name === 'boing') tone(ac, 'sine', 160, 110, 180, 0.12);
      else if (name === 'tick') tone(ac, 'triangle', 880, 880, 25, 0, 0.035);
      else if (name === 'final') { tone(ac, 'triangle', 392, 392, 110); tone(ac, 'triangle', 523, 523, 110, 0.12); tone(ac, 'triangle', 784, 784, 200, 0.24); }
    } catch { /* звук — прикраса, не причина падати */ }
  }

  // =============================================================================================
  // дрібниці
  // =============================================================================================

  const num = (n) => Number(n || 0).toLocaleString('uk-UA');

  function plural(n, one, few, many) {
    const d = n % 10, h = n % 100;
    return d === 1 && h !== 11 ? one : d >= 2 && d <= 4 && (h < 12 || h > 14) ? few : many;
  }

  const victims = (n) => n + ' ' + plural(n, 'жертва', 'жертви', 'жертв');

  /// Нік місця: знімок зі старту (той, хто вийшов, лишається з іменем), інакше — те, що знає каркас.
  function nick(ctx, v, i) {
    return (v.nicks && v.nicks[i]) || ctx.nickOf(i) || ctx.seatName(i);
  }

  /// «Оля», «Оля і Петро», «Оля, Петро і Ганна».
  function names(ctx, v, seats) {
    const list = (seats || []).map((i) => nick(ctx, v, i));
    return list.length <= 1 ? list.join('') : list.slice(0, -1).join(', ') + ' і ' + list[list.length - 1];
  }

  /// Картку показуємо без лапок і без кінцевої крапки чи знаку оклику: «Гасі!» чи «„Аполлон“» поруч із «ДЕСНА»
  /// видавали б людську руку чи банк — правду шукали б за оформленням, а не за змістом.
  const shown = (t) => String(t || '').replace(/[«»“”„"]/gu, '').replace(/[\s.!?…]+$/u, '').trim() || String(t || '');

  function state(root) {
    if (!root._bf) root._bf = { opened: new Set(), primed: false, writeQ: -1, auto: null, opt: null, padKey: '', finalQ: -1, tickAt: 0 };
    return root._bf;
  }

  const me = (ctx, v) => (ctx.mine && v.present && v.present[ctx.seat] ? ctx.seat : -1);
  const modalOpen = () => !!document.querySelector('.modal:not([hidden]), .padhelp, .padkbd');

  /// Фаза активного столу — для рядка підказок пада (hint читається щоразу, коли смужка перемальовується).
  let padPhase = '';

  // =============================================================================================
  // малювання
  // =============================================================================================

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    st.ctx = ctx;
    const phase = v.phase || 'read';
    const lobby = !ctx.playing && !v.result;
    const done = !!v.result && (phase === 'done' || !ctx.playing);
    const seat = me(ctx, v);
    padPhase = lobby || done ? '' : phase;

    // ---- шапка: номер, тема, ×2, дуга ----
    const no = root.querySelector('.bluff-no');
    const played = done && v.result ? (v.result.recap || []).length : 0;
    const noText = lobby ? ''
      : done && v.result.left ? 'Партію обірвано · зіграно ' + played + ' з ' + v.of
      : done ? 'Партію зіграно · ' + v.of + ' ' + plural(v.of, 'питання', 'питання', 'питань')
      : v.of ? 'Питання ' + v.q + ' з ' + v.of + (v.catLabel ? ' · ' + v.catLabel : '') : '';
    if (no.textContent !== noText) no.textContent = noText;
    root.querySelector('.bluff-x2').hidden = !(ctx.playing && v.final && !done);
    const arcHost = root.querySelector('.bluff-arc');
    if (ctx.playing && !done && v.endsAt) HGames.ui.timerArc(arcHost, v.endsAt, v.phaseMs || 1000);
    else {
      const arc = arcHost.querySelector('.garc');
      if (arc) { if (arc._arc) arc._arc.stop(); arc.remove(); }
    }
    setText(root.querySelector('.bluff-snd'), muted() ? '🔇' : '🔈');

    // ---- фінал — подія: банер і фанфара на початку останнього питання ----
    const finalNow = ctx.playing && !done && !!v.final && phase === 'read';
    const banner = root.querySelector('.bluff-final');
    banner.hidden = !finalNow;
    if (finalNow && st.finalQ !== v.q) { st.finalQ = v.q; sound('final'); }

    // ---- питання з пропуском ----
    const qBox = root.querySelector('.bluff-q');
    const cards = v.options || [];
    const open = v.revealed || [];
    const last = open.length ? open[open.length - 1] : -1;
    const truthOpen = cards.some((o) => o.truth === true);
    const truth = truthOpen ? cards.find((o) => o.truth === true) : null;
    const fill = phase === 'reveal' || phase === 'score' ? (truth ? truth.text : last >= 0 && cards[last] ? cards[last].text : '') : '';
    const qSig = (done || lobby ? '' : v.text || '') + '\u0001' + fill + '\u0001' + (truth ? 1 : 0);
    if (qBox.dataset.sig !== qSig) {
      const was = qBox.dataset.sig || '';
      qBox.dataset.sig = qSig;
      const text = done || lobby ? '' : v.text || '';
      const blank = '<span class="bluff-blank' + (truth ? ' truth' : fill ? ' lie' : '') + '">' + (fill ? ctx.esc(shown(fill)) : '&nbsp;') + '</span>';
      qBox.innerHTML = ctx.esc(text).split('___').join(blank);
      const b = qBox.querySelector('.bluff-blank');
      if (b && fill && was.split('\u0001')[0] === text) b.classList.add('swap');
    }
    qBox.hidden = lobby || done;
    qBox.classList.toggle('dim', phase === 'read');

    // ---- підказка фази ----
    const stage = root.querySelector('.bluff-stage');
    const my = v.my || {};
    const stageText = lobby || done ? ''
      : phase === 'read' ? (v.final ? '' : 'Читай уважно — за мить вигадуватимеш свою брехню')
      : phase === 'write' ? (waiting(ctx, v, v.wrote, seat) || (seat < 0 ? 'Байкарі брешуть…' : my.lie ? '' : 'Вигадай брехню, у яку повірять друзі. Правду писати не можна 🙂'))
      : phase === 'pick' ? (waiting(ctx, v, v.picked, seat) || (seat < 0 ? 'Гравці шукають правду…' : my.pick != null ? 'Обрано. Можна передумати, поки йде час' : 'Одна з карток — правда. Яка? Свою обрати не можна'))
      // Розкриття й рахунок пояснюють рядок «Ти…» під картками і статус каркаса — двічі те саме не пишемо.
      : '';
    if (stage.textContent !== stageText) stage.textContent = stageText;
    stage.hidden = !stageText;

    paintWrite(root, ctx, v, st, seat, phase);
    paintWho(root, ctx, v, phase, lobby, done);
    const fresh = paintCards(root, ctx, v, st, seat, phase, done);
    paintMe(root, ctx, v, seat, phase, done);

    // ---- «а насправді…» ----
    const note = root.querySelector('.bluff-note');
    const showNote = !done && (phase === 'reveal' || phase === 'score') && !!(v.note || v.quip);
    note.hidden = !showNote;
    if (showNote) {
      const nt = note.querySelector('.bluff-ntext');
      const ntText = v.note ? 'А насправді: ' + v.note : '';
      if (nt.textContent !== ntText) nt.textContent = ntText;
      nt.hidden = !ntText;
      const qt = note.querySelector('.bluff-quip');
      const qtText = v.quip ? '— ' + v.quip : '';
      if (qt.textContent !== qtText) qt.textContent = qtText;
    }

    paintEnd(root, ctx, v, done);
    paintScore(root, ctx, v, phase, lobby, done);
    root.querySelector('.bluff-rules').hidden = !lobby;
    const box = root.querySelector('.bluff');
    box.classList.toggle('bluff-lobby', lobby);
    box.classList.toggle('bluff-picking', !lobby && !done && phase === 'pick');
    padFocus(root, ctx, v, st, seat, phase, done, lobby);

    // Нове питання й початок вибору — питання знову на екран. На телефоні після розкриття сторінка стоїть на
    // рахунку, а наступне питання з'являлось під шапкою сайту: його доводилось шукати пальцем щоразу.
    // Першу відмальовку (відкрив стіл, F5) не чіпаємо — сторінка й так угорі.
    const step = ctx.playing && !done && !lobby ? v.q + ':' + phase : '';
    if (step !== st.step) {
      const was = st.step;
      st.step = step;
      if (was != null && (phase === 'read' || phase === 'pick' || phase === 'score')) requestAnimationFrame(() => showStep(root, phase));
    }

    // Щойно відкрита картка (чи правда з поясненням) — у видиму частину екрана: на телефоні й на Deck із вісьмома
    // картками вони лежать під краєм. 'nearest' не смикає сторінку, коли все й так видно.
    // Прокрутка — у наступному кадрі: там браузер і так рахує розкладку, а update() лишається дешевим.
    if (fresh.length) {
      const i = fresh[fresh.length - 1];
      const target = cards[i] && cards[i].truth && !note.hidden ? note : root.querySelector('.bluff-opts').children[i];
      if (target) requestAnimationFrame(() => reveal(target));
    }
  }

  /// Показати елемент, лише коли він справді під краєм (з урахуванням смуги плеєра внизу — scroll-margin у CSS).
  function reveal(el) {
    if (!el.isConnected) return;
    const r = el.getBoundingClientRect();
    if (r.top >= 60 && r.bottom <= innerHeight - 110) return;
    try { el.scrollIntoView({ block: 'nearest', behavior: reduced() ? 'auto' : 'smooth' }); } catch { /* старий браузер */ }
  }

  /// Видима смуга екрана [верх, низ] у координатах в'юпорта: під липкою шапкою сайту й над нижніми панелями
  /// (вкладки й міні-плеєр на телефоні), а з екранною клавіатурою — лише те, що над нею (visualViewport).
  /// Кличеться на зміну фази й на клавіатуру, не на кожен вид.
  function band() {
    const vv = window.visualViewport;
    let top = vv ? vv.offsetTop : 0;
    let low = vv ? vv.offsetTop + vv.height : window.innerHeight;
    const head = document.querySelector('body > header');
    if (head) top = Math.max(top, head.getBoundingClientRect().bottom);
    for (const el of document.querySelectorAll('body > nav.mtabs, #mini')) {
      if (!el.getClientRects().length || getComputedStyle(el).position !== 'fixed') continue;
      const r = el.getBoundingClientRect();
      if (r.height && r.top > (top + low) / 2) low = Math.min(low, r.top);
    }
    return { top, low };
  }

  /// Питання (у виборі — ще й перший рядок карток) має бути видно; коли ні — прокрутити так, щоб шапка гри стала
  /// під шапку сайту. Коли все й так на екрані — нічого не чіпаємо.
  function showStep(root, phase) {
    const head = root.querySelector('.bluff-top');
    if (!head || !head.isConnected || modalOpen()) return;
    if (phase === 'score') {
      // Рахунок між питаннями: на вузькому екрані таблиця під картками, і за 6 секунд її ніхто не бачив.
      // Праворуч колонкою (ПК, Дека) вона й так на екрані — тоді нічого не робимо.
      const sc = root.querySelector('.bluff-score');
      if (!sc || sc.hidden) return;
      const r = sc.getBoundingClientRect(), b = band();
      if (r.bottom > b.low + 8 && r.top > b.top + 40) {
        try { sc.scrollIntoView({ block: 'nearest', behavior: reduced() ? 'auto' : 'smooth' }); } catch { /* старий браузер */ }
      }
      return;
    }
    const tail = phase === 'pick' ? root.querySelector('.bluff-opts:not([hidden]) .bluff-cell') : root.querySelector('.bluff-q');
    const b = band();
    const t = head.getBoundingClientRect().top;
    const end = (tail || head).getBoundingClientRect().bottom;
    if (t >= b.top - 2 && end <= b.low + 2) return;
    try { window.scrollBy({ top: t - b.top - 6, behavior: reduced() ? 'auto' : 'smooth' }); } catch { /* старий браузер */ }
  }

  /// Екранна клавіатура телефона: поле з «Готово» — над нею, а питання — над полем, скільки влізе. Браузер сам
  /// показує лише поле (і то не завжди), а «Готово» й питання лишались під клавіатурою й шапкою.
  function fitWrite(root) {
    const input = root.querySelector('.bluff-in');
    if (!input || document.activeElement !== input || !HGames.ui.coarse()) return;
    const row = root.querySelector('.bluff-row').getBoundingClientRect();
    const q = root.querySelector('.bluff-q').getBoundingClientRect();
    const b = band();
    let d = 0;
    if (row.bottom > b.low - 8) d = row.bottom - (b.low - 8);
    else if (row.top < b.top + 4) d = row.top - (b.top + 4);
    const over = (b.top + 6) - (q.top - d);                // на скільки питання вилазить угору
    const room = (b.low - 8) - (row.bottom - d);           // на скільки ще можна опустити поле
    if (over > 0 && room > 0) d -= Math.min(over, room);
    if (Math.abs(d) > 3) window.scrollBy(0, d);
  }

  /// «Чекаємо: Петро» — коли решта вже написала (обрала), а стіл тримають один-три.
  function waiting(ctx, v, flags, seat) {
    if (!v.nicks || !v.present || !flags) return '';
    if (seat >= 0 && !flags[seat]) return '';
    const left = [];
    let present = 0;
    for (let i = 0; i < 8; i++) {
      if (!v.nicks[i] || !v.present[i]) continue;
      present++;
      if (!flags[i]) left.push(nick(ctx, v, i));
    }
    return left.length && left.length <= 3 && left.length < present ? '⏳ Чекаємо: ' + left.join(', ') : '';
  }

  /// Джойстик: кільце не має виходити з гри на «Встати» (один зайвий Ⓐ — і ти вже не за столом). Поки партія йде,
  /// колонка гри — межа кільця (data-pad-scope), а на кожну нову фазу модуль ставить кільце туди, де зараз справа.
  /// Коли висить вікно (новини, довідка пада, клавіатура), не ставимо — і не запам'ятовуємо фазу: повторимо, щойно
  /// вікно закриють (а після закриття каркас сам шукає data-pad-first у межі).
  function padFocus(root, ctx, v, st, seat, phase, done, lobby) {
    const main = root.querySelector('.bluff-main');
    const playing = seat >= 0 && ctx.playing && !done && !lobby;
    main.toggleAttribute('data-pad-scope', playing);
    const cards = [...root.querySelectorAll('.bluff-opts .bluff-opt')];
    const firstCard = cards.find((b) => !b.disabled && !b.classList.contains('mine')) || cards[0] || null;
    const want = !playing ? null
      : phase === 'write' ? root.querySelector('.bluff-in')
      : phase === 'pick' || phase === 'reveal' || phase === 'score' ? firstCard
      : root.querySelector('.bluff-q');
    for (const el of root.querySelectorAll('[data-pad-first]')) if (el !== want) el.removeAttribute('data-pad-first');
    if (want) want.setAttribute('data-pad-first', '');

    const key = v.q + ':' + phase;
    if (st.padKey === key) return;
    const pad = window.HPad;
    if (!pad || !pad.on || typeof pad.focus !== 'function' || !playing) { st.padKey = key; return; }
    if (modalOpen()) return;                     // спершу вікно — фазу не записуємо, повторимо на наступному виді
    // У розкритті й рахунку картки лишаються на місці, і кільце з них нікуди не зникає.
    const el = phase === 'reveal' || phase === 'score' ? null : want;
    if (el && (el.hidden || !el.isConnected)) return;
    st.padKey = key;
    if (el) { try { pad.focus(el); } catch { /* пад — прикраса, не причина падати */ } }
  }

  function paintWrite(root, ctx, v, st, seat, phase) {
    const box = root.querySelector('.bluff-write');
    const input = root.querySelector('.bluff-in');
    const my = v.my || {};
    const can = ctx.playing && phase === 'write' && seat >= 0;
    const opened = can && box.hidden;
    box.hidden = !can;
    input.disabled = !can;
    root.querySelector('.bluff-go').disabled = !can;
    root.querySelector('.bluff-dice').disabled = !can;
    if (!can) return;
    // Нове питання — чисте поле; після F5 — те, що вже записано на сервері.
    if (st.writeQ !== v.q) {
      st.writeQ = v.q;
      st.auto = my.auto ? my.lie : null;
      input.value = my.lie || '';
      count(root);
    }
    // Глек підказав — брехня з'являється в полі, її можна переписати.
    if (my.auto && my.lie && st.auto !== my.lie) {
      st.auto = my.lie;
      input.value = my.lie;
      count(root);
    }
    if (opened && !HGames.ui.coarse() && !modalOpen()) {
      const busy = document.activeElement;
      if (!busy || busy === document.body || !/^(INPUT|TEXTAREA|SELECT)$/.test(busy.tagName)) input.focus({ preventScroll: true });
    }
    const line = root.querySelector('.bluff-my');
    const text = my.lie ? 'Твоя брехня: «' + my.lie + '»' + (my.auto ? ' від Глека' : '') + ' · можна переписати, поки є час' : '';
    if (line.textContent !== text) line.textContent = text;
  }

  function count(root) {
    const input = root.querySelector('.bluff-in');
    const cnt = root.querySelector('.bluff-cnt');
    const n = (input.value || '').length;
    const text = n + '/' + MAX_LIE;
    if (cnt.textContent !== text) cnt.textContent = text;
    cnt.classList.toggle('full', n >= MAX_LIE);
  }

  /// Хто вже написав (write) чи обрав (pick): чипи ніків із ✓ — галочки, а не тексти.
  function paintWho(root, ctx, v, phase, lobby, done) {
    const box = root.querySelector('.bluff-who');
    const show = ctx.playing && !lobby && !done && (phase === 'read' || phase === 'write' || phase === 'pick');
    const flags = phase === 'write' ? v.wrote || [] : phase === 'pick' ? v.picked || [] : [];
    let html = '';
    if (show)
      for (let i = 0; i < 8; i++) {
        if (!v.nicks || !v.nicks[i] || !(v.present && v.present[i])) continue;
        html += '<span class="bluff-chip bluff-c' + i + (flags[i] ? ' on' : '') + '">' + (flags[i] ? '✓ ' : '') + ctx.esc(nick(ctx, v, i)) + '</span>';
      }
    if (box.dataset.sig !== html) { box.dataset.sig = html; box.innerHTML = html; }
    box.hidden = !html;
  }

  /// Картки будуються раз на питання (підпис — номер і тексти), далі лише перефарбовуються на місці.
  function buildCards(box, ctx, v, st) {
    const cards = v.options || [];
    const sig = v.q + '\u0001' + cards.map((o) => o.text).join('\u0001');
    if (box.dataset.sig === sig) return false;
    box.dataset.sig = sig;
    st.opened = new Set();
    st.primed = false;
    st.opt = null;
    box.innerHTML = cards.map((o, i) => '<div class="bluff-cell" data-i="' + i + '">'
      + '<button type="button" class="bluff-opt" data-i="' + i + '" aria-label="Картка ' + (i + 1) + ': ' + ctx.esc(shown(o.text)) + '">'
      + '<span class="bluff-n">' + (i + 1) + '</span>'
      + '<span class="bluff-otext">' + ctx.esc(shown(o.text)) + '</span>'
      + '<span class="bluff-tag"></span>'
      + '<span class="bluff-by"></span>'
      + '<span class="bluff-picks"></span>'
      + '</button>'
      + '<button type="button" class="bluff-like" data-i="' + i + '" hidden aria-label="Сподобалась брехня">' + HEART + ' <b>0</b></button>'
      + '</div>').join('');
    return true;
  }

  function setText(el, text) { if (el.textContent !== text) el.textContent = text; }
  function setHtml(el, html) { if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; } }

  /// Малює картки; повертає номери щойно відкритих (для шоу й прокрутки).
  function paintCards(root, ctx, v, st, seat, phase, done) {
    const box = root.querySelector('.bluff-opts');
    const cards = v.options || [];
    const show = !done && cards.length > 0 && (phase === 'pick' || phase === 'reveal' || phase === 'score');
    box.hidden = !show;
    const fresh = [];
    if (!show) return fresh;
    buildCards(box, ctx, v, st);
    const my = v.my || {};
    const likes = my.likes || [];
    const open = v.revealed || [];
    const last = phase === 'reveal' && open.length ? open[open.length - 1] : -1;
    // Оптимістичний вибір: картка світиться одразу по кліку, а не за пів секунди, коли прийде вид.
    let pick = my.pick != null ? my.pick : -1;
    if (st.opt && st.opt.q === v.q && phase === 'pick' && Date.now() - st.opt.at < OPTIMISTIC_MS && st.opt.i !== pick) pick = st.opt.i;
    const cells = box.children;
    for (let i = 0; i < cards.length && i < cells.length; i++) {
      const o = cards[i];
      const cell = cells[i];
      const btn = cell.firstElementChild;
      const isOpen = o.by != null;
      if (isOpen && !st.opened.has(i)) { st.opened.add(i); if (st.primed) fresh.push(i); }
      const cls = 'bluff-opt'
        + (o.mine ? ' mine' : '')
        + (i === pick ? ' on' : '')
        + (phase === 'reveal' && !isOpen ? ' wait' : '')
        + (isOpen ? ' open ' + (o.truth ? 'truth' : o.decoy ? 'decoy' : 'lie') : '')
        + (i === last ? ' now' : '')
        + (btn.classList.contains('flip') ? ' flip' : '')
        + (btn.classList.contains('inert') ? ' inert' : '');
      if (btn.className !== cls) btn.className = cls;
      // Картки лишаються «живими» й на розкритті: там Ⓐ/клік по чужій брехні — ❤, а кільцю пада нема куди зникати.
      const likable = seat >= 0 && isOpen && !o.truth && !o.decoy && !o.mine && (phase === 'reveal' || phase === 'score');
      btn.disabled = seat < 0 || (phase === 'pick' && o.mine);
      btn.classList.toggle('inert', !(phase === 'pick' ? seat >= 0 && !o.mine : likable));
      setText(btn.querySelector('.bluff-tag'), o.mine ? 'твоя' : i === pick ? (phase === 'pick' ? '✓ обрано' : '✓ твій вибір') : '');
      setText(btn.querySelector('.bluff-by'), !isOpen ? '' : o.truth ? '✅ Правда' : o.decoy ? '🏺 Глек' : '🤥 ' + names(ctx, v, o.by));
      const picks = !isOpen ? '' : (o.picks || []).length
        ? o.picks.map((p, n) => '<span class="bluff-pk bluff-c' + p + '" style="--n:' + n + '">' + ctx.esc(nick(ctx, v, p)) + '</span>').join('')
        : '<span class="bluff-none">' + (o.truth ? 'ніхто не вгадав' : 'ніхто не повірив') + '</span>';
      setHtml(btn.querySelector('.bluff-picks'), picks);

      const like = cell.lastElementChild;
      const showLike = isOpen && !o.truth && !o.decoy && (likable || o.likes > 0);
      like.hidden = !showLike;
      if (showLike) {
        like.disabled = !likable;
        like.classList.toggle('on', likes.includes(i));
        setText(like.querySelector('b'), String(o.likes || 0));
      }
    }
    st.primed = true;
    // Нова відкрита картка — перевертаємо й озвучуємо (після F5 уже відкриті — без шоу).
    for (const i of fresh) {
      const btn = cells[i] && cells[i].firstElementChild;
      const o = cards[i];
      if (btn && !reduced()) {
        btn.classList.add('flip');
        setTimeout(() => btn.classList.remove('flip'), 450);
      }
      sound(o.truth ? 'truth' : 'open');
      if (o.mine && (o.picks || []).length) sound('boing');
    }
    return fresh;
  }

  /// Мій рядок на розкритті: кого надурила моя брехня і хто надурив мене — найсмішніша мить питання одним рядком,
  /// щоб не вичитувати її з карток і таблиці (на телефоні таблиця ще й під краєм).
  function paintMe(root, ctx, v, seat, phase, done) {
    const box = root.querySelector('.bluff-me');
    let html = '';
    if (seat >= 0 && !done && (phase === 'reveal' || phase === 'score')) {
      const cards = v.options || [];
      const my = v.my || {};
      const parts = [];
      const picked = my.pick != null ? cards[my.pick] : null;
      if (picked && picked.by != null) {
        const t = v.truthDelta ? v.truthDelta[seat] : 0;
        if (picked.truth) parts.push('<b class="ok">✅ Правду знайдено' + (t ? ' · +' + num(t) : '') + '</b>');
        else if (picked.decoy) parts.push('<span class="lie">🏺 Тебе надурив Глек</span>');
        else parts.push('<span class="lie">🤥 Тебе надурили: <b>' + ctx.esc(names(ctx, v, picked.by)) + '</b></span>');
      }
      const mine = cards.find((o) => o.mine && o.by != null);
      if (mine) {
        const n = (mine.picks || []).length;
        const pts = (v.delta ? v.delta[seat] : 0) - (v.truthDelta ? v.truthDelta[seat] : 0);
        parts.push(n
          ? '<b class="win">😏 Твоя брехня зловила ' + victims(n) + (pts > 0 ? ' · +' + num(pts) : '') + '</b>'
          : '<span class="muted">Твоя брехня нікого не надурила</span>');
      }
      html = parts.join('<span class="bluff-sep"> · </span>');
    }
    setHtml(box, html);
    box.hidden = !html;
  }

  /// Кінець партії: переможці, найкраща брехня й «Як це було».
  function paintEnd(root, ctx, v, done) {
    const box = root.querySelector('.bluff-end');
    let html = '';
    if (done && v.result) {
      const r = v.result;
      const sc = r.scores || v.scores || [];
      const win = r.winners || [];
      html += win.length
        ? '<div class="bluff-win">🏆 ' + win.map((i) => '<b class="bluff-c' + i + '">' + ctx.esc(nick(ctx, v, i)) + '</b>').join(' і ')
          + ' — ' + num(sc[win[0]]) + '</div>'
        : r.left ? '<div class="bluff-win draw">🚪 Гравці розійшлись — партію не дограли</div>'
        : '<div class="bluff-win draw">🤝 Нічия — ніхто нікого не переграв</div>';
      if (r.left && !r.best) { /* про «нікого не надурили» мовчимо: партія просто обірвалась */ }
      else if (r.best)
        html += '<div class="bluff-best"><span class="muted small">Найкраща брехня партії</span>'
          + '<b>«' + ctx.esc(shown(r.best.text)) + '»</b>'
          + '<span>' + ctx.esc(names(ctx, v, r.best.by)) + (r.best.hlek ? ' з підказки Глека 🎲' : '') + ' · ' + victims(r.best.victims)
          + (r.best.likes ? ' · ' + HEART + ' ' + r.best.likes : '') + '</span></div>';
      else html += '<div class="bluff-best none muted small">Цього разу нікого так і не надурили — чесні ви якісь</div>';
      const recap = r.recap || [];
      if (recap.length)
        // На невисокому екрані (Deck, ноут 1280×800) «Як це було» згорнуте: інакше «Ще раз» їде під край.
        html += '<details class="bluff-recap"' + (tall() ? ' open' : '') + '><summary>Як це було · ' + recap.length + ' ' + plural(recap.length, 'питання', 'питання', 'питань') + '</summary><ol>'
          + recap.map((x) => '<li><span class="bluff-rq">' + ctx.esc(x.text).split('___').join('<b class="bluff-ra">' + ctx.esc(x.answer) + '</b>') + '</span>'
            + (x.note ? '<span class="bluff-rn muted small">' + ctx.esc(x.note) + '</span>' : '')
            + (x.best ? '<span class="bluff-rb small">🤥 «' + ctx.esc(shown(x.best.text)) + '»' + (x.best.hlek ? ' 🎲' : '') + ' — ' + ctx.esc(names(ctx, v, x.best.by)) + ', ' + victims(x.best.victims) + '</span>' : '')
            + '</li>').join('') + '</ol></details>';
    }
    // Порівнюємо з тим, що малювали, а не з innerHTML: інакше кожен вид згортав би розгорнуте людиною.
    setHtml(box, html);
    box.hidden = !html;
  }

  const tall = () => (window.innerHeight || 0) >= 900;

  function paintScore(root, ctx, v, phase, lobby, done) {
    const box = root.querySelector('.bluff-score');
    let html = '';
    if (!lobby) {
      const sc = (done && v.result && v.result.scores) || v.scores || [];
      const win = (v.result && v.result.winners) || [];
      const seats = [];
      for (let i = 0; i < 8; i++) if (v.nicks && v.nicks[i]) seats.push(i);
      seats.sort((a, b) => (sc[b] || 0) - (sc[a] || 0) || a - b);
      const deltas = !done && (phase === 'reveal' || phase === 'score');
      html = seats.map((i) => {
        const gone = !(v.present && v.present[i]);
        let chips = '';
        if (deltas) {
          // Скільки за правду — каже сервер: той, хто встав, вибір на картці лишає, а очок не отримує.
          const t = v.truthDelta ? v.truthDelta[i] || 0 : 0;
          const fooled = (v.delta ? v.delta[i] || 0 : 0) - t;
          const liked = v.likeDelta ? v.likeDelta[i] : 0;
          if (t > 0) chips += '<i class="bluff-d ok">+' + num(t) + ' ✅</i>';
          if (fooled > 0) chips += '<i class="bluff-d lie">+' + num(fooled) + ' 🤥' + (v.victims && v.victims[i] > 1 ? '×' + v.victims[i] : '') + '</i>';
          if (liked > 0) chips += '<i class="bluff-d like">+' + num(liked) + ' ' + HEART + '</i>';
        }
        return '<div class="bluff-srow' + (win.includes(i) ? ' win' : '') + (gone ? ' gone' : '') + '">'
          + '<span class="bluff-dot bluff-c' + i + '"></span>'
          + '<span class="bluff-sn" title="' + ctx.esc(nick(ctx, v, i)) + '">' + (win.includes(i) ? '🏆 ' : '') + ctx.esc(nick(ctx, v, i)) + (gone ? ' <small>🚪</small>' : '') + '</span>'
          + '<b class="bluff-sv">' + num(sc[i]) + '</b>'
          + (chips ? '<span class="bluff-sd">' + chips + '</span>' : '')
          + '</div>';
      }).join('');
      if (html) html = '<div class="bluff-shead muted small">Рахунок</div>' + html;
    }
    setHtml(box, html);
    box.hidden = !html;
  }

  /// Тихий цокіт останніх секунд — лише тому, хто ще тримає стіл (не написав чи не обрав).
  function tickLoop(root) {
    const st = root._bf;
    if (!st || !st.ctx) return;
    const ctx = st.ctx;
    const v = ctx.view || {};
    if (!ctx.playing || (v.phase !== 'write' && v.phase !== 'pick')) return;
    const seat = me(ctx, v);
    const my = v.my || {};
    if (seat < 0) return;
    const left = (Date.parse(v.endsAt) || 0) - Date.now();
    if (v.phase === 'write' && left > 0 && left <= RESCUE_MS) rescue(root, ctx, v, st);
    if (document.hidden || (v.phase === 'write' ? !!my.lie : my.pick != null)) return;
    if (left <= 0 || left > TICK_LAST_MS) return;
    const sec = Math.ceil(left / 1000);
    if (st.tickAt === v.q * 1000 + sec + (v.phase === 'pick' ? 500 : 0)) return;
    st.tickAt = v.q * 1000 + sec + (v.phase === 'pick' ? 500 : 0);
    sound('tick');
  }

  // =============================================================================================
  // дії
  // =============================================================================================

  const typed = (input) => (input.value || '').replace(/\s+/g, ' ').trim();

  /// Час спливає, а в полі — не надіслане (чи переписане після «Готово»): шлемо самі, по разу на кожен текст.
  /// Сервер відповість звичним «Записано: «…»» (або відмовою, як на «Готово»).
  function rescue(root, ctx, v, st) {
    const input = root.querySelector('.bluff-in');
    if (!input || input.disabled) return;
    const text = typed(input);
    const my = v.my || {};
    const key = v.q + '\u0001' + text;
    if (!text || text === (my.lie || '') || st.rescued === key) return;
    st.rescued = key;
    ctx.act('lie', { text });
  }

  function submit(root, ctx) {
    const input = root.querySelector('.bluff-in');
    const text = typed(input);
    if (!text) { ctx.toast('Спершу вигадай брехню 🙂', 'err'); input.focus(); return; }
    ctx.act('lie', { text }).then((r) => {
      // На телефоні ховаємо клавіатуру — хай видно, хто ще пише.
      if (r && r.ok && HGames.ui.coarse()) input.blur();
    });
  }

  function pickCard(root, ctx, i) {
    const v = ctx.view || {};
    const o = (v.options || [])[i];
    if (!o || v.phase !== 'pick' || me(ctx, v) < 0) return;
    if (o.mine) { ctx.toast('Свою брехню обирати не можна 🙂', 'err'); return; }
    const st = state(root);
    st.opt = { i, q: v.q, at: Date.now() };
    paint(root, ctx);
    ctx.act('pick', { i });
  }

  function likeCard(ctx, i) {
    const v = ctx.view || {};
    const o = (v.options || [])[i];
    if (!o || o.by == null || o.truth || o.decoy || o.mine || me(ctx, v) < 0) return false;
    if (v.phase !== 'reveal' && v.phase !== 'score') return false;
    ctx.act('like', { i });
    return true;
  }

  /// Стрілки по картках: сітка знає свої колонки, тож ↑↓ стрибають на рядок.
  function moveFocus(root, code) {
    const list = [...root.querySelectorAll('.bluff-opt')];
    if (!list.length) return;
    const at = list.indexOf(document.activeElement);
    const grid = root.querySelector('.bluff-opts');
    const cols = Math.max(1, getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length);
    const step = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: cols, ArrowUp: -cols }[code] || 0;
    let i = at < 0 ? 0 : at + step;
    // Під останньою картою рядка порожньо — ↓ стає на останню картку, а не губиться.
    if (code === 'ArrowDown' && i >= list.length && at < list.length - 1 && Math.floor(at / cols) < Math.floor((list.length - 1) / cols)) i = list.length - 1;
    // Перестрибуємо свою (вимкнену) картку в тому самому напрямку.
    while (i >= 0 && i < list.length && list[i].disabled) i += step || 1;
    if (i < 0 || i >= list.length) return;
    list[i].focus();
  }

  function digit(code) {
    const m = /^(?:Digit|Numpad)([1-9])$/.exec(code || '');
    return m ? +m[1] : 0;
  }

  // =============================================================================================
  // модуль
  // =============================================================================================

  HGames.register({
    id: 'bluff',
    icon: ICON,
    added: '2026-09-27',
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Байкарі',
      items: [
        '🤥 Питання з пропуском і дивною правдою: впиши свою правдоподібну брехню (до 40 знаків)',
        '🔍 Потім усі шукають правду серед брехень — свою обрати не можна',
        '💰 Вгадав правду — +1000, кожен, кого надурила твоя брехня, — +500 тобі, ' + HEART + ' за найсмішнішу — +100',
        '🎲 Нема ідей — «Хай Глек збреше»: він підкине брехню з банку, а очки за неї — твої',
        '⏱ Останнє питання — подвійне; коли всі натиснули «Готово», фаза не чекає таймера',
      ],
    },
    seatNames: (i) => String(i + 1),
    seatClass: SEAT_CLASS,
    pad: {
      // Рядок підказок — під фазу: у write карток ще нема, а Ⓐ на полі відкриває клавіатуру.
      get hint() {
        return padPhase === 'write' ? '{a} на полі — клавіатура · «Готово» чи 🎲'
          : padPhase === 'pick' ? '{dpad} по картках · {a} обрати правду'
          : padPhase === 'reveal' || padPhase === 'score' ? '{dpad} по картках · {a} ' + HEART + ' брехні'
          : padPhase === 'read' ? 'Читай питання — за мить брехати'
          : '';
      },
      when: (ctx) => ctx.mine && ctx.playing,
    },

    mount(root, ctx) {
      root._bf = null;
      ctx._bluffRoot = root;
      root.innerHTML = '<div class="bluff-wrap"><div class="bluff">'
        + '<div class="bluff-main">'
        + '<div class="bluff-top"><span class="bluff-no muted small"></span><span class="bluff-x2" hidden title="Останнє питання — очки подвійні">×2</span>'
        + '<span class="bluff-arc"></span><button type="button" class="ghost bluff-snd" title="Звук" aria-label="Звук">🔈</button></div>'
        + '<div class="bluff-final" hidden>🔥 Останнє питання — правда й жертви вдвічі дорожчі!</div>'
        + '<div class="bluff-q" aria-live="polite" data-pad-focus></div>'
        + '<div class="bluff-stage muted small"></div>'
        + '<div class="bluff-write" hidden>'
        // «Готово» — поруч із полем, як «надіслати» в месенджері: над екранною клавіатурою телефона видно обидва.
        + '<div class="bluff-row"><span class="bluff-field"><input class="bluff-in" type="text" maxlength="' + MAX_LIE + '" autocomplete="off" autocorrect="off"'
        + ' autocapitalize="off" spellcheck="false" enterkeyhint="done" placeholder="твоя брехня…" aria-label="Твоя брехня">'
        + '<span class="bluff-cnt muted small">0/' + MAX_LIE + '</span></span>'
        + '<button type="button" class="primary bluff-go">Готово</button></div>'
        + '<div class="bluff-btns"><button type="button" class="ghost bluff-dice" title="Глек підкине брехню з банку — очки за неї твої">🎲 Хай Глек збреше</button></div>'
        + '<div class="bluff-my muted small"></div>'
        + '</div>'
        + '<div class="bluff-who"></div>'
        + '<div class="bluff-opts" hidden></div>'
        + '<div class="bluff-me" hidden aria-live="polite"></div>'
        + '<div class="bluff-note" hidden><img src="/static/glek.svg" alt=""><div><div class="bluff-ntext"></div><div class="bluff-quip muted small"></div></div></div>'
        + '<div class="bluff-end" hidden></div>'
        + '<ul class="bluff-rules muted small">' + RULES.map((r) => '<li>' + r + '</li>').join('') + '</ul>'
        + '</div>'
        + '<div class="bluff-score" hidden></div>'
        + '</div></div>';

      const input = root.querySelector('.bluff-in');
      input.addEventListener('input', () => count(root));
      // Телефон: клавіатура виїжджає не миттєво — підлаштовуємось і на фокус (з затримкою), і на зміну видимої частини.
      input.addEventListener('focus', () => { if (HGames.ui.coarse()) setTimeout(() => fitWrite(root), 350); });
      root._bfFit = () => { if (document.activeElement === input) requestAnimationFrame(() => fitWrite(root)); };
      if (window.visualViewport) window.visualViewport.addEventListener('resize', root._bfFit);
      input.addEventListener('keydown', (e) => {
        if (e.key !== 'Enter' || e.isComposing) return;
        e.preventDefault();
        submit(root, ctx);
      });
      root.querySelector('.bluff-go').onclick = () => submit(root, ctx);
      root.querySelector('.bluff-dice').onclick = () => ctx.act('lie', { auto: true });
      root.querySelector('.bluff-snd').onclick = () => {
        store.set('bluffMute', muted() ? '0' : '1');
        root.querySelector('.bluff-snd').textContent = muted() ? '🔇' : '🔈';
        if (!muted()) sound('open');
      };
      root.querySelector('.bluff-opts').addEventListener('click', (e) => {
        const like = e.target.closest('.bluff-like');
        if (like) { likeCard(ctx, +like.dataset.i); return; }
        const opt = e.target.closest('.bluff-opt');
        if (!opt || opt.disabled) return;
        const v = ctx.view || {};
        if (v.phase === 'pick') pickCard(root, ctx, +opt.dataset.i);
        else likeCard(ctx, +opt.dataset.i);
      });
      // Звук — лише після жесту людини (політика браузерів і просто чемність).
      const wake = () => { gestured = true; };
      root.addEventListener('pointerdown', wake, { passive: true });
      root.addEventListener('keydown', wake);
      paint(root, ctx);
      root._bfTick = setInterval(() => tickLoop(root), 250);
    },

    update(root, ctx) {
      ctx._bluffRoot = root;
      paint(root, ctx);
    },

    onKey(e, ctx) {
      gestured = true;
      const root = ctx._bluffRoot;
      const v = ctx.view || {};
      if (!root || !ctx.playing) return false;
      const n = digit(e.code);
      if (n && v.phase === 'pick') {
        const o = (v.options || [])[n - 1];
        if (o) pickCard(root, ctx, n - 1);
        return !!o;
      }
      if (n && (v.phase === 'reveal' || v.phase === 'score')) return likeCard(ctx, n - 1);
      if (v.phase === 'pick' && /^Arrow(Up|Down|Left|Right)$/.test(e.code || '')) {
        moveFocus(root, e.code);
        return true;
      }
      // Брехню можна просто почати друкувати: перша ж літера йде в поле (без preventDefault — щоб не згубилась).
      if (v.phase === 'write' && me(ctx, v) >= 0 && e.key && (e.key.length === 1 || e.key === 'Enter') && !e.altKey && !e.ctrlKey && !e.metaKey) {
        const input = root.querySelector('.bluff-in');
        if (input && !input.disabled && document.activeElement !== input) input.focus({ preventScroll: true });
        return false;
      }
      return false;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      // Фаза — з виду: сервер міняє її лише разом із видом.
      const v = ctx.view || {};
      const mine = me(ctx, v) >= 0;
      const my = v.my || {};
      switch (v.phase) {
        case 'read': return v.final ? '🔥 Фінал: очки вдвічі' : 'Читай питання…';
        case 'write': return !mine ? 'Байкарі брешуть…' : my.lie ? 'Записано. Чекаємо на решту…' : 'Пиши брехню й тисни «Готово»';
        case 'pick': return !mine ? 'Усі думають…' : my.pick != null ? 'Обрано. Можна передумати' : 'Де правда? Обери картку';
        case 'reveal': return 'Розкриваємо…';
        case 'score': return v.q < v.of ? 'Рахунок · далі питання ' + (v.q + 1) : 'Рахунок';
        default: return '';
      }
    },

    unmount(root, ctx) {
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      if (root._bfTick) { clearInterval(root._bfTick); root._bfTick = 0; }
      if (root._bfFit && window.visualViewport) window.visualViewport.removeEventListener('resize', root._bfFit);
      root._bfFit = null;
      const main = root.querySelector('.bluff-main');
      if (main) main.removeAttribute('data-pad-scope');
      root._bf = null;
      padPhase = '';
      if (ctx && ctx._bluffRoot === root) ctx._bluffRoot = null;
    },
  });
})();
