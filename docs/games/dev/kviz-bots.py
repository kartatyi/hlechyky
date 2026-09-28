"""Боти для живої перевірки «Скільки?», «Де це?», «Глек-слова наввипередки» й Мафії: легкі SignalR-клієнти (JSON поверх
WebSocket, без negotiate), що сідають за стіл і грають, як ліниві люди, і заразом стережуть приховане.

    python kviz-bots.py --port 8265 --game skilky --nicks Бот1,Бот2,Бот3 [--room <id>] [--create '{"questions":"3"}']
                        [--start] [--watch Глядач] [--leave Бот2@2] [--rematch 1] [--seconds 600] [--slow 1.0]

- без --room бот чекає першу кімнату цієї гри в лобі й сідає туди; --create — перший бот ставить стіл сам;
- --start — господар (перший бот) тисне «Почати», щойно за столом усі боти;
- --watch НІК — ще одне з'єднання дивиться збоку (глядач): перевірка, що до розкриття він не бачить прихованого;
- --leave НІК@N — цей бот встає з-за столу на N-му раунді (Мафія — на N-му дні);
- --rematch K — після кінця партії господар тисне «Ще раз» K разів;
- --slow — множник пауз (0.3 — поспішні боти, 3 — сонні).

Що робить бот: «Скільки?» — пише випадкове число (інколи рядком «10 000» чи «2,5»); «Де це?» — ставить шпильку, інколи
переставляє, тисне «Готово», на розкритті — «Далі»; наввипередки — розгадує слово справжнім перебором за кольорами
(список data/words/uk-5.txt); Мафія — нічні справи за роллю й голос удень. Витоки — рядок «ВИТІК» у stdout і лічильник
у підсумку. Байти видів і кадрів рахуються на кожне з'єднання — підсумок наприкінці.
"""
import argparse, asyncio, json, os, random, sys, time

import websockets

RS = b"\x1e"
HERE = os.path.dirname(os.path.abspath(__file__))


def load_words(n=5):
    path = os.path.normpath(os.path.join(HERE, '..', '..', '..', 'data', 'words', f'uk-{n}.txt'))
    try:
        with open(path, encoding='utf-8') as f:
            return [w.strip() for w in f if len(w.strip()) == n]
    except OSError:
        return []


# наввипередки на 4 і 6 літер (опція «Довжина слова», прохід №3) — свої списки відповідей
WORDS_BY_LEN = {n: load_words(n) for n in (4, 5, 6)}


WORDS = load_words()



def fits(cand, rows):
    """Чи узгоджується слово з усіма відкритими рядками (ті самі правила, що Wordle.Marks на сервері)."""
    for r in rows:
        w, m = r['word'], r['marks']
        if score(cand, w) != m:
            return False
    return True


def score(answer, guess):
    n = len(answer)
    res = ['B'] * n
    left = {}
    for i in range(n):
        if guess[i] == answer[i]:
            res[i] = 'G'
        else:
            left[answer[i]] = left.get(answer[i], 0) + 1
    for i in range(n):
        if res[i] == 'G':
            continue
        if left.get(guess[i], 0) > 0:
            res[i] = 'Y'
            left[guess[i]] -= 1
    return ''.join(res)


class Stats:
    def __init__(self):
        self.leaks = 0
        self.acts = 0
        self.fails = {}
        self.bytes = {}   # (nick, kind) -> [count, total, max]

    def add(self, nick, kind, n):
        s = self.bytes.setdefault((nick, kind), [0, 0, 0])
        s[0] += 1
        s[1] += n
        s[2] = max(s[2], n)


