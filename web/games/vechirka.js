// Глечикова вечірка (vechirka) — клієнт флагмана (specs/vechirka.md §14–§15). Правила й таймери — на сервері; тут лише
// показ і дії. Один модуль, розділи: дані · дошка SVG · камера · фішки й анімації · HUD · рішення · міні-гра
// (HGames.embed) · свято (результати, фінал) · лобі · клавіші/пад · реєстрація.
(() => {
  'use strict';
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M5 3h6l-1 2c2 1 3 3 3 5 0 3-2 4-5 4s-5-1-5-4c0-2 1-4 3-5z" fill="var(--clay)"/>'
    + '<path d="M8 7l.7 1.4 1.5.2-1.1 1 .3 1.5L8 10.4l-1.4.7.3-1.5-1.1-1 1.5-.2z" fill="var(--accent)"/></svg>';

  // ---------- константи (ті самі, що VechirkaRules: сервер чекає busy того ж розміру) ----------
  const STEP_MS = 260, DICE_MS = 1400, HOP_MS = 620;
  const HUES = [212, 18, 135, 45, 282, 340, 182, 95];         // 8 кольорів гравців (обідок фішки, чип)
  const EMO = { clap: '👏', laugh: '😂', wow: '😱', angry: '😤', party: '🥳' };
  const PRICES = { pan: 5, horse: 5, pick: 7, fork: 8, gate: 4, key: 3, rope: 6, charm: 6 };
  // Предмети, які вживають у фазі turn (ключ — на розвилці, оберіг діє сам) — інакше чип сірий.
  const TURN_ITEMS = new Set(['pan', 'horse', 'pick', 'fork', 'gate', 'rope', 'pumpkin', 'feather']);
  const TILE_IC = { start: '⛲', chest: '🎁', event: '?', shop: '🛒', duel: '⚔', bank: '🐷', church: '⛪' };
  const BOARD_PHASES = new Set(['intro', 'order', 'late', 'turn', 'aim', 'walk', 'prompt', 'pick', 'card']);
  const SVGNS = 'http://www.w3.org/2000/svg';
  let uidSeq = 0;

  const live = new Set();                                    // стани змонтованих столів
  const stOf = (ctx) => [...live].find((s) => s.ctx === ctx) || null;
  const hue = (p) => HUES[((p && p.color) || 0) % HUES.length];
  const now = () => Date.now();
  const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
  const isPhone = (st) => !!(st.el && st.el.vch.classList.contains('phone'));
  const reduced = () => window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;

  // ---------- дані: карта + тексти (кеш за map:v) ----------
  const dataCache = {};
  function loadData(map, v) {
    const key = map + ':' + v;
    if (!dataCache[key]) {
      dataCache[key] = fetch('/api/games/vechirka/data?map=' + encodeURIComponent(map) + '&v=' + encodeURIComponent(v))
        .then((r) => (r.ok ? r.json() : Promise.reject(new Error('data ' + r.status))))
        .then(prep)
        .catch((e) => { delete dataCache[key]; throw e; });
    }
    return dataCache[key];
  }
  function prep(d) {
    const m = d.map;
    const byId = {}, out = {}, und = {};
    m.nodes.forEach((n) => { byId[n.id] = n; out[n.id] = []; und[n.id] = []; });
    m.edges.forEach(([a, b]) => { out[a].push(b); und[a].push(b); und[b].push(a); });
    return { map: m, texts: d.texts || {}, byId, out, und };
  }
  const T = (st) => (st.data && st.data.texts) || {};
  const itemTx = (st, k) => (T(st).items || {})[k] || { name: k, icon: '❔', short: '', long: '' };
  const itemIc = (st, k) => itemTx(st, k).icon || '❔';
  const fill = (s, o) => String(s || '').replace(/\{(\w+)\}/g, (m, k) => (o[k] == null ? m : o[k]));

  // ---------- аватарки ----------
  /// Для фішки в SVG: фото (img) або літера/емодзі. HPeople дає HTML — дістаємо з нього src чи текст.
  function avaOf(p) {
    if (!p) return { txt: '?' };
    if (p.bot && !p.nick) return { txt: '🤖' };
    const nick = p.nick || p.name || '?';
    try {
      if (window.HPeople && HPeople.ava) {
        const d = document.createElement('div');
        d.innerHTML = HPeople.ava(nick, 'ava');
        const img = d.querySelector('img');
        if (img && img.getAttribute('src')) return { img: img.getAttribute('src') };
        const t = d.textContent.trim();
        if (t) return { txt: Array.from(t).slice(0, 2).join('') };
      }
    } catch (e) { /* без аватарки — літера */ }
    return { txt: Array.from(nick)[0].toUpperCase() };
  }
  /// HTML-аватарка для HUD у кольоровому обідку гравця.
  function avaHtml(p, esc, cls) {
    let inner;
    if (!p.nick || (p.bot && !p.away)) inner = '<span class="vch-avb">🤖</span>';
    else if (window.HPeople && HPeople.ava) { try { inner = HPeople.ava(p.nick, 'ava'); } catch (e) { inner = ''; } }
    if (!inner) inner = '<span class="vch-avb">' + esc(Array.from(p.nick || p.name || '?')[0]) + '</span>';
    return '<span class="vch-ava ' + (cls || '') + '" style="--ph:' + hue(p) + '">' + inner
      + (p.away ? '<i class="vch-avx" title="за нього грає бот">🤖</i>' : p.auto ? '<i class="vch-avx" title="задрімав — ходить Глек">💤</i>' : '')
      + '</span>';
  }

  // ---------- дошка SVG ----------
  function rnd(seed) { let s = seed | 0; return () => ((s = (s * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff); }
  const pts = (a) => a.map((p) => p[0] + ',' + p[1]).join(' ');

  function decorSvg(d) {
    const x = d.x, y = d.y;
    switch (d.kind) {
      case 'pond': {
        let h = '<ellipse class="d-pond" cx="' + x + '" cy="' + y + '" rx="' + d.rx + '" ry="' + d.ry + '"/>'
          + '<ellipse class="d-pond2" cx="' + x + '" cy="' + y + '" rx="' + (d.rx - 26) + '" ry="' + (d.ry - 20) + '"/>';
        for (let k = 0; k < 5; k++) {
          const wx = x - d.rx * 0.6 + k * d.rx * 0.3, wy = y - d.ry * 0.35 + (k % 2) * d.ry * 0.55;
          h += '<path class="d-wave" d="M' + (wx - 22) + ' ' + wy + ' q11 -9 22 0 t22 0"/>';
        }
        return h;
      }
      case 'river': return '<polyline class="d-river" points="' + pts(d.points) + '"/>';
      case 'church':
        return '<g class="d-church" transform="translate(' + x + ' ' + y + ')"><rect x="-34" y="-20" width="68" height="46" rx="3"/>'
          + '<rect class="d-dk" x="-9" y="6" width="18" height="20" rx="9"/><path class="d-roof" d="M-40 -18 L0 -44 L40 -18z"/>'
          + '<rect x="-11" y="-74" width="22" height="34"/><circle class="d-dome" cx="0" cy="-80" r="14"/>'
          + '<path class="d-cross" d="M0 -112 v20 M-6 -104 h12"/></g>';
      case 'well':
        return '<g class="d-well" transform="translate(' + x + ' ' + y + ')"><ellipse cx="0" cy="12" rx="22" ry="9"/>'
          + '<rect x="-22" y="-4" width="44" height="16"/><path class="d-pole" d="M-30 14 L-30 -30 M-46 -40 L40 -6"/></g>';
      case 'mill':
        return '<g class="d-mill" transform="translate(' + x + ' ' + y + ')"><path class="d-wall" d="M-18 40 L-12 -20 L12 -20 L18 40z"/>'
          + '<path class="d-roof" d="M-16 -18 L0 -38 L16 -18z"/><g class="d-blades">'
          + '<path d="M0 -30 L-6 -86 L6 -86z M0 -30 L56 -36 L56 -24z M0 -30 L6 26 L-6 26z M0 -30 L-56 -24 L-56 -36z"/>'
          + '<circle cx="0" cy="-30" r="5"/></g></g>';
      case 'forest': {
        const r = rnd(x * 7 + y * 13);
        let h = '<g class="d-forest">';
        const n = Math.round(d.r / 9);
        const trees = [];
        for (let k = 0; k < n; k++) {
          const a = r() * Math.PI * 2, q = Math.sqrt(r()) * d.r;
          trees.push([x + Math.cos(a) * q, y + Math.sin(a) * q * 0.75, 14 + r() * 12]);
        }
        trees.sort((a, b) => a[1] - b[1]);
        trees.forEach(([tx, ty, s]) => {
          h += '<path class="d-tree" d="M' + tx + ' ' + (ty - s * 1.9) + ' L' + (tx + s) + ' ' + ty + ' L' + (tx - s) + ' ' + ty + 'z"/>'
            + '<rect class="d-trunk" x="' + (tx - 2.5) + '" y="' + ty + '" width="5" height="' + (s * 0.5) + '"/>';
        });
        return h + '</g>';
      }
      case 'tents': {
        let h = '<g class="d-tents">';
        const n = Math.max(2, Math.floor(d.w / 110));
        for (let k = 0; k < n; k++) {
          const tx = x - d.w / 2 + (k + 0.5) * (d.w / n), b = y + d.h / 2, tw = 40;
          h += '<path class="d-tent' + (k % 3) + '" d="M' + (tx - tw) + ' ' + b + ' L' + tx + ' ' + (b - d.h) + ' L' + (tx + tw) + ' ' + b + 'z"/>'
            + '<path class="d-tstripe" d="M' + tx + ' ' + (b - d.h) + ' L' + (tx - tw / 3) + ' ' + b + ' M' + tx + ' ' + (b - d.h)
            + ' L' + (tx + tw / 3) + ' ' + b + '"/><path class="d-flag" d="M' + tx + ' ' + (b - d.h) + ' v-14 l12 5 l-12 5"/>';
        }
        return h + '</g>';
      }
      case 'house':
        return '<g class="d-house" transform="translate(' + x + ' ' + y + ')"><rect x="-26" y="-12" width="52" height="34" rx="2"/>'
          + '<rect class="d-win" x="-16" y="-3" width="12" height="11"/><rect class="d-dk" x="6" y="2" width="11" height="20"/>'
          + '<path class="d-straw" d="M-34 -10 L0 -38 L34 -10z"/></g>';
      case 'fence': {
        let h = '<polyline class="d-fence" points="' + pts(d.points) + '"/>';
        d.points.forEach((p) => { h += '<rect class="d-post" x="' + (p[0] - 3) + '" y="' + (p[1] - 10) + '" width="6" height="16"/>'; });
        return h;
      }
      case 'bank': return '<text class="d-emoji" x="' + x + '" y="' + y + '">🐷</text>';
      default: return '';
    }
  }

  const branchOf = (id) => /^[lyp]/.test(id);
  function tileLabel(n) {
    if (n.type === 'coin') return '+' + (n.v || 0);
    if (n.type === 'trap') return n.bump && !n.v ? '🐺' : '−' + Math.abs(n.v || 0);
    return TILE_IC[n.type] || '•';
  }

  function buildBoard(st) {
    const D = st.data, m = D.map;
    const gate = new Set((m.gates || []).map((g) => g.from + '>' + g.to));
    let h = '<defs><radialGradient id="vg' + st.uid + '"><stop offset="0" stop-color="#ffd860" stop-opacity=".95"/>'
      + '<stop offset=".55" stop-color="#ffb627" stop-opacity=".45"/><stop offset="1" stop-color="#ffb627" stop-opacity="0"/></radialGradient>'
      + '<clipPath id="vc' + st.uid + '"><circle r="20"/></clipPath></defs>'
      + '<rect class="vb-grass" x="-400" y="-400" width="' + (m.w + 800) + '" height="' + (m.h + 800) + '"/>'
      + '<g class="vb-decor">' + (m.decor || []).map(decorSvg).join('') + '</g>'
      + '<g class="vb-zones">' + (m.zones || []).map((z) => '<text x="' + z.x + '" y="' + z.y + '">' + st.ctx.esc(z.title) + '</text>').join('') + '</g>';
    let o = '', r = '', f = '';
    m.edges.forEach(([a, b]) => {
      const A = D.byId[a], B = D.byId[b];
      const br = branchOf(a) || branchOf(b) ? ' br' : '';
      const ln = 'x1="' + A.x + '" y1="' + A.y + '" x2="' + B.x + '" y2="' + B.y + '"';
      if (gate.has(a + '>' + b) || (a.startsWith('p') || b.startsWith('p'))) { f += '<line class="vb-ferry" ' + ln + '/>'; return; }
      o += '<line class="vb-road-o' + br + '" ' + ln + '/>';
      r += '<line class="vb-road' + br + '" ' + ln + '/>';
    });
    // стрілочки руху на кільці — новачок бачить, куди «вперед»
    let arr = '';
    m.edges.forEach(([a, b], k) => {
      if (k % 3) return;
      const A = D.byId[a], B = D.byId[b];
      const mx = (A.x + B.x) / 2, my = (A.y + B.y) / 2, ang = Math.atan2(B.y - A.y, B.x - A.x) * 180 / Math.PI;
      arr += '<path class="vb-arrow" transform="translate(' + mx + ' ' + my + ') rotate(' + ang + ')" d="M-6 -6 L4 0 L-6 6"/>';
    });
    h += '<g class="vb-roads">' + o + r + f + arr + '</g><g class="vb-tiles">';
    m.nodes.forEach((n) => {
      h += '<g class="vt vt-' + n.type + (n.zone ? ' z-' + n.zone : '') + (n.type === 'coin' && (n.v || 0) > 3 ? ' rich' : '')
        + '" data-n="' + n.id + '" transform="translate(' + n.x + ' ' + n.y + ')"><circle class="vt-bg" r="30"/><circle class="vt-in" r="24"/>'
        + '<text class="vt-ic" dy=".35em">' + tileLabel(n) + '</text>'
        + (n.name ? '<text class="vt-nm" y="50">' + st.ctx.esc(n.name) + '</text>' : '') + '</g>';
    });
    h += '</g><g class="vb-hl"></g><g class="vb-gates"></g><g class="vb-stand"></g><g class="vb-toks"></g><g class="vb-fx"></g>';
    st.svg.innerHTML = h;
    st.g = {
      hl: st.svg.querySelector('.vb-hl'), gates: st.svg.querySelector('.vb-gates'), stand: st.svg.querySelector('.vb-stand'),
      toks: st.svg.querySelector('.vb-toks'), fx: st.svg.querySelector('.vb-fx'),
    };
    st.toks = new Map();
    st.standShown = null;
    st.gatesKey = '';
  }

  function svgEl(tag, attrs, parent) {
    const e = document.createElementNS(SVGNS, tag);
    for (const k in attrs) e.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(e);
    return e;
  }

  // ---------- камера (viewBox) ----------
  /// Масштаб «уся карта влазить» × zoom; центр — (x, y) у координатах карти.
  function camBox(st, c) {
    const m = st.data.map;
    const W = Math.max(1, st.stage.clientWidth), H = Math.max(1, st.stage.clientHeight);
    const s0 = Math.min(W / m.w, H / m.h);
    const vw = W / (s0 * c.z), vh = H / (s0 * c.z);
    // за край карти — не далі ніж на півклітинки: дошка не «тікає»
    const pad = 60;
    const x = vw >= m.w + pad * 2 ? m.w / 2 : clamp(c.x, vw / 2 - pad, m.w - vw / 2 + pad);
    const y = vh >= m.h + pad * 2 ? m.h / 2 : clamp(c.y, vh / 2 - pad, m.h - vh / 2 + pad);
    return { x, y, vw, vh, s: s0 * c.z };
  }
  function applyCam(st) {
    const b = camBox(st, st.cam);
    st.cam.x = b.x; st.cam.y = b.y;
    st.svg.setAttribute('viewBox', (b.x - b.vw / 2).toFixed(1) + ' ' + (b.y - b.vh / 2).toFixed(1) + ' ' + b.vw.toFixed(1) + ' ' + b.vh.toFixed(1));
    st.svg.classList.toggle('near', st.cam.z >= 1.4);
    st.camBox = b;
  }
  const defZoom = (st) => (isPhone(st) ? 2.2 : 1);
  function focusOn(st, x, y, z) {
    st.camT = { x, y, z: z == null ? st.camT.z : z };
    kick(st);
  }
  /// Точка екрана → координати карти.
  function toMap(st, cx, cy) {
    const r = st.svg.getBoundingClientRect(), b = st.camBox || camBox(st, st.cam);
    return { x: b.x - b.vw / 2 + (cx - r.left) / b.s, y: b.y - b.vh / 2 + (cy - r.top) / b.s };
  }

  function bindCamera(st) {
    const svg = st.svg;
    const ptrs = new Map();
    let drag = null, pinch = null, moved = false;
    svg.addEventListener('pointerdown', (e) => {
      ptrs.set(e.pointerId, { x: e.clientX, y: e.clientY });
      moved = false;
      if (ptrs.size === 1) drag = { x: e.clientX, y: e.clientY, cx: st.cam.x, cy: st.cam.y };
      if (ptrs.size === 2) {
        const [a, b] = [...ptrs.values()];
        pinch = { d: Math.hypot(a.x - b.x, a.y - b.y), z: st.cam.z };
        drag = null;
      }
    });
    svg.addEventListener('pointermove', (e) => {
      if (!ptrs.has(e.pointerId)) return;
      ptrs.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (pinch && ptrs.size >= 2) {
        const [a, b] = [...ptrs.values()];
        const z = clamp(pinch.z * Math.hypot(a.x - b.x, a.y - b.y) / Math.max(10, pinch.d), 0.8, 4);
        st.cam.z = st.camT.z = z; st.follow = false; moved = true; applyCam(st); showFocusBtn(st);
      } else if (drag) {
        const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
        if (!moved && Math.hypot(dx, dy) < 8) return;
        moved = true;
        if (!svg.hasPointerCapture(e.pointerId)) { try { svg.setPointerCapture(e.pointerId); } catch (er) { /* нічого */ } }
        const s = (st.camBox || camBox(st, st.cam)).s;
        st.cam.x = drag.cx - dx / s; st.cam.y = drag.cy - dy / s;
        st.camT.x = st.cam.x; st.camT.y = st.cam.y; st.follow = false;
        applyCam(st); showFocusBtn(st);
      }
    });
    const up = (e) => {
      const was = ptrs.has(e.pointerId);
      ptrs.delete(e.pointerId);
      if (ptrs.size < 2) pinch = null;
      if (!ptrs.size) {
        drag = null;
        if (was && !moved && e.type === 'pointerup') boardTap(st, e);
      }
    };
    svg.addEventListener('pointerup', up);
    svg.addEventListener('pointercancel', up);
    svg.addEventListener('wheel', (e) => {
      e.preventDefault();
      const p = toMap(st, e.clientX, e.clientY);
      const z = clamp(st.cam.z * (e.deltaY < 0 ? 1.15 : 1 / 1.15), 0.8, 4);
      // зум до курсора: точка під мишкою лишається на місці
      const k = st.cam.z / z;
      st.cam.x = p.x + (st.cam.x - p.x) * k; st.cam.y = p.y + (st.cam.y - p.y) * k; st.cam.z = z;
      st.camT = { x: st.cam.x, y: st.cam.y, z };
      st.follow = z > 1.05 ? false : st.follow;
      applyCam(st); showFocusBtn(st);
    }, { passive: false });
  }
  function showFocusBtn(st) { if (st.el.focus) st.el.focus.hidden = st.follow; }

  // ---------- фішки ----------
  function nodeXY(st, id) { const n = st.data.byId[id]; return n ? { x: n.x, y: n.y } : { x: 0, y: 0 }; }

  function ensureTok(st, p) {
    let t = st.toks.get(p.i);
    const ava = avaOf(p);
    const key = (ava.img || ava.txt) + '|' + p.color;
    if (t && t.key === key) return t;
    if (!t) {
      const pos = nodeXY(st, p.pos);
      t = { i: p.i, node: p.pos, x: pos.x, y: pos.y, q: [], hop: null, ox: 0, oy: 0 };
      t.g = svgEl('g', { class: 'tok', 'data-i': p.i }, st.g.toks);
      st.toks.set(p.i, t);
    }
    t.key = key;
    const h = hue(p);
    t.g.innerHTML = '<ellipse class="tk-sh" rx="17" ry="6" cy="24"/><g class="tk-b"><circle class="tk-pulse" r="27" style="stroke:hsl(' + h + ' 85% 55%)"/>'
      + '<circle class="tk-bg" r="22" style="fill:hsl(' + h + ' 55% 42%);stroke:hsl(' + h + ' 85% 62%)"/>'
      + (ava.img ? '<image href="' + st.ctx.esc(ava.img) + '" x="-20" y="-20" width="40" height="40" clip-path="url(#vc' + st.uid + ')" preserveAspectRatio="xMidYMid slice"/>'
        : '<text class="tk-t" dy=".36em">' + st.ctx.esc(ava.txt) + '</text>')
      + '</g><text class="tk-st" x="17" y="-15"></text><text class="tk-n" y="-36"></text>';
    t.st = t.g.querySelector('.tk-st');
    t.n = t.g.querySelector('.tk-n');
    return t;
  }

  /// Вид прийшов: фішки їдуть туди, де їх поставив сервер. walk — по кроках шляху, решта змін — стрибком-дугою.
  function syncTokens(st, v, fresh) {
    const seen = new Set();
    const a = v.anim;
    (v.players || []).forEach((p) => {
      seen.add(p.i);
      const t = ensureTok(st, p);
      t.p = p;
      t.g.classList.toggle('cur', v.cur === p.i && (v.phase === 'turn' || v.phase === 'aim' || v.phase === 'walk' || v.phase === 'prompt'));
      t.g.classList.toggle('away', !!(p.away || p.auto));
      t.st.textContent = (p.bump ? '🤕' : '') + (p.charm ? '🧿' : '') + (p.auto ? '💤' : '');
      const end = t.q.length ? t.q[t.q.length - 1].to : t.node;
      if (st.first) { t.q = []; t.hop = null; t.node = p.pos; const xy = nodeXY(st, p.pos); t.x = xy.x; t.y = xy.y; return; }
      if (fresh && a && a.kind === 'walk' && a.who === p.i && a.path && a.path.length) {
        a.path.forEach((n) => { if (n !== (t.q.length ? t.q[t.q.length - 1].to : t.node)) t.q.push({ to: n, ms: STEP_MS, h: 20 }); });
      }
      const end2 = t.q.length ? t.q[t.q.length - 1].to : t.node;
      if (end2 !== p.pos && end === end2) t.q.push({ to: p.pos, ms: HOP_MS, h: 140, fly: true });
    });
    for (const [i, t] of st.toks) if (!seen.has(i)) { t.g.remove(); st.toks.delete(i); }
    kick(st);
  }

  /// Хто на тій самій клітинці й не йде — віялом (до 8: колом r=18).
  function fanOut(st) {
    const by = {};
    for (const t of st.toks.values()) if (!t.hop && !t.q.length) (by[t.node] = by[t.node] || []).push(t);
    for (const id in by) {
      const arr = by[id].sort((a, b) => a.i - b.i);
      arr.forEach((t, k) => {
        if (arr.length < 2) { t.ox = 0; t.oy = 0; return; }
        const ang = -Math.PI / 2 + k * 2 * Math.PI / arr.length;
        t.ox = Math.cos(ang) * 19; t.oy = Math.sin(ang) * 19;
      });
    }
  }

  // ---------- літучі (предмети, глек) і спливашки ----------
  function flyer(st, txt, from, to, ms, h, cls) {
    const g = svgEl('g', { class: 'vf-fly ' + (cls || '') }, st.g.fx);
    const t = svgEl('text', { dy: '.35em' }, g);
    t.textContent = txt;
    st.flyers.push({ g, from, to, t0: now(), ms, h });
    kick(st);
  }
  function floatAt(st, x, y, txt, cls) {
    const g = svgEl('g', { class: 'vf-float ' + (cls || ''), transform: 'translate(' + x + ' ' + (y - 30) + ')' }, st.g.fx);
    const t = svgEl('text', {}, g);
    t.textContent = txt;
    setTimeout(() => g.remove(), 1700);
  }
  function tokXY(st, i) { const t = st.toks.get(i); return t ? { x: t.x + t.ox, y: t.y + t.oy } : null; }
  function burst(st, x, y, n) {
    const g = svgEl('g', { class: 'vf-burst', transform: 'translate(' + x + ' ' + y + ')' }, st.g.fx);
    for (let k = 0; k < (n || 14); k++) {
      const a = k / (n || 14) * Math.PI * 2, d = 46 + (k % 3) * 14;
      const c = svgEl('circle', { r: 4 + (k % 2) * 2, style: '--dx:' + (Math.cos(a) * d).toFixed(1) + 'px;--dy:' + (Math.sin(a) * d).toFixed(1) + 'px;fill:hsl(' + (40 + k * 23) + ' 90% 60%)' }, g);
      c.setAttribute('class', 'vf-spark');
    }
    setTimeout(() => g.remove(), 1300);
  }

  // ---------- rAF: камера, кроки, літучі, стрілка до лавки. Спить, коли нічого не рухається ----------
  function kick(st) {
    if (st.raf || st.dead) return;
    st.raf = requestAnimationFrame((ts) => frameLoop(st, ts));
  }
  function frameLoop(st) {
    st.raf = 0;
    if (st.dead || !st.data || !st.svg.isConnected) return;
    let busy = false;
    const t = now();
    // фішки
    for (const k of st.toks.values()) {
      if (!k.hop && k.q.length) {
        const s = k.q.shift();
        const a = { x: k.x, y: k.y }, b = nodeXY(st, s.to);
        k.hop = { a, b, t0: t, ms: reduced() ? 1 : s.ms, h: s.h, to: s.to, fly: s.fly };
        k.ox = 0; k.oy = 0;
      }
      if (k.hop) {
        busy = true;
        const f = clamp((t - k.hop.t0) / k.hop.ms, 0, 1);
        const e = k.hop.fly ? (f < 0.5 ? 2 * f * f : 1 - Math.pow(-2 * f + 2, 2) / 2) : f;
        k.x = k.hop.a.x + (k.hop.b.x - k.hop.a.x) * e;
        k.y = k.hop.a.y + (k.hop.b.y - k.hop.a.y) * e;
        const lift = Math.sin(Math.PI * f) * k.hop.h;
        k.g.setAttribute('transform', 'translate(' + k.x.toFixed(1) + ' ' + (k.y - lift).toFixed(1) + ')');
        k.n.textContent = k.q.length && !k.hop.fly ? String(k.q.length) : '';
        if (f >= 1) {
          k.node = k.hop.to; k.hop = null;
          if (!k.q.length) { k.n.textContent = ''; fanOut(st); }
        }
      }
    }
    fanOut(st);
    for (const k of st.toks.values()) {
      if (k.hop) continue;
      const tx = k.x + k.ox, ty = k.y + k.oy;
      k.g.setAttribute('transform', 'translate(' + tx.toFixed(1) + ' ' + ty.toFixed(1) + ')');
    }
    // літучі
    st.flyers = st.flyers.filter((fl) => {
      const f = clamp((t - fl.t0) / fl.ms, 0, 1);
      const a = typeof fl.from === 'function' ? fl.from() : fl.from, b = typeof fl.to === 'function' ? fl.to() : fl.to;
      if (!a || !b) { fl.g.remove(); return false; }
      const e = f < 0.5 ? 2 * f * f : 1 - Math.pow(-2 * f + 2, 2) / 2;
      const x = a.x + (b.x - a.x) * e, y = a.y + (b.y - a.y) * e - Math.sin(Math.PI * f) * fl.h;
      fl.g.setAttribute('transform', 'translate(' + x.toFixed(1) + ' ' + y.toFixed(1) + ') scale(' + (1 + Math.sin(Math.PI * f) * 0.6).toFixed(2) + ')');
      if (f >= 1) { fl.g.remove(); if (fl.done) fl.done(); return false; }
      busy = true;
      return true;
    });
    // камера: стежимо за фішкою того, хто ходить
    if (st.follow && st.followI != null) {
      const k = st.toks.get(st.followI);
      if (k) { st.camT.x = k.x; st.camT.y = k.y; }
    }
    const c = st.cam, g = st.camT;
    const dx = g.x - c.x, dy = g.y - c.y, dz = g.z - c.z;
    if (Math.abs(dx) + Math.abs(dy) > 0.5 || Math.abs(dz) > 0.002) {
      const k = reduced() ? 1 : 0.14;
      c.x += dx * k; c.y += dy * k; c.z += dz * k;
      busy = true;
    }
    applyCam(st);
    standArrow(st);
    if (busy) st.raf = requestAnimationFrame(() => frameLoop(st));
  }

  /// Лавка поза кадром — стрілка-вказівник на краю дошки в її бік (§15.2).
  function standArrow(st) {
    const el = st.el.arrow;
    const n = st.v && st.v.stand && st.data.byId[st.v.stand];
    if (!n || !st.camBox || st.mgOn) { el.hidden = true; return; }
    const b = st.camBox;
    const left = b.x - b.vw / 2, top = b.y - b.vh / 2;
    const inX = n.x > left + 20 && n.x < left + b.vw - 20, inY = n.y > top + 20 && n.y < top + b.vh - 20;
    if (inX && inY) { el.hidden = true; return; }
    el.hidden = false;
    const W = st.stage.clientWidth, H = st.stage.clientHeight;
    const sx = (n.x - left) * b.s, sy = (n.y - top) * b.s;
    const cx = W / 2, cy = H / 2, ang = Math.atan2(sy - cy, sx - cx);
    const kx = (W / 2 - 34) / Math.max(1, Math.abs(Math.cos(ang))), ky = (H / 2 - 34) / Math.max(1, Math.abs(Math.sin(ang)));
    const r = Math.min(kx, ky);
    el.style.transform = 'translate(' + (cx + Math.cos(ang) * r - 22).toFixed(0) + 'px,' + (cy + Math.sin(ang) * r - 22).toFixed(0) + 'px)';
    el.querySelector('i').style.transform = 'rotate(' + (ang * 180 / Math.PI).toFixed(0) + 'deg)';
  }

  // ---------- лавка Глека й шлагбауми ----------
  function drawStand(st, v) {
    const id = v.stand;
    if (!id || !st.data.byId[id]) { st.g.stand.innerHTML = ''; st.standShown = null; return; }
    if (st.standShown === id + ':' + v.price) return;
    const old = st.standShown && st.standShown.split(':')[0];
    st.standShown = id + ':' + v.price;
    const n = st.data.byId[id];
    const html = '<g class="vs' + (v.price < 20 ? ' sale' : '') + '" transform="translate(' + n.x + ' ' + n.y + ')"><circle class="vs-glow" r="58" fill="url(#vg' + st.uid + ')"/>'
      + '<g class="vs-rays"><path d="M0 -62 L5 -40 L-5 -40z M0 62 L5 40 L-5 40z M-62 0 L-40 5 L-40 -5z M62 0 L40 5 L40 -5z"/></g>'
      + '<text class="vs-jar" dy=".35em">🏺</text><g class="vs-tag" transform="translate(30 -30)"><rect x="-18" y="-12" width="36" height="24" rx="12"/>'
      + '<text dy=".35em">' + v.price + '</text></g></g>';
    if (old && old !== id && !st.first && st.data.byId[old]) {
      // переїзд лавки: глек злітає дугою й падає на новий стенд
      st.g.stand.innerHTML = '';
      const a = st.data.byId[old];
      const fl = { g: svgEl('g', { class: 'vf-fly big' }, st.g.fx), from: { x: a.x, y: a.y }, to: { x: n.x, y: n.y }, t0: now() + 500, ms: 1500, h: 260 };
      svgEl('text', { dy: '.35em' }, fl.g).textContent = '🏺';
      fl.done = () => { if (st.standShown && st.standShown.startsWith(id + ':')) { st.g.stand.innerHTML = html; burst(st, n.x, n.y, 10); } };
      st.flyers.push(fl);
      kick(st);
    } else st.g.stand.innerHTML = html;
  }
  function drawGates(st, v) {
    const gs = v.gates;
    if (!gs) return;                                  // у фазі mg не шлють — лишаємо, що було
    const key = JSON.stringify(gs);
    if (key === st.gatesKey) return;
    const was = new Set(JSON.parse(st.gatesKey || '[]').map((g) => g.node));
    st.gatesKey = key;
    st.g.gates.innerHTML = gs.map((g) => {
      const n = st.data.byId[g.node];
      const p = (v.players || []).find((x) => x.i === g.owner);
      if (!n) return '';
      return '<g class="vgate' + (was.has(g.node) || st.first ? '' : ' new') + '" transform="translate(' + (n.x + 22) + ' ' + (n.y - 24) + ')">'
        + '<rect x="-16" y="-9" width="32" height="18" rx="4" style="stroke:hsl(' + hue(p) + ' 80% 55%)"/><text dy=".35em">🚧</text></g>';
    }).join('');
  }

  /// Підсвітка цілей прицілювання / варіантів (вузли).
  function drawHl(st, nodes, cls) {
    st.g.hl.innerHTML = (nodes || []).map((id) => {
      const n = st.data.byId[id];
      return n ? '<circle class="vhl ' + (cls || '') + '" data-n="' + id + '" cx="' + n.x + '" cy="' + n.y + '" r="38"/>' : '';
    }).join('');
  }

  // ---------- каркас DOM ----------
  function shell(root, st) {
    root.innerHTML = '<div class="vch">'
      + '<div class="vch-top"></div>'
      + '<div class="vch-main"><div class="vch-stage"><div class="vch-bw">'
      + '<svg class="vch-board" xmlns="http://www.w3.org/2000/svg" preserveAspectRatio="xMidYMid meet" viewBox="0 0 1600 1000"></svg>'
      + '<div class="vch-mg" hidden></div><div class="vch-dice" hidden></div><div class="vch-ban" hidden></div>'
      + '<div class="vch-say" hidden></div><div class="vch-ov" hidden></div>'
      + '<button type="button" class="vch-focus" hidden title="До фішки, що ходить">🎯</button>'
      + '<div class="vch-arrow" hidden><i>➤</i><b>🏺</b></div><div class="vch-wait"><span class="spin"></span> розкладаємо село…</div>'
      + '</div><div class="vch-dock"></div></div>'
      + '<aside class="vch-side"><div class="vch-pl"></div><div class="vch-goal"></div><div class="vch-emo"></div>'
      + '<div class="vch-glek"></div><div class="vch-log"></div></aside></div>'
      + '<div class="vch-pop" hidden></div></div>';
    const q = (s) => root.querySelector(s);
    st.el = {
      vch: q('.vch'), top: q('.vch-top'), stage: q('.vch-stage'), bw: q('.vch-bw'), mg: q('.vch-mg'), dice: q('.vch-dice'),
      ban: q('.vch-ban'), say: q('.vch-say'), ov: q('.vch-ov'), focus: q('.vch-focus'), arrow: q('.vch-arrow'), wait: q('.vch-wait'),
      dock: q('.vch-dock'), side: q('.vch-side'), pl: q('.vch-pl'), goal: q('.vch-goal'), emo: q('.vch-emo'), glek: q('.vch-glek'),
      log: q('.vch-log'), pop: q('.vch-pop'),
    };
    st.svg = q('.vch-board');
    st.stage = st.el.bw;
    st.el.focus.onclick = () => { st.follow = true; st.camT.z = Math.max(st.camT.z, defZoom(st)); showFocusBtn(st); kick(st); };
    st.el.emo.innerHTML = Object.keys(EMO).map((k) => '<button type="button" data-emo="' + k + '" title="' + k + '">' + EMO[k] + '</button>').join('');
    root.addEventListener('click', (e) => onClick(st, e));
    st.ro = new ResizeObserver(() => { layout(st); if (st.data) { applyCam(st); kick(st); } });
    st.ro.observe(st.el.bw);
  }

  /// Ширина картки: телефон (< 760), середня (ПК поруч із балачкою) чи широка. Висота дошки на ПК — під ширину,
  /// щоб уся карта (1600×1000) влазила без порожніх смуг, але не вище за 78 % екрана.
  function layout(st) {
    const W = st.root.clientWidth;
    if (!W) return;
    const phone = W < 760;
    const was = st.el.vch.classList.contains('phone');
    st.el.vch.classList.toggle('phone', phone);
    st.el.vch.classList.toggle('mid', !phone && W < 1100);
    // перейшли телефон ↔ ПК (або перший замір після mount, коли ширина ще була 0) — типовий зум і стеження
    if (was !== phone || !st.laidOut) { st.laidOut = true; st.camT.z = defZoom(st); st.follow = true; if (st.data) kick(st); }
    if (phone) {
      // дошка + шторка мають влізти в екран під шапкою сайту: кнопка «Кинути» — без прокрутки
      const top = st.root.getBoundingClientRect().top + window.scrollY;
      const h = Math.round(clamp(window.innerHeight - top - 250, 230, 480));
      if (st.lastH !== h) { st.lastH = h; st.el.vch.style.setProperty('--vch-h', h + 'px'); }
      return;
    }
    const bw = st.el.bw.clientWidth || W - 250;
    const h = Math.round(clamp(bw * 0.625 + 110, 400, Math.min(window.innerHeight * 0.78, 900)));
    if (st.lastH !== h) { st.lastH = h; st.el.vch.style.setProperty('--vch-h', h + 'px'); }
  }

  function later(st, fn, ms) {
    const id = setTimeout(() => { st.timers.delete(id); if (!st.dead) fn(); }, ms);
    st.timers.add(id);
    return id;
  }

  // ---------- події виду: анімації, ефекти, реакції, Глек ----------
  const nameOf = (st, i) => { const p = (st.v.players || []).find((x) => x.i === i); return p ? p.name : '?'; };
  const playerOf = (v, i) => (v.players || []).find((x) => x.i === i) || null;
  const mineI = (v) => (v.you && v.you.i != null ? v.you.i : null);

  function banner(st, html, ms, cls) {
    const b = st.el.ban;
    b.className = 'vch-ban ' + (cls || '');
    b.innerHTML = html;
    b.hidden = false;
    void b.offsetWidth;
    b.classList.add('on');
    clearTimeout(st.banT);
    st.banT = later(st, () => { b.classList.remove('on'); later(st, () => { b.hidden = true; }, 300); }, ms || 1600);
  }

  const PIPS = { 1: [4], 2: [0, 8], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };
  const face = (n) => Array.from({ length: 9 }, (_, k) => '<i' + (PIPS[n].includes(k) ? ' class="on"' : '') + '></i>').join('');
  function showDice(st, dice) {
    const el = st.el.dice;
    if (!dice || !dice.length) return;
    clearInterval(st.diceI);
    el.hidden = false;
    el.className = 'vch-dice';
    el.innerHTML = dice.map(() => '<div class="vdie roll">' + face(1 + Math.floor(Math.random() * 6)) + '</div>').join('') + '<b class="vdsum"></b>';
    const ds = [...el.querySelectorAll('.vdie')];
    const t0 = now();
    st.diceI = setInterval(() => {
      if (now() - t0 > 900 || st.dead) {
        clearInterval(st.diceI);
        ds.forEach((d, k) => { d.innerHTML = face(dice[k]); d.classList.remove('roll'); d.classList.add('done'); });
        const dbl = dice.length === 2 && dice[0] === dice[1];
        el.querySelector('.vdsum').textContent = (dice.length > 1 ? '= ' + dice.reduce((a, b) => a + b, 0) : '') + (dbl ? ' · дубль! +2 🪙' : '');
        el.classList.add('done');
        return;
      }
      ds.forEach((d) => { d.innerHTML = face(1 + Math.floor(Math.random() * 6)); });
    }, 85);
    st.timers.add(st.diceI);
    clearTimeout(st.diceT);
    st.diceT = later(st, () => { el.classList.add('out'); later(st, () => { el.hidden = true; }, 350); }, DICE_MS + 700);
  }

  function onAnim(st, v) {
    const a = v.anim;
    if (!a) return;
    if (st.first || st.animSeq == null) { st.animSeq = a.seq; if (a.who != null) st.followI = a.who; return; }
    if (a.seq <= st.animSeq) return;
    st.animSeq = a.seq;
    const esc = st.ctx.esc;
    if (a.who != null && ['turn', 'dice', 'walk', 'land', 'event', 'buy', 'fly', 'gate', 'item'].includes(a.kind)) st.followI = a.who;
    switch (a.kind) {
      case 'turn': {
        const p = playerOf(v, a.who);
        if (!p) break;
        const me = mineI(v) === a.who;
        banner(st, me ? '🎲 Твій хід!' : 'Хід: ' + avaHtml(p, esc, 'sm') + ' <b>' + esc(p.name) + '</b>', 1300, me ? 'mine' : '');
        if (me && navigator.vibrate && document.visibilityState === 'visible' && (!navigator.userActivation || navigator.userActivation.hasBeenActive)) { try { navigator.vibrate(80); } catch (e) { /* нема */ } }
        break;
      }
      case 'dice': showDice(st, a.dice); break;
      case 'land': {
        const p = playerOf(v, a.who);
        const tile = p && st.svg.querySelector('.vt[data-n="' + p.pos + '"]');
        if (tile) { tile.classList.remove('flash'); void tile.getBBox(); tile.classList.add('flash'); later(st, () => tile.classList.remove('flash'), 1300); }
        break;
      }
      case 'event': {
        const e = (T(st).events || {})[a.key] || { title: a.key, icon: '?', text: '' };
        banner(st, '<div class="vev"><div class="vev-ic">' + esc(e.icon || '?') + '</div><b>' + esc(e.title) + '</b><p>' + esc(e.text || '') + '</p></div>',
          2500, 'event' + (a.key === 'wheel' ? ' wheel' : ''));
        break;
      }
      case 'buy': {
        const n = st.data.byId[a.key];
        if (n) {
          const fl = { g: svgEl('g', { class: 'vf-fly big' }, st.g.fx), from: { x: n.x, y: n.y }, to: () => tokXY(st, a.who), t0: now(), ms: 900, h: 120 };
          svgEl('text', { dy: '.35em' }, fl.g).textContent = '🏺';
          fl.done = () => { const xy = tokXY(st, a.who); if (xy) burst(st, xy.x, xy.y, 18); };
          st.flyers.push(fl);
          kick(st);
        }
        banner(st, '🏺 <b>' + esc(nameOf(st, a.who)) + '</b> купує золотий глек!', 1800, 'gold');
        break;
      }
      case 'fly': {
        const xy = tokXY(st, a.who);
        if (xy) floatAt(st, xy.x, xy.y - 20, itemIc(st, a.key), 'big');
        break;
      }
      case 'item': {
        if (a.to != null) {
          flyer(st, itemIc(st, a.key), () => tokXY(st, a.who), () => tokXY(st, a.to), 1000, 90, 'big');
          banner(st, esc(nameOf(st, a.who)) + ' → ' + esc(nameOf(st, a.to)) + ': ' + itemIc(st, a.key) + ' ' + esc(itemTx(st, a.key).name), 1400);
        } else {
          const xy = tokXY(st, a.who);
          if (xy) floatAt(st, xy.x, xy.y - 20, itemIc(st, a.key), 'big');
        }
        break;
      }
      case 'order': {
        (a.dice || []).forEach((r, i) => { const xy = tokXY(st, i); if (xy) later(st, () => floatAt(st, xy.x, xy.y - 10, '🎲 ' + r, 'big slow'), i * 220); });
        break;
      }
      case 'late': {
        const L = T(st).late || { title: '🌙 Пізній вечір', text: '' };
        banner(st, '<div class="vev"><div class="vev-ic">🌙</div><b>' + esc(L.title) + '</b><p>' + esc(L.text || '') + '</p></div>', 3200, 'event late');
        break;
      }
      default: break;
    }
  }

  function onFx(st, v) {
    if (!v.fx) return;
    const seen = st.fxSeen;
    const cnt = {};
    const keys = v.fx.map((f) => { const b = f.seq + '|' + f.kind + '|' + f.who + '|' + f.d + '|' + (f.k || ''); cnt[b] = (cnt[b] || 0) + 1; return b + '#' + cnt[b]; });
    if (st.first || !st.fxInit) { st.fxInit = true; keys.forEach((k) => seen.add(k)); return; }
    let n = 0;
    v.fx.forEach((f, k) => {
      if (seen.has(keys[k])) return;
      seen.add(keys[k]);
      later(st, () => fxOne(st, f), 120 + (n++) * 170);
    });
    if (seen.size > 200) { const keep = new Set(keys); for (const k of seen) if (!keep.has(k)) seen.delete(k); }
  }
  function fxOne(st, f) {
    const xy = tokXY(st, f.who);
    pulseChip(st, f.who, f.kind === 'coins' || f.kind === 'mg' ? (f.d >= 0 ? 'up' : 'down') : 'up');
    if (!xy) return;
    switch (f.kind) {
      case 'coins': case 'mg': if (f.d) floatAt(st, xy.x, xy.y, (f.d > 0 ? '+' : '−') + Math.abs(f.d) + ' 🪙', f.d > 0 ? 'good' : 'bad'); break;
      case 'item': floatAt(st, xy.x, xy.y, '+' + itemIc(st, f.k), 'good'); break;
      case 'bump': floatAt(st, xy.x, xy.y, '🤕 гуля!', 'bad'); break;
      case 'charm': floatAt(st, xy.x, xy.y, '🧿 відбив!', 'good'); break;
      case 'glek': floatAt(st, xy.x, xy.y, '+' + (f.d || 1) + ' 🏺', 'gold big'); burst(st, xy.x, xy.y, 16); break;
      default: break;
    }
  }
  function pulseChip(st, i, cls) {
    const row = st.el.pl.querySelector('[data-i="' + i + '"]');
    if (!row) return;
    row.classList.remove('up', 'down');
    void row.offsetWidth;
    row.classList.add(cls);
    later(st, () => row.classList.remove(cls), 900);
  }

  function onEmo(st, v) {
    const list = v.emo || [];
    const max = list.reduce((m, e) => Math.max(m, e.seq), 0);
    if (st.first || st.emoSeq == null) { st.emoSeq = max; return; }
    list.filter((e) => e.seq > st.emoSeq).forEach((e) => {
      const xy = !st.mgOn && tokXY(st, e.i);
      if (xy) floatAt(st, xy.x, xy.y - 26, EMO[e.k] || '🙂', 'emo');
      const row = st.el.pl.querySelector('[data-i="' + e.i + '"]');
      if (row) {
        const s = document.createElement('span');
        s.className = 'vemo-fly';
        s.textContent = EMO[e.k] || '🙂';
        row.appendChild(s);
        later(st, () => s.remove(), 1500);
      }
    });
    st.emoSeq = Math.max(st.emoSeq, max);
  }

  // Голос Глека: url грає, якщо не вимкнено в себе (🔇, localStorage vechirka-mute). Репліка без url — лише текст.
  const muted = () => { try { return localStorage.getItem('vechirka-mute') === '1'; } catch (e) { return false; } };
  function onSay(st, v) {
    const s = v.say;
    if (!s) return;
    st.el.glek.innerHTML = '<span class="vg-ic">🏺</span><span><b>Дядько Глек:</b> ' + st.ctx.esc(s.text) + '</span>';
    if (st.sayId === s.id && (st.sayUrl || !s.url)) return;
    const fresh = st.sayId !== s.id;
    st.sayId = s.id;
    if (fresh && !st.first) {
      const b = st.el.say;
      b.innerHTML = '<span class="vg-ic">🏺</span><span>' + st.ctx.esc(s.text) + '</span>';
      b.hidden = false;
      b.classList.remove('on'); void b.offsetWidth; b.classList.add('on');
      clearTimeout(st.sayT);
      st.sayT = later(st, () => { b.classList.remove('on'); later(st, () => { b.hidden = true; }, 300); }, Math.max(3500, s.text.length * 60));
    }
    if (s.url && st.sayUrl !== s.url && !st.first) {
      st.sayUrl = s.url;
      if (!muted()) {
        try {
          if (!st.audio) st.audio = new Audio();
          st.audio.src = s.url;
          const pr = st.audio.play();
          if (pr && pr.catch) pr.catch(() => { /* автозапуск заборонено — лише текст */ });
        } catch (e) { /* без звуку */ }
      }
    }
  }

  // ---------- HUD: шапка, гравці, мета, журнал ----------
  const isHost = (st) => {
    const r = st.ctx.room, me = st.ctx.me;
    return !!(r && me && me.nick && String(r.host || '').toLowerCase() === String(me.nick).toLowerCase());
  };
  function renderTop(st, v) {
    const esc = st.ctx.esc;
    const R = v.rounds || 0, r = Math.min(v.round || 0, R);
    const host = isHost(st) && BOARD_PHASES.has(v.phase) && v.phase !== 'intro';
    const mgNow = v.phase === 'mg' && v.mg ? '<span class="vt-now">🎮 ' + esc(v.mg.title) + (v.mg.duel ? ' · ⚔ дуель' : '') + (v.mg.x2 ? ' · ×2' : '') + '</span>' : '';
    const h = '<span class="vt-r">Коло <b>' + r + '</b>/' + R + '</span>'
      + (v.late ? '<span class="vt-late" title="Пізній вечір: монетки й пастки ×2">🌙</span>' : '')
      + (v.lastGame ? '<span class="vt-x2" title="Остання забава: виплати ×2">×2</span>' : '')
      + mgNow
      + '<span class="vt-sp"></span>'
      + '<span class="vt-chip' + (v.price < 20 ? ' sale' : '') + '" title="Ціна золотого глека' + (v.price < 20 ? ' — розпродаж!' : '') + '">🏺 ' + (v.price || 20) + '</span>'
      + '<span class="vt-chip" title="Скарбничка 🐷: стань на неї — забереш до 15">🐷 ' + (v.bank || 0) + '</span>'
      + (host ? '<button type="button" class="vt-b" data-pause="' + (v.paused === 'host' ? 0 : 1) + '" title="Пауза">' + (v.paused === 'host' ? '▶' : '⏸') + '</button>' : '')
      + '<button type="button" class="vt-b" data-mute title="Голос Глека (M)">' + (muted() ? '🔇' : '🔊') + '</button>'
      + '<button type="button" class="vt-b vt-logb" data-pop="log" title="Журнал">📜</button>'
      + '<button type="button" class="vt-b" data-pop="help" title="Як грати">❓</button>';
    if (st.topH !== h) { st.el.top.innerHTML = h; st.topH = h; }
  }

  function renderPlayers(st, v) {
    const esc = st.ctx.esc;
    const pl = v.players || [];
    const h = pl.map((p) => {
      const cur = v.cur === p.i && v.phase !== 'pick' && v.phase !== 'mg';
      const me = mineI(v) === p.i;
      return '<div class="vp' + (cur ? ' cur' : '') + (me ? ' me' : '') + (p.away || p.auto ? ' off' : '') + '" data-i="' + p.i + '" data-pop="p' + p.i + '" style="--ph:' + hue(p) + '">'
        + avaHtml(p, esc) + '<span class="vp-n">' + (cur ? '<i class="vp-cur">▶</i>' : '') + esc(p.name) + (me ? ' <i class="muted">(ти)</i>' : '')
        + (p.away ? ' <i class="muted small">🤖 за ' + esc(p.nick || p.name) + '</i>' : '') + '</span>'
        + '<span class="vp-g" title="Золоті глеки">🏺<b>' + p.gleks + '</b></span><span class="vp-c" title="Шеляги">🪙<b>' + p.coins + '</b></span>'
        + '<span class="vp-it">' + (p.items || []).map((k) => '<i title="' + esc(itemTx(st, k).name) + '">' + itemIc(st, k) + '</i>').join('')
        + (p.bump ? '<i title="Гуля: наступний хід — один кубик">🤕</i>' : '') + (p.charm ? '' : '') + '</span>'
        + '<span class="vp-pl" title="Місце">' + (p.place || '') + '</span></div>';
    }).join('');
    if (st.plH !== h) { st.el.pl.innerHTML = h; st.plH = h; }
  }

  function goalText(st, v) {
    const y = v.you;
    if (!y || y.i == null) return '';
    const p = playerOf(v, y.i);
    if (!p) return '';
    const hints = T(st).hints || {};
    if (y.toStand == null) return '🏺 Лавка Глека зараз далеко · у тебе ' + p.coins + '/' + v.price + ' 🪙';
    return fill(hints.goal || '🏺 Лавка Глека: {n} кроків · у тебе {coins}/{price} 🪙', { n: y.toStand, coins: p.coins, price: v.price });
  }

  function renderSide(st, v) {
    renderPlayers(st, v);
    const g = goalText(st, v);
    st.el.goal.innerHTML = g ? '<span>' + st.ctx.esc(g) + '</span>' : '';
    st.el.goal.hidden = !g;
    st.el.emo.hidden = !(v.you && v.you.can && v.you.can.includes('emo'));
    if (v.log) st.lastLog = v.log;
    const log = st.lastLog || [];
    const lh = '<div class="vl-h">📜 Журнал вечірки</div>' + log.slice().reverse().map((l) => '<div>' + st.ctx.esc(l) + '</div>').join('');
    if (st.logH !== lh) { st.el.log.innerHTML = lh; st.logH = lh; }
  }

  // ---------- спливні картки: гравець, клітинка, довідка, журнал ----------
  function openPop(st, html, cls) {
    const p = st.el.pop;
    p.className = 'vch-pop ' + (cls || '');
    p.innerHTML = '<div class="vpop-c"><button type="button" class="vpop-x" data-close title="Закрити">✕</button>' + html + '</div>';
    p.hidden = false;
  }
  function closePop(st) { st.el.pop.hidden = true; st.el.pop.innerHTML = ''; }

  function playerPop(st, i) {
    const v = st.v, esc = st.ctx.esc, p = playerOf(v, i);
    if (!p) return;
    const node = st.data.byId[p.pos];
    openPop(st, '<div class="vpp-h">' + avaHtml(p, esc, 'lg') + '<div><b>' + esc(p.name) + '</b><div class="muted small">'
      + (p.bot && !p.away ? 'бот вечірки' : p.away ? 'відпав — за нього грає бот' : p.auto ? 'задрімав — ходить Глек' : 'гравець') + ' · ' + p.place + '-е місце</div></div></div>'
      + '<div class="vpp-s"><span>🏺 <b>' + p.gleks + '</b> глеків</span><span>🪙 <b>' + p.coins + '</b> шелягів</span><span>🏆 <b>' + p.mgWins + '</b> перемог</span></div>'
      + (p.bump ? '<p>🤕 Гуля — наступний хід кидає один кубик.</p>' : '') + (p.charm ? '<p>🧿 Оберіг — відіб\'є наступну пакість.</p>' : '')
      + '<div class="vpp-it">' + ((p.items || []).length ? p.items.map((k) => { const t = itemTx(st, k); return '<div><b>' + t.icon + ' ' + esc(t.name) + '</b><span class="muted small">' + esc(t.long || t.short) + '</span></div>'; }).join('') : '<span class="muted">Предметів нема</span>') + '</div>'
      + (node ? '<p class="muted small">Стоїть: ' + esc(node.name || ((T(st).tiles || {})[node.type] || {}).name || node.id) + '</p>' : ''), 'pp');
  }

  function tilePop(st, id) {
    const v = st.v || {}, esc = st.ctx.esc, n = st.data.byId[id];
    if (!n) return;
    const tiles = T(st).tiles || {};
    const parts = [];
    const tt = tiles[n.type] || { name: n.type, hint: '' };
    parts.push('<div class="vtp-h"><span class="vtp-ic vt-' + n.type + '">' + tileLabel(n) + '</span><b>' + esc(n.name || tt.name) + '</b></div><p>' + esc(tt.hint || '') + '</p>');
    if (v.stand === id && tiles.stand) parts.push('<p><b>🏺 ' + esc(tiles.stand.name) + '</b> — ' + esc(fill(tiles.stand.hint, { price: v.price })) + '</p>');
    const gt = (v.gates || st.lastGates || []).find((g) => g.node === id);
    if (gt && tiles.gate) parts.push('<p><b>🚧 ' + esc(tiles.gate.name) + '</b> (' + esc(nameOf(st, gt.owner)) + ') — ' + esc(tiles.gate.hint) + '</p>');
    const shop = (v.shops || st.lastShops || {})[id];
    if (shop) parts.push('<div class="vpp-it">' + shop.map((k) => { const t = itemTx(st, k); return '<div><b>' + t.icon + ' ' + esc(t.name) + ' · ' + (PRICES[k] || '?') + ' 🪙</b><span class="muted small">' + esc(t.short) + '</span></div>'; }).join('') + '</div>');
    if (n.type === 'bank') parts.push('<p>Зараз у скарбничці: <b>' + (v.bank || 0) + ' 🪙</b></p>');
    const zone = n.zone && (T(st).zones || {})[n.zone];
    if (zone) parts.push('<p class="muted small">' + esc(zone.title) + ': ' + esc(zone.hint) + '</p>');
    const who = (v.players || []).filter((p) => p.pos === id);
    if (who.length) parts.push('<p class="muted small">Тут: ' + who.map((p) => esc(p.name)).join(', ') + '</p>');
    openPop(st, parts.join(''), 'tp');
  }

  function helpPop(st) {
    const esc = st.ctx.esc, t = T(st);
    const how = (t.howto || []).map((s) => '<div class="vh-s"><span class="vh-ic">' + esc(s.icon) + '</span><div><b>' + esc(s.title) + '</b><p>' + esc(s.text) + '</p></div></div>').join('');
    const tiles = Object.keys(t.tiles || {}).map((k) => '<div class="vh-t"><span class="vtp-ic vt-' + k + '">' + (k === 'coin' ? '+3' : k === 'trap' ? '−3' : k === 'stand' ? '🏺' : k === 'gate' ? '🚧' : TILE_IC[k] || '•') + '</span><span><b>' + esc(t.tiles[k].name) + '</b> — ' + esc(fill(t.tiles[k].hint, { price: 20 })) + '</span></div>').join('');
    const items = Object.keys(t.items || {}).map((k) => '<div class="vh-t"><span class="vh-ii">' + t.items[k].icon + '</span><span><b>' + esc(t.items[k].name) + (PRICES[k] ? ' · ' + PRICES[k] + ' 🪙' : '') + '</b> — ' + esc(t.items[k].long) + '</span></div>').join('');
    openPop(st, '<h3>🏺 Як грати</h3><div class="vh-g">' + how + '</div><h4>Клітинки</h4>' + tiles + '<h4>Предмети</h4>' + items
      + '<p class="muted small">Клавіші: Пробіл/Enter — кинути чи «так», ←/→ — вибір, 1–3 — предмет, Esc — скасувати, M — голос Глека.</p>', 'help');
  }

  // ---------- кліки ----------
  function onClick(st, e) {
    const t = e.target.closest('button, [data-pop], [data-close]');
    if (!t || !st.el.vch.contains(t)) {
      if (!st.el.pop.hidden && e.target === st.el.pop) closePop(st);
      return;
    }
    const ctx = st.ctx, d = t.dataset;
    if (d.close != null) { closePop(st); return; }
    if (d.emo) {
      if (now() - (st.emoAt || 0) < 2000) return;
      st.emoAt = now();
      ctx.act('emo', { k: d.emo });
      t.classList.add('sent'); later(st, () => t.classList.remove('sent'), 2000);
      if (st.el.dock.querySelector('.vd-emos')) st.el.dock.querySelector('.vd-emos').classList.remove('open');
      return;
    }
    if (d.mute != null) {
      try { localStorage.setItem('vechirka-mute', muted() ? '0' : '1'); } catch (er) { /* нема сховища */ }
      if (muted() && st.audio) st.audio.pause();
      st.topH = ''; renderTop(st, st.v);
      return;
    }
    if (d.pause != null) { ctx.act('pause', { on: d.pause === '1' }); return; }
    if (d.pop) {
      if (d.pop === 'help') helpPop(st);
      else if (d.pop === 'log') openPop(st, st.el.log.innerHTML, 'log');
      else if (d.pop[0] === 'p') playerPop(st, +d.pop.slice(1));
      return;
    }
    if (d.act) {
      const payload = d.p ? JSON.parse(d.p) : {};
      if (t.disabled) return;
      ctx.act(d.act, payload);
      return;
    }
    if (d.emoOpen != null) { t.parentElement.classList.toggle('open'); return; }
    if (d.join != null) { HGames.call('JoinRoom', ctx.room.id); return; }
  }

  /// Тап по дошці: у прицілюванні — ціль; інакше — картка клітинки чи гравця.
  function boardTap(st, e) {
    if (!st.v || st.mgOn) return;
    const v = st.v;
    const tokEl = e.target.closest && e.target.closest('.tok');
    const tileEl = e.target.closest && e.target.closest('.vt, .vhl');
    const aim = v.phase === 'aim' && v.aim && mineI(v) === v.aim.who ? v.aim : null;
    if (aim) {
      if (tokEl && aim.targets && aim.targets.includes(+tokEl.dataset.i)) { st.ctx.act('aim', { target: +tokEl.dataset.i }); return; }
      if (tileEl && aim.nodes && aim.nodes.includes(tileEl.dataset.n)) { st.ctx.act('aim', { node: tileEl.dataset.n }); return; }
      if (tileEl && aim.targets) {
        const who = (v.players || []).filter((p) => p.pos === tileEl.dataset.n && aim.targets.includes(p.i));
        if (who.length === 1) { st.ctx.act('aim', { target: who[0].i }); return; }
      }
    }
    if (tokEl) { playerPop(st, +tokEl.dataset.i); return; }
    if (tileEl) tilePop(st, tileEl.dataset.n);
  }

  // ---------- нижня шторка: хід, рішення, прицілювання ----------
  const J = (o) => JSON.stringify(o);
  function optBtn(esc, act, p, label, opts) {
    const o = opts || {};
    return '<button type="button" class="vopt' + (o.cls ? ' ' + o.cls : '') + '" data-act="' + act + '" data-p="' + esc(J(p)) + '"'
      + (o.off ? ' disabled' : '') + (o.title ? ' title="' + esc(o.title) + '"' : '') + (o.hl ? ' data-hl="' + esc(o.hl) + '"' : '') + '>' + label + '</button>';
  }

  function dockState(st, v) {
    const ctx = st.ctx, esc = ctx.esc, you = v.you || null, me = mineI(v), can = (you && you.can) || [];
    const t = T(st), parts = [];
    let timer = false;
    const cur = playerOf(v, v.cur);
    const mineP = me != null ? playerOf(v, me) : null;
    if (v.paused) {
      parts.push('<div class="vd-msg">⏸ ' + (v.paused === 'empty' ? esc((t.hints || {}).emptyPause || 'Чекаємо людей') : 'Пауза — чекаємо господаря') + '</div>');
    } else if (v.phase === 'turn' && cur) {
      timer = true;
      if (can.includes('roll')) {
        const items = (mineP && mineP.items) || [];
        parts.push('<div class="vd-ttl">🎲 Твій хід!' + (mineP.bump ? ' <span class="vd-warn">🤕 гуля — один кубик</span>' : '') + '</div>'
          + '<div class="vd-row">' + optBtn(esc, 'roll', {}, '🎲 Кинути', { cls: 'vd-roll primary' })
          + items.map((k, n) => {
            const tx = itemTx(st, k), ok = can.includes('item') && TURN_ITEMS.has(k);
            return optBtn(esc, 'item', { k }, '<span class="vi-ic">' + tx.icon + '</span><span class="vi-n">' + esc(tx.name) + '<small>' + (n + 1) + '</small></span>',
              { cls: 'vd-item', off: !ok, title: tx.short + (ok ? '' : ' (зараз не вжити)') });
          }).join('') + '</div>');
        if (you.tip) parts.push('<div class="vd-tip">💡 ' + esc((t.hints || {}).firstTurn || 'Кидай кубик і йди до золотого глека 🏺') + '</div>');
      } else {
        parts.push('<div class="vd-msg">' + avaHtml(cur, esc, 'sm') + ' Ходить <b>' + esc(cur.name) + '</b>…</div>');
      }
    } else if (v.phase === 'aim' && v.aim) {
      timer = true;
      const a = v.aim, tx = itemTx(st, a.item);
      if (me === a.who && can.includes('aim')) {
        let list = '';
        if (a.targets) list = a.targets.map((i) => { const p = playerOf(v, i); return p ? optBtn(esc, 'aim', { target: i }, avaHtml(p, esc, 'sm') + ' ' + esc(p.name) + ' <small>🪙' + p.coins + '</small>', { hl: 'p' + i }) : ''; }).join('');
        else if (a.nodes) list = a.nodes.map((id) => { const n = st.data.byId[id]; const tt = ((t.tiles || {})[n && n.type] || {}).name || ''; return optBtn(esc, 'aim', { node: id }, (n ? tileLabel(n) : '') + ' ' + esc((n && n.name) || tt) + ' <small>' + id + '</small>', { hl: 'n' + id }); }).join('');
        else if (a.range) for (let x = a.range[0]; x <= a.range[1]; x++) list += optBtn(esc, 'aim', { n: x }, String(x), { cls: 'vd-num' });
        parts.push('<div class="vd-ttl">' + tx.icon + ' ' + esc(tx.name) + ': ' + (a.range ? 'на скільки йдемо?' : a.nodes ? 'куди поставити?' : 'на кого?') + '</div>'
          + '<div class="vd-row vd-wrap">' + list + optBtn(esc, 'aim', {}, '✕ Скасувати', { cls: 'vd-cancel' }) + '</div>');
      } else parts.push('<div class="vd-msg">' + tx.icon + ' <b>' + esc(nameOf(st, a.who)) + '</b> цілиться: ' + esc(tx.name) + '…</div>');
    } else if (v.phase === 'prompt' && v.prompt) {
      timer = true;
      const pr = v.prompt, pt = (t.prompts || {})[pr.kind] || { title: pr.kind, hint: '' };
      const mine = me === pr.who && can.includes('pick');
      const opts = pr.options.map((o, k) => {
        const it = (T(st).items || {})[o.k];
        const sub = pr.kind === 'shop' && it ? '<small>' + esc(it.short) + '</small>' : '';
        const lab = '<span>' + esc(o.label) + (o.price != null ? ' <b class="vd-pr">' + o.price + ' 🪙</b>' : '') + '</span>' + sub;
        return optBtn(esc, 'pick', { o: k }, lab, { off: !mine || !o.ok, cls: (pr.kind === 'stand' && k === 0 ? 'primary ' : '') + (o.k === 'skip' ? 'vd-skip' : ''), hl: pr.kind === 'fork' || pr.kind === 'ferry' ? 'n' + o.k : '' });
      }).join('');
      parts.push('<div class="vd-ttl">' + esc(fill(pt.title, { price: v.price })) + (mine ? '' : ' <span class="muted">· вирішує ' + esc(nameOf(st, pr.who)) + '</span>') + '</div>'
        + (mine && pt.hint ? '<div class="vd-hint">' + esc(fill(pt.hint, { price: v.price })) + '</div>' : '')
        + '<div class="vd-row vd-wrap' + (mine ? '' : ' watch') + '">' + opts + '</div>');
    } else if (v.phase === 'walk' && cur) {
      parts.push('<div class="vd-msg">🚶 <b>' + esc(cur.name) + '</b> іде селом…</div>');
    } else if (v.phase === 'mg' && v.mg && v.mg.sub == null) {
      parts.push('<div class="vd-msg">👀 Дивишся: ' + esc(v.mg.title) + '</div>');
    }
    // повернення, «Я тут!», черга
    const meNick = ctx.me && ctx.me.nick ? String(ctx.me.nick).toLowerCase() : '';
    const awayMe = !you && meNick && (v.players || []).some((p) => p.away && String(p.nick || '').toLowerCase() === meNick);
    if (awayMe) parts.unshift('<div class="vd-row"><button type="button" class="primary vd-back" data-join>' + esc((t.hints || {}).back || '↩ Повернутись за стіл') + '</button></div>');
    if (you && you.waiting) parts.unshift('<div class="vd-hint">⏳ ' + esc((t.hints || {}).waiting || 'Ти в черзі на місце бота') + '</div>');
    if (can.includes('here')) parts.unshift('<div class="vd-row"><button type="button" class="primary vd-here" data-act="here" data-p="{}">💤 Я тут!</button><span class="muted small">Глек ходить за тебе, поки не натиснеш</span></div>');
    return { html: parts.join(''), timer };
  }

  function renderDock(st, v) {
    const ctx = st.ctx;
    const { html, timer } = dockState(st, v);
    const phone = isPhone(st);
    const goal = phone ? goalText(st, v) : '';
    const emo = phone && v.you && v.you.can && v.you.can.includes('emo');
    const full = (timer && v.until ? '<div class="vd-tm"></div>' : '') + '<div class="vd-body">' + html + '</div>'
      + (goal || emo ? '<div class="vd-foot">' + (goal ? '<span class="vd-goal">' + ctx.esc(goal) + '</span>' : '')
        + (emo ? '<div class="vd-emos"><button type="button" class="vd-emob" data-emo-open>😀</button><div class="vd-emol">'
          + Object.keys(EMO).map((k) => '<button type="button" data-emo="' + k + '">' + EMO[k] + '</button>').join('') + '</div></div>' : '') + '</div>' : '');
    const key = full + '|' + (v.until || '');
    const d = st.el.dock;
    d.hidden = (!html && !goal && !emo) || (st.mgOn && !/vd-here|vd-back/.test(html));
    d.classList.toggle('mine', /vopt|vd-here|vd-back/.test(html) && !/watch/.test(html));
    if (st.dockKey === key) return;
    const keepOpen = d.querySelector('.vd-emos.open');
    st.dockKey = key;
    if (st.arcD) { st.arcD.stop(); st.arcD = null; }
    d.innerHTML = full;
    if (keepOpen && d.querySelector('.vd-emos')) d.querySelector('.vd-emos').classList.add('open');
    const tm = d.querySelector('.vd-tm');
    if (tm) st.arcD = ctx.ui.timerArc(tm, v.until, v.total || 10000);
    st.sel = 0;
    markSel(st);
    // наведення на варіант — підсвітити ціль на дошці
    d.querySelectorAll('[data-hl]').forEach((b) => {
      const on = () => hlOne(st, b.dataset.hl, true), off = () => hlOne(st, b.dataset.hl, false);
      b.addEventListener('pointerenter', on); b.addEventListener('pointerleave', off);
      b.addEventListener('focus', on); b.addEventListener('blur', off);
    });
  }
  function hlOne(st, h, on) {
    if (!h) return;
    if (h[0] === 'p') { const t = st.toks.get(+h.slice(1)); if (t) t.g.classList.toggle('hov', on); }
    else { const el = st.svg.querySelector('.vt[data-n="' + h.slice(1) + '"]'); if (el) el.classList.toggle('hov', on); }
  }

  function selectable(st) {
    return [...st.el.vch.querySelectorAll('.vch-ov:not([hidden]) .vopt:not([disabled]), .vch-dock .vopt:not([disabled])')];
  }
  function markSel(st) {
    const list = selectable(st);
    list.forEach((b, k) => b.classList.toggle('kbd', k === st.sel && st.kbd));
  }

  // ---------- оверлеї посеред дошки: вступ, порядок, вибір і картка міні-гри, результати, Пізній вечір, фінал ----------
  function ovState(st, v) {
    const ctx = st.ctx, esc = ctx.esc, t = T(st), you = v.you || null, me = mineI(v), can = (you && you.can) || [];
    const players = v.players || [];
    if (v.paused && v.phase !== 'mg') {
      return { key: 'pause' + v.paused + isHost(st), html: '<div class="vo-card vo-pause"><div class="vo-big">' + (v.paused === 'empty' ? '🪑' : '⏸') + '</div><h3>'
        + (v.paused === 'empty' ? esc((t.hints || {}).emptyPause || 'Ні душі за столом') : 'Пауза — чекаємо господаря') + '</h3>'
        + (v.paused === 'host' && isHost(st) ? '<button type="button" class="primary" data-pause="0">▶ Продовжити</button>' : '') + '</div>' };
    }
    switch (v.phase) {
      case 'intro': {
        const tip = (t.tips || [])[(v.rounds || 0) % Math.max(1, (t.tips || []).length)] || '';
        return { key: 'intro', html: '<div class="vo-card vo-intro"><div class="vo-big vo-glek">🏺</div><h2>Глечикова вечірка</h2>'
          + '<p><b>' + v.rounds + '</b> кіл · мета — <b>золоті глеки</b> з лавки Дядька Глека</p>'
          + '<div class="vo-avs">' + players.map((p) => '<span>' + avaHtml(p, esc) + '<small>' + esc(p.name) + '</small></span>').join('') + '</div>'
          + (tip ? '<p class="muted small">💡 ' + esc(tip) + '</p>' : '') + '</div>' };
      }
      case 'order': {
        const rolls = (v.anim && v.anim.kind === 'order' && v.anim.dice) || [];
        return { key: 'order', html: '<div class="vo-card vo-order"><h3>🎲 Хто ходить першим</h3><ol>'
          + players.map((p, k) => '<li style="--d:' + (k * 0.25) + 's">' + avaHtml(p, esc, 'sm') + '<b>' + esc(p.name) + '</b>' + (rolls[p.i] ? '<span class="vo-roll">🎲 ' + rolls[p.i] + '</span>' : '') + '</li>').join('') + '</ol></div>' };
      }
      case 'pick': case 'card': {
        const pk = v.pick;
        const rou = st.rouT0 && now() - st.rouT0 < 1600;
        if (v.phase === 'pick' || rou) {
          if (!pk) return null;
          const chooser = playerOf(v, pk.chooser);
          const mine = !pk.chosen && me === pk.chooser && can.includes('pick');
          const cards = pk.options.map((id, k) => '<button type="button" class="vo-mg' + (mine ? ' vopt' : '') + (pk.chosen === id && !rou ? ' chosen' : '') + '" data-mg="' + esc(id) + '"'
            + (mine ? ' data-act="pick" data-p="' + esc(J({ o: k })) + '"' : ' tabindex="-1"') + '><span class="vo-mgi">' + HGames.iconOf(id) + '</span><b>' + esc(HGames.titleOf(id)) + '</b></button>').join('');
          const x2 = v.lastGame && !pk.duel ? '<div class="vo-x2">' + esc((t.lastGame || {}).title || 'Остання забава ×2') + '</div>' : '';
          return { key: 'pick' + J(pk) + rou + mine, rou: rou ? pk.chosen : null, timer: v.phase === 'pick' && !pk.chosen, html: '<div class="vo-card vo-pick">' + x2
            + '<h3>' + (pk.duel ? '⚔ Дуель — яка забава?' : '🎮 Міні-гра') + '</h3>'
            + (pk.duel ? '' : '<p>' + (mine ? '<b>Обирай ти</b> — ти зараз позаду, тож вибір твій' : 'Обирає ' + (chooser ? avaHtml(chooser, esc, 'sm') + ' <b>' + esc(chooser.name) + '</b> (позаду всіх)' : '…')) + '</p>')
            + '<div class="vo-mgs">' + cards + '</div><div class="vo-tm"></div></div>' };
        }
        const m = v.mg;
        if (!m) return null;
        const duel = m.duel;
        const ready = new Set(m.ready || []);
        const iPlay = m.sub != null;
        const seatsH = (m.seats || []).map((i, k) => { const p = playerOf(v, i); return p ? '<span class="vo-seat' + (ready.has(i) ? ' ok' : '') + '">' + avaHtml(p, esc, 'sm') + '<small>' + esc(m.names[k] || p.name) + (m.seatNames && m.seatNames[k] ? ' · ' + esc(m.seatNames[k]) : '') + '</small>' + (ready.has(i) ? '<i>✓</i>' : '') + '</span>' : ''; }).join('');
        let duelH = '';
        if (duel) {
          const a = playerOf(v, duel[0]), b = playerOf(v, duel[1]);
          const bets = m.bets || {};
          const myBet = me != null ? Object.keys(bets).find((k) => (bets[k] || []).includes(me)) : null;
          const side = (p) => '<div class="vo-ds">' + avaHtml(p, esc, 'lg') + '<b>' + esc(p.name) + '</b><small>👥 ' + ((bets[p.i] || []).length) + '</small>'
            + (can.includes('bet') ? optBtn(esc, 'bet', { i: p.i }, String(myBet) === String(p.i) ? '✓ Ставлю' : 'Ставлю на ' + esc(p.name), { cls: String(myBet) === String(p.i) ? 'on' : '' }) : '') + '</div>';
          duelH = a && b ? '<div class="vo-duel">' + side(a) + '<div class="vo-vs">⚔<small>' + (m.honor ? 'на честь' : 'на кону ' + (m.stake * 2) + ' 🪙') + '</small></div>' + side(b) + '</div>'
            + (can.includes('bet') ? '<p class="muted small">Вгадаєш переможця — +2 🪙 від Глека</p>' : '') : '';
        }
        return { key: 'card' + J(m.ready) + J(m.bets) + J(can) + m.id, timer: true, html: '<div class="vo-card vo-howto">'
          + (m.x2 ? '<div class="vo-x2">' + esc((t.lastGame || {}).title || 'Остання забава ×2') + '</div>' : '')
          + '<div class="vo-mgh"><span class="vo-mgi">' + HGames.iconOf(m.id) + '</span><h3>' + esc(m.title) + (m.repeat ? ' <small class="muted">(уже грали)</small>' : '') + '</h3></div>'
          + duelH + '<p class="vo-how">' + esc(m.howto || '') + '</p>'
          + (iPlay ? '<p class="vo-you">👥 Ти граєш за <b>' + esc(m.names[m.sub] || '') + '</b>' + (m.seatNames && m.seatNames[m.sub] ? ' · ' + esc(m.seatNames[m.sub]) : '') + '</p>' : duel ? '' : '<p class="muted">👀 Ти дивишся цю гру</p>')
          + '<div class="vo-seats">' + seatsH + '</div>'
          + '<div class="vo-act">' + (can.includes('ready') ? optBtn(esc, 'ready', {}, '✋ Готовий!', { cls: 'primary vo-ready' }) : iPlay ? '<span class="muted">✓ Чекаємо інших…</span>' : '') + '<div class="vo-tm"></div></div></div>' };
      }
      case 'results': {
        const m = v.mg;
        if (!m || !m.results) return null;
        const medal = ['🥇', '🥈', '🥉'];
        const how = m.how === 'Cap' ? '<p class="muted small">⏱ Час вийшов — рахуємо, як стояли</p>' : m.how === 'Crash' ? '<p class="muted small">💥 Гра зламалась — усім порівну</p>' : '';
        return { key: 'res' + J(m.results), html: '<div class="vo-card vo-res"><h3>🏁 ' + esc(m.title) + (m.duel ? ' · ⚔ дуель' : '') + (m.x2 ? ' · ×2' : '') + '</h3>' + how + '<ol>'
          + m.results.map((r, k) => { const p = playerOf(v, r.i); return p ? '<li style="--d:' + (0.3 + k * 0.45) + 's" class="' + (r.place === 1 ? 'win' : '') + (r.i === me ? ' me' : '') + '"><span class="vo-pl">' + (medal[r.place - 1] || r.place) + '</span>'
            + avaHtml(p, esc, 'sm') + '<b>' + esc(p.name) + '</b><span class="vo-coins">' + (r.coins ? '+' + r.coins + ' 🪙' : '—') + '</span></li>' : ''; }).join('') + '</ol></div>' };
      }
      case 'late': {
        const L = v.lateInfo;
        if (!L) return null;
        const mine = me != null && L.choosers.includes(me) && !(String(me) in (L.chosen || {})) && can.includes('pick');
        const names = L.choosers.map((i) => esc(nameOf(st, i))).join(' і ');
        const bs = t.bankSplit || {};
        return { key: 'late' + J(L) + mine, timer: true, html: '<div class="vo-card vo-late"><div class="vo-big">🌙</div><h3>' + esc((t.late || {}).title || 'Пізній вечір') + '</h3>'
          + '<p>' + esc((t.late || {}).text || '') + '</p>' + (bs.title ? '<p class="muted small">' + esc(bs.title) + ' — ' + esc(bs.text || '') + '</p>' : '')
          + '<h4>🎁 Подарунок від Глека — ' + (mine ? 'обирай!' : names) + '</h4>'
          + '<div class="vd-row vd-wrap">' + L.options.map((o, k) => optBtn(esc, 'pick', { o: k }, esc(o.label), { off: !mine, cls: 'vo-gift' })).join('') + '</div>'
          + '<div class="vo-chosen">' + Object.keys(L.chosen || {}).map((i) => esc(nameOf(st, +i)) + ' → ' + esc((L.options.find((o) => o.k === L.chosen[i]) || { label: L.chosen[i] }).label)).join(' · ') + '</div>'
          + '<div class="vo-tm"></div></div>' };
      }
      case 'final': case 'done': return finalState(st, v);
      default: return null;
    }
  }

  function finalState(st, v) {
    const esc = st.ctx.esc, t = T(st), F = v.final;
    if (!F) return null;
    const bon = (F.bonuses || []).map((b, k) => {
      const tx = (t.bonus || {})[b.key] || { title: b.title, icon: '🏺', text: '' };
      const fresh = k === F.bonuses.length - 1 && !(F.ranking || []).length && v.phase === 'final';
      const ws = (b.winners || []).map((i) => playerOf(v, i)).filter(Boolean);
      return '<div class="vo-bon' + (fresh ? ' fresh' : '') + '"><span class="vo-bi">' + esc(tx.icon || '🏺') + '</span><div><b>🏺 ' + esc(tx.title || b.title) + '</b><small>' + esc(tx.text || '') + '</small></div>'
        + '<div class="vo-bw">' + (ws.length ? ws.map((p) => avaHtml(p, esc, 'sm') + '<span>' + esc(p.name) + '</span>').join('') : '<span class="muted">нікому</span>') + '</div></div>';
    }).join('');
    const rank = F.ranking || [];
    let top = '';
    if (rank.length) {
      const first = rank.filter((r) => r.place === 1).map((r) => playerOf(v, r.i)).filter(Boolean);
      top = '<div class="vo-win">' + first.map((p) => '<div>' + avaHtml(p, esc, 'xl') + '<b>' + esc(p.name) + '</b></div>').join('') + '<div class="vo-crown">👑 ' + (first.length > 1 ? 'Голови вечірки!' : 'Голова вечірки!') + '</div></div>'
        + '<table class="vo-tbl"><thead><tr><th></th><th></th><th>🏺</th><th>🪙</th><th>🏆</th></tr></thead><tbody>'
        + rank.map((r) => { const p = playerOf(v, r.i); return p ? '<tr class="' + (r.place === 1 ? 'win' : '') + (r.i === mineI(v) ? ' me' : '') + '"><td>' + r.place + '</td><td>' + avaHtml(p, esc, 'sm') + ' ' + esc(p.name) + '</td><td>' + p.gleks + '</td><td>' + p.coins + '</td><td>' + p.mgWins + '</td></tr>' : ''; }).join('')
        + '</tbody></table>';
    }
    return { key: 'final' + J(F), confetti: rank.length > 0, drum: !rank.length && (F.bonuses || []).length > 0 && v.phase === 'final',
      html: '<div class="vo-card vo-final">' + (rank.length ? '<h2>🎉 Вечірці кінець!</h2>' : '<h2>🏺 Бонусні глеки</h2>' + (F.step < 0 ? '<p class="vo-drum">Глек дістає три таємні номінації… 🥁</p>' : ''))
        + top + (bon ? '<div class="vo-bons">' + bon + '</div>' : '') + '</div>' };
  }

  function renderOv(st, v) {
    const o = st.mgOn ? null : ovState(st, v);
    const el = st.el.ov;
    el.hidden = !o;
    st.el.vch.classList.toggle('dim', !!o);
    if (!o) { st.ovKey = ''; el.innerHTML = ''; if (st.arcO) { st.arcO.stop(); st.arcO = null; } return; }
    const key = o.key + '|' + (v.until || '') + (st.first ? '' : '');
    if (st.ovKey === key) return;
    st.ovKey = key;
    if (st.arcO) { st.arcO.stop(); st.arcO = null; }
    el.innerHTML = o.html;
    const tm = el.querySelector('.vo-tm');
    if (tm && o.timer && v.until) st.arcO = st.ctx.ui.timerArc(tm, v.until, v.total || 8000);
    if (o.rou) roulette(st, o.rou);
    if (o.confetti && !st.confDone) { st.confDone = true; confetti(st); }
    if (!o.confetti) st.confDone = false;
    st.sel = 0; markSel(st);
  }

  /// Рулетка 1,5 с: картки блимають і зупиняються на обраній; потім — картка «як грати».
  function roulette(st, chosen) {
    const cards = [...st.el.ov.querySelectorAll('.vo-mg')];
    const left = Math.max(200, 1500 - (now() - st.rouT0));
    if (cards.length) {
      let k = 0, delay = 70;
      const end = now() + left - 250;
      const step = () => {
        if (st.dead || !cards[0].isConnected) return;
        cards.forEach((c, n) => c.classList.toggle('flash', n === k % cards.length));
        if (now() < end) { k++; delay *= 1.12; later(st, step, delay); }
        else { cards.forEach((c) => { c.classList.remove('flash'); c.classList.toggle('chosen', c.dataset.mg === chosen); }); }
      };
      step();
    }
    later(st, () => { st.ovKey = ''; renderOv(st, st.v); }, left + 80);
  }

  function confetti(st) {
    if (reduced()) return;
    const box = document.createElement('div');
    box.className = 'vch-conf';
    let h = '';
    for (let k = 0; k < 70; k++) {
      h += '<i style="left:' + (Math.random() * 100).toFixed(1) + '%;--hh:' + Math.floor(Math.random() * 360) + ';animation-delay:' + (Math.random() * 1.6).toFixed(2) + 's;animation-duration:' + (2.4 + Math.random() * 1.8).toFixed(2) + 's"></i>';
    }
    box.innerHTML = h;
    st.el.bw.appendChild(box);
    later(st, () => box.remove(), 5200);
  }

  // ---------- міні-гра всередині вечірки (HGames.embed) ----------
  function embOpts(st, v) {
    const m = v.mg;
    return {
      view: m.view == null ? null : m.view,
      seat: m.sub == null ? null : m.sub,
      names: m.names || [], nicks: m.nicks || [], seatNames: m.seatNames || [],
      status: m.status || 'playing',
      roomId: st.ctx.room && st.ctx.room.id, host: st.ctx.room && st.ctx.room.host,
    };
  }
  function dropEmb(st) {
    if (st.emb) { try { st.emb.unmount(); } catch (e) { console.warn('[vechirka] unmount міні-гри', e); } }
    st.emb = null; st.embKey = '';
    st.el.mg.innerHTML = '';
  }
  function syncMg(st, v) {
    const on = v.phase === 'mg' && !!v.mg;
    st.mgOn = on;
    st.el.vch.classList.toggle('mgon', on);
    st.el.mg.hidden = !on;
    if (!on) { dropEmb(st); return; }
    const m = v.mg;
    const key = v.round + ':' + m.id + ':' + (m.duel ? m.duel.join('-') : '');
    if (st.embKey !== key) {
      dropEmb(st);
      st.embKey = key;
      st.el.mg.innerHTML = '<div class="vmg-h"><span class="vo-mgi">' + HGames.iconOf(m.id) + '</span><b>' + st.ctx.esc(m.title) + '</b>'
        + (m.duel ? '<span class="muted small">⚔ ' + st.ctx.esc(nameOf(st, m.duel[0]) + ' проти ' + nameOf(st, m.duel[1])) + '</span>' : '')
        + (m.x2 ? '<span class="vt-x2">×2</span>' : '') + (m.sub == null ? '<span class="muted small">👀 дивишся</span>' : '') + '</div><div class="vmg-b"></div>';
      try {
        st.emb = HGames.embed(st.el.mg.querySelector('.vmg-b'), m.id, Object.assign(embOpts(st, v), {
          frame: st.ctx.frame && st.ctx.frame.mg ? st.ctx.frame.mg : null,
          act: (a, p) => st.ctx.act('mg', { a, p }),
          input: (a, p) => st.ctx.input('mg', { a, p }),
        }));
      } catch (e) { console.warn('[vechirka] embed ' + m.id, e); st.emb = null; }
    } else if (st.emb) st.emb.update(embOpts(st, v));
  }

  // ---------- лобі ----------
  function renderLobby(st, v) {
    const ctx = st.ctx, esc = ctx.esc, t = T(st), room = ctx.room || {};
    const host = isHost(st);
    const bots = (v.lobby && v.lobby.bots) || [];
    const seats = (room.seats || []).map((s, i) => (typeof s === 'string' ? s : s && s.nick) || null).filter(Boolean);
    const total = seats.length + bots.length;
    const pl = seats.map((n, k) => ({ i: k, nick: n, name: n, color: k })).concat(bots.map((b, k) => ({ i: 100 + k, bot: true, name: b.name, color: seats.length + k })));
    const h = '<div class="vo-card vo-lobby"><div class="vo-mgh"><span class="vo-big vo-glek">🏺</span><div><h2>Глечикова вечірка</h2>'
      + '<p class="muted">Настільна вечірка на 2–8: ходиш селом, збираєш шеляги й купуєш у Дядька Глека золоті глеки. Між колами — міні-ігри.</p></div></div>'
      + '<div class="vl-row"><span>🕰 Вечір: <b>≈ ' + ((v.lobby && v.lobby.rounds) || '?') + ' кіл</b></span><span>👥 За столом: <b>' + total + '</b>/8</span></div>'
      + '<div class="vo-avs">' + pl.map((p) => '<span>' + avaHtml(p, esc) + '<small>' + esc(p.name) + '</small></span>').join('') + '</div>'
      + (host ? '<div class="vd-row">' + optBtn(esc, 'bot', { on: true }, '🤖 + бот', { off: total >= 8 }) + optBtn(esc, 'bot', { on: false }, '− бот', { off: !bots.length }) + '</div>'
        + '<p class="muted small">' + (total < 2 ? 'Треба хоча б двоє — поклич друзів або додай ботів.' : 'Усі на місці? Тисни «Почати».') + '</p>'
        : '<p class="muted small">Ботів додає й вечірку починає господар столу.</p>')
      + '<div class="vh-g">' + (t.howto || []).map((s) => '<div class="vh-s"><span class="vh-ic">' + esc(s.icon) + '</span><div><b>' + esc(s.title) + '</b><p>' + esc(s.text) + '</p></div></div>').join('') + '</div></div>';
    st.el.top.innerHTML = '<span class="vt-r">🏺 <b>Глечикова вечірка</b> · лобі</span><span class="vt-sp"></span><button type="button" class="vt-b" data-pop="help" title="Як грати">❓</button>';
    st.topH = '';
    st.el.ov.hidden = false;
    if (st.ovKey !== h) { st.ovKey = h; st.el.ov.innerHTML = h; }
    st.el.vch.classList.add('dim');
    st.el.dock.hidden = true;
    st.el.pl.innerHTML = pl.map((p) => '<div class="vp" style="--ph:' + hue(p) + '">' + avaHtml(p, esc) + '<span class="vp-n">' + esc(p.name) + '</span></div>').join('');
    st.plH = '';
    st.el.goal.hidden = true; st.el.emo.hidden = true;
    st.el.glek.innerHTML = '<span class="vg-ic">🏺</span><span><b>Дядько Глек:</b> Заходьте, сідайте — вечірка от-от почнеться!</span>';
    st.el.log.innerHTML = '';
  }

  // ---------- головний рендер ----------
  function render(root, ctx) {
    let st = root._vch;
    if (!st) {
      st = { root, uid: ++uidSeq, timers: new Set(), flyers: [], fxSeen: new Set(), cam: { x: 800, y: 500, z: 1 }, camT: { x: 800, y: 500, z: 1 },
        follow: true, first: true, sel: 0, kbd: false, toks: new Map() };
      root._vch = st;
      st.ctx = ctx;
      shell(root, st);
      bindCamera(st);
      live.add(st);
    }
    st.ctx = ctx;
    const v = ctx.view || {};
    layout(st);
    const mapId = v.map || 'selo', mapV = v.mapV || 1, dk = mapId + ':' + mapV;
    if (!st.data || st.dataKey !== dk) {
      if (st.loading !== dk) {
        st.loading = dk;
        loadData(mapId, mapV).then((d) => {
          if (st.dead) return;
          st.data = d; st.dataKey = dk; st.loading = null;
          buildBoard(st);
          st.first = true;
          const z = defZoom(st);
          st.cam = { x: d.map.w / 2, y: d.map.h / 2, z: isPhone(st) ? 1 : z };
          st.camT = { x: d.map.w / 2, y: d.map.h / 2, z };
          st.el.wait.hidden = true;
          applyCam(st);
          render(st.root, st.ctx);
        }).catch(() => { st.loading = null; st.el.wait.textContent = 'Карта не завантажилась — онови сторінку'; });
      }
      return;
    }
    const lobby = !v.phase || v.phase === 'lobby';
    st.el.vch.classList.toggle('lobby', lobby);
    st.v = v;
    if (lobby) { renderLobby(st, v); kick(st); return; }
    if (v.shops) st.lastShops = v.shops;
    if (v.gates) st.lastGates = v.gates;
    // «Ще раз» — нова вечірка, лічильники анімацій з нуля
    if (v.anim && st.animSeq != null && v.anim.seq < st.animSeq) { st.animSeq = null; st.fxSeen.clear(); st.fxInit = false; st.emoSeq = null; st.first = true; }
    const fresh = !!(v.anim && (st.animSeq == null || v.anim.seq > st.animSeq)) && !st.first;
    if (fresh && (v.anim.kind === 'roulette')) st.rouT0 = now();
    if (st.first && v.anim && v.anim.kind === 'roulette' && v.phase === 'card') st.rouT0 = 0;
    if (fresh && v.phase === 'intro') { st.follow = false; st.camT = { x: st.data.map.w / 2, y: st.data.map.h / 2, z: 1 }; later(st, () => { st.follow = true; st.camT.z = defZoom(st); kick(st); }, 4500); }
    renderTop(st, v);
    syncTokens(st, v, fresh);
    drawStand(st, v);
    drawGates(st, v);
    onAnim(st, v);
    onFx(st, v);
    onEmo(st, v);
    onSay(st, v);
    renderSide(st, v);
    syncMg(st, v);
    renderOv(st, v);
    renderDock(st, v);
    // цілі прицілювання — на дошці
    const aim = v.phase === 'aim' && v.aim;
    drawHl(st, aim && aim.nodes ? aim.nodes : v.phase === 'turn' && v.stand && mineI(v) === v.cur ? [] : [], 'aim');
    for (const t of st.toks.values()) t.g.classList.toggle('tgt', !!(aim && aim.targets && aim.targets.includes(t.i)));
    if (v.cur != null && ['turn', 'aim', 'walk', 'prompt'].includes(v.phase)) st.followI = v.cur;
    // корона «Голова вечірки» в людях (app.js, К5) — сервер ставить її на кінці вечірки
    if (v.phase === 'done' && !st.crownAsked) { st.crownAsked = true; later(st, () => { if (window.HPartyCrown) window.HPartyCrown.refresh(); }, 2500); }
    if (v.phase !== 'done') st.crownAsked = false;
    showFocusBtn(st);
    st.first = false;
    kick(st);
  }

  // ---------- клавіші й пад ----------
  function onKey(e, ctx) {
    const st = stOf(ctx);
    if (!st || !st.v) return false;
    if (st.mgOn) return !!(st.emb && st.emb.onKey(e));
    if (e.type && e.type !== 'keydown') return false;
    const k = e.key;
    if (k === 'm' || k === 'M' || k === 'ь' || k === 'Ь') { st.el.top.querySelector('[data-mute]') && st.el.top.querySelector('[data-mute]').click(); return true; }
    if (k === 'Escape') {
      if (!st.el.pop.hidden) { closePop(st); return true; }
      const c = st.el.dock.querySelector('.vd-cancel');
      if (c) { c.click(); return true; }
      return false;
    }
    const list = selectable(st);
    if (!list.length) return false;
    if (k === 'ArrowLeft' || k === 'ArrowRight' || k === 'ArrowUp' || k === 'ArrowDown') {
      st.kbd = true;
      st.sel = (st.sel + (k === 'ArrowLeft' || k === 'ArrowUp' ? -1 : 1) + list.length) % list.length;
      markSel(st);
      return true;
    }
    if (k === ' ' || k === 'Enter') {
      const b = st.kbd ? list[st.sel] : (st.el.dock.querySelector('.vd-roll:not([disabled])') || list[st.sel]);
      if (b) { b.click(); return true; }
      return false;
    }
    if (/^[1-3]$/.test(k)) {
      const it = [...st.el.dock.querySelectorAll('.vd-item')][+k - 1];
      if (it && !it.disabled) { it.click(); return true; }
      return false;
    }
    if (k === 'i' || k === 'I' || k === 'ш' || k === 'Ш') {
      const its = list.filter((b) => b.classList.contains('vd-item'));
      if (!its.length) return false;
      st.kbd = true;
      const cur = its.indexOf(list[st.sel]);
      st.sel = list.indexOf(its[(cur + 1) % its.length]);
      markSel(st);
      return true;
    }
    return false;
  }
  const BOARD_PAD = { dirs: 'x', a: 'Enter', x: 'i', hint: '{dpad} вибір · {a} кинути / так', when: (ctx) => ctx.mine && ctx.playing };

  HGames.register({
    id: 'vechirka',
    added: '2026-10-06',
    icon: ICON,
    /// У фазі mg — пад вбудованої гри (каркас вкладеному модулю його не роздає), інакше — пад дошки.
    get pad() {
      const st = [...live].find((s) => s.root.isConnected && s.mgOn && s.emb);
      if (st) return st.emb.pad;
      return BOARD_PAD;
    },
    mount(root, ctx) { render(root, ctx); },
    update(root, ctx) { render(root, ctx); },
    frame(root, ctx, f) {
      const st = root._vch;
      if (st && st.emb && f && f.mg) st.emb.frame(f.mg);
    },
    onKey,
    status(ctx) {
      const st = stOf(ctx), v = ctx.view || {};
      if (!ctx.playing || !v.phase) return '';
      if (v.phase === 'mg' && st && st.emb) { try { return st.emb.status() || ('🎮 ' + (v.mg ? v.mg.title : '')); } catch (e) { return ''; } }
      const me = mineI(v);
      if (v.paused) return '⏸ Пауза';
      if (v.phase === 'turn' && me != null && me === v.cur) return '🎲 Твій хід — кидай кубик';
      if (v.prompt && me === v.prompt.who) return '🤔 Тобі вирішувати';
      if (v.phase === 'turn' || v.phase === 'walk' || v.phase === 'prompt' || v.phase === 'aim') {
        const p = playerOf(v, v.cur);
        return p ? 'Ходить ' + p.name : '';
      }
      return { intro: '🏺 Вечірка починається', order: '🎲 Кидаємо порядок', pick: '🎮 Обираємо міні-гру', card: '🎮 Як грати', results: '🏁 Результати', late: '🌙 Пізній вечір', final: '🏺 Бонусні глеки', done: '🎉 Вечірці кінець' }[v.phase] || '';
    },
    unmount(root) {
      const st = root._vch;
      if (!st) return;
      st.dead = true;
      dropEmb(st);
      if (st.raf) cancelAnimationFrame(st.raf);
      st.timers.forEach((id) => { clearTimeout(id); clearInterval(id); });
      clearInterval(st.diceI);
      if (st.arcD) st.arcD.stop();
      if (st.arcO) st.arcO.stop();
      if (st.ro) st.ro.disconnect();
      if (st.audio) { try { st.audio.pause(); } catch (e) { /* нічого */ } }
      live.delete(st);
      root._vch = null;
      root.innerHTML = '';
    },
  });
})();
