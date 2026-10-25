// Рейси «Чумацького краю» в браузері: стан — із сервера (вид гри fura, specs/fura.md «Етап 1»), тут — усе, що бачить
// і робить водій: дошка замовлень, причіп у боксі відправника, навігація, здача, підсумок; телеметрія для судді.
//
// Сервер приймає позу двома шляхами: `input` (до 4/с, судить, але не зберігає) і `act` (зберігає стан столу) — тому
// шлемо обидва: input щочверть секунди, act раз на 5 с і щойно зчепились/відчепились. Вид із сервера приходить лише з
// тика (≤ 250 мс після дії), тож після дії ми не чекаємо на вид, а показуємо відповідь одразу.
//
// Етап 2 (вид stage: 2) — колона: за столом до 8 фур. Поза 10/с з курсом тягача й прапорцями (сигнал, фари, стоп) —
// з неї сервер складає кадр привидів (ghosts.js). Список колони, колонне замовлення «k» на дошці й події (сів, пішов,
// узяли колонне, приєднався, бонус) — з різниці двох видів. Вид stage: 1 (старий сервер) — усе як було, без колони.

const POSE_MS = 250, POSE2_MS = 100, SAVE_MS = 5000;
const PHASE_ICON = { free: '📋', pickup: '🅿', haul: '🚚', done: '✅' };
const escH = (t) => String(t == null ? '' : t).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
const km = (m) => (m >= 1000 ? (m / 1000).toFixed(1).replace('.', ',') + ' км' : Math.round(m) + ' м');

export class Trips {
  constructor(o) {
    Object.assign(this, o);          // THREE, app, hud, level, truck, model, nav, server, say, boxModal, sound, onPlaced
    this.view = null;
    this.places = Object.fromEntries(this.level.places.map((p) => [p.id, p]));
    this.cargo = Object.fromEntries(this.level.map.cargo.map((c) => [c.id, c]));
    this.poseAt = 0; this.saveAt = 0; this.lastC = false; this.flags = 0;
    this.poseN = 0; this.stateAt = 0;  // A5: номер пози й мить стану фізики (мс performance.now()), з якої вона знята
    this.placed = '';                // для якого замовлення й боксу причіп уже стоїть у відправника
    this.restored = false; this.shownResult = 0; this.voidShown = '';
    this.busy = false;
    // Сховав вкладку довше ніж на 15 с — сервер не рахує цей час у рейс (пауза без телеметрії), тож і наш годинник
    // між видами має стояти: зсуваємо точку відліку на тривалість паузи.
    this.onVis = () => {
      if (document.hidden) { this.hiddenAt = performance.now(); return; }
      const gap = this.hiddenAt ? performance.now() - this.hiddenAt : 0;
      this.hiddenAt = 0;
      if (gap > 15000 && this.clock) this.clock.at += gap;
    };
    document.addEventListener('visibilitychange', this.onVis);

    const panel = document.createElement('div');
    panel.className = 'fura-panel fura-order';
    this.hud.appendChild(panel);
    this.panel = panel;
    const mm = document.createElement('canvas');
    mm.className = 'fura-minimap'; mm.width = mm.height = 200;
    this.hud.appendChild(mm);
    this.mm = mm;
    const cv = document.createElement('div');
    cv.className = 'fura-panel fura-convoy';
    cv.style.display = 'none';
    this.hud.appendChild(cv);
    this.cvPanel = cv;
  }

  // ---------------------------------------------------------------- стан
  get phase() { return this.view ? this.view.phase : 'free'; }
  get order() { return this.view && this.view.order; }
  get stage2() { return !!(this.view && this.view.stage >= 2); }
  /** Мій нік за столом — з колони за моїм місцем. */
  myNick() { const v = this.view, c = v && v.convoy && v.convoy.find((x) => x.seat === v.seat); return c ? c.nick : null; }
  /** Бокс відправника: у своєму замовленні — dock, у колонному — свій бокс із col.docks. */
  dockOf(o) {
    if (!o) return null;
    if (o.dock != null) return o.dock;
    const col = this.view && this.view.col, me = this.myNick();
    if (!col || !col.docks || !me) return null;
    const k = Object.keys(col.docks).find((n) => same(n, me));
    return k ? col.docks[k] : null;
  }
  /** Я в колонному рейсі? */
  inCol() { const col = this.view && this.view.col, me = this.myNick(); return !!(col && me && (col.members || []).some((n) => same(n, me))); }

