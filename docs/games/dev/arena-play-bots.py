"""Боти для живої перевірки аркад пакета arena (Прохід по іграх №2, 28.09.2026): Бомбер, Танчики, Кривуля,
Земля, Дуель і Перестрілка. Легкі SignalR-клієнти замість зайвих Chrome — кожен бот окреме з'єднання з ніком
<prefix>1…N; сідає за стіл (JoinRoom), дивиться кадри (WatchRoom) і грає простими, але живими правилами:

  bomber    — тікає з-під вибуху (пошук у ширину до безпечної клітинки), кладе бомбу біля ящика чи суперника,
              лише коли є куди сховатись, решту часу бреде до найближчого ящика;
  tanks     — бачить суперника в ряду чи стовпці без стіни між ними — розвертається й бахкає, інакше їде
              куди їхав і стріляє в цеглу, що перед носом;
  curve     — сам домальовує слід з кадрів (як браузер) і щокадру дивиться на три курси вперед — обирає вільніший;
  territory — обводить прямокутні шматки поля й вертається додому, біля стіни звертає;
  duel      — стріляє за 180–450 мс після «ВОГОНЬ!», зрідка поспішає;
  shootout  — на «Готуйсь» бере ціль навмання, на «ВОГОНЬ!» стріляє за 200–500 мс.

    python arena-play-bots.py --port 8264 --room <id> --game bomber --n 5 [--prefix бот] [--secs 120]
                              [--rematch] [--leave 2:40] [--start]   # --start: бот №1 — господар, тисне «Почати»

Друкує раз на 5 с: скільки кадрів прийшло, середній і найбільший розмір кадру (усе повідомлення SignalR), партії.
"""
import argparse, asyncio, json, math, random, sys, time
from collections import deque

import websockets

RS = b"\x1e"
DELTA = [(1, 0), (0, 1), (-1, 0), (0, -1)]


