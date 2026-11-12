# 🖥 Екран: як програмі екрана отримувати команди з сайту

Адмін на сайті (Бібліотека → **🖥 Екран**, `https://hlechyky.pp.ua/#lib/screen`) міняє чотири речі:

| Що | Поле | Значення |
|---|---|---|
| Яскравість | `brightness` | ціле 0–100 |
| Колір | `color` | ціле 0–1023 |
| Пресет кольору | `preset` | `"red"`, `"green"`, `"blue"`, `"white"`, `"black"` або `null` |
| Калібровка | `calibration` | `true` / `false` |

Програма екрана тримає з'єднання з сайтом і дізнається про кожну зміну одразу — з будь-якого пристрою, з якого
адмін її зробив. А ще програма **пише на сайт**: свою температуру (°C) і результат виміру кольору (x, y, K), коли
адмін натисне «🔬 Виміряти колір» (розділ 5). Сервер — `src/Hlechyky/Screen.cs`, сторінка адміна — `web/screen.js`.

## 1. Ключ

Програма входить **ключем екрана**, не акаунтом. Взяти його: вкладка «🖥 Екран» → «🔌 Підключення програми екрана» →
👁 / 📋. Там же готові адреси для копіювання.

- Ключ передавай заголовком `X-Screen-Key: <ключ>` або в адресі `?key=<ключ>` (для WebSocket з браузера — лише в адресі).
- «🔄 Новий ключ» на сторінці — старий одразу перестає пускати: відкриті з'єднання з ним закриваються, запити
  отримують **401**. Тримай ключ у конфігу програми, не в коді.
- Ключ дає лише **читати** стан екрана — нічого змінити ним не можна.

## 2. Що приходить

Завжди той самий JSON (повний стан, не різниця):

```json
{
  "rev": 42,
  "brightness": 70,
  "color": 512,
  "preset": null,
  "calibration": false,
  "updatedAt": "2026-11-12T14:03:11.52+00:00",
  "measure": null
}
```

`measure` — запит виміру кольору: `null`, коли нічого не просили, або `{ "id": 7, "at": "…" }`, поки адмін чекає
відповіді (що з ним робити — розділ 5).

Як показувати:

1. **`preset` не `null`** — показати пресет (червоний / зелений / синій / білий / чорний). `color` при цьому лишається
   тим, що був до пресету: коли адмін посуне повзунок кольору, `preset` стане `null`, а `color` — новим.
2. **`preset` = `null`** — показати колір `color` (0–1023; як саме число перетворюється на колір — вирішує програма).
3. `brightness` діє завжди (і на пресет, і на свій колір).
4. `calibration: true` — увімкнути режим калібровки, `false` — вимкнути.

`rev` росте з кожною зміною. Той самий `rev` — нічого нового, можна не перемальовувати. Значення сервер уже
обмежив (0–100, 0–1023), невідомих пресетів не буває. Нових полів згодом може побільшати — невідомі поля ігноруй.

Повзунок на сайті шле зміни на ходу (кілька разів на секунду, поки його тягнуть) — програма має витримувати часті
повідомлення; якщо застосувати стан дорого, бери найсвіжіше й пропускай проміжні.

## 3. Спосіб А — WebSocket (рекомендую)

```
wss://hlechyky.pp.ua/api/screen/ws?key=<ключ>
```

- Одразу після з'єднання сервер шле **поточний стан**, далі — новий стан на кожну зміну. Кожне повідомлення —
  окремий текстовий фрейм з JSON вище.
- Слати нічого не обов'язково. Сервер пінгує кожні 20 с (стандартний WebSocket ping — бібліотеки відповідають самі).
  Температуру й вимір можна слати в цей самий сокет текстовим JSON (розділ 5) — відповіді на них не буде.
- Неправильний ключ — сервер відповідає **401** ще до з'єднання. Ключ змінили, поки з'єднання відкрите, —
  сервер закриває його з кодом **1008** (policy violation).
- Сайт перезапускається при деплої (~2–15 с) — з'єднання рветься. **Перепідключайся завжди**: пауза 1 с, 2 с, 4 с…
  до 30 с, після вдалого з'єднання — знову з 1 с. Після 401/1008 — довша пауза (хвилина) і перевірити ключ.

### Python (`pip install "websockets>=14"`)

