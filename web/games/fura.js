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

  let game = null;        // запущений полігон: { stop() } з fura/main.js
  let starting = false;

  function card(root) {
    root.innerHTML = '<div class="fura-card">'
      + '<div class="fura-title">🚚 Чумаки-далекобійники <span class="fura-stage">етап 0 · полігон</span></div>'
      + '<p>Тягач із причепом на майданчику: покатайся, спробуй кермо й зачепи причіп заднім ходом. '
      + 'Рейсів і черепків ще нема — це проба, як фура відчувається.</p>'
      + '<ul class="fura-dev"><li>🛞 <b>Кермо</b> (G29 та інші) — при першому вході гра попросить відкалібрувати</li>'
      + '<li>🎮 <b>Пад / Steam Deck</b> — лівий стік кермо, R2 газ, L2 гальмо, Ⓐ зчепити</li>'
      + '<li>⌨ <b>Клавіатура</b> — W/S газ і гальмо, A/D кермо, T зчепити, C камера</li></ul>'
      + '<button class="btn fura-go" type="button">🚚 На полігон</button>'
      + '<div class="fura-err muted small"></div></div>';
    root.querySelector('.fura-go').addEventListener('click', () => start(root));
  }

  async function start(root) {
    if (game || starting) return;
    starting = true;
    const btn = root.querySelector('.fura-go');
    const err = root.querySelector('.fura-err');
    if (btn) { btn.disabled = true; btn.textContent = '⏳ Заводимо…'; }
    if (err) err.textContent = '';
    try {
      const mod = await import('/games/fura/main.js');
      game = await mod.start({ onExit: stop });
    } catch (e) {
      console.error('[fura]', e);
      if (err) err.textContent = 'Не завелось: ' + (e && e.message ? e.message : e);
    } finally {
      starting = false;
      if (btn) { btn.disabled = false; btn.textContent = '🚚 На полігон'; }
    }
  }

  function stop() {
    const g = game;
    game = null;
    if (g) { try { g.stop(); } catch (e) { console.warn('[fura] stop', e); } }
  }

  HGames.register({
    id: 'fura',
    icon: ICON,
    pad: {
      get dirs() { return !!game; },
      rstick: true,
      exit: false,
      on() { return !!game; },
      hint: '{dpad} кермо · R2 газ · L2 гальмо · {a} зчепити',
    },
    mount(root) { card(root); },
    update() {},
    unmount() { stop(); },
    status() { return game ? 'на полігоні' : ''; },
  });
})();
