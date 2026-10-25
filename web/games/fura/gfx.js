// Налаштування графіки «Чумаків» 0.0.5 (STYLE.md §17.3): пресети Низька / Середня / Висока / Ультра і окремі пункти,
// як у великих іграх. Будь-який пункт поверх пресета робить пресет «своя». Зберігається в localStorage['fura.gfx'];
// першого запуску пресет обирається сам (Deck і слабкі — «Низька», вбудована відеокарта — «Середня», решта — «Висока»).
// Меню — вікно гри (boxModal): кожен рядок — кнопка, натиск (миша, Enter, Ⓐ пада, «зчепити» керма) — наступне значення.

export const PRESETS = {
  // 30 fps для керування фурою — погано (рецензія 25.10): «Низька» тримає 60, 30 — окремим пунктом меню
  low: { scale: 0.75, aa: 'fxaa', shadows: 1024, clouds: 'low', cloudShadows: true, trees: 'smooth', budget: 0, draw: 'near', grass: false, bloom: false, grade: true, dof: 'off', fps: 60 },
  medium: { scale: 1, aa: 'fxaa', shadows: 1024, clouds: 'low', cloudShadows: true, trees: 'fluffy', budget: 1, draw: 'mid', grass: true, bloom: true, grade: true, dof: 'weak', fps: 60 },
  high: { scale: 1, aa: 'fxaa', shadows: 2048, clouds: 'high', cloudShadows: true, trees: 'fluffy', budget: 2, draw: 'far', grass: true, bloom: true, grade: true, dof: 'strong', fps: 60 },
  // «Ультра» без ліміту кадрів, з MSAA 4×, тінями 4096 і хмарами ultra вантажила 5060 Ti на 100 % (26.10): тримає 60 кадрів,
  // FXAA, тіні 2048 і хмари high, як «Висока», а відрізняється деталізацією; найважче лишається окремими пунктами меню
  ultra: { scale: 1, aa: 'fxaa', shadows: 2048, clouds: 'high', cloudShadows: true, trees: 'fluffy', budget: 3, draw: 'far', grass: true, bloom: true, grade: true, dof: 'strong', fps: 60 },
};
// особисте — пресет не чіпає: поле зору, пора доби, піксель
const PERSONAL = { fov: 62, time: 'kyiv', pixel: 1 };
export const PRESET_NAME = { low: 'Низька', medium: 'Середня', high: 'Висока', ultra: 'Ультра', custom: 'Своя' };

// [ключ, назва, [значення], підпис значення]
const onOff = (v) => (v ? 'увімк' : 'вимк');
export const OPTIONS = [
  ['preset', 'Пресет', ['low', 'medium', 'high', 'ultra'], (v) => PRESET_NAME[v]],
  ['time', 'Пора доби', ['kyiv', 'day', 'fast'], (v) => ({ kyiv: 'як у Києві зараз', day: 'завжди день', fast: 'пришвидшена (1 хв = 1 год)' }[v])],
  ['scale', 'Роздільність рендеру', [0.5, 0.67, 0.75, 0.85, 1], (v) => Math.round(v * 100) + ' %'],
  ['aa', 'Згладжування', ['none', 'fxaa', 'msaa'], (v) => ({ none: 'нема', fxaa: 'FXAA', msaa: 'MSAA 4×' }[v])],
  ['shadows', 'Тіні', [0, 512, 1024, 2048, 4096], (v) => (v ? String(v) : 'вимк')],
  ['clouds', 'Хмари', ['off', 'low', 'high', 'ultra'], (v) => ({ off: 'вимк', low: 'низькі', high: 'високі', ultra: 'ультра' }[v])],
  ['cloudShadows', 'Тіні від хмар', [true, false], onOff],
  ['draw', 'Дальність', ['near', 'mid', 'far'], (v) => ({ near: 'близько', mid: 'середня', far: 'далеко' }[v])],
  ['budget', 'Деталізація', [0, 1, 2, 3], (v) => ['низька', 'середня', 'висока', 'ультра'][v]],
  ['trees', 'Крони дерев', ['flat', 'smooth', 'fluffy'], (v) => ({ flat: 'грані', smooth: 'гладкі', fluffy: 'пухнасті' }[v])],
  ['grass', 'Трава', [true, false], onOff],
  ['dof', 'Розмиття вдалині', ['off', 'weak', 'strong'], (v) => ({ off: 'вимк', weak: 'слабке', strong: 'сильне' }[v])],
  ['bloom', 'Сяйво (bloom)', [true, false], onOff],
  ['grade', 'Кольорокорекція', [true, false], onOff],
  ['fps', 'Обмеження FPS', [30, 60, 0], (v) => (v ? String(v) : 'без')],
  ['fov', 'Поле зору', [55, 62, 70, 80], (v) => v + '°'],
  ['pixel', 'Піксельний стиль', [1, 2, 3], (v) => (v > 1 ? '×' + v : 'вимк')],
];

