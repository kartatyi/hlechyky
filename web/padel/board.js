'use strict';
// ЗАГЛУШКА каркаса: board.js — табло: живі матчі, новий матч, рахунок, голос, годинник оренди.
// Заповнює агент клієнта за контрактом D:/or-wt/_tools/padel-contract.md.
Padel.tab({
  id: 'board', icon: '🎾', title: 'Табло', order: 1,
  mount(host) { host.innerHTML = '<h2>🎾 Табло</h2><div class="card empty"><span class="e">🎾</span>Тут скоро буде.</div>'; },
});
