# План робіт: ігрова платформа + 23 пункти

Гілка інтеграції — `games/platform` (worktree `D:\or-games`). `main` не чіпаємо до ручної перевірки
власником. Кожен робочий пакет (WP) — своя гілка `games/<wp>` у своєму worktree `D:\or-wt\<wp>`,
відгалужена від актуального `games/platform`. Злиття в `games/platform` робить оркестратор.

## 0. Правила для всіх агентів (обов'язкові)

1. **`D:\or` — прод. Туди не заходимо, там нічого не редагуємо, звідти нічого не запускаємо.** `web/` там
   віддається наживо; один збережений файл — і друзі бачать зламаний сайт. Працюємо ТІЛЬКИ у своєму worktree.
2. Worktree створюємо так (з будь-якого місця):
   `git -C D:/or worktree add D:/or-wt/<wp> -b games/<wp> games/platform`
   Далі всі команди — з `D:/or-wt/<wp>`. Наприкінці — `git add -A && git commit` у своїй гілці. Не пушимо.
   Гілку `main` і `games/platform` не чіпаємо (ні merge, ні checkout).
3. **Ніколи** `Stop-Process -Name dotnet`, `taskkill /IM dotnet.exe`, `Get-Process dotnet | Stop-Process`:
   так вбивається живий сервер радіо. Свій сервер (якщо треба) — тільки на порту зі свого
   `appsettings.Local.json` (`Site:ListenPort`, у worktree ставимо 8091+номер WP, див. п. 6), запускати
   `Start-Process` з `-PassThru`, зупиняти строго за своїм PID. `start.ps1`, `deploy.ps1`, `docker` — не запускати.
4. Коміти: українською, коротко, по суті, як у `git log`. **Без** трейлера `Co-Authored-By` і без згадок
   Claude/AI у повідомленнях.
5. Коментарі в коді — українською, у стилі наявних файлів (пояснюють «чому», а не «що»). Тексти для гравця —
   українською, без канцеляриту. Ніяких емодзі-феєрверків у повідомленнях сервера.
6. Спільні файли (`Program.cs`, `RadioHub.cs`, `app.js`, `index.html`, `style.css`, `Db.cs`, `core.js`,
   `Contracts.cs`) правлять лише ті WP, за якими вони закріплені нижче. Гра, якій «треба щось у каркасі», пише
   про це в `docs/games/specs/<id>.md` у розділ «Потрібно від каркаса» і робить обхід у своїх файлах.
7. Здача = `dotnet build` чисто, `dotnet test` зелено, spec доповнено розділом «Як реалізовано», ручна перевірка
   за TESTING.md §5 виконана і описана в останньому коміті або в spec.
8. Нічого не качати з інтернету в репозиторій, крім словника (WP4) з явно вказаною ліцензією.
9. `dotnet build` викликати з `-nodeReuse:false`, щоб MSBuild-ноди не тримали файли між worktree.

## 1. Хвиля 0 — скелет (оркестратор, зроблено до старту агентів)

- Документи в `docs/games/`, `Contracts.cs` (типи контракту), `GamesSetup.cs`/`EconomySetup.cs` (порожні
  розширення, що їх уже кличе `Program.cs`), проєкт тестів із димовим тестом, CI з `dotnet test`,
  `appsettings.Local.json` у `D:\or-games` (порт 8090, ffmpeg із проду).

## 2. Хвиля 1 — платформа (паралельно, 4 агенти)

