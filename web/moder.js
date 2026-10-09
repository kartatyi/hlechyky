/*
  Модерація Балачок — window.HModer (09.10.2026). Сервер — ChatModeration.cs, методи хаба Mod*.

  Усім: подія хаба chatMod { pinned, slowSec, slowUntil, mediaOff, mediaUntil, limits: [{ kind, nick, until }] } —
  📌 плашка над Балачками, а кому заборонили писати (🔇 mute) чи кидати файли (🚫 media), той бачить це в полі вводу
  й на 📎. 🐢 повільний режим — підказкою в полі. Строк вийшов — перемальовуємо самі, без нової події.

  Адміну (me.role === 'admin'): 📌 і 🗑 на кожному повідомленні (кнопки домальовує app.js через msgActs()),
  у картці людини — 🔇 Заткнути, 🚫 Без файлів, 🧹 Почистити зі строком; вкладка «🛡 Модерація» в Бібліотеці —
  файли всім, повільний режим, закріплене, хто обмежений (з «Зняти») і журнал дій.

  app.js кличе init() на старті, attach(conn) після з'єднання, tab(chatTab) при зміні вкладки балачок, paint() — коли
  змінився «я» (нік, роль).
*/
(() => {
  'use strict';

  let o = null;                        // що дає app.js
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  let st = null;                       // останній chatMod
  let chatTab = 'chat';
  let timer = 0;
  let tabBox = null;                   // відкрита вкладка «🛡 Модерація»
  let slowPick = 30;                   // скільки секунд обрано для 🐢 у вкладці
  let defaultPh = '';                  // підказки полів і 📎 без обмежень — беремо з розмітки
  let fileTitle = '';

  const TABLE_PH = 'Тяпни щось за столом…';
  /// Строки для 🔇 і 🚫 людині: хвилини, 0 — поки не зняти.
  const SPANS = [[10, '10 хв'], [60, '1 год'], [1440, '1 доба'], [10080, 'тиждень'], [0, 'назавжди']];
  /// Строки для перемикачів на всі Балачки.
  const ALL_SPANS = [[10, '10 хв'], [60, '1 год'], [1440, '1 доба'], [0, 'поки не зніму']];
  /// 🧹: години назад, 0 — за весь час.
  const CLEAN = [[1, 'за годину'], [24, 'за добу'], [0, 'усе']];
  const SLOWS = [[10, '10 с'], [30, '30 с'], [60, '1 хв'], [300, '5 хв']];
  const KIND = { mute: '🔇 не пише', media: '🚫 без файлів' };

  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const isAdmin = () => !!o && o.me.role === 'admin';
  const live = (until) => !until || new Date(until).getTime() > Date.now();
  const gap = (s) => (s % 60 === 0 ? s / 60 + ' хв' : s + ' с');
  const $ = (id) => document.getElementById(id);

  /// «до 21:30», «до 12.10 21:30» або «— поки адмін не зніме».
  function until(t, forever) {
    if (!t) return '— ' + (forever || 'поки адмін не зніме');
    const d = new Date(t);
    const hm = d.toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' });
    return d.toDateString() === new Date().toDateString() ? 'до ' + hm
      : 'до ' + d.toLocaleDateString('uk-UA', { day: '2-digit', month: '2-digit' }) + ' ' + hm;
  }

  const limits = () => ((st && st.limits) || []).filter((l) => live(l.until));
  const limitOf = (nick, kind) => (nick ? limits().find((l) => l.kind === kind && same(l.nick, nick)) : null) || null;
  const slowOn = () => !!st && st.slowSec > 0 && live(st.slowUntil);
  const mediaOff = () => !!st && st.mediaOff && live(st.mediaUntil);

  /// Чому мені не можна писати (null — можна). Адміна це не стосується.
  function writeBlock() {
    if (isAdmin()) return null;
    const l = limitOf(o.me.nick, 'mute');
    return l ? '🔇 Адмін заборонив тобі писати ' + until(l.until, 'поки не зніме') : null;
  }

  /// Чому мені не можна кинути файл (null — можна).
  function mediaBlock() {
    if (isAdmin()) return null;
    const w = writeBlock();
    if (w) return w;
    const l = limitOf(o.me.nick, 'media');
    if (l) return '🚫 Адмін заборонив тобі кидати файли ' + until(l.until, 'поки не зніме');
    if (mediaOff()) return '🚫 Файли в Балачках вимкнено ' + until(st.mediaUntil, 'поки адмін не ввімкне');
    return null;
  }

  // =============================================================================================
  // Що бачать усі: поле вводу, 📎, 📌
  // =============================================================================================

  function paint() {
    if (!o) return;
    const w = writeBlock();
    // у полі — коротко (на телефоні воно вузьке), повністю — у підказці
    const mu = w && limitOf(o.me.nick, 'mute');
    const short = mu ? (mu.until ? '🔇 Писати можна ' + until(mu.until).replace(/^до /, 'з ') : '🔇 Адмін заборонив писати') : '';
    const slow = !w && slowOn() && !isAdmin() ? `🐢 Раз на ${gap(st.slowSec)} — повільний режим` : '';
    const inp = $('chatInput');
    if (inp) {
      inp.disabled = !!w;
      inp.placeholder = short || slow || defaultPh;
      inp.title = w || slow;
    }
    const form = $('chatForm');
    if (form) { form.classList.toggle('muted', !!w); form.title = w || ''; }
    const tin = o.tableInput ? o.tableInput() : null;   // балачка столу: живе поза сторінкою, поки стола нема
    if (tin) { tin.disabled = !!w; tin.placeholder = short || TABLE_PH; tin.title = w || ''; }
    const fb = $('fileBtn');
    if (fb) {
      const m = mediaBlock();
      fb.classList.toggle('off', !!m);
      fb.title = m || fileTitle;
    }
    paintPin();
    schedule();
  }

  function paintPin() {
    const bar = $('pinBar');
    if (!bar) return;
    const p = st && st.pinned;
    bar.hidden = !p || chatTab !== 'chat';
    if (!p) { bar.innerHTML = ''; return; }
    const text = p.text || (p.file ? '📎 ' + p.file.name : '');
    bar.dataset.id = String(p.id);
    bar.title = 'Закріплене — тисни, щоб знайти в Балачках';
    bar.innerHTML = '<span class="pb-ico">📌</span><span class="pb-t"><b>' + esc(p.nick) + '</b> ' + esc(text) + '</span>'
      + (isAdmin() ? '<button type="button" class="ghost icon pb-x" title="Відкріпити" aria-label="Відкріпити">✕</button>' : '');
  }

  /// Найближчий строк, що вийде, — тоді й перемалюємо (і поле вводу, і відкриту вкладку).
  function schedule() {
    clearTimeout(timer);
    if (!st) return;
    const ends = [st.slowUntil, st.mediaUntil, ...limits().map((l) => l.until)]
      .filter(Boolean).map((t) => new Date(t).getTime() - Date.now()).filter((ms) => ms > 0);
    if (!ends.length) return;
    timer = setTimeout(() => { paint(); if (tabOpen()) renderTab(tabBox); }, Math.min(Math.min(...ends) + 500, 2 ** 31 - 1));
  }

  function apply(state) {
    st = state || null;
    paint();
    if (tabOpen()) renderTab(tabBox);
  }

  function onPinClick(e) {
    if (e.target.closest('.pb-x')) { e.stopPropagation(); call(null, 'ModPin', 0); return; }
    const id = $('pinBar').dataset.id;
    const el = id && $('messages').querySelector(`.msg[data-id="${id}"]`);
    if (!el) { o.toast('Це повідомлення вже давнє — гортай Балачки вгору'); return; }
    el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    el.classList.remove('flash');
    void el.offsetWidth;
    el.classList.add('flash');
  }

  // =============================================================================================
  // Дії адміна
  // =============================================================================================

  /// Виклик хаба з відповіддю { ok, message }: помилку — червоним тостом, «прибрано 12» — звичайним.
  async function call(btn, method, ...args) {
    const c = o.conn();
    if (!c) return null;
    try {
      const run = () => c.invoke(method, ...args);
      const r = btn ? await o.busy(btn, '…', run) : await run();
      if (r && !r.ok) o.toast(r.message || 'Не вийшло', 'err');
      else if (r && r.message) o.toast(r.message);
      return r;
    } catch (e) {
      o.toast('Халепа: ' + e.message, 'err');
      return null;
    }
  }

  /// Кнопки адміна на повідомленні Балачок (поруч із ❤ і ↩).
  const msgActs = () => (isAdmin()
    ? '<button type="button" class="ghost" data-a="pin" title="📌 Закріпити вгорі Балачок">📌</button>'
      + '<button type="button" class="ghost" data-a="del" title="🗑 Прибрати повідомлення в усіх">🗑</button>'
    : '');

  /// Натиснули 📌 чи 🗑 на повідомленні <paramref name="el"/>.
  function act(a, el) {
    const id = +el.dataset.id;
    if (!id || !isAdmin()) return;
    if (a === 'pin') {
      const pinned = st && st.pinned && st.pinned.id === id;
      call(null, 'ModPin', pinned ? 0 : id);
    } else if (a === 'del') {
      if (!confirm(`Прибрати повідомлення «${el.dataset.nick || ''}» в усіх?`)) return;
      call(null, 'ModDelete', id);
    }
  }

  /// Блок адміна в картці людини й у профілі: 🔇, 🚫, 🧹 і вибір строку.
  function cardHtml(nick) {
    if (!isAdmin() || !nick || same(nick, o.me.nick)) return '';
    const mu = limitOf(nick, 'mute');
    const md = limitOf(nick, 'media');
    return '<div class="pc-mod"><div class="pm-row">'
      + (mu ? `<button type="button" data-ma="lift-mute" title="Зняти заборону писати (${esc(until(mu.until))})">🔊 Хай пише</button>`
        : '<button type="button" data-ma="mute" title="Заборонити писати на строк — у Балачках, за столом, присвяти">🔇 Заткнути</button>')
      + (md ? `<button type="button" data-ma="lift-media" title="Зняти заборону на файли (${esc(until(md.until))})">📎 Хай кидає файли</button>`
        : '<button type="button" data-ma="media" title="Заборонити кидати файли на строк">🚫 Без файлів</button>')
      + '<button type="button" data-ma="clean" title="Прибрати все, що людина написала в Балачках">🧹 Почистити</button>'
      + '</div><div class="pm-pick" hidden></div></div>';
  }

  function wireCard(box, nick, after) {
    const root = box.querySelector('.pc-mod');
    if (!root) return;
    const pick = root.querySelector('.pm-pick');
    root.querySelectorAll('[data-ma]').forEach((b) => b.onclick = async (e) => {
      e.stopPropagation();
      const a = b.dataset.ma;
      if (a === 'lift-mute' || a === 'lift-media') {
        const r = await call(b, 'ModLift', nick, a.slice(5));
        if (r && r.ok && after) after();
        return;
      }
      const opts = a === 'clean' ? CLEAN : SPANS;
      const label = a === 'mute' ? '🔇 Не пише' : a === 'media' ? '🚫 Без файлів' : '🧹 Прибрати';
      pick.hidden = false;
      pick.innerHTML = `<span class="muted small">${label}:</span>`
        + opts.map(([v, t]) => `<button type="button" class="chip" data-v="${v}">${t}</button>`).join('');
      pick.querySelectorAll('[data-v]').forEach((c) => c.onclick = async (ev) => {
        ev.stopPropagation();
        const v = +c.dataset.v;
        let r;
        if (a === 'clean') {
          if (!confirm(`Прибрати всі повідомлення «${nick}» ${c.textContent === 'усе' ? 'за весь час' : c.textContent}?`)) return;
          r = await call(c, 'ModClean', nick, v);
        } else r = await call(c, 'ModLimit', nick, a, v);
        if (r && r.ok && after) after();
      });
    });
  }

  // =============================================================================================
  // Вкладка «🛡 Модерація»
  // =============================================================================================

  const tabOpen = () => !!tabBox && tabBox.isConnected && !!tabBox.querySelector('.modtab');

  async function renderTab(box) {
    tabBox = box;
    if (!isAdmin()) { box.innerHTML = '<div class="empty">Це бачить лише адмін</div>'; return; }
    const c = o.conn();
    if (!c) { box.innerHTML = '<div class="empty">Ще не під\'єдналися — мить…</div>'; return; }
    let r;
    try { r = await c.invoke('ModOverview'); } catch (e) { box.innerHTML = `<div class="empty">Ой-йой: ${esc(e.message)}</div>`; return; }
    if (!r) { box.innerHTML = '<div class="empty">Це бачить лише адмін</div>'; return; }
    if (box !== tabBox) return;
    st = r.state;
    paint();
    const s = r.state;
    const chips = (list, attr) => list.map(([v, t]) => `<button type="button" class="chip" data-${attr}="${v}">${t}</button>`).join('');

    const media = mediaOff()
      ? `<span class="mt-now off">вимкнено ${esc(until(s.mediaUntil, 'поки не ввімкнеш'))}</span><button type="button" class="ghost" data-media="-1">📎 Увімкнути</button>`
      : `<span class="mt-now">можна</span><span class="mt-chips"><span class="muted small">вимкнути на</span>${chips(ALL_SPANS, 'media')}</span>`;
    const slow = slowOn()
      ? `<span class="mt-now off">раз на ${gap(s.slowSec)} ${esc(s.slowUntil ? until(s.slowUntil) : '— поки не вимкнеш')}</span><button type="button" class="ghost" data-slowoff="1">🐇 Вимкнути</button>`
      : `<span class="mt-now">вимкнено</span><span class="mt-chips"><span class="muted small">раз на</span>`
        + SLOWS.map(([v, t]) => `<button type="button" class="chip${v === slowPick ? ' on' : ''}" data-sec="${v}">${t}</button>`).join('')
        + `<span class="muted small">на</span>${chips(ALL_SPANS, 'slow')}</span>`;
    const p = s.pinned;
    const pin = p
      ? `<span class="mt-now"><b>${esc(p.nick)}</b> ${esc(p.text || (p.file ? '📎 ' + p.file.name : ''))}</span><button type="button" class="ghost" data-unpin="1">Відкріпити</button>`
      : '<span class="mt-now muted">нічого — тисни 📌 на будь-якому повідомленні</span>';

    const rows = (r.limits || []).filter((l) => live(l.until)).map((l) => `<li class="mt-lim">
        <span class="n who-n" data-who="${esc(l.nick)}">${esc(l.nick)}</span>
        <span class="chip">${KIND[l.kind] || esc(l.kind)}</span>
        <span class="muted small">${esc(until(l.until, 'поки не знімеш'))} · від ${esc(l.by)}, ${esc(o.dayTime(l.at))}${l.ip ? ' · і за адресою' : ''}</span>
        <button type="button" class="ghost" data-lift="${esc(l.kind)}" data-nick="${esc(l.nick)}">Зняти</button></li>`).join('');
    const log = (r.log || []).map((x) => `<li><span class="muted small">${esc(o.dayTime(x.at))}</span> <b>${esc(x.by)}</b> ${esc(x.text)}</li>`).join('');

    box.innerHTML = `<div class="modtab">
      <div class="mt-sec"><h3>Балачки зараз</h3>
        <div class="mt-row"><span class="mt-k">🚫 Файли всім</span>${media}</div>
        <div class="mt-row"><span class="mt-k">🐢 Повільний режим</span>${slow}</div>
        <div class="mt-row"><span class="mt-k">📌 Закріплене</span>${pin}</div>
      </div>
      <div class="mt-sec"><h3>Хто обмежений</h3>
        <p class="muted small">Обмежити — тисни на нік у Балачках чи «Хто онлайн». Або впиши нік тут (і того, кого зараз нема):</p>
        <form class="mt-form"><input type="text" maxlength="64" placeholder="нік" autocomplete="off">
          <select class="mt-kind"><option value="mute">🔇 не пише</option><option value="media">🚫 без файлів</option></select>
          <select class="mt-span">${SPANS.map(([v, t]) => `<option value="${v}"${v === 60 ? ' selected' : ''}>${t}</option>`).join('')}</select>
          <button type="submit" class="primary">Обмежити</button></form>
        <ul class="list mt-lims">${rows || '<li class="empty">Ніхто — у Балачках мир</li>'}</ul>
      </div>
      <div class="mt-sec"><h3>Журнал</h3><ul class="list mt-log">${log || '<li class="empty">Ще нічого не робили</li>'}</ul></div>
    </div>`;

    box.querySelectorAll('[data-media]').forEach((b) => b.onclick = (e) => call(e.currentTarget, 'ModMediaAll', +b.dataset.media));
    box.querySelectorAll('[data-sec]').forEach((b) => b.onclick = () => {
      slowPick = +b.dataset.sec;
      box.querySelectorAll('[data-sec]').forEach((x) => x.classList.toggle('on', x === b));
    });
    box.querySelectorAll('[data-slow]').forEach((b) => b.onclick = (e) => call(e.currentTarget, 'ModSlow', slowPick, +b.dataset.slow));
    box.querySelectorAll('[data-slowoff]').forEach((b) => b.onclick = (e) => call(e.currentTarget, 'ModSlow', 0, 0));
    box.querySelectorAll('[data-unpin]').forEach((b) => b.onclick = (e) => call(e.currentTarget, 'ModPin', 0));
    box.querySelectorAll('[data-lift]').forEach((b) => b.onclick = (e) => call(e.currentTarget, 'ModLift', b.dataset.nick, b.dataset.lift));
    const f = box.querySelector('.mt-form');
    f.onsubmit = async (e) => {
      e.preventDefault();
      const nick = f.querySelector('input').value.trim();
      if (!nick) return;
      const r2 = await call(f.querySelector('button'), 'ModLimit', nick, f.querySelector('.mt-kind').value, +f.querySelector('.mt-span').value);
      if (r2 && r2.ok && tabOpen()) renderTab(tabBox);
    };
  }

  // =============================================================================================
  // Підключення до app.js
  // =============================================================================================

  function init(opts) {
    o = opts;
    if (o.esc) esc = o.esc;
    defaultPh = ($('chatInput') && $('chatInput').placeholder) || 'Отут тяпати…';
    fileTitle = ($('fileBtn') && $('fileBtn').title) || '';
    const bar = $('pinBar');
    if (bar) bar.addEventListener('click', onPinClick);
  }

  function attach(conn) {
    conn.on('chatMod', apply);
  }

  function tab(t) {
    chatTab = t;
    paintPin();
  }

  window.HModer = {
    init, attach, tab, paint, msgActs, act, cardHtml, wireCard, renderTab, writeBlock, mediaBlock,
    /// Чи цей нік зараз мовчить (для картки й «Хто онлайн»).
    muted: (nick) => !!limitOf(nick, 'mute'),
  };
})();
