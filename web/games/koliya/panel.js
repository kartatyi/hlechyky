// «Колія» — панель повзунків і перемикачів. Власник: K4.
// Контракт: createPanel(root, { meta, variants, values, onChange(values), onRestart(), audio, seed?, onSeed?(seed) })
//   → { set(values), setSeed(seed), toggle(open?), dispose() }
//  meta = PARAM_META (повзунки за групами), variants = VARIANTS (перемикачі); onChange — після кожної правки (main.js
//  зберігає в localStorage і застосовує з наступного рестарту); onRestart — кнопка «Почати заново».
//  onSeed(seed) — ОПЦІЙНО: поле сіду / «новий сід» (оркестратор має підключити: game.seed = seed; restart()).
//  Відкрити/закрити — кнопка ⚙ або F1 чи `. Стан звуку не зберігається: на старті завжди 🔇.
//  K12 (пад): prefs?: { quality: 'high'|'low', rumble: bool } + onPref?(k, v) — розділ «Пристрій» (не правила, діють
//  одразу); onToggle?(open) — панель відкрилась/закрилась; isOpen(); nav(key) — керування з пада: 'up'/'down' — рядок,
//  'left'/'right' — значення (повзунок, перемикач) або сусідня кнопка, 'a' — натиснути, 'b'/'menu' — закрити.
//  K16: групи в порядку GROUPS (Відчуття → Паротяг → Котел → Ресурси → Гравці → Світ → Додатки → Пристрій), перемикачі —
//  у своїх групах (GROUP_OF), короткі підписи SHORT (повний — у підказці title), «Типові» на групу; зверху пресети
//  PRESETS («Затишно», «Як Unrailed», «Хаос») — лише ставлять значення (від типових), рестарт — кнопкою.
//  prefs.tips (true/false) — «Підказки: так/ні» у «Пристрої» (onPref('tips', b) → game.tips.setEnabled).

const CSS = `
.kol-gear{position:fixed;right:8px;top:8px;z-index:30;width:46px;height:46px;border-radius:10px;border:1px solid #fff3;
  background:rgba(15,18,14,.7);color:#eee;font-size:24px;cursor:pointer;line-height:1;touch-action:manipulation}
.kol-gear:hover{background:rgba(40,46,36,.85)}
.kol-pan{position:fixed;right:0;top:0;bottom:0;width:340px;max-width:92vw;z-index:29;box-sizing:border-box;
  padding:60px 12px 14px;overflow-y:auto;background:rgba(14,17,13,.9);color:#e8e8e0;font:14px system-ui,sans-serif;
  border-left:1px solid #fff2;transform:translateX(100%);transition:transform .18s ease;backdrop-filter:blur(3px)}
.kol-pan.kol-open{transform:none}
.kol-row{display:flex;gap:6px;flex-wrap:wrap;margin:0 0 8px}
.kol-btn{flex:1 1 auto;min-height:36px;padding:6px 10px;border-radius:6px;border:1px solid #fff3;background:#2a3125;color:#eee;cursor:pointer;font:inherit}
.kol-btn:hover{background:#3a4433}
.kol-btn.kol-main{background:#4a6a2a;border-color:#7a9a4a;font-weight:600}
.kol-note,.kol-note2{opacity:.65;font-size:11.5px;margin:2px 0 10px}
.kol-grp{border-top:1px solid #fff2;padding:4px 0}
.kol-grp>summary{cursor:pointer;font-weight:600;padding:4px 0;list-style:none;user-select:none}
.kol-grp>summary::before{content:'▸ ';opacity:.6}
.kol-grp[open]>summary::before{content:'▾ '}
.kol-p{margin:5px 0 8px}
.kol-lab{display:flex;justify-content:space-between;gap:6px;opacity:.9}
.kol-lab.kol-ch{color:#ffd27a}
.kol-ctl{display:flex;gap:6px;align-items:center}
.kol-ctl input[type=range]{flex:1;min-width:0;height:28px;accent-color:#9bc25a}
.kol-num{width:58px;padding:2px 4px;border-radius:4px;border:1px solid #fff3;background:#111;color:#eee;font:inherit}
.kol-seg{display:flex;border:1px solid #fff3;border-radius:6px;overflow:hidden}
.kol-seg button{flex:1;min-height:34px;padding:5px 8px;border:0;background:#20261c;color:#ccc;cursor:pointer;font:inherit}
.kol-seg button+button{border-left:1px solid #fff2}
.kol-seg button.kol-on{background:#4a6a2a;color:#fff}
.kol-seed{width:110px}
.kol-ta{width:100%;box-sizing:border-box;height:90px;background:#111;color:#ddd;border:1px solid #fff3;border-radius:6px;font:11px monospace}
.kol-msg{min-height:14px;font-size:11.5px;color:#b8e08a}
.kol-pan .kol-foc{outline:3px solid #ffd23f;outline-offset:2px;border-radius:6px}
.kol-padnote{display:none;font-size:12.5px;color:#ffd27a;margin:0 0 8px}
.kol-grp>summary{display:flex;align-items:center;gap:6px}
.kol-gdef{margin-left:auto;padding:2px 8px;min-height:26px;border-radius:5px;border:1px solid #fff3;background:#20261c;color:#bbb;font:12px system-ui,sans-serif;cursor:pointer}
.kol-gdef:hover{background:#3a4433;color:#fff}
.kol-pres{margin:2px 0 4px;opacity:.75;font-size:12px}
.kol-preset{font-size:13px;padding:6px 6px}
.kol-pan.kol-bypad .kol-padnote{display:block}
`;

