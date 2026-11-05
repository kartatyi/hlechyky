import { writeFileSync, readFileSync } from 'node:fs';
import { INK, CARGO, card, mark, PMARK, compass, cart } from './art.mjs';
import { fx, buildMap, orderHint, routesOut } from './map.mjs';

const D = '/private/tmp/claude-501/-Users-admin-olimp-glechyk/a2ec8d08-6007-4765-b570-a92fbb040f3d/scratchpad/sol/d1';
const CSS = readFileSync(D + '/src/ui.css', 'utf8');
const glek = readFileSync(D + '/assets/glek.svg', 'utf8').replace('<svg ', '<svg class="glek" ').replace(/id="clay"/g, 'id="clayG"').replace(/url\(#clay\)/g, 'url(#clayG)');

const ORD = ['red', 'blue', 'green', 'black', 'yellow', 'white', 'orange', 'pink', 'wild'];
const mock = fx.mock;
const handTotal = Object.values(mock.myHand).reduce((a, b) => a + b, 0);

// ─── спільні значки ──────────────────────────────────────────────────────────────────────────────────
const IC = {
  cart: `<svg viewBox="0 0 24 18" class="ic"><path d="M3 10C2 1 22 1 21 10Z" fill="currentColor"/><rect x="1.500" y="10" width="21" height="3" rx="1" fill="currentColor"/><circle cx="7" cy="14.500" r="3" fill="none" stroke="currentColor" stroke-width="1.700"/><circle cx="17" cy="14.500" r="3" fill="none" stroke="currentColor" stroke-width="1.700"/></svg>`,
  cards: `<svg viewBox="0 0 22 20" class="ic"><rect x="2.500" y="3" width="10" height="14" rx="1.500" transform="rotate(-12 7 10)" fill="none" stroke="currentColor" stroke-width="1.700"/><rect x="8" y="3" width="11" height="15" rx="1.500" transform="rotate(8 13 10)" fill="currentColor"/></svg>`,
  seal: `<svg viewBox="0 0 20 20" class="ic"><path d="M10 1.500C12 1 13 3 15 3.500 17 4 17.500 6 18 7.500 18.500 9.500 19 11 17.500 13 16.500 15 16 16.500 14 17.500 12 18.500 10.500 19 8.500 18 6.500 17.500 4.500 17.500 3 16 1.500 14.500 1.500 12.500 1.500 10.500 1 8.500 2 6.500 3.500 5 5 3.500 8 2 10 1.500Z" fill="currentColor"/><circle cx="10" cy="10" r="4.200" fill="none" stroke="rgba(0,0,0,.45)" stroke-width="1.500"/></svg>`,
  hour: `<svg viewBox="0 0 30 40" class="hg"><path d="M5 3H25M5 37H25" stroke="#3b2412" stroke-width="3.600" stroke-linecap="round"/><path d="M8 4C8 15 13 16 13.500 20 13 24 8 25 8 36H22C22 25 17 24 16.500 20 17 16 22 15 22 4Z" fill="#fff8e3" fill-opacity=".85" stroke="#3b2412" stroke-width="1.800"/><path d="M10.500 11C12 15 14 17 15 20 16 17 18 15 19.500 11Z" fill="#c98e2c"/><path d="M15 21V30" stroke="#c98e2c" stroke-width="1.200"/><path d="M10 35C10.500 30 13 28.500 15 28.500S19.500 30 20 35Z" fill="#c98e2c"/></svg>`,
};
const sealSvg = (txt, wax = '#a51d16', big = false) => `<svg class="sealart" viewBox="0 0 60 60" aria-hidden="true">
  <path d="M30 3C35 2 37 6 42 7 47 9 49 12 51 15 55 19 58 24 56 30 57 35 56 40 53 44 50 49 46 51 42 54 37 56 33 58 28 57 23 58 19 55 15 53 10 51 8 47 6 43 3 38 2 34 3 29 2 23 5 19 8 15 11 11 15 9 19 7 23 5 26 3 30 3Z" fill="${wax}" stroke="rgba(30,5,0,.65)" stroke-width="1.200"/>
  <circle cx="30" cy="30" r="19.500" fill="rgba(0,0,0,.16)" stroke="rgba(255,220,190,.35)" stroke-width="1.100"/>
  <path d="M14 22C18 13 26 9 34 10" fill="none" stroke="rgba(255,240,220,.55)" stroke-width="2" stroke-linecap="round"/>
  <text x="30" y="38.500" text-anchor="middle" font-size="25" font-family="Luminari,Georgia,serif" fill="#fff3df">${txt}</text></svg>`;

function waxAvatar(p, i, size = 40) {
  const initial = p.name.startsWith('Глек') ? '' : p.name[0];
  return `<span class="ava" style="--c:${p.color};width:${size}px;height:${size}px">
    <svg viewBox="0 0 60 60"><path d="M30 2C36 1 40 5 45 7 50 10 53 13 55 18 58 23 59 27 57 33 58 38 55 43 52 47 49 51 45 54 40 56 35 58 30 59 25 57 20 58 15 55 11 52 7 48 4 44 3 39 1 34 1 29 3 24 3 18 7 14 11 10 15 6 20 3 25 3 27 2 28 2 30 2Z" fill="${p.color}" stroke="rgba(20,8,0,.7)" stroke-width="1.300"/><circle cx="30" cy="30" r="21" fill="rgba(0,0,0,.14)" stroke="rgba(255,235,210,.4)" stroke-width="1"/></svg>
    ${initial ? `<b>${initial}</b>` : `<span class="gl">${glek}</span>`}
    <i class="pmk"><svg viewBox="-13 -13 26 26"><circle r="13" fill="#20140b"/>${PMARK[i]}</svg></i>
  </span>`;
}
const nameFmt = (n) => n.replace(/ ?🤖/, '').trim();
const bot = (n) => /🤖/.test(n) ? `<span class="bot" title="Автомат">🤖</span>` : '';

const fmtLog = (l) => {
  let who = mock.players.findIndex((p) => l.startsWith(p.name));
  const pName = who >= 0 ? mock.players[who].name : '';
  const rest = who >= 0 ? l.slice(pName.length) : l;
  return `<li><i class="dot" style="background:${who >= 0 ? mock.players[who].color : '#888'}"></i><span><b>${nameFmt(pName)}</b>${bot(pName)}${rest.replace(/\(\+(\d+)\)/, '<em>+$1</em>')}</span></li>`;
};

const hints = mock.myTickets.map((t) => ({ ...t, h: orderHint(t.a, t.b) }));

function orderSlip(t, i, phone) {
  const h = t.h;
  const pips = t.done ? '' : (() => {
    const total = h.done + h.left;
    let o = '';
    for (let k = 0; k < total; k++) o += `<i class="${k < h.done ? 'on' : ''}"></i>`;
    return `<span class="pips">${o}</span>`;
  })();
  const plural = (n, a, b, c) => (n % 10 === 1 && n % 100 !== 11 ? a : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 10 || n % 100 >= 20) ? b : c);
  return `<div class="slip ${t.done ? 'done' : ''}" style="--r:${[-.5, .4, -.3][i]}deg">
    ${sealSvg(t.pts, t.done ? '#3e7a44' : '#a51d16')}
    <div class="sliptxt">
      <div class="route">${t.a} <span class="dash">—</span> ${t.b}</div>
      ${t.done ? `<div class="hint ok">Виконано: шляхи зʼєднано</div>`
        : `<div class="hint">${pips}<span>ще ${h.left} ${plural(h.left, 'шлях', 'шляхи', 'шляхів')} · ${h.wagons} ${plural(h.wagons, 'віз', 'вози', 'возів')}</span></div>`}
    </div>
  </div>`;
}

// ─── Головні вузли ───────────────────────────────────────────────────────────────────────────────────
function costPips() {
  const tile = (k) => { const C = CARGO[k]; const wild = k === 'wild';
    return `<span class="cp"><svg viewBox="-13 -13 26 26"><rect x="-12.300" y="-12.300" width="24.600" height="24.600" rx="4" fill="${wild ? '#ecc659' : C.soft}" stroke="${INK}" stroke-width="1.300"/><g transform="scale(.82)">${mark(k, wild ? INK : C.on)}</g></svg></span>`; };
  return tile('green').repeat(4) + tile('wild') + `<span class="cp miss" title="Бракує"><svg viewBox="-13 -13 26 26"><g transform="scale(.82)">${mark('green', '#8e1a14')}</g></svg></span>`;
}

// ═══ PC ═════════════════════════════════════════════════════════════════════════════════════════════
const AVOID_PC = (s) => {
  const r = (x, y, w, h) => [35 + (x - 340) / s, 44 + (y - 14) / s, w / s, h / s];
  return [r(332, 574, 392, 330), r(1320, 0, 280, 165), r(732, 794, 280, 100), r(1490, 170, 110, 40), r(985, 268, 180, 62)];
};
const PC = (() => {
  const s = .3185;
  const vb = [35 - 340 / s, 44 - 14 / s, 1600 / s, 900 / s];
  const px = (x, y) => [340 + (x - 35) * s, 14 + (y - 44) * s];
  const map = buildMap({
    s, vb, sizes: [17.5, 15, 13.5, 12.5],
    selectedBanner: { dx: -60, dy: 128, rot: 17.5, w: 500, h: 80, fs: 50 },
    vp: { x: 35 - 8 / s, y: 0, w: 3970, h: 2800, avoid: AVOID_PC(s) },
    deco: {
      rose: [3610, 2480],
      seas: [['МОРЕ ЧОРНЕ', 2760, 2440 + 300, -3, 62], ['Азовське', 3180, 2115, 8, 46], ['море', 3275, 2170, 8, 46]],
      foreign: [['Литва', 1290, 118, -3, 52], ['Польща', 45, 395, -8, 52], ['Волощина', 1000, 1660, 18, 50], ['Московія', 3370, 640, 8, 54], ['Дон', 3880, 1660, 80, 46], ['Кубань', 3770, 2390, 18, 46]],
      lands: [['ПОЛІССЯ', 1140, 280, 2, 38], ['ГАЛИЧИНА', 330, 950, -28, 34], ['ПОДІЛЛЯ', 1210, 1270, 20, 34], ['ГЕТЬМАНЩИНА', 2130, 590, 15, 30], ['СЛОБОЖАНЩИНА', 2765, 735, 12, 22], ['ЗАПОРОЖЖЯ', 2600, 1720, 8, 30], ['ДИКЕ ПОЛЕ', 3090, 1610, 4, 30], ['БУДЖАК', 1520, 2240, -38, 30], ['ТАВРІЯ', 2590, 1965, 0, 26]],
      rivers: [['Дніпро', 2392, 1400, 62, 38], ['Дністер', 1450, 1640, 40, 34], ['Півд. Буг', 1660, 1460, 60, 32], ['Десна', 2230, 280, 18, 34], ['Сів. Донець', 3560, 1040, 18, 30], ['Прип’ять', 1010, 215, 6, 30], ['Дунай', 1160, 2510, 8, 34]],
      creatures: [['serpent', 1872, 2214, 1.6], ['whale', 3106, 2482, 1.3], ['chaika', 2130, 2440, 1.2]],
      extra: `<g transform="translate(3610 2480)">${compass(150)}</g>`,
    },
  });

  const players = mock.players.map((p, i) => `<div class="pl ${p.me ? 'me' : ''} ${mock.turn === p.name ? 'turn' : ''}">
      ${waxAvatar(p, i, 38)}
      <div class="plname"><b>${nameFmt(p.name)}</b>${bot(p.name)}${p.me ? '<small>ти</small>' : ''}</div>
      <div class="plpts">${p.points}</div>
      <div class="plst">${p.wagons}</div><div class="plst">${p.me ? handTotal : p.cards}</div><div class="plst">${p.tickets}</div>
    </div>`).join('');

  const hand = ORD.map((k) => {
    const n = mock.myHand[k];
    const need = k === 'green' || k === 'wild';
    const depth = Math.min(n, 4);
    return `<div class="hc ${n ? '' : 'zero'} ${need ? 'use' : ''} k-${k}" title="${CARGO[k].name}">
      ${n > 1 ? `<span class="pile" style="--n:${depth}"></span>` : ''}
      ${card(k)}
      <span class="cnt">${n}</span>
    </div>`;
  }).join('');

  const market = mock.market.map((k) => `<button class="mc ${k === 'green' ? 'want' : ''} k-${k}" aria-label="Узяти: ${CARGO[k].name}">${card(k)}${k === 'green' ? '<span class="wanttag">бери!</span>' : ''}</button>`).join('');

  return `<!doctype html><html lang="uk"><head><meta charset="utf-8"><title>Соляний шлях — ПК</title><style>${CSS}</style></head>
  <body class="pc">
  <main class="stage">
    <div class="mapwrap">${map}</div>
    <svg class="fx" width="1600" height="900" aria-hidden="true">
      <defs>
        <filter id="grain" x="0" y="0" width="100%" height="100%"><feTurbulence type="fractalNoise" baseFrequency=".85" numOctaves="3" seed="11"/><feColorMatrix values="0 0 0 0 .32  0 0 0 0 .2  0 0 0 0 .07  0 0 0 .55 -.17"/></filter>
        <filter id="fibres" x="0" y="0" width="100%" height="100%"><feTurbulence type="fractalNoise" baseFrequency=".012 .22" numOctaves="3" seed="4"/><feColorMatrix values="0 0 0 0 .36  0 0 0 0 .22  0 0 0 0 .08  0 0 0 .55 -.2"/></filter>
        <filter id="blot" x="0" y="0" width="100%" height="100%"><feTurbulence type="fractalNoise" baseFrequency=".0055" numOctaves="4" seed="21"/><feColorMatrix values="0 0 0 0 .42  0 0 0 0 .26  0 0 0 0 .08  1.300 0 0 0 -.62"/></filter>
        <filter id="stainw"><feTurbulence type="fractalNoise" baseFrequency=".05" numOctaves="3" seed="9" result="n"/><feDisplacementMap in="SourceGraphic" in2="n" scale="14"/></filter>
        <radialGradient id="vig" cx=".62" cy=".5" r=".72"><stop offset=".62" stop-color="#3a1f08" stop-opacity="0"/><stop offset="1" stop-color="#3a1f08" stop-opacity=".62"/></radialGradient>
        <linearGradient id="fold" x1="0" x2="1"><stop offset="0" stop-color="#3a1f08" stop-opacity="0"/><stop offset=".47" stop-color="#3a1f08" stop-opacity=".2"/><stop offset=".5" stop-color="#fff" stop-opacity=".38"/><stop offset=".56" stop-color="#3a1f08" stop-opacity=".06"/><stop offset="1" stop-color="#3a1f08" stop-opacity="0"/></linearGradient>
        <linearGradient id="foldh" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stop-color="#3a1f08" stop-opacity="0"/><stop offset=".47" stop-color="#3a1f08" stop-opacity=".2"/><stop offset=".5" stop-color="#fff" stop-opacity=".38"/><stop offset=".56" stop-color="#3a1f08" stop-opacity=".06"/><stop offset="1" stop-color="#3a1f08" stop-opacity="0"/></linearGradient>
      </defs>
      <g style="mix-blend-mode:multiply"><rect width="1600" height="900" filter="url(#blot)" opacity=".5"/><rect width="1600" height="900" filter="url(#grain)" opacity=".75"/><rect width="1600" height="900" filter="url(#fibres)" opacity=".5"/></g>
      <!-- згини -->
      <g style="mix-blend-mode:hard-light" opacity=".65"><rect x="948" y="0" width="30" height="900" fill="url(#fold)"/><rect x="332" y="432" width="1268" height="26" fill="url(#foldh)"/></g>
      <!-- плями -->
      <g filter="url(#stainw)" style="mix-blend-mode:multiply" fill="none">
        <circle cx="640" cy="64" r="46" stroke="#6d3f12" stroke-opacity=".34" stroke-width="5"/><circle cx="640" cy="64" r="46" fill="#7a4a18" fill-opacity=".05" stroke="none"/><circle cx="706" cy="548" r="9" fill="#2b1a0d" fill-opacity=".22" stroke="none"/><circle cx="690" cy="534" r="3.500" fill="#2b1a0d" fill-opacity=".25" stroke="none"/><circle cx="720" cy="530" r="2.500" fill="#2b1a0d" fill-opacity=".25" stroke="none"/>
      </g>
      <g style="mix-blend-mode:multiply"><rect width="1600" height="900" fill="url(#vig)"/></g>
    </svg>

    <!-- картуш із назвою -->
    <header class="cartouche">
      <svg class="stars" viewBox="0 0 250 34"><path d="M6 28C70 4 170 2 244 22" fill="none" stroke="#7a5a33" stroke-width=".6" stroke-dasharray="1 3"/><g fill="#6d4a1e">${[[14,26,1.3],[30,18,.9],[46,16,1.4],[62,10,1],[80,13,.8],[96,6,1.5],[112,8,1],[130,5,1.2],[150,7,.9],[168,6,1.5],[186,10,1],[204,12,1.2],[222,16,.9],[238,22,1.3],[72,18,.7],[120,14,.7],[176,16,.7]].map(([x,y,r])=>`<path d="M${x} ${y-r*2.2}l${r*.5} ${r*1.7} ${r*1.7} ${r*.5} -${r*1.7} ${r*.5} -${r*.5} ${r*1.7} -${r*.5} -${r*1.7} -${r*1.7} -${r*.5} ${r*1.7} -${r*.5}z"/>`).join('')}</g></svg>
      <h1>Соляний шлях</h1>
      <p>мапа чумацьких доріг, бродів і торгів,<br>накреслена зі слів бувалих людей</p>
      <svg class="cartoon" viewBox="-70 -30 140 54">${cart(26, '#c8a86a', '')}<g transform="translate(46 2)"><path d="M-14-10C-8-14 6-13 11-10L13 1 14 14H10L9 4C4 7-4 7-9 4L-10 14H-14Z" fill="#5d3519" stroke="${INK}" stroke-width="1.400"/><path d="M11-10 18-10 22-16M14-10 17-6" fill="none" stroke="${INK}" stroke-width="1.600"/><path d="M11-12C13-16 19-16 21-10 20-6 16-5 13-6Z" fill="#5d3519" stroke="${INK}" stroke-width="1.400"/></g></svg>
    </header>

    <!-- масштаб -->
    <div class="scale"><span>Верст</span><i></i><em>0</em><em style="left:50%">50</em><em style="left:100%">100</em></div>

    <!-- Ліва шкіряна панель -->
    <aside class="ledger">
      <section class="turnb">
        <div class="tt"><small>Хід ${'14'}</small><h2>Твій хід, Оля!</h2><p>Займи шлях, бери карти чи нове замовлення</p></div>
        <div class="timer">${IC.hour}<b>0:42</b></div>
        <div class="burn"><i style="width:62%"></i></div>
      </section>

      <section class="players">
        <div class="thead"><span class="t">Чумаки</span><span class="c" title="Очки">очки</span><span class="c" title="Вози лишилось">${IC.cart}</span><span class="c" title="Карти на руці">${IC.cards}</span><span class="c" title="Замовлення">${IC.seal}</span></div>
        ${players}
      </section>

      <section class="orders">
        <h3>Мої замовлення <em>печатка — це очки</em></h3>
        ${hints.map((t, i) => orderSlip(t, i)).join('')}
      </section>

      <section class="journal">
        <h3>Журнал</h3>
        <ol>${mock.log.map(fmtLog).join('')}</ol>
      </section>

      <section class="market">
        <h3>Торг <em>відкриті карти і колода</em></h3>
        <div class="mrow">${market}
          <button class="mc deck" aria-label="Узяти наосліп">
            <span class="deckart"><svg viewBox="0 0 80 104"><rect x=".8" y=".8" width="78.400" height="102.400" rx="6" fill="#5a3418" stroke="#d8b77a" stroke-width="1.400"/><rect x="5" y="5" width="70" height="94" rx="3.500" fill="none" stroke="#d8b77a" stroke-width=".8"/><g transform="translate(40 50)" fill="none" stroke="#d8b77a" stroke-width="1.300">${[...Array(8)].map((_,i)=>`<path transform="rotate(${i*45})" d="M0-5 5-18 0-30-5-18Z"/>`).join('')}<circle r="5"/></g></svg></span>
            <span class="cnt deckn">${mock.deck}</span><span class="deckl">наосліп</span>
          </button>
          <p class="mnote"><b>За хід — дві карти.</b> Вола з торгу — лише одного.</p>
        </div>
      </section>
    </aside>

    <!-- Моя торба -->
    <section class="torba">
      <div class="flap"><h3>Моя торба</h3><span class="tot">${IC.cards} ${handTotal} карт</span></div>
      <div class="handgrid">${hand}<div class="hc wag">${IC.cart}<b>${mock.players[0].wagons}</b><small>возів лишилось</small></div></div>
    </section>

    <!-- Дія: обраний шлях -->
    <section class="action">
      <div class="atitle"><strong>Київ <span>—</span> Полтава</strong></div>
      <div class="acost">${costPips()}</div>
      <div class="ameta2">Є 5 із 6 карт · 6 возів · <b>+15</b> очок</div>
      <div class="abtns">
        <button class="take"><span>Узяти збіжжя</span></button>
        <button class="claim" disabled><span>Зайняти шлях</span><small>бракує 1</small></button>
      </div>
    </section>

    <!-- Дядько Глек -->
    <aside class="tip">
      <div class="tipglek">${glek}</div>
      <div class="scroll"><b>Дядько Глек радить:</b> збіжжя лежить на торгу! Бери, і наступним ходом Полтава твоя. П’ятнадцять очок на дорозі не валяються.</div>
    </aside>

    <div class="zoom"><button aria-label="Ближче">+</button><button aria-label="Далі">−</button></div>
  </main></body></html>`;
})();
writeFileSync(D + '/pc.html', PC);

// ═══ PHONE ══════════════════════════════════════════════════════════════════════════════════════════
const PHONE = (() => {
  const W = 390, H = 400;
  const s = .392;
  const x0 = 1790, y0 = 322;
  const vb = [x0, y0, W / s, H / s];
  const map = buildMap({
    id: 'p', s, vb, sizes: [16, 14.5, 13.5, 13], phone: true,
    vp: { x: x0, y: y0, w: W / s, h: H / s, avoid: [[x0 + (W - 52) / s, y0, 60 / s, 120 / s], [x0, y0 + (H - 74) / s, 120 / s, 80 / s], [x0 + 110 / s, y0 + 262 / s, 195 / s, 70 / s]] },
    selectedBanner: { dx: 6, dy: 112, rot: 17.5, w: 430, h: 70, fs: 44 },
    deco: {
      rose: null,
      foreign: [],
      lands: [['ГЕТЬМАНЩИНА', 2130, 590, 15, 26], ['СЛОБОЖАНЩИНА', 2700, 620, 0, 22]],
      rivers: [['Дніпро', 2395, 1240, 40, 34], ['Десна', 2140, 395, 20, 28]],
    },
  });
  const players = mock.players.map((p, i) => `<button class="ppl ${p.name === mock.turn ? 'turn' : ''}" aria-label="${nameFmt(p.name)}: ${p.points} очок">${waxAvatar(p, i, 34)}<span class="ptxt"><b>${p.points}</b><span class="pwag">${IC.cart}${p.wagons}</span></span></button>`).join('');

  const hand = ORD.filter((k) => mock.myHand[k] > 0).map((k) => `<button class="ht ${k === 'green' || k === 'wild' ? 'use' : ''} k-${k}" aria-label="${CARGO[k].name}: ${mock.myHand[k]}">${card(k, { label: false })}<span class="cnt">${mock.myHand[k]}</span></button>`).join('');
  const market = mock.market.map((k) => `<button class="ht mk ${k === 'green' ? 'want' : ''} k-${k}" aria-label="Узяти: ${CARGO[k].name}">${card(k, { label: false })}</button>`).join('');

  // міні-мапа: той самий контур, мала
  const mini = buildMini(x0, y0, W / s, H / s);

  return `<!doctype html><html lang="uk"><head><meta charset="utf-8"><meta name="viewport" content="width=390,initial-scale=1"><title>Соляний шлях — телефон</title><style>${CSS}</style></head>
  <body class="ph">
    <header class="site"><img src="assets/icon.svg" alt="" hidden><span class="brand">${glek}</span><span class="stitle">Соляний шлях</span><span class="smini"><button aria-label="Правила" class="sbtn">?</button><button aria-label="Меню столу" class="sbtn">⋯</button></span></header>
    <main class="game">
      <div class="pstrip">${players}<div class="ptimer">${IC.hour}<b>0:42</b></div></div>
      <div class="pturn"><i></i>Твій хід, Оля</div>
      <section class="pmap">
        ${map}
        <svg class="fx" viewBox="0 0 390 400" preserveAspectRatio="none" aria-hidden="true">
          <defs><filter id="grain2" x="0" y="0" width="100%" height="100%"><feTurbulence type="fractalNoise" baseFrequency=".85" numOctaves="3" seed="11"/><feColorMatrix values="0 0 0 0 .32  0 0 0 0 .2  0 0 0 0 .07  0 0 0 .5 -.15"/></filter>
          <filter id="blot2" x="0" y="0" width="100%" height="100%"><feTurbulence type="fractalNoise" baseFrequency=".012" numOctaves="4" seed="33"/><feColorMatrix values="0 0 0 0 .42  0 0 0 0 .26  0 0 0 0 .08  1.300 0 0 0 -.62"/></filter>
          <radialGradient id="vig2" cx=".5" cy=".5" r=".75"><stop offset=".6" stop-color="#3a1f08" stop-opacity="0"/><stop offset="1" stop-color="#3a1f08" stop-opacity=".5"/></radialGradient></defs>
          <g style="mix-blend-mode:multiply"><rect width="390" height="400" filter="url(#blot2)" opacity=".5"/><rect width="390" height="400" filter="url(#grain2)" opacity=".7"/><rect width="390" height="400" fill="url(#vig2)"/></g>
        </svg>
        <div class="pzoom"><button aria-label="Ближче">+</button><button aria-label="Далі">−</button></div>
        <button class="pmini" aria-label="Уся мапа">${mini}</button>
        <div class="pgo"><button aria-label="До міста Сімферополь"><span>↓</span>Сімферополь</button><button aria-label="До міста Одеса"><span>↙</span>Одеса</button></div>
        <div class="ppan"><svg viewBox="0 0 22 22"><path d="M11 2 7.500 5.500H14.500ZM11 20 7.500 16.500H14.500ZM2 11 5.500 7.500V14.500ZM20 11 16.500 7.500V14.500Z" fill="currentColor"/></svg>тягни</div>
      </section>
      <section class="paction">
        <div class="pa1"><strong>Київ — Полтава</strong><div class="acost mini">${costPips()}</div></div>
        <button class="claim" disabled><span>Зайняти</span><small>бракує 1</small></button>
        <button class="take"><span>Узяти<br>збіжжя</span></button>
      </section>
      <section class="ptip"><span class="tipg">${glek}</span><p><b>Глек:</b> збіжжя на торгу! Бери, і наступним ходом Полтава твоя.</p></section>
      <section class="prow"><h4>Торба<small>${handTotal}</small></h4><div class="hrow">${hand}</div></section>
      <section class="prow"><h4>Торг</h4><div class="hrow">${market}<button class="ht deckm" aria-label="Узяти наосліп"><svg viewBox="0 0 80 84"><rect x=".8" y=".8" width="78.400" height="82.400" rx="6" fill="#5a3418" stroke="#d8b77a" stroke-width="1.600"/><g transform="translate(40 42)" fill="none" stroke="#d8b77a" stroke-width="1.700">${[...Array(8)].map((_,i)=>`<path transform="rotate(${i*45})" d="M0-5 5-16 0-27-5-16Z"/>`).join('')}</g></svg><span class="cnt">${mock.deck}</span></button></div></section>
      <nav class="pdrawer">
        <button class="on"><span class="dseals">${hints.map((t) => `<span class="${t.done ? 'd' : ''}">${t.pts}</span>`).join('')}</span>Замовлення<i>˄</i></button>
        <button>Журнал<i>˄</i></button>
      </nav>
    </main>
    <nav class="mtabs">
      <button><span class="ico">📻</span>Ефір</button><button><span class="ico">🎵</span>Бібліотека</button><button class="on"><span class="ico">🎮</span>Ігри</button><button><span class="ico">📊</span>Хто скільки</button><button><span class="ico">💬</span>Балачки</button>
    </nav>
  </body></html>`;
})();
writeFileSync(D + '/phone.html', PHONE);

function buildMini(x, y, w, h) {
  const r = routesOut.filter(Boolean);
  return `<svg viewBox="0 0 4000 2730"><use href="#none"/><path d="${MINI_LAND}" fill="#f1e3bd" stroke="#2b1a0d" stroke-width="22"/><rect x="${x}" y="${y}" width="${w}" height="${h}" fill="#ffd95c" fill-opacity=".45" stroke="#b3261e" stroke-width="30"/></svg>`;
}
import { landD as MINI_LAND } from './map.mjs';
console.log('ok', hints.map((t) => [t.a, t.b, t.h.left, t.h.done, t.h.wagons]));