| WP | Гілка | Файли (власність) | Зміст |
|---|---|---|---|
| **WP0 core** | `games/wp0-core` | `Games/Room.cs, Rooms.cs, Registry.cs, Broadcaster.cs, TickEngine.cs, GamesSetup.cs, Impl/GridGame.cs, Impl/SnakeGame.cs`; правки `RadioHub.cs`, `Program.cs` (лише виклики), `RadioEngine.cs` (тільки `Presence.ConnectionsOf`); видалення `Games.cs`, `Snake.cs`; тести `Platform/Rooms*`, `TickEngine*`, `Broadcaster*`, `Games/GridGameTests`, `SnakeTests` | Кімнати, реєстр, розсилка, тик, хаб-методи з PROTOCOL §1–2, порт чотирьох наявних ігор. Економіку кличе через `IGameEvents`/`IEconomy` з Contracts. |
| **WP1 services** | `games/wp1-services` | `Games/Economy/Economy.cs, Ratings.cs, Leaderboards.cs, Achievements.cs, Daily.cs, EconomySetup.cs, Store.cs (IGameStore поверх Db)`; правки `Db.cs` (нові таблиці), `Endpoints` — ні, свої в `EconomySetup.MapHlechykyEconomy`; `DjBrain.cs` — додати `FlavorAsync`; `appsettings.json` + `Config.cs` (секція `Economy`); README таблиця налаштувань; тести `Platform/Economy*`, `Ratings*`, `Achievements*`, `Daily*` | Черепки, леджер, стелі, Ело, таблиці, ачівки, щоденний сід, `EconomyTicker` (онлайн-хвилини), HTTP `/api/games/leaderboard|profile|daily|wallet`, `DjBrain.FlavorAsync(string instruction, int maxChars)`. |
| **WP2 client** | `games/wp2-client` | `web/games/core.js, core.css`; правки `web/app.js` (вирізати старі ігри, підключити HGames), `web/index.html`, `web/static/style.css` (перенести блок ігор у core.css); модулі `web/games/ttt.js, c4.js, snake.js` (порт) | Каркас із ARCHITECTURE §10 і PROTOCOL §3, лобі, картка, профіль, таблиці, щоденна панель, гаманець, ачівка-тост, завантажувач. Працює проти протоколу; до злиття перевіряє себе на моках (`web/games/dev-mock.html` — не в проді). |
| **WP4 words** | `games/wp4-words` | `Games/Economy/Words.cs`, `Games/Economy/WordsSetup.cs` (DI — уже кличеться з Program.cs), `data/words/*`, `setup.ps1` (завантаження великого словника), `.gitignore`, тести `Platform/WordsTests` | Словники: `uk-5.txt` (відповіді Глек-слова, 1500–3000 поширених іменників), `uk-guess.txt` (усі допустимі 5-літерні форми), `uk-hangman.txt` (іменники 5–12 літер, 3000+), великий `uk-all` для Ерудита (усі словоформи, завантажується setup.ps1, з фолбеком на малі списки). Ліцензія — у `data/words/LICENSE.txt`. Пошук ≤ 1 мс, пам'ять ≤ 60 МБ. |

Після хвилі 1: злиття WP0+WP1+WP2+WP4 → **WP3 integration** (один агент): підняти сервер на 8090, зіграти
хрестики/чотири/змійку в двох вкладках, погасити розбіжності протоколу, зелені тести. Це база для хвилі 2.

## 3. Хвиля 2 — ігри (паралельно, кожна у своєму worktree від оновленого `games/platform`)

| WP | Пункти | Spec | Складність |
|---|---|---|---|
| G-chess | 22 | specs/chess.md | велика (перфт-тести, 960, піддавки) |
| G-checkers | 13 | specs/checkers.md | середня |
| G-battleship | 15 | specs/battleship.md | середня (Hidden) |
| G-mines | 17 + сапер дня | specs/mines.md | середня |
| G-scrabble | 23 | specs/scrabble.md | велика (потребує Words) |
| G-domino | 24 | specs/domino.md | середня (Hidden) |
| G-durak | 25 | specs/durak.md | велика (Hidden) |
| G-snake-modes | 28, 29 | specs/snake-modes.md | мала (розширює SnakeGame) |
| G-pong | 32 | specs/pong.md | середня (інтерполяція) |
| G-curve | 33 | specs/curve.md | середня |
| G-bomber | 34 | specs/bomber.md | велика |
| G-duel | 37 | specs/duel.md | мала-середня (вестерн-сцена) |
| G-territory | 38 | specs/territory.md | середня-велика |
| G-chatcmd | 39 + 40 | specs/chat-commands.md, specs/hangman.md | мала |
| G-wordle | 41 + 84 | specs/wordle.md, specs/daily.md | середня (Persistent, Daily) |
| G-skilky | 44 | specs/skilky.md | середня (банк запитань) |
| G-mafia | 46 | specs/mafia.md | велика (Hidden, фази, Глек) |
| G-adcontest | 73 | specs/ad-contest.md | середня (голосові, гачок ефіру) |
| G-clicker | 82 | specs/clicker.md | середня (Persistent) |