/** Пресет першого запуску: Deck і слабкі — «Низька», вбудована відеокарта — «Середня», решта — «Висока». */
export function autoPreset({ deck, gpu = '', cores = 8 } = {}) {
  if (deck || cores <= 4 || /SwiftShader|llvmpipe|Software/i.test(gpu)) return 'low';
  if (/Intel|UHD|Iris|Radeon\(TM\) Graphics|Vega \d|Adreno|Mali|Apple GPU|Apple M\d/i.test(gpu) && !/Arc/i.test(gpu)) return 'medium';
  return 'high';
}

/** Збережені налаштування або пресет першого запуску; first — чи це перший запуск (нічого не збережено). */
export function loadGfx(auto) {
  let saved = null;
  try { saved = JSON.parse(localStorage.getItem('fura.gfx') || 'null'); } catch { /* пусто */ }
  const preset = saved && (PRESETS[saved.preset] || saved.preset === 'custom') ? saved.preset : auto;
  const G = { preset, ...PERSONAL, ...PRESETS[preset === 'custom' ? 'high' : preset], ...(saved || {}) };
  // названий пресет — його значення, а не збережені (інакше зміна пресета в новій версії до старих гравців не доходить)
  if (PRESETS[preset]) Object.assign(G, PRESETS[preset]);
  for (const [k, , vals] of OPTIONS) if (k !== 'preset' && !vals.includes(G[k])) G[k] = (PRESETS[preset] || PRESETS.high)[k] ?? PERSONAL[k];
  return { G, first: !saved };
}
export function saveGfx(G) { try { localStorage.setItem('fura.gfx', JSON.stringify(G)); } catch { /* пусто */ } }

/** Змінити налаштування: спершу пресет, потім окремі пункти (інакше пресет перебив би «хмари вимк»). */
export function patchGfx(G, patch) {
  if (patch.preset && PRESETS[patch.preset]) Object.assign(G, PRESETS[patch.preset], { preset: patch.preset });
  let own = false;
  for (const [k, v] of Object.entries(patch)) {
    if (k === 'preset') continue;
    G[k] = v;
    if (!(k in PERSONAL)) own = true;
  }
  if (own) G.preset = Object.entries(PRESETS).find(([, p]) => Object.keys(p).every((k) => p[k] === G[k]))?.[0] || 'custom';
  return G;
}

/** Вікно «⚙ Графіка». o: { boxModal, G, onChange(patch), info() → рядок під списком }. */
export function openGfx({ boxModal, G, onChange, info }) {
  const row = ([k, name, vals, fmt]) => '<button type="button" class="fura-gfx-row" data-g="' + k + '"><span>' + name + '</span><b>'
    + (k === 'preset' && G.preset === 'custom' ? PRESET_NAME.custom : fmt(G[k])) + '</b></button>';
  return boxModal('<h3>⚙ Графіка</h3><div class="fura-gfx">' + OPTIONS.map(row).join('') + '</div>'
    + '<p class="muted small fura-gfx-info"></p>'
    + '<div class="fura-row"><button type="button" class="main" data-g="done">Готово</button></div>', (el, m) => {
    const paint = () => {
      for (const o of OPTIONS) {
        const b = el.querySelector('[data-g="' + o[0] + '"] b');
        if (b) b.textContent = o[0] === 'preset' && G.preset === 'custom' ? PRESET_NAME.custom : o[3](G[o[0]]);
      }
      const i = el.querySelector('.fura-gfx-info');
      if (i && info) i.textContent = info();
    };
    paint();
    const tick = setInterval(() => { if (!m.open) clearInterval(tick); else paint(); }, 1000);
    el.addEventListener('click', (e) => {
      const b = e.target.closest('[data-g]');
      if (!b) return;
      const k = b.dataset.g;
      if (k === 'done') { m.close(); return; }
      const o = OPTIONS.find((x) => x[0] === k);
      const vals = o[2], cur = k === 'preset' && G.preset === 'custom' ? -1 : vals.indexOf(G[k]);
      onChange({ [k]: vals[(cur + 1) % vals.length] });
      paint();
    });
  });
}
