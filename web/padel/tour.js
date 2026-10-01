'use strict';
// ЗАГЛУШКА каркаса: tour.js — турніри: американо, мексикано, мікст, король корту, пари, групи + плей-оф.
// Заповнює агент клієнта за контрактом D:/or-wt/_tools/padel-contract.md.
Padel.tab({
  id: 'tour', icon: '🏆', title: 'Турніри', order: 2,
  mount(host) { host.innerHTML = '<h2>🏆 Турніри</h2><div class="card empty"><span class="e">🏆</span>Тут скоро буде.</div>'; },
});
