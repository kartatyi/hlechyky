"""Перевірка «C# = JS» для передбачення пакета arena (Крижина й Аерохокей) у справжньому браузері.

Сервер рахує своє тіло/біту функціями IcefloeCore.Glide і HockeyCore.StepPad, браузер — копіями glide() і stepPad()
з web/games/icefloe.js і hockey.js. Еталон — tests/Hlechyky.Tests/Fixtures/arena-predict.json: журнали вводу й
стани після кожної пачки підкроків, записані з C# (тест Prediction_matches_the_browser_fixture звіряє з ним C#).
Цей скрипт відкриває сайт у headless Chrome (cdp2.py), проганяє ті самі журнали через window.IcefloeSim і
window.HockeySim і звіряє КОЖНЕ число до біта. Рівні обидва з файлом — рівні між собою.

    python docs/games/dev/arena-predict.py --port 9641 --site http://127.0.0.1:8223

Друкує підсумок і виходить з кодом 1, якщо хоч одне число не збіглось.
"""
import argparse, json, os, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
FIXTURE = os.path.join(ROOT, "tests", "Hlechyky.Tests", "Fixtures", "arena-predict.json")
CDP = "D:/or-wt/_tools/cdp2.py"

JS = r"""
const fx = __FIXTURE__;
for (let i = 0; i < 80 && !(window.IcefloeSim && window.HockeySim); i++) await sleep(150);
if (!window.IcefloeSim || !window.HockeySim) return { error: 'модулі icefloe/hockey не завантажились' };
// double → 64 біти: порівнюємо рівно, а не «майже»
const f64 = new Float64Array(1), u64 = new BigUint64Array(f64.buffer);
const bits = (v) => { f64[0] = v; return u64[0]; };
const out = { icefloe: { logs: 0, rows: 0, bad: [] }, hockey: { logs: 0, rows: 0, bad: [] } };

const I = window.IcefloeSim;
fx.icefloe.forEach((log, li) => {
  const b = { x: log.start[0], y: log.start[1], vx: log.start[2], vy: log.start[3] };
  log.runs.forEach((r, ri) => {
    const [count, want, mu, thrust] = r;
    for (let i = 0; i < count; i++) I.glide(b, want, mu, thrust);
    const got = [b.x, b.y, b.vx, b.vy], exp = log.expect[ri];
    for (let k = 0; k < 4; k++) if (bits(got[k]) !== bits(exp[k])) out.icefloe.bad.push([li, ri, k, got[k], exp[k]]);
    out.icefloe.rows++;
  });
  out.icefloe.logs++;
});

const Hk = window.HockeySim;
fx.hockey.forEach((log, li) => {
  const p = { x: log.start[0], y: log.start[1] };
  log.runs.forEach((r, ri) => {
    const [count, aim, tx0, ty0, dx, dy] = r;
    const t = { x: tx0, y: ty0 };
    Hk.clampAim(log.team, t);
    for (let i = 0; i < count; i++) Hk.stepPad(p, log.team, aim === 1, t.x, t.y, dx, dy);
    const got = [p.x, p.y], exp = log.expect[ri];
    for (let k = 0; k < 2; k++) if (bits(got[k]) !== bits(exp[k])) out.hockey.bad.push([li, ri, k, got[k], exp[k]]);
    out.hockey.rows++;
  });
  out.hockey.logs++;
});
out.icefloe.bad = out.icefloe.bad.slice(0, 10);
out.hockey.bad = out.hockey.bad.slice(0, 10);
return out;
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=9641, help="cdp-порт Chrome")
    ap.add_argument("--site", default="http://127.0.0.1:8223")
    a = ap.parse_args()
    with open(FIXTURE, encoding="utf-8") as f:
        fx = json.load(f)
    code = JS.replace("__FIXTURE__", json.dumps(fx))
    with tempfile.NamedTemporaryFile("w", suffix=".js", delete=False, encoding="utf-8") as t:
        t.write(code)
        path = t.name
    try:
        env = dict(os.environ, PYTHONIOENCODING="utf-8")
        r = subprocess.run([sys.executable, CDP, "--port", str(a.port), "--url", a.site + "/?cb=predict#games",
                            "--nick", "перевірка", "--js", path], capture_output=True, text=True, encoding="utf-8", env=env)
    finally:
        os.unlink(path)
    text = r.stdout.strip()
    start = text.find("{")
    if start < 0:
        print(text, r.stderr)
        sys.exit(2)
    depth, end = 0, start
    for i, ch in enumerate(text[start:], start):
        depth += ch == "{"
        depth -= ch == "}"
        if depth == 0:
            end = i + 1
            break
    res = json.loads(text[start:end])
    if res.get("error"):
        print("ПОМИЛКА:", res["error"])
        sys.exit(2)
    ok = True
    for game in ("icefloe", "hockey"):
        g = res[game]
        bad = len(g["bad"])
        ok &= bad == 0
        print(f"{game}: журналів {g['logs']}, звірено станів {g['rows']}, розбіжностей {bad}")
        for b in g["bad"]:
            print("   журнал {0}, пачка {1}, поле {2}: JS {3!r} ≠ C# {4!r}".format(*b))
    rest = text[end:].strip()
    if rest:
        print(rest)
    print("C# = JS до біта" if ok else "Є РОЗБІЖНОСТІ")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
