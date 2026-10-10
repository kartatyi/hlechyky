/*
  «✨ Що нового на сайті» — window.HSiteNews (09.10.2026). Те саме, що «що нового» в іграх (games/core.js), але про
  сайт загалом: розділи, ставки, записки, черепки — те, що людина сама не помітить, бо воно не в грі, у яку вона ходить.

  Показ — один раз на реліз (NEWS.v), лише тим, хто вже бував на сайті: новенькому все нове, тож йому тихо ставимо
  «бачив». Не лізе поверх гри: поки людина за столом чи в панелі гри (#games/room/…, #games/x:…) або висить інше вікно,
  картка чекає. «Бачив» пам'ятає сервер (той самий /api/games/news, ключ «site»), гість — лише цей браузер.

  Новий реліз з помітним для людей = нове v і нові пункти; виправлень сюди не пишемо. Пункт із when ховається, коли
  фічу вимкнено в конфігу (me.slots, me.bets, me.shards — з /api/me).

  app.js кличе init(o) на старті й ready() після /api/me.
*/
(() => {
  'use strict';

  const SEEN_LS = 'siteNewsSeen';
  // Хто вже бував: нік у localStorage лишається від будь-якого минулого заходу (гість теж). Читаємо до app.js —
  // далі app.js може сам його записати, і новенький виглядав би старим.
  let hadNick = false;
  try { hadNick = !!localStorage.getItem('nick'); } catch { /* приватне вікно */ }

  const NEWS = {
    v: '2026-10-10',
    lead: '«📊 Хто скільки» переродився — тепер туди варто зазирати.',
    items: [
      { icon: '📰', when: () => true,
        html: '<b>«Глечицький вісник».</b> Щодня свіжа газета від Дядька Глека: хто кого розгромив, хто зірвав банк, кого прожарили. Архів — аж з 7 вересня, а поруч — що було рівно місяць тому.',
        go: ['Читати', (o) => o.go('#stats/overview')] },
      { icon: '🎯', when: () => true,
        html: '<b>Твої цілі.</b> Скільки бракує до звання тижня, кого ось-ось обженеш у «Хто кого» й у Гонці, чий рекорд поруч.' },
      { icon: '🏺', when: () => true,
        html: '<b>Вкладка «Глек».</b> Що він ставить і на чиїх піснях учиться, хто його головний критик, чий смак кращий, кого він смажить і скільки заробив (і програв) у казино.',
        go: ['До Глека', (o) => o.go('#stats/glek')] },
      { icon: '📈', when: () => true,
        html: '<b>Графіки й рекорди.</b> Гонка тижня за всі ігри зі стрілками, хто піднявся; біржа черепків — хто багатіє, хто спускає; Ело в часі; Книга рекордів Глечиків.',
        go: ['Глянути', (o) => o.go('#stats/charts')] },
      { icon: '✨', when: () => true,
        html: '<b>Огляд, звання й підколи</b>, «⚔ Хто кого», коли ми тусимо, перл Балачок, а в профілі — цікавинки про людину. Таблиці ігор нарешті рівні, а черепки сортуються кліком по заголовку — хто скільки має чи хто скільки заробив, за день чи за весь час. Ботів у таблицях більше нема — лише люди. Є що нове — на 📊 світиться крапка.' },
    ],
  };

  let o = null;            // що дає app.js: { me, api, esc, go, games }
  let waiting = false;

  const localSeen = () => { try { return localStorage.getItem(SEEN_LS) || ''; } catch { return ''; } };
  function mark() {
    try { localStorage.setItem(SEEN_LS, NEWS.v); } catch { /* приватне вікно */ }
    if (o.me.account) o.api('POST', '/api/games/news', { game: 'site', v: NEWS.v }).catch(() => { /* наступного разу ще раз — не біда */ });
  }

  /// Чи можна вилізти: не за грою, не поверх іншого вікна, не в схованій вкладці.
  function free() {
    if (document.hidden) return false;
    if (/^#games\/(room|x:)/.test(location.hash || '')) return false;
    return !document.querySelector('.modal:not([hidden])');
  }

  function wait(items) {
    if (waiting) return;
    waiting = true;
    const tryShow = () => {
      if (!free()) return;
      window.removeEventListener('hashchange', later);
      document.removeEventListener('visibilitychange', later);
      clearInterval(t);
      show(items);
    };
    const later = () => setTimeout(tryShow, 400);
    window.addEventListener('hashchange', later);
    document.addEventListener('visibilitychange', later);
    const t = setInterval(tryShow, 3000);
    setTimeout(tryShow, 1500);   // хай сторінка спершу домалюється
  }

  function show(items) {
    const esc = o.esc;
    const wrap = document.createElement('div');
    wrap.className = 'modal snews';
    wrap.innerHTML = '<div class="card">'
      + '<div class="snews-kick">✨ Що нового на сайті</div>'
      + '<p class="muted">' + esc(NEWS.lead) + '</p>'
      + '<ul class="snews-list">' + items.map((it, i) => '<li><span class="snews-ico" aria-hidden="true">' + it.icon + '</span><div>'
        + it.html + (it.go ? ' <button class="ghost snews-go" type="button" data-go="' + i + '">' + esc(it.go[0]) + ' →</button>' : '')
        + '</div></li>').join('') + '</ul>'
      + '<div class="row"><button class="primary" type="button" data-ok data-pad-first>Ясно!</button></div></div>';
    const close = () => {
      if (!wrap.isConnected) return;
      wrap.remove();
      document.removeEventListener('keydown', onKey, true);
    };
    const onKey = (e) => { if (e.key === 'Escape' || (e.key === 'Enter' && e.target.matches('[data-ok], body'))) { e.preventDefault(); e.stopPropagation(); close(); } };
    wrap.addEventListener('click', (e) => {
      if (e.target === wrap) { close(); return; }
      const b = e.target.closest('[data-go]');
      if (!b) return;
      close();
      try { items[+b.dataset.go].go[1](o); } catch (err) { console.warn('[sitenews]', err); }
    });
    wrap.querySelector('[data-ok]').onclick = close;
    document.body.appendChild(wrap);
    document.addEventListener('keydown', onKey, true);
    wrap.querySelector('[data-ok]').focus();
    mark();                                    // показали — значить бачив, навіть якщо закриє F5-ом
  }

  window.HSiteNews = {
    init(opts) { o = opts; },
    /// Після /api/me: уже відомо, акаунт це чи гість і що ввімкнено в конфігу.
    async ready() {
      if (!o || waiting || localSeen() === NEWS.v) return;
      // Новенький (ні ніка з минулого, ні акаунта): порівнювати нема з чим — тихо «бачив».
      if (!hadNick && !o.me.account) { try { localStorage.setItem(SEEN_LS, NEWS.v); } catch { /* приватне вікно */ } return; }
      if (o.me.account) {
        try {
          const r = await o.api('GET', '/api/games/news');
          if (r && r.seen && r.seen.site === NEWS.v) { try { localStorage.setItem(SEEN_LS, NEWS.v); } catch { /* */ } return; }
        } catch { /* сервер мовчить — покажемо, гірше не буде */ }
      }
      const items = NEWS.items.filter((it) => { try { return it.when(o.me); } catch { return false; } });
      if (items.length) wait(items);
    },
  };
})();