  onView(v) {
    if (!v || v.stage == null) return;
    const before = this.view;
    this.view = v;
    if (this.ghosts) { this.ghosts.setMe(v.seat); this.ghosts.setConvoy(v.convoy); }
    // Сервер шле вид лише при змінах — час рейсу між видами рахуємо самі від останнього відомого
    this.clock = { sec: v.trip ? v.trip.sec : 0, haul: v.trip ? v.trip.haulSec : 0, at: performance.now() };
    if (!this.restored) {
      this.restored = true; this.restore(v);
      if (v.phase === 'free') setTimeout(() => this.voice.say('hello'), 1500);
      // перевідкрив гру з незакритим підсумком — показати мовчки: Глек цей рейс уже прокоментував
      if (v.phase === 'done' && v.result) { this.shownResult = v.result.trip; this.summary(v.result, true); }
    }
    else if (before && before.phase !== v.phase) this.onPhase(before.phase, v.phase);
    // нова дошка — наступне take, хай навіть на той самий бокс (відмовився й узяв колонне знову), ставить причіп наново
    if (v.phase === 'free') this.placed = '';
    // колонне: бокс стає відомий лише з виду (col.docks) — тоді й ставимо причіп
    if (v.phase === 'pickup' && v.order && this.placed !== this.placeKey(v.order) && this.dockOf(v.order) != null) this.putTrailerAtDock(v.order);
    this.target();
    if (v.trip && v.trip.voided && this.voidShown !== v.trip.voided) {
      this.voidShown = v.trip.voided;
      this.say('⚠ Цей рейс уже не зарахують: ' + v.trip.voided, 5000);
      this.voice.say('voided', { urgent: true });
    }
    if (v.phase === 'done' && v.result && this.shownResult !== v.result.trip) { this.shownResult = v.result.trip; this.summary(v.result); }
    if (v.stage >= 2) this.convoyEvents(before, v);
    this.bonusCheck(v, !before);                        // після підсумку: його фраза перебиває чергу, бонус — за нею
  }

  /** Бонус колони — з мого підсумку: result.bonus з 0 став > 0. Першому, хто здав, сервер дописує його, коли здає другий
   *  (і тією ж дією закриває колону — col: null), тож col.done тут не допоможе. Відкритий підсумок — лише рядок «Колона». */
  bonusCheck(v, first) {
    const r = v.phase === 'done' ? v.result : null;
    if (!r || !(r.bonus > 0) || this.bonusTrip === r.trip) return;
    this.bonusTrip = r.trip;
    const s = this.sum;
    if (s && s.m && s.m.open && s.trip === r.trip) {
      const tb = s.m.el.querySelector('table'), old = tb && tb.querySelector('.fura-colrow');
      if (old) old.outerHTML = this.colRow(r); else if (tb) tb.insertAdjacentHTML('beforeend', this.colRow(r));
    }
    if (first) return;                                  // перевідкрив гру з уже нарахованим бонусом — мовчки
    this.say('🎉 Колона здала разом — +' + r.bonus + ' 🏺 бонусу', 5000);
    this.voice.say('col_bonus');
  }

