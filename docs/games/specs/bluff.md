# Байкарі (`bluff`) — хвиля 2, пакет bluff

Spec для інженера: усе, що тут написано, — контракт. Що не написано — вирішуй у дусі каркаса (Skilky.cs, Telephone.cs) і
запиши в розділ «Як реалізовано» наприкінці. Читати перед роботою: `docs/games/ARCHITECTURE.md`, `PROTOCOL.md` (§3 — модуль і `pad`),
`INTEGRATION-NOTES.md` («Граблі»), `TESTING.md`, `D:/or-wt/_tools/AGENT-COMMON-wave2.md`, формат банку — `D:/or-wt/_wave2/CONTENT-FORMATS.md` (§bluff).

## 0. Суть і чому це весело

Як Fibbage. Питання з пропуском і дивною, але справжньою відповіддю: «У XIX столітті у Львові вулиці вперше освітлили
лампами на ___». Кожен за столом вписує **правдоподібну брехню**. Потім усі бачать варіанти впереміш — брехні гравців,
правду й одну-дві заготовлені брехні Глека, коли гравців мало — і обирають, де правда. Своє обрати не можна. Очки: вгадав
правду — +1000; кожен, кого надурила твоя брехня, — +500 тобі; ❤ за найсмішнішу брехню — +100. Останнє питання — подвійне.

**Удвох** це дуель брехунів: ти, друг, правда і два жарти Глека — п'ять карток, і половина партії — намагання вгадати, що з
цього написав саме друг. Кожен раунд — маленька детективна історія на 40 знаків. **На восьми** це шоу: дев'ять карток,
розкриття по одній від найменш до найбільш обраної, «на це купились Петро, Ганна й Іван — а це брехня Олі», хвиля сміху й
❤. Гра не карає за незнання: правду тут не знає ніхто, тому виграє той, хто найкраще відчуває «як звучить правда» і
вміє смішно збрехати. Раунд ≈ 1,5 хв, партія на 7 питань ≈ 10–12 хв.

Найближчі за духом у каркасі: `Skilky.cs` (фази від тика, `Hidden`, пам'ять бачених, банк JSON) і `Telephone.cs` (текстове
поле, «здав → чекаємо», ❤). Реалтайм-передбачення тут не потрібне — гра покрокова з таймерами.

## 1. GameInfo

```csharp
public override GameInfo Info { get; } = new(
    "bluff", "Байкарі", "байкарів", GameGroup.Party, 2, Seats /* 8 */,
    TickMs: 500, Start: StartMode.ByHost, Hidden: true, Private: false, Persistent: false, Rated: false, Score: ScoreOrder.None,
    Options:
    [
        new GameOption("questions", "Питань", [("5", "5"), ("7", "7"), ("10", "10")], "7"),
        new GameOption("pace", "Темп", [("fast", "Швидкий"), ("normal", "Звичайний"), ("slow", "Спокійний")], "normal"),
        new GameOption("cat", "Теми", BluffCats.All /* (key, label) нижче */, BluffCats.Any, Multi: true),
    ],
    Hint: "Питання з пропуском і дивна правда. Кожен вписує свою брехню, потім усі шукають правду серед брехень. Надурив друга — очки тобі");
```

- `Client` порожній (модуль `web/games/bluff.js` + `bluff.css`). `SeatName(i)` = `(i + 1).ToString(CultureInfo.InvariantCulture)`
  — місць до восьми, у чіпі має вміститись нік. `seatClass` у модулі: `['x','o','c','d','bluff-s4','bluff-s5','bluff-s6','bluff-s7']`.
- `ActsInLobby` — типово `false`. `TalkBlock` — типово (говорять усі й завжди). `talk` у `register` **не** вказувати.
- Теми (`BluffCats.All`, той самий порядок у попапі): `all` «Усі теми», `ukraine` «Україна», `history` «Історія», `nature` «Природа
  й тварини», `science` «Наука й техніка», `food` «Їжа й побут», `world` «Світ і звичаї», `sport` «Спорт і розваги», `lang` «Слова й
  мова», `odd` «Дивне». `BluffCats.Parse(option)` → `IReadOnlySet<string>?` (null — усі), `BluffCats.Label(key)`. Парсер — як
  `SkilkyTopics.Parse`: чужі ключі відкидаються, порожнє чи `all` → null.
- Темп (`BluffPace`): секунди на брехню / на вибір — `fast` 30/20, `normal` 45/30, `slow` 60/45.

## 2. Правила

### 2.1 Константи (усі — `public const` у класі `Bluff`, тести на них спираються)

| Ім'я | Значення | Що це |
|---|---|---|
| `Seats` | 8 | місць за столом |
| `MaxLie` | 40 | найдовша брехня, знаків після `Clean` |
| `MinOptions` | 5 | скільки карток намагаємось показати (правда + брехні + заготовки Глека) |
| `ReadMs` | 3000 | фаза `read`: питання видно, писати ще не можна |
| `StepPickedMs` | 3000 | крок розкриття картки, яку хтось обрав |
| `StepEmptyMs` | 1500 | крок розкриття картки, яку не обрав ніхто |
| `StepTruthMs` | 4000 | крок розкриття правди (завжди остання) |
| `ScoreMs` | 6000 | фаза `score` між питаннями |
| `TruthPts` | 1000 | вгадав правду |
| `FooledPts` | 500 | за кожного, кого надурила твоя брехня |
| `LikePts` | 100 | за кожне ❤ під твоєю брехнею |
| `FinalMult` | 2 | останнє питання: `TruthPts` і `FooledPts` подвоюються (❤ — ні) |

Фази (рядки, `public const string`): `PhaseRead = "read"`, `PhaseWrite = "write"`, `PhasePick = "pick"`, `PhaseReveal = "reveal"`,
`PhaseScore = "score"`, `PhaseDone = "done"`.

### 2.2 Партія

1. **Старт** (`Start()`, і на кожен «Ще раз»): `_nicks[s] = Ctx.NickOf(s)` — знімок ніків (той, хто вийде посеред партії, у
   розкритті лишається з іменем); `_present[s] = Ctx.Seated(s)`; рахунки, дельти, лайки — у нуль; питання партії — `Pick()`
   (§7.3); `_q = 0`; `BeginRead(now)`. Якщо питань нуль — `Finish([], "Байкарі: у цих темах не знайшлось питань, партії не буде")`
   (страховка; нормально це відсікає `CanStart`, див. 2.6).
2. **`read`** (3 с): усім видно номер питання, тему, текст із пропуском `___`, позначку «×2» на останньому. Писати ще не можна.
   У цю мить усі присутні позначаються в пам'яті «бачив» (`BluffSeen.Mark`).
3. **`write`** (за темпом, типово 45 с): кожен присутній шле `lie` (§3). Брехню можна переписувати до кінця фази. Фаза
   закінчується раніше, щойно **всі присутні** мають брехню (перевірка в `Tick`: `_endsAt = now`, як у Skilky). Хто не написав —
   у цьому питанні без своєї картки (надурити нікого не зможе), але обирати правду й ставити ❤ може.
4. **Складання карток** (`BuildOptions`, на переході `write → pick`, детерміновано):
   1. Брехні присутніх — у порядку місць 0..7. Кожна порівнюється з уже зібраними картками через `BluffText.LooksSame`
      (§2.4): збіглась — **приєднується до тієї картки як співавтор** (`by` росте), інакше — нова картка `{ text = як написав
      автор, by = [seat] }`. Це правило Fibbage: двоє написали одне й те саме — картка спільна, очки за кожну жертву отримує
      **кожен** співавтор повністю, і жоден не може її обрати. Гравцеві про збіг не кажемо (це підказка, що брехня влучна).
   2. Правда: `{ text = question.Answer, truth = true }`.
   3. Заготовки Глека (`question.Decoys`, у порядку банку): додаються, поки карток менше за `MinOptions` і заготовки не
      скінчились. Пропускаються ті, що `LooksSame` з будь-якою вже зібраною карткою (гравець написав те саме — його версія
      важливіша), і ті, що вже видані як «Хай Глек збреше» (§3, вони й так стоять як брехні гравців). Картка заготовки:
      `{ text, decoy = true, by = [] }` — жертви такої картки очок нікому не приносять («Глек надурив»).
   4. Перетасувати Фішером — Єйтсом на `Ctx.Rng`. Індекс у масиві = `i` картки. **Порядок один для всіх** — щоб за столом
      можна було сказати «третя — точно правда».
   Приклади: удвох, обоє написали різне → 2 брехні + правда + 2 заготовки = 5; утрьох → 3 + 1 + 1 = 5; учотирьох і більше,
   усі написали → без заготовок; удвох, один не написав → 1 + 1 + до 3 заготовок. Нікого й нічого → правда + заготовки.
5. **`pick`** (за темпом, типово 30 с): усі бачать картки (без авторства), обирають одну (`pick`), можна змінювати до кінця.
   Свою (де `by` містить моє місце) обрати не можна. Закінчується раніше, щойно всі присутні обрали.
