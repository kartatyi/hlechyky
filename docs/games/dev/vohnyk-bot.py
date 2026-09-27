"""Бот-гравець «Вогника і Краплі» в headless Chrome: підставляє в сторінку записані проходження рівнів
(data/vohnyk/levels/NN.json) і ганяє docs/games/dev/vohnyk-bot.js через D:/or-wt/_tools/cdp2.py.

  сам за двох, рівні 1→2→3 через «Ще раз»:
    python docs/games/dev/vohnyk-bot.py --port 9681 --nick Оля --role host --solo --levels 1,2,3 --shot qa/x.png
  удвох (два Chrome): господар створює стіл, гість сідає за нього, обоє грають свого героя:
    python docs/games/dev/vohnyk-bot.py --port 9682 --nick Петро --role guest --room <id> --levels 4 &
    python docs/games/dev/vohnyk-bot.py --port 9681 --nick Оля --role host --room <id> --levels 4
  (id столу — з HGames.call('CreateRoom', 'vohnyk', {}) у сторінці господаря). Друкує підсумок кожного рівня
  й середній час малювання кадру (drawStats: 300 останніх кадрів).
"""
import argparse, glob, json, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
CDP = "D:/or-wt/_tools/cdp2.py"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--nick", required=True)
    ap.add_argument("--role", default="host")
    ap.add_argument("--levels", default="1")
    ap.add_argument("--room", default="")
    ap.add_argument("--solo", action="store_true")
    ap.add_argument("--shot")
    ap.add_argument("--wait", type=int, default=0)
    ap.add_argument("--width", type=int, default=1280)
    ap.add_argument("--height", type=int, default=800)
    ap.add_argument("--mobile", action="store_true")
    ap.add_argument("--url", default="http://127.0.0.1:8227/?cb=2#games")
    ap.add_argument("--extra", default="")
    a = ap.parse_args()
    sols = {}
    for f in sorted(glob.glob(os.path.join(ROOT, "data", "vohnyk", "levels", "*.json"))):
        with open(f, encoding="utf-8") as fh:
            d = json.load(fh)
        sols[d["n"]] = d["solution"]
    cfg = {"role": a.role, "levels": [int(x) for x in a.levels.split(",") if x], "room": a.room, "solo": a.solo, "extra": a.extra}
    code = "window.__sols = " + json.dumps(sols, separators=(",", ":")) + ";\nwindow.__cfg = " + json.dumps(cfg, ensure_ascii=False) + ";\n"
    with open(os.path.join(HERE, "vohnyk-bot.js"), encoding="utf-8") as fh:
        code += fh.read()
    run = os.path.join(ROOT, "qa")
    os.makedirs(run, exist_ok=True)
    js = os.path.join(run, f"vohnyk-bot-run-{a.port}.js")
    with open(js, "w", encoding="utf-8") as fh:
        fh.write(code)
    cmd = [sys.executable, CDP, "--port", str(a.port), "--nick", a.nick, "--js", js, "--width", str(a.width), "--height", str(a.height)]
    if a.url:
        cmd += ["--url", a.url]
    if a.mobile:
        cmd += ["--mobile"]
    if a.shot:
        cmd += ["--shot", a.shot]
    if a.wait:
        cmd += ["--wait", str(a.wait)]
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", env=env)
    sys.stdout.write(r.stdout)
    sys.stderr.write(r.stderr)


if __name__ == "__main__":
    main()
