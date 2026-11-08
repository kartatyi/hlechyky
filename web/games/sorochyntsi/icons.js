// «Сорочинський ярмарок» — власні значки товарів і нагород (замість емодзі, які на Windows, Android і iOS різні й
// на полі виглядали вицвілими). Один набір для пласкої мапи (SVG), лиць крамниць у 3D (canvas, Path2D) і картки (HTML).
//
// Значок — у квадраті 32 × 32: `shape` — заливка (силует), `sline` — штрихи кольору силуету (стебло, ручка, спиця),
// `line` — штрихи деталей, `dot` — кружечки деталей [cx, cy, r].
// Малюється двома кольорами: fill (силует) і detail (штрихи й крапки). На жетоні — світлий силует з деталями кольору
// жетона; на крамниці — силует кольору товару з темнішими деталями.

export const ICONS = {
  // рушник на кілочку з червоною вишивкою й торочками
  linen: {
    shape: ['M4.5 3.5h23v3h-23z', 'M8 6h16v20.5l-2 2.5-2-2.5-2 2.5-2-2.5-2 2.5-2-2.5-2 2.5-2-2.5z'],
    line: ['M9.6 18.4l2.1-2.1 2.1 2.1 2.2-2.1 2.2 2.1 2.1-2.1 2.1 2.1', 'M9.6 22.8l2.1-2.1 2.1 2.1 2.2-2.1 2.2 2.1 2.1-2.1 2.1 2.1',
      'M16 9.4l2.1 2.1-2.1 2.1-2.1-2.1z'],
  },
  // рибина
  fish: {
    shape: ['M2.5 16C7 8.5 17 8 22.5 14L29.5 8.5 28 16l1.5 7.5-7-5.5C17 24 7 23.5 2.5 16z'],
    line: ['M12.5 11q2.2 5 0 10', 'M15 10.2l2.5-3.4 2.3 4.4'],
    dot: [[8.3, 14.6, 1.5]],
  },
  // пучок зілля, перев'язаний
  herbs: {
    shape: ['M16 11C11.8 8 11.8 3.2 16 1c4.2 2.2 4.2 7 0 10z', 'M14.6 16.2C9 16.7 5.4 13.2 5.8 8.6c4.6-.2 8.6 3 8.8 7.6z',
      'M17.4 14.2c.4-4.7 4.1-7.8 8.8-7.2.2 4.5-3.9 7.8-8.8 7.2z', 'M14.6 21.8C10 24 5.4 22.1 4.4 18.2c3.9-1.9 8.4-.4 10.2 3.6z',
      'M17.4 19.8c3-3.7 7.8-4.1 10.6-1.2-2.7 3.3-7.4 3.9-10.6 1.2z', 'M12.6 24.4h6.8v3.2h-6.8z'],
    sline: ['M16 31V8'],
    line: ['M16 10V3.5', 'M13.6 15.2 8.2 10.4', 'M18.4 13.2l5.4-4.6', 'M13.4 20.8 7 18.6', 'M18.6 18.9l6.4-1'],
  },
  // глечик з ручкою й розписом
  clay: {
    shape: ['M11.5 2.5h9v2.5h-1.3v3c5 2 8.3 6 8.3 11.3 0 6.4-5.2 10.2-11.5 10.2S4.5 25.7 4.5 19.3c0-5.3 3.3-9.3 8.3-11.3V5h-1.3z'],
    sline: ['M20.5 8.8c6.5-.6 8 7.4 3.2 10.4'],
    line: ['M7 17.5q2.25-2.4 4.5 0t4.5 0 4.5 0 4.5 0', 'M8.4 22.5h15.2'],
  },
  // клубок вовни зі спицею
  wool: {
    shape: ['M16 5.5a11.5 11.5 0 1 1 0 23 11.5 11.5 0 1 1 0-23z'],
    sline: ['M3 29.5 21 3.5', 'M26.5 22c3.2 2.2 3.4 6 .6 8'],
    line: ['M5.6 13c5 3.6 14.4 3.8 20.8-1', 'M5 19.6c6 4.4 16 4.4 22-.8', 'M9 26.4c3-6.2 8.6-12.4 15.4-17.6'],
  },
  // цвіт папороті — шість пелюсток і золоте серце
  fern: {
    shape: [0, 60, 120, 180, 240, 300].map((a) => rotPath('M16 16C12.6 11.4 12.6 5.6 16 2.2 19.4 5.6 19.4 11.4 16 16z', a)),
    line: [0, 60, 120, 180, 240, 300].map((a) => rotPath('M16 14V6', a)),
    dot: [[16, 16, 3.6]],
  },
  // гетьманський дукат із булавою
  gold: {
    shape: ['M16 3.5a12.5 12.5 0 1 1 0 25 12.5 12.5 0 1 1 0-25z'],
    line: ['M16 7a9 9 0 1 1 0 18 9 9 0 1 1 0-18z', 'M16 23.5V14.5', 'M13.4 23.5h5.2'],
    dot: [[16, 11.8, 3.6]],
  },
  // зірка-слава (⭐)
  star: { shape: ['M16 2.6l3.9 8.3 9 1.1-6.6 6.2 1.7 9-8-4.4-8 4.4 1.7-9-6.6-6.2 9-1.1z'] },
  // дві схрещені шаблі (⚔)
  sabres: {
    sline: ['M5 4.5c5 7 11 13.4 20 20.5', 'M27 4.5c-5 7-11 13.4-20 20.5', 'M21.2 21.3l3.6-3.6', 'M10.8 21.3l-3.6-3.6',
      'M23.5 23.5l3.6 3.6', 'M8.5 23.5l-3.6 3.6'],
  },
  // гетьманська булава (пернач)
  bulava: {
    shape: ['M16 2.5c4.6 0 7.4 2.8 7.4 6.6S20.6 15.7 16 15.7 8.6 12.9 8.6 9.1 11.4 2.5 16 2.5z', 'M14.6 15h2.8v13.5h-2.8z', 'M12.6 27.5h6.8v2.6h-6.8z'],
    line: ['M16 3.2v12', 'M12.3 4.2c-1.9 3-1.9 7.4 0 10.6', 'M19.7 4.2c1.9 3 1.9 7.4 0 10.6', 'M14 15.6h4'],
  },
  // гість (🏠) — хатка
  guest: { shape: ['M4 15 16 4.5 28 15h-3.4v12.5H7.4V15z'], line: ['M13 27.5v-7h6v7'] },
};

