'use strict';
// Рейтинг: таблиця Ело зі званнями, «ще рахуються» (менше трьох результатів), хімія пар. Тиць по людині —
// її профіль (#rating/<pid>), той самий рендер, що й «Я» (P.profile з me.js). Сервер — §3.4 контракту.
(function () {
  const P = window.Padel, esc = P.esc;
  let host = null, rows = null, chem = null, arg = '', loadT = 0;

  async function load() {
    try {
      const [r, c] = await Promise.all([P.api('rating'), P.api('chemistry')]);
      rows = r; chem = c;
    } catch { rows = rows || { rows: [], provisional: [] }; chem = chem || { pairs: [] }; }
    render();
  }
  const soon = () => { clearTimeout(loadT); loadT = setTimeout(() => { if (host && !host.hidden) load(); else rows = null; }, 400); };

  const delta = (d) => !d ? '<span class="muted">—</span>' : d > 0 ? '<span class="pos">▲' + d + '</span>' : '<span class="neg">▼' + -d + '</span>';

  function render() {
    if (!host) return;
    const box = host.querySelector('.rt-body');
    if (arg) {
      box.innerHTML = '<button class="btn sm ghost rt-back" data-back>← Уся таблиця</button><div class="rt-prof"></div>';
      if (P.profile) P.profile.render(box.querySelector('.rt-prof'), arg);
      return;
    }
    if (!rows) { box.innerHTML = '<div class="card empty">Рахую Ело…</div>'; return; }
    let h = '';
    if (!rows.rows.length && !rows.provisional.length) {
      h += '<div class="card empty"><span class="e">📈</span>Ще ніхто не зіграв жодного матчу.<br>Глек гріє сковорідку — перший матч на табло, і тут з\'явиться таблиця.'
        + '<div style="margin-top:10px"><button class="btn pri" data-go-board>🎾 На табло</button></div></div>';
    }
    if (rows.rows.length) {
      h += '<div class="card rt-card"><table class="tbl rt-tbl"><thead><tr><th>#</th><th class="l">Гравець</th><th>Рейтинг</th><th title="Зміна за 7 днів">7 дн</th>'
        + '<th>Ігор</th><th class="rt-w">Перемог</th></tr></thead><tbody>'
        + rows.rows.map((r, i) => '<tr data-pid="' + esc(r.pid) + '"' + (r.pid === P.me.pid ? ' class="me"' : '') + '><td class="muted">' + (i + 1) + '</td>'
          + '<td class="l"><div class="rt-who">' + P.who(r) + (r.title ? '<span class="rt-title">' + esc(r.title) + '</span>' : '') + '</div></td>'
          + '<td><b>' + r.rating + '</b></td><td>' + delta(r.delta7) + '</td><td>' + r.played + '</td><td class="rt-w">' + r.wins + '</td></tr>').join('')
        + '</tbody></table></div>';
    }
    if (rows.provisional.length) {
      h += '<h3 class="rt-h3">Ще рахуються <span class="muted small">— до рейтингу треба 3 результати</span></h3><div class="card rt-prov">'
        + rows.provisional.map((r) => '<button type="button" class="rt-pv" data-pid="' + esc(r.pid) + '">' + P.who(r)
          + '<span class="muted small">' + r.played + ' з 3 · ' + r.rating + '</span></button>').join('') + '</div>';
    }
    h += '<h3 class="rt-h3">🤝 Хімія пар</h3>';
    const pairs = (chem && chem.pairs) || [];
    h += pairs.length ? '<div class="rt-chem">' + pairs.map((p, i) => '<div class="card rt-pair' + (i === 0 ? ' top' : '') + '">'
      + '<div class="rt-pair-h"><span class="rt-avs">' + P.av(p.names[0]) + P.av(p.names[1]) + '</span><b>' + esc(p.names[0]) + ' + ' + esc(p.names[1]) + '</b>'
      + '<b class="rt-pct ' + (p.pct >= 50 ? 'pos' : 'neg') + '">' + p.pct + '%</b></div>'
      + '<div class="muted small">' + p.wins + ' ' + P.plural(p.wins, 'перемога', 'перемоги', 'перемог') + ' з ' + p.played + (i === 0 && p.pct >= 50 ? ' · найкраща пара' : '') + '</div>'
      + '<div class="rt-bar"><i style="width:' + p.pct + '%"></i></div></div>').join('') + '</div>'
      : '<div class="card empty small">Пари з’являться, коли хтось зіграє разом хоча б тричі. Сковорідка любить постійність.</div>';
    box.innerHTML = h;
  }

  Padel.tab({
    id: 'rating', icon: '📈', title: 'Рейтинг', order: 5,
    mount(el) {
      host = el;
      el.innerHTML = '<h2>📈 Рейтинг</h2><div class="rt-body"></div>';
      el.addEventListener('click', (e) => {
        if (e.target.closest('[data-back]')) { P.go('rating'); return; }
        if (e.target.closest('[data-go-board]')) { P.go('board'); return; }
        const r = e.target.closest('[data-pid]');
        if (r && !arg) P.go('rating', r.dataset.pid);
      });
      P.on('rating', soon);
    },
    show(el, a) {
      arg = a || '';
      el.querySelector('h2').hidden = !!arg;
      if (arg) render(); else if (rows) { render(); load(); } else load();
      window.scrollTo(0, 0);
    },
  });
})();