```python
import asyncio, json, websockets

URL = "wss://hlechyky.pp.ua/api/screen/ws"
KEY = "<ключ з вкладки «Екран»>"

def apply(s):
    if s["preset"]:
        print("пресет", s["preset"], "яскравість", s["brightness"])
    else:
        print("колір", s["color"], "яскравість", s["brightness"])
    print("калібровка", "так" if s["calibration"] else "ні")

async def main():
    pause = 1
    while True:
        try:
            async with websockets.connect(URL, additional_headers={"X-Screen-Key": KEY}) as ws:
                pause = 1
                async for msg in ws:
                    apply(json.loads(msg))
        except websockets.InvalidStatus as e:          # 401 — ключ не той
            print("сервер не пустив:", e.response.status_code)
            pause = 60
        except (OSError, websockets.ConnectionClosed) as e:
            print("з'єднання впало:", e)
        await asyncio.sleep(pause)
        pause = min(pause * 2, 30)

asyncio.run(main())
```

### C# (.NET, без бібліотек)

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var url = new Uri("wss://hlechyky.pp.ua/api/screen/ws");
var key = "<ключ з вкладки «Екран»>";
var pause = TimeSpan.FromSeconds(1);

while (true)
{
    try
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Screen-Key", key);
        await ws.ConnectAsync(url, CancellationToken.None);
        pause = TimeSpan.FromSeconds(1);
        var buf = new byte[4096];
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do { r = await ws.ReceiveAsync(buf, CancellationToken.None); ms.Write(buf, 0, r.Count); }
            while (!r.EndOfMessage);
            if (r.MessageType == WebSocketMessageType.Close) break;
            var s = JsonDocument.Parse(ms.ToArray()).RootElement;
            int brightness = s.GetProperty("brightness").GetInt32();
            int color = s.GetProperty("color").GetInt32();
            string? preset = s.GetProperty("preset").GetString();   // null — свій колір
            bool calibration = s.GetProperty("calibration").GetBoolean();
            Console.WriteLine($"rev {s.GetProperty("rev")}: {preset ?? color.ToString()} @ {brightness}%, калібровка {calibration}");
        }
    }
    catch (Exception e) { Console.WriteLine("з'єднання впало: " + e.Message); }
    await Task.Delay(pause);
    pause = TimeSpan.FromSeconds(Math.Min(pause.TotalSeconds * 2, 30));
}
```

## 4. Спосіб Б — довге опитування (звичайний HTTP GET)

Коли WebSocket незручний (мікроконтролер, проста бібліотека HTTP):

```
GET https://hlechyky.pp.ua/api/screen/device?key=<ключ>                     → одразу поточний стан
GET https://hlechyky.pp.ua/api/screen/device?key=<ключ>&since=<rev>&wait=25 → чекає зміни
```

- Без `since` — відповідь одразу.
- З `since` = останній `rev`, який ти бачив: сервер **тримає запит**, доки стан не зміниться (відповідь одразу, щойно
  адмін щось зробив), або до `wait` секунд (типово 25, найбільше 55) — тоді прийде той самий `rev`. Будь-який `since`,
  що не дорівнює поточному `rev`, — відповідь одразу.
- Таймаут HTTP-клієнта став більшим за `wait` (наприклад, `wait` + 15 с).
- Цикл: запит → застосувати, якщо `rev` новий → одразу наступний запит з `since=rev`. Помилка мережі / 5xx — пауза
  2–5 с і далі. **401** — ключ не той або його змінили.

### Python (`pip install requests`)

```python
import time, requests

BASE = "https://hlechyky.pp.ua/api/screen/device"
KEY = "<ключ з вкладки «Екран»>"
rev = None

while True:
    try:
        params = {} if rev is None else {"since": rev, "wait": 25}
        r = requests.get(BASE, params=params, headers={"X-Screen-Key": KEY}, timeout=40)
        if r.status_code == 401:
            print("ключ не той"); time.sleep(60); continue
        r.raise_for_status()
        s = r.json()
        if s["rev"] != rev:
            rev = s["rev"]
            print(s)          # застосувати стан
    except requests.RequestException as e:
        print("мережа:", e); time.sleep(3)
