/*
  Кахлі — три в ряд (docs/games/specs/kakhli.md): «Шлях кахляра» ('kakhli'), «Кахлі на час» ('kakhli-blitz') і
  «Кахлі дня» ('kakhli-daily') — три модулі в одному файлі (у паспортах Client: "kakhli").

  Правил тут нема. Сервер (Impl/KakhliCore.cs) вирішує, чи можна поміняти, що зійшлось, що спалахнуло й що
  насипалось, і шле кроки розв'язку — ми їх лише програємо. Наперед ми перевіряємо обмін тими самими правилами, щоб
  кахлі, з яких нічого не складеться, відскочили одразу, без походу на сервер (сервер однаково перевіряє все сам).

  Вид: { mode: 'path'|'blitz'|'daily', phase, turn, game: { board, steps, mv, swap, score, made } | null, … }
    board — { w, h, ids[], col[], kind[], holes: '#'/'.', clay: '0'..'2', rope: '0'/'1' } по клітинках;
    steps — [{ g: [id], m: [[id, клітинка, барва, вид]], fx: [[тип, клітинка, арг]], p: [[клітинка, очки]],
              cl: [[клітинка, шарів]], r: [клітинка], a: [[id, барва, вид]], b: [id по клітинках], ch, sh, fin, pts }];
    mv — номер ходу: новий на одиницю — програємо steps; інакше просто малюємо поле.
  Шлях: + { total, max, kilns, levels: [{ n, name, stars, open }], level: { n, name, moves, left, tip, two, three,
          scoreOnly, goals: [{ k: 's'|'c'|'l'|'r', color, need, have }], best, got, fresh, next } }.
  На час: + { inMs, leftMs, seconds, hot }. День: + { left, moves, daily: { day, no, board, place, players, share } }.
  Дії: act('swap', { c, d }) (d: 0 → · 1 ↓ · 2 ← · 3 ↑), 'level' { n }, 'retry', 'next', 'map', 'ready', 'end'.
*/
(() => {
  'use strict';

  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<rect x="1.5" y="1.5" width="6" height="6" rx="1.6" fill="var(--danger)"/>'
    + '<rect x="8.5" y="1.5" width="6" height="6" rx="1.6" fill="var(--accent)"/>'
    + '<rect x="1.5" y="8.5" width="6" height="6" rx="1.6" fill="var(--accent)"/>'
    + '<rect x="8.5" y="8.5" width="6" height="6" rx="1.6" fill="var(--ok)"/></svg>';

  const DIRS = [[1, 0], [0, 1], [-1, 0], [0, -1]];
  const NAMES = ['червоних', 'синіх', 'жовтих', 'зелених', 'білих', 'фіалкових'];
  /// Знак на кожній барві — щоб розрізняли й ті, кому кольори зливаються: мак, хвиля, сонце, листок, зірка, виноград.
  const SYMS = [
    '<g fill="currentColor"><circle cx="12" cy="6.6" r="3.9"/><circle cx="17.1" cy="10.3" r="3.9"/><circle cx="15.2" cy="16.3" r="3.9"/>'
      + '<circle cx="8.8" cy="16.3" r="3.9"/><circle cx="6.9" cy="10.3" r="3.9"/></g><circle cx="12" cy="12" r="2.7" fill="var(--kh-ink)"/>',
    '<path d="M2.5 8.5c3.2-3.2 6.3 3.2 9.5 0s6.3-3.2 9.5 0M2.5 15.5c3.2-3.2 6.3 3.2 9.5 0s6.3-3.2 9.5 0" fill="none" stroke="currentColor"'
      + ' stroke-width="2.6" stroke-linecap="round"/>',
    '<circle cx="12" cy="12" r="4.6" fill="currentColor"/><path d="M12 2.4v3.2M12 18.4v3.2M2.4 12h3.2M18.4 12h3.2M5.2 5.2l2.3 2.3M16.5 16.5'
      + 'l2.3 2.3M5.2 18.8l2.3-2.3M16.5 7.5l2.3-2.3" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"/>',
    '<path d="M12 2.6c5.2 3.1 7.4 8.4 5.1 13.3-1.4 3-3.9 4.6-5.1 5.3-1.2-.7-3.7-2.3-5.1-5.3C4.6 11 6.8 5.7 12 2.6z" fill="currentColor"/>'
      + '<path d="M12 6.2v15" stroke="var(--kh-ink)" stroke-width="1.4" opacity=".55"/>',
    '<rect x="6.2" y="6.2" width="11.6" height="11.6" rx="1.2" fill="currentColor"/>'
      + '<rect x="6.2" y="6.2" width="11.6" height="11.6" rx="1.2" fill="currentColor" transform="rotate(45 12 12)"/>'
      + '<circle cx="12" cy="12" r="2.6" fill="var(--kh-ink)" opacity=".7"/>',
    '<g fill="currentColor"><circle cx="7.4" cy="8.6" r="2.7"/><circle cx="12" cy="8.6" r="2.7"/><circle cx="16.6" cy="8.6" r="2.7"/>'
      + '<circle cx="9.7" cy="13.1" r="2.7"/><circle cx="14.3" cy="13.1" r="2.7"/><circle cx="12" cy="17.6" r="2.7"/></g>'
      + '<path d="M12 6V2.8" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>',
  ];
  const GLEK = '<path d="M9 3h6v2c0 .6.4 1 .9 1.3 3 1.5 4.1 4.1 4.1 7 0 4.5-3.6 7.7-8 7.7s-8-3.2-8-7.7c0-2.9 1.1-5.5 4.1-7C8.6 6 9 5.6 9 5z"'
    + ' fill="currentColor"/><path d="M5.6 12.6h12.8" stroke="#fff4e0" stroke-width="1.6" opacity=".85"/>';
  const svg = (inner) => '<svg viewBox="0 0 24 24" aria-hidden="true">' + inner + '</svg>';
  const face = (color, kind) => svg(kind === 4 ? GLEK : SYMS[color] || '');

  const PRAISE = ['', '', '', 'Гарно!', 'Оце розпис!', 'Аж піч загула!', 'Глек у захваті!', 'Ото майстер!'];
  const HINT_MS = 6000;

  const setHtml = (el, html) => { if (el._h !== html) { el._h = html; el.innerHTML = html; } };
  const wait = (ms) => new Promise((r) => setTimeout(r, Math.max(0, ms)));
  const num = (n) => String(Math.round(n || 0)).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
  const reduced = () => !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);

  // ---------- звук (WebAudio, без файлів; типово вимкнено) ----------

  const Snd = (() => {
    let ac = null;
    const KEY = 'kh-sound';
    const on = () => { try { return localStorage.getItem(KEY) === '1'; } catch { return false; } };
    function audio() {
      if (!ac) {
        const A = window.AudioContext || window.webkitAudioContext;
        if (!A) return null;
        ac = new A();
      }
      if (ac.state === 'suspended') ac.resume().catch(() => {});
      return ac;
    }
    function tone(a, freq, dur, type, vol, delay) {
      const t = a.currentTime + (delay || 0);
      const o = a.createOscillator();
      const g = a.createGain();
      o.type = type;
      o.frequency.setValueAtTime(freq, t);
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(vol, t + 0.008);
      g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
      o.connect(g).connect(a.destination);
      o.start(t);
      o.stop(t + dur + 0.02);
    }
    function thump(a) {
      const len = Math.floor(a.sampleRate * 0.22);
      const buf = a.createBuffer(1, len, a.sampleRate);
      const d = buf.getChannelData(0);
      for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, 3);
      const src = a.createBufferSource();
      const f = a.createBiquadFilter();
      const g = a.createGain();
      f.type = 'lowpass';
      f.frequency.value = 900;
      g.gain.value = 0.35;
      src.buffer = buf;
      src.connect(f).connect(g).connect(a.destination);
      src.start();
    }
    return {
      on,
      set(v) { try { localStorage.setItem(KEY, v ? '1' : '0'); } catch { /* приватне вікно — звук лише на цей раз */ } if (v) audio(); },
      play(kind, n) {
        if (!on()) return;
        const a = audio();
        if (!a) return;
        try {
          switch (kind) {
            case 'tuk': tone(a, 330 * Math.pow(2, Math.min((n || 1) - 1, 9) / 6), 0.11, 'triangle', 0.2); break;
            case 'swap': tone(a, 560, 0.05, 'sine', 0.07); break;
            case 'nope': tone(a, 170, 0.13, 'square', 0.05); break;
            case 'boom': thump(a); break;
            case 'glek': [523, 659, 784, 1047].forEach((f, i) => tone(a, f, 0.16, 'triangle', 0.13, i * 0.06)); break;
            case 'win': [392, 523, 659, 784].forEach((f, i) => tone(a, f, 0.3, 'triangle', 0.12, i * 0.09)); break;
          }
        } catch { /* звук — прикраса, не більше */ }
      },
    };
  })();

  // ---------- правила обміну (ті самі, що в KakhliBoard.CanSwap) ----------

  const at = (m, c) => { const id = m.ids[c]; return id > 0 ? m.tiles.get(id) || null : null; };
  const movable = (m, c) => c >= 0 && c < m.n && !m.holes[c] && !!at(m, c) && !at(m, c).rope;

  function neighbor(m, c, d) {
    const x = (c % m.w) + DIRS[d][0];
    const y = Math.floor(c / m.w) + DIRS[d][1];
    return x >= 0 && x < m.w && y >= 0 && y < m.h ? y * m.w + x : -1;
  }

  function line(m, c, colorAt) {
    const col = colorAt(c);
    if (col < 0) return false;
    const x = c % m.w;
    const y = Math.floor(c / m.w);
    let n = 1;
    for (let i = x - 1; i >= 0 && colorAt(y * m.w + i) === col; i--) n++;
    for (let i = x + 1; i < m.w && colorAt(y * m.w + i) === col; i++) n++;
    if (n >= 3) return true;
    n = 1;
    for (let j = y - 1; j >= 0 && colorAt(j * m.w + x) === col; j--) n++;
    for (let j = y + 1; j < m.h && colorAt(j * m.w + x) === col; j++) n++;
    return n >= 3;
  }

  function canSwap(m, a, b) {
    if (!movable(m, a) || !movable(m, b)) return false;
    if (Math.abs((a % m.w) - (b % m.w)) + Math.abs(Math.floor(a / m.w) - Math.floor(b / m.w)) !== 1) return false;
    const ta = at(m, a);
    const tb = at(m, b);
    if (ta.kind === 4 || tb.kind === 4) return true;
    if (ta.kind && tb.kind) return true;
    const colorAt = (c) => {
      const t = c === a ? tb : c === b ? ta : at(m, c);
      return t && t.kind !== 4 ? t.color : -1;
    };
    return line(m, a, colorAt) || line(m, b, colorAt);
  }

  function allMoves(m) {
    const out = [];
    for (let c = 0; c < m.n; c++) {
      if (c % m.w < m.w - 1 && canSwap(m, c, c + 1)) out.push([c, c + 1]);
      if (Math.floor(c / m.w) < m.h - 1 && canSwap(m, c, c + m.w)) out.push([c, c + m.w]);
    }
    return out;
  }

  // ---------- стан картки ----------

  function state(root) {
    if (!root._kh) {
      root._kh = {
        root, ctx: null, el: null, m: null, mv: -1, queue: [], running: false, anim: false, busy: false, dead: false,
        sel: -1, cur: -1, kbd: false, drag: null, hint: null, idleAt: Date.now(), optimistic: null, swapP: null,
        shown: 0, timer: 0, deadline: 0, goAt: 0, endSent: 0, lastNo: -1,
      };
    }
    return root._kh;
  }

  const view = (S) => (S.ctx && S.ctx.view) || {};
  const game = (S) => view(S).game || null;
  const speed = (S) => (reduced() ? 0.35 : view(S).mode === 'blitz' ? 0.7 : 1);

  /// Чи можна зараз ходити: моє соло, партія йде, поле на місці й гра чекає ходу.
  function live(S) {
    const ctx = S.ctx;
    const v = view(S);
    return !!(ctx && ctx.mine && ctx.playing && v.turn === 0 && v.game && S.m)
      && (v.mode !== 'blitz' || (S.goAt <= performance.now() && S.deadline > performance.now()));
  }

  function build(root, S) {
    if (S.el) return;
    root.classList.add('kh');
    root.innerHTML = '<div class="kh-bar"></div><div class="kh-goals"></div><div class="kh-tip muted small"></div>'
      + '<div class="kh-stage"><div class="kh-board"><div class="kh-cells"></div><div class="kh-tiles"></div><div class="kh-fx"></div>'
      + '<div class="kh-cur" hidden></div></div><div class="kh-over" hidden></div></div><div class="kh-side"></div>';
    S.el = {
      bar: root.querySelector('.kh-bar'),
      goals: root.querySelector('.kh-goals'),
      tip: root.querySelector('.kh-tip'),
      stage: root.querySelector('.kh-stage'),
      board: root.querySelector('.kh-board'),
      cells: root.querySelector('.kh-cells'),
      tiles: root.querySelector('.kh-tiles'),
      fx: root.querySelector('.kh-fx'),
      cur: root.querySelector('.kh-cur'),
      over: root.querySelector('.kh-over'),
      side: root.querySelector('.kh-side'),
    };
    wireBoard(S);
    root.addEventListener('click', (e) => onClick(S, e));
    S.timer = setInterval(() => tick(S), 200);
  }

  // ---------- поле: клітинки й кахлі ----------

  const keyOf = (b) => b.w + 'x' + b.h + ':' + b.holes;

  function cellsLayer(S, b) {
    const m = { w: b.w, h: b.h, n: b.w * b.h, key: keyOf(b), ids: b.ids.slice(), tiles: new Map(), holes: [], cell: [] };
    for (let c = 0; c < m.n; c++) m.holes[c] = b.holes[c] === '#';
    const el = S.el;
    el.board.style.setProperty('--w', b.w);
    el.board.style.setProperty('--h', b.h);
    let html = '';
    for (let c = 0; c < m.n; c++) html += '<div class="kh-c' + (m.holes[c] ? ' hole' : '') + '"></div>';
    el.cells.innerHTML = html;
    el.cells._h = null;
    m.cell = [...el.cells.children];
    el.tiles.innerHTML = '';
    el.fx.innerHTML = '';
    S.m = m;
    S.sel = -1;
    S.hint = null;
    if (S.cur < 0 || S.cur >= m.n) S.cur = Math.floor(m.h / 2) * m.w + Math.floor(m.w / 2);
  }

  function clayAt(S, c, left, crack) {
    const el = S.m.cell[c];
    if (!el) return;
    el.classList.toggle('l1', left === 1);
    el.classList.toggle('l2', left >= 2);
    if (crack) {
      el.classList.remove('crack');
      void el.offsetWidth;
      el.classList.add('crack');
    }
  }

  function place(t, x, y) {
    t.x = x;
    t.y = y;
    t.el.style.setProperty('--x', x);
    t.el.style.setProperty('--y', y);
  }

  function look(t) {
    t.el.className = 'kh-t c' + (t.kind === 4 ? 'g' : t.color) + ' k' + t.kind + (t.rope ? ' rp' : '');
    setHtml(t.inner, face(t.color, t.kind));
  }

  function tile(S, id, color, kind, cell, x, y) {
    const el = document.createElement('div');
    const inner = document.createElement('div');
    inner.className = 'kh-i';
    el.appendChild(inner);
    const t = { id, color, kind, rope: false, cell, el, inner, x: 0, y: 0 };
    look(t);
    place(t, x == null ? cell % S.m.w : x, y == null ? Math.floor(cell / S.m.w) : y);
    S.el.tiles.appendChild(el);
    S.m.tiles.set(id, t);
    return t;
  }

  /// Поле точно як у виді, без анімації: перший показ, F5, пропущений хід, кінець програвання.
  function reconcile(S, g) {
    const b = g.board;
    if (!S.m || S.m.key !== keyOf(b)) cellsLayer(S, b);
    const m = S.m;
    const seen = new Set();
    for (let c = 0; c < m.n; c++) {
      const id = b.ids[c];
      clayAt(S, c, +b.clay[c] || 0, false);
      if (!(id > 0)) continue;
      seen.add(id);
      let t = m.tiles.get(id);
      if (!t) t = tile(S, id, b.col[c], b.kind[c], c);
      t.color = b.col[c];
      t.kind = b.kind[c];
      t.rope = b.rope[c] === '1';
      t.cell = c;
      t.el.style.transitionDuration = '';
      look(t);
      place(t, c % m.w, Math.floor(c / m.w));
    }
    for (const [id, t] of m.tiles) if (!seen.has(id)) { t.el.remove(); m.tiles.delete(id); }
    m.ids = b.ids.slice();
    S.mv = g.mv;
    S.shown = g.score;
    S.hint = null;   // look() уже зняв з кахлів клас підказки
    if (S.sel >= 0 && !at(m, S.sel)) S.sel = -1;
    selDraw(S);
    curDraw(S);
    bar(S);
  }

  function clearBoard(S) {
    if (!S.m) return;
    S.el.tiles.innerHTML = '';
    S.el.cells.innerHTML = '';
    S.el.fx.innerHTML = '';
    S.m = null;
    S.mv = -1;
    S.queue = [];
  }

  // ---------- програвання кроків ----------

  function enqueue(S, g) {
    S.queue.push(g);
    if (!S.running) run(S);
  }

  async function run(S) {
    S.running = true;
    try {
      while (S.queue.length && !S.dead) {
        const g = S.queue.shift();
        const same = S.m && S.m.key === keyOf(g.board);
        if (same && g.mv === S.mv + 1 && g.steps && g.steps.length && !document.hidden && S.queue.length < 3) {
          S.anim = true;
          try { await animate(S, g); } catch (e) { console.warn('[kakhli] анімація', e); }
          S.anim = false;
        } else if (same && g.mv === S.mv && S.optimistic) {
          continue;   // свій обмін ще в дорозі — не відкочуємо кахлі назад до його кроків
        }
        if (!S.dead) reconcile(S, g);
      }
    } finally {
      S.anim = false;
      S.running = false;
      S.idleAt = Date.now();
      if (!S.dead && S.el) over(S);   // підсумок — лише коли останній каскад програно
    }
  }

  async function animate(S, g) {
    const sp = speed(S);
    if (S.swapP) await S.swapP;
    if (g.swap && S.optimistic !== g.swap.join(',')) await swapAnim(S, g.swap[0], g.swap[1]);
    S.optimistic = null;
    let finSaid = false;
    for (const st of g.steps) {
      if (S.dead) return;
      if (st.fin && !finSaid) { finSaid = true; caption(S, 'Глек доливає!'); await wait(350 * sp); }
      if (st.sh) caption(S, 'Глек перемішав кахлі');
      for (const f of st.fx) flash(S, f);
      if (st.fx.length) Snd.play(st.fx.some((f) => f[0] === 4) ? 'glek' : 'boom');
      // наявні кахлі, що змінили вигляд (глек перетворив барву на рушники, перемішування перефарбувало)
      for (const mk of st.m) {
        const t = S.m.tiles.get(mk[0]);
        if (!t) continue;
        t.color = mk[2];
        t.kind = mk[3];
        look(t);
        t.inner.classList.add('turn');
      }
      if (st.fx.length || st.m.length) await wait((st.fx.length ? 210 : 110) * sp);
      for (const id of st.g) {
        const t = S.m.tiles.get(id);
        if (!t) continue;
        S.m.tiles.delete(id);
        t.el.classList.add('gone');
        setTimeout(() => t.el.remove(), 260);
      }
      // нові спецкахлі на місці збігу
      let glek = false;
      for (const mk of st.m) {
        if (S.m.tiles.has(mk[0]) || st.g.includes(mk[0])) continue;   // перетворена чи вже знята — не нова
        const t = tile(S, mk[0], mk[2], mk[3], mk[1]);
        t.inner.classList.add('born');
        if (mk[3] === 4) glek = true;
      }
      for (const [c, left] of st.cl) clayAt(S, c, left, true);
      for (const c of st.r) {
        const t = at(S.m, c);
        if (t) { t.rope = false; t.el.classList.remove('rp'); t.inner.classList.add('turn'); }
      }
      for (const [c, p] of st.p) pop(S, c, p);
      if (st.g.length) Snd.play('tuk', st.ch);
      if (glek && !st.sh) caption(S, 'Чарівний глек!');
      else if (st.ch >= 3 && !st.fin) caption(S, PRAISE[Math.min(st.ch, PRAISE.length - 1)]);
      S.shown += st.pts || 0;
      bar(S);
      if (st.g.length) await wait(170 * sp);
      await fall(S, st, sp);
    }
  }

  /// Кахлі падають на свої нові місця (і пересуваються, коли Глек перемішує); нові — з-над поля.
  async function fall(S, st, sp) {
    const m = S.m;
    const add = new Map(st.a.map((a) => [a[0], a]));
    const fresh = {};
    st.b.forEach((id, c) => { if (add.has(id) && !m.tiles.has(id)) (fresh[c % m.w] = fresh[c % m.w] || []).push(c); });
    const moves = [];
    st.b.forEach((id, c) => {
      if (!(id > 0)) return;
      const x = c % m.w;
      const y = Math.floor(c / m.w);
      let t = m.tiles.get(id);
      if (!t) {
        const a = add.get(id);
        if (!a) return;
        const list = fresh[x] || [c];
        t = tile(S, id, a[1], a[2], c, x, list.indexOf(c) - list.length);
      }
      t.cell = c;
      if (t.x !== x || t.y !== y) moves.push([t, x, y, Math.abs(y - t.y) + Math.abs(x - t.x)]);
    });
    m.ids = st.b.slice();
    if (!moves.length) return;
    void S.el.tiles.offsetWidth;   // нові кахлі мають стати над полем до того, як поїдуть
    let longest = 0;
    for (const [t, x, y, d] of moves) {
      const ms = (st.sh ? 260 : 80 + 55 * Math.min(d, 9)) * sp;
      t.el.style.transitionTimingFunction = st.sh ? 'ease-in-out' : '';
      t.el.style.transitionDuration = ms + 'ms';
      place(t, x, y);
      longest = Math.max(longest, ms);
    }
    await wait(longest + 15);
    for (const [t] of moves) {
      t.el.style.transitionDuration = '';
      t.el.style.transitionTimingFunction = '';
      if (!st.sh) { t.inner.classList.remove('land'); void t.inner.offsetWidth; t.inner.classList.add('land'); }
    }
  }

  /// Дві кахлі міняються місцями (і в моделі теж). Удруге з тими самими клітинками — назад.
  function swapAnim(S, a, b) {
    const m = S.m;
    const ta = at(m, a);
    const tb = at(m, b);
    if (!ta || !tb) return Promise.resolve();
    const ms = 140 * speed(S);
    for (const t of [ta, tb]) {
      t.el.style.transitionTimingFunction = 'ease-in-out';
      t.el.style.transitionDuration = ms + 'ms';
    }
    const [ax, ay, bx, by] = [ta.x, ta.y, tb.x, tb.y];
    place(ta, bx, by);
    place(tb, ax, ay);
    [ta.cell, tb.cell] = [b, a];
    [m.ids[a], m.ids[b]] = [m.ids[b], m.ids[a]];
    Snd.play('swap');
    return wait(ms + 10).then(() => {
      for (const t of [ta, tb]) { t.el.style.transitionDuration = ''; t.el.style.transitionTimingFunction = ''; }
    });
  }

  // ---------- спалахи, очки, підписи ----------

  function fxEl(S, cls, x, y, extra) {
    const el = document.createElement('div');
    el.className = 'kh-fx-' + cls;
    el.style.setProperty('--x', x);
    el.style.setProperty('--y', y);
    if (extra) for (const k in extra) el.style.setProperty(k, extra[k]);
    S.el.fx.appendChild(el);
    setTimeout(() => el.remove(), 700);
    return el;
  }

  function flash(S, f) {
    const m = S.m;
    const [type, c, arg] = f;
    const x = c % m.w;
    const y = Math.floor(c / m.w);
    const row = (yy) => { if (yy >= 0 && yy < m.h) fxEl(S, 'row', 0, yy); };
    const col = (xx) => { if (xx >= 0 && xx < m.w) fxEl(S, 'col', xx, 0); };
    switch (type) {
      case 1: row(y); break;
      case 2: col(x); break;
      case 3: fxEl(S, 'blast', x, y, { '--r': arg || 1 }); break;
      case 4: fxEl(S, 'glek', x, y); break;
      case 5: row(y); col(x); break;
      case 6: for (let d = -1; d <= 1; d++) { row(y + d); col(x + d); } break;
    }
  }

  function pop(S, c, p) {
    if (!(p > 0)) return;
    const el = fxEl(S, 'pop', c % S.m.w, Math.floor(c / S.m.w));
    el.textContent = '+' + num(p);
    if (p >= 300) el.classList.add('big');
  }

  function caption(S, text) {
    if (!text) return;
    const old = S.el.stage.querySelector('.kh-cap');
    if (old) old.remove();
    const el = document.createElement('div');
    el.className = 'kh-cap';
    el.textContent = text;
    S.el.stage.appendChild(el);
    setTimeout(() => el.remove(), 1100);
  }

  // ---------- ввід ----------

  function cellAt(S, e) {
    const r = S.el.board.getBoundingClientRect();
    if (!S.m || !r.width) return -1;
    const x = Math.floor((e.clientX - r.left) / (r.width / S.m.w));
    const y = Math.floor((e.clientY - r.top) / (r.height / S.m.h));
    return x >= 0 && x < S.m.w && y >= 0 && y < S.m.h ? y * S.m.w + x : -1;
  }

  function wireBoard(S) {
    const b = S.el.board;
    b.addEventListener('pointerdown', (e) => {
      if (!live(S) || (e.pointerType === 'mouse' && e.button !== 0)) return;
      const c = cellAt(S, e);
      if (c < 0) return;
      S.kbd = false;
      S.drag = { c, x: e.clientX, y: e.clientY, done: false };
      try { b.setPointerCapture(e.pointerId); } catch { /* синтетичний указівник пада — без захоплення */ }
      e.preventDefault();
    });
    b.addEventListener('pointermove', (e) => {
      const d = S.drag;
      if (!d || d.done || !S.m) return;
      const size = b.getBoundingClientRect().width / S.m.w;
      const dx = e.clientX - d.x;
      const dy = e.clientY - d.y;
      if (Math.max(Math.abs(dx), Math.abs(dy)) < size * 0.38) return;
      d.done = true;
      trySwap(S, d.c, Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3));
    });
    const up = () => {
      const d = S.drag;
      S.drag = null;
      if (d && !d.done) tap(S, d.c);
    };
    b.addEventListener('pointerup', up);
    b.addEventListener('pointercancel', () => { S.drag = null; });
    b.addEventListener('contextmenu', (e) => e.preventDefault());
  }

  function tap(S, c) {
    if (!S.m) return;
    if (S.sel < 0 || !at(S.m, S.sel)) { select(S, c); return; }
    if (c === S.sel) { select(S, -1); return; }
    for (let d = 0; d < 4; d++) if (neighbor(S.m, S.sel, d) === c) { trySwap(S, S.sel, d); return; }
    select(S, c);
  }

  function select(S, c) {
    S.sel = c >= 0 && S.m && at(S.m, c) && !S.m.holes[c] ? c : -1;
    if (S.sel >= 0) S.cur = S.sel;
    selDraw(S);
    curDraw(S);
  }

  function selDraw(S) {
    if (!S.m) return;
    for (const t of S.m.tiles.values()) t.el.classList.toggle('sel', t.cell === S.sel);
  }

  function curDraw(S) {
    const el = S.el && S.el.cur;
    if (!el) return;
    el.hidden = !(S.kbd && S.m && live(S));
    if (!el.hidden) { el.style.setProperty('--x', S.cur % S.m.w); el.style.setProperty('--y', Math.floor(S.cur / S.m.w)); }
  }

  function unhint(S) {
    if (!S.hint || !S.m) { S.hint = null; return; }
    for (const c of S.hint) { const t = at(S.m, c); if (t) t.el.classList.remove('hint'); }
    S.hint = null;
  }

  async function trySwap(S, a, d) {
    if (!live(S)) return;
    S.idleAt = Date.now();
    unhint(S);
    const m = S.m;
    const b = neighbor(m, a, d);
    if (b < 0 || S.anim || S.busy) { if (b >= 0) select(S, a); return; }
    select(S, -1);
    if (!canSwap(m, a, b)) {
      if (!movable(m, a) || !movable(m, b)) {
        const t = at(m, a);
        if (t) { t.inner.classList.remove('nope'); void t.inner.offsetWidth; t.inner.classList.add('nope'); }
        if (t && t.rope) caption(S, 'Зв’язану не посунеш');
        Snd.play('nope');
        return;
      }
      S.busy = true;
      await swapAnim(S, a, b);
      Snd.play('nope');
      await swapAnim(S, a, b);
      S.busy = false;
      return;
    }
    S.busy = true;
    S.optimistic = a + ',' + b;
    S.swapP = swapAnim(S, a, b);
    const r = await S.ctx.tell('swap', { c: a, d });
    await S.swapP;
    S.swapP = null;
    if (!r || !r.ok) {
      if (S.optimistic === a + ',' + b) {
        S.optimistic = null;
        if (S.m === m) await swapAnim(S, a, b);
        S.ctx.toast(r ? r.message || 'Не вийшло' : 'Зв’язку нема — спробуй ще', 'err');
      }
    }
    S.busy = false;
  }

  function onKey(e, ctx) {
    const S = ctx._root && ctx._root._kh;
    if (!S || !live(S)) return false;
    const map = { ArrowRight: 0, ArrowDown: 1, ArrowLeft: 2, ArrowUp: 3 };
    const codes = { KeyD: 0, KeyS: 1, KeyA: 2, KeyW: 3 };
    const d = map[e.key] != null ? map[e.key] : codes[e.code];
    if (d != null) {
      S.kbd = true;
      S.idleAt = Date.now();
      if (S.sel >= 0) trySwap(S, S.sel, d);
      else {
        const n = neighbor(S.m, S.cur, d);
        if (n >= 0) S.cur = n;
        curDraw(S);
      }
      return true;
    }
    if (e.key === 'Enter' || e.key === ' ') {
      S.kbd = true;
      select(S, S.sel === S.cur ? -1 : S.cur);
      return true;
    }
    if (e.key === 'Escape' && S.sel >= 0) { select(S, -1); return true; }
    return false;
  }

  function onClick(S, e) {
    const b = e.target.closest('button');
    if (!b || !S.ctx) return;
    const ctx = S.ctx;
    if (b.hasAttribute('data-level')) ctx.act('level', { n: +b.dataset.level });
    else if (b.hasAttribute('data-do')) ctx.act(b.dataset.do);
    else if (b.hasAttribute('data-sound')) { Snd.set(!Snd.on()); bar(S); }
    else if (b.hasAttribute('data-share')) {
      const text = ((view(S).daily) || {}).share || '';
      const done = () => ctx.toast('Скопійовано — встав у Балачки');
      if (navigator.clipboard) navigator.clipboard.writeText(text).then(done, () => ctx.toast(text));
      else ctx.toast(text);
    }
  }

  // ---------- годинник: «на час», підказка ----------

  function tick(S) {
    if (S.dead || !S.ctx) return;
    const v = view(S);
    if (v.mode === 'blitz') {
      bar(S);
      over(S);
      const now = performance.now();
      if (v.phase === 'go' && S.ctx.playing && S.deadline && now >= S.deadline && Date.now() - S.endSent > 2000) {
        S.endSent = Date.now();
        S.ctx.tell('end');
      }
    }
    if (live(S) && !S.anim && !S.busy && !S.hint && !S.drag && Date.now() - S.idleAt > HINT_MS) {
      const moves = allMoves(S.m);
      if (moves.length) {
        S.hint = moves[Math.floor(Math.random() * moves.length)];
        for (const c of S.hint) { const t = at(S.m, c); if (t) t.el.classList.add('hint'); }
      }
    }
    curDraw(S);
  }

  const clock = (ms) => {
    const s = Math.max(0, Math.ceil(ms / 1000));
    return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
  };

  // ---------- рядок над полем, цілі, накладки, бічна панель ----------

  function bar(S) {
    if (!S.el || !S.ctx) return;
    const v = view(S);
    const g = v.game;
    const parts = [];
    if (v.mode === 'path' && v.phase === 'map') {
      parts.push('<span class="kh-big">★ <b>' + (v.total || 0) + '</b> <span class="muted">з ' + (v.max || 90) + '</span></span>');
    } else {
      parts.push('<span class="kh-big kh-score">' + (g ? num(S.shown) : '0') + '</span>');
      if (v.mode === 'blitz') {
        const total = (v.seconds || 120) * 1000;
        // під час відліку годинник стоїть на повних двох хвилинах — час піде разом із «поїхали»
        const left = v.phase === 'go' ? Math.min(total, Math.max(0, S.deadline - performance.now())) : (v.leftMs || 0);
        parts.push('<span class="kh-clock' + (left < 15000 && v.phase === 'go' ? ' hot' : '') + '">⏱ <b>' + clock(left) + '</b>'
          + '<i class="kh-tbar"><i style="width:' + Math.round(100 * left / total) + '%"></i></i></span>');
      } else if (v.mode === 'daily') {
        parts.push('<span>ходів <b>' + (v.left || 0) + '</b> з ' + (v.moves || 30) + '</span>');
      } else if (v.level) {
        parts.push('<span>ходів <b>' + v.level.left + '</b></span>');
      }
    }
    parts.push('<button type="button" class="ghost kh-snd" data-sound title="Звук">' + (Snd.on() ? '🔊' : '🔇') + '</button>');
    setHtml(S.el.bar, parts.join(''));
  }

  function mini(color) {
    return '<span class="kh-mini c' + color + '">' + svg(SYMS[color] || '') + '</span>';
  }

  function goals(S) {
    const v = view(S);
    const L = v.mode === 'path' && v.phase !== 'map' ? v.level : null;
    let html = '';
    let tip = '';
    if (L) {
      html = L.goals.map((g) => {
        const ok = g.have >= g.need;
        const body = g.k === 'c' ? mini(g.color) + ' <b>' + g.have + '</b>/' + g.need
          : g.k === 'l' ? '🧱 глина <b>' + g.have + '</b>/' + g.need
          : g.k === 'r' ? '🪢 мотузки <b>' + g.have + '</b>/' + g.need
          : '⭐ <b>' + num(Math.min(g.have, g.need)) + '</b>/' + num(g.need);
        const title = g.k === 'c' ? 'Зібрати ' + g.need + ' ' + (NAMES[g.color] || '') : g.k === 'l' ? 'Розбити всю глину'
          : g.k === 'r' ? 'Розв’язати всі мотузки' : 'Набрати очок';
        return '<span class="kh-g' + (ok ? ' ok' : '') + '" title="' + title + '">' + body + (ok ? ' ✓' : '') + '</span>';
      }).join('') + '<span class="kh-g kh-st muted" title="Зірки за очки">★★ ' + num(L.two) + ' · ★★★ ' + num(L.three) + '</span>';
      tip = '<b>' + (L.n + 1) + '. ' + S.ctx.esc(L.name) + '</b>' + (L.tip ? ' — ' + S.ctx.esc(L.tip) : '');
    } else if (v.mode === 'daily' && v.phase === 'play') {
      tip = '☀ <b>Кахлі дня №' + ((v.daily || {}).no || '') + '</b> — одне поле на всіх, ' + (v.moves || 30) + ' ходів, одна спроба';
    } else if (v.mode === 'blitz' && v.phase === 'go') {
      tip = 'Рушник — 4 в ряд, жаринка — кутом, глек — 5 в ряд. Поміняй дві спецкахлі між собою — буде сильніше';
    }
    setHtml(S.el.goals, html);
    S.el.goals.hidden = !html;
    setHtml(S.el.tip, tip);
    S.el.tip.hidden = !tip;
  }

  function stars(n) {
    return '<span class="kh-stars">' + [0, 1, 2].map((i) => '<i class="' + (i < n ? 'on' : '') + '">★</i>').join('') + '</span>';
  }

  function over(S) {
    const v = view(S);
    let html = '';
    // кінець партії не накриває поле, поки на ньому ще падають кахлі останнього ходу
    const hold = S.running && ['over', 'won', 'lost', 'done'].includes(v.phase);
    if (hold) { S.el.over.hidden = true; return; }
    if (v.mode === 'blitz') {
      const now = performance.now();
      if (v.phase === 'ready') {
        html = '<div class="kh-big">⏱ Поки піч гаряча</div><p>Дві хвилини на одному полі — склади якнайбільше.<br>'
          + 'Рушники, жаринки й глеки множать очки, каскади — ще більше.</p>'
          + (S.ctx.mine ? '<button type="button" class="primary" data-do="ready">Поїхали!</button>' : '');
      } else if (v.phase === 'go' && S.goAt > now) {
        html = '<div class="kh-count">' + Math.ceil((S.goAt - now) / 1000) + '</div>';
      } else if (v.phase === 'over') {
        const sc = (v.game || {}).score || 0;
        html = '<div class="kh-big">🔥 Піч згасла!</div><div class="kh-final">' + num(sc) + '</div><p>очок за дві хвилини'
          + (sc >= (v.hot || 9e9) ? ' — <b>гаряча піч!</b>' : '') + '</p>';
      }
    } else if (v.mode === 'path' && v.level && (v.phase === 'won' || v.phase === 'lost')) {
      const L = v.level;
      html = v.phase === 'won'
        ? '<div class="kh-big">🏆 Рівень пройдено!</div>' + stars(L.got)
          + '<p>' + num((v.game || {}).score) + ' очок' + (L.fresh ? ' · нових зірок: ' + L.fresh + ' — +' + L.fresh + ' 🏺' : '') + '</p>'
          + '<div class="kh-btns">' + (L.next != null ? '<button type="button" class="primary" data-do="next">Далі →</button>' : '')
          + '<button type="button" class="ghost" data-do="retry">Ще раз</button><button type="button" class="ghost" data-do="map">До мапи</button></div>'
        : '<div class="kh-big">😕 Ходи скінчились</div><p>' + num((v.game || {}).score) + ' очок — цілі ще не виконано</p>'
          + '<div class="kh-btns"><button type="button" class="primary" data-do="retry">Ще раз</button>'
          + '<button type="button" class="ghost" data-do="map">До мапи</button></div>';
    } else if (v.mode === 'daily' && v.phase === 'done') {
      const d = v.daily || {};
      html = '<div class="kh-big">☀ Кахлі дня складено</div><div class="kh-final">' + num((v.game || {}).score) + '</div>'
        + '<p>' + (d.place ? 'ти ' + d.place + '-й з ' + d.players : 'очок') + '</p>';
    }
    // накладку перемальовуємо лише коли вона змінилась (відлік — раз на секунду)
    setHtml(S.el.over, html);
    S.el.over.hidden = !html;
    S.el.over.classList.toggle('soft', v.mode === 'blitz' && v.phase === 'go');
  }

  function side(S) {
    const v = view(S);
    const ctx = S.ctx;
    let html = '';
    if (v.mode === 'path' && v.phase === 'map') {
      const kilns = v.kilns || [];
      const lv = v.levels || [];
      for (let k = 0; k < Math.ceil(lv.length / 10); k++) {
        html += '<div class="kh-kiln"><h4>🔥 ' + ctx.esc(kilns[k] || 'Піч ' + (k + 1)) + '</h4><div class="kh-levels">';
        for (const l of lv.slice(k * 10, k * 10 + 10)) {
          html += '<button type="button" class="kh-lv' + (l.stars ? ' done' : '') + '" data-level="' + l.n + '"' + (l.open ? '' : ' disabled')
            + ' title="' + ctx.esc(l.name) + '"><b>' + (l.open ? l.n + 1 : '🔒') + '</b>' + stars(l.stars) + '<i>' + ctx.esc(l.name) + '</i></button>';
        }
        html += '</div></div>';
      }
      html = '<div class="kh-map">' + html + '<p class="muted small">За кожну нову зірку — 🏺. Зірки — за очки, перша — за виконані цілі.</p></div>';
    } else if (v.mode === 'path' && v.phase === 'play') {
      html = '<div class="kh-btns"><button type="button" class="ghost" data-do="retry">↺ Почати рівень заново</button>'
        + '<button type="button" class="ghost" data-do="map">До мапи</button></div>';
    } else if (v.mode === 'daily' && v.phase === 'done') {
      const d = v.daily || {};
      const rows = (d.board || []).map((r, i) => '<div class="kh-row' + (d.place === i + 1 ? ' me' : '') + '"><span>' + (i + 1) + '.</span><span class="kh-n">'
        + ctx.esc(r.nick) + '</span><span class="kh-marks">' + ctx.esc(r.marks || '') + '</span><b>' + num(r.points) + '</b></div>').join('');
      html = '<div class="muted small">Таблиця дня' + (d.place ? ' · ти ' + d.place + '-й з ' + d.players : '') + '</div>'
        + '<div class="kh-table">' + rows + '</div>'
        + (d.share ? '<button type="button" class="ghost" data-share>📋 Скопіювати результат для Балачок</button>' : '')
        + '<div class="muted small">Нове поле — завтра опівночі</div>';
    }
    setHtml(S.el.side, html);
    S.el.side.hidden = !html;
  }

  // ---------- вид ----------

  function paint(root, ctx) {
    const S = state(root);
    S.ctx = ctx;
    ctx._root = root;
    build(root, S);
    const v = ctx.view || {};
    if (v.mode === 'blitz') {
      // відлік і годинник — від свого performance.now(): вид каже, скільки лишилось на мить відправки
      const now = performance.now();
      if (v.phase === 'go') { S.goAt = now + (v.inMs || 0); S.deadline = now + (v.leftMs || 0); }
      else { S.goAt = 0; S.deadline = 0; }
      if (v.phase !== 'go') S.endSent = 0;
    }
    const g = v.game;
    S.el.stage.hidden = !g && !(v.mode === 'blitz' && v.phase === 'ready');
    if (g) enqueue(S, g);
    else if (S.m) clearBoard(S);
    S.el.board.classList.toggle('kh-empty', !g);
    bar(S);
    goals(S);
    over(S);
    side(S);
    curDraw(S);
  }

  function status(ctx) {
    const v = ctx.view || {};
    if (v.mode === 'path') {
      if (v.phase === 'map') return 'Шлях кахляра · ★ ' + (v.total || 0) + ' з ' + (v.max || 90);
      const L = v.level || {};
      if (v.phase === 'won') return 'Рівень ' + (L.n + 1) + ' пройдено: ' + '★'.repeat(L.got || 0) + '☆'.repeat(3 - (L.got || 0));
      if (v.phase === 'lost') return 'Ходи скінчились — спробуй ще';
      return 'Рівень ' + (L.n + 1) + ' «' + (L.name || '') + '» · ходів ' + (L.left || 0);
    }
    if (v.mode === 'blitz') {
      if (v.phase === 'ready') return 'Тисни «Поїхали» — і дві хвилини, поки піч гаряча';
      if (v.phase === 'over') return 'За дві хвилини: ' + num((v.game || {}).score) + ' очок';
      return 'Склади якнайбільше, поки піч гаряча';
    }
    if (v.mode === 'daily') {
      const d = v.daily || {};
      if (v.phase === 'done') return 'Кахлі дня №' + (d.no || '') + ' складено: ' + num((v.game || {}).score) + (d.place ? ' · ' + d.place + '-й з ' + d.players : '');
      return 'Кахлі дня №' + (d.no || '') + ' · ходів ' + (v.left || 0) + ' з ' + (v.moves || 30);
    }
    return '';
  }

  const mod = (id) => ({
    id,
    icon: ICON,
    added: '2026-10-20',
    mount(root, ctx) { paint(root, ctx); },
    update(root, ctx) { paint(root, ctx); },
    unmount(root) {
      const S = root._kh;
      if (!S) return;
      S.dead = true;
      clearInterval(S.timer);
      root._kh = null;
    },
    onKey,
    status,
    pad: {
      dirs: true,
      a: 'Enter',
      hint: '{dpad} рамка по полю · {a} узяти кахлю, тоді {dpad} — куди поміняти',
      when: (ctx) => !!(ctx.playing && ctx.view && ctx.view.game && ctx.view.turn === 0),
    },
  });

  HGames.register(mod('kakhli'));
  HGames.register(mod('kakhli-blitz'));
  HGames.register(mod('kakhli-daily'));
})();
