// K16: кожна подія s.ev має звук, а гучності зведено ярусами (без вух — за амплітудою й тривалістю).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { SFX, LEVEL, soundOf } from '../../web/games/koliya/audio.js';
import { G } from '../../web/games/koliya/rules.js';

const dir = new URL('../../web/games/koliya/', import.meta.url);

/** Усі типи подій, що пишуться в s.ev: ev(s, 'x', …) і ev(s, { t: 'x', … }) у rules/extras/coop */
function eventTypes() {
  const out = new Set();
  for (const f of ['rules.js', 'extras.js', 'coop.js']) {
    const u = new URL(f, dir);
    if (!existsSync(u)) continue;
    const src = readFileSync(u, 'utf8');
    for (const m of src.matchAll(/\bev\(s,\s*'([a-z_]+)'/g)) out.add(m[1]);
    for (const m of src.matchAll(/\bev\(s,\s*\{\s*t:\s*'([a-z_]+)'/g)) out.add(m[1]);
    for (const m of src.matchAll(/\bev\(s,\s*tree \? '([a-z_]+)' : '([a-z_]+)'/g)) { out.add(m[1]); out.add(m[2]); }
  }
  // контракт (CONTRACT.md) + кооп-фішки K18, навіть якщо coop.js ще не злито
  for (const t of ['chop', 'rock', 'pick', 'drop', 'rail', 'craft', 'water', 'douse', 'whistle', 'alarm', 'won', 'lost', 'step',
    'honk', 'scare', 'goose_lost', 'upgrade', 'throw', 'catch', 'pass', 'stun', 'bump', 'land', 'sink']) out.add(t);
  return [...out];
}

// — «гучність» звуку: середня потужність (RMS² × A-зважування²) за тривалість, дБ
// гармоніки форм хвилі (амплітуда n-ї відносно основної) — сума квадратів з A-зважуванням кожної
const HARM = { sine: () => 0, triangle: (n) => (n % 2 ? 1 / (n * n) : 0), square: (n) => (n % 2 ? 1 / n : 0), sawtooth: (n) => 1 / n };
const WAVE_RMS = { sine: 0.707, triangle: 0.577, square: 1, sawtooth: 0.577 };
/** A-зважене RMS хвилі з основною f і повним RMS rms */
function weighted(type, f, rms) {
  let all = 0, wsum = 0;
  for (let n = 1; n <= 40 && n * f < 16000; n++) {
    const a = n === 1 ? 1 : HARM[type](n);
    all += a * a; wsum += (a * aWeight(n * f)) ** 2;
  }
  return rms * Math.sqrt(wsum / all);
}
function aWeight(f) {   // A-зважування, лінійно
  const f2 = f * f;
  const ra = (12194 ** 2 * f2 * f2) / ((f2 + 20.6 ** 2) * Math.sqrt((f2 + 107.7 ** 2) * (f2 + 737.9 ** 2)) * (f2 + 12194 ** 2));
  return ra * 10 ** (2 / 20);
}
const decayDiv = (vol) => 2 * Math.log(Math.max(1.0001, vol / 1e-4));   // ∫ спаду exp від vol до 1e-4: dur / (2·ln)
function loudness(name) {
  let E = 0;
  const k = 10 ** ((LEVEL[name] ?? 0) / 20);
  const A = {
    tone(t, f, d, { type = 'sine', vol = 0.3, to = null } = {}) {
      const fm = to ? Math.sqrt(f * to) : f, v = vol * k;
      E += weighted(type, fm, v * WAVE_RMS[type]) ** 2 * d / decayDiv(v);
    },
    noise(t, d, { vol = 0.3, f = 1500, q = 1, kind = 'bandpass' } = {}) {
      const bw = kind === 'highpass' ? 22050 - f : kind === 'lowpass' ? f : f / q, v = vol * k;
      const fc = kind === 'highpass' ? Math.min(8000, f * 2) : f;
      E += (v * 0.577 * Math.sqrt(Math.min(1, bw / 22050)) * aWeight(fc)) ** 2 * d / decayDiv(v);
    },
  };
  const dur = SFX[name](0, A);
  assert.ok(dur > 0 && dur < 2, `${name}: тривалість ${dur}`);
  return 10 * Math.log10(E / Math.max(0.2, dur));
}

test('кожна подія з rules/extras/coop має звук', () => {
  const miss = [];
  for (const t of eventTypes()) {
    const variants = t === 'honk' ? [{ t }, { t, p: 0 }] : [{ t, x: 0, y: 0 }];
    for (const e of variants) { const n = soundOf(e); if (!SFX[n]) miss.push(`${t}→${n}`); }
  }
  assert.deepEqual(miss, []);
});

test('кроки за підлогою; гудок гравця ≠ гуска', () => {
  const s = { w: 3, ground: new Uint8Array([G.GRASS, G.SWAMP, G.BRIDGE]) };
  assert.equal(soundOf({ t: 'step', x: 0, y: 0 }, s), 'step');
  assert.equal(soundOf({ t: 'step', x: 1, y: 0 }, s), 'squelch');
  assert.equal(soundOf({ t: 'step', x: 2, y: 0 }, s), 'plank');
  assert.equal(soundOf({ t: 'honk', x: 0, y: 0 }), 'honk');
  assert.equal(soundOf({ t: 'honk', x: 0, y: 0, p: 1 }), 'horn');
});

// яруси відносно удару сокирою (дБ): [від, до]
const TIER = {
  quiet: [-24, -11],   // кроки — фон
  small: [-15, -4],    // взяв/поклав/штовхнув
  action: [-7, 3],     // удари, рейка, вода, кидок
  event: [-6, 5],      // гуси, приголомшення
  loud: [-3, 9],       // свисток, тривога, гудок, покращення
  final: [-1, 12],     // перемога/поразка
};
const TIER_OF = {
  step: 'quiet', squelch: 'quiet', plank: 'quiet',
  pick: 'small', drop: 'small', bump: 'small', pass: 'small', land: 'small', catch: 'small', craft: 'small',
  chop: 'action', rock: 'action', rail: 'action', water: 'action', douse: 'action', throw: 'action', sink: 'action',
  honk: 'event', scare: 'event', goose_lost: 'event', stun: 'event',
  whistle: 'loud', alarm: 'loud', horn: 'loud', upgrade: 'loud',
  won: 'final', lost: 'final',
};

test('гучності зведено ярусами (відносно удару сокирою)', () => {
  const ref = loudness('chop');
  const rows = [], bad = [];
  for (const name of Object.keys(SFX)) {
    const rel = loudness(name) - ref, tier = TIER_OF[name];
    rows.push(`${name.padEnd(11)} ${rel.toFixed(1).padStart(6)} дБ  ${tier}  (без LEVEL ${(rel - (LEVEL[name] ?? 0) + (LEVEL.chop ?? 0)).toFixed(1)})`);
    assert.ok(tier, `${name}: нема ярусу`);
    const [lo, hi] = TIER[tier];
    if (rel < lo || rel > hi) bad.push(`${name} ${rel.toFixed(1)} поза ${tier} [${lo}, ${hi}]`);
  }
  if (process.env.KOL_AUDIO) console.log(rows.join('\n'));
  assert.deepEqual(bad, []);
  // кроки тихіші за будь-яку дію, фінал — гучніший за будь-яку дію
  const L = Object.fromEntries(Object.keys(SFX).map((n) => [n, loudness(n) - ref]));
  for (const n of ['chop', 'rock', 'rail']) {
    assert.ok(L.step < L[n] - 8 && L.squelch < L[n] - 6, `кроки тихіші за ${n}`);
    assert.ok(L.won > L[n] && L.lost > L[n], `фінал гучніший за ${n}`);
  }
});
