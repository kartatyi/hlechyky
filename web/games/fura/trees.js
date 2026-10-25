// Дерева «Чумаків» 0.0.5 (STYLE.md §15.4, §16; перенесено з проб 3–4 — fura/style: probe3.js makeTreeLOD, scene.js
// treeGeometries, trees3.js). Типи: кругла крона, плодове, тополя, сосна. Стилі крон — «грані» (як було), «гладкі»
// (густіші кулі, нормалі від центру), «пухнасті» (хмарка квадів-пучків листя, повернутих до камери, з рваним силуетом
// від своєї текстури). LOD за бюджетом: повне → просте (одна гладка куля) → імпостер (квад до камери з текстурою,
// запеченою при завантаженні) → нічого. Інстанси за типом і рівнем: ≤ 12 draw calls на весь край; тінь кидає лише
// повне (коробка тіні — до ~70 м). Перерозподіл — коли камера зрушила на 6 м.

import { PAL } from './materials.js';

// Пріоритети (STYLE.md §15.4): фура, дорога, знаки, будинки — завжди; бюджет ріже траву й дерева
export const BUDGET = [
  { name: 'Low', full: 50, simple: 250, imp: 800, grass: 0 },
  { name: 'Medium', full: 110, simple: 450, imp: 1100, grass: 40 },
  { name: 'High', full: 200, simple: 750, imp: 1400, grass: 70 },
  { name: 'Ultra', full: 450, simple: 1600, imp: 2000, grass: 110 },
];
const CROWN = { round: [PAL.crown, PAL.crownLight, PAL.crownDark], fruit: [PAL.crownLight, PAL.willow], poplar: [PAL.poplar, PAL.crownDark], pine: [PAL.pine, PAL.pineDark] };

function rng(seed) { let s = seed >>> 0 || 1; return () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; }; }

/** Геометрії за типом: det 2 — крона з 2–3 куль, det 1 — одна куля; sub — густота куль (1 — гладкі крони без кутів).
 *  Колір кори — у вертексах (÷ колір крони: інстанс множить). */
