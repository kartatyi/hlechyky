/*
  Публічний Альбом Піктіонарі (прохід №3, п. 26): гортати може будь-хто, навіть гість без ніка.
  GET /api/games/pictionary/album?before=<id> — сторінка з 24 малюнків, новіші першими: { items, more, total, admin };
  малюнок — штрихами (z, Impl/SketchWire.cs), малюємо тим самим кодом, що в грі (window.PCART з pictionary.js).
  Адмін бачить 🗑: POST /api/games/pictionary/album/delete { id }.
*/
(() => {
  const art = window.PCART;
  const grid = document.querySelector('.pa-grid');
  const more = document.querySelector('.pa-more button');
  const empty = document.querySelector('.pa-empty');
  const big = document.querySelector('.pa-big');
  const byId = new Map();
  let last = 0, admin = false, busy = false;

  const esc = (t) => String(t == null ? '' : t).replace(/[&<>"']/g, (ch) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]);
  const date = (iso) => { try { return new Date(iso).toLocaleDateString('uk-UA', { day: 'numeric', month: 'short', year: 'numeric' }); } catch { return ''; } };

  /// Малюнок у полотно будь-якого розміру: спершу в 1000 × 750 (заливка рахує пікселі там), потім — у потрібне.
  const paper = document.createElement('canvas');
  paper.width = art.W; paper.height = art.H;
  const pg = paper.getContext('2d', { willReadFrequently: true });
  function render(canvas, z) {
    pg.globalCompositeOperation = 'source-over';
    pg.fillStyle = '#ffffff';
    pg.fillRect(0, 0, art.W, art.H);
    for (const op of art.unpack(z)) art.drawOp(pg, op);
    const g = canvas.getContext('2d');
    g.imageSmoothingEnabled = true;
    g.drawImage(paper, 0, 0, canvas.width, canvas.height);
  }

  // мініатюри малюємо, лише коли картка на екрані: сторінка з 24 заливками — не дрібниця для телефона
  const seen = new IntersectionObserver((entries) => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      seen.unobserve(e.target);
      const a = byId.get(+e.target.dataset.id);
      if (a) render(e.target, a.z);
    }
  }, { rootMargin: '200px' });

  function card(a) {
    const el = document.createElement('figure');
    el.className = 'pa-card';
    el.innerHTML = '<canvas width="480" height="360" data-id="' + a.id + '"></canvas>'
      + '<figcaption><b>' + (a.home ? '🏠 ' : '') + esc(a.word) + '</b>'
      + '<span class="pa-meta">🎨 ' + esc(a.author) + (a.hearts ? ' · ❤ ' + a.hearts : '') + '</span>'
      + '<span class="pa-meta pa-dim">' + date(a.at) + (a.by && a.by !== a.author ? ' · 📌 ' + esc(a.by) : '') + '</span></figcaption>'
      + (admin ? '<button type="button" class="pa-del" title="Прибрати з альбому">🗑</button>' : '');
    el.querySelector('canvas').addEventListener('click', () => open(a));
    const del = el.querySelector('.pa-del');
    if (del) del.addEventListener('click', () => remove(a, el));
    seen.observe(el.querySelector('canvas'));
    return el;
  }

  function open(a) {
    render(big.querySelector('canvas'), a.z);
    big.querySelector('.pa-bigcap').innerHTML = '<b>' + (a.home ? '🏠 ' : '') + esc(a.word) + '</b> — ' + esc(a.author)
      + (a.hearts ? ' · ❤ ' + a.hearts : '') + ' · ' + date(a.at);
    if (big.showModal) big.showModal(); else big.setAttribute('open', '');
  }

  function remove(a, el) {
    if (!confirm('Прибрати «' + a.word + '» з альбому назавжди?')) return;
    fetch('/api/games/pictionary/album/delete', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: a.id }),
    }).then((r) => r.json()).then((d) => {
      if (d && d.ok) { el.remove(); byId.delete(a.id); } else alert((d && d.message) || 'Не вийшло');
    }).catch(() => alert('Не вийшло'));
  }

  function page() {
    if (busy) return;
    busy = true;
    more.disabled = true;
    fetch('/api/games/pictionary/album' + (last ? '?before=' + last : ''))
      .then((r) => r.json())
      .then((d) => {
        admin = !!d.admin;
        for (const a of d.items || []) {
          byId.set(a.id, a);
          grid.appendChild(card(a));
          last = a.id;
        }
        empty.hidden = byId.size > 0;
        more.hidden = !d.more;
      })
      .catch(() => { empty.hidden = false; empty.textContent = 'Альбом не відкрився — спробуй оновити сторінку.'; })
      .finally(() => { busy = false; more.disabled = false; });
  }

  big.addEventListener('click', (e) => { if (e.target === big) big.close(); });
  more.addEventListener('click', page);
  page();
})();
