// «Колія» — картка столу в «Іграх» (лобі: «Жваві» → «Аркада»; стіл і посиланням #games/new/koliya).
// Сама гра живе на окремій сторінці web/games/koliya/proto.html (світ рахує браузер, сервер лише нумерує команди —
// див. шапку koliya/net.js), тож картка — рамка з тією сторінкою, підключеною до цього столу (?net=<стіл>).
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M4 1v14M12 1v14" stroke="var(--muted)" stroke-width="1.4"/>'
    + '<path d="M3 3h10M3 6.5h10M3 10h10M3 13.5h10" stroke="var(--clay)" stroke-width="1.4"/></svg>';
  const frames = new WeakMap();

  HGames.register({
    id: 'koliya',
    icon: ICON,
    added: '2026-10-27',
    leaveFree: true,   // вихід посеред партії — не поразка (Koliya.OnLeave: решта грає далі); core.js askLeave
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
      f.style.cssText = 'width:100%;height:min(78vh,720px);border:0;border-radius:8px;display:block';
      f.allow = 'gamepad; autoplay';
      root.appendChild(f);
      frames.set(root, f);
    },
    unmount(root) { frames.delete(root); root.innerHTML = ''; },
  });
})();