6. **`reveal`** — шоу: картки відкриваються по одній. Порядок: усі не-правдиві картки за зростанням кількості голосів
   (рівні — за `i`), **правда завжди остання**. Крок триває `StepPickedMs` (хтось обрав), `StepEmptyMs` (ніхто) або
   `StepTruthMs` (правда). На момент відкриття картки нараховуються очки: брехня гравців — кожному співавтору `FooledPts × mult`
   за кожного, хто її обрав; правда — кожному, хто її обрав, `TruthPts × mult`; заготовка Глека — нікому. `mult` =
   `FinalMult` на останньому питанні, інакше 1. Очки за питання (без ❤) накопичуються в `_delta[seat]`, а `_victims[seat]` —
   скільком голову замакітрила його брехня в цьому питанні.
7. **❤** (`like`, §3) — під час `reveal` (лише вже відкриті картки) і `score`. Ставиться лише на брехню гравців (не на правду, не
   на Глека), не на свою; перемикач: повторний `like` — знімає. Кожне ❤ одразу дає кожному співавтору `+LikePts`, зняте —
   відбирає; лічиться в `_likeDelta[seat]` цього питання і в `_likes[i]` картки.
8. **`score`** (6 с): таблиця з дельтами. Потім наступне `read`. Після **останнього** питання фази `score` нема: одразу
   `Done()` — підсумок і так лишається на екрані в `done`.
9. **`done`**: переможці — усі присутні з найбільшим рахунком (кілька — кілька переможців); якщо найбільший рахунок 0 —
   `Finish([])` (нічия). `Ctx.Finish(winners, log, scores)`, де `scores` — словник `seat → рахунок` для всіх, хто сидів на старті й
   не вийшов. Рядок Журналу (нік завжди в називному, ніки ми не відмінюємо):
   `Байкарі: Оля 6 500, Петро 4 000, Ганна 2 500 · найкраща брехня — «свинячому салі» (Оля, 2 жертви)`; коли за партію ніхто нікого
   не надурив: `… · нікого так і не надурили`. Числа — з пробілами між тисячами (`Num` як у Skilky). Жертви: 1 жертва / 2–4 жертви
   / 5+ жертв. Найкраща брехня партії — брехня гравців із найбільшою кількістю жертв за всі питання; рівні — більше ❤; далі —
   раніша. Співавтори перелічуються через «і»: `(Оля і Петро, 3 жертви)`.
10. Ачівки (`Ctx.Award(seat, 0, "ach:…")`, нуль черепків — це сигнал): **`bluff-fox`** «Хитрий лис» — одна твоя брехня надурила
    **двох і більше** за одне питання (видається в момент розкриття, раз на партію на місце); **`bluff-nose`** «Нюх на правду» —
    вгадав правду в **кожному** питанні партії, партія ≥ 5 питань, гравець сидів від старту до кінця, за столом на кінець ≥ 2.
    Обидві — у каталог `Achievements.cs` блоком у кінці (§10).

### 2.3 Час і тик

- `TickMs = 500`. Усі переходи фаз — **лише** в `Tick()`, за `Ctx.Clock` і `_endsAt` (Skilky-стиль). `Act` тільки міняє стан і
  ставить `_dirty = true`. Каркас після `Act` реалтайм-гри видів не шле (`Rooms.Act`, `counts == false`), тож підтвердження
  гравцеві — це `ActResult.Accept(...)`, а видам решті — найближчий тик (≤ 0,5 с).
- `Tick()`:
  ```
  if (_phase == done) return None;
  now = Ctx.Clock.UtcNow;
  if (_phase == write && AllWrote()) _endsAt = now;      // «Готово» в усіх — не чекаємо таймер
  if (_phase == pick  && AllPicked()) _endsAt = now;
  if (now < _endsAt) { if (!_dirty) return None; _dirty = false; return Both; }
  _dirty = false;
  switch (_phase): read → BeginWrite(now); write → BeginPick(now) /*BuildOptions*/; pick → BeginReveal(now);
                   reveal → (крок є ще) NextStep(now) : (останнє питання ? Done() : BeginScore(now)); score → { _q++; BeginRead(now); }
  return Both;
  ```
  `AllWrote()`/`AllPicked()` рахують лише присутніх; нуль присутніх → `false` (партію тоді вже закрив `OnLeave`).
- `TickResult.Both` розсилає і кадр (§4.3), і види. Кадр за тик не шлемо (`None`, поки нічого не сталось): відлік клієнт веде сам
  від `endsAt`. Це і є «`Frame = false`, коли нічого не змінилось».
- Бюджет: середній `Tick()` на 8 гравцях **≤ 0,02 мс**, у порожньому тику — жодної алокації до `return None`. `BuildOptions` і
  сортування розкриття — раз на питання, там LINQ дозволений.

### 2.4 Перевірка тексту — `BluffText` (окремий `static class`, чистий, без стану; це найтестованіша частина гри)

```
Norm(s):   Unicode NFC → ToLowerInvariant → апострофи ’ ʼ ` ´ → ' → усе, крім літер, цифр, пробілу, ' і -, → пробіл →
           пробіли злити в один → Trim.
Tokens(s): Norm(s).Split(' ', '-') без порожніх.
Lev(a,b):  відстань Левенштейна (класична, без транспозицій), O(len·len), масиви можна алокувати — це не гарячий цикл.
TokenMatch(a, b):  a == b
                || (min(len) ≥ 4 && Lev ≤ 1)                                   // одруківка: гасі/гасу, Йорк/Йорку
                || (min(len) ≥ 4 && CommonPrefix ≥ 3 && Lev ≤ 2)               // відмінок: гасі/гасом, салі/салом
                || (min(len) ≥ 5 && CommonPrefix ≥ max(4, min(len) − 2))       // довші відмінки: кроликів/кролики, лампи/лампами
AllIn(need, have): кожен токен need має свій (ще не використаний) TokenMatch-токен у have — мультимножина, жадібно за порядком.
Letters(s): кількість літер і цифр у Norm(s).
LooksTrue(lie, question): для кожної форми f з [Answer] + Accept:
                Norm(lie) == Norm(f)
             || AllIn(Tokens(f), Tokens(lie))                       // правда всередині фрази: «звісно ж, на гасі»
             || (AllIn(Tokens(lie), Tokens(f)) && Letters(lie) ≥ 3) // шматок правди: «Буг» для «Південний Буг», «свинку» для «морську свинку»
LooksSame(a, b): Norm(a) == Norm(b) || (Tokens(a).Count == Tokens(b).Count && попарно TokenMatch)
Clean(s):  керівні символи → пробіл, пробіли (і нерозривні) злити, Trim; ЛИШЕ це — регістр і пунктуацію автора зберігаємо, картка
           показує текст як написано.
