/*
  «Дотепи» — Quiplash по-нашому (specs/dotepy.md). Клієнт нічого не вирішує: фази, час, голоси й очки живуть на
  сервері (Impl/Dotepy.cs), модуль лише малює те, що прийшло, і шле наміри.

  Вид (подія 'room', свій для кожного місця — гра Hidden; кадрів нема):
    { phase: 'lobby'|'write'|'vote'|'reveal'|'table'|'done', round, rounds, final, mode: 'duel'|'all',
      endsAt, totalMs, waiting, voice, players: [{ seat, nick, score, ready, voted, left }], prompts: string[],
      me: null | { tasks: [{ i, prompt, text, done }], mine: number[], voter, picks: number[] },
      card: null | { i, of, prompt, answers: [{ text, stock, seat, votes, medals, jury, juryBy, points, rank, prize, laughs }],
                     votersCount, votedCount, voted, juryVotes, perVoter, ranked, sweep, jinx, shown },
      say: null | { id, text, url, seconds }, table: null | { rows, best }, result: null | { winners, scores, best, early } }
  У дуелі до розкриття нема ні списку суддів, ні «хто проголосував» (це видало б двох авторів) — лише лічильники.
  Ходи: Input('draft', { i, text }) · Act('answer', { i, text }) · Act('edit', { i }) · Act('vote', { card, picks })
        · Act('laugh', { card, i }) — «😂» на розкритті.
  Глядач голосує як публіка: POST /api/games/dotepy/jury { room, card, pick }; сміється — POST /api/games/dotepy/laugh { room, card, i }.

  DOM оновлюється лише на подію 'room' (≤ 4 на секунду) і лише той поверх, чий підпис змінився. Анімації — CSS.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.5 2.5h11a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H7l-3 3v-3H2.5a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1z" fill="var(--accent)"/>'
    + '<path d="M4.5 5.2l1.4 2.2 1.4-2.2 1.4 2.2 1.4-2.2 1.4 2.2" fill="none" stroke="var(--accent-ink)" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"/>'
    + '</svg>';

  const MAX = 80;             // Dotepy.MaxAnswer
  const DRAFT_MS = 350;       // чернетка — не частіше
  const VERDICT_DELAY = 1200; // вердикт — коли голоси вже «прилетіли»
  const MEDALS = ['🥇', '🥈', '🥉'];
  const SPK_KEY = 'dotepySpeaker';
  const CONFETTI = ['😂', '🤣', '💥', '🏺', '✨', '🎉'];
  const PAD_SETTLE = 450;     // мс: поки картки сцени проявляться (dt-rise), кільце пада на них не ставимо

  const reduced = () => !!(window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches);

  function st(root) {
    if (!root._dt) root._dt = {
      keys: {}, sayId: 0, speaking: false, reading: false, next: null, later: 0, line: 0, radioMuted: null,
      drafts: {}, draftTimers: {}, local: null, jury: null, ac: null, stale: false, stats: { n: 0, sum: 0, max: 0 },
      laughed: new Set(), timers: new Set(),
    };
    return root._dt;
  }

  /// setTimeout, який unmount гарантовано зніме (звуки, конфеті, «😂» — усе, що може пережити картку).
  function later(s, fn, ms) {
    const id = setTimeout(() => { s.timers.delete(id); fn(); }, ms);
    s.timers.add(id);
    return id;
  }

  /// Пад (Steam Deck): поставити кільце сюди. Кожна нова сцена сама каже, де кільцю бути, — інакше після
  /// перебудови воно падало на найближчу кнопку, а нею бувало «Встати» каркаса (одне Ⓐ — і партія на трьох скінчена).
  function padTo(root, el) {
    const P = window.HPad;
    if (!P || !P.on || !el || !el.isConnected || typeof P.focus !== 'function') return;
    const a = document.activeElement;
    // людина саме друкує (екранна клавіатура пада на полі) — кільце не чіпаємо
    if (a && a !== el && /^(INPUT|TEXTAREA)$/.test(a.tagName) && !a.disabled && root.contains(a)) return;
    try { P.focus(el); } catch { /* пад — прикраса */ }
  }

  const col = (seat) => 'var(--dt-p' + seat + ', var(--accent))';
  const num = (n) => Number(n || 0).toLocaleString('uk-UA');
  const short = (s, n) => (s && s.length > n ? s.slice(0, n - 1) + '…' : s || '');
  /// Нік для очей: без приставки «гість » — у чіпі на десять знаків від «гість Петро» лишалось «гість Пет…».
  const disp = (nick) => (/^гість\s+\S/i.test(nick || '') ? nick.replace(/^гість\s+/i, '') : nick || '');

  function nickOf(ctx, v, seat) {
    const p = (v.players || []).find((x) => x.seat === seat);
    return disp((p && p.nick) || ctx.nickOf(seat) || ctx.seatName(seat));
  }

  function lobbyOf(ctx, v) {
    return !v.phase || v.phase === 'lobby' || !!(ctx.room && ctx.room.status === 'lobby');
  }

  // =============================================================================================
  // верх: раунд, таймер, тумблер голосу, гравці
  // =============================================================================================

  function paintTop(root, ctx, v) {
    const lobby = lobbyOf(ctx, v);
    const pill = root.querySelector('.dt-pill');
    const text = lobby ? '😂 Дотепи'
      : v.phase === 'done' ? 'Партію зіграно'
      : v.final ? '🏁 Останній дотеп'
      : 'Раунд ' + v.round + ' із ' + v.rounds + (v.mode === 'duel' ? ' · дуелі' : ' · на всіх');
    if (pill.textContent !== text) pill.textContent = text;

    const box = root.querySelector('.dt-arcbox');
    const wait = root.querySelector('.dt-wait');
    const timed = ctx.playing && !lobby && v.endsAt && !v.waiting && v.phase !== 'done';
    if (timed) HGames.ui.timerArc(box, v.endsAt, v.totalMs || 1000);
    else {
      const arc = box.querySelector(':scope > .garc');
      if (arc) { if (arc._arc) arc._arc.stop(); arc.remove(); }
    }
    wait.hidden = !(ctx.playing && v.waiting);

    const spk = root.querySelector('.dt-spk');
    spk.hidden = v.voice === 'none';
    const on = speakerOn(ctx);
    const label = on ? '🔊 Глек тут' : '🔇 Глек';
    if (spk.textContent !== label) spk.textContent = label;
    spk.classList.toggle('on', on);
    spk.title = on ? 'Дядько Глек читає вголос на цьому пристрої. Натисни — вимкнути'
      : 'Тут Глек мовчить. Натисни — хай читає тут (телевізор, колонка)';
  }

  function paintPlayers(root, ctx, v) {
    const box = root.querySelector('.dt-players');
    const list = lobbyOf(ctx, v) ? [] : (v.players || []);
    const voting = v.phase === 'vote';
    const writing = v.phase === 'write';
    // Лише ✓ тим, хто вже проголосував (сервер ставить його тільки там, де це нічого не видає: «на всіх» і фінал).
    // Жодних «🎭 автор / 🤔 суддя»: у дуелі це й були два автори картки.
    const html = list.map((p) => {
      const mark = p.left ? '🚪'
        : writing ? (p.ready ? '✓' : '✍')
        : voting && p.voted ? '✓'
        : '';
      const full = p.nick || '';
      return '<span class="dt-pl' + (p.left ? ' left' : '') + (mark === '✓' ? ' ok' : '') + '" style="--c:' + col(p.seat) + '" title="'
        + ctx.esc(full) + '"><i class="dt-dot"></i><b>' + (p.seat + 1) + '</b><span class="dt-nk">' + ctx.esc(short(disp(full), 12)) + '</span>'
        + '<em>' + num(p.score) + '</em>' + (mark ? '<u>' + mark + '</u>' : '') + '</span>';
    }).join('');
    if (box.dataset.sig !== html) { box.dataset.sig = html; box.innerHTML = html; }
  }

  // =============================================================================================
  // сцена
  // =============================================================================================

  function stageKey(ctx, v) {
    const m = (ctx.room && ctx.room.round) || 0;
    if (lobbyOf(ctx, v)) return 'lobby|' + (ctx.mine ? 'p' : 's');     // сів за стіл — з'являється поле свого завдання
    switch (v.phase) {
      case 'write': {
        const tasks = (v.me && v.me.tasks) || [];
        return 'write|' + m + '|' + v.round + '|' + (v.me ? 'p' + tasks.map((t) => t.i).join(',') : 's');
      }
      case 'vote':
      case 'reveal':
        return v.card ? 'card|' + m + '|' + v.round + '|' + v.card.i + '|' + v.card.answers.length + (v.me ? '|p' : '|s') : 'empty';
      case 'table': return 'table|' + m + '|' + v.round;
      case 'done': return 'done|' + m;
      default: return 'empty';
    }
  }

  function paintStage(root, ctx, v) {
    const s = st(root);
    const stage = root.querySelector('.dt-stage');
    const key = stageKey(ctx, v);
    if (s.keys.stage !== key) {
      const was = s.keys.stage;
      s.keys = { stage: key };
      // Нова сцена — шапка гри (раунд і дуга часу) знову на екрані. На телефоні «Почати» тиснуть унизу лобі,
      // і вся партія далі йшла з таймером за краєм — а завдання чи картка починались під шапкою сайту.
      if (was && !key.startsWith('lobby')) requestAnimationFrame(() => showTop(root));
      s.local = null;
      stage.className = 'dt-stage dt-' + key.split('|')[0];
      if (key.startsWith('lobby')) { stage.innerHTML = lobbyHtml(ctx); bindOwn(root, stage); }
      else if (key.startsWith('write')) buildWrite(root, ctx, v, stage);
      else if (key.startsWith('card')) buildCard(root, ctx, v, stage);
      else if (key.startsWith('table')) buildTable(root, ctx, v, stage);
      else if (key.startsWith('done')) buildDone(root, ctx, v, stage);
      else stage.innerHTML = '';
      padScene(root, stage);
    }
    if (key.startsWith('lobby')) {
      // склад міняється й до старту: на шістьох підказуємо коротку партію
      const tip = stage.querySelector('.dt-howtip');
      const text = lobbyTip(ctx);
      if (tip && tip.textContent !== text) { tip.textContent = text; tip.hidden = !text; }
      refreshOwn(ctx, v, stage);
    }
    if (key.startsWith('write')) refreshWrite(root, ctx, v, stage);
    else if (key.startsWith('card')) refreshCard(root, ctx, v, stage);
  }

  /// Шапка гри вилізла за верх (під липку шапку сайту) — прокрутити, щоб вона стала під неї. Коли видно — не чіпаємо.
  function showTop(root) {
    const top = root.querySelector('.dt-top');
    if (!top || !top.isConnected || document.querySelector('.modal:not([hidden]), .padhelp, .padkbd')) return;
    const head = document.querySelector('body > header');
    const hb = head ? Math.max(0, head.getBoundingClientRect().bottom) : 0;
    const t = top.getBoundingClientRect().top;
    if (t >= hb - 2) return;
    try { window.scrollBy({ top: t - hb - 6, behavior: reduced() ? 'auto' : 'smooth' }); } catch { /* старий браузер */ }
  }

  /// Де стати кільцю пада на новій сцені: перша жива ціль ([data-pad-first]) або тиха «стоянка» ([data-pad-focus]) —
  /// Ⓐ на ній нічого не робить. На кінці партії — «Ще раз» каркаса, якщо він є.
  function padScene(root, stage) {
    if (!window.HPad || !window.HPad.on) return;
    const s = st(root);
    const key = s.keys.stage;
    // Одразу — на тиху стоянку сцени (завдання, заголовок): стара ціль щойно зникла, і без цього кільце за кадр
    // упало б на найближчу кнопку, «Встати» каркаса.
    padTo(root, stage.querySelector('[data-pad-focus]'));
    // Картки з'являються анімацією з нульової прозорості, а пад такі цілі вважає невидимими й перескакує на
    // найближчу видиму — тож на першу справжню ціль кільце ставимо, коли вони вже проявились.
    later(s, () => {
      if (s.keys.stage !== key || !stage.isConnected) return;
      const vis = (e) => e && !e.disabled && !e.hidden && e.getClientRects().length > 0;
      let el = [...stage.querySelectorAll('[data-pad-first]')].find(vis) || null;
      if (!el && stage.classList.contains('dt-done')) {
        const table = root.closest('.gtable');
        el = table ? table.querySelector('[data-do="Rematch"]') : null;
      }
      if (!vis(el)) el = stage.querySelector('[data-pad-focus]');
      padTo(root, el);
    }, PAD_SETTLE);
  }

  function lobbyHtml(ctx) {
    const o = (ctx.room && ctx.room.options) || {};
    const rounds = o.rounds || 'full';
    const secs = +(o.write || 90);
    const votes = rounds === 'blitz'
      ? 'Роздаєш 🥇🥈🥉 чужим дотепам: 300, 200 і 100 очок.'
      : rounds === 'short'
        ? 'Голос — 100 очок. Троє й більше за одного — «Розгром!» А далі — «Останній дотеп» з медалями 🥇🥈🥉.'
        : 'Голос — 100 очок, у другому раунді — 200. Троє й більше за одного — «Розгром!»';
    return '<div class="dt-how">'
      + '<div class="dt-howrow"><b>✍</b><span><i>Пиши.</i> ' + (rounds === 'blitz' ? 'Одне дурне завдання на всіх' : 'Кожному — дурні завдання')
      + ', ' + secs + ' с на дотеп. Найсмішніша відповідь одним рядком.</span></div>'
      + '<div class="dt-howrow"><b>🗳</b><span><i>Голосуй.</i> Відповіді виходять анонімно — обирай найдотепнішу чужу.</span></div>'
      + '<div class="dt-howrow"><b>🎭</b><span><i>Дивись, хто це написав.</i> ' + votes + '</span></div>'
      + '<div class="dt-howsmall muted small">Троє й більше. ' + (o.voice === 'none' ? 'Цього разу Глек мовчить — усе текстом' : 'Дядько Глек зачитує все вголос')
      + '; глядачі голосують як публіка 👀 і сміються 😂</div>'
      + '<div class="dt-howtip small" hidden></div>'
      + (ctx.mine ? '<form class="dt-own"><input class="dt-ownin" type="text" maxlength="100" autocomplete="off" spellcheck="true"'
        + ' enterkeyhint="send" placeholder="Своє завдання для друзів — необов\'язково" aria-label="Своє завдання">'
        + '<button class="ghost dt-ownbtn" type="submit">Додати</button></form>' : '')
      + '<div class="dt-ownst muted small"></div>'
      + '</div>';
  }

  /// Своє завдання в лобі: «Що Петро завжди забуває на рибалці» — піде в партію першим, з підписом автора.
  function bindOwn(root, stage) {
    const form = stage.querySelector('.dt-own');
    if (!form) return;
    const input = form.querySelector('.dt-ownin');
    form.addEventListener('submit', (e) => {
      e.preventDefault();
      const c = root._ctx;
      if (!c) return;
      c.act('mine', { text: input.value }).then((r) => { if (r && r.ok) input.blur(); });
    });
    stage.addEventListener('click', (e) => {
      const b = e.target.closest('.dt-owndel');
      if (!b) return;
      const c = root._ctx;
      if (c) c.act('mine', { text: '' }).then((r) => { if (r && r.ok) input.value = ''; });
    });
  }

  function refreshOwn(ctx, v, stage) {
    const o = v.own || { count: 0, mine: null };
    const input = stage.querySelector('.dt-ownin');
    if (input && document.activeElement !== input && o.mine && !input.value) input.value = o.mine;
    const st = stage.querySelector('.dt-ownst');
    const html = (o.mine ? '✓ Твоє завдання в партії · <button type="button" class="linkish dt-owndel">прибрати</button>' : '')
      + (o.count ? (o.mine ? ' · ' : '') + 'своїх завдань за столом: ' + o.count : '');
    if (st && st.dataset.sig !== html) { st.dataset.sig = html; st.innerHTML = html; }
  }

  /// «✍ автор завдання: Петро» — під своїм завданням друга (нік у називному: ніки ми не відмінюємо).
  function byHtml(ctx, v, by) {
    return by == null ? '' : '<div class="dt-by" style="--c:' + col(by) + '">✍ автор завдання: <b>' + ctx.esc(nickOf(ctx, v, by)) + '</b></div>';
  }

  function lobbyTip(ctx) {
    const r = ctx.room || {};
    let seated = 0;
    for (let i = 0; i < 8; i++) if (ctx.nickOf(i)) seated++;
    const rounds = (r.options && r.options.rounds) || 'full';
    return seated >= 6 && rounds === 'full'
      ? '⏱ На ' + seated + ' повна партія — хвилин 12–15. Швидше — стіл «1 раунд + Останній дотеп».'
      : '';
  }

  // ---------- написання ----------

  function buildWrite(root, ctx, v, stage) {
    const s = st(root);
    const tasks = (v.me && v.me.tasks) || [];
    let html = '';
    if (v.say && v.say.text) html += '<div class="dt-say intro"><img src="/static/glek.svg" alt=""><span>' + ctx.esc(v.say.text) + '</span></div>';
    if (v.me) {
      html += tasks.map((t, n) => '<div class="dt-task" data-i="' + t.i + '" style="--n:' + n + '">'
        + '<div class="dt-prompt" data-pad-focus>' + ctx.esc(t.prompt) + '</div>' + byHtml(ctx, v, t.by)
        + '<form class="dt-form"><input class="dt-in" type="text" maxlength="' + MAX + '" autocomplete="off" spellcheck="true" data-pad-first'
        + ' enterkeyhint="send" placeholder="твій дотеп…" aria-label="Відповідь на завдання ' + (n + 1) + '">'
        + '<button class="primary dt-send" type="submit">Здати</button></form>'
        + '<div class="dt-meta"><span class="dt-cnt">0/' + MAX + '</span></div>'
        + '<div class="dt-given"><span class="dt-giventxt"></span><span class="dt-ok">✓ Здано</span>'
        + '<button type="button" class="ghost small dt-edit">Змінити</button></div>'
        + '</div>').join('');
      if (!tasks.length) html += '<div class="gempty">Цього раунду тобі завдань нема — дивись і чекай голосування</div>';
    } else {
      html += '<div class="dt-watch" data-pad-focus>Пишуть дотепи… 🤫 Скоро відповіді вийдуть на голосування — голосуй як публіка 👀</div>';
    }
    html += '<details class="dt-peek"' + (v.me ? '' : ' open') + '><summary>На що пишуть</summary><ol class="dt-plist"></ol></details>';
    stage.innerHTML = html;
    s.drafts = {};
    stage.querySelectorAll('.dt-task').forEach((box) => {
      const i = +box.dataset.i;
      const t = tasks.find((x) => x.i === i) || {};
      const input = box.querySelector('.dt-in');
      input.value = t.text || '';
      s.drafts[i] = input.value;
      counter(box);
      input.addEventListener('input', () => { counter(box); draftSoon(root, i, input); });
      input.addEventListener('focus', () => {
        // телефон: клавіатура не має закрити завдання — підсуваємо картку до середини
        if (HGames.ui.coarse()) setTimeout(() => box.scrollIntoView({ block: 'center', behavior: reduced() ? 'auto' : 'smooth' }), 250);
      });
      box.querySelector('.dt-form').addEventListener('submit', (e) => { e.preventDefault(); submit(root, i, input); });
      box.querySelector('.dt-edit').addEventListener('click', () => {
        const c = root._ctx;
        if (!c) return;
        c.act('edit', { i }).then((r) => { if (r && r.ok) setTimeout(() => input.focus(), 50); });
      });
    });
    // запитання з'явились — курсор одразу в першому полі (на телефоні — ні: клавіатура закрила б пів екрана)
    const first = stage.querySelector('.dt-task .dt-in');
    if (first && !HGames.ui.coarse() && ctx.playing && !(tasks[0] && tasks[0].done)) {
      const busy = document.activeElement;
      if (!busy || busy === document.body || !/^(INPUT|TEXTAREA|SELECT)$/.test(busy.tagName)) setTimeout(() => first.focus({ preventScroll: true }), 30);
    }
  }

  function counter(box) {
    const input = box.querySelector('.dt-in');
    const cnt = box.querySelector('.dt-cnt');
    const n = input.value.length;
    cnt.textContent = n + '/' + MAX;
    cnt.classList.toggle('hot', n > MAX - 10);
  }

  function draftSoon(root, i, input) {
    const s = st(root);
    if (s.draftTimers[i]) return;
    s.draftTimers[i] = setTimeout(() => {
      s.draftTimers[i] = 0;
      const c = root._ctx;
      if (!c || !c.playing || s.drafts[i] === input.value) return;
      s.drafts[i] = input.value;
      c.input('draft', { i, text: input.value });
    }, DRAFT_MS);
  }

  function submit(root, i, input) {
    const c = root._ctx;
    if (!c) return;
    const text = input.value.trim();
    if (!text) { c.toast('Порожній дотеп — то ще не дотеп', 'err'); input.focus(); return; }
    const s = st(root);
    clearTimeout(s.draftTimers[i]);
    s.draftTimers[i] = 0;
    s.drafts[i] = input.value;
    const box = input.closest('.dt-task');
    box.classList.add('sending');
    c.act('answer', { i, text }).then((r) => {
      box.classList.remove('sending');
      if (!r || !r.ok) return;
      // здав — курсор до наступного незданого поля
      const next = [...root.querySelectorAll('.dt-task')].find((b) => b !== box && !b.classList.contains('done'));
      if (next && !HGames.ui.coarse()) next.querySelector('.dt-in').focus();
      else input.blur();
    });
  }

  function refreshWrite(root, ctx, v, stage) {
    const s = st(root);
    const tasks = (v.me && v.me.tasks) || [];
    let doneAll = tasks.length > 0;
    let justDone = false;
    for (const t of tasks) {
      const box = stage.querySelector('.dt-task[data-i="' + t.i + '"]');
      if (!box) continue;
      const input = box.querySelector('.dt-in');
      if (box.classList.contains('done') !== t.done) {
        box.classList.toggle('done', t.done);
        if (t.done) justDone = true;
        // забрав назад («Змінити») — у полі те, що здавав
        if (!t.done && document.activeElement !== input) { input.value = t.text || ''; s.drafts[t.i] = input.value; counter(box); }
      }
      if (t.done) {
        const g = box.querySelector('.dt-giventxt');
        if (g.textContent !== t.text) g.textContent = t.text;
      }
      input.disabled = !ctx.playing || t.done;
      doneAll = doneAll && t.done;
    }
    const list = stage.querySelector('.dt-plist');
    const html = (v.prompts || []).map((p) => '<li>' + ctx.esc(p) + '</li>').join('');
    if (list && list.dataset.sig !== html) { list.dataset.sig = html; list.innerHTML = html; }
    const peek = stage.querySelector('.dt-peek');
    if (peek) peek.hidden = !!v.me && !doneAll;
    // здане поле зникає — кільце пада переходить на наступне незадане (або на завдання), а не на «Встати»
    if (justDone) {
      const next = [...stage.querySelectorAll('.dt-task:not(.done) .dt-in')].find((x) => !x.disabled);
      padTo(root, next || stage.querySelector('.dt-task [data-pad-focus]'));
    }
  }

  // ---------- картка: голосування й розкриття ----------

  function buildCard(root, ctx, v, stage) {
    const c = v.card;
    const k = c.answers.length;
    const mine = (v.me && v.me.mine) || [];
    const first = c.answers.findIndex((_, i) => !mine.includes(i));
    stage.innerHTML = '<div class="dt-cardhead"><span class="dt-of">' + (v.final ? 'Одне завдання — на всіх' : 'Картка ' + (c.i + 1) + ' з ' + c.of) + '</span>'
      + '<span class="dt-hint muted small"></span></div>'
      + '<div class="dt-prompt big" data-pad-focus>' + ctx.esc(c.prompt) + '</div>' + byHtml(ctx, v, c.by)
      + (v.final ? '<div class="dt-podium mini" hidden></div>' : '')
      + '<div class="dt-answers ' + (v.final ? 'final' : v.mode) + (k > 4 ? ' dense' : '') + ' n' + k + '">'
      + c.answers.map((a, i) => '<button type="button" class="dt-ans' + (a.stock ? ' stock' : '') + '" data-i="' + i + '" style="--n:' + i + '"' + (i === first ? ' data-pad-first' : '') + '>'
        + '<span class="dt-n">' + (i + 1) + '</span><span class="dt-txt">' + ctx.esc(a.text) + '</span>'
        + '<span class="dt-badge"></span><span class="dt-lol"></span><span class="dt-res"></span></button>').join('')
      + '</div>'
      + '<div class="dt-foot"><span class="dt-voted"></span><span class="dt-jury"></span></div>'
      + '<div class="dt-say" hidden></div>';
    stage.querySelectorAll('.dt-ans').forEach((b) => b.addEventListener('click', (e) => {
      if (!HGames.ui.human(e)) return;
      const c2 = root._ctx;
      if (c2 && c2.view && c2.view.phase === 'reveal') laugh(root, +b.dataset.i, b);
      else pick(root, +b.dataset.i);
    }));
  }

  /// Мій вибір на цій картці: локальний (щойно тапнув) або те, що підтвердив сервер.
  function myPicks(root, v) {
    const s = st(root);
    const key = v.round + ':' + (v.card && v.card.i);
    if (s.local && s.local.key === key) return s.local.picks;
    return (v.me && v.me.picks) || [];
  }

  function juryKey(ctx, v) {
    return ctx.room.id + ':' + ctx.room.round + ':' + v.round + ':' + (v.card && v.card.i);
  }

  function juryPick(root, ctx, v) {
    const s = st(root);
    const key = juryKey(ctx, v);
    if (s.jury && s.jury.key === key) return s.jury.pick;
    try {
      const saved = JSON.parse(sessionStorage.getItem('dotepyJury') || 'null');
      if (saved && saved.key === key) { s.jury = saved; return saved.pick; }
    } catch { /* приватне вікно */ }
    return null;
  }

  function pick(root, i) {
    const ctx = root._ctx;
    if (!ctx || !ctx.playing) return;
    const v = ctx.view || {};
    const c = v.card;
    if (v.phase !== 'vote' || !c) return;
    unlockSound(root);

    if (!ctx.mine) { juryVote(root, ctx, v, i); return; }
    const me = v.me || {};
    if ((me.mine || []).includes(i)) { ctx.toast('Це твій дотеп — за нього голосують інші', 'err'); return; }
    if (!me.voter) { ctx.toast('Твій дотеп у грі — голосують інші', 'err'); return; }

    let picks = myPicks(root, v).slice();
    if (!c.ranked) picks = [i];
    else if (picks.includes(i)) {
      if (picks.length === 1) { ctx.toast('Хоч один голос лишається — тапни інший дотеп, і медаль перейде', 'wait'); return; }
      picks = picks.filter((x) => x !== i);
    } else if (picks.length < c.perVoter) picks.push(i);
    else picks[picks.length - 1] = i;     // усі медалі роздано — остання переходить сюди
    send(root, ctx, v, picks);
  }

  function send(root, ctx, v, picks) {
    const s = st(root);
    const key = v.round + ':' + v.card.i;
    const was = s.local;
    s.local = { key, picks };
    beep(s, 880, 40);
    refreshCard(root, ctx, v, root.querySelector('.dt-stage'));
    ctx.act('vote', { card: v.card.i, picks }).then((r) => {
      if (r && r.ok) return;
      // сервер не прийняв — повертаємо, як було
      if (s.local && s.local.picks === picks) s.local = was && was.key === key ? was : null;
      const c = root._ctx;
      if (c) refreshCard(root, c, c.view || {}, root.querySelector('.dt-stage'));
    });
  }

  function juryVote(root, ctx, v, i) {
    const s = st(root);
    const key = juryKey(ctx, v);
    const was = s.jury;
    s.jury = { key, pick: i };
    beep(s, 660, 40);
    refreshCard(root, ctx, v, root.querySelector('.dt-stage'));
    const undo = (text) => {
      s.jury = was;
      ctx.toast(text, 'err');
      const c = root._ctx;
      if (c) refreshCard(root, c, c.view || {}, root.querySelector('.dt-stage'));
    };
    fetch('/api/games/dotepy/jury', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent((ctx.me && ctx.me.nick) || '') },
      body: JSON.stringify({ room: ctx.room.id, card: v.card.i, pick: i }),
    }).then((r) => r.json()).then((d) => {
      if (!d || !d.ok) { undo((d && d.message) || 'Голос публіки не пройшов'); return; }
      try { sessionStorage.setItem('dotepyJury', JSON.stringify(s.jury)); } catch { /* приватне вікно */ }
    }).catch(() => undo('Зв\'язку нема — голос публіки не дійшов'));
  }

  /// «😂» на розкритті: гравець — через хаб, глядач — через HTTP. Один сміх на відповідь; очок не дає — це зал.
  function laugh(root, i, btn) {
    const ctx = root._ctx;
    if (!ctx || !ctx.playing) return;
    const v = ctx.view || {};
    const c = v.card;
    if (v.phase !== 'reveal' || !c || !c.answers[i] || c.answers[i].seat == null) return;
    if (ctx.mine && ((v.me && v.me.mine) || []).includes(i)) return;       // зі свого не сміються
    const s = st(root);
    const key = (ctx.room.round || 0) + ':' + v.round + ':' + c.i + ':' + i;
    const b = btn || root.querySelector('.dt-ans[data-i="' + i + '"]');
    if (b && !reduced()) {
      // смішок вилітає з картки одразу — навіть якщо цей сміх уже зарахований
      const fly = document.createElement('span');
      fly.className = 'dt-fly';
      fly.textContent = '😂';
      fly.style.setProperty('--x', (20 + Math.random() * 60).toFixed(0) + '%');
      b.appendChild(fly);
      later(s, () => fly.remove(), 1300);
    }
    if (s.laughed.has(key)) return;
    s.laughed.add(key);
    unlockSound(root);
    beep(s, 740, 50, 'triangle', 0.035);
    if (b) b.classList.add('laughed');
    if (ctx.mine) { ctx.act('laugh', { card: c.i, i }); return; }
    fetch('/api/games/dotepy/laugh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent((ctx.me && ctx.me.nick) || '') },
      body: JSON.stringify({ room: ctx.room.id, card: c.i, i }),
    }).then((r) => r.json()).then((d) => { if (!d || !d.ok) s.laughed.delete(key); }).catch(() => s.laughed.delete(key));
  }

  function refreshCard(root, ctx, v, stage) {
    const c = v.card;
    if (!c || !stage || !stage.querySelector('.dt-answers')) return;
    const s = st(root);
    const me = v.me || {};
    const reveal = v.phase === 'reveal';
    const picks = ctx.mine ? myPicks(root, v) : [];
    const jury = !ctx.mine ? juryPick(root, ctx, v) : null;
    const mine = me.mine || [];

    const hint = stage.querySelector('.dt-hint');
    // на телефоні цифр нема — хвіст «· цифри 1–N» лише там, де є клавіатура
    const keys = HGames.ui.coarse() ? '' : ' · цифри 1–' + c.answers.length;
    const others = c.answers.some((a, i) => a.seat != null && !mine.includes(i));
    const hintText = reveal ? (v.final && c.shown < c.answers.length ? 'Розкриваємо з кінця…' : others && ctx.playing ? 'Смішно? Тапни — 😂' : '')
      : !ctx.mine ? 'Тапни — голос публіки 👀'
      : !me.voter ? 'Твій дотеп у грі — тримай кулаки'
      : c.ranked ? 'Тапай по черзі: 🥇 → 🥈' + (c.perVoter > 2 ? ' → 🥉' : '') + keys
      : 'Обери найдотепніше' + keys;
    if (hint.textContent !== hintText) hint.textContent = hintText;

    const buttons = stage.querySelectorAll('.dt-ans');
    let best = -1;
    let bestPts = 0;
    c.answers.forEach((a, i) => { if (a.points != null && a.points > bestPts) { bestPts = a.points; best = i; } });
    const allOpen = !v.final || c.shown >= c.answers.length;
    const laughPrefix = (ctx.room.round || 0) + ':' + v.round + ':' + c.i + ':';
    buttons.forEach((b, i) => {
      const a = c.answers[i];
      if (!a) return;
      const own = mine.includes(i);
      const rank = picks.indexOf(i);
      b.classList.toggle('mine', own);
      b.classList.toggle('off', !reveal && ctx.mine && !me.voter && !own);
      b.classList.toggle('picked', rank >= 0 || jury === i);
      b.classList.toggle('hidden', reveal && v.final && a.seat == null);
      // на розкритті відповіді знову натискні — тепер це «😂»
      b.disabled = reveal ? (own || a.seat == null || !ctx.playing) : (own || (ctx.mine && !me.voter) || !ctx.playing);
      b.classList.toggle('lolable', reveal && !b.disabled);
      b.classList.toggle('laughed', s.laughed.has(laughPrefix + i));
      const badge = own ? 'твій' : rank >= 0 ? (c.ranked ? MEDALS[rank] : '✓') : jury === i ? '👀' : '';
      const bEl = b.querySelector('.dt-badge');
      if (bEl.textContent !== badge) bEl.textContent = badge;
      const lol = b.querySelector('.dt-lol');
      const lolText = reveal && a.laughs ? '😂 ' + a.laughs : '';
      if (lol.textContent !== lolText) {
        lol.textContent = lolText;
        // «підстрибнути» — через Web Animations: перезапуск CSS-анімації класом вимагав би примусової розкладки
        if (lolText && !reduced() && lol.animate) lol.animate([{ transform: 'scale(.4)' }, { transform: 'scale(1.2)' }, { transform: 'none' }], { duration: 350, easing: 'ease-out' });
      }
      if (reveal && a.seat != null && !b.classList.contains('open')) openAnswer(root, ctx, v, b, a);
      b.classList.toggle('win', reveal && allOpen && i === best);
    });

    const voted = stage.querySelector('.dt-voted');
    // Дуель: хто суддя, а хто автор — таємниця до розкриття, тож лише «скільки з скількох».
    const secret = v.mode === 'duel' && !v.final;
    const vText = reveal ? ''
      : secret ? (c.votedCount ? 'проголосували: ' + c.votedCount + ' з ' + c.votersCount : 'ще ніхто не голосував')
      : (c.voted || []).length
        ? 'проголосували: ' + c.voted.map((x) => '✓ ' + ctx.esc(short(nickOf(ctx, v, x), 10))).join(' · ')
        : 'ще ніхто не голосував';
    if (voted.dataset.sig !== vText) { voted.dataset.sig = vText; voted.innerHTML = vText; }
    const jEl = stage.querySelector('.dt-jury');
    const jText = c.juryVotes ? '👀 публіка: ' + c.juryVotes : '';
    if (jEl.textContent !== jText) jEl.textContent = jText;

    const say = stage.querySelector('.dt-say');
    const sayText = reveal && v.say && allOpen ? v.say.text : '';
    const sayHtml = sayText ? '<img src="/static/glek.svg" alt=""><span>' + ctx.esc(sayText) + '</span>' : '';
    if (say.dataset.sig !== sayHtml) { say.dataset.sig = sayHtml; say.innerHTML = sayHtml; say.hidden = !sayHtml; }

    if (v.final) paintPodium(ctx, v, stage.querySelector('.dt-podium'));
    if (reveal && !s.keys.revealed) {
      s.keys.revealed = true;
      // голосування скінчилось: вибрані кнопки фіналу стали «сорочкою» (недоступні) — кільце пада на завдання,
      // інакше воно впало б на найближчу кнопку каркаса
      padTo(root, stage.querySelector('[data-pad-first]:not(:disabled)') || stage.querySelector('[data-pad-focus]'));
    }
    if (reveal && c.sweep != null && !s.keys.sweep) {
      s.keys.sweep = true;
      const b = buttons[c.sweep];
      if (b) sweep(root, b);
    }
    if (reveal && c.jinx && !s.keys.jinx) {
      s.keys.jinx = true;
      const box = stage.querySelector('.dt-answers');
      const rib = document.createElement('div');
      rib.className = 'dt-jinx';
      rib.textContent = '🤝 Думки сходяться! Обом — як за голос';
      box.parentNode.insertBefore(rib, box);
    }
  }

  /// Розкриття однієї відповіді: голоси прилітають чіпами, очки вистрибують, автор виїжджає знизу.
  function openAnswer(root, ctx, v, b, a) {
    const s = st(root);
    const res = b.querySelector('.dt-res');
    const votes = a.votes || [];
    const chips = votes.map((seat, n) => '<i class="dt-vote" style="--c:' + col(seat) + ';--d:' + (n * 80) + 'ms" title="'
      + ctx.esc(nickOf(ctx, v, seat)) + '">' + (a.medals ? MEDALS[(a.medals[n] || 1) - 1] : (seat + 1)) + '</i>').join('');
    // публіка: приз (від двох глядачів) — «👀 +100», інакше просто скільки глядачів і хто саме
    const by = (a.juryBy || []).map((n) => short(disp(n), 10));
    const jury = a.prize ? '<span class="dt-prize" title="' + ctx.esc(by.join(', ')) + '">👀 +' + (v.final ? 200 : 100) + '</span>'
      : a.jury ? '<span class="dt-jurysm muted" title="Голос публіки">👀 ' + ctx.esc(by.join(', ') || String(a.jury)) + '</span>' : '';
    const none = v.card && v.card.jinx ? '🤝 без голосування' : 'без голосів';
    res.innerHTML = '<span class="dt-votes">' + (chips || '<small class="muted">' + none + '</small>') + '</span>'
      + '<span class="dt-pts' + (a.points ? '' : ' zero') + '">' + (a.points ? '+' + num(a.points) : '0') + '</span>'
      + jury
      + '<span class="dt-author" style="--c:' + col(a.seat) + '"><i class="dt-dot"></i>' + ctx.esc(nickOf(ctx, v, a.seat))
      + (a.stock ? ' <small>· підставна</small>' : '') + '</span>';
    b.classList.add('open');
    if (!reduced()) votes.forEach((_, n) => later(s, () => beep(s, 520 + n * 40, 30, 'triangle', 0.03), 400 + n * 80));
  }

  function sweep(root, b) {
    const s = st(root);
    b.classList.add('sweep');
    const rib = document.createElement('span');
    rib.className = 'dt-sweep';
    rib.textContent = '💥 Розгром!';
    b.appendChild(rib);
    if (!reduced()) {
      // Конфеті злітає з боків і знизу картки й летить геть від тексту — переможний дотеп саме читають.
      const box = document.createElement('span');
      box.className = 'dt-confetti';
      let html = '';
      for (let n = 0; n < 24; n++) {
        const left = n % 2 === 0;
        const x = left ? (n * 7) % 12 : 88 + (n * 5) % 12;          // 0–12 % або 88–100 %
        const d = (n * 53) % 600, r = ((n * 71) % 60) + 20;
        html += '<span style="--x:' + x + '%;--d:' + d + 'ms;--r:' + (left ? -r : r) + 'deg;--dx:' + (left ? -1 : 1) + '">'
          + CONFETTI[n % CONFETTI.length] + '</span>';
      }
      box.innerHTML = html;
      b.appendChild(box);
      later(s, () => box.remove(), 2800);
    }
    [523, 659, 784].forEach((f, n) => later(s, () => beep(s, f, 120, 'triangle', 0.04), 1000 + n * 90));
  }

  /// П'єдестал фіналу: добудовується, коли розкриваються 🥉, 🥈, 🥇.
  function paintPodium(ctx, v, el) {
    if (!el) return;
    const c = v.card;
    const top = c.answers.filter((a) => a.rank != null && a.rank <= 3 && !a.stock);
    const slots = [2, 1, 3].map((r) => {
      const a = top.find((x) => x.rank === r);
      return '<div class="dt-step r' + r + (a ? ' on' : '') + '"' + (a ? ' style="--c:' + col(a.seat) + '"' : '') + '>'
        + '<b>' + MEDALS[r - 1] + '</b>' + (a ? '<span>' + ctx.esc(short(nickOf(ctx, v, a.seat), 12)) + '</span><em>+' + num(a.points) + '</em>' : '<span>?</span>')
        + '</div>';
    }).join('');
    if (el.dataset.sig !== slots) { el.dataset.sig = slots; el.innerHTML = slots; }
    el.hidden = v.phase !== 'reveal';
  }

  // ---------- підсумок раунду й партії ----------

  function bestHtml(ctx, v, b, title) {
    if (!b) return '';
    return '<div class="dt-best" style="--c:' + col(b.seat) + '">' + (title ? '<div class="dt-besttitle">' + title + '</div>' : '')
      + '<div class="dt-bestq">' + ctx.esc(b.prompt) + '</div>'
      + '<div class="dt-besta">«' + ctx.esc(b.text) + '»</div>'
      + '<div class="dt-bestby"><i class="dt-dot"></i>' + ctx.esc(nickOf(ctx, v, b.seat)) + ' · <b>+' + num(b.points) + '</b></div></div>';
  }

  function buildTable(root, ctx, v, stage) {
    const t = v.table || { rows: [] };
    const max = Math.max(1, ...t.rows.map((r) => r.score));
    stage.innerHTML = '<div class="dt-tabtitle" data-pad-focus>Раунд ' + v.round + ' позаду</div>'
      + '<div class="dt-bars">' + t.rows.map((r, n) => {
        const p = (v.players || []).find((x) => x.seat === r.seat) || {};
        return '<div class="dt-bar' + (p.left ? ' left' : '') + '" style="--c:' + col(r.seat) + ';--w:' + Math.round(100 * r.score / max) + '%;--n:' + n + '">'
          + '<span class="dt-bn"><i class="dt-dot"></i>' + ctx.esc(short(nickOf(ctx, v, r.seat), 14)) + '</span>'
          + '<span class="dt-bt"><i></i></span><b>' + num(r.score) + '</b>'
          + '<em>' + (r.delta ? '+' + num(r.delta) : '') + '</em></div>';
      }).join('') + '</div>'
      + bestHtml(ctx, v, t.best, '⭐ Дотеп раунду');
    requestAnimationFrame(() => stage.querySelectorAll('.dt-bar').forEach((b) => b.classList.add('go')));
  }

  function buildDone(root, ctx, v, stage) {
    const r = v.result || { winners: [], scores: [], best: [] };
    const rows = (v.players || []).slice().sort((a, b) => b.score - a.score || a.seat - b.seat);
    const podium = [1, 0, 2].map((n) => {
      const p = rows[n];
      if (!p) return '<div class="dt-step r' + (n + 1) + '"></div>';
      const win = (r.winners || []).includes(p.seat);
      return '<div class="dt-step on r' + (n + 1) + (win ? ' win' : '') + '" style="--c:' + col(p.seat) + '">'
        + '<b>' + (win ? '👑' : MEDALS[n]) + '</b><span>' + ctx.esc(short(disp(p.nick), 12)) + '</span><em>' + num(p.score) + '</em></div>';
    }).join('');
    const rest = rows.slice(3).map((p) => '<span class="dt-restp' + (p.left ? ' left' : '') + '" style="--c:' + col(p.seat) + '"><i class="dt-dot"></i>'
      + ctx.esc(short(disp(p.nick), 12)) + ' <b>' + num(p.score) + '</b></span>').join('');
    // усі по нулях — п'єдестал із нулів смішить не так, як треба
    const scored = rows.some((p) => p.score > 0);
    stage.innerHTML = (r.early ? '<div class="dt-early" data-pad-focus>🚪 Партію перервано: за столом лишилось менше трьох</div>' : '')
      + (scored ? '<div class="dt-podium big"' + (r.early ? '' : ' data-pad-focus') + '>' + podium + '</div>' + (rest ? '<div class="dt-rest">' + rest + '</div>' : '')
      : r.early ? '' : '<div class="dt-watch" data-pad-focus>Цього разу ніхто не набрав жодного очка 🤷</div>')
      + ((r.best || []).length ? '<div class="dt-besttitle">😂 Найдотепніше партії</div><div class="dt-bestlist">'
        + r.best.map((b) => bestHtml(ctx, v, b, '')).join('') + '</div>' : '')
      + (v.say && v.say.text ? '<div class="dt-say"><img src="/static/glek.svg" alt=""><span>' + ctx.esc(v.say.text) + '</span></div>' : '')
      + '<div class="dt-again muted small">' + ((v.players || []).filter((p) => !p.left).length >= 3
        ? 'Ще партію? Тисни «Ану ще раз» — завдання будуть нові' : 'На «Ану ще раз» треба щонайменше троє — клич друзів') + '</div>';
  }

  // =============================================================================================
  // голос Глека й звуки
  // =============================================================================================
  // Звучить лише там, де ввімкнено «🔊 Глек тут»: типово — у глядача (телевізор) і в господаря столу, щоб не
  // лунало з восьми телефонів разом. Радіо на час репліки глушимо, як у «Своїй грі».

  function speakerOn(ctx) {
    let saved = null;
    try { saved = localStorage.getItem(SPK_KEY); } catch { /* приватне вікно */ }
    if (saved === '1') return true;
    if (saved === '0') return false;
    const host = ctx.room && ctx.me && String(ctx.room.host || '').toLowerCase() === String(ctx.me.nick || '').toLowerCase();
    return ctx.seat == null || !!host;
  }

  function radio() { return document.getElementById('audio'); }

  function duck(s, on) {
    const r = radio();
    if (!r) return;
    if (on && s.radioMuted == null) { s.radioMuted = r.muted; r.muted = true; }
    if (!on && s.radioMuted != null) { r.muted = s.radioMuted; s.radioMuted = null; }
  }

  function hush(root) {
    const s = st(root);
    clearTimeout(s.later);
    s.later = 0;
    s.next = null;
    s.line = (s.line || 0) + 1;      // обірвана репліка ще може озватись (onerror після cancel) — її кінець не наш
    const a = root.querySelector('.dt-voice');
    if (a && !a.paused) a.pause();
    if (window.speechSynthesis && s.speaking) { try { speechSynthesis.cancel(); } catch { /* нема */ } }
    s.speaking = false;
    s.reading = false;
    duck(s, false);
  }

  function spoke(root) {
    const s = st(root);
    s.speaking = false;
    s.reading = false;
    const next = s.next;
    s.next = null;
    const ctx = root._ctx;
    if (next && ctx && speakerOn(ctx)) { playLater(root, next.line, next.delay); return; }
    duck(s, false);
  }

  function ukVoice() {
    if (!window.speechSynthesis) return null;
    return speechSynthesis.getVoices().find((x) => /^uk/i.test(x.lang)) || null;
  }

  function voice(root, ctx, v) {
    const s = st(root);
    if (v.voice === 'none') { if (s.speaking) hush(root); return; }
    const line = v.say;
    if (!line || line.id === s.sayId) return;
    s.sayId = line.id;
    if (!speakerOn(ctx) || !(ctx.playing || v.phase === 'done')) return;
    // читання картки — одразу, перебиваючи все; вердикт — після читання і коли голоси «прилетіли»
    if (v.phase === 'vote') { hush(root); play(root, line, true); return; }
    const delay = v.phase === 'reveal' && !v.final ? VERDICT_DELAY : 0;
    if (s.speaking && s.reading) { s.next = { line, delay }; return; }
    hush(root);
    playLater(root, line, delay);
  }

  function playLater(root, line, delay) {
    const s = st(root);
    clearTimeout(s.later);
    if (!delay) { play(root, line, false); return; }
    s.later = setTimeout(() => { s.later = 0; play(root, line, false); }, delay);
  }

  function play(root, line, reading) {
    const s = st(root);
    const n = s.line = (s.line || 0) + 1;
    const end = () => { if (s.line === n) spoke(root); };
    if (line.url) {
      const a = root.querySelector('.dt-voice');
      a.src = line.url;
      duck(s, true);
      s.speaking = true;
      s.reading = reading;
      a.onended = end;
      a.play().catch(end);
    } else {
      const uk = ukVoice();
      if (!uk) { spoke(root); return; }                 // без українського голосу краще тиша, ніж чужий акцент
      const u = new SpeechSynthesisUtterance(line.text);
      u.voice = uk; u.lang = uk.lang; u.rate = 1.5;      // темп як у Остапа з сервера (+50%)
      u.onend = u.onerror = end;
      duck(s, true);
      s.speaking = true;
      s.reading = reading;
      speechSynthesis.speak(u);
    }
  }

  /// WebAudio з'являється лише після жесту людини (браузер інакше не дасть) і лише коли Глек тут.
  function unlockSound(root) {
    const s = st(root);
    const ctx = root._ctx;
    if (s.ac || !ctx || !speakerOn(ctx) || (ctx.view && ctx.view.voice === 'none')) return;
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;
    try { s.ac = new AC(); } catch { s.ac = null; }
  }

  function beep(s, freq, ms, type, vol) {
    const ac = s.ac;
    if (!ac || ac.state === 'closed') return;
    try {
      const t = ac.currentTime;
      const o = ac.createOscillator();
      const g = ac.createGain();
      o.type = type || 'sine';
      o.frequency.value = freq;
      g.gain.setValueAtTime(vol || 0.05, t);
      g.gain.exponentialRampToValueAtTime(0.0001, t + ms / 1000);
      o.connect(g).connect(ac.destination);
      o.start(t);
      o.stop(t + ms / 1000 + 0.02);
    } catch { /* звук — прикраса */ }
  }

  // =============================================================================================
  // збірка
  // =============================================================================================

  function render(root, ctx) {
    const s = st(root);
    // схована вкладка: стан уже в ctx, малюємо, коли повернуться (visibilitychange)
    if (document.hidden) { s.stale = true; return; }
    s.stale = false;
    const t0 = performance.now();
    const v = ctx.view || {};
    paintTop(root, ctx, v);
    paintPlayers(root, ctx, v);
    paintStage(root, ctx, v);
    voice(root, ctx, v);
    const dt = performance.now() - t0;
    s.stats.n++;
    s.stats.sum += dt;
    if (dt > s.stats.max) s.stats.max = dt;
  }

  /// Картка, на якій зараз ця кімната (для onKey: каркас дає ctx, а не корінь).
  function rootOf(ctx) {
    for (const el of document.querySelectorAll('.dt')) {
      const r = el.parentElement;
      if (r && r._ctx === ctx) return r;
    }
    return null;
  }

  HGames.register({
    id: 'dotepy',
    added: '2026-09-27',
    icon: ICON,
    seatNames: (i) => String(i + 1),
    seatClass: ['dt0', 'dt1', 'dt2', 'dt3', 'dt4', 'dt5', 'dt6', 'dt7'],
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Дотепи',
      items: [
        '✍ Кожному — дурні завдання. Пиши найсмішнішу відповідь одним рядком, поки біжить дуга',
        '🗳 Далі всі голосують за чужі дотепи — анонімно, хто що написав, видно лише після голосування',
        '💥 Троє й більше голосів за одного — «Розгром!» і подвійний бонус; на розкритті тапай «😂»',
        '🏁 «Останній дотеп»: одне завдання на всіх, роздаєш 🥇🥈🥉',
        '🔊 Дядько Глек зачитує завдання й відповіді — тумблер «Глек тут» на картці; глядачі голосують як публіка',
      ],
    },
    pad: { hint: '{dpad} по дотепах · {a} обрати', when: (ctx) => ctx.mine && ctx.playing },

    mount(root, ctx) {
      root._ctx = ctx;
      const s = st(root);
      root.innerHTML = '<div class="dt">'
        + '<div class="dt-top"><span class="dt-pill"></span><span class="dt-arcbox"></span>'
        + '<span class="dt-wait muted small" hidden>Глек прокашлюється…</span>'
        + '<button type="button" class="ghost small dt-spk"></button></div>'
        + '<div class="dt-players"></div>'
        + '<div class="dt-stage"></div>'
        + '<audio class="dt-voice" preload="auto"></audio>'
        + '</div>';
      root.querySelector('.dt-spk').addEventListener('click', () => {
        const c = root._ctx;
        if (!c) return;
        const on = !speakerOn(c);
        try { localStorage.setItem(SPK_KEY, on ? '1' : '0'); } catch { /* приватне вікно */ }
        if (on) unlockSound(root); else hush(root);
        paintTop(root, c, c.view || {});
      });
      s.onDown = () => unlockSound(root);
      root.addEventListener('pointerdown', s.onDown, { passive: true });
      // F5 посеред репліки — стару не повторюємо: звучить лише те, що Глек скаже вже при нас
      s.sayId = (ctx.view && ctx.view.say && ctx.view.say.id) || 0;
      s.onVis = () => { if (!document.hidden && s.stale && root._ctx) render(root, root._ctx); };
      document.addEventListener('visibilitychange', s.onVis);
      root._dtUpdate = () => { if (root._ctx) render(root, root._ctx); };
      render(root, ctx);
    },

    update(root, ctx) {
      root._ctx = ctx;
      render(root, ctx);
    },

    unmount(root) {
      const s = root._dt;
      if (!s) return;
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      hush(root);
      Object.values(s.draftTimers).forEach((t) => clearTimeout(t));
      s.timers.forEach((t) => clearTimeout(t));
      s.timers.clear();
      document.removeEventListener('visibilitychange', s.onVis);
      root.removeEventListener('pointerdown', s.onDown);
      if (s.ac) { try { s.ac.close(); } catch { /* уже */ } }
      root._dt = null;
      root._ctx = null;
    },

    onKey(e, ctx) {
      const v = ctx.view || {};
      if (!ctx.playing || lobbyOf(ctx, v) || v.phase === 'done') return false;
      if (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName || '')) return false;
      const m = /^(?:Digit|Numpad)([1-9])$/.exec(e.code || '');
      if (m) {
        // Поки партія жива, цифри — наші в будь-якій фазі: запізніле «1» після голосування (у написанні нового раунду,
        // на підсумку) інакше провалювалось у гарячі клавіші сайту (1/2/3 — розділи) і ховало гру.
        const root = rootOf(ctx);
        const i = +m[1] - 1;
        if (root && v.card && i < v.card.answers.length) {
          if (v.phase === 'vote') pick(root, i);
          else if (v.phase === 'reveal') laugh(root, i);
        }
        return true;
      }
      if (e.key === 'Backspace' && ctx.mine && v.card && v.card.ranked && v.phase === 'vote') {
        const root = rootOf(ctx);
        const picks = root ? myPicks(root, v) : [];
        if (picks.length > 1) send(root, ctx, v, picks.slice(0, -1));
        return true;
      }
      return false;
    },

    status(ctx) {
      const v = ctx.view || {};
      // розійшлись посеред партії — це не «Нічия» (так каркас пише, коли переможців нема), а перервана партія
      if (v.phase === 'done' && v.result && v.result.early && !(v.result.winners || []).length) return 'Партію перервано — розійшлись';
      if (!ctx.playing || lobbyOf(ctx, v)) return ctx.room && ctx.room.status === 'lobby' ? 'Господар тисне «Почати» — треба щонайменше троє' : '';
      const me = v.me || {};
      switch (v.phase) {
        case 'write': {
          if (!ctx.mine) return 'Пишуть дотепи…';
          const tasks = me.tasks || [];
          const done = tasks.filter((t) => t.done).length;
          if (!tasks.length) return 'Чекаємо на решту';
          if (done < tasks.length) return 'Пиши дотепи · здано ' + done + ' з ' + tasks.length;
          const players = (v.players || []).filter((p) => !p.left);
          return 'Здано! Чекаємо решту (' + players.filter((p) => p.ready).length + ' з ' + players.length + ')';
        }
        case 'vote':
          if (v.waiting) return 'Глек прокашлюється…';
          if (!ctx.mine) return 'Голосуй як публіка 👀';
          if (!me.voter) return 'Твій дотеп у грі — тримай кулаки';
          if (v.card && v.card.ranked) {
            const n = (me.picks || []).length;
            if (!n) return 'Роздай ' + MEDALS.slice(0, v.card.perVoter).join('') + ' найдотепнішим';
            if (n < v.card.perVoter) return 'Ще ' + MEDALS.slice(n, v.card.perVoter).join('') + ' — кому?';
          }
          return (me.picks || []).length ? 'Голос є — чекаємо решту' : 'Голосуй за найдотепніше';
        case 'reveal': return v.card && v.card.jinx ? 'Думки сходяться!' : 'Розкриття…';
        // заголовок сцени вже каже «Раунд N позаду» — статус каже, що далі
        case 'table': return v.round + 1 >= v.rounds ? 'Далі — Останній дотеп 🏁' : 'Далі — раунд ' + (v.round + 1);
      }
      return '';
    },
  });
})();
