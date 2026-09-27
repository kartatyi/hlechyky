"""Боти «Своєї гри» для живої перевірки: кожен — окреме SignalR-з'єднання (JSON поверх WebSocket).

    C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe docs/games/dev/svoya-bots.py --port 8262 --room <id> \
        --nicks Петро,Ганна [--buzz 0.6] [--right 0.6] [--react 0.4-2.5] [--think 2-6] [--early] [--appeal 0.2] [--secs 1800]

(Python — лише повним шляхом: голий `python`/`py` на цій машині смикає «оновлення» Install Manager, і AppXSvc тече пам'яттю.)

Сідають за стіл (JoinRoom), дивляться його (WatchRoom) і грають, як люди:
  board — обирач тисне випадкову відкриту клітинку за 1–3 с;
  reading/buzz — коли кнопка відкрита для бота, з імовірністю --buzz тисне її через --react секунд
                 (--early — тиснуть і під час читання: перевірити фальстарт у early=lock);
  answering — відповідає за --think секунд: з імовірністю --right правильно (відповідь бере з вбудованих пакетів
              data/svoya/builtin за текстом запитання), інакше мимо;
  reveal — хто помилився, з імовірністю --appeal оскаржує;
  кіт — віддає випадковому, ціну бере більшу; аукціон — пас або ставка мінімуму; фінал — викреслює, ставить, відповідає.
Друкує фази, хто що відповів і підсумок; завершується, коли партію зіграно (або --secs).
Нік на сервері стане «гість <нік>» — так сайт кличе гостей.
"""
import argparse, asyncio, glob, json, os, random, sys, time

import websockets

sys.stdout.reconfigure(encoding="utf-8")
RS = "\x1e"
T0 = time.time()
HERE = os.path.dirname(os.path.abspath(__file__))


def answers():
    """Текст запитання → відповідь з усіх вбудованих пакетів."""
    out = {}
    for f in glob.glob(os.path.join(HERE, "..", "..", "..", "data", "svoya", "builtin", "*.json")):
        with open(f, encoding="utf-8") as fh:
            p = json.load(fh)
        for r in p.get("rounds", []):
            for t in r.get("themes", []):
                for q in t.get("questions", []):
                    out[q.get("text", "")] = q.get("answer", "")
    return out