  /** Перший вид: поставити фуру й причіп туди, де лишились (перевідкрив гру посеред рейсу). */
  restore(v) {
    const t = this.truck, h = this.level.height, o = v.order, p = v.pose;
    if (o && v.phase !== 'pickup') this.placed = this.placeKey(o);
    if (v.phase === 'pickup' && o) {
      this.putTrailerAtDock(o);
      if (p) { t.tb.setTranslation({ x: p.x, y: h(p.x, p.z) + 1.4, z: p.z }, true); if (p.h != null) t.tb.setRotation(t.yawQuat(p.h), true); }
    } else if (v.phase === 'haul' && o && p) {
      this.dress(o);
      if (p.c) t.place({ tractor: { x: p.x, z: p.z }, trailer: { x: p.rx, z: p.rz, yaw: p.ry, y: h(p.rx, p.rz) } }, true);
      else t.place({ tractor: { x: p.x, z: p.z, yaw: p.h ?? p.ry, y: h(p.x, p.z) }, trailer: { x: p.rx, z: p.rz, yaw: p.ry, y: h(p.rx, p.rz) } }, false);
      t.cargo = v.trip ? v.trip.minK : 100;
      this.say('Рейс триває: ' + this.title(o), 3000);
    }
    // поза зі старої дороги (тепер поруч стінки відбійників) чи впритул до хати: фура стояла б носом у стіну — на смугу
    if (p && o && this.level.unstick) this.level.unstick(t);
    this.onPlaced && this.onPlaced();
    if (v.phase === 'free') setTimeout(() => this.board(), 500);
  }

  onPhase(from, to) {
    const o = this.order;
    if (to === 'haul' && o) {
      this.truck.cargo = 100; this.truck.settle();
      this.say('🔗 Причіп твій. Вези в ' + this.places[o.to].short + ', не розбий!', 3500);
      this.marks = {};                       // пів дороги / близько / запізнився — кожне раз за рейс
      const c = this.cargo[o.cargo];
      this.voice.say('coupled');
      if (c && c.fragile >= 1) this.voice.say('fragile');
      else if (c && c.fragile === 0) this.voice.say('sturdy');
    }
    if (to === 'free' && from !== 'done') this.say('Замовлення знято', 2500);
  }

  dispose() { document.removeEventListener('visibilitychange', this.onVis); }

  placeKey(o) { return o ? o.id + ':' + o.from + ':' + this.dockOf(o) : ''; }

  /** Колона з різниці двох видів: хто сів і пішов, колонне взяли, приєднались (бонус — bonusCheck). */
  convoyEvents(b, v) {
    const me = this.myNick();
    if (!b || b.stage < 2) return;                 // перший вид — без «приєднався» для всіх, хто вже сидить
    const was = (b.convoy || []).map((c) => c.nick), now = (v.convoy || []).map((c) => c.nick);
    for (const n of now) if (!same(n, me) && !was.some((x) => same(x, n))) { this.say('🚚 У колоні +1: <b>' + escH(n) + '</b>', 3500); this.voice.say('join', { gap: 4000 }); }
    for (const n of was) if (!same(n, me) && !now.some((x) => same(x, n))) { this.say(escH(n) + ' — уже не за столом, фура чекатиме', 3500); this.voice.say('leave', { gap: 4000 }); }
    const bc = b.col, vc = v.col;
    if (vc) {
      const sameCol = bc && bc.no === vc.no, bm = sameCol ? bc.members || [] : [];
      const fresh = (vc.members || []).filter((n) => !bm.some((x) => same(x, n)));
      const o = vc.order, where = o ? this.cargoLine(o) : '';
      if (!sameCol && fresh.length) {
        const who = fresh[0];
        if (same(who, me)) this.say('🚚 Колонне твоє: ' + where + '. Хто приєднається з дошки — їде з тобою', 4000);
        else this.say('🚚 Колонне від <b>' + escH(who) + '</b>: ' + where + ' — приєднуйся з дошки (+15 %)', 5000);
        this.voice.say('col_take');
        fresh.shift();
      }
      for (const n of fresh) {
        if (same(n, me)) this.say('⛓ Ти в колоні: ' + where, 3000);
        else this.say('⛓ У колонному +1: <b>' + escH(n) + '</b>', 3500);
        this.voice.say('col_join', { gap: 3000 });
      }
    }
  }

  cargoLine(o) { const c = this.cargo[o.cargo]; return (c ? c.emoji + ' ' + c.title : o.cargo) + ' → ' + (this.places[o.to] ? this.places[o.to].short : o.to); }

