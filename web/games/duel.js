/*
  Дуель-вестерн. Реалтайм: сервер тикає раз на 50 мс, але кадр шле лише на зміну фази — тож
  малювати треба не «щотика», а «щофази», і вся анімація живе в CSS (duel.css).

  Кадр (Impl/Duel.cs): { phase: 'ready'|'aim'|'fire'|'result'|'done', round, wins: [w0, w1],
                         last: { winner, reason, ms: [m0, m1] } | null, nextIn }
  Вид: те саме + best: [ms0, ms1] (найшвидша реакція кожного за партію).
  Моменту «ВОГОНЬ!» у кадрі нема свідомо: інакше виграв би не той, у кого швидша рука, а той,
  хто читає кадри з консолі.

  Ввід: Input('shoot') — без payload. Стріляти можна будь-якою клавішею, кліком по сцені й кнопкою.
*/
(() => {
  'use strict';

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M8 1.6 9.7 5.65 14.09 6.02 10.76 8.9 11.76 13.18 8 10.9 4.24 13.18 5.24 8.9 1.91 6.02 6.3 5.65Z"'
    + ' fill="var(--accent)"/></svg>';

  /// Що кричить розпорядник дуелі. Порожньо — сцена мовчить (чекаємо суперника або партію зіграно).
  const CALL = { ready: 'ГОТУЙСЬ…', aim: 'ЦІЛЬСЯ…', fire: 'ВОГОНЬ!' };

  /// Клавіші, якими не стріляють: службові й ті, якими люди ходять по сторінці.
  const NOT_A_TRIGGER = new Set(['Tab', 'Escape', 'Shift', 'Control', 'Alt', 'Meta', 'CapsLock', 'ContextMenu', 'Enter']);

  /// Стан картки живе тут, а не на root: onKey отримує лише ctx, а він у картки той самий увесь час.
  const states = new WeakMap();

  /// П'ятикутна зірка шерифа на груди — та сама фігура, що й в іконці, лише менша.
  const STAR = 'M18.5 21.8 19.35 23.83 21.54 24.01 19.88 25.45 20.38 27.59 18.5 26.45'
    + ' 16.62 27.59 17.12 25.45 15.46 24.01 17.65 23.83Z';

  /// Капелюхи: у кожного стрільця свій силует, щоб на вулиці на чотирьох їх не плутали й без кольору.
  const HATS = {
    sheriff: '<ellipse cx="20" cy="12.2" rx="11.6" ry="2.4"/><path d="M13.8 12.2q-.5-7.4 6.2-7.4t6.2 7.4z"/>',
    bandit: '<ellipse cx="20" cy="12.8" rx="15.6" ry="3"/><path d="M14.8 12.8q-.7-6.3 5.2-6.3t5.2 6.3z"/>',
    // шулер — котелок: вузькі криси й кругла маківка
    gambler: '<ellipse cx="20" cy="12.6" rx="8.4" ry="1.8"/><path d="M14.6 12.6q0-6.4 5.4-6.4t5.4 6.4z"/>',
    // гробар — високий циліндр
    undertaker: '<ellipse cx="20" cy="12.6" rx="8.8" ry="1.7"/><rect x="15.2" y="2.2" width="9.6" height="10.6" rx="1"/>',
  };

  /// Силует проти сонця: капелюх, голова, тулуб, ноги і рука з револьвером окремою групою —
  /// саме її переможець піднімає вгору. <paramref name="scarf"/> — хустка кольору місця (перестрілка).
  function figure(kind, scarf) {
    return '<svg class="dfig" viewBox="0 0 40 62" aria-hidden="true">'
      + '<g fill="currentColor">'
      + (HATS[kind] || HATS.sheriff)
      + '<circle cx="20" cy="16.6" r="4.1"/>'
      + '<path d="M14.2 20.4q5.8-1.7 11.6 0l1.4 17.6h-14.4z"/>'
      + '<path d="M13.4 38h13.2l1.5 22h-5.1l-2-15.4h-1.9l-2 15.4h-5.1z"/>'
      + '<g class="darm"><path d="M25.2 21.2 30.8 29.6 28.4 31.3 22.8 23z"/>'
      + '<rect x="29.2" y="28.4" width="6.8" height="2.4" rx="1.2"/></g>'
      + '</g>'
      + (scarf ? '<path class="dscarf" d="M15.4 20.2h9.2l-4.6 5.2z" fill="' + scarf + '"/>' : '')
      + (kind === 'sheriff' ? '<path class="dstar" d="' + STAR + '" fill="var(--accent)"/>' : '')
      + '</svg>';
  }

  // ---- звук: постріл, свисток кулі в небо, дзвін на «ВОГОНЬ!». Вимикач — у картці ----
  const Snd = {
    on: (() => { try { return localStorage.getItem('duel.sound') !== '0'; } catch { return true; } })(),
    ctx: null,
    noise: null,
    ensure() {
      if (!this.on) return null;
      if (!this.ctx) {
        const AC = window.AudioContext || window.webkitAudioContext;
        const ua = navigator.userActivation;
        // AudioContext — лише після жесту людини, інакше браузер його глушить і сварить у консоль.
        if (!AC || (ua && !ua.hasBeenActive)) return null;
        try { this.ctx = new AC(); } catch { return null; }
        const len = Math.floor(this.ctx.sampleRate * 0.5);
        this.noise = this.ctx.createBuffer(1, len, this.ctx.sampleRate);
        const d = this.noise.getChannelData(0);
        for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, 2);
      }
      if (this.ctx.state === 'suspended') this.ctx.resume().catch(() => {});
      return this.ctx;
    },
    /// Постріл: шумовий удар крізь фільтр, що швидко темніє. <paramref name="far"/> — чужий, тихіший.
    bang(far) {
      const c = this.ensure();
      if (!c) return;
      const t = c.currentTime, src = c.createBufferSource(), f = c.createBiquadFilter(), g = c.createGain();
      src.buffer = this.noise;
      f.type = 'lowpass';
      f.frequency.setValueAtTime(far ? 1800 : 3200, t);
      f.frequency.exponentialRampToValueAtTime(300, t + 0.25);
      g.gain.setValueAtTime(far ? 0.16 : 0.28, t);
      g.gain.exponentialRampToValueAtTime(0.001, t + 0.35);
      src.connect(f).connect(g).connect(c.destination);
      src.start(t);
      src.stop(t + 0.4);
    },
    tone(freq, ms, type, vol, to) {
      const c = this.ensure();
      if (!c) return;
      const t = c.currentTime, o = c.createOscillator(), g = c.createGain();
      o.type = type;
      o.frequency.setValueAtTime(freq, t);
      if (to) o.frequency.exponentialRampToValueAtTime(to, t + ms / 1000);
      g.gain.setValueAtTime(vol, t);
      g.gain.exponentialRampToValueAtTime(0.0001, t + ms / 1000);
      o.connect(g).connect(c.destination);
      o.start(t);
      o.stop(t + ms / 1000 + 0.02);
    },
    /// Куля в небо: свист угору.
    whistle() { this.tone(700, 420, 'sine', 0.05, 2400); },
    /// «ВОГОНЬ!» — короткий дзвін, щоб і вухом почути.
    bell() { this.tone(1320, 380, 'triangle', 0.06); },
    set(on) {
      this.on = on;
      try { localStorage.setItem('duel.sound', on ? '1' : '0'); } catch { /* приватне вікно */ }
    },
  };

  /// Кнопка-вимикач звуку, одна на картку.
  function soundBtn(host) {
    let b = host.querySelector('.dsnd');
    if (!b) {
      b = document.createElement('button');
      b.type = 'button';
      b.className = 'dsnd ghost small';
      b.dataset.padSkip = '';
      b.addEventListener('click', () => { Snd.set(!Snd.on); if (Snd.on) Snd.bang(true); soundBtn(host); });
      host.appendChild(b);
    }
    const t = Snd.on ? '🔊 звук' : '🔇 без звуку';
    if (b.textContent !== t) b.textContent = t;
  }

  function state(root, ctx) {
    let st = states.get(ctx);
    if (!st || st.root !== root) {
      st = { root: root, last: null, seen: null, phase: '', timer: 0, els: null };
      states.set(ctx, st);
    }
    return st;
  }

  /// Кадр несе лише те, що змінилось за фазу, а рекорди приходять видом — тому не замінюємо
  /// стан кадром, а домішуємо його: інакше best зникав би між раундами.
  const merge = (prev, next) => Object.assign({}, prev || {}, next || {});

  /// Домішати вид — але ТІЛЬКИ якщо він справді новий. Каркас перемальовує картку і на кожну подію
  /// лобі (хтось створив стіл), причому тим самим, збереженим видом; а вид дуелі сервер шле лише на
  /// межі раундів. Без цієї перевірки чужий стіл у лобі гасив би «ВОГОНЬ!» посеред вікна пострілу
  /// й відкочував сцену на «Готуйсь…». Кожна подія 'room' приносить свіжий об'єкт — його й ловимо.
  function take(st, ctx) {
    if (!ctx.view || !ctx.view.phase || ctx.view === st.seen) return;
    st.seen = ctx.view;
    st.last = merge(st.last, ctx.view);
  }

  function build(root, ctx) {
    const st = state(root, ctx);
    if (st.els && st.els.wrap.isConnected) return st;
    root.innerHTML = '';
    const wrap = document.createElement('div');
    wrap.className = 'duel';
    wrap.innerHTML = '<div class="dtop"><span class="gscore dwins"><b>0</b> : <b>0</b></span>'
      + '<span class="dround muted small"></span></div>'
      + '<div class="dscene" data-phase="wait" role="button" tabindex="-1" aria-label="Сцена дуелі: тисни, щоб вистрілити">'
      + '<div class="dsky"></div><div class="dsun"></div><div class="dstreet"></div><div class="dtumble"></div>'
      + '<div class="dguy sheriff">' + figure('sheriff') + '</div>'
      + '<div class="dguy bandit">' + figure('bandit') + '</div>'
      + '<div class="dcall"></div><div class="dflash"></div></div>'
      + '<div class="dmsg small"></div>'
      + '<button type="button" class="primary dfire">🔫 Стріляти</button>';
    root.appendChild(wrap);
    soundBtn(wrap);
    st.els = {
      wrap: wrap,
      // Рахунок збираємо один раз, а далі правимо лише текст цифр: шлях кадру HTML не парсить.
      wins: [...wrap.querySelectorAll('.dwins b')],
      round: wrap.querySelector('.dround'),
      scene: wrap.querySelector('.dscene'),
      call: wrap.querySelector('.dcall'),
      msg: wrap.querySelector('.dmsg'),
      fire: wrap.querySelector('.dfire'),
      guys: [wrap.querySelector('.dguy.sheriff'), wrap.querySelector('.dguy.bandit')],
    };
    // Колбек беремо з останнього виклику (ctx той самий, але зайвий раз вішати слухач нема потреби).
    st.els.scene.addEventListener('pointerdown', () => shoot(st, st.ctx || ctx));
    st.els.fire.addEventListener('click', () => shoot(st, st.ctx || ctx));
    return st;
  }

  /// Стріляти можна й до слова «ВОГОНЬ!» — це і є фальстарт, і сервер його чесно зарахує.
  function shoot(st, ctx) {
    if (!ctx || !ctx.mine || !ctx.playing) return;
    const phase = (st.last && st.last.phase) || '';
    if (phase === 'result' || phase === 'done' || !phase) return;
    ctx.input('shoot');
  }

  const nameOf = (ctx, i) => ctx.nickOf(i) || ctx.seatName(i);

  /// Чим скінчився раунд — людською мовою і без відмінювання чужих ніків.
  function resultText(ctx, s) {
    const l = s && s.last;
    if (!l) return '';
    const ms = l.ms || [];
    if (l.reason === 'shot' && l.winner != null) {
      const w = l.winner, o = w === 0 ? 1 : 0;
      return nameOf(ctx, w) + ': ' + ms[w] + ' мс · '
        + nameOf(ctx, o) + (ms[o] != null ? ': ' + ms[o] + ' мс' : ' — без пострілу');
    }
    if (l.reason === 'false' && l.winner != null) {
      const late = l.winner === 0 ? 1 : 0;
      return 'Фальстарт: ' + nameOf(ctx, late) + ' — куля в небо. Раунд бере ' + nameOf(ctx, l.winner);
    }
    if (l.reason === 'both-false') return 'Обидва поспішили — по кулі в небо. Раунд перегравають';
    if (l.reason === 'sleep') return 'Ніхто не вистрілив — раунд перегравають';
    return '';
  }

  /// Спалах на 120 мс у мить пострілу. Клас знімаємо руками, щоб анімація завелась і вдруге.
  function flash(st) {
    const scene = st.els.scene;
    scene.classList.remove('flash');
    void scene.offsetWidth;                 // перезапуск анімації без цього не станеться
    scene.classList.add('flash');
    clearTimeout(st.timer);
    st.timer = setTimeout(() => scene.classList.remove('flash'), 260);
  }

  function render(root, ctx) {
    const st = state(root, ctx);
    if (!st.els) return;
    st.ctx = ctx;
    const s = st.last || {};
    // Партію могли й обірвати (хтось устав) — тоді фаза раунду на сервері лишилась якою була,
    // а сцені все одно кінець. Стіл, що чекає на суперника, стоїть тихо, без «Готуйсь…».
    const over = ctx.room && ctx.room.status === 'finished';
    const phase = over ? 'done' : ctx.playing ? (s.phase || 'ready') : 'wait';
    const wins = s.wins || [0, 0];
    const l = s.last;

    for (let i = 0; i < 2; i++) {
      const w = String(wins[i] || 0);
      if (st.els.wins[i].textContent !== w) st.els.wins[i].textContent = w;
    }
    // Рядок під рахунком: який зараз раунд і чий рекорд руки. Чужий рекорд у картці ні до чого.
    const parts = [];
    if (phase === 'done') parts.push('дуель зіграно');
    else if (phase !== 'wait') parts.push('раунд ' + (s.round || 1));
    const best = ctx.mine ? (s.best || [])[ctx.seat] : null;
    if (best != null) parts.push('твоя найшвидша ' + best + ' мс');
    const round = parts.join(' · ');
    if (st.els.round.textContent !== round) st.els.round.textContent = round;

    if (st.els.scene.dataset.phase !== phase) {
      const was = st.els.scene.dataset.phase;
      st.els.scene.dataset.phase = phase;
      if (phase === 'fire') { flash(st); Snd.bell(); }
      // Підсумок раунду — на слух: постріл або свист кулі в небо. Лише на живому переході, а не
      // коли картку щойно відкрили посеред паузи між раундами.
      if (phase === 'result' && was && was !== 'wait' && l) {
        if (l.reason === 'shot') Snd.bang(false);
        else if (l.reason === 'false' || l.reason === 'both-false') Snd.whistle();
      }
    }
    const call = CALL[phase] || '';
    if (st.els.call.textContent !== call) st.els.call.textContent = call;
    st.els.call.classList.toggle('big', phase === 'fire');

    // Пози показуємо, поки видно підсумок раунду: переможець піднімає револьвер, той, хто
    // спізнився або поспішив, падає в пилюку. Обірвану партію лишаємо без поз — там нема кого класти.
    // «Кінець» — лише коли стіл справді дограний: відкритий наново стіл (хтось сів на вільне місце)
    // має показувати очікування, а не підсумок минулої партії.
    const ended = (s.phase === 'done' && phase === 'done') || phase === 'result';
    const showPose = ended && l && l.winner != null;
    for (let i = 0; i < 2; i++) {
      st.els.guys[i].classList.toggle('won', !!showPose && l.winner === i);
      st.els.guys[i].classList.toggle('lost', !!showPose && l.winner !== i);
    }

    const msg = ended ? resultText(ctx, s)
      : phase === 'done' ? ''                       // партію обірвали: підсумок напише каркас
        : phase === 'wait' ? 'Чекаємо на другого стрільця'
          : ctx.mine ? 'Стріляй будь-якою клавішею, кліком по вулиці або кнопкою'
            : 'Дивишся збоку';
    if (st.els.msg.textContent !== msg) st.els.msg.textContent = msg;

    const canFire = ctx.mine && ctx.playing && phase !== 'result' && phase !== 'done' && phase !== 'wait';
    // Дуель зіграно — кнопку ховаємо: під нею «Ще раз», і велика неактивна «Стріляти» тільки плутає.
    st.els.fire.hidden = !ctx.mine || phase === 'done';
    if (st.els.fire.disabled !== !canFire) st.els.fire.disabled = !canFire;
  }

  HGames.register({
    id: 'duel',
    icon: ICON,
    seatNames: ['шериф', 'бандит'],
    seatClass: ['x', 'o'],
    pad: { a: 'Space', anyBtn: true, hint: '{a} стріляти (будь-яка кнопка) — щойно побачиш сигнал' },
    news: {
      v: '2026-09-24',
      title: 'Дуель: гримить і дзвенить',
      items: [
        '🔔 «ВОГОНЬ!» тепер ще й дзвенить, постріл гримить, а поспішив — чути, як куля свистить у небо',
        '🔇 Кому тихіше — вимикач звуку під сценою',
        '🤠 Зібрались утрьох чи вчотирьох? Поруч нова гра — «Перестрілка», кожен проти кожного',
      ],
    },

    mount(root, ctx) {
      take(build(root, ctx), ctx);
      render(root, ctx);
    },

    update(root, ctx) {
      take(build(root, ctx), ctx);
      render(root, ctx);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!st.els || !f || !f.phase) return;
      st.last = merge(st.last, f);
      render(root, ctx);
    },

    /// Стріляють будь-якою клавішею: у вестерні ніхто не шукає пробіл. Каркас уже відсіяв поля вводу
    /// й комбінації з Ctrl/Alt/Cmd, тож сюди доходить саме те, чим можна тиснути на гачок.
    onKey(e, ctx) {
      if (e.repeat || NOT_A_TRIGGER.has(e.key) || /^F\d{1,2}$/.test(e.key)) return false;
      const st = states.get(ctx);
      if (!st) return false;
      const phase = (st.last && st.last.phase) || '';
      if (!ctx.mine || !ctx.playing || phase === 'result' || phase === 'done' || !phase) return false;
      shoot(st, ctx);
      return true;
    },

    status(ctx) {
      const st = states.get(ctx);
      const s = (st && st.last) || {};
      if (s.phase === 'done' || !ctx.playing) return '';   // підсумок партії каркас напише сам
      if (s.phase === 'ready') return 'Готуйсь…';
      if (s.phase === 'aim') return 'Цілься…';
      if (s.phase === 'fire') return 'ВОГОНЬ! Тисни!';
      if (s.phase === 'result') return resultText(ctx, s);
      return '';
    },

    unmount(root, ctx) {
      const st = states.get(ctx);
      if (st) clearTimeout(st.timer);
      states.delete(ctx);
    },
  });

  // ============================================================================================
  // Перестрілка (shootout) — кожен проти кожного на трьох-чотирьох. Живе в цьому ж файлі (Client: "duel"):
  // та сама вулиця, ті самі силуети, звук і розпорядник, лише стрільців більше і кожен має ціль.
  //
  // Кадр (Impl/Shootout.cs): { phase, round, wins[4], plays[4], alive[4], aim[4] (місце | null),
  //   shot[4] (мс | null), hit[4] (у кого влучив | null), fs[4] (фальстарт), last: { winner, reason:
  //   'last'|'many'|'sleep' } | null, nextIn, target } ; вид — те саме + best[4].
  // Ввід: Input('aim', { at }) або Input('aim', { step: ±1 }), Input('shoot').
  // ============================================================================================

  const S_KINDS = ['sheriff', 'bandit', 'gambler', 'undertaker'];
  const S_SHAPES = ['●', '■', '▲', '◆'];
  const S_COLORS = [['--accent', '#f4c542'], ['--ok', '#7bd389'], ['--clay', '#d9825b'], ['--duel-blue', '#6fb3e8']];
  /// Де стоїть кожен на вулиці: двоє спереду по краях, решта — глибше, менші.
  const SPOTS = {
    3: [{ x: 11, b: 5, h: 60, face: 1 }, { x: 89, b: 5, h: 60, face: -1 }, { x: 50, b: 25, h: 44, face: 1 }],
    4: [{ x: 9, b: 5, h: 60, face: 1 }, { x: 91, b: 5, h: 60, face: -1 },
      { x: 33, b: 25, h: 43, face: 1 }, { x: 67, b: 25, h: 43, face: -1 }],
  };
  const VB_H = 56.25;              // висота viewBox оверлею ліній (ширина 100, сцена 16:9)
  const sstates = new WeakMap();

  function sState(root, ctx) {
    let st = sstates.get(ctx);
    if (!st || st.root !== root) {
      st = { root: root, last: null, seen: null, timer: 0, els: null, key: '', prev: null };
      sstates.set(ctx, st);
    }
    return st;
  }

  /// Хто на вулиці: у партії — ті, хто грає раунд; поза нею — ті, хто сидить за столом.
  function lineup(ctx, s) {
    const seats = [];
    const playing = ctx.playing && s && Array.isArray(s.plays) && s.plays.some(Boolean);
    for (let i = 0; i < 4; i++) {
      const on = playing ? s.plays[i] : !!(ctx.room && ctx.room.seats && ctx.room.seats[i] && ctx.room.seats[i].nick);
      if (on) seats.push(i);
    }
    return seats;
  }

  function spotsFor(n) { return SPOTS[n] || SPOTS[n < 3 ? 3 : 4]; }

  function sBuild(root, ctx) {
    const st = sState(root, ctx);
    if (st.els && st.els.wrap.isConnected) return st;
    root.innerHTML = '';
    const wrap = document.createElement('div');
    wrap.className = 'duel shoot';
    wrap.innerHTML = '<div class="dtop"><span class="sboard"></span><span class="dround muted small"></span></div>'
      + '<div class="dscene" data-phase="wait">'
      + '<div class="dsky"></div><div class="dsun"></div><div class="dstreet"></div><div class="dtumble"></div>'
      + '<div class="sguys"></div>'
      + '<svg class="slines" viewBox="0 0 100 ' + VB_H + '" preserveAspectRatio="none" aria-hidden="true"></svg>'
      + '<div class="dcall"></div><div class="dflash"></div></div>'
      + '<div class="dmsg small"></div>'
      + '<button type="button" class="primary dfire">🔫 Стріляти</button>';
    root.appendChild(wrap);
    soundBtn(wrap);
    st.els = {
      wrap: wrap,
      board: wrap.querySelector('.sboard'),
      round: wrap.querySelector('.dround'),
      scene: wrap.querySelector('.dscene'),
      guys: wrap.querySelector('.sguys'),
      lines: wrap.querySelector('.slines'),
      call: wrap.querySelector('.dcall'),
      msg: wrap.querySelector('.dmsg'),
      fire: wrap.querySelector('.dfire'),
    };
    // Тап по фігурі — ціль на неї. Стріляють кнопкою чи клавішею: тап по вулиці тут надто легко
    // сплутати з вибором цілі, а фальстарт коштує патрона.
    st.els.guys.addEventListener('pointerdown', (e) => {
      const g = e.target.closest('.dguy');
      const c = st.ctx || ctx;
      if (!g || !c.mine || !c.playing) return;
      const at = +g.dataset.seat;
      if (at === c.seat) return;
      e.preventDefault();
      sAim(st, c, { at: at });
    });
    st.els.fire.addEventListener('click', () => sShoot(st, st.ctx || ctx));
    return st;
  }

  const canAct = (st, ctx) => {
    const s = st.last || {};
    const ph = s.phase || '';
    return ctx.mine && ctx.playing && (ph === 'ready' || ph === 'aim' || ph === 'fire')
      && s.alive && s.alive[ctx.seat];
  };

  function sAim(st, ctx, payload) {
    if (!canAct(st, ctx)) return;
    // Малюємо ціль одразу, не чекаючи кадру: переводити ствол має бути миттєво.
    const s = st.last;
    if (payload.at != null && s.alive[payload.at] && payload.at !== ctx.seat) {
      s.aim = s.aim.slice();
      s.aim[ctx.seat] = payload.at;
      sRender(st.root, ctx);
    }
    ctx.input('aim', payload);
  }

  function sShoot(st, ctx) {
    if (!canAct(st, ctx)) return;
    const s = st.last;
    if (s.fs && s.fs[ctx.seat]) return;
    if (s.shot && s.shot[ctx.seat] != null) return;
    ctx.input('shoot');
  }

  function colorOf(ctx, i) { return ctx.css(S_COLORS[i][0], S_COLORS[i][1]); }

  /// Фігури: перебудовуємо лише коли змінився склад на вулиці, далі — тільки класи й підписи.
  function sGuys(st, ctx, seats) {
    const key = seats.join(',');
    if (st.key === key) return;
    st.key = key;
    const spots = spotsFor(seats.length);
    st.els.guys.innerHTML = seats.map((seat, k) => {
      const p = spots[k] || spots[0];
      // Табличка з ніком над крайніми стоїть не по центру, а всередину сцени — інакше на телефоні її ріже край.
      const edge = p.x < 20 ? ' el' : p.x > 80 ? ' er' : '';
      return '<div class="dguy s' + seat + (p.face < 0 ? ' left' : '') + edge + '" data-seat="' + seat + '" role="button"'
        + ' style="left:' + p.x + '%;bottom:' + p.b + '%;height:' + p.h + '%">'
        + figure(S_KINDS[seat], 'var(' + S_COLORS[seat][0] + ')')
        + '<div class="stag"><b>' + S_SHAPES[seat] + '</b> <span class="snick"></span><span class="sms"></span></div></div>';
    }).join('');
  }

  /// Лінії прицілів: від руки стрільця до грудей цілі. Моя — товща; влучання — суцільна червона.
  function sLines(st, ctx, s, seats) {
    const spots = spotsFor(seats.length);
    const where = {};
    seats.forEach((seat, k) => { where[seat] = spots[k] || spots[0]; });
    const y = (p, k) => VB_H * (1 - (p.b + p.h * k) / 100);
    const out = [];
    const phase = s.phase || '';
    const show = phase === 'ready' || phase === 'aim' || phase === 'fire' || phase === 'result';
    if (show && s.aim) {
      for (const seat of seats) {
        const from = where[seat];
        const hit = s.hit ? s.hit[seat] : null;
        const t = hit != null ? hit : s.aim[seat];
        // Мертвий не цілиться; фальстартер без патрона — теж (його ствол дивиться в небо).
        if (t == null || !where[t] || (hit == null && (!s.alive[seat] || (s.fs && s.fs[seat])))) continue;
        if (phase === 'result' && hit == null) continue;
        const to = where[t];
        const x1 = from.x + from.face * from.h * 0.12, y1 = y(from, 0.5);
        const x2 = to.x, y2 = y(to, 0.62);
        const mine = ctx.mine && seat === ctx.seat;
        out.push('<line x1="' + x1.toFixed(1) + '" y1="' + y1.toFixed(1) + '" x2="' + x2.toFixed(1) + '" y2="' + y2.toFixed(1)
          + '" class="sl' + (mine ? ' mine' : '') + (hit != null ? ' hit' : '') + '" stroke="' + (hit != null ? ctx.css('--danger', '#ff6b5a') : colorOf(ctx, seat)) + '"/>');
      }
    }
    const html = out.join('');
    if (st.els.lines.innerHTML !== html) st.els.lines.innerHTML = html;
  }

  const nick = (ctx, i) => ctx.nickOf(i) || ctx.seatName(i);

  /// Хто кого за раунд: «Оля ➜ Петро 231 мс · Ігор — куля в небо». Без відмінювання чужих ніків.
  function sSummary(ctx, s) {
    const l = s.last;
    if (!l) return '';
    const parts = [];
    const order = [0, 1, 2, 3].filter((i) => s.shot && s.shot[i] != null).sort((a, b) => s.shot[a] - s.shot[b]);
    for (const i of order) {
      const h = s.hit[i];
      parts.push(nick(ctx, i) + (h != null ? ' ➜ ' + nick(ctx, h) : ' — мимо') + ' ' + s.shot[i] + ' мс');
    }
    for (let i = 0; i < 4; i++) if (s.fs && s.fs[i]) parts.push(nick(ctx, i) + ' — куля в небо');
    const head = l.reason === 'last' && l.winner != null ? 'Раунд бере ' + nick(ctx, l.winner) + '!'
      : l.reason === 'sleep' ? 'Ніхто не вистрілив — раунд переграють'
        : 'На ногах лишились кілька — раунд нічий';
    return head + (parts.length ? ' · ' + parts.join(' · ') : '');
  }

  /// Звуки на зміну кадру: дзвін на «ВОГОНЬ!», постріл на кожен новий постріл, свист на фальстарт.
  function sSounds(st, ctx, s) {
    const p = st.prev;
    st.prev = s;
    if (!p || !s || !st.els.scene.offsetParent) return;
    if (s.phase === 'fire' && p.phase !== 'fire') Snd.bell();
    for (let i = 0; i < 4; i++) {
      if (s.shot && s.shot[i] != null && (!p.shot || p.shot[i] == null)) Snd.bang(!(ctx.mine && i === ctx.seat));
      if (s.fs && s.fs[i] && (!p.fs || !p.fs[i])) Snd.whistle();
    }
  }

  function sRender(root, ctx) {
    const st = sState(root, ctx);
    if (!st.els) return;
    st.ctx = ctx;
    const s = st.last || {};
    const over = ctx.room && ctx.room.status === 'finished';
    const phase = over ? 'done' : ctx.playing ? (s.phase || 'ready') : 'wait';
    const seats = lineup(ctx, s);
    const wins = s.wins || [0, 0, 0, 0];

    // Табло: фігура, нік і виграні раунди кожного.
    const board = seats.map((i) => '<span class="sb s' + i + (ctx.mine && i === ctx.seat ? ' me' : '') + '"><b>'
      + S_SHAPES[i] + '</b> ' + ctx.esc(nick(ctx, i)) + ' <em>' + (wins[i] || 0) + '</em></span>').join('');
    if (st.els.board.innerHTML !== board) st.els.board.innerHTML = board;
    const parts = [];
    if (phase === 'done') parts.push('перестрілку зіграно');
    else if (phase !== 'wait') parts.push('раунд ' + (s.round || 1) + ' · до ' + (s.target || 3) + ' перемог');
    const best = ctx.mine && s.best ? s.best[ctx.seat] : null;
    if (best != null) parts.push('твоя найшвидша ' + best + ' мс');
    const round = parts.join(' · ');
    if (st.els.round.textContent !== round) st.els.round.textContent = round;

    if (st.els.scene.dataset.phase !== phase) {
      st.els.scene.dataset.phase = phase;
      if (phase === 'fire') flash(st);
    }
    const call = CALL[phase] || '';
    if (st.els.call.textContent !== call) st.els.call.textContent = call;
    st.els.call.classList.toggle('big', phase === 'fire');

    sGuys(st, ctx, seats);
    const ended = phase === 'result' || (phase === 'done' && s.phase === 'done');
    for (const g of st.els.guys.children) {
      const i = +g.dataset.seat;
      // Лежить той, кого підстрелили: і посеред раунду, і на фінальному кадрі дограної партії.
      const shown = ctx.playing || phase === 'done';
      g.classList.toggle('lost', !!(shown && s.alive && s.plays && s.plays[i] && !s.alive[i]));
      g.classList.toggle('won', ended && s.last && s.last.winner === i);
      g.classList.toggle('fs', !!(s.fs && s.fs[i]));
      g.classList.toggle('me', !!ctx.mine && i === ctx.seat);
      g.classList.toggle('target', !!ctx.mine && ctx.playing && s.aim && s.aim[ctx.seat] === i && phase !== 'result');
      const n = g.querySelector('.snick');
      const nk = nick(ctx, i) + (ctx.mine && i === ctx.seat ? ' (ти)' : '');
      if (n.textContent !== nk) n.textContent = nk;
      const ms = g.querySelector('.sms');
      const t = s.shot && s.shot[i] != null && phase !== 'ready' && phase !== 'aim' ? ' · ' + s.shot[i] + ' мс'
        : s.fs && s.fs[i] ? ' · 💨' : '';
      if (ms.textContent !== t) ms.textContent = t;
    }
    sLines(st, ctx, s, seats);

    let msg;
    if (ended) msg = sSummary(ctx, s);
    else if (phase === 'done') msg = '';
    else if (phase === 'wait') {
      msg = seats.length < 3 ? 'Чекаємо стрільців: треба троє або четверо' : 'Стрільці на місцях — господар тисне «Почати»';
    } else if (!ctx.mine) msg = 'Дивишся збоку';
    else if (s.alive && !s.alive[ctx.seat]) msg = 'Тебе підстрелили — полеж, наступного раунду встанеш';
    else if (s.fs && s.fs[ctx.seat]) msg = 'Поспішив — куля в небо, патрона до кінця раунду нема. Сподівайся, що в тебе не влучать';
    else if (s.shot && s.shot[ctx.seat] != null) msg = 'Патрон витрачено — дивись, хто кого';
    else {
      const t = s.aim ? s.aim[ctx.seat] : null;
      msg = (t != null ? 'Ціль: ' + nick(ctx, t) + '. ' : '')
        + 'Змінити — тап по фігурі або ← →; на ВОГОНЬ стріляй кнопкою чи будь-якою іншою клавішею';
    }
    if (st.els.msg.textContent !== msg) st.els.msg.textContent = msg;

    const armed = canAct(st, ctx) && !(s.fs && s.fs[ctx.seat]) && !(s.shot && s.shot[ctx.seat] != null);
    st.els.fire.hidden = !ctx.mine || phase === 'done';
    if (st.els.fire.disabled !== !armed) st.els.fire.disabled = !armed;
  }

  function sTake(st, ctx) {
    if (!ctx.view || !ctx.view.phase || ctx.view === st.seen) return;
    st.seen = ctx.view;
    st.last = merge(st.last, ctx.view);
  }

  HGames.register({
    id: 'shootout',
    icon: '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
      + '<path d="M5.2 1.8 6.3 4.4 9.1 4.6 7 6.5 7.6 9.2 5.2 7.8 2.8 9.2 3.4 6.5 1.3 4.6 4.1 4.4Z" fill="var(--accent)"/>'
      + '<path d="M11 6.8 11.8 8.7 13.9 8.9 12.3 10.3 12.8 12.3 11 11.3 9.2 12.3 9.7 10.3 8.1 8.9 10.2 8.7Z" fill="var(--clay)"/>'
      + '<circle cx="4.2" cy="12.8" r="1.6" fill="var(--ok)"/></svg>',
    seatNames: ['шериф', 'бандит', 'шулер', 'гробар'],
    seatClass: ['x', 'o', 'c', 'db'],
    pad: { dirs: true, a: 'Space', anyBtn: true, hint: '{dpad} у кого цілишся · {a} вогонь (будь-яка кнопка)' },
    news: {
      v: '2026-09-24',
      title: 'Перестрілка: дуель на трьох-чотирьох',
      items: [
        '🤠 Нова гра: троє-четверо на одній вулиці, кожен проти кожного',
        '🎯 Поки «Цілься…» — обери, в кого цілишся (тап по фігурі або ← →). Хто в кого — бачать усі',
        '🔫 На ВОГОНЬ у кожного один патрон: хто перший вистрілив, той і влучив, а підстрелений уже не відповість',
        '👑 Раунд бере той, хто лишився на ногах сам. Перестрілку — перший, хто взяв три раунди',
      ],
    },

    mount(root, ctx) {
      sTake(sBuild(root, ctx), ctx);
      sRender(root, ctx);
    },

    update(root, ctx) {
      const st = sBuild(root, ctx);
      sTake(st, ctx);
      sRender(root, ctx);
    },

    frame(root, ctx, f) {
      const st = sState(root, ctx);
      if (!st.els || !f || !f.phase) return;
      st.last = merge(st.last, f);
      sSounds(st, ctx, st.last);
      sRender(root, ctx);
    },

    onKey(e, ctx) {
      if (e.repeat || NOT_A_TRIGGER.has(e.key) || /^F\d{1,2}$/.test(e.key)) return false;
      const st = sstates.get(ctx);
      if (!st || !canAct(st, ctx)) return false;
      if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') { sAim(st, ctx, { step: -1 }); return true; }
      if (e.key === 'ArrowRight' || e.key === 'ArrowDown') { sAim(st, ctx, { step: 1 }); return true; }
      if (/^[1-4]$/.test(e.key)) { sAim(st, ctx, { at: +e.key - 1 }); return true; }
      sShoot(st, ctx);
      return true;
    },

    status(ctx) {
      const st = sstates.get(ctx);
      const s = (st && st.last) || {};
      if (s.phase === 'done' || !ctx.playing) return '';
      if (s.phase === 'ready') return 'Готуйсь… обери ціль';
      if (s.phase === 'aim') return 'Цілься…';
      if (s.phase === 'fire') return 'ВОГОНЬ!';
      if (s.phase === 'result' && s.last) {
        const l = s.last;
        return l.reason === 'last' && l.winner != null ? 'Раунд бере ' + nick(ctx, l.winner) : l.reason === 'sleep' ? 'Ніхто не вистрілив' : 'Раунд нічий';
      }
      return '';
    },

    unmount(root, ctx) {
      const st = sstates.get(ctx);
      if (st) clearTimeout(st.timer);
      sstates.delete(ctx);
    },
  });
})();
