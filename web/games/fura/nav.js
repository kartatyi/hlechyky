// Навігація по «Чумацькому краю»: граф із доріг kray.js (ті самі згладжені криві, що на землі), маршрут від фури до
// цілі, мінімапа в куті (курсом угору) і «стовп світла» над боксом, видний здалеку. Маршрут рахуємо самі (Дейкстра по
// вузлах) — сервер для км бере ту саму мапу, тож довжини збігаються до згладження кутів.

export class Nav {
  constructor(THREE, scene, level) {
    this.T = THREE; this.level = level;
    const map = level.map;
    // вузли: перехрестя мапи + «ворота» дворів (кінці під'їздів)
    this.nodes = new Map(Object.entries(map.nodes).map(([id, [x, z]]) => [id, { id, x, z, edges: [] }]));
    for (const p of level.places) this.nodes.set('gate:' + p.id, { id: 'gate:' + p.id, x: p.gate[0], z: p.gate[1], edges: [] });
    this.roads = level.roads.map((r) => {
      const a = r.a, b = r.kind === 'yard' ? 'gate:' + r.id.slice(5) : r.b;
      const len = r.pts[r.pts.length - 1].s;
      const e = { r, a, b, len };
      this.nodes.get(a).edges.push(e); this.nodes.get(b).edges.push(e);
      return e;
    });
    this.route = null; this.routeAt = 0; this.target = null;

    // стовп світла над ціллю
    const g = new THREE.CylinderGeometry(1.6, 1.6, 60, 16, 1, true);
    const m = new THREE.MeshBasicMaterial({ color: '#f2c94c', transparent: true, opacity: 0.28, depthWrite: false, side: THREE.DoubleSide });
    this.beam = new THREE.Mesh(g, m);
    this.beam.visible = false;
    scene.add(this.beam);
    const ring = new THREE.Mesh(new THREE.RingGeometry(2.6, 3.2, 32), new THREE.MeshBasicMaterial({ color: '#f2c94c', transparent: true, opacity: 0.7, side: THREE.DoubleSide, depthWrite: false }));
    ring.rotation.x = -Math.PI / 2;
    this.ring = ring; ring.visible = false;
    scene.add(ring);
  }

  /** Ціль: {x, z, label} або null. */
  setTarget(t) {
    this.target = t;
    this.route = null; this.routeAt = 0;
    this.beam.visible = this.ring.visible = !!t;
    if (t) {
      const y = this.level.height(t.x, t.z);
      this.beam.position.set(t.x, y + 30, t.z);
      this.ring.position.set(t.x, y + 0.15, t.z);
    }
  }

  /** Найближча точка дороги до (x, z): {e, i, d}. */
  nearest(x, z) {
    let best = null;
    for (const e of this.roads) {
      const pts = e.r.pts;
      for (let i = 0; i < pts.length; i += 2) {
        const d = (pts[i].x - x) ** 2 + (pts[i].z - z) ** 2;
        if (!best || d < best.d) best = { e, i, d };
      }
    }
    best.d = Math.sqrt(best.d);
    return best;
  }

  /** Маршрут від (x, z) до цілі: масив точок [{x, z}] і довжина, м. Перераховуємо не частіше ніж раз на секунду. */
  update(x, z, now) {
    if (!this.target) return null;
    if (this.route && now - this.routeAt < 1000) return this.route;
    this.routeAt = now;
    const from = this.nearest(x, z), to = this.nearest(this.target.x, this.target.z);
    // Дейкстра від двох кінців дороги, на якій стоїмо
    const dist = new Map(), prev = new Map(), done = new Set();
    const s1 = from.e.r.pts[from.i].s;
    const startCost = { [from.e.a]: s1, [from.e.b]: from.e.len - s1 };
    for (const [n, c] of Object.entries(startCost)) dist.set(n, c);
    while (true) {
      let u = null, du = Infinity;
      for (const [n, d] of dist) if (!done.has(n) && d < du) { du = d; u = n; }
      if (!u) break;
      done.add(u);
      for (const e of this.nodes.get(u).edges) {
        const v = e.a === u ? e.b : e.a, nd = du + e.len;
        if (nd < (dist.get(v) ?? Infinity)) { dist.set(v, nd); prev.set(v, { u, e }); }
      }
    }
    // з якого кінця цільової дороги заходимо
    const s2 = to.e.r.pts[to.i].s;
    const viaA = (dist.get(to.e.a) ?? Infinity) + s2, viaB = (dist.get(to.e.b) ?? Infinity) + (to.e.len - s2);
    let pts = [];
    const slice = (e, s0, s1) => {
      const P = e.r.pts, out = [];
      if (s0 <= s1) { for (const p of P) if (p.s >= s0 && p.s <= s1) out.push(p); }
      else { for (let i = P.length - 1; i >= 0; i--) if (P[i].s <= s0 && P[i].s >= s1) out.push(P[i]); }
      return out;
    };
    let total;
    if (from.e === to.e) {
      pts = slice(from.e, s1, s2);
      total = Math.abs(s2 - s1);
    } else {
      const endNode = viaA <= viaB ? to.e.a : to.e.b;
      total = Math.min(viaA, viaB);
      const chain = [];
      for (let n = endNode; prev.has(n); n = prev.get(n).u) chain.unshift(prev.get(n));
      const firstNode = chain.length ? chain[0].u : endNode;
      pts.push(...slice(from.e, s1, firstNode === from.e.a ? 0 : from.e.len));
      for (const { u, e } of chain) pts.push(...slice(e, u === e.a ? 0 : e.len, u === e.a ? e.len : 0));
      pts.push(...slice(to.e, endNode === to.e.a ? 0 : to.e.len, s2));
    }
    pts.push({ x: this.target.x, z: this.target.z });
    total += from.d + Math.hypot(to.e.r.pts[to.i].x - this.target.x, to.e.r.pts[to.i].z - this.target.z);
    this.route = { pts, len: total };
    return this.route;
  }

