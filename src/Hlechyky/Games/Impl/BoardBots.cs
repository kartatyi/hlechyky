namespace Hlechyky.Games.Impl;

/// <summary>
/// «Глек підсідає» за настільні ігри без тика (Чотири в ряд на компанію, доміно, дурень). Місце бота в каркасі
/// порожнє: нагород і рейтингу він не бере (Rewards рахує лише людей), а гра сама пам'ятає, хто там сидить.
///
/// Тика в цих ігор нема й не буде: з тиком каркас перестав би слати види після кожного ходу і рахувати ходи.
/// Тож бот ходить, коли його «штовхне» клієнт котрогось із гравців-людей дією <see cref="Nudge"/> — не раніше,
/// ніж за <see cref="ThinkMs"/> після того, як до нього дійшла черга. Штовхнули зарано чи двоє одразу — відмова,
/// а клієнт шле її без тосту (HGames.send), тож ніхто нічого не бачить.
/// </summary>
public static class BoardBots
{
    public static readonly string[] Names = ["Глек 🤖", "Макітра 🤖", "Горнятко 🤖", "Куманець 🤖", "Глечик 🤖"];

    /// <summary>Скільки бот «думає»: миттєвий хід читався б як глюк, а людям треба встигнути побачити попередній.</summary>
    public const int ThinkMs = 1100;

    /// <summary>Дія, якою клієнт людини штовхає бота, чия черга.</summary>
    public const string Nudge = "bot";

    /// <summary>Опція столу: скільки Глеків підсідає на порожні місця.</summary>
    public static GameOption Option(int max) => new("bots", "🤖 Глек підсідає на порожні місця",
        [.. new[] { ("0", "ні"), ("1", "один Глек"), ("2", "два"), ("3", "три"), ("4", "чотири"), ("5", "п'ять") }.Take(max + 1)], "0");

    /// <summary>Скільки ботів просили в опціях (0, якщо нічого путнього).</summary>
    public static int Read(IReadOnlyDictionary<string, string> options, int max) =>
        options.TryGetValue("bots", out var b) && int.TryParse(b, out var n) && n >= 0 && n <= max ? n : 0;

    /// <summary>
    /// Розсадка на старті: порожні місця (від першого) займають боти, скільки просили. Повертає імена ботів по місцях
    /// (null — людина або пусто).
    /// </summary>
    public static string?[] Seat(IRoomContext ctx, int seats, int bots, int? onlyUpTo = null)
    {
        var names = new string?[seats];
        var n = 0;
        var cap = onlyUpTo ?? seats;
        for (var s = 0; s < seats && n < bots; s++)
        {
            if (ctx.Seated(s)) continue;
            if (Humans(ctx, seats) + n >= cap) break;
            names[s] = Names[n++ % Names.Length];
        }
        return names;
    }

    public static int Humans(IRoomContext ctx, int seats)
    {
        var n = 0;
        for (var s = 0; s < seats; s++) if (ctx.Seated(s)) n++;
        return n;
    }
}
