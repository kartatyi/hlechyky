// Фізичне небо «Чумаків» 0.0.5 (STYLE.md §17.1) — за ідеєю Hillaire 2020 («A Scalable and Production Ready Sky and
// Atmosphere Rendering Technique»), своїм кодом і спрощено: Релеївське (блакить) і Мі-розсіювання (серпанок, ореол
// сонця), озон (синява сутінків), планета-куля радіусом 6360 км. Дві маленькі таблиці на GPU:
//   • пропускання (256×64) — скільки світла доходить крізь атмосферу з висоти h під кутом; рахується раз;
//   • вигляд неба (192×108) — яскравість неба за напрямом від камери; перераховується, коли рушить сонце.
// Небо на куполі, колір туману (повітряна перспектива), колір сонця й заповнення — усе з цих таблиць.
// Одиниці — кілометри. Перенесено з проби 4 (fura/style: atmo4.js) з двома змінами для великого краю: туман
// доводиться до кольору неба на межі дальності (uFogFar — чанки за нею ховаються непомітно), а купол під обрієм
// показує серпанок обрію, а не «землю атмосфери» (край рельєфу не має темної смуги).

const ATMO_GLSL = `
const float Rg = 6360.0, Rt = 6460.0;
const vec3 RAY_S = vec3(5.802, 13.558, 33.1) * 1e-3;
const float MIE_S = 3.996e-3, MIE_E = 4.44e-3;
const vec3 OZ_A = vec3(0.650, 1.881, 0.085) * 1e-3;
const float PI_A = 3.14159265;
void medium(float h, out vec3 sR, out float sM, out vec3 ext) {
  float dR = exp(-h / 8.0), dM = exp(-h / 1.2), dO = max(0.0, 1.0 - abs(h - 25.0) / 15.0);
  sR = RAY_S * dR; sM = MIE_S * dM; ext = RAY_S * dR + vec3(MIE_E * dM) + OZ_A * dO;
}
// відстань уздовж променя до сфери радіуса r (зсередини — вихід; ззовні — вхід), −1 — нема
float raySphere(vec3 ro, vec3 rd, float r) {
  float b = dot(ro, rd), c = dot(ro, ro) - r * r, d = b * b - c;
  if (d < 0.0) return -1.0;
  d = sqrt(d);
  float t1 = -b - d, t2 = -b + d;
  return t1 > 0.0 ? t1 : t2;
}
float phaseR(float c) { return 3.0 / (16.0 * PI_A) * (1.0 + c * c); }
float phaseM(float c) { const float g = 0.8; float g2 = g * g; return 3.0 / (8.0 * PI_A) * ((1.0 - g2) * (1.0 + c * c)) / ((2.0 + g2) * pow(1.0 + g2 - 2.0 * g * c, 1.5)); }
// таблиця пропускання: u — косинус зенітного кута (−0,25…1), v — висота (0…100 км, квадратично)
vec2 tUV(float h, float mu) { return vec2(clamp((mu + 0.25) / 1.25, 0.0, 1.0), sqrt(clamp(h / 100.0, 0.0, 1.0))); }
`;

const T_FRAG = ATMO_GLSL + `
varying vec2 vUv;
void main() {
  float mu = vUv.x * 1.25 - 0.25, h = vUv.y * vUv.y * 100.0;
  vec3 ro = vec3(0.0, Rg + h, 0.0), rd = vec3(sqrt(max(0.0, 1.0 - mu * mu)), mu, 0.0);
  if (raySphere(ro, rd, Rg) > 0.0 && mu < 0.0) { gl_FragColor = vec4(0.0, 0.0, 0.0, 1.0); return; }
  float L = raySphere(ro, rd, Rt), dt = L / 40.0;
  vec3 od = vec3(0.0);
  for (int i = 0; i < 40; i++) {
    vec3 p = ro + rd * (float(i) + 0.5) * dt;
    vec3 sR, ext; float sM;
    medium(length(p) - Rg, sR, sM, ext);
    od += ext * dt;
  }
  gl_FragColor = vec4(exp(-od), 1.0);
}`;

