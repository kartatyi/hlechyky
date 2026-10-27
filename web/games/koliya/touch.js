// «Колія» — керування з телефона й планшета (K27).
// Контракт: createTouch(opts?) → { read(): { mx, my (-1..1), use (затиснуто), throw, horn (раз на натиск) } | null,
//   active (чи вже показали дотикове керування), swallow(), setHidden(on) (K42: повтор), setVibrate(on), buzz('tap'|'hit'|'lost'), dispose() }
//   opts: { canvas (зона стіка), root (куди вставити шар; document.body) }.
//   input.js (гачок setTouch) зливає read() у гравця 0. ⚙ — кнопка панелі (K4), ⏸ — кнопка паузи HUD (K23): обидві 46 px,
//   пальцем досяжні, тут не дублюємо.
// Показуємо лише на дотиковому пристрої: (pointer: coarse) одразу або з першого дотику; мишку на ПК не чіпаємо
// (стік слухає лише pointerType touch/pen, шар кнопок прозорий для подій поза кнопками).
// Стік плаваючий: де торкнувся в лівій ~60 % екрана — там центр; палець далі за радіус — центр тягнеться слідом.
// Портрет: не блокуємо — компактні кнопки + підказка «поверни телефон» (колія тягнеться вбік, боком видно далі),
//   її можна закрити ✕ (до кінця сесії). Пояснення вибору: друг, що зайшов за посиланням, має грати одразу, без примусу.
// Чисті функції (stickVec, follow) — для node-тестів, без DOM.

export const DEAD = 0.18;   // мертва зона, частка радіуса
export const FULL = 0.8;    // з цієї частки радіуса — повна швидкість

/** Вектор стіка зі зсуву пальця (dx, dy) у пікселях: { mx, my } у -1..1 (довжина ≤ 1), мертва зона й «повна» межа. */
export function stickVec(dx, dy, radius, dead = DEAD, full = FULL) {
  const r = Math.hypot(dx, dy) / (radius > 0 ? radius : 1);
  if (!(r > dead)) return { mx: 0, my: 0 };
  const m = Math.min(1, (r - dead) / Math.max(1e-6, full - dead));
  const k = m / r / (radius > 0 ? radius : 1);
  return { mx: dx * k, my: dy * k };
}

/** Плаваючий центр: палець далі за radius — центр підтягується так, щоб палець лишився на колі. → [cx, cy] */
export function follow(cx, cy, x, y, radius) {
  const dx = x - cx, dy = y - cy, d = Math.hypot(dx, dy);
  if (d <= radius) return [cx, cy];
  const k = (d - radius) / d;
  return [cx + dx * k, cy + dy * k];
}

/** Чи це дотиковий пристрій (без події): грубий вказівник і без наведення. */
export function isTouchDevice() {
  try { return matchMedia('(pointer: coarse)').matches && !matchMedia('(any-pointer: fine)').matches; } catch { return false; }
}

