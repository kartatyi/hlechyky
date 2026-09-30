/*
  Посиденьки: шумодав RNNoise в аудіопотоці (AudioWorklet) — мікрофон → hl-denoise → hl-gate (voice-worklet.js).

  RNNoise — нейромережевий шумодав Xiph.Org (© Xiph.Org Foundation, Mozilla, Jean-Marc Valin; BSD-3-Clause,
  https://github.com/xiph/rnnoise/blob/main/COPYING), скомпільований у wasm проєктом Jitsi: @jitsi/rnnoise-wasm 0.2.1
  (Apache-2.0, static/rnnoise-LICENSE.txt), файл rnnoise-sync.js — без змін, wasm (модель 0.2) вшитий усередину, бо в
  аудіопотоці нема ні fetch, ні setTimeout.

  RNNoise їсть кадри по 480 семплів (10 мс при 48 кГц — тому AudioContext у voice.js на 48 кГц) у масштабі int16.
  Аудіопотік дає по 128, тож збираємо кадр, чистимо й кладемо в чергу виходу; вихід стартує, коли в черзі вже є
  кадр, — +10 мс затримки, зате без дірок. Вимкнули (port: { on: false }) — звук іде наскрізь.
  Сторінці раз на ~100 мс — { vad }: імовірність, що в кадрі голос (0..1).
*/
import createRNNWasmModuleSync from './rnnoise-sync.js';

const FRAME = 480;
const RING = 4096;

class Denoise extends AudioWorkletProcessor {
  constructor() {
    super();
    this.on = true;
    this.m = createRNNWasmModuleSync();
    this.state = this.m._rnnoise_create();
    this.inPtr = this.m._malloc(FRAME * 4);
    this.outPtr = this.m._malloc(FRAME * 4);
    this.frame = new Float32Array(FRAME);
    this.fill = 0;
    this.ring = new Float32Array(RING);
    this.r = 0;
    this.w = 0;
    this.queued = 0;
    this.primed = false;
    this.vad = 0;
    this.tell = 0;
    this.port.onmessage = (e) => {
      const m = e.data || {};
      if (typeof m.on === 'boolean' && m.on !== this.on) {
        this.on = m.on;
        this.fill = 0;
        this.queued = 0;
        this.r = this.w = 0;
        this.primed = false;
      }
    };
  }

  process(inputs, outputs) {
    const input = inputs[0] && inputs[0][0];
    const out = outputs[0][0];
    if (!input) { out.fill(0); return true; }
    if (!this.on) { out.set(input); return true; }

    for (let i = 0; i < input.length; i++) {
      this.frame[this.fill++] = input[i] * 32768;
      if (this.fill < FRAME) continue;
      this.fill = 0;
      // HEAPF32 беремо щоразу: якщо wasm виросте пам'ять, старий вигляд стане порожнім.
      const heap = this.m.HEAPF32;
      heap.set(this.frame, this.inPtr >> 2);
      this.vad = this.m._rnnoise_process_frame(this.state, this.outPtr, this.inPtr);
      const res = heap.subarray(this.outPtr >> 2, (this.outPtr >> 2) + FRAME);
      for (let j = 0; j < FRAME; j++) {
        this.ring[this.w] = res[j] / 32768;
        this.w = (this.w + 1) % RING;
      }
      this.queued = Math.min(RING, this.queued + FRAME);
    }

    if (!this.primed && this.queued >= FRAME) this.primed = true;
    for (let i = 0; i < out.length; i++) {
      if (this.primed && this.queued > 0) {
        out[i] = this.ring[this.r];
        this.r = (this.r + 1) % RING;
        this.queued--;
      } else out[i] = 0;
    }

    this.tell += out.length;
    if (this.tell >= sampleRate * 0.1) {
      this.tell = 0;
      this.port.postMessage({ vad: Math.round(this.vad * 100) / 100 });
    }
    return true;
  }
}

registerProcessor('hl-denoise', Denoise);
