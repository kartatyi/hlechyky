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
    v: '2026.10.25.8',
    lead: 'Шаткуй! тепер з характерними овочами й на всю компанію, щоденні — тижневий турнір, а в колі є Літопис.',
    items: [
      { icon: '🔪', when: (me) => !!me.games,
        html: '<b>Шаткуй! з характером.</b> Гарбуз — двічі, часник — на зубчики, перець пече, картопля мчить. '
          + 'Через центр — «Рівненько!» +2, без промахів — множник до ×4, у Класиці — гарбуз-велетень. Очок більше, тож черепок — за 250.',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '👥', when: (me) => !!me.games,
        html: '<b>Шаткуй разом і леза.</b> До восьми шаткарів: віз один на всіх, поле в кожного своє, суперники збоку. '
          + 'А в Лавці — коса, шабля й золотий ніж; срібний серп і вогняне лезо — за ачівки.',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '🏆', when: (me) => !!me.games,
        html: '<b>Січ щоденних.</b> За кожну розгадану щоденну — очко і ще по очку за кожного, кого обійшов. '
          + 'Неділя опівночі — 👑 Гетьман тижня на весь тиждень, 10 000 / 5 000 / 2 500 🏺 і номінації 🐓 🎯 ⚡ 🔗 по 500.',
        go: ['Глянути', () => { location.hash = '#games/x:sich'; }] },
      { icon: '🍶', when: (me) => !!me.games,
        html: '<b>Звання за щоденні.</b> Глина → Черепок → Глечик → Макітра → Гетьман: вічний рейтинг, де обійти сильного дає більше. '
          + 'Звання видно в картці людини, а в Січі — твої дуелі й підказка, що ще можна відіграти сьогодні.' },
      { icon: '📈', when: (me) => !!me.games,
        html: '<b>Літопис кола.</b> Окрема сторінка про твоє Гончарне коло: як ти ріс поруч із гончарями округи, '
          + 'скільки й коли сидиш біля кола, «Майстер дня», звідки беруться множники й скільки ще до стелі, '
          + 'яка глина що дає, і <b>як грати краще</b>. А в хаті кола тепер живу я, Дядько Глек: клацни на мене — підкажу з числом.',
        go: ['Відкрити', (o) => o.go('#gonchar')] },
      { icon: '🔊', when: (me) => !!me.games,
        html: '<b>Чумаки: справжній дизель.</b> Двигун тепер на записах вантажного тягача: холостий, тяга під гору, '
          + 'турбіна, моторне гальмо; з кабіни глухіше, ззовні — вихлоп. У меню ☰ — «🎚 Гучність»: окремо мотор, '
          + 'ефекти, Глек у рації й радіо. Пробіл тепер завжди ручник, навіть коли перед тим клацнув кнопку мишею.',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '🚛', when: (me) => !!me.games,
        html: '<b>Колона в Чумаках.</b> До восьми фур за одним столом: друг сідає до тебе навіть посеред рейсу, '
          + 'фури одне одного видно на дорозі й на мінімапі. На дошці є колонне замовлення — той самий вантаж і маршрут, '
          + 'причіп у кожного свій; здали в межах 5 хвилин — кожному +15 %. Хто вже в рейсі — видно в лобі.',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '🚚', when: (me) => !!me.games,
        html: '<b>Чумаки-далекобійники.</b> Своя фура з причепом: бери замовлення на дошці, чіпляй причіп у відправника '
          + 'заднім ходом і вези глечики, кахлі, мед чи сіль. Крихке б\'ється на ямах — їдь акуратно. 150–500 черепків '
          + 'за рейс, Глек — диспетчер у рації. Кермо G29, пад, Steam Deck або клавіатура.',
        go: ['До гри', (o) => o.go('#games')] },
      { icon: '💡', when: (me) => !!me.nick,
        html: '<b>5000 черепків за записку.</b> Знайшов баг чи дав ідею, яку зробили, — тримай 5000 від Глека. '
          + 'Пиши в «💡» у шапці.',
        go: ['Написати', () => { const b = document.getElementById('fbBtn'); if (b) b.click(); }] },
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
