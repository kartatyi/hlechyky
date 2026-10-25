// Заміри й знімки графіки 0.0.5 з консолі (у грі не вантажиться): `const b = await import('/games/fura/gfxbench.js')`,
// далі `await b.suite({ tag: 'sky5', post: 'http://127.0.0.1:8798/' })` — пори доби × ракурси × пресети, PNG з canvas
// і JSON із числами (draw calls, трикутники, CPU подачі, GPU хмари / сцена / пост окремими таймер-запитами).
// Схована Browser-панель не крутить rAF і «присипляє» GPU: кадри малюємо самі (sky.draw), міряємо перемішано.

const F = () => window.__fura;
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

/** Ракурси: game — камера гри (за фурою / з кабіни), решта — вільна камера (freeCam) біля фури й у селі. */
export function views() {
  const f = F(), lv = f.level, T = f.truck.tb.translation(), yaw = f.truck.yawOf(f.truck.tb);
  const fx = Math.sin(yaw), fz = Math.cos(yaw), g = (x, z) => lv.height(x, z);
  const p = (x, z, h) => [x, g(x, z) + h, z];
  const st = f.sky.st, L = st.night ? st.moonDir : st.sunDir, lh = Math.hypot(L.x, L.z) || 1;
  // сільська вулиця: ліхтар біля села (вони — лише в селах) → найближча точка дороги, погляд уздовж неї
  const vil = lv.places.find((q) => q.id === 'honchar') || lv.places[0];
  const [vx, vz] = lv.map.nodes[vil.node];
  let lamp = null, bd = 1e9;
  const e = lv.pools ? lv.pools.instanceMatrix.array : [];
  for (let i = 0; i < e.length; i += 16) { const d = Math.hypot(e[i + 12] - vx, e[i + 14] - vz); if (d > 50 && d < bd) { bd = d; lamp = [e[i + 12], e[i + 14]]; } }
  let vq = { x: vx + 80, z: vz, tx: 1, tz: 0 }, best = 1e9;
  if (lamp) for (const r of lv.roads) for (const q of r.pts) { const d = Math.hypot(q.x - lamp[0], q.z - lamp[1]); if (d < best) { best = d; vq = q; } }
  return {
    chase: { cam: 'chase' },
    cabin: { cam: 'cabin' },
    wide: { pos: p(T.x - fx * 70 - fz * 50, T.z - fz * 70 + fx * 50, 28), look: [T.x + fx * 20, T.y, T.z + fz * 20], fov: 55 },
    cross: { pos: p(T.x + fx * 26 - fz * 4, T.z + fz * 26 + fx * 4, 1.7), look: [T.x, T.y + 1.5, T.z], fov: 55 },
    village: { pos: p(vq.x - vq.tz * 3, vq.z + vq.tx * 3, 2.6), look: p(vq.x + vq.tx * 60, vq.z + vq.tz * 60, 2.4), fov: 60 },
    sky: { pos: p(T.x - L.x / lh * 30 + fz * 14, T.z - L.z / lh * 30 - fx * 14, 3.5), look: [T.x + L.x / lh * 100, g(T.x, T.z) + 22, T.z + L.z / lh * 100], fov: 70 },
    skyup: { pos: p(T.x + fz * 14, T.z - fx * 14, 2.5), look: [T.x + fz * 14 + fx * 40, g(T.x, T.z) + 60, T.z - fx * 14 + fz * 40], fov: 75 },
  };
}

/** Розмір полотна (у схованій панелі вікно має нульовий розмір): w×h пікселів, null — як було. */
export function setSize(w, h) {
  const app = F().renderer.domElement.parentNode;
  app.style.cssText = w ? 'width:' + w + 'px;height:' + h + 'px;inset:auto;left:0;top:0' : '';
  window.dispatchEvent(new Event('resize'));
}

