"""
fitcheck — чи не стала якась гра гіршою на екранах (перевірка перед деплоєм).

Відкриває кожну гру в headless Chrome на 12 екранах (ПК 1366…2560, ⇔ і ⛶, планшет, телефон стоячи/лежачи, з ⛶ і без),
міряє measure.js-ом (поле нижче екрана, горизонтальна прокрутка, дрібні кнопки/клітинки, внутрішні скролбари, обрізаний
текст…) і порівнює з базою `tools/fitcheck/base/<гра>.json`. Гостьовий нік (не адмін), звук німий (Chrome --mute-audio +
muted на play()), свій Chrome на своєму порті з тимчасовим профілем, кожен стіл закривається одразу після замірів.
Плашка повітряної тривоги схована: вона жива, і без цього замір залежав би від того, чи зараз тривога.

  PY=C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe   # потрібен пакет websocket-client
  $PY tools/fitcheck/fitcheck.py check                  # ігри, змінені від замірів бази (git diff), — порівняти з базою
  $PY tools/fitcheck/fitcheck.py check --since origin/main   # змінені від цього коміту
  $PY tools/fitcheck/fitcheck.py check --games chess,pong    # ці ігри
  $PY tools/fitcheck/fitcheck.py check --all            # увесь каталог (~30 хв)
  $PY tools/fitcheck/fitcheck.py update                 # перезняти базу всього каталогу (після злиття, свідомих змін)
  $PY tools/fitcheck/fitcheck.py update --games chess   # перезняти базу лише цих ігор
  $PY tools/fitcheck/fitcheck.py --kill                 # закрити свій Chrome, якщо щось пішло не так

Сервер (--base, типово http://127.0.0.1:8100) має крутити той код, що перевіряємо: dev.ps1 up у теці з цим кодом.
Інші ключі: --port 9890 (CDP Chrome), --nick fitcheck, --modes d1366,m375 (лише ці екрани), --out <тека сирих замірів>,
--settle 0.9 (с після зміни екрана), --tol 8 (px допуску на «поле нижче екрана»).

«Змінені ігри»: кожен файл бази пам'ятає коміт, з якого його знято (`sha`); `git diff --name-only <sha>` (разом із
незакоміченим і новими файлами) → гра змінена, якщо зачеплено `web/games/<модуль>.*`, `web/games/<модуль>/…` чи
`web/games/<модуль>-*.*` (модуль — поле module каталогу або id). Змінився каркас (`web/games/core.*`, `web/static/style.css`,
`web/index.html`, `web/games/dock.*`) — перевіряються всі ігри. Серверний код гри (src/…) тут не відстежується —
змінив вигляд гри з C# — `check --games <id>`.

«Гірше» (вихід 1): на якомусь екрані бал вищий, ніж у базі (бали — як у summary.py аудиту: поле за екраном 4,
гориз. прокрутка 5, внутрішня прокрутка 2, дрібні клітинки 2, решта 1), або поле опустилось нижче екрана на > --tol px
більше, ніж було, або сторінка стала прокручуватись убік, або гра не відкрилась / екран не виміряно. Краще — просто
друкується (базу тоді варто оновити: `update --games …`). Вихід 2 — не вдалося міряти (сервер, Chrome, git).
"""
import argparse, base64, json, os, shutil, subprocess, sys, tempfile, time, urllib.request
from datetime import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
BASE_DIR = os.path.join(HERE, "base")
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"

