/*
  «🐓 Півник» — window.HPivnyk (21.10.2026, docs/tryvoga.md): флюгер поруч із мапою Тривоги. Циферблат довкола мене:
  кільця 15/30/45 км (або 50/100/150), що летить, звідки, куди — і чи на мене.

  Де я: GPS браузера (рахується лише тут — на сервер не йде й ніде не зберігається), точка «📌 Я тут» з мапи
  (localStorage цього браузера) або місто мого району. Цілі — той самий знімок NEPTUN, що й на мапі (tryvoga.js
  тягне /api/tryvoga/threats раз на 10 с, поки сторінку видно). З курсу й швидкості цілі — найближче проходження:
  «летить на тебе» чи «пройде за N км» і за скільки хвилин.

  Кольори зон — як у застосунку «Флюгер»: зелена — тихо, жовта — тривога в районі, червона — ціль у цьому колі.
  Компас телефону крутить циферблат; звук — тумблером, лише поки сторінка відкрита (системних сповіщень нема:
  рішення Тривоги — «лише плашка»).
*/
(() => {
  'use strict';

  const KEY = 'pivnyk';                 // localStorage: { gps, sound, compass, big, here: { lat, lon } }
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

  let h = null;          // господар — tryvoga.js: { esc, map, threats, region, alarm, oblastName, types, glyph, kick, focus }
  let root = null;
  let opt = load();
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
    let o = {};
    try { o = JSON.parse(localStorage.getItem(KEY) || '{}') || {}; } catch { o = {}; }
    const here = o.here && Number.isFinite(o.here.lat) && Number.isFinite(o.here.lon)
      && Math.abs(o.here.lat) <= 90 && Math.abs(o.here.lon) <= 180 ? { lat: o.here.lat, lon: o.here.lon } : null;
    return { gps: !!o.gps, sound: !!o.sound, compass: !!o.compass, big: !!o.big, here };
  }
  function save() { try { localStorage.setItem(KEY, JSON.stringify(opt)); } catch { /* приватне вікно */ } }

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

  /// Від якої точки рахуємо: GPS → «📌 Я тут» → місто району. null — мапа ще не завантажилась, а GPS нема.
  function me() {
    if (opt.gps && pv.fix) return { lat: pv.fix.lat, lon: pv.fix.lon, src: 'gps', acc: pv.fix.acc, at: pv.fix.at };
    if (opt.here) return { lat: opt.here.lat, lon: opt.here.lon, src: 'here' };
    const t = town();
    return t ? { lat: t.lat, lon: t.lon, src: 'raion', name: t.name, city: t.city } : null;
  }

  function gpsStart() {
    if (pv.watch != null || !opt.gps) return;
    if (!navigator.geolocation) { gpsFail('Цей браузер не дає місце — тицни на мапі «📌 Я тут».'); return; }
    pv.watch = navigator.geolocation.watchPosition((p) => {
      pv.fix = { lat: p.coords.latitude, lon: p.coords.longitude, acc: p.coords.accuracy, at: p.timestamp || Date.now() };
      say('');
      moved();
    }, (e) => {
      if (e.code === 1) gpsFail('Браузер не дав місце. Дозволь сайту «Місцезнаходження» або тицни на мапі «📌 Я тут».');
      else if (!pv.fix) say('GPS поки мовчить — рахую від ' + fallbackName() + '.');
    }, { enableHighAccuracy: false, maximumAge: 60000, timeout: 30000 });
  }
  function gpsStop() {
    if (pv.watch != null && navigator.geolocation) navigator.geolocation.clearWatch(pv.watch);
    pv.watch = null;
  }
  function gpsFail(text) {
    gpsStop();
    opt.gps = false;
    pv.fix = null;
    save();
    say(text);
    moved();
  }
  function fallbackName() {
    if (opt.here) return 'точки на мапі';
    const t = town();
    return t ? t.name : 'твого району';
  }

  /// «📌 Я тут» з мапи: точка важливіша за GPS, тож GPS вимикаємо — щоб було видно, від чого рахуємо.
  function setHere(lat, lon) {
    if (!Number.isFinite(lat) || !Number.isFinite(lon)) return;
    opt.here = { lat: +lat.toFixed(5), lon: +lon.toFixed(5) };
    opt.gps = false;
    gpsStop();
    pv.fix = null;
    save();
    say('');
    moved();
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
    paintTop(m);
    paintWhere(m);
    paintList();
    kick();
  }

  function stateOf() {
    const al = h.alarm();
    const th = h.threats();
    if (al.off) return { cls: 'off', text: 'Тривоги зараз не показуємо — сервер їх не опитує.' };
    if (al.stale || (th && th.stale)) return { cls: 'stale', ico: '❔', text: '<b>Не знаю, що в небі</b> — дані застаріли. Дивись застосунок «Повітряна тривога».' };
    const rs = rings();
    const inside = pv.list.filter((x) => x.live && x.zone < 3);
    const more = inside.length > 1 ? ` · у колі ${rs[2]} км їх ${inside.length}` : '';
    // найнебезпечніша — та, що найшвидше долетить до мене; нема такої — найближча
    const coming = pv.list.filter((x) => x.danger).sort((a, b) => a.pass.min - b.pass.min)[0];
    if (coming) {
      const what = coming.pass.km <= ON_ME_KM ? 'Летить на тебе' : 'Летить у твій бік';
      const by = coming.pass.km <= ON_ME_KM ? '' : `, пройде за ${Math.round(coming.pass.km)} км`;
      return { cls: coming.zone < 3 ? 'hot' : 'coming', ico: coming.zone < 3 ? '🟥' : '🟧',
        text: `<b>${what}</b> — ${esc(label(coming.t))} за ${Math.round(coming.d)} км, ${side(coming.b)}${by}, ${eta(coming.pass)}${more}` };
    }
    const near = inside[0];
    if (near) return { cls: 'hot', ico: '🟥', text: `<b>Ціль за ${Math.round(near.d)} км</b> — ${side(near.b)}${more}` };
    if (al.on) return { cls: 'alarm', ico: '🟨', text: `<b>Тривога в районі</b>, а в колі ${rs[2]} км нічого не видно` };
    if (!th) return { cls: 'wait', ico: '⏳', text: 'Дивлюсь, що в небі…' };
    return { cls: 'calm', ico: '🟩', text: `<b>Тихо</b> — у колі ${rs[2]} км нічого` };
  }

  function paintTop(m) {
    const box = $('.pv-state');
    if (!box) return;
    const s = m ? stateOf() : { cls: 'wait', ico: '⏳', text: 'Мапа вантажиться…' };
    box.className = 'pv-state ' + s.cls;
    box.innerHTML = (s.ico ? `<span class="pv-big">${s.ico}</span>` : '') + `<span>${s.text}</span>`;
    if (pv.cv) pv.cv.setAttribute('aria-label', 'Півник: ' + box.textContent);
    const t = (sel, on) => { const b = $(sel); if (b) { b.classList.toggle('on', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); } };
    t('.pv-gps', opt.gps);
    t('.pv-snd', opt.sound);
    t('.pv-cmp', opt.compass);
    const sc = $('.pv-scl');
    if (sc) sc.innerHTML = `◎ ${rings()[2]} км`;
    const sn = $('.pv-snd');
    if (sn) sn.innerHTML = (opt.sound ? '🔊' : '🔈') + ' звук';
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
    if (!m) html = opt.gps ? '📡 Шукаю тебе…' : '';
    else if (m.src === 'gps') {
      const acc = m.acc > 0 ? ' · ±' + (m.acc < 1000 ? Math.round(m.acc) + ' м' : (m.acc / 1000).toFixed(1) + ' км') : '';
      html = `📡 <span class="pv-coord">${mask(m.lat)}, ${mask(m.lon)}</span>${acc} · ${esc(clock(m.at))} ${eye}`;
    } else if (m.src === 'here') {
      html = `📌 Точка з мапи <span class="pv-coord">${mask(m.lat)}, ${mask(m.lon)}</span> ${eye}`
        + '<button type="button" class="ghost pv-unhere" title="Рахувати від міста району">✕ прибрати</button>';
    } else {
      html = `🏘 ${esc(m.name)}${m.city ? '' : ' (середина району)'}`
        + `<span class="tv-muted"> — ${opt.gps ? 'шукаю GPS…' : 'точніше: 📡 GPS або тицни на мапі «📌 Я тут»'}</span>`;
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
      return `<li data-id="${esc(t.id)}" class="${x.danger ? 'danger' : ''}${x.live ? '' : ' dim'}">`
        + `<span class="pv-ico">${m.ico}</span><span class="pv-t"><b>${Math.round(x.d)} км</b> · ${side(x.b)} ${Math.round(x.b) % 360}° · ${esc(label(t))}`
        + `${bits.length ? ' <span class="tv-muted">' + esc(bits.join(' · ')) + '</span>' : ''}<br>${passText(x)}</span></li>`;
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
          g.lineWidth = 1.5;
          g.globalAlpha *= 0.55;
          g.beginPath();
          x.trail.forEach(([d, b], i) => { const [tx, ty] = at(d * kmPx, b); if (i) g.lineTo(tx, ty); else g.moveTo(tx, ty); });
          g.lineTo(px, py);
          g.stroke();
          g.globalAlpha /= 0.55;
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
            g.lineWidth = 2;
            g.beginPath();
            g.arc(0, 0, 13, 0, Math.PI * 2);
            g.stroke();
          }
          g.scale(1.25, 1.25);
          const meta = h.types[x.t.type] || h.types.unknown;
          const rot = x.t.heading == null ? 0 : x.t.heading * RAD - turnRad;
          h.glyph(g, x.t.heading == null && meta.shape !== 'ring' ? 'dot' : meta.shape, color, rot);
          g.restore();
          if (x.t.count > 1) { g.textAlign = 'left'; text(g, '×' + x.t.count, px + 10, py - 10, c.text, 'bold 11px system-ui, sans-serif'); g.textAlign = 'center'; }
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

  /// el — порожня секція поруч із мапою; host — tryvoga.js.
  function mount(el, host) {
    h = host;
    root = el;
    root.innerHTML = `
      <h2 class="pv-h">🐓 Півник <span class="tv-muted">що летить довкола мене</span></h2>
      <div class="pv-state"></div>
      <div class="pv-tg" role="group" aria-label="Півник: налаштування">
        <button type="button" class="pv-gps" aria-pressed="false" title="Де я — за GPS телефону (лише в цьому браузері)">📡 GPS</button>
        <button type="button" class="pv-cmp" aria-pressed="false" title="Крутити циферблат за компасом телефону">🧭 компас</button>
        <button type="button" class="pv-snd" aria-pressed="false" title="Пискнути, коли ціль заходить у коло чи повертає на мене (лише поки сторінка відкрита)">🔈 звук</button>
        <button type="button" class="pv-scl" title="Кільця 15/30/45 км чи 50/100/150 км">◎ 45 км</button>
      </div>
      <div class="pv-dial"><canvas class="pv-cv" role="img" aria-label="Півник"></canvas></div>
      <div class="pv-where"></div>
      <p class="pv-msg" hidden></p>
      <ul class="pv-list" aria-label="Що летить довкола"></ul>
      <div class="pv-legend">
        <span><i class="pz calm"></i>тихо</span><span><i class="pz alarm"></i>тривога в районі</span>
        <span><i class="pz hot"></i>ціль у колі</span><span><i class="pz arr"></i>летить на тебе</span>
      </div>`;
    pv.wrap = $('.pv-dial');
    pv.cv = $('.pv-cv');
    pv.g = pv.cv.getContext('2d');
    cssColors();
    $('.pv-gps').onclick = () => {
      opt.gps = !opt.gps;
      say('');
      if (opt.gps) gpsStart(); else { gpsStop(); pv.fix = null; }
      save();
      moved();
    };
    $('.pv-cmp').onclick = () => {
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
    $('.pv-scl').onclick = () => {
      opt.big = !opt.big;
      save();
      moved();
    };
    root.addEventListener('click', (e) => {
      if (e.target.closest('.pv-eye')) { pv.reveal = !pv.reveal; paintWhere(me()); return; }
      if (e.target.closest('.pv-unhere')) { opt.here = null; save(); moved(); return; }
      const li = e.target.closest('.pv-list li[data-id]');
      if (li) h.focus(li.dataset.id);
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
    if (opt.gps) gpsStart();
    if (opt.compass) compassStart(false);
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

  window.HPivnyk = { mount, update, show, hide, setHere, me: where };
})();
