// Матеріали й палітра «Чумаків» — одне місце на весь світ (STYLE.md §2). Колір об'єкта — вертексний атрибут `color`,
// матеріал — лише «тип поверхні»: сто хат різних кольорів — один матеріал і один draw call на чанк. З 0.0.5 («Небо й
// графіка», гібрид B + V3, STYLE.md §15.1) усі — MeshStandardMaterial (PBR, оточення — небо тієї ж миті); туман, тінь
// хмар і rim додає sky.js латкою, вікна й ліхтарі вмикає воно ж за висотою сонця.
//
// Палітра — sRGB hex; three.js сам переводить у лінійні (ColorManagement увімкнено). Нових кольорів у модулях «від
// руки» не пишемо: беремо звідси або додаємо сюди з назвою.

export const PAL = {
  // земля
  grass: '#6f9a45', grassDry: '#a3a356', meadow: '#5f8f3e', verge: '#8a9a52', gravel: '#a39a86', soil: '#7a5f43',
  sand: '#cdbb8e', water: '#4f86a6',
  // дорога
  asphalt: '#686a6d', asphaltOld: '#77787a', asphaltPatch: '#55575a', concrete: '#a9a69e', curb: '#b9b5ab',
  dirt: '#8b7254', rut: '#6b5640', lineWhite: '#f1eee6', lineFaded: '#c4c1b8', lineYellow: '#e8b730',
  guardrail: '#c3c7cb', post: '#8d9196', reflector: '#d08a2c', bridge: '#9b968c', bridgeDark: '#7d7a72',
  ballast: '#8f8578', railSteel: '#6c6f75', sleeper: '#5d4a3a',
  // будинки (теперішній край)
  plaster: '#f2ede0', plasterWarm: '#e8dcc0', roofOchre: '#9a7a45', roofRed: '#8b3b2e', depot: '#b5582f',
  cream: '#efe6d2', brick: '#a5482f', brickDark: '#8e3a28', silo: '#c8ccd1', siloHall: '#d9d4c7', honey: '#c9a46a',
  honeyRoof: '#8a6a3a', slab: '#8b8f96', signBlue: '#2f5d8a', signPost: '#888888',
  // рослинність
  crown: '#4c8538', crownLight: '#6d9e3e', crownDark: '#3a6a33', poplar: '#517d3a', willow: '#7fa04e', pine: '#3a6636',
  pineDark: '#2f5a33', trunk: '#6e4a2f',
  // вікна й ліхтарі (0.0.5): скло вдень, тепле світло вночі
  glass: '#3c5468', windowLit: '#ffc77a', lampHead: '#d8d8d0', lampLit: '#ffe2b0', lampPost: '#7d8288',
};

/** Пляма світла ліхтаря: радіальний спад (білий — колір дає матеріал). */
function poolTexture(THREE) {
  const c = document.createElement('canvas');
  c.width = c.height = 64;
  const g = c.getContext('2d'), gr = g.createRadialGradient(32, 32, 0, 32, 32, 32);
  gr.addColorStop(0, 'rgba(255,255,255,1)'); gr.addColorStop(0.45, 'rgba(255,255,255,.55)'); gr.addColorStop(1, 'rgba(255,255,255,0)');
  g.fillStyle = gr; g.fillRect(0, 0, 64, 64);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.NoColorSpace;
  return t;
}

/**
 * Зерно 256² (сірий шум із середнім ≈ 0,74) — ламає «пластмасу» асфальту найдешевше. Колір матеріалу множимо на
 * 1/0,74, щоб тон лишався з палітри (STYLE §2.1). Плями двох масштабів + дрібні камінці.
 */
function grainTexture(THREE) {
  const N = 256, c = document.createElement('canvas');
  c.width = c.height = N;
  const g = c.getContext('2d'), img = g.createImageData(N, N);
  let s = 1918;
  const rnd = () => { s = (s * 1664525 + 1013904223) >>> 0; return s / 4294967296; };
  // плавний шум на сітці 8 і 32 пікселі (тайлиться: сітка замикається по модулю)
  const grid = (k) => { const n = N / k, a = new Float32Array(n * n); for (let i = 0; i < a.length; i++) a[i] = rnd(); return { n, k, a }; };
  const G1 = grid(32), G2 = grid(8);
  const smp = ({ n, k, a }, x, y) => {
    const fx = x / k, fy = y / k, ix = Math.floor(fx), iy = Math.floor(fy), u = fx - ix, v = fy - iy;
    const A = (i, j) => a[((j % n + n) % n) * n + ((i % n + n) % n)];
    const su = u * u * (3 - 2 * u), sv = v * v * (3 - 2 * v);
    return A(ix, iy) * (1 - su) * (1 - sv) + A(ix + 1, iy) * su * (1 - sv) + A(ix, iy + 1) * (1 - su) * sv + A(ix + 1, iy + 1) * su * sv;
  };
  for (let y = 0; y < N; y++) for (let x = 0; x < N; x++) {
    let v = 0.74 + (smp(G1, x, y) - 0.5) * 0.10 + (smp(G2, x, y) - 0.5) * 0.08 + (rnd() - 0.5) * 0.10;
    const r = rnd();
    if (r < 0.012) v += 0.16; else if (r < 0.024) v -= 0.14;           // камінці щебеню в асфальті
    const b = Math.max(0, Math.min(255, Math.round(v * 255)));
    const i = (y * N + x) * 4;
    img.data[i] = img.data[i + 1] = img.data[i + 2] = b; img.data[i + 3] = 255;
  }
  g.putImageData(img, 0, 0);
  const t = new THREE.CanvasTexture(c);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.colorSpace = THREE.NoColorSpace;          // множник яскравості, не колір
  t.anisotropy = 4;
  return t;
}

