// «Колія» — бот-автопілот (детермінований, бачить увесь стан). Власник: K2 (далі — задача «Бот»).
// Контракт: createBot(p) → { p, think(state) → { mx, my (цілі -100..100), use (bool) } }
export function createBot(p) {
  return { p, think() { return { mx: 0, my: 0, use: false }; } };
}
