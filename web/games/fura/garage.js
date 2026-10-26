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

/**
 * Що вже можна купити за рівнем (черепки не рахуємо): [{ tab, row, id, title, price }] — лічильники на вкладках,
 * вкладка, з якої гараж відкривається, і «🔧 Гараж · можна 6» на дошці й у підсумку. Гість, гараж зачинено — нічого.
 */
export function openNow(book, c) {
  if (!book || !c || !c.account || c.on === false) return [];
  const out = [], lvl = c.lvl || 1, owned = c.owned || {}, tune = c.tune || {};
  for (const [tab, list] of [['tractors', book.tractors], ['trailers', book.trailers]]) {
    for (const t of list) if (t.price && lvl >= t.lvl && !(owned[tab] || []).includes(t.id)) out.push({ tab, row: t.id, id: t.id, title: t.title, price: t.price });
  }
  for (const br of book.tune.branches) {
    const n = Math.min(br.steps, tune[br.id] || 0);
    if (n < br.steps && lvl >= levelsOf(book, br)[n]) out.push({ tab: 'tune', row: br.id, id: br.id + (n + 1), title: br.title + ' ' + (n + 1), price: book.tune.prices[n] });
  }
  return out;
}

/** Купують між рейсами: на дошці (free) і щойно здавши (done — підсумок, «далі» ще не тиснув). */
export const betweenTrips = (phase) => phase === 'free' || phase === 'done';

