/* «Чумаки-далекобійники» — етап 0, полігон (план: D:\or-wt\_fura\PLAN.md).
   Картка столу лише запускає гру: усе важке — 3D (three.js), фізика (Rapier, WASM), кермо — живе в ES-модулях
   web/games/fura/ і вантажиться через import() лише після «На полігон». Гра йде на весь екран поверх сайту
   (стеля .gcanvas 560 px для фури замала), ✕ повертає на картку.

   Пад: поки гра відкрита, вона сама читає стіки й курки (fura/input.js), тож pad.js не має ні гортати сторінку,
   ні тиснути кнопки сайту — `dirs` вмикається лише на час гри, а `on` з'їдає всі кнопки. Кермо pad.js і так
   відсіює, його гра читає прямо з navigator.getGamepads(). */
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1" y="4" width="9" height="7" rx="1" fill="var(--clay, #b5582f)"/>'
    + '<path d="M10 6h2.6l2.4 2.6V11h-5z" fill="var(--accent, #d08a2c)"/>'
    + '<circle cx="4" cy="12" r="1.6" fill="var(--muted, #555)"/><circle cx="12" cy="12" r="1.6" fill="var(--muted, #555)"/></svg>';

  let game = null;        // запущена гра: { stop() } з fura/main.js
  let ctxNow = null;      // останній ctx столу: з нього міст бере вид і шле дії
  // Міст «стіл ↔ 3D»: тихі дії (відповідь гра показує сама — тости сайту сховані під грою), телеметрія Input і вид
  const bridge = {
    tell: (a, p) => (ctxNow ? ctxNow.tell(a, p) : Promise.resolve(null)),
    input: (a, p) => { if (ctxNow) ctxNow.input(a, p); },
    view: () => (ctxNow ? ctxNow.view : null),
    onView: null,
  };
  let starting = false;

  function card(root) {
    root.innerHTML = '<div class="fura-card">'
      + '<div class="fura-title">🚚 Чумаки-далекобійники <span class="fura-stage">етап 1 · у роботі</span></div>'
      + '<p><b>Чумацький край</b> — пагорби, річка з мостами, траса, сільські дороги й ґрунтовки між депо, Гончарівкою, '
      + 'Цегельнею, Медовим, Соляним складом і Полтавкою. Рейси й черепки — скоро. <b>Полігон</b> — майданчик, щоб '
      + 'потренуватись чіплятись заднім ходом.</p>'
      + '<ul class="fura-dev"><li>🛞 <b>Кермо</b> (G29 та інші) — при першому вході гра попросить відкалібрувати</li>'
      + '<li>🎮 <b>Пад / Steam Deck</b> — лівий стік кермо, R2 газ, L2 гальмо, Ⓐ зчепити</li>'
      + '<li>⌨ <b>Клавіатура</b> — W/S газ і гальмо, A/D кермо, T зчепити, C камера</li></ul>'
      + '<div class="fura-row"><button class="btn fura-go" type="button" data-mode="kray">🗺 Чумацький край</button>'
      + '<button class="btn fura-go fura-go2" type="button" data-mode="polygon">🚧 Полігон</button></div>'
      + '<div class="fura-err muted small"></div></div>';
    root.querySelectorAll('.fura-go').forEach((b) => b.addEventListener('click', () => start(root, b)));
  }

  async function start(root, btn) {
    if (game || starting) return;
    starting = true;
    const err = root.querySelector('.fura-err');
    const label = btn.textContent;
    root.querySelectorAll('.fura-go').forEach((b) => { b.disabled = true; });
    btn.textContent = '⏳ Заводимо…';
    if (err) err.textContent = '';
    try {
      const mod = await import('/games/fura/main.js');
      bridge.onView = null;
      game = await mod.start({ onExit: stop, mode: btn.dataset.mode, server: btn.dataset.mode === 'kray' ? bridge : null });
    } catch (e) {
      console.error('[fura]', e);
      if (err) err.textContent = 'Не завелось: ' + (e && e.message ? e.message : e);
    } finally {
      starting = false;
      root.querySelectorAll('.fura-go').forEach((b) => { b.disabled = false; });
      btn.textContent = label;
    }
  }

  function stop() {
    const g = game;
    game = null;
    bridge.onView = null;
    if (g) { try { g.stop(); } catch (e) { console.warn('[fura] stop', e); } }
  }

  HGames.register({
    id: 'fura',
    icon: ICON,
    added: '2026-10-25',
    pad: {
      get dirs() { return !!game; },
      rstick: true,
      exit: false,
      on() { return !!game; },
      hint: '{dpad} кермо · R2 газ · L2 гальмо · {a} зчепити',
    },
    mount(root, ctx) { ctxNow = ctx; card(root); },
    update(root, ctx) {
      ctxNow = ctx;
      if (game && bridge.onView) { try { bridge.onView(ctx.view); } catch (e) { console.warn('[fura] view', e); } }
    },
    unmount() { stop(); },
    status() { return game ? 'на полігоні' : ''; },
  });
})();
