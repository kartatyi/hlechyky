"""Легкі боти «Байкарів» для живої перевірки: SignalR (JSON поверх WebSocket, як _tools/loadtest/load.py), кожен — окремий нік.

    C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe docs/games/dev/bluff-bots.py --port 8230 --room <id> --bots "Ганна:liar:пузаті хмарки,Іван:dice,Марта:liar:пузаті хмарки" \
        --secrets "пузаті хмарки,у хвості" --seconds 300 [--watch Глядач] [--leave "Іван@3:pick"]

Режими: liar:<брехня> — пише цю брехню; dice — «🎲 Хай Глек збреше»; idle — нічого не робить; усі обирають випадкову
не свою картку й ставлять ❤ першій відкритій чужій брехні. --watch — глядач без місця. --leave нік@q:фаза — встати.
Перевіряє на дроті: у фазі write жоден чужий вид не містить чужих брехень (--secrets), у pick — by/picks/truth/decoy
null у всіх видах. Наприкінці друкує JSON-підсумок. Потрібен пакет websockets; Python — лише повним шляхом (AGENT-COMMON).
"""
import argparse, asyncio, json, random, sys, time

import websockets

RS = "\x1e"


class Bot:
    def __init__(self, a, nick, mode, lie, seed, watch=False, leave=None):
        self.a, self.nick, self.mode, self.lie = a, nick, mode, lie
        self.watch = watch
        self.leave = leave            # (q, phase)
        self.rng = random.Random(seed)
        self.inv = 0
        self.pending = {}
        self.stats = {"nick": nick, "mode": mode, "views": 0, "maxView": 0, "leaks": [], "acts": {}, "errors": [], "result": None,
                      "seat": None, "phases": []}
        self.done_keys = set()
        self.ws = None
        self.last_phase = None
        self.finished = asyncio.Event()

    async def start(self):
        self.ws = await websockets.connect(f"ws://127.0.0.1:{self.a.port}/hub?nick={self.nick}", max_size=None,
                                           compression=None, ping_interval=None, open_timeout=30)
        await self.ws.send('{"protocol":"json","version":1}' + RS)
        asyncio.create_task(self.reader())
        await asyncio.sleep(0.3)
        if not self.watch:
            r = await self.invoke("JoinRoom", [self.a.room], wait=True)
            if not r or not r.get("ok"):
                self.stats["errors"].append(f"JoinRoom: {r}")
        await self.invoke("WatchRoom", [self.a.room])
        asyncio.create_task(self.pinger())

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
        await self.ws.send(json.dumps({"type": 1, "invocationId": iid, "target": target, "arguments": args}, ensure_ascii=False) + RS)
        if wait:
            try:
                return await asyncio.wait_for(fut, 20)
            except asyncio.TimeoutError:
                return None

    async def act(self, action, payload, tag):
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
                        target, fut = self.pending.pop(m.get("invocationId"), (None, None))
                        if fut is not None and not fut.done():
                            fut.set_result(m.get("result"))
                    elif m.get("type") == 1 and m.get("target") == "room":
                        # Окремою задачею: on_room чекає відповіді на свій Act, а її читає цей самий цикл.
                        asyncio.create_task(self.on_room(m["arguments"][0], len(rec.encode())))
        except Exception as e:
            self.stats["errors"].append(f"reader: {e!r}")
        finally:
            self.finished.set()

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
        q = v.get("q")
        if (q, phase) != self.last_phase:
            self.last_phase = (q, phase)
            self.stats["phases"].append(f"{q}:{phase}")
        text = json.dumps(v, ensure_ascii=False)
        my = v.get("my") or {}
        # --- приховане на дроті ---
        if phase == "write":
            for s in self.a.secrets:
                if s and s != (my.get("lie") or "") and s in text:
                    self.stats["leaks"].append(f"write q{q}: «{s}» у виді місця {seat}")
        if phase == "pick":
            for o in v.get("options") or []:
                for k in ("by", "picks", "truth", "decoy"):
                    if o.get(k) is not None:
                        self.stats["leaks"].append(f"pick q{q}: {k} у виді місця {seat}")
        if seat is None and my:
            self.stats["leaks"].append("глядач бачить my")
        if room.get("status") == "finished" and v.get("result") is not None:
            self.stats["result"] = {"winners": v["result"].get("winners"), "scores": v.get("scores"),
                                    "best": v["result"].get("best"), "recap": len(v["result"].get("recap") or [])}
        if self.watch or seat is None or room.get("status") != "playing":
            return
        if self.leave and q == self.leave[0] and phase == self.leave[1]:
            self.leave = None
            r = await self.invoke("LeaveRoom", [self.a.room], wait=True)
            self.stats["acts"]["leave"] = r
            return
        # --- поводимось як людина ---
        key = f"{q}:{phase}"
        topic = v.get("topic") or {}
        if phase == "topic" and topic.get("by") == seat and key not in self.done_keys and self.mode != "idle":
            self.done_keys.add(key)
            await asyncio.sleep(self.rng.uniform(0.5, 2.0))
            opts = topic.get("options") or []
            if opts:
                await self.act("topic", {"k": self.rng.choice(opts)["key"]}, "topic")
        elif phase == "write" and not my.get("lie") and key not in self.done_keys and self.mode != "idle":
            self.done_keys.add(key)
            await asyncio.sleep(self.rng.uniform(0.3, 2.0))
            if self.mode == "dice":
                await self.act("lie", {"auto": True}, "lie-auto")
            else:
                await self.act("lie", {"text": self.lie}, "lie")
        elif phase == "pick" and my.get("pick") is None and key not in self.done_keys and self.mode != "idle":
            self.done_keys.add(key)
            await asyncio.sleep(self.rng.uniform(0.3, 2.5))
            opts = [o for o in v.get("options") or [] if not o.get("mine")]
            if opts:
                await self.act("pick", {"i": self.rng.choice(opts)["i"]}, "pick")
        elif phase in ("reveal", "score") and self.mode != "idle":
            for o in v.get("options") or []:
                if o.get("by") and not o.get("truth") and not o.get("decoy") and not o.get("mine") and f"{q}:like" not in self.done_keys:
                    self.done_keys.add(f"{q}:like")
                    await self.act("like", {"i": o["i"]}, "like")
                    break


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8230)
    ap.add_argument("--room", required=True)
    ap.add_argument("--bots", default="")
    ap.add_argument("--secrets", default="")
    ap.add_argument("--seconds", type=int, default=300)
    ap.add_argument("--watch")
    ap.add_argument("--leave")
    a = ap.parse_args()
    a.secrets = [s for s in a.secrets.split(",") if s]
    leave = None
    if a.leave:
        n, rest = a.leave.split("@")
        qq, ph = rest.split(":")
        leave = (n, (int(qq), ph))
    bots = []
    for i, spec in enumerate(x for x in a.bots.split(",") if x):
        parts = spec.split(":", 2)
        nick, mode = parts[0], parts[1] if len(parts) > 1 else "liar"
        lie = parts[2] if len(parts) > 2 else f"вигадка {nick}"
        bots.append(Bot(a, nick, mode, lie, 100 + i, leave=leave[1] if leave and leave[0] == nick else None))
    if a.watch:
        bots.append(Bot(a, a.watch, "watch", "", 999, watch=True))
    for b in bots:
        await b.start()
    t0 = time.time()
    while time.time() - t0 < a.seconds:
        await asyncio.sleep(1)
        # Кінець — коли підсумок бачать усі гравці (і ті, що встали: вони лишаються дивитись).
        players = [b for b in bots if not b.watch]
        if players and all(b.stats["result"] for b in players):
            break
    for b in bots:
        try:
            await b.ws.close()
        except Exception:
            pass
    print(json.dumps([b.stats for b in bots], ensure_ascii=False, indent=1))


if __name__ == "__main__":
    asyncio.run(main())
