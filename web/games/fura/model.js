// Low-poly вигляд тягача й причепа. Групи лежать в осях своїх тіл (та сама система, що в truck.js: +Z вперед,
// праворуч −X), тож синхронізація — це поза тіла плюс колеса з даних контролера (довжина підвіски, оберт, кут).
// Для камери з кабіни зовнішня кабіна ховається, а показується «салон»: панель, кермо, стійки, дзеркала.
// Вигляд із Лавки «🚚 Фура» (гараж 0.0.6, setLook): фарба й «Металік», розпис на кабіні й борті, річ на панелі, наліпки й
// номер на задніх дверях, вимпел, люстра. Привид колони (buildGhost) бачить фарбу, наліпки й номер — з `convoy[].car`.

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
  // вигляд: що фарбується (кабіна, дефлектор), блиск «Металік», розпис на боках кабіни, номер спереду
  const paintMeshes = [];
  ext.traverse((o) => { if (o.material === paint) paintMeshes.push(o); });
  const gloss = new THREE.MeshPhongMaterial({ color: '#b5482f', shininess: 80, specular: '#8a8a8a', flatShading: true });
  const artMat = new THREE.MeshLambertMaterial({ transparent: true, depthWrite: false, polygonOffset: true, polygonOffsetFactor: -2 });
  artMat.visible = false;
  for (const s of [1, -1]) {
    const a = new THREE.Mesh(new THREE.PlaneGeometry(1.95, 0.98), artMat);
    a.position.set(s * 1.256, 0.42, 2.1); a.rotation.y = s * Math.PI / 2; ext.add(a);
  }
  const plateTex = new THREE.CanvasTexture(plateCanvas(null, ''));
  plateTex.colorSpace = THREE.SRGBColorSpace;
  const frontPlate = new THREE.Mesh(new THREE.PlaneGeometry(0.52, 0.115), new THREE.MeshLambertMaterial({ map: plateTex }));
  frontPlate.position.set(0, -0.27, 3.28); ext.add(frontPlate);
  const roof = new THREE.Group();          // вимпел / прапорці / люстра — будуються, коли вдягнуто
  ext.add(roof);

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
  const dashBox = new THREE.Group();       // кіт / вервечка / ялинка з Лавки
  cabin.add(dashBox);

  const tWheels = TRACTOR.wheels.map(([x, y, z], i) => wheel(tractor, x, y, z, TRACTOR.wheelR, i >= 2 ? 0.62 : 0.38));

  // ---------------- причіп
  const trailer = new THREE.Group();
  // Два малюнки борту, а не один віддзеркалений: дзеркало перевертало й літери («ГЛЕЧИКИ» задом наперед, 25.10).
  // На +X глечик ліворуч, на −X — праворуч: з обох боків він біля кабіни, а напис читається.
  const side = sideTexture(THREE, null, null, false), sideR = sideTexture(THREE, null, null, true);
  const cream = M('#efe6d2');
  const sideMat = new THREE.MeshLambertMaterial({ map: side });
  const sideMatR = new THREE.MeshLambertMaterial({ map: sideR });
  const backCv = backCanvas(null, [], '');
  const backTex = new THREE.CanvasTexture(backCv);
  backTex.colorSpace = THREE.SRGBColorSpace; backTex.anisotropy = 4;
  const back = new THREE.MeshLambertMaterial({ map: backTex });
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

  // ---------------- вигляд (setLook)
  let artNow = null, cargoNow = null, dashNow = null, lampMat = null, lightsOn = false;
  const dashObj = {};
  function sides() {
    sideTexture(THREE, cargoNow, side.image, false, artNow);
    sideTexture(THREE, cargoNow, sideR.image, true, artNow);
    side.needsUpdate = true; sideR.needsUpdate = true;
  }
  function buildDash(id) {
    if (dashObj[id]) return dashObj[id];
    const g = new THREE.Group(), pivot = new THREE.Group();
    g.add(pivot);
    if (id === 'dashcat') {
      const fur = M('#d08a4c'), dk = M('#3a2a20');
      g.position.set(-0.35, 1.215, 3.0);
      const body = new THREE.Mesh(new THREE.SphereGeometry(0.045, 8, 6), fur); body.scale.set(0.9, 0.8, 1.2); g.add(body);
      pivot.position.set(0, 0.04, 0.035);
      const head = new THREE.Mesh(new THREE.SphereGeometry(0.032, 8, 6), fur); head.position.set(0, 0.02, 0.012); pivot.add(head);
      for (const s of [1, -1]) {
        const ear = new THREE.Mesh(new THREE.ConeGeometry(0.012, 0.024, 4), fur); ear.position.set(s * 0.018, 0.05, 0.01); pivot.add(ear);
        const eye = new THREE.Mesh(new THREE.SphereGeometry(0.005, 4, 3), dk); eye.position.set(s * 0.012, 0.025, 0.04); pivot.add(eye);
      }
    } else {
      // висить на нитці під дзеркалом — маятник
      g.position.set(0.05, 2.12, 3.02);
      const str = new THREE.Mesh(new THREE.CylinderGeometry(0.002, 0.002, 0.2, 4), M('#eae2cf')); str.position.y = -0.1; pivot.add(str);
      if (id === 'dashtree') {
        const tree = new THREE.Mesh(new THREE.ConeGeometry(0.045, 0.12, 6), M('#2f7d3a')); tree.position.y = -0.26; tree.scale.z = 0.25; pivot.add(tree);
      } else {
        const cols = ['#2f6fd0', '#f3efe8', '#2f6fd0', '#f2c94c', '#2f6fd0'];
        cols.forEach((c, i) => { const b = new THREE.Mesh(new THREE.SphereGeometry(i === 2 ? 0.022 : 0.011, 6, 4), M(c)); b.position.y = -0.2 - i * 0.022; pivot.add(b); });
      }
    }
    g.traverse((o) => { if (o.isMesh) { o.castShadow = false; o.receiveShadow = false; } });
    g.visible = false;
    dashBox.add(g);
    return (dashObj[id] = { g, pivot });
  }
  function buildRoof(look) {
    while (roof.children.length) roof.remove(roof.children[0]);
    lampMat = null;
    if (look.flag === 'flagvympel') {
      const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.02, 0.02, 0.9, 6), chrome); pole.position.set(-1.05, 2.75, 1.2); roof.add(pole);
      const sh = new THREE.Shape(); sh.moveTo(0, 0); sh.lineTo(0, 0.28); sh.lineTo(-0.7, 0.14); sh.closePath();
      const pen = new THREE.Mesh(new THREE.ShapeGeometry(sh), new THREE.MeshLambertMaterial({ color: '#c8322a', side: THREE.DoubleSide }));
      pen.position.set(-1.05, 2.9, 1.2); pen.rotation.y = Math.PI / 2; roof.add(pen);
    } else if (look.flag === 'flagua') {
      for (const s of [1, -1]) {
        const pole = new THREE.Mesh(new THREE.CylinderGeometry(0.015, 0.015, 0.6, 6), chrome); pole.position.set(s * 1.15, 2.6, 3.0); roof.add(pole);
        for (const [c, y] of [['#2a5bd7', 2.8], ['#f5d000', 2.68]]) {
          const fl = new THREE.Mesh(new THREE.PlaneGeometry(0.34, 0.12), new THREE.MeshLambertMaterial({ color: c, side: THREE.DoubleSide }));
          fl.position.set(s * 1.15 - s * 0.17, y, 3.0); fl.rotation.y = Math.PI / 2; roof.add(fl);
        }
      }
    }
    if (look.lamp) {
      const gold = look.lamp === 'lampotaman';
      box(roof, 2.1, 0.12, 0.16, 0, 2.88, 2.35, gold ? M('#b8902a') : dark);
      lampMat = new THREE.MeshLambertMaterial({ color: gold ? '#fff0b0' : '#fff7dd', emissive: gold ? '#ffcc40' : '#fff0c0', emissiveIntensity: lightsOn ? 2.6 : 0.5 });
      for (let i = 0; i < 6; i++) box(roof, 0.26, 0.08, 0.02, -0.82 + i * 0.33, 2.88, 2.44, lampMat);
    }
    roof.traverse((o) => { if (o.isMesh) o.castShadow = true; });
  }
  /** Вигляд із виду (`career.look` / `convoy[].car.look`): ids речей Лавки «🚚 Фура»; nick — для номера. */
  function setLook(look, nick) {
    look = look || {};
    const pc = paintOf(look), rb = pc === 'rainbow';
    const col = rb ? '#ffffff' : pc || '#b5482f';
    for (const m of [paint, gloss]) {
      m.color.set(col);
      m.map = rb ? rainbowTexture(THREE) : null;
      m.needsUpdate = true;
    }
    for (const o of paintMeshes) o.material = look.gloss ? gloss : paint;
    const art = look.art || null;
    if (art !== artNow) {
      artNow = art;
      if (artMat.map) { artMat.map.dispose(); artMat.map = null; }
      if (art) { const t = new THREE.CanvasTexture(artCanvas(art, 512, 256)); t.colorSpace = THREE.SRGBColorSpace; artMat.map = t; }
      artMat.visible = !!art; artMat.needsUpdate = true;
      sides();
    }
    const dash = look.dash || null;
    if (dash !== dashNow) {
      dashNow = dash;
      for (const k of Object.keys(dashObj)) dashObj[k].g.visible = false;
      if (dash && dash !== 'dashjug') buildDash(dash).g.visible = true;
      jug.visible = dash !== 'dashcat';
      jug.rotation.set(0, 0, 0);
    }
    const plate = plateOf(nick);
    plateCanvas(plateTex.image, plate); plateTex.needsUpdate = true;
    backCanvas(backCv, look.stickers || [], plate); backTex.needsUpdate = true;
    buildRoof(look);
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
    /** Фари (L): у колоні їх бачать і інші — прапорець 2 у позі. Люстра з Лавки світиться з фарами. */
    setLights(on) { lamp.emissiveIntensity = on ? 2.4 : 0.9; lightsOn = on; if (lampMat) lampMat.emissiveIntensity = on ? 2.6 : 0.5; },
    setLook,
    /** Пружина від прискорення причепа (main.js): хитає «Глечик на панелі» й речі на нитці; кіт кивує. Без речі глечик стоїть. */
    setDash(rx, rz, now) {
      if (dashNow === 'dashjug') jug.rotation.set(rx, 0, rz);
      const d = dashNow && dashObj[dashNow];
      if (!d) return;
      if (dashNow === 'dashcat') d.pivot.rotation.set(0.18 * Math.sin(now / 520) + rx * 3, 0, rz * 1.5);
      else d.pivot.rotation.set(rx * 5, 0, rz * 5);
    },
    setLegs(down) { legs.position.y = down ? 0 : 0.55; legs.scale.y = down ? 1 : 0.5; },
    /** Борт причепа під вантаж замовлення: колір смуги й підпис. */
    setCargo(cargo) { cargoNow = cargo; sides(); },
    tmpQ,
  };
}

