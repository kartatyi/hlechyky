// 🔧 Гараж «Чумаків-далекобійників» (0.0.6, specs/fura.md «Гараж (0.0.6) — клієнт»). Книга — data/fura/garage.json
// (`GET /api/games/fura/garage`, ті самі числа рахує сервер); стан водія — `career` з виду свого місця. Тут: екран гаража
// (вкладки Тягачі · Причепи · Майстерня · Вигляд, тягач на постаменті), смуга ДВ під тахометром, новий рівень (тост і
// Глек), числа фізики у фуру (truck.setParams), вигляд на модель і ріжок у звук, стрілка навігатора.
//
// Купують — дією столу `buy {id}` (ref на сервері ідемпотентний: другий клік не списує), обирають — `pick`; лише між
// рейсами (free) і лише акаунт. Вигляд — речі Лавки «🚚 Фура»: вдягають `POST /api/lavka/wear`, потім `act garage`.
// Керування — свої кнопки (main.js кличе tick() з кадру, поки вікно відкрите): пад LB/RB вкладки, хрестовина вибір,
// Ⓐ тримати 1 с — купити, Ⓑ назад, Ⓨ тест-драйв; кермо — пелюстки вибір, «камера» вкладка, «зчепити» тримати,
// «меню» назад, «сигнал» тест-драйв; клавіатура — G/Esc, стрілки, Enter (тримати), P — тест-драйв.

import { BASE } from './truck.js';
import { buildModels, plateOf } from './model.js';
import { pads, isPad, loadCalib } from './input.js';

const TABS = [['tractors', '🚚 Тягачі'], ['trailers', '🛻 Причепи'], ['tune', '🔧 Майстерня'], ['look', '🎨 Вигляд']];
const HOLD_MS = 1000;
const escH = (t) => String(t == null ? '' : t).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const num = (x, d = 0) => (+x).toFixed(d).replace('.', ',');
const kw = (w) => num(w / 1000) + ' кВт';
const kn = (n) => num(n / 1000, n % 1000 ? 1 : 0) + ' кН';

// ---------------------------------------------------------------- книга
let bookP = null;
export function loadBook() {
  if (!bookP) bookP = fetch('/api/games/fura/garage', { cache: 'no-cache' }).then((r) => (r.ok ? r.json() : null)).catch(() => null);
  return bookP.then((b) => { if (!b) bookP = null; return b; });
}
const levelsOf = (book, br) => br.levels || book.tune.levels;
const total = (book, lvl) => { let t = 0; for (let l = 1; l < lvl; l++) t += book.levels.base + book.levels.step * (l - 1); return t; };

/** Числа фізики, як рахує сервер (FuraBook.Params): тягач, причіп, щаблі тюнінгу, стеля корисності. Для тест-драйву. */
export function paramsFor(book, tractor, trailer, tune) {
  const p = { ...(book.base || BASE), tractor, trailer };
  const put = (t, keys) => { if (t) for (const k of keys) if (t[k] != null) p[k] = k === 'limits' || k === 'hurt' ? p[k] * t[k] : t[k]; };
  put(book.tractors.find((x) => x.id === tractor), ['power', 'fmax', 'mass', 'steer', 'sway', 'limits']);
  put(book.trailers.find((x) => x.id === trailer), ['hurt', 'trailerStiff', 'trailerRelax']);
  for (const b of book.tune.branches) {
    const n = Math.max(0, Math.min(b.steps, (tune && tune[b.id]) || 0));
    if (!n) continue;
    for (const [k, per] of Object.entries(b.mul || {})) p[k] *= 1 + per * Math.min(n, b.mulSteps || b.steps);
    if (b.hurtPer) p.hurt *= Math.pow(b.hurtPer, n);
    for (let s = 1; s <= n; s++) Object.assign(p, (b.set || {})[s] || {});
  }
  if (p.power > book.cap.power) p.power = book.cap.power;
  if (p.hurt < book.cap.hurt) p.hurt = book.cap.hurt;
  if (p.limits > book.cap.limits) p.limits = book.cap.limits;
  return p;
}

/** Що відкриває рівень L: «тягач «Віл»», «Двигун 2», «далекі замовлення», титул. */
export function unlocksAt(book, L) {
  const out = [];
  for (const t of book.tractors) if (t.lvl === L) out.push('тягач «' + t.title + '»');
  for (const t of book.trailers) if (t.lvl === L) out.push('причіп «' + t.title + '»');
  for (const b of book.tune.branches) levelsOf(book, b).slice(0, b.steps).forEach((l, i) => { if (l === L) out.push(b.title + ' ' + (i + 1)); });
  if (book.far && book.far.lvl === L) out.push('далекі замовлення');
  const ti = book.titles && book.titles[L];
  if (ti) out.push('титул «' + (ti === 'chumak' ? 'Чумак' : ti === 'otaman' ? 'Отаман валки' : ti) + '»');
  return out;
}

