using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Глек не б'ється через порожню хату й поганий зв'язок (записки 12.10: smaug вийшла в «Ефір», не дочекавшись глека,
/// повернулась за хвилину — серії нема; у бібочки інтернет на мить зникає, і натиск на глек не зараховується).
///
/// 1. Пішов від кола — глек не його. Коло каже тихою дією <c>sight {on:false}</c>, щойно зникло з очей (інший розділ
///    сайту, лобі, інша гра, схована вкладка чи застосунок), і <c>sight {on:true}</c>, коли його знову видно. Запаска —
///    каркас (<see cref="Unwatched"/>): за кімнатою кола не стежить жодне з'єднання (пішов з «Ігор», закрив вкладку,
///    обрив). Глек, що злетів після цього, — порожня хата, як і за <see cref="EventGap"/>: серія ціла, фартух не чіпаємо.
///    Той, що вже летів, коли гончар пішов, — промах: бачив і пішов. Повернувся посеред польоту глека, що злетів без
///    нього, — і цей не рахується (<see cref="_sparedAt"/>). Справжня дія чи <c>sight {on:true}</c> — він знову тут.
/// 2. Натиск без зв'язку не губиться. Клієнт шле <c>grab</c>/<c>catch</c> із миттю натиску <c>t</c> (мс, серверний
///    годинник за його оцінкою) і, якщо не долетіло, повторює, щойно зв'язок повернеться. Сервер тим часом міг уже
///    записати глек розбитим (Sync на пачці кліків, на <c>look</c> чи на виді після реконекту) — пізній натиск, що влучив
///    у політ і прийшов не пізніше за <see cref="LateGrab"/>, латає: серія й фартух — як були до, далі звичайний
///    спійманий. Наступний глек лишається за розкладом. Повтор натиску, відповідь на який загубилась, удруге не платить.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Скільки натиск без зв'язку може йти до сервера й ще рахуватись.</summary>
    public static readonly TimeSpan LateGrab = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Наскільки мить натиску може бути раніше за зліт глека, щоб це ще був натиск на нього, а не повтор давнього: оцінка
    /// серверного годинника на клієнті відстає на дорогу виду. Глеки злітають щонайменше за пів хвилини один від одного.
    /// </summary>
    static readonly TimeSpan PressSlack = TimeSpan.FromSeconds(3);

    /// <summary>Коли гончар пішов від кола; <c>default</c> — він тут. Зберігається: кімнату могли вивантажити.</summary>
    DateTimeOffset _goneAt;
    /// <summary>
    /// Остання дія, яку зробив сам гончар (21.10, серія владіка 1 861 після v12). Не те саме, що <see cref="_seenAt"/>:
    /// відкрита картка сама шле <c>look</c> — підмайстер відкрив горно, купець вернувся, корабель прийшов, дошка
    /// застаріла, глек розбився, — і кожен такий садив гончаря біля кола. Тож поки картку видно, «затих на дві хвилини»
    /// не наставало ніколи, і глеки, що падали, поки він читав вікно чи дивився деінде, рвали серію (фартух — і кінець).
    /// Лише в пам'яті: після завантаження — та сама мітка, що й синхронізація.
    /// </summary>
    DateTimeOffset _handsAt;

    /// <summary>
    /// Це шле картка, а не гончар: таймер питає свіжий вид (<c>look</c> без вибору оздоби хати) чи вікно тримає глеки
    /// (<c>hold</c> — його відкривають і самі події, а відкрите підтверджують щодесять секунд, ClickerFallHold.cs).
    /// </summary>
    static bool Poll(string action, JsonElement payload) => (action == "look" && Str(payload, "key").Length == 0) || action == "hold";

    /// <summary>
    /// Гончар затих ще до того, як глек злетів, і мовчить довше за <see cref="EventGap"/> (81dfff0): глек летів без нього.
    /// </summary>
    bool Idle(FallRow fall, DateTimeOffset now) => now - _handsAt > EventGap && fall.At > _handsAt;
    /// <summary>Глек, що злетів без гончаря, а той повернувся посеред польоту: не рахується ні так, ні так.</summary>
    DateTimeOffset _sparedAt;
    /// <summary>Скільки глеків при серії пролетіло без гончаря, поки його не було (на повернення — «серія ціла»).</summary>
    int _spared;
    /// <summary>Те саме про щойнє повернення: його читає <see cref="Sight"/>.</summary>
    int _backSpared;

    /// <summary>Розбитий глек, який ще може залатати пізній натиск, і що було із серією до нього.</summary>
    sealed record BrokenFall(FallRow Fall, int Streak, bool Apron, bool Busy, int Cracks);
    BrokenFall? _broken;
    /// <summary>Розписний, що втік неспійманим, — так само.</summary>
    GoldenRow? _fled;
    /// <summary>Останні спіймані: повтор того самого натиску (відповідь загубилась у дорозі) удруге не платить.</summary>
    FallRow? _caughtFall;
    GoldenRow? _caughtGolden;

    void ResetLate(DateTimeOffset goneAt)
    {
        _goneAt = goneAt;
        _sparedAt = default;
        _spared = _backSpared = 0;
        _broken = null;
        _fled = null;
        _caughtFall = null;
        _caughtGolden = null;
    }

    /// <summary>Гончар пішов від кола. Пішов раніше — та мить і лишається.</summary>
    void Gone(DateTimeOffset now)
    {
        if (_goneAt != default) return;
        _goneAt = now;
        // fall.miss у виді залежить від цього. У дії версію вже підняв Act — вдруге не можна: вид після тихої дії
        // перестав би бути тихим (BuildView) і посадив би гончаря назад до кола. Unwatched кличуть поза дією.
        if (!_inAct) _viewVersion++;
    }

    public override void Unwatched() => Gone(Ctx.Clock.UtcNow);

    /// <summary>
    /// Глек летів без гончаря: той пішов раніше, ніж глек злетів (секунда запасу — «пішов» доходить до сервера з пінгом),
    /// або це той, посеред якого гончар повернувся.
    /// </summary>
    bool Unwitnessed(FallRow fall) =>
        (_goneAt != default && fall.At > _goneAt - EarlyGrace) || (_sparedAt != default && fall.At == _sparedAt);

    /// <summary>
    /// Гончар знову біля кола. Глек, що злетів без нього й досі летить, — теж не його: встигне — спіймає, ні — не біда.
    /// Повертає, скільки глеків при серії пролетіло без нього, поки його не було.
    /// </summary>
    int Back(DateTimeOffset now)
    {
        if (_goneAt != default && Unwitnessed(_fall) && now >= _fall.At - EarlyGrace) _sparedAt = _fall.At;
        _goneAt = default;
        var spared = _spared;
        _spared = 0;
        return spared;
    }

    /// <summary>
    /// <c>sight {on}</c>: коло зникло з очей (тиха дія — присутністю не рахується, див. QuietAct) чи знову видно (справжня:
    /// Back уже зроблено в ActInner). Повернувся, а глек тим часом упав сам, — скажемо, що серія ціла.
    /// </summary>
    ActResult Sight(JsonElement payload)
    {
        if (!Flag(payload, "on"))
        {
            Gone(Ctx.Clock.UtcNow);
            return ActResult.Done;
        }
        var spared = _backSpared;
        _backSpared = 0;
        return spared > 0 && _fallStreak > 0 ? ActResult.Accept($"🏺 Глек упав без тебе — серія {_fallStreak} ціла") : ActResult.Done;
    }

    /// <summary>
    /// Мить натиску з payload (<c>t</c>, мс від епохи за серверним годинником клієнта), якщо їй можна вірити: не з
    /// майбутнього (секунда на пінг) і не старша за <see cref="LateGrab"/>. Старий клієнт її не шле — null.
    /// </summary>
    static DateTimeOffset? PressedAt(JsonElement payload, DateTimeOffset now)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("t", out var v)
            || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var ms) || !double.IsFinite(ms))
            return null;
        DateTimeOffset at;
        try { at = DateTimeOffset.FromUnixTimeMilliseconds((long)ms); }
        catch (ArgumentOutOfRangeException) { return null; }
        return at <= now + EarlyGrace && now - at <= LateGrab ? at : null;
    }

    /// <summary>Натиск влучив у політ (разом із запасом на пінг, як у Grab).</summary>
    static bool InFlight(DateTimeOffset at, DateTimeOffset until, DateTimeOffset pressed) =>
        pressed >= at - EarlyGrace && pressed <= until + CatchGrace;

    /// <summary>Натиск давніший за цей глек: повтор, що застряг у дорозі, — його ловити не можна (див. <see cref="PressSlack"/>).</summary>
    static bool Stale(DateTimeOffset? pressed, DateTimeOffset at) => pressed is { } p && p < at - EarlyGrace - PressSlack;

    /// <summary>Глек з полиці вже не летить, а натиск прийшов із миттю всередині його польоту.</summary>
    ActResult? GrabLate(DateTimeOffset? pressed, DateTimeOffset now)
    {
        if (pressed is not { } t) return null;
        if (_caughtFall is { } caught && InFlight(caught.At, caught.Until, t)) return ActResult.Done;
        if (_broken is not { } b || !InFlight(b.Fall.At, b.Fall.Until, t)) return null;
        if (_guard.Locked(now) || _guard.Pending)
            return ActResult.Fail("Спершу Око майстра: покажи, що ти не автоклікер");
        // Що Sync зробив із серією й фартухом, коли записував глек розбитим, — назад; далі як звичайний спійманий.
        _broken = null;
        _caughtFall = b.Fall;
        _fallStreak = b.Streak;
        _apronUsed = b.Apron;
        _cracks = b.Cracks;
        if (b.Busy && _brokenBusy > 0) _brokenBusy--;
        return Grabbed(now, "📡 Спіймав — зв'язок наздогнав!");
    }

    /// <summary>Розписний уже втік, а натиск прийшов із миттю, поки він стояв на колі.</summary>
    ActResult? CatchLate(DateTimeOffset? pressed, DateTimeOffset now)
    {
        if (pressed is not { } t) return null;
        if (_caughtGolden is { } caught && InFlight(caught.At, caught.Until, t)) return ActResult.Done;
        if (_fled is not { } g || !InFlight(g.At, g.Until, t)) return null;
        if (_guard.Locked(now) || _guard.Pending)
            return ActResult.Fail("Спершу Око майстра: покажи, що ти не автоклікер");
        _fled = null;
        _caughtGolden = g;
        if (_goldenMissed > 0) _goldenMissed--;
        return Caught(g, now, t, " · 📡 зв'язок наздогнав");
    }
}
