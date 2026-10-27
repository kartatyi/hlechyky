// «Колія» — панель повзунків і перемикачів. Власник: K4.
// Контракт: createPanel(root, { meta, variants, values, onChange(values), onRestart(), audio, seed?, onSeed?(seed) })
//   → { set(values), setSeed(seed), toggle(open?), dispose() }
//  meta = PARAM_META (повзунки за групами), variants = VARIANTS (перемикачі); onChange — після кожної правки (main.js
//  зберігає в localStorage і застосовує з наступного рестарту); onRestart — кнопка «Почати заново».
//  onSeed(seed) — ОПЦІЙНО: поле сіду / «новий сід» (оркестратор має підключити: game.seed = seed; restart()).
//  Відкрити/закрити — кнопка ⚙ або F1 чи `. Стан звуку не зберігається: на старті завжди 🔇.

const CSS = `
.kol-gear{position:fixed;right:8px;top:8px;z-index:30;width:34px;height:34px;border-radius:8px;border:1px solid #fff3;
  background:rgba(15,18,14,.7);color:#eee;font-size:18px;cursor:pointer;line-height:1}
.kol-gear:hover{background:rgba(40,46,36,.85)}
.kol-pan{position:fixed;right:0;top:0;bottom:0;width:320px;max-width:92vw;z-index:29;box-sizing:border-box;
  padding:48px 10px 12px;overflow-y:auto;background:rgba(14,17,13,.86);color:#e8e8e0;font:12.5px system-ui,sans-serif;
  border-left:1px solid #fff2;transform:translateX(100%);transition:transform .18s ease;backdrop-filter:blur(3px)}
.kol-pan.kol-open{transform:none}
.kol-row{display:flex;gap:6px;flex-wrap:wrap;margin:0 0 8px}
.kol-btn{flex:1 1 auto;padding:5px 8px;border-radius:6px;border:1px solid #fff3;background:#2a3125;color:#eee;cursor:pointer;font:inherit}
.kol-btn:hover{background:#3a4433}
.kol-btn.kol-main{background:#4a6a2a;border-color:#7a9a4a;font-weight:600}
.kol-note{opacity:.65;font-size:11.5px;margin:2px 0 10px}
.kol-grp{border-top:1px solid #fff2;padding:4px 0}
.kol-grp>summary{cursor:pointer;font-weight:600;padding:4px 0;list-style:none;user-select:none}
.kol-grp>summary::before{content:'▸ ';opacity:.6}
.kol-grp[open]>summary::before{content:'▾ '}
.kol-p{margin:5px 0 8px}
.kol-lab{display:flex;justify-content:space-between;gap:6px;opacity:.9}
.kol-lab.kol-ch{color:#ffd27a}
.kol-ctl{display:flex;gap:6px;align-items:center}
.kol-ctl input[type=range]{flex:1;min-width:0;accent-color:#9bc25a}
.kol-num{width:58px;padding:2px 4px;border-radius:4px;border:1px solid #fff3;background:#111;color:#eee;font:inherit}
.kol-seg{display:flex;border:1px solid #fff3;border-radius:6px;overflow:hidden}
.kol-seg button{flex:1;padding:4px 6px;border:0;background:#20261c;color:#ccc;cursor:pointer;font:inherit}
.kol-seg button+button{border-left:1px solid #fff2}
.kol-seg button.kol-on{background:#4a6a2a;color:#fff}
.kol-seed{width:110px}
.kol-ta{width:100%;box-sizing:border-box;height:90px;background:#111;color:#ddd;border:1px solid #fff3;border-radius:6px;font:11px monospace}
.kol-msg{min-height:14px;font-size:11.5px;color:#b8e08a}
`;

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
    const o = open ?? !pan.classList.contains('kol-open');
    pan.classList.toggle('kol-open', o);
    if (!o && pan.contains(document.activeElement)) document.activeElement.blur();
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

  pan.append(r1, r2, el('div', 'kol-note', 'Зміни світу й правил діють з наступного рестарту.'));

  // — групи повзунків
  const ctl = {};   // k → { set(v) }
  const groups = {};
  for (const k in meta) {
    const m = meta[k], g = m.group || 'Інше';
    if (!groups[g]) {
      const d = el('details', 'kol-grp'); d.open = true;
      d.append(el('summary', null, g));
      groups[g] = d; pan.append(d);
    }
    const box = el('div', 'kol-p');
    const lab = el('div', 'kol-lab');
    const name = el('span', null, m.label || k);
    lab.append(name); lab.title = `${k} · типово ${m.def}`;
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

  // — перемикачі
  if (Object.keys(variants).length) {
    const d = el('details', 'kol-grp'); d.open = true;
    d.append(el('summary', null, 'Варіанти правил'));
    pan.append(d);
    for (const k in variants) {
      const v = variants[k];
      const box = el('div', 'kol-p');
      const lab = el('div', 'kol-lab', v.label || k);
      const seg = el('div', 'kol-seg');
      const btns = v.values.map((val, i) => {
        const b = el('button', null, v.labels?.[i] ?? val);
        b.onclick = () => { if (values[k] !== val) { values[k] = val; put(val); emit(); } };
        seg.append(b); return b;
      });
      const put = (val) => { btns.forEach((b, i) => b.classList.toggle('kol-on', v.values[i] === val)); lab.classList.toggle('kol-ch', val !== v.values[0]); };
      box.append(lab, seg); d.append(box);
      ctl[k] = { set: put };
    }
  }

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

  return {
    set(v) { values = { ...values, ...v }; refresh(); },
    setSeed(s) { seedIn.value = s; },
    toggle,
    dispose() { removeEventListener('keydown', onKey); clearTimeout(msgT); gear.remove(); pan.remove(); style.remove(); },
  };
}