  title(o) { const c = this.cargo[o.cargo]; return (c ? c.emoji + ' ' + c.title : o.cargo) + ': ' + this.places[o.from].short + ' → ' + this.places[o.to].short; }

  /** Причіп на бокс відправника — відчеплений, на ногах, з вантажем цього замовлення. */
  putTrailerAtDock(o) {
    const t = this.truck, from = this.places[o.from], dock = this.dockOf(o), d = from.docks.find((x) => x.id === dock) || from.docks[0];
    this.placed = this.placeKey(o);
    if (t.coupled) t.unjoin();
    t.legsCol.setEnabled(true);
    const zero = { x: 0, y: 0, z: 0 };
    t.rb.setTranslation({ x: d.x, y: this.level.height(d.x, d.z) + 1.36, z: d.z }, true);
    t.rb.setRotation(t.yawQuat(d.yaw), true);
    t.rb.setLinvel(zero, true); t.rb.setAngvel(zero, true);
    t.settle();
    this.dress(o);
    this.onPlaced && this.onPlaced();
  }

  /** Вантаж: маса, крихкість (для датчика), вигляд причепа. */
  dress(o) {
    const c = this.cargo[o.cargo];
    if (!c) return;
    this.truck.setCargo(c.mass, c.fragile);
    this.model.setCargo(c);
  }

  /** Куди веде навігація: у pickup — бокс відправника, у haul — бокс-ціль отримувача. */
  target() {
    const o = this.order, ph = this.phase;
    if (ph === 'pickup' && o) {
      const dock = this.dockOf(o), d = this.places[o.from].docks.find((x) => x.id === dock);
      this.nav.setTarget(d ? { x: d.x, z: d.z } : null);
    } else if (ph === 'haul' && o) {
      const p = this.places[o.to], d = p.docks[0];
      this.nav.setTarget({ x: d.x, z: d.z });
    } else this.nav.setTarget(null);
  }

  // ---------------------------------------------------------------- дії на сервер
  async act(action, payload) {
    if (this.busy && action !== 'pose') return null;
    if (action !== 'pose') this.busy = true;
    try {
      const r = await this.server.tell(action, payload);
      if (r === null) { if (action !== 'pose') this.say('Нема зв\'язку з сервером — спробуй ще раз', 3000); return null; }
      if (action !== 'pose' && r.message) this.say((r.ok ? '' : '⚠ ') + r.message, r.ok ? 2800 : 4200);
      return r;
    } finally { if (action !== 'pose') this.busy = false; }
  }

  pose() {
    const t = this.truck, a = t.tb.translation(), r = t.rb.translation();
    const p = {
      x: +a.x.toFixed(2), z: +a.z.toFixed(2), v: +t.speed().toFixed(2), c: t.coupled,
      k: Math.round(this.phase === 'haul' ? t.cargo : 100),
      rx: +r.x.toFixed(2), rz: +r.z.toFixed(2), ry: +t.yawOf(t.rb).toFixed(3),
    };
    // етап 2: курс тягача (0 = +Z, π/2 = +X — так само рахує yawOf) і прапорці для привида: 1 сигнал, 2 фари, 4 стоп
    if (this.stage2) { p.h = +t.yawOf(t.tb).toFixed(3); p.f = this.flags; }
    // A5: мить знімка за годинником браузера (s, мс) і номер (n) — привид у друга малюється за часом знімка, а не за тим,
    // коли поза доїхала до сервера й у який його тик потрапила (specs/fura.md, «Плавні привиди (A5)»)
    if (this.stage2) { p.s = Math.round(this.stateAt || performance.now()); p.n = this.poseN++; }
    return p;
  }

