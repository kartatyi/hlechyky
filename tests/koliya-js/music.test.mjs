// K29: музика й тло — без вух, лише числа: лад, темп від небезпеки, детермінізм фраз, тиша без увімкнення.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SCALES, STYLE, TONIC, MIX, VOICE, AMB_LAYERS, ambienceFor, focusX, tempoOf, densityOf, dangerOf, speedOf, makePhrase, barEvents, cadence, degToMidi,
  createMusic } from '../../web/games/koliya/music.js';
import { createAudio } from '../../web/games/koliya/audio.js';
import { newGame, defaultParams, U, TPS } from '../../web/games/koliya/rules.js';

const inMode = (midi, tonic, scale) => SCALES[scale].includes((((midi - tonic) % 12) + 12) % 12);

test('мелодія й бас лежать у ладі (гуцульський / мінор чумаків), такти без дір і накладок', () => {
  for (const theme of ['koliya', 'chumak']) {
    const { scale, bpb } = STYLE[theme];
    for (let seed = 1; seed <= 40; seed++) for (let n = 0; n < 12; n++) for (const density of [0, 0.5, 1]) {
      const ph = makePhrase(seed * 7919, n, { theme, density });
      assert.equal(ph.scale, scale);
      for (const x of ph.mel) {
        assert.ok(inMode(x.midi, TONIC, scale), `${theme} ${seed}/${n}: мелодія ${x.midi} поза ладом`);
        assert.equal(x.midi, degToMidi(x.deg, scale));
        assert.ok(x.midi >= TONIC - 5 && x.midi <= TONIC + 17, `діапазон сопілки ${x.midi}`);
      }
      for (const x of ph.bass) assert.ok(inMode(x.midi, 38, scale), `${theme}: бас ${x.midi} поза ладом`);
      // мелодія — суцільна: кожна нота починається там, де скінчилась попередня, разом — 4 такти
      let at = 0;
      for (const x of ph.mel) { assert.equal(x.at, at, `${theme} ${seed}/${n}: дірка/накладка на ${at}`); at += x.len; }
      assert.equal(at, ph.bars * bpb);
      // фраза закінчується на тоніці (A′) або на V (A, B)
      const last = ph.mel[ph.mel.length - 1].deg;
      assert.equal(last, n % 4 === 1 || n % 4 === 3 ? 0 : 4);
    }
  }
});

test('гуцульська підвищена IV (G#) розв’язується на крок — угору до V чи вниз до III', () => {
  let seen = 0;
  for (let seed = 1; seed <= 60; seed++) for (let n = 0; n < 8; n++) {
    const m = makePhrase(seed, n, { density: 0.8 }).mel;
    for (let i = 0; i + 1 < m.length; i++) {
      if (((m[i].deg % 7) + 7) % 7 !== 3) continue;
      if (i + 1 === m.length - 1) continue;   // остання нота фрази примусова (V чи I)
      seen++;
      assert.equal(Math.abs(m[i + 1].deg - m[i].deg), 1, `G# → ${m[i + 1].deg}`);
    }
  }
  assert.ok(seen > 20, `G# трапляється (${seen})`);
});

test('фрази детерміновані від сіду; різні сіди — різні мелодії; період A·A′·B·A′', () => {
  const a = makePhrase(123, 5, { density: 0.6 }), b = makePhrase(123, 5, { density: 0.6 });
  assert.deepEqual(a, b);
  let diff = 0;
  for (let s = 1; s <= 30; s++) {
    const x = makePhrase(s, 0).mel.map((q) => q.midi + ':' + q.len).join(), y = makePhrase(s + 1000, 0).mel.map((q) => q.midi + ':' + q.len).join();
    if (x !== y) diff++;
  }
  assert.ok(diff >= 28, `різні сіди дають різне (${diff}/30)`);
  // A′ починається так само, як A (перші два такти)
  const A = makePhrase(77, 4), A2 = makePhrase(77, 5), B = makePhrase(77, 6);
  const head = (p) => p.mel.filter((x) => x.at < 2 * p.bpb).map((x) => x.midi + ':' + x.len).join();
  assert.equal(head(A), head(A2));
  assert.notEqual(head(A), head(B));
  // новий період — новий матеріал
  assert.notEqual(makePhrase(77, 0).mel.map((x) => x.midi).join(), makePhrase(77, 4).mel.map((x) => x.midi).join());
});

