// Об'ємні хмари «Чумаків» 0.0.5 (STYLE.md §17.2; перенесено з проби 4 — fura/style: clouds4.js) — своїм кодом за
// опублікованою технікою Nubis (Schneider, SIGGRAPH 2015):
// шар купчастих 1,5–4 км над кулястою планетою, густина = покриття (2D-погода) × профіль висоти × 3D-шум
// Перлін-Ворлі, обгризений дрібним Ворлі; світло — проби густини в бік світила (Бер + «порох»), двопелюсткова фаза
// Хеньї-Грінстейна, наближене багатократне розсіювання, заповнення від неба, повітряна перспектива.
// Рахуємо в зниженій роздільності (¼…½) з тремтінням кроку й накопиченням між кадрами (перепроєкція за напрямом:
// хмари далекі, тож важить лише поворот камери). Шум печемо на GPU при завантаженні — 3D-текстури 128³ і 32³.

import { SKYLUT_GLSL, COVER_GLSL } from './atmo.js';

const VERT = 'varying vec2 vUv; void main(){ vUv = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }';

// ---------------------------------------------------------------- генерація 3D-шуму (шар за шаром)
const NOISE_FRAG = `
precision highp float;
uniform float uZ, uN;
uniform int uKind;
varying vec2 vUv;
vec3 h33(vec3 p) { p = fract(p * vec3(0.1031, 0.1030, 0.0973)); p += dot(p, p.yxz + 33.33); return fract((p.xxy + p.yxx) * p.zyx); }
float worley(vec3 p, float F) {
  vec3 q = p * F, i = floor(q), f = fract(q); float d = 1.0;
  for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
    vec3 o = vec3(float(x), float(y), float(z));
    vec3 c = mod(i + o, F);                                  // періодично — текстура тайлиться
    d = min(d, length(o + h33(c * 1.37 + F) - f));
  }
  return 1.0 - clamp(d, 0.0, 1.0);
}
float grad(vec3 i, vec3 f, float F) { vec3 g = h33(mod(i, F) * 1.91 + 7.0) * 2.0 - 1.0; return dot(g, f); }
float perlin(vec3 p, float F) {
  vec3 q = p * F, i = floor(q), f = fract(q), u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
  float a = grad(i, f, F), b = grad(i + vec3(1, 0, 0), f - vec3(1, 0, 0), F), c = grad(i + vec3(0, 1, 0), f - vec3(0, 1, 0), F), d = grad(i + vec3(1, 1, 0), f - vec3(1, 1, 0), F);
  float e = grad(i + vec3(0, 0, 1), f - vec3(0, 0, 1), F), g = grad(i + vec3(1, 0, 1), f - vec3(1, 0, 1), F), h = grad(i + vec3(0, 1, 1), f - vec3(0, 1, 1), F), k = grad(i + vec3(1, 1, 1), f - vec3(1, 1, 1), F);
  return mix(mix(mix(a, b, u.x), mix(c, d, u.x), u.y), mix(mix(e, g, u.x), mix(h, k, u.x), u.y), u.z) * 0.5 + 0.5;
}
float wfbm(vec3 p, float F) { return worley(p, F) * 0.625 + worley(p, F * 2.0) * 0.25 + worley(p, F * 4.0) * 0.125; }
float remap(float v, float a, float b, float c, float d) { return c + (v - a) / (b - a) * (d - c); }
void main() {
  vec3 p = vec3(vUv, (uZ + 0.5) / uN);
  if (uKind == 0) {
    float pf = perlin(p, 4.0) * 0.6 + perlin(p, 8.0) * 0.28 + perlin(p, 16.0) * 0.12;
    float pw = remap(pf, 0.0, 1.0, wfbm(p, 4.0), 1.0);       // Перлін-Ворлі: пухкі «капусти» з круглими краями
    gl_FragColor = vec4(clamp(pw, 0.0, 1.0), wfbm(p, 4.0), wfbm(p, 8.0), wfbm(p, 16.0));
  } else {
    gl_FragColor = vec4(wfbm(p, 4.0), wfbm(p, 8.0), wfbm(p, 16.0), 1.0);
  }
}`;

