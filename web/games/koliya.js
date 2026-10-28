// «Колія» — картка столу в «Іграх» (лобі: «Жваві» → «Аркада»; стіл і посиланням #games/new/koliya).
// Сама гра живе на окремій сторінці web/games/koliya/proto.html (світ рахує браузер, сервер лише нумерує команди —
// див. шапку koliya/net.js), тож картка — рамка з тією сторінкою, підключеною до цього столу (?net=<стіл>).
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M4 1v14M12 1v14" stroke="var(--muted)" stroke-width="1.4"/>'
    + '<path d="M3 3h10M3 6.5h10M3 10h10M3 13.5h10" stroke="var(--clay)" stroke-width="1.4"/></svg>';
  const frames = new WeakMap();
  // «Встав із цього столу» — у sessionStorage вкладки (рамка того самого походження читає його ж): рамка, що
  // перезавантажилась (перенесення картки в DOM, F5), не сідає сама знову (koliya/net.js enter). Сів знову — знімаємо.
  const LEFT = 'kol-left:';
  // Лише кнопка «Встати» (left): місце, звільнене обривом зв'язку (Rooms.Grace), — не «встав», рамка сідає назад сама.
  const mark = (id, on) => { try { if (on) sessionStorage.setItem(LEFT + id, '1'); else sessionStorage.removeItem(LEFT + id); } catch { } };

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
    // core.js — щойно «Встати» вдалось, ще до того, як картка поїде в склад (вид «не сидиш» може прийти пізніше)
    left(id) { mark(id, true); },
    // Посеред партії «Сісти» каркас не малює (його canSit — лише поза грою), а Колія пускає будь-коли (Koliya.LateJoin):
    // хто встав чи дивився збоку, сідає цією кнопкою (JoinRoom → Rooms.LateSeat), поки є вільне місце
    rejoin(rv) {
      const seats = (rv.room && rv.room.seats) || [];
      return seats.some((s) => !(typeof s === 'string' ? s : s && s.nick)) ? 'Сісти' : '';
    },
    update(root, ctx) {
      const id = ctx.room && ctx.room.id;
      if (!id) return;
      if (ctx.seat != null) mark(id, false);
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
      // Розгорнути — два способи: ⛶ столу (core.js) ховає сайт навколо, гра лишається рамкою до 720 px; ⛶ у самій грі
      // (koliya/panel.js) — справжній повний екран, гра на весь монітор (для цього allow="fullscreen")
      f.style.cssText = 'width:100%;height:min(78vh,720px);border:0;border-radius:8px;display:block';
      f.allow = 'gamepad; autoplay; fullscreen';
      root.appendChild(f);
      frames.set(root, f);
    },
    unmount(root) { frames.delete(root); root.innerHTML = ''; },
  });
})();
