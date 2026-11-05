import { readFileSync, writeFileSync } from 'node:fs';
import { fx, placeLabels, orderPath, crossings, f1, city, regions } from './geom.mjs';
const REG = regions.map((r) => r.d).join('');
import { R, defs, baseLayer, routesLayer, citiesLayer, riverLabels, PL, emblem } from './mapsvg.mjs';
import { cardSymbol, ORDER, CARD, ICON, GLYPH, SLOT, petal, flower, sunflower, kalyna, leaf, sprig, brush, stitchBand, mix, PAL, oxCart } from './art.mjs';

const m = fx.mock; const SEL = m.selectedRoute, sel = R[SEL];
const hand = m.myHand, handN = ORDER.reduce((s, c) => s + hand[c], 0);
const glek = readFileSync('glek.svg', 'utf8').replace(/<\?xml.*?\?>/, '').replace(/id="clay"/g, 'id="clayG"').replace(/url\(#clay\)/g, 'url(#clayG)');
const seals = { 'Сімферополь': 'А', 'Київ': 'А', 'Опішня': 'Б', 'Одеса': 'Б' };
const LINES = { 'Кам\'янець-Подільський': ['Кам’янець-', 'Подільський'], 'Івано-Франківськ': ['Івано-', 'Франківськ'] };
const BIG = ['Київ', 'Харків', 'Одеса', 'Львів', 'Дніпро'];
const cardDefs = `<svg width="0" height="0" style="position:absolute">${ORDER.map(cardSymbol).join('')}<defs>${Object.keys(GLYPH).map((k) => `<g id="tg-${k}">${GLYPH[k]}</g>`).join('')}</defs></svg>`;

// ── дрібні елементи UI ──
const card = (c, cls = '', extra = '') => `<svg class="cd ${cls}" viewBox="0 0 120 84" preserveAspectRatio="xMidYMin slice"><use href="#card-${c}"/></svg>${extra}`;
const tile = (c, w = 20, h = 28) => `<svg class="tile" width="${w}" height="${h}" viewBox="-13 -18 26 36"><rect x="-12" y="-17" width="24" height="34" rx="5" fill="${c === 'wild' ? '#2a1450' : SLOT[c]}" stroke="${c === 'wild' ? '#ffe08a' : '#0a0812'}" stroke-width="2"/><use href="#tg-${c}" transform="scale(.9)"/></svg>`;
const ico = {
  cart: `<svg viewBox="-48 -32 96 62" class="i"><path d="M-39 3C-39-34 39-34 39 3Z" fill="currentColor"/><path d="M-45 5H45L39 14H-39Z" fill="currentColor"/><circle cx="-25" cy="19" r="9" fill="currentColor"/><circle cx="25" cy="19" r="9" fill="currentColor"/></svg>`,
  card: `<svg viewBox="0 0 20 20" class="i"><rect x="2" y="5" width="11" height="14" rx="2" fill="none" stroke="currentColor" stroke-width="1.8" transform="rotate(-10 7 12)"/><rect x="7" y="3" width="11" height="15" rx="2" fill="currentColor"/></svg>`,
  scroll: `<svg viewBox="0 0 20 20" class="i"><path d="M5 2h10v3H9v9a4 4 0 0 1-4 4 3 3 0 0 1-3-3v-1h4V4a2 2 0 0 0-1-2z M15 2a3 3 0 0 1 3 3h-4a3 3 0 0 1 1-3z" fill="currentColor"/><path d="M9 7h6v7a4 4 0 0 1-3 4H7a5 5 0 0 0 2-4z" fill="currentColor" opacity=".55"/></svg>`,
};
const avatar = (p, s = 40) => {
  const inner = p.i === 1 ? `<div class="avg">${glek}</div>` : `<span>${p.name[0]}</span>`;
  return `<div class="av" style="--c:${p.color};--s:${s}px">${inner}<svg class="emb" viewBox="-13 -13 26 26"><circle r="12.5" fill="#0a0812"/><circle r="10.5" fill="${p.color}"/><g transform="scale(.72)">${emblem(p.i)}</g></svg></div>`;
};
const pname = (p) => p.name.replace(' 🤖', '');
const isBot = (p) => p.name.includes('🤖');
// таймер-квітка: пелюстки гаснуть по колу
function timerRose(size, lit = 17, tot = 24, txt = '0:42') {
  let s = ''; for (let i = 0; i < tot; i++) { const on = i < lit; s += petal(Math.cos((i / tot * 360 - 90) * Math.PI / 180) * 26, Math.sin((i / tot * 360 - 90) * Math.PI / 180) * 26, i / tot * 360 - 90, 22, 8.5, on ? (i % 2 ? '#ffd34d' : '#ef7d1a') : '#2c2740'); }
  return `<svg class="rose" width="${size}" height="${size}" viewBox="-50 -50 100 100">${s}<circle r="25" fill="#0a0812" stroke="#ffd34d" stroke-width="1.6"/><text y="6.5" text-anchor="middle" font-size="18" font-weight="900" fill="#ffe08a">${txt}</text></svg>`;
}
const band = (w, h, o) => `<svg class="band" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}" preserveAspectRatio="xMinYMid slice">${stitchBand(w, h, o)}</svg>`;

const orders = m.myTickets.map((t, i) => { const o = orderPath(t.a, t.b, R, m.claimed); const tot = o.own + o.left; return { ...t, L: 'АБВ'[i], own: t.done ? 1 : o.own, tot: t.done ? 1 : tot, left: o.left, via: o.todo }; });
const orderVia = ['Кропивницький → Кривий Ріг → Херсон → Перекоп → Сімферополь', 'Опішня → Кременчук, Кропивницький → Кривий Ріг → Миколаїв → Одеса'];

const CSS = `
@font-face{font-family:'Onest';font-weight:100 900;src:url(fonts/onest-cyrillic-wght-normal.woff2) format('woff2');unicode-range:U+0301,U+0400-045F,U+0490-0491,U+04B0-04B1,U+2116}
@font-face{font-family:'Onest';font-weight:100 900;src:url(fonts/onest-latin-wght-normal.woff2) format('woff2');unicode-range:U+0000-00FF,U+2000-206F,U+2191,U+2193,U+2212}
:root{--bg:#0f1f18;--panel2:#234032;--line:#2f4d3d;--text:#ecf1ea;--muted:#9db3a5;--accent:#f4c542;--accent-ink:#2a1e03;
 --ink:#0a0812;--night:#100d1e;--night2:#191530;--night3:#241e42;--gold:#ffd34d;--gold2:#ffe08a;--cream:#fbf1da;--dim:#a79fbe;--red:#d9301f}
*{box-sizing:border-box;margin:0}html,body{width:100%;height:100%;overflow:hidden;background:var(--ink);color:var(--cream);font-family:Onest,system-ui,sans-serif;-webkit-font-smoothing:antialiased;font-feature-settings:'tnum'}
button{font:inherit;color:inherit;border:0;background:none;cursor:pointer}
svg text{font-family:Onest,system-ui,sans-serif}
header{height:56px;display:flex;align-items:center;gap:12px;padding:8px 16px;background:#0f1f18;border-bottom:1px solid var(--line);color:var(--text);position:relative;z-index:5}
.brand{display:flex;align-items:center;gap:10px;font-weight:700;font-size:18px}.brand svg{width:30px;height:30px;display:block}
.vs{display:flex;gap:2px;background:#16291f;border-radius:11px;padding:2px}.vs b{font-weight:500;padding:7px 11px;border-radius:9px;color:var(--muted);font-size:14px;white-space:nowrap}.vs b.on{background:var(--panel2);color:var(--text)}
.sp{flex:1}.mechip{display:inline-flex;align-items:center;gap:7px;padding:3px 10px 3px 3px;border-radius:999px;background:var(--panel2);font-size:14px;white-space:nowrap}.mechip i{font-style:normal;color:var(--accent);font-size:13px}
.ava{width:26px;height:26px;border-radius:50%;background:#7a2a20;color:#ffd9d0;display:grid;place-items:center;font-weight:700;font-size:13px}
.av{--s:40px;width:var(--s);height:var(--s);border-radius:50%;position:relative;flex:none;background:var(--c);display:grid;place-items:center;font-weight:900;font-size:calc(var(--s)*.46);color:#fff;box-shadow:0 0 0 2px var(--ink),0 0 0 3.5px color-mix(in srgb,var(--c) 60%,#fff)}
.avg{width:100%;height:100%;border-radius:50%;background:#e9dfc5;overflow:hidden;display:block}.avg svg{width:118%;height:118%;margin:-1% -9%;display:block}
.emb{position:absolute;right:-5px;bottom:-5px;width:calc(var(--s)*.5);height:calc(var(--s)*.5)}
.cd{display:block;width:100%;height:100%}
.i{width:1em;height:1em;display:inline-block;vertical-align:-.12em}
.lb{font-weight:800;font-size:11.5px;letter-spacing:.12em;text-transform:uppercase;color:var(--gold)}
.lb em{font-style:normal;color:var(--dim);letter-spacing:.02em;text-transform:none;font-weight:600;margin-left:6px;font-size:12px}
`;

// ═══════════════ PC ═══════════════
const k = 844 / 2730, vbX = -14;
const fuP = 12.6 / k;
const labP = placeLabels(R, fuP, { lines: LINES, big: BIG, manual: { 'Івано-Франківськ': ['l', -.72], 'Київ': ['r', -.42], 'Хмельницький': ['r', -.62] } });
const pcFlora = () => {
  let s = '';
  // верхній правий кут: соняшник і калина над Сумщиною, поза кордоном
  s += sprig([4010, 900], [3990, 700], [3900, 560], [3800, 340], { L: 110, W: 42, w: 18, n: 7, a: PAL.grn, h: PAL.grn2, stem: PAL.grn, curl: false });
  s += brush([3890, 520], [3760, 470], [3620, 430], [3500, 300], 12, 5, PAL.grn);
  s += leaf(3660, 440, 120, 96, 42) + leaf(3580, 372, -128, 80, 34);
  s += kalyna(3468, 252, 76, 8);
  s += sunflower(3790, 250, 225);
  // Молдовська щілина між рукою й Одещиною
  s += sprig([1400, 2080], [1450, 1950], [1400, 1810], [1445, 1640], { L: 74, W: 30, w: 10, n: 6, a: PAL.grn, h: PAL.grn2, stem: PAL.grn, curl: false });
  s += kalyna(1445, 1590, 58, 21, { n: 9 });
  // квітка в морі, край руки
  s += sprig([1440, 2740], [1500, 2640], [1590, 2600], [1700, 2640], { L: 66, W: 28, w: 9, n: 5, a: PAL.grn, h: PAL.grn2, stem: PAL.grn, curl: false });
  s += flower(1720, 2650, 96);
  return s;
};
const pcMap = `<svg class="map" viewBox="${vbX} 0 ${f1(1240 / k)} 2730" width="1240" height="844">${defs()}${baseLayer()}${pcFlora()}
<text x="2610" y="125" font-size="116" font-weight="900" fill="#ffe08a" letter-spacing="-2">Соляний шлях</text>${oxCart(2960, 150, 1.85)}<path d="M2930 306q210 -16 450 -2q-220 -2 -450 2z" fill="#f6bf26"/>
${riverLabels(fuP, [['Дніпро', 2215, 900, -35], ['Дністер', 1190, 1395, 28], ['Десна', 1990, 225, 62], ['Прип’ять', 1120, 240, -6]])}
<g font-weight="700" fill="#86a9f2" text-anchor="middle" letter-spacing="30"><text x="2100" y="2500" font-size="62">ЧОРНЕ</text><text x="2100" y="2580" font-size="62">МОРЕ</text><text x="3330" y="2120" font-size="46" letter-spacing="14">АЗОВСЬКЕ МОРЕ</text></g>
${routesLayer(SEL)}${citiesLayer(labP, fuP, seals, [sel.a, sel.b])}
${(() => { const [x, y] = sel.mid; const bx = x + 70, by = y - 205; return `<g><path d="M${f1(x)} ${f1(y - 36)}L${f1(bx)} ${f1(by + 70)}" stroke="#ffd34d" stroke-width="7"/><g transform="translate(${f1(bx)} ${f1(by)})"><rect x="-265" y="-74" width="530" height="148" rx="34" fill="#0a0812" stroke="#ffd34d" stroke-width="7"/><use href="#g-green" transform="translate(-200 0) scale(0)"/><rect x="-240" y="-46" width="66" height="92" rx="14" fill="${SLOT.green}" stroke="#0a0812" stroke-width="3"/><use href="#g-green" transform="translate(-207 0) scale(2.1)"/><text x="-152" y="19" font-size="56" font-weight="900" fill="#ffe08a">6 збіжжя</text><text x="245" y="19" text-anchor="end" font-size="56" font-weight="900" fill="#fff">+15</text></g></g>`; })()}
</svg>`;

const pcHand = ORDER.map((c) => { const n = hand[c]; const use = c === 'green' || c === 'wild'; return `<div class="hc ${n ? '' : 'zero'} ${use ? 'pay' : ''}" style="--st:${Math.min(n, 4)}">${n > 1 ? '<i class="st s1"></i>' : ''}${n > 2 ? '<i class="st s2"></i>' : ''}<div class="hcf">${card(c)}<b class="nm" style="color:${CARD[c].t};--bandc:${CARD[c].f}">${CARD[c].n === 'Круторогий віл' ? 'Віл · будь-що' : CARD[c].n}</b></div><u class="ct">${n}</u></div>`; }).join('');
const sockets = [...Array(4).fill('green'), 'wild'].map((c) => tile(c, 24, 34)).join('') + '<span class="sock">?</span>';
const pcHTML = `<!doctype html><html lang="uk"><head><meta charset="utf-8"><title>Соляний шлях · Петриківка · ПК</title><style>${CSS}
body{width:1600px;height:900px}
.stage{position:absolute;left:0;top:56px;width:1240px;height:844px;overflow:hidden;background:#08070f}
.map{position:absolute;inset:0;display:block}.topb{position:absolute;left:0;top:0;width:1240px;height:8px;overflow:hidden;background:#0a0812;z-index:2}.topb svg{display:block}
.seam{position:absolute;left:1240px;top:56px;width:14px;height:844px;background:#0a0812;overflow:hidden}
.seam svg{position:absolute;left:0;top:0}
.rail{position:absolute;left:1254px;top:56px;width:346px;height:844px;background:var(--night);padding:11px 14px 12px;display:flex;flex-direction:column;justify-content:space-between}
/* хід */
.turn{position:absolute;left:10px;top:12px;display:flex;align-items:flex-start;gap:10px;z-index:3}.turn .tx{padding-top:4px}
.turn h1{font-size:24px;white-space:nowrap;font-weight:900;color:var(--gold2);line-height:1.02;letter-spacing:-.01em;text-shadow:0 2px 0 #000}
.turn p{font-size:12.5px;color:var(--cream);font-weight:500;line-height:1.25;white-space:nowrap}
/* гравці */
.lgd{display:flex;justify-content:space-between;align-items:center}.lgd span{letter-spacing:0;text-transform:none;color:var(--dim);font-weight:600;font-size:11.5px;display:inline-flex;align-items:center;gap:4px}.lgd .i{margin-left:6px;font-size:13px}
.mini-b{font-size:12px;font-weight:800;letter-spacing:0;text-transform:none;color:var(--gold2);height:26px;padding:0 9px;border-radius:8px;box-shadow:inset 0 0 0 1.5px #6d5a1e;background:#191530}
.pls{display:flex;flex-direction:column;gap:3px}
.pl{display:flex;align-items:center;gap:12px;padding:6px 10px 6px 10px;border-radius:12px;position:relative}
.pl.now{background:var(--night3);box-shadow:inset 0 0 0 1.5px var(--gold)}
.pl .who{flex:1;min-width:0}.pl .who b{display:block;font-size:14.5px;font-weight:800;line-height:1.1}.pl .who b small{font-size:10.5px;font-weight:700;color:var(--dim);margin-left:5px;padding:1px 5px;border-radius:5px;background:#2a2542;letter-spacing:.03em}
.st3{display:flex;gap:11px;margin-top:4px;font-size:12.5px;font-weight:700;color:var(--dim)}.st3 span{display:inline-flex;align-items:center;gap:4px}
.st3 .i{font-size:15px}.pl .sc{font-size:26px;font-weight:900;color:var(--cream);line-height:1;min-width:38px;text-align:right}.pl.lead .sc{color:var(--gold)}.pl .ldr{font-size:10px;font-weight:800;color:#1b0f06;background:var(--gold);padding:1px 5px;border-radius:5px;margin-left:5px}
/* ринок */
.mk{display:grid;grid-template-columns:repeat(3,1fr);gap:7px;margin-top:7px}
.mc{position:relative;aspect-ratio:120/74;border-radius:9px;overflow:hidden;box-shadow:0 0 0 2px var(--ink)}
.mc.want{box-shadow:0 0 0 2.5px var(--gold),0 0 16px rgba(255,211,77,.5)}
.mc .tg{position:absolute;right:4px;top:4px;background:var(--gold);color:#1b0f06;font-size:10.5px;font-weight:900;padding:1px 6px;border-radius:7px}
.deck{position:relative;aspect-ratio:120/74;border-radius:9px;background:#1a1430;box-shadow:0 0 0 2px var(--gold),3px 3px 0 0 var(--ink),3px 3px 0 2px #6d5a1e,6px 6px 0 0 var(--ink),6px 6px 0 2px #4b3e16;display:flex;flex-direction:column;align-items:center;justify-content:center;font-weight:800;font-size:12px;color:var(--gold2);overflow:hidden}
.deck strong{font-size:23px;line-height:1;font-weight:900}.deck svg{position:absolute;inset:0;opacity:.34;width:100%;height:100%}.deck span,.deck strong{position:relative}
/* замовлення */
.ord{display:flex;flex-direction:column;gap:7px;margin-top:7px}
.o{display:grid;grid-template-columns:24px 1fr auto;column-gap:8px;align-items:center}
.seal{width:24px;height:24px;border-radius:50%;background:var(--gold);color:#1b0f06;font-weight:900;font-size:13.5px;display:grid;place-items:center;box-shadow:inset 0 0 0 2px #1b0f0620,0 0 0 1.5px #0a0812}
.o b{font-size:13.5px;font-weight:800;white-space:nowrap}.o .pts{font-size:17px;font-weight:900;color:var(--gold2)}
.prog{grid-column:2/4;display:flex;gap:2px;margin-top:4px}.prog i{flex:1;height:7px;border-radius:2px;background:#322c4d}.prog i.on{background:#ef7d1a}
.o small{grid-column:2/4;font-size:11.2px;color:var(--dim);font-weight:500;margin-top:3px;line-height:1.25}
.o.done{opacity:.86}.o.done .seal{background:#2f9e4a;color:#fff}.o.done b{color:var(--dim);font-weight:700}.o.done .pts{color:#7ad36b}
/* журнал */
.log{display:flex;flex-direction:column;gap:3px;margin-top:6px;font-size:12.2px;line-height:1.25;color:#cfc8df;font-weight:500}.log div{display:flex;gap:7px}.log i{flex:none;width:9px;height:9px;margin-top:3px;display:block}.log b{color:#fff;font-weight:700}
.divb{height:10px;overflow:hidden;flex:none;margin:0 -14px;background:#0a0812}.divb svg{display:block}
.grow{flex:1;min-height:0}
/* рука */
.handp{position:absolute;left:10px;bottom:10px;width:418px;z-index:3}
.claim{background:#0a0812;border-radius:14px;box-shadow:0 0 0 2px var(--gold);padding:8px 11px;display:grid;grid-template-columns:1fr auto;gap:5px 10px;align-items:center;margin-bottom:7px}
.claim h3{font-size:16px;font-weight:900;color:var(--gold2);line-height:1.1;white-space:nowrap}.claim h3 small{font-size:12px;font-weight:700;color:var(--cream);margin-left:6px}
.claim .pay{display:flex;gap:3px;align-items:center}.sock{width:24px;height:34px;border-radius:6px;border:2px dashed var(--gold);color:var(--gold);display:grid;place-items:center;font-weight:900;font-size:15px}
.claim .pay em{font-style:normal;font-size:12.5px;font-weight:700;margin-left:8px;line-height:1.15;color:var(--cream)}.claim .pay em b{color:var(--gold);font-weight:900}
.btn{font-weight:900;font-size:14px;border-radius:10px;padding:0 13px;height:44px;display:inline-flex;align-items:center;justify-content:center;gap:6px;white-space:nowrap}
.btn.go{background:var(--gold);color:#1b0f06;box-shadow:0 3px 0 #a87a10}.btn.off{background:#231e3a;color:#b6aecb;box-shadow:inset 0 0 0 1.5px #4b4468}
.btns{grid-row:1/3;grid-column:2;display:flex;flex-direction:column;gap:6px}.btns .btn{height:36px;font-size:13px}
.hl{margin:0 0 7px 2px}.hg{display:grid;grid-template-columns:repeat(3,1fr);gap:8px 11px}
.hc{position:relative;aspect-ratio:120/65}
.hcf{position:absolute;inset:0;border-radius:10px;overflow:hidden;box-shadow:0 0 0 2px var(--ink);background:#000}
.hc.pay .hcf{box-shadow:0 0 0 2.5px var(--gold),0 0 14px rgba(255,211,77,.45)}
.hc.zero .hcf{filter:grayscale(.75) brightness(.55)}.hc.zero .ct{background:#1c1830;color:#8a83a0;box-shadow:0 0 0 1.5px #3b3556}
.hc:not(.pay):not(.zero) .hcf{filter:brightness(.94)}
.st{position:absolute;inset:0;border-radius:10px;background:var(--ink);box-shadow:inset 0 0 0 1.5px #d9cda8}.st.s1{transform:translate(3px,3px)}.st.s2{transform:translate(6px,6px)}
.nm{position:absolute;left:0;right:0;bottom:0;font-size:11.5px;font-weight:800;padding:1px 6px 2px;background:var(--bandc);text-align:center;box-shadow:0 -1px 0 rgba(0,0,0,.25)}
.ct{position:absolute;right:-6px;top:-7px;min-width:29px;height:29px;padding:0 5px;border-radius:15px;background:#0a0812;color:var(--gold2);font-weight:900;font-size:17px;display:grid;place-items:center;text-decoration:none;box-shadow:0 0 0 2px var(--gold)}
/* Глек */
.glek{position:absolute;right:6px;bottom:6px;width:224px;display:flex;align-items:flex-end;flex-direction:row-reverse;z-index:3}
.glek svg.g{width:68px;height:68px;flex:none;filter:drop-shadow(0 3px 0 #000);margin-left:-6px;position:relative;z-index:1}
.bub{background:var(--cream);color:#1b0f06;border-radius:14px 14px 3px 14px;padding:7px 10px;font-size:12.2px;line-height:1.26;font-weight:600;margin-bottom:12px;box-shadow:0 0 0 2px #0a0812}.bub b{font-weight:900;color:#a51b10}
.logo{position:absolute;left:2790px}
</style></head><body>${cardDefs}
<header><div class="brand">${glek.replace('<svg', '<svg')}<span>Глечики</span></div><div class="vs"><b>📻 Ефір</b><b>🎵 Бібліотека</b><b class="on">🎮 Ігри</b><b>📊 Хто скільки</b></div><div class="sp"></div><span class="mechip"><span class="ava">О</span>Оля <i>🏺 1 240</i></span></header>
<div class="stage">${pcMap}
 <div class="topb">${band(1240, 8)}</div><div class="turn">${timerRose(92)}<div><div class="tx"><h1>Твій хід, Оля</h1></div></div></div>
 <div class="handp">
  <div class="claim"><h3>Київ — Полтава<small>6 возів · +15</small></h3><div class="btns"><button class="btn off">Зайняти шлях</button><button class="btn go">Взяти збіжжя ↗</button></div><div class="pay">${sockets}<em>є 5 з 6:<br><b>бракує 1</b></em></div></div>
  <div class="lb hl">Моя рука<em>${handN} карт · золотим — чим платити</em></div><div class="hg">${pcHand}</div>
 </div>
 <div class="glek"><svg class="g" viewBox="0 0 128 128">${glek.replace(/<svg[^>]*>/, '').replace('</svg>', '')}</svg><div class="bub"><b>Збіжжя на ринку</b> — твоя шоста карта. Хапай, поки Петро не вхопив!</div></div>
</div>
<div class="seam">${`<svg width="14" height="844" viewBox="0 0 844 14" style="transform-origin:0 0;transform:rotate(90deg) translateY(-14px)">${stitchBand(844, 14)}</svg>`}</div>
<aside class="rail">
 <div class="lb lgd">Гравці<span>${ico.cart}вози ${ico.card}карти ${ico.scroll}замовл.</span></div><div class="pls">${PL.map((p) => `<div class="pl ${p.me ? 'now' : ''} ${p.points === Math.max(...PL.map((q) => q.points)) ? 'lead' : ''}">${avatar(p, 40)}<div class="who"><b>${pname(p)}${isBot(p) ? '<small>бот</small>' : ''}${p.points === Math.max(...PL.map((q) => q.points)) ? '<span class="ldr">лідер</span>' : ''}</b><div class="st3"><span title="вози">${ico.cart}${p.wagons}</span><span title="карти">${ico.card}${p.me ? handN : p.cards}</span><span title="замовлення">${ico.scroll}${p.tickets}</span></div></div><div class="sc">${p.points}</div></div>`).join('')}</div>
 <div class="divb">${band(346, 10)}</div>
 <section><div class="lb">Ринок<em>візьми 2 карти, або одного вола</em></div><div class="mk">${m.market.map((c) => `<div class="mc ${c === 'green' ? 'want' : ''}">${card(c)}<b class="nm" style="color:${CARD[c].t};--bandc:${CARD[c].f}">${c === 'wild' ? 'Віл · будь-що' : CARD[c].n}</b>${c === 'green' ? '<span class="tg">+1</span>' : ''}</div>`).join('')}<div class="deck"><svg viewBox="-60 -40 120 80" preserveAspectRatio="xMidYMid slice">${flower(0, 0, 44, {})}</svg><strong>${m.deck}</strong><span>колода</span></div></div></section>
 <div class="divb">${band(346, 10)}</div>
 <section><div class="lb lgd">Мої замовлення<button class="mini-b">+ нове замовлення</button></div><div class="ord">${orders.map((o, i) => o.done ? `<div class="o done"><span class="seal">✓</span><b>${o.a} — ${o.b}</b><span class="pts">+${o.pts}</span></div>` : `<div class="o"><span class="seal">${o.L}</span><b>${o.a} — ${o.b}</b><span class="pts">${o.pts}</span><div class="prog">${Array.from({ length: o.tot }, (_, j) => `<i class="${j < o.own ? 'on' : ''}"></i>`).join('')}</div><small>лишилось ${o.left} возів: ${orderVia[i]}</small></div>`).join('')}</div></section>
 <div class="divb">${band(346, 10)}</div>
 <section><div class="lb">Журнал</div><div class="log">${m.log.map((l) => { const nm = PL.find((p) => l.startsWith(p.name)); const t = nm ? l.slice(nm.name.length) : l; return `<div><i><svg viewBox="-11 -11 22 22"><g fill="${nm ? nm.color : '#fff'}"><circle r="10.5"/></g><g transform="scale(.7)">${emblem(nm ? nm.i : 0)}</g></svg></i><span><b>${nm ? pname(nm) : ''}</b>${t}</span></div>`; }).join('')}</div></section>
</aside>
</body></html>`;
writeFileSync('pc.html', pcHTML);

// ═══════════════ ТЕЛЕФОН ═══════════════
const kp = .37, viewW = 390 / kp, mapH = 432, viewH = mapH / kp, vx = 1752, vy = 300;
const fuF = 13.4 / kp;
const labF = placeLabels(R, fuF, { lines: LINES, big: BIG, manual: { 'Полтава': ['b', -.42] } });
const phMap = `<svg class="map" viewBox="${vx} ${vy} ${f1(viewW)} ${f1(viewH)}" width="390" height="${mapH}">${defs()}${baseLayer()}
${riverLabels(fuF, [['Дніпро', 2215, 900, -35], ['Десна', 1990, 225, 62]])}
${routesLayer(SEL)}${citiesLayer(labF, fuF, seals, [sel.a, sel.b])}
${(() => { const [x, y] = sel.mid; const bx = x + 90, by = y - 215; return `<g><path d="M${f1(x)} ${f1(y - 36)}L${f1(bx)} ${f1(by + 60)}" stroke="#ffd34d" stroke-width="8"/><g transform="translate(${f1(bx)} ${f1(by)})"><rect x="-222" y="-62" width="444" height="124" rx="30" fill="#0a0812" stroke="#ffd34d" stroke-width="7"/><rect x="-198" y="-40" width="54" height="80" rx="12" fill="${SLOT.green}" stroke="#0a0812" stroke-width="3"/><use href="#g-green" transform="translate(-171 0) scale(1.8)"/><text x="-128" y="17" font-size="48" font-weight="900" fill="#ffe08a">6 збіжжя</text><text x="205" y="17" text-anchor="end" font-size="48" font-weight="900" fill="#fff">+15</text></g></g>`; })()}
</svg>`;
// мінімапа: вся країна, рамка видимого
const miniMap = (() => { const rs = baseMini(); return rs; })();
function baseMini() {
  return `<svg viewBox="-60 -60 4120 2850" class="mini">${regionsMini()}<rect x="${vx}" y="${vy}" width="${f1(viewW)}" height="${f1(viewH)}" fill="#ffd34d30" stroke="#ffd34d" stroke-width="70" rx="20"/></svg>`;
}
function regionsMini() { return `<path d="${REG}" fill="#6a5a94" stroke="#0a0812" stroke-width="18"/>` + R.filter((X) => Object.values(m.claimed).flat().includes(X.id)).map((X) => { const o = Object.keys(m.claimed).find((p) => m.claimed[p].includes(X.id)); return `<path d="${X.d}" fill="none" stroke="${PL[o].color}" stroke-width="44" stroke-linecap="round"/>`; }).join('') + `<path d="${sel.d}" fill="none" stroke="#ffd34d" stroke-width="50"/>`; }
const phHand = ORDER.filter((c) => hand[c] > 0).map((c) => `<button class="ph ${c === 'green' || c === 'wild' ? 'pay' : ''}" data-n="${c}">${`<svg class="cd" viewBox="34 0 52 58" preserveAspectRatio="xMidYMid slice"><use href="#card-${c}" width="120" height="84"/></svg>`}<u>${hand[c]}</u></button>`).join('');
const phMk = m.market.map((c) => `<button class="ph ${c === 'green' ? 'want' : ''}" data-n="m${c}"><svg class="cd" viewBox="34 0 52 58" preserveAspectRatio="xMidYMid slice"><use href="#card-${c}" width="120" height="84"/></svg>${c === 'green' ? '<i>+1</i>' : ''}</button>`).join('');
const phoneHTML = `<!doctype html><html lang="uk"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Соляний шлях · Петриківка · телефон</title><style>${CSS}
body{width:390px;height:844px;position:relative}
header{height:56px;padding:8px 12px;gap:8px}.brand{font-size:16px;gap:8px}.mechip{font-size:13px}.ttl{font-weight:700;font-size:15px}
.mtabs{position:absolute;left:0;right:0;bottom:0;height:64px;display:flex;background:#0f1f18;border-top:1px solid var(--line);z-index:6}
.mtabs b{flex:1;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:2px;font-size:11px;font-weight:500;color:var(--muted);white-space:nowrap}.mtabs b span{font-size:19px;line-height:1}.mtabs b.on{color:var(--accent)}
.g{position:absolute;left:0;right:0;top:56px;height:724px;display:flex;flex-direction:column;background:var(--night)}
.top{height:50px;flex:none;display:flex;align-items:center;gap:3px;padding:0 4px 0 6px;background:var(--night);z-index:4}
.ch{flex:1;min-width:0;height:44px;display:flex;align-items:center;gap:6px;padding:0 4px;border-radius:10px;justify-content:center}
.ch.now{background:var(--night3);box-shadow:inset 0 0 0 1.5px var(--gold)}.ch .sc{font-size:17px;font-weight:900;color:var(--gold2);line-height:1}.ch small{display:block;font-size:10px;font-weight:700;color:var(--dim);line-height:1.1}
.mapw{position:relative;height:${mapH}px;flex:none;overflow:hidden;background:#08070f}
.map{display:block}
.ctrl{position:absolute;right:8px;top:8px;display:flex;flex-direction:column;gap:6px}
.rb{width:42px;height:42px;border-radius:12px;background:#0a0812e8;box-shadow:inset 0 0 0 1.5px var(--gold);color:var(--gold2);font-size:22px;font-weight:800;display:grid;place-items:center}
.mini{position:absolute;left:8px;top:8px;width:92px;height:64px;background:#0a0812;border-radius:10px;box-shadow:inset 0 0 0 1.5px #4b4468}
.yourt{position:absolute;left:106px;top:8px;height:30px;padding:0 12px;border-radius:15px;background:var(--gold);color:#1b0f06;font-weight:900;font-size:12.5px;display:flex;align-items:center;white-space:nowrap;box-shadow:0 0 0 2px #0a0812}
.ptip{position:absolute;left:6px;bottom:6px;display:flex;align-items:flex-end}.gg{width:46px;height:46px;flex:none;filter:drop-shadow(0 2px 0 #000);position:relative;z-index:1}
.pb{background:var(--cream);color:#1b0f06;font-size:12px;font-weight:600;line-height:1.22;padding:5px 9px;border-radius:12px 12px 12px 3px;margin:0 0 8px -4px;width:122px;box-shadow:0 0 0 2px #0a0812}.pb b{color:#a51b10;font-weight:900}
.panh{position:absolute;right:8px;bottom:8px;height:26px;padding:0 9px;border-radius:13px;background:#0a0812d0;color:var(--cream);font-size:11.5px;font-weight:700;display:flex;align-items:center;gap:5px;box-shadow:inset 0 0 0 1px #5a5272}
.sheet{flex:1;min-height:0;padding:0 8px 6px;background:var(--night);display:flex;flex-direction:column;justify-content:space-between;position:relative}
.seamh{height:8px;overflow:hidden;background:#0a0812;margin:0 -8px}.seamh svg{display:block}
.claimp{display:flex;align-items:center;gap:7px}
.claimp h3{font-size:14.5px;font-weight:900;color:var(--gold2);line-height:1.1;white-space:nowrap}.claimp small{display:block;font-size:11.5px;font-weight:700;color:var(--cream);margin-top:2px}
.ct1{flex:1;min-width:0}.pp{display:flex;gap:2px;align-items:center;margin-top:3px}.pp .sock{width:14px;height:20px;font-size:10px;border-radius:4px;border:1.5px dashed var(--gold);color:var(--gold);display:grid;place-items:center;font-weight:900}.pp em{font-style:normal;font-size:11.5px;font-weight:800;color:var(--gold);margin-left:5px;white-space:nowrap}
.seal{width:24px;height:24px;border-radius:50%;background:var(--gold);color:#1b0f06;font-weight:900;font-size:13.5px;display:grid;place-items:center}.seal.s{width:19px;height:19px;font-size:11px}
.btn{font-weight:900;font-size:13.5px;border-radius:10px;padding:0 10px;height:44px;display:inline-flex;align-items:center;justify-content:center;gap:5px;white-space:nowrap}
.btn.go{background:var(--gold);color:#1b0f06;box-shadow:0 3px 0 #a87a10}.btn.off{background:#231e3a;color:#b6aecb;box-shadow:inset 0 0 0 1.5px #4b4468}
.row{display:flex;gap:5px;align-items:stretch}
.rl{flex:none;width:40px;display:flex;flex-direction:column;align-items:center;justify-content:center;font-size:10px;font-weight:800;letter-spacing:.04em;color:var(--gold);text-transform:uppercase}
.ph{flex:1;min-width:40px;height:54px;position:relative;border-radius:8px;padding:0;box-shadow:0 0 0 1.5px #000;overflow:visible}
.ph svg{border-radius:8px;display:block}
.ph.pay,.ph.want{box-shadow:0 0 0 2px var(--gold)}
.ph u{position:absolute;right:-3px;top:-4px;min-width:21px;height:21px;border-radius:11px;background:#0a0812;color:var(--gold2);font-weight:900;font-size:13px;display:grid;place-items:center;text-decoration:none;box-shadow:0 0 0 1.5px var(--gold)}
.ph i{position:absolute;right:-3px;top:-4px;background:var(--gold);color:#1b0f06;font-size:10.5px;font-weight:900;font-style:normal;padding:1px 4px;border-radius:6px}
.dk{flex:1;min-width:40px;height:54px;border-radius:8px;background:#1a1430;box-shadow:0 0 0 1.5px var(--gold),2px 2px 0 1.5px #5b4b1a;color:var(--gold2);display:flex;flex-direction:column;align-items:center;justify-content:center;font-size:9.5px;font-weight:800}.dk b{font-size:17px;font-weight:900;line-height:1}
.seg{display:flex;gap:4px}.seg button{flex:1;height:40px;border-radius:10px;font-size:12.5px;font-weight:800;background:#191530;color:var(--dim);display:flex;align-items:center;justify-content:center;gap:5px}.seg button.on{background:var(--night3);color:var(--gold2);box-shadow:inset 0 0 0 1.5px #6d5a1e}
</style></head><body>${cardDefs}
<header><div class="brand">${glek}</div><span class="ttl">Соляний шлях</span><div class="sp"></div><span class="mechip"><span class="ava">О</span><i>🏺 1 240</i></span></header>
<div class="g">
 <div class="top">${timerRose(46)}${PL.map((p) => `<button class="ch ${p.me ? 'now' : ''}" data-n="${pname(p)}">${avatar(p, 28)}<div><div class="sc">${p.points}</div><small>${ico.cart} ${p.wagons}</small></div></button>`).join('')}</div>
 <div class="mapw">${phMap}${miniMap}<div class="ctrl"><button class="rb" data-n="plus">+</button><button class="rb" data-n="minus">−</button><button class="rb" data-n="fit"><svg width="22" height="22" viewBox="0 0 22 22" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><path d="M2 7V2h5M15 2h5v5M20 15v5h-5M7 20H2v-5"/></svg></button></div>
  <div class="yourt">Твій хід</div>
  <div class="ptip"><svg class="gg" viewBox="0 0 128 128">${glek.replace(/<svg[^>]*>/, '').replace('</svg>', '').replace(/clayG/g, 'clayF')}</svg><div class="pb"><b>Бери збіжжя</b> — шоста карта!</div></div>
  <div class="panh"><svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M8 1v14M1 8h14M8 1 6 3M8 1l2 2M8 15l-2-2M8 15l2-2M1 8l2-2M1 8l2 2M15 8l-2-2M15 8l-2 2"/></svg>тягни й щипай</div></div>
 <div class="sheet"><div class="seamh">${band(390, 8)}</div>
  <div class="claimp"><div class="ct1"><h3>Київ — Полтава</h3><div class="pp">${[...Array(4).fill('green'), 'wild'].map((c) => tile(c, 14, 20)).join('')}<span class="sock">?</span><em>бракує 1</em></div></div><button class="btn off" data-n="claim">Зайняти</button><button class="btn go" data-n="take">Взяти збіжжя</button></div>
  <div class="row"><div class="rl">Ринок</div>${phMk}<div class="dk"><b>${m.deck}</b>колода</div></div>
  <div class="row"><div class="rl">Рука<br>${handN}</div>${phHand}</div>
  <div class="seg"><button class="on" data-n="tabc">${ico.card} Карти</button><button data-n="tabo" style="flex:1.5">Замовлення <span class="seal s">А</span><span class="seal s">Б</span></button><button data-n="tabl">Журнал</button></div>
 </div>
</div>
<nav class="mtabs"><b><span>📻</span>Ефір</b><b><span>🎵</span>Бібліотека</b><b class="on"><span>🎮</span>Ігри</b><b><span>📊</span>Хто скільки</b><b><span>💬</span>Балачки</b></nav>
</body></html>`;
writeFileSync('phone.html', phoneHTML);
console.log('crossings', crossings(R).length, 'worst labels', Object.entries(labP).filter(([, l]) => l.cost > 40).map(([n, l]) => n + ':' + Math.round(l.cost)).join(' '));
