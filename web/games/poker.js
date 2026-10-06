// Покер — ЗАГЛУШКА етапу А (сервер). Справжній модуль (стіл, карти, панель ставки) — етап Б, specs/poker.md §6.
// Лежить, щоб каталог не вказував на відсутній файл. Форма виду й дій — specs/poker.md «Як реалізовано».
(function () {
  'use strict';
  HGames.register({
    id: 'poker',
    added: '2026-10-06',
    mount(root) {
      root.innerHTML = '<div class="poker-stub" style="padding:16px;color:var(--muted)">Покер ще накривають на стіл…</div>';
    },
    update() {},
    unmount() {},
  });
})();