/** Чужа фура колони — «привид»: та сама форма грубо (кабіна, рама, кузов, по шість коліс), без салону й текстур, щоб
 *  вісім фур коштували менше за одну свою. Осі ті самі, що в buildModels; фари, стоп і поворот кольору — з прапорців кадру. */
export function buildGhost(THREE, scene, color) {
  const mats = [];
  const M = (c, extra) => { const m = new THREE.MeshLambertMaterial({ color: c, flatShading: true, ...(extra || {}) }); mats.push(m); return m; };
  const geos = [];
  const box = (g, w, h, d, x, y, z, m) => { const geo = new THREE.BoxGeometry(w, h, d); geos.push(geo); const o = new THREE.Mesh(geo, m); o.position.set(x, y, z); g.add(o); return o; };
  const paint = M(color), stripe = M(color), dark = M('#2a2b2e'), glass = M('#26394d'), cream = M('#efe6d2'), tyre = M('#1d1d1f'),
    lamp = M('#fff3c4', { emissive: '#ffe7a0', emissiveIntensity: 0.25 }), red = M('#d93a2b', { emissive: '#ff2a12', emissiveIntensity: 0.15 });
  const wheelGeo = new THREE.CylinderGeometry(1, 1, 1, 8); geos.push(wheelGeo);
  wheelGeo.rotateZ(Math.PI / 2);
  const wheel = (g, x, y, z, r, w) => { const o = new THREE.Mesh(wheelGeo, tyre); o.scale.set(w, r, r); o.position.set(x, y, z); g.add(o); return o; };

  const tractor = new THREE.Group();
  box(tractor, 2.5, 2.6, 2.2, 0, 1.0, 2.1, paint);
  box(tractor, 2.3, 0.55, 1.5, 0, 2.55, 1.9, paint);
  box(tractor, 2.2, 0.95, 0.06, 0, 1.55, 3.2, glass);
  box(tractor, 2.5, 0.38, 0.3, 0, -0.27, 3.12, dark);
  box(tractor, 1.1, 0.32, 5.7, 0, -0.18, -0.55, dark);
  for (const s of [1, -1]) { box(tractor, 0.36, 0.2, 0.06, s * 0.88, -0.05, 3.27, lamp); box(tractor, 0.25, 0.14, 0.06, s * 0.9, -0.15, -3.42, red); }
  const tw = TRACTOR.wheels.map(([x, y, z], i) => wheel(tractor, x, y - 0.42, z, TRACTOR.wheelR, i >= 2 ? 0.62 : 0.38));

  const trailer = new THREE.Group();
  box(trailer, TRAILER.w, TRAILER.h, TRAILER.len, 0, TRAILER.h / 2, 0, cream);
  box(trailer, TRAILER.w + 0.02, 0.3, TRAILER.len - 0.4, 0, 0.35, 0, stripe);                   // смуга колонника (колір місця)
  box(trailer, 1.1, 0.26, 13.0, 0, -0.14, 0, dark);
  for (const s of [1, -1]) box(trailer, 0.3, 0.14, 0.06, s * 0.95, -0.35, -6.83, red);
  const rw = TRAILER.wheels.map(([x, y, z]) => wheel(trailer, x, y - 0.5, z, TRAILER.wheelR, 0.62));

  for (const g of [tractor, trailer]) g.traverse((o) => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = false; } });
  scene.add(tractor, trailer);
  let flags = -1, backMesh = null, lookKey = '';
  return {
    tractor, trailer, wheels: tw.concat(rw),
    /** f: 1 сигнал, 2 фари, 4 стоп. */
    setFlags(f) {
      if (f === flags) return;
      flags = f;
      lamp.emissiveIntensity = f & 2 ? 2.2 : 0.25;
      red.emissiveIntensity = f & 4 ? 2.4 : 0.15;
    },
    setVisible(on) { tractor.visible = trailer.visible = on; },
    /** Вигляд чужої фури з `convoy[].car.look`: фарба кабіни (смуга лишається кольором місця), наліпки й номер ззаду. */
    setLook(look, nick) {
      const key = JSON.stringify([look, nick]);
      if (key === lookKey) return;
      lookKey = key;
      const pc = paintOf(look);
      paint.color.set(pc === 'rainbow' ? '#e0b030' : pc || color);
      if (!backMesh) {
        const t = new THREE.CanvasTexture(backCanvas(null, [], ''));
        t.colorSpace = THREE.SRGBColorSpace;
        const m = M('#ffffff'); m.map = t;
        const geo = new THREE.PlaneGeometry(TRAILER.w - 0.05, TRAILER.h - 0.05); geos.push(geo);
        backMesh = new THREE.Mesh(geo, m);
        backMesh.position.set(0, TRAILER.h / 2, -TRAILER.len / 2 - 0.02); backMesh.rotation.y = Math.PI;
        trailer.add(backMesh);
      }
      backCanvas(backMesh.material.map.image, (look && look.stickers) || [], plateOf(nick));
      backMesh.material.map.needsUpdate = true;
    },
    dispose() {
      scene.remove(tractor, trailer);
      for (const g of geos) g.dispose();
      for (const m of mats) { if (m.map) m.map.dispose(); m.dispose(); }
    },
  };
}

