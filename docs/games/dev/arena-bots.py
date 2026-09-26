"""Боти для живої перевірки Крижини й Аерохокею (пакет arena): легкі SignalR-клієнти замість зайвих Chrome.

Кожен бот — окреме з'єднання (JSON-протокол поверх WebSocket, як web/static/signalr.min.js), нік <prefix>1…N.
Сідає за вказаний стіл (JoinRoom), дивиться кадри (WatchRoom) і грає простими правилами:
  icefloe — тримає сектор до центру криги, ривкає в найближчого, коли той ближче за 260 см; з берега цілить
            у найближчого живого й кидає сніжку раз на 2.2 с;
  hockey  — веде біту до шайби, коли та на своїй половині, інакше повертається до своїх воріт; 20 to/с.

    python arena-bots.py --port 8223 --room <id> --game icefloe --n 6 [--prefix бот] [--secs 120]
                         [--rematch] [--leave 1:40]   # бот №1 устає на 40-й секунді
                         [--calm]                     # тримаються біля центру й не штовхаються (танення, камера)

Друкує раз на 5 с: скільки кадрів прийшло, середній і найбільший розмір кадру в байтах (усе повідомлення SignalR).
"""
import argparse, asyncio, json, math, random, sys, time

import websockets

RS = b"\x1e"


