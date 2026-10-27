// «Колія» — панель повзунків і перемикачів. Власник: K4.
// Контракт: createPanel(root, { meta, variants, values, onChange(values), onRestart(), audio }) → { set(values), dispose() }
//  meta = PARAM_META (повзунки за групами), variants = VARIANTS (перемикачі); onChange — після кожної правки (main.js
//  зберігає в localStorage і застосовує з наступного рестарту); onRestart — кнопка «Почати заново».
// Заглушка скелета: лише кнопка рестарту.
export function createPanel(root, opts) {
  const b = document.createElement('button');
  b.textContent = '↻';
  b.title = 'Почати заново (R)';
  b.onclick = () => opts.onRestart();
  root.appendChild(b);
  return { set() { }, dispose() { b.remove(); } };
}
