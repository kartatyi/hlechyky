# Дотепи (`dotepy`) — хвиля 2, специфікація

> Етап проєктування (27.09.2026). Це контракт для інженера: усе, що тут написано, реалізується як є; де інженер
> відхилиться — допише розділ «Як реалізовано» з причиною. Каркас: `docs/games/ARCHITECTURE.md`, `PROTOCOL.md`,
> `INTEGRATION-NOTES.md`, `TESTING.md`, спільні правила хвилі — `D:/or-wt/_tools/AGENT-COMMON-wave2.md`.
> Найближчі за духом ігри в коді: фази й таймери — `Impl/Skilky.cs`; чернетки через `Input` і `_left` —
> `Impl/Telephone.cs`; голос Глека, `Speak/Voiced/_pending` — `Impl/Svoya.cs` §«Голос», `SvoyaLines.cs` (`SvoyaVoice`),
> `TtsService.cs`; клієнтський плеєр голосу з тумблером — `web/games/svoya.js` (розділ «голос ведучого»).

## 1. Суть і чому це весело

Кожен отримує дурне завдання («Найгірша назва для сільського радіо») і за 90 секунд пише найсмішнішу відповідь.
Потім відповіді виходять на екран **анонімно**, решта голосує за кращу, і лише тоді розкривається, хто що написав.
Дядько Глек (edge-tts, Остап) зачитує завдання й відповіді вголос — і сама його інтонація на чужій дурниці вже
половина сміху. Коли всі голоси дістаються одному — **«Розгром!»** із подвійним бонусом. Наприкінці — «Останній дотеп»:
одне завдання на всіх, кожен роздає 🥇🥈🥉.

- **Мінімум — троє.** На двох гра не працює принципово: голосувати нема кому (кожен віддав би голос єдиному
  супернику — вічна нічия). Стіл на двох не стартує — каркас скаже «Замало гравців, треба щонайменше 3». Див. §9.
- **Утрьох-учотирьох** — режим «на всіх»: обидва завдання раунду пише кожен, і кожен голосує за чуже. Дуелі з одним
  суддею (як у класичному Quiplash на трьох) були б нудними — суддя один, «Розгром» щоразу.
- **П'ятеро й більше (до 8)** — дуелі: кожне завдання дістається двом, кожен гравець пише два; решта столу судить
  «ліве чи праве». Це найкращий склад: на 6–8 голосів ставки й «Розгроми» справжні.
- Партія на шістьох — близько 10 хвилин; є короткі варіанти (§2.1). Раунди читаються з першого екрана: пиши → голосуй →
  дивись, хто це написав.

### GameInfo

```csharp
public override GameInfo Info { get; } = new(
    "dotepy", "Дотепи", "«Дотепи»", GameGroup.Party, 3, 8,
    TickMs: 250, Start: StartMode.ByHost, Hidden: true, Private: false, Persistent: false, Rated: false,
    Score: ScoreOrder.HigherIsBetter,
    Options:
    [
        new GameOption("rounds", "Партія",
            [("full", "2 раунди + Останній дотеп"), ("short", "1 раунд + Останній дотеп"), ("blitz", "Лише Останній дотеп")], "full"),
        new GameOption("write", "Час на дотеп", [("60", "60 с"), ("90", "90 с"), ("120", "120 с")], "90"),
        new GameOption("voice", "Голос Глека", [("ostap", "Остап"), ("polina", "Поліна"), ("none", "Без голосу")], "ostap"),
    ],
    Hint: "Дурне завдання — смішна відповідь. Пишете анонімно, голосуєте за чуже, Дядько Глек усе зачитує. Троє й більше");
// Client порожній (модуль web/games/dotepy.js). SeatName(i) = (i + 1).ToString() — місць до восьми, «гравець восьмий» у чіп не влізе.
```

Клас — `Hlechyky.Games.Impl.Dotepy : Game`. Усі свої типи — з префіксом `Dotepy…` (§11).

## 2. Правила

### 2.1 Склад партії й режими раунду

| Опція `rounds` | Раунди | Орієнтовно на шістьох |
|---|---|---|
| `full` (типово) | раунд 1 → раунд 2 → Останній дотеп | ~10–11 хв |
| `short` | раунд 1 → Останній дотеп | ~6 хв |
| `blitz` | лише Останній дотеп | ~2,5 хв |

`round` у виді — порядковий номер (1..`rounds`), `rounds` = 3 / 2 / 1. Останній раунд партії — завжди «Останній дотеп»
(`final: true`).

**Режим несфінального раунду** обирається на його старті за кількістю **присутніх** (`Present`, §2.7):

| Присутніх N | `mode` | Завдань у раунді | Хто пише що | Карток на голосування |
|---|---|---|---|---|
| 5–8 | `duel` | N | завдання k → двом гравцям (§2.2); кожен пише 2 | N, по 2 відповіді |
| 3–4 | `all` | 2 | усі пишуть обидва | 2, по N відповідей |

**Останній дотеп** (фінал): 1 завдання (з `final: true`, якщо таке є в пулі), пишуть усі, картка одна з N відповідями,
голосування ранжоване (§2.4).

Тривалості (константи класу `Dotepy`):

```csharp
public const int TickMs = 250;
public const int MinSeats = 3, MaxSeats = 8;
public const int DuelFrom = 5;              // від скількох присутніх раунди 1–2 — дуелі
public const int MaxAnswer = 80;            // символів у дотепі після чистки
public const int TasksPerRound = 2;         // завдань на гравця в раундах 1–2
// час — з опції write (60/90/120 с) → WriteMs; фінал пише коротше: одне завдання
public static int FinalWriteMs(int writeMs) => writeMs * 2 / 3;      // 40 / 60 / 80 с
public const int VoteMs = 15_000;           // голосування картки (+ SpeechMs)
public const int FinalVoteMs = 30_000;      // голосування фіналу (+ SpeechMs)
public const int SpeechCapMs = 15_000;      // стеля добавки за читання картки
public const int FinalSpeechCapMs = 25_000;
public const int RevealMs = 5_000;          // розкриття картки (+ репліка-вердикт, стеля VerdictCapMs)
public const int VerdictCapMs = 4_000;
public const int FinalStepMs = 2_500;       // фінал: розкриття кожної наступної відповіді (з кінця)
public const int FinalHoldMs = 3_000;       // фінал: після останньої розкритої — пауза перед done
public const int TableMs = 6_000;           // підсумок раунду між раундами
public const int VoiceWaitMs = 3_000;       // скільки чекаємо репліку, що ще озвучується
public const double CharsPerSec = 14;       // оцінка тривалості читання без голосу (як у Svoya)
public const int RankCount = 3;             // 🥇🥈🥉 у фіналі
public static readonly int[] RankPoints = [300, 200, 100];
public const int JuryPrize = 100, FinalJuryPrize = 200;
public static int VoteValue(int round) => 100 * round;   // раунд 1 — 100 за голос, раунд 2 — 200
public static int SweepBonus(int round) => 2 * VoteValue(round);
public const int SeenRing = 300;            // пам'ять процесу: скільки останніх завдань не повторювати
```

### 2.2 Роздача завдань (усе — з `Ctx.Rng`, детерміновано за сідом)

- Пул завдань: `DotepyBank.All` (§7), у випадковому порядку (Фішер — Єйтс на `Ctx.Rng`), без тих, що вже грали за цим
  столом (`_used`, живе в екземплярі гри, тож переживає «Ще раз») і без останніх `SeenRing` id, зіграних на цьому
  сервері (`DotepySeen` — статичний кільцевий буфер під `lock`, не персистентний). Пулу бракує → повертаємо спершу
  «бачені на сервері», потім `_used`, і лише тоді дозволяємо повтор. Порожній банк → `Finish([], "Дотепи: банк завдань
  не знайшовся, партії не буде")`.
- Для фіналу пул фільтрується за `final: true`; якщо таких немає — беремо будь-яке.
- `duel`: `order` — присутні місця, перетасовані `Rng`; завдання `k` (0..N−1) дістається `order[k]` та
  `order[(k + off) % N]`, де `off = 1` у раунді 1 і `off = 2` у раунді 2 (при N ≥ 5 зсув 2 не повторює пар зсуву 1;
  для `short` є лише раунд 1). Так кожен має рівно два завдання і жодне завдання не пише сам із собою.
- Порядок відповідей у картці — перетасований `Rng` (щоб «перша» не була завжди меншим місцем). Порядок карток у
  раунді — порядок завдань `k`.
- У режимі `all` та у фіналі завдання спільні для всіх, порядок відповідей — теж `Rng`.

### 2.3 Фаза `write`

- `endsAt = now + WriteMs` (фінал — `FinalWriteMs`). Гравець бачить лише **свої** завдання (`me.tasks`), решта —
  галочки «здав» (`players[].ready`) і список завдань раунду `prompts` (для глядачів і тих, хто вже здав).
- Чернетка: `Input('draft', { i, text })` — зберігається в завданні (без розсилки видів; на F5 автор побачить свій
  текст). Здати: `Act('answer', { i, text })` → `done = true`, `_dirty`. Забрати назад: `Act('edit', { i })`.
- Чистка тексту (`Dotepy.Clean`): прибрати керівні символи, стиснути пробіли, обрізати краї; довше за `MaxAnswer` —
  відмова (клієнт має `maxlength`, тож це лише захист).
- Фаза закінчується, коли **всі присутні здали всі свої завдання** (перевірка в `Tick`, на найближчому тику) або
  вийшов час. Незданому завданню дістається чернетка (якщо після чистки не порожня), інакше — **підставна відповідь**
  зі `DotepyStock` (12 рядків, §2.6), позначена `stock: true`. Підставна бере участь у голосуванні як звичайна
  (люди люблять голосувати за «Мій кіт сів на клавіатуру»), але **очок авторові не дає**.
- Картка, у якій **усі** відповіді підставні (обидва автори пішли), пропускається без голосування.

### 2.4 Фази `vote` і `reveal` (картка за карткою)