const SKY_FRAG = ATMO_GLSL + `
uniform sampler2D tT;
uniform vec3 uKeyDir;        // напрям на ключове світило (сонце, а глибокої ночі — місяць)
uniform float uCamH, uMS;
varying vec2 vUv;
vec3 trans(float h, float mu) { return texture2D(tT, tUV(h, mu)).rgb; }
void main() {
  float az = vUv.x * 2.0 * PI_A;
  float vv = vUv.y * 2.0 - 1.0;
  float el = sign(vv) * vv * vv * PI_A * 0.5;                     // густіше біля обрію
  vec3 rd = vec3(cos(el) * cos(az), sin(el), cos(el) * sin(az));
  float kel = asin(clamp(uKeyDir.y, -1.0, 1.0));
  vec3 sun = vec3(cos(kel), sin(kel), 0.0);                       // у кадрі таблиці азимут сонця = 0
  vec3 ro = vec3(0.0, Rg + uCamH, 0.0);
  float tG = raySphere(ro, rd, Rg), tA = raySphere(ro, rd, Rt);
  float L = (tG > 0.0) ? tG : tA;
  const int N = 30;
  float cs = dot(rd, sun), pR = phaseR(cs), pM = phaseM(cs);
  vec3 lum = vec3(0.0), T = vec3(1.0);
  float t0 = 0.0;
  for (int i = 0; i < N; i++) {
    float t1 = L * pow((float(i) + 1.0) / float(N), 2.0);           // крок росте від камери
    float dt = t1 - t0, tm = t0 + dt * 0.5; t0 = t1;
    vec3 p = ro + rd * tm;
    float r = length(p), h = r - Rg;
    vec3 sR, ext; float sM;
    medium(h, sR, sM, ext);
    vec3 Ts = trans(h, dot(p / r, sun));
    vec3 S = (sR * pR + sM * pM) * Ts + (sR + vec3(sM)) * uMS * Ts.g; // однократне + грубе багатократне
    vec3 Tst = exp(-ext * dt);
    lum += T * (S - S * Tst) / max(ext, vec3(1e-6));
    T *= Tst;
  }
  if (tG > 0.0) lum += T * vec3(0.12, 0.11, 0.09) * trans(0.0, max(sun.y, 0.0)) * max(sun.y, 0.0) * 0.3;   // відблиск землі
  gl_FragColor = vec4(lum, 1.0);
}`;

// Вибірка з таблиці вигляду неба за світовим напрямом (азимут — відносно сонця)
export const SKYLUT_GLSL = `
uniform sampler2D tSkyView;
uniform vec3 uKeyDir;
uniform float uKeyE;
vec3 skyLUT(vec3 d) {
  float el = asin(clamp(d.y, -1.0, 1.0));
  vec2 kz = normalize(uKeyDir.xz + vec2(1e-6)), dz = normalize(d.xz + vec2(1e-6));
  float az = atan(kz.x * dz.y - kz.y * dz.x, dot(kz, dz));
  if (az < 0.0) az += 6.2831853;
  float v = 0.5 + 0.5 * sign(el) * sqrt(abs(el) / 1.5707963);
  return texture2D(tSkyView, vec2(az / 6.2831853, v)).rgb * uKeyE;
}`;

const VERT = 'varying vec2 vUv; void main(){ vUv = uv; gl_Position = vec4(position.xy, 0.0, 1.0); }';

