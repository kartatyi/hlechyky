using Hlechyky.Games.Economy;

namespace Hlechyky.Games.Impl;

/// <summary>
/// «⚖ Фора» (пакет 3, 20.10): сильніші за столом отримують трохи менше, слабші — трохи більше, щоб у партії з Миколою
/// (82 % перемог у Цеглинах) решта мала шанс і не розходилась. Сила — з <see cref="ISkills"/> (пакет 1, з game_results
/// фоном), тож під замком кімнати тут лише читання словника. Що саме множиться — вирішує гра: сміття в Цеглинах, очки
/// у вікторинах, HP хати в Глекометах. Множник — від середньої сили людей за столом: <c>1 − K·(сила − середня)</c>
/// у межах [<see cref="Min"/>, <see cref="Max"/>]. Боти фори не мають і в середню не йдуть. На столі зі ставкою
/// черепками й у рейтингових іграх фори нема — там гроші й Ело мусять бути чесні «як є».
/// </summary>
public static class Handicap
{
    public const string Key = "handicap";
    public const string Auto = "auto", Off = "0";

    /// <summary>Опція столу — типово увімкнена: інакше її ніхто не ввімкне, а слабші так і розходитимуться.</summary>
    public static readonly GameOption Option = new(Key, "⚖ Фора",
        [(Auto, "за силою — сильнішим трохи важче"), (Off, "без фори")], Auto);

    /// <summary>Наскільки сильно різниця сил міняє множник; сила гравців на проді — приблизно від −0,3 до +0,5.</summary>
    public const double K = 0.8;
    public const double Min = 0.6, Max = 1.4;

    /// <summary>Множники ближче до 1 за цей поріг у підписі не показуємо — це шум, а не фора.</summary>
    const double Shown = 0.05;

    public static bool On(IReadOnlyDictionary<string, string> options) =>
        !(options.TryGetValue(Key, out var v) && v == Off) && !Staked(options);

    static bool Staked(IReadOnlyDictionary<string, string> options) =>
        options.TryGetValue("stake", out var s) && int.TryParse(s, out var n) && n > 0;

    /// <summary>Множник гравця з силою <paramref name="strength"/> серед столу із середньою <paramref name="mean"/>.</summary>
    public static double Factor(double strength, double mean) => Math.Clamp(1 - K * (strength - mean), Min, Max);

    /// <summary>
    /// Множники для людей на <paramref name="humanSeats"/> (місце → множник). Порожньо, коли фору вимкнено, стіл на ставку,
    /// гра рейтингова чи людей менше двох — тоді гра множить на 1 і нічого не пише.
    /// </summary>
    public static Dictionary<int, double> ForSeats(IRoomContext ctx, string gameId, IReadOnlyList<int> humanSeats, bool rated = false)
    {
        var result = new Dictionary<int, double>();
        if (rated || humanSeats.Count < 2 || !On(ctx.Options)) return result;
        var skills = ctx.Services?.GetService(typeof(ISkills)) as ISkills;
        if (skills is null) return result;
        var power = humanSeats.Select(s => skills.Strength(gameId, ctx.NickOf(s))).ToArray();
        var mean = power.Average();
        for (var i = 0; i < humanSeats.Count; i++) result[humanSeats[i]] = Factor(power[i], mean);
        return result;
    }

    /// <summary>Множник місця з <paramref name="factors"/> (нема — 1).</summary>
    public static double Of(IReadOnlyDictionary<int, double> factors, int seat) =>
        factors.TryGetValue(seat, out var f) ? f : 1;

    /// <summary>
    /// Ціле <paramref name="amount"/> × <paramref name="factor"/> з дробовим залишком у <paramref name="carry"/>:
    /// множник 0,7 на посилках 1, 1, 1 дає 0, 1, 1 (а не 0, 0, 0), тож за партію фора саме така, як написано.
    /// </summary>
    public static int Scale(int amount, double factor, ref double carry)
    {
        if (amount <= 0) return amount;
        var exact = amount * factor + carry;
        var whole = (int)Math.Floor(exact + 1e-9);
        carry = exact - whole;
        return whole;
    }

    /// <summary>
    /// Рядок у Журнал/лобі: «⚖ Фора: Микола ×0,7 · владік ×1,2» — лише ті, в кого множник помітно не 1; нікого — null.
    /// </summary>
    public static string? Note(IRoomContext ctx, IReadOnlyDictionary<int, double> factors)
    {
        var parts = factors.Where(kv => Math.Abs(kv.Value - 1) >= Shown)
            .OrderBy(kv => kv.Value)
            .Select(kv => $"{ctx.NickOf(kv.Key)} ×{kv.Value.ToString("0.0#", Uk)}")
            .ToList();
        return parts.Count == 0 ? null : "⚖ Фора: " + string.Join(" · ", parts);
    }

    static readonly System.Globalization.CultureInfo Uk = System.Globalization.CultureInfo.GetCultureInfo("uk-UA");
}
