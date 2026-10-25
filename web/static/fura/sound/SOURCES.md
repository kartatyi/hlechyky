# Звук фури — джерела й ліцензії

Усі записи — з [Freesound](https://freesound.org), ліцензія **CC0 1.0** (суспільне надбання,
https://creativecommons.org/publicdomain/zero/1.0/): можна використовувати, змінювати й поширювати без дозволу й підпису.
Авторів однаково називаємо — з поваги. Узято публічні превʼю (mp3 ~128 кбіт/с), завантажено 25.10.2026 з дозволу
користувача; далі обрізано, вирівняно за обертами, зведено в моно 96 кбіт/с.

| файл | що це | джерело | автор |
|---|---|---|---|
| `on1300.mp3`, `on1450.mp3`, `on1600.mp3`, `on1720.mp3` | двигун під газом з кабіни, 1300…1720 об/хв: шматки розгонів, вирівняні до сталих обертів | [freightliner going through gears](https://freesound.org/people/goose278/sounds/165168/) | goose278 |
| `idle700.mp3` | холостий тягача, 700 об/хв | [truck engine semi trailer idle deep close](https://freesound.org/people/kyles/sounds/637916/) | kyles |
| `ext1690.mp3` | вихлоп зовні під навантаженням, 1690 об/хв | [truck big diesel engine work up a hill sustained rev and pass by2](https://freesound.org/people/kyles/sounds/637914/) | kyles |
| `start.mp3` | старт двигуна (Ford LTL 9000) | [FORD LTL 9000_engine start and idle](https://freesound.org/people/YellowJacketStudios/sounds/435656/) | YellowJacketStudios |
| `horn.mp3` | пневматичний сигнал (петля середини гудка) | [air horn close and loud](https://freesound.org/people/goose278/sounds/174819/) | goose278 |
| `couple.mp3` | зчеп тягача з причепом | [tractor connecting to trailer with air brk](https://freesound.org/people/goose278/sounds/165167/) | goose278 |
| `air.mp3` | «пшш» — відпущені пневмогальма | [air_brakes_loud_fade_out](https://freesound.org/people/rabbydaw/sounds/504865/) | rabbydaw |

`bank.json` — опис банку для `web/games/fura/engine.js`: оберти кожної петлі, межі петлі (`loopStart`/`loopEnd`, з
запасом періодичного продовження з обох боків — шов не ламається затримкою декодера mp3), ефекти й переведення обертів
гри в оберти запису (`rpmMap`).

Збірка — `D:\or-wt\_fura\sound-tools\build.py` (поза репо: трекінг частоти спалахів, time-warp розгону до сталих
обертів, склейка шматків із фазовим вирівнюванням, циклічний фільтр, ffmpeg/libmp3lame). Перевірка банку — `verify.py`
там само. Сирі завантаження — `D:\or-wt\_fura\sound-cache\`.
