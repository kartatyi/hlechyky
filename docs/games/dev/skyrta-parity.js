// Скирта: паритет C# ↔ JS (spec §4). Еталонні вектори docs/games/dev/skyrta-parity.json (пише тест
// SkyrtaTests.Parity_vectors_match…) проганяються через window.SkyrtaCore — той самий рушій, яким браузер веде свій
// сніп. Будь-яка розбіжність = тап у браузері ляже не туди, куди поклав сервер.
//
// Запуск (вектори підставляє обгортка):
//   python docs/games/dev/skyrta-parity.py --port 9613 --url "http://127.0.0.1:8413/?cb=1#games"
// Друкує OK / MISMATCH для кожної групи.
const V = window.__skyrtaParity;
if (!window.SkyrtaCore) {
  await new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = '/games/skyrta.js?cb=' + Date.now();
    s.onload = resolve;
    s.onerror = () => reject(new Error('skyrta.js не завантажився'));
    document.head.appendChild(s);
  });
}
const K = window.SkyrtaCore;
const out = [];
if (!V) return ['MISMATCH векторів нема — запускай через skyrta-parity.py'];
let bad = 0, first = '';
for (const [t, h, n, s0, wa, wb, c] of V.center) {
  const got = K.center(t, K.speed(h), K.fromLeft(n), K.sways(h), s0, wa < 0 ? [] : [wa, wb]);
  if (got !== c && !bad++) first = JSON.stringify([t, h, n, s0, wa, wb]) + ' C# ' + c + ', JS ' + got;
}
out.push((bad ? 'MISMATCH' : 'OK') + ' center: ' + V.center.length + ' векторів' + (bad ? ', розбіжностей ' + bad + ' (' + first + ')' : ''));
bad = 0; first = '';
for (const [pl, pw, l, v, streak, nl, nw, k] of V.land) {
  const got = K.land(pl, pw, l, v, streak);
  if ((got[0] !== nl || got[1] !== nw || got[2] !== k) && !bad++) first = JSON.stringify([pl, pw, l, v, streak]) + ' C# ' + [nl, nw, k] + ', JS ' + got;
}
out.push((bad ? 'MISMATCH' : 'OK') + ' land: ' + V.land.length + ' векторів' + (bad ? ', розбіжностей ' + bad + ' (' + first + ')' : ''));
return out;