// ---------------- вигляд з Лавки «🚚 Фура»: id речей — Lavka.cs (група fura-*)
/** Фарби — ті самі відтінки, що кольори ніка (hue). */
export const PAINT = { paintember: 0, paintamber: 32, paintlinden: 62, paintgrass: 85, paintemerald: 110, paintmint: 135,
  paintsky: 195, paintcornflower: 212, paintsapphire: 228, paintlilac: 248, paintviolet: 270, paintraspberry: 318 };
export const STICKERS = {
  stickhlechyky: { text: 'ГЛЕЧИКИ', icon: '🏺', bg: '#b5482f', fg: '#fff3dc' },
  stickshlyakh: { text: 'ЧУМАЦЬКИЙ ШЛЯХ', icon: '✨', bg: '#23315c', fg: '#f2e6b8' },
  stickmama: { text: 'ЇДУ ДО МАМИ', icon: '💛', bg: '#f2c94c', fg: '#3a2a05' },
  stickmakitry: { text: 'ОБЕРЕЖНО: МАКІТРИ', icon: '⚠️', bg: '#f0a030', fg: '#2a1a05' },
  stickstar: { text: 'ЗОРЯНИЙ ЧУМАК', icon: '⭐', bg: '#1d2a4a', fg: '#ffd96a' },
};
/** Колір кабіни з вигляду: '#rrggbb', 'rainbow' або null («Чумак» як є). */
export function paintOf(look) {
  const id = look && look.paint;
  if (!id) return null;
  if (id === 'paintrainbow') return 'rainbow';
  const h = PAINT[id];
  return h == null ? null : hsl(h, 0.58, 0.42);
}
function hsl(h, s, l) {
  const a = s * Math.min(l, 1 - l), f = (n) => { const k = (n + h / 30) % 12; return l - a * Math.max(-1, Math.min(k - 3, 9 - k, 1)); };
  return '#' + [f(0), f(8), f(4)].map((x) => Math.round(x * 255).toString(16).padStart(2, '0')).join('');
}
/** Номерний знак з ніка (свого номера сервер ще не дає): «ГЛ 0427 ЧК» — у всіх у колоні той самий. */
export function plateOf(nick) {
  let h = 2166136261;
  for (const ch of String(nick || 'чумак').toLowerCase()) { h ^= ch.codePointAt(0); h = Math.imul(h, 16777619) >>> 0; }
  const L = ['ЧК', 'ГЛ', 'ВЗ', 'КР', 'ПЛ', 'МД', 'СЛ', 'ДН'];
  return L[h % 8] + ' ' + String(h % 10000).padStart(4, '0') + ' ' + L[(h >>> 8) % 8];
}