export class Atmosphere {
  constructor(THREE, renderer) {
    this.T = THREE; this.r = renderer;
    const T = THREE;
    this.cam = new T.OrthographicCamera(-1, 1, 1, -1, 0, 1);
    this.quad = new T.Mesh(new T.PlaneGeometry(2, 2)); this.quad.frustumCulled = false;
    this.qs = new T.Scene(); this.qs.add(this.quad);
    const rtOpt = { type: T.HalfFloatType, depthBuffer: false, minFilter: T.LinearFilter, magFilter: T.LinearFilter };
    this.trans = new T.WebGLRenderTarget(256, 64, rtOpt);
    this.view = new T.WebGLRenderTarget(192, 108, { ...rtOpt, wrapS: T.RepeatWrapping });
    this.view.texture.wrapS = T.RepeatWrapping;
    this.tMat = new T.ShaderMaterial({ vertexShader: VERT, fragmentShader: T_FRAG, depthTest: false, depthWrite: false });
    this.sMat = new T.ShaderMaterial({
      uniforms: { tT: { value: this.trans.texture }, uKeyDir: { value: new T.Vector3(0, 1, 0) }, uCamH: { value: 0.2 }, uMS: { value: 0.18 } },
      vertexShader: VERT, fragmentShader: SKY_FRAG, depthTest: false, depthWrite: false,
    });
    // опромінення від неба (для заповнення): 1×1, середнє за верхньою півсферою з косинусом
    this.irr = new T.WebGLRenderTarget(1, 1, { type: T.FloatType, depthBuffer: false });
    this.iMat = new T.ShaderMaterial({
      uniforms: { tSkyView: { value: this.view.texture }, uKeyDir: { value: new T.Vector3(0, 1, 0) }, uKeyE: { value: 1 } },
      vertexShader: VERT, fragmentShader: SKYLUT_GLSL + `
        void main() { vec3 s = vec3(0.0); float w = 0.0;
          for (int i = 0; i < 12; i++) for (int j = 1; j < 7; j++) {
            float a = float(i) / 12.0 * 6.2831853, e = float(j) / 7.0 * 1.5707963;
            vec3 d = vec3(cos(e) * cos(a), sin(e), cos(e) * sin(a)); float c = d.y;
            s += skyLUT(d) * c; w += c; }
          gl_FragColor = vec4(s / w, 1.0); }`,
      depthTest: false, depthWrite: false,
    });
    this.px = new Float32Array(4);
    this.bake();
    this.ms = 0;
  }
  /** Таблиця пропускання — раз (і ще раз після відновлення WebGL-контексту: вміст цілей тоді пропадає). */
  bake() { this._pass(this.tMat, this.trans); }
  _pass(mat, target) { this.quad.material = mat; const prev = this.r.getRenderTarget(); this.r.setRenderTarget(target); this.r.render(this.qs, this.cam); this.r.setRenderTarget(prev); }
  /** Перерахувати вигляд неба для напряму ключового світила; повертає опромінення неба (лінійне RGB на одиницю світила). */
  update(keyDir) {
    const t0 = performance.now();
    this.sMat.uniforms.uKeyDir.value.copy(keyDir);
    this._pass(this.sMat, this.view);
    this.iMat.uniforms.uKeyDir.value.copy(keyDir);
    this._pass(this.iMat, this.irr);
    this.r.readRenderTargetPixels(this.irr, 0, 0, 1, 1, this.px);
    this.ms = performance.now() - t0;
    return new this.T.Color(this.px[0], this.px[1], this.px[2]);
  }
}

/** Пропускання атмосфери до сонця з поверхні (CPU, та сама модель) — колір сонячного світла на землі. */
export function sunTransmittance(THREE, mu) {
  if (mu < -0.12) return new THREE.Color(0, 0, 0);
  const Rg = 6360, Rt = 6460, h0 = 0.2;
  const ro = [0, Rg + h0, 0], rd = [Math.sqrt(Math.max(0, 1 - mu * mu)), mu, 0];
  const b = ro[1] * rd[1], c = ro[1] * ro[1] - Rt * Rt, L = -b + Math.sqrt(b * b - c);
  const n = 60, dt = L / n, od = [0, 0, 0];
  for (let i = 0; i < n; i++) {
    const t = (i + 0.5) * dt, x = rd[0] * t, y = ro[1] + rd[1] * t, h = Math.hypot(x, y) - Rg;
    if (h < 0) return new THREE.Color(0, 0, 0);
    const dR = Math.exp(-h / 8), dM = Math.exp(-h / 1.2), dO = Math.max(0, 1 - Math.abs(h - 25) / 15);
    od[0] += (5.802e-3 * dR + 4.44e-3 * dM + 0.65e-3 * dO) * dt;
    od[1] += (13.558e-3 * dR + 4.44e-3 * dM + 1.881e-3 * dO) * dt;
    od[2] += (33.1e-3 * dR + 4.44e-3 * dM + 0.085e-3 * dO) * dt;
  }
  return new THREE.Color(Math.exp(-od[0]), Math.exp(-od[1]), Math.exp(-od[2]));
}

