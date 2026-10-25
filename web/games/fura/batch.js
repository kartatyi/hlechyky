// Пакет статичної геометрії по чанках (STYLE.md §4.3): усе нерухоме зливається в ОДИН меш на матеріал на чанк,
// уже в світових координатах, колір — вертексний. Ніякого `new Mesh` на хату, вибоїну чи шматок мосту.
//
// Трикутник іде в чанк за своїм центром. Чанк — квадрат size м від (x0, z0). Матеріали поділено на «основні»
// (видно до туману) і «дрібні» (розмітка, світлоповертачі — до near м від центру чанка): кожна група — свій THREE.LOD
// з порожнім рівнем далі, тож далекі чанки не малюються зовсім. Межі LOD можна посунути на льоту (setFar) — коли
// небо 2.0 відсуне туман.

class Buf {
  constructor() { this.a = new Float32Array(1024); this.n = 0; }
  push3(x, y, z) {
    if (this.n + 3 > this.a.length) { const b = new Float32Array(this.a.length * 2); b.set(this.a); this.a = b; }
    this.a[this.n++] = x; this.a[this.n++] = y; this.a[this.n++] = z;
  }
  view() { return this.a.subarray(0, this.n); }
}

export class ChunkBatch {
  /** opts: { x0, z0, size, uvKeys: ключі з UV = світ/uvScale (планарні, без швів між шматками), uvScale } */
  constructor(THREE, opts) {
    this.T = THREE;
    this.x0 = opts.x0; this.z0 = opts.z0; this.size = opts.size;
    this.uvKeys = new Set(opts.uvKeys || ['road']);
    this.uvScale = opts.uvScale || 4;
    this.chunks = new Map();          // 'ix:iz' → Map(key → {pos, col})
    this._c = new THREE.Color();
    this._v = new THREE.Vector3();
    this._m = new THREE.Matrix4();
    this.tris = 0;
  }
  _bucket(key, x, z) {
    const ix = Math.floor((x - this.x0) / this.size), iz = Math.floor((z - this.z0) / this.size);
    const ck = ix + ':' + iz;
    let ch = this.chunks.get(ck);
    if (!ch) { ch = new Map(); ch.ix = ix; ch.iz = iz; this.chunks.set(ck, ch); }
    let b = ch.get(key);
    if (!b) { b = { pos: new Buf(), col: new Buf() }; ch.set(key, b); }
    return b;
  }
  _rgb(color) { return color && color.isColor ? color : this._c.set(color == null ? 0xffffff : color); }
  /** Трикутник з трьох точек {x, y, z}; color — hex/Color, або масив з трьох (по вершині). */
  tri(key, a, b, c, color) {
    const bk = this._bucket(key, (a.x + b.x + c.x) / 3, (a.z + b.z + c.z) / 3);
    const cols = Array.isArray(color) ? color.map((x) => this._rgb(x).clone()) : null;
    const one = cols ? null : this._rgb(color);
    [a, b, c].forEach((p, i) => {
      bk.pos.push3(p.x, p.y, p.z);
      const q = cols ? cols[i] : one;
      bk.col.push3(q.r, q.g, q.b);
    });
    this.tris++;
  }
  /** Чотирикутник a-b-c-d (по колу). Бік не важить: матеріали двобічні. */
  quad(key, a, b, c, d, color) { this.tri(key, a, b, c, color); this.tri(key, a, c, d, color); }
  /** Будь-яка геометрія three.js: matrix — куди поставити, color — тон, ao(yLocal) — множник яскравості за висотою. */
  add(key, geo, matrix, color, ao) {
    const g = geo.index ? geo.toNonIndexed() : geo;
    const P = g.attributes.position, m = matrix || this._m.identity();
    const base = this._rgb(color).clone(), c = new this.T.Color();
    const v = [new this.T.Vector3(), new this.T.Vector3(), new this.T.Vector3()], ly = [0, 0, 0];
    for (let i = 0; i + 2 < P.count; i += 3) {
      for (let k = 0; k < 3; k++) { v[k].fromBufferAttribute(P, i + k); ly[k] = v[k].y; v[k].applyMatrix4(m); }
      const cx = (v[0].x + v[1].x + v[2].x) / 3, cz = (v[0].z + v[1].z + v[2].z) / 3;
      const bk = this._bucket(key, cx, cz);
      for (let k = 0; k < 3; k++) {
        bk.pos.push3(v[k].x, v[k].y, v[k].z);
        const f = ao ? ao(ly[k]) : 1;
        c.copy(base).multiplyScalar(f);
        bk.col.push3(c.r, c.g, c.b);
      }
      this.tris++;
    }
    if (g !== geo) g.dispose();
  }

