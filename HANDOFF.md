# HANDOFF — sweep3/skilky («Скільки?»)

## Зроблено (коміти в гілці, тести зелені: повний прогін 5351)
- Етап А повністю: 42 числова пряма, 45 питання про ігри сайту (+фоновий `SkilkyStats.Peek`), 41 «Ставлю на чуже» (опція
  `bets`), 44 питання про нас (лобі й між партіями), 47 команди 2–4 (опція `teams`), 48 «Скільки? дня» (`SkilkyDaily`,
  `SkilkyDailyBoard`). Звіт: `D:/or-wt/_sweep3/reports/skilky.md`, spec — розділ «Прохід №3».
- Етап Б: сервер і клієнт теми «📷 Якого року?» (`SkilkyPhotos.cs`, `/api/games/skilky/photo/<токен>.jpg`, кеш
  `cache/skilky`), фото й у «Скільки? дня». Маніфест `data/skilky/photos.json` — **23 фото** (
  12 Україна + 11 світ), живцем докачались 23/23.

## Лишилось (списком)
1. **Добрати бібліотеку до ≥ 120 фото** (зараз 23): різні епохи 1850–2024 (частіше 1900–2010), ~третина Україна
   (події, міста, побут, техніка), решта — світ (відомі події, дивні/кумедні кадри, мода, техніка, спорт). Без
   жорстокості, війни, агітації. Кандидати: `D:/or-wt/_sweep3/skilky-photos/cand.json` (сканер міг ще дописувати —
   `scan.log`); уже переглянуто номери 0–76 з `cand-a.json` (взято 24, одне прибрано). Автоскан по «<рік> in <місто>»
   дає багато нудних будівель — для цікавих кадрів краще ручні категорії/файли: напр. «Apollo 11» (NASA, PD), «Wright
   Flyer 1903», «Eiffel Tower construction», «Golden Gate Bridge construction», «1936 Summer Olympics»? (обережно, агітація),
   «Beatles 1964», «Einstein 1921», «Titanic 1912 departure», «Photographs by Prokudin-Gorsky» (1905–1915, кольорові,
   є Україна), «Kyiv in the 1970s», «Chernihiv», «Kamianets-Podilskyi», «Dnipro HES construction», «Antonov An-225»,
   «Kyiv Metro 1960», «Euro 2012 in Ukraine», мода/авто/комп'ютери (ENIAC 1946, IBM PC 1981). Перевіряй рік: дата
   зйомки в описі + категорія «<рік> in …» (не «1930s», не «circa»). Скрипт: `docs/games/dev/skilky-photos.py`
   (`scan` / `list` / `add <cand> 3,7 --captions caps.json`); для ручних файлів — допиши в маніфест тими ж полями.
2. **Розмір фото**: мініатюри Commons 1024 px виходять ~340 КБ (ціль ~150 КБ). Варіант — брати `iiurlwidth=800`
   (≈200 КБ) у скрипті й перегенерувати `url` у маніфесті (формат `/thumb/…/1024px-…` → `800px-…`); перекодування на
   сервері нема чим (без бібліотек зображень).
3. Живцем не грано: команди (лише тести), «питання про нас» у браузері, фото-тема на телефоні 390×664 (висота фото
   `min(52vh, 460px)`), пад/Steam Deck. Знімки: qa/bet.png (ставки + пряма, 1280).
4. Рецензент — за звичним ланцюжком.

## Пастки
- `SkilkyStats.Peek`: перше «Почати» після рестарту — без динамічних (кеш холодний), далі є. У тестах — `Warm()`.
- Модуль `skilky.js` реєструє два id: `skilky` і `skilky-daily` (Client = "skilky" на сервері).
- `data/*` у .gitignore — додано `!data/skilky/`.
- «☀ Сьогодні» показує «розгадано за 1 спробу» для дня (каркасна таблиця без очок) — див. «Каркасу» у звіті.

## Перевірка
```
dotnet test tests/Hlechyky.Tests -v q -nologo -nodeReuse:false --filter "FullyQualifiedName~Skilky|FullyQualifiedName~Kviz"
python docs/games/dev/kviz-bots.py --port 8306 --game skilky --nicks Оля,Петро --seconds 140   # боти за стіл
```
