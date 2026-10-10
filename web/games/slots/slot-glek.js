/* Однорукий Глек (slot-glek): 3×3, 5 ліній, ретро-корпус з лампочками, ручка, табло з механічними цифрами,
   ризик-гра «Ворожка». Механіка й мок-математика — тут; арт — slot-glek-art.js (SlotArt['slot-glek']). */
(function () {
  'use strict';
  const SK = window.SlotKit, ID = 'slot-glek';
  const R3 = [0, 1, 2];
  // Стрічки барабанів (по 34) — ті самі, що SlotGlekMath.Reels на сервері (він дає лише stops): вишні 9, груша 8,
  // слива 7, кавун 3, дзвоник 2, підкова 2, сімка 2 (стоять поруч), Глек 1.
  const REELS = [
    'plum cherry seven seven pear cherry plum pear cherry melon plum pear cherry bell plum pear cherry melon plum pear cherry horseshoe glek pear cherry plum melon cherry pear plum bell cherry pear horseshoe',
    'plum melon pear cherry plum bell pear cherry melon plum pear cherry pear seven seven cherry plum horseshoe cherry pear plum melon cherry pear plum glek cherry pear bell plum cherry pear horseshoe cherry',
    'horseshoe pear cherry bell plum pear cherry melon pear cherry plum horseshoe pear cherry plum bell melon cherry pear plum cherry plum pear cherry seven seven pear plum cherry melon pear glek cherry plum',
  ].map((s) => s.split(' '));
  // Лінії: рядок кожного барабана. 1 — середня, 2 — верхня, 3 — нижня, 4 і 5 — діагоналі.
  const LINES = [[1, 1, 1], [0, 0, 0], [2, 2, 2], [0, 1, 2], [2, 1, 0]];
  // Виплати — у ставках на лінію (ставка ÷ 5). Як на сервері (docs/games/specs/slots.md §1, RTP 97 % з чихом Глека);
  // на сайті slot.js ще й підставляє view.table.pay через _setPay — щоб ⓘ і табло не розійшлись із касою.
  const PAY = { glek: { 3: 500 }, seven: { 3: 120 }, horseshoe: { 3: 60 }, bell: { 3: 40 }, melon: { 3: 26 }, plum: { 3: 10 }, pear: { 3: 8 }, cherry: { 3: 7, 2: 1 } };
  // «Глек чхнув» 🤧: шанс на оберт і скільки клітинок (відсотки серед чихів). На сайті — з view.table.sneeze (там і
  // text для ⓘ); тут — мок для стенду, ті самі числа, що SlotGlekMath (SneezeOdds 12, SneezeWeights 14/5/1).
  const SNEEZE = { oneIn: 12, cells: { 1: 70, 2: 25, 3: 5 }, min: 1, max: 3,
    text: 'Після зупинки з шансом 1 з 12 Дядько Глек чхає 🤧 — 1–3 клітинки (не Глеки) стають дикими Глеками, і лінії рахуються вже на зміненому полі. Шанс завжди однаковий — не залежить ні від програшів, ні від гаманця.' };
  // Таблиця з сервера: виплати, а ще стрічки й лінії (сервер шле лише stops — крутимо його стрічки) і правила чиху.
  // Масиви міняємо на місці: makeReels/machine.reels тримають ті самі посилання.
  function setPay(pay, table) {
    if (pay) Object.keys(pay).forEach((k) => { if (!PAY[k]) return; Object.keys(PAY[k]).forEach((n) => delete PAY[k][n]); Object.assign(PAY[k], pay[k]); });
    if (!table) return;
    const t = table.reels;
    if (Array.isArray(t) && t.length === REELS.length && t.every((r) => Array.isArray(r) && r.length > 2 && r.every((k) => PAY[k]))) {
      t.forEach((r, c) => REELS[c].splice(0, REELS[c].length, ...r)); ALL = null;
    }
    const l = table.lines;
    if (Array.isArray(l) && l.length && l.every((x) => Array.isArray(x) && x.length === 3)) { LINES.splice(0, LINES.length, ...l.map((x) => x.slice())); ALL = null; }
    if (table.sneeze && table.sneeze.oneIn > 0) Object.assign(SNEEZE, table.sneeze);
  }
  const WILD = 'glek';

  function gridOf(stops) { return stops.map((s, c) => R3.map((r) => REELS[c][(s + r) % REELS[c].length])); }
  function evalLine(keys) {
    const base = keys.find((k) => k !== WILD) || WILD;
    let n = 0; for (const k of keys) { if (k === base || k === WILD) n++; else break; }
    const pay = (PAY[base] && PAY[base][n]) || 0;
    return pay ? { sym: base, n, pay } : null;
  }
  // Усе в «одиницях лінії»: u — сума виплат у ставках на лінію
  function evaluate(grid) {
    const items = []; let u = 0;
    LINES.forEach((ln, i) => {
      const w = evalLine(ln.map((r, c) => grid[c][r]));
      if (w) { items.push({ line: i, sym: w.sym, n: w.n, u: w.pay, cells: ln.slice(0, w.n).map((r, c) => [c, r]) }); u += w.pay; }
    });
    // очікування — лише чесно: на якійсь лінії перші два вже «сімка/Глек», і третій справді вирішує
    const big = (k) => k === WILD || k === 'seven';
    const tease = LINES.some((ln) => big(grid[0][ln[0]]) && big(grid[1][ln[1]])) ? [2] : [];
    return { items, u, tease };
  }
  // Чих (мок, як SlotGlekMath.Sneeze): 1 з oneIn; кількість — за відсотками cells, не більше, ніж є не-Глеків; які —
  // рівномірно серед не-Глеків. force — для демо (чхає завжди).
  function sneezeCells(grid, force) {
    if (!force && SK.rnd.int(SNEEZE.oneIn) !== SNEEZE.oneIn - 1) return [];
    const pool = [];
    grid.forEach((col, c) => col.forEach((k, r) => { if (k !== WILD) pool.push([c, r]); }));
    const n = Math.min(pool.length, Number(SK.rnd.weighted(SNEEZE.cells)) || 1);
    for (let i = pool.length - 1; i > 0; i--) { const j = SK.rnd.int(i + 1); [pool[i], pool[j]] = [pool[j], pool[i]]; }
    return pool.slice(0, n);
  }
  // sneeze — [[c, r]…] клітинки, що стануть Глеками (крок morph між spin і win; tease — на полі зупинки, до чиху)
  function scriptFor(stops, bet, state, extra, sneeze) {
    const grid0 = gridOf(stops), grid = grid0.map((col) => col.slice()), lb = bet / 5;
    (sneeze || []).forEach(([c, r]) => { grid[c][r] = WILD; });
    const ev = evaluate(grid), tease = evaluate(grid0).tease;
    const items = ev.items.map((it) => ({ line: it.line, cells: it.cells, sym: it.sym, amount: Math.round(it.u * lb) }));
    const win = items.reduce((a, it) => a + it.amount, 0);
    const steps = [{ t: 'spin', stops, tease }];
    if (sneeze && sneeze.length) steps.push({ t: 'morph', cells: sneeze.map(([c, r]) => [c, r, WILD]), why: 'sneeze' });
    if (items.length) steps.push({ t: 'win', items, amount: win });
    const sc = { bet, steps, win, state: state || {}, glek3: items.some((it) => it.sym === WILD && it.n === 3) };
    if (sneeze && sneeze.length) sc.sneeze = sneeze.length;
    return Object.assign(sc, extra || {});
  }
  const randStops = () => REELS.map((s) => SK.rnd.int(s.length));
  // демо чиху: випадкове поле, Глек чхає завжди; good — щоб чих дав виграш ≥ 2× (до 400 спроб)
  function demoSneeze(good) {
    return (bet, state) => {
      let best = null;
      for (let i = 0; i < (good ? 400 : 1); i++) {
        const stops = randStops(), sn = sneezeCells(gridOf(stops), true);
        best = { stops, sn };
        if (!good) break;
        const g = gridOf(stops); sn.forEach(([c, r]) => { g[c][r] = WILD; });
        if (evaluate(g).u / 5 >= 2 && evaluate(gridOf(stops)).u === 0) break;
      }
      return scriptFor(best.stops, bet, state, null, best.sn);
    };
  }

  // Усі зупинки (34×34×34) — для сценаріїв показу (рахуються раз, ~10 мс)
  let ALL = null;
  function all() {
    if (ALL) return ALL;
    ALL = [];
    for (let a = 0; a < REELS[0].length; a++) for (let b = 0; b < REELS[1].length; b++) for (let c = 0; c < REELS[2].length; c++) {
      const ev = evaluate(gridOf([a, b, c]));
      ALL.push({ stops: [a, b, c], m: ev.u / 5, n: ev.items.length, g3: ev.items.some((it) => it.sym === WILD && it.n === 3), tease: ev.tease.length > 0 });
    }
    return ALL;
  }
  function demoBy(pred, extra) {
    return (bet, state) => {
      const list = all().filter(pred);
      const pick = list.length ? SK.rnd.pick(list) : SK.rnd.pick(all());
      return scriptFor(pick.stops, bet, state, extra);
    };
  }

  // Свої репліки автомата (поза книгою кіта): привітання, три Глеки, очікування, ворожка, чих
  const SAY = {
    idle: ['смикай ручку, не соромся', 'ну шо, крутнемо?', 'барабани змащені, ручка чекає'],
    g3: ['та це ж я! тричі!', 'три Глеки — то вже родина'],
    sneeze: ['пардон! пилюка з барабанів', 'ой… накапав Глеками', 'хтось тут перцем посипав?', 'будьмо здорові — і я, і ви', 'не застуда — то я так вітаюсь'],
    sneeze2: ['оце чхнув — аж на дві клітинки'],
    sneeze3: ['ото чхнув! аж три клітинки забризкав'],
    tease: ['ану-ану…', 'не дихай…'],
    gamble: ['ворожка бачить усе… майже', 'ризикнеш?'],
    gwin: ['вгадала! ще?', 'карта до карти'],
    glose: ['карти брешуть, а Глек — ніколи', 'ну, не судилося'],
  };

  // Книга Глека-ведучого для цього автомата (кіт сам вирішує, коли сказати: дрібне — рідко, сильне — завжди)
  const HOST_SAY = {
    small: ['вишенька до вишеньки', 'на компот вистачить', 'дрібно, зате своє', 'дзенькнуло'],
    win: ['дзень!', 'о, пішло!', 'ото ряд!', 'файно лягло'],
    nice: ['гарно!', 'аж лампочки замиготіли', 'ото ручка вдало смикнулась'],
    big: ['оце так ярмарок!', 'тримай кишеню ширше', 'ото я розумію — занос'],
    legend: ['легенда! запишу в літопис', 'про таке на ярмарку пісні співатимуть'],
    idle: ['лампочки блимають — мені не нудно', 'ручка застоялась', 'а-а-ах… задрімав трохи'],
  };

  // ---------- «Глек чхнув» 🤧: своя сцена замість кітового morph ----------
  // Великий Дядько Глек виринає з-за вивіски, набирає повітря («а… а…»), АПЧХИ! — хмарка й бризки летять на барабани,
  // автомат здригається, і клітинки з «пчих» по черзі стають Глеками. ~1,4–1,8 с; турбо коротше; тап доводить до кінця.
  SK.svgParticle('gl-drop', '<svg viewBox="0 0 20 20"><path d="M10 1.5C10 1.5 3.6 9.6 3.6 13.2a6.4 6.4 0 0 0 12.8 0C16.4 9.6 10 1.5 10 1.5z" fill="#cdeeff" stroke="#3d7fae" stroke-width="1.6"/><ellipse cx="7.6" cy="12.4" rx="1.6" ry="2.6" fill="#fff" opacity=".85"/></svg>');
  SK.sound.add('gl-inhale', (h) => { h.noise(h.t, 0.32, 900, 0.6, 0.12); h.tone(220, h.t, 0.3, 'sine', 0.04, 420); });
  SK.sound.add('gl-achoo', (h) => { h.noise(h.t, 0.05, 2600, 0.8, 0.45); h.noise(h.t + 0.03, 0.4, 1500, 0.5, 0.4); h.tone(520, h.t, 0.22, 'triangle', 0.14, 160); });
  const fin = (a) => (a && a.finished ? a.finished.catch(() => {}) : Promise.resolve());
  async function sneezeScene(s, ctx) {
    const cells = (s.cells || []).filter((q) => Array.isArray(q));
    const cab = ctx.area.querySelector('.gl-cab-in');
    // сюрприз — подія й подив Глека-ведучого (без його репліки: вистава своя)
    ctx.emit('surprise', 'sneeze', s); ctx.react('surprise', { why: 'sneeze', say: false });
    if (!cab) { await ctx.reels.morph(s.cells || []); return; }
    cab.querySelectorAll('.gl-sz').forEach((e) => e.remove());
    const n = cells.length, z = document.createElement('div');
    z.className = 'gl-sz';
    z.innerHTML = '<div class="gl-sz-cloud"><i></i><i></i><i></i><i></i><i></i><i></i></div>'
      + '<div class="gl-sz-g">' + ctx.symHtml(WILD) + '<span class="gl-sz-m"></span></div>'
      + '<div class="gl-sz-b"></div>'
      + '<div class="gl-sz-a"><b>АПЧХИ!</b><small>Глек чхнув 🤧 +' + n + ' ' + (n === 1 ? 'дикий' : 'дикі') + '</small></div>';
    cab.appendChild(z);
    const g = z.querySelector('.gl-sz-g'), bub = z.querySelector('.gl-sz-b'), ach = z.querySelector('.gl-sz-a'), cloud = z.querySelector('.gl-sz-cloud'), mouth = z.querySelector('.gl-sz-m');
    const pose = (y, sx, sy, rot) => 'translateY(' + y + 'px) scale(' + sx + ',' + sy + ') rotate(' + rot + 'deg)';
    // 1. виринає з-за вивіски
    ctx.sound('whoosh');
    await fin(ctx.animate(g, [{ transform: pose(-70, .3, .3, -20), opacity: 0 }, { transform: pose(0, 1, 1, 0), opacity: 1 }], { duration: 240, easing: 'cubic-bezier(.3,1.5,.5,1)', fill: 'forwards' }));
    // 2. «а… а…» — набирає повітря: роздувається й закидає голову
    z.classList.add('s1'); bub.textContent = 'а…'; bub.classList.add('on'); ctx.sound('gl-inhale');
    ctx.animate(g, [{ transform: pose(0, 1, 1, 0) }, { transform: pose(-6, 1.07, 1.1, -7) }], { duration: 260, easing: 'ease-out', fill: 'forwards' });
    await ctx.wait(260);
    if (!ctx.turbo) {
      bub.textContent = 'а… а…'; ctx.sound('gl-inhale');
      ctx.animate(g, [{ transform: pose(-6, 1.07, 1.1, -7) }, { transform: pose(-12, 1.14, 1.2, -13) }], { duration: 280, easing: 'ease-out', fill: 'forwards' });
      await ctx.wait(280);
    }
    // 3. АПЧХИ! — ривок уперед, хмарка й бризки на барабани, автомат здригається
    z.classList.remove('s1'); z.classList.add('s3'); bub.classList.remove('on');
    ctx.sound('gl-achoo');
    ctx.animate(g, [{ transform: pose(-12, 1.14, 1.2, -13) }, { transform: pose(24, .9, 1.06, 11), offset: .35 }, { transform: pose(6, 1, 1, 4) }], { duration: 380, easing: 'ease-out', fill: 'forwards' });
    ctx.animate(ach, [{ transform: 'translate(-50%,0) scale(.3) rotate(-14deg)', opacity: 0 }, { transform: 'translate(-50%,0) scale(1.15) rotate(-6deg)', opacity: 1, offset: .35 }, { transform: 'translate(-50%,0) scale(1) rotate(-5deg)', opacity: 1 }], { duration: 380, easing: 'ease-out', fill: 'forwards' });
    const cl = ctx.animate(cloud, [{ transform: 'scale(.12)', opacity: .95 }, { transform: 'scale(1)', opacity: .8, offset: .4 }, { transform: 'scale(1.18)', opacity: 0 }], { duration: 1000, easing: 'cubic-bezier(.2,.7,.4,1)', fill: 'forwards' });
    ctx.shake(0.9); ctx.flash('#e6f6ff', { power: 0.35, ms: 360 });
    const port = ctx.orient === 'port';
    ctx.fx.at(mouth, { kind: 'gl-drop', n: port ? 12 : 20, speed: 720, angle: Math.PI / 2, spread: 1.9, gravity: 560, size: 7, life: 0.85 });
    ctx.say(SK.rnd.pick(n === 3 ? SAY.sneeze3 : n === 2 && Math.random() < 0.5 ? SAY.sneeze2 : SAY.sneeze), 2800);
    await ctx.wait(200);
    // 4. «пчих!» — клітинки по черзі стають Глеками
    for (let i = 0; i < n; i++) {
      const [c, r] = cells[i], e = ctx.morphCell(c, r, WILD);
      if (e) {
        e.classList.add('gl-snz');
        const pf = document.createElement('span'); pf.className = 'gl-pchyh'; pf.innerHTML = '<i></i><b>пчих!</b>'; e.appendChild(pf);
        fin(ctx.animate(pf, [{ transform: 'scale(.4)', opacity: 1 }, { transform: 'scale(1.1)', opacity: 1, offset: .4 }, { transform: 'scale(1.3)', opacity: 0 }], { duration: 620, easing: 'ease-out', fill: 'forwards' })).then(() => pf.remove());
        ctx.fx.at(e, { kind: 'spark', n: port ? 7 : 11, speed: 320, size: 8 });
        ctx.fx.at(e, { kind: 'gl-drop', n: port ? 3 : 5, speed: 260, spread: 6.28, gravity: 400, size: 6, life: 0.6 });
      }
      ctx.sound('clink', { p: i });
      await ctx.wait(n > 1 ? 170 : 150);
    }
    // 5. витирає вуса й ховається
    const out = ctx.animate(g, [{ transform: pose(6, 1, 1, 4), opacity: 1 }, { transform: pose(-50, .5, .5, 10), opacity: 0 }], { duration: 260, easing: 'ease-in', fill: 'forwards' });
    ctx.animate(ach, [{ opacity: 1 }, { opacity: 0 }], { duration: 260, fill: 'forwards' });
    await ctx.wait(230);
    Promise.all([fin(out), fin(cl)]).then(() => z.remove());
  }

  // ---------- корпус: геометрія як у extras.layout арту (viewBox 420×640) ----------
  const LAY = { w: 420, h: 640, window: { x: 56, y: 182, w: 308, h: 308, r: 10 }, tablo: { x: 76, y: 522, w: 268, h: 42 }, logo: { x: 44, y: 20, w: 332, h: 120 }, handle: { x: 418, y: 340 } };
  const SPOTS = (function () {
    const a = [];
    for (let i = 0; i < 16; i++) { const y = 198 + i * 25; a.push([26, y], [394, y]); }
    [[27, 160], [27, 132], [393, 160], [393, 132]].forEach((p) => a.push(p));
    const q = (x, y, z, t) => (1 - t) * (1 - t) * x + 2 * (1 - t) * t * y + t * t * z;
    for (let i = 0; i <= 13; i++) { const t = i / 14; a.push([q(27, 28, 210, t), q(104, 13, 11, t)]); if (i) a.push([q(393, 392, 210, t), q(104, 13, 11, t)]); }
    a.push([210, 11]);
    return a;
  })();
  // ---------- заглушки extras (поки арту нема) ----------
  const PH = {
    cabinet: '<svg viewBox="0 0 420 640"><defs><linearGradient id="gl-ph-wood" x1="0" x2="1"><stop offset="0" stop-color="#4f260f"/><stop offset=".15" stop-color="#8a4a22"/><stop offset=".5" stop-color="#b86a34"/><stop offset=".85" stop-color="#8a4a22"/><stop offset="1" stop-color="#4f260f"/></linearGradient></defs>'
      + '<path fill-rule="evenodd" fill="url(#gl-ph-wood)" stroke="#2e1606" stroke-width="6" d="M12 112Q14 8 210 6Q406 8 408 112V618Q408 634 392 634H28Q12 634 12 618ZM56 182H364V490H56Z"/>'
      + '<rect x="42" y="168" width="336" height="336" rx="16" fill="none" stroke="#d98c4e" stroke-width="12"/><rect x="42" y="168" width="336" height="336" rx="16" fill="none" stroke="#2e1606" stroke-width="3"/>'
      + '<rect x="44" y="20" width="332" height="120" rx="34" fill="#4a1020" stroke="#d98c4e" stroke-width="5"/>'
      + '<rect x="64" y="512" width="292" height="62" rx="12" fill="#d98c4e" stroke="#2e1606" stroke-width="3"/><rect x="76" y="522" width="268" height="42" rx="6" fill="#1a0f07"/>'
      + SPOTS.map((p) => '<circle cx="' + p[0].toFixed(1) + '" cy="' + p[1].toFixed(1) + '" r="7" fill="#24100c" stroke="#c99a3a" stroke-width="2"/>').join('') + '</svg>',
    handle: '<svg viewBox="0 0 60 200"><rect x="-6" y="158" width="34" height="28" rx="7" fill="#d9a92f" stroke="#2e1606" stroke-width="3"/>'
      + '<g class="arm"><path d="M30 172V40" stroke="#2e1606" stroke-width="12" stroke-linecap="round"/><path d="M30 172V40" stroke="#cfd6dc" stroke-width="6" stroke-linecap="round"/>'
      + '<g class="knob"><circle cx="30" cy="27" r="19" fill="#e5533d" stroke="#3a0d06" stroke-width="3"/><ellipse cx="23" cy="19" rx="6" ry="4" fill="#fff" opacity=".7"/></g></g>'
      + '<circle cx="30" cy="172" r="15" fill="#d9a92f" stroke="#2e1606" stroke-width="3"/></svg>',
    bulb: '<svg viewBox="0 0 20 20"><circle cx="10" cy="10" r="8.6" fill="#b5762e" stroke="#2e1606" stroke-width="1.4"/><g class="lit"><circle cx="10" cy="10" r="10" fill="#ffd27a" opacity=".45"/><circle cx="10" cy="10" r="5.8" fill="#fff1b8"/></g></svg>',
    card: (s) => s === 'back'
      ? '<svg viewBox="0 0 70 100"><rect x="2" y="2" width="66" height="96" rx="8" fill="#7a1a10" stroke="#f4c542" stroke-width="3"/><rect x="8" y="8" width="54" height="84" rx="5" fill="none" stroke="#f4c542" stroke-width="1.5" stroke-dasharray="4 4"/></svg>'
      : '<svg viewBox="0 0 70 100"><rect x="2" y="2" width="66" height="96" rx="8" fill="#fbf4e2" stroke="#2e1606" stroke-width="3"/><text x="35" y="64" text-anchor="middle" font-size="44" fill="' + (s === 'r' ? '#d4312a' : '#1c1c1c') + '">' + (s === 'r' ? '♥' : '♠') + '</text></svg>',
    fortune: '<div class="gl-ph-fortune"><span>🔮</span><b>ворожка</b></div>',
  };
  const ex = (ctx, name, ...a) => ctx.extra(name, ...a) || (typeof PH[name] === 'function' ? PH[name](...a) : PH[name]);
  const layOf = (ctx) => (ctx.art && ctx.art.extras && ctx.art.extras.layout) || LAY;
  const spotsOf = (ctx) => (ctx.art && ctx.art.extras && ctx.art.extras.bulbSpots) || SPOTS;

  // Лампочки: 4 фази-шари (HTML, анімується лише opacity шару — дешево); сусідні лампочки — у сусідніх фазах, тож вогник «біжить»
  function bulbsHtml(ctx) {
    const svg = ex(ctx, 'bulb'), spots = spotsOf(ctx);
    const order = spots.map((p, i) => [Math.atan2(p[1] - 330, p[0] - 210), i]).sort((a, b) => a[0] - b[0]).map((x) => x[1]);
    const ph = [[], [], [], []];
    order.forEach((idx, n) => ph[n % 4].push(spots[idx]));
    return '<div class="gl-bulbs">' + ph.map((list, k) => '<div class="gl-phase on" style="--k:' + k + '">' + list.map(([x, y]) =>
      '<div class="gl-bulb on" style="left:' + (x - 9).toFixed(1) + 'px;top:' + (y - 9).toFixed(1) + 'px">' + svg + '</div>').join('') + '</div>').join('') + '</div>';
  }
  function digitsHtml(n) {
    let h = '';
    for (let i = 0; i < n; i++) h += '<span class="gl-dw"><span class="gl-dc">' + '0123456789'.split('').map((d) => '<i>' + d + '</i>').join('') + '</span></span>';
    return '<div class="gl-digits">' + h + '</div>';
  }
  function setDigits(ctx, v) {
    const ws = ctx.gl && ctx.gl.digits; if (!ws) return;
    const s = String(Math.max(0, Math.floor(v))).padStart(ws.length, ' ').slice(-ws.length);
    ws.forEach((w, i) => {
      const ch = s[i], d = ch === ' ' ? 0 : Number(ch);
      w.classList.toggle('off', ch === ' ');
      w.firstChild.style.transform = 'translateY(' + (-d * 10) + '%)';
    });
  }
  function boardHtml(ctx) {
    const rows = ['glek', 'seven', 'horseshoe', 'bell', 'melon'];
    return '<div class="gl-board"><div class="gl-board-t">що платить</div>' + rows.map((k) =>
      '<div class="gl-board-r"><span class="gl-board-s">' + [0, 1, 2].map(() => '<span class="sk-cell">' + ctx.symHtml(k) + '</span>').join('') + '</span><b data-k="' + k + '"></b></div>').join('')
      + '<div class="gl-board-n">Глек — дикий, замінює всіх</div></div>';
  }
  function updBoard(ctx) {
    ctx.area.querySelectorAll('.gl-board b[data-k]').forEach((b) => { b.textContent = SK.fmt(PAY[b.dataset.k][3] * ctx.bet / 5); });
  }

  // ---------- ручка: стрижень стискається до точки кріплення (наче хилиться до нас), кулька їде вниз ----------
  function wireHandle(ctx, el) {
    const arm = el.querySelector('.arm'), knob = el.querySelector('.knob');
    const parts = arm ? Array.from(arm.children).filter((n) => n !== knob && !n.contains(knob)) : [];
    let px = 30, py = 172, ky = 27;
    try {
      let y0 = Infinity, y1 = -Infinity, x0 = Infinity, x1 = -Infinity;
      parts.forEach((n) => { const b = n.getBBox(); y0 = Math.min(y0, b.y); y1 = Math.max(y1, b.y + b.height); x0 = Math.min(x0, b.x); x1 = Math.max(x1, b.x + b.width); });
      if (isFinite(y1)) { py = y1; px = (x0 + x1) / 2; }
      if (knob) { const b = knob.getBBox(); ky = b.y + b.height / 2; }
    } catch (e) { /* без розмірів — беремо числа з контракту */ }
    if (arm) arm.style.transform = 'none';
    parts.forEach((n) => { n.style.transformBox = 'view-box'; n.style.transformOrigin = px + 'px ' + py + 'px'; });
    if (knob) { knob.style.transformBox = 'view-box'; knob.style.transformOrigin = px + 'px ' + ky + 'px'; }
    const len = py - ky;
    const set = (p) => {
      const s = 1 - 1.7 * p;
      parts.forEach((n) => { n.style.transform = 'scaleY(' + s.toFixed(3) + ')'; });
      if (knob) knob.style.transform = 'translateY(' + (len * (1 - s)).toFixed(1) + 'px) scale(' + (1 + 0.22 * p).toFixed(3) + ')';
    };
    const spring = () => { el.classList.remove('drag', 'snap'); el.classList.add('spring'); set(0); };
    let drag = null;
    ctx.gl.pullAnim = () => {
      if (drag) return;
      el.classList.remove('spring'); el.classList.add('snap'); set(1);
      ctx.sound('lever');
      ctx.timeout(spring, 170);
    };
    ctx.listen(el, 'pointerdown', (e) => {
      if (ctx.busy) return;
      e.preventDefault(); el.setPointerCapture(e.pointerId);
      const r = el.getBoundingClientRect();
      drag = { y0: e.clientY, travel: r.height * 0.55, p: 0, t: performance.now() };
      el.classList.remove('spring', 'snap'); el.classList.add('drag');
    });
    ctx.listen(el, 'pointermove', (e) => {
      if (!drag) return;
      drag.p = Math.max(0, Math.min(1, (e.clientY - drag.y0) / drag.travel)); set(drag.p);
    });
    const up = () => {
      if (!drag) return;
      const d = drag; drag = null;
      const tap = d.p < 0.06 && performance.now() - d.t < 350;
      if (d.p > 0.6) { ctx.sound('lever'); spring(); ctx.gl.byHandle = true; ctx.spin(); }
      else if (tap) { ctx.gl.pullAnim(); ctx.gl.byHandle = true; ctx.timeout(() => ctx.spin(), 120); }
      else spring();
    };
    ctx.listen(el, 'pointerup', up); ctx.listen(el, 'pointercancel', up);
  }

  // ---------- Ворожка ----------
  function gambleButton(ctx) {
    clearGambleBtn(ctx);
    if (!ctx.lastWin || ctx.auto) return;
    const b = document.createElement('button'); b.className = 'gl-gamble-btn';
    b.innerHTML = '<span>🔮</span> ворожка ×2';
    b.addEventListener('click', () => openGamble(ctx));
    ctx.hudExtra.appendChild(b);
  }
  function clearGambleBtn(ctx) { ctx.hudExtra.querySelectorAll('.gl-gamble-btn').forEach((b) => b.remove()); }
  function openGamble(ctx) {
    if (ctx.busy || !ctx.lastWin) return;
    clearGambleBtn(ctx);
    // srv — сайт: карту тягне сервер, гроші рухає він же (ctx.api.gamble/collect), локально не списуємо й не нараховуємо
    const srv = ctx.api && ctx.api.gamble ? ctx.api : null;
    // resume — Ворожка, що лишилась відкритою на сервері (перезавантаження): та сама сума, кроки й історія
    const rs = ctx.glGamble; ctx.glGamble = null;
    const stake0 = ctx.lastWin; let stake = stake0, round = (rs && rs.steps) || 0, open = true; const MAX = 5;
    const hist = rs && rs.hist ? rs.hist.slice(0, 6) : [];   // сервер: новіші спершу
    if (!srv) ctx.addBalance(-stake0);
    ctx.busy = true; ctx.updateHud(); ctx.clearWin();
    const cab = ctx.area.querySelector('.gl-cab-in');
    const ov = document.createElement('div'); ov.className = 'gl-fortune sk-noskip';
    ov.innerHTML = '<div class="gl-f-who">' + ex(ctx, 'fortune') + '<div class="gl-f-say">червона чи чорна?</div></div>'
      + '<div class="gl-f-main"><div class="gl-f-card"><div class="gl-f-face gl-f-back">' + ex(ctx, 'card', 'back') + '</div><div class="gl-f-face gl-f-front"></div></div>'
      + '<div class="gl-f-stake">на кону <b class="gl-f-v"></b><span class="gl-f-next"></span></div>'
      + '<div class="gl-f-btns"><button class="gl-f-r">♥ червона</button><button class="gl-f-b">♠ чорна</button></div>'
      + '<button class="gl-f-take">забрати <b></b></button>'
      + '<div class="gl-f-hist"></div><div class="gl-f-left"></div></div>';
    cab.appendChild(ov);
    requestAnimationFrame(() => ov.classList.add('in'));
    ctx.say(SK.rnd.pick(SAY.gamble), 2500);
    const $ = (s) => ov.querySelector(s);
    const card = $('.gl-f-card'), front = $('.gl-f-front');
    let lock = false;
    const upd = () => {
      $('.gl-f-v').textContent = SK.fmt(stake) + ' 🏺';
      $('.gl-f-next').textContent = round < MAX ? ' → вгадаєш: ' + SK.fmt(stake * 2) : '';
      $('.gl-f-take b').textContent = SK.fmt(stake);
      $('.gl-f-left').textContent = round < MAX ? 'ще можна ' + (MAX - round) + ' ' + (MAX - round === 1 ? 'раз' : MAX - round < 5 ? 'рази' : 'разів') : 'більше не можна';
      $('.gl-f-hist').innerHTML = hist.map((s) => '<span class="' + s + '">' + (s === 'r' ? '♥' : '♠') + '</span>').join('');
      setDigits(ctx, stake);
    };
    const close = (take) => {
      if (srv && take && open) srv.collect();
      if (take && stake) { if (!srv) ctx.addBalance(stake); ctx.sound('coin'); ctx.meter = stake; ctx.emit('meter', stake); ctx.root.querySelector('.sk-win-v').textContent = SK.fmt(stake); }
      ov.classList.remove('in'); ov.classList.add('out');
      ctx.timeout(() => { ov.remove(); ctx.busy = false; ctx.lastWin = 0; ctx.updateHud(); setDigits(ctx, take ? stake : 0); }, 350);
    };
    const unlock = () => { lock = false; ov.querySelectorAll('button').forEach((b) => { b.disabled = false; }); };
    const pick = async (guess) => {
      if (lock) return; lock = true;
      ov.querySelectorAll('.gl-f-btns button, .gl-f-take').forEach((b) => { b.disabled = true; });
      let res, g = null;
      if (srv) {
        g = await srv.gamble(guess);
        if (!g || !g.card) { if (g && g.open === false) { stake = 0; open = false; close(false); } else unlock(); return; }
        res = g.card;
      } else res = Math.random() < 0.5 ? 'r' : 'b';
      front.innerHTML = ex(ctx, 'card', res);
      card.classList.add('flip'); ctx.sound('flip');
      ctx.timeout(() => {
        hist.unshift(res); if (hist.length > 6) hist.pop();
        if (g) { open = !!g.open; if (g.balance != null) ctx.setBalance(g.balance); }
        if (g ? g.ok : res === guess) {
          if (g) { stake = g.amount; round = g.steps; } else { stake *= 2; round++; } ctx.sound('win'); ctx.fx.at(card, { kind: 'coin', n: 18 + round * 6, speed: 520 });
          $('.gl-f-say').textContent = SK.rnd.pick(SAY.gwin); ov.classList.add('ok', 'win');
          upd();
          if (round >= MAX || !open) { ctx.timeout(() => close(true), 1300); return; }
          ctx.timeout(() => { card.classList.remove('flip'); ov.classList.remove('ok', 'win'); unlock(); }, 900);
        } else {
          stake = 0; ctx.sound('lose'); ov.classList.add('bad');
          $('.gl-f-say').textContent = SK.rnd.pick(SAY.glose); upd();
          ctx.timeout(() => close(false), 1500);
        }
      }, 650);
    };
    $('.gl-f-r').addEventListener('click', () => pick('r'));
    $('.gl-f-b').addEventListener('click', () => pick('b'));
    $('.gl-f-take').addEventListener('click', () => { if (!lock) { lock = true; close(true); } });
    upd();
  }

  SK.define({
    id: ID,
    title: 'Однорукий Глек',
    grid: { cols: 3, rows: 3 },
    spinStyle: 'reels',
    reels: REELS,
    lines: LINES,
    payUnit: 1 / 5,
    sounds: { win: 'bell' },
    paytable: [
      { key: 'glek', pays: PAY.glek, note: 'дикий — замінює всіх; три Глеки — найбільше' },
      { key: 'seven', pays: PAY.seven }, { key: 'horseshoe', pays: PAY.horseshoe }, { key: 'bell', pays: PAY.bell },
      { key: 'melon', pays: PAY.melon }, { key: 'plum', pays: PAY.plum }, { key: 'pear', pays: PAY.pear },
      { key: 'cherry', pays: PAY.cherry, note: 'дві вишні зліва — теж платять' },
    ],
    rules(ctx) {
      const t = (ctx && ctx.machine && ctx.machine.table && ctx.machine.table.sneeze) || SNEEZE, cs = t.cells || {};
      const parts = Object.keys(cs).sort().map((k) => k + ' — ' + String(cs[k]).replace('.', ',') + ' %').join(', ');
      return '<b>5 ліній</b>, ставка ділиться між ними порівну. Платить однаковий ряд зліва направо від першого барабана; на кожній лінії — найбільший виграш. '
        + '<b>Дядько Глек</b> — дикий. Після виграшу — <b>ворожка</b>: вгадай колір карти — виграш ×2, до 5 разів; не вгадав — згорів. Ручку можна тягнути донизу.'
        + '<br><br><b>🤧 Глек чхнув.</b> ' + SK.esc(t.text || SNEEZE.text)
        + (parts ? ' Скільки клітинок чхає: ' + parts + '.' : '');
    },
    surprises: { sneeze: { title: 'Глек чхнув!', own: true } },
    say: HOST_SAY,
    steps: {
      // свій morph лише для чиху; інші перетворення (без why чи чужий why) — як у кіта
      async morph(s, ctx) { if (s.why === 'sneeze') return sneezeScene(s, ctx); return SK.steps.morph(s, ctx); },
    },
    spin(bet, state) { const stops = randStops(); return scriptFor(stops, bet, state, null, sneezeCells(gridOf(stops))); },
    demo: {
      'Малий виграш': demoBy((x) => x.m >= 1 && x.m < 3 && x.n === 1),
      'Дві лінії': demoBy((x) => x.n === 2 && x.m < 10),
      'Очікування': demoBy((x) => x.tease),
      'Великий занос': demoBy((x) => x.m >= 10 && x.m < 25),
      'Мега занос': demoBy((x) => x.m >= 25 && x.m < 50),
      'Епічний занос': demoBy((x) => x.m >= 50 && !x.g3),
      'Три Глеки': demoBy((x) => x.g3),
      'Ворожка': demoBy((x) => x.m >= 2 && x.m < 8, { gamble: true }),
      'Глек чхнув': demoSneeze(false),
      'Чих → виграш': demoSneeze(true),
    },
    _all: all, _evaluate: evaluate, _setPay: setPay,
    // сайт: view.gamble.open після перезавантаження — кнопка «Ворожка» знову з тією сумою
    _resumeGamble(ctx, g) {
      if (!g || !g.open || !(g.amount > 0) || ctx.busy) return;
      ctx.lastWin = g.amount; ctx.glGamble = g; setDigits(ctx, g.amount);
      gambleButton(ctx);
    },

    build(ctx) {
      const port = ctx.orient === 'port', L = layOf(ctx), W = L.window;
      ctx.gl = ctx.gl || {};
      // масштаб корпусу: висота зони, а на телефоні — ще й ширина (з ручкою, що виступає праворуч)
      const hx = L.handle.x - 30, k = Math.min((ctx.areaH - 12) / L.h, port ? (ctx.areaW - 12) / (hx + 60) : 9);
      const st = document.createElement('div'); st.className = 'gl-stage'; ctx.area.appendChild(st);
      const glass = ctx.extra('glass');
      const box = (r, cls, html) => '<div class="' + cls + '" style="left:' + r.x + 'px;top:' + r.y + 'px;width:' + r.w + 'px;height:' + r.h + 'px">' + (html || '') + '</div>';
      st.innerHTML = (port ? '' : boardHtml(ctx))
        + '<div class="gl-cab" style="width:' + ((hx + 60) * k).toFixed(1) + 'px;height:' + (L.h * k).toFixed(1) + 'px"><div class="gl-cab-in" style="width:' + L.w + 'px;height:' + L.h + 'px;transform:scale(' + k.toFixed(4) + ')">'
        + '<div class="gl-cab-bg">' + ex(ctx, 'cabinet') + '</div>'
        + box(W, 'gl-win', '<div class="gl-reelhost"></div>' + (glass ? '<div class="gl-glass">' + glass + '</div>' : ''))
        + box(L.logo, 'gl-logo', ctx.logoHtml())
        + box(L.tablo, 'gl-tablo', '<span class="gl-tablo-l">виграш</span>' + digitsHtml(6))
        + bulbsHtml(ctx)
        + '<div class="gl-handle" style="left:' + hx + 'px;top:' + (L.handle.y - 172) + 'px" title="тягни донизу">' + ex(ctx, 'handle') + '</div>'
        + '</div></div>';
      st.querySelector('.gl-win').style.borderRadius = (W.r || 10) + 'px';
      ctx.makeReels(st.querySelector('.gl-reelhost'), { cols: 3, rows: 3, height: W.h, cellW: (W.w - 8) / 3, gap: 4, style: 'cylinder', curve: 50, strips: REELS });
      ctx.gl.digits = Array.from(st.querySelectorAll('.gl-dw'));
      setDigits(ctx, ctx.meter || 0);
      wireHandle(ctx, st.querySelector('.gl-handle'));
      updBoard(ctx);
      if (!ctx.gl.greeted) { ctx.gl.greeted = true; ctx.timeout(() => ctx.say(SK.rnd.pick(SAY.idle), 4000), 600); }
    },
    onBet(ctx) { updBoard(ctx); },
    onMeter(ctx, v) { setDigits(ctx, v); },
    onSpinStart(ctx) {
      clearGambleBtn(ctx);
      const st = ctx.area.querySelector('.gl-stage'); if (st) st.classList.remove('gl-winning', 'gl-big');
      ctx.area.querySelectorAll('.gl-snz').forEach((e) => e.classList.remove('gl-snz'));
      ctx.area.querySelectorAll('.gl-sz, .gl-pchyh').forEach((e) => e.remove());
      if (!ctx.gl.byHandle && ctx.gl.pullAnim) ctx.gl.pullAnim();
      ctx.gl.byHandle = false;
    },
    onTease(ctx) { if (Math.random() < 0.4) ctx.say(SK.rnd.pick(SAY.tease), 1800); const st = ctx.area.querySelector('.gl-stage'); if (st) st.classList.add('gl-tease'); },
    onReelStop(ctx, c) { if (c === 2) { const st = ctx.area.querySelector('.gl-stage'); if (st) st.classList.remove('gl-tease'); } },
    onWin(ctx, step) {
      const st = ctx.area.querySelector('.gl-stage'); if (st) st.classList.add('gl-winning');
      // репліка на кожен виграш глушила Глека-ведучого: своя — лише на три Глеки, решту каже кіт зі своїм тротлінгом
      const g3 = (step.items || []).some((it) => it.sym === WILD && it.cells.length === 3);
      if (g3) ctx.say(SK.rnd.pick(SAY.g3), 3000);
    },
    onBigwin(ctx) { const st = ctx.area.querySelector('.gl-stage'); if (st) st.classList.add('gl-big'); },
    onSpinEnd(ctx, script) {
      if (!script) return;
      if (script.win > 0) {
        gambleButton(ctx);
        if (script.gamble) ctx.timeout(() => openGamble(ctx), 500);
      }
    },
  });
})();
