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
    /// Вікно підсумку — «Горно відкрите» після ручного обпалу чи «Розпис ліг» (v12, QA-2/QA-3): глеки вилітали прямо в
    /// нього, і глек з полиці розбивався, поки гончар читав, що вийшло. Тепер глеки чекають, доки клієнт скаже, що вікно
    /// закрили (<c>kiln {op:"seen"}</c>), але не довше за цю стелю — старий клієнт чи закрита вкладка не тримають їх вічно.
    /// </summary>
    public static readonly TimeSpan ResultCap = TimeSpan.FromSeconds(30);
    /// <summary>Обидва глеки чекали — розписний вилітає щонайменше на стільки пізніше за глек з полиці: дві цілі за раз — одна розбита.</summary>
    public static readonly TimeSpan HeldStagger = TimeSpan.FromSeconds(4);

    /// <summary>До коли висить вікно підсумку (лише в пам'яті); <c>default</c> — не висить.</summary>
    DateTimeOffset _resultUntil;

    /// <summary>Показали вікно підсумку (горно відкрили вручну, розпис ліг): глеки чекають його закриття.</summary>
    void ResultShown(DateTimeOffset now) => _resultUntil = now + ResultCap;

    /// <summary><c>kiln {op:"seen"}</c>: вікно підсумку закрили — глеки йдуть (HoldForMinigame після дії).</summary>
    ActResult ResultSeen()
    {
        _resultUntil = default;
        // Другий пристрій не показує вдруге підсумок, який уже прочитали деінде (QA-3).
        if (_kilnLast is { Seen: false } l) _kilnLast = l with { Seen = true };
        return ActResult.Done;
    }

    /// <summary>
    /// Починається мінігра на час (ручне горно, розпис технікою), а глек з полиці вже в повітрі чи ось-ось: руки в мінігрі,
    /// і промах — не промах (QA-3: глек, відпущений після розпису, бився на старті горна). Серія ціла, як і в того, хто
    /// пішов від кола (<see cref="Unwitnessed"/>).
    /// </summary>
    void SpareAirborne(DateTimeOffset now)
    {
        if (_fall.At != default && now >= _fall.At - EarlyGrace && now < _fall.Until) _sparedAt = _fall.At;
    }
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
        // Вікно підсумку: гончар читає, що вийшло, а не дивиться на сцену.
        if (_resultUntil != default && now < _resultUntil && _resultUntil > until) until = _resultUntil;
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
        var floor = now + MinigameAfter;
        // Відкладений глек іде на свій час, але не раніше за floor; != — бо мінігра чи вікно підсумку могли скінчитись
        // раніше за стелю, до якої його відклали: тоді він вертається ближче.
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