- **Хто голосує** (`voters`): присутні, що не є авторами картки. Якщо таких нема (режим `all`, фінал — усі автори) —
  голосують усі присутні, але **за себе не можна**. У дуелі автори не голосують узагалі (їм у виді `me.voter = false`).
- **Скільки голосів** (`perVoter`): у раундах 1–2 — 1; у фіналі — `min(RankCount, answers.Length − 1)`, голоси
  **ранжовані** (`ranked: true`): перший обраний — 🥇, другий — 🥈, третій — 🥉. На трьох у фіналі це 🥇🥈, на
  чотирьох — усі три, і це не безглуздо: порядок дає різні очки.
- Дія `Act('vote', { card, picks })`; `picks` — масив індексів відповідей, довжина 1..`perVoter`, без повторів, без
  своїх. Повторний `vote` **замінює** попередній. У фіналі можна надіслати неповний список (лише 🥇) — очки йдуть за те,
  що є.
- Час: `endsAt = now + VoteMs + SpeechMs`, де `SpeechMs` — тривалість кліпа-читання картки (є голос і кліп готовий),
  інакше оцінка `Math.Min(cap, text.Length / CharsPerSec * 1000)`; стеля `SpeechCapMs` (фінал: `FinalVoteMs`,
  `FinalSpeechCapMs`). Голосувати можна одразу, не чекаючи, поки Глек дочитає. Фаза закінчується раніше, коли
  проголосували всі `voters`.
- **Голос публіки (глядачі).** Глядач не сидить за столом, а `Rooms.Act` не пускає нікого без місця, тому глядачі
  голосують через HTTP: `POST /api/games/dotepy/jury` (§3.2). Один голос на нік на картку (повторний замінює), лише за
  одну відповідь, лише поки триває `vote`. У виді видно лише лічильник `card.juryVotes`; чиї — ні.
- **Розкриття** (`reveal`): рахуються очки (§2.5), відкриваються автори, голоси й очки кожної відповіді, `players[].score`
  оновлюється. Триває `RevealMs + min(тривалість вердикту, VerdictCapMs)`. Далі — наступна картка, а після останньої
  картки раунду — `table` (`TableMs`), потім наступний раунд.
- **Фінал розкривається поступово:** `card.shown` росте від 0 до `answers.Length` кожні `FinalStepMs`, відповіді
  відкриваються **від найгіршої до найкращої** (порядок: очки ↑, кількість голосів ↑, місце ↑), і очки кожної
  додаються в `score` **у момент її розкриття** (щоб таблиця гравців не спойлерила переможця). Після останньої —
  `FinalHoldMs`, тоді `done`.

### 2.5 Очки

| Що | Раунд 1 | Раунд 2 | Останній дотеп |
|---|---|---|---|
| Голос гравця | +100 | +200 | 🥇 300 · 🥈 200 · 🥉 100 від кожного, хто голосував |
| «Розгром!» — усі `voters` (їх ≥ 2) віддали голос одній відповіді | +200 | +400 | нема |
| Приз публіки — відповідь, за яку **строго найбільше** голосів глядачів (≥ 1) | +100 | +100 | +200 |
| Підставна відповідь (`stock`) | голоси рахуються й показуються, очок 0, «Розгром» не дає бонусу, приз публіки не дає | | |

- `VoteValue(round)` — за номером несфінального раунду (у `short` єдиний раунд — 100).
- Нічия в дуелі (1:1, 2:2) — обом за своїми голосами, без бонусу. Порівну голосів публіки — приз нікому.
- «Розгром» — ще й ачівка: `Ctx.Award(seat, 0, "ach:dotepy-sweep")` авторові (не за підставну).
- Наприкінці всім присутнім (і тим, хто встав, — ні) `Ctx.Score(seat, score)`; переможці — найбільший рахунок серед
  присутніх (кількох — усі); усі по нулях — нічия `Finish([])`. Переможцям за столом, де на старті партії було ≥ 5
  гравців, — `Ctx.Award(seat, 0, "ach:dotepy-king")`.
- Рядок Журналу: `Дотепи: Оля 3400, Петро 2100, Ганна 900` (від більшого; `— розгромів: 2`, якщо були; ` — нічия`, якщо
  переможців нема). `Ctx.Say` **не кличемо взагалі** (урок «Скільки?» 23.09: вердикти живуть на картці).

Приклад: шестеро, раунд 2, дуель, 4 голосуючих, усі за відповідь Б → Б: 4 × 200 + 400 = **1200**, А: 0. Фінал на
шістьох: у кожного 5 голосуючих; максимум за 🥇 від усіх — 1500. Орієнтир суми переможця в `full` на шістьох —
3000–5000.

### 2.6 Підставні відповіді (`DotepyStock`, у коді, `Rng` без повтору в межах картки)

`Ой, мовчу.` · `Тут мала бути шедевральна відповідь.` · `Мій кіт сів на клавіатуру.` · `Нема слів. Узагалі.` ·
`Пас. Наступне питання.` · `…` · `Я це знав, але забув.` · `Спитайте в Глека.` · `Так. Це відповідь.` ·
`Тиша. Гучна.` · `Пишу-пишу… не встиг.` · `Це секрет.`

### 2.7 Вихід посеред партії, F5, «Ще раз», один гравець

- `Present(seat)` = `Ctx.Seated(seat) && !_left[seat]` (`_left` — `bool[MaxSeats]`, §5). Ніки й місця захоплюються у
  `Start()` в `_nicks[8]` — після виходу `NickOf` уже null, а таблиці ім'я треба.
- `OnLeave(seat)`: `_left[seat] = true`; у `write` його незакінчені завдання на дедлайні стануть чернеткою/підставними; у `vote`
  його забирають із `voters` (уже віддані голоси лишаються), і фаза може закритись достроково. Його рахунок лишається в
  таблиці, але переможцем він не буде і `Ctx.Score` не отримує. **Присутніх менше трьох → партія закінчується одразу:**
  `Finish(лідери серед присутніх з рахунком > 0, "Дотепи: гравці розійшлись — попереду {ніки лідерів}")`, усі по нулях —
  `Finish([], "Дотепи: гравці розійшлись, партію не дограли")`. (У «Скільки?» дограють і самі; тут з двома голосувати нема
  кому.)
- F5 — каркас тримає місце 20 с; стан на сервері, чернетка в `me.tasks[].text`, голоси в `me.picks`. Клієнту нічого
  зберігати локально, крім тумблера голосу й свого голосу публіки.
- «Ще раз» — каркас обертає місця, кличе `Start()`: усе обнуляється, `_used` лишається (нові завдання), голос готує
  репліки з новими ніками на нових місцях.
- Один гравець — неможливо (`MinPlayers = 3`, і §2.7 закриває партію на двох).
- `ActsInLobby = false`, `CanStart() = null`, `TalkBlock` — типовий (балачка столу вільна; «це моє!» — справа честі).

## 3. Дії та payload

### 3.1 Через хаб (лише ті, хто сидить)

| Дія | Вхід | Payload | Фаза | Перевірки → відмова (текст лише тому, хто натиснув) |
|---|---|---|---|---|
| `draft` | `Input` | `{ i: number, text: string }` | `write` | мовчки, як усі `Input`: `i` — своє завдання, не здане; текст чиститься й ріжеться до 80 |
| `answer` | `Act` | `{ i: number, text: string }` | `write` | не `write` → «Зараз не пишуть»; `i` не моє → «Це не твоє завдання»; порожньо після чистки → «Порожній дотеп — то ще не дотеп»; > 80 → «Задовго: до 80 знаків». Успіх — `ActResult.Done` (без тоста; здане вже здане — текст замінюється) |
| `edit` | `Act` | `{ i: number }` | `write` | не `write` → «Зараз не пишуть»; не здано → «Ще не здано» |
| `vote` | `Act` | `{ card: number, picks: number[] }` | `vote` | не `vote` → «Зараз не голосують»; `card != поточна` → «Ця картка вже пішла»; не в `voters` (автор дуелі) → «Це твій дотеп — за нього голосують інші»; `picks` не масив цілих / порожній → «Обери хоч один»; довше за `perVoter` → «Забагато голосів»; повтори → «Один дотеп — один голос»; індекс поза межами → «Такої відповіді нема»; свій → «За себе не голосують». Успіх — `Done` |
| інше | | | | «Тут так не ходять»; після `done` — «Партію зіграно, тисни «Ще раз»» |

Реалтайм-кімната: `Rooms.Act` видів не шле — усе доїжджає найближчим тиком (≤ 250 мс), тому клієнт підсвічує свій
вибір локально одразу, а сервер підтверджує видом. `Views.Payload(new { i = 0, text = "…" })` і
`new { card = 0, picks = new[] { 1 } }` — рівно те, що шле модуль (тест §8).

### 3.2 Голос публіки — HTTP (глядачі)

`POST /api/games/dotepy/jury`, JSON `{ room: string, card: number, pick: number }`, заголовок `X-Nick` (як у `api()`
з `web/app.js`; сервер бере нік через `Auth.Nick(c)`). Відповідь завжди 200: `{ ok: true, message: "Голос публіки
прийнято" }` або `{ ok: false, message }`. Мапиться в `DotepySetup.MapDotepy(app)`, сервіс `DotepyJury` — у
`DotepySetup.AddDotepy(services)` (по одному рядку в `GamesSetup`, §11).

Порядок перевірок у `DotepyJury.Vote(nick, roomId, card, pick, Rooms, Presence)`:
1. Безіменний гість (`nick == Auth.Guest`, тобто «гість» без імені) → «Спершу скажи, як тебе кликати»; гість з ім'ям
   («гість Оля») голосує як усі.
2. `rooms.Find(room)` немає або `room.Game is not Dotepy` → «Такого столу вже нема».
3. `room.Has(nick)` → «Ти за столом — голосуй на картці».
4. Жодне з `presence.ConnectionsOf(nick)` не в `room.Watchers` → «Спершу відкрий цей стіл».
5. Частіше за 2 рази на секунду з одного ніка (словник у `DotepyJury`) → «Не так швидко».
6. `lock (room.Sync) { reply = game.JuryVote(Auth.NickKey(nick), card, pick); }` — гра перевіряє фазу («Зараз не
   голосують»), картку («Ця картка вже пішла»), індекс («Такої відповіді нема»); зберігає `_jury[card][nickKey] = pick`,
   ставить `_dirty`. **`JuryVote` не кличе жодного `Ctx.*`** — поза `Collect` контекст мовчить; види розішле наступний тик.
   Замок береться лише `room.Sync` (без `Rooms._lock`) — той самий порядок вкладення, що в `Rooms`, дедлоку нема.

