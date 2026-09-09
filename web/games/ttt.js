/*
  Хрестики-нолики: класика ('ttt') і зникаючі ('ttt3') — два модулі в одному файлі.
  Правила однакові, різниця лише в тому, що в зникаючих одна мітка на полі підморгує:
  саме її змете наступний хід свого ж господаря. Каркас дозволяє кілька register в одному
  файлі, тому окремого ttt3.js не існує — завантажувач його пропустить, побачивши has('ttt3').

  Вид із сервера (Impl/GridGame.cs): { cells: ('x'|'o'|null)[], width, turn, line, fading }.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.3 2.3 6.9 6.9 M6.9 2.3 2.3 6.9" stroke="var(--accent)" stroke-width="1.8" stroke-linecap="round" fill="none"/>'
    + '<circle cx="11.1" cy="11.1" r="3" stroke="var(--ok)" stroke-width="1.8" fill="none"/></svg>';
  const MARK = ['✕', '◯'];

  /// Мітка місця: сервер каже 'x'/'o', але поле може прийти й числом місця.
  const markOf = (c) => (c === 'x' || c === 0 ? MARK[0] : c === 'o' || c === 1 ? MARK[1] : '');
  const clsOf = (c) => (c === 'x' || c === 0 ? 'x' : c === 'o' || c === 1 ? 'o' : '');

  function paint(root, ctx) {
    const v = ctx.view || {};
    const cells = v.cells || [];
    const w = v.width || Math.round(Math.sqrt(cells.length)) || 3;
    const line = v.line || [];
    HGames.ui.grid(root, {
      cols: w,
      rows: Math.max(1, Math.round(cells.length / w)),
      cell: (i) => ({
        html: markOf(cells[i]),
        cls: [clsOf(cells[i]), line.includes(i) ? 'win' : '', i === v.fading ? 'fading' : ''].filter(Boolean).join(' '),
        disabled: !(ctx.myTurn && !cells[i]),
      }),
      onCell: (i) => ctx.act('move', { cell: i }),
    });
  }

  const mod = (id) => ({
    id,
    icon: ICON,
    seatNames: ['✕', '◯'],
    seatClass: ['x', 'o'],
    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },
  });

  HGames.register(mod('ttt'));
  HGames.register(mod('ttt3'));
})();
