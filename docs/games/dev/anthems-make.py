"""«Гімн переможця»: дванадцять готових гімнів Лавки (docs/games/specs/anthem.md §1, §5).

    python docs/games/dev/anthems-make.py [--ffmpeg PATH] [--out DIR] [id ...]

Без id — усі дванадцять; з id (fanfare, hopak, …) — лише ці. Кожен гімн синтезується тут же, чистим Python без
numpy (лише stdlib): адитивний синтез із таблиць гармонік, ADSR-обвідні, шум для ударних і оплесків, проста
реверберація (гребінцеві + всепропускні фільтри). Проміжний WAV лягає в тимчасову теку (не в репозиторій), ffmpeg
кодує його в <out>/<id>.mp3 — типово web/static/anthems/: стерео, 44,1 кГц, 128k, loudnorm I=-16 / TP=-1.5, як і
вирізаний «Свій трек» (LavkaAnthems), щоб готові гімни й свої за столом звучали однаково гучно.

--ffmpeg — шлях до ffmpeg (типово змінна FFMPEG, інакше той, що в PATH; на проді D:/or/tools/yt-dlp/ffmpeg.exe).

Мелодії власні — без цитат чужих пісень, гімнів чи фанфар. Тривалість 4–9 с, файл ≤ 150 КБ (скрипт перевіряє).
Перезапуск дає ті самі файли: випадковість — random.Random із фіксованим зерном на кожен гімн, ffmpeg із +bitexact
(без підпису версії кодера в тегах). Слухати під час перевірки не треба: скрипт друкує тривалість і розмір, а
спектрограми — ffmpeg showspectrumpic.
"""
import argparse, math, os, random, subprocess, sys, tempfile, time, wave
from array import array

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
SR = 44100
TN = 4096                      # довжина таблиці хвилі; індекс — int(фаза) & MASK
MASK = TN - 1
BLOCK = 32                     # частота (вібрато, глісандо, форманти) оновлюється раз на блок — так утричі швидше
TAU = 2 * math.pi
SINE = array("d", (math.sin(TAU * i / TN) for i in range(TN)))
MAX_BYTES = 150 * 1024


def hz(m):
    return 440.0 * 2 ** ((m - 69) / 12)


def zeros(n):
    return array("d", bytes(8 * n))


def sec(n):
    return int(round(n * SR))


# ---------------------------------------------------------------- таблиці хвиль

