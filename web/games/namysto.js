/*
  «Намисто» (namysto, namysto-daily; docs/games/specs/namysto.md). Дядько Глек посередині плює намистинами, разок
  котиться жолобом до криниці; три однакові впритул — розсипаються.

  Грає браузер: та сама цілочислова симуляція, що й на сервері (namysto-sim.js), крутиться тут миттєво, а на сервер
  летить журнал «на кроці N — постріл у точку / поміняв» і «я вже на кроці s» (раз на 250 мс). Сервер проганяє той
  самий журнал і рахує очки сам. Епоха у виді (ep) росте, коли треба перезібратись із журналу сервера (відстали
  понад 3 с, закрили вкладку) — тоді копія будується з jr і докочується до real.

  Полотно — свого розміру: задній буфер = показаний розмір × DPR (не більше 2), а не 720×DPR, — на телефоні це
  вчетверо менше пікселів. rAF крутиться лише поки видно поле; тло й жолоб намальовані раз в окремий шар, намистини —
  готові спрайти. Звук — синтез WebAudio, типово вимкнено (🔊).
*/
(() => {
  'use strict';

  const W = 720, PX = 16, R = 16;   // поле в px, su на px, радіус намистини
  const STEP_MS = 1000 / 60;
  const BATCH_MS = 250, SAVE_MS = 10000;
  const DPR_MAX = 2;
  const AIM_DIST = 320;
  const FINGER_GLEK = 46;   // дотик ближче до Глека — «поміняти», а не постріл

  // Кольори намистин: коралі, бурштин, бірюза, смарагд, перли, аметист. Візерунок — щоб розрізняли й дальтоніки.
  const BEADS = [
    { name: 'коралі', c: '#d8343a', hi: '#ffb3a8', lo: '#6e0d16', mark: 'dot' },
    { name: 'бурштин', c: '#f0a020', hi: '#ffe7a0', lo: '#7a4300', mark: 'ring' },
    { name: 'бірюза', c: '#22b3ae', hi: '#b8fff7', lo: '#0b4f4c', mark: 'cross' },
    { name: 'смарагд', c: '#3c9e45', hi: '#c7f5b5', lo: '#123f17', mark: 'bar' },
    { name: 'перли', c: '#ebe4d6', hi: '#ffffff', lo: '#8a7f6c', mark: 'none' },
    { name: 'аметист', c: '#8e4fc2', hi: '#e2c2ff', lo: '#35134f', mark: 'two' },
  ];
  const PLACES = [
    { name: 'Хата', icon: '🏠', top: '#f1dcb6', bot: '#d8b484', groove: '#7a4a24', grooveHi: '#a8743e', thread: '#b23a2a', orn: '#b23a2a', orn2: '#2b2118' },
    { name: 'Левада', icon: '🌿', top: '#bfe0a2', bot: '#7fb36a', groove: '#5a4026', grooveHi: '#8a6a40', thread: '#f4f0e0', orn: '#ffffff', orn2: '#f4d24a' },
    { name: 'Ярмарок', icon: '🎪', top: '#ffe08a', bot: '#f4a948', groove: '#7a3d1c', grooveHi: '#b5652e', thread: '#2f6fd0', orn: '#d8343a', orn2: '#2f6fd0' },
    { name: 'Купальська ніч', icon: '🌙', top: '#26345e', bot: '#0d1430', groove: '#0a0f22', grooveHi: '#2a3a66', thread: '#f4d24a', orn: '#fff6c8', orn2: '#9ad1ff' },
  ];
  const BONUS = {
    1: { icon: '⏳', name: 'Пісочний годинник', say: 'Повільніше!' },
    2: { icon: '⏪', name: 'Задній хід', say: 'Назад!' },
    3: { icon: '💥', name: 'Горщик із жаром', say: 'Бах!' },
    4: { icon: '🎯', name: 'Око', say: 'Око!' },
  };

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.2 5.2c1.6 5 9.9 5 11.6 0" fill="none" stroke="var(--muted)" stroke-width=".9"/>'
    + '<circle cx="3" cy="6.6" r="1.8" fill="#d8343a"/><circle cx="5.6" cy="8.6" r="1.8" fill="#f0a020"/>'
    + '<circle cx="8.4" cy="9.3" r="1.8" fill="#22b3ae"/><circle cx="11.1" cy="8.5" r="1.8" fill="#3c9e45"/>'
    + '<circle cx="13.3" cy="6.5" r="1.8" fill="#8e4fc2"/></svg>';
  const ICON_DAY = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="8" cy="5.2" r="2.6" fill="var(--accent)"/>'
    + '<path d="M8 .6v1.1M3.9 2.3l.8.8M12.1 2.3l-.8.8" stroke="var(--accent)" stroke-width="1.1" stroke-linecap="round"/>'
    + '<circle cx="2.6" cy="10.4" r="1.7" fill="#d8343a"/><circle cx="5.6" cy="12.6" r="1.7" fill="#f0a020"/>'
    + '<circle cx="9" cy="13" r="1.7" fill="#22b3ae"/><circle cx="12.4" cy="11.4" r="1.7" fill="#8e4fc2"/></svg>';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
  const num = (n) => String(Math.round(n)).replace(/\B(?=(\d{3})+(?!\d))/g, '\u00a0');
  const plural = (n, a, b, c) => { const t = Math.abs(n) % 100, o = t % 10; return t > 10 && t < 20 ? c : o === 1 ? a : o >= 2 && o <= 4 ? b : c; };
  const ochok = (n) => num(n) + ' ' + plural(n, 'очко', 'очки', 'очок');
  const coarse = () => !!(window.HGames && HGames.ui && HGames.ui.coarse && HGames.ui.coarse());

  // ---------- траси й симуляція (спільні на всі картки) ----------

  let TR = null;          // { v, places, byN: Map n → { meta, track } }
  let trLoad = null;
  const live = new Set();

  function loadSim() {
    if (window.NamystoSim) return Promise.resolve();
    if (loadSim.p) return loadSim.p;
    loadSim.p = new Promise((resolve, reject) => {
      const s = document.createElement('script');
      s.src = '/games/namysto-sim.js?v=20261020';
      s.onload = () => resolve();
      s.onerror = () => { loadSim.p = null; reject(new Error('namysto-sim.js')); };
      document.head.appendChild(s);
    });
    return loadSim.p;
  }

  function loadTracks(v) {
    if (TR && (v == null || TR.v === v)) return Promise.resolve(TR);
    if (trLoad) return trLoad;
    trLoad = Promise.all([loadSim(), fetch('/api/games/namysto/tracks', { credentials: 'same-origin' }).then((r) => {
      if (!r.ok) throw new Error('tracks ' + r.status);
      return r.json();
    })]).then(([, d]) => {
      const byN = new Map();
      for (const t of d.tracks) byN.set(t.n, { meta: t, track: NamystoSim.Track.fromDelta(t.pts, t.sx, t.sy) });
      TR = { v: d.v, places: d.places, byN };
      trLoad = null;
      for (const st of live) { st.bgKey = ''; st.thumbs = false; refresh(st); }
      return TR;
    }).catch((e) => { trLoad = null; console.warn('[namysto] траси не завантажились', e); throw e; });
    return trLoad;
  }

  // ---------- стан картки ----------

  function stOf(ctx) {
    if (!ctx) return null;
    for (const s of live) if (s.ctx === ctx) return s;
    const id = ctx.room && ctx.room.id;
    if (!id) return null;
    for (const s of live) if (s.ctx && s.ctx.room && s.ctx.room.id === id && s.root.isConnected) { s.ctx = ctx; return s; }
    return null;
  }

  function state(root, ctx) {
    let st = root._nm;
    if (!st) {
      st = root._nm = {
        root, ctx, sim: null, simKey: '', ep: -1, paused: false, ended: false,
        anchorStep: 0, anchorAt: 0, pending: [], sentAt: 0, savedAt: 0, sentS: -1,
        aim: { a: -Math.PI / 2, x: 360, y: 40, mode: 'mouse', on: false }, held: { l: 0, r: 0 }, touch: null,
        fx: [], texts: [], raf: 0, tm: 0, k: 1, cw: 0, bg: null, bgKey: '', spr: null, sprKey: '', glek: null,
        hudAt: 0, hudKey: '', ovKey: '', pickKey: '', thumbs: false, drain: 0, drainAt: 0, won: 0, shake: 0,
        lastDraw: 0, padAt: 0, analogAt: 0, lastView: null, dangerAt: 0,
      };
      live.add(st);
      st.pumpTm = setInterval(() => pump(st), 200);
    }
    st.ctx = ctx;
    return st;
  }

  // ---------- DOM ----------

  function shell(root, st) {
    if (st.stage && st.stage.isConnected) return;
    root.classList.add('namysto');
    root.innerHTML = '';
    const hud = document.createElement('div');
    hud.className = 'nm-hud';
    hud.innerHTML = '<span class="nm-where"></span><b class="nm-score" title="Очки"></b><span class="nm-left" title="Скільки намистин ще в разку"></span>'
      + '<span class="nm-bon"></span>'
      + '<button type="button" class="ghost nm-snd" title="Звук (типово вимкнено)">🔇</button>'
      + '<button type="button" class="ghost nm-pause" title="Пауза: Esc, P або Ⓑ">⏸</button>';
    const stage = document.createElement('div');
    stage.className = 'nm-stage';
    const el = document.createElement('canvas');
    el.className = 'gcanvas nm-field';
    el.setAttribute('aria-label', 'Поле «Намиста»');
    const ov = document.createElement('div');
    ov.className = 'nm-ov';
    ov.hidden = true;
    stage.append(el, ov);
    const pick = document.createElement('div');
    pick.className = 'nm-pick';
    pick.hidden = true;
    const help = document.createElement('div');
    help.className = 'nm-help muted small';
    root.append(hud, stage, pick, help);
    st.hud = hud; st.stage = stage; st.el = el; st.ov = ov; st.pickEl = pick; st.help = help;
    st.g = el.getContext('2d');
    wire(st);
    fit(st);
    if (window.ResizeObserver) {
      st.ro = new ResizeObserver(() => fit(st));
      st.ro.observe(stage);
    }
  }

  /// Задній буфер — під показаний розмір (≤ 2× DPR), логічні координати — 720×720.
  function fit(st) {
    const el = st.el;
    const cw = Math.max(120, Math.round(el.clientWidth || st.stage.clientWidth || 360));
    const dpr = Math.min(DPR_MAX, window.devicePixelRatio || 1);
    const px = Math.round(cw * dpr);
    if (el.width !== px || el.height !== px) { el.width = px; el.height = px; }
    st.k = px / W;
    st.cw = cw;
    st.g.setTransform(st.k, 0, 0, st.k, 0, 0);
    st.bgKey = '';
    st.sprKey = '';
  }

  function fieldPoint(st, e) {
    const r = st.el.getBoundingClientRect();
    if (!(r.width > 0 && r.height > 0)) return { x: st.aim.x, y: st.aim.y };
    return { x: (e.clientX - r.left) * W / r.width, y: (e.clientY - r.top) * W / r.height };
  }

  function wire(st) {
    const el = st.el;
    el.addEventListener('contextmenu', (e) => e.preventDefault());
    el.addEventListener('pointermove', (e) => {
      const p = fieldPoint(st, e);
      if (e.pointerType === 'mouse') { aimAt(st, p.x, p.y, 'mouse'); return; }
      if (st.touch && st.touch.id === e.pointerId) {
        if (Math.hypot(p.x - st.touch.x0, p.y - st.touch.y0) > 12) st.touch.moved = true;
        if (!st.touch.glek || st.touch.moved) aimAt(st, p.x, p.y, 'touch');
      }
    });
    el.addEventListener('pointerleave', (e) => { if (e.pointerType === 'mouse' && st.aim.mode === 'mouse') st.aim.on = false; });
    el.addEventListener('pointerdown', (e) => {
      if (!HGames.ui.human(e)) return;
      const p = fieldPoint(st, e);
      if (phaseOf(st) === 'ready') { if (e.button === 0) go(st); return; }
      if (!playable(st)) return;
      e.preventDefault();
      if (e.pointerType === 'mouse') {
        aimAt(st, p.x, p.y, 'mouse');
        if (e.button === 2) swap(st);
        else if (e.button === 0) fire(st);
        return;
      }
      const t = track(st);
      const nearGlek = t && Math.hypot(p.x - t.Sx / PX, p.y - t.Sy / PX) < FINGER_GLEK;
      st.touch = { id: e.pointerId, x0: p.x, y0: p.y, glek: nearGlek, moved: false };
      try { el.setPointerCapture(e.pointerId); } catch { /* старий Safari */ }
      if (!nearGlek) aimAt(st, p.x, p.y, 'touch');
    });
    const up = (e) => {
      const t = st.touch;
      if (!t || t.id !== e.pointerId) return;
      st.touch = null;
      if (!playable(st)) return;
      if (t.glek && !t.moved) { swap(st); return; }
      const p = fieldPoint(st, e);
      aimAt(st, p.x, p.y, 'touch');
      fire(st);
    };
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', (e) => { if (st.touch && st.touch.id === e.pointerId) st.touch = null; });

    st.hud.querySelector('.nm-snd').addEventListener('click', () => { Snd.toggle(); paintHud(st, true); });
    st.hud.querySelector('.nm-pause').addEventListener('click', () => (st.paused ? resume(st) : pause(st)));
    st.ov.addEventListener('click', (e) => onOverlay(st, e));
    st.pickEl.addEventListener('click', (e) => {
      const b = e.target.closest('[data-n]');
      if (!b || b.disabled) return;
      st.ctx.act('level', { n: +b.dataset.n });
    });
    st.keyup = (e) => { const k = dirKey(e); if (k) st.held[k] = 0; };
    document.addEventListener('keyup', st.keyup);
    st.blurFn = () => { st.held.l = st.held.r = 0; };
    window.addEventListener('blur', st.blurFn);
    st.vis = () => { if (document.hidden) pause(st, true); };
    document.addEventListener('visibilitychange', st.vis);
  }

  function dirKey(e) {
    switch (e.code) {
      case 'ArrowLeft': case 'KeyA': return 'l';
      case 'ArrowRight': case 'KeyD': return 'r';
      default: return '';
    }
  }

  // ---------- вид → стан ----------

  const viewOf = (st) => (st.ctx && st.ctx.view) || {};
  const phaseOf = (st) => viewOf(st).phase || '';
  const lvOf = (st) => viewOf(st).lv || null;
  function track(st) {
    const lv = lvOf(st);
    return lv && TR && TR.byN.get(lv.n) ? TR.byN.get(lv.n).track : null;
  }
  const playable = (st) => !!(st.sim && st.ctx.mine && phaseOf(st) === 'go' && !st.paused && st.sim.State === NamystoSim.Playing);

  function refresh(st) {
    const v = viewOf(st);
    if (v.phase === 'go') syncGo(st, v);
    paintOverlay(st);
    paintPick(st);
    paintHud(st, true);
    wake(st);
  }

  /// Вид у фазі «котиться»: своя копія є й тієї ж епохи — лишаємо (вона попереду сервера); інакше — з журналу сервера.
  function syncGo(st, v) {
    if (!TR || !window.NamystoSim || !v.lv) return;
    const key = v.lv.n + ':' + v.seed + ':' + v.lv.beads + ':' + v.lv.speed;
    if (!st.sim || st.simKey !== key || v.ep !== st.ep) rebuild(st, v, key);
    if (v.paused && !st.paused) { st.paused = true; st.pending = []; }
  }

  function rebuild(st, v, key) {
    const t = TR.byN.get(v.lv.n);
    if (!t) return;
    const lv = v.lv;
    const sim = new NamystoSim.Sim(t.track, lv.colors, lv.beads, lv.speed, lv.par, v.seed >>> 0);
    const jr = v.jr || [];
    let k = 0;
    const upTo = Math.max(v.s || 0, 0);
    while (sim.State === NamystoSim.Playing && sim.S < upTo) {
      while (k < jr.length && jr[k][0] === sim.S) { const e = jr[k++]; sim.apply(e[1], e[2] || 0, e[3] || 0); }
      sim.step();
    }
    sim.events.length = 0;
    st.sim = sim;
    st.simKey = key;
    st.ep = v.ep;
    st.pending = [];
    st.paused = !!v.paused;
    st.ended = false;
    st.drain = 0; st.won = 0;
    st.fx.length = 0; st.texts.length = 0;
    // копія докочується до годинника сервера (real), а не стоїть на його кроці
    st.anchorStep = Math.max(sim.S, v.paused ? sim.S : (v.real || 0));
    st.anchorAt = performance.now();
    st.sentS = -1;
  }

  // ---------- дії ----------

  function aimAt(st, x, y, mode) {
    const t = track(st);
    st.aim.mode = mode;
    st.aim.on = true;
    st.aim.x = clamp(x, -200, W + 200);
    st.aim.y = clamp(y, -200, W + 200);
    if (t) st.aim.a = Math.atan2(st.aim.y - t.Sy / PX, st.aim.x - t.Sx / PX);
  }

  /// Ціль пострілу цілими пікселями: миша/палець — сама точка, стік і стрілки — точка на промені кута.
  function target(st) {
    const t = track(st);
    if (st.aim.mode === 'mouse' || st.aim.mode === 'touch') return [Math.round(st.aim.x), Math.round(st.aim.y)];
    const sx = t.Sx / PX, sy = t.Sy / PX;
    return [Math.round(sx + Math.cos(st.aim.a) * AIM_DIST), Math.round(sy + Math.sin(st.aim.a) * AIM_DIST)];
  }

  function fire(st) {
    if (!playable(st)) return;
    const [x, y] = target(st);
    // сервер приймає лише цілі в межах −2000…3000 — інше й не стріляємо (інакше копії розійдуться)
    if (!Number.isFinite(x) || !Number.isFinite(y) || x < -2000 || x > 3000 || y < -2000 || y > 3000) return;
    const s = st.sim.S;
    if (st.sim.fire(x, y)) { st.pending.push([s, 1, x, y]); Snd.shot(); pulse(st, 'fire'); }
  }

  function swap(st) {
    if (!playable(st)) return;
    const s = st.sim.S;
    if (st.sim.swap()) { st.pending.push([s, 2]); Snd.swap(); }
  }

  async function go(st) {
    const v = viewOf(st);
    if (v.phase !== 'ready' || !st.ctx.mine || st.going) return;
    if (!TR || !window.NamystoSim) { try { await loadTracks(v.tv); } catch { st.ctx.toast('Не вийшло завантажити траси — спробуй ще раз'); return; } }
    Snd.unlock();
    st.going = true;
    const r = await st.ctx.act('go');
    st.going = false;
    if (!r || !r.ok) return;
    // своя копія — одразу, з того самого зерна: вид «котиться» прийде за мить, і та сама епоха її не перебудує
    rebuild(st, { lv: v.lv, seed: v.seed, ep: v.ep, s: 0, real: 0, jr: [], paused: false }, v.lv.n + ':' + v.seed + ':' + v.lv.beads + ':' + v.lv.speed);
    Snd.start();
    refresh(st);
  }

  function batch(st, now) {
    const sim = st.sim;
    if (!sim || !st.ctx.mine || phaseOf(st) !== 'go' || st.paused) return;
    const due = st.pending.length > 0 || sim.S !== st.sentS;
    if (!due || (now - st.sentAt < BATCH_MS && sim.State === NamystoSim.Playing)) return;
    st.sentAt = now;
    st.sentS = sim.S;
    const p = { s: sim.S, ep: st.ep, e: st.pending };
    st.pending = [];
    // «Намисто дня» береже спробу на сервері після кожної дії, тож раз на 10 с пачка йде дією, а не вводом
    if (viewOf(st).mode === 'daily' && now - st.savedAt > SAVE_MS) { st.savedAt = now; st.ctx.tell('j', p); }
    else st.ctx.input('j', p);
  }

  function pause(st, quiet) {
    const sim = st.sim;
    if (!sim || st.paused || phaseOf(st) !== 'go' || sim.State !== NamystoSim.Playing || !st.ctx.mine) return;
    st.paused = true;
    const p = { s: sim.S, ep: st.ep, e: st.pending };
    st.pending = [];
    st.sentS = sim.S;
    st.ctx.tell('pause', p);
    if (!quiet) Snd.click();
    refresh(st);
  }

  async function resume(st) {
    if (!st.paused || !st.sim) return;
    Snd.unlock();
    const r = await st.ctx.tell('resume');
    if (!r || !r.ok) { if (r) st.ctx.toast(r.message || 'Не вийшло'); return; }
    // сервер міг перезібрати (епоха зросла) — тоді вид прийде сам і rebuild поставить годинник
    st.paused = false;
    st.anchorStep = st.sim.S;
    st.anchorAt = performance.now();
    refresh(st);
  }

  function onOverlay(st, e) {
    const b = e.target.closest('[data-do]');
    if (!b) return;
    const ctx = st.ctx, id = ctx.room && ctx.room.id;
    switch (b.dataset.do) {
      case 'go': go(st); break;
      case 'resume': resume(st); break;
      case 'pick': ctx.act('pick'); break;
      case 'again': HGames.call('Rematch', id); break;
      case 'next': ctx.act('next', { n: +b.dataset.n }).then((r) => { if (r && r.ok) HGames.call('Rematch', id); }); break;
      case 'share': {
        const txt = (viewOf(st).daily || {}).share || '';
        const ok = () => ctx.toast('Скопійовано — кидай друзям');
        try { navigator.clipboard.writeText(txt).then(ok, () => ctx.toast(txt)); } catch { ctx.toast(txt); }
        break;
      }
    }
  }

  // ---------- цикл ----------

  function wake(st) {
    if (st.raf || st.tm) return;
    st.raf = requestAnimationFrame(() => loop(st));
  }

  function loop(st) {
    st.raf = 0;
    if (!st.el || !st.el.isConnected || st.root._nm !== st) return;
    const ph = phaseOf(st);
    if (ph === 'pick') return;   // список трас — DOM, полотна не видно
    // картка під display:none чи вкладка схована — дрімаємо таймером
    if (!st.el.offsetParent || document.hidden) { st.tm = setTimeout(() => { st.tm = 0; loop(st); }, 400); return; }
    const now = performance.now();
    const active = ph === 'go' && st.sim && !st.paused && st.sim.State === NamystoSim.Playing;
    // готовність, пауза й підсумок — 20 кадрів на секунду вистачить (лише ефекти), сама гра — кожен кадр
    const calm = !active && st.fx.length === 0 && st.texts.length === 0 && !st.drain && !st.won;
    if (calm && now - st.lastDraw < 50) { st.raf = requestAnimationFrame(() => loop(st)); return; }
    st.raf = requestAnimationFrame(() => loop(st));
    const alpha = advance(st, now);
    st.lastDraw = now;
    draw(st, now, alpha);
    paintHud(st, false);
  }

  /// Крок гри: докотити свою копію до годинника, ефекти, пачка журналу. Повертає частку кроку для малювання.
  function advance(st, now) {
    const sim = st.sim;
    const active = phaseOf(st) === 'go' && sim && !st.paused && sim.State === NamystoSim.Playing;
    let alpha = 1;
    if (active) {
      steer(st, now);
      const target = st.anchorStep + Math.floor((now - st.anchorAt) / STEP_MS);
      let n = 0;
      while (sim.S < target && sim.State === NamystoSim.Playing && n < 240) { sim.step(); n++; }
      if (sim.S < target - 240) { st.anchorStep = sim.S; st.anchorAt = now; }   // не встигаємо — краще повільніше, ніж ривками
      alpha = clamp((now - st.anchorAt) / STEP_MS - (sim.S - 1 - st.anchorStep), 0, 1);
      drainEvents(st);
      if (sim.State !== NamystoSim.Playing) ended(st, now);
    } else if (sim && sim.events.length) drainEvents(st);
    if (phaseOf(st) === 'go' && sim) batch(st, now);
    return alpha;
  }

  /// Запасний насос: сторінку не сховано, а кадрів нема (вкладка у фоні вікна, слабкий телефон) — гра однаково йде в
  /// ногу з годинником і шле журнал; інакше сервер через 3 с котив би сам і просив перезбиратись.
  function pump(st) {
    if (!st.el || !st.el.isConnected || st.root._nm !== st) return;
    const now = performance.now();
    if (now - st.lastDraw < 300 || document.hidden) return;
    advance(st, now);
  }

  function ended(st, now) {
    if (st.ended) return;
    st.ended = true;
    if (st.sim.State === NamystoSim.Lost) { st.drain = now; Snd.lose(); }
    else { st.won = now; Snd.win(); burst(st); }
    refresh(st);
  }

  /// Стрілки (і стік через pad.js) крутять приціл; аналоговий стік — показує напрям прямо.
  function steer(st, now) {
    const dt = Math.min(0.05, (now - (st.steerAt || now)) / 1000);
    st.steerAt = now;
    let turned = false;
    if (now - st.padAt < 60000 && window.HPad && HPad.list) {
      let pads = [];
      try { pads = HPad.list() || []; } catch { pads = []; }
      for (const gp of pads) {
        if (!gp || !gp.axes || gp.axes.length < 2) continue;
        let ax = gp.axes[0], ay = gp.axes[1];
        if (gp.axes.length >= 4 && Math.hypot(gp.axes[2], gp.axes[3]) > Math.hypot(ax, ay)) { ax = gp.axes[2]; ay = gp.axes[3]; }
        if (Math.hypot(ax, ay) > 0.4) {
          st.aim.a = Math.atan2(ay, ax);
          st.aim.mode = 'pad'; st.aim.on = true; st.analogAt = now; turned = true;
        }
        break;
      }
    }
    if (!turned) {
      const d = (st.held.r ? 1 : 0) - (st.held.l ? 1 : 0);
      if (d) {
        st.heldFor = (st.heldFor || 0) + dt;
        const speed = 1.4 + Math.min(2.6, st.heldFor * 3);   // рад/с: спершу тонко, затиснув — швидше
        st.aim.a += d * speed * dt;
        st.aim.mode = 'keys'; st.aim.on = true;
      } else st.heldFor = 0;
    }
  }

  // ---------- події симуляції → ефекти й звук ----------

  function drainEvents(st) {
    const sim = st.sim;
    const ev = sim.events;
    if (!ev.length) return;
    const t = sim.Track, p = [0, 0];
    for (const e of ev) {
      if (e.t === 'pop') {
        let cx = 0, cy = 0;
        for (const b of e.beads) {
          t.at(b.D, p);
          const x = p[0] / PX, y = p[1] / PX;
          cx += x; cy += y;
          sparks(st, x, y, BEADS[b.C].c, 5);
        }
        cx /= e.beads.length; cy /= e.beads.length;
        let label = '+' + e.pts;
        if (e.combo >= 2) label += ' · ланцюжок ×' + e.combo;
        else if (e.gaps) label += ' · у щілину!';
        else if (e.streak >= 3) label += ' · серія ' + e.streak;
        say(st, cx, cy - 18, label, e.combo >= 2 ? '#ffe066' : '#ffffff', e.combo >= 2 ? 1.25 : 1);
        Snd.pop(e.combo, e.beads.length);
        if (window.HPad && HPad.rumble && st.aim.mode === 'pad') HPad.rumble(60 + e.combo * 30, 0.2 + e.combo * 0.12, 0.4);
      } else if (e.t === 'bomb') {
        st.fx.push({ k: 'ring', x: e.x / PX, y: e.y / PX, at: performance.now(), c: '#ff7a1a' });
        for (const b of e.beads) { t.at(b.D, p); sparks(st, p[0] / PX, p[1] / PX, '#ff9a3c', 4); }
        st.shake = performance.now();
        Snd.boom();
      } else if (e.t === 'bonus') {
        const b = BONUS[e.k];
        say(st, t.Sx / PX, t.Sy / PX - 64, b.icon + ' ' + b.say, '#fff3b0', 1.2);
        Snd.bonus();
      } else if (e.t === 'hit') {
        Snd.tick();
      } else if (e.t === 'miss') {
        // промах — тихо: і так видно
      }
    }
    ev.length = 0;
    if (st.fx.length > 160) st.fx.splice(0, st.fx.length - 160);
  }

  function sparks(st, x, y, c, n) {
    const now = performance.now();
    for (let i = 0; i < n; i++) {
      const a = Math.random() * Math.PI * 2, v = 60 + Math.random() * 140;
      st.fx.push({ k: 'dot', x, y, vx: Math.cos(a) * v, vy: Math.sin(a) * v, at: now, c, r: 2 + Math.random() * 3 });
    }
  }

  function say(st, x, y, text, c, scale) {
    st.texts.push({ x: clamp(x, 80, W - 80), y: clamp(y, 30, W - 20), text, c, at: performance.now(), s: scale || 1 });
    if (st.texts.length > 8) st.texts.shift();
  }

  function burst(st) {
    const t = st.sim.Track;
    for (let i = 0; i < 6; i++) sparks(st, t.Sx / PX, t.Sy / PX, BEADS[i].c, 8);
  }

  function pulse(st) { st.kick = performance.now(); }

  // ---------- малювання ----------

  function sprites(st) {
    const key = st.k.toFixed(3);
    if (st.spr && st.sprKey === key) return st.spr;
    const k = st.k;
    const size = Math.ceil((2 * R + 4) * k);
    const make = (fn) => {
      const c = document.createElement('canvas');
      c.width = c.height = size;
      const g = c.getContext('2d');
      g.setTransform(k, 0, 0, k, 0, 0);
      g.translate(R + 2, R + 2);
      fn(g);
      return c;
    };
    const bead = BEADS.map((b) => make((g) => {
      const gr = g.createRadialGradient(-R * 0.35, -R * 0.4, R * 0.1, 0, 0, R);
      gr.addColorStop(0, b.hi);
      gr.addColorStop(0.45, b.c);
      gr.addColorStop(1, b.lo);
      g.fillStyle = gr;
      g.beginPath(); g.arc(0, 0, R - 0.5, 0, Math.PI * 2); g.fill();
      g.strokeStyle = 'rgba(0,0,0,.28)'; g.lineWidth = 1;
      g.stroke();
      // візерунок — тонко, щоб не заважав кольору
      g.fillStyle = g.strokeStyle = b.mark === 'none' ? 'rgba(0,0,0,0)' : (b.c === '#ebe4d6' ? 'rgba(90,70,40,.5)' : 'rgba(255,255,255,.55)');
      g.lineWidth = 2;
      switch (b.mark) {
        case 'dot': g.beginPath(); g.arc(2, 3, 3.2, 0, Math.PI * 2); g.fill(); break;
        case 'ring': g.beginPath(); g.arc(2, 3, 5, 0, Math.PI * 2); g.stroke(); break;
        case 'cross': g.beginPath(); g.moveTo(-2, 0); g.lineTo(6, 8); g.moveTo(6, 0); g.lineTo(-2, 8); g.stroke(); break;
        case 'bar': g.fillRect(0, -3, 4, 13); break;
        case 'two': g.beginPath(); g.arc(-1, 4, 2.4, 0, Math.PI * 2); g.arc(6, 4, 2.4, 0, Math.PI * 2); g.fill(); break;
      }
      // блік
      g.fillStyle = 'rgba(255,255,255,.75)';
      g.beginPath(); g.ellipse(-R * 0.38, -R * 0.45, R * 0.28, R * 0.18, -0.6, 0, Math.PI * 2); g.fill();
      // дірочка під нитку
      g.fillStyle = 'rgba(0,0,0,.35)';
      g.beginPath(); g.arc(0, 0, 1.6, 0, Math.PI * 2); g.fill();
    }));
    const bonus = {};
    for (const kd of [1, 2, 3, 4]) bonus[kd] = make((g) => {
      g.font = '18px system-ui, "Segoe UI Emoji", "Apple Color Emoji", sans-serif';
      g.textAlign = 'center'; g.textBaseline = 'middle';
      g.fillStyle = 'rgba(255,255,255,.85)';
      g.beginPath(); g.arc(0, 0, R * 0.78, 0, Math.PI * 2); g.fill();
      g.fillText(BONUS[kd].icon, 0, 1);
    });
    st.spr = { bead, bonus, size, half: (R + 2) };
    st.sprKey = key;
    return st.spr;
  }

  function glekSprite(st) {
    const key = st.k.toFixed(3);
    if (st.glek && st.glek.key === key) return st.glek;
    if (!glekImg.complete || !glekImg.naturalWidth) return null;
    const S = 96, c = document.createElement('canvas');
    c.width = c.height = Math.ceil(S * st.k);
    const g = c.getContext('2d');
    g.drawImage(glekImg, 0, 0, c.width, c.height);
    st.glek = { c, S, key };
    return st.glek;
  }
  const glekImg = new Image();
  glekImg.src = '/static/glek.svg';
  glekImg.onload = () => { for (const st of live) { st.glek = null; wake(st); } };

  /// Тло місця, жолоб, нитка й криниця — раз на трасу й розмір.
  function background(st) {
    const lv = lvOf(st), t = track(st);
    const key = (lv ? lv.n + ':' + lv.place : '-') + ':' + st.k.toFixed(3) + ':' + (t ? 1 : 0);
    if (st.bg && st.bgKey === key) return st.bg;
    const c = st.bg || document.createElement('canvas');
    c.width = c.height = st.el.width;
    const g = c.getContext('2d');
    g.setTransform(st.k, 0, 0, st.k, 0, 0);
    const pl = PLACES[(lv && lv.place) || 0] || PLACES[0];
    const gr = g.createLinearGradient(0, 0, 0, W);
    gr.addColorStop(0, pl.top); gr.addColorStop(1, pl.bot);
    g.fillStyle = gr;
    g.fillRect(0, 0, W, W);
    ornament(g, (lv && lv.place) || 0, pl);
    if (t) {
      const path = () => {
        g.beginPath();
        g.moveTo(t.X[0] / PX, t.Y[0] / PX);
        for (let i = 1; i < t.Count; i++) g.lineTo(t.X[i] / PX, t.Y[i] / PX);
      };
      g.lineJoin = g.lineCap = 'round';
      path(); g.strokeStyle = 'rgba(0,0,0,.22)'; g.lineWidth = 44; g.stroke();
      path(); g.strokeStyle = pl.groove; g.lineWidth = 38; g.stroke();
      path(); g.strokeStyle = pl.grooveHi; g.lineWidth = 30; g.stroke();
      path(); g.strokeStyle = pl.groove; g.globalAlpha = 0.55; g.lineWidth = 22; g.stroke(); g.globalAlpha = 1;
      path(); g.strokeStyle = pl.thread; g.lineWidth = 1.6; g.setLineDash([7, 5]); g.stroke(); g.setLineDash([]);
      well(g, t.X[t.Count - 1] / PX, t.Y[t.Count - 1] / PX);
    }
    st.bg = c;
    st.bgKey = key;
    return c;
  }

  function ornament(g, place, pl) {
    const rnd = mulberry(place * 7919 + 13);
    g.save();
    if (place === 0) {
      // вишиванка по краях: ромбики червоним і чорним
      for (const y of [14, W - 14]) for (let x = 12; x < W; x += 24) {
        g.fillStyle = (x / 24) % 2 < 1 ? pl.orn : pl.orn2;
        g.beginPath(); g.moveTo(x, y - 7); g.lineTo(x + 7, y); g.lineTo(x, y + 7); g.lineTo(x - 7, y); g.closePath(); g.fill();
      }
    } else if (place === 1) {
      // квіточки на леваді
      for (let i = 0; i < 70; i++) {
        const x = rnd() * W, y = rnd() * W;
        g.fillStyle = i % 3 ? pl.orn : pl.orn2;
        g.globalAlpha = 0.5;
        for (let k = 0; k < 5; k++) { g.beginPath(); g.arc(x + Math.cos(k * 1.26) * 3, y + Math.sin(k * 1.26) * 3, 2, 0, 7); g.fill(); }
      }
    } else if (place === 2) {
      // стрічки ярмарку
      g.globalAlpha = 0.18;
      for (let i = -W; i < W * 2; i += 46) {
        g.fillStyle = (i / 46) % 2 ? pl.orn : pl.orn2;
        g.beginPath(); g.moveTo(i, 0); g.lineTo(i + 18, 0); g.lineTo(i + 18 - W * 0.5, W); g.lineTo(i - W * 0.5, W); g.closePath(); g.fill();
      }
    } else {
      // зорі й місяць Купальської ночі
      for (let i = 0; i < 110; i++) {
        g.fillStyle = i % 4 ? pl.orn : pl.orn2;
        g.globalAlpha = 0.25 + rnd() * 0.6;
        g.beginPath(); g.arc(rnd() * W, rnd() * W, rnd() * 1.6 + 0.4, 0, 7); g.fill();
      }
      g.globalAlpha = 0.9;
      g.fillStyle = '#fff6c8';
      g.beginPath(); g.arc(640, 70, 26, 0, 7); g.fill();
      g.fillStyle = pl.top;
      g.beginPath(); g.arc(652, 62, 24, 0, 7); g.fill();
    }
    g.restore();
  }

  function well(g, x, y) {
    g.save();
    const hole = g.createRadialGradient(x, y, 4, x, y, 26);
    hole.addColorStop(0, '#02060c'); hole.addColorStop(0.7, '#0e2a3e'); hole.addColorStop(1, '#244c66');
    g.fillStyle = hole;
    g.beginPath(); g.arc(x, y, 26, 0, 7); g.fill();
    for (let i = 0; i < 12; i++) {
      const a = i * Math.PI / 6;
      g.fillStyle = i % 2 ? '#8d8a82' : '#a9a59b';
      g.beginPath(); g.ellipse(x + Math.cos(a) * 30, y + Math.sin(a) * 30, 8, 6, a, 0, 7); g.fill();
      g.strokeStyle = 'rgba(0,0,0,.3)'; g.lineWidth = 1; g.stroke();
    }
    g.restore();
  }

  function mulberry(seed) {
    let s = seed >>> 0;
    return () => {
      s = (s + 0x6D2B79F5) >>> 0;
      let t = s;
      t = Math.imul(t ^ (t >>> 15), t | 1);
      t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  function draw(st, now, alpha) {
    const g = st.g, k = st.k;
    g.setTransform(1, 0, 0, 1, 0, 0);
    const bg = background(st);
    let shake = 0;
    if (st.shake && now - st.shake < 300) shake = (1 - (now - st.shake) / 300) * 6;
    g.drawImage(bg, shake ? (Math.random() - 0.5) * shake * k : 0, shake ? (Math.random() - 0.5) * shake * k : 0);
    g.setTransform(k, 0, 0, k, 0, 0);
    const t = track(st), sim = st.sim, ph = phaseOf(st);
    if (!t) { loadingText(g); return; }
    const showChain = sim && (ph === 'go' || ph === 'over') && !(st.paused && ph === 'go');
    if (showChain) {
      drawDanger(st, g, now, t);
      drawChain(st, g, now, alpha, t);
      drawShots(st, g, alpha);
    }
    drawGlek(st, g, now, t, showChain || ph === 'ready');
    drawFx(st, g, now);
  }

  function loadingText(g) {
    g.fillStyle = 'rgba(0,0,0,.45)';
    g.font = '600 22px system-ui, sans-serif';
    g.textAlign = 'center';
    g.fillText('Розкладаю намисто…', W / 2, W / 2);
  }

  function drawDanger(st, g, now, t) {
    const sim = st.sim;
    if (!sim.Beads.length) return;
    const left = sim.End - sim.Beads[0].D;
    const near = 8 * NamystoSim.Dm;
    if (left > near || sim.State === NamystoSim.Won) return;
    const x = t.X[t.Count - 1] / PX, y = t.Y[t.Count - 1] / PX;
    const u = 1 - left / near;
    const beat = 0.5 + 0.5 * Math.sin(now / (160 - u * 80));
    g.save();
    g.globalAlpha = 0.25 + 0.45 * u * beat;
    const gr = g.createRadialGradient(x, y, 20, x, y, 90 + 40 * u);
    gr.addColorStop(0, 'rgba(255,40,30,.9)'); gr.addColorStop(1, 'rgba(255,40,30,0)');
    g.fillStyle = gr;
    g.beginPath(); g.arc(x, y, 130, 0, 7); g.fill();
    g.restore();
    if (sim.State === NamystoSim.Playing && now - st.dangerAt > 900 - u * 500) { st.dangerAt = now; Snd.danger(u); }
  }

  function drawChain(st, g, now, alpha, t) {
    const sim = st.sim, spr = sprites(st), B = sim.Beads, p = [0, 0];
    const k = st.k, size = spr.size / k, half = spr.half;
    const drainAdd = st.drain ? Math.min(1, (now - st.drain) / 1400) : 0;
    const fade = st.drain ? 1 - drainAdd : 1;
    const insertSteps = 6;
    g.save();
    if (fade < 1) g.globalAlpha = Math.max(0, fade);
    for (let i = B.length - 1; i >= 0; i--) {
      const b = B[i];
      let d = b.pd + (b.D - b.pd) * alpha;
      if (drainAdd) d += drainAdd * 2600 * PX * drainAdd;
      if (d < 0) continue;
      t.at(Math.min(Math.round(d), sim.End), p);
      let x = p[0] / PX, y = p[1] / PX;
      if (b.fx != null && sim.S - b.at < insertSteps) {
        const u = clamp((sim.S - b.at - 1 + alpha) / insertSteps, 0, 1);
        x = b.fx / PX + (x - b.fx / PX) * u;
        y = b.fy / PX + (y - b.fy / PX) * u;
      }
      g.drawImage(spr.bead[b.C], x - half, y - half, size, size);
      if (b.Bonus) {
        const blink = b.BonusLeft < 180 ? (Math.floor(now / 140) % 2 ? 0.35 : 1) : 1;
        const s = 0.8 + 0.08 * Math.sin(now / 160);
        g.globalAlpha = blink * fade;
        g.drawImage(spr.bonus[b.Bonus], x - half * s, y - half * s, size * s, size * s);
        g.globalAlpha = fade;
      }
    }
    g.restore();
  }

  function drawShots(st, g, alpha) {
    const sim = st.sim, spr = sprites(st), half = spr.half, size = spr.size / st.k;
    for (const s of sim.Shots) {
      const x = (s.px + (s.X - s.px) * alpha) / PX, y = (s.py + (s.Y - s.py) * alpha) / PX;
      g.drawImage(spr.bead[s.C], x - half, y - half, size, size);
    }
  }

  function drawGlek(st, g, now, t, alive) {
    const sim = st.sim;
    const sx = t.Sx / PX, sy = t.Sy / PX;
    const a = st.aim.a;
    const kick = st.kick && now - st.kick < 120 ? (1 - (now - st.kick) / 120) * 5 : 0;
    // приціл: коротка пунктирна стежка; з «Оком» — промінь до першої намистини
    if (alive && sim && st.ctx.mine && !st.paused && sim.State === NamystoSim.Playing) {
      const eye = sim.EyeLeft > 0;
      const len = eye ? ray(st, sx, sy, a) : (coarse() && st.touch ? 220 : 90);
      g.save();
      g.strokeStyle = eye ? 'rgba(255,240,140,.85)' : 'rgba(255,255,255,.55)';
      g.lineWidth = eye ? 3 : 2;
      g.setLineDash(eye ? [] : [6, 8]);
      g.beginPath();
      g.moveTo(sx + Math.cos(a) * 46, sy + Math.sin(a) * 46);
      g.lineTo(sx + Math.cos(a) * len, sy + Math.sin(a) * len);
      g.stroke();
      g.restore();
    }
    const gl = glekSprite(st);
    g.save();
    g.translate(sx - Math.cos(a) * kick, sy - Math.sin(a) * kick);
    g.rotate(a + Math.PI / 2);
    if (gl) g.drawImage(gl.c, -gl.S / 2, -gl.S / 2 - 6, gl.S, gl.S);
    else { g.fillStyle = '#c5763a'; g.beginPath(); g.arc(0, 0, 36, 0, 7); g.fill(); }
    g.restore();
    if (!sim) return;
    const spr = sprites(st), half = spr.half, size = spr.size / st.k;
    // поточна — в горлечку, наступна — на пузі, менша
    const mx = sx + Math.cos(a) * 34, my = sy + Math.sin(a) * 34;
    if (sim.State === NamystoSim.Playing) g.drawImage(spr.bead[sim.Cur], mx - half, my - half, size, size);
    const nx = sx - Math.cos(a) * 12, ny = sy - Math.sin(a) * 12;
    g.drawImage(spr.bead[sim.Next], nx - half * 0.55, ny - half * 0.55, size * 0.55, size * 0.55);
  }

  /// Скільки пікселів до першої намистини на промені (для «Ока»): кроком 6 px, не далі 900.
  function ray(st, sx, sy, a) {
    const sim = st.sim, t = sim.Track, B = sim.Beads, p = [0, 0];
    const pts = [];
    for (let i = 0; i < B.length; i++) {
      if (B[i].D < NamystoSim.Dm / 2 || B[i].D >= sim.End) continue;
      t.at(B[i].D, p);
      pts.push(p[0] / PX, p[1] / PX);
    }
    const cx = Math.cos(a), cy = Math.sin(a);
    for (let d = 46; d < 900; d += 6) {
      const x = sx + cx * d, y = sy + cy * d;
      for (let i = 0; i < pts.length; i += 2) {
        const dx = pts[i] - x, dy = pts[i + 1] - y;
        if (dx * dx + dy * dy < 4 * R * R) return d;
      }
    }
    return 900;
  }

  function drawFx(st, g, now) {
    const fx = st.fx;
    let j = 0;
    for (let i = 0; i < fx.length; i++) {
      const f = fx[i];
      const age = (now - f.at) / 1000;
      if (f.k === 'dot') {
        if (age > 0.6) continue;
        g.globalAlpha = 1 - age / 0.6;
        g.fillStyle = f.c;
        g.beginPath(); g.arc(f.x + f.vx * age, f.y + f.vy * age + 120 * age * age, f.r * (1 - age), 0, 7); g.fill();
      } else if (f.k === 'ring') {
        if (age > 0.45) continue;
        g.globalAlpha = 1 - age / 0.45;
        g.strokeStyle = f.c; g.lineWidth = 6;
        g.beginPath(); g.arc(f.x, f.y, 20 + age * 260, 0, 7); g.stroke();
      }
      fx[j++] = f;
    }
    fx.length = j;
    g.globalAlpha = 1;
    const tx = st.texts;
    let m = 0;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    for (let i = 0; i < tx.length; i++) {
      const t = tx[i];
      const age = (now - t.at) / 1000;
      if (age > 1.2) continue;
      g.globalAlpha = age < 0.9 ? 1 : 1 - (age - 0.9) / 0.3;
      g.font = '800 ' + Math.round(20 * t.s) + 'px system-ui, "Segoe UI", sans-serif';
      g.lineWidth = 4; g.strokeStyle = 'rgba(0,0,0,.55)';
      g.strokeText(t.text, t.x, t.y - age * 40);
      g.fillStyle = t.c;
      g.fillText(t.text, t.x, t.y - age * 40);
      tx[m++] = t;
    }
    tx.length = m;
    g.globalAlpha = 1;
  }

  // ---------- шапка, накладки, список трас ----------

  function set(el, txt) { if (el.textContent !== txt) el.textContent = txt; }

  function paintHud(st, force) {
    const now = performance.now();
    if (!force && now - st.hudAt < 100) return;
    st.hudAt = now;
    const v = viewOf(st), lv = v.lv, sim = st.sim, ph = v.phase;
    const kids = st.hud.children;
    const daily = v.mode === 'daily';
    const name = lv ? '«' + lv.name + '»' : '';
    set(kids[0], ph === 'pick' ? '📿 Обери трасу' : daily ? '☀ №' + ((v.daily && v.daily.no) || '') + ' ' + name : name + (lv ? ' · ' + lv.n + '/12' : ''));
    const showSim = sim && (ph === 'go' || ph === 'over');
    set(kids[1], showSim ? num(sim.Score) : '');
    set(kids[2], showSim ? '📿 ' + (sim.ToSpawn + sim.Beads.length) : lv && ph === 'ready' ? '📿 ' + lv.beads : '');
    let bon = '';
    if (showSim && ph === 'go') {
      if (sim.SlowLeft > 0) bon += '⏳' + Math.ceil(sim.SlowLeft / 60) + ' ';
      if (sim.BackLeft > 0) bon += '⏪' + Math.ceil(sim.BackLeft / 60) + ' ';
      if (sim.EyeLeft > 0) bon += '🎯' + Math.ceil(sim.EyeLeft / 60);
    }
    set(kids[3], bon.trim());
    set(kids[4], Snd.on ? '🔊' : '🔇');
    const pb = kids[5];
    pb.hidden = !(ph === 'go' && st.ctx.mine && sim && sim.State === NamystoSim.Playing);
    set(pb, st.paused ? '▶' : '⏸');
    st.el.classList.toggle('play', ph === 'go' && !!st.ctx.mine && !st.paused);
    const help = coarse()
      ? 'Веди пальцем — видно приціл, відпустив — постріл. Дотик до Глека — поміняти намистини'
      : 'Миша — приціл, клац — постріл, правий клік чи пробіл — поміняти. Esc — пауза';
    set(st.help, ph === 'go' || ph === 'ready' ? help : '');
  }

  function paintOverlay(st) {
    const v = viewOf(st), ph = v.phase, lv = v.lv, ctx = st.ctx, sim = st.sim;
    st.stage.hidden = ph === 'pick';
    let key = '', html = '';
    if (!ctx.mine) { key = 'watch'; html = ''; }
    else if (ph === 'ready' && lv) {
      key = 'ready:' + lv.n + ':' + v.mode + ':' + (TR ? 1 : 0);
      const pl = PLACES[lv.place] || PLACES[0];
      html = '<div class="nm-card-ov"><div class="nm-kicker">' + (v.mode === 'daily'
        ? '☀ Намисто дня №' + esc((v.daily && v.daily.no) || '') + ' · одна спроба'
        : pl.icon + ' ' + esc(pl.name) + ' · траса ' + lv.n + ' з 12') + '</div>'
        + '<h3>«' + esc(lv.name) + '»</h3>'
        + '<p>' + lv.beads + ' ' + plural(lv.beads, 'намистина', 'намистини', 'намистин') + ' · ' + lv.colors + ' ' + plural(lv.colors, 'колір', 'кольори', 'кольорів')
        + ' · норма ' + Math.floor(lv.par / 60) + ':' + String(lv.par % 60).padStart(2, '0') + '</p>'
        + '<p class="muted small">Збий три однакові впритул. Не дай разкові докотитись до криниці.</p>'
        + '<div class="nm-btns"><button type="button" class="primary" data-do="go">▶ Котити!</button>'
        + (v.mode === 'daily' ? '' : '<button type="button" class="ghost" data-do="pick">☰ Траси</button>') + '</div></div>';
    } else if (ph === 'go' && st.paused) {
      key = 'pause';
      html = '<div class="nm-card-ov"><h3>⏸ Пауза</h3><p class="muted small">Поки пауза — разок сховано і він стоїть.</p>'
        + '<div class="nm-btns"><button type="button" class="primary" data-do="resume">▶ Далі</button></div></div>';
    } else if (ph === 'go' && sim && sim.State !== NamystoSim.Playing) {
      key = 'local:' + sim.State;
      html = '<div class="nm-card-ov soft"><h3>' + (sim.State === NamystoSim.Won ? '📿 Разок зібрано!' : '💧 Намисто впало в криницю') + '</h3>'
        + '<p>' + ochok(sim.Score) + '</p><p class="muted small">Глек рахує…</p></div>';
    } else if (ph === 'over') {
      const r = v.result || {};
      key = 'over:' + JSON.stringify([r, v.daily && v.daily.board, v.daily && v.daily.place, lv && lv.n, (v.levels || []).filter((x) => x.open).length]);
      html = overHtml(v, r, lv, (ctx.me && ctx.me.nick) || '');
    }
    if (key === st.ovKey) return;
    st.ovKey = key;
    st.ov.innerHTML = html;
    st.ov.hidden = !html;
  }

  function overHtml(v, r, lv, myNick) {
    const won = !!r.won;
    let h = '<div class="nm-card-ov wide"><h3>' + (won ? '📿 Разок зібрано!' : '💧 Намисто впало в криницю') + '</h3>';
    if (r.score != null) {
      h += '<p class="nm-big">' + ochok(r.score) + '</p>';
      const bits = [];
      if (r.popped != null) bits.push('розсипано ' + r.popped);
      if (r.combo >= 2) bits.push('ланцюжок ×' + r.combo);
      if (r.clear) bits.push('за разок +' + num(r.clear));
      if (r.time) bits.push('за час +' + num(r.time));
      if (r.misses) bits.push('мимо ' + r.misses);
      if (bits.length) h += '<p class="muted small">' + esc(bits.join(' · ')) + '</p>';
    }
    if (v.mode === 'daily') {
      const d = v.daily || {};
      h += '<div class="nm-day"><b>☀ Намисто дня №' + esc(d.no || '') + '</b>' + (d.place ? ' · ' + d.place + '-е місце з ' + d.players : '');
      if (d.board && d.board.length) {
        h += '<ol>' + d.board.map((x) => '<li' + (myNick && x.nick === myNick ? ' class="me"' : '') + '><span>' + esc(x.nick) + (x.won ? ' 📿' : '')
          + '</span><b>' + num(x.points) + '</b></li>').join('') + '</ol>';
      }
      h += '<div class="muted small">Нова траса — завтра. Сьогодні в усіх ті самі намистини.</div></div>';
      if (d.share) h += '<div class="nm-btns"><button type="button" class="ghost" data-do="share">📋 Поділитись</button></div>';
      return h + '</div>';
    }
    if (r.first) h += '<p class="nm-gift">Траса пройдена вперше: +' + (r.shards || 0) + ' 🏺' + (lv && lv.n < 12 ? ', відкрито наступну' : '') + '</p>';
    else if (r.record) h += '<p class="nm-gift">🏆 Новий рекорд траси!</p>';
    else if (r.best) h += '<p class="muted small">Рекорд траси: ' + ochok(r.best) + '</p>';
    const open = (v.levels || []).filter((x) => x.open).length;
    const nextN = lv ? lv.n + 1 : 0;
    h += '<div class="nm-btns"><button type="button" class="primary" data-do="again">▶ Ще раз</button>';
    if (lv && nextN <= 12 && nextN <= open) h += '<button type="button" class="primary" data-do="next" data-n="' + nextN + '">⏭ Далі</button>';
    h += '<button type="button" class="ghost" data-do="next" data-n="0">☰ Траси</button></div>';
    return h + '</div>';
  }

  function paintPick(st) {
    const v = viewOf(st);
    const on = v.phase === 'pick' && v.mode !== 'daily';
    st.pickEl.hidden = !on;
    if (!on) return;
    const levels = v.levels || [];
    const key = JSON.stringify(levels) + ':' + (TR ? TR.v : '');
    if (key === st.pickKey) return;
    st.pickKey = key;
    let h = '';
    for (let p = 0; p < 4; p++) {
      const pl = PLACES[p];
      h += '<section class="nm-place"><h4>' + pl.icon + ' ' + esc(pl.name) + '</h4><div class="nm-row">';
      for (let n = p * 3 + 1; n <= p * 3 + 3; n++) {
        const l = levels[n - 1] || { n, open: n === 1 };
        const meta = TR && TR.byN.get(n) ? TR.byN.get(n).meta : null;
        const top = (l.top || [])[0];
        h += '<button type="button" class="nm-tcard' + (l.open ? '' : ' locked') + (l.cleared ? ' done' : '') + '" data-n="' + n + '"' + (l.open ? '' : ' disabled')
          + ' title="' + (l.open ? 'Котити цю трасу' : 'Відкриється, коли пройдеш попередню') + '">'
          + '<canvas width="96" height="96" data-thumb="' + n + '"></canvas>'
          + '<b>' + n + ' · ' + esc(meta ? meta.name : '…') + '</b>'
          + '<span class="muted small">' + (l.open ? (l.best != null ? (l.cleared ? '✓ ' : '') + 'рекорд ' + num(l.best) : 'ще не грано') : '🔒 закрито') + '</span>'
          + (top ? '<span class="muted small nm-top1">🥇 ' + esc(top.nick) + ' · ' + num(top.best) + '</span>' : '')
          + '</button>';
      }
      h += '</div></section>';
    }
    st.pickEl.innerHTML = h;
    thumbs(st);
  }

  function thumbs(st) {
    if (!TR) return;
    st.pickEl.querySelectorAll('canvas[data-thumb]').forEach((c) => {
      const n = +c.dataset.thumb, tr = TR.byN.get(n);
      if (!tr) return;
      const dpr = Math.min(2, window.devicePixelRatio || 1);
      c.width = c.height = 96 * dpr;
      const g = c.getContext('2d');
      const s = 96 * dpr / W;
      g.setTransform(s, 0, 0, s, 0, 0);
      const pl = PLACES[tr.meta.place] || PLACES[0];
      const gr = g.createLinearGradient(0, 0, 0, W);
      gr.addColorStop(0, pl.top); gr.addColorStop(1, pl.bot);
      g.fillStyle = gr; g.fillRect(0, 0, W, W);
      const t = tr.track;
      g.beginPath();
      g.moveTo(t.X[0] / PX, t.Y[0] / PX);
      for (let i = 1; i < t.Count; i++) g.lineTo(t.X[i] / PX, t.Y[i] / PX);
      g.lineJoin = g.lineCap = 'round';
      g.strokeStyle = pl.groove; g.lineWidth = 34; g.stroke();
      g.strokeStyle = pl.grooveHi; g.lineWidth = 20; g.stroke();
      g.fillStyle = '#0e2a3e';
      g.beginPath(); g.arc(t.X[t.Count - 1] / PX, t.Y[t.Count - 1] / PX, 30, 0, 7); g.fill();
      g.fillStyle = '#c5763a';
      g.beginPath(); g.arc(t.Sx / PX, t.Sy / PX, 36, 0, 7); g.fill();
    });
  }

  // ---------- звук (WebAudio, типово вимкнено) ----------

  const Snd = (() => {
    let ac = null, on = false;
    try { on = localStorage.getItem('nm-sound') === '1'; } catch { on = false; }
    const ctx = () => {
      if (!on) return null;
      if (!ac) { try { ac = new (window.AudioContext || window.webkitAudioContext)(); } catch { ac = null; } }
      if (ac && ac.state === 'suspended') ac.resume().catch(() => {});
      return ac;
    };
    function tone(f, dur, type, vol, slide, delay) {
      const a = ctx();
      if (!a) return;
      const t0 = a.currentTime + (delay || 0);
      const o = a.createOscillator(), g = a.createGain();
      o.type = type || 'sine';
      o.frequency.setValueAtTime(f, t0);
      if (slide) o.frequency.exponentialRampToValueAtTime(Math.max(40, f * slide), t0 + dur);
      g.gain.setValueAtTime(0.0001, t0);
      g.gain.exponentialRampToValueAtTime(vol || 0.12, t0 + 0.01);
      g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
      o.connect(g); g.connect(a.destination);
      o.start(t0); o.stop(t0 + dur + 0.02);
    }
    return {
      get on() { return on; },
      toggle() {
        on = !on;
        try { localStorage.setItem('nm-sound', on ? '1' : '0'); } catch { /* приватне вікно */ }
        if (on) { ctx(); tone(660, 0.12, 'sine', 0.08); }
      },
      unlock() { ctx(); },
      shot() { tone(220, 0.07, 'triangle', 0.1, 0.6); },
      swap() { tone(520, 0.05, 'square', 0.04); },
      tick() { tone(1400, 0.03, 'square', 0.03); },
      click() { tone(800, 0.04, 'sine', 0.05); },
      pop(combo, n) {
        const base = 760 * Math.pow(1.12, Math.min(6, (combo || 1) - 1));
        tone(base, 0.18, 'sine', 0.12, 1.5);
        tone(base * 1.5, 0.14, 'sine', 0.06, 1.3, 0.04);
        if (n > 3) tone(base * 2, 0.12, 'sine', 0.05, 1.2, 0.08);
      },
      boom() { tone(120, 0.35, 'sawtooth', 0.12, 0.3); },
      bonus() { [660, 880, 1100].forEach((f, i) => tone(f, 0.1, 'triangle', 0.07, 1, i * 0.07)); },
      danger(u) { tone(110 + u * 60, 0.12, 'sine', 0.05 + u * 0.06); },
      start() { [440, 554, 660].forEach((f, i) => tone(f, 0.12, 'triangle', 0.07, 1, i * 0.08)); },
      win() { [523, 659, 784, 1047].forEach((f, i) => tone(f, 0.22, 'triangle', 0.09, 1, i * 0.11)); },
      lose() { [392, 330, 262, 196].forEach((f, i) => tone(f, 0.26, 'sine', 0.08, 0.9, i * 0.13)); },
    };
  })();

  // ---------- пад ----------

  function padOn(btn, ctx) {
    const st = stOf(ctx);
    if (!st) return false;
    st.padAt = performance.now();
    const ph = phaseOf(st);
    if (ph === 'ready' && btn === 'a') { go(st); return true; }
    if (ph !== 'go') return false;
    if (btn === 'start' || btn === 'y') { st.paused ? resume(st) : pause(st); return true; }
    if (st.paused) { if (btn === 'a') { resume(st); return true; } return false; }
    if (btn === 'a' || btn === 'rt' || btn === 'rb') {
      if (st.aim.mode === 'mouse' || st.aim.mode === 'touch') st.aim.mode = 'pad';
      fire(st);
      return true;
    }
    if (btn === 'x' || btn === 'lt' || btn === 'lb') { swap(st); return true; }
    return false;
  }

  // ---------- реєстрація ----------

  const MOD = {
    added: '2026-10-20',
    arcade: true,
    pad: {
      dirs: true, rstick: true, on: padOn,
      hint: '{dpad} приціл (стік — куди дивиться Глек) · {a} постріл · {x} поміняти · {start} пауза',
      when: (ctx) => !!(ctx.mine && ctx.playing && ctx.view && (ctx.view.phase === 'go' || ctx.view.phase === 'ready')),
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      shell(root, st);
      loadTracks(ctx.view && ctx.view.tv).catch(() => {});
      refresh(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      shell(root, st);
      const v = ctx.view || {};
      if (!TR || TR.v !== v.tv) loadTracks(v.tv).catch(() => {});
      // інша партія (Ще раз) — своя копія вже не та
      if (v.phase !== 'go' && v.phase !== 'over' && st.sim) { st.sim = null; st.simKey = ''; st.paused = false; }
      refresh(st);
    },

    onKey(e, ctx) {
      const st = stOf(ctx);
      if (!st || !ctx.mine) return false;
      const ph = phaseOf(st);
      if (ph === 'ready' && (e.code === 'Space' || e.code === 'Enter')) { if (!e.repeat) go(st); return true; }
      if (ph !== 'go') return false;
      if (e.code === 'Escape' || e.code === 'KeyP') {
        if (e.repeat) return true;
        if (st.paused) resume(st); else pause(st);
        return true;
      }
      if (st.paused) return false;
      const k = dirKey(e);
      if (k) {
        if (e.hpad && performance.now() - st.analogAt < 400) return true;
        if (e.hpad) st.padAt = performance.now();
        st.held[k] = 1;
        return true;
      }
      if (e.code === 'ArrowUp' || e.code === 'ArrowDown') return true;
      if (e.code === 'Enter' || e.code === 'KeyF' || e.code === 'KeyW') { if (!e.repeat) fire(st); return true; }
      if (e.code === 'Space' || e.code === 'KeyS' || e.code === 'KeyX') {
        if (e.repeat) return true;
        // пробіл: з мишею — поміняти (стріляє клац), з клавіатурою — постріл
        if (e.code === 'Space' && st.aim.mode !== 'mouse') fire(st); else swap(st);
        return true;
      }
      return false;
    },

    status(ctx) {
      const v = ctx.view || {};
      if (!ctx.mine) return '';
      switch (v.phase) {
        case 'pick': return 'Обери трасу — наступна відкривається, коли пройдеш попередню';
        case 'ready': return v.mode === 'daily' ? 'Одна спроба на день — коли готовий, тисни «Котити!»' : 'Тисни «Котити!» — і разок покотиться';
        case 'go': return '';
        default: return '';
      }
    },

    unmount(root) {
      const st = root._nm;
      if (!st) return;
      pause(st, true);
      if (st.raf) cancelAnimationFrame(st.raf);
      clearTimeout(st.tm);
      clearInterval(st.pumpTm);
      st.raf = 0; st.tm = 0;
      if (st.ro) st.ro.disconnect();
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.blurFn) window.removeEventListener('blur', st.blurFn);
      if (st.vis) document.removeEventListener('visibilitychange', st.vis);
      live.delete(st);
      root._nm = null;
      root.classList.remove('namysto');
    },
  };
  HGames.register(Object.assign({}, MOD, { id: 'namysto', icon: ICON }));
  HGames.register(Object.assign({}, MOD, { id: 'namysto-daily', icon: ICON_DAY }));
})();
