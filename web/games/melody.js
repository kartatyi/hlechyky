/*
  «Вгадай мелодію». Треки, уривки, час і очки — на сервері (Impl/Melody.cs); модуль грає уривок, приймає здогадки
  й показує, хто що впізнав.

  Вид (подія 'room', свій для кожного місця — гра Hidden):
    { phase: 'loading'|'play'|'reveal'|'done', round, rounds, clipSec, until, totalMs,
      clip: '/api/games/melody/<токен>.mp3' | null,
      me: null | { artist, title, points },
      found: [{ seat, artist, title, points }],
      answer: null | { artist, title, thumb },     // лише після раунду
      skip: seat[],                               // хто готовий пропустити цей трек
      scores[], left[], error, result,
      // прохід №3 (29.09):
      found[].fast — за скільки секунд узяв бонус швидкості; me.blocked — промахнувся у варіантах; me.who — «хто закинув»
      grow: null | { stage, sec: [3,6,10], bonus: [20,10,0] }   // «з першої ноти»: котрий уривок звучить
      choices: null | string[]  choicesAt: null | час           // варіанти виконавців (лише коли вже відкрились)
      who: null | { by: null | string[] }                        // пісня з «Хто закинув?»; by — після раунду
      teams: null | { of: seat→0/1, scores: [a,b], names, found: [{artist,title}] | null }
      duel: null | { a, b, round, closed, winner, bets: [na, nb], mine, all }
      note: null | string }
  Хід: Act('guess', { text }) — відповідь приходить тостом («🎤 виконавець +70» або «Мимо»);
       Act('pick', { i }) — варіант виконавця; Act('who', { nick }) — хто закинув; Act('bet', { seat }) — ставка в дуелі;
       Act('skip') — «готовий пропустити» (ще раз — передумав). Усі, хто не вгадав усе, готові — трек пропускається.

  Поки звучить уривок, радіо на сторінці глушимо (muted), а потім повертаємо як було.
*/
(() => {
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M6 12.5V3.2l7-1.4v9.2" fill="none" stroke="var(--accent)" stroke-width="1.5" stroke-linejoin="round"/>'
    + '<circle cx="4.3" cy="12.4" r="1.9" fill="var(--clay)"/><circle cx="11.3" cy="11" r="1.9" fill="var(--clay)"/></svg>';

  /// Вписати html, лише коли він справді інший: порівняння з el.innerHTML майже ніколи не збігається (браузер
  /// серіалізує апостроф ніка як ', а esc дає &#39;), і DOM перебудовувався на кожен вид. Свій хелпер, а не
  /// HGames.ui.html: модуль після деплою підміняється на льоту й може застати старий каркас.
  const setHtml = (el, html) => { if (el._h === html) return false; el._h = html; el.innerHTML = html; return true; };
  const seatsOf = (ctx) => (ctx.room && ctx.room.seats ? ctx.room.seats.length : 12);

  /// Фінальна дуель, а це місце лише ставить: вгадувати, пропускати й тицяти варіанти йому нема чого.
  const benchedAt = (v, seat) => !!(v.duel && v.round === v.duel.round && seat !== v.duel.a && seat !== v.duel.b);

  function st(root) {
    if (!root._mg) root._mg = { clip: '', timer: 0, radioMuted: null, played: false, disliked: {}, queued: {} };
    return root._mg;
  }

  // ---------- звук ----------

  function radio() { return document.getElementById('audio'); }

  function duck(s, on) {
    const r = radio();
    if (!r) return;
    if (on && s.radioMuted === null) { s.radioMuted = r.muted; r.muted = true; }
    if (!on && s.radioMuted !== null) { r.muted = s.radioMuted; s.radioMuted = null; }
  }

  function player(root) { return root.querySelector('.mgaudio'); }

  // Гучність уривків — своя, окремо від радіо. Повзунок іде по децибелах, як у плеєрі радіо (app.js):
  // 0..100, крок 0.5 дБ, тож тихі рівні мають десятки кроків. У localStorage — сама гучність 0..1.
  const VOL_DB = 50;
  const VOL_KEY = 'melodyVolume';
  const posToVol = (p) => (p <= 0 ? 0 : Math.pow(10, -VOL_DB * (1 - p / 100) / 20));
  const volToPos = (v) => (v <= 0 ? 0 : Math.min(100, Math.max(1, Math.round(100 * (1 + 20 * Math.log10(v) / VOL_DB)))));

  function savedVolume() {
    const read = (k) => { try { return parseFloat(localStorage.getItem(k) ?? ''); } catch { return NaN; } };
    const own = read(VOL_KEY);
    if (Number.isFinite(own)) return own;
    const radioVol = read('volume');                  // вперше — як у радіо, щоб не оглушити
    return Number.isFinite(radioVol) ? radioVol : 0.5;
  }

  function setVolume(root, p, save) {
    p = Math.min(100, Math.max(0, p));
    const v = posToVol(p);
    const a = player(root);
    if (a) a.volume = v;
    const range = root.querySelector('.mgvol input');
    if (range && +range.value !== p) range.value = p;
    const icon = root.querySelector('.mgvol button');
    if (icon) icon.textContent = p === 0 ? '🔇' : p < 50 ? '🔈' : p < 85 ? '🔉' : '🔊';
    if (save) { try { localStorage.setItem(VOL_KEY, String(v)); } catch { /* приватне вікно */ } }
  }

  function bindVolume(root) {
    const box = root.querySelector('.mgvol');
    const range = box.querySelector('input');
    const icon = box.querySelector('button');
    let before = 60;
    setVolume(root, volToPos(savedVolume()), false);
    range.oninput = () => setVolume(root, parseInt(range.value, 10), true);
    icon.onclick = () => {
      const p = parseInt(range.value, 10);
      if (p > 0) { before = p; setVolume(root, 0, true); } else setVolume(root, before || 60, true);
    };
    range.addEventListener('wheel', (e) => {
      e.preventDefault();
      setVolume(root, parseInt(range.value, 10) + (e.deltaY < 0 ? 2 : -2), true);
    }, { passive: false });
  }

  function play(root) {
    const a = player(root);
    const s = st(root);
    if (!a || !a.src) return;
    a.currentTime = 0;
    duck(s, true);
    a.play().then(() => { s.played = true; paintPlay(root); })
      .catch(() => { duck(s, false); paintPlay(root); });   // браузер не дав грати без кліку — покажемо кнопку
  }

  function paintPlay(root) {
    const a = player(root);
    const btn = root.querySelector('.mgplay');
    if (!a || !btn) return;
    const playing = !a.paused && !a.ended;
    btn.textContent = playing ? '⏸ Пауза' : st(root).played ? '↻ Ще раз' : '▶ Врубити';
    const disc = root.querySelector('.mgdisc');
    // Не голий .spin: у style.css це кружальце завантаження з рамкою, і платівка брала його обідок.
    if (disc) disc.classList.toggle('mgspinning', playing);
  }

  function progress(root) {
    const a = player(root);
    const bar = root.querySelector('.mgwave i');
    if (!a || !bar) return;
    const d = a.duration || 1;
    bar.style.width = Math.min(100, (a.currentTime / d) * 100) + '%';
  }

  // ---------- 👎 ----------
  // Дизлайк живе в базі, а не в партії: тиснеш — трек (і та сама пісня з інших завантажень) більше не трапиться
  // в «Вгадай мелодію» нікому. Стан кнопки браузер тримає сам: у партію й так потрапляють лише треки без дизлайків.

  function dislikeBtn(root, id, small) {
    const on = !!st(root).disliked[id];
    return '<button type="button" class="ghost mgdis' + (on ? ' on' : '') + (small ? ' small' : '') + '" data-track="' + String(id).replace(/"/g, '&quot;') + '"'
      + ' title="' + (on ? 'Зняти дизлайк' : 'Більше не давати цей трек у «Вгадай мелодію»') + '">'
      + (small ? '👎' : on ? '👎 Не трапиться' : '👎 Більше не давати') + '</button>';
  }

  async function dislike(root, id) {
    if (!id || !window.HGames) return;
    const r = await HGames.call('MelodyDislike', id);
    if (!r || !r.ok) return;
    st(root).disliked[id] = !!r.disliked;
    root.querySelectorAll('.mgdis').forEach((b) => {
      if (b.dataset.track !== id) return;
      const small = b.classList.contains('small');
      b.classList.toggle('on', !!r.disliked);
      b.textContent = small ? '👎' : r.disliked ? '👎 Не трапиться' : '👎 Більше не давати';
      b.title = r.disliked ? 'Зняти дизлайк' : 'Більше не давати цей трек у «Вгадай мелодію»';
    });
  }

  // ---------- ➕ На радіо ----------
  // Той самий шлях, що й «закинути з історії» на сторінці радіо (POST /api/queue/track/<id>): ліміти, права й
  // «уже в черзі» — як у звичайного замовлення, відповідь сервера — тостом.

  function radioBtn(root, id, small) {
    const on = !!st(root).queued[id];
    return '<button type="button" class="ghost mgradio' + (small ? ' small' : '') + '"' + (on ? ' disabled' : '')
      + ' data-track="' + String(id).replace(/"/g, '&quot;') + '" title="' + (on ? 'Уже закинуто на радіо' : 'Закинути цей трек у чергу радіо') + '">'
      + (on ? '✓ На радіо' : small ? '➕' : '➕ На радіо') + '</button>';
  }

  async function toRadio(root, id) {
    if (!id) return;
    let nick = '';
    try { nick = localStorage.getItem('nick') || ''; } catch { /* приватне вікно */ }
    const say = (t, bad) => { const c = root._ctx; if (c && c.toast) c.toast(t, bad ? 'err' : ''); };
    if (!nick) { say('Спершу назвись — тоді й закидай', true); return; }
    const btns = () => Array.from(root.querySelectorAll('.mgradio')).filter((b) => b.dataset.track === id);
    btns().forEach((b) => { b.disabled = true; });
    let ok = false, msg = '';
    try {
      const r = await fetch('/api/queue/track/' + encodeURIComponent(id), {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent(nick) },
      });
      let d = null;
      try { d = await r.json(); } catch { /* без тіла */ }
      ok = r.ok;
      msg = (d && d.message) || (ok ? 'Закинуто на радіо' : 'Не вийшло закинути');
    } catch { msg = 'Не вийшло закинути — зв\'язок?'; }
    if (ok) st(root).queued[id] = true;
    say(ok ? '📻 ' + msg : msg, !ok);
    btns().forEach((b) => {
      b.disabled = ok;
      if (ok) { b.textContent = '✓ На радіо'; b.title = 'Уже закинуто на радіо'; }
    });
  }

  // ---------- розмітка ----------

  /// Наступний трек не готовий уже кілька секунд — отже, пісня ще качається з YouTube: кажемо, що чекати недовго.
  const SLOW_LOAD_MS = 4000;

  function slowLoad(root) {
    const el = root.querySelector('.mgload');
    if (!el || el.dataset.slow || Date.now() - (st(root).stageAt || 0) < SLOW_LOAD_MS) return;
    el.dataset.slow = '1';
    el.textContent = 'Глек докачує пісню з полиці — ще кілька секунд…';
  }

  function timer(root, ctx) {
    const v = ctx.view || {};
    slowLoad(root);
    const box = root.querySelector('.mgtime');
    const live = ctx.playing && (v.phase === 'play' || v.phase === 'reveal');
    box.style.visibility = live ? 'visible' : 'hidden';
    if (!live) return;
    const left = Math.max(0, new Date(v.until).getTime() - Date.now());
    box.querySelector('i').style.width = Math.max(0, Math.min(100, left / (v.totalMs || 1) * 100)) + '%';
    box.querySelector('i').classList.toggle('hot', v.phase === 'play' && left < 8000);
    const t = String(Math.ceil(left / 1000));
    const n = box.querySelector('span');
    if (n.textContent !== t) n.textContent = t;
  }

  function stage(root, ctx, v) {
    const el = root.querySelector('.mgstage');
    let key, html;
    if (ctx.room && ctx.room.status === 'lobby') {
      key = 'lobby:' + lobbyHint(ctx);
      html = '<div class="mgdisc"></div><div class="mgwait">Господар тисне «Почати» — і звучить перший уривок</div>' + lobbyHint(ctx);
    } else if (v.phase === 'loading') {
      key = 'loading:' + v.round;
      html = '<div class="mgwait"><span class="spin"></span> <span class="mgload">' + (v.round ? 'Мить — наступний трек…' : 'Дядько Глек порпається на полицях…') + '</span></div>';
    } else if (v.phase === 'play') {
      key = 'play:' + v.round;
      html = '<div class="mgdisc"></div>'
        + '<div class="mgwave"><i></i></div>'
        + '<button type="button" class="primary mgplay">▶ Врубити</button>';
    } else if (v.phase === 'done' && (v.played || []).length) {
      key = 'done:' + v.played.map((t) => t.id).join(',') + ':' + (v.error || '');
      html = (v.error ? '<div class="mgwait mgnote">' + ctx.esc(v.error) + '</div>' : '')
        + '<div class="mgwait">Що звучало. ➕ — закинути на радіо, 👎 — більше не давати в цій грі</div><div class="mgplayed">'
        + v.played.map((t) => '<div class="mgpl">'
          + '<span class="mgmini"' + (t.thumb ? ' style="background-image:url(' + ctx.esc(t.thumb) + ')"' : '') + '></span>'
          + '<span class="mgpt"><b>' + ctx.esc(t.title) + '</b><i>' + ctx.esc(t.artist) + '</i></span>'
          + radioBtn(root, t.id, true) + dislikeBtn(root, t.id, true) + '</div>').join('')
        + '</div>';
    } else if (v.phase === 'reveal' || (v.phase === 'done' && v.answer)) {
      const a = v.answer || {};
      key = 'reveal:' + v.round + ':' + (v.phase);
      html = '<div class="mgcover"' + (a.thumb ? ' style="background-image:url(' + ctx.esc(a.thumb) + ')"' : '') + '></div>'
        + '<div class="mganswer"><div class="mgtitle">' + ctx.esc(a.title || '') + '</div><div class="mgartist">' + ctx.esc(a.artist || '') + '</div></div>'
        + '<div class="mgrow"><button type="button" class="ghost mgplay">↻ Ще раз</button>' + (a.id ? radioBtn(root, a.id, false) + dislikeBtn(root, a.id, false) : '') + '</div>';
    } else {
      key = 'done';
      html = v.error ? '<div class="mgwait">' + ctx.esc(v.error) + '</div>' : '';
    }
    if (el.dataset.key === key) return;
    el.dataset.key = key;
    st(root).stageAt = Date.now();
    el.innerHTML = html;
    el.querySelectorAll('.mgdis').forEach((b) => b.onclick = () => dislike(root, b.dataset.track));
    el.querySelectorAll('.mgradio').forEach((b) => b.onclick = () => toRadio(root, b.dataset.track));
    const btn = el.querySelector('.mgplay');
    if (btn) btn.onclick = () => {
      const a = player(root);
      if (a && !a.paused && !a.ended) a.pause(); else play(root);
    };
  }

  function audio(root, ctx, v) {
    const s = st(root);
    const a = player(root);
    const want = v.clip || '';
    if (want === s.clip) return;
    s.clip = want;
    s.played = false;
    a.pause();
    if (!want) { a.removeAttribute('src'); a.load(); duck(s, false); return; }
    a.src = want;
    // Уривок стартує сам, щойно раунд почався. Після розкриття — ні: там кнопка «Ще раз».
    if (v.phase === 'play') play(root);
  }

  function guessBox(root, ctx, v) {
    const form = root.querySelector('.mgguess');
    const input = form.querySelector('input');
    const me = v.me || {};
    const all = me.artist && me.title;
    const benched = benchedAt(v, ctx.seat);   // фінальна дуель, а ти лише ставиш
    const can = !!ctx.mine && !!ctx.playing && v.phase === 'play' && !all && !me.blocked && !benched;
    const opened = can && input.disabled;
    input.disabled = !can;
    // Новий трек — курсор одразу в полі: вгадують наввипередки, і шукати поле мишкою — програти секунду.
    // На телефоні — ні: клавіатура закрила б пів екрана ще до першої ноти.
    if (opened && !HGames.ui.coarse()) {
      const busy = document.activeElement;
      if (!busy || busy === document.body || busy === input || !/^(INPUT|TEXTAREA|SELECT)$/.test(busy.tagName)) setTimeout(() => input.focus(), 0);
    }
    form.querySelector('button').disabled = !can;
    // Після партії поле «Чекаємо на трек…» лише вводило в оману: треків більше не буде.
    form.hidden = v.phase === 'done';
    input.placeholder = !ctx.mine ? 'Дивишся збоку'
      : v.phase !== 'play' ? 'Чекаємо на трек…'
        : benched ? '⚔️ Дуель — вгадують лише двоє'
          : me.blocked ? '🙈 Цей трек уже без тебе'
            : all ? 'Є! Усе вгадано 🎉' : 'виконавець або назва…';
    const marks = root.querySelector('.mgmarks');
    const html = ctx.mine && ctx.playing && v.phase !== 'done' && !benched
      ? '<span class="chip' + (me.artist ? ' on' : '') + '">🎤 виконавець</span><span class="chip' + (me.title ? ' on' : '') + '">🎵 назва</span>'
      : '';
    setHtml(marks, html);
    form.onsubmit = (e) => {
      e.preventDefault();
      const text = input.value.trim();
      const c = root._ctx;
      if (!text || !c) return;
      c.act('guess', { text }).then((r) => {
        if (r && (r.ok || r.message === 'Мимо')) input.value = '';
        input.focus();
      });
    };
  }

  /// Кнопка «Пропустити» і хто вже готовий. Кнопки нема тому, хто вгадав усе: йому пропускати нема чого.
  function skipBox(root, ctx, v) {
    const el = root.querySelector('.mgskip');
    const me = v.me || {};
    const skip = v.skip || [];
    const mineSkip = skip.indexOf(ctx.seat) >= 0;
    const can = !!ctx.mine && !!ctx.playing && v.phase === 'play' && !(me.artist && me.title) && !me.blocked && !benchedAt(v, ctx.seat);
    const waiting = [];
    if (v.phase === 'play') {
      for (let i = 0; i < seatsOf(ctx); i++) {
        if (!ctx.nickOf(i) || (v.left || []).indexOf(i) >= 0 || benchedAt(v, i)) continue;
        const f = (v.found || []).find((x) => x.seat === i);
        if (f && f.artist && f.title) continue;
        waiting.push(i);
      }
    }
    // Троє й менше — поіменно; більше — числом (хто саме, видно ⏭ у рахунку): на столі з дванадцяти перелік
    // ніків розтягувався на пів екрана телефона.
    const ready = !skip.length ? ''
      : skip.length > 3 ? '⏭ ' + skip.length + ' з ' + waiting.length + ' — за пропуск'
        : '⏭ ' + skip.map((i) => ctx.esc(ctx.nickOf(i) || '')).join(', ') + ' — за пропуск';
    const html = v.phase !== 'play' ? ''
      : (can ? '<button type="button" class="' + (mineSkip ? 'primary' : 'ghost') + ' mgskipbtn">'
        + (mineSkip ? '⏭ Я за пропуск (' + skip.length + '/' + waiting.length + ')' : '⏭ Пропустити') + '</button>' : '')
        + (ready && !(can && mineSkip && skip.length === 1) ? '<span class="muted small">' + ready + '</span>' : '');
    if (setHtml(el, html)) {
      const b = el.querySelector('.mgskipbtn');
      if (b) b.onclick = () => { const c = root._ctx; if (c) c.act('skip'); };
    }
  }

  // ---------- прохід №3: підказки лобі, варіанти, «хто закинув», дуель, команди ----------

  function lobbyHint(ctx) {
    const o = (ctx.room && ctx.room.options) || {};
    const bits = [];
    if (o.teams === '1') bits.push('🟠🔵 Команди: непарні місця — 🟠 Глечики, парні — 🔵 Макітри (від 6 гравців). Кожне вгадане — раз на команду');
    if (o.grow === '1') bits.push('🎹 З першої ноти: уривок росте 3 → 6 → 10 с — впізнав на 3 с: +20, на 6 с: +10');
    if (o.choices === '1') bits.push('🔘 В останні 5 с уривка — 4 виконавці на вибір (за них удвічі менше); не той — трек без тебе');
    if (o.duel === '1') bits.push('⚔️ Останній трек — дуель двох лідерів, решта вгадує, хто візьме (+50)');
    if (String(o.cat || '').split(',').indexOf('who') >= 0) bits.push('📻 «Хто закинув?» — ваші замовлення з радіо: після виконавця чи назви назви ще й хто закинув (+50)');
    return bits.length ? '<div class="mghints">' + bits.map((b) => '<div>' + ctx.esc(b) + '</div>').join('') + '</div>' : '';
  }

  const nickBtn = (ctx, i, cls, data, on) => '<button type="button" class="' + (on ? 'primary' : 'ghost') + ' ' + cls + '" data-v="' + data + '">'
    + ctx.esc(ctx.nickOf(i) || '') + '</button>';

  function extra(root, ctx, v) {
    const el = root.querySelector('.mgextra');
    const me = v.me || {};
    const play = v.phase === 'play' && !!ctx.playing;
    const mine = !!ctx.mine && !!ctx.playing && !benchedAt(v, ctx.seat);
    const bettor = !!ctx.mine && !!ctx.playing;
    const parts = [];
    if (v.note && v.phase !== 'done') parts.push('<div class="mgline muted small">ℹ️ ' + ctx.esc(v.note) + '</div>');

    // «з першої ноти»: котрий уривок звучить і що він дає
    if (v.grow && play) {
      parts.push('<div class="mggrow">' + v.grow.sec.map((sec, k) => '<span class="chip' + (k === v.grow.stage ? ' on' : k < v.grow.stage ? ' mgpast' : '') + '">'
        + sec + ' с' + (v.grow.bonus[k] ? ' · ⚡+' + v.grow.bonus[k] : '') + '</span>').join('') + '</div>');
    }

    // фінальна дуель
    const d = v.duel;
    if (d && v.phase !== 'done' || d && d.winner !== -2) {
      const na = ctx.esc(nickAt(ctx, v, d.a)), nb = ctx.esc(nickAt(ctx, v, d.b));
      const res = d.winner >= 0 ? ' — трек бере ' + ctx.esc(nickAt(ctx, v, d.winner)) + '!' : d.winner === -1 ? ' — нічия, ставки згоріли' : '';
      let body = '';
      const duelist = ctx.seat === d.a || ctx.seat === d.b;
      if (d.winner === -2 && bettor && !duelist && !d.closed) {
        body = '<div class="muted small">Хто візьме фінальний трек? Вгадаєш — +50</div><div class="mgbtns">'
          + nickBtn(ctx, d.a, 'mgbet', d.a, d.mine === d.a) + nickBtn(ctx, d.b, 'mgbet', d.b, d.mine === d.b) + '</div>';
      } else if (d.winner === -2 && duelist) {
        body = '<div class="muted small">' + (v.round === d.round ? 'Вгадують лише двоє — давай!' : 'Наступний трек — лише для вас двох') + '</div>';
      } else if (d.winner === -2 && d.closed) {
        body = '<div class="muted small">Ставки зачинено' + (d.mine >= 0 ? ' — твоя на ' + ctx.esc(nickAt(ctx, v, d.mine)) : '') + '</div>';
      } else if (d.winner !== -2 && d.mine >= 0) {
        body = '<div class="small">' + (d.mine === d.winner ? '🎯 Ставка зайшла: +50' : '🙈 Ставка не зайшла') + '</div>';
      }
      parts.push('<div class="mgduel"><b>⚔️ Фінальна дуель: ' + na + ' проти ' + nb + res + '</b>'
        + (d.bets[0] + d.bets[1] ? '<span class="muted small">ставки: ' + d.bets[0] + ' на ' + na + ' · ' + d.bets[1] + ' на ' + nb + '</span>' : '')
        + body + '</div>');
    }

    // варіанти виконавців
    const teamGot = (k) => (v.teams && v.teams.found && ctx.seat != null && v.teams.of[ctx.seat] >= 0 ? v.teams.found[v.teams.of[ctx.seat]][k] : null);
    if (play && mine) {
      if (me.blocked) parts.push('<div class="mgline small">🙈 Не той варіант — цей трек уже без тебе</div>');
      else if (v.choices && !me.artist && !teamGot('artist')) {
        parts.push('<div class="mgline muted small">Хто співає? За варіант — удвічі менше, не той — трек без тебе</div><div class="mgbtns mgchoices">'
          + v.choices.map((c, i) => '<button type="button" class="ghost mgch" data-v="' + i + '">' + ctx.esc(c) + '</button>').join('') + '</div>');
      } else if (v.choicesAt && !me.artist) parts.push('<div class="mgline muted small">🔘 За 5 с до кінця уривка тут з\'являться 4 виконавці на вибір</div>');
    }

    // «Хто закинув?»
    if (v.who) {
      if (v.who.by && v.phase === 'reveal') {
        parts.push('<div class="mgline">📻 Закинув' + (v.who.by.length > 1 ? 'и' : '') + ' на радіо: <b>' + v.who.by.map(ctx.esc).join(', ') + '</b></div>');
      } else if (play && mine) {
        const tw = teamGot('who');   // у командній грі замовника називають раз на команду
        const said = me.who != null ? me.who : tw != null ? tw : null;
        if (said === true) parts.push('<div class="mgline small">📻 Замовника вгадано: +50</div>');
        else if (said === false) parts.push('<div class="mgline small">📻 Не той замовник — побачимо після треку</div>');
        else if (me.artist || me.title || teamGot('artist') || teamGot('title')) {
          const btns = [];
          for (let i = 0; i < seatsOf(ctx); i++) if (ctx.nickOf(i) && (v.left || []).indexOf(i) < 0) btns.push(nickBtn(ctx, i, 'mgwho', ctx.esc(ctx.nickOf(i)), false));
          parts.push('<div class="mgline">📻 Хто з вас закинув цю пісню на радіо? <b>+50</b></div><div class="mgbtns">' + btns.join('') + '</div>');
        } else parts.push('<div class="mgline muted small">📻 Цю пісню закинув хтось із вас — вгадай виконавця чи назву, тоді назвеш хто</div>');
      }
    }

    if (setHtml(el, parts.join(''))) {
      const act = (a, p) => { const c = root._ctx; if (c) c.act(a, p); };
      el.querySelectorAll('.mgch').forEach((b) => b.onclick = () => act('pick', { i: +b.dataset.v }));
      el.querySelectorAll('.mgwho').forEach((b) => b.onclick = () => act('who', { nick: b.textContent }));
      el.querySelectorAll('.mgbet').forEach((b) => b.onclick = () => act('bet', { seat: +b.dataset.v }));
    }
  }

  /// Дві команди — рахунок великими цифрами над таблицею, під час треку — що команда вже вгадала.
  function teamsBar(ctx, v) {
    const t = v.teams;
    if (!t) return '';
    const res = v.phase === 'done' && v.result;
    return '<div class="mgteams">' + [0, 1].map((k) => {
      const f = t.found && t.found[k];
      const win = res && res.team === k;
      return '<div class="mgteam mgt' + k + (win ? ' win' : '') + (t.of[ctx.seat] === k ? ' me' : '') + '"><span>' + (win ? '🏆 ' : '') + ctx.esc(t.names[k]) + '</span>'
        + '<b>' + t.scores[k] + '</b><i>' + (f ? (f.artist ? '🎤' : '') + (f.title ? '🎵' : '') : '') + '</i></div>';
    }).join('') + '</div>';
  }

  /// Нік місця; після партії — і тих, хто вже встав з-за столу: підсумок не має губити переможця.
  function nickAt(ctx, v, i) {
    const res = v.phase === 'done' && v.result;
    return ctx.nickOf(i) || (res && res.nicks && res.nicks[i]) || '';
  }

  /// Шапка після партії: хто переміг і з чим, а не просто «Партію зіграно».
  function doneHead(ctx, v) {
    const res = v.result || {};
    if (res.teams) {
      const names = (v.teams && v.teams.names) || ['Команда 1', 'Команда 2'];
      return res.team >= 0 ? '🏆 ' + names[res.team] + ' — ' + res.teams[0] + ' : ' + res.teams[1] : 'Нічия — ' + res.teams[0] + ' : ' + res.teams[1];
    }
    const w = (res.winners || []).map((i) => nickAt(ctx, v, i)).filter(Boolean);
    if (!w.length) return 'Партію зіграно' + (res.scores ? ' — жодної пісні не впізнали' : '');
    const pts = (res.scores || [])[res.winners[0]] || 0;
    return '🏆 ' + w.join(' і ') + (w.length > 1 ? ' — по ' : ' — ') + pts;
  }

  function scores(root, ctx, v) {
    const found = {};
    // після партії 🎤🎵 останнього треку лише плутали б: наче хтось «вгадав усе»
    if (v.phase !== 'done') for (const f of v.found || []) found[f.seat] = f;
    const rows = [];
    for (let i = 0; i < seatsOf(ctx); i++) {
      const nick = nickAt(ctx, v, i);
      if (nick) rows.push({ i, nick, score: (v.scores || [])[i] || 0, f: found[i] });
    }
    rows.sort((a, b) => b.score - a.score);
    const left = v.left || [];
    const win = (v.phase === 'done' && v.result && v.result.winners) || [];
    const tm = v.teams && v.teams.of;
    const html = rows.map((r) => '<div class="mgsc' + (r.i === ctx.seat ? ' me' : '') + (tm && tm[r.i] >= 0 ? ' mgt' + tm[r.i] : '') + (left.indexOf(r.i) >= 0 ? ' off' : '')
      + (win.indexOf(r.i) >= 0 ? ' win' : '') + '">'
      + '<span class="mgn">' + (win.indexOf(r.i) >= 0 ? '🏆 ' : '') + ctx.esc(r.nick) + '</span>'
      + '<span class="mgf">' + (r.f && r.f.artist ? '🎤' : '') + (r.f && r.f.title ? '🎵' : '')
      + (r.f && r.f.fast ? '⚡' : '') + (r.f && r.f.who === true ? '📻' : '') + (r.f && r.f.blocked ? '🙈' : '')
      + (v.phase === 'play' && (v.skip || []).indexOf(r.i) >= 0 ? '⏭' : '') + '</span>'
      + (r.f && r.f.points && v.phase !== 'done' ? '<em>+' + r.f.points + '</em>' : '')
      + '<b>' + r.score + '</b></div>').join('');
    const el = root.querySelector('.mgscores');
    // У лобі — без рахунку з нулями: хто сів, і так видно в шапці столу, а дванадцять рядків нулів відсували
    // «Почати» господаря на екран униз.
    const lobby = ctx.room && ctx.room.status === 'lobby';
    const out = lobby ? '' : teamsBar(ctx, v) + html;
    setHtml(el, out);
  }

  function render(root, ctx) {
    root._ctx = ctx;
    const v = ctx.view || {};
    const head = root.querySelector('.mghead');
    const text = v.phase === 'done' ? doneHead(ctx, v) : v.round ? 'Трек ' + v.round + ' з ' + v.rounds : ctx.playing ? 'Готуємось' : '';
    if (head.textContent !== text) head.textContent = text;
    head.classList.toggle('mgwon', v.phase === 'done' && !!(v.result && (v.result.winners || []).length));
    // після партії повзунок гучності ні до чого: слухати вже нема чого
    root.querySelector('.mgvol').hidden = v.phase === 'done';
    stage(root, ctx, v);
    extra(root, ctx, v);
    audio(root, ctx, v);
    guessBox(root, ctx, v);
    skipBox(root, ctx, v);
    scores(root, ctx, v);
    timer(root, ctx);
    paintPlay(root);
  }

  HGames.register({
    id: 'melody',
    added: '2026-09-17',          // нова гра: «🆕» у лобі два тижні тим, хто ще не грав (core.js, isNewGame)
    news: {
      v: '2026-09-29',
      title: 'Вгадай мелодію: дуель, команди і «Хто закинув?»',
      items: [
        '⚡ Впізнав за перші 5 секунд — +20 очок; «з першої ноти»: уривок росте 3 → 6 → 10 с, хто раніше — той з бонусом',
        '📻 «Хто закинув?» — пісні, які ви самі ставили на радіо: після назви вгадай ще й хто закинув (+50)',
        '🔘 Варіанти: в останні 5 секунд уривка — чотири виконавці на вибір (для телефона й Деки), не той — трек без тебе',
        '⚔️ Фінальна дуель двох лідерів зі ставками решти і 🟠🔵 команди для великої компанії — опціями столу',
        '➕ Після партії будь-який трек з «Що звучало» — одним тиском на радіо',
      ],
    },
    icon: ICON,
    seatClass: ['x', 'o', 'c', 'd', 'x', 'o', 'c', 'd', 'x', 'o', 'c', 'd'],

    mount(root, ctx) {
      root.innerHTML = '<div class="mgwrap">'
        + '<div class="mgtop"><div class="mghead muted small"></div><div class="mgtime"><i></i><span></span></div></div>'
        + '<div class="mgstage"></div>'
        + '<audio class="mgaudio" preload="auto"></audio>'
        + '<div class="mgvol"><button type="button" class="ghost" title="Вирубити / врубити звук">🔉</button>'
        + '<input type="range" min="0" max="100" step="1" aria-label="Гучність уривка" title="Гучність уривка · колесо миші — по кроку"></div>'
        + '<div class="mgextra"></div>'
        + '<div class="mgmarks"></div>'
        + '<div class="mgskip"></div>'
        + '<form class="mgguess"><input type="text" maxlength="80" autocomplete="off" spellcheck="false" enterkeyhint="send">'
        + '<button class="primary" type="submit">➤</button></form>'
        + '<div class="mgscores"></div>'
        + '</div>';
      const s = st(root);
      const a = player(root);
      bindVolume(root);
      const settle = () => { paintPlay(root); if (a.paused || a.ended) duck(s, false); };
      a.addEventListener('play', () => paintPlay(root));
      a.addEventListener('pause', settle);
      a.addEventListener('ended', settle);
      a.addEventListener('timeupdate', () => progress(root));
      s.timer = setInterval(() => { if (root._ctx) timer(root, root._ctx); }, 250);
      render(root, ctx);
    },

    update(root, ctx) { render(root, ctx); },

    unmount(root) {
      const s = root._mg;
      if (!s) return;
      clearInterval(s.timer);
      const a = player(root);
      if (a) a.pause();
      duck(s, false);
    },

    status(ctx) {
      const v = ctx.view || {};
      if (v.phase === 'done' || !ctx.playing) return v.phase === 'done' ? (v.error || 'Партію зіграно') : '';
      if (v.phase === 'loading') return 'Глек порпається на полицях…';
      if (v.phase === 'reveal') return v.round >= v.rounds ? 'Глек рахує очки…' : 'Наступний трек за мить…';
      if (!ctx.mine) return 'Дивишся збоку';
      const d = v.duel;
      if (d && v.round === d.round) return ctx.seat === d.a || ctx.seat === d.b ? '⚔️ Фінальна дуель — вгадуй!' : '⚔️ Фінальна дуель — став, хто візьме';
      if (v.me && v.me.blocked) return 'Не той варіант — чекай наступного треку';
      return 'Хто співає і як зветься пісня?';
    },
  });
})();
