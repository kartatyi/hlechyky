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
    /// <summary>Найдовше, скільки одна мінігра тримає глеки, — від її початку.</summary>
    public static readonly TimeSpan MinigameCap = TimeSpan.FromSeconds(90);

    // Коли глек мав злетіти до відкладення: мінігра скінчилась раніше за стелю — він повертається до свого часу
    // (але не раніше, ніж за MinigameAfter). Лише в пам'яті: перезапуск посеред мінігри коштує щонайбільше відкладення до стелі.
    DateTimeOffset _fallPlanned, _goldenPlanned;

    /// <summary>Коли кінчиться мінігра на час, що йде просто зараз, або <c>default</c>, якщо жодної.</summary>
    DateTimeOffset MinigameUntil(DateTimeOffset now)
    {
        var until = default(DateTimeOffset);
        // Ручний обпал: пів хвилини тримати жар. Підмайстер палить без мінігри.
        if (_litAt != default && !_litHelper && now < _litAt + KilnBurn) until = _litAt + KilnBurn;
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
        if (_fallPlanned != default)
        {
            var t = _fallPlanned > now + MinigameAfter ? _fallPlanned : now + MinigameAfter;
            if (now < _fall.At - EarlyGrace && _fall.At > t) _fall = _fall with { At = t, Until = t + (_fall.Until - _fall.At) };
            _fallPlanned = default;
        }
        if (_goldenPlanned != default)
        {
            var t = _goldenPlanned > now + MinigameAfter ? _goldenPlanned : now + MinigameAfter;
            if (now < _golden.At - EarlyGrace && _golden.At > t) _golden = _golden with { At = t, Until = t + (_golden.Until - _golden.At) };
            _goldenPlanned = default;
        }
    }

    /// <summary>
    /// Що буде, якщо цей глек з полиці розіб'ється на очах у гончаря: «streak» — серія обірветься, «apron» — фартух
    /// уберіже серію, «none» — серії й нема. Клієнт малює «трісь» саме за цим, а не вгадує (ті самі гілки, що в Sync).
    /// </summary>
    string FallMiss => _fallStreak <= 0 ? "none" : Tool("apron") && !_apronUsed ? "apron" : "streak";
}
