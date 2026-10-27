// «Колія» — малювання додатків K11: гуси 🪿 і таблички проміжної станції 🏪.
//  createExtrasRender(scene, THREE) → { update(s, alpha), dispose() } — для render.js (3D: x → X, y → Z, вгору — Y, 1 клітинка = 1)
//  drawExtras2d(canvas, s) — поверх налагоджувального виду debug2d (той самий масштаб і зсув, що й там)
// Гачки: render.js — після створення scene: `const extras = createExtrasRender(scene, THREE);`, у draw: `extras.update(s, alpha);`
//        main.js — у frame() після view.draw: `if (q.get('view') === '2d') drawExtras2d(canvas, game.state);`
import { U, G, I, wagonPos } from './rules.js';
import { UPGRADES } from './extras.js';

const ITEM_COL = { [I.AXE]: 0xd33333, [I.PICK]: 0x3388cc, [I.BUCKET]: 0x99bbdd, [I.BUCKET_FULL]: 0x1166ee };

export function createExtrasRender(scene, THREE) {
  const root = new THREE.Group(); root.name = 'kol-extras'; scene.add(root);
  const box = new THREE.BoxGeometry(1, 1, 1);
  const mat = (c) => new THREE.MeshLambertMaterial({ color: c });
  const M = { white: mat(0xf4f4ee), grey: mat(0xcfcfc8), orange: mat(0xff8a1e), black: mat(0x161616), wood: mat(0x8a5a2e),
              board: mat(0xe9d8a6), chosen: mat(0x6fd36f), roof: mat(0xa8452f) };
  const itemMat = {};
  const cube = (parent, x, y, z, sx, sy, sz, m) => {
    const o = new THREE.Mesh(box, m); o.position.set(x, y, z); o.scale.set(sx, sy, sz); o.castShadow = true; parent.add(o); return o;
  };

  // ---- гуска: дивиться вздовж +X ----
  function makeGoose() {
    const g = new THREE.Group();
    const body = new THREE.Group(); g.add(body);
    cube(body, 0, 0.2, 0, 0.42, 0.22, 0.26, M.white);          // тулуб
    cube(body, -0.24, 0.26, 0, 0.12, 0.1, 0.18, M.grey);       // хвіст
    cube(body, 0, 0.24, 0.14, 0.26, 0.1, 0.04, M.grey);        // крила
    cube(body, 0, 0.24, -0.14, 0.26, 0.1, 0.04, M.grey);
    const neck = new THREE.Group(); neck.position.set(0.17, 0.28, 0); body.add(neck);
    cube(neck, 0, 0.14, 0, 0.08, 0.28, 0.08, M.white);
    const head = new THREE.Group(); head.position.set(0.03, 0.3, 0); neck.add(head);
    cube(head, 0, 0, 0, 0.14, 0.1, 0.1, M.white);
    cube(head, 0.11, -0.01, 0, 0.1, 0.05, 0.06, M.orange);     // дзьоб
    cube(head, 0.03, 0.02, 0.052, 0.025, 0.025, 0.01, M.black);
    cube(head, 0.03, 0.02, -0.052, 0.025, 0.025, 0.01, M.black);
    const legL = cube(g, 0.02, 0.05, 0.07, 0.04, 0.1, 0.04, M.orange);
    const legR = cube(g, 0.02, 0.05, -0.07, 0.04, 0.1, 0.04, M.orange);
    const held = new THREE.Group(); held.position.set(0.16, -0.06, 0); head.add(held);
    root.add(g);
    return { g, body, neck, head, legL, legR, held, heldK: -1, px: 0, py: 0, x: 0, y: 0, tick: -1, yaw: 0 };
  }
  function setHeld(m, k) {
    if (m.heldK === k) return;
    m.heldK = k;
    while (m.held.children.length) m.held.remove(m.held.children[0]);
    if (!k) return;
    itemMat[k] = itemMat[k] || mat(ITEM_COL[k] || 0xffffff);
    if (k === I.AXE || k === I.PICK) { cube(m.held, 0, -0.05, 0, 0.04, 0.22, 0.04, M.wood); cube(m.held, 0, 0.05, 0, 0.16, 0.06, 0.05, itemMat[k]); }
    else cube(m.held, 0, -0.07, 0, 0.13, 0.13, 0.13, itemMat[k]);
  }

  const geese = new Map();   // id → меш
  // ---- табличка проміжної станції ----
  let sign = null;           // { key, g, boards: [{ k, mesh }] }
  function makeSigns(s) {
    if (sign) { root.remove(sign.g); sign = null; }
    const m = s.mid; if (!m) return;
    const g = new THREE.Group(); root.add(g);
    // навіс над платформою
    const cx = (m.x0 + m.x1 + 1) / 2, cz = (m.y0 + m.y1 + 1) / 2, sw = m.x1 - m.x0 + 1, sh = m.y1 - m.y0 + 1;
    for (const [dx, dz] of [[0, 0], [1, 0], [0, 1], [1, 1]]) cube(g, m.x0 + 0.2 + dx * (sw - 0.4), 0.7, m.y0 + 0.2 + dz * (sh - 0.4), 0.1, 1.4, 0.1, M.wood);
    cube(g, cx, 1.45, cz, sw + 0.3, 0.12, sh + 0.3, M.roof);
    const boards = m.signs.map((sg) => {
      const b = new THREE.Group(); b.position.set(sg.x + 0.5, 0, sg.y + 0.5); g.add(b);
      cube(b, 0, 0.35, 0, 0.08, 0.7, 0.08, M.wood);
      const tex = labelTex(sg.k);
      const face = new THREE.Mesh(new THREE.PlaneGeometry(0.8, 0.5), new THREE.MeshBasicMaterial({ map: tex, transparent: true }));
      face.rotation.x = -0.5; face.position.set(0, 0.85, 0.06); b.add(face);
      const back = cube(b, 0, 0.85, 0, 0.84, 0.54, 0.04, M.board); back.rotation.x = -0.5;
      return { k: sg.k, b, back };
    });
    sign = { key: s.seed + ':' + s.w, g, boards };
  }
  function labelTex(k) {
    const c = document.createElement('canvas'); c.width = 256; c.height = 160;
    const x = c.getContext('2d');
    x.fillStyle = '#e9d8a6'; x.fillRect(0, 0, 256, 160);
    x.strokeStyle = '#6b4520'; x.lineWidth = 8; x.strokeRect(4, 4, 248, 152);
    x.font = '72px serif'; x.textAlign = 'center'; x.textBaseline = 'middle'; x.fillText(UPGRADES[k].icon, 128, 62);
    x.font = 'bold 32px sans-serif'; x.fillStyle = '#3a2410';
    x.fillText(UPGRADES[k].short, 128, 128, 236);
    const t = new THREE.CanvasTexture(c); t.colorSpace = THREE.SRGBColorSpace || t.colorSpace; return t;
  }

  return {
    update(s, alpha = 0) {
      // таблички
      const key = s.mid ? s.seed + ':' + s.w : null;
      if ((sign && sign.key) !== key) makeSigns(s);
      if (sign && s.mid) for (const b of sign.boards) {
        const picked = s.mid.pick === b.k, gone = s.mid.pick && !picked;
        b.b.visible = !gone;
        b.back.material = picked ? M.chosen : M.board;
        b.b.position.y = !s.mid.pick && s.mid.wait ? Math.abs(Math.sin(s.tick * 0.25)) * 0.12 : 0;   // підстрибують, коли потяг чекає
      }
      // гуси
      const live = new Set();
      for (const gs of s.geese || []) {
        live.add(gs.id);
        let m = geese.get(gs.id);
        if (!m) { m = makeGoose(); m.px = m.x = gs.x; m.py = m.y = gs.y; m.tick = s.tick; geese.set(gs.id, m); }
        if (m.tick !== s.tick) { m.px = m.x; m.py = m.y; m.x = gs.x; m.y = gs.y; m.tick = s.tick; }
        const x = (m.px + (m.x - m.px) * alpha) / U, z = (m.py + (m.y - m.py) * alpha) / U;
        const dx = m.x - m.px, dz = m.y - m.py;
        if (dx || dz) { const want = Math.atan2(-dz, dx); let d = want - m.yaw; d = Math.atan2(Math.sin(d), Math.cos(d)); m.yaw += d * 0.3; }
        const cx = Math.floor(x), cz = Math.floor(z);
        const inS = cx >= 0 && cz >= 0 && cx < s.w && cz < s.h;
        const gk = inS ? s.ground[cz * s.w + cx] : G.GRASS;
        const swim = gk === G.WATER;
        const ph = (s.tick + alpha) * (gs.mode === 'go' ? 0.9 : 1.4) + gs.id;
        m.g.position.set(x, swim ? -0.32 : 0.03, z);
        m.g.rotation.y = m.yaw;
        m.body.rotation.x = Math.sin(ph) * 0.12;                 // перевальцем
        m.body.position.y = Math.abs(Math.sin(ph)) * 0.03;
        m.legL.position.x = 0.02 + Math.sin(ph) * 0.05; m.legR.position.x = 0.02 - Math.sin(ph) * 0.05;
        m.legL.visible = m.legR.visible = !swim;
        m.neck.rotation.z = gs.mode === 'flee' ? -0.5 : Math.sin(ph * 0.5) * 0.15;   // тікає — шия вперед
        setHeld(m, gs.hold || 0);
      }
      for (const [k, m] of geese) if (!live.has(k)) { root.remove(m.g); geese.delete(k); }
    },
    dispose() { scene.remove(root); geese.clear(); sign = null; },
  };
}