const CSS = `
html.kol-touch, html.kol-touch body { overscroll-behavior: none; -webkit-user-select: none; user-select: none;
  -webkit-touch-callout: none; -webkit-tap-highlight-color: transparent; }
.kol-tl { position: fixed; inset: 0; z-index: 4; pointer-events: none; display: none; font: 600 14px system-ui, sans-serif; color: #f4efe2; }
html.kol-touch .kol-tl { display: block; }
.kol-tl * { box-sizing: border-box; }
.kol-sb, .kol-sk { position: absolute; left: 0; top: 0; border-radius: 50%; pointer-events: none; transform: translate(-50%, -50%); }
.kol-sb { border: 2px solid #ffffff55; background: #0b0d0955; }
.kol-sk { background: #f4efe2bb; box-shadow: 0 1px 6px #000a; }
.kol-tl.kol-idle .kol-sb, .kol-tl.kol-idle .kol-sk { opacity: .35; }
.kol-sb-tip { position: absolute; left: 50%; top: 100%; transform: translate(-50%, 6px); white-space: nowrap; font-size: 12px; opacity: .9; }
.kol-tl:not(.kol-idle) .kol-sb-tip { display: none; }
.kol-tb { position: absolute; pointer-events: auto; touch-action: none; border-radius: 50%; display: flex; flex-direction: column;
  align-items: center; justify-content: center; line-height: 1; background: #0f120dcc; border: 2px solid #ffffff44;
  color: #fff; text-shadow: 0 1px 2px #000a; transition: transform .06s, background .06s; }
.kol-tb small { font-size: 11px; margin-top: 3px; font-weight: 600; opacity: .9; }
.kol-tb.kol-on { background: #4a6a2aee; border-color: #9bc25a; transform: scale(.93); }
.kol-tuse { --s: clamp(78px, 25vmin, 112px); width: var(--s); height: var(--s); font-size: calc(var(--s) * .36);
  right: calc(16px + env(safe-area-inset-right)); bottom: calc(16px + env(safe-area-inset-bottom)); }
.kol-tthrow, .kol-thorn { --s: clamp(54px, 16vmin, 74px); width: var(--s); height: var(--s); font-size: calc(var(--s) * .38); }
.kol-tthrow { right: calc(28px + env(safe-area-inset-right) + clamp(78px, 25vmin, 112px)); bottom: calc(12px + env(safe-area-inset-bottom)); }
.kol-thorn { right: calc(10px + env(safe-area-inset-right)); bottom: calc(26px + env(safe-area-inset-bottom) + clamp(78px, 25vmin, 112px)); }
@media (orientation: portrait) {
  .kol-tuse { --s: clamp(72px, 21vmin, 96px); }
  .kol-tthrow, .kol-thorn { --s: clamp(50px, 14vmin, 64px); }
}
.kol-tpo { position: absolute; left: 50%; bottom: calc(45% + env(safe-area-inset-bottom)); transform: translateX(-50%); max-width: calc(100vw - 24px); display: none;
  pointer-events: auto; align-items: center; gap: .6em; padding: .45em .5em .45em .8em; border-radius: 10px; background: #171b13ee;
  border: 1px solid #ffffff2a; font-size: 13px; }
.kol-tpo button { font: inherit; font-size: 18px; color: #fff; background: none; border: 0; min-width: 36px; min-height: 36px; }
@media (orientation: portrait) { .kol-tl.kol-po .kol-tpo { display: flex; } }
/* екрани HUD (K9/K23): на дотику клавіші ховаємо, пояснюємо пальцем. Старт: .kol-keys + .kol-hint + .kol-go;
   кінець: .kol-sum … .kol-go (лишається кнопка «Ще раз»); пауза: «Esc — продовжити» */
html.kol-touch .kol-keys, html.kol-touch .kol-keys + .kol-hint, html.kol-touch .kol-keys + .kol-hint + .kol-go > span,
html.kol-touch .kol-sum ~ .kol-go, html.kol-touch .kol-pnet ~ .kol-hint:last-child { display: none; }
html.kol-touch .kol-keys + .kol-hint + .kol-go::before { content: 'Ліворуч веди пальцем — ходити · ✋ дія · 🎯 кинути · 📯 гудок';
  display: block; font-size: .8em; font-weight: 500; margin-bottom: .5em; animation: none; }
html.kol-touch .kol-keys + .kol-hint + .kol-go::after { content: '👆 Торкнись — почати'; }
`;

const BTN = [
  ['use', 'kol-tuse', '✋', 'Дія'],
  ['throw', 'kol-tthrow', '🎯', 'Кинути'],
  ['horn', 'kol-thorn', '📯', 'Гудок'],
];