let rainbowT = null;
function rainbowTexture(THREE) {
  if (rainbowT) return rainbowT;
  const c = document.createElement('canvas'); c.width = 8; c.height = 256;
  const g = c.getContext('2d'), gr = g.createLinearGradient(0, 0, 0, 256);
  ['#e53935', '#fb8c00', '#fdd835', '#43a047', '#1e88e5', '#3949ab', '#8e24aa'].forEach((x, i, a) => gr.addColorStop(i / (a.length - 1), x));
  g.fillStyle = gr; g.fillRect(0, 0, 8, 256);
  rainbowT = new THREE.CanvasTexture(c); rainbowT.colorSpace = THREE.SRGBColorSpace;
  return rainbowT;
}

/** Номерний знак: білий, синя смужка «UA» ліворуч. */
function plateCanvas(c, text) {
  c = c || document.createElement('canvas');
  c.width = 256; c.height = 56;
  const g = c.getContext('2d');
  g.fillStyle = '#f7f7f2'; g.fillRect(0, 0, 256, 56);
  g.strokeStyle = '#222'; g.lineWidth = 3; g.strokeRect(2, 2, 252, 52);
  g.fillStyle = '#1f4fbf'; g.fillRect(4, 4, 30, 48);
  g.fillStyle = '#f5d000'; g.fillRect(10, 10, 18, 12);
  g.fillStyle = '#fff'; g.font = '700 14px system-ui'; g.textAlign = 'center'; g.fillText('UA', 19, 44);
  g.fillStyle = '#111'; g.font = '800 34px system-ui'; g.textBaseline = 'middle';
  g.fillText(text || '', 146, 30, 206);
  return c;
}

