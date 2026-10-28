// «Хутори» — стенд-макети вигляду (dev/look.html). Не прод.
// «Було» — справжня картка: прод-модуль khutory.js монтується на підробному виді (fixture.js) поза екраном, і потрібний
// шматок DOM клонується в розділ. «Стало» — свій HTML поверх тих самих видів; поле — пласка карта board2d.js (і 3D-сцена в
// розділі «Підсвітка»). Прод-файли лише читаються.
import * as C from '/games/khutory/consts.js';
import * as K from '/games/khutory/hud-calc.js';
import { makeView } from '/games/khutory/dev/fixture.js';
import { createBoard2d } from '/games/khutory/board2d.js';

const $ = (s, r = document) => r.querySelector(s);
const q = new URLSearchParams(location.search);
const ONLY = q.get('only');
const PHONE = q.get('w') === 'phone';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const RI = { wood: '🪵', clay: '🧱', wool: '🧶', grain: '🌾', ore: '⛏️' };
const SHAPE = ['●', '▲', '■', '◆'];
const pl = (v, s) => v.players.find((p) => p.seat === s) || { seat: s, color: s, nick: 'місце ' + (s + 1) };
const col = (v, s) => C.SEAT_COLOR[pl(v, s).color];
const nick = (v, s) => pl(v, s).nick;
const dot = (v, s) => '<i style="--pc:' + col(v, s) + '">' + SHAPE[pl(v, s).color] + '</i>';

// ─── Справжня картка поза екраном ─────────────────────────────────────────
const mod = window.HGames.mods.khutory;
async function real(view, width) {
  const box = document.createElement('div');
  box.style.width = width + 'px';
  const root = document.createElement('div');
  box.appendChild(root);
  $('#lk-hold').appendChild(box);
  const seat = view.me ? view.me.seat : null;
  const ctx = {
    room: { id: 'khu-look', status: view.phase === 'over' ? 'done' : 'playing' },
    seat, view, me: { nick: 'Smaug' }, playing: view.phase !== 'over', mine: seat !== null, myTurn: seat !== null && view.turn === seat,
    act: () => Promise.resolve({ ok: true }), toast() {}, esc, seatName: (i) => 'місце ' + i,
    nickOf: (i) => nick(view, i), css: () => '', ui: window.HGames.ui,
  };
  mod.mount(root, ctx);
  for (let i = 0; i < 120 && !root.querySelector('.khu-pl'); i++) await sleep(50);
  await sleep(400);
  return {
    root,
    part(sel) { const n = root.querySelector(sel); return n ? n.cloneNode(true) : document.createElement('div'); },
    done() { try { mod.unmount(root); } catch (e) { /* стенд */ } box.remove(); },
  };
}
function khu(node, width) {
  const w = document.createElement('div');
  w.className = 'khu lk-real';
  if (width) w.style.width = width + 'px';
  w.appendChild(node);
  return w;
}

// ─── Пласке поле з накладкою ──────────────────────────────────────────────
async function field(host, view, legal) {
  host.classList.add('lk-field');
  const b = await createBoard2d(host, { reducedMotion: true });
  b.setBoard(view.board); b.setState(view);
  if (legal) b.setLegal(legal);
  const ov = document.createElement('div');
  ov.className = 'lk-ov';
  host.appendChild(ov);
  return { b, ov };
}
function follow(host, draw) {
  const go = () => requestAnimationFrame(draw);
  go();
  if (window.ResizeObserver) new ResizeObserver(go).observe(host);
  setTimeout(go, 300);
}
const div = (cls, html) => { const d = document.createElement('div'); if (cls) d.className = cls; if (html != null) d.innerHTML = html; return d; };
const hexName = (v, h) => { const x = v.board.hexes[h]; return C.TERRAIN_NAME[x.t] + (x.num ? ' ' + x.num : ''); };