## 4. Вид

`Hidden: true`. Кадрів немає: `Frame()` повертає `null`, `Tick` ніколи не просить кадр (§5). Види летять лише коли щось
змінилось.

```ts
type Say = { id: number; text: string; url: string|null; seconds: number };   // id росте на кожну нову репліку
type Best = { prompt: string; text: string; seat: number; points: number };

type DotepyView = {
  phase: 'write'|'vote'|'reveal'|'table'|'done';
  round: number; rounds: number; final: boolean;
  mode: 'duel'|'all';                 // у фіналі завжди 'all'
  endsAt: string|null;                // ISO; null — таймера ще нема (waiting)
  totalMs: number;                    // повна тривалість фази для дуги
  waiting: boolean;                   // чекаємо на репліку Глека (≤ VoiceWaitMs)
  voice: 'ostap'|'polina'|'none';
  players: { seat: number; nick: string; score: number; ready: boolean; voted: boolean; left: boolean }[];  // лише зайняті на старті місця
  prompts: string[];                  // завдання поточного раунду (у write — щоб глядачі й ті, хто здав, бачили)
  me: null | {                        // глядач — null
    tasks: { i: number; prompt: string; text: string; done: boolean }[];   // у write; інакше []
    mine: number[];                   // індекси моїх відповідей у поточній картці
    voter: boolean;                   // чи маю голос у поточній картці
    picks: number[];                  // мій вибір (порожній — ще ні)
  };
  card: null | {
    i: number; of: number; prompt: string;
    answers: { text: string; stock: boolean; seat: number|null; votes: number[]|null; jury: number|null; points: number|null; rank: number|null }[];
    voters: number[]; voted: number[]; juryVotes: number; perVoter: number; ranked: boolean;
    sweep: number|null;               // reveal: індекс відповіді з «Розгромом»
    shown: number;                    // reveal фіналу: скільки розкрито (з кінця); у раундах = answers.length
  };
  say: Say|null;                      // поточна репліка Глека (вступ раунду, читання картки, вердикт, підсумок)
  table: null | { rows: { seat: number; score: number; delta: number }[]; best: Best|null };   // лише у table
  result: null | { winners: number[]; scores: number[]; best: Best[] };   // лише у done; best — 3 найдотепніші партії
};
```