Кожна гра проходить конвеєр: **реалізація → два незалежні рецензенти (правила; інтеграція/UX/безпека) →
виправлення → повторна перевірка тестами**. Рецензент, який не знайшов нічого, пише «нічого» і чому.

## 4. Хвиля 3 — інтеграція, QA, документація

1. Злиття всіх `games/g-*` у `games/platform` (оркестратор), збірка, тести.
2. QA-агент: сервер на 8090, кожна гра руками в 2–3 вкладках (localhost / 127.0.0.1 / мобільний viewport),
   список знайденого → агенти-виправлячі → повтор.
3. Документація: README (розділ «Ігри» — що є, як грати, черепки, таблиці; таблиця налаштувань `Economy`),
   CONTRIBUTING («Як додати гру» → посилання на docs/games), `docs/games/REPORT.md` — що зроблено, що
   відкладено, як перевірити.
4. Фінальний стан: `games/platform` закомічена, worktree `D:\or-games` із сервером на 8090 для ручної
   перевірки власником. Злиття в `main` і деплой — рішення власника.

## 5. Порядок злиття і конфлікти

- Хвиля 1: WP4 → WP1 → WP0 → WP2 (від найменш до найбільш конфліктного). Очікувані конфлікти: тільки
  `Program.cs` (немає — виклики вже в скелеті) і `Db.cs` (лише WP1). `app.js` — лише WP2.
- Хвиля 2: усі гри додають лише свої файли; `Db.cs`/`core.js` не чіпають. Конфліктів не має бути; якщо є —
  гра зробила щось не по правилах, і це привід для рецензії.

## 6. Локальні порти й налаштування worktree

`appsettings.Local.json` у worktree (файл у `.gitignore`, створити руками):
```json
{
  "Site": { "ListenPort": 8091 },
  "YtDlp": { "FfmpegDir": "D:/or/tools/yt-dlp", "BinaryPath": "D:/or/tools/yt-dlp/yt-dlp.exe" },
  "Auth": { "AdminKey": "dev" },
  "Liquidsoap": { "Port": 11234 },
  "Icecast": { "StatusUrl": "http://127.0.0.1:18000/status-json.xsl" },
  "AutoDj": { "Enabled": false },
  "DjBot": { "Enabled": false },
  "Deploy": { "Enabled": false }
}
```

Останні п'ять рядків обов'язкові: liquidsoap проду слухає `127.0.0.1:1234` на цій самій машині, і сервер
із worktree без них знайде живий ефір і почне ним керувати. З ними ефір показує «↓» — саме те, що треба.
Порт: WP0 8091, WP1 8092, WP2 8093, WP4 8094, ігри — 8100 + порядковий номер у таблиці (chess 8101, checkers
8102, …, clicker 8119). Інтеграційний `D:\or-games` — 8090. Liquidsoap/Icecast у worktree не потрібні:
ефір буде «↓», ігри це не зачіпає.

## 7. Що свідомо відкладено (не в цій роботі)

- Скіп/пріоритет черги за черепки (економіка готова, продукт — потім).
- Закріплення ніка PIN-кодом.
- Турніри/сезони (79 не обрано).
- Мультиплеєрні кімнати, що переживають рестарт сервера.
