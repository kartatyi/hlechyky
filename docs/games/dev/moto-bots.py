"""Боти для живої перевірки «Мотоциклів», «Змійок» і «Понгу» (пакет moto): легкі SignalR-клієнти замість зайвих Chrome.

Кожен бот — окреме з'єднання (JSON-протокол поверх WebSocket, як web/static/signalr.min.js), нік <prefix>1…N.
Або створює стіл сам (--create <гра>, перший бот — господар, решта сідають), або сідає за готовий (--room <id>).
Грають простими правилами: мотоцикли й змійки обирають із «прямо / ліворуч / праворуч» той хід, за яким
найбільше вільного місця (заливка), змійки ще й тягнуться до яблука; понг веде ракетку за м'ячем (`to`, 20/с).

    python moto-bots.py --port 8267 --create tron-party --n 4 [--rounds 3] [--field big] [--prefix бот]
    python moto-bots.py --port 8267 --room <id> --n 1            # підсісти ботом до людини в браузері
    python moto-bots.py --port 8267 --create tron --n 2 --probe  # ще й міряти затримку «поворот → кадр»

Друкує наприкінці: скільки кадрів, інтервал між кадрами (середній, p95, найбільший — чи тримає сервер тик),
розмір кадру й виду на дроті (усе повідомлення SignalR, байт), скільки партій дограно, і з --probe — за скільки мс
після Input('turn') голова справді повернула (середнє й p95; бот на тій самій машині, тож це майже чисте
очікування тика сервера).
"""
import argparse, asyncio, json, random, statistics, sys, time

import websockets

RS = b"\x1e"
DELTAS = [(1, 0), (0, 1), (-1, 0), (0, -1)]
GRID = {"tron", "tron-party", "snake", "snake-party", "snake-coop"}


def pct(xs, p):
    if not xs:
        return 0
    s = sorted(xs)
    return s[min(len(s) - 1, int(len(s) * p))]


