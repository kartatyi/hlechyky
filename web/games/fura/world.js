// Полігон етапу 0: асфальтовий майданчик із боксами для причепів, кільце дороги довкола, гірка 10 % (вгору,
// поличка, вниз), смуга «лежачих поліцейських» (на них б'ється вантаж), конуси-слалом, дерева й хати.
// Фізика проста й дешева: земля — одна плита, гірка й горби — повернуті коробки й циліндри, дерева й хати —
// нерухомі тіла. Візерунки (асфальт, трава, розмітка) малюємо на canvas, щоб не тягнути текстури файлами.

export const SPAWN = {
  trailer: { x: 0, z: 0 },          // причіп у боксі Б-1 передом на північ (+Z)
  tractor: { x: 0, z: 17 },         // тягач перед ним, задом до причепа: треба здати назад ~8 м
};

const ROAD_W = 9;
const LOOP = { cx: 0, cz: 10, rx: 125, rz: 88 };

function rng(seed) {
  let s = seed >>> 0;
  return () => { s = (s + 0x6D2B79F5) >>> 0; let t = s; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}

function canvasTex(THREE, w, h, draw, repeat) {
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  draw(c.getContext('2d'), w, h);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 4;
  if (repeat) { t.wrapS = t.wrapT = THREE.RepeatWrapping; t.repeat.set(repeat[0], repeat[1]); }
  return t;
}

function speckle(g, w, h, base, spread, n, rnd) {
  g.fillStyle = base; g.fillRect(0, 0, w, h);
  for (let i = 0; i < n; i++) {
    const v = (rnd() - 0.5) * spread;
    g.fillStyle = v > 0 ? `rgba(255,255,255,${v})` : `rgba(0,0,0,${-v})`;
    g.fillRect(rnd() * w, rnd() * h, 1 + rnd() * 3, 1 + rnd() * 3);
  }
}

export function buildWorld(THREE, RAPIER, scene, world, quality) {
  const rnd = rng(1918);
  const mats = new Map();
  const mat = (color, extra) => {
    const k = color + (extra ? JSON.stringify(extra) : '');
    if (!mats.has(k)) mats.set(k, new THREE.MeshLambertMaterial({ color, flatShading: true, ...extra }));
    return mats.get(k);
  };
  const add = (mesh, shadow = true) => { mesh.castShadow = shadow; mesh.receiveShadow = true; scene.add(mesh); return mesh; };
  const fixed = (x, y, z, q) => {
    const d = RAPIER.RigidBodyDesc.fixed().setTranslation(x, y, z);
    if (q) d.setRotation(q);
    return world.createRigidBody(d);
  };
  const qAxis = (ax, ay, az, a) => { const q = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(ax, ay, az), a); return { x: q.x, y: q.y, z: q.z, w: q.w }; };

  // ---- земля
  const ground = fixed(0, -0.5, 0);
  world.createCollider(RAPIER.ColliderDesc.cuboid(400, 0.5, 400).setFriction(0.9), ground);
  const grass = canvasTex(THREE, 256, 256, (g, w, h) => speckle(g, w, h, '#7fa65a', 0.18, 2600, rnd), [120, 120]);
  const gMesh = new THREE.Mesh(new THREE.PlaneGeometry(800, 800), new THREE.MeshLambertMaterial({ map: grass }));
  gMesh.rotation.x = -Math.PI / 2;
  add(gMesh, false);

  // ---- майданчик із боксами
  const yard = { x0: -45, x1: 45, z0: -28, z1: 46 };
  const yw = yard.x1 - yard.x0, yd = yard.z1 - yard.z0;
  const yardTex = canvasTex(THREE, 1024, Math.round(1024 * yd / yw), (g, w, h) => {
    speckle(g, w, h, '#5b5e63', 0.12, 9000, rnd);
    // Розмітку малюємо в координатах світу (лівий край canvas — захід, верх — північ), а самі написи —
    // повернутими на 180° (label): так їх читає водій, що дивиться на північ (перевірено знімками).
    const px = (x) => ((x - yard.x0) / yw) * w, pz = (z) => ((yard.z1 - z) / yd) * h;
    const label = (t, x, y) => { g.save(); g.translate(x, y); g.scale(-1, -1); g.fillText(t, 0, 0); g.restore(); };
    g.strokeStyle = 'rgba(245,240,225,.9)'; g.lineWidth = 3;
    // бокси Б-1…Б-4 для причепів (4,5 м завширшки), Б-1 — посередині, там стоїть причіп
    for (let k = -2; k <= 3; k++) {
      const x = -2.25 + k * 4.5;
      g.beginPath(); g.moveTo(px(x), pz(-8.5)); g.lineTo(px(x), pz(8)); g.stroke();
    }
    g.beginPath(); g.moveTo(px(-11.25), pz(-8.5)); g.lineTo(px(11.25 + 4.5), pz(-8.5)); g.stroke();
    g.fillStyle = 'rgba(245,240,225,.9)'; g.font = 'bold 34px system-ui'; g.textAlign = 'center'; g.textBaseline = 'middle';
    [[0, 'Б-1'], [4.5, 'Б-2'], [-4.5, 'Б-3'], [-9, 'Б-4'], [9, 'Б-5']].forEach(([x, t]) => label(t, px(x), pz(9.5)));
    // жовта «зебра» виїзду на північ
    g.fillStyle = 'rgba(242,201,76,.85)';
    for (let i = 0; i < 6; i++) g.fillRect(px(4.5 - i * 1.6) - 7, pz(yard.z1) - 26, 14, 26);
  });
  const yMesh = new THREE.Mesh(new THREE.PlaneGeometry(yw, yd), new THREE.MeshLambertMaterial({ map: yardTex }));
  yMesh.rotation.x = -Math.PI / 2;
  yMesh.position.set((yard.x0 + yard.x1) / 2, 0.015, (yard.z0 + yard.z1) / 2);
  add(yMesh, false);

  // ---- дороги: кільце й з'їзд від майданчика на північ
  const roadTex = canvasTex(THREE, 128, 256, (g, w, h) => {
    speckle(g, w, h, '#4d5056', 0.12, 1800, rnd);
    g.fillStyle = 'rgba(245,240,225,.95)';
    g.fillRect(w * 0.035, 0, 4, h); g.fillRect(w * 0.965 - 4, 0, 4, h);
    g.fillRect(w / 2 - 2, 0, 4, h * 0.45);
  }, null);
  roadTex.wrapS = roadTex.wrapT = THREE.RepeatWrapping;
  const roadMat = new THREE.MeshLambertMaterial({ map: roadTex });
  const roads = [];
  function ribbon(pts, closed) {
    const pos = [], uv = [], idx = [];
    let v = 0;
    const n = pts.length;
    for (let i = 0; i < n; i++) {
      const a = pts[closed ? (i - 1 + n) % n : Math.max(0, i - 1)], b = pts[closed ? (i + 1) % n : Math.min(n - 1, i + 1)];
      let tx = b.x - a.x, tz = b.z - a.z;
      const l = Math.hypot(tx, tz) || 1; tx /= l; tz /= l;
      const nx = -tz, nz = tx;
      if (i > 0) v += Math.hypot(pts[i].x - pts[i - 1].x, pts[i].z - pts[i - 1].z) / 12;
      pos.push(pts[i].x + nx * ROAD_W / 2, 0.03, pts[i].z + nz * ROAD_W / 2, pts[i].x - nx * ROAD_W / 2, 0.03, pts[i].z - nz * ROAD_W / 2);
      uv.push(0, v, 1, v);
    }
    const segs = closed ? n : n - 1;
    for (let i = 0; i < segs; i++) {
      const j = (i + 1) % n;
      idx.push(i * 2, j * 2, i * 2 + 1, i * 2 + 1, j * 2, j * 2 + 1);
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
    geo.setIndex(idx);
    geo.computeVertexNormals();
    add(new THREE.Mesh(geo, roadMat), false);
    roads.push(pts);
  }
  const loop = [];
  for (let i = 0; i < 200; i++) {
    const a = (i / 200) * Math.PI * 2;
    loop.push({ x: LOOP.cx + Math.cos(a) * LOOP.rx, z: LOOP.cz + Math.sin(a) * LOOP.rz });
  }
  ribbon(loop, true);
  const exitRoad = [];
  for (let z = yard.z1 - 1; z <= LOOP.cz + LOOP.rz + 1; z += 4) exitRoad.push({ x: 0, z });
  ribbon(exitRoad, false);

  // ---- гірка 10 %: смуга на схід від майданчика вздовж Z
  const HX = 80, hillW = 10, rise = 3, run = 30, top = 20, z0 = -40;
  const slope = Math.atan2(rise, run), len = Math.hypot(rise, run);
  const dirt = mat('#8a7a63');
  const slab = (cx, cy, cz, hx, hy, hz, ang) => {
    const q = qAxis(1, 0, 0, ang);
    const b = fixed(cx, cy, cz, q);
    world.createCollider(RAPIER.ColliderDesc.cuboid(hx, hy, hz).setFriction(0.9), b);
    const m = new THREE.Mesh(new THREE.BoxGeometry(hx * 2, hy * 2, hz * 2), dirt);
    m.position.set(cx, cy, cz); m.quaternion.set(q.x, q.y, q.z, q.w);
    add(m);
  };
  const th = 0.5;
  // вгору (передом на +Z): кут зі знаком мінус піднімає дальній край
  slab(HX, rise / 2 - th * Math.cos(slope), z0 + run / 2, hillW / 2, th, len / 2, -slope);
  slab(HX, rise - th, z0 + run + top / 2, hillW / 2, th, top / 2, 0);
  slab(HX, rise / 2 - th * Math.cos(slope), z0 + run + top + run / 2, hillW / 2, th, len / 2, slope);
  sign(THREE, scene, mat, HX - hillW / 2 - 1.5, z0 - 4, 'ГІРКА 10 %', Math.PI / 2);

  // ---- «лежачі поліцейські» на заході: циліндри впоперек смуги, виступають на 8 см
  const BX = -80, bz0 = -36;
  const bumpMat = mat('#e0b23c');
  const stripTex = mat('#5a5d62');
  const strip = new THREE.Mesh(new THREE.PlaneGeometry(hillW, 84), stripTex);
  strip.rotation.x = -Math.PI / 2; strip.position.set(BX, 0.02, bz0 + 40); add(strip, false);
  for (let i = 0; i < 9; i++) {
    const z = bz0 + 8 + i * 8, r = 0.35, cy = -r + 0.08;
    const q = qAxis(0, 0, 1, Math.PI / 2);
    const b = fixed(BX, cy, z, q);
    world.createCollider(RAPIER.ColliderDesc.cylinder(hillW / 2, r).setFriction(0.9), b);
    const m = new THREE.Mesh(new THREE.CylinderGeometry(r, r, hillW, 10), bumpMat);
    m.position.set(BX, cy, z); m.rotation.z = Math.PI / 2;
    add(m, false);
  }
  sign(THREE, scene, mat, BX + hillW / 2 + 1.5, bz0 - 2, 'ГОРБИ — ВЕЗИ ТИХО', -Math.PI / 2);

  // ---- конуси (рухомі, легенькі): слалом на з'їзді й ряд біля боксів
  const cones = [];
  const coneGeo = new THREE.ConeGeometry(0.22, 0.7, 8);
  const coneMat = mat('#f07a2a');
  const coneAt = (x, z) => {
    const b = world.createRigidBody(RAPIER.RigidBodyDesc.dynamic().setTranslation(x, 0.36, z).setLinearDamping(0.3).setAngularDamping(0.5));
    world.createCollider(RAPIER.ColliderDesc.cylinder(0.35, 0.2).setMass(3).setFriction(0.8), b);
    const m = new THREE.Mesh(coneGeo, coneMat);
    m.castShadow = true;
    scene.add(m);
    cones.push({ b, m });
  };
  for (let i = 0; i < 8; i++) coneAt((i % 2 ? 2.2 : -2.2), 58 + i * 9);
  for (let i = 0; i < 6; i++) coneAt(16 + i * 2.5, -12);

  // ---- дерева (з тілами, щоб не проїжджати крізь) і хати
  const nearRoad = (x, z, pad) => {
    if (x > yard.x0 - pad && x < yard.x1 + pad && z > yard.z0 - pad && z < yard.z1 + pad) return true;
    if (Math.abs(x - HX) < hillW / 2 + pad && z > z0 - pad && z < z0 + 2 * run + top + pad) return true;
    if (Math.abs(x - BX) < hillW / 2 + pad && z > bz0 - pad && z < bz0 + 84 + pad) return true;
    for (const r of roads) for (const p of r) if (Math.abs(p.x - x) < 40 && Math.hypot(p.x - x, p.z - z) < ROAD_W / 2 + pad) return true;
    return false;
  };
  const trees = [];
  const nTrees = quality === 'low' ? 140 : 260;
  for (let tries = 0; trees.length < nTrees && tries < 5000; tries++) {
    const x = (rnd() - 0.5) * 520, z = (rnd() - 0.5) * 460 + 10;
    if (nearRoad(x, z, 5)) continue;
    trees.push({ x, z, s: 0.8 + rnd() * 0.7, kind: rnd() < 0.35 ? 1 : 0 });
  }
  const trunkGeo = new THREE.CylinderGeometry(0.18, 0.28, 2.2, 6);
  const crownGeo = new THREE.IcosahedronGeometry(1.7, 0);
  const pineGeo = new THREE.ConeGeometry(1.6, 4.2, 7);
  const trunks = new THREE.InstancedMesh(trunkGeo, mat('#6e4a2f'), trees.length);
  const crowns = new THREE.InstancedMesh(crownGeo, mat('#4f8a3c'), trees.length);
  const pines = new THREE.InstancedMesh(pineGeo, mat('#3d6b3a'), trees.length);
  const tmp = new THREE.Object3D();
  let nc = 0, np = 0;
  trees.forEach((t, i) => {
    tmp.position.set(t.x, 1.1 * t.s, t.z); tmp.scale.setScalar(t.s); tmp.rotation.set(0, rnd() * 6, 0); tmp.updateMatrix();
    trunks.setMatrixAt(i, tmp.matrix);
    tmp.position.y = (t.kind ? 3.6 : 3.2) * t.s; tmp.updateMatrix();
    if (t.kind) pines.setMatrixAt(np++, tmp.matrix); else crowns.setMatrixAt(nc++, tmp.matrix);
    const b = fixed(t.x, 1.1 * t.s, t.z);
    world.createCollider(RAPIER.ColliderDesc.cylinder(1.1 * t.s, 0.3 * t.s), b);
  });
  crowns.count = nc; pines.count = np;
  for (const m of [trunks, crowns, pines]) { m.castShadow = true; m.receiveShadow = true; scene.add(m); }

  const hut = (x, z, rot, w, d) => {
    const g = new THREE.Group();
    const wall = new THREE.Mesh(new THREE.BoxGeometry(w, 2.8, d), mat('#f2ede0'));
    wall.position.y = 1.4; g.add(wall);
    const roof = new THREE.Mesh(new THREE.ConeGeometry(Math.hypot(w, d) * 0.62, 2.4, 4), mat('#9a7a45'));
    roof.position.y = 2.8 + 1.2; roof.rotation.y = Math.PI / 4; roof.scale.set(w / Math.hypot(w, d) * 1.41, 1, d / Math.hypot(w, d) * 1.41); g.add(roof);
    const door = new THREE.Mesh(new THREE.BoxGeometry(0.9, 1.9, 0.05), mat('#5a7da8'));
    door.position.set(0, 0.95, d / 2 + 0.03); g.add(door);
    for (const sx of [-1, 1]) {
      const win = new THREE.Mesh(new THREE.BoxGeometry(0.8, 0.8, 0.05), mat('#6fa0c8'));
      win.position.set(sx * w * 0.3, 1.6, d / 2 + 0.03); g.add(win);
    }
    g.position.set(x, 0, z); g.rotation.y = rot;
    g.traverse((o) => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; } });
    scene.add(g);
    const q = qAxis(0, 1, 0, rot);
    const b = fixed(x, 1.4, z, q);
    world.createCollider(RAPIER.ColliderDesc.cuboid(w / 2, 1.4, d / 2), b);
  };
  hut(-30, 62, Math.PI, 8, 6); hut(-44, 66, Math.PI * 0.9, 7, 5); hut(32, 64, Math.PI * 1.05, 9, 6);
  hut(-20, -48, 0, 10, 7); hut(26, -50, 0.1, 8, 6);
  // сарай-депо біля боксів
  const depot = new THREE.Mesh(new THREE.BoxGeometry(18, 7, 10), mat('#b5582f'));
  depot.position.set(-30, 3.5, -12); depot.castShadow = depot.receiveShadow = true; scene.add(depot);
  world.createCollider(RAPIER.ColliderDesc.cuboid(9, 3.5, 5), fixed(-30, 3.5, -12));
  sign(THREE, scene, mat, -12, 30, 'ЧУМАКИ · ПОЛІГОН', Math.PI * 0.75, 1.6);

  return {
    cones,
    sync() {
      for (const c of cones) {
        const p = c.b.translation(), r = c.b.rotation();
        c.m.position.set(p.x, p.y, p.z);
        c.m.quaternion.set(r.x, r.y, r.z, r.w);
      }
    },
    resetCones() {
      // конуси повертаються на свої місця (після «На старт»)
      let i = 0;
      const put = (x, z) => { const c = cones[i++]; c.b.setTranslation({ x, y: 0.36, z }, true); c.b.setRotation({ x: 0, y: 0, z: 0, w: 1 }, true); c.b.setLinvel({ x: 0, y: 0, z: 0 }, true); c.b.setAngvel({ x: 0, y: 0, z: 0 }, true); };
      for (let k = 0; k < 8; k++) put((k % 2 ? 2.2 : -2.2), 58 + k * 9);
      for (let k = 0; k < 6; k++) put(16 + k * 2.5, -12);
    },
  };
}

