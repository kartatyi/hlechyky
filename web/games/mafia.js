/*
  Мафія. Уся сіль гри — у тому, чого людині НЕ показують, тому модуль нічого не додумує: малює рівно те,
  що прийшло у виді для його місця. Ролі, голоси й нічний чат сервер кладе у вид вибірково (гра Hidden),
  і якщо чогось у виді нема — його нема й на екрані.

  Вид із сервера (Impl/Mafia.cs):
    { phase, day, endsAt, phaseMs, rules: {...}, players: [{seat, nick, alive, role}], me: {role, alive}|null,
      night: { votes, chat, myCheck, healed, blocked, stab }|null, dayInfo: {killed, saved, fallen}|null,
      votes: {seat: seat|null}, voted: [seat], log: [], result: {winners, team}|null }
  Кадр (летить усій кімнаті, тому публічний): { phase, day, endsAt, phaseMs, alive: [] }.

  Тривалості фаз більше не константа: стіл вибирає темп, і дуга-таймер крутиться рівно стільки, скільки
  сказав сервер (phaseMs). Список унизу лишається тільки як запасний варіант для старого кадра.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2 9h12" stroke="var(--accent)" stroke-width="1.6" stroke-linecap="round" fill="none"/>'
    + '<path d="M4.5 9c0-3 1.4-4.6 3.5-4.6S11.5 6 11.5 9" stroke="var(--accent)" stroke-width="1.6" fill="none"/>'
    + '<circle cx="5.6" cy="12" r="1.6" fill="var(--muted)"/><circle cx="10.4" cy="12" r="1.6" fill="var(--muted)"/>'
    + '</svg>';

  // Запасні тривалості (спокійний темп) — якщо сервер старий і phaseMs у кадрі ще нема.
  const PHASE_MS = { intro: 10000, night: 45000, day: 95000, vote: 45000 };
  const PHASE_TITLE = { lobby: 'Збираємось', intro: 'Знайомство', night: 'Ніч', day: 'День', vote: 'Голосування', done: 'Кінець' };
  const ROLE = {
    mafia: { title: 'Мафія', cls: 'mf-r-mafia' },
    don: { title: 'Дон', cls: 'mf-r-don' },
    sheriff: { title: 'Комісар', cls: 'mf-r-sheriff' },
    doctor: { title: 'Лікар', cls: 'mf-r-doctor' },
    maniac: { title: 'Маньяк', cls: 'mf-r-maniac' },
    kuma: { title: 'Кума', cls: 'mf-r-kuma' },
    civil: { title: 'Мирний', cls: 'mf-r-civil' },
  };
  /// Картка ролі на знайомстві: хто ти й чого хочеш — новачкові без цього нема за що вхопитись.
  const ROLE_CARD = {
    mafia: { icon: '🔪', text: 'Уночі разом зі своїми обираєте, хто не прокинеться, а вдень ти — найчесніший мирний у селі. Виграєте, коли вас не менше, ніж чесних.' },
    don: { icon: '🎩', text: 'Голова мафії: уночі твоє слово вирішальне, а комісару ти здаєшся мирним. Удень — ні пари з вуст.' },
    sheriff: { icon: '🔎', text: 'Щоночі перевіряєш одного: мафія чи ні. Удень переконай село — але обережно, мафія шукає саме тебе.' },
    doctor: { icon: '💊', text: 'Щоночі рятуєш одного від ножа. Двічі поспіль ту саму людину не можна. Граєш за мирних.' },
    maniac: { icon: '🪓', text: 'Сам проти всіх: ріжеш щоночі й виграєш, коли лишишся сам або з одним-єдиним мирним.' },
    kuma: { icon: '🥧', text: 'Уночі йдеш у гості: до кого зайшла, той цієї ночі нічого не встигне. Граєш за мирних.' },
    civil: { icon: '🌾', text: 'Уночі спиш, удень шукаєш мафію й голосуєш. Твоя зброя — язик і пильне око.' },
  };
  const TEAM = { mafia: 'Перемогла мафія', civil: 'Перемогли мирні', maniac: 'Переміг маньяк', draw: 'Нічия' };
  const PACE = { calm: 'спокійний темп', fast: 'швидкий темп', slow: 'неспішний темп' };

  const roleTitle = (r) => (ROLE[r] ? ROLE[r].title : '');
  const roleCls = (r) => (ROLE[r] ? ROLE[r].cls : '');
  const phaseTitle = (v) => (PHASE_TITLE[v.phase] || '') + (v.phase === 'night' || v.phase === 'day' ? ' ' + (v.day || 1) : '');
  const isMafia = (r) => r === 'mafia' || r === 'don';
  /// Перша ніч, у яку стіл домовився не проливати крові.
  const quietNight = (v) => !!(v.rules && !v.rules.firstNightKill && v.phase === 'night' && (v.day || 1) === 1);

  /// Дуга-таймер: заводимо на фазу, що йде, і гасимо, щойно партія стала. Схований вузол лишається
  /// в DOM, тож сам цикл HGames.ui.timerArc не спиниться — його треба спинити руками.
  function arcTo(host, v) {
    if (!host) return;
    if (v) { HGames.ui.timerArc(host, v.endsAt, v.phaseMs || PHASE_MS[v.phase] || 45000); return; }
    const el = host.querySelector(':scope > .garc');
    if (el && el._arc) el._arc.stop();
  }

  /// Ніч, за яку вже все зроблено: комісару кнопки більше не потрібні (перевірка одна на ніч).
  const nightKey = (v) => v.phase + ':' + v.day;

  /// Дія, яку моє місце може зробити з місцем p у цій фазі. null — кнопки нема.
  function deed(v, p, mySeat, checked) {
    const me = v.me;
    if (!me || !me.alive || !p.alive) return null;
    if (v.phase === 'vote') return { action: 'vote', label: 'Вигнати' };
    if (v.phase !== 'night') return null;
    if (isMafia(me.role)) return quietNight(v) || isMafia(p.role) ? null : { action: 'kill', label: 'Вбити' };
    if (me.role === 'maniac') {
      // Маньяк сам по собі: б'є кого хоче, крім себе. Тихої першої ночі ніж лишається в халяві.
      return quietNight(v) || p.seat === mySeat ? null : { action: 'kill', label: 'Зарізати' };
    }
    if (me.role === 'sheriff') {
      // Перевірка одна на ніч: показувати кнопку, яку сервер однаково відхилить, — знущання.
      return p.seat === mySeat || checked ? null : { action: 'check', label: 'Перевірити' };
    }
    if (me.role === 'doctor') {
      const self = p.seat === mySeat;
      if (self && v.rules && !v.rules.selfHeal) return null;
      return { action: 'heal', label: 'Врятувати' };
    }
    if (me.role === 'kuma') return p.seat === mySeat ? null : { action: 'block', label: 'У гості' };
    return null;
  }

  /// Що я вже вибрав цієї фази — щоб кнопка світилась, а не губилась.
  function myPick(v, seat) {
    if (v.phase === 'vote') return v.votes && v.votes[seat] != null ? v.votes[seat] : null;
    if (v.phase !== 'night' || !v.night || !v.me) return null;
    if (isMafia(v.me.role)) return v.night.votes && v.night.votes[seat] != null ? v.night.votes[seat] : null;
    if (v.me.role === 'doctor') return v.night.healed == null ? null : v.night.healed;
    if (v.me.role === 'kuma') return v.night.blocked == null ? null : v.night.blocked;
    if (v.me.role === 'maniac') return v.night.stab == null ? null : v.night.stab;
    return null;
  }

  /// Скільки пальців показує на це місце: удень — відкриті голоси села, уночі — вибір мафії.
  function tally(v) {
    const map = {};
    // Уночі рахуємо пальці мафії, удень — відкриті голоси села. Поза цими фазами лічильника нема:
    // нічний вибір лишається в стані до наступної ночі, і показувати його вдень — тільки плутати.
    const night = v.phase === 'night' && v.night && v.me && (isMafia(v.me.role) || !v.me.alive);
    const src = v.phase === 'vote' ? v.votes : (night ? v.night.votes : null);
    for (const k in (src || {})) {
      const t = src[k];
      if (t == null) continue;
      map[t] = (map[t] || 0) + 1;
    }
    return map;
  }

  /// Рядок під шапкою: за якими правилами грає цей стіл. Це не таємниця — усі за столом однакові.
  function rulesLine(v) {
    const r = v.rules;
    if (!r) return '';
    const bits = [PACE[r.pace] || r.pace];
    bits.push(r.mafia + (r.mafia === 1 ? ' мафія' : r.mafia < 5 ? ' мафії' : ' мафій') + (r.don ? ' з доном' : ''));
    if (r.sheriff) bits.push('комісар');
    if (r.doctor) bits.push('лікар' + (r.selfHeal ? '' : ' (себе не рятує)'));
    if (r.maniac) bits.push('маньяк');
    if (r.kuma) bits.push('кума');
    if (!r.openVotes) bits.push('таємні голоси');
    if (!r.reveal) bits.push('ролі не розкривають');
    if (!r.firstNightKill) bits.push('перша ніч тиха');
    return bits.join(' · ');
  }

  function build(root, ctx) {
    const el = document.createElement('div');
    el.className = 'mafia';
    el.innerHTML = '<div class="mf-top">'
      + '<div class="mf-arc"></div>'
      + '<div class="mf-head"><b class="mf-phase"></b><span class="mf-hint muted small"></span></div>'
      + '<span class="mf-me chip"></span>'
      + '</div>'
      + '<div class="mf-rules muted small"></div>'
      + '<div class="mf-players"></div>'
      + '<div class="mf-act"></div>'
      + '<div class="mf-chat" hidden><div class="mf-lines"></div>'
      + '<form class="mf-say"><input class="mf-input" type="text" maxlength="200" placeholder="шепнути своїм…" autocomplete="off">'
      + '<button class="primary" type="submit">Шепнути</button></form></div>'
      + '<details class="mf-logbox"><summary class="muted small">Хроніка села</summary><div class="mf-log"></div></details>';
    root.appendChild(el);

    // Слухачі вішаємо раз, а свіжий ctx кладемо на елемент: інакше клік назавжди пішов би в перший.
    el.querySelector('.mf-players').addEventListener('click', async (e) => {
      const b = e.target.closest('.mf-do');
      if (!b || b.disabled) return;
      const c = el._mfCtx;
      if (!c) return;
      const key = c.view ? nightKey(c.view) : '';
      const r = await c.act(b.dataset.act, { seat: +b.dataset.seat });
      if (r && r.ok && b.dataset.act === 'check') el._checked = key;
    });
    el.querySelector('.mf-say').addEventListener('submit', (e) => {
      e.preventDefault();
      const input = el.querySelector('.mf-input');
      const text = input.value.trim();
      if (!text) return;
      input.value = '';
      const c = el._mfCtx;
      if (c) c.act('say', { text });
    });
    el._mfCtx = ctx;
    return el;
  }

  function paint(root, ctx) {
    const el = root.querySelector(':scope > .mafia') || build(root, ctx);
    el._mfCtx = ctx;
    const v = ctx.view || {};
    if (!v.phase) { el.querySelector('.mf-players').innerHTML = '<div class="gwait">чекаю на село…</div>'; return; }

    // Своє місце каркас кладе в ctx.seat; у глядача воно null, і жодної нічної кнопки він не побачить.
    const mySeat = ctx.seat == null ? null : ctx.seat;

    // ---- шапка ----
    const running = v.phase !== 'lobby' && v.phase !== 'done';
    const arcHost = el.querySelector('.mf-arc');
    arcHost.hidden = !running;
    // Схований вузол лишається в DOM, тож сам цикл rAF не спиниться: дограна картка живе в лобі
    // ще чверть години, і крутити її дугу весь цей час нема за що.
    arcTo(arcHost, running ? v : null);
    el.querySelector('.mf-phase').textContent = phaseTitle(v);
    el.querySelector('.mf-hint').textContent = hint(v);
    const me = el.querySelector('.mf-me');
    me.className = 'mf-me chip' + (v.me ? ' ' + roleCls(v.me.role) : '')
      + (v.me && !v.me.alive ? ' mf-out' : '');
    me.textContent = v.me ? roleTitle(v.me.role) + (v.me.alive ? '' : ' (вибув)')
      : mySeat == null ? 'Дивишся збоку' : 'За столом';
    // Забув, що вміє твоя роль, — наведи на чіп (картка з поясненням була лише на знайомстві).
    const card = v.me && ROLE_CARD[v.me.role];
    if (card) me.title = card.text; else me.removeAttribute('title');
    const rules = el.querySelector('.mf-rules');
    const rulesText = rulesLine(v);
    if (rules.dataset.sig !== rulesText) { rules.dataset.sig = rulesText; rules.textContent = rulesText; }

    // ---- село ----
    const counts = tally(v);
    const checks = {};
    for (const c of (v.night && v.night.myCheck) || []) checks[c.seat] = c.mafia;
    const voted = {};
    for (const s of v.voted || []) voted[s] = true;
    // Таємні голоси: скільки на кого — не наша справа, зате видно, хто вже визначився.
    const secret = v.phase === 'vote' && v.rules && !v.rules.openVotes && !!(v.me && v.me.alive);
    const pick = myPick(v, mySeat);
    const checked = el._checked === nightKey(v);
    const rows = (v.players || []).map((p) => {
      const d = deed(v, p, mySeat, checked);
      const tags = [];
      if (p.role) tags.push('<span class="mf-tag ' + roleCls(p.role) + '">' + ctx.esc(roleTitle(p.role)) + '</span>');
      else if (checks[p.seat] != null) tags.push('<span class="mf-tag ' + (checks[p.seat] ? 'mf-r-mafia' : 'mf-r-civil') + '">'
        + (checks[p.seat] ? 'мафія' : 'не мафія') + '</span>');
      if (secret && voted[p.seat]) tags.push('<span class="mf-count" title="вже визначився">✔</span>');
      else if (!secret && counts[p.seat]) tags.push('<span class="mf-count" title="скільки на нього показують">' + counts[p.seat] + '</span>');
      return '<div class="mf-p' + (p.alive ? '' : ' dead') + (p.seat === mySeat ? ' me' : '') + '">'
        + '<span class="mf-nick">' + ctx.esc(p.nick || ('гравець ' + (p.seat + 1))) + '</span>'
        + '<span class="mf-tags">' + tags.join('') + '</span>'
        + (d ? '<button class="mf-do' + (pick === p.seat ? ' on' : '') + '" data-act="' + d.action + '" data-seat="' + p.seat + '">'
          + d.label + '</button>' : '')
        + '</div>';
    }).join('');
    const box = el.querySelector('.mf-players');
    if (box.dataset.sig !== rows) { box.dataset.sig = rows; box.innerHTML = rows; }

    // ---- що робити зараз ----
    const act = el.querySelector('.mf-act');
    const actHtml = advice(v, ctx);
    // Картка ролі на знайомстві — одразу під шапкою, а не під списком села: на телефоні з дюжиною гравців
    // її інакше довелось би шукати прокруткою.
    act.classList.toggle('mf-first', v.phase === 'intro');
    if (act.dataset.sig !== actHtml) {
      act.dataset.sig = actHtml;
      act.innerHTML = actHtml;
      const toChat = act.querySelector('[data-chat]');
      if (toChat) toChat.onclick = () => HGames.openTable();
      const skip = act.querySelector('[data-skip]');
      if (skip) skip.onclick = () => ctx.act('vote', { seat: null });
    }

    // ---- нічний чат: лише мафії й тим, хто вже вибув ----
    const chat = el.querySelector('.mf-chat');
    const canWhisper = !!(v.me && isMafia(v.me.role) && v.me.alive && v.phase === 'night');
    const sawChat = !!(v.night && v.me && (isMafia(v.me.role) || !v.me.alive));
    // Порожню скриньку показуємо лише тоді, коли в неї є що покласти: інакше вона займає місце дарма.
    chat.hidden = !sawChat || (!canWhisper && !((v.night.chat || []).length));
    if (!chat.hidden) {
      const lines = (v.night.chat || []).map((c) =>
        '<div class="mf-line"><b>' + ctx.esc(nickOf(v, c.seat)) + '</b> ' + ctx.esc(c.text) + '</div>').join('')
        || '<div class="muted small">Поки що тихо.</div>';
      const host = el.querySelector('.mf-lines');
      if (host.dataset.sig !== lines) {
        host.dataset.sig = lines;
        host.innerHTML = lines;
        host.scrollTop = host.scrollHeight;
      }
      el.querySelector('.mf-say').hidden = !canWhisper;
    }

    // ---- хроніка ----
    const log = (v.log || []).map((l) => '<div>' + ctx.esc(l) + '</div>').join('') || '<div class="muted small">Ще нічого не сталось.</div>';
    const logBox = el.querySelector('.mf-log');
    if (logBox.dataset.sig !== log) { logBox.dataset.sig = log; logBox.innerHTML = log; }
    // Партію зіграно — хроніку розгортаємо самі: «а як воно було» — перше, що всі хочуть побачити.
    // Лише раз на партію, щоб не розгортати те, що людина згорнула руками.
    const logDetails = el.querySelector('.mf-logbox');
    const endKey = v.phase === 'done' ? 'done:' + (v.log || []).length : '';
    if (endKey && el._logOpened !== endKey) { el._logOpened = endKey; logDetails.open = true; }
  }

  const nickOf = (v, seat) => {
    const p = (v.players || []).find((x) => x.seat === seat);
    return (p && p.nick) || ('гравець ' + (seat + 1));
  };

  function hint(v) {
    // Ранок буває тихий не лише тому, що лікар устиг: мафія могла й не назвати нікого. Сервер у
    // хроніці ці випадки навмисне не розрізняє — не розрізняє їх і шапка.
    if (v.phase === 'day' && v.dayInfo) {
      const fallen = v.dayInfo.fallen || (v.dayInfo.killed != null ? [v.dayInfo.killed] : []);
      if (fallen.length >= 2) return fallen.map((s) => nickOf(v, s)).join(' і ') + ' не прокинулись';
      if (fallen.length === 1) return nickOf(v, fallen[0]) + ' не прокинувся';
      return 'уночі всі вціліли';
    }
    // Хто переміг, на дограній картці вже каже великий рядок унизу — у шапці вдруге не повторюємо.
    return '';
  }

  /// Рядок «що зараз робити» — головна підказка картки, бо правила гри тримає сервер.
  function advice(v, ctx) {
    const chat = '<button class="ghost" data-chat>💬 До суперечки</button>';
    if (v.phase === 'lobby') {
      return '<span class="muted small">Чекаємо, поки господар почне. Треба щонайменше троє'
        + ' (утрьох — коротка партія: перша ніч тиха, і все вирішує один день).</span>';
    }
    if (v.phase === 'done') {
      const t = v.result ? (TEAM[v.result.team] || '') : '';
      const icon = { mafia: '🔪', civil: '🌾', maniac: '🪓', draw: '🤝' }[v.result && v.result.team] || '';
      return '<span class="mf-final">' + (icon ? icon + ' ' : '') + ctx.esc(t) + '</span>';
    }
    if (!v.me) return '<span class="muted small">Дивишся збоку: ролі й нічні справи тобі не покажуть, а поки йде партія — глядачі за столом мовчать.</span>';
    if (!v.me.alive) return '<span class="muted small">Тебе вже нема серед живих. Дивись усе й читай суперечку, але слова тобі за столом до кінця партії не дадуть.</span>';
    if (v.phase === 'intro') {
      const card = ROLE_CARD[v.me.role];
      const trio = (v.players || []).length === 3;
      if (!card) return '<span class="muted small">Запам\'ятай, хто ти. Село ось-ось засне.</span>';
      return '<div class="mf-card ' + roleCls(v.me.role) + '"><div class="mf-card-icon" aria-hidden="true">' + card.icon + '</div>'
        + '<div class="mf-card-body"><b>Ти — ' + ctx.esc(roleTitle(v.me.role).toLowerCase()) + '</b>'
        + '<span>' + ctx.esc(card.text) + '</span>'
        + (trio ? '<span class="muted small">Утрьох: перша ніч тиха, а вдень одне голосування вирішує все.</span>' : '')
        + '<span class="muted small">Запам\'ятай і нікому не кажи. Село ось-ось засне.</span></div></div>';
    }
    if (v.phase === 'night') {
      const quiet = quietNight(v);
      if (isMafia(v.me.role)) {
        if (quiet) return '<span class="muted small">Перша ніч тиха: знайомтесь пошепки, ножі — завтра.</span>';
        return '<span class="muted small">Домовляйтесь пошепки й показуйте на жертву.'
          + (v.me.role === 'don' ? ' Твоє слово вирішальне, і комісару ти показуєшся мирним.' : '') + '</span>';
      }
      if (v.me.role === 'maniac') {
        return quiet
          ? '<span class="muted small">Перша ніч тиха. Придивляйся, кого різатимеш завтра.</span>'
          : '<span class="muted small">Ти сам проти всіх: ні мафія, ні село тобі не свої.</span>';
      }
      if (v.me.role === 'sheriff') return '<span class="muted small">Одна перевірка за ніч. Обирай уважно.</span>';
      if (v.me.role === 'doctor') {
        return '<span class="muted small">Кого рятуєш?'
          + (v.rules && v.rules.selfHeal ? ' Себе можна, але не двічі поспіль ту саму людину.' : ' Себе — не можна, і не двічі поспіль ту саму людину.')
          + '</span>';
      }
      if (v.me.role === 'kuma') return '<span class="muted small">Іди в гості: до кого зайдеш, той цієї ночі нічого не встигне. Двічі поспіль в одну хату не ходять.</span>';
      return '<span class="muted small">Спи. Уночі за тебе працюють інші.</span>';
    }
    if (v.phase === 'day') return '<span class="muted small">Сперечайтесь у балачці столу — картка лише рахує час.</span>' + chat;
    const secret = v.rules && !v.rules.openVotes;
    return '<span class="muted small">Тисни «Вигнати» біля когось. Передумати можна до кінця.'
      + (secret ? ' Голоси таємні: видно лише, хто вже визначився.' : '') + '</span>'
      + '<button class="ghost" data-skip>Утриматись</button>' + chat;
  }

  HGames.register({
    id: 'mafia',
    news: {
      v: '2026-09-24b',
      title: 'Мафія: суперечка — за столом',
      items: [
        '💬 Село сперечається в балачці столу, а не в загальних Балачках: там і Глек-ведучий, і кнопка «До суперечки» на картці',
        '🤐 За столом мертві й глядачі тепер справді мовчать, поки йде партія. До старту й після — балакають усі',
        '👥 Стіл від трьох: мафіозі, комісар і мирний. Перша ніч тиха, а вдень мирний вирішує, котрий «комісар» справжній',
        '🃏 На знайомстві — картка твоєї ролі: хто ти й чого хочеш (і підказка на чіпі ролі до кінця партії)',
      ],
    },
    // Розмова тут і є гра: балачку столу каркас розгортає сам, щойно людина підійшла до столу.
    talk: 'main',
    icon: ICON,
    mount(root, ctx) { build(root, ctx); paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },

    /// Кадр публічний і короткий — оновлюємо ним лише шапку, решта прийде з видом.
    frame(root, ctx, f) {
      const el = root.querySelector(':scope > .mafia');
      if (!el || !f || !f.phase) return;
      const running = f.phase !== 'lobby' && f.phase !== 'done';
      const arc = el.querySelector('.mf-arc');
      arc.hidden = !running;
      arcTo(arc, running ? f : null);
      el.querySelector('.mf-phase').textContent = phaseTitle(f);
    },

    unmount(root) {
      arcTo(root.querySelector('.mf-arc'), null);
    },

    status(ctx) {
      // Фазу беремо з кадра — він приходить першим. Але після «Ще раз» кадр ще з минулої партії
      // (нового не буде до зміни фази), тому «кінець» у кадрі посеред живої партії ігноруємо.
      const f = ctx.frame || {};
      // Кімната вже не грає — а кадру з 'done' могло й не бути: партію, яку скінчив чийсь вихід
      // з-за столу, завершує не тик, тож у кадрі так і лишилась учорашня фаза. Тоді віримо виду.
      const over = !ctx.room || ctx.room.status !== 'playing';
      const src = f.phase && !over && !(ctx.playing && f.phase === 'done') ? f : (ctx.view || {});
      if (!src.phase || src.phase === 'done' || src.phase === 'lobby') return '';
      return phaseTitle(src);
    },
  });
})();