class Bot:
    def __init__(self, idx, a, stats):
        self.idx, self.a, self.stats = idx, a, stats
        self.nick = f"{a.prefix}{idx}"
        self.inv = 0
        self.waiters = {}
        self.room = a.room
        self.game = a.create
        self.seat = None
        self.view = None
        self.status = None
        self.w, self.h = 26, 18
        self.bodies = []          # тіла за місцями, голова перша
        self.seen = []
        self.apples = []
        self.alive = 15
        self.frame = None
        self.last_frame_at = None
        self.rng = random.Random(idx * 7919)
        self.sent_tick = -1
        self.ticks = 0
        self.probe = None         # (коли послали, з якої голови, куди)
        self.finished = 0
        self.to_at = 0

    # ---------- дріт ----------
    async def start(self):
        url = f"ws://127.0.0.1:{self.a.port}/hub?nick={self.nick}"
        self.ws = await websockets.connect(url, max_size=None, compression=None, ping_interval=None, open_timeout=30)
        await self.ws.send(b'{"protocol":"json","version":1}' + RS, text=True)
        asyncio.create_task(self.reader())
        asyncio.create_task(self.pinger())

    async def call(self, target, args):
        self.inv += 1
        iid = str(self.inv)
        fut = asyncio.get_running_loop().create_future()
        self.waiters[iid] = fut
        msg = {"type": 1, "invocationId": iid, "target": target, "arguments": args}
        await self.ws.send(json.dumps(msg, ensure_ascii=False, separators=(",", ":")).encode() + RS, text=True)
        return await asyncio.wait_for(fut, 10)

    async def send(self, target, args):
        msg = {"type": 1, "target": target, "arguments": args}
        await self.ws.send(json.dumps(msg, ensure_ascii=False, separators=(",", ":")).encode() + RS, text=True)

    async def input(self, action, payload):
        self.stats["inputs"] += 1
        await self.send("Input", [self.room, action, payload])

    async def pinger(self):
        while True:
            await asyncio.sleep(10)
            try:
                await self.ws.send(b'{"type":6}' + RS, text=True)
            except websockets.ConnectionClosed:
                return

    async def reader(self):
        try:
            while True:
                data = await self.ws.recv(decode=False)
                for rec in data.split(RS):
                    if not rec:
                        continue
                    m = json.loads(rec)
                    if m.get("type") == 3:
                        fut = self.waiters.pop(m.get("invocationId"), None)
                        if fut and not fut.done():
                            fut.set_result(m.get("result"))
                        continue
                    if m.get("type") != 1:
                        continue
                    tgt, args = m.get("target"), m.get("arguments") or []
                    if tgt == "frame" and args and args[0].get("id") == self.room:
                        now = time.perf_counter()
                        if self.idx == 1:
                            st = self.stats
                            st["frames"] += 1
                            st["fbytes"] += len(rec)
                            st["fmax"] = max(st["fmax"], len(rec))
                            f = args[0]["f"]
                            # інтервали міряємо лише в живій грі (не на відліку й не на паузах)
                            if self.last_frame_at is not None and not (f.get("startIn") or f.get("serveIn")):
                                st["gaps"].append((now - self.last_frame_at) * 1000)
                            self.last_frame_at = now
                        self.on_frame(args[0]["f"], now)
                        await self.think()
                    elif tgt == "room" and args and args[0].get("room", {}).get("id") == self.room:
                        rv = args[0]
                        if self.idx == 1:
                            self.stats["views"] += 1
                            self.stats["vbytes"] += len(rec)
                            self.stats["vmax"] = max(self.stats["vmax"], len(rec))
                        self.seat = rv.get("seat")
                        was, self.status = self.status, rv["room"]["status"]
                        self.game = rv["room"]["game"]
                        if rv.get("view") is not self.view:
                            self.on_view(rv.get("view") or {})
                        if was == "playing" and self.status == "finished":
                            self.finished += 1
                            if self.idx == 1:
                                self.stats["finished"] += 1
                                res = rv["room"].get("result") or {}
                                print(f"  партію зіграно: {res.get('text')}", flush=True)
                                if self.finished < self.a.rounds:
                                    asyncio.create_task(self.rematch())
                        self.last_frame_at = None
        except websockets.ConnectionClosed:
            pass

    async def rematch(self):
        await asyncio.sleep(1.5)
        r = await self.call("Rematch", [self.room])
        if r and not r.get("ok"):
            print("  Rematch:", r.get("message"), flush=True)

    # ---------- стан поля ----------
    def on_view(self, v):
        self.view = v
        g = self.game
        if g == "snake":
            self.w, self.h = 26, 18
            self.bodies = [list(v.get("a") or []), list(v.get("b") or [])]
            self.apples = [v.get("apple")] if v.get("apple") is not None else []
        elif g in ("tron", "tron-party", "snake-party"):   # з проходу №3 дуель мотоциклів — у форматі гурту
            self.w, self.h = v.get("width", 26), v.get("height", 18)
            self.bodies = [list(b or []) for b in (v.get("t") or [])]
            self.apples = list(v.get("ap") or [])
            self.alive = v.get("al", 15)
        elif g == "snake-coop":
            self.w, self.h = v.get("width", 26), v.get("height", 18)
            self.bodies = [list(v.get("s") or [])]
            self.apples = [v.get("apple")] if v.get("apple", -1) >= 0 else []
        self.seen = [set(b) for b in self.bodies]

    def on_frame(self, f, now):
        self.frame = f
        self.ticks += 1
        g = self.game
        old = [b[0] if b else None for b in self.bodies]
        if g in ("tron", "tron-party"):
            h2 = f.get("h2") or []
            for i, c in enumerate(f.get("h") or []):
                if i < len(h2) and h2[i] is not None and h2[i] >= 0 and i < len(self.bodies) and h2[i] not in self.seen[i]:
                    self.seen[i].add(h2[i])
                    self.bodies[i].insert(0, h2[i])   # турбо: перша клітинка подвійного кроку
                if c is not None and c >= 0 and i < len(self.bodies) and c not in self.seen[i]:
                    self.seen[i].add(c)
                    self.bodies[i].insert(0, c)
            self.alive = f.get("al", self.alive)
        elif g == "snake":
            self.bodies = [list(f.get("a") or []), list(f.get("b") or [])]
            self.apples = [f.get("apple")] if f.get("apple") is not None else []
        elif g == "snake-party":
            self.bodies = [list(b or []) for b in (f.get("t") or [])]
            self.apples = list(f.get("ap") or [])
            self.alive = f.get("al", self.alive)
        elif g == "snake-coop":
            self.bodies = [list(f.get("s") or [])]
            self.apples = [f.get("apple")] if f.get("apple", -1) >= 0 else []
        # проба затримки: голова повернула туди, куди просили
        if self.probe and g in GRID:
            sent_at, head0, want = self.probe
            me = self.my_body()
            if me and len(me) >= 2 and me[0] != head0 and self.dir_of(me) == want:
                self.stats["lat"].append((now - sent_at) * 1000)
                self.probe = None
            elif now - sent_at > 1.0:
                self.probe = None

    def my_body(self):
        if self.game == "snake-coop":
            return self.bodies[0] if self.bodies else []
        if self.seat is None or self.seat >= len(self.bodies):
            return []
        return self.bodies[self.seat]

    def dir_of(self, body):
        a, b = body[0], body[1]
        dx, dy = a % self.w - b % self.w, a // self.w - b // self.w
        for d, (x, y) in enumerate(DELTAS):
            if (x, y) == (dx, dy):
                return d
        return None

    def ahead(self, cell, d):
        x, y = cell % self.w + DELTAS[d][0], cell // self.w + DELTAS[d][1]
        if x < 0 or y < 0 or x >= self.w or y >= self.h:
            return None
        return y * self.w + x

    def space(self, start, busy, cap=160):
        """Скільки вільних клітинок досяжно з start (заливка до cap) — щоб не заїжджати в глухий кут."""
        seen, stack = {start}, [start]
        while stack and len(seen) < cap:
            c = stack.pop()
            for d in range(4):
                n = self.ahead(c, d)
                if n is not None and n not in busy and n not in seen:
                    seen.add(n)
                    stack.append(n)
        return len(seen)

    # ---------- правила ----------
    async def think(self):
        if self.status != "playing" or self.seat is None:
            return
        f = self.frame or {}
        g = self.game
        if g == "pong":
            await self.pong(f)
            return
        if g not in GRID or f.get("winner") is not None:
            return
        me = self.my_body()
        if len(me) < 2:
            return
        if g in ("tron-party", "snake-party") and not (self.alive & (1 << self.seat)):
            return
        cur = self.dir_of(me)
        if cur is None:
            return
        if f.get("startIn"):
            return
        if self.sent_tick == self.ticks:
            return
        busy = set()
        for b in self.bodies:
            busy.update(b)
        best, score = cur, -1
        opts = [cur, (cur + 1) % 4, (cur + 3) % 4]
        if g == "snake-coop":
            keys = (self.view or {}).get("keys") or [15, 15, 15, 15]
            mask = keys[self.seat] if self.seat < len(keys) else 0
        for d in opts:
            n = self.ahead(me[0], d)
            if n is None or n in busy:
                continue
            s = self.space(n, busy)
            if self.apples and g in ("snake", "snake-party", "snake-coop"):
                ax = min(abs(a % self.w - n % self.w) + abs(a // self.w - n // self.w) for a in self.apples if a is not None and a >= 0) if any(a is not None and a >= 0 for a in self.apples) else 0
                s = s * 10 - ax
            s += self.rng.random() * (3 if d != cur else 5)
            if s > score:
                best, score = d, s
        if best != cur:
            if g == "snake-coop" and not (mask & (1 << best)):
                return
            self.sent_tick = self.ticks
            if self.a.probe and self.probe is None:
                self.probe = (time.perf_counter(), me[0], best)
            await self.input("turn", {"dir": best})

    async def pong(self, f):
        now = time.time()
        if now - self.to_at < 0.05:
            return
        self.to_at = now
        if f.get("mode") == "arena":
            side = self.seat
            along = f.get("bx") if side in (2, 3) else f.get("by")
            await self.input("to", {"y": round(along + self.rng.uniform(-4, 4), 1)})
        else:
            await self.input("to", {"y": round(f.get("by", 50) + self.rng.uniform(-5, 5), 1)})


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8267)
    ap.add_argument("--room")
    ap.add_argument("--create", help="гра: tron, tron-party, snake, snake-party, snake-coop, pong")
    ap.add_argument("--field", help="опція field для «…гуртом» (auto|small|big)")
    ap.add_argument("--opts", help='інші опції столу JSON-ом, напр. {"series":"3","map":"torus","turbo":"1"}')
    ap.add_argument("--n", type=int, default=2)
    ap.add_argument("--prefix", default="бот")
    ap.add_argument("--rounds", type=int, default=1, help="скільки партій зіграти (далі — «Ще раз»)")
    ap.add_argument("--secs", type=int, default=180, help="стеля часу")
    ap.add_argument("--probe", action="store_true", help="міряти затримку поворот → кадр")
    a = ap.parse_args()
    if not a.room and not a.create:
        sys.exit("треба --room або --create")
    stats = {"frames": 0, "fbytes": 0, "fmax": 0, "views": 0, "vbytes": 0, "vmax": 0, "finished": 0,
             "gaps": [], "lat": [], "inputs": 0}
    bots = [Bot(i + 1, a, stats) for i in range(a.n)]
    for b in bots:
        await b.start()
    if a.create:
        opts = {"field": a.field} if a.field else {}
        if a.opts: opts.update(json.loads(a.opts))
        r = await bots[0].call("CreateRoom", [a.create, opts])
        if not r or not r.get("ok"):
            sys.exit(f"CreateRoom: {r}")
        a.room = r["roomId"]
        print("стіл", a.room, flush=True)
        for b in bots:
            b.room = a.room
        await bots[0].send("WatchRoom", [a.room])
        for b in bots[1:]:
            await b.send("WatchRoom", [a.room])
            r = await b.call("JoinRoom", [a.room])
            if not r or not r.get("ok"):
                print(f"{b.nick} JoinRoom: {r}", flush=True)
        await asyncio.sleep(0.5)
        if bots[0].status == "lobby":
            r = await bots[0].call("StartRoom", [a.room])
            if not r or not r.get("ok"):
                print("StartRoom:", r, flush=True)
    else:
        for b in bots:
            await b.send("WatchRoom", [a.room])
            r = await b.call("JoinRoom", [a.room])
            print(f"{b.nick} JoinRoom: {r}", flush=True)
    t0 = time.time()
    while time.time() - t0 < a.secs and bots[0].finished < a.rounds:
        await asyncio.sleep(0.5)
    await asyncio.sleep(0.5)
    s = stats
    gaps = s["gaps"]
    print(f"кадрів {s['frames']}, партій {s['finished']}, Input від усіх {s['inputs']}")
    if gaps:
        print(f"інтервал кадрів, мс: середній {statistics.mean(gaps):.1f}, медіана {statistics.median(gaps):.1f}, "
              f"p95 {pct(gaps, 0.95):.1f}, найбільший {max(gaps):.1f}")
    if s["frames"]:
        print(f"кадр на дроті, байт: середній {s['fbytes'] / s['frames']:.0f}, найбільший {s['fmax']}")
    if s["views"]:
        print(f"вид на дроті, байт: видів {s['views']}, середній {s['vbytes'] / s['views']:.0f}, найбільший {s['vmax']}")
    if s["lat"]:
        print(f"поворот → кадр, мс: {len(s['lat'])} проб, середнє {statistics.mean(s['lat']):.1f}, "
              f"p95 {pct(s['lat'], 0.95):.1f}, найбільше {max(s['lat']):.1f}")
    for b in bots:
        try:
            await b.call("LeaveRoom", [a.room])
        except Exception:
            pass
        await b.ws.close()


if __name__ == "__main__":
    asyncio.run(main())