// ---------------------------------------------------------------- трасування
const CLOUD_FRAG = SKYLUT_GLSL + COVER_GLSL + `
precision highp sampler3D;
uniform sampler3D tShape, tDetail;
uniform sampler2D tPrev;
uniform mat4 uInvProj, uCamWorld, uPrevVP;
uniform vec3 uCamPos, uSunCol, uAmbTop, uAmbBot;
uniform float uFrame, uSteps, uLightSteps, uDetail, uBlend, uHasPrev, uDens;
varying vec2 vUv;
const float RG = 6360.0, RB = 6361.5, RT = 6364.0;
float rs(vec3 ro, vec3 rd, float r) { float b = dot(ro, rd), c = dot(ro, ro) - r * r, d = b * b - c; if (d < 0.0) return -1.0; d = sqrt(d); float t1 = -b - d, t2 = -b + d; return t1 > 0.0 ? t1 : t2; }
float remap(float v, float a, float b, float c, float d) { return c + (v - a) / (b - a) * (d - c); }
float density(vec3 p, bool detail) {
  float r = length(p), hf = (r - RB) / (RT - RB);
  if (hf < 0.0 || hf > 1.0) return 0.0;
  float cov = coverage(p.xz);
  if (cov < 0.01) return 0.0;
  cov = sqrt(cov);                                                        // покриття «пухкіше»: помірна хмарність — справді кілька хмар
  float prof = smoothstep(0.0, 0.1, hf) * smoothstep(1.0, mix(0.3, 0.6, cov), hf);   // купчастий профіль
  vec3 sp = vec3(p.x, r - RG, p.z) / 4.5 + vec3(uWind.x, 0.0, uWind.y) * uTime / 4.5;
  vec4 s = texture(tShape, sp);
  float low = s.g * 0.625 + s.b * 0.25 + s.a * 0.125;
  float base = remap(s.r, -(1.0 - low), 1.0, 0.0, 1.0) * prof;
  float d = clamp(remap(base, 1.0 - cov, 1.0, 0.0, 1.0), 0.0, 1.0) * cov;
  if (detail && d > 0.0) {
    vec3 dn = texture(tDetail, sp * 6.0).rgb;
    float hfn = dn.r * 0.625 + dn.g * 0.25 + dn.b * 0.125;
    hfn = mix(hfn, 1.0 - hfn, clamp(hf * 6.0, 0.0, 1.0));                            // унизу — пасма, вгорі — «капуста»
    d = clamp(remap(d, hfn * 0.38, 1.0, 0.0, 1.0), 0.0, 1.0);
  }
  return d * uDens;
}
float hg(float c, float g) { float g2 = g * g; return (1.0 - g2) / (4.0 * 3.14159 * pow(1.0 + g2 - 2.0 * g * c, 1.5)); }
float ign(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }
void main() {
  vec4 vv = uInvProj * vec4(vUv * 2.0 - 1.0, 1.0, 1.0);
  vec3 dir = normalize((uCamWorld * vec4(normalize(vv.xyz / vv.w), 0.0)).xyz);
  vec4 res = vec4(0.0);
  if (dir.y > -0.03) {
    vec3 ro = vec3(uCamPos.x * 1e-3, RG + 0.2 + uCamPos.y * 1e-3, uCamPos.z * 1e-3);
    float t0 = rs(ro, dir, RB), t1 = rs(ro, dir, RT);
    t1 = min(t1, t0 + 22.0);
    if (t0 > 0.0 && t0 < 70.0 && t1 > t0) {
      float n = uSteps, ds = (t1 - t0) / n;
      float t = t0 + ds * fract(ign(gl_FragCoord.xy) + uFrame * 0.618034);
      float cs = dot(dir, uKeyDir);
      float ph = mix(hg(cs, 0.72), hg(cs, -0.2), 0.3);
      vec3 T = vec3(1.0), col = vec3(0.0);
      float firstHit = -1.0;
      for (int i = 0; i < 128; i++) {
        if (float(i) >= n || T.g < 0.03) break;
        vec3 p = ro + dir * t;
        float d = density(p, uDetail > 0.5);
        if (d > 0.001) {
          if (firstHit < 0.0) firstHit = t;
          // світло: проби в бік світила, крок росте
          float dl = 0.0, ls = 0.09;
          for (int j = 0; j < 8; j++) { if (float(j) >= uLightSteps) break; ls *= 1.55; dl += density(p + uKeyDir * ls, false) * ls; }
          const float SIG = 22.0;                                                  // 1/км на одиницю густини
          float hf = (length(p) - RB) / (RT - RB);
          vec3 lum = vec3(0.0); float a = 1.0, b = 1.0, c = 1.0;
          for (int o = 0; o < 4; o++) {                                             // багатократне розсіювання (Wrenninge: октави з меншим згасанням)
            lum += uSunCol * a * mix(ph, 0.08, float(o) * 0.3) * exp(-dl * SIG * 0.45 * c);
            a *= 0.62; c *= 0.3;
          }
          lum *= 1.0 - exp(-d * SIG * 0.4) * 0.35;                                   // «порох»: трохи темніший край з боку світла
          lum += mix(uAmbBot, uAmbTop, hf) * (0.55 + 0.45 * hf);
          vec3 ext = vec3(d * SIG);
          vec3 st = exp(-ext * ds);
          col += T * (lum - lum * st);                                             // енергетично вірне інтегрування
          T *= st;
        }
        t += ds * (d > 0.001 ? 1.0 : 1.6);
      }
      float a = 1.0 - T.g;
      if (a > 0.0) {
        float fade = exp(-max(firstHit, 0.0) * 0.045);                             // повітряна перспектива
        col = col * fade + skyLUT(dir) * a * (1.0 - fade);
      }
      res = vec4(col, a);
    }
  }
  // накопичення: попередній кадр за тим самим напрямом
  if (uHasPrev > 0.5) {
    vec4 cp = uPrevVP * vec4(dir, 0.0);
    vec2 pu = cp.xy / cp.w * 0.5 + 0.5;
    if (cp.w > 0.0 && pu.x > 0.0 && pu.x < 1.0 && pu.y > 0.0 && pu.y < 1.0) res = mix(res, texture2D(tPrev, pu), uBlend);
  }
  gl_FragColor = res;
}`;

