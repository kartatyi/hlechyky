/*
  «Як облупленого» — чи знаєш ти друзів (specs/oblup.md). Сервер (Impl/Oblup.cs) веде фази, час і очки; модуль малює
  шкалу з рамкою чи міткою, вибір питання, риску крізь доріжки всіх і підсумок «хто кого знає».

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { phase: 'between'|'pick'|'guess'|'reveal'|'done', round, of, hero, endsAt, seconds, laps,
      choices: [{q,l,r,own}] | null      // лише героєві у виборі
      q: {q,l,r,own} | null               // зі ставок
      done: bool[], my: {pos,size} | null, sizes: [{key,width,points,name}], bull, span,
      reveal: {pos, say, rows:[{seat,pos,size,hit,bull,points}]} | null,
      scores, handicap, result, recap: {rounds, knows, open, dark} | null, mine: {list:[{id,q,l,r}], max, total} }
  Кадр ('frame', лише з новиною): { round, phase, endsAt, done, scores }.
  Ходи: pick {i}; place {pos} (герой) чи {pos, size}; mine {q,l,r} | {del}.
*/
(() => {
  // Шкала від бірюзи до глини, рамка на ній і червона риска крізь рамку — гра в одному значку.
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<defs><linearGradient id="obIcoG" x1="0" x2="1"><stop stop-color="#3fae9c"/><stop offset="1" stop-color="#d9824a"/></linearGradient></defs>'
    + '<rect x=".8" y="5.6" width="14.4" height="4.8" rx="2.4" fill="url(#obIcoG)"/>'
    + '<rect x="2.9" y="3.7" width="6.4" height="8.6" rx="1.8" fill="none" stroke="var(--accent)" stroke-width="1.6"/>'
    + '<path d="M6.4 1.4v13.2" stroke="#ff5a3c" stroke-width="1.7" stroke-linecap="round"/></svg>';

  const MS = { between: 3000, pick: 15000, reveal: 8000 };
  const SPAN = 100;
  /// Те саме, що Oblup.Boxes на сервері (вид однаково приносить sizes — це лише на перший кадр).
  const SIZES = [{ key: 'w', width: 30, points: 2, name: 'широка' }, { key: 'm', width: 18, points: 3, name: 'середня' },
    { key: 'n', width: 8, points: 6, name: 'вузька' }];
  const POINTS_PER_SHARD = 5;
  /// За стільки до кінця ставок непідтверджену рамку відсилаємо самі — щоб не пропала через забуту кнопку.
  const AUTO_SEND_MS = 1300;

  const boostOf = (ctx) => Math.max(1, (ctx && ctx.me && ctx.me.boost) || 1);
  function plural(n, one, few, many) {
    const d = n % 10, h = n % 100;
    return d === 1 && h !== 11 ? one : d >= 2 && d <= 4 && (h < 12 || h > 14) ? few : many;
  }

  function state(root) {
    if (!root._ob) root._ob = { round: -1, phase: '', pos: null, size: 'm', sent: '', pickSel: 0, hold: null };
    return root._ob;
  }

  /// innerHTML лише коли HTML справді інший — інакше анімації риски починались би спочатку з кожним кадром.
  function setHtml(el, html) {
    if (!el || el._h === html) return;
    el._h = html;
    el.innerHTML = html;
  }

  function nameAt(ctx, i) { return (ctx.nameOf && ctx.nameOf(i)) || ctx.nickOf(i) || ('місце ' + (i + 1)); }

  function sizesOf(v) { return (v && v.sizes && v.sizes.length ? v.sizes : SIZES); }
  function sizeOf(v, key) { return sizesOf(v).find((s) => s.key === key) || sizesOf(v)[1]; }

  /// Кадр свіжіший за вид, але вірити йому можна лише в межах того самого раунду й фази.
  function fresh(ctx, v) {
    const f = ctx.frame;
    return f && f.round === v.round && f.phase === v.phase ? f : null;
  }

  function seats(ctx) {
    const room = ctx.room || {};
    const n = (room.seats && room.seats.length) || 0;
    const list = [];
    for (let i = 0; i < n; i++) if (ctx.nickOf(i) || (ctx.nameOf && ctx.nameOf(i))) list.push(i);
    return list;
  }

  /// Літера на мітці героя: з ніка без «гість » — інакше в усіх гостей була б «Г».
  function initial(name) {
    const s = String(name || '').replace(/^гість\s+/i, '').trim();
    return (Array.from(s)[0] || '?').toUpperCase();
  }

  const isHero = (ctx, v) => ctx.mine && v.hero === ctx.seat && v.hero >= 0;
  const canPlace = (ctx, v) => ctx.mine && ctx.playing && v.phase === 'guess';

  /// Центр рамки — так, щоб уся вона лишалась на шкалі (те саме робить сервер).
  function clampFor(ctx, v, pos, sizeKey) {
    const p = Math.round(Math.max(0, Math.min(SPAN, pos)));
    if (isHero(ctx, v)) return p;
    const half = Math.floor(sizeOf(v, sizeKey).width / 2);
    return Math.max(half, Math.min(SPAN - half, p));
  }

  const keyOf = (st, hero) => st.pos == null ? '' : hero ? 'p' + st.pos : st.size + st.pos;

  // ---------------------------------------------------------------------------------------
  // шкала (ставки)
  // ---------------------------------------------------------------------------------------

  /// Рамка чи мітка на інтерактивній шкалі — лише стилі, без innerHTML: так тягнеться плавно.
  function paintMark(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const bar = root.querySelector('.obbar');
    if (!bar) return;
    const rim = bar.querySelector('.obrim');
    const pin = bar.querySelector('.obpin');
    const ghost = bar.querySelector('.obghost');
    const hero = isHero(ctx, v);
    const has = st.pos != null;
    ghost.hidden = has || !canPlace(ctx, v);
    pin.hidden = !hero || !has;
    rim.hidden = hero || !has;
    if (!has) return;
    if (hero) {
      pin.style.left = st.pos + '%';
    } else {
      const s = sizeOf(v, st.size);
      rim.style.left = (st.pos - s.width / 2) + '%';
      rim.style.width = s.width + '%';
      rim.dataset.size = s.key;
      const label = '+' + s.points;
      const b = rim.firstElementChild;
      if (b.textContent !== label) b.textContent = label;
    }
    bar.setAttribute('aria-valuenow', String(st.pos));
    paintGo(root, ctx);
  }

  /// Кнопка «Ставлю» і рядок під нею: що зараз відіслано.
  function paintGo(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const go = root.querySelector('.obgo');
    const my = root.querySelector('.obmy');
    if (!go) return;
    const hero = isHero(ctx, v);
    const k = keyOf(st, hero);
    const server = v.my ? (hero ? 'p' + v.my.pos : (v.my.size || '') + v.my.pos) : '';
    const sent = !!k && (k === server || k === st.sent);
    const label = sent ? '✓ Поставлено' : server || st.sent ? 'Переставити' : hero ? 'Ось я' : 'Ставлю';
    if (go.textContent !== label) go.textContent = label;
    go.disabled = !k || sent;
    go.classList.toggle('ready', !!k && !sent);
    // Нічого не стоїть — підказує сама шкала («тицяй на шкалу») і рядок статусу; двічі не пишемо.
    const text = !k ? ''
      : sent ? (hero ? 'Можна пересунути, поки інші думають' : 'Можна пересунути, поки є час')
      : 'Ще не відіслано — тисни «' + label + '»';
    if (my.textContent !== text) my.textContent = text;
  }

  function moveTo(root, ctx, pos) {
    const v = ctx.view || {};
    if (!canPlace(ctx, v)) return;
    const st = state(root);
    st.pos = clampFor(ctx, v, pos, st.size);
    paintMark(root, ctx);
  }

  function step(root, ctx, d) {
    const st = state(root);
    moveTo(root, ctx, (st.pos == null ? 50 : st.pos) + d);
  }

  function setSize(root, ctx, key) {
    const v = ctx.view || {};
    if (!canPlace(ctx, v) || isHero(ctx, v)) return;
    const st = state(root);
    st.size = key;
    if (st.pos != null) st.pos = clampFor(ctx, v, st.pos, key);
    paintSizes(root, ctx);
    paintMark(root, ctx);
  }

  function cycleSize(root, ctx) {
    const list = sizesOf(ctx.view);
    const st = state(root);
    const i = list.findIndex((s) => s.key === st.size);
    setSize(root, ctx, list[(i + 1) % list.length].key);
  }

  function send(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    if (!canPlace(ctx, v) || st.pos == null) return false;
    const hero = isHero(ctx, v);
    const k = keyOf(st, hero);
    if (k === st.sent) return true;
    st.sent = k;
    paintGo(root, ctx);
    Promise.resolve(ctx.act('place', hero ? { pos: st.pos } : { pos: st.pos, size: st.size }))
      .then((r) => { if (!r || !r.ok) { if (st.sent === k) st.sent = ''; } paintGo(root, ctx); }, () => { st.sent = ''; paintGo(root, ctx); });
    return true;
  }

  function posFromEvent(bar, e) {
    const r = bar.getBoundingClientRect();
    return r.width > 0 ? (e.clientX - r.left) / r.width * SPAN : 50;
  }

  function wireBar(root, ctx) {
    const bar = root.querySelector('.obbar');
    let drag = false;
    bar.addEventListener('pointerdown', (e) => {
      if (!canPlace(root._ctx || ctx, (root._ctx || ctx).view || {})) return;
      drag = true;
      try { bar.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
      moveTo(root, root._ctx || ctx, posFromEvent(bar, e));
      e.preventDefault();
    });
    bar.addEventListener('pointermove', (e) => { if (drag) moveTo(root, root._ctx || ctx, posFromEvent(bar, e)); });
    const stop = () => { drag = false; };
    bar.addEventListener('pointerup', stop);
    bar.addEventListener('pointercancel', stop);
  }

  // ---------------------------------------------------------------------------------------
  // блоки картки
  // ---------------------------------------------------------------------------------------

  function polesHtml(ctx, q) {
    return '<div class="obpoles"><span class="obl">' + ctx.esc(q.l) + '</span><span class="obr">' + ctx.esc(q.r) + '</span></div>';
  }

  function paintSizes(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const box = root.querySelector('.obsizes');
    const show = canPlace(ctx, v) && !isHero(ctx, v);
    const html = !show ? '' : sizesOf(v).map((s, i) => '<button type="button" class="obsz' + (s.key === st.size ? ' on' : '')
      + '" data-size="' + s.key + '" aria-pressed="' + (s.key === st.size) + '" title="' + (i ? '' : 'Ширина рамки: ↑ / ↓ чи R') + '">'
      + '<i class="obszi" style="--w:' + s.width + '"></i><span>' + s.name + '</span><b>+' + s.points + '</b></button>').join('');
    setHtml(box, html);
  }

  function paintHead(root, ctx) {
    const v = ctx.view || {};
    const ph = v.phase || 'between';
    const head = root.querySelector('.obhead');
    const heroName = v.hero >= 0 ? nameAt(ctx, v.hero) : '';
    const me = isHero(ctx, v);
    let html = '';
    if (!ctx.playing && !v.result) {
      html = '<div class="obbanner lobby"><b>Як облупленого</b><small>Хто кого тут знає як облупленого?</small></div>';
    } else if (ph === 'between') {
      html = '<div class="obbanner" data-k="' + v.round + '"><small>Раунд ' + v.round + ' з ' + v.of + '</small>'
        + '<b>' + (me ? '📍 У центрі — ти!' : '📍 У центрі — ' + ctx.esc(heroName)) + '</b>'
        + '<small>' + (me ? 'Зараз обереш питання про себе' : 'Вгадуй, де стане ' + ctx.esc(heroName)) + '</small></div>';
    } else if (ph === 'pick') {
      html = me ? '<div class="obask">📍 Ти в центрі — <b>обери питання про себе</b></div>'
        : '<div class="obwaitq"><b>' + ctx.esc(heroName) + '</b> обирає питання<span class="obdots"><i>.</i><i>.</i><i>.</i></span></div>';
    } else if ((ph === 'guess' || ph === 'reveal') && v.q) {
      const who = ph === 'reveal' ? '📍 ' + ctx.esc(heroName) : me ? 'Де ти?' : 'Де стане <b>' + ctx.esc(heroName) + '</b>?';
      html = '<div class="obask">' + who + (v.q.own ? ' <span class="obown" title="Питання дописав хтось за столом">✍ своє</span>' : '') + '</div>'
        + '<div class="obq">' + ctx.esc(v.q.q) + '</div>';
    }
    setHtml(head, html);
  }

  function paintPick(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const box = root.querySelector('.obpick');
    const list = v.phase === 'pick' && isHero(ctx, v) && v.choices ? v.choices : [];
    const html = list.map((c, i) => '<button type="button" class="obcard' + (i === st.pickSel ? ' sel' : '') + '" data-pick="' + i + '">'
      + '<b>' + ctx.esc(c.q) + '</b><span class="obcp"><i>' + ctx.esc(c.l) + '</i><em>⟷</em><i>' + ctx.esc(c.r) + '</i></span>'
      + (c.own ? '<small>✍ питання від когось за столом</small>' : '') + '</button>').join('');
    setHtml(box, html);
  }

  function paintScale(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const wrap = root.querySelector('.obscale');
    const show = v.phase === 'guess' && v.q;
    wrap.hidden = !show;
    if (!show) return;
    setHtml(wrap.querySelector('.obpolesbox'), polesHtml(ctx, v.q));
    const bar = wrap.querySelector('.obbar');
    const active = canPlace(ctx, v);
    bar.classList.toggle('live', active);
    bar.classList.toggle('hero', isHero(ctx, v));
    bar.tabIndex = active ? 0 : -1;
    // Глядач і той, хто не за столом, бачать шкалу без рамки — лише питання й галочки.
    if (!active) st.pos = null;
    paintMark(root, ctx);
  }

  /// Риска: доріжка героя згори, під нею — доріжки всіх, і червона риска з'їжджає крізь них по черзі.
  function boardHtml(ctx, v) {
    const r = v.reveal;
    if (!r || !v.q) return '';
    const heroName = nameAt(ctx, v.hero);
    const pos = r.pos;
    const cut = pos == null ? '' : '<i class="obcut" style="left:' + pos + '%"></i>';
    let html = '<div class="obboard' + (pos == null ? ' void' : '') + '">'
      + '<span></span>' + polesHtml(ctx, v.q) + '<span></span>'
      + '<span class="obname hero">📍 ' + ctx.esc(heroName) + '</span>'
      + '<div class="obtrack herot" style="--i:0">' + (pos == null ? '<em class="obnone">мітки нема</em>'
        : '<span class="obpin on" style="left:' + pos + '%"><i>' + ctx.esc(initial(heroName)) + '</i></span>') + cut + '</div>'
      + '<b class="obpts"></b>';
    const rows = (r.rows || []).slice().sort((a, b) => a.seat - b.seat);
    rows.forEach((x, n) => {
      const s = x.size ? sizeOf(v, x.size) : null;
      const cls = x.pos == null ? 'none' : x.hit ? 'hit' : 'miss';
      const mine = ctx.mine && x.seat === ctx.seat ? ' me' : '';
      html += '<span class="obname' + mine + '">' + ctx.esc(nameAt(ctx, x.seat)) + '</span>'
        + '<div class="obtrack ' + cls + mine + '" style="--i:' + (n + 1) + '">'
        + (s ? '<span class="obrim ' + s.key + (x.bull ? ' bull' : '') + '" style="left:' + (x.pos - s.width / 2) + '%;width:' + s.width + '%"><b>'
          + (x.hit ? '+' + x.points : '+' + s.points) + '</b></span>' : '<em class="obnone">без рамки</em>')
        + cut + '</div>'
        + '<b class="obpts ' + cls + '" style="--i:' + (n + 1) + '">' + (x.hit ? (x.bull ? '🎯' : '') + '+' + x.points : x.pos == null ? '' : '0') + '</b>';
    });
    html += '</div>';
    if (r.say) html += '<div class="obsay" style="--i:' + (rows.length + 1) + '"><img src="/static/glek.svg" alt=""><span>' + ctx.esc(r.say) + '</span></div>';
    return html;
  }

  function paintWho(root, ctx) {
    const v = ctx.view || {};
    const box = root.querySelector('.obwho');
    const f = fresh(ctx, v);
    const done = (f && f.done) || v.done || [];
    const show = ctx.playing && v.phase === 'guess';
    const html = !show ? '' : seats(ctx).map((i) => '<span class="obchip' + (done[i] ? ' on' : '') + (i === v.hero ? ' hero' : '') + '">'
      + (done[i] ? '✓ ' : '') + (i === v.hero ? '📍 ' : '') + ctx.esc(nameAt(ctx, i)) + '</span>').join('');
    setHtml(box, html);
  }

  function hcText(v, i) {
    const f = i >= 0 && v.handicap && v.handicap[i];
    if (!f || Math.abs(f - 1) < 0.05) return '';
    return String(Math.round(f * 100) / 100).replace('.', ',');
  }

  function paintScores(root, ctx) {
    const v = ctx.view || {};
    const f = fresh(ctx, v);
    const sc = (f && f.scores && f.scores.length) ? f.scores : (v.scores || []);
    const box = root.querySelector('.obscore');
    const win = (v.result && v.result.winners) || [];
    const list = seats(ctx);
    const html = (!ctx.playing && !v.result) || (!v.result && !list.some((i) => sc[i])) ? '' : list.slice()
      .sort((a, b) => (sc[b] || 0) - (sc[a] || 0) || a - b)
      .map((i) => {
        const shards = v.result ? Math.floor((sc[i] || 0) / POINTS_PER_SHARD) * boostOf(ctx) : 0;
        const hc = hcText(v, i);
        return '<div class="obsrow' + (win.includes(i) ? ' win' : '') + (i === v.hero && ctx.playing ? ' hero' : '') + '">'
          + '<span>' + (i === v.hero && ctx.playing && v.phase !== 'done' ? '📍 ' : '') + ctx.esc(nameAt(ctx, i))
          + (hc ? ' <i class="obhc" title="⚖ Фора: очки ×' + hc + '">⚖ ×' + hc + '</i>' : '') + '</span>'
          + (shards > 0 ? '<i class="obshard" title="черепки за очки">🏺+' + shards + '</i>' : '')
          + '<b>' + (sc[i] || 0) + '</b></div>';
      }).join('');
    setHtml(box, html);
  }

  /// Міні-шкала для підсумку: де стояв герой.
  function miniHtml(ctx, r) {
    return '<span class="obmini" title="' + ctx.esc(r.l + ' ⟷ ' + r.r) + '"><i class="l">' + ctx.esc(r.l) + '</i>'
      + '<span class="obminibar">' + (r.pos == null ? '' : '<b style="left:' + r.pos + '%"></b>') + '</span>'
      + '<i class="r">' + ctx.esc(r.r) + '</i></span>';
  }

  function endHtml(ctx, v) {
    const rc = v.recap;
    if (v.phase !== 'done' || !rc) return '';
    const sc = (v.result && v.result.scores) || v.scores || [];
    const win = (v.result && v.result.winners) || [];
    let html = '<div class="obend">';
    if (win.length === 1) html += '<div class="obwin">🏆 <b>' + ctx.esc(nameAt(ctx, win[0])) + '</b> знає цей стіл найкраще — ' + (sc[win[0]] || 0) + ' ' + plural(sc[win[0]] || 0, 'очко', 'очки', 'очок') + '</div>';
    else if (win.length > 1) html += '<div class="obwin">🤝 Нарівні: ' + win.map((i) => '<b>' + ctx.esc(nameAt(ctx, i)) + '</b>').join(', ') + ' — по ' + (sc[win[0]] || 0) + '</div>';
    else html += '<div class="obwin">Ніхто ні разу не влучив — ви одне для одного загадки</div>';
    if (ctx.mine && !win.includes(ctx.seat) && sc[ctx.seat] != null) html += '<div class="muted small">У тебе — ' + sc[ctx.seat] + '</div>';

    if (rc.knows && rc.knows.length) {
      html += '<div class="obknows"><div class="obh">Хто кого знає як облупленого</div>'
        + rc.knows.map((k) => '<div class="obkrow"><b>' + ctx.esc(k.name) + '</b><span class="muted">— найкраще знає</span><b class="by">'
          + k.names.map((n) => ctx.esc(n)).join(', ') + '</b><i>+' + k.points + '</i></div>').join('') + '</div>';
    }
    const badges = [];
    if (rc.open) badges.push('<span class="obbadge">📖 Відкрита книга — <b>' + ctx.esc(rc.open.name) + '</b> <i>влучили ' + rc.open.hits + ' з ' + rc.open.of + '</i></span>');
    if (rc.dark) badges.push('<span class="obbadge">🎭 Темна конячка — <b>' + ctx.esc(rc.dark.name) + '</b> <i>влучили ' + rc.dark.hits + ' з ' + rc.dark.of + '</i></span>');
    if (badges.length) html += '<div class="obbadges">' + badges.join('') + '</div>';

    const rounds = rc.rounds || [];
    if (rounds.length) {
      html += '<details class="obrounds"><summary>Як це було — ' + rounds.length + ' ' + plural(rounds.length, 'раунд', 'раунди', 'раундів') + '</summary><ol>'
        + rounds.map((r) => '<li><div><b>' + ctx.esc(r.name) + '</b> · ' + ctx.esc(r.q) + (r.own ? ' <span class="obown">✍</span>' : '') + '</div>'
          + '<div class="obrline">' + miniHtml(ctx, r) + '<span class="muted small">' + (r.pos == null ? 'без мітки' : 'влучили ' + r.hits + ' з ' + r.of) + '</span></div></li>').join('')
        + '</ol></details>';
    }
    return html + '</div>';
  }

  function paintMine(root, ctx) {
    const v = ctx.view || {};
    const box = root.querySelector('.obmine');
    const m = v.mine || { list: [], max: 3, total: 0 };
    // Дописати своє можна будь-коли, крім вибору й ставок — тоді не до того.
    box.hidden = !ctx.mine || (ctx.playing && (v.phase === 'pick' || v.phase === 'guess'));
    if (box.hidden) return;
    const sum = '✍ Своє питання' + (m.total ? ' · у черзі столу ' + m.total : '');
    const s = box.querySelector('summary');
    if (s.textContent !== sum) s.textContent = sum;
    setHtml(box.querySelector('.obmlist'), (m.list || []).map((x) => '<li><span>' + ctx.esc(x.q) + ' <i class="muted">(' + ctx.esc(x.l) + ' ⟷ ' + ctx.esc(x.r) + ')</i></span>'
      + '<button type="button" class="obmdel" data-del="' + ctx.esc(x.id) + '" title="Прибрати">✕</button></li>').join(''));
    const full = (m.list || []).length >= (m.max || 3);
    box.querySelector('.obmform').hidden = full;
    const note = box.querySelector('.obmnote');
    const nt = full ? 'У черзі вже ' + m.max + ' твоїх — хай спершу зіграють' : 'Твоє питання дістанеться комусь у центрі, тобі воно не випаде. До ' + m.max + ' у черзі';
    if (note.textContent !== nt) note.textContent = nt;
  }

  function addMine(root, ctx) {
    const q = root.querySelector('.obmq'), l = root.querySelector('.obml'), r = root.querySelector('.obmr');
    Promise.resolve(ctx.act('mine', { q: q.value, l: l.value, r: r.value })).then((res) => {
      if (res && res.ok) { q.value = ''; l.value = ''; r.value = ''; }
      paint(root, ctx);
    }, () => {});
  }

  /// Автовідсилання: рамка стоїть, а «Ставлю» не натиснули — за мить до кінця відсилаємо самі.
  function watchDeadline(root, ctx) {
    const st = state(root);
    const v = ctx.view || {};
    const need = canPlace(ctx, v) && v.endsAt;
    if (!need) { if (st.timer) { clearInterval(st.timer); st.timer = 0; } return; }
    if (st.timer) return;
    st.timer = setInterval(() => {
      const c = root._ctx;
      const cv = (c && c.view) || {};
      if (!c || !canPlace(c, cv)) { clearInterval(st.timer); st.timer = 0; return; }
      const left = Date.parse(cv.endsAt) - Date.now();
      if (left < AUTO_SEND_MS && st.pos != null && keyOf(st, isHero(c, cv)) !== st.sent) send(root, c);
    }, 250);
  }

  function paint(root, ctx) {
    root._ctx = ctx;
    const v = ctx.view || {};
    const st = state(root);
    const ph = v.phase || 'between';

    // Новий раунд чи фаза — свіжа шкала; після перезаходу посеред ставок — те, що вже на сервері.
    if (st.round !== v.round || st.phase !== ph) {
      if (st.round !== v.round || ph !== 'reveal') { st.pos = null; st.sent = ''; }
      if (ph === 'pick') st.pickSel = 0;
      st.round = v.round;
      st.phase = ph;
    }
    if (ph === 'guess' && st.pos == null && v.my) {
      st.pos = v.my.pos;
      if (v.my.size) st.size = v.my.size;
      st.sent = keyOf(st, isHero(ctx, v));
    }

    const top = root.querySelector('.obtop');
    const no = root.querySelector('.obno');
    const noText = !ctx.playing || !v.of ? '' : ph === 'done' ? '' : 'Раунд ' + v.round + ' з ' + v.of;
    if (no.textContent !== noText) no.textContent = noText;
    if (ctx.playing && v.endsAt && ph !== 'done') {
      const total = ph === 'guess' ? (v.seconds || 45) * 1000 : (MS[ph] || 8000);
      HGames.ui.timerArc(top, v.endsAt, total);
    } else {
      const arc = top.querySelector(':scope > .garc');
      if (arc) { if (arc._arc) arc._arc.stop(); arc.remove(); }
    }

    paintHead(root, ctx);
    paintPick(root, ctx);
    paintScale(root, ctx);
    paintSizes(root, ctx);
    const act = root.querySelector('.obact');
    act.hidden = !canPlace(ctx, v);
    paintGo(root, ctx);
    setHtml(root.querySelector('.obboard-box'), ph === 'reveal' ? boardHtml(ctx, v) : '');
    setHtml(root.querySelector('.obendbox'), endHtml(ctx, v));
    const rules = root.querySelector('.obrules');
    // Правила — у лобі й перед першим раундом; далі всі вже знають, а на телефоні вони лише штовхають гру вниз.
    rules.hidden = ctx.playing && !(ph === 'between' && v.round === 1) || !!v.result;
    paintWho(root, ctx);
    paintScores(root, ctx);
    paintMine(root, ctx);
    watchDeadline(root, ctx);
  }

  function onClick(root, ctx, e) {
    const b = e.target.closest('button');
    if (!b || !root.contains(b)) return;
    const c = root._ctx || ctx;
    if (b.dataset.pick != null) c.act('pick', { i: +b.dataset.pick });
    else if (b.dataset.size) setSize(root, c, b.dataset.size);
    else if (b.classList.contains('obgo')) send(root, c);
    else if (b.dataset.del) Promise.resolve(c.act('mine', { del: b.dataset.del })).then(() => paint(root, c), () => {});
    else if (b.classList.contains('obmadd')) addMine(root, c);
  }

  function holdStop(root) {
    const st = root && root._ob;
    if (st && st.hold) { clearTimeout(st.hold.t); clearInterval(st.hold.i); st.hold = null; }
  }

  const RULES = 'По черзі хтось один — <b>у центрі</b>: обирає питання й таємно стає на шкалу між двома крайнощами. '
    + 'Решта ставить рамку туди, де, на їхню думку, буде мітка. Рамка <b>широка</b> — +2, <b>середня</b> — +3, <b>вузька</b> — +6, '
    + 'риска за крок від центру рамки — 🎯 ще +1. Хто в центрі, очок не бере. За кожні 5 очок — 🏺';

  const roots = new Map();

  HGames.register({
    id: 'oblup',
    added: '2026-11-01',
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd'],

    pad: {
      dirs: 'x',
      a: 'Enter',
      x: 'r',
      get hint() { return '{dpad} по шкалі · {x} інша рамка · {a} ставлю'; },
      when: (ctx) => ctx.mine && ctx.playing && ((ctx.view || {}).phase === 'guess' || ((ctx.view || {}).phase === 'pick' && (ctx.view || {}).hero === ctx.seat)),
    },

    mount(root, ctx) {
      root._ob = null;
      state(root);
      root.innerHTML = '<div class="obw">'
        + '<div class="obmain">'
        + '<div class="obtop"><span class="obno muted small"></span></div>'
        + '<div class="obhead"></div>'
        + '<div class="obpick"></div>'
        + '<div class="obscale g-field" hidden><div class="obpolesbox"></div>'
        + '<div class="obbar" role="slider" aria-label="Шкала" aria-valuemin="0" aria-valuemax="100">'
        + '<span class="obticks"></span><span class="obghost">тицяй на шкалу</span>'
        + '<span class="obrim" hidden><b></b></span><span class="obpin" hidden><i>Я</i></span></div></div>'
        + '<div class="obsizes"></div>'
        + '<div class="obact" hidden><button type="button" class="primary obgo">Ставлю</button><div class="obmy muted small"></div></div>'
        + '<div class="obboard-box"></div>'
        + '<div class="obendbox"></div>'
        + '<div class="obwho"></div>'
        + '<div class="obrules muted small">' + RULES + '</div>'
        + '<details class="obmine" hidden><summary>✍ Своє питання</summary>'
        + '<div class="obmform"><input class="obmq" type="text" maxlength="90" autocomplete="off" placeholder="Питання: «Наскільки ти боїшся висоти?»" aria-label="Своє питання">'
        + '<div class="obmlr"><input class="obml" type="text" maxlength="32" autocomplete="off" placeholder="ліворуч: «ні краплі»" aria-label="Лівий край">'
        + '<input class="obmr" type="text" maxlength="32" autocomplete="off" placeholder="праворуч: «до тремтіння»" aria-label="Правий край"></div>'
        + '<button type="button" class="obmadd">Додати</button></div>'
        + '<ul class="obmlist"></ul><div class="obmnote muted small"></div></details>'
        + '</div>'
        + '<div class="obscore"></div>'
        + '</div>';
      root.querySelector('.obw').addEventListener('click', (e) => onClick(root, ctx, e));
      root.querySelector('.obmform').addEventListener('keydown', (e) => {
        if (e.key === 'Enter') { e.preventDefault(); addMine(root, root._ctx || ctx); }
      });
      wireBar(root, ctx);
      // Стік пада тисне стрілку й тримає: рухаємо, поки не відпустять (keyup ловимо самі — onKey бачить лише keydown).
      root._up = (e) => { if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') holdStop(root); };
      document.addEventListener('keyup', root._up);
      roots.set(ctx, root);
      paint(root, ctx);
    },

    update(root, ctx) { roots.set(ctx, root); paint(root, ctx); },

    frame(root, ctx) {
      if (!root.querySelector('.obwho')) return;
      paintWho(root, ctx);
      paintScores(root, ctx);
    },

    onKey(e, ctx) {
      const root = roots.get(ctx);
      if (!root || !root.isConnected) return false;
      const v = ctx.view || {};
      const st = state(root);
      // Цифри 1–4 — гарячі клавіші розділів сайту (Ефір, Бібліотека…): їх не чіпаємо, інакше вибір питання ще й
      // перекидав на «Ефір». Тому вибір — стрілками й Enter, ширина рамки — ↑/↓ чи R.
      if (v.phase === 'pick' && isHero(ctx, v) && ctx.playing) {
        if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') { st.pickSel = e.key === 'ArrowLeft' ? 0 : 1; paintPick(root, ctx); return true; }
        if (e.key === 'Enter') { ctx.act('pick', { i: st.pickSel }); return true; }
        return false;
      }
      if (!canPlace(ctx, v)) return false;
      if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
        const d = (e.key === 'ArrowLeft' ? -1 : 1) * (e.shiftKey ? 5 : 1);
        if (e.repeat && st.hold) return true;
        holdStop(root);
        step(root, ctx, d);
        // Тримають — після паузи їде саме, як повзунок; keyup зупиняє.
        st.hold = { t: setTimeout(() => { if (st.hold) st.hold.i = setInterval(() => step(root, root._ctx || ctx, d), 45); }, 320), i: 0 };
        return true;
      }
      if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
        if (isHero(ctx, v)) return false;
        // ↑ — вужча (ризик і більше очок), ↓ — ширша; по краях стоїть.
        const list = sizesOf(v);
        const i = list.findIndex((s) => s.key === st.size) + (e.key === 'ArrowUp' ? 1 : -1);
        if (i >= 0 && i < list.length) setSize(root, ctx, list[i].key);
        return true;
      }
      if (e.key === 'r' || e.key === 'R' || e.key === 'к' || e.key === 'К') { if (isHero(ctx, v)) return false; cycleSize(root, ctx); return true; }
      if (e.key === 'Enter') return send(root, ctx);
      return false;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      const v = ctx.view || {};
      const hero = v.hero >= 0 ? nameAt(ctx, v.hero) : '';
      if (v.phase === 'pick') return isHero(ctx, v) ? (ctx.ui.coarse() ? 'Тицьни картку з питанням' : 'Клацни картку або ← → і Enter') : hero + ' обирає питання…';
      if (v.phase === 'guess') {
        if (!ctx.mine) return 'Гравці ставлять…';
        return isHero(ctx, v) ? 'Де ти насправді? Тицяй на шкалу й тисни «Ось я»' : 'Де стане ' + hero + '? Постав рамку й тисни «Ставлю»';
      }
      if (v.phase === 'reveal') return 'Риска!';
      if (v.phase === 'between') return 'У центрі — ' + hero;
      return '';
    },

    unmount(root) {
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      holdStop(root);
      const st = root._ob;
      if (st && st.timer) clearInterval(st.timer);
      if (root._up) document.removeEventListener('keyup', root._up);
      for (const [k, r] of roots) if (r === root) roots.delete(k);
      root._ob = null;
      root._ctx = null;
    },
  });
})();
