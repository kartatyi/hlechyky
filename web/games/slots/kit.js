/* SlotKit — спільний програвач слотів і HUD розділу «🎰 Азарт».
   Звичайний скрипт (не модуль), глобал window.SlotKit. API — у docs/games/dev/slots/KIT.md (там же стенди proto.html / index.html / art.html — відкривати файлом).
   Автомат оголошує себе SlotKit.define({...}), стенд/лобі монтує SlotKit.mount(el, id, opts).
   Правила: у спокої — нуль rAF (лише CSS), rAF живе тільки під час обертів/підрахунку/частинок. */
(function () {
  'use strict';
  const SK = window.SlotKit = window.SlotKit || {};
  SK.version = 1;
  SK.machines = SK.machines || {};
  SK.BETS = [10, 20, 50, 100, 200, 500];
  SK.TIERS = [
    { x: 10, key: 'big', label: 'Великий занос' },
    { x: 25, key: 'mega', label: 'Мега занос' },
    { x: 50, key: 'epic', label: 'Епічний занос' },
  ];
  // Розміри «дизайну» (у px до масштабування). Автомат малює в .sk-area саме таких розмірів.
  SK.LAYOUT = {
    land: { w: 1280, h: 800, ticker: 34, hud: 120 },
    port: { w: 420, h: 864, ticker: 30, hud: 206 },
  };
  SK.stats = SK.stats || { raf: 0, loops: 0 };
  SK.define = function (m) { SK.machines[m.id] = m; return m; };

  // ---------- дрібниці ----------
  const fmt = SK.fmt = (n) => String(Math.round(n || 0)).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  const esc = SK.esc = (t) => String(t == null ? '' : t).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const mod = (a, n) => ((a % n) + n) % n;
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const div = (cls, html) => { const d = document.createElement('div'); if (cls) d.className = cls; if (html != null) d.innerHTML = html; return d; };
  SK.rnd = {
    int: (n) => Math.floor(Math.random() * n),
    pick: (a) => a[Math.floor(Math.random() * a.length)],
    // weights: {key: вага}
    weighted(w) { let s = 0; for (const k in w) s += w[k]; let x = Math.random() * s; for (const k in w) { x -= w[k]; if (x < 0) return k; } return Object.keys(w)[0]; },
  };
  const hue = (s) => { let h = 0; for (const ch of String(s)) h = (h * 31 + ch.charCodeAt(0)) % 360; return h; };
  const F0 = 4.3; // похідна easeOutBack(c1=1.3) у нулі — щоб гальмування підхоплювало швидкість без ривка
  function easeOutBack(u) { const c1 = 1.3, c3 = c1 + 1; return 1 + c3 * Math.pow(u - 1, 3) + c1 * Math.pow(u - 1, 2); }
  const LINE_COLORS = ['#f4c542', '#ff6b6b', '#5ec8ff', '#7bd389', '#d78bff', '#ffa24c', '#ff8fc7', '#9ef0e0', '#c9e265', '#ffd27a'];
  SK.lineColor = (i) => LINE_COLORS[mod(i, LINE_COLORS.length)];

  // ---------- Скарбничка Глека (мок): однакова в лобі й на автоматі ----------
  SK.jackpot = () => 1250000 + Math.floor((Date.now() - Date.UTC(2026, 9, 9)) / 1000 * 1.3);
  // Живе: сайт (web/games/slot.js) кладе сюди відповідь /api/slots/feed — { jackpot, mustHit?, lines: [рядок…] }.
  // Поки SK.live нема (стенд), тікер показує мок вище.
  SK.live = SK.live || null;
  SK.setLive = function (o) { SK.live = Object.assign(SK.live || {}, o || {}); };
  // Свій вид частинок із будь-якого SVG (напр. extras.spark): SK.svgParticle('spark', svg) → 'spark' для fx.burst({kind})
  SK.svgParticle = function (name, svg) {
    if (!svg) return null;
    let src = String(svg); if (!/xmlns=/.test(src)) src = src.replace('<svg', '<svg xmlns="http://www.w3.org/2000/svg"');
    const img = new Image(); img.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(src);
    SK.particles[name] = function (g, p) {
      if (img.complete && img.naturalWidth) g.drawImage(img, -p.s, -p.s, p.s * 2, p.s * 2);
      else { g.fillStyle = p.color || '#ffe58a'; g.beginPath(); g.arc(0, 0, p.s * 0.5, 0, 7); g.fill(); }
    };
    return name;
  };
  SK.feed = [
    'smaug виніс 24 600 🏺 у «Розбитих глеках»',
    'владік зловив три Глеки — 30 000 🏺',
    'микола вгадав Ворожку 5 разів поспіль',
    'гість_42 заповнив скарб козака: ×1000',
    'Цвіт папороті розцвів у smaug — 18 250 🏺',
    'Глек каже: крутіть, черепки самі не розіб’ються',
  ];

  // ---------- Звук: WebAudio-синт, за замовчуванням вимкнено ----------
  SK.sound = SK.sound || (function () {
    let ac = null, master = null, on = false, noiseBuf = null;
    try { on = localStorage.getItem('slots.sound') === '1'; } catch (e) { on = false; }
    function audio() {
      if (!ac) {
        const A = window.AudioContext || window.webkitAudioContext; if (!A) return null;
        ac = new A(); master = ac.createGain(); master.gain.value = 0.45; master.connect(ac.destination);
      }
      if (ac.state === 'suspended') ac.resume();
      return ac;
    }
    function tone(f, t0, dur, type, vol, f2) {
      const o = ac.createOscillator(), g = ac.createGain();
      o.type = type || 'sine'; o.frequency.setValueAtTime(f, t0);
      if (f2) o.frequency.exponentialRampToValueAtTime(f2, t0 + dur);
      g.gain.setValueAtTime(0.0001, t0); g.gain.exponentialRampToValueAtTime(vol || 0.2, t0 + 0.008);
      g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
      o.connect(g).connect(master); o.start(t0); o.stop(t0 + dur + 0.03);
    }
    function noise(t0, dur, freq, q, vol, type) {
      if (!noiseBuf) {
        noiseBuf = ac.createBuffer(1, ac.sampleRate * 0.6, ac.sampleRate);
        const d = noiseBuf.getChannelData(0); for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
      }
      const s = ac.createBufferSource(); s.buffer = noiseBuf;
      const f = ac.createBiquadFilter(); f.type = type || 'bandpass'; f.frequency.value = freq; f.Q.value = q || 1;
      const g = ac.createGain(); g.gain.setValueAtTime(vol || 0.3, t0); g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
      s.connect(f).connect(g).connect(master); s.start(t0); s.stop(t0 + dur + 0.03);
    }
    const arp = (t, notes, step, dur, type, vol) => notes.forEach((f, i) => tone(f, t + i * step, dur, type, vol));
    const lib = {
      stop: (h, o) => { h.tone(170 * (o.pitch || 1), h.t, 0.1, 'sine', 0.5, 55); h.noise(h.t, 0.05, 1100, 1.2, 0.3); },
      lever: (h) => { h.noise(h.t, 0.14, 380, 0.8, 0.4); h.tone(95, h.t + 0.05, 0.18, 'sine', 0.45, 48); },
      tick: (h, o) => h.tone(1300 + (o.p || 0) * 1400, h.t, 0.028, 'square', 0.05),
      click: (h) => h.tone(900, h.t, 0.03, 'square', 0.05),
      win: (h) => arp(h.t, [523, 659, 784, 1047], 0.07, 0.25, 'triangle', 0.2),
      bell: (h) => { h.tone(1318, h.t, 1.3, 'sine', 0.22); h.tone(2637, h.t, 0.8, 'sine', 0.08); h.tone(3951, h.t, 0.4, 'sine', 0.04); },
      big: (h) => { arp(h.t, [392, 523, 659, 784, 1047, 1319], 0.08, 0.3, 'triangle', 0.2); [523, 659, 784].forEach((f) => h.tone(f, h.t + 0.5, 1.1, 'sawtooth', 0.05)); },
      level: (h) => { arp(h.t, [659, 784, 988, 1319], 0.06, 0.35, 'square', 0.08); h.noise(h.t, 0.4, 5000, 0.5, 0.12, 'highpass'); },
      coin: (h) => { h.tone(1975, h.t, 0.1, 'square', 0.06); h.tone(2637, h.t + 0.05, 0.35, 'sine', 0.14); },
      crack: (h) => { h.noise(h.t, 0.16, 2400, 0.8, 0.5); h.noise(h.t + 0.03, 0.22, 650, 0.7, 0.3); h.tone(320, h.t, 0.08, 'triangle', 0.2, 90); },
      bonus: (h) => { arp(h.t, [392, 523, 659, 784], 0.12, 0.4, 'square', 0.08); [523, 659, 784, 1047].forEach((f) => h.tone(f, h.t + 0.55, 1.4, 'triangle', 0.1)); },
      lose: (h) => { h.tone(392, h.t, 0.18, 'triangle', 0.14); h.tone(294, h.t + 0.16, 0.35, 'triangle', 0.14); },
      flip: (h) => { h.noise(h.t, 0.08, 3000, 1, 0.2); h.noise(h.t + 0.09, 0.06, 2200, 1, 0.15); },
    };
    const api = {
      get on() { return on; },
      set(v) { on = !!v; try { localStorage.setItem('slots.sound', on ? '1' : '0'); } catch (e) { /* приватне вікно */ } if (on) audio(); },
      add(name, fn) { lib[name] = fn; },               // fn(h, opts), h = {t, tone, noise}
      play(name, opts) {
        if (!on || !lib[name]) return;
        try { const a = audio(); if (!a) return; lib[name]({ t: a.currentTime + 0.005, tone, noise, ac: a, out: master }, opts || {}); } catch (e) { /* звук не критичний */ }
      },
      // Наростання (очікування): повертає stop()
      rise(ms) {
        if (!on) return () => {};
        try {
          const a = audio(); const t = a.currentTime;
          const o = a.createOscillator(), f = a.createBiquadFilter(), g = a.createGain();
          o.type = 'sawtooth'; o.frequency.setValueAtTime(110, t); o.frequency.exponentialRampToValueAtTime(440, t + ms / 1000);
          f.type = 'lowpass'; f.frequency.setValueAtTime(400, t); f.frequency.exponentialRampToValueAtTime(2600, t + ms / 1000);
          g.gain.setValueAtTime(0.0001, t); g.gain.exponentialRampToValueAtTime(0.09, t + ms / 1000);
          o.connect(f).connect(g).connect(master); o.start(t);
          return () => { try { const n = a.currentTime; g.gain.cancelScheduledValues(n); g.gain.setValueAtTime(g.gain.value, n); g.gain.exponentialRampToValueAtTime(0.0001, n + 0.12); o.stop(n + 0.15); } catch (e) { /* вже зупинено */ } };
        } catch (e) { return () => {}; }
      },
    };
    return api;
  })();

  // ---------- rAF-цикл: живе лише поки є задачі ----------
  function Loop() {
    const fns = new Set(); let id = 0, last = 0;
    function frame(t) {
      SK.stats.raf++;
      const dt = Math.min(0.05, Math.max(0, (t - last) / 1000)); last = t;
      for (const f of Array.from(fns)) { let r; try { r = f(t, dt); } catch (e) { console.error(e); r = false; } if (r === false) fns.delete(f); }
      if (fns.size) id = requestAnimationFrame(frame); else { id = 0; SK.stats.loops--; }
    }
    return {
      add(f) { fns.add(f); if (!id) { last = performance.now(); id = requestAnimationFrame(frame); SK.stats.loops++; } },
      remove(f) { fns.delete(f); },
      stop() { fns.clear(); if (id) { cancelAnimationFrame(id); id = 0; SK.stats.loops--; } },
    };
  }

  // ---------- Частинки: один canvas, існує лише поки є частинки ----------
  SK.particles = SK.particles || {};
  const P = SK.particles;
  P.coin = P.coin || function (g, p) {
    const w = Math.max(0.12, Math.abs(Math.cos(p.spin))); g.scale(w, 1);
    g.fillStyle = '#9a6b12'; g.beginPath(); g.arc(0, 0, p.s, 0, 7); g.fill();
    g.fillStyle = '#f4c542'; g.beginPath(); g.arc(0, 0, p.s * 0.84, 0, 7); g.fill();
    g.fillStyle = '#ffe58a'; g.beginPath(); g.arc(-p.s * 0.25, -p.s * 0.25, p.s * 0.32, 0, 7); g.fill();
  };
  P.shard = P.shard || function (g, p) {
    g.fillStyle = p.color || '#c5763a'; g.strokeStyle = 'rgba(60,25,8,.7)'; g.lineWidth = 1.5;
    g.beginPath(); p.pts.forEach((q, i) => (i ? g.lineTo(q[0] * p.s, q[1] * p.s) : g.moveTo(q[0] * p.s, q[1] * p.s))); g.closePath(); g.fill(); g.stroke();
  };
  P.confetti = P.confetti || function (g, p) { g.scale(1, Math.abs(Math.cos(p.spin)) + 0.1); g.fillStyle = p.color; g.fillRect(-p.s, -p.s * 0.45, p.s * 2, p.s * 0.9); };
  P.spark = P.spark || function (g, p) { g.globalCompositeOperation = 'lighter'; g.globalAlpha *= 0.9; g.fillStyle = p.color || '#ffd27a'; g.beginPath(); g.arc(0, 0, p.s * 0.5, 0, 7); g.fill(); };
  const CONF = ['#f4c542', '#e5533d', '#4fb0e8', '#7bd389', '#f7f0de', '#d78bff'];
  const CLAY = ['#c5763a', '#a85a2a', '#d98c4e', '#8e4a22'];

  function FX(root, loop) {
    let cv = null, g = null, W = 0, H = 0, dpr = 1;
    const parts = [], emitters = [];
    function ensure() {
      if (cv) return;
      cv = document.createElement('canvas'); cv.className = 'sk-fx'; root.appendChild(cv);
      const r = root.getBoundingClientRect(); dpr = Math.min(2, window.devicePixelRatio || 1);
      W = r.width; H = r.height; cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr); g = cv.getContext('2d');
      loop.add(step);
    }
    function make(x, y, o) {
      const kind = o.kind || 'coin', a = (o.angle != null ? o.angle : -Math.PI / 2) + (Math.random() - 0.5) * (o.spread != null ? o.spread : 2.2);
      const sp = (o.speed || 520) * (0.45 + Math.random() * 0.75);
      const p = { kind, x, y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp, r: Math.random() * 6, vr: (Math.random() - 0.5) * 10,
        spin: Math.random() * 6, vs: 4 + Math.random() * 8, s: (o.size || 12) * (0.7 + Math.random() * 0.6), life: o.life || 2.6, age: 0, img: o.img, data: o.data,
        g: o.gravity != null ? o.gravity : 1100, color: o.color || (kind === 'shard' ? CLAY[SK.rnd.int(4)] : kind === 'confetti' ? CONF[SK.rnd.int(CONF.length)] : null) };
      if (kind === 'shard') { const n = 3 + SK.rnd.int(2); p.pts = []; for (let i = 0; i < n; i++) { const t = i / n * 6.283 + Math.random() * 0.8; const rr = 0.5 + Math.random() * 0.6; p.pts.push([Math.cos(t) * rr, Math.sin(t) * rr]); } }
      if (kind === 'spark') { p.g = o.gravity != null ? o.gravity : 120; p.life = o.life || 0.9; }
      parts.push(p);
    }
    const api = {
      // x, y — у px відносно кореня автомата
      burst(x, y, o) { o = o || {}; ensure(); const n = o.n || 24; for (let i = 0; i < n && parts.length < 400; i++) make(x, y, o); },
      at(el, o) { const r = el.getBoundingClientRect(), rr = root.getBoundingClientRect(); api.burst(r.left - rr.left + r.width / 2, r.top - rr.top + r.height / 2, o); },
      // дощ згори впродовж ms
      rain(o) { o = o || {}; ensure(); emitters.push({ until: performance.now() + (o.ms || 2000), rate: o.rate || 40, acc: 0, o }); },
      stopRain() { emitters.length = 0; },
      get count() { return parts.length; },
      clear() { parts.length = 0; emitters.length = 0; },
    };
    function step(t, dt) {
      if (!cv) return false;
      for (let i = emitters.length - 1; i >= 0; i--) {
        const e = emitters[i]; if (t > e.until) { emitters.splice(i, 1); continue; }
        e.acc += dt * e.rate; while (e.acc >= 1) { e.acc--; make(Math.random() * W, -20, Object.assign({ angle: Math.PI / 2, spread: 0.6, speed: 160 }, e.o)); }
      }
      g.setTransform(dpr, 0, 0, dpr, 0, 0); g.clearRect(0, 0, W, H);
      for (let i = parts.length - 1; i >= 0; i--) {
        const p = parts[i]; p.age += dt; p.vy += p.g * dt; p.vx *= 0.995; p.x += p.vx * dt; p.y += p.vy * dt; p.r += p.vr * dt; p.spin += p.vs * dt;
        if (p.age > p.life || p.y > H + 40) { parts.splice(i, 1); continue; }
        g.save(); g.globalAlpha = clamp((p.life - p.age) / 0.4, 0, 1); g.translate(p.x, p.y); g.rotate(p.r);
        (P[p.kind] || P.coin)(g, p); g.restore();
      }
      if (!parts.length && !emitters.length) { cv.remove(); cv = null; g = null; return false; }
      return true;
    }
    api.destroy = () => { parts.length = 0; emitters.length = 0; if (cv) { cv.remove(); cv = null; } };
    return api;
  }

  // ---------- Барабани (spinStyle 'reels'): стрічки, циліндр, очікування, slam ----------
  // grid — завжди по колонках: grid[c][r]. Зупинка stops[c] — індекс верхнього видимого символу у стрічці strips[c].
  function Reels(ctx, host, o) {
    const cols = o.cols, rows = o.rows, gap = o.gap == null ? 10 : o.gap, cyl = o.style === 'cylinder';
    const strips = o.strips || null;
    const keysPool = o.pool || (strips ? strips.flat() : Object.keys((ctx.art && ctx.art.symbols) || {}));
    const tm = (o.curve || 52) * Math.PI / 180;
    // size — висота символу; або height — висота вікна (тоді size рахується сам). cellW — ширина барабана (за замовчуванням = size)
    const S = o.height ? (cyl ? o.height * tm / ((rows + 0.85) * Math.sin(tm)) : o.height / rows) : o.size;
    const CW = o.cellW || S;
    const R = cyl ? (rows + 0.85) * S / (2 * tm) : 0;
    const H = cyl ? Math.round(2 * R * Math.sin(tm)) : rows * S;
    const W = cols * CW + (cols - 1) * gap;
    const M = 3; // запас символів над видимим вікном у послідовності
    const KMIN = cyl ? -2 : -1, KMAX = cyl ? rows + 1 : rows, POOL = KMAX - KMIN + 1;
    const el = div('sk-reels' + (cyl ? ' sk-cyl' : ''));
    el.style.width = W + 'px'; el.style.height = H + 'px'; el.style.setProperty('--S', S + 'px');
    host.appendChild(el);
    const rk = () => SK.rnd.pick(keysPool);
    const reels = [];
    let stopsNow = null;
    for (let c = 0; c < cols; c++) {
      const r = { c, el: div('sk-reel'), pool: [], seq: [], p: M, landed: true, plan: null, fast: false };
      r.el.style.cssText = 'left:' + c * (CW + gap) + 'px;width:' + CW + 'px;height:' + H + 'px';
      for (let i = 0; i < POOL; i++) { const cell = div('sk-cell'); cell.style.width = CW + 'px'; cell.style.height = S + 'px'; r.el.appendChild(cell); r.pool.push(cell); }
      if (cyl) r.el.appendChild(div('sk-shade'));
      r.el.appendChild(div('sk-glow'));
      el.appendChild(r.el); reels.push(r);
    }
    const lines = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    lines.setAttribute('class', 'sk-lines'); lines.setAttribute('viewBox', '0 0 ' + W + ' ' + H); lines.setAttribute('width', W); lines.setAttribute('height', H);
    el.appendChild(lines);

    function seqFromStop(c, stop) { const s = strips[c], L = s.length, a = []; for (let i = -M; i < rows + 2; i++) a.push(s[mod(stop + i, L)]); return a; }
    function seqFromGrid(c, col) { const a = []; for (let i = 0; i < M; i++) a.push(rk()); return a.concat(col, [rk(), rk()]); }
    function windowOf(step, c) { return step.stops && strips ? seqFromStop(c, step.stops[c]) : seqFromGrid(c, step.grid[c]); }

    function render(r) {
      const fp = Math.floor(r.p), fr = r.p - fp;
      for (let k = KMIN; k <= KMAX; k++) {
        const idx = fp + k, cell = r.pool[mod(idx, POOL)];
        const key = r.seq[idx] != null ? r.seq[idx] : r.seq[clamp(idx, 0, r.seq.length - 1)];
        if (cell._key !== key) { cell._key = key; cell.dataset.k = key; cell.replaceChildren(ctx.symNode(key)); }
        const cc = (k - fr + 0.5) * S - rows * S / 2;
        let y, sc = 1, hid = false;
        if (cyl) { const th = cc / R; hid = Math.abs(th) > 1.5; y = H / 2 + R * Math.sin(th) - S / 2; sc = Math.max(0.02, Math.cos(th)); }
        else y = H / 2 + cc - S / 2;
        cell.style.transform = 'translate3d(0,' + y.toFixed(2) + 'px,0)' + (sc !== 1 ? ' scaleY(' + sc.toFixed(3) + ')' : '');
        cell.style.visibility = hid ? 'hidden' : '';
      }
    }
    function setNow(step) {
      reels.forEach((r, c) => { r.seq = windowOf(step, c); r.p = M; r.landed = true; r.plan = null; render(r); });
      stopsNow = step.stops ? step.stops.slice() : null;
    }

    let spinning = null; // {res, stopRise}
    function spin(step) {
      const turbo = ctx.turbo, sp = o.speed || 1;
      const base = turbo ? 360 : 820, stag = turbo ? 100 : 230, D = turbo ? 0.24 : 0.4, teaseMs = turbo ? 1000 : 1700;
      const v = (turbo ? 30 : 22) * sp, tw = turbo ? 0.08 : 0.16;
      const tease = new Set(step.tease || []);
      const now = performance.now();
      ctx.clearWin();
      let extra = 0;
      reels.forEach((r, c) => {
        if (tease.has(c)) extra += teaseMs;
        const t0 = now / 1000, Tland = (now + base + c * stag + extra) / 1000;
        const win = windowOf(step, c);
        const cur = r.seq.slice(r.p - M, r.p + rows + 2);
        const d = v * D / F0, Tc = Tland - D - (t0 + tw);
        const F = Math.max(3, Math.round(d + v * Tc - (rows + M + 2)));
        const filler = [];
        const s = strips && step.stops ? strips[c] : null;
        for (let j = 0; j < F; j++) filler.push(s ? s[mod(step.stops[c] + rows + 2 + j, s.length)] : rk());
        r.seq = win.concat(filler, cur);
        const pStart = rows + M + 2 + F + M;
        r.plan = { t0, tw, Tland, D, d, v: (pStart - M - d) / Tc, pStart, teased: tease.has(c) };
        r.landed = false; r.teasing = false;
      });
      stopsNow = step.stops ? step.stops.slice() : null;
      el.classList.add('sk-spinning');
      return new Promise((res) => {
        spinning = { res, stopRise: null };
        ctx.loop.add(function tick(tms) {
          if (!spinning) return false;
          const t = tms / 1000; let all = true;
          reels.forEach((r, c) => {
            if (r.landed) return;
            const Pl = r.plan; let p, fast;
            if (t < Pl.t0 + Pl.tw) { const u = Math.max(0, (t - Pl.t0) / Pl.tw); p = Pl.pStart + 0.3 * Math.sin(Math.PI * u); fast = false; }
            else if (t < Pl.Tland - Pl.D) { p = M + Pl.d + Pl.v * (Pl.Tland - Pl.D - t); fast = true; }
            else if (t < Pl.Tland) { const u = (t - (Pl.Tland - Pl.D)) / Pl.D; p = M + Pl.d * (1 - easeOutBack(u)); fast = u < 0.3; }
            else { land(r, c); return; }
            all = false; r.p = p; render(r);
            if (fast !== r.fast) { r.fast = fast; r.el.classList.toggle('sk-fast', fast); }
            if (Pl.teased && !r.teasing && reels.slice(0, c).every((q) => q.landed)) {
              r.teasing = true; r.el.classList.add('sk-tease'); el.classList.add('sk-teasing');
              if (spinning.stopRise) spinning.stopRise();
              spinning.stopRise = SK.sound.rise(Math.max(300, (Pl.Tland - t) * 1000));
              ctx.emit('tease', c);
            }
          });
          if (all) {
            const sp2 = spinning; spinning = null; if (sp2.stopRise) sp2.stopRise();
            el.classList.remove('sk-spinning', 'sk-teasing'); sp2.res(); return false;
          }
          return true;
        });
      });
    }
    function land(r, c) {
      r.landed = true; r.seq = r.seq.slice(0, rows + M + 2); r.p = M; render(r);
      r.fast = false; r.el.classList.remove('sk-fast', 'sk-tease');
      if (r.teasing && spinning && spinning.stopRise) { spinning.stopRise(); spinning.stopRise = null; }
      r.el.classList.remove('sk-bump'); void r.el.offsetWidth; r.el.classList.add('sk-bump');
      ctx.sound('stop', { pitch: 1 - c * 0.04 }); ctx.emit('reelStop', c);
    }
    // Швидка зупинка (тап/пробіл під час оберту)
    function slam() {
      if (!spinning) return false;
      const t = performance.now() / 1000; let i = 0;
      reels.forEach((r) => {
        if (r.landed) return; const Pl = r.plan; if (t >= Pl.Tland - Pl.D) return;
        Pl.D = 0.2; Pl.d = Pl.v * Pl.D / F0; Pl.Tland = t + Pl.D + 0.06 * i++; Pl.t0 = t - 1; Pl.tw = 0; Pl.teased = false;
        r.el.classList.remove('sk-tease');
      });
      el.classList.remove('sk-teasing');
      if (spinning.stopRise) { spinning.stopRise(); spinning.stopRise = null; }
      return true;
    }
    function cell(c, r) { const q = reels[c]; return q.pool[mod(Math.round(q.p) + r, POOL)]; }
    function center(c, r) {
      const x = c * (CW + gap) + CW / 2, cc = (r + 0.5) * S - rows * S / 2;
      return [x, cyl ? H / 2 + R * Math.sin(cc / R) : H / 2 + cc];
    }
    function grid() { return reels.map((q) => q.seq.slice(q.p, q.p + rows)); }

    // початок
    const init = o.initial || ctx.keepGrid && { grid: ctx.keepGrid } || (strips ? { stops: strips.map((s) => SK.rnd.int(s.length)) } : { grid: reels.map(() => Array.from({ length: rows }, rk)) });
    setNow(init);
    const api = {
      kind: 'reels', el, cols, rows, size: S, cellW: CW, gap, width: W, height: H,
      spin, slam, set: setNow, cell, center, grid, linesEl: lines,
      get stops() { return stopsNow; },
      get busy() { return !!spinning; },
      cells() { const a = []; for (let c = 0; c < cols; c++) for (let r = 0; r < rows; r++) a.push(cell(c, r)); return a; },
    };
    addLines(api, lines);
    return api;
  }

  // Лінії виграшу поверх поля (спільне для барабанів і сітки)
  function addLines(api, svg) {
    api.showLines = function (list) {
      let h = '';
      list.forEach((it) => {
        const pts = it.pts.map(([c, r]) => api.center(c, r));
        const d = 'M' + pts.map((p) => p[0].toFixed(1) + ' ' + p[1].toFixed(1)).join(' L');
        const col = it.color || SK.lineColor(it.n || 0);
        h += '<path d="' + d + '" class="sk-line-o"/><path d="' + d + '" class="sk-line" style="stroke:' + col + '"/>';
        pts.forEach((p) => { h += '<circle cx="' + p[0] + '" cy="' + p[1] + '" r="7" style="fill:' + col + '" class="sk-dot"/>'; });
      });
      svg.innerHTML = h; svg.classList.toggle('on', !!list.length);
    };
    api.clearLines = () => { svg.innerHTML = ''; svg.classList.remove('on'); };
  }

  // ---------- Сітка з падінням (spinStyle 'drop'): каскади, кластери ----------
  function DropGrid(ctx, host, o) {
    const cols = o.cols, rows = o.rows, S = o.size, gap = o.gap == null ? 6 : o.gap;
    const W = cols * S + (cols - 1) * gap, H = rows * S + (rows - 1) * gap;
    const keysPool = o.pool || Object.keys((ctx.art && ctx.art.symbols) || {});
    const pickInit = weighted(o.weights || ctx.machine.weights, keysPool);
    const el = div('sk-grid'); el.style.width = W + 'px'; el.style.height = H + 'px'; el.style.setProperty('--S', S + 'px');
    host.appendChild(el);
    const colEls = []; for (let c = 0; c < cols; c++) { const ce = div('sk-col'); ce.style.cssText = 'left:' + c * (S + gap) + 'px;width:' + S + 'px;height:' + H + 'px'; el.appendChild(ce); colEls.push(ce); }
    const lines = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    lines.setAttribute('class', 'sk-lines'); lines.setAttribute('viewBox', '0 0 ' + W + ' ' + H); lines.setAttribute('width', W); lines.setAttribute('height', H);
    el.appendChild(lines);
    let cells = [];
    const pos = (c, r) => [c * (S + gap), r * (S + gap)];
    function mk(c, r, key) {
      const e = div('sk-cell'); e.style.width = e.style.height = S + 'px';
      const [x, y] = pos(c, r); e.style.transform = 'translate(' + x + 'px,' + y + 'px)';
      e._key = key; e.dataset.k = key; e.appendChild(ctx.symNode(key)); el.insertBefore(e, lines); return e;
    }
    function setNow(step) {
      cells.flat().forEach((e) => e.remove());
      cells = step.grid.map((col, c) => col.map((k, r) => mk(c, r, k)));
    }
    const dur = (ms) => (ctx.turbo ? ms * 0.55 : ms);
    function fallIn(e, c, r, fromRows, delay) {
      const [x, y] = pos(c, r);
      return e.animate([{ transform: 'translate(' + x + 'px,' + (y - fromRows * (S + gap)) + 'px)' }, { transform: 'translate(' + x + 'px,' + y + 'px)' }],
        { duration: dur(260 + fromRows * 45), delay: dur(delay), easing: 'cubic-bezier(.45,.05,.55,1.25)', fill: 'backwards' }).finished;
    }
    function spin(step) {
      ctx.clearWin();
      const tease = new Set(step.tease || []), stag = 90; let extra = 0;
      const outs = [], ins = [];
      cells.forEach((col, c) => col.forEach((e, r) => {
        const [x, y] = pos(c, r);
        const a = e.animate([{ transform: 'translate(' + x + 'px,' + y + 'px)', opacity: 1 }, { transform: 'translate(' + x + 'px,' + (y + H + S) + 'px)', opacity: 0.4 }],
          { duration: dur(320), delay: dur(c * stag * 0.6 + (rows - r) * 18), easing: 'cubic-bezier(.5,0,.9,.6)', fill: 'forwards' });
        outs.push(a.finished.then(() => e.remove()));
      }));
      const next = step.grid.map((col, c) => col.map((k, r) => mk(c, r, k)));
      next.forEach((col, c) => {
        if (tease.has(c)) extra += ctx.turbo ? 700 : 1300;
        const delay = 260 + c * stag + extra;
        if (tease.has(c)) ctx.timeout(() => { colEls[c].classList.add('sk-tease'); ctx.emit('tease', c); }, dur(delay - (ctx.turbo ? 700 : 1300)));
        const landed = Promise.all(col.map((e, r) => fallIn(e, c, r, rows + 1, delay + (rows - 1 - r) * 28)));
        ins.push(landed.then(() => { colEls[c].classList.remove('sk-tease'); ctx.sound('stop', { pitch: 1.2 - c * 0.03 }); ctx.emit('reelStop', c); }));
      });
      cells = next;
      return Promise.all(outs.concat(ins));
    }
    // step: {remove:[[c,r]], grid: поле після падіння}
    async function cascade(step) {
      const rem = new Set(step.remove.map(([c, r]) => c + ',' + r));
      const els = step.remove.map(([c, r]) => cells[c][r]);
      const m = ctx.machine;
      if (m.onRemove) await m.onRemove(els, ctx, step);
      else {
        ctx.sound('crack');
        els.forEach((e) => { e.classList.add('sk-pop'); ctx.fx.at(e, { kind: 'spark', n: 6, speed: 260, size: 8 }); });
        await ctx.wait(dur(300));
      }
      els.forEach((e) => e.remove());
      ctx.reels.clearLines();
      const falls = [], fresh = [], moved = [];
      cells = cells.map((col, c) => {
        const keep = col.map((e, r) => [e, r]).filter(([, r]) => !rem.has(c + ',' + r));
        const k = rows - keep.length, ncol = [];
        for (let r = 0; r < k; r++) { const e = mk(c, r, step.grid[c][r]); ncol.push(e); fresh.push([c, r]); falls.push(fallIn(e, c, r, k + 0.5, c * 40 + (k - 1 - r) * 30)); }
        keep.forEach(([e, r0], i) => {
          const r = k + i, key = step.grid[c][r];
          if (key != null && e._key !== key) { e._key = key; e.dataset.k = key; e.replaceChildren(ctx.symNode(key)); }
          e.classList.remove('win');
          const [x, y] = pos(c, r); e.style.transform = 'translate(' + x + 'px,' + y + 'px)';
          if (r !== r0) { moved.push([c, r]); falls.push(fallIn(e, c, r, r - r0, c * 40)); }
          ncol.push(e);
        });
        return ncol;
      });
      await Promise.all(falls);
      ctx.sound('stop', { pitch: 1.3 });
      // хто новий, а хто зсунувся (поле ПІСЛЯ падіння) — для своєї пружинки; також подія 'drop'
      ctx.lastDrop = { fresh, moved };
      ctx.emit('drop', ctx.lastDrop);
      return ctx.lastDrop;
    }
    async function morph(list) {
      list.forEach(([c, r, key]) => {
        const e = cells[c][r]; e._key = key; e.dataset.k = key; e.replaceChildren(ctx.symNode(key));
        e.classList.remove('sk-morph'); void e.offsetWidth; e.classList.add('sk-morph');
      });
      ctx.sound('coin'); await ctx.wait(dur(450));
    }
    const api = {
      kind: 'drop', el, cols, rows, size: S, gap, width: W, height: H,
      spin, cascade, morph, set: setNow, slam: () => false, linesEl: lines,
      cell: (c, r) => cells[c] && cells[c][r],
      center: (c, r) => { const [x, y] = pos(c, r); return [x + S / 2, y + S / 2]; },
      grid: () => cells.map((col) => col.map((e) => e._key)),
      cells: () => cells.flat(),
      colEl: (c) => colEls[c],
      get busy() { return false; },
    };
    addLines(api, lines);
    setNow(o.initial || ctx.keepGrid && { grid: ctx.keepGrid } || { grid: Array.from({ length: cols }, () => Array.from({ length: rows }, pickInit)) });
    return api;
  }
  // weights: { ключ: вага } — початкове поле не з рівномірного пулу; нема — рівномірно з pool
  function weighted(w, pool) {
    const ks = w ? Object.keys(w).filter((k) => w[k] > 0) : [];
    if (!ks.length) return () => SK.rnd.pick(pool);
    const tot = ks.reduce((a, k) => a + w[k], 0);
    return () => { let x = Math.random() * tot; for (const k of ks) { x -= w[k]; if (x < 0) return k; } return ks[ks.length - 1]; };
  }
  // Барабани теж уміють morph (напр. дикі, що з'являються)
  function reelsMorph(api, ctx) {
    api.morph = async function (list) {
      list.forEach(([c, r, key]) => { const e = api.cell(c, r); e._key = key; e.dataset.k = key; e.replaceChildren(ctx.symNode(key)); e.classList.remove('sk-morph'); void e.offsetWidth; e.classList.add('sk-morph'); });
      ctx.sound('coin'); await ctx.wait(ctx.turbo ? 250 : 450);
    };
  }

  // ---------- Спільні кроки сценарію ----------
  // Кожен крок: async (step, ctx). Автомат додає свої в machine.steps (вони мають пріоритет).
  // «Найбільший можливий виграш»: стеля оберту (table.cap, що slot.js кладе в machine.table) — підпис банера
  SK.CAP_TEXT = 'Найбільший можливий виграш';
  function capSub(ctx) { const t = ctx.machine && ctx.machine.table, cap = (t && t.cap) || ctx.machine.cap; return cap ? 'стеля — ' + cap + '× ставки' : ''; }
  SK.steps = {
    async spin(s, ctx) { await ctx.reels.spin(s); },
    async set(s, ctx) { ctx.reels.set(s); },
    async win(s, ctx) {
      const items = s.items || [s];
      const amount = s.amount != null ? s.amount : items.reduce((a, it) => a + (it.amount || 0), 0);
      ctx.showWin(items);
      ctx.lastWins = items;
      ctx.emit('win', s);
      const m = amount / ctx.bet;
      ctx.sound(m >= 5 ? 'big' : (ctx.machine.sounds && ctx.machine.sounds.win) || 'win');
      const cap = ctx.script && ctx.script.win >= SK.TIERS[0].x * ctx.bet ? 700 : 2600;
      await ctx.rollMeter(ctx.meter + amount, Math.min(cap, ctx.rollMs(amount)));
      await ctx.wait(s.hold != null ? s.hold : 350);
    },
    async cascade(s, ctx) {
      await ctx.reels.cascade(s);
      ctx.reels.cells().forEach((e) => e && e.classList.remove('dim'));   // після падіння поле знову світле
      if (s.n) ctx.emit('cascade', s.n);
    },
    async morph(s, ctx) { await ctx.reels.morph(s.cells); },
    async banner(s, ctx) {
      const sc = ctx.script;
      if (sc && (sc.capped || sc.cap === true) && /стел/i.test(s.text || '')) { await ctx.banner(SK.CAP_TEXT, Object.assign({}, s, { sub: capSub(ctx) || s.sub, ms: Math.max(s.ms || 0, 2200) })); return; }
      await ctx.banner(s.text, s);
    },
    async pause(s, ctx) { await ctx.wait(s.ms || 500); },
    async sound(s, ctx) { ctx.sound(s.name); },
    async clear(s, ctx) { ctx.clearWin(); },
    async bonusIn(s, ctx) {
      ctx.fs = { left: s.count || 0, total: s.count || 0, won: 0 };
      ctx.root.classList.add('sk-infs'); ctx.updateHud();
      ctx.sound('bonus');
      if (ctx.machine.bonusIn) { await ctx.machine.bonusIn(s, ctx); return; }
      ctx.setScene('bonus');
      const ov = ctx.overlay('sk-bonus-in',
        '<div class="sk-ov-card"><div class="sk-ov-t">' + esc(s.title || 'Вільні оберти') + '</div>'
        + '<div class="sk-ov-n">' + esc(s.count) + '</div><div class="sk-ov-s">' + esc(s.sub || 'вільних обертів') + '</div>'
        + '<div class="sk-ov-hint">тисни, щоб почати</div></div>');
      ctx.fx.at(ov.querySelector('.sk-ov-n'), { kind: 'spark', n: 40, speed: 600, size: 10 });
      await ctx.wait(ctx.auto ? 1800 : 3200, true);
      await ctx.closeOverlay(ov);
    },
    async fs(s, ctx) {
      if (!ctx.fs) ctx.fs = { left: 0, total: 0, won: 0 };
      if (s.add) { ctx.fs.total += s.add; ctx.fs.left += s.add; ctx.updateHud(); await ctx.banner('+' + s.add + ' вільних', { ms: 1400 }); }
      if (s.left != null) ctx.fs.left = s.left;
      ctx.updateHud();
    },
    async bonusOut(s, ctx) {
      const total = s.total != null ? s.total : ctx.meter;
      if (ctx.machine.bonusOut) await ctx.machine.bonusOut(s, ctx);
      else {
        const ov = ctx.overlay('sk-bonus-out',
          '<div class="sk-ov-card"><div class="sk-ov-t">' + esc(s.title || 'Бонус приніс') + '</div><div class="sk-ov-n">0</div><div class="sk-ov-s">🏺</div></div>');
        const n = ov.querySelector('.sk-ov-n');
        ctx.sound('big');
        await ctx.roll(0, total, Math.min(4000, ctx.rollMs(total) + 800), (v) => { n.textContent = fmt(v); });
        ctx.fx.at(n, { kind: 'coin', n: 50, speed: 700 });
        await ctx.wait(1800, true);
        await ctx.closeOverlay(ov);
        ctx.setScene('base');
      }
      ctx.fs = null; ctx.root.classList.remove('sk-infs'); ctx.updateHud();
    },
  };

  // Скарбничка Глека: найбільше свято. Сума не входить у script.win — кіт зараховує її сам.
  SK.steps.jackpot = async function (s, ctx) {
    const amt = Math.max(0, Math.round(s.amount || 0));
    ctx.emit('jackpot', amt);
    ctx.sound('bonus');
    const ov = ctx.overlay('sk-big sk-jackpot', '<div class="sk-big-rays"></div><div class="sk-big-c"><div class="sk-jk-pot">🏺</div>'
      + '<div class="sk-big-t">Скарбничка Глека!</div><div class="sk-big-n">0</div><div class="sk-big-x">' + esc(s.sub || 'уся — тобі') + '</div></div>');
    ov.dataset.tier = 'jackpot';
    const N = ov.querySelector('.sk-big-n');
    const rr = () => ctx.root.getBoundingClientRect();
    const burst = (k) => { const r = rr(); ctx.fx.burst(r.width / 2, r.height * 0.42, { kind: k, n: k === 'coin' ? 60 : 90, speed: 1000, size: k === 'coin' ? 15 : 10 }); };
    burst('confetti'); burst('coin');
    ctx.fx.rain({ kind: 'coin', ms: 9000, rate: 44, size: 15 });
    const tick = ctx.interval(() => burst('confetti'), 1500);
    await ctx.roll(0, amt, clamp(ctx.rollMs(amt) + 2600, 3200, 8000), (v) => { N.textContent = fmt(v); });
    ctx.clearTimer(tick);
    N.classList.add('done'); ctx.sound('big'); burst('coin');
    ctx.addBalance(amt); ctx.sound('coin');
    if (SK.live && SK.live.jackpot != null) SK.live.jackpot = 0;   // впала — тікер підхопить нову суму з наступного опитування
    await ctx.wait(3200, true);
    ctx.fx.stopRain();
    await ctx.closeOverlay(ov);
  };

  // ---------- Монтування ----------
  // SlotKit.mount(el, idАбоАвтомат, {api, balance, onBalance}) → екземпляр
  SK.mount = function (host, idOrMachine, opts) {
    opts = opts || {};
    const machine = typeof idOrMachine === 'string' ? SK.machines[idOrMachine] : idOrMachine;
    if (!machine) throw new Error('SlotKit: нема автомата ' + idOrMachine);
    const id = machine.id;
    const art = (window.SlotArt || {})[id] || null;
    const loop = Loop();
    const timers = new Set(), intervals = new Set(), offs = [];
    const handlers = {};
    const tplCache = new Map();
    const bets = (opts.bets && opts.bets.length ? opts.bets : null) || machine.bets || SK.BETS;

    // спільні градієнти арту — один раз на сторінку
    if (art && art.extras && art.extras.defs && !document.getElementById('sk-defs-' + id)) {
      const d = div('sk-defs'); d.id = 'sk-defs-' + id; d.setAttribute('aria-hidden', 'true'); d.innerHTML = art.extras.defs; document.body.appendChild(d);
    }

    const root = div('sk-root slot-' + id);
    root.tabIndex = -1;
    root.innerHTML = '<div class="sk-scene"></div>'
      + '<div class="sk-box"><div class="sk-ticker"></div><div class="sk-area"></div><div class="sk-hud"></div><div class="sk-layer"></div></div>';
    host.appendChild(root);
    const $ = (s) => root.querySelector(s);
    const box = $('.sk-box'), area = $('.sk-area'), hud = $('.sk-hud'), layer = $('.sk-layer'), sceneEl = $('.sk-scene'), ticker = $('.sk-ticker');

    let skipFns = [];
    const anims = new Set();
    const ctx = {
      id, machine, art, root, box, area, hud, layer, loop, api: opts.api || null, bets,
      bet: opts.bet && bets.includes(opts.bet) ? opts.bet : bets[Math.min(2, bets.length - 1)],
      balance: opts.balance != null ? opts.balance : 10000,
      state: machine.initialState ? machine.initialState() : {},
      turbo: false, auto: 0, autoStopBonus: true, busy: false,
      meter: 0, fs: null, lastWin: 0, lastWins: null, script: null, orient: 'land', scale: 1, keepGrid: null,
      reels: null, fx: null,
      // події: spinStart, spinEnd(script), reelStop(c), tease(c), win(step), meter(v), bigwin(tier), resize, idle
      on(ev, fn) { (handlers[ev] = handlers[ev] || []).push(fn); },
      emit(ev, a, b) { (handlers[ev] || []).forEach((f) => { try { f(a, b); } catch (e) { console.error(e); } }); const h = machine['on' + ev[0].toUpperCase() + ev.slice(1)]; if (h) try { h(ctx, a, b); } catch (e) { console.error(e); } },
      sound(name, o) { SK.sound.play(name, o); },
      timeout(fn, ms) { const t = setTimeout(() => { timers.delete(t); fn(); }, ms); timers.add(t); return t; },
      interval(fn, ms) { const t = setInterval(fn, ms); intervals.add(t); return t; },
      clearTimer(t) { clearTimeout(t); clearInterval(t); timers.delete(t); intervals.delete(t); },
      listen(target, ev, fn, o) { target.addEventListener(ev, fn, o); offs.push(() => target.removeEventListener(ev, fn, o)); },
      // Чекання, яке пропускається тапом/пробілом (skippable=true — лише тапом, без турбо)
      wait(ms, tapOnly) {
        const real = ctx.turbo ? ms * (tapOnly ? 0.6 : 0.5) : ms;   // заставки (tapOnly) у турбо — трохи коротші
        return new Promise((res) => { let done = false; const fin = () => { if (done) return; done = true; skipFns = skipFns.filter((f) => f !== fin); res(); }; ctx.timeout(fin, real); skipFns.push(fin); });
      },
      skip() { const f = skipFns; skipFns = []; f.forEach((x) => x()); anims.forEach((a) => { try { a.finish(); } catch (e) { /* уже знята */ } }); anims.clear(); },
      // WAAPI, яку пропуск (тап/пробіл) доводить до кінця; у турбо — коротша. → Animation
      animate(el, kf, o) {
        o = Object.assign({}, typeof o === 'number' ? { duration: o } : o);
        if (ctx.turbo && o.duration) o.duration *= 0.6;
        const a = el.animate(kf, o); anims.add(a);
        a.finished.then(() => anims.delete(a), () => anims.delete(a));
        return a;
      },
      // політ html від from до to (елементи будь-де в автоматі; масштаб кіта врахований) → Promise
      flyTo(from, to, html, o) {
        o = o || {};
        const f = div('sk-fly ' + (o.cls || ''), html || ''), [x0, y0] = ctx.rel(from, box), [x1, y1] = ctx.rel(to, box);
        f.style.left = x0 + 'px'; f.style.top = y0 + 'px';
        layer.appendChild(f);
        const dx = x1 - x0, dy = y1 - y0, arc = o.arc != null ? o.arc : -Math.min(160, Math.hypot(dx, dy) * 0.3);
        const a = ctx.animate(f, [
          { transform: 'translate(-50%,-50%) scale(' + (o.from || 1) + ')', opacity: 1 },
          { transform: 'translate(calc(-50% + ' + dx / 2 + 'px),calc(-50% + ' + (dy / 2 + arc) + 'px)) scale(' + ((o.from || 1) + (o.to || 0.6)) / 2 * 1.15 + ')', opacity: 1, offset: 0.5 },
          { transform: 'translate(calc(-50% + ' + dx + 'px),calc(-50% + ' + dy + 'px)) scale(' + (o.to || 0.6) + ')', opacity: o.fade === false ? 1 : 0.2 },
        ], { duration: o.ms || 650, easing: o.easing || 'cubic-bezier(.45,0,.3,1)', delay: o.delay || 0, fill: 'forwards' });
        return a.finished.catch(() => {}).then(() => { f.remove(); if (o.burst && to.isConnected) ctx.fx.at(to, o.burst); });
      },
      symHtml(key) {
        const s = art && art.symbols && art.symbols[key];
        return s && s.svg ? s.svg : '<div class="sk-ph" style="--h:' + hue(key) + '"><span>' + esc(key) + '</span></div>';
      },
      symNode(key) {
        let t = tplCache.get(key);
        if (!t) { t = document.createElement('template'); t.innerHTML = ctx.symHtml(key); tplCache.set(key, t); }
        return t.content.cloneNode(true);
      },
      symName(key) { const s = art && art.symbols && art.symbols[key]; return (s && s.name) || key; },
      logoHtml() { return art && art.logo ? art.logo : '<div class="sk-logo-ph">' + esc(machine.title || id) + '</div>'; },
      // extras арту з заглушкою: ctx.extra('card', 'r') → svg або fallback
      extra(name, ...args) {
        const v = art && art.extras && art.extras[name];
        try { const r = typeof v === 'function' ? v(...args) : v; if (typeof r === 'string' && r.trim()) return r; } catch (e) { console.error(e); }
        return null;
      },
      makeReels(el, o) {
        const r = (o.kind || machine.spinStyle) === 'drop' ? DropGrid(ctx, el, o) : Reels(ctx, el, Object.assign({ strips: machine.reels }, o));
        if (r.kind === 'reels') reelsMorph(r, ctx);
        if (!ctx.reels) ctx.reels = r;
        return r;
      },
      cell(c, r) { return ctx.reels.cell(c, r); },
      // тихо перетворити клітинку без очікування (morph чекає ~450 мс на крок)
      morphCell(c, r, key) {
        const e = ctx.reels && ctx.reels.cell(c, r); if (!e) return null;
        e._key = key; e.dataset.k = key; e.replaceChildren(ctx.symNode(key));
        e.classList.remove('sk-morph', 'win', 'dim', 'glint'); void e.offsetWidth; e.classList.add('sk-morph');
        return e;
      },
      // центр елемента в px дизайну відносно base (за замовчуванням ctx.area) — для польотів між елементами
      rel(el, base) {
        base = base || area;   // base може бути й box (коробка дизайну) — тоді px від її лівого верхнього кута
        const a = base.getBoundingClientRect(), b = el.getBoundingClientRect(), k = (a.width / (base.offsetWidth || a.width)) || 1;
        return [(b.left - a.left + b.width / 2) / k, (b.top - a.top + b.height / 2) / k];
      },
      // клітинка поля в px поля ctx.reels.el: { x, y, w, h }
      cellBox(c, r) {
        const R = ctx.reels, [x, y] = R.center(c, r), w = R.cellW || R.size, h = R.size;
        return { x: x - w / 2, y: y - h / 2, w, h };
      },
      // своя сітка поверх ctx.reels: шар із гніздом на кожну клітинку; hide — сховати барабани під ним
      gridLayer(cls, o) {
        const R = ctx.reels; if (!R) return null;
        const L = div('sk-glayer ' + (cls || '')); const slots = [];
        for (let c = 0; c < R.cols; c++) {
          slots.push([]);
          for (let r = 0; r < R.rows; r++) {
            const b = ctx.cellBox(c, r), e = div('sk-gslot');
            e.style.cssText = 'left:' + b.x + 'px;top:' + b.y + 'px;width:' + b.w + 'px;height:' + b.h + 'px';
            e.dataset.c = c; e.dataset.r = r; L.appendChild(e); slots[c].push(e);
          }
        }
        R.el.appendChild(L);
        const api = { el: L, slot: (c, r) => slots[c] && slots[c][r], hide(on) { ctx.hideReels(on); }, remove() { L.remove(); ctx.hideReels(false); } };
        if (o && o.hide) api.hide(true);
        return api;
      },
      hideReels(on) { if (!ctx.reels) return; ctx.reels.hidden = !!on; ctx.reels.el.classList.toggle('sk-rhide', !!on); },
      clearWin() {
        if (!ctx.reels) return;
        ctx.reels.cells().forEach((e) => e && e.classList.remove('win', 'glint', 'dim'));
        ctx.reels.clearLines(); root.querySelectorAll('.sk-float').forEach((e) => e.remove());
        stopCycle();
      },
      // items: [{cells:[[c,r]], amount, line?, color?}]
      showWin(items, noFloat) {
        const R = ctx.reels, ls = [];
        const winSet = new Set();
        items.forEach((it, i) => {
          (it.cells || []).forEach(([c, r]) => { const e = R.cell(c, r); if (e) { e.classList.add('win'); winSet.add(e); } });
          if (it.line != null && machine.lines) ls.push({ pts: machine.lines[it.line].map((r, c) => [c, r]), n: it.line, color: it.color });
          else if (it.path) ls.push({ pts: it.path, n: i, color: it.color });
        });
        if (machine.dimLosers !== false) R.cells().forEach((e) => e && e.classList.toggle('dim', !winSet.has(e)));
        R.showLines(ls);
        if (!noFloat) items.forEach((it) => {
          if (!it.amount || !it.cells || !it.cells.length) return;
          const mid = it.cells[Math.floor(it.cells.length / 2)];
          const [x, y] = R.center(mid[0], mid[1]);
          const f = div('sk-float', '+' + fmt(it.amount)); f.style.left = x + 'px'; f.style.top = y + 'px';
          if (it.line != null) f.style.color = SK.lineColor(it.line);
          R.el.appendChild(f); ctx.timeout(() => f.remove(), 1600);
        });
      },
      rollMs(amount) { const m = amount / ctx.bet; return clamp(500 + 650 * Math.log2(1 + m), 500, 6000); },
      // Загальний лічильник: from→to за ms, onUpd(v). Пропуск — тап/пробіл.
      roll(from, to, ms, onUpd) {
        if (ctx.turbo) ms *= 0.6;
        return new Promise((res) => {
          const t0 = performance.now(); let lastTick = 0, done = false;
          const fin = () => { if (done) return; done = true; skipFns = skipFns.filter((f) => f !== fin); onUpd(to); res(); };
          skipFns.push(fin);
          loop.add((t) => {
            if (done) return false;
            const u = clamp((t - t0) / ms, 0, 1), e = 1 - Math.pow(1 - u, 2.2);
            onUpd(from + (to - from) * e);
            const gapMs = 140 - 100 * u;
            if (t - lastTick > gapMs) { lastTick = t; ctx.sound('tick', { p: u }); }
            if (u >= 1) { fin(); return false; }
            return true;
          });
        });
      },
      // стеля (script.capped / cap: true): лічильник не показує більше, ніж зараховано (script.win + Скарбничка)
      rollMeter(to, ms) { const sc = ctx.script; if (sc && (sc.capped || sc.cap === true) && sc.win != null) to = Math.min(to, sc.win + (sc.jackpot || 0)); const from = ctx.meter; ctx.meter = to; return ctx.roll(from, to, ms, (v) => setMeter(v)); },
      setScene(which) {
        ctx.sceneNow = which;
        const sc = art && art.scene || {};
        if (which === 'bonus' && sc.bonus) {
          if (!sceneEl.querySelector('.sk-sc-bonus')) { const b = div('sk-sc sk-sc-bonus', sc.bonus); sceneEl.appendChild(b); }
          void sceneEl.offsetWidth;
        }
        sceneEl.classList.toggle('bonus', which === 'bonus');
        root.classList.toggle('sk-bonus', which === 'bonus');
        if (which !== 'bonus') ctx.timeout(() => { if (ctx.sceneNow !== 'bonus') { const b = sceneEl.querySelector('.sk-sc-bonus'); if (b) b.remove(); } }, 900);
      },
      overlay(cls, html) {
        const ov = div('sk-ov ' + cls, html); layer.appendChild(ov);
        ov.addEventListener('pointerdown', () => ctx.skip());
        requestAnimationFrame(() => ov.classList.add('in'));
        return ov;
      },
      closeOverlay(ov) { ov.classList.remove('in'); ov.classList.add('out'); return new Promise((r) => ctx.timeout(() => { ov.remove(); r(); }, 320)); },
      async banner(text, o) {
        o = o || {};
        const b = ctx.overlay('sk-banner ' + (o.kind ? 'sk-b-' + o.kind : ''), '<div class="sk-banner-t">' + esc(text) + '</div>' + (o.sub ? '<div class="sk-banner-s">' + esc(o.sub) + '</div>' : ''));
        await ctx.wait(o.ms || 1500);
        await ctx.closeOverlay(b);
      },
      addBalance(n) { ctx.balance += n; updateHud(); if (opts.onBalance) opts.onBalance(ctx.balance); },
      // баланс від сервера (сайт): поставити як є, без анімації
      setBalance(n) { if (n == null || !isFinite(n)) return; ctx.balance = n; updateHud(); },
      say(text, ms) { const m = hud.querySelector('.sk-msg'); if (!m) return; m.textContent = text; m.classList.remove('pulse'); void m.offsetWidth; m.classList.add('pulse'); if (ms) ctx.timeout(() => { if (m.textContent === text) m.textContent = ''; }, ms); },
      spin: () => doSpin(),
      updateHud: () => updateHud(),
    };
    ctx.fx = FX(root, loop);

    // ----- HUD -----
    hud.innerHTML =
      '<div class="sk-stat sk-bal"><small>баланс</small><b><i>🏺</i> <span class="sk-bal-v">0</span></b></div>'
      + '<div class="sk-stat sk-betbox"><small>ставка</small><div class="sk-bet"><button class="sk-bm" aria-label="менша ставка">−</button><b class="sk-bet-v">0</b><button class="sk-bp" aria-label="більша ставка">+</button></div></div>'
      + '<div class="sk-stat sk-winbox"><small class="sk-win-l">виграш</small><b class="sk-win-v">0</b><span class="sk-msg"></span></div>'
      + '<div class="sk-extra"></div>'
      + '<div class="sk-ctrl">'
      + '<button class="sk-ico sk-info" title="Таблиця виплат" aria-label="таблиця виплат">ⓘ</button>'
      + '<button class="sk-ico sk-snd" title="Звук" aria-label="звук">🔈</button>'
      + '<button class="sk-tog sk-turbo" title="Турбо"><span>⚡</span>турбо</button>'
      + '<div class="sk-autowrap"><button class="sk-tog sk-auto" title="Автогра"><span>↻</span>авто</button></div>'
      + '</div>'
      + '<button class="sk-spin" aria-label="крутити"><svg viewBox="0 0 100 100" class="sk-spin-i"><path d="M50 18a32 32 0 1 1-30.4 22" /><path d="M14 24l6 17 17-6" /></svg><span class="sk-spin-t">крутити</span></button>';
    ctx.hudExtra = hud.querySelector('.sk-extra');
    const spinBtn = hud.querySelector('.sk-spin');
    function setMeter(v) { hud.querySelector('.sk-win-v').textContent = fmt(v); ctx.emit('meter', v); }
    function updateHud() {
      hud.querySelector('.sk-bal-v').textContent = fmt(ctx.balance);
      hud.querySelector('.sk-bet-v').textContent = fmt(ctx.bet);
      const lab = hud.querySelector('.sk-win-l');
      lab.textContent = ctx.fs ? 'вільні ' + ctx.fs.left + ' / ' + ctx.fs.total : 'виграш';
      hud.querySelector('.sk-snd').textContent = SK.sound.on ? '🔊' : '🔈';
      hud.querySelector('.sk-snd').classList.toggle('on', SK.sound.on);
      hud.querySelector('.sk-turbo').classList.toggle('on', ctx.turbo);
      hud.querySelector('.sk-auto').classList.toggle('on', !!ctx.auto);
      hud.querySelector('.sk-auto').innerHTML = ctx.auto ? '<span>■</span>' + (ctx.auto === Infinity ? '∞' : ctx.auto) : '<span>↻</span>авто';
      const bi = bets.indexOf(ctx.bet);
      hud.querySelector('.sk-bm').disabled = ctx.busy || bi <= 0;
      hud.querySelector('.sk-bp').disabled = ctx.busy || bi >= bets.length - 1;
      spinBtn.classList.toggle('busy', ctx.busy);
      spinBtn.classList.toggle('poor', !ctx.busy && ctx.balance < ctx.bet);
      hud.querySelector('.sk-spin-t').textContent = ctx.busy ? (ctx.auto ? 'стоп' : 'швидше') : 'крутити';
      root.classList.toggle('sk-busy', ctx.busy);
    }
    const setBet = (d) => { if (ctx.busy) return; const i = clamp(bets.indexOf(ctx.bet) + d, 0, bets.length - 1); ctx.bet = bets[i]; ctx.sound('click'); updateHud(); ctx.emit('bet', ctx.bet); };
    ctx.listen(hud.querySelector('.sk-bm'), 'click', () => setBet(-1));
    ctx.listen(hud.querySelector('.sk-bp'), 'click', () => setBet(1));
    ctx.listen(hud.querySelector('.sk-snd'), 'click', () => { SK.sound.set(!SK.sound.on); updateHud(); ctx.sound('click'); });
    ctx.listen(hud.querySelector('.sk-turbo'), 'click', () => { ctx.turbo = !ctx.turbo; root.classList.toggle('sk-turbo-on', ctx.turbo); updateHud(); });
    ctx.listen(hud.querySelector('.sk-info'), 'click', () => openPaytable());
    ctx.listen(hud.querySelector('.sk-auto'), 'click', () => { if (ctx.auto) { ctx.auto = 0; updateHud(); } else openAutoMenu(); });
    ctx.listen(spinBtn, 'click', () => act());

    function openAutoMenu() {
      closeMenus();
      const wrap = hud.querySelector('.sk-autowrap');
      const m = div('sk-menu',
        '<div class="sk-menu-t">автогра</div><div class="sk-menu-n">'
        + [10, 25, 50, 100, Infinity].map((n) => '<button data-n="' + n + '">' + (n === Infinity ? '∞' : n) + '</button>').join('')
        + '</div><label class="sk-chk"><input type="checkbox"' + (ctx.autoStopBonus ? ' checked' : '') + '> стоп на бонусі</label>');
      wrap.appendChild(m);
      m.querySelectorAll('button').forEach((b) => b.addEventListener('click', (e) => {
        e.stopPropagation(); ctx.auto = Number(b.dataset.n); ctx.autoStopBonus = m.querySelector('input').checked; m.remove(); updateHud(); if (!ctx.busy) doSpin();
      }));
      m.addEventListener('click', (e) => e.stopPropagation());
      ctx.timeout(() => ctx.listen(document, 'click', closeMenus), 0);
    }
    function closeMenus() { root.querySelectorAll('.sk-menu').forEach((m) => m.remove()); }

    function openPaytable() {
      closeMenus();
      const unit = machine.payUnit || 1;
      const rows = (machine.paytable || []).map((row) => {
        // row.labels: { 12: '12+', 10: '10–11' } — свій підпис кількості замість «N×»/«N+»
        const pays = Object.keys(row.pays || {}).sort((a, b) => parseFloat(b) - parseFloat(a)).map((n) => '<div><i>' + esc(row.labels && row.labels[n] != null ? row.labels[n] : n + (row.unit || '×')) + '</i> <b>' + fmt(row.pays[n] * unit * ctx.bet) + '</b></div>').join('');
        return '<div class="sk-pt-row"><div class="sk-pt-sym sk-cell">' + ctx.symHtml(row.key) + '</div><div class="sk-pt-txt"><div class="sk-pt-n">' + esc(ctx.symName(row.key)) + '</div>' + (row.note ? '<div class="sk-pt-note">' + esc(row.note) + '</div>' : '') + '</div><div class="sk-pt-pay">' + pays + '</div></div>';
      }).join('');
      let linesH = '';
      if (machine.lines && machine.grid) {
        linesH = '<h4>лінії</h4><div class="sk-pt-lines">' + machine.lines.map((ln, i) => {
          let g = '<div class="sk-pt-line"><b style="color:' + SK.lineColor(i) + '">' + (i + 1) + '</b><div class="sk-pt-mini" style="grid-template-columns:repeat(' + machine.grid.cols + ',1fr)">';
          for (let r = 0; r < machine.grid.rows; r++) for (let c = 0; c < machine.grid.cols; c++) g += '<span' + (ln[c] === r ? ' style="background:' + SK.lineColor(i) + '"' : '') + '></span>';
          return g + '</div></div>';
        }).join('') + '</div>';
      }
      const rules = typeof machine.rules === 'function' ? machine.rules(ctx) : (machine.rules || '');
      const ov = div('sk-modal', '<div class="sk-modal-c"><button class="sk-x" aria-label="закрити">✕</button><h3>' + esc(machine.title || id) + ' — виплати</h3>'
        + '<div class="sk-pt-bet">за ставки ' + fmt(ctx.bet) + ' 🏺</div><div class="sk-pt">' + rows + '</div>' + linesH + '<div class="sk-pt-rules">' + rules + '</div></div>');
      layer.appendChild(ov);
      const close = () => ov.remove();
      ov.addEventListener('click', (e) => { if (e.target === ov || e.target.classList.contains('sk-x')) close(); });
    }

    // ----- тікер «Скарбничка Глека» -----
    ticker.innerHTML = '<span class="sk-jp"><i>🏺</i> скарбничка Глека <b class="sk-jp-v"></b><small class="sk-jp-must"></small></span><span class="sk-feed"><span class="sk-feed-t"></span></span>';
    let feedI = SK.rnd.int(SK.feed.length), jpShown = null;
    const jpEl = ticker.querySelector('.sk-jp-v'), mustEl = ticker.querySelector('.sk-jp-must');
    // Живу суму (SK.live) показуємо, що м'яко доростає до свіжої: раз на секунду чверть різниці
    const tickJp = () => {
      const L = SK.live;
      if (!L || L.jackpot == null) { jpEl.textContent = fmt(SK.jackpot()); return; }
      const t = L.jackpot;
      jpShown = jpShown == null || t < jpShown ? t : Math.min(t, jpShown + Math.max(1, Math.ceil((t - jpShown) / 4)));
      jpEl.textContent = fmt(jpShown);
      const m = L.mustHit > 0 ? 'впаде до ' + fmt(L.mustHit) : '';
      if (mustEl.textContent !== m) mustEl.textContent = m;
    };
    const feedNow = () => (SK.live ? SK.live.lines || [] : SK.feed);
    const tickFeed = () => {
      const t = ticker.querySelector('.sk-feed-t'), list = feedNow();
      if (!list.length) { t.textContent = ''; return; }
      t.classList.remove('in'); void t.offsetWidth; t.textContent = list[feedI++ % list.length]; t.classList.add('in');
    };
    tickJp(); tickFeed(); ctx.interval(tickJp, 1000); ctx.interval(tickFeed, 7000);

    // ----- сцена -----
    const sc = art && art.scene;
    if (sc && sc.base) sceneEl.appendChild(div('sk-sc sk-sc-base', sc.base));

    // ----- вписування в контейнер -----
    function fit() {
      const w = root.clientWidth, h = root.clientHeight; if (!w || !h) return;
      const orient = w / h < 0.78 ? 'port' : 'land';
      const L = SK.LAYOUT[orient];
      const k = Math.min(w / L.w, h / L.h);
      box.style.width = L.w + 'px'; box.style.height = L.h + 'px';
      box.style.setProperty('--ticker', L.ticker + 'px'); box.style.setProperty('--hud', L.hud + 'px');
      box.style.transform = 'translate(-50%,-50%) scale(' + k.toFixed(4) + ')';
      ctx.scale = k;
      if (orient !== ctx.orient || !ctx.built) {
        if (ctx.busy && ctx.built) { ctx.pendingOrient = true; return; }
        rebuild(orient);
      }
      ctx.emit('resize');
    }
    function rebuild(orient) {
      if (ctx.built && ctx.reels) ctx.keepGrid = ctx.reels.grid();
      if (ctx.built && machine.unbuild) machine.unbuild(ctx);
      ctx.orient = orient; root.classList.toggle('sk-port', orient === 'port'); root.classList.toggle('sk-land', orient === 'land');
      area.innerHTML = ''; ctx.hudExtra.innerHTML = ''; ctx.reels = null;
      ctx.areaW = SK.LAYOUT[orient].w; ctx.areaH = SK.LAYOUT[orient].h - SK.LAYOUT[orient].ticker - SK.LAYOUT[orient].hud;
      if (machine.build) machine.build(ctx);
      else { const st = div('sk-stage'); area.appendChild(st); ctx.makeReels(st, { cols: machine.grid.cols, rows: machine.grid.rows, size: orient === 'port' ? 56 : 110 }); }
      ctx.built = true; ctx.pendingOrient = false;
    }
    const ro = new ResizeObserver(() => fit());
    ro.observe(root);
    fit(); updateHud();

    // ----- блиск у спокої: setTimeout, без rAF -----
    let glintT = 0;
    function glintLoop() {
      glintT = ctx.timeout(() => {
        if (!ctx.busy && ctx.reels && !ctx.reels.hidden && !ctx.fs && ctx.sceneNow !== 'bonus' && !document.hidden) {
          const cs = ctx.reels.cells().filter((e) => e && !e.classList.contains('win'));
          const e = SK.rnd.pick(cs); if (e) { e.classList.add('glint'); ctx.timeout(() => e.classList.remove('glint'), 1100); }
        }
        glintLoop();
      }, 2600 + Math.random() * 3200);
    }
    glintLoop();
    // по черзі показувати виграшні лінії після оберту (таймером)
    let cycleT = 0;
    function stopCycle() { if (cycleT) { ctx.clearTimer(cycleT); cycleT = 0; } }
    function startCycle() {
      const items = (ctx.lastWins || []).filter((it) => it.cells && it.cells.length);
      if (items.length < 2) return;
      let i = 0;
      const next = () => {
        if (ctx.busy || !ctx.reels) return;
        ctx.reels.cells().forEach((e) => e && e.classList.remove('win'));
        ctx.showWin([items[i % items.length]], true); i++;
        cycleT = ctx.timeout(next, 1400);
      };
      cycleT = ctx.timeout(next, 1600);
    }

    // ----- оберт -----
    function act() {
      if (layer.querySelector('.sk-modal')) return;
      if (ctx.busy) {
        if (ctx.auto) { ctx.auto = 0; updateHud(); }
        if (ctx.reels && ctx.reels.slam()) return;
        ctx.skip(); return;
      }
      doSpin();
    }
    async function doSpin(given) {
      if (ctx.busy) return;
      closeMenus();
      if (ctx.balance < ctx.bet && !given) {
        ctx.auto = 0; updateHud(); ctx.say('черепків бракує — зменш ставку', 2500);
        spinBtn.classList.remove('shake'); void spinBtn.offsetWidth; spinBtn.classList.add('shake'); return;
      }
      ctx.busy = true; ctx.meter = 0; setMeter(0); ctx.lastWins = null; ctx.say('');
      if (ctx.balance >= ctx.bet) ctx.addBalance(-ctx.bet);
      updateHud();
      ctx.emit('spinStart');
      let script;
      try {
        script = given || await (opts.api && opts.api.spin ? opts.api.spin(ctx.bet, ctx.state, ctx) : machine.spin(ctx.bet, ctx.state, ctx));
        ctx.script = script;
        await play(script);
        await finish(script);
      } catch (e) { console.error('SlotKit: збій оберту', e); }
      ctx.busy = false; ctx.script = null;
      if (script && script.state) ctx.state = script.state;
      updateHud();
      // після каскадів старі виграшні клітинки вже впали — у спокої перебирати нема чого
      if (machine.cycleWins === false || (script && script.steps && script.steps.some((s) => s.t === 'cascade'))) ctx.lastWins = null;
      ctx.emit('spinEnd', script);
      if (ctx.pendingOrient) fit();
      startCycle();
      if (ctx.auto) {
        // бонус: script.bonus, крок bonusIn або свої кроки з machine.bonusSteps (напр. ['holdIn'])
        const bsteps = ['bonusIn'].concat(machine.bonusSteps || []);
        const hadBonus = script && (script.bonus || script.hold || (script.steps && script.steps.some((s) => bsteps.includes(s.t))));
        if (hadBonus && ctx.autoStopBonus) ctx.auto = 0;
        else if (ctx.balance < ctx.bet) ctx.auto = 0;
        else { if (ctx.auto !== Infinity) ctx.auto--; if (ctx.auto) ctx.timeout(() => { if (ctx.auto && !ctx.busy) doSpin(); }, ctx.turbo ? 200 : 550); }
        updateHud();
      }
    }
    async function play(script) {
      for (const s of script.steps || []) {
        const h = (machine.steps && machine.steps[s.t]) || SK.steps[s.t];
        if (!h) { console.warn('SlotKit: невідомий крок', s.t); continue; }
        await h(s, ctx);
      }
    }
    async function finish(script) {
      const total = script.win || 0;
      ctx.lastWin = total;
      // стеля без свого банера в сценарії (hold, cluster) — кіт каже сам
      if ((script.capped || script.cap === true) && !(script.steps || []).some((s) => s.t === 'banner')) await ctx.banner(SK.CAP_TEXT, { sub: capSub(ctx), ms: 2200 });
      if (total > 0) {
        if (ctx.meter !== total) await ctx.rollMeter(total, ctx.meter ? 400 : ctx.rollMs(total));
        if (total >= SK.TIERS[0].x * ctx.bet) await bigWin(total);
        ctx.addBalance(total); ctx.sound('coin');
      }
    }
    // Сходинки заносів з ескалацією
    async function bigWin(total) {
      const bet = ctx.bet, tiers = SK.TIERS.filter((t) => total >= t.x * bet);
      const top = tiers[tiers.length - 1];
      const ms = { big: 3200, mega: 4800, epic: 6000 }[top.key];
      const ov = ctx.overlay('sk-big', '<div class="sk-big-rays"></div><div class="sk-big-c"><div class="sk-big-t">' + tiers[0].label + '</div><div class="sk-big-n">0</div><div class="sk-big-x"></div></div>');
      const T = ov.querySelector('.sk-big-t'), N = ov.querySelector('.sk-big-n'), X = ov.querySelector('.sk-big-x');
      let lvl = 0; ov.dataset.tier = tiers[0].key;
      ctx.sound('big'); ctx.emit('bigwin', tiers[0].key);
      ctx.fx.rain({ kind: 'coin', ms: ms, rate: 26, size: 13 });
      const burst = () => { const r = root.getBoundingClientRect(); ctx.fx.burst(r.width / 2, r.height * 0.45, { kind: 'confetti', n: 70, speed: 900, size: 9 }); ctx.fx.burst(r.width / 2, r.height * 0.45, { kind: 'shard', n: 18, speed: 700, size: 14 }); };
      burst();
      await ctx.roll(0, total, ms, (v) => {
        N.textContent = fmt(v); X.textContent = '×' + (v / bet).toFixed(1).replace('.0', '');
        while (lvl < tiers.length - 1 && v >= tiers[lvl + 1].x * bet) {
          lvl++; T.textContent = tiers[lvl].label; ov.dataset.tier = tiers[lvl].key;
          T.classList.remove('punch'); void T.offsetWidth; T.classList.add('punch');
          ctx.sound('level'); ctx.emit('bigwin', tiers[lvl].key); burst();
        }
      });
      if (lvl < tiers.length - 1) { lvl = tiers.length - 1; T.textContent = top.label; ov.dataset.tier = top.key; }
      N.classList.add('done');
      await ctx.wait(2400, true);
      ctx.fx.stopRain();
      await ctx.closeOverlay(ov);
    }

    // ----- клавіатура й пад -----
    const seen = () => root.isConnected && root.offsetParent !== null && !document.hidden;
    ctx.listen(window, 'keydown', (e) => {
      if (!seen()) return;
      if (e.code !== 'Space' && e.key !== ' ') { if (e.key === 'Escape') { closeMenus(); const m = layer.querySelector('.sk-modal'); if (m) m.remove(); } return; }
      const tg = e.target; if (tg && (tg.tagName === 'INPUT' || tg.tagName === 'TEXTAREA' || tg.isContentEditable)) return;
      e.preventDefault(); if (!e.repeat) act();
    });
    let padT = 0, padPrev = false;
    const padPoll = () => {
      const pads = navigator.getGamepads ? Array.from(navigator.getGamepads()).filter(Boolean) : [];
      if (!pads.length) { ctx.clearTimer(padT); padT = 0; return; }
      const down = seen() && pads.some((p) => p.buttons[0] && p.buttons[0].pressed);
      if (down && !padPrev) act();
      padPrev = down;
    };
    const padOn = () => { if (!padT) padT = ctx.interval(padPoll, 80); };
    ctx.listen(window, 'gamepadconnected', padOn);
    if (navigator.getGamepads && Array.from(navigator.getGamepads()).some(Boolean)) padOn();
    // тап по полю під час оберту — швидше
    ctx.listen(area, 'pointerdown', (e) => { if (ctx.busy && !e.target.closest('button,.sk-noskip')) act(); });

    const inst = {
      ctx, machine,
      spin: () => doSpin(),
      play: (script) => doSpin(script),
      demo(name) { const f = machine.demo && machine.demo[name]; if (f && !ctx.busy) return doSpin(f(ctx.bet, ctx.state, ctx)); },
      addBalance: (n) => ctx.addBalance(n),
      get busy() { return ctx.busy; },
      destroy() {
        ctx.auto = 0; ctx.skip(); loop.stop(); ctx.fx.destroy(); ro.disconnect();
        timers.forEach(clearTimeout); intervals.forEach(clearInterval); timers.clear(); intervals.clear();
        offs.forEach((f) => f()); if (machine.destroy) try { machine.destroy(ctx); } catch (e) { console.error(e); }
        root.remove();
      },
    };
    if (machine.mounted) machine.mounted(ctx, inst);
    return inst;
  };
})();