// ---------------------------------------------------------------- покриття хмар (спільне: хмари, їхні тіні)
export const COVER_GLSL = `
uniform float uCover, uTime;
uniform vec2 uWind;
float c4h(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
float c4n(vec2 p) { vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
  return mix(mix(c4h(i), c4h(i + vec2(1, 0)), f.x), mix(c4h(i + vec2(0, 1)), c4h(i + vec2(1, 1)), f.x), f.y); }
// погода: покриття 0…1 на площині (км), пливе з вітром (км/с)
float coverage(vec2 xz) {
  vec2 q = (xz + uWind * uTime) / 7.0;
  float n = c4n(q) * 0.55 + c4n(q * 2.1 + 3.1) * 0.3 + c4n(q * 4.3 + 7.7) * 0.15;
  float c = uCover * 1.35;                                    // 40 % → видимо «кілька хмар», 80 % → суцільно
  return clamp((n - (1.0 - c)) / max(c, 1e-3), 0.0, 1.0);
}`;

/** Спільні уніформи неба для купола, хмар і латки матеріалів. */
export function atmoUniforms(THREE) {
  const f = (v) => ({ value: v });
  return {
    tSkyView: f(null), uKeyDir: f(new THREE.Vector3(0, 1, 0)), uKeyE: f(1), tClouds: f(null), uRes: f(new THREE.Vector2(1, 1)),
    uSunDir: f(new THREE.Vector3(0, 1, 0)), uSunDisc: f(new THREE.Color()), uMoonDir: f(new THREE.Vector3(0, 1, 0)), uMoonVis: f(0), uStars: f(0),
    uCloudsOn: f(1), uCover: f(0.4), uTime: f(0), uWind: f(new THREE.Vector2(0.008, 0.003)), uCloudShadowAmt: f(0.7),
    uFogDens: f(0.0007), uFogStart: f(60), uFogFall: f(0.02), uFogFar: f(1600), uRim: f(0.35), uRimCol: f(new THREE.Color()),
  };
}

/** Купол: небо з таблиці + диск сонця + місяць і зорі + об'ємні хмари (ціль нижчої роздільності, за екранними UV). */
export function makeSky(THREE, U) {
  return new THREE.ShaderMaterial({
    uniforms: { ...U },
    vertexShader: 'varying vec3 vW; void main(){ vec4 w = modelMatrix * vec4(position,1.); vW = w.xyz; gl_Position = projectionMatrix * viewMatrix * w; gl_Position.z = gl_Position.w; }',
    fragmentShader: SKYLUT_GLSL + `
      uniform sampler2D tClouds;
      uniform vec2 uRes;
      uniform vec3 uSunDir, uSunDisc, uMoonDir;
      uniform float uMoonVis, uStars, uTime, uCloudsOn;
      varying vec3 vW;
      void main() {
        vec3 d = normalize(vW - cameraPosition);
        // під обрієм — серпанок обрію (як туман на краю рельєфу), а не темна «земля» атмосфери
        vec3 c = d.y > 0.0 ? skyLUT(d) : skyLUT(normalize(vec3(d.x, 0.01, d.z))), night = vec3(0.0);
        float sd = dot(d, uSunDir);
        vec3 disc = uSunDisc * smoothstep(0.99985, 0.99993, sd) * (0.6 + 0.4 * smoothstep(0.99985, 1.0, sd));   // край диска темніший
        if (uStars > 0.01 && d.y > 0.0) {
          vec3 q = floor(d * 230.0);
          float h = fract(sin(dot(q, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
          night += vec3(0.9, 0.95, 1.0) * step(0.9975, h) * (0.55 + 0.45 * sin(uTime * 2.3 + h * 77.0)) * uStars * smoothstep(0.0, 0.2, d.y) * 0.9;
        }
        float md = dot(d, uMoonDir);
        night += vec3(0.85, 0.9, 1.0) * smoothstep(0.99985, 0.99992, md) * uMoonVis * 1.6;
        if (uCloudsOn > 0.5) {
          vec4 cl = texture2D(tClouds, gl_FragCoord.xy / uRes);
          c = c * (1.0 - cl.a) + cl.rgb;
          disc *= 1.0 - cl.a;
          night *= 1.0 - smoothstep(0.02, 0.25, cl.a);                         // зорі й місяць — за хмарами (вночі експозиція велика — ховати рішуче)
        }
        c += night;
        gl_FragColor = vec4(c + disc, 1.0);
      }`,
    side: THREE.BackSide, depthWrite: false, fog: false,
  });
}

