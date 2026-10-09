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
    v: '2026-10-09',
    lead: 'Накопичилось чимало — ось головне.',
    items: [
      { icon: '🎰', when: (me) => me.games,
        html: '<b>Розділ «Азарт» в Іграх.</b> Слоти, рулетка, Лелека, ставки й покер — в одному місці. А весь каталог тепер поділено на теми, щоб серед 70 ігор не губитись.',
        go: ['Глянути', (o) => o.games && o.games.openSection ? o.games.openSection('azart') : o.go('#games')] },
      { icon: '🍒', when: (me) => me.games && me.slots,
        html: '<b>Чотири автомати на черепки:</b> Однорукий Глек, Козацький скарб, Цвіт папороті й Розбиті глеки. З кожного оберту росте Скарбничка Глека — хтось її таки зірве.' },
      { icon: '📈', when: (me) => me.games && me.lelka,
        html: '<b>Лелека.</b> Лелека несе Глека вгору, множник росте — забери черепки, поки не впустила. Один політ на всіх.' },
      { icon: '🎲', when: (me) => me.games && me.bets && me.bets.events,
        html: '<b>Ставки на події.</b> Хто виграє матч, що станеться у світі: Глек дає кеф, ставиш черепки. Свою подію можна запропонувати адміну.',
        go: ['До ставок', (o) => o.go('#games/x:bets')] },
      { icon: '🃏', when: (me) => me.games && me.bets && me.bets.tables,
        html: '<b>Ставки за столами.</b> Перед партією чи на «Ще раз» можна поставити черепки на того, хто виграє, — і на себе теж.' },
      { icon: '✋', when: (me) => me.games,
        html: '<b>«✋ Готовий» за столами.</b> Хто сидить, тисне «Готовий», а той, хто починає партію, бачить, чи всі зібрались.' },
      { icon: '💡', when: () => true,
        html: '<b>Записки «💡 Розробнику» з картинками.</b> Скрін — через 📎 або Ctrl+V. До записки сам додається, де ти на сайті й з чого заходиш, а до бага — ще й останні помилки сторінки: так його легше знайти.' },
      { icon: '🪙', when: (me) => me.shards && (me.shards.buy || me.shards.sell),
        html: '<b>Черепки за гривні</b> — тепер великими кнопками в профілі, у картці «🏺 Черепки».' },
      { icon: '🏺', when: (me) => me.games,
        html: '<b>Гончарне коло:</b> серія глеків з полиці після сотні дає +1 % за кожен глек замість +2 %. До сотні все як було.' },
      { icon: '🔐', when: (me) => me.account,
        html: '<b>«Вийти на всіх пристроях»</b> — у картці «Ти — …»: як зайшов десь не на своєму.' },
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
