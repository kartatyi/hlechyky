# HANDOFF — пакет `geo` («Де це?»), прохід №3

Гілка `sweep3/geo`, worktree `D:/or-wt/s3-geo`, сервер 8307, cdp 9507. Звіт — `D:/or-wt/_sweep3/reports/geo.md`
(допиши туди розділ п. 52 і тести), spec — `docs/games/specs/geo.md` розділ «Прохід №3».

## Зроблено (етапи А і Б) — коміти 96a52dd, daa96e3, 365ad3a (+ звіт/spec)
51 пам'ять (`GeoSeen`), 50 запечатане наступне фото + `GeoShrink` (ffmpeg), 49 «Де це? дня» (`GeoDaily`), 54 дуель
(`mode=duel`), 53 підказка «область» (`area`) + детальна мапа (`geo-detail.json`, `build-detail.py`). Тести —
`tests/Hlechyky.Tests/Games/GeoSweep3Tests.cs` (8), усі Geo-тести зелені (152).

## Лишилось — етап В: п. 52 «Мої фото» (рішення користувача: БЕЗ модерації, адмін видаляє)
Запропонований дизайн (вирішено, можна брати як є):
1. **Сховище** `GeoMine` (новий `Impl/GeoMine.cs`, синглтон у `GeoSetup.AddGeo`): метадані — таблиця SQLite `geo_mine`
   (як `PictionaryStore`: id TEXT(12 hex), nick, nick_key, lat, lon, title ≤ 60, story ≤ 280, at, bytes), файли —
   `data/geo-mine/<id>.jpg` (каталог уже ігнорується `.gitignore` правилом `data/*`). Уся таблиця — у пам'ять на старті
   (фоном, як `GeoSeen`), гра під замком читає лише пам'ять. Ліміти: 20 фото на ніка, 60 МБ на все, файл ≤ 400 КБ.
2. **Ендпоінти** в `GeoSetup.MapGeo` (шаблон авторизації — `PictionarySetup.cs:64-100`: `Auth.Nick(c)`, `Auth.Guest`,
   `Auth.IsAdmin(c)`): `POST /api/games/geo/mine` (тіло — JPEG, параметри lat/lon/title/story; гість — відмова),
   `GET /api/games/geo/mine` (свої фото; адміну — усі, з `admin: true`), `GET /api/games/geo/mine/<id>.jpg` (лише власнику
   чи адміну — чужі фото це спойлер гри), `POST /api/games/geo/mine/delete` (власник чи адмін).
   **EXIF:** клієнт перетискає через canvas (1280 px, JPEG 0.8 → ~180 КБ; canvas і так губить EXIF), сервер **обов'язково**
   ще раз `GeoImage.Strip` (null → відмова «це не JPEG»). Координати — лише шпилька (lat/lon з мапи: клієнт шле сітку
   X,Y, сервер `GeoMap.Unproject`), перевірка `GeoMap.File?.RegionAt` — на суходолі України, інакше відмова.
3. **Гра:** опція столу `friends`: `off` (типово) / `mix` («домішувати» — до третини раундів) / `only` («лише наші»).
   `GeoMatch.Start`: фото друзів як `GeoPlace(Id "u:<id>", Name = title, Region = RegionAt(...), Cat "friends", …,
   Photos [GeoPhoto(title, "mine:<id>", nick, "", "", "")])` і `Round.Path` = файл із `data/geo-mine` (токени вже вміють
   `IssueFile(path)` і `IssueSealed(path)`). Пам'ять `GeoSeen` працює з id `u:<id>` без змін.
   - «Де це? — від Влада»: у виді `by` (нік автора) з `between`; клієнт пише в шапці раунду «📸 Фото від Влада».
   - **Автор на своє фото не відповідає:** `Guess`/`Area` → «Це твоє фото — дивись, як інші шукають», у `AllPresent(_ready)`
     автор не чекається; очок за раунд автор отримує **половину середнього** очок інших (щоб «лише наші» не карало автора
     і не давало фармити: сфоткав легке — друзі влучили — тобі половина). У розкритті рядок автора «📸 автор · +N».
   - Після розкриття — історія автора (`story`) замість підпису Вікісховища (`reveal.photo` → `{ author: nick, story }`).
   - Банк друзів порожній, а стоїть `only` — `CanStart` «Ще ніхто не закинув своїх фото — «📸 Мої фото» в лобі гри».
4. **Клієнт:** кнопка «📸 Мої фото» (у лобі столу гри й у тренуванні) → панель: сітка своїх фото з ✕, «＋ Додати»: вибір
   файла → canvas-стиск → мапа (та сама `geo-map` з детальним шаром, тап = шпилька) → назва + історія → «Закинути».
   Адмін бачить усі й може видалити. Вмістити на 390×664 (панель на весь екран), пад: Ⓑ — закрити.
5. **Тести:** стиск/Strip (EXIF з GPS не лишається), ліміти, чужий не видалить, адмін видалить, `friends=only` дає фото
   друзів, автор не відповідає й отримує половину середнього, історія лише після розкриття, приховане: у `guess` ні
   координат, ні `title`, ні `story` у виді/кадрі.
6. `news` модуля (v 2026-09-29) — додати пункт «📸 Мої фото…»; spec — дописати в «Прохід №3».

## Пастки
- `appsettings.Local.json` у worktree має `YtDlp:FfmpegDir = D:/or/tools/yt-dlp` (для `GeoShrink`) — не комітити.
  Кеш `cache/geo` уже перетиснутий (39 МБ) — наступний запуск нічого не тисне.
- Великі Python/C#-правки — файлом-сценарієм: `qa/rep.py <файл>` (запуск повним шляхом Python) (блоки `@@@ шлях`, `<<<`, `===`, `>>>`);
  файли CRLF — rep.py їх зберігає.
- Перевірка в браузері: `qa/daily.js` (грає «Де це? дня»), `qa/map1.js` (тренування, мапа), `qa/mob.js` (стіл-дуель
  на телефоні з 💡). Перед `OpenSolo` закрити модалку «Хто прийшов?» («Не зараз»), після — `location.hash =
  '#games/room/' + roomId`.
- Overpass для доріг: overpass-api.de відповідає 504, спрацював `overpass.private.coffee` (171 с, 20 МБ).

## Команди
```
dotnet build src/Hlechyky/Hlechyky.csproj -v q -nologo -nodeReuse:false 2>&1 | grep -E " error " | head
dotnet test tests/Hlechyky.Tests -v q -nologo -nodeReuse:false --filter "FullyQualifiedName~Geo" 2>&1 | grep -E "\[FAIL\]|Passed!|Failed!"
PYTHONIOENCODING=utf-8 C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe D:/or-wt/_tools/cdp2.py --port 9507 --url "http://127.0.0.1:8307/?cb=1#games" --nick Тестер --js qa/daily.js
```