  /** Куди повертати: кут (рад, + — праворуч) до точки маршруту за ~40 м попереду. */
  aim(x, z, yaw) {
    if (!this.route) return null;
    const P = this.route.pts;
    let k = 0, best = Infinity;
    for (let i = 0; i < P.length; i++) { const d = Math.hypot(P[i].x - x, P[i].z - z); if (d < best) { best = d; k = i; } }
    let acc = 0, j = k;
    while (j < P.length - 1 && acc < 40) { acc += Math.hypot(P[j + 1].x - P[j].x, P[j + 1].z - P[j].z); j++; }
    const want = Math.atan2(P[j].x - x, P[j].z - z);
    return -Math.atan2(Math.sin(want - yaw), Math.cos(want - yaw));
  }

  /** Мінімапа: canvas, фура в центрі, курсом угору; scale — пікселів на метр. others — колона: [{x, z, nick, color}]. */
  draw(cv, x, z, yaw, scale = 0.33, others = null) {
    const g = cv.getContext('2d'), w = cv.width, h = cv.height;
    g.clearRect(0, 0, w, h);
    g.save();
    g.beginPath(); g.arc(w / 2, h / 2, w / 2 - 2, 0, Math.PI * 2); g.clip();
    g.fillStyle = 'rgba(58,72,48,.82)'; g.fillRect(0, 0, w, h);
    g.translate(w / 2, h / 2);
    g.rotate(yaw);                     // курс угору: світ обертаємо проти курсу
    g.scale(-scale, -scale);           // +Z (північ) угору екрана, +X (схід) праворуч — дзеркало осей canvas
    g.translate(-x, -z);
    const line = (pts, width, color) => {
      g.beginPath();
      pts.forEach((p, i) => (i ? g.lineTo(p.x, p.z) : g.moveTo(p.x, p.z)));
      g.lineWidth = width / scale; g.strokeStyle = color; g.lineJoin = 'round'; g.lineCap = 'round'; g.stroke();
    };
    line(this.level.river.map((p) => ({ x: p.x, z: p.z })), 5, '#5d9cc8');
    for (const e of this.roads) line(e.r.pts, e.r.kind === 'highway' ? 4 : e.r.kind === 'dirt' ? 2.5 : 3, e.r.kind === 'dirt' ? '#a58c69' : '#d8d4cc');
    if (this.route) line(this.route.pts, 5, '#f2994a');
    for (const p of this.level.places) {
      g.fillStyle = '#efe6d2';
      g.fillRect(p.yard.x - p.yard.w / 2, p.yard.z - p.yard.d / 2, p.yard.w, p.yard.d);
    }
    if (this.target) { g.beginPath(); g.arc(this.target.x, this.target.z, 6 / scale, 0, Math.PI * 2); g.fillStyle = '#f2c94c'; g.fill(); }
    g.restore();
    // колона: крапки з ніками; хто за краєм — притиснутий до кола, щоб було видно, в який бік
    if (others && others.length) {
      const R = w / 2 - 8, c = Math.cos(yaw), s = Math.sin(yaw);
      g.save();
      g.font = '600 11px system-ui'; g.textAlign = 'center'; g.textBaseline = 'bottom';
      const tags = [];          // де вже підписали: ближчі одне до одного ніки не громадимо — видно крапки
      for (const o of others) {
        // та сама поза, що й у світі вище: зсув, дзеркало осей, поворот на курс
        const dx = -(o.x - x) * scale, dz = -(o.z - z) * scale;
        let px = dx * c - dz * s, py = dx * s + dz * c;
        const d = Math.hypot(px, py), edge = d > R;
        if (edge) { px *= R / d; py *= R / d; }
        px += w / 2; py += h / 2;
        g.beginPath(); g.arc(px, py, edge ? 4 : 5, 0, Math.PI * 2);
        g.fillStyle = o.color || '#6ba7f2'; g.fill(); g.lineWidth = 1.5; g.strokeStyle = '#fff'; g.stroke();
        if (o.nick && !edge && !tags.some((t) => Math.abs(t.x - px) < 46 && Math.abs(t.y - py) < 13)) {
          tags.push({ x: px, y: py });
          g.lineWidth = 3; g.strokeStyle = 'rgba(20,22,28,.8)'; g.strokeText(o.nick, px, py - 6);
          g.fillStyle = '#fff'; g.fillText(o.nick, px, py - 6);
        }
      }
      g.restore();
    }
    // фура — трикутник у центрі, носом угору
    g.save(); g.translate(w / 2, h / 2);
    g.beginPath(); g.moveTo(0, -9); g.lineTo(6, 7); g.lineTo(-6, 7); g.closePath();
    g.fillStyle = '#ff5a3c'; g.fill(); g.lineWidth = 2; g.strokeStyle = '#fff'; g.stroke();
    g.restore();
    // N на краю
    g.save(); g.translate(w / 2, h / 2); g.rotate(yaw);
    g.fillStyle = '#fff'; g.font = 'bold 12px system-ui'; g.textAlign = 'center'; g.fillText('Пн', 0, -(w / 2 - 14));
    g.restore();
  }
}
