/*
  Посиденьки — голосовий чат. Один глобал — window.HVoice.

  Як це працює:
  - Голос іде напряму між браузерами (WebRTC, кожен з кожним). Сервер (VoiceChat.cs) лише каже, хто де (подія
    `voice` — усім), кого я чую і хто чує мене (`voiceMe` — мені) і передає листи, поки браузери домовляються про
    з'єднання (`voiceSignal`).
  - Позивний (peer) вкладка вигадує собі сама й тримає, поки відкрита. Сервер перезапустився з деплоєм — заходимо
    знову з тим самим позивним, а вже встановлені з'єднання з людьми живуть далі: голос не рветься. Тому людину, що
    зникла зі списку, тримаємо ще 15 с (GRACE_MS), перш ніж рвати з нею з'єднання.
  - Домовляємось за «чемними переговорами» (perfect negotiation, MDN): колізію розводить чемність — чемний (у кого
    позивний більший) відступає; перше знайомство починає лише нечемний.
  - Кожне з'єднання має свій номер сесії (sid, росте з часом), і кожен лист несе обидва: s — мого з'єднання, r — яке
    з'єднання того боку я знаю. Лист до мого старого з'єднання (r не мій) і від їхнього старого (s менший) — у кошик;
    у них новіше (s більший) — і я роблю нове. Нове з'єднання одразу каже «hello». Так пара не німіє, коли один бік
    зробив нове з'єднання (вийшов-зайшов, телефон довго був без мережі), а другий ще тримає старе.
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
  const NUDGE_MS = 4000;         // чемний чекає на першу пропозицію стільки, а тоді починає сам
  const STUCK_MS = 10000;        // домовились, а зв'язку нема стільки — перезапуск ICE
  const DEAD_MS = 25000;         // і досі нема — нечемний робить з'єднання наново (чемний — удвічі пізніше)
  const MAX_SIGNAL = 24000;      // довший лист SignalR (32 КБ) не прийме й закриє з'єднання з сервером
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
  let pressed = false;           // тримаю кнопку «говорити» — клавіша, пад чи кнопка на екрані (press)
  const press = { key: false, pad: false, hold: false };
  let acStuck = false;           // браузер приспав звук (айфон: дзвінок, заблокований екран) — чекаємо дотику
  const nickMem = new Map();     // позивний → нік: після деплою людина кілька секунд не в списку, а гучність її лишається
  // На айфоні гучність <audio> лише для читання — повзунки там не показуємо, щоб не брехали.
  const volWorks = (() => { try { const a = document.createElement('audio'); a.volume = 0.5; return a.volume === 0.5; } catch { return false; } })();
  let myTalk = false, myLevel = -120;
  const peers = new Map();       // позивний → з'єднання
  let links = new Map();         // позивний → { send, recv, watch } з останнього voiceMe

  // звук
  let ac = null, gate = null, dest = null, sendTrack = null, micStream = null, micSrc = null, sink = null;
  let box = null;                // схований контейнер для <audio> людей

  // ---------- вхід / вихід ----------
  /// Зайти: table — одразу в голос цього столу (кнопка «🎙 Говорити» на картці), null — у Посиденьки,
  /// або в голос столу, за яким сидиш (на компанію), — як і при переході.
  async function join(table) {
    if (!o.me.account) { o.toast('Посиденьки — лише для акаунтів: зареєструй нік, і заходь'); o.askNick(true, 'register'); return; }
    if (want) return;
    if (!window.RTCPeerConnection || !window.AudioWorkletNode) { o.toast('Цей браузер голосу не вміє — спробуй Chrome чи свіжий Safari', 'err'); return; }
    want = true;
    paint();
    try {
      await startAudio();
      await enter(table || autoTable());
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
    stopShare(false);
    closeViewer(false);
    want = false;
    ready = false;
    queued = [];
    room = null;
    links = new Map();
    for (const p of [...peers.values()]) closePeer(p);
    stopAudio();
    releaseAll();
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
    // Айфон приспить звук (дзвінок, заблокований екран, Bluetooth) — і мікрофон, і лічильники мовчатимуть. Будимо; не
    // вийшло — просимо дотику й чесно кажемо в панелі.
    const ctx = ac;
    ctx.onstatechange = () => {
      if (ctx !== ac) return;
      if (ctx.state === 'running') { if (acStuck) { acStuck = false; paint(); } return; }
      if (ctx.state === 'closed') return;
      ctx.resume().catch(() => {});
      setTimeout(() => { if (ctx === ac && ctx.state !== 'running') { acStuck = true; needGesture(); paint(); } }, 600);
    };
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
  function setDeaf(on) {
    setDeafLocal(on);
    if (viewer) viewer.querySelector('video').muted = deaf;
    tellServer();
    paint();
    updateDuck();
  }

  function setPressed(src, on) {
    press[src] = on;
    const now = press.key || press.pad || press.hold;
    if (pressed === now) return;
    pressed = now;
    tellGate();
    paintTalk();
  }
  function releaseAll() { press.key = press.pad = press.hold = false; setPressed('key', false); }

  // ---------- з'єднання з людьми ----------
  let sidSeq = 0;
  const newSid = () => Date.now() * 1000 + (sidSeq++ % 1000);

  function ensurePeer(id) {
    const p = peers.get(id);
    if (p) { p.goneAt = 0; return p; }
    return makePeer(id);
  }

  /// Зробити з'єднання наново (у того боку нове, чи наше давно не піднімається): те, що вже знали, — лишаємо.
  function recreatePeer(p) {
    const link = p.link, goneAt = p.goneAt;
    closePeer(p, true);
    const q = makePeer(p.id);
    q.goneAt = goneAt;
    q.slow = p.slow;
    q.born = p.born;   // «нема зв'язку» рахуємо від першої спроби, а не від кожного нового з'єднання
    applyLink(q, link);
    return q;
  }

  function makePeer(id) {
    const pc = new RTCPeerConnection({ iceServers: ice });
    const p = {
      id, pc, polite: peerId > id, makingOffer: false, ignoreOffer: false, chain: Promise.resolve(),
      link: { send: false, recv: false, watch: false }, sender: null, audio: null, stream: null, src: null, meter: null,
      level: -120, talk: false, state: 'new', since: Date.now(), goneAt: 0, restartAt: 0, iceOut: [], iceTimer: 0,
      negotiated: false, nudged: false, mySid: newSid(), theirSid: 0, discAt: 0, born: Date.now(),
    };
    peers.set(id, p);
    p.sender = pc.addTrack(sendTrack, dest.stream);
    p.sender.replaceTrack(null).catch(() => {});
    // Перше знайомство починає лише нечемний бік. Коли обидва кидали пропозиції разом, чемний відкочував свою, і
    // Chrome після такого відкату переставав збирати кандидатів: з'єднання вічно висіло «new» (заміри 30.09).
    // Чемний чекає на пропозицію; не дочекався за NUDGE_MS — починає сам (сторож нижче). Далі — як завжди.
    pc.onnegotiationneeded = () => { if (!p.polite || p.negotiated) makeOffer(p); };
    pc.onsignalingstatechange = () => {
      if (pc.signalingState !== 'stable' || !pc.remoteDescription || p.negotiated) return;
      p.negotiated = true;
      if (p.link.watch && screen) shareTo(p);
    };
    pc.onicecandidate = (e) => { if (e.candidate) queueIce(p, e.candidate); };
    pc.onconnectionstatechange = () => {
      p.state = pc.connectionState;
      p.discAt = p.state === 'disconnected' ? Date.now() : 0;
      if (p.state === 'failed') restartIce(p);
      paintPeers();
    };
    pc.ontrack = (e) => onTrack(p, e);
    sendSignal(p, { hello: 1 });   // «це моє нове з'єднання»: хто тримає старе, зробить нове й собі
    return p;
  }

  async function makeOffer(p) {
    try {
      p.makingOffer = true;
      await p.pc.setLocalDescription();
      sendSignal(p, { d: p.pc.localDescription });
    } catch (e) { console.warn('[voice] offer', e); }
    finally { p.makingOffer = false; }
  }

  function closePeer(p, again) {
    if (peers.get(p.id) === p) peers.delete(p.id);
    clearTimeout(p.iceTimer);
    try { p.pc.close(); } catch { /* уже */ }
    if (p.src) try { p.src.disconnect(); } catch { /* уже */ }
    if (p.meter) try { p.meter.disconnect(); } catch { /* уже */ }
    if (p.audio) { p.audio.srcObject = null; p.audio.remove(); }
    if (p.talk) { p.talk = false; updateDuck(); }
    if (viewing === p.id && !again) closeViewer(false);
  }

  function sendSignal(p, msg) {
    if (!conn || conn.state !== 'Connected') return;
    msg.s = p.mySid;
    msg.r = p.theirSid || 0;
    const data = JSON.stringify(msg);
    if (data.length > MAX_SIGNAL) { console.warn('[voice] завеликий лист, не шлю', data.length); return; }
    conn.invoke('VoiceSignal', p.id, data).catch(() => {});
  }

  /// Кандидати летять пачками: з десятком людей їх були б сотні окремих листів.
  function queueIce(p, c) {
    p.iceSent = (p.iceSent || 0) + 1;
    p.iceOut.push(c.toJSON ? c.toJSON() : c);
    if (p.iceTimer) return;
    p.iceTimer = setTimeout(() => {
      p.iceTimer = 0;
      const list = p.iceOut;
      p.iceOut = [];
      if (list.length) sendSignal(p, { c: list });
    }, 150);
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
    const s = +msg.s || 0, r = +msg.r || 0;
    let p = peers.get(x.from) || makePeer(x.from);
    if (!links.has(x.from)) p.goneAt = p.goneAt || Date.now();   // прийшов раніше за voiceMe — нехай доведе, що він тут
    if (r && r !== p.mySid) return;                               // до мого старого з'єднання
    if (!p.theirSid) p.theirSid = s;
    else if (s < p.theirSid) return;                              // від їхнього старого з'єднання
    else if (s > p.theirSid) { p = recreatePeer(p); p.theirSid = s; }   // у них нове — і в мене буде нове
    if (msg.hello) return;
    const target = p;
    target.chain = target.chain.then(() => handleSignal(target, msg)).catch((e) => console.warn('[voice] signal', e));
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
    p.iceGot = (p.iceGot || 0) + (msg.c || []).length;
    for (const c of msg.c || []) {
      try { await pc.addIceCandidate(c); }
      catch (e) { if (!p.ignoreOffer) console.warn('[voice] ice', e); }
    }
  }

  function onTrack(p, e) {
    const stream = (e.streams && e.streams[0]) || new MediaStream([e.track]);
    // Екран людини (відео й, якщо поділились, його звук) — окремий потік, не той, що з мікрофона.
    if (e.track.kind === 'video' || (p.stream && stream.id !== p.stream.id)) { onScreenTrack(p, stream); return; }
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
    if (link.watch && screen) shareTo(p); else unshareTo(p);
    applyPlayback(p);
    if (!link.recv && p.talk) { p.talk = false; paintTalk(); updateDuck(); }
  }

  // ---------- показ екрана ----------
  // Екран летить лише тим, хто сам натиснув «Дивитись» (сервер каже це в link.watch): кожному глядачеві — свій потік,
  // тож показувати всім підряд — це марно палити віддачу. До 2,5 Мбіт/с і 30 кадрів на глядача. Доріжки екрана
  // живуть в окремих трансиверах з'єднання: перестав показувати — вони стають неактивними, почав знову — ті самі
  // трансивери оживають (m-рядки в описі з'єднання не множаться).
  let screen = null;             // мій екран (getDisplayMedia), поки показую
  let viewing = null;            // чий екран я дивлюсь (позивний)
  let viewer = null;             // вікно глядача
  const canShare = () => !!(navigator.mediaDevices && navigator.mediaDevices.getDisplayMedia);

  async function startShare() {
    if (!ready || screen) return;
    if (!canShare()) { o.toast('Цей браузер не вміє показувати екран — спробуй Chrome на комп\'ютері'); return; }
    try {
      screen = await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: { ideal: 30, max: 30 } }, audio: true });
    } catch (e) {
      if (e && e.name !== 'NotAllowedError' && e.name !== 'AbortError') o.toast('Не вийшло показати екран — ' + (e.message || e), 'err');
      return;
    }
    const v = screen.getVideoTracks()[0];
    if (v) {
      try { v.contentHint = 'motion'; } catch { /* старий браузер */ }
      v.addEventListener('ended', () => stopShare(true));   // «Припинити показ» у самому браузері
    }
    if (conn && conn.state === 'Connected') conn.invoke('VoiceShare', true).catch(() => {});
    for (const p of peers.values()) if (p.link.watch) shareTo(p);
    paint();
  }

  function stopShare(tell) {
    if (!screen) return;
    screen.getTracks().forEach((t) => t.stop());
    screen = null;
    for (const p of peers.values()) unshareTo(p);
    if (tell && ready && conn && conn.state === 'Connected') conn.invoke('VoiceShare', false).catch(() => {});
    paint();
  }

  function shareTo(p) {
    if (!screen || !p.negotiated) return;
    p.shareTr = p.shareTr || {};
    for (const t of screen.getTracks()) {
      const tr = p.shareTr[t.kind];
      if (tr && tr.sender.track === t) continue;
      if (!tr) {
        const opts = { direction: 'sendonly', streams: [screen] };
        if (t.kind === 'video') opts.sendEncodings = [{ maxBitrate: 2500000, maxFramerate: 30 }];
        p.shareTr[t.kind] = p.pc.addTransceiver(t, opts);
      } else {
        tr.direction = 'sendonly';
        tr.sender.replaceTrack(t).catch(() => {});
        try { tr.sender.setStreams(screen); } catch { /* старий браузер */ }
      }
    }
  }

  function unshareTo(p) {
    for (const tr of Object.values(p.shareTr || {})) {
      if (!tr.sender.track && tr.direction === 'inactive') continue;
      tr.sender.replaceTrack(null).catch(() => {});
      try { tr.direction = 'inactive'; } catch { /* з'єднання вже закрите */ }
    }
  }

  function onScreenTrack(p, stream) {
    p.screen = stream;
    if (viewing === p.id) showViewer(p);
  }

  async function watch(peer) {
    if (!ready || !conn) return;
    if (viewing && viewing !== peer) closeViewer(true);
    viewing = peer;
    const err = await conn.invoke('VoiceWatch', peer, true).catch(() => 'Не вийшло');
    if (err) { viewing = null; o.toast(err); paint(); return; }
    const p = peers.get(peer);
    if (p && p.screen && p.screen.getVideoTracks().some((t) => t.readyState === 'live')) showViewer(p);
    else showViewer(p || { id: peer }, true);
    paint();
  }

  function showViewer(p, waiting) {
    if (!viewer) {
      viewer = document.createElement('div');
      viewer.className = 'vcscreen';
      viewer.innerHTML = '<div class="vcs-bar"><b class="vcs-who"></b><button type="button" class="icon ghost" data-vcs="full" title="На весь екран">⛶</button>'
        + '<button type="button" class="icon ghost" data-vcs="close" title="Не дивитись">✕</button></div>'
        + '<video class="vcs-video" autoplay playsinline></video><p class="vcs-wait muted small">Чекаю на картинку…</p>';
      document.body.appendChild(viewer);
      viewer.addEventListener('click', (e) => {
        const b = e.target.closest('[data-vcs]');
        if (!b) return;
        if (b.dataset.vcs === 'close') { closeViewer(true); return; }
        const v = viewer.querySelector('video');
        if (v.requestFullscreen) v.requestFullscreen().catch(() => {});
        else if (v.webkitEnterFullscreen) v.webkitEnterFullscreen();
      });
      viewer.querySelector('video').addEventListener('playing', () => { viewer.querySelector('.vcs-wait').hidden = true; });
    }
    viewer.querySelector('.vcs-who').textContent = '🖥 ' + (nickOf(p.id) || 'екран');
    const v = viewer.querySelector('video');
    if (!waiting && p.screen && v.srcObject !== p.screen) { v.srcObject = p.screen; v.play().catch(() => needGesture()); }
    v.muted = deaf;
    if (waiting) viewer.querySelector('.vcs-wait').hidden = false;
    viewer.hidden = false;
  }

  function closeViewer(tell) {
    if (tell && viewing && ready && conn && conn.state === 'Connected') conn.invoke('VoiceWatch', viewing, false).catch(() => {});
    const was = viewing;
    viewing = null;
    if (viewer) { const v = viewer.querySelector('video'); v.srcObject = null; viewer.hidden = true; }
    if (was) paint();
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
      // Людина зникла зі списку — даємо їй 15 с повернутись: сервер перезапускається, і тоді голос іде далі як ішов.
      // Але якщо в іншу кімнату перейшов я сам — з тими, хто лишився, глушимось в обидва боки одразу (за столом мафії
      // Посиденьки чути не мають). З'єднання не рвемо: за стіл зазвичай переходять ті самі люди, по одному, і коли
      // людина дійде, голос піде тим самим з'єднанням, без секунди тиші на нове.
      // За столом (мафія!) і тому, хто просто зник, — одразу тиша в обидва боки: 15 с чужих вух тут забагато.
      if (moved || room !== HOME) applyLink(p, { peer: p.id, send: false, recv: false, watch: false });
      if (!p.goneAt) p.goneAt = Date.now();
    }
    for (const l of links.values()) applyLink(ensurePeer(l.peer), l);
    paint();
  }

  function onRoster(r) {
    known = true;
    roster = r && r.rooms ? r : { rooms: [] };
    for (const x of roster.rooms) for (const m of x.members || []) if (m.peer) nickMem.set(m.peer, m.nick);
    for (const p of peers.values()) applyPlayback(p);   // гучність — за ніком, а нік міг щойно з'явитись
    paint();
    if (o && o.onRoster) o.onRoster();
  }

  function onKick(x) {
    leaveLocal();
    o.toast((x && x.text) || 'Тебе вивели з Посиденьок');
  }

  // Раз на секунду: ті, хто зник і не повернувся, — геть; хто довго не з'єднується — показати; сторож переговорів.
  setInterval(() => {
    const now = Date.now();
    let changed = false;
    for (const p of [...peers.values()]) {
      if (p.goneAt && now - p.goneAt > GRACE_MS) { closePeer(p); changed = true; continue; }
      if (p.state !== 'connected' && now - p.born > SLOW_MS && !p.slow) { p.slow = true; changed = true; }
      else if (p.state === 'connected' && p.slow) { p.slow = false; changed = true; }
      if (p.goneAt) continue;
      // Чемний так і не дочекався пропозиції (лист загубився, нечемний перезапускався) — починає сам.
      if (p.polite && !p.negotiated && !p.nudged && now - p.since > NUDGE_MS && p.pc.signalingState === 'stable') {
        p.nudged = true;
        makeOffer(p);
      }
      // Домовились, а зв'язку нема: кандидати загубились чи мережа змінилась — перезапускаємо ICE (нечемний, щоб не вдвох).
      if (p.negotiated && p.state !== 'connected' && now - p.since > STUCK_MS && (!p.polite || now - p.since > 2 * STUCK_MS)) restartIce(p);
      // Зв'язок хитається вже 5 с — не чекаємо, поки браузер сам визнає «failed» (це ще пів хвилини).
      if (p.discAt && now - p.discAt > 5000 && !p.polite) restartIce(p);
      // Так і не піднялось — робимо з'єднання наново (той бік побачить новий номер сесії і зробить своє).
      if (p.state !== 'connected' && now - p.since > (p.polite ? 2 * DEAD_MS : DEAD_MS)) recreatePeer(p);
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
    setPressed('key', true);
  }, true);
  addEventListener('keyup', (e) => {
    if (set.ptt.code && e.code === set.ptt.code) setPressed('key', false);
  }, true);
  // Відпускання клавіші у схованій вкладці не прийде — відпускаємо самі.
  addEventListener('blur', releaseAll);
  document.addEventListener('visibilitychange', () => { if (document.hidden) releaseAll(); });

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
    } else if (ready && set.mode === 'ptt' && set.ptt.pad >= 0) setPressed('pad', !!now[set.ptt.pad]);
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
    return nickMem.get(peer) || '';
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
    panel.addEventListener('focusout', () => setTimeout(flushPaint, 0));
    // «Тримай і говори» пальцем: вказівник захоплюємо, щоб відпускання прийшло сюди, навіть коли палець з'їхав.
    let holding = false;
    panel.addEventListener('pointerdown', (e) => {
      const hold = e.target.closest('[data-hold]');
      if (!hold) return;
      e.preventDefault();
      try { hold.setPointerCapture(e.pointerId); } catch { /* старий браузер */ }
      holding = true;
      setPressed('hold', true);
    });
    // Відпускання слухаємо на всьому документі: панель могла перемалюватись, поки палець тримав кнопку.
    const release = () => { if (holding) { holding = false; setPressed('hold', false); flushPaint(); } };
    for (const ev of ['pointerup', 'pointercancel']) document.addEventListener(ev, release, true);
    panel.addEventListener('lostpointercapture', release);
    isHolding = () => holding;
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

  let isHolding = () => false;
  let paintLater = false;
  /// Людина саме тягне повзунок, тримає «говорити» чи розгорнула список — innerHTML вибив би це з-під пальця.
  function busyPanel() {
    const a = document.activeElement;
    return isHolding() || !!(a && panel && panel.contains(a) && /^(INPUT|SELECT)$/.test(a.tagName) && a.type !== 'checkbox');
  }
  function flushPaint() { if (paintLater) { paintLater = false; paint(); } }

  function paint() {
    syncPadPoll();
    paintBtn();
    decorateAll();
    if (!panel || !open) return;
    if (busyPanel()) { paintLater = true; paintPeers(); paintTalk(); return; }
    const sig = JSON.stringify([want, ready, room, listenOnly, muted, deaf, set.mode, set.ptt, set.duck, binding, o.me.account, table, !!screen, viewing, acStuck,
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
    if (m.share && !meRow) marks.push('<button type="button" class="vc-watch' + (viewing === m.peer ? ' on' : '') + '" data-act="watch" data-peer="'
      + esc(m.peer) + '">' + (viewing === m.peer ? '🖥 Дивишся' : '🖥 Дивитись') + '</button>');
    else if (m.share) marks.push('<span title="ти показуєш екран">🖥</span>');
    if (m.deaf) marks.push('<span title="нікого не чує">🙉</span>');
    else if (m.muted) marks.push('<span title="мікрофон вимкнено">🔇</span>');
    if (l && !l.recv) marks.push('<span class="vc-rule" title="За правилами гри ти зараз його не чуєш">🌙 не чути</span>');
    if (l && !l.send) marks.push('<span class="vc-rule" title="За правилами гри він зараз тебе не чує">🤫 тебе не чує</span>');
    const vol = meRow || !volWorks ? '' : '<input class="vc-vol" type="range" min="0" max="100" step="1" data-vol="' + esc(m.nick) + '" value="'
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
    if (acStuck) h.push('<p class="vc-rulebar">⚠ Браузер приспав звук — тицни будь-де на сторінці, щоб тебе знову було чути</p>');
    const rule = ruleText();
    if (rule) h.push('<p class="vc-rulebar">' + rule + '</p>');
    h.push('<div class="vc-list">' + memberRow(meM, true) + mine.filter((m) => m.peer !== peerId).map((m) => memberRow(m, false)).join('')
      + (mine.length <= 1 ? '<p class="muted small vc-alone">Поки ти тут сам — поклич когось у Балачках.</p>' : '') + '</div>');
    // керування
    h.push('<div class="vc-ctl">'
      + '<button type="button" class="vc-b' + (muted || listenOnly ? ' off' : '') + '" data-act="mute"' + (listenOnly ? ' disabled title="Мікрофона нема — лише слухаєш"' : '') + '>'
      + (muted || listenOnly ? '🔇 Мікрофон вимкнено' : '🎙 Мікрофон') + '</button>'
      + '<button type="button" class="vc-b' + (deaf ? ' off' : '') + '" data-act="deaf" title="Нікого не чути (і тебе теж)">' + (deaf ? '🙉 Нікого не чую' : '🎧 Чую всіх') + '</button>'
      + (canShare() ? '<button type="button" class="vc-b' + (screen ? ' on' : '') + '" data-act="share">' + (screen ? '🖥 Зупинити показ' : '🖥 Показати екран') + '</button>' : '')
      + '<button type="button" class="vc-b vc-leave" data-act="leave">Вийти</button></div>');
    if (set.mode === 'ptt' && !listenOnly) {
      h.push('<button type="button" class="vc-hold" data-hold title="Тримай і говори">'
        + (set.ptt.label ? 'Тримай «' + esc(set.ptt.label) + '» або цю кнопку — і говори' : 'Тримай цю кнопку — і говори') + '</button>');
    }
    const moves = [];
    if (table && room !== 't:' + table.id) moves.push('<button type="button" class="ghost small" data-act="table">🎲 До голосу столу «' + esc(table.title) + '»</button>');
    if (room !== HOME) moves.push('<button type="button" class="ghost small" data-act="home">↩ Назад у Посиденьки</button>');
    if (moves.length) h.push('<div class="vc-moves">' + moves.join('') + '</div>');
    h.push(settingsHtml());
    h.push(othersHtml());
    return h.join('');
  }

  /// Коли гра за столом ділить голос (мафія вночі, мертві, капітан у Позивних) — одним рядком, чому когось не чути.
  function ruleText() {
    const all = [...links.values()];
    if (room === HOME || !all.length) return '';
    const hear = all.filter((l) => l.recv).length, heard = all.filter((l) => l.send).length;
    if (hear === all.length && heard === all.length) return '';
    if (!hear && !heard) return '🌙 Зараз ти нікого не чуєш і тебе ніхто — так велить гра';
    if (!heard) return '🤫 Зараз тебе не чути — так велить гра, а ти слухай';
    if (heard < all.length && hear === all.length) return '🪑 Ти на лаві: чуєш усіх, а тебе — лише такі самі, як ти';
    return '🎲 Гра ділить голос: чуєш ' + hear + ' з ' + all.length + ', тебе — ' + heard;
  }

  function othersHtml() {
    const others = roster.rooms.filter((r) => r.id !== room && (r.id !== HOME || (want && ready)));
    if (!others.length) return '';
    // Стіл — посиланням: відкрити його й звідти вже «🎙 Говорити» (у голос столу пускають тих, хто за ним сидить чи дивиться).
    return '<div class="vc-else">' + others.map((r) => '<div class="small">'
      + (r.table ? '<a href="#games/room/' + encodeURIComponent(r.table) + '" data-act="close">' + esc(roomLabel(r)) + '</a>' : '<b>' + esc(roomLabel(r)) + '</b>')
      + ': ' + r.members.map((m) => esc(m.nick)).join(', ') + '</div>').join('') + '</div>';
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
    // Кільце на чіпі за столом — лише тим, кого я справді чую (і собі, коли мене чути).
    const talking = new Set();
    if (want && ready && myTalk) talking.add(key(o.me.nick));
    for (const p of peers.values()) if (p.talk) talking.add(key(nickOf(p.id)));
    for (const chip of document.querySelectorAll('.gtable .gseat[data-nick]')) chip.classList.toggle('vc-talk', talking.has(key(chip.dataset.nick)));
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
    else if (act === 'share') { if (screen) stopShare(true); else startShare(); }
    else if (act === 'watch') { if (viewing === b.dataset.peer) closeViewer(true); else watch(b.dataset.peer); }
    else if (act === 'table' && table) follow(table.id, true);
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
    if (k === 'mode') { set.mode = t.value; releaseAll(); }
    else if (k === 'duck') { set.duck = t.checked; updateDuck(); }
    else if (k === 'mic') { set.mic = t.value; if (ac) openMic().then(() => { tellServer(); paint(); }); }
    save();
    tellGate();
    paint();
  }

  // ---------- голос столу ----------
  // Сів за стіл на компанію (3+ місця: мафія, шпигун, Своя гра, дурень на чотирьох) — голос іде за стіл: там свої
  // правила (мафія вночі чує лише мафію), а хто не грає, не чує, про що домовляються за столом. Встав — сервер сам
  // поверне в Посиденьки. На дуелях сам не переходиш: двоє за шахами зазвичай балакають з усіма в Посиденьках, а
  // перейти можна кнопкою «🎙» на картці. «↩ Назад у Посиденьки» запам'ятовує стіл — туди вже не тягне.
  let table = null;              // стіл на екрані — те, що каже core.js через app.js: { id, title, seat, max, … }
  const stayHome = new Set();    // столи, з яких людина сама пішла в Посиденьки

  /// Стіл, куди голос іде сам: на екрані, я за ним сиджу, він на компанію, і звідти я сам не йшов.
  function autoTable() {
    return table && table.seat != null && (table.max || 0) >= 3 && !stayHome.has(table.id) ? table.id : null;
  }

  function onTable(t) {
    table = t;
    const auto = autoTable();
    if (auto && want && ready && room !== 't:' + auto) follow(auto, false);
    paint();
  }

  /// Перейти в голос столу (null — у Посиденьки). byHand — людина сама натиснула: відмову показати.
  async function follow(tableId, byHand) {
    if (!ready || !conn || conn.state !== 'Connected') return;
    if (byHand) {
      if (tableId) stayHome.delete(tableId);
      else if (room && room !== HOME) stayHome.add(room.slice(2));
    }
    const err = await conn.invoke('VoiceFollow', tableId).catch(() => null);
    if (err && byHand) o.toast(err);
  }

  /// Картка столу (core.js кличе після кожного перемальовування шапки, а ми — коли змінився голос): 🎙 на чіпах тих,
  /// хто в голосі столу, кільце на тих, хто говорить, і кнопка голосу — лише на столі, що зараз на екрані.
  function decorate(el) {
    if (!el || !el.dataset || !el.dataset.room) return;
    const id = el.dataset.room;
    const head = el.querySelector('.gseats');
    if (!head) return;
    const r = roster.rooms.find((x) => x.id === 't:' + id);
    const inTable = new Set(r ? r.members.map((m) => key(m.nick)) : []);
    for (const chip of head.querySelectorAll('.gseat[data-nick]')) chip.classList.toggle('vc-in', inTable.has(key(chip.dataset.nick)));
    let b = head.querySelector('.gvc');
    const show = !!(table && table.id === id && o.me.account && window.RTCPeerConnection);
    if (!show) { if (b) b.remove(); return; }
    if (!b) {
      b = document.createElement('button');
      b.type = 'button';
      b.className = 'gseat gvc';
      b.dataset.vc = id;
      head.appendChild(b);
    }
    const here = want && ready && room === 't:' + id;
    const n = r ? r.members.length : 0;
    b.classList.toggle('on', here);
    b.textContent = here ? '🎙 Голос столу · ' + n : n ? '🎙 Тут говорять · ' + n : '🎙 Говорити';
    b.title = here ? 'Ти в голосі цього столу — натисни, щоб керувати' : 'Зайти в голос цього столу';
  }

  function decorateAll() {
    for (const el of document.querySelectorAll('.gtable[data-room]')) decorate(el);
  }

  function onVcClick(e) {
    const b = e.target.closest('[data-vc]');
    if (!b) return;
    const id = b.dataset.vc;
    if (!want) join(id);
    else if (room !== 't:' + id) follow(id, true);
    else setOpen(!open);
  }

  // ---------- публічне ----------
  window.HVoice = {
    init(opts) {
      o = opts;
      if (o.esc) esc = o.esc;
      mountUi();
      document.addEventListener('click', onVcClick);
    },
    /// app.js: біля якого столу стоїмо (core.js tableInfo) — або null.
    onTable,
    /// core.js: шапку картки столу щойно перемальовано.
    decorate,
    attach(c) {
      conn = c;
      c.on('voice', onRoster);
      c.on('voiceMe', onMe);
      c.on('voiceSignal', onSignal);
      c.on('voiceKick', onKick);
    },
    /// Після реконекту (чи сервера після деплою) — зайти знову з тим самим позивним: з'єднання з людьми живуть.
    reconnected() {
      // Поки браузер питав дозволу на мікрофон, хаб перепідключився: вхід сам зайде, коли звук запуститься.
      if (!want || !dest) return;
      const table = room && room !== HOME ? room.slice(2) : null;
      enter(table).then(() => {
        // Листи, що летіли, поки зв'язку з сервером не було, пропали — хто не з'єднаний, пробує ще раз.
        for (const p of peers.values()) if (p.state !== 'connected' && !p.polite && p.negotiated) restartIce(p);
      }).catch((e) => console.warn('[voice] rejoin', e));
    },
    /// Для списку людей: 🎙 біля тих, хто в голосі.
    inVoice(nick) { return roster.rooms.some((r) => (r.members || []).some((m) => same(m.nick, nick))); },
    get joined() { return want && ready; },
    open: () => setOpen(true),
    /// Для консолі й перевірок: що зараз із голосом і з кожним з'єднанням.
    stats: () => ({
      peer: peerId, want, ready, room, muted, deaf, listenOnly, talk: myTalk, level: myLevel, mode: set.mode,
      peers: [...peers.values()].map((p) => ({ id: p.id, nick: nickOf(p.id), state: p.state, talk: p.talk, level: p.level,
        send: p.link.send, recv: p.link.recv, gone: !!p.goneAt, polite: p.polite, sig: p.pc.signalingState, ice: p.pc.iceConnectionState,
        local: p.pc.localDescription && p.pc.localDescription.type, remote: p.pc.remoteDescription && p.pc.remoteDescription.type,
        offering: p.makingOffer, ignored: p.ignoreOffer, gather: p.pc.iceGatheringState, iceSent: p.iceSent || 0, iceGot: p.iceGot || 0 })),
    }),
  };
})();
