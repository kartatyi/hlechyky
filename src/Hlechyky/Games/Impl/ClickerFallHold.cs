namespace Hlechyky.Games.Impl;

/// <summary>
/// Глек чекає мінігру (записка Smaug №29): поки гончар розписує партію технікою чи палить горно сам, кожен натиск —
/// у мінігрі, і глек з полиці (чи розписний), що злетів би саме тоді, — 100 % розбитий і обірвана серія. Тож поки
/// мінігра на час іде, сервер не пускає глеки: відкладає їх до її кінця й ще трохи (<see cref="MinigameAfter"/>).
/// Початок і кінець мінігри сервер знає з дій (горно: <c>light</c> → <c>open</c>, розпис: <c>paint</c> → <c>decor</c>),
/// прапорця від клієнта не треба. Стеля — <see cref="MinigameCap"/> від початку: недороблений розпис (закрив вікно,
/// пішов) не тримає глеки довше. Глек, що вже летів, коли мінігра почалась, долітає: клієнт малює його поверх, у смузі
/// справа, і зловити його можна так само.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Скільки глек ще чекає після кінця мінігри: гончар устигає підняти очі на сцену.</summary>
    public static readonly TimeSpan MinigameAfter = TimeSpan.FromSeconds(1.5);
    /// <summary>
    /// Після ручного обпалу — вікно «Горно відкрите» з підсумком (v12, QA-2): глеки, що чекали, вилітали через 1,5 с прямо
    /// в нього, разом, і глек з полиці розбивався, поки гончар читав, що вийшло. Тепер — стільки на прочитання, а розписний —
    /// ще на <see cref="HeldStagger"/> пізніше за глек з полиці.
    /// </summary>
    public static readonly TimeSpan KilnResultAfter = TimeSpan.FromSeconds(8), HeldStagger = TimeSpan.FromSeconds(4);

    /// <summary>Коли востаннє відкрили горно після ручного обпалу (лише в пам'яті): від цього рахуємо час на прочитання.</summary>
    DateTimeOffset _kilnOpenedAt;
    /// <summary>Найдовше, скільки одна мінігра тримає глеки, — від її початку.</summary>
    public static readonly TimeSpan MinigameCap = TimeSpan.FromSeconds(90);

    // Коли глек мав злетіти до відкладення: мінігра скінчилась раніше за стелю — він повертається до свого часу
    // (але не раніше, ніж за MinigameAfter). Лише в пам'яті: перезапуск посеред мінігри коштує щонайбільше відкладення до стелі.
    DateTimeOffset _fallPlanned, _goldenPlanned;

    /// <summary>Коли кінчиться мінігра на час, що йде просто зараз, або <c>default</c>, якщо жодної.</summary>
    DateTimeOffset MinigameUntil(DateTimeOffset now)
    {
        var until = default(DateTimeOffset);
        // Ручний обпал: пів хвилини тримати жар. Підмайстер палить без мінігри. Після жару — доки горно не відкрили
        // (v12, QA-2: відпускали ще до запиту «open», і стеля на прочитання підсумку не діяла), але не довше, ніж чекає
        // запис палія з іншого вікна.
        if (_litAt != default && !_litHelper && now < _litAt + KilnBurn + KilnOtherWait) until = _litAt + KilnBurn + KilnOtherWait;
        // Розпис технікою: візерунок видано, траєкторію ще не прислано.
        if (_paintSeed != 0 && _litAt == default)
        {
            var end = _paintAt + (PatternLife < MinigameCap ? PatternLife : MinigameCap);
            if (now < end && end > until) until = end;
        }
        return until;
    }

    /// <summary>
    /// Відкласти глеки під мінігру або, коли вона скінчилась раніше за стелю, повернути їх до свого часу.
    /// Кличеться після кожної дії (мінігра починається й кінчається дією) і в <see cref="Sync"/>.
    /// </summary>
    void HoldForMinigame(DateTimeOffset now)
    {
        var until = MinigameUntil(now);
        if (until != default)
        {
            var t = until + MinigameAfter;
            // Уже на сцені чи от-от (у запасі на пінг клієнт міг його й намалювати) — хай долітає.
            if (now < _fall.At - EarlyGrace && _fall.At < t)
            {
                if (_fallPlanned == default) _fallPlanned = _fall.At;
                _fall = _fall with { At = t, Until = t + (_fall.Until - _fall.At) };
            }
            if (now < _golden.At - EarlyGrace && _golden.At < t)
            {
                if (_goldenPlanned == default) _goldenPlanned = _golden.At;
                _golden = _golden with { At = t, Until = t + (_golden.Until - _golden.At) };
            }
            return;
        }
        // Щойно відкрили горно — спершу час прочитати підсумок, а не 1,5 с.
        var floor = now + MinigameAfter;
        if (_kilnOpenedAt != default && _kilnOpenedAt + KilnResultAfter > floor) floor = _kilnOpenedAt + KilnResultAfter;
        // Відкладений глек іде на свій час, але не раніше за floor; != — бо після горна floor може бути й пізніше,
        // ніж кінець мінігри, до якого його відклали.
        var fallAt = default(DateTimeOffset);
        if (_fallPlanned != default)
        {
            var t = _fallPlanned > floor ? _fallPlanned : floor;
            if (now < _fall.At - EarlyGrace && _fall.At != t) _fall = _fall with { At = t, Until = t + (_fall.Until - _fall.At) };
            fallAt = _fall.At;
            _fallPlanned = default;
        }
        if (_goldenPlanned != default)
        {
            var t = _goldenPlanned > floor ? _goldenPlanned : floor;
            // Обидва чекали — розписний не разом із глеком з полиці: дві цілі за раз — одна з них розбита.
            if (fallAt != default && t < fallAt + HeldStagger && t > fallAt - HeldStagger) t = fallAt + HeldStagger;
            if (now < _golden.At - EarlyGrace && _golden.At != t) _golden = _golden with { At = t, Until = t + (_golden.Until - _golden.At) };
            _goldenPlanned = default;
        }
    }

    /// <summary>
    /// Що буде, якщо цей глек з полиці розіб'ється на очах у гончаря: «streak» — серія обірветься, «apron» — фартух
    /// уберіже серію, «none» — серії й нема, «away» — глек летить без гончаря, серія ціла (ClickerLate.cs: повернувся
    /// посеред його польоту). Клієнт малює «трісь» саме за цим, а не вгадує (ті самі гілки, що в Sync).
    /// </summary>
    string FallMiss => _fallStreak <= 0 ? "none" : Unwitnessed(_fall) ? "away" : Tool("apron") && !_apronUsed ? "apron" : "streak";
}
