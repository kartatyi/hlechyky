"""Боти «Вгадай мелодію» для живої перевірки: кожен — окреме SignalR-з'єднання (JSON поверх WebSocket).

    C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe docs/games/dev/melody-bots.py --port 8262 --room <id> \
        --nicks Петро,Ганна [--db data/hlechyky.db] [--know 0.6] [--delay 2-9] [--skip 0.3] [--leave Ганна@3] [--rematch] [--secs 900]

(Python — лише повним шляхом: голий `python`/`py` на цій машині смикає «оновлення» Install Manager, і AppXSvc тече пам'яттю.)

Сідають за стіл (JoinRoom), дивляться його (WatchRoom). Уривків боти не слухають — «вгадують» перебором: з бази
worktree (--db) беруть усі треки з файлом у кеші й на кожен раунд з імовірністю --know пробують їх по одному
(«виконавець назва» раз на 750 мс, щоб не впертись у GuessEveryMs), поки не вгадають обидва. Хто «не знає» — через
--delay секунд тисне «Пропустити» з імовірністю --skip, інакше просто мовчить до кінця часу.
--leave Нік@N — цей бот встає з-за столу на треку N. --rematch — коли партію зіграно, перший бот тисне «Ще раз».
Друкує треки, хто що вгадав і підсумок; завершується, коли партію зіграно (або --secs).
Нік на сервері стане «гість <нік>» — так сайт кличе гостей.
"""
import argparse, asyncio, json, random, sqlite3, sys, time

import websockets

sys.stdout.reconfigure(encoding="utf-8")
RS = "\x1e"


def candidates(db):
    try:
        c = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
        return [f"{a} {t}" for a, t in c.execute("SELECT artist, title FROM tracks WHERE file_path IS NOT NULL")]
    except Exception as e:
        print("база не читається:", e, flush=True)
        return []