```
Перевірочні пари (стають тестами): правда «гасі» (accept «гас», «гасові лампи») — відкидаються «гасі», «Гасі!», «на гасі», «гасу»,
«гасом», «гасових лампах», а також «газі» (Lev 1 при довжині 4 — одруківка правди); приймаються «свинячому салі», «нафті», «спирті».
Правда «Південний Буг» — відкидаються «буг», «Пд. Буг» (токени «пд», «буг»: «буг» ⊆ форми, літер ≥ 3), приймаються «Дніпро»,
«Десна». Правда «2 метрів» (accept «2 м», «два метри», «двох метрів») — відкидаються «2 метри» (Lev «метри»/«метрів» = 1), «два
метри», «2 м» (є в accept); приймається «2 км» («км» ≠ «метрів»; без «2 м» в accept воно теж пройшло б — синоніми й скорочення
ловить лише банк, не евристика). `LooksSame(«свинячому салі», «Свинячому салі!»)` — так; («салі», «салі свинячому») — ні (порядок).

Відмова, коли `LooksTrue`: **«Схоже, ти випадково написав правду — вигадай іншу 🙂»** (одна фраза на всі випадки; вона в
однині чоловічого роду навмисно — це звертання «ти», не розповідь про гравця).

### 2.5 Вихід посеред партії, F5, «Ще раз», один гравець

- `OnLeave(seat)`: `_present[seat] = false`, `_dirty = true`. Його брехня (якщо картки вже складено) лишається на столі з його
  ніком зі знімка; його голос лишається; очки йому більше не йдуть (єдина точка нарахування `Credit(seat, points)` пропускає
  неприсутніх), у переможцях його нема. Якщо присутніх лишилось **менше двох** — партія закінчується **нічиєю**: `_phase = done`,
  `Finish([], "Байкарі: гравці розійшлись, партію не дограли")`. Нічия, а не перемога останнього: за неявку перемог не дають
  (так у Skilky й Мафії), і дві вкладки одного ніка не фармлять «Першу перемогу».
- F5 посеред партії: каркас тримає місце 20 с; стан гри цілком у виді (моя брехня в `my.lie`, мій вибір у `my.pick`, мої ❤ у
  `my.likes`), тож клієнт після `room` малює все з нуля. Нічого зберігати не треба.
- «Ще раз» (`Rematch`): каркас обертає місця й кличе `Start()` — чистий стан, нові питання (пам'ять бачених не даст повторів,
  доки в банку є свіжі). Рахунок серії між партіями не ведемо.
- Один гравець: `MinPlayers = 2`, тож стіл не стартує (каркас: «Замало гравців, треба щонайменше 2»); стіл з одним у лобі каркас
  сам прибере за 30 хв. Дограний стіл із вільним місцем каркас відкриває новому гравцеві (reopen) — `Start()` усе скидає.

### 2.6 `Configure` і `CanStart`

- `Configure`: `questions` (5/7/10, інакше 7), `pace` (`BluffPace`), `cat` → `_cats = BluffCats.Parse`. Каркас уже звів чужі значення до
  типових (`Rooms.Effective`), тож тут лише розбір.
- `CanStart()`: якщо `BluffBank.All` за обраними темами порожній → `"У цих темах ще нема питань — обери інші теми"`; інакше `null`.
  Менше питань, ніж просили, — не біда: граємо стільки, скільки є (`of` у виді = справжня кількість).

## 3. Дії (`Act`) і payload

| action | payload | Перевірки (у цьому порядку) і тексти відмов | Успіх |
|---|---|---|---|
| `lie` | `{ "text": string }` або голий рядок | `done` → «Партію зіграно, тисни «Ще раз»»; фаза ≠ `write` → «Зараз не час брехати»; не присутній → «Ти вже не за столом»; `Clean(text)` порожнє → «Порожня брехня нікого не надурить»; довше за `MaxLie` → «Коротше — до 40 знаків»; `LooksTrue` → «Схоже, ти випадково написав правду — вигадай іншу 🙂» | `_lie[seat] = text; _auto[seat] = false; _dirty = true;` → `Accept("Записано: «{text}»")` |
| `lie` | `{ "auto": true }` | ті самі перші три; далі: перша заготовка з `question.Decoys`, яка не `LooksSame` з жодною поточною брехнею присутніх і не видана іншому як auto; нема → «Глек уже все вибрехав — пиши сам 🙂» | `_lie[seat] = decoy; _auto[seat] = true;` → `Accept("Глек підказав: «{decoy}». Можеш переписати")` |
| `pick` | `{ "i": number }` або голе число | `done` → як вище; фаза ≠ `pick` → «Зараз не час обирати»; не присутній → «Ти вже не за столом»; не число / поза `0..options.Count-1` → «Нема такої картки»; `options[i].by` містить seat → «Свою брехню обирати не можна 🙂» | `_pick[seat] = i; _dirty = true;` → `ActResult.Done` (без тоста: картка підсвітиться) |
| `like` | `{ "i": number }` | `done` → як вище; фаза не `reveal`/`score` → «❤ ставлять на розкритті»; не присутній → «Ти вже не за столом»; нема такої картки → «Нема такої картки»; картка ще не відкрита → «Цього ще не показували»; правда чи заготовка Глека → «❤ ставлять брехням гравців»; `by` містить seat → «Собі ❤ не ставлять 🙂» | перемкнути `_likedBy[i]` для seat; кожному співавтору `± LikePts` у `_scores` і `_likeDelta`; `_dirty = true;` → `ActResult.Done` |
| інше | — | «Тут так не ходять» | — |

- Нелегальна дія стан не змінює (тест: вид до/після однаковий).
- Числа читати терпимо: `{ i: 3 }`, `3`, `"3"` — усе годиться (`JsonElement.TryGetInt32`, рядок через `int.TryParse`).
- Обмеження каркаса: 10 `Act`/с; payload ≤ 8 КБ. Модуль шле `lie` лише на «Готово»/Enter і на 🎲, `pick`/`like` — на клік.

## 4. View і Frame

### 4.1 `View(seat)` (`seat == null` — глядач)

```ts
{
  phase: 'read'|'write'|'pick'|'reveal'|'score'|'done',
  q: number,                 // номер питання, з 1
  of: number,                // питань у партії (справжня кількість)
  final: boolean,            // останнє питання — ×2
  endsAt: string,            // ISO; у reveal — кінець поточного кроку
  phaseMs: number,           // повна тривалість фази/кроку — для дуги
  cat: string, catLabel: string,
  text: string,              // питання з ___ (є в усіх фазах, крім done — там '' і recap)
  nicks: (string|null)[8],   // знімок ніків зі старту
  present: boolean[8],
  wrote: boolean[8],         // хто вже має брехню (write) / мав (далі)
  picked: boolean[8],
  my: null | { lie: string|null, auto: boolean, pick: number|null, likes: number[] },   // лише своє місце; глядач — null
  options: null | Array<{
      i: number, text: string,
      mine: boolean,                 // true лише у власному виді автора; у чужих і в глядача — false
      by: number[]|null,             // місця авторів; null, поки картку не відкрито; [] — заготовка Глека
      picks: number[]|null,          // хто обрав; null до відкриття
      truth: boolean|null,           // null до відкриття
      decoy: boolean|null,           // null до відкриття
      likes: number }>,              // ❤ (0 до відкриття)
  revealed: number[],        // індекси карток у порядку відкриття (порожньо до reveal)
  note: string|null,         // «а насправді…» — лише коли правду вже відкрито
  quip: string|null,         // фраза Глека до правди (§6.6), разом із note
  scores: number[8], delta: number[8], likeDelta: number[8], victims: number[8],
  result: null | {
      winners: number[], scores: number[8],
      best: null | { q: number, text: string, by: number[], victims: number, likes: number },
      recap: Array<{ q: number, text: string, answer: string, note: string|null, best: null | { text: string, by: number[], victims: number } }> }
}
```

Що приховано від кого (**тести обов'язкові**):
- `my` — лише своєму місцю. У виді іншого місця і глядача текст моєї брехні **відсутній** у фазі `write` (немає ні в `options`, бо їх
  ще нема, ні деінде — перевірка пошуком підрядка по `Views.Text(view)`).
- У `pick` `options[].text` бачать усі однаково (це публічний стіл), але `by`, `picks`, `truth`, `decoy` — `null` у **всіх** видах,
  зокрема у власному; `mine` — `true` лише у власника картки. `my.pick` — лише свій; чужий вибір видно тільки через `picked[]`
  (галочка).
- У `reveal` заповнюються лише картки з `revealed`; решта — `null` (зокрема правда: до її кроку `truth` у всіх картках або `false`
  (відкриті брехні), або `null`).
- Глядач бачить те саме, що гравець, мінус `my` і `mine`. Після `done` — усе відкрито всім.
- Поле `turn` не потрібне (ходів нема, є спільний таймер) — статус пише модуль.

### 4.2 Приклад (фаза `reveal`, 3 гравці, вид місця 0, після другого кроку)

```json
{ "phase":"reveal","q":2,"of":7,"final":false,"endsAt":"2026-09-27T18:04:11.5000000+00:00","phaseMs":3000,
  "cat":"ukraine","catLabel":"Україна",
  "text":"У XIX столітті у Львові вулиці вперше освітлили лампами на ___",
  "nicks":["Оля","Петро","Ганна",null,null,null,null,null],
  "present":[true,true,true,false,false,false,false,false],
  "wrote":[true,true,true,false,false,false,false,false],
  "picked":[true,true,true,false,false,false,false,false],
  "my":{"lie":"свинячому салі","auto":false,"pick":3,"likes":[1]},
  "options":[
    {"i":0,"text":"китовому жирі","mine":false,"by":[],"picks":[],"truth":false,"decoy":true,"likes":0},
    {"i":1,"text":"олії з реп'яхів","mine":false,"by":[1],"picks":[2],"truth":false,"decoy":false,"likes":1},
    {"i":2,"text":"свинячому салі","mine":true,"by":null,"picks":null,"truth":null,"decoy":null,"likes":0},
    {"i":3,"text":"гасі","mine":false,"by":null,"picks":null,"truth":null,"decoy":null,"likes":0},
    {"i":4,"text":"самогоні","mine":false,"by":null,"picks":null,"truth":null,"decoy":null,"likes":0}],
  "revealed":[0,1],"note":null,"quip":null,
  "scores":[1000,1500,0,0,0,0,0,0],"delta":[0,500,0,0,0,0,0,0],"likeDelta":[0,100,0,0,0,0,0,0],"victims":[0,1,0,0,0,0,0,0],
  "result":null }
```
Розмір: на 8 гравцях і 9 картках ≈ 1,7 КБ; у `done` з recap на 10 питань ≈ 3,5 КБ (межа каркаса 32 КБ). Виміряти в тесті
`Views.Text(...)` і записати в «Як реалізовано».

### 4.3 `Frame()`

Публічний, без жодного рядка тексту, крім фази й ISO-часу:
```json
{ "phase":"pick","q":3,"step":0,"endsAt":"2026-09-27T18:04:11.5000000+00:00",
  "wrote":[true,true,false,false,false,false,false,false],"picked":[true,false,false,false,false,false,false,false],
  "scores":[1000,500,0,0,0,0,0,0] }