/** Налагоджувальний 2D: той самий масштаб/зсув, що в debug2d.draw */
export function drawExtras2d(canvas, s) {
  if (!s || (!s.mid && !(s.geese && s.geese.length))) return;
  const g = canvas.getContext('2d');
  const W = canvas.width, H = canvas.height;
  const c = Math.max(8, Math.floor(H / s.h));
  const head = wagonPos(s, 0).x / U;
  const ox = Math.max(0, Math.min(s.w * c - W, head * c - W * 0.35));
  g.save(); g.translate(-ox, (H - s.h * c) / 2);
  if (s.mid) {
    const m = s.mid;
    g.strokeStyle = '#ffcc33'; g.lineWidth = 2;
    g.strokeRect(m.x0 * c, m.y0 * c, (m.x1 - m.x0 + 1) * c, (m.y1 - m.y0 + 1) * c);
    g.font = `${Math.floor(c * 0.8)}px serif`; g.textAlign = 'center'; g.textBaseline = 'middle';
    for (const sg of m.signs) {
      if (m.pick && m.pick !== sg.k) continue;
      g.fillStyle = m.pick === sg.k ? '#6fd36f' : '#e9d8a6';
      g.fillRect(sg.x * c + 1, sg.y * c + 1, c - 2, c - 2);
      g.fillText(UPGRADES[sg.k].icon, sg.x * c + c / 2, sg.y * c + c / 2);
    }
  }
  for (const gs of s.geese || []) {
    const x = gs.x / U * c, y = gs.y / U * c;
    g.fillStyle = '#fff'; g.beginPath(); g.ellipse(x, y, c * 0.28, c * 0.2, 0, 0, 7); g.fill();
    g.fillStyle = '#ff8a1e'; g.fillRect(x + c * 0.2, y - c * 0.05, c * 0.15, c * 0.1);
    if (gs.hold) { g.fillStyle = '#' + (ITEM_COL[gs.hold] || 0xffffff).toString(16).padStart(6, '0'); g.fillRect(x - 3, y - c * 0.45, 6, 6); }
    if (gs.mode === 'flee') { g.strokeStyle = '#f33'; g.lineWidth = 1; g.beginPath(); g.arc(x, y, c * 0.35, 0, 7); g.stroke(); }
  }
  g.restore();
}
