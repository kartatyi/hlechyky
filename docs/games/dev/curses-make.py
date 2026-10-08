"""«😈 Прокльони»: звуки, що лунають за столом, коли прокльон спрацьовує (docs/games/specs/flair.md §1.1).

    python docs/games/dev/curses-make.py [--ffmpeg PATH] [--out DIR] [id ...]

Без id — усі п'ять (boo, crickets, funeral, goat, clown); з id — лише ці. «Сумний тромбон» (sadtrombone) тут не
синтезується: прокльон бере той самий файл, що й гімн, — /static/anthems/trombone.mp3.

Синтез — той самий, що й у гімнів: будівельні блоки (таблиці хвиль, ADSR, фільтри, мікс, реверберація, запис WAV і
кодування) беремо з anthems-make.py (його ім'я з дефісом, тож вантажимо через importlib), тут — лише нові
інструменти й самі звуки. Чистий Python без numpy. Проміжний WAV — у тимчасовій теці, ffmpeg кодує його в
<out>/<id>.mp3 — типово web/static/curses/: стерео, 44,1 кГц, 128k, loudnorm I=-16 / TP=-1.5, як гімни.

--ffmpeg — шлях до ffmpeg (типово змінна FFMPEG, інакше той, що в PATH).

Похоронний марш — перші два такти третьої частини Сонати № 2 Шопена (1839 р., суспільне надбання); решта — власне.
Тривалість 3–7 с, файл ≤ 120 КБ (скрипт перевіряє). Перезапуск дає ті самі файли: random.Random із фіксованим
зерном на кожен звук, ffmpeg із +bitexact. Слухати під час перевірки не треба: скрипт друкує тривалість і розмір, а
спектрограми — ffmpeg showspectrumpic.
"""
import argparse, importlib.util, math, os, random, sys, tempfile, time
from array import array

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("anthems_make", os.path.join(HERE, "anthems-make.py"))
A = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(A)

ROOT, SR, TAU, BLOCK = A.ROOT, A.SR, A.TAU, A.BLOCK
sec, zeros, hz, mul, noise = A.sec, A.zeros, A.hz, A.mul, A.noise
voice, adsr, perc, partials = A.voice, A.adsr, A.perc, A.partials
lowpass, highpass, bandpass, Mix = A.lowpass, A.highpass, A.bandpass, A.Mix
MAX_BYTES = 120 * 1024


def lin(pts, t):
    """Значення ламаної [(с, v), …] у момент t — лінійно (для гучності, де бувають нулі)."""
    if t <= pts[0][0]:
        return pts[0][1]
    for (t0, v0), (t1, v1) in zip(pts, pts[1:]):
        if t < t1:
            return v0 + (v1 - v0) * (t - t0) / (t1 - t0)
    return pts[-1][1]


def curve(pts, n):
    """Обвідна з ламаної гучності, по блоках (як частота в voice())."""
    env = zeros(n)
    for b in range(0, n, BLOCK):
        v = lin(pts, b / SR)
        for i in range(b, min(b + BLOCK, n)):
            env[i] = v
    A._tail(env, 10)
    return env


def add(*sigs, g=None):
    """Сума сигналів (довжина — найкоротшого) з вагами g."""
    n = min(len(s) for s in sigs)
    g = g or [1.0] * len(sigs)
    return array("d", (sum(s[i] * w for s, w in zip(sigs, g)) for i in range(n)))


def whistle(rng, pts, length, vel, vib=(6.0, 0.006, 0.1)):
    """Свист: синус по ламаній висоті плюс трохи «повітря» — шум у смузі навколо кінцевої висоти."""
    env = adsr(length - 0.08, a=0.035, d=0.2, s=0.9, r=0.05, peak=vel)
    s = voice(pts, env, "sine", vib=vib)
    b = mul(bandpass(noise(rng, len(env)), pts[-1][1], 3.0), env)
    return add(s, b, g=[1.0, 0.25])


# ---------------------------------------------------------------- бу-у-у

