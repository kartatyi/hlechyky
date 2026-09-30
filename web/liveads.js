/*
  Жива реклама Дядька Глека — window.HLiveAds. Лише малює: правила, ціни, ліміти й черга — на сервері (LiveAds.cs).

  1) Картка «🔥 Прожарка в ефірі» — полиця Лавки #lavka/roast (lavka.js кличе mount(el)): кого прожарити, «анонімно»,
     кнопка з ціною, скільки замовлень лишилось на сьогодні, мої замовлення зі станом і перемикач «Мене не прожарювати».
     Сервер: GET /api/liveads, POST /api/liveads/order { target, anon }, POST /api/liveads/optout { off }.
  2) Блок «Жива реклама» у вкладці «📣 Реклама» (лише господар, app.js кличе admin(box)): увімкнено, частка живих,
     «Прожарити зараз», «Новини зараз», останні 20 роликів із ▶ (звичайний <audio preload="none">, сам не грає).
     Сервер: GET /api/liveads/admin, POST /api/liveads/admin/{enabled|share|roast|news}.

  Подій хабу в живої реклами нема: картка перечитує себе після кожної дії й поки видна — раз на 20 с, якщо є
  замовлення в дорозі (у черзі / скоро / в ефірі), інакше раз на хвилину. Вкладку реклами app.js і так перечитує
  на кожен новий трек.
*/
(() => {
  'use strict';

  let o = null;                        // що дає app.js: api, esc, toast, busy, me, askNick, onBalance
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();

  let card = null;                     // останнє GET /api/liveads
  let cardAt = 0;
  let pick = '';                       // кого вибрано (переживає перемальовування)
  let anon = false;
  let host = null;                     // елемент, куди змонтовано картку
  let timer = 0;

  const STAGE = {
    queued: ['⏳', 'у черзі', ''],
    soon: ['📻', 'скоро в ефірі', 'warn'],
    air: ['🔴', 'зараз в ефірі', 'err'],
    done: ['✓', 'зіграно', 'ok'],
    refunded: ['↩', 'не вийшло — черепки повернуто', ''],
  };
  const KIND = { roast: '🔥 прожарка', order: '🔥 замовлена', event: '⚡ подія', news: '📰 новини' };
  const STATUS = { queued: 'у черзі', ready: 'готовий', sent: 'в черзі ефіру', aired: 'зіграно', failed: 'не вийшов', refunded: 'повернуто', paying: 'оплата' };

  function when(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    const hm = String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
    return d.toDateString() === new Date().toDateString() ? hm : String(d.getDate()).padStart(2, '0') + '.' + String(d.getMonth() + 1).padStart(2, '0') + ' ' + hm;
  }
  const gen = (n) => (window.HLavka && window.HLavka.genitive ? window.HLavka.genitive(n) : n);
  const myNick = () => (o && o.me && o.me.nick) || '';

  // =============================================================================================
  // 🔥 Картка в Лавці
  // =============================================================================================

  async function loadCard() {
    card = await o.api('GET', '/api/liveads');
    cardAt = Date.now();
    return card;
  }

  /// Кого можна вибрати: сервер дає активних за 14 днів без відмови; себе — першим (можна прожарити й себе).
  function targetsOf(c) {
    const list = (c.targets || []).filter((n) => !same(n, myNick()));
    list.sort((a, b) => a.localeCompare(b, 'uk'));
    if (c.account && !c.optOut) list.unshift(myNick());
    return list;
  }

  function cardHtml(c) {
    const acc = c.account;
    const price = anon ? c.anonPrice : c.price;
    const targets = acc ? targetsOf(c) : [];
    if (!targets.some((n) => same(n, pick))) pick = targets.find((n) => !same(n, myNick())) || targets[0] || '';
    const short = price - (c.balance || 0);
    let btn;
    if (!c.enabled) btn = '<button disabled>Прожарки на перерві</button>';
    else if (!targets.length) btn = '<button disabled>Нема кого прожарити</button>';
    else if (c.left <= 0) btn = '<button disabled>На сьогодні все</button>';
    else if (short > 0) btn = '<button disabled title="Черепки капають за радіо, партії й щоденний глек">Бракує ' + short + ' 🏺</button>';
    else btn = '<button class="primary" type="button" data-la-order>🔥 Замовити за ' + price + ' 🏺</button>';
    const hint = !c.enabled ? 'Господар поставив прожарки на перерву — замовити зараз не вийде.'
      : short > 0 && c.left > 0 && targets.length ? 'Черепків бракує — вони капають за радіо, партії й щоденний глек.'
        : c.left <= 0 ? 'Ліміт — ' + c.dailyPerBuyer + ' на день. Завтра Глек знову до послуг.'
          : 'Не вийде ролик — черепки повернуться.';
    const form = !acc
      ? '<div class="lv-guest"><span>🔒 Замовити прожарку чи відмовитись від неї може лише акаунт — так Глек знає, від кого вона.</span>'
        + '<button class="primary" type="button" data-la-acc>Закріпити нік</button></div>'
      : '<div class="la-form">'
        + '<label class="la-field"><span class="muted small">Кого прожарити</span><select data-la-target' + (targets.length ? '' : ' disabled') + '>'
        + (targets.length ? targets.map((n) => '<option value="' + esc(n) + '"' + (same(n, pick) ? ' selected' : '') + '>'
          + esc(n) + (same(n, myNick()) ? ' (це ти)' : '') + '</option>').join('') : '<option>— нікого —</option>')
        + '</select></label>'
        + '<label class="la-check"><input type="checkbox" data-la-anon' + (anon ? ' checked' : '') + '> <span>анонімно — Глек не скаже, хто замовив <span class="muted small">(' + c.anonPrice + ' 🏺 замість ' + c.price + ')</span></span></label>'
        + '<div class="la-buy">' + btn + '<span class="muted small">сьогодні ще ' + c.left + ' з ' + c.dailyPerBuyer + '</span></div>'
        + '<div class="muted small">' + hint + '</div>'
        + '</div>';
    const orders = (c.orders || []).slice().sort((a, b) => (a.createdAt < b.createdAt ? 1 : -1)).slice(0, 5);
    const mine = acc && orders.length
      ? '<div class="la-mine"><div class="muted small">Мої замовлення</div><ul class="la-orders">' + orders.map((r) => {
        const s = STAGE[r.status] || ['•', r.status, ''];
        return '<li><span class="la-o-who">для <b>' + esc(gen(r.target)) + '</b>' + (r.anon ? ' <span class="muted small">· анонімно</span>' : '') + '</span>'
          + '<span class="chip ' + s[2] + '">' + s[0] + ' ' + esc(s[1]) + '</span>'
          + '<span class="muted small la-o-at">' + r.price + ' 🏺 · ' + when(r.createdAt) + '</span></li>';
      }).join('') + '</ul></div>'
      : '';
    const opt = acc
      ? '<label class="la-check la-opt"><input type="checkbox" data-la-optout' + (c.optOut ? ' checked' : '') + '> <span><b>Мене не прожарювати</b>'
        + '<span class="muted small"> — Глек не чіпатиме тебе ні в прожарках, ні в новинах, і замовити тебе не вийде.</span></span></label>'
      : '';
    return '<div class="la-card">'
      + '<div class="la-top"><span class="la-fire" aria-hidden="true">🔥</span><div><div class="lv-name">Прожарка в ефірі</div>'
      + '<div class="muted small">Дядько Глек прожарить обраного гравця його ж статистикою — в ефірі радіо, для всіх, хто слухає.</div></div></div>'
      + form + mine + opt + '</div>';
  }

  function paint() {
    if (!host || !host.isConnected) return;
    if (!card) { host.innerHTML = '<div class="gwait"><span class="spin"></span> розпалюю…</div>'; return; }
    host.innerHTML = cardHtml(card);
    const sel = host.querySelector('[data-la-target]');
    if (sel) sel.onchange = () => { pick = sel.value; };
    const an = host.querySelector('[data-la-anon]');
    if (an) an.onchange = () => { anon = an.checked; paint(); };
    const acc = host.querySelector('[data-la-acc]');
    if (acc) acc.onclick = () => o.askNick(true, 'register', String(myNick()).replace(/^гість\s*/i, ''));
    const buy = host.querySelector('[data-la-order]');
    if (buy) buy.onclick = (e) => order(e.currentTarget);
    const off = host.querySelector('[data-la-optout]');
    if (off) off.onchange = () => optOut(off);
  }

  async function refresh() {
    try { await loadCard(); } catch (e) { if (!card && host && host.isConnected) host.innerHTML = '<div class="gempty">Прожарка не відповідає: ' + esc(e.message) + '</div>'; return; }
    paint();
  }

  function tick() {
    clearTimeout(timer);
    if (!host || !host.isConnected) { host = null; return; }
    const moving = card && (card.orders || []).some((r) => r.status === 'queued' || r.status === 'soon' || r.status === 'air');
    timer = setTimeout(async () => {
      if (!host || !host.isConnected) { host = null; return; }
      if (document.visibilityState === 'visible') await refresh();
      tick();
    }, moving ? 20000 : 60000);
  }

  async function order(btn) {
    const target = pick;
    const price = anon ? card.anonPrice : card.price;
    const ask = window.HLavka && window.HLavka.ask;
    if (ask && !(await ask('🔥 Прожарка', 'Дядько Глек прожарить <b>' + esc(same(target, myNick()) ? 'тебе' : target) + '</b> в ефірі за <b>' + price + ' 🏺</b>'
      + (anon ? ', а хто замовив — не скаже' : ' і скаже, що це від тебе') + '. Не вийде ролик — черепки повернуться.', 'Замовити'))) return;
    await o.busy(btn, 'замовляю…', async () => {
      try {
        const r = await o.api('POST', '/api/liveads/order', { target, anon });
        o.toast(r.message || 'Є! Прожарка в черзі', 'ok');
        if (card && typeof r.balance === 'number') card.balance = r.balance;
      } catch (e) { o.toast(e.message, 'err'); }
    });
    await refresh();
    tick();
    if (o.onBalance) o.onBalance();
  }

  async function optOut(box) {
    const off = box.checked;
    box.disabled = true;
    try {
      const r = await o.api('POST', '/api/liveads/optout', { off });
      o.toast(r.message || (off ? 'Гаразд — Глек тебе не чіпає' : 'Глек знову може тебе прожарити'), 'ok');
    } catch (e) { box.checked = !off; o.toast(e.message, 'err'); }
    box.disabled = false;
    await refresh();
  }

  /// Змонтувати картку в el (полиця Лавки). Що вже знаємо — одразу, свіже — щойно прийде.
  function mount(el) {
    host = el;
    paint();
    if (!card || Date.now() - cardAt > 3000) refresh().then(tick); else tick();
  }

  // =============================================================================================
  // 📣 Вкладка «Реклама»: блок «Жива реклама» (лише господар)
  // =============================================================================================

  async function admin(box) {
    if (!box) return;
    let a, c;
    try {
      [a, c] = await Promise.all([o.api('GET', '/api/liveads/admin'), o.api('GET', '/api/liveads').catch(() => null)]);
    } catch (e) { box.innerHTML = '<div class="ads-head"><b>🔥 Жива реклама</b><span class="muted small">не відповідає: ' + esc(e.message) + '</span></div>'; return; }
    const share = Math.round((a.share || 0) * 100);
    const nicks = (c && c.targets) || [];
    const ready = (a.ready || []).map((r) => (KIND[r.kind] || r.kind) + (r.target ? ' · ' + r.target : '')).join(', ');
    const row = (r) => {
      const who = r.kind === 'news' ? 'усім' : esc(r.target || '—');
      const buyer = r.buyer ? (r.anon ? '🕶 таємно від ' : 'від ') + esc(r.buyer === 'господар' ? 'господаря' : r.buyer) : '';
      const st = STAGE[r.stage] && r.stage !== 'queued' ? STAGE[r.stage][0] + ' ' + STAGE[r.stage][1] : STATUS[r.status] || r.status;
      return '<li class="la-rec">'
        + '<div class="la-rec-head"><span class="chip">' + esc(KIND[r.kind] || r.kind) + '</span><b>' + who + '</b>'
        + (buyer ? '<span class="muted small">' + buyer + (r.price ? ' · ' + r.price + ' 🏺' : '') + '</span>' : '')
        + '<span class="chip ' + (r.status === 'failed' || r.status === 'refunded' ? 'err' : r.status === 'aired' ? 'ok' : '') + '">' + esc(st) + '</span>'
        + '<span class="muted small">' + when(r.playedAt || r.createdAt) + (r.seconds ? ' · ' + Math.round(r.seconds) + ' с' : '') + '</span></div>'
        + (r.text ? '<div class="la-text" title="Натисни — показати весь текст">' + esc(r.text) + '</div>' : '')
        + (r.facts ? '<div class="muted small">факти: ' + esc(Array.isArray(r.facts) ? r.facts.join(', ') : r.facts) + '</div>' : '')
        + (r.url ? '<audio controls preload="none" src="' + esc(r.url) + '"></audio>' : '')
        + '</li>';
    };
    box.innerHTML = '<div class="ads-head la-admin">'
      + '<div class="ads-row"><b>🔥 Жива реклама</b><span class="muted small">прожарки присутніх, замовлені прожарки, реакції на події й новини '
      + esc(a.news.from) + '–' + esc(a.news.to) + ' — Глек пише їх на льоту</span></div>'
      + (a.configEnabled ? '' : '<div class="muted small">⚠ вимкнено в LiveAds:Enabled — перемикач нижче не діє, поки не ввімкнеш у налаштуваннях</div>')
      + '<div class="ads-row"><label class="ads-switch la-on"><input type="checkbox" data-la-on' + (a.enabled ? ' checked' : '') + '> жива реклама ' + (a.enabled ? 'увімкнена' : 'вимкнена') + '</label></div>'
      + '<div class="ads-row la-share"><span>Частка живих прожарок</span><input type="range" min="0" max="100" step="5" value="' + share + '" data-la-share>'
      + '<b data-la-share-v>' + share + ' %</b><span class="muted small">решта слотів — бібліотека</span></div>'
      + '<div class="ads-row"><input type="text" list="laNicks" maxlength="40" placeholder="нік" autocomplete="off" data-la-nick value="' + esc(nicks[0] || '') + '">'
      + '<datalist id="laNicks">' + nicks.map((n) => '<option value="' + esc(n) + '">').join('') + '</datalist>'
      + '<button class="primary" type="button" data-la-roast>🔥 Прожарити зараз</button>'
      + '<button class="ghost" type="button" data-la-news>📰 Новини зараз</button></div>'
      + '<div class="muted small">Людину — не частіше ніж раз на ' + a.targetCooldownMinutes + ' хв. У запасі: ' + (ready ? esc(ready) : 'нічого') + '.</div>'
      + '</div>'
      + '<ul class="list la-recent">' + ((a.recent || []).map(row).join('') || '<li class="empty">Живих роликів ще не було.</li>') + '</ul>';

    const again = () => admin(box);
    const done = (r) => { o.toast(r.message, 'ok'); return again(); };
    const fail = (e) => o.toast(e.message, 'err');
    const on = box.querySelector('[data-la-on]');
    on.onchange = () => o.api('POST', '/api/liveads/admin/enabled', { enabled: on.checked }).then(done).catch((e) => { on.checked = !on.checked; fail(e); });
    const sh = box.querySelector('[data-la-share]');
    const shv = box.querySelector('[data-la-share-v]');
    sh.oninput = () => { shv.textContent = sh.value + ' %'; };
    sh.onchange = () => o.api('POST', '/api/liveads/admin/share', { share: +sh.value / 100 }).then((r) => o.toast(r.message, 'ok')).catch(fail);
    const nick = box.querySelector('[data-la-nick]');
    box.querySelector('[data-la-roast]').onclick = (e) => {
      const n = nick.value.trim();
      if (!n) { o.toast('Кого прожарити? Впиши нік', 'err'); return; }
      o.busy(e.currentTarget, 'печу…', () => o.api('POST', '/api/liveads/admin/roast', { nick: n }).then(done).catch(fail));
    };
    box.querySelector('[data-la-news]').onclick = (e) => o.busy(e.currentTarget, 'печу…', () => o.api('POST', '/api/liveads/admin/news').then(done).catch(fail));
    box.querySelectorAll('.la-text').forEach((t) => t.onclick = () => t.classList.toggle('open'));
  }

  window.HLiveAds = {
    init(opts) {
      o = opts;
      if (o.esc) esc = o.esc;
    },
    mount,
    admin,
  };
})();
