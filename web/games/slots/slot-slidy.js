/* Сліди на полиці (slot-slidy): полиця 6×6, платить кластер — 5+ однакових, що стикаються боками. Виграшний посуд
   б'ється, а на полиці лишається слід від глини: перший — мокре коло (×1), удруге там же — ×2, далі ×4 … ×128. Виграш
   кластера × сума слідів ×2+ під ним. У базі сліди живуть до кінця каскаду свого оберту; горно (3+) — 10 вільних, де
   сліди не стираються. «Купити бонус» — лише коли сервер його продає (view.buy). Сценарій рахує сервер
   (SlotSlidyMath, docs/games/specs/slot-slidy.md); мок нижче — лише для стенду docs/games/dev/slots/proto.html. */
(function () {
  'use strict';
  const SK = window.SlotKit, ID = 'slot-slidy', COLS = 6, ROWS = 6, N = COLS * ROWS;
  const PAYING = ['k1', 'k2', 'k3', 'pot', 'makitra', 'kumanets', 'glek'], FURN = 'furnace';
  const SIZES = [5, 6, 7, 8, 9, 11, 13, 16];
  const LABELS = ['5', '6', '7', '8', '9–10', '11–12', '13–15', '16+'];
  // Виплати в ставках за [5, 6, 7, 8, 9–10, 11–12, 13–15, 16+] — як на сервері (SlotSlidyMath.Pay10); на сайті slot.js
  // ще й підставляє view.table через _setPay, щоб ⓘ не розійшлась із касою
  const PAY = {
    k1: [0.5, 0.6, 0.8, 1, 1.5, 2.5, 5, 12], k2: [0.6, 0.8, 1, 1.2, 2, 4, 8, 20], k3: [0.8, 1, 1.2, 1.6, 2.5, 5, 10, 25],
    pot: [1.2, 1.5, 2, 2.5, 4, 8, 15, 40], makitra: [1.5, 2, 2.5, 3, 5, 10, 20, 50], kumanets: [2, 2.5, 3, 4, 6, 12, 25, 80],
    glek: [2.5, 4, 5, 6, 10, 20, 50, 200],
  };
  const W_BASE = { k1: 26, k2: 24, k3: 22, pot: 16, makitra: 12, kumanets: 9, glek: 6, furnace: 1.1 };
  const W_FS = { k1: 33, k2: 31, k3: 28, pot: 13, makitra: 10, kumanets: 7, glek: 5, furnace: 1 };
  const LADDER = [1, 2, 4, 8, 16, 32, 64, 128];
  const fmt = (v) => SK.fmt(v);
  const tierOf = (n) => (n >= 16 ? 7 : n >= 13 ? 6 : n >= 11 ? 5 : n >= 9 ? 4 : n - 5);

  // ---------- мок-математика (стенд; на сайті все рахує сервер) ----------
  const rk = (w) => SK.rnd.weighted(w);
  const randGrid = (w) => Array.from({ length: COLS }, () => Array.from({ length: ROWS }, () => rk(w)));
  function clusters(g) {
    const seen = new Set(), out = [];
    for (let c = 0; c < COLS; c++) for (let r = 0; r < ROWS; r++) {
      const k = g[c][r]; if (k === FURN || seen.has(c * ROWS + r)) continue;
      const cells = [], st = [[c, r]]; seen.add(c * ROWS + r);
      while (st.length) {
        const [x, y] = st.pop(); cells.push([x, y]);
        [[1, 0], [-1, 0], [0, 1], [0, -1]].forEach(([dx, dy]) => {
          const nx = x + dx, ny = y + dy, i = nx * ROWS + ny;
          if (nx < 0 || ny < 0 || nx >= COLS || ny >= ROWS || seen.has(i) || g[nx][ny] !== k) return;
          seen.add(i); st.push([nx, ny]);
        });
      }
      if (cells.length >= 5) out.push({ sym: k, cells: cells.sort((a, b) => a[0] - b[0] || a[1] - b[1]) });
    }
    return out;
  }
  function tumble(grid, tr, bet, w, steps) {
    let win = 0, n = 0;
    for (let guard = 0; guard < 60; guard++) {
      const cl = clusters(grid); if (!cl.length) break;
      const items = cl.map((k) => {
        let m = 0; k.cells.forEach(([c, r]) => { const v = tr[c * ROWS + r]; if (v >= 2) m += v; });
        m = m || 1;
        const pay = Math.max(1, Math.round(PAY[k.sym][tierOf(k.cells.length)] * bet));
        return { sym: k.sym, n: k.cells.length, cells: k.cells, pay, mult: m, amount: pay * m };
      });
      const amount = items.reduce((a, it) => a + it.amount, 0); win += amount; n++;
      steps.push({ t: 'win', items, amount, hold: 240 });
      const rem = new Set(), up = [], remove = [];
      items.forEach((it) => it.cells.forEach(([c, r]) => rem.add(c * ROWS + r)));
      Array.from(rem).sort((a, b) => a - b).forEach((i) => {
        const v = tr[i]; tr[i] = v <= 0 ? 1 : Math.min(128, v * 2);
        up.push([Math.floor(i / ROWS), i % ROWS, tr[i]]); remove.push([Math.floor(i / ROWS), i % ROWS]);
      });
      grid = grid.map((col, c) => {
        const keep = col.filter((k, r) => !rem.has(c * ROWS + r));
        return Array.from({ length: ROWS - keep.length }, () => rk(w)).concat(keep);
      });
      steps.push({ t: 'cascade', remove, grid, n, up });
    }
    return { grid, win };
  }
  const furnCells = (g) => { const a = []; g.forEach((col, c) => col.forEach((k, r) => { if (k === FURN) a.push([c, r]); })); return a; };
  function teaseFor(g, need) {
    const t = []; let n = 0;
    for (let c = 0; c < COLS; c++) { if (n === need - 1) t.push(c); n += g[c].filter((k) => k === FURN).length; if (n >= need) break; }
    return t;
  }
  function freeRound(bet, steps, o) {
    steps.push({ t: 'bonusIn', count: 10, title: o.bought ? 'Бонус куплено!' : 'Горно розпалилось!', sub: 'вільних обертів · сліди не стираються' });
    const tr = new Array(N).fill(0);
    let left = 10, total = 10, spins = 0, win = 0;
    while (left > 0) {
      left--; spins++; steps.push({ t: 'fs', left });
      const g = (o.force && o.force(spins)) || randGrid(W_FS);
      steps.push({ t: 'spin', grid: g, tease: total < 50 ? teaseFor(g, 3) : [] });
      const t = tumble(g, tr, bet, W_FS, steps); win += t.win;
      const fc = furnCells(t.grid);
      if (fc.length >= 3 && total < 50) { const add = Math.min(10, 50 - total); steps.push({ t: 'furn', cells: fc, fs: true }); steps.push({ t: 'fs', add }); left += add; total += add; }
    }
    steps.push({ t: 'bonusOut', total: win, spins, top: Math.max(...tr), title: 'Горно віддало' });
    return { win, spins };
  }
  function makeScript(bet, state, o) {
    o = o || {};
    const grid = randGrid(o.w || W_BASE); if (o.first) o.first(grid);
    const tease = teaseFor(grid, 3), steps = [{ t: 'spin', grid, tease }];
    const tr = new Array(N).fill(0);
    const t = tumble(grid, tr, bet, o.w || W_BASE, steps);
    let win = t.win, bonus = false;
    const fc = furnCells(t.grid);
    if (fc.length >= 3) { bonus = true; steps.push({ t: 'furn', cells: fc }); win += freeRound(bet, steps, o).win; }
    return { bet, steps, win, state: state || {}, bonus, meta: { bonus, top: Math.max(...tr) } };
  }
  function demoBy(pred, o, tries) {
    return (bet, state) => { let s = null; for (let i = 0; i < (tries || 3000); i++) { s = makeScript(bet, state, o); if (pred(s, s.win / bet)) return s; } return s; };
  }
  const RICH = { k1: 40, k2: 36, k3: 10, pot: 6, makitra: 5, kumanets: 4, glek: 3, furnace: 0.4 };

  const SAY = {
    idle: ['п\'ять однакових поруч — і дзень!', 'де розбилось — там слід лишиться', 'удруге на тому ж місці — ×2'],
    win: ['дзень!', 'розбилось — заплатили', 'ще наліплю, не шкодуй'],
    casc: ['ще падає!', 'полиця в слідах!', 'бий там само — множиться'],
    trace: ['слід множить!', 'на сліду — вдвічі', 'глина пам\'ятає'],
    big: ['оце погром!', 'уся полиця в золоті', 'гончар плаче, а ти радієш'],
    tease: ['ану ще одне горно…', 'не дихай…', 'жар близько…'],
  };

  // ---------- черепки для частинок (extras.shards з арту «Розбитих глеків») ----------
  const SHARD_IMG = {};
  function shardImgs(ctx, key) {
    if (SHARD_IMG[key]) return SHARD_IMG[key];
    let list = null;
    try { list = ctx.art && ctx.art.extras && ctx.art.extras.shards && ctx.art.extras.shards(key); } catch (e) { list = null; }
    SHARD_IMG[key] = (list || []).map((svg) => { const im = new Image(); im.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg); return im; });
    return SHARD_IMG[key];
  }
  SK.particles.slShard = SK.particles.slShard || function (g, p) {
    const im = p.color, s = p.s;
    if (!im || !im.complete || !im.naturalWidth) return;
    g.scale(1, Math.max(0.3, Math.abs(Math.cos(p.spin * 0.6))));
    g.drawImage(im, -s, -s, 2 * s, 2 * s);
  };

  // ---------- сліди на полиці ----------
  const S0 = 94;   // клітинка у px рамки (вікно 564×564 у viewBox 600)
  function traceHtml(ctx, v) {
    return ctx.extra('trace', v) || (v > 0 ? '<div class="sl-tph" data-v="' + v + '">' + (v > 1 ? '×' + v : '') + '</div>' : '');
  }
  // слід лежить ПІД посудом (видно, коли посуд розбився), а його число — ще й значком у куті клітинки поверх посуду
  function paintTrace(ctx, i, anim) {
    const sl = ctx.sl, el = sl.trEls && sl.trEls[i]; if (!el) return;
    const v = sl.tr[i];
    el.dataset.v = v;
    el.innerHTML = traceHtml(ctx, v);
    const b = sl.bdEls && sl.bdEls[i];
    if (b) { b.dataset.v = v; b.textContent = v >= 2 ? '×' + v : ''; }
    if (anim && v > 0) {
      el.classList.remove('stamp'); void el.offsetWidth; el.classList.add('stamp');
      if (b && v >= 2) { b.classList.remove('stamp'); void b.offsetWidth; b.classList.add('stamp'); }
    }
  }
  function clearTraces(ctx) {
    const sl = ctx.sl; sl.tr.fill(0);
    if (sl.trEls) sl.trEls.forEach((e) => { e.dataset.v = 0; e.innerHTML = ''; e.classList.remove('stamp', 'hot'); });
    if (sl.bdEls) sl.bdEls.forEach((e) => { e.dataset.v = 0; e.textContent = ''; e.classList.remove('stamp', 'hot'); });
    ladder(ctx);
  }
  function ladder(ctx) {
    const st = stageOf(ctx); if (!st) return;
    const top = Math.max(0, ...ctx.sl.tr);
    st.querySelectorAll('.sl-lad i').forEach((e) => e.classList.toggle('on', +e.dataset.v <= top && top > 0));
    const t = st.querySelector('.sl-top b'); if (t) t.textContent = top >= 2 ? '×' + top : top === 1 ? 'мокрий' : '—';
  }

  // ---------- купити бонус ----------
  function buyOn(ctx) { const m = SK.machines[ID]; return !!(m.buy && m.buy.price > 0 && ctx.api && ctx.api.buy); }
  function buyBtn(ctx) {
    const st = stageOf(ctx); if (!st) return;
    const b = st.querySelector('.sl-buy'); if (!b) return;
    const on = buyOn(ctx);
    b.hidden = !on;
    if (!on) return;
    const price = SK.machines[ID].buy.price * ctx.bet;
    b.querySelector('b').textContent = fmt(price) + ' 🏺';
    b.disabled = !!ctx.busy || ctx.sl.buying || ctx.balance < price;
  }
  async function buy(ctx) {
    if (!buyOn(ctx) || ctx.busy || ctx.sl.buying || !ctx.inst) return;
    const price = SK.machines[ID].buy.price * ctx.bet;
    if (ctx.balance < price) { ctx.say('черепків на бонус бракує', 2400); return; }
    const ok = await confirmBuy(ctx, price);
    if (!ok || ctx.busy) return;
    ctx.sl.buying = true; buyBtn(ctx);
    let script = null;
    try { script = await ctx.api.buy(ctx.bet); } catch (e) { script = null; }
    ctx.sl.buying = false;
    if (!script || !script.steps || !script.steps.length) { buyBtn(ctx); return; }
    ctx.addBalance(-(price - ctx.bet));   // кіт сам зніме ще ставку; після оберту баланс однаково прийде з сервера
    ctx.inst.play(script);
  }
  function confirmBuy(ctx, price) {
    return new Promise((res) => {
      // sk-modal: поки питаємо, пробіл і Ⓐ не крутять (кіт мовчить, коли відкрите модальне вікно)
      const ov = ctx.overlay('sl-ask sk-modal sk-noskip', '<div class="sl-ask-c"><div class="sl-ask-t">Купити бонус?</div>'
        + '<div class="sl-ask-s">10 вільних обертів одразу — сліди не стираються весь бонус</div>'
        + '<div class="sl-ask-p">' + fmt(price) + ' 🏺 <small>(' + SK.machines[ID].buy.price + '× ставки)</small></div>'
        + '<div class="sl-ask-b"><button class="sl-ask-no">ні</button><button class="sl-ask-yes">купити</button></div></div>');
      const done = (v) => { ctx.closeOverlay(ov); res(v); };
      ov.querySelector('.sl-ask-yes').addEventListener('click', (e) => { e.stopPropagation(); done(true); });
      ov.querySelector('.sl-ask-no').addEventListener('click', (e) => { e.stopPropagation(); done(false); });
      ov.addEventListener('click', (e) => { if (e.target === ov) done(false); });
    });
  }

  // ---------- DOM ----------
  const stageOf = (ctx) => ctx.area.querySelector('.sl-stage');
  function setCasc(ctx, n) {
    const st = stageOf(ctx); if (!st) return;
    const p = st.querySelector('.sl-casc'); if (!p) return;
    p.querySelector('b').textContent = n;
    p.classList.toggle('on', n > 0); p.classList.toggle('hot', n >= 3);
  }
  function hintsHtml(ctx) {
    return '<div class="sl-hints">'
      + '<div class="sl-hint"><span class="sl-hint-n">5+</span><span>однакових <b>поруч</b> (боками) — виграш, посуд б\'ється</span></div>'
      + '<div class="sl-hint"><span class="sl-hint-i">' + traceHtml(ctx, 2) + '</span><span>де розбилось — <b>слід</b>; удруге там же ×2, далі ×4 … ×128</span></div>'
      + '<div class="sl-hint"><span class="sl-hint-i sk-cell">' + ctx.symHtml(FURN) + '</span><span><b>3 горна</b> — 10 вільних, сліди не стираються</span></div>'
      + '</div>';
  }
  function ladderHtml(ctx) {
    return '<div class="sl-lad">' + LADDER.map((v) => '<i data-v="' + v + '">' + traceHtml(ctx, v) + '</i>').join('') + '</div>';
  }
  function payRows() {
    return PAYING.slice().reverse().map((k) => {
      const pays = {}, labels = {};
      SIZES.forEach((n, t) => { pays[n] = PAY[k][t]; labels[n] = LABELS[t]; });
      return { key: k, unit: '+', pays, labels, note: k === 'glek' ? 'найдорожчий' : '' };
    }).concat([{ key: FURN, pays: {}, note: '3+ будь-де після каскаду — 10 вільних обертів; у вільних 3+ — ще 10 (разом до 50)' }]);
  }

  SK.define({
    id: ID,
    title: 'Сліди на полиці',
    grid: { cols: COLS, rows: ROWS },
    spinStyle: 'drop',
    payUnit: 1,
    sounds: { win: 'bell' },
    paytable: payRows(),
    _setPay(pay) {
      if (!pay) return;
      PAYING.forEach((k) => { const p = pay[k]; if (p && p['5'] != null) PAY[k] = SIZES.map((n) => +p[String(n)]); });
      SK.machines[ID].paytable = payRows();
    },
    rules: '<b>6×6</b>, ліній нема: платить <b>кластер</b> — 5 і більше однакових, що стикаються боками (не навскіс). '
      + 'Виграшний посуд б\'ється, решта падає, згори — нові (<b>каскад</b>), поки є виграш. '
      + 'Де посуд розбився, на полиці лишається <b>слід</b>: перший — мокре коло (ще не множить), удруге на тому ж місці — <b>×2</b>, '
      + 'далі ×4, ×8 … до <b>×128</b>. Виграш кластера множиться на <b>суму</b> слідів ×2+ під ним. У звичайному оберті сліди '
      + 'живуть до кінця його каскаду. <b>Горно</b>: 3+ на полиці — <b>10 вільних обертів</b>, і сліди не стираються весь бонус; '
      + 'у вільних 3+ горна — ще 10 (разом до 50). Стеля — 5000× ставки за оберт. Волатильність висока: часто дрібно, '
      + 'а як полиця вкриється золотими слідами — тримайся.',
    initialState: () => ({}),
    spin(bet, state) { return makeScript(bet, state); },
    demo: {
      'Малий виграш': demoBy((s, x) => !s.bonus && x > 0 && x < 2),
      'Каскад зі слідами': demoBy((s) => !s.bonus && s.steps.some((x) => x.t === 'win' && x.items.some((it) => it.mult > 1)), { w: RICH }),
      'Очікування горна': demoBy((s) => s.steps[0].tease.length > 0 && !s.bonus, { first: (g) => { g[0][2] = FURN; g[1][4] = FURN; } }),
      'Вхід у бонус': demoBy((s) => s.bonus, { first: (g) => { g[0][1] = FURN; g[2][3] = FURN; g[4][0] = FURN; } }, 200),
      'Куплений бонус': (bet, state) => { const steps = []; const b = freeRound(bet, steps, { bought: true }); return { bet, steps, win: b.win, state: state || {}, bonus: true, bought: true }; },
    },

    mounted(ctx, inst) { ctx.inst = inst; },

    build(ctx) {
      const sl = ctx.sl = ctx.sl || { tr: new Array(N).fill(0), casc: 0, greeted: false, buying: false };
      const port = ctx.orient === 'port';
      ctx.root.classList.add('slot-slot-cascade');   // анімації посуду й сцени — з арту «Розбитих глеків»
      const k = port ? Math.min((ctx.areaW) / 600, (ctx.areaH - 200) / 600) : Math.min((ctx.areaH - 16) / 600, (ctx.areaW - 2 * 300 - 40) / 600);
      const st = document.createElement('div'); st.className = 'sl-stage ' + (port ? 'sl-port' : 'sl-land');
      ctx.area.appendChild(st);
      const frame = ctx.extra('frame');
      const frameHtml = '<div class="sl-frame-box" style="width:' + (600 * k).toFixed(1) + 'px;height:' + (600 * k).toFixed(1) + 'px">'
        + '<div class="sl-frame-in" style="transform:scale(' + k.toFixed(4) + ')">'
        + (frame ? '<div class="sl-frame-bg">' + frame + '</div>' : '<div class="sl-frame-bg sl-frame-ph"></div>')
        + '<div class="sl-field"><div class="sl-traces"></div><div class="sl-gridhost"></div><div class="sl-badges"></div></div></div></div>';
      const casc = '<div class="sl-casc"><small>каскад</small><b>0</b></div>';
      const top = '<div class="sl-top"><small>найбільший слід</small><b>—</b></div>';
      const buyB = '<button class="sl-buy sk-noskip" hidden><span>купити бонус</span><b></b></button>';
      if (port) {
        st.innerHTML = '<div class="sl-prow"><div class="sl-logo">' + ctx.logoHtml() + '</div><div class="sl-pinfo">' + top + casc + '</div></div>'
          + frameHtml
          + '<div class="sl-prow sl-pbot">' + ladderHtml(ctx) + buyB + '</div>';
      } else {
        st.innerHTML = '<div class="sl-side sl-left"><div class="sl-logo">' + ctx.logoHtml() + '</div>' + hintsHtml(ctx) + '</div>'
          + frameHtml
          + '<div class="sl-side sl-right"><div class="sl-ladbox"><div class="sl-lad-t">сліди на полиці</div>' + ladderHtml(ctx)
          + '<div class="sl-lad-s"><span class="sl-l-base">живуть до кінця оберту</span><span class="sl-l-fs">не стираються весь бонус</span></div></div>'
          + top + casc + buyB + '</div>';
      }
      const tl = st.querySelector('.sl-traces'), bl = st.querySelector('.sl-badges');
      sl.trEls = []; sl.bdEls = [];
      for (let i = 0; i < N; i++) {
        const c = Math.floor(i / ROWS), r = i % ROWS, box = 'left:' + c * S0 + 'px;top:' + r * S0 + 'px;width:' + S0 + 'px;height:' + S0 + 'px';
        const e = document.createElement('div'); e.className = 'sl-tr'; e.style.cssText = box; tl.appendChild(e); sl.trEls.push(e);
        const b = document.createElement('div'); b.className = 'sl-bd'; b.style.cssText = box; bl.appendChild(b); sl.bdEls.push(b);
        paintTrace(ctx, i, false);
      }
      ctx.makeReels(st.querySelector('.sl-gridhost'), { cols: COLS, rows: ROWS, size: S0, gap: 0, pool: PAYING.concat([FURN]),
        weights: W_BASE, initial: ctx.keepGrid ? undefined : { grid: randGrid(W_BASE) } });
      const bb = st.querySelector('.sl-buy');
      bb.addEventListener('click', (e) => { e.stopPropagation(); buy(ctx); });
      bb.addEventListener('pointerdown', (e) => e.stopPropagation());
      setCasc(ctx, sl.casc); ladder(ctx); buyBtn(ctx);
      PAYING.concat([FURN]).forEach((key) => shardImgs(ctx, key));
      if (!sl.greeted) { sl.greeted = true; ctx.timeout(() => ctx.say(SK.rnd.pick(SAY.idle), 4000), 700); }
    },

    steps: {
      async spin(s, ctx) {
        ctx.sl.casc = 0; setCasc(ctx, 0);
        if (!ctx.fs) clearTraces(ctx);   // у базі сліди живуть лише в межах оберту
        await ctx.reels.spin(s);
        const st = stageOf(ctx); if (st) st.classList.remove('sl-tease');
      },
      // кластер на слідах: сліди під ним спалахують, «×M» над ним — далі звичайний підрахунок кіта
      async win(s, ctx) {
        const hot = (s.items || []).filter((it) => it.mult > 1);
        if (hot.length) {
          const st = stageOf(ctx);
          hot.forEach((it) => {
            it.cells.forEach(([c, r]) => { const i = c * ROWS + r; if (ctx.sl.tr[i] >= 2) { ctx.sl.trEls[i].classList.add('hot'); ctx.sl.bdEls[i].classList.add('hot'); } });
            const mid = it.cells[Math.floor(it.cells.length / 2)], cell = ctx.cell(mid[0], mid[1]);
            if (st && cell) {
              const [x, y] = ctx.rel(cell), p = document.createElement('div');
              p.className = 'sl-xpop' + (it.mult >= 32 ? ' gold' : ''); p.textContent = '×' + fmt(it.mult);
              p.style.left = x.toFixed(1) + 'px'; p.style.top = y.toFixed(1) + 'px';
              st.appendChild(p); ctx.timeout(() => p.remove(), 1900);
            }
          });
          ctx.sound('level'); ctx.say(SK.rnd.pick(SAY.trace), 2200);
          await ctx.wait(520);
        }
        await SK.steps.win(s, ctx);
        ctx.sl.trEls.forEach((e) => e.classList.remove('hot')); ctx.sl.bdEls.forEach((e) => e.classList.remove('hot'));
      },
      async cascade(s, ctx) {
        await SK.steps.cascade(s, ctx);
        ctx.sl.casc = s.n || ctx.sl.casc + 1; setCasc(ctx, ctx.sl.casc);
        if (ctx.sl.casc === 3) ctx.say(SK.rnd.pick(SAY.casc), 2200);
      },
      // горна: світяться й жевріють; вхід у бонус чи ще +10
      async furn(s, ctx) {
        ctx.clearWin();
        ctx.showWin([{ cells: s.cells, amount: 0 }], true);
        s.cells.forEach(([c, r]) => { const e = ctx.cell(c, r); if (e) ctx.fx.at(e, { kind: 'spark', n: 22, speed: 380, size: 10, angle: -Math.PI / 2, spread: 1.4, gravity: -40, color: '#ffb347', life: 1.3 }); });
        ctx.sound('bonus');
        ctx.say(s.fs ? 'ще дровець — +10 обертів!' : 'горно розгоряється!', 2500);
        await ctx.wait(s.fs ? 900 : 1300);
        ctx.clearWin();
      },
      async bonusIn(s, ctx) {
        clearTraces(ctx);
        ctx.clearWin();
        const bought = !!(ctx.script && ctx.script.bought);
        await SK.steps.bonusIn(Object.assign({ title: bought ? 'Бонус куплено!' : 'Горно розпалилось!', sub: 'вільних обертів · сліди не стираються' }, s), ctx);
      },
      async bonusOut(s, ctx) {
        await SK.steps.bonusOut(Object.assign({ title: 'Горно віддало' }, s), ctx);
        clearTraces(ctx);
      },
    },

    // тріск: посуд розлітається черепками, на його місці — слід (step.up — нові значення слідів)
    async onRemove(els, ctx, step) {
      ctx.sound('crack');
      els.forEach((e) => { e.classList.remove('dim'); e.classList.add('sl-cracking'); });
      await ctx.wait(ctx.turbo ? 120 : 220);
      const rr = ctx.root.getBoundingClientRect();
      els.forEach((e) => {
        const r = e.getBoundingClientRect(), cx = r.left - rr.left + r.width / 2, cy = r.top - rr.top + r.height / 2, W = r.width;
        const imgs = shardImgs(ctx, e._key).filter((im) => im.complete && im.naturalWidth);
        const n = Math.min(4, imgs.length || 3);
        for (let j = 0; j < n; j++) {
          const a = -Math.PI / 2 + ((j + 0.5) / n - 0.5) * 3.2 + (Math.random() - 0.5) * 0.4;
          ctx.fx.burst(cx, cy, imgs.length
            ? { kind: 'slShard', n: 1, angle: a, spread: 0.35, speed: W * 5.6, size: W * 0.18, gravity: W * 16, color: imgs[j], life: 1.5 }
            : { kind: 'shard', n: 1, angle: a, spread: 0.35, speed: W * 5.6, size: W * 0.12, gravity: W * 16, life: 1.5 });
        }
        e.classList.add('sl-gone');
      });
      (step && step.up || []).forEach(([c, r, v]) => { ctx.sl.tr[c * ROWS + r] = v; paintTrace(ctx, c * ROWS + r, true); });
      ladder(ctx);
      if ((step && step.up || []).some((u) => u[2] >= 2)) ctx.sound('coin');
      await ctx.wait(ctx.turbo ? 90 : 180);
    },

    onSpinStart(ctx) { const st = stageOf(ctx); if (st) st.classList.remove('sl-big', 'sl-tease'); buyBtn(ctx); },
    onSpinEnd(ctx) { buyBtn(ctx); },
    onBet(ctx) { buyBtn(ctx); },
    onTease(ctx) {
      const st = stageOf(ctx); if (st) st.classList.add('sl-tease');
      ctx.reels.cells().forEach((e) => { if (e && e._key === FURN) e.classList.add('win', 'sl-wait'); });
      ctx.say(SK.rnd.pick(SAY.tease), 2000);
    },
    onReelStop(ctx, c) {
      if (c === COLS - 1) {
        const st = stageOf(ctx); if (st) st.classList.remove('sl-tease');
        ctx.reels.cells().forEach((e) => { if (e && e.classList.contains('sl-wait')) e.classList.remove('win', 'sl-wait'); });
      }
    },
    onWin(ctx, step) {
      if (ctx.sl.casc >= 2 || (step.items || []).some((it) => it.mult > 1)) return;
      ctx.say(SK.rnd.pick(step.amount >= 10 * ctx.bet ? SAY.big : SAY.win), 2600);
    },
    onBigwin(ctx) { const st = stageOf(ctx); if (st) st.classList.add('sl-big'); ctx.say(SK.rnd.pick(SAY.big), 4000); },
  });
})();
