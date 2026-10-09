/* Козацький скарб (slot-hold): 5×3, 20 ліній, дукати → «Утримуй і вигравай» (3 респіни-свічки, Булава, Пірнач,
   джекпоти Міні/Мажор/Гетьманський скарб). Механіка й мок-математика — тут; арт — slot-hold-art.js (SlotArt['slot-hold']).
   Сценарій — чисті дані: spin → morph(why: 'rain')? → win? → holdIn → (respin, double?, collect?)×N → holdCount → grand? → holdOut.
   «Дукатний дощ» (сюрприз базової гри, вирішує сервер): після зупинки 1–4 дукати падають на клітинки без дуката й поза
   виграшними рядами — своя сцена в STEPS.morph (хмара, дукати падають поштучно, «Скарб відкрито!» на 6+). */
(function () {
  'use strict';
  const SK = window.SlotKit, ID = 'slot-hold';
  const COLS = 5, ROWS = 3, R3 = [0, 1, 2];
  const WILD = 'wild';
  // Дукати на барабанах: ключ → номінал у ставках (Міні/Мажор — джекпоти)
  const COINV = { c1: 1, c2: 2, c3: 3, c5: 5, c10: 10, c25: 25, cmini: 20, cmajor: 100 };
  const COINK = { cmini: 'mini', cmajor: 'major' };
  const JP = { mini: 20, major: 100, grand: 1000 };
  const isCoin = (k) => Object.prototype.hasOwnProperty.call(COINV, k);

  // Стрічки (по 30) — ті самі, що на сервері (SlotHoldMath.Reels, математика 98 % від 10.10.2026): сервер шле лише зупинки.
  // Бунчука нема на першому барабані, на третьому — два; дукати ×1…×5 і Міні (×10, ×25, Мажор — лише в респінах).
  const REELS = [
    's1 s2 c1 s3 pipe s4 s1 mug s2 s3 horse s4 s1 c2 sabre s2 s3 s4 cossack s1 pipe c5 s2 s1 mug s4 sabre s2 s3 s1',
    's2 s3 c2 c1 wild s4 mug s2 s1 s4 s3 pipe s1 horse s4 s2 sabre s1 s3 cossack s4 c5 s2 pipe s1 mug s3 c5 s4 s2',
    's3 c1 s4 s1 horse s2 cmini c2 pipe s4 wild s1 s3 s2 mug s1 s3 sabre s4 c5 s1 cossack s2 s3 pipe wild s1 s3 s4 s2',
    's4 s1 c3 s2 sabre s3 c1 c2 s4 pipe s1 wild s2 horse s3 mug s4 s1 c3 s2 cossack s3 s1 s4 pipe s2 s1 c5 mug s3',
    's1 c2 s3 s2 mug s4 c1 s1 horse s3 wild s2 s4 pipe s1 sabre s2 s3 s2 cossack s4 c3 s1 mug s3 s1 s2 pipe s4 cmini',
  ].map((s) => s.split(' '));
  // 20 ліній: рядок кожного барабана
  const LINES = [
    [1, 1, 1, 1, 1], [0, 0, 0, 0, 0], [2, 2, 2, 2, 2], [0, 1, 2, 1, 0], [2, 1, 0, 1, 2],
    [0, 0, 1, 2, 2], [2, 2, 1, 0, 0], [1, 0, 0, 0, 1], [1, 2, 2, 2, 1], [1, 0, 1, 2, 1],
    [1, 2, 1, 0, 1], [0, 1, 1, 1, 0], [2, 1, 1, 1, 2], [0, 1, 0, 1, 0], [2, 1, 2, 1, 2],
    [1, 1, 0, 1, 1], [1, 1, 2, 1, 1], [0, 0, 2, 0, 0], [2, 2, 0, 2, 2], [0, 2, 0, 2, 0],
  ];
  // Виплати — у ставках на лінію (ставка ÷ 20); на сайті їх підміняє view.table.pay
  const PAY = {
    wild: { 5: 3000, 4: 400, 3: 75 }, cossack: { 5: 1000, 4: 200, 3: 50 }, horse: { 5: 500, 4: 120, 3: 40 },
    sabre: { 5: 400, 4: 100, 3: 30 }, mug: { 5: 300, 4: 80, 3: 24 }, pipe: { 5: 240, 4: 60, 3: 22 },
    s1: { 5: 90, 4: 24, 3: 10 }, s2: { 5: 90, 4: 24, 3: 10 }, s3: { 5: 80, 4: 20, 3: 8 }, s4: { 5: 80, 4: 20, 3: 8 },
  };

  // ⓘ і дощ моку — з view.table сервера (slot.js кличе _setPay); без сервера (стенд) — числа зі spec
  let T = null;
  const RAIN = { chance: 1 / 16, counts: { 1: 0.7, 2: 0.22, 3: 0.06, 4: 0.02 }, coins: { c1: 5, c2: 5, c3: 3, c5: 5, cmini: 2 } };
  const rainT = () => (T && T.rain) || RAIN;

  function gridOf(stops) { return stops.map((s, c) => R3.map((r) => REELS[c][(s + r) % REELS[c].length])); }
  function evalLine(keys) {
    let base = keys.find((k) => k !== WILD) || WILD;
    if (!PAY[base]) base = WILD;                         // дукат ламає лінію: лишається хіба ряд бунчуків
    let n = 0; for (const k of keys) { if (k === base || k === WILD) n++; else break; }
    let w = 0; for (const k of keys) { if (k === WILD) w++; else break; }
    const p1 = PAY[base][n] || 0, p2 = PAY.wild[w] || 0;
    if (p2 > p1) return { sym: WILD, n: w, pay: p2 };
    return p1 ? { sym: base, n, pay: p1 } : null;
  }
  function evaluate(grid) {
    const items = []; let u = 0;
    LINES.forEach((ln, i) => {
      const w = evalLine(ln.map((r, c) => grid[c][r]));
      if (w) { items.push({ line: i, sym: w.sym, n: w.n, u: w.pay, cells: ln.slice(0, w.n).map((r, c) => [c, r]) }); u += w.pay; }
    });
    const coins = [];
    grid.forEach((col, c) => col.forEach((k, r) => { if (isCoin(k)) coins.push({ c, r, k: COINK[k] || 'coin', v: COINV[k] }); }));
    // очікування — чесно: барабан c тягнеться, коли на попередніх уже рівно 5 дукатів (бракує одного)
    const tease = []; let before = 0;
    for (let c = 0; c < COLS; c++) { if (before === 5) tease.push(c); before += grid[c].filter(isCoin).length; }
    return { items, u, coins, tease };
  }

  // ---------- «Утримуй і вигравай»: мок респінів ----------
  // o: pLand — шанс дуката в порожньому гнізді; saves — скільки разів «рятує» остання свічка; plan — [{at, k}] примусові
  // особливі на респіні at; fill — тягнути до повного поля.
  function bonusCoin(o) {
    const x = Math.random();
    if (x < 0.045) return { k: 'mace', v: 0 };
    if (x < 0.085) return { k: 'pirnach', v: 0 };
    if (x < 0.12) return { k: 'mini', v: JP.mini };
    if (x < 0.128) return { k: 'major', v: JP.major };
    const y = Math.random();
    const v = y < 0.34 ? 1 : y < 0.6 ? 2 : y < 0.76 ? 3 : y < 0.89 ? 5 : y < 0.97 ? 10 : 25;
    return { k: 'coin', v: o && o.rich && v < 3 ? 3 : v };
  }
  function holdSim(coins, bet, o) {
    o = o || {};
    const g = Array.from({ length: COLS }, () => Array(ROWS).fill(null));
    coins.forEach((x) => { g[x.c][x.r] = { k: x.k, v: x.v }; });
    const steps = [{ t: 'holdIn', coins: coins.map((x) => [x.c, x.r, x.k, x.v]) }];
    const plan = (o.plan || []).slice();
    let left = 3, n = 0, saves = o.saves || 0, guard = 0;
    const empties = () => { const a = []; for (let c = 0; c < COLS; c++) for (let r = 0; r < ROWS; r++) if (!g[c][r]) a.push([c, r]); return a; };
    while (left > 0 && guard++ < 80) {
      const em = empties(); if (!em.length) break;
      const land = [];
      const pL = o.pLand != null ? o.pLand : 0.05;
      em.forEach(([c, r]) => { if (Math.random() < pL) land.push(Object.assign({ c, r }, bonusCoin(o))); });
      const taken = (c, r) => land.some((x) => x.c === c && x.r === r);
      plan.filter((p) => p.at === n).forEach((p) => {
        const free = em.filter(([c, r]) => !taken(c, r)); if (!free.length) return;
        const [c, r] = SK.rnd.pick(free);
        land.push({ c, r, k: p.k, v: p.k === 'mini' ? JP.mini : p.k === 'major' ? JP.major : p.k === 'coin' ? (p.v || 5) : 0 });
      });
      if (!land.length && left === 1 && saves > 0) { saves--; const [c, r] = SK.rnd.pick(em); land.push(Object.assign({ c, r }, bonusCoin(o), o.fill ? {} : {})); }
      // особливі не раніше, ніж є що збирати/множити
      land.forEach((x) => { if ((x.k === 'mace' || x.k === 'pirnach') && coins.length < 1) { x.k = 'coin'; x.v = 2; } });
      const before = left;
      land.forEach((x) => { g[x.c][x.r] = { k: x.k, v: x.v }; });
      left = land.length ? 3 : left - 1;
      const emLeft = em.length - land.length;
      steps.push({ t: 'respin', left: before, after: left, land: land.map((x) => [x.c, x.r, x.k, x.v]), slow: before === 1 || em.length <= 2 });
      // Пірнач — усе ×2 (дукати й булави; джекпоти — ні), потім Булава збирає
      land.filter((x) => x.k === 'pirnach').forEach((p) => {
        const cells = [];
        for (let c = 0; c < COLS; c++) for (let r = 0; r < ROWS; r++) { const it = g[c][r]; if (it && (it.k === 'coin' || it.k === 'mace') && it.v) { it.v *= 2; cells.push([c, r, it.v]); } }
        steps.push({ t: 'double', at: [p.c, p.r], cells });
      });
      land.filter((x) => x.k === 'mace').forEach((m) => {
        const from = []; let sum = 0;
        for (let c = 0; c < COLS; c++) for (let r = 0; r < ROWS; r++) {
          const it = g[c][r]; if (!it || (c === m.c && r === m.r) || !(it.k === 'coin' || it.k === 'mace') || !it.v) continue;
          from.push([c, r, it.v]); sum += it.v;
        }
        g[m.c][m.r].v = Math.max(1, sum);
        steps.push({ t: 'collect', at: [m.c, m.r], from, total: g[m.c][m.r].v });
      });
      n++;
      if (!emLeft) break;
    }
    const full = !empties().length;
    const cells = []; let total = 0;
    for (let c = 0; c < COLS; c++) for (let r = 0; r < ROWS; r++) {
      const it = g[c][r]; if (!it || !it.v) continue;
      const amt = Math.round(it.v * bet); cells.push([c, r, amt, it.k]); total += amt;
    }
    steps.push({ t: 'holdCount', cells, total });
    const grand = full ? JP.grand * bet : 0;
    if (full) steps.push({ t: 'grand', amount: grand });
    steps.push({ t: 'holdOut', total: total + grand });
    return { steps, total: total + grand, full, respins: n };
  }

  // ---------- «Дукатний дощ» (мок для стенду; на сайті — сервер) ----------
  function wpick(w) {
    let t = 0; for (const k in w) t += +w[k];
    let x = Math.random() * t;
    for (const k in w) { x -= +w[k]; if (x < 0) return k; }
    return Object.keys(w)[0];
  }
  const rainCount = () => +wpick(rainT().counts);
  // як сервер: лише клітинки без дуката й поза виграшними рядами, кожна вільна — рівноймовірно
  function rainOn(grid, ev, n) {
    const busy = new Set();
    ev.items.forEach((it) => it.cells.forEach(([c, r]) => busy.add(c + ',' + r)));
    const free = [];
    grid.forEach((col, c) => col.forEach((k, r) => { if (!isCoin(k) && !busy.has(c + ',' + r)) free.push([c, r]); }));
    const cells = [];
    for (let i = 0; i < n && free.length; i++) {
      const [c, r] = free.splice(SK.rnd.int(free.length), 1)[0], k = wpick(rainT().coins);
      grid[c][r] = k; cells.push([c, r, k, COINV[k]]);
    }
    return cells;
  }

  // bo — підкрутки показу (стенд): rain — скільки дукатів дощу (0 — без дощу); без bo — дощ випадковий, як на сервері
  function scriptFor(stops, bet, state, bo) {
    const grid = gridOf(stops), lb = bet / 20;
    let ev = evaluate(grid);
    const tease = ev.tease;
    const nRain = bo && bo.rain != null ? bo.rain : !bo && Math.random() < rainT().chance ? rainCount() : 0;
    const rain = nRain ? rainOn(grid, ev, nRain) : [];
    if (rain.length) ev = evaluate(grid);   // ряди ті самі (дощ їх не чіпає), дукатів — більше
    const items = ev.items.map((it) => ({ line: it.line, cells: it.cells, sym: it.sym, amount: Math.round(it.u * lb) }));
    let win = items.reduce((a, it) => a + it.amount, 0);
    const steps = [{ t: 'spin', stops, tease }];
    if (rain.length) steps.push({ t: 'morph', cells: rain, why: 'rain' });
    if (items.length) steps.push({ t: 'win', items, amount: win });
    let hold = null, capped = false;
    if (ev.coins.length >= 6) {
      hold = holdSim(ev.coins, bet, bo);
      // як сервер (SlotHoldMath): повне поле — оберт віддає рівно стелю (grand = доплата), інакше скриня обрізається стелею
      const capW = JP.grand * bet, cnt = hold.steps.find((s) => s.t === 'holdCount'), out = hold.steps.find((s) => s.t === 'holdOut');
      const gr = hold.steps.find((s) => s.t === 'grand');
      if (gr) { gr.amount = Math.max(0, capW - win - cnt.total); out.total = cnt.total + gr.amount; }
      else if (win + out.total > capW) { out.total = Math.max(0, capW - win); capped = true; }
      hold.total = out.total;
      steps.push(...hold.steps); win += hold.total;
    }
    const S = { bet, steps, win, state: state || {}, hold: !!hold, full: !!(hold && hold.full) };
    if (capped) S.capped = true;
    if (rain.length) S.rain = rain.length;
    return S;
  }

  // ---------- сценарії показу: випадкові зупинки, відібрані за ознакою ----------
  let POOL = null;
  function pool() {
    if (POOL) return POOL;
    POOL = [];
    for (let i = 0; i < 60000; i++) {
      const stops = REELS.map((s) => SK.rnd.int(s.length)), ev = evaluate(gridOf(stops));
      POOL.push({ stops, m: ev.u / 20, n: ev.items.length, coins: ev.coins.length, t4: ev.tease.includes(4), tease: ev.tease.length > 0 });
    }
    return POOL;
  }
  const pickBy = (pred) => { const l = pool().filter(pred); return (l.length ? SK.rnd.pick(l) : SK.rnd.pick(pool())).stops; };
  // повне поле — лише там, де його й показуємо
  const demoBy = (pred, bo) => (bet, state) => {
    let s = null;
    for (let i = 0; i < 40; i++) { s = scriptFor(pickBy(pred), bet, state, bo || { rain: 0 }); if (!s.full || (bo && bo.fill)) break; }
    return s;
  };
  // бонус, що дав виграш у межах [lo, hi) ставок
  function bonusIn(lo, hi, bo, minCoins) {
    return (bet, state) => {
      const trig = pool().filter((x) => x.coins >= (minCoins || 6) && x.m < 2);
      let best = null;
      for (let i = 0; i < 400; i++) {
        const s = scriptFor(SK.rnd.pick(trig).stops, bet, state, Object.assign({ rain: 0 }, bo)), m = s.win / bet;
        if (m >= lo && m < hi && !s.full) return s;
        if (!best || Math.abs(m - lo) < Math.abs(best.win / bet - lo)) best = s;
      }
      return best;
    };
  }

  const SAY = {
    idle: ['шість дукатів — і скриня твоя', 'дукати самі не прийдуть — крути', 'козак без скарбу — як люлька без тютюну'],
    tease: ['ще один дукат…', 'не дихай — одного бракує'],
    rain: ['з неба — дукати!', 'хмара з грошима — бачив таке?', 'дощ дукатами — підставляй шапку', 'небо сьогодні платить'],
    open: ['дощ доніс шостий — скриня!', 'скарб відкрито! оце злива', 'відчиняй скриню, козаче!'],
    trigger: ['скриня! тримай, не впусти', 'о, запахло скарбом'],
    reset: ['ще три свічки!', 'дзень! горить знову', 'тримається козак'],
    last: ['остання свічка…', 'ну ж бо, ну ж бо…'],
    mace: ['булава все збирає', 'гетьман прийшов по своє'],
    pirnach: ['пірнач — усе вдвічі!', 'полковник каже: удвічі'],
    mini: ['міні — а приємно'], major: ['мажор! оце так'],
    count: ['рахуємо скарб…'],
    grand: ['гетьманський скарб! ти шо, гетьман?'],
  };
  const pick = (a) => SK.rnd.pick(a);

  // ⓘ: правила; дощ і шанси респіну — з view.table сервера (на стенді — числа зі spec)
  const pct = (x) => String(Math.round(x * 1000) / 10).replace('.', ',') + ' %';
  function rulesHtml() {
    const rn = rainT(), rs = T && T.respin;
    const rainTxt = (T && T.rain && T.rain.text) || ('Дукатний дощ: після зупинки в 1 з ' + Math.round(1 / rn.chance)
      + ' обертів на поле падає 1–4 дукати — лише на клітинки поза виграшними лініями, тож виграш ліній не меншає.');
    let resp = '';
    if (rs) {
      const cv = rs.coins ? Object.keys(rs.coins).sort((a, b) => a - b).map((v) => '×' + v + ' ' + pct(rs.coins[v])).join(', ') : '';
      resp = ' На кожному респіні в порожньому гнізді щось падає з шансом <b>' + pct(rs.land) + '</b>; з того, що впало: Булава '
        + pct(rs.mace) + ', Пірнач ' + pct(rs.pirnach) + ', Міні ' + pct(rs.mini) + ', Мажор ' + pct(rs.major) + (cv ? ', дукати ' + cv : '') + '.';
    }
    const jp = JP;
    return '<b>20 ліній</b>, ставка ділиться між ними порівну; платить однаковий ряд зліва направо від першого барабана. '
      + '<b>6+ дукатів</b> на полі (разом із дощем) — «утримуй і вигравай»: дукати лишаються, решта гнізд крутиться; <b>3 респіни</b>, '
      + 'кожен новий дукат знову дає 3. <b>Булава</b> збирає номінали всіх дукатів у себе, <b>Пірнач</b> — усі дукати й булави ×2. '
      + 'Дукати «Міні» ×' + jp.mini + ' і «Мажор» ×' + jp.major + ' ставки; усі 15 гнізд — <b>Гетьманський скарб</b>: оберт віддає ×' + jp.grand + ' ставки.'
      + resp + '<br><br>🌧 ' + rainTxt;
  }

  // ---------- арт: дукати з номіналами як символи барабанів; заглушки ----------
  function ensureSyms() {
    const A = window.SlotArt && window.SlotArt[ID];
    if (!A || A._holdSyms || !A.extras || typeof A.extras.coin !== 'function') return;
    A._holdSyms = true;
    Object.keys(COINV).forEach((k) => {
      const lbl = k === 'cmini' ? 'Міні' : k === 'cmajor' ? 'Мажор' : String(COINV[k]);
      A.symbols[k] = { name: k === 'cmini' ? 'Дукат «Міні»' : k === 'cmajor' ? 'Дукат «Мажор»' : 'Дукат ×' + COINV[k], tier: 'special', svg: A.extras.coin(lbl) };
    });
  }
  const PH = {
    coin: (l) => '<div class="sh-ph sh-ph-coin">' + (l || '') + '</div>',
    mace: (l) => '<div class="sh-ph sh-ph-mace">' + (l || 'булава') + '</div>',
    pirnach: (l) => '<div class="sh-ph sh-ph-pir">' + (l || '×2') + '</div>',
    empty: '<div class="sh-ph sh-ph-empty"></div>',
    respins: (n) => '<div class="sh-ph">' + '🕯'.repeat(n) + '</div>',
    chest: '<div class="sh-ph" style="font-size:90px">🧰</div>',
    jackpots: '<div class="sh-ph sh-ph-jp"><span class="jp jp-mini">МІНІ <b class="jp-v jp-v-mini"></b></span><span class="jp jp-grand">ГЕТЬМАН <b class="jp-v jp-v-grand"></b></span><span class="jp jp-major">МАЖОР <b class="jp-v jp-v-major"></b></span></div>',
    frame: '', frameBack: '',
  };
  const ex = (ctx, name, ...a) => ctx.extra(name, ...a) || (typeof PH[name] === 'function' ? PH[name](...a) : PH[name] || '');
  function itemSvg(ctx, it) {
    if (!it) return ex(ctx, 'empty');
    if (it.k === 'mini') return ex(ctx, 'coin', 'Міні');
    if (it.k === 'major') return ex(ctx, 'coin', 'Мажор');
    if (it.k === 'mace') return ex(ctx, 'mace', it.v ? String(it.v) : '');
    if (it.k === 'pirnach') return ex(ctx, 'pirnach', '×2');
    return ex(ctx, 'coin', String(it.v));
  }

  // ---------- звуки (синт; за замовчуванням звук вимкнено) ----------
  try {
    if (SK.sound && SK.sound.add) {
      SK.sound.add('dzen', (h, o) => { const p = (o && o.p) || 1; h.tone(1320 * p, h.t, 0.4, 'sine', 0.16, 1320 * p); h.tone(1980 * p, h.t + 0.012, 0.28, 'triangle', 0.07, 1990 * p); });
      SK.sound.add('puff', (h) => { h.noise(h.t, 0.22, 900, 1, 0.12); });
      SK.sound.add('lid', (h) => { h.tone(160, h.t, 0.25, 'square', 0.08, 90); h.noise(h.t, 0.18, 1400, 1, 0.12); });
    }
  } catch (e) { /* без звуку — не біда */ }

  // ---------- сцена ----------
  const H = (ctx) => ctx.sh;
  const fmt = (n) => SK.fmt(n);
  function setJp(ctx) {
    const st = H(ctx) && H(ctx).stage; if (!st) return;
    ['mini', 'major', 'grand'].forEach((k) => st.querySelectorAll('.jp-v-' + k).forEach((t) => { t.textContent = fmt(JP[k] * ctx.bet); }));
  }
  function jpFlash(ctx, k, keep) {
    const st = H(ctx) && H(ctx).stage; if (!st) return;
    st.querySelectorAll('.sh-jp .jp-' + k).forEach((g) => { g.classList.remove('hit'); void g.getBoundingClientRect(); g.classList.add('hit'); if (keep) g.classList.add('lit'); });
  }
  function hintHtml(ctx) {
    return '<div class="sh-hint"><div class="sh-hint-c">' + ex(ctx, 'coin', '5') + '</div>'
      + '<div class="sh-hint-t"><b>6+ дукатів</b> — утримуй і вигравай</div>'
      + '<div class="sh-hint-sp"><span>' + ex(ctx, 'mace', '') + '</span><i>булава збирає</i><span>' + ex(ctx, 'pirnach', '×2') + '</span><i>пірнач — удвічі</i></div></div>';
  }
  function candHtml() { return '<div class="sh-cand"><div class="sh-cand-svg"></div><div class="sh-cand-t"></div></div>'; }
  function sumHtml() { return '<div class="sh-sum"><small>у скрині</small><b>0</b></div>'; }

  function setCandles(ctx, n, fx) {
    const h = H(ctx); if (!h) return;
    h.left = n;
    const t = n >= 3 ? 'три респіни' : n === 2 ? 'ще два' : n === 1 ? 'ще один' : 'остання!';
    h.stage.querySelectorAll('.sh-cand').forEach((w) => {
      w.querySelector('.sh-cand-svg').innerHTML = ex(ctx, 'respins', n);
      w.querySelector('.sh-cand-t').textContent = t;
      w.classList.remove('reset', 'out', 'last'); void w.offsetWidth;
      if (fx) w.classList.add(fx);
      if (!n) w.classList.add('last');
    });
  }
  function updSum(ctx, bump) {
    const h = H(ctx); if (!h || !h.g) return;
    let s = 0; h.g.forEach((col) => col.forEach((it) => { if (it && it.v) s += it.v * ctx.bet; }));
    h.stage.querySelectorAll('.sh-sum').forEach((e) => {
      e.querySelector('b').textContent = fmt(s);
      if (bump) { e.classList.remove('bump'); void e.offsetWidth; e.classList.add('bump'); }
    });
  }
  function setCell(ctx, c, r) {
    const h = H(ctx), e = h.hc[c][r], it = h.g[c][r];
    e.classList.toggle('full', !!it);
    e.dataset.k = it ? it.k : '';
    e.innerHTML = '<div class="sh-hci">' + itemSvg(ctx, it) + '</div>';
  }
  function anim(e, cls, ms, ctx) { e.classList.remove(cls); void e.offsetWidth; e.classList.add(cls); ctx.timeout(() => e.classList.remove(cls), ms); }
  let STRIP = null;
  function spinCell(ctx, c, r) {
    const h = H(ctx), e = h.hc[c][r];
    if (!STRIP) { const a = ex(ctx, 'coin', ''), b = ex(ctx, 'empty'); STRIP = '<div class="sh-strip">' + [a, b, b, a, b, b].map((x) => '<div class="sh-sq">' + x + '</div>').join('') + '</div>'; }
    e.classList.add('spin'); e.innerHTML = STRIP;
    e.style.setProperty('--d', (-Math.random() * 0.3).toFixed(2) + 's');
  }
  // число летить з клітинки до точки (у px поля)
  function fly(ctx, from, to, text, cls) {
    const h = H(ctx), S = h.S, f = document.createElement('div');
    f.className = 'sh-fly ' + (cls || ''); f.textContent = text;
    const x0 = (from[0] + 0.5) * S, y0 = (from[1] + 0.5) * S;
    f.style.left = x0 + 'px'; f.style.top = y0 + 'px';
    h.holdEl.appendChild(f); void f.offsetWidth;
    f.style.transform = 'translate(' + ((to[0] + 0.5) * S - x0).toFixed(1) + 'px,' + ((to[1] + 0.5) * S - y0).toFixed(1) + 'px) translate(-50%,-50%) scale(.7)';
    f.classList.add('go');
    ctx.timeout(() => f.remove(), 700);
  }
  function floatAt(ctx, c, r, text, cls) {
    const h = H(ctx), S = h.S, f = document.createElement('div');
    f.className = 'sh-plus ' + (cls || ''); f.textContent = text;
    f.style.left = (c + 0.5) * S + 'px'; f.style.top = (r + 0.5) * S + 'px';
    h.holdEl.appendChild(f); ctx.timeout(() => f.remove(), 1100);
  }

  function enterHold(ctx, coins) {
    const h = H(ctx);
    h.on = true;
    h.g = Array.from({ length: COLS }, () => Array(ROWS).fill(null));
    coins.forEach(([c, r, k, v]) => { h.g[c][r] = { k, v }; });
    for (let c = 0; c < COLS; c++) for (let r = 0; r < ROWS; r++) setCell(ctx, c, r);
    h.stage.classList.add('sh-bonus');
    coins.forEach(([, , k]) => { if (k === 'mini' || k === 'major') jpFlash(ctx, k, true); });
    setCandles(ctx, 3);
    updSum(ctx);
  }
  function leaveHold(ctx) {
    const h = H(ctx); if (!h) return;
    h.on = false; h.g = null;
    h.stage.classList.remove('sh-bonus', 'sh-slow', 'sh-grand');
    h.stage.querySelectorAll('.sh-jp .jp').forEach((g) => g.classList.remove('lit', 'hit'));
    h.hc.forEach((col) => col.forEach((e) => { e.className = 'sh-hc'; e.innerHTML = ''; }));
    ctx.clearWin();
  }

  // ---------- «Дукатний дощ»: хмара над скринею, дукати падають поштучно, клітинка «приймає» ----------
  const CLOUD = '<svg viewBox="0 0 320 132" class="sh-cloud-svg" aria-hidden="true"><defs>'
    + '<linearGradient id="shr-cl" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#6c5a8e"/><stop offset=".6" stop-color="#3a2c55"/><stop offset="1" stop-color="#211836"/></linearGradient>'
    + '<radialGradient id="shr-gl" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="#ffe38a" stop-opacity=".9"/><stop offset="1" stop-color="#ffb400" stop-opacity="0"/></radialGradient></defs>'
    + '<ellipse class="a-rglow" cx="160" cy="112" rx="150" ry="22" fill="url(#shr-gl)"/>'
    + '<path d="M44 106Q14 106 16 83Q19 61 46 63Q47 33 82 31Q98 6 134 15Q158-2 188 15Q216 4 236 29Q270 25 276 55Q306 59 304 84Q302 106 274 106Z" fill="url(#shr-cl)" stroke="#ffd447" stroke-width="3.5" stroke-linejoin="round"/>'
    + '<path d="M70 58Q74 40 96 40M150 26Q170 14 190 26M236 44Q256 42 262 58" fill="none" stroke="#a796c9" stroke-width="5" stroke-linecap="round" opacity=".55"/>'
    + '<g fill="#ffe27a"><circle class="a-rsp1" cx="96" cy="118" r="4"/><circle class="a-rsp2" cx="160" cy="124" r="5"/><circle class="a-rsp3" cx="226" cy="118" r="4"/></g></svg>';
  function rainLayer(ctx) {
    const h = H(ctx), fr = h.stage.querySelector('.sh-frame');
    const L = document.createElement('div'); L.className = 'sh-rainl';
    L.innerHTML = '<div class="sh-cloud">' + CLOUD + '</div>';
    fr.appendChild(L);
    return L;
  }
  // клітинка барабана стає дукатом — так само, як reelsMorph кіта (dataset.k читають holdIn, onTease, підсвітка)
  function putCoin(ctx, c, r, key) {
    const e = ctx.reels && ctx.reels.cell(c, r); if (!e) return null;
    if (ctx.reels.poke) ctx.reels.poke(c, r, key);
    e._key = key; e.dataset.k = key; e.replaceChildren(ctx.symNode(key));
    e.classList.remove('win', 'dim', 'glint', 'sk-morph');
    return e;
  }
  function onField(ctx) { return ctx.reels ? ctx.reels.cells().filter((e) => e && isCoin(e.dataset.k)) : []; }
  async function rainScene(s, ctx) {
    const h = H(ctx), cells = s.cells || [];
    if (!h || !h.stage || !ctx.reels || !cells.length) { await SK.steps.morph(s, ctx); return; }
    // тап/пробіл: ctx.skip доводить поточне до кінця — а ми ще й решту дукатів кладемо одразу
    let skipped = false;
    const sk0 = ctx.skip;
    ctx.skip = function () { skipped = true; return sk0.apply(this, arguments); };
    const S = h.S, calm = SK.calm && SK.calm();
    const L = rainLayer(ctx), cloud = L.firstChild;
    try {
      ctx.emit('surprise', 'rain', s);
      ctx.react('surprise', { why: 'rain', say: false });
      ctx.say(pick(SAY.rain), 2400);
      h.stage.classList.add('sh-raining');
      ctx.sound('whoosh');
      ctx.animate(cloud, [{ transform: 'translate(-50%, -45%) scale(.55)', opacity: 0 }, { transform: 'translate(-50%, 0) scale(1)', opacity: 1 }],
        { duration: 340, easing: 'cubic-bezier(.3, 1.5, .5, 1)', fill: 'both' });
      ctx.timeout(() => cloud.classList.add('on'), 340);
      await ctx.wait(280);
      const n = cells.length, stag = n > 2 ? 250 : 300, fall = 520;
      const cw = S * 3.2, cx = COLS * S / 2, cy = -S * 0.42;   // низ хмари (px поля)
      const one = async ([c, r, key, v], i) => {
        if (i) await ctx.wait(i * stag);
        const tx = (c + 0.5) * S, ty = (r + 0.5) * S;
        if (!skipped && L.isConnected) {
          const sx = cx + (tx - cx) * 0.62 + (Math.random() - 0.5) * 0.12 * cw, dx = sx - tx, dy = cy - ty;
          const d = document.createElement('div'); d.className = 'sh-drop';
          d.style.left = tx + 'px'; d.style.top = ty + 'px';
          d.innerHTML = '<div class="sh-drop-i glint">' + ctx.symHtml(key) + '</div>';
          L.appendChild(d);
          const spin = Math.max(2, Math.round(fall / 190));
          const a = ctx.animate(d, [
            { transform: 'translate(-50%, -50%) translate(' + dx.toFixed(1) + 'px, ' + dy.toFixed(1) + 'px) scale(.5)', opacity: 0 },
            { opacity: 1, offset: 0.14 },
            { transform: 'translate(-50%, -50%) translate(0, 0) scale(1)', opacity: 1 },
          ], { duration: fall, easing: 'cubic-bezier(.55, 0, .9, .55)', fill: 'both' });
          if (!calm) ctx.animate(d.firstChild, [{ transform: 'scaleX(1)' }, { transform: 'scaleX(.14)' }, { transform: 'scaleX(1)' }], { duration: fall / spin, iterations: spin });
          ctx.fx.at(d, { kind: 'spark', n: 6, speed: 150, size: 7, gravity: 260 });
          ctx.sound('clink', { p: 1.3 + i * 0.08 });
          await a.finished.catch(() => {});
          d.remove();
        }
        const e = putCoin(ctx, c, r, key);
        if (!e || skipped) return;
        // удар: клітинка «приймає» дукат — сплющення, кільце-спалах, іскри, номінал спалахує
        anim(e, 'sh-catch', 700, ctx);
        const ring = document.createElement('div'); ring.className = 'sh-ring';
        ring.style.left = tx + 'px'; ring.style.top = ty + 'px'; L.appendChild(ring);
        ctx.timeout(() => ring.remove(), 700);
        const f = document.createElement('div'); f.className = 'sh-plus sh-rplus' + (key === 'cmini' ? ' big' : '');
        f.textContent = key === 'cmini' ? 'Міні!' : '×' + v;
        f.style.left = tx + 'px'; f.style.top = ty + 'px'; L.appendChild(f);
        ctx.timeout(() => f.remove(), 1100);
        ctx.fx.at(e, { kind: 'spark', n: calm ? 8 : 16, speed: 380, size: 9 });
        ctx.fx.at(e, { kind: 'coin', n: 4, speed: 300, size: 9, life: 1.1 });
        ctx.fx.at(e, { kind: 'dust', n: 4, speed: 80, size: 14 });
        ctx.sound('dzen', { p: 1 + i * 0.09 });
        ctx.shake(0.22 + i * 0.07);
      };
      await Promise.all(cells.map(one));
      // що не впало (тап посеред дощу) — уже на місці; хмара тане
      cells.forEach(([c, r, key]) => { const e = ctx.reels.cell(c, r); if (e && e.dataset.k !== key) putCoin(ctx, c, r, key); });
      cloud.classList.remove('on');
      const out = ctx.animate(cloud, [{ transform: 'translate(-50%, 0) scale(1)', opacity: 1 }, { transform: 'translate(-50%, -30%) scale(.8)', opacity: 0 }],
        { duration: 300, easing: 'ease-in', fill: 'both' });
      h.stage.classList.remove('sh-raining');
      await ctx.wait(skipped ? 60 : 260);
      // 6+ дукатів після дощу — окремий момент «Скарб відкрито!» (ескалація), далі holdIn
      const field = onField(ctx);
      if (field.length >= 6) {
        h.rainOpened = true;
        ctx.sound('bell'); ctx.flash('#ffd447', { power: 0.9, ms: 520 }); ctx.shake(1.4);
        ctx.react('surprise', { mood: 'wow', ms: 2200, say: false });
        ctx.say(pick(SAY.open), 2800);
        field.forEach((e, i) => { e.classList.add('sh-hot'); ctx.timeout(() => ctx.fx.at(e, { kind: 'spark', n: calm ? 6 : 12, speed: 340, size: 9 }), i * 60); });
        const o = document.createElement('div'); o.className = 'sh-open';
        o.innerHTML = '<b>' + field.length + '</b><span>дукатів — скарб відкрито!</span>';
        L.appendChild(o);
        ctx.animate(o, [{ transform: 'translate(-50%, -50%) scale(.3)', opacity: 0 }, { transform: 'translate(-50%, -50%) scale(1.15)', opacity: 1, offset: 0.45 }, { transform: 'translate(-50%, -50%) scale(1)', opacity: 1 }],
          { duration: 520, easing: 'cubic-bezier(.3, 1.4, .5, 1)', fill: 'both' });
        await ctx.wait(skipped ? 300 : 1100);
        field.forEach((e) => e.classList.remove('sh-hot'));
      }
      await out.finished.catch(() => {});
    } finally {
      ctx.skip = sk0;
      L.remove();
      h.stage.classList.remove('sh-raining');
    }
  }

  // ---------- кроки ----------
  const STEPS = {
    // «Дукатний дощ» — своя сцена; інші morph (без why чи чужі) — як у кіті
    async morph(s, ctx) {
      if (s.why === 'rain') return rainScene(s, ctx);
      return SK.steps.morph(s, ctx);
    },
    async holdIn(s, ctx) {
      const h = H(ctx);
      const rained = h.rainOpened; h.rainOpened = false;   // дощ уже сказав «скарб відкрито» — не повторюємось
      ctx.clearWin();
      ctx.showWin([{ cells: s.coins.map(([c, r]) => [c, r]) }], true);
      h.stage.classList.add('sh-trig');
      ctx.sound('bell'); if (!rained) ctx.say(pick(SAY.trigger), 3000);
      await ctx.wait(rained ? 650 : 1300);
      h.stage.classList.remove('sh-trig');
      ctx.sound('bonus');
      const ov = ctx.overlay('sh-chest-ov',
        '<div class="sh-chest-card"><div class="sh-chest-rays"></div><div class="sh-chest-box">' + ex(ctx, 'chest') + '</div>'
        + '<div class="sh-chest-t">утримуй і вигравай</div><div class="sh-chest-s">3 респіни · новий дукат — знову 3</div>'
        + '<div class="sh-chest-hint">тисни, щоб почати</div></div>');
      await ctx.wait(450, true);
      ov.classList.add('open'); ctx.sound('lid');
      ctx.timeout(() => {
        const b = ov.querySelector('.sh-chest-box'); if (b) ctx.fx.at(b, { kind: 'coin', n: 46, speed: 720 });
        ctx.sound('coin'); ctx.flash('#ffd98a', { power: 0.6 }); ctx.shake(0.8);
      }, 900);
      ctx.setScene('bonus');
      await ctx.wait(ctx.auto ? 2000 : 3200, true);
      enterHold(ctx, s.coins);
      await ctx.closeOverlay(ov);
      await ctx.wait(350);
    },
    async respin(s, ctx) {
      const h = H(ctx); if (!h || !h.on) return;
      const now = s.left - 1;
      setCandles(ctx, now, 'out'); ctx.sound('puff');
      if (now === 0) ctx.say(pick(SAY.last), 2600);
      const order = [];
      for (let c = 0; c < COLS; c++) for (let r = 0; r < ROWS; r++) if (!h.g[c][r]) order.push([c, r]);
      order.forEach(([c, r]) => spinCell(ctx, c, r));
      const land = new Map(s.land.map((x) => [x[0] + ',' + x[1], x]));
      const slow = !!s.slow;
      let rise = null;
      if (slow) { h.stage.classList.add('sh-slow'); try { rise = SK.sound.rise(1800 + order.length * 160); } catch (e) { rise = null; } }
      await ctx.wait(slow ? 900 : 340);
      for (let i = 0; i < order.length; i++) {
        const [c, r] = order[i], rest = order.length - i;
        if (slow && rest <= 3) { h.hc[c][r].classList.add('sh-tease'); await ctx.wait(rest === 1 ? 950 : 520); }
        else if (i) await ctx.wait(slow ? 150 : 55);
        const x = land.get(c + ',' + r), e = h.hc[c][r];
        e.classList.remove('spin', 'sh-tease'); e.style.removeProperty('--d');
        if (x) {
          h.g[c][r] = { k: x[2], v: x[3] }; setCell(ctx, c, r); anim(e, 'sh-stick', 900, ctx);
          ctx.sound('dzen', { p: 1 + (i % 5) * 0.06 }); ctx.fx.at(e, { kind: 'spark', n: 18, speed: 420 });
          if (x[2] === 'mini' || x[2] === 'major') { jpFlash(ctx, x[2], true); ctx.say(pick(SAY[x[2]]), 2200); }
          updSum(ctx, true);
        } else { setCell(ctx, c, r); anim(e, 'sh-land', 400, ctx); if (c !== (order[i + 1] || [])[0]) ctx.sound('stop', { pitch: 1.25 - c * 0.04 }); }
      }
      if (rise && rise.stop) rise.stop(); else if (typeof rise === 'function') rise();
      h.stage.classList.remove('sh-slow');
      if (s.land.length) {
        setCandles(ctx, 3, 'reset'); ctx.sound('level');
        if (!s.land.some((x) => x[2] !== 'coin')) ctx.say(pick(SAY.reset), 1800);
      }
      await ctx.wait(s.land.length ? 560 : 300);
    },
    async double(s, ctx) {
      const h = H(ctx); if (!h || !h.on) return;
      const [pc, pr] = s.at, pe = h.hc[pc][pr];
      anim(pe, 'sh-power', 1400, ctx); ctx.sound('bell'); ctx.say(pick(SAY.pirnach), 2200);
      h.stage.classList.add('sh-wave'); ctx.timeout(() => h.stage.classList.remove('sh-wave'), 1400);
      await ctx.wait(500);
      let maxD = 0;
      s.cells.forEach(([c, r, v]) => {
        h.g[c][r].v = v;
        const d = Math.hypot(c - pc, r - pr); maxD = Math.max(maxD, d);
        ctx.timeout(() => { setCell(ctx, c, r); anim(h.hc[c][r], 'sh-dbl', 700, ctx); floatAt(ctx, c, r, '×2', 'x2'); ctx.sound('tick', { p: 0.3 + d / 6 }); }, d * 130);
      });
      await ctx.wait(maxD * 130 + 700);
      updSum(ctx, true);
    },
    async collect(s, ctx) {
      const h = H(ctx); if (!h || !h.on) return;
      const [mc, mr] = s.at, me = h.hc[mc][mr];
      me.classList.add('sh-power'); ctx.sound('bell'); ctx.say(pick(SAY.mace), 2400);
      await ctx.wait(450);
      s.from.forEach(([c, r, v], i) => ctx.timeout(() => {
        anim(h.hc[c][r], 'sh-give', 500, ctx); fly(ctx, [c, r], [mc, mr], '×' + v);
        ctx.sound('dzen', { p: 0.9 + i * 0.05 });
      }, i * 110));
      await ctx.wait(s.from.length * 110 + 600);
      h.g[mc][mr].v = s.total; setCell(ctx, mc, mr);
      me.classList.remove('sh-power'); anim(me, 'sh-stick', 900, ctx);
      ctx.fx.at(me, { kind: 'spark', n: 34, speed: 560 }); ctx.sound('big');
      floatAt(ctx, mc, mr, '×' + s.total, 'big');
      updSum(ctx, true);
      await ctx.wait(800);
    },
    async holdCount(s, ctx) {
      const h = H(ctx); if (!h || !h.on) return;
      ctx.say(pick(SAY.count), 2000);
      h.stage.classList.add('sh-counting');
      let acc = ctx.meter, i = 0;
      for (const [c, r, amt, k] of s.cells) {
        const e = h.hc[c][r], jp = k === 'mini' || k === 'major';
        anim(e, 'sh-count', 600, ctx);
        floatAt(ctx, c, r, '+' + fmt(amt), jp ? 'big' : '');
        if (jp) { jpFlash(ctx, k); ctx.sound('big'); ctx.fx.at(e, { kind: 'coin', n: 24, speed: 520 }); }
        else ctx.sound('dzen', { p: 1 + Math.min(i, 14) * 0.04 });
        acc += amt;
        await ctx.rollMeter(acc, jp ? 700 : 240);
        await ctx.wait(jp ? 500 : 90);
        e.classList.add('done'); i++;
      }
      h.stage.classList.remove('sh-counting');
      await ctx.wait(300);
    },
    async grand(s, ctx) {
      const h = H(ctx);
      if (h) { h.stage.classList.add('sh-grand'); jpFlash(ctx, 'grand', true); }
      ctx.say(pick(SAY.grand), 4000);
      const ov = ctx.overlay('sh-grand-ov',
        '<div class="sh-grand-card"><div class="sh-grand-rays"></div><div class="sh-grand-coin">' + ex(ctx, 'coin', 'Гетьман') + '</div>'
        + '<div class="sh-grand-t">гетьманський скарб!</div><div class="sh-grand-s">усі п\'ятнадцять гнізд — твої · ' + JP.grand + '× ставки</div><div class="sh-grand-n">0</div></div>');
      ctx.sound('big'); ctx.fx.rain({ kind: 'coin', ms: 4200, rate: 34, size: 14 });
      const n = ov.querySelector('.sh-grand-n');
      await ctx.wait(500, true);
      // s.amount — доплата до стелі (сервер), а свято показує весь скарб: 1000× ставки = лічильник + доплата
      const from = ctx.meter, full = from + s.amount;
      await ctx.roll(0, full, 3200, (v) => { n.textContent = fmt(v); });
      ctx.sound('level'); ctx.fx.at(n, { kind: 'coin', n: 60, speed: 760 });
      await ctx.rollMeter(full, 500);
      await ctx.wait(2000, true);
      await ctx.closeOverlay(ov);
    },
    async holdOut(s, ctx) {
      const ov = ctx.overlay('sk-bonus-out sh-out-ov',
        '<div class="sk-ov-card"><div class="sk-ov-t">скриня віддала</div><div class="sk-ov-n">0</div><div class="sk-ov-s">🏺</div></div>');
      const n = ov.querySelector('.sk-ov-n');
      ctx.sound('big');
      await ctx.roll(0, s.total, Math.min(3000, ctx.rollMs(s.total) * 0.6 + 500), (v) => { n.textContent = fmt(v); });
      ctx.fx.at(n, { kind: 'coin', n: 44, speed: 700 });
      if (s.total >= 10 * ctx.bet) { ctx.flash('#ffe08a', { power: 0.55 }); ctx.shake(0.6); }
      await ctx.wait(1700, true);
      leaveHold(ctx);
      ctx.setScene('base');
      await ctx.closeOverlay(ov);
    },
  };

  SK.define({
    id: ID,
    title: 'Козацький скарб',
    grid: { cols: COLS, rows: ROWS },
    spinStyle: 'reels',
    reels: REELS,
    lines: LINES,
    payUnit: 1 / 20,
    sounds: { win: 'bell' },
    paytable: [
      { key: 'wild', pays: PAY.wild, note: 'бунчук — дикий, замінює всіх, крім дукатів' },
      { key: 'cossack', pays: PAY.cossack, note: 'найдорожчий' },
      { key: 'horse', pays: PAY.horse }, { key: 'sabre', pays: PAY.sabre }, { key: 'mug', pays: PAY.mug }, { key: 'pipe', pays: PAY.pipe },
      { key: 's1', pays: PAY.s1, note: 'і вишита вина' }, { key: 's3', pays: PAY.s3, note: 'і вишита трефа' },
      { key: 'c5', pays: {}, note: '6+ дукатів будь-де (разом із дощем) — «утримуй і вигравай»' },
    ],
    rules: () => rulesHtml(),
    // Глек-ведучий кіта говорить за дрібні виграші — наші слова, рідше (кіт тротлить)
    say: {
      win: ['дзень!', 'козацька удача', 'є копієчка', 'на тютюн вистачить'],
      small: ['хоч на люльку', 'копієчка до копієчки'],
      nice: ['оце по-козацьки!', 'кошовий кивнув'],
      big: ['оце так здобич!', 'кошовий заздрить', 'тримай кишеню ширше'],
      dry: ['козак терпить — і отаманом стає', 'дукати сплять — хай поспать'],
      surprise: SAY.rain,
    },
    surprises: { rain: { title: 'Дукатний дощ', own: true } },
    initialState: () => ({}),
    spin(bet, state) { return scriptFor(REELS.map((s) => SK.rnd.int(s.length)), bet, state); },
    demo: {
      'Малий виграш': demoBy((x) => x.m >= 1 && x.m < 4 && x.coins < 5 && x.n <= 2),
      'Очікування': demoBy((x) => x.t4),
      'Утримуй і вигравай': demoBy((x) => x.coins >= 6 && x.m < 2, { saves: 1 }),
      'Респіни зі скиданням': demoBy((x) => x.coins === 6 && x.m < 2, { pLand: 0.03, saves: 3 }),
      'Булава': demoBy((x) => x.coins >= 7 && x.m < 2, { plan: [{ at: 1, k: 'mace' }], saves: 1 }),
      'Пірнач': demoBy((x) => x.coins >= 7 && x.m < 2, { plan: [{ at: 1, k: 'pirnach' }], saves: 1 }),
      'Джекпот Міні': demoBy((x) => x.coins >= 6 && x.m < 2, { plan: [{ at: 0, k: 'mini' }] }),
      'Джекпот Мажор': demoBy((x) => x.coins >= 6 && x.m < 2, { plan: [{ at: 1, k: 'major' }], saves: 1 }),
      'Гетьманський скарб': demoBy((x) => x.coins >= 8 && x.m < 2, { pLand: 0.2, saves: 99, fill: true }),
      'Великий занос': (bet, state) => {
        const l = pool().filter((x) => x.m >= 10 && x.m < 25 && x.coins < 6);
        return l.length ? scriptFor(SK.rnd.pick(l).stops, bet, state, { rain: 0 }) : bonusIn(10, 25, { saves: 1 })(bet, state);
      },
      // дощ, що не довів до скрині: 1–3 дукати на поле з кількома дукатами
      'Дукатний дощ': (bet, state) => {
        const x = SK.rnd.pick(pool().filter((p) => p.coins >= 1 && p.coins <= 3 && p.m < 2));
        return scriptFor(x.stops, bet, state, { rain: Math.min(3, 5 - x.coins) });
      },
      // дощ доніс шостий (сьомий) дукат — «Скарб відкрито!» → скриня
      'Дощ → скарб': (bet, state) => {
        for (let i = 0; i < 40; i++) {
          const x = SK.rnd.pick(pool().filter((p) => (p.coins === 4 || p.coins === 5) && p.m < 2));
          const s = scriptFor(x.stops, bet, state, { rain: 6 - x.coins + (Math.random() < 0.35 ? 1 : 0), saves: 1 });
          if (!s.full) return s;
        }
        return scriptFor(pickBy((p) => p.coins === 4), bet, state, { rain: 2, saves: 1 });
      },
      'Мега занос': bonusIn(25, 50, { saves: 1, plan: [{ at: 1, k: 'pirnach' }] }),
      'Епічний занос': bonusIn(50, 400, { saves: 2, rich: true, plan: [{ at: 1, k: 'mace' }, { at: 2, k: 'pirnach' }] }, 7),
    },
    bonusSteps: ['holdIn'],
    // на сайті slot.js підставляє view.table: pay ({ sym: { "5": … } }) і jackpots ({ mini, major, grand })
    _setPay(pay, table) {
      if (table) T = table;
      // стрічки — з сервера (щоб ніколи не розійтися з зупинками); масиви ті самі, що в kit (machine.reels)
      if (table && Array.isArray(table.reels)) {
        table.reels.forEach((st, c) => { if (REELS[c] && Array.isArray(st) && st.length) { REELS[c].length = 0; st.forEach((k) => REELS[c].push(String(k))); } });
        POOL = null;
      }
      if (pay) Object.keys(pay).forEach((k) => { if (!PAY[k]) return; Object.keys(PAY[k]).forEach((n) => delete PAY[k][n]); Object.keys(pay[k]).forEach((n) => { PAY[k][n] = +pay[k][n]; }); });
      const jp = table && table.jackpots; if (jp) ['mini', 'major', 'grand'].forEach((k) => { if (jp[k] != null) JP[k] = +jp[k]; });
      const cv = table && table.coins; if (cv) Object.keys(cv).forEach((k) => { if (k in COINV) COINV[k] = +cv[k]; });
    },
    _pool: pool, _evaluate: evaluate, _holdSim: holdSim, _scriptFor: scriptFor, _reels: REELS,

    build(ctx) {
      ensureSyms();
      const port = ctx.orient === 'port';
      const S = port ? 76 : 146;
      const prev = ctx.sh || {};
      const st = document.createElement('div');
      st.className = 'sh-stage ' + (port ? 'sh-port' : 'sh-land');
      ctx.area.appendChild(st);
      const frame = '<div class="sh-frame" style="width:' + COLS * S + 'px;height:' + ROWS * S + 'px;--S:' + S + 'px">'
        + '<div class="sh-frame-back">' + ex(ctx, 'frameBack') + '</div><div class="sh-reelhost"></div><div class="sh-hold"></div>'
        + '<div class="sh-frame-top">' + ex(ctx, 'frame') + '</div></div>';
      const logo = '<div class="sh-logo">' + ctx.logoHtml() + '</div>';
      st.innerHTML = '<div class="sh-jp">' + ex(ctx, 'jackpots') + '</div>'
        + (port
          ? '<div class="sh-mid">' + frame + '</div><div class="sh-bottom">' + logo + hintHtml(ctx) + candHtml() + sumHtml() + '</div>'
          : '<div class="sh-mid"><div class="sh-side sh-side-l">' + logo + candHtml() + '</div>' + frame + '<div class="sh-side sh-side-r">' + hintHtml(ctx) + sumHtml() + '</div></div>');
      const holdEl = st.querySelector('.sh-hold');
      const hc = [];
      for (let c = 0; c < COLS; c++) {
        hc.push([]);
        for (let r = 0; r < ROWS; r++) {
          const e = document.createElement('div'); e.className = 'sh-hc';
          e.style.cssText = 'left:' + c * S + 'px;top:' + r * S + 'px;width:' + S + 'px;height:' + S + 'px';
          holdEl.appendChild(e); hc[c].push(e);
        }
      }
      ctx.sh = { stage: st, holdEl, hc, S, on: false, g: null, greeted: prev.greeted };
      ctx.makeReels(st.querySelector('.sh-reelhost'), { cols: COLS, rows: ROWS, size: S, gap: 0, strips: REELS });
      setJp(ctx);
      setCandles(ctx, 3);
      if (!ctx.sh.greeted) { ctx.sh.greeted = true; ctx.timeout(() => { if (!ctx.busy) ctx.say(pick(SAY.idle), 4000); }, 700); }
    },
    steps: STEPS,
    onBet(ctx) { setJp(ctx); },
    onSpinStart(ctx) {
      const h = H(ctx); if (h) { h.stage.classList.remove('sh-tease', 'sh-winning'); h.rainOpened = false; }
    },
    onTease(ctx) {
      const h = H(ctx); if (!h) return;
      if (!h.stage.classList.contains('sh-tease')) ctx.say(pick(SAY.tease), 2000);
      h.stage.classList.add('sh-tease');
      ctx.reels.cells().forEach((e) => { if (e && isCoin(e.dataset.k) && e.closest('.sk-reel') && !e.closest('.sk-reel').classList.contains('sk-tease')) e.classList.add('sh-hot'); });
    },
    onReelStop(ctx, c) {
      const h = H(ctx); if (!h || !ctx.reels) return;
      let any = false;
      for (let r = 0; r < ROWS; r++) { const e = ctx.reels.cell(c, r); if (e && isCoin(e.dataset.k)) { any = true; anim(e, 'sh-landcoin', 600, ctx); } }
      if (any) ctx.sound('dzen', { p: 0.8 + c * 0.05 });
      if (c === COLS - 1) { h.stage.classList.remove('sh-tease'); ctx.reels.cells().forEach((e) => e && e.classList.remove('sh-hot')); }
    },
    onWin(ctx) {
      const h = H(ctx); if (h) h.stage.classList.add('sh-winning');
    },
    // після перезавантаження slot.js ставить поле за stops (до дощу) — дукати дощу кладемо тихо
    onRestore(ctx, script) {
      const m = script && (script.steps || []).find((x) => x.t === 'morph' && x.why === 'rain');
      if (m && ctx.reels) (m.cells || []).forEach(([c, r, key]) => putCoin(ctx, c, r, key));
    },
    unbuild(ctx) { ctx.sh = Object.assign({}, ctx.sh, { stage: null }); },
  });
})();