// Що дає щабель — словами (числа поруч рахуються з книги). Заміри — стенд у браузері, 25.10 (specs «Гараж — клієнт»).
const PERK = {
  cruise: 'Круїз-контроль: тримає швидкість сам. Увімкнути — K · хрестовина → · кнопка керма «круїз». Газ чи гальмо знімають.',
  quickShift: 'Розумний автомат: на підйомі не «полює» передачами (вгору — лише коли вища потягне) і раніше скидає вниз.',
  retarder: 'Сповільнювач: гальмо двигуном 0,06 → 0,10 — спуск без педалі, менше поздовжніх ударів по вантажу.',
  abs: 'ABS: колесо не блокується — у повороті на гальмі кермо тримає, вантаж б\'ється менше.',
  allSeason: 'Всесезонні: на дощі й снігу зчеплення не падає (погода — з етапу 4).',
  quiet: 'Тихий салон: двигун у кабіні −30 %, радіо чистіше.',
  nav: 'Навігатор: стрілка наступного повороту й «через 300 м» над спідометром.',
  seat: 'Крісло на пневмі: трус камери в кабіні −50 %.',
};
const MEASURED = {
  engine: 'Заміряно (гірка 10 %, 12 т, з «розумним автоматом»): 17 → 19 км/год на 2-му щаблі, 23 — на 3-му й 4-му. Рівна дорога — та сама 90.',
  gear: 'Без «розумного автомата» фура на гірці 10 % перемикає 3 ↔ 4 по колу й іде 11–19 км/год; з ним тримає рівно.',
  brakes: 'Шлях з 59 км/год, 12 т: 29,2 м → 26,6 (1) → 24,4 (2). ABS у повороті: курс +3°, шкода вантажу 17,6 → 11,2 %.',
  susp: 'Лише від доріг: удар чужої фури в «💥 зіткненнях» б\'є, як усіх.',
};

// ---------------------------------------------------------------- гараж
export class Garage {
  /**
   * o: { THREE, app, hud, truck, model, sound, voice, input, nav, server (міст стола або null — полігон), say,
   *      takeModal(m), onTestDrive(params, label) (з краю на полігон), onLavka() (вийти й відкрити Лавку), testDrive }
   */
  constructor(o) {
    Object.assign(this, o);
    this.book = null;
    this.v = null;                       // останній вид стола
    this.tab = 'tractors'; this.sel = {};
    this.m = null;                       // відкрите вікно
    this.lastTrip = undefined;           // career.last.trip, який уже бачили (новий — підсумок рівня)
    this.lavka = null;
    this.paramsKey = ''; this.lookKey = '';
    loadBook().then((b) => { this.book = b; if (this.v) this.view(this.v); if (this.m) this.render(); });
    if (this.hud) {
      const sp = this.hud.querySelector('.fura-speedo');
      this.xpEl = document.createElement('div');
      this.xpEl.className = 'fura-xp'; this.xpEl.style.display = 'none';
      this.xpEl.innerHTML = '<i></i><span></span>';
      if (sp) sp.appendChild(this.xpEl);
      this.navEl = document.createElement('div');
      this.navEl.className = 'fura-panel fura-navi'; this.navEl.style.display = 'none';
      this.hud.appendChild(this.navEl);
    }
  }

  get career() { return (this.v && this.v.career) || (this.testDrive && this.testDrive.career) || null; }
  get phase() { return this.v ? this.v.phase : 'free'; }
  myNick() { const v = this.v, c = v && v.convoy && v.convoy.find((x) => x.seat === v.seat); return c ? c.nick : ''; }

  // ------------------------------------------------------------ вид стола
  /** Кожен вид: числа у фізику, вигляд на модель, смуга ДВ, новий рівень. */
  view(v) {
    if (!v || v.stage == null) return;
    this.v = v;
    const c = v.career;
    if (c && c.params) {
      const key = JSON.stringify(c.params);
      if (key !== this.paramsKey) { this.paramsKey = key; this.truck.setParams(c.params); this.tractorSound(c.params.tractor); }
    }
    const lk = JSON.stringify([c && c.look, this.myNick()]);
    if (lk !== this.lookKey) { this.lookKey = lk; this.dress(c && c.look); }
    this.xp();
    if (c) {
      const last = c.last;
      if (this.lastTrip === undefined) this.lastTrip = last ? last.trip : null;      // перший вид — без привітань
      else if (last && last.trip !== this.lastTrip) {
        this.lastTrip = last.trip;
        if (last.up) this.levelUp(last.up);
      }
    }
    if (this.m && this.m.open) this.render();
  }

  dress(look) {
    if (this.model && this.model.setLook) this.model.setLook(look, this.myNick());
    if (this.sound && this.sound.setHorn) this.sound.setHorn(look && look.horn);
  }

  tractorSound(id) {
    const t = this.book && this.book.tractors.find((x) => x.id === id);
    if (this.sound && this.sound.setVoiceOf) this.sound.setVoiceOf(t ? t.sound : 'diesel');
  }

  levelUp(L) {
    const un = this.book ? unlocksAt(this.book, L) : [];
    this.say('🎉 Рівень ' + L + '!' + (un.length ? ' Відкрилось: ' + un.join(', ') + ' — 🔧 гараж (G)' : ''), 6000);
    if (this.voice) this.voice.say('level_up');
  }

  /** Смуга ДВ під тахометром: «Р7 · 420/450» — ДВ від початку рівня й скільки до наступного. */
  xp() {
    const c = this.career, el = this.xpEl;
    if (!el) return;
    if (!c) { el.style.display = 'none'; return; }
    const have = c.xp - c.from, need = Math.max(1, c.next - c.from);
    el.style.display = '';
    el.querySelector('i').style.width = Math.max(0, Math.min(100, (have / need) * 100)) + '%';
    el.querySelector('span').textContent = 'Р' + c.lvl + (c.stars ? ' ' + '★'.repeat(Math.min(3, c.stars)) + (c.stars > 3 ? c.stars : '') : '') + ' · ' + have + '/' + need
      + (c.tired ? ' · втома' : '');
    el.title = 'Досвід водія: ' + c.xp + ' ДВ' + (c.tired ? ' · сьогодні вже 10 рейсів — далі ДВ ×0,5' : '');
  }