def _harm(kind, h):
    """Гармоніки (номер, амплітуда) для тембру. h — скільки їх уміщається нижче ~15 кГц на цій висоті."""
    r = range(1, h + 1)
    if kind == "sine":
        return [(1, 1.0)]
    if kind == "saw":
        return [(n, 1 / n) for n in r]
    if kind == "dark":         # «закритий» пилкоподібний — для синта, поки фільтр не відкрився
        return [(n, 1 / n ** 2.2) for n in r]
    if kind == "brass":        # мідь: гармоніки спадають повільно, горб біля 1–2 кГц дає «металевість»
        return [(n, 1 / n ** 0.7 * (1.3 if 3 <= n <= 8 else 1.0)) for n in r]
    if kind == "brassdark":
        return [(n, 1 / n ** 2.0) for n in r]
    if kind == "square":
        return [(n, 1 / n) for n in r if n % 2]
    if kind == "pulse":        # імпульс 25 % — «восьмибітний» голос
        return [(n, math.sin(math.pi * n * 0.25) / n) for n in r]
    if kind == "tri":
        return [(n, (1 if (n // 2) % 2 == 0 else -1) / n ** 2) for n in r if n % 2]
    if kind == "reed":         # язичок баяна: багато гармонік, парні тихіші
        return [(n, 1 / n ** 0.75 * (1.0 if n % 2 else 0.55)) for n in r]
    if kind == "horn":         # трембіта: довга вузька труба, гнусавий горб на 3–6 гармоніці
        return [(n, 1 / n ** 1.15 * (1.6 if 3 <= n <= 6 else 1.0)) for n in r]
    if kind == "string":
        return [(n, 1 / n ** 1.1) for n in r]
    if kind == "fiddle":       # скрипка: пилка з корпусним горбом на 4–7 гармоніці
        return [(n, 1 / n * (1.8 if 4 <= n <= 7 else 1.0)) for n in r]
    if kind == "voice":        # джерело для «браво»/«гоп» — потім його ріжуть форманти
        return [(n, 1 / n ** 0.9) for n in r]
    raise KeyError(kind)


_STEPS = (1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 32, 40)
_TABS = {}


def table(kind, f):
    """Таблиця тембру для висоти f: гармонік рівно стільки, щоб найвища лишалась нижче ~15 кГц (без аліасингу).
    Кількість округлюється вниз до _STEPS — таблиць мало, і кожну рахуємо раз."""
    lim = max(1, int(15000 / max(f, 1.0)))
    h = max(s for s in _STEPS if s <= lim)
    key = (kind, h)
    t = _TABS.get(key)
    if t is None:
        acc = [0.0] * TN
        for n, a in _harm(kind, h):
            for i in range(TN):
                acc[i] += a * SINE[(n * i) & MASK]
        peak = max(abs(x) for x in acc) or 1.0
        t = _TABS[key] = array("d", (x / peak for x in acc))
    return t


# ---------------------------------------------------------------- обвідні й генератори

def adsr(dur, a=0.01, d=0.1, s=0.7, r=0.1, peak=1.0):
    """Атака лінійна, спад і відпуск — експонентою. Довжина — dur + 4r; останні 2 мс — у нуль, без клацання."""
    na = max(1, sec(a))
    nh = max(na, sec(dur))
    nr = max(1, sec(r * 4))
    env = zeros(nh + nr)
    for i in range(na):
        env[i] = peak * i / na
    v, tgt = peak, s * peak
    kd = math.exp(-1 / (max(d, 1e-4) * SR))
    for i in range(na, nh):
        v = tgt + (v - tgt) * kd
        env[i] = v
    kr = math.exp(-1 / (r * SR))
    for i in range(nh, nh + nr):
        v *= kr
        env[i] = v
    _tail(env)
    return env


def perc(tau, a=0.001, peak=1.0, length=None):
    """Удар: коротка атака й експонентний спад до −60 дБ (або до length секунд)."""
    na = max(1, sec(a))
    n = sec(length) if length else na + sec(tau * 6.9)
    env = zeros(n)
    for i in range(min(na, n)):
        env[i] = peak * i / na
    v, k = peak, math.exp(-1 / (tau * SR))
    for i in range(na, n):
        env[i] = v
        v *= k
    _tail(env)
    return env


def _tail(x, ms=2.0):
    m = min(len(x), max(1, int(ms * SR / 1000)))
    n = len(x)
    for j in range(m):
        x[n - m + j] *= (m - 1 - j) / m


def _at(pts, t):
    """Значення ламаної [(с, v), …] у момент t; між точками — геометрично (для частоти це рівні півтони)."""
    if t <= pts[0][0]:
        return pts[0][1]
    for (t0, v0), (t1, v1) in zip(pts, pts[1:]):
        if t < t1:
            u = (t - t0) / (t1 - t0)
            return v0 * (v1 / v0) ** u
    return pts[-1][1]


def voice(f, env, kind="saw", bright=None, kind2=None, vib=None, ph=0.0):
    """Осцилятор із таблиці. f — Гц або ламана [(с, Гц), …] (глісандо між точками); довжина звуку — довжина env.
    bright — друга обвідна для яскравішої таблиці kind2 (мідь і синт яскравішають з гучністю);
    vib — (Гц, частка, затримка с): вібрато вступає поступово, як у живого виконавця."""
    pts = f if isinstance(f, list) else [(0.0, f)]
    top = max(p[1] for p in pts)
    t1 = table(kind, top * 1.03)
    t2 = table(kind2, top * 1.03) if bright is not None else None
    n = len(env)
    out = zeros(n)
    k = TN / SR
    vr, vd, vdel = vib or (0.0, 0.0, 0.0)
    vph, vinc = 0.0, vr * TN / SR * BLOCK
    glide = len(pts) > 1
    fr = pts[0][1]
    for b in range(0, n, BLOCK):
        t = b / SR
        if glide:
            fr = _at(pts, t)
        x = fr
        if vd:
            x *= 1 + vd * min(1.0, max(0.0, (t - vdel) / 0.3)) * SINE[int(vph) & MASK]
            vph += vinc
        inc = x * k
        e = min(b + BLOCK, n)
        if t2 is None:
            for i in range(b, e):
                out[i] = t1[int(ph) & MASK] * env[i]
                ph += inc
        else:
            for i in range(b, e):
                p = int(ph) & MASK
                out[i] = (t1[p] + t2[p] * bright[i]) * env[i]
                ph += inc
    return out


def partials(f, spec, a=0.0015, cap=3.5):
    """Негармонійні обертони (глечик, дзвін, литаври): [(відношення, амплітуда, τ с)], кожен згасає сам."""
    n = sec(min(cap, max(tau for _, _, tau in spec) * 6.9))
    out = zeros(n)
    for ratio, amp, tau in spec:
        fr = f * ratio
        if fr > 18000:
            continue
        m = min(n, sec(tau * 6.9))
        inc, k, v, ph = fr * TN / SR, math.exp(-1 / (tau * SR)), amp, 0.0
        for i in range(m):
            out[i] += SINE[int(ph) & MASK] * v
            ph += inc
            v *= k
    na = max(1, sec(a))
    for i in range(min(na, n)):
        out[i] *= i / na
    # обрізаний cap-ом хвіст не уривається: останні 0,4 с плавно сходять у нуль
    nf = min(n, sec(0.4)) if n < sec(max(tau for _, _, tau in spec) * 6.9) else sec(0.02)
    for j in range(nf):
        out[n - nf + j] *= 0.5 + 0.5 * math.cos(math.pi * j / nf)
    return out


def noise(rng, n):
    r = rng.random
    return array("d", (r() * 2 - 1 for _ in range(n)))


def lowpass(x, fc):
    a = 1 - math.exp(-TAU * fc / SR)
    y, out = 0.0, zeros(len(x))
    for i in range(len(x)):
        y += a * (x[i] - y)
        out[i] = y
    return out


def highpass(x, fc):
    a = 1 - math.exp(-TAU * fc / SR)
    y, out = 0.0, zeros(len(x))
    for i in range(len(x)):
        y += a * (x[i] - y)
        out[i] = x[i] - y
    return out


def bandpass(x, fc, q):
    """Двополюсний смуговий (RBJ, 0 дБ на вершині). fc — число або ламана [(с, Гц), …] — форманти, що пливуть."""
    pts = fc if isinstance(fc, list) else [(0.0, fc)]
    out = zeros(len(x))
    x1 = x2 = y1 = y2 = 0.0
    for b in range(0, len(x), BLOCK):
        w = TAU * min(_at(pts, b / SR), SR * 0.45) / SR
        al = math.sin(w) / (2 * q)
        a0 = 1 + al
        b0, a1, a2 = al / a0, -2 * math.cos(w) / a0, (1 - al) / a0
        for i in range(b, min(b + BLOCK, len(x))):
            v = x[i]
            y = b0 * (v - x2) - a1 * y1 - a2 * y2
            x2, x1, y2, y1 = x1, v, y1, y
            out[i] = y
    return out


def mul(x, env):
    n = min(len(x), len(env))
    return array("d", (x[i] * env[i] for i in range(n)))


def put(buf, sig, t, g=1.0):
    i0 = sec(t)
    for k in range(max(0, -i0), min(len(sig), len(buf) - i0)):
        buf[i0 + k] += sig[k] * g


# ---------------------------------------------------------------- мікс і реверберація

class Mix:
    """Стерео-шина плюс моно-посил на реверберацію. add() кладе звук у момент t з панорамою (−1…1)."""

    def __init__(self, length):
        self.n = sec(length)
        self.L, self.R, self.S = zeros(self.n), zeros(self.n), zeros(self.n)

    def add(self, sig, t, gain=1.0, pan=0.0, rev=0.25):
        a = (max(-1.0, min(1.0, pan)) + 1) * math.pi / 4
        gl, gr, gs = gain * math.cos(a) * math.sqrt(2), gain * math.sin(a) * math.sqrt(2), gain * rev
        i0 = sec(t)
        L, R, S = self.L, self.R, self.S
        for k in range(max(0, -i0), min(len(sig), self.n - i0)):
            x = sig[k]
            i = i0 + k
            L[i] += x * gl
            R[i] += x * gr
            S[i] += x * gs


def _comb(x, d, fb, damp, out):
    buf, j, fs, dm = [0.0] * d, 0, 0.0, 1 - damp
    for i in range(len(x)):
        y = buf[j]
        fs = y * dm + fs * damp
        buf[j] = x[i] + fs * fb
        out[i] += y
        j += 1
        if j == d:
            j = 0


def _allpass(x, d, g=0.5):
    buf, j = [0.0] * d, 0
    for i in range(len(x)):
        b, v = buf[j], x[i]
        buf[j] = v + b * g
        x[i] = b - v
        j += 1
        if j == d:
            j = 0


def reverb(mix, size=1.0, fb=0.84, damp=0.3, wet=0.3):
    """Спрощений Freeverb: чотири гребінці й два всепропускні на канал; правий — зі зсувом затримок на 23 семпли,
    тож хвіст розходиться по стерео."""
    for ch, spread in ((mix.L, 0), (mix.R, 23)):
        acc = zeros(mix.n)
        for d in (1116, 1277, 1422, 1617):
            _comb(mix.S, int(d * size) + spread, fb, damp, acc)
        for d in (556, 341):
            _allpass(acc, int(d * size) + spread)
        g = wet * 0.25
        for i in range(mix.n):
            ch[i] += acc[i] * g


def write_wav(mix, path, fade=0.25):
    """Нормує пік до −1 дБ (гучність далі вирівнює loudnorm), знімає постійну складову, плавно гасить кінець."""
    L, R, n = mix.L, mix.R, mix.n
    for ch in (L, R):                       # DC-фільтр ~10 Гц: шум і биття не зсувають нуль
        x1 = y1 = 0.0
        for i in range(n):
            v = ch[i]
            y1 = v - x1 + 0.9986 * y1
            x1 = v
            ch[i] = y1
    peak = max(max(map(abs, L)), max(map(abs, R))) or 1.0
    g = 0.89 / peak
    nf, ni = sec(fade), sec(0.003)
    pcm = array("h", bytes(4 * n))
    for i in range(n):
        w = g
        if i < ni:
            w *= i / ni
        if i >= n - nf:
            w *= 0.5 + 0.5 * math.cos(math.pi * (i - (n - nf)) / nf)
        pcm[2 * i] = int(max(-1.0, min(1.0, L[i] * w)) * 32767)
        pcm[2 * i + 1] = int(max(-1.0, min(1.0, R[i] * w)) * 32767)
    if sys.byteorder == "big":
        pcm.byteswap()
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())


# ---------------------------------------------------------------- інструменти

def brass(f, dur, vel=0.8, scoop=0.985):
    """Мідь: підтяг висоти на атаці, яскравість росте з гучністю, пізнє вібрато."""
    env = adsr(dur, a=0.03, d=0.3, s=0.78, r=0.1)
    bright = array("d", (v * vel for v in env))
    return voice([(0.0, f * scoop), (0.045, f)], env, "brassdark", bright, "brass", vib=(5.3, 0.004, 0.28))


def strings(f, dur, vel=0.8, rng=None, a=0.12):
    """Ансамбль струнних: три розстроєні голоси (±7 центів) з власним вібрато."""
    env = adsr(dur, a=a, d=0.4, s=0.9, r=0.3, peak=vel)
    out = zeros(len(env))
    for cents, vr in ((-7, 5.3), (0, 5.8), (7, 6.2)):
        s = voice(f * 2 ** (cents / 1200), env, "string", vib=(vr, 0.0035, 0.12), ph=(rng.random() if rng else 0) * TN)
        for i in range(len(out)):
            out[i] += s[i] * 0.4
    return out


def timpani(f, vel=1.0, rng=None):
    body = partials(f, [(1.0, 1.0, 0.85), (1.5, 0.45, 0.5), (1.99, 0.28, 0.35), (2.44, 0.14, 0.22)], a=0.002)
    if rng:
        knock = lowpass(mul(noise(rng, sec(0.06)), perc(0.012)), 500)
        for i in range(len(knock)):
            body[i] += knock[i] * 1.5
    return array("d", (v * vel for v in body))


def snare(rng, tau=0.05, tone=0.5):
    """Удар малого барабана: шум (дріт) плюс короткий тон мембрани."""
    n = sec(tau * 6)
    nz = highpass(lowpass(noise(rng, n), 7000), 350)
    env = perc(tau, length=tau * 6)
    head = partials(185, [(1.0, tone, 0.025), (1.78, tone * 0.5, 0.015)], a=0.0005)
    out = zeros(n)
    for i in range(n):
        out[i] = nz[i] * env[i] + (head[i] if i < len(head) else 0.0)
    return out


def cymbal(rng, tau=0.7, length=2.4):
    n = sec(length)
    nz = highpass(highpass(noise(rng, n), 3500), 3500)
    body = mul(nz, perc(tau, length=length))
    crash = mul(highpass(noise(rng, n), 1500), perc(0.12, length=length))
    metal = partials(1.0, [(f, 0.12, t) for f, t in ((3150, 0.5), (4870, 0.4), (5340, 0.6), (6320, 0.3),
                                                       (7910, 0.35), (9230, 0.25))], a=0.0005, cap=length)
    return array("d", (body[i] + crash[i] * 0.7 + (metal[i] if i < len(metal) else 0.0) for i in range(n)))


def kick(f0=110.0, f1=45.0, tau=0.3):
    env = perc(tau)
    return voice([(0.0, f0), (0.07, f1)], env, "sine")


def pot(f, rng, vel=1.0):
    """Глиняний глечик під паличкою: глухий основний тон, негармонійні обертони, що швидко гаснуть, і стук."""
    body = partials(f, [(1.0, 1.0, 0.3), (2.42, 0.32, 0.09), (3.93, 0.22, 0.05), (6.3, 0.1, 0.025)])
    knock = lowpass(mul(noise(rng, sec(0.03)), perc(0.006)), 1800)
    for i in range(len(knock)):
        body[i] += knock[i] * 0.5
    return array("d", (v * vel for v in body))


def bell(f, big=False):
    """Церковний дзвін (f — номінал): гул на октаву нижче, терція-тирція, квінта… — характерно негармонійно."""
    s = 1.6 if big else 1.0
    spec = [(0.5, 0.32, 2.4 * s), (0.503, 0.12, 2.0 * s), (1.0, 0.45, 1.7 * s), (1.19, 0.34, 1.3 * s),
            (1.5, 0.2, 0.9 * s), (2.0, 0.55, 0.8 * s), (2.51, 0.24, 0.45 * s), (2.97, 0.17, 0.35 * s),
            (4.07, 0.09, 0.2 * s)]
    return partials(f, spec, a=0.001, cap=4.5 if big else 3.2)


def reed(f, dur, vel=0.8, shake=0.0):
    """Баян: два язички, розстроєні на ±5 центів (биття), shake — «тремтіння міха» (амплітудна модуляція 7 Гц)."""
    env = adsr(dur, a=0.012, d=0.12, s=0.85, r=0.05, peak=vel)
    if shake:
        for i in range(len(env)):
            env[i] *= 1 - shake * (0.5 + 0.5 * math.sin(TAU * 7.0 * i / SR))
    a = voice(f * 2 ** (-5 / 1200), env, "reed")
    b = voice(f * 2 ** (5 / 1200), env, "reed", ph=TN / 3)
    return array("d", (a[i] + b[i] for i in range(len(a))))


def fiddle(f, dur, vel=0.8):
    env = adsr(dur, a=0.018, d=0.08, s=0.8, r=0.06, peak=vel)
    return voice([(0.0, f * 0.99), (0.025, f)], env, "fiddle", vib=(6.3, 0.005, 0.12))


def chip(f, dur, kind="pulse", vel=0.7, rel=0.03):
    env = adsr(dur, a=0.003, d=0.09, s=0.65, r=rel, peak=vel)
    return voice(f, env, kind)


def synth(f, dur, vel=0.7, pluck=0.15, a=0.004, r=0.12, detune=0.0):
    """Синт: «закрита» пилка плюс яскрава, що гасне за pluck с — як фільтр, що відкрився й закривається."""
    env = adsr(dur, a=a, d=0.25, s=0.6, r=r, peak=vel)
    bright = perc(pluck, length=len(env) / SR)
    out = voice(f, env, "dark", bright, "saw")
    if detune:
        o2 = voice(f * 2 ** (detune / 1200), env, "dark", bright, "saw", ph=TN / 2)
        out = array("d", ((out[i] + o2[i]) * 0.6 for i in range(len(out))))
    return out


def shout(rng, f, vowels, length):
    """Вигук натовпу шумом і дзижчанням через форманти: vowels — ламані [(с, F1)], [(с, F2)] і обвідна [(с, гучність)]."""
    f1, f2, amp = vowels
    n = sec(length)
    env = zeros(n)
    for b in range(0, n, BLOCK):
        v = _at([(t, max(a, 1e-4)) for t, a in amp], b / SR)
        for i in range(b, min(b + BLOCK, n)):
            env[i] = v
    src = voice([(0.0, f), (length * 0.4, f * 1.12), (length, f * 0.9)], env, "voice", vib=(5.5, 0.01, 0.0))
    nz = mul(noise(rng, n), env)
    mixsrc = array("d", (src[i] + nz[i] * 0.6 for i in range(n)))
    a, b = bandpass(mixsrc, f1, 5.0), bandpass(mixsrc, f2, 7.0)
    out = array("d", (a[i] + b[i] * 0.6 for i in range(n)))
    _tail(out, 15)
    return out


# ---------------------------------------------------------------- гімни

def a_fanfare(rng):
    """Мідь: тріоль-розгін угору, сходинки, тримана тоніка з литаврами."""
    beat = 0.52
    m = Mix(6.6)
    tp1 = [(67, 1/3), (72, 1/3), (76, 1/3), (79, 1), (76, .5), (79, .5), (81, 1.5), (79, .5), (77, .5), (74, .5),
           (79, 1), (84, 3)]
    tp2 = [(64, 1/3), (67, 1/3), (72, 1/3), (76, 1), (72, .5), (76, .5), (77, 1.5), (76, .5), (74, .5), (71, .5),
           (74, 1), (79, 3)]
    t = 0.0
    for (n1, b), (n2, _) in zip(tp1, tp2):
        d = b * beat * (0.97 if b < 3 else 1.0)
        vel = 1.0 if b >= 3 else 0.8
        m.add(brass(hz(n1), d, vel), t, 0.5, -0.25, 0.25)
        m.add(brass(hz(n2), d, vel * 0.9), t, 0.38, 0.3, 0.25)
        t += b * beat
    # валторни й тромбони — акорди під мелодією
    chords = [(1, 2, (48, 55, 64)), (3, 2, (53, 57, 60)), (5, 1, (55, 59, 62)), (6, 1, (55, 59, 65)),
              (7, 3, (48, 55, 60, 64))]
    for b0, nb, notes in chords:
        for j, nn in enumerate(notes):
            m.add(brass(hz(nn), nb * beat * 0.98, 0.75, 0.99), b0 * beat, 0.2, (j - 1.5) * 0.25, 0.3)
    m.add(timpani(hz(36), 0.9, rng), 1 * beat, 0.7, 0.1, 0.2)
    m.add(timpani(hz(36), 0.7, rng), 3 * beat, 0.6, 0.1, 0.2)
    m.add(timpani(hz(43), 0.8, rng), 5 * beat, 0.65, 0.1, 0.2)
    for k in range(10):                      # дріб литавр перед фінальним акордом
        m.add(timpani(hz(43), 0.25 + 0.05 * k, rng), (6 + k / 10) * beat, 0.6, 0.1, 0.2)
    m.add(timpani(hz(36), 1.2, rng), 7 * beat, 0.8, 0.1, 0.25)
    return m, dict(size=1.1, wet=0.35)


def a_drumroll(rng):
    """Дріб малого барабана з крещендо й прискоренням, «ба-дум» і удар тарілки."""
    m = Mix(5.8)
    takes = [snare(random.Random(rng.randrange(1 << 30)), tau=0.045 + 0.01 * (k % 3)) for k in range(8)]
    t, k, end = 0.0, 0, 3.0
    while t < end:
        u = t / end
        vel = 0.12 + 0.88 * u ** 1.8
        m.add(takes[k % 8], t + rng.uniform(-0.003, 0.003), vel * rng.uniform(0.85, 1.0), -0.12 if k % 2 else 0.12, 0.2)
        t += 1 / (14 + 9 * u)
        k += 1
    m.add(snare(rng, tau=0.07, tone=0.9), 3.08, 1.0, -0.1, 0.2)
    m.add(kick(95, 50, 0.18), 3.08, 0.8, 0.0, 0.1)
    hit = 3.32
    m.add(snare(rng, tau=0.09, tone=1.0), hit, 1.0, 0.0, 0.3)
    m.add(kick(110, 42, 0.25), hit, 0.9, 0.0, 0.15)
    m.add(cymbal(rng, 0.75, 2.4), hit, 1.2, -0.45, 0.3)
    m.add(cymbal(rng, 0.8, 2.4), hit + 0.004, 1.2, 0.45, 0.3)
    return m, dict(size=0.9, wet=0.3)


def a_chiptune(rng):
    """«Рівень пройдено» прямокутною хвилею: імпульсна мелодія з луною, трикутний бас, шумові тарілочки."""
    u = 0.1
    m = Mix(5.0)
    lead = [(72, 1), (76, 1), (79, 1), (84, 1), (83, 1), (79, 1), (76, 1), (79, 1),
            (81, 2), (77, 2), (81, 2), (84, 2),
            (83, 1), (84, 1), (86, 2), (79, 2), (83, 2),
            (88, 1), (86, 1), (84, 1), (83, 1), (81, 2), (83, 2),
            (84, 8)]
    t = 0.0
    for n, d in lead:
        last = d == 8
        s = chip(hz(n), d * u * (1.0 if last else 0.85), "pulse", 0.7, rel=0.12 if last else 0.03)
        m.add(s, t, 0.55, -0.3, 0.12)
        m.add(s, t + u * 0.75, 0.22, 0.45, 0.12)        # «луна» — та сама нота тихіше праворуч, як у старих приставках
        t += d * u
    bass = [48, 60] * 2 + [53, 65] * 2 + [55, 67] * 2 + [53, 65, 55, 67]
    for j, n in enumerate(bass):
        m.add(chip(hz(n), u * 1.7, "tri", 0.9), j * 2 * u, 0.45, 0.0, 0.05)
    m.add(chip(hz(36), 8 * u, "tri", 0.9, rel=0.15), 32 * u, 0.5, 0.0, 0.05)
    m.add(chip(hz(48), 8 * u, "tri", 0.9, rel=0.15), 32 * u, 0.3, 0.0, 0.05)
    arp = [72, 76, 79, 84]                             # фінальна нота — з «восьмибітною» арпеджією під нею
    for j in range(16):
        m.add(chip(hz(arp[j % 4]), u / 2 * 0.9, "square", 0.5), 32 * u + j * u / 2, 0.25, 0.3, 0.1)
    hat = [None] * 4
    for j in range(4):
        r2 = random.Random(rng.randrange(1 << 30))
        hold, v, nz = 0, 0.0, zeros(sec(0.08))
        for i in range(len(nz)):                       # шум «сходинками», як у генератора шуму NES
            if hold == 0:
                v, hold = r2.random() * 2 - 1, 3
            hold -= 1
            nz[i] = v
        hat[j] = mul(highpass(nz, 2000), perc(0.018 + 0.01 * j))
    for j in range(16):
        m.add(hat[j % 4], j * 2 * u, 0.25 if j % 2 else 0.4, 0.1, 0.05)
    m.add(mul(highpass(noise(rng, sec(1.0)), 3000), perc(0.25)), 32 * u, 0.3, 0.0, 0.2)
    return m, dict(size=0.7, wet=0.15)


def a_trombone(rng):
    """«Уа-уа-уа-уааа»: тромбон із сурдиною-«плунжером» сповзає вниз, остання нота хитається й провисає."""
    notes = [(62, 0.0, 0.55), (59, 0.6, 0.55), (58, 1.2, 0.6), (57, 1.85, 2.3)]
    total = 4.3
    n = sec(total)
    env, bright = zeros(n), zeros(n)
    pts = []
    for j, (nn, t0, d) in enumerate(notes):
        f = hz(nn)
        pts += [(t0, f * 1.01), (t0 + 0.06, f)]
        last = j == len(notes) - 1
        # між нотами — коротке ковзання кулісою; остання наприкінці провисає майже на півтону
        pts += [(t0 + d * 0.55, f), (t0 + d, f * 2 ** (-0.8 / 12))] if last else [(t0 + d, f)]
        i0, i1 = sec(t0), min(n, sec(t0 + d + (0.3 if last else 0.1)))
        for i in range(i0, i1):
            x = (i - i0) / SR
            a = min(1.0, x / 0.04)                    # атака язиком
            if last:
                tail = max(0.0, (x - d * 0.6) / (d * 0.5))
                a *= max(0.0, 1 - tail) ** 1.5
                br = 0.55 + 0.45 * math.sin(TAU * 2.6 * x - 1.2)   # «уа-уа» плунжером на довгій ноті
            else:
                a *= 1 - 0.55 * min(1.0, max(0.0, (x - d * 0.6) / (d * 0.4)))  # наприкінці ноти плунжер прикриває
                if x > d:                             # хвіст гасне під атаку наступної — обвідна без стрибків
                    a *= max(0.0, 1 - (x - d) / 0.1)
                br = math.sin(math.pi * min(1.0, x / d)) ** 0.7
            env[i] = max(env[i], a * 0.9)
            bright[i] = max(bright[i], br)
    _tail(env, 30)
    vib = (5.2, 0.012, 2.2)                           # на останній ноті вібрато широке, як комічне «е-е-е»
    s = voice(pts, env, "brassdark", bright, "brass", vib=vib)
    low = voice([(t, f / 2) for t, f in pts], env, "brassdark", vib=vib)
    m = Mix(total + 0.6)
    m.add(s, 0.0, 0.8, -0.1, 0.25)
    m.add(low, 0.0, 0.35, 0.15, 0.2)
    return m, dict(size=0.85, wet=0.25)


def a_dzen(rng):
    """Глечики-маримба: весела пентатонна арпеджія, глиняне «бум» на сильні долі, тремоло й фінальне «дзень»."""
    u = 60 / 132 / 4
    m = Mix(5.9)
    mel = [(65, 1), (69, 1), (72, 1), (69, 1), (74, 1), (72, 1), (69, 1), (72, 1), (77, 2), (74, 1), (72, 1),
           (69, 2), (72, 2),
           (67, 1), (69, 1), (72, 1), (74, 1), (77, 1), (74, 1), (72, 1), (74, 1), (79, 2), (77, 1), (74, 1),
           (72, 2), (77, 2)]
    cache = {}

    def p(nn):
        if nn not in cache:
            cache[nn] = pot(hz(nn), random.Random(nn))
        return cache[nn]

    t = 0.0
    for j, (nn, d) in enumerate(mel):
        m.add(p(nn), t, 0.6 * rng.uniform(0.85, 1.0), 0.35 if j % 2 else -0.35, 0.25)
        t += d * u
    bass = [53, 60, 53, 57, 55, 62, 48, 55]
    for j, nn in enumerate(bass):
        m.add(p(nn), j * 4 * u, 0.55, -0.15, 0.2)
    boom = kick(95, 70, 0.12)
    for j in range(0, 8, 2):
        m.add(boom, j * 4 * u, 0.45, 0.0, 0.15)
    t = 32 * u
    for j in range(8):                                # тремоло двома паличками з крещендо
        m.add(p(84 if j % 2 else 77), t + j * u / 2, 0.25 + 0.05 * j, 0.3 if j % 2 else -0.3, 0.25)
    t += 4 * u
    for nn, pan in ((53, 0.0), (65, -0.3), (69, 0.3), (72, -0.15), (77, 0.15), (89, 0.0)):
        m.add(p(nn), t, 0.55, pan, 0.35)
    m.add(boom, t, 0.6, 0.0, 0.2)
    return m, dict(size=1.0, wet=0.3)


def a_trembita(rng):
    """Трембіта на натуральних обертонах (основний тон D2): довгий поклик і луна, що вертається з гір."""
    f0 = hz(38)
    # (обертон, початок, кінець) — між ними короткі «переломи», як у живого трембітаря
    phrase = [(4, 0.0, 1.0), (6, 1.06, 1.6), (8, 1.66, 2.6), (9, 2.66, 2.95), (8, 3.0, 3.3), (10, 3.36, 3.66),
              (9, 3.72, 3.95), (8, 4.0, 4.55), (6, 4.62, 5.3)]
    total = 5.7
    n = sec(total)
    pts, env = [], zeros(n)
    for k, (h, t0, t1) in enumerate(phrase):
        f = f0 * h
        pts += [(t0, f * (0.97 if k == 0 else 1.0)), (t0 + (0.25 if k == 0 else 0.04), f), (t1, f)]
    pts.append((total, f0 * 6 * 2 ** (-1.5 / 12)))   # наприкінці голос «падає»
    # обвідна суцільна: плавний вступ, а на кожному «переломі» — неглибокий провал (подув язиком), без стрибків
    for i in range(n):
        env[i] = min(1.0, i / sec(0.35))
    for h, t0, t1 in phrase[1:]:
        c, w = sec(t0), sec(0.05)
        for i in range(max(0, c - w), min(n, c + w)):
            env[i] *= 1 - 0.5 * (0.5 + 0.5 * math.cos(math.pi * (i - c) / w))
    for i in range(sec(total - 1.1), n):              # останній звук гасне
        env[i] *= max(0.0, (n - i) / sec(1.1)) ** 0.8
    _tail(env, 20)
    dry = voice(pts, env, "horn", vib=(4.8, 0.007, 0.5))
    breath = mul(bandpass(noise(rng, n), 1200, 1.5), env)
    for i in range(n):
        dry[i] += breath[i] * 0.04
    m = Mix(8.6)
    m.add(dry, 0.0, 0.9, -0.25, 0.3)
    for dt, g, pan, lp in ((0.62, 0.33, 0.7, 2200), (1.3, 0.2, -0.6, 1500), (2.05, 0.11, 0.45, 1000)):
        m.add(lowpass(lowpass(dry, lp), lp), dt, g, pan, 0.5)
    return m, dict(size=1.35, fb=0.86, wet=0.4)


def a_bayan(rng):
    """Весільний туш на баяні: розгін угору, тричі акорд, розгін униз, трель на домінанті, тонічний акорд з міхом."""
    beat = 60 / 132
    s16 = beat / 4
    m = Mix(6.0)
    t = 0.0
    for nn in (62, 64, 66, 67, 69, 71, 72, 73):                      # розгін
        m.add(reed(hz(nn), s16 * 0.9, 0.75), t, 0.4, 0.15, 0.2)
        t += s16
    G = (71, 74, 79)
    C = (72, 76, 79)
    for ch, d in ((G, 0.5), (G, 0.25), (G, 0.25), (C, 0.5), (G, 0.5)):  # «та-та-та, та-там»
        for nn in ch:
            m.add(reed(hz(nn), d * beat * 0.8, 0.85), t, 0.3, 0.2, 0.2)
        t += d * beat
    for nn in (79, 78, 76, 74, 72, 71, 69, 67):                     # розгін униз
        m.add(reed(hz(nn), s16 * 0.9, 0.75), t, 0.4, 0.15, 0.2)
        t += s16
    td = t
    for nn in (72, 74, 78):                                          # D7 і трель угорі
        m.add(reed(hz(nn), 2 * beat, 0.7), t, 0.27, 0.2, 0.2)
    for j in range(16):
        m.add(reed(hz(81 if j % 2 == 0 else 83), beat / 8 * 0.95, 0.75), t + j * beat / 8, 0.3, 0.25, 0.2)
    t += 2 * beat
    tf = t
    for nn in (67, 71, 74, 79, 83):                                  # фінал із «тремтінням міха»
        m.add(reed(hz(nn), 3 * beat, 0.85, shake=0.35), t, 0.27, 0.2, 0.25)
    # ліва рука: бас і акорд-кнопка по черзі («ум-па»)
    lh = [(0, 43, (55, 59, 62)), (1, 50, (55, 59, 62)), (2, 43, (55, 59, 62))]
    t0 = 2 * beat
    for b, bn, ch in lh:
        m.add(reed(hz(bn), beat * 0.45, 0.8), t0 + b * beat, 0.4, -0.25, 0.15)
        for nn in ch:
            m.add(reed(hz(nn), beat * 0.3, 0.6), t0 + (b + 0.5) * beat, 0.2, -0.25, 0.15)
    for b, bn in ((0, 50), (1, 45)):
        m.add(reed(hz(bn), beat * 0.45, 0.8), td + b * beat, 0.4, -0.25, 0.15)
        for nn in (57, 60, 62):
            m.add(reed(hz(nn), beat * 0.3, 0.6), td + (b + 0.5) * beat, 0.2, -0.25, 0.15)
    m.add(reed(hz(43), 3 * beat, 0.85, shake=0.35), tf, 0.45, -0.25, 0.2)
    for nn in (55, 59, 62):
        m.add(reed(hz(nn), 3 * beat, 0.6, shake=0.35), tf, 0.2, -0.25, 0.2)
    return m, dict(size=0.9, wet=0.25)


def a_bells(rng):
    """Передзвін шести дзвонів: «рядами» вниз, кілька перестановок, а наприкінці — всі разом і великий дзвін."""
    names = [81, 79, 77, 76, 74, 72]                 # A5 … C5, перший — найменший
    cache = {nn: bell(hz(nn)) for nn in names}
    rows = [(1, 2, 3, 4, 5, 6), (2, 1, 4, 3, 6, 5), (2, 4, 1, 6, 3, 5), (1, 2, 3, 4, 5, 6)]
    m = Mix(8.6)
    t, step = 0.05, 0.19
    for r, row in enumerate(rows):
        for b in row:
            nn = names[b - 1]
            m.add(cache[nn], t + rng.uniform(-0.008, 0.008), 0.32 * rng.uniform(0.85, 1.0), -0.65 + 0.26 * (b - 1), 0.3)
            t += step
        t += step if r % 2 else 0.0                  # після кожного другого ряду — пауза, як у дзвонарів
    t += 0.1
    for b, nn in enumerate(names):
        m.add(cache[nn], t + b * 0.012, 0.3, -0.65 + 0.26 * b, 0.3)
    m.add(bell(hz(60), big=True), t, 0.6, 0.0, 0.35)                  # великий дзвін, C4
    return m, dict(size=1.3, fb=0.85, wet=0.35, fade=1.2)


def a_applause(rng):
    """Зала аплодує: сотні ляпань, свист і два «бра-во!» кількома голосами."""
    total = 7.4
    m = Mix(total)
    claps = []
    for j in range(16):
        r2 = random.Random(1000 + j)
        n = sec(0.09)
        x = mul(noise(r2, n), perc(0.008 + 0.012 * r2.random(), a=0.0006, length=0.09))
        claps.append(bandpass(x, 800 + 1600 * r2.random(), 1.1 + r2.random()))
    t = 0.0
    while t < total - 0.4:
        dens = min(1.0, t / 0.7) * (1.0 if t < 4.3 else max(0.0, 1 - (t - 4.3) / 2.6))
        rate = 4 + 85 * dens
        if dens > 0.02:
            m.add(claps[rng.randrange(16)], t, rng.uniform(0.3, 0.8) * (0.4 + 0.6 * dens), rng.uniform(-0.85, 0.85), 0.35)
        t += rng.expovariate(rate)
    hum = lowpass(lowpass(noise(rng, sec(total)), 600), 600)        # гомін зали
    for i in range(len(hum)):
        x = i / SR
        hum[i] *= min(1.0, x / 0.8) * max(0.0, min(1.0, (total - 0.6 - x) / 2.5)) * 2.5
    m.add(hum, 0.0, 0.4, 0.0, 0.3)

    def whistle(pts, length, vel):
        env = adsr(length - 0.08, a=0.04, d=0.2, s=0.9, r=0.05, peak=vel)
        s = voice(pts, env, "sine", vib=(6.0, 0.006, 0.1))
        b = mul(bandpass(noise(rng, len(env)), pts[-1][1], 4.0), env)
        return array("d", (s[i] + b[i] * 0.3 for i in range(len(s))))

    m.add(whistle([(0.0, 1500), (0.22, 2700), (0.3, 2700), (0.65, 1300)], 0.7, 0.5), 1.1, 0.5, 0.55, 0.3)
    m.add(whistle([(0.0, 1800), (0.9, 3000)], 0.95, 0.45), 2.9, 0.45, -0.5, 0.3)
    m.add(whistle([(0.0, 2200), (0.1, 2600)], 0.18, 0.5), 4.4, 0.45, 0.35, 0.3)
    m.add(whistle([(0.0, 2200), (0.1, 2600)], 0.2, 0.5), 4.62, 0.45, 0.35, 0.3)
    # «бра-во!»: «а» (F1 750, F2 1250) переходить в «о» (F1 450, F2 820), між ними — провал на «в»
    bravo = ([(0.0, 600), (0.08, 750), (0.3, 750), (0.38, 450)], [(0.0, 1100), (0.08, 1250), (0.3, 1250), (0.38, 820)],
             [(0.0, 0.0), (0.03, 1.0), (0.26, 0.9), (0.31, 0.25), (0.36, 1.0), (0.55, 0.7), (0.65, 0.0)])
    for t0, pans in ((1.7, (-0.6, -0.2, 0.3)), (3.5, (0.5, 0.1, -0.4))):
        for k, pan in enumerate(pans):
            f = (125, 165, 210)[k] * rng.uniform(0.95, 1.05)
            m.add(shout(rng, f, bravo, 0.66), t0 + rng.uniform(0, 0.07), 0.5, pan, 0.35)
    return m, dict(size=1.2, wet=0.35)


def a_hopak(rng):
    """Гопак: власний наспів в українському дорійському ладі (D: ре мі фа соль# ля сі до), скрипка, цимбали-перебір,
    бас і бубон; наприкінці — трель і «Гоп!»."""
    beat = 60 / 152
    q, e, s = beat, beat / 2, beat / 4
    mel = [(69, e), (74, s), (76, s), (77, e), (76, s), (74, s),
           (68, s), (69, s), (71, s), (72, s), (74, e), (69, e),
           (77, s), (76, s), (74, s), (72, s), (71, s), (72, s), (74, s), (76, s),
           (77, e), (76, e), (74, q),
           (81, e), (80, s), (81, s), (77, e), (76, s), (77, s),
           (74, s), (76, s), (77, s), (80, s), (81, e), (81, e),
           (81, s), (80, s), (77, s), (76, s), (74, s), (72, s), (71, s), (68, s),
           (69, e), (74, e)]
    m = Mix(7.6)
    t = 0.0
    for nn, d in mel:
        m.add(fiddle(hz(nn), d * 0.92, 0.85), t, 0.55, -0.2, 0.2)
        t += d
    for j in range(8):                                # трель ре-мі
        m.add(fiddle(hz(74 if j % 2 == 0 else 76), s / 2 * 0.95, 0.75), t + j * s / 2, 0.5, -0.2, 0.2)
    t += q
    m.add(fiddle(hz(74), 0.75, 0.9), t, 0.55, -0.2, 0.25)
    m.add(fiddle(hz(62), 0.75, 0.8), t, 0.35, -0.1, 0.25)
    # гармонія по тактах: ре-мінор і мі-мажор (соль# ладу — терція мі-мажору), останній — фінальний ре-мінор
    Dm, E = (50, 45, (62, 65, 69)), (40, 47, (64, 68, 71))
    harm = [Dm, E, Dm, Dm, Dm, Dm, E, Dm, Dm]
    pluck = {}

    def tsymbaly(nn):                                 # перебір «цимбалами»: гармонійні обертони, удар молоточком
        if nn not in pluck:
            pluck[nn] = partials(hz(nn), [(1, 1.0, 0.35), (2, 0.5, 0.2), (3, 0.3, 0.12), (4, 0.15, 0.08)], a=0.001)
        return pluck[nn]

    dum = kick(90, 62, 0.14)
    jingle = [mul(highpass(noise(random.Random(50 + j), sec(0.12)), 5000), perc(0.03 + 0.01 * j)) for j in range(3)]
    for b, (root, fifth, ch) in enumerate(harm):
        for k in range(2):
            tb = (2 * b + k) * q
            if b == len(harm) - 1 and k == 1:
                break                                 # на останню долю — фінальний удар нижче
            m.add(fiddle(hz(root if k == 0 else fifth), q * 0.5, 0.7), tb, 0.3, 0.15, 0.15)
            for j, nn in enumerate(ch):
                m.add(tsymbaly(nn), tb + e + j * 0.012, 0.18, 0.3, 0.2)
            m.add(dum, tb, 0.55 if k == 0 else 0.35, 0.0, 0.15)
            m.add(jingle[k], tb + e, 0.3, 0.2, 0.15)
            m.add(jingle[2], tb + e + s, 0.15, 0.2, 0.15)
    m.add(dum, t, 0.8, 0.0, 0.2)
    m.add(jingle[2], t, 0.4, 0.2, 0.2)
    m.add(fiddle(hz(38), 0.75, 0.8), t, 0.3, 0.15, 0.2)
    for j, nn in enumerate((62, 65, 69)):
        m.add(tsymbaly(nn), t + j * 0.015, 0.22, 0.3, 0.25)
    hop = ([(0.0, 300), (0.05, 520), (0.25, 480)], [(0.0, 700), (0.05, 900), (0.25, 850)],
           [(0.0, 0.0), (0.02, 1.0), (0.14, 0.8), (0.24, 0.0)])
    for k, pan in enumerate((-0.4, 0.0, 0.4)):
        m.add(shout(rng, (190, 230, 160)[k], hop, 0.26), t + 0.02 * k, 0.45, pan, 0.25)
    return m, dict(size=0.9, wet=0.22)


def a_cosmos(rng):
    """Космос: синт-арпеджіо злітає на чотирьох акордах (з пінг-понг-луною), шумовий злет, басовий удар і широкий акорд."""
    u = 60 / 128 / 4
    m = Mix(7.4)
    arps = [(57, 60, 64, 69, 72, 76, 81, 84), (53, 57, 60, 65, 69, 72, 77, 81),
            (60, 64, 67, 72, 76, 79, 84, 88), (67, 71, 74, 79, 83, 86, 91, 95)]
    bus = zeros(m.n)
    t = 0.0
    for c, notes in enumerate(arps):
        for j, nn in enumerate(notes):
            put(bus, synth(hz(nn), u * 0.8, 0.5 + 0.06 * c, pluck=0.06 + 0.02 * c, r=0.06), t)
            t += u
    hit = t
    for dt, g, pan in ((0.0, 0.7, -0.15), (3 * u, 0.32, 0.6), (6 * u, 0.18, -0.6), (9 * u, 0.09, 0.5)):
        m.add(bus, dt, g, pan, 0.25)
    for c, notes in enumerate(arps):                  # тиха підкладка під арпеджіо
        for j, nn in enumerate(notes[:3]):
            m.add(synth(hz(nn - 12), 8 * u, 0.3, pluck=0.5, a=0.2, r=0.15, detune=9), c * 8 * u, 0.22,
                  (j - 1) * 0.4, 0.3)
    rise = bandpass(noise(rng, sec(hit)), [(0.0, 300), (hit, 7000)], 2.5)
    for i in range(len(rise)):
        rise[i] *= (i / len(rise)) ** 2
    m.add(rise, 0.0, 0.6, 0.0, 0.3)
    m.add(kick(80, 30, 0.6), hit, 1.3, 0.0, 0.1)
    m.add(lowpass(mul(noise(rng, sec(0.6)), perc(0.1)), 900), hit, 0.8, 0.0, 0.3)
    for j, nn in enumerate((48, 55, 60, 64, 67, 72, 76)):
        m.add(synth(hz(nn), 1.3, 0.5, pluck=0.5, a=0.01, r=0.45, detune=12), hit, 0.3, (j - 3) * 0.2, 0.4)
    for j, nn in enumerate((84, 88, 91, 96, 91, 96)):  # іскри вгорі після удару
        m.add(synth(hz(nn), u * 0.6, 0.4, pluck=0.04, r=0.08), hit + 0.25 + j * u, 0.16, 0.6 if j % 2 else -0.6, 0.5)
    return m, dict(size=1.4, fb=0.86, wet=0.4)


def a_solemn(rng):
    """Урочисто: оркестровий тутті — струнні з мелодією, мідь на акордах, литаври з дробом і тарілка на фіналі."""
    beat = 0.6
    m = Mix(8.2)
    mel = [(67, 0, 1), (72, 1, 1.5), (74, 2.5, 0.5), (76, 3, 1), (79, 4, 1), (77, 5, 0.5), (76, 5.5, 0.5),
           (74, 6, 1), (72, 7, 3)]
    for nn, b, d in mel:
        last = d == 3
        m.add(brass(hz(nn), d * beat * 0.96, 0.95 if last else 0.85), b * beat, 0.4, 0.15, 0.3)
        m.add(strings(hz(nn + 12), d * beat * 0.98, 0.8, rng, a=0.06), b * beat, 0.35, -0.45, 0.35)
    # (доля, тривалість, бас, струнні, мідь)
    chords = [(1, 2, 36, (55, 64, 72), (60, 64)), (3, 1, 45, (57, 64, 72), (60, 64)),
              (4, 1, 40, (55, 59, 71), (59, 64)), (5, 1, 41, (57, 65, 69), (60, 65)),
              (6, 1, 43, (55, 62, 71), (59, 62)), (7, 3, 36, (55, 64, 72, 76), (55, 60, 64))]
    for b, d, bass, st, br in chords:
        dur = d * beat * (0.99 if d < 3 else 1.0)
        m.add(strings(hz(bass), dur, 0.9, rng, a=0.05), b * beat, 0.26, 0.25, 0.3)
        m.add(strings(hz(bass + 12), dur, 0.8, rng, a=0.05), b * beat, 0.22, 0.35, 0.3)
        for j, nn in enumerate(st):
            m.add(strings(hz(nn), dur, 0.75, rng, a=0.05), b * beat, 0.2, -0.5 + 0.3 * j, 0.35)
        for j, nn in enumerate(br):
            m.add(brass(hz(nn), dur, 0.7, 0.99), b * beat, 0.2, 0.3 - 0.3 * j, 0.3)
    for k in range(10):                               # дріб литавр на затакті
        m.add(timpani(hz(43), 0.2 + 0.07 * k, rng), k * beat / 10, 0.6, 0.2, 0.25)
    m.add(timpani(hz(36), 1.1, rng), 1 * beat, 0.75, 0.2, 0.25)
    m.add(timpani(hz(43), 0.9, rng), 6 * beat, 0.7, 0.2, 0.25)
    for k in range(10):
        m.add(timpani(hz(43), 0.3 + 0.06 * k, rng), (6 + k / 10) * beat, 0.6, 0.2, 0.25)
    m.add(timpani(hz(36), 1.3, rng), 7 * beat, 0.8, 0.2, 0.3)
    m.add(cymbal(rng, 0.9, 2.6), 7 * beat, 0.35, -0.2, 0.35)
    return m, dict(size=1.3, fb=0.85, wet=0.38)


# id → (зерно, генератор); порядок — як на полиці (spec §1)
ANTHEMS = {
    "fanfare": (11, a_fanfare),
    "drumroll": (12, a_drumroll),
    "chiptune": (13, a_chiptune),
    "trombone": (14, a_trombone),
    "dzen": (15, a_dzen),
    "trembita": (16, a_trembita),
    "bayan": (17, a_bayan),
    "bells": (18, a_bells),
    "applause": (19, a_applause),
    "hopak": (20, a_hopak),
    "cosmos": (21, a_cosmos),
    "solemn": (22, a_solemn),
}


def encode(ff, wav, mp3):
    subprocess.run([ff, "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", wav,
                    "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact",
                    "-ac", "2", "-ar", "44100", "-c:a", "libmp3lame", "-b:a", "128k",
                    "-af", "loudnorm=I=-16:TP=-1.5:LRA=11,aresample=44100", mp3], check=True)


def main():
    ap = argparse.ArgumentParser(description="Синтез готових гімнів Лавки → web/static/anthems/<id>.mp3")
    ap.add_argument("ids", nargs="*", help="які гімни (типово всі)")
    ap.add_argument("--ffmpeg", default=os.environ.get("FFMPEG") or "ffmpeg")
    ap.add_argument("--out", default=os.path.join(ROOT, "web", "static", "anthems"))
    args = ap.parse_args()
    bad = [i for i in args.ids if i not in ANTHEMS]
    if bad:
        sys.exit(f"нема таких гімнів: {', '.join(bad)} (є: {', '.join(ANTHEMS)})")
    os.makedirs(args.out, exist_ok=True)
    ok = True
    with tempfile.TemporaryDirectory(prefix="anthems-") as tmp:
        for aid in args.ids or ANTHEMS:
            seed, fn = ANTHEMS[aid]
            t0 = time.time()
            mix, rv = fn(random.Random(seed))
            fade = rv.pop("fade", 0.25)
            reverb(mix, **rv)
            wav, mp3 = os.path.join(tmp, aid + ".wav"), os.path.join(args.out, aid + ".mp3")
            write_wav(mix, wav, fade)
            encode(args.ffmpeg, wav, mp3)
            size = os.path.getsize(mp3)
            ok &= size <= MAX_BYTES and 4.0 <= mix.n / SR <= 9.0
            flag = "" if size <= MAX_BYTES else "  ← більше 150 КБ!"
            print(f"{aid:9} {mix.n / SR:4.1f} с  {size / 1024:5.1f} КБ  {time.time() - t0:4.1f} с синтезу{flag}")
    if not ok:
        sys.exit("є гімни поза межами 4–9 с або 150 КБ")


if __name__ == "__main__":
    main()
