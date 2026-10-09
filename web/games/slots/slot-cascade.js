/* Розбиті глеки (slot-cascade): 6×5 на стелажі, платить будь-де від 8 однакових; виграшний посуд тріскається
   своїми черепками, решта падає, згори — нові (каскад). Писанки-множники, горно → вільні оберти з накопиченням.
   Механіка, мок-математика й сценарії показу — тут; арт — slot-cascade-art.js (SlotArt['slot-cascade']). */
(function () {
  'use strict';
  const SK = window.SlotKit, ID = 'slot-cascade', COLS = 6, ROWS = 5;
  const LOW = ['k1', 'k2', 'k3', 'k4'], HIGH = ['bowl', 'pot', 'makitra', 'kumanets', 'glek'], PAYING = LOW.concat(HIGH);
  // Виплати в ставках: [8–9, 10–11, 12+] однакових будь-де
  const PAY = {
    k1: [0.3, 0.9, 2.5], k2: [0.5, 1, 4], k3: [0.6, 1.2, 5], k4: [1, 1.5, 8],
    bowl: [1.2, 2, 10], pot: [1.5, 2.5, 12], makitra: [2, 5, 15], kumanets: [2.5, 10, 25], glek: [10, 25, 50],
  };
  const FURN = 'furnace', FURN_PAY = { 4: 3, 5: 5, 6: 100 };
  const MULTS = [2, 3, 5, 10, 25, 50, 100];
  // Ваги символів (pys — писанка; її число — з ваг M_*)
  const W_BASE = { k1: 20, k2: 19, k3: 18, k4: 17, bowl: 12, pot: 10, makitra: 8, kumanets: 6, glek: 4.5, furnace: 2.3, pys: 0.45 };
  const W_FS = { k1: 20, k2: 19, k3: 18, k4: 17, bowl: 12, pot: 10, makitra: 8, kumanets: 6, glek: 4.5, furnace: 1.9, pys: 1.6 };
  const M_BASE = { 2: 40, 3: 26, 5: 18, 10: 10, 25: 4, 50: 1.5, 100: 0.5 };
  const M_FS = { 2: 34, 3: 26, 5: 20, 10: 12, 25: 5, 50: 2, 100: 1 };
  const POOL = PAYING.concat([FURN]);
  const isM = (k) => typeof k === 'string' && /^x\d+$/.test(k);
  const mv = (k) => +k.slice(1);
  const fmt = (v) => SK.fmt(v);

  // Писанки з числом — окремі «символи» x2…x100 з extras.multiplier арту
  (function regMults() {
    const art = window.SlotArt && SlotArt[ID];
    if (!art || !art.symbols) return;
    MULTS.forEach((v) => {
      const k = 'x' + v; if (art.symbols[k]) return;
      let svg = null; try { svg = art.extras && art.extras.multiplier && art.extras.multiplier('×' + v); } catch (e) { svg = null; }
      if (svg) art.symbols[k] = { name: 'Писанка ×' + v, tier: 'special', svg };
    });
  })();

  // ---------- мок-математика ----------
  function rk(o) { const k = SK.rnd.weighted(o.w); return k === 'pys' ? 'x' + SK.rnd.weighted(o.m) : k; }
  function randGrid(o) { return Array.from({ length: COLS }, () => Array.from({ length: ROWS }, () => rk(o))); }
  function evaluate(grid, bet) {
    const pos = {};
    grid.forEach((col, c) => col.forEach((k, r) => { (pos[k] = pos[k] || []).push([c, r]); }));
    const items = [];
    PAYING.forEach((k) => {
      const cells = pos[k]; if (!cells || cells.length < 8) return;
      const n = cells.length, u = PAY[k][n >= 12 ? 2 : n >= 10 ? 1 : 0];
      items.push({ sym: k, n, cells, amount: Math.max(1, Math.round(u * bet)) });
    });
    return items;
  }
  function furnCells(grid) { const a = []; grid.forEach((col, c) => col.forEach((k, r) => { if (k === FURN) a.push([c, r]); })); return a; }
  function dropGrid(grid, rem, o) {
    const set = new Set(rem.map(([c, r]) => c + ',' + r));
    return grid.map((col, c) => {
      const keep = col.filter((k, r) => !set.has(c + ',' + r));
      return Array.from({ length: ROWS - keep.length }, () => rk(o)).concat(keep);
    });
  }
  // Очікування — лише чесно: колонки, що падають, коли до бонусу бракує рівно одного горна
  function teaseFor(grid, need) {
    const t = []; let n = 0;
    for (let c = 0; c < COLS; c++) {
      if (n === need - 1) t.push(c);
      n += grid[c].filter((k) => k === FURN).length;
      if (n >= need) break;
    }
    return t;
  }
  // Каскад до кінця + писанки наприкінці. acc — накопичений множник бонусу
  function tumble(grid, bet, o, acc) {
    const steps = []; let seq = 0, n = 0;
    for (let guard = 0; guard < 30; guard++) {
      const items = evaluate(grid, bet); if (!items.length) break;
      const amount = items.reduce((a, it) => a + it.amount, 0); seq += amount;
      steps.push({ t: 'win', items, amount, hold: 260 });
      const remove = items.flatMap((it) => it.cells), next = dropGrid(grid, remove, o); n++;
      steps.push({ t: 'cascade', remove, grid: next, n }); grid = next;
    }
    const pys = []; grid.forEach((col, c) => col.forEach((k, r) => { if (isM(k)) pys.push([c, r, mv(k)]); }));
    let mult = null, add = 0;
    if (seq > 0 && pys.length) {
      const sum = pys.reduce((a, p) => a + p[2], 0), from = o.fs ? acc || 0 : 0, to = from + sum;
      add = seq * to - seq;
      mult = { t: 'mult', cells: pys, from, to, seq, add, fs: !!o.fs };
      steps.push(mult);
    }
    return { steps, grid, win: seq + add, casc: n, mult };
  }
  function bonus(bet, o) {
    const fo = Object.assign({ w: W_FS, m: M_FS, fs: true }, o.fsO || {});
    const steps = [{ t: 'bonusIn', count: 10 }];
    let left = 10, total = 10, acc = 0, win = 0, nm = 0, spins = 0, maxCasc = 0;
    while (left > 0 && spins < 60) {
      left--; spins++; steps.push({ t: 'fs', left });
      const g = randGrid(fo);
      steps.push({ t: 'spin', grid: g, tease: total < 50 ? teaseFor(g, 3) : [] });
      const t = tumble(g, bet, fo, acc); steps.push(...t.steps); win += t.win; maxCasc = Math.max(maxCasc, t.casc);
      if (t.mult) { nm++; acc = t.mult.to; }
      const fc = furnCells(t.grid);
      if (fc.length >= 3 && total < 50) { steps.push({ t: 'furn', cells: fc, amount: 0, fs: true }); steps.push({ t: 'fs', add: 5 }); left += 5; total += 5; }
    }
    steps.push({ t: 'bonusOut', total: win, spins, acc });
    return { steps, win, nm, acc, spins, maxCasc };
  }
  function makeScript(bet, state, o) {
    o = Object.assign({ w: W_BASE, m: M_BASE }, o || {});
    const grid = randGrid(o); if (o.force) o.force(grid);
    const tease = teaseFor(grid, 4);
    const steps = [{ t: 'spin', grid, tease }];
    const meta = { casc: 0, mults: [], bonus: false, tease: tease.length > 0, fsMults: 0, acc: 0 };
    const t = tumble(grid, bet, o, 0); steps.push(...t.steps);
    let win = t.win; meta.casc = t.casc; if (t.mult) meta.mults.push(t.mult.to);
    const fc = furnCells(t.grid);
    if (fc.length >= 4) {
      const pay = Math.round(FURN_PAY[Math.min(6, fc.length)] * bet);
      steps.push({ t: 'furn', cells: fc, amount: pay }); win += pay;
      const b = bonus(bet, o); steps.push(...b.steps); win += b.win;
      Object.assign(meta, { bonus: true, fsMults: b.nm, acc: b.acc, fsWin: b.win, spins: b.spins });
    }
    return { bet, steps, win, state: state || {}, meta };
  }

  // ---------- сценарії показу ----------
  function place(key, n, maxCol) {
    return (g) => {
      const cols = SK.rnd.pick([[0, 1, 2, 3, 4, 5]]).slice(0, maxCol || COLS).sort(() => Math.random() - 0.5).slice(0, n);
      cols.forEach((c) => { g[c][SK.rnd.int(ROWS)] = key; });
    };
  }
  function demoBy(pred, o, tries) {
    return (bet, state) => {
      let s = null;
      for (let i = 0; i < (tries || 4000); i++) {
        s = makeScript(bet, state, typeof o === 'function' ? o() : o);
        if (pred(s.meta, s.win / bet, s)) return s;
      }
      return s;
    };
  }
  const W_RICH = Object.assign({}, W_BASE, { glek: 9, kumanets: 9, makitra: 9, pys: 1.4, furnace: 0.6 });
  const W_LOWS = { k1: 30, k2: 28, k3: 26, k4: 24, bowl: 8, pot: 6, makitra: 4, kumanets: 3, glek: 2, furnace: 0.6, pys: 0 };
  const NOPYS = Object.assign({}, W_BASE, { pys: 0, furnace: 1 });

  const SAY = {
    idle: ['полиці повні — бий посуд', 'тут платять за биті глеки', 'вісім однакових — і дзень!'],
    win: ['дзень!', 'тріснуло — заплатили', 'бий-бий, ще наліплю'],
    casc: ['ще падає!', 'та воно саме б\'ється!', 'каскад пішов — не зупиняй'],
    big: ['оце погром!', 'весь стелаж — у черепки', 'гончар плаче, а ти радієш'],
    tease: ['ану ще одне горно…', 'не дихай…', 'жар близько…'],
    mult: ['писанка множить!', 'на писанку — і в кишеню', 'крашанки не б\'ються, а множать'],
  };

  // ---------- черепки: картинки з extras.shards(key) для частинок ----------
  const SHARD_IMG = {};
  function shardImgs(ctx, key) {
    if (SHARD_IMG[key]) return SHARD_IMG[key];
    let list = null;
    try { list = ctx.art && ctx.art.extras && ctx.art.extras.shards && ctx.art.extras.shards(key); } catch (e) { list = null; }
    SHARD_IMG[key] = (list || []).map((svg) => { const im = new Image(); im.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg); return im; });
    return SHARD_IMG[key];
  }
  // черепок: вигнутий (сплющення за обертом) — так здається, що він крутиться в повітрі
  SK.particles.scShard = function (g, p) {
    const im = p.color, s = p.s;
    if (!im || !im.complete || !im.naturalWidth) return;
    g.scale(1, Math.max(0.3, Math.abs(Math.cos(p.spin * 0.6))));
    g.drawImage(im, -s, -s, 2 * s, 2 * s);
  };
  const CRACK = '<svg viewBox="0 0 100 100"><g class="sc-crk-o"><path pathLength="100" d="M52 6 L45 30 L58 44 L41 66 L50 94"/><path pathLength="100" d="M45 30 L20 38 L8 34"/><path pathLength="100" d="M58 44 L82 54 L94 70"/><path pathLength="100" d="M41 66 L22 78"/></g>'
    + '<g class="sc-crk"><path pathLength="100" d="M52 6 L45 30 L58 44 L41 66 L50 94"/><path pathLength="100" d="M45 30 L20 38 L8 34"/><path pathLength="100" d="M58 44 L82 54 L94 70"/><path pathLength="100" d="M41 66 L22 78"/></g></svg>';

  // ---------- DOM ----------
  const stageOf = (ctx) => ctx.area.querySelector('.sc-stage');
  function setCounter(ctx, v, bump) {
    const st = stageOf(ctx); if (!st) return;
    const t = st.querySelector('.sc-counter-text'); if (!t) return;
    t.textContent = '×' + fmt(Math.max(1, v));
    const box = st.querySelector('.sc-mult');
    box.classList.toggle('on', v > 0);
    box.classList.toggle('gold', v >= 25);
    if (bump) { box.classList.remove('bump'); void box.offsetWidth; box.classList.add('bump'); }
  }
  function setCasc(ctx, n) {
    const st = stageOf(ctx); if (!st) return;
    const p = st.querySelector('.sc-casc'); if (!p) return;
    p.querySelector('b span').textContent = n;
    p.classList.toggle('on', n > 0); p.classList.toggle('hot', n >= 3);
    if (n > 0) { p.classList.remove('pop'); void p.offsetWidth; p.classList.add('pop'); }
  }
  function land(ctx, cols) {
    cols.forEach((c) => {
      for (let r = 0; r < ROWS; r++) {
        const e = ctx.cell(c, r); if (!e) continue;
        e.classList.remove('sc-land'); void e.offsetWidth; e.classList.add('sc-land');
      }
    });
    ctx.timeout(() => ctx.reels && ctx.reels.cells().forEach((e) => e && e.classList.remove('sc-land')), 380);
  }
  function hintsHtml(ctx) {
    return '<div class="sc-hints">'
      + '<div class="sc-hint"><span class="sc-hint-n">8+</span><span>однакових будь-де — виграш, посуд б\'ється</span></div>'
      + '<div class="sc-hint"><span class="sc-hint-i sk-cell">' + ctx.symHtml(FURN) + '</span><span><b>4 горна</b> — 10 вільних обертів</span></div>'
      + '<div class="sc-hint"><span class="sc-hint-i sk-cell">' + ctx.symHtml('x10') + '</span><span>писанка множить увесь каскад</span></div>'
      + '</div>';
  }

  SK.define({
    id: ID,
    title: 'Розбиті глеки',
    grid: { cols: COLS, rows: ROWS },
    spinStyle: 'drop',
    payUnit: 1,
    sounds: { win: 'bell' },
    paytable: PAYING.slice().reverse().map((k) => ({ key: k, unit: '+', pays: { 12: PAY[k][2], 10: PAY[k][1], 8: PAY[k][0] }, note: k === 'glek' ? 'найдорожчий' : '' }))
      .concat([
        { key: FURN, pays: FURN_PAY, unit: '', note: '4+ будь-де — 10 вільних обертів; у бонусі 3+ — ще 5' },
        { key: 'x10', pays: {}, note: 'писанка ×2…×100: наприкінці каскаду летить на дощечку й множить його виграш' },
      ]),
    rules: '<b>6×5</b>, ліній нема: платять <b>8 і більше однакових будь-де</b> на полицях (8–9, 10–11, 12+). '
      + 'Виграшний посуд б\'ється, решта падає, згори падають нові — і так, поки є виграш (<b>каскад</b>). '
      + '<b>Писанка</b> — множник: наприкінці каскаду всі писанки летять на дощечку й додаються, сума множить виграш оберту. '
      + '<b>Горно</b>: 4+ — 10 вільних обертів; у вільних 3+ горна — ще 5, а писанки <b>не згорають</b>: множник росте до кінця бонусу '
      + 'й множить кожен оберт, де впала нова писанка. Висока волатильність: часто тихо, зате як піде — то стелажем.',
    initialState: () => ({}),
    spin(bet, state) { return makeScript(bet, state); },
    demo: {
      'Малий виграш': demoBy((m, x) => m.casc === 1 && !m.bonus && !m.mults.length && x >= 0.3 && x < 3, { w: NOPYS }),
      'Каскад 3+': demoBy((m, x) => m.casc >= 3 && !m.bonus && x < 10, { w: W_LOWS }),
      'Писанка ×10': demoBy((m) => !m.bonus && m.mults[0] === 10, { w: NOPYS, force: place('x10', 1) }),
      'Очікування горна': demoBy((m) => m.tease && !m.bonus, { w: NOPYS, force: (g) => { [0, 1, 2].forEach((c) => { g[c][SK.rnd.int(ROWS)] = FURN; }); } }),
      'Вхід у бонус': demoBy((m) => m.bonus && m.fsWin > 0, { force: place(FURN, 4) }),
      'Бонус з множниками': demoBy((m) => m.bonus && m.fsMults >= 3 && m.acc >= 15, { force: place(FURN, 4), fsO: { w: Object.assign({}, W_FS, { pys: 4 }) } }, 600),
      'Великий занос': demoBy((m, x) => !m.bonus && x >= 10 && x < 25, { w: W_RICH }),
      'Мега занос': demoBy((m, x) => !m.bonus && x >= 25 && x < 50, { w: W_RICH }),
      'Епічний занос': demoBy((m, x) => !m.bonus && x >= 50 && x < 600, { w: W_RICH }),
    },
    _make: makeScript, _evaluate: evaluate, _tease: teaseFor,

    build(ctx) {
      const sc = ctx.sc = ctx.sc || { acc: 0, casc: 0, mult: 0, greeted: false };
      const port = ctx.orient === 'port';
      const SIDE = 270;
      const k = port ? Math.min((ctx.areaW - 8) / 640, (ctx.areaH - 190) / 560) : Math.min((ctx.areaH - 14) / 560, (ctx.areaW - 2 * SIDE - 40) / 640);
      const st = document.createElement('div'); st.className = 'sc-stage ' + (port ? 'sc-port' : 'sc-land');
      ctx.area.appendChild(st);
      const counter = ctx.extra('counter');
      const multHtml = '<div class="sc-mult"><div class="sc-counter-box">' + (counter || '<div class="sc-counter-ph"></div>')
        + '<div class="sc-counter-text">×1</div></div><small class="sc-mult-l"><span class="sc-l-base">множник каскаду</span><span class="sc-l-fs">множник бонусу</span></small></div>';
      const cascHtml = '<div class="sc-casc"><small>каскад</small><b>×<span>0</span></b></div>';
      const frame = ctx.extra('frame');
      const frameHtml = '<div class="sc-frame-box" style="width:' + (640 * k).toFixed(1) + 'px;height:' + (560 * k).toFixed(1) + 'px">'
        + '<div class="sc-frame-in' + (frame ? '' : ' sc-frame') + '" style="transform:scale(' + k.toFixed(4) + ')">'
        + (frame ? '<div class="sc-frame-bg">' + frame + '</div>' : '')
        + '<div class="sc-field' + (frame ? '' : ' sc-shelves') + '"></div></div></div>';
      if (port) {
        st.innerHTML = '<div class="sc-logo">' + ctx.logoHtml() + '</div>'
          + '<div class="sc-row">' + cascHtml + multHtml + '</div>' + frameHtml;
      } else {
        st.innerHTML = '<div class="sc-side sc-left"><div class="sc-logo">' + ctx.logoHtml() + '</div>' + hintsHtml(ctx) + '</div>'
          + frameHtml
          + '<div class="sc-side sc-right">' + multHtml + cascHtml + '</div>';
      }
      const base = { w: NOPYS, m: M_BASE };
      ctx.makeReels(st.querySelector('.sc-field'), { cols: COLS, rows: ROWS, size: 100, gap: 0, pool: POOL,
        initial: ctx.keepGrid ? undefined : { grid: randGrid(base) } });
      setCounter(ctx, ctx.fs ? sc.acc : sc.mult);
      setCasc(ctx, sc.casc);
      POOL.forEach((key) => shardImgs(ctx, key));
      if (!sc.greeted) { sc.greeted = true; ctx.timeout(() => ctx.say(SK.rnd.pick(SAY.idle), 4000), 700); }
    },

    steps: {
      async spin(s, ctx) {
        ctx.sc.casc = 0; setCasc(ctx, 0);
        if (!ctx.fs) { ctx.sc.mult = 0; setCounter(ctx, 0); }
        await ctx.reels.spin(s);
        const st = stageOf(ctx); if (st) st.classList.remove('sc-tease');
      },
      async cascade(s, ctx) {
        await ctx.reels.cascade(s);
        land(ctx, Array.from(new Set(s.remove.map((p) => p[0]))));
        ctx.sc.casc = s.n || ctx.sc.casc + 1; setCasc(ctx, ctx.sc.casc);
        if (ctx.sc.casc === 3) ctx.say(SK.rnd.pick(SAY.casc), 2200);
        ctx.emit('cascade', ctx.sc.casc);
      },
      // писанки світяться → летять на дощечку → сума множить виграш каскаду
      async mult(s, ctx) {
        const st = stageOf(ctx); if (!st) return;
        ctx.clearWin();
        const eggs = s.cells.map(([c, r]) => ctx.cell(c, r)).filter(Boolean);
        eggs.forEach((e) => e.classList.add('win'));
        ctx.sound('level'); ctx.say(SK.rnd.pick(SAY.mult), 2600);
        await ctx.wait(560);
        const box = st.querySelector('.sc-counter-box'), K = ctx.scale || 1;
        const sr = st.getBoundingClientRect(), tr = box.getBoundingClientRect();
        const tx = (tr.left - sr.left + tr.width * 0.62) / K, ty = (tr.top - sr.top + tr.height * 0.5) / K;
        let cur = s.from;
        const gap = ctx.turbo ? 120 : 240, fly = ctx.turbo ? 380 : 680;
        await Promise.all(s.cells.map(([c, r, v], i) => new Promise((res) => {
          ctx.timeout(() => {
            const e = ctx.cell(c, r); if (!e) { cur += v; setCounter(ctx, cur, true); res(); return; }
            const er = e.getBoundingClientRect();
            const w = er.width / K, x0 = (er.left - sr.left) / K, y0 = (er.top - sr.top) / K;
            const f = document.createElement('div'); f.className = 'sc-fly';
            f.style.cssText = 'left:' + x0.toFixed(1) + 'px;top:' + y0.toFixed(1) + 'px;width:' + w.toFixed(1) + 'px;height:' + w.toFixed(1) + 'px';
            f.innerHTML = '<div class="win">' + ctx.symHtml('x' + v) + '</div>'; st.appendChild(f);
            e.classList.remove('win'); e.classList.add('sc-spent');
            const dx = tx - (x0 + w / 2), dy = ty - (y0 + w / 2), lift = Math.min(160, 60 + Math.abs(dx) * 0.25);
            ctx.sound('click');
            const a = f.animate([
              { transform: 'translate(0,0) scale(1) rotate(0)' },
              { transform: 'translate(0,-14px) scale(1.25) rotate(-8deg)', offset: 0.18 },
              { transform: 'translate(' + (dx * 0.5).toFixed(1) + 'px,' + (dy * 0.5 - lift).toFixed(1) + 'px) scale(1.15) rotate(10deg)', offset: 0.55 },
              { transform: 'translate(' + dx.toFixed(1) + 'px,' + dy.toFixed(1) + 'px) scale(.4) rotate(0)', opacity: 0.85 },
            ], { duration: fly, easing: 'cubic-bezier(.45,.1,.6,1)', fill: 'forwards' });
            let done = false;
            const fin = () => {
              if (done) return; done = true; f.remove(); cur += v; setCounter(ctx, cur, true);
              ctx.sound('coin', { pitch: 1 + i * 0.08 }); ctx.fx.at(box, { kind: 'spark', n: 16 + Math.min(30, v), speed: 420, size: 9, color: v >= 25 ? '#ffe27a' : '#fff1c4' });
              res();
            };
            a.finished.then(fin, fin); ctx.timeout(fin, fly + 200);
          }, i * gap);
        })));
        if (s.fs) ctx.sc.acc = s.to; else ctx.sc.mult = s.to;
        setCounter(ctx, s.to, true);
        await ctx.wait(220);
        // «N × M» над полем, лічильник виграшу біжить
        const fb = st.querySelector('.sc-frame-box'), fr = fb.getBoundingClientRect();
        const pop = document.createElement('div'); pop.className = 'sc-xpop' + (s.to >= 25 ? ' gold' : '');
        pop.style.left = ((fr.left - sr.left + fr.width / 2) / K).toFixed(1) + 'px'; pop.style.top = ((fr.top - sr.top + fr.height * 0.45) / K).toFixed(1) + 'px';
        pop.innerHTML = '<span class="sc-xp-a">' + fmt(s.seq) + '</span> <i>×</i> <span class="sc-xp-m">' + fmt(s.to) + '</span>';
        st.appendChild(pop); ctx.timeout(() => pop.remove(), 6000);
        ctx.sound('big');
        ctx.fx.burst((fr.left - ctx.root.getBoundingClientRect().left) + fr.width / 2, (fr.top - ctx.root.getBoundingClientRect().top) + fr.height * 0.45, { kind: 'spark', n: 40, speed: 700, size: 10, color: '#ffe27a' });
        await ctx.rollMeter(ctx.meter + s.add, Math.min(2600, ctx.rollMs(s.add)));
        pop.classList.add('done'); pop.innerHTML = '<span class="sc-xp-m">+' + fmt(s.seq * s.to) + '</span>';
        await ctx.wait(650);
        pop.classList.add('out');
        ctx.timeout(() => pop.remove(), 400);
      },
      // горна: світяться, жар; у базі ще й платять
      async furn(s, ctx) {
        const st = stageOf(ctx);
        ctx.clearWin();
        ctx.showWin([{ cells: s.cells, amount: s.amount || 0 }]);
        s.cells.forEach(([c, r]) => { const e = ctx.cell(c, r); if (e) ctx.fx.at(e, { kind: 'spark', n: 22, speed: 380, size: 10, angle: -Math.PI / 2, spread: 1.4, gravity: -40, color: '#ffb347', life: 1.3 }); });
        ctx.sound('bonus');
        if (st) st.classList.add('sc-hot');
        ctx.say(s.fs ? 'ще дровець — +5 обертів!' : 'горно розгоряється!', 2500);
        if (s.amount) await ctx.rollMeter(ctx.meter + s.amount, Math.min(1500, ctx.rollMs(s.amount)));
        await ctx.wait(s.fs ? 900 : 1300);
        if (st) st.classList.remove('sc-hot');
      },
    },

    // ---------- бонус: заставка й підсумок ----------
    async bonusIn(s, ctx) {
      ctx.sc.acc = 0; setCounter(ctx, 0);
      ctx.clearWin();
      ctx.setScene('bonus');
      const ov = ctx.overlay('sc-bin', '<div class="sc-bin-glow"></div><div class="sc-bin-card">'
        + '<div class="sc-bin-furn win sk-cell">' + ctx.symHtml(FURN) + '</div>'
        + '<div class="sc-bin-t">горно розпалилось!</div>'
        + '<div class="sc-bin-n">0</div><div class="sc-bin-s">вільних обертів</div>'
        + '<div class="sc-bin-p">писанки не згорають — множник росте до кінця бонусу</div>'
        + '<div class="sc-bin-hint">тисни, щоб почати</div></div>');
      const fur = ov.querySelector('.sc-bin-furn'), n = ov.querySelector('.sc-bin-n');
      const ember = (big) => ctx.fx.at(fur, { kind: 'spark', n: big ? 50 : 24, speed: big ? 700 : 450, size: big ? 12 : 9, angle: -Math.PI / 2, spread: 1.6, gravity: -80, color: '#ffb347', life: 1.6 });
      ctx.timeout(() => ember(true), 260);
      ctx.timeout(() => ember(false), 1100);
      ctx.timeout(() => ember(false), 2000);
      await ctx.wait(350, true);
      await ctx.roll(0, s.count, 700, (v) => { n.textContent = Math.round(v); });
      n.classList.add('pop');
      await ctx.wait(ctx.auto ? 1500 : 2700, true);
      await ctx.closeOverlay(ov);
    },
    async bonusOut(s, ctx) {
      const total = s.total || 0;
      const ov = ctx.overlay('sc-bout', '<div class="sc-bin-glow"></div><div class="sc-bin-card">'
        + '<div class="sc-bin-t">горно віддало</div><div class="sc-bin-n">0</div><div class="sc-bin-s">🏺 черепків</div>'
        + '<div class="sc-bin-p">' + (s.spins || 0) + ' обертів' + (s.acc ? ' · множник дійшов до ×' + fmt(s.acc) : '') + '</div></div>');
      const n = ov.querySelector('.sc-bin-n');
      ctx.sound('big');
      await ctx.roll(0, total, Math.min(4000, ctx.rollMs(total) + 600), (v) => { n.textContent = fmt(v); });
      n.classList.add('pop');
      if (total > 0) { ctx.fx.at(n, { kind: 'coin', n: 46, speed: 720 }); ctx.fx.at(n, { kind: 'shard', n: 16, speed: 600, size: 12 }); }
      await ctx.wait(1900, true);
      await ctx.closeOverlay(ov);
      ctx.setScene('base');
      ctx.sc.acc = 0; ctx.sc.mult = 0; setCounter(ctx, 0);
    },

    // свій тріск: тріщини → посуд розлітається своїми черепками
    async onRemove(els, ctx) {
      ctx.sound('crack');
      els.forEach((e) => {
        e.classList.remove('dim');
        const o = document.createElement('div'); o.className = 'sc-crackl';
        o.style.transform = 'rotate(' + (SK.rnd.int(4) * 90 - 8 + Math.random() * 16).toFixed(0) + 'deg) scaleX(' + (Math.random() < 0.5 ? -1 : 1) + ')';
        o.innerHTML = CRACK; e.appendChild(o); e.classList.add('sc-cracking');
      });
      await ctx.wait(ctx.turbo ? 170 : 320);
      const rr = ctx.root.getBoundingClientRect();
      els.forEach((e) => {
        const r = e.getBoundingClientRect(), cx = r.left - rr.left + r.width / 2, cy = r.top - rr.top + r.height / 2, W = r.width;
        const imgs = shardImgs(ctx, e._key).filter((im) => im.complete && im.naturalWidth);
        const n = imgs.length || 4;
        for (let j = 0; j < n; j++) {
          const a = -Math.PI / 2 + ((j + 0.5) / n - 0.5) * 3.4 + (Math.random() - 0.5) * 0.4;
          const ox = Math.cos(a) * W * 0.16, oy = Math.sin(a) * W * 0.16;
          ctx.fx.burst(cx + ox, cy + oy, imgs.length
            ? { kind: 'scShard', n: 1, angle: a, spread: 0.35, speed: W * 6.2, size: W * 0.2, gravity: W * 17, color: imgs[j], life: 1.7 }
            : { kind: 'shard', n: 1, angle: a, spread: 0.35, speed: W * 6.2, size: W * 0.13, gravity: W * 17, life: 1.7 });
        }
        ctx.fx.burst(cx, cy, { kind: 'spark', n: 4, speed: W * 2.6, size: W * 0.08, color: '#fff3d0', life: 0.5 });
        e.classList.add('sc-gone');
      });
      await ctx.wait(ctx.turbo ? 80 : 150);
    },

    onSpinStart(ctx) { const st = stageOf(ctx); if (st) st.classList.remove('sc-big', 'sc-hot', 'sc-tease'); },
    // очікування: горна, що вже лежать, розпалюються; полиця тремтить
    onTease(ctx) {
      const st = stageOf(ctx); if (st) st.classList.add('sc-tease');
      ctx.reels.cells().forEach((e) => { if (e && e._key === FURN) e.classList.add('win', 'sc-wait'); });
      ctx.say(SK.rnd.pick(SAY.tease), 2000);
    },
    onReelStop(ctx, c) {
      land(ctx, [c]);
      if (c === COLS - 1) {
        const st = stageOf(ctx); if (st) st.classList.remove('sc-tease');
        ctx.reels.cells().forEach((e) => { if (e && e.classList.contains('sc-wait')) e.classList.remove('win', 'sc-wait'); });
      }
    },
    onWin(ctx, step) {
      if (ctx.sc.casc >= 2) return;
      ctx.say(SK.rnd.pick(step.amount >= 10 * ctx.bet ? SAY.big : SAY.win), 2600);
    },
    onBigwin(ctx) { const st = stageOf(ctx); if (st) st.classList.add('sc-big'); ctx.say(SK.rnd.pick(SAY.big), 4000); },
  });
})();
