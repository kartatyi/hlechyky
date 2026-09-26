// «Вогник і Крапля»: паритет C# ↔ JS (specs/vohnyk.md §5.5). Кожен рівень лежить у data/vohnyk/levels разом із
// записаним проходженням (solution) і хешами світу, які порахував C# (check: кожні 100 кроків і фінальний). Тут те
// саме проходження крутить VohnykSim — та сама симуляція, що передбачає героя в браузері, — і хеші мають збігтися
// до біта. Node на машині нема, тож обидві половини звіряються з одним записаним файлом.
//
// Запуск (рівні з розв'язками віддає лише адмінові — у dev AdminKey=dev, кука ставиться з ?k=dev):
//   python D:/or-wt/_tools/cdp2.py --port 9683 --url "http://127.0.0.1:8227/?k=dev#games" --js docs/games/dev/vohnyk-parity.js
// Друкує рядок на рівень: OK / MISMATCH, і підсумок «15/15».
if (!window.VohnykSim) {
  await new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = '/games/vohnyk-sim.js?cb=' + Date.now();
    s.onload = resolve;
    s.onerror = () => reject(new Error('vohnyk-sim.js не завантажився'));
    document.head.appendChild(s);
  });
}
const res = await fetch('/api/games/vohnyk/levels', { credentials: 'same-origin' });
if (!res.ok) return ['MISMATCH /api/games/vohnyk/levels → ' + res.status + ' (потрібна адмінська кука: відкрий ?k=dev)'];
const levels = await res.json();
const out = [];
let ok = 0;
for (const l of levels) {
  const t0 = performance.now();
  const c = l.check;
  const run = window.VohnykSim.replay(l, l.solution, c.every);
  const ms = (performance.now() - t0).toFixed(1);
  const want = c.hashes.map((h) => h >>> 0);
  let first = -1;
  for (let i = 0; i < Math.max(want.length, run.hashes.length); i++) if (want[i] !== run.hashes[i]) { first = i; break; }
  const same = first < 0 && run.clearedAt === c.steps && run.hash === (c.hash >>> 0);
  if (same) ok++;
  out.push((same ? 'OK' : 'MISMATCH') + ' рівень ' + l.n + ' «' + l.name + '»: ' + run.clearedAt + '/' + c.steps + ' кроків, '
    + l.solution.length + ' вводів' + (first >= 0 ? ', розійшлось до кроку ' + (first + 1) * c.every : '') + ' (' + ms + ' мс)');
}
out.push(ok + '/' + levels.length + ' рівнів збігаються з C#');
return out;
