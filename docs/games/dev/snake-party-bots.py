"""Боти для живої перевірки «Змійок гуртом» (snake-party, прохід №3): легкі SignalR-клієнти замість зайвих Chrome.

Кожен бот — окреме з'єднання (JSON-протокол поверх WebSocket), нік <prefix>1…N. Або створює стіл сам (--create з
опціями, перший бот — господар), або сідає за готовий (--room <id>). Живий бот обирає з «прямо / ліворуч / праворуч»
найпросторіший хід (заливка) і тягнеться до найближчого яблука чи бонуса; вибулий (раунд «до останньої») раз на ~5 с
кидає яблуко або камінь (Act('drop', {cell: -1})).

    python snake-party-bots.py --port 8318 --create --n 4 --opts '{"series":"3","bonus":"1","wrap":"1"}' --rounds 1
    python snake-party-bots.py --port 8318 --room <id> --n 2          # підсісти ботами до людини в браузері

Друкує: скільки кадрів і найбільший кадр на дроті (байт), кидки, результати партій.
"""
import argparse, asyncio, json, random, sys, time

import websockets

RS = b"\x1e"
DELTAS = [(1, 0), (0, 1), (-1, 0), (0, -1)]


def unpack(s, w, h):
    if not s:
        return []
    i = 0
    while i < len(s) and s[i].isdigit():
        i += 1
    c = int(s[:i])
    out = [c]
    for ch in s[i:]:
        x, y = c % w, c // w
        if ch == "r": x += 1
        elif ch == "l": x -= 1
        elif ch == "d": y += 1
        else: y -= 1
        c = (y % h) * w + (x % w)
        out.append(c)
    return out


