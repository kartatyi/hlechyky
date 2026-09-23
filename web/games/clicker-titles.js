/*
  Звання округи Гончарного кола (docs/games/specs/clicker-titles.md). Частина ядра clicker.js.

  У вкладці «🤝 Село», під цехом:
  1) мої звання — чипи; натиск ставить звання біля ніка чи знімає (до трьох), «найрідкісніші самі» — скинути вибір;
  2) п'ять груп: 👑 перші в окрузі (хто тримає, з яким числом, скільки в мене), ⭐ звання дня (хто тримає з учора,
     хто веде сьогодні, моє число), 🏅 рідкісні (прогрес або ✓), 🙈 таємні (??? доки ніхто не вибив, далі — умова
     й хто перший), 🎖 пам'ятні; і пам'ятний глечик «Округа», якщо подарунок забрано.
  Значки — у вивісці над лічильником (st.titleIcons → api.sign); натиск на вивіску веде сюди.
  Нове звання — тост, іскри, рядок у стрічці (пам'ятаємо в localStorage, тож F5 не повторює).
  Дані: свій вид — view.titles, спільне — дошка з GET /api/games/clicker/guild (st.guildRoster.titles, тягне цех).
  Дія: titles { op: 'show', keys: [...] }.
*/
(() => {
  const human = HGames.ui.human;

  const KINDS = [
    { kind: 'top', title: '👑 Перші в окрузі', note: 'одне на всю округу: хто обжене — забирає собі' },
    { kind: 'day', title: '⭐ Звання дня', note: 'учорашні переможці тримають їх увесь сьогоднішній день' },
    { kind: 'rare', title: '🏅 Рідкісні', note: 'вибиває кожен, хто зможе, — і назавжди' },
    { kind: 'secret', title: '🙈 Таємні', note: 'умову видно, щойно хтось в окрузі вибив звання першим' },
    { kind: 'memory', title: '🎖 Пам\'ятні', note: 'лише для тих, хто грав до звань, — більше не вибити' },
  ];
  const OPEN_KEY = 'clk.titles.open';
  const SEEN_KEY = 'clk.titles.seen';

  const cat = (st) => (st.catalog && st.catalog.titles) || null;
  const board = (st) => (st.guildRoster && st.guildRoster.titles) || null;

  /// Звання з каталогу; таємне — з назвою, лише якщо воно моє (вид) або вже відкрите (дошка цеху).
  function def(st, key) {
    const c = cat(st);
    const t = c && c.list.find((x) => x.key === key);
    if (t && t.name) return t;
    const own = st.titles && st.titles.secrets && st.titles.secrets[key];
    const b = board(st);
    const open = b && b.secrets && b.secrets[key];
    const s = own || open;
    return s ? Object.assign({ key, kind: 'secret' }, s) : null;
  }

  const kyivTime = (ms) => new Date(ms).toLocaleTimeString('uk-UA', { timeZone: 'Europe/Kyiv', hour: '2-digit', minute: '2-digit' });
  const date = (iso) => {
    const d = Date.parse(iso);
    return d ? new Date(d).toLocaleDateString('uk-UA', { timeZone: 'Europe/Kyiv', day: 'numeric', month: 'long' }) : '';
  };

  /// Число звання для людини: клейма й кліки — коротко, ранковий клік — годинником, приріст — відсотками.
  function val(api, key, v) {
    if (!(v > 0)) return '';
    if (key === 'rooster') return kyivTime(v);
    if (key === 'rising') return '+' + api.num(v * 100) + ' %';
    return api.short(v);
  }

  function openSet() {
    try { return new Set(JSON.parse(HClicker.api.storeGet(OPEN_KEY, '["top"]'))); } catch (e) { return new Set(['top']); }
  }

  // ---------- малюнок ----------

  function mineHtml(st, api) {
    const v = st.titles;
    const esc = (x) => api.esc(st, x);
    const c = cat(st);
    const max = (c && c.badgesMax) || 3;
    const icons = v.badges.map((k) => (def(st, k) || {}).icon || '').join(' ');
    const chips = v.mine.map((k) => {
      const t = def(st, k);
      if (!t) return '';
      return '<button type="button" class="clkt-chip k-' + t.kind + (v.show.includes(k) ? ' on' : '') + '" data-tshow="' + esc(k) + '" title="'
        + esc(t.desc || '') + '">' + t.icon + ' ' + esc(t.name) + '</button>';
    }).join('');
    return '<div class="clkt-mine">'
      + '<div class="clkt-line"><span class="muted small">Біля ніка:</span> <b class="clkt-icons">' + (icons || '—') + '</b>'
      + (v.show.length ? ' <button type="button" class="ghost small clkt-reset">найрідкісніші самі</button>' : ' <span class="muted small">(найрідкісніші самі)</span>')
      + '</div>'
      + (v.mine.length
        ? '<div class="clkt-chips">' + chips + '</div><div class="muted small">Натисни звання, щоб поставити його біля ніка чи зняти — до ' + max + '.</div>'
        : '<div class="muted small">Поки жодного — але їх тут багато, і деякі зовсім поруч.</div>')
      + '</div>';
  }

  function rowHtml(st, api, t, who, extra, mine) {
    const esc = (x) => api.esc(st, x);
    return '<div class="clkt-row' + (mine ? ' mine' : '') + '"><span class="clkt-ico">' + t.icon + '</span>'
      + '<div class="clkt-txt"><b>' + esc(t.name) + '</b><span class="muted small">' + esc(t.desc || '') + '</span>' + (extra || '') + '</div>'
      + '<div class="clkt-who small">' + who + '</div></div>';
  }

  function bar(api, have, need) {
    const pct = need > 0 ? Math.min(100, Math.floor((have / need) * 100)) : 0;
    return '<div class="clkt-bar"><i style="width:' + pct + '%"></i></div><span class="muted small">' + api.short(have) + ' з ' + api.short(need) + '</span>';
  }

  function kindHtml(st, api, k) {
    const v = st.titles;
    const c = cat(st);
    const b = board(st);
    const esc = (x) => api.esc(st, x);
    const mine = new Set(v.mine);
    const list = c.list.filter((x) => x.kind === k.kind);
    let rows = '';
    let have = 0;
    let hidden = 0;
    for (const raw of list) {
      const key = raw.key;
      const own = mine.has(key);
      if (own) have++;
      const my = v.values[key] || 0;
      if (k.kind === 'top') {
        const h = b && b.tops && b.tops[key];
        const who = own ? '<b class="clkt-you">ти</b> · ' + val(api, key, my)
          : h ? esc(h.nick) + ' · ' + val(api, key, h.v) + (my > 0 ? '<br><span class="muted">у тебе ' + val(api, key, my) + '</span>' : '')
            : b ? '<span class="muted">ще нікому</span>' : '<span class="muted">…</span>';
        rows += rowHtml(st, api, raw, who, '', own);
      } else if (k.kind === 'day') {
        const h = b && b.days && b.days[key];
        const lead = b && b.lead && b.lead[key];
        const who = own ? '<b class="clkt-you">ти</b> (з учора)' : h ? esc(h.nick) + ' <span class="muted">(з учора)</span>' : '<span class="muted">ще нікому</span>';
        const today = lead
          ? '<span class="muted small">сьогодні веде: ' + (lead.nick && myIs(st, lead.nick) ? 'ти' : esc(lead.nick)) + ' · ' + val(api, key, lead.v)
            + (my > 0 && !(lead.nick && myIs(st, lead.nick)) ? ' · у тебе ' + val(api, key, my) : '') + '</span>'
          : my > 0 ? '<span class="muted small">у тебе сьогодні ' + val(api, key, my) + '</span>' : '';
        rows += rowHtml(st, api, raw, who, today, own);
      } else if (k.kind === 'rare') {
        const at = v.earned[key];
        const p = v.progress[key];
        const who = own ? '✓ <span class="muted">' + esc(date(at)) + '</span>' : first(st, api, key);
        const extra = !own && p && p[1] > 1 ? bar(api, p[0], p[1]) : '';
        rows += rowHtml(st, api, raw, who, extra, own);
      } else if (k.kind === 'secret') {
        const t = def(st, key);
        if (!t) { hidden++; continue; }
        const p = v.progress[key];
        const who = own ? '✓' + (v.earned[key] ? ' <span class="muted">' + esc(date(v.earned[key])) + '</span>' : '') : first(st, api, key);
        rows += rowHtml(st, api, t, who, !own && p && p[1] > 1 ? bar(api, p[0], p[1]) : '', own);
      } else {
        const who = own ? '✓' : '<span class="muted">—</span>';
        rows += rowHtml(st, api, raw, who, '', own);
      }
    }
    // Невідкриті таємні — одним рядком: десять однакових «???» нічого не кажуть, крім числа.
    if (hidden > 0) {
      rows += '<div class="clkt-row hidden"><span class="clkt-ico">🙈</span><div class="clkt-txt"><b>??? × ' + hidden + '</b>'
        + '<span class="muted small">' + (hidden === list.length ? 'Усі ' + hidden : 'Ще ' + hidden) + ' ' + api.plural(hidden, 'таємне', 'таємні', 'таємних')
        + ' — ніхто в окрузі їх ще не вибив. Хто перший — той і відкриє умову всім</span></div><div class="clkt-who small"></div></div>';
    }
    if (k.kind === 'memory' && v.gift) {
      rows += '<div class="clkt-row mine"><span class="clkt-ico">🏺</span><div class="clkt-txt"><b>Пам\'ятний глечик «Округа»</b>'
        + '<span class="muted small">Подарунок округи за оновлення зі званнями — стоїть на стіні звань у хаті</span></div><div class="clkt-who small">✓</div></div>';
    }
    const open = st.titlesOpen.has(k.kind) ? ' open' : '';
    return '<details class="clkt-sec" data-tk="' + k.kind + '"' + open + '><summary><b>' + k.title + '</b> <span class="muted small">· '
      + have + ' з ' + list.length + '</span></summary><div class="muted small clkt-note">' + k.note + '</div>' + rows + '</details>';
  }

  /// «першим — Микола» для рідкісного й відкритого таємного, якщо хтось уже вибив.
  function first(st, api, key) {
    const b = board(st);
    const f = b && b.firsts && b.firsts[key];
    return f ? '<span class="muted">першим — ' + api.esc(st, f.nick) + '</span>' : '';
  }

  const myIs = (st, nick) => {
    const me = (st.ctx && st.ctx.me && st.ctx.me.nick) || '';
    return me.trim().toLowerCase() === String(nick).trim().toLowerCase();
  };

  function paint(st, api) {
    st.titlesDirty = false;
    if (!st.titlesEl || !st.titles) return;
    const c = cat(st);
    if (!c) {
      api.swap(st.titlesEl, '<section class="clkg-card clkt"><div class="clk-sub">🎖 Звання округи</div><div class="muted small">Кличемо звання…</div></section>');
      return;
    }
    const html = '<section class="clkg-card clkt"><div class="clk-sub">🎖 Звання округи <span class="muted small">· у тебе '
      + st.titles.mine.length + '</span></div>' + mineHtml(st, api)
      + KINDS.map((k) => kindHtml(st, api, k)).join('') + '</section>';
    api.swap(st.titlesEl, html);
  }

  // ---------- дії ----------

  function setShow(st, api, keys) {
    api.act(st, 'titles', { op: 'show', keys }).then((r) => { if (r && r.ok) api.sfx('tap'); });
  }

  function onClick(st, api, e) {
    const chip = e.target.closest('[data-tshow]');
    if (chip) {
      if (!human(e) || !st.titles) return;
      const key = chip.dataset.tshow;
      const max = (cat(st) && cat(st).badgesMax) || 3;
      let show = st.titles.show.slice();
      if (show.includes(key)) show = show.filter((k) => k !== key);
      else if (show.length >= max) { api.toast(st, 'Біля ніка — до ' + max + ' значків: спершу зніми якийсь', 'warn'); return; }
      else show.push(key);
      setShow(st, api, show);
      return;
    }
    if (e.target.closest('.clkt-reset')) {
      if (human(e)) setShow(st, api, []);
    }
  }

  /// Нові звання — тост, іскри й рядок у стрічці. «Нові» — яких не було минулого разу (localStorage): перший вид після
  /// встановлення лише запам'ятовує, а звання, втрачене й повернене, вітаємо знову.
  function celebrate(st, api) {
    const v = st.titles;
    let seen = null;
    try { seen = JSON.parse(api.storeGet(SEEN_KEY, 'null')); } catch (e) { seen = null; }
    api.storeSet(SEEN_KEY, JSON.stringify(v.mine));
    if (!Array.isArray(seen)) return;
    const fresh = v.mine.filter((k) => !seen.includes(k));
    if (!fresh.length) return;
    const t = def(st, fresh[0]);
    if (!t) return;
    const text = t.icon + ' Нове звання: «' + t.name + '»' + (fresh.length > 1 ? ' і ще ' + (fresh.length - 1) : '');
    api.toast(st, text, 'ok');
    api.feed(st, text);
    api.sfx('rank-up');
    api.sparks(st, null, 20, true, 50, 40);
    api.popAt(st, t.icon, 'big', 50, 30);
  }

  function mountBlock(st) {
    if (st.titlesEl || !st.guildPane || !st.guildBody) return;
    st.titlesEl = document.createElement('div');
    st.titlesEl.className = 'clkt-box';
    st.guildBody.insertAdjacentElement('afterend', st.titlesEl);
    st.titlesEl.addEventListener('click', (e) => onClick(st, HClicker.api, e));
    // toggle не спливає — ловимо на спуску, щоб пам'ятати, які групи гравець тримає розгорнутими.
    st.titlesEl.addEventListener('toggle', (e) => {
      const d = e.target;
      if (!d || !d.dataset || !d.dataset.tk) return;
      if (d.open) st.titlesOpen.add(d.dataset.tk); else st.titlesOpen.delete(d.dataset.tk);
      HClicker.api.storeSet(OPEN_KEY, JSON.stringify([...st.titlesOpen]));
    }, true);
  }

  HClicker.part({
    id: 'titles',
    order: 65,

    mount(st, api) {
      st.titles = null;
      st.titlesEl = null;
      st.titlesDirty = false;
      st.titlesOpen = openSet();
      st.titleIcons = '';
      st.titlesGift = null;
      mountBlock(st);
      // Вивіска з значками веде до звань: у «Село» й одразу до розділу.
      if (st.sign) {
        st.sign.addEventListener('click', () => {
          if (!st.titles || !st.titles.mine.length || !st.panes || !st.panes.guild) return;
          api.showTab(st, 'guild');
          setTimeout(() => { if (st.titlesEl) st.titlesEl.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); }, 60);
        });
      }
    },

    update(st, v, api) {
      const t = v.titles;
      if (!t) return;
      st.titles = {
        mine: t.mine || [], earned: t.earned || {}, show: t.show || [], badges: t.badges || [], values: t.values || {},
        progress: t.progress || {}, secrets: t.secrets || {}, gift: !!t.gift,
      };
      mountBlock(st);
      if (st.mine) celebrate(st, api);
      // Подарунок щойно забрано (закрили «Що нового») — іскри над колом.
      if (st.titlesGift === false && st.titles.gift) {
        api.sparks(st, null, 28, true, 50, 45);
        api.popAt(st, '🎁', 'big', 50, 30);
        api.sfx('gift');
      }
      st.titlesGift = st.titles.gift;
      const icons = st.titles.badges.map((k) => (def(st, k) || {}).icon || '').join('');
      if (icons !== st.titleIcons) {
        st.titleIcons = icons;
        api.sign(st);
      }
      if (st.sign) st.sign.classList.toggle('clkt-link', st.titles.mine.length > 0);
      st.titlesDirty = true;
      if (st.tab === 'guild') paint(st, api);
    },

    slow(st, api) {
      // Дошка приходить разом зі списком цеху: новий список — перемалювати, навіть якщо виду не було.
      if (st.guildRoster !== st.titlesRoster) {
        st.titlesRoster = st.guildRoster;
        st.titlesDirty = true;
      }
      if (st.titlesDirty && st.tab === 'guild') paint(st, api);
    },

    unmount(st) {
      st.titlesEl = null;
    },
  });
})();
