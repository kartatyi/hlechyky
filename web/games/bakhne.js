/*
  Куди бахне — пам'ять у ритмі (Impl/Bakhne.cs; spec docs/games/specs/bakhne.md).

  Дядько Глек на даху показує стрілки, потім «Пішли!» — і на кожен такт ступаєш один крок у своєму дворі 3×3.
  Після такту Глек кидає горщики з жаром на всі плитки, крім правильної.

  Кадр (spec §4.2): { t, ph (0 ready · 1 show · 2 go · 3 steps · 4 end · 5 over · 6 lobby), round, len, trick,
    left, at, ar, bt, beat, done, a: [[напрямок, червона]], need: [напрямок], safe: [x, y] | null,
    p: [8 × [x, y, серця, fl] | null] (fl: 1 живий · 2 влучило останнім бахом · 4 крок у цьому такті),
    ev: [[id, kind, a, b]] }.  Напрямки як у HGames.ui.dpad: 0 → · 1 ↓ · 2 ← · 3 ↑.
  Ввід: Input('step', { d }).

  Поле — DOM, а не канвас: дев'ять плиток на двір, вісім дворів — цього мало, щоб платити за власне малювання,
  зате анімації (горщики, дим, тіні) — CSS, і рядок не перемальовується без потреби. Годинник такту — свій:
  тик сервера з кадру + час, що минув відтоді (кадри щонайменше раз на 200 мс), тож тіні горщиків ростуть плавно.
  Свій крок малюємо одразу (передбачення від p[seat]); кадр із нашою подією step/fence чи бах його замінює правдою.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1" y="7" width="14" height="8" rx="1.2" fill="var(--bk-yard, #3d5c3f)"/>'
    + '<path d="M5.7 7v8M10.3 7v8M1 10.6h14" stroke="var(--bg)" stroke-width=".8"/>'
    + '<path d="M6.4 1.4h3.2l-.4 1.2c1.6.6 2.4 1.8 2.4 3.1 0 1.8-1.5 2.9-3.6 2.9S4.4 7.5 4.4 5.7c0-1.3.8-2.5 2.4-3.1Z" fill="var(--clay)"/>'
    + '<path d="M8 9.8 9.3 11l1.7-.2-.8 1.5.8 1.5-1.7-.3L8 14.6 6.7 13.5l-1.7.3.8-1.5-.8-1.5 1.7.2Z" fill="var(--accent)"/></svg>';

  const DX = [1, 0, -1, 0], DY = [0, 1, 0, -1];
  const ROT = [0, 90, 180, 270];
  const NAMES = ['синій', 'рудий', 'зелений', 'жовтий', 'бузковий', 'м’ятний', 'рожевий', 'сірий'];
  const TRICK = {
    1: { icon: '🥴', short: 'Глек напився', long: 'Глек напився — ступай НАВПАКИ кожну стрілку' },
    2: { icon: '🔴', short: 'червоні стрілки', long: 'Червона стрілка — ступай у протилежний бік' },
    3: { icon: '⚡', short: 'швидкий показ', long: 'Швидкий показ — дивись уважно' },
  };
  const KEYS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };

  const live = new Set();
  const stOf = (ctx) => [...live].find((s) => s.ctx === ctx) || null;
  const padOn = () => !!(window.HPad && HPad.pads > 0);
  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  /// 1 крок · 2 кроки · 5 кроків
  const plural = (n, one, few, many) => {
    const d = n % 10, h = n % 100;
    return n + ' ' + (d === 1 && h !== 11 ? one : d >= 2 && d <= 4 && (h < 12 || h > 14) ? few : many);
  };

  const arrowSvg = (d, cls) => '<svg class="bk-arr' + (cls ? ' ' + cls : '') + '" viewBox="0 0 24 24" style="--r:' + ROT[d] + 'deg" aria-hidden="true">'
    + '<path d="M2.5 9.4h10.2V4.2L21.6 12l-8.9 7.8v-5.2H2.5Z"/></svg>';
  const ARIA = ['праворуч', 'вниз', 'ліворуч', 'вгору'];

  /// Дядько Глек: глек з вусами. Клас drunk — хитається й червоніє носом, throw — підстрибує на баху.
  const GLEK = '<svg class="bk-glek" viewBox="0 0 64 64" aria-hidden="true">'
    + '<path class="bk-g-body" d="M24 6h16l-2 5c7 3 12 9 12 18 0 14-8 27-18 27S14 43 14 29c0-9 5-15 12-18Z"/>'
    + '<path class="bk-g-rim" d="M22 4.5h20v4H22Z"/>'
    + '<path class="bk-g-handle" d="M49 22c6 0 8 4 7 9s-5 8-9 8" fill="none" stroke-width="3.2"/>'
    + '<circle class="bk-g-eye" cx="27" cy="26" r="2.6"/><circle class="bk-g-eye" cx="37" cy="26" r="2.6"/>'
    + '<circle class="bk-g-nose" cx="32" cy="31" r="2.8"/>'
    + '<path class="bk-g-moust" d="M32 34c-3-2-8-2-11 2 3-1 6 0 8 1 1 .6 2 .6 3-.4 1 1 2 1 3 .4 2-1 5-2 8-1-3-4-8-4-11-2Z"/>'
    + '<path class="bk-g-band" d="M17 44c9 4 21 4 30 0" fill="none" stroke-width="2.4"/></svg>';

  // ---------- стан ----------
  function state(root, ctx) {
    let st = root._bakhne;
    if (!st) {
      st = root._bakhne = {
        root, el: null, yards: new Map(), yardKey: '', f: null, off: null, tickMs: 50, grace: 3,
        evId: -1, evSeen: false, pred: null, stepBeat: -99, raf: 0, timers: new Set(), banner: '', bannerKey: '',
        fitS: null, layoutW: 0, flashN: 0, slotsKey: '', hudKey: '', swipe: null,
      };
    }
    st.ctx = ctx;
    live.add(st);
    return st;
  }

  const later = (st, ms, fn) => {
    const id = setTimeout(() => { st.timers.delete(id); fn(); }, ms);
    st.timers.add(id);
  };

  /// Тик сервера «зараз»: тик останнього кадру + час відтоді. Зсув згладжуємо до найранішого приходу кадру —
  /// запізнілий кадр не тягне годинник назад.
  function takeClock(st, f) {
    const now = performance.now() / st.tickMs;
    const off = f.t - now;
    if (st.off == null || Math.abs(off - st.off) > 8) st.off = off;
    else st.off = off > st.off ? off : st.off + (off - st.off) * 0.15;
  }
  const tNow = (st) => (st.off == null ? (st.f ? st.f.t : 0) : performance.now() / st.tickMs + st.off);

  const nameAt = (ctx, i) => (ctx.nameOf && ctx.nameOf(i)) || (ctx.nickOf && ctx.nickOf(i)) || null;
  function nameOf(st, i) {
    const v = st.ctx.view || {};
    return nameAt(st.ctx, i) || ((v.bot || []).includes(i) ? '🤖 бот' : (st.ctx.seatName ? st.ctx.seatName(i) : NAMES[i]));
  }

  /// Хто грає: місця з фішкою в кадрі.
  const seatsOf = (f) => (f && f.p ? f.p.map((q, i) => (q ? i : -1)).filter((i) => i >= 0) : []);
  const mySeat = (st) => {
    const c = st.ctx;
    return c.mine && c.seat != null && st.f && st.f.p && st.f.p[c.seat] ? c.seat : null;
  };

  // ---------- каркас DOM ----------
  function shell(root, st) {
    if (st.el && st.el.isConnected) return;
    root.innerHTML = '<div class="bk">'
      + '<div class="bk-roof"><div class="bk-glekbox">' + GLEK + '<i class="bk-pot0"></i></div>'
      + '<div class="bk-bubble"><div class="bk-big"></div></div>'
      + '<div class="bk-info"><div class="bk-round"></div><div class="bk-trick"></div></div></div>'
      + '<div class="bk-slots" aria-live="polite"></div>'
      + '<div class="bk-beat"><i></i></div>'
      + '<div class="bk-stage"><div class="bk-field"><div class="bk-main"></div><div class="bk-others"></div></div>'
      + '<div class="bk-howto" hidden></div><div class="bk-banner" hidden></div></div>'
      + '<div class="bk-pad"></div></div>';
    st.el = root.firstChild;
    const q = (s) => st.el.querySelector(s);
    st.roof = q('.bk-roof'); st.glek = q('.bk-glekbox'); st.bubble = q('.bk-bubble'); st.big = q('.bk-big');
    st.roundEl = q('.bk-round'); st.trickEl = q('.bk-trick'); st.slots = q('.bk-slots'); st.beatEl = q('.bk-beat');
    st.beatBar = q('.bk-beat i'); st.stage = q('.bk-stage'); st.field = q('.bk-field'); st.main = q('.bk-main');
    st.others = q('.bk-others'); st.howto = q('.bk-howto'); st.bannerEl = q('.bk-banner'); st.padHost = q('.bk-pad');
    st.yards.clear();
    st.yardKey = '';
    st.slotsKey = '';
    st.hudKey = '';
    wireInput(st);
  }

  function yardHtml(i, big) {
    let tiles = '';
    for (let k = 0; k < 9; k++) tiles += '<div class="bk-tile" data-k="' + k + '"><i class="bk-sh"></i></div>';
    return '<div class="bk-yard bk' + i + (big ? ' big' : '') + '" data-seat="' + i + '">'
      + '<div class="bk-ytop"><b class="bk-yname"></b><span class="bk-hearts"></span></div>'
      + '<div class="bk-ground">' + tiles + '<div class="bk-tok"><span class="bk-face"></span></div><div class="bk-oi">ой!</div>'
      + '<div class="bk-gone"></div></div></div>';
  }

  /// Двори: свій — великий (main), решта — мініатюри; глядач бачить усіх однаково. Перебудова лише коли змінився
  /// склад чи «чий великий».
  function buildYards(st) {
    const f = st.f;
    const seats = seatsOf(f);
    const me = mySeat(st);
    const key = seats.join(',') + '|' + me;
    if (key === st.yardKey) return;
    st.yardKey = key;
    st.yards.clear();
    st.main.innerHTML = me != null ? yardHtml(me, true) : '';
    st.others.innerHTML = seats.filter((i) => i !== me).map((i) => yardHtml(i, false)).join('');
    st.field.classList.toggle('watch', me == null);
    st.el.querySelectorAll('.bk-yard').forEach((y) => {
      const i = +y.dataset.seat;
      st.yards.set(i, {
        el: y, ground: y.querySelector('.bk-ground'), tiles: [...y.querySelectorAll('.bk-tile')], tok: y.querySelector('.bk-tok'),
        face: y.querySelector('.bk-face'), name: y.querySelector('.bk-yname'), hearts: y.querySelector('.bk-hearts'),
        oi: y.querySelector('.bk-oi'), gone: y.querySelector('.bk-gone'), nameK: '', heartsK: '', posK: '',
      });
    });
    st.layoutW = 0;
    layout(st);
  }

  /// Розміри дворів під root (не вікно: у вечірці модуль живе в чужій картці) і висоту екрана.
  function layout(st, force) {
    const w = Math.floor(st.root.clientWidth || st.el.clientWidth || 360);
    let fit = null;
    try { fit = HGames.ui.fit ? HGames.ui.fit() : null; } catch { fit = null; }
    const avail = fit ? fit.h - fit.top - fit.dock : (window.innerHeight || 700);
    const key = w + '|' + avail + '|' + st.yardKey + '|' + HGames.ui.coarse();
    if (!force && key === st.layoutK) return;
    st.layoutK = key;
    const me = mySeat(st);
    const n = st.yards.size - (me != null ? 1 : 0);
    const coarse = HGames.ui.coarse();
    const wide = w >= 620;
    st.el.classList.toggle('wide', wide);
    let big = 0, mini;
    if (me != null) {
      if (wide) {
        big = clamp(Math.min(360, avail - 210), 220, 360);
        const right = w - big - 28;
        const cols = n <= 1 ? 1 : n <= 4 ? 2 : n <= 6 ? 3 : 4;
        const rows = Math.ceil(n / cols) || 1;
        mini = clamp(Math.min(Math.floor((right - (cols - 1) * 10) / cols), Math.floor((big + 30) / rows) - 34), 64, 150);
      } else {
        mini = n ? clamp(Math.floor((w - 6 * (n - 1)) / n), 40, 84) : 0;
        const extra = 168 + (n ? mini + 26 : 0) + (coarse ? 128 : 0);
        big = clamp(Math.min(w - 12, 340, avail - extra), 170, 340);
      }
    } else {
      const cols = n <= 1 ? 1 : n <= 2 ? 2 : wide ? (n <= 4 ? n : 4) : (n <= 4 ? 2 : 3);
      mini = clamp(Math.floor((w - (cols - 1) * 10) / cols) - 4, 70, wide ? 200 : 150);
    }
    st.yards.forEach((y, i) => {
      const s = i === me ? big : mini;
      y.el.style.setProperty('--s', s + 'px');
      y.el.classList.toggle('tiny', s < 64);
    });
  }

  // ---------- кадр → DOM ----------
  function render(st) {
    const ctx = st.ctx;
    const v = ctx.view || {};
    const f = st.f;
    if (!st.el) return;
    const lobby = !f || f.ph === 6 || (ctx.room && ctx.room.status === 'lobby');
    st.el.classList.toggle('lobby', lobby);
    st.howto.hidden = !lobby;
    if (lobby) howto(st);
    if (!f) return;
    buildYards(st);
    st.el.classList.toggle('drunk', f.trick === 1 && f.ph !== 6);
    st.el.classList.toggle('ph-steps', f.ph === 3);
    st.el.classList.toggle('ph-show', f.ph === 1);
    hud(st, v, f);
    slots(st, f);
    bubble(st, f);
    const me = mySeat(st);
    st.yards.forEach((y, i) => {
      const q = f.p[i];
      if (!q) return;
      const nm = nameOf(st, i);
      if (nm !== y.nameK) {
        y.nameK = nm;
        y.name.textContent = nm;
        // на фішці — перша літера ніка («гість Оля» → О), у бота — 🤖
        const bot = (v.bot || []).includes(i) || /^(🤖|бот\s)/.test(nm || '');
        y.face.textContent = bot ? '🤖' : (String(nm || '?').replace(/^гість\s+/, '').trim()[0] || '?').toUpperCase();
      }
      const max = v.heartsMax || (v.party ? 1 : 2);
      const out = v.out && v.out[i] != null;
      const hk = q[2] + '|' + max + '|' + out;
      if (hk !== y.heartsK) {
        y.heartsK = hk;
        y.hearts.innerHTML = out ? '<span class="bk-dead">💥 раунд ' + v.out[i] + '</span>'
          : '<span class="bk-h">' + '❤'.repeat(Math.max(0, q[2])) + '</span><span class="bk-h0">' + '❤'.repeat(Math.max(0, max - q[2])) + '</span>';
      }
      const alive = !!(q[3] & 1);
      y.el.classList.toggle('out', !alive && f.ph !== 6);
      y.el.classList.toggle('won', f.ph === 5 && (v.winners || []).includes(i));
      let x = q[0], yy = q[1];
      if (i === me && st.pred) { x = st.pred.x; yy = st.pred.y; }
      const pk = x + ',' + yy;
      if (pk !== y.posK) {
        y.posK = pk;
        y.tok.style.setProperty('--x', x);
        y.tok.style.setProperty('--y', yy);
      }
    });
    if (me != null) {
      const q = f.p[me];
      const stepped = st.pred || (q && (q[3] & 4));
      st.field.classList.toggle('stepped', !!stepped && (f.ph === 2 || f.ph === 3));
    }
    controls(st);
    banners(st, v, f);
  }

  function howto(st) {
    if (st.howto.dataset.on) return;
    st.howto.dataset.on = '1';
    const demo = '<div class="bk-demo"><div class="bk-ground">' + '<div class="bk-tile"></div>'.repeat(9)
      + '<div class="bk-tok bk-demotok"></div></div></div>';
    st.howto.innerHTML = demo + '<ol>'
      + '<li><b>Глек показує стрілки</b> — ' + arrowSvg(3) + arrowSvg(0) + arrowSvg(1) + ' запам\'ятай порядок</li>'
      + '<li><b>«Пішли!»</b> — на кожен такт <b>один крок</b> у своєму дворі (стрілки/WASD, хрестовина, свайп чи дотик плитки)</li>'
      + '<li>Після такту <b>бахає по всіх плитках, крім правильної</b> 🏺💥 — два серця, виграє останній живий</li>'
      + '<li>З 4-го раунду підступи: ' + arrowSvg(0, 'red') + ' червона стрілка й 🥴 «Глек напився» — <b>навпаки</b></li></ol>';
  }

  function hud(st, v, f) {
    const tr = TRICK[f.trick];
    const max = v.roundsMax || 12;
    const k = f.ph + '|' + f.round + '|' + f.trick + '|' + max + '|' + (v.party ? 1 : 0);
    if (k === st.hudKey) return;
    st.hudKey = k;
    st.roundEl.textContent = f.ph === 6 ? 'Куди бахне' : 'Раунд ' + (f.round || 1) + ' / ' + max + (f.len ? ' · ' + plural(f.len, 'крок', 'кроки', 'кроків') : '');
    st.trickEl.innerHTML = tr && f.ph !== 6 ? '<span class="bk-tr t' + f.trick + '">' + tr.icon + ' ' + tr.long + '</span>' : '';
  }

  /// Смужка стрілок: у показі — лише остання видна, попередні — крапки (пам'ять!); у тактах — бахнуті такти
  /// відкривають правильний крок, поточний світиться; у кінці раунду — уся послідовність.
  function slots(st, f) {
    const len = f.len || 0;
    const a = f.a || [], need = f.need || [];
    const ph = f.ph;
    const cur = ph === 3 ? f.beat : ph === 2 ? 0 : -1;
    const key = ph + '|' + len + '|' + a.length + '|' + need.length + '|' + cur + '|' + f.round;
    if (key === st.slotsKey) return;
    st.slotsKey = key;
    if (ph === 6 || ph === 0 || !len) { st.slots.innerHTML = ''; return; }
    let h = '';
    for (let i = 0; i < len; i++) {
      let cls = 'bk-slot', inner = '';
      if (ph === 1) {
        if (i < a.length - 1) { cls += ' seen'; inner = '<i></i>'; }
        else if (i === a.length - 1) { cls += ' now' + (a[i][1] ? ' red' : ''); inner = arrowSvg(a[i][0]); }
      } else if (ph === 2 || ph === 3) {
        if (i < need.length) { cls += ' done'; inner = arrowSvg(need[i]); }
        else if (i === cur) { cls += ' cur'; inner = '<b>?</b>'; }
      } else {
        const shown = a[i], right = need[i];
        if (shown) {
          cls += ' all' + (shown[1] ? ' red' : '');
          inner = arrowSvg(shown[0]) + (right != null && right !== shown[0] ? '<em>' + arrowSvg(right) + '</em>' : '');
        }
      }
      h += '<div class="' + cls + '">' + inner + '</div>';
    }
    st.slots.innerHTML = h;
    st.slots.style.setProperty('--n', len);
  }

  function bubble(st, f) {
    const a = f.a || [];
    let key, html, cls = '';
    if (f.ph === 1 && a.length) {
      const last = a[a.length - 1];
      key = 'a' + f.round + ':' + a.length;
      html = arrowSvg(last[0], last[1] ? 'red' : '');
      cls = last[1] ? 'red' : '';
    } else if (f.ph === 1) { key = 'w' + f.round; html = '<span class="bk-say">Дивись…</span>'; }
    else if (f.ph === 0) { key = 'r'; html = '<span class="bk-say">Готуйсь!</span>'; }
    else if (f.ph === 2) { key = 'g' + f.round; html = '<span class="bk-say go">Пішли!</span>'; }
    else if (f.ph === 3) { key = 's' + f.round + ':' + f.beat; html = '<span class="bk-say">Крок ' + (f.beat + 1) + '<small>/' + f.len + '</small></span>'; }
    else if (f.ph === 4) { key = 'e' + f.round; html = '<span class="bk-say">Ще раунд!</span>'; }
    else if (f.ph === 5) { key = 'o'; html = '<span class="bk-say">Борщ!</span>'; }
    else { key = 'l'; html = '<span class="bk-say">Хто пам\'ятає — той і їсть борщ</span>'; }
    if (key === st.bubbleK) return;
    st.bubbleK = key;
    st.big.innerHTML = html;
    st.bubble.className = 'bk-bubble ' + cls + (f.ph === 6 ? ' long' : '');
    restart(st.bubble, 'pop');
  }

  function restart(el, cls) {
    if (!el) return;
    el.classList.remove(cls);
    void el.offsetWidth;
    el.classList.add(cls);
  }

  /// Банер посеред двору: новий раунд з підступом, «Пішли!», «тебе накрило», підсумок.
  function banners(st, v, f) {
    const me = mySeat(st);
    let key = '', html = '';
    if (f.ph === 5) {
      const ws = v.winners || [];
      key = 'over' + ws.join(',');
      html = ws.length === 1 ? '🏆 ' + st.ctx.esc(nameOf(st, ws[0])) + (ws[0] === me ? ' — це ти!' : '')
        : ws.length ? '🤝 Поділили: ' + ws.map((i) => st.ctx.esc(nameOf(st, i))).join(', ') : 'Нікого не лишилось';
    } else if (f.ph === 1 && f.a && f.a.length <= 1 && f.trick) {
      key = 'tr' + f.round;
      html = TRICK[f.trick].icon + ' ' + TRICK[f.trick].long;
    } else if (f.ph === 0) {
      key = 'ready';
      html = 'Готуйсь — Глек лізе на дах';
    }
    if (key === st.bannerKey) return;
    st.bannerKey = key;
    if (!html) { st.bannerEl.hidden = true; return; }
    flash(st, html, f.ph === 5 ? 0 : 1600, f.ph === 5 ? 'over' : '');
  }

  function flash(st, html, ms, cls) {
    st.bannerEl.innerHTML = html;
    st.bannerEl.className = 'bk-banner ' + (cls || '');
    st.bannerEl.hidden = false;
    restart(st.bannerEl, 'in');
    const my = ++st.flashN;
    if (ms) later(st, ms, () => { if (st.flashN === my) st.bannerEl.hidden = true; });
  }

  // ---------- події кадру ----------
  function events(st, f) {
    const ev = f.ev || [];
    if (!st.evSeen) {
      // перший кадр після mount/F5: старі події не програємо
      st.evSeen = true;
      for (const e of ev) st.evId = Math.max(st.evId, e[0]);
      return;
    }
    if (ev.length && ev[ev.length - 1][0] < st.evId - 50) st.evId = -1;   // «Ще раз»: id почались заново
    const me = mySeat(st);
    const booms = [];
    for (const e of ev) {
      if (e[0] <= st.evId) continue;
      st.evId = e[0];
      const [, kind, a, b] = e;
      if (kind === 3) booms.push(b);
      else if (kind === 4) hit(st, a, a === me);
      else if (kind === 5) { if (a === me) later(st, 500, () => flash(st, '💥 Тебе накрило — дивись, як інші', 2200, 'bad')); }
      else if (kind === 6) {
        st.pred = null;
        st.stepBeat = -99;
        st.yards.forEach((y) => y.tiles.forEach((t) => t.classList.remove('scorch', 'boom', 'safe')));
        if (a > 1 && !b) flash(st, 'Раунд ' + a, 1100, '');
      } else if (kind === 2) { if (me != null) flash(st, 'Пішли!', 800, 'go'); }
      else if (kind === 8 || kind === 9) {
        if (a === me) st.pred = null;
        const y = st.yards.get(a);
        if (y && kind === 9) { y.el.style.setProperty('--fx', DX[b] * 6 + 'px'); y.el.style.setProperty('--fy', DY[b] * 6 + 'px'); restart(y.tok, 'bump'); }
        else if (y && a !== me) restart(y.tok, 'hop');
      } else if (kind === 7) st.pred = null;
    }
    if (booms.length) bang(st, booms[booms.length - 1]);
  }

  /// Бах: горщики на всі плитки всіх дворів, крім правильної; правильна спалахує зеленим.
  function bang(st, safe) {
    st.pred = null;
    const all = [];
    st.yards.forEach((y) => { if (!y.el.classList.contains('out')) y.tiles.forEach((t, k) => { t.classList.remove('boom', 'safe'); all.push([t, k]); }); });
    void st.field.offsetWidth;
    for (const [t, k] of all) {
      if (k === safe) t.classList.add('safe');
      else t.classList.add('boom', 'scorch');
    }
    restart(st.glek, 'throw');
    if (!reduced()) restart(st.stage, 'quake');
  }

  function hit(st, seat, mine) {
    const y = st.yards.get(seat);
    if (y) { restart(y.el, 'hit'); restart(y.oi, 'show'); }
    if (mine) restart(st.stage, 'ouch');
  }

  // ---------- ввід ----------
  function canStep(st) {
    const f = st.f, me = mySeat(st);
    if (me == null || !f || !st.ctx.playing) return false;
    const q = f.p[me];
    return !!(q && (q[3] & 1)) && (f.ph === 2 || f.ph === 3);
  }

  /// Крок: шлемо завжди (правду скаже сервер), а фішку рухаємо одразу, якщо такт явно наш.
  function step(st, d) {
    if (!canStep(st)) return;
    const f = st.f, me = mySeat(st);
    const t = tNow(st);
    const g = st.grace;
    let beat;
    if (f.ph === 2) {
      // на «Пішли!» сервер бере натиск лише в останні grace тиків відліку; раніше — «Зачекай»
      if (t < f.at + f.bt - g - 1) { restart(st.bubble, 'nope'); return; }
      beat = 0;
    } else {
      beat = Math.floor((t - f.at) / f.bt);
      const inGrace = t - (f.at + beat * f.bt) < g;
      if (beat >= f.len) return;
      if (st.stepBeat === beat && !inGrace) { restart(st.bubble, 'nope'); return; }
    }
    st.ctx.input('step', { d });
    st.stepBeat = beat;
    const q = f.p[me];
    const x0 = st.pred ? st.pred.x : q[0], y0 = st.pred ? st.pred.y : q[1];
    const x = x0 + DX[d], y = y0 + DY[d];
    const yd = st.yards.get(me);
    if (x < 0 || x > 2 || y < 0 || y > 2) {
      if (yd) { yd.el.style.setProperty('--fx', DX[d] * 6 + 'px'); yd.el.style.setProperty('--fy', DY[d] * 6 + 'px'); restart(yd.tok, 'bump'); }
      return;
    }
    st.pred = { x, y, at: performance.now() };
    if (yd) { yd.posK = ''; restart(yd.tok, 'hop'); }
    render(st);
  }

  function wireInput(st) {
    // дотик плитки поруч і свайп по своєму двору; мишкою — клік по сусідній плитці
    st.main.addEventListener('pointerdown', (e) => {
      if (!canStep(st)) return;
      st.swipe = { x: e.clientX, y: e.clientY, id: e.pointerId, done: false };
    });
    st.main.addEventListener('pointermove', (e) => {
      const s = st.swipe;
      if (!s || s.id !== e.pointerId || s.done) return;
      const dx = e.clientX - s.x, dy = e.clientY - s.y;
      if (Math.max(Math.abs(dx), Math.abs(dy)) < 26) return;
      s.done = true;
      step(st, Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3));
    });
    const end = (e) => {
      const s = st.swipe;
      st.swipe = null;
      if (!s || s.id !== e.pointerId || s.done || e.type !== 'pointerup') return;
      const t = e.target.closest && e.target.closest('.bk-tile');
      const me = mySeat(st);
      if (!t || me == null || !st.f) return;
      const k = +t.dataset.k, q = st.f.p[me];
      const x0 = st.pred ? st.pred.x : q[0], y0 = st.pred ? st.pred.y : q[1];
      const dx = (k % 3) - x0, dy = Math.floor(k / 3) - y0;
      if (Math.abs(dx) + Math.abs(dy) !== 1) return;
      step(st, dx === 1 ? 0 : dx === -1 ? 2 : dy === 1 ? 1 : 3);
    };
    st.main.addEventListener('pointerup', end);
    st.main.addEventListener('pointercancel', end);
  }

  function controls(st) {
    const on = canStep(st) || (st.ctx.mine && st.ctx.playing && st.f && st.f.ph <= 1 && mySeat(st) != null);
    const pad = on ? HGames.ui.dpad(st.padHost, (d) => step(st, d)) : null;
    if (!on) { const el = st.padHost.querySelector(':scope > .dpad'); if (el) el.remove(); }
    if (pad) pad.classList.toggle('dim', !canStep(st));
  }

  // ---------- цикл: тіні горщиків і смужка такту ----------
  function spin(st) {
    if (st.raf) return;
    const loop = () => {
      st.raf = 0;
      if (!st.el || !st.el.isConnected) return;
      const f = st.f;
      if (!f || !st.ctx.playing || f.ph === 5 || f.ph === 6) { st.field.style.setProperty('--pr', 0); st.beatBar.style.transform = 'scaleX(0)'; return; }
      st.raf = requestAnimationFrame(loop);
      if (st.pred && performance.now() - st.pred.at > 700) { st.pred = null; render(st); }
      const t = tNow(st);
      let pr = 0, bar = 0;
      if (f.ph === 3 && f.bt) {
        const k = Math.floor((t - f.at) / f.bt);
        const into = t - (f.at + k * f.bt);
        pr = clamp(into / (f.bt + st.grace), 0, 1);
        bar = clamp(into / f.bt, 0, 1);
        // тіні ростуть до самого баху; перші grace тиків нового такту ще догоряє минулий
        if (k > 0 && into < st.grace) pr = 1;
      } else if (f.ph === 2 && f.bt) {
        bar = clamp((t - f.at) / f.bt, 0, 1);
      } else if (f.ph === 1 && f.ar) {
        bar = clamp((t - f.at) / (f.ar * Math.max(1, f.len)), 0, 1);
      }
      st.field.style.setProperty('--pr', pr.toFixed(3));
      st.beatBar.style.transform = 'scaleX(' + bar.toFixed(3) + ')';
      st.beatEl.className = 'bk-beat ph' + f.ph;
    };
    st.raf = requestAnimationFrame(loop);
  }

  // ---------- статус ----------
  function statusText(ctx) {
    const st = stOf(ctx);
    const v = ctx.view || {};
    const f = (st && st.f) || ctx.frame || v.frame;
    if (!ctx.playing) {
      if (ctx.room && ctx.room.status === 'lobby') return v.botOffer ? 'Сам за столом? «🤖 + бот» — і граєш проти двох ботів' : 'Пам\'ять у ритмі на 1–8. Стартує господар';
      return '';
    }
    if (!f) return '';
    const tr = TRICK[f.trick];
    const how = padOn() ? 'хрестовина пада' : HGames.ui.coarse() ? 'хрестовина, свайп чи дотик плитки' : 'стрілки/WASD чи клік по сусідній плитці';
    const me = st ? mySeat(st) : null;
    if (me == null) return 'Дивишся збоку · раунд ' + (f.round || 1);
    const q = f.p && f.p[me];
    if (q && !(q[3] & 1)) return '💥 Тебе накрило — дивись, хто пам\'ятає краще';
    if (f.ph === 0) return 'Готуйсь — зараз Глек покаже стрілки';
    if (f.ph === 1) return 'Запам\'ятовуй ' + plural(f.len, 'стрілку', 'стрілки', 'стрілок') + (tr ? ' · ' + tr.icon + ' ' + tr.short : '');
    if (f.ph === 2) return 'Пішли! Перший крок — на такт · ' + how;
    if (f.ph === 3) return 'Крок ' + (f.beat + 1) + ' з ' + f.len + ' · ' + how + (f.trick === 1 ? ' · 🥴 навпаки!' : f.trick === 2 ? ' · 🔴 червона — навпаки' : '');
    if (f.ph === 4) return 'Раунд ' + f.round + ' позаду · далі довше';
    return '';
  }

  HGames.register({
    id: 'bakhne',
    added: '2026-10-06',
    icon: ICON,
    seatNames: NAMES,
    seatClass: ['bk0', 'bk1', 'bk2', 'bk3', 'bk4', 'bk5', 'bk6', 'bk7'],
    pad: {
      dirs: true,
      hint: '{dpad} крок у такт',
      when: (ctx) => !!(ctx.playing && ctx.mine),
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      shell(root, st);
      const v = ctx.view || {};
      if (v.tickMs) st.tickMs = v.tickMs;
      if (v.grace != null) st.grace = v.grace;
      if (v.frame) { st.f = v.frame; takeClock(st, v.frame); events(st, v.frame); }
      st.onResize = () => { layout(st); };
      window.addEventListener('resize', st.onResize);
      if (HGames.ui.onFit) HGames.ui.onFit(root, () => layout(st));
      render(st);
      layout(st, true);
      spin(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      shell(root, st);
      const v = ctx.view || {};
      if (v.tickMs) st.tickMs = v.tickMs;
      if (v.grace != null) st.grace = v.grace;
      const vf = v.frame;
      // вид — правда поза грою й на зміні фази; посеред гри кадри свіжіші
      if (vf && (!st.f || !ctx.playing || vf.t >= st.f.t || vf.t < st.f.t - 40)) {
        if (st.f && vf.t < st.f.t - 40) { st.evId = -1; st.evSeen = false; st.off = null; }
        st.f = vf;
        takeClock(st, vf);
        events(st, vf);
      }
      if (!ctx.playing) { st.pred = null; st.stepBeat = -99; }
      render(st);
      layout(st);
      spin(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!f) return;
      if (st.f && f.t < st.f.t - 40) { st.evId = -1; st.off = null; }
      else if (st.f && f.t < st.f.t) return;   // запізнілий кадр
      st.f = f;
      takeClock(st, f);
      events(st, f);
      render(st);
      spin(st);
    },

    onKey(e, ctx) {
      const st = stOf(ctx);
      if (!st || !ctx.mine || !ctx.playing) return false;
      const d = KEYS[e.code];
      if (d == null) return false;
      if (!e.repeat) step(st, d);
      return true;
    },

    status: statusText,

    unmount(root) {
      const st = root._bakhne;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      st.timers.forEach((id) => clearTimeout(id));
      st.timers.clear();
      if (st.onResize) window.removeEventListener('resize', st.onResize);
      if (HGames.ui.onFit) HGames.ui.onFit(root, null);
      live.delete(st);
      root._bakhne = null;
    },
  });
})();
