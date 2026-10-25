// Low-poly вигляд тягача й причепа. Групи лежать в осях своїх тіл (та сама система, що в truck.js: +Z вперед,
// праворуч −X), тож синхронізація — це поза тіла плюс колеса з даних контролера (довжина підвіски, оберт, кут).
// Для камери з кабіни зовнішня кабіна ховається, а показується «салон»: панель, кермо, стійки, дзеркала.

import { TRACTOR, TRAILER } from './truck.js';

export function buildModels(THREE, scene) {
  const cache = new Map();
  const M = (color, extra) => {
    const k = color + JSON.stringify(extra || {});
    if (!cache.has(k)) cache.set(k, new THREE.MeshLambertMaterial({ color, flatShading: true, ...(extra || {}) }));
    return cache.get(k);
  };
  const box = (g, w, h, d, x, y, z, m) => { const o = new THREE.Mesh(new THREE.BoxGeometry(w, h, d), m); o.position.set(x, y, z); g.add(o); return o; };
  const cylX = (g, r, len, x, y, z, m, seg = 12) => { const o = new THREE.Mesh(new THREE.CylinderGeometry(r, r, len, seg), m); o.rotation.z = Math.PI / 2; o.position.set(x, y, z); g.add(o); return o; };
  const cylZ = (g, r, len, x, y, z, m) => { const o = new THREE.Mesh(new THREE.CylinderGeometry(r, r, len, 10), m); o.rotation.x = Math.PI / 2; o.position.set(x, y, z); g.add(o); return o; };

  const paint = M('#b5482f'), dark = M('#2a2b2e'), glass = M('#26394d', { emissive: '#0b1520' }), chrome = M('#c9ccd1'),
    tyre = M('#1d1d1f'), rim = M('#9aa0a8'), lamp = M('#fff3c4', { emissive: '#ffe7a0', emissiveIntensity: 0.9 }),
    red = M('#d93a2b', { emissive: '#5a0d08' }), amber = M('#f0a030', { emissive: '#5a3405' });

  // ---------------- тягач
  const tractor = new THREE.Group();
  const ext = new THREE.Group();          // зовнішня кабіна (ховаємо з кабіни)
  tractor.add(ext);
  box(ext, 2.5, 2.6, 2.2, 0, 1.0, 2.1, paint);
  box(ext, 2.3, 0.55, 1.5, 0, 2.55, 1.9, paint).rotation.x = -0.12;           // дефлектор
  box(ext, 2.2, 0.95, 0.06, 0, 1.55, 3.2, glass);                             // лобове
  for (const s of [1, -1]) box(ext, 0.06, 0.8, 0.9, s * 1.26, 1.55, 2.55, glass);   // бокові вікна
  box(ext, 1.7, 0.85, 0.06, 0, 0.35, 3.21, dark);                             // ґрати
  for (let i = 0; i < 4; i++) box(ext, 1.6, 0.04, 0.07, 0, 0.1 + i * 0.18, 3.23, chrome);
  box(ext, 2.5, 0.38, 0.3, 0, -0.27, 3.12, dark);                             // бампер
  for (const s of [1, -1]) {
    box(ext, 0.36, 0.2, 0.06, s * 0.88, -0.05, 3.27, lamp);
    box(ext, 0.16, 0.12, 0.06, s * 1.12, -0.05, 3.27, amber);
    box(ext, 0.05, 0.05, 0.5, s * 1.32, 1.65, 3.0, dark);                     // кронштейн дзеркала
    box(ext, 0.08, 0.55, 0.28, s * 1.55, 1.45, 3.15, dark);                   // дзеркало
    box(ext, 0.12, 0.12, 2.0, s * 1.26, -0.35, 2.1, dark);                    // підніжка
  }
  for (let i = 0; i < 5; i++) box(ext, 0.16, 0.08, 0.06, -0.5 + i * 0.25, 2.33, 3.12, amber);   // вогні на даху
  const stack = new THREE.Mesh(new THREE.CylinderGeometry(0.08, 0.08, 1.7, 8), chrome);
  stack.position.set(1.0, 2.25, 0.95); ext.add(stack);

  // рама, бак, сідло, крила — видно з обох камер
  box(tractor, 1.1, 0.32, 5.7, 0, -0.18, -0.55, dark);
  for (const s of [1, -1]) cylZ(tractor, 0.33, 1.3, s * 0.98, -0.18, 0.45, chrome);
  const plate = new THREE.Mesh(new THREE.CylinderGeometry(0.5, 0.5, 0.1, 14), M('#3a3b3f'));
  plate.position.set(0, TRACTOR.fifth.y - 0.05, TRACTOR.fifth.z); tractor.add(plate);
  for (const z of [-1.55, -2.9]) for (const s of [1, -1]) box(tractor, 0.75, 0.06, 0.95, s * 1.0, 0.42, z, dark);
  for (const s of [1, -1]) box(tractor, 0.25, 0.14, 0.06, s * 0.9, -0.15, -3.42, red);

  // салон
  const cabin = new THREE.Group();
  cabin.visible = false;
  tractor.add(cabin);
  const inside = M('#6a655d');
  box(cabin, 2.4, 0.42, 0.55, 0, 0.98, 2.92, inside);                          // панель
  box(cabin, 0.7, 0.16, 0.1, 0.6, 1.12, 2.68, M('#2b2c30'));                   // прилади (нижче лінії погляду)
  box(cabin, 2.45, 0.06, 2.1, 0, 2.27, 2.1, inside);                           // стеля
  for (const s of [1, -1]) {
    const pillar = box(cabin, 0.07, 1.05, 0.08, s * 1.2, 1.62, 3.16, inside);
    pillar.rotation.x = 0.08;
    box(cabin, 0.06, 0.06, 0.5, s * 1.34, 1.65, 3.02, dark);
    box(cabin, 0.08, 0.55, 0.28, s * 1.55, 1.45, 3.15, dark);
  }
  box(cabin, 2.4, 0.08, 0.1, 0, 1.06, 3.2, inside);                            // низ лобового
  box(cabin, 2.4, 0.07, 0.06, 0, 2.06, 3.2, inside);                           // верх лобового
  const wheelPivot = new THREE.Group();
  wheelPivot.position.set(0.6, 1.26, 2.5);
  wheelPivot.rotation.x = -1.05;
  cabin.add(wheelPivot);
  const rimMesh = new THREE.Mesh(new THREE.TorusGeometry(0.24, 0.028, 6, 20), M('#1a1a1a'));
  wheelPivot.add(rimMesh);
  const spoke = new THREE.Mesh(new THREE.BoxGeometry(0.44, 0.04, 0.03), M('#1a1a1a'));
  rimMesh.add(spoke);
  const spoke2 = new THREE.Mesh(new THREE.BoxGeometry(0.04, 0.22, 0.03), M('#1a1a1a'));
  spoke2.position.y = -0.11; rimMesh.add(spoke2);
  // глечик на панелі — хитається від ям (наш «датчик» вантажу)
  const jug = new THREE.Group();
  const jugBody = new THREE.Mesh(new THREE.SphereGeometry(0.045, 8, 6), M('#c26a3a'));
  jugBody.scale.set(1, 1.15, 1); jug.add(jugBody);
  const jugNeck = new THREE.Mesh(new THREE.CylinderGeometry(0.02, 0.026, 0.04, 8), M('#c26a3a'));
  jugNeck.position.y = 0.06; jug.add(jugNeck);
  jug.position.set(-0.35, 1.24, 3.0);
  cabin.add(jug);

  const tWheels = TRACTOR.wheels.map(([x, y, z], i) => wheel(tractor, x, y, z, TRACTOR.wheelR, i >= 2 ? 0.62 : 0.38));

  // ---------------- причіп
  const trailer = new THREE.Group();
  // Два малюнки борту, а не один віддзеркалений: дзеркало перевертало й літери («ГЛЕЧИКИ» задом наперед, 25.10).
  // На +X глечик ліворуч, на −X — праворуч: з обох боків він біля кабіни, а напис читається.
  const side = sideTexture(THREE, null, null, false), sideR = sideTexture(THREE, null, null, true);
  const cream = M('#efe6d2');
  const sideMat = new THREE.MeshLambertMaterial({ map: side });
  const sideMatR = new THREE.MeshLambertMaterial({ map: sideR });
  const back = M('#e2d7bf');
  // BoxGeometry: +X, −X, +Y, −Y, +Z, −Z
  const boxMesh = new THREE.Mesh(new THREE.BoxGeometry(TRAILER.w, TRAILER.h, TRAILER.len), [sideMat, sideMatR, cream, dark, cream, back]);
  boxMesh.position.set(0, TRAILER.h / 2, 0);
  trailer.add(boxMesh);
  box(trailer, 1.1, 0.26, 13.0, 0, -0.14, 0, dark);
  for (const s of [1, -1]) box(trailer, 0.06, 0.35, 5.2, s * 1.18, -0.3, 0.2, M('#8b8f96'));    // бокові захисти
  box(trailer, 2.4, 0.18, 0.14, 0, -0.55, -6.75, M('#8b8f96'));
  for (const s of [1, -1]) { box(trailer, 0.3, 0.14, 0.06, s * 0.95, -0.35, -6.83, red); box(trailer, 0.1, 0.1, 0.06, s * 0.68, -0.35, -6.83, amber); }
  for (let i = -2; i <= 2; i++) box(trailer, 0.04, TRAILER.h - 0.1, 0.03, i * 0.5, TRAILER.h / 2, -6.82, M('#c7bba1'));
  const legs = new THREE.Group();
  for (const s of [1, -1]) box(legs, 0.12, 1.15, 0.12, s * 0.85, -0.6, 3.2, M('#6d7178'));
  box(legs, 1.9, 0.08, 0.08, 0, -0.4, 3.2, M('#6d7178'));
  for (const s of [1, -1]) box(legs, 0.3, 0.06, 0.3, s * 0.85, -1.2, 3.2, M('#6d7178'));
  trailer.add(legs);
  const kp = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 0.05, 0.12, 8), chrome);
  kp.position.set(TRAILER.kingpin.x, -0.06, TRAILER.kingpin.z); trailer.add(kp);
  const rWheels = TRAILER.wheels.map(([x, y, z]) => wheel(trailer, x, y, z, TRAILER.wheelR, 0.62));

  for (const g of [tractor, trailer]) g.traverse((o) => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; } });
  // салон не кидає й не ловить тіней: інакше стеля кабіни затемнює все всередині до чорного
  cabin.traverse((o) => { if (o.isMesh) { o.castShadow = false; o.receiveShadow = false; } });
  scene.add(tractor, trailer);

  function wheel(parent, x, y, z, r, w) {
    const g = new THREE.Group();
    g.position.set(x, y, z);
    const spin = new THREE.Group();
    g.add(spin);
    cylX(spin, r, w, 0, 0, 0, tyre, 14);
    cylX(spin, r * 0.55, w + 0.02, 0, 0, 0, rim, 8);
    box(spin, w + 0.04, r * 0.25, 0.06, 0, r * 0.3, 0, M('#6f747b'));       // щоб було видно, що крутиться
    parent.add(g);
    return { g, spin, x, y, z, r };
  }

  const tmpQ = new THREE.Quaternion();
  return {
    tractor, trailer, ext, cabin,
    /** Поза тіла (уже згладжена в main.js) і колеса з контролера. */
    syncBody(group, pos, quat) { group.position.copy(pos); group.quaternion.copy(quat); },
    syncWheels(vc, list, steerSign) {
      for (let i = 0; i < list.length; i++) {
        const w = list[i];
        const len = vc.wheelSuspensionLength(i);
        w.g.position.y = w.y - (len == null ? 0.4 : len);
        w.g.rotation.y = (vc.wheelSteering(i) || 0) * steerSign;
        w.spin.rotation.x = -(vc.wheelRotation(i) || 0);
      }
    },
    tWheels, rWheels, legs, wheelPivot, jug,
    setCabin(on) { ext.visible = !on; cabin.visible = on; },
    setLegs(down) { legs.position.y = down ? 0 : 0.55; legs.scale.y = down ? 1 : 0.5; },
    /** Борт причепа під вантаж замовлення: колір смуги й підпис. */
    setCargo(cargo) {
      sideTexture(THREE, cargo, side.image, false);
      sideTexture(THREE, cargo, sideR.image, true);
      side.needsUpdate = true; sideR.needsUpdate = true;
    },
    tmpQ,
  };
}

