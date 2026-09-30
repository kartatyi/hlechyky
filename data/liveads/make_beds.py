"""Музичні підкладки для живої реклами (сервер кладе під них голос і крутить по колу) і «ба-дум-тсс».

python make_beds.py      ->  beds/<стиль>.mp3, 16 тактів кожна, без голосу; стик у кінці — на межі такту, тож петля рівна;
                             плюс beds/rimshot.mp3
python make_beds.py rim  ->  лише beds/rimshot.mp3 (генератор реклам для нього не потрібен)
Звук підкладок — з генератора реклам data/ads-gen/make_ads.py (там і стилі), сід фіксований. «Ба-дум-тсс» — та сама
логіка, що rimshot() у data/ads-gen/ads2.py, але зі своїм сідом, щоб не залежати від генератора.
"""
import os, subprocess, sys, wave
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
SR = 44100
TONIC = {"folk": 52, "polka": 53, "lounge": 50, "hold": 57, "sport": 52}
FF = os.environ.get("FFMPEG", "D:/or/tools/yt-dlp/ffmpeg.exe")


def write_mp3(m, name, bitrate="64k"):
    wav = os.path.join(HERE, "beds", name + ".wav")
    with wave.open(wav, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR); w.writeframes((m * 32767).astype(np.int16).tobytes())
    subprocess.run([FF, "-v", "error", "-y", "-i", wav, "-ac", "1", "-ar", "44100", "-b:a", bitrate, os.path.join(HERE, "beds", name + ".mp3")], check=True)
    os.remove(wav)


def rimshot():
    """«Ба-дум-тсс»: два томи і тарілка."""
    rng = np.random.default_rng(7)
    n = int(1.2 * SR)
    buf = np.zeros(n)

    def add(sig, t):
        i = int(t * SR)
        m = min(len(sig), n - i)
        buf[i:i + m] += sig[:m]

    def tom(f0, f1, vel):
        m = int(0.35 * SR)
        t = np.arange(m) / SR
        ph = 2 * np.pi * np.cumsum(f1 + (f0 - f1) * np.exp(-t / 0.05)) / SR
        head = np.sin(ph) * np.exp(-t / 0.13)
        snare = rng.normal(0, 1, m) * np.exp(-t / 0.06) * 0.35
        return vel * (head + snare)

    t = np.arange(int(1.0 * SR)) / SR
    x = rng.normal(0, 1, len(t))
    x = x - np.convolve(x, np.ones(3) / 3, mode="same")
    metal = sum(np.sin(2 * np.pi * f * t) for f in (3150, 4870, 6320, 7910)) * 0.15
    cym = (x + metal) * np.exp(-t / 0.32) * 0.55
    add(tom(230, 160, 0.9), 0.0)
    add(tom(170, 110, 1.0), 0.17)
    add(cym, 0.36)
    return buf / (np.max(np.abs(buf)) + 1e-9) * 0.8


def beds():
    sys.path.insert(0, os.path.join(HERE, "..", "ads-gen"))
    import make_ads as M
    for style, tonic in TONIC.items():
        st = M.STYLE[style]
        beat = 60 / st["bpm"]
        bar = 4 * beat
        bars = 16
        n = int(bars * bar * M.SR)
        m = np.zeros(n + 4 * M.SR)
        for k in range(bars):
            M.render_bar(m, style, M.chord(tonic, st["prog"][k % 4]), k * bar, beat)
        # хвіст за межею петлі накладаємо на початок — тоді стик не клацає
        tail = m[n:]
        m = m[:n]
        m[:len(tail)] += tail
        m = m / (np.max(np.abs(m)) + 1e-9) * 0.9
        write_mp3(m, style)
        print(f"{style}: {bars} тактів, {bars * bar:.1f} с")


if __name__ == "__main__":
    if sys.argv[1:] != ["rim"]:
        beds()
    write_mp3(rimshot(), "rimshot", "96k")
    print("rimshot: 1,2 с")
