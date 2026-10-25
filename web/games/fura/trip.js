// Рейси «Чумацького краю» в браузері: стан — із сервера (вид гри fura, specs/fura.md «Етап 1»), тут — усе, що бачить
// і робить водій: дошка замовлень, причіп у боксі відправника, навігація, здача, підсумок; телеметрія для судді.
//
// Сервер приймає позу двома шляхами: `input` (до 4/с, судить, але не зберігає) і `act` (зберігає стан столу) — тому
// шлемо обидва: input щочверть секунди, act раз на 5 с і щойно зчепились/відчепились. Вид із сервера приходить лише з
// тика (≤ 250 мс після дії), тож після дії ми не чекаємо на вид, а показуємо відповідь одразу.

const POSE_MS = 250, SAVE_MS = 5000;

export class Trips {
  constructor(o) {
    Object.assign(this, o);          // THREE, app, hud, level, truck, model, nav, server, say, boxModal, sound, onPlaced
    this.view = null;
    this.places = Object.fromEntries(this.level.places.map((p) => [p.id, p]));
    this.cargo = Object.fromEntries(this.level.map.cargo.map((c) => [c.id, c]));
    this.poseAt = 0; this.saveAt = 0; this.lastC = false;
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
  }

  // ---------------------------------------------------------------- стан
  get phase() { return this.view ? this.view.phase : 'free'; }
  get order() { return this.view && this.view.order; }

  onView(v) {
    if (!v || v.stage == null) return;
    const before = this.view;
    this.view = v;
    // Сервер шле вид лише при змінах — час рейсу між видами рахуємо самі від останнього відомого
    this.clock = { sec: v.trip ? v.trip.sec : 0, haul: v.trip ? v.trip.haulSec : 0, at: performance.now() };
    if (!this.restored) {
      this.restored = true; this.restore(v);
      if (v.phase === 'free') setTimeout(() => this.voice.say('hello'), 1500);
      // перевідкрив гру з незакритим підсумком — показати мовчки: Глек цей рейс уже прокоментував
      if (v.phase === 'done' && v.result) { this.shownResult = v.result.trip; this.summary(v.result, true); }
    }
    else if (before && before.phase !== v.phase) this.onPhase(before.phase, v.phase);
    this.target();
    if (v.trip && v.trip.voided && this.voidShown !== v.trip.voided) {
      this.voidShown = v.trip.voided;
      this.say('⚠ Цей рейс уже не зарахують: ' + v.trip.voided, 5000);
      this.voice.say('voided', { urgent: true });
    }
    if (v.phase === 'done' && v.result && this.shownResult !== v.result.trip) { this.shownResult = v.result.trip; this.summary(v.result); }
  }

