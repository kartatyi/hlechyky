'use strict';
// ЗАГЛУШКА каркаса: money.js — хто кому скільки: витрати за корт і ракетки, загальний баланс.
// Заповнює агент клієнта за контрактом D:/or-wt/_tools/padel-contract.md.
Padel.tab({
  id: 'money', icon: '💸', title: 'Гроші', order: 4,
  mount(host) { host.innerHTML = '<h2>💸 Гроші</h2><div class="card empty"><span class="e">💸</span>Тут скоро буде.</div>'; },
});
