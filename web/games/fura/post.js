// Пост-обробка «Чумаків» 0.0.5 (STYLE.md §15.3, §17.3; перенесено з проби 3–4 — fura/style: post3.js). Сцена малюється
// в HDR-ціль (масштаб рендеру, піксель ÷px з полем 1 тексель довкола, за бажанням — MSAA 4×), uber-прохід: FXAA (лише
// px = 1 і без MSAA) → bloom → експозиція → Neutral або AgX → LUT пори доби → віньєтка → дизеринг проти смуг (або
// палітра з упорядкованим дизерингом) → на екран; зі зниженням — через ціль низької роздільності й «блит» (піксель —
// найближчим текселем зі зсувом на дробову частину: камера прив'язана до сітки текселів, див. sky.js).

const VERT = 'varying vec2 vUv; void main(){ vUv = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }';

const UBER = `
precision highp sampler3D;
uniform sampler2D tColor, tBloom;
uniform sampler3D tLut;
uniform vec2 px;
uniform float uExposure, uTone, uVig, uGrain, uTime, uBloom, uLutAmt, uFxaa, uQuant, uDither;
varying vec2 vUv;

float lum(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }
float tl(vec3 c) { float l = lum(c); return l / (1.0 + l); }
vec3 col(vec2 uv) { return texture2D(tColor, uv).rgb; }
vec3 fxaa(vec2 uv) {
  vec3 cNW = col(uv + vec2(-1.0, -1.0) * px), cNE = col(uv + vec2(1.0, -1.0) * px);
  vec3 cSW = col(uv + vec2(-1.0, 1.0) * px), cSE = col(uv + vec2(1.0, 1.0) * px), cM = col(uv);
  float lNW = tl(cNW), lNE = tl(cNE), lSW = tl(cSW), lSE = tl(cSE), lM = tl(cM);
  float lMin = min(lM, min(min(lNW, lNE), min(lSW, lSE))), lMax = max(lM, max(max(lNW, lNE), max(lSW, lSE)));
  if (lMax - lMin < max(0.03, lMax * 0.12)) return cM;
  vec2 dir = vec2(-((lNW + lNE) - (lSW + lSE)), (lNW + lSW) - (lNE + lSE));
  float red = max((lNW + lNE + lSW + lSE) * 0.03125, 1.0 / 128.0);
  dir = clamp(dir / (min(abs(dir.x), abs(dir.y)) + red), vec2(-8.0), vec2(8.0)) * px;
  vec3 a = 0.5 * (col(uv + dir * (1.0 / 3.0 - 0.5)) + col(uv + dir * (2.0 / 3.0 - 0.5)));
  vec3 b = a * 0.5 + 0.25 * (col(uv + dir * -0.5) + col(uv + dir * 0.5));
  float lb = tl(b);
  return (lb < lMin || lb > lMax) ? a : b;
}

vec3 neutral(vec3 c) {                                                       // Khronos PBR Neutral
  const float s = 0.76, dd = 0.15; float x = min(c.r, min(c.g, c.b));
  float o = x < 0.08 ? x - 6.25 * x * x : 0.04; c -= o; float p = max(c.r, max(c.g, c.b));
  if (p < s) return c; float d2 = 1.0 - s; float np = 1.0 - d2 * d2 / (p + d2 - s); c *= np / p;
  float g = 1.0 - 1.0 / (dd * (p - np) + 1.0); return mix(c, vec3(np), g);
}
vec3 srgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(0.0031308, c)); }
vec3 agxCurve(vec3 x) { vec3 x2 = x * x, x4 = x2 * x2;
  return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232; }
vec3 agx(vec3 v) {
  const mat3 m = mat3(0.842479062253094, 0.0423282422610123, 0.0423756549057051, 0.0784335999999992, 0.878468636469772, 0.0784336,
                      0.0792237451477643, 0.0791661274605434, 0.879142973793104);
  const mat3 mi = mat3(1.19687900512017, -0.0528968517574562, -0.0529716355144438, -0.0980208811401368, 1.15190312990417, -0.0980434501171241,
                       -0.0990297440797205, -0.0989611768448433, 1.15107367264116);
  const float lo = -12.47393, hi = 4.026069;
  v = m * max(v, vec3(1e-10)); v = (clamp(log2(v), lo, hi) - lo) / (hi - lo); v = agxCurve(v);
  float l = lum(v); v = pow(max(v, 0.0), vec3(1.25)); v = vec3(l) + 1.25 * (v - vec3(l));
  return mi * v;
}
float bayer4(vec2 p) {
  ivec2 q = ivec2(mod(p, 4.0));
  int i = q.x + q.y * 4;
  float m[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
  return (m[i] + 0.5) / 16.0;
}

void main() {
  vec3 c = uFxaa > 0.5 ? fxaa(vUv) : col(vUv);
  c += texture2D(tBloom, vUv).rgb * uBloom;
  c *= uExposure;
  vec3 d = uTone > 0.5 ? agx(c) : srgb(clamp(neutral(c), 0.0, 1.0));
  d = mix(d, texture(tLut, clamp(d, 0.0, 1.0) * (31.0 / 32.0) + 0.5 / 32.0).rgb, uLutAmt);
  vec2 q = vUv - 0.5; d *= 1.0 - uVig * dot(q, q) * 2.2;
  if (uQuant > 1.5) d = floor(clamp(d, 0.0, 1.0) * (uQuant - 1.0) + bayer4(gl_FragCoord.xy)) / (uQuant - 1.0);   // палітра з дизерингом
  else if (uDither > 0.5) d += (bayer4(gl_FragCoord.xy) - 0.5) * (1.5 / 255.0);                                // лише проти смуг
  if (uGrain > 0.0) d += (fract(sin(dot(gl_FragCoord.xy + uTime * 61.7, vec2(12.9898, 78.233))) * 43758.5453) - 0.5) * uGrain;
  gl_FragColor = vec4(clamp(d, 0.0, 1.0), 1.0);
}`;