```
≈ 190 байт. Летить лише разом із `Both` (на зміну), не щотиком. Тест: серіалізований кадр не містить жодної брехні, правди й ніка.

## 5. Модель симуляції

Фізики нема. Стан — масиви на `Seats` (`string?[] _lie`, `bool[] _auto`, `int[] _pick` (−1 — не обрав), `long[] _scores`,
`long[] _delta`, `long[] _likeDelta`, `int[] _victims`, `bool[] _present`, `string?[] _nicks`) і список карток поточного
питання `List<BluffOption>` (`Text`, `List<int> By`, `bool Truth`, `bool Decoy`, `HashSet<int> LikedBy`), `int[] _order` (порядок
розкриття), `int _step`. Масиви створюються раз у конструкторі й чистяться `Array.Clear`/`Fill`. Час — лише `Ctx.Clock`,
випадковість — лише `Ctx.Rng`, у такому порядку: `Start` (тасування пулу питань) → на кожному `write → pick` (тасування карток) →
`quip` до правди (`Rng.Next(Quips.Length)` на переході до кроку правди). Отже, з тим самим сідом і тією самою послідовністю дій
(і тиків) партія відтворюється байт у байт (тест). Мережа: нічого не передбачаємо — усе покрокове; при лагах гравець просто бачить
менше секунд на дузі; після реконекту каркас перепідписує й шле вид.

## 6. Клієнт (`web/games/bluff.js`, `web/games/bluff.css`)

Усі класи й змінні — з префіксом `bluff-` / `--bluff-`. Малювання — DOM (як Skilky), без канвасу й rAF (крім дуги каркаса
`ui.timerArc`): вид приходить ≤ 30 разів на питання, тож перебудова невеликих блоків на кожну подію `room` — нормальна, але
**лише коли підпис змінився** (`dataset.sig`, як у Skilky: інакше кожен кадр згортав би `<details>`, а картка втрачала :hover).
Заміряти `update()` у headless Chrome: середнє за 300 викликів < 4 мс.

### 6.1 Розкладка (`root.innerHTML` у `mount`, далі лише оновлення)

```
.bluff                      max-width 960px, margin auto, width 100%
  .bluff-top                рядок: .bluff-no («Питання 3 з 7 · Історія»), .bluff-x2 («×2», лише final), дуга timerArc, кнопка 🔈/🔇
  .bluff-q                  питання великим (clamp(1.05em, 2.2vw, 1.5em)); пропуск — <span class="bluff-blank">…</span>,
                            у reveal туди підставляється текст відкритої картки (кожен крок), у кінці лишається правда зеленим
  .bluff-write [hidden]     (лише мені у write) input.bluff-in (type=text, maxlength=40, autocomplete=off, spellcheck=false,
                            enterkeyhint="done", placeholder «твоя брехня…») + .bluff-cnt («12/40») + button.primary «Готово»
                            + button.ghost «🎲 Хай Глек збреше» + .bluff-my («Твоя брехня: «…» · можна переписати, поки є час»)
  .bluff-who                чипи присутніх (колір місця + нік) з ✓ — хто написав (write) / хто обрав (pick); у reveal/score прихований
  .bluff-opts               сітка карток button.bluff-opt (grid: repeat(auto-fill, minmax(150px, 1fr)); ≥ 700px картки — minmax(210px, 1fr))
     .bluff-opt             .mine (моя: disabled, бейдж «твоя»), .on (мій вибір), .wait (у reveal ще закрита — притемнена),
                            .lie / .truth / .decoy після відкриття; усередині: .bluff-otext, .bluff-picks (чипи тих, хто обрав),
                            .bluff-by (бейдж: «🤥 Оля», «🤥 Оля і Петро», «🏺 Глек», «✅ Правда»), button.bluff-like («❤ 2»; .on — моє)
  .bluff-note [hidden]      «А насправді: {note}» + .bluff-quip (фраза Глека) — після відкриття правди
  .bluff-score              таблиця: .bluff-srow (нік, рахунок, дельти чипами: «+1000 ✅», «+500 🤥×2», «+100 ❤»); .win — переможці;
                            у done зверху .bluff-best («Найкраща брехня партії: «…» — Оля, 2 жертви ❤3») і <details class="bluff-recap">
                            «Як це було» (питання → правда → чия брехня взяла найбільше)
```
- Телефон 375: `.bluff-opts` — 2 колонки, картки ≥ 44 px заввишки, текст до двох рядків (`overflow-wrap:anywhere`); кнопки
  «Готово»/🎲 — у два рядки на всю ширину; горизонтального скролу нема (`scrollWidth == clientWidth`, тест cdp2 `--mobile`).
- ПК 1280 і Steam Deck 1280×800: 3 колонки карток; 9 карток — 3 рядки ≈ 300 px; усе вміщається без прокрутки картки. Full HD:
  те саме, `.bluff` 960 px центрований, шрифт питання 1.5em.
- Кольори місць: `--bluff-s0..7` у `:root` `bluff.css` (жовтий `#e0a94a`, зелений `#6fb86f`, рудий `#d97757`, сірий `#9aa3ad`,
  синій `#5b8dd6`, рожевий `#d97ab0`, бірюзовий `#7fc7c4`, фіолетовий `#b48ce0`) + чіп завжди з ніком (не лише колір).
  `.gseat.bluff-s4..7` — той самий колір у шапці картки каркаса. Решта кольорів — змінні теми (`--accent`, `--ok`, `--clay`, `--panel2`…).
- Анімації (усі гасяться `@media (prefers-reduced-motion: reduce)`): відкриття картки — `bluff-flip` 400 мс (rotateY); чипи «хто
  обрав» — `bluff-pop` 250 мс з затримкою `--n·60ms`; правда — зелене сяйво 600 мс; дельта в таблиці — `bluff-rise` 500 мс;
  текст у пропуску міняється з коротким fade 200 мс.

### 6.2 Керування

| Дія | Клавіатура | Пад (без `dirs`; кільце фокуса каркаса ходить по кнопках) | Палець / мишка |
|---|---|---|---|
| Писати брехню | фокус у `input` одразу (лише не-`coarse`, як у Skilky), Enter — «Готово» | Ⓐ на полі — фокус; на Steam Deck клавіатура Deck (Steam + Ⓧ); Ⓐ на «Готово» | тап у поле, екранна клавіатура; «Готово» |
| 🎲 Глек | — (кнопка; окремої клавіші не треба) | Ⓐ на кнопці | тап |
| Обрати картку | `Digit1..9`/`Numpad1..9` (`e.code`) — картка № n; стрілки ←→↑↓ — фокус між `.bluff-opt`; Enter/Space — обрати (нативно, це `<button>`) | хрестовина — кільце по картках, Ⓐ — обрати | тап по картці (можна змінити) |
| ❤ | `Digit1..9` у `reveal`/`score` — перемкнути ❤ на картці № n (лише відкритій) | Ⓐ на ❤ | тап по ❤ |
| Звук | — | — | 🔈 у `.bluff-top` (`localStorage['bluffMute']`) |

