# Щоденний глек (`daily`) — пункт 84

Не окрема гра, а каркас навколо щоденних соло-головоломок. Сервісна частина — WP1 (`Economy/Daily.cs`),
панель — WP2 (`core.js`), а G-wordle доводить обидві до ладу на реальних іграх (перша — Глек-слово, друга —
Сапер дня від G-mines).

## Сервіс `Daily` (WP1)

Час, сід і номер дня вже є в контракті — статичний `Days` у `Contracts.cs` (`Days.Today(clock)`,
`Days.Seed(puzzleId, day)`, `Days.NextMidnight(now)`, `Days.Number(day)`), маркер `IDailyGame` — там само.
`Daily` — тонкий сервіс над ними:

```csharp
public sealed class Daily(IClock clock)
{
    public string Today() => Days.Today(clock);
    public IReadOnlyList<string> Puzzles;           // Id ігор, що реалізують IDailyGame (свій скан збірки, щоб не залежати від Registry WP0)
    public DailyStatus Status(string nick);         // те, що віддає GET /api/games/daily
}
```

Таблиця `daily_results(day, game, nick_key, nick, solved INTEGER, attempts INTEGER, ms INTEGER, created_at, PRIMARY KEY(day, game, nick_key))` —
пишеться з `SoloScoreEvent`, де `Key` = `daily:<game>:<day>:<nick_key>` (гра передає ключ кімнати). Серія
(streak) = кількість послідовних днів до сьогодні з `solved=1` для гри.

HTTP `GET /api/games/daily` →
```ts
{ day, no, nextMidnight,
  puzzles: [{ game, title, me: { solved, attempts, ms } | null, streak, solvedCount,
              top: [{ nick, attempts, ms }] (≤ 10, за attempts↑ потім ms↑) }] }
```

## Панель (WP2)

У вкладці «Ігри» — окрема кнопка/вкладка «Щоденний глек»: картки кожної щоденної гри (назва, «розв'язано
за N спроб / не розв'язано», серія 🔥N, «N людей уже розв'язали»), кнопка «Грати» → `OpenSolo(game)` →
кімната відкривається на тій же вкладці (соло-кімнати малюються так само, як звичайні, але без чіпів місць
і без «Дивлюсь збоку»). Топ-10 дня під карткою.

## Нагорода

`DailyReward=5` черепків за розв'язану головоломку (гра кличе `Award` з `reason "daily:<game>"`, стеля — раз
на день через ref). Ачівки `wordle-2`, `wordle-7`, `mines-fast` — WP1 за `SoloScoreEvent`.

## Тести (WP1: ≥ 8; G-wordle: інтеграція)

- `Today()` на 20:59 UTC 09.09 → «2026-09-09»; на 21:01 UTC → «2026-09-10» (Київ = UTC+3 у вересні).
- `Seed` стабільний і різний для різних puzzleId/днів.
- Streak: 3 дні поспіль → 3; пропуск учора → 0 (або 1, якщо сьогодні розв'язано).
- `daily_results` upsert не дублює; топ сортується за спробами, потім часом.
