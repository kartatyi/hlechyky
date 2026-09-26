"""Сільське ралі: паритет C# ↔ JS у headless Chrome.

Підставляє журнали з tests/Hlechyky.Tests/Games/RallyReplays/*.json у сторінку (window.__rallyJournals)
і виконує docs/games/dev/rally-parity.js через D:/or-wt/_tools/cdp2.py:

    python docs/games/dev/rally-parity.py --port 9631 --url "http://127.0.0.1:8222/?cb=1#games"

Код виходу 0 — усі OK, 1 — є MISMATCH.
"""
import argparse, glob, json, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", "..", ".."))
CDP = os.environ.get("CDP", "D:/or-wt/_tools/cdp2.py")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--url", required=True)
    ap.add_argument("--nick", default="паритет")
    a = ap.parse_args()
    journals = []
    for f in sorted(glob.glob(os.path.join(ROOT, "tests", "Hlechyky.Tests", "Games", "RallyReplays", "*.json"))):
        with open(f, encoding="utf-8") as fh:
            j = json.load(fh)
        j["name"] = os.path.basename(f)
        journals.append(j)
    code = "window.__rallyJournals = " + json.dumps(journals, ensure_ascii=False, separators=(",", ":")) + ";\n"
    with open(os.path.join(HERE, "rally-parity.js"), encoding="utf-8") as fh:
        code += fh.read()
    run = os.path.join(ROOT, "qa")
    os.makedirs(run, exist_ok=True)
    js = os.path.join(run, "rally-parity-run.js")
    with open(js, "w", encoding="utf-8") as fh:
        fh.write(code)
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    r = subprocess.run([sys.executable, CDP, "--port", str(a.port), "--url", a.url, "--nick", a.nick, "--js", js],
                       capture_output=True, text=True, encoding="utf-8", env=env)
    sys.stdout.write(r.stdout)
    sys.stderr.write(r.stderr)
    sys.exit(0 if "MISMATCH" not in r.stdout and '"OK' in r.stdout else 1)


if __name__ == "__main__":
    main()
