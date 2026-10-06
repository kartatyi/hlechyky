'use strict';
// Брелоки біля корту: дешеві Bluetooth-«краплі» (iTag і його клони) як пульт рахунку. Один брелок — одна команда;
// що робить натиск, вирішує табло (board.js, Padel.tags.onPress). Тут — лише Bluetooth: під'єднати, ловити натиски
// (один чи два швидкі), пищати у відповідь, перепідключатись, коли брелок відпав.
// Web Bluetooth є лише в Chrome на Android і в Chrome/Edge на компі; на айфоні Safari його не має.
// Як говорить iTag: кнопка — сповіщення на FFE0/FFE1; пік — Immediate Alert 1802/2A06 (0 — тиша, 1/2 — пищить, доки
// не скажеш 0); Link Loss 1803/2A06 = 0 — щоб не верещав, коли телефон відійшов; батарея — 180F/2A19.
(function () {
  const P = window.Padel;
  const BT = navigator.bluetooth;
  const SVC_BTN = 0xffe0, CH_BTN = 0xffe1;
  const OPT = [0xffe0, 0xfff0, 0x1802, 0x1803, 0x180f];
  // Як такі брелоки звуть себе в ефірі (namePrefix розрізняє великі й малі літери). Не знайшовся — «показати всі».
  const NAMES = ['iTAG', 'iTag', 'ITAG', 'Itag', 'Tag', 'TAG', 'Smart', 'SMART', 'TRYBE', 'Trybe', 'Tracker', 'TRACKER', 'MLE', 'Find', 'FIND', 'Key', 'KEY'];
  // Другий натиск у цьому вікні — «два швидкі». Тому й одиночний натиск віддаємо табло лише через стільки.
  const DOUBLE_MS = 450;
  // Пік — [пищить, тиша, пищить, …] у мс. Коротше ~120 мс брелок може й не встигнути пискнути.
  const BEEPS = { ok: [180], undo: [150, 130, 150], err: [700], hi: [110, 110, 110, 110, 110] };

  const slots = [null, null];   // { t, dev, id, name, state: saved|link|on|lost, bat, btn, alert, q, timer, tries, pt, mute, raw, at }
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const alive = (s) => !!s && slots[s.t] === s;

  const T = P.tags = {
    /// Чи вміє цей браузер Bluetooth зі сторінки (Chrome/Edge, HTTPS)
    ok: !!BT && window.isSecureContext !== false,
    ios: /iPhone|iPad|iPod/.test(navigator.userAgent || ''),
    /// Чи є на пристрої Bluetooth і чи він увімкнений (комп без адаптера — false)
    async avail() { if (!T.ok) return false; try { return BT.getAvailability ? await BT.getAvailability() : true; } catch { return true; } },
    /// Стан брелока команди t: null або { name, state, bat, raw, at }
    slot(t) { const s = slots[t]; return s ? { name: s.name, state: s.state, bat: s.bat, raw: s.raw, at: s.at } : null; },
    /// Є хоч один брелок, що тримає (або ловить) з'єднання — перезавантаження сторінки його обірве
    busy() { return slots.some((s) => s && s.dev); },
    /// Хоч один брелок зараз на зв'язку
    live() { return slots.some((s) => s && s.state === 'on'); },
    pair, drop, swap, beep,
    /// (t, two) — натиснули брелок команди t (two — двічі швидко). Ставить табло.
    onPress: null,
    /// (t, what) — щось змінилось: link | on | back | lost | drop | press | swap
    onChange: null,
  };

  function changed(s, what) { if (T.onChange) { try { T.onChange(s ? s.t : -1, what); } catch (e) { console.error(e); } } }
  function save() { P.pref('tags', slots.map((s) => s ? { id: s.id, name: s.name } : null)); }

  function mk(dev, t, name) {
    const s = { t, dev, id: dev ? dev.id : '', name: String(name || (dev && dev.name) || '').trim() || 'брелок', state: 'saved', bat: null, btn: null, alert: null,
      q: Promise.resolve(), timer: 0, tries: 0, pt: 0, mute: 0, raw: null, at: 0 };
    s.onVal = (e) => { if (alive(s)) press(s, e.target.value); };
    if (dev) dev.addEventListener('gattserverdisconnected', () => lost(s));
    return s;
  }

  /// Під'єднати брелок команді t. Кличеться з тицю (Chrome показує список пристроїв лише на жест).
  /// all — показати всі пристрої поруч, а не лише схожі на брелок. Повертає true, якщо брелок на зв'язку.
  async function pair(t, all) {
    if (!T.ok) throw new Error('Цей браузер не вміє Bluetooth — потрібен Chrome на Android або на компі');
    let dev;
    try {
      dev = await BT.requestDevice(all ? { acceptAllDevices: true, optionalServices: OPT }
        : { filters: [{ services: [SVC_BTN] }].concat(NAMES.map((n) => ({ namePrefix: n }))), optionalServices: OPT });
    } catch (e) {
      if (e && e.name === 'NotFoundError') return false;   // закрили список, нічого не обравши
      if (e && e.name === 'SecurityError') throw new Error('Браузер не дав Bluetooth цій сторінці — відкрий Падельню окремо: hlechyky.pp.ua/padel/');
      throw new Error('Bluetooth: ' + ((e && e.message) || 'не вийшло'));
    }
    const o = slots[1 - t];
    if (o && o.id === dev.id) throw new Error('Цей брелок уже в іншої команди — ⇄ поміняй місцями');
    drop(t, true);
    const s = slots[t] = mk(dev, t);
    save();
    try { await link(s); }
    catch (e) {
      if (!alive(s)) return false;
      if (e && e.nobtn) { drop(t); throw new Error('«' + s.name + '» не схожий на брелок-кнопку' + (e.svcs ? ' (є лише: ' + e.svcs + ')' : '') + '. Якщо це він — напиши, розберемось.'); }
      s.state = 'lost'; changed(s, 'lost'); retry(s);
      throw new Error('«' + s.name + '» не відповів — тримай його ближче, я пробую ще');
    }
    beep(t, 'hi', true);
    return true;
  }

  /// З'єднання й налаштування: кнопка, тиша при розриві, пік, батарея
  async function link(s) {
    const dev = s.dev;
    s.state = 'link'; changed(s, 'link');
    const g = await within(dev.gatt.connect(), 15000, () => { try { dev.gatt.disconnect(); } catch { /* */ } });
    if (!alive(s)) { try { g.disconnect(); } catch { /* */ } return; }
    let btn = null;
    try { btn = await (await g.getPrimaryService(SVC_BTN)).getCharacteristic(CH_BTN); } catch { /* не класичний iTag — шукаємо далі */ }
    if (!btn) btn = await anyNotify(g);
    if (!btn) { const e = new Error('nobtn'); e.nobtn = true; e.svcs = await svcList(g); throw e; }
    await btn.startNotifications();
    if (s.btn && s.btn !== btn) s.btn.removeEventListener('characteristicvaluechanged', s.onVal);
    btn.addEventListener('characteristicvaluechanged', s.onVal);
    s.btn = btn;
    try { await (await (await g.getPrimaryService(0x1803)).getCharacteristic(0x2a06)).writeValue(Uint8Array.of(0)); } catch { /* нема Link Loss — хай */ }
    try { s.alert = await (await g.getPrimaryService(0x1802)).getCharacteristic(0x2a06); } catch { s.alert = null; }
    try { s.bat = (await (await (await g.getPrimaryService(0x180f)).getCharacteristic(0x2a19)).readValue()).getUint8(0); } catch { s.bat = null; }
    if (!alive(s)) return;
    const back = s.tries > 0;
    s.state = 'on'; s.tries = 0;
    changed(s, back ? 'back' : 'on');
  }

  /// Клони без FFE0: перша характеристика зі сповіщенням у тих сервісах, що нам дозволено бачити
  async function anyNotify(g) {
    for (const u of [0xfff0, SVC_BTN]) {
      try {
        const sv = await g.getPrimaryService(u);
        for (const c of await sv.getCharacteristics()) if (c.properties && c.properties.notify) return c;
      } catch { /* нема такого */ }
    }
    return null;
  }
  async function svcList(g) {
    try { return (await g.getPrimaryServices()).map((x) => x.uuid.slice(4, 8)).join(', '); } catch { return ''; }
  }
  function within(p, ms, onLate) {
    let t;
    return Promise.race([p, new Promise((_, rej) => { t = setTimeout(() => { if (onLate) onLate(); rej(new Error('timeout')); }, ms); })])
      .finally(() => clearTimeout(t));
  }

  function lost(s) {
    if (!alive(s) || !s.dev) return;
    s.alert = null;
    const was = s.state;
    s.state = 'lost';
    if (was === 'on') changed(s, 'lost');
    retry(s);
  }
  /// Пробуємо знову й знову, поки сторінка відкрита: 1,5 → 3 → 6 → 12 → 15 с. Повернулась на екран — одразу.
  function retry(s, now) {
    if (!alive(s) || !s.dev || s.timer || s.state === 'on' || s.state === 'link') return;
    const ms = now ? 50 : Math.min(15000, 1500 * Math.pow(2, Math.min(s.tries, 4)));
    s.tries++;
    s.timer = setTimeout(async () => {
      s.timer = 0;
      if (!alive(s) || s.state === 'on' || s.state === 'link') return;
      try { await link(s); }
      catch { if (!alive(s)) return; s.state = 'lost'; changed(s, 'wait'); retry(s); }
    }, ms);
  }
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) return;
    for (const s of slots) if (s && s.dev && s.state === 'lost') { clearTimeout(s.timer); s.timer = 0; s.tries = 0; retry(s, true); }
  });

  /// Натиск: другий у вікні DOUBLE_MS — «два швидкі»; після них ще DOUBLE_MS глухі, щоб третій не став очком
  function press(s, v) {
    const now = Date.now();
    s.raw = v && v.byteLength ? v.getUint8(0) : null; s.at = now;
    changed(s, 'press');
    if (now < s.mute) return;
    if (s.pt) { clearTimeout(s.pt); s.pt = 0; s.mute = now + DOUBLE_MS; fire(s, true); return; }
    s.pt = setTimeout(() => { s.pt = 0; fire(s, false); }, DOUBLE_MS);
  }
  function fire(s, two) { if (alive(s) && T.onPress) { try { T.onPress(s.t, two); } catch (e) { console.error(e); } } }

  /// Пікнути брелоком команди t: ok — одне коротке, undo — два, err — довге, hi — «це я» (тріль).
  /// force — навіть коли пік вимкнено в налаштуваннях (під'єднали — хай пискне, щоб знати, котрий).
  function beep(t, kind, force) {
    const s = slots[t];
    if (!s || !s.alert || s.state !== 'on') return;
    if (!force && P.pref('tagBeep') === false) return;
    const pat = BEEPS[kind] || BEEPS.ok, c = s.alert;
    queue(s, async () => {
      for (let i = 0; i < pat.length; i++) { await put(c, i % 2 ? 0 : 2); await sleep(pat[i]); }
      await put(c, 0);
    });
  }
  /// Immediate Alert пишуть без відповіді; деякі клони вміють лише з відповіддю
  async function put(c, v) {
    const b = Uint8Array.of(v);
    if (c.properties && c.properties.writeWithoutResponse && c.writeValueWithoutResponse) return c.writeValueWithoutResponse(b);
    return c.writeValue(b);
  }
  /// Bluetooth не любить дві операції разом — шикуємо їх у чергу на кожен брелок
  function queue(s, fn) { s.q = s.q.then(fn).catch(() => { /* брелок відпав посеред піку — не біда */ }); return s.q; }

  /// Від'єднати брелок команди t. quiet — без події (заміна іншим)
  function drop(t, quiet) {
    const s = slots[t];
    if (!s) return;
    slots[t] = null;
    clearTimeout(s.timer); clearTimeout(s.pt);
    if (s.btn) s.btn.removeEventListener('characteristicvaluechanged', s.onVal);
    // Писк при розриві лишаємо вимкненим, інакше ✕ змусить брелок верещати; після вимкнення-ввімкнення він сам
    // повернеться до заводського
    if (s.dev && s.dev.gatt && s.dev.gatt.connected) { try { s.dev.gatt.disconnect(); } catch { /* */ } }
    if (!quiet) { save(); changed(s, 'drop'); }
    // Падельню оновили, поки брелоки тримали з'єднання (padel.js відклав перезавантаження) — тепер можна
    if (P.stale && !T.busy()) setTimeout(() => location.reload(), 400);
  }

  function swap() {
    const [a, b] = slots;
    slots[0] = b; slots[1] = a;
    if (slots[0]) slots[0].t = 0;
    if (slots[1]) slots[1].t = 1;
    save(); changed(null, 'swap');
  }

  /// Після перезавантаження: Chrome, що пам'ятає дозволи (getDevices), — під'єднуємо самі; інакше лишаємо ім'я,
  /// щоб у листі було видно «був такий — тиць, щоб під'єднати».
  async function restore() {
    const saved = P.pref('tags') || [];
    if (!T.ok || !saved.some(Boolean)) return;
    let devs = [];
    if (BT.getDevices) { try { devs = await BT.getDevices(); } catch { /* не дали */ } }
    for (const t of [0, 1]) {
      const sv = saved[t];
      if (!sv || slots[t]) continue;
      const dev = devs.find((d) => d.id === sv.id);
      slots[t] = mk(dev || null, t, sv.name);
      if (dev) { slots[t].state = 'lost'; retry(slots[t], true); }
    }
    changed(null, 'restore');
  }
  restore();
})();