  /**
   * Меші в сцену. mats — { ключ: матеріал }; shadow — { ключ: [cast, receive] }; near — ключі «дрібного» шару й
   * відстань, до якої їх видно; far — до якої видно чанк узагалі.
   */
  build(scene, mats, { shadow = {}, nearKeys = ['mark', 'reflect'], near = 600, far = 1000 } = {}) {
    const T = this.T, half = this.size / 2;
    this.lods = [];
    let meshes = 0;
    for (const ch of this.chunks.values()) {
      const cx = this.x0 + (ch.ix + 0.5) * this.size, cz = this.z0 + (ch.iz + 0.5) * this.size;
      const groups = { base: new T.Group(), near: new T.Group() };
      // ключі з тим самим матеріалом (напр., metal = matte, поки світло Lambert) — в один меш
      const byMat = new Map();
      for (const [key, b] of ch) {
        if (!b.pos.n) continue;
        const mat = mats[key], isN = nearKeys.includes(key);
        const id = mat.uuid + (isN ? ":n" : ":b");
        if (!byMat.has(id)) byMat.set(id, { key, isNear: isN, parts: [] });
        byMat.get(id).parts.push(b);
      }
      for (const { key, isNear, parts } of byMat.values()) {
        const g = new T.BufferGeometry();
        const cat = (f) => { let n = 0; for (const b of parts) n += b[f].n; const a = new Float32Array(n); let o = 0; for (const b of parts) { a.set(b[f].view(), o); o += b[f].n; } return a; };
        const pos = cat('pos');
        g.setAttribute('position', new T.BufferAttribute(pos, 3));
        g.setAttribute('color', new T.BufferAttribute(cat('col'), 3));
        if (this.uvKeys.has(key)) {
          const uv = new Float32Array(pos.length / 3 * 2), s = 1 / this.uvScale;
          for (let i = 0, j = 0; i < pos.length; i += 3, j += 2) { uv[j] = pos[i] * s; uv[j + 1] = pos[i + 2] * s; }
          g.setAttribute('uv', new T.BufferAttribute(uv, 2));
        }
        g.computeVertexNormals();          // без індексу — нормаль грані
        g.computeBoundingSphere();
        g.translate(-cx, 0, -cz);          // LOD міряє відстань від свого центру — сітку зсуваємо до нього
        g.computeBoundingSphere();
        const m = new T.Mesh(g, mats[key]);
        const sh = Object.entries(shadow).filter(([k]) => mats[k] === mats[key]).map(([, v]) => v);
        m.castShadow = sh.some((v) => v[0]); m.receiveShadow = !sh.length || sh.some((v) => v[1]);
        m.name = 'chunk:' + key;
        m.matrixAutoUpdate = false;
        (isNear ? groups.near : groups.base).add(m);
        meshes++;
      }
      for (const [kind, grp] of Object.entries(groups)) {
        if (!grp.children.length) continue;
        const lod = new T.LOD();
        lod.position.set(cx, 0, cz);
        lod.addLevel(grp, 0);
        lod.addLevel(new T.Object3D(), (kind === 'near' ? near : far) + half * Math.SQRT2);
        lod.userData.kind = kind;
        lod.autoUpdate = true;
        scene.add(lod);
        this.lods.push(lod);
      }
    }
    this.chunks.clear();
    return { meshes, lods: this.lods.length, tris: this.tris };
  }

  /** Посунути межу «чанк видно» (напр., за туманом): far — до краю чанка. */
  setFar(far) {
    const half = this.size / 2 * Math.SQRT2;
    for (const lod of this.lods || []) if (lod.userData.kind === 'base') lod.levels[1].distance = far + half;
  }
}