// ═══ 1. Пропозиції ════════════════════════════════════════════════════════
async function secOffers(sec) {
  const v = makeView('trade', { seat: 1 });
  v.offers = [{ id: 7, from: 0, to: null, give: { grain: 1 }, get: { wood: 1 }, acc: [], dec: [] }];
  const r = await real(v, 359);
  const mk = () => {
    const ph = div('lk-phone'), scr = div('lk-scr');
    scr.appendChild(khu(r.part('.khu')));
    ph.appendChild(scr);
    return ph;
  };
  const before = mk(), after = mk();
  r.done();
  sec.querySelector('[data-before]').appendChild(before);
  requestAnimationFrame(() => {
    const o = before.querySelector('.khu-offers'), top = before.getBoundingClientRect().top;
    const y = o ? Math.round(o.getBoundingClientRect().top - top) : 0;
    before.appendChild(div('lk-fold', '📨 пропозиція Smaug — нижче краю екрана, на ' + y + ' px; за 10 с її знімуть'));
  });
  after.querySelectorAll('.khu-mid').forEach((b) => { if (/Торгувати/.test(b.textContent)) b.insertAdjacentHTML('beforeend', '<span class="lk-badge">1</span>'); });
  const hand = v.me.hand;
  after.appendChild(div('lk-inc', incHtml(v, hand)));
  sec.querySelector('[data-after]').appendChild(after);
}
function incHtml(v, hand) {
  return '<div class="lk-inc-h"><span class="bell">🔔</span> ' + dot(v, 0).replace('<i ', '<i class="khu-pdot" ') + '<b>' + esc(nick(v, 0)) + '</b>&nbsp;пропонує обмін<span class="sec">7 с</span></div>'
    + '<div class="lk-deal"><span class="side"><small>отримаєш</small>🌾 1</span><span class="arr">⇄</span><span class="side"><small>віддаси</small>🪵 1</span></div>'
    + '<div class="lk-inc-left">у тебе: 🪵 ' + hand.wood + ' → ' + (hand.wood - 1) + ' · 🌾 ' + hand.grain + ' → ' + (hand.grain + 1) + '</div>'
    + '<div class="lk-inc-b"><button type="button" class="primary">✅ Згоден</button><button type="button">❌ Ні</button><button type="button">↩ Інакше</button></div>'
    + '<div class="lk-bar"><i></i></div>';
}

// ═══ 2. Кармелюк ══════════════════════════════════════════════════════════
async function secRobber(sec) {
  const v = makeView('robber', { seat: 0 });
  const legal = { kind: 'robber', h: v.me.robber };
  const fb = div(); sec.querySelector('[data-before]').appendChild(fb);
  await field(fb, v, legal);
  fb.insertAdjacentHTML('afterend', '<p class="lk-note">Наведи мишею — підказка «Кармелюк тут зачепить: …». На телефоні наведення нема.</p>');

  const fa = div(); sec.querySelector('[data-after]').appendChild(fa);
  const { b, ov } = await field(fa, v, legal);
  const me = v.me.seat;
  const info = legal.h.map((h) => {
    const hx = v.board.hexes[h];
    const on = v.towns.filter((t) => hx.v.includes(t.v));
    const own = on.some((t) => t.seat === me);
    const vict = [...new Set(on.filter((t) => t.seat !== me).map((t) => t.seat))];
    const score = (C.PIPS[hx.num] || 0) * vict.length + vict.reduce((s, x) => s + pl(v, x).points, 0) * 0.3;
    return { h, own, vict, score: own ? -1 : score };
  });
  const best = info.reduce((a, x) => (x.score > a.score ? x : a), { score: 0 });
  const draw = () => {
    ov.innerHTML = '';
    for (const x of info) {
      const p = b.screenPos('h', x.h);
      if (!p) continue;
      let h = '';
      if (x.h === best.h) h += '<span class="best">⭐ Глек радить</span>';
      if (x.own) h += '<span class="own">⚠ твоя</span>';
      if (x.vict.length) h += '<span class="v">' + x.vict.map((s) => dot(v, s) + pl(v, s).cards).join(' ') + '</span>';
      else if (!x.own) h += '<span class="none">нікого</span>';
      const el = div('lk-hx', h);
      const c0 = b.screenPos('v', v.board.hexes[x.h].v[0]), R = c0 ? Math.hypot(c0.x - p.x, c0.y - p.y) : 30;
      el.style.left = p.x + 'px'; el.style.top = (p.y + R * 0.62) + 'px';
      ov.appendChild(el);
    }
  };
  follow(fa, draw);
  const own = info.find((x) => x.own);
  fa.insertAdjacentHTML('afterend', '<div class="lk-ask" style="position:relative;left:auto;top:auto;transform:none;margin:12px auto 0">'
    + '<b>⚠ Тут стоїть і твоя хата</b><p>Кармелюк на «' + esc(hexName(v, own ? own.h : 0)) + '» — і ти сам не отримуватимеш звідси карт, поки його не проженуть.</p>'
    + '<div class="row"><button type="button">Все одно сюди</button><button type="button" class="primary">Обрати інший</button></div></div>'
    + '<p class="lk-note">Цифра біля кружечка — скільки карт у руці жертви. Кружечок — колір і фігура гравця.</p>');
}