  /** Навігатор (кабіна, щабель 2): наступний поворот маршруту. Кличе main.js раз на кадр HUD. */
  navHint() {
    const el = this.navEl;
    if (!el) return;
    const r = this.nav && this.nav.route, t = this.truck;
    let html = '';
    if (t.P.nav && r && r.pts.length > 3) {
      const a = t.tb.translation(), P = r.pts;
      let k = 0, best = Infinity;
      for (let i = 0; i < P.length; i++) { const d = (P[i].x - a.x) ** 2 + (P[i].z - a.z) ** 2; if (d < best) { best = d; k = i; } }
      const dir = (i, j) => Math.atan2(P[j].x - P[i].x, P[j].z - P[i].z);
      let d = 0, turn = 0, at = 0;
      for (let j = k + 1; j < P.length - 1 && d < 1500; j++) {
        d += Math.hypot(P[j].x - P[j - 1].x, P[j].z - P[j - 1].z);
        // курс 15 м до і після точки
        let i0 = j, s0 = 0; while (i0 > k && s0 < 15) { s0 += Math.hypot(P[i0].x - P[i0 - 1].x, P[i0].z - P[i0 - 1].z); i0--; }
        let i1 = j, s1 = 0; while (i1 < P.length - 1 && s1 < 15) { s1 += Math.hypot(P[i1 + 1].x - P[i1].x, P[i1 + 1].z - P[i1].z); i1++; }
        if (i0 === j || i1 === j) continue;
        const da = Math.atan2(Math.sin(dir(j, i1) - dir(i0, j)), Math.cos(dir(j, i1) - dir(i0, j)));
        if (Math.abs(da) > 0.5 && d > 12) { turn = da; at = d; break; }
      }
      // курс: +X — схід; кут atan2(x, z) росте ліворуч від погляду (праворуч у фури −X), тож мінус — праворуч
      if (turn) html = '<b>' + (turn < 0 ? '↱' : '↰') + '</b> через ' + (at < 100 ? Math.round(at / 10) * 10 : Math.round(at / 50) * 50) + ' м';
      else html = '<b>⬆</b> прямо ' + (r.len >= 1000 ? num(r.len / 1000, 1) + ' км' : Math.round(r.len) + ' м');
    }
    if (el._html !== html) { el.innerHTML = html; el._html = html; }
    el.style.display = html ? '' : 'none';
  }

  // ------------------------------------------------------------ вікно
  open(tab) {
    if (this.m && this.m.open) { if (tab) { this.tab = tab; this.render(); } return; }
    if (tab) this.tab = tab;
    const el = document.createElement('div');
    el.className = 'fura-garage';
    el.innerHTML = '<div class="fura-gr-top"><b>🔧 Гараж</b><span class="fura-gr-lvl"></span><span class="fura-gr-bal"></span>'
      + '<button type="button" class="fura-gr-x" title="Назад (Esc · Ⓑ)">✕</button></div>'
      + '<nav class="fura-gr-tabs"></nav>'
      + '<div class="fura-gr-body"><div class="fura-gr-list"></div><div class="fura-gr-stage"><canvas></canvas><div class="fura-gr-tag"></div></div>'
      + '<div class="fura-gr-card"></div></div>'
      + '<div class="fura-gr-help muted">⌨ ←→ вкладки · ↑↓ вибір · Enter тримати — купити · P тест-драйв · Esc назад'
      + ' &nbsp;|&nbsp; 🎮 LB/RB · ✚ · Ⓐ тримати · Ⓨ тест-драйв · Ⓑ &nbsp;|&nbsp; 🛞 пелюстки · «камера» вкладка · «зчепити» тримати</div>';
    this.app.appendChild(el);
    const m = { open: true, el, noNav: true, tick: (now, dt) => this.tick(now, dt), close: () => this.close() };
    this.m = m;
    this.fresh = true; this.prevPad = new Map(); this.hold = null; this.msg = '';
    this.takeModal(m);
    el.addEventListener('click', (e) => this.onClick(e));
    el.addEventListener('pointerdown', (e) => { const b = e.target.closest('[data-act=hold]'); if (b && !b.disabled) { e.preventDefault(); this.startHold('mouse'); } });
    const up = () => { if (this.hold && this.hold.by === 'mouse') this.hold = null; };
    el.addEventListener('pointerup', up); el.addEventListener('pointerleave', up);
    this.stage(el.querySelector('.fura-gr-stage canvas'));
    this.render();
    this.loadLavka();
    if (this.server && this.v) this.server.tell('garage').catch(() => {});      // свіже вдягнуте з Лавки
  }

  close() {
    const m = this.m;
    if (!m || !m.open) return;
    m.open = false;
    m.el.remove();
    this.m = null;
    this.hold = null;
    if (this.pd) {
      this.pd.scene.traverse((o) => { if (o.geometry) o.geometry.dispose(); const ms = o.material ? [].concat(o.material) : []; for (const x of ms) { if (x.map) x.map.dispose(); x.dispose(); } });
      this.pd.renderer.dispose();
      try { this.pd.renderer.forceContextLoss(); } catch { /* пусто */ }
      this.pd = null;
    }
    m.onClosed && m.onClosed();
  }

