// «Хутори» — 3D-сцена поля («дерев'яна настолка»). API — рівно контракт §10 (docs/games/specs/khutory.md):
//   const sc = await createScene(host, { onPick, onHover, quality: 'high'|'low', reducedMotion });
//   sc.setBoard(board); sc.setState(view); sc.setLegal({ kind, v, e, h }); await sc.roll(a, b); await sc.produce(gains);
//   sc.pop(kind, id); sc.screenPos(kind, id); sc.resetView(); sc.resize(); sc.dispose();
// Сцена не знає правил і не шле дій — лише малює й каже, куди клацнули (лише по підсвіченому).
//
// Будова: усе нерухоме (плитки, жителі без руху, жетони, пристані, рамка) — ОДИН злитий меш із кольорами вершин;
// однакові рухомі речі (дерева, вівці, колоски, дим, фігури, підсвітка) — InstancedMesh; море хвилюється у вершинному
// шейдері. Разом ~30 викликів малювання + тіні. Клік — без raycast по мешах: найближче підсвічене місце в пікселях
// екрана (великий «хітбокс» для пальця), для гексів — перетин променя з площиною поля.

import * as THREE from '/static/vendor/three-0.186.1/three.module.js';
import * as M from './models.js';
import { SEAT_COLOR, RES_COLOR } from './consts.js';

const D2R = Math.PI / 180;
const DEF_POLAR = 50 * D2R, POLAR_MIN = 35 * D2R, POLAR_MAX = 70 * D2R, AZ_LIM = 70 * D2R;
const ZOOM_MIN = 0.65, ZOOM_MAX = 1.6;
const S3 = Math.sqrt(3);
const UP = new THREE.Vector3(0, 1, 0);
const BG = '#2b1f16';
const PIECE_S = 1.3;   // фігури трохи більші за «правдиві» — щоб читались здалеку й на телефоні

const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
const easeOut = (k) => 1 - Math.pow(1 - k, 3);
const easeInOut = (k) => k < 0.5 ? 4 * k * k * k : 1 - Math.pow(-2 * k + 2, 3) / 2;
function backOut(k) { const c1 = 2.2, c3 = c1 + 1; return 1 + c3 * Math.pow(k - 1, 3) + c1 * Math.pow(k - 1, 2); }

