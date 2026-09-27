"""«Під глеком»: жива перевірка клієнта в headless Chrome (через D:/or-wt/_tools/cdp2.py).

    python docs/games/dev/dice-check.py --site 8228 --cdp 9691 [--what rules,perf,mock] [--shots qa]

rules — minQ із web/games/dice.js проти знімка C# (tests/Hlechyky.Tests/Games/DiceRuleTable.json, його тримає тест
        The_rule_table_the_client_duplicates_matches_the_server) на всіх 4344 випадках + довідкові числа
        біноміальної підказки зі spec §6.6. Так C# і JS звіряються через один і той самий знімок.
perf  — docs/games/dev/dice-perf.js: 300 update() на шістьох у справжньому перебігу (ставка за ставкою, розкриття,
        «Далі» по одному) — середній час JS і JS + розкладка.
mock  — docs/games/dev/dice-mock.js: рідкісні стани без везіння (влучне «Точно!», паліфіко, кісточка, що падає, «Точно!»
        при надлишку, підсумок зі смішними нагородами, лобі на трьох кісточках) — знімки в --shots (--modes — які).

Скрипт лише читає файли репозиторію й пише тимчасовий JS поруч із собою в %TEMP%; Chrome лишається жити
(закрити: python D:/or-wt/_tools/cdp2.py --port <cdp> --kill).
"""
import argparse, json, os, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
CDP = "D:/or-wt/_tools/cdp2.py"

RULES_JS = r"""
const TABLE = __TABLE__;
const src = await (await fetch('/games/dice.js?cb=' + Date.now())).text();
let mod = null;
new Function('HGames', src)({ register(m) { mod = m; }, ui: {} });
const { minQ, binom } = mod.rules;
let i = 0; const bad = [];
for (let p = 0; p <= 180; p++) {
  const prev = p === 0 ? null : { q: Math.floor((p - 1) / 6) + 1, f: (p - 1) % 6 + 1 };
  for (let f = 1; f <= 6; f++) for (let pal = 0; pal < 2; pal++) for (let one = 0; one < 2; one++) {
    const got = minQ(prev, f, pal === 1, one === 1 ? 1 : 2);
    if (got !== TABLE[i]) bad.push({ p, f, pal, one, got, want: TABLE[i] });
    i++;
  }
}
const pc = (n, p, m) => Math.round(binom(n, p, m, false) * 100);
const ref = [[10, 1/3, 1, 98], [10, 1/3, 2, 90], [10, 1/3, 3, 70], [10, 1/3, 4, 44], [10, 1/3, 5, 21],
  [25, 1/3, 8, 63], [25, 1/3, 9, 46], [25, 1/3, 10, 30], [10, 1/6, 1, 84], [10, 1/6, 2, 52], [10, 1/6, 3, 22]];
const binBad = ref.filter(([n, p, m, w]) => pc(n, p, m) !== w).map(([n, p, m, w]) => ({ n, p, m, want: w, got: pc(n, p, m) }));
return { checked: i, tableLen: TABLE.length, mismatches: bad.length, first: bad.slice(0, 5), binomChecked: ref.length, binBad };
"""


def run(args, js_text, shot=None, width=1280, height=800):
    fd, path = tempfile.mkstemp(suffix=".js", prefix="dice-check-")
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(js_text)
    cmd = [sys.executable, CDP, "--port", str(args.cdp), "--width", str(width), "--height", str(height), "--js", path]
    if args.url:
        cmd += ["--url", f"http://127.0.0.1:{args.site}/?cb=dicecheck#games"]
        args.url = False
    if shot:
        cmd += ["--shot", shot]
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", env=env, timeout=180)
        print(out.stdout.strip())
        if out.stderr.strip():
            print(out.stderr.strip())
    finally:
        os.remove(path)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    ap = argparse.ArgumentParser()
    ap.add_argument("--site", type=int, default=8228)
    ap.add_argument("--cdp", type=int, default=9691)
    ap.add_argument("--what", default="rules,perf")
    ap.add_argument("--shots", default="qa")
    ap.add_argument("--modes", default="exact,pal,fall,fallall,exact0,win,lobby3", help="які вигадані стани знімати (mock)")
    a = ap.parse_args()
    a.url = True
    what = set(a.what.split(","))
    if "rules" in what:
        table = json.load(open(os.path.join(ROOT, "tests/Hlechyky.Tests/Games/DiceRuleTable.json"), encoding="utf-8"))["table"]
        print("== правило minQ: JS проти знімка C#")
        run(a, RULES_JS.replace("__TABLE__", json.dumps(table)))
    if "perf" in what:
        print("== update() на шістьох")
        run(a, open(os.path.join(HERE, "dice-perf.js"), encoding="utf-8").read())
    if "mock" in what:
        os.makedirs(os.path.join(ROOT, a.shots), exist_ok=True)
        mock = open(os.path.join(HERE, "dice-mock.js"), encoding="utf-8").read()
        for mode in a.modes.split(","):
            print(f"== вигаданий вид: {mode}")
            run(a, f"window.__mode = '{mode}';\n" + mock, shot=os.path.join(ROOT, a.shots, f"dice-mock-{mode}.png"))


if __name__ == "__main__":
    main()
