# «Де це?» — дані гри

- `places.json` — банк місць (формат `geo` з `D:/or-wt/_wave2/CONTENT-FORMATS.md`, spec — `docs/games/specs/geo.md` §7.1).
  Стартовий банк — 19 місць із додатка А spec; головний (~250 місць) підкладають окремо, формат той самий.
  Фото лише з `upload.wikimedia.org` / `thumb.wikimedia.org` під вільними ліцензіями (CC0, PD, CC BY, CC BY-SA);
  автор і ліцензія показуються гравцям після розкриття. Кривий запис гра відкидає поодинці й пише в лог.
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