class Bot:
    def __init__(self, nick, a, rng, shared, songs):
        self.nick, self.a, self.rng, self.shared, self.songs = nick, a, rng, shared, songs
        self.inv = 0
        self.pending = {}
        self.view = None
        self.seat = None
        self.status = None
        self.round = 0
        self.gone = False
        self.done = asyncio.Event()

    async def start(self):
        self.ws = await websockets.connect(f"ws://127.0.0.1:{self.a.port}/hub?nick={self.nick}", max_size=None,
                                           ping_interval=None, open_timeout=30)
        await self.ws.send('{"protocol":"json","version":1}' + RS)
        asyncio.create_task(self.reader())
        asyncio.create_task(self.pinger())
        r = await self.call("JoinRoom", [self.a.room])
        print(f"[{self.nick}] JoinRoom: {(r or {}).get('message')}", flush=True)
        await self.send("WatchRoom", [self.a.room])

    async def pinger(self):
        while True:
            await asyncio.sleep(10)
            try:
                await self.ws.send('{"type":6}' + RS)
            except Exception:
                return

    async def send(self, target, args):
        await self.ws.send(json.dumps({"type": 1, "target": target, "arguments": args}, ensure_ascii=False) + RS)

    async def call(self, target, args):
        self.inv += 1
        iid = str(self.inv)
        fut = asyncio.get_running_loop().create_future()
        self.pending[iid] = fut
        await self.ws.send(json.dumps({"type": 1, "invocationId": iid, "target": target, "arguments": args}, ensure_ascii=False) + RS)
        try:
            return await asyncio.wait_for(fut, 20)
        except asyncio.TimeoutError:
            return None

    async def act(self, action, payload=None):
        return await self.call("Act", [self.a.room, action, payload if payload is not None else {}])

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
                            fut.set_result(m.get("result"))
                    elif m.get("type") == 1 and m.get("target") == "room":
                        rv = m["arguments"][0]
                        if rv["room"]["id"] != self.a.room:
                            continue
                        self.view = rv.get("view") or {}
                        self.seat = rv.get("seat")
                        self.status = rv["room"]["status"]
                        self.react()
        except Exception as e:
            if not self.gone:
                print(f"[{self.nick}] зв'язок: {e}", flush=True)

    def react(self):
        v = self.view or {}
        if self.status == "finished" or v.get("phase") == "done":
            if not self.shared.get("printed"):
                self.shared["printed"] = True
                print(f"== партію зіграно: {v.get('result')} error={v.get('error')} треків={len(v.get('played') or [])}", flush=True)
            if self.a.rematch and not self.shared.get("rematch") and self.nick == self.shared["first"]:
                self.shared["rematch"] = True
                asyncio.create_task(self.rematch())
            elif not self.a.rematch:
                self.done.set()
            return
        if v.get("phase") == "play" and v.get("round") != self.round:
            self.round = v.get("round")
            if self.nick == self.shared["first"]:
                print(f"-- трек {self.round} з {v.get('rounds')}", flush=True)
            if self.a.leave and self.a.leave[0] == self.nick and self.round == self.a.leave[1]:
                asyncio.create_task(self.leave())
                return
            asyncio.create_task(self.play_round(self.round))
        if v.get("phase") == "reveal" and self.nick == self.shared["first"] and self.shared.get("shown") != v.get("round"):
            self.shared["shown"] = v.get("round")
            ans = v.get("answer") or {}
            print(f"   відповідь: {ans.get('artist')} — {ans.get('title')}; очки {v.get('scores')}", flush=True)

    async def play_round(self, rnd):
        lo, hi = self.a.delay
        await asyncio.sleep(self.rng.uniform(lo, hi))
        if self.gone or self.round != rnd:
            return
        if self.rng.random() >= self.a.know:
            if self.rng.random() < self.a.skip:
                await self.act("skip")
            return
        songs = self.songs[:]
        self.rng.shuffle(songs)
        for s in songs:
            v = self.view or {}
            if self.gone or v.get("phase") != "play" or v.get("round") != rnd:
                return
            me = v.get("me") or {}
            if me.get("artist") and me.get("title"):
                return
            r = await self.act("guess", {"text": s})
            if r and r.get("ok"):
                print(f"   [{self.nick}] {s}: {r.get('message')}", flush=True)
            await asyncio.sleep(0.75)

    async def leave(self):
        self.gone = True
        r = await self.call("LeaveRoom", [self.a.room])
        print(f"[{self.nick}] встав з-за столу: {(r or {}).get('message')}", flush=True)

    async def rematch(self):
        await asyncio.sleep(3)
        r = await self.call("Rematch", [self.a.room])
        print(f"[{self.nick}] Ще раз: {(r or {}).get('message')}", flush=True)
        self.shared["printed"] = False


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--room", required=True)
    ap.add_argument("--nicks", required=True)
    ap.add_argument("--db", default="data/hlechyky.db")
    ap.add_argument("--know", type=float, default=0.6)
    ap.add_argument("--delay", default="2-9")
    ap.add_argument("--skip", type=float, default=0.3)
    ap.add_argument("--leave")
    ap.add_argument("--rematch", action="store_true")
    ap.add_argument("--secs", type=int, default=900)
    ap.add_argument("--seed", type=int, default=7)
    a = ap.parse_args()
    lo, hi = a.delay.split("-")
    a.delay = (float(lo), float(hi))
    if a.leave:
        n, r = a.leave.split("@")
        a.leave = (n, int(r))
    songs = candidates(a.db)
    print(f"кандидатів: {len(songs)}", flush=True)
    nicks = a.nicks.split(",")
    shared = {"first": nicks[0]}
    bots = [Bot(n, a, random.Random(a.seed + i), shared, songs) for i, n in enumerate(nicks)]
    for b in bots:
        await b.start()
    t0 = time.time()
    while time.time() - t0 < a.secs and not any(b.done.is_set() for b in bots):
        await asyncio.sleep(0.5)


if __name__ == "__main__":
    asyncio.run(main())