function treeGeometries(THREE, det, sub = 0) {
  // [geo, matrix, color, aoBottom, radial] — radial: нормалі від центру кулі (гладка крона без граней)
  const merge = (parts) => {
    const pos = [], nrm = [], col = [];
    const c = new THREE.Color(), v = new THREE.Vector3(), n = new THREE.Vector3();
    for (const [geo, m, hex, ao, radial] of parts) {
      if (geo.index) geo.computeVertexNormals();
      const g = geo.index ? geo.toNonIndexed() : geo;
      const nm = new THREE.Matrix3().getNormalMatrix(m);
      for (let i = 0; i < g.attributes.position.count; i++) {
        v.fromBufferAttribute(g.attributes.position, i);
        const ly = v.y;
        if (radial) n.copy(v).normalize(); else n.fromBufferAttribute(g.attributes.normal, i);
        n.applyMatrix3(nm).normalize();
        v.applyMatrix4(m);
        pos.push(v.x, v.y, v.z); nrm.push(n.x, n.y, n.z);
        c.set(hex); const k = ao ? 0.62 + 0.38 * Math.min(1, Math.max(0, (ly + 1) / 2)) : 1; col.push(c.r * k, c.g * k, c.b * k);
      }
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setAttribute('normal', new THREE.Float32BufferAttribute(nrm, 3));
    geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
    return geo;
  };
  const M = (x, y, z, sx, sy, sz) => new THREE.Matrix4().compose(new THREE.Vector3(x, y, z), new THREE.Quaternion(), new THREE.Vector3(sx, sy, sz));
  const ico = (d) => new THREE.IcosahedronGeometry(1, d + sub);
  const trunk = (h, rr) => [new THREE.CylinderGeometry(rr * 0.7, rr, h, 5), M(0, h / 2, 0, 1, 1, 1), '#ffffff', false, false];
  const lvl2 = det >= 2;
  const types = {
    round: merge(lvl2
      ? [trunk(2.6, 0.26), [ico(0), M(0, 3.9, 0, 2.0, 1.7, 2.0), '#ffffff', true, true], [ico(0), M(0.9, 4.6, 0.4, 1.4, 1.3, 1.4), '#ffffff', true, true], [ico(0), M(-0.8, 4.4, -0.5, 1.5, 1.3, 1.5), '#ffffff', true, true]]
      : [trunk(2.4, 0.26), [ico(0), M(0, 3.7, 0, 1.9, 1.8, 1.9), '#ffffff', false, true]]),
    fruit: merge(lvl2
      ? [trunk(1.6, 0.18), [ico(0), M(0, 2.6, 0, 1.8, 1.3, 1.8), '#ffffff', true, true], [ico(0), M(0.7, 3.0, 0.3, 1.2, 1.0, 1.2), '#ffffff', true, true]]
      : [trunk(1.6, 0.18), [ico(0), M(0, 2.6, 0, 1.7, 1.3, 1.7), '#ffffff', false, true]]),
    poplar: merge(lvl2
      ? [trunk(3, 0.3), [ico(1), M(0, 9, 0, 1.6, 7.5, 1.6), '#ffffff', true, true]]
      : [trunk(3, 0.3), [ico(0), M(0, 9, 0, 1.5, 7.5, 1.5), '#ffffff', false, true]]),
    pine: merge([trunk(3, 0.25), [new THREE.ConeGeometry(2.2, 4.5, 7), M(0, 4.5, 0, 1, 1, 1), '#ffffff', lvl2, false], [new THREE.ConeGeometry(1.7, 4, 7), M(0, 7, 0, 1, 1, 1), '#ffffff', lvl2, false]]),
  };
  // колір інстанса множить вертексний, тож кора = кора / зелень — після множення лишається корою
  const kb = new THREE.Color(PAL.trunk), kc = new THREE.Color(PAL.crown);
  for (const [k, g] of Object.entries(types)) {
    const p = g.attributes.position, c = g.attributes.color, lim = { fruit: 1.25, pine: 2.1, poplar: 1.45, round: 1.85 }[k];
    for (let i = 0; i < p.count; i++) if (p.getY(i) < lim && Math.hypot(p.getX(i), p.getZ(i)) < 0.4) c.setXYZ(i, kb.r / kc.r, kb.g / kc.g, kb.b / kc.b);
  }
  return types;
}

// ---------------------------------------------------------------- пухнасті крони (trees3.js)
// Кулі крон тих самих типів: [центр x, y, z, радіуси x, y, z, кількість квадів]
const CROWNS = {
  round: { trunk: [2.6, 0.26], spheres: [[0, 3.9, 0, 2.0, 1.7, 2.0, 22], [0.9, 4.6, 0.4, 1.4, 1.3, 1.4, 12], [-0.8, 4.4, -0.5, 1.5, 1.3, 1.5, 12]] },
  fruit: { trunk: [1.6, 0.18], spheres: [[0, 2.6, 0, 1.8, 1.3, 1.8, 18], [0.7, 3.0, 0.3, 1.2, 1.0, 1.2, 10]] },
  poplar: { trunk: [3, 0.3], spheres: [[0, 9, 0, 1.45, 7.5, 1.45, 60]], half: 0.55 },     // тополя — вужча, дрібніші пучки
};

/** Текстура «пучка листя» 128²: густо в центрі, рідше з краю; сірі відтінки (колір дає вершина й інстанс). */
function leafTexture(THREE) {
  const c = document.createElement('canvas'); c.width = c.height = 128;
  const g = c.getContext('2d');
  g.fillStyle = 'rgb(200,200,200)'; g.beginPath(); g.arc(64, 64, 30, 0, Math.PI * 2); g.fill();   // центр непрозорий — там UV стовбура
  let s = 11;
  const r = () => { s = (s * 16807) % 2147483647; return s / 2147483647; };
  for (let i = 0; i < 480; i++) {
    const a = r() * Math.PI * 2, d = Math.sqrt(r()) * 54;
    const x = 64 + Math.cos(a) * d, y = 64 + Math.sin(a) * d;
    const v = Math.round(170 + r() * 85 - d * 0.6);                    // до краю — трохи темніше (глибина)
    g.fillStyle = `rgb(${v},${v},${v})`;
    g.save(); g.translate(x, y); g.rotate(r() * Math.PI);
    g.beginPath(); g.ellipse(0, 0, 4 + r() * 5, 2 + r() * 3, 0, 0, Math.PI * 2); g.fill();
    g.restore();
  }
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace; t.anisotropy = 4;
  return t;
}

/** Геометрія пухнастого дерева: стовбур + квади на поверхні куль крони. aLeaf = (кут x, кут y, піврозмір). */
function fluffyGeometry(THREE, k) {
  const C = CROWNS[k];
  const pos = [], nrm = [], col = [], uv = [], leaf = [], idx = [];
  const kb = new THREE.Color(PAL.trunk), kc = new THREE.Color(PAL.crown);
  const tg = new THREE.CylinderGeometry(C.trunk[1] * 0.7, C.trunk[1], C.trunk[0], 6); tg.translate(0, C.trunk[0] / 2, 0);
  const tp = tg.attributes.position, tn = tg.attributes.normal;
  for (let i = 0; i < tp.count; i++) {
    pos.push(tp.getX(i), tp.getY(i), tp.getZ(i)); nrm.push(tn.getX(i), tn.getY(i), tn.getZ(i));
    col.push(kb.r / kc.r, kb.g / kc.g, kb.b / kc.b); uv.push(0.5, 0.5); leaf.push(0, 0, 0);   // uv у центрі текстури — непрозоро
  }
  for (let i = 0; i < tg.index.count; i++) idx.push(tg.index.getX(i));
  // крона: точки Фібоначчі на еліпсоїді (0,82 радіуса), квад у кожній
  const golden = Math.PI * (3 - Math.sqrt(5));
  for (const [cx, cy, cz, rx, ry, rz, n] of C.spheres) {
    const half = Math.min(rx, rz) * (C.half || 0.62);
    for (let i = 0; i < n; i++) {
      const y = 1 - 2 * (i + 0.5) / n, rr = Math.sqrt(1 - y * y), a = i * golden;
      const ux = Math.cos(a) * rr, uz = Math.sin(a) * rr;
      const p = [cx + ux * rx * 0.82, cy + y * ry * 0.82, cz + uz * rz * 0.82];
      const nn = new THREE.Vector3(ux / rx, y / ry, uz / rz).normalize();       // нормаль еліпсоїда
      const ao = 0.62 + 0.38 * (y * 0.5 + 0.5);                                // низ крони темніший
      const b = pos.length / 3;
      for (const [sx, sy] of [[-1, -1], [1, -1], [1, 1], [-1, 1]]) {
        pos.push(...p); nrm.push(nn.x, nn.y, nn.z); col.push(ao, ao, ao);
        uv.push(sx * 0.5 + 0.5, sy * 0.5 + 0.5); leaf.push(sx, sy, half * (0.85 + 0.3 * ((i * 7919) % 13) / 13));
      }
      idx.push(b, b + 1, b + 2, b, b + 2, b + 3);
    }
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('normal', new THREE.Float32BufferAttribute(nrm, 3));
  g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  g.setAttribute('aLeaf', new THREE.Float32BufferAttribute(leaf, 3));
  g.setIndex(idx);
  g.computeBoundingSphere();
  g.boundingSphere.radius += 3;
  return g;
}

// Білборд: квад розгортається в просторі камери (xy), розмір × масштаб інстанса
const BILLBOARD = `vec4 mvPosition = vec4( transformed, 1.0 );
float lsc = 1.0;
#ifdef USE_INSTANCING
  mvPosition = instanceMatrix * mvPosition;
  lsc = length( instanceMatrix[ 0 ].xyz );
#endif
mvPosition = modelViewMatrix * mvPosition;
mvPosition.xy += aLeaf.xy * aLeaf.z * lsc;
gl_Position = projectionMatrix * mvPosition;`;

/** Додати білборд листя до матеріалу (після інших латок — атмосфери тощо). */
function patchLeaf(mat, key) {
  const prev = mat.onBeforeCompile;
  mat.onBeforeCompile = (sh, r) => {
    if (prev) prev(sh, r);
    sh.vertexShader = 'attribute vec3 aLeaf;\n' + sh.vertexShader.replace('#include <project_vertex>', BILLBOARD);
  };
  const k0 = mat.customProgramCacheKey ? mat.customProgramCacheKey() : '';
  mat.customProgramCacheKey = () => k0 + '-leaf-' + key;
  mat.needsUpdate = true;
  return mat;
}

/**
 * Дерева краю. list — [{ x, y, z, s, pine }] з kray.js (колайдери ставить він); patch(m, key) — латка світла неба
 * (sky.patch), бо латку білборда листя треба накласти поверх неї. Повертає { setStyle, update, rebake, info }.
 */
export function makeTrees(THREE, scene, renderer, list, patch) {
  const t0 = performance.now();
  const std = (o) => new THREE.MeshStandardMaterial(Object.assign({ vertexColors: true, flatShading: true, side: THREE.DoubleSide, roughness: 0.92, metalness: 0 }, o));
  const full = treeGeometries(THREE, 2), simple = treeGeometries(THREE, 1);
  const smoothFull = treeGeometries(THREE, 2, 1), smoothSimple = treeGeometries(THREE, 1, 1);
  const leafTex = leafTexture(THREE);
  const fluffy = { round: fluffyGeometry(THREE, 'round'), fruit: fluffyGeometry(THREE, 'fruit'), poplar: fluffyGeometry(THREE, 'poplar'), pine: smoothFull.pine };
  const matFlat = patch(std({ side: THREE.FrontSide }), 'tree-flat');
  const matSmooth = patch(std({ flatShading: false }), 'tree-smooth');
  const matLeaf = patchLeaf(patch(std({ flatShading: false, map: leafTex, alphaTest: 0.5 }), 'leaf'), 'leaf');
  matLeaf.color.setScalar(1.45);                              // текстура листя сіра (~0,7) — повертаємо тон палітри
  const depthLeaf = patchLeaf(new THREE.MeshDepthMaterial({ depthPacking: THREE.RGBADepthPacking, map: leafTex, alphaTest: 0.5 }), 'depth');
  const r = rng(77), tmp = new THREE.Object3D(), cc = new THREE.Color();
  // імпостер: повне дерево збоку (без світла, вертексні кольори) у прозору ціль 128×256
  const impMat = {}, impGeo = {}, impRT = {};
  const cam = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 100), sc = new THREE.Scene();
  function bakeImpostors() {
    const prev = renderer.getRenderTarget(), prevClear = renderer.getClearColor(new THREE.Color()), prevA = renderer.getClearAlpha();
    for (const [k, g] of Object.entries(full)) {
      g.computeBoundingBox();
      const b = g.boundingBox, w = Math.max(b.max.x - b.min.x, b.max.z - b.min.z) * 1.05, h = (b.max.y - b.min.y) * 1.03;
      if (!impRT[k]) impRT[k] = new THREE.WebGLRenderTarget(128, 256, { minFilter: THREE.LinearMipmapLinearFilter, generateMipmaps: true });
      const m = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ vertexColors: true, color: new THREE.Color(PAL.crown) }));
      sc.clear(); sc.add(m);
      Object.assign(cam, { left: -w / 2, right: w / 2, top: h, bottom: 0 }); cam.position.set(0, b.min.y, 50); cam.lookAt(0, b.min.y, 0); cam.updateProjectionMatrix();
      renderer.setRenderTarget(impRT[k]); renderer.setClearColor(0x000000, 0); renderer.clear(); renderer.render(sc, cam);
      m.material.dispose();
      if (!impGeo[k]) {
        const pg = new THREE.PlaneGeometry(w, h); pg.translate(0, h / 2 + b.min.y, 0);
        const n = pg.attributes.normal; for (let i = 0; i < n.count; i++) n.setXYZ(i, 0, 0.6, 0.8);   // нормаль трохи вгору — світло рівніше
        impGeo[k] = pg;
        impMat[k] = patch(new THREE.MeshStandardMaterial({ map: impRT[k].texture, alphaTest: 0.5, side: THREE.DoubleSide, roughness: 0.95 }), 'imp');
      }
    }
    renderer.setRenderTarget(prev); renderer.setClearColor(prevClear, prevA);
  }
  bakeImpostors();
  const base = new THREE.Color(PAL.crown), inv = new THREE.Color(1 / base.r, 1 / base.g, 1 / base.b);
  // тип: сосни — з краю (pine), решта — кругла крона, плодове або тополя (за хешем місця: та сама мапа — ті самі дерева)
  const typeOf = (t) => {
    if (t.pine) return 'pine';
    let h = Math.sin(Math.round(t.x) * 12.9898 + Math.round(t.z) * 78.233) * 43758.5453; h -= Math.floor(h);
    return h < 0.68 ? 'round' : h < 0.84 ? 'fruit' : 'poplar';
  };
  const groups = {};
  for (const k of Object.keys(full)) {
    const items = list.filter((t) => typeOf(t) === k).map((t) => {
      const pal = CROWN[k];
      return { x: t.x, y: t.y, z: t.z, s: t.s, rot: r() * 6.28, col: new THREE.Color(pal[Math.floor(r() * pal.length)]).multiplyScalar(0.92 + r() * 0.16) };
    });
    if (!items.length) continue;
    const mk = (geo, m, cast, name) => {
      const im = new THREE.InstancedMesh(geo, m, items.length);
      im.count = 0; im.castShadow = cast; im.receiveShadow = true; im.frustumCulled = false; im.name = 'trees:' + k + ':' + name;
      im.setColorAt(0, cc.set('#ffffff'));
      scene.add(im);
      return im;
    };
    groups[k] = { items, full: mk(full[k], matFlat, true, 'full'), simple: mk(simple[k], matFlat, false, 'simple'), imp: mk(impGeo[k], impMat[k], false, 'imp') };
  }
  const at = new THREE.Vector3(1e9, 0, 0);
  let B = BUDGET[2], mult = 1, style = 'flat', counts = {};
  const api = {
    bakeMs: Math.round(performance.now() - t0),
    get style() { return style; },
    setStyle(s) {
      style = s;
      for (const [k, G] of Object.entries(groups)) {
        const leafy = s === 'fluffy' && k !== 'pine';
        G.full.geometry = s === 'flat' ? full[k] : s === 'smooth' ? smoothFull[k] : fluffy[k];
        G.full.material = s === 'flat' ? matFlat : leafy ? matLeaf : matSmooth;
        G.full.customDepthMaterial = leafy ? depthLeaf : undefined;
        G.simple.geometry = s === 'flat' ? simple[k] : smoothSimple[k];
        G.simple.material = s === 'flat' ? matFlat : matSmooth;
      }
    },
    /** budget — 0…3 (BUDGET), m — множник дальності (DRAW.imp), maxFar — межа туману. */
    setBudget(budget, m = 1, maxFar = 2000) {
      const b = BUDGET[Math.max(0, Math.min(3, budget | 0))];
      B = { ...b, simple: b.simple * m, imp: Math.min(b.imp * m, maxFar) };
      B.full = Math.min(B.full, B.simple);
      mult = m;
      at.set(1e9, 0, 0);
    },
    get budget() { return B; },
    /** Кожен кадр: перерозподіл інстансів, коли камера зрушила на 6 м (або force). */
    update(p, force) {
      if (!force && (p.x - at.x) ** 2 + (p.z - at.z) ** 2 < 36) return;
      at.copy(p);
      counts = {};
      for (const [k, G] of Object.entries(groups)) {
        let nf = 0, ns = 0, ni = 0;
        for (const t of G.items) {
          const dx = t.x - p.x, dz = t.z - p.z, d = Math.sqrt(dx * dx + dz * dz);
          let im, i;
          if (d < B.full) { im = G.full; i = nf++; } else if (d < B.simple) { im = G.simple; i = ns++; } else if (d < B.imp) { im = G.imp; i = ni++; } else continue;
          tmp.position.set(t.x, t.y, t.z);
          tmp.rotation.set(0, im === G.imp ? Math.atan2(-dx, -dz) : t.rot, 0);
          tmp.scale.setScalar(t.s); tmp.updateMatrix();
          im.setMatrixAt(i, tmp.matrix);
          im.setColorAt(i, im === G.imp ? cc.copy(t.col).multiply(inv) : t.col);
        }
        for (const [im, n] of [[G.full, nf], [G.simple, ns], [G.imp, ni]]) {
          im.count = n; im.visible = n > 0; im.instanceMatrix.needsUpdate = true; if (im.instanceColor) im.instanceColor.needsUpdate = true;
        }
        counts[k] = [nf, ns, ni];
      }
    },
    /** Після відновлення WebGL-контексту — текстури імпостерів знову. */
    rebake() { bakeImpostors(); },
    info() { return { trees: list.length, style, budget: B.name, mult, counts }; },
  };
  return api;
}