/** Табличка на двох стовпчиках з написом (canvas). rot — куди дивиться лицем. */
function sign(THREE, scene, mat, x, z, text, rot, scale = 1) {
  const c = document.createElement('canvas');
  c.width = 512; c.height = 128;
  const g = c.getContext('2d');
  g.fillStyle = '#2f5d8a'; g.fillRect(0, 0, 512, 128);
  g.strokeStyle = '#f2ede0'; g.lineWidth = 8; g.strokeRect(8, 8, 496, 112);
  g.fillStyle = '#f2ede0'; g.font = 'bold 54px system-ui'; g.textAlign = 'center'; g.textBaseline = 'middle';
  g.fillText(text, 256, 68, 470);
  const tex = new THREE.CanvasTexture(c);
  tex.colorSpace = THREE.SRGBColorSpace;
  const grp = new THREE.Group();
  const board = new THREE.Mesh(new THREE.BoxGeometry(4 * scale, 1 * scale, 0.08), [mat('#2f5d8a'), mat('#2f5d8a'), mat('#2f5d8a'), mat('#2f5d8a'),
    new THREE.MeshLambertMaterial({ map: tex }), mat('#2f5d8a')]);
  board.position.y = 2.2 * scale;
  grp.add(board);
  for (const sx of [-1.6, 1.6]) {
    const post = new THREE.Mesh(new THREE.BoxGeometry(0.1, 2.2 * scale, 0.1), mat('#888888'));
    post.position.set(sx * scale, 1.1 * scale, -0.06);
    grp.add(post);
  }
  grp.position.set(x, 0, z);
  grp.rotation.y = rot;
  grp.traverse((o) => { if (o.isMesh) o.castShadow = true; });
  scene.add(grp);
}