class Bot:
    def __init__(self, nick, a, rng, shared, book):
        self.nick, self.a, self.rng, self.shared, self.book = nick, a, rng, shared, book
        self.inv = 0
        self.pending = {}
        self.view = None
        self.seat = None
        self.status = None
        self.busy = set()
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
            print(f"[{self.nick}] зв'язок: {e}", flush=True)

    def once(self, key, coro):
        if key in self.busy:
            return
        self.busy.add(key)
        asyncio.create_task(coro)

    def react(self):
        v = self.view or {}
        ph = v.get("phase")
        me = v.get("me") or {}
        sp = me.get("special") or {}
        cell = v.get("cell") or {}
        qkey = f"{v.get('round')}:{cell.get('theme')}:{cell.get('q')}"
        if self.nick == self.shared["first"]:
            k = f"{ph}:{qkey}:{v.get('answering')}"
            if self.shared.get("last") != k:
                self.shared["last"] = k
                extra = ""
                if ph == "reading" and v.get("question"):
                    extra = f" «{(v['question'].get('text') or '')[:60]}»"
                if ph == "reveal" and v.get("answer"):
                    extra = f" відповідь: {v['answer'].get('text')}; рахунок {[s for s in v.get('scores', [])][:6]}"
                w = " ⏳голос" if v.get("waiting") else ""
                print(f"-- {time.time() - T0:6.1f} с {ph}{w} р{v.get('round')} {qkey}{extra}", flush=True)
        if self.status == "finished" or ph == "done":
            if not self.shared.get("printed"):
                self.shared["printed"] = True
                print(f"== партію зіграно: {v.get('result')} error={v.get('error')}", flush=True)
            self.done.set()
            return
        if ph == "board" and me.get("canPick"):
            self.once("pick:" + qkey + str(v.get("chooser")), self.pick(v))
        if ph in ("reading", "buzz") and me.get("canBuzz") and (ph == "buzz" or self.a.early):
            self.once(f"buzz:{qkey}:{ph}", self.buzz(qkey))
        if ph == "answering" and me.get("canAnswer"):
            self.once("answer:" + qkey, self.answer(v))
        if ph == "reveal" and me.get("canAppeal") and self.rng.random() < self.a.appeal:
            self.once("appeal:" + qkey, self.do("appeal"))
        if ph == "cat":
            if sp.get("canGive"):
                self.once("give:" + qkey, self.give(v))
            if sp.get("canCatPrice"):
                self.once("catPrice:" + qkey, self.do("catPrice", {"max": True}))
        if ph == "auction":
            a = v.get("auction") or {}
            if a.get("turn") == self.seat and (sp.get("canBid") or sp.get("canPass")):
                self.once(f"bid:{qkey}:{len(a.get('bids') or [])}", self.bid(v))
        if ph == "strike" and sp.get("canStrike") and (v.get("final") or {}).get("turn") == self.seat:
            self.once(f"strike:{len((v.get('final') or {}).get('struck') or [])}", self.strike(v))
        if ph == "bet" and sp.get("canBet") and sp.get("bet") is None:
            self.once("bet", self.bet(v))
        if ph == "final" and sp.get("canFinalAnswer") and not sp.get("answer"):
            self.once("final", self.final(v))

    async def do(self, action, payload=None, delay=(0.5, 1.5)):
        await asyncio.sleep(self.rng.uniform(*delay))
        r = await self.act(action, payload)
        if r and not r.get("ok"):
            print(f"   [{self.nick}] {action}: {r.get('message')}", flush=True)
        return r

    async def pick(self, v):
        await asyncio.sleep(self.rng.uniform(1, 3))
        cells = [(ti, qi) for ti, t in enumerate(v.get("board") or []) for qi, c in enumerate(t["cells"]) if c["open"]]
        if cells:
            ti, qi = self.rng.choice(cells)
            await self.act("pick", {"theme": ti, "q": qi})

    async def buzz(self, qkey):
        if self.rng.random() >= self.a.buzz:
            return
        await asyncio.sleep(self.rng.uniform(*self.a.react))
        r = await self.act("buzz")
        print(f"   [{self.nick}] 🔔 {(r or {}).get('message') or ('ok' if (r or {}).get('ok') else r)}", flush=True)

    async def answer(self, v):
        await asyncio.sleep(self.rng.uniform(*self.a.think))
        q = v.get("question") or {}
        right = self.book.get(q.get("text") or "")
        text = right if right and self.rng.random() < self.a.right else self.rng.choice(["не знаю", "Київ", "1991", "Шевченко", "борщ"])
        r = await self.act("answer", {"text": text})
        print(f"   [{self.nick}] відповідь «{text}»: {(r or {}).get('message')}", flush=True)

    async def give(self, v):
        await asyncio.sleep(self.rng.uniform(1, 2))
        seats = [i for i, n in enumerate((v.get("scores") or [])) if i != self.seat]
        for s in self.rng.sample(seats, len(seats)):
            r = await self.act("give", {"seat": s})
            if r and r.get("ok"):
                return

    async def bid(self, v):
        await asyncio.sleep(self.rng.uniform(1, 2.5))
        a = v.get("auction") or {}
        sp = (v.get("me") or {}).get("special") or {}
        if sp.get("canBid") and self.rng.random() < 0.5:
            await self.act("bid", {"amount": a.get("minBid")})
        else:
            await self.act("pass")

    async def strike(self, v):
        await asyncio.sleep(self.rng.uniform(1, 2))
        f = v.get("final") or {}
        left = [i for i in range(len(f.get("themes") or [])) if i not in (f.get("struck") or [])]
        if left:
            await self.act("strike", {"theme": self.rng.choice(left)})

    async def bet(self, v):
        await asyncio.sleep(self.rng.uniform(1, 4))
        mine = (v.get("scores") or [0] * 9)[self.seat]
        await self.act("bet", {"amount": max(1, int(mine * self.rng.uniform(0.2, 1)))})

    async def final(self, v):
        await asyncio.sleep(self.rng.uniform(3, 8))
        q = v.get("question") or {}
        right = self.book.get(q.get("text") or "")
        text = right if right and self.rng.random() < self.a.right else "не знаю"
        await self.act("answer", {"text": text})


def span(s):
    lo, hi = s.split("-")
    return (float(lo), float(hi))


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--room", required=True)
    ap.add_argument("--nicks", required=True)
    ap.add_argument("--buzz", type=float, default=0.6)
    ap.add_argument("--right", type=float, default=0.6)
    ap.add_argument("--react", default="0.4-2.5")
    ap.add_argument("--think", default="2-6")
    ap.add_argument("--early", action="store_true")
    ap.add_argument("--appeal", type=float, default=0.0)
    ap.add_argument("--secs", type=int, default=1800)
    ap.add_argument("--seed", type=int, default=11)
    a = ap.parse_args()
    a.react, a.think = span(a.react), span(a.think)
    book = answers()
    print(f"відповідей у книзі: {len(book)}", flush=True)
    nicks = a.nicks.split(",")
    shared = {"first": nicks[0]}
    bots = [Bot(n, a, random.Random(a.seed + i), shared, book) for i, n in enumerate(nicks)]
    for b in bots:
        await b.start()
    t0 = time.time()
    while time.time() - t0 < a.secs and not all(b.done.is_set() for b in bots):
        await asyncio.sleep(0.5)


if __name__ == "__main__":
    asyncio.run(main())