export function createTouch(opts = {}) {
  const doc = document, html = doc.documentElement;
  const canvas = opts.canvas || doc.querySelector('canvas');
  const style = doc.createElement('style'); style.textContent = CSS; doc.head.append(style);
  const layer = doc.createElement('div'); layer.className = 'kol-tl kol-idle';
  (opts.root || doc.body).append(layer);
  const el = (cls, parent, htmlText) => { const e = doc.createElement('div'); e.className = cls; if (htmlText) e.innerHTML = htmlText; parent.append(e); return e; };
  const base = el('kol-sb', layer, '<span class="kol-sb-tip">веди пальцем</span>');
  const knob = el('kol-sk', layer);
  const btns = {};
  for (const [k, cls, ico, cap] of BTN) { const b = el('kol-tb ' + cls, layer, `${ico}<small>${cap}</small>`); b.dataset.k = k; btns[k] = b; }
  const po = el('kol-tpo', layer, '<span>📱↻ Поверни телефон боком — колію видно далі. Можна й так.</span><button aria-label="Закрити">✕</button>');
  let poClosed = false;
  try { poClosed = sessionStorage.getItem('kol-po') === '1'; } catch { }

  let active = false, vibrate = true;
  let R = 56;                                     // радіус стіка, px
  let sid = null, cx = 0, cy = 0, fx = 0, fy = 0; // палець стіка: id, центр, поточна точка
  const held = new Map();                         // pointerId → ключ кнопки
  const hits = new Set();                         // throw/horn з минулого read()

  function radius() { R = Math.max(40, Math.min(72, 0.16 * Math.min(innerWidth, innerHeight))); return R; }
  function ghost() {   // стік у спокої — блідий у лівому нижньому куті (підказка, де водити)
    radius();
    put(R * 1.6 + 12, innerHeight - R * 1.6 - 12, 0, 0);
  }
  function put(x, y, kx, ky) {
    base.style.width = base.style.height = 2 * R + 'px';
    knob.style.width = knob.style.height = 0.9 * R + 'px';
    base.style.transform = `translate(${x - R}px, ${y - R}px)`;
    knob.style.transform = `translate(${x + kx - 0.45 * R}px, ${y + ky - 0.45 * R}px)`;
  }
  function draw() {
    if (sid == null) { layer.classList.add('kol-idle'); ghost(); return; }
    layer.classList.remove('kol-idle');
    const dx = fx - cx, dy = fy - cy, d = Math.hypot(dx, dy), k = d > R ? R / d : 1;
    put(cx, cy, dx * k, dy * k);
  }
  let poTimer = 0, wasPortrait = null;
  function orient() {   // підказка в портреті — до ✕ (на сесію) або 12 с (до наступного повороту)
    const portrait = innerHeight > innerWidth;
    if (portrait === wasPortrait && !poClosed) return;   // resize від панелі адреси — не показувати знову
    wasPortrait = portrait;
    const on = !poClosed && portrait;
    layer.classList.toggle('kol-po', on);
    clearTimeout(poTimer);
    if (on) poTimer = setTimeout(() => layer.classList.remove('kol-po'), 12000);
  }
  function activate() {
    if (active) return;
    active = true; html.classList.add('kol-touch');
    orient(); draw();
  }

  // — стік: дотики по канві (мишку не чіпаємо)
  const touchy = (e) => e.pointerType === 'touch' || e.pointerType === 'pen';
  const onDown = (e) => {
    if (!touchy(e)) return;
    activate();
    e.preventDefault();
    if (sid != null || e.clientX > innerWidth * 0.6) return;
    sid = e.pointerId; radius();
    cx = fx = e.clientX; cy = fy = e.clientY;
    try { canvas.setPointerCapture(sid); } catch { }
    draw();
  };
  const onMove = (e) => {
    if (e.pointerId !== sid) return;
    fx = e.clientX; fy = e.clientY;
    [cx, cy] = follow(cx, cy, fx, fy, R);
    draw();
  };
  const onUp = (e) => { if (e.pointerId === sid) { sid = null; draw(); } };
  canvas?.addEventListener('pointerdown', onDown);
  addEventListener('pointermove', onMove);
  addEventListener('pointerup', onUp);
  addEventListener('pointercancel', onUp);
  canvas?.addEventListener('lostpointercapture', onUp);

  // — кнопки (мультитач: у кожного пальця своя)
  const press = (e) => {
    const b = e.target.closest?.('[data-k]');
    if (!b || !layer.contains(b)) return;
    e.preventDefault();
    activate();
    const k = b.dataset.k;
    held.set(e.pointerId, k); b.classList.add('kol-on');
    try { b.setPointerCapture(e.pointerId); } catch { }
    if (k === 'throw' || k === 'horn') hits.add(k);
    buzz('tap');
  };
  const release = (e) => {
    const k = held.get(e.pointerId); if (!k) return;
    held.delete(e.pointerId);
    if (![...held.values()].includes(k)) btns[k]?.classList.remove('kol-on');
  };
  layer.addEventListener('pointerdown', press);
  layer.addEventListener('pointerup', release);
  layer.addEventListener('pointercancel', release);
  layer.addEventListener('lostpointercapture', release);
  po.querySelector('button').addEventListener('click', () => { poClosed = true; try { sessionStorage.setItem('kol-po', '1'); } catch { } orient(); });

  // — без меню на довгий натиск, зуму жестом (iOS), скролу
  const noMenu = (e) => { if (active && !e.target.closest?.('input, textarea, .kol-pan')) e.preventDefault(); };
  const noGesture = (e) => e.preventDefault();
  addEventListener('contextmenu', noMenu);
  doc.addEventListener('gesturestart', noGesture);
  const firstTouch = (e) => { if (touchy(e)) activate(); };
  addEventListener('pointerdown', firstTouch, true);
  const onResize = () => { orient(); draw(); };
  addEventListener('resize', onResize);
  const reset = () => { sid = null; held.clear(); hits.clear(); for (const k in btns) btns[k].classList.remove('kol-on'); draw(); };
  const onVis = () => { if (doc.hidden) reset(); };
  doc.addEventListener('visibilitychange', onVis);
  addEventListener('blur', reset);

  function buzz(kind) {
    if (!vibrate || !active || typeof navigator.vibrate !== 'function') return;
    if (navigator.userActivation && !navigator.userActivation.hasBeenActive) return;   // до першого справжнього дотику браузер однаково не дасть
    const pat = kind === 'lost' ? [60, 40, 120] : kind === 'hit' ? 18 : 10;
    try { navigator.vibrate(pat); } catch { }
  }

  if (isTouchDevice()) activate(); else draw();

  return {
    get active() { return active; },
    read() {
      if (!active) return null;
      const o = sid == null ? { mx: 0, my: 0 } : stickVec(fx - cx, fy - cy, R);
      const keys = new Set(held.values());
      const r = { mx: o.mx, my: o.my, use: keys.has('use'), throw: hits.has('throw'), horn: hits.has('horn') };
      hits.clear();
      return r;
    },
    swallow() { hits.clear(); },
    // K42: на час повтору (replay.js) шар стіка й кнопок ховаємо — пальці не тиснуть у гру, що стоїть
    setHidden(on) { layer.style.display = on ? 'none' : ''; if (on) reset(); },
    setVibrate(on) { vibrate = !!on; },
    buzz,
    dispose() {
      canvas?.removeEventListener('pointerdown', onDown); canvas?.removeEventListener('lostpointercapture', onUp);
      removeEventListener('pointermove', onMove); removeEventListener('pointerup', onUp); removeEventListener('pointercancel', onUp);
      removeEventListener('contextmenu', noMenu); doc.removeEventListener('gesturestart', noGesture);
      removeEventListener('pointerdown', firstTouch, true); removeEventListener('resize', onResize);
      doc.removeEventListener('visibilitychange', onVis); removeEventListener('blur', reset);
      layer.remove(); style.remove(); html.classList.remove('kol-touch');
    },
  };
}
