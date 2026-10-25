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

  // Версія сайту (web/history-data.js) — той самий день; другий реліз того ж дня — 2026.10.13.2.
  const NEWS = {
    v: '2026.10.26',
    lead: 'Чумаки 0.0.7: Житомир, машини на дорогах і нове небо.',
    items: [
      { icon: '🏙', when: (me) => !!me.games,
        html: '<b>Житомир.</b> Друга мапа — справжній Житомир з OpenStreetMap: вулиці, траси, будинки, водойми й рельєф. '
          + 'Ставиш стіл — обираєш «Край» чи «Житомир». Гараж, досвід і денна стеля — спільні, а рейс на кожній мапі свій: '
          + 'поїхав у Житомир — рейс у краю дочекається.',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '🚗', when: (me) => !!me.games,
        html: '<b>Машини на дорогах.</b> Краєм і Житомиром їздять легковики й буси — однакові для всіх за столом. Вони пригальмують '
          + 'за фурою, але влетиш у машину — буде удар, і глечики поб\'ються. У «⚙ Графіка» — пункт «Трафік»: вимк, мало, багато.',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '☁', when: (me) => !!me.games,
        html: '<b>Чумаки 0.0.7: небо й графіка.</b> Небо світить за справжнім київським часом — світанок, полудень, захід, '
          + 'ніч із зорями; об\'ємні хмари кидають тіні на поля, далеке м\'якне й тоне в повітрі, нові дерева. У меню ☰ — '
          + '«⚙ Графіка»: пресети від «Низької» до «Ультра», тіні, хмари, розмиття вдалині, обмеження кадрів. '
          + 'Гра сама обирає пресет під твій комп\'ютер, а Steam Deck — «Низьку».',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '🔧', when: (me) => !!me.games,
        html: '<b>Чумаки 0.0.6: гараж.</b> За кожен рейс — досвід і рівень (до 20-го). За черепки — тягачі «Віл» і «Ластівка», '
          + 'причепи «Ізотерм», «Пух» і «Низькорамник» під нові вантажі, майстерня (двигун, коробка, гальма, підвіска, шини, кабіна) і вигляд: '
          + 'фарба, розпис (петриківка, вишиванка), ріжок, прапорець і люстра на дах, наліпки. Перед покупкою — тест-драйв. Хто вже їздив — досвід за старі рейси зараховано. '
          + 'Гараж — кнопкою на дошці замовлень, у меню ☰ або клавішею G.',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '💥', when: (me) => !!me.games,
        html: '<b>Чумаки 0.0.4.1: зіткнення наживо.</b> На столі з «💥» фури друзів тепер тверді й через інтернет: '
          + 'і у дворі, і поруч на ходу. Примарні лише біля самих боксів. Режим столу видно в грі плашкою й у «Живих столах», '
          + 'а вхід «🚚 Чумаки» простий: Їхати, режим 👻/💥, хто зараз у рейсі, Полігон.',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '🛣', when: (me) => !!me.games,
        html: '<b>Чумаки 0.0.4: дорога 2.0.</b> Траса — дві смуги з узбіччям і розміткою, села — свої вузькі дороги, '
          + 'ґрунтовки з коліями. Перехрестя з заокругленими кутами, з\'їзди у двори розширюються, на високих насипах і '
          + 'мостах — відбійники, що тримають фуру. Якщо фура після перезаходу опинилась у стіні — сама стане на смугу.',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '💥', when: (me) => !!me.games,
        html: '<b>Чумаки 0.0.3: зіткнення.</b> Ставиш стіл — обираєш «👻 привиди» чи «💥 зіткнення». У зіткненнях фури друзів '
          + 'тверді: влетиш — того, в кого влетів, відкине, а глечики поб\'ються в обох. У дворах і на малій швидкості фури проходять крізь, '
          + 'щоб ніхто не заблокував бокс і причіп.',
        go: ['До гри', (o) => o.go('#games')] },
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
      + '<div class="row"><button class="primary" type="button" data-ok data-pad-first>Ясно!</button>'
      + (window.HHistory ? '<a class="ghost snews-hist" href="#history" data-hist>📜 Уся історія</a>' : '') + '</div></div>';
    const close = () => {
      if (!wrap.isConnected) return;
      wrap.remove();
      document.removeEventListener('keydown', onKey, true);
    };
    const onKey = (e) => { if (e.key === 'Escape' || (e.key === 'Enter' && e.target.matches('[data-ok], body'))) { e.preventDefault(); e.stopPropagation(); close(); } };
    wrap.addEventListener('click', (e) => {
      if (e.target === wrap) { close(); return; }
      // «📜 Уся історія» — звичайне посилання на #history: картку лише прибираємо, перехід зробить сам браузер.
      if (e.target.closest('[data-hist]')) { close(); return; }
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