  /** Телеметрія судді й кадру привидів — з кожного кадру малювання (HUD малюється раз на 66 мс, а треба рівно 10/с). */
  telemetry(now, inp, stateAt) {
    if (!this.view) return;
    // stateAt — мить, якій відповідає стан фізики (кадр мінус залишок кроку): поза знята саме з нього
    this.stateAt = Number.isFinite(stateAt) ? stateAt : now;
    const t = this.truck;
    this.flags = (inp && inp.horn ? 1 : 0) | (t.lights ? 2 : 0) | ((t.brakeNow || 0) > 0.05 ? 4 : 0);
    const every = this.stage2 ? POSE2_MS : POSE_MS;
    if (now - this.poseAt < every) return;
    // крок рівний: від розкладу, а не від останнього кадру (інакше 60 fps давали б 9,2/с замість 10)
    this.poseAt = now - this.poseAt < every * 2 ? this.poseAt + every : now;
    const p = this.pose();
    this.server.input('pose', p);
    if (this.perf) this.perf.input(false);
    if (now - this.saveAt >= SAVE_MS || p.c !== this.lastC) { this.saveAt = now; this.act('pose', p); if (this.perf) this.perf.input(true); }
    this.lastC = p.c;
  }

  /** Зона отримувача: двір +15 м або ≤ 25 м від боксу (так само судить сервер). */
  inDropZone() {
    const o = this.order;
    if (!o) return false;
    const p = this.places[o.to], r = this.truck.rb.translation(), y = p.yard;
    if (Math.abs(r.x - y.x) <= y.w / 2 + 15 && Math.abs(r.z - y.z) <= y.d / 2 + 15) return true;
    return p.docks.some((d) => Math.hypot(d.x - r.x, d.z - r.z) <= 25);
  }

  nearPickup(m) {
    const o = this.order;
    if (!o) return false;
    const dock = this.dockOf(o), d = this.places[o.from].docks.find((x) => x.id === dock), a = this.truck.tb.translation();
    return !!d && Math.hypot(d.x - a.x, d.z - a.z) <= m;
  }

  /** Кнопка «зчепити» (T/Ⓐ): у зоні отримувача з відчепленим причепом — це «Здати». true — з'їли натиск. */
  couplePress() {
    if (this.phase === 'haul' && !this.truck.coupled && this.inDropZone()) { this.deliver(); return true; }
    return false;
  }

  async deliver() {
    if (Math.abs(this.truck.speed()) > 0.5) { this.say('Спершу зупинись', 2000); return; }
    this.act('pose', this.pose());
    await this.act('deliver');
  }

  async take(id) {
    const r = await this.act('take', { id });
    if (r && r.ok) {
      const o = this.view.board && this.view.board.find((x) => x.id === id);
      // колонне: свій бокс скаже вид (col.docks) — причіп поставить onView
      if (o) { this.view = { ...this.view, phase: 'pickup', order: o }; if (this.dockOf(o) != null) this.putTrailerAtDock(o); this.target(); }
      if (id !== 'k') this.voice.say('take');        // колонному — свої col_take / col_join з виду
    }
  }

  async cancel() { const r = await this.act('cancel'); if (r && r.ok) this.voice.say('cancel'); }

  async next() { await this.act('next'); }

  /** Автозчеп: ставимо тягач під шворень плавно, щоб суддя не прийняв це за телепорт (стрибок ≤ 30 м за ~2 с). */
  async auto() {
    if (this.phase !== 'pickup') { this.say('Автозчеп — коли причіп чекає у відправника', 2500); return; }
    if (!this.nearPickup(30)) { this.say('Під\'їдь до боксу ближче — до 30 м', 2500); return; }
    const r = await this.act('auto');
    if (!r || !r.ok) return;
    this.truck.glideUnder(2.0, () => { this.act('pose', this.pose()); });
  }

  // ---------------------------------------------------------------- кадр
  /** Раз на кадр HUD (~15/с): телеметрія, мапа, панель. Повертає підказку {text, strong} — strong перекриває інші. */
  tick(now, inp) {
    if (!this.view) return null;
    const t = this.truck;
    const a = t.tb.translation();
    const route = this.nav.update(a.x, a.z, now);
    this.nav.draw(this.mm, a.x, a.z, t.yawOf(t.tb), undefined, this.ghosts ? this.ghosts.list() : null);
    this.drawPanel(route);
    this.drawConvoy(a);
    this.cues(route, now);
    // підказка
    const o = this.order, ph = this.phase;
    if (ph === 'pickup' && o && !t.coupled && this.nearPickup(30))
      return { text: 'Здай назад під причіп у боксі ' + this.dockOf(o) + ' · або ☰ → «🔗 Автозчеп» (−10 %)' };
    if (ph === 'haul' && o && this.inDropZone()) {
      if (t.coupled) return { text: 'Приїхав! Стань у бокс, зупинись і відчепись (<b>T</b> · <b>Ⓐ</b>)', strong: true };
      return { text: '✅ Тисни <b>T</b> · <b>Ⓐ</b> — здати вантаж', strong: true };
    }
    return null;
  }