`onKey` повертає `true` (з'їдає) лише для цифр у `pick`/`reveal`/`score` і стрілок у `pick`; решту віддає каркасу (Esc — вихід).
`pad` у `register`: `{ hint: '{dpad} по картках · {a} обрати або ❤', when: (ctx) => ctx.mine && ctx.playing }` — без `dirs`/`a`,
лише підказка (PROTOCOL §3: гра без стіка `pad` може не оголошувати; тут він потрібен рівно для рядка підказки). Ⓑ не займати.
Номер картки для цифр — її порядок у сітці (`i + 1`), малюється дрібно в куті картки (`.bluff-n`), корисно й для «третя — правда!».

### 6.3 Статус (`status(ctx)`, фаза — з виду, а не з кадру, як у Skilky)

`read` → «Читай питання…»; `write` → мені без брехні «Пиши брехню й тисни «Готово»», мені з брехнею «Записано. Чекаємо на решту…»,
глядачу «Байкарі брешуть…»; `pick` → мені без вибору «Де правда? Обери картку», з вибором «Обрано. Можна передумати», глядачу
«Усі думають…»; `reveal` → «Розкриваємо…»; `score` → «Рахунок · далі питання {q+1}»; `done` → '' (каркас напише «Перемога: …»
або «Нічия»); лобі → ''.

### 6.4 Підсумок і «Ще раз»

У `done`: `.bluff-score` з коронами 🏆 у переможців, `.bluff-best`, `<details class="bluff-recap" open>` — раз відкритий людиною
стан не перебивати (порівнювати `dataset.sig`, не `innerHTML`). Кнопка «Ще раз» — каркаса. Після рематчу `ctx.frame` ще пів
секунди може нести кадр минулої партії: усі читання з кадру — через `fresh(ctx, v)` (`f.q === v.q && f.phase === v.phase`).

### 6.5 Звук (опційно, WebAudio-синтез, після першого жесту, гучність 0,08, вимикач 🔈)

Відкриття картки — короткий «блип» (square 220 Гц, 40 мс); правда — дві ноти (440 → 660 Гц, по 90 мс); «твою брехню обрали» —
низький «бойнг» (sine 160 → 110 Гц, 180 мс). Без файлів. Якщо `AudioContext` нема — мовчки.

### 6.6 Тексти

- `news` у `register`:
  ```js
  news: { v: '2026-09-27', title: 'Нова гра: Байкарі', items: [
    '🤥 Питання з пропуском і дивною правдою: впиши свою правдоподібну брехню (до 40 знаків)',
    '🔍 Потім усі шукають правду серед брехень — свою обрати не можна',
    '💰 Вгадав правду — +1000, кожен, кого надурила твоя брехня, — +500 тобі, ❤ за найсмішнішу — +100',
    '🎲 Нема ідей — «Хай Глек збреше»: він підкине брехню з банку, а очки за неї — твої',
    '⏱ Останнє питання — подвійне; коли всі натиснули «Готово», фаза не чекає таймера',
  ] }
  ```
- Фрази Глека до правди (`Bluff.Quips`, 10 штук, обираються `Ctx.Rng`, у виді `quip`; нік не підставляється — відмінків нема):
  «Отак-то. Правда буває дивнішою за брехню.», «І це не жарт — так і було.», «Хто вгадав — той сьогодні з нюхом.», «Не вірите?
  Загугліть після партії.», «Правда стояла поруч і мовчала.», «Ось вона, справжня. Решта — байки.», «Це чиста правда, хоч і звучить
  як брехня.», «Так буває: правда — найдивніша картка на столі.», «Байкарі старались, але правда — ось.», «Записуйте, на ярмарку
  розкажете.»
- Бейджі авторства: `🤥 {нік}` / `🤥 {нік} і {нік}` / `🏺 Глек` / `✅ Правда`; «хто обрав»: чипи ніків; нікого — «ніхто не повірив»
  сірим. Жодних дієслів у роді про гравця.
- Іконка (16×16, у стилі наявних — довгий ніс):
  ```html
  <svg class="gico" viewBox="0 0 16 16" aria-hidden="true">
    <circle cx="6.5" cy="8" r="5" fill="none" stroke="var(--accent)" stroke-width="1.8"/>
    <circle cx="5" cy="7" r="1" fill="var(--accent)"/>
    <path d="M9.5 8h5.5" stroke="var(--clay)" stroke-width="2" stroke-linecap="round"/>
  </svg>
  ```

## 7. Дані й контент

### 7.1 Файл і формат

`data/bluff/questions.json` — рівно формат §bluff з `CONTENT-FORMATS.md`: `{ "version": 1, "questions": [ { id, cat, q, answer,
accept[], decoys[], note, source } ] }`. У `.gitignore` — виняток `!data/bluff/` (§10). Стартовий банк для тестів — Додаток А
(16 питань); перевірений банк (~250) підкладуть на етапі виправлень **у тому самому форматі**, тож парсер має бути терпимим:
поля `accept`, `decoys`, `note`, `source`, `id` — необов'язкові (порожні за замовчуванням), невідома `cat` → `odd`.

### 7.2 `BluffBank` (як `SkilkyBank`)

`Lazy` на процес, `Paths.Resolve("data/bluff/questions.json")`, `Load(path)` для тестів; **ніколи не кидає**: нема файла/битий
JSON → порожній список. Запис відкидається мовчки, якщо: `q` порожнє або містить не рівно один `___`; `answer` порожнє. Тип
`BluffQuestion { Id, Cat, Q, Answer, Accept (string[]), Decoys (string[]), Note, Source; string Key }`, `Key` — 16 hex SHA-256 від
`Q.Trim()` (як `SkilkyQuestion.Key`: банк переписують і сортують, а переписане питання й так нове). `Forms` — `[Answer] + Accept`.

### 7.3 Вибір питань і пам'ять — `BluffSeen`

Копія `SkilkySeen` під своїм ім'ям і таблицею `bluff_seen(nick_key, q_key, seen_at, times)` (через `Db.With`, `Db` береться з
`Ctx.Services.GetService<Db>()` у `Start()`; без бази мовчить). `Pick()`: пул = банк за `_cats` → Фішер — Єйтс на `Ctx.Rng` →
`BluffSeen.Freshest(pool, q => q.Key, LastSeen(ніки присутніх), _questions)` (спершу не бачені ніким за столом, далі бачені
найдавніше; серед однаково свіжих — тасований порядок). `Mark` — на кожному `BeginRead` для присутніх. Не генералізувати
`SkilkySeen` — це файл іншої гри; своя копія ~100 рядків.

### 7.4 Ліцензії

Факти банку — суспільне надбання; `note` і `decoys` — власний текст авторів. `source` у гру не потрапляє (записка для людей).
Без жартів про війну, політику, релігію, хвороби, реальних приватних осіб.

## 8. Тести — `tests/Hlechyky.Tests/Games/BluffTests.cs` (+ `BluffTextTests.cs`)

Мінімум 45. Хелпери як у `SkilkyTests`: `Table(players, seed)`, `Until(h, phase)`, `Phase(h)`, `View(seat)`. Пам'ятай, що
`Ctx.Rng` смикається на старті, тож «потрібне питання» шукають перебором сідів (`Asking(...)`).

**Банк (5)**
- `The_starter_bank_has_at_least_fifteen_questions_with_one_blank_each` — ≥ 15, у кожному рівно один `___`, `answer` непорожній, `decoys` ≥ 2.
- `Question_ids_and_keys_are_unique`
- `Every_category_in_the_bank_is_known` — усі `cat` ∈ `BluffCats.All` (крім `all`).
- `A_missing_or_broken_bank_file_loads_as_empty_not_as_an_exception`
- `Unknown_category_falls_back_to_odd_and_a_question_without_blank_is_dropped`

**Текст, `BluffTextTests` (9)**
- `Norm_folds_case_apostrophes_punctuation_and_spaces`
- `The_exact_truth_is_a_truth` («гасі», «Гасі!»)
- `An_inflected_truth_is_a_truth` («гасу», «гасом», «гасових лампах»)
- `The_truth_hidden_inside_a_phrase_is_a_truth` («звісно ж, на гасі»)
- `A_piece_of_a_multiword_truth_is_a_truth` («Буг» для «Південний Буг», «свинку» для «морську свинку»)
- `An_unrelated_lie_is_not_a_truth` («свинячому салі», «нафті», «Дніпро»)
- `A_typo_away_from_the_truth_is_still_the_truth` («газі» проти «гасі»)
- `Numbers_match_by_digits_and_words_come_from_accept` («2 метри» ≡ «2 метрів»; «2 км» — ні)
- `LooksSame_ignores_case_and_punctuation_but_not_word_order`

**Стіл і фази (7)**
- `The_table_waits_for_the_host_and_shows_the_question_only_from_read` — у лобі `text` порожній.
- `Empty_topics_refuse_to_start_with_a_clear_message` (`CanStart`).
- `Options_questions_pace_and_topics_are_read_from_the_lobby`
- `Read_lasts_three_seconds_then_write_opens` (годинник).
- `Write_ends_early_when_everyone_has_a_lie`
- `Pick_ends_early_when_everyone_has_picked`
- `A_move_makes_the_next_tick_carry_the_fresh_views_and_a_quiet_tick_sends_nothing` (`TickResult.None` без змін).

**Брехня (8)**
- `A_lie_is_recorded_and_echoed_only_to_its_author`
- `A_lie_can_be_rewritten_until_the_phase_ends`
- `An_empty_lie_and_a_too_long_lie_are_refused_and_change_nothing`
- `The_truth_written_by_accident_is_refused_with_the_friendly_text`
- `A_lie_outside_the_write_phase_is_refused`
- `Hlek_lends_a_decoy_as_an_auto_lie_and_it_earns_points_like_any_lie`
- `When_decoys_run_out_the_auto_lie_is_refused` (4 гравці просять auto при 3 заготовках).
- `Someone_who_never_wrote_still_picks_and_likes_but_has_no_card`

**Картки (6)**
- `Two_players_get_five_cards_with_two_decoys`
- `Five_players_who_all_wrote_get_no_decoys`
- `Identical_lies_merge_into_one_card_with_both_authors_and_neither_can_pick_it`
- `A_decoy_that_matches_a_players_lie_is_skipped`
- `Card_order_is_the_same_for_every_seat_and_the_watcher`
- `The_same_seed_and_moves_give_the_same_cards_and_the_same_views` (детермінізм; інший сід — інший порядок).

**Вибір і розкриття (10)**
- `Picking_your_own_card_is_refused`
- `A_pick_can_be_changed_and_an_out_of_range_pick_is_refused`
- `Truth_pickers_get_a_thousand_each`
- `A_liar_gets_five_hundred_per_victim_and_co_authors_each_get_the_full_amount`
- `Victims_of_a_decoy_pay_nobody`
- `The_final_question_doubles_truth_and_fooling_but_not_likes`
- `Cards_are_revealed_from_least_to_most_picked_with_the_truth_last`
- `Reveal_steps_last_three_seconds_when_picked_one_and_a_half_when_not_and_four_for_the_truth`
- `A_like_adds_a_hundred_to_each_author_and_a_second_like_takes_it_back`
- `Likes_are_refused_before_the_card_is_revealed_on_own_cards_on_the_truth_and_after_the_score_phase`

**Приховане (6)**
- `During_write_no_other_view_and_no_frame_contains_my_lie` (пошук підрядка в `Views.Text`).
- `During_pick_nobody_sees_authors_picks_truth_or_decoy_flags`
- `During_pick_mine_is_true_only_in_the_authors_view`
- `During_reveal_unrevealed_cards_stay_null_including_the_truth_flag`
- `The_watcher_view_has_no_my_and_matches_the_spec_shape` (форма через `Views.Json`: перелік полів).
- `The_frame_is_small_and_carries_no_text` (розмір ≤ 300 байт, жодної брехні/ніка).

