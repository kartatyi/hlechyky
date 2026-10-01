'use strict';
// ЗАГЛУШКА каркаса: gather.js — збори на гру: хто йде, ракетки, черга.
// Заповнює агент клієнта за контрактом D:/or-wt/_tools/padel-contract.md.
Padel.tab({
  id: 'gather', icon: '🗓️', title: 'Збори', order: 3,
  mount(host) { host.innerHTML = '<h2>🗓️ Збори</h2><div class="card empty"><span class="e">🗓️</span>Тут скоро буде.</div>'; },
});
