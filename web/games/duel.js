/*
  Дуель-вестерн. Реалтайм: сервер тикає раз на 50 мс, але кадр шле лише на зміну фази — тож
  малювати треба не «щотика», а «щофази», і вся анімація живе в CSS (duel.css).

  Кадр (Impl/Duel.cs): { phase: 'ready'|'aim'|'fire'|'result'|'done', round, wins: [w0, w1],
                         last: { winner, reason, ms: [m0, m1] } | null, nextIn }
  Вид: те саме + best: [ms0, ms1] (найшвидша реакція кожного за партію).
  Моменту «ВОГОНЬ!» у кадрі нема свідомо: інакше виграв би не той, у кого швидша рука, а той,
  хто читає кадри з консолі.

  Ввід: Input('shoot') — без payload. Стріляти можна будь-якою клавішею, кліком по сцені й кнопкою.

  Прохід №3 (29.09): опційні поля кадру — sig ('word'|'bell'|'sun'|'sky', опція «Сигнал»), decoy { w, n } (обманка
  в «Цілься…», опція «Обманки»), fid + ping[] (опція «Пінг»: клієнт відлунює кожен кадр Input('pong', { f: fid })),
  avg[] (середня реакція за партію), rec { week: { n, ms } | null, pb[] } (рекорди «найшвидшої руки»),
  last.bait / last.nr[] ('pb'|'week') / last.pc[] (поправка на пінг). Турнір стрільців (duelcup) — той самий кадр
  дуелі + pair [місце ліворуч, праворуч] | null, need, cup (сітка), champ.
*/
(() => {
  'use strict';

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M8 1.6 9.7 5.65 14.09 6.02 10.76 8.9 11.76 13.18 8 10.9 4.24 13.18 5.24 8.9 1.91 6.02 6.3 5.65Z"'
    + ' fill="var(--accent)"/></svg>';

  /// Що кричить розпорядник дуелі. Порожньо — сцена мовчить (чекаємо суперника або партію зіграно).
  const CALL = { ready: 'ГОТУЙСЬ…', aim: 'ЦІЛЬСЯ…', fire: 'ВОГОНЬ!' };

  /// Чим гримить «ВОГОНЬ!» (п. 67): напис зі спалахом, лише дзвін, спалах сонця чи червоне небо — «вухаті» проти «очатих».
  const SIGS = {
    word: { badge: '', hint: '' },
    bell: { badge: '🔔 на дзвін', hint: '🔔 Цей раунд — на дзвін: стріляй, щойно почуєш' },
    sun: { badge: '☀️ на сонце', hint: '☀️ Цей раунд — на сонце: стріляй, щойно воно спалахне' },
    sky: { badge: '🌆 на небо', hint: '🌆 Цей раунд — на небо: стріляй, щойно воно почервоніє' },
  };
  const sigOf = (s) => (s && s.sig && SIGS[s.sig] ? s.sig : 'word');

  /// Слово розпорядника. Обманка (п. 65) кричить 0,9 с тим самим червоним, що й справжнє «ВОГОНЬ!». Коли сигнал — не
  /// напис, «ЦІЛЬСЯ…» лишається й на вогні: інакше зникле слово саме стало б сигналом.
  function callText(st, s, phase) {
    if (phase === 'aim' && s.decoy && st.baitUntil > performance.now()) return s.decoy.w;
    if (phase === 'fire') {
      const sig = sigOf(s);
      if (sig === 'word') return CALL.fire;
      if (sig === 'bell' && !Snd.on) return '🔔';   // без звуку дзвону не почуєш — хоч значок
      return CALL.aim;
    }
    return CALL[phase] || '';
  }

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
    /// Обманка: глухий «бздинь» — схожий на сигнал рівно настільки, щоб рука смикнулась.
    clonk() { this.tone(440, 260, 'square', 0.035, 330); },
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
    b.title = Snd.on ? 'Вирубити звук' : 'Врубити звук';
  }

  /// Кнопка «Стріляти» бахає на натиск (pointerdown), а не на click: той приходить, лише коли палець
  /// відпустили, — це ще 50–120 мс, а раунд тут вирішують десятки. На телефоні саме ця кнопка й головна,
  /// тож тапом по ній людина програвала тому, хто тисне клавішу. Клавіатура (Enter на кнопці) — через click.
  function trigger(btn, fire) {
    btn.addEventListener('pointerdown', (e) => {
      if (e.button > 0) return;
      e.preventDefault();
      fire();
    });
    btn.addEventListener('click', (e) => { if (e.detail === 0) fire(); });
  }

  /// Телефон: у Перестрілці на чотирьох шапка столу й табло штовхали сцену вниз, і кнопка «Стріляти»
  /// ховалась під нижнім меню та «💬 Стіл». Раз на партію (room.startedAt), коли вона пішла, прокручуємо
  /// так, щоб рахунок, сцена й кнопка стали між шапкою сайту й меню. Усе й так видно — не чіпаємо.
  function fitPhone(st, ctx) {
    // Поворот телефона чи ⛶ — підгонку повторюємо (раз на партію її лишаємо для прокрутки самої людини).
    // Лише справжня зміна екрана, а не смикання адресного рядка під час прокрутки.
    const host = st.els && st.els.wrap.parentNode;
    if (host && !st.fitHook && ctx.ui.onFit) {
      st.fitW = innerWidth; st.fitH = innerHeight;
      st.fitHook = (f) => {
        const w = (f && f.w) || innerWidth, h = (f && f.h) || innerHeight;
        if (st.fitW === w && Math.abs(st.fitH - h) < 100) return;
        st.fitW = w; st.fitH = h;
        st.fitFor = null;
        if (st.ctx) fitPhone(st, st.ctx);
      };
      ctx.ui.onFit(host, st.fitHook);
    }
    if (!st.els || !ctx.mine || !ctx.playing || !ctx.room || !ctx.ui.coarse()) return;
    const key = ctx.room.startedAt || '';
    if (st.fitFor === key) return;
    const a = st.els.wrap.getBoundingClientRect(), b = st.els.fire.getBoundingClientRect();
    if (!a.height || !b.height) return;              // картку зараз не видно — спробуємо на наступному виді
    st.fitFor = key;
    let top, limit;
    if (ctx.ui.fit) {
      // каркас знає, що зараз зайнято згори й знизу (у ⛶ і лежачи шапки й меню нема)
      const f = ctx.ui.fit();
      top = f.top + 4;
      limit = f.h - f.dock - 6;
    } else {
      const head = document.querySelector('header');
      top = (head ? head.getBoundingClientRect().bottom : 0) + 4;
      const tabs = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--tabs-h')) || 0;
      const fab = document.querySelector('.tchat.drawer:not(.open) .tc-head');
      const fr = fab && fab.getBoundingClientRect();
      limit = (fr && fr.height ? Math.min(fr.top, innerHeight - tabs) : innerHeight - tabs) - 6;
    }
    const lo = b.bottom - limit, hi = a.top - top;   // на скільки прокрутити: не менше lo, не більше hi
    const dy = lo <= hi ? Math.min(Math.max(0, lo), hi) : lo;
    if (Math.abs(dy) < 2) return;
    const calm = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    window.scrollBy({ top: dy, behavior: calm ? 'auto' : 'smooth' });
  }

  /// Перемалювати, лише коли рядок справді інший. Порівнювати з el.innerHTML марно: браузер серіалізує його
  /// по-своєму (&#39; → ', лапки, style), тож «інше» виходило майже завжди — і DOM перебудовувався щокадру.
  const putHtml = (el, html) => { if (el._h !== html) { el._h = html; el.innerHTML = html; } };
  const putText = (el, t) => { if (el.textContent !== t) el.textContent = t; };
  const nick = (ctx, i) => ctx.nickOf(i) || ctx.seatName(i);

  /// Шкала реакцій раунду (п. 68): «⚡ сигнал → Оля 231 → Петро 305», хто поспішив — окремим рядком, а наприкінці
  /// партії — середня реакція кожного. rows: [{ n, ms, fs, me, win, nr }].
  function scaleHtml(ctx, rows, avg, width) {
    const shots = rows.filter((r) => r.ms != null).sort((a, b) => a.ms - b.ms);
    const fs = rows.filter((r) => r.fs);
    let h = '';
    if (shots.length) {
      const max = Math.max(400, shots[shots.length - 1].ms) * 1.1;
      const w = width || 400;
      // Підписи — на «доріжках» над і під лінією: кожен на першу, де не налазить на сусіда ліворуч (на чотирьох
      // у Перестрілці двоє з різницею в 20 мс інакше писались один по одному).
      const lanes = [-1e9, -1e9, -1e9, -1e9];
      let deep = false;
      let marks = '';
      for (const r of shots) {
        const x = 5 + (r.ms / max) * 92;
        const side = x < 22 ? 'l' : x > 78 ? 'r' : '';
        const text = r.n + ' ' + r.ms + (r.nr === 'week' ? ' 🏆' : r.nr === 'pb' ? ' 🏅' : '');
        const tw = text.length * 6.6 + 10, px = (x / 100) * w;
        const from = side === 'l' ? px - 8 : side === 'r' ? px - tw + 8 : px - tw / 2;
        let lane = lanes.findIndex((end) => end + 6 <= from);
        if (lane < 0) lane = lanes.indexOf(Math.min(...lanes));
        lanes[lane] = from + tw;
        if (lane > 1) deep = true;
        marks += '<span class="dsc-mark ln' + lane + (side ? ' ' + side : '') + (r.me ? ' me' : '') + (r.win ? ' win' : '')
          + '" style="left:' + x.toFixed(1) + '%"><b>' + ctx.esc(text) + '</b></span>';
      }
      h += '<div class="dsc-bar' + (deep ? ' deep' : '') + '"><i class="dsc-zero" title="сигнал">⚡</i>' + marks + '</div>';
    }
    if (fs.length) h += '<div class="dsc-line">💨 зарано: ' + fs.map((r) => ctx.esc(r.n)).join(', ') + '</div>';
    if (avg) h += '<div class="dsc-line dsc-avg">' + avg + '</div>';
    return h;
  }

  /// «Середня реакція: Оля 247 мс · Петро 301 мс» — лише тим, хто хоч раз влучно стріляв.
  function avgText(ctx, s, seats) {
    if (!s.avg) return '';
    const parts = seats.filter((i) => s.avg[i] != null).map((i) => ctx.esc(nick(ctx, i)) + ' ' + s.avg[i] + ' мс');
    return parts.length ? 'Середня реакція: ' + parts.join(' · ') : '';
  }

  /// Рядок під сценою: рекорд тижня (п. 64), мій рекорд і поправка на пінг, якщо стіл з нею.
  /// pbSeats — які місця стоять за елементами rec.pb (у дуелі й перестрілці — самі місця, у турнірі — пара).
  function recText(ctx, s, pbSeats, pingSeats) {
    const parts = [];
    const r = s.rec;
    if (r && r.week) parts.push('🏆 Найшвидша рука тижня: ' + r.week.n + ' — ' + r.week.ms + ' мс');
    const k = ctx.mine ? pbSeats.indexOf(ctx.seat) : -1;
    if (r && r.pb && k >= 0 && r.pb[k] != null) parts.push('твій рекорд ' + r.pb[k] + ' мс');
    if (s.ping && pingSeats.length) {
      parts.push('📶 поправка на пінг: ' + pingSeats.map((i) => nick(ctx, i) + ' −' + (s.ping[i] || 0)).join(' · ') + ' мс');
    }
    return parts.join(' · ');
  }

  /// Тости рекордів одразу після раунду: рекорд тижня бачать усі, свій особистий — лише я.
  function recToasts(ctx, nr, seatOf, msOf) {
    if (!nr) return;
    nr.forEach((f, i) => {
      if (!f) return;
      const seat = seatOf(i);
      if (f === 'week') ctx.toast('🏆 ' + nick(ctx, seat) + ' — найшвидша рука тижня: ' + msOf(i) + ' мс!');
      else if (ctx.mine && seat === ctx.seat) ctx.toast('🏅 Новий рекорд: ' + msOf(i) + ' мс!');
    });
  }

  /// Нова обманка в кадрі — кричимо її 0,9 с і глухо бздинькаємо. rerender — як перемалювати, коли вона стихне.
  function baitCheck(st, s, rerender) {
    if (s.phase !== 'aim' || !s.decoy || s.decoy.n === st.baitN) return;
    st.baitN = s.decoy.n;
    st.baitUntil = performance.now() + 900;
    if (st.els && st.els.scene.offsetParent) Snd.clonk();
    clearTimeout(st.baitTimer);
    st.baitTimer = setTimeout(() => { st.baitUntil = 0; rerender(); }, 920);
  }

  /// Сигнал на вогні: напис — спалах і дзвін; дзвін — лише дзвін; сонце й небо — мовчки (CSS за data-sig).
  function fireCue(st, s) {
    const sig = sigOf(s);
    if (sig === 'word') flash(st);
    if (sig === 'word' || sig === 'bell') Snd.bell();
  }

  /// Значок сигналу в куті сцени, поки раунд живий (лише з опцією «різний»).
  function sigBadge(st, s, phase) {
    const live = phase === 'ready' || phase === 'aim' || phase === 'fire';
    putText(st.els.sig, live && s.sig ? SIGS[sigOf(s)].badge : '');
    if (st.els.scene.dataset.sig !== sigOf(s)) st.els.scene.dataset.sig = sigOf(s);
  }

  function state(root, ctx) {
    let st = states.get(ctx);
    if (!st || st.root !== root) {
      st = { root: root, last: null, seen: null, phase: '', timer: 0, els: null, baitN: null, baitUntil: 0, baitTimer: 0, fid: null, pairKey: '' };
      states.set(ctx, st);
    }
    return st;
  }

  /// Кадр несе лише те, що змінилось за фазу, а рекорди приходять видом — тому не замінюємо
  /// стан кадром, а домішуємо його: інакше best зникав би між раундами. Обманку сервер шле лише в «Цілься…»
  /// (поля нема — нема й обманки), тож її не домішуємо, а беремо як є.
  const merge = (prev, next) => {
    const o = Object.assign({}, prev || {}, next || {});
    if (next && next.phase && !('decoy' in next)) o.decoy = null;
    return o;
  };

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
    wrap.className = 'duel' + (st.cup ? ' cup' : '');
    // Турнір: над рахунком — хто з ким зараз (ніки пари), під кнопкою — сітка.
    wrap.innerHTML = '<div class="dtop">' + (st.cup ? '<b class="dpn"></b>' : '')
      + '<span class="gscore dwins"><b>0</b> : <b>0</b></span>' + (st.cup ? '<b class="dpn"></b>' : '')
      + '<span class="duround muted small"></span></div>'
      + '<div class="dscene" data-phase="wait" role="button" tabindex="-1" aria-label="Сцена дуелі: тисни, щоб вистрілити">'
      + '<div class="dsky"></div><div class="dsun"></div><div class="dstreet"></div><div class="dtumble"></div>'
      + '<div class="dguy sheriff">' + figure('sheriff') + '</div>'
      + '<div class="dguy bandit">' + figure('bandit') + '</div>'
      + '<div class="dsig"></div><div class="dcall"></div><div class="dflash"></div></div>'
      + '<div class="dmsg small"></div>'
      + '<button type="button" class="primary dfire">🔫 Стріляти</button>'
      // Шкала — під кнопкою: з'являється лише між раундами, і кнопка під пальцем не має стрибати.
      + '<div class="dscale small"></div>'
      + '<div class="drec muted small"></div>'
      + (st.cup ? '<div class="dcup small"></div>' : '');
    root.appendChild(wrap);
    soundBtn(wrap);
    st.els = {
      wrap: wrap,
      // Рахунок збираємо один раз, а далі правимо лише текст цифр: шлях кадру HTML не парсить.
      wins: [...wrap.querySelectorAll('.dwins b')],
      names: [...wrap.querySelectorAll('.dpn')],
      round: wrap.querySelector('.duround'),
      scene: wrap.querySelector('.dscene'),
      sig: wrap.querySelector('.dsig'),
      call: wrap.querySelector('.dcall'),
      msg: wrap.querySelector('.dmsg'),
      scale: wrap.querySelector('.dscale'),
      rec: wrap.querySelector('.drec'),
      cup: wrap.querySelector('.dcup'),
      fire: wrap.querySelector('.dfire'),
      guys: [wrap.querySelector('.dguy.sheriff'), wrap.querySelector('.dguy.bandit')],
    };
    // Колбек беремо з останнього виклику (ctx той самий, але зайвий раз вішати слухач нема потреби).
    st.els.scene.addEventListener('pointerdown', () => shoot(st, st.ctx || ctx));
    trigger(st.els.fire, () => shoot(st, st.ctx || ctx));
    return st;
  }

  /// Яка сторона вулиці моя: у дуелі — моє місце, у турнірі — моє місце в поточній парі (-1 — я дивлюсь).
  function mySide(st, ctx) {
    if (!ctx.mine) return -1;
    if (!st.cup) return ctx.seat;
    const p = st.last && st.last.pair;
    return p ? p.indexOf(ctx.seat) : -1;
  }
  /// Яке місце стоїть на стороні i.
  const seatOf = (st, i) => (st.cup ? ((st.last && st.last.pair) || [0, 1])[i] : i);
  const fighter = (st, ctx) => ctx.playing && mySide(st, ctx) >= 0;

  /// Стріляти можна й до слова «ВОГОНЬ!» — це і є фальстарт, і сервер його чесно зарахує.
  function shoot(st, ctx) {
    if (!ctx || !fighter(st, ctx)) return;
    const phase = (st.last && st.last.phase) || '';
    if (phase === 'result' || phase === 'done' || !phase) return;
    ctx.input('shoot');
  }

  const nameOf = (ctx, i) => ctx.nickOf(i) || ctx.seatName(i);

  /// Чим скінчився раунд — людською мовою і без відмінювання чужих ніків.
  function resultText(st, ctx, s) {
    const l = s && s.last;
    if (!l) return '';
    const ms = l.ms || [];
    const n = (i) => nameOf(ctx, seatOf(st, i));
    if (l.reason === 'shot' && l.winner != null) {
      const w = l.winner, o = w === 0 ? 1 : 0;
      return n(w) + ': ' + ms[w] + ' мс · ' + n(o) + (ms[o] != null ? ': ' + ms[o] + ' мс' : ' — без пострілу');
    }
    if (l.reason === 'false' && l.winner != null) {
      const late = l.winner === 0 ? 1 : 0;
      return (l.bait ? n(late) + ' стрельнув на «' + l.bait + '» — куля в небо' : 'Фальстарт: ' + n(late) + ' — куля в небо')
        + '. Раунд бере ' + n(l.winner);
    }
    if (l.reason === 'both-false') {
      return (l.bait ? 'Обидва повелись на «' + l.bait + '»' : 'Обидва поспішили') + ' — по кулі в небо. Раунд перегравають';
    }
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

  /// Турнір: назва кола за кількістю матчів у ньому.
  const cupRound = (n) => (n === 1 ? 'фінал' : n === 2 ? 'півфінал' : n === 4 ? 'чвертьфінал' : '1/' + n);

  /// Сітка турніру: кола стовпчиками, поточна пара підсвічена, переможці жирним, «без бою» — сірим.
  function cupHtml(ctx, s) {
    const cup = s.cup;
    if (!cup || !cup.length) return '';
    const nm = (i) => ctx.esc(nick(ctx, i)) + (ctx.mine && i === ctx.seat ? ' (ти)' : '');
    let h = '';
    for (const round of cup) {
      h += '<div class="dcr"><div class="dcr-t">' + cupRound(round.length) + '</div>';
      for (const m of round) {
        const [a, b, w, sa, sb, walk] = m;
        if (a == null && b == null) continue;
        if (a == null || b == null) {
          h += '<div class="dcm bye">' + nm(a != null ? a : b) + ' — без бою</div>';
          continue;
        }
        const cur = w == null && s.pair && s.pair[0] === a && s.pair[1] === b;
        const mid = w != null ? (walk ? '—' : sa + ':' + sb) : cur ? '⚔' : 'vs';
        h += '<div class="dcm' + (cur ? ' cur' : '') + '"><span' + (w === a ? ' class="w"' : '') + '>' + nm(a) + '</span> <em>' + mid
          + '</em> <span' + (w === b ? ' class="w"' : '') + '>' + nm(b) + '</span></div>';
      }
      h += '</div>';
    }
    if (s.champ != null) h += '<div class="dcr champ">🤠 ' + nm(s.champ) + '</div>';
    return h;
  }

  /// Чи вибув я з турніру: є дограний матч зі мною, і переможець — не я.
  function knockedOut(ctx, s) {
    if (!s.cup || !ctx.mine) return false;
    return s.cup.some((r) => r.some((m) => m[2] != null && (m[0] === ctx.seat || m[1] === ctx.seat) && m[2] !== ctx.seat));
  }

  /// Хто на вулиці за столом зараз (для очікування турніру).
  const seatedCount = (ctx) => ((ctx.room && ctx.room.seats) || []).filter((x) => x && x.nick).length;

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
    const side = mySide(st, ctx);
    const pair = st.cup ? s.pair || null : [0, 1];

    for (let i = 0; i < 2; i++) putText(st.els.wins[i], String(wins[i] || 0));
    if (st.cup) {
      // Турнір: ніки пари над рахунком і капелюхи за місцями — перемальовуємо фігури лише на зміну пари.
      for (let i = 0; i < 2; i++) putText(st.els.names[i], pair && ctx.playing ? nick(ctx, pair[i]) : '');
      const key = pair ? pair.join(',') : '';
      if (st.pairKey !== key) {
        st.pairKey = key;
        for (let i = 0; i < 2; i++) {
          st.els.guys[i].innerHTML = figure(pair ? S_KINDS[pair[i] % 4] : i ? 'bandit' : 'sheriff');
        }
      }
    }
    // Рядок під рахунком: який зараз раунд і чий рекорд руки. Чужий рекорд у картці ні до чого.
    const parts = [];
    if (phase === 'done') parts.push(st.cup ? 'турнір зіграно' : 'дуель зіграно');
    else if (phase !== 'wait') {
      if (st.cup && s.cup && s.cup.length) parts.push(cupRound(s.cup[s.cup.length - 1].length) + ' · матч до ' + (s.need || 2));
      parts.push('раунд ' + (s.round || 1));
    }
    const mySeatBest = ctx.mine && s.best ? s.best[ctx.seat] : null;
    if (mySeatBest != null) parts.push('твоя найшвидша ' + mySeatBest + ' мс');
    putText(st.els.round, parts.join(' · '));

    sigBadge(st, s, phase);
    if (st.els.scene.dataset.phase !== phase) {
      const was = st.els.scene.dataset.phase;
      st.els.scene.dataset.phase = phase;
      if (phase === 'fire') fireCue(st, s);
      // Підсумок раунду — на слух: постріл або свист кулі в небо. Лише на живому переході, а не
      // коли картку щойно відкрили посеред паузи між раундами.
      if (phase === 'result' && was && was !== 'wait' && l) {
        if (l.reason === 'shot') Snd.bang(false);
        else if (l.reason === 'false' || l.reason === 'both-false') Snd.whistle();
        recToasts(ctx, l.nr, (i) => seatOf(st, i), (i) => (l.ms[i] || 0) + ((l.pc && l.pc[i]) || 0));
      }
    }
    // Турнір: на старті кожного матчу розпорядник оголошує пару.
    const intro = st.cup && phase === 'ready' && !l && pair;
    const call = intro ? nick(ctx, pair[0]) + ' ⚔ ' + nick(ctx, pair[1]) : callText(st, s, phase);
    putText(st.els.call, call);
    st.els.call.classList.toggle('big', phase === 'fire' && call === CALL.fire || (phase === 'aim' && call !== CALL.aim && !!call));
    st.els.call.classList.toggle('intro', !!intro);

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

    let msg;
    if (st.cup && phase === 'done' && s.champ != null && s.phase === 'done') msg = '🤠 Шериф вечора — ' + nick(ctx, s.champ) + '!';
    else if (ended) msg = resultText(st, ctx, s);
    else if (phase === 'done') msg = '';                       // партію обірвали: підсумок напише каркас
    else if (phase === 'wait') {
      msg = !st.cup ? 'Чекаємо на другого стрільця'
        : seatedCount(ctx) < 3 ? 'Чекаємо стрільців: від трьох до восьми' : 'Стрільці на місцях — господар тисне «Почати»';
    } else if (side < 0) {
      msg = !st.cup || !pair ? 'Дивишся збоку'
        : 'Стріляються ' + nick(ctx, pair[0]) + ' і ' + nick(ctx, pair[1]) + ' — '
          + (!ctx.mine ? 'дивишся збоку' : knockedOut(ctx, s) ? 'ти вибув, вболівай' : 'твоя пара попереду, вболівай');
    } else if (phase === 'ready' && s.sig && sigOf(s) !== 'word') {
      msg = SIGS[sigOf(s)].hint + (sigOf(s) === 'bell' && !Snd.on ? ' (звук вимкнено — покажемо 🔔)' : '');
    } else {
      msg = ctx.ui.coarse() ? 'Стріляй тапом по вулиці або кнопкою — щойно побачиш «ВОГОНЬ!»' : 'Стріляй будь-якою клавішею, кліком по вулиці або кнопкою';
    }
    putText(st.els.msg, msg);

    // Шкала раунду — поки видно підсумок; наприкінці — ще й середня реакція.
    let scale = '';
    if (ended && l) {
      const rows = [0, 1].map((i) => ({
        n: nick(ctx, seatOf(st, i)),
        ms: l.ms ? l.ms[i] : null,
        fs: (l.reason === 'false' && l.winner !== i) || l.reason === 'both-false',
        me: i === side,
        win: l.winner === i,
        nr: l.nr ? l.nr[i] : null,
      }));
      const everyone = st.cup ? [0, 1, 2, 3, 4, 5, 6, 7].filter((i) => s.avg && s.avg[i] != null) : [0, 1];
      scale = scaleHtml(ctx, rows, phase === 'done' ? avgText(ctx, s, everyone) : '', st.els.scale.clientWidth || st.els.scene.clientWidth);
    }
    putHtml(st.els.scale, scale);
    putText(st.els.rec, recText(ctx, s, st.cup ? pair || [] : [0, 1], pair || []));
    if (st.els.cup) putHtml(st.els.cup, ctx.playing || phase === 'done' ? cupHtml(ctx, s) : '');

    const canFire = side >= 0 && ctx.playing && phase !== 'result' && phase !== 'done' && phase !== 'wait';
    // Дуель зіграно — кнопку ховаємо: під нею «Ще раз», і велика неактивна «Стріляти» тільки плутає.
    // У турнірі кнопка — лише в пари, що зараз стріляється.
    st.els.fire.hidden = !ctx.mine || phase === 'done' || (st.cup && ctx.playing && side < 0);
    if (st.els.fire.disabled !== !canFire) st.els.fire.disabled = !canFire;
  }

  /// Спільне для дуелі й турніру: кадр → стан, відлуння для заміру пінгу, обманка, перемалювати.
  function onFrame(root, ctx, f) {
    const st = state(root, ctx);
    if (!st.els || !f || !f.phase) return;
    // Відлуння — першим ділом: сервер міряє дорогу кадру туди й назад, зайва робота до нього — зайві мілісекунди.
    if (f.fid != null && f.fid !== st.fid) {
      st.fid = f.fid;
      if (ctx.mine && ctx.playing) ctx.input('pong', { f: f.fid });
    }
    st.last = merge(st.last, f);
    baitCheck(st, st.last, () => render(root, st.ctx || ctx));
    render(root, ctx);
  }

  function duelStatus(ctx, cup) {
    const st = states.get(ctx);
    const s = (st && st.last) || {};
    if (s.phase === 'done' || !ctx.playing) return '';   // підсумок партії каркас напише сам
    if (cup && st && mySide(st, ctx) < 0 && s.pair) {
      return 'Стріляються ' + nick(ctx, s.pair[0]) + ' і ' + nick(ctx, s.pair[1]);
    }
    if (s.phase === 'ready') return s.sig && sigOf(s) !== 'word' ? 'Готуйсь… ' + SIGS[sigOf(s)].badge : 'Готуйсь…';
    if (s.phase === 'aim') return 'Цілься…';
    if (s.phase === 'fire') return sigOf(s) === 'word' ? 'ВОГОНЬ! Тисни!' : 'Цілься…';
    // Хто скільки мілісекунд — уже написано під сценою; тут коротко, щоб не читати те саме двічі.
    if (s.phase === 'result') {
      const l = s.last;
      if (l && l.winner != null) {
        const need = cup ? s.need || 2 : 3;               // Duel.WinsNeeded
        const last = (s.wins || []).some((w) => w >= need);
        return 'Раунд бере ' + nameOf(ctx, cup && st ? seatOf(st, l.winner) : l.winner)
          + (last ? (cup ? ' — і матч!' : ' — і всю дуель!') : ' — мить, і наступний');
      }
      return l ? 'Раунд переграють' : '';
    }
    return '';
  }

  function duelKey(e, ctx) {
    if (e.repeat || NOT_A_TRIGGER.has(e.key) || /^F\d{1,2}$/.test(e.key)) return false;
    const st = states.get(ctx);
    if (!st) return false;
    const phase = (st.last && st.last.phase) || '';
    if (!fighter(st, ctx) || phase === 'result' || phase === 'done' || !phase) return false;
    shoot(st, ctx);
    return true;
  }

  function duelUnmount(root, ctx) {
    const st = states.get(ctx);
    if (st) { clearTimeout(st.timer); clearTimeout(st.baitTimer); }
    states.delete(ctx);
  }

  HGames.register({
    id: 'duel',
    icon: ICON,
    seatNames: ['шериф', 'бандит'],
    seatClass: ['x', 'o'],
    pad: { a: 'Space', anyBtn: true, hint: '{a} стріляти (будь-яка кнопка) — щойно побачиш сигнал' },
    news: {
      v: '2026-09-29',
      title: 'Дуель: рекорди, шкала реакцій і обманки',
      items: [
        '🏆 Найшвидша рука тижня й твій рекорд — під сценою, а побитий рекорд одразу вискакує «🏅 Новий рекорд: 173 мс!»',
        '📏 Шкала раунду «⚡ → Оля 231 → Петро 305», а наприкінці — середня реакція кожного',
        '🐦 Опція «Обманки»: у «Цілься…» інколи кричать «ВОДА!» чи «ВОРОН!» — хто стрельнув, той поспішив',
        '🔔 Опція «Сигнал: різний» — раунд на дзвін, на сонце чи на червоне небо; 📶 опція «Пінг» зрівнює Wi-Fi з мобільним',
        '🤠 Новий «Турнір стрільців» на 3–8: пари дуелять по черзі, переможець — шериф вечора',
      ],
    },

    mount(root, ctx) {
      take(build(root, ctx), ctx);
      render(root, ctx);
    },

    update(root, ctx) {
      const st = build(root, ctx);
      take(st, ctx);
      render(root, ctx);
      fitPhone(st, ctx);
    },

    frame: onFrame,

    /// Стріляють будь-якою клавішею: у вестерні ніхто не шукає пробіл. Каркас уже відсіяв поля вводу
    /// й комбінації з Ctrl/Alt/Cmd, тож сюди доходить саме те, чим можна тиснути на гачок.
    onKey: duelKey,

    status(ctx) { return duelStatus(ctx, false); },

    unmount: duelUnmount,
  });

  // Турнір стрільців (duelcup, п. 66): та сама вулиця й той самий поєдинок, лише пари міняються за сіткою.
  HGames.register({
    id: 'duelcup',
    added: '2026-09-29',
    icon: '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
      + '<path d="M4 2h8v2.4a4 4 0 0 1-8 0z" fill="var(--accent)"/><path d="M2 3h2v2.2A2 2 0 0 1 2 3zm12 0h-2v2.2A2 2 0 0 0 14 3z" fill="var(--clay)"/>'
      + '<rect x="7" y="8" width="2" height="3.4" fill="var(--accent)"/><rect x="4.6" y="11.4" width="6.8" height="2.4" rx=".8" fill="var(--clay)"/></svg>',
    seatClass: ['x', 'o', 'c', 'db', 'x', 'o', 'c', 'db'],
    pad: { a: 'Space', anyBtn: true, hint: '{a} стріляти (будь-яка кнопка), коли твоя пара на вулиці' },

    mount(root, ctx) {
      state(root, ctx).cup = true;
      take(build(root, ctx), ctx);
      render(root, ctx);
    },

    update(root, ctx) {
      state(root, ctx).cup = true;
      const st = build(root, ctx);
      take(st, ctx);
      render(root, ctx);
      fitPhone(st, ctx);
    },

    frame: onFrame,
    onKey: duelKey,
    status(ctx) { return duelStatus(ctx, true); },
    unmount: duelUnmount,
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
      st = { root: root, last: null, seen: null, timer: 0, els: null, key: '', prev: null, baitN: null, baitUntil: 0, baitTimer: 0 };
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
    wrap.innerHTML = '<div class="dtop"><span class="sboard"></span><span class="duround muted small"></span></div>'
      + '<div class="dscene" data-phase="wait">'
      + '<div class="dsky"></div><div class="dsun"></div><div class="dstreet"></div><div class="dtumble"></div>'
      + '<div class="sguys"></div>'
      + '<svg class="slines" viewBox="0 0 100 ' + VB_H + '" preserveAspectRatio="none" aria-hidden="true"></svg>'
      + '<div class="dsig"></div><div class="dcall"></div><div class="dflash"></div></div>'
      + '<div class="dmsg small"></div>'
      + '<button type="button" class="primary dfire">🔫 Стріляти</button>'
      // Шкала — під кнопкою: з'являється лише між раундами, і кнопка під пальцем не має стрибати.
      + '<div class="dscale small"></div>'
      + '<div class="drec muted small"></div>';
    root.appendChild(wrap);
    soundBtn(wrap);
    st.els = {
      wrap: wrap,
      sig: wrap.querySelector('.dsig'),
      scale: wrap.querySelector('.dscale'),
      rec: wrap.querySelector('.drec'),
      board: wrap.querySelector('.sboard'),
      round: wrap.querySelector('.duround'),
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
    trigger(st.els.fire, () => sShoot(st, st.ctx || ctx));
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
    putHtml(st.els.lines, html);
  }

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
    for (let i = 0; i < 4; i++) {
      if (s.fs && s.fs[i]) parts.push(nick(ctx, i) + (l.bait ? ' стрельнув на «' + l.bait + '»' : ' — куля в небо'));
    }
    let head;
    if (l.reason === 'points') {
      // Очки (п. 173): «+2 Ігор ⚡ · +1 Петро» — найшвидше влучання з блискавкою.
      const got = [0, 1, 2, 3].filter((i) => l.gain && l.gain[i] > 0).sort((a, b) => l.gain[b] - l.gain[a]);
      head = got.length ? got.map((i) => '+' + l.gain[i] + ' ' + nick(ctx, i) + (l.fast === i ? ' ⚡' : '')).join(', ') : 'Ніхто не влучив';
    } else {
      head = l.reason === 'last' && l.winner != null ? 'Раунд бере ' + nick(ctx, l.winner) + '!'
        : l.reason === 'sleep' ? 'Ніхто не вистрілив — раунд переграють'
          : 'На ногах лишились кілька — раунд нічий';
    }
    return head + (parts.length ? ' · ' + parts.join(' · ') : '');
  }

  /// Звуки на зміну кадру: дзвін на «ВОГОНЬ!», постріл на кожен новий постріл, свист на фальстарт.
  function sSounds(st, ctx, s) {
    const p = st.prev;
    st.prev = s;
    if (!p || !s || !st.els.scene.offsetParent) return;
    // Дзвін на «ВОГОНЬ!» грає fireCue разом зі спалахом (і лише для сигналів «напис» і «дзвін»).
    if (s.phase === 'result' && p.phase !== 'result' && s.last) recToasts(ctx, s.last.nr, (i) => i, (i) => s.shot[i]);
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
      + S_SHAPES[i] + '</b> ' + ctx.esc(nick(ctx, i)) + ' <em>' + (wins[i] || 0) + '</em></span>').join('')
      + (s.pts && phase !== 'wait' ? '<span class="sb muted">очки</span>' : '');
    putHtml(st.els.board, board);
    const parts = [];
    if (phase === 'done') parts.push('перестрілку зіграно');
    else if (phase !== 'wait') parts.push('раунд ' + (s.round || 1) + ' · ' + (s.pts ? 'до ' + (s.target || 7) + ' очок' : 'до ' + (s.target || 3) + ' перемог'));
    const best = ctx.mine && s.best ? s.best[ctx.seat] : null;
    if (best != null) parts.push('твоя найшвидша ' + best + ' мс');
    const round = parts.join(' · ');
    if (st.els.round.textContent !== round) st.els.round.textContent = round;

    sigBadge(st, s, phase);
    if (st.els.scene.dataset.phase !== phase) {
      st.els.scene.dataset.phase = phase;
      if (phase === 'fire' && st.els.scene.offsetParent) fireCue(st, s);
    }
    const call = callText(st, s, phase);
    putText(st.els.call, call);
    st.els.call.classList.toggle('big', phase === 'fire' && call === CALL.fire || (phase === 'aim' && call !== CALL.aim && !!call));

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
    else if (s.fs && s.fs[ctx.seat]) msg = 'Зарано! Куля в небо, патрона до кінця раунду нема. Сподівайся, що в тебе не влучать';
    else if (s.shot && s.shot[ctx.seat] != null) msg = 'Патрон витрачено — дивись, хто кого';
    else if (phase === 'ready' && s.sig && sigOf(s) !== 'word') msg = SIGS[sigOf(s)].hint;
    else {
      const t = s.aim ? s.aim[ctx.seat] : null;
      msg = (t != null ? 'Ціль: ' + nick(ctx, t) + '. ' : '')
        + (ctx.ui.coarse() ? 'Змінити — тап по фігурі; на ВОГОНЬ тисни «Стріляти»'
          : 'Змінити — тап по фігурі або ← →; на ВОГОНЬ стріляй кнопкою чи будь-якою іншою клавішею');
    }
    if (st.els.msg.textContent !== msg) st.els.msg.textContent = msg;

    // Шкала раунду (п. 68) — поки видно підсумок; наприкінці — середня реакція; під кнопкою — рекорди (п. 64).
    let scale = '';
    if (ended && s.last) {
      const rows = seats.map((i) => ({
        n: nick(ctx, i), ms: s.shot ? s.shot[i] : null, fs: !!(s.fs && s.fs[i]), me: ctx.mine && i === ctx.seat,
        win: s.last.winner === i || s.last.fast === i, nr: s.last.nr ? s.last.nr[i] : null,
      }));
      scale = scaleHtml(ctx, rows, phase === 'done' ? avgText(ctx, s, seats) : '', st.els.scale.clientWidth || st.els.scene.clientWidth);
    }
    putHtml(st.els.scale, scale);
    putText(st.els.rec, recText(ctx, s, [0, 1, 2, 3], []));

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
      v: '2026-09-29',
      title: 'Перестрілка: очки за влучання',
      items: [
        '🎯 Тепер рахунок — очки: +1 за влучання, ще +1 найшвидшому, партія до 7 (старі «раунди» — опцією столу)',
        '📏 Під сценою — шкала реакцій раунду, наприкінці — середня реакція кожного',
        '🏆 Найшвидша рука тижня й твій рекорд — спільні з Дуеллю',
        '🐦 Опції «Обманки» й «Сигнал: різний» — як у Дуелі',
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
      fitPhone(st, ctx);
    },

    frame(root, ctx, f) {
      const st = sState(root, ctx);
      if (!st.els || !f || !f.phase) return;
      st.last = merge(st.last, f);
      sSounds(st, ctx, st.last);
      baitCheck(st, st.last, () => sRender(root, st.ctx || ctx));
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
      if (s.phase === 'ready') return 'Готуйсь… обери ціль' + (s.sig && sigOf(s) !== 'word' ? ' · ' + SIGS[sigOf(s)].badge : '');
      if (s.phase === 'aim') return 'Цілься…';
      if (s.phase === 'fire') return sigOf(s) === 'word' ? 'ВОГОНЬ!' : 'Цілься…';
      if (s.phase === 'result' && s.last) {
        const l = s.last;
        if (l.reason === 'points') return l.fast != null ? 'Найшвидше влучання — ' + nick(ctx, l.fast) + ' ⚡' : 'Ніхто не влучив';
        return l.reason === 'last' && l.winner != null ? 'Раунд бере ' + nick(ctx, l.winner) : l.reason === 'sleep' ? 'Ніхто не вистрілив' : 'Раунд нічий';
      }
      return '';
    },

    unmount(root, ctx) {
      const st = sstates.get(ctx);
      if (st) { clearTimeout(st.timer); clearTimeout(st.baitTimer); }
      sstates.delete(ctx);
    },
  });
})();
