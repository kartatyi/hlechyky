"""«Під глеком»: боти для живої перевірки — легкі SignalR-клієнти (JSON поверх WebSocket, як _tools/loadtest/load.py).

    python docs/games/dev/dice-bot.py --port 8228 --room <id> --nicks Петро,Ганна,Іван [--leave Ганна:100]
                                      [--sleepy Іван] [--exact 0.35] [--react 0.3] [--stay] [--rematch 1] [--seconds 600]
    python docs/games/dev/dice-bot.py --port 8228 --create --options '{"dice":"3"}' --nicks Петро,Ганна --start

Кожен бот сидить за столом, дивиться (WatchRoom), читає СВІЙ вид (гра Hidden) і грає як обережна людина:
на своєму ході рахує біноміальну ймовірність поточної ставки зі своїми кісточками; нижче ~35 % — «Брешеш!»,
інакше — найнижча законна ставка на грані, яка найімовірніша з його рукою. Поза чергою інколи каже «Точно!», якщо
шанс «рівно» пристойний. У розкритті тисне «Далі» за 1–2 с. --sleepy — ніки, що ніколи не ходять (так видно
таймер ходу: ⏰-ставку за сплячого й авто-«Брешеш!»), --leave нік:секунд — встати посеред партії, --stay —
лишатись за столом після кінця (чекати на «Ще раз» людини), --react — з такою ймовірністю реагує на чужу ставку
(🤨 якщо не вірить, 😏 якщо вірить, 😂 інакше). Друкує, що робить, і всі відмови сервера.
"""
import argparse, asyncio, json, math, random, sys, time

import websockets

RS = "\x1e"
sys.stdout.reconfigure(encoding="utf-8")


def min_q(prev, f, pal, my_dice):
    if not 1 <= f <= 6:
        return 0
    if prev is None:
        return 0 if (not pal and f == 1) else 1
    if pal:
        if my_dice > 1:
            return prev["q"] + 1 if f == prev["f"] else 0
        return prev["q"] if f > prev["f"] else prev["q"] + 1
    if prev["f"] != 1:
        return (prev["q"] + 1) // 2 if f == 1 else (prev["q"] if f > prev["f"] else prev["q"] + 1)
    return prev["q"] + 1 if f == 1 else 2 * prev["q"] + 1


def binom_ge(n, p, m):
    if m <= 0:
        return 1.0
    if m > n:
        return 0.0
    return sum(math.comb(n, k) * p ** k * (1 - p) ** (n - k) for k in range(m, n + 1))


def binom_eq(n, p, m):
    if m < 0 or m > n:
        return 0.0
    return math.comb(n, m) * p ** m * (1 - p) ** (n - m)


