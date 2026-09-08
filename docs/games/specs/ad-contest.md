# Озвуч рекламу (`ad-contest`) — пункт 73

Не гра-кімната в лобі, а **конкурс** — окрема панель у вкладці «Ігри» (група Party, картка «Реклама глека»)
з власним HTTP API і сховищем у БД. Реалізується як `Game` з `Id="ad-contest"`, `MaxPlayers=1`,
`Private=false`, `Persistent=true`, `Start=Immediate`? — Ні. Кімнатна модель тут не пасує (учасників багато,
конкурс триває дні). Тому: **сервіс `AdContest`** + ендпоінти + клієнтська панель, **без** класу `Game`.
Клієнт: `web/games/ad-contest.js` реєструє «панель» через `HGames.registerPanel({ id, title, icon, mount, update })`
— WP2 передбачає такий хук (панелі поруч із «Профіль»/«Таблиця»/«Щоденний глек»). Якщо хука нема —
модуль вставляє свою картку в групу Party через `HGames.registerTile(...)`; узгодити з тим, що зробив WP2
(прочитати `core.js`).

## Життєвий цикл конкурсу

- Таблиці: `ad_contests(id, script, created_at, closes_at, closed INTEGER, winner_nick_key)`,
  `ad_entries(id, contest_id, nick_key, nick, track_id, created_at)`, `ad_votes(contest_id, voter_key, entry_id, created_at, PK(contest_id, voter_key))`.
  DDL — у власному файлі `Impl/AdContestStore.cs` (виконує `CREATE TABLE IF NOT EXISTS` на старті через
  своє з'єднання до `data/hlechyky.db`; `Db.cs` не чіпати).
- **Сценарій.** При відкритті конкурсу сервер отримує текст реклами: якщо `DjBrain.FlavorAsync` дає
  відповідь — просимо «рекламу глиняного глека на 15–20 секунд, весело, українською, без назв брендів»;
  інакше — один із 12 вбудованих шаблонів (написати смачно: «Глек — це не посуд, це стан душі…»). Сценарій
  показується учасникам.
- **Відкриття:** адмін тисне «Новий конкурс» (`POST /api/ads/new`, тільки admin) або автоматично щопонеділка
  12:00 Києва, якщо активного нема (`AdContestTicker` — hosted service, перевірка раз на хвилину за `IClock`).
  Тривалість — 3 дні (`closes_at`), потім `closed`.
- **Участь:** `POST /api/ads/{id}/entry` — тіло як у `/api/voice` (сирий запис із браузера); сервер кличе
  `VoiceService.SaveAsync` (той самий ffmpeg-конвеєр, ліміт 30 с — `-t` через окремий параметр? `SaveAsync`
  ріже за `MaxSeconds`; для реклами приймаємо і ріжемо до 30 с на клієнті (`MediaRecorder` зупиняється на
  30 с) — сервер додатково перевіряє тривалість ≤ 35 с, інакше 400). Один запис на ніка на конкурс (новий
  замінює старий, старий файл видаляється).
- **Голосування:** `POST /api/ads/{id}/vote {entryId}` — один голос, не за себе, можна змінити. Прослухати —
  `/api/voice/<track_id>.mp3` (уже є).
- **Закриття:** найбільше голосів; рівність — раніший запис. Виплати через `IStakes.Grant` (ref
  `ad:<id>:winner:<nick>`, `ad:<id>:entry:<nick>`, `ad:<id>:vote:<nick>`): 25 / 3 / 1. Ачівка `ad-winner`
  через `GameEvents.Raise(new AwardEvent(...))` (WP1 слухає `Awarded` для ачівок). Журнал: «🎙 Конкурс реклами:
  переміг Петро — 7 голосів. Його реклама тепер крутиться в ефірі».
- **Ефір.** Переможна реклама стає **джинглом**: сервіс підписується на `RadioEngine.TrackStarted` і після
  кожних `Ad:EveryTracks=6` треків (лічильник у пам'яті), не частіше ніж раз на `Ad:MinMinutes=25` хв і лише
  коли `Presence.Count > 0`, кличе `engine.AddVoice(track, path, "Дядько Глек")` з `TrackInfo(Id=<track_id>,
  Title="Реклама глека", Artist=<нік переможця>)` — реклама стає в кінець черги як звичайне голосове. Це
  єдиний дотик до ефіру, через уже наявний публічний метод. Вимикається `Ad:Jingle=false`.
- Налаштування `Ad` у `Config.cs`/`appsettings.json` (власний клас `AdOptions` в `Impl/AdContest.cs`,
  реєстрація `Configure<AdOptions>` — у `GamesSetup`? Ні, спільний файл. Робити в статичному методі
  `AdContest.AddTo(IServiceCollection, IConfiguration)` і… хто його кличе? Виняток: G-adcontest додає **один
  рядок** у `EconomySetup.AddHlechykyEconomy`/`MapHlechykyEconomy` — після інтеграції хвилі 1 це вже стабільні
  файли; конфлікт з іншими іграми неможливий, бо лише ця гра їх чіпає. Те саме для `AdContestTicker`.)

## HTTP

- `GET /api/ads` → `{ active: { id, script, closesAt, entries: [{ id, nick, trackId, votes, mine }], myVote, myEntry } | null, past: [{ id, winner, votes, closedAt, trackId }] (≤ 10) }`
- `POST /api/ads/new` (admin), `POST /api/ads/{id}/entry` (аудіо), `DELETE /api/ads/{id}/entry` (свій),
  `POST /api/ads/{id}/vote { entryId }`, `POST /api/ads/{id}/close` (admin).

## Клієнт

- Панель: сценарій великим текстом у «рамці афіші», кнопка «🎙 Записати рекламу» (той самий UI запису, що
  в app.js — **скопіювати** потрібні функції в модуль, не імпортувати з app.js; ліміт 30 с), список записів
  із ▶ і кнопкою «Голосую», лічильник голосів, таймер до закриття, минулі переможці.

## Тести (≥ 15)

- Вибір шаблону без LLM; закриття: більшість/рівність; один голос на ніка, не за себе; заміна запису
  видаляє старий файл (FakeVoice — тест через інтерфейс `IVoiceSaver`, який обгортає `VoiceService`);
  виплати через `IStakes` фейк із правильними refs (двічі закрити — не подвоює); джингл: після 6 треків і
  не частіше 25 хв, і лише коли є онлайн (FakeClock, фейковий `Presence` — реальний `Presence` простий,
  можна справжній); автоматичне відкриття щопонеділка.
