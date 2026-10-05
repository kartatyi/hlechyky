// Проба міні-гри (mgprobe) — стенд хвилі «Вечірка»: одна міні-гра в режимі вечірки, вбудована через HGames.embed,
// тим самим шляхом, яким її покаже «Глечикова вечірка». Картка «як грати» 3 с → гра → таблиця місць → «Ще раз».
// У лобі гри нема: стіл відкривають посиланням #games/new/mgprobe. Контракт — docs/games/specs/party-minigame.md.
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<circle cx="8" cy="8" r="6.3" fill="none" stroke="var(--accent)" stroke-width="1.6"/>'
    + '<circle cx="8" cy="8" r="3.2" fill="none" stroke="var(--clay)" stroke-width="1.4"/>'
    + '<circle cx="8" cy="8" r="1.1" fill="var(--ok)"/></svg>';

  /// Стан кожного змонтованого столу: root → { ctx, emb, embKey, arc }.
  const live = new Set();
  const stOf = (ctx) => [...live].find((s) => s.ctx === ctx) || null;

  function shell(root, st) {
    if (st.el && st.el.isConnected) return;
    root.innerHTML = '<div class="mgp"><div class="mgp-card" hidden></div><div class="mgp-game"></div>'
      + '<div class="mgp-res" hidden></div><div class="mgp-note muted small"></div></div>';
    st.el = root.firstChild;
    st.card = st.el.querySelector('.mgp-card');
    st.game = st.el.querySelector('.mgp-game');
    st.res = st.el.querySelector('.mgp-res');
    st.note = st.el.querySelector('.mgp-note');
  }

  function dropEmbed(st) {
    if (st.emb) st.emb.unmount();
    st.emb = null;
    st.embKey = '';
  }

  /// Що віддати вбудованій грі з нашого виду.
  function embOpts(ctx, v) {
    return {
      view: v.mg || null,
      seat: v.sub == null ? null : v.sub,
      names: v.names || [],
      nicks: v.nicks || [],
      seatNames: v.seatNames || [],
      status: v.phase === 'result' ? 'finished' : 'playing',
      result: v.result ? { winners: v.result.winners || [], draw: false } : null,
      roomId: ctx.room && ctx.room.id,
      host: ctx.room && ctx.room.host,
    };
  }

  function render(root, ctx) {
    const st = root._mgp || (root._mgp = { emb: null, embKey: '' });
    st.ctx = ctx;
    st.root = root;
    live.add(st);
    shell(root, st);
    const v = ctx.view || {};
    const esc = ctx.esc;
    const lobby = ctx.room && ctx.room.status === 'lobby';
    const isHost = ctx.room && ctx.me && String(ctx.room.host || '').toLowerCase() === String(ctx.me.nick || '').toLowerCase();

    // картка «як грати»
    const showCard = !lobby && v.phase === 'howto';
    st.card.hidden = !showCard;
    if (showCard) {
      const key = v.round + ':' + v.game;
      if (st.cardKey !== key) {
        st.cardKey = key;
        st.card.innerHTML = '<div class="mgp-arc"></div><div><b>' + esc(v.title || v.game) + '</b><p>' + esc(v.howto || '') + '</p>'
          + '<p class="muted small">' + esc((v.names || []).join(' · ')) + '</p></div>';
        if (st.arc) st.arc.stop();
        st.arc = v.until ? ctx.ui.timerArc(st.card.querySelector('.mgp-arc'), v.until, v.howtoMs || 3000) : null;
      }
    } else if (st.arc) { st.arc.stop(); st.arc = null; st.cardKey = ''; }

    // сама міні-гра: монтуємо на play, лишаємо видно на result (підсумок на полі), знімаємо на новій картці
    if (lobby || v.phase === 'howto' || !v.game) dropEmbed(st);
    else {
      const key = v.round + ':' + v.game;
      if (st.embKey !== key) {
        dropEmbed(st);
        st.embKey = key;
        st.emb = HGames.embed(st.game, v.game, Object.assign(embOpts(ctx, v), {
          act: (a, p) => st.ctx.act('mg', { a, p }),
          input: (a, p) => st.ctx.input('mg', { a, p }),
        }));
      } else st.emb.update(embOpts(ctx, v));
    }

    // таблиця місць і «Ще раз»
    const res = !lobby && v.phase === 'result' && v.result;
    st.res.hidden = !res;
    if (res) {
      const order = (v.names || []).map((n, i) => i).sort((a, b) => res.places[a] - res.places[b] || a - b);
      const how = res.how === 'cap' ? ' · ⏱ стеля часу' : res.how === 'crash' ? ' · 💥 гра зламалась — усім порівну' : '';
      const games = v.games || [];
      st.res.innerHTML = '<h4>🎯 ' + esc(v.title || v.game) + ' — місця' + how + '</h4>'
        + '<table class="mgp-tbl"><tbody>' + order.map((i) => '<tr' + (i === v.sub ? ' class="me"' : '') + '><td>' + res.places[i]
          + '</td><td>' + esc(v.names[i] || '') + '</td><td class="num">' + res.scores[i] + '</td></tr>').join('') + '</tbody></table>'
        + (isHost ? '<div class="mgp-again"><select class="mgp-pick">' + games.map((g) => '<option value="' + esc(g[0]) + '"'
          + (g[0] === v.game ? ' selected' : '') + '>' + esc(g[1]) + '</option>').join('') + '</select>'
          + '<button class="primary" data-again>Ще раз</button></div>' : '<p class="muted small">«Ще раз» тисне господар столу</p>');
      const b = st.res.querySelector('[data-again]');
      if (b) b.onclick = () => { const pick = st.res.querySelector('.mgp-pick'); ctx.act('again', { game: pick ? pick.value : v.game }); };
    }

    st.note.textContent = lobby
      ? '🧪 Стенд: ' + (v.title || v.game || '') + ' · ботів ' + (v.bots == null ? 0 : v.bots) + (isHost ? ' — тисни «Почати», коли всі сіли' : ' — чекаємо на господаря')
      : '';
  }

  HGames.register({
    id: 'mgprobe',
    added: '2026-10-06',
    icon: ICON,
    /// Пад — той, що в міні-гри (handle.pad уже прив'язаний до її ctx); getter, бо гра міняється на «Ще раз».
    get pad() {
      const st = [...live].find((s) => s.emb && s.emb.pad && s.root && s.root.isConnected);
      return st ? st.emb.pad : null;
    },

    mount(root, ctx) {
      render(root, ctx);
    },
    update(root, ctx) { render(root, ctx); },
    frame(root, ctx, f) {
      const st = root._mgp;
      if (st && st.emb && f && f.mg) st.emb.frame(f.mg);
    },
    onKey(e, ctx) {
      const st = stOf(ctx);
      return !!(st && st.emb && st.emb.onKey(e));
    },
    status(ctx) {
      const st = stOf(ctx);
      const v = ctx.view || {};
      if (!ctx.playing) return '';
      if (v.phase === 'howto') return 'Зараз: ' + (v.title || v.game);
      if (v.phase === 'result') return 'Міні-гру зіграно';
      return (st && st.emb && st.emb.status()) || '';
    },
    unmount(root) {
      const st = root._mgp;
      if (!st) return;
      dropEmbed(st);
      if (st.arc) st.arc.stop();
      live.delete(st);
      root._mgp = null;
    },
  });
})();