  /** Голос на подіях рейсу, які сервер не рахує: пів дороги, близько, запізнився, стоїть без діла. */
  cues(route, now) {
    const o = this.order, ph = this.phase, m = this.marks || (this.marks = {});
    if (ph === 'haul' && o && route) {
      const km = o.km * 1000;
      if (!m.half && km > 600 && route.len < km * 0.5) { m.half = true; this.voice.say('half'); }
      if (!m.near && route.len < 300) { m.near = true; this.voice.say('near'); }
      const sec = this.clock ? this.clock.haul + (now - this.clock.at) / 1000 : 0;
      if (!m.late && sec > o.minutes * 60) { m.late = true; this.voice.say('late'); }
    }
    if (ph !== 'free') this.busyAt = now;
    else if (now - (this.busyAt || (this.busyAt = now)) > 120000) { this.busyAt = now; this.voice.say('idle', { gap: 180000 }); }
  }

  drawPanel(route) {
    const v = this.view, o = this.order, ph = this.phase;
    let html = '';
    if (ph === 'free') html = '<b>📋 Замовлення чекають</b><div class="muted">☰ / Start — дошка</div>';
    else if (o) {
      const c = this.cargo[o.cargo];
      // термін — на рейс із вантажем (від зчеплення), як судить сервер
      const run = ph === 'haul' && this.clock ? (performance.now() - this.clock.at) / 1000 : 0;
      const sec = ph === 'haul' ? (this.clock ? this.clock.haul : 0) + run : 0, lim = o.minutes * 60;
      const mmss = (s) => Math.floor(s / 60) + ':' + String(Math.floor(s % 60)).padStart(2, '0');
      const late = sec > lim;
      html = '<div class="fura-o1">' + (c ? c.emoji : '') + ' <b>' + (c ? c.title : o.cargo) + '</b> · ' + this.places[o.from].short + ' → ' + this.places[o.to].short + '</div>'
        + '<div class="fura-o2">' + (ph === 'pickup' ? '🅿 забрати: <b>' + this.places[o.from].short + ', бокс ' + (this.dockOf(o) ?? '…') + '</b>' : ph === 'haul' ? '🏁 везти в <b>' + this.places[o.to].short + '</b>' : '✅ здано')
        + (route ? ' · ' + (route.len >= 1000 ? (route.len / 1000).toFixed(1).replace('.', ',') + ' км' : Math.round(route.len) + ' м') : '') + '</div>'
        + '<div class="fura-o3"><span class="' + (late ? 'bad' : '') + '">⏱ ' + (ph === 'haul' ? mmss(sec) + ' / ' + mmss(lim) : 'термін ' + o.minutes + ' хв — від зчеплення') + '</span> · ~' + o.pay + ' 🏺'
        + (v.trip && v.trip.auto ? ' · автозчеп' : '') + (c && c.fragile === 0 ? ' · не б\'ється' : '') + (this.inCol() ? ' · ⛓ колонне +15 %' : '') + '</div>';
    }
    if (this.panel._html !== html) { this.panel.innerHTML = html; this.panel._html = html; }
    this.panel.style.display = html ? '' : 'none';
  }

