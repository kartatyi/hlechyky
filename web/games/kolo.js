/*
  Гончарне колесо (Азарт → 🎡 Рулетка). Один круг на весь сайт: Дядько Глек крутить за розкладом — ставки 10 с →
  крутиться 5 с → виплати 3 с. Ставлять на множники ×2 (миска) / ×3 (горщик) / ×6 (макітра) / ×30 (глек), на кілька
  одразу; «Тріснув!» — усі програли. Правила, гроші й сегмент — на сервері (Impl/Kolo*.cs, docs/games/specs/kolo.md);
  тут лише круг, кнопки й наміри.

  Вид: { phase: 'bets'|'spin'|'result'|'off', round, until, leftMs, phaseMs, hash, seed, wheel: int[125], picks,
         spin: { no, seg, x, leftMs, ms } | null, history: [{ round, seg, x }],
         players: [{ nick, seat, color, here, mine, total, bets: [{ pick, amount }] }], totals: [{ pick, amount, people }],
         onTable, last: { round, seg, x, hash, seed, staked, paid, big, results: [{ nick, color, staked, paid, net }] } | null,
         glek: { mood, say, seq }, me: { free, onTable, canClear, canRepeat, repeatCost, note } | null,
         limits: { min, max }, on }
  Наміри: act('bet', { pick, amount }), act('clear'), act('repeat').

  Час — від приходу виду (leftMs). Круг крутиться CSS-переходом transform (без rAF) і стає рівно на spin.seg під
  «пальцем майстра» вгорі. Звуку нема.
*/
(() => {
  'use strict';

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="8" cy="8" r="6.6" fill="none" stroke="var(--clay)" stroke-width="2.6" stroke-dasharray="2.1 1.05"/>'
    + '<circle cx="8" cy="8" r="4.2" fill="var(--clay)" opacity=".55"/>'
    + '<path d="M6.4 6.2h3.2v1c1 .5 1.4 1.4 1.3 2.3-.2 1.2-1.2 1.9-2.9 1.9s-2.7-.7-2.9-1.9c-.1-.9.3-1.8 1.3-2.3z" fill="var(--accent)"/>'
    + '<path d="M8 .4v2.4" stroke="var(--accent)" stroke-width="1.6" stroke-linecap="round"/></svg>';

  const PICKS = [2, 3, 6, 30];
  const NAME = { 2: 'миска', 3: 'горщик', 6: 'макітра', 30: 'глек', 0: 'тріснув!' };
  const SHOUT = { 2: 'Миска', 3: 'Горщик', 6: 'Макітра', 30: 'Глек удався!', 0: 'Тріснув!' };
  const COLV = { 2: '--ko-c2', 3: '--ko-c3', 6: '--ko-c6', 30: '--ko-c30', 0: '--ko-c0' };
  const COLF = { 2: '#3f6fb5', 3: '#3f9a5a', 6: '#e0a32e', 30: '#c8352b', 0: '#2a2420' };
  const QUICK = [10, 50, 100, 500];
  const SPIN_EASE = 'cubic-bezier(.12,.62,.16,1)';
  const SAY_MS = 4000;
  const BADGE = { idle: '👋', hurry: '⏳', call: '', dance: '💃', laugh: '😂', clap: '👏', rake: '', sigh: '😮‍💨', doze: '💤' };

  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const store = {
    get(k, d) { try { const v = localStorage.getItem('kolo_' + k); return v == null ? d : v; } catch { return d; } },
    set(k, v) { try { localStorage.setItem('kolo_' + k, String(v)); } catch { /* приватне вікно */ } },
  };
  const session = {
    get(k) { try { return sessionStorage.getItem(k); } catch { return null; } },
    set(k, v) { try { sessionStorage.setItem(k, v); } catch { /* і так добре */ } },
  };
  const fmtN = (n) => Math.round(+n || 0).toString().replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  const short = (n) => {
    n = +n || 0;
    if (n >= 100000) return Math.round(n / 1000) + 'к';
    if (n >= 10000) return (Math.round(n / 100) / 10).toString().replace('.', ',') + 'к';
    return String(n);
  };
  const signed = (n) => (n > 0 ? '+' + fmtN(n) : n < 0 ? '−' + fmtN(Math.abs(n)) : '0');
  const xLabel = (x) => (x ? '×' + x : '💥');
  const mod = (a, m) => ((a % m) + m) % m;

  const roots = new WeakMap();   // ctx → root: onKey і pad приходять із ctx

  // ---------------------------------------------------------------------------------------------
  // Стан картки й каркас DOM
  // ---------------------------------------------------------------------------------------------

  function state(root) {
    if (!root._ko) {
      let amount = parseInt(store.get('amt', '50'), 10);
      if (!(amount > 0)) amount = 50;
      root._ko = {
        ctx: null, el: null, layout: '', W: 0, spinNo: null, animNo: null, landAt: 0, landedNo: null, restSeg: null,
        timers: [], untilKey: '', untilAt: 0, seq: undefined, sayT: 0, amount, cur: 0, curOn: false, tick: 0, ro: null,
        wheelSig: '', size: 0, pre: null, busy: false,
      };
    }
    return root._ko;
  }

  function setHtml(el, html) {
    if (el._sig !== html) { el._sig = html; el.innerHTML = html; return true; }
    return false;
  }

  function later(st, fn, ms) {
    const t = setTimeout(() => { st.timers = st.timers.filter((x) => x !== t); fn(); }, Math.max(0, ms));
    st.timers.push(t);
    return t;
  }

  function skeleton(root, ctx) {
    const st = state(root);
    if (st.el && root.contains(st.el.box)) return st.el;
    const box = document.createElement('div');
    box.className = 'ko';
    let conf = '';
    for (let i = 0; i < 12; i++) conf += '<i></i>';
    box.innerHTML = ''
      + '<div class="ko-stage">'
      + '<div class="ko-glek" data-mood="idle"><img src="/static/glek.svg" alt="Дядько Глек" draggable="false"><span class="ko-badge"></span></div>'
      + '<div class="ko-say" aria-live="polite"></div>'
      + '<div class="ko-wheelbox"><canvas class="ko-wheel" aria-hidden="true"></canvas>'
      + '<span class="ko-finger" aria-hidden="true"></span>'
      + '<div class="ko-hub" aria-live="polite"><b class="ko-hx"></b><small class="ko-hs"></small></div>'
      + '<div class="ko-conf" aria-hidden="true">' + conf + '</div></div>'
      + '<div class="ko-bar" aria-hidden="true"><i></i></div>'
      + '<button type="button" class="ko-ib" title="Правила й чесність">ⓘ</button>'
      + '</div>'
      + '<div class="ko-main">'
      + '<div class="ko-hist" aria-label="Останні 20 кіл"></div>'
      + '<div class="ko-closed" hidden></div>'
      + '<div class="ko-picks">' + PICKS.map((p, i) => '<button type="button" class="ko-pick p' + p + '" data-pick="' + p + '" data-i="' + i + '">'
        + '<span class="ko-px">×' + p + '</span><span class="ko-pn"></span><span class="ko-pm"></span><span class="ko-pt"></span>'
        + '<span class="ko-pa"></span></button>').join('') + '</div>'
      + '<div class="ko-panel">'
      + '<div class="ko-amt"><button type="button" class="ko-pm2" data-a="minus" aria-label="Менше">−</button>'
      + '<input class="ko-in" inputmode="numeric" autocomplete="off" aria-label="Сума ставки, черепків">'
      + '<button type="button" class="ko-pm2" data-a="plus" aria-label="Більше">+</button></div>'
      + '<div class="ko-q">' + QUICK.map((q) => '<button type="button" data-q="' + q + '">' + q + '</button>').join('') + '</div>'
      + '<div class="ko-acts"><button type="button" class="ko-clear">✕ Зняти</button><button type="button" class="ko-rep">🔁 Повторити</button></div>'
      + '</div>'
      + '<div class="ko-line"></div>'
      + '<div class="ko-sit"></div>'
      + '<div class="ko-last"></div>'
      + '</div>'
      + '<div class="ko-info" hidden data-pad-scope></div>';
    root.appendChild(box);
    const q = (s) => box.querySelector(s);
    st.el = {
      box, stage: q('.ko-stage'), glek: q('.ko-glek'), badge: q('.ko-badge'), say: q('.ko-say'), wheelbox: q('.ko-wheelbox'),
      wheel: q('.ko-wheel'), hub: q('.ko-hub'), hx: q('.ko-hx'), hs: q('.ko-hs'), conf: q('.ko-conf'), bar: q('.ko-bar i'),
      hist: q('.ko-hist'), closed: q('.ko-closed'), picks: [...box.querySelectorAll('.ko-pick')], amt: q('.ko-in'),
      clear: q('.ko-clear'), rep: q('.ko-rep'), line: q('.ko-line'), sit: q('.ko-sit'), last: q('.ko-last'), info: q('.ko-info'),
      panel: q('.ko-panel'),
    };
    st.el.amt.value = st.amount;
    wire(root, st);
    fitLayout(root, st, true);
    if (window.ResizeObserver) {
      // через таймер: синхронна перемальовка в колбеку RO дає «ResizeObserver loop completed…»
      st.ro = new ResizeObserver(() => {
        clearTimeout(st.roT);
        st.roT = setTimeout(() => { if (root._ko) { fitLayout(root, st); drawWheel(st); } }, 0);
      });
      st.ro.observe(root);
    }
    return st.el;
  }

  /// Розкладка за шириною картки: ≥ 760 — колесо ліворуч, ставки праворуч; вужче — одне під одним (телефон).
  function fitLayout(root, st, force) {
    const w = root.getBoundingClientRect().width || root.clientWidth || window.innerWidth;
    if (!w && !force) return false;
    const lay = w >= 760 ? 'wide' : 'vert';
    if (lay === st.layout) return false;
    st.layout = lay;
    st.el.box.classList.toggle('ko-wide', lay === 'wide');
    st.el.box.classList.toggle('ko-vert', lay !== 'wide');
    return true;
  }

  // ---------------------------------------------------------------------------------------------
  // Колесо: петриківка на гончарному крузі (полотно малюється раз на розмір, далі лише обертається)
  // ---------------------------------------------------------------------------------------------

  function cssColor(st, x) {
    const v = getComputedStyle(st.el.box).getPropertyValue(COLV[x]).trim();
    return v || COLF[x];
  }

  function drawWheel(st) {
    const v = st.ctx && st.ctx.view;
    const wheel = (v && v.wheel) || null;
    if (!wheel || !wheel.length) return;
    const cv = st.el.wheel;
    const css = Math.round(st.el.wheelbox.getBoundingClientRect().width || 0);
    if (!css) return;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    const sig = css + ':' + dpr + ':' + wheel.join('');
    if (sig === st.wheelSig) return;
    st.wheelSig = sig;
    cv.width = Math.round(css * dpr);
    cv.height = Math.round(css * dpr);
    const c = cv.getContext('2d');
    if (!c) return;
    c.setTransform(dpr, 0, 0, dpr, 0, 0);
    const R = css / 2, n = wheel.length, step = (Math.PI * 2) / n, top = -Math.PI / 2;
    c.clearRect(0, 0, css, css);
    c.translate(R, R);
    // обід — випалена глина
    const rim = c.createRadialGradient(0, 0, R * 0.7, 0, 0, R);
    rim.addColorStop(0, '#8a4a22'); rim.addColorStop(0.85, '#6a3415'); rim.addColorStop(1, '#3e1d0a');
    c.beginPath(); c.arc(0, 0, R, 0, Math.PI * 2); c.fillStyle = rim; c.fill();
    // сегменти — полив'яні вічка по колу
    const r0 = R * 0.72, r1 = R * 0.95;
    const col = {};
    for (const x of [0, 2, 3, 6, 30]) col[x] = cssColor(st, x);
    for (let i = 0; i < n; i++) {
      const x = wheel[i], a0 = top + i * step, a1 = a0 + step;
      c.beginPath();
      c.arc(0, 0, x === 30 ? R * 0.985 : r1, a0, a1);
      c.arc(0, 0, r0, a1, a0, true);
      c.closePath();
      c.fillStyle = col[x];
      c.fill();
      c.lineWidth = Math.max(0.6, R * 0.006);
      c.strokeStyle = 'rgba(30, 14, 4, .55)';
      c.stroke();
      if (x === 30) {   // глек — золотий край і крапка
        c.beginPath(); c.arc(0, 0, R * 0.975, a0 + step * 0.12, a1 - step * 0.12);
        c.strokeStyle = '#ffd65a'; c.lineWidth = Math.max(1, R * 0.018); c.stroke();
        const am = a0 + step / 2;
        c.beginPath(); c.arc(Math.cos(am) * R * 0.835, Math.sin(am) * R * 0.835, Math.max(1.4, R * 0.016), 0, Math.PI * 2);
        c.fillStyle = '#ffe9a8'; c.fill();
      } else if (x === 0) {   // тріснув — блискавка-тріщина
        const am = a0 + step / 2, ca = Math.cos(am), sa = Math.sin(am), px = -sa, py = ca, w = R * 0.012;
        c.beginPath();
        c.moveTo(ca * r0, sa * r0);
        c.lineTo(ca * R * 0.79 + px * w, sa * R * 0.79 + py * w);
        c.lineTo(ca * R * 0.85 - px * w, sa * R * 0.85 - py * w);
        c.lineTo(ca * r1, sa * r1);
        c.strokeStyle = '#f2e6cf'; c.lineWidth = Math.max(0.8, R * 0.008); c.stroke();
      }
    }
    // гончарний круг: сира глина з борозенками
    const disk = c.createRadialGradient(-R * 0.15, -R * 0.18, R * 0.05, 0, 0, r0);
    disk.addColorStop(0, '#e8b98a'); disk.addColorStop(0.55, '#c98a56'); disk.addColorStop(1, '#9a5a2e');
    c.beginPath(); c.arc(0, 0, r0 - R * 0.01, 0, Math.PI * 2); c.fillStyle = disk; c.fill();
    c.lineWidth = Math.max(0.6, R * 0.004);
    c.strokeStyle = 'rgba(90, 44, 14, .28)';
    for (let k = 1; k <= 6; k++) { c.beginPath(); c.arc(0, 0, r0 * (0.25 + k * 0.11), 0, Math.PI * 2); c.stroke(); }
    // петриківка: вінок зернят і завитків (три кольори), щоб оберт було видно
    const orn = ['#c8352b', '#e8b83a', '#4f8a3a'];
    for (let i = 0; i < 12; i++) {
      const a = (i / 12) * Math.PI * 2, rr = r0 * 0.78;
      c.save();
      c.rotate(a);
      c.translate(rr, 0);
      c.rotate(Math.PI / 2);
      c.beginPath();
      c.ellipse(0, 0, R * 0.022, R * 0.07, 0, 0, Math.PI * 2);
      c.fillStyle = orn[i % 3];
      c.fill();
      c.restore();
      const b = a + Math.PI / 12, rb = r0 * 0.6;
      c.beginPath();
      c.arc(Math.cos(b) * rb, Math.sin(b) * rb, R * 0.028, b, b + Math.PI * 1.5);
      c.strokeStyle = orn[(i + 1) % 3];
      c.lineWidth = Math.max(1, R * 0.012);
      c.lineCap = 'round';
      c.stroke();
    }
    c.setTransform(1, 0, 0, 1, 0, 0);
  }

  // ---------------------------------------------------------------------------------------------
  // Час, оберт, Глек
  // ---------------------------------------------------------------------------------------------

  function syncTime(st, v) {
    const key = (v.phase || '') + '|' + (v.until || '');
    if (key !== st.untilKey) {
      st.untilKey = key;
      st.untilAt = v.leftMs != null ? Date.now() + Math.max(0, v.leftMs) : 0;
    }
  }
  const leftMs = (st) => (st.untilAt ? Math.max(0, st.untilAt - Date.now()) : 0);

  const segAngle = (seg, n) => (seg + 0.5) * (360 / n);
  function setRot(el, deg, ms) {
    el.style.transition = ms > 0 ? 'transform ' + Math.round(ms) + 'ms ' + SPIN_EASE : 'none';
    el.style.transform = 'rotate(' + deg.toFixed(3) + 'deg)';
  }
  function rest(st, seg, n) {
    st.W = -segAngle(seg, n);
    setRot(st.el.wheel, st.W, 0);
  }

  function landed(st, v) {
    const sp = v && v.spin;
    if (!sp) return false;
    if (st.animNo === sp.no && Date.now() < st.landAt - 40) return false;
    return v.phase !== 'spin' || st.landedNo === sp.no;
  }

  function syncWheel(root, st, v) {
    const n = (v.wheel && v.wheel.length) || 125;
    const sp = v.spin;
    if (!sp) {
      // ставки: круг стоїть, де став; свіжа картка — на останньому сегменті історії
      if (st.spinNo == null && st.restSeg == null && v.history && v.history.length) { st.restSeg = v.history[0].seg; rest(st, st.restSeg, n); }
      return;
    }
    if (sp.no === st.spinNo) return;
    st.spinNo = sp.no;
    const T = Math.max(0, +sp.leftMs || 0);
    if (v.phase !== 'spin' || T < 800 || reduced()) {
      // F5, реконект, пізній вид, «менше руху»: одразу кінцеве положення
      st.animNo = null;
      st.landedNo = sp.no;
      rest(st, sp.seg, n);
      return;
    }
    const step = 360 / n;
    const target = -segAngle(sp.seg, n) + (Math.random() - 0.5) * step * 0.6;
    let W1 = st.W + 360 * 4 + mod(target - st.W, 360);
    if (W1 - st.W < 360 * 4) W1 += 360;
    st.W = W1;
    st.animNo = sp.no;
    st.landAt = Date.now() + T;
    setRot(st.el.wheel, W1, T);
    const no = sp.no;
    later(st, () => {
      if (!root._ko) return;
      st.landedNo = no;
      paint(root, st.ctx);
    }, T);
  }

  function syncGlek(root, st, ctx, v) {
    const g = v.glek || {};
    // розрахунок уже на дроті, а круг ще крутиться — настрій і «Макітра ×6!» Глек скаже, коли круг стане
    if (v.spin && v.phase !== 'spin' && st.animNo === v.spin.no && !landed(st, v)) return;
    const mood = BADGE[g.mood] != null ? g.mood : 'idle';
    const el = st.el;
    if (el.glek.dataset.mood !== mood) el.glek.dataset.mood = mood;
    if (el.badge.textContent !== BADGE[mood]) el.badge.textContent = BADGE[mood];
    if (g.seq === st.seq) return;
    const first = st.seq === undefined;
    st.seq = g.seq;
    const key = 'kolo_seq:' + ((ctx.room && ctx.room.id) || '');
    const seen = first && session.get(key) === String(g.seq);
    session.set(key, String(g.seq));
    if (seen) return;   // F5: минулу хмарку й анімацію не повторюємо
    if (g.say) {
      el.say.textContent = g.say;
      el.say.classList.add('on');
      clearTimeout(st.sayT);
      st.sayT = setTimeout(() => { if (root._ko) el.say.classList.remove('on'); }, SAY_MS);
    }
    if (reduced()) return;
    el.glek.classList.remove('go');
    void el.glek.offsetWidth;
    el.glek.classList.add('go');
    if (mood === 'dance') {
      const R = (el.wheelbox.clientWidth || 200) / 2;
      el.conf.querySelectorAll('i').forEach((p, i) => {
        const a = (i / 12) * Math.PI * 2 + Math.random() * 0.4, d = R * (0.45 + Math.random() * 0.5);
        p.style.setProperty('--dx', Math.round(Math.cos(a) * d) + 'px');
        p.style.setProperty('--dy', Math.round(Math.sin(a) * d - R * 0.25) + 'px');
        p.style.setProperty('--r', Math.round(Math.random() * 720 - 360) + 'deg');
        p.style.animationDelay = (i % 4) * 90 + 'ms';
      });
      el.conf.classList.remove('on');
      void el.conf.offsetWidth;
      el.conf.classList.add('on');
      later(st, () => el.conf.classList.remove('on'), 3400);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Малювання
  // ---------------------------------------------------------------------------------------------

  function canBet(ctx, st, v) {
    return !!(ctx.mine && ctx.playing && v && v.on && v.me && v.phase === 'bets' && leftMs(st) > 0);
  }
  function limits(v) {
    const L = (v && v.limits) || {};
    return { min: L.min || 1, max: L.max || 0 };
  }

  function paint(root, ctx) {
    if (!ctx) return;
    const st = state(root);
    st.ctx = ctx;
    const el = skeleton(root, ctx);
    const v = ctx.view;
    if (!v || !v.phase) { setHtml(el.line, '<span class="muted">Глек місить глину…</span>'); return; }
    el.box.classList.toggle('watch', !ctx.mine);
    syncTime(st, v);
    drawWheel(st);
    syncWheel(root, st, v);
    syncGlek(root, st, ctx, v);
    // «Чесно наперед»: відбиток, показаний ДО того, як колесо стало, — із ним «ⓘ» звіряє seed
    if (v.hash && !v.seed && v.phase === 'bets') st.pre = { round: v.round, hash: v.hash };
    paintHub(st, v);
    paintHist(st, v);
    paintPicks(st, ctx, v);
    paintPanel(st, ctx, v);
    paintLast(st, ctx, v);
    el.closed.hidden = !!v.on;
    if (!v.on) el.closed.textContent = v.phase === 'off' ? 'Гончарне колесо відпочиває — ставок зараз не приймаю' : 'Каса зачинена — ставок зараз не приймаю';
  }

  function paintHub(st, v) {
    const el = st.el, ld = landed(st, v);
    let x = '', s = '', cls = '';
    if (v.spin && ld) {
      x = xLabel(v.spin.x);
      s = v.spin.x ? NAME[v.spin.x] : 'тріснув!';
      cls = 'h' + v.spin.x;
    } else if (v.phase === 'spin') {
      x = '…'; s = 'крутиться';
    } else if (v.phase === 'bets') {
      const left = leftMs(st);
      x = left > 0 ? String(Math.ceil(left / 1000)) : '0';
      s = left > 0 ? 'ставки' : 'ставки зроблено';
      cls = 'hb';
    } else if (v.phase === 'off') {
      x = '💤'; s = 'відпочиває';
    }
    if (el.hx.textContent !== x) el.hx.textContent = x;
    if (el.hs.textContent !== s) el.hs.textContent = s;
    const c = 'ko-hub ' + cls + (ld && v.spin ? ' land' : '');
    if (el.hub.className !== c) el.hub.className = c;
    const k = v.phase === 'bets' && v.phaseMs ? Math.max(0, Math.min(1, leftMs(st) / v.phaseMs)) : 0;
    const w = (k * 100).toFixed(1) + '%';
    if (el.bar.style.width !== w) el.bar.style.width = w;
  }

  function paintHist(st, v) {
    let h = v.history || [];
    // круг ще крутиться, а розрахунок уже в історії — новий сегмент покажемо, коли стане
    if (v.spin && h.length && h[0].round === v.spin.no && !landed(st, v)) h = h.slice(1);
    setHtml(st.el.hist, h.length
      ? h.map((r) => '<span class="ko-hp h' + r.x + '" title="Коло ' + r.round + ': ' + (r.x ? '×' + r.x + ' ' + NAME[r.x] : 'тріснув') + '">' + xLabel(r.x) + '</span>').join('')
      : '<span class="muted small">Тут буде, що випадало останні 20 кіл</span>');
  }

  function paintPicks(st, ctx, v) {
    const ok = canBet(ctx, st, v), ld = landed(st, v);
    const win = v.spin && ld && (v.phase === 'spin' || v.phase === 'result') ? v.spin.x : null;
    const wheel = v.wheel || [];
    const n = wheel.length || 125;
    const totals = {};
    for (const t of v.totals || []) totals[t.pick] = t;
    const mineRow = (v.players || []).find((p) => p.mine);
    const mine = {};
    for (const b of (mineRow && mineRow.bets) || []) mine[b.pick] = b.amount;
    st.el.picks.forEach((btn, i) => {
      const p = PICKS[i];
      const cnt = wheel.filter((x) => x === p).length;
      setHtml(btn.querySelector('.ko-pn'), NAME[p] + ' · ' + (cnt ? Math.round((cnt / n) * 1000) / 10 : 0).toString().replace('.', ',') + ' %');
      setHtml(btn.querySelector('.ko-pm'), mine[p] ? 'твоє ' + fmtN(mine[p]) : '');
      const t = totals[p] || { amount: 0, people: 0 };
      setHtml(btn.querySelector('.ko-pt'), t.amount ? 'на столі ' + short(t.amount) + ' · 👤' + t.people : '');
      const who = (v.players || []).filter((pl) => (pl.bets || []).some((b) => b.pick === p));
      setHtml(btn.querySelector('.ko-pa'), who.slice(0, 5).map((pl) => '<i class="ko-av c' + (pl.color % 12) + (pl.here ? '' : ' gone') + '" title="'
        + ctx.esc(pl.nick) + '">' + ctx.esc(Array.from(pl.nick || '?')[0].toUpperCase()) + '</i>').join('') + (who.length > 5 ? '<i class="ko-av more">+' + (who.length - 5) + '</i>' : ''));
      btn.disabled = !ok;
      btn.classList.toggle('win', win === p);
      btn.classList.toggle('lose', win != null && win !== p);
      btn.classList.toggle('has', !!mine[p]);
      btn.classList.toggle('cur', st.curOn && st.cur === i);
    });
  }

  function paintPanel(st, ctx, v) {
    const el = st.el, me = v.me, ok = canBet(ctx, st, v);
    el.panel.hidden = !ctx.mine;
    if (document.activeElement !== el.amt && String(st.amount) !== el.amt.value) el.amt.value = st.amount;
    el.amt.disabled = !ctx.mine;
    el.clear.disabled = !(me && me.canClear && ok);
    el.rep.disabled = !(me && me.canRepeat && ok);
    setHtml(el.rep, '🔁 Повторити' + (me && me.repeatCost ? ' · ' + fmtN(me.repeatCost) : ''));
    const L = limits(v);
    el.panel.querySelectorAll('.ko-q button').forEach((b) => { b.disabled = !ctx.mine || (me && +b.dataset.q > me.free) || +b.dataset.q < L.min; });
    let line = '';
    if (ctx.mine && me) {
      line = (me.onTable ? 'На колі <b>' + fmtN(me.onTable) + '</b> · ' : '') + 'вільних <b>' + fmtN(me.free) + '</b> 🏺'
        + ' · ставка ' + L.min + '–' + (L.max ? fmtN(L.max) : '∞') + ' на множник';
      if (me.note) line += '<br><span class="ko-note">' + ctx.esc(me.note) + '</span>';
      if (me.onTable && !ok) line += '<br><span class="muted small">Твої ставки зіграють, навіть якщо встанеш</span>';
    } else if (!ctx.mine) {
      line = '<span class="muted">Ти дивишся збоку — сядь, щоб ставити</span>';
    }
    setHtml(el.line, line);
    const free = ctx.room && ctx.room.seats ? ctx.room.seats.some((s) => !s.nick) : false;
    setHtml(el.sit, !ctx.mine && ctx.playing ? (free ? '<button type="button" class="primary ko-sitb">🪑 Сісти до колеса</button>' : '<span class="muted">Місць нема — дивись</span>') : '');
  }

  function paintLast(st, ctx, v) {
    const l = v.last;
    // поки круг крутиться, last — ще минуле коло; нове покажемо, коли круг стане
    if (!l || (v.spin && l.round === v.spin.no && !landed(st, v))) {
      setHtml(st.el.last, '');
      return;
    }
    const nick = ctx.me && ctx.me.nick;
    const rows = (l.results || []).slice(0, 12).map((r) => '<li class="' + (r.nick === nick ? 'me' : '') + '"><i class="ko-dot c' + (r.color % 12) + '"></i>'
      + '<span class="n">' + ctx.esc(r.nick) + '</span><span class="s">' + fmtN(r.staked) + '</span><b class="' + (r.net > 0 ? 'up' : r.net < 0 ? 'down' : '') + '">'
      + signed(r.net) + '</b></li>').join('');
    setHtml(st.el.last, '<div class="ko-lh">Коло ' + l.round + ': <b class="ko-lx h' + l.x + '">' + xLabel(l.x) + ' ' + (l.x ? NAME[l.x] : 'тріснув') + '</b>'
      + (l.staked ? ' · поставили ' + fmtN(l.staked) + ', виплачено ' + fmtN(l.paid) : ' · без ставок') + '</div>'
      + (rows ? '<ul>' + rows + '</ul>' : ''));
  }

  // ---------------------------------------------------------------------------------------------
  // Дії
  // ---------------------------------------------------------------------------------------------

  function setAmount(st, a) {
    const v = st.ctx && st.ctx.view, L = limits(v);
    a = Math.max(L.min, Math.round(a) || 0);
    if (L.max > 0) a = Math.min(a, L.max);
    st.amount = a;
    st.el.amt.value = a;
    store.set('amt', a);
  }
  function stepAmount(st, d) {
    const a = st.amount;
    const inc = a < 100 ? 10 : a < 1000 ? 50 : 100;
    setAmount(st, d > 0 ? a + inc : a - (a <= 100 ? 10 : a <= 1000 ? 50 : 100));
    if (st.ctx) paint(st.root, st.ctx);
  }

  function bet(root, ctx, pick) {
    const st = state(root), v = ctx.view;
    if (!canBet(ctx, st, v)) return false;
    const btn = st.el.picks[PICKS.indexOf(pick)];
    if (btn) { btn.classList.remove('tap'); void btn.offsetWidth; btn.classList.add('tap'); }
    ctx.act('bet', { pick, amount: st.amount });
    return true;
  }
  function command(root, ctx, act) {
    const st = state(root), v = ctx.view;
    if (!canBet(ctx, st, v)) return false;
    if (act === 'clear' && !(v.me && v.me.canClear)) return false;
    if (act === 'repeat' && !(v.me && v.me.canRepeat)) return false;
    ctx.act(act);
    return true;
  }

  function wire(root, st) {
    const el = st.el;
    st.root = root;
    el.box.addEventListener('click', (e) => {
      const ctx = st.ctx;
      if (!ctx) return;
      const pk = e.target.closest('.ko-pick');
      if (pk) { st.curOn = false; bet(root, ctx, +pk.dataset.pick); return; }
      const q = e.target.closest('.ko-q button');
      if (q) { setAmount(st, +q.dataset.q); paint(root, ctx); return; }
      const pm = e.target.closest('.ko-pm2');
      if (pm) { stepAmount(st, pm.dataset.a === 'plus' ? 1 : -1); return; }
      if (e.target.closest('.ko-clear')) { command(root, ctx, 'clear'); return; }
      if (e.target.closest('.ko-rep')) { command(root, ctx, 'repeat'); return; }
      if (e.target.closest('.ko-sitb')) { if (window.HGames && HGames.call) HGames.call('JoinRoom', ctx.room.id); return; }
      if (e.target.closest('.ko-ib')) { info(root, st, true); return; }
      if (e.target.closest('.ko-x0') || e.target === el.info) { info(root, st, false); return; }
      if (e.target.closest('.ko-chk')) verify(st);
    });
    el.amt.addEventListener('change', () => { setAmount(st, parseInt(el.amt.value.replace(/\D/g, ''), 10) || 0); if (st.ctx) paint(root, st.ctx); });
    el.amt.addEventListener('keydown', (e) => { if (e.key === 'Enter') el.amt.blur(); });
  }

  function onKey(e, ctx) {
    const root = roots.get(ctx);
    if (!root || !root._ko) return false;
    const st = root._ko;
    if (e.target && /^(INPUT|TEXTAREA)$/.test(e.target.tagName)) return false;
    if (!st.el.info.hidden) {
      if (e.code === 'Escape' || e.code === 'Enter' || e.code === 'Space') { info(root, st, false); return true; }
      return false;
    }
    if (!ctx.mine) return false;
    const code = e.code;
    if (code === 'ArrowLeft' || code === 'ArrowRight') {
      st.cur = st.curOn ? mod(st.cur + (code === 'ArrowRight' ? 1 : -1), PICKS.length) : st.cur;
      st.curOn = true;
      paint(root, ctx);
      return true;
    }
    if (e.repeat) return false;
    if (code === 'Enter' || code === 'NumpadEnter' || code === 'Space') { st.curOn = true; bet(root, ctx, PICKS[st.cur]); paint(root, ctx); return true; }
    const dg = /^(?:Digit|Numpad)([1-4])$/.exec(code);
    if (dg) { st.cur = +dg[1] - 1; bet(root, ctx, PICKS[st.cur]); return true; }
    if (code === 'Backspace' || code === 'KeyX') { command(root, ctx, 'clear'); return true; }
    if (code === 'KeyR') { command(root, ctx, 'repeat'); return true; }
    if (code === 'Minus' || code === 'NumpadSubtract') { stepAmount(st, -1); return true; }
    if (code === 'Equal' || code === 'NumpadAdd') { stepAmount(st, 1); return true; }
    return false;
  }

  // ---------------------------------------------------------------------------------------------
  // ⓘ Правила й «чесно наперед»
  // ---------------------------------------------------------------------------------------------

  function info(root, st, open) {
    const el = st.el.info;
    if (!open) { el.hidden = true; return; }
    const ctx = st.ctx, v = (ctx && ctx.view) || {}, L = limits(v), wheel = v.wheel || [], n = wheel.length || 125;
    const rows = PICKS.concat([0]).map((x) => {
      const c = wheel.filter((w) => w === x).length;
      return '<tr><td><b class="ko-lx h' + x + '">' + xLabel(x) + '</b> ' + (x ? NAME[x] : 'тріснув') + '</td><td>' + c + '</td><td>'
        + (Math.round((c / n) * 1000) / 10).toString().replace('.', ',') + ' %</td><td>' + (x ? (Math.round((x * c / n) * 1000) / 10).toString().replace('.', ',') + ' %' : '—') + '</td></tr>';
    }).join('');
    const l = v.last;
    st.infoL = l;
    el.innerHTML = '<div class="ko-card"><button type="button" class="ko-x0" aria-label="Закрити">✕</button>'
      + '<h3>Гончарне колесо — як грати</h3><ul>'
      + '<li>Глек крутить круг для всіх: <b>10 с ставки</b> → крутиться 5 с → виплати 3 с. Сісти можна будь-коли; встав — ставки однаково зіграють.</li>'
      + '<li>Став на <b>×2 миску, ×3 горщик, ×6 макітру чи ×30 глек</b> — можна на кілька одразу. Ставка ' + L.min + '–' + (L.max ? fmtN(L.max) : '∞') + ' 🏺 на множник; натиснув ще раз — додав.</li>'
      + '<li>Круг став на твоєму множнику — отримуєш ставку × множник. На «💥 Тріснув!» програють усі.</li>'
      + '<li>Черепки списуються в мить «Ставки зроблено!», виграш приходить, коли круг стане.</li></ul>'
      + '<table class="ko-tab"><tr><th>сегмент</th><th>з ' + n + '</th><th>шанс</th><th>повертає</th></tr>' + rows + '</table>'
      + '<h3>Чесно наперед</h3>'
      + '<p>Глек вирішує сегмент на початку ставок і одразу показує відбиток <code>hash = sha256(seed)</code>. Коли круг стане, — сам <code>seed</code>: '
      + 'перевір, що відбиток збігається, а сегмент = (перші 13 hex-символів seed як число) mod ' + n + '.</p>'
      + '<div class="ko-kv"><span>зараз</span><b>#' + ctx.esc(v.round || '—') + '</b><code>' + ctx.esc(v.hash || '—') + '</code></div>'
      + (l ? '<div class="ko-kv"><span>минуле</span><b>#' + ctx.esc(l.round) + ' · ' + xLabel(l.x) + '</b><code>hash ' + ctx.esc(l.hash) + '</code><code>seed ' + ctx.esc(l.seed) + '</code></div>'
        + '<button type="button" class="ko-chk">Перевірити минуле коло</button><div class="ko-res"></div>'
        : '<p class="muted">Минулого кола ще не було — перевірка з’явиться, коли круг стане.</p>')
      + '</div>';
    el.hidden = false;
  }

  async function sha256(s) {
    const b = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(s));
    return [...new Uint8Array(b)].map((x) => x.toString(16).padStart(2, '0')).join('');
  }
  /// Сегмент із seed — як KoloCore.Seg на сервері: перші 13 hex (52 біти) mod розмір кола.
  function segOf(seed, n) { return Number(BigInt('0x' + String(seed).slice(0, 13)) % BigInt(n)); }

  async function verify(st) {
    const l = st.infoL, out = st.el.info.querySelector('.ko-res'), v = (st.ctx && st.ctx.view) || {};
    if (!l || !out) return;
    if (!window.crypto || !crypto.subtle) { out.textContent = 'Цей браузер не рахує sha256 (потрібен https)'; return; }
    try {
      const early = st.pre && st.pre.round === l.round;
      const hash = early ? st.pre.hash : l.hash;
      const okH = (await sha256(l.seed)) === String(hash).toLowerCase();
      const wheel = v.wheel || [];
      const seg = segOf(String(l.seed).toLowerCase(), wheel.length || 125);
      const okS = seg === l.seg && wheel[seg] === l.x;
      out.innerHTML = (okH ? '✅ відбиток збігся' : '❌ відбиток НЕ збігся') + (early ? '' : ' (відбитка до кола не бачив — зайшов посеред раунду)')
        + '<br>' + (okS ? '✅' : '⚠') + ' за формулою сегмент ' + seg + ' — ' + xLabel(wheel[seg]) + ', на колі ' + xLabel(l.x);
    } catch (e) { out.textContent = 'Не вийшло перевірити: ' + e.message; }
  }

  // ---------------------------------------------------------------------------------------------
  // Статус і тикер
  // ---------------------------------------------------------------------------------------------

  function statusText(ctx) {
    const v = ctx.view;
    if (!v || !v.phase) return '';
    const root = roots.get(ctx), st = root && root._ko;
    const me = v.me;
    switch (v.phase) {
      case 'bets': {
        const s = Math.ceil((st ? leftMs(st) : (v.leftMs || 0)) / 1000);
        const head = s > 0 ? 'Ставки ще ' + s + ' с' : 'Ставки зроблено';
        if (!ctx.mine) return head + ' · сядь, щоб ставити';
        return head + ' · ' + (me && me.onTable ? 'твоїх на колі ' + me.onTable : 'обирай множник');
      }
      case 'spin':
      case 'result': {
        const sp = v.spin;
        if (!sp || (st && !landed(st, v))) return 'Ставки зроблено — круг крутиться…';
        const head = sp.x ? SHOUT[sp.x] + ' ×' + sp.x : 'Тріснув!';
        const r = v.last && v.last.round === sp.no && ctx.me ? (v.last.results || []).find((x) => x.nick === ctx.me.nick) : null;
        return r ? head + ' · ти ' + signed(r.net) : head;
      }
      case 'off': return 'Гончарне колесо відпочиває';
      default: return '';
    }
  }

  function tick(root) {
    const st = root._ko;
    if (!st || !st.ctx || !st.ctx.view) return;
    const v = st.ctx.view;
    if (v.phase !== 'bets') return;
    paintHub(st, v);
    const card = root.closest('.gtable');
    const se = card && card.querySelector('.gstatus');
    const text = statusText(st.ctx);
    if (se && text && se.textContent !== text) se.textContent = text;
    // дедлайн ставок минув на клієнті — кнопки гасимо, не чекаючи сервера
    const off = !canBet(st.ctx, st, v);
    if (off !== st.el.picks[0].disabled) paint(root, st.ctx);
  }

  // ---------------------------------------------------------------------------------------------
  // Реєстрація
  // ---------------------------------------------------------------------------------------------

  HGames.register({
    id: 'kolo',
    added: '2026-10-10',
    icon: ICON,
    seatNames: (i) => 'місце ' + (i + 1),
    pad: {
      dirs: 'x',
      a: 'Enter',
      x: 'KeyX',
      hint: '{dpad} множник · {a} поставити · {x} зняти · {lb}{rb} сума · {y} повторити',
      on(btn, ctx) {
        const root = roots.get(ctx);
        if (!root || !root._ko) return false;
        if (btn === 'lb' || btn === 'rb') { stepAmount(root._ko, btn === 'rb' ? 1 : -1); return true; }
        if (btn === 'y') { command(root, ctx, 'repeat'); return true; }
        return false;
      },
      when: (ctx) => ctx.mine && ctx.playing,
    },

    mount(root, ctx) {
      roots.set(ctx, root);
      const st = state(root);
      st.ctx = ctx;
      skeleton(root, ctx);
      st.tick = setInterval(() => tick(root), 250);
      paint(root, ctx);
    },

    update(root, ctx) {
      roots.set(ctx, root);
      paint(root, ctx);
    },

    visible(root, ctx, on) {
      if (on && root._ko) { fitLayout(root, root._ko); drawWheel(root._ko); paint(root, ctx); }
    },

    onKey,
    status: statusText,

    unmount(root) {
      const st = root._ko;
      if (!st) return;
      clearInterval(st.tick);
      clearTimeout(st.sayT);
      clearTimeout(st.roT);
      st.timers.forEach(clearTimeout);
      st.timers = [];
      if (st.ro) st.ro.disconnect();
      root._ko = null;
    },

    // Для перевірки (qa): формула сегмента й кути — без DOM.
    qa: { segOf, segAngle, sha256 },
  });
})();
