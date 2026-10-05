/*
  Гостинці — спільний жолоб з гори, одна кнопка «Хапай» (Impl/Hostyntsi.cs; spec docs/games/specs/hostyntsi.md §4).

  Вид (раз на подію): { phase, round, rounds, party, skin, basketMax, spacing, halfWin, jitterMs, tickMs, values,
    cat, goldMul, totals[8], sums[8][], left[8], winner, winners, series, bot…, frame }.
  Кадр (20 Гц у грі): { t, ph (0 відлік · 1 жолоб · 2 підсумок раунду · 3 кінець · 4 лобі), r, left, sl, v, len,
    st[8] (станція, 0 — верхня), g: [[id, kind, d, mul×100]], p: [8 × [уКошику, вартість, рахунок, опік, руки] | null],
    b: [8 × [kind, val, …] | null], ev: [[t, тип, місце, kind, val, id]] }.
  Ввід: Input('grab', { id }) — id гостинця, що зараз у моєму вікні (найнижчий).

  Жолоб малюємо змійкою згори вниз: станція k стоїть на k-му вигині (по черзі праворуч і ліворуч), вікно — ±halfWin
  довкола вигину. Між кадрами гостинці їдуть самі (d += v·mul/100·dt), свій хап показуємо одразу (гостинець летить у
  кошик), а кадр лише підтверджує — подія 1/3 з цим id — чи відкочує («повз!»).
  Модуль вбудовується у вечірку (HGames.embed): нічого поза ctx і своїм root, розмір міряємо по root.
*/
(() => {
  const TICK = 50, SP = 1000, HW = 250, BASKET = 8;
  const SEAT = ['#5aa9ff', '#e08a5f', '#7bd389', '#f4c542', '#b48cf2', '#6fd6c2', '#f08cb8', '#b7c2bd'];
  const VAL = [1, 3, 5, -3, -1, 0];
  const NAMES = ['глек', 'розписний', 'золотий', 'жар', 'гнилий гарбуз', 'кіт у мішку'];
  const NIK = ['подарунок', 'великий подарунок', 'золотий подарунок', 'різочка', 'гнилий гарбуз', 'кіт у мішку'];
  const ICON = '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M6 1.6h4l-.5 1.4c1.9.6 2.6 2 2.2 3.4H4.3C3.9 5 4.6 3.6 6.5 3Z" fill="var(--accent)"/>'
    + '<path d="M1.6 7h12.8l-1.7 7.4H3.3Z" fill="var(--clay)"/>'
    + '<path d="M2.6 9.4h10.8M3.1 11.9h9.8M6 7v7.4M10 7v7.4" stroke="var(--text)" stroke-width=".8" opacity=".55"/></svg>';

  const live = new Set();
  const reduced = () => !!(window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches);
  const coarse = () => HGames.ui.coarse();
  const nameAt = (ctx, i) => (ctx.nameOf && ctx.nameOf(i)) || ctx.seatName(i);
  const short = (s, n) => (s && s.length > n ? s.slice(0, n - 1) + '…' : s || '');
  const sign = (v) => (v > 0 ? '+' + v : v < 0 ? '−' + -v : '0');
  const kname = (k, nik) => (nik ? NIK : NAMES)[k];
  const good = (k) => k === 0 || k === 1 || k === 2;
  /// Рахунок «зараз»: поки котиться — разом із кошиком; з кінцем раунду кошик уже висипано в рахунок.
  const scoreOf = (f, q) => (q ? q[2] + (f && f.ph <= 1 ? q[1] : 0) : 0);

  // =============================================================================================
  // Спрайти: малюємо руками, щоб глек був глеком у будь-якому браузері (емодзі різняться й не крутяться)
  // =============================================================================================

  function potShape(c, r) {
    c.beginPath();
    c.moveTo(-r * 0.36, -r * 0.6);
    c.bezierCurveTo(-r * 1.08, -r * 0.34, -r * 1.02, r * 0.86, 0, r * 0.92);
    c.bezierCurveTo(r * 1.02, r * 0.86, r * 1.08, -r * 0.34, r * 0.36, -r * 0.6);
    c.closePath();
  }

  function pot(c, r, body, edge, rim) {
    potShape(c, r);
    c.fillStyle = body;
    c.fill();
    c.lineWidth = Math.max(1, r * 0.09);
    c.strokeStyle = edge;
    c.stroke();
    c.fillStyle = rim;
    c.beginPath();
    c.ellipse(0, -r * 0.66, r * 0.48, r * 0.17, 0, 0, Math.PI * 2);
    c.fill();
    c.stroke();
  }

  function shine(c, r, a) {
    c.fillStyle = 'rgba(255,255,255,' + a + ')';
    c.beginPath();
    c.ellipse(-r * 0.4, -r * 0.02, r * 0.14, r * 0.36, 0.35, 0, Math.PI * 2);
    c.fill();
  }

  function star(c, x, y, s) {
    c.beginPath();
    for (let i = 0; i < 8; i++) {
      const a = (i * Math.PI) / 4, q = i % 2 ? s * 0.32 : s;
      c.lineTo(x + Math.cos(a) * q, y + Math.sin(a) * q);
    }
    c.closePath();
    c.fill();
  }

  function box(c, r, body, ribbon) {
    const s = r * 0.82;
    c.fillStyle = body;
    c.strokeStyle = 'rgba(0,0,0,.35)';
    c.lineWidth = Math.max(1, r * 0.08);
    c.beginPath();
    c.rect(-s, -s * 0.7, s * 2, s * 1.6);
    c.fill();
    c.stroke();
    c.fillStyle = ribbon;
    c.fillRect(-s * 0.16, -s * 0.7, s * 0.32, s * 1.6);
    c.fillRect(-s, -s * 0.05, s * 2, s * 0.3);
    c.beginPath();
    c.ellipse(-s * 0.32, -s * 0.86, s * 0.34, s * 0.2, -0.5, 0, Math.PI * 2);
    c.ellipse(s * 0.32, -s * 0.86, s * 0.34, s * 0.2, 0.5, 0, Math.PI * 2);
    c.fill();
  }

  /// Гостинець з центром у (0,0) і радіусом r. ph — час, мс (жар мерехтить, кіт ворушить вухами).
  function gift(c, kind, r, nik, ph) {
    if (nik && kind <= 2) {
      if (kind === 0) box(c, r * 0.95, '#d6453d', '#f4d36b');
      else if (kind === 1) box(c, r * 1.12, '#2f9e5b', '#e8433b');
      else {
        box(c, r * 1.02, '#f2c230', '#c8322c');
        c.fillStyle = 'rgba(255,255,255,.9)';
        star(c, r * 0.55, -r * 0.55, r * 0.3 * (0.7 + 0.3 * Math.sin(ph / 140)));
      }
      return;
    }
    if (kind === 0) {
      pot(c, r, '#b9693c', '#6e3a1d', '#a45a31');
      shine(c, r, 0.28);
    } else if (kind === 1) {
      pot(c, r, '#f4e8cf', '#7a5a3a', '#e7d6b4');
      // петриківка: синя хвиля й червоні ягідки
      c.strokeStyle = '#2f62c9';
      c.lineWidth = Math.max(1.2, r * 0.13);
      c.beginPath();
      c.moveTo(-r * 0.78, r * 0.12);
      c.bezierCurveTo(-r * 0.35, -r * 0.25, -r * 0.1, r * 0.45, r * 0.25, r * 0.08);
      c.bezierCurveTo(r * 0.45, -r * 0.12, r * 0.65, r * 0.1, r * 0.82, r * 0.02);
      c.stroke();
      c.fillStyle = '#d8342c';
      for (const [x, y] of [[-r * 0.4, r * 0.45], [r * 0.05, -r * 0.18], [r * 0.48, r * 0.4]]) {
        c.beginPath();
        c.arc(x, y, r * 0.13, 0, Math.PI * 2);
        c.fill();
      }
      shine(c, r, 0.35);
    } else if (kind === 2) {
      const g = c.createLinearGradient(-r, -r, r, r);
      g.addColorStop(0, '#fff2a8');
      g.addColorStop(0.45, '#f2c230');
      g.addColorStop(1, '#b8860b');
      pot(c, r, g, '#8a6200', '#e0ad1d');
      shine(c, r, 0.55);
      c.fillStyle = 'rgba(255,255,255,.95)';
      star(c, r * 0.6, -r * 0.6, r * 0.32 * (0.6 + 0.4 * Math.abs(Math.sin(ph / 160))));
    } else if (kind === 3) {
      if (nik) {
        // різочка: в'язка прутиків з червоною стрічкою
        c.strokeStyle = '#6b4a2b';
        c.lineCap = 'round';
        c.lineWidth = Math.max(1.2, r * 0.13);
        for (let i = -2; i <= 2; i++) {
          c.beginPath();
          c.moveTo(i * r * 0.08, r * 0.95);
          c.quadraticCurveTo(i * r * 0.2, 0, i * r * 0.38, -r * 0.95);
          c.stroke();
        }
        c.fillStyle = '#d8342c';
        c.fillRect(-r * 0.34, r * 0.2, r * 0.68, r * 0.22);
        c.lineCap = 'butt';
        return;
      }
      const fl = 0.75 + 0.25 * Math.sin(ph / 70) * Math.sin(ph / 23);
      const g = c.createRadialGradient(0, 0, r * 0.2, 0, 0, r * 1.5);
      g.addColorStop(0, 'rgba(255,120,30,' + (0.55 * fl) + ')');
      g.addColorStop(1, 'rgba(255,60,0,0)');
      c.fillStyle = g;
      c.beginPath();
      c.arc(0, 0, r * 1.5, 0, Math.PI * 2);
      c.fill();
      c.fillStyle = '#3b1c12';
      c.beginPath();
      c.moveTo(-r * 0.9, r * 0.1);
      c.lineTo(-r * 0.5, -r * 0.7);
      c.lineTo(r * 0.3, -r * 0.82);
      c.lineTo(r * 0.92, -r * 0.1);
      c.lineTo(r * 0.6, r * 0.75);
      c.lineTo(-r * 0.4, r * 0.82);
      c.closePath();
      c.fill();
      c.strokeStyle = 'rgb(255,' + Math.round(140 + 80 * fl) + ',40)';
      c.lineWidth = Math.max(1, r * 0.12);
      c.beginPath();
      c.moveTo(-r * 0.5, -r * 0.3);
      c.lineTo(0, r * 0.05);
      c.lineTo(r * 0.45, -r * 0.4);
      c.moveTo(0, r * 0.05);
      c.lineTo(-r * 0.1, r * 0.6);
      c.stroke();
    } else if (kind === 4) {
      c.fillStyle = '#a8692c';
      c.strokeStyle = '#5e3a17';
      c.lineWidth = Math.max(1, r * 0.08);
      for (const [x, w] of [[-r * 0.42, 0.55], [r * 0.42, 0.55], [0, 0.62]]) {
        c.beginPath();
        c.ellipse(x, r * 0.08, r * w, r * 0.78, 0, 0, Math.PI * 2);
        c.fill();
        c.stroke();
      }
      c.fillStyle = '#6f7a2a';
      for (const [x, y, q] of [[-r * 0.5, r * 0.3, 0.2], [r * 0.25, -r * 0.2, 0.16], [r * 0.5, r * 0.45, 0.13]]) {
        c.beginPath();
        c.arc(x, y, r * q, 0, Math.PI * 2);
        c.fill();
      }
      c.fillStyle = '#4a3418';
      c.fillRect(-r * 0.1, -r * 0.92, r * 0.2, r * 0.3);
      // муха кружляє над гнилим
      const a = ph / 180;
      c.fillStyle = '#222';
      c.beginPath();
      c.arc(Math.cos(a) * r * 0.9, -r * 0.95 + Math.sin(a * 1.7) * r * 0.25, Math.max(1, r * 0.09), 0, Math.PI * 2);
      c.fill();
    } else {
      // кіт у мішку: мішок із мішковини, з горловини стирчать вуха
      const tw = Math.sin(ph / 260) > 0.85 ? 0.25 : 0;
      c.fillStyle = '#3e3029';
      c.beginPath();
      c.moveTo(-r * 0.42, -r * 0.5);
      c.lineTo(-r * 0.3 - tw * r, -r * 1.02);
      c.lineTo(-r * 0.08, -r * 0.55);
      c.moveTo(r * 0.42, -r * 0.5);
      c.lineTo(r * 0.3, -r * 1.02);
      c.lineTo(r * 0.08, -r * 0.55);
      c.fill();
      c.fillStyle = '#c9a46b';
      c.strokeStyle = '#7a5a30';
      c.lineWidth = Math.max(1, r * 0.08);
      c.beginPath();
      c.moveTo(-r * 0.45, -r * 0.52);
      c.bezierCurveTo(-r * 1.05, -r * 0.1, -r * 1.0, r * 0.95, 0, r * 0.95);
      c.bezierCurveTo(r * 1.0, r * 0.95, r * 1.05, -r * 0.1, r * 0.45, -r * 0.52);
      c.closePath();
      c.fill();
      c.stroke();
      c.fillStyle = '#7a5a30';
      c.fillRect(-r * 0.48, -r * 0.6, r * 0.96, r * 0.16);
      c.fillStyle = '#5a3e1c';
      c.font = '800 ' + Math.round(r * 1.05) + 'px system-ui, sans-serif';
      c.textAlign = 'center';
      c.textBaseline = 'middle';
      c.fillText('?', 0, r * 0.28);
    }
  }

  /// Як гостинець котиться: глеки й подарунки перевалюються, гарбуз крутиться, мішок підстрибує.
  function giftAt(c, kind, x, y, r, d, nik, ph) {
    c.save();
    c.translate(x, y);
    if (kind === 4) c.rotate(d / 140);
    else if (kind === 5) c.translate(0, -Math.abs(Math.sin(d / 90)) * r * 0.25);
    else if (kind !== 3) c.rotate(Math.sin(d / 110) * 0.38);
    gift(c, kind, r, nik, ph);
    c.restore();
  }

  /// Картинки гостинців для кошика під полем (DOM): малюємо раз на вид і скін.
  const icons = {};
  function iconUrl(kind, nik) {
    const k = kind + (nik ? 'n' : '');
    if (icons[k]) return icons[k];
    const cv = document.createElement('canvas');
    cv.width = cv.height = 56;
    const c = cv.getContext('2d');
    c.translate(28, 30);
    gift(c, kind, 21, nik, 0);
    icons[k] = cv.toDataURL();
    return icons[k];
  }

  // =============================================================================================
  // Стан і геометрія
  // =============================================================================================

  function state(root, ctx) {
    let st = root._hy;
    if (!st) {
      st = root._hy = {
        root, ctx, last: null, lastAt: 0, seen: new Set(), evInit: false, pend: new Map(), shown: new Map(),
        fly: [], fl: [], puffs: [], banner: null, raf: 0, awakeUntil: 0, guardUntil: 0, W: 0, H: 0, dpr: 0,
        bg: null, bgKey: '', disp: [], btnK: '', basketK: '', hudK: '', legendK: '', flakes: null, hotSince: 0,
      };
      live.add(st);
    }
    st.ctx = ctx;
    return st;
  }

  const frameOf = (st) => st.last || (st.ctx.view && st.ctx.view.frame) || null;
  const nik = (st) => !!(st.ctx.view && st.ctx.view.skin === 'nik');
  const phaseOf = (st) => { const f = frameOf(st); return f ? f.ph : 4; };

  function geo(st) {
    const f = frameOf(st);
    const len = (f && f.len) || 3000;
    const W = st.W, H = st.H;
    const y0 = 36, y1 = H - 24;
    const A = Math.max(36, Math.min(150, W / 2 - 58));
    return { len, W, H, y0, y1, cx: W / 2, A, k: (y1 - y0) / len, n: Math.max(1, Math.round(len / SP) - 1) };
  }
  const at = (G, d) => ({ x: G.cx + G.A * Math.sin((Math.PI * d) / SP - Math.PI / 2), y: G.y0 + d * G.k });
  /// Де стоїть станція k: на зовнішньому боці k-го вигину (парні — праворуч, непарні — ліворуч).
  function stationXY(G, k) {
    const p = at(G, (k + 1) * SP);
    const side = k % 2 === 0 ? 1 : -1;
    return { x: p.x + side * 38, y: p.y, side, cx: p.x };
  }
  const giftR = (G) => Math.max(7, Math.min(13, G.k * SP * 0.2));

  /// Скільки станцій: у грі — хто грає, у лобі — хто сів (кадр лобі вже має st за ними).
  function seatsIn(f) {
    const out = [];
    if (f && f.st) f.st.forEach((k, s) => { if (k != null) out.push(s); });
    return out;
  }

  /// Полотно — своє, бо висота залежить від кількості станцій і місця на екрані (міряємо root, не вікно).
  function size(st) {
    const cv = st.cv;
    if (!cv || !cv.isConnected) return false;
    const rw = st.wrap.clientWidth || st.root.clientWidth || 360;
    const W = Math.max(260, Math.min(560, Math.floor(rw)));
    const f = frameOf(st);
    const n = Math.max(2, f && f.len ? Math.round(f.len / SP) - 1 : seatsIn(f).length || 2);
    const fit = HGames.ui.fit ? HGames.ui.fit() : { h: window.innerHeight, top: 0, dock: 0 };
    // Скільки влазить без скролу: від справжнього верху полотна (над ним чипи місць і статус, що буває у 2 рядки)
    // до доку каркаса, мінус кошик із кнопкою «Хапай» під полем — вона мусить бути видна над доком, а не під ним.
    // У лобі кошика ще нема — резервуємо стільки ж, щоб полотно не стрибало на старті.
    const top = Math.max(fit.top, cv.getBoundingClientRect().top + (window.scrollY || 0));
    const mh = st.me && !st.me.hidden ? st.me.offsetHeight : 0;
    const below = (mh || (coarse() ? 104 : 112)) + 14;
    const floor = coarse() ? 220 : 300;
    const avail = Math.max(floor, fit.h - fit.dock - top - below);
    const pref = (coarse() ? 110 : 150) + (coarse() ? 66 : 76) * (n + 1);
    const H = Math.round(Math.max(floor, Math.min(avail, pref, 760)));
    const dpr = Math.min(2.5, window.devicePixelRatio || 1);
    if (W === st.W && H === st.H && dpr === st.dpr) return false;
    st.W = W;
    st.H = H;
    st.dpr = dpr;
    cv.width = Math.round(W * dpr);
    cv.height = Math.round(H * dpr);
    cv.style.width = W + 'px';
    cv.style.height = H + 'px';
    st.bg = null;
    st.flakes = null;
    return true;
  }

  // =============================================================================================
  // Тло: небо, схил, хата нагорі, жолоб, вікна станцій, рів унизу — малюємо раз на розмір
  // =============================================================================================

  function rnd(seed) {
    let s = seed >>> 0 || 1;
    return () => { s ^= s << 13; s ^= s >>> 17; s ^= s << 5; return ((s >>> 0) % 10000) / 10000; };
  }

  function chutePath(c, G, from, to) {
    c.beginPath();
    for (let d = from; d <= to; d += 20) {
      const p = at(G, d);
      if (d === from) c.moveTo(p.x, p.y);
      else c.lineTo(p.x, p.y);
    }
    const e = at(G, to);
    c.lineTo(e.x, e.y);
  }

  function background(st) {
    const G = geo(st);
    const isNik = nik(st);
    const key = [st.W, st.H, st.dpr, G.len, isNik].join();
    if (st.bg && st.bgKey === key) return st.bg;
    const cv = st.bg || document.createElement('canvas');
    cv.width = Math.round(st.W * st.dpr);
    cv.height = Math.round(st.H * st.dpr);
    const c = cv.getContext('2d');
    c.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
    const R = rnd(G.len * 7 + st.W);
    // небо й схил
    const sky = c.createLinearGradient(0, 0, 0, st.H);
    if (isNik) {
      sky.addColorStop(0, '#16233f');
      sky.addColorStop(0.12, '#2a3d63');
      sky.addColorStop(0.16, '#dfe9f3');
      sky.addColorStop(1, '#b7c9da');
    } else {
      sky.addColorStop(0, '#ffd99a');
      sky.addColorStop(0.11, '#f6c27f');
      sky.addColorStop(0.15, '#9fc76f');
      sky.addColorStop(1, '#4f8a45');
    }
    c.fillStyle = sky;
    c.fillRect(0, 0, st.W, st.H);
    // далекі пагорби по обрію
    c.fillStyle = isNik ? 'rgba(255,255,255,.55)' : 'rgba(70,120,60,.45)';
    c.beginPath();
    c.moveTo(0, st.H * 0.16);
    for (let x = 0; x <= st.W; x += 20) c.lineTo(x, st.H * 0.14 - Math.sin(x / 60) * 6 - Math.sin(x / 23) * 2);
    c.lineTo(st.W, st.H * 0.2);
    c.lineTo(0, st.H * 0.2);
    c.fill();
    if (isNik) {
      c.fillStyle = 'rgba(255,255,255,.8)';
      for (let i = 0; i < 26; i++) c.fillRect(R() * st.W, R() * st.H * 0.12, 1.4, 1.4);
    }
    // трава/замети: кущики й квіточки — на схилі під жолобом
    for (let i = 0; i < Math.round(st.W * st.H / 2600); i++) {
      const x = R() * st.W, y = st.H * 0.18 + R() * st.H * 0.82;
      if (isNik) {
        c.fillStyle = 'rgba(255,255,255,.7)';
        c.beginPath();
        c.ellipse(x, y, 6 + R() * 8, 2 + R() * 2, 0, 0, Math.PI * 2);
        c.fill();
      } else {
        c.strokeStyle = 'rgba(40,90,40,.45)';
        c.lineWidth = 1.2;
        c.beginPath();
        c.moveTo(x - 3, y);
        c.lineTo(x - 1, y - 5);
        c.moveTo(x, y);
        c.lineTo(x + 1, y - 6);
        c.moveTo(x + 3, y);
        c.lineTo(x + 4, y - 4);
        c.stroke();
        if (R() < 0.25) {
          c.fillStyle = ['#fff6c9', '#f7a6c4', '#a9c7ff'][Math.floor(R() * 3)];
          c.beginPath();
          c.arc(x + 6, y - 3, 1.8, 0, Math.PI * 2);
          c.fill();
        }
      }
    }
    // рів унизу
    const end = at(G, G.len);
    c.fillStyle = isNik ? 'rgba(90,110,140,.55)' : 'rgba(60,40,20,.55)';
    c.beginPath();
    c.ellipse(end.x, end.y + 8, 30, 10, 0, 0, Math.PI * 2);
    c.fill();
    // жолоб: темний борт, світле дно, рисочка по середині
    c.lineCap = 'round';
    c.lineJoin = 'round';
    chutePath(c, G, 0, G.len);
    c.strokeStyle = 'rgba(0,0,0,.18)';
    c.lineWidth = 26;
    c.stroke();
    c.strokeStyle = isNik ? '#7f6a58' : '#6b4426';
    c.lineWidth = 22;
    c.stroke();
    c.strokeStyle = isNik ? '#b39a83' : '#b98652';
    c.lineWidth = 15;
    c.stroke();
    c.strokeStyle = isNik ? 'rgba(255,255,255,.6)' : 'rgba(255,225,170,.45)';
    c.lineWidth = 2;
    c.stroke();
    if (isNik) {
      // сніг на бортах
      c.strokeStyle = 'rgba(255,255,255,.85)';
      c.lineWidth = 3;
      c.setLineDash([10, 14]);
      chutePath(c, G, 0, G.len);
      c.stroke();
      c.setLineDash([]);
    }
    // хата нагорі — звідти Дядько Глек пускає гостинці
    const top = at(G, 0);
    hut(c, top.x, G.y0 + 4, isNik);
    st.bg = cv;
    st.bgKey = key;
    return cv;
  }

  function hut(c, x, y, isNik) {
    c.save();
    c.translate(x, y);
    c.fillStyle = '#f3ead6';
    c.strokeStyle = '#7a5a3a';
    c.lineWidth = 1.2;
    c.fillRect(-17, -16, 34, 18);
    c.strokeRect(-17, -16, 34, 18);
    c.fillStyle = isNik ? '#eef4fa' : '#c9a24e';
    c.beginPath();
    c.moveTo(-23, -14);
    c.lineTo(0, -32);
    c.lineTo(23, -14);
    c.closePath();
    c.fill();
    c.stroke();
    c.fillStyle = '#5a3a1e';
    c.fillRect(-5, -9, 10, 11);
    c.fillStyle = '#7fb3e0';
    c.fillRect(8, -12, 6, 6);
    c.restore();
  }

  // =============================================================================================
  // Кадри й події
  // =============================================================================================

  /// Події кадру: кожна живе 20 тиків, тож бачимо її кілька разів — зводимо за ключем.
  function events(st, f) {
    if (!f.ev) return;
    const ctx = st.ctx;
    const first = !st.evInit;
    st.evInit = true;
    for (const e of f.ev) {
      const [t, type, s, kind, val, id] = e;
      const key = t + ':' + type + ':' + s + ':' + id;
      if (st.seen.has(key)) continue;
      st.seen.add(key);
      if (first) continue;   // F5 / вхід посеред партії — старі події не програємо
      const mine = ctx.mine && s === ctx.seat;
      const pend = mine ? st.pend.get(id) : null;
      if (type === 1 || type === 3) {
        if (pend) {
          st.pend.delete(id);
          if (kind === 5) floater(st, s, (val > 0 ? '+' + val : sign(val)) + (val >= 5 ? ' 🐈!' : ' 🐈'), val > 0 ? '#9df29d' : '#ff8a7a', 1.25);
        } else {
          if (mine) st.guessMiss = false;   // натиск навмання сервер таки зарахував — підказку «повз» не ковтаємо далі
          const g = st.shown.get(id);
          launch(st, s, kind, g ? g.d : null);
          floater(st, s, type === 3 ? '−3 🔥' : (kind === 5 ? (val > 0 ? '+' + val : sign(val)) + ' 🐈' : sign(val)), val > 0 ? '#9df29d' : '#ff8a7a', mine ? 1.2 : 0.9);
        }
        st.shown.delete(id);
        if (type === 3) burnPuff(st, s);
      } else if (type === 2) {
        if (pend) undo(st, id, 'повз!');
        else if (!mine || !st.guessMiss) floater(st, s, 'повз', '#ffffff', 0.75);
        if (mine) st.guessMiss = false;
      } else if (type === 4) {
        floater(st, s, '🧺 ' + sign(val), val >= 0 ? '#ffe28a' : '#ff8a7a', 1.35, 1600);
      }
    }
    // старі ключі прибираємо, щоб множина не росла
    if (st.seen.size > 400) {
      for (const k of st.seen) if (+k.split(':')[0] < f.t - 60) st.seen.delete(k);
    }
  }

  /// Свій хап, якого кадр так і не підтвердив, — відкочуємо: гостинець знову на жолобі, над тобою «повз!».
  function undo(st, id, why) {
    const p = st.pend.get(id);
    if (!p) return;
    st.pend.delete(id);
    st.fly = st.fly.filter((x) => x.id !== id);
    if (st.ctx.mine) floater(st, st.ctx.seat, why, '#ffb0a0', 1);
  }

  function takeFrame(st, f, now) {
    const prev = st.last;
    events(st, f);
    // гостинці: що зникло не через хап — доїхало до рову
    const ids = new Set();
    for (const g of f.g || []) ids.add(g[0]);
    if (prev && f.ph === 1) {
      for (const [id, g] of st.shown) {
        if (!ids.has(id) && !st.pend.has(id) && g.d > f.len - 400) puff(st, f.len);
        if (!ids.has(id)) st.shown.delete(id);
      }
    }
    if (f.ph !== 1) st.shown.clear();
    // зміна місць: банер «ти тепер …», аватарки з'їжджають на нові станції
    if (prev && st.ctx.mine && prev.st && f.st && prev.st[st.ctx.seat] != null && f.st[st.ctx.seat] != null
      && prev.st[st.ctx.seat] !== f.st[st.ctx.seat] && prev.r === f.r && f.ph === 1) {
      const k = f.st[st.ctx.seat], n = seatsIn(f).length;
      st.banner = { text: '🔄 Зміна! Ти тепер ' + (k === 0 ? 'нагорі' : k === n - 1 ? 'внизу' : (k + 1) + '-й згори'), at: now };
    }
    if (prev && f.ph !== prev.ph && f.ph === 0) st.banner = null;
    // підтвердження: що висить понад 0,7 с без події — не взяли (у кадрі це вже видно)
    for (const [id, p] of st.pend) if (now - p.at > 700) undo(st, id, 'повз!');
    if (f.ph !== 1) st.pend.clear();
    st.last = f;
    st.lastAt = now;
  }

  // =============================================================================================
  // Хап
  // =============================================================================================

  function myState(st) {
    const f = frameOf(st), ctx = st.ctx;
    if (!f || !ctx.mine || !f.p) return null;
    const q = f.p[ctx.seat];
    if (!q || !f.st || f.st[ctx.seat] == null) return null;
    const dt = Math.max(0, performance.now() - st.lastAt);
    const left = (t) => Math.max(0, t * TICK - dt);
    return { count: q[0] + countPend(st), val: q[1], total: q[2], score: scoreOf(f, q), burn: left(q[3]), lock: left(q[4]), k: f.st[ctx.seat] };
  }
  function countPend(st) { let n = 0; for (const p of st.pend.values()) if (p.kind !== 3) n++; return n; }

  /// Гостинці зараз (екстраполяція кадру): [{id, kind, d, mul}], без тих, що я вже «схопив».
  function giftsNow(st, now) {
    const f = frameOf(st);
    if (!f || !f.g) return [];
    const dt = f.ph === 1 ? Math.min(300, Math.max(0, now - st.lastAt)) : 0;
    const out = [];
    for (const [id, kind, d, mul] of f.g) {
      if (st.pend.has(id)) continue;
      const want = d + ((f.v * mul) / 100) * (dt / 1000);
      const s = st.shown.get(id);
      // кадр запізнився — не смикаємо гостинець назад, лише доганяємо
      const shown = s && want < s.d && s.d - want < 300 ? s.d : want;
      if (s) { s.d = shown; s.kind = kind; } else st.shown.set(id, { d: shown, kind });
      out.push({ id, kind, d: shown, mul });
    }
    return out;
  }

  /// Що зараз у моєму вікні (найнижче) — те й хапатимемо.
  function target(st, now) {
    const me = myState(st), f = frameOf(st);
    if (!me || !f || f.ph !== 1) return null;
    const c = (me.k + 1) * SP;
    let best = null;
    for (const g of giftsNow(st, now)) {
      const slack = ((f.v * g.mul) / 100) * 0.05;
      if (g.d >= c - HW && g.d <= c + HW + slack && (!best || g.d > best.d)) best = g;
    }
    return best;
  }

  function grab(st) {
    const ctx = st.ctx, f = frameOf(st);
    if (!ctx.mine || !ctx.playing || !f) return;
    const now = performance.now();
    if (f.ph !== 1) { if (f.ph === 0) floater(st, ctx.seat, 'ще не котиться', '#fff', 0.8); return; }
    const me = myState(st);
    if (!me) return;
    if (now < st.guardUntil) return;
    if (me.burn > 0) { floater(st, ctx.seat, 'пече! 🔥', '#ffb0a0', 0.9); st.guardUntil = now + 250; return; }
    if (me.lock > 0) return;
    if (me.count >= BASKET) { floater(st, ctx.seat, 'кошик повний', '#ffe28a', 0.9); st.guardUntil = now + 400; return; }
    const g = target(st, now);
    st.guardUntil = now + 160;
    if (!g) {
      // натиск навмання: сервер дасть промах і 0,4 с без рук — показуємо це одразу
      // id 0 — свідомий промах: без id сервер узяв би найнижчий у вікні, а кадр відстає — і «повз» обернувся б жаром
      ctx.input('grab', { id: 0 });
      st.guessMiss = true;
      floater(st, ctx.seat, 'повз', '#ffffff', 0.8);
      st.guardUntil = now + 400;
      return;
    }
    ctx.input('grab', { id: g.id });
    st.pend.set(g.id, { at: now, kind: g.kind });
    launch(st, ctx.seat, g.kind, g.d, g.id);
    st.shown.delete(g.id);
    if (g.kind === 3) { floater(st, ctx.seat, '−3 🔥', '#ff8a7a', 1.2); burnPuff(st, ctx.seat); }
    else if (g.kind === 5) floater(st, ctx.seat, '🐈 ?', '#ffe28a', 1);
    else floater(st, ctx.seat, sign(VAL[g.kind]), VAL[g.kind] > 0 ? '#9df29d' : '#ff8a7a', 1.2);
    if (navigator.vibrate && coarse() && navigator.userActivation && navigator.userActivation.hasBeenActive) try { navigator.vibrate(g.kind === 3 ? 60 : 15); } catch { /* без вібро */ }
    spin(st);
  }

  // =============================================================================================
  // Ефекти
  // =============================================================================================

  function seatPos(st, s) {
    const f = frameOf(st);
    const k = f && f.st ? f.st[s] : null;
    if (k == null || !st.W) return null;
    const G = geo(st);
    const d = st.disp[s];
    if (d && d.x != null) return { x: d.x, y: d.y };
    return stationXY(G, k);
  }

  function launch(st, s, kind, d, id) {
    const to = seatPos(st, s);
    if (!to || !st.W) return;
    const G = geo(st), f = frameOf(st);
    const k = f && f.st ? f.st[s] : 0;
    const from = at(G, d != null ? d : (k + 1) * SP);
    st.fly.push({ id, kind, x0: from.x, y0: from.y, s, at: performance.now(), dur: reduced() ? 120 : 360 });
  }

  function floater(st, s, text, color, scale, dur) {
    st.fl.push({ s, text, color, scale: scale || 1, at: performance.now(), dur: dur || 1000 });
    if (st.fl.length > 24) st.fl.shift();
    spin(st);
  }

  function puff(st, d) {
    st.puffs.push({ d, at: performance.now(), kind: 'dust' });
    if (st.puffs.length > 12) st.puffs.shift();
  }
  function burnPuff(st, s) {
    st.puffs.push({ s, at: performance.now(), kind: 'smoke' });
  }

  // =============================================================================================
  // Малювання
  // =============================================================================================

  function text(c, s, x, y, size, color, weight, align) {
    c.font = (weight || 700) + ' ' + size + 'px system-ui, -apple-system, "Segoe UI", sans-serif';
    c.textAlign = align || 'center';
    c.textBaseline = 'middle';
    c.lineWidth = Math.max(2.5, size / 4);
    c.strokeStyle = 'rgba(20,14,8,.78)';
    c.lineJoin = 'round';
    c.strokeText(s, x, y);
    c.fillStyle = color;
    c.fillText(s, x, y);
  }

  function draw(st, now) {
    const cv = st.cv;
    if (!cv || !st.W) return;
    const c = st.g2d;
    const f = frameOf(st);
    const ctx = st.ctx;
    const G = geo(st);
    c.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
    c.drawImage(background(st), 0, 0, st.W, st.H);
    const isNik = nik(st);
    const ph = f ? f.ph : 4;
    const seats = seatsIn(f);
    const v = ctx.view || {};
    const left = v.left || [];
    const pulse = 0.5 + 0.5 * Math.sin(now / 160);
    const gifts = giftsNow(st, now);
    const R = giftR(G);

    // вікна станцій кольором того, хто там стоїть; моє — яскравіше й дихає
    for (const s of seats) {
      const k = f.st[s];
      const cc = (k + 1) * SP;
      const mine = ctx.mine && s === ctx.seat;
      chutePath(c, G, cc - HW, cc + HW);
      c.lineCap = 'butt';
      c.strokeStyle = SEAT[s];
      c.globalAlpha = left[s] ? 0.15 : mine ? 0.45 + 0.25 * pulse : 0.32;
      c.lineWidth = mine ? 24 : 20;
      c.stroke();
      c.globalAlpha = 1;
      for (const e of [cc - HW, cc + HW]) {
        const p = at(G, e);
        c.fillStyle = 'rgba(255,255,255,.75)';
        c.fillRect(p.x - 12, p.y - 1, 24, 2);
      }
    }

    // гостинці
    for (const g of gifts) {
      const p = at(G, g.d);
      // щойно з хати — проступає з дверей, а не висить над дахом
      c.globalAlpha = Math.max(0, Math.min(1, (g.d - 40) / 160));
      giftAt(c, g.kind, p.x, p.y, R * (g.kind === 1 ? 1.08 : 1), g.d, isNik, now + g.id * 97);
    }
    c.globalAlpha = 1;
    st.inWin = null;
    if (ctx.mine && ph === 1) {
      const t = target(st, now);
      st.inWin = t;
      if (t) {
        const p = at(G, t.d);
        c.strokeStyle = good(t.kind) ? 'rgba(160,255,160,.95)' : t.kind === 5 ? 'rgba(255,226,138,.95)' : 'rgba(255,110,90,.95)';
        c.lineWidth = 2.5;
        c.beginPath();
        c.globalAlpha = 1;
        c.arc(p.x, p.y, R * 1.45 + pulse * 2, 0, Math.PI * 2);
        c.stroke();
      }
    }

    // гравці
    for (const s of seats) drawSeat(st, c, G, s, now, pulse);

    // гостинці, що летять у кошик
    st.fly = st.fly.filter((x) => now - x.at < x.dur);
    for (const x of st.fly) {
      const to = seatPos(st, x.s);
      if (!to) continue;
      const t = Math.min(1, (now - x.at) / x.dur);
      const e = 1 - (1 - t) * (1 - t);
      const px = x.x0 + (to.x - x.x0) * e, py = x.y0 + (to.y - x.y0) * e - Math.sin(Math.PI * t) * 26;
      c.save();
      c.translate(px, py);
      c.rotate(t * 2.4);
      c.scale(1 - 0.4 * t, 1 - 0.4 * t);
      gift(c, x.kind, R * 1.1, isNik, now);
      c.restore();
    }

    // пил біля рову, дим від жару
    st.puffs = st.puffs.filter((p) => now - p.at < 700);
    for (const p of st.puffs) {
      const t = (now - p.at) / 700;
      let x, y;
      if (p.kind === 'dust') { const e = at(G, G.len); x = e.x; y = e.y + 6; } else {
        const sp = seatPos(st, p.s);
        if (!sp) continue;
        x = sp.x; y = sp.y - 10;
      }
      c.fillStyle = p.kind === 'dust' ? 'rgba(120,90,60,' + (0.5 * (1 - t)) + ')' : 'rgba(70,70,70,' + (0.55 * (1 - t)) + ')';
      for (let i = 0; i < 4; i++) {
        c.beginPath();
        c.arc(x + (i - 1.5) * 7 * (1 + t), y - t * (p.kind === 'dust' ? 8 : 26) - i % 2 * 4, 4 + t * 7, 0, Math.PI * 2);
        c.fill();
      }
    }

    // сніг у Миколаїв день
    if (isNik) snow(st, c, now);

    // спливні написи над гравцями
    st.fl = st.fl.filter((x) => now - x.at < x.dur);
    for (const x of st.fl) {
      const sp = seatPos(st, x.s);
      if (!sp) continue;
      const t = (now - x.at) / x.dur;
      c.globalAlpha = t > 0.7 ? (1 - t) / 0.3 : 1;
      const xx = Math.max(30, Math.min(st.W - 30, sp.x - stationSide(st, x.s) * 26));
      text(c, x.text, xx, sp.y - 18 - t * 26, Math.round(14 * x.scale), x.color, 800);
      c.globalAlpha = 1;
    }

    overlay(st, c, G, f, now);
  }

  function stationSide(st, s) {
    const f = frameOf(st);
    const k = f && f.st ? f.st[s] : 0;
    return k % 2 === 0 ? 1 : -1;
  }

  function drawSeat(st, c, G, s, now, pulse) {
    const f = frameOf(st), ctx = st.ctx;
    const k = f.st[s];
    const home = stationXY(G, k);
    // аватарка з'їжджає на нову станцію, а не стрибає
    let d = st.disp[s];
    if (!d || d.k == null) d = st.disp[s] = { x: home.x, y: home.y, k };
    if (d.k !== k) { d.k = k; d.fromX = d.x; d.fromY = d.y; d.at = now; }
    if (d.at != null) {
      const t = Math.min(1, (now - d.at) / (reduced() ? 1 : 520));
      const e = t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
      d.x = d.fromX + (home.x - d.fromX) * e;
      d.y = d.fromY + (home.y - d.fromY) * e - Math.sin(Math.PI * t) * 14;
      if (t >= 1) d.at = null;
    } else { d.x = home.x; d.y = home.y; }
    const x = d.x, y = d.y;
    const v = ctx.view || {};
    const gone = v.left && v.left[s];
    const mine = ctx.mine && s === ctx.seat;
    const q = f.p ? f.p[s] : null;
    const burn = q ? q[3] : 0, lock = q ? q[4] : 0;
    const r = 12;
    c.globalAlpha = gone ? 0.35 : 1;
    // руки до жолоба
    c.strokeStyle = SEAT[s];
    c.lineWidth = 3;
    c.beginPath();
    c.moveTo(x, y);
    c.lineTo(home.cx + home.side * 9, y);
    c.stroke();
    if (mine) {
      c.fillStyle = 'rgba(255,255,255,' + (0.25 + 0.25 * pulse) + ')';
      c.beginPath();
      c.arc(x, y, r + 7, 0, Math.PI * 2);
      c.fill();
    }
    if (burn > 0) {
      c.fillStyle = 'rgba(255,80,30,' + (0.35 + 0.3 * pulse) + ')';
      c.beginPath();
      c.arc(x, y, r + 6, 0, Math.PI * 2);
      c.fill();
    }
    c.fillStyle = SEAT[s];
    c.strokeStyle = mine ? '#fff' : 'rgba(20,14,8,.7)';
    c.lineWidth = mine ? 3 : 1.5;
    c.beginPath();
    c.arc(x, y, r, 0, Math.PI * 2);
    c.fill();
    c.stroke();
    // очі: дивляться на жолоб; обпікся — мружиться
    c.fillStyle = '#1b130c';
    const ex = -home.side * 2.5;
    if (burn > 0) {
      c.fillRect(x + ex - 5, y - 3, 4, 1.6);
      c.fillRect(x + ex + 1, y - 3, 4, 1.6);
    } else {
      c.beginPath();
      c.arc(x + ex - 3, y - 2.5, 1.7, 0, Math.PI * 2);
      c.arc(x + ex + 3, y - 2.5, 1.7, 0, Math.PI * 2);
      c.fill();
    }
    c.strokeStyle = '#1b130c';
    c.lineWidth = 1.3;
    c.beginPath();
    if (burn > 0) c.arc(x + ex, y + 6, 3, Math.PI * 1.15, Math.PI * 1.85);
    else c.arc(x + ex, y + 2.5, 3.2, Math.PI * 0.15, Math.PI * 0.85);
    c.stroke();
    // руки зайняті — сірий сектор, що тане
    if (lock > 0 && burn <= 0) {
      c.strokeStyle = 'rgba(255,255,255,.85)';
      c.lineWidth = 2.5;
      c.beginPath();
      c.arc(x, y, r + 3, -Math.PI / 2, -Math.PI / 2 + Math.PI * 2 * Math.min(1, lock / 8));
      c.stroke();
    }
    // ім'я з рахунком угорі, кошик знизу
    const score = q ? scoreOf(f, q) : (v.totals && v.totals[s]) || 0;
    const name = mine ? 'ти' : short(String(nameAt(ctx, s)).replace(/^🤖 бот /, '🤖 '), 10);
    const label = name + ' · ' + score;
    c.font = '700 11px system-ui, sans-serif';
    const w = c.measureText(label).width;
    const lx = Math.max(w / 2 + 3, Math.min(st.W - w / 2 - 3, x));
    text(c, label, lx, y - r - 8, mine ? 12 : 11, mine ? '#fff' : SEAT[s], 800);
    if (q) {
      const n = q[0];
      const bx = Math.max(18, Math.min(st.W - 18, x)) - 15;
      for (let i = 0; i < BASKET; i++) {
        c.fillStyle = i < n ? (n >= BASKET ? '#ffcf5a' : '#fff') : 'rgba(0,0,0,.35)';
        c.fillRect(bx + i * 4, y + r + 5, 3, 4);
      }
    }
    if (gone) text(c, 'пішов', x, y + r + 8, 10, '#fff', 600);
    c.globalAlpha = 1;
  }

  function snow(st, c, now) {
    if (!st.flakes) {
      const R = rnd(st.W * 3 + st.H);
      st.flakes = Array.from({ length: Math.round(st.W * st.H / 6000) }, () => ({ x: R() * st.W, y: R() * st.H, s: 0.6 + R() * 1.6, v: 12 + R() * 22 }));
    }
    c.fillStyle = 'rgba(255,255,255,.85)';
    const dt = st.snowAt ? Math.min(100, now - st.snowAt) / 1000 : 0;
    st.snowAt = now;
    for (const fl of st.flakes) {
      fl.y += fl.v * dt;
      fl.x += Math.sin((now / 900) + fl.v) * 6 * dt;
      if (fl.y > st.H) { fl.y = -4; }
      c.beginPath();
      c.arc(fl.x, fl.y, fl.s, 0, Math.PI * 2);
      c.fill();
    }
  }

  function panel(c, st, h) {
    const w = Math.min(st.W - 24, 320), x = (st.W - w) / 2, y = Math.max(40, (st.H - h) / 2);
    c.fillStyle = 'rgba(25,18,10,.78)';
    c.beginPath();
    if (c.roundRect) c.roundRect(x, y, w, h, 12);
    else c.rect(x, y, w, h);
    c.fill();
    return { x, y, w };
  }

  /// Відлік, підсумок раунду, кінець партії, банер зміни — поверх поля.
  function overlay(st, c, G, f, now) {
    const ctx = st.ctx, v = ctx.view || {};
    if (!f) return;
    const dt = now - st.lastAt;
    if (f.ph === 0) {
      const sec = Math.max(1, Math.ceil((f.left * TICK - dt) / 1000));
      const p = panel(c, st, 112);
      text(c, (v.party ? 'Гостинці' : 'Раунд ' + (f.r || 1) + ' з ' + (v.rounds || 3)), st.W / 2, p.y + 20, 15, '#ffe9b8', 700);
      text(c, String(sec), st.W / 2, p.y + 58, 42, '#fff', 900);
      text(c, ctx.mine ? 'Хапай, коли гостинець у твоєму вікні' : 'Зараз покотиться', st.W / 2, p.y + 94, 12, '#ffe9b8', 600);
    } else if (f.ph === 2 || (f.ph === 3 && !v.party)) {
      const over = f.ph === 3;
      const rows = seatsIn(f).map((s) => ({
        s,
        val: over ? (v.totals && v.totals[s]) || 0 : ((v.sums && v.sums[s]) || [])[(f.r || 1) - 1] || 0,
        tot: (v.totals && v.totals[s]) || 0,
      }));
      if (!rows.length) return;
      rows.sort((a, b) => b.val - a.val || b.tot - a.tot);
      const h = 44 + rows.length * 22;
      const p = panel(c, st, h);
      text(c, over ? '🏁 Партію зіграно' : '🧺 Раунд ' + f.r + ' — кошики', st.W / 2, p.y + 20, 15, '#ffe9b8', 800);
      rows.forEach((r, i) => {
        const y = p.y + 46 + i * 22;
        const medal = over && r.val === rows[0].val ? '🏆 ' : '';
        const nm = ctx.mine && r.s === ctx.seat ? 'ти' : short(String(nameAt(ctx, r.s)), 14);
        text(c, medal + nm, p.x + 16, y, 13, SEAT[r.s], 700, 'left');
        text(c, over ? String(r.val) : sign(r.val) + '  (' + r.tot + ')', p.x + p.w - 16, y, 13, '#fff', 800, 'right');
      });
    }
    if (st.banner) {
      const t = (now - st.banner.at) / 1600;
      if (t >= 1) st.banner = null;
      else {
        c.globalAlpha = t > 0.75 ? (1 - t) / 0.25 : 1;
        const y = G.y0 + 14;
        c.fillStyle = 'rgba(25,18,10,.75)';
        c.fillRect(0, y - 14, st.W, 28);
        text(c, st.banner.text, st.W / 2, y, 14, '#fff', 800);
        c.globalAlpha = 1;
      }
    }
  }

  // =============================================================================================
  // DOM: рядок над полем, мій кошик, кнопка «Хапай», пам'ятка гостинців
  // =============================================================================================

  function shell(root, st) {
    if (st.el && st.el.isConnected) return;
    root.insertAdjacentHTML('beforeend', '<div class="hy"><div class="hyhud"></div><div class="hywrap"><canvas class="hycv"></canvas></div>'
      + '<div class="hyme" hidden><div class="hybasket"></div><button type="button" class="hygrab"><b>Хапай</b><small></small></button></div>'
      + '<div class="hylegend"></div></div>');
    st.el = root.lastChild;
    st.hud = st.el.querySelector('.hyhud');
    st.wrap = st.el.querySelector('.hywrap');
    st.cv = st.el.querySelector('.hycv');
    st.g2d = st.cv.getContext('2d');
    st.me = st.el.querySelector('.hyme');
    st.basket = st.el.querySelector('.hybasket');
    st.btn = st.el.querySelector('.hygrab');
    st.legend = st.el.querySelector('.hylegend');
    const press = (e) => {
      if (!HGames.ui.human(e)) return;
      e.preventDefault();
      grab(st);
    };
    st.btn.addEventListener('pointerdown', press);
    st.btn.addEventListener('click', (e) => e.preventDefault());
    // на полі теж: тап будь-де — хап (на телефоні палець і так над полем)
    st.cv.addEventListener('pointerdown', (e) => {
      if (!st.ctx.mine || !st.ctx.playing) return;
      press(e);
    });
  }

  function hud(st, now) {
    const ctx = st.ctx, v = ctx.view || {}, f = frameOf(st);
    const parts = [];
    if (f && f.ph <= 2 && v.phase !== 'lobby') {
      if (!v.party) parts.push('<span class="hychip">Раунд <b>' + (f.r || 1) + '</b>/' + (v.rounds || 3) + '</span>');
      if (f.ph === 1) {
        const s = Math.max(0, Math.ceil((f.left * TICK - (now - st.lastAt)) / 1000));
        parts.push('<span class="hychip' + (s <= 5 ? ' hot' : '') + '">⏱ ' + Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0') + '</span>');
        if (f.sl > 0) {
          const z = Math.ceil((f.sl * TICK - (now - st.lastAt)) / 1000);
          if (z <= 3 && z > 0) parts.push('<span class="hychip hot">🔄 зміна за ' + z + '</span>');
        }
      }
      const me = myState(st);
      if (me) parts.push('<span class="hychip mine">ти: <b>' + me.score + '</b></span>');
    }
    const k = parts.join('');
    if (k !== st.hudK) { st.hudK = k; st.hud.innerHTML = k; }
  }

  function basketDom(st) {
    const ctx = st.ctx, f = frameOf(st), isNik = nik(st);
    const show = !!(ctx.mine && f && f.ph <= 2 && ctx.view && ctx.view.phase !== 'lobby' && f.p && f.p[ctx.seat]);
    st.me.hidden = !show;
    st.el.classList.toggle('hyplay', show);
    if (!show) return;
    const items = [];
    const b = (f.b && f.b[ctx.seat]) || [];
    for (let i = 0; i < b.length; i += 2) items.push([b[i], b[i + 1], false]);
    for (const p of st.pend.values()) if (p.kind !== 3) items.push([p.kind, null, true]);
    const q = f.p[ctx.seat];
    const key = items.map((x) => x.join('/')).join(',') + '|' + q[1] + '|' + isNik;
    if (key === st.basketK) return;
    st.basketK = key;
    let h = '';
    for (let i = 0; i < BASKET; i++) {
      const it = items[i];
      if (!it) { h += '<span class="hyslot"></span>'; continue; }
      const val = it[1] == null ? (it[0] === 5 ? '?' : sign(VAL[it[0]])) : sign(it[1]);
      h += '<span class="hyslot on' + (it[2] ? ' wait' : '') + '" title="' + kname(it[0], isNik) + '"><img alt="" src="' + iconUrl(it[0], isNik) + '"><i class="'
        + (it[1] != null && it[1] < 0 ? 'neg' : '') + '">' + val + '</i></span>';
    }
    h += '<span class="hysum" title="у кошику">🧺 <b>' + sign(q[1]) + '</b></span>';
    st.basket.innerHTML = h;
  }

  /// Кнопка «Хапай» підказує, що зараз у вікні: зелена — бери, червона — жар чи гарбуз, жовта — кіт.
  function button(st) {
    const me = myState(st), f = frameOf(st);
    if (!me || !f) return;
    let cls = '', sub = coarse() ? '' : 'пробіл';
    if (f.ph === 0) { cls = 'wait'; sub = 'зараз покотиться'; } else if (f.ph !== 1) { cls = 'wait'; sub = 'раунд скінчився'; } else if (me.burn > 0) { cls = 'burn'; sub = '🔥 пече — ще ' + (me.burn / 1000).toFixed(1) + ' с'; } else if (me.count >= BASKET) { cls = 'full'; sub = 'кошик повний — дивись, що хапають інші'; } else if (st.inWin) {
      const k = st.inWin.kind;
      cls = good(k) ? 'go' : k === 5 ? 'cat' : 'bad';
      sub = kname(k, nik(st)) + ' ' + (k === 5 ? '−3…+5' : sign(VAL[k]));
    } else if (me.lock > 0) cls = 'lock';
    const key = cls + '|' + sub;
    if (key === st.btnK) return;
    st.btnK = key;
    st.btn.className = 'hygrab' + (cls ? ' ' + cls : '');
    st.btn.querySelector('small').textContent = sub;
  }

  function legend(st) {
    const isNik = nik(st);
    const ph = phaseOf(st);
    const brief = ph === 1 || ph === 2;
    const key = isNik + '|' + brief;
    if (key === st.legendK) return;
    st.legendK = key;
    const vals = ['+1', '+3', '+5', '−3', '−1', '−3…+5'];
    let h = '';
    for (let k = 0; k < 6; k++) {
      h += '<span class="hyleg' + (good(k) ? ' g' : k === 5 ? ' c' : ' b') + '"><img alt="" src="' + iconUrl(k, isNik) + '">'
        + (brief ? '' : '<em>' + kname(k, isNik) + '</em>') + '<b>' + vals[k] + '</b></span>';
    }
    if (!brief) {
      h += '<p class="hytip">Гостинці котяться повз усіх згори вниз. Тисни <b>«Хапай»</b>, коли гостинець у <b>твоєму вікні</b> на жолобі.'
        + ' Кошик — на 8: дрібноту пропускай. Жар пече 2 с. Місця щоразу міняються — кожен побуде нагорі.</p>';
    }
    st.legend.innerHTML = h;
    st.legend.classList.toggle('brief', brief);
  }

  // =============================================================================================
  // Цикл
  // =============================================================================================

  /// Живе, поки котиться жолоб і ще трохи після (догорають написи й польоти); у лобі й на кінці спить.
  function spin(st) {
    st.awakeUntil = performance.now() + 1500;
    if (st.raf) return;
    const loop = () => {
      st.raf = 0;
      if (!st.cv || !st.cv.isConnected) return;
      const now = performance.now();
      const ph = phaseOf(st);
      const busy = st.ctx.playing && ph <= 2;
      if (!busy && now > st.awakeUntil) { draw(st, now); return; }
      st.raf = requestAnimationFrame(loop);
      if (!st.cv.offsetParent || document.hidden) return;
      if (now - (st.fitAt || 0) > 500) { st.fitAt = now; size(st); }
      // застарілі передбачення — кадрів нема (обрив), а хап висить
      for (const [id, p] of st.pend) if (now - p.at > 1200) undo(st, id, 'повз!');
      draw(st, now);
      hud(st, now);
      button(st);
      basketDom(st);
    };
    st.raf = requestAnimationFrame(loop);
  }

  function refresh(st) {
    size(st);
    legend(st);
    hud(st, performance.now());
    basketDom(st);
    button(st);
    draw(st, performance.now());
    spin(st);
  }

  function statusText(ctx) {
    const st = [...live].find((s) => s.ctx === ctx);
    const v = ctx.view || {};
    const f = (st && frameOf(st)) || v.frame;
    if (!ctx.playing) {
      if (ctx.room && ctx.room.status === 'lobby') {
        const host = ctx.room.host && ctx.me && String(ctx.room.host).toLowerCase() === String(ctx.me.nick).toLowerCase();
        return host ? 'Тисни «Почати», коли всі сіли (1–8; самому — «🤖 + бот»)' : 'Спільний жолоб на 1–8. Стартує господар';
      }
      return '';
    }
    if (!f) return '';
    if (f.ph === 0) return 'Готуйсь… ' + (v.party ? '' : 'раунд ' + (f.r || 1) + ' з ' + (v.rounds || 3));
    if (f.ph === 2) return 'Раунд ' + f.r + ' — кошики висипано в рахунок';
    if (!ctx.mine) return 'Дивишся збоку' + (v.party ? '' : ' · раунд ' + (f.r || 1) + ' з ' + (v.rounds || 3));
    const me = st && myState(st);
    if (me && me.burn > 0) return '🔥 Пече! Ще мить без рук';
    if (me && me.count >= BASKET) return '🧺 Кошик повний — чекай кінця раунду';
    const how = coarse() ? 'Тисни «Хапай»' : 'Пробіл чи «Хапай»';
    return how + ', коли гостинець у твоєму вікні · кошик ' + (me ? me.count : 0) + '/8';
  }

  HGames.register({
    id: 'hostyntsi',
    added: '2026-10-06',
    icon: ICON,
    seatNames: ['синій', 'рудий', 'зелений', 'жовтий', 'бузковий', 'м’ятний', 'рожевий', 'сірий'],
    seatClass: ['hy0', 'hy1', 'hy2', 'hy3', 'hy4', 'hy5', 'hy6', 'hy7'],
    pad: { a: 'Space', anyBtn: true, hint: '{a} хапай' },

    mount(root, ctx) {
      const st = state(root, ctx);
      shell(root, st);
      if (ctx.view && ctx.view.frame) takeFrame(st, ctx.view.frame, performance.now());
      if (window.ResizeObserver) {
        st.ro = new ResizeObserver(() => { if (size(st)) refresh(st); });
        st.ro.observe(st.wrap);
      }
      if (HGames.ui.onFit) HGames.ui.onFit(root, () => { if (size(st)) refresh(st); });
      refresh(st);
    },

    update(root, ctx) {
      const st = state(root, ctx);
      shell(root, st);
      const vf = ctx.view && ctx.view.frame;
      // вид — правда поза грою (лобі, кінець, F5); посеред гри свіжіші кадри, але новий раунд чи фаза — з виду
      if (vf && (!st.last || !ctx.playing || vf.ph !== st.last.ph || vf.t >= st.last.t)) takeFrame(st, vf, performance.now());
      if (ctx.view && ctx.view.phase === 'lobby') { st.pend.clear(); st.fly = []; st.fl = []; st.disp = []; st.evInit = false; }
      refresh(st);
    },

    frame(root, ctx, f) {
      const st = state(root, ctx);
      if (!f) return;
      if (st.last && f.t < st.last.t && f.ph === st.last.ph && f.r === st.last.r) return;   // запізнілий кадр
      takeFrame(st, f, performance.now());
      spin(st);
    },

    onKey(e, ctx) {
      if (!ctx.mine || !ctx.playing) return false;
      if (e.code !== 'Space' && e.code !== 'ArrowDown' && e.code !== 'KeyS') return false;
      const st = [...live].find((s) => s.ctx === ctx);
      if (!st) return false;
      if (!e.repeat) grab(st);
      return true;
    },

    status: statusText,

    unmount(root) {
      const st = root._hy;
      if (!st) return;
      cancelAnimationFrame(st.raf);
      st.raf = 0;
      if (st.ro) st.ro.disconnect();
      if (HGames.ui.onFit) HGames.ui.onFit(root, null);
      if (st.el) st.el.remove();
      live.delete(st);
      root._hy = null;
    },
  });
})();
