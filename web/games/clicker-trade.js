/*
  Заморська торгівля Гончарного кола (v12, docs/games/specs/clicker-v12.md §3). Частина ядра clicker.js — пакет A.
  Розділ «🚢 Заморська торгівля» у вкладці «🤝 Село»: кораблі біля Пристані, вантаж (trade { do: 'load' | 'loadAll', id, n }),
  Слава у світі й наступна заморська майстерня. Дані — view.trade (ClickerTrade.cs), пороги — shopCatalog.slavaGates.
  Шов ядра: порожня частина; пакет A наповнює.
*/
(() => {
  if (!window.HClicker || !window.HClicker.part) return;
  window.HClicker.part({ id: 'trade', order: 64 });
})();
