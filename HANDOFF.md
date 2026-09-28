# HANDOFF — пакет hulianka (прохід №3), реалізатор №1 → наступник

Гілка `sweep3/hulianka`, worktree `D:/or-wt/s3-hulianka`, сервер 8320, cdp 9520. Спільні правила —
`D:/or-wt/_sweep3/AGENT-COMMON-sweep3.md`; звіт — `D:/or-wt/_sweep3/reports/hulianka.md` (розділи 186/188/189/190 уже є).

## Зроблено (коміти f1415d9, d1badc8, 857a142, 7aa952f + spec)
- **189 кубок тижня** і **188 привиди друзів** (Забіг дня) — `Impl/DinoDailySetup.cs`, `DinoDaily` у `Dino.cs`, клієнт
  `runner.js` (`friendGhosts`, `ghostDecode`, `cupHtml`, `ghostsHtml`). Тести `DinoDailyGhostsTests` (8, зелені).
- **186 дух лавини** (Стрибозаври, опція `spirit`, типово off) — `Dino.cs` (`Drop`), клієнт (`isSpirit`, `spiritDrop`,
  `spiritNext`, `avengerHtml`). Тести `DinoSpiritTests` (3). Перевірено в Chrome з ботами: статус «👻 Ти — дух лавини…»,
  кнопка «👻 N с», опція в шапці столу.
- **190 ключ лелек** (Лелеки, опція `key`, типово off) — `RunnerSim.cs` (`KeyOn`, `DecideKey`, `InKey`, `KnowKey`,
  `WireKey`), `Storks.cs`, `RunnerFrame.Ky`, JS-дзеркало `Sim` (`inKey`, `knowKey`), `knowKeys`, пунктир, `keyHtml`.
  Тести `StorksKeyTests` (4). Chrome з ботами: кадр ≤ 213 Б, `ky` приходить, «🪽 Найвірніша в ключі» у підсумку.
- news модуля `runner.js` для dino / dino-daily / storks (v 2026-09-29); spec dino, dino-daily, storks — «Прохід №3».
- `docs/games/dev/runner-bots.js` — боти (окремий WS): `rb.bot(nick, {spirit, jump, flap, low})`, `b.join(roomId)`.

## Лишилось (по порядку)
1. **Юрма 124 смішинки на розкритті** (S) — `Crowd.cs`/`CrowdCore.cs` + `crowd.js`; лише з даних, які сервер уже має.
2. **Юрма 121 детектив** (M) — вибулі й глядачі клацають по селянину «це Оля?», на розкритті «🕵 Петро вгадав трьох»,
   +1 очко вибулим за влучну. Глядач не бачить id — лише спільна здогадка; не пхати в балачку.
3. **Замри 195 естафета** (M) — режимом/опцією (типово як зараз): дві команди, торкнувся глека — очко команді, на старт.
4. **Естафета у Стрибозаврах** — вирішено НЕ робити (записано в spec dino.md і треба — у звіт розділом «232»).
5. Живий огляд Забігу дня в Chrome: привиди друзів на сцені й рядки «🏆 Кубок тижня» / «👻 Поруч біжать» у вікні після
   спроби (бот-скрипт `qa/t-daily.js` не зловив вид соло-кімнати — `OpenSolo` відповідь/`room` приходить до `b.room`;
   простіше: перша спроба ботом через `Act('in', {s:0,k:5})`, друга — людиною у вкладці, `#games` → Соло → Забіг дня).
6. Самоперевірка за завданням: `git diff sweep3/main` перечитати; телефон 390×664 (`--mobile`) і 1280×800 з
   `--fakepad` для runner (кнопка «👻», рядок кубка під таблицею на телефоні — чи не переповнює вікно).
7. Повний `dotnet test` (≈5300), звіт (розділи 121/124/195/232, «Каркасу», «Тести»), зупинити сервер, `--kill` Chrome.

## Пастки
- `web/games/runner.js` і більшість `.cs` — **CRLF**; `grep -c $'\r$'` у Git Bash бреше (0). Правлю Python-скриптами з
  `a.replace('\n', '\r\n')`; Edit-інструмент теж працює. Апостроф у JS-рядку в одинарних лапках — лише `’`.
- Після правки JS браузер cdp тримає старий `?v=` модуля (хеш `/api/front` оновлюється не одразу) — `cdp2.py --kill`.
- `node` на машині нема — синтаксис JS перевіряй завантаженням у Chrome (cdp2 друкує `EXCEPTION SyntaxError`).
- Боти: ніки мусять бути свіжі (інакше «ти вже за столом»/`JoinRoom` false) — `'Бот' + Date.now() % 1000`.
- Тест-фільтр без шуму попереджень: `dotnet test ... --filter X 2>&1 | grep -v warning | grep -E "Passed!|Failed|Assert|Expected|Actual|\.cs:line"`.

## Команди
```
dotnet build src/Hlechyky/Hlechyky.csproj -v q -nologo -nodeReuse:false 2>&1 | grep " error "
dotnet test tests/Hlechyky.Tests -nologo -nodeReuse:false --filter "FullyQualifiedName~Dino|FullyQualifiedName~Storks|FullyQualifiedName~Runner"
cat docs/games/dev/runner-bots.js qa/t-dino.js > qa/run-dino.js
C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe D:/or-wt/_tools/cdp2.py --port 9520 --url "http://127.0.0.1:8320/?cb=9#games" --nick Ярка --js qa/run-dino.js --width 1280 --height 800
```
Сервер (PowerShell) — як у AGENT-COMMON; PID у `srv.pid`.
