/*
  Вогник і Крапля (specs/vohnyk.md). Кооп-платформер на двох: Вогник боїться води, Крапля — лави, обоє — болота.

  Сервер тикає раз на 40 мс і крутить детерміновану симуляцію кроками по 20 мс (Impl/VohnykWorld.cs). Тут та сама
  симуляція (vohnyk-sim.js) крутиться наперед: свій герой рухається того ж кадру, коли натиснув, а кадр сервера
  лише звіряється з передбаченим (хеш світу на кроці n). Не збіглось — беремо світ сервера й доганяємо свій ввід;
  герой, якого цим посунуло, доїжджає плавно (зсув, що згасає). Глядач крутить ту саму симуляцію з кадру сервера
  й утримуваними клавішами з нього ж — тому і в нього рух без ривків.

  Кадр: { n, ph (0 відлік, 1 гра, 2 смерть, 3 «Разом!»), pt (кроків до кінця фази), t (годинник, кроки), d (смертей),
          h (хеш світу), a (активний герой у соло), lv (рівень), dc (причина смерті), ack, w (увесь стан світу) }.
  Вид: { phase, solo, active, picked, levels[], level (статика), run, result, f }.
  Ввід: input('in', { n: крок, c: герой, k: маска 1 ліворуч, 2 праворуч, 4 стрибок }) — лише на зміну.
  Дії: act('pick', { level: n }) у лобі, act('reset') — почати рівень заново, act('giveup') — здатись.
*/
(() => {
  const TILE = 40, SU = 16;
  const STEP_MS = 20, READY = 100, DEAD = 30, CLEAR = 60;
  const PH_READY = 0, PH_GO = 1, PH_DEAD = 2, PH_CLEAR = 3;
  const KL = 1, KR = 2, KJ = 4;
  const HIST = 64;
  const LEAD = 4;               // на скільки кроків свій світ іде попереду сервера
  const SEND_PER_SEC = 24;      // квота каркаса — 30 Input/с; тримаємо запас
  // Поки тримаєш клавішу, раз на пів секунди нагадуємо про неї серверу: хто мовчить понад 1,5 с (Vohnyk.StaleSteps),
  // того сервер вважає зниклим і відпускає йому клавіші — щоб герой зниклого не бігав у воду раз у раз.
  const HEARTBEAT = 25;
  const AWAY_MS = 200, CALM_MS = 50;  // цикл малювання: схована картка / лобі й підсумок без ефектів (spin)
  const SOLO_LATCH = 100;       // VohnykWorld.SoloLatch: сам за двох кнопка брами «все разом» тримається ще 2 с
  const NAMES = ['Вогник', 'Крапля'];
  const EMO = ['🔥', '💧'];
  // Кольори сигналів: кнопка чи важіль і все, що вони рухають, — одного кольору (як у Fireboy & Watergirl)
  const SIG_COLORS = ['#ffd84a', '#c792ff', '#5ef0a0', '#ff7eb6', '#7fd8ff', '#ffa14a', '#e8f0f5', '#b8e05a', '#ff6b6b', '#8aa2ff', '#f5c6a5', '#4ee0d0'];

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M5 1.5c.4 2-1.8 3.2-1.8 6.2A3.3 3.3 0 0 0 6.5 11a3.3 3.3 0 0 0 3.3-3.3C9.8 5.6 8.3 4.9 8 3c-.9 1-1 2-1 2.6C6.3 4.5 5.9 3 5 1.5z" fill="var(--clay)"/>'
    + '<path d="M6.5 6.4c-.6.9-1.2 1.6-1.2 2.6a1.2 1.2 0 0 0 2.4 0c0-1-.6-1.7-1.2-2.6z" fill="var(--accent)"/>'
    + '<path d="M11.5 6.5c-1.2 1.8-2.6 3.3-2.6 5.1a2.6 2.6 0 0 0 5.2 0c0-1.8-1.4-3.3-2.6-5.1z" fill="var(--vh-water, #46b4ff)"/>'
    + '<circle cx="10.8" cy="11.8" r=".8" fill="#fff" opacity=".7"/></svg>';

  const DEATH_TEXT = {
    1: ['Вогник зашипів і згас', 'Пшшш… Вогник — у воду', 'Вогник погас. Мокро'],
    2: ['Крапля випарувалась', 'Пар пішов — Крапля в лаві', 'Крапля закипіла'],
    3: ['Болото не питає, хто ти', 'Загруз. Болото не жартує', 'Болото — 1 : герої — 0'],
    4: ['Обоє разом. Хоч синхронно', 'Подвійна — це вже мистецтво', 'Обоє. Зате дружно'],
    5: ['Ще раз, з початку', 'З чистого аркуша', 'Спробуймо інакше'],
  };

  // prefers-reduced-motion питаємо раз і слухаємо зміну — не створюємо MediaQueryList десятки разів за кадр
  const rmq = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
  let reducedNow = !!(rmq && rmq.matches);
  if (rmq) {
    const onRm = (e) => { reducedNow = !!e.matches; };
    if (rmq.addEventListener) rmq.addEventListener('change', onRm); else if (rmq.addListener) rmq.addListener(onRm);
  }
  const reduced = () => reducedNow;
  const clock = (ms) => {
    const s = Math.max(0, Math.floor(ms / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  /// Десяті — округлено (15 580 мс → 0:15,6), і так скрізь, де показуємо десяті.
  const clockTenths = (ms) => {
    const d = Math.max(0, Math.round(ms / 100));
    return clock(Math.floor(d / 10) * 1000) + ',' + (d % 10);
  };
  const stars = (n) => '★'.repeat(n) + '☆'.repeat(Math.max(0, 3 - n));
  const hash32 = (a, b) => {
    let h = 0x811c9dc5;
    h = Math.imul(h ^ (a & 0xffff), 16777619);
    h = Math.imul(h ^ (a >>> 16), 16777619);
    h = Math.imul(h ^ (b & 0xffff), 16777619);
    h = Math.imul(h ^ (b >>> 16), 16777619);
    return (h >>> 0) / 4294967296;
  };

  // ---------------------------------------------------------------------------------------------
  // Симуляція підвантажується окремим файлом — щоб register був синхронним (інакше «не завантажився»)
  // ---------------------------------------------------------------------------------------------

  let simWaiters = null;
  function withSim(cb) {
    if (window.VohnykSim) { cb(); return; }
    if (!simWaiters) {
      simWaiters = [];
      const s = document.createElement('script');
      s.src = '/games/vohnyk-sim.js?v=20260927';
      s.onload = () => { const w = simWaiters; simWaiters = null; w.forEach((f) => f()); };
      s.onerror = () => { console.warn('[vohnyk] vohnyk-sim.js не завантажився'); };
      document.head.appendChild(s);
    }
    simWaiters.push(cb);
  }

  // ---------------------------------------------------------------------------------------------
  // Звук: WebAudio-синтез, тихо, після першого жесту, вимикач 🔈
  // ---------------------------------------------------------------------------------------------

  const snd = {
    ac: null,
    muted: (() => { try { return localStorage.getItem('vohnykMute') === '1'; } catch (_) { return false; } })(),
    wake() {
      if (this.muted) return;
      try {
        if (!this.ac) { const A = window.AudioContext || window.webkitAudioContext; if (A) this.ac = new A(); }
        if (this.ac && this.ac.state === 'suspended') this.ac.resume();
      } catch (_) { this.ac = null; }
    },
    tone(f0, f1, ms, type, gain, delay) {
      const ac = this.ac;
      if (!ac || this.muted || ac.state !== 'running') return;
      const t0 = ac.currentTime + (delay || 0);
      const o = ac.createOscillator(), g = ac.createGain();
      o.type = type || 'sine';
      o.frequency.setValueAtTime(f0, t0);
      if (f1 !== f0) o.frequency.exponentialRampToValueAtTime(Math.max(20, f1), t0 + ms / 1000);
      g.gain.setValueAtTime((gain || 0.15) * 0.5, t0);
      g.gain.exponentialRampToValueAtTime(0.0001, t0 + ms / 1000);
      o.connect(g).connect(ac.destination);
      o.start(t0);
      o.stop(t0 + ms / 1000 + 0.02);
    },
    noise(ms, gain) {
      const ac = this.ac;
      if (!ac || this.muted || ac.state !== 'running') return;
      const len = Math.floor(ac.sampleRate * ms / 1000);
      const buf = ac.createBuffer(1, len, ac.sampleRate), d = buf.getChannelData(0);
      for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * (1 - i / len);
      const s = ac.createBufferSource(), g = ac.createGain();
      g.gain.value = (gain || 0.15) * 0.5;
      s.buffer = buf;
      s.connect(g).connect(ac.destination);
      s.start();
    },
    jump() { this.tone(300, 600, 80, 'sine', 0.1); },
    land() { this.noise(40, 0.05); },
    gem() { this.tone(880, 880, 60, 'triangle', 0.12); this.tone(1320, 1320, 60, 'triangle', 0.12, 0.06); },
    death() { this.noise(300, 0.15); },
    clear() { [523, 659, 784, 1047].forEach((f, i) => this.tone(f, f, 90, 'triangle', 0.12, i * 0.09)); },
  };

  // ---------------------------------------------------------------------------------------------
  // Стан картки
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    if (!root._vh) {
      root._vh = {
        root, ctx, raf: 0, cv: null, S: 1, stat: null, statKey: '',
        src: null, lvN: 0, L: null, world: null, surf: [], cells: [], crystals: [],
        // передбачення; gi — номер партії за столом (кадр чужої партії, навіть того самого рівня, — не наш)
        gi: -1, ph: PH_READY, pt: READY, t: 0, d: 0, cause: 0, stepLocal: -1, acc: 0, alpha: 1, lastNow: 0,
        hist: null, histMeta: null, kLog: [new Int32Array(HIST), new Int32Array(HIST)], kLogStep: [new Int32Array(HIST).fill(-1), new Int32Array(HIST).fill(-1)],
        sentK: [0, 0], sendTimes: [], lastSendStep: [0, 0],
        lastN: -1, lastAt: 0, rate: 0.05, rateRef: null, lastF: null,
        // ввід
        held: 0, touch: 0, active: 0, mode: 'kb', switchHeld: false, soloInit: false, rTimer: 0, resetArm: 0,
        // малювання; ефекти смерті й «Разом!» — за переходами (dShown, clearFor), а не за точним кроком
        off: [[0, 0], [0, 0]], parts: [], shake: 0, landT: [0, 0], wasGround: [1, 1], lastDraw: 0,
        leverAng: [], banner: null, deadText: '', deadFor: -1, dShown: 0, clearFor: -1, gemsSeen: 0, pal: null, palAt: 0,
        drawMs: new Float32Array(300), drawN: 0, drawI: 0, hudSig: '', hudAt: 0,
        ip: null, wire: [], sigWas: 0, best: {}, giveupArm: 0, pickSig: '', againBusy: false,
      };
    }
    const st = root._vh;
    st.ctx = ctx;
    ctx._vh = st;
    return st;
  }

  const mine = (st) => !!(st.ctx && st.ctx.mine);
  const playing = (st) => !!(st.ctx && st.ctx.playing);
  const view = (st) => (st.ctx && st.ctx.view) || {};
  /// Кого я веду: удвох — героя свого місця; сам — обох (активного — клавішами).
  const myHeroes = (st) => (!mine(st) ? [] : view(st).solo ? [0, 1] : [st.ctx.seat]);

  // ---------------------------------------------------------------------------------------------
  // Рівень: розбір, поверхні рідин, кристали, статичний шар
  // ---------------------------------------------------------------------------------------------

  function setLevel(st, src) {
    const Sim = window.VohnykSim;
    st.src = src;
    st.lvN = src.n;
    st.L = Sim.parseLevel(src);
    st.world = Sim.create(st.L);
    const len = st.world.stateLength;
    st.hist = Array.from({ length: HIST }, () => new Int32Array(len));
    st.histMeta = Array.from({ length: HIST }, () => ({ step: -1, h: 0, ph: 0, pt: 0, t: 0, d: 0 }));
    st.kLogStep[0].fill(-1);
    st.kLogStep[1].fill(-1);
    st.stepLocal = -1;
    st.lastN = -1;
    st.lastF = null;
    st.rateRef = null;
    st.ph = PH_READY; st.pt = READY; st.t = 0; st.d = 0; st.cause = 0;
    st.dShown = 0; st.deadFor = -1; st.deadText = ''; st.clearFor = -1; st.banner = null;
    st.sentK[0] = st.sentK[1] = 0;
    st.leverAng = st.L.levers.map((l) => (l.init ? 1 : -1));
    st.gemsSeen = 0;
    st.parts.length = 0;
    // розкладка знімка стану (як VohnykWorld.Save): герої по 14, скрині по 3, двері, ліфти по 2 — для інтерполяції
    const nx = st.L.boxes.length, nd = st.L.doors.length, nf = st.L.lifts.length;
    st.ip = {
      hx: new Float32Array(2), hy: new Float32Array(2), bx: new Float32Array(nx), by: new Float32Array(nx),
      door: new Float32Array(nd), lx: new Float32Array(nf), ly: new Float32Array(nf),
      oBox: 28, oDoor: 28 + 3 * nx, oLift: 28 + 3 * nx + nd,
    };
    // кольори сигналів: кнопки, потім важелі (як біти масок у симуляції); у кожного механізму — кольори його сигналів
    const nsig = st.L.buttons.length + st.L.levers.length;
    st.sigColor = [];
    for (let i = 0; i < nsig; i++) st.sigColor.push(SIG_COLORS[i % SIG_COLORS.length]);
    st.wire = new Float64Array(nsig);
    st.sigWas = 0;
    // поверхні рідин: суцільні горизонтальні відрізки клітинок, над якими не рідина
    const L = st.L, W = L.W, H = L.H;
    const liquid = (t) => t === 2 || t === 3 || t === 4;
    st.surf = [];
    st.cells = [];
    for (let r = 0; r < H; r++) {
      let c = 0;
      while (c < W) {
        const t = L.tiles[r * W + c];
        if (!liquid(t)) { c++; continue; }
        const above = r > 0 ? L.tiles[(r - 1) * W + c] : 1;
        if (liquid(above)) { st.cells.push({ c, r, t }); c++; continue; }
        let c1 = c;
        while (c1 + 1 < W && L.tiles[r * W + c1 + 1] === t && !liquid(r > 0 ? L.tiles[(r - 1) * W + c1 + 1] : 1)) c1++;
        st.surf.push({ c0: c, c1, r, t });
        c = c1 + 1;
      }
    }
    // кристали: повітряні клітинки біля каменю, вибір — з хешу номера рівня
    st.crystals = [];
    for (let r = 1; r < H - 1 && st.crystals.length < 40; r++)
      for (let c = 1; c < W - 1 && st.crystals.length < 40; c++) {
        if (L.tiles[r * W + c] !== 0) continue;
        const up = L.tiles[(r - 1) * W + c] === 1, left = L.tiles[r * W + c - 1] === 1, right = L.tiles[r * W + c + 1] === 1;
        if (!(up || left || right)) continue;
        const k = hash32(L.n * 7919 + r, c * 104729);
        if (k < 0.09) st.crystals.push({ c, r, k, side: up ? 'up' : left ? 'left' : 'right' });
      }
    st.statKey = '';
    fit(st);
  }

  /// Масштаб полотна: логічні пікселі рівня × S, щоб підкладка дорівнювала видимому розміру (× DPR каркаса).
  function fit(st) {
    if (!st.L || !st.wrap) return;
    const W = st.L.W * TILE, H = st.L.H * TILE;
    // пропорції рівня — і на полотні, і на всій картці (телефон лежачи рахує з них ширину середньої колонки)
    st.wrap.style.setProperty('--vh-ratio', (W / H).toFixed(4));
    st.root.style.setProperty('--vh-ratio', (W / H).toFixed(4));
    const shown = st.wrap.clientWidth || W;
    const S = Math.max(0.25, Math.min(2, Math.round((shown / W) * 20) / 20));
    const cw = Math.round(W * S), ch = Math.round(H * S);
    if (!st.cv || st.cv.w !== cw || st.cv.h !== ch) {
      st.cv = HGames.ui.canvas(st.wrap, { w: cw, h: ch, cls: 'vh-board' });
      st.statKey = '';
      // кнопки пальця — після полотна: стоячи телефон кладе їх смугою під ним
      if (st.touchEl && st.touchEl.previousElementSibling !== st.cv.el) st.wrap.appendChild(st.touchEl);
    } else st.cv.resize();
    st.S = cw / W;
  }

  function palette(st) {
    const now = performance.now();
    if (st.pal && now - st.palAt < 1000) return st.pal;
    const c = st.ctx.css;
    st.palAt = now;
    st.pal = {
      fire: c('--vh-fire', '#ff8c1a'), fire2: c('--vh-fire2', '#ffd23a'),
      water: c('--vh-water', '#46b4ff'), water2: c('--vh-water2', '#bfe6ff'),
      lava: c('--vh-lava', '#ff5a1f'), mud: c('--vh-mud', '#4f7d23'), mud2: c('--vh-mud2', '#c6f57a'), stone: c('--vh-stone', '#2a3a44'),
      bg: c('--vh-bg', '#101b24'), bg2: c('--vh-bg2', '#1a2a36'), door: c('--vh-door', '#6b7f8f'), box: c('--vh-box', '#9b6b3a'),
      cr: [c('--vh-crystal1', '#7fe0ff'), c('--vh-crystal2', '#c69bff'), c('--vh-crystal3', '#8dff9e')],
      shade: c('--gshade', 'rgba(15, 31, 24, .62)'), ok: c('--ok', '#7bd389'),
    };
    return st.pal;
  }

  function hintText(st, text) {
    const m = st.mode;
    const lr = m === 'pad' ? 'стік' : m === 'touch' ? '◀ ▶' : '← →';
    const jump = m === 'pad' ? 'Ⓐ' : m === 'touch' ? '▲' : '↑ або пробіл';
    const sw = m === 'pad' ? 'Ⓧ' : m === 'touch' ? '⇄' : 'Tab';
    return String(text).replace(/\{left\}\{right\}/g, lr).replace(/\{left\}/g, lr).replace(/\{right\}/g, lr)
      .replace(/\{jump\}/g, jump).replace(/\{switch\}/g, sw);
  }

  function wrapText(g, text, maxW) {
    const words = text.split(' ');
    const lines = [];
    let line = '';
    for (const w of words) {
      const t = line ? line + ' ' + w : w;
      if (g.measureText(t).width > maxW && line) { lines.push(line); line = w; } else line = t;
    }
    if (line) lines.push(line);
    return lines;
  }

  function buildStatic(st) {
    const key = st.lvN + ':' + st.cv.el.width + 'x' + st.cv.el.height + ':' + st.mode;
    if (st.statKey === key && st.stat) return;
    st.statKey = key;
    const L = st.L, W = L.W, H = L.H, pal = palette(st);
    const c = st.stat || document.createElement('canvas');
    c.width = st.cv.el.width;
    c.height = st.cv.el.height;
    st.stat = c;
    const g = c.getContext('2d');
    g.setTransform(c.width / (W * TILE), 0, 0, c.height / (H * TILE), 0, 0);
    // тло печери
    const grad = g.createLinearGradient(0, 0, 0, H * TILE);
    grad.addColorStop(0, pal.bg);
    grad.addColorStop(1, pal.bg2);
    g.fillStyle = grad;
    g.fillRect(0, 0, W * TILE, H * TILE);
    // далекі брили на задній стіні
    g.globalAlpha = 0.07;
    g.fillStyle = '#9fb7c7';
    for (let i = 0; i < 24; i++) {
      const x = hash32(L.n, i * 31) * W * TILE, y = hash32(i * 17, L.n + 3) * H * TILE, r = 18 + hash32(i, L.n * 5) * 50;
      g.beginPath(); g.ellipse(x, y, r * 1.4, r, 0, 0, Math.PI * 2); g.fill();
    }
    g.globalAlpha = 1;
    // кристали
    for (const k of st.crystals) {
      const x = k.c * TILE, y = k.r * TILE;
      g.fillStyle = pal.cr[Math.floor(k.k * 1000) % 3];
      g.shadowColor = g.fillStyle;
      g.shadowBlur = 8;
      g.globalAlpha = 0.75;
      const n = 2 + Math.floor(k.k * 100) % 2;
      for (let i = 0; i < n; i++) {
        const w = 5 + (i * 3) % 5, h = 12 + ((i + k.c) * 7) % 10;
        if (k.side === 'up') {
          const bx = x + 8 + i * 9;
          g.beginPath(); g.moveTo(bx - w / 2, y); g.lineTo(bx + (i - 1) * 2, y + h); g.lineTo(bx + w / 2, y); g.closePath(); g.fill();
        } else {
          const bx = k.side === 'left' ? x : x + TILE, dir = k.side === 'left' ? 1 : -1, by = y + 8 + i * 10;
          g.beginPath(); g.moveTo(bx, by - w / 2); g.lineTo(bx + dir * h, by + (i - 1) * 2); g.lineTo(bx, by + w / 2); g.closePath(); g.fill();
        }
      }
      g.shadowBlur = 0;
      g.globalAlpha = 1;
    }
    // камінь: що глибше в скелі (далі від печери), то темніший — так видно обрис печери
    const depth = new Uint8Array(W * H).fill(9);
    for (let r = 0; r < H; r++)
      for (let cc = 0; cc < W; cc++) if (L.tiles[r * W + cc] !== 1) depth[r * W + cc] = 0;
    for (let pass = 1; pass <= 3; pass++)
      for (let r = 0; r < H; r++)
        for (let cc = 0; cc < W; cc++) {
          const i = r * W + cc;
          if (depth[i] !== 9) continue;
          for (let dr = -1; dr <= 1; dr++)
            for (let dc = -1; dc <= 1; dc++) {
              const rr = r + dr, c2 = cc + dc;
              if (rr >= 0 && rr < H && c2 >= 0 && c2 < W && depth[rr * W + c2] === pass - 1) depth[i] = pass;
            }
        }
    for (let r = 0; r < H; r++)
      for (let cc = 0; cc < W; cc++) {
        if (L.tiles[r * W + cc] !== 1) continue;
        const x = cc * TILE, y = r * TILE, k = hash32(L.n * 131 + r, cc);
        g.fillStyle = pal.stone;
        g.fillRect(x, y, TILE, TILE);
        const dp = depth[r * W + cc];
        if (dp > 1) { g.fillStyle = 'rgba(0,0,0,' + (dp >= 9 ? 0.34 : 0.1 * (dp - 1)).toFixed(2) + ')'; g.fillRect(x, y, TILE, TILE); }
        g.fillStyle = k < 0.5 ? 'rgba(255,255,255,' + (0.035 * k).toFixed(3) + ')' : 'rgba(0,0,0,' + (0.06 * (k - 0.5)).toFixed(3) + ')';
        g.fillRect(x, y, TILE, TILE);
        g.fillStyle = 'rgba(0,0,0,.14)';
        g.beginPath(); g.arc(x + 8 + k * 22, y + 12 + (1 - k) * 16, 2 + k * 2, 0, Math.PI * 2); g.fill();
        if (k > 0.7) { g.beginPath(); g.arc(x + 30 - k * 10, y + 30, 1.6, 0, Math.PI * 2); g.fill(); }
        const above = r > 0 ? L.tiles[(r - 1) * W + cc] : 1;
        if (above === 0) {                         // верх уступу — світлий край і трохи моху
          g.fillStyle = 'rgba(190,220,235,.22)';
          g.fillRect(x, y, TILE, 3);
          if (k > 0.55) { g.fillStyle = 'rgba(120,200,140,.35)'; g.fillRect(x + 4 + k * 10, y, 10, 2); }
        }
        const below = r + 1 < H ? L.tiles[(r + 1) * W + cc] : 1;
        if (below === 0) { g.fillStyle = 'rgba(0,0,0,.25)'; g.fillRect(x, y + TILE - 3, TILE, 3); }
      }
    // дно рідин
    for (let r = 0; r < H; r++)
      for (let cc = 0; cc < W; cc++) {
        const t = L.tiles[r * W + cc];
        if (t < 2) continue;
        g.fillStyle = t === 2 ? '#0d3552' : t === 3 ? '#5a1a08' : '#16280c';
        g.fillRect(cc * TILE, r * TILE, TILE, TILE);
      }
    // рейки ліфтів
    g.strokeStyle = 'rgba(160,180,195,.25)';
    g.lineWidth = 2;
    g.setLineDash([6, 6]);
    for (const f of L.lifts) {
      const x0 = f.col * TILE + f.tiles * TILE / 2, y0 = f.row * TILE + 8, x1 = f.toCol * TILE + f.tiles * TILE / 2, y1 = f.toRow * TILE + 8;
      g.beginPath(); g.moveTo(x0, y0); g.lineTo(x1, y1); g.stroke();
    }
    g.setLineDash([]);
    // арки виходів
    for (let h = 0; h < 2; h++) {
      const [ec, er] = h === 0 ? st.src.exits.fire : st.src.exits.water;
      const x = ec * TILE, y = er * TILE;
      g.fillStyle = 'rgba(0,0,0,.35)';
      g.beginPath();
      g.moveTo(x + 4, y + 2 * TILE); g.lineTo(x + 4, y + 16); g.arc(x + 20, y + 16, 16, Math.PI, 0); g.lineTo(x + 36, y + 2 * TILE);
      g.closePath(); g.fill();
      g.strokeStyle = h === 0 ? pal.fire : pal.water;
      g.lineWidth = 3;
      g.stroke();
      g.font = '14px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(EMO[h], x + 20, y + 20);
    }
    // гнізда кнопок і важелів
    for (const b of L.buttons) {
      g.fillStyle = '#1b262d';
      g.fillRect(b.col * TILE + 2, b.row * TILE + 36, 36, 4);
    }
    for (const l of L.levers) {
      g.fillStyle = '#3a4a55';
      g.beginPath(); g.arc(l.col * TILE + 20, l.row * TILE + 38, 8, Math.PI, 0); g.fill();
    }
    // підказки навчальних рівнів
    for (const hnt of st.src.hints || []) {
      const x = hnt.at[0] * TILE + 4, y = hnt.at[1] * TILE + 4, w = hnt.w * TILE - 8;
      g.font = '700 15px system-ui, sans-serif';
      const lines = wrapText(g, hintText(st, hnt.text), w - 16);
      const hh = 12 + lines.length * 19;
      g.fillStyle = 'rgba(8,16,22,.72)';
      g.strokeStyle = 'rgba(191,230,255,.35)';
      g.lineWidth = 1;
      g.beginPath(); g.roundRect(x, y, w, hh, 8); g.fill(); g.stroke();
      g.fillStyle = '#e8f4fb';
      g.textAlign = 'left';
      g.textBaseline = 'top';
      lines.forEach((ln, i) => g.fillText(ln, x + 8, y + 7 + i * 19));
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Передбачення: крок партії (той самий автомат фаз, що на сервері), журнал, звірка з кадром
  // ---------------------------------------------------------------------------------------------

  function metaSave(st, s) {
    const i = ((s % HIST) + HIST) % HIST;
    st.world.save(st.hist[i]);
    const m = st.histMeta[i];
    m.step = s; m.h = st.world.hash(); m.ph = st.ph; m.pt = st.pt; m.t = st.t; m.d = st.d;
  }

  /// k героя c на кроці s: свого — із журналу натисків, чужого — з останнього кадру (тримається сталим).
  function kFor(st, c, s) {
    if (myHeroes(st).includes(c)) {
      const i = ((s % HIST) + HIST) % HIST;
      if (st.kLogStep[c][i] === s) return st.kLog[c][i];
      return st.sentK[c];
    }
    return st.lastF && st.lastF.w ? st.lastF.w[c * 14 + 4] : 0;
  }

  function wantK(st, c, s) {
    if (!playing(st) || st.ph === PH_CLEAR) return 0;
    // гачок для ботів у перевірках: той самий шлях вводу й передбачення, лише клавіші — з записаного проходження
    if (st.botK) return st.botK(c, s) & 7;
    if (view(st).solo && c !== st.active) return 0;
    if (document.hidden) return 0;
    return (st.held | st.touch) & 7;
  }

  function canSend(st) {
    const now = performance.now();
    while (st.sendTimes.length && now - st.sendTimes[0] > 1000) st.sendTimes.shift();
    if (st.sendTimes.length >= SEND_PER_SEC) return false;
    st.sendTimes.push(now);
    return true;
  }

  /// Один крок уперед. record — це новий крок (не повтор): тоді ж і рішення, що натиснуто, й відправка вводу.
  function advance(st, record) {
    const s = st.stepLocal + 1;
    if (record) {
      for (const c of myHeroes(st)) {
        const want = wantK(st, c, s);
        // на зміну — одразу; тримаю й далі — нагадую раз на HEARTBEAT кроків (інакше сервер вирішить, що я зник)
        if ((want !== st.sentK[c] || (want !== 0 && s - st.lastSendStep[c] >= HEARTBEAT)) && canSend(st)) {
          st.sentK[c] = want;
          st.lastSendStep[c] = s;
          st.ctx.input('in', { n: s, c: c, k: want });
        }
        const i = s % HIST;
        st.kLog[c][i] = st.sentK[c];
        st.kLogStep[c][i] = s;
      }
    }
    const w = st.world;
    switch (st.ph) {
      case PH_READY:
        if (--st.pt <= 0) { st.ph = PH_GO; st.pt = 0; }
        break;
      case PH_GO:
        w.step(kFor(st, 0, s), kFor(st, 1, s));
        st.t++;
        if (w.anyDied()) {
          st.ph = PH_DEAD; st.pt = DEAD; st.d++;
          st.cause = w.Died[0] && w.Died[1] ? 4 : w.deathTile(w.Died[0] ? 0 : 1) === 4 ? 3 : w.Died[0] ? 1 : 2;
        } else if (w.Cleared) { st.ph = PH_CLEAR; st.pt = CLEAR; }
        break;
      case PH_DEAD:
        st.t++;
        if (--st.pt <= 0) { w.reset(true); st.ph = PH_GO; st.pt = 0; st.cause = 0; }
        break;
      default:
        if (st.pt > 0) st.pt--;
        break;
    }
    st.stepLocal = s;
    metaSave(st, s);
  }

  function heroPx(st) {
    const w = st.world;
    return [[w.X[0] / SU, w.Y[0] / SU], [w.X[1] / SU, w.Y[1] / SU]];
  }

  /// Взяти світ із кадру як стан на кроці f.n і наздогнати свій крок наново — зі своїми записаними натисками.
  function adopt(st, f) {
    const before = heroPx(st);
    const hadWorld = st.stepLocal >= 0;
    st.world.load(f.w);
    st.ph = f.ph; st.pt = f.pt; st.t = f.t; st.d = f.d; st.cause = f.dc || 0;
    // свій світ далеко попереду кадру — це вже не передбачення, а розсинхрон: стаємо на кадр, pump дожене сам
    const target = st.stepLocal > f.n + 12 ? f.n : Math.max(st.stepLocal, f.n);
    st.stepLocal = f.n;
    metaSave(st, f.n);
    while (st.stepLocal < target) advance(st, false);
    if (!hadWorld) {
      // зайшли посеред партії (F5, глядач): смерть, що вже йде, показуємо без вибуху й звуку
      st.gemsSeen = st.world.Gems;
      st.dShown = st.d;
      if (st.ph === PH_DEAD) { st.deadText = pickText(st.cause, st.d); st.deadFor = st.d; }
      return;
    }
    phaseFx(st);
    const after = heroPx(st);
    for (let h = 0; h < 2; h++) {
      const o = st.off[h];
      o[0] += before[h][0] - after[h][0];
      o[1] += before[h][1] - after[h][1];
      if (Math.abs(o[0]) > 40 || Math.abs(o[1]) > 40) { o[0] = 0; o[1] = 0; }
    }
  }

  function onFrame(st, f) {
    // кадр іншої партії (минула спроба того самого рівня після «Ще раз») — не наш: інакше крок стрибав би на тисячі
    if (!f || !f.w || !st.world || f.lv !== st.lvN || (f.gi != null && f.gi !== st.gi)) return;
    // глядач бачить, котрого героя веде той, хто грає сам за двох (у гравця — своє, локальне)
    if (!mine(st) && f.a != null) st.active = f.a;
    const now = performance.now();
    // швидкість сервера (кроків за мс): тик не рівно 40 мс (таймер ОС), тож міряємо з пар кадрів, рознесених
    // хоча б на 0,4 с; перший замір беремо цілим, далі згладжуємо
    if (!st.rateRef || f.n < st.rateRef.n) st.rateRef = { n: f.n, at: now, first: !st.rateRef };
    else if (now - st.rateRef.at > 400) {
      const r = (f.n - st.rateRef.n) / (now - st.rateRef.at);
      if (r > 0.02 && r < 0.08) st.rate = st.rateRef.first ? r : st.rate * 0.7 + r * 0.3;
      st.rateRef = { n: f.n, at: now, first: false };
    }
    const fresh = st.lastN < 0 || f.n < st.lastN - 50;
    if (!fresh && f.n < st.lastN) return;          // старий кадр, що заблукав, — ігноруємо
    st.lastF = f;
    st.lastN = f.n;
    st.lastAt = now;
    if (fresh || st.stepLocal < f.n) { adopt(st, f); return; }
    const m = st.histMeta[f.n % HIST];
    if (!(m.step === f.n && m.h === (f.h >>> 0) && m.ph === f.ph && m.pt === f.pt && m.t === f.t && m.d === f.d)) adopt(st, f);
    resendIfLost(st, f);
  }

  /// Квота каркаса мовчки ковтає зайві Input. Якщо сервер застосовує не те, що я тримаю, а мій останній ввід
  /// мав би вже дійти, — нагадаємо.
  function resendIfLost(st, f) {
    if (!playing(st) || f.ph !== PH_GO) return;
    for (const c of myHeroes(st)) {
      const serverK = f.w[c * 14 + 4];
      const i = f.n % HIST;
      const mineK = st.kLogStep[c][i] === f.n ? st.kLog[c][i] : st.sentK[c];
      if (serverK !== mineK && f.n > st.lastSendStep[c] + 8 && canSend(st)) {
        st.lastSendStep[c] = st.stepLocal + 1;
        st.ctx.input('in', { n: st.stepLocal + 1, c: c, k: st.sentK[c] });
      }
    }
  }

  /// Скільки кроків прокрутити зараз, щоб іти на LEAD попереду сервера (глядач — впритул до нього).
  function pump(st, now) {
    if (!st.world || st.lastN < 0) return;
    const dt = Math.min(250, Math.max(0, now - (st.lastNow || now)));
    st.lastNow = now;
    if (now - st.lastAt > 3000) return;                // зв'язок мовчить — стоїмо
    const lead = mine(st) ? LEAD : 0;
    const want = Math.floor(st.lastN + (now - st.lastAt) * st.rate) + lead;
    const cur = st.stepLocal;
    let steps;
    // alpha — яку частку наступного кроку вже «прожито»: героїв і механізми малюємо між двома кроками, інакше
    // ~50 кроків/с на екрані 60 Гц дають візерунок «4,5 px → 4,5 px → 0» і смикання
    if (cur < want - 2) { steps = Math.min(10, want - cur); st.alpha = 1; }
    else if (cur > want + 2) { steps = 0; st.alpha = 1; }
    else {
      st.acc += dt * st.rate;
      steps = Math.floor(st.acc);
      if (cur + steps > want + 2) {
        // забігли наперед — стоїмо на найновішому кроці (alpha = 1), а не відскакуємо назад на попередній
        steps = Math.max(0, want + 2 - cur);
        st.acc = 0.999;
        st.alpha = 1;
      } else {
        st.acc -= steps;
        st.alpha = Math.min(1, Math.max(0, st.acc));
      }
    }
    for (let i = 0; i < steps; i++) { advance(st, true); stepFx(st); }
    if (steps) phaseFx(st);
  }

  // ---------------------------------------------------------------------------------------------
  // Ефекти: частинки, звуки, трясіння — з переходів локального світу
  // ---------------------------------------------------------------------------------------------

  function burst(st, x, y, n, colors, speed, life, grav) {
    const cap = reduced() ? 20 : 160;
    for (let i = 0; i < n; i++) {
      if (st.parts.length >= cap) st.parts.shift();
      const a = Math.random() * Math.PI * 2, v = speed * (0.4 + Math.random() * 0.8);
      st.parts.push({ x, y, vx: Math.cos(a) * v, vy: Math.sin(a) * v - speed * 0.3, life, max: life, c: colors[i % colors.length], g: grav, s: 2 + Math.random() * 2.5 });
    }
  }

  /// Ефекти кожного кроку: іскри бігу, звуки стрибка й приземлення, самоцвіти, «дроти» від кнопок і важелів.
  function stepFx(st) {
    const w = st.world, pal = palette(st);
    const reduce = reduced();
    const me = myHeroes(st);
    for (let h = 0; h < 2; h++) {
      const cx = w.X[h] / SU + 12, feet = w.Y[h] / SU + 36;
      // бігом — іскри Вогника й крапельки Краплі
      if (st.ph === PH_GO && w.Grounded[h] && Math.abs(w.Vx[h]) > 40 && Math.random() < (reduce ? 0.1 : 0.5))
        burst(st, cx - Math.sign(w.Vx[h]) * 8, feet - 4, 1, h === 0 ? [pal.fire2, pal.fire] : [pal.water2, pal.water], 1.2, 22, h === 0 ? -0.03 : 0.12);
      if (w.Grounded[h] && !st.wasGround[h]) { st.landT[h] = 100; if (me.includes(h)) snd.land(); }
      if (w.JumpAge[h] === 1 && me.includes(h)) snd.jump();
      st.wasGround[h] = w.Grounded[h];
    }
    if (w.Gems !== st.gemsSeen) {
      const got = w.Gems & ~st.gemsSeen;
      st.L.gems.forEach((gm, i) => {
        if (got & (1 << i)) {
          burst(st, gm.col * TILE + 20, gm.row * TILE + 20, reduce ? 4 : 12, gm.who === 0 ? [pal.fire2, '#fff'] : [pal.water2, '#fff'], 2.2, 30, 0.02);
          snd.gem();
        }
      });
      st.gemsSeen = w.Gems;
    }
    // кнопку натиснули чи важіль перемкнули — на мить видно «дроти» до всього, що він рухає
    const sig = w.signalMask(), nb = st.L.buttons.length;
    const edge = (sig & ~st.sigWas) | ((sig ^ st.sigWas) & ~((1 << nb) - 1));
    if (edge && st.ph === PH_GO) {
      const now = performance.now();
      for (let i = 0; i < st.wire.length; i++) if (edge & (1 << i)) st.wire[i] = now;
    }
    st.sigWas = sig;
  }

  /// Ефекти переходів фази: смерть і «Разом!». Ловимо саме перехід (кількість смертей, номер партії), а не точний
  /// крок, — тож спрацьовує й тоді, коли смерть чи кінець прийшли кадром сервера, а не з передбачення.
  function phaseFx(st) {
    const w = st.world, pal = palette(st);
    const reduce = reduced();
    if (st.d < st.dShown) st.dShown = st.d;                  // передбачена смерть не справдилась — її ще покажемо
    if (st.ph === PH_DEAD && st.d !== st.dShown) {
      st.dShown = st.d;
      st.deadText = pickText(st.cause, st.d);
      st.deadFor = st.d;
      for (let h = 0; h < 2; h++) {
        if (!w.Died[h] && st.cause !== 5) continue;
        const x = w.X[h] / SU + 12, y = w.Y[h] / SU + 20, tile = w.Died[h] ? w.deathTile(h) : 0;
        burst(st, x, y, reduce ? 10 : 36, h === 0 ? [pal.fire, pal.fire2, '#555'] : [pal.water, pal.water2, '#ddd'], 3, 36, 0.1);
        // пара: Вогник шипить у воді, Крапля википає в лаві — сірі клуби вгору
        if (!reduce && (tile === 2 || tile === 3)) burst(st, x, y - 6, 14, ['#cfd8dc', '#eceff1', '#b0bec5'], 1.1, 60, -0.05);
        if (!reduce && tile === 4) burst(st, x, y + 12, 10, [pal.mud2, pal.mud], 1.4, 40, 0.08);
      }
      if (!reduce) st.shake = 200;
      snd.death();
    }
    if (st.ph === PH_CLEAR && st.clearFor !== st.gi) {
      st.clearFor = st.gi;
      const W = st.L.W * TILE;
      for (let i = 0; i < (reduce ? 20 : 80); i++)
        st.parts.push({ x: Math.random() * W, y: -10 - Math.random() * 60, vx: (Math.random() - 0.5) * 1.5, vy: 1 + Math.random() * 2,
          life: 120, max: 120, c: i % 2 ? pal.fire : pal.water, g: 0.02, s: 3 + Math.random() * 2, conf: true });
      st.banner = { at: performance.now(), text: view(st).solo ? 'Сам за двох!' : 'Разом!', sub: st.d === 0 ? 'Жодної втрати!' : '' };
      snd.clear();
    }
    if (st.ph !== PH_CLEAR) {
      st.banner = null;
      if (st.clearFor === st.gi) st.clearFor = -1;          // передбачене «Разом!» не справдилось
    }
  }

  function pickText(cause, d) {
    const list = DEATH_TEXT[cause] || DEATH_TEXT[5];
    return list[(Math.max(1, d) - 1) % list.length];
  }

  // ---------------------------------------------------------------------------------------------
  // Малювання
  // ---------------------------------------------------------------------------------------------

  function wave(still, tt, x, r) {
    return still ? 0 : 3 * Math.sin(tt / 300 + x / 25 + r);
  }

  function drawLiquids(st, g, pal, tt) {
    const still = reduced();
    for (const s of st.surf) {
      const x0 = s.c0 * TILE, x1 = (s.c1 + 1) * TILE, y = s.r * TILE, yb = y + TILE;
      g.fillStyle = s.t === 2 ? pal.water : s.t === 3 ? pal.lava : pal.mud;
      g.globalAlpha = s.t === 2 ? 0.85 : 1;
      g.beginPath();
      g.moveTo(x0, yb);
      for (let x = x0; x < x1; x += 8) g.lineTo(x, y + 6 + wave(still, tt, x, s.r));
      g.lineTo(x1, y + 6 + wave(still, tt, x1, s.r));
      g.lineTo(x1, yb);
      g.closePath();
      g.fill();
      g.strokeStyle = s.t === 2 ? pal.water2 : s.t === 3 ? pal.fire2 : pal.mud2;
      g.lineWidth = 1.5;
      g.globalAlpha = 0.6;
      g.beginPath();
      g.moveTo(x0, y + 6 + wave(still, tt, x0, s.r));
      for (let x = x0 + 8; x < x1; x += 8) g.lineTo(x, y + 6 + wave(still, tt, x, s.r));
      g.lineTo(x1, y + 6 + wave(still, tt, x1, s.r));
      g.stroke();
      g.globalAlpha = 1;
      // болото світиться отруйним по краю — щоб на загальному плані не читалось як земля
      if (s.t === 4) {
        g.fillStyle = pal.mud2;
        g.globalAlpha = 0.22;
        g.fillRect(x0, y + 8, x1 - x0, 5);
        g.globalAlpha = 1;
      }
      // лава й болото булькають: бульбашка на клітинку раз на ~1,5 с (болото — дві й частіше)
      if (s.t !== 2 && !still)
        for (let c = s.c0; c <= s.c1; c++)
          for (let b = 0; b < (s.t === 4 ? 2 : 1); b++) {
            const ph = ((tt / (s.t === 4 ? 1000 : 1500)) + hash32(c + b * 7, s.r)) % 1;
            if (ph > 0.4) continue;
            g.fillStyle = s.t === 3 ? pal.fire2 : pal.mud2;
            g.globalAlpha = 1 - ph / 0.4;
            g.beginPath(); g.arc(c * TILE + 8 + hash32(s.r + b, c) * 24, y + 12 - ph * 18, 2 + ph * 6, 0, Math.PI * 2); g.fill();
            g.globalAlpha = 1;
          }
    }
    for (const k of st.cells) {
      g.fillStyle = k.t === 2 ? pal.water : k.t === 3 ? pal.lava : pal.mud;
      g.globalAlpha = k.t === 2 ? 0.85 : 1;
      g.fillRect(k.c * TILE, k.r * TILE, TILE, TILE);
      g.globalAlpha = 0.35;
      g.fillStyle = k.t === 2 ? pal.water2 : k.t === 3 ? pal.fire2 : pal.mud2;
      const off = still ? 0 : (tt / 40) % TILE;
      for (let i = 0; i < 3; i++) g.fillRect(k.c * TILE + 6 + i * 12, k.r * TILE + ((off + i * 13) % (TILE - 10)), 2, 10);
      g.globalAlpha = 1;
    }
  }

  /// Значення з попереднього кроку (prev — знімок стану) до поточного на частку a; стрибок понад плитку — без згладжування.
  function lerpSu(prev, idx, cur, a) {
    if (!prev) return cur;
    const p = prev[idx];
    return Math.abs(cur - p) < 640 ? p + (cur - p) * a : cur;
  }

  /// Позиції для малювання — між двома останніми кроками (st.alpha): герої, скрині, двері, ліфти. Фізика не міняється.
  function interp(st) {
    const w = st.world, ip = st.ip, a = st.alpha, s = st.stepLocal;
    const i = (((s - 1) % HIST) + HIST) % HIST;
    const prev = s > 0 && a < 1 && st.histMeta[i].step === s - 1 ? st.hist[i] : null;
    for (let h = 0; h < 2; h++) {
      ip.hx[h] = lerpSu(prev, h * 14, w.X[h], a) / SU;
      ip.hy[h] = lerpSu(prev, h * 14 + 1, w.Y[h], a) / SU;
    }
    for (let b = 0; b < ip.bx.length; b++) {
      ip.bx[b] = lerpSu(prev, ip.oBox + b * 3, w.BoxX[b], a) / SU;
      ip.by[b] = lerpSu(prev, ip.oBox + b * 3 + 1, w.BoxY[b], a) / SU;
    }
    for (let d = 0; d < ip.door.length; d++) ip.door[d] = lerpSu(prev, ip.oDoor + d, w.DoorO[d], a) / SU;
    for (let f = 0; f < ip.lx.length; f++) {
      ip.lx[f] = lerpSu(prev, ip.oLift + f * 2, w.LiftX[f], a) / SU;
      ip.ly[f] = lerpSu(prev, ip.oLift + f * 2 + 1, w.LiftY[f], a) / SU;
    }
  }

  /// Кольорові крапки сигналів (кнопок і важелів) механізму: all — ланцюжком, inv — порожні кружечки з рискою.
  function sigDots(st, g, mask, all, inv, cx, cy) {
    let n = 0;
    for (let i = 0; i < st.sigColor.length; i++) if (mask & (1 << i)) n++;
    let x = cx - (n - 1) * 5;
    if (all && n > 1) {
      g.strokeStyle = 'rgba(255,255,255,.55)';
      g.lineWidth = 1.5;
      g.beginPath(); g.moveTo(x, cy); g.lineTo(x + (n - 1) * 10, cy); g.stroke();
    }
    for (let i = 0; i < st.sigColor.length; i++) {
      if (!(mask & (1 << i))) continue;
      g.beginPath(); g.arc(x, cy, 3.4, 0, Math.PI * 2);
      if (inv) { g.strokeStyle = st.sigColor[i]; g.lineWidth = 1.8; g.stroke(); }
      else { g.fillStyle = st.sigColor[i]; g.fill(); }
      x += 10;
    }
    if (inv) {
      g.strokeStyle = '#ff6b5b';
      g.lineWidth = 1.8;
      g.beginPath(); g.moveTo(cx - n * 5 - 2, cy + 5); g.lineTo(cx + n * 5 + 2, cy - 5); g.stroke();
    }
  }

  function drawMechs(st, g, pal, tt, w, kf) {
    const L = st.L, ip = st.ip;
    // двері: кам'яна плита з рунами, що їде вгору; на перемичці — кольори кнопок/важелів, що їх відчиняють
    for (let i = 0; i < L.doors.length; i++) {
      const d = L.doors[i];
      const x = d.col * TILE, y = d.row * TILE;
      const hpx = d.tiles * TILE - ip.door[i];
      if (hpx > 0) {
        g.fillStyle = pal.door;
        g.fillRect(x + 3, y, TILE - 6, hpx);
        g.fillStyle = 'rgba(0,0,0,.25)';
        g.fillRect(x + 3, y, 3, hpx);
        g.strokeStyle = st.sigColor[firstBit(d.mask)] || '#9fe7ff';
        g.globalAlpha = 0.75;
        g.lineWidth = 1.5;
        for (let ry = y + 14; ry < y + hpx - 6; ry += 22) {
          g.beginPath(); g.moveTo(x + 14, ry); g.lineTo(x + 20, ry + 8); g.lineTo(x + 26, ry); g.stroke();
        }
        g.globalAlpha = 1;
        g.fillStyle = 'rgba(0,0,0,.35)';
        g.fillRect(x + 3, y + hpx - 3, TILE - 6, 3);
      }
      // перемичка лишається й тоді, коли двері відчинені: видно, що тут двері й чиї вони
      g.fillStyle = 'rgba(10,16,22,.8)';
      g.fillRect(x + 1, y, TILE - 2, 8);
      sigDots(st, g, d.mask, d.all, d.inv, x + 20, y + 4);
    }
    // ліфти: металева плита, заклепки — кольорів своїх сигналів
    for (let i = 0; i < L.lifts.length; i++) {
      const f = L.lifts[i];
      const x = ip.lx[i], y = ip.ly[i], wd = f.tiles * TILE;
      g.fillStyle = '#8d9aa4';
      g.fillRect(x, y, wd, 16);
      g.fillStyle = '#c5d0d8';
      g.fillRect(x, y, wd, 3);
      g.fillStyle = '#4a555d';
      for (let rx = x + 8; rx < x + wd; rx += 16) { g.beginPath(); g.arc(rx, y + 10, 2, 0, Math.PI * 2); g.fill(); }
      g.fillStyle = 'rgba(10,16,22,.7)';
      g.fillRect(x + wd / 2 - 22, y + 5, 44, 10);
      sigDots(st, g, f.mask, f.all, f.inv, x + wd / 2, y + 10);
    }
    // кнопки: пластина свого кольору; натиснута — втоплена й світить; сам за двох брама «все разом» — кільце-таймер
    for (let i = 0; i < L.buttons.length; i++) {
      const b = L.buttons[i], v = w.Button[i], on = v !== 0, col = st.sigColor[i];
      const bx = b.col * TILE, by = b.row * TILE;
      g.fillStyle = on ? '#6f8290' : '#a8bac6';
      g.fillRect(bx + 4, by + 32 + (on ? 4 : 0), 32, on ? 4 : 8);
      g.fillStyle = col;
      g.globalAlpha = on ? 1 : 0.8;
      g.fillRect(bx + 6, by + (on ? 36 : 32), 28, 2);
      g.globalAlpha = 1;
      g.fillStyle = on ? col : '#3c4a52';
      g.beginPath(); g.arc(bx + 20, by + 27, 3, 0, Math.PI * 2); g.fill();
      if (v >= 2) {
        g.strokeStyle = col;
        g.lineWidth = 2;
        g.beginPath(); g.arc(bx + 20, by + 27, 7, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * Math.min(1, (v - 1) / SOLO_LATCH)); g.stroke();
      }
    }
    // важелі: палиця хилиться туди, куди перемкнули; кулька — колір сигналу, вогник біля основи — стан
    const nb = L.buttons.length;
    const ease = reduced() ? 1 : 1 - Math.pow(0.75, kf);
    for (let i = 0; i < L.levers.length; i++) {
      const l = L.levers[i];
      const target = w.Lever[i] ? 1 : -1;
      st.leverAng[i] += (target - st.leverAng[i]) * ease;
      const px = l.col * TILE + 20, py = l.row * TILE + 36, a = st.leverAng[i] * 0.6;
      g.strokeStyle = '#d8c9a3';
      g.lineWidth = 3;
      g.lineCap = 'round';
      g.beginPath(); g.moveTo(px, py); g.lineTo(px + Math.sin(a) * 22, py - Math.cos(a) * 22); g.stroke();
      g.fillStyle = st.sigColor[nb + i];
      g.beginPath(); g.arc(px + Math.sin(a) * 22, py - Math.cos(a) * 22, 4.5, 0, Math.PI * 2); g.fill();
      g.fillStyle = w.Lever[i] ? pal.ok : '#e57373';
      g.beginPath(); g.arc(px + (w.Lever[i] ? 9 : -9), py + 1, 2, 0, Math.PI * 2); g.fill();
    }
    // скрині
    for (let i = 0; i < L.boxes.length; i++) {
      const x = ip.bx[i], y = ip.by[i];
      g.fillStyle = pal.box;
      g.fillRect(x + 1, y + 1, 38, 38);
      g.strokeStyle = '#5e3d1d';
      g.lineWidth = 2.5;
      g.strokeRect(x + 2, y + 2, 36, 36);
      g.beginPath(); g.moveTo(x + 4, y + 4); g.lineTo(x + 36, y + 36); g.moveTo(x + 36, y + 4); g.lineTo(x + 4, y + 36); g.stroke();
    }
    // самоцвіти
    const still = reduced();
    for (let i = 0; i < L.gems.length; i++) {
      if (w.Gems & (1 << i)) continue;
      const gm = L.gems[i];
      const cx = gm.col * TILE + 20, cy = gm.row * TILE + 20, r = 10 + (still ? 0 : 2 * Math.sin(tt / 400 + i));
      g.fillStyle = gm.who === 0 ? pal.fire : pal.water;
      g.shadowColor = g.fillStyle;
      g.shadowBlur = 10;
      g.beginPath(); g.moveTo(cx, cy - r); g.lineTo(cx + r * 0.75, cy); g.lineTo(cx, cy + r); g.lineTo(cx - r * 0.75, cy); g.closePath(); g.fill();
      g.shadowBlur = 0;
      g.fillStyle = 'rgba(255,255,255,.7)';
      g.beginPath(); g.moveTo(cx, cy - r * 0.6); g.lineTo(cx + r * 0.3, cy - r * 0.1); g.lineTo(cx, cy); g.closePath(); g.fill();
      if (!still && ((tt / 2000 + i * 0.37) % 1) < 0.08) {
        g.strokeStyle = '#fff';
        g.lineWidth = 1.5;
        g.beginPath(); g.moveTo(cx - 12, cy - 8); g.lineTo(cx - 4, cy - 8); g.moveTo(cx - 8, cy - 12); g.lineTo(cx - 8, cy - 4); g.stroke();
      }
    }
    // виходи світяться, коли свій герой у них; обоє — миготять частіше
    for (let h = 0; h < 2; h++) {
      if (!w.InExit[h]) continue;
      const [ec, er] = h === 0 ? st.src.exits.fire : st.src.exits.water;
      const pulse = 0.35 + 0.35 * Math.sin(tt / (w.InExit[0] && w.InExit[1] ? 60 : 95));
      g.fillStyle = h === 0 ? pal.fire2 : pal.water2;
      g.globalAlpha = pulse;
      g.fillRect(ec * TILE + 6, er * TILE + 8, 28, 72);
      g.globalAlpha = 1;
    }
  }

  const firstBit = (m) => { for (let i = 0; i < 31; i++) if (m & (1 << i)) return i; return 0; };

  /// «Дроти»: щойно кнопку натиснули чи важіль перемкнули — пунктир його кольору до всього, що він рухає (1,4 с).
  function drawWires(st, g, now) {
    const L = st.L, ip = st.ip, nb = L.buttons.length;
    let any = false;
    for (let i = 0; i < st.wire.length; i++) {
      const age = now - st.wire[i];
      if (!st.wire[i] || age > 1400) continue;
      if (!any) { any = true; g.save(); g.lineWidth = 2.5; g.setLineDash([7, 6]); g.lineDashOffset = -now / 40; }
      const src = i < nb ? L.buttons[i] : L.levers[i - nb];
      const sx = src.col * TILE + 20, sy = src.row * TILE + (i < nb ? 30 : 20);
      g.strokeStyle = st.sigColor[i];
      g.globalAlpha = Math.min(1, (1400 - age) / 500) * 0.9;
      g.beginPath();
      for (const d of L.doors) if (d.mask & (1 << i)) { g.moveTo(sx, sy); g.lineTo(d.col * TILE + 20, d.row * TILE + 6); }
      for (let f = 0; f < L.lifts.length; f++)
        if (L.lifts[f].mask & (1 << i)) { g.moveTo(sx, sy); g.lineTo(ip.lx[f] + L.lifts[f].tiles * TILE / 2, ip.ly[f] + 8); }
      g.stroke();
    }
    if (any) g.restore();
  }

  /// Герой. die — як він гине цього кадру: 0 ні, 2/3 — чужа рідина (тане), 4 — болото (тоне, очі — останні); dieK 0…1.
  function drawHero(st, g, pal, tt, h, x, y, w, me, label, die, dieK) {
    let sy = 1;
    if (!reduced() && !die) {
      if (w.Vy[h] < 0 && !w.Grounded[h]) sy = 1.12;
      else if (st.landT[h] > 0) sy = 0.86;
    }
    const cx = x + 12, bottom = y + 36;
    // своя рідина під ногами — «по коліна»
    const L = st.L;
    const tr = Math.floor((w.Y[h] / SU + 36) / TILE), tc = Math.floor(cx / TILE);
    const below = tr >= 0 && tr < L.H && tc >= 0 && tc < L.W ? L.tiles[tr * L.W + tc] : 0;
    const wade = !die && w.Grounded[h] && ((h === 0 && below === 3) || (h === 1 && below === 2)) ? 8 : 0;
    if (me && !die) {
      g.strokeStyle = 'rgba(255,255,255,.75)';
      g.lineWidth = 1.5;
      g.beginPath(); g.ellipse(cx, bottom + 1, 14, 3.5, 0, 0, Math.PI * 2); g.stroke();
    }
    g.save();
    let sink = 0;
    if (die === 4) {
      // болото: повільно тоне — видно лише те, що над поверхнею
      const surf = Math.floor((bottom - 1) / TILE) * TILE + 6;
      g.beginPath(); g.rect(cx - 30, surf - 80, 60, 80); g.clip();
      sink = dieK * 44;
    }
    g.translate(cx, bottom + wade + sink);
    if (die === 2 || die === 3) {
      const k = Math.max(0.05, 1 - dieK);
      g.globalAlpha = k;
      g.scale(k, k);
    } else g.scale(1 / Math.sqrt(sy), sy);
    if (h === 0) {
      // Вогник: полум'я з трьома язиками
      const f = reduced() ? 0 : tt / 90;
      g.fillStyle = die === 2 ? '#9e9e9e' : pal.fire;
      g.beginPath();
      g.moveTo(-15, -6);
      g.quadraticCurveTo(-17, -24, -9 + Math.sin(f) * 2, -38);
      g.quadraticCurveTo(-5, -30, -2, -34);
      g.quadraticCurveTo(1 + Math.sin(f * 1.3) * 2, -48, 5, -40);
      g.quadraticCurveTo(8, -32, 11 + Math.sin(f * 0.8) * 2, -37);
      g.quadraticCurveTo(18, -20, 15, -6);
      g.quadraticCurveTo(13, 0, 0, 0);
      g.quadraticCurveTo(-13, 0, -15, -6);
      g.fill();
      g.fillStyle = die === 2 ? '#cfd8dc' : pal.fire2;
      g.beginPath(); g.ellipse(0, -11, 9, 10, 0, 0, Math.PI * 2); g.fill();
    } else {
      // Крапля: кругле денце, гострий верх, відблиск
      g.fillStyle = die === 3 ? '#e0e6ea' : pal.water;
      g.beginPath();
      g.moveTo(0, -42);
      g.bezierCurveTo(6, -30, 16, -22, 15, -13);
      g.arc(0, -13, 15, 0, Math.PI, false);
      g.bezierCurveTo(-16, -22, -6, -30, 0, -42);
      g.fill();
      g.fillStyle = pal.water2;
      g.globalAlpha *= 0.8;
      g.beginPath(); g.ellipse(-7, -20, 2.5, 5, -0.4, 0, Math.PI * 2); g.fill();
      g.globalAlpha /= 0.8;
    }
    // очі дивляться туди, куди йде; кліпають раз на ~3 с; гине — очі хрестиками чи круглі від жаху
    const look = w.Facing[h] ? 1 : -1;
    const blink = !reduced() && !die && ((tt / 3000 + h * 0.43) % 1) < 0.04;
    for (let e = 0; e < 2; e++) {
      const ex = e ? 5 : -5;
      g.fillStyle = '#fff';
      g.beginPath(); g.ellipse(ex + look * 2, -17, die === 4 ? 4.4 : 3.6, blink ? 0.8 : die === 4 ? 5.4 : 4.6, 0, 0, Math.PI * 2); g.fill();
      if (blink) continue;
      if (die === 2 || die === 3) {
        g.strokeStyle = '#1b2530';
        g.lineWidth = 1.4;
        g.beginPath(); g.moveTo(ex + look * 2 - 2, -19); g.lineTo(ex + look * 2 + 2, -15); g.moveTo(ex + look * 2 + 2, -19); g.lineTo(ex + look * 2 - 2, -15); g.stroke();
      } else {
        g.fillStyle = '#1b2530';
        g.beginPath(); g.arc(ex + (die === 4 ? look * 2 : look * 3.4), die === 4 ? -18.5 : -16.5, die === 4 ? 1.5 : 1.9, 0, Math.PI * 2); g.fill();
      }
    }
    g.restore();
    if (wade) {
      g.fillStyle = h === 0 ? pal.lava : pal.water;
      g.globalAlpha = 0.9;
      g.fillRect(cx - 16, bottom, 32, wade + 2);
      g.globalAlpha = 1;
    }
    if (label) {
      g.font = '700 11px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'bottom';
      g.fillStyle = 'rgba(0,0,0,.6)';
      g.fillText(label, cx + 1, y - 13);
      g.fillStyle = '#fff';
      g.fillText(label, cx, y - 14);
    }
  }

  /// Частинки: рух і життя — за часом (kf = dt / 16,7 мс), тож на 120/144 Гц вони не мчать удвічі швидше.
  function drawParts(st, g, kf) {
    const p = st.parts;
    let j = 0;
    for (let i = 0; i < p.length; i++) {
      const q = p[i];
      q.life -= kf;
      if (q.life <= 0) continue;
      q.x += q.vx * kf; q.y += q.vy * kf; q.vy += q.g * kf;
      if (q.conf) q.vx += Math.sin(q.life / 7) * 0.05 * kf;
      g.globalAlpha = Math.min(1, q.life / (q.max * 0.5));
      g.fillStyle = q.c;
      if (q.conf) g.fillRect(q.x, q.y, q.s, q.s * 1.6);
      else { g.beginPath(); g.arc(q.x, q.y, q.s, 0, Math.PI * 2); g.fill(); }
      p[j++] = q;
    }
    p.length = j;
    g.globalAlpha = 1;
  }

  function drawOverlay(st, g, pal) {
    const W = st.L.W * TILE, H = st.L.H * TILE;
    const v = view(st);
    if (!playing(st)) {
      if (st.ctx.room.status === 'lobby') return;
      g.fillStyle = pal.shade;
      g.fillRect(0, 0, W, H);
      return;
    }
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    if (st.ph === PH_READY) {
      g.fillStyle = pal.shade;
      g.fillRect(0, 0, W, H);
      g.fillStyle = '#fff';
      g.font = '800 96px system-ui, sans-serif';
      g.fillText(String(Math.max(1, Math.ceil((st.pt * STEP_MS) / 1000))), W / 2, H / 2 - 20);
      g.font = '700 24px system-ui, sans-serif';
      let who = 'Дивишся збоку';
      if (mine(st)) who = v.solo ? 'Керуєш обома: ' + hintText(st, '{switch}') + ' перемикає героя' : 'ти — ' + EMO[st.ctx.seat] + ' ' + NAMES[st.ctx.seat];
      g.fillText(who, W / 2, H / 2 + 50);
      g.font = '600 16px system-ui, sans-serif';
      g.fillStyle = 'rgba(255,255,255,.8)';
      g.fillText('Рівень ' + st.lvN + ' «' + (st.src.name || '') + '»', W / 2, H / 2 + 82);
    } else if (st.ph === PH_DEAD && st.deadText && st.deadFor === st.d) {
      g.font = '800 30px system-ui, sans-serif';
      g.fillStyle = 'rgba(0,0,0,.55)';
      g.fillText(st.deadText, W / 2 + 2, 62);
      g.fillStyle = '#fff';
      g.fillText(st.deadText, W / 2, 60);
    }
    if (st.banner) {
      const k = reduced() ? 1 : Math.min(1, (performance.now() - st.banner.at) / 800);
      const sc = 0.4 + 0.6 * (1 - Math.pow(1 - k, 3));
      g.save();
      g.translate(W / 2, H / 2 - 10);
      g.scale(sc, sc);
      g.font = '900 88px system-ui, sans-serif';
      g.lineWidth = 8;
      g.strokeStyle = 'rgba(0,0,0,.5)';
      g.strokeText(st.banner.text, 0, 0);
      const grad = g.createLinearGradient(-200, 0, 200, 0);
      grad.addColorStop(0, pal.fire);
      grad.addColorStop(1, pal.water);
      g.fillStyle = grad;
      g.fillText(st.banner.text, 0, 0);
      if (st.banner.sub) {
        g.font = '700 26px system-ui, sans-serif';
        g.fillStyle = '#fff';
        g.fillText(st.banner.sub, 0, 64);
      }
      g.restore();
    }
  }

  function draw(st) {
    const cv = st.cv;
    if (!cv || !st.L || !st.world) return;
    const t0 = performance.now();
    const dt = Math.min(100, Math.max(0, t0 - (st.lastDraw || t0)));
    st.lastDraw = t0;
    const kf = dt / 16.67;
    buildStatic(st);
    if (!playing(st)) st.alpha = 1;
    interp(st);
    const g = cv.ctx, pal = palette(st), tt = t0, ip = st.ip;
    const W = st.L.W * TILE, H = st.L.H * TILE;
    g.save();
    g.scale(st.S, st.S);
    if (st.shake > 0) {
      st.shake -= dt;
      g.translate((Math.random() - 0.5) * 8, (Math.random() - 0.5) * 8);
    }
    g.drawImage(st.stat, 0, 0, W, H);
    const w = st.world;
    drawLiquids(st, g, pal, tt);
    drawMechs(st, g, pal, tt, w, kf);
    drawWires(st, g, t0);
    // герої: позиція між кроками + плавний зсув після виправлення, що згасає (за часом, не за кадрами)
    const dec = Math.pow(0.75, kf);
    for (let h = 0; h < 2; h++) {
      const o = st.off[h];
      o[0] *= dec; o[1] *= dec;
      if (Math.abs(o[0]) < 0.05) o[0] = 0;
      if (Math.abs(o[1]) < 0.05) o[1] = 0;
      if (st.landT[h] > 0) st.landT[h] -= dt;
    }
    const v = view(st);
    const me = myHeroes(st);
    const labels = playing(st) && !v.solo && (st.ph === PH_READY || st.ph === PH_DEAD);
    const first = v.solo && st.active === 0 ? 1 : 0;
    const dying = playing(st) && st.ph === PH_DEAD;
    for (let n = 0; n < 2; n++) {
      const h = n === 0 ? first : 1 - first;
      let die = 0, dieK = 0;
      if (dying && w.Died[h]) {
        die = w.deathTile(h);
        dieK = 1 - st.pt / DEAD;
        if (!die || reduced()) continue;                    // без анімацій — просто зник у спалаху
      }
      const x = ip.hx[h] + st.off[h][0], y = ip.hy[h] + st.off[h][1];
      const isMe = me.includes(h) && (!v.solo || h === st.active);
      let label = '';
      if (labels && !die) label = me.includes(h) && st.ph === PH_READY ? 'ти' : (st.ctx.nickOf(h) || '');
      drawHero(st, g, pal, tt, h, x, y, w, isMe, label, die, dieK);
    }
    drawParts(st, g, kf);
    drawOverlay(st, g, pal);
    g.restore();
    st.drawMs[st.drawI] = performance.now() - t0;
    st.drawI = (st.drawI + 1) % st.drawMs.length;
    if (st.drawN < st.drawMs.length) st.drawN++;
  }

  // ---------------------------------------------------------------------------------------------
  // HUD, мапа рівнів, підсумок
  // ---------------------------------------------------------------------------------------------

  function hud(st) {
    const el = st.hudEl;
    if (!el) return;
    const now = performance.now();
    if (now - st.hudAt < 100) return;
    st.hudAt = now;
    const ctx = st.ctx, v = view(st), w = st.world;
    let html = '';
    for (let i = 0; i < 2; i++) {
      const nick = ctx.nickOf(i) || (v.solo ? ctx.nickOf(1 - i) : '');
      const act = v.solo && playing(st) && st.active === i ? ' act' : '';
      html += '<span class="vh-chip vh-h' + i + act + (ctx.seat === i ? ' me' : '') + '">' + EMO[i] + ' ' + ctx.esc(nick || NAMES[i]) + '</span>';
    }
    if (st.L && w && ctx.room.status !== 'lobby') {
      for (let who = 0; who < 2; who++) {
        let got = 0, tot = 0;
        st.L.gems.forEach((gm, i) => { if (gm.who === who) { tot++; if (w.Gems & (1 << i)) got++; } });
        html += '<span class="vh-chip vh-gem vh-h' + who + '">💎 ' + got + '/' + tot + '</span>';
      }
      html += '<span class="vh-chip vh-clock">⏱ ' + clock(st.t * STEP_MS) + '</span>';
      // «заново» скидає рівень обом — тому не випадковим натиском: клавішею R — утримати, мишею — двічі, пальцем — довго
      const armed = st.resetArm > now;
      html += '<button type="button" class="vh-chip vh-dead' + (st.rTimer ? ' hold' : '') + (armed ? ' armed' : '') + '" data-vh="reset" title="'
        + (HGames.ui.coarse() ? 'Потримай, щоб почати рівень заново' : 'Почати рівень заново: утримай R або клацни двічі') + '"'
        + (mine(st) && playing(st) ? '' : ' disabled') + '>☠ ' + (armed ? 'Заново?' : st.d) + '</button>';
    }
    html += '<button type="button" class="vh-chip vh-snd" data-vh="mute" data-pad-skip aria-label="звук">' + (snd.muted ? '🔇' : '🔈') + '</button>';
    if (mine(st) && playing(st)) html += '<button type="button" class="vh-chip vh-give" data-vh="giveup">' + (st.giveupArm > now ? 'Точно здатись?' : 'Здатись') + '</button>';
    if (html !== st.hudSig) { st.hudSig = html; el.innerHTML = html; }
  }

  /// Перша літера ніка для підпису малих зірок партнера («гість Петро» → «П»: беремо останнє слово).
  const initial = (nick) => { const w = String(nick || '').trim().split(/\s+/); return Array.from(w[w.length - 1] || '')[0] || ''; };

  /// Хто зараз господар — мій нік?
  const amHost = (ctx) => !!(ctx.room.host && ctx.me && ctx.room.host.toLowerCase() === (ctx.me.nick || '').toLowerCase());

  /// Мапа рівнів. mini — для підсумку поверх полотна (номер і зірки; назва — у підказці).
  function levelsHtml(st, mini) {
    const ctx = st.ctx, v = view(st);
    const lobby = ctx.room.status === 'lobby';
    // у лобі обирає господар; після партії — будь-хто з сидячих («Ще раз» з обраним рівнем)
    const canPick = ctx.mine && (lobby ? amHost(ctx) : ctx.room.status === 'finished');
    const other = ctx.seat === 0 ? 1 : 0;
    const otherNick = ctx.seat != null ? ctx.nickOf(other) : null;
    const res = v.result;
    let html = '<div class="vh-grid' + (mini ? ' mini' : '') + '">';
    for (const l of v.levels || []) {
      const my = ctx.seat != null ? l.stars[ctx.seat] : null, ot = otherNick ? l.stars[other] : null;
      const sel = mini ? res && l.n === res.level : l.n === v.picked;
      const tip = '«' + l.name + '» · 💎 ' + l.gems[0] + ' 🔥 + ' + l.gems[1] + ' 💧 · ⏱ ' + clock(l.par) + ' — встигнете, і буде третя зірка'
        + (l.best ? ' · ваш рекорд ' + clockTenths(l.best.ms) : '') + (canPick ? '' : ' · обирає господар');
      html += '<button type="button" class="vh-lv' + (sel ? ' sel' : '') + (l.unlocked ? '' : ' lock') + '" data-lv="' + l.n + '"'
        + (canPick && l.unlocked ? '' : ' disabled') + (sel && !mini ? ' data-pad-first' : '') + ' title="' + ctx.esc(tip) + '">'
        + '<b>' + l.n + '</b>';
      if (mini) {
        html += '<span class="vh-st">' + (l.unlocked ? '<i class="big">' + stars(my || 0) + '</i>' : '🔒') + '</span></button>';
        continue;
      }
      html += '<span class="vh-nm">' + ctx.esc(l.name) + '</span>'
        + (l.unlocked
          ? '<span class="vh-st">' + (my != null ? '<i class="big" title="твої зірки">' + stars(my || 0) + '</i>' : '')
            + (ot != null ? '<i class="small" title="зірки: ' + ctx.esc(otherNick) + '">' + ctx.esc(initial(otherNick)) + ' ' + stars(ot || 0) + '</i>' : '') + '</span>'
          : '<span class="vh-st">🔒</span>')
        + '<span class="vh-gm">💎 ' + l.gems[0] + '+' + l.gems[1] + ' · ⏱ ' + clock(l.par) + '</span>'
        + (l.best ? '<span class="vh-bs">' + clockTenths(l.best.ms) + ' · ' + ctx.esc(l.best.nicks) + '</span>' : '')
        + '</button>';
    }
    html += '</div>';
    if (!mini) html += '<div class="vh-legend muted small">★ пройшли · ★ усі 💎 · ★ встигли за ⏱ · великі зірки — твої, малі — партнера</div>';
    return html;
  }

  function bestHtml(st, n) {
    const b = st.best[n];
    if (!b || !b.rows) return '<div class="vh-best muted small">Завантажую рекорди…</div>';
    if (!b.rows.length) return '<div class="vh-best"><h4>Найкращі на рівні ' + n + '</h4><div class="muted small">Ще ніхто не пройшов — будьте першими</div></div>';
    let html = '<div class="vh-best"><h4>Найкращі на рівні ' + n + '</h4><ol>';
    for (const r of b.rows) html += '<li><span>' + st.ctx.esc(r.nicks) + (r.solo ? ' <i class="muted">сам</i>' : '') + '</span><span>' + clockTenths(r.ms) + '</span><span>☠ ' + r.deaths + '</span><span class="vh-stars">' + stars(r.stars) + '</span></li>';
    return html + '</ol></div>';
  }

  function loadBest(st, n) {
    const b = st.best[n];
    if (b && performance.now() - b.at < 30000) return;
    st.best[n] = { at: performance.now(), rows: b ? b.rows : null };
    fetch('/api/games/vohnyk/best?level=' + n, { credentials: 'same-origin' })
      .then((r) => (r.ok ? r.json() : null))
      .then((j) => { if (j) { st.best[n] = { at: performance.now(), rows: j.rows || [] }; panel(st); } })
      .catch(() => {});
  }

  /// Підсумок партії поверх полотна: час, зірки, хто скільки разів загинув, і що далі — наступний, цей же чи будь-який.
  function resultHtml(st) {
    const v = view(st), r = v.result;
    if (!r) return '';
    const ctx = st.ctx;
    const lv = (v.levels || [])[r.level - 1];
    const name = lv ? ' «' + ctx.esc(lv.name) + '»' : '';
    const by = r.deathsBy || [0, 0];
    const deaths = r.deaths ? ' · ☠ ' + r.deaths + (by[0] + by[1] ? ' <span class="muted">(🔥 ' + by[0] + ' · 💧 ' + by[1] + ')</span>' : '') : ' · без жодної втрати';
    let html = r.cleared
      ? '<div class="vh-res ok"><b>' + (v.solo ? 'Сам за двох!' : 'Разом!') + '</b> Рівень ' + r.level + name + ' за ' + clockTenths(r.ms)
        + ' <span class="vh-stars">' + stars(r.stars) + '</span> · 💎 ' + r.gems + '/' + r.gemsAll + deaths
      : '<div class="vh-res"><b>Не дограли</b> рівень ' + r.level + name + deaths;
    // останній рівень печери — не просто «Ще раз 15-й»: сказати, що пройдено все й що далі (прохід 28.09)
    if (r.cleared && r.level === (v.levels || []).length)
      html += '<div class="small">🏆 Овва — усю печеру пройдено! Далі — на час: побийте свій рекорд на будь-якому рівні</div>';
    if (ctx.mine) {
      const nextLv = (v.levels || [])[r.next - 1];
      html += '<div class="vh-acts">';
      if (r.cleared && r.next !== r.level && nextLv && nextLv.unlocked)
        html += '<button type="button" class="primary" data-again="' + r.next + '">▶ Рівень ' + r.next + ' «' + ctx.esc(nextLv.name) + '»</button>';
      html += '<button type="button"' + (r.cleared && r.next !== r.level ? '' : ' class="primary"') + ' data-again="' + r.level + '">↻ Ще раз ' + r.level + '-й</button>';
      html += '</div><div class="small">…або будь-який відчинений на мапі:</div>' + levelsHtml(st, true);
    }
    return html + '</div>';
  }

  function put(el, html, key) {
    el.hidden = !html;
    if (el['_' + key] !== html) { el['_' + key] = html; el.innerHTML = html; }
  }

  function panel(st) {
    if (!st.pickEl) return;
    const ctx = st.ctx, v = view(st);
    const status = ctx.room.status;
    let pick = '', best = '', over = '';
    if (status === 'lobby') {
      const n = v.picked || 1;
      loadBest(st, n);
      pick = levelsHtml(st, false);
      best = bestHtml(st, n);
    } else if (status === 'finished') {
      const n = (v.result && v.result.level) || v.picked || 1;
      loadBest(st, n);
      over = resultHtml(st) + bestHtml(st, n);
    }
    put(st.pickEl, pick, 'sig');
    put(st.bestEl, best, 'sig');
    put(st.overEl, over, 'sig');
  }

  /// Після партії: «Ще раз» каркаса (місця обертаються, Round++) і тут же обраний рівень — сервер перезапускає
  /// відлік уже з ним. Так і «далі», і «ще раз цей», і будь-який з мапи — без нового столу.
  async function again(st, level) {
    if (st.againBusy) return;
    st.againBusy = true;
    try {
      const id = st.ctx.room.id;
      const r = await HGames.call('Rematch', id);
      if (r && r.ok && level) await HGames.call('Act', id, 'pick', { level });
    } finally { st.againBusy = false; }
  }

  // ---------------------------------------------------------------------------------------------
  // Керування
  // ---------------------------------------------------------------------------------------------

  const KEY = { ArrowLeft: KL, KeyA: KL, ArrowRight: KR, KeyD: KR, Space: KJ, ArrowUp: KJ, KeyW: KJ };

  function switchHero(st) {
    if (!view(st).solo || !mine(st) || !playing(st)) return;
    st.active = 1 - st.active;
    // старий відпускає, новий бере те, що затиснуто — з найближчого кроку (старий шлемо першим). Новому — завжди,
    // навіть без клавіш: так сервер знає, кого я веду, і глядач бачить перемикання одразу
    for (const c of [1 - st.active, st.active]) {
      const want = wantK(st, c, st.stepLocal + 1);
      if ((want !== st.sentK[c] || c === st.active) && canSend(st)) {
        st.sentK[c] = want;
        st.lastSendStep[c] = st.stepLocal + 1;
        st.ctx.input('in', { n: st.stepLocal + 1, c: c, k: want });
      }
    }
    st.hudAt = 0;
  }

  function releaseAll(st) {
    st.held = 0;
    st.touch = 0;
    if (st.rTimer) { clearTimeout(st.rTimer); st.rTimer = 0; }
    if (st.touchEl) st.touchEl.querySelectorAll('.on').forEach((b) => b.classList.remove('on'));
  }

  /// Вкладку сховали чи сторінку закривають (F5) — одразу кажемо серверу, що клавіші відпущено: rAF уже не крутиться,
  /// і без цього герой бігав би з затиснутою клавішею, доки сервер сам не вирішить, що мене нема.
  function sendRelease(st) {
    releaseAll(st);
    if (!mine(st) || !playing(st) || st.stepLocal < 0) return;
    for (const c of myHeroes(st)) {
      if (st.sentK[c] === 0) continue;
      st.sentK[c] = 0;
      st.lastSendStep[c] = st.stepLocal + 1;
      st.ctx.input('in', { n: st.stepLocal + 1, c: c, k: 0 });
    }
  }

  function giveUp(st) {
    const now = performance.now();
    if (st.giveupArm > now) { st.giveupArm = 0; st.ctx.act('giveup'); }
    else st.giveupArm = now + 3000;
    st.hudAt = 0;
    hud(st);
  }

  function buildTouch(st) {
    const el = document.createElement('div');
    el.className = 'vh-touch';
    el.innerHTML = '<div class="vh-tl"><button type="button" data-k="1" aria-label="ліворуч">◀</button><button type="button" data-k="2" aria-label="праворуч">▶</button></div>'
      + '<button type="button" class="vh-sw" data-sw aria-label="інший герой">⇄</button>'
      + '<button type="button" class="vh-tj" data-k="4" aria-label="стрибок">▲</button>';
    const pid = {};
    el.addEventListener('pointerdown', (e) => {
      const b = e.target.closest('button');
      if (!b) return;
      e.preventDefault();
      snd.wake();
      st.mode = 'touch';
      if (b.hasAttribute('data-sw')) { switchHero(st); return; }
      const k = +b.dataset.k;
      try { b.setPointerCapture(e.pointerId); } catch (_) { /* старий браузер */ }
      pid[e.pointerId] = k;
      st.touch |= k;
      b.classList.add('on');
    });
    const up = (e) => {
      const k = pid[e.pointerId];
      if (!k) return;
      delete pid[e.pointerId];
      st.touch &= ~k;
      const b = el.querySelector('[data-k="' + k + '"]');
      if (b) b.classList.remove('on');
    };
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
    el.addEventListener('contextmenu', (e) => e.preventDefault());
    return el;
  }

  // ---------------------------------------------------------------------------------------------
  // Каркас картки
  // ---------------------------------------------------------------------------------------------

  function build(root, st) {
    root.classList.add('vh');
    root.innerHTML = '<div class="vh-hud"></div><div class="vh-turn">🔄 Поверни телефон боком — так видно більше</div>'
      + '<div class="vh-stage"><div class="vh-wrap"><div class="vh-over" hidden></div></div><div class="vh-bestbox" hidden></div></div>'
      + '<div class="vh-pick" hidden></div>';
    st.hudEl = root.querySelector('.vh-hud');
    st.wrap = root.querySelector('.vh-wrap');
    st.pickEl = root.querySelector('.vh-pick');
    st.overEl = root.querySelector('.vh-over');
    st.bestEl = root.querySelector('.vh-bestbox');
    st.touchEl = buildTouch(st);
    st.wrap.appendChild(st.touchEl);
    st.hudEl.addEventListener('click', (e) => {
      const b = e.target.closest('[data-vh]');
      if (!b || b.disabled) return;
      snd.wake();
      if (b.dataset.vh === 'mute') {
        snd.muted = !snd.muted;
        try { localStorage.setItem('vohnykMute', snd.muted ? '1' : '0'); } catch (_) { /* приватне вікно */ }
        if (!snd.muted) snd.wake();
        st.hudAt = 0;
        hud(st);
      } else if (b.dataset.vh === 'reset') {
        // мишею — двома клацаннями за 3 с (перше лише питає «Заново?»); пальцем — довгим дотиком нижче
        if (HGames.ui.coarse()) return;
        const now = performance.now();
        if (st.resetArm > now) { st.resetArm = 0; st.ctx.act('reset'); } else st.resetArm = now + 3000;
        st.hudAt = 0;
        hud(st);
      } else if (b.dataset.vh === 'giveup') giveUp(st);
    });
    // на пальці «заново» — лише довгим дотиком до ☠: випадковий тап не має скидати рівень
    st.hudEl.addEventListener('pointerdown', (e) => {
      const b = e.target.closest('[data-vh="reset"]');
      if (!b || b.disabled || !HGames.ui.coarse()) return;
      clearTimeout(st.pressT);
      b.classList.add('hold');
      st.pressT = setTimeout(() => { b.classList.remove('hold'); st.ctx.act('reset'); }, 600);
    });
    const cancel = () => { clearTimeout(st.pressT); st.hudEl.querySelectorAll('.hold').forEach((x) => x.classList.remove('hold')); };
    st.hudEl.addEventListener('pointerup', cancel);
    st.hudEl.addEventListener('pointercancel', cancel);
    st.hudEl.addEventListener('pointerleave', cancel);
    st.pickEl.addEventListener('click', (e) => {
      const b = e.target.closest('[data-lv]');
      if (!b || b.disabled) return;
      st.ctx.act('pick', { level: +b.dataset.lv });
    });
    // підсумок: «▶ далі», «↻ ще раз цей» і плитки мапи — нова партія з обраним рівнем
    st.overEl.addEventListener('click', (e) => {
      const b = e.target.closest('[data-again], [data-lv]');
      if (!b || b.disabled) return;
      snd.wake();
      again(st, +(b.dataset.again || b.dataset.lv));
    });
    // клік чи дотик по іншому героєві — перемкнутись (сам за двох)
    st.wrap.addEventListener('pointerdown', (e) => {
      if (!st.cv || e.target !== st.cv.el) return;
      snd.wake();
      if (!view(st).solo || !mine(st) || !playing(st) || !st.world) return;
      const r = st.cv.el.getBoundingClientRect();
      const x = ((e.clientX - r.left) / r.width) * st.L.W * TILE, y = ((e.clientY - r.top) / r.height) * st.L.H * TILE;
      const o = 1 - st.active, w = st.world;
      if (Math.abs(x - (w.X[o] / SU + 12)) < 30 && Math.abs(y - (w.Y[o] / SU + 18)) < 34) switchHero(st);
    });
    st.keyup = (e) => {
      const k = KEY[e.code];
      if (k) st.held &= ~k;
      if (e.code === 'Tab' || e.code === 'KeyQ') st.switchHeld = false;
      if (e.code === 'KeyR' && st.rTimer) { clearTimeout(st.rTimer); st.rTimer = 0; st.hudAt = 0; }
    };
    document.addEventListener('keyup', st.keyup);
    st.blur = () => releaseAll(st);
    window.addEventListener('blur', st.blur);
    st.vis = () => { if (document.hidden) sendRelease(st); st.lastNow = 0; };
    document.addEventListener('visibilitychange', st.vis);
    st.hide = () => sendRelease(st);
    window.addEventListener('pagehide', st.hide);
    if (window.ResizeObserver) {
      st.ro = new ResizeObserver(() => {
        fit(st);
        wake(st, true);
        // телефон лежачи: ⛶ чи поворот міняють розмір — полотно знову між шапкою й вкладками
        if (playing(st) && phoneLandscape()) { clearTimeout(st.centreT); st.centreT = setTimeout(() => centre(st), 120); }
      });
      st.ro.observe(st.wrap);
    }
    st.mode = HGames.ui.coarse() ? 'touch' : (window.HPad && window.HPad.pads > 0) ? 'pad' : 'kb';
  }

  function apply(root, ctx) {
    const st = state(root, ctx);
    if (!st.hudEl) build(root, st);
    const v = ctx.view || {};
    root.classList.toggle('vh-seated', !!ctx.mine);
    root.classList.toggle('vh-solo', !!v.solo);
    root.classList.toggle('vh-lobby', ctx.room.status === 'lobby');
    root.classList.toggle('vh-play', ctx.room.status === 'playing');
    root.classList.toggle('vh-done', ctx.room.status === 'finished');
    if (!window.VohnykSim) { withSim(() => { if (root._vh) apply(root, root._vh.ctx); }); return; }
    // нова партія (у тому числі «Ще раз» на той самий рівень) — нове передбачення з нуля
    const gi = v.gi != null ? v.gi : 0;
    if (v.level && (!st.world || v.level.n !== st.lvN || gi !== st.gi)) setLevel(st, v.level);
    else if (v.level) st.src = v.level;
    st.gi = gi;
    if (st.world) st.world.solo = !!v.solo;
    if (v.solo && !st.soloInit) { st.active = ctx.seat != null ? ctx.seat : v.active || 0; st.soloInit = true; }
    if (!v.solo) st.soloInit = false;
    if (ctx.room.status !== 'playing' && st.world) {
      if (ctx.room.status === 'lobby') {
        // лобі: рівень стоїть на старті — таким його й покажемо
        st.world.reset(false);
        st.ph = PH_READY; st.pt = READY; st.t = 0; st.d = 0; st.banner = null;
      }
      st.lastN = -1;
      st.stepLocal = -1;
      st.sentK[0] = st.sentK[1] = 0;
      releaseAll(st);
    }
    const cf = ctx.frame;
    const f = cf && cf.lv === st.lvN && cf.gi === st.gi ? cf : v.f;
    if (f && playing(st) && (st.lastN < 0 || f.n > st.lastN)) onFrame(st, f);
    // телефон лежачи: на старті партії підкрутити сторінку так, щоб видно було і рядок над полем, і полотно
    if (playing(st) && !st.centred && phoneLandscape()) { st.centred = true; setTimeout(() => centre(st), 60); }
    if (!playing(st)) st.centred = false;
    // партію дограно: показуємо світ таким, яким він був наприкінці (і після F5 теж)
    if (ctx.room.status === 'finished' && st.world && v.f && v.f.lv === st.lvN && v.f.w) {
      st.world.load(v.f.w);
      st.t = v.f.t; st.d = v.f.d;
      st.gemsSeen = st.world.Gems;
    }
    fit(st);
    st.hudAt = 0;
    hud(st);
    panel(st);
    wake(st, true);
    spin(st);
  }

  const phoneLandscape = () => !!(window.matchMedia && window.matchMedia('(pointer: coarse) and (orientation: landscape) and (max-height: 500px)').matches);

  /// Телефон лежачи: прокрутити так, щоб між шапкою сайту й вкладками стали рядок над полем і полотно (а як не
  /// вміщаються обоє — хоч полотно посередині).
  function centre(st) {
    try {
      const cs = getComputedStyle(document.body);
      const px = (v) => parseFloat(cs.getPropertyValue(v)) || 0;
      const head = document.querySelector('header');
      const top = head ? Math.max(0, head.getBoundingClientRect().bottom) : 0;
      const bottom = window.innerHeight - px('--tabs-h') - px('--mini-h');
      const r = st.wrap.getBoundingClientRect();
      const hudTop = st.hudEl.getBoundingClientRect().top;
      if (r.bottom - hudTop <= bottom - top) window.scrollBy(0, hudTop - top - 2);
      else window.scrollBy(0, r.top - (top + Math.max(0, (bottom - top - r.height) / 2)));
    } catch (_) { /* без прокрутки теж можна грати */ }
  }

  /// rAF — лише поки треба (прохід 28.09): схована картка чи вкладка — перевірка раз на 0,2 с без rAF; лобі й
  /// підсумок, де вже не летять іскри й не трусить, — 20 кадрів/с (хвилі рідин і так повільні); вид і розмір будять
  /// одразу (wake). Було 60 повних кадрів/с усюди, і порожні колбеки в схованій картці.
  function spin(st) {
    if (st.raf || st.idleT) return;
    const loop = (now) => {
      st.raf = 0;
      if (!st.root.isConnected || st.root._vh !== st) return;
      const shown = !document.hidden && !!st.cv && !!st.cv.el.offsetParent;
      st.away = !shown;
      if (shown) {
        if (playing(st)) pump(st, now);
        draw(st);
        hud(st);
      } else st.lastNow = 0;
      const gap = !shown ? AWAY_MS : !playing(st) && !st.parts.length && !(st.shake > 0) ? CALM_MS : 0;
      if (gap) st.idleT = setTimeout(() => { st.idleT = 0; st.raf = requestAnimationFrame(loop); }, gap);
      else st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  /// Цикл дрімає — розбудити зараз; force — і тоді, коли картка була схована (кадри в схованій будити не мусять).
  function wake(st, force) {
    if (!st || !st.idleT || (st.away && !force)) return;
    clearTimeout(st.idleT);
    st.idleT = 0;
    spin(st);
  }

  HGames.register({
    id: 'vohnyk',
    added: '2026-09-27',               // нова гра хвилі 2: плитка світиться «🆕» два тижні тим, хто ще не грав
    icon: ICON,
    seatNames: NAMES,
    seatClass: ['vhf', 'vhw'],
    pad: {
      dirs: 'x',
      a: 'Space',
      // сам за двох героя перемикає Ⓧ (чи LB/RB); Ⓨ лишається підказками каркаса
      on(btn, ctx) {
        const st = ctx._vh;
        if (!st) return false;
        st.mode = 'pad';
        if (view(st).solo && (btn === 'x' || btn === 'lb' || btn === 'rb')) { switchHero(st); return true; }
        return false;
      },
      hint: '{dpad} бігти · {a} стрибок · {x} інший герой (сам за двох)',
    },
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Вогник і Крапля',
      items: [
        '🔥💧 Кооп-платформер на двох: Вогник боїться води, Крапля — лави, обоє — болота',
        '🔘 Кнопки тримають двері й ліфти, поки на них стоїш; важелі перемикаються, коли проходиш крізь них. Що що відчиняє — видно за кольором',
        '📦 Скрині штовхаються й тонуть у болоті — виходить місток',
        '💎 Самоцвіти свого кольору й двоє дверей: обоє у своїх — «Разом!». Зірки за час і самоцвіти',
        '🧍 Сам? Керуй обома — Tab (Ⓧ на джойстику) перемикає героя. Після рівня — «далі», «ще раз цей» або будь-який на мапі',
      ],
    },

    mount(root, ctx) {
      state(root, ctx);
      apply(root, ctx);
    },

    update(root, ctx) {
      apply(root, ctx);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!st.world || !playing(st)) return;
      onFrame(st, f);
      wake(st);
    },

    onKey(e, ctx) {
      const st = ctx._vh;
      if (!st || !ctx.mine || !ctx.playing) return false;
      snd.wake();
      st.mode = e.hpad ? 'pad' : 'kb';
      if (e.code === 'Tab' || e.code === 'KeyQ') {
        // удвох Tab не наш — хай браузер переводить фокус як завжди
        if (!view(st).solo) return false;
        if (!st.switchHeld) { st.switchHeld = true; switchHero(st); }
        return true;
      }
      if (e.code === 'KeyR') {
        // «заново» скидає рівень обом: треба утримати R пів секунди, випадковий дотик поруч із WASD нічого не зробить
        if (!e.repeat && !st.rTimer) {
          st.rTimer = setTimeout(() => { st.rTimer = 0; st.hudAt = 0; if (playing(st)) st.ctx.act('reset'); }, 500);
          st.hudAt = 0;
        }
        return true;
      }
      const k = KEY[e.code];
      if (!k) return false;
      st.held |= k;
      return true;
    },

    status(ctx) {
      const st = ctx._vh;
      const v = ctx.view || {};
      const r = ctx.room;
      if (r.status === 'lobby') {
        if (!ctx.mine) return 'Дивишся збоку';
        return amHost(ctx) ? 'Обери рівень і тисни «Почати»' : 'Господар обирає рівень';
      }
      if (r.status === 'finished') {
        const res = v.result;
        if (!res) return '';
        if (!ctx.mine) return res.cleared ? 'Пройшли рівень ' + res.level : 'Не дограли';
        if (res.cleared) return res.next !== res.level ? 'Пройдено! «Ще раз» — рівень ' + res.next + ', або обери будь-який на мапі' : 'Усі 15 пройдено! «Ще раз» — знову 15-й, або обери на мапі';
        return '«Ще раз» — той самий рівень, або обери інший на мапі';
      }
      if (!st || !st.world || !st.L) return 'Готуйсь…';
      if (st.lastN >= 0 && performance.now() - st.lastAt > 3000) return 'Зв\'язок…';
      if (st.ph === PH_READY) {
        if (!ctx.mine) return 'Готуйсь…';
        return v.solo ? 'Готуйсь… керуєш обома, ' + hintText(st, '{switch}') + ' перемикає' : 'Готуйсь… ти — ' + EMO[ctx.seat] + ' ' + NAMES[ctx.seat];
      }
      let got = 0;
      for (let i = 0; i < st.L.gems.length; i++) if (st.world.Gems & (1 << i)) got++;
      if (st.ph === PH_CLEAR) {
        const ms = st.t * STEP_MS;
        const s = 1 + (got === st.L.gems.length ? 1 : 0) + (ms <= (st.src.par || 0) ? 1 : 0);
        return (v.solo ? 'Сам за двох! ' : 'Разом! ') + 'Рівень ' + st.lvN + ' за ' + clock(ms) + ' ' + stars(s);
      }
      let tail = '';
      if (ctx.mine && !HGames.ui.coarse()) {
        if (st.mode === 'pad') tail = v.solo ? ' · Ⓧ — інший герой' : '';
        else tail = (v.solo ? ' · Tab — інший герой' : '') + ' · R (утримати) — заново';
      }
      return 'Рівень ' + st.lvN + ' · ' + clock(st.t * STEP_MS) + ' · 💎 ' + got + '/' + st.L.gems.length + (st.d ? ' · ☠ ' + st.d : '') + tail;
    },

    unmount(root) {
      const st = root._vh;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      clearTimeout(st.idleT);
      clearTimeout(st.centreT);
      st.idleT = 0;
      if (st.rTimer) clearTimeout(st.rTimer);
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.vis) document.removeEventListener('visibilitychange', st.vis);
      if (st.hide) window.removeEventListener('pagehide', st.hide);
      if (st.ro) st.ro.disconnect();
      root._vh = null;
    },
  });

  // для заміру швидкодії й перевірок у headless Chrome
  window.__vohnyk = {
    /// видима картка (у сторінці бувають і сховані картки інших столів)
    st() {
      const all = [...document.querySelectorAll('.vh')].filter((el) => el._vh);
      const el = all.find((e) => e.offsetParent) || all[all.length - 1];
      return el ? el._vh : null;
    },
    drawStats() {
      const st = this.st();
      if (!st || !st.drawN) return null;
      const a = Array.from(st.drawMs.subarray(0, st.drawN)).sort((x, y) => x - y);
      return { n: a.length, avg: a.reduce((s, x) => s + x, 0) / a.length, p95: a[Math.floor(a.length * 0.95)], max: a[a.length - 1] };
    },
  };
})();