/** Поставити ракурс і прогнати n кадрів (хмари накопичуються, тінь оновлюється). */
export function view(name, n = 8) {
  const f = F(), v = views()[name];
  if (v.cam) { f.freeCam = null; f.cam = v.cam; } else f.freeCam = { pos: v.pos, look: v.look, fov: v.fov };
  f.sky.clouds.hasPrev = false;
  frames(n);
}
/** n кадрів гри без фізики: камера, небо, дерева, малювання. */
export function frames(n = 1) {
  const f = F();
  for (let i = 0; i < n; i++) {
    syncModel();
    placeCam();
    f.sky.tick(1 / 60, f.camera);
    f.sky.setHeadlights(f.sky.dark);
    if (f.trees) f.trees.update(f.camera.position);
    if (f.grass) f.grass.update(f.camera.position);
    f.sky.draw(f.scene, f.camera);
  }
}
// модель фури за тілами (у кадрі rAF це робить main.js)
function syncModel() {
  const f = F(), m = f.model, t = f.truck;
  const pq = (b) => { const p = b.translation(), q = b.rotation(); return [new f.camera.position.constructor(p.x, p.y, p.z), new f.camera.quaternion.constructor(q.x, q.y, q.z, q.w)]; };
  m.syncBody(m.tractor, ...pq(t.tb)); m.syncBody(m.trailer, ...pq(t.rb));
  m.syncWheels(t.tvc, m.tWheels, 1); m.syncWheels(t.rvc, m.rWheels, 1); m.setLegs(!t.coupled);
}
/** Фура зчеплена на трасі: найдовша дорога, точка на частці k довжини, права смуга, курс уздовж. */
export function onRoad(k = 0.4, kind) {
  const f = F(), lv = f.level;
  const roads = lv.roads.filter((r) => !kind || r.kind === kind).sort((a, b) => b.pts[b.pts.length - 1].s - a.pts[a.pts.length - 1].s);
  const r = roads[0], pts = r.pts, L = pts[pts.length - 1].s;
  const at = (s) => pts.reduce((b, p) => (Math.abs(p.s - s) < Math.abs(b.s - s) ? p : b), pts[0]);
  const S = r.edge.sec, o = S.shift + S.lane * (S.lanes / 2 - 0.5);
  const lane = (s) => { const p = at(s); return { x: p.x - p.tz * o, z: p.z + p.tx * o, yaw: Math.atan2(p.tx, p.tz) }; };
  const tl = lane(L * k - 7.85), tr = lane(L * k);
  f.truck.place({ tractor: { x: 0, z: 0 }, trailer: { x: tl.x, z: tl.z, yaw: tr.yaw, y: lv.height(tl.x, tl.z) } }, true);
  return { road: r.name || r.id, kind: r.kind, x: tr.x, z: tr.z };
}
// камера гри без кадру rAF: як placeCamera у main.js (спрощено: freeCam або позиція за фурою)
function placeCam() {
  const f = F(), c = f.camera;
  if (f.freeCam) { c.position.set(...f.freeCam.pos); c.lookAt(...f.freeCam.look); c.fov = f.freeCam.fov || 60; c.updateProjectionMatrix(); c.updateMatrixWorld(); f.model.setCabin(false); return; }
  const tb = f.truck.tb, t = tb.translation(), yaw = f.truck.yawOf(tb), G = f.gfx.G;
  if (f.cam === 'cabin') {
    const q = tb.rotation(), Q = new f.camera.quaternion.constructor(q.x, q.y, q.z, q.w);
    const e = f.camera.position.constructor;
    c.position.copy(new e(0.6, 1.66, 1.95).applyQuaternion(Q).add(new e(t.x, t.y, t.z)));
    c.quaternion.copy(Q).multiply(new f.camera.quaternion.constructor().setFromAxisAngle(new e(0, 1, 0), Math.PI + 0.3)).multiply(new f.camera.quaternion.constructor().setFromAxisAngle(new e(1, 0, 0), -0.12));
    c.fov = G.fov + 8; f.model.setCabin(true);
  } else {
    const long = f.truck.coupled, d = long ? 25 : 14, h = long ? 8 : 5.5;
    c.position.set(t.x - Math.sin(yaw) * d, t.y + h, t.z - Math.cos(yaw) * d);
    c.lookAt(t.x + Math.sin(yaw) * 5, t.y + 1.8, t.z + Math.cos(yaw) * 5);
    c.fov = G.fov; f.model.setCabin(false);
  }
  c.updateProjectionMatrix(); c.updateMatrixWorld();
}

