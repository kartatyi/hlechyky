"""Жива перевірка Клавоперегонів у власному headless Chrome (D:/or-wt/_tools/cdp2.py) + боти SignalR (trbots.py).

Сервер гри — на 127.0.0.1:8232 (worktree хвилі 2), Chrome — на cdp-порті 9731 (глядач — 9732). Кожен сценарій —
окремий файл scen_*.py, запуск повним шляхом до Python:

    C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe docs/games/dev/typerace-qa/scen_duel.py
    …/scen_ten.py 1920 1080       — десятеро (9 ботів: «метроном», утікач) + глядач, Full HD
    …/scen_f5.py                  — F5 посеред заїзду й «Ще раз»
    …/scen_mobile.py 3            — телефон 375×812: тап по тексту, «свайп» відкинуто
    …/scen_deck.py 10             — Steam Deck 1280×800, ?deck=1, фейковий пад (fakepad.js)
    …/scen_solo.py                — тренування: вибір, рекорд, «Ще раз», «Стоп»
    …/scen_watch.py, scen_leave.py, scen_keyfocus.py, scen_parity.py (журнал JS ↔ C#)

Справжні натиски — через CDP (Input.insertText / dispatchKeyEvent): вони isTrusted, як у людини. Знімки лягають у
<корінь>/qa/ (його нема в гіті).
"""
import json, os, random, sys, time

sys.path.insert(0, 'D:/or-wt/_tools')
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import cdp2  # noqa: E402
from trbots import run_bots  # noqa: E402

PORT = 8232
BASE = f"http://127.0.0.1:{PORT}/?cb={int(time.time())}#games"
def _root():
    d = os.path.dirname(os.path.abspath(__file__))
    while d and not os.path.exists(os.path.join(d, 'liquidsoap', 'radio.liq')):
        up = os.path.dirname(d)
        if up == d: break
        d = up
    return d


QA = os.path.join(_root(), 'qa')
os.makedirs(QA, exist_ok=True)
sys.stdout.reconfigure(encoding='utf-8')