MODES = {
    "d1366": (1366, 768, False, False), "d1920": (1920, 1080, False, False), "d1920f": (1920, 1080, False, True),
    "d2560": (2560, 1440, False, False), "d2560f": (2560, 1440, False, True),
    "t768": (768, 1024, True, False), "m375": (375, 812, True, False), "m375f": (375, 812, True, True),
    "l812": (812, 375, True, False), "l812f": (812, 375, True, True),
    "d1920w": (1920, 1080, False, False), "d2560w": (2560, 1440, False, False),   # ⇔ «на всю ширину» на ПК
}
ORDER = ["d1366", "d1920", "d1920w", "d1920f", "d2560", "d2560w", "d2560f", "t768", "m375", "m375f", "l812", "l812f"]
# ваги проблем — ті самі, що в summary.py аудиту екранів
W = {"h-overflow": 5, "field-cut-right": 5, "outside-viewport": 3, "field-below-fold": 4, "field-taller-than-screen": 3,
     "card-below-fold": 1, "inner-scroll": 2, "field-small": 1, "field-capped": 1, "cells-small": 2, "small-buttons": 1,
     "clipped-text": 1, "tiny-fonts": 1, "no-field": 0, "no-card": 6}
UA = {"field-below-fold": "поле нижче екрана", "field-taller-than-screen": "поле вище за вікно",
      "card-below-fold": "низ картки за екраном", "inner-scroll": "внутрішня прокрутка", "field-small": "поле дрібне",
      "field-capped": "поле в стелі", "cells-small": "клітинки дрібні", "small-buttons": "дрібні кнопки",
      "clipped-text": "обрізаний текст", "tiny-fonts": "шрифт < 11 px", "outside-viewport": "вилазить за екран",
      "h-overflow": "гориз. прокрутка сторінки", "field-cut-right": "поле обрізане справа", "no-field": "нема поля",
      "no-card": "нема картки гри"}
FRAMEWORK = ("web/games/core.", "web/static/style.css", "web/index.html", "web/games/dock.")
CLICKER_MODES = ("d1920", "m375")   # коло — один замір на ПК і телефоні (воно важке)


# ---------------------------------------------------------------- Chrome / CDP

def get_json(url, timeout=5):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return json.loads(r.read().decode())


def profile_dir(port):
    return os.path.join(tempfile.gettempdir(), f"hlechyky-fitcheck-{port}")


def ensure_chrome(port):
    try:
        get_json(f"http://127.0.0.1:{port}/json/version")
        return
    except Exception:
        pass
    if not os.path.isfile(CHROME):
        raise SystemExit(f"нема Chrome: {CHROME}")
    subprocess.Popen([CHROME, "--headless=new", f"--remote-debugging-port={port}", f"--user-data-dir={profile_dir(port)}",
                      "--no-first-run", "--no-default-browser-check", "--disable-gpu", "--mute-audio",
                      "--autoplay-policy=user-gesture-required", "--remote-allow-origins=*", "--disk-cache-size=1048576",
                      "--disable-extensions", "--disable-component-update", "--disable-background-networking",
                      "--disable-sync", "about:blank"],
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=0x00000008)
    for _ in range(80):
        try:
            get_json(f"http://127.0.0.1:{port}/json/version")
            return
        except Exception:
            time.sleep(0.25)
    raise SystemExit("Chrome не піднявся")


def kill_chrome(port):
    try:
        v = get_json(f"http://127.0.0.1:{port}/json/version")
        import websocket
        ws = websocket.create_connection(v["webSocketDebuggerUrl"], timeout=10, suppress_origin=True)
        ws.send(json.dumps({"id": 1, "method": "Browser.close"}))
        ws.close()
    except Exception:
        pass
    for _ in range(20):
        try:
            shutil.rmtree(profile_dir(port))
            break
        except FileNotFoundError:
            break
        except Exception:
            time.sleep(0.25)


class Tab:
    def __init__(self, ws_url):
        import websocket
        self.ws = websocket.create_connection(ws_url, timeout=150, suppress_origin=True)
        self.id = 0

    def call(self, method, **params):
        self.id += 1
        mid = self.id
        self.ws.send(json.dumps({"id": mid, "method": method, "params": params}))
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("id") == mid:
                if "error" in msg:
                    raise RuntimeError(f"{method}: {msg['error']}")
                return msg.get("result", {})


# ---------------------------------------------------------------- заміри

def catalog(base):
    try:
        return get_json(base + "/api/games/catalog", timeout=15)["games"]
    except Exception as e:
        raise SystemExit(f"сервер {base} не відповідає ({e}) — підніми dev.ps1 up з кодом, який перевіряєш")