const BLIT = `uniform sampler2D t; uniform vec2 uSrc, uInner, uOff; varying vec2 vUv;
void main() { gl_FragColor = texture2D(t, (vUv * uInner + 1.0 + uOff) / uSrc); }`;

const PRE = `uniform sampler2D t; uniform vec2 px; uniform float uThr, uKnee; varying vec2 vUv;
void main() {
  vec3 c = 0.25 * (texture2D(t, vUv + vec2(-1.0, -1.0) * px).rgb + texture2D(t, vUv + vec2(1.0, -1.0) * px).rgb
                 + texture2D(t, vUv + vec2(-1.0, 1.0) * px).rgb + texture2D(t, vUv + vec2(1.0, 1.0) * px).rgb);
  float l = max(c.r, max(c.g, c.b));
  float s = clamp(l - uThr + uKnee, 0.0, 2.0 * uKnee); s = s * s / (4.0 * uKnee + 1e-4);
  gl_FragColor = vec4(c * max(s, l - uThr) / max(l, 1e-4), 1.0);
}`;
const DOWN = `uniform sampler2D t; uniform vec2 px; varying vec2 vUv;
void main() {
  gl_FragColor = vec4(0.25 * (texture2D(t, vUv + vec2(-1.0, -1.0) * px).rgb + texture2D(t, vUv + vec2(1.0, -1.0) * px).rgb
                            + texture2D(t, vUv + vec2(-1.0, 1.0) * px).rgb + texture2D(t, vUv + vec2(1.0, 1.0) * px).rgb), 1.0);
}`;
const UP = `uniform sampler2D t; uniform vec2 px; varying vec2 vUv;
void main() {
  vec3 c = texture2D(t, vUv).rgb * 4.0;
  c += (texture2D(t, vUv + vec2(px.x, 0.0)).rgb + texture2D(t, vUv - vec2(px.x, 0.0)).rgb
      + texture2D(t, vUv + vec2(0.0, px.y)).rgb + texture2D(t, vUv - vec2(0.0, px.y)).rgb) * 2.0;
  c += texture2D(t, vUv + px).rgb + texture2D(t, vUv - px).rgb + texture2D(t, vUv + vec2(px.x, -px.y)).rgb + texture2D(t, vUv + vec2(-px.x, px.y)).rgb;
  gl_FragColor = vec4(c / 16.0, 1.0);
}`;