// ═══ 3. Підсвітка на 3D ═══════════════════════════════════════════════════
async function secGlow(sec) {
  let createScene;
  try { ({ createScene } = await import('/games/khutory/scene.js')); } catch (e) { sec.querySelector('[data-after]').textContent = '3D не завантажилось'; return; }
  const v = makeView('main', { seat: 0 });
  const legal = { kind: 'road', e: v.me.spots.road };
  const mk = async (slot) => {
    const host = div('lk-field'); sec.querySelector(slot).appendChild(host);
    const sc = await createScene(host, { quality: 'high', reducedMotion: true });
    sc.setBoard(v.board); sc.setState(v); sc.setLegal(legal); sc.resize();
    return { host, sc };
  };
  await mk('[data-before]');
  const { host, sc } = await mk('[data-after]');
  const ov = div('lk-ov lk-glow'); host.appendChild(ov);
  const NS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(NS, 'svg'); ov.appendChild(svg);
  const draw = () => {
    let h = '';
    for (const e of legal.e) {
      const [a, c] = v.board.edges[e];
      const A = sc.screenPos('v', a), B = sc.screenPos('v', c);
      if (!A || !B) continue;
      const k = 0.2, x1 = A.x + (B.x - A.x) * k, y1 = A.y + (B.y - A.y) * k, x2 = B.x - (B.x - A.x) * k, y2 = B.y - (B.y - A.y) * k;
      const xy = ' x1="' + x1 + '" y1="' + y1 + '" x2="' + x2 + '" y2="' + y2 + '"/>';
      h += '<line class="g"' + xy + '<line class="k"' + xy + '<line class="y"' + xy + '<line class="c"' + xy;
    }
    if (svg._h !== h) { svg._h = h; svg.innerHTML = h; }
  };
  setInterval(draw, 150);
  draw();
}

