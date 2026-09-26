"""Боти для живої перевірки «Шпигуна»: легкі SignalR-клієнти (JSON поверх WebSocket, без negotiate), що сідають за
стіл і грають, як ліниві люди: питають по черзі (з реплікою в балачці столу), відповідають, голосують, показують
у фіналі, тиснуть «Готово», а шпигун-бот за якийсь час пробує назвати локацію.

    python spy-bots.py --port 8229 --nicks Бот1,Бот2,Бот3 [--room <id>] [--guess-after 45] [--guess-right 0.3]
                       [--yes 0.5] [--accuse-after 0] [--leave Бот2@play:20] [--seconds 900] [--rematch]

Без --room бот чекає на першу кімнату «spy» у лобі й сідає туди. Швидкодія й квоти: кожен бот шле не частіше
кількох дій на хвилину, тож квоти каркаса (10 Act/с) не зачіпає. Пише в stdout рядок на кожну свою дію.
"""
import argparse, asyncio, json, random, sys, time

import websockets

RS = b"\x1e"

QUESTIONS = [
    "А тут часто буває людно?", "Що ти тут зазвичай тримаєш у руках?", "Тут можна нормально поїсти?",
    "Скільки ти тут уже?", "Тут шумно?", "Ти тут по роботі чи для душі?", "А взуття тут яке краще?",
    "Сюди з дітьми ходять?", "Тут пахне чимось особливим?", "Тобі тут подобається, чесно?",
]
ANSWERS = [
    "Буває по-різному 🙂", "Та як завжди", "Ну, залежить від дня", "Краще б я був удома",
    "Тут головне — не поспішати", "Ой, не питай", "Та нормально, жити можна", "Хто як, а я звик",
]


