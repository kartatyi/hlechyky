/*
  «Де це?» (geo) і «Де це? Тренування» (geo-solo) — фото звідкись з України, шпилька на мапі, очки за відстань.
  Клієнт нічого не вирішує: фази, час, очки й правда живуть на сервері (Impl/GeoMatch.cs). Модуль малює мапу,
  фото й розкриття і шле наміри:
    ctx.act('guess', { x, y })   — шпилька в одиницях сітки 4000×2730 (цілі);
    ctx.act('ready')             — «Готово»;
    ctx.act('next')              — «Далі» на розкритті.
  Проєкції тут нема й не треба: мапа (geo-map.json) уже в одиницях сітки, правду й чужі шпильки сервер шле в них же.

  Вид (подія 'room', свій для кожного місця — гра Hidden; до розкриття чужих шпильок і правди в ньому нема):
    { phase: 'lobby'|'between'|'guess'|'reveal'|'done', round, rounds, endsAt, phaseMs, seconds, hints, photo,
      pinned, ready, next, my: null|{x,y,ready}, reveal: null|{ x, y, lat, lon, name, region, cat, wikidata,
      photo: { title, author, license, licenseUrl, page }, say, rows: [{ seat, x, y, km, points, best, bull }] },
      scores, left, result, recap, turn: null }
  Кадр (подія 'frame', лише на зміну): { ph, r, ends, pin, rdy, nxt }. Видів на шпильку сервер не шле: свою шпильку
  малюємо одразу самі, а підтвердження — відповідь на act.

  Малювання: один <canvas> розміром із рамку (CSS-пікселі × DPR). Статичне (море, суходіл, межі, річки, міста)
  — в offscreen-канвас, перемальовується лише коли змінились масштаб/зсув/розмір; кадр — бліт + шпильки.
  requestAnimationFrame крутиться лише поки щось рухається (курсор, падіння шпильки, підліт мапи, розкриття,
  пульс останніх 5 с); протяг і колесо малюють по подіях. Решту часу — нуль кадрів.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true"><path d="M2.5 12.5 5 5l4 1.4 4.5-1.4-2.5 7.5-4-1.4z" fill="var(--ok)" opacity=".6"/><path d="M8 1.2a2.9 2.9 0 0 1 2.9 2.9C10.9 6.3 8 9.6 8 9.6S5.1 6.3 5.1 4.1A2.9 2.9 0 0 1 8 1.2z" fill="var(--accent)"/><circle cx="8" cy="4.1" r="1.15" fill="var(--accent-ink)"/></svg>';

  const W = 4000, H = 2730;            // сітка мапи — та сама, що GeoMap.W/H на сервері
  const KMAX = 12;
  const MAP_URL = '/games/geo-map.json';
  /// Десять місць — десять різних відтінків (жовтий, зелений, помаранчевий, блакитний, рожевий, фіалковий, білий,
  /// синій, бірюзовий, лаймовий); червоного серед них нема — червоно-біла мішень лише в правди.
  const SEAT_COLORS = ['#f4c542', '#7bd389', '#e8833a', '#6fb3e8', '#e88ac0', '#b48cf2', '#f0f0f0', '#5c7cfa', '#5ad1c9', '#a3e635'];
  const SEAT_CLASS = SEAT_COLORS.map((_, i) => 'geo-s' + i);
  const DRAG_PX = 6;
  /// Стільки мс мапа має постояти, щоб статичний шар перемалювати начисто; доти (протяг, колесо, щипок,
  /// підліт) кадр — готовий «атлас» мапи, розтягнутий під масштаб: одна drawImage замість тисяч точок.
  const SETTLE_MS = 140;
  const RULES = 'Фото звідкись з України — тицьни на мапі, де це знято. За кілометр і ближче — 5000 очок, '
    + 'за 100 км — 2885, за 500 — 313, далі крихти. За кожні 5000 очок партії — 🏺 черепок (до 30 на день).';
  const PLACE = ['перше', 'друге', 'третє', 'четверте', 'п’яте', 'шосте', 'сьоме', 'восьме', 'дев’яте', 'десяте'];

  const reduced = () => { try { return matchMedia('(prefers-reduced-motion: reduce)').matches; } catch { return false; } };
  const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
  const easeOut = (t) => 1 - (1 - t) * (1 - t);
  const easeInOut = (t) => (t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2);

  /// Ціле з пробілами між тисячами: 21 340.
  const num = (n) => String(Math.round(n || 0)).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  /// Кілометри так, як їх пишуть люди: «менше кілометра», «7,3 км», «1 043 км». Те саме, що GeoText.Km.
  function km(d) {
    if (d == null) return '';
    if (d < 1) return 'менше кілометра';
    const tenth = Math.round(d * 10) / 10;
    if (tenth < 10) return tenth.toFixed(1).replace('.', ',') + ' км';
    return num(Math.round(d)) + ' км';
  }

  // ---------------------------------------------------------------------------------------------
  // мапа: один раз на модуль
  // ---------------------------------------------------------------------------------------------

  let mapPromise = null;
  function loadMap() {
    if (!mapPromise) {
      mapPromise = fetch(MAP_URL)
        .then((r) => { if (!r.ok) throw new Error('мапа ' + r.status); return r.json(); })
        .then(buildMap)
        .catch((e) => { mapPromise = null; throw e; });
    }
    return mapPromise;
  }

  /// Кільця з дуг (кінець дуги = початок наступної; від'ємний індекс — дуга навпаки) і Path2D в одиницях сітки.
  function buildMap(raw) {
    const arcs = raw.arcs || [];
    const refs = new Uint8Array(arcs.length);
    for (const r of raw.regions || []) for (const ring of r.rings) for (const id of ring) refs[Math.abs(id) - 1]++;
    const land = new Path2D(), borders = new Path2D(), outline = new Path2D();
    for (const r of raw.regions || []) {
      for (const ring of r.rings) {
        let first = true;
        for (const id of ring) {
          const a = arcs[Math.abs(id) - 1];
          const n = a.length / 2;
          for (let k = first ? 0 : 1; k < n; k++) {
            const i = id > 0 ? k : n - 1 - k;
            if (first) { land.moveTo(a[2 * i], a[2 * i + 1]); first = false; } else land.lineTo(a[2 * i], a[2 * i + 1]);
          }
        }
        land.closePath();
      }
    }
    let points = 0;
    arcs.forEach((a, i) => {
      // дуга одного кільця — контур країни (узбережжя, кордон), двох — межа областей
      const p = refs[i] === 1 ? outline : borders;
      p.moveTo(a[0], a[1]);
      for (let k = 2; k < a.length; k += 2) p.lineTo(a[k], a[k + 1]);
      points += a.length / 2;
    });
    const river = new Path2D(), riverBig = new Path2D();
    const riverLabels = [];
    for (const rv of raw.rivers || []) {
      const p = rv.big ? riverBig : river;
      let best = null, bestLen = 0;
      for (const l of rv.lines) {
        p.moveTo(l[0], l[1]);
        let len = 0;
        for (let k = 2; k < l.length; k += 2) { p.lineTo(l[k], l[k + 1]); len += Math.hypot(l[k] - l[k - 2], l[k + 1] - l[k - 1]); }
        if (len > bestLen) { bestLen = len; best = l; }
      }
      // підписуємо лише головні, щоб мапа не стала атласом
      if (best && /^(Дніпро|Дністер|Південний Буг|Десна|Сіверський Донець)$/.test(rv.name)) {
        const m = Math.floor(best.length / 4) * 2;
        riverLabels.push({ name: rv.name, x: best[m], y: best[m + 1] });
      }
    }
    return { land, borders, outline, river, riverBig, riverLabels, cities: raw.cities || [], points };
  }

  // ---------------------------------------------------------------------------------------------
  // звук: тихо, лише WebAudio-синтез і лише після жесту (шпилька — це жест)
  // ---------------------------------------------------------------------------------------------

  let actx = null;
  const soundOn = () => { try { return localStorage.getItem('geoSound') !== '0'; } catch { return true; } };
  function tone(freqs, ms, gain, start) {
    if (!soundOn() || (!start && !actx)) return;
    try {
      actx = actx || new (window.AudioContext || window.webkitAudioContext)();
      let t = actx.currentTime;
      for (const f of freqs) {
        const o = actx.createOscillator(), g = actx.createGain();
        o.type = 'sine';
        o.frequency.value = f;
        g.gain.setValueAtTime(gain, t);
        g.gain.exponentialRampToValueAtTime(0.0001, t + ms / 1000);
        o.connect(g).connect(actx.destination);
        o.start(t);
        o.stop(t + ms / 1000 + 0.02);
        t += ms / 1000;
      }
    } catch { /* без звуку теж можна */ }
  }

  // ---------------------------------------------------------------------------------------------
  // стан картки
  // ---------------------------------------------------------------------------------------------

  function state(root, ctx) {
    if (!root._geo) {
      root._geo = {
        root, ctx, map: null,
        cv: null, g: null, stat: null, sg: null, dpr: 1, cw: 0, ch: 0,
        s0: 0, k: 1, ox: 0, oy: 0, staticDirty: true, sv: { k: -1, ox: 0, oy: 0, cw: 0, ch: 0, dpr: 0, hints: '' },
        viewT: 0, atlas: null, atlasKey: '', hints: '', redTimer: 0, scrolled: '', podFocus: '',
        raf: 0, fly: null, fit: null, drop: 0, revT0: 0, revKey: '', lastKey: '', lastPhase: '',
        pin: null, pinRound: -1, readyRound: -1, nextRound: -1,
        cur: null, keys: { l: 0, r: 0, u: 0, d: 0 }, keyT0: 0, lastT: 0,
        ptrs: new Map(), down: null, pinch: null,
        colors: SEAT_COLORS.slice(), css: {},
        full: null, ro: null, keyup: null, onVis: null, labels: [], pins: [],
        perf: { draws: 0, drawMs: 0, statics: 0, staticMs: 0 },
      };
    }
    root._geo.ctx = ctx;
    ctx._geo = root._geo;
    // для заміру швидкодії з консолі (docs/games/specs/geo.md, «Як реалізовано»): один кадр прямо зараз
    if (!root._geo.redraw) { const st = root._geo; st.redraw = () => draw(st, performance.now()); }
    return root._geo;
  }

  const V = (ctx) => ctx.view || {};
  /// Фаза для екрана: стіл, що знову відкрився після партії (хтось підсів), — це лобі, хоч вид іще «done».
  const phaseOf = (ctx) => (ctx.room && ctx.room.status === 'lobby' ? 'lobby' : V(ctx).phase || 'lobby');
  /// Кадр свіжіший за вид, але вірити йому можна лише в межах того самого раунду й фази (урок «Скільки?»).
  function fresh(ctx) {
    const v = V(ctx), f = ctx.frame;
    return f && f.r === v.round && f.ph === v.phase ? f : null;
  }
  function seatsOf(ctx) {
    const n = (ctx.room && ctx.room.seats && ctx.room.seats.length) || 0;
    const out = [];
    for (let i = 0; i < n; i++) if (ctx.nickOf(i)) out.push(i);
    return out;
  }
  /// Нік місця. Хто встав (посеред партії чи вже після) — нік із виду: каркас його забуває, партія — ні.
  const nick = (ctx, i) => ctx.nickOf(i) || (V(ctx).nicks || [])[i] || '№' + ctx.seatName(i);
  const canPin = (st, ctx) => ctx.mine && ctx.playing && phaseOf(ctx) === 'guess' && st.readyRound !== V(ctx).round;

  // ---------------------------------------------------------------------------------------------
  // перетворення: екран (CSS-пікселі канваса) ↔ мапа (одиниці сітки)
  // ---------------------------------------------------------------------------------------------

  const sc = (st) => st.s0 * st.k;
  /// Екранні x і y окремо — у кадрі rAF без масивів на кожну шпильку.
  const sx = (st, x) => x * st.s0 * st.k + st.ox;
  const sy = (st, y) => y * st.s0 * st.k + st.oy;
  const toMap = (st, x, y) => [(x - st.ox) / sc(st), (y - st.oy) / sc(st)];

  /// Мапа не тікає з рамки: менша за рамку — по центру, більша — з полем моря до чверті рамки за краєм
  /// (інакше зірка правди на березі Криму чи Закарпаття липла б до самого краю екрана).
  function clampView(st) {
    const mw = W * sc(st), mh = H * sc(st);
    const mx = st.cw * 0.25, my = st.ch * 0.25;
    st.ox = mw <= st.cw ? (st.cw - mw) / 2 : clamp(st.ox, st.cw - mw - mx, mx);
    st.oy = mh <= st.ch ? (st.ch - mh) / 2 : clamp(st.oy, st.ch - mh - my, my);
  }

  /// Вид зсунули чи наблизили безперервно (протяг, колесо, щипок, підліт): поки рухається — атлас, потім начисто.
  function moved(st) {
    st.viewT = performance.now();
    kick(st);
  }

  function home(st) {
    st.k = 1;
    st.fly = null;
    st.fit = null;
    clampView(st);
    st.staticDirty = true;
    kick(st);
  }

  function zoomAt(st, px, py, f) {
    const k = clamp(st.k * f, 1, KMAX);
    if (k === st.k) return;
    st.ox = px - (px - st.ox) * (k / st.k);
    st.oy = py - (py - st.oy) * (k / st.k);
    st.k = k;
    st.fly = null;
    st.fit = null;
    clampView(st);
    moved(st);
  }

  function zoomKey(st, f) {
    if (st.cur) zoomAt(st, sx(st, st.cur.x), sy(st, st.cur.y), f);
    else zoomAt(st, st.cw / 2, st.ch / 2, f);
  }

  /// Вмістити всі точки (одиниці сітки) з полем 15–17 % — одразу, без польоту.
  function fitView(st, pts, maxK) {
    let x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity;
    for (const [x, y] of pts) { x0 = Math.min(x0, x); y0 = Math.min(y0, y); x1 = Math.max(x1, x); y1 = Math.max(y1, y); }
    const bw = Math.max(x1 - x0, 1), bh = Math.max(y1 - y0, 1);
    st.k = clamp(Math.min(st.cw * 0.7 / (bw * st.s0), st.ch * 0.66 / (bh * st.s0)), 1, maxK || KMAX);
    st.ox = st.cw / 2 - (x0 + x1) / 2 * st.s0 * st.k;
    st.oy = st.ch / 2 - (y0 + y1) / 2 * st.s0 * st.k;
    clampView(st);
  }

  /// Плавно підлетіти до точок. Ціль пам'ятаємо (st.fit), поки людина сама не рушила мапу: якщо рамка тим часом
  /// змінить розмір (з'явилась смуга прокрутки, розгорнули картку), вписуємо наново, а не лишаємо пів польоту.
  function flyTo(st, pts, maxK) {
    if (!pts.length) return;
    st.fit = { pts, maxK };
    if (!st.cw) return;                                   // рамка ще не виміряна — впишемо, щойно буде
    const from = { k: st.k, ox: st.ox, oy: st.oy };
    fitView(st, pts, maxK);
    const to = { k: st.k, ox: st.ox, oy: st.oy };
    if (!reduced() && st.cv && st.cv.offsetParent) {
      st.k = from.k; st.ox = from.ox; st.oy = from.oy;
      st.fly = { t0: performance.now(), ms: 500, from, to };
      moved(st);
    } else {
      st.staticDirty = true;                              // без польоту — одразу начисто, одним кадром
      kick(st);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // малювання
  // ---------------------------------------------------------------------------------------------

  function cssColors(st, ctx) {
    const c = (n, f) => ctx.css(n, f) || f;
    st.css = {
      sea: c('--geo-sea', '#123038'), land: c('--geo-land', '#2b4c3c'), border: c('--geo-border', 'rgba(236,241,234,.26)'),
      outline: c('--accent2', '#d9a92f'), river: c('--geo-river', '#4a8fb3'), muted: c('--muted', '#9db3a5'),
      text: c('--text', '#ecf1ea'), accent: c('--accent', '#f4c542'), danger: c('--danger', '#e57373'),
      ink: c('--accent-ink', '#2a1e03'), shade: c('--gshade', 'rgba(15,31,24,.62)'), bg: c('--bg', '#0f1f18'),
    };
    st.colors = SEAT_COLORS.map((f, i) => c('--geo-p' + i, f));
  }

  /// Рамка змінилась: канваси під нові пікселі, «вписати» — наново, центр мапи лишається центром.
  function resize(st) {
    if (!st.cv) return;
    const box = st.cv.parentElement;
    const cw = box.clientWidth, ch = box.clientHeight;
    if (!cw || !ch) return;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    if (cw === st.cw && ch === st.ch && dpr === st.dpr) return;
    const first = !st.cw;
    const center = first ? [W / 2, H / 2] : toMap(st, st.cw / 2, st.ch / 2);
    st.cw = cw; st.ch = ch; st.dpr = dpr;
    st.s0 = Math.min(cw / W, ch / H);
    const pw = Math.round(cw * dpr), ph = Math.round(ch * dpr);
    st.cv.width = pw; st.cv.height = ph;
    st.stat.width = pw; st.stat.height = ph;
    st.ox = cw / 2 - center[0] * sc(st);
    st.oy = ch / 2 - center[1] * sc(st);
    clampView(st);
    st.fly = null;
    if (st.fit) fitView(st, st.fit.pts, st.fit.maxK);
    st.staticDirty = true;
    kick(st);
  }

  function renderStatic(st, hints) {
    const t0 = performance.now();
    const g = st.sg, c = st.css, m = st.map;
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.fillStyle = c.sea;
    g.fillRect(0, 0, st.stat.width, st.stat.height);
    if (m) {
      const s = st.dpr * sc(st);
      g.setTransform(s, 0, 0, s, st.dpr * st.ox, st.dpr * st.oy);
      g.fillStyle = c.land;
      g.fill(m.land, 'evenodd');
      g.lineJoin = 'round';
      g.lineCap = 'round';
      if (hints !== 'none') {
        g.strokeStyle = c.border;
        g.lineWidth = 1 / sc(st);
        g.stroke(m.borders);
        g.strokeStyle = c.river;
        g.lineWidth = 1.2 / sc(st);
        g.stroke(m.river);
        g.lineWidth = 1.8 / sc(st);
        g.stroke(m.riverBig);
      }
      g.strokeStyle = c.outline;
      g.lineWidth = 1.5 / sc(st);
      g.stroke(m.outline);
      if (hints === 'full') {
        g.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
        g.textBaseline = 'middle';
        const small = st.cw < 480;
        if (st.k >= 1.5) {
          g.font = 'italic ' + (small ? 10 : 11) + 'px system-ui, sans-serif';
          g.textAlign = 'center';
          for (const r of m.riverLabels) {
            const x = sx(st, r.x), y = sy(st, r.y);
            if (x < -60 || y < -20 || x > st.cw + 60 || y > st.ch + 20) continue;
            label(g, r.name, x, y, c.river, c.bg);
          }
        }
        g.textAlign = 'left';
        for (const city of m.cities) {
          if (city.lvl > 1 && st.k < 1.8) continue;
          const x = sx(st, city.x), y = sy(st, city.y);
          if (x < -90 || y < -20 || x > st.cw + 10 || y > st.ch + 20) continue;
          g.fillStyle = c.text;
          g.beginPath();
          g.arc(x, y, city.lvl === 1 ? 2.6 : 2, 0, Math.PI * 2);
          g.fill();
          g.font = (city.lvl === 1 ? '600 ' : '') + (small ? 10 : city.lvl === 1 ? 13 : 11) + 'px system-ui, sans-serif';
          label(g, city.name, x + 5, y - 1, city.lvl === 1 ? c.text : c.muted, c.bg);
        }
      }
    }
    st.staticDirty = false;
    st.perf.statics++;
    st.perf.staticMs += performance.now() - t0;
  }

  function label(g, text, x, y, fill, halo) {
    g.lineJoin = 'round';
    g.strokeStyle = halo;
    g.lineWidth = 3;
    g.globalAlpha = 0.85;
    g.strokeText(text, x, y);
    g.globalAlpha = 1;
    g.fillStyle = fill;
    g.fillText(text, x, y);
  }

  const PIN_W = 24, PIN_H = 34, PIN_TIP = 31;             // спрайт шпильки в CSS-пікселях; вістря — на PIN_TIP

  /// Шпилька-крапля: вістря в точці, голівка з номером місця (колір — не єдина ознака). Малюється один раз на
  /// місце в маленький канвас, далі — одна drawImage: десять обведених крапель із цифрами щокадру коштували
  /// більше за всю мапу.
  function pinSprite(st, seat) {
    const key = st.dpr + ':' + st.colors[seat % 10];
    const have = st.pins[seat];
    if (have && have.key === key) return have.c;
    const c = document.createElement('canvas');
    c.width = Math.ceil(PIN_W * st.dpr); c.height = Math.ceil(PIN_H * st.dpr);
    const g = c.getContext('2d');
    g.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
    const x = PIN_W / 2, y = PIN_TIP, hy = y - 17;
    g.fillStyle = 'rgba(0,0,0,.35)';
    g.beginPath();
    g.ellipse(x, y, 4, 1.6, 0, 0, Math.PI * 2);
    g.fill();
    g.beginPath();
    g.moveTo(x, y);
    g.arc(x, hy, 9, Math.PI * 0.72, Math.PI * 0.28);
    g.closePath();
    g.fillStyle = st.colors[seat % 10];
    g.fill();
    g.lineWidth = 1.5;
    g.strokeStyle = 'rgba(10,20,15,.85)';
    g.stroke();
    g.fillStyle = '#1a1406';
    g.font = 'bold 10px system-ui, sans-serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(String(seat + 1), x, hy + 0.5);
    st.pins[seat] = { key, c };
    return c;
  }

  function drawPin(g, st, x, y, seat, lift) {
    g.drawImage(pinSprite(st, seat), x - PIN_W / 2, y - PIN_TIP - (lift || 0), PIN_W, PIN_H);
  }

  const TAU = Math.PI * 2;
  /// Правда — червоно-біла мішень з темним обідком і білим ореолом: форма й кольори, яких нема в жодної шпильки
  /// (шпильки — краплі кольору місця з номером), тож біля жовтої «1» її не сплутати.
  function drawTruth(g, x, y, r) {
    g.fillStyle = 'rgba(255,255,255,.22)';
    g.beginPath(); g.arc(x, y, r * 1.9, 0, TAU); g.fill();
    g.fillStyle = '#e53935';
    g.beginPath(); g.arc(x, y, r, 0, TAU); g.fill();
    g.lineWidth = 2;
    g.strokeStyle = 'rgba(10,20,15,.9)';
    g.stroke();
    g.fillStyle = '#fff';
    g.beginPath(); g.arc(x, y, r * 0.66, 0, TAU); g.fill();
    g.fillStyle = '#e53935';
    g.beginPath(); g.arc(x, y, r * 0.34, 0, TAU); g.fill();
  }

  function crosshair(g, x, y) {
    g.beginPath();
    g.arc(x, y, 9, 0, Math.PI * 2);
    g.moveTo(x - 15, y); g.lineTo(x - 5, y);
    g.moveTo(x + 5, y); g.lineTo(x + 15, y);
    g.moveTo(x, y - 15); g.lineTo(x, y - 5);
    g.moveTo(x, y + 5); g.lineTo(x, y + 15);
    g.stroke();
  }

  function roundRect(g, x, y, w, h, r) {
    g.beginPath();
    g.moveTo(x + r, y);
    g.arcTo(x + w, y, x + w, y + h, r);
    g.arcTo(x + w, y + h, x, y + h, r);
    g.arcTo(x, y + h, x, y, r);
    g.arcTo(x, y, x + w, y, r);
    g.closePath();
  }

  /// Чи статичний шар намальовано саме для цього виду (масштаб, зсув, розмір, підказки) — числами, без склеювання
  /// рядка в кожному кадрі.
  function sameView(st, hints) {
    const v = st.sv;
    return v.k === st.k && v.ox === st.ox && v.oy === st.oy && v.cw === st.cw && v.ch === st.ch && v.dpr === st.dpr && v.hints === hints;
  }
  function markView(st, hints) {
    const v = st.sv;
    v.k = st.k; v.ox = st.ox; v.oy = st.oy; v.cw = st.cw; v.ch = st.ch; v.dpr = st.dpr; v.hints = hints;
  }

  /// Атлас: уся мапа (без підписів) один раз у канвас удвічі щільніший за рамку при k = 1. Під час руху
  /// кадр — лише розтягнутий атлас; лінії в ньому товщі, щоб при k = 1 виглядати як начисто.
  function atlasOf(st, hints) {
    const aw = Math.round(clamp(st.cw * st.dpr * 2, 1000, 2400));
    const key = aw + ':' + hints;
    if (st.atlas && st.atlasKey === key) return st.atlas;
    const ah = Math.round(aw * H / W);
    const c = st.atlas || document.createElement('canvas');
    c.width = aw; c.height = ah;
    const g = c.getContext('2d'), s = aw / W, u = aw / (W * st.s0 * st.dpr), m = st.map, css = st.css;
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.fillStyle = css.sea;
    g.fillRect(0, 0, aw, ah);
    g.setTransform(s, 0, 0, s, 0, 0);
    g.fillStyle = css.land;
    g.fill(m.land, 'evenodd');
    g.lineJoin = 'round';
    g.lineCap = 'round';
    if (hints !== 'none') {
      g.strokeStyle = css.border; g.lineWidth = u / s; g.stroke(m.borders);
      g.strokeStyle = css.river; g.lineWidth = 1.2 * u / s; g.stroke(m.river);
      g.lineWidth = 1.8 * u / s; g.stroke(m.riverBig);
    }
    g.strokeStyle = css.outline; g.lineWidth = 1.5 * u / s; g.stroke(m.outline);
    st.atlas = c;
    st.atlasKey = key;
    return c;
  }

  function preview(st, g, hints) {
    const a = atlasOf(st, hints);
    g.fillStyle = st.css.sea;
    g.fillRect(0, 0, st.cv.width, st.cv.height);
    g.drawImage(a, st.dpr * st.ox, st.dpr * st.oy, st.dpr * W * sc(st), st.dpr * H * sc(st));
    st.perf.previews = (st.perf.previews || 0) + 1;
  }

  function draw(st, now) {
    const t0 = performance.now();
    const ctx = st.ctx, v = V(ctx), c = st.css;
    const phase = phaseOf(ctx);
    const hints = v.hints || 'full';
    const g = st.g;
    g.setTransform(1, 0, 0, 1, 0, 0);
    if (st.staticDirty || !sameView(st, hints)) {
      if (st.map && !st.staticDirty && (st.fly || now - st.viewT < SETTLE_MS)) preview(st, g, hints);
      else { renderStatic(st, hints); markView(st, hints); g.drawImage(st.stat, 0, 0); }
    } else g.drawImage(st.stat, 0, 0);
    g.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);

    const rv = (phase === 'reveal' || phase === 'done') && v.reveal ? v.reveal : null;
    if (rv) {
      const t = now - st.revT0;
      const still = reduced();
      const tx = sx(st, rv.x), ty = sy(st, rv.y);
      const rows = rv.rows || [];
      // Порядок шарів: лінії → мішень правди → чужі шпильки → плашки км → своя шпилька. Мішень під шпильками:
      // найкращий момент раунду — своя шпилька біля цілі — не має ховатись під правдою.
      // Лінії від шпильок до правди — від найближчого, кроком 80 мс (суцільні: пунктир у програмному
      // растрі коштував пів мілісекунди на кадр).
      g.lineCap = 'round';
      g.lineWidth = 2;
      g.globalAlpha = 0.85;
      for (let i = 0; i < rows.length; i++) {
        const r = rows[i];
        if (r.x == null) continue;
        const p = still ? 1 : easeOut(clamp((t - 300 - i * 80) / 500, 0, 1));
        if (p <= 0) continue;
        const px = sx(st, r.x), py = sy(st, r.y);
        g.strokeStyle = st.colors[r.seat % 10];
        g.beginPath();
        g.moveTo(px, py);
        g.lineTo(px + (tx - px) * p, py + (ty - py) * p);
        g.stroke();
      }
      g.globalAlpha = 1;
      const grow = still ? 1 : clamp(t / 300, 0, 1);
      drawTruth(g, tx, ty, 11 * (grow < 1 ? 0.2 + easeOut(grow) * 0.95 : 1));
      const me = ctx.mine ? ctx.seat : -1;
      let mine = null;
      const taken = st.labels;
      taken.length = 0;
      for (let i = 0; i < rows.length; i++) {
        const r = rows[i];
        if (r.x == null) continue;
        const px = sx(st, r.x), py = sy(st, r.y);
        // голівка шпильки зайнята — плашку км на неї не кладемо
        taken.push(px, py - 17, 22, 0);
        if (r.seat === me) { mine = r; continue; }
        drawPin(g, st, px, py, r.seat, 0);
      }
      // підписи кілометрів на плашках — коли лінія домальована; ті, що налізли б на вже намальовані плашки чи
      // голівки шпильок, пропускаємо
      g.font = '600 11px system-ui, sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      for (let i = 0; i < rows.length; i++) {
        const r = rows[i];
        if (r.x == null || r.km == null) continue;
        const p = still ? 1 : clamp((t - 800 - i * 80) / 200, 0, 1);
        if (p <= 0) continue;
        const px = sx(st, r.x), py = sy(st, r.y);
        if (Math.hypot(tx - px, ty - py) < 46) continue;    // впритул до мішені плашка лише заважала б
        const text = km(r.km);
        const mx = (px + tx) / 2, my = (py + ty) / 2;
        const w = g.measureText(text).width + 10;
        let hit = false;
        for (let j = 0; j < taken.length; j += 4)
          if (Math.abs(taken[j] - mx) * 2 < taken[j + 2] + w && Math.abs(taken[j + 1] - my) < 20) { hit = true; break; }
        if (hit) continue;
        taken.push(mx, my, w, 0);
        g.globalAlpha = p;
        g.fillStyle = c.shade;
        roundRect(g, mx - w / 2, my - 9, w, 18, 9);
        g.fill();
        g.fillStyle = c.text;
        g.fillText(text, mx, my + 0.5);
        g.globalAlpha = 1;
      }
      // своя — останньою, поверх усього, з обідком навколо голівки: на десятьох її не треба шукати
      if (mine) {
        const px = sx(st, mine.x), py = sy(st, mine.y);
        g.lineWidth = 2.5;
        g.strokeStyle = c.text;
        g.beginPath();
        g.arc(px, py - 17, 12.5, 0, TAU);
        g.stroke();
        drawPin(g, st, px, py, mine.seat, 0);
      }
    } else if (phase === 'guess' && ctx.mine && st.pin && st.pinRound === v.round) {
      const p = reduced() ? 1 : clamp((now - st.drop) / 250, 0, 1);
      drawPin(g, st, sx(st, st.pin.x), sy(st, st.pin.y), ctx.seat || 0, (1 - easeOut(p)) * 18);
    }

    if (st.cur && canPin(st, ctx)) {
      const x = sx(st, st.cur.x), y = sy(st, st.cur.y);
      g.lineWidth = 3.5;
      g.strokeStyle = 'rgba(0,0,0,.7)';
      crosshair(g, x, y);
      g.lineWidth = 1.5;
      g.strokeStyle = c.text;
      crosshair(g, x, y);
    }

    // останні 5 секунд — рамка червоніє (пульсує, якщо рух не вимкнено)
    if (phase === 'guess' && v.endsAt) {
      const left = Date.parse(v.endsAt) - Date.now();
      if (left < 5000 && left > -600) {
        g.globalAlpha = reduced() ? 1 : 0.55 + 0.45 * Math.sin(now / 130);
        g.strokeStyle = c.danger;
        g.lineWidth = 4;
        g.strokeRect(2, 2, st.cw - 4, st.ch - 4);
        g.globalAlpha = 1;
      }
    }
    st.perf.draws++;
    st.perf.drawMs += performance.now() - t0;
  }

  /// Чи є що рухати в наступному кадрі. Ні — rAF зупиняється, і картка не коштує нічого.
  function busy(st, now) {
    const ctx = st.ctx, v = V(ctx), phase = phaseOf(ctx);
    if (st.fly) return true;
    if (st.cur && (st.keys.l || st.keys.r || st.keys.u || st.keys.d)) return true;
    // після руху — ще кадр-другий, доки статичний шар не перемалюється начисто
    if (st.map && (st.staticDirty || !sameView(st, v.hints || 'full'))) return true;
    if (reduced()) return false;
    if (phase === 'guess' && now - st.drop < 260) return true;
    if ((phase === 'reveal' || phase === 'done') && v.reveal && now - st.revT0 < 1100 + 80 * (v.reveal.rows || []).length) return true;
    if (phase === 'guess' && v.endsAt) {
      const left = Date.parse(v.endsAt) - Date.now();
      if (left < 5000 && left > -600) return true;
    }
    return false;
  }

  function kick(st) {
    if (st.raf || !st.cv) return;
    st.raf = requestAnimationFrame(() => loop(st));
  }

  function loop(st) {
    st.raf = 0;
    if (!st.cv || !st.cv.isConnected) return;
    const now = performance.now();
    const dt = st.lastT ? Math.min(0.05, (now - st.lastT) / 1000) : 0;
    st.lastT = now;
    if (st.fly) {
      const p = clamp((now - st.fly.t0) / st.fly.ms, 0, 1), e = easeInOut(p);
      const a = st.fly.from, b = st.fly.to;
      st.k = a.k * Math.pow(b.k / a.k, e);             // масштаб — геометрично: наближення відчувається рівним
      st.ox = a.ox + (b.ox - a.ox) * e;
      st.oy = a.oy + (b.oy - a.oy) * e;
      if (p >= 1) st.fly = null;
      st.viewT = now;
    }
    moveCursor(st, dt, now);
    // схована вкладка — не малюємо (стан приймаємо далі; повернеться — домалюємо)
    if (!document.hidden && st.cw) draw(st, now);
    if (busy(st, now)) st.raf = requestAnimationFrame(() => loop(st));
    else st.lastT = 0;
  }

  // ---------------------------------------------------------------------------------------------
  // курсор клавіатури й пада
  // ---------------------------------------------------------------------------------------------

  function ensureCursor(st) {
    if (st.cur) return;
    const v = V(st.ctx);
    if (st.pin && st.pinRound === v.round) st.cur = { x: st.pin.x, y: st.pin.y };
    else {
      const [x, y] = toMap(st, st.cw / 2, st.ch / 2);
      st.cur = { x, y };
    }
  }

  /// Перші 150 мс — повільно (дрібно підправити), далі швидко. Уперся в край наближеної мапи — їде мапа.
  function moveCursor(st, dt, now) {
    if (!st.cur || !dt) return;
    const dx = st.keys.r - st.keys.l, dy = st.keys.d - st.keys.u;
    if (!dx && !dy) return;
    const speed = (now - st.keyT0 < 150 ? 200 : 420) * dt;
    const len = Math.hypot(dx, dy);
    let cx = sx(st, st.cur.x), cy = sy(st, st.cur.y);
    cx += dx / len * speed;
    cy += dy / len * speed;
    const m = 14;
    let px = 0, py = 0;
    if (cx < m) { px = m - cx; cx = m; } else if (cx > st.cw - m) { px = st.cw - m - cx; cx = st.cw - m; }
    if (cy < m) { py = m - cy; cy = m; } else if (cy > st.ch - m) { py = st.ch - m - cy; cy = st.ch - m; }
    if (px || py) {
      const ox = st.ox, oy = st.oy;
      st.ox += px; st.oy += py;
      clampView(st);
      if (st.ox !== ox || st.oy !== oy) st.viewT = now;
    }
    const [mx, my] = toMap(st, cx, cy);
    st.cur.x = clamp(mx, 0, W);
    st.cur.y = clamp(my, 0, H);
  }

  // ---------------------------------------------------------------------------------------------
  // дії
  // ---------------------------------------------------------------------------------------------

  function placePin(st, mx, my) {
    const ctx = st.ctx;
    if (!canPin(st, ctx)) return;
    const v = V(ctx);
    const p = { x: Math.round(clamp(mx, 0, W)), y: Math.round(clamp(my, 0, H)) };
    const was = st.pinRound === v.round ? st.pin : null;
    st.pin = p;
    st.pinRound = v.round;
    st.drop = performance.now();
    tone([220], 40, 0.08, true);
    kick(st);
    paintBar(st.root, ctx);
    paintChips(st.root, ctx);
    ctx.act('guess', { x: p.x, y: p.y }).then((r) => {
      // сервер не прийняв (час вийшов, «Готово» з іншої вкладки) — повертаємо, як було
      if (r && !r.ok && st.pin === p) { st.pin = was; kick(st); paintBar(st.root, st.ctx); paintChips(st.root, st.ctx); }
    }).catch(() => {});
  }

  function readyOrNext(st) {
    const ctx = st.ctx, v = V(ctx), phase = phaseOf(ctx);
    if (!ctx.mine || !ctx.playing) return;
    if (phase === 'guess' && st.pin && st.pinRound === v.round && st.readyRound !== v.round) {
      const round = v.round;
      st.readyRound = round;
      st.cur = null;
      paintBar(st.root, ctx);
      paintChips(st.root, ctx);
      kick(st);
      ctx.act('ready').then((r) => {
        if (r && !r.ok && st.readyRound === round && !/^Уже/.test(r.message || '')) {
          st.readyRound = -1;
          paintBar(st.root, st.ctx);
          paintChips(st.root, st.ctx);
        }
      }).catch(() => {});
    } else if (phase === 'reveal' && st.nextRound !== v.round) {
      st.nextRound = v.round;
      paintBar(st.root, ctx);
      ctx.act('next').catch(() => {});
    }
  }

  // ---------------------------------------------------------------------------------------------
  // фото
  // ---------------------------------------------------------------------------------------------

  function openFull(st) {
    const url = V(st.ctx).photo;
    if (!url || st.full || phaseOf(st.ctx) === 'between') return;
    const el = document.createElement('div');
    el.className = 'geofull';
    el.innerHTML = '<img alt="Фото місця" draggable="false"><button type="button" class="geofullx" data-pad-first>✕ назад до мапи</button>';
    el.querySelector('img').src = url;
    el.addEventListener('click', () => closeFull(st));
    document.body.appendChild(el);
    st.full = el;
  }

  function closeFull(st) {
    if (!st.full) return;
    st.full.remove();
    st.full = null;
  }

  function paintPhoto(root, ctx) {
    const st = root._geo, v = V(ctx), phase = phaseOf(ctx);
    const frame = root.querySelector('.geoframe');
    const img = root.querySelector('.geoimg');
    const url = phase === 'lobby' ? null : v.photo || null;
    if (url && img.dataset.src !== url) {
      img.dataset.src = url;
      root.querySelector('.geoerr').hidden = true;
      img.src = url;
    }
    if (!url && img.dataset.src) { img.dataset.src = ''; img.removeAttribute('src'); }
    frame.classList.toggle('empty', !url);
    frame.classList.toggle('geoblur', phase === 'between');
    const curtain = root.querySelector('.geocurtain');
    const ct = phase === 'between' ? 'Раунд ' + v.round + ' з ' + v.rounds
      : !url ? '📷 Тут буде фото' : '';
    if (curtain.textContent !== ct) curtain.textContent = ct;
    curtain.hidden = !ct;
    root.querySelector('.geofullbtn').hidden = !url || phase === 'between';
    if (st.full && (!url || phase === 'between')) closeFull(st);
    if (st.full && url) { const fi = st.full.querySelector('img'); if (fi.getAttribute('src') !== url) fi.src = url; }

    // підпис — лише після розкриття: назва, область, автор і ліцензія знімка
    const cap = root.querySelector('.geocap');
    const rv = (phase === 'reveal' || phase === 'done') ? v.reveal : null;
    let html = '';
    if (rv) {
      const p = rv.photo || {};
      const lic = p.licenseUrl
        ? '<a href="' + ctx.esc(p.licenseUrl) + '" target="_blank" rel="noopener">' + ctx.esc(p.license) + '</a>'
        : ctx.esc(p.license || '');
      html = '<b>' + ctx.esc(rv.name) + '</b><span class="muted"> · ' + ctx.esc(rv.region) + '</span>'
        + '<div class="small muted">Фото: ' + (p.page ? '<a href="' + ctx.esc(p.page) + '" target="_blank" rel="noopener">' + ctx.esc(p.author) + '</a>' : ctx.esc(p.author || ''))
        + ' · ' + lic + '</div>';
    }
    if (cap.dataset.sig !== html) { cap.dataset.sig = html; cap.innerHTML = html; }
    cap.hidden = !html;
  }

  // ---------------------------------------------------------------------------------------------
  // шапка, кнопка, розкриття, рахунок
  // ---------------------------------------------------------------------------------------------

  function plural(n, one, few, many) {
    const d = n % 10, hh = n % 100;
    return d === 1 && hh !== 11 ? one : d >= 2 && d <= 4 && (hh < 12 || hh > 14) ? few : many;
  }

  function paintTop(root, ctx) {
    const v = V(ctx), phase = phaseOf(ctx);
    const top = root.querySelector('.geotop');
    const no = root.querySelector('.georound');
    const t = phase === 'lobby' || !v.rounds ? '' : phase === 'done' ? 'Зіграно ' + v.rounds + ' ' + plural(v.rounds, 'раунд', 'раунди', 'раундів')
      : 'Раунд ' + v.round + ' з ' + v.rounds;
    if (no.textContent !== t) no.textContent = t;
    if (ctx.playing && v.endsAt && (phase === 'between' || phase === 'guess' || phase === 'reveal')) {
      ctx.ui.timerArc(top, v.endsAt, v.phaseMs || 1000);
    } else {
      const arc = top.querySelector(':scope > .garc');
      if (arc) { if (arc._arc) arc._arc.stop(); arc.remove(); }
    }
    paintChips(root, ctx);
  }

  function paintChips(root, ctx) {
    const st = root._geo, v = V(ctx), phase = phaseOf(ctx);
    const box = root.querySelector('.geochips');
    if (!box) return;
    const f = fresh(ctx);
    const pinned = (f && f.pin) || v.pinned || [];
    const ready = (f && f.rdy) || v.ready || [];
    const next = (f && f.nxt) || v.next || [];
    const pts = {};
    if (phase === 'reveal' && v.reveal) for (const r of v.reveal.rows || []) pts[r.seat] = r.points;
    const show = ctx.playing && phase !== 'lobby' && phase !== 'done';
    const html = !show ? '' : seatsOf(ctx).map((i) => {
      const mine = i === ctx.seat;
      const isReady = phase === 'guess' && (ready.includes(i) || (mine && st.readyRound === v.round));
      const isPinned = phase === 'guess' && (pinned.includes(i) || (mine && st.pinRound === v.round));
      const mark = phase === 'guess' ? (isReady ? '✓' : isPinned ? '📍' : '')
        : phase === 'reveal' ? (pts[i] != null ? '+' + pts[i] : '') + (next.includes(i) ? ' →' : '') : '';
      // на вузькій картці (телефон, десятеро) чіп — лише кружок із номером: заповнений — готовий, обведений
      // товще — поставив; нік — у підказці. Так десять чіпів лягають в один рядок, а мапа — вище згину.
      return '<span class="geochip ' + SEAT_CLASS[i % 10] + (isReady ? ' on' : '') + (isPinned ? ' pin' : '') + (mine ? ' me' : '')
        + '" title="' + ctx.esc(nick(ctx, i)) + '">'
        + '<i class="geodot"></i><i class="geonum">' + (i + 1) + '</i><span class="geonick">' + ctx.esc(nick(ctx, i)) + '</span>'
        + (mark ? '<b>' + mark + '</b>' : '') + '</span>';
    }).join('');
    if (box.dataset.sig !== html) { box.dataset.sig = html; box.innerHTML = html; }
  }

  function paintBar(root, ctx) {
    const st = root._geo, v = V(ctx), phase = phaseOf(ctx);
    const bar = root.querySelector('.geobar');
    if (!bar) return;
    const btn = bar.querySelector('.geogo');
    const hint = bar.querySelector('.geohint');
    const coarse = ctx.ui.coarse();
    const seated = ctx.mine && ctx.playing;
    const show = seated && (phase === 'between' || phase === 'guess' || phase === 'reveal');
    let label = '', dis = true, h = '';
    if (phase === 'lobby') h = RULES;
    else if (phase === 'between') { label = 'Готуйсь…'; h = 'Фото проявляється — за мить мапа оживе'; }
    else if (phase === 'guess') {
      const pinned = st.pin && st.pinRound === v.round;
      if (!ctx.mine) h = 'Гравці ставлять шпильки…';
      else if (st.readyRound === v.round) { label = 'Чекаємо решту…'; h = 'Шпилька зафіксована'; }
      else if (pinned) { label = 'Готово ✓'; dis = false; h = 'Шпилька зарахується й так; «Готово» — щоб не чекати' + (coarse ? '' : ' (Enter)'); }
      else { label = 'Постав шпильку'; h = coarse ? 'Тапни на мапі, де це знято · два пальці — масштаб' : 'Клікни на мапі, де це знято · колесо — масштаб · тягни мапу — рух'; }
    } else if (phase === 'reveal') {
      const f = fresh(ctx);
      if (st.nextRound === v.round || (f && (f.nxt || []).includes(ctx.seat))) label = 'Чекаємо решту…';
      else { label = v.round >= (v.rounds || 0) ? 'Підсумок →' : 'Далі →'; dis = false; }
    }
    bar.hidden = !show && !h;
    btn.hidden = !show;
    // кнопки масштабу на пальці — тут, під мапою, а не на ній (там вони закривали Сумщину й шпильки)
    const zb = bar.querySelector('.geozoom');
    if (zb) zb.hidden = !(show && (phase === 'guess' || phase === 'reveal'));
    if (btn.textContent !== label) btn.textContent = label;
    if (btn.disabled !== dis) btn.disabled = dis;
    if (hint.textContent !== h) hint.textContent = h;
  }

  /// «Ти: 246 км · +1 282 · п'яте місце з 10» — щоб на десятьох не шукати себе у двох таблицях.
  function meHtml(ctx, rows) {
    if (!ctx.mine || rows.length < 1) return '';
    const mine = rows.find((r) => r.seat === ctx.seat);
    if (!mine) return '';
    if (mine.x == null) return '<div class="geome none">Ти цього разу без шпильки</div>';
    const place = 1 + rows.filter((r) => r.points > mine.points).length;
    const where = rows.length > 1 ? ' · ' + (PLACE[place - 1] || place + '-е') + ' місце з ' + rows.length : '';
    return '<div class="geome' + (mine.bull ? ' bull' : '') + '"><span>' + (mine.bull ? '🎯 В яблучко! ' : 'Ти: ') + km(mine.km) + '</span>'
      + '<b>+' + num(mine.points) + '</b><span class="muted">' + where + '</span></div>';
  }

  function revealHtml(ctx, v) {
    const rv = v.reveal;
    if (!rv) return '';
    const rows = rv.rows || [];
    const say = rv.say ? '<div class="geosay"><img src="/static/glek.svg" alt=""><span>' + ctx.esc(rv.say) + '</span></div>' : '';
    if (!rows.length) return say;
    // самому таблиця з одного рядка лише повторювала б «Ти: …»
    if (rows.length === 1 && ctx.mine && rows[0].seat === ctx.seat) return meHtml(ctx, rows) + say;
    return meHtml(ctx, rows) + '<div class="georows">' + rows.map((r, n) =>
      '<div class="georow' + (r.best ? ' best' : '') + (r.x == null ? ' none' : '') + (ctx.mine && r.seat === ctx.seat ? ' me' : '') + '" style="--n:' + n + '">'
      + '<span class="geon ' + SEAT_CLASS[r.seat % 10] + '"><i class="geodot"></i>' + (r.best ? '🏆 ' : '') + (r.bull ? '🎯 ' : '')
      + ctx.esc(nick(ctx, r.seat)) + '</span>'
      + '<span class="geokm">' + (r.km == null ? '— без шпильки' : km(r.km)) + '</span>'
      + '<b class="geop">' + (r.points ? '+' + r.points : '0') + '</b></div>').join('') + '</div>' + say;
  }

  function scoreHtml(ctx, v) {
    const sc = v.scores || [];
    const win = (v.result && v.result.winners) || [];
    const done = phaseOf(ctx) === 'done' && !!v.result;
    const left = v.left || [];
    // усі, хто грав цю партію (і ті, хто вже встав), — з виду; старий вид без nicks — ті, хто сидить
    const seats = v.nicks ? v.nicks.map((n, i) => (n ? i : -1)).filter((i) => i >= 0) : seatsOf(ctx);
    if (!seats.length || (!ctx.playing && !v.result)) return '';
    // до першого розкриття всі по нулях — рахунок лише займав би місце
    if (!done && !seats.some((i) => sc[i])) return '';
    // 🏆 — лише коли було кого перемагати; самому (тренування, стіл на одного) — просто рахунок
    const crown = seats.length > 1;
    return '<div class="geoshead muted small">' + (done ? 'Підсумок' : 'Рахунок') + '</div>' + seats.slice()
      .sort((a, b) => (sc[b] || 0) - (sc[a] || 0) || a - b)
      .map((i) => {
        // черепки за очки — так само, як рахує сервер (GeoMatch.PointsPerShard)
        const shards = done ? Math.floor((sc[i] || 0) / 5000) : 0;
        const gone = left.includes(i);
        const won = crown && done && win.includes(i);
        return '<div class="geosrow ' + SEAT_CLASS[i % 10] + (won ? ' win' : '') + (gone ? ' gone' : '') + (ctx.mine && i === ctx.seat ? ' me' : '') + '">'
          + '<span><i class="geodot"></i>' + (won ? '🏆 ' : '') + ctx.esc(nick(ctx, i))
          + (gone ? ' <i class="muted small">· встав</i>' : '') + '</span>'
          + (shards > 0 ? '<i class="geoshard" title="черепки за очки">🏺+' + shards + '</i>' : '')
          + '<b>' + num(sc[i]) + '</b></div>';
      }).join('');
  }

  function recapHtml(ctx, v) {
    const list = v.recap || [];
    if (!list.length) return '';
    const players = (v.nicks || []).filter((n) => n).length;
    return '<details class="georecap" open><summary>Як це було · ' + list.length + ' ' + plural(list.length, 'раунд', 'раунди', 'раундів') + '</summary><ol>'
      + list.map((r) => {
        // 🏆 — лише ті, в кого він був у розкритті (самому — нікого); інакше — чия це відстань, без трофея
        const solo = (ctx.room && ctx.room.maxPlayers) === 1 || players < 2;
        const best = r.best || [];
        const who = best.length ? '🏆 ' + best.map((i) => ctx.esc(nick(ctx, i))).join(', ') + ' — '
          : !solo && r.top != null ? ctx.esc(nick(ctx, r.top)) + ' — ' : '';
        const got = r.km == null ? '<span class="muted">без шпильки</span>'
          : who + km(r.km) + (r.points ? ' <b>+' + r.points + '</b>' : '');
        return '<li>' + (r.photo ? '<img src="' + ctx.esc(r.photo) + '" alt="" loading="lazy">' : '<i class="geothumb"></i>')
          + '<span class="geort"><b>' + ctx.esc(r.name) + '</b><span class="muted small">' + ctx.esc(r.region) + '</span>'
          + '<span class="small">' + got + '</span></span></li>';
      }).join('') + '</ol></details>';
  }

  /// «Ще раз» каркаса — під карткою, на 1280×800 нижче згину. П'єдестал має свою кнопку, що тисне ту саму.
  const againOf = (root) => (root.parentElement && root.parentElement.querySelector('.gbtns [data-do="Rematch"]')) || null;

  /// П'єдестал у підсумку: хто виграв партію — першим рядком і великими літерами (у розкритті останнього
  /// раунду 🏆 означає «найближчий у раунді», і плеєри плутали), нижче — твоє місце, поруч «Ще раз».
  function podiumHtml(root, ctx, v) {
    if (phaseOf(ctx) !== 'done' || !v.result || !v.rounds) return '';
    const sc = v.scores || [];
    const seats = v.nicks ? v.nicks.map((n, i) => (n ? i : -1)).filter((i) => i >= 0) : seatsOf(ctx);
    if (!seats.length) return '';
    const win = v.result.winners || [];
    let head, sub = '';
    if (seats.length === 1) {
      const i = seats[0];
      head = '<span class="geopodt">🎯 ' + (ctx.mine ? 'Твій результат' : ctx.esc(nick(ctx, i))) + ': <b>' + num(sc[i]) + '</b>'
        + ' <span class="muted">з ' + num(v.rounds * 5000) + '</span></span>';
    } else if (!win.length) {
      head = '<span class="geopodt">🤷 Ніхто нікуди не влучив</span>';
    } else {
      head = '<span class="geopodt">🏆 ' + win.map((i) => '<b><i class="geodot ' + SEAT_CLASS[i % 10] + '"></i>' + ctx.esc(nick(ctx, i)) + '</b>').join(' і ')
        + ' — ' + num(sc[win[0]]) + (win.length > 1 ? ' <span class="muted">(нічия на першому)</span>' : '') + '</span>';
      if (ctx.mine && seats.includes(ctx.seat)) {
        const place = 1 + seats.filter((i) => (sc[i] || 0) > (sc[ctx.seat] || 0)).length;
        sub = win.includes(ctx.seat) ? 'Це ти — перше місце з ' + seats.length + '!'
          : 'Ти — ' + (PLACE[place - 1] || place + '-е') + ' місце з ' + seats.length + ' · ' + num(sc[ctx.seat]);
      }
    }
    const again = againOf(root) ? '<button type="button" class="primary geoagain" data-pad-first>Ще раз</button>' : '';
    return '<div class="geopodl">' + head + (sub ? '<span class="geopods muted">' + sub + '</span>' : '') + '</div>' + again;
  }

  function paintPodium(root, ctx) {
    const pod = root.querySelector('.geopod');
    const html = podiumHtml(root, ctx, V(ctx));
    if (pod.dataset.sig !== html) {
      pod.dataset.sig = html;
      pod.innerHTML = html;
      const b = pod.querySelector('.geoagain');
      if (b) b.onclick = () => { const a = againOf(root); if (a) a.click(); };
    }
    pod.hidden = !html;
    // Пад: після партії кільце — на «Ще раз», а не на «← Лобі» (Ⓐ після «Підсумок →» виводив зі столу).
    const st = root._geo, b = pod.querySelector('.geoagain');
    const key = b ? (ctx.room ? ctx.room.round : 0) + ':' + (V(ctx).rounds || 0) : '';
    if (b && st.podFocus !== key) {
      st.podFocus = key;
      try { if (window.HPad && HPad.on && HPad.focus) HPad.focus(b); } catch { /* без пада — і так видно */ }
    }
  }

  function paintResult(root, ctx) {
    const v = V(ctx), phase = phaseOf(ctx);
    const rev = root.querySelector('.georev');
    // у підсумку таблиці останнього раунду нема: її 🏆 («найближчий у раунді») плутали з переможцем партії
    const html = phase === 'reveal' ? revealHtml(ctx, v) : '';
    if (rev.dataset.sig !== html) { rev.dataset.sig = html; rev.innerHTML = html; }
    const score = root.querySelector('.geoscore');
    const sh = phase === 'lobby' ? '' : scoreHtml(ctx, v);
    if (score.dataset.sig !== sh) { score.dataset.sig = sh; score.innerHTML = sh; }
    const recap = root.querySelector('.georecapbox');
    const rh = phase === 'done' ? recapHtml(ctx, v) : '';
    // порівнюємо з тим, що малювали, а не з innerHTML: інакше кожне оновлення згортало б розгорнуте людиною
    if (recap.dataset.sig !== rh) { recap.dataset.sig = rh; recap.innerHTML = rh; }
    const res = root.querySelector('.georesult');
    res.hidden = !html && !sh;
    // у підсумку таблиці раунду нема — рахунок на всю ширину, кількома стовпчиками
    res.classList.toggle('done', phase === 'done');
  }

  // ---------------------------------------------------------------------------------------------
  // оновлення з виду
  // ---------------------------------------------------------------------------------------------

  function sync(st, ctx) {
    const v = V(ctx), phase = phaseOf(ctx);
    const match = ctx.room ? ctx.room.round : 0;
    const key = phase + ':' + v.round + ':' + match;
    if (key !== st.lastKey) {
      const was = st.lastPhase;
      st.lastKey = key;
      st.lastPhase = phase;
      // новий раунд, «Ще раз», лобі — чиста мапа й свій пін забуто
      if (phase === 'between' || phase === 'lobby' || (phase === 'guess' && was !== 'between')) {
        if (phase !== 'guess') { st.pin = null; st.pinRound = -1; }
        st.readyRound = -1;
        st.nextRound = -1;
        st.cur = null;
        if (st.k !== 1 || st.fit) home(st);
      }
      if ((phase === 'reveal' || phase === 'done') && v.reveal && st.revKey !== match + ':' + v.round) {
        st.revKey = match + ':' + v.round;
        st.revT0 = performance.now();
        st.cur = null;
        const pts = [[v.reveal.x, v.reveal.y]];
        for (const r of v.reveal.rows || []) if (r.x != null) pts.push([r.x, r.y]);
        flyTo(st, pts, pts.length > 1 ? KMAX : 4);
        const my = (v.reveal.rows || []).find((r) => r.seat === ctx.seat);
        if (my && my.bull && phase === 'reveal') tone([660, 880], 80, 0.06, false);
      }
    }
    // свій пін: вид — правда, але пін, поставлений після виду, лишаємо (сервер видів на шпильку не шле)
    if (phase === 'guess' && ctx.mine && v.my) {
      if (!st.pin || st.pinRound !== v.round) { st.pin = { x: v.my.x, y: v.my.y }; st.pinRound = v.round; st.drop = 0; }
      if (v.my.ready) st.readyRound = v.round;
    }
    if (st.hints !== v.hints) { st.hints = v.hints; st.staticDirty = true; }
    // рамка червоніє за 5 с до кінця — розбудити малювання саме тоді (до того кадрів нема взагалі)
    clearTimeout(st.redTimer);
    if (phase === 'guess' && v.endsAt) {
      const wait = Date.parse(v.endsAt) - 5000 - Date.now();
      if (wait > 0) st.redTimer = setTimeout(() => kick(st), wait + 20);
    }
    kick(st);
  }

  function paint(root, ctx) {
    const st = root._geo;
    sync(st, ctx);
    paintTop(root, ctx);
    paintPodium(root, ctx);
    paintPhoto(root, ctx);
    paintBar(root, ctx);
    paintResult(root, ctx);
    roundInView(st, ctx);
    const mapEl = root.querySelector('.geomap');
    mapEl.classList.toggle('can', canPin(st, ctx));
    mapEl.classList.toggle('dim', phaseOf(ctx) === 'between');
  }

  /// На початку кожного раунду фото, мапа й «Готово» мають бути в полі зору: на телефоні з десятьма гравцями
  /// мапа починалась нижче згину, а низ її (Крим, Донеччина) ховали смуга радіо й вкладки. Раз на раунд, лише
  /// тому, хто грає, і лише коли щось справді не влазить — підкручуємо сторінку рівно настільки, щоб низ
  /// кнопки (або хоч мапи) став над нижніми смугами, а верх фото не заїхав під шапку сайту.
  function roundInView(st, ctx) {
    const v = V(ctx), phase = phaseOf(ctx);
    if (!ctx.mine || !ctx.playing || (phase !== 'between' && phase !== 'guess')) return;
    // і в «готуйсь», і на початку вгадування: у guess під мапою з'являється рядок масштабу — кнопка нижчає
    const key = (ctx.room ? ctx.room.round : 0) + ':' + v.round + ':' + phase;
    if (st.scrolled === key) return;
    st.scrolled = key;
    requestAnimationFrame(() => {
      const main = st.root.querySelector('.geomain'), map = st.root.querySelector('.geomap');
      if (!main || !map || !map.offsetParent) return;
      const bar = st.root.querySelector('.geobar');
      const last = bar && !bar.hidden && bar.offsetParent ? bar : map;
      const cs = getComputedStyle(document.documentElement);
      const bars = (parseFloat(cs.getPropertyValue('--tabs-h')) || 0) + (parseFloat(cs.getPropertyValue('--mini-h')) || 0);
      const head = document.querySelector('header');
      const top = head ? Math.max(0, head.getBoundingClientRect().bottom) : 0;
      const over = last.getBoundingClientRect().bottom - (innerHeight - bars - 8);   // > 0 — низ сховано
      const room = main.getBoundingClientRect().top - top - 6;                        // < 0 — верх фото під шапкою
      // униз — поки низ не видно, але не далі, ніж верх фото до шапки; угору — якщо фото заїхало під шапку
      // й знизу є запас; не влазить узагалі (телефон лежачи) — не чіпаємо, людина прокрутить сама
      const dy = over > 1 ? (room > 1 ? Math.min(over, room) : 0) : room < -1 ? Math.max(room, over) : 0;
      if (Math.abs(dy) > 1) window.scrollBy({ top: dy, behavior: reduced() ? 'auto' : 'smooth' });
    });
  }

  // ---------------------------------------------------------------------------------------------
  // мишка й палець
  // ---------------------------------------------------------------------------------------------

  function local(st, e) {
    const r = st.cv.getBoundingClientRect();
    return [e.clientX - r.left, e.clientY - r.top];
  }

  function bindPointer(st) {
    const el = st.cv;
    el.addEventListener('pointerdown', (e) => {
      if (e.button > 0) return;
      const [x, y] = local(st, e);
      st.ptrs.set(e.pointerId, { x, y });
      try { el.setPointerCapture(e.pointerId); } catch { /* не критично */ }
      if (st.ptrs.size === 1) st.down = { id: e.pointerId, x, y, lx: x, ly: y, moved: false };
      else if (st.ptrs.size === 2) {
        const [a, b] = [...st.ptrs.values()];
        st.pinch = { d: Math.hypot(a.x - b.x, a.y - b.y) || 1, mx: (a.x + b.x) / 2, my: (a.y + b.y) / 2 };
        if (st.down) st.down.moved = true;           // два пальці — це вже не тап
      }
      st.fly = null;
      if (e.pointerType === 'mouse') st.cur = null;   // мишка поруч — хрестик клавіатури ховаємо
    });
    el.addEventListener('pointermove', (e) => {
      if (!st.ptrs.has(e.pointerId)) return;
      const [x, y] = local(st, e);
      st.ptrs.set(e.pointerId, { x, y });
      if (st.ptrs.size >= 2 && st.pinch) {
        const [a, b] = [...st.ptrs.values()];
        const d = Math.hypot(a.x - b.x, a.y - b.y) || 1;
        const mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
        st.fit = null;
        st.ox += mx - st.pinch.mx;
        st.oy += my - st.pinch.my;
        zoomAt(st, mx, my, d / st.pinch.d);
        clampView(st);
        st.pinch = { d, mx, my };
        moved(st);
        return;
      }
      const dn = st.down;
      if (!dn || dn.id !== e.pointerId) return;
      if (!dn.moved && Math.hypot(x - dn.x, y - dn.y) > DRAG_PX) { dn.moved = true; el.classList.add('drag'); }
      if (dn.moved) {
        st.fit = null;
        st.ox += x - dn.lx;
        st.oy += y - dn.ly;
        clampView(st);
        moved(st);
      }
      dn.lx = x; dn.ly = y;
    });
    const up = (e, cancel) => {
      if (!st.ptrs.has(e.pointerId)) return;
      st.ptrs.delete(e.pointerId);
      if (st.ptrs.size < 2) st.pinch = null;
      const dn = st.down;
      if (dn && dn.id === e.pointerId) {
        st.down = null;
        el.classList.remove('drag');
        // Людськості тут не питаємо: скрипт, що тицяє в мапу, правди не знає — сервер однаково суддя всім.
        if (!dn.moved && !cancel) {
          const [x, y] = local(st, e);
          const [mx, my] = toMap(st, x, y);
          placePin(st, mx, my);
        }
      }
      kick(st);
    };
    el.addEventListener('pointerup', (e) => up(e, false));
    el.addEventListener('pointercancel', (e) => up(e, true));
    el.addEventListener('wheel', (e) => {
      if (!e.deltaY) return;
      e.preventDefault();
      const [x, y] = local(st, e);
      const steps = clamp(-e.deltaY / (e.deltaMode === 1 ? 3 : 100), -4, 4);
      zoomAt(st, x, y, Math.pow(1.15, steps));
    }, { passive: false });
    el.addEventListener('dblclick', (e) => {
      const [x, y] = local(st, e);
      zoomAt(st, x, y, 2);
    });
  }

  // ---------------------------------------------------------------------------------------------
  // модуль
  // ---------------------------------------------------------------------------------------------

  const KEYDIR = { ArrowLeft: 'l', KeyA: 'l', ArrowRight: 'r', KeyD: 'r', ArrowUp: 'u', KeyW: 'u', ArrowDown: 'd', KeyS: 'd' };

  const common = {
    icon: ICON,
    seatClass: SEAT_CLASS,

    mount(root, ctx) {
      const st = state(root, ctx);
      const zoom = '<button type="button" data-z="in" data-pad-skip aria-label="Наблизити">＋</button>'
        + '<button type="button" data-z="out" data-pad-skip aria-label="Віддалити">−</button>'
        + '<button type="button" data-z="home" data-pad-skip aria-label="Уся мапа">⌂</button>';
      root.innerHTML = '<div class="geowrap">'
        + '<div class="geotop"><span class="georound"></span><div class="geochips"></div>'
        + '<button type="button" class="geosnd" data-pad-skip title="Звук">🔈</button></div>'
        + '<div class="geopod" hidden></div>'
        + '<div class="geomain">'
        + '<div class="geophoto"><div class="geoframe empty">'
        + '<img class="geoimg" alt="Фото місця" draggable="false">'
        + '<div class="geocurtain"></div>'
        + '<div class="geoerr" hidden>Фото не завантажилось <button type="button">⟳</button></div>'
        + '<button type="button" class="geofullbtn" data-pad-skip title="Фото на весь екран (F)" aria-label="Фото на весь екран">⛶</button>'
        + '</div><div class="geocap" hidden></div></div>'
        + '<div class="geomap"><canvas class="geocanvas" aria-label="Мапа України: тицьни, де знято фото"></canvas>'
        + '<div class="geomapmsg">мапа вантажиться…</div>'
        + '<div class="geozoom geozoomm">' + zoom + '</div></div>'
        + '</div>'
        + '<div class="geobar"><div class="geozoom geozoomb" hidden>' + zoom + '</div><span class="geohint muted small"></span>'
        + '<button type="button" class="primary geogo" data-pad-first hidden></button></div>'
        + '<div class="georesult" hidden><div class="georev"></div><div class="geoscore"></div></div>'
        + '<div class="georecapbox"></div>'
        + '</div>';
      st.cv = root.querySelector('.geocanvas');
      st.g = st.cv.getContext('2d');
      st.stat = document.createElement('canvas');
      st.sg = st.stat.getContext('2d');
      cssColors(st, ctx);
      bindPointer(st);

      const img = root.querySelector('.geoimg');
      root.querySelector('.geogo').onclick = () => readyOrNext(st);
      root.querySelector('.geofullbtn').onclick = () => openFull(st);
      img.addEventListener('error', () => { if (img.dataset.src) root.querySelector('.geoerr').hidden = false; });
      img.addEventListener('load', () => { root.querySelector('.geoerr').hidden = true; });
      img.addEventListener('click', () => openFull(st));
      root.querySelector('.geoerr button').onclick = () => { img.src = img.dataset.src + '#' + Date.now(); };
      root.querySelectorAll('.geozoom').forEach((z) => z.addEventListener('click', (e) => {
        const b = e.target.closest('button');
        if (!b) return;
        if (b.dataset.z === 'home') home(st); else zoomKey(st, b.dataset.z === 'in' ? 1.5 : 1 / 1.5);
      }));
      const snd = root.querySelector('.geosnd');
      const paintSnd = () => { snd.textContent = soundOn() ? '🔈' : '🔇'; snd.title = soundOn() ? 'Звук є — вимкнути' : 'Звук вимкнено — увімкнути'; };
      snd.onclick = () => { try { localStorage.setItem('geoSound', soundOn() ? '0' : '1'); } catch { /* ні то ні */ } paintSnd(); };
      paintSnd();

      st.keyup = (e) => {
        const d = KEYDIR[e.code];
        if (d && st.keys[d]) { st.keys[d] = 0; kick(st); }
      };
      document.addEventListener('keyup', st.keyup);
      st.onVis = () => { if (!document.hidden) { st.staticDirty = true; kick(st); } };
      document.addEventListener('visibilitychange', st.onVis);
      st.ro = new ResizeObserver(() => resize(st));
      st.ro.observe(root.querySelector('.geomap'));

      loadMap().then((m) => {
        if (!st.cv) return;
        st.map = m;
        const msg = root.querySelector('.geomapmsg');
        if (msg) msg.hidden = true;
        st.staticDirty = true;
        kick(st);
      }).catch(() => {
        const msg = root.querySelector('.geomapmsg');
        if (msg) msg.textContent = 'мапа не завантажилась — онови сторінку';
      });
      paint(root, ctx);
      resize(st);
    },

    update(root, ctx) {
      if (!root._geo) return;
      state(root, ctx);
      paint(root, ctx);
    },

    /// Кадр — лише чіпи й кнопка: фаза міняється тільки разом із видом.
    frame(root, ctx) {
      if (!root._geo) return;
      paintChips(root, ctx);
      paintBar(root, ctx);
    },

    onKey(e, ctx) {
      const st = ctx._geo;
      if (!st || !st.cv) return false;
      const phase = phaseOf(ctx);
      if (e.code === 'Escape') {
        if (st.full) { closeFull(st); return true; }
        return false;
      }
      if (e.code === 'KeyF') {
        if (!V(ctx).photo || phase === 'lobby') return false;
        if (st.full) closeFull(st); else openFull(st);
        return true;
      }
      if (st.full) return false;
      // «Готуйсь…» (2 с): пробіл/Enter — це Ⓐ/RT пада, і вони не мають тиснути нічого з каркаса, а стрілки —
      // гортати сторінку; мапа ще неактивна, тож просто ковтаємо
      if (phase === 'between' && ctx.mine && ctx.playing && (e.code === 'Space' || e.code === 'Enter' || e.code === 'NumpadEnter' || KEYDIR[e.code])) return true;
      if (e.code === 'Equal' || e.code === 'NumpadAdd' || e.code === 'BracketRight') { zoomKey(st, 1.5); return true; }
      if (e.code === 'Minus' || e.code === 'NumpadSubtract' || e.code === 'BracketLeft') { zoomKey(st, 1 / 1.5); return true; }
      if (e.code === 'Digit0' || e.code === 'Numpad0' || e.code === 'Home') { home(st); return true; }
      if (e.code === 'Enter' || e.code === 'NumpadEnter') {
        if (!ctx.mine || !ctx.playing || (phase !== 'guess' && phase !== 'reveal')) return false;
        readyOrNext(st);
        return true;
      }
      if (e.code === 'Space' && phase === 'reveal' && ctx.mine && ctx.playing) {
        if (!e.repeat) readyOrNext(st);
        return true;
      }
      if (!canPin(st, ctx)) return false;
      const d = KEYDIR[e.code];
      if (d) {
        if (!st.cur) { ensureCursor(st); st.keyT0 = performance.now(); }
        if (!st.keys[d]) {
          if (!(st.keys.l || st.keys.r || st.keys.u || st.keys.d)) st.keyT0 = performance.now();
          st.keys[d] = 1;
        }
        kick(st);
        return true;
      }
      if (e.code === 'Space') {
        if (e.repeat) return true;
        if (!st.cur) { ensureCursor(st); kick(st); return true; }
        placePin(st, st.cur.x, st.cur.y);
        return true;
      }
      return false;
    },

    status(ctx) {
      const st = ctx._geo, v = V(ctx), phase = phaseOf(ctx);
      // тренування: замість сухого «Готово» — скільки набрав із можливих
      if (!ctx.playing && phase === 'done' && ctx.room && ctx.room.maxPlayers === 1 && v.rounds)
        return 'Результат: ' + num((v.scores || [])[0]) + ' з ' + num(v.rounds * 5000);
      if (!ctx.playing) return '';
      if (phase === 'between') return 'Раунд ' + v.round + ' з ' + v.rounds + ' — готуйсь…';
      if (phase === 'guess') {
        if (!ctx.mine) return 'Гравці думають…';
        if (st && st.readyRound === v.round) return 'Готово! Чекаємо решту…';
        if ((st && st.pin && st.pinRound === v.round) || v.my) return 'Шпилька стоїть — зарахується й так; «Готово» — щоб не чекати';
        return 'Тицьни на мапу, де це';
      }
      if (phase === 'reveal') return 'Ось де це насправді';
      return '';
    },

    pad: {
      dirs: true,                 // стік/хрестовина → стрілки → хрестик курсора
      a: 'Space',                 // Ⓐ — шпилька
      x: 'KeyF',                  // Ⓧ — фото на весь екран
      on(btn, ctx) {              // масштаб і «готово» — під пальцями, щоб не шукати
        const st = ctx._geo;
        if (!st) return false;
        if (btn === 'lb') { zoomKey(st, 1 / 1.5); return true; }
        if (btn === 'rb') { zoomKey(st, 1.5); return true; }
        if (btn === 'lt') { home(st); return true; }
        if (btn === 'rt') { readyOrNext(st); return true; }
        return false;             // Ⓑ — вийти, Ⓨ — довідка, ☰ — на весь екран: лишаються каркасу
      },
      hint: '{dpad} курсор · {a} шпилька · {rt} готово · {lb}{rb} масштаб · {x} фото',
      // Уся партія: «готуйсь» (там Ⓐ/RT нічого не роблять — раніше кільце стояло на «← Лобі», і Ⓐ викидав зі
      // столу), вгадування й розкриття (там Ⓐ і RT — «Далі»). Після партії пад знову водить кільце, і воно
      // стає на «Ще раз» п'єдесталу (paintPodium).
      when: (ctx) => ctx.mine && ctx.playing && ['between', 'guess', 'reveal'].includes(phaseOf(ctx)),
    },

    unmount(root) {
      const st = root._geo;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      clearTimeout(st.redTimer);
      st.raf = 0;
      if (st.keyup) document.removeEventListener('keyup', st.keyup);
      if (st.onVis) document.removeEventListener('visibilitychange', st.onVis);
      if (st.ro) st.ro.disconnect();
      closeFull(st);
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      st.cv = null;
      root._geo = null;
    },
  };

  HGames.register(Object.assign({
    id: 'geo',
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Де це?',
      items: [
        '📷 Фото звідкись з України — тицьни на мапу, де це знято',
        '📏 Що ближче шпилька, то більше очок: 5000 за влучання, 2885 за сто кілометрів, крихти за пів країни',
        '⏱ 45 секунд на раунд; «Готово» — і не чекаємо таймера, коли всі визначились',
        '🔍 Мапу можна наблизити колесом чи щипком, фото — розгорнути на весь екран (F)',
        '🏆 П’ять раундів, підсумок і черепки за очки; самому — «Тренування» в Соло з таблицею рекордів',
      ],
    },
  }, common));

  HGames.register(Object.assign({ id: 'geo-solo' }, common));
})();