// ═══ 4. Плашки гравців ════════════════════════════════════════════════════
async function secPlates(sec) {
  const v = makeView('main', { seat: 0 });
  v.deadline = new Date(Date.now() + 8000).toISOString();
  const r1 = await real(v, 1100);
  const bw = sec.querySelector('[data-before]');
  bw.appendChild(khu(r1.part('.khu-players')));
  r1.done();
  const r2 = await real(v, 359);
  bw.appendChild(div('', '<p class="lk-note">Телефон: плашки в ряд, решта виїжджає вбік ⇆</p>'));
  bw.appendChild(khu(r2.part('.khu-players'), 359));
  r2.done();
  bw.appendChild(div('lk-note', 'Значення 🂠 / 📜 / ⚔ — лише в підказці мишею (title).'));

  const aw = sec.querySelector('[data-after]');
  aw.appendChild(div('lk-pls', v.players.map((p) => plate(v, p, false)).join('')));
  aw.appendChild(div('', '<p class="lk-note">Телефон (375 px):</p>'));
  const ph = div('lk-pls phone', v.players.map((p) => plate(v, p, true)).join(''));
  ph.style.maxWidth = '359px';
  aw.appendChild(ph);
  aw.appendChild(div('lk-legend', '<span>⭐ очки до перемоги</span><span>🂠 карт у руці</span><span>📜 грамот</span><span>🛤 шлях до «Чумацького шляху»</span><span>⚔ козаків до «Сотні»</span><span>●▲■◆ — фігура гравця</span>'));
  sec.appendChild(palette());
}
function plate(v, p, phone) {
  const cur = v.turn === p.seat, mine = v.me && v.me.seat === p.seat;
  const pts = mine ? v.me.points : p.points;
  const L = v.longest, A = v.army;
  const roadNeed = Math.max(5, L && L.seat !== p.seat ? L.len + 1 : 5), armyNeed = Math.max(3, A && A.seat !== p.seat ? A.n + 1 : 3);
  const bar = (n, need) => '<span class="t"><i style="width:' + Math.min(100, Math.round(n / need * 100)) + '%"></i></span>';
  const road = L && L.seat === p.seat ? '<span class="lk-aw has">🛤 ' + (phone ? '+2' : 'Шлях +2') + '</span>'
    : '<span class="lk-aw' + (p.road >= roadNeed - 1 ? ' near' : '') + '">🛤 ' + p.road + '/' + roadNeed + bar(p.road, roadNeed) + '</span>';
  const army = A && A.seat === p.seat ? '<span class="lk-aw has">⚔ +2</span>'
    : '<span class="lk-aw' + (p.knights >= armyNeed - 1 ? ' near' : '') + '">⚔ ' + p.knights + '/' + armyNeed + bar(p.knights, armyNeed) + '</span>';
  const late = cur ? 8 : 0;
  const cards = phone ? '⭐ <b>' + pts + '</b> · 🂠 <b>' + p.cards + '</b>' + (p.cards > 7 ? '<span style="color:var(--danger)">⚠</span>' : '') + ' · 📜 <b>' + p.dev + '</b>'
    : '🂠 <b>' + p.cards + '</b> ' + C.plural(p.cards, 'карта', 'карти', 'карт') + (p.cards > 7 ? ' <span style="color:var(--danger)">⚠ &gt;7</span>' : '')
      + ' · 📜 <b>' + p.dev + '</b> ' + C.plural(p.dev, 'грамота', 'грамоти', 'грамот');
  return '<div class="lk-pl' + (cur ? ' turn late' : '') + '" style="--pc:' + col(v, p.seat) + '">'
    + '<span class="lk-av">' + esc(String(p.nick)[0].toUpperCase()) + '<span class="shp">' + SHAPE[p.color] + '</span></span>'
    + '<span class="lk-pn">' + esc(p.nick) + (mine ? ' <em>(ти)</em>' : '') + (p.bot ? ' 🤖' : '') + '</span>'
    + (phone ? '' : '<span class="lk-pts">⭐ <b>' + pts + '</b>' + (phone ? '' : ' з ' + v.target) + '<span class="t"><i style="width:' + Math.round(pts / v.target * 100) + '%"></i></span></span>')
    + '<span class="lk-row">' + cards + '</span>'
    + '<span class="lk-row" style="grid-column:' + (phone ? '1 / -1' : '2') + '">' + road + army + '</span>'
    + (cur ? '<span class="lk-sec10">⏱ ' + late + ' с</span><span class="lk-tm"><i style="width:' + Math.round(late / 90 * 100) + '%"></i></span>' : '')
    + '</div>';
}
function palette() {
  const now = C.SEAT_COLOR, names = C.SEAT_COLOR_NAME;
  const prop = ['#d64541', '#2f6fd6', '#f1ece0', '#c86fb5'];
  const pn = ['червоні', 'сині', 'білі', 'рожеві'];
  const sw = (list, nm, f) => '<div class="sw" style="filter:' + f + '">' + list.map((c, i) => '<span style="background:' + c + (c === '#f1ece0' ? ';color:#222;text-shadow:none' : '') + '" title="' + nm[i] + '">' + SHAPE[i] + '</span>').join('') + '</div>';
  const row = (l, list, nm) => '<b>' + l + '</b>' + sw(list, nm, 'none') + sw(list, nm, 'url(#lk-deut)') + sw(list, nm, 'url(#lk-prot)');
  return div('', '<h3 style="margin:16px 0 0;font-size:1em">Кольори гравців очима дальтоніка (симуляція)</h3><div class="lk-pal"><span></span><span class="h">звичайний зір</span><span class="h">дейтеранопія (~5 % чоловіків)</span><span class="h">протанопія (~1 %)</span>'
    + row('Зараз: ' + names.join(', '), now, names) + row('Пропоную: ' + pn.join(', '), prop, pn) + '</div>'
    + '<p class="lk-note">Зараз червоні й помаранчеві для дейтеранопа — один бурий колір, сині й фіолетові близькі. Пропоную помаранчевих зробити <b>білими</b> (як у класичному Катані; дах — кремовий з темним кантом), фіолетових — <b>рожевими</b>; і головне — <b>фігура ●▲■◆</b> скрізь, де є колір.</p>');
}