/** Шлях, повернутий на a° довкола (16, 16) — лише для чисел у M/C/V/L з абсолютними координатами. */
function rotPath(d, a) {
  const r = a * Math.PI / 180, c = Math.cos(r), s = Math.sin(r);
  // Переводимо V (вертикаль) у L, щоб поворот був чесним.
  const toks = d.replace(/([MCVLZ])/gi, ' $1 ').trim().split(/[\s,]+/);
  let out = '', cmd = '', buf = [], lastX = 16, lastY = 16;
  const pt = (x, y) => { lastX = x; lastY = y; const dx = x - 16, dy = y - 16; return (16 + dx * c - dy * s).toFixed(2) + ' ' + (16 + dx * s + dy * c).toFixed(2); };
  const flush = () => {
    if (!cmd) return;
    if (cmd === 'Z' || cmd === 'z') { out += 'Z'; return; }
    if (cmd === 'V') { for (const y of buf) out += 'L' + pt(lastX, +y); return; }
    out += cmd;
    const p = [];
    for (let i = 0; i + 1 < buf.length; i += 2) p.push(pt(+buf[i], +buf[i + 1]));
    out += p.join(' ');
  };
  for (const t of toks) {
    if (/^[MCVLZ]$/i.test(t)) { flush(); cmd = t.toUpperCase(); buf = []; } else buf.push(t);
  }
  flush();
  return out;
}

/** Відтінки для силуету на крамниці: полотно — темніше за жетон, щоб не зникало на світлому лиці. */
export const ICON_FILL = { linen: '#eee2c2', fish: '#3d7cc9', herbs: '#4f9a3a', clay: '#c4512f', wool: '#4a413c', fern: '#9a5cf0', gold: '#e2b23a' };
export const ICON_DETAIL = { linen: '#b3262e', fish: '#1f3f69', herbs: '#2c5a20', clay: '#6b2614', wool: '#d9cdb8', fern: '#ffd86b', gold: '#7a5400' };

const NS = 'http://www.w3.org/2000/svg';

/**
 * SVG-значок у (x, y) — центр, розмір s. fill — силует, detail — деталі (типово — ICON_FILL/ICON_DETAIL товару).
 * Повертає <g>. lw — товщина штрихів у одиницях 32-квадрата.
 */
