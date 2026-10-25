/*
  Шаткуй! — наш Fruit Ninja (записка #62, docs/games/specs/shatkuy.md): Дядько Глек підкидає з воза овочі, ріжеш змахом.
  Чотири гри на одному модулі: 'shatkuy' (Класика), 'shatkuy-fair' (Ярмарок), 'shatkuy-zen' (Дзен), 'shatkuy-daily' (Шаткуй дня).
  Правила, розклад і очки — на сервері (Impl/Shatkuy.cs, ShatkuyCore.cs); тут сцена, лезо, сік і звук.

  Вид: { mode: 'classic'|'fair'|'zen'|'daily', phase: 'ready'|'go'|'over', now, t0, len, lives, misses, score, best, games,
    combo, gleks, big, cuts, frz: [[s, e]], dbl: [[s, e]], fruits: [{ id, k, at, d, x0, x1, h, sp, c, m }],
    end: null | { why, score, best, rec, combo, cuts, big, gleks }, k: { lag, look, frost, bigMax, glek }, daily? }
  Наміри: act('go'), input('cut', { c: [[id, w], …] }) — w — номер змаху (комбо й удари по великому кавуну).

  Час: t — справжні мс від t0 за годинником сервера (зсув — з `now`), g — ігрові мс: мороз іде вдвічі повільніше
  (та сама формула, що ShatkuyCore.Game). Розрізане малюємо одразу, очки — сервер плюс ще не підтверджене.
  Звук типово вимкнено (🔇 у куті) — поверх радіо інакше заважав би.
*/
(() => {
  'use strict';

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M3 13.2a5.6 5.6 0 0 1 0-8l8 8a5.6 5.6 0 0 1-8 0z" fill="#ff4d5e"/><path d="M3.6 12.6a4.7 4.7 0 0 1 0-6.8l6.8 6.8a4.7 4.7 0 0 1-6.8 0z" fill="#ffd0d4" opacity=".55"/>'
    + '<path d="M5.4 10.6l9-9" stroke="var(--accent)" stroke-width="1.8" stroke-linecap="round"/><path d="M5.4 10.6l2.2-2.2" stroke="#7a4a1e" stroke-width="2.6" stroke-linecap="round"/></svg>';

  // r — радіус у частках поля, flesh — м'якуш, juice — сік. Коди — як у ShatkuyCore.
  const KINDS = {
    s: { name: 'слива', r: 0.056, flesh: '#f2c14a', juice: '#8a2a9c' },
    b: { name: 'буряк', r: 0.06, flesh: '#c2185b', juice: '#b0103e' },
    a: { name: 'яблуко', r: 0.062, flesh: '#fff1c8', juice: '#ffe9a8' },
    g: { name: 'груша', r: 0.064, flesh: '#fbf3c4', juice: '#dfe88a' },
    d: { name: 'диня', r: 0.078, flesh: '#ffe98a', juice: '#ffd54a' },
    p: { name: 'гарбуз', r: 0.085, flesh: '#ffa23a', juice: '#ff8a1a' },
    k: { name: 'кавун', r: 0.095, flesh: '#ff3d55', juice: '#ff2a48' },
    r: { name: 'помідор', r: 0.058, flesh: '#ff7a5a', juice: '#e8261a' },
    m: { name: 'морква', r: 0.06, flesh: '#ffb04a', juice: '#ff8a1a' },
    G: { name: 'Дядьків глек', r: 0.072, flesh: '#c87a42', juice: '#a65a2a' },
    F: { name: 'крижана слива', r: 0.06, flesh: '#e6f7ff', juice: '#9fdcff' },
    X: { name: 'золоте яблуко', r: 0.064, flesh: '#fff3c0', juice: '#ffd54a' },
    Z: { name: 'кукурудза', r: 0.068, flesh: '#fff2a0', juice: '#ffe066' },
    B: { name: 'великий кавун', r: 0.17, flesh: '#ff3d55', juice: '#ff2a48' },
  };
  const DEF_K = { lag: 600, look: 1000, frost: 0.5, bigMax: 30, glek: 10 };
  const FROST_MS = 5000, DOUBLE_MS = 7000;

  const MODES = {
    classic: { title: 'Класика', icon: '🔪', rules: [
      'Шаткуй усе, що летить. Пропустиш три — кінець.',
      '<b>Дядьків глек</b> не чіпай: розіб\'єш — і Глек образиться, партії кінець.',
      'Три й більше одним махом — <b>комбо</b>. Кожні 100 очок повертають життя.'] },
    fair: { title: 'Ярмарок', icon: '🎪', rules: [
      'Хвилина ярмарку, життів нема — шаткуй скільки встигнеш.',
      '❄ <b>Крижана слива</b> — усе сповільнюється. ✨ <b>Золоте яблуко</b> — очки ×2. 🌽 <b>Кукурудза</b> — злива з воза.',
      '🍉 Наприкінці — <b>великий кавун</b>: лупи по ньому, скільки встигнеш. Глек — мінус 10.'] },
    zen: { title: 'Дзен', icon: '🍃', rules: [
      'Півтори хвилини спокою: самі плоди, ні глеків, ні промахів.',
      'Просто шаткуй. Три й більше одним махом — комбо.'] },
    daily: { title: 'Шаткуй дня', icon: '☀', rules: [
      'Сорок п\'ять секунд і <b>один віз на всіх</b> — те саме летить у кожного. Спроба одна.',
      '❄ мороз, ✨ ×2, 🌽 злива, 🍉 великий кавун наприкінці. Глек — мінус 10.'] },
  };

  const SAY = {
    ready: ['Серп нагострив? Ріж кавуна — і поїхали!', 'Свіженьке з городу! Хто шаткуватиме?', 'Ану, покажи, як ти шаткуєш!'],
    go: ['Лови!', 'Ану!', 'Тримай!', 'Летить!'],
    combo: ['Оце мах!', 'Ого, одним махом!', 'Як на весіллі!', 'Майстер!'],
    combo5: ['Та ти козак!', 'Шаткуєш, як вітер!', 'Аж свистить!'],
    glek: ['Мій глечик!!!', 'Ой лишенько, глек!', 'Та ти що, то ж глек!'],
    glekT: ['Ой, глечик! Мінус десять', 'Глек не чіпай!', 'Обережно з глеками!'],
    frost: ['Брр, мороз!', 'Ковзай, поки холодно!', 'Усе як у сні…'],
    dbl: ['Золоте! Удвічі!', 'Золото — і очки вдвічі!'],
    shower: ['Злива з воза!', 'Ловіть усе!', 'Сипле, як з відра!'],
    big: ['Кавунище! Лупи!', 'Бий його, бий!', 'Оце кавун!'],
    miss: ['Ой, проґавив!', 'Та куди ж він…', 'Не спи!'],
    time: ['Час!', 'Ярмарок скінчився!'],
    rec: ['Новий рекорд! Хвалю!', 'Такого ще не було!'],
    over: ['Ну, ще разок?', 'Непогано нашаткував!', 'Борщ буде!'],
  };
  const pick = (a) => a[Math.floor(Math.random() * a.length)];

  // ---------------------------------------------------------------------------------------------
  // Дрібниці
  // ---------------------------------------------------------------------------------------------
  const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
  const tms = (x) => (typeof x === 'number' ? x : x ? Date.parse(x) : NaN);
  const fmtN = (n) => Math.round(n).toString().replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const points = (n) => ((n % 100) >= 11 && (n % 100) <= 14 ? 'очок' : ({ 1: 'очко', 2: 'очки', 3: 'очки', 4: 'очки' })[n % 10] || 'очок');
  const store = {
    get(k, d) { try { const v = localStorage.getItem('shatkuy_' + k); return v == null ? d : JSON.parse(v); } catch (e) { return d; } },
    set(k, v) { try { localStorage.setItem('shatkuy_' + k, JSON.stringify(v)); } catch (e) { /* приватне вікно */ } },
  };
  const reduced = () => { try { return matchMedia('(prefers-reduced-motion: reduce)').matches; } catch (e) { return false; } };
  const plain = (k) => 'sbagdpkrm'.includes(k);
  const rnd = (a, b) => a + Math.random() * (b - a);

  /// Ігровий час: та сама формула, що ShatkuyCore.Game.
  function gameMs(t, frz, rate) {
    let slow = 0;
    for (const [s, e] of frz) { if (t <= s) break; slow += Math.min(t, e) - s; }
    return Math.floor(t - slow * (1 - rate));
  }
  const inside = (spans, t) => spans.some(([s, e]) => t >= s && t < e);

  // ---------------------------------------------------------------------------------------------
  // Звук — синтез WebAudio, без файлів. Типово вимкнено.
  // ---------------------------------------------------------------------------------------------
  const Snd = (() => {
    let ac = null, out = null, noise = null, on = !!store.get('snd', false), lastSquish = 0;
    function A() {
      if (!on) return null;
      if (!ac) {
        try {
          ac = new (window.AudioContext || window.webkitAudioContext)();
          out = ac.createGain(); out.gain.value = 0.32;
          const comp = ac.createDynamicsCompressor(); out.connect(comp); comp.connect(ac.destination);
          noise = ac.createBuffer(1, ac.sampleRate, ac.sampleRate);
          const d = noise.getChannelData(0); for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
        } catch (e) { ac = null; return null; }
      }
      if (ac.state === 'suspended') ac.resume().catch(() => {});
      return ac;
    }
    function hiss(t, dur, f0, f1, q, vol, type) {
      const s = ac.createBufferSource(); s.buffer = noise;
      const f = ac.createBiquadFilter(); f.type = type || 'bandpass'; f.Q.value = q;
      f.frequency.setValueAtTime(f0, t); f.frequency.exponentialRampToValueAtTime(Math.max(20, f1), t + dur);
      const g = ac.createGain(); g.gain.setValueAtTime(0.0001, t); g.gain.exponentialRampToValueAtTime(vol, t + Math.min(0.02, dur / 3)); g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
      s.connect(f); f.connect(g); g.connect(out); s.start(t, Math.random() * 0.4); s.stop(t + dur + 0.05);
    }
    function tone(t, f0, f1, dur, vol, type) {
      const o = ac.createOscillator(); o.type = type || 'sine';
      o.frequency.setValueAtTime(f0, t); o.frequency.exponentialRampToValueAtTime(Math.max(20, f1), t + dur);
      const g = ac.createGain(); g.gain.setValueAtTime(0.0001, t); g.gain.exponentialRampToValueAtTime(vol, t + 0.008); g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
      o.connect(g); g.connect(out); o.start(t); o.stop(t + dur + 0.05);
    }
    const play = (fn) => { const a = A(); if (a) try { fn(a.currentTime); } catch (e) { /* звук — не головне */ } };
    return {
      on: () => on,
      set(v) { on = !!v; store.set('snd', on); if (on) play((t) => tone(t, 660, 990, 0.12, 0.2, 'triangle')); },
      swoosh(speed) { play((t) => hiss(t, 0.16, 500 + speed * 0.4, 2600 + speed, 1.2, clamp(0.08 + speed / 9000, 0.08, 0.3))); },
      squish(k) {
        const now = performance.now(); if (now - lastSquish < 45) return; lastSquish = now;
        const big = k === 'k' || k === 'd' || k === 'p' || k === 'B';
        play((t) => {
          hiss(t, big ? 0.22 : 0.15, big ? 1400 : 2200, 260, 0.9, big ? 0.55 : 0.42, 'lowpass');
          tone(t, big ? 170 : rnd(240, 320), 70, big ? 0.14 : 0.09, 0.32, 'triangle');
          hiss(t + 0.03, 0.08, 5000, 2500, 3, 0.08);
        });
      },
      combo(n) { play((t) => { const f = [523, 659, 784, 1047, 1319, 1568, 2093]; for (let i = 0; i < Math.min(n, 7); i++) tone(t + i * 0.06, f[i], f[i] * 1.01, 0.18, 0.18, 'triangle'); }); },
      smash() { play((t) => {
        hiss(t, 0.6, 3200, 160, 0.7, 0.9, 'lowpass'); tone(t, 110, 38, 0.45, 0.8, 'sine');
        for (let i = 0; i < 7; i++) tone(t + 0.04 + Math.random() * 0.25, rnd(1600, 3400), rnd(1200, 2600), 0.06, 0.07, 'square');
      }); },
      frost() { play((t) => { for (let i = 0; i < 6; i++) tone(t + i * 0.07, 1760 + i * 220, 2400 + i * 200, 0.5, 0.06, 'sine'); hiss(t, 0.7, 6000, 9000, 4, 0.07, 'highpass'); }); },
      gold() { play((t) => { [880, 1320, 1760, 2640].forEach((f, i) => tone(t + i * 0.05, f, f, 0.35, 0.12, 'triangle')); }); },
      shower() { play((t) => { for (let i = 0; i < 8; i++) hiss(t + i * 0.05, 0.08, 900, 400, 1, 0.18, 'bandpass'); tone(t, 220, 440, 0.4, 0.12, 'sawtooth'); }); },
      thump() { play((t) => { tone(t, 120, 60, 0.12, 0.45, 'sine'); hiss(t, 0.1, 1800, 300, 1, 0.3, 'lowpass'); }); },
      burst() { play((t) => { hiss(t, 0.5, 2400, 200, 0.8, 0.8, 'lowpass'); tone(t, 90, 40, 0.4, 0.6); }); },
      miss() { play((t) => tone(t, 330, 140, 0.28, 0.14, 'sawtooth')); },
      tick() { play((t) => tone(t, 1200, 1200, 0.03, 0.08, 'square')); },
      over() { play((t) => { [660, 520, 400].forEach((f, i) => tone(t + i * 0.12, f, f * 0.98, 0.22, 0.14, 'triangle')); }); },
      rec() { play((t) => { [523, 659, 784, 1047, 784, 1047, 1319].forEach((f, i) => tone(t + i * 0.08, f, f, 0.2, 0.15, 'triangle')); }); },
    };
  })();

  // ---------------------------------------------------------------------------------------------
  // Малювання плодів (центр 0,0; радіус r). Звичайні — як у «Кавунах на ярмарку».
  // ---------------------------------------------------------------------------------------------
  function radial(g, r, stops, fx, fy) {
    const gr = g.createRadialGradient(r * (fx == null ? -0.35 : fx), r * (fy == null ? -0.4 : fy), r * 0.1, 0, 0, r * 1.1);
    stops.forEach((s, i) => gr.addColorStop(i / (stops.length - 1), s));
    return gr;
  }
  function stem(g, r, color, len, bend) {
    g.strokeStyle = color; g.lineWidth = Math.max(2, r * 0.12); g.lineCap = 'round';
    g.beginPath(); g.moveTo(0, -r * 0.82); g.quadraticCurveTo(r * (bend || 0.15), -r * (1.0 + len * 0.5), r * (bend || 0.15) * 1.6, -r * (1 + len)); g.stroke();
  }
  function leaf(g, x, y, w, h, rot, color) {
    g.save(); g.translate(x, y); g.rotate(rot);
    g.fillStyle = color; g.beginPath(); g.ellipse(0, 0, w, h, 0, 0, Math.PI * 2); g.fill();
    g.strokeStyle = 'rgba(0,0,0,.25)'; g.lineWidth = 1; g.beginPath(); g.moveTo(-w * 0.9, 0); g.lineTo(w * 0.9, 0); g.stroke();
    g.restore();
  }
  function shine(g, r, a) {
    g.fillStyle = 'rgba(255,255,255,' + (a || 0.35) + ')';
    g.beginPath(); g.ellipse(-r * 0.38, -r * 0.42, r * 0.22, r * 0.13, -0.7, 0, Math.PI * 2); g.fill();
  }
  function outline(g, r) { g.lineWidth = Math.max(1.5, r * 0.06); g.strokeStyle = 'rgba(25,12,6,.55)'; }
  function halo(g, r, rgb, a) {
    const gl = g.createRadialGradient(0, 0, r * 0.7, 0, 0, r * 1.6);
    gl.addColorStop(0, 'rgba(' + rgb + ',' + a + ')'); gl.addColorStop(1, 'rgba(' + rgb + ',0)');
    g.fillStyle = gl; g.beginPath(); g.arc(0, 0, r * 1.6, 0, Math.PI * 2); g.fill();
  }
  function plumShape(g, r) { g.beginPath(); g.ellipse(0, 0, r * 0.88, r, 0, 0, Math.PI * 2); }
  function appleShape(g, r) {
    g.beginPath(); g.moveTo(0, -r * 0.7);
    g.bezierCurveTo(r * 0.6, -r * 1.15, r * 1.25, -r * 0.4, r * 0.9, r * 0.45);
    g.bezierCurveTo(r * 0.7, r * 1.0, r * 0.2, r * 1.05, 0, r * 0.9);
    g.bezierCurveTo(-r * 0.2, r * 1.05, -r * 0.7, r * 1.0, -r * 0.9, r * 0.45);
    g.bezierCurveTo(-r * 1.25, -r * 0.4, -r * 0.6, -r * 1.15, 0, -r * 0.7);
  }
  function pumpkin(g, r, cols, stemCol) {
    outline(g, r);
    [[-0.62, 0.5], [0.62, 0.5], [-0.32, 0.62], [0.32, 0.62], [0, 0.66]].forEach(([x, w]) => {
      g.fillStyle = radial(g, r, cols, x * 0.5 - 0.2, -0.3);
      g.beginPath(); g.ellipse(x * r, 0, w * r, r * 0.86, 0, 0, Math.PI * 2); g.fill(); g.stroke();
    });
    g.fillStyle = stemCol; g.beginPath();
    g.moveTo(-r * 0.1, -r * 0.75); g.lineTo(-r * 0.06, -r * 1.12); g.lineTo(r * 0.16, -r * 1.16); g.lineTo(r * 0.12, -r * 0.75); g.fill();
    shine(g, r, 0.28);
  }
  function kavun(g, r, glow) {
    if (glow) halo(g, r, '255,215,90', 0.35);
    g.fillStyle = radial(g, r, ['#9ee07a', '#3a9a3a', '#14501c']); outline(g, r);
    g.beginPath(); g.ellipse(0, 0, r * 1.05, r * 0.95, 0, 0, Math.PI * 2); g.fill();
    g.save(); g.clip();
    g.strokeStyle = '#123f16'; g.lineWidth = r * 0.16; g.lineJoin = 'round';
    for (let i = -2; i <= 2; i++) {
      g.beginPath();
      for (let j = 0; j <= 8; j++) {
        const y = -r + j * r * 0.25, x = i * r * 0.42 + (j % 2 ? r * 0.08 : -r * 0.08) * (1 - Math.abs(i) * 0.2);
        if (j) g.lineTo(x, y); else g.moveTo(x, y);
      }
      g.stroke();
    }
    g.restore();
    g.beginPath(); g.ellipse(0, 0, r * 1.05, r * 0.95, 0, 0, Math.PI * 2); g.stroke();
    stem(g, r, '#5a3a1a', 0.18, 0.2); shine(g, r, 0.35);
  }
  function sparkle(g, x, y, s, color) {
    g.fillStyle = color; g.beginPath();
    g.moveTo(x, y - s); g.quadraticCurveTo(x, y, x + s, y); g.quadraticCurveTo(x, y, x, y + s); g.quadraticCurveTo(x, y, x - s, y); g.quadraticCurveTo(x, y, x, y - s);
    g.fill();
  }

  const DRAW = {
    s(g, r) {
      g.fillStyle = radial(g, r, ['#c79ae6', '#7a3aa8', '#3d1458']); outline(g, r);
      plumShape(g, r); g.fill(); g.stroke();
      g.strokeStyle = 'rgba(40,10,60,.45)'; g.lineWidth = r * 0.07; g.beginPath(); g.moveTo(r * 0.1, -r * 0.9); g.quadraticCurveTo(r * 0.45, 0, r * 0.1, r * 0.92); g.stroke();
      g.fillStyle = 'rgba(220,210,255,.18)'; g.beginPath(); g.ellipse(-r * 0.2, -r * 0.1, r * 0.55, r * 0.75, 0, 0, Math.PI * 2); g.fill();
      stem(g, r, '#5a3a1a', 0.3, 0.1); shine(g, r, 0.3);
    },
    b(g, r) {
      leaf(g, -r * 0.25, -r * 1.15, r * 0.22, r * 0.5, -0.35, '#3f8a3a');
      leaf(g, r * 0.25, -r * 1.2, r * 0.22, r * 0.55, 0.3, '#4f9a44');
      g.strokeStyle = '#b0204a'; g.lineWidth = r * 0.1; g.beginPath(); g.moveTo(0, -r * 0.8); g.lineTo(-r * 0.2, -r * 1.1); g.moveTo(0, -r * 0.8); g.lineTo(r * 0.2, -r * 1.12); g.stroke();
      g.fillStyle = radial(g, r, ['#e0507a', '#9a1238', '#4d0518']); outline(g, r);
      g.beginPath(); g.moveTo(0, -r * 0.85);
      g.bezierCurveTo(r * 1.05, -r * 0.85, r * 1.0, r * 0.55, r * 0.1, r * 0.95);
      g.lineTo(0, r * 1.35); g.lineTo(-r * 0.1, r * 0.95);
      g.bezierCurveTo(-r * 1.0, r * 0.55, -r * 1.05, -r * 0.85, 0, -r * 0.85); g.fill(); g.stroke();
      shine(g, r, 0.25);
    },
    a(g, r) {
      g.fillStyle = radial(g, r, ['#ff9a7a', '#e2261e', '#8a0e10']); outline(g, r);
      appleShape(g, r); g.fill(); g.stroke();
      stem(g, r, '#5a3a1a', 0.35, 0.12); leaf(g, r * 0.32, -r * 1.05, r * 0.3, r * 0.14, -0.5, '#4caf50'); shine(g, r, 0.4);
    },
    g(g, r) {
      g.fillStyle = radial(g, r, ['#f6f7b0', '#c2d24a', '#7a8a1a'], -0.3, 0); outline(g, r);
      g.beginPath(); g.moveTo(0, -r * 1.0);
      g.bezierCurveTo(r * 0.45, -r * 1.0, r * 0.45, -r * 0.3, r * 0.8, r * 0.2);
      g.bezierCurveTo(r * 1.1, r * 0.7, r * 0.6, r * 1.05, 0, r * 1.02);
      g.bezierCurveTo(-r * 0.6, r * 1.05, -r * 1.1, r * 0.7, -r * 0.8, r * 0.2);
      g.bezierCurveTo(-r * 0.45, -r * 0.3, -r * 0.45, -r * 1.0, 0, -r * 1.0); g.fill(); g.stroke();
      g.fillStyle = 'rgba(200,120,40,.22)'; g.beginPath(); g.ellipse(r * 0.35, r * 0.4, r * 0.35, r * 0.3, 0, 0, Math.PI * 2); g.fill();
      g.save(); g.translate(0, -r * 0.2); stem(g, r, '#5a3a1a', 0.3, 0.2); g.restore(); shine(g, r, 0.3);
    },
    d(g, r) {
      g.fillStyle = radial(g, r, ['#fff3a8', '#f0c23a', '#b07a10']); outline(g, r);
      g.beginPath(); g.ellipse(0, 0, r * 1.18, r * 0.8, 0, 0, Math.PI * 2); g.fill(); g.stroke();
      g.save(); g.beginPath(); g.ellipse(0, 0, r * 1.15, r * 0.77, 0, 0, Math.PI * 2); g.clip();
      g.strokeStyle = 'rgba(255,250,220,.55)'; g.lineWidth = Math.max(1, r * 0.04);
      for (let i = -4; i <= 4; i++) {
        g.beginPath(); g.moveTo(i * r * 0.32 - r * 0.6, -r); g.lineTo(i * r * 0.32 + r * 0.6, r); g.stroke();
        g.beginPath(); g.moveTo(i * r * 0.32 + r * 0.6, -r); g.lineTo(i * r * 0.32 - r * 0.6, r); g.stroke();
      }
      g.restore();
      g.fillStyle = '#6a4a1a'; g.beginPath(); g.arc(-r * 1.15, 0, r * 0.08, 0, Math.PI * 2); g.fill();
      shine(g, r, 0.3);
    },
    p(g, r) { pumpkin(g, r, ['#ffd08a', '#f58a1e', '#a8460a'], '#3f6a1a'); },
    k(g, r) { kavun(g, r, false); },
    B(g, r) { kavun(g, r, true); },
    r(g, r) {   // помідор: приплюснутий, з зеленою зірочкою
      g.fillStyle = radial(g, r, ['#ff9a80', '#e8261a', '#7a0a06']); outline(g, r);
      g.beginPath(); g.ellipse(0, r * 0.05, r * 1.02, r * 0.9, 0, 0, Math.PI * 2); g.fill(); g.stroke();
      g.strokeStyle = 'rgba(120,10,6,.35)'; g.lineWidth = r * 0.06;
      [-0.45, 0, 0.45].forEach((x) => { g.beginPath(); g.moveTo(x * r, -r * 0.75); g.quadraticCurveTo(x * r * 1.4, 0, x * r, r * 0.85); g.stroke(); });
      g.fillStyle = '#3f8a3a';
      for (let i = 0; i < 5; i++) { const a = -Math.PI / 2 + i * Math.PI * 2 / 5; leaf(g, Math.cos(a) * r * 0.22, -r * 0.78 + Math.sin(a) * r * 0.12, r * 0.24, r * 0.08, a, '#3f8a3a'); }
      g.fillStyle = '#2f6a2a'; g.beginPath(); g.arc(0, -r * 0.8, r * 0.09, 0, Math.PI * 2); g.fill();
      shine(g, r, 0.45);
    },
    m(g, r) {   // морква: конус із бадиллям
      leaf(g, -r * 0.18, -r * 1.12, r * 0.12, r * 0.42, -0.4, '#4f9a44');
      leaf(g, r * 0.05, -r * 1.22, r * 0.12, r * 0.48, 0.05, '#3f8a3a');
      leaf(g, r * 0.26, -r * 1.1, r * 0.12, r * 0.4, 0.45, '#5aa84e');
      const gr = g.createLinearGradient(-r * 0.5, 0, r * 0.5, 0);
      gr.addColorStop(0, '#c4560a'); gr.addColorStop(0.4, '#ff9a2a'); gr.addColorStop(1, '#b8480a');
      g.fillStyle = gr; outline(g, r);
      g.beginPath(); g.moveTo(-r * 0.48, -r * 0.78); g.quadraticCurveTo(0, -r * 0.95, r * 0.48, -r * 0.78);
      g.quadraticCurveTo(r * 0.3, r * 0.4, r * 0.04, r * 1.3); g.quadraticCurveTo(-r * 0.02, r * 1.36, -r * 0.06, r * 1.3);
      g.quadraticCurveTo(-r * 0.3, r * 0.4, -r * 0.48, -r * 0.78); g.fill(); g.stroke();
      g.strokeStyle = 'rgba(110,40,0,.4)'; g.lineWidth = Math.max(1, r * 0.05);
      [[-0.3, -0.35, 0.05], [0.25, -0.05, 0.05], [-0.18, 0.35, 0.05], [0.14, 0.7, 0.04]].forEach(([x, y]) => { g.beginPath(); g.moveTo(x * r, y * r); g.lineTo(x * r + (x < 0 ? 0.22 : -0.22) * r, y * r + 0.04 * r); g.stroke(); });
      g.fillStyle = 'rgba(255,255,255,.3)'; g.beginPath(); g.ellipse(-r * 0.2, -r * 0.4, r * 0.08, r * 0.35, 0.15, 0, Math.PI * 2); g.fill();
    },
    G(g, r) {   // Дядьків глек: теракота, біла хвиля, ручка — не різати!
      halo(g, r, '255,60,40', 0.32);
      g.strokeStyle = '#5a2f14'; g.lineWidth = r * 0.26; g.lineCap = 'round';
      g.beginPath(); g.moveTo(r * 0.38, -r * 0.62); g.bezierCurveTo(r * 1.2, -r * 0.7, r * 1.15, r * 0.25, r * 0.62, r * 0.32); g.stroke();
      g.strokeStyle = '#c5763a'; g.lineWidth = r * 0.13; g.stroke();
      g.fillStyle = radial(g, r, ['#f0b080', '#c26a32', '#6a3010']); outline(g, r);
      g.beginPath(); g.moveTo(-r * 0.32, -r * 0.95); g.lineTo(r * 0.32, -r * 0.95); g.lineTo(r * 0.26, -r * 0.6);
      g.bezierCurveTo(r * 1.05, -r * 0.45, r * 1.05, r * 0.85, r * 0.45, r * 0.98); g.lineTo(-r * 0.45, r * 0.98);
      g.bezierCurveTo(-r * 1.05, r * 0.85, -r * 1.05, -r * 0.45, -r * 0.26, -r * 0.6); g.closePath(); g.fill(); g.stroke();
      g.fillStyle = '#7a3a14'; g.beginPath(); g.ellipse(0, -r * 0.95, r * 0.36, r * 0.1, 0, 0, Math.PI * 2); g.fill();
      g.strokeStyle = '#fff6e0'; g.lineWidth = Math.max(1.4, r * 0.08);
      g.beginPath(); for (let i = 0; i <= 12; i++) { const x = -r * 0.82 + i * r * 0.137, y = r * 0.12 + (i % 2 ? -r * 0.12 : r * 0.08); if (i) g.lineTo(x, y); else g.moveTo(x, y); } g.stroke();
      g.fillStyle = '#d4122a'; [-0.45, 0, 0.45].forEach((x) => { g.beginPath(); g.arc(x * r, r * 0.5, r * 0.08, 0, Math.PI * 2); g.fill(); });
      shine(g, r, 0.3);
    },
    F(g, r) {   // крижана слива
      halo(g, r, '150,220,255', 0.5);
      g.fillStyle = radial(g, r, ['#ffffff', '#8ed4f5', '#2a6a9a']); outline(g, r);
      plumShape(g, r); g.fill(); g.stroke();
      g.strokeStyle = 'rgba(255,255,255,.85)'; g.lineWidth = Math.max(1.2, r * 0.07); g.lineCap = 'round';
      for (let i = 0; i < 3; i++) {
        const a = i * Math.PI / 3; g.beginPath(); g.moveTo(Math.cos(a) * r * 0.55, Math.sin(a) * r * 0.55); g.lineTo(-Math.cos(a) * r * 0.55, -Math.sin(a) * r * 0.55); g.stroke();
      }
      sparkle(g, r * 0.55, -r * 0.6, r * 0.22, '#fff'); sparkle(g, -r * 0.6, r * 0.45, r * 0.15, '#fff');
      stem(g, r, '#6a8aa0', 0.3, 0.1); shine(g, r, 0.5);
    },
    X(g, r) {   // золоте яблуко
      halo(g, r, '255,210,70', 0.55);
      g.fillStyle = radial(g, r, ['#fffbe0', '#f5c33b', '#9a6a08']); outline(g, r);
      appleShape(g, r); g.fill(); g.stroke();
      stem(g, r, '#6a4a1a', 0.35, 0.12); leaf(g, r * 0.32, -r * 1.05, r * 0.3, r * 0.14, -0.5, '#7ac04a');
      sparkle(g, r * 0.6, -r * 0.5, r * 0.22, '#fff'); sparkle(g, -r * 0.55, r * 0.5, r * 0.14, '#fff8c0');
      g.font = '900 ' + Math.round(r * 0.7) + 'px Onest, system-ui, sans-serif'; g.textAlign = 'center'; g.textBaseline = 'middle';
      g.lineWidth = r * 0.12; g.strokeStyle = '#5a3a04'; g.fillStyle = '#fff6d0'; g.strokeText('×2', 0, r * 0.2); g.fillText('×2', 0, r * 0.2);
      shine(g, r, 0.55);
    },
    Z(g, r) {   // кукурудза в листі
      halo(g, r, '255,230,90', 0.4);
      leaf(g, -r * 0.45, r * 0.45, r * 0.3, r * 0.85, 0.4, '#5aa84e');
      leaf(g, r * 0.45, r * 0.45, r * 0.3, r * 0.85, -0.4, '#4f9a44');
      g.fillStyle = radial(g, r, ['#fff6b0', '#f5c824', '#b88a04']); outline(g, r);
      g.beginPath(); g.ellipse(0, -r * 0.1, r * 0.5, r * 1.05, 0, 0, Math.PI * 2); g.fill(); g.stroke();
      g.save(); g.beginPath(); g.ellipse(0, -r * 0.1, r * 0.48, r * 1.02, 0, 0, Math.PI * 2); g.clip();
      g.fillStyle = 'rgba(160,110,0,.35)';
      for (let y = -r * 1.05; y < r; y += r * 0.2) for (let x = -r * 0.5; x < r * 0.5; x += r * 0.2) {
        g.beginPath(); g.ellipse(x + ((y / (r * 0.2)) & 1 ? r * 0.1 : 0), y, r * 0.07, r * 0.08, 0, 0, Math.PI * 2); g.fill();
      }
      g.restore();
      leaf(g, 0, r * 0.75, r * 0.22, r * 0.5, 0, '#3f8a3a');
      shine(g, r, 0.4);
    },
  };

  /// Зріз половинки: м'якуш еліпсом уздовж лінії розрізу.
  function face(g, k, r) {
    const K = KINDS[k] || KINDS.a;
    g.fillStyle = K.flesh;
    g.beginPath(); g.ellipse(0, 0, r * (k === 'd' ? 1.1 : 0.92), r * 0.34, 0, 0, Math.PI * 2); g.fill();
    g.strokeStyle = 'rgba(255,255,255,.35)'; g.lineWidth = Math.max(1, r * 0.05);
    g.beginPath(); g.ellipse(0, 0, r * 0.78, r * 0.24, 0, 0, Math.PI * 2); g.stroke();
    if (k === 'k' || k === 'B') {
      g.fillStyle = '#1a1010';
      for (let i = -3; i <= 3; i++) { g.beginPath(); g.ellipse(i * r * 0.22, (i % 2 ? 1 : -1) * r * 0.08, r * 0.04, r * 0.07, 0.4, 0, Math.PI * 2); g.fill(); }
    } else if (k === 'p' || k === 'd' || k === 'r') {
      g.fillStyle = k === 'r' ? 'rgba(255,230,140,.9)' : 'rgba(255,250,220,.85)';
      for (let i = -2; i <= 2; i++) { g.beginPath(); g.ellipse(i * r * 0.25, 0, r * 0.06, r * 0.03, 0, 0, Math.PI * 2); g.fill(); }
    } else if (k === 'm') {
      g.fillStyle = '#ffcf7a'; g.beginPath(); g.ellipse(0, 0, r * 0.35, r * 0.14, 0, 0, Math.PI * 2); g.fill();
    } else if (k === 'Z') {
      g.fillStyle = '#ffe680'; for (let i = -3; i <= 3; i++) { g.beginPath(); g.arc(i * r * 0.22, 0, r * 0.07, 0, Math.PI * 2); g.fill(); }
    } else if (k === 'F') {
      sparkle(g, 0, 0, r * 0.2, '#fff');
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Спрайти: кожен вид малюється раз на розмір поля — далі лише drawImage
  // ---------------------------------------------------------------------------------------------
  function sprite(st, k) {
    const c0 = st.sprites[k];
    if (c0) return c0;
    const r = (KINDS[k] || KINDS.a).r * st.g.u, dpr = st.g.dpr, half = Math.ceil(r * 1.75);
    const c = document.createElement('canvas');
    c.width = c.height = Math.ceil(half * 2 * dpr);
    const g = c.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, half * dpr, half * dpr);
    (DRAW[k] || DRAW.a)(g, r);
    return (st.sprites[k] = { c, half, r });
  }
  function blit(g, sp, scale) {
    const h = sp.half * (scale || 1);
    g.drawImage(sp.c, -h, -h, h * 2, h * 2);
  }

  // ---------------------------------------------------------------------------------------------
  // Тло: дошка для шаткування, рушник угорі, тінь по краях
  // ---------------------------------------------------------------------------------------------
  function paintBack(st) {
    const { W, H, dpr } = st.g;
    const c = st.back || (st.back = document.createElement('canvas'));
    c.width = Math.round(W * dpr); c.height = Math.round(H * dpr);
    const g = c.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    const bg = g.createLinearGradient(0, 0, W, H);
    bg.addColorStop(0, '#8a5a2c'); bg.addColorStop(0.5, '#7a4a20'); bg.addColorStop(1, '#5a3414');
    g.fillStyle = bg; g.fillRect(0, 0, W, H);
    // дошки поперек, з волокнами й сучками (сід — сталий, щоб тло не стрибало на кожен ресайз)
    let seed = 7;
    const r1 = () => ((seed = (seed * 16807) % 2147483647) / 2147483647);
    const n = Math.max(4, Math.round(H / 90));
    for (let i = 0; i < n; i++) {
      const y = (i / n) * H, h = H / n;
      g.fillStyle = i % 2 ? 'rgba(255,220,170,.05)' : 'rgba(0,0,0,.07)'; g.fillRect(0, y, W, h);
      g.strokeStyle = 'rgba(255,225,180,.07)'; g.lineWidth = 1;
      for (let k = 0; k < 7; k++) {
        const y0 = y + h * (0.1 + 0.8 * r1());
        g.beginPath(); g.moveTo(0, y0);
        for (let x = 0; x <= W; x += 60) g.lineTo(x, y0 + Math.sin(x / 90 + k) * 3 + (r1() - 0.5) * 2);
        g.stroke();
      }
      if (r1() < 0.7) {
        const kx = r1() * W, ky = y + h * (0.3 + 0.4 * r1());
        g.fillStyle = 'rgba(60,30,8,.35)'; g.beginPath(); g.ellipse(kx, ky, 9, 5, 0, 0, Math.PI * 2); g.fill();
        g.strokeStyle = 'rgba(60,30,8,.2)'; g.beginPath(); g.ellipse(kx, ky, 16, 9, 0, 0, Math.PI * 2); g.stroke();
      }
      g.fillStyle = 'rgba(25,10,2,.55)'; g.fillRect(0, y + h - 2, W, 2);
      g.fillStyle = 'rgba(255,225,180,.1)'; g.fillRect(0, y + h, W, 1);
    }
    // зарубки від ножа
    g.strokeStyle = 'rgba(40,18,4,.25)'; g.lineWidth = 1.2;
    for (let i = 0; i < 26; i++) {
      const x = r1() * W, y = r1() * H, a = r1() * Math.PI, l = 10 + r1() * 30;
      g.beginPath(); g.moveTo(x, y); g.lineTo(x + Math.cos(a) * l, y + Math.sin(a) * l); g.stroke();
    }
    // рушник угорі
    const band = Math.round(clamp(H * 0.05, 12, 22));
    g.fillStyle = '#f4ead2'; g.fillRect(0, 0, W, band);
    g.fillStyle = 'rgba(0,0,0,.25)'; g.fillRect(0, band, W, 3);
    const s = band / 5;
    for (let x = 0, i = 0; x < W + s * 6; x += s * 6, i++) {
      g.fillStyle = i % 2 ? '#1a0e0a' : '#c4122a';
      g.beginPath(); g.moveTo(x + s * 3, s * 0.6); g.lineTo(x + s * 5, band / 2); g.lineTo(x + s * 3, band - s * 0.6); g.lineTo(x + s, band / 2); g.closePath(); g.fill();
      g.fillStyle = '#f4ead2'; g.beginPath(); g.arc(x + s * 3, band / 2, s * 0.45, 0, Math.PI * 2); g.fill();
    }
    // тінь по краях
    const v = g.createRadialGradient(W / 2, H * 0.55, Math.min(W, H) * 0.35, W / 2, H * 0.55, Math.max(W, H) * 0.75);
    v.addColorStop(0, 'rgba(0,0,0,0)'); v.addColorStop(1, 'rgba(0,0,0,.5)');
    g.fillStyle = v; g.fillRect(0, 0, W, H);
  }

  // ---------------------------------------------------------------------------------------------
  // Каркас сторінки
  // ---------------------------------------------------------------------------------------------
  function skeleton(root, st) {
    root.innerHTML = '<div class="sk">'
      + '<div class="sk-stage"><canvas class="sk-cv"></canvas>'
      + '<div class="sk-tint"></div>'
      + '<img class="sk-glek" src="/static/glek.svg" alt="" draggable="false">'
      + '<div class="sk-hud"><div class="sk-score"><b>0</b><small></small></div>'
      + '<div class="sk-right"><div class="sk-lives"></div><div class="sk-time"><b></b><i><i></i></i></div><div class="sk-buffs"></div></div></div>'
      + '<button type="button" class="sk-snd" data-pad-skip title="Звук"></button>'
      + '<div class="sk-say"></div><div class="sk-fx"></div><div class="sk-big"></div>'
      + '<div class="sk-card" hidden></div></div></div>';
    const q = (s) => root.querySelector(s);
    st.el = {
      box: q('.sk'), stage: q('.sk-stage'), cv: q('.sk-cv'), tint: q('.sk-tint'), glek: q('.sk-glek'), score: q('.sk-score b'), best: q('.sk-score small'),
      lives: q('.sk-lives'), time: q('.sk-time'), timeB: q('.sk-time b'), timeBar: q('.sk-time i i'), buffs: q('.sk-buffs'), snd: q('.sk-snd'),
      say: q('.sk-say'), fx: q('.sk-fx'), big: q('.sk-big'), card: q('.sk-card'),
    };
    wire(root, st);
  }

  function wire(root, st) {
    const el = st.el;
    const on = (n, ev, fn, o) => { n.addEventListener(ev, fn, o); st.offs.push(() => n.removeEventListener(ev, fn, o)); };
    on(el.snd, 'click', () => { Snd.set(!Snd.on()); el.snd.blur(); paintSnd(st); });
    on(el.card, 'click', (e) => {
      const b = e.target.closest('button'); if (!b) return;
      if (b.dataset.a === 'go') go(st);
      else if (b.dataset.a === 'again') again(st);
      else if (b.dataset.a === 'share') share(st);
    });
    const cv = el.cv;
    on(cv, 'pointerdown', (e) => {
      if (e.button != null && e.button > 0) return;
      try { cv.setPointerCapture(e.pointerId); } catch (x) { /* не всі вміють */ }
      const p = pt(st, e);
      st.ptr = { id: e.pointerId, x: p.x, y: p.y, touch: e.pointerType !== 'mouse', at: performance.now() };
      newSwipe(st);
      st.trail.push({ x: p.x, y: p.y, at: performance.now() });
      loop(st);
      e.preventDefault();
    });
    on(cv, 'pointermove', (e) => {
      if (!st.ptr || st.ptr.id !== e.pointerId) return;
      const list = e.getCoalescedEvents ? e.getCoalescedEvents() : [e];
      for (const ev of list.length ? list : [e]) {
        const p = pt(st, ev), now = performance.now();
        if (now - st.ptr.at > 260) newSwipe(st);   // спинився — новий змах (комбо — лише одним рухом)
        moveBlade(st, st.ptr.x, st.ptr.y, p.x, p.y, st.ptr.touch ? 10 : 4, now - st.ptr.at);
        st.ptr.x = p.x; st.ptr.y = p.y; st.ptr.at = now;
      }
      loop(st);
    });
    const up = (e) => { if (st.ptr && st.ptr.id === e.pointerId) { st.ptr = null; endSwipe(st); } };
    on(cv, 'pointerup', up); on(cv, 'pointercancel', up); on(cv, 'lostpointercapture', up);
    on(cv, 'contextmenu', (e) => e.preventDefault());
  }
  function pt(st, e) { const b = st.el.cv.getBoundingClientRect(); return { x: e.clientX - b.left, y: e.clientY - b.top }; }

  // ---------------------------------------------------------------------------------------------
  // Стан і годинник
  // ---------------------------------------------------------------------------------------------
  const cur = (st) => st.ctx && st.ctx.view;
  const K = (st) => { const v = cur(st); return (v && v.k) || DEF_K; };
  const modeOf = (st) => { const v = cur(st); return (v && v.mode) || 'classic'; };
  function syncClock(st, v) {
    const s = tms(v.now);
    if (!isFinite(s)) return;
    st.offs2.push({ d: s - Date.now(), at: Date.now() });
    while (st.offs2.length > 16 || (st.offs2.length && Date.now() - st.offs2[0].at > 60000)) st.offs2.shift();
    st.off = Math.max(...st.offs2.map((o) => o.d));   // найменша затримка — найближче до правди
  }
  const snow = (st) => Date.now() + (st.off || 0);
  /// Справжні мс від t0 партії.
  const tNow = (st) => (st.t0 ? snow(st) - st.t0 : -1e9);
  /// Мороз: сервер плюс свій, ще не підтверджений.
  function frost(st) {
    const v = cur(st), srv = (v && v.frz) || [];
    const mine = st.frzLocal.filter(([s]) => !srv.some(([a]) => Math.abs(a - s) < 1200));
    return mine.length ? [...srv, ...mine].sort((a, b) => a[0] - b[0]) : srv;
  }
  function doubles(st) {
    const v = cur(st), srv = (v && v.dbl) || [];
    const mine = st.dblLocal.filter(([s]) => !srv.some(([a]) => Math.abs(a - s) < 1200));
    return mine.length ? [...srv, ...mine] : srv;
  }
  const gNow = (st) => gameMs(Math.max(0, tNow(st)), frost(st), K(st).frost);
  const playing = (st) => { const v = cur(st); return !!(v && v.phase === 'go' && st.t0 && st.ctx.mine); };

  /// Позиція плоду в ігрову мить g (CSS-пікселі поля); null — ще не вилетів чи давно впав.
  function where(st, f, g) {
    const s = (g - f.at) / f.d;
    if (s < 0 || s > 1.15) return null;
    const { W, H } = st.g, r = (KINDS[f.k] || KINDS.a).r * st.g.u;
    const y = H + r - (f.h * H + r) * 4 * s * (1 - s);
    return { x: (f.x0 + (f.x1 - f.x0) * s) * W, y, s, rot: f.sp * Math.PI * 2 * s, r };
  }

  /// Злити вид сервера з локальним: нова партія, нові плоди, підтверджене сервером, кінець.
  function merge(st, v) {
    const t0 = tms(v.t0);
    if (v.phase === 'go' && t0 && t0 !== st.t0) {
      // нова партія
      st.t0 = t0; st.fruits = new Map(); st.gains = new Map(); st.frzLocal = []; st.dblLocal = []; st.sent = [];
      st.wSeq = 0; st.w = 0; st.wCount = 0; st.bigHits = 0; st.bigW = -1; st.overShown = false; st.timeUp = false; st.lastTick = 0;
      st.startCut = false;
      card(st, null);
      big(st, '<b>Ану!</b>', 'go', 900);
      say(st, pick(SAY.go));
    }
    if (v.phase === 'ready') {
      if (st.phase !== 'ready') { st.t0 = 0; st.fruits = new Map(); st.startCut = false; st.startAt = performance.now(); say(st, pick(SAY.ready)); }
      if (!st.startCut) card(st, 'ready');
    }
    for (const f of v.fruits || []) {
      let o = st.fruits.get(f.id);
      if (!o) { o = { id: f.id, k: f.k, at: f.at, d: f.d, x0: f.x0, x1: f.x1, h: f.h, sp: f.sp, cut: false }; st.fruits.set(f.id, o); }
      if (f.c) { if (!o.cut && o.k !== 'B') o.cut = 'srv'; st.gains.delete(f.id); }
    }
    if (v.phase === 'over' && st.phase !== 'over') ended(st, v);
    if (v.phase === 'over' && !st.overShown && !st.overT) st.overT = setTimeout(() => { st.overT = 0; st.overShown = true; card(st, 'over'); }, st.phase === 'go' ? 1100 : 0);
    st.phase = v.phase;
  }

  function ended(st, v) {
    const e = v.end || {};
    endSwipe(st);
    if (st.phase !== 'go') return;
    if (e.why === 'glek') {
      if (!st.glekBoom) boomAt(st, st.g.W / 2, st.g.H / 2);
      say(st, pick(SAY.glek));
    } else if (e.why === 'miss') {
      big(st, '<b>Три промахи</b>', 'lost', 1400); say(st, pick(SAY.miss));
    } else if (e.why === 'time') {
      if (!st.timeUp) big(st, '<b>Час!</b>', 'time', 1400);
      say(st, pick(SAY.time));
    }
    if (e.rec && e.score > 0) { setTimeout(() => { Snd.rec(); confetti(st); say(st, pick(SAY.rec)); }, 900); } else setTimeout(() => Snd.over(), 700);
    st.glekBoom = false;
  }

  // ---------------------------------------------------------------------------------------------
  // Лезо й розрізи
  // ---------------------------------------------------------------------------------------------
  function newSwipe(st) { endSwipe(st); st.w = ++st.wSeq; st.wCount = 0; st.swooshAt = 0; }
  function endSwipe(st) {
    if (st.wCount >= 3) comboFx(st, st.wCount, st.wPos);
    st.wCount = 0;
  }

  /// Відрізок леза (ax,ay)→(bx,by) у пікселях поля за dt мс.
  function moveBlade(st, ax, ay, bx, by, slack, dt) {
    const now = performance.now(), len = Math.hypot(bx - ax, by - ay);
    st.trail.push({ x: bx, y: by, at: now });
    if (len < 3) return;
    const speed = len / Math.max(8, dt) * 1000;   // пікс/с
    if (speed > 900 && now - (st.swooshAt || 0) > 220) { st.swooshAt = now; Snd.swoosh(speed); }
    if (st.phase === 'ready' && !st.startCut && st.ctx.mine) { startSlash(st, ax, ay, bx, by, slack); return; }
    if (!playing(st) || speed < 120) return;
    const g = gNow(st);
    if (tNow(st) < -150) return;
    const v = cur(st), len0 = v && v.len;
    if (len0 && g > len0 + K(st).lag) return;
    for (const f of st.fruits.values()) {
      if (f.cut && f.k !== 'B') continue;
      const p = where(st, f, g);
      if (!p || p.s > 1.05 || p.y > st.g.H + p.r * 0.6) continue;
      if (segDist(p.x, p.y, ax, ay, bx, by) <= p.r * (f.k === 'B' ? 0.95 : 0.85) + slack) cut(st, f, p, { dx: bx - ax, dy: by - ay });
      else if (f.k === 'B') f.inBlade = false;
    }
  }
  function segDist(px, py, ax, ay, bx, by) {
    const dx = bx - ax, dy = by - ay, L = dx * dx + dy * dy;
    let t = L ? ((px - ax) * dx + (py - ay) * dy) / L : 0;
    t = clamp(t, 0, 1);
    const x = ax + dx * t - px, y = ay + dy * t - py;
    return Math.sqrt(x * x + y * y);
  }

  function cut(st, f, p, dir) {
    const t = tNow(st), mult = (modeOf(st) === 'fair' || modeOf(st) === 'daily') && inside(doubles(st), t) ? 2 : 1;
    if (f.k === 'B') {
      // удар — кожен новий захід леза в кавун (сервер рахує раз на «змах», тож у кожного удару свій номер)
      const now = performance.now();
      if (f.inBlade || st.bigHits >= K(st).bigMax || now - (f.hitAt || 0) < 70) { f.inBlade = true; return; }
      f.inBlade = true; f.hitAt = now; st.bigHits++;
      juice(st, p.x, p.y, f.k, p.r * 0.5, dir, 16);
      floater(st, '+' + mult, p.x + rnd(-p.r, p.r) * 0.6, p.y - p.r * 0.9, '#fff', 1.3);
      Snd.thump();
      if (st.bigHits % 5 === 1) say(st, pick(SAY.big));
      send(st, f.id, 1000000 + st.bigHits);
      return;
    }
    f.cut = true;
    halves(st, f, p, dir);
    if (f.k === 'G') {
      if (modeOf(st) === 'classic') { boomAt(st, p.x, p.y); st.glekBoom = true; }
      else {
        shards(st, p.x, p.y, p.r, 26); Snd.smash(); shake(st, 2);
        floater(st, '−' + (K(st).glek || 10), p.x, p.y - p.r, '#ff4d4d', 1.8);
        st.gains.set(f.id, { gain: -(K(st).glek || 10), at: performance.now() });
        say(st, pick(SAY.glekT)); vibrate(60);
      }
      send(st, f.id);
      return;
    }
    juice(st, p.x, p.y, f.k, p.r, dir, f.k === 'k' || f.k === 'd' || f.k === 'p' ? 28 : 20);
    Snd.squish(f.k);
    st.wCount++; st.wPos = { x: p.x, y: p.y };
    let gain = 1;
    if (st.wCount === 3) gain += 3; else if (st.wCount > 3) gain += 1;
    gain *= mult;
    st.gains.set(f.id, { gain, at: performance.now() });
    floater(st, '+' + (mult > 1 ? mult : 1), p.x, p.y - p.r * 0.8, mult > 1 ? '#ffd54a' : '#fff8e0', 1);
    if (f.k === 'F') {
      const s = Math.max(0, t), last = st.frzLocal[st.frzLocal.length - 1];
      if (last && last[1] > s) last[1] = s + FROST_MS; else st.frzLocal.push([s, s + FROST_MS]);
      Snd.frost(); big(st, '<b>❄ Мороз!</b>', 'frost', 1100); say(st, pick(SAY.frost)); ring(st, p.x, p.y, '#bfefff', p.r * 6);
    } else if (f.k === 'X') {
      const s = Math.max(0, t), last = st.dblLocal[st.dblLocal.length - 1];
      if (last && last[1] > s) last[1] = s + DOUBLE_MS; else st.dblLocal.push([s, s + DOUBLE_MS]);
      Snd.gold(); big(st, '<b>✨ ×2!</b>', 'gold', 1100); say(st, pick(SAY.dbl)); ring(st, p.x, p.y, '#ffe08a', p.r * 6);
    } else if (f.k === 'Z') {
      Snd.shower(); big(st, '<b>🌽 Злива!</b>', 'shower', 1100); say(st, pick(SAY.shower)); throwGlek(st);
    }
    send(st, f.id);
  }

  /// Розріз — у пачку до сервера, не частіше раз на 40 мс.
  function send(st, id, w) {
    st.sent.push([id, w == null ? st.w : w]);
    flushSoon(st);
  }
  function flushSoon(st) {
    if (st.flushT) return;
    st.flushT = setTimeout(() => {
      st.flushT = 0;
      const c = st.sent.splice(0, 64);
      if (c.length) try { st.ctx.input('cut', { c }); } catch (e) { /* старий каркас */ }
      if (st.sent.length) flushSoon(st);
    }, 40);
  }

  /// Стартовий кавун на «ready»: розрізав — поїхали.
  function startSlash(st, ax, ay, bx, by, slack) {
    const m = startMelon(st);
    if (segDist(m.x, m.y, ax, ay, bx, by) > m.r + slack) return;
    st.startCut = true;
    halves(st, { k: 'k' }, { x: m.x, y: m.y, rot: 0, r: m.r }, { dx: bx - ax, dy: by - ay });
    juice(st, m.x, m.y, 'k', m.r, { dx: bx - ax, dy: by - ay }, 30);
    Snd.squish('k');
    go(st);
  }
  function startMelon(st) {
    const { W, H, u } = st.g, t = (performance.now() - (st.startAt || 0)) / 1000;
    // під карткою правил (вона вгорі), над Глеком
    return { x: W * 0.56, y: H * 0.76 + Math.sin(t * 2.2) * H * 0.012, r: KINDS.k.r * u * 1.3, rot: Math.sin(t * 1.3) * 0.15 };
  }

  function go(st) {
    const v = cur(st);
    if (!v || v.phase !== 'ready' || st.busy || !st.ctx.mine) return;
    st.busy = true; st.startCut = true;
    card(st, null);
    st.ctx.act('go').catch(() => null).then((r) => {
      st.busy = false;
      if (!r || !r.ok) { st.startCut = false; const vv = cur(st); if (vv && vv.phase === 'ready') card(st, 'ready'); }
    });
  }
  function again(st) {
    if (!st.ctx || !st.ctx.room || st.busy) return;
    st.busy = true;
    Promise.resolve(HGames.call('Rematch', st.ctx.room.id)).catch(() => null).then(() => { st.busy = false; });
  }
  function share(st) {
    const v = cur(st), text = ((v && v.daily) || {}).share || '';
    const done = () => st.ctx.toast('Скопійовано — встав у Балачки');
    if (navigator.clipboard) navigator.clipboard.writeText(text).then(done, () => st.ctx.toast(text)); else st.ctx.toast(text);
  }

  // ---------------------------------------------------------------------------------------------
  // Соковитість: половинки, сік, плями, кільця, уламки, тексти
  // ---------------------------------------------------------------------------------------------
  const MAXP = 700;
  function halves(st, f, p, dir) {
    const sc = st.g.H / 500, now = performance.now();
    const ang = dir && (dir.dx || dir.dy) ? Math.atan2(dir.dy, dir.dx) : Math.random() * Math.PI;
    const nx = -Math.sin(ang), ny = Math.cos(ang), sp = clamp(Math.hypot(dir.dx || 0, dir.dy || 0) / 20, 0.6, 1.6);
    for (const side of [1, -1]) {
      st.halves.push({ k: f.k, x: p.x, y: p.y, vx: nx * side * 110 * sc * sp + Math.cos(ang) * 60 * sc, vy: ny * side * 110 * sc * sp - 120 * sc,
        rot0: p.rot || 0, spin: 0, vr: side * rnd(2, 5), ang, side, r: p.r, born: now });
    }
    if (st.halves.length > 90) st.halves.splice(0, st.halves.length - 90);
    st.slices.push({ x: p.x, y: p.y, ang, r: p.r * 1.9, born: now });
  }
  function juice(st, x, y, k, r, dir, n) {
    const K0 = KINDS[k] || KINDS.a, sc = st.g.H / 500, now = performance.now();
    const ang = dir && (dir.dx || dir.dy) ? Math.atan2(dir.dy, dir.dx) : 0;
    const many = reduced() ? 0.5 : 1;
    for (let i = 0; i < Math.round(n * many); i++) {
      // більше бризок уздовж розрізу
      const a = (Math.random() < 0.55 ? ang + (Math.random() < 0.5 ? 0 : Math.PI) : Math.random() * Math.PI * 2) + rnd(-0.5, 0.5);
      const v = rnd(90, 420) * sc;
      st.drops.push({ x, y, vx: Math.cos(a) * v, vy: Math.sin(a) * v - 140 * sc, r: r * rnd(0.05, 0.14), c: K0.juice, born: now, life: rnd(450, 900) });
    }
    if (st.drops.length > MAXP) st.drops.splice(0, st.drops.length - MAXP);
    splat(st, x, y, r, K0.juice);
    ring(st, x, y, K0.juice, r * 2.2);
  }
  function splat(st, x, y, r, c) {
    const blobs = [];
    for (let i = 0; i < 9; i++) { const a = Math.random() * Math.PI * 2, d = r * rnd(0.5, 1.6); blobs.push([Math.cos(a) * d, Math.sin(a) * d, r * rnd(0.12, 0.38)]); }
    st.stains.push({ x, y, r: r * rnd(0.8, 1.15), c, born: performance.now(), blobs, rot: Math.random() * Math.PI });
    if (st.stains.length > 34) st.stains.shift();
  }
  function ring(st, x, y, c, r) { st.rings.push({ x, y, c, r, born: performance.now() }); }
  function shards(st, x, y, r, n) {
    const sc = st.g.H / 500, now = performance.now();
    for (let i = 0; i < n; i++) {
      const a = Math.random() * Math.PI * 2, v = rnd(160, 620) * sc;
      st.shards.push({ x, y, vx: Math.cos(a) * v, vy: Math.sin(a) * v - 200 * sc, s: r * rnd(0.15, 0.45), rot: Math.random() * 6, vr: rnd(-12, 12),
        c: ['#c26a32', '#a85a28', '#e09a60', '#7a3a14', '#fff6e0'][i % 5], born: now, life: rnd(900, 1500) });
    }
  }
  function boomAt(st, x, y) {
    shards(st, x, y, KINDS.G.r * st.g.u, 46);
    juice(st, x, y, 'G', KINDS.G.r * st.g.u, null, 20);
    st.flashAt = performance.now();
    shake(st, 3); Snd.smash(); vibrate([80, 40, 120]);
    big(st, '<b>Бабах!</b><small>глек розбито</small>', 'lost', 1600);
  }
  function confetti(st) {
    const { W } = st.g, now = performance.now(), cols = ['#ff4d5e', '#ffd54a', '#5ccf6a', '#4fc3f7', '#fff', '#c4122a'];
    for (let i = 0; i < (reduced() ? 40 : 120); i++)
      st.shards.push({ x: rnd(0, W), y: -10, vx: rnd(-60, 60), vy: rnd(80, 260), s: rnd(3, 7), rot: Math.random() * 6, vr: rnd(-10, 10), c: cols[i % cols.length], born: now, life: rnd(1800, 3000), paper: true });
  }
  function floater(st, text, x, y, color, size) {
    st.floats.push({ text, x, y, color, size: size || 1, born: performance.now() });
    if (st.floats.length > 40) st.floats.shift();
  }
  function comboFx(st, n, pos) {
    if (!pos) return;
    const el = st.el.fx;
    if (el.childElementCount > 6) el.firstChild.remove();
    const d = document.createElement('div');
    d.className = 'sk-combo' + (n >= 5 ? ' hot' : '');
    d.innerHTML = '<b>Комбо ×' + n + '</b><small>+' + n + '</small>';
    d.style.left = Math.round(clamp(pos.x, 80, st.g.W - 80)) + 'px'; d.style.top = Math.round(clamp(pos.y - 40, 60, st.g.H - 60)) + 'px';
    el.appendChild(d);
    setTimeout(() => d.remove(), 1200);
    Snd.combo(n); shake(st, n >= 5 ? 2 : 1); vibrate(n >= 5 ? 40 : 20);
    say(st, pick(n >= 5 ? SAY.combo5 : SAY.combo));
  }
  function shake(st, lv) {
    if (reduced()) return;
    const s = st.el.stage;
    s.classList.remove('shake1', 'shake2', 'shake3'); void s.offsetWidth; s.classList.add('shake' + lv);
  }
  function vibrate(p) { try { if (navigator.vibrate && HGames.ui.coarse()) navigator.vibrate(p); } catch (e) { /* без вібро */ } }
  function say(st, text) {
    const el = st.el.say, now = performance.now();
    if (!el || !text || now - (st.sayAt || 0) < 1800) return;
    st.sayAt = now;
    el.textContent = text;
    el.classList.remove('on'); void el.offsetWidth; el.classList.add('on');
    clearTimeout(st.sayT); st.sayT = setTimeout(() => el.classList.remove('on'), 2400);
  }
  function big(st, html, cls, ms) {
    const el = st.el.big;
    el.innerHTML = html || '';
    el.className = 'sk-big' + (cls ? ' B-' + cls : '');
    void el.offsetWidth; el.classList.add('on');
    clearTimeout(st.bigT);
    if (ms) st.bigT = setTimeout(() => { el.classList.remove('on'); }, ms);
  }
  function throwGlek(st) {
    const g = st.el.glek; if (!g) return;
    const now = performance.now(); if (now - (st.throwAt || 0) < 350) return; st.throwAt = now;
    g.classList.remove('throw'); void g.offsetWidth; g.classList.add('throw');
  }

  // ---------------------------------------------------------------------------------------------
  // Картки: «Поїхали» і підсумок
  // ---------------------------------------------------------------------------------------------
  function card(st, kind) {
    const el = st.el.card, v = cur(st);
    if (!kind || !v) { el.hidden = true; el._k = ''; return; }
    const md = MODES[v.mode] || MODES.classic, dl = v.daily;
    const sig = kind + '|' + v.mode + '|' + v.best + '|' + (v.end ? v.end.score : '') + '|' + (dl ? (dl.board || []).length + ':' + dl.players : '');
    if (el._k === sig && !el.hidden) return;
    el._k = sig;
    let h = '';
    if (kind === 'ready') {
      h = '<div class="sk-c sk-ready"><h3>' + md.icon + ' ' + esc(md.title) + (dl && dl.no ? ' №' + dl.no : '') + '</h3><ul>' + md.rules.map((x) => '<li>' + x + '</li>').join('') + '</ul>'
        + (v.best ? '<p class="sk-rec">🏆 рекорд — <b>' + fmtN(v.best) + '</b></p>' : '')
        + (st.ctx.mine ? '<button type="button" class="sk-go" data-a="go" data-pad-first>Поїхали!</button><p class="sk-hint">або розріж кавун змахом</p>' : '<p class="sk-hint">дивишся збоку</p>')
        + '</div>';
    } else {
      const e = v.end || {};
      const why = { glek: '💥 Глек розбито!', miss: '✕ Три промахи', time: '⏱ Час!', left: '🚪 Пішов з поля' }[e.why] || 'Кінець';
      h = '<div class="sk-c sk-over"><div class="sk-why">' + why + '</div>'
        + '<div class="sk-total"><b data-n="' + (e.score || 0) + '">' + fmtN(e.score || 0) + '</b><small>' + points(e.score || 0) + '</small></div>'
        + (e.rec && e.score > 0 ? '<div class="sk-newrec">🏆 Новий рекорд!</div>' : v.best ? '<div class="sk-old">рекорд — ' + fmtN(v.best) + '</div>' : '')
        + '<div class="sk-stats"><span>🔪 <b>' + (e.cuts || 0) + '</b> порізано</span>'
        + (e.combo >= 3 ? '<span>⚡ комбо <b>×' + e.combo + '</b></span>' : '')
        + (e.big ? '<span>🍉 <b>' + e.big + '</b> по кавуну</span>' : '')
        + (v.mode !== 'zen' ? '<span>🏺 ' + (e.gleks ? '<b>' + e.gleks + '</b> ' + (e.gleks === 1 ? 'глек' : 'глеки') : 'глеки цілі') + '</span>' : '') + '</div>';
      if (dl) {
        const rows = dl.board || [];
        h += '<div class="sk-day"><h4>Сьогодні' + (dl.place ? ' — ти <b>' + dl.place + '</b> з ' + dl.players : '') + '</h4>'
          + (rows.length ? '<ol>' + rows.map((r) => '<li' + (r.nick === st.ctx.me.nick ? ' class="me"' : '') + '><span>' + esc(r.nick) + '</span><b>' + fmtN(r.points) + '</b></li>').join('') + '</ol>' : '')
          + '<button type="button" class="sk-share" data-a="share">📋 Поділитись</button><p class="sk-hint">новий віз — після півночі</p></div>';
      } else if (st.ctx.mine) h += '<button type="button" class="sk-go" data-a="again" data-pad-first>Ще раз!</button>';
      h += '</div>';
    }
    el.innerHTML = h;
    el.hidden = false;
    const n = el.querySelector('.sk-total b');
    if (n && !reduced()) countUp(n, +n.dataset.n);
  }
  function countUp(el, to) {
    const t0 = performance.now(), dur = clamp(to * 6, 300, 1100);
    const step = () => { const k = Math.min(1, (performance.now() - t0) / dur); el.textContent = fmtN(Math.round(to * (1 - Math.pow(1 - k, 3)))); if (k < 1) requestAnimationFrame(step); };
    el.textContent = '0'; requestAnimationFrame(step);
    setTimeout(() => { el.textContent = fmtN(to); }, dur + 400);   // схована вкладка без rAF
  }

  // ---------------------------------------------------------------------------------------------
  // Табло
  // ---------------------------------------------------------------------------------------------
  function shownScore(st) {
    const v = cur(st);
    if (!v) return 0;
    let s = v.score || 0;
    const now = performance.now();
    for (const [id, x] of st.gains) {
      if (now - x.at > 2500) { st.gains.delete(id); continue; }
      s += x.gain;
    }
    // удари по великому кавуну, яких сервер ще не бачив (×2 тут не рахуємо — за мить прийде точне)
    if (v.phase === 'go') s += Math.max(0, st.bigHits - (v.big || 0));
    return Math.max(0, s);
  }

  function hud(st) {
    const v = cur(st), el = st.el;
    if (!v) return;
    const sc = v.phase === 'over' && v.end ? v.end.score : shownScore(st);
    if (el.score._v !== sc) {
      el.score._v = sc; el.score.textContent = fmtN(sc);
      el.score.classList.remove('bump'); void el.score.offsetWidth; el.score.classList.add('bump');
    }
    const bt = v.best ? '🏆 ' + fmtN(v.best) : '';
    if (el.best.textContent !== bt) el.best.textContent = bt;
    // життя
    if (v.lives) {
      const sig = v.lives + ':' + v.misses;
      if (el.lives._s !== sig) {
        el.lives._s = sig;
        let h = '';
        for (let i = 0; i < v.lives; i++) h += '<i class="' + (i < v.misses ? 'x' : '') + '">✕</i>';
        el.lives.innerHTML = h;
      }
      el.lives.hidden = false;
    } else el.lives.hidden = true;
    // годинник
    const t = tNow(st), go = v.phase === 'go' && st.t0;
    if (v.len) {
      const g = go ? gNow(st) : 0, left = go ? clamp(v.len - g, 0, v.len) : v.phase === 'over' ? 0 : v.len;
      const s = Math.ceil(left / 1000), txt = Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
      if (el.timeB.textContent !== txt) {
        el.timeB.textContent = txt;
        if (go && s <= 5 && s > 0) Snd.tick();
      }
      el.timeBar.style.width = (100 * left / v.len).toFixed(1) + '%';
      el.time.classList.toggle('hot', !!go && left < 10000);
      el.time.hidden = false;
      if (go && left <= 0 && !st.timeUp) { st.timeUp = true; big(st, '<b>Час!</b>', 'time', 1400); }
    } else el.time.hidden = true;
    // мороз, ×2
    const fz = frost(st), db = doubles(st);
    const fLeft = go ? Math.max(0, ...fz.map(([, e]) => e - t)) : 0, dLeft = go ? Math.max(0, ...db.map(([, e]) => e - t)) : 0;
    const bs = (fLeft > 0 ? 'f' + Math.ceil(fLeft / 1000) : '') + (dLeft > 0 ? 'd' + Math.ceil(dLeft / 1000) : '');
    if (el.buffs._s !== bs) {
      el.buffs._s = bs;
      el.buffs.innerHTML = (fLeft > 0 ? '<span class="frost">❄ ' + Math.ceil(fLeft / 1000) + '</span>' : '') + (dLeft > 0 ? '<span class="gold">×2 ' + Math.ceil(dLeft / 1000) + '</span>' : '');
    }
    el.tint.className = 'sk-tint' + (fLeft > 0 ? ' frost' : '') + (dLeft > 0 ? ' gold' : '');
    el.box.classList.toggle('P-go', !!go);
  }
  function paintSnd(st) { st.el.snd.textContent = Snd.on() ? '🔊' : '🔇'; st.el.snd.title = Snd.on() ? 'Звук увімкнено' : 'Звук вимкнено — увімкнути'; }

  // ---------------------------------------------------------------------------------------------
  // Полотно й цикл
  // ---------------------------------------------------------------------------------------------
  function layout(root, st) {
    const b = st.el.stage.getBoundingClientRect();
    const W = Math.max(240, Math.round(b.width)), H = Math.max(240, Math.round(b.height));
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    if (st.g && st.g.W === W && st.g.H === H && st.g.dpr === dpr) return;
    st.g = { W, H, dpr, u: Math.min(W * 0.75, H) };
    st.sprites = {};
    const cv = st.el.cv;
    cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr);
    cv.style.width = W + 'px'; cv.style.height = H + 'px';
    st.c2 = cv.getContext('2d');
    paintBack(st);
    const gk = st.el.glek, u = st.g.u;
    gk.style.width = Math.round(clamp(u * 0.2, 56, 120)) + 'px';
    draw(st);
  }

  function draw(st) {
    const g = st.c2, G = st.g;
    if (!g || !G) return false;
    const now = performance.now(), sc = G.H / 500, v = cur(st);
    g.setTransform(G.dpr, 0, 0, G.dpr, 0, 0);
    g.drawImage(st.back, 0, 0, G.W, G.H);
    // плями соку на дошці: свіжі — яскраві, гаснуть за кілька секунд
    st.stains = st.stains.filter((s) => now - s.born < 6500);
    for (const s of st.stains) {
      const age = now - s.born;
      g.globalAlpha = 0.5 * (age < 4000 ? 1 : 1 - (age - 4000) / 2500);
      g.fillStyle = s.c;
      g.beginPath(); g.ellipse(s.x, s.y, s.r, s.r * 0.75, s.rot, 0, Math.PI * 2); g.fill();
      for (const [dx, dy, rr] of s.blobs) { g.beginPath(); g.arc(s.x + dx, s.y + dy, rr, 0, Math.PI * 2); g.fill(); }
    }
    g.globalAlpha = 1;
    // стартовий кавун
    if (v && v.phase === 'ready' && !st.startCut && st.ctx.mine) {
      const m = startMelon(st), sp = sprite(st, 'k');
      g.save(); g.translate(m.x, m.y); g.rotate(m.rot);
      const pulse = 1 + Math.sin(now / 260) * 0.03;
      g.globalAlpha = 0.35 + 0.15 * Math.sin(now / 300);
      g.fillStyle = '#fff6c0'; g.beginPath(); g.arc(0, 0, m.r * 1.5 * pulse, 0, Math.PI * 2); g.fill();
      g.globalAlpha = 1;
      blit(g, sp, (m.r / sp.r) * pulse);
      g.restore();
    }
    // плоди
    // після кінця плоди ще падають (на глеку Класики — завмирають), а нові не вилітають
    const go = !!(v && v.phase === 'go' && st.t0);
    const anim = !!(st.t0 && v && v.phase !== 'ready');
    if (go) st.lastG = gNow(st);
    else if (anim && v.end && v.end.why !== 'glek') st.lastG = (st.lastG || 0) + Math.min(50, now - (st.lastDraw || now));
    st.lastDraw = now;
    const gm = st.lastG || 0;
    let alive = false;
    const lives = v && v.lives;
    for (const [id, f] of st.fruits) {
      const p = anim && (go || !f.cut) ? where(st, f, gm) : null;
      // ще не вилетів (сервер показує розклад на секунду наперед) — чекає; давно впав — геть
      if (!p) { if (!anim || gm - f.at > f.d + 2500 || (!go && f.at > gm)) st.fruits.delete(id); continue; }
      if (!go && p.s > 1.1) { st.fruits.delete(id); continue; }
      alive = true;
      if (!f.thrown && p.s > 0) { f.thrown = true; throwGlek(st); }
      if (f.k === 'B') { drawBig(st, g, f, p, now); continue; }
      if (f.cut) continue;
      if (go && lives && !f.missFx && p.s > 1.0 && f.k !== 'G' && st.ctx.mine) {
        f.missFx = true;
        floater(st, '✕', clamp(p.x, 20, G.W - 20), G.H - 24, '#ff3b3b', 2);
        Snd.miss();
        if (Math.random() < 0.4) say(st, pick(SAY.miss));
      }
      const sp = sprite(st, f.k);
      g.save(); g.translate(p.x, p.y); g.rotate(f.k === 'G' ? Math.sin(p.rot) * 0.4 : p.rot);
      if (f.k === 'G') {   // глек пульсує червоним — «не чіпай»
        const a = 0.25 + 0.2 * Math.sin(now / 120);
        g.fillStyle = 'rgba(255,40,30,' + a + ')'; g.beginPath(); g.arc(0, 0, p.r * 1.45, 0, Math.PI * 2); g.fill();
      } else if (f.k === 'F' || f.k === 'X' || f.k === 'Z') {
        g.globalAlpha = 0.5 + 0.3 * Math.sin(now / 150);
        g.strokeStyle = f.k === 'F' ? '#bff0ff' : '#ffe48a'; g.lineWidth = 3;
        g.beginPath(); g.arc(0, 0, p.r * (1.3 + 0.08 * Math.sin(now / 200)), 0, Math.PI * 2); g.stroke();
        g.globalAlpha = 1;
      }
      blit(g, sp);
      g.restore();
    }
    // половинки
    st.halves = st.halves.filter((h) => now - h.born < 2200 && h.y < G.H + h.r * 3);
    for (const h of st.halves) {
      const dt = Math.min(0.05, (now - (h.t || h.born)) / 1000); h.t = now;
      h.vy += 1500 * sc * dt; h.x += h.vx * dt; h.y += h.vy * dt; h.spin += h.vr * dt;
      const sp = sprite(st, h.k), k = h.r / sp.r;
      g.save(); g.translate(h.x, h.y); g.rotate(h.spin + h.ang);
      g.beginPath(); g.rect(-h.r * 2.2, h.side > 0 ? 0 : -h.r * 2.6, h.r * 4.4, h.r * 2.6); g.clip();
      g.save(); g.rotate(h.rot0 - h.ang); blit(g, sp, k); g.restore();
      face(g, h.k, h.r);
      g.restore();
    }
    // спалахи розрізу
    st.slices = st.slices.filter((s) => now - s.born < 160);
    for (const s of st.slices) {
      const k = 1 - (now - s.born) / 160;
      g.save(); g.translate(s.x, s.y); g.rotate(s.ang);
      g.globalAlpha = k; g.fillStyle = '#fffdf0';
      g.beginPath(); g.ellipse(0, 0, s.r * (1.2 - k * 0.2), Math.max(1.2, s.r * 0.07 * k), 0, 0, Math.PI * 2); g.fill();
      g.restore();
    }
    g.globalAlpha = 1;
    // кільця соку
    st.rings = st.rings.filter((r) => now - r.born < 420);
    for (const r of st.rings) {
      const k = (now - r.born) / 420;
      g.globalAlpha = 0.55 * (1 - k); g.strokeStyle = r.c; g.lineWidth = Math.max(1, 6 * (1 - k));
      g.beginPath(); g.arc(r.x, r.y, r.r * (0.3 + 0.9 * k), 0, Math.PI * 2); g.stroke();
    }
    g.globalAlpha = 1;
    // краплі
    st.drops = st.drops.filter((d) => now - d.born < d.life);
    for (const d of st.drops) {
      const dt = Math.min(0.05, (now - (d.t || d.born)) / 1000); d.t = now;
      d.vy += 1300 * sc * dt; d.x += d.vx * dt; d.y += d.vy * dt;
      const k = 1 - (now - d.born) / d.life;
      g.globalAlpha = Math.min(1, k * 1.5); g.fillStyle = d.c;
      const sp = Math.min(3, Math.hypot(d.vx, d.vy) / 260);
      g.save(); g.translate(d.x, d.y); g.rotate(Math.atan2(d.vy, d.vx));
      g.beginPath(); g.ellipse(0, 0, Math.max(1, d.r) * (1 + sp * 0.6), Math.max(1, d.r), 0, 0, Math.PI * 2); g.fill();
      g.restore();
    }
    g.globalAlpha = 1;
    // уламки глека й конфеті
    st.shards = st.shards.filter((s) => now - s.born < s.life && s.y < G.H + 40);
    for (const s of st.shards) {
      const dt = Math.min(0.05, (now - (s.t || s.born)) / 1000); s.t = now;
      if (s.paper) { s.vy = Math.min(s.vy + 120 * dt, 220); s.vx += Math.sin(now / 300 + s.rot) * 40 * dt; } else s.vy += 1500 * sc * dt;
      s.x += s.vx * dt; s.y += s.vy * dt; s.rot += s.vr * dt;
      g.globalAlpha = Math.min(1, 2 * (1 - (now - s.born) / s.life));
      g.save(); g.translate(s.x, s.y); g.rotate(s.rot); g.fillStyle = s.c;
      if (s.paper) g.fillRect(-s.s, -s.s * 0.4, s.s * 2, s.s * 0.8);
      else { g.beginPath(); g.moveTo(-s.s, -s.s * 0.6); g.lineTo(s.s, -s.s * 0.2); g.lineTo(s.s * 0.2, s.s); g.closePath(); g.fill(); }
      g.restore();
    }
    g.globalAlpha = 1;
    // спалах від глека
    if (st.flashAt && now - st.flashAt < 450) {
      g.fillStyle = 'rgba(255,250,235,' + (0.85 * (1 - (now - st.flashAt) / 450)) + ')'; g.fillRect(0, 0, G.W, G.H);
    }
    // тексти «+1»
    st.floats = st.floats.filter((f) => now - f.born < 900);
    g.textAlign = 'center'; g.textBaseline = 'middle';
    for (const f of st.floats) {
      const k = (now - f.born) / 900, s = Math.round(clamp(G.u * 0.045, 14, 30) * f.size * (k < 0.15 ? 0.6 + k * 2.7 : 1));
      g.globalAlpha = k < 0.7 ? 1 : 1 - (k - 0.7) / 0.3;
      g.font = '900 ' + s + 'px Onest, system-ui, sans-serif';
      g.lineWidth = Math.max(3, s * 0.18); g.strokeStyle = 'rgba(36,20,46,.9)'; g.fillStyle = f.color;
      g.strokeText(f.text, f.x, f.y - k * 40 * sc); g.fillText(f.text, f.x, f.y - k * 40 * sc);
    }
    g.globalAlpha = 1;
    // лезо
    drawTrail(st, g, now);
    if (st.pad && now - st.pad.seen < 3000 && st.ctx.mine) {
      g.save(); g.translate(st.pad.x, st.pad.y);
      g.strokeStyle = st.pad.hold ? '#fff6c0' : 'rgba(255,255,255,.75)'; g.lineWidth = 2;
      g.beginPath(); g.arc(0, 0, st.pad.hold ? 9 : 12, 0, Math.PI * 2); g.stroke();
      sparkle(g, 0, 0, st.pad.hold ? 8 : 5, '#fff');
      g.restore();
    }
    if (st.wCount >= 3 && st.ptr && now - st.ptr.at > 260) endSwipe(st);
    return alive || st.halves.length || st.drops.length || st.trail.length || st.stains.length || st.shards.length || st.floats.length || go
      || (v && v.phase === 'ready') || !!(st.pad && now - st.pad.seen < 3000);
  }

  function drawBig(st, g, f, p, now) {
    const sp = sprite(st, 'B'), hit = f.hitAt ? now - f.hitAt : 1e9;
    const wob = hit < 220 ? 1 + 0.12 * Math.sin(hit / 220 * Math.PI) : 1;
    const shakeX = hit < 220 ? rnd(-4, 4) : 0;
    if (!f.burst && (p.s > 0.98 || st.bigHits >= K(st).bigMax || (cur(st) && cur(st).phase === 'over'))) {
      f.burst = true;
      if (st.bigHits > 0) {
        juice(st, p.x, p.y, 'k', p.r, null, 60); shards(st, p.x, p.y, p.r * 0.4, 0);
        halves(st, { k: 'B' }, p, { dx: 1, dy: -0.2 });
        Snd.burst(); shake(st, 2);
        big(st, '<b>🍉 ×' + st.bigHits + '</b><small>великий кавун</small>', 'gold', 1500);
      }
    }
    if (f.burst) return;
    g.save(); g.translate(p.x + shakeX, p.y); g.rotate(Math.sin(now / 400) * 0.08);
    blit(g, sp, wob);
    if (st.bigHits > 0) {
      g.font = '900 ' + Math.round(p.r * 0.55) + 'px Onest, system-ui, sans-serif'; g.textAlign = 'center'; g.textBaseline = 'middle';
      g.lineWidth = p.r * 0.1; g.strokeStyle = '#24142e'; g.fillStyle = '#ffe08a';
      g.strokeText('×' + st.bigHits, 0, -p.r * 1.25); g.fillText('×' + st.bigHits, 0, -p.r * 1.25);
    }
    g.restore();
  }

  /// Слід леза: стрічка, що звужується до хвоста, — світіння і біла серцевина.
  function drawTrail(st, g, now) {
    st.trail = st.trail.filter((p) => now - p.at < 170);
    const pts = st.trail;
    if (pts.length < 2) return;
    const gold = playing(st) && inside(doubles(st), tNow(st)), icy = playing(st) && inside(frost(st), tNow(st));
    const w0 = clamp(st.g.u * 0.022, 5, 14);
    for (const [wk, col] of [[2.4, gold ? 'rgba(255,200,60,.35)' : icy ? 'rgba(140,220,255,.35)' : 'rgba(180,220,255,.28)'], [1, '#ffffff']]) {
      const L = [], R = [];
      for (let i = 0; i < pts.length; i++) {
        const a = pts[Math.max(0, i - 1)], b = pts[Math.min(pts.length - 1, i + 1)];
        let nx = -(b.y - a.y), ny = b.x - a.x; const l = Math.hypot(nx, ny) || 1; nx /= l; ny /= l;
        const k = i / (pts.length - 1), fade = 1 - (now - pts[i].at) / 170;
        const w = w0 * wk * k * Math.max(0.15, fade) * 0.5;
        L.push([pts[i].x + nx * w, pts[i].y + ny * w]); R.push([pts[i].x - nx * w, pts[i].y - ny * w]);
      }
      g.fillStyle = col; g.beginPath(); g.moveTo(L[0][0], L[0][1]);
      for (const p of L) g.lineTo(p[0], p[1]);
      for (let i = R.length - 1; i >= 0; i--) g.lineTo(R[i][0], R[i][1]);
      g.closePath(); g.fill();
    }
    const h = pts[pts.length - 1];
    g.fillStyle = '#fff'; g.beginPath(); g.arc(h.x, h.y, w0 * 0.5, 0, Math.PI * 2); g.fill();
  }

  // ---------------------------------------------------------------------------------------------
  // Джойстик: лівий стік водить лезо, Ⓐ чи RT — тримай і веди
  // ---------------------------------------------------------------------------------------------
  function padPoll(st, dt) {
    const list = window.HPad && HPad.list ? HPad.list() : [];
    const gp = list && list[0];
    if (!gp || !st.g) return;
    const ax = gp.axes[0] || 0, ay = gp.axes[1] || 0, m = Math.hypot(ax, ay);
    const hold = !!((gp.buttons[0] && gp.buttons[0].pressed) || (gp.buttons[7] && gp.buttons[7].pressed) || (gp.buttons[5] && gp.buttons[5].pressed));
    if (!st.pad) { if (m < 0.3 && !hold) return; st.pad = { x: st.g.W / 2, y: st.g.H / 2, hold: false, seen: 0 }; }
    const P = st.pad;
    if (m > 0.12 || hold) P.seen = performance.now();
    const k = m > 0.12 ? Math.pow((m - 0.12) / 0.88, 1.5) / m : 0, spd = st.g.u * 2.2 * dt / 1000;
    const ox = P.x, oy = P.y;
    P.x = clamp(P.x + ax * k * spd, 0, st.g.W); P.y = clamp(P.y + ay * k * spd, 0, st.g.H);
    const now = performance.now();
    if (hold && !P.hold) { newSwipe(st); st.trail.push({ x: P.x, y: P.y, at: now }); P.movedAt = now; }
    if (!hold && P.hold) endSwipe(st);
    P.hold = hold;
    if (hold && (ox !== P.x || oy !== P.y)) {
      if (now - (P.movedAt || now) > 260) newSwipe(st);   // спинився — новий змах, як мишею
      moveBlade(st, ox, oy, P.x, P.y, 14, dt);
      P.movedAt = now;
    }
  }

  function loop(st) {
    if (st.raf || !st.shown) return;
    let last = performance.now();
    const step = () => {
      st.raf = 0;
      if (!st.shown || !st.g || st.dead) return;
      const now = performance.now();
      padPoll(st, Math.min(50, now - last)); last = now;
      const more = draw(st);
      hud(st);
      if (more) st.raf = requestAnimationFrame(step);
    };
    st.raf = requestAnimationFrame(step);
  }
  function stop(st) { if (st.raf) cancelAnimationFrame(st.raf); st.raf = 0; }

  function sync(root, st) {
    const v = cur(st);
    if (!v || !st.el) return;
    syncClock(st, v);
    merge(st, v);
    hud(st);
    loop(st);
    const cardEl = st.root && st.root.closest('.gtable'), se = cardEl && cardEl.querySelector('.gstatus'), t = status(st.ctx);
    if (se && t && se.textContent !== t) se.textContent = t;
  }

  function status(ctx) {
    const st = ctx && ctx._sk, v = st && cur(st);
    if (!v) return 'Шаткуй!';
    const md = MODES[v.mode] || MODES.classic;
    if (v.phase === 'go') return md.icon + ' ' + md.title + ': ' + fmtN(shownScore(st)) + ' ' + points(shownScore(st));
    if (v.phase === 'over' && v.end) return md.icon + ' ' + md.title + ': ' + fmtN(v.end.score) + ' ' + points(v.end.score) + (v.end.rec && v.end.score ? ' — рекорд!' : '');
    return md.icon + ' ' + md.title + ' — ріж кавуна, щоб почати';
  }

  function setup(root, ctx) {
    let st = root._sk;
    if (!st) {
      st = root._sk = {
        root, offs: [], offs2: [], off: 0, fruits: new Map(), gains: new Map(), frzLocal: [], dblLocal: [], sent: [],
        halves: [], drops: [], stains: [], rings: [], slices: [], shards: [], floats: [], trail: [], sprites: {},
        shown: ctx.shown !== false, w: 0, wSeq: 0, wCount: 0, bigHits: 0, bigW: -1, phase: '',
      };
      st.ctx = ctx; ctx._sk = st;
      skeleton(root, st);
      paintSnd(st);
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => { clearTimeout(st.roT); st.roT = setTimeout(() => layout(root, st), 60); });
        st.ro.observe(st.el.stage);
      }
      const cardEl = root.closest && root.closest('.gtable');
      if (cardEl) cardEl.classList.add('sk-wide');
    }
    st.ctx = ctx; ctx._sk = st;
    return st;
  }

  const mod = (id) => ({
    id,
    added: '2026-10-25',
    icon: ICON,
    seatNames: () => 'шаткар',
    pad: {
      dirs: true,
      a: 'Space',
      exit: false,
      hint: '{dpad} лезо · {a} тримай і веди — шаткуєш',
      when: (ctx) => !!(ctx.mine && ctx.view && ctx.view.phase === 'go'),
    },
    mount(root, ctx) {
      const st = setup(root, ctx);
      layout(root, st);
      sync(root, st);
    },
    update(root, ctx) {
      const st = setup(root, ctx);
      if (!st.g) layout(root, st);
      sync(root, st);
    },
    visible(root, ctx, on) {
      const st = root._sk;
      if (!st) return;
      st.shown = on;
      if (on) { layout(root, st); sync(root, st); } else stop(st);
    },
    onKey(e, ctx) {
      const st = ctx._sk;
      if (!st) return false;
      const tag = e.target && e.target.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA') return false;
      const v = cur(st);
      if (v && v.phase === 'go') return ['Space', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(e.code) || e.key === ' ';
      if (e.repeat) return false;
      if (e.code === 'Space' || e.key === ' ' || e.code === 'Enter' || e.key === 'Enter') {
        if (v && v.phase === 'ready') { go(st); return true; }
        if (v && v.phase === 'over' && !v.daily && st.overShown) { again(st); return true; }
      }
      return false;
    },
    status,
    unmount(root) {
      const st = root._sk;
      if (!st) return;
      st.dead = true;
      stop(st);
      clearTimeout(st.roT); clearTimeout(st.sayT); clearTimeout(st.flushT); clearTimeout(st.bigT); clearTimeout(st.overT);
      if (st.ro) st.ro.disconnect();
      st.offs.forEach((f) => f()); st.offs = [];
      const cardEl = root.closest && root.closest('.gtable');
      if (cardEl) cardEl.classList.remove('sk-wide');
      if (st.ctx) st.ctx._sk = null;
      root._sk = null;
    },
    // для перевірок: формули без DOM
    qa: { gameMs, where },
  });

  HGames.register(mod('shatkuy'));
  HGames.register(mod('shatkuy-fair'));
  HGames.register(mod('shatkuy-zen'));
  HGames.register(mod('shatkuy-daily'));
})();
