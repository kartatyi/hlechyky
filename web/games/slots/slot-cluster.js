/* Цвіт папороті (slot-cluster): поле 7×7 у вінку, кластери від 5 сусідніх (гор./верт.), каскад, шкала папороті з чарами:
   1) світлячки — 3–6 диких; 2) русалка — один вид квітів стає іншим; 3) цвіт папороті — дикий 3×3, вибух, 5 вільних
   обертів (шкала не скидається). Механіка, мок-математика і сценарії показу — тут; арт — slot-cluster-art.js. */
(function () {
  'use strict';
  const SK = window.SlotKit, ID = 'slot-cluster';
  const C = 7, R = 7;
  const LOW = ['f1', 'f2', 'f3', 'f4'], HIGH = ['wreath', 'candle', 'fire', 'comb'], PAYS = LOW.concat(HIGH);
  const WILD = 'fern', BLOOM = 'bloom';
  // ваги падіння символів (звичайна гра) — як на сервері (SlotClusterMath.Weights); на сайті _tune бере їх з view.table
  const W8 = { f1: 22, f2: 19, f3: 17, f4: 14, wreath: 10, candle: 9, fire: 7, comb: 5, fern: 1.35 };
  let FS_FERN = 2.2;                 // у вільних листків папороті більше
  // розміри кластера → сходинка виплати; виплати — у ставках
  const SIZES = [5, 6, 7, 8, 9, 11, 13, 16];
  const PAY = {   // spec §1.2 («98 %», 09.10.2026); на сайті — з view.table.pay (_setPay)
    f1: [0.2, 0.2, 0.3, 0.3, 0.4, 0.7, 1.2, 3],
    f2: [0.2, 0.2, 0.3, 0.4, 0.5, 1, 2, 5],
    f3: [0.2, 0.3, 0.4, 0.5, 0.7, 1.2, 2.3, 6],
    f4: [0.3, 0.3, 0.4, 0.6, 0.8, 1.5, 2.6, 8],
    wreath: [0.5, 0.6, 0.8, 1, 1.5, 2.3, 5, 12],
    candle: [0.6, 0.8, 1, 1.5, 2, 2.6, 6, 20],
    fire: [0.8, 1, 1.5, 2, 2.5, 4.5, 10, 30],
    comb: [1, 1.5, 2, 3, 4.5, 8, 20, 60],
  };
  // крапельки шкали; рівні — бутони шкали. Сервер (spec §2): RTP бази 97 %, виграш 47 % обертів, цвіт ≈ 1 з 72;
  // мок стенду — приблизно те саме (гроші рахує сервер). На сайті — з view.table.levels (_tune)
  const LV = [11, 30, 52]; let MAX = 52;
  // перелесник (сюрприз): на кожному полі (і у вільних) з шансом 1 з 12 летить від краю до краю й лишає 2–4 листки
  const PERE = { chance: 1 / 12, min: 2, max: 4, flight: 7 };
  const FS_N = 5, FS_ADD = 3, FS_CAP = 15;
  const NAMES = ['Світлячки', 'Русалка', 'Цвіт папороті'];
  const SUBS = ['світлячки роблять дикі', 'русалка міняє квіти', 'дикий цвіт 3×3 і 5 вільних'];
  const BLOOM_AREA = []; for (let c = 2; c <= 4; c++) for (let r = 2; r <= 4; r++) BLOOM_AREA.push([c, r]);
  const BOOM_AREA = []; for (let c = 1; c <= 5; c++) for (let r = 1; r <= 5; r++) BOOM_AREA.push([c, r]);

  let serverRules = '';   // view.table.rules (готовий HTML для ⓘ) — на сайті; на стенді — свій текст з тих самих чисел

  // ---------- Мок-математика ----------
  const rnd = Math.random;
  function picker(w) {
    const ks = Object.keys(w), tot = ks.reduce((a, k) => a + w[k], 0);
    return () => { let x = rnd() * tot; for (const k of ks) { x -= w[k]; if (x < 0) return k; } return ks[ks.length - 1]; };
  }
  const copy = (g) => g.map((col) => col.slice());
  const isW = (k) => k === WILD || k === BLOOM;
  function payOf(sym, n, bet) {
    let i = -1; for (let j = 0; j < SIZES.length; j++) if (n >= SIZES[j]) i = j;
    return i < 0 ? 0 : Math.max(1, Math.round(PAY[sym][i] * bet));
  }
  // кластери: для кожного символу — зв'язні області «символ або дикий», від 5
  function clusters(g) {
    const out = [];
    for (const s of PAYS) {
      const seen = new Uint8Array(C * R);
      for (let c = 0; c < C; c++) for (let r = 0; r < R; r++) {
        if (g[c][r] !== s || seen[c * R + r]) continue;
        const vis = new Uint8Array(C * R), comp = [], q = [[c, r]]; vis[c * R + r] = 1;
        while (q.length) {
          const [x, y] = q.pop(); comp.push([x, y]); if (g[x][y] === s) seen[x * R + y] = 1;
          for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
            const nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= C || ny >= R || vis[nx * R + ny]) continue;
            const k = g[nx][ny]; if (k === s || isW(k)) { vis[nx * R + ny] = 1; q.push([nx, ny]); }
          }
        }
        if (comp.length >= 5) out.push({ sym: s, cells: comp });
      }
    }
    return out;
  }
  const winOf = (g, bet) => clusters(g).reduce((a, k) => a + payOf(k.sym, k.cells.length, bet), 0);
  function fall(g, rem, pick) {
    const rs = new Set(rem.map(([c, r]) => c * R + r));
    return g.map((col, c) => {
      const keep = col.filter((k, r) => !rs.has(c * R + r));
      return Array.from({ length: R - keep.length }, pick).concat(keep);
    });
  }
  function unionCells(list) {
    const m = new Map(); list.forEach((cells) => cells.forEach(([c, r]) => m.set(c * R + r, [c, r]))); return Array.from(m.values());
  }
  function shuffle(a) { for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(rnd() * (i + 1)); [a[i], a[j]] = [a[j], a[i]]; } return a; }

  // світлячки: 3–6 клітинок стають дикими (з кількох спроб частіше беремо кращу — чари мають тішити)
  function planFlies(g, bet) {
    const free = []; for (let c = 0; c < C; c++) for (let r = 0; r < R; r++) if (!isW(g[c][r])) free.push([c, r]);
    const n = 3 + Math.floor(rnd() * 4);
    let best = null, bw = -1;
    for (let t = 0; t < 6; t++) {
      const set = shuffle(free.slice()).slice(0, n), h = copy(g);
      set.forEach(([c, r]) => { h[c][r] = WILD; });
      const w = winOf(h, bet); if (w > bw) { bw = w; best = set; }
    }
    if (rnd() < 0.35) best = shuffle(free.slice()).slice(0, n);
    return best.map(([c, r]) => [c, r, WILD]);
  }
  // русалка: один вид квітів → інший (частіше той, що дає найбільші кластери)
  function planMermaid(g, bet) {
    const opts = [];
    for (const a of LOW) for (const b of LOW) {
      if (a === b) continue;
      const cells = []; for (let c = 0; c < C; c++) for (let r = 0; r < R; r++) if (g[c][r] === a) cells.push([c, r, b]);
      if (!cells.length) continue;
      const h = copy(g); cells.forEach(([c, r]) => { h[c][r] = b; });
      opts.push({ from: a, to: b, cells, w: winOf(h, bet) });
    }
    if (!opts.length) return null;
    opts.sort((x, y) => y.w - x.w);
    return rnd() < 0.7 ? opts[0] : opts[Math.floor(rnd() * opts.length)];
  }

  // перелесник: ламана від краю до протилежного краю (по клітинці на колонку чи рядок, щокроку вбік на 0/±1),
  // 2–4 листки на недикі клітинки шляху в порядку польоту — як SlotClusterMath.Perelesnyk
  function planPere(g) {
    let path = [], free = [];
    for (let t = 0; ; t++) {
      const dir = Math.floor(rnd() * 4); let side = Math.floor(rnd() * 7);
      path = []; free = [];
      for (let i = 0; i < PERE.flight; i++) {
        if (i > 0) side = Math.max(0, Math.min(6, side + Math.floor(rnd() * 3) - 1));
        const along = dir % 2 === 0 ? i : PERE.flight - 1 - i;
        const p = dir < 2 ? [along, side] : [side, along];
        path.push(p); if (!isW(g[p[0]][p[1]])) free.push(i);
      }
      if (free.length >= PERE.min || t >= 9) break;
    }
    if (!free.length) return null;
    const n = PERE.min + Math.floor(rnd() * (PERE.max - PERE.min + 1));
    const at = shuffle(free.slice()).slice(0, n).sort((a, b) => a - b);
    return { t: 'morph', why: 'perelesnyk', cells: at.map((i) => [path[i][0], path[i][1], WILD]), path };
  }
  function pereMaybe(S) {
    if (rnd() >= PERE.chance) return;
    const st = planPere(S.grid); if (!st) return;
    st.cells.forEach(([c, r, k]) => { S.grid[c][r] = k; });
    S.steps.push(st); S.info.pere = (S.info.pere || 0) + 1;
    if (S.fs) S.info.pereFs = (S.info.pereFs || 0) + 1;
  }

  // Розіграш поля до кінця: каскади, рівні шкали. S: {grid, meter, done, steps, pick, bet, fs, info}
  function resolve(S) {
    let won = 0, chain = 0, boom = false;
    for (let guard = 0; guard < 80; guard++) {
      const cl = clusters(S.grid);
      if (cl.length || boom) {
        let rem = [];
        if (cl.length) {
          const items = cl.map((k) => ({ cells: k.cells, sym: k.sym, n: k.cells.length, amount: payOf(k.sym, k.cells.length, S.bet) }));
          const amount = items.reduce((a, it) => a + it.amount, 0);
          chain++; won += amount;
          S.steps.push({ t: 'win', items, amount, hold: 250 });
          rem = unionCells(items.map((it) => it.cells));
          S.info.maxCl = Math.max(S.info.maxCl, ...items.map((it) => it.n));
        }
        if (boom) rem = unionCells([rem, BOOM_AREA]);
        const before = S.meter;
        S.meter = Math.min(MAX, S.meter + (boom ? 0 : rem.length));
        S.grid = fall(S.grid, rem, S.pick);
        S.steps.push({ t: 'cascade', remove: rem, grid: copy(S.grid), n: chain, fern: S.meter, from: before, boom: boom || undefined });
        if (boom) {
          boom = false;
          if (S.fs) {             // у вільних шкала доходить до цвіту — +3 оберти і знову з нуля
            const add = Math.min(FS_ADD, FS_CAP - S.fsTotal);
            if (add > 0) { S.fsTotal += add; S.fsLeft += add; S.steps.push({ t: 'fs', add }); }
            S.meter = 0; S.done = [false, false, false];
            S.steps.push({ t: 'fern', to: 0 });
          }
        }
        continue;
      }
      // виграшів нема — чи досягнуто рівня шкали?
      const L = [0, 1, 2].find((i) => !S.done[i] && S.meter >= LV[i]);
      if (L == null) break;
      S.done[L] = true; S.info.lv[L]++;
      if (L === 0) {
        const cells = planFlies(S.grid, S.bet);
        cells.forEach(([c, r, k]) => { S.grid[c][r] = k; });
        S.steps.push({ t: 'lvl', n: 1, cells });
      } else if (L === 1) {
        const p = planMermaid(S.grid, S.bet);
        if (!p) continue;
        p.cells.forEach(([c, r, k]) => { S.grid[c][r] = k; });
        S.steps.push({ t: 'lvl', n: 2, from: p.from, to: p.to, cells: p.cells });
      } else {
        const cells = BLOOM_AREA.map(([c, r]) => [c, r, BLOOM]);
        cells.forEach(([c, r, k]) => { S.grid[c][r] = k; });
        S.steps.push({ t: 'lvl', n: 3, cells });
        boom = true; S.bloom = true;
      }
    }
    S.info.chain = Math.max(S.info.chain, chain);
    return won;
  }

  function simulate(bet, state, w) {
    const pick = picker(w || W8);
    const fresh = () => Array.from({ length: C }, () => Array.from({ length: R }, pick));
    const info = { chain: 0, lv: [0, 0, 0], maxCl: 0, bonus: false };
    const S = { grid: fresh(), meter: 0, done: [false, false, false], steps: [], pick, bet, fs: false, info };
    S.steps.push({ t: 'spin', grid: copy(S.grid) });
    pereMaybe(S);
    let total = resolve(S);
    if (S.bloom) {
      info.bonus = true;
      S.steps.push({ t: 'bonusIn', count: FS_N, title: 'Цвіт папороті', sub: 'вільних обертів · шкала не скидається' });
      S.steps.push({ t: 'fern', to: 0 });
      Object.assign(S, { fs: true, fsLeft: FS_N, fsTotal: FS_N, meter: 0, done: [false, false, false] });
      let fsWon = 0;
      const wf = Object.assign({}, w || W8); wf.fern *= FS_FERN; const pf = picker(wf);
      S.pick = pf;
      while (S.fsLeft > 0) {
        S.fsLeft--;
        S.steps.push({ t: 'fs', left: S.fsLeft });
        S.grid = Array.from({ length: C }, () => Array.from({ length: R }, pf)); S.bloom = false;
        S.steps.push({ t: 'spin', grid: copy(S.grid) });
        pereMaybe(S);
        fsWon += resolve(S);
      }
      S.steps.push({ t: 'bonusOut', total: fsWon, title: 'Цвіт папороті приніс' });
      info.fsWon = fsWon; total += fsWon;
    }
    S.steps.push({ t: 'fern', to: 0, end: true });
    return { bet, steps: S.steps, win: total, state: state || {}, info };
  }

  // Сценарії показу: розігруємо, доки не випаде потрібне (за потреби — на «щедрих» вагах)
  const RICH = { f1: 30, f2: 26, f3: 12, f4: 10, wreath: 7, candle: 6, fire: 5, comb: 4, fern: 2.2 };
  const HOT = { f1: 14, f2: 12, f3: 10, f4: 9, wreath: 12, candle: 11, fire: 10, comb: 9, fern: 3 };
  const HOT2 = { f1: 7, f2: 7, f3: 7, f4: 7, wreath: 13, candle: 13, fire: 14, comb: 14, fern: 4 };
  function demoBy(pred, w, score, tries) {
    return (bet, state) => {
      let best = null, bs = -Infinity;
      for (let i = 0; i < (tries || 2500); i++) {
        const s = simulate(bet, state, i < 600 ? W8 : w || W8);
        if (pred(s.info, s.win / bet)) return s;
        const sc = score ? score(s.info, s.win / bet) : s.win;
        if (sc > bs) { bs = sc; best = s; }
      }
      return best;
    };
  }
  function sim(n, bet) {   // для перевірки: SlotKit.machines['slot-cluster']._sim(20000)
    bet = bet || 100; let tot = 0, hit = 0, lv = [0, 0, 0], big = 0, pere = 0; const t0 = performance.now();
    for (let i = 0; i < n; i++) { const s = simulate(bet); tot += s.win; if (s.win) hit++; s.info.lv.forEach((v, j) => { if (v) lv[j]++; }); if (s.win >= 10 * bet) big++; if (s.info.pere) pere++; }
    return { rtp: +(tot / n / bet).toFixed(3), hit: +(hit / n).toFixed(3), lv1: +(lv[0] / n).toFixed(4), lv2: +(lv[1] / n).toFixed(4), lv3: +(lv[2] / n).toFixed(4), big: +(big / n).toFixed(4), pere: +(pere / n).toFixed(4), ms: Math.round(performance.now() - t0) };
  }

  // ---------- Звук ----------
  SK.sound.add('scl-drop', (h, o) => { const p = o.p || 0; h.tone(520 + p * 900, h.t, 0.12, 'sine', 0.16, 900 + p * 1300); h.tone(1800 + p * 1200, h.t + 0.03, 0.08, 'triangle', 0.05); });
  SK.sound.add('scl-fly', (h) => { h.tone(1400, h.t, 0.35, 'sine', 0.06, 2600); h.tone(2100, h.t + 0.08, 0.3, 'sine', 0.04, 3200); });
  SK.sound.add('scl-splash', (h) => { h.noise(h.t, 0.5, 900, 0.6, 0.35); h.noise(h.t + 0.1, 0.4, 3000, 0.7, 0.15); h.tone(300, h.t, 0.3, 'sine', 0.15, 120); });
  SK.sound.add('scl-boom', (h) => { h.noise(h.t, 0.7, 500, 0.5, 0.55); h.tone(110, h.t, 0.6, 'sine', 0.5, 40); [784, 988, 1175, 1568].forEach((f, i) => h.tone(f, h.t + 0.15 + i * 0.07, 0.6, 'triangle', 0.08)); });
  SK.sound.add('scl-ember', (h) => { h.noise(h.t, 0.22, 2600, 0.9, 0.16, 'highpass'); h.tone(660, h.t, 0.25, 'triangle', 0.1, 1320); h.tone(1980, h.t + 0.05, 0.16, 'sine', 0.05, 2640); });
  SK.sound.add('scl-puff', (h) => { h.noise(h.t, 0.18, 4200, 0.8, 0.12, 'highpass'); h.tone(1568, h.t, 0.18, 'sine', 0.06, 2093); });

  // ---------- Частинки: золота зірочка художника ----------
  let sparkImg = null;
  function sparkImage(ctx) {
    if (sparkImg) return sparkImg;
    let s = ctx.extra('spark'); if (!s) return null;
    if (!/xmlns=/.test(s)) s = s.replace('<svg ', '<svg xmlns="http://www.w3.org/2000/svg" ');
    sparkImg = new Image(); sparkImg.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(s);
    return sparkImg;
  }
  SK.particles.sclspark = function (g, p) {
    g.globalCompositeOperation = 'lighter';
    if (sparkImg && sparkImg.complete && sparkImg.naturalWidth) g.drawImage(sparkImg, -p.s, -p.s, p.s * 2, p.s * 2);
    else { g.fillStyle = '#ffe58a'; g.beginPath(); g.arc(0, 0, p.s * 0.5, 0, 7); g.fill(); }
  };

  // ---------- Розкладка ----------
  // широко: зона 1280×646 — ліворуч лого й рівні, посередині поле у вінку, праворуч шкала;
  // телефон: 420×628 — лого, шкала лежачи, під нею поле (клітинка 50 → ~43 px на 360-px екрані)
  function layOf(ctx) {
    if (ctx.orient === 'port') {
      const S = 50, g = 3, F = 7 * S + 6 * g;
      return { port: true, S, g, F, fx: (420 - F) / 2, fy: 628 - F - 26, mw: 300, mx: 60, my: 74 };
    }
    const S = 74, g = 4, F = 7 * S + 6 * g;
    return { port: false, S, g, F, fx: 412, fy: (646 - F) / 2, mh: 560, mx: 1000, my: 43 };
  }
  const U = [1 / 3, 2 / 3, 1];
  const budY = (u) => 300 - u * 262;   // y бутона у viewBox шкали (0 0 90 320)
  // крапельки → частка шкали: бутони арту стоять на 1/3, 2/3, 1 довжини, а рівні — на LV (11 · 30 · 52), тож шкала
  // ламана — кожен бутон розкривається рівно тоді, коли рівень доступний
  function meterU(v) {
    v = Math.max(0, Math.min(MAX, v));
    let a = 0, ua = 0;
    for (let i = 0; i < 3; i++) { if (v <= LV[i]) return ua + (U[i] - ua) * (v - a) / ((LV[i] - a) || 1); a = LV[i]; ua = U[i]; }
    return 1;
  }

  function iconOf(ctx, n) {
    if (n === 1) return ctx.extra('firefly') || '✨';
    return ctx.symHtml(n === 2 ? 'comb' : BLOOM);
  }

  function build(ctx) {
    const L = layOf(ctx), cl = ctx.cl = ctx.cl || { fern: 0 };
    const st = document.createElement('div'); st.className = 'scl-stage' + (L.port ? ' port' : ''); ctx.area.appendChild(st);
    cl.st = st;
    const meter = ctx.extra('meter', meterU(cl.fern)) || '<div class="scl-meter-ph"></div>';
    const legend = [1, 2, 3].map((n) => '<div class="scl-lv" data-n="' + n + '"><i>' + iconOf(ctx, n) + '</i><span><b>' + NAMES[n - 1] + '</b><small>' + SUBS[n - 1] + '</small></span></div>').join('');
    let h = '';
    if (L.port) {
      h += '<div class="scl-logo">' + ctx.logoHtml() + '</div>';
      const k = L.mw / 320;
      h += '<div class="scl-mwrap" style="left:' + L.mx + 'px;top:' + L.my + 'px;width:' + L.mw + 'px;height:' + (90 * k).toFixed(1) + 'px">'
        + '<div class="scl-mrot" style="width:' + (90 * k).toFixed(1) + 'px;height:' + L.mw + 'px">' + meter + '</div></div>';
      h += U.map((u, i) => '<div class="scl-mlab" data-n="' + (i + 1) + '" style="left:' + (L.mx + (320 - budY(u)) * k).toFixed(0) + 'px;top:' + (L.my + 90 * k + 2).toFixed(0) + 'px">' + ['світлячки', 'русалка', 'цвіт'][i] + '</div>').join('');
    } else {
      h += '<div class="scl-side"><div class="scl-logo">' + ctx.logoHtml() + '</div><div class="scl-legend">' + legend + '</div><div class="scl-chain"></div></div>';
      const k = L.mh / 320;
      h += '<div class="scl-mwrap" style="left:' + L.mx + 'px;top:' + L.my + 'px;width:' + (90 * k).toFixed(1) + 'px;height:' + L.mh + 'px">' + meter + '</div>';
      h += U.map((u, i) => '<div class="scl-mlab land" data-n="' + (i + 1) + '" style="left:' + (L.mx + 90 * k + 6).toFixed(0) + 'px;top:' + (L.my + budY(u) * k).toFixed(0) + 'px">' + NAMES[i] + '</div>').join('');
    }
    h += '<div class="scl-board" style="left:' + L.fx + 'px;top:' + L.fy + 'px;width:' + L.F + 'px;height:' + L.F + 'px">'
      + (ctx.extra('frame') || '') + '<div class="scl-gridhost"></div><div class="scl-fxl"></div>' + (L.port ? '<div class="scl-chain"></div>' : '') + '</div>';
    h += '<div class="scl-flash"></div><div class="scl-fly-layer"></div>';
    st.innerHTML = h;
    st.style.setProperty('--S', L.S + 'px');
    cl.meterEl = st.querySelector('.scl-mwrap svg');
    cl.board = st.querySelector('.scl-board'); cl.fxl = st.querySelector('.scl-fxl'); cl.fly = st.querySelector('.scl-fly-layer');
    cl.L = L;
    const pk = picker(W8);
    ctx.makeReels(st.querySelector('.scl-gridhost'), { cols: C, rows: R, size: L.S, gap: L.g, pool: PAYS,
      initial: ctx.keepGrid ? undefined : { grid: Array.from({ length: C }, () => Array.from({ length: R }, pk)) } });
    sparkImage(ctx);
    paintLevels(ctx);
    if (!cl.greeted) { cl.greeted = true; ctx.timeout(() => ctx.say('шукаєм цвіт папороті? крути', 4000), 700); }
  }

  // ---------- Шкала ----------
  function paintLevels(ctx) {
    const cl = ctx.cl, v = cl.fern;
    cl.st.querySelectorAll('[data-n]').forEach((e) => e.classList.toggle('on', v >= LV[e.dataset.n - 1]));
  }
  function setFern(ctx, v, sound) {
    const cl = ctx.cl, was = cl.fern; cl.fern = v;
    const ms = ctx.art && ctx.art.extras && ctx.art.extras.meterSet;
    if (cl.meterEl && ms) ms(cl.meterEl, meterU(v));
    paintLevels(ctx);
    const wrap = cl.st.querySelector('.scl-mwrap');
    if (v > was && wrap) { wrap.classList.remove('gulp'); void wrap.offsetWidth; wrap.classList.add('gulp'); }
    if (sound && v > was) ctx.sound('scl-drop', { p: v / MAX });
    const crossed = LV.findIndex((t) => was < t && v >= t);
    if (crossed >= 0) levelReady(ctx, crossed + 1);
  }
  // бутон розкрився — спалах шкали, рядок рівня світиться
  function levelReady(ctx, n) {
    const cl = ctx.cl;
    if (cl.meterEl) { cl.meterEl.classList.remove('flash'); void cl.meterEl.getBoundingClientRect(); cl.meterEl.classList.add('flash'); }
    ctx.sound('level');
    const bud = cl.meterEl && cl.meterEl.querySelector('.bud.b' + n);
    if (bud) ctx.fx.at(bud, { kind: 'sclspark', n: 18, speed: 300, size: 9, gravity: 60, life: 1 });
  }
  // центр елемента в px сцени (дизайну) — помічник кіта
  function rel(ctx, el) { return ctx.rel(el, ctx.cl.st); }
  function headEl(ctx) { return ctx.cl.meterEl && (ctx.cl.meterEl.querySelector('.m-head') || ctx.cl.meterEl); }

  // крапельки: від згаслих клітинок до голівки шкали (WAAPI, без rAF)
  function drops(ctx, els) {
    const cl = ctx.cl, head = headEl(ctx); if (!head || !cl.fly) return 0;
    const [hx, hy] = rel(ctx, head), sp = ctx.extra('spark') || '';
    const list = els.length > 14 ? els.filter((e, i) => i % Math.ceil(els.length / 14) === 0) : els;
    const dur = ctx.turbo ? 380 : 620;
    list.forEach((e, i) => {
      const [x, y] = rel(ctx, e);
      const d = document.createElement('div'); d.className = 'scl-drop'; d.innerHTML = sp; cl.fly.appendChild(d);
      const mx = (x + hx) / 2 + (Math.random() - 0.5) * 120, my = Math.min(y, hy) - 60 - Math.random() * 80;
      const a = d.animate([
        { transform: 'translate(' + x + 'px,' + y + 'px) scale(.6)', opacity: 0 },
        { transform: 'translate(' + mx + 'px,' + my + 'px) scale(1.1)', opacity: 1, offset: 0.45 },
        { transform: 'translate(' + hx + 'px,' + hy + 'px) scale(.5)', opacity: 0.9 },
      ], { duration: dur, delay: i * 30, easing: 'cubic-bezier(.4,0,.6,1)', fill: 'both' });
      a.finished.then(() => d.remove(), () => d.remove());
    });
    return dur + list.length * 30;
  }

  // ---------- Свої кроки ----------
  async function onRemove(els, ctx, step) {
    const cl = ctx.cl;
    if (step.boom) {
      const big = cl.fxl.querySelector('.scl-bigbloom');
      ctx.sound('scl-boom');
      ctx.shake(1.3); ctx.flash('#ffe0f0', { power: 0.7, ms: 520 });
      flash(ctx, 3);
      if (big) { big.classList.add('boom'); ctx.fx.at(big, { kind: 'sclspark', n: 70, speed: 900, size: 14, gravity: 200, life: 1.4 }); ctx.fx.at(big, { kind: 'confetti', n: 40, speed: 800 }); }
      const [cx, cy] = ctx.reels.center(3, 3);
      // хвиля від центру: затримка за відстанню
      els.forEach((e) => {
        const m = /translate\(([-\d.]+)px,\s*([-\d.]+)px\)/.exec(e.style.transform || '');
        const dist = m ? Math.hypot(+m[1] + cl.L.S / 2 - cx, +m[2] + cl.L.S / 2 - cy) : 0;
        if (e.firstElementChild) e.firstElementChild.style.animationDelay = Math.round(dist * 1.4) + 'ms';
        e.classList.add('fade');
      });
      await ctx.wait(ctx.turbo ? 500 : 900, true);
      if (big) big.remove();
      return;
    }
    ctx.sound('scl-puff');
    els.forEach((e) => { e.classList.add('fade'); ctx.fx.at(e, { kind: 'sclspark', n: 4, speed: 220, size: 8, gravity: 90, life: 0.8 }); });
    const ms = drops(ctx, els);
    // крапельки долітають — шкала наливається
    ctx.timeout(() => { if (step.fern != null) setFern(ctx, step.fern, true); }, Math.min(ms, ctx.turbo ? 420 : 700));
    await ctx.wait(ctx.turbo ? 300 : 520, true);
  }

  function flash(ctx, n) {
    const f = ctx.cl.st.querySelector('.scl-flash'); if (!f) return;
    f.className = 'scl-flash'; void f.offsetWidth; f.classList.add('go', 'n' + n);
  }
  function callout(ctx, n) {
    const cl = ctx.cl, el = document.createElement('div');
    el.className = 'scl-call n' + n;
    el.innerHTML = '<i>' + iconOf(ctx, n) + '</i><b>' + NAMES[n - 1] + '</b><small>' + SUBS[n - 1] + '</small>';
    cl.board.appendChild(el);
    ctx.timeout(() => { el.classList.add('out'); ctx.timeout(() => el.remove(), 400); }, ctx.turbo ? 1100 : 1800);
  }
  function morphCell(ctx, c, r, key, burst) {
    if (!ctx.reels) return;
    const e = ctx.cell(c, r); if (!e) return;
    e._key = key; e.dataset.k = key; e.replaceChildren(ctx.symNode(key));
    e.classList.remove('sk-morph', 'win', 'dim', 'glint'); void e.offsetWidth; e.classList.add('sk-morph');
    if (burst) ctx.fx.at(e, { kind: 'sclspark', n: burst, speed: 320, size: 10, gravity: 80, life: 0.9 });
  }

  async function flies(ctx, s) {
    const cl = ctx.cl, fly = ctx.extra('firefly') || '<div class="scl-fly-ph"></div>';
    const src = cl.meterEl && cl.meterEl.querySelector('.bud.b1');
    const [sx, sy] = src ? rel(ctx, src) : [0, 0];
    const k = ctx.turbo ? 0.6 : 1;
    const jobs = s.cells.map(([c, r, key], i) => {
      const e = ctx.cell(c, r); if (!e) return Promise.resolve();
      const [tx, ty] = rel(ctx, e);
      const d = document.createElement('div'); d.className = 'scl-ff'; d.innerHTML = fly; cl.fly.appendChild(d);
      const ang = Math.random() * 6.28, rad = 90 + Math.random() * 90;
      const m1 = [sx + (tx - sx) * 0.35 + Math.cos(ang) * rad, sy + (ty - sy) * 0.35 + Math.sin(ang) * rad];
      const m2 = [tx + Math.cos(ang + 2.4) * 60, ty + Math.sin(ang + 2.4) * 60];
      const tr = (p, sc, rot) => 'translate(' + p[0].toFixed(1) + 'px,' + p[1].toFixed(1) + 'px) rotate(' + rot + 'deg) scale(' + sc + ')';
      const a = d.animate([
        { transform: tr([sx, sy], 0.3, 0), opacity: 0 },
        { transform: tr(m1, 1.2, -25), opacity: 1, offset: 0.4 },
        { transform: tr(m2, 1.1, 20), opacity: 1, offset: 0.75 },
        { transform: tr([tx, ty], 0.9, 0), opacity: 1 },
      ], { duration: (1100 + i * 40) * k, delay: i * 170 * k, easing: 'ease-in-out', fill: 'both' });
      ctx.timeout(() => ctx.sound('scl-fly'), i * 170 * k);
      return a.finished.then(() => {
        morphCell(ctx, c, r, key, 16); ctx.sound('coin');
        const b = d.animate([{ opacity: 1, transform: tr([tx, ty], 0.9, 0) }, { opacity: 0, transform: tr([tx, ty - 30], 1.6, 0) }], { duration: 380, fill: 'both' });
        return b.finished.then(() => d.remove());
      }, () => d.remove());
    });
    await Promise.all(jobs);
    await ctx.wait(350);
  }

  async function mermaid(ctx, s) {
    const cl = ctx.cl, svg = ctx.extra('mermaid');
    const m = document.createElement('div'); m.className = 'scl-mer'; m.innerHTML = svg || '<div class="scl-mer-ph">🧜‍♀️</div>';
    cl.fxl.appendChild(m);
    void m.offsetWidth; m.classList.add('in');
    const sv = m.querySelector('svg'); if (sv) sv.classList.add('go');
    ctx.sound('scl-splash');
    ctx.timeout(() => ctx.fx.at(m, { kind: 'sclspark', n: 26, speed: 420, size: 9, gravity: 260, life: 1 }), 250);
    await ctx.wait(1100);
    // квіти, що зміняться, світяться
    ctx.say('русалка махнула гребенем: ' + ctx.symName(s.from).toLowerCase() + ' → ' + ctx.symName(s.to).toLowerCase(), 3000);
    s.cells.forEach(([c, r]) => { const e = ctx.cell(c, r); if (e) e.classList.add('scl-tgt'); });
    await ctx.wait(650);
    const [mx, my] = [cl.L.F / 2, cl.L.F];
    const order = s.cells.map(([c, r, k]) => { const [x, y] = ctx.reels.center(c, r); return { c, r, k, d: Math.hypot(x - mx, y - my) }; }).sort((a, b) => a.d - b.d);
    order.forEach((o, i) => ctx.timeout(() => {
      const e = ctx.cell(o.c, o.r); if (e) e.classList.remove('scl-tgt');
      morphCell(ctx, o.c, o.r, o.k, 6); if (i % 3 === 0) ctx.sound('scl-drop', { p: Math.min(1, i / order.length) });
    }, i * (ctx.turbo ? 25 : 45)));
    await ctx.wait(order.length * (ctx.turbo ? 25 : 45) + 450, true);
    m.classList.add('out');
    ctx.timeout(() => m.remove(), 700);
    await ctx.wait(300);
  }

  async function bloomIn(ctx, s) {
    const cl = ctx.cl, L = cl.L;
    const b = document.createElement('div'); b.className = 'scl-bigbloom';
    const x = 2 * (L.S + L.g), w = 3 * L.S + 2 * L.g;
    b.style.cssText = 'left:' + x + 'px;top:' + x + 'px;width:' + w + 'px;height:' + w + 'px';
    b.innerHTML = '<div class="scl-bb-rays"></div>' + ctx.symHtml(BLOOM);
    cl.fxl.appendChild(b);
    ctx.sound('bonus');
    s.cells.forEach(([c, r, k]) => { morphCell(ctx, c, r, k); const e = ctx.cell(c, r); if (e) e.classList.add('scl-under'); });
    void b.offsetWidth; b.classList.add('in');
    ctx.fx.at(b, { kind: 'sclspark', n: 50, speed: 650, size: 12, gravity: 120, life: 1.2 });
    ctx.say('цвіт папороті! всю ніч шукали', 3500);
    await ctx.wait(1500);
  }

  // ---------- Перелесник (сюрприз) ----------
  // Вогняний дух влітає з-за краю, летить точно по s.path (центри клітинок) і, пролітаючи над клітинкою з s.cells,
  // лишає там листок папороті з іскристим спалахом; вилітає за протилежний край. Один WAAPI-політ (ctx.animate:
  // тап доводить до кінця, турбо коротший), листки — за прогресом польоту; після польоту дозасвічуємо, що лишилось,
  // тож поле завжди рівно spin.grid + cells — наступний win рахується на ньому.
  async function perelesnyk(s, ctx) {
    const cl = ctx.cl, L = cl.L, path = s.path, step = L.S + L.g;
    ctx.emit('surprise', 'perelesnyk', s);
    ctx.react('surprise', { why: 'perelesnyk' });
    ctx.clearWin();
    const pts = path.map(([c, r]) => { const e = ctx.cell(c, r); return e ? rel(ctx, e) : null; });
    if (pts.some((p) => !p)) { await SK.steps.morph(s, ctx); return; }
    // вхід і вихід — на 1,6 клітинки за краями вздовж головного напрямку польоту
    const [x0, y0] = pts[0], [x6, y6] = pts[pts.length - 1], dl = Math.hypot(x6 - x0, y6 - y0) || 1;
    const ux = (x6 - x0) / dl, uy = (y6 - y0) / dl, out = step * 1.6;
    const way = [[x0 - ux * out, y0 - uy * out]].concat(pts, [[x6 + ux * out, y6 + uy * out]]);
    const n = way.length, len = [0];
    for (let i = 1; i < n; i++) len.push(len[i - 1] + Math.hypot(way[i][0] - way[i - 1][0], way[i][1] - way[i - 1][1]));
    const tot = len[n - 1] || 1;
    // кут — уздовж відрізка, без стрибків через ±180°; летить ліворуч — дзеркалимо, щоб личко не було догори дриґом
    const flip = ux < -0.5 ? -1 : 1, ang = [];
    for (let i = 0; i < n; i++) {
      const j = Math.min(i, n - 2);
      let a = Math.atan2(way[j + 1][1] - way[j][1], way[j + 1][0] - way[j][0]) * 180 / Math.PI + (flip < 0 ? 180 : 0);
      if (i) { while (a - ang[i - 1] > 180) a -= 360; while (a - ang[i - 1] < -180) a += 360; }
      ang.push(a);
    }
    const sz = L.S * 2;
    const sp = document.createElement('div'); sp.className = 'scl-pere';
    sp.style.cssText = 'width:' + sz + 'px;height:' + (sz * 0.6) + 'px;left:' + (-sz * 0.72) + 'px;top:' + (-sz * 0.3) + 'px';
    sp.innerHTML = ctx.extra('perelesnyk') || '<div class="scl-pere-ph"></div>';
    cl.fly.appendChild(sp);
    // ярлик над полем: хто це прилетів
    const tag = document.createElement('div'); tag.className = 'scl-pere-tag'; tag.innerHTML = '<b>Перелесник!</b><small>лишає листки папороті</small>';
    cl.board.appendChild(tag);
    ctx.timeout(() => { tag.classList.add('out'); ctx.timeout(() => tag.remove(), 400); }, ctx.turbo ? 900 : 1500);
    ctx.sound('whoosh');
    ctx.flash('#ffb14a', { power: 0.35, ms: 380 });
    const kf = way.map((p, i) => ({
      transform: 'translate(' + p[0].toFixed(1) + 'px,' + p[1].toFixed(1) + 'px) rotate(' + ang[i].toFixed(1) + 'deg) scale(' + flip + ',1)',
      opacity: i === 0 || i === n - 1 ? 0 : 1, offset: len[i] / tot,
    }));
    // у вільних — коротше; турбо — ctx.animate сам ×0,6
    const a = ctx.animate(sp, kf, { duration: ctx.fs ? 1050 : 1450, easing: 'linear', fill: 'both' });
    // листок — коли дух над клітинкою: індекс клітинки в path → частка шляху (+1 — точка входу)
    const drops = s.cells.map(([c, r, key]) => {
      const i = path.findIndex(([pc, pr]) => pc === c && pr === r);
      return { c, r, key, at: i < 0 ? 1 : len[i + 1] / tot, done: false };
    });
    const drop = (d) => {
      if (d.done) return; d.done = true;
      morphCell(ctx, d.c, d.r, d.key, SK.calm() ? 8 : 18);
      const e = ctx.cell(d.c, d.r);
      if (e) {
        e.classList.remove('scl-pered'); void e.offsetWidth; e.classList.add('scl-pered');
        ctx.timeout(() => e.classList.remove('scl-pered'), 1300);
        ctx.fx.at(e, { kind: 'spark', n: 8, speed: 260, size: 7, gravity: 120, life: 0.6 });
      }
      ctx.sound('scl-ember');
    };
    let k = 0;
    const iv = ctx.interval(() => {
      const t = a.effect && a.effect.getComputedTiming().progress;
      if (t == null) return;
      drops.forEach((d) => { if (t >= d.at - 0.01) drop(d); });
      // хвіст іскор за духом (телефон і зменшений рух — удвічі рідше)
      if (k++ % (SK.calm() || ctx.orient === 'port' ? 2 : 1) === 0 && t > 0.04 && t < 0.96) ctx.fx.at(sp, { kind: 'sclspark', n: 2, speed: 90, size: 7, gravity: -30, life: 0.55 });
    }, 40);
    await a.finished.catch(() => {});
    ctx.clearTimer(iv);
    drops.forEach(drop);            // тап/пропуск — решта листків одразу
    sp.remove();
    ctx.shake(0.3);
    await ctx.wait(ctx.fs ? 160 : 260);
  }

  const steps = {
    // morph з why: 'perelesnyk' — своя вистава; решта (і без шляху) — як у кіта
    async morph(s, ctx) {
      if (s.why === 'perelesnyk' && Array.isArray(s.path) && s.path.length && ctx.cl && ctx.cl.fly) return perelesnyk(s, ctx);
      return SK.steps.morph(s, ctx);
    },
    async lvl(s, ctx) {
      ctx.clearWin();
      flash(ctx, s.n);
      callout(ctx, s.n);
      ctx.cl.st.querySelectorAll('.scl-lv[data-n="' + s.n + '"],.scl-mlab[data-n="' + s.n + '"]').forEach((e) => { e.classList.remove('hot'); void e.offsetWidth; e.classList.add('hot'); });
      ctx.sound('level');
      await ctx.wait(800);
      if (s.n === 1) { if (Math.random() < 0.4) ctx.say(SK.rnd.pick(['світлячки злетілись!', 'летять, летять — і все дике', 'світлячки на купала не спізнюються']), 2500); await flies(ctx, s); }
      else if (s.n === 2) await mermaid(ctx, s);
      else await bloomIn(ctx, s);
    },
    async fern(s, ctx) {
      if (s.to === ctx.cl.fern) return;
      setFern(ctx, s.to, false);
      if (!s.end) await ctx.wait(500);
    },
  };

  // бонус: своя заставка з цвітом
  async function bonusIn(s, ctx) {
    ctx.setScene('bonus');
    ctx.flash('#ff9ad0', { power: 0.8, ms: 600 }); ctx.shake(0.8);
    const ov = ctx.overlay('sk-bonus-in scl-bin',
      '<div class="sk-ov-card"><div class="scl-bin-bloom"><div class="scl-bb-rays"></div>' + ctx.symHtml(BLOOM) + '</div>'
      + '<div class="sk-ov-t">' + s.title + '</div><div class="sk-ov-n">' + s.count + '</div><div class="sk-ov-s">' + s.sub + '</div>'
      + '<div class="sk-ov-hint">тисни, щоб почати</div></div>');
    ctx.fx.at(ov.querySelector('.sk-ov-n'), { kind: 'sclspark', n: 50, speed: 650, size: 12, gravity: 100, life: 1.4 });
    await ctx.wait(ctx.auto ? 1800 : 3400, true);
    await ctx.closeOverlay(ov);
  }

  function clearFx(ctx) {
    const cl = ctx.cl; if (!cl || !cl.st) return;
    cl.st.querySelectorAll('.scl-bigbloom,.scl-mer,.scl-call,.scl-ff,.scl-drop,.scl-pere,.scl-pere-tag').forEach((e) => e.remove());
    const ch = cl.st.querySelectorAll('.scl-chain'); ch.forEach((e) => { e.textContent = ''; e.classList.remove('on'); });
  }

  function payRows() {
    return PAYS.slice().reverse().map((k) => ({
      key: k, unit: '+',
      pays: { 5: PAY[k][0], 8: PAY[k][3], 11: PAY[k][5], 16: PAY[k][7] },
      note: k === 'comb' ? 'найдорожчий' : null,
    })).concat([{ key: WILD, pays: {}, note: 'дикий — входить у будь-який кластер' }]);
  }

  SK.define({
    id: ID,
    title: 'Цвіт папороті',
    grid: { cols: C, rows: R },
    spinStyle: 'drop',
    payUnit: 1,
    sounds: { win: 'win' },
    paytable: payRows(),
    // на сайті slot.js підставляє view.table.pay ({ sym: { "5": …, "16": … } }) і сам view.table — ⓘ, шкала, ваги
    // демо й перелесник не розійдуться з сервером
    _setPay(pay, table) {
      const m = SK.machines[ID];
      if (pay) Object.keys(PAY).forEach((k) => { const p = pay[k]; if (p) PAY[k] = SIZES.map((n, i) => (p[n] != null ? +p[n] : PAY[k][i])); });
      if (table) {
        if (Array.isArray(table.levels) && table.levels.length === 3) m._tune({ lv: table.levels.map(Number), w: table.weights || null, fsf: table.fsFern });
        else if (table.weights) m._tune({ w: table.weights, fsf: table.fsFern });
        const pe = table.pere;
        if (pe) Object.assign(PERE, { chance: +pe.chance || PERE.chance, min: pe.min || PERE.min, max: pe.max || PERE.max, flight: pe.flight || PERE.flight });
        if (typeof table.rules === 'string' && table.rules) serverRules = table.rules;
      }
      m.paytable = payRows();
    },
    rules: () => serverRules || ('<b>Кластер</b> — 5 і більше однакових, що стикаються боками (не навскіс). Виграшні згасають, решта падає, згори нові — і знову. '
      + '<b>Листок папороті</b> — дикий. '
      + '<b>Перелесник</b> — на початку кожного оберту (і вільного) з шансом 1 з ' + Math.round(1 / PERE.chance) + ' пролітає полем і лишає ' + PERE.min + '–' + PERE.max + ' листки папороті. '
      + 'Кожен згаслий символ кладе крапельку в <b>шкалу папороті</b>; рівні, раз за оберт кожен: '
      + '<b>' + LV[0] + ' · світлячки</b> — 3–6 клітинок стають дикими; <b>' + LV[1] + ' · русалка</b> — один вид квітів стає іншим; '
      + '<b>' + LV[2] + ' · цвіт папороті</b> — дикий 3×3 посеред поля, вибухає, далі <b>5 вільних обертів</b>, де шкала не скидається (знову до цвіту — +3, разом до 15).'),
    // сюрприз: вистава своя (steps.morph), кіт лише знає назву
    surprises: { perelesnyk: { title: 'Перелесник!', sub: 'лишає листки папороті', own: true } },
    // репліки Глека-ведучого під купальську ніч (кіт сам тротлить і не повторює)
    say: {
      small: ['пелюсточка до пелюсточки', 'на віночок ще не тягне', 'дрібненько, як роса'],
      win: ['квіти зійшлись', 'отакий букет!', 'лови віночок'],
      nice: ['гарно розцвіло!', 'аж папороть зашелестіла', 'файний букет'],
      big: ['оце купала!', 'весь луг твій!', 'та тут на весілля вистачить'],
      surprise: ['перелесник! не лови — обпечешся', 'вогник полетів, листки лишив', 'то перелесник, на купала вони такі', 'ой, хтось пролетів!'],
      bonus: ['цвіт папороті! всю ніч шукали', 'знайшли таки цвіт!'],
      dry: ['папороть цвіте раз на рік, а квіти — щооберта', 'кожен оберт — нова ніч, нічого не пам’ятає', 'може, по воду до русалки? я почекаю'],
      idle: ['туман над лугом… крутимо?', 'світлячки нудьгують'],
    },
    initialState: () => ({}),
    spin(bet, state) { return simulate(bet, state); },
    demo: {
      'Малий кластер': demoBy((x, m) => m > 0 && m < 3 && x.chain === 1 && !x.lv[0] && !x.pere),
      'Перелесник': demoBy((x, m) => x.pere && !x.bonus && m > 0 && m < 10, null, (x, m) => (x.pere ? 10 + Math.min(m, 9) : 0) - (x.bonus ? 50 : 0)),
      'Перелесник + цвіт': demoBy((x) => x.pere && x.bonus, RICH, (x) => (x.bonus ? 100 : 0) + (x.pere ? 50 : 0) + x.lv[1], 4000),
      'Каскад 3+': demoBy((x, m) => x.chain >= 3 && !x.lv[1] && m < 10, RICH, (x) => x.chain),
      'Світлячки': demoBy((x, m) => x.lv[0] && !x.lv[1] && m < 10, RICH, (x) => x.lv[0] * 5 - x.lv[1] * 9),
      'Русалка': demoBy((x) => x.lv[1] && !x.lv[2], RICH, (x) => x.lv[1] * 5 - x.lv[2] * 9),
      'Цвіт папороті': demoBy((x) => x.bonus, RICH, (x) => (x.bonus ? 100 : 0) + x.lv[1] * 2 + x.lv[0]),
      'Великий занос': demoBy((x, m) => m >= 10 && m < 25 && !x.bonus, HOT, (x, m) => (m < 25 ? m : -m)),
      'Мега занос': demoBy((x, m) => m >= 25 && m < 50 && !x.bonus, HOT2, (x, m) => (x.bonus ? -1 : m < 50 ? m : 50 - m)),
      'Епічний занос': demoBy((x, m) => m >= 50 && !x.bonus, HOT2, (x, m) => (x.bonus ? -1 : m), 7000),
    },
    _tune(o) { if (o.w) { for (const k in W8) delete W8[k]; Object.keys(o.w).forEach((k) => { if (k !== BLOOM) W8[k] = +o.w[k]; }); } if (o.lv) { LV.splice(0, 3, ...o.lv); MAX = LV[2]; } if (o.pay) Object.assign(PAY, o.pay); if (o.fsf) FS_FERN = o.fsf; },
    _sim: sim, _simulate: simulate, _clusters: clusters,
    build,
    steps,
    onRemove,
    bonusIn,
    onSpinStart(ctx) { clearFx(ctx); },
    // після каскадів старі виграшні клітинки вже впали/зникли — кіт не має перебирати їх у спокої
    onSpinEnd(ctx) { ctx.lastWins = null; ctx.clearWin(); },
    onCascade(ctx, n) {
      if (n < 2) return;   // dim після падіння знімає кіт
      ctx.cl.st.querySelectorAll('.scl-chain').forEach((e) => { e.textContent = 'каскад ×' + n; e.classList.remove('on'); void e.offsetWidth; e.classList.add('on'); });
    },
    onWin(ctx, step) {
      const big = (step.items || []).reduce((a, it) => Math.max(a, it.n || 0), 0);
      if (big >= 11) ctx.say(SK.rnd.pick(['оце віночок!', 'цілий луг!', 'та тут на весілля вистачить']), 2500);
    },
    unbuild(ctx) { clearFx(ctx); },
  });
})();