export class Post {
  constructor(THREE, renderer) {
    this.T = THREE; this.r = renderer;
    this.cam = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
    this.quad = new THREE.Mesh(new THREE.PlaneGeometry(2, 2)); this.quad.frustumCulled = false;
    this.qs = new THREE.Scene(); this.qs.add(this.quad);
    const sm = (frag, uniforms, extra) => new THREE.ShaderMaterial({ uniforms, vertexShader: VERT, fragmentShader: frag, depthTest: false, depthWrite: false, ...extra });
    this.black = new THREE.DataTexture(new Uint8Array([0, 0, 0, 255]), 1, 1); this.black.needsUpdate = true;
    const f = (v) => ({ value: v });
    this.uber = sm(UBER, {
      tColor: f(null), tBloom: f(this.black), tLut: f(null), px: f(new THREE.Vector2()),
      uExposure: f(1), uTone: f(0), uVig: f(0.18), uGrain: f(0),
      uTime: f(0), uBloom: f(0), uLutAmt: f(1), uFxaa: f(1), uQuant: f(0), uDither: f(1),
    });
    this.blit = sm(BLIT, { t: f(null), uSrc: f(new THREE.Vector2()), uInner: f(new THREE.Vector2()), uOff: f(new THREE.Vector2()) });
    this.pre = sm(PRE, { t: f(null), px: f(new THREE.Vector2()), uThr: f(1.0), uKnee: f(0.5) });
    this.down = sm(DOWN, { t: f(null), px: f(new THREE.Vector2()) });
    this.up = sm(UP, { t: f(null), px: f(new THREE.Vector2()) }, { blending: THREE.AdditiveBlending, transparent: true });
    this.bloomOn = false;
  }
  /** W×H — екран (canvas), pxs — розмір «пікселя» (1…4), scale — масштаб рендеру (0,5…1; збільшення плавне),
   *  msaa — 4× на цілі сцени (лише без пікселя). Ціль сцени — (W·scale/pxs + 2)×(H·scale/pxs + 2), коли є зниження. */
  setSize(W, H, pxs, scale = 1, msaa = false) {
    const T = this.T;
    if (this.rt) { this.rt.dispose(); this.ldr.dispose(); }
    for (const b of this.chain || []) b.dispose();
    this.W = W; this.H = H; this.pxs = pxs; this.scale = scale; this.msaa = msaa && pxs === 1;
    this.low = pxs > 1 || scale < 0.999;
    this.wl = Math.floor(W * scale / pxs); this.hl = Math.floor(H * scale / pxs);
    const m = this.low ? 2 : 0, w = this.wl + m, h = this.hl + m;
    this.rw = w; this.rh = h;
    this.rt = new T.WebGLRenderTarget(w, h, { type: T.HalfFloatType, samples: this.msaa ? 4 : 0 });
    const filt = pxs > 1 ? T.NearestFilter : T.LinearFilter;           // піксель — чіткий, масштаб рендеру — плавний
    this.ldr = new T.WebGLRenderTarget(w, h, { depthBuffer: false, minFilter: filt, magFilter: filt });
    this.chain = [];
    for (let k = 1; k <= 4; k++) this.chain.push(new T.WebGLRenderTarget(Math.max(1, w >> k), Math.max(1, h >> k), { type: T.HalfFloatType, depthBuffer: false }));
    this.uber.uniforms.px.value.set(1 / w, 1 / h);
  }
  dispose() {
    if (this.rt) { this.rt.dispose(); this.ldr.dispose(); }
    for (const b of this.chain || []) b.dispose();
    for (const m of [this.uber, this.blit, this.pre, this.down, this.up, this.black, this.quad.geometry]) m.dispose();
    this.rt = null; this.chain = [];
  }
  _pass(mat, target, clear = true) {
    this.quad.material = mat;
    this.r.setRenderTarget(target);
    const ac = this.r.autoClear; this.r.autoClear = clear;
    this.r.render(this.qs, this.cam);
    this.r.autoClear = ac;
  }
  /** Після рендеру сцени в this.rt. off — дробовий зсув камери в текселях (піксельний режим). */
  finish(time, off) {
    const u = this.uber.uniforms;
    if (this.bloomOn) {
      const c = this.chain;
      this.pre.uniforms.t.value = this.rt.texture; this.pre.uniforms.px.value.set(1 / this.rw, 1 / this.rh);
      this._pass(this.pre, c[0]);
      for (let k = 1; k < c.length; k++) { this.down.uniforms.t.value = c[k - 1].texture; this.down.uniforms.px.value.set(1 / c[k - 1].width, 1 / c[k - 1].height); this._pass(this.down, c[k]); }
      for (let k = c.length - 1; k > 0; k--) { this.up.uniforms.t.value = c[k].texture; this.up.uniforms.px.value.set(1 / c[k].width, 1 / c[k].height); this._pass(this.up, c[k - 1], false); }
      u.tBloom.value = c[0].texture;
    } else u.tBloom.value = this.black;
    u.tColor.value = this.rt.texture; u.uTime.value = time;
    if (this.low) {
      this._pass(this.uber, this.ldr);
      const b = this.blit.uniforms;
      b.t.value = this.ldr.texture; b.uSrc.value.set(this.rw, this.rh); b.uInner.value.set(this.wl, this.hl); b.uOff.value.set(off ? off.x : 0, off ? off.y : 0);
      this._pass(this.blit, null);
    } else this._pass(this.uber, null);
  }
}