  async loadLavka() {
    const c = this.career;
    if (!c || !c.account || !this.server) return;
    try {
      const r = await fetch('/api/lavka', { headers: { 'X-Nick': encodeURIComponent(this.myNick()) } });
      if (r.ok) { this.lavka = await r.json(); if (this.m) this.render(); }
    } catch { /* без Лавки — вкладка «Вигляд» лише з посиланням */ }
  }

  /** Постамент: свій маленький рендер із тягачем теперішньої моделі й фарби, крутиться, поки вікно відкрите. */
  stage(cv) {
    const THREE = this.THREE;
    try {
      const renderer = new THREE.WebGLRenderer({ canvas: cv, antialias: true, alpha: true });
      renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 1.5));
      renderer.toneMapping = THREE.ACESFilmicToneMapping;
      const scene = new THREE.Scene();
      scene.add(new THREE.HemisphereLight('#f4ecdf', '#3a3530', 1.3));
      const sun = new THREE.DirectionalLight('#fff1d6', 2.2); sun.position.set(6, 10, 7); scene.add(sun);
      const base = new THREE.Mesh(new THREE.CylinderGeometry(4.4, 4.7, 0.4, 40), new THREE.MeshLambertMaterial({ color: '#3b3f49' }));
      base.position.y = -0.95; scene.add(base);
      const rim = new THREE.Mesh(new THREE.TorusGeometry(4.45, 0.05, 6, 48), new THREE.MeshBasicMaterial({ color: '#d08a2c' }));
      rim.rotation.x = Math.PI / 2; rim.position.y = -0.74; scene.add(rim);
      const model = buildModels(THREE, scene);
      model.trailer.visible = false;
      const pivot = new THREE.Group(); scene.add(pivot);
      pivot.add(model.tractor);
      model.tractor.position.set(0, 0.3, -0.4);
      const camera = new THREE.PerspectiveCamera(34, 1.4, 0.1, 80);
      camera.position.set(9.5, 4.2, 9.5); camera.lookAt(0, 0.9, 0);
      this.pd = { renderer, scene, camera, model, pivot, cv, w: 0, h: 0 };
      this.pd.look = '';
    } catch (e) { console.warn('[fura] постамент гаража', e); this.pd = null; }
  }

  // ------------------------------------------------------------ що в списку
  items() {
    const b = this.book, c = this.career || {};
    if (!b) return [];
    const owned = c.owned || { tractors: ['chumak'], trailers: ['tent'] }, pick = c.pick || { tractor: 'chumak', trailer: 'tent' };
    const lvl = c.lvl || 1, tune = c.tune || {};
    if (this.tab === 'tractors' || this.tab === 'trailers') {
      const list = this.tab === 'tractors' ? b.tractors : b.trailers, key = this.tab === 'tractors' ? 'tractor' : 'trailer';
      return list.map((t) => {
        const has = (owned[this.tab] || []).includes(t.id), picked = pick[key] === t.id, self = key === 'trailer' && t.carries !== 'tent';
        const act = !has ? { kind: 'buy', id: t.id } : picked || self ? null : { kind: 'pick', [key]: t.id };
        return { key, id: t.id, t, title: t.title, lvl: t.lvl, price: t.price, has, picked, self, act, locked: lvl < t.lvl };
      });
    }
    if (this.tab === 'tune') {
      return b.tune.branches.map((br) => {
        const n = Math.min(br.steps, tune[br.id] || 0), next = n < br.steps ? n + 1 : 0;
        const L = levelsOf(b, br);
        return { key: 'tune', id: br.id, br, title: br.title, n, next, lvl: next ? L[next - 1] : 0, price: next ? b.tune.prices[next - 1] : 0,
          has: !next, act: next ? { kind: 'buy', id: br.id + next } : null, locked: next && lvl < L[next - 1] };
      });
    }
    // вигляд: куплене в Лавці «🚚 Фура» — вдягнути / зняти; і «купити» — у Лавку
    const out = [];
    const items = ((this.lavka && this.lavka.items) || []).filter((x) => /^fura-/.test(x.kind) && x.owned);
    for (const it of items) out.push({ key: 'look', id: it.id, it, title: it.title, has: true, worn: !!it.worn, act: { kind: 'wear', it } });
    out.push({ key: 'shop', id: 'shop', title: '🛒 Купити в Лавці «🚚 Фура»', act: { kind: 'shop' } });
    return out;
  }

  selected(list) {
    list = list || this.items();
    const id = this.sel[this.tab];
    return list.find((x) => x.id === id) || list[0] || null;
  }

  /** Перемалювати вікно (вкладки, список, картку). Дешево: лише коли щось змінилось. */
  render() {
    const m = this.m;
    if (!m || !m.open) return;
    const el = m.el, b = this.book, c = this.career;
    const top = c ? 'Р' + c.lvl + (c.stars ? ' ★' + c.stars : '') + ' · ' + c.xp + ' ДВ' + (c.lvl < (b ? b.levels.max : 20) ? ' · до Р' + (c.lvl + 1) + ': ' + (c.next - c.xp) : '') : 'гість полігона';
    const bal = this.lavka && this.lavka.balance != null ? '🏺 ' + this.lavka.balance : '';
    const tabs = TABS.map(([k, l]) => '<button type="button" data-tab="' + k + '"' + (k === this.tab ? ' class="on"' : '') + '>' + l + '</button>').join('');
    const list = this.items(), s = this.selected(list);
    if (s) this.sel[this.tab] = s.id;
    const rows = !b ? '<div class="muted">⏳ Відчиняю гараж…</div>' : list.map((x) => {
      let state = '';
      if (x.key === 'tune') state = x.has ? '✓ усе' : x.n + '/' + x.br.steps + (x.locked ? ' · 🔒 Р' + x.lvl : ' · ' + x.price + ' 🏺');
      else if (x.key === 'look') state = x.worn ? '✓ на фурі' : '';
      else if (x.key !== 'shop') state = x.picked ? '✓ їду' : x.has ? (x.self ? '✓ сам під вантаж' : '✓ твій') : x.locked ? '🔒 Р' + x.lvl : x.price + ' 🏺';
      return '<button type="button" class="fura-gr-it' + (x === s ? ' on' : '') + (x.locked ? ' locked' : '') + '" data-id="' + escH(x.id) + '"><span>' + escH(x.title) + '</span><small>' + state + '</small></button>';
    }).join('');
    const card = s ? this.cardHtml(s) : '';
    const html = [top, bal, tabs, rows, card, this.msg].join('|');
    if (m._html === html) return;
    m._html = html;
    el.querySelector('.fura-gr-lvl').textContent = top;
    el.querySelector('.fura-gr-bal').textContent = bal;
    el.querySelector('.fura-gr-tabs').innerHTML = tabs;
    el.querySelector('.fura-gr-list').innerHTML = rows;
    el.querySelector('.fura-gr-card').innerHTML = card + (this.msg ? '<div class="fura-gr-msg">' + this.msg + '</div>' : '');
    const on = el.querySelector('.fura-gr-it.on');
    if (on && on.scrollIntoView) on.scrollIntoView({ block: 'nearest' });
    el.querySelector('.fura-gr-tag').textContent = s && (s.key === 'tractor') ? s.title : '';
  }

  /** Чому купити не можна (або null — можна). */
  why(x) {
    const c = this.career;
    if (!this.server) return 'Купують у «Чумацькому краї» — тут лише тест-драйв';
    if (!c) return 'Сядь за стіл — тоді гараж твій';
    if (!c.account) return 'Закріпи нік (акаунт) — тоді гараж твій. Тест-драйв — можна й так';
    if (c.on === false) return 'Гараж зачинено';
    if (this.phase !== 'free') return 'Гараж — між рейсами: здай чи зніми замовлення';
    if (x && x.locked) return '🔒 Це відкриється з рівня ' + x.lvl;
    return null;
  }

  cardHtml(x) {
    const b = this.book, c = this.career || {}, pick = c.pick || { tractor: 'chumak', trailer: 'tent' };
    const row = (a, v) => '<tr><td>' + a + '</td><td>' + v + '</td></tr>';
    let h = '<h4>' + escH(x.title) + '</h4>';
    let canTest = false;
    if (x.key === 'tractor') {
      const t = x.t, now = b.tractors.find((y) => y.id === pick.tractor) || b.tractors[0];
      const cmp = (k, f) => (now && now.id !== t.id && now[k] !== t[k] ? f(now[k]) + ' → ' : '') + '<b>' + f(t[k]) + '</b>';
      // note тягача з книги не показуємо: її «гірка ~64 проти ~56» — оцінка за потужністю, а заміри дають інше (MEASURED)
      h += '<table>'
        + row('Потужність', cmp('power', kw) + ' (~' + Math.round(t.power / 735.5) + ' к.с.)') + row('Тяга', cmp('fmax', kn))
        + row('Маса тягача', cmp('mass', (m) => num(m / 1000, 1) + ' т')) + row('Кермо', cmp('steer', (s) => num(s, 2) + ' рад'))
        + (t.limits !== 1 ? row('Вантаж', 'пороги ударів +' + Math.round((t.limits - 1) * 100) + ' %') : '')
        + (t.sway !== 1 ? row('Кабіна', 'хитається на ' + Math.round((1 - t.sway) * 100) + ' % менше') : '')
        + row('Звук', { diesel: 'теперішній дизель', v8: 'нижчий, важкий', six: 'вищий, рядна «шістка»' }[t.sound] || t.sound)
        + row('Швидкість', 'до 90 км/год, як у всіх') + '</table><p class="muted small">У «💥 зіткненнях» маса й габарити — стандартні, як у «Чумака».</p>';
      canTest = true;
    } else if (x.key === 'trailer') {
      const t = x.t;
      h += '<p class="muted">' + escH(t.note || '') + '</p><table>'
        + row('Везе', t.carries === 'tent' ? 'усі вантажі краю' : t.carries === 'izoterm' ? '🥛 Молоко, 🍦 Морозиво' : '🔔 Дзвін, 🚜 Трактор')
        + (t.xp && t.xp !== 1 ? row('Досвід', '×' + num(t.xp, 1) + ' ДВ за рейс') : '')
        + (t.hurt && t.hurt !== 1 ? row('Вантаж', 'шкода ×' + num(t.hurt, 2) + ', підвіска м\'якша') : '')
        + row('Як чіпляється', t.carries === 'tent' ? 'обираєш сам (тент чи «Пух»)' : 'сам під свій вантаж — замовлення з\'являться на дошці') + '</table>';
      canTest = t.carries === 'tent';
    } else if (x.key === 'tune') {
      const br = x.br, L = levelsOf(b, br), tr = b.tractors.find((y) => y.id === pick.tractor) || b.tractors[0];
      h += '<table>';
      for (let s = 1; s <= br.steps; s++) {
        const perk = (br.perks || []).find((p) => p.step === s);
        let what = '';
        if (perk) what = (PERK[perk.id] || perk.title);
        else if (br.id === 'engine') what = 'потужність ' + kw(tr.power * (1 + 0.05 * s)) + ', тяга ' + kn(tr.fmax * (1 + 0.05 * s));
        else if (br.id === 'brakes') what = 'гальма +' + (10 * s) + ' %: тягач ' + Math.round(b.base.brake * (1 + 0.1 * s)) + ', причіп ' + Math.round(b.base.trailerBrake * (1 + 0.1 * s));
        else if (br.id === 'susp') what = 'пороги вантажу +' + (5 * s) + ' %, шкода ×' + num(Math.pow(br.hurtPer, s), 2);
        else if (br.id === 'tires') what = 'зчеплення +' + (4 * s) + ' %';
        else what = br.note || '';
        const st = s <= x.n ? '✓' : s === x.next ? (x.locked ? '🔒 Р' + L[s - 1] : '<b>' + b.tune.prices[s - 1] + ' 🏺</b>') : '🔒 Р' + L[s - 1];
        h += '<tr' + (s === x.next ? ' class="fura-gr-next"' : '') + '><td>' + s + ' · ' + st + '</td><td>' + escH(what) + '</td></tr>';
      }
      h += '</table>' + (MEASURED[br.id] ? '<p class="muted small">' + escH(MEASURED[br.id]) + '</p>' : '');
      canTest = !!x.next;
    } else if (x.key === 'look') {
      const it = x.it;
      h += '<p>' + escH(lookText(it)) + '</p><p class="muted small">Видно всім у колоні' + (it.kind === 'fura-horn' ? '; ріжок звучить у тебе (H · Ⓨ)' : '') + '.</p>';
    } else if (x.key === 'shop') {
      h += '<p>Фарби, «Металік», розписи, ріжки, глечик і кіт на панель, наліпки, вимпел, люстра — у Лавці, група «🚚 Фура». Купують назавжди й можна дарувати.</p>'
        + '<p class="muted small">Номер на фурі поки рахується з ніка: ' + escH(plateOf(this.myNick())) + '.</p>';
    }
    // дія
    const a = x.act;
    let btn = '';
    if (a && a.kind === 'buy') {
      const why = this.why(x);
      btn = why ? '<p class="fura-gr-why">' + escH(why) + '</p>' : '<button type="button" class="main fura-gr-buy" data-act="hold"><i></i><span>Тримай 1 с — купити за ' + x.price + ' 🏺</span></button>';
    } else if (a && a.kind === 'pick') {
      const why = this.why(null);
      btn = why ? '<p class="fura-gr-why">' + escH(why) + '</p>' : '<button type="button" class="main" data-act="press">✓ Їхати ним</button>';
    } else if (a && a.kind === 'wear') btn = '<button type="button" class="main" data-act="press">' + (x.worn ? 'Зняти з фури' : 'Вдягти на фуру') + '</button>';
    else if (a && a.kind === 'shop') btn = '<button type="button" class="main" data-act="press">🛒 У Лавку (гра чекатиме)</button>';
    else if (x.key !== 'look') btn = '<p class="muted">' + (x.picked ? '✓ Ти їдеш ним' : x.self ? '✓ Твій — чіпляється сам під свій вантаж' : '✓ Усе куплено') + '</p>';
    if (canTest) btn += '<button type="button" data-act="test">🧪 Тест-драйв на полігоні (Ⓨ · P)</button>';
    return h + '<div class="fura-gr-acts">' + btn + '</div>';
  }

  // ------------------------------------------------------------ дії
  onClick(e) {
    const t = e.target;
    if (t.closest('.fura-gr-x')) { this.close(); return; }
    const tb = t.closest('[data-tab]');
    if (tb) { this.tab = tb.dataset.tab; this.msg = ''; this.render(); return; }
    const it = t.closest('.fura-gr-it');
    if (it) { this.sel[this.tab] = it.dataset.id; this.msg = ''; this.render(); return; }
    const a = t.closest('[data-act]');
    if (a && a.dataset.act === 'press') this.act();
    else if (a && a.dataset.act === 'test') this.test();
  }

  move(d) {
    const list = this.items();
    if (!list.length) return;
    const i = Math.max(0, list.indexOf(this.selected(list)));
    this.sel[this.tab] = list[(i + d + list.length) % list.length].id;
    this.msg = '';
    this.render();
  }

  tabBy(d) {
    const i = TABS.findIndex(([k]) => k === this.tab);
    this.tab = TABS[(i + d + TABS.length) % TABS.length][0];
    this.msg = '';
    this.render();
  }

  /** Ⓐ / Enter / клік: купівля — лише утриманням (startHold), решта — одразу. */
  press(by) {
    const s = this.selected();
    if (!s || !s.act) return;
    if (s.act.kind === 'buy') this.startHold(by);
    else this.act();
  }

  startHold(by) {
    const s = this.selected();
    if (!s || !s.act || s.act.kind !== 'buy' || this.busy) return;
    const why = this.why(s);
    if (why) { this.msg = escH(why); this.render(); return; }
    this.hold = { by, at: performance.now(), id: s.act.id };
  }

  async act() {
    const s = this.selected();
    if (!s || !s.act || this.busy) return;
    const a = s.act;
    // з кадру гри не виходимо: main.js після tick ще крутить фізику — вихід наступним завданням
    if (a.kind === 'shop') { this.close(); setTimeout(() => this.onLavka && this.onLavka(), 0); return; }
    if (a.kind === 'wear') return this.wear(a.it);
    if (a.kind === 'buy') return this.buy(a.id, s);
    if (a.kind === 'pick') {
      const why = this.why(null);
      if (why) { this.msg = escH(why); this.render(); return; }
      this.busy = true;
      try {
        const r = await this.server.tell('pick', a.tractor ? { tractor: a.tractor } : { trailer: a.trailer });
        this.msg = r && r.ok ? '✓ Обрано: ' + escH(s.title) : '⚠ ' + escH((r && r.message) || 'Нема зв\'язку з сервером');
      } finally { this.busy = false; this.render(); }
    }
  }

  async buy(id, s) {
    if (this.busy) return;
    const why = this.why(s);
    if (why) { this.msg = escH(why); this.render(); return; }
    this.busy = true; this.msg = '⏳ Купую…'; this.render();
    try {
      const r = await this.server.tell('buy', { id });
      if (r && r.ok) {
        this.msg = '✓ ' + escH(r.message || 'Куплено');
        if (this.voice) this.voice.say('buy');
        this.say(r.message || '🔧 Куплено', 3000);
        this.loadLavka();                 // баланс
      } else this.msg = '⚠ ' + escH((r && r.message) || 'Нема зв\'язку з сервером');
    } finally { this.busy = false; this.render(); }
  }

  async wear(it) {
    const worn = (this.lavka && this.lavka.worn && this.lavka.worn.fura) || {};
    let slot = it.kind;
    if (it.kind === 'fura-sticker') {
      const slots = ['fura-sticker', 'fura-sticker2', 'fura-sticker3'];
      slot = it.worn ? slots.find((x) => worn[x] === it.id) : slots.find((x) => !worn[x]);
      if (!slot) { this.msg = 'Наліпок — до трьох: спершу зніми одну'; this.render(); return; }
    }
    this.busy = true;
    try {
      const r = await fetch('/api/lavka/wear', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Nick': encodeURIComponent(this.myNick()) },
        body: JSON.stringify(it.worn ? { slot } : { slot, item: it.id }) });
      const d = await r.json().catch(() => null);
      this.msg = r.ok ? '✓ ' + escH((d && d.message) || 'Готово') : '⚠ ' + escH((d && d.message) || 'Не вийшло');
      await this.loadLavka();
      if (this.server) await this.server.tell('garage');
    } catch { this.msg = '⚠ Нема зв\'язку'; } finally { this.busy = false; this.render(); }
  }

  /** Тест-драйв обраного: безкоштовно, без рівня, на полігоні (з краю — main/fura.js перезапускає гру на полігоні). */
  test() {
    const s = this.selected(), b = this.book;
    if (!s || !b) return;
    const c = this.career || {}, pick = c.pick || { tractor: 'chumak', trailer: 'tent' }, tune = { ...(c.tune || {}) };
    let tractor = pick.tractor, trailer = pick.trailer, label = '';
    if (s.key === 'tractor') { tractor = s.id; label = 'тягач «' + s.title + '»'; }
    else if (s.key === 'trailer' && s.t.carries === 'tent') { trailer = s.id; label = 'причіп «' + s.title + '»'; }
    else if (s.key === 'tune' && s.next) { tune[s.id] = s.next; label = s.title + ' ' + s.next; }
    else return;
    const p = paramsFor(b, tractor, trailer, tune);
    if (c.equal || c.on === false) label += ' (за столом «⚖» усе одно «Чумак»)';
    this.close();
    if (this.server) { setTimeout(() => this.onTestDrive && this.onTestDrive(p, label, c), 0); return; }
    this.truck.setParams(p);
    this.tractorSound(p.tractor);
    this.say('🧪 Тест-драйв: ' + label + '. Гірка 10 % — на схід від майданчика', 4500);
  }

  // ------------------------------------------------------------ кадр (поки вікно відкрите)
  tick(now, dt) {
    const inp = this.input;
    let move = 0, tab = 0, back = false, test = false, ok = false;
    let held = inp.keys.has('Enter') || inp.keys.has('NumpadEnter');
    if (inp.took('ArrowDown')) move = 1;
    if (inp.took('ArrowUp')) move = -1;
    if (inp.took('ArrowRight') || inp.took('KeyE')) tab = 1;
    if (inp.took('ArrowLeft') || inp.took('KeyQ')) tab = -1;
    if (inp.took('Escape') || inp.took('KeyG')) back = true;
    if (inp.took('KeyP')) test = true;
    if (inp.took('Enter') || inp.took('NumpadEnter')) ok = 'kbd';
    const on = (gp, k) => k != null && k >= 0 && gp.buttons[k] && gp.buttons[k].pressed;
    for (const gp of pads()) {
      const h = new Set();
      if (isPad(gp)) {
        if (on(gp, 12) || (gp.axes[1] || 0) < -0.6) h.add('up');
        if (on(gp, 13) || (gp.axes[1] || 0) > 0.6) h.add('down');
        if (on(gp, 4) || on(gp, 14)) h.add('left');
        if (on(gp, 5) || on(gp, 15)) h.add('right');
        if (on(gp, 0)) h.add('ok');
        if (on(gp, 1)) h.add('back');
        if (on(gp, 3)) h.add('test');
      } else {
        const c = loadCalib(gp.id), bt = c && c.buttons;
        if (bt) {
          if (on(gp, bt.gearDown)) h.add('up');
          if (on(gp, bt.gearUp)) h.add('down');
          if (on(gp, bt.camera)) h.add('right');
          if (on(gp, bt.couple)) h.add('ok');
          if (on(gp, bt.menu)) h.add('back');
          if (on(gp, bt.horn)) h.add('test');
        }
      }
      const before = this.prevPad.get(gp.id) || new Set();
      if (!this.fresh) for (const a of h) if (!before.has(a)) {
        if (a === 'up') move = -1; else if (a === 'down') move = 1; else if (a === 'left') tab = -1; else if (a === 'right') tab = 1;
        else if (a === 'ok') ok = gp.id; else if (a === 'back') back = true; else if (a === 'test') test = true;
      }
      // утримання Ⓐ (лише того пада, що почав)
      if (this.hold && this.hold.by === gp.id && h.has('ok')) held = true;
      this.prevPad.set(gp.id, h);
    }
    this.fresh = false;
    if (back) { this.close(); return; }
    if (tab) this.tabBy(tab);
    if (move) this.move(move);
    if (test) this.test();
    if (!this.m) return;
    if (ok) this.press(ok);
    // утримання: клавіатура — Enter, пад — Ⓐ, миша — кнопка (pointerup знімає)
    const H = this.hold;
    if (H) {
      const still = H.by === 'mouse' || held;
      const s = this.selected();
      if (!still || !s || !s.act || s.act.id !== H.id) this.hold = null;
      else if (now - H.at >= HOLD_MS) { this.hold = null; this.buy(H.id, s); }
    }
    const bar = this.m.el.querySelector('.fura-gr-buy i');
    if (bar) bar.style.width = (this.hold ? Math.min(100, ((now - this.hold.at) / HOLD_MS) * 100) : 0) + '%';
    this.spin(dt);
  }

  /** Постамент: крутити тягач, розмір — за полотном, вигляд — вдягнуте (у «Вигляді» — ще й обране на примірку). */
  spin(dt) {
    const pd = this.pd;
    if (!pd) return;
    const w = pd.cv.clientWidth, h = pd.cv.clientHeight;
    if (!w || !h) return;
    if (w !== pd.w || h !== pd.h) {
      pd.w = w; pd.h = h; pd.renderer.setSize(w, h, false); pd.camera.aspect = w / h;
      // вузьке полотно — камера далі, щоб тягач (~7 м) уміщався й на телефоні
      const k = Math.max(1, 1.25 / pd.camera.aspect);
      pd.camera.position.set(9.5 * k, 4.2 + (k - 1) * 2, 9.5 * k); pd.camera.lookAt(0, 0.9, 0);
      pd.camera.updateProjectionMatrix();
    }
    const c = this.career, s = this.selected();
    let look = (c && c.look) ? { ...c.look, stickers: [...(c.look.stickers || [])] } : { stickers: [] };
    if (s && s.key === 'look' && !s.worn) look = tryOn(look, s.it);
    const key = JSON.stringify(look);
    if (key !== pd.look) { pd.look = key; if (pd.model.setLook) pd.model.setLook(look, this.myNick()); }
    pd.pivot.rotation.y += dt * 0.45;
    pd.renderer.render(pd.scene, pd.camera);
  }
}