  /** Колона: хто за столом, що везе й куди, як далеко від мене; колонне — зверху. Лише коли за столом не сам. */
  drawConvoy(me) {
    const v = this.view, list = (v && v.stage >= 2 && v.convoy) || [];
    let html = '';
    if (list.length > 1) {
      const rows = list.filter((c) => c.seat !== v.seat).map((c) => {
        const p = this.ghosts && this.ghosts.poseOf(c.seat);
        const what = c.cargo ? (this.cargo[c.cargo] ? this.cargo[c.cargo].emoji : '📦') + ' → ' + (this.places[c.to] ? this.places[c.to].short : c.to || '?') : 'без замовлення';
        return '<div class="fura-cv-row"><i style="background:' + (this.ghosts ? this.ghosts.colorOf(c.seat) : '#6ba7f2') + '"></i>'
          + '<b>' + escH(c.nick) + '</b>' + (c.col ? ' ⛓' : '') + ' <span class="w">' + (PHASE_ICON[c.phase] || '') + ' ' + what + '</span>'
          + '<span class="d">' + (p ? km(Math.hypot(p.x - me.x, p.z - me.z)) : '—') + '</span></div>';
      }).join('');
      const col = v.col;
      const head = col && col.order ? '<div class="fura-cv-col">⛓ Колонне: ' + this.cargoLine(col.order) + ' · ' + (col.members || []).length + ' фур'
        + (col.done && col.done.length ? ' · здали ' + col.done.length : '') + '</div>' : '';
      html = '<b>🚚 Колона · ' + list.length + '</b>' + head + rows;
    }
    if (this.cvPanel._html !== html) { this.cvPanel.innerHTML = html; this.cvPanel._html = html; }
    this.cvPanel.style.display = html ? '' : 'none';
  }

  // ---------------------------------------------------------------- вікна
  board() {
    const v = this.view;
    if (!v) return;
    if (v.phase === 'done' && v.result) return this.summary(v.result, true);
    if (v.phase !== 'free') return this.current();
    const list = v.board || [];
    const rows = list.map((o) => {
      const c = this.cargo[o.cargo] || {};
      const frag = c.fragile >= 1.2 ? '💥 дуже крихке' : c.fragile >= 0.9 ? '⚠ крихке' : c.fragile > 0 ? 'обережно' : 'не б\'ється';
      // колонне (етап 2): одне на стіл, хто вже в ньому — видно; бокс кожному свій
      const k = o.id === 'k', mem = k && v.col && v.col.members && v.col.members.length ? v.col.members : null;
      const colLine = !k ? '' : '<br><span class="fura-ord-k">' + (mem ? '🚚 приєднатися (' + mem.map(escH).join(', ') + ')' : '🚚 почати колону — +15 % тим, хто здасть разом') + '</span>';
      return '<button type="button" class="fura-ord' + (k ? ' fura-ord-col' : '') + '" data-id="' + o.id + '"><span class="e">' + (c.emoji || '📦') + '</span>'
        + '<span class="t"><b>' + (k ? '⛓ Колонне: ' : '') + (c.title || o.cargo) + '</b><br>' + this.places[o.from].short + ' → ' + this.places[o.to].short
        + '<br><small>' + String(o.km).replace('.', ',') + ' км · ' + o.minutes + ' хв · ' + frag + '</small>' + colLine + '</span>'
        + '<span class="p">~' + o.pay + ' 🏺' + (k ? '<small>+15 %</small>' : '') + '</span></button>';
    }).join('');
    this.boxModal('<h3>📋 Дошка замовлень</h3><p class="muted">Причіп забираєш у відправника сам. Плата — коли довезеш; ціле менше — менше черепків. Денна стеля — ' + v.cap + ' 🏺.</p>'
      + '<div class="fura-ords">' + rows + '</div><div class="fura-row"><button type="button" class="close">Покататись без замовлення</button></div>', (el, m) => {
      el.addEventListener('click', (e) => {
        const b = e.target.closest('.fura-ord');
        if (b) { m.close(); this.take(b.dataset.id === 'k' ? 'k' : +b.dataset.id); }
        else if (e.target.closest('.close')) m.close();
      });
    });
  }