/** Кадр у PNG (той самий кадр, що щойно намальовано) з підписом; post — адреса приймача. */
export async function shoot(name, label, post) {
  const f = F(), cv = f.renderer.domElement;
  frames(1);
  const c2 = document.createElement('canvas'); c2.width = cv.width; c2.height = cv.height;
  const g = c2.getContext('2d');
  g.drawImage(cv, 0, 0);
  if (label) {
    const H = c2.height, lines = label.split('\n'), lh = Math.round(H / 26);
    const fz = (i) => (i ? '500 ' : '700 ') + Math.round(H / (i ? 44 : 32)) + 'px system-ui';
    const w = Math.max(...lines.map((l, i) => { g.font = fz(i); return g.measureText(l).width; })) + 32;
    g.fillStyle = 'rgba(28,32,40,.8)'; g.beginPath(); g.roundRect(14, 14, w, lh * lines.length + 18, 10); g.fill();
    lines.forEach((l, i) => { g.fillStyle = i ? '#f2c08a' : '#ffffff'; g.font = fz(i); g.fillText(l, 30, 14 + lh * (i + 1)); });
  }
  const blob = await new Promise((res) => c2.toBlob(res, 'image/png'));
  for (let k = 0; k < 4; k++) {
    try { const r = await fetch(post + name, { method: 'POST', body: blob }); return r.ok ? blob.size : -1; } catch { await wait(300); }
  }
  return -1;
}

/** Один кадр: draw calls і трикутники (з тінню й постом). */
export function stats() {
  const r = F().renderer;
  r.info.autoReset = false; r.info.reset();
  frames(1);
  const o = { calls: r.info.render.calls, triangles: r.info.render.triangles };
  r.info.autoReset = true;
  return o;
}

/** n кадрів: CPU подачі (медіана) і GPU хмари / сцена (з тінню) / пост (таймер-запити, середнє / медіана). */
export async function bench(n = 60) {
  const f = F(), r = f.renderer, gl = r.getContext(), tq = gl.getExtension('EXT_disjoint_timer_query_webgl2');
  const px = new Uint8Array(4), sync = () => gl.readPixels(0, 0, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px);
  frames(10); sync();
  const cpu = [], Q = { clouds: [], scene: [], post: [] };
  for (let i = 0; i < n; i++) {
    let cur = null;
    const q = tq && { begin(k) { cur = gl.createQuery(); gl.beginQuery(tq.TIME_ELAPSED_EXT, cur); Q[k].push(cur); }, end() { gl.endQuery(tq.TIME_ELAPSED_EXT); } };
    const t0 = performance.now();
    f.sky.tick(1 / 60, f.camera);
    if (f.trees) f.trees.update(f.camera.position);
    f.sky.draw(f.scene, f.camera, q);
    cpu.push(performance.now() - t0);
    sync();
  }
  await wait(60);
  const read = (list) => { const o = []; for (const x of list) { if (gl.getQueryParameter(x, gl.QUERY_RESULT_AVAILABLE) && !gl.getParameter(tq.GPU_DISJOINT_EXT)) o.push(gl.getQueryParameter(x, gl.QUERY_RESULT) / 1e6); gl.deleteQuery(x); } return o; };
  const med = (a) => { if (!a.length) return null; const b = [...a].sort((x, y) => x - y); return +b[b.length >> 1].toFixed(3); };
  const avg = (a) => (a.length ? +(a.reduce((x, y) => x + y, 0) / a.length).toFixed(3) : null);
  const res = { cpu: med(cpu) };
  if (tq) { res.gpuClouds = avg(read(Q.clouds)); res.gpuScene = med(read(Q.scene)); res.gpuPost = med(read(Q.post)); }
  return res;
}

