'use strict';
// Збори на гру: найближчі й минулі, хто йде (і хто бере ракетку в клубі), черга, «+ Збір», вписати друга чи гостя.
// З картки збору — у табло, турнір і розрахунок (вкладки C1 і «Гроші»). Сервер — §3.1 контракту.
(function () {
  const P = window.Padel, esc = P.esc;
  const HOURS = [0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4];
  let host = null, data = null, focus = '', pastOpen = false, reloadT = 0;

  const num = (h) => String(h).replace('.', ',');
  const hm = (iso) => new Intl.DateTimeFormat('uk-UA', { timeZone: 'Europe/Kyiv', hour: '2-digit', minute: '2-digit' }).format(new Date(iso));
  const kyivDay = (d) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Kyiv', year: 'numeric', month: '2-digit', day: '2-digit' }).format(d);
  const can = () => P.me.account;
  const mine = (g) => P.me.admin || (!!P.me.pid && g.byPid === P.me.pid);
  const goingMe = (g) => g.going.find((p) => p.pid === P.me.pid);
  const waitMe = (g) => g.wait.find((p) => p.pid === P.me.pid);
  const ended = (g) => new Date(g.until).getTime() <= Date.now();
  const find = (id) => data && (data.upcoming.find((x) => x.id === id) || data.recent.find((x) => x.id === id));

  async function load() {
    try { data = await P.api('gatherings'); } catch { data = data || { upcoming: [], recent: [] }; }
    render();
  }

  /// Подія `gathering` несе вид одного збору — кладемо на місце; новий чи той, що перейшов з найближчих у минулі, —
  /// перечитати список (рідко, тож не шкода).
  function upsert(g) {
    if (!data || !g || !g.id) return;
    const i = data.upcoming.findIndex((x) => x.id === g.id), j = data.recent.findIndex((x) => x.id === g.id);
    const up = g.status === 'open' && !ended(g);
    if (i >= 0 && up) data.upcoming[i] = g;
    else if (j >= 0 && !up) data.recent[j] = g;
    else { clearTimeout(reloadT); reloadT = setTimeout(load, 200); return; }
    render();
  }

  // ---------------------------------------------------------------- вигляд

  function render() {
    if (!host) return;
    const box = host.querySelector('.gt-list');
    if (!data) { box.innerHTML = '<div class="card empty">Гортаю календар…</div>'; return; }
    const up = data.upcoming.slice().sort((a, b) => a.start.localeCompare(b.start));
    const past = data.recent.slice().sort((a, b) => b.start.localeCompare(a.start));
    let html = '';
    if (!can()) html += '<div class="card gt-guest muted">Записуватись і збирати можуть акаунти — <a href="/">увійди на головній</a>. Дивитись можна й так.</div>';
    html += up.length ? up.map(card).join('')
      : '<div class="card empty"><span class="e">🗓️</span>Найближчих зборів нема. Сковорідка холоне, Глек нудьгує.'
        + (can() ? '<div style="margin-top:10px"><button class="btn pri" data-act="new">+ Зібрати на гру</button></div>' : '') + '</div>';
    if (past.length) {
      html += '<details class="gt-past"' + (pastOpen ? ' open' : '') + '><summary>Минулі й скасовані <span class="muted">· ' + past.length + '</span></summary>'
        + past.map(card).join('') + '</details>';
    }
    box.innerHTML = html;
    if (focus) {
      const el = box.querySelector('[data-gid="' + CSS.escape(focus) + '"]');
      if (el) { if (el.closest('details')) el.closest('details').open = true; el.classList.add('hl'); el.scrollIntoView({ block: 'center', behavior: 'smooth' }); }
      focus = '';
    }
  }

  function card(g) {
    const open = g.status === 'open', done = ended(g), live = open && !done && new Date(g.start).getTime() <= Date.now();
    const me = goingMe(g), queued = waitMe(g);
    const full = g.going.length >= g.slots;
    const free = Math.max(0, g.slots - g.going.length);
    let h = '<article class="card gt' + (open ? '' : ' off') + (done ? ' done' : '') + '" data-gid="' + esc(g.id) + '">';
    h += '<div class="gt-h"><div class="gt-t"><b class="gt-when">' + esc(P.when(g.start)) + '</b>'
      + (live ? '<span class="gt-tag live">грають</span>' : '') + (!open ? '<span class="gt-tag">скасовано</span>' : '')
      + '<div class="muted small">📍 ' + esc(g.place) + ' · ' + g.courts + ' ' + P.plural(g.courts, 'корт', 'корти', 'кортів')
      + ' · ' + num(g.hours) + ' год · до ' + hm(g.until) + '</div></div>'
      + '<div class="gt-cnt' + (full ? ' full' : '') + '"><b>' + g.going.length + '</b> з ' + g.slots + '</div></div>';
    if (g.note) h += '<div class="gt-note">' + esc(g.note) + '</div>';
    h += '<div class="gt-who">' + g.going.map((p) => goer(g, p)).join('')
      + (open && !done ? Array.from({ length: Math.min(free, 8) }, () => '<span class="gt-slot">вільно</span>').join('') + (free > 8 ? '<span class="gt-slot">+' + (free - 8) + '</span>' : '') : '')
      + '</div>';
    if (g.wait.length) h += '<div class="gt-wait"><span class="muted small">Черга:</span> ' + g.wait.map((p) => goer(g, p)).join('') + '</div>';
    const rackets = g.going.filter((p) => p.racket).length;
    h += '<div class="muted small gt-meta">Зібрав ' + esc(g.by) + (rackets ? ' · 🎾 ракеток у клубі: ' + rackets : '') + '</div>';

    // Записатись можна лише на відкритий і ще не скінчений; табло й турнір — поки збір живий; розрахунок — і після
    let act = '';
    if (can() && open && !done) {
      if (me) {
        act += '<button class="btn sm' + (me.racket ? ' on' : '') + '" data-act="racket" title="Ракетку береш у клубі (за гру)">🎾 ' + (me.racket ? 'беру ракетку ✓' : 'беру ракетку') + '</button>'
          + '<button class="btn sm ghost" data-act="leave">Не йду</button>';
      } else if (queued) {
        act += '<span class="muted small">Ти в черзі — №' + (g.wait.indexOf(queued) + 1) + '</span><button class="btn sm ghost" data-act="leave">З черги</button>';
      } else {
        act += '<button class="btn sm pri" data-act="join">✋ ' + (full ? 'У чергу' : 'Я йду') + '</button>';
      }
      act += '<button class="btn sm ghost" data-act="friend">+ друга / гостя</button>';
    }
    let go = '';
    if (can() && open && !done) go += '<button class="btn sm" data-act="match">▶ Матч</button><button class="btn sm" data-act="tour">🏆 Турнір</button>';
    if (can() && (open || g.expense)) go += '<button class="btn sm' + (g.expense ? ' paid' : '') + '" data-act="money">💸 ' + (g.expense ? 'Розрахунок ✓' : 'Розрахунок') + '</button>';
    if (mine(g) && open) go += '<button class="btn sm ghost" data-act="edit" title="Змінити або скасувати збір">✎ Змінити</button>';
    if (act) h += '<div class="row gt-act">' + act + '</div>';
    if (go) h += '<div class="row gt-go">' + go + '</div>';
    return h + '</article>';
  }

  /// Людина в списку. Чужих (гостя — будь-хто, бо вписав, мабуть, ти; інших — творець) можна торкнути: ракетка, виписати.
  function goer(g, p) {
    const touch = can() && g.status === 'open' && !ended(g) && (mine(g) || p.guest) && p.pid !== P.me.pid;
    return '<span class="gt-p' + (touch ? ' tap' : '') + '"' + (touch ? ' data-goer="' + esc(p.pid) + '" role="button" tabindex="0"' : '') + '>'
      + P.av(p.name) + '<span>' + esc(p.name) + '</span>' + (p.guest ? '<span class="g">гість</span>' : '')
      + (p.racket ? '<span class="gt-r" title="бере ракетку в клубі">🎾</span>' : '') + '</span>';
  }

  // ---------------------------------------------------------------- дії

  async function act(g, what, el) {
    const id = g.id;
    const run = async (path, body) => {
      el.disabled = true;
      try { const r = await P.api('gatherings/' + id + '/' + path, body || {}); if (r && r.gathering) upsert(r.gathering); return r; }
      catch { return null; } finally { el.disabled = false; }
    };
    switch (what) {
      case 'join': {
        const r = await run('join', {});
        if (r) P.toast(r.gathering && goingMe(r.gathering) ? '✋ Записав. Сковорідку гріємо!' : 'Місць нема — ти в черзі. Звільниться — скажу');
        break;
      }
      case 'leave': await run('leave', {}); break;
      case 'racket': { const me = goingMe(g); await run('racket', { racket: !(me && me.racket) }); break; }
      case 'friend': friend(g); break;
      case 'edit': form(g); break;
      case 'match': P.go('board', 'new:' + id); break;
      case 'tour': P.go('tour', 'new:' + id); break;
      case 'money': P.go('money', g.expense ? 'edit:' + g.expense : 'new:' + id); break;
    }
  }

  /// Вписати друга чи гостя: вибір кількох, потім по одному на сервер (понад місця — у черзу, це вже його справа).
  function friend(g) {
    const s = P.sheet('Вписати на ' + P.when(g.start), '<div class="gt-pick"></div><div class="row" style="margin-top:12px;justify-content:flex-end">'
      + '<button class="btn ghost" data-x>Скасувати</button><button class="btn pri" data-ok disabled>Вписати</button></div>');
    const taken = new Set([...g.going, ...g.wait].map((p) => p.pid));
    let chosen = [];
    const ok = s.el.querySelector('[data-ok]');
    P.pick(s.el.querySelector('.gt-pick'), {
      hint: 'Кого вписуємо? Гостя — впиши ім\'я в пошук і «+ гість».',
      onChange(v) { chosen = v.filter((x) => !taken.has(x)); ok.disabled = !chosen.length; ok.textContent = chosen.length > 1 ? 'Вписати ' + chosen.length : 'Вписати'; },
    });
    s.el.querySelector('[data-x]').onclick = () => s.close();
    ok.onclick = async () => {
      ok.disabled = true;
      for (const pid of chosen) {
        try { const r = await P.api('gatherings/' + g.id + '/join', { pid }); if (r.gathering) upsert(r.gathering); } catch { ok.disabled = false; return; }
      }
      P.toast(chosen.length > 1 ? 'Вписав ' + chosen.length + ' ' + P.plural(chosen.length, 'людину', 'людей', 'людей') : 'Вписав');
      s.close();
    };
  }

  function goerSheet(g, pid) {
    const p = [...g.going, ...g.wait].find((x) => x.pid === pid); if (!p) return;
    const inGoing = g.going.includes(p);
    const s = P.sheet(p.name, '<div class="stack">'
      + (inGoing ? '<button class="btn" data-r>🎾 ' + (p.racket ? 'Не бере ракетку' : 'Бере ракетку в клубі') + '</button>' : '<div class="muted">У черзі, №' + (g.wait.indexOf(p) + 1) + '</div>')
      + '<button class="btn bad" data-l>Виписати</button></div>');
    const call = async (path, body) => { try { const x = await P.api('gatherings/' + g.id + '/' + path, body); if (x.gathering) upsert(x.gathering); s.close(); } catch { /* тост уже був */ } };
    const r = s.el.querySelector('[data-r]');
    if (r) r.onclick = () => call('racket', { pid, racket: !p.racket });
    s.el.querySelector('[data-l]').onclick = () => call('leave', { pid });
  }

  /// Новий збір або правка: дата й час (київський), години, корти, місця, місце з підказками, примітка.
  function form(g) {
    const last = P.pref('gather.last') || {};
    const now = new Date();
    const kyivHour = +new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Kyiv', hour: '2-digit', hour12: false }).format(now);
    const v = g ? { date: g.local.slice(0, 10), time: g.local.slice(11, 16), hours: g.hours, courts: g.courts, slots: g.slots, place: g.place, note: g.note }
      : { date: kyivDay(kyivHour >= 17 ? new Date(now.getTime() + 864e5) : now), time: last.time || '18:00', hours: last.hours || 1.5,
        courts: last.courts || 1, slots: 4 * (last.courts || 1), place: last.place || '', note: '' };
    let slotsTouched = !!g && g.slots !== 4 * g.courts;
    const seg = (name, list, cur, fmt) => '<div class="seg" data-seg="' + name + '">' + list.map((x) => '<button type="button" data-v="' + x + '" class="' + (x === cur ? 'on' : '') + '">' + fmt(x) + '</button>').join('') + '</div>';
    const s = P.sheet(g ? 'Змінити збір' : 'Збір на падел', '<form class="stack gt-form">'
      + '<div class="gt-2"><div class="field"><label>Дата</label><input class="inp" type="date" name="date" required value="' + esc(v.date) + '"></div>'
      + '<div class="field"><label>Початок</label><input class="inp" type="time" name="time" step="900" required value="' + esc(v.time) + '"></div></div>'
      + '<div class="field"><label>Скільки годин</label>' + seg('hours', HOURS, v.hours, num) + '</div>'
      + '<div class="gt-2"><div class="field"><label>Кортів</label>' + seg('courts', [1, 2, 3, 4, 5, 6], v.courts, String) + '</div>'
      + '<div class="field gt-slots"><label>Місць</label><input class="inp" type="number" name="slots" min="2" max="48" inputmode="numeric" value="' + v.slots + '"></div></div>'
      + '<div class="field"><label>Де граємо</label><input class="inp" name="place" maxlength="60" required placeholder="Padel Club Позняки" value="' + esc(v.place) + '" autocomplete="off">'
      + '<div class="chips gt-places"></div></div>'
      + '<div class="field"><label>Примітка</label><input class="inp" name="note" maxlength="200" placeholder="корт №3, м\'ячі несе Влад" value="' + esc(v.note) + '"></div>'
      + '<div class="row gt-form-b">' + (g ? '<button type="button" class="btn ghost bad" data-cancel>Скасувати збір</button>' : '')
      + '<span class="pd-grow"></span><button class="btn pri" type="submit">' + (g ? 'Зберегти' : 'Зібрати') + '</button></div></form>');
    const f = s.el.querySelector('form');
    const state = { hours: v.hours, courts: v.courts };
    f.addEventListener('click', (e) => {
      const b = e.target.closest('[data-seg] button'); if (!b) return;
      const name = b.parentElement.dataset.seg;
      state[name] = +b.dataset.v;
      for (const x of b.parentElement.children) x.classList.toggle('on', x === b);
      if (name === 'courts' && !slotsTouched) f.slots.value = 4 * state.courts;
    });
    f.slots.addEventListener('input', () => { slotsTouched = true; });
    // Підказки — останні місця, де грали
    P.api('places').then((r) => {
      const list = (r && r.places) || [];
      s.el.querySelector('.gt-places').innerHTML = list.map((x) => '<button type="button" class="chip gt-place">' + esc(x) + '</button>').join('');
    }).catch(() => {});
    s.el.querySelector('.gt-places').addEventListener('click', (e) => { const b = e.target.closest('.gt-place'); if (b) f.place.value = b.textContent; });
    const cancel = s.el.querySelector('[data-cancel]');
    if (cancel) cancel.onclick = async () => {
      if (!confirm('Скасувати збір? Усім, хто йшов, прийде тост.')) return;
      try { const r = await P.api('gatherings/' + g.id + '/cancel', {}); if (r.gathering) upsert(r.gathering); P.toast('Збір скасовано'); s.close(); } catch { /* тост уже був */ }
    };
    f.addEventListener('submit', async (e) => {
      e.preventDefault();
      const body = { local: f.date.value + 'T' + f.time.value, hours: state.hours, courts: state.courts, slots: +f.slots.value || 4 * state.courts,
        place: f.place.value.trim(), note: f.note.value.trim() };
      const btn = f.querySelector('[type=submit]'); btn.disabled = true;
      try {
        const r = await P.api(g ? 'gatherings/' + g.id + '/edit' : 'gatherings', body);
        P.pref('gather.last', { time: f.time.value, hours: state.hours, courts: state.courts, place: body.place });
        if (r.gathering) { focus = r.gathering.id; if (find(r.gathering.id)) upsert(r.gathering); else load(); }
        P.toast(g ? 'Збір оновлено' : '🍳 Зібрав! Тепер клич друзів');
        s.close();
      } catch { btn.disabled = false; }
    });
  }

  Padel.tab({
    id: 'gather', icon: '🗓️', title: 'Збори', order: 3,
    mount(el) {
      host = el;
      el.innerHTML = '<div class="gt-top"><h2>🗓️ Збори</h2>' + (can() ? '<button class="btn pri" data-act="new">+ Збір</button>' : '') + '</div><div class="gt-list"></div>';
      el.addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (b) {
          if (b.dataset.act === 'new') { form(null); return; }
          const c = b.closest('[data-gid]'), g = c && find(c.dataset.gid);
          if (g) act(g, b.dataset.act, b);
          return;
        }
        const p = e.target.closest('[data-goer]');
        if (p) { const g = find(p.closest('[data-gid]').dataset.gid); if (g) goerSheet(g, p.dataset.goer); }
      });
      el.addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.target.matches('[data-goer]')) e.target.click(); });
      el.addEventListener('toggle', (e) => { if (e.target.classList && e.target.classList.contains('gt-past')) pastOpen = e.target.open; }, true);
      P.on('gathering', upsert);
      P.on('reconnected', load);
    },
    show(el, arg) {
      if (arg === 'new') { if (can()) form(null); }
      else if (arg) focus = arg;
      load();
    },
  });
})();
