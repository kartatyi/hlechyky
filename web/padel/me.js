'use strict';
// «Я»: моя статистика в Падельні (профіль — той самий рендер, що й у «Рейтингу»: P.profile), відзнаки, мої банки
// (картка з маскою й перевіркою Луна, або посилання на банку), «Це був я» — прив'язати гостя падела до себе.
// Сервер — §3.3–3.4 і §2.1 контракту.
(function () {
  const P = window.Padel, esc = P.esc;
  let host = null, banks = null;

  // Ті самі 12, що на сервері (PadelRatingBadges): нездобуті показуємо тьмяно з правилом — сервер шле лише здобуті
  const BADGES = [
    ['first', '🍳', 'Перша пательня', 'Перший результат у Падельні'],
    ['bagel', '🥯', 'Бублик', 'Сет «на суху» — 6:0 (у швидкому 4:0)'],
    ['comeback', '🔄', 'Камбек', 'Виграний сет після 1:5 (у швидкому після 0:3)'],
    ['golden', '✨', 'Золота рука', '3 виграні вирішальні очки за матч'],
    ['tiebreak', '🧊', 'Нерви зі сталі', '3 виграні тайбрейки'],
    ['streak', '🔥', 'На хвилі', '5 перемог поспіль'],
    ['social', '🤝', 'Душа компанії', 'У парі з 10 різними людьми'],
    ['regular', '📅', 'Завсідник', '10 зборів на падел'],
    ['champion', '👑', 'Король турніру', 'Перше місце в турнірі'],
    ['podium', '🏅', 'П\'єдестал', '3 рази в трійці турніру'],
    ['wall', '🧱', 'Залізна стіна', 'Нагорода «Залізна стіна» в турнірі'],
    ['marathon', '🏃', 'Марафонець', '50 результатів'],
  ];
  /// «4 жовт.»; рік — лише коли не цей.
  const day = (iso) => {
    const d = new Date(iso), y = d.getFullYear() !== new Date().getFullYear();
    return new Intl.DateTimeFormat('uk-UA', { timeZone: 'Europe/Kyiv', day: 'numeric', month: 'short', ...(y ? { year: 'numeric' } : {}) }).format(d);
  };
  const link = (p) => '<a class="pf-link" href="#rating/' + encodeURIComponent(p.pid) + '">' + P.who(p) + '</a>';

  // ---------------------------------------------------------------- профіль (спільний з «Рейтингом»)

  P.profile = {
    async render(el, pid, opts) {
      el.innerHTML = '<div class="card empty">Гортаю протоколи матчів…</div>';
      let d;
      try { d = await P.api('profile/' + encodeURIComponent(pid)); }
      catch { el.innerHTML = '<div class="card empty"><span class="e">🤷</span>Такого гравця Глек не знає.</div>'; return; }
      if (!el.isConnected) return;
      el.innerHTML = profileHtml(d, opts || {});
    },
  };

  function profileHtml(d, o) {
    const pl = d.player;
    let h = '<div class="card pf-head">' + P.av(pl.name, 'big') + '<div class="pf-name"><b>' + esc(pl.name) + '</b>'
      + (pl.guest ? '<span class="muted small"> гість</span>' : '')
      + '<div class="muted small">' + (d.rank ? '№' + d.rank + ' у таблиці' : d.played ? 'ще рахується — до рейтингу 3 результати' : 'ще не грав')
      + (d.streak && d.streak.n >= 2 ? ' · ' + (d.streak.kind === 'w' ? '🔥 ' + d.streak.n + ' ' + P.plural(d.streak.n, 'перемога', 'перемоги', 'перемог') + ' поспіль'
        : '🥶 ' + d.streak.n + ' ' + P.plural(d.streak.n, 'поразка', 'поразки', 'поразок') + ' поспіль') : '') + '</div></div>'
      + '<div class="pf-r"><span class="lbl">Ело</span><b class="num">' + d.rating + '</b></div></div>';

    if (!d.played) {
      h += '<div class="card empty"><span class="e">🍳</span>' + (o.own ? 'Ще жодного результату. Перша пательня чекає — зіграй матч на табло чи турнір.'
        : 'Ще жодного результату. Глек тримає місце в таблиці.') + '</div>';
    } else {
      const tile = (v, l, cls) => '<div class="pf-tile"><b class="num' + (cls ? ' ' + cls : '') + '">' + v + '</b><span>' + l + '</span></div>';
      const pn = (n, a, b, c) => P.plural(n, a, b, c);
      h += '<div class="pf-tiles">' + tile(d.played, pn(d.played, 'гра', 'гри', 'ігор')) + tile(d.wins, pn(d.wins, 'перемога', 'перемоги', 'перемог'), 'pos')
        + tile(d.losses, pn(d.losses, 'поразка', 'поразки', 'поразок'), 'neg')
        + (d.draws ? tile(d.draws, pn(d.draws, 'нічия', 'нічиї', 'нічиїх')) : '') + tile(d.winPct + '%', 'перемог')
        + tile(d.pointsFor + ':' + d.pointsAgainst, 'очки') + '</div>';
      const b = d.best || {};
      const best = [];
      if (b.partner) best.push('<div class="card pf-best"><span class="lbl">🤝 Найкращий напарник</span>' + link(b.partner) + '<span class="muted small">' + b.partner.pct + '% · ' + b.partner.wins + ' з ' + b.partner.played + '</span></div>');
      if (b.rival) best.push('<div class="card pf-best"><span class="lbl">⚔️ Частий суперник</span>' + link(b.rival) + '<span class="muted small">' + b.rival.played + ' ' + P.plural(b.rival.played, 'гра', 'гри', 'ігор') + ' · ' + b.rival.wins + ':' + b.rival.losses + '</span></div>');
      if (b.nemesis) best.push('<div class="card pf-best nem"><span class="lbl">😈 Немезида</span>' + link(b.nemesis) + '<span class="muted small">' + b.nemesis.losses + ' ' + P.plural(b.nemesis.losses, 'поразка', 'поразки', 'поразок') + ' з ' + b.nemesis.played + '</span></div>');
      if (best.length) h += '<div class="pf-bests">' + best.join('') + '</div>';
      h += chart(d.ratingHistory);
    }

    const got = new Map((d.badges || []).map((x) => [x.key, x]));
    h += '<h3 class="pf-h3">Відзнаки <span class="muted small">' + got.size + ' з ' + BADGES.length + '</span></h3><div class="pf-badges">'
      + BADGES.map(([k, e, t, rule]) => { const x = got.get(k);
        return '<div class="pf-badge' + (x ? ' on' : '') + '" title="' + esc(rule) + '"><span class="e">' + e + '</span><b>' + esc(t) + '</b><span class="small">'
          + (x ? 'з ' + esc(day(x.at)) : esc(rule)) + '</span></div>'; }).join('') + '</div>';

    if (d.played) {
      if (d.partners.length) {
        h += '<h3 class="pf-h3">Напарники</h3><div class="card pf-tc"><table class="tbl"><thead><tr><th class="l">З ким</th><th>Ігор</th><th>Перемог</th><th>%</th></tr></thead><tbody>'
          + d.partners.map((x) => '<tr><td class="l">' + link(x) + '</td><td>' + x.played + '</td><td>' + x.wins + '</td><td class="' + (x.pct >= 50 ? 'pos' : 'neg') + '">' + x.pct + '%</td></tr>').join('')
          + '</tbody></table></div>';
      }
      if (d.rivals.length) {
        h += '<h3 class="pf-h3">Суперники</h3><div class="card pf-tc"><table class="tbl"><thead><tr><th class="l">Проти кого</th><th>Ігор</th><th>Перемог</th><th>Поразок</th></tr></thead><tbody>'
          + d.rivals.map((x) => '<tr><td class="l">' + link(x) + '</td><td>' + x.played + '</td><td class="pos">' + x.wins + '</td><td class="neg">' + x.losses + '</td></tr>').join('')
          + '</tbody></table></div>';
      }
      if (d.recent.length) {
        h += '<h3 class="pf-h3">Останні матчі</h3><div class="card pf-recent">' + d.recent.map((m) => {
          const team = (t) => t.map((p) => esc(p.name)).join(' + ');
          const mark = m.won == null ? '<span class="pf-res d">=</span>' : m.won ? '<span class="pf-res w">✓</span>' : '<span class="pf-res l">✗</span>';
          return '<div class="pf-m">' + mark + '<div class="pf-mt"><b>' + team(m.teams[0]) + ' <span class="muted">vs</span> ' + team(m.teams[1]) + '</b>'
            + '<span class="muted small">' + esc(day(m.at)) + ' · ' + (m.source === 'tour' ? '🏆 турнір' : '🎾 табло') + '</span></div><b class="num pf-sc">' + esc(m.score) + '</b></div>';
        }).join('') + '</div>';
      }
    }
    return h;
  }

  /// Графік рейтингу: простий SVG-рядок (без бібліотек) — від старту 1200 до сьогодні, по точці на результат.
  function chart(hist) {
    if (!hist || !hist.length) return '';
    const pts = [1200, ...hist.map((x) => x.r)];
    if (pts.length < 3) return '';
    const W = 300, H = 80, min = Math.min(...pts), max = Math.max(...pts), span = Math.max(20, max - min);
    const lo = min - span * 0.1, hi = max + span * 0.1;
    const xy = pts.map((r, i) => [(i / (pts.length - 1)) * W, H - ((r - lo) / (hi - lo)) * H]);
    const line = xy.map(([x, y]) => x.toFixed(1) + ',' + y.toFixed(1)).join(' ');
    const base = H - ((1200 - lo) / (hi - lo)) * H;
    return '<div class="card pf-chart"><div class="pf-ch-h"><span class="lbl">Рейтинг</span><span class="muted small num">мін ' + min + ' · макс ' + max + '</span></div>'
      + '<svg viewBox="0 0 ' + W + ' ' + H + '" preserveAspectRatio="none" aria-label="Графік рейтингу">'
      + (base > 0 && base < H ? '<line x1="0" x2="' + W + '" y1="' + base.toFixed(1) + '" y2="' + base.toFixed(1) + '" class="pf-ch-base"/>' : '')
      + '<polygon points="0,' + H + ' ' + line + ' ' + W + ',' + H + '" class="pf-ch-area"/>'
      + '<polyline points="' + line + '" class="pf-ch-line"/></svg></div>';
  }

  // ---------------------------------------------------------------- мої банки

  const digits = (s) => String(s || '').replace(/\D/g, '');
  function luhn(s) {
    let sum = 0;
    for (let i = 0; i < s.length; i++) { let d = +s[s.length - 1 - i]; if (i % 2) { d *= 2; if (d > 9) d -= 9; } sum += d; }
    return sum % 10 === 0;
  }

  async function loadBanks() {
    try { banks = (await P.api('banks')).banks || []; } catch { banks = banks || []; }
    paintBanks();
  }

  function paintBanks() {
    const el = host && host.querySelector('.me-banks'); if (!el) return;
    if (!banks) { el.innerHTML = '<div class="muted small">…</div>'; return; }
    el.innerHTML = (banks.length ? banks.map((b, i) => P.bank.row(b, { tail: '<span class="pd-grow"></span><button type="button" class="btn xs ghost" data-bedit="' + i + '" title="Змінити">✎</button>'
      + '<button type="button" class="btn xs ghost bad" data-bdel="' + i + '" title="Прибрати">✕</button>' })).join('')
      : '<div class="muted small">Ще нема. Додай картку чи банку — їх побачать лише ті, хто тобі винен.</div>')
      + (banks.length < 6 ? '<button type="button" class="btn sm" data-badd style="align-self:flex-start">+ Додати банк</button>' : '');
  }

  async function saveBanks(list) {
    const r = await P.api('banks', { banks: list }, 'PUT');
    banks = r.banks || list;
    paintBanks();
  }

  function bankForm(i) {
    const b = i != null ? banks[i] : { bank: 'mono', title: '', card: '', link: '' };
    const s = P.sheet(i != null ? 'Змінити банк' : 'Новий банк', '<form class="stack me-bform">'
      + '<div class="field"><label>Банк</label><div class="chips me-bk">' + P.bank.list.map(([k, n]) => '<button type="button" class="chip' + (k === b.bank ? ' on' : '') + '" data-k="' + k + '"><span class="mn-bk mn-' + k + '">' + esc(n) + '</span></button>').join('') + '</div></div>'
      + '<div class="field"><label>Назва (для себе й друзів)</label><input class="inp" name="title" maxlength="40" placeholder="Чорна моно" value="' + esc(b.title || '') + '"></div>'
      + '<div class="field"><label>Номер картки</label><input class="inp num me-cardin" name="card" inputmode="numeric" autocomplete="off" placeholder="0000 0000 0000 0000" maxlength="19" value="' + esc(P.bank.card(b.card)) + '">'
      + '<span class="small me-luhn"></span></div>'
      + '<div class="field"><label>або посилання на банку</label><input class="inp" name="link" type="url" maxlength="300" placeholder="https://send.monobank.ua/jar/…" value="' + esc(b.link || '') + '"></div>'
      + '<div class="muted small">Досить чогось одного. Бачать лише ті, хто тобі винен, — ніде публічно.</div>'
      + '<div class="row" style="justify-content:flex-end"><button class="btn pri" type="submit">Зберегти</button></div></form>');
    const f = s.el.querySelector('form');
    let kind = b.bank;
    s.el.querySelector('.me-bk').addEventListener('click', (e) => {
      const c = e.target.closest('[data-k]'); if (!c) return;
      kind = c.dataset.k;
      for (const x of c.parentElement.children) x.classList.toggle('on', x === c);
    });
    const say = s.el.querySelector('.me-luhn');
    const check = () => {
      const d = digits(f.card.value);
      say.className = 'small me-luhn';
      if (!d) say.textContent = '';
      else if (d.length < 16) say.textContent = 'ще ' + (16 - d.length) + ' ' + P.plural(16 - d.length, 'цифра', 'цифри', 'цифр');
      else if (luhn(d)) { say.textContent = '✓ номер сходиться'; say.classList.add('pos'); }
      else { say.textContent = '✗ номер не сходиться — перевір цифри'; say.classList.add('neg'); }
    };
    // Маска 4×4: лише цифри, пробіли самі; курсор лишається після тієї самої цифри
    f.card.addEventListener('input', () => {
      const el = f.card, pos = el.selectionStart || 0;
      const before = digits(el.value.slice(0, pos)).length;
      const d = digits(el.value).slice(0, 16);
      el.value = P.bank.card(d);
      let p = 0, n = 0;
      while (p < el.value.length && n < before) { if (/\d/.test(el.value[p])) n++; p++; }
      try { el.setSelectionRange(p, p); } catch { /* type=text — можна */ }
      check();
    });
    check();
    f.addEventListener('submit', async (e) => {
      e.preventDefault();
      const card = digits(f.card.value), lnk = f.link.value.trim();
      if (!card && !lnk) { P.toast('Додай картку або посилання', 'bad'); return; }
      if (card && (card.length !== 16 || !luhn(card))) { P.toast('Номер картки не той — перевір 16 цифр', 'bad'); f.card.focus(); return; }
      if (lnk && !/^https:\/\/[^\s/]+\.[^\s]+/.test(lnk)) { P.toast('Посилання — https-адреса', 'bad'); f.link.focus(); return; }
      const item = { id: b.id || null, bank: kind, title: f.title.value.trim(), card: card || null, link: lnk || null };
      const list = banks.slice();
      if (i != null) list[i] = item; else list.push(item);
      const btn = f.querySelector('[type=submit]'); btn.disabled = true;
      try { await saveBanks(list); P.toast('Банк збережено'); s.close(); } catch { btn.disabled = false; }
    });
  }

  // ---------------------------------------------------------------- «Це був я»

  async function paintGuests(force) {
    const el = host && host.querySelector('.me-guests'); if (!el) return;
    const d = await P.players.load(force);
    const list = (d.players || []).filter((p) => p.guest && !p.linkedTo);
    const card = el.closest('.me-gcard');
    card.hidden = !list.length;
    el.innerHTML = list.map((p) => '<div class="me-g">' + P.who(p, { noGuest: true }) + '<span class="muted small">'
      + (p.played ? p.played + ' ' + P.plural(p.played, 'гра', 'гри', 'ігор') : 'ще не грав') + (p.last ? ' · ' + esc(day(p.last)) : '') + '</span>'
      + '<span class="pd-grow"></span><button type="button" class="btn xs" data-link="' + esc(p.pid) + '">Це я</button></div>').join('');
  }

  function linkGuest(pid) {
    const d = P.players.known() || {};
    const g = (d.players || []).find((x) => x.pid === pid); if (!g) return;
    const s = P.sheet('Це був ти?', '<div class="stack"><div class="glek-say"><span class="glek-av">🏺</span><div>Прив\'язати гостя <b>«' + esc(g.name) + '»</b> до тебе, <b>'
      + esc(P.me.nick) + '</b>? Усі його матчі, рейтинг, борги й відзнаки стануть твоїми.<div class="muted small">Назад відв\'язати може лише адмін — тож без жартів.</div></div></div>'
      + '<div class="row" style="justify-content:flex-end"><button class="btn ghost" data-x>Ні, не я</button><button class="btn pri" data-ok>Так, це я</button></div></div>');
    s.el.querySelector('[data-x]').onclick = () => s.close();
    s.el.querySelector('[data-ok]').onclick = async (e) => {
      e.target.disabled = true;
      try {
        await P.api('guests/link', { guest: pid });
        P.toast('🔗 Тепер «' + g.name + '» — це ти');
        s.close(); P.players.forget(); paintGuests(true); renderProfile();
      } catch { e.target.disabled = false; }
    };
  }

  // ---------------------------------------------------------------- вкладка

  function renderProfile() {
    const el = host && host.querySelector('.me-prof');
    if (el && P.me.pid) P.profile.render(el, P.me.pid, { own: true });
  }

  Padel.tab({
    id: 'me', icon: '🙂', title: 'Я', order: 6,
    mount(el) {
      host = el;
      if (!P.me.account) {
        el.innerHTML = '<h2>🙂 Я</h2><div class="card empty"><span class="e">🙂</span>Увійди на головній, щоб бачити свою статистику, відзнаки й банки, '
          + 'записуватись на збори й вести рахунок.<div style="margin-top:12px"><a class="btn pri" href="/">Увійти на головній</a></div></div>';
        return;
      }
      el.innerHTML = '<div class="me-prof"></div>'
        + '<h3 class="pf-h3">💳 Мої банки</h3><div class="card me-bcard"><div class="me-banks stack"></div></div>'
        + '<div class="me-gcard" hidden><h3 class="pf-h3">🙋 Це був я</h3><div class="card"><div class="muted small" style="margin-bottom:8px">Грав гостем, поки не мав акаунта? '
        + 'Прив\'яжи — і вся історія переїде до тебе.</div><div class="me-guests stack"></div></div></div>'
        + '<div class="muted small me-foot">Ти — <b>' + esc(P.me.nick) + '</b>. Вийти чи змінити акаунт — <a href="/">на головній</a>.</div>';
      el.addEventListener('click', (e) => {
        const t = e.target;
        if (t.closest('[data-copy]')) { P.bank.copy(t.closest('[data-copy]').dataset.copy, 'Номер картки скопійовано'); return; }
        if (t.closest('[data-badd]')) { bankForm(null); return; }
        const be = t.closest('[data-bedit]'); if (be) { bankForm(+be.dataset.bedit); return; }
        const bd = t.closest('[data-bdel]');
        if (bd) {
          const b = banks[+bd.dataset.bdel];
          if (b && confirm('Прибрати «' + (b.title || P.bank.name(b.bank)) + '»?')) saveBanks(banks.filter((x) => x !== b)).then(() => P.toast('Прибрав')).catch(() => {});
          return;
        }
        const lk = t.closest('[data-link]'); if (lk) linkGuest(lk.dataset.link);
      });
      P.on('rating', () => { if (!host.hidden) renderProfile(); });
    },
    show() {
      if (!P.me.account) return;
      renderProfile(); loadBanks(); paintGuests(true);
    },
  });
})();
