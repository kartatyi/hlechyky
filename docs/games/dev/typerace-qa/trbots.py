"""Боти-гонщики Клавоперегонів для живої перевірки: легкі SignalR-клієнти (JSON поверх WebSocket, як _tools/loadtest).

Кожен бот сідає за стіл (JoinRoom), дивиться його (WatchRoom), чекає фази go і «друкує» з заданою швидкістю:
шле Input('pos', {c, e}) не частіше 5/с (іноді з червоним), а на кінці — Act('finish', {k, d}) з людським журналом
(ті самі кроки по 4 мс, що й у браузері). robot=True — рівний ритм (суддя має сказати «метроном»).

    from trbots import run_bots
    th = run_bots(8232, room, ["Петро", "Ганна"], cpm=[260, 180], robot=[False, True])
"""
import asyncio, json, random, threading, time

import websockets

RS = b"\x1e"
ALPHA = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"


def enc(ms):
    v = min(4095, max(0, int(ms / 4 + 0.5)))
    return ALPHA[v >> 6] + ALPHA[v & 63]


class Bot:
    def __init__(self, port, room, nick, cpm=240, robot=False, leave_at=None, seed=1, errors=0.03, rounds=1, leave_after=False):
        self.port, self.room, self.nick, self.cpm, self.robot = port, room, nick, cpm, robot
        self.leave_at = leave_at          # частка тексту, на якій бот встає з-за столу (None — дограє)
        self.rng = random.Random(seed)
        self.errors = errors
        self.rounds = rounds
        self.leave_after = leave_after    # встати одразу після свого фінішу (утікач-переможець)
        self.seen = set()
        self.inv = 0
        self.pending = {}
        self.view = None
        self.seat = None
        self.log = []
        self.done = asyncio.Event()
        self.result = None
        self.round = None

    async def invoke(self, target, args, wait=True):
        self.inv += 1
        iid = str(self.inv)
        fut = asyncio.get_running_loop().create_future() if wait else None
        if wait:
            self.pending[iid] = fut
        msg = {"type": 1, "target": target, "arguments": args}
        if wait:
            msg["invocationId"] = iid
        await self.ws.send(json.dumps(msg, ensure_ascii=False).encode() + RS, text=True)
        if wait:
            return await asyncio.wait_for(fut, 30)

    async def reader(self):
        try:
            while True:
                data = await self.ws.recv(decode=False)
                for rec in data.split(RS):
                    if not rec:
                        continue
                    m = json.loads(rec)
                    if m.get("type") == 3:
                        fut = self.pending.pop(m.get("invocationId"), None)
                        if fut and not fut.done():
                            fut.set_result(m.get("result"))
                    elif m.get("type") == 1 and m.get("target") == "room":
                        rv = m["arguments"][0]
                        if rv.get("room", {}).get("id") == self.room:
                            self.view = rv.get("view")
                            self.seat = rv.get("seat")
                            self.status = rv.get("room", {}).get("status")
        except websockets.ConnectionClosed:
            pass

    async def run(self):
        url = f"ws://127.0.0.1:{self.port}/hub?nick={self.nick}"
        self.ws = await websockets.connect(url, max_size=None, compression=None, ping_interval=None, open_timeout=30)
        await self.ws.send(b'{"protocol":"json","version":1}' + RS, text=True)
        asyncio.create_task(self.reader())
        r = await self.invoke("JoinRoom", [self.room])
        self.log.append(("join", r))
        await self.invoke("WatchRoom", [self.room], wait=False)
        # чекаємо заїзду (а з rounds > 1 — і наступних, після «Ще раз»)
        for _ in range(self.rounds):
            for _ in range(3000):
                v = self.view or {}
                if v.get("phase") == "go" and v.get("goAt") not in self.seen:
                    break
                await asyncio.sleep(0.1)
            else:
                return
            self.seen.add(self.view.get("goAt"))
            await self.race()
            if self.leave_after:
                await asyncio.sleep(1.0)
                self.log.append(("leave", await self.invoke("LeaveRoom", [self.room])))
                return
            if self.leave_at is not None:
                return

    async def race(self):
        v = self.view
        text, length = v["text"], v["len"]
        k, d = [], []
        c, red = 0, False
        mean = 60000.0 / self.cpm
        last_pos = 0.0
        t_last = time.time()
        first = True
        while c < length:
            if self.leave_at is not None and c >= self.leave_at * length:
                self.log.append(("leave", await self.invoke("LeaveRoom", [self.room])))
                return
            if (self.view or {}).get("phase") != "go":
                return
            delay = mean if self.robot else mean * (0.45 + 1.1 * self.rng.random())
            if first:
                delay = 700 + self.rng.random() * 400
                first = False
            await asyncio.sleep(delay / 1000)
            now = time.time()
            ms = (now - t_last) * 1000
            t_last = now
            if not self.robot and not red and self.rng.random() < self.errors:
                red = True
                k.append("x"); d.append(enc(ms))
            elif red:
                red = False
                k.append("b"); d.append(enc(ms))
            else:
                c += 1
                k.append("c"); d.append(enc(ms))
            if now - last_pos >= 0.2:
                last_pos = now
                await self.invoke("Input", [self.room, "pos", {"c": c, "e": 1 if red else 0}], wait=False)
        self.result = await self.invoke("Act", [self.room, "finish", {"k": "".join(k), "d": "".join(d)}])
        self.log.append(("finish", self.result))


