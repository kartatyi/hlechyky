'use strict';
// Гроші: хто кому скільки. «Ти винен» (з банками кредитора — скопіювати картку, відкрити банку, «✓ Скинув»),
// «Тобі винні» («✓ Отримав»), увесь граф, витрата (форма з передзаповненням зі збору й живими частками), історія.
// Баланс загальний між людьми, як Splitwise (сервер — §3.2–3.3 контракту). Позначка на вкладці — скільки людей
// у розрахунку зі мною (і я винен, і мені винні).
(function () {
  const P = window.Padel, esc = P.esc;
  let host = null, data = null, loadT = 0, formOpen = null;

  // ---------------------------------------------------------------- банки (спільне з «Я»)

  const BANKS = [['mono', 'monobank'], ['privat', 'ПриватБанк'], ['pumb', 'ПУМБ'], ['abank', 'А-Банк'], ['sense', 'Sense Bank'], ['izi', 'izibank'], ['other', 'Інший банк']];
  /// Банки людини: назви, картка 4×4, копіювання, рядок банку. «Я» бере звідси ж (me.js вантажиться після).
  P.bank = {
    list: BANKS,
    name: (k) => (BANKS.find((b) => b[0] === k) || BANKS[BANKS.length - 1])[1],
    card: (d) => String(d || '').replace(/\D/g, '').replace(/(\d{4})(?=\d)/g, '$1 '),
    async copy(text, what) {
      try { await navigator.clipboard.writeText(text); }
      catch {
        // http без безпечного контексту — старим способом
        const t = document.createElement('textarea'); t.value = text; t.style.position = 'fixed'; t.style.opacity = '0';
        document.body.appendChild(t); t.select(); try { document.execCommand('copy'); } catch { /* ну й гаразд */ } t.remove();
      }
      P.toast('📋 ' + (what || 'Скопійовано'));
    },
    /// Рядок банку: позначка банку, назва, картка з «Скопіювати» і/або посилання на банку.
    row(b, opts) {
      const o = opts || {};
      return '<div class="mn-bank"><span class="mn-bk mn-' + esc(b.bank) + '">' + esc(P.bank.name(b.bank)) + '</span>'
        + (b.title ? '<span class="mn-btitle">' + esc(b.title) + '</span>' : '')
        + (b.card ? '<span class="mn-card num">' + esc(P.bank.card(b.card)) + '</span>'
          + (o.noCopy ? '' : '<button type="button" class="btn xs" data-copy="' + esc(b.card) + '">📋 Скопіювати</button>') : '')
        + (b.link ? '<a class="btn xs" href="' + esc(b.link) + '" target="_blank" rel="noopener noreferrer">🫙 Банка ↗</a>' : '')
        + (o.tail || '') + '</div>';
    },
  };

  // ---------------------------------------------------------------- дані

  /// Сирий fetch, а не P.api: гостю сайту /money — 403, і тост «нема прав» на кожному відкритті сторінки був би спамом.
  async function fetchMoney() {
    try {
      const r = await fetch('/api/padel/money', { credentials: 'same-origin' });
      if (r.status === 403 || r.status === 401) return { guest: true };
      return r.ok ? await r.json() : null;
    } catch { return null; }
  }

  async function refresh() {
    const d = await fetchMoney();
    if (d) data = d;
    P.badge('money', data && !data.guest ? (data.owe.length + data.owed.length) || '' : '');
    render();
    return data;
  }
  const soon = () => { clearTimeout(loadT); loadT = setTimeout(refresh, 300); };

  const names = () => {
    const m = {};
    if (data && !data.guest) for (const x of [...data.owe, ...data.owed]) m[x.pid] = x.name;
    return m;
  };
  const nm = (pid) => names()[pid] || P.name(pid);
  const isGuestPid = (pid) => String(pid).startsWith('g:');
  const whoP = (pid) => P.who({ pid, name: nm(pid), guest: isGuestPid(pid) });
  const day = (iso) => new Intl.DateTimeFormat('uk-UA', { timeZone: 'Europe/Kyiv', day: 'numeric', month: 'short' }).format(new Date(iso));
  const mayEdit = (e) => P.me.admin || (data && e.payer === data.me) || e.by === P.me.nick;

  // ---------------------------------------------------------------- вигляд

  function render() {
    if (!host) return;
    const box = host.querySelector('.mn-body');
    const top = host.querySelector('.mn-new');
    if (top) top.hidden = !(data && !data.guest);
    if (!data) { box.innerHTML = '<div class="card empty">Рахую копійки…</div>'; return; }
    if (data.guest) {
      box.innerHTML = '<div class="card empty"><span class="e">💸</span>Гроші бачать лише акаунти — <a href="/">увійди на головній</a>.<br>'
        + '<span class="small">Глек чужих гаманців не показує.</span></div>';
      return;
    }
    const oweSum = data.owe.reduce((s, x) => s + x.amount, 0), owedSum = data.owed.reduce((s, x) => s + x.amount, 0);
    let h = '<div class="card mn-sum">';
    if (!oweSum && !owedSum) h += '<div class="glek-say"><span class="glek-av">🏺</span><div><b>Усі квити.</b><div class="muted small">Глек задоволено булькає: ні ти нікому, ні тобі ніхто.</div></div></div>';
    else {
      h += '<div class="mn-tot"><span class="lbl">Ти винен</span><b class="' + (oweSum ? 'neg' : 'muted') + ' num">' + P.money(oweSum) + '</b></div>'
        + '<div class="mn-tot"><span class="lbl">Тобі винні</span><b class="' + (owedSum ? 'pos' : 'muted') + ' num">' + P.money(owedSum) + '</b></div>';
    }
    h += '</div>';

    h += '<div class="mn-cols"><section><h3>Ти винен</h3>' + (data.owe.length ? data.owe.map(oweCard).join('')
      : '<div class="card empty small">Нікому. Можна спати спокійно.</div>') + '</section>';
    h += '<section><h3>Тобі винні</h3>' + (data.owed.length ? data.owed.map(owedCard).join('')
      : '<div class="card empty small">Ніхто. Сковорідка чиста.</div>') + '</section></div>';

    if (data.all.length) {
      h += '<section><h3>Хто кому</h3><div class="card mn-graph">' + data.all.map((l) => {
        const me = l.from === data.me || l.to === data.me;
        return '<div class="mn-edge' + (me ? ' me' : '') + '">' + whoP(l.from) + '<span class="mn-arr">→</span>' + whoP(l.to)
          + '<b class="num">' + P.money(l.amount) + '</b></div>';
      }).join('') + '</div></section>';
    }
    h += '<section><h3>Історія</h3>' + historyHtml() + '</section>';
    box.innerHTML = h;
  }

  function oweCard(x) {
    const banks = (x.banks || []).map((b) => P.bank.row(b)).join('');
    return '<div class="card mn-owe" data-pid="' + esc(x.pid) + '"><div class="mn-h">' + P.who(x) + '<b class="mn-amt neg num">' + P.money(x.amount) + '</b></div>'
      + (banks ? '<div class="mn-banks">' + banks + '</div>'
        : '<div class="muted small">' + (x.guest ? 'Гість — розрахуйтесь при зустрічі.' : 'Банків не додав — спитай, куди скинути.') + '</div>')
      + '<div class="row"><button class="btn pri sm" data-act="paid">✓ Скинув</button></div></div>';
  }

  function owedCard(x) {
    return '<div class="card mn-owe" data-pid="' + esc(x.pid) + '"><div class="mn-h">' + P.who(x) + '<b class="mn-amt pos num">' + P.money(x.amount) + '</b></div>'
      + '<div class="row"><button class="btn sm" data-act="got">✓ Отримав</button></div></div>';
  }

  function historyHtml() {
    const items = [
      ...data.expenses.map((e) => ({ at: e.at, e })),
      ...data.payments.map((p) => ({ at: p.at, p })),
    ].sort((a, b) => String(b.at).localeCompare(String(a.at)));
    if (!items.length) return '<div class="card empty"><span class="e">🧾</span>Ще ніхто ні за що не платив. Перший корт — і тут з\'явиться розрахунок.</div>';
    return '<div class="card mn-hist">' + items.map((it) => {
      if (it.e) {
        const e = it.e, mine = e.shares[data.me];
        const what = ['корт'].concat(e.rackets.length ? ['ракетки'] : [], e.other.map((o) => o.title.toLowerCase())).join(', ');
        return '<button type="button" class="mn-it" data-exp="' + esc(e.id) + '"><span class="mn-ic">🧾</span><span class="mn-tx"><b>' + esc(day(e.date + 'T12:00:00Z')) + ' · ' + esc(what) + '</b>'
          + '<span class="muted small">платив ' + esc(nm(e.payer)) + ' · ' + e.people.length + ' ' + P.plural(e.people.length, 'людина', 'людини', 'людей')
          + (mine != null && e.payer !== data.me ? ' · твоя частка ' + P.money(mine) : '') + '</span></span><b class="num">' + P.money(e.total) + '</b></button>';
      }
      const p = it.p;
      const del = P.me.admin || p.by === P.me.nick;
      return '<div class="mn-it"><span class="mn-ic">💸</span><span class="mn-tx"><b>' + esc(nm(p.from)) + ' → ' + esc(nm(p.to)) + '</b>'
        + '<span class="muted small">' + esc(day(p.at)) + ' · записав ' + esc(p.by) + (p.note ? ' · ' + esc(p.note) : '') + '</span></span>'
        + '<b class="num pos">' + P.money(p.amount) + '</b>' + (del ? '<button type="button" class="btn xs ghost" data-unpay="' + esc(p.id) + '" title="Прибрати платіж">✕</button>' : '') + '</div>';
    }).join('') + '</div>';
  }

  // ---------------------------------------------------------------- платежі

  /// «✓ Скинув» (я → кредитор) чи «✓ Отримав» (боржник → я): сума за замовчуванням — увесь борг, можна менше.
  function pay(x, iPaid) {
    const title = iPaid ? 'Скинув — ' + x.name : 'Отримав від ' + x.name;
    const s = P.sheet(title, '<form class="stack">'
      + (iPaid && (x.banks || []).length ? '<div class="mn-banks">' + x.banks.map((b) => P.bank.row(b)).join('') + '</div>' : '')
      + '<div class="field"><label>Скільки, грн</label><input class="inp mn-big" type="number" name="amount" min="1" max="100000" inputmode="numeric" required value="' + x.amount + '">'
      + '<span class="muted small">Увесь борг — ' + P.money(x.amount) + '. Частину теж можна.</span></div>'
      + '<div class="field"><label>Примітка</label><input class="inp" name="note" maxlength="100" placeholder="' + (iPaid ? 'на моно' : 'готівкою на корті') + '"></div>'
      + '<div class="row" style="justify-content:flex-end"><button class="btn pri" type="submit">✓ Записати</button></div></form>');
    const f = s.el.querySelector('form');
    s.el.addEventListener('click', copyClick);
    f.addEventListener('submit', async (e) => {
      e.preventDefault();
      const amount = Math.round(+f.amount.value);
      const btn = f.querySelector('[type=submit]'); btn.disabled = true;
      try {
        await P.api('payments', { from: iPaid ? data.me : x.pid, to: iPaid ? x.pid : data.me, amount, note: f.note.value.trim() });
        P.toast(iPaid ? '💸 Записав: скинув ' + P.money(amount) : '💸 Записав: отримав ' + P.money(amount));
        s.close(); refresh();
      } catch { btn.disabled = false; }
    });
  }

  function copyClick(e) {
    const b = e.target.closest('[data-copy]');
    if (b) { e.preventDefault(); P.bank.copy(b.dataset.copy, 'Номер картки скопійовано'); }
  }

  // ---------------------------------------------------------------- витрата

  /// Перегляд без запису: сирий fetch, бо на кожну літеру тост «Кого ділимо?» — то вже знущання; помилка — під формою.
  async function preview(body) {
    try {
      const r = await fetch('/api/padel/expenses/preview', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      const d = await r.json().catch(() => null);
      return r.ok && d && d.ok !== false ? d : { error: (d && d.message) || 'Не порахувалось' };
    } catch { return { error: 'Нема зв\'язку з сервером' }; }
  }

  /// Форма витрати. e — наявна (правка), gid — збір для нової. Без збору — дата й люди руками.
  async function expenseForm(e, gid) {
    if (formOpen) formOpen.close();
    if (!data || data.guest) return;
    const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
    let st;
    if (e) st = { id: e.id, gathering: e.gathering, date: e.date, payer: e.payer, people: e.people.slice(), court: e.court, racketPrice: e.racketPrice,
      rackets: e.rackets.slice(), other: e.other.map((o) => ({ title: o.title, amount: o.amount, pids: o.pids ? o.pids.slice() : null })) };
    else {
      st = { id: null, gathering: gid || null, date: today, payer: data.me, people: [data.me], court: data.defaults.courtPerHour, racketPrice: data.defaults.racketPrice, rackets: [], other: [] };
      if (gid) {
        // Передзаповнення — сервер сам знає збір: хто йшов, хто з ракеткою, скільки годин і кортів
        const r = await preview({ gathering: gid });
        if (r.expense) { const x = r.expense; Object.assign(st, { date: x.date, payer: x.payer, people: x.people.slice(), court: x.court, racketPrice: x.racketPrice, rackets: x.rackets.slice() }); }
      }
    }
    const ro = e && !mayEdit(e);
    let gLabel = '';
    if (st.gathering) {
      try { const g = await P.api('gatherings/' + st.gathering); gLabel = P.when(g.start) + ' · ' + g.place; } catch { gLabel = st.gathering; }
    }
    const s = P.sheet(e ? (ro ? 'Розрахунок' : 'Правка розрахунку') : 'Новий розрахунок', '<form class="stack mn-form">'
      + (st.gathering ? '<div class="mn-gl">🗓️ Збір: <b>' + esc(gLabel) + '</b></div>'
        : '<div class="field"><label>Коли грали</label><input class="inp" type="date" name="date" value="' + esc(st.date) + '"></div>')
      + '<div class="field"><label>Хто платив</label><select class="inp" name="payer"></select></div>'
      + '<div class="mn-2"><div class="field"><label>Корт, грн</label><input class="inp" type="number" name="court" min="0" max="1000000" inputmode="numeric" value="' + st.court + '"></div>'
      + '<div class="field"><label>Ракетка, грн за гру</label><input class="inp" type="number" name="rp" min="0" max="10000" inputmode="numeric" value="' + st.racketPrice + '"></div></div>'
      + '<div class="field"><label>Хто ділить корт</label><div class="mn-people"></div></div>'
      + '<div class="field"><label>🎾 Хто брав ракетку</label><div class="chips mn-rk"></div></div>'
      + '<div class="field"><label>Інше (м\'ячі, вода…)</label><div class="mn-other"></div><button type="button" class="btn sm ghost" data-add style="align-self:flex-start">+ рядок</button></div>'
      + '<div class="mn-prev card"></div>'
      + (ro ? '<div class="muted small">Правити може той, хто записав, платник або адмін.</div>'
        : '<div class="row">' + (e ? '<button type="button" class="btn ghost bad" data-del>Видалити</button>' : '') + '<span class="pd-grow"></span><button class="btn pri" type="submit">' + (e ? 'Зберегти' : 'Записати') + '</button></div>')
      + '</form>', () => { formOpen = null; if (/^#money\//.test(location.hash)) history.replaceState(null, '', '#money'); });
    formOpen = s;
    const f = s.el.querySelector('form');
    if (ro) for (const x of f.querySelectorAll('input,select')) x.disabled = true;

    const known = () => {
      const k = P.players.known() || {};
      const list = [...st.people];
      for (const pid of [data.me, st.payer]) if (pid && !list.includes(pid)) list.push(pid);
      for (const x of [...(k.players || []), ...(k.accounts || [])]) if (!x.linkedTo && !list.includes(x.pid)) list.push(x.pid);
      return list;
    };
    const paintPayer = () => {
      f.payer.innerHTML = known().map((pid) => '<option value="' + esc(pid) + '"' + (pid === st.payer ? ' selected' : '') + '>' + esc(nm(pid)) + (pid === data.me ? ' (я)' : '') + '</option>').join('');
    };
    const paintRackets = () => {
      s.el.querySelector('.mn-rk').innerHTML = st.people.map((pid) => '<button type="button" class="chip' + (st.rackets.includes(pid) ? ' on' : '') + '" data-rk="' + esc(pid) + '">'
        + P.av(nm(pid)) + esc(nm(pid)) + (st.rackets.includes(pid) ? ' 🎾' : '') + '</button>').join('') || '<span class="muted small">Спершу обери людей</span>';
    };
    const paintOther = () => {
      s.el.querySelector('.mn-other').innerHTML = st.other.map((o, i) => '<div class="mn-oth" data-i="' + i + '">'
        + '<div class="mn-oth-r"><input class="inp" data-f="title" maxlength="40" placeholder="М\'ячі" value="' + esc(o.title) + '">'
        + '<input class="inp num" data-f="amount" type="number" min="1" max="100000" inputmode="numeric" placeholder="грн" value="' + (o.amount || '') + '">'
        + '<button type="button" class="btn xs ghost" data-rm title="Прибрати рядок">✕</button></div>'
        + '<div class="mn-oth-r"><div class="seg"><button type="button" data-all class="' + (o.pids ? '' : 'on') + '">на всіх</button><button type="button" data-some class="' + (o.pids ? 'on' : '') + '">вибраним</button></div></div>'
        + (o.pids ? '<div class="chips">' + st.people.map((pid) => '<button type="button" class="chip' + (o.pids.includes(pid) ? ' on' : '') + '" data-op="' + esc(pid) + '">' + P.av(nm(pid)) + esc(nm(pid)) + '</button>').join('') + '</div>' : '')
        + '</div>').join('');
      if (ro) for (const x of s.el.querySelectorAll('.mn-other input')) x.disabled = true;
    };
    const body = () => ({
      gathering: st.gathering || null, date: st.gathering ? null : st.date, payer: st.payer, people: st.people, court: st.court,
      racketPrice: st.racketPrice, rackets: st.rackets.filter((p) => st.people.includes(p)),
      other: st.other.filter((o) => o.amount > 0).map((o) => ({ title: o.title, amount: o.amount, pids: o.pids ? o.pids.filter((p) => st.people.includes(p)) : null })),
    });
    let prevT = 0, prevN = 0;
    const paintPrev = () => {
      clearTimeout(prevT);
      prevT = setTimeout(async () => {
        const n = ++prevN;
        const r = await preview(body());
        if (n !== prevN || !s.el.isConnected) return;
        const box = s.el.querySelector('.mn-prev');
        if (r.error) { box.innerHTML = '<div class="warn small">' + esc(r.error) + '</div>'; return; }
        const order = [...st.people, ...Object.keys(r.shares).filter((p) => !st.people.includes(p))];
        box.innerHTML = '<div class="mn-prev-h"><span class="lbl">Частки</span><b class="num">Разом ' + P.money(r.total) + '</b></div>'
          + order.filter((p) => r.shares[p] != null).map((p) => '<div class="mn-sh">' + whoP(p)
            + (p === st.payer ? '<span class="mn-payer">💳 платив</span>' : '') + '<b class="num">' + P.money(r.shares[p]) + '</b></div>').join('')
          + (st.people.includes(st.payer) ? '' : '<div class="muted small">' + esc(nm(st.payer)) + ' лише платив — сам не грав.</div>');
      }, 300);
    };
    const changed = () => { paintRackets(); paintOther(); paintPayer(); paintPrev(); };

    paintPayer(); paintRackets(); paintOther(); paintPrev();
    if (!ro) {
      await P.pick(s.el.querySelector('.mn-people'), { value: st.people, hint: 'Ділимо на', onChange(v) { st.people = v; changed(); } });
    } else {
      s.el.querySelector('.mn-people').innerHTML = '<div class="chips">' + st.people.map((p) => '<span class="chip">' + P.av(nm(p)) + esc(nm(p)) + '</span>').join('') + '</div>';
    }
    paintPayer();
    if (ro) { f.addEventListener('submit', (x) => x.preventDefault()); return; }

    f.payer.addEventListener('change', () => { st.payer = f.payer.value; paintPrev(); });
    if (f.date) f.date.addEventListener('change', () => { st.date = f.date.value; });
    f.court.addEventListener('input', () => { st.court = Math.max(0, Math.round(+f.court.value || 0)); paintPrev(); });
    f.rp.addEventListener('input', () => { st.racketPrice = Math.max(0, Math.round(+f.rp.value || 0)); paintPrev(); });
    s.el.querySelector('.mn-rk').addEventListener('click', (x) => {
      const b = x.target.closest('[data-rk]'); if (!b) return;
      const pid = b.dataset.rk, i = st.rackets.indexOf(pid);
      if (i >= 0) st.rackets.splice(i, 1); else st.rackets.push(pid);
      paintRackets(); paintPrev();
    });
    s.el.querySelector('[data-add]').addEventListener('click', () => { st.other.push({ title: '', amount: 0, pids: null }); paintOther(); const r = s.el.querySelectorAll('.mn-oth [data-f=title]'); if (r.length) r[r.length - 1].focus(); });
    const oth = s.el.querySelector('.mn-other');
    oth.addEventListener('input', (x) => {
      const inp = x.target.closest('[data-f]'); if (!inp) return;
      const o = st.other[+inp.closest('[data-i]').dataset.i];
      if (inp.dataset.f === 'title') o.title = inp.value; else o.amount = Math.max(0, Math.round(+inp.value || 0));
      paintPrev();
    });
    oth.addEventListener('click', (x) => {
      const row = x.target.closest('[data-i]'); if (!row) return;
      const o = st.other[+row.dataset.i];
      if (x.target.closest('[data-rm]')) st.other.splice(+row.dataset.i, 1);
      else if (x.target.closest('[data-all]')) o.pids = null;
      else if (x.target.closest('[data-some]')) o.pids = o.pids || st.people.slice();
      else if (x.target.closest('[data-op]')) {
        const pid = x.target.closest('[data-op]').dataset.op, i = o.pids.indexOf(pid);
        if (i >= 0) o.pids.splice(i, 1); else o.pids.push(pid);
      } else return;
      paintOther(); paintPrev();
    });
    const del = s.el.querySelector('[data-del]');
    if (del) del.addEventListener('click', async () => {
      if (!confirm('Видалити розрахунок? Борги за ним зникнуть із балансу.')) return;
      try { await P.api('expenses/' + e.id, undefined, 'DELETE'); P.toast('Розрахунок видалено'); s.close(); refresh(); } catch { /* тост уже був */ }
    });
    f.addEventListener('submit', async (x) => {
      x.preventDefault();
      const btn = f.querySelector('[type=submit]'); btn.disabled = true;
      try {
        if (e) await P.api('expenses/' + e.id, body(), 'PUT'); else await P.api('expenses', body());
        P.toast(e ? 'Розрахунок оновлено' : '🧾 Записав. Тепер кожен бачить, скільки винен');
        s.close(); refresh();
      } catch { btn.disabled = false; }
    });
  }

  /// #money/new, #money/new:g5, #money/edit:e7 — відкрити форму, щойно є дані.
  async function openArg(arg) {
    if (!arg) return;
    if (!data || data.guest) await refresh();
    if (!data || data.guest) return;
    const [kind, id] = arg.split(':');
    if (kind === 'new') {
      const has = id && data.expenses.find((x) => x.gathering === id);
      expenseForm(has || null, has ? null : id || null);
    } else if (kind === 'edit') {
      const e = data.expenses.find((x) => x.id === id);
      if (e) expenseForm(e); else P.toast('Того розрахунку вже нема в останніх');
    }
  }

  Padel.tab({
    id: 'money', icon: '💸', title: 'Гроші', order: 4,
    mount(el) {
      host = el;
      el.innerHTML = '<div class="mn-top"><h2>💸 Гроші</h2><button class="btn pri mn-new" data-act="new" hidden>+ Витрата</button></div><div class="mn-body"></div>';
      el.addEventListener('click', (e) => {
        if (e.target.closest('[data-copy]')) { copyClick(e); return; }
        const a = e.target.closest('[data-act]');
        if (a) {
          if (a.dataset.act === 'new') { expenseForm(null, null); return; }
          const pid = a.closest('[data-pid]') && a.closest('[data-pid]').dataset.pid;
          const list = a.dataset.act === 'paid' ? data.owe : data.owed;
          const x = list.find((y) => y.pid === pid);
          if (x) pay(x, a.dataset.act === 'paid');
          return;
        }
        const ex = e.target.closest('[data-exp]');
        if (ex) { const x = data.expenses.find((y) => y.id === ex.dataset.exp); if (x) expenseForm(x); return; }
        const un = e.target.closest('[data-unpay]');
        if (un) {
          if (!confirm('Прибрати цей платіж? Борг повернеться.')) return;
          P.api('payments/' + un.dataset.unpay, undefined, 'DELETE').then(() => { P.toast('Платіж прибрано'); refresh(); }).catch(() => {});
        }
      });
      render();
    },
    show(el, arg) { refresh().then(() => openArg(arg)); },
  });

  P.on('money', soon);
  P.on('reconnected', soon);
  // Позначка на вкладці — одразу, навіть якщо в «Гроші» не заходили
  refresh();
})();