// ═══ 6. Події на полі ═════════════════════════════════════════════════════
async function secEvents(sec) {
  const v = makeView('other', { seat: 0 });
  v.robber = 11;
  v.log = [...v.log, 'владік будує хату', 'владік міняє з банком 4 дерева на 1 залізо', 'владік ставить Кармелюка на глинище 8', 'владік забирає карту в Smaug'];
  const r = await real(v, 1100);
  const bw = sec.querySelector('[data-before]');
  const fb = div(); bw.appendChild(fb);
  await field(fb, v, null);
  bw.appendChild(khu(r.part('.khu-log')));
  r.done();

  const fa = div(); sec.querySelector('[data-after]').appendChild(fa);
  const { b, ov } = await field(fa, v, null);
  const town = v.towns.find((t) => t.seat === 1 && !t.city);
  const feed = div('lk-feed',
    '<div style="--pc:' + col(v, 1) + '">🥷 владік → глинище 8 · забрав карту в <b>тебе</b></div>'
    + '<div style="--pc:' + col(v, 1) + '">🤝 владік ⇄ банк: 4🪵 → 1⛏️</div>'
    + '<div class="old" style="--pc:' + col(v, 1) + '">🏠 владік: нова хата</div>');
  fa.appendChild(feed);
  const NS = 'http://www.w3.org/2000/svg';
  const draw = () => {
    ov.innerHTML = '';
    const P = b.screenPos('v', town.v), H0 = b.screenPos('h', 9), H1 = b.screenPos('h', 11);
    if (!P || !H0 || !H1) return;
    const rip = div('lk-rip'); rip.style.cssText = 'left:' + P.x + 'px;top:' + P.y + 'px;--pc:' + col(v, 1);
    const bub = div('lk-bub', '🏠 владік: нова хата'); bub.style.cssText = 'left:' + P.x + 'px;top:' + P.y + 'px;--pc:' + col(v, 1);
    const svg = document.createElementNS(NS, 'svg'); svg.setAttribute('class', 'lk-arrow');
    const mx = (H0.x + H1.x) / 2, my = Math.min(H0.y, H1.y) - 40;
    const d = 'M' + H0.x + ',' + H0.y + ' Q' + mx + ',' + my + ' ' + H1.x + ',' + (H1.y - 14);
    svg.innerHTML = '<path d="' + d + '"/><path class="c" d="' + d + '"/><circle cx="' + H1.x + '" cy="' + (H1.y - 14) + '" r="6" fill="#ffe27a"/>';
    const bub2 = div('lk-bub', '🥷 → глинище 8 · ↘ карта від тебе'); bub2.style.cssText = 'left:' + Math.min(H1.x, fa.clientWidth - 125) + 'px;top:' + (H1.y + 58) + 'px;--pc:#e2554a;border-left-color:#e2554a';
    ov.append(svg, rip, bub, bub2);
  };
  follow(fa, draw);
}