/**
 * Латка матеріалів (Standard, Lambert, Basic): тінь від хмар (покриття), rim, туман кольором неба з таблиці
 * (повітряна перспектива) — і на межі дальності uFogFar туман стає повним, тож чанки ховаються непомітно.
 */
export function patchAtmo(THREE, mat, U, tag = 'a') {
  mat.fog = false;
  let lfb = THREE.ShaderChunk.lights_fragment_begin;
  const at = lfb.indexOf('RE_Direct( directLight', lfb.indexOf('getDirectionalLightInfo'));
  lfb = 'float cloudSh = cloudShadow(vPW);\n' + lfb.slice(0, at) + 'directLight.color *= cloudSh;\n\t\t' + lfb.slice(at);
  const prev = mat.onBeforeCompile;
  mat.onBeforeCompile = (sh, r) => {
    if (prev) prev(sh, r);
    Object.assign(sh.uniforms, U);
    sh.vertexShader = sh.vertexShader
      .replace('#include <fog_pars_vertex>', 'varying vec3 vPW;\n#include <fog_pars_vertex>')
      .replace('#include <fog_vertex>', `#include <fog_vertex>
        { vec4 pw = vec4(transformed, 1.0);
          #ifdef USE_INSTANCING
            pw = instanceMatrix * pw;
          #endif
          vPW = (modelMatrix * pw).xyz; }`);
    sh.fragmentShader = sh.fragmentShader
      .replace('#include <fog_pars_fragment>', `varying vec3 vPW;
        uniform float uFogDens, uFogStart, uFogFall, uFogFar, uRim, uCloudShadowAmt;
        uniform vec3 uRimCol;
        ${SKYLUT_GLSL}
        ${COVER_GLSL}
        float cloudShadow(vec3 pw) {
          if (uCloudShadowAmt < 0.01 || uKeyDir.y < 0.03 || uCover < 0.01) return 1.0;
          vec3 p = pw * 1e-3;
          vec2 q = p.xz + uKeyDir.xz / max(uKeyDir.y, 0.15) * (2.4 - p.y);      // середина шару хмар
          return 1.0 - smoothstep(0.05, 0.55, coverage(q)) * uCloudShadowAmt;
        }`)
      .replace('#include <lights_fragment_begin>', lfb)
      .replace('#include <lights_fragment_end>', `#include <lights_fragment_end>
        { vec3 nw = normalize((vec4(normal, 0.0) * viewMatrix).xyz);
          float rim = pow(1.0 - saturate(dot(normal, normalize(vViewPosition))), 3.0) * (1.0 - abs(nw.y));
          reflectedLight.directDiffuse += uRimCol * rim * uRim * diffuseColor.rgb * (0.4 + 0.6 * cloudSh); }`)
      .replace('#include <fog_fragment>', `{
          vec3 dv = vPW - cameraPosition; float dist = length(dv); vec3 vd = dv / dist;
          float hf = exp(-max(vPW.y, 0.0) * uFogFall);
          float f = 1.0 - exp(-max(dist - uFogStart, 0.0) * uFogDens * mix(0.45, 1.0, hf));
          f = max(f, smoothstep(uFogFar * 0.62, uFogFar, dist));
          vec3 fc = skyLUT(normalize(vec3(vd.x, max(vd.y, 0.0) * 0.3 + 0.01, vd.z)));
          gl_FragColor.rgb = mix(gl_FragColor.rgb, fc, clamp(f, 0.0, 1.0));
        }`);
  };
  const k0 = mat.customProgramCacheKey && prev ? mat.customProgramCacheKey() : '';
  mat.customProgramCacheKey = () => k0 + '-atmo-' + tag;
  mat.needsUpdate = true;
  return mat;
}