def boo_voice(rng, f, length):
    """Один «бу-у-у»: пилка й «голос» із придихом, форманти «у» (F1 ≈ 320, F2 ≈ 760), висота сповзає вниз."""
    n = sec(length)
    env = curve([(0.0, 0.0), (0.07, 0.75), (length * 0.35, 1.0), (length * 0.7, 0.8), (length, 0.0)], n)
    pts = [(0.0, f * 1.1), (0.18, f), (length * 0.6, f * 0.95), (length, f * 0.8)]
    vib = (rng.uniform(4.5, 6.0), 0.012, 0.15)
    ph = rng.random() * A.TN
    saw = voice(pts, env, "saw", vib=vib, ph=ph)
    vo = voice(pts, env, "voice", vib=vib, ph=ph)
    nz = mul(noise(rng, n), env)
    src = add(saw, vo, nz, g=[0.55, 0.55, 0.12])
    # «б» на початку — губи закриті: F1 нижче й відкривається за 80 мс
    f1 = [(0.0, 200), (0.08, 320 * rng.uniform(0.92, 1.08))]
    f2 = [(0.0, 600), (0.08, 760 * rng.uniform(0.92, 1.08))]
    out = add(bandpass(src, f1, 4.0), bandpass(src, f2, 5.0), lowpass(src, 400), g=[1.0, 0.45, 0.35])
    A._tail(out, 15)
    return out


def c_boo(rng):
    """Зала гуде «бу-у-у» трьома хвилями (два десятки розстроєних голосів), два свисти незгоди й гомін."""
    total = 6.4
    m = Mix(total)
    waves = [(0.0, 0.3, 8), (2.15, 0.3, 7), (4.2, 0.3, 7)]          # три хвилі з подихом між ними
    for w0, spread, cnt in waves:
        for k in range(cnt):
            f = rng.choice((rng.uniform(85, 140), rng.uniform(95, 150), rng.uniform(170, 240)))
            length = rng.uniform(1.5, 1.9)
            m.add(boo_voice(rng, f, length), w0 + rng.uniform(0, spread), rng.uniform(0.25, 0.4),
                  rng.uniform(-0.85, 0.85), 0.35)
    hum = lowpass(lowpass(noise(rng, sec(total)), 500), 500)          # гомін зали
    for i in range(len(hum)):
        x = i / SR
        hum[i] *= min(1.0, x / 0.4) * max(0.0, min(1.0, (total - 0.3 - x) / 1.5)) * 2.5
    m.add(hum, 0.0, 0.12, 0.0, 0.3)
    # свист двома пальцями: різкий підйом, тримає, обривається вниз
    m.add(whistle(rng, [(0.0, 2300), (0.08, 3100), (0.7, 3000), (0.9, 2300)], 0.95, 0.5), 1.3, 0.26, 0.6, 0.3)
    m.add(whistle(rng, [(0.0, 2800), (0.12, 3300), (0.35, 3200), (0.6, 1900)], 0.65, 0.5), 3.4, 0.24, -0.55, 0.3)
    m.add(whistle(rng, [(0.0, 2500), (0.06, 2900), (0.25, 2850)], 0.3, 0.45), 4.6, 0.2, 0.4, 0.3)
    return m, dict(size=1.2, wet=0.35)


# ---------------------------------------------------------------- цвіркуни

def chirp(f, pulses=4, plen=0.016, period=0.028, h2=0.12):
    """Цвірінь: кілька коротких синусових імпульсів (sin²-вікно), висота трохи падає від першого до останнього."""
    n = sec(pulses * period + 0.01)
    out = zeros(n)
    m = sec(plen)
    for p in range(pulses):
        i0 = sec(p * period)
        fr = f * (1 - 0.006 * p)
        inc, inc2 = fr * A.TN / SR, 2 * fr * A.TN / SR
        amp = 1.0 - 0.12 * p
        ph = ph2 = 0.0
        for j in range(m):
            w = math.sin(math.pi * j / m) ** 2 * amp
            out[i0 + j] += (A.SINE[int(ph) & A.MASK] + h2 * A.SINE[int(ph2) & A.MASK]) * w
            ph += inc
            ph2 += inc2
    return out


