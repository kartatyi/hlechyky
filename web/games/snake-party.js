/*
  «Змійки гуртом» (snake-party, 2–4). До проходу №3 їх малював спільний із мотоциклами snake-modes.js; тепер у
  змійок своє ядро (Impl/SnakeArena.cs) і свій модуль: яблука з розбитих змійок, кидки вибулих, бонуси, тор,
  серія з автостартом і режим «на час».

  Вид (подія 'room'): { width, height, b: string[] (тіло місця рядком: номер клітинки голови, далі r/d/l/u — куди
    наступна клітинка від попередньої, на торі й через край), dirs, present: bool[], al: маска живих, crash: int[],
    place, wins, ap: int[] (яблука), startIn, winner: null|'win'|'draw', winners: int[],
    wrap?, bonus?, timed? (скільки тиків триває раунд «на час»), ser?/round?/over? (серія), …кадрові поля }.
  Кадр (10 на секунду): { b, ap, al, startIn, winner } і лише коли є що сказати: lt (яблука-здобич), rk (камінці),
    bn: [клітинка, 4 ⭐ | 5 ✂ | 6 ❄], sl (маска сповільнених), tl (тиків до кінця «на час»), nx (тиків до наступного
    раунду серії), rs (тиків до відродження за місцями), dc (тиків до наступного кидка вибулого), ev: [[що, місце, клітинка]].
  Ввід: Input('turn', { dir }) — 0 праворуч, 1 вниз, 2 ліворуч, 3 вгору; Act('drop', { cell, k: 'a'|'r' }) — кидок
    вибулого (cell −1 — «кинь сам»: яблуко куди випаде, камінь перед носом найдовшої).
*/
(() => {
  const W = 26, PX = 16, TICK = 120;
  const DIRS = { ArrowRight: 0, KeyD: 0, ArrowDown: 1, KeyS: 1, ArrowLeft: 2, KeyA: 2, ArrowUp: 3, KeyW: 3 };
  const STEP = [[1, 0], [0, 1], [-1, 0], [0, -1]];
  /// Кольори місць і фігурки на голові: колір — для ока, фігурка — для тих, у кого кольори зливаються.
  const RIDERS = [
    { v: '--accent', f: '#f4c542', shape: 'circle', mark: '●' },
    { v: '--ok', f: '#7bd389', shape: 'square', mark: '■' },
    { v: '--snp2', f: '#6fb3e8', shape: 'triangle', mark: '▲' },
    { v: '--snp3', f: '#e88ac0', shape: 'diamond', mark: '◆' },
  ];
  const BONUS = { 4: '⭐', 5: '✂️', 6: '❄️' };
  const FX = { g: '+3', s: '✂️', z: '❄️', a: '🍎', r: '🪨', b: '✨' };
  const FX_MS = 1400;

  const html = (el, s) => {
    if (HGames.ui.html) { HGames.ui.html(el, s); return; }
    if (el._h === s) return;
    el._h = s;
    el.innerHTML = s;
  };

  /// Палітра з CSS-змінних: getComputedStyle — раз на колір і вид, а не на кожен кадр.
  function palette(css) {
    const m = new Map();
    return (name, fallback) => {
      let v = m.get(name);
      if (v === undefined) { v = css(name, fallback); m.set(name, v); }
      return v;
    };
  }

  /// Тіло з рядка (див. шапку): «156ll» → [156, 181, 180].
  function unpack(s, w, h) {
    if (!s) return [];
    let i = 0;
    while (i < s.length && s.charCodeAt(i) >= 48 && s.charCodeAt(i) <= 57) i++;
    let c = +s.slice(0, i);
    const out = [c];
    for (; i < s.length; i++) {
      let x = c % w, y = Math.floor(c / w);
      const ch = s[i];
      if (ch === 'r') x++; else if (ch === 'l') x--; else if (ch === 'd') y++; else y--;
      c = ((y + h) % h) * w + ((x + w) % w);
      out.push(c);
    }
    return out;
  }

  /// Куди дивиться голова — з голови й «шиї» (на торі шия буває по той бік краю).
  function heading(cells, w, h) {
    if (!cells || cells.length < 2) return null;
    const a = cells[0], b = cells[1];
    let dx = (a % w) - (b % w), dy = Math.floor(a / w) - Math.floor(b / w);
    if (dx === -(w - 1)) dx = 1; else if (dx === w - 1) dx = -1;
    if (dy === -(h - 1)) dy = 1; else if (dy === h - 1) dy = -1;
    return dx === 1 ? 0 : dy === 1 ? 1 : dx === -1 ? 2 : dy === -1 ? 3 : null;
  }

  // Дзеркало серверної черги поворотів — своя голова «дивиться» туди, куди щойно натиснули (як у мотоциклах).
  function queueTurn(st, cur, dir) {
    const now = performance.now();
    st.q = st.q.filter((t) => now - t.at < 400);
    const last = st.q.length ? st.q[st.q.length - 1].dir : cur;
    if (st.q.length >= 2 || last == null || dir === last || (dir + 2) % 4 === last) return;
    st.q.push({ dir, at: now });
  }
  function settleTurns(st, cur) {
    const now = performance.now();
    st.q = st.q.filter((t) => now - t.at < 400);
    if (st.q.length && st.q[0].dir === cur) st.q.shift();
  }
  function facing(st, cur) {
    const t = st.q[0];
    return t && performance.now() - t.at < 400 ? t.dir : cur;
  }

  function nose(g, x, y, dir, color) {
    const c = PX / 2, r = PX * 0.24;
    const [dx, dy] = STEP[dir];
    const tx = x + c + dx * (c - 2), ty = y + c + dy * (c - 2);
    const bx = tx - dx * r * 1.6, by = ty - dy * r * 1.6;
    g.fillStyle = color;
    g.beginPath();
    g.moveTo(tx, ty);
    g.lineTo(bx - dy * r, by + dx * r);
    g.lineTo(bx + dy * r, by - dx * r);
    g.closePath();
    g.fill();
  }

  function shapeAt(g, x, y, shape, color) {
    const cx = x + PX / 2, cy = y + PX / 2, r = PX / 2 - 3.5;
    g.fillStyle = color;
    g.beginPath();
    if (shape === 'circle') g.arc(cx, cy, r, 0, Math.PI * 2);
    else if (shape === 'square') g.rect(cx - r, cy - r, r * 2, r * 2);
    else if (shape === 'triangle') { g.moveTo(cx, cy - r - 0.5); g.lineTo(cx + r + 0.5, cy + r); g.lineTo(cx - r - 0.5, cy + r); g.closePath(); }
    else { g.moveTo(cx, cy - r - 1); g.lineTo(cx + r + 1, cy); g.lineTo(cx, cy + r + 1); g.lineTo(cx - r - 1, cy); g.closePath(); }
    g.fill();
  }

  // =============================================================================================
  // Стан
  // =============================================================================================

  function state(root, ctx) {
    if (!root._snp) {
      root._snp = {
        cv: null, view: null, w: W, h: 18, css: palette(ctx.css),
        b: ['', '', '', ''], t: [[], [], [], []], ap: [], lt: [], rk: [], bn: null, sl: 0,
        al: 0, crash: [-1, -1, -1, -1], place: [], wins: [], present: [], startIn: 0, winner: null, winners: [],
        wrap: false, bonus: false, timed: 0, ser: 0, round: 0, over: false, tl: 0, nx: 0, rs: null, dc: null,
        fx: [], kind: 'a', q: [], waiting: true,
      };
    }
    root._snp.ctx = ctx;
    ctx._snp = root._snp;
    return root._snp;
  }

  const bodies = (st, b) => { st.b = b; st.t = b.map((s) => unpack(s, st.w, st.h)); };

  function applyView(st, v) {
    st.view = v;
    st.w = v.width || W;
    st.h = v.height || 18;
    bodies(st, (v.b || []).slice());
    st.crash = (v.crash || []).slice();
    st.place = (v.place || []).slice();
    st.wins = (v.wins || []).slice();
    st.present = (v.present || []).slice();
    st.winners = (v.winners || []).slice();
    st.wrap = !!v.wrap;
    st.bonus = !!v.bonus;
    st.timed = v.timed || 0;
    st.ser = v.ser || 0;
    st.round = v.round || 0;
    st.over = !!v.over;
    applyFrame(st, v);
  }

  /// Кадрові поля (вид несе їх теж). Необов'язкових у кадрі може не бути — тоді «нема».
  function applyFrame(st, f) {
    st.ap = f.ap || [];
    st.lt = f.lt || [];
    st.rk = f.rk || [];
    st.bn = f.bn || null;
    st.sl = f.sl || 0;
    st.al = f.al || 0;
    st.startIn = f.startIn || 0;
    st.winner = f.winner == null ? null : f.winner;
    st.tl = f.tl || 0;
    st.nx = f.nx || 0;
    st.rs = f.rs || null;
    st.dc = f.dc || null;
    if (Array.isArray(f.ev) && f.ev !== st.lastEv) {
      st.lastEv = f.ev;
      const now = performance.now();
      for (const [k, s, c] of f.ev) st.fx.push({ k, s, c, at: now });
    }
  }

  const alive = (st, s) => (st.al & (1 << s)) !== 0;
  const me = (st) => (st.ctx.mine && st.ctx.seat != null && st.present[st.ctx.seat] ? st.ctx.seat : null);
  /// Моє тіло, поки я живий (null — глядач або вибула).
  function mine(st) {
    const s = me(st);
    return s != null && alive(st, s) ? st.t[s] : null;
  }
  /// Я вибула в раунді «до останньої» й можу кидати (№151).
  function thrower(st) {
    const s = me(st);
    return s != null && !alive(st, s) && !st.timed && st.startIn <= 0 && st.winner == null && st.ctx.playing;
  }
  const dropWait = (st) => { const s = me(st); return s != null && st.dc ? st.dc[s] || 0 : 0; };

  function turn(st, dir) {
    const ctx = st.ctx;
    if (!ctx || !ctx.mine || !ctx.playing) return;
    ctx.input('turn', { dir });
    const body = mine(st);
    if (!body || st.winner != null) return;
    queueTurn(st, heading(body, st.w, st.h), dir);
    draw(st);
  }

  function drop(st, cell, kind) {
    if (!thrower(st)) return false;
    st.kind = kind || st.kind;
    st.ctx.act('drop', { cell, k: st.kind === 'r' ? 'r' : 'a' });
    return true;
  }

  // =============================================================================================
  // Малюнок
  // =============================================================================================

  function draw(st) {
    if (!st.cv) return;
    const ctx = st.ctx;
    const waiting = !ctx.playing;
    st.waiting = waiting;
    const g = st.cv.ctx, w = st.w, cw = st.cv.w, ch = st.cv.h;
    const cx = (cell) => (cell % w) * PX, cy = (cell) => Math.floor(cell / w) * PX;
    g.fillStyle = st.css('--bg2', '#16291f');
    g.fillRect(0, 0, cw, ch);
    if (st.wrap) {
      // тор: край — пунктир, а не стіна
      g.strokeStyle = st.css('--snp-tor', 'rgba(111, 179, 232, .55)');
      g.lineWidth = 2;
      g.setLineDash([6, 6]);
      g.strokeRect(1, 1, cw - 2, ch - 2);
      g.setLineDash([]);
    }

    // вибула й кидаю — підсвітимо, куди не можна (ближче трьох клітинок до голів)
    if (thrower(st)) {
      g.fillStyle = st.css('--snp-nogo', 'rgba(229, 115, 115, .13)');
      for (let s = 0; s < 4; s++) {
        if (!alive(st, s) || !st.t[s].length) continue;
        const hx = st.t[s][0] % w, hy = Math.floor(st.t[s][0] / w);
        g.fillRect((hx - 2) * PX, (hy - 2) * PX, PX * 5, PX * 5);
      }
    }

    const clay = st.css('--clay', '#c5763a');
    g.fillStyle = clay;
    for (const a of st.ap) { g.beginPath(); g.arc(cx(a) + PX / 2, cy(a) + PX / 2, PX / 2 - 2.5, 0, Math.PI * 2); g.fill(); }
    // здобич — трохи менша, з листочком: видно, що це не «звичайне» яблуко, а крихти чиєїсь змійки
    const leaf = st.css('--ok', '#7bd389');
    for (const a of st.lt) {
      g.fillStyle = clay;
      g.beginPath(); g.arc(cx(a) + PX / 2, cy(a) + PX / 2 + 1, PX / 2 - 3.5, 0, Math.PI * 2); g.fill();
      g.fillStyle = leaf;
      g.fillRect(cx(a) + PX / 2, cy(a) + 2, 3, 3);
    }
    const stone = st.css('--muted', '#8a9a90');
    for (const r of st.rk) {
      g.fillStyle = stone;
      g.beginPath(); g.roundRect(cx(r) + 1.5, cy(r) + 2.5, PX - 3, PX - 4, 5); g.fill();
      g.strokeStyle = st.css('--bg', '#0f1f18');
      g.lineWidth = 1.5;
      g.stroke();
    }
    if (st.bn) {
      const [c, k] = st.bn;
      // бонус світиться, щоб його помітили й на дрібному полі телефона
      const pulse = 0.35 + 0.25 * Math.sin(performance.now() / 180);
      g.globalAlpha = pulse;
      g.fillStyle = k === 6 ? '#8fd3ff' : k === 5 ? '#e0e0e0' : '#ffd54a';
      g.beginPath(); g.arc(cx(c) + PX / 2, cy(c) + PX / 2, PX * 0.85, 0, Math.PI * 2); g.fill();
      g.globalAlpha = 1;
      g.font = (PX - 2) + 'px system-ui, "Segoe UI Emoji", sans-serif';
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      g.fillText(BONUS[k] || '?', cx(c) + PX / 2, cy(c) + PX / 2 + 1);
    }

    const dark = st.css('--bg', '#0f1f18');
    const my = mine(st);
    for (let s = 0; s < 4; s++) {
      const cells = st.t[s];
      if (!cells || !cells.length) continue;
      const r = RIDERS[s];
      g.fillStyle = st.css(r.v, r.f);
      for (let i = 0; i < cells.length; i++) {
        g.beginPath();
        g.roundRect(cx(cells[i]) + 1, cy(cells[i]) + 1, PX - 2, PX - 2, i ? 3 : 6);
        g.fill();
      }
      if (st.sl & (1 << s)) {
        // сповільнена — інеєм по тілу
        g.fillStyle = 'rgba(190, 230, 255, .45)';
        for (const c of cells) g.fillRect(cx(c) + 3, cy(c) + 3, PX - 6, PX - 6);
      }
      shapeAt(g, cx(cells[0]), cy(cells[0]), r.shape, dark);
      if (cells === my && st.winner == null) {
        const d = facing(st, heading(cells, w, st.h));
        if (d != null) nose(g, cx(cells[0]), cy(cells[0]), d, st.css('--text', '#ecf1ea'));
      }
    }

    const crosses = () => {
      g.strokeStyle = st.css('--danger', '#e57373');
      g.lineWidth = 2.5;
      st.crash.forEach((cell, s) => {
        if (cell == null || cell < 0 || !st.present[s]) return;
        const x = cx(cell), y = cy(cell);
        g.beginPath();
        g.moveTo(x + 3, y + 3); g.lineTo(x + PX - 3, y + PX - 3);
        g.moveTo(x + PX - 3, y + 3); g.lineTo(x + 3, y + PX - 3);
        g.stroke();
      });
    };

    // спалахи подій: +3, ✂, ❄, кинуте яблуко чи камінь, відродження
    const k = Math.max(1, cw / (W * PX));
    if (st.fx.length) {
      const now = performance.now();
      st.fx = st.fx.filter((e) => now - e.at < FX_MS);
      g.textAlign = 'center';
      g.textBaseline = 'middle';
      for (const e of st.fx) {
        const t = (now - e.at) / FX_MS;
        g.globalAlpha = 1 - t;
        g.font = '700 ' + Math.round(15 * k) + 'px system-ui, "Segoe UI Emoji", sans-serif';
        g.fillStyle = st.css('--text', '#ecf1ea');
        g.fillText(FX[e.k] || '•', cx(e.c) + PX / 2, cy(e.c) - 4 - t * 14);
      }
      g.globalAlpha = 1;
    }

    const seat = me(st);
    const counting = st.startIn > 0 && !waiting && st.winner == null;
    // «на час»: моя змійка розбилась — скільки до повернення
    if (!counting && st.winner == null && st.timed && seat != null && st.rs && st.rs[seat] > 0) {
      g.fillStyle = st.css('--text', '#ecf1ea');
      g.textAlign = 'center';
      g.textBaseline = 'top';
      g.font = '700 ' + Math.round(18 * k) + 'px system-ui, sans-serif';
      g.fillText('знову в грі за ' + Math.ceil((st.rs[seat] * TICK) / 1000) + '…', cw / 2, 8);
    }
    if (!counting && st.winner == null) { crosses(); return; }

    g.fillStyle = st.css('--gshade', 'rgba(15, 31, 24, .62)');
    g.fillRect(0, 0, cw, ch);
    crosses();
    g.fillStyle = st.css('--text', '#ecf1ea');
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    if (counting) {
      if (seat != null && st.t[seat] && st.t[seat].length) {
        const x = cx(st.t[seat][0]), y = cy(st.t[seat][0]);
        g.strokeStyle = st.css('--text', '#ecf1ea');
        g.lineWidth = 2;
        g.beginPath();
        g.arc(x + PX / 2, y + PX / 2, PX * 1.15, 0, Math.PI * 2);
        g.stroke();
      }
      g.font = '700 ' + Math.round(46 * k) + 'px system-ui, sans-serif';
      g.fillText(String(Math.ceil((st.startIn * TICK) / 1000)), cw / 2, ch / 2);
      g.font = '600 ' + Math.round(17 * k) + 'px system-ui, sans-serif';
      if (st.ser && st.round > 1) g.fillText('Раунд ' + st.round, cw / 2, ch / 2 - 44 * k);
      if (seat != null) {
        const r = RIDERS[seat];
        g.fillStyle = st.css(r.v, r.f);
        g.fillText('ти — ' + r.mark + ' ' + ctx.seatName(seat), cw / 2, ch / 2 + 42 * k);
      }
      return;
    }
    const who = (st.winners || []).map((s) => ctx.nickOf(s) || ctx.seatName(s));
    const between = st.ser && st.nx > 0;
    g.font = '700 ' + Math.round(26 * k) + 'px system-ui, sans-serif';
    const title = st.winner === 'draw' || !who.length ? 'Нічия' : '🏆 ' + who.join(' і ');
    g.fillText(between ? title + (who.length ? ' бере раунд' : '') : title, cw / 2, ch / 2 - (between ? 14 * k : 0), cw - 24);
    if (between) {
      g.font = '600 ' + Math.round(16 * k) + 'px system-ui, sans-serif';
      g.fillText('Наступний раунд за ' + Math.ceil((st.nx * TICK) / 1000) + '… · до ' + st.ser + ' перемог', cw / 2, ch / 2 + 22 * k, cw - 24);
    } else if (st.ser && st.over && who.length) {
      g.font = '600 ' + Math.round(16 * k) + 'px system-ui, sans-serif';
      g.fillText('серія до ' + st.ser + ' — ваша!', cw / 2, ch / 2 + 26 * k, cw - 24);
    }
  }

  // =============================================================================================
  // Табло, кидки, хрестовина
  // =============================================================================================

  /// Табло: раунд серії / годинник «на час», далі фігурка, нік і перемоги кожного; хто вибув — блідий.
  function scoreboard(root, ctx, st) {
    let el = root.querySelector(':scope > .snp-score');
    if (!el) {
      el = document.createElement('div');
      el.className = 'snp-score';
      root.insertBefore(el, root.firstChild);
    }
    const parts = [];
    if (st.ser) parts.push('<em>Раунд ' + st.round + ' · до ' + st.ser + '</em>');
    if (st.timed) {
      const sec = Math.ceil(((st.startIn > 0 && !st.tl ? st.timed : st.tl) * TICK) / 1000);
      parts.push('<em class="snp-clock' + (sec <= 10 && st.winner == null ? ' snp-hot' : '') + '">⏱ '
        + Math.floor(sec / 60) + ':' + String(sec % 60).padStart(2, '0') + '</em>');
    }
    for (let s = 0; s < 4; s++) {
      if (!st.present[s]) continue;
      const out = !st.timed && st.startIn <= 0 && !alive(st, s) && st.place[s] !== 1;
      const nick = ctx.nickOf(s) || ctx.seatName(s);
      const len = st.timed && st.t[s] ? ' <small>' + st.t[s].length + '</small>' : '';
      parts.push('<span class="snp-s' + s + (out ? ' snp-out' : '') + (ctx.seat === s ? ' snp-me' : '') + '"><i>'
        + RIDERS[s].mark + '</i>' + ctx.esc(nick) + ' <b>' + (st.wins[s] || 0) + '</b>' + len + '</span>');
    }
    html(el, parts.join(''));
  }

  /// Панель вибулого: що кидати і скільки ще чекати. Лише в раунді «до останньої».
  function dropBar(root, st) {
    let el = root.querySelector(':scope > .snp-drop');
    if (!thrower(st)) { if (el) el.remove(); return; }
    if (!el) {
      el = document.createElement('div');
      el.className = 'snp-drop';
      el.addEventListener('pointerdown', (e) => {
        const b = e.target.closest('button[data-k]');
        if (!b) return;
        e.preventDefault();
        const s = root._snp;
        if (s) { s.kind = b.dataset.k; dropBar(root, s); }
      });
      const cvEl = st.cv && st.cv.el;
      if (cvEl && cvEl.nextSibling) root.insertBefore(el, cvEl.nextSibling); else root.appendChild(el);
    }
    const wait = dropWait(st);
    const sec = Math.ceil((wait * TICK) / 1000);
    html(el, '<button type="button" data-k="a" class="' + (st.kind === 'a' ? 'snp-on' : '') + '">🍎 Яблуко</button>'
      + '<button type="button" data-k="r" class="' + (st.kind === 'r' ? 'snp-on' : '') + '">🪨 Камінь на 3 с</button>'
      + '<span>' + (wait > 0 ? 'ще ' + sec + ' с…' : (HGames.ui.coarse && HGames.ui.coarse() ? 'тапни по полю' : 'клацни по полю · F / R')) + '</span>');
  }

  /// Тап по полю, коли вибула: клітинка під пальцем → кидок.
  function tapDrop(el) {
    if (el._snpTap) return;
    el._snpTap = true;
    el.addEventListener('pointerdown', (e) => {
      const st = el._snpRoot && el._snpRoot._snp;
      if (!st || !thrower(st) || e.button > 0) return;
      const r = el.getBoundingClientRect();
      if (!r.width || !r.height) return;
      const x = Math.floor(((e.clientX - r.left) / r.width) * st.w);
      const y = Math.floor(((e.clientY - r.top) / r.height) * st.h);
      if (x < 0 || y < 0 || x >= st.w || y >= st.h) return;
      e.preventDefault();
      drop(st, y * st.w + x);
    });
  }

  /// Свайп по полю: провів пальцем щонайменше 18 px — поворот у бік переважної осі (як у мотоциклах).
  function swipe(el) {
    if (el._snpSwipe) return;
    el._snpSwipe = true;
    let from = null;
    const live = () => { const st = el._snpRoot && el._snpRoot._snp; return st && st.ctx.mine && st.ctx.playing && mine(st) ? st : null; };
    el.addEventListener('pointerdown', (e) => {
      if (e.button > 0 || !live()) return;
      from = { x: e.clientX, y: e.clientY, id: e.pointerId };
      try { el.setPointerCapture(e.pointerId); } catch { /* стара миша без capture */ }
    });
    el.addEventListener('pointermove', (e) => {
      if (!from || e.pointerId !== from.id) return;
      const dx = e.clientX - from.x, dy = e.clientY - from.y;
      if (Math.max(Math.abs(dx), Math.abs(dy)) < 18) return;
      from.x = e.clientX;
      from.y = e.clientY;
      const st = live();
      if (st) turn(st, Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3));
    });
    const end = (e) => { if (from && e.pointerId === from.id) from = null; };
    el.addEventListener('pointerup', end);
    el.addEventListener('pointercancel', end);
  }

  function dpad(root, ctx, st) {
    if (!ctx.mine) { const d = root.querySelector(':scope > .dpad'); if (d) d.remove(); return; }
    const el = HGames.ui.dpad(root, (d) => turn(st, d));
    if (el && el.style.touchAction !== 'manipulation') el.style.touchAction = 'manipulation';
  }

  function canvas(root, st) {
    st.cv = HGames.ui.canvas(root, { w: st.w * PX, h: st.h * PX, cls: 'snp-board' + (st.w > W ? ' snp-big' : '') });
    st.cv.el._snpRoot = root;
  }

  function touchable(el, on) {
    const want = on ? 'none' : '';
    if (el.style.touchAction !== want) el.style.touchAction = want;
  }

  HGames.register({
    id: 'snake-party',
    icon: '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
      + '<path d="M2 13h4.2a2.6 2.6 0 0 0 0-5.2H5.2a2.6 2.6 0 0 1 0-5.2H9" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>'
      + '<path d="M13.5 14V9.5" fill="none" stroke="var(--ok)" stroke-width="2" stroke-linecap="round"/>'
      + '<circle cx="12.6" cy="3.4" r="1.9" fill="var(--clay)"/></svg>',
    seatNames: ['жовта', 'зелена', 'синя', 'рожева'],
    seatClass: ['snp-s0', 'snp-s1', 'snp-s2', 'snp-s3'],
    pad: { dirs: true, a: 'KeyF', x: 'KeyR', hint: '{dpad} куди повзти · вибула: {a} кинути яблуко, {x} камінь' },
    news: {
      v: '2026-09-29',
      title: 'Змійки гуртом: яблука з розбитих, кидки й бонуси',
      items: [
        '🍎 Розбита змійка розсипається яблуками — хапай здобич, поки не вхопили інші',
        '🪨 Вибула? Тапни по полю — кинь яблуко чи камінець на 3 с (раз на 5 с, не під самий ніс)',
        '🔁 Партія до 3 чи 5 перемог: наступний раунд стартує сам, без «Ще раз»',
        '⏱ Режим «на час»: 90 с, розбилась — за 2 с знову на полі, перемагає найдовша',
        '🌀 Поле-тор без стін і ✨ бонуси: ⭐ +3, ✂ хвіст навпіл, ❄ пригальмувати решту',
      ],
    },

    mount(root, ctx) {
      const st = state(root, ctx);
      canvas(root, st);
      swipe(st.cv.el);
      tapDrop(st.cv.el);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      if (!st.cv) return;
      st.css = palette(ctx.css);
      dpad(root, ctx, st);
      const v = ctx.view;
      if (v && Array.isArray(v.b) && v !== st.view) {
        applyView(st, v);
        if (st.startIn > 0 || st.winner != null) st.q = [];
      }
      canvas(root, st);
      swipe(st.cv.el);
      tapDrop(st.cv.el);
      touchable(st.cv.el, ctx.mine && ctx.playing);
      scoreboard(root, ctx, st);
      dropBar(root, st);
      draw(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!st.cv || !f) return;
      if (Array.isArray(f.b)) bodies(st, f.b);
      applyFrame(st, f);
      scoreboard(root, ctx, st);
      dropBar(root, st);
      const body = mine(st);
      if (body) settleTurns(st, heading(body, st.w, st.h));
      draw(st);
    },

    onKey(e, ctx) {
      const st = ctx._snp;
      if (st && (e.code === 'KeyF' || e.code === 'KeyR') && thrower(st)) {
        if (!e.repeat) drop(st, -1, e.code === 'KeyR' ? 'r' : 'a');
        return true;
      }
      const dir = DIRS[e.code];
      if (dir === undefined || !ctx.mine || !ctx.playing) return false;
      if (e.repeat) return true;   // автоповтор лише їв би квоту Input
      if (st) turn(st, dir); else ctx.input('turn', { dir });
      return true;
    },

    status(ctx) {
      if (!ctx.playing) return '';
      const st = ctx._snp;
      if (!st) return '';
      if (st.startIn > 0) return 'Готуйсь…';
      if (st.winner != null) return st.ser && st.nx > 0 ? 'Наступний раунд стартує сам' : '';
      if (!ctx.mine) return 'Дивишся збоку';
      const s = me(st);
      if (s != null && alive(st, s)) {
        return st.timed ? 'Їж і не врізайся: розбилась — за 2 с знову, а за годинником перемагає найдовша'
          : 'Стрілки або WASD — їж яблука й не врізайся';
      }
      if (st.timed) return 'Розбилась! За мить знову на полі';
      return 'Вибула — кидай 🍎 чи 🪨 на поле й дивись, хто переживе решту';
    },

    unmount(root) {
      if (root._snp && root._snp.cv) root._snp.cv.el._snpRoot = null;
      root._snp = null;
    },
  });
})();
