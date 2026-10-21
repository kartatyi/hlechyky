/*
  «Майстер дня» Гончарного кола (v12, docs/games/specs/clicker-v12.md §5). Частина ядра clicker.js — пакет C.
  Табло дня в «Селі» й чип у рядку статусу кола. Дані — view.day (ClickerDay.cs): day, ends, prizes, minPoints,
  minPlayers, person, mine {place, points}, board [{place, nick, points, me}], players, last {day, line}.
  Шов ядра: порожня частина; пакет C наповнює.
*/
(() => {
  if (!window.HClicker || !window.HClicker.part) return;
  window.HClicker.part({ id: 'day', order: 64.5 });
})();
