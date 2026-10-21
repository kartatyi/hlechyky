/*
  «🧭 Довкола мене» — window.HDovkola (21.10.2026, docs/tryvoga.md): циферблат поруч із мапою Тривоги. Кільця
  15/30/45 км (або 50/100/150) довкола мене: що летить, звідки, куди — і чи на мене. До 21.10 звалось «Півник».

  Де я: GPS браузера (рахується лише тут — на сервер не йде й ніде не зберігається), точка «📌 Я тут» з мапи
  (localStorage цього браузера) або місто мого району. Цілі — той самий знімок NEPTUN, що й на мапі (tryvoga.js
  тягне /api/tryvoga/threats раз на 10 с, поки сторінку видно). З курсу й швидкості цілі — найближче проходження:
  «летить на тебе» чи «пройде за N км» і за скільки хвилин.

  Зони кольором: зелена — тихо, жовта — тривога в районі, червона — ціль у цьому колі. Стан одним рядком іде в картку
  статусу згори сторінки (host.near), тут — циферблат, звідки рахуємо й перелік. Компас телефону крутить циферблат;
  звук — тумблером, лише поки сторінка відкрита (системних сповіщень нема: рішення Тривоги — «лише плашка»).
*/
(() => {
  'use strict';

  const KEY = 'dovkola';                // localStorage: { src, sound, compass, big, here: { lat, lon } }
  const OLD_KEY = 'pivnyk';             // до 21.10: { gps, sound, compass, big, here } — переносимо раз
  const SCALES = { small: [15, 30, 45], big: [50, 100, 150] };
  const FAR = 3;                        // далі за зовнішнє кільце, але ближче за FAR × його — рискою на краю
  const ON_ME_KM = 10;                  // пройде ближче — «летить на тебе» (похибка місця в NEPTUN 8–13 км)
  const DANGER_KM = 15;                 // ...ближче й за годину — червона стрілка
  const OLD_MS = 15 * 60000;            // ціль не оновлювалась 15 хв — бліда, не рахується
  const EARTH_KM = 6371;
  const RAD = Math.PI / 180;
  // Швидкість, коли NEPTUN її не дав, км/год — лише щоб сказати «≈ за скільки хвилин».
  const SPEED = { uav: 170, fpv: 80, missile: 750, ballistic: 2500, kab: 400, mig31k: 900, recon: 150, unknown: 170 };
  const SIDES = ['північ', 'пн-схід', 'схід', 'пд-схід', 'південь', 'пд-захід', 'захід', 'пн-захід'];

  // господар — tryvoga.js: { esc, map, threats, region, alarm, oblastName, types, glyph, kick, focus, near, narrow, tab }
  let h = null;
  let root = null;
  let opt = load();
  // компас є лише в телефона й планшета: на ПК кнопка тільки заважала б
  const touch = !!(window.matchMedia && matchMedia('(pointer: coarse)').matches);
  const pv = {
    cv: null, g: null, wrap: null, size: 0, dpr: 1, raf: 0, ro: null, css: {},
    shown: false, watch: null, fix: null, reveal: false, msg: '',
    heading: null, hx: 1, hy: 0, abs: false, cmpT: 0, listening: false,
    list: [], zones: new Map(), onMe: new Set(), baseline: false, anchor: null, ac: null, beepAt: 0,
  };

  const $ = (sel) => root && root.querySelector(sel);
  const esc = (s) => (h ? h.esc(s) : String(s));

  // ---------------------------------------------------------------------------------------------
  // налаштування (лише цей браузер)
  // ---------------------------------------------------------------------------------------------

  function load() {
    const read = (k) => { try { return JSON.parse(localStorage.getItem(k) || 'null'); } catch { return null; } };
    const o = read(KEY) || read(OLD_KEY) || {};
    const here = o.here && Number.isFinite(o.here.lat) && Number.isFinite(o.here.lon)
      && Math.abs(o.here.lat) <= 90 && Math.abs(o.here.lon) <= 180 ? { lat: o.here.lat, lon: o.here.lon } : null;
    // звідки рахуємо: старе { gps: true } — це GPS, а точка з мапи без GPS — точка
    let src = o.src === 'gps' || o.src === 'here' || o.src === 'raion' ? o.src : o.gps ? 'gps' : here ? 'here' : 'raion';
    if (src === 'here' && !here) src = 'raion';
    return { src, sound: !!o.sound, compass: !!o.compass, big: !!o.big, here };
  }
  function save() {
    try { localStorage.setItem(KEY, JSON.stringify(opt)); localStorage.removeItem(OLD_KEY); } catch { /* приватне вікно */ }
  }

  const rings = () => (opt.big ? SCALES.big : SCALES.small);

  // ---------------------------------------------------------------------------------------------
  // де я
  // ---------------------------------------------------------------------------------------------

  let townCache = { key: null, map: null, v: null };
  let probe = null;
  /// Місто мого району (обласний центр усередині нього), а нема — середина району. Для Житомирського — Житомир.
  function town() {
    const m = h && h.map();
    if (!m || !m.unproject) return null;
    const r = h.region();
    if (townCache.key === r.key && townCache.map === m) return townCache.v;
    const u = m.byKey[r.key];
    let v = null;
    if (u) {
      probe = probe || document.createElement('canvas').getContext('2d');
      const t = (m.towns || []).find((c) => probe.isPointInPath(u.p, c.x, c.y, 'evenodd'));
      const [lat, lon] = m.unproject(t ? t.x : u.c[0], t ? t.y : u.c[1]);
      v = { lat, lon, name: t ? t.name : u.n, city: !!t };
    }
    townCache = { key: r.key, map: m, v };
    return v;
  }

  /// Від якої точки рахуємо: обране джерело, а поки GPS мовчить — місто району. null — мапа ще не завантажилась.
  function me() {
    if (opt.src === 'gps' && pv.fix) return { lat: pv.fix.lat, lon: pv.fix.lon, src: 'gps', acc: pv.fix.acc, at: pv.fix.at };
    if (opt.src === 'here' && opt.here) return { lat: opt.here.lat, lon: opt.here.lon, src: 'here' };
    const t = town();
    return t ? { lat: t.lat, lon: t.lon, src: 'raion', name: t.name, city: t.city } : null;
  }

  function gpsStart() {
    if (pv.watch != null || opt.src !== 'gps') return;
    if (!navigator.geolocation) { gpsFail('Цей браузер не дає місце — обери «📌 Точка» і торкнись мапи.'); return; }
    pv.watch = navigator.geolocation.watchPosition((p) => {
      pv.fix = { lat: p.coords.latitude, lon: p.coords.longitude, acc: p.coords.accuracy, at: p.timestamp || Date.now() };
      say('');
      moved();
    }, (e) => {
      if (e.code === 1) gpsFail('Браузер не дав місце. Дозволь сайту «Місцезнаходження» або обери «📌 Точка».');
      else if (!pv.fix) say('GPS поки мовчить — рахую від ' + townName() + '.');
    }, { enableHighAccuracy: false, maximumAge: 60000, timeout: 30000 });
  }
  function gpsStop() {
    if (pv.watch != null && navigator.geolocation) navigator.geolocation.clearWatch(pv.watch);
    pv.watch = null;
  }
  function gpsFail(text) {
    gpsStop();
    opt.src = 'raion';
    pv.fix = null;
    save();
    say(text);
    moved();
  }
  function townName() {
    const t = town();
    return t ? t.name : 'твого району';
  }

  /// «Рахую від»: місто району, GPS чи точка з мапи. Точки ще нема — підказуємо, як її поставити.
  function setSrc(s) {
    say('');
    if (s === 'here' && (!opt.here || opt.src === 'here')) {
      say(opt.here ? 'Інша точка: торкнись мапи там, де ти, і натисни «📌 Я тут».' : 'Торкнись мапи там, де ти, і натисни «📌 Я тут».');
      if (h.narrow()) h.tab('map');
      if (!opt.here) return;
    }
    if (opt.src === s) return;
    opt.src = s;
    if (s === 'gps') gpsStart(); else { gpsStop(); pv.fix = null; }
    save();
    moved();
  }

  /// «📌 Я тут» з мапи: тепер рахуємо від цієї точки (GPS вимикаємо — щоб було видно, від чого рахуємо).
  function setHere(lat, lon) {
    if (!Number.isFinite(lat) || !Number.isFinite(lon)) return;
    opt.here = { lat: +lat.toFixed(5), lon: +lon.toFixed(5) };
    opt.src = 'here';
    gpsStop();
    pv.fix = null;
    save();
    say('');
    moved();
    if (h && h.narrow()) h.tab('near');
  }

  /// Точка чи масштаб змінились — перемалювати й циферблат, і кільце на мапі.
  function moved() {
    update();
    if (h) h.kick();
  }

  /// Я пересів (інша точка, інше джерело, понад 2 км GPS) чи інший масштаб — зони рахуємо наново без писку:
  /// ціль не «зайшла» в коло, це коло переїхало.
  function anchor(m) {
    const a = pv.anchor;
    if (a && m && a.src === m.src && a.big === opt.big && distKm(a.lat, a.lon, m.lat, m.lon) <= 2) return;
    pv.anchor = m ? { lat: m.lat, lon: m.lon, src: m.src, big: opt.big } : null;
    pv.zones.clear();
    pv.onMe.clear();
    pv.baseline = false;
  }

  // ---------------------------------------------------------------------------------------------
  // геометрія
  // ---------------------------------------------------------------------------------------------

  function distKm(la1, lo1, la2, lo2) {
    const p1 = la1 * RAD, p2 = la2 * RAD, dp = p2 - p1, dl = (lo2 - lo1) * RAD;
    const a = Math.sin(dp / 2) ** 2 + Math.cos(p1) * Math.cos(p2) * Math.sin(dl / 2) ** 2;
    return 2 * EARTH_KM * Math.asin(Math.min(1, Math.sqrt(a)));
  }
  /// Азимут від мене на ціль: 0 — північ, 90 — схід.
  function bearing(la1, lo1, la2, lo2) {
    const p1 = la1 * RAD, p2 = la2 * RAD, dl = (lo2 - lo1) * RAD;
    const y = Math.sin(dl) * Math.cos(p2);
    const x = Math.cos(p1) * Math.sin(p2) - Math.sin(p1) * Math.cos(p2) * Math.cos(dl);
    return (Math.atan2(y, x) / RAD + 360) % 360;
  }
  const side = (b) => SIDES[Math.round(b / 45) % 8];

  /// Найближче проходження по прямій за курсом: на якій відстані (km) і за скільки хвилин (min).
  /// along ≤ 0 — найближча точка вже позаду: віддаляється. Плоска сітка довкола мене — на сотні км досить.
  function passOf(m, t) {
    if (t.heading == null || t.areaOnly) return null;
    const e = (t.lon - m.lon) * RAD * EARTH_KM * Math.cos(m.lat * RAD);
    const n = (t.lat - m.lat) * RAD * EARTH_KM;
    const hr = t.heading * RAD, ve = Math.sin(hr), vn = Math.cos(hr);
    const along = -(e * ve + n * vn);
    if (along <= 0) return { away: true };
    const guess = !(t.speed > 0);
    const sp = guess ? SPEED[t.type] || SPEED.unknown : t.speed;
    return { km: Math.abs(e * vn - n * ve), along, min: (along / sp) * 60, guess };
  }

  /// Кожна ціль відносно мене: відстань, азимут, зона, проходження, слід — щоб малювати без перерахунку при повороті.
  function look(m) {
    const th = h && h.threats();
    const list = [];
    if (!m || !th || th.stale) return list;
    const now = Date.now();
    const rs = rings();
    for (const t of th.threats || []) {
      if (t.areaOnly) continue;
      const d = distKm(m.lat, m.lon, t.lat, t.lon);
      if (d > rs[2] * FAR) continue;
      const old = now - (Date.parse(t.updated) || now) > OLD_MS;
      const pass = passOf(m, t);
      const live = !t.advisory && !old;
      const zone = d <= rs[0] ? 0 : d <= rs[1] ? 1 : d <= rs[2] ? 2 : 3;
      const danger = live && !!pass && !pass.away && pass.km <= DANGER_KM && pass.min <= 60;
      const trail = d <= rs[2] * 1.5 && t.trail ? t.trail.map(([la, lo]) => [distKm(m.lat, m.lon, la, lo), bearing(m.lat, m.lon, la, lo)]) : [];
      list.push({ t, d, b: bearing(m.lat, m.lon, t.lat, t.lon), zone, live, old, pass, danger, trail });
    }
    list.sort((a, b) => a.d - b.d);
    return list;
  }

  // ---------------------------------------------------------------------------------------------
  // оновлення: стан, перелік, звук
  // ---------------------------------------------------------------------------------------------

  function update() {
    if (!root) return;
    const m = me();
    anchor(m);
    pv.list = look(m);
    listen();
    paintState(m);
    paintControls();
    paintWhere(m);
    paintList();
    kick();
  }

  /// Одним рядком — для картки статусу згори: найгірше, що зараз довкола мене.
  function stateOf() {
    const al = h.alarm();
    const th = h.threats();
    if (al.off) return null;
    if (al.stale || (th && th.stale)) return { cls: 'stale', ico: '❔', text: '<b>Не знаю, що в небі</b> — дані застаріли' };
    const rs = rings();
    const inside = pv.list.filter((x) => x.live && x.zone < 3);
    const more = inside.length > 1 ? ` · у колі ${rs[2]} км їх ${inside.length}` : '';
    // найнебезпечніша — та, що найшвидше долетить до мене; нема такої — найближча
    const coming = pv.list.filter((x) => x.danger).sort((a, b) => a.pass.min - b.pass.min)[0];
    if (coming) {
      const what = coming.pass.km <= ON_ME_KM ? 'Летить на тебе' : 'Летить у твій бік';
      const by = coming.pass.km <= ON_ME_KM ? '' : `, пройде за ${Math.round(coming.pass.km)} км`;
      // колір — за найгіршим: хай найшвидша ще далеко, а в колі вже хтось є — червоний
      const hot = coming.zone < 3 || inside.length > 0;
      return { cls: hot ? 'hot' : 'coming', ico: hot ? '🟥' : '🟧',
        text: `<b>${what}</b> — ${esc(label(coming.t))} за ${Math.round(coming.d)} км, ${side(coming.b)}${by}, ${eta(coming.pass)}${more}` };
    }
    const near = inside[0];
    if (near) return { cls: 'hot', ico: '🟥', text: `<b>Ціль за ${Math.round(near.d)} км</b> — ${side(near.b)}, ${esc(label(near.t))}${more}` };
    if (!th) return { cls: 'wait', ico: '⏳', text: 'Дивлюсь, що в небі…' };
    return { cls: al.on ? 'alarm' : 'calm', ico: al.on ? '🟨' : '🟩', text: `<b>У колі ${rs[2]} км нічого не видно</b>` };
  }

  function paintState(m) {
    const box = h && h.near;
    if (!box) return;
    const s = m ? stateOf() : { cls: 'wait', ico: '⏳', text: 'Мапа вантажиться…' };
    box.hidden = !s;
    if (!s) return;
    box.className = 'tv-near ' + s.cls;
    box.innerHTML = `<span class="pv-big" aria-hidden="true">${s.ico}</span>`
      + `<span class="tv-near-t"><span class="tv-near-k">Довкола мене</span> ${s.text}</span>`;
    if (pv.cv) pv.cv.setAttribute('aria-label', box.textContent);
  }

  /// Перемикачі: радіус, звідки рахуємо, звук і компас.
  function paintControls() {
    const pick = (sel, on) => { for (const b of root.querySelectorAll(sel)) { const v = on(b); b.classList.toggle('on', v); b.setAttribute('aria-checked', v ? 'true' : 'false'); } };
    pick('.pv-scl button', (b) => (b.dataset.big === '1') === opt.big);
    // поки GPS не відповів, показуємо його обраним — рахуємо ж тимчасово від міста (про це рядок нижче)
    pick('.pv-src button', (b) => b.dataset.src === opt.src);
    const tw = $('.pv-town');
    if (tw) tw.textContent = townName() === 'твого району' ? 'Район' : townName();
    const t = (sel, on) => { const b = $(sel); if (b) { b.classList.toggle('on', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); } };
    t('.pv-snd', opt.sound);
    t('.pv-cmp', opt.compass);
    const sn = $('.pv-snd');
    if (sn) sn.textContent = (opt.sound ? '🔊' : '🔈') + ' Звук';
  }

  function mask(v) { return pv.reveal ? v.toFixed(4) : v.toFixed(4).replace(/\d/g, '*'); }
  function clock(t) {
    try { return new Date(t).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit', timeZone: 'Europe/Kyiv' }); }
    catch { return new Date(t).toLocaleTimeString(); }
  }

  function paintWhere(m) {
    const box = $('.pv-where');
    if (!box) return;
    let html;
    const eye = `<button type="button" class="icon ghost pv-eye" title="${pv.reveal ? 'Сховати координати' : 'Показати координати'}" aria-label="${pv.reveal ? 'Сховати координати' : 'Показати координати'}">${pv.reveal ? '🙈' : '👁'}</button>`;
    if (!m) html = opt.src === 'gps' ? 'Шукаю тебе…' : '';
    else if (m.src === 'gps') {
      const acc = m.acc > 0 ? ' · ±' + (m.acc < 1000 ? Math.round(m.acc) + ' м' : (m.acc / 1000).toFixed(1) + ' км') : '';
      html = `<span class="pv-coord">${mask(m.lat)}, ${mask(m.lon)}</span>${acc} · ${esc(clock(m.at))} ${eye}`
        + '<span class="tv-muted"> — лише в цьому браузері</span>';
    } else if (m.src === 'here') {
      html = `Точка з мапи <span class="pv-coord">${mask(m.lat)}, ${mask(m.lon)}</span> ${eye}`;
    } else if (opt.src === 'gps') {
      html = `<span class="tv-muted">Шукаю GPS… поки рахую від ${esc(m.name)}</span>`;
    } else {
      html = `${esc(m.name)}<span class="tv-muted"> — ${m.city ? 'місто твого району' : 'середина району'}</span>`;
    }
    box.innerHTML = html;
    const msg = $('.pv-msg');
    if (msg) { msg.hidden = !pv.msg; msg.textContent = pv.msg; }
  }
  function say(text) {
    pv.msg = text || '';
    const msg = $('.pv-msg');
    if (msg) { msg.hidden = !pv.msg; msg.textContent = pv.msg; }
  }

  const label = (t) => { const m = h.types[t.type] || h.types.unknown; return t.title || m.label; };
  /// «за 7 хв»; швидкість не від NEPTUN, а типова для цього типу — «≈ за 7 хв».
  function eta(p) {
    const m = p.min;
    const v = m < 1 ? 'за хвилину' : m < 90 ? 'за ' + Math.round(m) + ' хв' : 'за ' + Math.round(m / 60) + ' год';
    return (p.guess ? '≈ ' : '') + v;
  }
  function passText(x) {
    const p = x.pass;
    if (!p) return '<span class="pv-pass">курс невідомий</span>';
    if (p.away) return '<span class="pv-pass">віддаляється</span>';
    if (p.km <= ON_ME_KM) return `<span class="pv-pass on">летить на тебе · ${eta(p)}</span>`;
    // іде боком: ближче, ніж зараз, уже не буде
    if (p.along < 2 || p.km > x.d - 3) return `<span class="pv-pass">летить мимо, ${Math.round(p.km)} км від тебе</span>`;
    return `<span class="pv-pass${x.danger ? ' on' : ''}">пройде за ${Math.round(p.km)} км · ${eta(p)}</span>`;
  }

  function paintList() {
    const ul = $('.pv-list');
    if (!ul) return;
    const th = h.threats();
    const far = rings()[2] * FAR;
    if (!th || th.stale || h.alarm().off) { ul.innerHTML = ''; return; }
    const rows = pv.list.slice(0, 8).map((x) => {
      const t = x.t;
      const m = h.types[t.type] || h.types.unknown;
      const bits = [];
      if (t.count > 1) bits.push('×' + t.count);
      if (t.quality && t.quality !== 'confirmed' && t.unc) bits.push('±' + Math.round(t.unc) + ' км');
      if (t.advisory) bits.push('спостереження');
      if (x.old) bits.push('давно не оновлювалась');
      return `<li><button type="button" class="pv-row${x.danger ? ' danger' : ''}${x.live ? '' : ' dim'}" data-id="${esc(t.id)}">`
        + `<span class="pv-ico" aria-hidden="true">${m.ico}</span><span class="pv-t"><b>${Math.round(x.d)} км</b> · ${side(x.b)}, ${Math.round(x.b) % 360}° · ${esc(label(t))}`
        + `${t.locality ? ' · ' + esc(t.locality) : ''}`
        + `${bits.length ? ' <span class="tv-muted">' + esc(bits.join(' · ')) + '</span>' : ''}<br>${passText(x)}</span></button></li>`;
    });
    // «десь в області» без точки: на циферблат не лягає, але знати варто
    const ob = h.oblastName();
    const vague = ob ? (th.threats || []).filter((t) => t.areaOnly && t.region === ob).length : 0;
    ul.innerHTML = rows.join('')
      || `<li class="pv-none tv-muted">У колі ${far} км нічого не видно.</li>`;
    if (vague) ul.insertAdjacentHTML('beforeend', `<li class="pv-none tv-muted">+ ${vague} «десь в області» без точного місця</li>`);
  }

  /// Писк: ціль зайшла глибше (зовнішнє коло — раз, середнє — двічі, внутрішнє — тричі) або повернула на мене.
  /// Перший знімок після відкриття чи зміни точки — тихо: що вже є, видно й так.
  function listen() {
    let n = 0, hi = false;
    for (const x of pv.list) {
      if (!x.live) continue;
      const id = x.t.id;
      const prev = pv.zones.has(id) ? pv.zones.get(id) : 3;
      if (x.zone < prev) {
        if (pv.baseline) n = Math.max(n, 3 - x.zone);
        pv.zones.set(id, x.zone);
      }
      const onMe = x.pass && !x.pass.away && x.pass.km <= ON_ME_KM && x.pass.min <= 30;
      if (onMe && !pv.onMe.has(id)) { pv.onMe.add(id); if (pv.baseline) hi = true; }
    }
    if (h.threats()) pv.baseline = true;
    if ((n || hi) && opt.sound && pv.shown && !document.hidden) beep(Math.max(n, hi ? 2 : 0), hi);
  }

  function audio() {
    try {
      pv.ac = pv.ac || new (window.AudioContext || window.webkitAudioContext)();
      if (pv.ac.state === 'suspended') pv.ac.resume().catch(() => {});
    } catch { pv.ac = null; }
    return pv.ac;
  }
  function beep(n, hi) {
    const ac = audio();
    if (!ac || Date.now() - pv.beepAt < 3000) return;
    pv.beepAt = Date.now();
    let t = ac.currentTime + 0.02;
    for (let i = 0; i < n; i++) {
      const o = ac.createOscillator(), g = ac.createGain();
      o.type = 'sine';
      o.frequency.value = hi ? (i % 2 ? 990 : 1320) : 880;
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(0.22, t + 0.015);
      g.gain.exponentialRampToValueAtTime(0.0001, t + 0.17);
      o.connect(g).connect(ac.destination);
      o.start(t);
      o.stop(t + 0.2);
      t += 0.26;
    }
  }

  // ---------------------------------------------------------------------------------------------
  // компас
  // ---------------------------------------------------------------------------------------------

  function compassStart(fromTap) {
    const D = window.DeviceOrientationEvent;
    if (!D) { compassFail('Компаса тут нема — північ угорі.'); return; }
    const go = () => {
      if (pv.listening) return;
      pv.listening = true;
      pv.abs = false;
      window.addEventListener('deviceorientationabsolute', onAbs);
      window.addEventListener('deviceorientation', onRel);
      clearTimeout(pv.cmpT);
      pv.cmpT = setTimeout(() => { if (pv.heading == null) compassFail('Компас мовчить (на ПК й Steam Deck його нема) — північ угорі.'); }, 3000);
    };
    if (typeof D.requestPermission === 'function') {
      // айфон дає компас лише з натиску; після перезавантаження — тицнути 🧭 ще раз
      if (!fromTap) { opt.compass = false; return; }
      const no = () => compassFail('Браузер не дав компас — північ угорі.');
      D.requestPermission().then((r) => (r === 'granted' ? go() : no())).catch(no);
    } else go();
  }
  function compassStop() {
    window.removeEventListener('deviceorientationabsolute', onAbs);
    window.removeEventListener('deviceorientation', onRel);
    clearTimeout(pv.cmpT);
    pv.listening = false;
    pv.heading = null;
  }
  function compassFail(text) {
    compassStop();
    opt.compass = false;
    save();
    say(text);
    update();
  }
  function onAbs(e) {
    if (e.alpha == null) return;
    pv.abs = true;
    turn(360 - e.alpha);
  }
  function onRel(e) {
    if (typeof e.webkitCompassHeading === 'number' && e.webkitCompassHeading >= 0) { turn(e.webkitCompassHeading); return; }
    if (!pv.abs && e.absolute && e.alpha != null) turn(360 - e.alpha);
  }
  /// Куди дивиться верх екрана (з поправкою на поворот екрана), згладжено по колу.
  function turn(deg) {
    const so = (screen.orientation && screen.orientation.angle) || window.orientation || 0;
    const r = ((deg + so + 360) % 360) * RAD;
    if (pv.heading == null) { pv.hx = Math.cos(r); pv.hy = Math.sin(r); }
    else { pv.hx = pv.hx * 0.8 + Math.cos(r) * 0.2; pv.hy = pv.hy * 0.8 + Math.sin(r) * 0.2; }
    pv.heading = (Math.atan2(pv.hy, pv.hx) / RAD + 360) % 360;
    if (pv.msg && pv.msg.startsWith('Компас')) say('');
    kick();
  }

  // ---------------------------------------------------------------------------------------------
  // циферблат
  // ---------------------------------------------------------------------------------------------

  function kick() {
    if (pv.raf || !pv.cv || !pv.shown) return;
    pv.raf = requestAnimationFrame(() => { pv.raf = 0; draw(); });
  }

  function resize() {
    if (!pv.cv) return;
    const w = pv.wrap.clientWidth;
    if (!w) return;
    const dpr = Math.min(3, window.devicePixelRatio || 1);
    if (w === pv.size && dpr === pv.dpr) return;
    pv.size = w;
    pv.dpr = dpr;
    pv.cv.width = Math.round(w * dpr);
    pv.cv.height = Math.round(w * dpr);
    draw();
  }

  function cssColors() {
    const cs = getComputedStyle(root);
    const c = (n, f) => (cs.getPropertyValue(n) || '').trim() || f;
    pv.css = {
      calm: c('--pv-calm', 'rgba(98,170,82,.42)'), alarm: c('--pv-alarm', 'rgba(232,178,52,.42)'),
      hot: c('--pv-hot', 'rgba(229,83,77,.55)'), grey: c('--pv-grey', 'rgba(157,179,165,.16)'),
      ring: c('--pv-ring', 'rgba(10,20,15,.55)'), text: c('--text', '#ecf1ea'), muted: c('--muted', '#9db3a5'),
      halo: c('--bg', '#0f1f18'), north: c('--pv-north', '#ff6b5e'), danger: c('--pv-danger', '#ff4d61'),
    };
    for (const t of Object.keys(h.types)) pv.css['t-' + t] = c('--tv-' + t, '#f0820e');
  }

  function text(g, s, x, y, color, font) {
    g.font = font;
    g.lineJoin = 'round';     // інакше обвідка колеться шпичаками на «м», «к»
    g.lineWidth = 3;
    g.strokeStyle = pv.css.halo;
    g.strokeText(s, x, y);
    g.fillStyle = color;
    g.fillText(s, x, y);
  }

  function draw() {
    if (!pv.cv || !pv.size) return;
    const g = pv.g, S = pv.size, c = pv.css;
    g.setTransform(pv.dpr, 0, 0, pv.dpr, 0, 0);
    g.clearRect(0, 0, S, S);
    const cx = S / 2, cy = S / 2, R = S / 2 - 26;
    if (R < 40) return;
    const rs = rings(), kmPx = R / rs[2];
    const turnRad = opt.compass && pv.heading != null ? pv.heading * RAD : 0;
    const at = (r, deg) => { const a = deg * RAD - turnRad; return [cx + r * Math.sin(a), cy - r * Math.cos(a)]; };
    const m = me();
    const al = h.alarm();
    const th = h.threats();
    const grey = !m || al.off || al.stale || !th || th.stale;

    // зони: кожна — своїм кольором, від зовнішньої до внутрішньої
    const hot = [false, false, false];
    for (const x of pv.list) if (x.live && x.zone < 3) hot[x.zone] = true;
    for (let i = 2; i >= 0; i--) {
      g.fillStyle = grey ? c.grey : hot[i] ? c.hot : al.on ? c.alarm : c.calm;
      g.beginPath();
      g.arc(cx, cy, rs[i] * kmPx, 0, Math.PI * 2);
      g.fill();
      g.strokeStyle = c.ring;
      g.lineWidth = 1.5;
      g.stroke();
    }

    // поділки й градуси, сторони світу — за компасом, підписи завжди прямо
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.strokeStyle = c.muted;
    for (let deg = 0; deg < 360; deg += 5) {
      const len = deg % 30 === 0 ? 9 : deg % 10 === 0 ? 6 : 3;
      const [x1, y1] = at(R, deg), [x2, y2] = at(R - len, deg);
      g.lineWidth = deg % 30 === 0 ? 1.4 : 0.8;
      g.beginPath();
      g.moveTo(x1, y1);
      g.lineTo(x2, y2);
      g.stroke();
      if (deg % 30 === 0 && deg % 90 !== 0 && R > 110) {
        const [tx, ty] = at(R - 20, deg);
        text(g, deg + '°', tx, ty, c.muted, '10px system-ui, sans-serif');
      }
    }
    [['Пн', 0], ['Сх', 90], ['Пд', 180], ['Зх', 270]].forEach(([s, deg]) => {
      const [tx, ty] = at(R + 14, deg);
      text(g, s, tx, ty, deg === 0 ? c.north : c.text, 'bold 13px system-ui, sans-serif');
    });
    // підписи кілець — унизу, не крутяться
    for (const km of rs) text(g, km + ' км', cx, cy + km * kmPx - 9, c.text, '11px system-ui, sans-serif');

    if (!grey) {
      // сліди, кола похибки, курс до найближчої точки — лише всередині циферблата
      g.save();
      g.beginPath();
      g.arc(cx, cy, R, 0, Math.PI * 2);
      g.clip();
      for (const x of pv.list) {
        if (x.d > rs[2] * 1.5) continue;
        const color = c['t-' + (h.types[x.t.type] ? x.t.type : 'unknown')];
        const [px, py] = at(x.d * kmPx, x.b);
        g.globalAlpha = x.live ? 1 : 0.45;
        if (x.trail.length) {
          g.strokeStyle = color;
          g.lineWidth = 2.2;
          g.globalAlpha *= 0.85;
          g.beginPath();
          x.trail.forEach(([d, b], i) => { const [tx, ty] = at(d * kmPx, b); if (i) g.lineTo(tx, ty); else g.moveTo(tx, ty); });
          g.lineTo(px, py);
          g.stroke();
          g.globalAlpha /= 0.85;
        }
        if (x.t.quality !== 'confirmed' && x.t.unc > 0) {
          g.setLineDash([3, 3]);
          g.strokeStyle = color;
          g.lineWidth = 1;
          g.beginPath();
          g.arc(px, py, x.t.unc * kmPx, 0, Math.PI * 2);
          g.stroke();
          g.setLineDash([]);
        }
        if (x.pass && !x.pass.away && x.live && x.pass.km <= rs[2]) {
          // куди долетить за курсом: пунктир до найближчої до мене точки
          const a = x.t.heading * RAD - turnRad, L = x.pass.along * kmPx;
          g.setLineDash([5, 4]);
          g.strokeStyle = x.danger ? c.danger : color;
          g.lineWidth = x.danger ? 2 : 1.2;
          g.beginPath();
          g.moveTo(px, py);
          g.lineTo(px + L * Math.sin(a), py - L * Math.cos(a));
          g.stroke();
          g.setLineDash([]);
        }
        g.globalAlpha = 1;
      }
      g.restore();

      // азимут: стрілка від мене на кожну живу ціль у колі; небезпечна — червона й товща
      for (const x of pv.list) {
        if (!x.live || x.zone > 2) continue;
        const r = Math.max(0, x.d * kmPx - 12);
        if (r < 14) continue;
        const [ex, ey] = at(r, x.b);
        arrow(g, cx, cy, ex, ey, x.danger ? c.danger : c.text, x.danger ? 3 : 1.8);
      }

      // самі цілі; далекі — рискою на краю
      for (const x of pv.list) {
        const color = c['t-' + (h.types[x.t.type] ? x.t.type : 'unknown')];
        g.globalAlpha = x.live ? 1 : 0.45;
        if (x.zone > 2) {
          const [ex, ey] = at(R - 3, x.b);
          g.save();
          g.translate(ex, ey);
          g.rotate(x.b * RAD - turnRad);
          g.fillStyle = x.danger ? c.danger : color;
          g.strokeStyle = 'rgba(0,0,0,.6)';
          g.lineWidth = 1;
          g.beginPath();
          const k = x.danger ? 1.4 : 1;
          g.moveTo(0, 7 * k); g.lineTo(5 * k, -3 * k); g.lineTo(-5 * k, -3 * k);
          g.closePath();
          g.fill();
          g.stroke();
          g.restore();
        } else {
          const [px, py] = at(x.d * kmPx, x.b);
          g.save();
          g.translate(px, py);
          if (x.danger) {
            g.strokeStyle = c.danger;
            g.lineWidth = 2.2;
            g.beginPath();
            g.arc(0, 0, 16, 0, Math.PI * 2);
            g.stroke();
          }
          g.scale(1.5, 1.5);            // як на мапі: значок із білою обвідкою й темним гало (glyph у tryvoga.js)
          const meta = h.types[x.t.type] || h.types.unknown;
          const rot = x.t.heading == null ? 0 : x.t.heading * RAD - turnRad;
          h.glyph(g, x.t.heading == null && meta.shape !== 'ring' ? 'dot' : meta.shape, color, rot);
          g.restore();
          if (x.t.count > 1) { g.textAlign = 'left'; text(g, '×' + x.t.count, px + 12, py - 11, c.text, 'bold 12px system-ui, sans-serif'); g.textAlign = 'center'; }
        }
        g.globalAlpha = 1;
      }
    } else if (m) {
      text(g, al.off ? 'вимкнено' : !th ? 'дивлюсь…' : 'дані застаріли', cx, cy - 22, c.text, '13px system-ui, sans-serif');
    }

    // я — у центрі; місто району — порожнім кружком (це не точно я)
    g.beginPath();
    g.arc(cx, cy, 5, 0, Math.PI * 2);
    g.lineWidth = 2.5;
    g.strokeStyle = c.halo;
    g.stroke();
    if (m && m.src !== 'raion') { g.fillStyle = c.text; g.fill(); }
    else { g.strokeStyle = c.text; g.lineWidth = 1.6; g.stroke(); }
  }

  function arrow(g, x1, y1, x2, y2, color, w) {
    const a = Math.atan2(y2 - y1, x2 - x1), hl = 6 + w * 2;
    g.strokeStyle = color;
    g.fillStyle = color;
    g.lineWidth = w;
    g.lineCap = 'round';
    g.beginPath();
    g.moveTo(x1, y1);
    g.lineTo(x2 - Math.cos(a) * hl * 0.6, y2 - Math.sin(a) * hl * 0.6);
    g.stroke();
    g.beginPath();
    g.moveTo(x2, y2);
    g.lineTo(x2 - hl * Math.cos(a - 0.4), y2 - hl * Math.sin(a - 0.4));
    g.lineTo(x2 - hl * Math.cos(a + 0.4), y2 - hl * Math.sin(a + 0.4));
    g.closePath();
    g.fill();
  }

  // ---------------------------------------------------------------------------------------------
  // назовні
  // ---------------------------------------------------------------------------------------------

  /// el — порожня секція поруч із мапою; host — tryvoga.js (host.near — рядок стану в картці згори).
  function mount(el, host) {
    h = host;
    root = el;
    root.innerHTML = `
      <div class="pv-head">
        <h2 class="pv-h">🧭 Довкола мене</h2>
        <div class="pv-seg pv-scl" role="radiogroup" aria-label="Радіус кілець">
          <button type="button" role="radio" data-big="0" title="Кільця 15, 30 і 45 км">45 км</button>
          <button type="button" role="radio" data-big="1" title="Кільця 50, 100 і 150 км">150 км</button>
        </div>
      </div>
      <div class="pv-dial"><canvas class="pv-cv" role="img" aria-label="Довкола мене"></canvas></div>
      <div class="pv-from">
        <span class="pv-lbl">Рахую від</span>
        <div class="pv-seg pv-src" role="radiogroup" aria-label="Від якої точки рахувати">
          <button type="button" role="radio" data-src="raion" title="Від міста твого району (обирається згори)">🏘 <span class="pv-town">Район</span></button>
          <button type="button" role="radio" data-src="gps" title="Де я — за GPS (координати лишаються в цьому браузері)">📡 GPS</button>
          <button type="button" role="radio" data-src="here" title="Від точки, яку позначиш на мапі">📌 Точка</button>
        </div>
      </div>
      <div class="pv-where"></div>
      <p class="pv-msg" role="status" hidden></p>
      <div class="pv-tg" role="group" aria-label="Звук і компас">
        <button type="button" class="pv-snd" aria-pressed="false" title="Пискнути, коли ціль заходить у коло чи повертає на мене (лише поки сторінка відкрита)">🔈 Звук</button>
        <button type="button" class="pv-cmp" aria-pressed="false" title="Крутити циферблат за компасом телефону">🧭 Компас</button>
      </div>
      <ul class="pv-list" aria-label="Що летить довкола"></ul>
      <div class="pv-legend">
        <span><i class="pz calm"></i>тихо</span><span><i class="pz alarm"></i>тривога в районі</span>
        <span><i class="pz hot"></i>ціль у колі</span><span><i class="pz arr"></i>летить на тебе</span>
      </div>`;
    pv.wrap = $('.pv-dial');
    pv.cv = $('.pv-cv');
    pv.g = pv.cv.getContext('2d');
    cssColors();
    if (!touch) $('.pv-cmp').remove();
    $('.pv-scl').addEventListener('click', (e) => {
      const b = e.target.closest('button[data-big]');
      if (!b || (b.dataset.big === '1') === opt.big) return;
      opt.big = b.dataset.big === '1';
      save();
      moved();
    });
    $('.pv-src').addEventListener('click', (e) => {
      const b = e.target.closest('button[data-src]');
      if (b) setSrc(b.dataset.src);
    });
    if (touch) $('.pv-cmp').onclick = () => {
      opt.compass = !opt.compass;
      say('');
      if (opt.compass) compassStart(true); else compassStop();
      save();
      update();
    };
    $('.pv-snd').onclick = () => {
      opt.sound = !opt.sound;
      save();
      if (opt.sound) { audio(); pv.beepAt = 0; beep(1, false); }
      update();
    };
    root.addEventListener('click', (e) => {
      if (e.target.closest('.pv-eye')) { pv.reveal = !pv.reveal; paintWhere(me()); return; }
      const row = e.target.closest('.pv-list [data-id]');
      if (row) h.focus(row.dataset.id);
    });
    // звук після перезавантаження: браузер дасть його лише після першого дотику до сторінки
    document.addEventListener('pointerdown', () => { if (opt.sound && pv.ac && pv.ac.state === 'suspended') pv.ac.resume().catch(() => {}); }, { passive: true });
    pv.ro = new ResizeObserver(resize);
    pv.ro.observe(pv.wrap);
    update();
  }

  function show() {
    pv.shown = true;
    pv.baseline = false;
    if (!root) return;
    if (opt.src === 'gps') gpsStart();
    if (opt.compass && touch) compassStart(false);
    if (opt.sound) audio();
    resize();
    update();
  }
  function hide() {
    pv.shown = false;
    gpsStop();
    compassStop();
  }

  /// Для мапи: від якої точки рахуємо і яке зовнішнє кільце (пунктиром довкола мене).
  function where() {
    if (!root) return null;
    const m = me();
    return m ? { lat: m.lat, lon: m.lon, src: m.src, km: rings()[2] } : null;
  }

  window.HDovkola = { mount, update, show, hide, setHere, me: where };
})();