class Bot:
    def __init__(self, nick, port, room, rng, quiet, exact_rate, react_rate=0.0):
        self.nick, self.port, self.room, self.rng, self.quiet = nick, port, room, rng, quiet
        self.exact_rate = exact_rate
        self.react_rate = react_rate
        self.react_key = None
        self.ws = None
        self.inv = 0
        self.pending = {}
        self.view = None
        self.seat = None
        self.status = None
        self.busy = False
        self.acted_key = None
        self.stats = {"bid": 0, "liar": 0, "exact": 0, "ready": 0, "react": 0, "fail": []}
        self.gone = False
        self.sleepy = False

    def log(self, *a):
        if not self.quiet:
            print(f"[{time.strftime('%H:%M:%S')}] {self.nick}:", *a, flush=True)

    async def connect(self):
        url = f"ws://127.0.0.1:{self.port}/hub?nick={self.nick}"
        self.ws = await websockets.connect(url, max_size=None, compression=None, ping_interval=None, open_timeout=30)
        await self.ws.send('{"protocol":"json","version":1}' + RS)
        asyncio.create_task(self.reader())
        asyncio.create_task(self.pinger())
        asyncio.create_task(self.brain())

    async def brain(self):
        while not self.gone:
            await asyncio.sleep(0.25)
            try:
                await self.think()
            except Exception as e:  # noqa: BLE001
                self.log("think впав:", repr(e))

    async def invoke(self, target, args, wait=True):
        self.inv += 1
        iid = str(self.inv)
        fut = asyncio.get_running_loop().create_future()
        self.pending[iid] = fut
        await self.ws.send(json.dumps({"type": 1, "invocationId": iid, "target": target, "arguments": args}, ensure_ascii=False) + RS)
        if wait:
            return await asyncio.wait_for(fut, 20)

    async def send(self, target, args):
        await self.ws.send(json.dumps({"type": 1, "target": target, "arguments": args}, ensure_ascii=False) + RS)

    async def reader(self):
        try:
            async for data in self.ws:
                for rec in data.split(RS):
                    if not rec:
                        continue
                    m = json.loads(rec)
                    if m.get("type") == 3:
                        fut = self.pending.pop(m.get("invocationId"), None)
                        if fut and not fut.done():
                            fut.set_result(m.get("result") if "error" not in m else {"ok": False, "message": m["error"]})
                    elif m.get("type") == 1 and m.get("target") == "room":
                        rv = m["arguments"][0]
                        if rv.get("room", {}).get("id") != self.room:
                            continue
                        self.status = rv["room"]["status"]
                        self.seat = rv.get("seat")
                        self.view = rv.get("view")
        except websockets.ConnectionClosed:
            pass

    async def pinger(self):
        while not self.gone:
            await asyncio.sleep(10)
            try:
                await self.ws.send('{"type":6}' + RS)
            except websockets.ConnectionClosed:
                return

    async def act(self, action, payload):
        r = await self.invoke("Act", [self.room, action, payload])
        if r and r.get("ok"):
            self.stats[action] += 1
        else:
            msg = (r or {}).get("message")
            self.stats["fail"].append(f"{action}:{msg}")
            self.log("ВІДМОВА", action, payload, "→", msg)
        return r

    async def react_later(self, e):
        await asyncio.sleep(self.rng.uniform(0.3, 1.2))
        if not self.gone:
            await self.act("react", {"e": e})

    def my_dice(self, v):
        for p in v.get("players", []):
            if p["seat"] == self.seat:
                return p["dice"]
        return 0

    def chance(self, v, q, f, exact=False):
        my = v.get("my") or []
        wild = v.get("wild") and f != 1
        k = sum(1 for d in my if d == f or (wild and d == 1))
        unknown = max(0, v["total"] - len(my))
        p = 1 / 3 if wild else 1 / 6
        return binom_eq(unknown, p, q - k) if exact else binom_ge(unknown, p, q - k)

    async def think(self):
        v = self.view
        if self.gone or self.sleepy or self.status != "playing" or v is None or self.seat is None or self.busy:
            return
        me = next((p for p in v.get("players", []) if p["seat"] == self.seat), None)
        if not me or not me["alive"]:
            return
        key = (v["round"], len(v.get("history", [])), v["phase"])
        bid0 = v.get("bid")
        if (self.react_rate and v["phase"] == "bid" and bid0 and bid0["seat"] != self.seat and key != self.react_key):
            self.react_key = key
            if self.rng.random() < self.react_rate:
                p = self.chance(v, bid0["q"], bid0["f"])
                asyncio.create_task(self.react_later(0 if p < 0.4 else 1 if p > 0.8 else 2))
        if key == self.acted_key:
            return
        phase = v["phase"]
        if phase == "reveal":
            if self.seat in (v.get("ready") or []):
                return
            self.acted_key = key
            self.busy = True
            await asyncio.sleep(self.rng.uniform(0.8, 2.2))
            self.busy = False
            if self.view and self.view["phase"] == "reveal" and self.view["round"] == key[0]:
                await self.act("ready", None)
            return
        if phase != "bid":
            return
        bid = v.get("bid")
        # «Точно!» поза чергою
        if bid and v.get("canExact") and self.rng.random() < self.exact_rate and self.chance(v, bid["q"], bid["f"], True) > 0.28:
            self.acted_key = key
            self.busy = True
            await asyncio.sleep(self.rng.uniform(0.7, 1.5))
            self.busy = False
            if self.view and self.view.get("bid") == bid and self.view["phase"] == "bid":
                self.log("Точно!", bid)
                await self.act("exact", {"q": bid["q"], "f": bid["f"]})
            return
        if v.get("turn") != self.seat:
            return
        self.acted_key = key
        self.busy = True
        await asyncio.sleep(self.rng.uniform(0.9, 2.4))
        self.busy = False
        v = self.view
        if not v or v["phase"] != "bid" or v.get("turn") != self.seat:
            return
        bid = v.get("bid")
        if bid and self.chance(v, bid["q"], bid["f"]) < 0.36:
            self.log("Брешеш!", bid)
            await self.act("liar", {"q": bid["q"], "f": bid["f"]})
            return
        md = self.my_dice(v)
        my = v.get("my") or []
        best = None
        for f in [2, 3, 4, 5, 6, 1]:
            m = min_q(bid, f, v["palifico"], md)
            if m <= 0 or m > v["total"]:
                continue
            p = self.chance(v, m, f)
            score = p + self.rng.uniform(0, 0.08)
            if best is None or score > best[0]:
                best = (score, m, f)
        if best is None:
            if bid:
                await self.act("liar", {"q": bid["q"], "f": bid["f"]})
            return
        _, q, f = best
        self.log("ставлю", q, "×", f, "(рука", my, ")")
        await self.act("bid", {"q": q, "f": f})


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8228)
    ap.add_argument("--room")
    ap.add_argument("--create", action="store_true")
    ap.add_argument("--options", default="{}")
    ap.add_argument("--start", action="store_true")
    ap.add_argument("--nicks", required=True)
    ap.add_argument("--leave", default="", help="нік:секунд — встати посеред партії")
    ap.add_argument("--seconds", type=int, default=900)
    ap.add_argument("--sleepy", default="", help="ніки через кому, що ніколи не ходять (таймер ходу)")
    ap.add_argument("--quiet", action="store_true")
    ap.add_argument("--exact", type=float, default=0.35)
    ap.add_argument("--react", type=float, default=0.0, help="ймовірність реакції на чужу ставку")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--rematch", type=int, default=0, help="скільки разів натиснути «Ще раз» після кінця")
    ap.add_argument("--stay", action="store_true", help="після кінця партії сидіти далі (чекати на чуже «Ще раз»)")
    a = ap.parse_args()
    rng = random.Random(a.seed)
    nicks = [n for n in a.nicks.split(",") if n]
    bots = []
    room = a.room
    for i, n in enumerate(nicks):
        b = Bot(n, a.port, room, random.Random(rng.random()), a.quiet, a.exact, a.react)
        b.sleepy = n in a.sleepy.split(",")
        await b.connect()
        await asyncio.sleep(0.2)
        if a.create and i == 0 and not room:
            r = await b.invoke("CreateRoom", ["dice", json.loads(a.options)])
            print("CreateRoom", r, flush=True)
            room = r["roomId"]
            b.room = room
        else:
            b.room = room
            r = await b.invoke("JoinRoom", [room])
            print(n, "JoinRoom", r, flush=True)
        await b.send("WatchRoom", [room])
        bots.append(b)
    if a.start:
        await asyncio.sleep(0.5)
        print("StartRoom", await bots[0].invoke("StartRoom", [room]), flush=True)
    leave = {}
    if a.leave:
        for part in a.leave.split(","):
            n, s = part.split(":")
            leave[n] = float(s)
    t0 = time.time()
    rematches = 0
    finished_seen = False
    while time.time() - t0 < a.seconds:
        await asyncio.sleep(0.5)
        for b in bots:
            if b.nick in leave and not b.gone and time.time() - t0 >= leave[b.nick]:
                print(b.nick, "встає:", await b.invoke("LeaveRoom", [room]), flush=True)
                b.gone = True
        st = next((b.status for b in bots if not b.gone), None)
        if st == "finished" and not finished_seen:
            finished_seen = True
            v = next(b.view for b in bots if not b.gone)
            print("ПАРТІЯ:", json.dumps(v.get("result"), ensure_ascii=False), flush=True)
            if rematches < a.rematch:
                rematches += 1
                await asyncio.sleep(2)
                live = next(b for b in bots if not b.gone)
                print("Ще раз:", await live.invoke("Rematch", [room]), flush=True)
                finished_seen = False
            elif not a.stay:
                break
        if st == "playing":
            finished_seen = False
        if all(b.gone for b in bots):
            break
    for b in bots:
        print(b.nick, json.dumps(b.stats, ensure_ascii=False), flush=True)


asyncio.run(main())