/** Задні двері причепа: стулки, до трьох наліпок, номер унизу. */
function backCanvas(c, stickers, plate) {
  c = c || document.createElement('canvas');
  c.width = 512; c.height = 560;
  const g = c.getContext('2d');
  g.fillStyle = '#e2d7bf'; g.fillRect(0, 0, 512, 560);
  g.fillStyle = '#c7bba1'; g.fillRect(253, 0, 6, 560);
  for (const x of [128, 384]) { g.fillStyle = '#a99d84'; g.fillRect(x - 3, 40, 6, 480); }
  (stickers || []).slice(0, 3).forEach((id, i) => {
    const s = STICKERS[id];
    if (!s) return;
    const w = 440, h = 64, x = 36, y = 70 + i * 86;
    g.fillStyle = s.bg; roundRect(g, x, y, w, h, 18); g.fill();
    g.fillStyle = s.fg; g.font = '800 30px system-ui'; g.textAlign = 'center'; g.textBaseline = 'middle';
    g.fillText(s.icon + ' ' + s.text, 256, y + h / 2 + 2, w - 24);
  });
  if (plate) {
    const p = plateCanvas(null, plate);
    g.drawImage(p, 128, 470, 256, 56);
  }
  return c;
}
function roundRect(g, x, y, w, h, r) { g.beginPath(); g.moveTo(x + r, y); g.arcTo(x + w, y, x + w, y + h, r); g.arcTo(x + w, y + h, x, y + h, r); g.arcTo(x, y + h, x, y, r); g.arcTo(x, y, x + w, y, r); g.closePath(); }