// K16: порядок груп і куди йдуть ключі, яким PARAM_META/VARIANTS дає іншу групу (або жодної — перемикачі)
const GROUPS = ['Відчуття', 'Паротяг', 'Котел', 'Ресурси', 'Гравці', 'Світ', 'Додатки', 'Інше'];
const GROUP_OF = {
  move: 'Відчуття', useMode: 'Відчуття', aim: 'Відчуття', hitMs: 'Відчуття',
  speedCurve: 'Паротяг', overheat: 'Котел',
  bump: 'Гравці', throw: 'Гравці', pass: 'Гравці', stun: 'Гравці',
  geese: 'Додатки', stationMode: 'Додатки',
};
// K16: короткі людські підписи (повний опис — у title)
const SHORT = {
  speedStart: 'Швидкість на старті, кл/с', speedGrowth: 'Розгін: + кл/с щохвилини', speedMax: 'Найшвидше, кл/с',
  stepN: 'Сходинка: кожні N', stepGain: 'Сходинка: + кл/с', startSec: 'Пауза перед рушанням, с',
  heatSec: 'Котел гріється до краю, с', fireSec: 'Пожежа до поразки, с',
  craftSec: 'Рейка в майстерні, с', treeHits: 'Ударів на дерево', rockHits: 'Ударів на скелю',
  woodPerTree: 'Дров з дерева', ironPerRock: 'Заліза зі скелі', storeMax: 'Склад: місць на кожне',
  railsMax: 'Рейок про запас', railsPerCraft: 'Рейок з дерева+заліза', startRails: 'Рейок на старті',
  playerSpeed: 'Швидкість гравця, кл/с', carryMax: 'Несеш у руках до', hitMs: 'Пауза між ударами, мс',
  stripLen: 'Довжина смуги', stripWidth: 'Ширина смуги', forest: 'Ліс', rocks: 'Скелі', water: 'Вода', swamp: 'Болото',
  zoneLen: 'Довжина біому', hardGrow: 'Важчає до станції', swampSlow: 'Хода болотом (частка)',
  geeseSec: 'Гуси: раз на, с', geeseMax: 'Гусей у зграї до', geeseSpeed: 'Гуси: швидкість, кл/с',
  move: 'Рух', useMode: 'Удар', aim: 'Куди діє кнопка', speedCurve: 'Розгін паротяга', overheat: 'Перегрів котла',
  geese: 'Гуси-злодюжки 🪿', stationMode: 'Станція 🏪',
  bump: 'Штовханина 👐', throw: 'Кидок речі 🎯', pass: 'Передача в руки 🤝', stun: 'Сокирою по партнеру 💫',
};
// K16: короткі назви значень перемикачів (повні — у title кнопки)
const VAL_SHORT = {
  useMode: ['Затиснув', 'Натиском', 'Б’є сам'], aim: ['Перед собою', 'Розумний'], speedCurve: ['Від часу', 'За рейки', 'Від шляху'],
  stationMode: ['Кінець', 'Проміжна'],
};
// K16: пресети — від типових; ключі, яких нема в meta/variants, пропускаються
export const PRESETS = {
  cozy: { label: '☕ Затишно', note: 'повільний паротяг, багато ресурсів',
    v: { speedStart: 0.07, speedGrowth: 0.015, speedMax: 0.4, startSec: 45, heatSec: 90, fireSec: 20, craftSec: 1.5,
      woodPerTree: 3, ironPerRock: 3, storeMax: 15, railsMax: 12, startRails: 8 } },
  unrailed: { label: '🚂 Як Unrailed', note: 'клітинками, б\'є сам, розумний приціл',
    v: { move: 'grid', useMode: 'auto', aim: 'smart' } },
  chaos: { label: '🌪 Хаос', note: 'швидкий паротяг, гуси, кидки й штовханина',
    v: { speedStart: 0.2, speedGrowth: 0.06, speedMax: 1.0, startSec: 15, heatSec: 35,
      geese: 'on', geeseSec: 25, throw: 'on', bump: 'on', stun: 'on', pass: 'on' } },
};

