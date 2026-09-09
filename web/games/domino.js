/*
  Доміно. Правила й увесь стан — на сервері (Impl/Domino.cs); тут лише малюнок і наміри:
  «поклади оцю кістку», «тягну з базару», «пас».

  Вид із сервера (гра Hidden, тож у кожного свій):
  { turn, players, round, line: [{ tile:[a,b], double }], ends: [l,r]|null, hand: [[a,b]]|null,
    counts: number[], boneyard, scores: number[], canPlay, mustDraw,
    lastRound: { winner, points, reason }|null, result: { winner, scores }|null }

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
  const half = (n) => '<span class="dhalf">'
    + Array.from({ length: 9 }, (_, i) => '<i' + (PIPS[n] && PIPS[n].includes(i) ? ' class="on"' : '') + '></i>').join('')
    + '</span>';
  /// Кістка: дві половинки й риска між ними. Дубль ставимо вертикально — так його видно в ланцюгу.
  const boneHtml = (t, cls) => '<span class="dbone' + (t[0] === t[1] ? ' dbl' : '') + (cls ? ' ' + cls : '') + '">'
    + half(t[0]) + '<span class="dbar"></span>' + half(t[1]) + '</span>';

  const same = (a, b) => a && b && ((a[0] === b[0] && a[1] === b[1]) || (a[0] === b[1] && a[1] === b[0]));
  const fits = (t, end) => end != null && (t[0] === end || t[1] === end);

  /// Стан самої картки (а не партії): яку кістку гравець тримає «на вильоті», поки обирає бік.
  const stateOf = (root) => (root._dom || (root._dom = { pick: null }));

  function paint(root, ctx) {
    const v = ctx.view || {};
    const st = stateOf(root);
    const line = v.line || [];
    const ends = v.ends || null;
    const hand = v.hand || [];
    const my = !!ctx.myTurn;
    if (!my && st.pick) st.pick = null;          // не твій хід — нема чого й обирати бік

    box(root, 'dhead', head(v, ctx));
    box(root, 'dline', line.length
      ? line.map((b) => boneHtml(b.tile)).join('')
      : '<span class="muted small">кладіть першу кістку</span>');

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

    box(root, 'dctl', controls(v, ctx, st));
    root.querySelectorAll('.dctl [data-do]').forEach((b) => b.onclick = () => {
      const what = b.dataset.do;
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

  function head(v, ctx) {
    const seats = (ctx.room && ctx.room.seats) || [];
    const scores = v.scores || [];
    const counts = v.counts || [];
    const chips = seats.filter((s) => s.nick).map((s) => '<span class="dsc' + (s.i === v.turn ? ' on' : '') + '">'
      + ctx.esc(s.nick) + ' <b>' + (scores[s.i] || 0) + '</b>'
      + '<i>(' + (counts[s.i] || 0) + ')</i></span>').join('');
    const last = v.lastRound && v.lastRound.reason
      ? '<span class="dlast">' + (v.lastRound.winner == null
        ? 'минулий раунд: риба, очки нікому'
        : 'минулий раунд: ' + ctx.esc(ctx.nickOf(v.lastRound.winner) || ctx.seatName(v.lastRound.winner))
          + ' +' + v.lastRound.points) + '</span>'
      : '';
    return '<span class="chip">раунд ' + (v.round || 1) + '</span>'
      + '<span class="chip">базар ' + (v.boneyard || 0) + '</span>'
      + chips + last;
  }

  function controls(v, ctx, st) {
    if (st.pick) {
      const e = v.ends || [0, 0];
      return '<span class="muted small">З якого боку?</span>'
        + '<button class="primary" data-do="left">◀ до ' + e[0] + '</button>'
        + '<button class="primary" data-do="right">до ' + e[1] + ' ▶</button>'
        + '<button class="ghost" data-do="cancel">Ні, іншу</button>';
    }
    if (!ctx.myTurn) return '';
    if (v.mustDraw) return '<button class="primary" data-do="draw">Тягнути (' + (v.boneyard || 0) + ')</button>';
    if (!v.canPlay) return '<button class="primary" data-do="pass">Пас</button>';
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
    return el;
  }

  HGames.register({
    id: 'domino',
    icon: ICON,
    seatNames: ['перший', 'другий', 'третій', 'четвертий'],
    seatClass: ['x', 'o', 'c', 'd'],
    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },
    status(ctx) {
      const v = ctx.view || {};
      // Партія зі ста очок — кажемо рахунок; перемога через те, що всі встали, очок не має, і там
      // краще звучить типовий рядок каркаса «Перемога: X».
      const won = v.result && (v.result.scores || [])[v.result.winner];
      if (won) return 'Партію зіграно: ' + (ctx.nickOf(v.result.winner) || ctx.seatName(v.result.winner)) + ' — ' + won + ' очок';
      if (!ctx.playing) return '';
      if (ctx.myTurn && v.mustDraw) return 'Нема чим ходити — тягни з базару';
      if (ctx.myTurn && !v.canPlay) return 'Ходити нема чим і базар порожній — пас';
      return '';
    },
  });
})();