test('темп і щільність ростуть зі швидкістю й небезпекою; чумацька повільніша', () => {
  for (const theme of ['koliya', 'chumak']) {
    let prev = 0;
    for (let d = 0; d <= 1.0001; d += 0.1) { const t = tempoOf(theme, 0.3, d); assert.ok(t > prev); prev = t; }
    assert.ok(tempoOf(theme, 1, 0) > tempoOf(theme, 0, 0));
    assert.ok(densityOf(theme, 0.5, 1) > densityOf(theme, 0.5, 0));
  }
  assert.ok(tempoOf('chumak', 1, 1) < tempoOf('koliya', 0, 0), 'найшвидша чумацька повільніша за найспокійнішу колію');
  assert.ok(tempoOf('koliya', 1, 1) <= 160);
  // такт у секундах коротшає з небезпекою
  const ph = makePhrase(5, 0);
  const span = (d) => { const spe = 60 / tempoOf('koliya', 0.5, d) / 2; const ev = barEvents(ph, 1, spe, 10); return ev[ev.length - 1].t - 10 + spe; };
  assert.ok(span(1) < span(0));
  // щільність додає ноти й бубон
  const cnt = (d) => { let k = 0; for (let n = 0; n < 16; n++) { const p = makePhrase(9, n, { density: d }); k += p.mel.length + p.drum.length; } return k; };
  assert.ok(cnt(1) > cnt(0.2) * 1.3, `${cnt(1)} vs ${cnt(0.2)}`);
});

test('небезпека: пожежа, перегрів, рейки кінчаються', () => {
  const s = newGame(7, defaultParams(), 1);
  assert.equal(dangerOf(s), 0);
  s.train.speed = s.params.speedMax * U / TPS * 1000;   // на стелі
  assert.ok(Math.abs(speedOf(s) - 1) < 1e-9);
  s.train.pos = (s.path.length - 1) * U - 100;          // рейок майже нема
  assert.ok(dangerOf(s) > 0.9, `кінець колії ${dangerOf(s)}`);
  s.path.push(...Array.from({ length: 200 }, (_, i) => s.path[s.path.length - 1] + 1 + i));
  assert.equal(dangerOf(s), 0, 'рейок удосталь');
  s.train.heat = 800; assert.ok(dangerOf(s) > 0.4 && dangerOf(s) < 0.6);
  s.train.heat = 1000; assert.equal(dangerOf(s), 1);
  s.train.heat = 0; s.train.fire = 5; assert.equal(dangerOf(s), 1);
  s.status = 'won'; assert.equal(dangerOf(s), 0);
});

test('перемога — каденція до тоніки з тонікою в басі', () => {
  for (const theme of ['koliya', 'chumak']) {
    const c = cadence(theme), sc = STYLE[theme].scale;
    const end = Math.max(...c.mel.map((x) => x.at + x.len));
    const final = c.mel.filter((x) => x.at + x.len === end);
    assert.ok(final.some((x) => x.deg === 0 && x.midi === TONIC), 'остання — тоніка');
    for (const x of c.mel) assert.ok(inMode(x.midi, TONIC, sc));
    assert.ok(c.bass.some((x) => x.at + x.len === end && x.midi === 38));
  }
});

test('зведення: музика тихіша за ефекти, тло ще тихіше', () => {
  assert.ok(MIX.music < MIX.sfx && MIX.amb < MIX.music);
  // найгучніший момент музики (сопілка + басоля + «дум» разом) — тихіше за удар сокирою (піки chop ×0,5 майстра)
  const musicPeak = MIX.music * (VOICE.mel + VOICE.bass * 0.5 + VOICE.dum);
  const chopPeak = MIX.sfx * 0.35;
  assert.ok(musicPeak < chopPeak, `${musicPeak} < ${chopPeak}`);
});

// — заглушка WebAudio: рахує, що будували
function stubAudio() {
  const count = { ctx: 0, osc: 0, src: 0, stop: 0 };
  const param = (v = 0) => ({ value: v, setValueAtTime() { }, linearRampToValueAtTime() { }, exponentialRampToValueAtTime() { },
    setTargetAtTime() { }, cancelScheduledValues() { } });
  const node = (extra = {}) => new Proxy({ connect() { }, disconnect() { }, start() { }, stop() { count.stop++; }, ...extra }, {
    get(o, k) { if (!(k in o)) o[k] = typeof k === 'string' ? param() : undefined; return o[k]; },
  });
  class Ctx {
    constructor() { count.ctx++; count.c = this; this.currentTime = 0; this.sampleRate = 8000; this.destination = node(); this.state = 'running'; }
    createGain() { return node(); }
    createOscillator() { count.osc++; return node(); }
    createBufferSource() { count.src++; return node(); }
    createBiquadFilter() { return node(); }
    createDynamicsCompressor() { return node(); }
    createBuffer(c, n) { return { getChannelData: () => new Float32Array(n) }; }
    resume() { } suspend() { this.state = 'suspended'; } close() { }
  }
  globalThis.AudioContext = Ctx;
  return count;
}

test('без «Увімк.» — жодного звуку: ні контексту, ні осциляторів', () => {
  const count = stubAudio();
  const mem = new Map(), storage = { getItem: (k) => mem.get(k) ?? null, setItem: (k, v) => mem.set(k, v) };
  const audio = createAudio();
  const mu = createMusic(audio, { storage, test: true });
  assert.equal(mu.music, false); assert.equal(mu.amb, false);
  const s = newGame(7, defaultParams(), 2);
  for (let i = 0; i < 200; i++) { mu.update(s); mu._tick(); }
  mu.setVolume('music', 0.3); mu.setVolume('sfx', 0.7);
  assert.equal(count.ctx, 0, 'AudioContext не створено');
  assert.equal(count.osc + count.src, 0);
  assert.equal(audio.volume, 0.7);

  // збережене «увімк.» під ?test не вмикається само
  mem.set('kol-music', JSON.stringify({ music: true, amb: true }));
  const mu2 = createMusic(audio, { storage, test: true });
  assert.equal(mu2.music, false); assert.equal(mu2.amb, false);
  assert.equal(count.ctx, 0);
  delete globalThis.AudioContext;
});

