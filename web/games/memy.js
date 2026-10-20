/*
  «Мемологія» (memy — стіл, memy-solo — тренування). Меми, кадри, час, варіанти й очки — на сервері (Impl/Memy.cs);
  модуль показує картинку, приймає відповідь і гарно розкриває, що то був за мем. Контракт — docs/games/specs/memy.md §7.

  Вид (подія 'room'; гра Hidden — свій кожному місцю, глядачеві me = null):
    { phase: 'loading'|'play'|'reveal'|'done', round, rounds, seconds, until, totalMs, what, answerMode, show,
      mode: 'name'|'phrase'|null, frame: 0|1|2, image: '/api/games/memy/<токен>.jpg'|null, nextFrameAt,
      canWrite, choices: string[]|null, choicesAt, choicesOpen,
      me: null | { text, how: 'write'|'pick', hit, blocked, points, frame },
      found: [{ seat, hit, blocked, points, frame }], skip: seat[],          // у play — без чужих текстів
      answer: null | { id, mode, name, phrase, year, about, page, sources[], image },   // лише reveal/done
      answers: null | [{ seat, text, how, hit, blocked, points, frame }],
      played: null | [{ id, name, phrase, year, page, image }],             // лише done
      scores[12], left[], note, error, result: { winners, scores, nicks } }
  Хід: Act('guess', { text }) — промах приходить відмовою «Мимо» (її кажемо в рядку під полем, а не тостом);
       Act('pick', { i }) — варіант, одна спроба; Act('skip') — «не знаю — далі» (ще раз — передумав).
  Гра реалтаймова (тик 500 мс): після дії вид приходить із найближчим тиком, тож влучання й тицьнутий варіант
  позначаємо одразу самі, ще до виду.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.8" y="2.3" width="12.4" height="11.4" rx="2" fill="none" stroke="var(--accent)" stroke-width="1.5"/>'
    + '<rect x="3.6" y="4.1" width="2.4" height="2.4" fill="var(--clay)"/><rect x="6" y="6.5" width="2.4" height="2.4" fill="var(--clay)" opacity=".7"/>'
    + '<path d="M8.6 11.4c.8.9 2.6.9 3.4 0" fill="none" stroke="var(--ok)" stroke-width="1.4" stroke-linecap="round"/>'
    + '<circle cx="10" cy="7.6" r=".9" fill="var(--ok)"/><circle cx="12.4" cy="7.6" r=".9" fill="var(--ok)"/></svg>';

  const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  /// Вписати html, лише коли він справді інший (свій хелпер, як у melody.js: модуль підміняють на льоту).
  const setHtml = (el, html) => { if (!el || el._h === html) return false; el._h = html; el.innerHTML = html; return true; };
  const V = (ctx) => (ctx && ctx.view) || {};
  const seatsOf = (ctx) => (ctx.room && ctx.room.seats ? ctx.room.seats.length : 12);
  const isSolo = (ctx) => !!(ctx.room && (ctx.room.maxPlayers === 1 || ctx.room.game === 'memy-solo'));
  const lobbyOf = (ctx) => !!(ctx.room && ctx.room.status === 'lobby');
  const secs = (iso) => (iso ? Math.max(0, Math.ceil((Date.parse(iso) - Date.now()) / 1000)) : 0);
  /// Посилання з банку — лише https: решту (javascript:, http) не показуємо взагалі.
  const safeUrl = (u) => (typeof u === 'string' && /^https:\/\/[^\s"'<>]+$/i.test(u) ? u : '');
  const hostOf = (u) => { try { return new URL(u).hostname.replace(/^www\./, ''); } catch { return 'джерело'; } };
  /// Корінь картки з ctx: onKey і pad.when отримують лише ctx, а карток на складі може бути кілька.
  const rootOf = (ctx) => (ctx && ctx._memyRoot && ctx._memyRoot._memy ? ctx._memyRoot : null);

  function plural(n, one, few, many) {
    const d = n % 10, hh = n % 100;
    return d === 1 && hh !== 11 ? one : d >= 2 && d <= 4 && (hh < 12 || hh > 14) ? few : many;
  }
  const pts = (n) => n + ' ' + plural(n, 'очко', 'очки', 'очок');

  function st(root) {
    if (!root._memy) root._memy = { round: -1, sel: -1, picked: -1, pickRound: -1, hitRound: -1, want: '', shown: '', board: null, prevBest: null };
    return root._memy;
  }

  /// Нік місця; після партії — і тих, хто вже встав: підсумок не має губити переможця.
  function nickAt(ctx, v, i) {
    const res = v.result;
    return ctx.nickOf(i) || (ctx.nameOf && ctx.nameOf(i)) || (res && res.nicks && res.nicks[i]) || '';
  }

  const gone = (ctx, v) => ctx.seat != null && (v.left || []).indexOf(ctx.seat) >= 0;
  /// Моє в раунді вже зроблено (влучив / профукав варіант) — з виду або щойно, ще до виду.
  function settled(root, v) {
    const me = v.me || {};
    return !!(me.hit || me.blocked || st(root).hitRound === v.round);
  }
  /// Можна ще щось робити в цьому раунді.
  const canAct = (root, ctx, v) => !!ctx.mine && !!ctx.playing && v.phase === 'play' && !gone(ctx, v) && !settled(root, v);
  const canPick = (root, ctx, v) => canAct(root, ctx, v) && !!(v.choices && v.choices.length) && st(root).pickRound !== v.round;
  const canWrite = (root, ctx, v) => canAct(root, ctx, v) && !!v.canWrite;

  // ---------------------------------------------------------------------------------------------
  // дії
  // ---------------------------------------------------------------------------------------------

  function guess(root) {
    const ctx = root._ctx, v = V(ctx), s = st(root);
    const input = root.querySelector('.memy-input');
    const text = input.value.trim();
    if (!text || !canWrite(root, ctx, v)) return;
    const round = v.round;
    const quiet = !!ctx.tell;   // тихо: «Мимо» кажемо в рядку під полем, а не червоним тостом щоразу
    Promise.resolve(quiet ? ctx.tell('guess', { text }) : ctx.act('guess', { text })).then((r) => {
      if (!root._memy) return;
      if (!r) { ctx.toast('Зв\'язку нема — спробуй ще', 'err'); return; }
      if (r.ok) {
        s.hitRound = round;
        input.value = '';
        if (r.message && quiet) ctx.toast(r.message, 'ok');
      } else if (r.message === 'Мимо') {
        input.value = '';
        input.classList.remove('memy-shake');
        void input.offsetWidth;          // перезапустити струс
        input.classList.add('memy-shake');
      } else if (r.message && quiet) ctx.toast(r.message, 'err');
      const c = root._ctx;
      paint(root, c);
      if (!HGames.ui.coarse() && canWrite(root, c, V(c))) input.focus();
    });
  }

  function pick(root, i) {
    const ctx = root._ctx, v = V(ctx), s = st(root);
    if (!canPick(root, ctx, v) || i < 0 || i >= v.choices.length) return;
    const round = v.round;
    s.pickRound = round;
    s.picked = i;
    s.sel = i;
    paint(root, ctx);
    ctx.act('pick', { i }).then((r) => {
      // відмова (вид запізнився, раунд скінчився) — кнопки знову живі
      const s2 = root._memy;
      if (r && !r.ok && s2 && s2.pickRound === round) { s2.pickRound = -1; s2.picked = -1; paint(root, root._ctx); }
    });
  }

  function skip(root) {
    const ctx = root._ctx;
    if (ctx) ctx.act('skip');
  }

  // ---------------------------------------------------------------------------------------------
  // шапка: раунд, що вгадуємо, таймер
  // ---------------------------------------------------------------------------------------------

  function paintTop(root, ctx, v) {
    const lobby = lobbyOf(ctx);
    const round = root.querySelector('.memy-round');
    let t = '';
    if (!lobby && v.phase === 'done') {
      const n = (v.played || []).length;
      t = n ? 'Зіграно ' + n + ' ' + plural(n, 'мем', 'меми', 'мемів') : '';
    } else if (!lobby && v.round) {
      t = 'Мем ' + v.round + ' з ' + v.rounds;
      if (isSolo(ctx)) t += ' · ' + pts((v.scores || [])[0] || 0);
    }
    if (round.textContent !== t) round.textContent = t;

    const mode = root.querySelector('.memy-mode');
    let m = '';
    const phrase = (v.phase === 'reveal' && v.answer ? v.answer.mode : v.mode) === 'phrase';
    if (!lobby && v.phase === 'play') m = phrase ? '💬 Яка фраза?' : '🖼 Що за мем?';
    else if (!lobby && v.phase === 'reveal') m = phrase ? '💬 Ось яка фраза' : '🖼 Ось що за мем';
    if (mode.textContent !== m) mode.textContent = m;
    mode.classList.toggle('memy-phrase', phrase);
    mode.hidden = !m;

    const host = root.querySelector('.memy-arc');
    if (ctx.playing && v.until && (v.phase === 'play' || v.phase === 'reveal')) {
      ctx.ui.timerArc(host, v.until, v.totalMs || 1000);
      host.classList.toggle('memy-rv', v.phase === 'reveal');
    } else {
      const arc = host.querySelector(':scope > .garc');
      if (arc) { if (arc._arc) arc._arc.stop(); arc.remove(); }
    }
  }

  // ---------------------------------------------------------------------------------------------
  // картинка
  // ---------------------------------------------------------------------------------------------

  /// Кадр — у <img> лише тоді, коли вже завантажився: між кадрами не блимає порожня рамка.
  function paintImage(root, ctx, v) {
    const s = st(root);
    const frame = root.querySelector('.memy-frame');
    const img = root.querySelector('.memy-img');
    const lobby = lobbyOf(ctx);
    const want = !lobby && v.phase !== 'done' && v.phase !== 'loading' ? (v.phase === 'reveal' && v.answer ? v.answer.image : v.image) || '' : '';
    s.pixWant = v.phase === 'play' && v.show === 'pixels' && v.frame < 2;
    if (want !== s.want) {
      s.want = want;
      frame.classList.remove('memy-broken');
      if (!want) { img.removeAttribute('src'); s.shown = ''; frame.classList.add('memy-empty'); }
      else {
        const pre = new Image();
        pre.decoding = 'async';
        pre.onload = () => {
          const s2 = root._memy;
          if (!s2 || s2.want !== want) return;
          img.src = want;
          s2.shown = want;
          frame.classList.remove('memy-empty', 'memy-broken');
          frame.classList.toggle('memy-pix', !!s2.pixWant);
          if (root._ctx) paintCover(root, root._ctx, V(root._ctx));
        };
        pre.onerror = () => {
          const s2 = root._memy;
          if (s2 && s2.want === want) { frame.classList.add('memy-broken'); if (root._ctx) paintCover(root, root._ctx, V(root._ctx)); }
        };
        pre.src = want;
      }
    }
    if (s.shown === want) frame.classList.toggle('memy-pix', s.pixWant);
    // розкриття: чітка картинка з легким спалахом
    const rv = v.phase === 'reveal';
    if (rv && s.flashRound !== v.round) {
      s.flashRound = v.round;
      frame.classList.remove('memy-flash');
      void frame.offsetWidth;
      frame.classList.add('memy-flash');
    }
    frame.classList.toggle('memy-revealed', rv);
    paintCover(root, ctx, v);
    root.querySelector('.memy-left').hidden = v.phase === 'done' && !lobby;
    paintSteps(root, ctx, v);
  }

  /// Шар поверх рамки: лобі, «Глек дістає мем», картинка ще їде чи не прийшла.
  function paintCover(root, ctx, v) {
    const s = st(root);
    const frame = root.querySelector('.memy-frame');
    const cover = root.querySelector('.memy-cover');
    let html = '';
    if (lobbyOf(ctx)) html = '<div class="memy-qmark">?</div><div>Картинка без підпису — а ти згадай, що за мем</div>';
    else if (v.phase === 'loading') html = '<div><span class="spin"></span></div><div>' + (v.round ? 'Глек дістає наступний мем…' : 'Глек гортає мемарню…') + '</div>';
    else if (s.want && frame.classList.contains('memy-broken')) html = '<div>Картинка не прийшла</div><button type="button" class="ghost memy-retry">⟳ Ще раз</button>';
    else if (s.want && !s.shown) html = '<div><span class="spin"></span></div>';
    if (setHtml(cover, html)) {
      const b = cover.querySelector('.memy-retry');
      if (b) b.onclick = () => { s.want = ''; paintImage(root, root._ctx, V(root._ctx)); };
    }
    cover.hidden = !html;
  }

  /// Під картинкою: на якому кадрі проявлення і скільки за нього дають, коли наступний.
  function paintSteps(root, ctx, v) {
    const el = root.querySelector('.memy-steps');
    let html = '';
    if (!lobbyOf(ctx) && v.phase === 'play') {
      if (v.show === 'pixels') {
        const lab = ['🧩 +50', '🧩 +25', '👁 чітка'];
        html = '<div class="memy-dots">' + lab.map((l, k) => '<span class="memy-dot' + (k === v.frame ? ' on' : k < v.frame ? ' past' : '') + '">' + l + '</span>').join('') + '</div>'
          + '<span class="memy-next muted small" data-next></span>';
      } else html = '<span class="muted small">⚡ Швидше влучиш — більше очок (до +50)</span>';
    }
    setHtml(el, html);
    el.hidden = !html;
    liveTexts(root, ctx, v);
  }

  // ---------------------------------------------------------------------------------------------
  // відповідь: поле, варіанти, «не знаю»
  // ---------------------------------------------------------------------------------------------

  function paintAsk(root, ctx, v) {
    const s = st(root), me = v.me || {};
    const box = root.querySelector('.memy-ask');
    const play = v.phase === 'play' && !lobbyOf(ctx) && !!ctx.playing;
    box.hidden = !play;
    if (!play) return;
    const input = root.querySelector('.memy-input');
    if (s.round !== v.round) { s.round = v.round; s.sel = -1; s.picked = -1; s.pickRound = -1; input.value = ''; }
    const phrase = v.mode === 'phrase';
    const hitNow = !!(me.hit || s.hitRound === v.round);

    // поле
    const form = root.querySelector('.memy-form');
    const can = canWrite(root, ctx, v);
    form.hidden = !(ctx.mine && v.canWrite) || gone(ctx, v) || hitNow || !!me.blocked;
    const opened = can && input.disabled;
    input.disabled = !can;
    form.querySelector('button').disabled = !can;
    input.placeholder = phrase ? 'фраза з мему…' : 'назва мему…';
    // Новий мем — курсор одразу в полі (на ПК): вгадують наввипередки. На телефоні — ні: клавіатура закрила б картинку.
    if (opened && !HGames.ui.coarse()) {
      const busy = document.activeElement;
      if (!busy || busy === document.body || busy === input || !/^(INPUT|TEXTAREA|SELECT)$/.test(busy.tagName)) setTimeout(() => { if (!input.disabled) input.focus(); }, 0);
    }

    // мій стан
    const line = root.querySelector('.memy-me');
    let mine = '';
    if (!ctx.mine) mine = '<span class="muted">👀 Дивишся збоку — відповіді побачиш після раунду</span>';
    else if (gone(ctx, v)) mine = '<span class="muted">Ти вже встав з-за столу</span>';
    else if (hitNow) {
      mine = '<span class="memy-ok">✓ Є!' + (me.points ? ' +' + me.points : '') + (me.hit && me.frame === 0 ? ' 🧩 з пікселів' : '') + '</span>'
        + (isSolo(ctx) ? '' : '<span class="muted"> · чекаємо інших</span>');
    } else if (me.blocked) mine = '<span class="memy-bad">🙈 Не «' + esc(me.text) + '» — цей мем уже без тебе</span>';
    else if (s.pickRound === v.round) mine = '<span class="muted">Глек перевіряє…</span>';
    else if (me.text && me.how === 'write') mine = '<span class="memy-bad">✗ «' + esc(me.text) + '» — не те.</span><span class="muted"> Пиши ще</span>';
    else if ((v.skip || []).indexOf(ctx.seat) >= 0) mine = '<span class="muted">🤷 Пасуєш — чекаємо інших</span>';
    setHtml(line, mine);
    line.hidden = !mine;
    line.classList.toggle('memy-big', hitNow || !!me.blocked);

    // варіанти: відлік до них, «пів ціни», самі кнопки
    const hint = root.querySelector('.memy-hint');
    let h = '';
    if (!canAct(root, ctx, v)) h = '';
    else if (v.answerMode === 'write') h = '✍ Лише писати — варіантів не буде';
    else if (v.choices && v.choices.length) {
      h = v.answerMode === 'pick' ? '👆 Тицяй варіант — одна спроба, не той — мем без тебе'
        : '🔘 Варіанти — тепер усе за <b>пів ціни</b>. Не той — мем без тебе';
    } else if (v.choicesAt) h = '🔘 Варіанти — за <b data-choices></b> · відтоді <b>пів ціни</b>';
    setHtml(hint, h);
    hint.hidden = !h;

    const grid = root.querySelector('.memy-choices');
    const list = v.choices || [];
    const pickOk = canPick(root, ctx, v);
    const picked = me.how === 'pick' ? list.indexOf(me.text) : s.pickRound === v.round ? s.picked : -1;
    const html = list.map((c, i) => '<button type="button" class="memy-ch' + (i === s.sel && pickOk ? ' memy-sel' : '')
      + (i === picked ? (me.hit ? ' memy-right' : me.blocked ? ' memy-wrong' : ' memy-mine') : '') + '" data-i="' + i + '"'
      + (pickOk ? '' : ' disabled') + '><kbd>' + (i + 1) + '</kbd><span>' + esc(c) + '</span></button>').join('');
    // хто вже влучив — варіанти йому ні до чого, а місце на телефоні дороге
    const showGrid = list.length > 0 && !!ctx.mine && (pickOk || picked >= 0);
    grid.hidden = !showGrid;
    grid.classList.toggle('memy-long', list.some((c) => c.length > 26));
    box.classList.toggle('memy-open', showGrid);
    if (setHtml(grid, showGrid ? html : '')) {
      grid.querySelectorAll('.memy-ch').forEach((b) => { b.onclick = () => pick(root, +b.dataset.i); });
      // на телефоні варіанти під картинкою — раз на мем підкрутити, щоб було видно всі чотири (запас під вкладки сайту)
      if (showGrid && s.chSeen !== v.round) {
        s.chSeen = v.round;
        const r = grid.getBoundingClientRect();
        if (r.bottom > window.innerHeight - 72) grid.scrollIntoView({ block: 'end', behavior: 'smooth' });
      }
    }

    // «не знаю — далі»
    const sk = root.querySelector('.memy-skipbox');
    const mineSkip = (v.skip || []).indexOf(ctx.seat) >= 0;
    const skipCan = canAct(root, ctx, v) && s.pickRound !== v.round;
    const skHtml = skipCan ? '<button type="button" class="' + (mineSkip ? 'primary' : 'ghost') + ' memy-skip">'
      + (mineSkip ? '↩ Передумав — ще подумаю' : '🤷 Не знаю — далі') + '</button>' : '';
    if (setHtml(sk, skHtml)) {
      const b = sk.querySelector('.memy-skip');
      if (b) b.onclick = () => skip(root);
    }
    sk.hidden = !skHtml;

    // хто вже вгадав (без текстів — їх видно лише на розкритті)
    const who = root.querySelector('.memy-found');
    const found = (v.found || []).filter((f) => f.seat !== ctx.seat);
    const skips = (v.skip || []).filter((i) => i !== ctx.seat);
    const chips = found.map((f) => '<span class="chip ' + (f.hit ? 'ok' : 'err') + '">' + (f.hit ? '✓ ' : '🙈 ') + esc(nickAt(ctx, v, f.seat)) + '</span>')
      .concat(skips.map((i) => '<span class="chip">🤷 ' + esc(nickAt(ctx, v, i)) + '</span>'));
    const fh = isSolo(ctx) || !chips.length ? '' : chips.join('');
    setHtml(who, fh);
    who.hidden = !fh;
    liveTexts(root, ctx, v);
  }

  /// Лічильники, що йдуть щосекунди: до наступного кадру й до варіантів.
  function liveTexts(root, ctx, v) {
    const n = root.querySelector('[data-next]');
    if (n) {
      const t = v.nextFrameAt && v.phase === 'play' ? secs(v.nextFrameAt) : 0;
      const txt = t > 0 ? 'чіткіше за ' + t + ' с' : '';
      if (n.textContent !== txt) n.textContent = txt;
    }
    const c = root.querySelector('[data-choices]');
    if (c) {
      const t = secs(v.choicesAt);
      const txt = t > 0 ? t + ' с' : 'мить';
      if (c.textContent !== txt) c.textContent = txt;
    }
  }

  // ---------------------------------------------------------------------------------------------
  // розкриття
  // ---------------------------------------------------------------------------------------------

  function answerRows(ctx, v) {
    const by = {};
    for (const a of v.answers || []) by[a.seat] = a;
    const rows = [];
    for (let i = 0; i < seatsOf(ctx); i++) {
      const a = by[i];
      const nick = nickAt(ctx, v, i);
      if (!nick && !a) continue;
      if (!a && (v.left || []).indexOf(i) >= 0) continue;
      rows.push({ i, nick, a });
    }
    // спершу влучні (за очками), далі промахи, наприкінці — хто змовчав
    const rank = (r) => (r.a && r.a.hit ? 0 : r.a ? 1 : 2);
    rows.sort((x, y) => rank(x) - rank(y) || ((y.a && y.a.points) || 0) - ((x.a && x.a.points) || 0) || x.i - y.i);
    return rows.map((r) => {
      const a = r.a;
      const how = !a ? '' : a.how === 'pick' ? '<span class="memy-how" title="тицьнув варіант">👆</span>' : '<span class="memy-how" title="написав сам">✍</span>';
      const mark = !a ? '🤷' : a.hit ? '✓' : '✗';
      const text = a ? '«' + esc(a.text) + '»' : '<i class="muted">не знав</i>';
      const extra = a && a.hit && a.frame === 0 ? ' 🧩' : '';
      return '<div class="memy-arow' + (a && a.hit ? ' hit' : a ? ' miss' : ' none') + (r.i === ctx.seat ? ' me' : '') + '">'
        + '<b class="memy-mark">' + mark + '</b><span class="memy-anick">' + esc(r.nick) + '</span>' + how
        + '<span class="memy-atext">' + text + '</span>'
        + '<em>' + (a && a.points ? '+' + a.points + extra : '') + '</em></div>';
    }).join('');
  }

  function revealHtml(ctx, v) {
    const a = v.answer;
    if (!a) return '';
    const phrase = a.mode === 'phrase';
    const page = safeUrl(a.page);
    const src = (a.sources || []).map(safeUrl).filter((u) => u && u !== page).slice(0, 3);
    const links = (page ? ['<a href="' + esc(page) + '" target="_blank" rel="noopener noreferrer">🖼 Звідки картинка ↗</a>'] : [])
      .concat(src.map((u) => '<a href="' + esc(u) + '" target="_blank" rel="noopener noreferrer">📚 ' + esc(hostOf(u)) + ' ↗</a>'));
    return '<div class="memy-rvcard">'
      + '<div class="memy-kicker">' + (phrase ? 'Фраза з мему' : 'Це мем') + (a.year ? '<span class="chip">📅 ' + esc(a.year) + '</span>' : '') + '</div>'
      + '<div class="memy-name">' + esc(a.name) + '</div>'
      + (a.phrase ? '<div class="memy-phr' + (phrase ? ' memy-big' : '') + '">«' + esc(a.phrase) + '»</div>' : '')
      + (a.about ? '<p class="memy-about">' + esc(a.about) + '</p>' : '')
      + (links.length ? '<div class="memy-links">' + links.join('') + '</div>' : '')
      + '</div>'
      + '<div class="memy-answers">' + answerRows(ctx, v) + '</div>';
  }

  function paintReveal(root, ctx, v) {
    const el = root.querySelector('.memy-reveal');
    const show = v.phase === 'reveal' && !!v.answer && !lobbyOf(ctx);
    setHtml(el, show ? revealHtml(ctx, v) : '');
    el.hidden = !show;
  }

  // ---------------------------------------------------------------------------------------------
  // рахунок і кінець партії
  // ---------------------------------------------------------------------------------------------

  function paintScores(root, ctx, v) {
    const el = root.querySelector('.memy-scores');
    if (lobbyOf(ctx) || isSolo(ctx) || (v.phase === 'loading' && !v.round)) { setHtml(el, ''); el.hidden = true; return; }
    const found = {};
    if (v.phase === 'play') for (const f of v.found || []) found[f.seat] = f;
    const skip = v.phase === 'play' ? v.skip || [] : [];
    const rows = [];
    for (let i = 0; i < seatsOf(ctx); i++) {
      const nick = nickAt(ctx, v, i);
      if (nick) rows.push({ i, nick, score: (v.scores || [])[i] || 0, f: found[i] });
    }
    rows.sort((a, b) => b.score - a.score || a.i - b.i);
    const left = v.left || [];
    const win = (v.phase === 'done' && v.result && v.result.winners) || [];
    const html = rows.map((r) => '<div class="memy-sc' + (r.i === ctx.seat ? ' me' : '') + (left.indexOf(r.i) >= 0 ? ' off' : '') + (win.indexOf(r.i) >= 0 ? ' win' : '') + '">'
      + '<span class="memy-sn">' + (win.indexOf(r.i) >= 0 ? '🏆 ' : '') + esc(r.nick) + '</span>'
      + '<span class="memy-sf">' + (r.f && r.f.hit ? '✓' : '') + (r.f && r.f.blocked ? '🙈' : '') + (skip.indexOf(r.i) >= 0 ? '🤷' : '') + '</span>'
      + (r.f && r.f.points ? '<em>+' + r.f.points + '</em>' : '')
      + '<b>' + r.score + '</b></div>').join('');
    setHtml(el, html);
    el.hidden = !html;
  }

  function doneHead(ctx, v) {
    const res = v.result || {};
    const w = (res.winners || []).map((i) => nickAt(ctx, v, i)).filter(Boolean);
    if (!w.length) return (v.played || []).length ? 'Нічия — жодного мему не вгадали' : 'Партію зіграно';
    const p = (res.scores || [])[res.winners[0]] || 0;
    return '🏆 ' + w.join(' і ') + (w.length > 1 ? ' — по ' : ' — ') + pts(p) + ' · ' + (w.length > 1 ? 'головні мемологи' : 'головний мемолог');
  }

  function soloHtml(root, ctx, v) {
    const s = st(root);
    const mine = (v.scores || [])[0] || 0;
    const best = s.board;
    let rec = '';
    if (ctx.me && ctx.me.account === false) rec = '<div class="memy-rec">Рекорд у «Таблицю» пишеться лише акаунтам — зайди, і він збережеться</div>';
    else if (best && best.best != null) {
      const fresh = mine > 0 && (s.prevBest == null ? mine >= best.best : mine > s.prevBest);
      rec = fresh ? '<div class="memy-rec memy-new">🏆 Новий рекорд!' + (s.prevBest ? ' Було ' + s.prevBest : '') + '</div>'
        : '<div class="memy-rec">Твій рекорд — <b>' + best.best + '</b>' + (best.place ? ' · ' + best.place + '-е місце в «Таблиці»' : '') + '</div>';
    }
    const n = (v.played || []).length;
    return '<div class="memy-solo"><div class="memy-bign">' + mine + '</div><div class="muted">' + plural(mine, 'очко', 'очки', 'очок')
      + ' за ' + n + ' ' + plural(n, 'мем', 'меми', 'мемів') + '</div>' + rec + '</div>';
  }

  function paintDone(root, ctx, v) {
    const el = root.querySelector('.memy-done');
    const show = v.phase === 'done' && !lobbyOf(ctx);
    el.hidden = !show;
    if (!show) { setHtml(el, ''); return; }
    const played = v.played || [];
    const html = (isSolo(ctx) ? soloHtml(root, ctx, v) : '<div class="memy-win">' + esc(doneHead(ctx, v)) + '</div>')
      + (v.error ? '<div class="memy-note">⚠️ ' + esc(v.error) + '</div>' : '')
      + (v.note ? '<div class="memy-note muted">ℹ️ ' + esc(v.note) + '</div>' : '')
      + (played.length ? '<div class="memy-subh">Що було в партії</div><div class="memy-played">'
        + played.map((m) => {
          const page = safeUrl(m.page);
          return '<div class="memy-pl">'
            + '<span class="memy-thumb">' + (m.image ? '<img loading="lazy" alt="" src="' + esc(m.image) + '">' : '') + '</span>'
            + '<span class="memy-plt"><b>' + esc(m.name) + '</b>' + (m.phrase ? '<i>«' + esc(m.phrase) + '»</i>' : '')
            + '<small class="muted">' + (m.year ? esc(m.year) : '') + (page ? (m.year ? ' · ' : '') + '<a href="' + esc(page) + '" target="_blank" rel="noopener noreferrer">звідки ↗</a>' : '') + '</small></span>'
            + '</div>';
        }).join('') + '</div>' : '');
    setHtml(el, html);
  }

  /// Рекорд тренування — з «Таблиці» (`/api/games/leaderboard`, соло-рядки {nick, best, tries}). Рядок у базу каркас
  /// пише вже після Finish — тож після партії тягнемо з запасом. До партії — щоб знати, чи рекорд новий.
  function loadBoard(root, ctx, after) {
    const nick = (ctx.me && ctx.me.nick) || '';
    if (!nick) return;
    const go = () => fetch('/api/games/leaderboard?game=memy-solo&period=all', { headers: { 'X-Nick': encodeURIComponent(nick) } })
      .then((r) => (r.ok ? r.json() : null))
      .then((d) => {
        const s2 = root._memy;
        if (!s2) return;
        const rows = d && Array.isArray(d.rows) ? d.rows : [];
        const k = rows.findIndex((r) => String(r.nick || '').toLowerCase() === nick.toLowerCase());
        const row = k >= 0 ? { best: rows[k].best, place: k + 1 } : { best: null, place: 0 };
        if (!after) s2.prevBest = row.best;
        s2.board = row;
        if (after && root._ctx) paintDone(root, root._ctx, V(root._ctx));
      })
      .catch(() => {});
    if (after) setTimeout(go, 900); else go();
  }

  // ---------------------------------------------------------------------------------------------
  // лобі
  // ---------------------------------------------------------------------------------------------

  function lobbyHtml(ctx) {
    const o = (ctx.room && ctx.room.options) || {};
    const what = { name: 'назву мему', phrase: 'фразу з мему', mix: 'назву мему або його фразу' }[o.what || 'mix'] || 'назву мему';
    const ans = o.answer || 'both';
    const at = { 8: 'за 8 с до кінця', 5: 'за 5 с до кінця', half: 'з половини раунду' }[o.choicesAt || '8'] || 'під кінець';
    const bits = [
      '🖼 ' + (o.rounds || 10) + ' мемів по ' + (o.seconds || 20) + ' с — вгадуй ' + what,
      (o.show || 'pixels') === 'pixels' ? '🧩 Картинка проявляється з пікселів: влучив на першому кадрі — +50, на другому — +25' : '⚡ Картинка одразу ціла: хто швидше, тому більше',
      ans === 'pick' ? '👆 Лише варіанти: одна спроба, по 50 очок' : ans === 'write' ? '✍ Лише писати: 100 очок за влучання, спроб скільки завгодно'
        : '✍ Спершу пишеш сам (100 очок), ' + at + ' з\'являться 4 варіанти — відтоді пів ціни',
      '🥇 Перший, хто влучив, — ще +20. Після кожного мему — звідки він узявся',
    ];
    return '<div class="memy-lobby">' + bits.map((b) => '<div>' + esc(b) + '</div>').join('')
      + (isSolo(ctx) ? '' : '<div class="muted small">Господар тисне «Почати»</div>') + '</div>';
  }

  // ---------------------------------------------------------------------------------------------
  // збирання
  // ---------------------------------------------------------------------------------------------

  function paint(root, ctx) {
    if (!ctx || !root._memy) return;
    root._ctx = ctx;
    ctx._memyRoot = root;
    const v = V(ctx);
    const lobby = lobbyOf(ctx);
    const wrap = root.querySelector('.memy');
    wrap.classList.toggle('memy-isdone', v.phase === 'done' && !lobby);
    wrap.classList.toggle('memy-inlobby', lobby);
    paintTop(root, ctx, v);
    paintImage(root, ctx, v);
    paintAsk(root, ctx, v);
    paintReveal(root, ctx, v);
    paintDone(root, ctx, v);
    paintScores(root, ctx, v);
    const lb = root.querySelector('.memy-lobbybox');
    setHtml(lb, lobby ? lobbyHtml(ctx) : '');
    lb.hidden = !lobby;
    const note = root.querySelector('.memy-notes');
    const n = v.phase !== 'done' && !lobby ? [v.note && 'ℹ️ ' + v.note, v.error && '⚠️ ' + v.error].filter(Boolean) : [];
    setHtml(note, n.map((t) => '<div>' + esc(t) + '</div>').join(''));
    note.hidden = !n.length;
    // рекорд тренування: на початку партії — яким він був (щоб знати, чи новий), після — яким став
    const s = st(root);
    if (isSolo(ctx) && v.phase !== s.lastPhase) {
      if (v.phase === 'done') loadBoard(root, ctx, true);
      else if (s.lastPhase === undefined || s.lastPhase === 'done') loadBoard(root, ctx, false);
    }
    s.lastPhase = v.phase;
  }

  /// Хрестовина / стрілки по варіантах: два стовпці (довгі фрази — один).
  function moveSel(root, ctx, code) {
    const v = V(ctx), s = st(root);
    const n = (v.choices || []).length;
    if (!n) return;
    const grid = root.querySelector('.memy-choices');
    const cols = getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length || 1;
    if (s.sel < 0) s.sel = 0;
    else if (code === 'ArrowLeft') s.sel = Math.max(0, s.sel - 1);
    else if (code === 'ArrowRight') s.sel = Math.min(n - 1, s.sel + 1);
    else if (code === 'ArrowUp') s.sel = s.sel - cols >= 0 ? s.sel - cols : s.sel;
    else if (code === 'ArrowDown') s.sel = s.sel + cols < n ? s.sel + cols : s.sel;
    paintAsk(root, ctx, v);
  }

  const common = {
    icon: ICON,
    added: '2026-10-20',          // нова гра: «🆕» у лобі два тижні тим, хто ще не грав (core.js, isNewGame)
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o', 'c', 'd', 'x', 'o', 'c', 'd'],

    mount(root, ctx) {
      root.innerHTML = '<div class="memy">'
        + '<div class="memy-top"><span class="memy-round muted small"></span><span class="memy-mode" hidden></span><span class="memy-arc"></span></div>'
        + '<div class="memy-notes small" hidden></div>'
        + '<div class="memy-main">'
        + '<div class="memy-left">'
        + '<div class="memy-frame memy-empty"><img class="memy-img" alt="Картинка мему" draggable="false"><div class="memy-cover"></div></div>'
        + '<div class="memy-steps" hidden></div>'
        + '</div>'
        + '<div class="memy-side">'
        + '<div class="memy-lobbybox" hidden></div>'
        + '<div class="memy-ask" hidden>'
        + '<div class="memy-me" hidden></div>'
        + '<form class="memy-form" hidden><input class="memy-input" type="text" maxlength="80" autocomplete="off" autocorrect="off" autocapitalize="off" spellcheck="false" enterkeyhint="send" aria-label="Відповідь">'
        + '<button class="primary" type="submit" aria-label="Відповісти">➤</button></form>'
        + '<div class="memy-hint small" hidden></div>'
        + '<div class="memy-choices" hidden></div>'
        + '<div class="memy-skipbox" hidden></div>'
        + '<div class="memy-found" hidden></div>'
        + '</div>'
        + '<div class="memy-reveal" hidden></div>'
        + '<div class="memy-done" hidden></div>'
        + '<div class="memy-scores" hidden></div>'
        + '</div></div></div>';
      const s = st(root);
      const input = root.querySelector('.memy-input');
      root.querySelector('.memy-form').onsubmit = (e) => { e.preventDefault(); guess(root); };
      // У полі цифри 1–4 — варіанти, поки поле порожнє: руку з клавіатури не знімати
      input.addEventListener('keydown', (e) => {
        const c = root._ctx;
        if (!c || !root._memy) return;
        const k = /^(?:Digit|Numpad)([1-4])$/.exec(e.code);
        if (k && !input.value && canPick(root, c, V(c))) { e.preventDefault(); pick(root, +k[1] - 1); }
      });
      input.addEventListener('animationend', () => input.classList.remove('memy-shake'));
      s.timer = setInterval(() => { if (root._ctx && root._memy) liveTexts(root, root._ctx, V(root._ctx)); }, 250);
      paint(root, ctx);
    },

    update(root, ctx) { paint(root, ctx); },

    unmount(root) {
      const s = root._memy;
      if (!s) return;
      clearInterval(s.timer);
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      if (root._ctx && root._ctx._memyRoot === root) root._ctx._memyRoot = null;
      root._memy = null;
    },

    onKey(e, ctx) {
      // Esc посеред партії — з'їдаємо, щоб каркас не викинув зі столу: на Steam Deck кнопка B, якою ховають
      // екранну клавіатуру після відповіді, приходить саме як Escape (так само у svoya.js).
      if (e.key === 'Escape') return !!(ctx.mine && ctx.playing);
      const root = rootOf(ctx);
      if (!root) return false;
      const v = V(ctx);
      if (!ctx.mine || !ctx.playing) return false;
      // між раундами клавіші пада (Enter, F9, стрілки) ковтаємо, щоб не гортали сторінку й не тиснули каркас
      if (v.phase !== 'play') return /^(Enter|NumpadEnter|F9|Arrow\w+)$/.test(e.code) && (v.phase === 'reveal' || v.phase === 'loading');
      const k = /^(?:Digit|Numpad)([1-4])$/.exec(e.code);
      if (k && canPick(root, ctx, v)) { pick(root, +k[1] - 1); return true; }
      if (/^Arrow/.test(e.code) && canPick(root, ctx, v)) { moveSel(root, ctx, e.code); return true; }
      if (e.code === 'Enter' || e.code === 'NumpadEnter') {
        const s = st(root);
        if (canPick(root, ctx, v)) {
          if (s.sel < 0) { s.sel = 0; paintAsk(root, ctx, v); } else pick(root, s.sel);
          return true;
        }
        // Ⓐ пада без варіантів: є написане — відповісти, нема — у поле (Steam+X відкриє клавіатуру Деки)
        const input = root.querySelector('.memy-input');
        if (canWrite(root, ctx, v)) { if (input.value.trim()) guess(root); else input.focus(); }
        return true;              // посеред партії Enter нічого з каркаса не тисне
      }
      if (e.code === 'F9') { if (canAct(root, ctx, v)) skip(root); return true; }   // Ⓧ пада — «не знаю»
      // просто друкуй: літера поза полем — фокус у поле, і вона туди ж і впаде
      if (e.key && e.key.length === 1 && !e.repeat && /\p{L}/u.test(e.key) && canWrite(root, ctx, v)) root.querySelector('.memy-input').focus();
      return false;
    },

    status(ctx) {
      const v = V(ctx);
      if (lobbyOf(ctx)) return isSolo(ctx) ? '' : 'Господар тисне «Почати»';
      if (v.phase === 'done' || !ctx.playing) return v.phase === 'done' ? (v.error || (isSolo(ctx) ? 'Тренування позаду' : 'Партію зіграно')) : '';
      if (v.phase === 'loading') return 'Глек гортає мемарню…';
      if (v.phase === 'reveal') return v.round >= v.rounds ? 'Глек рахує очки…' : 'Наступний мем за мить…';
      if (!ctx.mine) return 'Дивишся збоку';
      const me = v.me || {};
      if (me.hit) return isSolo(ctx) ? 'Є!' : 'Є! Чекаємо інших';
      if (me.blocked) return 'Не той варіант — чекай наступного мему';
      return v.mode === 'phrase' ? 'Яка фраза з цього мему?' : 'Що це за мем?';
    },

    // Пад — на всю живу партію (як у geo): інакше між раундами кільце стоїть на «Закрити»/«← Лобі», і Ⓐ, натиснутий
    // на мить пізніше, ніж скінчився раунд, викидав зі столу (так і сталося на стенді). Хрестовина ходить по
    // варіантах, Ⓐ — обрати (без варіантів — відповісти написане чи стати в поле), Ⓧ — «не знаю — далі».
    // Ⓑ коротко — нічого, утримати — вийти (pad.js, holdB). Після партії кільце знову водить по картці («Ще раз»).
    pad: {
      dirs: true,
      a: 'Enter',
      x: 'F9',
      hint: '{dpad} варіант · {a} обрати · {x} не знаю',
      when: (ctx) => !!(ctx.mine && ctx.playing && ['loading', 'play', 'reveal'].includes(V(ctx).phase)),
    },
  };

  HGames.register(Object.assign({ id: 'memy' }, common));
  HGames.register(Object.assign({ id: 'memy-solo' }, common));
})();