// ═══ 7. Числа ═════════════════════════════════════════════════════════════
async function secNumbers(sec) {
  const v = makeView('main', { seat: 0 });
  const r = await real(v, 1100);
  const bw = sec.querySelector('[data-before]');
  bw.appendChild(khu(r.part('.khu-dice')));
  bw.appendChild(div('lk-note', 'Лише останній кидок. Які числа годують мої хати — шукай сам на полі; статистика кидків — тільки в підсумку партії.'));
  const fb = div(); fb.style.marginTop = '8px'; bw.appendChild(fb);
  await field(fb, v, null);
  r.done();

  const me = v.me.seat, mine = v.towns.filter((t) => t.seat === me);
  const yieldOf = (n) => {
    const out = {};
    v.board.hexes.forEach((hx, i) => {
      if (hx.num !== n || i === v.robber) return;
      for (const t of mine) if (hx.v.includes(t.v)) { const res = C.TERRAIN_RES[hx.t]; out[res] = (out[res] || 0) + (t.city ? 2 : 1); }
    });
    return out;
  };
  let avg = 0;
  const cells = [];
  for (let n = 2; n <= 12; n++) {
    const y = n === 7 ? null : yieldOf(n);
    const cnt = y ? Object.values(y).reduce((a, b) => a + b, 0) : 0;
    avg += (C.PIPS[n] || 0) / 36 * cnt;
    cells.push({ n, y, cnt });
  }
  const cur = v.dice[0] + v.dice[1];
  const hist = [8, 5, 7, 10, 4, 9, 6, cur];
  const aw = sec.querySelector('[data-after]');
  const box = div('');
  const paint = (sel) => {
    box.innerHTML = '<div class="lk-numh"><span class="dice">🎲</span><b>' + v.dice[0] + ' + ' + v.dice[1] + ' = ' + cur + '</b><span class="muted">→ тобі: ' + (Object.entries(yieldOf(cur)).map(([k, n]) => RI[k] + (n > 1 ? '×' + n : '')).join(' ') || 'нічого') + '</span></div>'
      + '<div class="lk-nums' + (PHONE ? ' phone' : '') + '">' + cells.map((c) => {
        const p = C.PIPS[c.n] || 6;
        const y = c.n === 7 ? '🥷' : Object.entries(c.y).map(([k, n]) => RI[k] + (n > 1 ? '<sup>×' + n + '</sup>' : '')).join('');
        return '<button type="button" class="lk-num' + (c.cnt ? ' mine' : '') + (c.n === 7 ? ' seven' : '') + (c.n === cur ? ' cur' : '') + (c.n === sel ? ' sel' : '') + '" data-n="' + c.n + '">'
          + '<span class="n' + (c.n === 6 || c.n === 8 ? ' hot' : '') + '">' + c.n + '</span><span class="p">' + '<i></i>'.repeat(c.n === 7 ? 6 : p) + '</span><span class="y">' + (y || '<span class="muted">—</span>') + '</span></button>';
      }).join('') + '</div>'
      + '<div class="lk-hist">Останні кидки: ' + hist.map((n) => '<span class="' + (n === 7 ? 's7' : '') + (cells[n - 2].cnt ? ' my' : '') + '">' + n + '</span>').join('') + '</div>'
      + '<p class="lk-note">За кидок у середньому тобі ≈ <b>' + avg.toFixed(2).replace('.', ',') + ' карти</b> · за коло з 4 гравцями ≈ <b>' + (avg * 4).toFixed(1).replace('.', ',') + '</b>. Зелена рамка в кидках — числа, що тобі щось дали.</p>';
  };
  aw.appendChild(box);
  const fa = div(); fa.style.marginTop = '8px'; aw.appendChild(fa);
  const { b } = await field(fa, v, null);
  const pick = (n) => { paint(n); b.setLegal({ kind: 'robber', h: v.board.hexes.map((_, i) => i).filter((i) => v.board.hexes[i].num === n) }); };
  box.addEventListener('click', (e) => { const t = e.target.closest('[data-n]'); if (t) pick(+t.dataset.n); });
  pick(8);
}

