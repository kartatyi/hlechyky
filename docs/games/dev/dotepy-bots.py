"""Боти «Дотепів» для живої перевірки: кожен — окреме SignalR-з'єднання (JSON поверх WebSocket).

    python docs/games/dev/dotepy-bots.py --port 8231 --room <id> --nicks Петро,Ганна [--write 2-6] [--vote 1-4] [--leave Ганна@vote] [--secs 600]

Сідають за стіл (JoinRoom), дивляться його (WatchRoom), на кожен свій вид:
  write — здають свої завдання (смішні рядки з запасу) через випадкову паузу, спершу чернетку;
  vote  — якщо мають голос і ще не голосували — голосують за випадкову чужу (у фіналі — одразу всі медалі).
--leave Нік@фаза — цей бот встає з-за столу, щойно побачить фазу (write/vote/reveal/table).
--rematch — коли партію зіграно, перший бот тисне «Ще раз».
Друкує фази й підсумок; завершується, коли партію зіграно (або --secs).
Потрібен пакет websockets. Нік на сервері стане «гість <нік>» — так сайт кличе гостей.
Людину за столом зручно водити справжнім браузером (headless Chrome, D:/or-wt/_tools/cdp2.py), решту — цими ботами.
"""
import argparse, asyncio, json, random, sys, time

import websockets

sys.stdout.reconfigure(encoding="utf-8")
RS = "\x1e"

LINES = [
    "Радіо «Три корови FM» — у нас навіть реклама мукає",
    "Мій кіт сказав би краще, але він спить",
    "Глек не винен, що ви не вмієте жартувати",
    "Борщ із ананасом — смак, що не пробачають",
    "Трактор теж людина, просто з гусеницями",
    "Бабуся бачила все, але нікому не скаже",
    "Ой, то не я, то вітер",
    "Сусід з перфоратором — це вже спосіб життя",
    "Вай-фай на городі ловить лише біля картоплі",
    "Кум приніс салат, а пішов із холодильником",
    "Півень у нас на аутсорсі",
    "Спочатку було слово, а потім теща",
    "Вареники з кавою — сніданок чемпіонів дивацтва",
    "Хто рано встає, той не встигає поснідати",
    "Моя порада: не радьтеся з дядьком на ярмарку",
    "Це не дача, це спортзал без абонемента",
]


class Bot:
    def __init__(self, nick, a, rng, shared):
        self.nick, self.a, self.rng, self.shared = nick, a, rng, shared
        self.inv = 0
        self.pending = {}
        self.view = None
        self.seat = None
        self.status = None
        self.busy = set()
        self.gone = False
        self.done = asyncio.Event()

    async def start(self):
        self.ws = await websockets.connect(f"ws://127.0.0.1:{self.a.port}/hub?nick={self.nick}", max_size=None,
                                           ping_interval=None, open_timeout=30)
        await self.ws.send('{"protocol":"json","version":1}' + RS)
        asyncio.create_task(self.reader())
        asyncio.create_task(self.pinger())
        r = await self.call("JoinRoom", [self.a.room])
        print(f"[{self.nick}] JoinRoom: {r}", flush=True)
        await self.send("WatchRoom", [self.a.room])

    async def pinger(self):
        # ping — до кінця скрипта, а не до кінця партії: інакше сервер закриє з'єднання, і за 20 с бота знімуть з-за столу
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
                        asyncio.create_task(self.react())
        except Exception as e:
            if not self.gone:
                print(f"[{self.nick}] зв'язок: {e}", flush=True)

    async def react(self):
        v = self.view or {}
        phase = v.get("phase")
        if self.status == "finished":
            if not self.shared.get("finished"):
                self.shared["finished"] = True
                res = v.get("result") or {}
                print(f"== ПАРТІЯ: переможці {res.get('winners')}, рахунок {[p['nick'] + ' ' + str(p['score']) for p in v.get('players', [])]}", flush=True)
                if self.a.rematch and not self.shared.get("rematched"):
                    self.shared["rematched"] = True
                    await asyncio.sleep(3)
                    print(f"[{self.nick}] Ще раз: {await self.call('Rematch', [self.a.room])}", flush=True)
                    self.shared["finished"] = False
                    return
            self.done.set()
            return
        if self.status != "playing" or self.seat is None or self.gone:
            return
        if self.a.leave and self.a.leave.startswith(self.nick + "@") and phase == self.a.leave.split("@")[1]:
            self.gone = True
            print(f"[{self.nick}] встаю посеред «{phase}»: {await self.call('LeaveRoom', [self.a.room])}", flush=True)
            self.done.set()
            return
        key = (v.get("round"), phase)
        if self.shared.get("say") != key and self.nick == self.a.nicks[0]:
            self.shared["say"] = key
            print(f"-- раунд {v.get('round')}/{v.get('rounds')} {phase} ({v.get('mode')})", flush=True)
        me = v.get("me") or {}
        if phase == "write":
            for t in me.get("tasks", []):
                tag = ("w", v.get("round"), t["i"])
                if t["done"] or tag in self.busy:
                    continue
                self.busy.add(tag)
                asyncio.create_task(self.write(t["i"]))
        elif phase == "vote" and me.get("voter") and not me.get("picks"):
            card = v.get("card") or {}
            tag = ("v", v.get("round"), card.get("i"))
            if tag in self.busy:
                return
            self.busy.add(tag)
            asyncio.create_task(self.vote(card))

    async def write(self, i):
        lo, hi = self.a.write
        await asyncio.sleep(self.rng.uniform(lo, hi) / 2)
        text = self.rng.choice(LINES)
        await self.send("Input", [self.a.room, "draft", {"i": i, "text": text[: len(text) // 2]}])
        await asyncio.sleep(self.rng.uniform(lo, hi) / 2)
        r = await self.call("Act", [self.a.room, "answer", {"i": i, "text": text}])
        if not r or not r.get("ok"):
            print(f"[{self.nick}] answer {i}: {r}", flush=True)

    async def vote(self, card):
        lo, hi = self.a.vote
        await asyncio.sleep(self.rng.uniform(lo, hi))
        me = (self.view or {}).get("me") or {}
        mine = set(me.get("mine", []))
        others = [i for i in range(len(card.get("answers", []))) if i not in mine]
        self.rng.shuffle(others)
        picks = others[: max(1, card.get("perVoter", 1))]
        r = await self.call("Act", [self.a.room, "vote", {"card": card["i"], "picks": picks}])
        if not r or not r.get("ok"):
            print(f"[{self.nick}] vote {card.get('i')}: {r}", flush=True)


def span(s):
    lo, hi = (float(x) for x in s.split("-"))
    return lo, hi


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8231)
    ap.add_argument("--room", required=True)
    ap.add_argument("--nicks", required=True)
    ap.add_argument("--write", type=span, default=(2, 6))
    ap.add_argument("--vote", type=span, default=(1, 4))
    ap.add_argument("--leave")
    ap.add_argument("--rematch", action="store_true")
    ap.add_argument("--secs", type=int, default=900)
    ap.add_argument("--seed", type=int, default=1)
    a = ap.parse_args()
    a.nicks = a.nicks.split(",")
    shared = {}
    bots = [Bot(n, a, random.Random(a.seed * 31 + k), shared) for k, n in enumerate(a.nicks)]
    for b in bots:
        await b.start()
        await asyncio.sleep(0.2)
    t0 = time.time()
    while time.time() - t0 < a.secs and not all(b.done.is_set() for b in bots):
        await asyncio.sleep(0.5)
    print("боти: кінець", round(time.time() - t0), "с", flush=True)


if __name__ == "__main__":
    asyncio.run(main())
