/*
  Посиденьки — голосовий чат. Один глобал — window.HVoice.

  Як це працює:
  - Голос іде напряму між браузерами (WebRTC, кожен з кожним). Сервер (VoiceChat.cs) лише каже, хто де (подія
    `voice` — усім), кого я чую і хто чує мене (`voiceMe` — мені) і передає листи, поки браузери домовляються про
    з'єднання (`voiceSignal`).
  - Позивний (peer) вкладка вигадує собі сама й тримає, поки відкрита. Сервер перезапустився з деплоєм — заходимо
    знову з тим самим позивним, а вже встановлені з'єднання з людьми живуть далі: голос не рветься. Тому людину, що
    зникла зі списку, тримаємо ще 15 с (GRACE_MS), перш ніж рвати з нею з'єднання.
  - Домовляємось за «чемними переговорами» (perfect negotiation, MDN): обидва боки можуть почати, а колізію
    розводить чемність — чемний (у кого позивний більший) відступає.
  - Мікрофон іде через AudioWorklet (static/voice-worklet.js): там голосова активація й кнопка «говорити» — у фоні
    вони працюють так само, як на видноті. Той самий доріжковий трек (sendTrack) іде всім; кому слухати не можна
    (правила гри за столом), тому трек знімаємо (replaceTrack(null)), а чужий голос, який мені не можна, глушимо.
  - Коли хтось говорить, радіо притихає (o.duck).

  app.js дає: $, esc, toast, me, askNick, duck(f), onRoster(), nickStyle(n) — і кличе attach(conn), reconnected().
*/
(() => {
  'use strict';

  let o = null;
  let conn = null;
  let esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const same = (a, b) => String(a || '').toLowerCase() === String(b || '').toLowerCase();
  const key = (n) => String(n || '').trim().toLowerCase();

  const HOME = 'home';
  const GRACE_MS = 15000;        // скільки тримати з'єднання з людиною, що зникла зі списку (сервер перезапускається)
  const SLOW_MS = 15000;         // стільки з'єднуємось — і досі ні: показуємо «нема зв'язку»
  const TALK_DB = -55;           // чужий голос тихіший за це — мовчить (у нього свої ворота, тиша там справжня)

  // ---------- налаштування ----------
  const DEF = { mode: 'vad', threshold: -50, ptt: { code: '', label: '', pad: -1 }, duck: true, duckTo: 0.3, mic: '', vol: {}, muted: false };
  let set = load();
  function load() {
    try { return Object.assign({}, DEF, JSON.parse(localStorage.getItem('vc') || '{}')); } catch { return Object.assign({}, DEF); }
  }
  function save() { try { localStorage.setItem('vc', JSON.stringify(set)); } catch { /* приватне вікно */ } }

  // ---------- стан ----------
  const peerId = (() => {
    const a = new Uint8Array(12);
    crypto.getRandomValues(a);
    return Array.from(a, (b) => (b % 36).toString(36)).join('');
  })();
  let roster = { rooms: [] };
  let known = false;             // чи сервер уже казав, хто де (гість без ніка до хаба не підключений)
  let want = false;              // людина хоче бути в голосі (переживає реконект)
  let ready = false;             // зайшли: є відповідь сервера й ice
  let joining = null;            // проміс входу, що зараз іде
  let queued = [];               // voiceMe/voiceSignal, що прилетіли раніше за відповідь на VoiceJoin
  let room = null;               // де я: 'home' | 't:<стіл>'
  let ice = [];
  let muted = !!set.muted;
  let deaf = false;
  let mutedBeforeDeaf = false;
  let listenOnly = false;        // мікрофона нема чи не дали — лише слухаю
  let pressed = false;           // тримаю кнопку «говорити»
  let myTalk = false, myLevel = -120;
  const peers = new Map();       // позивний → з'єднання
  let links = new Map();         // позивний → { send, recv, watch } з останнього voiceMe

  // звук
  let ac = null, gate = null, dest = null, sendTrack = null, micStream = null, micSrc = null, sink = null;
  let box = null;                // схований контейнер для <audio> людей

  // ---------- вхід / вихід ----------
  async function join() {
    if (!o.me.account) { o.toast('Посиденьки — лише для акаунтів: зареєструй нік, і заходь'); o.askNick(true, 'register'); return; }
    if (want) return;
    if (!window.RTCPeerConnection || !window.AudioWorkletNode) { o.toast('Цей браузер голосу не вміє — спробуй Chrome чи свіжий Safari', 'err'); return; }
    want = true;
    paint();
    try {
      await startAudio();
      await enter(null);
    } catch (e) {
      console.warn('[voice] join', e);
      o.toast('Халепа: не зайшлось у Посиденьки — ' + (e.message || e), 'err');
      leaveLocal();
    }
  }

  /// Попросити сервер пустити (table — голос столу, null — Посиденьки). Кличе і вхід, і реконект.
  async function enter(table) {
    if (!conn || conn.state !== 'Connected') return;
    const p = (async () => {
      // Реконект: ice уже знаємо, тож листи людей не чекають на відповідь — і панель не блимає «Заходжу…».
      if (!ice.length) ready = false;
      let r = await conn.invoke('VoiceJoin', peerId, table, muted || listenOnly, deaf);
      if (!r.ok && table) r = await conn.invoke('VoiceJoin', peerId, null, muted || listenOnly, deaf);
      if (!want) return;
      if (!r.ok) {
        o.toast(r.error || 'Не пустили в Посиденьки', 'err');
        leaveLocal();
        return;
      }
      ice = (r.ice || []).map((s) => ({ urls: s.urls, username: s.username || undefined, credential: s.credential || undefined }));
      ready = true;
      const q = queued;
      queued = [];
      for (const [ev, x] of q) (ev === 'me' ? onMe : onSignal)(x);
      paint();
    })();
    joining = p;
    try { await p; } finally { if (joining === p) joining = null; }
  }

  function leave() {
    if (conn && conn.state === 'Connected') conn.invoke('VoiceLeave').catch(() => {});
    leaveLocal();
  }

  /// Прибрати все своє: з'єднання, мікрофон, притишення радіо.
  function leaveLocal() {
    want = false;
    ready = false;
    queued = [];
    room = null;
    links = new Map();
    for (const p of [...peers.values()]) closePeer(p);
    stopAudio();
    pressed = false;
    myTalk = false;
    setDuck(false);
    paint();
  }

  // ---------- мікрофон ----------
  async function startAudio() {
    if (ac) return;
    ac = new (window.AudioContext || window.webkitAudioContext)({ latencyHint: 'interactive' });
    await ac.audioWorklet.addModule('/static/voice-worklet.js');
    gate = new AudioWorkletNode(ac, 'hl-gate', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] });
    gate.port.onmessage = (e) => onMyLevel(e.data);
    dest = ac.createMediaStreamDestination();
    dest.channelCount = 1;
    gate.connect(dest);
    sendTrack = dest.stream.getAudioTracks()[0];
    // Чужі лічильники рівня мусять бути підключені до виходу, інакше браузер їх не крутить: через нульову гучність.
    sink = ac.createGain();
    sink.gain.value = 0;
    sink.connect(ac.destination);
    tellGate();
    await openMic();
    if (ac.state === 'suspended') await ac.resume().catch(() => {});
  }

  async function openMic() {
    closeMic();
    try {
      micStream = await navigator.mediaDevices.getUserMedia({
        audio: { deviceId: set.mic ? { ideal: set.mic } : undefined, echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 },
      });
      micSrc = ac.createMediaStreamSource(micStream);
      micSrc.connect(gate);
      listenOnly = false;
      // Мікрофон вийняли — лишаємось слухати.
      micStream.getAudioTracks()[0].addEventListener('ended', () => { listenOnly = true; tellGate(); tellServer(); paint(); o.toast('Мікрофон зник — поки лише слухаєш'); });
    } catch (e) {
      listenOnly = true;
      const denied = e && (e.name === 'NotAllowedError' || e.name === 'SecurityError');
      o.toast(denied ? 'Мікрофон не дозволено — ти в Посиденьках лише слухаєш. Дозволь його біля адреси сайту (🔒), і зайди знову'
        : 'Мікрофона не знайшлось — поки лише слухаєш', 'err');
    }
    tellGate();
  }

  function closeMic() {
    if (micSrc) { try { micSrc.disconnect(); } catch { /* уже */ } micSrc = null; }
    if (micStream) { micStream.getTracks().forEach((t) => t.stop()); micStream = null; }
  }

  function stopAudio() {
    closeMic();
    if (ac) ac.close().catch(() => {});
    ac = gate = dest = sendTrack = sink = null;
  }

  function tellGate() {
    if (!gate) return;
    gate.port.postMessage({ mode: set.mode, threshold: set.threshold, muted: muted || listenOnly, pressed });
  }

  function tellServer() {
    if (ready && conn && conn.state === 'Connected') conn.invoke('VoiceSet', muted || listenOnly, deaf).catch(() => {});
  }

  function onMyLevel(m) {
    if (!m) return;
    myLevel = m.level;
    if (m.open !== myTalk) { myTalk = m.open; paintTalk(); }
    paintMeter();
  }

  function setMuted(on) {
    if (deaf && !on) setDeafLocal(false);
    muted = on;
    set.muted = on;
    save();
    tellGate();
    tellServer();
    paint();
  }

  function setDeafLocal(on) {
    deaf = on;
    if (on) { mutedBeforeDeaf = muted; muted = true; }
    else muted = mutedBeforeDeaf;
    for (const p of peers.values()) applyPlayback(p);
    tellGate();
  }
  function setDeaf(on) { setDeafLocal(on); tellServer(); paint(); updateDuck(); }

  function setPressed(on) {
    if (pressed === on) return;
    pressed = on;
    tellGate();
    paint();
  }

  // ---------- з'єднання з людьми ----------
  function ensurePeer(id) {
    let p = peers.get(id);
    if (p) { p.goneAt = 0; return p; }
    const pc = new RTCPeerConnection({ iceServers: ice });
    p = {
      id, pc, polite: peerId > id, makingOffer: false, ignoreOffer: false, chain: Promise.resolve(),
      link: { send: false, recv: false, watch: false }, sender: null, audio: null, stream: null, src: null, meter: null,
      level: -120, talk: false, state: 'new', since: Date.now(), goneAt: 0, restartAt: 0, iceOut: [], iceTimer: 0,
    };
    peers.set(id, p);
    p.sender = pc.addTrack(sendTrack, dest.stream);
    p.sender.replaceTrack(null).catch(() => {});
    pc.onnegotiationneeded = async () => {
      try {
        p.makingOffer = true;
        await pc.setLocalDescription();
        sendSignal(p, { d: pc.localDescription });
      } catch (e) { console.warn('[voice] offer', e); }
      finally { p.makingOffer = false; }
    };
    pc.onicecandidate = (e) => { if (e.candidate) queueIce(p, e.candidate); };
    pc.onconnectionstatechange = () => {
      p.state = pc.connectionState;
      if (p.state === 'failed') restartIce(p);
      paintPeers();
    };
    pc.ontrack = (e) => onTrack(p, e);
    return p;
  }

  function closePeer(p) {
    peers.delete(p.id);
    clearTimeout(p.iceTimer);
    try { p.pc.close(); } catch { /* уже */ }
    if (p.src) try { p.src.disconnect(); } catch { /* уже */ }
    if (p.meter) try { p.meter.disconnect(); } catch { /* уже */ }
    if (p.audio) { p.audio.srcObject = null; p.audio.remove(); }
    if (p.talk) { p.talk = false; updateDuck(); }
  }

  function sendSignal(p, msg) {
    if (!conn || conn.state !== 'Connected') return;
    conn.invoke('VoiceSignal', p.id, JSON.stringify(msg)).catch(() => {});
  }

  /// Кандидати летять пачками: з десятком людей їх були б сотні окремих листів.
  function queueIce(p, c) {
    p.iceOut.push(c.toJSON ? c.toJSON() : c);
    if (p.iceTimer) return;
    p.iceTimer = setTimeout(() => {
      p.iceTimer = 0;
      const list = p.iceOut;
      p.iceOut = [];
      if (list.length) sendSignal(p, { c: list });
    }, 80);
  }

  function restartIce(p) {
    const now = Date.now();
    if (now - p.restartAt < 15000) return;
    p.restartAt = now;
    try { p.pc.restartIce(); } catch (e) { console.warn('[voice] restartIce', e); }
  }

  function onSignal(x) {
    if (!want || !x || !x.from) return;
    if (!ready) { queued.push(['signal', x]); return; }
    let msg;
    try { msg = JSON.parse(x.data); } catch { return; }
    const p = ensurePeer(x.from);
    if (!links.has(x.from)) p.goneAt = p.goneAt || Date.now();   // прийшов раніше за voiceMe — нехай доведе, що він тут
    p.chain = p.chain.then(() => handleSignal(p, msg)).catch((e) => console.warn('[voice] signal', e));
  }

  async function handleSignal(p, msg) {
    const pc = p.pc;
    if (msg.d) {
      const offer = msg.d.type === 'offer';
      const collision = offer && (p.makingOffer || pc.signalingState !== 'stable');
      p.ignoreOffer = !p.polite && collision;
      if (p.ignoreOffer) return;
      await pc.setRemoteDescription(msg.d);
      if (offer) {
        await pc.setLocalDescription();
        sendSignal(p, { d: pc.localDescription });
      }
    }
    for (const c of msg.c || []) {
      try { await pc.addIceCandidate(c); }
      catch (e) { if (!p.ignoreOffer) console.warn('[voice] ice', e); }
    }
  }

  function onTrack(p, e) {
    if (e.track.kind !== 'audio') return;
    const stream = (e.streams && e.streams[0]) || new MediaStream([e.track]);
    p.stream = stream;
    if (!box) {
      box = document.createElement('div');
      box.hidden = true;
      document.body.appendChild(box);
    }
    if (!p.audio) {
      p.audio = document.createElement('audio');
      p.audio.autoplay = true;
      p.audio.setAttribute('playsinline', '');
      box.appendChild(p.audio);
    }
    p.audio.srcObject = stream;
    applyPlayback(p);
    p.audio.play().catch(() => needGesture());
    // Рівень міряємо в аудіопотоці: і для кільця «говорить», і щоб притишити радіо навіть у схованій вкладці.
    if (ac && gate) {
      if (p.src) try { p.src.disconnect(); } catch { /* уже */ }
      p.src = ac.createMediaStreamSource(stream);
      if (!p.meter) {
        p.meter = new AudioWorkletNode(ac, 'hl-meter', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] });
        p.meter.port.onmessage = (ev) => onLevel(p, ev.data);
        p.meter.connect(sink);
      }
      p.src.connect(p.meter);
    }
  }

  let gestureAsked = false;
  /// Браузер не дав грати без дотику до сторінки — перший дотик вмикає всіх.
  function needGesture() {
    if (gestureAsked) return;
    gestureAsked = true;
    o.toast('Тицни будь-де на сторінці — і почуєш Посиденьки');
    const go = () => {
      gestureAsked = false;
      if (ac) ac.resume().catch(() => {});
      for (const p of peers.values()) if (p.audio) p.audio.play().catch(() => {});
    };
    document.addEventListener('pointerdown', go, { once: true, capture: true });
    document.addEventListener('keydown', go, { once: true, capture: true });
  }

  function applyPlayback(p) {
    if (!p.audio) return;
    p.audio.muted = deaf || !p.link.recv;
    p.audio.volume = volOf(nickOf(p.id));
  }

  function onLevel(p, db) {
    p.level = db;
    const talk = db > TALK_DB && p.link.recv && !deaf;
    if (talk === p.talk) return;
    p.talk = talk;
    paintTalk();
    updateDuck();
  }

  function applyLink(p, link) {
    p.link = link;
    p.sender.replaceTrack(link.send ? sendTrack : null).catch(() => {});
    applyPlayback(p);
    if (!link.recv && p.talk) { p.talk = false; paintTalk(); updateDuck(); }
  }

  // ---------- події сервера ----------
  function onMe(x) {
    if (!want || !x) return;
    if (!ready) { queued.push(['me', x]); return; }
    const moved = room !== null && room !== x.room;
    room = x.room;
    links = new Map((x.links || []).map((l) => [l.peer, l]));
    for (const p of [...peers.values()]) {
      if (links.has(p.id)) continue;
      // Перейшли в іншу кімнату — рвемо одразу; людина зникла зі списку — даємо їй час повернутись (сервер перезапускається).
      if (moved) closePeer(p);
      else if (!p.goneAt) p.goneAt = Date.now();
    }
    for (const l of links.values()) applyLink(ensurePeer(l.peer), l);
    paint();
  }

  function onRoster(r) {
    known = true;
    roster = r && r.rooms ? r : { rooms: [] };
    for (const p of peers.values()) applyPlayback(p);   // гучність — за ніком, а нік міг щойно з'явитись
    paint();
    if (o && o.onRoster) o.onRoster();
  }

  function onKick(x) {
    leaveLocal();
    o.toast((x && x.text) || 'Тебе вивели з Посиденьок');
  }

  // Раз на секунду: ті, хто зник і не повернувся, — геть; хто довго не з'єднується — показати.
  setInterval(() => {
    const now = Date.now();
    let changed = false;
    for (const p of [...peers.values()]) {
      if (p.goneAt && now - p.goneAt > GRACE_MS) { closePeer(p); changed = true; }
      else if (p.state !== 'connected' && now - p.since > SLOW_MS && !p.slow) { p.slow = true; changed = true; }
      else if (p.state === 'connected' && p.slow) { p.slow = false; changed = true; }
    }
    if (changed) paintPeers();
  }, 1000);

  // ---------- радіо притихає ----------
  let duckOn = false, duckCur = 1, duckTimer = 0;
  function updateDuck() {
    let any = false;
    for (const p of peers.values()) if (p.talk) { any = true; break; }
    setDuck(any && set.duck);
  }
  function setDuck(on) {
    if (duckOn === on && duckTimer) return;
    duckOn = on;
    clearInterval(duckTimer);
    const target = on ? set.duckTo : 1;
    // Униз — швидко (слово не має тонути в музиці), угору — поволі (між словами музика не стрибає).
    const step = on ? 0.12 : 0.035;
    duckTimer = setInterval(() => {
      duckCur = duckCur > target ? Math.max(target, duckCur - step) : Math.min(target, duckCur + step);
      if (o && o.duck) o.duck(duckCur);
      if (duckCur === target) { clearInterval(duckTimer); duckTimer = 0; }
    }, 40);
  }

  // ---------- кнопка «говорити» ----------
  const typing = (t) => t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName));
  let binding = false;
  addEventListener('keydown', (e) => {
    if (binding) {
      e.preventDefault();
      e.stopPropagation();
      if (e.code === 'Escape') { stopBinding(); return; }
      set.ptt = { code: e.code, label: keyLabel(e), pad: -1 };
      save();
      stopBinding();
      return;
    }
    if (!ready || set.mode !== 'ptt' || !set.ptt.code || e.code !== set.ptt.code || e.repeat) return;
    if (typing(e.target) && e.key.length === 1) return;   // друкуєш у полі — це буква, а не «говорити»
    setPressed(true);
  }, true);
  addEventListener('keyup', (e) => {
    if (set.ptt.code && e.code === set.ptt.code) setPressed(false);
  }, true);
  // Відпускання клавіші у схованій вкладці не прийде — відпускаємо самі.
  addEventListener('blur', () => setPressed(false));
  document.addEventListener('visibilitychange', () => { if (document.hidden) setPressed(false); });

  function keyLabel(e) {
    if (e.code.startsWith('Key')) return e.code.slice(3);
    if (e.code.startsWith('Digit')) return e.code.slice(5);
    const names = { Space: 'Пробіл', Backquote: '`', ControlLeft: 'Ctrl ліворуч', ControlRight: 'Ctrl праворуч', AltLeft: 'Alt ліворуч',
      AltRight: 'Alt праворуч', ShiftLeft: 'Shift ліворуч', ShiftRight: 'Shift праворуч', CapsLock: 'Caps Lock', Tab: 'Tab' };
    return names[e.code] || e.code;
  }

  // Кнопка пада: браузер подій не шле, тож поки вона призначена — опитуємо сам.
  let padTimer = 0, padPrev = [];
  function padPoll() {
    const pads = (navigator.getGamepads && navigator.getGamepads()) || [];
    const now = [];
    for (const g of pads) if (g) g.buttons.forEach((b, i) => { if (b.pressed) now[i] = true; });
    if (binding) {
      const i = now.findIndex((v, j) => v && !padPrev[j]);
      if (i >= 0) { set.ptt = { code: '', label: 'кнопка пада ' + i, pad: i }; save(); stopBinding(); }
    } else if (ready && set.mode === 'ptt' && set.ptt.pad >= 0) setPressed(!!now[set.ptt.pad]);
    padPrev = now;
  }
  function syncPadPoll() {
    const need = binding || (want && set.mode === 'ptt' && set.ptt.pad >= 0);
    if (need && !padTimer) padTimer = setInterval(padPoll, 30);
    if (!need && padTimer) { clearInterval(padTimer); padTimer = 0; }
  }
  function startBinding() { binding = true; syncPadPoll(); paint(); }
  function stopBinding() { binding = false; syncPadPoll(); paint(); }

  // ---------- хто є хто ----------
  function myRoom() { return roster.rooms.find((r) => r.id === room) || null; }
  function homeRoom() { return roster.rooms.find((r) => r.id === HOME) || null; }
  function nickOf(peer) {
    for (const r of roster.rooms) for (const m of r.members || []) if (m.peer === peer) return m.nick;
    return '';
  }
  function memberOf(peer) {
    for (const r of roster.rooms) for (const m of r.members || []) if (m.peer === peer) return m;
    return null;
  }
  function volOf(nick) {
    const v = set.vol[key(nick)];
    return typeof v === 'number' ? Math.min(1, Math.max(0, v)) : 1;
  }
  function total() { return roster.rooms.reduce((n, r) => n + (r.members || []).length, 0); }
  function roomLabel(r) { return r.id === HOME ? '🪑 Посиденьки' : '🎲 ' + r.title; }

  // ---------- шапка й панель ----------
  let btn = null, panel = null, open = false, listSig = '';

  function mountUi() {
    btn = document.getElementById('vcBtn');
    if (!btn) return;
    btn.onclick = (e) => { e.stopPropagation(); setOpen(!open); };
    panel = document.createElement('div');
    panel.id = 'vcPanel';
    panel.className = 'vcpanel';
    panel.hidden = true;
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-label', 'Посиденьки — голос');
    document.body.appendChild(panel);
    panel.addEventListener('click', onPanelClick);
    panel.addEventListener('input', onPanelInput);
    panel.addEventListener('change', onPanelInput);
    // «Тримай і говори» пальцем: вказівник захоплюємо, щоб відпускання прийшло сюди, навіть коли палець з'їхав.
    let holding = false;
    panel.addEventListener('pointerdown', (e) => {
      const hold = e.target.closest('[data-hold]');
      if (!hold) return;
      e.preventDefault();
      try { hold.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
      holding = true;
      setPressed(true);
    });
    const release = () => { if (holding) { holding = false; setPressed(false); } };
    for (const ev of ['pointerup', 'pointercancel', 'lostpointercapture']) panel.addEventListener(ev, release);
    panel.addEventListener('contextmenu', (e) => { if (e.target.closest('[data-hold]')) e.preventDefault(); });
    // Шлях події, а не closest: кнопка, на яку клацнули, могла вже зникнути з перемальованої панелі.
    document.addEventListener('click', (e) => {
      const path = e.composedPath();
      if (open && !path.includes(panel) && !path.includes(btn)) setOpen(false);
    });
    document.addEventListener('keydown', (e) => { if (open && e.key === 'Escape' && !binding) setOpen(false); });
    paint();
  }

  function setOpen(on) {
    open = on;
    if (!panel) return;
    panel.hidden = !on;
    btn.setAttribute('aria-expanded', String(on));
    listSig = '';
    if (on && want && ready) loadMics();
    paint();
  }

  function paint() {
    syncPadPoll();
    paintBtn();
    if (!panel || !open) return;
    const sig = JSON.stringify([want, ready, room, listenOnly, muted, deaf, set.mode, set.ptt, set.duck, binding, o.me.account,
      roster, [...links.values()], mics.map((m) => m.deviceId), set.mic]);
    if (sig !== listSig) {
      listSig = sig;
      const keep = panel.querySelector('.vc-set');
      const wasOpen = keep ? keep.open : false;
      panel.innerHTML = panelHtml();
      const s = panel.querySelector('.vc-set');
      if (s && wasOpen) s.open = true;
    }
    paintPeers();
    paintTalk();
    paintMeter();
  }

  function paintBtn() {
    if (!btn) return;
    const n = total();
    const badge = btn.querySelector('.vc-n');
    if (badge) { badge.textContent = String(n); badge.hidden = n === 0 || (want && ready); }
    btn.classList.toggle('on', want && ready);
    btn.classList.toggle('live', n > 0);
    btn.classList.toggle('muted', want && ready && (muted || listenOnly));
    const ico = btn.querySelector('.vc-ico');
    if (ico) ico.textContent = want && ready && (muted || listenOnly) ? '🔇' : '🎙';
    const home = homeRoom();
    const who = home ? home.members.map((m) => m.nick).join(', ') : '';
    btn.title = want && ready ? 'Ти в голосі: ' + (myRoom() ? roomLabel(myRoom()) : 'Посиденьки') + ' — натисни, щоб керувати'
      : who ? 'Посиденьки — зараз там: ' + who : 'Посиденьки — голосовий чат. Зайди першим';
  }

  function memberRow(m, meRow) {
    const p = meRow ? null : peers.get(m.peer);
    const l = meRow ? null : links.get(m.peer);
    const marks = [];
    if (m.deaf) marks.push('<span title="нікого не чує">🙉</span>');
    else if (m.muted) marks.push('<span title="мікрофон вимкнено">🔇</span>');
    if (l && !l.recv) marks.push('<span class="vc-rule" title="За правилами гри ти зараз його не чуєш">🌙 не чути</span>');
    if (l && !l.send) marks.push('<span class="vc-rule" title="За правилами гри він зараз тебе не чує">🤫 тебе не чує</span>');
    const vol = meRow ? '' : '<input class="vc-vol" type="range" min="0" max="100" step="1" data-vol="' + esc(m.nick) + '" value="'
      + Math.round(volOf(m.nick) * 100) + '" title="Гучність: ' + esc(m.nick) + '" aria-label="Гучність ' + esc(m.nick) + '">';
    const hue = window.HPeople && HPeople.hue ? HPeople.hue(m.nick) : 200;
    return '<div class="vc-m' + (meRow ? ' me' : '') + '" data-peer="' + esc(m.peer) + '" style="--h:' + hue + '">'
      + '<span class="vc-ava" aria-hidden="true">' + esc((m.nick || '?').trim().charAt(0).toUpperCase()) + '</span>'
      + '<button type="button" class="vc-nick" data-who="' + esc(m.nick) + '">' + esc(m.nick) + (meRow ? ' <span class="muted">(ти)</span>' : '') + '</button>'
      + '<span class="vc-marks">' + marks.join('') + '</span>'
      + '<span class="vc-state"></span>' + vol + '</div>';
  }

  function panelHtml() {
    const h = [];
    h.push('<div class="vc-head"><b>' + (ready && myRoom() ? esc(roomLabel(myRoom())) : '🪑 Посиденьки') + '</b>'
      + '<button type="button" class="icon ghost vc-x" data-act="close" title="Сховати" aria-label="Сховати">✕</button></div>');
    if (!want || !ready) {
      const home = homeRoom();
      if (home && home.members.length) h.push('<p class="vc-who">Зараз тут: ' + home.members.map((m) => '<b>' + esc(m.nick) + '</b>').join(', ') + '</p>');
      else if (known) h.push('<p class="vc-who muted">Поки порожньо — зайди першим, решта підтягнеться.</p>');
      else h.push('<p class="vc-who muted">Голосовий чат просто на сайті — без Діскорда.</p>');
      if (!o.me.account) {
        h.push('<p class="muted small">Посиденьки — лише для акаунтів: так ніхто чужий не влізе в розмову.</p>'
          + '<button type="button" class="primary vc-join" data-act="register">Зареєструвати нік</button>');
      } else {
        h.push('<button type="button" class="primary vc-join" data-act="join"' + (want ? ' disabled' : '') + '>'
          + (want ? '<span class="spin"></span> Заходжу…' : '🎙 Зайти в Посиденьки') + '</button>'
          + '<p class="muted small">Браузер спитає дозволу на мікрофон. Краще в навушниках — тоді радіо не лізе в мікрофон.</p>');
      }
      h.push(othersHtml());
      return h.join('');
    }
    const r = myRoom();
    const mine = r ? r.members : [];
    const meM = mine.find((m) => m.peer === peerId) || { peer: peerId, nick: o.me.nick, muted, deaf };
    h.push('<div class="vc-list">' + memberRow(meM, true) + mine.filter((m) => m.peer !== peerId).map((m) => memberRow(m, false)).join('')
      + (mine.length <= 1 ? '<p class="muted small vc-alone">Поки ти тут сам — поклич когось у Балачках.</p>' : '') + '</div>');
    // керування
    h.push('<div class="vc-ctl">'
      + '<button type="button" class="vc-b' + (muted || listenOnly ? ' off' : '') + '" data-act="mute"' + (listenOnly ? ' disabled title="Мікрофона нема — лише слухаєш"' : '') + '>'
      + (muted || listenOnly ? '🔇 Мікрофон вимкнено' : '🎙 Мікрофон') + '</button>'
      + '<button type="button" class="vc-b' + (deaf ? ' off' : '') + '" data-act="deaf" title="Нікого не чути (і тебе теж)">' + (deaf ? '🙉 Нікого не чую' : '🎧 Чую всіх') + '</button>'
      + '<button type="button" class="vc-b vc-leave" data-act="leave">Вийти</button></div>');
    if (set.mode === 'ptt' && !listenOnly) {
      h.push('<button type="button" class="vc-hold" data-hold title="Тримай і говори">'
        + (set.ptt.label ? 'Тримай «' + esc(set.ptt.label) + '» або цю кнопку — і говори' : 'Тримай цю кнопку — і говори') + '</button>');
    }
    if (room !== HOME) h.push('<button type="button" class="ghost small vc-home" data-act="home">↩ Назад у Посиденьки</button>');
    h.push(settingsHtml());
    h.push(othersHtml());
    return h.join('');
  }

  function othersHtml() {
    const others = roster.rooms.filter((r) => r.id !== room && (r.id !== HOME || (want && ready)));
    if (!others.length) return '';
    return '<div class="vc-else">' + others.map((r) => '<div class="small"><b>' + esc(roomLabel(r)) + '</b>: '
      + r.members.map((m) => esc(m.nick)).join(', ') + '</div>').join('') + '</div>';
  }

  function settingsHtml() {
    const opt = (v, t) => '<option value="' + v + '"' + (set.mode === v ? ' selected' : '') + '>' + t + '</option>';
    const micOpts = ['<option value="">Типовий мікрофон</option>'].concat(mics.map((m, i) => '<option value="' + esc(m.deviceId) + '"'
      + (set.mic === m.deviceId ? ' selected' : '') + '>' + esc(m.label || 'Мікрофон ' + (i + 1)) + '</option>'));
    return '<details class="vc-set"><summary>⚙ Мікрофон і звук</summary>'
      + '<label class="vc-row">Коли говорити <select data-set="mode">' + opt('vad', 'Від голосу') + opt('ptt', 'Поки тримаю кнопку') + opt('open', 'Завжди відкрито') + '</select></label>'
      + (set.mode === 'vad' ? '<label class="vc-row">Поріг <input type="range" min="-75" max="-20" step="1" data-set="threshold" value="' + set.threshold + '"></label>'
        + '<div class="vc-meter" title="Рівень мікрофона; риска — поріг: голос має заходити за неї"><i class="vc-lvl"></i><b class="vc-thr"></b></div>' : '')
      + (set.mode === 'ptt' ? '<div class="vc-row">Кнопка <button type="button" class="vc-b" data-act="bind">'
        + (binding ? 'Натисни клавішу чи кнопку пада… (Esc — скасувати)' : set.ptt.label ? '«' + esc(set.ptt.label) + '» — змінити' : 'Обрати клавішу чи кнопку пада') + '</button></div>'
        + '<p class="muted small">Клавіша працює, лише коли вкладка Глечиків перед очима. У грі на весь екран — краще «Від голосу». '
        + 'На Steam Deck можна призначити задній гріп на клавішу в Steam Input.</p>' : '')
      + '<label class="vc-row">Мікрофон <select data-set="mic">' + micOpts.join('') + '</select></label>'
      + '<label class="vc-row vc-check"><input type="checkbox" data-set="duck"' + (set.duck ? ' checked' : '') + '> Притишувати радіо, коли хтось говорить</label>'
      + '</details>';
  }

  /// Живе: хто говорить (кільця), стан з'єднань — без перемальовування панелі.
  function paintTalk() {
    if (btn) btn.classList.toggle('talk', (want && ready && myTalk) || [...peers.values()].some((p) => p.talk));
    if (!panel || !open) return;
    for (const el of panel.querySelectorAll('.vc-m')) {
      const peer = el.dataset.peer;
      const t = peer === peerId ? myTalk : !!(peers.get(peer) && peers.get(peer).talk);
      el.classList.toggle('talk', t);
    }
    const hold = panel.querySelector('.vc-hold');
    if (hold) hold.classList.toggle('on', pressed);
  }

  function paintPeers() {
    if (!panel || !open) return;
    for (const el of panel.querySelectorAll('.vc-m:not(.me)')) {
      const p = peers.get(el.dataset.peer);
      const s = el.querySelector('.vc-state');
      if (!s) continue;
      let text = '', cls = '';
      if (!p || p.state === 'new' || p.state === 'connecting') { text = p && p.slow ? '⚠ нема зв\'язку' : 'з\'єднуюсь…'; cls = p && p.slow ? 'bad' : 'wait'; }
      else if (p.state === 'disconnected') { text = 'зв\'язок хитається'; cls = 'wait'; }
      else if (p.state === 'failed' || p.state === 'closed') { text = '⚠ нема зв\'язку'; cls = 'bad'; }
      s.textContent = text;
      s.className = 'vc-state ' + cls;
      s.title = cls === 'bad' ? 'Напряму не пробились. Буває з мобільним інтернетом — тоді потрібен ретранслятор. Скажи розробнику 💡' : '';
    }
  }

  function paintMeter() {
    if (!panel || !open) return;
    const lvl = panel.querySelector('.vc-lvl');
    if (!lvl) return;
    const pct = (db) => Math.max(0, Math.min(100, ((db + 80) / 70) * 100));
    lvl.style.width = pct(myLevel) + '%';
    lvl.classList.toggle('open', myTalk);
    panel.querySelector('.vc-thr').style.left = pct(set.threshold) + '%';
  }

  let mics = [];
  async function loadMics() {
    try {
      const list = await navigator.mediaDevices.enumerateDevices();
      mics = list.filter((d) => d.kind === 'audioinput' && d.deviceId && d.deviceId !== 'default' && d.deviceId !== 'communications');
      paint();
    } catch { /* нема то й нема */ }
  }

  function onPanelClick(e) {
    const b = e.target.closest('[data-act]');
    if (!b) return;
    const act = b.dataset.act;
    if (act === 'close') setOpen(false);
    else if (act === 'join') join().then(() => { if (open) loadMics(); });
    else if (act === 'register') { setOpen(false); o.askNick(true, 'register'); }
    else if (act === 'leave') leave();
    else if (act === 'mute') setMuted(!muted);
    else if (act === 'deaf') setDeaf(!deaf);
    else if (act === 'bind') { if (binding) stopBinding(); else startBinding(); }
    else if (act === 'home') follow(null, true);
  }

  function onPanelInput(e) {
    const t = e.target;
    if (t.dataset.vol != null) {
      set.vol[key(t.dataset.vol)] = +t.value / 100;
      save();
      for (const p of peers.values()) if (same(nickOf(p.id), t.dataset.vol)) applyPlayback(p);
      return;
    }
    const k = t.dataset.set;
    if (!k || e.type === 'input' && k !== 'threshold') return;
    if (k === 'threshold') { set.threshold = +t.value; save(); tellGate(); paintMeter(); return; }
    if (k === 'mode') { set.mode = t.value; setPressed(false); }
    else if (k === 'duck') { set.duck = t.checked; updateDuck(); }
    else if (k === 'mic') { set.mic = t.value; if (ac) openMic().then(() => { tellServer(); paint(); }); }
    save();
    tellGate();
    paint();
  }

  // ---------- столи (етап 2 доповнює) ----------
  /// Перейти в голос столу (null — у Посиденьки). byHand — людина сама натиснула: відмову показати.
  async function follow(table, byHand) {
    if (!ready || !conn || conn.state !== 'Connected') return;
    const err = await conn.invoke('VoiceFollow', table).catch(() => null);
    if (err && byHand) o.toast(err);
  }

  // ---------- публічне ----------
  window.HVoice = {
    init(opts) {
      o = opts;
      if (o.esc) esc = o.esc;
      mountUi();
    },
    attach(c) {
      conn = c;
      c.on('voice', onRoster);
      c.on('voiceMe', onMe);
      c.on('voiceSignal', onSignal);
      c.on('voiceKick', onKick);
    },
    /// Після реконекту (чи сервера після деплою) — зайти знову з тим самим позивним: з'єднання з людьми живуть.
    reconnected() {
      if (!want) return;
      const table = room && room !== HOME ? room.slice(2) : null;
      enter(table).catch((e) => console.warn('[voice] rejoin', e));
    },
    /// Для списку людей: 🎙 біля тих, хто в голосі.
    inVoice(nick) { return roster.rooms.some((r) => (r.members || []).some((m) => same(m.nick, nick))); },
    get joined() { return want && ready; },
    open: () => setOpen(true),
    /// Для консолі й перевірок: що зараз із голосом і з кожним з'єднанням.
    stats: () => ({
      peer: peerId, want, ready, room, muted, deaf, listenOnly, talk: myTalk, level: myLevel, mode: set.mode,
      peers: [...peers.values()].map((p) => ({ id: p.id, nick: nickOf(p.id), state: p.state, talk: p.talk, level: p.level,
        send: p.link.send, recv: p.link.recv, gone: !!p.goneAt, polite: p.polite })),
    }),
  };
})();
