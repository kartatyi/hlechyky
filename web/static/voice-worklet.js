/*
  Посиденьки: обробка звуку в аудіопотоці браузера (AudioWorklet). Живе тут, а не в таймерах сторінки, бо схована
  вкладка отримує таймер раз на секунду — голосова активація у фоні (граєш у щось інше, а говориш сюди) різала б
  слова навпіл. Аудіопотік не гальмують ніколи.

  hl-gate — мікрофон → те, що летить людям:
    - затримка 30 мс: коли рівень перейшов поріг, ворота відчиняються ще до першого звуку слова — початок не обрізає;
    - режими: 'vad' (від голосу: поріг у дБ і 450 мс «хвоста» після останнього звуку), 'ptt' (поки тримаєш кнопку,
      плюс 200 мс хвоста), 'open' (завжди відчинено); muted — зачинено завжди;
    - гучність воріт іде плавно (10 мс відчинитись, 120 мс зачинитись): без клацань;
    - сторінці раз на ~50 мс — { level, open }: рівень мікрофона до воріт (для шкали) і чи відчинено.
  hl-meter — чужий голос: лише міряє рівень і раз на ~60 мс каже його сторінці (хто говорить, коли притишувати радіо).
*/

const dbOf = (sumSq, n) => (n > 0 && sumSq > 0 ? 10 * Math.log10(sumSq / n) : -120);

class Gate extends AudioWorkletProcessor {
  constructor() {
    super();
    this.mode = 'vad';
    this.threshold = -50;
    this.muted = false;
    this.pressed = false;
    this.releaseAt = 0;            // до якого часу (секунд) ворота ще відчинені після останнього звуку чи відпускання
    this.gain = 0;
    this.open = false;
    this.level = -120;             // згладжений рівень
    this.delay = new Float32Array(Math.max(128, Math.round(sampleRate * 0.03)));
    this.pos = 0;
    this.tell = 0;
    this.port.onmessage = (e) => {
      const m = e.data || {};
      if (m.mode) this.mode = m.mode;
      if (typeof m.threshold === 'number') this.threshold = m.threshold;
      if (typeof m.muted === 'boolean') this.muted = m.muted;
      if (typeof m.pressed === 'boolean') {
        this.pressed = m.pressed;
        if (!m.pressed) this.releaseAt = currentTime + 0.2;
      }
    };
  }

  process(inputs, outputs) {
    const input = inputs[0] && inputs[0][0];
    const out = outputs[0][0];
    const n = out.length;
    let sum = 0;
    if (input) for (let i = 0; i < n; i++) sum += input[i] * input[i];
    const db = dbOf(sum, input ? n : 0);
    // швидко вгору, повільно вниз — як стрілка на пульті
    this.level = db > this.level ? db : this.level + (db - this.level) * 0.15;

    let want;
    if (this.muted) want = false;
    else if (this.mode === 'open') want = true;
    else if (this.mode === 'ptt') want = this.pressed || currentTime < this.releaseAt;
    else {
      if (db > this.threshold) this.releaseAt = currentTime + 0.45;
      want = currentTime < this.releaseAt;
    }
    const changed = want !== this.open;
    this.open = want;

    const up = 1 / (sampleRate * 0.01), down = 1 / (sampleRate * 0.12);
    const d = this.delay, len = d.length;
    for (let i = 0; i < n; i++) {
      const x = input ? input[i] : 0;
      const y = d[this.pos];
      d[this.pos] = x;
      this.pos = (this.pos + 1) % len;
      this.gain = want ? Math.min(1, this.gain + up) : Math.max(0, this.gain - down);
      out[i] = y * this.gain;
    }

    this.tell += n;
    if (changed || this.tell >= sampleRate * 0.05) {
      this.tell = 0;
      this.port.postMessage({ level: Math.round(this.level), open: this.open });
    }
    return true;
  }
}

class Meter extends AudioWorkletProcessor {
  constructor() {
    super();
    this.level = -120;
    this.tell = 0;
  }

  process(inputs) {
    const ch = inputs[0] && inputs[0][0];
    let sum = 0, n = 0;
    if (ch) { n = ch.length; for (let i = 0; i < n; i++) sum += ch[i] * ch[i]; }
    const db = dbOf(sum, n);
    this.level = db > this.level ? db : this.level + (db - this.level) * 0.1;
    this.tell += 128;
    if (this.tell >= sampleRate * 0.06) {
      this.tell = 0;
      this.port.postMessage(Math.round(this.level));
    }
    return true;
  }
}

registerProcessor('hl-gate', Gate);
registerProcessor('hl-meter', Meter);