class Bot:
    def __init__(self, idx, a, stats):
        self.idx, self.a, self.stats = idx, a, stats
        self.nick = f"{a.prefix}{idx}"
        self.inv = 0
        self.seat = None
        self.view = None
        self.frame = None
        self.status = None
        self.rng = random.Random(idx * 7919)
        self.sent = None
        self.sent_at = 0.0
        self.last_dash = 0
        self.last_throw = 0
        self.left = False

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

    async def aim(self, a):
        """Намір Крижини: шлемо зміну, а той самий напрямок — раз на 0.4 с, як браузер (сервер без підтвердження
        гасить тягу за 1.2 с — Icefloe.KeepTicks)."""
        now = time.time()
        if a != self.sent or (a >= 0 and now - self.sent_at >= 0.4):
            self.sent = a
            self.sent_at = now
            await self.input("move", {"a": a})

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
                        await self.think()
                    elif tgt == "room" and args and args[0].get("room", {}).get("id") == self.a.room:
                        rv = args[0]
                        self.seat = rv.get("seat")
                        self.view = rv.get("view")
                        was, self.status = self.status, rv["room"]["status"]
                        if self.view and self.view.get("frame") and self.status != "playing":
                            self.frame = self.view["frame"]
                        if was == "playing" and self.status == "finished":
                            self.stats["finished"] += 1
                            res = rv["room"].get("result") or {}
                            print(f"[{self.nick}] партію зіграно: {res.get('text')}", flush=True)
                            if self.a.rematch and self.idx == 1 and not self.left:
                                await asyncio.sleep(3)
                                await self.send("Rematch", [self.a.room], reply=True)
        except websockets.ConnectionClosed:
            pass

    async def pinger(self):
        while True:
            await asyncio.sleep(10)
            try:
                await self.ws.send(b'{"type":6}' + RS, text=True)
            except websockets.ConnectionClosed:
                return

    # ---------- правила ----------
    async def think(self):
        if self.seat is None or self.status != "playing" or self.left:
            return
        if self.a.game == "icefloe":
            await self.icefloe()
        else:
            await self.hockey()

    async def icefloe(self):
        f = self.frame
        if self.a.calm:
            # тихо тримаються біля центру (не далі 250 см), нікого не штовхають — щоб дожити до танення
            p = f.get("p") or []
            me = p[self.seat] if self.seat < len(p) else None
            if not me or not (me[5] & 1):
                return
            C = (self.view or {}).get("pond", 2600) / 2
            dx, dy = C - me[0], C - me[1]
            a = round(math.atan2(dy, dx) / (math.pi / 8)) % 16 if math.hypot(dx, dy) > 250 else -1
            await self.aim(a)
            return
        p = f.get("p") or []
        me = p[self.seat] if self.seat < len(p) else None
        if not me or f.get("ph") not in (0, 1):
            return
        now = time.time()
        C = (self.view or {}).get("pond", 2600) / 2
        alive = bool(me[5] & 1)
        # найближчий живий суперник
        best, bd = None, 1e9
        for i, q in enumerate(p):
            if q and i != self.seat and (q[5] & 1):
                d = math.hypot(q[0] - me[0], q[1] - me[1])
                if d < bd:
                    best, bd = q, d
        if alive:
            # до центру з легким «вітром», щоб не стояли стовпом; поруч суперник — на нього
            tx, ty = C + self.rng.uniform(-150, 150), C + self.rng.uniform(-150, 150)
            if best is not None and bd < 420:
                tx, ty = best[0], best[1]
            dx, dy = tx - me[0], ty - me[1]
            a = -1 if math.hypot(dx, dy) < 60 else round(math.atan2(dy, dx) / (math.pi / 8)) % 16
            await self.aim(a)
            if f.get("ph") == 1 and best is not None and bd < 260 and me[6] == 0 and now - self.last_dash > 1.1 \
                    and self.rng.random() < 0.5:
                self.last_dash = now
                await self.input("dash")
        else:
            if best is None:
                return
            a = round(math.atan2(best[1] - me[1], best[0] - me[0]) / (math.pi / 8)) % 16
            await self.aim(a)
            if me[7] > 0 and me[6] == 0 and now - self.last_throw > 2.2:
                self.last_throw = now
                await self.input("throw")

    async def hockey(self):
        f = self.frame
        v = self.view or {}
        teams = v.get("teams") or [[0], [1]]
        team = 0 if self.seat in teams[0] else 1
        pads = f.get("p") or []
        if self.seat * 2 + 1 >= len(pads) or pads[self.seat * 2] is None:
            return
        px, py = f["x"], f["y"]
        mine = px < 100 if team == 0 else px > 100
        goal_x = 0 if team == 0 else 200
        if mine:
            # б'ємо трохи з-за шайби, щоб відскок летів до чужих воріт
            tx = px + (-6 if team == 0 else 6)
            ty = py + self.rng.uniform(-3, 3)
        else:
            tx = goal_x + (22 if team == 0 else -22)
            ty = 60 + (py - 60) * 0.5
        now = time.time()
        if self.sent is None or now - self.sent >= 0.05:
            self.sent = now
            await self.input("to", {"x": round(tx, 2), "y": round(ty, 2)})


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8223)
    ap.add_argument("--room", required=True)
    ap.add_argument("--game", choices=["icefloe", "hockey"], required=True)
    ap.add_argument("--n", type=int, default=1)
    ap.add_argument("--prefix", default="бот")
    ap.add_argument("--secs", type=int, default=120)
    ap.add_argument("--rematch", action="store_true")
    ap.add_argument("--leave", help="номер_бота:секунда — встати посеред партії")
    ap.add_argument("--calm", action="store_true", help="Крижина: боти тихо тримаються біля центру")
    a = ap.parse_args()
    stats = {"frames": 0, "bytes": 0, "max": 0, "finished": 0}
    bots = [Bot(i + 1, a, stats) for i in range(a.n)]
    for b in bots:
        await b.start()
        await asyncio.sleep(0.15)
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
            print(f"[{b.nick}] устав з-за столу на {leave[1]:.0f}-й секунді", flush=True)
            leave = None
        if time.time() - last >= 5:
            last = time.time()
            n = max(1, stats["frames"])
            print(f"кадрів {stats['frames']}, середній {stats['bytes'] / n:.0f} Б, найбільший {stats['max']} Б, партій {stats['finished']}", flush=True)
    for b in bots:
        await b.ws.close()


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    asyncio.run(main())