export class Clouds {
  constructor(THREE, renderer) {
    this.T = THREE; this.r = renderer;
    const T = THREE;
    this.cam = new T.OrthographicCamera(-1, 1, 1, -1, 0, 1);
    this.quad = new T.Mesh(new T.PlaneGeometry(2, 2)); this.quad.frustumCulled = false;
    this.qs = new T.Scene(); this.qs.add(this.quad);
    this.bake();
    this.mat = new T.ShaderMaterial({
      uniforms: {
        tShape: { value: this.shape.texture }, tDetail: { value: this.detail.texture }, tPrev: { value: null },
        tSkyView: { value: null }, uKeyDir: { value: new T.Vector3(0, 1, 0) }, uKeyE: { value: 1 },
        uInvProj: { value: new T.Matrix4() }, uCamWorld: { value: new T.Matrix4() }, uPrevVP: { value: new T.Matrix4() },
        uCamPos: { value: new T.Vector3() }, uSunCol: { value: new T.Color() }, uAmbTop: { value: new T.Color() }, uAmbBot: { value: new T.Color() },
        uCover: { value: 0.4 }, uTime: { value: 0 }, uFrame: { value: 0 }, uSteps: { value: 48 }, uLightSteps: { value: 5 }, uDetail: { value: 1 },
        uBlend: { value: 0.85 }, uHasPrev: { value: 0 }, uDens: { value: 2.2 }, uWind: { value: new T.Vector2(0.008, 0.003) },
      },
      vertexShader: VERT, fragmentShader: CLOUD_FRAG, depthTest: false, depthWrite: false,
    });
    this.frame = 0; this.rt = []; this.cur = 0; this.prevVP = new T.Matrix4(); this.hasPrev = false;
    this.scale = 0.25;
    this._rot = new T.Matrix4();
  }
  /** Шум — при завантаженні й ще раз після відновлення WebGL-контексту (вміст цілей тоді пропадає). */
  bake() {
    const t0 = performance.now();
    if (this.shape) { this.shape.dispose(); this.detail.dispose(); }
    this.shape = this._bake(128, 0);
    this.detail = this._bake(32, 1);
    if (this.mat) { this.mat.uniforms.tShape.value = this.shape.texture; this.mat.uniforms.tDetail.value = this.detail.texture; }
    this.hasPrev = false;
    this.bakeMs = Math.round(performance.now() - t0);
  }
  _bake(N, kind) {
    const T = this.T;
    const rt = new T.WebGL3DRenderTarget(N, N, N, { depthBuffer: false });
    rt.texture.format = T.RGBAFormat; rt.texture.type = T.UnsignedByteType;
    rt.texture.minFilter = rt.texture.magFilter = T.LinearFilter;
    rt.texture.wrapS = rt.texture.wrapT = rt.texture.wrapR = T.RepeatWrapping;
    const m = new T.ShaderMaterial({ uniforms: { uZ: { value: 0 }, uN: { value: N }, uKind: { value: kind } }, vertexShader: VERT, fragmentShader: NOISE_FRAG, depthTest: false, depthWrite: false });
    this.quad.material = m;
    const prev = this.r.getRenderTarget();
    for (let z = 0; z < N; z++) { m.uniforms.uZ.value = z; this.r.setRenderTarget(rt, z); this.r.render(this.qs, this.cam); }
    this.r.setRenderTarget(prev);
    m.dispose();
    return rt;
  }
  /** Розмір цілі хмар — частка роздільності екрана (¼ — Low, ½ — High). */
  setSize(W, H, scale) {
    const T = this.T;
    this.scale = scale;
    for (const r of this.rt) r.dispose();
    const w = Math.max(16, Math.round(W * scale)), h = Math.max(16, Math.round(H * scale));
    this.rt = [0, 1].map(() => new T.WebGLRenderTarget(w, h, { type: T.HalfFloatType, depthBuffer: false, minFilter: T.LinearFilter, magFilter: T.LinearFilter }));
    this.hasPrev = false;
  }
  get texture() { return this.rt[this.cur].texture; }
  dispose() {
    for (const r of [this.shape, this.detail, ...this.rt]) if (r) r.dispose();
    this.rt = [];
    this.mat.dispose(); this.quad.geometry.dispose();
  }
  /** Кадр хмар для камери cam. o: { sky (Atmosphere), keyDir, keyE, sunCol, ambTop, ambBot, cover, time, steps, lightSteps, detail, dens }. */
  render(cam, o) {
    const u = this.mat.uniforms;
    u.tSkyView.value = o.sky.view.texture; u.uKeyDir.value.copy(o.keyDir); u.uKeyE.value = o.keyE;
    u.uInvProj.value.copy(cam.projectionMatrixInverse); u.uCamWorld.value.copy(cam.matrixWorld); u.uCamPos.value.copy(cam.position);
    u.uSunCol.value.copy(o.sunCol); u.uAmbTop.value.copy(o.ambTop); u.uAmbBot.value.copy(o.ambBot);
    u.uCover.value = o.cover; u.uTime.value = o.time; u.uSteps.value = o.steps; u.uLightSteps.value = o.lightSteps; u.uDetail.value = o.detail ? 1 : 0; u.uDens.value = o.dens || 2.2;
    u.uFrame.value = this.frame++ % 64;
    const prev = this.rt[this.cur], next = this.rt[this.cur ^ 1];
    u.tPrev.value = prev.texture; u.uHasPrev.value = this.hasPrev ? 1 : 0; u.uPrevVP.value.copy(this.prevVP); u.uBlend.value = o.blend ?? 0.85;
    this.quad.material = this.mat;
    const pr = this.r.getRenderTarget();
    this.r.setRenderTarget(next); this.r.render(this.qs, this.cam); this.r.setRenderTarget(pr);
    this.cur ^= 1; this.hasPrev = true;
    // «поворот без зсуву» поточної камери — для перепроєкції наступного кадру
    const rot = this._rot.extractRotation(cam.matrixWorld).invert();
    this.prevVP.multiplyMatrices(cam.projectionMatrix, rot);
  }
}