**Кінець, вихід, рематч (9)**
- `Seven_questions_end_with_the_top_scorer_winning_and_scores_in_the_event`
- `Equal_scores_give_several_winners_and_all_zeros_give_a_draw`
- `The_journal_line_names_the_best_lie_and_its_victims` («… · найкраща брехня — «…» (Оля, 2 жертви)»).
- `The_recap_lists_every_question_with_its_truth`
- `One_of_three_leaving_keeps_the_party_going_and_leaves_their_card_on_the_table`
- `The_second_of_two_leaving_ends_the_party_in_a_draw`
- `A_leaver_earns_nothing_and_cannot_win`
- `Rematch_gives_a_clean_state_and_marks_seen_questions` (з `Db` на `TempDb`: `bluff_seen` має рядки; без бази — тихо).
- `Fox_and_nose_achievements_are_requested_once_and_only_when_earned` (`h.Awards` з `ach:bluff-fox`, `ach:bluff-nose`).

**Контракт і перф (2)**
- `The_server_accepts_exactly_what_the_module_sends` — `lie {text}`, `lie {auto:true}`, `pick {i}`, `like {i}` (і голі число/рядок).
- `[Trait("Category","Perf")] [Collection(SerialPerf.Name)] Eight_liars_for_three_thousand_ticks_stay_cheap` — 8 гравців, на кожному
  питанні всі шлють `lie`, `pick`, `like`, 3000 тиків (≈ 25 хвилин гри, кілька партій через `Rematch`) — найкраща з трьох спроб
  < 1000 мс; у звіт — справжнє число і середнє на тик.

Перф-бюджет: `Tick` ≤ 0,02 мс середнє (8 гравців), `View` ≤ 2 КБ (pick/reveal) і ≤ 4 КБ (done), `Frame` ≤ 300 байт, клієнт
`update()` < 4 мс середнє за 300 викликів у headless Chrome, `scrollWidth == clientWidth` на 375.

Жива перевірка (cdp2.py, порти 9711–9714, ≤ 3 Chrome): партія на двох (1280) до кінця з «Ще раз»; партія на п'ятьох (двоє
Chrome + троє легких SignalR-ботів, `_tools/loadtest/load.py`), у ній: злиття однакових брехень, заготовка Глека, 🎲, ❤, вихід
одного посеред `pick`, F5 посеред `write` (брехня на місці); телефон 375 (`--mobile`) — без горизонтального скролу; Full HD 1920;
глядач (`seat: null`, `my: null`, у сирому JSON `room` жодної чужої брехні у `write`); `?deck=1` — підказка пада. Помилок у консолі
й у `srv.err` нема. Наприкінці `--kill` на кожному порту.

## 9. Ризики й свідомі рішення

1. **Однакові брехні зливаються, а не відкидаються.** Бриф каже лише про правду; сказати гравцеві «хтось уже так написав» —
   підказка, що брехня влучна. Fibbage робить так само: спільна картка, повні очки кожному співавтору.
2. **«Хай Глек збреше» (🎲) — додано.** На телефоні й Deck друкувати ліньки, а без картки гравець пів раунду просто чекає. Заготовка
   стає його брехнею з усіма очками; заготовок 2–3 на питання, тож у великій компанії четвертому Глек відмовить — це чесно й смішно.
3. **Фаза `read` (3 с) — додана.** Щоб питання не падало на голову посеред набирання минулої брехні й усі стартували водночас.
4. **Останнє питання без `score`** — одразу `done`: підсумок і так на екрані, зайвих 6 с не треба.
5. **Менше двох присутніх — нічия**, не перемога останнього (анти-фарм, як у Skilky/Мафії).
6. **Очки великі (1000/500/100)** — це шоу, «+1000» приємніше за «+10». У Журналі — «6 500» з пробілом.
7. **Черепків за очки нема** (у Skilky є `points/5`). Каркас платить за перемогу/нічию/участь. Можна додати одним `Ctx.Award`
   пізніше, коли побачимо темп гри; ризик фарму брехнею на двох акаунтах без стелі — тому зараз ні.
8. **Правда серед карток видна глядачам у `pick`** — це публічний стіл (гравці й так її бачать), прихована лише авторство й вибір.
9. **Перевірка правди — евристика.** Відмінки й одруківки ловимо (§2.4), синоніми — ні: за це відповідає поле `accept` банку.
   Хибне відкидання чесної брехні («салат» при правді «сало») можливе й дешеве — гравець пише іншу.
10. **Steam Deck: писати брехню — клавіатурою Deck** (Steam + Ⓧ на сфокусованому полі). У підказці пада про це не пишемо (рядок
    короткий), написано в spec і в «що нового» не треба — брат і так знає Deck.
11. **Ніки не відмінюються** — усі тексти побудовані в називному (бейджі «🤥 Оля», Журнал «(Оля, 2 жертви)»).
12. **Стартовий банк (Додаток А) — 16 фактів, кожен звірено на етапі проєктування скриптом через API Вікіпедії з текстом своєї
    `source`-сторінки** (uk.wikipedia; для лелеки — Вікісловник, для Отіса, марафону й морських свинок — en.wikipedia, бо українські
    статті цих подробиць не мають). Два питання першого варіанта («кетчуп як ліки» 1830-х і «кролики Наполеона» 1807) викинуто:
    факти популярні, але на Вікіпедії їх нема, тож фактчекеру нічим підтвердити. Занзібарську «найкоротшу війну» (38 хвилин, 1896)
    не брали свідомо — тема війни, хай і давньої. Банк — для тестів і першого запуску; інженер перед комітом ще раз проганяє `source`
    через `curl` до API Вікіпедії (не WebSearch). Справжній банк ~250 підкладуть автори й фактчекери у тому самому форматі.
13. **Лишено на потім:** голос Глека (TTS) для зачитування питань; глядацьке голосування; «командний» режим; черепки за очки;
    статистика «скільком за весь час замакітрив голову» у профілі.

## 10. Спільні файли (дописати, перелічити у звіті дослівно)

- `.gitignore`: рядок `!data/bluff/` після `!data/telephone/` з коментарем `# Банк питань «Байкарів».`
- `src/Hlechyky/Games/Economy/Achievements.cs`, блок у кінці каталогу:
  ```csharp
  // хвиля 2: bluff
  new("bluff-fox",  "Хитрий лис",     "Одна брехня в «Байкарях» надурила двох і більше", "🦊", 20),
  new("bluff-nose", "Нюх на правду",  "Вгадав правду в кожному питанні партії «Байкарів»", "👃", 25),
  ```
- `GamesSetup.cs` — **не потрібен**: банк — статичний `Lazy`, пам'ять бачених бере `Db` з `Ctx.Services`.

## Додаток А. Стартовий банк — `data/bluff/questions.json`

Кожен факт звірено скриптом через API Вікіпедії з текстом сторінки в `source` (uk.wikipedia; де українська стаття подробиці не
має — en.wikipedia чи Вікісловник); `decoys` і `note` — власний текст. Категорії покривають усі дев'ять.

