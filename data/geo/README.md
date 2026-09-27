# «Де це?» — дані гри

- `places.json` — банк місць (формат `geo` з `D:/or-wt/_wave2/CONTENT-FORMATS.md`, spec — `docs/games/specs/geo.md` §7.1):
  **250 місць, 258 фото** — зібрали окремі агенти, звірив куратор (координати й області — з Вікідата, ліцензії й автори —
  з Вікісховища, усі фото переглянуто). Стартові 19 місць із додатка А spec тепер — фікстура тестів
  (`tests/Hlechyky.Tests/Fixtures/geo-starter.json`).
  Фото лише з `upload.wikimedia.org` / `thumb.wikimedia.org` під вільними ліцензіями (CC0, PD, CC BY, CC BY-SA);
  автор і ліцензія показуються гравцям після розкриття (посилання — лише https). Кривий запис гра відкидає поодинці
  й пише в лог.
- Додаючи місця: `cat` — одне з `city`/`castle`/`nature`/`village` (як в опції «Місця»), точка — на суходолі України в
  названій області (тест `Every_bank_place_lies_on_ukrainian_land_in_the_region_the_bank_names`), і **без назви
  місця на самому фото**: жодних гравюр і листівок із підписом, вивісок із назвою міста, що читаються на весь екран.
- Фото качає сервер сам (`GeoPhotos`) у `cache/geo/` (не в гіті): ім'я файла — SHA-256 адреси, EXIF/XMP/ICC/коментарі
  зрізано. Клієнт бачить лише непрозорий токен `/api/games/geo/<24 hex>.jpg`.
- `build-map.py` — збірник мапи `web/games/geo-map.json` (≈40 КБ) з Natural Earth 10m: 27 одиниць України в
  міжнародно визнаних кордонах (з Кримом і Севастополем), дуги в стилі TopoJSON, спрощення Дугласа — Пекера,
  вісім річок, обласні центри. Запуск з кореня репозиторію:

  ```
  C:/Users/Ya/AppData/Local/Python/pythoncore-3.14-64/python.exe data/geo/build-map.py
  ```

  Вихідні файли NE (~48 МБ) скрипт качає в `%TEMP%` і в гіт не кладе.

Made with Natural Earth. Free vector and raster map data @ naturalearthdata.com (суспільне надбання).