// колір смуги на борту за вантажем
const STRIPE = { jugs: '#b5482f', tiles: '#2f6fa8', roof: '#a5352a', honey: '#d39a1c', hay: '#8a9a2a', salt: '#8b96a3', clay: '#7a5234', goods: '#2f8a7a' };

/** Борт причепа. potRight — глечик праворуч, напис ліворуч від нього (для борту, де кабіна праворуч). */
function sideTexture(THREE, cargo, canvas, potRight) {
  const c = canvas || document.createElement('canvas');
  c.width = 1024; c.height = 208;
  const g = c.getContext('2d');
  g.fillStyle = '#efe6d2'; g.fillRect(0, 0, 1024, 208);
  g.fillStyle = (cargo && STRIPE[cargo.id]) || '#b5482f'; g.fillRect(0, 168, 1024, 18);
  g.fillStyle = '#d08a2c'; g.fillRect(0, 186, 1024, 8);
  // глечик (вушко — у бік напису)
  const px = potRight ? 1024 - 130 : 130, dir = potRight ? -1 : 1;
  g.fillStyle = '#c26a3a';
  g.beginPath(); g.ellipse(px, 100, 52, 58, 0, 0, Math.PI * 2); g.fill();
  g.fillRect(px - 18, 28, 36, 30);
  g.beginPath(); g.ellipse(px, 28, 26, 8, 0, 0, Math.PI * 2); g.fill();
  g.strokeStyle = '#c26a3a'; g.lineWidth = 9;
  g.beginPath(); g.arc(px + dir * 48, 78, 24, potRight ? Math.PI - 1.4 : -1.2, potRight ? Math.PI + 1.2 : 1.4); g.stroke();
  g.fillStyle = '#efe6d2'; g.fillRect(px - 46, 98, 92, 6);
  g.textAlign = potRight ? 'right' : 'left';
  g.fillStyle = '#2f3a4a'; g.font = '900 88px system-ui'; g.textBaseline = 'middle';
  g.fillText('ГЛЕЧИКИ', potRight ? 1024 - 230 : 230, 92);
  g.fillStyle = '#6b5c46'; g.font = '600 30px system-ui';
  g.fillText(cargo ? 'вантаж: ' + cargo.title.toLowerCase() + ' · чумацька служба' : 'перевезення крихкого · чумацька служба', potRight ? 1024 - 236 : 236, 146);
  if (canvas) return null;
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 4;
  return t;
}