def cough(rng, f0=150.0):
    """«Кхе-кхе»: два вибухи шуму через форманти «е» плюс коротке дзижчання зв'язок; другий тихіший."""
    out = zeros(sec(0.6))
    for t0, vel, ln in ((0.0, 1.0, 0.26), (0.24, 0.6, 0.22)):
        n = sec(ln)
        env = perc(0.055, a=0.004, length=ln)
        nz = mul(noise(rng, n), env)
        buzz = voice([(0.0, f0 * 1.15), (ln, f0 * 0.85)], env, "voice")
        src = add(nz, buzz, g=[1.0, 0.5])
        s = lowpass(lowpass(add(bandpass(src, [(0.0, 900), (0.05, 550)], 1.5), bandpass(src, 1700, 3.0), src,
                                g=[1.0, 0.6, 0.1]), 3500), 3500)
        A.put(out, s, t0, vel)
    A._tail(out, 20)
    return out


def c_crickets(rng):
    """Ніякова тиша: два цвіркуни ритмічними групами, третій далеко, нічний вітерець і одинокий кашель під кінець."""
    total = 6.6
    m = Mix(total)
    c1, c2, c3 = chirp(4500), chirp(4150, pulses=3, period=0.03), chirp(4850, pulses=5, period=0.024)
    t = 0.15                                                          # перший — групами по три, з паузою
    while t < total - 0.5:
        for k in range(3):
            m.add(c1, t + k * 0.21 + rng.uniform(-0.004, 0.004), 0.5 * (1 - 0.1 * k), -0.45, 0.2)
        t += 0.63 + 0.5 + rng.uniform(-0.03, 0.03)
    t = 0.6                                                           # другий — рівно, рідше, праворуч
    while t < total - 0.4:
        m.add(c2, t, 0.3, 0.55, 0.25)
        t += 0.47 + rng.uniform(-0.02, 0.02)
    t = 0.35                                                          # далекий — тихо, у ревербі
    while t < total - 0.4:
        for k in range(2):
            m.add(c3, t + k * 0.17, 0.1, 0.1, 0.8)
        t += 0.9
    air = lowpass(lowpass(noise(rng, sec(total)), 350), 350)          # нічне повітря
    for i in range(len(air)):
        x = i / SR
        air[i] *= min(1.0, x / 0.6) * (0.8 + 0.2 * math.sin(TAU * 0.3 * x)) * 2.0
    m.add(air, 0.0, 0.18, 0.0, 0.2)
    m.add(cough(rng), 4.45, 0.55, 0.25, 0.3)
    return m, dict(size=1.3, fb=0.85, wet=0.3)


# ---------------------------------------------------------------- похоронний марш

def c_funeral(rng):
    """Шопен, Соната № 2, ч. III (1839): перші два такти — тромбон в октаву, низька мідь «дзвонить» остинато
    (B♭–F / G♭–D♭) на кожну долю, литаври на першу й третю."""
    q = 60 / 72
    m = Mix(6.9)
    # си-бемоль мінор: (MIDI, доля початку, тривалість у долях)
    mel = [(58, 0, 1), (58, 1, 0.75), (58, 1.75, 0.25), (58, 2, 2),
           (61, 4, 0.75), (60, 4.75, 0.25), (60, 5, 0.75), (58, 5.75, 0.25), (58, 6, 0.75), (57, 6.75, 0.25),
           (58, 7, 1.6)]
    for nn, b, d in mel:
        last = b == 7
        dur = d * q * (0.9 if d < 1 else 0.95)
        vel = 0.9 if d >= 1 else 0.75
        m.add(A.brass(hz(nn), dur, vel, 0.99), b * q, 0.45, -0.15, 0.3)
        m.add(A.brass(hz(nn - 12), dur, vel * 0.9, 0.99), b * q, 0.3, 0.1, 0.3)
        if last:
            m.add(A.brass(hz(nn + 12), dur, 0.6, 0.99), b * q, 0.12, -0.3, 0.35)
    for beat in range(8):                                             # остинато: B♭m, далі G♭ над тим самим басом
        notes = (34, 41, 46) if beat % 2 == 0 else (42, 46, 49)
        d = q * (0.85 if beat < 7 else 1.4)
        for j, nn in enumerate(notes):
            m.add(A.brass(hz(nn), d, 0.55, 0.995), beat * q, 0.17, 0.35 - 0.2 * j, 0.3)
    for beat in (0, 2, 4, 6):
        m.add(A.timpani(hz(34 if beat % 4 == 0 else 41), 0.9 if beat % 4 == 0 else 0.7, rng), beat * q, 0.55, 0.15,
              0.3)
    m.add(A.timpani(hz(34), 0.6, rng), 7 * q, 0.5, 0.15, 0.3)
    return m, dict(size=1.35, fb=0.86, wet=0.38, fade=0.6)