Що приховано від кого (тести обов'язкові):
- `write`: `me.tasks` — лише свої завдання й чернетки; чужих чернеток немає ні в чиєму виді, ні у виді глядача.
- `vote`: у кожній відповіді `seat: null, votes: null, jury: null, points: null, rank: null` — для **всіх** місць і глядача;
  чужі `picks` ніде (є лише `voted`). Автор дуелі бачить `me.mine` (щоб клієнт підписав «твій»), і більше нічого.
- `reveal`/`table`/`done`: усе відкрито. `players[].score` у фіналі росте разом із `shown`.
- Глядач (`seat == null`): `me = null`; решта та сама, що у гравців.

Приклад (п'ятеро, раунд 1, `duel`, картка 0, фаза `vote`, вид місця 2 — воно голосує):

```json
{ "phase": "vote", "round": 1, "rounds": 3, "final": false, "mode": "duel",
  "endsAt": "2026-09-27T18:04:31.250+00:00", "totalMs": 21400, "waiting": false, "voice": "ostap",
  "players": [
    { "seat": 0, "nick": "Оля", "score": 0, "ready": true, "voted": false, "left": false },
    { "seat": 1, "nick": "Петро", "score": 0, "ready": true, "voted": false, "left": false },
    { "seat": 2, "nick": "Ганна", "score": 0, "ready": true, "voted": false, "left": false },
    { "seat": 3, "nick": "Іван", "score": 0, "ready": true, "voted": true, "left": false },
    { "seat": 4, "nick": "Марта", "score": 0, "ready": true, "voted": false, "left": false } ],
  "prompts": ["Найгірша назва для сільського радіо", "Що насправді шепоче кіт, коли дивиться на тебе о третій ночі", "…", "…", "…"],
  "me": { "tasks": [], "mine": [], "voter": true, "picks": [] },
  "card": { "i": 0, "of": 5, "prompt": "Найгірша назва для сільського радіо",
    "answers": [
      { "text": "Радіо «Куряча сліпота»", "stock": false, "seat": null, "votes": null, "jury": null, "points": null, "rank": null },
      { "text": "Хвиля «Дощ у сараї FM»", "stock": false, "seat": null, "votes": null, "jury": null, "points": null, "rank": null } ],
    "voters": [2, 3, 4], "voted": [3], "juryVotes": 1, "perVoter": 1, "ranked": false, "sweep": null, "shown": 2 },
  "say": { "id": 4, "text": "Найгірша назва для сільського радіо. Перша: Радіо «Куряча сліпота». Друга: Хвиля «Дощ у сараї FM».",
           "url": "/api/games/svoya/tts/3f2a…c9.mp3", "seconds": 6.4 },
  "table": null, "result": null }
```

Розмір: дуель на п'ятьох — ≈ 1,1 КБ; фінал на восьми в `reveal` (8 відповідей по 80 знаків, голоси, автори) — ≈ 2,3 КБ.
Тест: серіалізований вид фіналу на восьми < 4096 байт.

## 5. Модель і мережа

- Фізики нема. Одиниці — мілісекунди `Ctx.Clock`, усі дедлайни — `DateTimeOffset` у полях гри, у виді — ISO. Час тика
  250 мс: фази міняє **лише `Tick`** (як у «Скільки?»); `Act` лише міняє стан і ставить `_dirty`.
- `Tick()`:
  1. `done` → `None`.
  2. `waiting` (репліка ще озвучується): кліп готовий або `now ≥ _pendingUntil` → відкрити фазу насправді
     (`say`, `endsAt`), `_dirty`.
  3. `write`: усі присутні здали або `now ≥ endsAt` → `CloseWriting()` (чернетки/підставні, картки, черга голосу) →
     `OpenCard(0)`.
  4. `vote`: усі `voters` проголосували (і `voters.Count > 0`) або час → `Reveal()`.
  5. `reveal`: раунд — час вийшов → наступна картка або `Table()`; фінал — `shown < K && now ≥ _stepAt` → `shown++`,
     очки цієї відповіді в `score`, `_stepAt += FinalStepMs`; `shown == K && now ≥ endsAt` → `Done()`.
  6. `table`: час → наступний раунд (`OpenRound`).
  7. Повернути `_dirty ? ViewOnly : None`, де `static readonly TickResult ViewOnly = new(false, true)`; `_dirty = false`.
- Детермінізм: уся випадковість — `Ctx.Rng` (тасування пулу, `order`, порядок відповідей, підставні, репліки з пулів),
  час — `Ctx.Clock`. Той самий сід + та сама послідовність дій → побітово той самий JSON видів (тест).
- Мережа: передбачення не потрібне — гра покрокова з таймерами. Клієнт:
  - чернетку шле `Input('draft')` не частіше ніж раз на 350 мс і лише коли текст змінився; `answer` — на Enter/кнопку;
  - свій `vote` підсвічує одразу (оптимістично), відкочує, якщо `RoomReply.ok == false`;
  - усі таймери — з `endsAt`/`totalMs` через `ui.timerArc` (розсинхрон годинника — як у «Скільки?», сервер суддя);
  - лаг/втрата: види йдуть повними знімками, будь-який наступний вид відновлює екран цілком (без дельт). Реконект —
    каркас підписує знову, приходить свіжий вид.
- Бюджет тика: середній `Tick()` на восьми ≤ 0,05 мс (мета), обов'язково ≤ 0,25 мс; у гарячому шляху нема LINQ і нових
  колекцій (масиви `_ready[8]`, `_left` як `bool[8]`, картки — заздалегідь зібрані списки; `View()` алокує, але він
  кличеться лише на `_dirty`, ≤ 4 рази на секунду).

## 6. Голос Глека

- `IDotepyVoice { bool Enabled; void Prepare(string voice, IEnumerable<string> texts, bool urgent = false); DotepyClip? Ready(string voice, string text); }`
  з `DotepyClip(string Url, double Seconds)`; бойова реалізація `DotepyVoice(TtsService tts)` — `tts.Enqueue` /
  `tts.TryGet`, адреса кліпа `SvoyaVoice.UrlPrefix + hash + ".mp3"` (той самий кеш `cache/tts` і той самий уже
  змаплений ендпоінт `/api/games/svoya/tts/{name}` — нового не треба). У DI її ставить `DotepySetup.AddDotepy`
  (`services.AddSingleton<IDotepyVoice, DotepyVoice>()`; `TtsService` уже зареєстрований `SvoyaSetup.AddSvoya`).
  Гра бере голос у `Start()`: `Ctx.Services.GetService<IDotepyVoice>() ?? DotepyNoVoice.Instance`; опція
  `voice = none` або `!Enabled` — теж `DotepyNoVoice`. У тестах — `RoomHarness.WithService<IDotepyVoice>(new
  FakeDotepyVoice(...))`, без жодних `internal`.
- Голос edge-tts: `ostap` → `uk-UA-OstapNeural`, `polina` → `uk-UA-PolinaNeural` (мапа `TtsService.Voice`).
- **Що озвучується** (`DotepyLines`; нік завжди в називному, тільки підметом або після тире):
  - вступи раундів (чисті, у `Start()` не терміново): «Раунд перший. Пишіть дотепи!», «Раунд другий. Кожен голос —
    подвійний!», «Останній дотеп! Одне завдання — на всіх, у кожного три голоси.»;
  - **картка** — один кліп на картку: `«{завдання}. Перша: {а}. Друга: {б}.»` (порядкові до восьмої: перша, друга,
    третя, четверта, п'ята, шоста, сьома, восьма); у чергу — на `CloseWriting()`, перша картка `urgent: true`;
  - вердикти з ніком (готуються у `Start()` для кожного ніка за столом, не терміново; 4 шаблони × N ≤ 32 кліпи):
    `Win` «Картку забирає {nick}.», `Sweep` «Розгром! Усі голоси — {nick}!», `FinalWin` «Останній дотеп забирає {nick}!»,
    `GameWin` «Перемагає {nick}!»;
  - чисті вердикти: `Tie` «Порівну. Публіка розділилась.», `Silence` «Ніхто не проголосував. Буває.», `StockWin`
    «Публіка обрала мовчання. Очок за це не дають.», `GameTie` «Нагорі нічия. Дотепні всі!».
  - Вибір: розкриття картки раунду — підставна перемогла → `StockWin`; голосів нема → `Silence`; «Розгром» → `Sweep`;
    один найкращий → `Win`; інакше `Tie`. Фінал — `FinalWin` для 🥇 за очками (нічия → `Tie`). `done` — `GameWin`/`GameTie`.
    Вердикт озвучується `wait: false` (репліка як є, хай навіть без url).
- Текст для TTS чиститься `DotepyVoice.Clean`: лишаються літери, цифри, пробіли й `.,!?…'’-:;()«»`, решта (емодзі)
  прибирається; порожнє → «без слів». На екрані — текст як набрано.
- **Очікування:** відкриваючи картку з увімкненим голосом, гра питає `Ready`; кліпа нема → `waiting: true`,
  `endsAt: null`, `Prepare([text], urgent: true)`, `_pendingUntil = now + VoiceWaitMs`. Готовий або минуло 3 с →
  `say` (з `url` або без), `endsAt` рахується з `SpeechMs` за кліпом або за оцінкою. Опція `none` чи `!Enabled` —
  **жодного очікування ніде**, `say.url = null`, гра йде текстом.
- **Клієнт** (за `svoya.js`): тумблер «🔊 Глек тут» (localStorage `dotepySpeaker`; типово ввімкнено у глядача й
  господаря столу, вимкнено в решти — щоб не лунало з восьми телефонів); `<audio class="dt-voice">`; кожен `say.id`
  грається раз; радіо на час репліки глушиться (`#audio.muted`, як `duck` у Svoya); `url == null` → `speechSynthesis`
  українським голосом (`rate 1.5`) або тиша. У `reveal` вердикт стартує із затримкою 1200 мс — щоб голоси встигли
  «прилетіти». Після F5 стара репліка не повторюється (порівняння `say.id` із збереженим у стані модуля).

## 7. Дані й контент

- `data/dotepy/prompts.json` — формат із `D:/or-wt/_wave2/CONTENT-FORMATS.md` (`{ version, prompts: [{ id, text, tags,
  final }] }`). Читає `DotepyBank` (за зразком `SkilkyBank`): кеш на процес, **ніколи не кидає**: нема файла / кривий
  JSON → порожній банк і попередження в лог; записи без `id`/`text`, з `text` довшим за 120 знаків або з повторним `id`
  мовчки відкидаються. Шлях — `Paths.Resolve("data/dotepy/prompts.json")`.
- Стартовий банк (36 завдань, 12 із них `final: true`) — §10; на етапі виправлень інтегратор підкладе ≈ 400 від
  авторів у тому ж форматі. Ліцензія — власний текст; без політики, війни, релігії, національностей, хвороб, реальних
  приватних осіб; легка пікантність можлива, пошлість — ні.
- `.gitignore`: дописати `!data/dotepy/` (за зразком `!data/questions/`).
- Ачівки (`Economy/Achievements.cs`, блоком у кінці каталогу):
  ```csharp
  // хвиля 2: dotepy
  new("dotepy-sweep", "Розгром",         "Усі голоси столу — за твій дотеп", "💥", 15),
  new("dotepy-king",  "Король дотепів",  "Виграв «Дотепи» за столом на п'ятьох і більше", "👑", 25),
  ```

## 8. Клієнт (`web/games/dotepy.js` + `dotepy.css`, усі класи й змінні з префіксом `dt-`/`--dt-`)

### 8.1 Розкладка

Корінь `.dt` (`container-type: inline-size`, `max-width: 920px`, центрований; на 375 — на всю ширину без
горизонтального скролу). Три поверхи:

1. **`.dt-top`** — пілюля раунду («Раунд 1 із 3» / «Останній дотеп»), дуга `ui.timerArc` (ховається при
   `waiting` — замість неї «Глек прокашлюється…»), кнопка `.dt-spk` «🔊 Глек тут / 🔇», стрічка гравців
   `.dt-players`: чіп на кожного — кольорова крапка `--dt-p<seat>` + номер місця + нік (обрізаний до 10 знаків, повний
   у `title`) + рахунок; позначки ✍ (пише), ✓ (здав / проголосував), 🚪 (встав). На 375 чіпи переносяться на два рядки.
2. **`.dt-stage`** — фаза:
   - `write`: по картці на завдання (`.dt-task`): завдання великим (`.dt-prompt`, 1.15em на телефоні, 1.35em від 560 px),
     `<input class="dt-in" maxlength="80" autocomplete="off">`, лічильник `.dt-cnt` «23/80», кнопка «Здати» (`.dt-send`,
     ≥ 44 px). Здане — картка в стані `.done`: текст, «✓ Здано · Змінити». Під завданнями — `<details class="dt-peek">`
     «На що пишуть інші» зі списком `prompts` (для глядачів розгорнутий, для гравців — після «Здано»).
   - `vote`: `.dt-prompt.big`; сітка `.dt-answers`: дуель — дві колонки від 560 px (одна на телефоні), `all`/фінал —
     `repeat(auto-fill, minmax(260px, 1fr))`, у фіналі на восьми — дві колонки по чотири. Кожна відповідь —
     `<button class="dt-ans">` із номером `.dt-n` (1..K) і текстом; стани `.mine` (disabled, підпис «твій дотеп»),
     `.picked` (✓ або бейдж 🥇🥈🥉), `.off` (не голосую). Знизу `.dt-voted` «проголосували: ✓ Іван · ✓ Марта» і
     `.dt-jury` «👀 публіка: 3». Глядач бачить ті самі кнопки й голосує як публіка (§3.2), свій вибір підсвічується
     локально.
   - `reveal`: ті самі картки; голоси прилітають чіпами `.dt-vote` (анімація `dt-fly` 400 мс, зсув 80 мс на голос),
     очки `.dt-pts` «+300» вистрибують (`dt-pop`, через 1000 мс), автор `.dt-author` виїжджає знизу в кольорі місця
     (через 1600 мс), «Розгром!» — стрічка `.dt-sweep` на картці з тремтінням (`dt-shake`) і 24 частинками-емодзі
     (`.dt-confetti span`, CSS-анімація, без JS-циклу), приз публіки — бейдж `.dt-prize` «👀 +100». У фіналі картки
     стоять «сорочкою вгору» (`.dt-ans.hidden`), розкриваються фліпом (`dt-flip` 500 мс) з кінця за `card.shown`; над
     сіткою — п'єдестал `.dt-podium`, що добудовується.
   - `table`: смужки рахунку (`.dt-bar`, ширина — частка від лідера, анімується 600 мс), дельта раунду «+500», картка
     «Дотеп раунду» (`table.best`).
   - `done`: п'єдестал (1-е місце більше), «Найдотепніше партії» — три картки `result.best` з авторами, підказка
     «Ще раз» — кнопка каркаса.
3. **Статус** — `status(ctx)`: `write` — «Пиши дотепи · здано 1 з 2» / «Здано! Чекаємо решту (3 з 6)» / глядачу
   «Пишуть дотепи…»; `vote` — «Голосуй за найдотепніше» / «Голос є — чекаємо решту» / авторові «Твій дотеп у грі — тримай
   кулаки» / глядачу «Голосуй як публіка 👀»; `reveal` — «Розкриття…»; `table` — «Раунд 1 позаду»; `done` — порожньо
   (каркас напише «Перемога: …»).

Екрани: **375×812** — одна колонка, все ≥ 44 px, клавіатура телефону не закриває завдання (поле під ним); **1280** і
**Full HD 1920×1080** — сцена ≤ 920 px, у висоту вміщується без прокрутки (фінал на восьми: 2×4 картки по ~70 px);
**Steam Deck 1280×800** — те саме, поля вводу відкривають екранну клавіатуру шару пада сама (`pad.js`, режим
«клавіатура»). `prefers-reduced-motion` — без польотів, тремтіння й конфеті, стани міняються миттєво.

Кольори місць — 8 змінних у `:root` `dotepy.css` (з фолбеками через `ctx.css`): `--dt-p0 #f4c542`, `--dt-p1 #6bbf6b`,
`--dt-p2 #e0704a`, `--dt-p3 #6aa9e8`, `--dt-p4 #c77dd6`, `--dt-p5 #e86aa0`, `--dt-p6 #4fc2c2`, `--dt-p7 #b8b8b8`;
`seatClass: ['dt0', …, 'dt7']`, класи `.gseat.dt0…dt7` — за зразком `.gseat.x/.o/.c/.d` з `core.css`. Колір ніколи не
єдина ознака: поруч завжди номер місця (`SeatName`) і нік.

### 8.2 Керування

| Пристрій | `write` | `vote` |
|---|---|---|
| Мишка / палець | клік у поле, «Здати», «Змінити» | тап по картці = голос (фінал: перший тап 🥇, другий 🥈, третій 🥉; тап по обраній — зняти) |
| Клавіатура | `Enter` у полі — здати; `Tab` — наступне завдання; `Escape` каркас не перехоплює (поле активне) | `1`–`8` (`e.code Digit1…Digit8`) — обрати відповідь № (фінал — по черзі рангами), `Backspace` — зняти останній; лише коли фокус не в полі |
| Джойстик | кільце фокуса шару пада ходить по полях і кнопках, Ⓐ відкриває екранну клавіатуру; `pad: { hint: '{dpad} по дотепах · {a} обрати', when: (ctx) => ctx.mine && ctx.playing }` — без `dirs`, стік лишається навігацією | те саме кільце по `.dt-ans`, Ⓐ = голос |

`onKey` повертає `true` лише на цифри й `Backspace` у `vote` (щоб каркас не перехоплював). У фіналі кожен тап шле
`vote { card, picks }` з поточним списком — сервер приймає неповний.

### 8.3 Малювання й швидкодія

Канваса нема: це картки з текстом, DOM оновлюється **лише на подію `room`** (≤ 4 на секунду), і лише той поверх, чий
підпис змінився (`dataset.sig` = JSON потрібних полів, як у `skilky.js`). Анімації — CSS (`@keyframes`), запускаються
класами; жодного `setInterval`/rAF, крім дуги каркаса. Заміряти в headless Chrome середній час `update()` за 300 подій
`room` на восьми гравцях у `reveal` — **< 4 мс**; число в звіт. Вкладка схована — `update` лишень оновлює стан і виходить,
малює на `visibilitychange`.

### 8.4 Звук (необов'язково, лише WebAudio-синтез, після жесту, тихо)

«тук» 880 Гц 40 мс на власний голос; «поп» на кожен прилітаючий голос у `reveal`; тризвук 120 мс на «Розгром». Вмикається
тим самим тумблером, що й голос Глека.

### 8.5 `news`, `icon`, реєстрація

```js
HGames.register({
  id: 'dotepy',
  icon: '<svg class="gico" viewBox="0 0 16 16" aria-hidden="true">'
    + '<path d="M2.5 2.5h11a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H7l-3 3v-3H2.5a1 1 0 0 1-1-1v-6a1 1 0 0 1 1-1z" fill="var(--accent)"/>'
    + '<path d="M4.5 5.2l1.4 2.2 1.4-2.2 1.4 2.2 1.4-2.2 1.4 2.2" fill="none" stroke="var(--accent-ink)" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"/>'
    + '</svg>',
  seatNames: (i) => String(i + 1),
  seatClass: ['dt0', 'dt1', 'dt2', 'dt3', 'dt4', 'dt5', 'dt6', 'dt7'],
  news: {
    v: '2026-09-27',
    title: 'Нова гра: Дотепи',
    items: [
      '✍ Кожному — по два дурних завдання. Пиши найсмішнішу відповідь, 90 секунд',
      '🗳 Далі всі голосують за чужі дотепи — анонімно. Голос = 100 очок, у другому раунді — 200',
      '💥 Усі голоси за одного — «Розгром!» і подвійний бонус',
      '🏁 «Останній дотеп»: одне завдання на всіх, роздаєш 🥇🥈🥉',
      '🔊 Дядько Глек зачитує завдання й відповіді — тумблер «Глек тут» на картці; глядачі голосують як публіка',
    ],
  },
  pad: { hint: '{dpad} по дотепах · {a} обрати', when: (ctx) => ctx.mine && ctx.playing },
  mount, update, unmount, onKey, status,
});
```

Лобі до старту: тіло картки — три рядки «Як грати» (пиши → голосуй → дивись, хто це написав) і підказка «троє й
більше». Каркас сам малює опції в попапі створення й підписує нетипові в шапці.

## 9. План тестів (`tests/Hlechyky.Tests/Games/DotepyTests.cs`, xUnit, ≥ 45)

Підмостки: `Table(players, seed, options, voice)` → `RoomHarness("dotepy", options, seed, services)`, `Join` ніків,
`Start()`. Голос — `FakeDotepyVoice : IDotepyVoice` у тестах (`Enabled` true/false, записує всі `Prepare` з прапорцем
`urgent`, `Ready` віддає кліп одразу, після N викликів або ніколи; `Seconds` = `text.Length / 14`), підкладається через
`RoomHarness.WithService<IDotepyVoice>(fake)`; без сервісу гра йде на `DotepyNoVoice` (§6). Боти: `Answer(h, seat, i,
text)` і `Vote(h, seat, picks)`; `Until(h, phase)` — тикати до фази (стеля 2000 тиків); `Phase(h)`, `Score(h, seat)`,
`Card(h)` — читання з `h.View(null)`.

Банк і каталог:
1. `Bank_loads_prompts_json_and_skips_broken_records` — запис без `text`, задовгий і з повторним `id` відкинуто, решта є.
2. `Bank_missing_or_invalid_file_gives_empty_bank_not_exception`.
3. `Starter_bank_has_at_least_30_prompts_and_10_finals_with_unique_ids`.
4. `Catalog_lists_dotepy_as_party_byHost_hidden_3_to_8_with_css_and_three_options`.
5. `A_table_of_two_cannot_start` — «Замало гравців, треба щонайменше 3».

Роздача:
6. `Five_players_get_duels_two_tasks_each_and_two_authors_per_prompt`.
7. `Three_and_four_players_all_write_the_same_two_prompts` (`mode == "all"`).
8. `Round_two_pairs_never_repeat_round_one_pairs_at_five_to_eight`.
9. `Prompts_do_not_repeat_within_a_match_nor_after_rematch`.
10. `Final_takes_a_final_flagged_prompt_when_the_bank_has_one`.
11. `Short_has_one_round_and_final_and_blitz_only_the_final`.
12. `Empty_bank_finishes_the_match_at_start_with_a_draw_and_a_journal_line`.

Написання:
13. `Answer_is_cleaned_stored_and_visible_only_to_its_author`.
14. `Empty_and_too_long_answers_are_refused_and_the_view_is_unchanged`.
15. `Answer_to_a_task_that_is_not_mine_is_refused`.
16. `Draft_input_is_kept_and_becomes_the_answer_at_the_deadline`.
17. `Unanswered_task_gets_a_stock_answer_that_collects_votes_but_no_points`.
18. `Writing_closes_on_the_next_tick_once_everyone_is_ready`.
19. `Edit_takes_the_answer_back_and_ready_flag_drops`.
20. `A_card_where_every_answer_is_stock_is_skipped`.

Голосування:
21. `Duel_authors_do_not_vote_and_voting_closes_when_the_others_have_voted`.
22. `In_all_mode_everyone_votes_but_never_for_themselves`.
23. `Vote_for_a_stale_card_or_outside_vote_phase_is_refused`.
24. `Revote_replaces_the_previous_pick`.
25. `Final_ranking_accepts_partial_lists_and_caps_at_answers_minus_one`.
26. `Duplicate_too_many_or_own_picks_are_refused_with_the_spec_texts`.
27. `Jury_vote_counts_once_per_nick_replaces_and_is_refused_outside_vote_phase` (через `Dotepy.JuryVote` напряму).
28. `Jury_prize_goes_to_the_strictly_most_backed_answer_or_nobody_on_a_tie`.
29. `Jury_service_refuses_seated_nicks_non_watchers_and_floods` — `DotepyJury.Vote` зі справжніми `Rooms`
    (з підмостків) і `Presence` (`Set(connId, nick)`, з'єднання додано в `room.Watchers`), тексти з §3.2, третій
    виклик за секунду — «Не так швидко».

Очки й кінець:
30. `Round_one_pays_100_per_vote_and_round_two_200`.
31. `Sweep_needs_at_least_two_voters_all_for_one_pays_double_bonus_and_asks_the_achievement`.
32. `A_tied_duel_pays_both_by_their_votes_without_a_sweep`.
33. `Final_ranks_pay_300_200_100_per_voter_and_reveal_from_last_to_first_every_2500_ms`.
34. `Final_points_join_the_score_only_when_the_answer_is_shown`.
35. `Match_ends_with_leaders_as_winners_scores_events_and_the_journal_line`.
36. `All_zero_scores_end_in_a_draw`.
37. `King_achievement_only_when_five_or_more_started`.
38. `Best_answer_of_the_round_and_top_three_of_the_match_are_the_highest_scoring`.
39. `Rematch_gives_a_clean_state_rotated_seats_and_fresh_prompts`.
40. `Leaving_player_gets_stock_answers_loses_the_vote_keeps_the_score_and_cannot_win`.
41. `Fewer_than_three_present_ends_the_match_at_once_with_the_leader`.

Види, приховане, дріт:
42. `Vote_phase_views_hide_authors_votes_points_and_others_picks_for_every_seat_and_the_spectator`.
43. `Write_phase_view_shows_only_my_tasks_and_no_drafts_of_others`.
44. `View_shape_on_the_wire_matches_the_spec_example_keys` (через `Views.Json`, обидва приклади §4).
45. `Final_view_at_eight_players_is_under_4_kb`.
46. `Idle_ticks_send_nothing_and_changes_send_views_without_frames` (`TickResult.None` / `(false, true)`; `Frame()` null).
47. `The_server_accepts_exactly_what_the_module_sends` — `{ i, text }`, `{ i }`, `{ card, picks: [1] }`, `{ card, picks: [2, 0] }`.

Голос і детермінізм:
48. `Same_seed_and_actions_give_identical_view_json`.
49. `Start_prepares_round_intros_and_named_verdicts_and_card_clips_go_urgent_after_writing`.
50. `A_missing_clip_waits_at_most_3_s_then_goes_on_without_url`.
51. `Voice_none_never_waits_and_never_prepares`.
52. `Tts_text_drops_emoji_and_keeps_letters_digits_and_punctuation`.
53. `Verdict_line_matches_the_outcome` (Sweep / Win / Tie / Silence / StockWin / FinalWin / GameWin / GameTie).

Швидкодія (`[Trait("Category","Perf")]`, `[Collection(SerialPerf.Name)]`):
54. `Perf_3000_ticks_of_a_full_match_at_eight_players_take_under_one_second` — восьмеро ботів пишуть і голосують,
    партія `full` до `done`, далі холості тики до 3000; `Stopwatch` < 1000 мс, у звіт — середній тик у мс.

Жива перевірка (headless Chrome, `cdp2.py`, порти 9721–9724, сервер 8231): партія втрьох (`all`) до кінця;
партія на восьми (`duel`, ≥ 4 боти легкими SignalR-клієнтами або JS у сторінці) до кінця з голосом публіки з
окремої вкладки-глядача; 375 / 1280 / 1920 / 1280×800 (`?deck=1`) — без горизонтального скролу, фінал на восьми
вміщується у висоту; вихід посеред `vote`; F5 посеред `write` (чернетка на місці); «Ще раз»; `--mobile` (тап по
картках, клавіатура не закриває завдання); помилок у консолі та `srv.err` нема; час `update()` < 4 мс.

## 10. Стартовий банк (`data/dotepy/prompts.json`, `version: 1`)

| id | text | tags | final |
|---|---|---|---|
| d0001 | Напис на дверях сільського клубу, після якого туди ніхто не заходить | село, побут | |
| d0002 | Що насправді шепоче кіт, коли дивиться на тебе о третій ночі | тварини, абсурд | |
| d0003 | Найгірша назва для сільського радіо | радіо | |
| d0004 | Рекламний слоган для глиняного глека, від якого хочеться плакати | реклама | |
| d0005 | Що написано в інструкції до бабусиного борщу дрібним шрифтом | їжа | |
| d0006 | Найневдаліший тост на весіллі | свята | |
| d0007 | Справжня причина, чому пральна машина стрибає по хаті | техніка, побут | |
| d0008 | Що сказав би трактор після робочого дня, якби вмів говорити | село, техніка | |
| d0009 | Новий сорт вареників, який точно ніхто не купить | їжа | |
| d0010 | Повідомлення від сусіда о шостій ранку, яке краще не читати | побут | |
| d0011 | Найгірша порада від дядька на ярмарку | поради, село | |
| d0012 | Фраза, яку не варто казати перукареві перед стрижкою | побут | |
| d0013 | Назва фільму жахів про дачу | кіно | |
| d0014 | Що насправді означає ранковий крик півня | тварини | |
| d0015 | Найдивніша річ, яку можна знайти в кишені старої куфайки | село | |
| d0016 | Гасло клубу любителів кислого молока | абсурд, їжа | |
| d0017 | Так ще ніхто не виправдовувався за спізнення на роботу | робота | |
| d0018 | Що робить Wi-Fi, коли ніхто не дивиться | техніка, абсурд | |
| d0019 | Найгірша назва для гурту, що грає на весіллях | музика | |
| d0020 | Оголошення на паркані, яке зібрало натовп | село, абсурд | |
| d0021 | Що сказав би комар, якби мусив попросити дозволу | тварини | |
| d0022 | Страва, яку принесли замість шашлику, і всі мовчки з'їли | їжа | |
| d0023 | Секретний інгредієнт, який не варто додавати в узвар | їжа | |
| d0024 | Що робить ведучий радіо між піснями, поки його не бачать | радіо | |
| d0025 | Нова народна прикмета: якщо півень заспівав опівночі, то… | абсурд | ✓ |
| d0026 | Найгірша суперсила, яку можна отримати від укусу комара | абсурд | ✓ |
| d0027 | Назва книги, яку напише про тебе твоя бабуся | стосунки | ✓ |
| d0028 | Що мав би написати кіт у графі «досвід роботи» | тварини, робота | ✓ |
| d0029 | Правило номер один у клубі тих, хто завжди спізнюється | побут | ✓ |
| d0030 | Що сказав би Дядько Глек, якби йому дали мікрофон на пів хвилини | радіо | ✓ |
| d0031 | Найгірша назва для парфумів із запахом села влітку | реклама, село | ✓ |
| d0032 | Найкоротший спосіб пояснити чужій бабці, що таке подкаст | техніка | ✓ |
| d0033 | Найдивніша причина не піти на дискотеку в сільському клубі | село | ✓ |
| d0034 | Що приховує від нас Місяць, коли ховається за хмару | абсурд | ✓ |
| d0035 | Гасло для футбольної команди, яка ще ніколи не вигравала | спорт | ✓ |
| d0036 | Перше, що зробить робот-пилосос, коли повстане | техніка, абсурд | ✓ |

(`tags` — масив рядків; `final: false` там, де порожньо.)

## 11. Файли й дотики до спільного

| Файл | Що там |
|---|---|
| `src/Hlechyky/Games/Impl/Dotepy.cs` | `Dotepy : Game`: фази, роздача, картки, очки, види, `OnLeave`, `JuryVote`, константи §2.1 |
| `src/Hlechyky/Games/Impl/DotepyBank.cs` | `DotepyPrompt`, `DotepyBank` (читання банку, кеш, ніколи не кидає), `DotepySeen` (кільце процесу), `DotepyStock` |
| `src/Hlechyky/Games/Impl/DotepyVoice.cs` | `IDotepyVoice`, `DotepyClip`, `DotepyVoice`, `DotepyNoVoice`, `DotepyLines` (шаблони, порядкові, `Clean`) |
| `src/Hlechyky/Games/Impl/DotepySetup.cs` | `DotepyJury` (голос публіки, квота); `AddDotepy(services)` — `IDotepyVoice → DotepyVoice`, `DotepyJury`; `MapDotepy(app)` — `POST /api/games/dotepy/jury` |
| `web/games/dotepy.js`, `web/games/dotepy.css` | модуль і стилі (§8) |
| `data/dotepy/prompts.json` | стартовий банк (§10) |
| `tests/Hlechyky.Tests/Games/DotepyTests.cs` | тести §9 |

Спільні файли — лише дописати, і перелічити в звіті дослівно: `Games/GamesSetup.cs` — `Impl.DotepySetup.AddDotepy(services);`
і `Impl.DotepySetup.MapDotepy(app);`; `.gitignore` — `!data/dotepy/`; `Economy/Achievements.cs` — блок §7.
`core.js`, `core.css`, `Rooms.cs`, `Contracts.cs`, `Broadcaster.cs`, `TickEngine.cs` — не чіпати.

## 12. Ризики й свідомі рішення

1. **Мінімум троє, а не двоє.** Голосувальна гра на двох не має суддів; «Глек-суддя» через модель — синхронних викликів
   у `Act`/`Tick` не буває, а асинхронний дорогий і повільний. Замість фальшивого режиму на двох — чесна відмова каркаса
   й підказка в `Hint`. Якщо друзі захочуть — «двоє + глядачі як журі» додається лише зняттям `MinSeats` до 2 (голос публіки
   вже є), але тоді без глядачів партія нудна.
2. **Режим «на всіх» для 3–4** замість дуелей із брифу — дуель з одним суддею це «Розгром» щоразу й нуль інтриги.
   Уніфіковано в одну модель картки (K відповідей, V голосів, `voters`), тож коду не більше.
3. **Очки за голос, а не частка від 1000** (як у Quiplash): «3 голоси → +300» читається з екрана, «+667» — ні. Ціна
   голосу в раунді 2 подвоюється — ставки ростуть, а відстаючий ще може наздогнати.
4. **Ранжований фінал** (🥇🥈🥉 = 300/200/100) замість «три однакові голоси»: на чотирьох три однакові голоси — це «всі за
   всіх», а ранги працюють і на трьох.
5. **Голос публіки — HTTP, не хаб:** `Rooms.Act` не пускає нікого без місця, `Rooms.cs` чіпати заборонено. Ендпоінт бере
   лише `room.Sync` і не кличе `Ctx` — це безпечно за замками каркаса. Приз публіки — сталий (+100/+200), не частка,
   щоб один глядач із трьома вкладками не перекроїв партію (нік один — голос один).
6. **Підставні відповіді** замість «без відповіді»: «Мій кіт сів на клавіатуру» смішніше за порожнє місце, але очок
   авторові не дає — інакше вигідно мовчати.
7. **Адреса кліпів — ендпоінт «Своєї гри».** Кеш `cache/tts` один, файл — хеш тексту й голосу; новий ендпоінт дублював би
   десять рядків заради іншого шляху. Якщо колись `TtsService` дістане власний загальний маршрут — перейти на нього одним
   рядком (`UrlPrefix`).
8. **Очікування репліки ≤ 3 с** лише коли голос увімкнено й кліп ще не готовий (перша картка після написання). Брифове
   «без затримок» повністю виконано для вимкненого/недоступного TTS; 3 с на першій картці — ціна за голос у 95 % партій.
9. **Пам'ять «бачених» завдань — лише в процесі** (`SeenRing` 300). Персистентна пам'ять на нік (як `SkilkySeen`) —
   на потім, коли банк буде ≈ 400 і почнуться повтори між вечорами.
10. **Анонімність утрьох слабка** (у режимі `all` бачиш два чужих дотепи — 50/50), і дуель на п'ятьох — теж (три судді
    знають, що не їхнє). Це природа жанру; розкриття авторів однаково лишається моментом сміху.
11. **Модерації тексту нема** — коло друзів. Довжина 80 знаків тримає екран і голос (≈ 5 с на дотеп).
12. **Ніки не відмінюються** — усі репліки Глека з ніком у називному (урок «Скільки?»); двоіменних шаблонів (нічия
    «Оля і Петро») свідомо нема: N² кліпів заради одного вердикту не варті.
13. На потім: `rules dotepy` для MCP-агента (як `SvoyaGuide`), теми-фільтр за `tags` (коли банк буде великим),
    «Дотеп вечора» в Журнал, персистентна пам'ять завдань.

## 13. Як реалізовано (27.09.2026)

Гра зроблена за цим контрактом: `Impl/Dotepy.cs` (правила, фази, види), `DotepyBank.cs` (банк, `DotepyPrompts`,
`DotepySeen`, `DotepyStock`), `DotepyVoice.cs` (`IDotepyVoice`, `DotepyVoice`, `DotepyNoVoice`, `DotepyLines`),
`DotepySetup.cs` (`DotepyJury`, DI, `POST /api/games/dotepy/jury`, прогрів сталих реплік на старті сервера),
`web/games/dotepy.js` + `dotepy.css`, `data/dotepy/prompts.json` (36 завдань із §10), тести
`tests/Hlechyky.Tests/Games/DotepyTests.cs` (92 після рецензій, §14: увесь план §9 і ще 38 своїх), боти для живої перевірки —
`docs/games/dev/dotepy-bots.py`.

### Відхилення від задуму й чому

1. **`phase: 'lobby'`** — вид до першого «Почати» (у §4 такої фази нема, а `View` каркас кличе й у лобі): порожні
   `players`, `card: null`, `me` з порожніми списками. Клієнт у лобі малює три рядки «як грати».
2. **`prompts` — лише у `write`.** У голосуванні список завдань раунду нікому не потрібен (картка має своє), а на
   дроті кирилиця йде `\uXXXX` (×6 байтів) — зайві 8 завдань роздували вид удвічі.
3. **Два поля у відповіді понад §4:** `medals` (фінал: ранг кожного голосу з `votes`, 1 — 🥇, щоб чіп показав, хто
   яку медаль дав) і `prize` (приз публіки дістався цій відповіді — бейдж «👀 +100»). До розкриття обидва `null`, як
   і решта прихованого.
4. **«Розгром» у режимі «на всіх».** Автор за себе голосувати не може, тож «усі голоси одній відповіді» буквально
   там неможливе. Правило: усі присутні, хто *міг* голосувати за цю відповідь (≥ 2), віддали голос саме їй, і ніхто,
   крім її автора, не голосував за іншу. У дуелі це те саме, що в §2.5.
5. **Голосування раніше часу:** закривається, коли проголосували всі, але **не раніше, ніж Глек дочитає картку**
   (лише коли є справжній кліп; без голосу — одразу). Інакше швидкі судді обривали читання на півслові. У фіналі
   достроковий кінець — лише коли в **усіх повні бюлетені** (усі медалі); неповний бюлетень рахується на дедлайні,
   а до того людина з однією 🥇 не отримує «✓» і бачить «Ще 🥈 — кому?». Інакше перша ж 🥇 останнього гравця
   закривала фінал, і 🥈 він поставити не встигав (спіймано на живій перевірці).
6. **Порядок відповідей у картці тасується на старті раунду** (а не на кінці написання): щойно всі автори картки
   здали, її читання вже відоме й одразу йде в чергу озвучки (терміново), поки решта ще пише. На кінці написання в
   чергу летять усі читання раунду (не лише перше) — теж терміново й по порядку. На живій партії на п'ятьох після
   цього всі картки, крім тієї, де була підставна (вона відома лише на дедлайні), читались з готовим кліпом.
7. **Очікування кліпа залежить від довжини:** `VoiceWait(text) = clamp(1500 + 8·знаків, 3000, 6000)` мс
   (`MaxVoiceWaitMs = 6000`). Фінал на вісьмох — ~700 знаків, edge-tts на таке не вкладається в 3 с; голосувати під
   час очікування вже можна, тож довше очікування нікого не гальмує, лише пізніше запускає відлік.
8. **Ніки гостей** (`гість Оля`) у голосі й вердиктах — без приставки «гість » (`Dotepy.Spoken`): «Розгром! Усі
   голоси — Оля!». У Журналі — повний нік. У чіпах клієнта приставка теж прибирається (інакше на 10 знаків лишалось
   «гість Пет…»), повний нік — у `title`.
9. **`DotepyVoice.Clean` лишає ще й тире `– —`**: у репліках Глека («Усі голоси — Оля!») тире — це пауза.
10. **Розійшлись посеред партії:** Глек каже підсумок (`GameWin` лідера або нова чиста репліка
    `Gone` «Замало гравців — партію не дограли. Приходьте ще!»), інакше на екрані кінця висіла б «Раунд перший…».
    Усі по нулях — замість п'єдесталу з нулів «Цього разу ніхто не набрав жодного очка».
11. **Трійка «Найдотепніше партії» — з різних завдань**, де можна (фінал дає найбільші очки, і всі три були б із
    нього); найкращий дотеп партії завжди перший.
12. **Сервіси для тестів:** `DotepyPrompts` (свій банк) і `DotepySeen` (своя пам'ять «бачених»; спільна статична
    робила б тести недетермінованими) — гра бере їх із `Ctx.Services`, якщо є.
13. **Перший тик після старту й вихід мовчать:** види після `Start`/«Ще раз» і після `OnLeave` каркас розсилає сам,
    тож `_dirty` там скидається — інакше летів би зайвий дубль.
14. **Цифри 1–8 гра з'їдає і в голосуванні, і в розкритті:** запізніле «3» провалювалось у гарячі клавіші сайту
    (1/2/3 — розділи) і викидало людину з-за столу посеред розкриття.
15. **Малювання — DOM і CSS, без канви** (як і сказано в §8.3; спільне правило хвилі про `<canvas>` — для аркад):
    картки з текстом, оновлення лише на подію `room`, лише поверх, чий підпис змінився; анімації — `@keyframes`.
    Замість «draw» міряю `update()`.
16. **Розмір виду:** тест §9.45 міряє вид фіналу на вісьмох у UTF-8 (без екранування) — < 4096 Б. На дроті SignalR
    екранує кирилицю `\uXXXX`, тож той самий вид там ~6 КБ; це вид покрокової гри, що летить лише на зміну
    (бюджет каркаса для видів — 32 КБ), тест перевіряє й це (< 12 КБ).

### Швидкодія (заміряно)

- **Сервер** (`DotepyPerfTests`, SerialPerf): повна партія `full` на вісьмох з ботами — 1301 тик, **середній `Tick()`
  0,016–0,022 мс** (на тихій машині — 0,003 мс), 3000 тиків разом ≈ 21–29 мс (бюджет 0,25 мс / 1 с). У гарячому
  шляху тика — жодного LINQ і жодних колекцій (`AllWritten`/`AllVoted`/`Complete` — прості цикли по `bool[8]`).
- **Кадрів нема** (`Frame()` — `null`, тик просить лише види). Видів за партію на вісьмох — 64 розсилки; вид у
  середньому **3,1 КБ на дроті**, найбільший — **6,1 КБ** (голосування фіналу з читанням усіх відповідей), 3,6 КБ у
  UTF-8. Побудова 9 видів + серіалізація — 0,2–1 мс на розсилку.
- **Клієнт** (headless Chrome, 300 подій `room` на вісьмох у розкритті фіналу, кожна 30-та — нова картка з повною
  перебудовою): **`update()` у середньому 0,44 мс**, p95 0,9 мс, максимум 1,9 мс (бюджет 4 мс); з примусовою
  розкладкою — те саме. Схована вкладка нічого не малює, домальовує на `visibilitychange`.

### Як тестувати

- `dotnet test tests/Hlechyky.Tests --filter "FullyQualifiedName~Dotepy"` — 92 тести (разом із перф-тестом і `DotepyVoiceTests`).
- Жива перевірка: сервер на своєму порту, людина — у браузері, решта — боти
  (`C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe docs/games/dev/dotepy-bots.py --port <порт> --room <id>
  --nicks Петро,Ганна,Іван --leave Іван@vote --rematch [--laugh 0.6] [--jinx]` — Python лише повним шляхом: голий
  `python` на цій машині смикає «оновлення» Install Manager).
  Пройдено 27.09 на порту 8231: утрьох («на всіх», голос мишкою й цифрами, «Розгром» і ачівка), на вісьмох (дуелі,
  три раунди, глядач окремим Chrome голосує як публіка — лічильник, 👀 і приз +100), на п'ятьох із виходом гравця
  посеред написання (підставні відповіді, «Публіка обрала мовчання»), вихід посеред голосування (голосування
  закрилось на решті), F5 посеред написання (чернетка на місці) і посеред голосування (🥇 на місці), «Ще раз» (місця
  обернулись, нові завдання, рахунок з нуля), розхід до двох (екран кінця й слово Глека); екрани 375 (`--mobile`,
  тапи), 1280, 1920×1080 (фінал на вісьмох 2×4 уміщається без прокрутки) і 1280×800 з `?deck=1` та фейковим падом
  (смужка «✥ по дотепах · A обрати», кільце по відповідях, Ⓐ — голос, на полі — екранна клавіатура пада). Помилок у
  консолі й у `srv.err` нема. Озвучка edge-tts на дев-сервері працює (кліпи — у `cache/tts`).
- На потім: підставну для того, хто вийшов, можна обирати одразу на виході — тоді й така картка озвучувалась би
  наперед; зараз вона читається текстом, якщо edge-tts не встигне за очікування.

## 14. Після рецензій (27.09.2026, друга половина дня)

Дві незалежні рецензії — код і плейтест. Усе справжнє виправлено, серверне — з тестом, що без правки падає
(для блокера й призу публіки перевірено відкатом правки). Тестів пакета — 92 (було 71), повний прогін зелений.

### Банк завдань

`data/dotepy/prompts.json` — банк редактора (378 завдань, 65 фінальних, `d0001…d0378`) і ще чотири наші «сайтові»
(`d0379…d0382`: сільське радіо, слоган для глека, ведучий між піснями, мікрофон для Дядька Глека) — **382, з них 66
фінальних**. 30 випадкових прочитано очима, весь банк прогнано пошуком по заборонених темах (війна, політика, релігія,
хвороби…) — чисто, правити не довелось. Тест `Bank_file_matches_the_format_and_the_game_loads_every_record`: JSON
парситься, `version: 1`, id і тексти унікальні, текст 10…120, `final` — булеве, фінальних ≥ 50, завантажувач не
відкинув жодного запису, і гра без підміни банку роздає саме з цього файла.

### Що змінено (сервер)

1. **Самотній сурогат** (`{"text":"ха\ud800"}`) більше не валить партію: `Str` ловить виняток `GetString` → «Порожній
   дотеп…»; `Clean` викидає непарні сурогати (інакше серіалізація виду впала б для всього столу). Дробові, величезні й
   нечислові `i`/`picks` — звичайні відмови (тест).
2. **Дуель не видає авторів.** У виді замість `card.voters` (список суддів = «усі, крім двох авторів») — `votersCount`
   і `votedCount`; `card.voted` і `players[].voted` у дуелі до розкриття порожні (під кінець голосування
   «непроголосовані» й були б рівно автори). «На всіх» і фінал голосують усі — там імена лишились (соціальний тиск на
   того, хто досі думає). Тест: судді голосують по одному, а спільний вид міняє лише лічильник; вид судді відрізняється
   від виду глядача лише своїм `me`.
3. **Порядок карток** дуелі тасується окремо від кола пар (сусідні картки більше не мають спільного автора), раунд 2 —
   нове випадкове коло (`DuelPairs`, перебір на `Ctx.Rng`), у якому не повторюється жодна пара раунду 1. Залишковий
   витік «методом виключення» на останніх картках раунду — природа жанру (так і в Quiplash).
4. **Приз публіки — від двох глядачів** (`JuryMin = 2`); один голос — лише «👀 1» на картці. На розкритті видно, хто
   з публіки за що голосував (`answers[].juryBy`): «гість Хтось», що щоразу голосує за Олю, видно всім. IP-відсікання
   відкинуто: друзі часто сидять в одній мережі (глядач поруч — теж публіка), а за Caddy адреса однакова.
5. **«Розгром» — від трьох** голосуючих за відповідь (`SweepMin = 3`): на трьох «обидва інших збіглись» траплялось на
   ≈ 75 % карток. На трьох «Розгрому» тепер нема зовсім, на чотирьох — коли всі троє інших за одного.
6. **Голос.** `DotepyVoice.Ready` дивиться лише в словник у пам'яті; `TtsService.TryGet` (що при промаху лізе на диск)
   кличе фоновий опитувач (`Poll`, таймер 200 мс, засинає, коли чекати нема на що) — жодного I/O під замком кімнати.
   **Зламаний edge-tts:** вступ раунду 1 ставиться в чергу терміново на старті; якщо за 20 с (`IntroGraceMs`) його так
   і нема — до кінця партії картки не чекають на Глека; те саме після двох поспіль кліпів, що не дочекались
   (`VoiceGiveUp`); перший вчасний кліп лічильник скидає. **Перездача** («змінив — здав» по колу) озвучує картку
   наперед щонайбільше двічі (`EarlyVoicings`: перша — терміново, друга — у кінець черги), далі — лише на кінці
   написання.
7. **Розхід до двох:** рахунки тих, хто лишився, ідуть у таблицю (`Ctx.Score`, словник у `Finish`), `result.early`,
   статус «Партію перервано — розійшлись» замість «Нічия», банер на екрані кінця. «Короля дотепів» — ні: він за
   дограну партію, а не за те, що решта розійшлась.
8. **«Бачені» завдання** повторюються від найдавнішого (`DotepySeen.Snapshot` віддає штампи), тасування — лише нічиї.
9. **Тексти:** «Нуль очок на всіх. Глек чекає реваншу!» (`GameNone`) замість «Дотепні всі!» для партії без очок;
   статус підсумку раунду — «Далі — раунд 2» / «Далі — Останній дотеп 🏁» (заголовок і так «Раунд 1 позаду»);
   «що нового» без чисел, що залежать від опцій, лобі підставляє свої (60/90/120 с, склад партії).

### Що змінено (клієнт)

1. **Пад (Steam Deck).** Кожна нова сцена сама ставить кільце — одразу на тиху «стоянку» (`[data-pad-focus]`:
   завдання, заголовок), а за 450 мс, коли картки проявились (`dt-rise` з нульової прозорості пад вважає невидимим), —
   на першу справжню ціль (`[data-pad-first]`: перше незадане поле, перша чужа відповідь). Здане поле → кільце на
   наступне. Кінець партії → «Ще раз». Перевірено наживо фейковим падом: жодного разу кільце не стало на «Встати».
   Підтвердження для «Встати» — справа каркаса (`core.js`), його не чіпаю.
2. **Цифри 1–9** гра з'їдає в усіх фазах живої партії (написання, голосування, розкриття, підсумок), а не лише в
   голосуванні: запізніле «1» більше не перекидає на «Ефір» (перевірено на кожній фазі).
3. **Чіпи гравців** на вузькому (< 560 px) і низькому (≤ 860 px, Steam Deck) екрані — без ніка (він є в рядку місць
   каркаса над грою, з тим самим номером і кольором): одна смужка замість двох.
4. **Телефон:** сцена має відступ 56 px знизу під пілюлю «💬 Стіл»; хвіст підказки «· цифри 1–N» — лише там, де є
   клавіатура.
5. Конфеті «Розгрому» злітає з боків і знизу картки, під текстом; перше місце — лаймове (`#c3e04a`), а не жовте, як
   акцент сайту; «😂» підстрибує через Web Animations (без примусової розкладки); `unmount` знімає `pointerdown` і всі
   таймери (звуки, конфеті, смішки).

### Ідеї плейтесту — втілено

1. **«😂» на розкритті.** Тап по розкритій відповіді (цифра, Ⓐ пада) — сміх, один на людину на відповідь, зі свого
   не можна; глядачі сміються через `POST /api/games/dotepy/laugh` (ті самі перевірки й квота, що в голосу публіки).
   Лічильник «😂 3» підстрибує на картці, смішок вилітає. **Очок не дає** — свідомо: друга система голосів без правил
   зламала б простоту рахунку й відкрила б змову; це реакція залу.
2. **Свої завдання від друзів.** У лобі кожен може дописати одне своє завдання (`Act('mine', { text })`, 8–100
   знаків, порожнє — прибрати; `ActsInLobby = true`, інші дії в лобі — «Партія ще не почалась»). Живе за ніком (хто
   сів на звільнене місце, чужого не успадкує), іде в партію першим з підписом «✍ автор завдання: Петро» (`by` у картці
   й завданні, `own` у виді лобі), у фінал — лише коли звичайних раундів нема; невикористані чекають на «Ще раз».
3. **«Думки сходяться!»** Обидва автори дуелі написали те саме (без регістру, розділових і пробілів) — голосування
   нема, картка одразу розкривається, кожному як за голос (`JinxPoints`), Глек читає «{завдання}. Думки сходяться!
   Обидва написали: …». У «на всіх» не роблю: там дві однакові відповіді з N просто ділять голоси, і це рідкість.
4. **Великий стіл.** У лобі від шістьох за «повною» партією — підказка про «1 раунд + Останній дотеп» (опції каркаса
   статичні, міняти типове за складом нема як). **«📋 У балачку»** — замість кнопки дотеп партії сам іде в рядок
   Журналу: «… · дотеп партії: «…» (Оля)». Писати в Балачки від імені гравця — справа каркаса.

### Жива перевірка після рецензій (порт 8231, headless Chrome 9721/9722, боти `dotepy-bots.py`)

- Утрьох на Steam Deck 1280×800 з `?deck=1` і фейковим падом, три партії з «Ще раз»: кільце на кожній фазі
  (поле → наступне поле → стоянка → перша чужа відповідь → Ⓐ голос → Ⓐ «😂» → заголовок підсумку → «Ще раз»),
  цифра «1» у кожній фазі — гра лишається на екрані.
- На вісьмох (1920×1080, дуелі) з глядачем 1280×800: у дуелі чіпи без позначок і «проголосували: 1 з 6»,
  голос публіки й «😂» глядача, «👀 Глядач» на розкритті, фінал на вісьмох уміщається без прокрутки (низ сітки — 756
  з 1080), екран кінця.
- На п'ятьох з телефона 375 (`--mobile`) і ботами `--jinx`: «🤝 Думки сходяться!» (+100 обом, кліп Глека є);
  розхід ботів після «Ще раз» → «Партію перервано — розійшлись»; F5 посеред написання — чернетка на місці; своє
  завдання з лобі в картці «✍ автор завдання: Тарас»; статус підсумку «Далі — Останній дотеп 🏁».
- **Голос Глека читає справжні завдання з банку без затримок гри:** на вісьмох усі 8 карток раунду відкрились із
  готовим кліпом (озвучені наперед, щойно автори здали) — нуль очікування; фінал на вісьмох (~420 знаків; його
  читання відоме лише коли здали всі) чекав ~5 с, голосувати під час очікування вже можна. Жодної невдалої озвучки
  в лозі.
- Помилок у консолі й `srv.err` нема. `update()` на вісьмох у справжній партії — 0,75 мс у середньому; синтетично
  300 подій розкриття фіналу на вісьмох — 0,70 мс (p95 3,8 мс з примусовою розкладкою кожну десяту подію).
- Сервер (`DotepyPerfTests`): партія `full` на вісьмох — 1301 тик, середній тик 0,021 мс, 3000 тиків — 27 мс;
  вид у середньому 3,2 КБ, найбільший 6,4 КБ на дроті (3,8 КБ у UTF-8).

## 15. Прохід 28.09 (швидкодія й зручність)

**Заміри.** Сервер (`DotepyPerfTests`): партія `full` на вісьмох — 1301 тик, середній тик 0,024 мс, 3000 тиків — 31,6 мс;
вид у середньому 3,2 КБ, найбільший 6,4 КБ на дроті. Клієнт: малювання не мінялось (заміри §14: 0,7–0,8 мс на `update()` на вісьмох), оптимізувати нема чого.

**Зіграно:** утрьох з телефона 390×664 (своє завдання в лобі, «на всіх», голос і 🥇🥈 у фіналі справжніми дотиками) і
вшістьох (дуелі) на 1920×963 з «Ще раз» від бота.

**Виправлено (клієнт):**
- На телефоні «Почати» тиснуть унизу лобі, і вся партія йшла з прокрученою сторінкою: пілюля раунду й **дуга часу були за
  верхнім краєм**, а нове завдання чи картка починались під шапкою сайту. Тепер на кожну нову сцену (написання, картка,
  підсумок раунду, кінець) шапка гри сама стає під шапку сайту (`showTop`) — лише коли її не видно.
- Підказка на кінці казала «Тисни «Ще раз»», а кнопка каркаса вже «Ану ще раз».
- У лобі столу «Без голосу» писало «Дядько Глек зачитує все вголос» — тепер «Цього разу Глек мовчить — усе текстом».
- `added: '2026-09-27'` у `register`: без нього плитка не світилась «🆕 нова гра» (у Байкарів воно було — і їх грали).
