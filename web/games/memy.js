// «Мемологія» (memy, memy-solo) — ЗАГЛУШКА етапу 2. Сервер уже є; справжній клієнт (картинка з проявленням, поле,
// варіанти, розкриття, пад, `added`) — етап 3 за контрактом docs/games/specs/memy.md §7, він переписує файл цілком.
// Тут — лише щоб у каталогу був модуль (ContractsTests) і картка не писала «завантажую…» вічно.
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="2" y="2.5" width="12" height="11" rx="2" fill="none" stroke="var(--accent)" stroke-width="1.6"/>'
    + '<circle cx="6" cy="6.5" r="1.3" fill="var(--clay)"/>'
    + '<path d="M3.5 12l3-3 2 2 2-2.5 2.5 3.5" fill="none" stroke="var(--ok)" stroke-width="1.4"/></svg>';

  function draw(root, ctx) {
    const v = (ctx && ctx.view) || {};
    if (!root.firstChild) root.innerHTML = '<div class="memy-stub muted"></div>';
    root.firstChild.textContent = 'Мемологія ще готується: сервер є, екран гри — незабаром.'
      + (v.phase ? ' Зараз: ' + v.phase + (v.round ? ', мем ' + v.round + ' з ' + v.rounds : '') : '');
  }

  const common = { icon: ICON, mount: draw, update: draw };
  HGames.register(Object.assign({ id: 'memy' }, common));
  HGames.register(Object.assign({ id: 'memy-solo' }, common));
})();
