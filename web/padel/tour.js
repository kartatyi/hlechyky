'use strict';
// Турніри Падельні: живі й минулі, створення (шість форматів, гравці чи пари, ♀ для міксту, корти, матч до/на час,
// оренда → порада сервера /plan), раунди вкладками з кортами й рахунком, «▶ вести на табло», таблиця зі стрілками,
// групи й сітка плей-оф, п'єдестал і нагороди, «📺 Таблиця на екран». Пари, таблицю, «середнє» й нагороди рахує
// сервер (PadelTours/PadelTourGen) — тут лише показуємо його вид і вносимо рахунок.
(function () {
  const P = window.Padel, esc = P.esc, plural = P.plural;
  const FORMATS = [
    ['americano', '🔄', 'Американо', 'Щораунду новий напарник. Кожен рахує свої очки.'],
    ['mexicano', '🌶️', 'Мексикано', 'Перший раунд навмання, далі пари за таблицею: сильні грають із сильними.'],
    ['mixed', '💃', 'Мікст', 'Як американо, але кожна пара — дівчина й хлопець.'],
    ['king', '👑', 'Король корту', 'Переможці йдуть на корт вище, переможені — нижче.'],
    ['team', '👯', 'Командний', 'Фіксовані пари, кожна грає з кожною.'],
    ['groups', '🏟️', 'Групи й плей-оф', 'Фіксовані пари: коло в групах, далі півфінали й фінал.'],
  ];
  const FMT = Object.fromEntries(FORMATS.map((f) => [f[0], f]));
  const STAGE = { semi: '½ фіналу', final: 'Фінал', third: 'За 3-тє', group: null };
  const pairFmt = (f) => f === 'team' || f === 'groups';
  const fmtH = (h) => String(h).replace('.', ',') + ' год';
  const dur = (min) => P.span(min * 60000);

  let host = null, mode = 'list', curId = null, cur = null, list = null, pending = false;
  const selR = {};             // турнір → вибраний раунд (індекс)
  let F = null;                // форма нового турніру
  let planT = 0, planSeq = 0, listT = 0;

  const $ = (s) => host && host.querySelector(s);
  const visible = () => !!(P.current && P.current.tab && P.current.tab.id === 'tour');
  const isOrg = (t) => P.me.admin || (!!P.me.pid && (t.ctl || []).includes(P.me.pid));
  const scored = (m) => m.sa != null && m.sb != null;
  const ukey = (u) => (u || []).join('|');

  // ------------------------------------------------------------------ список

  async function loadList() {
    try { list = await P.api('tournaments'); } catch { list = list || { live: [], recent: [] }; }
    P.badge('tour', (list.live || []).length || '');
    if (mode === 'list' && host && visible()) renderList();
  }
  function cardHtml(s) {
    const f = FMT[s.format] || ['', '🏆', 'Турнір'];
    return '<button type="button" class="tr-card' + (s.status === 'live' ? ' live' : '') + '" data-tid="' + esc(s.id) + '"><span class="e">' + f[1] + '</span><div>'
      + '<b>' + esc(s.title) + '</b><span class="muted">' + esc(f[2]) + ' · ' + s.players + ' ' + plural(s.players, 'гравець', 'гравці', 'гравців')
      + ' · ' + esc(P.when(s.createdAt, { time: false })) + (s.leader ? ' · ' + (s.status === 'done' ? '🥇 ' : 'веде ') + esc(s.leader) : '') + '</span></div></button>';
  }
  function renderList() {
    const l = list || { live: [], recent: [] };
    host.innerHTML = '<div class="tr-head"><h2>🏆 Турніри</h2><button type="button" class="btn pri" data-act="new">+ Турнір</button></div>'
      + '<div class="tr-sec"><h3>Зараз</h3>' + ((l.live || []).length ? '<div class="tr-list">' + l.live.map(cardHtml).join('') + '</div>'
        : '<div class="card empty"><span class="e">🏆</span>Живих турнірів нема. Глек уже намалював сітку на серветці — бракує лише вас.</div>') + '</div>'
      + ((l.recent || []).length ? '<div class="tr-sec"><h3>Минулі</h3><div class="tr-list">' + l.recent.map(cardHtml).join('') + '</div></div>' : '');
  }

  // ------------------------------------------------------------------ створення

  function seg(key, opts, val) {
    return '<div class="seg" data-k="' + key + '">' + opts.map(([v, l]) => '<button type="button" data-v="' + esc(v) + '" class="'
      + (String(v) === String(val) ? 'on' : '') + '">' + esc(l) + '</button>').join('') + '</div>';
  }

  async function openNew(gid) {
    if (!P.me.account && !P.me.admin) { P.toast('Турнір заводять лише акаунти — увійди на головній'); P.go('tour'); return; }
    mode = 'new';
    F = { fmt: 'americano', players: [], women: [], courts: 2, total: '24', minutes: 12, booking: 1.5, rounds: 6, manual: false, plan: null, gathering: '', gath: [] };
    host.innerHTML = '<div class="card empty">Розкладаю сітку на серветці…</div>';
    try { F.gath = (await P.api('gatherings')).upcoming || []; } catch { F.gath = []; }
    if (gid) {
      let g = null; try { g = await P.api('gatherings/' + encodeURIComponent(gid)); } catch { g = null; }
      if (g) {
        F.gathering = g.id; F.players = (g.going || []).map((x) => x.pid);
        F.courts = Math.min(6, Math.max(1, g.courts || 1)); F.booking = g.hours || 1.5;
        if (!F.gath.some((x) => x.id === g.id)) F.gath.unshift(g);
      }
    }
    if (mode !== 'new') return;
    renderNew();
  }

  function renderNew() {
    const fmts = FORMATS.map(([v, e, t, d]) => '<button type="button" class="fmt' + (F.fmt === v ? ' on' : '') + '" data-fmt="' + v + '"><span class="e">' + e + '</span><b>'
      + esc(t) + '</b><small>' + esc(d) + '</small></button>').join('');
    const gOpt = (x) => '<option value="' + esc(x.id) + '"' + (F.gathering === x.id ? ' selected' : '') + '>' + esc(P.when(x.start) + ' · ' + x.place) + '</option>';
    host.innerHTML = '<div class="tr-head"><h2>🏆 Новий турнір</h2><a class="btn sm ghost" href="#tour">← Турніри</a></div>'
      + '<div class="card tset">'
      + '<div class="field"><label>Формат</label><div class="fmt-grid">' + fmts + '</div></div>'
      + '<div class="field"><label>Хто грає <span class="cnt" id="tnCnt"></span></label><div id="tnPick"></div><div id="tnExtra"></div></div>'
      + '<div class="row3">'
      + '<div class="field"><label>Кортів</label>' + seg('courts', [[1, '1'], [2, '2'], [3, '3'], [4, '4'], [5, '5'], [6, '6']], F.courts) + '</div>'
      + '<div class="field"><label>Матч</label>' + seg('total', [['16', 'до 16'], ['21', 'до 21'], ['24', 'до 24'], ['32', 'до 32'], ['time', '⏱ на час']], F.total) + '</div>'
      + '<div class="field" id="tnMinF"><label>Хвилин на матч</label><div class="stepper"><button type="button" class="btn sm" data-min="-1">−</button><b id="tnMin"></b>'
      + '<button type="button" class="btn sm" data-min="1">+</button></div></div>'
      + '<div class="field"><label>Корт заброньовано на</label>' + seg('booking', [[1, '1 год'], [1.5, '1,5 год'], [2, '2 год'], [3, '3 год']], F.booking) + '</div>'
      + '<div class="field" id="tnRdF"><label>Раундів</label><div class="stepper"><button type="button" class="btn sm" data-rd="-1">−</button><b id="tnRd"></b>'
      + '<button type="button" class="btn sm" data-rd="1">+</button><span id="tnRdAuto"></span></div></div>'
      + '</div>'
      + '<div class="row3"><div class="field" style="flex:1;min-width:200px"><label>Назва</label><input class="inp" id="tnTitle" maxlength="60" placeholder="сама складеться: «'
      + esc((FMT[F.fmt] || [])[2] || 'Турнір') + ' …»"></div>'
      + '<div class="field"><label>Збір</label><select class="inp" id="tnG"><option value="">без збору</option>' + F.gath.map(gOpt).join('') + '</select></div></div>'
      + '<div class="tsum" id="tnSum"></div>'
      + '<div class="tgo"><button type="button" class="btn pri big" id="tnGo">Почати турнір →</button></div>'
      + '</div>';
    mountPick();
    paintNew();
  }

  async function mountPick() {
    const pair = pairFmt(F.fmt);
    await P.pick($('#tnPick'), {
      value: F.players, numbered: pair,
      hint: pair ? 'Пари — за порядком вибору: 1 + 2, 3 + 4…' : F.fmt === 'mixed' ? 'Обери всіх, а нижче познач дівчат ♀.' : 'Тицяй усіх, хто грає (від 4).',
      onChange: (v) => { F.players = v; F.women = F.women.filter((p) => v.includes(p)); paintNew(); },
    });
  }

  function units() { return pairFmt(F.fmt) ? Math.floor(F.players.length / 2) : F.players.length; }

  function paintNew() {
    if (mode !== 'new' || !$('#tnSum')) return;
    const pair = pairFmt(F.fmt), n = units();
    $('#tnCnt').textContent = pair ? n + ' ' + plural(n, 'пара', 'пари', 'пар') : F.players.length;
    let extra = '';
    if (F.fmt === 'mixed' && F.players.length) {
      extra = '<div class="lbl" style="margin:10px 0 6px">♀ Хто з них дівчата</div><div class="chips tr-women">' + F.players.map((p) => '<button type="button" class="chip'
        + (F.women.includes(p) ? ' on' : '') + '" data-w="' + esc(p) + '">' + P.av(P.name(p)) + esc(P.name(p)) + (F.women.includes(p) ? ' ♀' : '') + '</button>').join('') + '</div>'
        + '<div class="muted small" style="margin-top:4px">' + F.women.length + ' ♀ · ' + (F.players.length - F.women.length) + ' ♂ — зайві відпочивають по черзі</div>';
    } else if (pair && F.players.length) {
      const ps = [];
      for (let i = 0; i + 1 < F.players.length; i += 2) ps.push('<span class="tr-pair"><span class="n">' + (i / 2 + 1) + '.</span>' + esc(P.name(F.players[i])) + ' + ' + esc(P.name(F.players[i + 1])) + '</span>');
      extra = '<div class="tr-pairs">' + ps.join('') + (F.players.length % 2 ? '<span class="tr-pair warn">' + esc(P.name(F.players[F.players.length - 1])) + ' — без пари</span>' : '') + '</div>';
    }
    $('#tnExtra').innerHTML = extra;
    $('#tnMinF').hidden = F.total !== 'time';
    $('#tnMin').textContent = F.minutes;
    $('#tnRdF').hidden = F.fmt === 'groups';
    $('#tnRd').textContent = F.rounds;
    const p = F.plan;
    $('#tnRdAuto').innerHTML = F.manual && p ? '<button type="button" class="btn xs ghost" data-rdauto>як раджу (' + p.rec + ')</button>' : '';
    $('#tnSum').innerHTML = summary();
    clearTimeout(planT); planT = setTimeout(loadPlan, 150);
  }

  function summary() {
    const pair = pairFmt(F.fmt), n = units(), p = F.plan;
    const unit = (k) => pair ? plural(k, 'пара', 'пари', 'пар') : plural(k, 'гравець', 'гравці', 'гравців');
    if (pair ? n < 2 : n < 4) return '<span class="warn">Треба хоча б ' + (pair ? '2 пари (4 гравці по черзі)' : '4 гравці') + '.</span>';
    if (F.fmt === 'mixed' && (F.women.length < 2 || F.players.length - F.women.length < 2)) return '<span class="warn">Для міксту — щонайменше дві дівчини й двоє хлопців: познач ♀.</span>';
    if (!p) return '<span class="muted">Рахую…</span>';
    const used = pair ? p.slots / 2 : p.slots / 4;
    let s = '<b>' + n + ' ' + unit(n) + '</b> · ' + used + ' ' + plural(used, 'корт', 'корти', 'кортів')
      + (used < F.courts ? ' <span class="warn">(на ' + F.courts + ' не вистачає людей)</span>' : '') + ' → '
      + (p.sit ? 'щораунду ' + p.sit + ' ' + (pair ? plural(p.sit, 'пара', 'пари', 'пар') : '') + ' ' + plural(p.sit, 'відпочиває', 'відпочивають', 'відпочивають') + ' (по черзі, без очок)' : 'грають усі, ніхто не сидить') + '.<br>';
    const R = F.fmt === 'groups' ? p.rec : F.rounds;
    s += 'Раунд ≈ ' + p.perRound + ' хв. <b>' + R + ' ' + plural(R, 'раунд', 'раунди', 'раундів') + ' ≈ ' + dur(R * p.perRound) + '</b> — у ' + fmtH(F.booking) + ' оренди влазить ' + p.fit + '.';
    if (F.fmt === 'americano' || F.fmt === 'mixed') s += ' Щоб кожен зіграв у парі з кожним — ' + p.full + '.';
    else if (F.fmt === 'team') s += ' Коло, щоб кожна пара зіграла з кожною, — ' + p.full + '.';
    else if (F.fmt === 'groups') s += ' Це група (' + p.full + ') і плей-оф.';
    if (F.fmt !== 'groups' && R * p.perRound > F.booking * 60) s += '<br><span class="warn">У оренду не влазить — або менше раундів, або коротші матчі.</span>';
    if (F.fmt === 'mixed') {
      // порада /plan не знає, хто ♀: на корт треба дві дівчини й двоє хлопців — тож кортів може зайняти менше
      const w = F.women.length, m = F.players.length - w, c = Math.min(F.courts, Math.floor(w / 2), Math.floor(m / 2));
      if (c < used) s += '<br><span class="warn">На корт — дві дівчини й двоє хлопців: з ' + w + ' ♀ і ' + m + ' ♂ грає ' + c + ' ' + plural(c, 'корт', 'корти', 'кортів') + ', решта відпочиває по черзі.</span>';
    }
    if (p.avg) s += '<br><span class="warn">' + esc(p.note) + '.</span>';
    else if (F.fmt !== 'groups' && p.sit && R % p.fair) s += '<br><span class="warn">За ' + R + ' ' + plural(R, 'раунд', 'раунди', 'раундів') + ' відпочинуть не порівну — таблиця рахуватиме середнє за матч (порівну — кратно ' + p.fair + ').</span>';
    return s;
  }

  async function loadPlan() {
    const n = units();
    if (pairFmt(F.fmt) ? n < 2 : n < 4) { F.plan = null; return; }
    const my = ++planSeq;
    const qs = new URLSearchParams({ format: F.fmt, n, courts: F.courts, total: F.total, booking: F.booking });
    if (F.total === 'time') qs.set('minutes', F.minutes);
    let p; try { p = await P.api('tournaments/plan?' + qs.toString()); } catch { return; }
    if (my !== planSeq || mode !== 'new') return;
    const same = JSON.stringify(p) === JSON.stringify(F.plan);
    F.plan = p;
    if (!F.manual) F.rounds = p.rec;
    if (!same) { $('#tnRd').textContent = F.rounds; $('#tnRdAuto').innerHTML = F.manual ? '<button type="button" class="btn xs ghost" data-rdauto>як раджу (' + p.rec + ')</button>' : ''; $('#tnSum').innerHTML = summary(); }
  }

  async function create() {
    const pair = pairFmt(F.fmt), n = units();
    if (pair ? n < 2 : n < 4) { P.toast(pair ? 'Потрібно щонайменше 2 пари' : 'Потрібно щонайменше 4 гравці'); return; }
    const body = { format: F.fmt, courts: F.courts, total: F.total, booking: F.booking };
    if (pair) { body.pairs = []; for (let i = 0; i + 1 < F.players.length; i += 2) body.pairs.push([F.players[i], F.players[i + 1]]); }
    else body.players = F.players.slice();
    if (F.fmt === 'mixed') body.women = F.women.slice();
    if (F.total === 'time') body.minutes = F.minutes;
    if (F.fmt !== 'groups') body.rounds = F.rounds;
    const title = $('#tnTitle').value.trim(); if (title) body.title = title;
    const g = $('#tnG').value; if (g) body.gathering = g;
    const go = $('#tnGo'); go.disabled = true;
    let r; try { r = await P.api('tournaments', body); } catch { go.disabled = false; return; }
    P.toast('🏆 Поїхали! Пари розкладено');
    P.go('tour', r.tournament.id);
  }

  // ------------------------------------------------------------------ турнір

  function names(t) {
    const map = {};
    for (const p of t.players || []) map[p.pid] = p.name;
    for (const pr of t.pairs || []) for (const p of pr) map[p.pid] = p.name;
    return (pid) => map[pid] || P.name(pid);
  }
  const unitHtml = (u, nm) => u.map((pid) => '<span class="pp">' + P.av(nm(pid)) + '<span>' + esc(nm(pid)) + '</span></span>').join(' ');
  const unitText = (u, nm) => u.map(nm).join(' + ');
  const canEdit = (t, m) => t.status === 'live' && (isOrg(t) || (!!P.me.pid && (m.a.includes(P.me.pid) || m.b.includes(P.me.pid))));
  const numTotal = (t) => t.total === 'time' ? null : +t.total;

  function roundMark(r) {
    if (!r.ready) return '⏳';
    if (r.matches.length && r.matches.every(scored)) return '✓';
    if (r.matches.some((m) => scored(m) || m.live)) return '•';
    return '';
  }
  function defaultRound(t) {
    const i = t.rounds.findIndex((r) => r.ready && !r.matches.every(scored));
    if (i >= 0) return i;
    const j = t.rounds.findIndex((r) => !r.ready);
    return j >= 0 ? j : Math.max(0, t.rounds.length - 1);
  }

  function renderTour() {
    const t = cur, nm = names(t), live = t.status === 'live', org = isOrg(t);
    const f = FMT[t.format] || ['', '🏆', 'Турнір'];
    const units = t.pairs ? t.pairs.length : (t.players || []).length;
    const done = t.rounds.filter((r) => r.ready && r.matches.length && r.matches.every(scored)).length;
    const anyScore = t.rounds.some((r) => r.matches.some(scored));
    if (selR[t.id] == null || selR[t.id] >= t.rounds.length) selR[t.id] = defaultRound(t);
    const act = (a, l, cls) => '<button type="button" class="btn sm' + (cls ? ' ' + cls : '') + '" data-act="' + a + '">' + l + '</button>';
    host.innerHTML = '<div class="tr-head"><a class="btn sm ghost" href="#tour">← Турніри</a></div>'
      + '<div class="card t-head"><div><div class="tt">' + f[1] + ' ' + esc(t.title) + '</div>'
      + '<div class="muted small">' + esc(f[2]) + ' · ' + units + ' ' + (t.pairs ? plural(units, 'пара', 'пари', 'пар') : plural(units, 'гравець', 'гравці', 'гравців'))
      + ' · ' + t.courts + ' ' + plural(t.courts, 'корт', 'корти', 'кортів') + ' · ' + (t.total === 'time' ? '⏱ ' + t.minutes + ' хв на матч' : 'матч до ' + esc(t.total))
      + ' · організатор ' + esc(t.organizer) + (t.status === 'done' ? ' · <b>завершено</b>' : '') + '</div>'
      + '<div class="muted small">зіграно ' + done + ' з ' + t.rounds.length + ' ' + plural(t.rounds.length, 'раунду', 'раундів', 'раундів') + '</div></div>'
      + '<div class="t-actions">' + act('lbfs', '📺 Таблиця на екран')
      + (live && org && t.format !== 'groups' ? act('addround', '+ раунд') : '')
      + (live && org ? act('finish', '🏁 Завершити', 'pri') : '')
      + ((P.me.admin || (org && !anyScore)) ? act('delete', '🗑', 'ghost bad') : '') + '</div></div>'
      + '<div class="t-grid"><div><div class="rtabs" id="trTabs"></div><div id="trBody"></div><div id="trPo"></div></div><div class="card lbwrap" id="trLb"></div></div>'
      + '<div id="trFinal"></div>';
    $('#trTabs').innerHTML = t.rounds.map((r, i) => {
      const s = roundMark(r), lbl = (r.stage && STAGE[r.stage]) || 'Р' + (i + 1);
      return '<button type="button" class="rtab' + (i === selR[t.id] ? ' on' : '') + (s === '✓' ? ' done' : '') + '" data-ri="' + i + '">' + esc(lbl) + (s ? ' <small>' + s + '</small>' : '') + '</button>';
    }).join('');
    renderRound(nm);
    $('#trLb').innerHTML = lbHtml(t, nm);
    if (t.format === 'groups') $('#trPo').innerHTML = bracketHtml(t, nm);
    $('#trFinal').innerHTML = finalHtml(t, nm);
  }

  function renderRound(nm) {
    const t = cur, i = selR[t.id], r = t.rounds[i], box = $('#trBody');
    if (!r) { box.innerHTML = '<div class="court empty">Раундів ще нема.</div>'; return; }
    const tot = numTotal(t);
    if (!r.ready) {
      const why = r.stage && r.stage !== 'group' ? 'Пари плей-оф з’являться, щойно дограють попередній раунд.'
        : t.format === 'mexicano' ? 'Мексикано ставить разом сильних: 1‑й + 3‑й проти 2‑го + 4‑го з таблиці на першому корті, наступна четвірка — на другому.'
          : t.format === 'king' ? 'Король корту: переможці йдуть на корт вище, переможені — нижче, і пари перебиваються.' : '';
      box.innerHTML = '<div class="court empty">⏳ Пари раунду ' + (i + 1) + ' з’являться, щойно буде весь рахунок раунду ' + i + '.<br><span class="muted">' + why + '</span></div>';
      return;
    }
    const side = (u, c, win) => '<div class="side ' + c + (win ? ' win' : '') + '">' + u.map((pid) => '<span class="pp">' + P.av(nm(pid)) + '<span>' + esc(nm(pid)) + '</span></span>').join('') + '</div>';
    const inp = (ci, s, v) => '<input type="number" inputmode="numeric" min="0"' + (tot ? ' max="' + tot + '"' : '') + ' data-ci="' + ci + '" data-s="' + s + '" value="' + (v == null ? '' : v) + '" placeholder="–" aria-label="очки">';
    box.innerHTML = r.matches.map((m, ci) => {
      const sg = m.stage || r.stage, ok = scored(m), ed = canEdit(t, m), st = sg && STAGE[sg] ? STAGE[sg] : m.group ? 'Група ' + m.group : '';
      const board = m.live ? '<button type="button" class="btn xs" data-board="' + esc(m.live) + '">📺 на табло</button>'
        : ed && !ok ? '<button type="button" class="btn xs ghost" data-live="' + ci + '" title="Рахувати цей матч на табло — рахунок сам ляже сюди">▶ вести на табло</button>' : '';
      return '<div class="court' + (ok ? ' done' : '') + '"><div class="court-h"><b>Корт ' + m.court + '</b>' + (st ? '<span>' + esc(st) + '</span>' : '') + board + '</div>'
        + '<div class="cm">' + side(m.a, 'a', ok && m.sa > m.sb) + '<div class="sc">'
        + (ed ? inp(ci, 'a', m.sa) + '<span>:</span>' + inp(ci, 'b', m.sb) : '<span class="ro">' + (m.sa == null ? '–' : m.sa) + '</span><span>:</span><span class="ro">' + (m.sb == null ? '–' : m.sb) + '</span>')
        + '</div>' + side(m.b, 'b', ok && m.sb > m.sa) + '</div>'
        + (m.by && ok ? '<div class="by">вніс ' + esc(m.by) + '</div>' : '') + '</div>';
    }).join('')
      + (r.sit && r.sit.length ? '<div class="sitrow">🪑 Відпочива' + (r.sit.length > 1 ? 'ють' : 'є') + ': ' + r.sit.map((pid) => '<span class="pp">' + P.av(nm(pid)) + esc(nm(pid)) + '</span>').join('')
        + ' <span class="muted">— без очок</span></div>' : '')
      + (t.status === 'live' && r.matches.some((m) => canEdit(t, m))
        ? '<div class="muted hint">' + (tot ? 'Вписуєш одне число — друге ставиться саме: разом завжди ' + tot + '.' : 'Впиши обидва числа' + (r.stage && r.stage !== 'group' ? ' — у плей-оф нічиєї нема.' : ' — нічия теж буває.')) + '</div>'
        : t.status === 'live' ? '<div class="muted hint">Рахунок вносять гравці цього корту й організатор.</div>' : '')
      + (r.matches.length && r.matches.every(scored) && i + 1 < t.rounds.length
        ? '<div class="ract"><button type="button" class="btn sm pri" data-ri="' + (i + 1) + '">' + esc((t.rounds[i + 1].stage && STAGE[t.rounds[i + 1].stage]) || 'Раунд ' + (i + 2)) + ' →</button></div>' : '');
  }

  function lbTable(t, rows, nm, all) {
    const rb = t.rankBy, any = rows.some((r) => r.pl > 0), med = ['🥇', '🥈', '🥉'];
    return '<table class="lb"><thead><tr><th>#</th><th class="l">' + (t.pairs ? 'Пара' : 'Гравець') + '</th><th class="' + (rb === 'sum' ? 's' : '') + '">Очки</th><th>М</th>'
      + '<th class="' + (rb === 'wins' ? 's' : '') + '">В</th><th>±</th><th class="' + (rb === 'avg' ? 's' : '') + '">Сер.</th><th></th></tr></thead><tbody>'
      + rows.map((s, i) => {
        const mv = all ? s.move : 0;
        return '<tr class="' + (i < 3 && any ? 'top' : '') + '"><td>' + (any && i < 3 ? med[i] : i + 1) + '</td><td class="l">' + unitHtml(s.unit, nm) + '</td>'
          + '<td class="big">' + s.pts + '</td><td>' + s.pl + '</td><td>' + s.w + '</td><td class="' + (s.diff > 0 ? 'pos' : s.diff < 0 ? 'neg' : '') + '">' + (s.diff > 0 ? '+' : '') + s.diff + '</td>'
          + '<td>' + (s.pl ? Number(s.avg).toFixed(1) : '–') + '</td><td class="mv">' + (mv > 0 ? '<span class="up">▲' + mv + '</span>' : mv < 0 ? '<span class="dn">▼' + (-mv) + '</span>' : '') + '</td></tr>';
      }).join('') + '</tbody></table>';
  }

  function lbHtml(t, nm) {
    const by = { sum: 'сумою очок', avg: 'середнім за матч', wins: 'перемогами, далі різницею' }[t.rankBy] || '';
    let body;
    const grp = {};
    for (const r of t.rounds) for (const m of r.matches) if (m.group) { grp[ukey(m.a)] = m.group; grp[ukey(m.b)] = m.group; }
    const groups = [...new Set(Object.values(grp))].sort();
    if (t.format === 'groups' && groups.length > 1) {
      body = groups.map((g) => '<div class="lb-g"><b>Група ' + esc(g) + '</b>' + lbTable(t, t.table.filter((r) => grp[ukey(r.unit)] === g), nm, false) + '</div>').join('');
    } else body = lbTable(t, t.table, nm, true);
    return '<div class="lb-h"><b>Таблиця</b><span class="muted">за ' + by + '</span><button type="button" class="btn xs lb-x" data-act="lbfs">✕</button></div>'
      + (t.rankBy === 'avg' ? '<div class="lb-avg">Рахуємо середнє за матч — зіграли різну кількість матчів, бо відпочивали не порівну.</div>' : '')
      + body
      + '<div class="muted lb-f">М — матчів, В — перемог, ± — різниця очок, Сер. — середнє за матч. Стрілки — як змінилось місце за останній раунд. За відпочинок очок нема.</div>';
  }

  /// Групи й плей-оф: хто з ким у півфіналах і фіналі (поки групи не дограно — «1A – 2B»)
  function bracketHtml(t, nm) {
    const po = t.rounds.filter((r) => r.stage && r.stage !== 'group');
    if (!po.length) return '';
    const grpN = new Set(t.rounds.flatMap((r) => r.matches.map((m) => m.group)).filter(Boolean)).size;
    const ms = (st) => po.flatMap((r) => r.matches.filter((m) => (m.stage || r.stage) === st));
    const mHtml = (m) => {
      const ok = scored(m);
      return '<div class="po-m"><div class="' + (ok && m.sa > m.sb ? 'w' : '') + '"><span>' + esc(unitText(m.a, nm)) + '</span><span>' + (m.sa == null ? '' : m.sa) + '</span></div>'
        + '<div class="' + (ok && m.sb > m.sa ? 'w' : '') + '"><span>' + esc(unitText(m.b, nm)) + '</span><span>' + (m.sb == null ? '' : m.sb) + '</span></div></div>';
    };
    const wait = (txt) => '<div class="po-m muted">' + txt + '</div>';
    const semi = ms('semi'), fin = ms('final'), third = ms('third');
    const semiPlan = grpN > 1 ? ['1A – 2B', '1B – 2A'] : ['1 – 4', '2 – 3'];
    return '<div class="card" style="margin-top:12px"><b>🏟️ Плей-оф</b><div class="po">'
      + '<div class="po-col"><b>½ фіналу</b>' + (semi.length ? semi.map(mHtml).join('') : semiPlan.map(wait).join('')) + '</div>'
      + '<div class="po-col"><b>Фінал</b>' + (fin.length ? fin.map(mHtml).join('') : wait('переможці півфіналів')) + '</div>'
      + '<div class="po-col"><b>За 3-тє</b>' + (third.length ? third.map(mHtml).join('') : wait('переможені в півфіналах')) + '</div>'
      + '</div></div>';
  }

  function finalHtml(t, nm) {
    if (!t.final) return '';
    const row = (u) => t.table.find((r) => ukey(r.unit) === ukey(u));
    const val = (u) => { const r = row(u); return !r ? '' : t.rankBy === 'wins' ? r.w + ' ' + plural(r.w, 'перемога', 'перемоги', 'перемог') : t.rankBy === 'avg' ? Number(r.avg).toFixed(1) + ' за матч' : r.pts; };
    const pd = t.final.podium || [];
    const pod = [[pd[1], 2], [pd[0], 1], [pd[2], 3]].map(([u, p]) => u ? '<div class="pod p' + p + '">' + P.av(nm(u[0])) + '<b>' + esc(unitText(u, nm)) + '</b><span>' + esc(val(u)) + '</span><div class="pod-col">' + p + '</div></div>' : '<div></div>').join('');
    const aw = (t.final.awards || []).map((a) => '<div class="aw"><span class="e">' + esc(a.emoji) + '</span><div><b>' + esc(a.title) + '</b><div class="muted">' + esc(a.text) + '</div></div></div>').join('');
    return '<div class="card final"><div class="final-h"><b>🏁 Підсумки</b><span class="muted small">Глек записав у літопис — рейтинг і відзнаки вже знають</span></div>'
      + '<div class="podium">' + pod + '</div>' + (aw ? '<div class="awards">' + aw + '</div>' : '') + '</div>';
  }

  // ------------------------------------------------------------------ дії

  async function sendScore(ri, ci) {
    const t = cur, r = t.rounds[ri], m = r && r.matches[ci];
    if (!m) return;
    const box = host.querySelectorAll('#trBody .court')[ci];
    const ia = box.querySelector('[data-s="a"]'), ib = box.querySelector('[data-s="b"]');
    const val = (el) => { const v = parseInt(el.value, 10); return Number.isNaN(v) ? null : Math.max(0, v); };
    const a = val(ia), b = val(ib);
    if ((a == null) !== (b == null)) return;          // на час — чекаємо друге число
    if (a === m.sa && b === m.sb) return;
    let res; try { res = await P.api('tournaments/' + encodeURIComponent(t.id) + '/score', { round: ri + 1, court: m.court, a, b }); }
    catch { return; }
    const before = t.rounds.filter((x) => x.ready).length;
    cur = res.tournament;
    if (cur.rounds.filter((x) => x.ready).length > before) P.toast('Раунд ' + (before + 1) + ' готовий' + (cur.format === 'mexicano' ? ' — пари за таблицею 🌶️' : cur.format === 'king' ? ' — переможці йдуть угору 👑' : ' — пари вже на кортах'));
    refresh();
  }

  /// Перемалювати, але не з-під пальців: поки людина в полі рахунку — чекаємо, доки вийде
  function refresh() {
    const a = document.activeElement;
    if (a && host.contains(a) && a.matches('.sc input')) { pending = true; return; }
    pending = false;
    if (mode === 'tour' && cur) renderTour();
  }

  async function lbFs() {
    const el = $('#trLb'); if (!el) return;
    if (document.fullscreenElement) { try { await document.exitFullscreen(); } catch { /* */ } return; }
    if (el.classList.contains('fake-fs')) { el.classList.remove('fake-fs'); P.wake(false); return; }
    try { if (!el.requestFullscreen) throw new Error('нема'); await el.requestFullscreen(); } catch { el.classList.add('fake-fs'); }
    P.wake(true);
  }

  function confirmSheet(title, text, ok, fn) {
    const sh = P.sheet(title, '<p style="margin-top:0">' + text + '</p><div class="tgo"><button type="button" class="btn" data-no>Ні</button>'
      + '<button type="button" class="btn pri" data-yes>' + esc(ok) + '</button></div>');
    sh.el.querySelector('[data-no]').onclick = sh.close;
    sh.el.querySelector('[data-yes]').onclick = () => { sh.close(); fn(); };
  }

  async function tourAct(path, body) {
    let r; try { r = await P.api('tournaments/' + encodeURIComponent(cur.id) + '/' + path, body || {}); } catch { return null; }
    if (r.tournament) { cur = r.tournament; refresh(); }
    return r;
  }

  function onClick(e) {
    const el = e.target;
    const tid = el.closest('[data-tid]');
    if (tid) { P.go('tour', tid.dataset.tid); return; }
    const a = el.closest('[data-act]');
    if (a) {
      const k = a.dataset.act;
      if (k === 'new') P.go('tour', 'new');
      else if (k === 'lbfs') lbFs();
      else if (k === 'addround') tourAct('rounds', { add: 1 }).then((r) => { if (r) { selR[cur.id] = cur.rounds.length - 1; renderTour(); } });
      else if (k === 'finish') {
        const left = cur.rounds.filter((r) => !r.ready || !r.matches.every(scored)).length;
        confirmSheet('🏁 Завершити турнір?', (left ? 'Ще ' + left + ' ' + plural(left, 'раунд', 'раунди', 'раундів') + ' без повного рахунку — рахуємо те, що є. ' : '')
          + 'Глек складе п’єдестал і нагороди, а результат піде в рейтинг.', '🏁 Завершити', () => tourAct('finish').then((r) => { if (r) P.toast('🏁 Турнір завершено'); }));
      } else if (k === 'delete') {
        confirmSheet('🗑 Видалити турнір?', 'Без сліду: ні таблиці, ні історії. Якщо вже грали — краще «🏁 Завершити».', 'Видалити', async () => {
          try { await P.api('tournaments/' + encodeURIComponent(cur.id) + '/delete', {}); } catch { return; }
          P.toast('Турнір видалено'); P.go('tour');
        });
      }
      return;
    }
    const f = el.closest('[data-fmt]');
    if (f && mode === 'new') {
      F.fmt = f.dataset.fmt; F.manual = false; F.plan = null;
      for (const x of host.querySelectorAll('.fmt')) x.classList.toggle('on', x === f);
      $('#tnTitle').placeholder = 'сама складеться: «' + FMT[F.fmt][2] + ' …»';
      mountPick();
      paintNew(); return;
    }
    const sb = el.closest('.seg[data-k] button');
    if (sb && mode === 'new') {
      const k = sb.parentElement.dataset.k, v = sb.dataset.v;
      F[k] = k === 'courts' ? +v : k === 'booking' ? +v : v;
      for (const x of sb.parentElement.children) x.classList.toggle('on', x === sb);
      F.plan = null; paintNew(); return;
    }
    if (el.closest('[data-min]') && mode === 'new') { F.minutes = Math.min(20, Math.max(10, F.minutes + +el.closest('[data-min]').dataset.min)); F.plan = null; paintNew(); return; }
    if (el.closest('[data-rd]') && mode === 'new') { F.rounds = Math.min(60, Math.max(1, F.rounds + +el.closest('[data-rd]').dataset.rd)); F.manual = true; paintNew(); return; }
    if (el.closest('[data-rdauto]') && mode === 'new') { F.manual = false; if (F.plan) F.rounds = F.plan.rec; paintNew(); return; }
    const w = el.closest('[data-w]');
    if (w && mode === 'new') { const p = w.dataset.w; F.women = F.women.includes(p) ? F.women.filter((x) => x !== p) : F.women.concat(p); paintNew(); return; }
    if (el.closest('#tnGo')) { create(); return; }
    const rt = el.closest('[data-ri]');
    if (rt && cur) { selR[cur.id] = +rt.dataset.ri; renderTour(); return; }
    const bd = el.closest('[data-board]');
    if (bd) { P.go('board', bd.dataset.board); return; }
    const lv = el.closest('[data-live]');
    if (lv && cur) {
      const ri = selR[cur.id], m = cur.rounds[ri].matches[+lv.dataset.live];
      P.api('tournaments/' + encodeURIComponent(cur.id) + '/live', { round: ri + 1, court: m.court }).then((r) => P.go('board', r.match.id)).catch(() => {});
    }
  }

  function onInput(e) {
    const inp = e.target.closest('.sc input'); if (!inp || !cur) return;
    const tot = numTotal(cur);
    const v = parseInt(inp.value, 10);
    if (tot && !Number.isNaN(v) && v >= 0 && v <= tot) {
      const o = inp.closest('.sc').querySelector('[data-s="' + (inp.dataset.s === 'a' ? 'b' : 'a') + '"]');
      if (o) o.value = tot - v;
    }
  }
  function onChange(e) {
    const inp = e.target.closest('.sc input');
    if (inp && cur) { sendScore(selR[cur.id], +inp.dataset.ci); return; }
    if (e.target.id === 'tnG' && mode === 'new') F.gathering = e.target.value;
  }
  function onFocusOut() {
    // фокус переходить з поля в поле того самого корту — ще не малюємо; вийшли зовсім — малюємо відкладене
    setTimeout(() => { if (pending) refresh(); }, 0);
  }

  async function openTour(id) {
    mode = 'tour';
    if (curId !== id) { curId = id; cur = null; }
    if (!cur) host.innerHTML = '<div class="card empty">Дістаю сітку…</div>';
    let t; try { t = await P.api('tournaments/' + encodeURIComponent(id)); }
    catch { host.innerHTML = '<div class="card empty"><span class="e">🤷</span>Такого турніру нема — може, його видалили.<br><a href="#tour">← Турніри</a></div>'; return; }
    if (curId !== id || mode !== 'tour') return;
    cur = t;
    await P.players.load();
    renderTour();
  }

  P.on('tournament', (t) => {
    if (!t || !t.id) return;
    if (t.status === 'deleted') {
      if (mode === 'tour' && curId === t.id && visible()) { P.toast('Цей турнір видалили'); P.go('tour'); }
    } else if (mode === 'tour' && curId === t.id && t.rounds) {
      const before = cur ? cur.rounds.filter((x) => x.ready).length : 0;
      cur = t;
      if (before && t.rounds.filter((x) => x.ready).length > before && visible()) P.toast('Новий раунд готовий — пари вже на кортах');
      refresh();
    }
    clearTimeout(listT);
    listT = setTimeout(loadList, 400);
  });
  // Живий матч турніру на табло — значок «📺 на табло» і рахунок оновить подія tournament; тут лише після реконекту
  P.on('reconnected', () => { if (mode === 'tour' && curId && visible()) openTour(curId); else loadList(); });

  P.tab({
    id: 'tour', icon: '🏆', title: 'Турніри', order: 2,
    mount(h) {
      host = h;
      host.addEventListener('click', onClick);
      host.addEventListener('input', onInput);
      host.addEventListener('change', onChange);
      host.addEventListener('focusout', onFocusOut);
      host.addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.target.matches('.sc input')) e.target.blur(); });
    },
    show(h, arg) {
      host = h;
      if (!arg) { mode = 'list'; if (list) renderList(); else host.innerHTML = '<div class="card empty">…</div>'; loadList(); }
      else if (arg === 'new' || arg.startsWith('new:')) openNew(arg.startsWith('new:') ? arg.slice(4) : null);
      else openTour(arg);
    },
    hide() {
      const el = $('#trLb'); if (el) el.classList.remove('fake-fs');
      if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    },
  });
  setTimeout(loadList, 0);
})();
