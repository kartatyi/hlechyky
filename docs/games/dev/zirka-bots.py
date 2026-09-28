"""Боти для китайських шашок (zirka): жадібно тягнуть фішки до цільового кута, інколи випадково.

    python zirka-bots.py --port 8324 --nicks Бот1,Бот2 --create [--wait-join 20] [--start] [--seconds 300]
    python zirka-bots.py --port 8324 --nicks Бот3 --room <id>

--create: перший бот ставить стіл і друкує його id; --wait-join N — стільки секунд чекати, поки підсяде ще хтось
(людина з браузера); --start — перший бот тисне «Почати». Грають, поки стіл не скінчиться або не мине --seconds.
"""
import argparse, asyncio, json, random, sys

import websockets

RS = "\x1e"


def cells():
    out = []
    for r in range(-8, 9):
        for q in range(-8, 9):
            x, z, y = q, r, -q - r
            if (x <= 4 and y <= 4 and z <= 4) or (x >= -4 and y >= -4 and z >= -4):
                out.append((q, r))
    return out


CELLS = cells()


def coord(i, axis):
    q, r = CELLS[i]
    return q if axis == 0 else (-q - r if axis == 1 else r)


class Bot:
    def __init__(self, nick, port, room, rng):
        self.nick, self.port, self.room, self.rng = nick, port, room, rng
        self.view, self.seat, self.status, self.inv, self.pending, self.moves = None, None, None, 0, {}, 0

    async def connect(self):
        self.ws = await websockets.connect(f"ws://127.0.0.1:{self.port}/hub?nick={self.nick}", max_size=None, ping_interval=None)
        await self.ws.send('{"protocol":"json","version":1}' + RS)
        asyncio.create_task(self.reader())

    async def invoke(self, target, args):
        self.inv += 1
        iid = str(self.inv)
        fut = asyncio.get_running_loop().create_future()
        self.pending[iid] = fut
        await self.ws.send(json.dumps({"type": 1, "invocationId": iid, "target": target, "arguments": args}, ensure_ascii=False) + RS)
        return await asyncio.wait_for(fut, 20)

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
                        if rv.get("room", {}).get("id") == self.room:
                            self.status, self.seat, self.view = rv["room"]["status"], rv.get("seat"), rv.get("view")
        except websockets.ConnectionClosed:
            pass

    def pick(self):
        v = self.view
        home = next(h for h in v["homes"] if h["seat"] == self.seat)
        axis, sign = home["axis"], home["sign"]
        best, score = None, -1e9
        for frm, tos in v["moves"].items():
            for to in tos:
                s = (coord(int(frm), axis) - coord(to, axis)) * sign + self.rng.random() * 0.6
                if s > score:
                    best, score = (int(frm), to), s
        return best

    async def play(self):
        if self.status != "playing" or not self.view or self.view.get("turn") != self.seat:
            return
        mv = self.pick()
        if mv:
            r = await self.invoke("Act", [self.room, "move", {"from": mv[0], "to": mv[1]}])
            if r and r.get("ok"):
                self.moves += 1
            else:
                print(self.nick, "ВІДМОВА", r, flush=True)


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8324)
    ap.add_argument("--room")
    ap.add_argument("--create", action="store_true")
    ap.add_argument("--start", action="store_true")
    ap.add_argument("--wait-join", type=int, default=0)
    ap.add_argument("--nicks", required=True)
    ap.add_argument("--seconds", type=int, default=300)
    ap.add_argument("--delay", type=float, default=0.15)
    a = ap.parse_args()
    rng = random.Random(1)
    bots, room = [], a.room
    for i, n in enumerate(a.nicks.split(",")):
        b = Bot(n, a.port, room, random.Random(rng.random()))
        await b.connect()
        await asyncio.sleep(0.2)
        if a.create and i == 0 and not room:
            r = await b.invoke("CreateRoom", ["zirka", {}])
            room = r["roomId"]
            b.room = room
            print("ROOM", room, flush=True)
        else:
            b.room = room
            print("join", n, await b.invoke("JoinRoom", [room]), flush=True)
        await b.invoke("WatchRoom", [room])   # без підписки види столу не приходять
        bots.append(b)
    if a.wait_join:
        await asyncio.sleep(a.wait_join)
    if a.start:
        print("start", await bots[0].invoke("StartRoom", [room]), flush=True)
    loop = asyncio.get_running_loop()
    end = loop.time() + a.seconds
    while loop.time() < end:
        await asyncio.sleep(a.delay)
        for b in bots:
            await b.play()
        if all(b.status == "finished" for b in bots):
            break
    v = bots[0].view or {}
    print("STATUS", bots[0].status, "moves", sum(b.moves for b in bots), "result", v.get("result"), "home", v.get("home"), flush=True)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    asyncio.run(main())
