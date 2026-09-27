/*
  «Дотепи» — Quiplash по-нашому (specs/dotepy.md). Клієнт нічого не вирішує: фази, час, голоси й очки живуть на
  сервері (Impl/Dotepy.cs), модуль лише малює те, що прийшло, і шле наміри.

  Вид (подія 'room', свій для кожного місця — гра Hidden; кадрів нема):
    { phase: 'lobby'|'write'|'vote'|'reveal'|'table'|'done', round, rounds, final, mode: 'duel'|'all',
      endsAt, totalMs, waiting, voice, players: [{ seat, nick, score, ready, voted, left }], prompts: string[],
      me: null | { tasks: [{ i, prompt, text, done }], mine: number[], voter, picks: number[] },
      card: null | { i, of, prompt, answers: [{ text, stock, seat, votes, medals, jury, points, rank, prize }],
                     voters, voted, juryVotes, perVoter, ranked, sweep, shown },
      say: null | { id, text, url, seconds }, table: null | { rows, best }, result: null | { winners, scores, best } }
  Ходи: Input('draft', { i, text }) · Act('answer', { i, text }) · Act('edit', { i }) · Act('vote', { card, picks }).
  Глядач голосує як публіка: POST /api/games/dotepy/jury { room, card, pick }.

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

  const reduced = () => !!(window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches);

  function st(root) {
    if (!root._dt) root._dt = {
      keys: {}, sayId: 0, speaking: false, reading: false, next: null, later: 0, line: 0, radioMuted: null,
      drafts: {}, draftTimers: {}, local: null, jury: null, ac: null, stale: false, stats: { n: 0, sum: 0, max: 0 },
    };
    return root._dt;
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
    const voters = (v.card && v.card.voters) || [];
    const html = list.map((p) => {
      const mark = p.left ? '🚪'
        : writing ? (p.ready ? '✓' : '✍')
        : voting ? (p.voted ? '✓' : voters.includes(p.seat) ? '🤔' : '🎭')
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
    if (lobbyOf(ctx, v)) return 'lobby';
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
      s.keys = { stage: key };
      s.local = null;
      stage.className = 'dt-stage dt-' + key.split('|')[0];
      if (key === 'lobby') stage.innerHTML = lobbyHtml();
      else if (key.startsWith('write')) buildWrite(root, ctx, v, stage);
      else if (key.startsWith('card')) buildCard(root, ctx, v, stage);
      else if (key.startsWith('table')) buildTable(root, ctx, v, stage);
      else if (key.startsWith('done')) buildDone(root, ctx, v, stage);
      else stage.innerHTML = '';
    }
    if (key.startsWith('write')) refreshWrite(root, ctx, v, stage);
    else if (key.startsWith('card')) refreshCard(root, ctx, v, stage);
  }

  function lobbyHtml() {
    return '<div class="dt-how">'
      + '<div class="dt-howrow"><b>✍</b><span><i>Пиши.</i> Кожному — дурне завдання. Найсмішніша відповідь одним рядком.</span></div>'
      + '<div class="dt-howrow"><b>🗳</b><span><i>Голосуй.</i> Відповіді виходять анонімно — обирай найдотепнішу чужу.</span></div>'
      + '<div class="dt-howrow"><b>🎭</b><span><i>Дивись, хто це написав.</i> Голос — 100 очок, у другому раунді — 200. Усі за одного — «Розгром!»</span></div>'
      + '<div class="dt-howsmall muted small">Троє й більше. Дядько Глек зачитує все вголос; глядачі голосують як публіка 👀</div>'
      + '</div>';
  }

  // ---------- написання ----------

  function buildWrite(root, ctx, v, stage) {
    const s = st(root);
    const tasks = (v.me && v.me.tasks) || [];
    let html = '';
    if (v.say && v.say.text) html += '<div class="dt-say intro"><img src="/static/glek.svg" alt=""><span>' + ctx.esc(v.say.text) + '</span></div>';
    if (v.me) {
      html += tasks.map((t, n) => '<div class="dt-task" data-i="' + t.i + '" style="--n:' + n + '">'
        + '<div class="dt-prompt">' + ctx.esc(t.prompt) + '</div>'
        + '<form class="dt-form"><input class="dt-in" type="text" maxlength="' + MAX + '" autocomplete="off" spellcheck="true"' + (n === 0 ? ' data-pad-first' : '')
        + ' enterkeyhint="send" placeholder="твій дотеп…" aria-label="Відповідь на завдання ' + (n + 1) + '">'
        + '<button class="primary dt-send" type="submit">Здати</button></form>'
        + '<div class="dt-meta"><span class="dt-cnt">0/' + MAX + '</span></div>'
        + '<div class="dt-given"><span class="dt-giventxt"></span><span class="dt-ok">✓ Здано</span>'
        + '<button type="button" class="ghost small dt-edit">Змінити</button></div>'
        + '</div>').join('');
      if (!tasks.length) html += '<div class="gempty">Цього раунду тобі завдань нема — дивись і чекай голосування</div>';
    } else {
      html += '<div class="dt-watch">Пишуть дотепи… 🤫 Скоро відповіді вийдуть на голосування — голосуй як публіка 👀</div>';
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
    for (const t of tasks) {
      const box = stage.querySelector('.dt-task[data-i="' + t.i + '"]');
      if (!box) continue;
      const input = box.querySelector('.dt-in');
      if (box.classList.contains('done') !== t.done) {
        box.classList.toggle('done', t.done);
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
  }

  // ---------- картка: голосування й розкриття ----------

  function buildCard(root, ctx, v, stage) {
    const c = v.card;
    const k = c.answers.length;
    stage.innerHTML = '<div class="dt-cardhead"><span class="dt-of">' + (v.final ? 'Одне завдання — на всіх' : 'Картка ' + (c.i + 1) + ' з ' + c.of) + '</span>'
      + '<span class="dt-hint muted small"></span></div>'
      + '<div class="dt-prompt big">' + ctx.esc(c.prompt) + '</div>'
      + (v.final ? '<div class="dt-podium mini" hidden></div>' : '')
      + '<div class="dt-answers ' + (v.final ? 'final' : v.mode) + (k > 4 ? ' dense' : '') + ' n' + k + '">'
      + c.answers.map((a, i) => '<button type="button" class="dt-ans' + (a.stock ? ' stock' : '') + '" data-i="' + i + '" style="--n:' + i + '"' + (i === 0 ? ' data-pad-first' : '') + '>'
        + '<span class="dt-n">' + (i + 1) + '</span><span class="dt-txt">' + ctx.esc(a.text) + '</span>'
        + '<span class="dt-badge"></span><span class="dt-res"></span></button>').join('')
      + '</div>'
      + '<div class="dt-foot"><span class="dt-voted"></span><span class="dt-jury"></span></div>'
      + '<div class="dt-say" hidden></div>';
    stage.querySelectorAll('.dt-ans').forEach((b) => b.addEventListener('click', (e) => {
      if (!HGames.ui.human(e)) return;
      pick(root, +b.dataset.i);
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
    const hintText = reveal ? (v.final && c.shown < c.answers.length ? 'Розкриваємо з кінця…' : '')
      : !ctx.mine ? 'Тапни — голос публіки 👀'
      : !me.voter ? 'Твій дотеп у грі — тримай кулаки'
      : c.ranked ? 'Тапай по черзі: 🥇 → 🥈' + (c.perVoter > 2 ? ' → 🥉' : '') + ' · цифри 1–' + c.answers.length
      : 'Обери найдотепніше · цифри 1–' + c.answers.length;
    if (hint.textContent !== hintText) hint.textContent = hintText;

    const buttons = stage.querySelectorAll('.dt-ans');
    let best = -1;
    let bestPts = 0;
    c.answers.forEach((a, i) => { if (a.points != null && a.points > bestPts) { bestPts = a.points; best = i; } });
    const allOpen = !v.final || c.shown >= c.answers.length;
    buttons.forEach((b, i) => {
      const a = c.answers[i];
      if (!a) return;
      const own = mine.includes(i);
      const rank = picks.indexOf(i);
      b.classList.toggle('mine', own);
      b.classList.toggle('off', !reveal && ctx.mine && !me.voter && !own);
      b.classList.toggle('picked', rank >= 0 || jury === i);
      b.classList.toggle('hidden', reveal && v.final && a.seat == null);
      b.disabled = reveal || own || (ctx.mine && !me.voter) || !ctx.playing;
      const badge = own ? 'твій' : rank >= 0 ? (c.ranked ? MEDALS[rank] : '✓') : jury === i ? '👀' : '';
      const bEl = b.querySelector('.dt-badge');
      if (bEl.textContent !== badge) bEl.textContent = badge;
      if (reveal && a.seat != null && !b.classList.contains('open')) openAnswer(root, ctx, v, b, a);
      b.classList.toggle('win', reveal && allOpen && i === best);
    });

    const voted = stage.querySelector('.dt-voted');
    const vText = reveal ? '' : (c.voted || []).length
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
    if (reveal && c.sweep != null && !s.keys.sweep) {
      s.keys.sweep = true;
      const b = buttons[c.sweep];
      if (b) sweep(root, b);
    }
  }

  /// Розкриття однієї відповіді: голоси прилітають чіпами, очки вистрибують, автор виїжджає знизу.
  function openAnswer(root, ctx, v, b, a) {
    const s = st(root);
    const res = b.querySelector('.dt-res');
    const votes = a.votes || [];
    const chips = votes.map((seat, n) => '<i class="dt-vote" style="--c:' + col(seat) + ';--d:' + (n * 80) + 'ms" title="'
      + ctx.esc(nickOf(ctx, v, seat)) + '">' + (a.medals ? MEDALS[(a.medals[n] || 1) - 1] : (seat + 1)) + '</i>').join('');
    res.innerHTML = '<span class="dt-votes">' + (chips || '<small class="muted">без голосів</small>') + '</span>'
      + '<span class="dt-pts' + (a.points ? '' : ' zero') + '">' + (a.points ? '+' + num(a.points) : '0') + '</span>'
      + (a.prize ? '<span class="dt-prize">👀 +' + (v.final ? 200 : 100) + '</span>' : '')
      + '<span class="dt-author" style="--c:' + col(a.seat) + '"><i class="dt-dot"></i>' + ctx.esc(nickOf(ctx, v, a.seat))
      + (a.stock ? ' <small>· підставна</small>' : '') + '</span>';
    b.classList.add('open');
    if (!reduced()) votes.forEach((_, n) => setTimeout(() => beep(s, 520 + n * 40, 30, 'triangle', 0.03), 400 + n * 80));
  }

  function sweep(root, b) {
    const s = st(root);
    b.classList.add('sweep');
    const rib = document.createElement('span');
    rib.className = 'dt-sweep';
    rib.textContent = '💥 Розгром!';
    b.appendChild(rib);
    if (!reduced()) {
      const box = document.createElement('span');
      box.className = 'dt-confetti';
      let html = '';
      for (let n = 0; n < 24; n++) {
        const x = (n * 37) % 100, d = (n * 53) % 600, r = ((n * 71) % 120) - 60;
        html += '<span style="--x:' + x + '%;--d:' + d + 'ms;--r:' + r + 'deg">' + CONFETTI[n % CONFETTI.length] + '</span>';
      }
      box.innerHTML = html;
      b.appendChild(box);
      setTimeout(() => box.remove(), 2600);
    }
    [523, 659, 784].forEach((f, n) => setTimeout(() => beep(s, f, 120, 'triangle', 0.04), 1000 + n * 90));
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
    stage.innerHTML = '<div class="dt-tabtitle">Раунд ' + v.round + ' позаду</div>'
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
    stage.innerHTML = (scored ? '<div class="dt-podium big">' + podium + '</div>' + (rest ? '<div class="dt-rest">' + rest + '</div>' : '')
      : '<div class="dt-watch">Цього разу ніхто не набрав жодного очка 🤷</div>')
      + ((r.best || []).length ? '<div class="dt-besttitle">😂 Найдотепніше партії</div><div class="dt-bestlist">'
        + r.best.map((b) => bestHtml(ctx, v, b, '')).join('') + '</div>' : '')
      + (v.say && v.say.text ? '<div class="dt-say"><img src="/static/glek.svg" alt=""><span>' + ctx.esc(v.say.text) + '</span></div>' : '')
      + '<div class="dt-again muted small">' + ((v.players || []).filter((p) => !p.left).length >= 3
        ? 'Ще партію? Тисни «Ще раз» — завдання будуть нові' : 'На «Ще раз» треба щонайменше троє — клич друзів') + '</div>';
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
    icon: ICON,
    seatNames: (i) => String(i + 1),
    seatClass: ['dt0', 'dt1', 'dt2', 'dt3', 'dt4', 'dt5', 'dt6', 'dt7'],
    news: {
      v: '2026-09-27',
      title: 'Нова гра: Дотепи',
      items: [
        '✍ Кожному — по два дурних завдання. Пиши найсмішнішу відповідь, 90 секунд',
        '🗳 Далі всі голосують за чужі дотепи — анонімно. Голос = 100 очок, у другому раунді — 200',
        '💥 Усі голоси за одного — «Розгром!» і подвійний бонус',
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
      root.addEventListener('pointerdown', () => unlockSound(root), { passive: true });
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
      document.removeEventListener('visibilitychange', s.onVis);
      if (s.ac) { try { s.ac.close(); } catch { /* уже */ } }
      root._dt = null;
      root._ctx = null;
    },

    onKey(e, ctx) {
      const v = ctx.view || {};
      if (!ctx.playing || !v.card || (v.phase !== 'vote' && v.phase !== 'reveal')) return false;
      if (e.target && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName || '')) return false;
      const root = rootOf(ctx);
      if (!root) return false;
      const m = /^(?:Digit|Numpad)([1-8])$/.exec(e.code || '');
      if (m) {
        // Цифру з'їдаємо і тоді, коли голосування щойно скінчилось: інакше запізніле «3» провалилось би в
        // гарячі клавіші сайту (1/2/3 — розділи) і викинуло б людину з-за столу посеред розкриття.
        const i = +m[1] - 1;
        if (v.phase === 'vote' && i < v.card.answers.length) pick(root, i);
        return true;
      }
      if (e.key === 'Backspace' && ctx.mine && v.card.ranked) {
        const picks = myPicks(root, v);
        if (picks.length > 1) send(root, ctx, v, picks.slice(0, -1));
        return true;
      }
      return false;
    },

    status(ctx) {
      const v = ctx.view || {};
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
        case 'reveal': return 'Розкриття…';
        case 'table': return 'Раунд ' + v.round + ' позаду';
      }
      return '';
    },
  });
})();