def listed(cat):
    return [g["id"] for g in cat if not g.get("unlisted") and not g.get("off")]


def measure_games(a, ids, modes, log=lambda s: print(s, flush=True)):
    """{гра: {"open": {d|t: результат open()}, "m": {екран: замір measure()}}}"""
    lib = open(os.path.join(HERE, "measure.js"), encoding="utf-8").read()
    ensure_chrome(a.port)
    pages = [t for t in get_json(f"http://127.0.0.1:{a.port}/json") if t.get("type") == "page"]
    tab = Tab(pages[0]["webSocketDebuggerUrl"])
    tab.call("Runtime.enable")
    tab.call("Page.enable")
    tab.call("Page.addScriptToEvaluateOnNewDocument",
             source="try{const p=HTMLMediaElement.prototype.play;HTMLMediaElement.prototype.play=function(){this.muted=true;"
                    "this.volume=0;return p.apply(this,arguments)}}catch(e){}")
    # гостьовий нік — ще до скриптів сторінки; вікно «Хто прийшов?» закриваємо в load()
    tab.call("Page.addScriptToEvaluateOnNewDocument", source="try{localStorage.setItem('nick', %s)}catch(e){}" % json.dumps(a.nick))
    # плашка повітряної тривоги — справжня й жива: з нею картка нижча на 40–80 px, і замір залежав би від того, чи зараз тривога
    tab.call("Page.addScriptToEvaluateOnNewDocument",
             source="document.addEventListener('DOMContentLoaded',()=>{const s=document.createElement('style');"
                    "s.textContent='#tryBar{display:none!important}';document.head.appendChild(s)})")

    def ev(expr, timeout=100000):
        r = tab.call("Runtime.evaluate", expression="(async()=>{var __fitLib=1;" + expr + "})()",
                     awaitPromise=True, returnByValue=True, timeout=timeout)
        if "exceptionDetails" in r:
            d = r["exceptionDetails"]
            return {"jsError": (d.get("exception", {}) or {}).get("description") or d.get("text")}
        return r.get("result", {}).get("value")

    def screen(m):
        w, h, touch, _ = MODES[m]
        tab.call("Emulation.setDeviceMetricsOverride", width=w, height=h, deviceScaleFactor=1, mobile=touch)
        tab.call("Emulation.setTouchEmulationEnabled", enabled=touch, maxTouchPoints=5 if touch else 1)
        tab.call("Emulation.setEmitTouchEventsForMouse", enabled=touch, configuration="mobile")

    def load(m):
        screen(m)
        tab.call("Page.navigate", url=a.base + "/#games")
        time.sleep(4)
        ev("const b=document.getElementById('nickLater'), m=document.getElementById('nickModal'); if (b && m && !m.hidden) b.click(); return 1;")
        ev(lib + "\nreturn 1;")
        if not ev("return await window.__fit.waitHub();"):
            log("  ! хаб не піднявся")

    out = {g: {"open": {}, "m": {}} for g in ids}
    groups = [g for g in ([m for m in modes if not MODES[m][2]], [m for m in modes if MODES[m][2]]) if g]
    for grp in groups:
        load(grp[0])
        for n, gid in enumerate(ids, 1):
            use = [m for m in grp if gid != "clicker" or m in CLICKER_MODES]
            if not use:
                continue
            ev("window.__fit.full(false); return 1;")
            screen(use[0])
            time.sleep(0.5)
            o = ev("return await window.__fit.open(%s);" % json.dumps(gid))
            try:
                if isinstance(o, dict) and o.get("roomId"):
                    for m in use:
                        screen(m)
                        ev("window.__fit.full(%s); window.__fit.wide && window.__fit.wide(%s); return 1;"
                           % ("true" if MODES[m][3] else "false", "true" if m.endswith("w") else "false"))
                        time.sleep(a.settle)
                        r = ev("return window.__fit.measure(%s);" % json.dumps(m))
                        if isinstance(r, dict) and r.get("label"):
                            out[gid]["m"][m] = r
            finally:
                c = ev("return await window.__fit.close();")
                if isinstance(c, dict) and c.get("left") is False:
                    time.sleep(1)
                    ev("return await window.__fit.close();")
            out[gid]["open"][grp[0][0]] = o
            st = o.get("status") if isinstance(o, dict) else None
            log(f"  [{n}/{len(ids)}] {gid} {'ПК' if grp[0][0] == 'd' else 'дотик'}: {(st or o.get('err')) if isinstance(o, dict) else o}")
    return out