```json
{ "version": 1, "questions": [
  { "id": "b0001", "cat": "ukraine", "q": "Найдовша річка, що тече лише територією України, — ___", "answer": "Південний Буг",
    "accept": ["пд буг", "буг", "південний буг"], "decoys": ["Десна", "Інгулець", "Ворскла"],
    "note": "806 км від Хмельниччини до Чорного моря, і жодного метра за кордоном; Дніпро й Дністер довші, але починаються не в Україні.",
    "source": "https://uk.wikipedia.org/wiki/Південний_Буг" },
  { "id": "b0002", "cat": "science", "q": "Найпоширеніший хімічний елемент у Всесвіті — ___", "answer": "водень",
    "accept": ["гідроген", "h"], "decoys": ["залізо", "кисень", "кремній"],
    "note": "Близько трьох чвертей усієї звичайної речовини — це водень; зорі з нього й горять.",
    "source": "https://uk.wikipedia.org/wiki/Водень" },
  { "id": "b0003", "cat": "odd", "q": "1518 року в Страсбурзі сталася епідемія ___", "answer": "танцю",
    "accept": ["танців", "танцювальна", "танцювальної чуми", "танець"], "decoys": ["сміху", "позіхання", "чхання"],
    "note": "Сотні людей танцювали на вулицях днями без упину; лікарі радили… танцювати далі, і для декого це скінчилось погано.",
    "source": "https://uk.wikipedia.org/wiki/Танцювальна_чума_1518_року" },
  { "id": "b0004", "cat": "nature", "q": "Серце креветки розташоване в ___", "answer": "голові",
    "accept": ["голова", "в голові", "головогрудях"], "decoys": ["хвості", "лапках", "панцирі"],
    "note": "У ракоподібних серце сидить у головогрудях — просто за головою, під панциром.",
    "source": "https://uk.wikipedia.org/wiki/Ракоподібні" },
  { "id": "b0005", "cat": "food", "q": "Кав'ярню «Під синьою пляшкою» у Відні 1680-х відкрив шляхтич із-під Самбора на прізвище ___", "answer": "Кульчицький",
    "accept": ["кульчицького", "кульчицьким", "юрій кульчицький", "юрій-франц кульчицький", "кульчицкий", "kulczycki", "kolschitzky"], "decoys": ["Сагайдачний", "Дорошенко", "Вишневецький"],
    "note": "Юрій-Франц Кульчицький, герой оборони Відня 1683 року, узяв каву з трофеїв османського табору; гірка віденцям не зайшла — тоді він додав цукру й молока, і так народилась «віденська кава».",
    "source": "https://uk.wikipedia.org/wiki/Юрій-Франц_Кульчицький" },
  { "id": "b0006", "cat": "lang", "q": "Українське слово «лелека» запозичене з ___ мов", "answer": "тюркських",
    "accept": ["тюркська", "турецької", "турецька", "тюркські", "тюркської", "османської"], "decoys": ["балтійських", "грецької", "романських"],
    "note": "Османською й турецькою лелека — leylek; звідти й наша «лелека», а «чорногуз» і «бусол» — свої.",
    "source": "https://en.wiktionary.org/wiki/лелека" },
  { "id": "b0007", "cat": "history", "q": "Перший ліфт із гальмом безпеки Еліша Отіс показав публіці 1854 року в ___", "answer": "Нью-Йорку",
    "accept": ["нью йорк", "new york", "ню йорку"], "decoys": ["Лондоні", "Парижі", "Чикаго"],
    "note": "На виставці в нью-йоркському Кришталевому палаці Отіс сам стояв на платформі й наказав перерубати трос — і не впав.",
    "source": "https://en.wikipedia.org/wiki/Elisha_Otis" },
  { "id": "b0008", "cat": "sport", "q": "Дивна довжина марафону — 42 км 195 м — усталилась після Олімпіади 1908 року в ___", "answer": "Лондоні",
    "accept": ["лондон", "london"], "decoys": ["Афінах", "Парижі", "Стокгольмі"],
    "note": "Трасу проклали від Віндзорського замку до королівської ложі на стадіоні — вийшло рівно 26 миль 385 ярдів.",
    "source": "https://en.wikipedia.org/wiki/Marathon" },
  { "id": "b0009", "cat": "world", "q": "У токійському метро працюють «осія» — люди, які ___ пасажирів у вагони", "answer": "заштовхують",
    "accept": ["штовхають", "запихають", "впихають", "підштовхують", "заштовхує", "штовхає"], "decoys": ["витягують", "рахують", "будять"],
    "note": "Осія в білих рукавичках у годину пік буквально впихають людей у переповнений вагон, щоб зачинились двері.",
    "source": "https://en.wikipedia.org/wiki/Passenger_pusher" },
  { "id": "b0010", "cat": "ukraine", "q": "Перший електричний трамвай на теренах сучасної України пустили 1892 року в ___", "answer": "Києві",
    "accept": ["київ", "києві", "kyiv"], "decoys": ["Львові", "Одесі", "Харкові"],
    "note": "Кінний трамвай не брав київських круч, тож на Олександрівському узвозі пустили електричний — перший у Російській імперії.",
    "source": "https://uk.wikipedia.org/wiki/Київський_трамвай" },
  { "id": "b0011", "cat": "science", "q": "Розправлена ДНК з однієї клітини людини має довжину близько ___", "answer": "2 метрів",
    "accept": ["2 м", "два метри", "двох метрів", "2 метри", "2м"], "decoys": ["2 сантиметрів", "20 метрів", "2 кілометрів"],
    "note": "Два метри нитки, згорнутої в ядро розміром кілька мікрометрів; у всьому тілі — мільярди кілометрів.",
    "source": "https://uk.wikipedia.org/wiki/ДНК" },
  { "id": "b0012", "cat": "food", "q": "До XVII століття морква була переважно ___ кольору, а помаранчеву вивели в Нідерландах", "answer": "фіолетового",
    "accept": ["фіолетова", "пурпурового", "пурпурна", "фіолетовий", "жовтого", "жовта"], "decoys": ["білого", "чорного", "синього"],
    "note": "Дика морква з Центральної Азії була фіолетова й жовта; помаранчеву закріпили голландські селекціонери.",
    "source": "https://uk.wikipedia.org/wiki/Морква" },
  { "id": "b0013", "cat": "odd", "q": "У Швейцарії законом заборонено тримати лише одну ___", "answer": "морську свинку",
    "accept": ["морська свинка", "морських свинок", "свинку морську", "свинку"], "decoys": ["золоту рибку", "черепаху", "канарку"],
    "note": "Морські свинки — соціальні тварини, і закон про захист тварин 2008 року вимагає для них товариства.",
    "source": "https://en.wikipedia.org/wiki/Guinea_pig" },
  { "id": "b0014", "cat": "history", "q": "Анна, донька Ярослава Мудрого, 1051 року стала королевою ___", "answer": "Франції",
    "accept": ["франція", "французька", "французькою", "france", "франків"], "decoys": ["Норвегії", "Угорщини", "Швеції"],
    "note": "Дружина Генріха I і мати короля Філіппа I; у французьких паперах вона — Anne de Kyiv, Анна Київська. Норвегія й Угорщина — то її сестри Єлизавета й Анастасія.",
    "source": "https://uk.wikipedia.org/wiki/Анна_Ярославна" },
  { "id": "b0015", "cat": "nature", "q": "Кров восьминога ___ кольору", "answer": "синього",
    "accept": ["синя", "блакитного", "блакитна", "голубого", "синій"], "decoys": ["зеленого", "жовтого", "прозорого"],
    "note": "Кисень у восьминогів переносить не гемоглобін із залізом, а гемоціанін із міддю — він і синій.",
    "source": "https://uk.wikipedia.org/wiki/Восьминоги" },
  { "id": "b0016", "cat": "sport", "q": "Перший чемпіонат світу з футболу 1930 року відбувся в ___", "answer": "Уругваї",
    "accept": ["уругвай", "uruguay"], "decoys": ["Бразилії", "Італії", "Аргентині"],
    "note": "Господарі й стали чемпіонами — перемогли Аргентину 4:2 у Монтевідео.",
    "source": "https://uk.wikipedia.org/wiki/Чемпіонат_світу_з_футболу_1930" }
] }
```

Перевірка банку тестом: 16 ≥ 15 питань, у кожному один `___`, категорій — усі дев'ять, ids унікальні.

---

## Як реалізовано

Гілка `wave2/bluff`, 27.09.2026. Усе з §1–§8 зроблено; нижче — файли, де й чому відступили від задуму, числа й як це
перевіряти. Коли текст вище й цей розділ розходяться — правий цей розділ.

### Файли

| Файл | Що там |
|---|---|
| `src/Hlechyky/Games/Impl/Bluff.cs` | гра: фази від тика, картки, розкриття, ❤, кінець, вихід, види й кадр |
| `src/Hlechyky/Games/Impl/BluffText.cs` | перевірка тексту (§2.4) — чиста статика |
| `src/Hlechyky/Games/Impl/BluffBank.cs` | `BluffQuestion`, `BluffCats`, `BluffPace`, `BluffBank` і `BluffBankSource` (банк для тестів) |
| `src/Hlechyky/Games/Impl/BluffSeen.cs` | пам'ять бачених `bluff_seen` (копія `SkilkySeen`, запис фоном) |
| `data/bluff/questions.json` | стартовий банк — Додаток А, 16 питань (перевірений банк на ~276 кладуть на етапі виправлень) |
| `web/games/bluff.js`, `web/games/bluff.css` | клієнт |
| `tests/Hlechyky.Tests/Games/BluffTests.cs` | 62 тести гри + `BluffPerfTests` (1 перф-тест у `SerialPerf`) |
| `tests/Hlechyky.Tests/Games/BluffTextTests.cs` | 13 тестів перевірки тексту |
| `docs/games/dev/bluff-bots.py` | легкі SignalR-боти для живої перевірки (див. «Як тестувати») |

Спільні файли — рівно §10: рядок `!data/bluff/` у `.gitignore` і дві ачівки блоком `// хвиля 2: bluff` у кінці каталогу
`Achievements.cs`. `GamesSetup.cs` не чіпали.

### Відхилення від задуму (і чому)