// ---------------------------------------------------------------- трава біля камери (проба 3: scene.js grassTufts)
/**
 * Кущики трави (три схрещені трикутники) у колі R м довкола камери: світова сітка клітин 16 м, у кожній — ті самі
 * кущики (хеш клітини), лише на узбіччях (до 18 м від дороги); на самій дорозі й у дворах — нема. Одна InstancedMesh,
 * перерозподіл — коли камера зрушила на 8 м. level: { ground(x, z), offRoad(x, z, pad), nearRoad(x, z), places }.
 */
export function makeGrass(THREE, scene, level, patch, max = 9000) {
  const g = new THREE.BufferGeometry(), pos = [], col = [];
  const r0 = rng(5);
  for (let k = 0; k < 3; k++) {
    const a = k * Math.PI / 3, c = Math.cos(a) * 0.22, s = Math.sin(a) * 0.22, lean = (r0() - 0.5) * 0.2;
    pos.push(-c * 0.7, 0, -s * 0.7, c * 0.7, 0, s * 0.7, lean, 0.3 + r0() * 0.12, lean);
    col.push(0.7, 0.7, 0.7, 0.7, 0.7, 0.7, 1.0, 1.0, 0.95);
  }
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
  g.computeVertexNormals();
  const n = g.attributes.normal; for (let i = 0; i < n.count; i++) n.setXYZ(i, 0, 1, 0);   // світло як на землі — без чорних боків
  const mat = patch(new THREE.MeshStandardMaterial({ vertexColors: true, side: THREE.DoubleSide, roughness: 0.95 }), 'grass');
  const im = new THREE.InstancedMesh(g, mat, max);
  im.count = 0; im.frustumCulled = false; im.receiveShadow = true; im.name = 'grass';
  im.setColorAt(0, new THREE.Color('#ffffff'));
  scene.add(im);
  const C = 16, cells = new Map(), tmp = new THREE.Object3D(), cc = new THREE.Color();
  const tones = [PAL.verge, PAL.grass, PAL.grassDry, PAL.meadow];
  const yards = (level.places || []).map((p) => p.yard).filter(Boolean);
  const inYard = (x, z) => yards.some((y) => Math.abs(x - y.x) < y.w / 2 + 2 && Math.abs(z - y.z) < y.d / 2 + 2);
  function cell(ix, iz) {
    const key = ix + ':' + iz;
    let c = cells.get(key);
    if (c) return c;
    c = [];
    const r = rng((ix * 73856093) ^ (iz * 19349663) ^ 0x5bd1e995);
    for (let i = 0; i < 30; i++) {
      const x = (ix + r()) * C, z = (iz + r()) * C;
      const d = level.nearRoad(x, z), verge = d < 18;
      if (!verge) continue;                                   // лише вздовж доріг: у полях колір кущика не збігався б із полем
      if (!level.offRoad(x, z, 0.6) || inYard(x, z)) continue;
      const s = 0.7 + r() * 0.8;
      tmp.position.set(x, level.ground(x, z), z); tmp.rotation.set(0, r() * 6.28, 0); tmp.scale.setScalar(verge ? s : s * 1.2); tmp.updateMatrix();
      c.push(tmp.matrix.clone(), cc.set(tones[Math.floor(r() * tones.length)]).multiplyScalar(1.0 + r() * 0.15).clone());
    }
    cells.set(key, c);
    if (cells.size > 4000) cells.delete(cells.keys().next().value);
    return c;
  }
  const at = new THREE.Vector3(1e9, 0, 0);
  let R = 70;
  return {
    mesh: im,
    setRadius(r) { R = r; at.set(1e9, 0, 0); im.visible = r > 0; if (!r) im.count = 0; },
    update(p, force) {
      if (!R || (!force && (p.x - at.x) ** 2 + (p.z - at.z) ** 2 < 64)) return;
      at.copy(p);
      let k = 0;
      const c0x = Math.floor((p.x - R) / C), c1x = Math.floor((p.x + R) / C), c0z = Math.floor((p.z - R) / C), c1z = Math.floor((p.z + R) / C);
      for (let ix = c0x; ix <= c1x && k < max; ix++) for (let iz = c0z; iz <= c1z && k < max; iz++) {
        const cx = (ix + 0.5) * C - p.x, cz = (iz + 0.5) * C - p.z;
        if (cx * cx + cz * cz > (R + C) * (R + C)) continue;
        const list = cell(ix, iz);
        for (let i = 0; i < list.length && k < max; i += 2) {
          const e = list[i].elements, dx = e[12] - p.x, dz = e[14] - p.z;
          if (dx * dx + dz * dz > R * R) continue;
          im.setMatrixAt(k, list[i]); im.setColorAt(k, list[i + 1]); k++;
        }
      }
      im.count = k; im.instanceMatrix.needsUpdate = true; if (im.instanceColor) im.instanceColor.needsUpdate = true;
    },
    info() { return { tufts: im.count, R, cells: cells.size }; },
  };
}