export async function createScene(host, opts = {}) {
  const quality = opts.quality === 'low' ? 'low' : 'high';
  const reduced = !!opts.reducedMotion;
  const bgAnim = quality === 'high' && !reduced;
  const onPick = typeof opts.onPick === 'function' ? opts.onPick : () => {};
  const onHover = typeof opts.onHover === 'function' ? opts.onHover : () => {};

  // ------------------------------------------------------------------------------------------------ рендерер
  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({ antialias: quality === 'high', powerPreference: 'high-performance' });
  } catch (e) {
    throw new Error('WebGL недоступний: ' + (e && e.message || e));
  }
  if (!renderer.getContext()) { renderer.dispose(); throw new Error('WebGL недоступний'); }
  renderer.setPixelRatio(quality === 'low' ? Math.min(1, window.devicePixelRatio || 1) : Math.min(2, window.devicePixelRatio || 1));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.NeutralToneMapping;
  renderer.toneMappingExposure = 1.05;
  renderer.shadowMap.enabled = quality === 'high';
  renderer.shadowMap.type = THREE.PCFShadowMap;
  const canvas = renderer.domElement;
  canvas.style.cssText = 'display:block;width:100%;height:100%;touch-action:none;outline:none;-webkit-tap-highlight-color:transparent;user-select:none';
  host.appendChild(canvas);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(BG);
  scene.fog = new THREE.Fog(BG, 20, 45);
  const cam = new THREE.PerspectiveCamera(34, 1, 0.1, 120);

  scene.add(new THREE.HemisphereLight('#fff1dc', '#5a4030', 1.35));
  const sun = new THREE.DirectionalLight('#ffe6c0', 2.5);
  sun.position.set(-5.5, 11, 6);
  sun.castShadow = quality === 'high';
  if (sun.castShadow) {
    sun.shadow.mapSize.set(2048, 2048);
    Object.assign(sun.shadow.camera, { left: -7.5, right: 7.5, top: 7.5, bottom: -7.5, near: 1, far: 30 });
    sun.shadow.bias = -0.0005; sun.shadow.normalBias = 0.02;
  }
  scene.add(sun); scene.add(sun.target);

  const matVC = new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, roughness: 0.88, metalness: 0 });
  const table = new THREE.Mesh(M.tableGeo(), new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 0.92 }));
  table.receiveShadow = true; table.matrixAutoUpdate = false;
  scene.add(table);

  const seaTime = { value: 0 };
  const seaMat = new THREE.MeshStandardMaterial({ vertexColors: true, flatShading: true, roughness: 0.35, metalness: 0.05 });
  if (bgAnim) {
    seaMat.onBeforeCompile = (sh) => {
      sh.uniforms.uTime = seaTime;
      sh.vertexShader = 'uniform float uTime;\n' + sh.vertexShader.replace('#include <begin_vertex>',
        '#include <begin_vertex>\n transformed.y += sin(position.x * 1.9 + uTime * 1.1) * 0.012 + sin(position.z * 2.4 - uTime * 0.85 + position.x * 0.7) * 0.01;');
    };
  }

  const atlas = M.makeAtlas();
  const tokenMat = new THREE.MeshStandardMaterial({ map: atlas, roughness: 0.75, polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -1 });

  // ------------------------------------------------------------------------------------------------ інстанси
  function inst(geo, cap, { color = false, shadow = true, mat = matVC } = {}) {
    const m = new THREE.InstancedMesh(geo, mat, cap);
    m.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
    m.frustumCulled = false;
    m.castShadow = shadow && quality === 'high';
    m.receiveShadow = quality === 'high';
    if (color) { const w = new THREE.Color(1, 1, 1); for (let i = 0; i < cap; i++) m.setColorAt(i, w); }
    m.count = 0;
    m.visible = false;
    scene.add(m);
    return m;
  }
  const townWalls = inst(M.houseWallsGeo(false), 24);
  const townRoof = inst(M.roofGeo(false), 24, { color: true });
  const cg = M.cityGeo();
  const cityWalls = inst(cg.walls, 20);
  const cityColor = inst(cg.color, 20, { color: true });
  const roads = inst(M.roadGeo(), 72, { color: true });
  const chips = inst(M.chipGeo(), 60, { color: true, shadow: false });

  const hlMat = new THREE.MeshBasicMaterial({ vertexColors: true, transparent: true, opacity: 0.95, depthWrite: false, toneMapped: false });
  const hlRings = inst(M.ringGeo(), 54, { shadow: false, mat: hlMat });
  const hlStrips = inst(M.stripGeo(), 72, { shadow: false, mat: hlMat });
  const hlHex = inst(M.hexRingGeo(), 19, { shadow: false, mat: hlMat });
  for (const m of [hlRings, hlStrips, hlHex]) { m.receiveShadow = false; m.renderOrder = 5; }

  const robber = new THREE.Mesh(M.robberGeo(), matVC);
  robber.castShadow = quality === 'high'; robber.visible = false;
  scene.add(robber);

  const dieTex = M.dieTexture();
  const dieMat = new THREE.MeshStandardMaterial({ map: dieTex, roughness: 0.45 });
  const dice = [0, 1].map(() => {
    const d = new THREE.Mesh(M.dieGeo(), dieMat);
    d.castShadow = quality === 'high'; d.visible = false;
    scene.add(d);
    return d;
  });

  // Привид фігури під курсором (у моєму кольорі) — щоб було видно, що саме поставиться.
  const ghostWhite = new THREE.MeshStandardMaterial({ vertexColors: true, transparent: true, opacity: 0.6, depthWrite: false, flatShading: true });
  const ghostCol = new THREE.MeshStandardMaterial({ vertexColors: true, transparent: true, opacity: 0.7, depthWrite: false, flatShading: true });
  const ghosts = {
    town: grp(new THREE.Mesh(M.houseWallsGeo(false), ghostWhite), new THREE.Mesh(M.roofGeo(false), ghostCol)),
    city: grp(new THREE.Mesh(cg.walls.clone(), ghostWhite), new THREE.Mesh(cg.color.clone(), ghostCol)),
    road: grp(new THREE.Mesh(M.roadGeo(), ghostCol)),
  };
  function grp(...ms) { const g = new THREE.Group(); ms.forEach((m) => { m.renderOrder = 6; g.add(m); }); g.visible = false; scene.add(g); return g; }

  // ------------------------------------------------------------------------------------------------ поле
  let boardId = null, board = null;
  let boardGroup = null;
  let hexPos = [], vxPos = [], edgeInfo = [];
  let trees = null, sheep = null, wheat = null, smoke = null;
  let bgData = null;

  function disposeBoard() {
    if (!boardGroup) return;
    boardGroup.traverse((o) => {
      if (o.geometry && !o.isSprite) o.geometry.dispose();   // у спрайтів геометрія спільна на весь three
      if (o.isSprite) { o.material.map?.dispose(); o.material.dispose(); }
    });
    scene.remove(boardGroup);
    boardGroup = null; trees = sheep = wheat = smoke = null; bgData = null;
  }

  function setBoard(b) {
    if (!b || !Array.isArray(b.hexes)) return;
    if (b.id != null && b.id === boardId) return;
    disposeBoard();
    board = b; boardId = b.id;
    const seed = M.hashStr(b.id ?? 'x');
    hexPos = b.hexes.map((h) => new THREE.Vector3(S3 * (h.q + h.r / 2), M.TILE_H, 1.5 * h.r));
    vxPos = b.vx.map(([x, y]) => new THREE.Vector3(x, M.TILE_H, y));
    edgeInfo = b.edges.map(([a, c]) => {
      const A = vxPos[a], C = vxPos[c];
      return { mid: A.clone().add(C).multiplyScalar(0.5), ry: -Math.atan2(C.z - A.z, C.x - A.x), a, c };
    });

    boardGroup = new THREE.Group();
    const sb = new M.Builder(), tb = new M.Builder(true);
    const out = { oak: [], spruce: [], sheep: [], wheat: [], smoke: [] };
    b.hexes.forEach((h, i) => {
      const p = hexPos[i], rng = M.rngOf(seed + i * 977);
      M.addShore(sb, p.x, p.z, rng);
      M.addTile(sb, p.x, p.z, h.t, rng);
      M.decorate(sb, h.t, p.x, p.z, rng, out);
      if (h.num != null) { M.addTokenBody(sb, p.x, p.z, rng); M.addTokenTop(tb, p.x, p.z, h.num); }
    });
    M.addFrame(sb);
    const signs = [];
    (b.ports || []).forEach((pt, i) => {
      const e = b.edges[pt.e]; if (!e) return;
      const A = b.vx[e[0]], C = b.vx[e[1]];
      const at = M.addPort(sb, A, C, M.rngOf(seed + 5000 + i));
      signs.push([at, pt.kind]);
    });
    const stat = new THREE.Mesh(sb.geometry(), matVC);
    stat.castShadow = stat.receiveShadow = quality === 'high';
    stat.matrixAutoUpdate = false;
    boardGroup.add(stat);
    if (!tb.empty) {
      const tops = new THREE.Mesh(tb.geometry(), tokenMat);
      tops.receiveShadow = quality === 'high'; tops.matrixAutoUpdate = false;
      boardGroup.add(tops);
    }
    const sea = new THREE.Mesh(M.seaGeo(hexPos.map((p) => [p.x, p.z])), seaMat);
    sea.receiveShadow = quality === 'high'; sea.matrixAutoUpdate = false;
    boardGroup.add(sea);
    for (const [[x, y, z], kind] of signs) {
      const t = atlas.clone();
      const [u0, v0, su, sv] = M.atlasCell(M.portCell(kind));
      t.offset.set(u0 + 0.004, v0 + 0.032); t.repeat.set(su - 0.008, sv - 0.064);
      const sp = new THREE.Sprite(new THREE.SpriteMaterial({ map: t, alphaTest: 0.2, fog: false }));
      sp.center.set(0.5, 0.1);
      sp.position.set(x, y - 0.1, z);
      sp.scale.set(0.56, 0.43, 1);
      boardGroup.add(sp);
    }

    // Рухомі жителі.
    const mk = (geo, list, shadow) => {
      if (!list.length) { geo.dispose(); return null; }
      const m = new THREE.InstancedMesh(geo, matVC, list.length);
      m.frustumCulled = false;
      m.castShadow = shadow && quality === 'high'; m.receiveShadow = quality === 'high';
      if (bgAnim) m.instanceMatrix.setUsage(THREE.DynamicDrawUsage);
      boardGroup.add(m);
      return m;
    };
    trees = { oak: mk(M.oakGeo(), out.oak, true), spruce: mk(M.spruceGeo(), out.spruce, true) };
    sheep = mk(M.sheepGeo(), out.sheep, true);
    wheat = mk(M.wheatGeo(), out.wheat, false);
    smoke = mk(M.puffGeo(), out.smoke.flatMap((s) => [s, s, s, s]), false);
    bgData = out;
    scene.add(boardGroup);
    animBg(0, true);
    fit();
    placePieces(); placeRobber(true); rebuildLegal();
    dirty = true;
  }

  // ------------------------------------------------------------------------------------------------ фонова анімація
  const _m4 = new THREE.Matrix4(), _q = new THREE.Quaternion(), _eu = new THREE.Euler(), _pv = new THREE.Vector3(), _sv = new THREE.Vector3();
  function setM(mesh, i, x, y, z, rx, ry, rz, s, sy = s) {
    _eu.set(rx, ry, rz, 'YXZ');
    mesh.setMatrixAt(i, _m4.compose(_pv.set(x, y, z), _q.setFromEuler(_eu), _sv.set(s, sy, s)));
  }
  function animBg(t, force = false) {
    if (!bgData || (!bgAnim && !force)) return;
    const H = M.TILE_H;
    for (const k of ['oak', 'spruce']) {
      const m = trees[k]; if (!m) continue;
      bgData[k].forEach((o, i) => setM(m, i, o.x, H, o.z, 0.035 * Math.sin(t * 1.2 + o.ph), o.ry, 0.03 * Math.sin(t * 0.9 + o.ph * 1.7), o.s));
      m.instanceMatrix.needsUpdate = true;
    }
    if (sheep) {
      bgData.sheep.forEach((o, i) => {
        const a = o.ph + o.sp * t + 0.5 * Math.sin(t * 0.25 + o.ph);
        const da = o.sp + 0.125 * Math.cos(t * 0.25 + o.ph);       // > 0: вівця завжди йде вперед (або пасеться)
        const x = o.x + o.rx * Math.cos(a), z = o.z + o.rz * Math.sin(a);
        const head = Math.atan2(-(o.rz * Math.cos(a)), -o.rx * Math.sin(a));
        const bob = Math.abs(Math.sin(t * 5 + o.ph)) * 0.006 * Math.min(1, da * 6);
        setM(sheep, i, x, H + bob, z, 0, head, 0, 1.15);
      });
      sheep.instanceMatrix.needsUpdate = true;
    }
    if (wheat) {
      bgData.wheat.forEach((o, i) => {
        const w = 0.1 * Math.sin(t * 1.6 - o.x * 1.3 - o.z * 0.5) + 0.04;
        setM(wheat, i, o.x, H, o.z, 0, o.ry, 0, o.s);
        // нахил «за вітром» (вітер уздовж +x): довертаємо матрицю навколо осі z у світі
        _m4.makeRotationZ(-w);
        wheat.getMatrixAt(i, _mw); _mw.premultiply(_mT.makeTranslation(-o.x, -H, -o.z)).premultiply(_m4).premultiply(_mT.makeTranslation(o.x, H, o.z));
        wheat.setMatrixAt(i, _mw);
      });
      wheat.instanceMatrix.needsUpdate = true;
    }
    if (smoke) {
      bgData.smoke.forEach((o, j) => {
        for (let k = 0; k < 4; k++) {
          const life = bgAnim ? ((t * 0.28 + k / 4) % 1) : 0.15 + k * 0.2;
          let s = 0.03 + life * 0.06;
          if (life > 0.7) s *= (1 - life) / 0.3;
          if (life < 0.08) s *= life / 0.08;
          setM(smoke, j * 4 + k, o.x + life * 0.14 + Math.sin(life * 6 + j) * 0.02, o.y + life * 0.55, o.z - life * 0.05, life * 2, life * 3, 0, s);
        }
      });
      smoke.instanceMatrix.needsUpdate = true;
    }
    seaTime.value = t;
  }
  const _mw = new THREE.Matrix4(), _mT = new THREE.Matrix4();

  // ------------------------------------------------------------------------------------------------ фігури
  let view = null;
  const seatColor = new Map();
  let myColor = new THREE.Color(SEAT_COLOR[0]);
  const popT = new Map();   // 'v12' / 'e30' → { t, seen }
  function colorOf(seat) { return seatColor.get(seat) ?? new THREE.Color(SEAT_COLOR[seat % 4] ?? '#888888'); }

  function placePieces(now = performance.now()) {
    let anyPop = false;
    const popS = (key) => {
      const p = popT.get(key);
      if (!p) return [1, 0];
      if (!p.seen) { p.seen = true; p.t = now; }
      const k = (now - p.t) / 520;
      if (k >= 1) { popT.delete(key); return [1, 0]; }
      anyPop = true;
      return [Math.max(0.001, backOut(clamp(k, 0, 1))), (1 - easeOut(clamp(k, 0, 1))) * 0.35];
    };
    let nt = 0, nc = 0, nr = 0;
    if (view && board) {
      for (const t of view.towns || []) {
        const P = vxPos[t.v]; if (!P) continue;
        const [s, dy] = popS('v' + t.v);
        const ry = ((t.v * 37) % 9 - 4) * 0.045;
        const c = colorOf(t.seat);
        if (t.city) {
          if (nc >= cityWalls.instanceMatrix.count) continue;
          setM(cityWalls, nc, P.x, P.y + dy, P.z, 0, ry, 0, s * PIECE_S); setM(cityColor, nc, P.x, P.y + dy, P.z, 0, ry, 0, s * PIECE_S);
          cityColor.setColorAt(nc, c); nc++;
        } else {
          if (nt >= townWalls.instanceMatrix.count) continue;
          setM(townWalls, nt, P.x, P.y + dy, P.z, 0, ry, 0, s * PIECE_S); setM(townRoof, nt, P.x, P.y + dy, P.z, 0, ry, 0, s * PIECE_S);
          townRoof.setColorAt(nt, c); nt++;
        }
      }
      for (const r of view.roads || []) {
        const E = edgeInfo[r.e]; if (!E || nr >= roads.instanceMatrix.count) continue;
        const [s, dy] = popS('e' + r.e);
        setM(roads, nr, E.mid.x, E.mid.y + dy - 0.005, E.mid.z, 0, E.ry, 0, s, s * 1.15);
        roads.getMatrixAt(nr, _mw); _mw.scale(_sv.set(1, 1, 1.25)); roads.setMatrixAt(nr, _mw);
        roads.setColorAt(nr, colorOf(r.seat)); nr++;
      }
    }
    for (const [m, n] of [[townWalls, nt], [townRoof, nt], [cityWalls, nc], [cityColor, nc], [roads, nr]]) {
      m.count = n; m.visible = n > 0;
      m.instanceMatrix.needsUpdate = true;
      if (m.instanceColor) m.instanceColor.needsUpdate = true;
    }
    for (const [k, p] of popT) if (p.seen === false && now - p.t > 3000) popT.delete(k);
    return anyPop;
  }

  // Кармелюк.
  let robberHex = null, robberAnim = null;
  function robberSpot(h) {
    const p = hexPos[h];
    return p ? new THREE.Vector3(p.x + M.ROBBER_SPOT[0], M.TILE_H, p.z + M.ROBBER_SPOT[1]) : null;
  }
  function placeRobber(instant) {
    const h = view?.robber;
    if (h == null || !board || !hexPos[h]) { robber.visible = false; robberHex = null; return; }
    const to = robberSpot(h);
    if (robberHex == null || instant || reduced || !robber.visible) {
      robber.position.copy(to); robber.rotation.set(0, 0.3, 0); robber.visible = true; robberHex = h; return;
    }
    if (h === robberHex) return;
    const from = robber.position.clone();
    robberHex = h;
    if (robberAnim) robberAnim.cancel();
    robberAnim = tween(650, (k) => {
      const e = easeInOut(k);
      robber.position.lerpVectors(from, to, e);
      robber.position.y = M.TILE_H + Math.sin(Math.PI * k) * (0.9 + from.distanceTo(to) * 0.12);
      robber.rotation.set(0, 0.3 + e * Math.PI * 2, 0);
    });
  }

  function setState(v) {
    if (!v) return;
    if (v.board) setBoard(v.board);
    view = v;
    seatColor.clear();
    for (const p of v.players || []) seatColor.set(p.seat, new THREE.Color(SEAT_COLOR[p.color] ?? SEAT_COLOR[p.seat % 4]));
    const ms = v.me?.seat;
    myColor = ms != null ? colorOf(ms) : new THREE.Color('#f4e7c5');
    ghostCol.color.copy(myColor);
    placePieces();
    placeRobber(false);
    if (!rolling) {
      if (Array.isArray(v.dice) && v.dice.length === 2) {
        if (!dice[0].visible || shownDice[0] !== v.dice[0] || shownDice[1] !== v.dice[1]) restDice(v.dice[0], v.dice[1]);
      } else { dice[0].visible = dice[1].visible = false; shownDice = [0, 0]; }
    }
    dirty = true;
  }

  // ------------------------------------------------------------------------------------------------ підсвітка
  let legal = { kind: null, v: [], e: [], h: [] };
  let hover = null;   // { kind: 'v'|'e'|'h', id, i }
  function setLegal(l) {
    legal = { kind: l?.kind ?? null, v: (l?.v || []).slice(), e: (l?.e || []).slice(), h: (l?.h || []).slice() };
    if (!legal.kind) legal.v = legal.e = legal.h = [];
    setHover(null);
    rebuildLegal();
    dirty = true;
  }
  function pickKind() { return legal.kind === 'road' ? 'e' : legal.kind === 'robber' ? 'h' : legal.kind ? 'v' : null; }
  function legalList() { const k = pickKind(); return k === 'v' ? legal.v : k === 'e' ? legal.e : k === 'h' ? legal.h : []; }
  function rebuildLegal(t = clockT) {
    const k = pickKind(), list = board ? legalList() : [];
    const pulse = reduced ? 1 : 1 + 0.08 * Math.sin(t * 4.2);
    let n = 0;
    for (const m of [hlRings, hlStrips, hlHex]) { m.count = 0; m.visible = false; }
    if (!k || !list.length) return;
    list.forEach((id, i) => {
      const hov = hover && hover.id === id;
      const s = (hov ? 1.35 : 1) * (reduced ? 1 : 1 + 0.08 * Math.sin(t * 4.2 + i * 0.6));
      if (k === 'v') {
        const P = vxPos[id]; if (!P) return;
        const big = legal.kind === 'city' ? 1.75 : 1;
        setM(hlRings, n++, P.x, P.y + 0.014, P.z, 0, 0, 0, s * big, 1);
      } else if (k === 'e') {
        const E = edgeInfo[id]; if (!E) return;
        setM(hlStrips, n++, E.mid.x, E.mid.y + 0.012, E.mid.z, 0, E.ry, 0, hov ? 1.15 : 1, 1);
        if (!reduced) { hlStrips.getMatrixAt(n - 1, _mw); _mw.scale(_sv.set(1, 1, s)); hlStrips.setMatrixAt(n - 1, _mw); }
      } else {
        const P = hexPos[id]; if (!P) return;
        setM(hlHex, n++, P.x, P.y + 0.012, P.z, 0, 0, 0, (hov ? 1.04 : 1) * (reduced ? 1 : 1 + 0.02 * Math.sin(t * 4.2)), 1);
      }
    });
    const m = k === 'v' ? hlRings : k === 'e' ? hlStrips : hlHex;
    m.count = n; m.visible = n > 0; m.instanceMatrix.needsUpdate = true;
    hlMat.opacity = reduced ? 0.95 : 0.78 + 0.2 * (pulse - 0.92) / 0.16;
  }
  function setHover(h) {
    const same = (h?.id ?? null) === (hover?.id ?? null) && (h?.kind ?? null) === (hover?.kind ?? null);
    hover = h;
    for (const g of Object.values(ghosts)) g.visible = false;
    if (h && lastPointerType === 'mouse') {
      const g = legal.kind === 'road' ? ghosts.road : legal.kind === 'town' ? ghosts.town : legal.kind === 'city' ? ghosts.city : null;
      if (g) {
        if (h.kind === 'e') { const E = edgeInfo[h.id]; g.position.set(E.mid.x, E.mid.y + 0.03, E.mid.z); g.rotation.set(0, E.ry, 0); }
        else { const P = vxPos[h.id]; g.position.set(P.x, P.y + 0.04, P.z); g.rotation.set(0, ((h.id * 37) % 9 - 4) * 0.045, 0); }
        if (h.kind === 'e') g.scale.set(1, 1.15, 1.25); else g.scale.setScalar(PIECE_S);
        g.visible = true;
      }
    }
    canvas.style.cursor = h ? 'pointer' : '';
    if (!same) { rebuildLegal(); onHover(h ? h.kind : null, h ? h.id : null); }
    dirty = true;
  }

  // ------------------------------------------------------------------------------------------------ камера
  const cs = { az: 0, pol: DEF_POLAR, zoom: 1, target: new THREE.Vector3(0, 0, 0.25) };
  const vel = { az: 0, pol: 0 };
  let baseDist = 14, W = 1, H = 1;
  function applyCam() {
    const d = baseDist / cs.zoom, sp = Math.sin(cs.pol);
    cam.position.set(cs.target.x + d * sp * Math.sin(cs.az), cs.target.y + d * Math.cos(cs.pol), cs.target.z + d * sp * Math.cos(cs.az));
    cam.lookAt(cs.target);
    cam.updateMatrixWorld();
    scene.fog.near = d + 4; scene.fog.far = d + 26;
  }
  const _pj = new THREE.Vector3();
  function fit() {
    const save = { az: cs.az, pol: cs.pol, zoom: cs.zoom, t: cs.target.clone() };
    cs.az = 0; cs.pol = DEF_POLAR; cs.zoom = 1; cs.target.set(0, 0, 0);
    const R = M.SEA_R + M.FRAME_W, pts = [];
    for (let i = 0; i < 6; i++) for (const y of [0, 0.25]) pts.push(new THREE.Vector3(R * Math.cos(i * Math.PI / 3), y, R * Math.sin(i * Math.PI / 3)));
    const lim = W / H < 0.8 ? 0.97 : 0.93;
    // Відстань — двійковим пошуком, потім зсув цілі вглиб/до себе, щоб рамка стояла посередині по вертикалі; двічі.
    let tz = 0;
    for (let pass = 0; pass < 3; pass++) {
      cs.target.set(0, 0, tz);
      let lo = 3, hi = 90;
      for (let it = 0; it < 30; it++) {
        baseDist = (lo + hi) / 2; applyCam();
        let ok = true;
        for (const p of pts) { _pj.copy(p).project(cam); if (Math.abs(_pj.x) > lim || Math.abs(_pj.y) > lim) { ok = false; break; } }
        if (ok) hi = baseDist; else lo = baseDist;
      }
      baseDist = hi; applyCam();
      let y0 = 1, y1 = -1;
      for (const p of pts) { _pj.copy(p).project(cam); y0 = Math.min(y0, _pj.y); y1 = Math.max(y1, _pj.y); }
      tz -= (y0 + y1) / 2 * baseDist * Math.tan(cam.fov * D2R / 2) / Math.cos(DEF_POLAR);
    }
    defTargetZ = tz;
    diceRest = computeRest();
    cs.az = save.az; cs.pol = save.pol; cs.zoom = save.zoom; cs.target.copy(save.t);
    if (!touchedView) cs.target.set(0, 0, defTargetZ);
    applyCam();
    dirty = true;
  }
  let defTargetZ = 0, touchedView = false;
  let resetAnim = null;
  function resetView() {
    touchedView = false;
    vel.az = vel.pol = 0;
    if (resetAnim) resetAnim.cancel();
    const from = { az: cs.az, pol: cs.pol, zoom: cs.zoom, t: cs.target.clone() };
    const to = { az: 0, pol: DEF_POLAR, zoom: 1, t: new THREE.Vector3(0, 0, defTargetZ) };
    const step = (k) => {
      const e = easeInOut(k);
      cs.az = from.az + (to.az - from.az) * e; cs.pol = from.pol + (to.pol - from.pol) * e; cs.zoom = from.zoom + (to.zoom - from.zoom) * e;
      cs.target.lerpVectors(from.t, to.t, e);
      applyCam();
    };
    if (reduced) { step(1); dirty = true; return; }
    resetAnim = tween(450, step);
  }

  function resize() {
    const w = Math.max(1, host.clientWidth | 0), h = Math.max(1, host.clientHeight | 0);
    if (w === W && h === H && renderer.getSize(new THREE.Vector2()).x === w) return;
    W = w; H = h;
    renderer.setSize(w, h, false);
    cam.aspect = w / h; cam.updateProjectionMatrix();
    fit();
  }

  // ------------------------------------------------------------------------------------------------ влучання
  function project(v, out = {}) {
    _pj.copy(v).project(cam);
    out.x = (_pj.x + 1) / 2 * W; out.y = (1 - _pj.y) / 2 * H; out.z = _pj.z;
    return out;
  }
  function pxPerUnit() {
    const a = project(new THREE.Vector3(cs.target.x, M.TILE_H, cs.target.z)), b = project(new THREE.Vector3(cs.target.x + 1, M.TILE_H, cs.target.z));
    return Math.hypot(a.x - b.x, a.y - b.y) || 40;
  }
  const _ray = new THREE.Raycaster(), _plane = new THREE.Plane(new THREE.Vector3(0, 1, 0), -M.TILE_H), _hitP = new THREE.Vector3();
  function hitAt(x, y, touch) {
    const k = pickKind(); if (!k || !board) return null;
    const list = legalList(); if (!list.length) return null;
    const ppu = pxPerUnit();
    let best = null, bd = Infinity;
    if (k === 'v') {
      const thr = clamp(ppu * 0.45, 18, touch ? 48 : 32), tmp = new THREE.Vector3();
      for (const id of list) {
        const P = vxPos[id]; if (!P) continue;
        const s = project(tmp.copy(P).setY(P.y + 0.06));
        const d = Math.hypot(s.x - x, s.y - y);
        if (d < thr && d < bd) { bd = d; best = { kind: 'v', id }; }
      }
    } else if (k === 'e') {
      const thr = clamp(ppu * 0.32, 14, touch ? 42 : 26);
      for (const id of list) {
        const E = edgeInfo[id]; if (!E) continue;
        const a = project(vxPos[E.a]), b = project(vxPos[E.c]);
        const dx = b.x - a.x, dy = b.y - a.y, L2 = dx * dx + dy * dy || 1;
        const t = clamp(((x - a.x) * dx + (y - a.y) * dy) / L2, 0.12, 0.88);
        const d = Math.hypot(a.x + dx * t - x, a.y + dy * t - y);
        if (d < thr && d < bd) { bd = d; best = { kind: 'e', id }; }
      }
    } else {
      _ray.setFromCamera(new THREE.Vector2(x / W * 2 - 1, -(y / H) * 2 + 1), cam);
      if (_ray.ray.intersectPlane(_plane, _hitP)) {
        for (const id of list) {
          const P = hexPos[id]; if (!P) continue;
          const d = Math.hypot(P.x - _hitP.x, P.z - _hitP.z);
          if (d < 0.98 && d < bd) { bd = d; best = { kind: 'h', id }; }
        }
      }
    }
    return best;
  }

  function screenPos(kind, id) {
    let P = null;
    if (kind === 'v' || kind === 'town' || kind === 'city') P = vxPos[id] && vxPos[id].clone().setY(M.TILE_H + 0.12);
    else if (kind === 'e' || kind === 'road') P = edgeInfo[id] && edgeInfo[id].mid.clone().setY(M.TILE_H + 0.05);
    else if (kind === 'h' || kind === 'robber') P = hexPos[id] && hexPos[id].clone().setY(M.TILE_H + 0.06);
    if (!P) return null;
    const s = project(P);
    return { x: Math.round(s.x * 10) / 10, y: Math.round(s.y * 10) / 10 };
  }

  // ------------------------------------------------------------------------------------------------ вказівник
  const ptrs = new Map();
  let drag = null, pinch = null, lastPointerType = 'mouse', lastTap = 0;
  const local = (e) => { const r = canvas.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; };
  function onDown(e) {
    kick();
    lastPointerType = e.pointerType || 'mouse';
    try { canvas.setPointerCapture(e.pointerId); } catch { }
    const [x, y] = local(e);
    ptrs.set(e.pointerId, { x, y });
    vel.az = vel.pol = 0;
    if (resetAnim) { resetAnim.cancel(); resetAnim = null; }
    if (ptrs.size === 1) drag = { x0: x, y0: y, x, y, t0: performance.now(), moved: false, pan: e.button === 1 || e.button === 2 || e.shiftKey, lt: performance.now() };
    else if (ptrs.size === 2) {
      const [a, b] = [...ptrs.values()];
      pinch = { d0: Math.hypot(a.x - b.x, a.y - b.y) || 1, z0: cs.zoom, mx: (a.x + b.x) / 2, my: (a.y + b.y) / 2 };
      if (drag) drag.moved = true;
    }
  }
  function onMove(e) {
    kick();
    lastPointerType = e.pointerType || 'mouse';
    const [x, y] = local(e);
    if (!ptrs.has(e.pointerId)) {
      if (lastPointerType === 'mouse') setHover(hitAt(x, y, false));
      return;
    }
    ptrs.get(e.pointerId).x = x; ptrs.get(e.pointerId).y = y;
    if (ptrs.size >= 2 && pinch) {
      const [a, b] = [...ptrs.values()];
      const d = Math.hypot(a.x - b.x, a.y - b.y) || 1, mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
      cs.zoom = clamp(pinch.z0 * d / pinch.d0, ZOOM_MIN, ZOOM_MAX);
      panBy(mx - pinch.mx, my - pinch.my); pinch.mx = mx; pinch.my = my;
      touchedView = true; applyCam(); dirty = true;
      return;
    }
    if (!drag) return;
    const dx = x - drag.x, dy = y - drag.y;
    if (!drag.moved && Math.hypot(x - drag.x0, y - drag.y0) > (lastPointerType === 'touch' ? 10 : 5)) { drag.moved = true; setHover(null); }
    if (drag.moved) {
      const now = performance.now(), dt = Math.max(1, now - drag.lt);
      if (drag.pan) panBy(dx, dy);
      else {
        const da = -dx * 0.006, dp = -dy * 0.005;
        cs.az = clamp(cs.az + da, -AZ_LIM, AZ_LIM); cs.pol = clamp(cs.pol + dp, POLAR_MIN, POLAR_MAX);
        vel.az = 0.6 * vel.az + 0.4 * da / dt; vel.pol = 0.6 * vel.pol + 0.4 * dp / dt;
      }
      drag.lt = now;
      touchedView = true; applyCam(); dirty = true;
    }
    drag.x = x; drag.y = y;
  }
  function panBy(dx, dy) {
    const d = baseDist / cs.zoom, k = 2 * d * Math.tan(cam.fov * D2R / 2) / H;
    const right = new THREE.Vector3(Math.cos(cs.az), 0, -Math.sin(cs.az)), fwd = new THREE.Vector3(-Math.sin(cs.az), 0, -Math.cos(cs.az));
    cs.target.addScaledVector(right, -dx * k).addScaledVector(fwd, dy * k / Math.cos(cs.pol) * 0.6);
    const r = Math.hypot(cs.target.x, cs.target.z), R = 3.6;
    if (r > R) { cs.target.x *= R / r; cs.target.z *= R / r; }
  }
  function onUp(e) {
    kick();
    const p = ptrs.get(e.pointerId);
    ptrs.delete(e.pointerId);
    try { canvas.releasePointerCapture(e.pointerId); } catch { }
    if (ptrs.size < 2) pinch = null;
    if (ptrs.size === 1) { const [q] = [...ptrs.values()]; drag = { x0: q.x, y0: q.y, x: q.x, y: q.y, t0: performance.now(), moved: true, pan: false, lt: performance.now() }; return; }
    if (!drag || !p || e.type === 'pointercancel') { drag = null; return; }
    const d = drag; drag = null;
    if (d.moved) { if (performance.now() - d.lt > 80) vel.az = vel.pol = 0; return; }
    const touch = (e.pointerType || 'mouse') !== 'mouse';
    const hit = hitAt(p.x, p.y, touch);
    if (hit) { lastTap = 0; onPick(hit.kind, hit.id); return; }
    if (touch) {   // подвійний дотик по порожньому — як подвійний клік
      const now = performance.now();
      if (now - lastTap < 320) { lastTap = 0; resetView(); } else lastTap = now;
    }
  }
  function onWheel(e) {
    e.preventDefault(); kick();
    cs.zoom = clamp(cs.zoom * Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0015)), ZOOM_MIN, ZOOM_MAX);
    touchedView = true; applyCam(); dirty = true;
  }
  function onDbl(e) { e.preventDefault(); if (!hitAt(...local(e), false)) resetView(); }
  function onLeave() { if (!ptrs.size) { setHover(null); kick(); } }
  const noCtx = (e) => e.preventDefault();
  canvas.addEventListener('pointerdown', onDown);
  canvas.addEventListener('pointermove', onMove);
  canvas.addEventListener('pointerup', onUp);
  canvas.addEventListener('pointercancel', onUp);
  canvas.addEventListener('pointerleave', onLeave);
  canvas.addEventListener('wheel', onWheel, { passive: false });
  canvas.addEventListener('dblclick', onDbl);
  canvas.addEventListener('contextmenu', noCtx);

  // ------------------------------------------------------------------------------------------------ анімації
  const tweens = new Set();
  function tween(dur, fn) {
    let resolve;
    const p = new Promise((r) => { resolve = r; });
    const tw = { t0: performance.now(), dur, fn, done: false, resolve, promise: p,
      cancel() { if (tw.done) return; tw.done = true; tweens.delete(tw); resolve(); } };
    tweens.add(tw);
    kick();
    return tw;
  }
  function runTweens(now) {
    for (const tw of [...tweens]) {
      const k = clamp((now - tw.t0) / tw.dur, 0, 1);
      tw.fn(k);
      if (k >= 1) { tw.done = true; tweens.delete(tw); tw.resolve(); }
    }
    return tweens.size > 0;
  }

  // Кубики.
  let rolling = null, shownDice = [0, 0];
  const _qa = new THREE.Quaternion(), _qb = new THREE.Quaternion();
  function faceQuat(n, yaw, out = new THREE.Quaternion()) {
    const nv = M.DIE_NORMAL[n] || M.DIE_NORMAL[1];
    _qa.setFromUnitVectors(new THREE.Vector3(...nv), UP);
    _qb.setFromAxisAngle(UP, yaw);
    return out.copy(_qb).multiply(_qa);
  }
  // Де кубики лежать: точка столу, що в основному виді падає в правий нижній кут екрана, але поза рамкою.
  let diceRest = null;
  function computeRest() {
    const sz = 0.18, wide = W / H >= 1.05;
    const ndc = wide ? [0.8, -0.62] : [0.5, -0.84];
    const pl = new THREE.Plane(new THREE.Vector3(0, 1, 0), -sz), P = new THREE.Vector3();
    _ray.setFromCamera(new THREE.Vector2(ndc[0], ndc[1]), cam);
    if (!_ray.ray.intersectPlane(pl, P)) P.set(5.6, sz, 2.2);
    const ap = (M.SEA_R + M.FRAME_W) * Math.cos(Math.PI / 6) + 0.38;
    let best = 0, bn = null;
    for (let i = 0; i < 6; i++) {
      const a = Math.PI / 6 + i * Math.PI / 3, n = new THREE.Vector3(Math.cos(a), 0, Math.sin(a));
      const d = P.x * n.x + P.z * n.z;
      if (d > best || !bn) { best = d; bn = n; }
    }
    if (best < ap) P.addScaledVector(bn, ap - best);
    const t = new THREE.Vector3(-bn.z, 0, bn.x);
    const A = P.clone().addScaledVector(t, 0.3), B = P.clone().addScaledVector(t, -0.3);
    const sx = (v) => Math.abs(_pj.copy(v).project(cam).x);
    return sx(A) < sx(B) ? [B.addScaledVector(bn, 0.12), A] : [A.addScaledVector(bn, 0.12), B];
  }
  function restSpots() { return (diceRest || [new THREE.Vector3(5.75, 0.18, 1.75), new THREE.Vector3(5.3, 0.18, 2.45)]).map((v) => v.clone()); }
  function restDice(a, b) {
    const R = restSpots();
    [a, b].forEach((n, i) => { dice[i].position.copy(R[i]); faceQuat(n, 0.25 + i * 0.5, dice[i].quaternion); dice[i].visible = true; });
    shownDice = [a, b];
    dirty = true;
  }
  function roll(a, b) {
    a = clamp(a | 0, 1, 6); b = clamp(b | 0, 1, 6);
    if (rolling) rolling.cancel();
    if (reduced) { restDice(a, b); return Promise.resolve(); }
    const R = restSpots(), parts = [];
    const outDir = new THREE.Vector3(R[0].x, 0, R[0].z).normalize();
    const side = new THREE.Vector3(-outDir.z, 0, outDir.x);
    const starts = R.map((r, i) => r.clone().addScaledVector(outDir, 2.6 + i * 0.3).addScaledVector(side, (i ? -1 : 1) * 0.9));
    const finals = [a, b].map((n, i) => faceQuat(n, 0.25 + i * 0.5));
    const spin = [7.5, 9.0];
    dice.forEach((d) => { d.visible = true; });
    shownDice = [a, b];
    const tw = tween(1100, (k) => {
      [0, 1].forEach((i) => {
        const kk = clamp((k * 1100 - i * 60) / 1040, 0, 1), e = easeOut(kk);
        const d = dice[i], S = starts[i], E = R[i];
        d.position.set(S.x + (E.x - S.x) * e, 0, S.z + (E.z - S.z) * e);
        d.position.y = E.y + 0.9 * Math.abs(Math.cos(kk * Math.PI * 2.5)) * Math.pow(1 - kk, 2);
        const mv = new THREE.Vector3(E.x - S.x, 0, E.z - S.z).normalize();
        const axis = new THREE.Vector3().crossVectors(UP, mv).normalize();
        _qa.setFromAxisAngle(axis, -spin[i] * (1 - e));
        d.quaternion.copy(_qa).multiply(finals[i]);
      });
    });
    rolling = tw;
    return tw.promise.then(() => { if (rolling === tw) rolling = null; dirty = true; });
  }

  // Роздача: жетони ресурсів злітають із гексів до хати, що їх отримала, і тануть.
  let produceTw = null;
  const _dirC = new THREE.Vector3();
  function produce(gains) {
    if (produceTw) produceTw.cancel();
    if (reduced || !Array.isArray(gains) || !gains.length || !board) return Promise.resolve();
    const list = [];
    gains.forEach((g, gi) => {
      const P = hexPos[g.hex]; if (!P) return;
      const hv = board.hexes[g.hex]?.v || [];
      const town = (view?.towns || []).find((t) => t.seat === g.seat && hv.includes(t.v));
      const to = town ? vxPos[town.v].clone() : P.clone();
      const c = new THREE.Color(RES_COLOR[g.res] ?? '#cccccc');
      for (let j = 0; j < Math.min(3, g.n || 1) && list.length < chips.instanceMatrix.count; j++)
        list.push({ from: P.clone().setY(M.TILE_H + 0.12), to: to.setY(M.TILE_H + 0.35), delay: Math.min(0.2, (list.length) * 0.05), c, j });
    });
    if (!list.length) return Promise.resolve();
    list.forEach((o, i) => chips.setColorAt(i, o.c));
    chips.instanceColor.needsUpdate = true;
    chips.count = list.length; chips.visible = true;
    produceTw = tween(1000, (k) => {
      list.forEach((o, i) => {
        const kk = clamp((k - o.delay) / 0.78, 0, 1);
        let x, y, z, s;
        if (kk < 0.6) {
          const e = easeInOut(kk / 0.6);
          x = o.from.x + (o.to.x - o.from.x) * e; z = o.from.z + (o.to.z - o.from.z) * e;
          y = o.from.y + (o.to.y - o.from.y) * e + Math.sin(Math.PI * e) * 0.45 + o.j * 0.07;
          s = Math.min(1, kk / 0.15);
        } else {
          const e = (kk - 0.6) / 0.4;
          x = o.to.x; z = o.to.z; y = o.to.y + o.j * 0.07 + e * 0.35; s = 1 - e;
        }
        // монетка лицем до камери, легенько крутиться
        _pv.set(x, y, z); _dirC.copy(cam.position).sub(_pv).normalize();
        _q.setFromUnitVectors(UP, _dirC).multiply(_qa.setFromAxisAngle(UP, kk * 5));
        chips.setMatrixAt(i, _m4.compose(_pv, _q, _sv.setScalar(Math.max(0.001, s * 1.35))));
      });
      chips.instanceMatrix.needsUpdate = true;
      if (k >= 1) { chips.count = 0; chips.visible = false; }
    });
    return produceTw.promise.then(() => { chips.count = 0; chips.visible = false; dirty = true; });
  }

  function pop(kind, id) {
    const key = (kind === 'road' || kind === 'e') ? 'e' + id : 'v' + id;
    if (reduced) return;
    popT.set(key, { t: performance.now(), seen: false });
    if (view) placePieces();
    kick();
  }

  // ------------------------------------------------------------------------------------------------ цикл
  let raf = 0, dirty = true, clockT = 0, lastNow = performance.now(), alive = true, popping = false;
  function frame(now) {
    raf = 0;
    if (!alive || document.hidden) return;
    const dt = Math.min(0.05, Math.max(0, (now - lastNow) / 1000));
    lastNow = now; clockT += dt;
    let active = false;
    // згасання обертання
    if (!ptrs.size && (Math.abs(vel.az) > 1e-5 || Math.abs(vel.pol) > 1e-5)) {
      const ms = dt * 1000;
      cs.az = clamp(cs.az + vel.az * ms, -AZ_LIM, AZ_LIM); cs.pol = clamp(cs.pol + vel.pol * ms, POLAR_MIN, POLAR_MAX);
      const f = Math.exp(-dt * 6); vel.az *= f; vel.pol *= f;
      if (Math.abs(vel.az) < 1e-5) vel.az = 0;
      if (Math.abs(vel.pol) < 1e-5) vel.pol = 0;
      applyCam(); active = true;
    }
    if (runTweens(now)) active = true;
    if (popT.size) { popping = placePieces(now); if (popping) active = true; }
    if (bgAnim && boardGroup) { animBg(clockT); active = true; }
    if (legal.kind && !reduced) { rebuildLegal(clockT); active = true; }
    if (active || dirty) {
      renderer.render(scene, cam);
      dirty = false;
    }
    if (active || dirty) raf = requestAnimationFrame(frame);
  }
  function kick() {
    if (!alive || raf || document.hidden) return;
    lastNow = performance.now();
    raf = requestAnimationFrame(frame);
  }
  // Без фонової анімації цикл засинає, коли нічого не рухається; будь-яка зміна будить його через dirty.
  const dirtyProxy = () => { dirty = true; kick(); };
  function onVis() { if (document.hidden) { if (raf) cancelAnimationFrame(raf); raf = 0; } else dirtyProxy(); }
  document.addEventListener('visibilitychange', onVis);
  const ro = typeof ResizeObserver === 'function' ? new ResizeObserver(() => { resize(); dirtyProxy(); }) : null;
  ro?.observe(host);

  resize();
  applyCam();
  kick();

  function wrap(fn) { return (...a) => { if (!alive) return undefined; const r = fn(...a); dirtyProxy(); return r; }; }

  function dispose() {
    if (!alive) return;
    alive = false;
    if (raf) cancelAnimationFrame(raf); raf = 0;
    for (const tw of [...tweens]) tw.cancel();
    document.removeEventListener('visibilitychange', onVis);
    ro?.disconnect();
    canvas.removeEventListener('pointerdown', onDown);
    canvas.removeEventListener('pointermove', onMove);
    canvas.removeEventListener('pointerup', onUp);
    canvas.removeEventListener('pointercancel', onUp);
    canvas.removeEventListener('pointerleave', onLeave);
    canvas.removeEventListener('wheel', onWheel);
    canvas.removeEventListener('dblclick', onDbl);
    canvas.removeEventListener('contextmenu', noCtx);
    disposeBoard();
    const mats = new Set(), geos = new Set();
    scene.traverse((o) => {
      if (o.geometry) geos.add(o.geometry);
      if (o.material) (Array.isArray(o.material) ? o.material : [o.material]).forEach((m) => mats.add(m));
      if (o.isInstancedMesh) o.dispose();
    });
    geos.forEach((g) => g.dispose());
    mats.forEach((m) => m.dispose());
    [atlas, dieTex, seaMat, tokenMat, ghostWhite, ghostCol, hlMat, matVC, dieMat].forEach((x) => x.dispose());
    renderer.dispose();
    renderer.forceContextLoss?.();
    canvas.remove();
  }

  const api = {
    setBoard: wrap(setBoard),
    setState: wrap(setState),
    setLegal: wrap(setLegal),
    roll: (a, b) => alive ? (kick(), roll(a, b)) : Promise.resolve(),
    produce: (g) => alive ? (kick(), produce(g)) : Promise.resolve(),
    pop: wrap(pop),
    screenPos: (k, id) => alive ? screenPos(k, id) : null,
    resetView: wrap(resetView),
    resize: wrap(() => { W = H = 0; resize(); }),
    dispose,
  };
  // Лише для стенду (dev/scene.html): лічильник викликів малювання. У контракт не входить.
  Object.defineProperty(api, '_dbg', { value: { renderer, scene, camera: cam, get raf() { return raf; } }, enumerable: false });
  return api;
}
