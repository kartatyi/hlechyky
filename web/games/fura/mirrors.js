// Бокові дзеркала «Фури 2.0» (STYLE §9): render-to-texture лише з камери «з кабіни», по одному дзеркалу за кадр.
// Обидва дзеркала — половини однієї цілі (один матеріал, один draw call); камера дзеркала стоїть у склі й дивиться назад
// із розворотом назовні 0,06 рад, FOV 30°, далекість 200 м, лише шар 0 (без трави й дрібноти), без проходу тіней.
// Ціль: 160×320 на «Низькій», 256×512 — решта. Дзеркало, якого не видно в кадрі, не малюється.

export function makeMirrors(THREE, tractor, glasses) {
  // glasses: [{ pos: [x, y, z], w, h, out: ±1 }] — центр скла в осях тягача, скло дивиться назад (−Z)
  const W0 = 256, H0 = 512;
  let rt = null, W = 0, H = 0, turn = 0;
  const mat = new THREE.MeshBasicMaterial({ color: '#ffffff' });
  const dark = new THREE.MeshStandardMaterial({ color: '#1c2630', roughness: 0.08, metalness: 1 });
  const cam = new THREE.PerspectiveCamera(30, 0.5, 0.3, 200);
  const items = glasses.map((g, i) => {
    const geo = new THREE.PlaneGeometry(g.w, g.h);
    const uv = geo.attributes.uv;                                  // половина цілі, віддзеркалена по горизонталі
    for (let k = 0; k < uv.count; k++) uv.setX(k, (i + 1 - uv.getX(k)) / glasses.length);
    const m = new THREE.Mesh(geo, dark);
    m.position.set(...g.pos);
    m.rotation.y = Math.PI + g.out * 0.12;                        // лицем назад і трохи до водія
    m.userData.noAtmo = true;
    m.castShadow = false; m.receiveShadow = false;
    tractor.add(m);
    return { g, m, i };
  });
  const v = new THREE.Vector3(), q = new THREE.Quaternion(), e = new THREE.Euler(), frustum = new THREE.Frustum(), pm = new THREE.Matrix4();
  let on = false;
  return {
    meshes: items.map((it) => it.m),
    setCabin(cabin) { on = cabin; for (const it of items) it.m.material = cabin && rt ? mat : dark; },
    /** Одне дзеркало за кадр. prep(true) — перед проходом (сховати салон, показати кабіну зовні), prep(false) — після. */
    render(renderer, scene, camera, o, prep) {
      if (!on) return;
      const low = o && o.low, w = low ? 160 : W0, h = low ? 320 : H0;
      if (!rt || W !== w || H !== h) {
        if (rt) rt.dispose();
        W = w; H = h;
        rt = new THREE.WebGLRenderTarget(W * items.length, H, { type: THREE.HalfFloatType, depthBuffer: true });
        rt.texture.colorSpace = THREE.LinearSRGBColorSpace;
        mat.map = rt.texture; mat.needsUpdate = true;
        for (const it of items) it.m.material = mat;
      }
      camera.updateMatrixWorld();
      pm.multiplyMatrices(camera.projectionMatrix, camera.matrixWorldInverse); frustum.setFromProjectionMatrix(pm);
      let it = null;
      for (let k = 0; k < items.length; k++) {
        const c = items[(turn + k) % items.length];
        c.m.getWorldPosition(v);
        if (frustum.containsPoint(v)) { it = c; break; }
      }
      turn++;
      if (!it) return;
      // камера: у склі, назад, назовні 0,06 рад і трохи вниз
      it.m.getWorldPosition(v);
      tractor.getWorldQuaternion(q);
      e.set(-0.035, -it.g.out * 0.06, 0, 'YXZ');                   // камера дивиться вздовж своєї −Z = назад тягача
      cam.position.copy(v);
      cam.quaternion.copy(q).multiply(new THREE.Quaternion().setFromEuler(e));
      cam.updateMatrixWorld();
      const sky = o && o.sky, clouds = sky && sky.U && sky.U.tClouds, cl = clouds && clouds.value;
      if (clouds) clouds.value = null;                              // хмари неба — у координатах екрана головної камери
      const prevT = renderer.getRenderTarget(), au = renderer.shadowMap.autoUpdate;
      renderer.shadowMap.autoUpdate = false;
      for (const x of items) x.m.visible = false;
      prep(true);
      rt.viewport.set(it.i * W, 0, W, H); rt.scissor.set(it.i * W, 0, W, H); rt.scissorTest = true;
      renderer.setRenderTarget(rt);
      renderer.render(scene, cam);
      renderer.setRenderTarget(prevT);
      prep(false);
      for (const x of items) x.m.visible = true;
      renderer.shadowMap.autoUpdate = au;
      if (clouds) clouds.value = cl;
    },
    dispose() { if (rt) rt.dispose(); mat.dispose(); dark.dispose(); },
  };
}