const cache = new WeakMap();

/**
 * Матеріали світу. Ключі = «тип поверхні» для пакета (batch.js):
 * - `road` — асфальт, узбіччя, кути перехресть, настил: вертексний колір × зерно (UV — світові метри / 4);
 * - `mark` — розмітка: на 1 см над асфальтом і з більшим polygonOffset — лінії чіткі на будь-якій відстані;
 * - `matte` — хати, дахи, шпали, вивіски; `concrete` — мости (настил, парапети, опори); `metal` — відбійник,
 *   стійки й стовпи ліхтарів; `reflect` — світлоповертачі;
 * - `glass` — темні вікна; `win` — вікна, що світять уночі; `lamp` — плафони ліхтарів; `pool` — пляма світла
 *   ліхтаря на землі (адитивна, без туману);
 * - `tree` — дерева (інстанси з вертексним кольором), `terrain` — рельєф.
 * Усі двобічні (бік квада не важить), без власного кольору — він у вершинах.
 */
export function makeMaterials(THREE, quality) {
  if (cache.has(THREE)) return cache.get(THREE);
  const grain = grainTexture(THREE);
  const S = (o) => new THREE.MeshStandardMaterial(Object.assign({ vertexColors: true, side: THREE.DoubleSide, roughness: 0.92, metalness: 0 }, o));
  const k = 1 / 0.74;
  const M = {
    grain,
    road: S({ map: grain, color: new THREE.Color(k, k, k), roughness: 0.88, polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -4 }),
    mark: S({ roughness: 0.6, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -10 }),
    matte: S({ flatShading: true }),
    // бетон мостів: як matte, але тіні не кидає — інакше без normalBias сонця (sky.js) парапет сам себе «смугує»
    concrete: S({ flatShading: true, roughness: 0.95 }),
    metal: S({ flatShading: true, roughness: 0.45, metalness: 0.35 }),
    reflect: S({ roughness: 0.5, emissive: new THREE.Color(PAL.reflector), emissiveIntensity: 0.15, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -10 }),
    glass: S({ flatShading: true, roughness: 0.08, metalness: 0.1 }),
    win: S({ flatShading: true, roughness: 0.08, metalness: 0.1, emissive: new THREE.Color(PAL.windowLit), emissiveIntensity: 0 }),
    lamp: S({ flatShading: true, roughness: 0.4, emissive: new THREE.Color(PAL.lampLit), emissiveIntensity: 0 }),
    pool: new THREE.MeshBasicMaterial({ map: poolTexture(THREE), transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, fog: false,
      polygonOffset: true, polygonOffsetFactor: -3, polygonOffsetUnits: -12 }),
    tree: S({ flatShading: true, side: THREE.FrontSide }),
    terrain: S({ roughness: 0.98 }),
    quality,
  };
  M.pool.userData.noAtmo = true;
  // кольори полів у kray.js записані числами «як у палітрі» (sRGB), а вертексний колір three читає як лінійний — з
  // Lambert і ACES це давало блідо-пастельний край; тепер переводимо в шейдері (степінь 1,6, а не 2,2: смуги укосів і
  // узбіч підібрано під старе світло — з повним 2,2 вони ставали брудно-темними)
  M.terrain.onBeforeCompile = (sh) => { sh.vertexShader = sh.vertexShader.replace('#include <color_vertex>', '#include <color_vertex>\n\tvColor.rgb = pow(max(vColor.rgb, vec3(0.0)), vec3(1.6));'); };
  M.terrain.customProgramCacheKey = () => 'terrain-srgb';
  for (const [name, m] of Object.entries(M)) if (m && m.isMaterial && !m.name) m.name = 'fura:' + name;
  cache.set(THREE, M);
  return M;
}