export function svgIcon(parent, key, x, y, s, { fill, detail, lw = 1.9, stroke, sw = 0 } = {}) {
  const ic = ICONS[key];
  const g = document.createElementNS(NS, 'g');
  g.setAttribute('transform', 'translate(' + (x - s / 2) + ',' + (y - s / 2) + ') scale(' + (s / 32) + ')');
  const f = fill || ICON_FILL[key] || '#fff', d = detail || ICON_DETAIL[key] || '#000';
  for (const p of ic.shape || []) {
    const e = document.createElementNS(NS, 'path');
    e.setAttribute('d', p); e.setAttribute('fill', f);
    if (stroke) { e.setAttribute('stroke', stroke); e.setAttribute('stroke-width', sw); e.setAttribute('stroke-linejoin', 'round'); e.setAttribute('paint-order', 'stroke'); }
    g.appendChild(e);
  }
  for (const p of ic.sline || []) {
    const e = document.createElementNS(NS, 'path');
    e.setAttribute('d', p); e.setAttribute('fill', 'none'); e.setAttribute('stroke', f);
    e.setAttribute('stroke-width', lw * 1.25); e.setAttribute('stroke-linecap', 'round'); e.setAttribute('stroke-linejoin', 'round');
    g.appendChild(e);
  }
  for (const p of ic.line || []) {
    const e = document.createElementNS(NS, 'path');
    e.setAttribute('d', p); e.setAttribute('fill', 'none'); e.setAttribute('stroke', d);
    e.setAttribute('stroke-width', lw); e.setAttribute('stroke-linecap', 'round'); e.setAttribute('stroke-linejoin', 'round');
    g.appendChild(e);
  }
  for (const [cx, cy, r] of ic.dot || []) {
    const e = document.createElementNS(NS, 'circle');
    e.setAttribute('cx', cx); e.setAttribute('cy', cy); e.setAttribute('r', r); e.setAttribute('fill', d);
    g.appendChild(e);
  }
  if (parent) parent.appendChild(g);
  return g;
}

/** Той самий значок рядком SVG — для HTML картки (чипи, кнопки, плашки). */
export function iconHtml(key, { size = 16, fill, detail, lw = 1.9, cls = 'sor-ic' } = {}) {
  const ic = ICONS[key];
  if (!ic) return '';
  const f = fill || ICON_FILL[key] || 'currentColor', d = detail || ICON_DETAIL[key] || 'currentColor';
  let s = '<svg class="' + cls + '" viewBox="0 0 32 32" width="' + size + '" height="' + size + '" aria-hidden="true">';
  for (const p of ic.shape || []) s += '<path d="' + p + '" fill="' + f + '"/>';
  for (const p of ic.sline || []) s += '<path d="' + p + '" fill="none" stroke="' + f + '" stroke-width="' + lw * 1.25 + '" stroke-linecap="round" stroke-linejoin="round"/>';
  for (const p of ic.line || []) s += '<path d="' + p + '" fill="none" stroke="' + d + '" stroke-width="' + lw + '" stroke-linecap="round" stroke-linejoin="round"/>';
  for (const [cx, cy, r] of ic.dot || []) s += '<circle cx="' + cx + '" cy="' + cy + '" r="' + r + '" fill="' + d + '"/>';
  return s + '</svg>';
}

/** Canvas: значок у (x, y) — центр, розмір s (для атласу лиць 3D). */
export function canvasIcon(g, key, x, y, s, { fill, detail, lw = 1.9 } = {}) {
  const ic = ICONS[key];
  if (!ic || typeof Path2D === 'undefined') return;
  g.save();
  g.translate(x - s / 2, y - s / 2); g.scale(s / 32, s / 32);
  const f = fill || ICON_FILL[key] || '#fff', d = detail || ICON_DETAIL[key] || '#000';
  g.fillStyle = f;
  for (const p of ic.shape || []) g.fill(new Path2D(p));
  g.lineCap = 'round'; g.lineJoin = 'round';
  g.strokeStyle = f; g.lineWidth = lw * 1.25;
  for (const p of ic.sline || []) g.stroke(new Path2D(p));
  g.strokeStyle = d; g.lineWidth = lw;
  for (const p of ic.line || []) g.stroke(new Path2D(p));
  g.fillStyle = d;
  for (const [cx, cy, r] of ic.dot || []) { g.beginPath(); g.arc(cx, cy, r, 0, Math.PI * 2); g.fill(); }
  g.restore();
}

/** Кольори значка на жетоні товару: світлий силует, деталі — кольору жетона (полотно й дукат — свої, бо світлі). */
export function tokenIconColors(good, tokenColor) {
  if (good === 'linen') return { fill: '#cdb78a', detail: '#b3262e' };
  if (good === 'gold') return { fill: '#fff0b0', detail: '#9a6a00' };
  return { fill: 'rgba(255,250,236,.94)', detail: tokenColor };
}