class Bot:
    def __init__(self, idx, a, stats):
        self.idx, self.a, self.stats = idx, a, stats
        self.nick = f"{a.prefix}{idx}"
        self.inv, self.waiters = 0, {}
        self.room, self.seat, self.status = a.room, None, None
        self.view, self.f = {}, {}
        self.w, self.h, self.wrap, self.timed = 26, 18, False, 0
        self.bodies = [[], [], [], []]
        self.rng = random.Random(idx * 7919)
        self.last_drop = 0
        self.finished = 0
        self.sent_dir = None

    async def start(self):
        self.ws = await websockets.connect(f"ws://127.0.0.1:{self.a.port}/hub?nick={self.nick}", max_size=None,
                                           compression=None, ping_interval=None, open_timeout=30)
        await self.ws.send(b'{"protocol":"json","version":1}' + RS, text=True)
        asyncio.create_task(self.reader())

    async def call(self, target, args):
        self.inv += 1
        iid = str(self.inv)
        fut = asyncio.get_running_loop().create_future()
        self.waiters[iid] = fut
        msg = {"type": 1, "invocationId": iid, "target": target, "arguments": args}
        await self.ws.send(json.dumps(msg, ensure_ascii=False).encode() + RS, text=True)
        return await asyncio.wait_for(fut, 10)

    async def send(self, target, args):
        msg = {"type": 1, "target": target, "arguments": args}
        await self.ws.send(json.dumps(msg, ensure_ascii=False).encode() + RS, text=True)

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
                        if self.idx == 1:
                            self.stats["frames"] += 1
                            self.stats["fmax"] = max(self.stats["fmax"], len(rec))
                        self.on_frame(args[0]["f"])
                        await self.think()
                    elif tgt == "room" and args and args[0].get("room", {}).get("id") == self.room:
                        rv = args[0]
                        self.seat = rv.get("seat")
                        was, self.status = self.status, rv["room"]["status"]
                        v = rv.get("view") or {}
                        if v.get("b") is not None:
                            self.view = v
                            self.w, self.h = v.get("width", 26), v.get("height", 18)
                            self.wrap, self.timed = bool(v.get("wrap")), v.get("timed", 0)
                            self.on_frame(v)
                        if was == "playing" and self.status == "finished":
                            self.finished += 1
                            if self.idx == 1:
                                res = rv["room"].get("result") or {}
                                print(f"  партію зіграно: {res.get('text')}", flush=True)
                                self.stats["finished"] += 1
                                if self.finished < self.a.rounds:
                                    asyncio.create_task(self.rematch())
        except websockets.ConnectionClosed:
            pass

    async def rematch(self):
        await asyncio.sleep(1.5)
        r = await self.call("Rematch", [self.room])
        if self.status == "lobby" or (r and r.get("ok")):
            await self.call("StartRoom", [self.room])

    def on_frame(self, f):
        self.f = f
        self.bodies = [unpack(s, self.w, self.h) for s in (f.get("b") or [])]

    def step(self, cell, d):
        x, y = cell % self.w + DELTAS[d][0], cell // self.w + DELTAS[d][1]
        if self.wrap:
            return (y % self.h) * self.w + (x % self.w)
        if x < 0 or y < 0 or x >= self.w or y >= self.h:
            return -1
        return y * self.w + x

    async def think(self):
        f, s = self.f, self.seat
        if self.status != "playing" or s is None or f.get("startIn") or f.get("winner") is not None:
            return
        alive = (f.get("al", 0) >> s) & 1
        if not alive:
            if not self.timed and time.time() - self.last_drop > 5.3 and not (f.get("dc") or [0] * 4)[s]:
                self.last_drop = time.time()
                asyncio.create_task(self.drop("r" if self.rng.random() < 0.5 else "a"))   # відповідь читає цей самий reader
            return
        body = self.bodies[s] if s < len(self.bodies) else []
        if len(body) < 2:
            return
        busy = set()
        for b in self.bodies:
            busy.update(b[:-1] if b else [])
        busy.update(f.get("rk") or [])
        head = body[0]
        cur = None
        for d in range(4):
            if self.step(head, d) == body[1]:
                cur = (d + 2) % 4
        if cur is None:
            return
        food = list(f.get("ap") or []) + list(f.get("lt") or []) + ([f["bn"][0]] if f.get("bn") else [])
        best, best_d = -1, cur
        for d in (cur, (cur + 1) % 4, (cur + 3) % 4):
            n = self.step(head, d)
            if n < 0 or n in busy:
                continue
            room = self.flood(n, busy, 60)
            near = min((abs(n % self.w - a % self.w) + abs(n // self.w - a // self.w) for a in food), default=0)
            score = room * 10 - near + (5 if d == cur else 0) + self.rng.random()
            if room >= 25:
                score = 1000 - near * 3 + self.rng.random()
            if score > best:
                best, best_d = score, d
        if best_d != cur and best_d != self.sent_dir:
            self.sent_dir = best_d
            await self.send("Input", [self.room, "turn", {"dir": best_d}])
        elif best_d == cur:
            self.sent_dir = None

    async def drop(self, kind):
        r = await self.call("Act", [self.room, "drop", {"cell": -1, "k": kind}])
        self.stats["drops"] += 1
        if r and not r.get("ok"):
            self.stats["drop_fail"] += 1
            if self.stats["drop_fail"] <= 3:
                print("  кидок не вдався:", r.get("message"), flush=True)

    def flood(self, start, busy, cap):
        seen, stack = {start}, [start]
        while stack and len(seen) < cap:
            c = stack.pop()
            for d in range(4):
                n = self.step(c, d)
                if n >= 0 and n not in busy and n not in seen:
                    seen.add(n)
                    stack.append(n)
        return len(seen)


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8318)
    ap.add_argument("--create", action="store_true")
    ap.add_argument("--room")
    ap.add_argument("--n", type=int, default=2)
    ap.add_argument("--opts", default="{}")
    ap.add_argument("--rounds", type=int, default=1)
    ap.add_argument("--prefix", default="бот")
    ap.add_argument("--secs", type=float, default=240)
    a = ap.parse_args()
    stats = {"frames": 0, "fmax": 0, "finished": 0, "drops": 0, "drop_fail": 0}
    bots = [Bot(i + 1, a, stats) for i in range(a.n)]
    for b in bots:
        await b.start()
    if a.create:
        r = await bots[0].call("CreateRoom", ["snake-party", json.loads(a.opts)])
        if not r or not r.get("ok"):
            sys.exit(f"CreateRoom: {r}")
        a.room = r.get("roomId")
        for b in bots:
            b.room = a.room
        for b in bots:
            await b.send("WatchRoom", [a.room])   # кадри летять лише тим, хто дивиться стіл
        for b in bots[1:]:
            await b.call("JoinRoom", [a.room])
        print("стіл", a.room, flush=True)
        await asyncio.sleep(0.5)
        print("StartRoom:", await bots[0].call("StartRoom", [a.room]), flush=True)
    else:
        for b in bots:
            await b.send("WatchRoom", [a.room])
            print(b.nick, "JoinRoom:", await b.call("JoinRoom", [a.room]), flush=True)
    t0 = time.time()
    while time.time() - t0 < a.secs and stats["finished"] < a.rounds:
        await asyncio.sleep(0.5)
    print(f"кадрів {stats['frames']}, найбільший {stats['fmax']} Б, кидків {stats['drops']} (відмов {stats['drop_fail']}), партій {stats['finished']}", flush=True)
    for b in bots:
        try:
            await b.call("LeaveRoom", [a.room])
        except Exception:
            pass


if __name__ == "__main__":
    asyncio.run(main())