class Bot:
    def __init__(self, nick, a, rng):
        self.nick, self.a, self.rng = nick, a, rng
        self.ws = None
        self.inv = 0
        self.pending = {}
        self.room = a.room
        self.rv = None
        self.done = asyncio.Event()
        self.seen_key = None
        self.phase_at = time.time()
        self.plan = set()      # що вже заплановано в цій фазі
        self.left = False
        self.asking = False

    def log(self, *parts):
        print(f"[{time.strftime('%H:%M:%S')}] {self.nick}:", *parts, flush=True)

    async def start(self):
        url = f"ws://127.0.0.1:{self.a.port}/hub?nick={self.nick}"
        self.ws = await websockets.connect(url, max_size=None, compression=None, ping_interval=None, open_timeout=30)
        await self.ws.send(b'{"protocol":"json","version":1}' + RS, text=True)
        asyncio.create_task(self.reader())
        asyncio.create_task(self.pinger())
        while not self.room:
            await asyncio.sleep(0.3)
        r = await self.invoke("JoinRoom", [self.room])
        self.log("JoinRoom", r)
        await self.invoke("WatchRoom", [self.room], wait=False)

    async def invoke(self, target, args, wait=True):
        self.inv += 1
        iid = str(self.inv)
        fut = asyncio.get_running_loop().create_future() if wait else None
        if fut:
            self.pending[iid] = fut
        msg = {"type": 1, "target": target, "arguments": args}
        if wait:
            msg["invocationId"] = iid
        await self.ws.send(json.dumps(msg, ensure_ascii=False).encode() + RS, text=True)
        if wait:
            try:
                return await asyncio.wait_for(fut, 20)
            except asyncio.TimeoutError:
                return {"ok": False, "message": "timeout"}

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
                            fut.set_result(m.get("result") if "error" not in m else {"ok": False, "message": m["error"]})
                    elif m.get("type") == 1:
                        tgt, args = m.get("target"), m.get("arguments") or []
                        if tgt == "rooms" and not self.room:
                            for r in args[0] or []:
                                if r.get("game") == "spy" and r.get("status") == "lobby":
                                    self.room = r["id"]
                                    break
                        elif tgt == "room" and args and args[0].get("room", {}).get("id") == self.room:
                            self.rv = args[0]
                            self.on_view()
        except websockets.ConnectionClosed:
            self.done.set()

    async def pinger(self):
        while not self.done.is_set():
            await asyncio.sleep(10)
            try:
                await self.ws.send(b'{"type":6}' + RS, text=True)
            except websockets.ConnectionClosed:
                return

    # ---------- поведінка ----------

    def later(self, key, delay, coro_fn):
        if key in self.plan:
            return
        self.plan.add(key)

        async def run():
            await asyncio.sleep(delay)
            if self.left or not self.rv:
                return
            try:
                await coro_fn()
            except websockets.ConnectionClosed:
                pass
        asyncio.create_task(run())

    def on_view(self):
        rv = self.rv
        v = rv.get("view") or {}
        room = rv.get("room") or {}
        seat = rv.get("seat")
        phase = v.get("phase")
        key = f"{room.get('round')}:{v.get('round')}:{phase}"
        if key != self.seen_key:
            self.seen_key = key
            self.plan = set()
            self.phase_at = time.time()
        if room.get("status") == "finished":
            if self.a.rematch and "rematch" not in self.plan:
                self.later("rematch", 4 + self.rng.random() * 3, self.rematch)
            return
        me = v.get("me")
        if seat is None or not me:
            return
        present = [p["seat"] for p in v.get("players", []) if p.get("here")]
        if seat not in present:
            return
        rng = self.rng

        # вихід за сценарієм: --leave Нік@фаза:секунди
        for spec in self.a.leave:
            nick, _, rest = spec.partition("@")
            ph, _, sec = rest.partition(":")
            if nick == self.nick and ph == phase and f"leave" not in self.plan:
                self.later("leave", float(sec or 0), self.leave)

        if phase == "play":
            asker, by = v.get("asker"), v.get("askedBy")
            if asker == seat and not self.asking:
                targets = [s for s in present if s != seat and s != by]
                if targets:
                    self.asking = True

                    async def go():
                        await asyncio.sleep(2.5 + rng.random() * 4)
                        try:
                            if not self.left:
                                await self.ask(targets, by is not None)
                        finally:
                            self.asking = False
                    asyncio.create_task(go())
            if me.get("spy") and self.a.guess_after > 0:
                self.later("guess", self.a.guess_after + rng.random() * 10, self.guess)
            if self.a.accuse_after > 0 and not any(p["seat"] == seat and p.get("accused") for p in v["players"]):
                self.later("accuse", self.a.accuse_after + rng.random() * 8, self.accuse)
        elif phase == "vote":
            vt = v.get("vote") or {}
            if vt.get("suspect") != seat and str(seat) not in (vt.get("votes") or {}):
                yes = rng.random() < self.a.yes or (me.get("spy") and vt.get("suspect") != seat)
                self.later("vote", 1 + rng.random() * 4, lambda: self.act("vote", {"yes": bool(yes)}))
        elif phase == "final":
            bl = (v.get("blame") or {}).get("votes") or {}
            if str(seat) not in bl:
                others = [s for s in present if s != seat]
                self.later("blame", 2 + rng.random() * 6, lambda: self.act("blame", {"seat": rng.choice(others)}))
        elif phase == "reveal":
            mine = next((p for p in v["players"] if p["seat"] == seat), {})
            if not mine.get("ready"):
                self.later("ready", 1.5 + rng.random() * 3, lambda: self.act("ready", {}))

    async def act(self, action, payload):
        r = await self.invoke("Act", [self.room, action, payload])
        self.log(action, json.dumps(payload, ensure_ascii=False), "→", r)
        return r

    async def say(self, text):
        await self.invoke("TableSay", [self.room, text])

    async def ask(self, targets, answer_first):
        if answer_first:
            await self.say(self.rng.choice(ANSWERS))
            await asyncio.sleep(1 + self.rng.random() * 2)
        v = (self.rv or {}).get("view") or {}
        if v.get("phase") != "play":
            return
        t = self.rng.choice(targets)
        nick = next((p["nick"] for p in v.get("players", []) if p["seat"] == t), "?")
        if v.get("asker") != self.rv.get("seat"):
            return
        q = self.rng.choice(QUESTIONS)
        await self.say(f"{nick}, {q[0].lower()}{q[1:]}")
        await self.act("ask", {"seat": t})

    async def guess(self):
        v = (self.rv or {}).get("view") or {}
        if v.get("phase") not in ("play", "final") or not (v.get("me") or {}).get("spy"):
            return
        deck = [d[0] for d in v.get("deck", [])]
        # Шпигун-бот локації не знає; «влучити» він може лише навмання, як і людина
        await self.act("guess", {"loc": self.rng.choice(deck)})

    async def accuse(self):
        v = (self.rv or {}).get("view") or {}
        if v.get("phase") != "play":
            return
        seat = self.rv.get("seat")
        others = [p["seat"] for p in v["players"] if p.get("here") and p["seat"] != seat]
        await self.act("accuse", {"seat": self.rng.choice(others)})

    async def leave(self):
        self.left = True
        r = await self.invoke("LeaveRoom", [self.room])
        self.log("LeaveRoom", r)

    async def rematch(self):
        r = await self.invoke("Rematch", [self.room])
        self.log("Rematch", r)


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8229)
    ap.add_argument("--room")
    ap.add_argument("--nicks", required=True)
    ap.add_argument("--guess-after", type=float, default=0, help="секунд від початку раунду, коли шпигун-бот вгадує (0 — ніколи)")
    ap.add_argument("--accuse-after", type=float, default=0, help="секунд, коли бот висуває підозру (0 — ніколи)")
    ap.add_argument("--yes", type=float, default=0.5, help="ймовірність «так» у голосуванні")
    ap.add_argument("--leave", action="append", default=[], help="Нік@фаза:секунди — встати з-за столу")
    ap.add_argument("--seconds", type=float, default=900)
    ap.add_argument("--rematch", action="store_true")
    ap.add_argument("--seed", type=int, default=1)
    a = ap.parse_args()
    bots = [Bot(n, a, random.Random(a.seed * 1000 + i)) for i, n in enumerate(a.nicks.split(","))]
    for b in bots:
        await b.start()
        await asyncio.sleep(0.3)
    try:
        await asyncio.wait_for(asyncio.gather(*(b.done.wait() for b in bots)), a.seconds)
    except asyncio.TimeoutError:
        pass
    for b in bots:
        try:
            await b.ws.close()
        except Exception:  # noqa: BLE001
            pass


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    asyncio.run(main())