# ---------------------------------------------------------------- цап

def bleat(rng, f, length, rate=9.5):
    """«Ме-е-е»: голос із частим тремтінням (гучність і висота пульсують разом), «м» закрите, далі форманти «е»."""
    n = sec(length)
    base = [(0.0, 0.0), (0.03, 0.45), (0.11, 0.5), (0.16, 1.0), (length * 0.75, 0.85), (length, 0.0)]
    env = zeros(n)
    for b in range(0, n, BLOCK):
        t = b / SR
        trem = 1 - 0.65 * (0.5 + 0.5 * math.cos(TAU * rate * t)) * min(1.0, max(0.0, (t - 0.12) / 0.1))
        v = lin(base, t) * trem
        for i in range(b, min(b + BLOCK, n)):
            env[i] = v
    A._tail(env, 10)
    pts = [(0.0, f * 0.8), (0.14, f * 1.08), (length * 0.45, f), (length, f * 0.86)]
    a = voice(pts, env, "voice", vib=(rate, 0.03, 0.1))
    b = voice([(t, v * 1.012) for t, v in pts], env, "saw", vib=(rate, 0.03, 0.1), ph=A.TN / 3)  # хрипке биття
    nz = mul(noise(rng, n), env)
    src = add(a, b, nz, g=[0.7, 0.4, 0.25])
    f1 = [(0.0, 250), (0.11, 260), (0.17, 600)]                       # «м» → «е»
    f2 = [(0.0, 1100), (0.11, 1150), (0.17, 1900)]
    out = add(bandpass(src, f1, 4.0), bandpass(src, f2, 6.0), bandpass(src, 2700, 8.0), lowpass(src, 350),
              g=[1.0, 0.8, 0.35, 0.3])
    A._tail(out, 15)
    return out


def goatbell(rng, f=1240.0):
    """Дзвіночок на шиї: бляшаний, негармонійний, коротко гасне; язичок ще й ляскає (клацання шуму)."""
    body = partials(f, [(1.0, 1.0, 0.32), (1.51, 0.55, 0.22), (2.17, 0.45, 0.14), (2.83, 0.28, 0.1),
                        (3.62, 0.18, 0.06)], a=0.0008, cap=1.5)
    clank = highpass(mul(noise(rng, sec(0.02)), perc(0.003)), 2500)
    for i in range(len(clank)):
        body[i] += clank[i] * 0.6
    return body


def c_goat(rng):
    """Цап: дзвіночок дзеленькає, «ме-е-е», ще дзенькіт, друге «ме-е-е» вище й довше, і дзвіночок наостанок."""
    m = Mix(5.6)
    bells = [goatbell(random.Random(70 + k), 1240 * (1 + 0.003 * k)) for k in range(4)]

    def shake(t0, hits):
        for k, (dt, v) in enumerate(hits):
            m.add(bells[k % 4], t0 + dt, 0.3 * v, 0.35, 0.3)

    shake(0.05, [(0.0, 1.0), (0.11, 0.6), (0.19, 0.8), (0.33, 0.4)])
    m.add(bleat(rng, 330, 1.25), 0.45, 0.8, -0.15, 0.25)
    shake(1.85, [(0.0, 0.7), (0.09, 0.9), (0.2, 0.5)])
    m.add(bleat(rng, 370, 1.6, rate=10.5), 2.35, 0.85, -0.1, 0.25)
    shake(4.2, [(0.0, 0.9), (0.12, 0.5), (0.2, 0.7), (0.31, 0.35), (0.45, 0.2)])
    return m, dict(size=0.9, wet=0.25, fade=0.4)


