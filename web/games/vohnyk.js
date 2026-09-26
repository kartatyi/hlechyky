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
  const NAMES = ['Вогник', 'Крапля'];
  const EMO = ['🔥', '💧'];

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

  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  const clock = (ms) => {
    const s = Math.max(0, Math.floor(ms / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };
  const clockTenths = (ms) => clock(ms) + ',' + Math.floor((ms % 1000) / 100);
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
        // передбачення
        ph: PH_READY, pt: READY, t: 0, d: 0, cause: 0, stepLocal: -1, acc: 0, lastNow: 0,
        hist: null, histMeta: null, kLog: [new Int32Array(HIST), new Int32Array(HIST)], kLogStep: [new Int32Array(HIST).fill(-1), new Int32Array(HIST).fill(-1)],
        sentK: [0, 0], sendTimes: [], lastSendStep: [0, 0],
        lastN: -1, lastAt: 0, rate: 0.05, rateRef: null, lastF: null,
        // ввід
        held: 0, touch: 0, active: 0, mode: 'kb', switchHeld: false, soloInit: false,
        // малювання
        off: [[0, 0], [0, 0]], parts: [], shake: 0, landT: [0, 0], wasGround: [1, 1],
        leverAng: [], banner: null, deadText: '', gemsSeen: 0, pal: null, palAt: 0, drawMs: [], hudSig: '', hudAt: 0,
        best: {}, giveupArm: 0, pickSig: '',
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
    st.rateRef = null;
    st.ph = PH_READY; st.pt = READY; st.t = 0; st.d = 0; st.cause = 0;
    st.leverAng = st.L.levers.map((l) => (l.init ? 1 : -1));
    st.gemsSeen = 0;
    st.parts.length = 0;
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
    st.wrap.style.setProperty('--vh-ratio', (W / H).toFixed(4));
    const shown = st.wrap.clientWidth || W;
    const S = Math.max(0.25, Math.min(2, Math.round((shown / W) * 20) / 20));
    const cw = Math.round(W * S), ch = Math.round(H * S);
    if (!st.cv || st.cv.w !== cw || st.cv.h !== ch) {
      st.cv = HGames.ui.canvas(st.wrap, { w: cw, h: ch, cls: 'vh-board' });
      st.statKey = '';
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
      lava: c('--vh-lava', '#ff5a1f'), mud: c('--vh-mud', '#5a4a2a'), stone: c('--vh-stone', '#2a3a44'),
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
    const sw = m === 'pad' ? 'Ⓨ' : m === 'touch' ? '⇄' : 'Tab';
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
    // камінь
    for (let r = 0; r < H; r++)
      for (let cc = 0; cc < W; cc++) {
        if (L.tiles[r * W + cc] !== 1) continue;
        const x = cc * TILE, y = r * TILE, k = hash32(L.n * 131 + r, cc);
        g.fillStyle = pal.stone;
        g.fillRect(x, y, TILE, TILE);
        if (k < 0.1 || k > 0.92) {
          g.fillStyle = k < 0.1 ? 'rgba(255,255,255,.05)' : 'rgba(0,0,0,.18)';
          g.fillRect(x, y, TILE, TILE);
        }
        g.fillStyle = 'rgba(0,0,0,.18)';
        g.beginPath(); g.arc(x + 8 + k * 22, y + 12 + (1 - k) * 16, 2.5, 0, Math.PI * 2); g.fill();
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
        g.fillStyle = t === 2 ? '#0d3552' : t === 3 ? '#5a1a08' : '#2b2413';
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
      g.font = '600 12px system-ui, sans-serif';
      const lines = wrapText(g, hintText(st, hnt.text), w - 16);
      const hh = 12 + lines.length * 15;
      g.fillStyle = 'rgba(8,16,22,.72)';
      g.strokeStyle = 'rgba(191,230,255,.35)';
      g.lineWidth = 1;
      g.beginPath(); g.roundRect(x, y, w, hh, 8); g.fill(); g.stroke();
      g.fillStyle = '#e8f4fb';
      g.textAlign = 'left';
      g.textBaseline = 'top';
      lines.forEach((ln, i) => g.fillText(ln, x + 8, y + 7 + i * 15));
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

  function wantK(st, c) {
    if (!playing(st) || st.ph === PH_CLEAR) return 0;
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
        const want = wantK(st, c);
        if (want !== st.sentK[c] && canSend(st)) {
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
    const target = Math.max(st.stepLocal, f.n);
    st.stepLocal = f.n;
    metaSave(st, f.n);
    while (st.stepLocal < target) advance(st, false);
    if (!hadWorld) { st.gemsSeen = st.world.Gems; return; }
    const after = heroPx(st);
    for (let h = 0; h < 2; h++) {
      const o = st.off[h];
      o[0] += before[h][0] - after[h][0];
      o[1] += before[h][1] - after[h][1];
      if (Math.abs(o[0]) > 40 || Math.abs(o[1]) > 40) { o[0] = 0; o[1] = 0; }
    }
  }

  function onFrame(st, f) {
    if (!f || !f.w || !st.world || f.lv !== st.lvN) return;
    const now = performance.now();
    // швидкість сервера (кроків за мс) — з пар кадрів, рознесених хоча б на секунду
    if (!st.rateRef || f.n < st.rateRef.n) st.rateRef = { n: f.n, at: now };
    else if (now - st.rateRef.at > 1000) {
      const r = (f.n - st.rateRef.n) / (now - st.rateRef.at);
      if (r > 0.02 && r < 0.08) st.rate = st.rate * 0.6 + r * 0.4;
      st.rateRef = { n: f.n, at: now };
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
    if (cur < want - 2) steps = Math.min(10, want - cur);
    else if (cur > want + 2) steps = 0;
    else {
      st.acc += dt * st.rate;
      steps = Math.floor(st.acc);
      st.acc -= steps;
      if (cur + steps > want + 2) steps = Math.max(0, want + 2 - cur);
    }
    for (let i = 0; i < steps; i++) { advance(st, true); effects(st); }
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

  function effects(st) {
    const w = st.world, pal = palette(st);
    const reduce = reduced();
    const me = myHeroes(st);
    for (let h = 0; h < 2; h++) {
      const cx = w.X[h] / SU + 12, feet = w.Y[h] / SU + 36;
      // бігом — іскри Вогника й крапельки Краплі
      if (st.ph === PH_GO && w.Grounded[h] && Math.abs(w.Vx[h]) > 40 && Math.random() < (reduce ? 0.1 : 0.5))
        burst(st, cx - Math.sign(w.Vx[h]) * 8, feet - 4, 1, h === 0 ? [pal.fire2, pal.fire] : [pal.water2, pal.water], 1.2, 22, h === 0 ? -0.03 : 0.12);
      if (w.Grounded[h] && !st.wasGround[h]) { st.landT[h] = 6; if (me.includes(h)) snd.land(); }
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
    if (st.ph === PH_DEAD && st.pt === DEAD - 1) {
      for (let h = 0; h < 2; h++)
        if (w.Died[h] || st.cause === 5)
          burst(st, w.X[h] / SU + 12, w.Y[h] / SU + 20, reduce ? 10 : 40, h === 0 ? [pal.fire, pal.fire2, '#555'] : [pal.water, pal.water2, '#ddd'], 3, 36, 0.1);
      if (!reduce) st.shake = 200;
      st.deadText = pickText(st.cause, st.d);
      snd.death();
    }
    if (st.ph === PH_CLEAR && st.pt === CLEAR - 1) {
      const W = st.L.W * TILE;
      for (let i = 0; i < (reduce ? 20 : 80); i++)
        st.parts.push({ x: Math.random() * W, y: -10 - Math.random() * 60, vx: (Math.random() - 0.5) * 1.5, vy: 1 + Math.random() * 2,
          life: 120, max: 120, c: i % 2 ? pal.fire : pal.water, g: 0.02, s: 3 + Math.random() * 2, conf: true });
      st.banner = { at: performance.now(), text: view(st).solo ? 'Сам за двох!' : 'Разом!', sub: st.d === 0 ? 'Жодної втрати!' : '' };
      snd.clear();
    }
    if (st.ph !== PH_CLEAR) st.banner = null;
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
      g.strokeStyle = s.t === 2 ? pal.water2 : s.t === 3 ? pal.fire2 : '#8a7a4a';
      g.lineWidth = 1.5;
      g.globalAlpha = 0.6;
      g.beginPath();
      g.moveTo(x0, y + 6 + wave(still, tt, x0, s.r));
      for (let x = x0 + 8; x < x1; x += 8) g.lineTo(x, y + 6 + wave(still, tt, x, s.r));
      g.lineTo(x1, y + 6 + wave(still, tt, x1, s.r));
      g.stroke();
      g.globalAlpha = 1;
      // лава й болото булькають: одна бульбашка на клітинку раз на ~1,5 с
      if (s.t !== 2 && !still)
        for (let c = s.c0; c <= s.c1; c++) {
          const ph = ((tt / 1500) + hash32(c, s.r)) % 1;
          if (ph > 0.3) continue;
          g.fillStyle = s.t === 3 ? pal.fire2 : '#8a7a4a';
          g.globalAlpha = 1 - ph / 0.3;
          g.beginPath(); g.arc(c * TILE + 10 + hash32(s.r, c) * 20, y + 10 - ph * 20, 2 + ph * 6, 0, Math.PI * 2); g.fill();
          g.globalAlpha = 1;
        }
    }
    for (const k of st.cells) {
      g.fillStyle = k.t === 2 ? pal.water : k.t === 3 ? pal.lava : pal.mud;
      g.globalAlpha = k.t === 2 ? 0.85 : 1;
      g.fillRect(k.c * TILE, k.r * TILE, TILE, TILE);
      g.globalAlpha = 0.35;
      g.fillStyle = k.t === 2 ? pal.water2 : pal.fire2;
      const off = still ? 0 : (tt / 40) % TILE;
      for (let i = 0; i < 3; i++) g.fillRect(k.c * TILE + 6 + i * 12, k.r * TILE + ((off + i * 13) % (TILE - 10)), 2, 10);
      g.globalAlpha = 1;
    }
  }

  function drawMechs(st, g, pal, tt, w) {
    const L = st.L;
    // двері: кам'яна плита з рунами, що їде вгору
    for (let i = 0; i < L.doors.length; i++) {
      const d = L.doors[i];
      const hpx = d.tiles * TILE - w.DoorO[i] / SU;
      if (hpx <= 0) continue;
      const x = d.col * TILE, y = d.row * TILE;
      g.fillStyle = pal.door;
      g.fillRect(x + 3, y, TILE - 6, hpx);
      g.fillStyle = 'rgba(0,0,0,.25)';
      g.fillRect(x + 3, y, 3, hpx);
      g.strokeStyle = d.inv ? '#ffb3a0' : d.all ? '#ffd86b' : '#9fe7ff';
      g.globalAlpha = 0.7;
      g.lineWidth = 1.5;
      for (let ry = y + 10; ry < y + hpx - 6; ry += 22) {
        g.beginPath(); g.moveTo(x + 14, ry); g.lineTo(x + 20, ry + 8); g.lineTo(x + 26, ry); g.stroke();
      }
      g.globalAlpha = 1;
      g.fillStyle = 'rgba(0,0,0,.35)';
      g.fillRect(x + 3, y + hpx - 3, TILE - 6, 3);
    }
    // ліфти: металева плита із заклепками
    for (let i = 0; i < L.lifts.length; i++) {
      const f = L.lifts[i];
      const x = w.LiftX[i] / SU, y = w.LiftY[i] / SU, wd = f.tiles * TILE;
      g.fillStyle = '#8d9aa4';
      g.fillRect(x, y, wd, 16);
      g.fillStyle = '#c5d0d8';
      g.fillRect(x, y, wd, 3);
      g.fillStyle = '#4a555d';
      for (let rx = x + 8; rx < x + wd; rx += 16) { g.beginPath(); g.arc(rx, y + 10, 2, 0, Math.PI * 2); g.fill(); }
    }
    // кнопки
    for (let i = 0; i < L.buttons.length; i++) {
      const b = L.buttons[i], on = w.Button[i];
      g.fillStyle = on ? '#7a8f9c' : '#a8bac6';
      g.fillRect(b.col * TILE + 4, b.row * TILE + 32 + (on ? 4 : 0), 32, on ? 4 : 8);
      g.fillStyle = on ? pal.ok : '#3c4a52';
      g.beginPath(); g.arc(b.col * TILE + 20, b.row * TILE + 28, 2.5, 0, Math.PI * 2); g.fill();
    }
    // важелі: палиця хилиться туди, куди перемкнули
    for (let i = 0; i < L.levers.length; i++) {
      const l = L.levers[i];
      const target = w.Lever[i] ? 1 : -1;
      st.leverAng[i] += (target - st.leverAng[i]) * (reduced() ? 1 : 0.25);
      const px = l.col * TILE + 20, py = l.row * TILE + 36, a = st.leverAng[i] * 0.6;
      g.strokeStyle = '#d8c9a3';
      g.lineWidth = 3;
      g.lineCap = 'round';
      g.beginPath(); g.moveTo(px, py); g.lineTo(px + Math.sin(a) * 22, py - Math.cos(a) * 22); g.stroke();
      g.fillStyle = w.Lever[i] ? pal.ok : '#e57373';
      g.beginPath(); g.arc(px + Math.sin(a) * 22, py - Math.cos(a) * 22, 4, 0, Math.PI * 2); g.fill();
    }
    // скрині
    for (let i = 0; i < L.boxes.length; i++) {
      const x = w.BoxX[i] / SU, y = w.BoxY[i] / SU;
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

  function drawHero(st, g, pal, tt, h, x, y, w, me, label) {
    let sy = 1;
    if (!reduced()) {
      if (w.Vy[h] < 0 && !w.Grounded[h]) sy = 1.12;
      else if (st.landT[h] > 0) sy = 0.86;
    }
    const cx = x + 12, bottom = y + 36;
    // своя рідина під ногами — «по коліна»
    const L = st.L;
    const tr = Math.floor((w.Y[h] / SU + 36) / TILE), tc = Math.floor(cx / TILE);
    const below = tr >= 0 && tr < L.H && tc >= 0 && tc < L.W ? L.tiles[tr * L.W + tc] : 0;
    const wade = w.Grounded[h] && ((h === 0 && below === 3) || (h === 1 && below === 2)) ? 8 : 0;
    if (me) {
      g.strokeStyle = 'rgba(255,255,255,.75)';
      g.lineWidth = 1.5;
      g.beginPath(); g.ellipse(cx, bottom + 1, 14, 3.5, 0, 0, Math.PI * 2); g.stroke();
    }
    g.save();
    g.translate(cx, bottom + wade);
    g.scale(1 / Math.sqrt(sy), sy);
    if (h === 0) {
      // Вогник: полум'я з трьома язиками
      const f = reduced() ? 0 : tt / 90;
      g.fillStyle = pal.fire;
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
      g.fillStyle = pal.fire2;
      g.beginPath(); g.ellipse(0, -11, 9, 10, 0, 0, Math.PI * 2); g.fill();
    } else {
      // Крапля: кругле денце, гострий верх, відблиск
      g.fillStyle = pal.water;
      g.beginPath();
      g.moveTo(0, -42);
      g.bezierCurveTo(6, -30, 16, -22, 15, -13);
      g.arc(0, -13, 15, 0, Math.PI, false);
      g.bezierCurveTo(-16, -22, -6, -30, 0, -42);
      g.fill();
      g.fillStyle = pal.water2;
      g.globalAlpha = 0.8;
      g.beginPath(); g.ellipse(-7, -20, 2.5, 5, -0.4, 0, Math.PI * 2); g.fill();
      g.globalAlpha = 1;
    }
    // очі дивляться туди, куди йде; кліпають раз на ~3 с
    const look = w.Facing[h] ? 1 : -1;
    const blink = !reduced() && ((tt / 3000 + h * 0.43) % 1) < 0.04;
    for (const ex of [-5, 5]) {
      g.fillStyle = '#fff';
      g.beginPath(); g.ellipse(ex + look * 2, -17, 3.6, blink ? 0.8 : 4.6, 0, 0, Math.PI * 2); g.fill();
      if (!blink) { g.fillStyle = '#1b2530'; g.beginPath(); g.arc(ex + look * 3.4, -16.5, 1.9, 0, Math.PI * 2); g.fill(); }
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

  function drawParts(st, g) {
    const p = st.parts;
    let j = 0;
    for (let i = 0; i < p.length; i++) {
      const q = p[i];
      q.life--;
      if (q.life <= 0) continue;
      q.x += q.vx; q.y += q.vy; q.vy += q.g;
      if (q.conf) q.vx += Math.sin(q.life / 7) * 0.05;
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
    } else if (st.ph === PH_DEAD && st.deadText) {
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
    buildStatic(st);
    const g = cv.ctx, pal = palette(st), tt = t0;
    const W = st.L.W * TILE, H = st.L.H * TILE;
    g.save();
    g.scale(st.S, st.S);
    if (st.shake > 0) {
      st.shake -= 16;
      g.translate((Math.random() - 0.5) * 8, (Math.random() - 0.5) * 8);
    }
    g.drawImage(st.stat, 0, 0, W, H);
    const w = st.world;
    drawLiquids(st, g, pal, tt);
    drawMechs(st, g, pal, tt, w);
    // герої: позиція світу + плавний зсув після виправлення, що згасає
    for (let h = 0; h < 2; h++) {
      const o = st.off[h];
      o[0] *= 0.75; o[1] *= 0.75;
      if (Math.abs(o[0]) < 0.05) o[0] = 0;
      if (Math.abs(o[1]) < 0.05) o[1] = 0;
      if (st.landT[h] > 0) st.landT[h]--;
    }
    const v = view(st);
    const me = myHeroes(st);
    const labels = playing(st) && !v.solo && (st.ph === PH_READY || st.ph === PH_DEAD);
    const order = v.solo && st.active === 0 ? [1, 0] : [0, 1];
    for (const h of order) {
      if (playing(st) && st.ph === PH_DEAD && w.Died[h]) continue;
      const x = w.X[h] / SU + st.off[h][0], y = w.Y[h] / SU + st.off[h][1];
      const isMe = me.includes(h) && (!v.solo || h === st.active);
      let label = '';
      if (labels) label = me.includes(h) && st.ph === PH_READY ? 'ти' : (st.ctx.nickOf(h) || '');
      drawHero(st, g, pal, tt, h, x, y, w, isMe, label);
    }
    drawParts(st, g);
    drawOverlay(st, g, pal);
    g.restore();
    st.drawMs.push(performance.now() - t0);
    if (st.drawMs.length > 300) st.drawMs.shift();
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
      html += '<button type="button" class="vh-chip vh-dead" data-vh="reset" title="Почати рівень заново (R)"' + (mine(st) && playing(st) ? '' : ' disabled') + '>☠ ' + st.d + '</button>';
    }
    html += '<button type="button" class="vh-chip vh-snd" data-vh="mute" data-pad-skip aria-label="звук">' + (snd.muted ? '🔇' : '🔈') + '</button>';
    if (mine(st) && playing(st)) html += '<button type="button" class="vh-chip vh-give" data-vh="giveup">' + (st.giveupArm > now ? 'Точно здатись?' : 'Здатись') + '</button>';
    if (html !== st.hudSig) { st.hudSig = html; el.innerHTML = html; }
  }

  function levelsHtml(st) {
    const ctx = st.ctx, v = view(st);
    const host = ctx.room.host && ctx.me && ctx.room.host.toLowerCase() === (ctx.me.nick || '').toLowerCase();
    const canPick = host && ctx.mine && ctx.room.status === 'lobby';
    const other = ctx.seat === 0 ? 1 : 0;
    let html = '<div class="vh-grid">';
    for (const l of v.levels || []) {
      const my = ctx.seat != null ? l.stars[ctx.seat] : null, ot = ctx.seat != null ? l.stars[other] : null;
      const sel = l.n === v.picked;
      html += '<button type="button" class="vh-lv' + (sel ? ' sel' : '') + (l.unlocked ? '' : ' lock') + '" data-lv="' + l.n + '"'
        + (canPick && l.unlocked ? '' : ' disabled') + (sel ? ' data-pad-first' : '') + (canPick ? '' : ' title="Обирає господар"') + '>'
        + '<b>' + l.n + '</b><span class="vh-nm">' + ctx.esc(l.name) + '</span>'
        + (l.unlocked
          ? '<span class="vh-st">' + (my != null ? '<i class="big">' + stars(my || 0) + '</i>' : '')
            + (ot != null && ctx.nickOf(other) ? '<i class="small" title="' + ctx.esc(ctx.nickOf(other)) + '">' + stars(ot || 0) + '</i>' : '') + '</span>'
          : '<span class="vh-st">🔒</span>')
        + '<span class="vh-gm">💎 ' + l.gems[0] + '+' + l.gems[1] + ' · ⏱ ' + clock(l.par) + '</span>'
        + (l.best ? '<span class="vh-bs">' + clockTenths(l.best.ms) + ' · ' + ctx.esc(l.best.nicks) + '</span>' : '')
        + '</button>';
    }
    return html + '</div>';
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

  function resultHtml(st) {
    const r = view(st).result;
    if (!r) return '';
    const ctx = st.ctx;
    if (r.cleared) {
      const next = r.next !== r.level ? '«Ще раз» — це наступний рівень (' + r.next + ')' : 'Усі 15 пройдено! «Ще раз» — знову 15-й';
      return '<div class="vh-res ok"><b>' + (view(st).solo ? 'Сам за двох!' : 'Разом!') + '</b> Рівень ' + r.level + ' за ' + clockTenths(r.ms)
        + ' <span class="vh-stars">' + stars(r.stars) + '</span> · 💎 ' + r.gems + '/' + r.gemsAll + ' · ☠ ' + r.deaths
        + (ctx.mine ? '<div class="small">' + next + '</div>' : '') + '</div>';
    }
    return '<div class="vh-res"><b>Не дограли</b> рівень ' + r.level + ' · ☠ ' + r.deaths + (ctx.mine ? '<div class="small">«Ще раз» — той самий рівень</div>' : '') + '</div>';
  }

  function panel(st) {
    const el = st.pickEl;
    if (!el) return;
    const ctx = st.ctx, v = view(st);
    const status = ctx.room.status;
    let html = '';
    if (status === 'lobby') {
      const n = v.picked || 1;
      loadBest(st, n);
      html = levelsHtml(st) + bestHtml(st, n);
    } else if (status === 'finished') {
      const n = (v.result && v.result.level) || v.picked || 1;
      loadBest(st, n);
      html = resultHtml(st) + bestHtml(st, n);
    }
    el.hidden = !html;
    if (html !== st.pickSig) { st.pickSig = html; el.innerHTML = html; }
  }

  // ---------------------------------------------------------------------------------------------
  // Керування
  // ---------------------------------------------------------------------------------------------

  const KEY = { ArrowLeft: KL, KeyA: KL, ArrowRight: KR, KeyD: KR, Space: KJ, ArrowUp: KJ, KeyW: KJ };

  function switchHero(st) {
    if (!view(st).solo || !mine(st) || !playing(st)) return;
    st.active = 1 - st.active;
    // старий відпускає, новий бере те, що затиснуто — з найближчого кроку (старий шлемо першим)
    for (const c of [1 - st.active, st.active]) {
      const want = wantK(st, c);
      if (want !== st.sentK[c] && canSend(st)) {
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
    if (st.touchEl) st.touchEl.querySelectorAll('.on').forEach((b) => b.classList.remove('on'));
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
      + '<div class="vh-wrap"></div><div class="vh-pick" hidden></div>';
    st.hudEl = root.querySelector('.vh-hud');
    st.wrap = root.querySelector('.vh-wrap');
    st.pickEl = root.querySelector('.vh-pick');
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
      } else if (b.dataset.vh === 'reset') st.ctx.act('reset');
      else if (b.dataset.vh === 'giveup') giveUp(st);
    });
    st.pickEl.addEventListener('click', (e) => {
      const b = e.target.closest('[data-lv]');
      if (!b || b.disabled) return;
      st.ctx.act('pick', { level: +b.dataset.lv });
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
    };
    document.addEventListener('keyup', st.keyup);
    st.blur = () => releaseAll(st);
    window.addEventListener('blur', st.blur);
    st.vis = () => { if (document.hidden) releaseAll(st); st.lastNow = 0; };
    document.addEventListener('visibilitychange', st.vis);
    if (window.ResizeObserver) {
      st.ro = new ResizeObserver(() => fit(st));
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
    if (!window.VohnykSim) { withSim(() => { if (root._vh) apply(root, root._vh.ctx); }); return; }
    if (v.level && (!st.world || v.level.n !== st.lvN)) setLevel(st, v.level);
    else if (v.level) st.src = v.level;
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
    const f = ctx.frame && ctx.frame.lv === st.lvN ? ctx.frame : v.f;
    if (f && playing(st) && (st.lastN < 0 || f.n > st.lastN)) onFrame(st, f);
    fit(st);
    st.hudAt = 0;
    hud(st);
    panel(st);
    spin(st);
  }

  function spin(st) {
    if (st.raf) return;
    const loop = (now) => {
      if (!st.root.isConnected || st.root._vh !== st) { st.raf = 0; return; }
      if (!document.hidden && st.cv && st.cv.el.offsetParent) {
        if (playing(st)) pump(st, now);
        draw(st);
        hud(st);
      } else st.lastNow = 0;
      st.raf = requestAnimationFrame(loop);
    };
    st.raf = requestAnimationFrame(loop);
  }

  HGames.register({
    id: 'vohnyk',
    icon: ICON,
    seatNames: NAMES,
    seatClass: ['vhf', 'vhw'],
    pad: {
      dirs: 'x',
      a: 'Space',
      on(btn, ctx) {
        const st = ctx._vh;
        if (!st) return false;
        st.mode = 'pad';
        if (view(st).solo && (btn === 'y' || btn === 'x')) { switchHero(st); return true; }
        return false;
      },
      hint: '{dpad} бігти · {a} стрибок · {y} інший герой (коли сам за двох)',
    },
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Вогник і Крапля',
      items: [
        '🔥💧 Кооп-платформер на двох: Вогник боїться води, Крапля — лави, обоє — болота',
        '🔘 Кнопки тримають двері й ліфти, поки на них стоїш; важелі перемикаються, коли проходиш крізь них',
        '📦 Скрині штовхаються й тонуть у болоті — виходить місток',
        '💎 Самоцвіти свого кольору й двоє дверей: обоє у своїх — «Разом!». Зірки за час і самоцвіти',
        '🧍 Сам? Керуй обома — Tab (Ⓨ на джойстику) перемикає героя. «Ще раз» після проходження — наступний рівень',
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
    },

    onKey(e, ctx) {
      const st = ctx._vh;
      if (!st || !ctx.mine || !ctx.playing) return false;
      snd.wake();
      st.mode = e.hpad ? 'pad' : 'kb';
      if (e.code === 'Tab' || e.code === 'KeyQ') {
        if (!st.switchHeld) { st.switchHeld = true; switchHero(st); }
        return true;
      }
      if (e.code === 'KeyR') {
        if (!e.repeat) ctx.act('reset');
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
        const host = r.host && ctx.me && r.host.toLowerCase() === (ctx.me.nick || '').toLowerCase();
        return host ? 'Обери рівень і тисни «Почати»' : 'Господар обирає рівень';
      }
      if (r.status === 'finished') {
        const res = v.result;
        if (!res) return '';
        if (!ctx.mine) return res.cleared ? 'Пройшли рівень ' + res.level : 'Не дограли';
        if (res.cleared) return res.next !== res.level ? 'Пройдено! «Ще раз» — це наступний рівень (' + res.next + ')' : 'Усі 15 пройдено! «Ще раз» — знову 15-й';
        return '«Ще раз» — той самий рівень';
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
      return 'Рівень ' + st.lvN + ' · ' + clock(st.t * STEP_MS) + ' · 💎 ' + got + '/' + st.L.gems.length + (st.d ? ' · ☠ ' + st.d : '')
        + (ctx.mine && !HGames.ui.coarse() ? (v.solo ? ' · ' + hintText(st, '{switch}') + ' — інший герой' : '') + ' · R — заново' : '');
    },

    unmount(root) {
      const st = root._vh;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blur) window.removeEventListener('blur', st.blur);
      if (st.vis) document.removeEventListener('visibilitychange', st.vis);
      if (st.ro) st.ro.disconnect();
      root._vh = null;
    },
  });

  // для заміру швидкодії й перевірок у headless Chrome
  window.__vohnyk = {
    st() { const el = document.querySelector('.vh'); return el && el._vh; },
    drawStats() {
      const st = this.st();
      if (!st || !st.drawMs.length) return null;
      const a = st.drawMs.slice().sort((x, y) => x - y);
      return { n: a.length, avg: a.reduce((s, x) => s + x, 0) / a.length, p95: a[Math.floor(a.length * 0.95)], max: a[a.length - 1] };
    },
  };
})();
