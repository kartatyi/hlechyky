"""Боти «Зіпсованого телефону» і «Позивних» для живої перевірки: SignalR (JSON поверх WebSocket, як bluff-bots.py).

    C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe docs/games/dev/slova-bots.py --port 8326 --game telephone \
        --bots "Ганна,Іван" [--room <id> | --create '{"show":"auto"}'] [--start] [--seconds 300] [--speed 1]

--room — сісти до наявного столу (його господар — людина в браузері, він і тисне «Почати»); --create — перший бот створює
стіл із цими опціями, решта сідають, а з --start перший бот і починає. Друкує id столу одразу (щоб підключити браузер).

Телефон: фразу пише з ideas (або «бот <нік> крок N»), малює кілька штрихів (Input draw) і здає Act done {n}; на показі
ставить ❤ кожному третьому чужому запису; «Далі» не тисне (кіно гортає саме), якщо не --next. Позивні: у фазі setup
тисне go, капітан дає підказку «бот N», польові тикають у перше закрите слово. Наприкінці — JSON-підсумок.
Звуку не вмикає нічого: це лише дріт.
"""
import argparse, asyncio, json, random, time

import websockets

RS = "\x1e"


class Bot:
    def __init__(self, a, nick, seed):
        self.a, self.nick = a, nick
        self.rng = random.Random(seed)
        self.inv = 0
        self.pending = {}
        self.stats = {"nick": nick, "views": 0, "maxView": 0, "acts": {}, "errors": [], "result": None, "seat": None,
                      "phases": [], "says": 0, "autoMs": []}
        self.done = set()
        self.ws = None
        self.last = None

    async def connect(self):
        self.ws = await websockets.connect(f"ws://127.0.0.1:{self.a.port}/hub?nick={self.nick}", max_size=None,
                                           compression=None, ping_interval=None, open_timeout=30)
        await self.ws.send('{"protocol":"json","version":1}' + RS)
        asyncio.create_task(self.reader())
        asyncio.create_task(self.pinger())
        await asyncio.sleep(0.3)

    async def join(self):
        r = await self.invoke("JoinRoom", [self.a.room], wait=True)
        if not r or not r.get("ok"):
            self.stats["errors"].append(f"JoinRoom: {r}")
        await self.invoke("WatchRoom", [self.a.room])

    async def pinger(self):
        while True:
            await asyncio.sleep(10)
            try:
                await self.ws.send('{"type":6}' + RS)
            except Exception:
                return

    async def invoke(self, target, args, wait=False):
        self.inv += 1
        iid = str(self.inv)
        fut = asyncio.get_running_loop().create_future() if wait else None
        self.pending[iid] = (target, fut)
        msg = {"type": 1, "target": target, "arguments": args}
        if wait:
            msg["invocationId"] = iid
        await self.ws.send(json.dumps(msg, ensure_ascii=False) + RS)
        if wait:
            try:
                return await asyncio.wait_for(fut, 20)
            except asyncio.TimeoutError:
                return None

    async def act(self, action, payload, tag=None):
        tag = tag or action
        r = await self.invoke("Act", [self.a.room, action, payload], wait=True)
        ok = bool(r and r.get("ok"))
        key = f"{tag}:{'ok' if ok else 'fail'}"
        self.stats["acts"][key] = self.stats["acts"].get(key, 0) + 1
        if not ok:
            self.stats["errors"].append(f"{tag}: {r and r.get('message')}")
        return r

    async def reader(self):
        try:
            async for data in self.ws:
                for rec in data.split(RS):
                    if not rec:
                        continue
                    m = json.loads(rec)
                    if m.get("type") == 3:
                        _, fut = self.pending.pop(m.get("invocationId"), (None, None))
                        if fut is not None and not fut.done():
                            fut.set_result(m.get("result"))
                    elif m.get("type") == 1 and m.get("target") == "room":
                        asyncio.create_task(self.on_room(m["arguments"][0], len(rec.encode())))
        except Exception as e:
            self.stats["errors"].append(f"reader: {e!r}")

    async def on_room(self, rv, size):
        room = rv.get("room") or {}
        if room.get("id") != self.a.room:
            return
        v = rv.get("view") or {}
        seat = rv.get("seat")
        self.stats["views"] += 1
        self.stats["maxView"] = max(self.stats["maxView"], size)
        self.stats["seat"] = seat
        phase = v.get("phase")
        if phase != self.last:
            self.last = phase
            self.stats["phases"].append(phase)
        if room.get("status") == "finished" and v.get("result") is not None:
            self.stats["result"] = v["result"]
        if seat is None or room.get("status") != "playing":
            return
        if self.a.game == "telephone":
            await self.telephone(v, seat)
        else:
            await self.pozyvni(v, seat)

    async def telephone(self, v, seat):
        sp = self.a.speed
        if v.get("phase") == "step":
            t = v.get("task")
            key = f"s{v.get('step')}"
            if not t or t.get("ready") or key in self.done:
                return
            self.done.add(key)
            await asyncio.sleep(self.rng.uniform(0.5, 2.5) / sp)
            if t["kind"] == "draw":
                n = self.rng.randint(3, 9)
                for k in range(n):
                    x0, y0 = self.rng.randint(80, 900), self.rng.randint(80, 650)
                    pts = [x0, y0]
                    for _ in range(self.rng.randint(4, 20)):
                        x0 = max(0, min(999, x0 + self.rng.randint(-60, 60)))
                        y0 = max(0, min(749, y0 + self.rng.randint(-60, 60)))
                        pts += [x0, y0]
                    await self.invoke("Input", [self.a.room, "draw", {"s": k + 1, "c": self.rng.randint(1, 19), "w": 8, "p": pts}])
                await asyncio.sleep(0.3)
                await self.act("done", {"n": n}, "draw")
            else:
                ideas = t.get("ideas") or []
                text = self.rng.choice(ideas) if ideas and t["kind"] == "phrase" else f"бот {self.nick} бачить щось {v.get('step')}"
                await self.act("done", {"text": text}, t["kind"])
        elif v.get("phase") == "reveal":
            r = v.get("reveal") or {}
            key = f"r{r.get('chain')}:{r.get('shown')}"
            if key in self.done:
                return
            self.done.add(key)
            if r.get("say"):
                self.stats["says"] += 1
            self.stats["autoMs"].append(r.get("autoMs"))
            e = (r.get("entries") or [])[-1:] or [None]
            e = e[0]
            if e and e.get("seat") not in (seat, -1) and self.rng.random() < 0.34:
                await asyncio.sleep(self.rng.uniform(0.3, 1.2))
                await self.act("like", {"chain": r["chain"], "index": e["index"]}, "like")
            if self.a.next and not r.get("auto"):
                await asyncio.sleep(self.rng.uniform(1.5, 3.0) / sp)
                await self.act("next", {}, "next")

    async def pozyvni(self, v, seat):
        ph = v.get("phase")
        if ph == "setup" and "go" not in self.done:
            self.done.add("go")
            await asyncio.sleep(1.0)
            await self.act("go", {}, "go")
            return
        teams = v.get("teams") or {}
        side = v.get("side")
        boss = (teams.get(side) or {}).get("boss")
        mine = side and seat in ((teams.get(side) or {}).get("seats") or [])
        if not mine:
            return
        if ph == "clue" and boss == seat:
            key = f"c{len(v.get('log') or [])}"
            if key in self.done:
                return
            self.done.add(key)
            await asyncio.sleep(self.rng.uniform(1, 3) / self.a.speed)
            words = ["шум", "тінь", "вітер", "поле", "казка", "сонце", "дорога"]
            await self.act("clue", {"word": self.rng.choice(words), "count": self.rng.randint(1, 2)}, "clue")
        elif ph == "guess" and boss != seat:
            key = f"g{len(v.get('log') or [])}:{(v.get('clue') or {}).get('left')}"
            if key in self.done:
                return
            self.done.add(key)
            await asyncio.sleep(self.rng.uniform(0.8, 2) / self.a.speed)
            closed = [i for i, c in enumerate(v.get("board") or []) if not c.get("open")]
            if closed:
                await self.act("pick", {"i": self.rng.choice(closed)}, "pick")


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8326)
    ap.add_argument("--game", default="telephone", choices=["telephone", "pozyvni"])
    ap.add_argument("--room")
    ap.add_argument("--create")
    ap.add_argument("--start", action="store_true")
    ap.add_argument("--next", action="store_true")
    ap.add_argument("--bots", default="Ганна,Іван")
    ap.add_argument("--seconds", type=int, default=300)
    ap.add_argument("--speed", type=float, default=1.0)
    a = ap.parse_args()
    bots = [Bot(a, n, 100 + i) for i, n in enumerate(x for x in a.bots.split(",") if x)]
    for b in bots:
        await b.connect()
    if not a.room:
        opts = json.loads(a.create or "{}")
        r = await bots[0].invoke("CreateRoom", [a.game, opts], wait=True)
        a.room = (r or {}).get("roomId")
        print(json.dumps({"room": a.room, "reply": r}, ensure_ascii=False), flush=True)
        await bots[0].invoke("WatchRoom", [a.room])
        rest = bots[1:]
    else:
        rest = bots
    for b in rest:
        await b.join()
    if a.start:
        await asyncio.sleep(0.5)
        r = await bots[0].invoke("StartRoom", [a.room], wait=True)
        print(json.dumps({"start": r}, ensure_ascii=False), flush=True)
    t0 = time.time()
    while time.time() - t0 < a.seconds:
        await asyncio.sleep(1)
        if all(b.stats["result"] for b in bots):
            break
    for b in bots:
        try:
            await b.ws.close()
        except Exception:
            pass
    for b in bots:
        b.stats["autoMs"] = b.stats["autoMs"][:12]
    print(json.dumps([b.stats for b in bots], ensure_ascii=False, indent=1))


if __name__ == "__main__":
    asyncio.run(main())