class Bot:
    def __init__(self, nick, a, rng, stats, host=False, watcher=False):
        self.nick, self.a, self.rng, self.stats = nick, a, rng, stats
        self.host, self.watcher = host, watcher
        self.ws = None
        self.inv = 0
        self.pending = {}
        self.room = a.room
        self.rv = None
        self.done = asyncio.Event()
        self.plan = set()
        self.left = False
        self.rematches = 0
        self.finished_rounds = set()
        self.leave_at = None
        for spec in (a.leave or '').split(','):
            if '@' in spec and spec.split('@')[0] == nick:
                self.leave_at = int(spec.split('@')[1])

    def log(self, *parts):
        print(f"[{time.strftime('%H:%M:%S')}] {self.nick}:", *parts, flush=True)

    def leak(self, what):
        self.stats.leaks += 1
        self.log('ВИТІК', what)

    async def start(self, created=None):
        url = f"ws://127.0.0.1:{self.a.port}/hub?nick={self.nick}"
        self.ws = await websockets.connect(url, max_size=None, compression=None, ping_interval=None, open_timeout=30)
        await self.ws.send(b'{"protocol":"json","version":1}' + RS, text=True)
        asyncio.create_task(self.reader())
        asyncio.create_task(self.pinger())
        if self.host and self.a.create is not None and not self.room:
            opts = json.loads(self.a.create) if self.a.create else {}
            r = await self.invoke("CreateRoom", [self.a.game, opts])
            self.log("CreateRoom", r)
            self.room = r.get('roomId')
            if created:
                created.set_result(self.room)
        while not self.room:
            await asyncio.sleep(0.3)
        if self.watcher:
            await self.invoke("WatchRoom", [self.room], wait=False)
            self.log("дивлюсь збоку", self.room)
            return
        if not (self.host and self.a.create is not None):
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

    async def act(self, action, payload=None):
        self.stats.acts += 1
        r = await self.invoke("Act", [self.room, action, payload])
        if r and not r.get('ok'):
            key = action + ': ' + (r.get('message') or '')
            self.stats.fails[key] = self.stats.fails.get(key, 0) + 1
        return r

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
                                if r.get("game") == self.a.game and r.get("status") == "lobby":
                                    self.room = r["id"]
                                    break
                        elif tgt == "room" and args and args[0].get("room", {}).get("id") == self.room:
                            self.stats.add(self.nick, 'room', len(rec))
                            self.rv = args[0]
                            try:
                                self.check(self.rv, rec)
                                self.on_view()
                            except Exception as e:  # бот не має падати від кривого виду — це теж знахідка
                                self.log('помилка обробки виду', repr(e))
                        elif tgt == "frame" and args and args[0].get("id") == self.room:
                            self.stats.add(self.nick, 'frame', len(rec))
                            self.check_frame(args[0].get('f'), rec)
        except websockets.ConnectionClosed:
            self.done.set()

    async def pinger(self):
        while not self.done.is_set():
            await asyncio.sleep(10)
            try:
                await self.ws.send(b'{"type":6}' + RS, text=True)
            except websockets.ConnectionClosed:
                return

    def later(self, key, delay, fn):
        if key in self.plan:
            return
        self.plan.add(key)

        async def run():
            await asyncio.sleep(delay * self.a.slow)
            if self.left or not self.rv:
                return
            try:
                await fn()
            except websockets.ConnectionClosed:
                pass
            except Exception as e:
                self.log('помилка дії', repr(e))
        asyncio.create_task(run())

    # ---------------------------------------------------------------- спільне

    def on_view(self):
        rv = self.rv
        room = rv['room']
        v = rv.get('view') or {}
        if room['status'] == 'finished':
            key = 'fin:' + str(room['round'])
            if key not in self.plan:
                self.plan.add(key)
                self.log('кінець партії:', (room.get('result') or {}).get('text'))
                if self.host and self.rematches < self.a.rematch:
                    self.rematches += 1
                    self.later('rematch:' + str(room['round']), 2.5, lambda: self.rematch())
                elif self.host or self.a.rematch == 0:
                    pass
            return
        if self.host and self.a.start and room['status'] == 'lobby':
            seated = sum(1 for s in room['seats'] if s.get('nick'))
            if seated >= self.a.expect:
                self.later('start:' + str(room['round']), 1.0, lambda: self.start_room())
        if room['status'] != 'playing' or rv.get('seat') is None:
            return
        getattr(self, 'play_' + self.a.game.replace('-', '_'))(room, v, rv['seat'])

    async def start_room(self):
        r = await self.invoke("StartRoom", [self.room])
        self.log("StartRoom", r)

    async def rematch(self):
        r = await self.invoke("Rematch", [self.room])
        self.log("Rematch", r)

    async def leave(self):
        self.left = True
        r = await self.invoke("LeaveRoom", [self.room])
        self.log("LeaveRoom", r)

    def maybe_leave(self, room, n):
        if self.leave_at is not None and n >= self.leave_at and not self.left:
            self.later('leave', 0.5 + self.rng.random() * 2, lambda: self.leave())
            return True
        return False

    # ---------------------------------------------------------------- витоки

    def check(self, rv, raw):
        v = rv.get('view') or {}
        seat = rv.get('seat')
        g = self.a.game
        if g == 'skilky':
            if v.get('phase') in ('ask', 'between') and v.get('reveal'):
                self.leak('skilky: розкриття посеред відповіді')
            if v.get('phase') == 'between' and v.get('question'):
                self.leak('skilky: текст питання в паузі')
            if seat is None and v.get('my') is not None:
                self.leak('skilky: глядач має my')
        elif g == 'geo':
            if v.get('phase') in ('between', 'guess'):
                if v.get('reveal') or v.get('recap'):
                    self.leak('geo: правда до розкриття')
                s = raw.decode('utf-8', 'replace')
                for bad in ('"lat"', '"lon"', 'wikimedia', '"region"', '"rows"'):
                    if bad in s:
                        self.leak('geo: ' + bad + ' у виді ' + v.get('phase'))
                if seat is None and v.get('my') is not None:
                    self.leak('geo: глядач має my')
        elif g == 'wordle-race':
            if v.get('phase') == 'play':
                if v.get('answer'):
                    self.leak('race: слово у виді посеред раунду')
                me = v.get('me') or {}
                # з проходу №3 хто вже вгадав чи відмучився, бачить чужі літери; у спринті — ніхто до кінця
                peek = v.get('mode') != 'sprint' and (me.get('solved') or me.get('failed'))
                for p in v.get('players') or []:
                    if p.get('words') is not None and p.get('seat') != seat and not peek:
                        self.leak('race: чужі літери посеред раунду')
        elif g == 'mafia':
            me = v.get('me')
            if v.get('phase') not in ('lobby', 'done') and me and me.get('alive'):
                mine = me.get('role')
                for p in v.get('players') or []:
                    if p['seat'] == seat or not p.get('role') or not p.get('alive'):
                        continue
                    if mine in ('mafia', 'don') and p['role'] in ('mafia', 'don'):
                        continue
                    self.leak(f"mafia: {mine} бачить роль живого {p['seat']} ({p['role']})")
                if mine not in ('mafia', 'don') and (v.get('night') or {}).get('chat'):
                    self.leak('mafia: нічний чат не мафії')
            if seat is None and v.get('phase') not in ('lobby', 'done'):
                if any(p.get('role') and p.get('alive') for p in v.get('players') or []):
                    self.leak('mafia: глядач бачить роль живого')

    def check_frame(self, f, raw):
        s = raw.decode('utf-8', 'replace')
        if self.a.game == 'geo':
            for bad in ('"lat"', '"lon"', '"x"', 'wikimedia'):
                if bad in s:
                    self.leak('geo: ' + bad + ' у кадрі')

    # ---------------------------------------------------------------- «Скільки?»

    def play_skilky(self, room, v, seat):
        if self.maybe_leave(room, v.get('round') or 0):
            return
        if v.get('phase') != 'ask' or v.get('my') is not None:
            return
        key = f"ask:{room['round']}:{v.get('round')}"

        async def go():
            r = self.rng.random()
            if r < 0.15:
                val = f"{self.rng.randint(1, 99)} {self.rng.randint(0, 999):03d}"   # «12 345»
            elif r < 0.25:
                val = f"{self.rng.randint(0, 30)},{self.rng.randint(1, 9)}"          # «2,5»
            elif v.get('unit') == 'рік':
                val = self.rng.randint(900, 2020)
            else:
                val = round(10 ** (self.rng.random() * 5))
            res = await self.act('answer', {'value': val})
            self.log('число', val, '→', (res or {}).get('message'))
        self.later(key, 1 + self.rng.random() * 6, go)

    # ---------------------------------------------------------------- «Де це?»

    def play_geo(self, room, v, seat):
        if self.maybe_leave(room, v.get('round') or 0):
            return
        ph, rnd = v.get('phase'), f"{room['round']}:{v.get('round')}"
        if ph == 'guess' and v.get('my') is None:
            async def pin():
                x, y = self.rng.randint(400, 3600), self.rng.randint(300, 2400)
                r = await self.act('guess', {'x': x, 'y': y})
                if self.rng.random() < 0.4:      # передумав
                    await asyncio.sleep((0.5 + self.rng.random()) * self.a.slow)
                    await self.act('guess', {'x': x + self.rng.randint(-200, 200), 'y': y + self.rng.randint(-200, 200)})
                if self.rng.random() < 0.8:
                    await asyncio.sleep((0.5 + self.rng.random() * 2) * self.a.slow)
                    await self.act('ready')
            self.later('pin:' + rnd, 1 + self.rng.random() * 5, pin)
        elif ph == 'reveal':
            self.later('next:' + rnd, 2 + self.rng.random() * 4, lambda: self.act('next'))

    # ---------------------------------------------------------------- наввипередки

    def play_wordle_race(self, room, v, seat):
        if self.maybe_leave(room, v.get('round') or 0):
            return
        me = v.get('me')
        if v.get('phase') != 'play' or not me or me.get('solved') or me.get('failed'):
            return
        rows = me.get('rows') or []
        hints = me.get('hints') or []
        words = WORDS_BY_LEN.get(v.get('len') or 5) or WORDS
        key = f"w:{room['round']}:{v.get('round')}:{len(me.get('played') or [])}:{len(rows)}:{len(hints)}"
        # 💡 інколи бере підказку — на третій спробі й далі
        if v.get('hint') and len(rows) >= 2 and len(hints) < (v.get('maxHints') or 0) and self.rng.random() < .5:
            async def hint():
                r = await self.act('hint', {})
                self.log('підказка →', (r or {}).get('message'))
            self.later('h' + key, 1 + self.rng.random() * 3, hint)
            return

        async def go():
            cands = [w for w in words if fits(w, rows) and all(w[h['i']] == h['ch'] for h in hints)]
            if not cands:
                cands = words
            word = self.rng.choice(cands)
            r = await self.act('guess', {'word': word})
            self.log('слово', word, f'({len(cands)} можливих)', '→', (r or {}).get('message'))
        self.later(key, 3 + self.rng.random() * 8, go)

    # ---------------------------------------------------------------- Мафія

    def play_mafia(self, room, v, seat):
        me = v.get('me')
        if self.maybe_leave(room, v.get('day') or 0):
            return
        if not me or not me.get('alive'):
            return
        ph, day = v.get('phase'), v.get('day')
        alive = [p['seat'] for p in v.get('players') or [] if p.get('alive')]
        others = [s for s in alive if s != seat]
        role = me.get('role')
        mafia = {p['seat'] for p in v.get('players') or [] if p.get('role') in ('mafia', 'don')}
        rules = v.get('rules') or {}
        quiet = not rules.get('firstNightKill') and day == 1
        key = f"{room['round']}:{ph}:{day}"
        if ph == 'night':
            if role in ('mafia', 'don') and not quiet:
                targets = [s for s in others if s not in mafia]
                if targets:
                    self.later('k' + key, 2 + self.rng.random() * 6, lambda: self.act('kill', {'seat': self.rng.choice(targets)}))
                self.later('say' + key, 1 + self.rng.random() * 3, lambda: self.act('say', {'text': self.rng.choice(['ну що, кого?', 'я за тихого', 'давай швидше'])}))
            elif role == 'maniac' and not quiet and others:
                self.later('m' + key, 2 + self.rng.random() * 6, lambda: self.act('kill', {'seat': self.rng.choice(others)}))
            elif role == 'sheriff' and others:
                self.later('c' + key, 2 + self.rng.random() * 6, lambda: self.act('check', {'seat': self.rng.choice(others)}))
            elif role == 'doctor' and alive:
                self.later('h' + key, 2 + self.rng.random() * 6, lambda: self.act('heal', {'seat': self.rng.choice(alive)}))
            elif role == 'kuma' and others:
                self.later('b' + key, 2 + self.rng.random() * 6, lambda: self.act('block', {'seat': self.rng.choice(others)}))
        elif ph == 'vote' and others:
            self.later('v' + key, 2 + self.rng.random() * 8, lambda: self.act('vote', {'seat': self.rng.choice(others)}))


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--port', type=int, required=True)
    ap.add_argument('--game', required=True, choices=['skilky', 'geo', 'wordle-race', 'mafia'])
    ap.add_argument('--nicks', required=True)
    ap.add_argument('--room')
    ap.add_argument('--create', help='JSON опцій столу; перший бот ставить стіл сам')
    ap.add_argument('--start', action='store_true')
    ap.add_argument('--expect', type=int, default=0, help='скільки сидячих чекати перед «Почати» (типово — усі боти)')
    ap.add_argument('--watch')
    ap.add_argument('--leave')
    ap.add_argument('--rematch', type=int, default=0)
    ap.add_argument('--seconds', type=int, default=600)
    ap.add_argument('--slow', type=float, default=1.0)
    ap.add_argument('--seed', type=int, default=7)
    a = ap.parse_args()
    nicks = [n for n in a.nicks.split(',') if n]
    a.expect = a.expect or len(nicks)
    stats = Stats()
    rng = random.Random(a.seed)
    bots = [Bot(n, a, random.Random(rng.random()), stats, host=(i == 0)) for i, n in enumerate(nicks)]
    created = asyncio.get_running_loop().create_future() if a.create is not None and not a.room else None
    await bots[0].start(created)
    if created:
        a.room = await created
        for b in bots[1:]:
            b.room = a.room
    await asyncio.gather(*(b.start() for b in bots[1:]))
    if a.watch:
        w = Bot(a.watch, a, random.Random(1), stats, watcher=True)
        w.room = bots[0].room
        await w.start()
        bots.append(w)
    t0 = time.time()
    while time.time() - t0 < a.seconds:
        await asyncio.sleep(1)
        rv = bots[0].rv
        if rv and rv['room']['status'] == 'finished' and bots[0].rematches >= a.rematch and 'fin:' + str(rv['room']['round']) in bots[0].plan:
            await asyncio.sleep(2)
            break
    print('--- підсумок ---')
    print('дій:', stats.acts, 'витоків:', stats.leaks)
    for k, n in sorted(stats.fails.items(), key=lambda x: -x[1]):
        print(f'  відмова ×{n}: {k}')
    for (nick, kind), (cnt, tot, mx) in sorted(stats.bytes.items()):
        print(f'  {nick} {kind}: {cnt} шт, у середньому {tot // max(cnt, 1)} Б, найбільший {mx} Б')
    for b in bots:
        try:
            await b.ws.close()
        except Exception:
            pass


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    asyncio.run(main())
