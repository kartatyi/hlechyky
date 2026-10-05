// Глечикова вечірка (vechirka) — ЗАГЛУШКА клієнта до етапу K1 (specs/vechirka.md §15): сервер S1 уже грає, а справжній
// модуль (дошка SVG, картки, міні-ігри через HGames.embed) пише K1 поверх цього файла. Поки — рядок стану.
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M5 3h6l-1 2c2 1 3 3 3 5 0 3-2 4-5 4s-5-1-5-4c0-2 1-4 3-5z" fill="var(--clay)"/>'
    + '<path d="M8 7l.7 1.4 1.5.2-1.1 1 .3 1.5L8 10.4l-1.4.7.3-1.5-1.1-1 1.5-.2z" fill="var(--accent)"/></svg>';

  function render(root, ctx) {
    const v = ctx.view || {};
    const p = (v.players || []).map((x) => `${x.name} 🏺${x.gleks} 🪙${x.coins}`).join(' · ');
    root.innerHTML = '<div class="muted small" style="padding:16px"></div>';
    root.firstChild.textContent = v.phase === 'lobby'
      ? `Глечикова вечірка: ≈ ${v.lobby ? v.lobby.rounds : '?'} кіл. Клієнт ще будується.`
      : `Коло ${v.round}/${v.rounds} · ${v.phase} · ${p}`;
  }

  HGames.register({
    id: 'vechirka',
    added: '2026-10-06',
    icon: ICON,
    mount(root, ctx) { render(root, ctx); },
    update(root, ctx) { render(root, ctx); },
    unmount(root) { root.innerHTML = ''; },
  });
})();