/** Що дає щабель s гілки — словами й числами (картка Майстерні й повідомлення після покупки). */
function stepText(book, br, s, tr) {
  const perk = (br.perks || []).find((p) => p.step === s);
  if (perk) return PERK[perk.id] || perk.title;
  if (br.id === 'engine') return 'потужність ' + kw(tr.power * (1 + 0.05 * s)) + ', тяга ' + kn(tr.fmax * (1 + 0.05 * s));
  if (br.id === 'brakes') return 'гальма +' + (10 * s) + ' %: тягач ' + Math.round(book.base.brake * (1 + 0.1 * s)) + ', причіп ' + Math.round(book.base.trailerBrake * (1 + 0.1 * s));
  if (br.id === 'susp') return 'пороги вантажу +' + (5 * s) + ' %, шкода ×' + num(Math.pow(br.hurtPer, s), 2);
  if (br.id === 'tires') return 'зчеплення +' + (4 * s) + ' %';
  return br.note || '';
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
    else {
      // одразу туди, де є що купити (Майстерня першою): на Р3 «Тягачі» — суцільні 🔒, і здається, що купити нічого
      const o = openNow(this.book, this.career), first = o.find((x) => x.tab === 'tune') || o[0];
      if (first) { this.tab = first.tab; this.sel[first.tab] = first.row; }
    }
    const el = document.createElement('div');
    el.className = 'fura-garage';
    el.innerHTML = '<div class="fura-gr-top"><b>🔧 Гараж</b><span class="fura-gr-lvl"></span><span class="fura-gr-can"></span><span class="fura-gr-bal"></span>'
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
    const up = () => { if (this.hold && this.hold.by === 'mouse') this.letGo(); };
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
    // догляд (Гараж 2): свій номер, мийка, рихтування — лише вигляд, будь-коли; купують утриманням, як решту
    const care = b.care || {}, lk = c.look || {}, pc = (x) => Math.round((x || 0) * 100) + ' %';
    out.push({ key: 'care', id: 'plate', title: '🔢 Свій номер', price: care.plate, act: { kind: 'buy', id: 'plate' } });
    out.push({ key: 'care', id: 'wash', title: '🧽 Мийка · бруд ' + pc(lk.dirt), price: care.wash, act: (lk.dirt || 0) >= 0.01 ? { kind: 'buy', id: 'wash' } : null });
    out.push({ key: 'care', id: 'fix', title: '🔨 Рихтування · вм\'ятини ' + pc(lk.dents), price: care.fix, act: (lk.dents || 0) >= 0.01 ? { kind: 'buy', id: 'fix' } : null });
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
    const el = m.el, b = this.book, c = this.career, can = openNow(b, c);
    const top = c ? 'Р' + c.lvl + (c.stars ? ' ★' + c.stars : '') + ' · ' + c.xp + ' ДВ' + (c.lvl < (b ? b.levels.max : 20) ? ' · до Р' + (c.lvl + 1) + ': ' + (c.next - c.xp) : '') : 'гість полігона';
    const hint = !c ? '' : can.length ? '🛒 Можна купити зараз: ' + can.length + (betweenTrips(this.phase) ? '' : ' — після рейсу') : '';
    const bal = this.lavka && this.lavka.balance != null ? '🏺 ' + this.lavka.balance : '';
    const tabs = TABS.map(([k, l]) => {
      const n = can.filter((x) => x.tab === k).length;
      return '<button type="button" data-tab="' + k + '"' + (k === this.tab ? ' class="on"' : '') + (n ? ' title="Можна купити зараз: ' + n + '"' : '') + '>' + l
        + (n ? ' <i class="fura-gr-n">' + n + '</i>' : '') + '</button>';
    }).join('');
    const list = this.items(), s = this.selected(list);
    const canRow = new Set(can.filter((x) => x.tab === this.tab).map((x) => x.row));
    if (s) this.sel[this.tab] = s.id;
    const rows = !b ? '<div class="muted">⏳ Відчиняю гараж…</div>' : list.map((x) => {
      let state = '';
      if (x.key === 'tune') state = x.has ? '✓ усе' : x.n + '/' + x.br.steps + (x.locked ? ' · 🔒 Р' + x.lvl : ' · ' + x.price + ' 🏺');
      else if (x.key === 'look') state = x.worn ? '✓ на фурі' : '';
      else if (x.key !== 'shop') state = x.picked ? '✓ їду' : x.has ? (x.self ? '✓ сам під вантаж' : '✓ твій') : x.locked ? '🔒 Р' + x.lvl : x.price + ' 🏺';
      return '<button type="button" class="fura-gr-it' + (x === s ? ' on' : '') + (x.locked ? ' locked' : '') + (canRow.has(x.id) ? ' can' : '') + '" data-id="' + escH(x.id) + '"><span>' + escH(x.title) + '</span><small>' + state + '</small></button>';
    }).join('');
    const card = s ? this.cardHtml(s) : '';
    const html = [top, hint, bal, tabs, rows, card, this.msg].join('|');
    if (m._html === html) return;
    m._html = html;
    el.querySelector('.fura-gr-lvl').textContent = top;
    el.querySelector('.fura-gr-can').textContent = hint;
    el.querySelector('.fura-gr-bal').textContent = bal;
    el.querySelector('.fura-gr-tabs').innerHTML = tabs;
    el.querySelector('.fura-gr-list').innerHTML = rows;
    el.querySelector('.fura-gr-card').innerHTML = card + (this.msg ? '<div class="fura-gr-msg">' + this.msg + '</div>' : '');
    const pi = el.querySelector('.fura-gr-plate');   // номер: текст живе поза html картки, інакше перемальовка з'їдала б фокус
    if (pi) { pi.value = this.plateText || ''; pi.oninput = () => { this.plateText = pi.value; }; }
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
    if (!betweenTrips(this.phase) && !(x && x.key === 'care')) return 'Посеред рейсу гараж зачинено — здай вантаж чи відмовся від замовлення (☰ → дошка). Подивитись і тест-драйв — можна';
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
        const what = stepText(b, br, s, tr);
        const st = s <= x.n ? '✓' : s === x.next ? (x.locked ? '🔒 Р' + L[s - 1] : '<b>' + b.tune.prices[s - 1] + ' 🏺</b>') : '🔒 Р' + L[s - 1];
        h += '<tr' + (s === x.next ? ' class="fura-gr-next"' : '') + '><td>' + s + ' · ' + st + '</td><td>' + escH(what) + '</td></tr>';
      }
      h += '</table>' + (MEASURED[br.id] ? '<p class="muted small">' + escH(MEASURED[br.id]) + '</p>' : '');
      canTest = !!x.next;
    } else if (x.key === 'look') {
      const it = x.it;
      h += '<p>' + escH(lookText(it)) + '</p><p class="muted small">Видно всім у колоні' + (it.kind === 'fura-horn' ? '; ріжок звучить у тебе (H · Ⓨ)' : '') + '.</p>';
    } else if (x.key === 'care') {
      const lk = c.look || {};
      if (x.id === 'plate') {
        h += '<p>Зараз: <b>' + escH(lk.plate || plateOf(this.myNick())) + '</b>' + (lk.plate ? '' : ' (з ніка)') + '</p>'
          + '<p><input class="fura-gr-plate" maxlength="12" placeholder="до 8 знаків: ГЛЕК 01" autocomplete="off"></p>'
          + '<p class="muted small">Літери (українські й латинські), цифри, пробіл і дефіс; решту сервер викине. Повернутись до вже оплаченого номера — безплатно; порожнє — знову з ніка.</p>';
      } else {
        const v = x.id === 'wash' ? lk.dirt : lk.dents;
        h += '<p>' + (x.id === 'wash' ? 'Бруд росте з кілометрами рейсів. Мийка — і фура знову блищить.' : 'Вм\'ятини — від розбитого вантажу й ударів чужих фур. Рихтування — і як нова.')
          + '</p><p>Зараз: <b>' + Math.round((v || 0) * 100) + ' %</b></p><p class="muted small">Лише вигляд: на фізику й черепки не впливає. Видно всім у колоні.</p>';
      }
    } else if (x.key === 'shop') {
      h += '<p>Фарби, «Металік», розписи, ріжки, глечик і кіт на панель, наліпки, вимпел, люстра, а для кабіни — чохли сидінь, кермо й шторки — у Лавці, група «🚚 Фура». Купують назавжди й можна дарувати.</p>'
        + '<p class="muted small">Свій номер, мийка й рихтування — тут же, у «Вигляді».</p>';
    }
    // дія
    const a = x.act;
    let btn = '';
    if (a && a.kind === 'buy') {
      const why = this.why(x);
      const verb = x.key !== 'care' ? 'купити' : x.id === 'plate' ? 'поставити' : x.id === 'wash' ? 'помити' : 'вирихтувати';
      btn = why ? '<p class="fura-gr-why">' + escH(why) + '</p>' : '<button type="button" class="main fura-gr-buy" data-act="hold"><i></i><span>🛒 Затисни й тримай 1 с — ' + verb + ' за ' + x.price + ' 🏺</span></button>'
        + '<p class="muted small">Мишею — тримай кнопку · Enter — тримай · 🎮 Ⓐ — тримай. '
        + (x.key === 'tune' ? 'Діє одразу, назавжди, на кожному тягачі.' : x.key === 'care' ? 'Лише вигляд, будь-коли.' : 'Назавжди.') + '</p>';
    } else if (a && a.kind === 'pick') {
      const why = this.why(null);
      btn = why ? '<p class="fura-gr-why">' + escH(why) + '</p>' : '<button type="button" class="main" data-act="press">✓ Їхати ним</button>';
    } else if (a && a.kind === 'wear') btn = '<button type="button" class="main" data-act="press">' + (x.worn ? 'Зняти з фури' : 'Вдягти на фуру') + '</button>';
    else if (a && a.kind === 'shop') btn = '<button type="button" class="main" data-act="press">🛒 У Лавку (гра чекатиме)</button>';
    else if (x.key === 'care') btn = '<p class="muted">' + (x.id === 'wash' ? '✓ Фура чиста' : '✓ Ані вм\'ятини') + '</p>';
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

  /** Відпустив раніше за 1 с: не купили — і кажемо чому, а не мовчимо (клік мишею тут нічого не купує). */
  letGo() {
    const H = this.hold;
    this.hold = null;
    if (H && performance.now() - H.at < HOLD_MS * 0.9) {
      this.msg = '✋ Затисни й тримай, поки смужка не заповниться (1 с) — так випадково не купиш';
      this.render();
    }
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
      const r = s && s.key === 'care'
        ? await this.server.tell(id, id === 'plate' ? { text: this.plateText || '' } : {})
        : await this.server.tell('buy', { id });
      if (r && r.ok) {
        this.msg = '✓ ' + escH(r.message || 'Куплено') + escH(this.boughtText(s));
        if (this.voice) this.voice.say('buy');
        this.say(r.message || '🔧 Куплено', 3000);
        this.loadLavka();                 // баланс
      } else this.msg = '⚠ ' + escH((r && r.message) || 'Нема зв\'язку з сервером');
    } finally { this.busy = false; this.render(); }
  }

  /** «— уже у фурі: потужність 420 кВт…» — що змінилось, одразу після покупки. */
  boughtText(s) {
    const b = this.book;
    if (!b || !s) return '';
    if (s.key === 'tune' && s.next) {
      const pick = (this.career && this.career.pick) || {}, tr = b.tractors.find((y) => y.id === pick.tractor) || b.tractors[0];
      return '. Уже у фурі: ' + stepText(b, s.br, s.next, tr);
    }
    if (s.key === 'tractor') return '. Ти вже їдеш ним';
    if (s.key === 'trailer') return s.t.carries === 'tent' ? '. Уже причеплено' : '. Його вантажі — на дошці';
    return '';
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
      if (!still) this.letGo();
      else if (!s || !s.act || s.act.id !== H.id) this.hold = null;
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
    if (s && s.key === 'care' && s.id === 'plate' && this.plateText) look.plate = this.plateText.toUpperCase().slice(0, 8);   // примірка
    if (s && s.key === 'care' && s.id !== 'plate') look[s.id === 'wash' ? 'dirt' : 'dents'] = 0;                            // «як буде»
    const key = JSON.stringify(look);
    if (key !== pd.look) { pd.look = key; if (pd.model.setLook) pd.model.setLook(look, this.myNick()); }
    pd.pivot.rotation.y += dt * 0.45;
    pd.renderer.render(pd.scene, pd.camera);
  }
}

/** Вигляд із приміряною річчю (на постаменті, нічого не вдягає). */
function tryOn(look, it) {
  const k = { 'fura-paint': 'paint', 'fura-gloss': 'gloss', 'fura-art': 'art', 'fura-horn': 'horn', 'fura-dash': 'dash', 'fura-flag': 'flag', 'fura-lamp': 'lamp' }[it.kind];
  const cab = { 'fura-seat': 'seat', 'fura-wheel': 'wheel', 'fura-curtain': 'curtain' }[it.kind];
  if (k) look[k] = it.id;
  else if (cab) look[cab] = it.art && it.art.look;         // кабіна: у виді — значення контракту, не id речі
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
    case 'fura-seat': return 'Чохли на сидіння в кабіні — видно з керма.';
    case 'fura-wheel': return 'Кермо в кабіні.';
    case 'fura-curtain': return 'Шторки на вікнах кабіни.';
  }
  return it.title;
}
