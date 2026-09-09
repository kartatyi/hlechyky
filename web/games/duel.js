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

  /// Силует проти сонця: капелюх, голова, тулуб, ноги і рука з револьвером окремою групою —
  /// саме її переможець піднімає вгору.
  function figure(kind) {
    const hat = kind === 'sheriff'
      ? '<ellipse cx="20" cy="12.2" rx="11.6" ry="2.4"/><path d="M13.8 12.2q-.5-7.4 6.2-7.4t6.2 7.4z"/>'
      : '<ellipse cx="20" cy="12.8" rx="15.6" ry="3"/><path d="M14.8 12.8q-.7-6.3 5.2-6.3t5.2 6.3z"/>';
    return '<svg class="dfig" viewBox="0 0 40 62" aria-hidden="true">'
      + '<g fill="currentColor">'
      + hat
      + '<circle cx="20" cy="16.6" r="4.1"/>'
      + '<path d="M14.2 20.4q5.8-1.7 11.6 0l1.4 17.6h-14.4z"/>'
      + '<path d="M13.4 38h13.2l1.5 22h-5.1l-2-15.4h-1.9l-2 15.4h-5.1z"/>'
      + '<g class="darm"><path d="M25.2 21.2 30.8 29.6 28.4 31.3 22.8 23z"/>'
      + '<rect x="29.2" y="28.4" width="6.8" height="2.4" rx="1.2"/></g>'
      + '</g>'
      + (kind === 'sheriff' ? '<path class="dstar" d="' + STAR + '" fill="var(--accent)"/>' : '')
      + '</svg>';
  }

  function state(root, ctx) {
    let st = states.get(ctx);
    if (!st || st.root !== root) {
      st = { root: root, last: null, phase: '', timer: 0, els: null };
      states.set(ctx, st);
    }
    return st;
  }

  /// Кадр несе лише те, що змінилось за фазу, а рекорди приходять видом — тому не замінюємо
  /// стан кадром, а домішуємо його: інакше best зникав би між раундами.
  const merge = (prev, next) => Object.assign({}, prev || {}, next || {});

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
    st.els = {
      wrap: wrap,
      wins: wrap.querySelector('.dwins'),
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

    const score = '<b>' + (wins[0] || 0) + '</b> : <b>' + (wins[1] || 0) + '</b>';
    if (st.els.wins.innerHTML !== score) st.els.wins.innerHTML = score;
    // Рядок під рахунком: який зараз раунд і чий рекорд руки. Чужий рекорд у картці ні до чого.
    const parts = [];
    if (phase === 'done') parts.push('дуель зіграно');
    else if (phase !== 'wait') parts.push('раунд ' + (s.round || 1));
    const best = ctx.mine ? (s.best || [])[ctx.seat] : null;
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

    // Пози показуємо, поки видно підсумок раунду: переможець піднімає револьвер, той, хто
    // спізнився або поспішив, падає в пилюку. Обірвану партію лишаємо без поз — там нема кого класти.
    const ended = s.phase === 'done' || phase === 'result';
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
    st.els.fire.hidden = !ctx.mine;
    if (st.els.fire.disabled !== !canFire) st.els.fire.disabled = !canFire;
  }

  HGames.register({
    id: 'duel',
    icon: ICON,
    seatNames: ['шериф', 'бандит'],
    seatClass: ['x', 'o'],

    mount(root, ctx) {
      const st = build(root, ctx);
      if (ctx.view && ctx.view.phase) st.last = merge(st.last, ctx.view);
      render(root, ctx);
    },

    update(root, ctx) {
      const st = build(root, ctx);
      if (ctx.view && ctx.view.phase) st.last = merge(st.last, ctx.view);
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
})();
