/*
  Графіки «Хто скільки» — window.HChart. Без бібліотек: SVG, що перемальовується під ширину блока.

  HChart.line(el, opts) — лінії в часі (біржа черепків, Ело, каса Глека):
    series: [{ name, h, color?, pts: [[x, y], …], me?, dash? }]
      x — мітка часу в мс (або просто число, тоді xFmt свій), точки — за зростанням x;
      h — відтінок як у ніка (HPeople.hue), color — будь-який CSS-колір замість нього (напр. Глек — var(--clay));
      me — «це ти»: товща лінія, у підказці жирним.
    step   — сходинками (баланс тримається до наступної зміни), інакше — ламана;
    zero   — вісь Y завжди від нуля;
    yFmt(v), xFmt(x) — підписи (за замовчуванням: число по-українськи й дата «9 жовт.» / «14:00»);
    height — висота в px (220), legend — легенда під графіком (true), max — найбільше ліній на легенді (12).
  Наведення чи дотик — вертикальна риска й підказка зі значеннями ліній у цю мить; клік по імені в легенді
  виділяє лінію (решта бліднуть), ще клік — знімає.
  Повертає { redraw(), destroy() }.
*/
(() => {
  'use strict';

  const NS = 'http://www.w3.org/2000/svg';
  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const num = (n) => Math.round(n || 0).toLocaleString('uk-UA');
  const colorOf = (s) => s.color || 'hsl(' + (s.h == null ? 45 : s.h) + ' 72% 64%)';

  /// «Гарний» крок сітки: 1, 2, 2.5, 5 × 10ⁿ — щоб підписи осі були круглими.
  function niceStep(span, ticks) {
    const raw = span / Math.max(1, ticks);
    const p = Math.pow(10, Math.floor(Math.log10(raw || 1)));
    const m = raw / p;
    return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10) * p;
  }

  function defXFmt(span) {
    return span < 2 * 864e5
      ? (x) => new Date(x).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' })
      : (x) => new Date(x).toLocaleDateString('uk-UA', { day: 'numeric', month: 'short' });
  }

  /// Значення лінії в мить x: сходинки — останнє відоме, ламана — між сусідніми точками. До першої точки — нема.
  function valueAt(s, x, step) {
    const p = s.pts;
    if (!p.length || x < p[0][0]) return null;
    let i = 0;
    while (i + 1 < p.length && p[i + 1][0] <= x) i++;
    if (step || i + 1 >= p.length) return p[i][1];
    const [x0, y0] = p[i], [x1, y1] = p[i + 1];
    return x1 === x0 ? y1 : y0 + (y1 - y0) * (x - x0) / (x1 - x0);
  }

  function line(el, opts) {
    const o = Object.assign({ height: 220, legend: true, max: 12, step: false, zero: false, yFmt: num }, opts || {});
    const series = (o.series || []).filter((s) => s && s.pts && s.pts.length);
    let focus = null;
    el.classList.add('hch');
    if (!series.length) {
      el.innerHTML = '<div class="gempty">Поки нема з чого малювати.</div>';
      return { redraw() {}, destroy() {} };
    }
    const xs = series.flatMap((s) => s.pts.map((p) => p[0]));
    const ys = series.flatMap((s) => s.pts.map((p) => p[1]));
    const x0 = Math.min(...xs), x1 = Math.max(...xs);
    let y0 = Math.min(...ys), y1 = Math.max(...ys);
    if (o.zero) { y0 = Math.min(0, y0); y1 = Math.max(0, y1); }
    if (y1 === y0) { y1 += 1; y0 -= y0 > 0 ? 1 : 0; }
    const yStep = niceStep(y1 - y0, 4);
    y0 = Math.floor(y0 / yStep) * yStep;
    y1 = Math.ceil(y1 / yStep) * yStep;
    const xFmt = o.xFmt || defXFmt(x1 - x0);

    el.innerHTML = '<div class="hch-plot"><svg class="hch-svg" xmlns="' + NS + '" role="img"'
      + (o.label ? ' aria-label="' + esc(o.label) + '"' : '') + '></svg><div class="hch-tip" hidden></div></div>'
      + (o.legend ? '<div class="hch-leg"></div>' : '');
    const plot = el.querySelector('.hch-plot');
    const svg = el.querySelector('svg');
    const tip = el.querySelector('.hch-tip');
    const leg = el.querySelector('.hch-leg');
    let geo = null;

    function draw() {
      const W = Math.max(240, plot.clientWidth || 600), H = o.height;
      const yLabels = [];
      for (let v = y0; v <= y1 + yStep / 2; v += yStep) yLabels.push(v);
      const padL = Math.min(72, 12 + 7 * Math.max(...yLabels.map((v) => String(o.yFmt(v)).length))), padR = 10, padT = 10, padB = 24;
      const X = (x) => padL + (x1 === x0 ? (W - padL - padR) / 2 : (x - x0) / (x1 - x0) * (W - padL - padR));
      const Y = (y) => padT + (1 - (y - y0) / (y1 - y0)) * (H - padT - padB);
      geo = { W, H, padL, padR, X, Y };
      svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);
      svg.setAttribute('width', W);
      svg.setAttribute('height', H);
      let h = '';
      yLabels.forEach((v) => {
        const y = Y(v).toFixed(1);
        h += '<line class="hch-grid' + (v === 0 ? ' zero' : '') + '" x1="' + padL + '" x2="' + (W - padR) + '" y1="' + y + '" y2="' + y + '"/>'
          + '<text class="hch-y" x="' + (padL - 6) + '" y="' + y + '" dy="0.32em" text-anchor="end">' + esc(o.yFmt(v)) + '</text>';
      });
      const nx = Math.max(2, Math.min(6, Math.floor((W - padL - padR) / 90)));
      for (let i = 0; i <= nx; i++) {
        const x = x0 + (x1 - x0) * i / nx;
        h += '<text class="hch-x" x="' + X(x).toFixed(1) + '" y="' + (H - 6) + '" text-anchor="' + (i === 0 ? 'start' : i === nx ? 'end' : 'middle') + '">' + esc(xFmt(x)) + '</text>';
        if (x1 === x0) break;
      }
      // спершу бліді, потім виділена й «моя» — щоб їх не перекривали
      const order = series.map((s, i) => i).sort((a, b) => rank(series[a]) - rank(series[b]));
      order.forEach((i) => {
        const s = series[i];
        let d = '';
        s.pts.forEach((p, j) => {
          const px = X(p[0]).toFixed(1), py = Y(p[1]).toFixed(1);
          if (!j) d += 'M' + px + ' ' + py;
          else if (o.step) d += 'H' + px + 'V' + py;
          else d += 'L' + px + ' ' + py;
        });
        if (o.step) d += 'H' + X(x1).toFixed(1);
        const dim = focus != null && focus !== i;
        h += '<path class="hch-line' + (s.me ? ' me' : '') + (focus === i ? ' focus' : '') + (dim ? ' dim' : '') + '" d="' + d + '"'
          + ' style="stroke:' + colorOf(s) + '"' + (s.dash ? ' stroke-dasharray="5 4"' : '') + '/>';
        if (s.pts.length === 1) h += '<circle class="hch-dot" cx="' + X(s.pts[0][0]).toFixed(1) + '" cy="' + Y(s.pts[0][1]).toFixed(1) + '" r="3" style="fill:' + colorOf(s) + '"/>';
      });
      h += '<line class="hch-cur" x1="0" x2="0" y1="' + padT + '" y2="' + (H - padB) + '" visibility="hidden"/>';
      svg.innerHTML = h;
    }
    const rank = (s) => (focus != null && series[focus] === s ? 3 : s.me ? 2 : 1);

    function legend() {
      if (!leg) return;
      const shown = series.slice(0, o.max);
      leg.innerHTML = shown.map((s, i) => '<button type="button" class="hch-chip' + (focus === i ? ' on' : '') + (s.me ? ' me' : '') + '" data-i="' + i + '">'
        + '<i style="background:' + colorOf(s) + '"></i>' + esc(s.name) + '</button>').join('')
        + (series.length > shown.length ? '<span class="muted small">і ще ' + (series.length - shown.length) + '</span>' : '');
      leg.querySelectorAll('[data-i]').forEach((b) => b.onclick = () => {
        const i = +b.dataset.i;
        focus = focus === i ? null : i;
        draw();
        legend();
      });
    }

    function move(ev) {
      if (!geo) return;
      const r = svg.getBoundingClientRect();
      const px = (ev.clientX - r.left) * (geo.W / r.width);
      if (px < geo.padL || px > geo.W - geo.padR) return hide();
      const x = x0 + (px - geo.padL) / (geo.W - geo.padL - geo.padR) * (x1 - x0);
      const cur = svg.querySelector('.hch-cur');
      cur.setAttribute('x1', px);
      cur.setAttribute('x2', px);
      cur.setAttribute('visibility', 'visible');
      const vals = series.map((s, i) => ({ s, i, v: valueAt(s, x, o.step) })).filter((a) => a.v != null)
        .sort((a, b) => (focus === b.i) - (focus === a.i) || b.v - a.v).slice(0, 8);
      tip.innerHTML = '<div class="hch-tip-x">' + esc(xFmt(x)) + '</div>' + vals.map((a) => '<div class="hch-tip-r' + (a.s.me ? ' me' : '') + '">'
        + '<i style="background:' + colorOf(a.s) + '"></i><span>' + esc(a.s.name) + '</span><b>' + esc(o.yFmt(a.v)) + '</b></div>').join('');
      tip.hidden = false;
      const left = px / geo.W * r.width;
      tip.style.left = Math.min(Math.max(0, left + 12), r.width - tip.offsetWidth) + 'px';
      if (left + 12 + tip.offsetWidth > r.width) tip.style.left = Math.max(0, left - 12 - tip.offsetWidth) + 'px';
    }
    function hide() {
      tip.hidden = true;
      const cur = svg.querySelector('.hch-cur');
      if (cur) cur.setAttribute('visibility', 'hidden');
    }
    svg.addEventListener('pointermove', move);
    svg.addEventListener('pointerdown', move);
    svg.addEventListener('pointerleave', hide);

    draw();
    legend();
    let ro = null;
    if (window.ResizeObserver) {
      let last = plot.clientWidth;
      ro = new ResizeObserver(() => { if (plot.clientWidth !== last) { last = plot.clientWidth; draw(); } });
      ro.observe(plot);
    }
    return { redraw: draw, destroy() { if (ro) ro.disconnect(); } };
  }

  window.HChart = { line, valueAt };
})();