def compact(gid, raw, sha):
    """База однієї гри: на кожному екрані бал, типи проблем і числа, які порівнюємо."""
    scr = {}
    for m in ORDER:
        r = raw["m"].get(m)
        if not r:
            continue
        types = sorted({i.split(":")[0] for i in r.get("issues", [])})
        f = r.get("field") or {}
        scr[m] = {"score": sum(W.get(t, 1) for t in types), "issues": r.get("issues", []),
                  "below": f.get("below", 0) or 0, "el": (f.get("el") or "").split(" > ")[-1],
                  "cardBelow": r.get("cardBelow", 0) or 0, "hOver": (r.get("page") or {}).get("hOver", 0) or 0,
                  "status": r.get("status")}
    opened = {k: (v.get("status") if isinstance(v, dict) and v.get("roomId") else None) for k, v in raw["open"].items()}
    return {"game": gid, "sha": sha, "at": datetime.now().strftime("%Y-%m-%d %H:%M"), "open": opened, "screens": scr}


# ---------------------------------------------------------------- git

def git(*args):
    r = subprocess.run(["git", "-C", REPO, *args], capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        raise RuntimeError(r.stderr.strip() or f"git {' '.join(args)}")
    return r.stdout


def head_sha():
    sha = git("rev-parse", "--short=10", "HEAD").strip()
    dirty = git("status", "--porcelain", "--", "web").strip()
    return sha + ("+dirty" if dirty else "")


def changed_files(ref):
    files = set(git("diff", "--name-only", ref.replace("+dirty", ""), "--", "web").split())
    files |= set(git("ls-files", "--others", "--exclude-standard", "--", "web").split())
    return sorted(files)


def games_touched(files, cat):
    """(ігри, причина) — які ігри зачепили ці файли; каркас → усі."""
    fw = [f for f in files if f.startswith(FRAMEWORK)]
    ids = listed(cat)
    if fw:
        return set(ids), "каркас: " + ", ".join(fw[:4]) + (" …" if len(fw) > 4 else "")
    mods = {g["id"]: g.get("module") or g["id"] for g in cat}
    keys = set(mods.values()) | set(ids)
    hit, why = set(), []
    for f in files:
        if not f.startswith("web/games/"):
            continue
        rel = f[len("web/games/"):]
        # власник файла — найдовший модуль/id, з якого він починається: chess.js, slots/slot-cascade-art.css,
        # clicker-album.js → clicker (але snake-modes.js — свій модуль, не snake)
        def owner(r):
            return max((k for k in keys if r.startswith((k + ".", k + "/", k + "-"))), key=len, default=None)
        own = owner(rel)
        if own is None and "/" in rel:   # slots/slot-cascade-art.css → slot-cascade
            own = owner(rel.rsplit("/", 1)[1])
        got = [g for g in ids if own in (g, mods[g])] if own else []
        if not got and "/" in rel:   # спільне в теці родини (slots/kit.js) → усі ігри модуля slot(s)
            d = rel.split("/")[0]
            got = [g for g in ids if mods[g] in (d, d.rstrip("s")) or mods[g].startswith(d + "/")]
        if got:
            hit |= set(got)
        elif not rel.startswith("art/"):
            why.append(rel)
    return hit, ("не прив'язано до гри (не перевіряю): " + ", ".join(why[:6])) if why else ""


# ---------------------------------------------------------------- порівняння

def compare(base, now, tol):
    """[(гірше?, рядок)] для однієї гри."""
    out = []
    g = base["game"]
    for k, st in base.get("open", {}).items():
        if st and not now["open"].get(k):
            out.append((True, f"{g} {'ПК' if k == 'd' else 'дотик'}: не відкрилась (у базі — {st})"))
    for m, b in base["screens"].items():
        n = now["screens"].get(m)
        if n is None:
            if any(now["open"].values()):
                out.append((True, f"{g} {m}: не виміряно (у базі бал {b['score']})"))
            continue
        bt, nt = {i.split(":")[0] for i in b["issues"]}, {i.split(":")[0] for i in n["issues"]}
        bad, good = [], []   # один рядок на гру/екран
        if n["score"] > b["score"]:
            new = [next(i for i in n["issues"] if i.startswith(t)) for t in sorted(nt - bt, key=lambda t: -W.get(t, 1))]
            gone = sorted(bt - nt)
            bad.append(f"бал {b['score']} → {n['score']}"
                       + (f" (з'явилось: {', '.join(UA.get(i.split(':')[0], i) + ' ' + i.partition(':')[2] for i in new)})" if new else "")
                       + (f" (зникло: {', '.join(UA.get(t, t) for t in gone)})" if gone else ""))
        elif n["score"] < b["score"]:
            good.append(f"бал {b['score']} → {n['score']}")
        if n["below"] > max(4, b["below"] + tol):
            bad.append(f"поле нижче екрана {b['below']} → {n['below']} px `{n['el']}`")
        elif b["below"] > 4 and n["below"] < b["below"] - tol:
            good.append(f"поле нижче екрана {b['below']} → {n['below']} px")
        if n["hOver"] > b["hOver"] + 1:
            bad.append(f"прокрутка вбік {b['hOver']} → {n['hOver']} px")
        if bad:
            out.append((True, f"{g} {m}: " + "; ".join(bad)))
        elif good:
            out.append((False, f"{g} {m}: краще — " + "; ".join(good)))
    return out


def load_base(gid):
    p = os.path.join(BASE_DIR, gid + ".json")
    if not os.path.isfile(p):
        return None
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def save_base(c):
    os.makedirs(BASE_DIR, exist_ok=True)
    with open(os.path.join(BASE_DIR, c["game"] + ".json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(c, f, ensure_ascii=False, indent=1)
        f.write("\n")


# ---------------------------------------------------------------- команди

def pick_games(a, cat):
    ids = listed(cat)
    if a.games:
        want = [g for g in a.games.split(",") if g]
        bad = [g for g in want if g not in {x["id"] for x in cat}]
        if bad:
            raise SystemExit("нема в каталозі: " + ", ".join(bad))
        return want, "задано --games"
    if a.all or a.cmd == "update":
        return ids, "увесь каталог"
    # змінені: від --since або від коміту, з якого знято базу кожної гри
    refs = {}
    for g in ids:
        b = load_base(g)
        ref = a.since or (b or {}).get("sha")
        if not ref:
            refs.setdefault(None, []).append(g)
        else:
            refs.setdefault(ref, []).append(g)
    pick, notes = set(refs.pop(None, [])), []
    if pick:
        notes.append(f"без бази: {', '.join(sorted(pick))}")
    for ref, gs in refs.items():
        try:
            files = changed_files(ref)
        except RuntimeError as e:
            notes.append(f"git diff {ref} не вдався ({e}) — перевіряю {len(gs)} ігор цієї бази")
            pick |= set(gs)
            continue
        hit, why = games_touched(files, cat)
        pick |= hit & set(gs)
        if why:
            notes.append(why)
    return [g for g in ids if g in pick], "змінені від бази" + (f" ({a.since})" if a.since else "") + ("; " + "; ".join(notes) if notes else "")


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description="Заміри екранів ігор проти бази (див. шапку файла).")
    ap.add_argument("cmd", nargs="?", choices=["check", "update"], default="check")
    ap.add_argument("--base", default="http://127.0.0.1:8100", help="dev-сервер із кодом, який перевіряємо")
    ap.add_argument("--port", type=int, default=9890, help="порт CDP свого Chrome")
    ap.add_argument("--nick", default="fitcheck", help="гостьовий нік")
    ap.add_argument("--games", default="", help="через кому; інакше — змінені (check) чи всі (update)")
    ap.add_argument("--all", action="store_true", help="check усього каталогу")
    ap.add_argument("--since", default="", help="git-ref: змінені від нього, а не від коміту бази")
    ap.add_argument("--modes", default=",".join(ORDER))
    ap.add_argument("--out", default="", help="тека сирих замірів (типово — тимчасова)")
    ap.add_argument("--settle", type=float, default=0.9)
    ap.add_argument("--tol", type=int, default=8, help="px допуску на «поле нижче екрана»")
    ap.add_argument("--keep", action="store_true", help="не закривати Chrome наприкінці")
    ap.add_argument("--kill", action="store_true")
    a = ap.parse_args()
    if a.kill:
        kill_chrome(a.port)
        print("Chrome закрито")
        return 0
    a.base = a.base.rstrip("/")
    modes = [m for m in ORDER if m in a.modes.split(",")]
    cat = catalog(a.base)
    try:
        sha = head_sha()
        ids, why = pick_games(a, cat)
    except RuntimeError as e:
        print("git:", e)
        return 2
    print(f"fitcheck {a.cmd}: {len(ids)} ігор — {why}", flush=True)
    if not ids:
        print("нічого перевіряти — ок")
        return 0
    t0 = time.time()
    try:
        raw = measure_games(a, ids, modes)
    finally:
        if not a.keep:
            kill_chrome(a.port)
    if a.out:
        os.makedirs(a.out, exist_ok=True)
        for g, r in raw.items():
            with open(os.path.join(a.out, g + ".json"), "w", encoding="utf-8") as f:
                json.dump(r, f, ensure_ascii=False, indent=1)
    now = {g: compact(g, raw[g], sha) for g in ids}
    print(f"заміри: {time.time() - t0:.0f} с")

    if a.cmd == "update":
        bad = [g for g in ids if not any(now[g]["open"].values())]
        for g in ids:
            if g not in bad:
                if set(modes) != set(ORDER):   # частина екранів — решту лишаємо з попередньої бази
                    old = load_base(g)
                    if old:
                        now[g]["screens"] = {**old["screens"], **now[g]["screens"]}
                save_base(now[g])
        print(f"базу записано: {len(ids) - len(bad)} ігор у {os.path.relpath(BASE_DIR, REPO)}" + (f"; не відкрились (не записано): {', '.join(bad)}" if bad else ""))
        if "+dirty" in sha:
            print("увага: у web/ є незакомічені зміни — база знята з них (sha позначено +dirty)")
        return 2 if bad else 0

    worse, better, nobase = [], [], []
    for g in ids:
        b = load_base(g)
        if not b:
            nobase.append(g)
            continue
        if modes != ORDER:
            b = {**b, "screens": {k: v for k, v in b["screens"].items() if k in modes}}
        for w, line in compare(b, now[g], a.tol):
            (worse if w else better).append(line)
    for line in better:
        print("  + " + line)
    if nobase:
        print("  ? нема бази: " + ", ".join(nobase) + " — додай: fitcheck.py update --games " + ",".join(nobase))
    if worse:
        print(f"ГІРШЕ ({len(worse)}):")
        for line in worse:
            print("  ✗ " + line)
        print("Виправ або, якщо так і задумано, онови базу: fitcheck.py update --games "
              + ",".join(sorted({l.split(' ')[0] for l in worse})))
        return 1
    print(f"ок: {len(ids) - len(nobase)} ігор не гірші за базу" + (" (є покращення — можна оновити базу)" if better else ""))
    return 0


if __name__ == "__main__":
    sys.exit(main())
