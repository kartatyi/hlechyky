// «Хутори» — чисті рішення жестів поля (без DOM і three.js; тести — dev/gestures.test.mjs).
// Поле не забирає в сторінки прокрутку: колесо без Ctrl/⌘ і одне-пальцеве вертикальне ведення гортають сторінку,
// камера — Ctrl/⌘ + колесо (і щипок тачпада — він приходить як ctrl+wheel) та два пальці.

/** Що робить колесо над полем: 'zoom' (Ctrl/⌘ або щипок тачпада) чи 'scroll' (лишити браузеру). */
export function wheelAction(e) {
  return e && (e.ctrlKey || e.metaKey) ? 'zoom' : 'scroll';
}

/** Множник наближення на одну подію колеса. Рядки (deltaMode 1) — грубо; дрібні пікселі (щипок тачпада) — чутливіше. */
export function wheelZoomFactor(deltaY, deltaMode = 0) {
  const dy = Number(deltaY) || 0;
  const k = deltaMode === 1 ? 0.05 : deltaMode === 2 ? 0.5 : (Math.abs(dy) < 40 ? 0.006 : 0.0015);
  return Math.exp(-dy * k);
}

/** Кут у (-π, π]. */
export function wrapAngle(a) {
  let x = a % (2 * Math.PI);
  if (x <= -Math.PI) x += 2 * Math.PI;
  else if (x > Math.PI) x -= 2 * Math.PI;
  return x;
}

/** Два пальці: було a0,b0 — стало a1,b1 ({x,y} в пікселях екрана, y донизу).
 *  scale — у скільки разів розвели пальці; rot — поворот лінії між пальцями (радіани, + = за годинниковою на екрані);
 *  dx, dy — зсув середини. */
export function twoFinger(a0, b0, a1, b1) {
  const d0 = Math.hypot(a0.x - b0.x, a0.y - b0.y) || 1, d1 = Math.hypot(a1.x - b1.x, a1.y - b1.y) || 1;
  const r0 = Math.atan2(b0.y - a0.y, b0.x - a0.x), r1 = Math.atan2(b1.y - a1.y, b1.x - a1.x);
  return {
    scale: d1 / d0,
    rot: wrapAngle(r1 - r0),
    dx: (a1.x + b1.x) / 2 - (a0.x + b0.x) / 2,
    dy: (a1.y + b1.y) / 2 - (a0.y + b0.y) / 2,
  };
}

/** Чи показувати підказку зараз: не частіше ніж раз на gap мс. */
export function hintDue(now, lastShown, gap) {
  return !(lastShown > 0) || now - lastShown >= gap;
}
