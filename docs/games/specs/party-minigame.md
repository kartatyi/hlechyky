# Міні-гра вечірки — контракт (05.10.2026)

«Глечикова вечірка» (`vechirka`) — дошка, між ходами якої всі за столом грають коротку міні-гру з нашого каталогу.
Міні-гра **живе всередині столу вечірки**: вечірка створює екземпляр іншої гри, дає їй свій `IRoomContext`, ганяє
Configure/Start/Act/Tick/View/Frame і перехоплює Finish. Код каркаса — `src/Hlechyky/Games/Party.cs`; зразок —
Крижина (`Impl/Icefloe.cs`); стенд — `mgprobe` (`Impl/MgProbe.cs`, `web/games/mgprobe.js`).

## 1. Опції режиму (`PartyMode.Read(options)` у `Configure`)
- `party=1` — режим вечірки; без нього `PartyMode.Read` → null, гра звичайна.
- `bots=0,3` — місця ботів (може бути порожнім): там грає **бот твоєї гри** (як у соло з ботом).
- `botlvl=easy|normal|hard` — рівень (`PartyMode.Level`, той самий `LiveBots.Level`).
- Решта ключів — від вечірки, якщо гра їх попросить (тема тощо; поки не використовується).
- У `Info.Options` ключі `party`/`bots` **не додавай**: `Rooms.Effective` відкидає невідомі, тож у лобі режим не
  ввімкнеш. `botlvl` у звичайній грі — це твоя `LiveBots.LevelOption`, якщо вона є.

Місця: вечірка садить людей і ботів на 0..N−1 (`Ctx.Players` = N, 2–8). `Ctx.Seated(i)` — лише присутня людина:
бот і людина, що відпала, — `false`. Людина, що відпала **до** міні-гри, приходить ботом (`bots`). Вихід **посеред**
міні-гри грі не прокидається (`OnLeave` не кличуть): місце просто стоїть — гра мусить дограти й без нього.

## 2. Інтерфейс `IPartyMinigame` (лише такі ігри йдуть у пул — `PartyPool`)
```csharp
public sealed class Tyr : Game, IPartyMinigame
{
    public string Howto => "Стріляй по глечиках, не по гусях. Мишка/дотик — приціл і постріл";   // 1–2 рядки
    public int PartyCapMs => 75_000;    // жорстка стеля від Start, ≤ 120 000 (хост і так обріже)
    public int PartyMin => 2;  public int PartyMax => 8;
    public IReadOnlyDictionary<int, long> PartyScores() => …;   // поточні scores КОЖНОГО місця 0..N−1
}
```
У режимі вечірки гра: **одна коротка партія** (45–90 с — у брифі гри), без лобі-фаз, готовності й «🤖 + бот»;
сама кінчає `Ctx.Finish(winners, log, scores)` з **повними scores для кожного місця** (більше = краще, рівні —
поділене місце) до стелі, навіть якщо всі стоять AFK. Вечірка рахує місця **лише за scores**; winners гри — для
журналу (хост бере переможцями всіх із найкращим score).

## 3. Що робить хост (`MinigameHost`) і що глушиться (`SubRoomContext`)
- `Rng` — свій, засіяний з батьківського (детермінізм за сідом столу); `Clock`, `Services`, `RoomId` — батьківські;
  `Round` = 1; `HostSeat` — місце господаря в підгрі (або null); `NickOf` — нік людини.
- `Finish` перехоплено: до кімнати не доходить (ні Журналу, ні черепків, ні таблиць). Другий Finish — мимо.
- `Score`/`Award` (ачівки, рекорди) — **глушаться**. Не шли їх у режимі вечірки взагалі (тест ловить `Ctx.Muted`).
- `Log` і `Say` — у батьківський стіл як є: Дядько Глек той самий. Не спам — у вечірці міні-ігор багато.
- Виняток у підгрі (Configure/Start/Act/Tick/View/Frame) → лог сервера і результат «усі рівні»; вечірка живе далі.
  `GameError` з Act — звичайна відмова (тост гравцеві), не поломка.
- Стеля: минула `PartyCapMs` (≤ 120 с), а Finish нема → `PartyScores()` → результат з `How = Cap`.
- Результат — `MinigameResult`: `Scores` (усі місця; бракує — 0), `Places` (1 — найкраще, рівні — поділене),
  `Winners`, `Log`, `Verdict`, `How` (`Finished`/`Cap`/`Crash`).

## 4. Тики
Вечірка (і `mgprobe`) має `TickMs = 20` — крок `TickEngine`, тож будь-який крок підгри (40/50/60) лягає точно.
Хост на кожен тик батька дивиться на `Ctx.Clock` і кличе `Tick()` підгри, коли настав її час; відстали — щонайбільше
`MaxCatchUp = 2` кроки за раз, далі не надолужує. `TickResult` підгри стає результатом батька (кадр/вид шлються лише
тоді, коли підгра щось сказала). Покрокова гра без `TickMs` теж годиться: тоді `Tick()` їй не кличуть, а стеля —
за годинником (хост перевіряє її на кожному тику батька).