/** Розпис на кабіну (прозоре тло): петриківка, вишиванка, чумацький віз, трипілля. */
function artCanvas(id, w, h) {
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  drawArt(c.getContext('2d'), id, 0, 0, w, h);
  return c;
}
function drawArt(g, id, x0, y0, w, h) {
  g.save(); g.translate(x0, y0);
  const lineW = Math.max(2, h / 40);
  if (id === 'artvyshyvanka') {
    // хрестики ромбами: червоне з чорним, у два ряди
    const s = h / 10;
    for (let row = 0; row < 2; row++) for (let i = 0; i * s * 6 < w + s * 6; i++) {
      const cx = i * s * 6 + (row ? s * 3 : 0), cy = h * (row ? 0.68 : 0.32);
      for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
        if (Math.abs(dx) + Math.abs(dy) > 2) continue;
        g.strokeStyle = Math.abs(dx) + Math.abs(dy) === 2 ? '#c4161c' : (dx + dy) % 2 ? '#1a1a1a' : '#c4161c';
        g.lineWidth = lineW;
        const px = cx + dx * s, py = cy + dy * s, r = s * 0.38;
        g.beginPath(); g.moveTo(px - r, py - r); g.lineTo(px + r, py + r); g.moveTo(px + r, py - r); g.lineTo(px - r, py + r); g.stroke();
      }
    }
    g.fillStyle = '#c4161c'; g.fillRect(0, h * 0.04, w, lineW); g.fillRect(0, h * 0.94, w, lineW);
  } else if (id === 'artpetrykivka') {
    // гілка з квітами-«купавками», листям і завитками
    g.strokeStyle = '#2f7d3a'; g.lineWidth = lineW * 1.4;
    g.beginPath(); g.moveTo(0, h * 0.62);
    for (let x = 0; x <= w; x += w / 8) g.quadraticCurveTo(x + w / 16, h * (x / (w / 8) % 2 ? 0.45 : 0.78), x + w / 8, h * 0.62);
    g.stroke();
    for (let i = 0; i < 5; i++) {
      const cx = w * (0.1 + i * 0.2), cy = h * (i % 2 ? 0.36 : 0.5), r = h * 0.17;
      for (let k = 0; k < 7; k++) {
        const a = -Math.PI + (k / 6) * Math.PI;
        g.fillStyle = k % 2 ? '#f0a030' : '#d9381e';
        g.beginPath(); g.ellipse(cx + Math.cos(a) * r * 0.7, cy + Math.sin(a) * r * 0.7, r * 0.55, r * 0.22, a, 0, Math.PI * 2); g.fill();
      }
      g.fillStyle = '#f5d000'; g.beginPath(); g.arc(cx, cy, r * 0.3, 0, Math.PI * 2); g.fill();
      for (const s of [-1, 1]) {
        g.fillStyle = '#3f9a45';
        g.beginPath(); g.ellipse(cx + s * r * 1.1, cy + r * 0.9, r * 0.5, r * 0.18, s * 0.6, 0, Math.PI * 2); g.fill();
        g.strokeStyle = '#2f7d3a'; g.lineWidth = lineW;
        g.beginPath(); g.arc(cx + s * r * 1.6, cy + r * 0.2, r * 0.3, 0, Math.PI * 1.5); g.stroke();
      }
    }
  } else if (id === 'arttrypillia') {
    // спіралі трипільської кераміки: охра, чорне, біле
    const n = Math.max(2, Math.round(w / (h * 0.9)));
    for (let i = 0; i < n; i++) {
      const cx = (i + 0.5) * (w / n), cy = h / 2, R = h * 0.38;
      for (const [col, wd] of [['#f3efe8', lineW * 3.2], ['#2a1d14', lineW * 1.6]]) {
        g.strokeStyle = col; g.lineWidth = wd; g.beginPath();
        for (let t = 0; t < Math.PI * 5; t += 0.1) {
          const r = R * (1 - t / (Math.PI * 5.4)), d = i % 2 ? 1 : -1;
          const px = cx + Math.cos(t * d) * r, py = cy + Math.sin(t * d) * r;
          if (t === 0) g.moveTo(px, py); else g.lineTo(px, py);
        }
        g.stroke();
      }
    }
    g.fillStyle = '#9c4a1c'; g.fillRect(0, 0, w, lineW * 2); g.fillRect(0, h - lineW * 2, w, lineW * 2);
  } else if (id === 'artviz') {
    // чумацький віз із волом і зорями Чумацького Шляху
    const brown = '#4a2f1c';
    g.fillStyle = '#f2e6b8';
    for (let i = 0; i < 14; i++) { g.beginPath(); g.arc((i * 97) % w, h * (0.08 + ((i * 37) % 30) / 100), lineW * (i % 3 ? 0.8 : 1.4), 0, Math.PI * 2); g.fill(); }
    const s = h / 100, bx = w * 0.42;
    g.fillStyle = brown;
    g.fillRect(bx, 48 * s, 60 * s, 16 * s);                                   // віз
    for (let k = 0; k < 6; k++) g.fillRect(bx + k * 11 * s, 34 * s, 3 * s, 15 * s);
    g.fillRect(bx - 34 * s, 56 * s, 36 * s, 3 * s);                             // дишло
    g.strokeStyle = brown; g.lineWidth = 3 * s;
    for (const wx of [bx + 10 * s, bx + 50 * s]) {
      g.beginPath(); g.arc(wx, 70 * s, 12 * s, 0, Math.PI * 2); g.stroke();
      for (let a = 0; a < 6; a++) { g.beginPath(); g.moveTo(wx, 70 * s); g.lineTo(wx + Math.cos(a) * 12 * s, 70 * s + Math.sin(a) * 12 * s); g.stroke(); }
    }
    const ox = bx - 80 * s;                                                    // віл
    g.beginPath(); g.ellipse(ox + 22 * s, 56 * s, 24 * s, 12 * s, 0, 0, Math.PI * 2); g.fill();
    g.beginPath(); g.ellipse(ox - 4 * s, 50 * s, 9 * s, 7 * s, -0.4, 0, Math.PI * 2); g.fill();
    for (const k of [6, 14, 30, 38]) g.fillRect(ox + k * s, 62 * s, 4 * s, 18 * s);
    g.lineWidth = 2.5 * s; g.beginPath(); g.moveTo(ox - 8 * s, 44 * s); g.quadraticCurveTo(ox - 14 * s, 34 * s, ox - 4 * s, 34 * s); g.stroke();
    g.fillStyle = brown; g.font = '800 ' + Math.round(16 * s) + 'px system-ui'; g.textAlign = 'center';
    g.fillText('ЧУМАКИ', w * 0.82, 66 * s);
  }
  g.restore();
}


// колір смуги на борту за вантажем
const STRIPE = { jugs: '#b5482f', tiles: '#2f6fa8', roof: '#a5352a', honey: '#d39a1c', hay: '#8a9a2a', salt: '#8b96a3', clay: '#7a5234', goods: '#2f8a7a' };

/** Борт причепа. potRight — глечик праворуч, напис ліворуч від нього (для борту, де кабіна праворуч). */
function sideTexture(THREE, cargo, canvas, potRight, art) {
  const c = canvas || document.createElement('canvas');
  c.width = 1024; c.height = 208;
  const g = c.getContext('2d');
  g.fillStyle = '#efe6d2'; g.fillRect(0, 0, 1024, 208);
  if (art) drawArt(g, art, 0, 2, 1024, 22);         // розпис — смугою вгорі борту
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