export function createPanel(root, opts) {
  const meta = opts.meta || {}, variants = opts.variants || {};
  let values = { ...opts.values };
  const defs = {};
  for (const k in meta) defs[k] = meta[k].def;
  for (const k in variants) defs[k] = variants[k].values[0];

  const el = (tag, cls, txt) => { const e = document.createElement(tag); if (cls) e.className = cls; if (txt != null) e.textContent = txt; return e; };
  const style = el('style'); style.textContent = CSS;
  document.head.appendChild(style);

  const gear = el('button', 'kol-gear', '⚙'); gear.title = 'Налаштування (F1 або `)';
  const pan = el('div', 'kol-pan');
  root.append(gear, pan);

  // кліки по кнопках не лишають фокус на панелі — клавіші знову йдуть у гру
  pan.addEventListener('click', (e) => { if (e.target.tagName === 'BUTTON') e.target.blur(); });
  gear.addEventListener('click', () => { gear.blur(); toggle(); });
  // клавіші з полів вводу панелі не доходять до window (гра слухає window)
  const stopKeys = (e) => {
    const t = e.target.tagName;
    if (t === 'INPUT' || t === 'TEXTAREA' || t === 'SELECT') {
      e.stopPropagation();
      if (e.key === 'Escape' || (e.key === 'Enter' && t === 'INPUT')) e.target.blur();
    }
  };
  pan.addEventListener('keydown', stopKeys); pan.addEventListener('keyup', stopKeys);
  // повзунок після відпускання миші віддає фокус грі (стрілки інакше рухали б повзунок, а не гравця)
  pan.addEventListener('change', (e) => { if (e.target.type === 'range') e.target.blur(); });

  function toggle(open) {
    const was = pan.classList.contains('kol-open');
    const o = open ?? !was;
    pan.classList.toggle('kol-open', o);
    if (!o) { pan.classList.remove('kol-bypad'); setFoc(null); }
    if (!o && pan.contains(document.activeElement)) document.activeElement.blur();
    if (o !== was) opts.onToggle?.(o);
  }
  const onKey = (e) => {
    if (e.key === 'F1' || e.code === 'Backquote') { e.preventDefault(); toggle(); }
  };
  addEventListener('keydown', onKey);

  // — верх: дії
  const r1 = el('div', 'kol-row');
  const bRestart = el('button', 'kol-btn kol-main', '↻ Почати заново (R)');
  bRestart.onclick = () => opts.onRestart?.();
  const audio = opts.audio;
  const bSnd = el('button', 'kol-btn');
  const sndLabel = () => { bSnd.textContent = audio?.enabled ? '🔊' : '🔇'; bSnd.title = audio?.enabled ? 'Вимкнути звук' : 'Увімкнути звук'; };
  bSnd.style.flex = '0 0 42px';
  bSnd.onclick = () => { audio?.setEnabled(!audio.enabled); sndLabel(); };
  sndLabel();
  r1.append(bRestart, bSnd);

  // — сід
  const r2 = el('div', 'kol-row'); r2.style.alignItems = 'center';
  const seedIn = el('input', 'kol-num kol-seed'); seedIn.type = 'number'; seedIn.min = 0; seedIn.title = 'Сід смуги';
  seedIn.value = opts.seed ?? '';
  const bSeedGo = el('button', 'kol-btn', 'Грати сід');
  const bSeedNew = el('button', 'kol-btn', '🎲 Новий сід');
  const seedOk = !!opts.onSeed;
  if (!seedOk) { for (const b of [seedIn, bSeedGo, bSeedNew]) { b.disabled = true; b.title = 'Сід — з адреси ?seed='; } }
  bSeedGo.onclick = () => { const v = Math.floor(+seedIn.value); if (Number.isFinite(v) && v >= 0) opts.onSeed?.(v >>> 0); };
  bSeedNew.onclick = () => { const v = (Math.random() * 1e9) >>> 0; seedIn.value = v; opts.onSeed?.(v); };
  seedIn.addEventListener('keydown', (e) => { if (e.key === 'Enter') bSeedGo.click(); });
  r2.append(el('span', null, 'Сід'), seedIn, bSeedGo, bSeedNew);

  // K16: пресети — лише ставлять значення
  const r4 = el('div', 'kol-row');
  for (const id in PRESETS) {
    const pr = PRESETS[id], b = el('button', 'kol-btn kol-preset', pr.label);
    b.title = pr.note;
    b.onclick = () => {
      const nv = { ...defs };
      for (const k in pr.v) if (k in defs) nv[k] = pr.v[k];
      values = { ...values, ...nv }; refresh(); emit();
      say(`${pr.label}: ${pr.note} — з наступного рестарту (↻ вище)`);
    };
    r4.append(b);
  }
  pan.append(el('div', 'kol-padnote', '🎮 ✚ ↑↓ — рядок · ←→ — значення · A — натиснути · B — закрити'),
    r1, r2, el('div', 'kol-lab kol-pres', 'Пресети'), r4, el('div', 'kol-note2', 'Зміни світу й правил діють з наступного рестарту.'));

  // — пристрій (K12): діє одразу, не правила
  const prefs = { quality: 'high', rumble: true, tips: true, ...(opts.prefs || {}) };
  const prefCtl = [];
  const devGroup = () => {
    const d = el('details', 'kol-grp'); d.open = true;
    d.append(el('summary', null, 'Пристрій'));
    const seg = (label, k, vals, labels) => {
      const box = el('div', 'kol-p');
      const s = el('div', 'kol-seg');
      const btns = vals.map((val, i) => {
        const b = el('button', null, labels[i]);
        b.onclick = () => { if (prefs[k] !== val) { prefs[k] = val; put(); opts.onPref?.(k, val); } };
        s.append(b); return b;
      });
      const put = () => btns.forEach((b, i) => b.classList.toggle('kol-on', vals[i] === prefs[k]));
      put(); prefCtl.push(put);
      box.append(el('div', 'kol-lab', label), s); d.append(box);
    };
    seg('Якість картинки', 'quality', ['high', 'low'], ['Висока', 'Низька (слабкий ноут)']);
    seg('Вібрація (пад, телефон)', 'rumble', [true, false], ['Увімк.', 'Вимк.']);
    seg('Підказки', 'tips', [true, false], ['Так', 'Ні']);   // K16
    return d;
  };

  // — групи (K16: порядок GROUPS; у групі спершу перемикачі, тоді повзунки; «Типові» на групу)
  const ctl = {};   // k → { set(v) }
  const groups = {}, groupKeys = {};
  const groupOf = (k, own) => GROUP_OF[k] ?? (GROUPS.includes(own) ? own : 'Інше');
  for (const k in variants) (groupKeys[groupOf(k, variants[k].group)] ??= []).push(k);
  for (const k in meta) (groupKeys[groupOf(k, meta[k].group)] ??= []).push(k);
  for (const g of [...GROUPS, ...Object.keys(groupKeys).filter((g) => !GROUPS.includes(g))]) {
    if (!groupKeys[g]) continue;
    const d = el('details', 'kol-grp'); d.open = true;
    const sum = el('summary', null, g);
    const bDef = el('button', 'kol-gdef', 'Типові'); bDef.title = `Скинути «${g}» до типових`;
    bDef.onclick = (e) => {
      e.preventDefault(); e.stopPropagation();
      for (const k of groupKeys[g]) values[k] = defs[k];
      refresh(); emit(); say(`«${g}» — типові, з наступного рестарту`);
    };
    sum.append(bDef); d.append(sum);
    groups[g] = d; pan.append(d);
  }
  for (const k in variants) {
    const v = variants[k];
    const box = el('div', 'kol-p');
    const lab = el('div', 'kol-lab', SHORT[k] ?? v.label ?? k); lab.title = v.label ?? k;
    const seg = el('div', 'kol-seg');
    const btns = v.values.map((val, i) => {
      const b = el('button', null, VAL_SHORT[k]?.[i] ?? v.labels?.[i] ?? val);
      b.title = v.labels?.[i] ?? val;
      b.onclick = () => { if (values[k] !== val) { values[k] = val; put(val); emit(); } };
      seg.append(b); return b;
    });
    const put = (val) => { btns.forEach((b, i) => b.classList.toggle('kol-on', v.values[i] === val)); lab.classList.toggle('kol-ch', val !== v.values[0]); };
    box.append(lab, seg); groups[groupOf(k, v.group)].append(box);
    ctl[k] = { set: put };
  }
  for (const k in meta) {
    const m = meta[k], g = groupOf(k, m.group);
    const box = el('div', 'kol-p');
    const lab = el('div', 'kol-lab');
    const name = el('span', null, SHORT[k] ?? m.label ?? k);
    lab.append(name); lab.title = `${m.label || k} · типово ${m.def}`;
    const c = el('div', 'kol-ctl');
    const rg = el('input'); rg.type = 'range'; rg.min = m.min; rg.max = m.max; rg.step = m.step;
    const num = el('input', 'kol-num'); num.type = 'number'; num.min = m.min; num.max = m.max; num.step = m.step;
    c.append(rg, num); box.append(lab, c); groups[g].append(box);
    const dec = String(m.step).includes('.') ? String(m.step).split('.')[1].length : 0;
    const put = (v) => { rg.value = v; num.value = (+v).toFixed(dec); lab.classList.toggle('kol-ch', +v !== +m.def); };
    const commit = (raw) => {
      let v = +raw; if (!Number.isFinite(v)) { put(values[k]); return; }
      v = Math.min(m.max, Math.max(m.min, v));
      v = +(Math.round(v / m.step) * m.step).toFixed(dec + 2);
      put(v);
      if (values[k] !== v) { values[k] = v; emit(); }
    };
    rg.addEventListener('input', () => commit(rg.value));
    num.addEventListener('change', () => commit(num.value));
    ctl[k] = { set: put };
  }

  // — пристрій — останнім; перед ним прихований якір .kol-note: «🎨 Вигляд» (K5) стає сюди ж, між «Додатками» й «Пристроєм»
  const anchor = el('div', 'kol-note'); anchor.style.display = 'none';
  pan.append(anchor, devGroup());

  // — низ: скинути / копіювати / вставити
  const r3 = el('div', 'kol-row'); r3.style.marginTop = '10px';
  const bReset = el('button', 'kol-btn', 'Скинути до типових');
  const bCopy = el('button', 'kol-btn', '📋 Скопіювати налаштування');
  const bPaste = el('button', 'kol-btn', 'Вставити');
  r3.append(bReset, bCopy, bPaste);
  const msg = el('div', 'kol-msg');
  const ta = el('textarea', 'kol-ta'); ta.style.display = 'none';
  ta.placeholder = 'JSON налаштувань… (Enter у полі не потрібен — натисни «Вставити» ще раз)';
  pan.append(r3, msg, ta);
  let msgT = 0;
  const say = (t) => { msg.textContent = t; clearTimeout(msgT); msgT = setTimeout(() => { msg.textContent = ''; }, 3000); };

  bReset.onclick = () => { values = { ...values, ...defs }; refresh(); emit(); say('Типові — з наступного рестарту'); };
  bCopy.onclick = async () => {
    const txt = JSON.stringify(values);
    try { await navigator.clipboard.writeText(txt); say('Скопійовано в буфер'); }
    catch { ta.style.display = ''; ta.value = txt; ta.focus(); ta.select(); say('Буфер недоступний — скопіюй із поля'); }
  };
  bPaste.onclick = async () => {
    let txt = ta.style.display !== 'none' && ta.value.trim() && ta.value.trim() !== JSON.stringify(values) ? ta.value : null;
    if (txt == null) {
      try { txt = await navigator.clipboard.readText(); }
      catch { ta.style.display = ''; ta.value = ''; ta.focus(); say('Встав JSON у поле й натисни «Вставити»'); return; }
    }
    if (!apply(txt)) say('Не схоже на налаштування Колії');
    else { ta.style.display = 'none'; say('Вставлено — з наступного рестарту'); }
  };
  function apply(txt) {
    let o; try { o = JSON.parse(txt); } catch { return false; }
    if (!o || typeof o !== 'object') return false;
    let n = 0;
    for (const k in o) {
      if (meta[k] && Number.isFinite(+o[k])) { values[k] = Math.min(meta[k].max, Math.max(meta[k].min, +o[k])); n++; }
      else if (variants[k] && variants[k].values.includes(o[k])) { values[k] = o[k]; n++; }
    }
    if (!n) return false;
    refresh(); emit(); return true;
  }

  function emit() { opts.onChange?.({ ...values }); }
  function refresh() { for (const k in ctl) ctl[k].set(values[k] ?? defs[k]); }
  refresh();

  // — керування з пада (K12): фокус-рамка ходить по рядках, ←→ міняють значення
  let foc = null;
  function setFoc(e) {
    if (foc) foc.classList.remove('kol-foc');
    foc = e;
    if (foc) { foc.classList.add('kol-foc'); foc.scrollIntoView?.({ block: 'nearest' }); }
  }
  // цілі: кнопки поза перемикачами, перемикачі (.kol-seg), повзунки (.kol-p з range), заголовки груп
  function targets() {
    const out = [];
    for (const e of pan.querySelectorAll('button, .kol-seg, .kol-p, summary')) {
      if (e.disabled || !e.getClientRects().length) continue;
      if (e.tagName === 'BUTTON' && e.parentElement.classList.contains('kol-seg')) continue;
      if (e.classList.contains('kol-p') && !e.querySelector('input[type=range]')) continue;
      out.push(e);
    }
    return out;
  }
  function nav(key) {
    if (key === 'b' || key === 'menu') { toggle(false); return; }
    if (!pan.classList.contains('kol-open')) toggle(true);
    pan.classList.add('kol-bypad');
    const list = targets();
    if (!list.length) return;
    let i = list.indexOf(foc);
    if (i < 0) { setFoc(list[0]); return; }
    const sameRow = (a, b) => a && b && a.tagName === 'BUTTON' && b.tagName === 'BUTTON' && a.parentElement === b.parentElement;
    if (key === 'up' || key === 'down') {
      // кнопки одного рядка — один крок
      const d = key === 'down' ? 1 : -1;
      let j = i + d;
      while (j >= 0 && j < list.length && sameRow(list[j], list[i])) j += d;
      if (j >= 0 && j < list.length) setFoc(key === 'up' && list[j].tagName === 'BUTTON'
        ? list.find((e) => sameRow(e, list[j]) || e === list[j]) : list[j]);
      return;
    }
    if (key === 'left' || key === 'right') {
      const d = key === 'right' ? 1 : -1;
      if (foc.classList.contains('kol-seg')) {
        const bs = [...foc.children], k = bs.findIndex((b) => b.classList.contains('kol-on'));
        const n = bs[Math.max(0, Math.min(bs.length - 1, (k < 0 ? 0 : k) + d))];
        n?.click();
      } else if (foc.classList.contains('kol-p')) {
        const rg = foc.querySelector('input[type=range]');
        const st = +rg.step || 1, v = Math.min(+rg.max, Math.max(+rg.min, +rg.value + d * st));
        rg.value = v; rg.dispatchEvent(new Event('input', { bubbles: true }));
      } else if (sameRow(list[i + d], foc)) setFoc(list[i + d]);
      return;
    }
    if (key === 'a') {
      if (foc.tagName === 'SUMMARY') { foc.parentElement.open = !foc.parentElement.open; return; }
      if (foc.tagName === 'BUTTON') foc.click();
    }
  }

  return {
    set(v) { values = { ...values, ...v }; refresh(); },
    setSeed(s) { seedIn.value = s; },
    setPrefs(p) { Object.assign(prefs, p); prefCtl.forEach((f) => f()); },
    isOpen: () => pan.classList.contains('kol-open'),
    nav,
    toggle,
    dispose() { removeEventListener('keydown', onKey); clearTimeout(msgT); gear.remove(); pan.remove(); style.remove(); },
  };
}