```

### Перевірити руками

```bash
curl -s -H "X-Screen-Key: <ключ>" https://hlechyky.pp.ua/api/screen/device
```

```bash
curl -s -H "X-Screen-Key: <ключ>" "https://hlechyky.pp.ua/api/screen/device?since=42&wait=55"
```

Другий запит «висить», доки на сайті щось не змінять, — і тоді одразу повертає новий стан.

## 5. Програма пише на сайт: температура і вимір кольору

Один і той самий звіт — JSON з одним або обома полями:

```json
{ "temperature": 41.5 }
```

```json
{ "measure": { "id": 7, "x": 0.3127, "y": 0.3290, "k": 6504 } }
```

| Поле | Що | Межі |
|---|---|---|
| `temperature` | температура екрана, **°C** (дробове можна) | від −100 до 300 |
| `measure.id` | номер запиту з `measure.id` у стані | можна не слати — тоді це відповідь на поточний запит |
| `measure.x`, `measure.y` | координати кольору CIE 1931 xy | від 0 до 1 |
| `measure.k` | корельована колірна температура, **K** | більше 0 |

Куди слати — на вибір:

- **HTTP:** `POST https://hlechyky.pp.ua/api/screen/device` з `X-Screen-Key: <ключ>` (або `?key=`) і
  `Content-Type: application/json`. Відповідь `200 {"ok":true}`; неправильні значення — `400 {"message":"…"}` з
  поясненням; не той ключ — `401`.
- **WebSocket:** надіслати той самий JSON текстовим фреймом у відкритий `/api/screen/ws`. Відповіді нема (сокет шле лише
  стан); неправильне сервер мовчки пропускає — для налагодження зручніше HTTP.

**Температура.** Шли, коли змінилась, або раз на 5–30 с — на сторінці вона одразу оновлюється, а якщо понад 2 хв нічого
не приходило, стає сірою (остання відома). Стан екрана (`rev`) від температури не змінюється. Після перезапуску сайту
температура порожня, доки програма не надішле нову.

**Вимір кольору.**
1. Адмін тисне «🔬 Виміряти колір» → у стані з'являється `"measure": { "id": 7, … }` і `rev` росте — програма отримує це
   як звичайну зміну (WebSocket / опитування).
2. Програма міряє колір на екрані й шле `{ "measure": { "id": 7, "x": …, "y": …, "k": … } }`.
3. Сайт показує x, y і K адміну (разом із тим, що екран тоді показував) і знімає запит: прийде новий стан з
   `"measure": null`.

**Вимір може тривати скільки завгодно** (на малій яскравості — довго): сайт не має строку, запит висить, доки програма
не відповість. Адмін бачить «📏 Екран міряє… 0:42» з лічильником, а в результаті — скільки міряло. Тому:

- міряй **у фоні** (окремий потік / задача), щоб з'єднання жило: WebSocket-бібліотека відповідала на пінги, а
  температура йшла далі (див. приклад на WebSocket нижче);
- поки міряєш, стежи за станом: прийшов `"measure": null` — адмін **скасував** (можна перервати й нічого не слати);
  прийшов інший `id` — адмін почав **заново** (перервати старий і міряти новий). Відповідь на старий `id` сайт усе одно
  прийме й покаже в списку, але запит від неї не зникне;
- до виміру сайт прив'язує те, що екран показував **у мить натискання**; якщо адмін під час виміру змінить яскравість
  чи колір, біля результату буде «⚠ міняли під час виміру». Чи підлаштовуватись під нові налаштування на ходу — вирішує
  програма (простіше — доміряти те, з чим почала).

Поки запит висить, `measure` приходить у кожному стані — тож програма, що перезапустилась, теж його побачить. Пам'ятай
`id` останнього відправленого виміру й не міряй той самий двічі. Виміряти й надіслати «сам», без запиту, теж можна
(без `id`) — на сторінці з'явиться в списку вимірів.

### Python: опитування + температура + вимір

```python
import time, requests

BASE = "https://hlechyky.pp.ua/api/screen/device"
KEY = "<ключ з вкладки «Екран»>"
H = {"X-Screen-Key": KEY}

def apply(s): print("стан", s)                      # показати на екрані
def read_temp(): return 41.5                        # твій датчик, °C
def measure_color(): return 0.3127, 0.3290, 6504    # твій вимір: x, y, K

rev, done, last_temp = None, None, 0
while True:
    try:
        if time.time() - last_temp > 10:            # температура раз на 10 с
            requests.post(BASE, json={"temperature": read_temp()}, headers=H, timeout=10)
            last_temp = time.time()
        params = {} if rev is None else {"since": rev, "wait": 10}
        r = requests.get(BASE, params=params, headers=H, timeout=25)
        if r.status_code == 401:
            print("ключ не той"); time.sleep(60); continue
        r.raise_for_status()
        s = r.json()
        if s["rev"] != rev:
            rev = s["rev"]
            apply(s)
        ask = s.get("measure")
        if ask and ask["id"] != done:
            x, y, k = measure_color()
            requests.post(BASE, json={"measure": {"id": ask["id"], "x": x, "y": y, "k": k}}, headers=H, timeout=10).raise_for_status()
            done = ask["id"]
    except requests.RequestException as e:
        print("мережа:", e); time.sleep(3)
```