def run_bots(port, room, nicks, cpm=None, robot=None, leave_at=None, seeds=None, rounds=1, tag=None, leave_after=None):
    """Запустити ботів у фоновому потоці; повертає (потік, список ботів)."""
    bots = []
    tag = tag if tag is not None else str(random.Random().randint(10, 99))   # свіжий нік на кожен прогін: старий ще 20 с «сидить» за минулим столом
    nicks = [n + tag for n in nicks]
    for i, n in enumerate(nicks):
        bots.append(Bot(port, room, n, cpm=(cpm or [240] * len(nicks))[i], robot=(robot or [False] * len(nicks))[i],
                        leave_at=(leave_at or [None] * len(nicks))[i], seed=(seeds or list(range(1, 50)))[i], rounds=rounds,
                        leave_after=(leave_after or [False] * len(nicks))[i]))

    def main():
        async def all_():
            await asyncio.gather(*(b.run() for b in bots), return_exceptions=True)
            await asyncio.sleep(1.5)
            for b in bots:
                try:
                    await b.ws.close()
                except Exception:  # noqa: BLE001
                    pass
        asyncio.run(all_())

    th = threading.Thread(target=main, daemon=True)
    th.start()
    return th, bots


def create_room(port, nick, opts):
    """Бот-господар: створює стіл, повертає (room_id, бот). Бот далі сам сидить і їде, як решта."""
    box = {}

    async def go():
        b = Bot(port, None, nick)
        b.ws = await websockets.connect(f"ws://127.0.0.1:{port}/hub?nick={nick}", max_size=None, compression=None,
                                        ping_interval=None, open_timeout=30)
        await b.ws.send(b'{"protocol":"json","version":1}' + RS, text=True)
        asyncio.create_task(b.reader())
        r = await b.invoke("CreateRoom", ["typerace", opts])
        box["room"] = r.get("roomId")
        await b.ws.close()

    asyncio.run(go())
    return box["room"]


def start_room(port, nick, room):
    async def go():
        b = Bot(port, room, nick)
        b.ws = await websockets.connect(f"ws://127.0.0.1:{port}/hub?nick={nick}", max_size=None, compression=None,
                                        ping_interval=None, open_timeout=30)
        await b.ws.send(b'{"protocol":"json","version":1}' + RS, text=True)
        asyncio.create_task(b.reader())
        r = await b.invoke("StartRoom", [room])
        await b.ws.close()
        return r
    return asyncio.run(go())
