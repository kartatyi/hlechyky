// fit-audit: заміри «як гра лягає на екран» (проєкт «Світлиця», 31.10.2026).
//
// Два способи запуску:
//  1) tools/fitcheck/fitcheck.py (рекомендовано): вантажить цей файл у сторінку, далі кличе
//     window.__fit.open/measure/full/close на кожному екрані й порівнює з базою (див. шапку fitcheck.py).
//  2) Напряму з cdp2.py --js: міряє те, що зараз на екрані (стіл уже відкритий), або, якщо задано
//     localStorage['fit.game'] = '<id>', сам відкриває цю гру, міряє і прибирає за собою:
//       cdp2.py --port 9703 --url "http://127.0.0.1:8700/?k=dev#games" --eval "localStorage['fit.game']='chess'"
//       cdp2.py --port 9703 --js D:/or-wt/_svit/tools/fit/measure.js
//
// Що міряє measure(): переповнення по горизонталі, чи влазить картка/поле у висоту вікна, внутрішні скролбари,
// частку доступної площі, яку займає поле (канвас/дошка/найбільший блок), обрізаний текст, шрифти < 11px,
// дрібні кнопки (< 32px на дотику), канваси, що вперлися в стелю max-width.
(function () {
  if (window.__fit && window.__fit.v === 3) return;
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  // Ніякого звуку: усе, що грає, — німе (Chrome у cdp2 і так --mute-audio).
  try {
    const play = HTMLMediaElement.prototype.play;
    if (!play.__fitMuted) {
      HTMLMediaElement.prototype.play = function () { this.muted = true; this.volume = 0; return play.apply(this, arguments); };
      HTMLMediaElement.prototype.play.__fitMuted = true;
    }
  } catch (e) { /* */ }

  const H = () => window.HGames;
  const vis = (el) => {
    if (!el || !el.getClientRects().length) return false;
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.display !== 'none' && +cs.opacity > 0.05;
  };
  const path = (el) => {
    const out = [];
    for (let e = el, i = 0; e && e !== document.body && i < 4; e = e.parentElement, i++) {
      let s = e.tagName.toLowerCase();
      if (e.id) s += '#' + e.id;
      const cls = [...e.classList].filter((c) => !/^(on|off|x|o|sel|hot|win|turn|mine)$/.test(c)).slice(0, 3);
      if (cls.length) s += '.' + cls.join('.');
      out.unshift(s);
      if (e.id || /^(gtable|gbody)$/.test(e.classList[0] || '')) break;
    }
    return out.join(' > ');
  };
  const R = (r) => r && { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) };
  const txt = (el) => (el.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 40);

  function card() {
    const a = H() && H().active && H().active();
    return (a && a.el) || document.querySelector('.grbox > .gtable:not([hidden])');
  }

  /// Головне поле гри: найбільший видимий канвас/.board/svg у .gbody; інакше найбільший прямий нащадок .gbody.
  function field(body) {
    if (!body) return null;
    let best = null, ba = 0;
    const cands = body.querySelectorAll('canvas, .board, svg, [class*="board"], [class*="field"], [class*="arena"], [class*="stage"], [class*="grid"]');
    for (const el of cands) {
      if (!vis(el)) continue;
      if (el.tagName === 'svg' && el.closest('button')) continue;
      const r = el.getBoundingClientRect();
      const a = r.width * r.height;
      if (r.width < 60 || r.height < 60) continue;
      if (a > ba) { ba = a; best = el; }
    }
    if (!best) {
      for (const el of body.children) {
        if (!vis(el)) continue;
        const r = el.getBoundingClientRect();
        if (r.width * r.height > ba) { ba = r.width * r.height; best = el; }
      }
    }
    return best;
  }

  function measure(label) {
    window.scrollTo(0, 0);
    const de = document.documentElement, b = document.body;
    const vw = window.innerWidth, vh = window.innerHeight;
    const out = { label, vw, vh, at: new Date().toISOString().slice(11, 19), issues: [] };
    out.body = [...b.classList].filter((c) => /^(g-|gfull|chat-collapsed|clk-)/.test(c));
    const a = H() && H().active && H().active();
    if (a && a.ctx) {
      out.game = a.id; out.status = a.ctx.room.status; out.seat = a.ctx.seat;
      out.playing = !!a.ctx.playing;
    }
    const c = card();
    out.hasCard = !!c;
    // сторінка
    out.page = { sw: de.scrollWidth, sh: de.scrollHeight, hOver: Math.max(0, de.scrollWidth - de.clientWidth), vOver: Math.max(0, de.scrollHeight - vh) };
    if (out.page.hOver > 1) out.issues.push('h-overflow:' + out.page.hOver);
    if (!c) { out.issues.push('no-card'); return out; }
    const grbox = c.parentElement, gbody = c.querySelector('.gbody');
    const cr = c.getBoundingClientRect(), br = grbox.getBoundingClientRect();
    out.card = R(cr);
    out.grbox = R(br);
    out.gfitH = parseFloat(getComputedStyle(de).getPropertyValue('--gfit-h')) || null;
    // скільки місця під стіл: від верху картки до низу вікна (мінус нижній док на телефоні)
    const dock = parseFloat(getComputedStyle(de).getPropertyValue('--gdock-h')) || 0;
    const availH = Math.max(1, vh - cr.top - (b.classList.contains('g-imm') ? 0 : dock));
    out.avail = { w: Math.round(br.width), h: Math.round(availH), dock };
    out.cardBelow = Math.max(0, Math.round(cr.bottom - (vh - dock)));
    if (out.cardBelow > 4) out.issues.push('card-below-fold:' + out.cardBelow);
    // поле
    const f = field(gbody);
    if (f) {
      const fr = f.getBoundingClientRect();
      out.field = Object.assign(R(fr), { el: path(f), tag: f.tagName.toLowerCase() });
      out.field.fillW = +(fr.width / br.width).toFixed(2);
      out.field.fillH = +(fr.height / availH).toFixed(2);
      out.field.fill = +Math.max(out.field.fillW, out.field.fillH).toFixed(2);   // ближче до 1 — поле вперлося в межу
      out.field.below = Math.max(0, Math.round(fr.bottom - (vh - dock)));
      out.field.cutRight = Math.max(0, Math.round(fr.right - vw));
      if (out.field.below > 4) out.issues.push('field-below-fold:' + out.field.below);
      if (out.field.cutRight > 1) out.issues.push('field-cut-right:' + out.field.cutRight);
      if (vw >= 1366 && !/^m|^l|^t/.test(label || '') && out.field.fill < 0.6) out.issues.push('field-small:' + out.field.fill);
      if (fr.height > availH * 1.02 && fr.height > 200) out.issues.push('field-taller-than-screen:' + Math.round(fr.height) + '/' + Math.round(availH));
      const mw = getComputedStyle(f).maxWidth;
      out.field.maxW = mw;
      if (mw && mw !== 'none' && /px$/.test(mw) && Math.abs(fr.width - parseFloat(mw)) < 2 && br.width > parseFloat(mw) + 120) out.issues.push('field-capped:' + mw);
    } else out.issues.push('no-field');
    // канваси
    out.canvases = [...c.querySelectorAll('canvas')].filter(vis).slice(0, 4).map((cv) => {
      const r = cv.getBoundingClientRect();
      return { w: Math.round(r.width), h: Math.round(r.height), bw: cv.width, bh: cv.height, maxW: getComputedStyle(cv).maxWidth, cls: cv.className };
    });
    // внутрішні скролбари (уся сторінка столу + сама сторінка)
    const scrolls = [];
    for (const el of [document.scrollingElement, ...document.querySelectorAll('.groom *, #colChat, #colChat *')]) {
      if (!el || !vis(el)) continue;
      const cs = getComputedStyle(el);
      const oy = /auto|scroll/.test(cs.overflowY), ox = /auto|scroll/.test(cs.overflowX);
      if (el === document.scrollingElement) continue;
      const dy = el.scrollHeight - el.clientHeight, dx = el.scrollWidth - el.clientWidth;
      if ((oy && dy > 2 && el.clientHeight > 0) || (ox && dx > 2 && el.clientWidth > 0)) {
        const inGame = !!el.closest('.groom');
        const hidden = cs.scrollbarWidth === 'none';
        scrolls.push({ el: path(el), y: oy && dy > 2 ? dy : 0, x: ox && dx > 2 ? dx : 0, ch: el.clientHeight, cw: el.clientWidth, inGame, thin: hidden ? 'none' : cs.scrollbarWidth });
      }
    }
    out.scrolls = scrolls.slice(0, 12);
    const gs = scrolls.filter((s) => s.inGame);
    if (gs.length) out.issues.push('inner-scroll:' + gs.length);
    // обрізаний текст, дрібні шрифти, дрібні кнопки, вилазить за картку
    const clipped = [], tiny = [], small = [], outside = [];
    const coarse = matchMedia('(pointer: coarse)').matches;
    const minBtn = coarse ? 32 : 24;
    for (const el of c.querySelectorAll('*')) {
      if (!vis(el)) continue;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0) continue;
      const cs = getComputedStyle(el);
      const ownText = [...el.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim());
      if (ownText) {
        const fs = parseFloat(cs.fontSize);
        if (fs < 11 && tiny.length < 40) tiny.push({ el: path(el), fs, t: txt(el) });
        if ((cs.overflow !== 'visible' || cs.textOverflow === 'ellipsis') && el.scrollWidth > el.clientWidth + 1 && el.clientWidth > 0 && clipped.length < 40)
          clipped.push({ el: path(el), sw: el.scrollWidth, cw: el.clientWidth, t: txt(el) });
      }
      if ((el.tagName === 'BUTTON' || el.getAttribute('role') === 'button' || el.tagName === 'SELECT' || (el.tagName === 'INPUT' && el.type !== 'hidden')) && !el.disabled) {
        const kbd = !!el.closest('.gkbd');   // клавіатура: 11 клавіш у рядку на 375 — вужчі за 32 неминуче, межа — 24
        if ((kbd ? (r.width < 24 || r.height < minBtn) : Math.min(r.width, r.height) < minBtn) && small.length < 40 && !el.closest('.board')) small.push({ el: path(el), w: Math.round(r.width), h: Math.round(r.height), t: txt(el) });
      }
      if ((r.right > vw + 1 || r.left < -1) && outside.length < 20 && cs.position !== 'fixed') {
        // рахуємо, лише якщо не обрізано предком з overflow
        let clippedBy = null;
        for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
          if (getComputedStyle(p).overflowX !== 'visible') { clippedBy = p; break; }
        }
        if (!clippedBy || clippedBy.getBoundingClientRect().right > vw + 1) outside.push({ el: path(el), l: Math.round(r.left), r: Math.round(r.right) });
      }
    }
    // клітинки дошки на дотику
    if (coarse) {
      const cell = c.querySelector('.board .cell');
      if (cell) { const r = cell.getBoundingClientRect(); out.cell = Math.round(Math.min(r.width, r.height)); if (out.cell < 32) out.issues.push('cells-small:' + out.cell); }
    }
    out.tinyFonts = tiny.length; out.tinySample = tiny.slice(0, 6);
    out.clipped = clipped.length; out.clippedSample = clipped.slice(0, 6);
    out.smallBtns = small.length; out.smallSample = small.slice(0, 6);
    out.outside = outside.slice(0, 6);
    if (tiny.length) out.issues.push('tiny-fonts:' + tiny.length);
    if (clipped.length) out.issues.push('clipped-text:' + clipped.length);
    if (small.length && coarse) out.issues.push('small-buttons:' + small.length);
    if (outside.length) out.issues.push('outside-viewport:' + outside.length);
    // шапка столу
    const head = document.querySelector('.grhead');
    if (head && vis(head)) out.head = R(head.getBoundingClientRect());
    return out;
  }

  async function waitCard(ms) {
    const t0 = Date.now();
    while (Date.now() - t0 < ms) {
      const a = H() && H().active && H().active();
      const c = card();
      if (a && a.mod && c && c.querySelector('.gbody') && c.querySelector('.gbody').children.length) return true;
      await sleep(150);
    }
    return false;
  }

  async function waitHub() {
    for (let i = 0; i < 80; i++) {
      if (H() && H().catalog && H().catalog.games.length) {
        return true;
      }
      await sleep(200);
    }
    return false;
  }

  const cur = { id: null, game: null };

  /// Відкрити стіл гри: соло — OpenSolo; на кількох — CreateRoom (+ бот, якщо гра пропонує, + «Почати все одно»).
  async function open(gameId) {
    const res = { game: gameId, steps: [] };
    await waitHub();
    const g = ((H().catalog || {}).games || []).find((x) => x.id === gameId);
    if (!g) { res.err = 'нема в каталозі'; return res; }
    if (!/^#games/.test(location.hash)) { location.hash = '#games'; await sleep(600); }
    let r;
    if (g.maxPlayers === 1) {
      r = await H().call('OpenSolo', gameId, null);
      res.steps.push('OpenSolo:' + (r && r.ok));
    } else {
      const opts = {};
      for (const o of g.options || []) if (o.key === 'bots' && (o.values || []).some((v) => v[0] === '1')) opts.bots = '1';
      // fit-core: нік уже сидить за іншим столом — каркас питає модалкою «Встати й поставити» і чекає; тиснемо самі
      const ask = setInterval(() => {
        const b = [...document.querySelectorAll('.gmodal button, .modal button')].find((x) => /Встати й поставити|Все одно встати/.test(x.textContent));
        if (b) b.click();
      }, 200);
      try { r = await H().call('CreateRoom', gameId, opts); } finally { clearInterval(ask); }
      res.steps.push('CreateRoom:' + (r && r.ok) + (r && !r.ok ? ':' + r.message : ''));
    }
    if (!r || !r.ok || !r.roomId) { res.err = (r && r.message) || 'хаб не відповів'; return res; }
    cur.id = r.roomId; cur.game = gameId;
    res.roomId = r.roomId;
    // підписка, як у openRoom: соло хаб підписує сам; на кількох — WatchRoom
    if (g.maxPlayers > 1) { try { await H().call('WatchRoom', r.roomId); } catch (e) { /* */ } }
    location.hash = '#games/room/' + encodeURIComponent(r.roomId);
    res.loaded = await waitCard(12000);
    await sleep(700);
    // суперник-бот і старт
    let a = H().active();
    if (g.maxPlayers > 1 && a && a.ctx) {
      const v = a.ctx.view || {};
      if (v.botOffer && !v.botWanted && a.ctx.room.status !== 'playing') {
        const q = await H().call('Act', r.roomId, 'bot', { on: true });
        res.steps.push('bot:' + (q && q.ok));
        await sleep(900);
      }
      a = H().active();
      if (a && a.ctx.room.status !== 'playing' && g.start === 'byHost') {
        const q = await H().call('StartRoomAnyway', r.roomId);
        res.steps.push('start:' + (q && q.ok) + (q && !q.ok ? ':' + q.message : ''));
        await sleep(1500);
      }
    }
    await waitCard(4000);
    await sleep(1200);
    a = H().active();
    res.status = a && a.ctx ? a.ctx.room.status : null;
    return res;
  }

  // fit-core (31.10): на ПК ⛶ = g-fs (повний екран), ⇔ = gfull («на всю ширину», .grwide); на дотику ⛶ = gfull.
  const isFull = () => { const b = document.body.classList; return b.contains('g-fs') || (b.contains('gfull') && !document.querySelector('.grhead .grwide')); };
  function full(on) {
    const btn = document.querySelector('.grhead .grfull');
    if (!!on !== isFull() && btn) btn.click();
    return isFull();
  }
  function wide(on) {
    const btn = document.querySelector('.grhead .grwide');
    if (btn && !!on !== document.body.classList.contains('gfull')) btn.click();
    return document.body.classList.contains('gfull');
  }

  async function close() {
    full(false);
    wide(false);
    const id = cur.id;
    let ok = null;
    // Як людина: «Закрити»/«Встати» картки — каркас тоді ще й прибирає картку й вид соло (інакше лишаються
    // чипи «інших моїх столів» у рядку столу й зсувають заміри наступних ігор).
    const btn = id && document.querySelector('.gtable[data-room="' + id + '"] [data-do="LeaveRoom"]');
    if (btn) {
      btn.click();
      for (let i = 0; i < 30 && /^#games\/room/.test(location.hash); i++) await sleep(100);
      ok = !/^#games\/room/.test(location.hash) ? true : null;
    }
    if (id && ok !== true) { const r = await H().call('LeaveRoom', id); ok = !!(r && r.ok); }
    cur.id = null;
    location.hash = '#games';
    await sleep(400);
    return { left: ok, id };
  }

  window.__fit = { v: 4, open, measure, full, wide, close, waitCard, waitHub, cur };
})();

// Запуск напряму з cdp2.py --js (див. шапку): тут є return верхнього рівня (cdp2 загортає файл в async-функцію).
if (typeof __fitLib === 'undefined') {
  const g = localStorage.getItem('fit.game');
  if (g) {
    const o = await window.__fit.open(g);
    const m = [window.__fit.measure('here')];
    window.__fit.full(true); await sleep(900); m.push(window.__fit.measure('here-full'));
    const c = await window.__fit.close();
    localStorage.removeItem('fit.game');
    return { open: o, m, close: c };
  }
  return window.__fit.measure('here');
}
