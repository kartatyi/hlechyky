/*
  Прилади для замірів у сторінці сайту (прохід №3, п. 254; виросли з qa/instr.js проходу №2):
  лічильники rAF-колбеків, обробників 'frame'/'room' SignalR, getComputedStyle і слухачів document/window.

  Як підключити: cdp2.py --instr --url … (ставиться ДО всіх скриптів сторінки — тоді слухачі рахуються від нуля),
  або просто виконати цей файл у вже відкритій сторінці (--js): тоді лічильники слухачів — приріст від цієї миті.

    __perf.on = true; __perf.reset();      — почати замір
    … гра, партія, лобі …
    __perf.report()                        — { secs, raf: [{k, perSec, avg, max, over4}], frames, rooms, gcsPerSec, pendingRaf }
    __perf.listeners                       — 'doc:keydown' → скільки додано мінус знято (витік = росте після виходу зі столу)

  rAF рахується за першими 70 знаками тексту колбека — видно, чия анімація крутиться. pendingRaf > 0 у лобі чи на
  статичному столі — хтось крутить кадри в порожнечу. Обробники SignalR загортаються, щойно прийде перше повідомлення.
*/
(() => {
  if (window.__perf) return;
  const P = window.__perf = { raf: {}, frames: [], rooms: [], on: false, gcs: 0, listeners: {}, conn: null, t0: performance.now() };

  const origRaf = window.requestAnimationFrame.bind(window);
  const origCancel = window.cancelAnimationFrame.bind(window);
  P.pending = new Set();
  window.requestAnimationFrame = (cb) => {
    const key = String(cb).replace(/\s+/g, ' ').slice(0, 70);
    const id = origRaf((ts) => {
      P.pending.delete(id);
      if (!P.on) return cb(ts);
      const t0 = performance.now();
      try { return cb(ts); } finally {
        const dt = performance.now() - t0;
        const r = P.raf[key] || (P.raf[key] = { n: 0, sum: 0, max: 0, over4: 0 });
        r.n++; r.sum += dt; r.max = Math.max(r.max, dt); if (dt > 4) r.over4++;
      }
    });
    P.pending.add(id);
    return id;
  };
  window.cancelAnimationFrame = (id) => { P.pending.delete(id); return origCancel(id); };

  const gcs = window.getComputedStyle.bind(window);
  window.getComputedStyle = (...a) => { if (P.on) P.gcs++; return gcs(...a); };

  for (const [name, target] of [['doc', document], ['win', window]]) {
    const add = target.addEventListener.bind(target), rem = target.removeEventListener.bind(target);
    target.addEventListener = (type, fn, opt) => { const k = name + ':' + type; P.listeners[k] = (P.listeners[k] || 0) + 1; return add(type, fn, opt); };
    target.removeEventListener = (type, fn, opt) => { const k = name + ':' + type; P.listeners[k] = (P.listeners[k] || 0) - 1; return rem(type, fn, opt); };
  }

  // SignalR мініфікований: імена полів (nt — обробники, Wt — виклик обробника) міняються від версії до версії,
  // тож шукаємо їх за змістом, а не за назвою.
  const handlersOf = (conn) => {
    for (const k of Object.keys(conn)) {
      const v = conn[k];
      if (v && typeof v === 'object' && !Array.isArray(v) && (Array.isArray(v.room) || Array.isArray(v.frame) || Array.isArray(v.rooms))) return v;
    }
    return null;
  };
  const wrap = (conn) => {
    const map = handlersOf(conn);
    if (!map) return;
    for (const name of ['frame', 'room']) {
      const hs = map[name];
      if (!hs || hs.__w) continue;
      const list = hs.map((h) => function (...a) {
        if (!P.on) return h.apply(this, a);
        const t0 = performance.now();
        try { return h.apply(this, a); } finally { (name === 'frame' ? P.frames : P.rooms).push(performance.now() - t0); }
      });
      list.__w = true;
      map[name] = list;
    }
  };
  const hook = () => {
    const S = window.signalR;
    if (!S || !S.HubConnection) return false;
    const proto = S.HubConnection.prototype;
    const name = Object.getOwnPropertyNames(proto).find((n) => {
      if (n === 'constructor') return false;
      const d = Object.getOwnPropertyDescriptor(proto, n);
      return d && typeof d.value === 'function' && /No client method/.test(String(d.value));
    });
    if (!name) { console.warn('[instr] не знайшов, як SignalR кличе обробники — кадри не рахуються'); return true; }
    const orig = proto[name];
    proto[name] = function (...a) { if (!P.conn) P.conn = this; wrap(this); return orig.apply(this, a); };
    return true;
  };
  if (!hook()) {
    // Ставимось до скриптів сторінки: signalR ще нема — чекаємо, поки завантажиться.
    const t = setInterval(() => { if (hook()) clearInterval(t); }, 20);
    setTimeout(() => clearInterval(t), 30000);
  }

  P.reset = () => { P.raf = {}; P.frames = []; P.rooms = []; P.gcs = 0; P.t0 = performance.now(); };
  P.report = () => {
    const secs = (performance.now() - P.t0) / 1000;
    const st = (a) => a.length ? { n: a.length, avg: +(a.reduce((x, y) => x + y, 0) / a.length).toFixed(3), max: +Math.max(...a).toFixed(2), over4: a.filter((x) => x > 4).length } : { n: 0 };
    const raf = Object.entries(P.raf).map(([k, r]) => ({ k, perSec: +(r.n / secs).toFixed(1), avg: +(r.sum / r.n).toFixed(3), max: +r.max.toFixed(2), over4: r.over4 }));
    return { secs: +secs.toFixed(1), raf, frames: st(P.frames), rooms: st(P.rooms), gcsPerSec: +(P.gcs / secs).toFixed(1), pendingRaf: P.pending.size };
  };
})();
