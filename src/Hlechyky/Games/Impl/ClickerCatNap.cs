using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Кіт спить, серія тріскає (21.10: хто боїться втратити серію).
///
/// 1. Глеки з полиці скидає кіт. <c>nap {on:true}</c> — «🥛 Молока котові»: кіт засинає, і глеки не падають зовсім —
///    серія завмирає (не росте й не рветься), доки його не розбудять (<c>nap {on:false}</c>). Глек, що саме летить,
///    кіт доскидає: його ще ловлять, а засинає кіт після нього. Поки спить, і в гості не ходить. Дохід без гончаря
///    (пасив, комора під полицею) і «хвилина гри» рахуються за часом, а не за глеками, що впали, — кіт їх не чіпає.
///    Сплячий кіт — це глек, відкладений на <see cref="NapParked"/>: решта коду (Sync, Grab, мінігри, Око майстра)
///    просто бачить глек, до якого ще далеко.
/// 2. Розбитий глек серію не обнуляє, а відколює від неї: перший розбитий поспіль — <see cref="CrackStep"/> серії,
///    другий поспіль — удвічі більше, і так далі; десятий поспіль забирає решту. Спійманий глек лік «поспіль» скидає.
///    Фартух, як і досі, вибачає перший промах цілком — тоді тріщина починається з другого.
/// </summary>
public sealed partial class Clicker
{
    /// <summary>Скільки серії відколює кожен розбитий глек поспіль: перший — 10 %, другий — 20 %, …</summary>
    public const double CrackStep = 0.1;

    /// <summary>Куди «відкладено» глек, поки кіт спить: так далеко, що Sync його ніколи не дочекається.</summary>
    public static readonly DateTimeOffset NapParked = new(9000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Коли котові налили молока; <c>default</c> — не спить. Зберігається: кіт спить між заходами й пристроями.</summary>
    DateTimeOffset _napAt;
    /// <summary>Скільки глеків поспіль уже розбилось (не рахуючи вибаченого фартухом): від цього — розмір тріщини.</summary>
    int _cracks;

    bool Napping => _napAt != default;

    /// <summary>Глек відкладено під сплячого кота (а не просто чекає свого часу).</summary>
    bool FallParked => _fall.At == NapParked;

    /// <summary>Кіт спить — наступного глека нема. Відкладене під мінігру теж нікуди не вертається.</summary>
    void ParkFall()
    {
        _fallPlanned = default;
        _fall = new FallRow(NapParked, NapParked + FallShown, 50);
    }

    /// <summary>
    /// <c>nap {on}</c>: приспати кота (on) чи розбудити. Спить — глеки не падають; глек, що вже летить, ще треба
    /// спіймати (Sync поставить на його місце відкладений). Прокинувся — перший глек за звичайні хвилину-дві.
    /// </summary>
    ActResult Nap(JsonElement payload)
    {
        var now = Ctx.Clock.UtcNow;
        if (Flag(payload, "on"))
        {
            if (Napping) return ActResult.Done;
            _napAt = now;
            if (FallFlying(now)) return ActResult.Accept("🥛 Кіт доскидає цей глек — лови! — і засне");
            ParkFall();
            return ActResult.Accept(_fallStreak > 0
                ? $"😴 Кіт спить — глеки з полиці не падають, серія {_fallStreak} чекає"
                : "😴 Кіт спить — глеки з полиці не падають");
        }
        if (!Napping) return ActResult.Done;
        _napAt = default;
        if (FallParked) ScheduleFall(now);
        return ActResult.Accept("🐈 Кіт прокинувся — скоро знову скине глек");
    }

    /// <summary>
    /// Що у виді про сон кота: <c>"on"</c> — спить, глеків нема; <c>"soon"</c> — засинає, щойно долетить глек у
    /// повітрі; <c>null</c> — не спить.
    /// </summary>
    string? NapView => !Napping ? null : FallParked ? "on" : "soon";

    /// <summary>Скільки серії відколе <paramref name="k"/>-й розбитий поспіль: частка, округлена, щонайменше один глек.</summary>
    int CrackLoss(int k)
    {
        if (_fallStreak <= 0) return 0;
        var lose = (int)Math.Round(_fallStreak * Math.Min(1, CrackStep * k), MidpointRounding.AwayFromZero);
        return Math.Clamp(lose, 1, _fallStreak);
    }

    /// <summary>Скільки забере серії глек у повітрі, якщо розіб'ється на очах (фартух ще цілий — нічого).</summary>
    int NextCrackLoss => Tool("apron") && !_apronUsed ? 0 : CrackLoss(_cracks + 1);

    /// <summary>Розбився на очах, фартух не вберіг: тріщина в серії, щоразу поспіль більша.</summary>
    void Crack()
    {
        if (_fallStreak <= 0) return;
        _cracks++;
        _fallStreak -= CrackLoss(_cracks);
    }
}