## 5. Вид, кадр, дії
- Вид батька кладе вид підгри як є: `mg = host.View(місце_батька)` (місце підгри — `host.SubOf`; не грає → вид
  глядача). Кадр батька — `{ mg: host.Frame() }` (`Frame()` гри, а без нього — `View(null)`). Кадр бачать усі —
  нічого таємного (як і завжди).
- Дії: модуль-господар шле `act('mg', { a, p })` / `input('mg', { a, p })`, батько викликає
  `host.Act(місце_батька, a, p)` — гра бачить звичайні `action`/`payload`. Реалтайм: після Act у Playing вид не
  шлеться — лише з `TickResult.View` (як завжди).
- Save/Load: хост не зберігається. Після рестарту сервера вечірка стартує ту саму міні-гру заново (новий хост,
  той самий id і склад) — переграти чесніше, ніж зарахувати нічию.

## 6. Клієнт: `HGames.embed(host, gameId, opts)` (`web/games/core.js`)
Підвантажує модуль і css гри, створює свій root (`.gembed`) у `host`, будує ctx підгри і кличе `mount` → `update`.
`opts`: `view, frame, seat` (місце в підгрі або null), `names[]` (ім'я кожного місця: нік чи бот), `nicks[]`,
`seatNames[]`, `status` (`'playing'|'finished'`), `result` (`{winners}`), `options`, `act(a, p)`, `input(a, p)`.
Handle: `{ ready, update(o), frame(f), onKey(e), status(), pad, root, mod, ctx, unmount() }`.
- ctx підгри: `room` (вигадана шапка: `game`, `status`, `seats` з ботами, `seatNames`), `seat/mine/playing/myTurn`,
  `nameOf/nickOf/seatName`, `act/input` (через opts), `toast/esc/css/ui/me` — як у каркаса, `embedded: true`.
- **Клавіші й пад каркас вкладеному модулю не роздає** — це робить господар: `onKey(e, ctx) { return emb.onKey(e) }`,
  `get pad() { return emb ? emb.pad : null }` (handle.pad уже прив'язаний до ctx підгри: `when`, `on`).
- `status()` — рядок статусу підгри: господар може показати його своїм `status`.

Вимоги до модуля міні-гри: нічого поза `ctx` і своїм `root` — ні `location`, ні глобальний стан столу, ні
`document.querySelector` поза root, розмір міряй по `root` (не вікну); таймери/rAF/слухачі — прибрати в `unmount`
(його кличуть між міні-іграми). `ctx.room.status === 'finished'` — міні-гру зіграно (поле ще видно під таблицею).

## 7. Як тестувати
**Сервер — `PartyHarness`** (`tests/Hlechyky.Tests/Support/PartyHarness.cs`): справжній хост над фальшивим столом.
```csharp
var h = new PartyHarness("tyr", humans: 1, bots: 3, level: LiveBots.Level.Hard, seed: 5);
h.Start();                          // Configure(party=1, bots=1,2,3) → Start
h.Act(0, "shoot", new { x = 1 });   // людина — місця 0..humans−1
h.Tick(50);                         // тики батька по 20 мс (TickSub(n) — кроками гри)
var r = h.RunToEnd();               // до Finish чи стелі (+1 с); r.Scores / r.Places / r.Winners / r.How
Assert.Equal(0, h.Ctx.Muted);       // ні Award, ні Score
Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
```
Обов'язково: самі боти без людського вводу → `How == Finished` до стелі, scores на всіх місцях; людина, що грає
добре (скриптом), — вгорі; `PartyScores()` на півдорозі — усі місця; звичайна гра без змін. Зразок — `PartyTests`.

**Браузер — `mgprobe`:** `http://127.0.0.1:<порт>/#games/new/mgprobe` → «Міні-гра», «Ботів», «Рівень ботів» →
«Поставити стіл» → друзі сідають за посиланням `#games/room/<id>` → «Почати» → картка «як грати» 3 с → гра →
таблиця місць (місце, ім'я, score) → «Ще раз» (можна обрати іншу гру). Стенд **ніколи не кличе Finish**: ні
черепків, ні таблиць результатів. У лобі гри нема (`IUnlistedGame`; у каталозі `unlisted: true`).

## 8. Приклад — Крижина
`Icefloe : Game, IPartyMinigame`: `Configure` читає `PartyMode` → один раунд, без команд; `Start` садить ботів на
`bots` (рівень `botlvl`); раунд скінчився → `PartyOver()`: scores — порядок випадіння (перший шубовснув — 0, другий —
1…, хто на кризі — кількість упалих, рівні між собою), `Finish` без серії й ачівок. `OnLeave` у режимі вечірки нічого
не кінчає. Стеля 90 с (відлік 3 + раунд ≤ 75 + підсумок 3). Звичайна Крижина не змінилась.