export const gpu = () => { const gl = F().renderer.getContext(), e = gl.getExtension('WEBGL_debug_renderer_info'); return e ? gl.getParameter(e.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER); };
const fmt = (v) => (v == null ? '?' : v.toFixed(2).replace('.', ','));

export const TIMES = { svitanok: 5.2, ranok: 8, poludne: 13, 'den-hmary': 13, zakhid: 20.5, sutinky: 21.45, nich: 23.5 };

/**
 * Серія: pory — { назва: година }, shots — ракурси, presets — пресети для замірів «за фурою».
 * Канва — розміром size (як у пробі: 1280×800). Повертає й шле JSON.
 */
export async function suite({ tag = 'sky5', post = 'http://127.0.0.1:8798/', pory = TIMES, shots = ['chase', 'wide', 'cross', 'village', 'cabin', 'sky', 'skyup'], size = [1280, 800], presets = ['low', 'medium', 'high', 'ultra'], preset = 'high', cover = 0.4, reps = 3 } = {}) {
  const f = F();
  const keep = { ...f.gfx.G };
  setSize(...size);
  f.gfx.set({ preset });
  const out = { gpu: gpu(), size: size.join('×'), loadMs: f.gfx.loadMs, cloudBakeMs: f.sky.clouds.bakeMs, pory: {}, presets: {} };
  for (const [name, h] of Object.entries(pory)) {
    f.sky.setHour(h, name === 'den-hmary' ? 0.75 : cover);
    for (const s of shots) {
      view(s, 10);
      const i = f.sky.info(), st = stats();
      const lbl = 'Чумаки 0.0.5 · ' + i.hour + ' (сонце ' + Math.round(i.elev) + '°) · хмарність ' + Math.round(i.cover * 100) + ' % · ' + f.gfx.G.preset + '\n'
        + 'ракурс ' + s + ' · ' + st.calls + ' calls · ' + Math.round(st.triangles / 1000) + 'k △ · крони ' + f.gfx.G.trees + ' · хмари ' + f.gfx.G.clouds;
      out.pory[name + '-' + s] = { ...st, png: await shoot('nebo-' + name + '-' + s + '.png', lbl, post) };
    }
  }
  // пресети: за фурою, полудень, хмарність 40 %; перемішано reps разів, медіани
  f.sky.setHour(13, cover);
  view('chase', 10);
  const runs = {};
  for (let k = 0; k < reps; k++) for (const p of presets) {
    f.gfx.set({ preset: p }); view('chase', 30);
    const b = await bench(60), s = stats();
    (runs[p] = runs[p] || []).push({ ...b, ...s });
  }
  for (const [p, list] of Object.entries(runs)) {
    const m = (k) => { const a = list.map((x) => x[k]).filter((x) => x != null).sort((x, y) => x - y); return a.length ? a[a.length >> 1] : null; };
    out.presets[p] = { calls: m('calls'), triangles: m('triangles'), cpu: m('cpu'), gpuClouds: m('gpuClouds'), gpuScene: m('gpuScene'), gpuPost: m('gpuPost') };
    f.gfx.set({ preset: p }); view('chase', 20); view('village', 10);
    const b = out.presets[p];
    const lbl = 'Чумаки 0.0.5 · пресет ' + p + ' · 13:00 · хмарність 40 %\nза фурою: ' + b.calls + ' calls · ' + Math.round(b.triangles / 1000) + 'k △ · CPU ' + fmt(b.cpu) + ' мс · GPU хмари ' + fmt(b.gpuClouds) + ' + сцена ' + fmt(b.gpuScene) + ' + пост ' + fmt(b.gpuPost) + ' мс';
    view('chase', 10); await shoot('preset-' + p + '-chase.png', lbl, post);
    view('village', 10); await shoot('preset-' + p + '-village.png', lbl, post);
  }
  await fetch(post + tag + '-zamiry.json', { method: 'POST', body: JSON.stringify(out, null, 1) }).catch(() => {});
  f.gfx.set(keep); f.sky.setHour(null, null);
  setSize(null);
  f.freeCam = null; f.cam = 'chase';
  return out;
}