  current() {
    const o = this.order;
    if (!o) return;
    this.boxModal('<h3>🚚 ' + this.title(o) + '</h3>'
      + '<p>' + (this.phase === 'pickup' ? 'Причіп чекає: <b>' + this.places[o.from].title + ', бокс ' + (this.dockOf(o) ?? '…') + '</b>.' : 'Вези в <b>' + this.places[o.to].title + '</b> — заїдь у двір, зупинись, відчепись і здай.') + '</p>'
      + '<p class="muted">' + String(o.km).replace('.', ',') + ' км · термін ' + o.minutes + ' хв · ~' + o.pay + ' 🏺</p>'
      + '<div class="fura-row">' + (this.phase === 'pickup' ? '<button type="button" class="main auto">🔗 Автозчеп (−10 %)</button>' : '')
      + '<button type="button" class="go">Їхати далі</button><button type="button" class="cancel">✖ Відмовитись від замовлення</button></div>', (el, m) => {
      el.addEventListener('click', (e) => {
        if (e.target.closest('.auto')) { m.close(); this.auto(); }
        else if (e.target.closest('.cancel')) { m.close(); this.cancel(); }
        else if (e.target.closest('.go')) m.close();
      });
    });
  }

  /** Рядок «Колона» в підсумку: бонус уже нараховано (r.bonus, черепки × Boost) чи ще чекає, щоб хтось здав у вікні 5 хв. */
  colRow(r) {
    const row = (a, b) => '<tr class="fura-colrow"><td>' + a + '</td><td>' + b + '</td></tr>';
    if (r.bonus > 0) return row('Колона', '<b>+' + r.bonus + ' 🏺</b> — здали разом, +15 %');
    if (!r.col) return '';
    const col = this.view && this.view.col;
    const t0 = col && col.firstAt != null ? (typeof col.firstAt === 'number' ? col.firstAt : Date.parse(col.firstAt)) : NaN;
    const until = Number.isFinite(t0) ? new Date(t0 + 5 * 60000).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' }) : '';
    return row('Колона', col ? '+15 %, якщо ще хтось із колони здасть' + (until ? ' до ' + until : ' за 5 хв') : 'разом не здали — без бонусу');
  }

  summary(r, quiet) {
    const row = (a, b) => '<tr><td>' + a + '</td><td>' + b + '</td></tr>';
    const mmss = (s) => Math.floor(s / 60) + ':' + String(Math.floor(s % 60)).padStart(2, '0');
    const c = this.cargo[r.cargo] || {};
    if (!quiet) this.voice.say(r.broken ? 'done_broken' : r.k >= 95 ? 'done_good' : 'done_ok', { urgent: true });
    if (!quiet && r.exact) this.voice.say('exact');
    this.sum = { trip: r.trip, m: null };
    this.sum.m = this.boxModal('<h3>' + (r.broken ? '💔 Усе розбито' : '✅ Рейс №' + r.trip + ' здано') + '</h3>'
      + '<table>' + row('Вантаж', (c.emoji || '') + ' ' + (c.title || r.cargo)) + row('Маршрут', this.places[r.from].short + ' → ' + this.places[r.to].short)
      + row('Відстань', String(r.km).replace('.', ',') + ' км (проїхав ' + String(r.driven).replace('.', ',') + ')')
      + row('Час', mmss(r.sec) + ' з ' + r.minutes + ':00' + (r.onTime ? ' — вчасно, +10 %' : ' — запізнився'))
      + row('Ціле', r.k + ' %') + row('У бокс', r.exact ? 'точно, +10 %' : 'не в бокс')
      + (r.auto ? row('Автозчеп', '−10 %') : '')
      + row('Черепки', '<b>+' + r.shards + ' 🏺</b>') + this.colRow(r) + '</table>'
      + '<p class="muted small">Денна стеля рейсів — ' + this.view.cap + ' 🏺: понад неї рейс зараховано, але черепків нема.</p>'
      + '<div class="fura-row"><button type="button" class="main next">Далі — нові замовлення</button></div>', (el, m) => {
      el.querySelector('.next').addEventListener('click', async () => { m.close(); await this.next(); setTimeout(() => this.board(), 400); });
    });
  }
}