class Bot:
    def __init__(self, idx, a, stats):
        self.idx, self.a, self.stats = idx, a, stats
        self.nick = f"{a.prefix}{idx}"
        self.inv = 0
        self.seat = None
        self.view = None
        self.frame = None
        self.status = None
        self.rng = random.Random(idx * 7919 + 13)
        self.sent = None
        self.left = False
        self.plan = {}
        self.grid = None      # кривуля: свій растр сліду

    async def start(self):
        url = f"ws://127.0.0.1:{self.a.port}/hub?nick={self.nick}"
        self.ws = await websockets.connect(url, max_size=None, compression=None, ping_interval=None, open_timeout=30)
        await self.ws.send(b'{"protocol":"json","version":1}' + RS, text=True)
        asyncio.create_task(self.reader())
        await self.send("WatchRoom", [self.a.room])
        await self.send("JoinRoom", [self.a.room], reply=True)
        asyncio.create_task(self.pinger())

    async def send(self, target, args, reply=False):
        msg = {"type": 1, "target": target, "arguments": args}
        if reply:
            self.inv += 1
            msg["invocationId"] = str(self.inv)
        await self.ws.send(json.dumps(msg, ensure_ascii=False, separators=(",", ":")).encode() + RS, text=True)

    async def input(self, action, payload=None):
        await self.send("Input", [self.a.room, action, payload if payload is not None else {}])

    async def want(self, action, payload, key):
        """Напрямок «тримають» — шлемо лише зміну, як браузер."""
        if self.sent == key:
            return
        self.sent = key
        await self.input(action, payload)

    async def reader(self):
        try:
            while True:
                data = await self.ws.recv(decode=False)
                for rec in data.split(RS):
                    if not rec or not rec.startswith(b'{"type":1'):
                        continue
                    m = json.loads(rec)
                    tgt, args = m.get("target"), m.get("arguments") or []
                    if tgt == "frame" and args and args[0].get("id") == self.a.room:
                        self.frame = args[0]["f"]
                        if self.idx == 1:
                            self.stats["frames"] += 1
                            self.stats["bytes"] += len(rec)
                            self.stats["max"] = max(self.stats["max"], len(rec))
                        try:
                            await self.think()
                        except Exception as e:   # бот не має падати від дивного кадру
                            print(f"[{self.nick}] думка впала: {e!r}", flush=True)
                    elif tgt == "room" and args and args[0].get("room", {}).get("id") == self.a.room:
                        rv = args[0]
                        self.seat = rv.get("seat")
                        self.view = rv.get("view")
                        was, self.status = self.status, rv["room"]["status"]
                        if self.idx == 1:
                            self.stats["views"] += 1
                            self.stats["vbytes"] += len(rec)
                        if self.a.game == "curve" and self.view and self.view.get("width"):
                            self.curve_view()
                        if self.a.game == "territory" and self.view and isinstance(self.view.get("owner"), str):
                            self.plan = {}
                        if was == "playing" and self.status == "finished":
                            self.stats["finished"] += 1
                            res = rv["room"].get("result") or {}
                            if self.idx == 1:
                                print(f"партію зіграно: {res.get('text')}", flush=True)
                            if self.a.rematch and self.idx == 1 and not self.left:
                                await asyncio.sleep(4)
                                await self.send("Rematch", [self.a.room], reply=True)
                        if was != "playing" and self.status == "playing":
                            self.sent = None
                            self.grid = None
        except websockets.ConnectionClosed:
            pass

    async def pinger(self):
        while True:
            await asyncio.sleep(10)
            try:
                await self.ws.send(b'{"type":6}' + RS, text=True)
            except websockets.ConnectionClosed:
                return

    async def think(self):
        if self.seat is None or self.status != "playing" or self.left:
            return
        await getattr(self, self.a.game)()

    # ---------------------------------------------------------------- бомбер
    async def bomber(self):
        f, v = self.frame, self.view or {}
        if f.get("phase") != "go":
            await self.want("move", {"dir": -1}, -1)
            return
        W, H, SUB = v.get("width", 15), v.get("height", 13), v.get("sub", 12)
        me = (f.get("p") or [])[self.seat]
        if not me or not me.get("alive"):
            return
        walls = set(v.get("walls") or [])
        boxes = set(f.get("boxes") or [])
        bombs = {b["y"] * W + b["x"] for b in f.get("b") or []}
        rng_max = max([p.get("range", 2) for p in f.get("p") or [] if p] + [2])
        cell = round(me["y"] / SUB) * W + round(me["x"] / SUB)

        def free(c, extra=()):
            return 0 <= c < W * H and c not in walls and c not in boxes and c not in bombs and c not in extra

        def blast(bs):
            out = set()
            for b in bs:
                out.add(b)
                for dx, dy in DELTA:
                    x, y = b % W, b // W
                    for _ in range(rng_max):
                        x, y = x + dx, y + dy
                        c = y * W + x
                        if not (0 <= x < W and 0 <= y < H) or c in walls:
                            break
                        out.add(c)
                        if c in boxes:
                            break
            return out

        danger = blast(bombs) | set(f.get("f") or [])

        flames = set(f.get("f") or [])

        def escape(start, bad):
            """Перший крок до найближчої безпечної клітинки (пошук у ширину); None — тікати нікуди."""
            first, q = {start: None}, deque([start])
            while q:
                c = q.popleft()
                if c != start and c not in bad:
                    return first[c]
                for d, (dx, dy) in enumerate(DELTA):
                    n = c + dy * W + dx
                    if n not in first and free(n) and n not in flames:
                        first[n] = d if c == start else first[c]
                        q.append(n)
            return None

        if cell in danger:
            d = escape(cell, danger)
            await self.want("move", {"dir": -1 if d is None else d}, d)
            return
        # біля ящика або суперника — бомба, якщо буде куди втекти
        near_box = any((cell + dy * W + dx) in boxes for dx, dy in DELTA)
        foe = any(p and p.get("alive") and i != self.seat and abs(p["x"] - me["x"]) + abs(p["y"] - me["y"]) <= 2 * SUB
                  for i, p in enumerate(f.get("p") or []))
        if (near_box or foe) and me.get("bombs", 1) > 0 and self.rng.random() < 0.5 and cell not in bombs:
            if escape(cell, danger | blast([cell])) is not None:
                await self.input("bomb")
                return
        # бредемо: тримаємо напрямок, поки можна, інколи міняємо; у небезпеку не заходимо
        d = self.plan.get("d", self.rng.randrange(4))
        options = [k for k, (dx, dy) in enumerate(DELTA) if free(cell + dy * W + dx) and (cell + dy * W + dx) not in danger]
        if d not in options or self.rng.random() < 0.08:
            d = self.rng.choice(options) if options else -1
        self.plan["d"] = d
        await self.want("move", {"dir": d}, d)

    # ---------------------------------------------------------------- танчики
    async def tanks(self):
        f, v = self.frame, self.view or {}
        if f.get("phase") != "go":
            return
        W, H, SUB = v.get("width", 21), v.get("height", 15), v.get("sub", 12)
        men = f.get("p") or []
        me = men[self.seat] if self.seat < len(men) else None
        if not me or not me.get("alive"):
            self.sent = None
            return
        steel = set(v.get("walls") or [])
        bricks = set(f.get("bricks") or [])
        cx, cy = round(me["x"] / SUB), round(me["y"] / SUB)
        now = time.time()

        def clear(x0, y0, x1, y1):
            if x0 == x1:
                rng = range(min(y0, y1) + 1, max(y0, y1))
                return all((y * W + x0) not in steel and (y * W + x0) not in bricks for y in rng)
            rng = range(min(x0, x1) + 1, max(x0, x1))
            return all((y0 * W + x) not in steel and (y0 * W + x) not in bricks for x in rng)

        for i, p in enumerate(men):
            if i == self.seat or not p or not p.get("alive"):
                continue
            px, py = round(p["x"] / SUB), round(p["y"] / SUB)
            if (px == cx or py == cy) and clear(cx, cy, px, py):
                d = (0 if px > cx else 2) if py == cy else (1 if py > cy else 3)
                if me.get("d") != d:
                    await self.want("move", {"dir": d}, d)
                    await asyncio.sleep(0.03)
                    await self.want("move", {"dir": -1}, -1)
                elif me.get("reload", 0) == 0:
                    await self.input("fire")
                return
        d = self.plan.get("d", self.rng.randrange(4))
        ahead = (cy + DELTA[d][1]) * W + (cx + DELTA[d][0])
        if ahead in bricks and me.get("reload", 0) == 0 and self.rng.random() < 0.6:
            await self.input("fire")
        if ahead in steel or ahead in bricks or now > self.plan.get("until", 0):
            if ahead in steel or self.rng.random() < 0.6:
                d = self.rng.randrange(4)
            self.plan["until"] = now + self.rng.uniform(0.6, 1.8)
        self.plan["d"] = d
        await self.want("move", {"dir": d}, d)

    # ---------------------------------------------------------------- кривуля
    def curve_view(self):
        v = self.view
        W, H = v["width"], v["height"]
        self.grid = bytearray(W * H)
        self.gw, self.gh = W, H
        for s in v.get("segments") or []:
            if not s:
                continue
            pts, gaps = s.get("pts") or [], set(s.get("gaps") or [])
            for k in range(len(pts) // 2):
                if k not in gaps:
                    self.paint(pts[2 * k], pts[2 * k + 1])
        self.prev = {}

    def paint(self, x, y):
        for dy in range(-2, 3):
            for dx in range(-2, 3):
                gx, gy = int(x) + dx, int(y) + dy
                if 0 <= gx < self.gw and 0 <= gy < self.gh and dx * dx + dy * dy <= 5:
                    self.grid[gy * self.gw + gx] = 1

    async def curve(self):
        f = self.frame
        if self.grid is None or f.get("r") != self.plan.get("r"):
            if self.view and self.view.get("width"):
                self.curve_view()
            self.plan = {"r": f.get("r")}
        heads = f.get("heads") or []
        for i, h in enumerate(heads):
            if h and h.get("alive") and not h.get("gap"):
                p = self.prev.get(i)
                if p:   # відрізок від минулої голови до нової, як у браузера
                    for k in range(1, 3):
                        self.paint(p[0] + (h["x"] - p[0]) * k / 2, p[1] + (h["y"] - p[1]) * k / 2)
                self.prev[i] = (h["x"], h["y"])
        if f.get("phase") != "play":
            await self.want("turn", {"d": 0}, 0)
            return
        me = heads[self.seat] if self.seat < len(heads) else None
        if not me or not me.get("alive"):
            return
        a = math.radians(me.get("a", 0))

        def room(turn):
            best = 0
            ang, x, y = a, me["x"], me["y"]
            for step in range(1, 26):
                ang += turn * math.radians(7.2) * 1.0
                x += math.cos(ang) * 1.6 * 1.2
                y += math.sin(ang) * 1.6 * 1.2
                if x < 3 or y < 3 or x > self.gw - 3 or y > self.gh - 3:
                    return best
                if step > 3 and self.grid[int(y) * self.gw + int(x)]:
                    return best
                best = step
            return best + 1

        scores = {t: room(t) for t in (0, -1, 1)}
        top = max(scores.values())
        pick = 0 if scores[0] == top else (-1 if scores[-1] >= scores[1] else 1)
        await self.want("turn", {"d": pick}, pick)

    # ---------------------------------------------------------------- земля
    async def territory(self):
        f, v = self.frame, self.view or {}
        heads = f.get("heads") or []
        me = heads[self.seat] if self.seat < len(heads) else None
        if not me or not me.get("alive") or me.get("x", -1) < 0:
            self.plan = {}
            return
        W, H = v.get("width", 40), v.get("height", 30)
        x, y, d = me["x"], me["y"], me["dir"]
        p = self.plan
        if not p:
            p.update({"leg": 0, "len": self.rng.randint(3, 7), "go": 0, "turn": self.rng.choice((1, 3))})
        p["go"] += 1
        want = d
        if p["go"] >= p["len"]:
            p["go"] = 0
            p["leg"] += 1
            p["len"] = self.rng.randint(3, 7) if p["leg"] % 2 == 0 else self.rng.randint(2, 5)
            want = (d + p["turn"]) % 4
        nx, ny = x + DELTA[want][0], y + DELTA[want][1]
        if not (1 <= nx < W - 1 and 1 <= ny < H - 1):
            want = (want + p["turn"]) % 4
            nx, ny = x + DELTA[want][0], y + DELTA[want][1]
            if not (0 <= nx < W and 0 <= ny < H):
                want = (want + 2) % 4
        if want != d and (want + 2) % 4 != d:
            await self.input("turn", {"dir": want})

    # ---------------------------------------------------------------- дуель і перестрілка
    async def duel(self):
        f = self.frame
        ph = f.get("phase")
        key = (ph, f.get("round"), json.dumps(f.get("wins")))
        if ph == "aim" and self.plan.get("key") != key:
            self.plan["key"] = key
            if self.rng.random() < 0.06:
                await asyncio.sleep(self.rng.uniform(0.3, 1.2))
                await self.input("shoot")
        if ph == "fire" and self.plan.get("fired") != key:
            self.plan["fired"] = key
            asyncio.create_task(self.later(self.rng.uniform(0.18, 0.45), "shoot"))

    async def shootout(self):
        f = self.frame
        ph = f.get("phase")
        key = (f.get("round"), json.dumps(f.get("wins")))
        alive = f.get("alive") or []
        if ph == "ready" and self.plan.get("aimed") != key and self.seat < len(alive) and alive[self.seat]:
            self.plan["aimed"] = key
            targets = [i for i, a in enumerate(alive) if a and i != self.seat]
            if targets:
                await self.input("aim", {"at": self.rng.choice(targets)})
        if ph == "fire" and self.plan.get("fired") != key:
            self.plan["fired"] = key
            asyncio.create_task(self.later(self.rng.uniform(0.2, 0.5), "shoot"))

    async def later(self, secs, action):
        await asyncio.sleep(secs)
        await self.input(action)


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8264)
    ap.add_argument("--room", required=True)
    ap.add_argument("--game", choices=["bomber", "tanks", "curve", "territory", "duel", "shootout"], required=True)
    ap.add_argument("--n", type=int, default=1)
    ap.add_argument("--prefix", default="бот")
    ap.add_argument("--secs", type=int, default=120)
    ap.add_argument("--rematch", action="store_true")
    ap.add_argument("--start", action="store_true", help="бот №1 тисне «Почати», коли всі сіли")
    ap.add_argument("--leave", help="номер_бота:секунда — встати посеред партії")
    a = ap.parse_args()
    stats = {"frames": 0, "bytes": 0, "max": 0, "finished": 0, "views": 0, "vbytes": 0}
    bots = [Bot(i + 1, a, stats) for i in range(a.n)]
    for b in bots:
        await b.start()
        await asyncio.sleep(0.15)
    if a.start:
        await asyncio.sleep(0.5)
        await bots[0].send("StartRoom", [a.room], reply=True)
    leave = None
    if a.leave:
        k, s = a.leave.split(":")
        leave = (int(k), float(s))
    t0 = time.time()
    last = t0
    while time.time() - t0 < a.secs:
        await asyncio.sleep(0.5)
        if leave and time.time() - t0 >= leave[1]:
            b = bots[leave[0] - 1]
            b.left = True
            await b.send("LeaveRoom", [a.room], reply=True)
            print(f"[{b.nick}] встає з-за столу на {leave[1]:.0f}-й секунді", flush=True)
            leave = None
        if time.time() - last >= 5:
            last = time.time()
            n = max(1, stats["frames"])
            print(f"кадрів {stats['frames']}, середній {stats['bytes'] / n:.0f} Б, найбільший {stats['max']} Б, "
                  f"видів {stats['views']} ({stats['vbytes'] / max(1, stats['views']):.0f} Б), партій {stats['finished']}", flush=True)
    for b in bots:
        await b.ws.close()


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    asyncio.run(main())