// ═══ 8. Торг ══════════════════════════════════════════════════════════════
async function secTrade(sec) {
  const v = makeView('main', { seat: 0 });
  v.me.hand = { wood: 2, clay: 1, wool: 0, grain: 4, ore: 0 };
  v.me.can = { road: true, town: false, city: false, dev: false };
  v.me.why = { town: 'бракує 1 вовни', city: 'бракує 2 зерна й 3 заліза', dev: 'бракує 1 вовни й 1 заліза' };
  v.offers = [{ id: 3, from: 0, to: null, give: { clay: 1 }, get: { wool: 1 }, acc: [], dec: [1, 2, 3] }];
  const r = await real(v, 1100);
  const bw = sec.querySelector('[data-before]');
  // Права колонка картки шириною ~300 px усередині «широкої» (≥ 700 px) картки — як на ПК.
  const side = div('khu-side'); side.style.width = '300px';
  side.append(r.part('.khu-act'), r.part('.khu-offers'));
  bw.style.overflow = 'hidden';
  bw.appendChild(khu(side, 700));
  r.done();
  bw.appendChild(div('lk-note', '<b>Банк:</b> «🤝 Торгувати» → «4 → 1» → ресурс = 3 кліки, і лише по одному.<br><b>Коли ходить бот:</b> «Глек торгується сам — з ним обмін не відкриваємо».'));

  const rate = v.me.rates, hand = v.me.hand;
  const bank = C.RES.map((k) => {
    const n = rate[k], ok = hand[k] >= n;
    return '<span class="lk-chip' + (k === 'grain' ? ' on' : '') + (ok ? '' : ' off') + '">' + RI[k] + ' ' + n + '→1' + (n < 4 ? ' ⚓' : '') + '</span>';
  }).join('');
  const aw = sec.querySelector('[data-after]');
  aw.appendChild(div('lk-act',
    '<div class="lk-bgrid">'
    + '<div class="lk-b ok"><span class="bn">🛤 Шлях</span><span class="why">🪵🧱 — можна!</span></div>'
    + '<div class="lk-b"><span class="bn">🏠 Хата</span><span class="why">бракує 1 🧶</span><button type="button" class="fix">🔁 3🌾 → 🧶 і будуй</button></div>'
    + '<div class="lk-b"><span class="bn">🏛 Містечко</span><span class="why">бракує 3 ⛏️ (зерно є)</span></div>'
    + '<div class="lk-b"><span class="bn">📜 Грамота</span><span class="why">бракує 🧶 і ⛏️</span></div></div>'
    + '<div class="lk-bank"><div class="l">🏦 Банк — завжди під рукою: що віддаєш</div><div class="lk-chips">' + bank + '</div>'
    + '<div class="lk-pick">за 3🌾 бери: <button type="button">🪵</button><button type="button">🧱</button><button type="button" class="primary">🧶</button><button type="button">⛏️</button>'
    + ' <span class="muted">×1</span></div></div>'
    + '<div class="lk-offer"><b>🙅 Усі відмовились</b> від «даєш 1 глину → хочеш 1 вовну»<br><span class="muted small">❌ владік · ❌ микола · ❌ Глек</span>'
    + '<div class="row"><button type="button" class="primary">✏ Змінити</button><button type="button">🗑 Прибрати</button><button type="button">🏦 У банк</button></div></div>'
    + '<div class="lk-txt"><b>Тексти:</b><br>«дає <s>1 глина</s>» → «дає <ins>1 глину</ins>» (знахідний відмінок; так само «хоче 1 вовну»)<br>'
    + '«<s>Глек торгується сам — з ним обмін не відкриваємо</s>» → «<ins>🤖 Зараз ходить Глек — він міняє лише з банком. Твоя черга торгуватись — у твій хід</ins>»</div>'));
}