test('увімкнена музика планує ноти; поразка — обрив; перемога — каденція', () => {
  const count = stubAudio();
  const audio = createAudio();
  const mu = createMusic(audio, { storage: null, test: true });
  const s = newGame(7, defaultParams(), 1);
  mu.update(s);
  mu.setMusic(true);
  assert.equal(count.ctx, 1);
  assert.equal(audio.enabled, false, 'ефекти лишились 🔇');
  for (let i = 0; i < 20; i++) mu._tick();
  assert.ok(count.osc > 5, `заплановано ноти (${count.osc})`);
  assert.ok(mu.debug().nodes > 0);
  s.status = 'lost'; mu._tick();
  assert.equal(mu.debug().nodes, 0, 'обрив — усе заглушено');
  assert.equal(mu.debug().mode, 'done');
  const before = count.osc; mu._tick(); mu._tick();
  assert.equal(count.osc, before, 'після поразки — тиша');
  s.status = 'run'; mu._tick();
  assert.ok(count.osc > before, 'рестарт — грає знову');
  s.status = 'won'; const b2 = count.osc; mu._tick();
  assert.ok(count.osc > b2, 'каденція');
  mu.setMusic(false);
  mu.dispose();
  delete globalThis.AudioContext;
});

test('тло за біомом під камерою: птахи в гаю, вітер у степу, жаби в болоті, тиша в скелях, плюскіт біля води', () => {
  const s = newGame(7, defaultParams(), 2);
  s.ground = new Array(s.w * s.h).fill(0);   // без води
  const at = (kind) => { s.zones = [{ x0: 0, x1: s.w, kind, d: 0, traps: 0 }]; return ambienceFor(s); };
  const g = at('grove'), st = at('steppe'), sw = at('swamp'), stone = at('stone');
  assert.equal(g.biome, 'grove');
  assert.ok(g.birds > 0.8 && g.birds > g.wind);
  assert.ok(st.wind > 0.7 && st.wind > st.birds);
  assert.ok(sw.frogs > 0.8);
  for (const k of AMB_LAYERS) assert.equal(stone[k], 0, `тиша в скелях: ${k}`);
  assert.equal(stone.water, 0);
  // вода поруч з камерою — плюскіт навіть у скелях
  const x = Math.floor(focusX(s));
  for (let y = 0; y < s.h; y++) for (let dx = -2; dx <= 2; dx++) s.ground[y * s.w + x + dx] = 1;
  assert.ok(at('stone').water > 0.5);
  // біом береться під камерою — між паротягом і гуртом
  s.ground.fill(0);
  s.zones = [{ x0: 0, x1: 4, kind: 'grove' }, { x0: 4, x1: s.w, kind: 'swamp' }];
  for (const p of s.players) p.x = 40 * U;
  assert.ok(focusX(s) > 4);
  assert.equal(ambienceFor(s).biome, 'swamp');
});

test('тло: без «Увімк.» нічого; увімкнене — переходи плавні, у скелях змовкає', () => {
  const count = stubAudio();
  const audio = createAudio();
  const mu = createMusic(audio, { storage: null, test: true });
  const s = newGame(7, defaultParams(), 1);
  s.ground = new Array(s.w * s.h).fill(0);
  s.zones = [{ x0: 0, x1: s.w, kind: 'grove' }];
  mu.update(s); for (let i = 0; i < 20; i++) mu._tick();
  assert.equal(count.ctx, 0);
  mu.setAmb(true);
  const step = () => { count.c.currentTime += 0.09; mu._tick(); };
  assert.equal(count.ctx, 1);
  assert.equal(mu.debug().mode, 'play'); assert.equal(mu.music, false, 'тло не вмикає музику');
  step();
  const first = mu.debug().ambLv.birds;
  assert.ok(first > 0 && first < 0.1, `перший тик — лише початок переходу (${first})`);
  for (let i = 0; i < 60; i++) step();   // ~5,4 с
  assert.ok(mu.debug().ambLv.birds > 0.8, 'за кілька секунд — гай');
  s.zones = [{ x0: 0, x1: s.w, kind: 'stone' }];
  for (let i = 0; i < 4; i++) step();
  const mid = mu.debug().ambLv.birds;
  assert.ok(mid > 0.4, `не обривається різко (${mid})`);
  for (let i = 0; i < 120; i++) step();
  assert.ok(mu.debug().ambLv.birds < 0.01 && mu.debug().ambLv.wind < 0.01, 'у скелях тиша');
  mu.setAmb(false);
  assert.equal(mu.debug().ambEv, 0);
  mu.dispose();
  delete globalThis.AudioContext;
});