class Page:
    def __init__(self, cdp_port, nick, width=1280, height=1000, mobile=False, url=BASE):
        cdp2.ensure_chrome(cdp_port)
        pages = [t for t in cdp2.get_json(f"http://127.0.0.1:{cdp_port}/json") if t.get("type") == "page"]
        self.tab = cdp2.Tab(pages[0]["webSocketDebuggerUrl"])
        t = self.tab
        t.call("Runtime.enable"); t.call("Log.enable"); t.call("Page.enable")
        self.resize(width, height, mobile)
        t.call("Page.navigate", url=url)
        time.sleep(2.5)
        t.call("Runtime.evaluate", expression=f"localStorage.setItem('nick', {json.dumps(nick)})")
        t.call("Page.reload")
        time.sleep(3.5)
        t.events = []          # консоль до перезавантаження — не наша
        self.nick = nick

    def resize(self, w, h, mobile=False):
        self.tab.call("Emulation.setDeviceMetricsOverride", width=w, height=h, deviceScaleFactor=1, mobile=mobile)
        self.tab.call("Emulation.setTouchEmulationEnabled", enabled=mobile)

    def ev(self, code, timeout=60):
        r = self.tab.call("Runtime.evaluate", expression="(async () => {\n" + code + "\n})()", awaitPromise=True,
                          returnByValue=True, timeout=timeout * 1000)
        if "exceptionDetails" in r:
            d = r["exceptionDetails"]
            raise RuntimeError((d.get("exception", {}) or {}).get("description") or d.get("text"))
        return r.get("result", {}).get("value")

    def shot(self, name, full=False):
        params = {"format": "png"}
        if full:
            params["captureBeyondViewport"] = True
            m = self.tab.call("Page.getLayoutMetrics")
            cs = m.get("cssContentSize") or m.get("contentSize")
            params["clip"] = {"x": 0, "y": 0, "width": cs["width"], "height": cs["height"], "scale": 1}
        data = self.tab.call("Page.captureScreenshot", **params)["data"]
        import base64
        path = os.path.join(QA, name)
        with open(path, "wb") as f:
            f.write(base64.b64decode(data))
        return path

    def logs(self):
        return self.tab.drain_logs()

    def insert(self, text):
        self.tab.call("Input.insertText", text=text)

    def key(self, key, code, vk):
        self.tab.call("Input.dispatchKeyEvent", type="rawKeyDown", key=key, code=code, windowsVirtualKeyCode=vk, nativeVirtualKeyCode=vk)
        self.tab.call("Input.dispatchKeyEvent", type="keyUp", key=key, code=code, windowsVirtualKeyCode=vk, nativeVirtualKeyCode=vk)

    def backspace(self):
        self.key("Backspace", "Backspace", 8)

    def click(self, x, y):
        self.tab.call("Input.dispatchMouseEvent", type="mouseMoved", x=x, y=y)
        self.tab.call("Input.dispatchMouseEvent", type="mousePressed", x=x, y=y, button="left", clickCount=1)
        time.sleep(0.05)
        self.tab.call("Input.dispatchMouseEvent", type="mouseReleased", x=x, y=y, button="left", clickCount=1)

    def tap(self, x, y):
        self.tab.call("Input.dispatchTouchEvent", type="touchStart", touchPoints=[{"x": x, "y": y}])
        time.sleep(0.06)
        self.tab.call("Input.dispatchTouchEvent", type="touchEnd", touchPoints=[])

    # ---- гра ----
    def leave_all(self):
        """Встати з-за всіх столів, де сиджу (після обірваного прогону ніку ще 20 с тримають місце)."""
        return self.ev("""const ids = [...new Set([...document.querySelectorAll('[data-room]')].map(e => e.dataset.room))];
          const out = [];
          for (const id of ids) { const r = await HGames.call('LeaveRoom', id); out.push(id + ':' + r.ok); await new Promise(z => setTimeout(z, 150)); }
          await new Promise(z => setTimeout(z, 1100));   // квота каркаса: 10 викликів на секунду
          return out;""")

    def create(self, game="typerace", opts=None):
        if not game.endswith('-solo'):
            self.leave_all()
        rid = self.ev(f"const r = await HGames.call('CreateRoom', '{game}', {json.dumps(opts or {})}); "
                      "if (!r.ok) return 'ERR ' + r.message; location.hash = '#games/room/' + r.roomId; return r.roomId;")
        time.sleep(1.2)
        self.close_news()
        return rid

    def dismiss(self):
        """Закрити вікна сайту поверх гри («Хто прийшов?», «Що нового») — як зробила б людина."""
        return self.ev("""let n = 0;
          for (const m of document.querySelectorAll('.modal')) {
            if (m.hidden || !m.getClientRects().length) continue;
            const b = [...m.querySelectorAll('button')].find(x => /Не зараз|Зрозуміло|Закрити|Гаразд/.test(x.textContent));
            if (b) { b.click(); n++; }
          }
          if (document.activeElement && document.activeElement !== document.body && !document.activeElement.closest('.tr-wrap')) document.activeElement.blur();
          return n;""")

    def close_news(self):
        return self.ev("await new Promise(r => setTimeout(r, 300)); const b = document.querySelector('.gnews button'); if (b) { b.click(); return true; } return false;")

    def open_solo(self, game="typerace-solo"):
        rid = self.ev(f"const r = await HGames.call('OpenSolo', '{game}', null); location.hash = '#games/room/' + r.roomId; return r.roomId;")
        time.sleep(1.5)
        self.close_news()
        return rid

    def goto(self, rid):
        self.ev(f"location.hash = '#games/room/{rid}'; return 1;")
        time.sleep(1.5)
        self.close_news()

    def state(self):
        return self.ev("const s = __typerace.state(); if (!s) return null; return {phase: s.phase, c: s.c, len: s.len, text: s.text, "
                       "red: s.red, finished: s.finished, k: s.k.length, events: s.events, localGo: s.localGo};")

    def wait_phase(self, phase, timeout=30):
        self.dismiss()
        for _ in range(int(timeout * 5)):
            s = self.state()
            if s and s.get("phase") == phase:
                return s
            time.sleep(0.2)
        raise RuntimeError(f"{self.nick}: фаза {phase} не настала, зараз {self.state()}")

    def focused(self):
        return self.ev("const a = document.activeElement; return a ? a.className : null;")

    def type_text(self, cpm=260, errors=0.04, seed=1, stop_at=None):
        """Друкує поточний текст справжніми натисками: живий ритм, іноді помилка й Backspace."""
        rng = random.Random(seed)
        s = self.state()
        text = s["text"]
        mean = 60.0 / cpm
        i = s["c"]
        end = len(text) if stop_at is None else int(len(text) * stop_at)
        while i < end:
            ch = text[i]
            if ch == '\n':
                ch = ' '
            if ch == '’' and rng.random() < 0.5:
                ch = "'"
            if rng.random() < errors and ch != ' ':
                self.insert('о' if ch != 'о' else 'а')
                time.sleep(mean * (0.5 + rng.random()))
                self.backspace()
                time.sleep(mean * (0.5 + rng.random()))
            self.insert(ch)
            i += 1
            time.sleep(mean * (0.45 + 1.1 * rng.random()))


def room_state(page):
    return page.ev("const s = __typerace.state(); const v = s && s.ctx && s.ctx.view; return v ? {phase: v.phase, racers: v.racers, "
                   "result: v.result, len: v.len} : null;")


def free_mem_gb():
    import subprocess
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "[int]((Get-CimInstance Win32_OperatingSystem).FreeVirtualMemory/1MB)"], capture_output=True, text=True)
    return int(out.stdout.strip() or 0)


if __name__ == "__main__":
    print("див. qa/scen_*.py")
