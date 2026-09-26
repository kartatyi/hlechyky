// Сільське ралі: паритет C# ↔ JS (spec §5.7). Журнали вводу з tests/Hlechyky.Tests/Games/RallyReplays/*.json
// прокручуються тією самою RallySim, що передбачає свою машину в браузері, і хеш стану має збігтися з тим,
// що записав C# (RallyCore). Плюс контрольні суми таблиць sin/cos.
//
// Запуск (журнали підставляє обгортка — браузерові їх більше нізвідки взяти):
//   python docs/games/dev/rally-parity.py --port 9631 --url "http://127.0.0.1:8222/?cb=1#games"
// Друкує OK / MISMATCH для кожного журналу.
const journals = window.__rallyJournals || [];
if (!window.RallySim) {
  // модуль гри ще не вантажився (стіл ралі не відкривали) — підтягнемо сам
  await new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = '/games/rally.js?cb=' + Date.now();
    s.onload = resolve;
    s.onerror = () => reject(new Error('rally.js не завантажився'));
    document.head.appendChild(s);
  });
}
const out = [];
const tables = window.RallySim.tables();
out.push((tables.sin === 'c53c97bf' && tables.cos === '4c534e0f' ? 'OK' : 'MISMATCH') + ' таблиці sin/cos: ' + tables.sin + ' / ' + tables.cos);
for (const j of journals) {
  const t0 = performance.now();
  const got = window.RallySim.replay(j);
  const ms = (performance.now() - t0).toFixed(1);
  out.push((got === j.hash ? 'OK' : 'MISMATCH') + ' ' + j.name + ': ' + j.track + ', ' + j.inputs.length + ' вводів, '
    + j.ticks + ' тиків — C# ' + j.hash + ', JS ' + got + ' (' + ms + ' мс)');
}
if (!journals.length) out.push('MISMATCH журналів нема — запускай через rally-parity.py');
return out;
