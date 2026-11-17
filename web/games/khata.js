/*
  «Здам хату» — фото квартири й місто, більше нічого: вгадай, скільки коштує оренда за місяць. Клієнт нічого не
  вирішує: фази, час і бали живуть на сервері (Impl/Khata.cs), модуль лише малює й шле наміри.

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { round, of, phase: 'between'|'ask'|'reveal'|'done', city, unit: '₴'|'$', photos: [url], endsAt, seconds,
      answered: bool[], my: number|null, own: bool (моя квартира — я мовчу),
      reveal: null | { price, by, say, rows: [{ seat, value, ratio, points }] },
      scores: number[], result: null | { winners, scores, fresh },
      recap: null | [{ city, unit, price, photo, best, value, points }], bank: { fit, total } }
  Кадр (подія 'frame', лише з новиною): { round, of, phase, endsAt, answered, scores }
  Хід: Act('answer', { value }) — рядок як є («15 000», «15к» сервер розбере сам).
  Банк квартир (його наповнюють гравці, розробник схвалює) — /api/games/khata/… (Impl/KhataSetup.cs).
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M1.6 7.6 8 2.2l6.4 5.4" fill="none" stroke="var(--accent)" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>'
    + '<path d="M3.4 7.4v6h9.2v-6" fill="none" stroke="var(--clay)" stroke-width="1.6" stroke-linejoin="round"/>'
    + '<rect x="6.7" y="9.4" width="2.6" height="4" fill="var(--accent)"/></svg>';

  const MS = { between: 3000, ask: 30000, reveal: 8000 };
  /// Те саме, що Khata.PointsPerShard на сервері.
  const POINTS_PER_SHARD = 500;
  /// До скількох байтів тиснемо фото (сервер пускає до 400 КБ) і до якої довшої сторони.
  const PH_MAX = 380 * 1024;
  const PH_SIDES = [1280, 1024, 800];
  const PH_MIN = 240;
  const API = '/api/games/khata';
  // Без прапорців: Windows не має їх у шрифтах і малює літерами «UA», «EU».
  const REGIONS = [['ua', 'Україна', '₴'], ['eu', 'Європа', '$'], ['world', 'Світ', '$']];

  const boostOf = (ctx) => Math.max(1, (ctx && ctx.me && ctx.me.boost) || 1);
  function plural(n, one, few, many) {
    const d = n % 10, h = n % 100;
    return d === 1 && h !== 11 ? one : d >= 2 && d <= 4 && (h < 12 || h > 14) ? few : many;
  }
  const pts = (n) => num(n) + ' ' + plural(Math.abs(n), 'бал', 'бали', 'балів');
  const flatsWord = (n) => plural(n, 'квартира', 'квартири', 'квартир');

  /// Шкала словами — та сама, що Khata.Points на сервері. Міняєш там — міняй і тут.
  function rulesFor(ctx) {
    const k = boostOf(ctx);
    return 'Оголошення справжні. Оренда — за місяць, житло з Airbnb — за ніч: що саме, написано біля поля. '
      + 'Бали за точність, до 1000 за квартиру: промах до 2 % — 1000, на 10 % — близько 800, на чверть — 540, удвічі — 130, '
      + 'утричі — майже нуль. Удвічі дешевше й удвічі дорожче — однаково далеко. '
      + 'За кожні ' + POINTS_PER_SHARD + ' балів за квартири, які бачиш уперше, — 🏺 ' + k + ' ' + plural(k, 'черепок', 'черепки', 'черепків');
  }

  function num(v) {
    if (v == null || isNaN(v)) return '—';
    return Math.round(v).toLocaleString('uk-UA');
  }
  const money = (v, unit) => num(v) + ' ' + (unit || '');

  /// Те саме читання ціни, що й на сервері (Khata.Text). null — не ціна.
  function parsePrice(raw) {
    let s = String(raw || '').replace(/\s+/g, '').toLowerCase().replace(/грн|₴|\$/g, '').replace(/,/g, '.').replace(/\.+$/, '');
    let mult = 1;
    for (const suf of ['тис', 'к', 'k']) if (s.endsWith(suf)) { s = s.slice(0, -suf.length).replace(/\.+$/, ''); mult = 1000; break; }
    if (!s || !/^(\d+\.?\d*|\.\d+)$/.test(s)) return null;
    const n = Number(s) * mult;
    return Number.isFinite(n) ? n : null;
  }

  /// Наскільки повз: до двох разів — у відсотках, далі — у разах («на 900 % більше» нічого не каже).
  function missText(ratio) {
    if (!(ratio > 0)) return '';
    if (Math.abs(ratio - 1) < 0.005) return 'точно';
    const k = ratio > 1 ? ratio : 1 / ratio;
    if (k <= 2) {
      const p = Math.round(Math.abs(ratio - 1) * 100);
      return (ratio > 1 ? '+' : '−') + Math.max(1, p) + ' %';
    }
    const r = k >= 10 ? Math.round(k) : Math.round(k * 10) / 10;
    return 'у ' + String(r).replace('.', ',') + ' раза ' + (ratio > 1 ? 'дорожче' : 'дешевше');
  }

  function state(root) {
    if (!root._kh) root._kh = { round: -1, idx: 0, key: '', form: null, bank: null };
    return root._kh;
  }

  /// innerHTML лише коли HTML справді інший (порівнюємо з тим, що самі клали) — анімації розкриття не починаються знову.
  function setHtml(el, html) {
    if (!el || el._h === html) return;
    el._h = html;
    el.innerHTML = html;
  }

  /// Кадр свіжіший за вид, але вірити йому можна лише в межах того самого раунду й фази.
  function fresh(ctx, v) {
    const f = ctx.frame;
    return f && f.round === v.round && f.phase === v.phase ? f : null;
  }
  /// Хто зараз сидить на місці.
  function sitter(ctx, i) { return (ctx.nameOf && ctx.nameOf(i)) || ctx.nickOf(i); }
  /// Ім'я місця в цій партії: хто сидить, а якщо вже встав — хто на ньому починав (view.names), а не «гравець 2».
  function nameAt(ctx, i) { return sitter(ctx, i) || ((ctx.view || {}).names || [])[i] || ''; }
  /// Місця партії: з тими, хто встав посеред гри (рахунок і підсумок їх пам'ятають). now — лише ті, хто сидить зараз.
  function seats(ctx, now) {
    const room = ctx.room || {};
    const list = [];
    const n = (room.seats && room.seats.length) || 0;
    for (let i = 0; i < n; i++) if (now ? sitter(ctx, i) : nameAt(ctx, i)) list.push(i);
    return list;
  }

  // =========================================================================================
  // фото: карусель на картці й перегляд на весь екран
  // =========================================================================================

  function paintPhoto(root, ctx, v) {
    const st = state(root);
    const fig = root.querySelector('.khphoto');
    const list = (v.phase === 'ask' || v.phase === 'reveal') ? (v.photos || []) : [];
    const key = list.join('|');
    if (st.key !== key) {
      st.key = key;
      st.idx = 0;
      // решту фото квартири тягнемо одразу — гортання без білих пауз
      for (const u of list.slice(1)) { const im = new Image(); im.decoding = 'async'; im.referrerPolicy = 'no-referrer'; im.src = u; }
    }
    fig.hidden = !list.length;
    if (!list.length) return;
    if (st.idx >= list.length) st.idx = 0;
    const img = fig.querySelector('img');
    if (img.getAttribute('src') !== list[st.idx]) img.setAttribute('src', list[st.idx]);
    const many = list.length > 1;
    fig.querySelector('.khprev').hidden = !many;
    fig.querySelector('.khnext').hidden = !many;
    setHtml(fig.querySelector('.khdots'), many ? list.map((_, i) => '<i' + (i === st.idx ? ' class="on"' : '') + '></i>').join('') : '');
  }

  function turn(root, ctx, d) {
    const st = state(root);
    const n = st.key ? st.key.split('|').length : 0;
    if (n < 2) return;
    st.idx = (st.idx + d + n) % n;
    paintPhoto(root, ctx, ctx.view || {});
  }

  /// Перегляд на весь екран — для фото раунду й для квартир у списках банку. Лежить у body: картка має
  /// container-type, а в ній position: fixed рахувався б від картки, а не від вікна.
  function openViewer(root, photos, idx) {
    closeViewer(root);
    if (!photos || !photos.length) return;
    const st = state(root);
    const box = document.createElement('div');
    box.className = 'khview';
    box.innerHTML = '<img alt="Фото квартири" decoding="async" referrerpolicy="no-referrer">'
      + '<button type="button" class="khvprev" aria-label="Попереднє фото">‹</button>'
      + '<button type="button" class="khvnext" aria-label="Наступне фото">›</button>'
      + '<button type="button" class="khvclose" aria-label="Закрити">✕</button><div class="khvno"></div>';
    let i = Math.max(0, Math.min(photos.length - 1, idx || 0));
    const show = () => {
      box.querySelector('img').setAttribute('src', photos[i]);
      box.querySelector('.khvno').textContent = photos.length > 1 ? (i + 1) + ' / ' + photos.length : '';
      box.querySelector('.khvprev').hidden = box.querySelector('.khvnext').hidden = photos.length < 2;
    };
    const go = (d) => { i = (i + d + photos.length) % photos.length; show(); };
    box.addEventListener('click', (e) => {
      if (e.target.closest('.khvprev')) go(-1);
      else if (e.target.closest('.khvnext')) go(1);
      else closeViewer(root);            // тап по фото чи повз — назад
    });
    swipe(box, go);
    st.viewer = { box, go };
    document.body.appendChild(box);
    show();
  }

  function closeViewer(root) {
    const st = root._kh;
    if (st && st.viewer) { st.viewer.box.remove(); st.viewer = null; }
  }

  /// Гортання пальцем: горизонтальний змах від 40 px.
  function swipe(el, go) {
    let x0 = null, y0 = 0;
    el.addEventListener('pointerdown', (e) => { x0 = e.clientX; y0 = e.clientY; });
    el.addEventListener('pointerup', (e) => {
      if (x0 == null) return;
      const dx = e.clientX - x0, dy = e.clientY - y0;
      x0 = null;
      if (Math.abs(dx) >= 40 && Math.abs(dx) > Math.abs(dy) * 1.5) { el._swiped = Date.now(); go(dx < 0 ? 1 : -1); }
    });
    el.addEventListener('click', (e) => {
      // змах закінчується кліком — він не має ще й відкривати/закривати перегляд
      if (el._swiped && Date.now() - el._swiped < 350) { e.stopImmediatePropagation(); e.preventDefault(); }
    }, true);
  }

  // =========================================================================================
  // розкриття, підсумок, рахунок
  // =========================================================================================

  /// Оренда — за місяць, оголошення Airbnb — за ніч.
  const perText = (per) => (per === 'night' ? 'за ніч' : 'за місяць');
  /// Посилання назовні: нова вкладка, без передачі сторінки, з якої прийшли.
  const OUT = ' target="_blank" rel="noopener noreferrer"';

  function revealHtml(ctx, v, admin) {
    const r = v.reveal;
    if (!r) return '';
    const rows = r.rows || [];
    const outer = r.source && r.source !== 'own';
    // Справжнє оголошення: що це за житло й посилання на нього — лише тепер, коли ціни вже названо.
    const what = outer
      ? '<div class="khwhat">' + (r.about ? '<span>' + ctx.esc(r.about) + '</span>' : '')
        + (r.link ? '<a href="' + ctx.esc(r.link) + '"' + OUT + '>Відкрити на ' + ctx.esc(r.by || 'сайті') + ' ↗</a>' : '')
        + (admin && r.id ? '<button type="button" class="ghost khdrop" data-drop="' + r.id + '" title="Прибрати це оголошення з гри назавжди">🗑</button>' : '')
        + '</div>'
      : (r.by ? '<div class="khby muted small">квартиру додав(ла) ' + ctx.esc(r.by) + '</div>' : '');
    const head = '<div class="khans"><span class="muted small">Насправді</span><b>' + money(r.price, v.unit) + '</b><i>' + perText(v.per) + '</i></div>' + what;
    const say = r.say ? '<div class="khsay"><img src="/static/glek.svg" alt=""><span>' + ctx.esc(r.say) + '</span></div>' : '';
    if (!rows.length) return head + '<div class="gempty">Отакої — ніхто не назвав жодної ціни.</div>' + say;
    return head + '<div class="khrows">' + rows.map((x, n) =>
      '<div class="khrow' + (x.points ? ' on' : '') + (n === 0 && x.points ? ' best' : '') + '" style="--n:' + n + ';--p:' + (x.points / 10) + '%">'
      + '<span class="khn">' + (x.points >= 1000 ? '🎯 ' : '') + ctx.esc(nameAt(ctx, x.seat) || ctx.seatName(x.seat)) + '</span>'
      + '<span class="khv">' + money(x.value, v.unit) + '</span>'
      + '<span class="khd muted small">' + missText(x.ratio) + '</span>'
      + '<span class="khp">' + (x.points ? '+' + num(x.points) : '0') + '</span></div>').join('') + '</div>' + say;
  }

  function recapHtml(ctx, v) {
    const list = v.recap || [];
    if (!list.length) return '';
    return '<details class="khrecap" open><summary>Як це було · ' + list.length + ' ' + flatsWord(list.length) + '</summary><ol>'
      + list.map((r) => '<li><img src="' + ctx.esc(r.photo) + '" alt="" loading="lazy" referrerpolicy="no-referrer"><span class="khrc"><b>' + ctx.esc(r.city) + '</b> — '
        + '<span class="khra">' + money(r.price, r.unit) + ' ' + perText(r.per) + '</span>'
        + (r.link ? ' <a class="khrl" href="' + ctx.esc(r.link) + '"' + OUT + ' title="Відкрити оголошення">↗</a>' : '')
        + (r.best != null ? '<span class="khrb">найближче — ' + ctx.esc(nameAt(ctx, r.best) || ctx.seatName(r.best)) + ': ' + money(r.value, r.unit)
          + ' · <b>+' + num(r.points) + '</b></span>' : '<span class="khrb">ніхто не назвав ціни</span>') + '</span></li>').join('')
      + '</ol></details>';
  }

  const PLACE = ['перше', 'друге', 'третє', 'четверте', 'п’яте', 'шосте', 'сьоме', 'восьме', 'дев’яте', 'десяте', 'одинадцяте', 'дванадцяте'];
  const shardsOf = (ctx, v, i) => Math.floor((((v.result || {}).fresh || [])[i] || 0) / POINTS_PER_SHARD) * boostOf(ctx);

  function podiumHtml(ctx, v) {
    if (v.phase !== 'done' || !v.result) return '';
    const sc = v.scores || [];
    const list = seats(ctx);
    if (!list.length) return '';
    const bonus = (i) => (shardsOf(ctx, v, i) > 0 ? ' <span class="khshard">🏺+' + shardsOf(ctx, v, i) + '</span>' : '');
    if (list.length === 1) {
      const i = list[0];
      return '<div class="khpodt">🏠 ' + (i === ctx.seat ? 'Твій результат' : ctx.esc(nameAt(ctx, i))) + ': <b>' + pts(sc[i] || 0) + '</b>' + bonus(i) + '</div>';
    }
    const win = v.result.winners || [];
    if (!win.length) return '<div class="khpodt">🤷 Ніхто й близько не вгадав — нічия</div>';
    let html = '<div class="khpodt">🏆 ' + win.map((i) => '<b>' + ctx.esc(nameAt(ctx, i) || ctx.seatName(i)) + '</b>').join(' і ')
      + ' — ' + pts(sc[win[0]] || 0) + '</div>';
    if (ctx.mine && list.includes(ctx.seat)) {
      const my = sc[ctx.seat] || 0;
      const place = 1 + list.filter((i) => (sc[i] || 0) > my).length;
      const where = win.includes(ctx.seat)
        ? (win.length > 1 ? 'Ти серед переможців!' : 'Це ти — перше місце з ' + list.length + '!')
        : 'Ти — ' + (PLACE[place - 1] || place + '-е') + ' місце з ' + list.length + ' · ' + pts(my);
      html += '<div class="khpods muted">' + where + bonus(ctx.seat) + '</div>';
    }
    return html;
  }

  function paintWho(root, ctx) {
    const v = ctx.view || {};
    const f = fresh(ctx, v);
    const done = (f && f.answered) || v.answered || [];
    const show = ctx.playing && (v.phase === 'ask' || v.phase === 'between');
    setHtml(root.querySelector('.khwho'), show ? seats(ctx, true).map((i) => '<span class="khchip' + (done[i] ? ' on' : '') + '">'
      + (done[i] ? '✓ ' : '') + ctx.esc(nameAt(ctx, i)) + '</span>').join('') : '');
  }

  function paintScores(root, ctx) {
    const v = ctx.view || {};
    const f = fresh(ctx, v);
    const sc = (f && f.scores && f.scores.length) ? f.scores : (v.scores || []);
    const win = (v.result && v.result.winners) || [];
    const list = seats(ctx);
    const html = (!ctx.playing && !v.result) || (!v.result && !list.some((i) => sc[i])) ? '' : list
      .slice()
      .sort((a, b) => (sc[b] || 0) - (sc[a] || 0) || a - b)
      .map((i) => {
        const shards = v.result ? shardsOf(ctx, v, i) : 0;
        return '<div class="khsrow' + (win.includes(i) ? ' win' : '') + '"><span>' + ctx.esc(nameAt(ctx, i)) + '</span>'
          + (shards > 0 ? '<i class="khshard" title="черепки за квартири, бачені вперше">🏺+' + shards + '</i>' : '')
          + '<b>' + num(sc[i] || 0) + '</b></div>';
      }).join('');
    setHtml(root.querySelector('.khscore'), html);
  }

  function paintPreview(root) {
    const input = root.querySelector('.khin');
    const box = root.querySelector('.khprevw');
    if (!input || !box) return;
    const raw = (input.value || '').trim();
    const n = raw ? parsePrice(raw) : null;
    const sent = root._kh && root._kh.my;
    const unit = (root.querySelector('.khunit') || {}).textContent || '';
    const text = !raw || input.disabled || (n != null && sent != null && Math.abs(n - sent) < 1e-9) ? ''
      : n == null ? 'Не схоже на ціну — лише цифри: «15000», «15 000» або «15к»'
      : n >= 10000 || String(n) !== raw ? '= ' + money(n, unit)
      : '';
    if (box.textContent !== text) box.textContent = text;
    box.classList.toggle('bad', !!raw && n == null);
  }

  function answer(root, ctx) {
    const input = root.querySelector('.khin');
    if (!input) return;
    const raw = (input.value || '').trim();
    if (!raw) { ctx.toast('Ану, назви ціну', 'err'); input.focus(); return; }
    Promise.resolve(ctx.act('answer', { value: raw })).then((r) => {
      // На телефоні після відповіді клавіатура ховається — вона закривала фото й розкриття.
      if (r && r.ok && HGames.ui.coarse() && document.activeElement === input) input.blur();
    }, () => {});
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = state(root);
    const phase = v.phase || 'between';
    const mine = ctx.mine && ctx.playing;
    const lobby = !ctx.playing && !v.result;

    const no = root.querySelector('.khno');
    const noText = !v.of || lobby ? '' : phase === 'done' ? 'Зіграно ' + v.of + ' ' + flatsWord(v.of) : 'Квартира ' + v.round + ' з ' + v.of;
    if (no.textContent !== noText) no.textContent = noText;

    const top = root.querySelector('.khtop');
    if (ctx.playing && v.endsAt && phase !== 'done') {
      const total = phase === 'ask' && v.seconds ? v.seconds * 1000 : (MS[phase] || 30000);
      HGames.ui.timerArc(top, v.endsAt, total);
    } else {
      const arc = top.querySelector(':scope > .garc');
      if (arc) { if (arc._arc) arc._arc.stop(); arc.remove(); }
    }
    // У лобі ні номера квартири, ні відліку — порожня смужка лише відсувала б текст.
    top.hidden = !noText && !top.querySelector(':scope > .garc');

    const pod = root.querySelector('.khpod');
    const podHtml = podiumHtml(ctx, v);
    setHtml(pod, podHtml);
    pod.hidden = !podHtml;

    paintPhoto(root, ctx, v);

    const city = root.querySelector('.khcity');
    const cityText = lobby ? 'Господар тисне «Почати» — можна й самому, а хто встигне, підсяде'
      : phase === 'between' ? 'Готуйсь — зараз покажуть квартиру…'
      : phase === 'done' ? '' : (v.city || '');
    if (city.textContent !== cityText) city.textContent = cityText;
    city.classList.toggle('wait', lobby || phase === 'between');
    city.hidden = !cityText;

    const rules = root.querySelector('.khrules');
    rules.hidden = !(lobby || (phase === 'between' && !v.result));

    const own = root.querySelector('.khown');
    const ownText = v.own && phase === 'ask' ? '🏠 Це твоя квартира — ти ціну знаєш, тож мовчиш. Дивись, як мучаться інші 😉' : '';
    if (own.textContent !== ownText) own.textContent = ownText;
    own.hidden = !ownText;

    const ask = root.querySelector('.khask');
    const input = root.querySelector('.khin');
    if (st.round !== v.round) { st.round = v.round; input.value = ''; }
    st.my = phase === 'ask' ? v.my : null;
    const canAsk = mine && phase === 'ask' && !v.own;
    const opened = canAsk && ask.hidden;
    ask.hidden = !canAsk;
    input.disabled = !canAsk;
    // Квартира з'явилась — курсор одразу в полі; на телефоні — ні: клавіатура закрила б фото.
    if (opened && !HGames.ui.coarse() && !document.querySelector('.modal:not([hidden]) input:focus')) {
      const busy = document.activeElement;
      if (!busy || busy === document.body || !/^(INPUT|TEXTAREA|SELECT)$/.test(busy.tagName)) input.focus();
    }
    const unit = root.querySelector('.khunit');
    const unitText = v.unit ? v.unit + ' ' + perText(v.per) : '';
    if (unit.textContent !== unitText) unit.textContent = unitText;

    const my = root.querySelector('.khmy');
    const myText = v.my != null && phase === 'ask' ? 'Твоя ціна: ' + money(v.my, v.unit) + '. Можна змінити, поки є час'
      : !ctx.mine && ctx.playing && phase === 'ask' ? 'Дивишся збоку'
      : '';
    if (my.textContent !== myText) my.textContent = myText;

    setHtml(root.querySelector('.khrev'), phase === 'reveal' ? revealHtml(ctx, v, !!(st.bank && st.bank.admin)) : '');
    setHtml(root.querySelector('.khrecapbox'), phase === 'done' ? recapHtml(ctx, v) : '');
    paintPreview(root);
    paintWho(root, ctx);
    paintScores(root, ctx);

    // Банк квартир — у лобі й після партії; посеред гри він лише відволікав би.
    const bank = root.querySelector('.khbank');
    const showBank = !ctx.playing;
    if (bank.hidden === showBank) {
      bank.hidden = !showBank;
      if (showBank) loadBank(root, ctx, false);
    }
    // Перемальовуємо, коли змінилось те, що в ньому написано: скільки квартир і для яких міст стіл (⚙ Налаштування).
    const sig = [(v.bank || {}).fit, (v.bank || {}).total, regionOpt(ctx)].join('|');
    if (showBank && st.bankSig !== sig) { st.bankSig = sig; renderBank(root, ctx); }
  }

  const regionOpt = (ctx) => String(((ctx.room && ctx.room.options) || {}).region || 'all');

  /// Скільки квартир банку пасує столу. Рахуємо самі зі свіжого /bank: вид кімнати приходить лише з подіями столу,
  /// тож після «Додати квартиру» у ньому ще стара кількість.
  function fitOf(ctx, st) {
    const b = st.bank;
    if (!b || !b.byRegion) return ((ctx.view || {}).bank || {}).fit;
    const picked = regionOpt(ctx).split(',').filter((r) => r in b.byRegion);
    return !picked.length || regionOpt(ctx).split(',').includes('all') ? b.total : picked.reduce((n, r) => n + (b.byRegion[r] || 0), 0);
  }

  // =========================================================================================
  // банк квартир: скільки є, додати свою, мої; розробнику — схвалити, виправити, зняти
  // =========================================================================================

  async function api(ctx, method, url, body) {
    const h = { 'X-Nick': encodeURIComponent((ctx.me && ctx.me.nick) || '') };
    let b;
    if (body) { h['Content-Type'] = 'application/json'; b = JSON.stringify(body); }
    const r = await fetch(url, { method, headers: h, body: b, credentials: 'same-origin' });
    let d = null;
    try { d = await r.json(); } catch { /* без тіла */ }
    if (!r.ok) throw new Error((d && d.message) || 'HTTP ' + r.status);
    return d || {};
  }

  async function loadBank(root, ctx, force) {
    const st = state(root);
    if (st.bankBusy || (!force && st.bank && Date.now() - st.bankAt < 30000)) return;
    st.bankBusy = true;
    try {
      st.bank = await api(ctx, 'GET', API + '/bank');
      st.bankAt = Date.now();
    } catch (e) {
      st.bankErr = e.message;
    } finally {
      st.bankBusy = false;
      if (root._kh === st) {
        renderBank(root, ctx);
        // Імпорт світу йде фоном кілька хвилин — поки йде, поступ перечитуємо самі.
        const box = root.querySelector('.khbank');
        if (st.bank && st.bank.import && st.bank.import.running && box && !box.hidden) {
          clearTimeout(st.poll);
          st.poll = setTimeout(() => { if (root._kh === st) loadBank(root, ctx, true); }, 3000);
        }
      }
    }
  }

  const unitOf = (region) => (REGIONS.find((r) => r[0] === region) || REGIONS[0])[2];

  function flatRow(ctx, st, f, admin) {
    const editing = st.edit === f.id;
    const live = f.status === 'ok';
    const thumb = '<button type="button" class="khfth" data-kh="view" data-id="' + f.id + '" title="Переглянути фото">'
      + '<img src="' + ctx.esc(f.photos[0]) + '" alt="" loading="lazy">' + (f.photos.length > 1 ? '<i>' + f.photos.length + '</i>' : '') + '</button>';
    if (editing) {
      return '<div class="khfl" data-id="' + f.id + '">' + thumb + '<div class="khfe">'
        + '<select class="khe-region">' + REGIONS.map((r) => '<option value="' + r[0] + '"' + (r[0] === f.region ? ' selected' : '') + '>' + r[1] + '</option>').join('') + '</select>'
        + '<input class="khe-city" type="text" maxlength="40" value="' + ctx.esc(f.city) + '" aria-label="Місто">'
        + '<input class="khe-price" type="text" inputmode="numeric" value="' + f.price + '" aria-label="Ціна за місяць">'
        + '<button type="button" class="primary" data-kh="save" data-id="' + f.id + '">Зберегти</button>'
        + '<button type="button" class="ghost" data-kh="cancel">Скасувати</button></div></div>';
    }
    return '<div class="khfl" data-id="' + f.id + '">' + thumb
      + '<div class="khfi"><b>' + ctx.esc(f.city) + '</b><span>' + money(f.price, f.unit) + ' за місяць</span>'
      + '<span class="muted small">' + (admin ? 'від ' + ctx.esc(f.by) + ' · ' : '') + (live ? '✅ у грі' : '⏳ чекає на схвалення') + '</span></div>'
      + '<div class="khfa">'
      + (admin && !live ? '<button type="button" class="primary" data-kh="approve" data-id="' + f.id + '">✓ Схвалити</button>' : '')
      + (admin ? '<button type="button" class="ghost" data-kh="edit" data-id="' + f.id + '" title="Виправити місто чи ціну">✎</button>' : '')
      + '<button type="button" class="ghost" data-kh="remove" data-id="' + f.id + '" title="Зняти квартиру">🗑</button></div></div>';
  }

  function formHtml(ctx, st) {
    const fm = st.form;
    const lim = (st.bank && st.bank.limits) || { photos: 5, city: 40 };
    return '<div class="khform">'
      + '<div class="khfrow khregs" role="radiogroup" aria-label="Де квартира">' + REGIONS.map((r) =>
        '<button type="button" class="khreg' + (fm.region === r[0] ? ' on' : '') + '" role="radio" aria-checked="' + (fm.region === r[0]) + '" data-kh="region" data-v="' + r[0] + '">'
        + r[1] + ' · ' + r[2] + '</button>').join('') + '</div>'
      + '<div class="khfrow"><input class="khf-city" type="text" maxlength="' + lim.city + '" placeholder="Місто — єдине, що побачать гравці" aria-label="Місто" value="' + ctx.esc(fm.city) + '"></div>'
      + '<div class="khfrow"><input class="khf-price" type="text" inputmode="numeric" placeholder="Ціна оренди" aria-label="Ціна оренди за місяць" value="' + ctx.esc(fm.price) + '">'
      + '<span class="khfunit">' + unitOf(fm.region) + ' за місяць</span></div>'
      + '<div class="khfphotos">' + fm.photos.map((p, i) => '<span class="khfp"><img src="' + p.url + '" alt="Фото ' + (i + 1) + '">'
        + '<button type="button" data-kh="unphoto" data-i="' + i + '" aria-label="Прибрати фото">✕</button></span>').join('')
      + (fm.photos.length < lim.photos ? '<button type="button" class="khfadd" data-kh="pick">📷<small>' + (fm.photos.length ? 'ще фото' : 'додати фото') + '</small></button>' : '')
      + '</div>'
      + '<div class="muted small">До ' + lim.photos + ' фото. Вони стискаються у браузері, геомітки зникають. Бери ті, де видно саму квартиру: без облич, адрес і номерів телефону. '
      + 'Текстом гравці побачать лише місто.</div>'
      + '<div class="khfrow khfbtns"><button type="button" class="primary" data-kh="send"' + (fm.busy ? ' disabled' : '') + '>' + (fm.busy ? 'Надсилаю…' : 'Додати в банк') + '</button>'
      + '<button type="button" class="ghost" data-kh="close">Скасувати</button></div></div>';
  }

  function renderBank(root, ctx) {
    const st = state(root);
    const box = root.querySelector('.khbank');
    if (!box || box.hidden) return;
    // Форму не перебудовуємо, поки людина в ній пише: значення полів забираємо в стан перед кожним малюванням.
    keepForm(root);
    const b = st.bank;
    const v = ctx.view || {};
    const total = b ? b.total : (v.bank || {}).total;
    const fit = fitOf(ctx, st);
    let html = '<div class="khbh"><span>🏠 У банку <b>' + (total == null ? '…' : num(total)) + '</b> ' + flatsWord(total || 0)
      + (total && fit != null && fit !== total ? ' <span class="muted">· для обраних міст — ' + num(fit) + '</span>' : '') + '</span>';
    if (!b) html += '<span class="muted small">' + (st.bankErr ? 'Банк не відповів: ' + ctx.esc(st.bankErr) : 'Зазираю в банк…') + '</span></div>';
    else {
      html += (b.account
        ? (st.form ? '' : '<button type="button" class="primary" data-kh="open">🏠 Додати квартиру</button>')
        : '<span class="muted small">Додавати квартири можна з акаунта</span>') + '</div>';
      const src = b.bySource || {};
      if (src.airbnb) html += '<div class="muted small">Зі світу — ' + num(src.airbnb) + ' справжніх оголошень Airbnb (ціна за ніч)'
        + (src.own ? ', від гравців — ' + num(src.own) : '') + '. Дані світу — <a href="https://insideairbnb.com/"' + OUT + '>Inside Airbnb</a>, CC BY 4.0; '
        + 'фото показуються просто з Airbnb.</div>';
      else if (!total && !st.form) html += '<div class="muted small">Банк порожній. '
        + (b.admin ? 'Набери світ кнопкою нижче або додай квартиру сам.' : 'Світ набирає розробник; свою квартиру можна додати кнопкою вище.') + '</div>';
      if (b.admin) {
        const im = b.import || {};
        html += '<div class="khimp">' + (im.running
          ? '<span>🌍 Набираю світ: ' + im.done + ' з ' + im.total + ' міст · нових ' + num(im.added) + ' · ' + ctx.esc(im.message || '') + '</span>'
          : '<button type="button" class="ghost" data-kh="import">🌍 ' + (src.airbnb ? 'Донабрати світ' : 'Набрати світ') + ' з Inside Airbnb</button>'
            + (im.message ? '<span class="muted small">' + ctx.esc(im.message) + '</span>' : '')) + '</div>';
      }
      if (b.account && !st.form) html += '<div class="muted small">За кожну схвалену квартиру — 🏺 ' + b.reward + '. Свою квартиру в компанії ти не вгадуєш.</div>';
      if (st.form) html += formHtml(ctx, st);
      const pending = (b.all || []).filter((f) => f.status !== 'ok');
      const live = (b.all || []).filter((f) => f.status === 'ok');
      if (b.admin && pending.length) html += '<details class="khlist" open><summary>🛠 Чекають на схвалення · ' + pending.length + '</summary>'
        + pending.map((f) => flatRow(ctx, st, f, true)).join('') + '</details>';
      const mine = (b.mine || []).filter((f) => !b.admin || !pending.some((p) => p.id === f.id));
      if (!b.admin && mine.length) html += '<details class="khlist"' + (st.openMine ? ' open' : '') + ' data-list="mine"><summary>Мої квартири · ' + mine.length + '</summary>'
        + mine.map((f) => flatRow(ctx, st, f, false)).join('') + '</details>';
      if (b.admin && live.length) html += '<details class="khlist"' + (st.openAll ? ' open' : '') + ' data-list="all"><summary>Усі квартири в грі · ' + live.length + '</summary>'
        + live.map((f) => flatRow(ctx, st, f, true)).join('') + '</details>';
    }
    const active = document.activeElement;
    const cls = active && box.contains(active) ? (active.className || '').split(' ').find((c) => c.startsWith('khf-') || c.startsWith('khe-')) : '';
    const pos = cls ? active.selectionStart : null;
    box.innerHTML = html;
    if (cls) {
      const again = box.querySelector('.' + cls);
      if (again) { again.focus(); try { if (pos != null) again.setSelectionRange(pos, pos); } catch { /* не текстове поле */ } }
    }
  }

  function keepForm(root) {
    const st = root._kh;
    if (!st || !st.form) return;
    const c = root.querySelector('.khf-city'), p = root.querySelector('.khf-price');
    if (c) st.form.city = c.value;
    if (p) st.form.price = p.value;
  }

  const toBlob = (c, type, q) => new Promise((done) => c.toBlob(done, type, q));

  /// Фото з телефона (4–12 МБ) → JPEG до 1280 px і ≤ 380 КБ. Малюємо через canvas — EXIF (і геомітка) лишається за бортом.
  async function shrinkPhoto(file) {
    const url = URL.createObjectURL(file);
    try {
      const img = await new Promise((ok, no) => {
        const i = new Image();
        i.onload = () => ok(i);
        i.onerror = () => no(new Error('Цей файл браузер не відкриває як фото — спробуй JPEG чи PNG'));
        i.src = url;
      });
      const w = img.naturalWidth, h = img.naturalHeight;
      for (const side of PH_SIDES) {
        const k = Math.min(1, side / Math.max(w, h));
        const c = document.createElement('canvas');
        c.width = Math.max(1, Math.round(w * k));
        c.height = Math.max(1, Math.round(h * k));
        if (Math.min(c.width, c.height) < PH_MIN) throw new Error('Фото замале або надто видовжене — візьми інше');
        const g = c.getContext('2d');
        g.fillStyle = '#ffffff';
        g.fillRect(0, 0, c.width, c.height);
        g.imageSmoothingQuality = 'high';
        g.drawImage(img, 0, 0, c.width, c.height);
        for (const q of [0.82, 0.7, 0.55]) {
          const b = await toBlob(c, 'image/jpeg', q);
          if (b && b.size <= PH_MAX) return b;
        }
      }
      throw new Error('Фото не вдалося стиснути — візьми інше');
    } finally {
      URL.revokeObjectURL(url);
    }
  }

  const toBase64 = (blob) => new Promise((ok, no) => {
    const r = new FileReader();
    r.onload = () => ok(String(r.result).split(',')[1] || '');
    r.onerror = () => no(new Error('Фото не прочиталось'));
    r.readAsDataURL(blob);
  });

  function pickPhotos(root, ctx) {
    const st = state(root);
    const lim = ((st.bank && st.bank.limits) || { photos: 5 }).photos;
    const inp = document.createElement('input');
    inp.type = 'file';
    inp.accept = 'image/jpeg,image/png,image/webp';
    inp.multiple = true;
    inp.onchange = async () => {
      const files = Array.from(inp.files || []);
      for (const f of files) {
        if (!st.form || st.form.photos.length >= lim) { if (st.form) ctx.toast('Не більше ' + lim + ' фото на квартиру', 'err'); break; }
        try {
          const blob = await shrinkPhoto(f);
          if (root._kh !== st || !st.form) return;
          st.form.photos.push({ blob, url: URL.createObjectURL(blob) });
          renderBank(root, ctx);
        } catch (e) { ctx.toast(e.message, 'err'); }
      }
    };
    inp.click();
  }

  function dropForm(st) {
    if (st.form) for (const p of st.form.photos) URL.revokeObjectURL(p.url);
    st.form = null;
  }

  async function sendFlat(root, ctx) {
    const st = state(root);
    keepForm(root);
    const fm = st.form;
    if (!fm || fm.busy) return;
    const city = fm.city.trim().replace(/\s+/g, ' ');
    const price = parsePrice(fm.price);
    if (city.length < 2) { ctx.toast('Напиши місто', 'err'); return; }
    if (price == null || price < 1) { ctx.toast('Напиши ціну оренди за місяць — числом', 'err'); return; }
    if (!fm.photos.length) { ctx.toast('Додай хоча б одне фото квартири', 'err'); return; }
    fm.busy = true;
    renderBank(root, ctx);
    try {
      const photos = [];
      for (const p of fm.photos) photos.push(await toBase64(p.blob));
      const d = await api(ctx, 'POST', API + '/flat', { region: fm.region, city, price: Math.round(price), photos });
      ctx.toast(d.message || 'Квартиру додано');
      dropForm(st);
      st.openMine = true;
      await loadBank(root, ctx, true);
    } catch (e) {
      ctx.toast(e.message, 'err');
      if (st.form) st.form.busy = false;
      if (root._kh === st) renderBank(root, ctx);
    }
  }

  function flatOf(st, id) {
    const b = st.bank || {};
    return (b.all || []).concat(b.mine || []).find((f) => f.id === id);
  }

  async function bankAct(root, ctx, url, body, ask) {
    if (ask && !window.confirm(ask)) return;
    const st = state(root);
    try {
      const d = await api(ctx, 'POST', API + url, body);
      if (d.message) ctx.toast(d.message);
      st.edit = null;
      await loadBank(root, ctx, true);
    } catch (e) { ctx.toast(e.message, 'err'); }
  }

  function onBank(root, ctx, e) {
    const st = state(root);
    const t = e.target.closest('[data-kh]');
    if (!t) return;
    const id = t.dataset.id ? +t.dataset.id : 0;
    switch (t.dataset.kh) {
      case 'open': st.form = { region: 'ua', city: '', price: '', photos: [], busy: false }; renderBank(root, ctx); break;
      case 'close': dropForm(st); renderBank(root, ctx); break;
      case 'region': keepForm(root); st.form.region = t.dataset.v; renderBank(root, ctx); break;
      case 'pick': keepForm(root); pickPhotos(root, ctx); break;
      case 'unphoto': {
        keepForm(root);
        const p = st.form.photos.splice(+t.dataset.i, 1)[0];
        if (p) URL.revokeObjectURL(p.url);
        renderBank(root, ctx);
        break;
      }
      case 'send': sendFlat(root, ctx); break;
      case 'view': { const f = flatOf(st, id); if (f) openViewer(root, f.photos, 0); break; }
      case 'approve': bankAct(root, ctx, '/flat/approve', { id }); break;
      case 'import': bankAct(root, ctx, '/import', {}); break;
      case 'remove': {
        const f = flatOf(st, id);
        bankAct(root, ctx, '/flat/remove', { id }, 'Зняти квартиру' + (f ? ' «' + f.city + '»' : '') + '? Фото видаляться.');
        break;
      }
      case 'edit': st.edit = id; renderBank(root, ctx); break;
      case 'cancel': st.edit = null; renderBank(root, ctx); break;
      case 'save': {
        const row = t.closest('.khfl');
        const price = parsePrice(row.querySelector('.khe-price').value);
        if (price == null || price < 1) { ctx.toast('Ціна — числом', 'err'); break; }
        bankAct(root, ctx, '/flat/edit', { id, region: row.querySelector('.khe-region').value, city: row.querySelector('.khe-city').value, price: Math.round(price) });
        break;
      }
      default: break;
    }
  }

  HGames.register({
    id: 'khata',
    added: '2026-11-17',          // нова гра: «🆕» у лобі два тижні тим, хто ще не грав
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd'],

    mount(root, ctx) {
      root._kh = null;
      state(root);
      root.innerHTML = '<div class="khwrap">'
        + '<div class="khmain">'
        + '<div class="khtop"><span class="khno muted small"></span></div>'
        + '<div class="khpod" hidden></div>'
        + '<figure class="khphoto" hidden><img alt="Фото квартири" decoding="async">'
        + '<button type="button" class="khprev" aria-label="Попереднє фото" hidden>‹</button>'
        + '<button type="button" class="khnext" aria-label="Наступне фото" hidden>›</button>'
        + '<div class="khdots"></div></figure>'
        + '<div class="khcity"></div>'
        + '<div class="khown small" hidden></div>'
        + '<div class="khrules muted small" hidden>' + rulesFor(ctx) + '</div>'
        + '<div class="khask" hidden><input class="khin" type="text" inputmode="decimal" enterkeyhint="done" autocomplete="off"'
        + ' placeholder="твоя ціна" aria-label="Твоя ціна оренди"><span class="khunit muted small"></span>'
        + '<button type="button" class="primary khgo">Відповісти</button></div>'
        + '<div class="khprevw small" aria-live="polite"></div>'
        + '<div class="khmy muted small"></div>'
        + '<div class="khrev"></div>'
        + '<div class="khrecapbox"></div>'
        + '<div class="khwho"></div>'
        + '<div class="khbank" hidden></div>'
        + '</div>'
        + '<div class="khscore"></div>'
        + '</div>';
      root.querySelector('.khgo').onclick = () => answer(root, ctx);
      const input = root.querySelector('.khin');
      input.addEventListener('keydown', (e) => {
        if (e.key !== 'Enter') return;
        e.preventDefault();
        answer(root, ctx);
      });
      input.addEventListener('input', () => paintPreview(root));
      const fig = root.querySelector('.khphoto');
      const photo = fig.querySelector('img');
      photo.fetchPriority = 'high';
      // Фото сторонніх оголошень вантажаться просто з сайту-джерела: звідки прийшли — не кажемо.
      photo.referrerPolicy = 'no-referrer';
      // Не відкрилось — мабуть, оголошення зняли: сервер перевірить сам і прибере його з банку.
      photo.addEventListener('error', () => {
        const src = photo.getAttribute('src') || '';
        const st = state(root);
        if (!/^https:/.test(src) || st.broken === src) return;
        st.broken = src;
        ctx.toast('Фото не відкрилось — схоже, це оголошення вже зняли', 'err');
        api(ctx, 'POST', API + '/broken', { url: src }).catch(() => {});
      });
      root.querySelector('.khrev').addEventListener('click', (e) => {
        const b = e.target.closest('[data-drop]');
        if (b) bankAct(root, ctx, '/flat/remove', { id: +b.dataset.drop }, 'Прибрати це оголошення з гри назавжди?');
      });
      swipe(fig, (d) => turn(root, ctx, d));
      fig.addEventListener('click', (e) => {
        if (e.target.closest('.khprev')) turn(root, ctx, -1);
        else if (e.target.closest('.khnext')) turn(root, ctx, 1);
        else if (e.target.tagName === 'IMG') { const st = state(root); openViewer(root, st.key ? st.key.split('|') : [], st.idx); }
      });
      root.querySelector('.khbank').addEventListener('click', (e) => onBank(root, ctx, e));
      root.querySelector('.khbank').addEventListener('toggle', (e) => {
        const d = e.target;
        if (!d.dataset || !d.dataset.list) return;
        const st = state(root);
        if (d.dataset.list === 'mine') st.openMine = d.open; else st.openAll = d.open;
      }, true);
      paint(root, ctx);
    },

    update(root, ctx) { paint(root, ctx); },

    /// Кадр не чіпає ні фото, ні розкриття — лише галочки й рахунок.
    frame(root, ctx) {
      if (!root.querySelector('.khwho')) return;
      paintWho(root, ctx);
      paintScores(root, ctx);
    },

    /// Перегляд на весь екран: Esc — закрити, стрілки — гортати.
    onKey(e, ctx) {
      const box = document.querySelector('.khview');
      if (!box) return false;
      if (e.key === 'Escape') { box.click(); return true; }
      if (e.key === 'ArrowLeft') { box.querySelector('.khvprev').click(); return true; }
      if (e.key === 'ArrowRight') { box.querySelector('.khvnext').click(); return true; }
      return false;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      const s = ctx.view || {};
      if (s.phase === 'ask' && s.own && ctx.mine) return '';   // про це вже каже рядок під містом
      if (s.phase === 'ask') return ctx.mine ? (ctx.ui.coarse() ? 'Назви ціну й тисни «Відповісти»' : 'Назви ціну й тисни Enter') : 'Гравці прицінюються…';
      if (s.phase === 'reveal') return 'Ось скільки вона коштує насправді';
      if (s.phase === 'between') return 'Зараз буде квартира…';
      return '';
    },

    unmount(root) {
      const arc = root.querySelector('.garc');
      if (arc && arc._arc) arc._arc.stop();
      closeViewer(root);
      if (root._kh) { dropForm(root._kh); clearTimeout(root._kh.poll); }
      root._kh = null;
    },
  });
})();