/** Вигляд із приміряною річчю (на постаменті, нічого не вдягає). */
function tryOn(look, it) {
  const k = { 'fura-paint': 'paint', 'fura-gloss': 'gloss', 'fura-art': 'art', 'fura-horn': 'horn', 'fura-dash': 'dash', 'fura-flag': 'flag', 'fura-lamp': 'lamp' }[it.kind];
  if (k) look[k] = it.id;
  else if (it.kind === 'fura-sticker' && !look.stickers.includes(it.id)) look.stickers = look.stickers.concat(it.id).slice(-3);
  return look;
}

function lookText(it) {
  switch (it.kind) {
    case 'fura-paint': return it.id === 'paintrainbow' ? 'Фарба кабіни «Веселка».' : 'Фарба кабіни: ' + it.title + '.';
    case 'fura-gloss': return 'Блиск поверх будь-якої фарби.';
    case 'fura-art': return 'Розпис на кабіну й борт причепа.';
    case 'fura-horn': return 'Ріжок замість звичайного сигналу.';
    case 'fura-dash': return it.id === 'dashjug' ? 'Глечик на панелі хитається на поворотах і ямах — від прискорення причепа.' : 'Річ на панелі кабіни.';
    case 'fura-sticker': return 'Наліпка на задні двері причепа (до трьох).';
    case 'fura-flag': return 'На дах кабіни.';
    case 'fura-lamp': return 'Діодна люстра на дах — світиться з фарами.';
  }
  return it.title;
}
