/*
  Доміно. Правила й увесь стан — на сервері (Impl/Domino.cs); тут лише малюнок і наміри:
  «поклади оцю кістку», «тягну з базару», «пас».

  Вид із сервера (гра Hidden, тож у кожного свій):
  { turn, players, round, line: [{ tile:[a,b], double }], ends: [l,r]|null, hand: [[a,b]]|null,
    counts: number[], boneyard, scores: number[], canPlay, mustDraw,
    lastRound: { winner, points, reason, round, left: [a,b][][] }|null, result: { winner|null, scores }|null,
    target: 1|50|100 }   // 1 — партія з одного раунду

  Прохід №3 (29.09): kozel (дворовий «Козел» 2×2 до 101: scores — штраф пари, пари 0+2 і 1+3), bots (імена Глеків по
  місцях), botIn (коли Глек «подумав» — штовхаємо його дією 'bot'), goats (козли минулої партії). «Риба!» і вихід —
  з грюком: кістка б'є об стіл (анімація; звук — лише з вимикачем 🔈, типово тихо).

  Ланцюг приходить уже орієнтованим (line[i].tile[1] === line[i+1].tile[0]), тому перевертати
  половинки самим не треба — малюємо як є, а дублі кладемо поперек.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1" y="4" width="14" height="8" rx="2.2" fill="none" stroke="var(--accent)" stroke-width="1.4"/>'
    + '<path d="M8 4.2 V11.8" stroke="var(--accent)" stroke-width="1.1"/>'
    + '<circle cx="4.6" cy="8" r="1.15" fill="var(--ok)"/>'
    + '<circle cx="11.3" cy="6.4" r="1.15" fill="var(--ok)"/>'
    + '<circle cx="11.3" cy="9.6" r="1.15" fill="var(--ok)"/></svg>';

  // Ті самі дев'ять позицій, що й у кубика в app.js: половинка кістки — це сітка 3×3.
  const PIPS = { 0: [], 1: [4], 2: [0, 8], 3: [0, 4, 8], 4: [0, 2, 6, 8], 5: [0, 2, 4, 6, 8], 6: [0, 2, 3, 5, 6, 8] };
  const half = (n) => '<span class="dom-half">'
    + Array.from({ length: 9 }, (_, i) => '<i' + (PIPS[n] && PIPS[n].includes(i) ? ' class="on"' : '') + '></i>').join('')
    + '</span>';
  /// Кістка: дві половинки й риска між ними. Дубль ставимо вертикально — так його видно в ланцюгу.
  const boneHtml = (t, cls) => '<span class="dom-bone' + (t[0] === t[1] ? ' dom-dbl' : '') + (cls ? ' ' + cls : '') + '">'
    + half(t[0]) + '<span class="dom-bar"></span>' + half(t[1]) + '</span>';

  const TARGET = 100;   // класична межа; стіл може грати коротше — тоді її каже view.target
  const targetOf = (v) => (v && v.target) || TARGET;
  /// «100 очок», «104 очки», «101 очко» — число в рядку має читатись по-людськи.
  const pips = (n) => n + ' ' + (n % 100 >= 11 && n % 100 <= 14 ? 'очок'
    : n % 10 === 1 ? 'очко'
      : n % 10 >= 2 && n % 10 <= 4 ? 'очки' : 'очок');

  const same = (a, b) => a && b && ((a[0] === b[0] && a[1] === b[1]) || (a[0] === b[1] && a[1] === b[0]));
  const fits = (t, end) => end != null && (t[0] === end || t[1] === end);

  /// Стан самої картки (а не партії): яку кістку гравець тримає «на вильоті», поки обирає бік.
  const stateOf = (root) => (root._dom || (root._dom = { pick: null }));

  /// Ім'я на місці: нік людини або «Глек 🤖» (місце бота в каркасі порожнє). Козлам минулої партії — 🐐.
  function nameAt(ctx, i) {
    const v = ctx.view || {};
    const n = ctx.nickOf(i) || (v.bots && v.bots[i]) || '';
    return n && v.goats && v.goats.includes(n) ? n + ' 🐐' : n;
  }
  const whoAt = (ctx, i) => nameAt(ctx, i) || ctx.seatName(i);

  // ---- «Риба!» з грюком (№209): звук лише з вимикачем, типово тихо — як у Кістках ----
  const soundOn = () => { try { return localStorage.getItem('domino.sound') === '1'; } catch { return false; } };
  let actx = null;
  /// Кістка об стіл: глухий удар (низький синус, що швидко падає) і тріск дерева (шум крізь смугу). Лише після жесту.
  function knock(times) {
    if (!soundOn()) return;
    const AC = window.AudioContext || window.webkitAudioContext;
    const ua = navigator.userActivation;
    if (!AC || (ua && !ua.hasBeenActive)) return;
    try { actx = actx || new AC(); } catch { return; }
    if (actx.state === 'suspended') actx.resume().catch(() => {});
    const t0 = actx.currentTime + 0.02;
    for (let k = 0; k < times; k++) {
      const t = t0 + k * 0.16;
      const o = actx.createOscillator(), g = actx.createGain();
      o.frequency.setValueAtTime(150, t);
      o.frequency.exponentialRampToValueAtTime(55, t + 0.12);
      g.gain.setValueAtTime(0.55, t);
      g.gain.exponentialRampToValueAtTime(0.001, t + 0.18);
      o.connect(g).connect(actx.destination);
      o.start(t); o.stop(t + 0.2);
      const len = Math.floor(actx.sampleRate * 0.06);
      const buf = actx.createBuffer(1, len, actx.sampleRate);
      const d = buf.getChannelData(0);
      for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, 3);
      const n = actx.createBufferSource(), f = actx.createBiquadFilter(), ng = actx.createGain();
      n.buffer = buf; f.type = 'bandpass'; f.frequency.value = 1800; f.Q.value = 0.8; ng.gain.value = 0.35;
      n.connect(f).connect(ng).connect(actx.destination);
      n.start(t);
    }
  }

  /// Новий кінець раунду — грюк: «Риба!» двічі, вихід — раз. Першу картинку (відкрили стіл посеред партії) не чіпаємо.
  function bang(root, v) {
    const st = stateOf(root);
    const last = v.lastRound;
    const key = last && last.reason ? (last.round || 0) + ':' + last.reason : '';
    const was = st.bangKey;
    st.bangKey = key;
    if (was === undefined || !key || key === was) return;
    const fish = last.reason === 'fish';
    let el = root.querySelector(':scope > .dom-slam');
    if (!el) { el = document.createElement('div'); el.className = 'dom-slam'; root.appendChild(el); }
    el.textContent = fish ? '🐟 Риба!' : '💥 Є! Вийшов!';
    el.classList.remove('go');
    void el.offsetWidth;                                   // перезапустити анімацію
    el.classList.add('go');
    clearTimeout(st.slamT);
    st.slamT = setTimeout(() => el.classList.remove('go'), 1400);
    knock(fish ? 2 : 1);
  }

  /// Глек, чия черга, ходить на штовхан нашого клієнта (тика в гри нема). Першим штовхає перша людина за столом,
  /// решта — із запасом. Зарано чи вдруге — сервер відмовить, а send шле без тосту.
  function nudge(root, ctx) {
    const st = stateOf(root);
    const v = ctx.view || {};
    clearTimeout(st.nt);
    st.nt = 0;
    if (!ctx.playing || ctx.seat == null || typeof v.botIn !== 'number' || !HGames.send) return;
    let rank = 0;
    for (let i = 0; i < ctx.seat; i++) if (ctx.nickOf(i)) rank++;
    const fire = () => {
      if (!root._dom || !ctx.room) return;
      HGames.send('Act', ctx.room.id, 'bot', null);
      st.nt = setTimeout(fire, 1500);
    };
    st.nt = setTimeout(fire, v.botIn + 60 + rank * 700);
  }

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = stateOf(root);
    // Стіл у лобі — тіло порожнє. Дограний стіл каркас віддає новому гравцеві (Rooms.Join, гілка
    // reopen) і вертає кімнату в лобі, а вид гри до самого «Почати» тримає стару партію разом із
    // result: без цього новачок бачив би залишки чужої роздачі й чужий рахунок. Тому дивимось саме
    // на стан кімнати, а не на вид.
    const idle = !ctx.playing && !(ctx.room && ctx.room.status === 'finished');
    const line = idle ? [] : (v.line || []);
    const ends = v.ends || null;
    const hand = idle ? [] : (v.hand || []);
    const my = !idle && !!ctx.myTurn;
    if (!my && st.pick) st.pick = null;          // не твій хід — нема чого й обирати бік

    box(root, 'dom-head', idle ? '' : head(v, ctx));
    // Нова кістка в ланцюгу злітає на місце — ловимо її, порівнюючи з тим, що було до цього виду.
    const fresh = idle ? '' : freshSide(st, line);
    box(root, 'dom-round', idle ? '' : roundBox(v, ctx, line));
    box(root, 'dom-line', idle ? '' : (line.length
      ? line.map((b, i) => boneHtml(b.tile, (fresh === 'left' && i === 0) || (fresh === 'right' && i === line.length - 1) ? 'fresh' : '')).join('')
      : '<span class="muted small">' + (my ? 'Твій хід — бахни будь-яку кістку' : 'Чекаємо першу кістку') + '</span>'));

    // Рука — віяло каркаса: клік по кістці або ходить одразу, або питає, з якого боку класти.
    ctx.ui.hand(root, hand.map((t) => ({ t, disabled: !(my && (line.length === 0 || fits(t, ends[0]) || fits(t, ends[1]))) })), {
      render: (it) => boneHtml(it.t, same(it.t, st.pick) ? 'sel' : ''),
      onItem: (it) => {
        if (!my) return;
        const both = line.length > 0 && fits(it.t, ends[0]) && fits(it.t, ends[1]) && ends[0] !== ends[1];
        if (both) { st.pick = it.t; paint(root, ctx); return; }
        st.pick = null;
        ctx.act('play', { tile: it.t });
      },
    });

    box(root, 'dom-ctl', idle ? '' : controls(v, ctx, st));
    const snd = root.querySelector('.dom-head [data-snd]');
    if (snd) snd.onclick = () => {
      try { localStorage.setItem('domino.sound', soundOn() ? '0' : '1'); } catch { /* приватне вікно */ }
      paint(root, ctx);
      knock(1);
    };
    root.querySelectorAll('.dom-ctl [data-act]').forEach((b) => b.onclick = () => {
      const what = b.dataset.act;
      if (what === 'left' || what === 'right') {
        const tile = st.pick;
        st.pick = null;
        if (tile) ctx.act('play', { tile, end: what });
        return;
      }
      if (what === 'cancel') { st.pick = null; paint(root, ctx); return; }
      ctx.act(what);
    });
  }

  /// З якого боку ланцюг виріс відтоді, як ми його бачили: 'left' | 'right' | ''.
  function freshSide(st, line) {
    const sig = line.map((b) => b.tile.join('')).join('|');
    const was = st.line || '';
    st.line = sig;
    if (!was || sig === was || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return '';
    if (sig.endsWith('|' + was)) return 'left';
    if (sig.startsWith(was + '|')) return 'right';
    return '';
  }

  /// Підсумок щойно зіграного раунду — великим, поки в новому раунді ще ніхто не походив: хто вийшов
  /// чи чия риба, скільки очок і що в кого лишилось на руках. Далі він стискається в рядок у шапці.
  function roundBox(v, ctx, line) {
    const last = v.lastRound;
    // Наприкінці партії теж показуємо: останній раунд і є тим, що все вирішив.
    if (!last || !last.reason || (line.length && !v.result)) return '';
    const who = last.winner == null ? '' : ctx.esc(whoAt(ctx, last.winner));
    const pair = (s) => ctx.esc(whoAt(ctx, s)) + ' і ' + ctx.esc(whoAt(ctx, (s + 2) % 4));
    const title = v.kozel
      ? (last.winner == null ? '🐟 Риба — порівну, штраф нікому'
        : last.reason === 'out'
          ? '🎉 ' + who + ' — усі кістки на столі! Штраф парі ' + pair((last.winner + 1) % 4) + ': +' + last.points
          : '🐟 Риба! Важчі руки в пари ' + pair((last.winner + 1) % 4) + ' — штраф +' + last.points)
      : last.reason === 'out'
      ? '🎉 ' + who + ' — усі кістки на столі! +' + last.points
      : last.winner == null ? '🐟 Риба — порівну, очки нікому' : '🐟 Риба! Найлегша рука в ' + who + ': +' + last.points;
    const left = (last.left || []).map((bones, i) => {
      if (!bones || !bones.length || !nameAt(ctx, i)) return '';
      const sum = bones.reduce((a, t) => a + t[0] + t[1], 0);
      return '<div class="dom-rl"><i>' + ctx.esc(nameAt(ctx, i)) + '</i>' + bones.map((t) => boneHtml(t, 'mini')).join('')
        + '<b>' + pips(sum) + '</b></div>';
    }).join('');
    return '<div class="dom-rt">Раунд ' + (last.round || Math.max(1, (v.round || 2) - 1)) + ': ' + title + '</div>' + left;
  }

  function head(v, ctx) {
    const scores = v.scores || [];
    const counts = v.counts || [];
    const chip = (i, score) => '<span class="dom-sc' + (i === v.turn ? ' on' : '') + '">'
      + ctx.esc(nameAt(ctx, i)) + (score ? ' <b>' + (scores[i] || 0) + '</b>' : '') + '<i>(' + (counts[i] || 0) + ')</i></span>';
    let chips = '';
    if (v.kozel) {
      // Пари через одного, штраф — один на пару.
      for (const t of [0, 1]) {
        chips += '<span class="dom-pair">' + chip(t, false) + '<em>+</em>' + chip(t + 2, false)
          + '<b title="Штраф пари: хто перший набрав ' + targetOf(v) + ', ті й козли">🐐 ' + (scores[t] || 0) + '</b></span>';
      }
    } else for (let i = 0; i < 4; i++) if (nameAt(ctx, i)) chips += chip(i, true);
    // Поки на столі великий підсумок раунду (новий ще не почався), рядок у шапці його лише дублював би.
    const last = v.lastRound && v.lastRound.reason && (v.line || []).length && !v.result
      ? '<span class="dom-last">' + (v.lastRound.winner == null
        ? 'минулий раунд: риба, очки нікому'
        : v.kozel
          ? 'минулий раунд: ' + (v.lastRound.reason === 'fish' ? 'риба' : ctx.esc(whoAt(ctx, v.lastRound.winner)) + ' вийшов')
            + ', штраф 🐐 +' + v.lastRound.points
          : 'минулий раунд: ' + ctx.esc(whoAt(ctx, v.lastRound.winner))
          + ' +' + v.lastRound.points) + '</span>'
      : '';
    const goal = v.kozel ? '🐐 «Козел» до ' + targetOf(v) : targetOf(v) <= 1 ? 'один раунд' : 'до ' + targetOf(v);
    const ends = v.ends ? '<span class="chip dom-ends" title="Вільні кінці ланцюга">кінці <b>' + v.ends[0] + '</b> · <b>' + v.ends[1] + '</b></span>' : '';
    const snd = '<button type="button" class="ghost dom-snd" data-snd title="Грюк кісткою на «Рибі» й виході">'
      + (soundOn() ? '🔈' : '🔇') + '</button>';
    return '<span class="chip">раунд ' + (v.round || 1) + ' · ' + goal + '</span>'
      + (v.kozel ? '' : '<span class="chip">базар ' + (v.boneyard || 0) + '</span>') + ends
      + chips + last + snd;
  }

  function controls(v, ctx, st) {
    if (st.pick) {
      const e = v.ends || [0, 0];
      return '<span class="muted small">З якого боку?</span>'
        + '<button class="primary" data-act="left">◀ до ' + e[0] + '</button>'
        + '<button class="primary" data-act="right">до ' + e[1] + ' ▶</button>'
        + '<button class="ghost" data-act="cancel">Ні, іншу</button>';
    }
    if (!ctx.myTurn) return '';
    if (v.mustDraw) return '<button class="primary" data-act="draw">Тягнути (' + (v.boneyard || 0) + ')</button>';
    if (!v.canPlay) return '<button class="primary" data-act="pass">Пас</button>';
    return '<span class="muted small">Обери кістку</span>';
  }

  /// Свій блок усередині .gbody: створюємо раз, далі лише міняємо вміст — щоб не смикався ані ланцюг, ані рука.
  function box(root, cls, html) {
    let el = root.querySelector(':scope > .' + cls);
    if (!el) {
      el = document.createElement('div');
      el.className = cls;
      // Порядок блоків задає CSS через order, тож можна просто дописувати в кінець.
      root.appendChild(el);
    }
    if (el.dataset.sig !== html) { el.dataset.sig = html; el.innerHTML = html; }
    // Порожній блок ховаємо: у ланцюга своя підкладка, і в лобі вона світила б порожньою плитою.
    el.hidden = !html;
    return el;
  }

  HGames.register({
    id: 'domino',
    icon: ICON,
    seatNames: ['перший', 'другий', 'третій', 'четвертий'],
    seatClass: ['x', 'o', 'c', 'd'],
    news: {
      v: '2026-09-29',
      title: 'Доміно: «Козел», Глек і «Риба!» з грюком',
      items: [
        '🐐 Новий режим «Козел» 2×2: пари через одного, по сім кісток, до 101 штрафу — хто набрав, ті й козли',
        '🤖 Бракує людей — на порожнє місце підсяде Глек (без черепків)',
        '🐟 «Риба!» і вихід гримлять кісткою об стіл; звук — кнопкою 🔈 у шапці, типово тихо',
      ],
    },
    // Свій маркер на тілі картки: під ним живуть усі правила, що чіпають спільні .ghand/.gcard,
    // інакше вони поїхали б і в чужі ігри — файл стилів вантажиться на весь сайт.
    mount(root, ctx) { root.classList.add('dom-game'); paint(root, ctx); bang(root, ctx.view || {}); nudge(root, ctx); },
    update(root, ctx) { paint(root, ctx); bang(root, ctx.view || {}); nudge(root, ctx); },
    unmount(root) { const st = root._dom; if (st) { clearTimeout(st.nt); clearTimeout(st.slamT); } root._dom = null; },
    status(ctx) {
      const v = ctx.view || {};
      const room = ctx.room || {};
      if (room.status === 'lobby') {
        // MinPlayers = 1 заради Глека: сам-на-сам каркас сказав би «Можна рушати», а старт відмовить.
        const opt = room.options || {};
        let people = 0;
        for (let i = 0; i < 4; i++) if (ctx.nickOf(i)) people++;
        const all = people + +(opt.bots || 0);
        if (opt.mode === 'kozel' && all < 4) return '🐐 «Козел» — це дві пари: чекаємо четверо (або відкрий стіл з 🤖 Глеком)';
        return all < 2 ? 'Чекаємо, хто підсяде (або відкрий стіл з «🤖 Глек підсідає»)' : '';
      }
      if (v.kozel && ctx.room && ctx.room.status === 'finished' && v.goats && v.result) {
        const mine = ctx.seat != null && v.result.winner != null && ctx.seat % 2 === v.result.winner % 2;
        return (mine ? 'Є! ' : '') + '🐐 Козли — ' + v.goats.join(' і ');
      }
      if (ctx.playing && v.bots && v.turn != null && v.bots[v.turn]) return v.bots[v.turn] + ' думає…';
      // Рахунок кажемо лише тоді, коли партію справді догуляли до межі (view.target) і стіл ще дограний.
      // Перемога через те, що всі встали, стільки очок не має, а віддану новому гравцеві кімнату
      // каркас уже вернув у лобі — в обох випадках краще звучить його ж рядок.
      const won = (v.result && (v.result.scores || [])[v.result.winner]) || 0;
      const done = ctx.room && ctx.room.status === 'finished';
      const yes = v.result && v.result.winner != null && v.result.winner === ctx.seat ? 'Є! ' : '';   // «Є!» — лише переможцеві
      if (done && v.result && targetOf(v) <= 1) {
        return v.result.winner == null ? 'Риба порівну — нічия'
          : yes + 'Раунд і партія — ' + whoAt(ctx, v.result.winner);
      }
      if (done && won >= targetOf(v)) return yes + 'Партію зіграно: ' + whoAt(ctx, v.result.winner) + ' — ' + pips(won);
      if (!ctx.playing) return '';
      if (ctx.myTurn && v.mustDraw) return 'Нема чим ходити — тягни з базару';
      if (ctx.myTurn && !v.canPlay) return 'Ходити нема чим і базар порожній — пас';
      return '';
    },
  });
})();
