/*
  «🚨 Тривога» — window.HTryvoga (21.10.2026, docs/tryvoga.md): плашка під шапкою, кнопка 🚨 у шапці й сторінка
  #tryvoga з мапою районів і цілей у небі.

  Чи тривога в моєму районі, вирішує сервер (/api/tryvoga → status): тут лише малюємо. Район — localStorage
  tryvogaRegion ({ key, oblast, name }), в акаунта ще й на сервері (з будь-якого пристрою). Поки не обрав — типовий
  із сервера (Житомир). Хаб шле лише «щось змінилось» (tryvoga) — тоді перепитуємо свій район.

  Дані — NEPTUN через наш сервер. Його умова й наша чесність: посилання на NEPTUN і «це не офіційне оповіщення»
  завжди поруч із мапою, а коли дані застаріли — кажемо «не знаю», а не «тихо».
*/
(() => {
  'use strict';

  const MAP_URL = '/static/tryvoga-map.json';
  const REGION_KEY = 'tryvogaRegion';
  const HIDE_KEY = 'tryvogaHidden';
  const THREATS_MS = 10000;
  const KMAX = 14;
  const EARTH_KM = 6371;

  // Типи цілей NEPTUN: як підписати й яким значком малювати. Кольори — у tryvoga.css (--tv-<тип>).
  const TYPES = {
    uav: { label: 'БпЛА', ico: '🛩', shape: 'delta' },
    fpv: { label: 'FPV', ico: '🛩', shape: 'delta' },
    missile: { label: 'Ракети', ico: '🚀', shape: 'dart' },
    ballistic: { label: 'Балістика', ico: '🚀', shape: 'diamond' },
    kab: { label: 'КАБ', ico: '💣', shape: 'dot' },
    mig31k: { label: 'Авіація', ico: '✈', shape: 'plane' },
    recon: { label: 'Розвідка', ico: '🛰', shape: 'ring' },
    unknown: { label: 'Невідомі', ico: '❔', shape: 'ring' },
  };
  const typeOf = (t) => TYPES[t] || TYPES.unknown;

  let o = null;              // { esc, go, api, me } з app.js
  let data = null;           // /api/tryvoga
  let region = null;         // свій район з localStorage
  let shown = false;
  let threats = null;        // /api/tryvoga/threats
  let threatTimer = 0;
  let lastRefresh = 0;
  let pingTimer = 0;
  let built = false;
  let pending = false;       // район щойно змінили, а сервер про нього ще не відповів — старого статусу не показуємо

  const $ = (id) => document.getElementById(id);
  const esc = (s) => (o ? o.esc(s) : String(s));

  // ---------------------------------------------------------------------------------------------
  // час і слова
  // ---------------------------------------------------------------------------------------------

  let clockFmt = null, dateFmt = null;
  function kyiv(t, withDate) {
    try {
      clockFmt = clockFmt || new Intl.DateTimeFormat('uk-UA', { timeZone: 'Europe/Kyiv', hour: '2-digit', minute: '2-digit' });
      dateFmt = dateFmt || new Intl.DateTimeFormat('uk-UA', { timeZone: 'Europe/Kyiv', day: '2-digit', month: '2-digit', year: 'numeric' });
      return (withDate ? dateFmt : clockFmt).format(t);
    } catch { return t.toLocaleTimeString(); }
  }
  function plural(n, one, few, many) {
    const a = Math.abs(n) % 100, b = a % 10;
    return n + ' ' + (a > 10 && a < 20 ? many : b === 1 ? one : b >= 2 && b <= 4 ? few : many);
  }
  /// Скільки триває: «12 хв», «2 год 5 хв», «3 дні».
  function lasts(ms) {
    const m = Math.max(0, Math.floor(ms / 60000));
    if (m < 60) return m + ' хв';
    const h = Math.floor(m / 60);
    if (h < 24) return h + ' год' + (m % 60 ? ' ' + (m % 60) + ' хв' : '');
    return plural(Math.floor(h / 24), 'день', 'дні', 'днів');
  }
  function ago(iso) {
    const t = Date.parse(iso || '');
    if (!t) return '';
    const m = Math.floor((Date.now() - t) / 60000);
    if (m < 1) return 'щойно';
    if (m < 60) return m + ' хв тому';
    return Math.floor(m / 60) + ' год тому';
  }
  /// «з 03:44», а тривога з минулого року — «з 04.04.2022».
  function sinceText(iso) {
    const t = Date.parse(iso || '');
    if (!t) return '';
    return 'з ' + kyiv(new Date(t), Date.now() - t > 20 * 3600 * 1000);
  }
  /// Що саме: з причин NEPTUN («Дронова загроза (жовтий рівень)»), без них — з рівня.
  function what(level, reasons) {
    const r = (reasons || []).join(' ');
    if (/ракет|балісти/i.test(r)) return 'ракетна загроза';
    if (/дрон|бпла|шахед/i.test(r)) return 'загроза дронів';
    return level === 'yellow' ? 'загроза дронів' : '';
  }

  // ---------------------------------------------------------------------------------------------
  // мій район
  // ---------------------------------------------------------------------------------------------

  const okRegion = (r) => r && typeof r.key === 'string' && typeof r.oblast === 'string' && typeof r.name === 'string' && r.key && r.oblast;
  function loadRegion() {
    try {
      const r = JSON.parse(localStorage.getItem(REGION_KEY) || 'null');
      region = okRegion(r) ? r : null;
    } catch { region = null; }
  }
  function keepRegion(r) {
    region = r;
    try { localStorage.setItem(REGION_KEY, JSON.stringify(r)); } catch { /* приватне вікно */ }
  }
  const DEF = { key: 'житомирський', oblast: 'житомирська', name: 'Житомир' };
  const cur = () => region || (data && data.def) || DEF;

  async function setRegion(r) {
    keepRegion(r);
    pending = true;
    paint();
    if (o.me.account) { try { await o.api('POST', '/api/tryvoga/region', r); } catch { /* район лишиться хоч у браузері */ } }
    refresh();
  }

  // ---------------------------------------------------------------------------------------------
  // дані
  // ---------------------------------------------------------------------------------------------

  async function refresh() {
    lastRefresh = Date.now();
    const r = cur();
    const q = region ? '?raion=' + encodeURIComponent(r.key) + '&oblast=' + encodeURIComponent(r.oblast) : '';
    let d;
    try { d = await o.api('GET', '/api/tryvoga' + q); } catch { pending = false; paint(); return; }
    pending = false;
    if (!d) return;
    data = d;
    if (d.off) { paint(); return; }
    // Район акаунта головніший за браузерний: обрав на телефоні — і на ноуті той самий. Акаунт без району, а в
    // браузері є — віддаємо серверу. В обох випадках статус ми спитали не про той район — перепитаємо.
    const my = okRegion(d.my) ? d.my : null;
    if (my && (!region || region.key !== my.key || region.oblast !== my.oblast || region.name !== my.name)) {
      const again = !region || region.key !== my.key;
      keepRegion(my);
      if (again) { refresh(); return; }
    } else if (!my && region && o.me.account) {
      o.api('POST', '/api/tryvoga/region', region).catch(() => {});
    }
    paint();
  }

  /// Хаб: тривоги змінились чи дані застаріли. Вкладок багато — розводимо їх у часі, щоб не прийти всім разом.
  function ping() {
    clearTimeout(pingTimer);
    pingTimer = setTimeout(refresh, 200 + Math.random() * 1500);
  }

  async function loadThreats() {
    clearTimeout(threatTimer);
    if (!shown) return;
    if (!document.hidden) {
      try {
        const t = await o.api('GET', '/api/tryvoga/threats');
        if (t && !t.off) { threats = t; paintThreats(); kick(); }
      } catch { /* наступного разу */ }
    }
    if (shown) threatTimer = setTimeout(loadThreats, THREATS_MS);
  }

  // ---------------------------------------------------------------------------------------------
  // плашка й кнопка в шапці
  // ---------------------------------------------------------------------------------------------

  const status = () => (!pending && data && !data.off && data.status) || { on: false };
  const hideId = (s) => cur().key + '|' + s.since + '|' + s.level;
  function hidden(s) { try { return localStorage.getItem(HIDE_KEY) === hideId(s); } catch { return false; } }

  function paint() {
    paintBar();
    paintBtn();
    if (shown) { paintPage(); paintThreats(); }
  }

  function paintBar() {
    const bar = $('tryBar');
    if (!bar) return;
    const off = !data || data.off;
    const s = status();
    const stale = !off && data.stale;
    // Дані застаріли, а востаннє була тривога — кажемо «не знаю», а не мовчки ховаємо плашку.
    const show = !off && s.on && !hidden(s);
    bar.hidden = !show;
    if (!show) return;
    bar.classList.toggle('lvl-red', !stale && s.level === 'red');
    bar.classList.toggle('lvl-yellow', !stale && s.level === 'yellow');
    bar.classList.toggle('lvl-stale', stale);
    const name = esc(cur().name);
    const since = Date.parse(s.since || '');
    const w = what(s.level, s.reasons);
    bar.querySelector('.nb-text').innerHTML = stale
      ? `<b>Не знаю, чи ще триває тривога</b> — ${name}: дані не оновлюються з ${esc(kyiv(new Date(Date.parse(data.at) || Date.now())))}. Дивись застосунок «Повітряна тривога».`
      : `<b>Повітряна тривога</b> — ${name}${w ? ' · ' + w : ''}${since ? ' · ' + lasts(Date.now() - since) : ''}`;
  }

  function paintBtn() {
    const b = $('tryBtn');
    if (!b) return;
    const off = !data || data.off;
    b.hidden = off && !!data;
    const s = status();
    const stale = !off && data && data.stale;
    b.classList.toggle('on', !stale && s.on);
    b.classList.toggle('red', !stale && s.on && s.level === 'red');
    b.classList.toggle('yellow', !stale && s.on && s.level === 'yellow');
    b.classList.toggle('stale', !!stale);
    const name = cur().name;
    b.title = stale ? 'Повітряна тривога: дані застаріли — мапа'
      : s.on ? `Повітряна тривога — ${name}${what(s.level, s.reasons) ? ': ' + what(s.level, s.reasons) : ''}. Мапа`
        : `${name}: тихо. Мапа тривог і того, що в небі`;
    b.setAttribute('aria-label', b.title);
  }

  // ---------------------------------------------------------------------------------------------
  // мапа: збірка
  // ---------------------------------------------------------------------------------------------

  let map = null, mapPromise = null;

  function loadMap() {
    if (!mapPromise) {
      mapPromise = fetch(MAP_URL)
        .then((r) => { if (!r.ok) throw new Error('мапа ' + r.status); return r.json(); })
        .then(buildMap)
        .catch((e) => { mapPromise = null; throw e; });
    }
    return mapPromise;
  }

  function buildMap(raw) {
    // дуги — дельтами: перша точка абсолютна, далі різниці (build-tryvoga.py)
    const arcs = raw.arcs.map((d) => {
      const a = new Int32Array(d.length);
      a[0] = d[0]; a[1] = d[1];
      for (let i = 2; i < d.length; i += 2) { a[i] = a[i - 2] + d[i]; a[i + 1] = a[i - 1] + d[i + 1]; }
      return a;
    });
    const users = arcs.map(() => []);
    const units = raw.units.map((u, ui) => {
      const p = new Path2D();
      const bb = [1e9, 1e9, -1e9, -1e9];
      for (const ring of u.rings) {
        let first = true;
        for (const id of ring) {
          const a = arcs[Math.abs(id) - 1];
          users[Math.abs(id) - 1].push(ui);
          const n = a.length / 2;
          for (let k = first ? 0 : 1; k < n; k++) {
            const i = id > 0 ? k : n - 1 - k;
            const X = a[2 * i], Y = a[2 * i + 1];
            if (first) { p.moveTo(X, Y); first = false; } else p.lineTo(X, Y);
            if (X < bb[0]) bb[0] = X; if (Y < bb[1]) bb[1] = Y; if (X > bb[2]) bb[2] = X; if (Y > bb[3]) bb[3] = Y;
          }
        }
        p.closePath();
      }
      return { k: u.k, n: u.n, o: u.o, c: u.c, p, bb };
    });
    // дуга одного району — контур країни; двох з різних областей — межа області; з однієї — межа району
    const outline = new Path2D(), oblast = new Path2D(), raion = new Path2D();
    arcs.forEach((a, i) => {
      const us = users[i];
      const p = us.length < 2 ? outline : units[us[0]].o !== units[us[1]].o ? oblast : raion;
      p.moveTo(a[0], a[1]);
      for (let k = 2; k < a.length; k += 2) p.lineTo(a[k], a[k + 1]);
    });
    const river = new Path2D();
    for (const rv of raw.rivers || []) for (const l of rv.lines) {
      river.moveTo(l[0], l[1]);
      for (let k = 2; k < l.length; k += 2) river.lineTo(l[k], l[k + 1]);
    }
    const P = raw.proj, r = Math.PI / 180;
    const N = (Math.sin(P.phi1 * r) + Math.sin(P.phi2 * r)) / 2;
    const C = Math.cos(P.phi1 * r) ** 2 + 2 * N * Math.sin(P.phi1 * r);
    const rho0 = Math.sqrt(C - 2 * N * Math.sin(P.phi0 * r)) / N;
    // Рівновеликий Альберс — той самий, що в build-map.py: координати цілей лягають у ту саму сітку.
    // tilt — на скільки «північ» на мапі тут повернута (меридіани сходяться): курс цілі малюємо з поправкою.
    const project = (lat, lon) => {
      const rho = Math.sqrt(C - 2 * N * Math.sin(lat * r)) / N;
      const th = N * (lon - P.lam0) * r;
      return [(rho * Math.sin(th) - P.x0) * P.k, (P.y0 - (rho0 - rho * Math.cos(th))) * P.k, th];
    };
    const byKey = Object.create(null);
    for (const u of units) byKey[u.k] = u;
    const oblasts = Object.create(null);
    for (const ob of raw.oblasts) oblasts[ob.k] = ob;
    map = { W: raw.w, H: raw.h, units, byKey, oblasts, oblastList: raw.oblasts, outline, oblast, raion, river,
      cities: raw.cities || [], project, kmUnits: P.k / EARTH_KM };
    return map;
  }

  // ---------------------------------------------------------------------------------------------
  // мапа: вид (той самий підхід, що в «Де це?»: s0 — вписати, k — наближення, ox/oy — зсув)
  // ---------------------------------------------------------------------------------------------

  const st = { cv: null, g: null, box: null, cw: 0, ch: 0, dpr: 1, s0: 1, k: 1, ox: 0, oy: 0, raf: 0,
    ptrs: new Map(), down: null, pinch: null, full: false, hits: [], css: {}, ro: null, hintT: 0 };
  const sc = () => st.s0 * st.k;
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));

  function clampView() {
    if (!map) return;
    const mw = map.W * sc(), mh = map.H * sc();
    const mx = st.cw * 0.25, my = st.ch * 0.25;
    st.ox = mw <= st.cw ? (st.cw - mw) / 2 : clamp(st.ox, st.cw - mw - mx, mx);
    st.oy = mh <= st.ch ? (st.ch - mh) / 2 : clamp(st.oy, st.ch - mh - my, my);
  }
  function kick() {
    if (st.raf || !st.cv) return;
    st.raf = requestAnimationFrame(() => { st.raf = 0; draw(); });
  }
  function zoomAt(px, py, f) {
    const k = clamp(st.k * f, 1, KMAX);
    if (k === st.k) return;
    st.ox = px - (px - st.ox) * (k / st.k);
    st.oy = py - (py - st.oy) * (k / st.k);
    st.k = k;
    clampView();
    hidePop();
    kick();
  }
  /// Вписати рамку [x0, y0, x1, y1] (одиниці сітки) з полем.
  function fit(bb, maxK) {
    const bw = Math.max(bb[2] - bb[0], 1), bh = Math.max(bb[3] - bb[1], 1);
    st.k = clamp(Math.min(st.cw * 0.8 / (bw * st.s0), st.ch * 0.8 / (bh * st.s0)), 1, maxK || KMAX);
    st.ox = st.cw / 2 - (bb[0] + bb[2]) / 2 * sc();
    st.oy = st.ch / 2 - (bb[1] + bb[3]) / 2 * sc();
    clampView();
    hidePop();
    kick();
  }
  function home() { st.k = 1; clampView(); hidePop(); kick(); }
  function oblastBox(ok) {
    const bb = [1e9, 1e9, -1e9, -1e9];
    for (const u of map.units) if (u.o === ok) { bb[0] = Math.min(bb[0], u.bb[0]); bb[1] = Math.min(bb[1], u.bb[1]); bb[2] = Math.max(bb[2], u.bb[2]); bb[3] = Math.max(bb[3], u.bb[3]); }
    return bb[0] < 1e9 ? bb : null;
  }
  /// «📍»: мій район разом з областю довкола — щоб було видно, що летить у твій бік.
  function focusMine() {
    if (!map) return;
    const u = map.byKey[cur().key];
    const bb = oblastBox(cur().oblast) || (u && u.bb);
    if (bb) fit(bb, 6);
  }

  function resize() {
    if (!st.cv || !map) return;
    const cw = st.box.clientWidth, ch = st.box.clientHeight;
    if (!cw || !ch) return;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    if (cw === st.cw && ch === st.ch && dpr === st.dpr) return;
    const first = !st.cw;
    const center = first ? [map.W / 2, map.H / 2] : [(st.cw / 2 - st.ox) / sc(), (st.ch / 2 - st.oy) / sc()];
    st.cw = cw; st.ch = ch; st.dpr = dpr;
    st.s0 = Math.min(cw / map.W, ch / map.H);
    st.cv.width = Math.round(cw * dpr);
    st.cv.height = Math.round(ch * dpr);
    st.ox = cw / 2 - center[0] * sc();
    st.oy = ch / 2 - center[1] * sc();
    clampView();
    draw();
  }

  function cssColors() {
    const cs = getComputedStyle(st.box);
    const c = (n, f) => (cs.getPropertyValue(n) || '').trim() || f;
    st.css = {
      sea: c('--tv-sea', '#132a20'), land: c('--tv-land', '#234032'), red: c('--tv-red', 'rgba(229,83,77,.62)'),
      yellow: c('--tv-yellow', 'rgba(244,197,66,.5)'), raion: c('--tv-raion', 'rgba(236,241,234,.1)'),
      oblast: c('--tv-oblast', 'rgba(236,241,234,.34)'), outline: c('--tv-outline', 'rgba(236,241,234,.55)'),
      river: c('--tv-river', 'rgba(74,143,179,.45)'), mine: c('--text', '#ecf1ea'), text: c('--text', '#ecf1ea'),
      muted: c('--muted', '#9db3a5'), halo: c('--bg', '#0f1f18'),
    };
    for (const t of Object.keys(TYPES)) st.css['t-' + t] = c('--tv-' + t, '#f0820e');
  }

  // ---------------------------------------------------------------------------------------------
  // мапа: малювання
  // ---------------------------------------------------------------------------------------------

  const RANK = { yellow: 1, red: 2 };
  /// Рівень тривоги на кожен район: свій запис, запис усієї його області, або сам район — «уся одиниця» (м. Київ).
  function levels() {
    const own = Object.create(null), whole = Object.create(null);
    for (const a of (data && !data.off && data.alerts) || []) {
      const box = a.whole ? whole : own;
      if ((RANK[a.level] || 2) > (RANK[box[a.key]] || 0)) box[a.key] = a.level;
    }
    const lv = Object.create(null);
    for (const u of map.units) {
      let best = null;
      for (const l of [own[u.k], whole[u.k], whole[u.o]]) if (l && (RANK[l] || 2) > (RANK[best] || 0)) best = l;
      if (best) lv[u.k] = best;
    }
    return lv;
  }

  function draw() {
    if (!st.cv || !map || !st.cw) return;
    const g = st.g, c = st.css, s = sc(), d = st.dpr;
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.fillStyle = c.sea;
    g.fillRect(0, 0, st.cv.width, st.cv.height);
    g.setTransform(d * s, 0, 0, d * s, d * st.ox, d * st.oy);
    const lv = levels();
    const stale = data && data.stale;
    for (const u of map.units) {
      g.fillStyle = c.land;
      g.fill(u.p, 'evenodd');
      const l = lv[u.k];
      if (l && !stale) {
        g.fillStyle = l === 'yellow' ? c.yellow : c.red;
        g.fill(u.p, 'evenodd');
      }
    }
    g.lineJoin = 'round';
    g.lineCap = 'round';
    g.strokeStyle = c.river;
    g.lineWidth = 1.1 / s;
    g.stroke(map.river);
    g.strokeStyle = c.raion;
    g.lineWidth = 0.8 / s;
    g.stroke(map.raion);
    g.strokeStyle = c.oblast;
    g.lineWidth = 1.1 / s;
    g.stroke(map.oblast);
    g.strokeStyle = c.outline;
    g.lineWidth = 1.5 / s;
    g.stroke(map.outline);
    const mine = map.byKey[cur().key];
    if (mine) {
      g.strokeStyle = c.mine;
      g.lineWidth = 2.2 / s;
      g.stroke(mine.p);
    }
    // далі — у пікселях екрана: значки однакового розміру за будь-якого наближення
    g.setTransform(d, 0, 0, d, 0, 0);
    paintCities(g, s);
    paintTargets(g, s);
    if (mine) pin(g, mine.c[0] * s + st.ox, mine.c[1] * s + st.oy);
  }

  function halo(g, text, x, y, color, font) {
    g.font = font;
    g.lineWidth = 3;
    g.strokeStyle = st.css.halo;
    g.strokeText(text, x, y);
    g.fillStyle = color;
    g.fillText(text, x, y);
  }

  function paintCities(g, s) {
    // обласні центри — лише коли мапа вже не дрібна: на всю Україну в телефоні підписи злиплися б
    if (s < 0.17) return;
    g.textBaseline = 'middle';
    g.textAlign = 'left';
    for (const ct of map.cities) {
      const x = ct.x * s + st.ox, y = ct.y * s + st.oy;
      if (x < -40 || y < -10 || x > st.cw + 10 || y > st.ch + 10) continue;
      g.fillStyle = st.css.muted;
      g.beginPath();
      g.arc(x, y, 2, 0, Math.PI * 2);
      g.fill();
      halo(g, ct.name, x + 5, y, st.css.muted, '11px system-ui, sans-serif');
    }
  }

  function pin(g, x, y) {
    g.save();
    g.translate(x, y);
    g.fillStyle = st.css.mine;
    g.strokeStyle = st.css.halo;
    g.lineWidth = 2;
    g.beginPath();
    g.moveTo(0, 0);
    g.bezierCurveTo(-7, -9, -7, -17, 0, -17);
    g.bezierCurveTo(7, -17, 7, -9, 0, 0);
    g.closePath();
    g.stroke();
    g.fill();
    g.fillStyle = st.css.halo;
    g.beginPath();
    g.arc(0, -11.5, 2.6, 0, Math.PI * 2);
    g.fill();
    g.restore();
  }

  /// Значок цілі: носом за курсом (поправка на те, що північ на конічній мапі трохи повернута).
  function glyph(g, shape, color, rot) {
    g.save();
    g.rotate(rot);
    g.fillStyle = color;
    g.strokeStyle = 'rgba(0,0,0,.65)';
    g.lineWidth = 1.2;
    g.beginPath();
    if (shape === 'delta') { g.moveTo(0, -8); g.lineTo(6, 6); g.lineTo(0, 3); g.lineTo(-6, 6); }
    else if (shape === 'dart') { g.moveTo(0, -10); g.lineTo(3.5, 6); g.lineTo(0, 4); g.lineTo(-3.5, 6); }
    else if (shape === 'diamond') { g.moveTo(0, -9); g.lineTo(5, 0); g.lineTo(0, 9); g.lineTo(-5, 0); }
    else if (shape === 'plane') { g.moveTo(0, -9); g.lineTo(2, -2); g.lineTo(8, 2); g.lineTo(2, 2); g.lineTo(1.5, 7); g.lineTo(-1.5, 7); g.lineTo(-2, 2); g.lineTo(-8, 2); g.lineTo(-2, -2); }
    else if (shape === 'ring') { g.arc(0, 0, 5, 0, Math.PI * 2); g.closePath(); g.stroke(); g.restore(); return; }
    else g.arc(0, 0, 4.5, 0, Math.PI * 2);
    g.closePath();
    g.fill();
    g.stroke();
    g.restore();
  }

  function paintTargets(g, s) {
    st.hits = [];
    const list = (threats && threats.threats) || [];
    const now = Date.now();
    for (const t of list) {
      if (t.areaOnly) continue;            // «десь в області» — центр області, а не місце: лише в переліку
      const [X, Y, tilt] = map.project(t.lat, t.lon);
      const x = X * s + st.ox, y = Y * s + st.oy;
      if (x < -60 || y < -60 || x > st.cw + 60 || y > st.ch + 60) continue;
      const meta = typeOf(t.type);
      const color = st.css['t-' + (TYPES[t.type] ? t.type : 'unknown')];
      const old = now - (Date.parse(t.updated) || now) > 15 * 60000;
      g.globalAlpha = t.advisory || old ? 0.5 : t.lifecycle === 'uncertain' ? 0.8 : 1;
      if (t.trail && t.trail.length) {
        g.strokeStyle = color;
        g.lineWidth = 1.5;
        g.setLineDash([]);
        g.globalAlpha *= 0.55;
        g.beginPath();
        t.trail.forEach(([la, lo], i) => {
          const [tx, ty] = map.project(la, lo);
          if (i === 0) g.moveTo(tx * s + st.ox, ty * s + st.oy); else g.lineTo(tx * s + st.ox, ty * s + st.oy);
        });
        g.lineTo(x, y);
        g.stroke();
        g.globalAlpha /= 0.55;
      }
      if (t.quality !== 'confirmed' && t.unc > 0) {
        const r = t.unc * map.kmUnits * s;
        if (r > 6) {
          g.setLineDash([3, 3]);
          g.strokeStyle = color;
          g.lineWidth = 1;
          g.globalAlpha *= 0.6;
          g.beginPath();
          g.arc(x, y, r, 0, Math.PI * 2);
          g.stroke();
          g.setLineDash([]);
          g.globalAlpha /= 0.6;
        }
      }
      g.save();
      g.translate(x, y);
      const rot = t.heading == null ? 0 : (t.heading * Math.PI) / 180 - tilt;
      glyph(g, t.heading == null && meta.shape !== 'ring' ? 'dot' : meta.shape, color, rot);
      g.restore();
      if (t.count > 1) halo(g, '×' + t.count, x + 8, y - 8, st.css.text, 'bold 11px system-ui, sans-serif');
      g.globalAlpha = 1;
      st.hits.push({ x, y, t });
    }
  }

  // ---------------------------------------------------------------------------------------------
  // мапа: жести
  // ---------------------------------------------------------------------------------------------

  function local(e) {
    const r = st.cv.getBoundingClientRect();
    return [e.clientX - r.left, e.clientY - r.top];
  }

  function onDown(e) {
    st.cv.setPointerCapture(e.pointerId);
    const [x, y] = local(e);
    st.ptrs.set(e.pointerId, { x, y });
    if (st.ptrs.size === 1) st.down = { x, y, ox: st.ox, oy: st.oy, moved: false };
    else if (st.ptrs.size === 2) {
      const [a, b] = [...st.ptrs.values()];
      st.pinch = { d: Math.hypot(a.x - b.x, a.y - b.y), k: st.k, cx: (a.x + b.x) / 2, cy: (a.y + b.y) / 2 };
      if (st.down) st.down.moved = true;
    }
  }
  function onMove(e) {
    if (!st.ptrs.has(e.pointerId)) return;
    const [x, y] = local(e);
    st.ptrs.set(e.pointerId, { x, y });
    if (st.pinch && st.ptrs.size >= 2) {
      const [a, b] = [...st.ptrs.values()];
      const f = (st.pinch.k * Math.hypot(a.x - b.x, a.y - b.y) / Math.max(1, st.pinch.d)) / st.k;
      zoomAt(st.pinch.cx, st.pinch.cy, f);
      return;
    }
    const dn = st.down;
    if (!dn) return;
    if (!dn.moved && Math.hypot(x - dn.x, y - dn.y) < 5) return;
    dn.moved = true;
    st.ox = dn.ox + (x - dn.x);
    st.oy = dn.oy + (y - dn.y);
    clampView();
    hidePop();
    kick();
  }
  function onUp(e) {
    const dn = st.down;
    st.ptrs.delete(e.pointerId);
    if (st.ptrs.size < 2) st.pinch = null;
    if (st.ptrs.size) return;
    st.down = null;
    if (dn && !dn.moved && e.type === 'pointerup') tap(dn.x, dn.y);
  }
  function onWheel(e) {
    // На сторінці колесо гортає сторінку; наближає — на весь екран або з Ctrl (як у вбудованих мапах).
    if (!st.full && !e.ctrlKey) {
      const h = st.box.querySelector('.tv-hint');
      h.hidden = false;
      clearTimeout(st.hintT);
      st.hintT = setTimeout(() => { h.hidden = true; }, 1300);
      return;
    }
    e.preventDefault();
    const [x, y] = local(e);
    zoomAt(x, y, Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0018)));
  }

  function tap(x, y) {
    let best = null, bd = 18;
    for (const h of st.hits) {
      const dd = Math.hypot(h.x - x, h.y - y);
      if (dd < bd) { bd = dd; best = h; }
    }
    if (best) { showPop(x, y, targetHtml(best.t)); return; }
    const s = sc(), d = st.dpr, g = st.g;
    g.setTransform(d * s, 0, 0, d * s, d * st.ox, d * st.oy);
    let hit = null;
    for (let i = map.units.length - 1; i >= 0; i--) if (g.isPointInPath(map.units[i].p, x * d, y * d, 'evenodd')) { hit = map.units[i]; break; }
    g.setTransform(1, 0, 0, 1, 0, 0);
    if (!hit) { hidePop(); return; }
    showPop(x, y, unitHtml(hit), hit);
  }

  function alertOf(u) {
    const list = (data && !data.off && data.alerts) || [];
    return list.filter((a) => a.key === u.k || (a.whole && a.key === u.o));
  }
  const oblastName = (ok) => (map && map.oblasts[ok] && map.oblasts[ok].n) || '';
  const oblastShort = (ok) => (map && map.oblasts[ok] && map.oblasts[ok].s) || '';

  function unitHtml(u) {
    const hits = alertOf(u);
    const isMine = u.k === cur().key;
    let st8;
    if (data && data.stale) st8 = '<span class="tv-muted">дані про тривоги застаріли</span>';
    else if (!hits.length) st8 = '<span class="tv-ok">тихо</span>';
    else {
      const lvl = hits.some((a) => a.level === 'red') ? 'red' : 'yellow';
      const since = hits.map((a) => a.since).sort()[0];
      const w = what(lvl, hits.flatMap((a) => a.reasons || []));
      st8 = `<span class="tv-lvl ${lvl}">🚨 тривога${w ? ' · ' + esc(w) : ''}</span> <span class="tv-muted">${esc(sinceText(since))}</span>`;
    }
    const where = u.k === u.o ? '' : ' · ' + esc(oblastShort(u.o)) + (u.o === 'м. київ' || u.o === 'севастополь' ? '' : ' обл.');
    return `<b>${esc(u.n)}</b>${where}<br>${st8}`
      + (isMine ? '<div class="tv-muted tv-small">📍 це твій район</div>' : '<button type="button" class="tv-mine">📍 Мій район</button>');
  }

  function targetHtml(t) {
    const m = typeOf(t.type);
    const bits = [];
    if (t.count > 1) bits.push('група ×' + t.count);
    if (t.speed) bits.push(Math.round(t.speed) + ' км/год');
    if (t.sources) bits.push(plural(t.sources, 'підтвердження', 'підтвердження', 'підтверджень'));
    if (t.quality && t.quality !== 'confirmed') bits.push('місце приблизне' + (t.unc ? ', ±' + Math.round(t.unc) + ' км' : ''));
    if (t.advisory) bits.push('спостереження, не тривога');
    return `<b>${m.ico} ${esc(t.title || m.label)}</b>${t.region ? ' · ' + esc(t.region.replace(' область', ' обл.')) : ''}<br>`
      + (t.text ? esc(t.text) + '<br>' : '')
      + `<span class="tv-muted">${esc(bits.join(' · '))}${bits.length ? ' · ' : ''}${esc(ago(t.updated))}</span>`;
  }

  let popUnit = null;
  function showPop(x, y, html, unit) {
    const p = st.box.querySelector('.tv-pop');
    p.innerHTML = html;
    p.hidden = false;
    popUnit = unit || null;
    // над пальцем, але не за краєм рамки
    const w = p.offsetWidth, h = p.offsetHeight;
    p.style.left = clamp(x - w / 2, 6, st.cw - w - 6) + 'px';
    p.style.top = (y - h - 14 >= 6 ? y - h - 14 : Math.min(y + 14, st.ch - h - 6)) + 'px';
  }
  function hidePop() {
    const p = st.box && st.box.querySelector('.tv-pop');
    if (p) p.hidden = true;
    popUnit = null;
  }

  function setFull(on) {
    st.full = on;
    const wrap = $('tryvoga').querySelector('.tv-mapwrap');
    wrap.classList.toggle('tv-full', on);
    document.body.classList.toggle('tv-full-open', on);
    wrap.querySelector('.tv-fullbtn').textContent = on ? '✕' : '⤢';
    wrap.querySelector('.tv-fullbtn').title = on ? 'Згорнути (Esc)' : 'На весь екран';
    hidePop();
    requestAnimationFrame(resize);
  }

  // ---------------------------------------------------------------------------------------------
  // сторінка
  // ---------------------------------------------------------------------------------------------

  function buildPage() {
    const root = $('tryvoga');
    if (!root || built) return;
    built = true;
    root.innerHTML = `
      <section class="panel tv-me">
        <div class="tv-state" id="tvState"></div>
        <label class="tv-pick"><span>📍 Мій район</span><select id="tvRegion" aria-label="Мій район"><option>…</option></select></label>
      </section>
      <section class="panel tv-mappanel">
        <div class="tv-mapwrap">
          <div class="tv-box">
            <canvas class="tv-cv" aria-label="Мапа тривог по районах і цілей у небі"></canvas>
            <div class="tv-pop" role="dialog" hidden></div>
            <div class="tv-hint" hidden>Наблизити — Ctrl + колесо або ⤢</div>
            <div class="tv-load">Мапа вантажиться…</div>
          </div>
          <div class="tv-ctl">
            <button type="button" class="icon tv-zin" title="Наблизити" aria-label="Наблизити">＋</button>
            <button type="button" class="icon tv-zout" title="Віддалити" aria-label="Віддалити">－</button>
            <button type="button" class="icon tv-home" title="Уся Україна" aria-label="Уся Україна">⌂</button>
            <button type="button" class="icon tv-me-btn" title="Мій район і область" aria-label="Мій район і область">📍</button>
            <button type="button" class="icon tv-fullbtn" title="На весь екран" aria-label="На весь екран">⤢</button>
          </div>
        </div>
        <div class="tv-legend">
          <span><i class="sw red"></i>ракетна загроза</span><span><i class="sw yellow"></i>загроза дронів</span>
          <span><i class="gl uav"></i>БпЛА</span><span><i class="gl missile"></i>ракети</span><span><i class="gl kab"></i>КАБ</span>
          <span><i class="gl ring"></i>розвідка й інше</span><span><i class="ci"></i>місце приблизне</span>
        </div>
      </section>
      <section class="panel tv-sky">
        <h2>У небі зараз <span class="count" id="tvSkyAt"></span></h2>
        <div id="tvSky" class="tv-skybody"><p class="tv-muted">Вантажу…</p></div>
      </section>
      <p class="tv-note">⚠ Це не офіційне оповіщення: дані можуть запізнюватись чи бути неточними. Орієнтуйся на сирену
        й застосунок «Повітряна тривога». Дані: <a href="https://neptun.in.ua/" target="_blank" rel="noopener">Карта повітряних тривог — NEPTUN</a>.
        <span id="tvAt"></span></p>`;
    const box = root.querySelector('.tv-box');
    st.box = box;
    st.cv = root.querySelector('.tv-cv');
    st.g = st.cv.getContext('2d');
    st.cv.addEventListener('pointerdown', onDown);
    st.cv.addEventListener('pointermove', onMove);
    st.cv.addEventListener('pointerup', onUp);
    st.cv.addEventListener('pointercancel', onUp);
    st.cv.addEventListener('wheel', onWheel, { passive: false });
    const zc = (f) => zoomAt(st.cw / 2, st.ch / 2, f);
    root.querySelector('.tv-zin').onclick = () => zc(1.6);
    root.querySelector('.tv-zout').onclick = () => zc(1 / 1.6);
    root.querySelector('.tv-home').onclick = home;
    root.querySelector('.tv-me-btn').onclick = focusMine;
    root.querySelector('.tv-fullbtn').onclick = () => setFull(!st.full);
    box.querySelector('.tv-pop').addEventListener('click', (e) => {
      if (!e.target.closest('.tv-mine') || !popUnit) return;
      const u = popUnit;
      hidePop();
      choose(u);
    });
    $('tvRegion').addEventListener('change', (e) => {
      const u = map && map.byKey[e.target.value];
      if (u) { choose(u); focusMine(); }
    });
    $('tvSky').addEventListener('click', (e) => {
      const ob = e.target.closest('[data-ob]');
      if (ob && map) { const bb = oblastBox(ob.dataset.ob); if (bb) fit(bb, 6); st.box.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); return; }
      const id = e.target.closest('[data-id]');
      if (id && map) focusTarget(id.dataset.id);
    });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && st.full) setFull(false); });
    st.ro = new ResizeObserver(() => resize());
    st.ro.observe(box);
    loadMap().then(() => {
      box.querySelector('.tv-load').remove();
      cssColors();
      fillSelect();
      resize();
      paintThreats();
      if (status().on) focusMine();
      paintPage();
    }).catch(() => { box.querySelector('.tv-load').textContent = 'Мапа не завантажилась — онови сторінку.'; });
  }

  /// Типовий район зветься так, як його назвав сервер («Житомир»), а не «Житомирський район».
  function choose(u) {
    const def = (data && data.def) || DEF;
    const name = u.k === def.key ? def.name : u.n;
    setRegion({ key: u.k, oblast: u.o, name });
  }

  function focusTarget(id) {
    const h = st.hits.find((x) => x.t.id === id);
    const t = h ? h.t : ((threats && threats.threats) || []).find((x) => x.id === id);
    if (!t) return;
    const [X, Y] = map.project(t.lat, t.lon);
    const r = 40 * map.kmUnits;
    fit([X - r, Y - r, X + r, Y + r], 5);
    st.box.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    requestAnimationFrame(() => {
      const hh = st.hits.find((x) => x.t.id === id);
      if (hh) showPop(hh.x, hh.y, targetHtml(hh.t));
    });
  }

  function fillSelect() {
    const sel = $('tvRegion');
    if (!sel || !map) return;
    const groups = map.oblastList.map((ob) => {
      const us = map.units.filter((u) => u.o === ob.k).sort((a, b) => a.n.localeCompare(b.n, 'uk'));
      if (!us.length) return '';
      if (us.length === 1 && us[0].k === ob.k) return `<option value="${esc(us[0].k)}">${esc(us[0].n)}</option>`;
      return `<optgroup label="${esc(ob.n)}">` + us.map((u) => `<option value="${esc(u.k)}">${esc(u.n)}</option>`).join('') + '</optgroup>';
    });
    sel.innerHTML = groups.join('');
    sel.value = cur().key;
  }

  function paintPage() {
    const box = $('tvState');
    if (!box) return;
    const s = status();
    const r = cur();
    const off = !data || data.off;
    let html;
    if (off) html = '<span class="tv-big">🛡</span><div><b>Тривоги зараз не показуємо</b><div class="tv-muted">Сервер їх не опитує.</div></div>';
    else if (pending) html = `<span class="tv-big">⏳</span><div><b>Перевіряю</b> — ${esc(r.name)}</div>`;
    else if (data.stale) {
      const since = Date.parse(data.at || '');
      html = `<span class="tv-big">❔</span><div><b>Не знаю, чи є тривога</b> — ${esc(r.name)}<div class="tv-muted">`
        + (since ? `Дані не оновлюються з ${esc(kyiv(new Date(since)))}.` : 'Дані про тривоги ще не надійшли.')
        + ' Дивись застосунок «Повітряна тривога».</div></div>';
    }
    else if (s.on) {
      const w = what(s.level, s.reasons);
      html = `<span class="tv-big">🚨</span><div><b>Повітряна тривога</b>${w ? ' · ' + esc(w) : ''}<div class="tv-muted">${esc(r.name)} · ${esc(sinceText(s.since))} · ${esc(lasts(Date.now() - Date.parse(s.since)))}</div></div>`;
    } else html = `<span class="tv-big">🟢</span><div><b>Тихо</b> — ${esc(r.name)}<div class="tv-muted">Тривоги в районі зараз нема.</div></div>`;
    box.className = 'tv-state' + (off || pending ? '' : data.stale ? ' stale' : s.on ? ' on ' + s.level : ' calm');
    box.innerHTML = html;
    const sel = $('tvRegion');
    if (sel && map && sel.value !== r.key) sel.value = r.key;
    const at = $('tvAt');
    if (at && data && data.at) at.textContent = 'Оновлено о ' + kyiv(new Date(Date.parse(data.at)));
    kick();
  }

  function paintThreats() {
    const body = $('tvSky');
    if (!body) return;
    const list = (threats && threats.threats) || [];
    $('tvSkyAt').textContent = threats && threats.at ? kyiv(new Date(Date.parse(threats.at))) : '';
    if (threats && threats.stale) { body.innerHTML = '<p class="tv-muted">Дані про цілі застаріли — NEPTUN зараз не відповідає.</p>'; return; }
    if (!list.length) { body.innerHTML = '<p class="tv-muted">Нічого не видно. Добре.</p>'; return; }
    const n = (t) => (t.count > 1 ? t.count : 1);
    const byType = {};
    for (const t of list) { const k = TYPES[t.type] ? t.type : 'unknown'; byType[k] = (byType[k] || 0) + n(t); }
    const chips = Object.keys(TYPES).filter((k) => byType[k])
      .map((k) => `<span class="tv-chip"><i class="gl ${TYPES[k].shape === 'ring' ? 'ring' : k}"></i>${TYPES[k].label} <b>${byType[k]}</b></span>`).join('');
    const myOb = oblastName(cur().oblast);
    const mine = list.filter((t) => t.region && t.region === myOb)
      .sort((a, b) => (Date.parse(b.updated) || 0) - (Date.parse(a.updated) || 0));
    const row = (t) => {
      const m = typeOf(t.type);
      const where = t.areaOnly ? 'десь в області' : '';
      return `<li data-id="${esc(t.id)}"><span class="tv-ico">${m.ico}</span><span class="tv-t">${esc(t.text || t.title || m.label)}${t.count > 1 ? ' <b>×' + t.count + '</b>' : ''}${where ? ' <span class="tv-muted">' + where + '</span>' : ''}</span><span class="tv-ago">${esc(ago(t.updated))}</span></li>`;
    };
    const perOb = {};
    for (const t of list) if (t.region) perOb[t.region] = (perOb[t.region] || 0) + n(t);
    const obKey = (name) => (map ? (map.oblastList.find((x) => x.n === name) || {}).k : '') || '';
    const obs = Object.entries(perOb).sort((a, b) => b[1] - a[1])
      .map(([name, c]) => `<button type="button" class="tv-ob" data-ob="${esc(obKey(name))}">${esc(name.replace(' область', ''))} <b>${c}</b></button>`).join('');
    body.innerHTML = `<div class="tv-chips">${chips}</div>`
      + (myOb ? `<h3>${esc(myOb)}</h3>` + (mine.length ? `<ul class="tv-list">${mine.map(row).join('')}</ul>` : '<p class="tv-muted">У твоїй області зараз нічого не видно.</p>') : '')
      + (obs ? `<h3>По областях</h3><div class="tv-obs">${obs}</div>` : '');
  }

  // ---------------------------------------------------------------------------------------------
  // назовні
  // ---------------------------------------------------------------------------------------------

  function show() {
    shown = true;
    buildPage();
    paintPage();
    paintThreats();
    if (map) requestAnimationFrame(resize);
    loadThreats();
    if (Date.now() - lastRefresh > 20000) refresh();
  }
  function hide() {
    if (!shown) return;
    shown = false;
    clearTimeout(threatTimer);
    if (st.full) setFull(false);
    hidePop();
  }

  function init(opts) {
    o = opts;
    loadRegion();
    const bar = $('tryBar');
    if (bar) {
      bar.querySelector('.tryx').onclick = () => {
        try { localStorage.setItem(HIDE_KEY, hideId(status())); } catch { /* */ }
        paintBar();
      };
    }
    const btn = $('tryBtn');
    if (btn) btn.onclick = () => o.go('#tryvoga');
    // «12 хв» на плашці має рости, а хаб може й мовчати (перепідключення) — раз на хвилину перепитуємо самі
    setInterval(() => {
      if (document.hidden) return;
      if (Date.now() - lastRefresh > 60000) refresh(); else paintBar();
      if (shown) paintPage();
    }, 30000);
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) return;
      if (Date.now() - lastRefresh > 30000) refresh();
      if (shown) loadThreats();
    });
    paint();
  }

  /// Після /api/me: уже відомо, чи це акаунт (чий район головніший).
  function ready() { refresh(); }

  window.HTryvoga = { init, ready, ping, show, hide, refresh };
})();
