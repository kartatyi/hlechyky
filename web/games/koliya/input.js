// «Колія» — керування: клавіатура й пади (Gamepad API), до 2 локальних гравців. Власник: K2.
// Контракт: createInput() → { poll(): [{ mx, my (-1..1), use (затиснуто) }] для гравців 0..1,
//   restartPressed(): true раз на натиск R / Start, dispose() }
// Заглушка скелета: гравець 0 — WASD + Space, гравець 1 — стрілки + Enter.
export function createInput() {
  const down = new Set();
  let restart = false;
  const kd = (e) => { down.add(e.code); if (e.code === 'KeyR') restart = true; };
  const ku = (e) => down.delete(e.code);
  addEventListener('keydown', kd); addEventListener('keyup', ku);
  const axis = (a, b) => (down.has(b) ? 1 : 0) - (down.has(a) ? 1 : 0);
  return {
    poll() {
      return [
        { mx: axis('KeyA', 'KeyD'), my: axis('KeyW', 'KeyS'), use: down.has('Space') || down.has('KeyE') },
        { mx: axis('ArrowLeft', 'ArrowRight'), my: axis('ArrowUp', 'ArrowDown'), use: down.has('Enter') },
      ];
    },
    restartPressed() { const r = restart; restart = false; return r; },
    dispose() { removeEventListener('keydown', kd); removeEventListener('keyup', ku); },
  };
}
