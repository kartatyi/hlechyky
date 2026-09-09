/*
  Чотири в ряд. Дошка та сама сітка, що й у хрестиків, тільки клітинки круглі, а хід — це колонка:
  фішку на дно кладе сервер. Тому клікабельна вся колонка, поки її верхня клітинка порожня.

  Вид із сервера (Impl/GridGame.cs): { cells: ('x'|'o'|null)[], width, turn, line }.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="2.6" cy="13.4" r="2.1" fill="var(--accent)"/>'
    + '<circle cx="6.4" cy="9.6" r="2.1" fill="var(--accent)"/>'
    + '<circle cx="10.2" cy="5.8" r="2.1" fill="var(--accent)"/>'
    + '<circle cx="14" cy="2" r="2.1" fill="var(--accent)"/>'
    + '<circle cx="2.6" cy="5.8" r="2.1" fill="var(--ok)"/>'
    + '<circle cx="6.4" cy="2" r="2.1" fill="var(--ok)"/></svg>';

  const clsOf = (c) => (c === 'x' || c === 0 ? 'x' : c === 'o' || c === 1 ? 'o' : '');

  function paint(root, ctx) {
    const v = ctx.view || {};
    const cells = v.cells || [];
    const w = v.width || 7;
    const line = v.line || [];
    HGames.ui.grid(root, {
      cols: w,
      // до старту cells порожній — показуємо типову дошку 7×6, а не один ряд
      rows: cells.length ? Math.ceil(cells.length / w) : 6,
      cls: 'discs',
      cell: (i) => ({
        html: '',
        cls: [clsOf(cells[i]), line.includes(i) ? 'win' : ''].filter(Boolean).join(' '),
        // вільна колонка = порожня клітинка у верхньому ряду
        disabled: !(ctx.myTurn && !cells[i % w]),
      }),
      // сервер (GridGame) чекає на { cell }; у грі з гравітацією cell — це номер колонки, а не клітинки
      onCell: (i) => ctx.act('move', { cell: i % w }),
    });
  }

  HGames.register({
    id: 'c4',
    icon: ICON,
    seatNames: ['жовті', 'зелені'],
    seatClass: ['x', 'o'],
    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },
  });
})();
