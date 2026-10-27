// «Колія» — картка столу в «Іграх» (лобі: «Жваві» → «Аркада»; стіл і посиланням #games/new/koliya).
// Сама гра живе на окремій сторінці web/games/koliya/proto.html (світ рахує браузер, сервер лише нумерує команди —
// див. шапку koliya/net.js), тож картка — рамка з тією сторінкою, підключеною до цього столу (?net=<стіл>).
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M4 1v14M12 1v14" stroke="var(--muted)" stroke-width="1.4"/>'
    + '<path d="M3 3h10M3 6.5h10M3 10h10M3 13.5h10" stroke="var(--clay)" stroke-width="1.4"/></svg>';
  const frames = new WeakMap();

  // Рамка: звично — до 720 px заввишки; у ⛶ столу (body.gfull) — до низу вікна, щоб гра не стояла віконцем посеред
  // порожнього екрана. Що під рамкою в картці (місця гравців) — лишаємо видимим.
  let booted = false;
  function fitFrame(f) {
    if (!document.body.classList.contains('gfull')) { if (f.style.height) f.style.height = ''; return; }
    const r = f.getBoundingClientRect(), box = f.closest('.gtable');
    const below = box ? Math.max(0, box.getBoundingClientRect().bottom - r.bottom) : 0;
    const dock = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--gdock-h')) || 0;
    const h = Math.max(320, Math.floor(window.innerHeight - dock - (r.top + window.scrollY) - below - 12)) + 'px';
    if (f.style.height !== h) f.style.height = h;
  }
  function boot() {
    if (booted) return;
    booted = true;
    const st = document.createElement('style');
    st.textContent = '.kol-frame{width:100%;height:min(78vh,720px);border:0;border-radius:8px;display:block}';
    document.head.appendChild(st);
    const all = () => document.querySelectorAll('iframe.kol-frame').forEach(fitFrame);
    new MutationObserver(all).observe(document.body, { attributes: true, attributeFilter: ['class'] });
    window.addEventListener('resize', all);
  }

  HGames.register({
    id: 'koliya',
    icon: ICON,
    added: '2026-10-27',
    leaveFree: true,   // вихід посеред партії — не поразка (Koliya.OnLeave: решта грає далі); core.js askLeave
    // Джойстик гра читає сама, у рамці (koliya/input.js). Шар пада сайту (web/static/pad.js) за столом лише мовчить:
    // стік не водить кільце фокуса сторінкою, кнопки не тиснуть сайт — інакше Ⓑ виводила зі столу, а плечі гортали
    // розділи. Зі столу — утримання Ⓑ, як в інших живих іграх.
    pad: { dirs: true, rstick: true, on: (b) => b !== 'b', hint: '{dpad} ходити · {a} рубати, брати, класти · {y} гудок' },
    mount(root, ctx) { this.update(root, ctx); },
    update(root, ctx) {
      const id = ctx.room && ctx.room.id;
      if (!id) return;
      let f = frames.get(root);
      if (f && f.dataset.room === id && f.isConnected) return;
      const nick = (ctx.me && ctx.me.nick) || '';
      root.innerHTML = '';
      f = document.createElement('iframe');
      f.dataset.room = id;
      // K55: перевірочні ключі сайту (/?lag=120&drive=1#games/room/…) — у рамку: пінг, автоводій, ≤ 10 кадрів/с
      const pass = new URLSearchParams(location.search), extra = ['lag', 'jit', 'drive', 'smooth', 'test']
        .filter((k) => pass.has(k)).map((k) => '&' + k + (pass.get(k) ? '=' + encodeURIComponent(pass.get(k)) : '')).join('');
      f.src = '/games/koliya/proto.html?net=' + encodeURIComponent(id) + (nick ? '&nick=' + encodeURIComponent(nick) : '') + extra;
      f.className = 'kol-frame';
      f.allow = 'gamepad; autoplay; fullscreen';   // fullscreen — ⛶ у самій грі (koliya/panel.js) розгортає рамку на весь екран
      boot();
      root.appendChild(f);
      frames.set(root, f);
      fitFrame(f);
    },
    unmount(root) { frames.delete(root); root.innerHTML = ''; },
  });
})();