1. **Перевірка тексту (§2.4) — точніша за псевдокод**, бо жива гра й перевірений банк показали діри:
   - `Norm` ще й зводить латинські двійники кирилиці (`a c e i o p x y k`) і викидає невидимі знаки (нульовий пробіл,
     м'який перенос) та наголоси. Інакше «гaсi» з латинськими літерами чи «га\u200bсі» на картці читались би як «гасі»,
     а перевірка бачила б інше слово — правда проскакувала б на стіл.
   - `TokenMatch`: слова з цифрами — лише точно (1855 при правді 1854 — чесна брехня, а не одруківка); «довгий
     відмінок» — лише коли різниця довжин ≤ 3 (закінчення, а не нове слово: «hollywoodhills» ≠ «hollywood»).
   - «Шматок правди» зараховується, лише коли шматок — щонайменше половина змістовних слів форми: інакше форма банку
     «овоч, а не фрукт» робила б правдою «фрукт».
   - `LooksSame` не зважає на службові слова без напрямку (`в у на з із зі зо і й та а ж же`): «у хвості» і «хвості» —
     одна картка (у питанні й так стоїть «в ___»; жива гра дала саме такий дубль, коли Глек видав «хвості» поруч із
     чужим «у хвості»). «До/від/під/над/за…» — змістовні: «нахилитися до сонця» ≠ «нахилитися від сонця».
   - Брехня з самих смайликів нормалізується в порожнечу — такі порівнюються як написано (Clean, без регістру).
   - `Clean` прибирає невидимі знаки (крім ZWJ — на ньому тримаються складені смайлики).
2. **Заготовки банку ріже не `LooksTrue`, а `LooksSame` з будь-якою формою правди** (§7.2 казав лише про кривий запис).
   Прогін перевіреного банку (`content/bluff/verified-A..D.json`, 276 питань) показав: грубе сито гравців вирізало б
   чесні заготовки («сорок центів» при правді «сорокова формула» з accept «сорокова» — у WD-40 не лишилось би жодної,
   «котячись клубочком за вітром» при accept «за вітром», «фрукт» через «овоч, а не фрукт»). Заготовки пишуть і
   звіряють люди, тож геть летять лише близнюки правди («гасу» при «гасі») і дублі між собою. Кнопка 🎲 видає заготовку
   без перевірки `LooksTrue`, тож вона завжди працює. Нотатка для етапу контенту: у кількох питаннях accept задовгий
   («вітром», «отруту» — гравцеві «отруйні голки» сервер скаже «ти випадково написав правду»).
3. **Банк для тестів — `BluffBankSource` через `Ctx.Services`** (як фрази Зіпсованого телефону). Правила тестуються на
   своєму банку з десяти питань і не зламаються, коли автори перепишуть справжній; справжній банк перевіряють окремі
   тести (≥ 15 питань, один пропуск, ≥ 2 заготовки, дев'ять тем) і повна партія на ньому. У проді сервіс не
   реєструється — гра бере `BluffBank.All`.
4. **`BluffSeen.Mark` пише фоном** (черга `Task.ContinueWith` на пулі, по одному запису): позначка ставиться з тика, а
   спільні правила забороняють базу під замком кімнати. `LastSeen` — раз на партію в `Start()`, як у «Скільки?». Щоб
   «Ще раз» не обігнав фоновий запис (повний прогін тестів це таки впіймав), екземпляр пам'ятає свої позначки й
   домішує їх до прочитаного з бази — за тим самим столом повторів нема навіть без бази. Для тестів — `BluffSeen.Idle`.
5. **`result.left`** (нове поле виду): `true`, коли партія скінчилась нічиєю, бо гравці розійшлись. Клієнт пише
   «🚪 Гравці розійшлись — партію не дограли», а не «ніхто нікого не переграв».
6. **Відмова після кінця — «Партію зіграно, тисни «Ану ще раз»»**: так кнопка зветься в `main` після «Лад» (злиття
   хвилі 2 вже поміняло це в Під глеком і Забігу дня). Каркас у цій гілці ще каже «Ще раз», але до гри після `Finish`
   ходи однаково не доходять — рядок лише страховка.
7. **Клієнт не читає кадр** (`fresh(ctx, v)` не знадобився): кадр летить лише разом із видами (`TickResult.Both`), тож
   вид завжди свіжий; фаза, галочки й рахунок — з виду. `Frame()` лишився за контрактом (§4.3) — для каркаса й глядачів.
8. **Картки — великими літерами** (`text-transform: uppercase`), а кінцева крапка чи знак оклику на картці не
   показуються. Інакше «Гасі!» поруч із «ДЕСНА» з банку видавало б людську руку, і правду шукали б за регістром і
   пунктуацією, а не за змістом. Сервер зберігає текст як написано.
9. **Картки не вимикаються на розкритті**: Ⓐ/клік по відкритій чужій брехні ставить ❤ (цифра — теж), а кільце пада
   лишається на картках. У першому варіанті вимкнені картки зникали з-під кільця, і воно стрибало на «Встати» —
   один зайвий Ⓐ, і брат уже не за столом. Ще й на кожну нову фазу модуль ставить кільце сам (`HPad.focus`): у write —
   на поле брехні (Ⓐ відкриває клавіатуру пада), у pick — на першу чужу картку, у read — на питання (`data-pad-focus`,
   безпечна ціль). Моя картка в pick — вимкнена (кільце її оминає).
10. **`added: '2026-09-27'`** у `register` поруч із `news`: у `main` після «Лад» нова гра світиться «🆕 нова гра» саме з
    `added` (`news` там — лише для оновлень). У цій гілці каркас поле ігнорує.
11. **Макет**: рахунок праворуч колонкою 230 px, коли картка ширша за 820 px (container query), інакше — під картками;
    сітка карток міряє саму колонку гри (3 колонки на 1280, Deck і Full HD, 2 — на телефоні). Кнопки й поле — як у §6.1.
12. Дрібне: стрілки ↑↓ у виборі стрибають на рядок сітки й оминають свою картку; друкувати брехню можна почати
    одразу — перша літера сама йде в поле; клік по картці світиться одразу (оптимістично, до 1,5 с чекаємо вид).

### Числа (ця машина)

| Що | Бюджет | Вийшло |
|---|---|---|
| `Tick()` на 8 гравцях, разом із кімнатою й кадром на зміну | ≤ 0,02 мс (spec), ≤ 0,25 мс (COMMON) | 3000 тиків — 17–20 мс, **≈ 0,006–0,007 мс на тик** (найкраща з трьох спроб, 5 партій по 10 питань з усіма ходами) |
| алокації тихого тика | 0 | 10 000 тихих тиків — < 256 Б разом (0 на тик; тест) |
| кадр на 8 гравцях | ≤ 300 Б | **206 Б** |
| вид у pick/reveal, 8 брехень по 40 знаків | ≤ 2 КБ (spec) | **4,3 КБ** — кирилиця на дроті як `\uXXXX` (6 байт на літеру), самі 8 × 40 літер — 1,9 КБ; межа каркаса 32 КБ |
| вид у done з recap на 10 питань | ≤ 4 КБ (spec) | **10,7 КБ** (та сама причина); на живій партії вісьмох RoomView разом із шапкою столу ≤ 5,5 КБ |
| `update()` у headless Chrome, 300 викликів по 61 різному виду партії на вісьмох | < 4 мс | **0,22 мс** у середньому (p95 0,5, макс 1,4); на живій партії — 0,96 мс за 106 справжніх оновлень |

Бюджет виду в §4.2 був порахований без екранування кирилиці; 4–11 КБ раз на подію (≤ 2 рази на секунду) — копійки,
тож стискати вид не стали.

### Як тестувати

- Юніт-тести: `dotnet test tests/Hlechyky.Tests -v q -nologo --filter "FullyQualifiedName~Bluff"` — 76 тестів (62 гра +
  1 перф + 13 тексту). Перф окремо: `--filter "FullyQualifiedName~BluffPerfTests"` (у `ITestOutputHelper` — справжнє число).
- Сервер із копії збірки на своєму порту (AGENT-COMMON), стіл: `HGames.call('CreateRoom','bluff',{questions:'5',pace:'slow'})`.
- Боти: `C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe docs/games/dev/bluff-bots.py --port <порт> --room <id>
  --bots "Ганна:liar:пузаті хмарки,Іван:dice,Марта:liar:Пузаті хмарки!" --secrets "пузаті хмарки,Пузаті хмарки!" --watch Глядач
  --leave "Іван@2:pick"` — сідають, брешуть (dice — через 🎲), обирають, ставлять ❤, один встає посеред вибору; кожен
  перевіряє на дроті, що у write чужої брехні в його виді нема, а в pick `by/picks/truth/decoy` — `null`.
- Наживо (headless Chrome, `cdp2.py`) зіграно й передивлено знімками: партія на двох до кінця з «Ще раз» (1280), партія на
  п'ятьох (двоє Chrome + троє ботів: злиття «пузаті хмарки»/«Пузаті хмарки!» у спільну картку двох авторів, 🎲, ❤, вихід
  одного посеред вибору, F5 посеред write — брехня в полі й у рядку «Твоя брехня»), дві партії на вісьмох (Chrome + 7
  ботів; глядач у Chrome і Python-глядач — ані `my`, ані чужої брехні в сирому JSON `room`), телефон 375 (`--mobile`,
  без горизонтального скролу, картки по дві, довга брехня переноситься), Full HD 1920, Steam Deck 1280×800 з `?deck=1` і
  `fakepad.js` (підказка «✥ по картках · Ⓐ обрати або ❤», кільце по картках, Ⓐ — вибір і ❤, 🎲 з кільця), клавіатура
  (Enter у полі, стрілки, цифри — вибір і ❤), вихід другого з двох через grace — нічия «розійшлись». Помилок у консолі
  й у `srv.err` нема. Боти на дроті: 0 витоків за ≈ 2 000 видів.

### Що не вдалось / лишено

- Евристика `TokenMatch` усе ще ловить хибні «відмінки» на шестилітерних словах зі спільним коренем («отруйні» ≈
  «отруту»): гравець отримає «ти випадково написав правду» і напише інше — так і задумано (§9.9), але це видно.
- Звук перевірено лише на відсутність помилок (headless з `--mute-audio`); на слух — ні.
- Черепків за очки нема (§9.7) — за перемогу й участь платить каркас.