// ═══ 9. Підсумок ══════════════════════════════════════════════════════════
async function secOver(sec) {
  const v = makeView('over', { seat: 0 });
  const r = await real(v, 1100);
  sec.querySelector('[data-before]').appendChild(khu(r.part('.khu-sum')));
  r.done();
  const o = v.over, S = o.stats.seats;
  // Звання — чесний лідер у кожній номінації (у проді: не більше двох на людину, решта — не показувати).
  const top = (f) => S.reduce((a, x) => (f(x) > f(a) ? x : a));
  const card = (i, n, s, d, need) => '<div class="lk-ti' + (need ? ' need' : '') + '" style="--pc:' + (s == null ? 'var(--accent)' : col(v, s)) + '"><div class="i">' + i + '</div><div class="n">' + n + '</div>'
    + (s == null ? '' : '<div class="w">' + dot(v, s).replace('<i ', '<i class="khu-pdot" ') + '<b>' + esc(nick(v, s)) + '</b></div>') + '<div class="d">' + d + '</div></div>';
  const ro = top((x) => x.built.road), tr = top((x) => x.trades), st = top((x) => x.stolen), lo = S.reduce((a, x) => (x.got < a.got ? x : a));
  const di = o.stats.dice, nmax = di.indexOf(Math.max(...di)) + 2;
  // «Твій момент»: коло, де ти найбільше піднявся в таблиці (або найбільший приріст очок).
  const me = v.me.seat, tl = o.stats.timeline;
  const rank = (row, s) => 1 + row.filter((x) => x > row[s]).length;
  let mo = null;
  for (let i = 2; i < tl.length; i++) {
    const up = rank(tl[i - 1], me) - rank(tl[i], me), gain = tl[i][me] - tl[i - 1][me];
    const sc = up * 10 + gain;
    if (!mo || sc > mo.sc) mo = { i, up, gain, sc, place: rank(tl[i], me) };
  }
  const k = Math.round(o.stats.rounds * mo.i / (tl.length - 1));
  const aw = sec.querySelector('[data-after]');
  aw.appendChild(div('', '<div class="lk-win" style="--pc:' + col(v, o.winner) + '"><span class="cup">🏆</span><div><div class="muted small">Голова громади</div><b>'
    + dot(v, o.winner).replace('<i ', '<i class="khu-pdot" ') + esc(nick(v, o.winner)) + '</b> — ' + o.points[o.winner] + ' ⭐ за ' + o.stats.rounds + ' кіл</div>'
    + '<button type="button" class="primary" style="margin-left:auto">🔁 Ще раз</button></div>'
    + '<div class="lk-titles">'
    + card('🐂', 'Чумак року', ro.seat, ro.built.road + ' шляхів прокладено')
    + card('🤝', 'Ярмарковий голова', tr.seat, tr.trades + ' обмінів')
    + card('🙈', 'Кубики не люблять', lo.seat, 'лише ' + lo.got + ' карт з полів за партію')
    + card('🥷', 'Кармелюків побратим', st.seat, st.stolen + ' рази обібрав сусідів')
    + card('😿', 'Улюбленець Кармелюка', null, 'кого обкрадали найчастіше — сервер ще не рахує (нове поле <code>robbed</code>)', true)
    + card('🎲', 'Число партії: ' + nmax, null, 'випало ' + di[nmax - 2] + ' разів' + (nmax === 7 ? ' — Кармелюк не вилазив із хуторів' : ''))
    + '</div>'
    + '<div class="lk-moment">✨ <b>Твій момент</b>: коло ~' + k + ' — ' + (mo.up > 0 ? 'ти обійшов суперника й вийшов на ' + ['', 'перше', 'друге', 'третє', 'четверте'][mo.place] + ' місце' : '+' + mo.gain + ' ⭐ за коло')
    + '. Далі — ' + o.points[me] + ' ⭐, ' + (o.winner === me ? 'перемога!' : 'до перемоги бракувало ' + (v.target - o.points[me]) + '.') + '</div>'));
}

// ─── Запуск ───────────────────────────────────────────────────────────────
const SECS = { offers: secOffers, robber: secRobber, glow: secGlow, plates: secPlates, events: secEvents, numbers: secNumbers, trade: secTrade, over: secOver };
const nav = $('#lk-nav');
for (const s of document.querySelectorAll('.lk-sec')) {
  const a = document.createElement('a');
  a.href = '?only=' + s.id + (PHONE ? '&w=phone' : '');
  a.textContent = s.dataset.t;
  if (ONLY === s.id) a.className = 'on';
  nav.appendChild(a);
  if (ONLY && ONLY !== s.id) s.hidden = true;
}
const all = document.createElement('a'); all.href = '?'; all.textContent = 'усі разом'; nav.appendChild(all);
if (ONLY) { document.body.classList.add('lk-one'); $('.lk-intro').hidden = true; }
if (PHONE) for (const c of document.querySelectorAll('.lk-cols')) c.style.gridTemplateColumns = 'minmax(0,1fr)';
window.__lk = { done: false, errors: [] };
(async () => {
  for (const s of document.querySelectorAll('.lk-sec')) {
    if (s.hidden || !SECS[s.id]) continue;
    try { await SECS[s.id](s); } catch (e) { window.__lk.errors.push(s.id + ': ' + (e && e.stack || e)); console.error(e); }
  }
  window.__lk.done = true;
})();