(`wait` тут 10 с, а не 25, щоб температура йшла вчасно. Тут вимір блокує цикл — поки міряє, температура не йде і
скасування не видно; для довгих вимірів краще варіант на WebSocket нижче.)

### Python: те саме на WebSocket, вимір у фоні

```python
import asyncio, json, websockets

URL = "wss://hlechyky.pp.ua/api/screen/ws"
KEY = "<ключ з вкладки «Екран»>"

def apply(s): print("стан", s)
def read_temp(): return 41.5
def measure_color(): return 0.3127, 0.3290, 6504    # може йти хвилини — тут він у потоці, не блокує з'єднання

async def temps(ws):
    while True:
        await ws.send(json.dumps({"temperature": read_temp()}))
        await asyncio.sleep(10)

async def measure(ws, mid):
    x, y, k = await asyncio.to_thread(measure_color)
    await ws.send(json.dumps({"measure": {"id": mid, "x": x, "y": y, "k": k}}))

async def main():
    done, job, pause = None, None, 1      # done — id, який уже взяли в роботу; job — вимір, що йде
    while True:
        try:
            async with websockets.connect(URL, additional_headers={"X-Screen-Key": KEY}) as ws:
                pause = 1
                sender = asyncio.create_task(temps(ws))
                try:
                    async for msg in ws:
                        s = json.loads(msg)
                        apply(s)
                        ask = s.get("measure")
                        if job and not job.done() and (ask is None or ask["id"] != done):
                            job.cancel()          # скасували або почали заново (потік доміряє, але результат не піде)
                        if ask and ask["id"] != done:
                            done = ask["id"]
                            job = asyncio.create_task(measure(ws, done))
                finally:
                    sender.cancel()
                    if job: job.cancel()
                    done = None                   # після реконекту запит, що висить, візьмемо знову
        except (OSError, websockets.ConnectionClosed, websockets.InvalidStatus) as e:
            print("з'єднання:", e)
        await asyncio.sleep(pause)
        pause = min(pause * 2, 30)

asyncio.run(main())
```

### Перевірити руками

```bash
curl -s -H "X-Screen-Key: <ключ>" -H "Content-Type: application/json" -d '{"temperature": 41.5}' https://hlechyky.pp.ua/api/screen/device
```

```bash
curl -s -H "X-Screen-Key: <ключ>" -H "Content-Type: application/json" -d '{"measure": {"x": 0.3127, "y": 0.329, "k": 6504}}' https://hlechyky.pp.ua/api/screen/device
```

## 6. «Екран на зв'язку»

Вгорі вкладки адмін бачить 🟢 «Екран на зв'язку» — поки відкритий WebSocket або чекає опитування, і ще ~70 с після
останнього запиту; інакше ⚪ «Екрана не чути — востаннє …». Нічого окремо для цього програмі робити не треба.

## 7. Для розробки

Дев-сервер: `http://127.0.0.1:8100/api/screen/device`, `ws://127.0.0.1:8100/api/screen/ws`; ключ там свій — з
вкладки «Екран» на деві (`/?k=dev`). Адмінське API (кука акаунта адміна): `GET /api/screen` (стан, температура,
останні 20 вимірів, ключ), `POST /api/screen` `{ brightness?, color?, preset?, calibration? }` — змінити лише передане
(`preset: ""` — зняти пресет), `POST /api/screen/measure` — запит виміру, `POST /api/screen/measure/cancel` —
скасувати його, `POST /api/screen/key` — новий ключ.
Повзунок яскравості на сторінці — у децибелах (1…100 на 40 дБ), але програмі приходить те саме ціле 0–100.
Тести — `tests/Hlechyky.Tests/Platform/ScreenTests.cs`.