# ---------------------------------------------------------------- клоун

def honk(rng, f, dur):
    """Клаксон-груша: язичок (квадрат + баянний), підтяг висоти на атаці, гнусавий горб ~1,1 кГц, опуск на відпуску."""
    env = adsr(dur, a=0.012, d=0.06, s=0.85, r=0.02)
    pts = [(0.0, f * 0.75), (0.025, f * 1.03), (0.06, f), (dur, f * 0.98), (dur + 0.06, f * 0.8)]
    sq = voice(pts, env, "square", vib=(28.0, 0.01, 0.0))
    rd = voice(pts, env, "reed", ph=A.TN / 4)
    raw = add(sq, rd, g=[0.6, 0.6])
    buzz = mul(highpass(noise(rng, len(env)), 3000), env)
    return add(bandpass(raw, 1100, 2.0), bandpass(raw, 2500, 3.0), raw, buzz, g=[1.6, 0.7, 0.35, 0.06])


def c_clown(rng):
    """«Бі-біп» клаксоном, свисток-повзунок з'їжджає вниз, і тихий дзвяк тарілочки."""
    m = Mix(4.4)
    m.add(honk(rng, 470, 0.11), 0.08, 0.7, -0.2, 0.15)
    m.add(honk(rng, 470, 0.3), 0.3, 0.75, -0.2, 0.15)
    slide = whistle(rng, [(0.0, 2100), (0.07, 2350), (0.2, 2300), (1.35, 420)], 1.45, 0.6, vib=(7.0, 0.012, 0.05))
    m.add(slide, 0.95, 0.5, 0.25, 0.2)
    tap = A.cymbal(rng, tau=0.18, length=1.2)
    m.add(tap, 2.75, 0.14, 0.45, 0.3)
    m.add(A.snare(rng, tau=0.03, tone=0.6), 2.75, 0.25, 0.1, 0.2)
    return m, dict(size=0.9, wet=0.22, fade=0.35)


# id → (зерно, генератор); порядок — як на полиці (spec §1.1), без sadtrombone
CURSES = {
    "boo": (31, c_boo),
    "crickets": (32, c_crickets),
    "funeral": (33, c_funeral),
    "goat": (34, c_goat),
    "clown": (35, c_clown),
}


def main():
    ap = argparse.ArgumentParser(description="Синтез звуків прокльонів Лавки → web/static/curses/<id>.mp3")
    ap.add_argument("ids", nargs="*", help="які прокльони (типово всі)")
    ap.add_argument("--ffmpeg", default=os.environ.get("FFMPEG") or "ffmpeg")
    ap.add_argument("--out", default=os.path.join(ROOT, "web", "static", "curses"))
    args = ap.parse_args()
    bad = [i for i in args.ids if i not in CURSES]
    if bad:
        sys.exit(f"нема таких прокльонів: {', '.join(bad)} (є: {', '.join(CURSES)}; sadtrombone — це гімн trombone)")
    os.makedirs(args.out, exist_ok=True)
    ok = True
    with tempfile.TemporaryDirectory(prefix="curses-") as tmp:
        for cid in args.ids or CURSES:
            seed, fn = CURSES[cid]
            t0 = time.time()
            mix, rv = fn(random.Random(seed))
            fade = rv.pop("fade", 0.25)
            A.reverb(mix, **rv)
            wav, mp3 = os.path.join(tmp, cid + ".wav"), os.path.join(args.out, cid + ".mp3")
            A.write_wav(mix, wav, fade)
            A.encode(args.ffmpeg, wav, mp3)
            size = os.path.getsize(mp3)
            good = size <= MAX_BYTES and 3.0 <= mix.n / SR <= 7.0
            ok &= good
            flag = "" if good else "  ← поза 3–7 с або 120 КБ!"
            print(f"{cid:9} {mix.n / SR:4.1f} с  {size / 1024:5.1f} КБ  {time.time() - t0:4.1f} с синтезу{flag}")
    if not ok:
        sys.exit("є звуки поза межами 3–7 с або 120 КБ")


if __name__ == "__main__":
    main()