  /** Перший вид: поставити фуру й причіп туди, де лишились (перевідкрив гру посеред рейсу). */
  restore(v) {
    const t = this.truck, h = this.level.height, o = v.order, p = v.pose;
    if (v.phase === 'pickup' && o) {
      this.putTrailerAtDock(o);
      if (p) t.tb.setTranslation({ x: p.x, y: h(p.x, p.z) + 1.4, z: p.z }, true);
    } else if (v.phase === 'haul' && o && p) {
      this.dress(o);
      if (p.c) t.place({ tractor: { x: p.x, z: p.z }, trailer: { x: p.rx, z: p.rz, yaw: p.ry, y: h(p.rx, p.rz) } }, true);
      else t.place({ tractor: { x: p.x, z: p.z, yaw: p.ry, y: h(p.x, p.z) }, trailer: { x: p.rx, z: p.rz, yaw: p.ry, y: h(p.rx, p.rz) } }, false);
      t.cargo = v.trip ? v.trip.minK : 100;
      this.say('Рейс триває: ' + this.title(o), 3000);
    }
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

  title(o) { const c = this.cargo[o.cargo]; return (c ? c.emoji + ' ' + c.title : o.cargo) + ': ' + this.places[o.from].short + ' → ' + this.places[o.to].short; }

  /** Причіп на бокс відправника — відчеплений, на ногах, з вантажем цього замовлення. */
  putTrailerAtDock(o) {
    const t = this.truck, from = this.places[o.from], d = from.docks.find((x) => x.id === o.dock) || from.docks[0];
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
      const d = this.places[o.from].docks.find((x) => x.id === o.dock);
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
    return {
      x: +a.x.toFixed(2), z: +a.z.toFixed(2), v: +t.speed().toFixed(2), c: t.coupled,
      k: Math.round(this.phase === 'haul' ? t.cargo : 100),
      rx: +r.x.toFixed(2), rz: +r.z.toFixed(2), ry: +t.yawOf(t.rb).toFixed(3),
    };
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
    const d = this.places[o.from].docks.find((x) => x.id === o.dock), a = this.truck.tb.translation();
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
      if (o) { this.view = { ...this.view, phase: 'pickup', order: o }; this.putTrailerAtDock(o); this.target(); }
      this.voice.say('take');
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
    if (now - this.poseAt >= POSE_MS) {
      this.poseAt = now;
      const p = this.pose();
      this.server.input('pose', p);
      if (now - this.saveAt >= SAVE_MS || p.c !== this.lastC) { this.saveAt = now; this.act('pose', p); }
      this.lastC = p.c;
    }
    const a = t.tb.translation();
    const route = this.nav.update(a.x, a.z, now);
    this.nav.draw(this.mm, a.x, a.z, t.yawOf(t.tb));
    this.drawPanel(route);
    this.cues(route, now);
    // підказка
    const o = this.order, ph = this.phase;
    if (ph === 'pickup' && o && !t.coupled && this.nearPickup(30))
      return { text: 'Здай назад під причіп у боксі ' + o.dock + ' · або ☰ → «🔗 Автозчеп» (−10 %)' };
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
        + '<div class="fura-o2">' + (ph === 'pickup' ? '🅿 забрати: <b>' + this.places[o.from].short + ', бокс ' + o.dock + '</b>' : ph === 'haul' ? '🏁 везти в <b>' + this.places[o.to].short + '</b>' : '✅ здано')
        + (route ? ' · ' + (route.len >= 1000 ? (route.len / 1000).toFixed(1).replace('.', ',') + ' км' : Math.round(route.len) + ' м') : '') + '</div>'
        + '<div class="fura-o3"><span class="' + (late ? 'bad' : '') + '">⏱ ' + (ph === 'haul' ? mmss(sec) + ' / ' + mmss(lim) : 'термін ' + o.minutes + ' хв — від зчеплення') + '</span> · ~' + o.pay + ' 🏺'
        + (v.trip && v.trip.auto ? ' · автозчеп' : '') + (c && c.fragile === 0 ? ' · не б\'ється' : '') + '</div>';
    }
    if (this.panel._html !== html) { this.panel.innerHTML = html; this.panel._html = html; }
    this.panel.style.display = html ? '' : 'none';
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
      return '<button type="button" class="fura-ord" data-id="' + o.id + '"><span class="e">' + (c.emoji || '📦') + '</span>'
        + '<span class="t"><b>' + (c.title || o.cargo) + '</b><br>' + this.places[o.from].short + ' → ' + this.places[o.to].short
        + '<br><small>' + String(o.km).replace('.', ',') + ' км · ' + o.minutes + ' хв · ' + frag + '</small></span>'
        + '<span class="p">~' + o.pay + ' 🏺</span></button>';
    }).join('');
    this.boxModal('<h3>📋 Дошка замовлень</h3><p class="muted">Причіп забираєш у відправника сам. Плата — коли довезеш; ціле менше — менше черепків. Денна стеля — ' + v.cap + ' 🏺.</p>'
      + '<div class="fura-ords">' + rows + '</div><div class="fura-row"><button type="button" class="close">Покататись без замовлення</button></div>', (el, m) => {
      el.addEventListener('click', (e) => {
        const b = e.target.closest('.fura-ord');
        if (b) { m.close(); this.take(+b.dataset.id); }
        else if (e.target.closest('.close')) m.close();
      });
    });
  }

  current() {
    const o = this.order;
    if (!o) return;
    this.boxModal('<h3>🚚 ' + this.title(o) + '</h3>'
      + '<p>' + (this.phase === 'pickup' ? 'Причіп чекає: <b>' + this.places[o.from].title + ', бокс ' + o.dock + '</b>.' : 'Вези в <b>' + this.places[o.to].title + '</b> — заїдь у двір, зупинись, відчепись і здай.') + '</p>'
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

  summary(r, quiet) {
    const row = (a, b) => '<tr><td>' + a + '</td><td>' + b + '</td></tr>';
    const mmss = (s) => Math.floor(s / 60) + ':' + String(Math.floor(s % 60)).padStart(2, '0');
    const c = this.cargo[r.cargo] || {};
    if (!quiet) this.voice.say(r.broken ? 'done_broken' : r.k >= 95 ? 'done_good' : 'done_ok', { urgent: true });
    if (!quiet && r.exact) this.voice.say('exact');
    this.boxModal('<h3>' + (r.broken ? '💔 Усе розбито' : '✅ Рейс №' + r.trip + ' здано') + '</h3>'
      + '<table>' + row('Вантаж', (c.emoji || '') + ' ' + (c.title || r.cargo)) + row('Маршрут', this.places[r.from].short + ' → ' + this.places[r.to].short)
      + row('Відстань', String(r.km).replace('.', ',') + ' км (проїхав ' + String(r.driven).replace('.', ',') + ')')
      + row('Час', mmss(r.sec) + ' з ' + r.minutes + ':00' + (r.onTime ? ' — вчасно, +10 %' : ' — запізнився'))
      + row('Ціле', r.k + ' %') + row('У бокс', r.exact ? 'точно, +10 %' : 'не в бокс')
      + (r.auto ? row('Автозчеп', '−10 %') : '')
      + row('Черепки', '<b>+' + r.shards + ' 🏺</b>') + '</table>'
      + '<p class="muted small">Денна стеля рейсів — ' + this.view.cap + ' 🏺: понад неї рейс зараховано, але черепків нема.</p>'
      + '<div class="fura-row"><button type="button" class="main next">Далі — нові замовлення</button></div>', (el, m) => {
      el.querySelector('.next').addEventListener('click', async () => { m.close(); await this.next(); setTimeout(() => this.board(), 400); });
    });
  }
}
