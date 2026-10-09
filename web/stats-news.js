/* «📊 Хто скільки» — stats-news.js: угорі «✨ Огляду» — «📰 Глечицький вісник» (газета дня з архівом ◀ ▶),
   «📅 Цього дня місяць тому» і «🎯 Мої цілі»; «🔔 нове» — крапка на 📊 і позначка на вкладці, коли там справді є що глянути.
   Рахує сервер (LitopysNews.cs), тут лише малюємо. Контракт — D:/or-wt/_tools/stats2-contract.md, пакет news. */
(() => {
  const P = window.HPeople;
  if (!P || !P.kit) return;
  const k = P.kit;
  const esc = (s) => k.esc(s);

  const MONTHS = ['січня', 'лютого', 'березня', 'квітня', 'травня', 'червня', 'липня', 'серпня', 'вересня', 'жовтня', 'листопада', 'грудня'];
  const WEEKDAYS = ['неділя', 'понеділок', 'вівторок', 'середа', 'четвер', 'пʼятниця', 'субота'];
  /// «четвер, 9 жовтня» — день київський, рядок «2026-10-09»: рахуємо без часового поясу браузера.
  const dayWord = (day, withDow) => {
    const [y, m, d] = day.split('-').map(Number);
    const dow = new Date(Date.UTC(y, m - 1, d)).getUTCDay();
    return (withDow ? WEEKDAYS[dow] + ', ' : '') + d + ' ' + MONTHS[m - 1];
  };

  /// Частини заголовка: рядок — текст, { n } — нік (кольором і з карткою, як усюди).
  const parts = (list) => (list || []).map((x) => (typeof x === 'string' ? esc(x) : k.nickLink(x.n))).join('');
  const go = (href) => (href ? '<button type="button" class="sn-go" data-go="' + esc(href) + '" aria-label="Глянути" title="Глянути">→</button>' : '');
  const myNick = () => ((k.me() || {}).nick || '').trim();
  const wireGo = (root) => root.querySelectorAll('[data-go]').forEach((b) => b.onclick = () => k.go(b.dataset.go));

  // ---------- маленький кеш: Огляд перемальовується на кожен перемикач періоду, а газета від періоду не залежить ----------
  const cache = new Map();
  async function get(url, ttlMs) {
    const c = cache.get(url);
    if (c && Date.now() - c.at < ttlMs) return c.data;
    const data = await k.api('GET', url);
    cache.set(url, { at: Date.now(), data });
    return data;
  }

  // =============================================================================================
  // 🔔 Нове в статистиці
  // =============================================================================================

  /// Сервер віддає сигнатури по вкладках ({ overview, glek, … }); те, що людина вже бачила, — у localStorage окремо
  /// на кожен нік. Різниться — крапка на 📊 і «нове» на вкладці. Відкрила вкладку — бачила.
  const Pulse = (() => {
    const POLL_MS = 5 * 60e3;
    const BADGE = ' <span class="stnew">нове</span>';
    let fresh = null;       // останні сигнатури з сервера
    let lastPoll = 0;
    let badged = new Set();
    const lsKey = () => 'statsPulse:' + (myNick().toLowerCase() || '-');
    const load = () => { try { return JSON.parse(localStorage.getItem(lsKey()) || 'null'); } catch (e) { return null; } };
    const save = (m) => { try { localStorage.setItem(lsKey(), JSON.stringify(m)); } catch (e) { /* приватне вікно — без пам'яті */ } };

    function paint() {
      const seen = load() || {};
      const unseen = new Set(fresh ? Object.keys(fresh).filter((p) => seen[p] !== fresh[p]) : []);
      document.querySelectorAll('#mainNav button[data-route="stats"], .mtabs button[data-route="stats"]')
        .forEach((b) => b.classList.toggle('sn-dot', unseen.size > 0));
      unseen.forEach((p) => { if (!badged.has(p)) P.statsBadge(p, BADGE); });
      badged.forEach((p) => { if (!unseen.has(p)) P.statsBadge(p, null); });
      badged = unseen;
    }

    function sawTab(tab) {
      if (!fresh || !(tab in fresh)) return;
      const seen = load() || {};
      if (seen[tab] === fresh[tab]) return;
      seen[tab] = fresh[tab];
      save(seen);
      paint();
    }

    /// Відкрито «Хто скільки» на вкладці, де було нове, — вже бачила.
    function sawShown() {
      const tab = P.statsShown && P.statsShown();
      if (tab) sawTab(tab);
    }

    async function poll() {
      if (document.hidden) return;
      lastPoll = Date.now();
      const nick = myNick();
      let d;
      try { d = await k.api('GET', '/api/stats/pulse' + (nick ? '?nick=' + encodeURIComponent(nick) : '')); } catch (e) { return; }
      fresh = (d && d.parts) || null;
      if (!fresh) return;
      // новенькому без історії — тихо: усе, що є зараз, вважаємо баченим
      if (!load()) save(Object.assign({}, fresh));
      sawShown();
      paint();
    }

    setTimeout(poll, 4000);
    setInterval(() => { if (!document.hidden) poll(); }, POLL_MS);
    document.addEventListener('visibilitychange', () => { if (!document.hidden && Date.now() - lastPoll > 60e3) poll(); });
    window.addEventListener('hashchange', () => setTimeout(sawShown, 0));
    return { sawTab, poll };
  })();

  // =============================================================================================
  // 📰 Газета
  // =============================================================================================

  let viewDay = null;   // null — свіжий випуск; інакше — день з архіву

  function headHtml(h, lead) {
    if (lead) {
      return '<article class="sn-lead"><div class="sn-lead-h"><span class="sn-ico" aria-hidden="true">' + esc(h.icon) + '</span>'
        + '<h4>' + parts(h.parts) + '</h4>' + go(h.href) + '</div>'
        + (h.sub ? '<p class="sn-sub">' + parts(h.sub) + '</p>' : '') + '</article>';
    }
    return '<li><span class="sn-ico" aria-hidden="true">' + esc(h.icon) + '</span><div class="sn-txt"><div class="sn-h">' + parts(h.parts) + '</div>'
      + (h.sub ? '<div class="sn-sub">' + parts(h.sub) + '</div>' : '') + '</div>' + go(h.href) + '</li>';
  }

  function paperHtml(d) {
    const items = d.items || [];
    const body = d.lead
      ? headHtml(d.lead, true) + (items.length ? '<ul class="sn-items">' + items.map((h) => headHtml(h, false)).join('') + '</ul>' : '')
      : '<div class="gempty glek">Випуск порожній: у Глечиках був вихідний — навіть Глек дрімав.</div>';
    const nav = (day, label, arrow) => '<button type="button" class="sn-nav" data-day="' + esc(day || '') + '"' + (day ? '' : ' disabled')
      + ' aria-label="' + label + '" title="' + label + '">' + arrow + '</button>';
    return '<header class="sn-mast">' + nav(d.prev, 'Попередній випуск', '◀')
      + '<div class="sn-title"><div class="sn-name">📰 Глечицький вісник</div>'
      + '<div class="sn-meta">№ ' + d.no + ' · ' + esc(dayWord(d.day, true)) + (d.today ? ' · <b>сьогодні</b>' : '') + ' · ціна — 1 черепок</div></div>'
      + nav(d.next, 'Наступний випуск', '▶') + '</header>'
      + '<div class="sn-weather"><span class="muted">Погода в Глечиках:</span> ' + esc(d.weather || '') + '</div>'
      + (d.fallback ? '<div class="sn-flag">Сьогодні ще тихо — ось учорашній випуск. Свіжий складеться, щойно щось станеться.</div>' : '')
      + body
      + '<footer class="sn-foot"><span class="muted small">Головний редактор — Дядько Глек. Усі збіги з дійсністю невипадкові.</span>'
      + (viewDay ? '<button type="button" class="sn-fresh">До свіжого випуску</button>' : '') + '</footer>';
  }

  /// t — токен Огляду (не малювати запізнілу відповідь); гортання ◀ ▶ малює без нього.
  async function drawPaper(box, t) {
    const url = '/api/stats/gazette' + (viewDay ? '?day=' + viewDay : '');
    const d = await get(url, viewDay ? 3600e3 : 60e3);   // минулий випуск не міняється, свіжий — хвилину
    if (t !== undefined && k.stale(t)) return null;
    box.innerHTML = paperHtml(d);
    box.querySelectorAll('.sn-nav[data-day]').forEach((b) => b.onclick = () => {
      if (!b.dataset.day) return;
      viewDay = b.dataset.day;
      box.classList.add('sn-turn');
      drawPaper(box).catch(() => {}).finally(() => box.classList.remove('sn-turn'));
    });
    const fresh = box.querySelector('.sn-fresh');
    if (fresh) fresh.onclick = () => { viewDay = null; drawPaper(box).catch(() => {}); };
    wireGo(box);
    if (!viewDay) Pulse.sawTab('overview');   // свіжий випуск побачила — Огляд уже не «нове»
    return d;
  }

  // =============================================================================================
  // 📅 Цього дня місяць тому
  // =============================================================================================

  function agoHtml(a) {
    const heads = (a.heads || []).map((h) => '<li><span class="sn-ico" aria-hidden="true">' + esc(h.icon) + '</span><span>' + parts(h.parts) + '</span></li>').join('');
    return '<h3>📅 ' + esc(a.label) + ' <span class="muted small">' + esc(dayWord(a.day)) + '</span></h3>'
      + (heads ? '<ul class="sn-ago-list">' + heads + '</ul>' : '')
      + (a.top ? '<div class="small"><span class="muted">Найбільше грали:</span> ' + k.nickLink(a.top.nick) + ' — ' + esc(k.cnt(a.top.rounds, 'партія', 'партії', 'партій')) + '</div>' : '')
      + (a.song ? '<div class="small"><span class="muted">Крутили:</span> «' + esc(a.song) + '»</div>' : '')
      + '<button type="button" class="sn-open" data-day="' + esc(a.day) + '">Гортати той випуск (№ ' + a.no + ')</button>';
  }

  // =============================================================================================
  // 🎯 Мої цілі
  // =============================================================================================

  function goalsHtml(list) {
    return '<h3>🎯 Мої цілі <span class="muted small">що ось-ось</span></h3><ul class="sn-goals-list">' + list.map((g) => {
      const pct = g.of > 0 ? Math.max(4, Math.min(100, Math.round(100 * g.have / g.of))) : 0;
      return '<li><span class="sn-ico" aria-hidden="true">' + esc(g.icon) + '</span><div class="sn-txt"><div>' + parts(g.parts) + '</div>'
        + (g.of > 0 && g.have > 0 ? '<div class="sn-bar" role="presentation"><i style="--w:' + pct + '%"></i></div>' : '') + '</div>'
        + (g.href ? '<button type="button" class="sn-act" data-go="' + esc(g.href) + '">' + esc(g.label || 'Гайда') + '</button>' : '') + '</li>';
    }).join('') + '</ul>';
  }

  // =============================================================================================
  // Угорі «✨ Огляду»
  // =============================================================================================

  P.overviewTop(async (el, t) => {
    el.className = 'sn-top';
    el.innerHTML = '<section class="panel stbox sn-paper" aria-label="Газета дня"><div class="gwait"><span class="spin"></span> друкуємо газету…</div></section>'
      + '<div class="sn-side"><section class="panel stbox sn-ago" hidden></section><section class="panel stbox sn-goals" hidden></section></div>';
    const paper = el.querySelector('.sn-paper');
    const agoBox = el.querySelector('.sn-ago');
    const goalBox = el.querySelector('.sn-goals');
    const nick = myNick();
    const goals = nick ? get('/api/stats/goals?nick=' + encodeURIComponent(nick), 60e3).catch(() => null) : Promise.resolve(null);
    // «місяць тому» приходить разом зі свіжим випуском; гортали архів — свіжий беремо окремо (зазвичай із кешу)
    const front = viewDay ? get('/api/stats/gazette', 60e3).catch(() => null) : null;
    let d = null;
    try { d = await drawPaper(paper, t); } catch (e) { if (!k.stale(t)) paper.innerHTML = '<div class="gempty">Газета не вийшла: ' + esc(e.message) + '</div>'; }
    if (k.stale(t)) return;
    const a = (front ? await front : d) || {};
    if (k.stale(t)) return;
    if (a.ago) {
      agoBox.hidden = false;
      agoBox.innerHTML = agoHtml(a.ago);
      agoBox.querySelector('.sn-open').onclick = (ev) => {
        viewDay = ev.currentTarget.dataset.day;
        const smooth = !matchMedia('(prefers-reduced-motion: reduce)').matches;
        drawPaper(paper).then(() => paper.scrollIntoView({ block: 'start', behavior: smooth ? 'smooth' : 'auto' })).catch(() => {});
      };
    }
    const g = await goals;
    if (k.stale(t)) return;
    if (g && g.goals && g.goals.length) {
      goalBox.hidden = false;
      goalBox.innerHTML = goalsHtml(g.goals);
      wireGo(goalBox);
    }
    el.classList.toggle('sn-solo', agoBox.hidden && goalBox.hidden);
  });
})();
