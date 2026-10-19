using Hlechyky.Games.Economy;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Поділ на дві команди за силою (пакет 1, 19.10): рівні суми сил, щоразу трохи по-новому. Раніше командні ігри ділили
/// «через одного за місцями», а «Ще раз» зсуває всіх на одне місце — і пари не мінялись ніколи (на проді в командній
/// Кривулі тих самих двох ставило разом у всіх п'яти партіях). Сила людини — <see cref="ISkills"/>, бота — за рівнем.
/// </summary>
public static class TeamSplit
{
    /// <summary>Сила бота за рівнем (у тих самих одиницях, що й <see cref="SkillBoard.Formula"/>).</summary>
    public const double BotEasy = -0.4, BotNormal = 0, BotHard = 0.3;
    /// <summary>Наскільки поділ може бути гіршим за найкращий, щоб іще потрапити в жеребкування.</summary>
    public const double Slack = 0.15;
    /// <summary>Далі цього перебір усіх поділів задорогий (2^(n−1)) — беремо «змійку» за силою.</summary>
    const int MaxExact = 12;

    /// <summary>Значення опції «команди»: «1» — за силою (і всі старі збережені «1»), «2» — як сіли (через одного).</summary>
    public const string ByStrength = "1", BySeat = "2";

    public static double BotStrength(LiveBots.Level level) => level switch
    {
        LiveBots.Level.Easy => BotEasy,
        LiveBots.Level.Hard => BotHard,
        _ => BotNormal,
    };

    /// <summary>
    /// Команда (0/1) кожного гравця. Розміри відрізняються не більше ніж на 1; з поділів, у яких різниця сум не гірша
    /// за найкращу більше ніж на <see cref="Slack"/>, — випадковий, і, якщо є вибір, не такий, як <paramref name="previous"/>
    /// (з точністю до перейменування команд; −1 у previous — «тоді не грав», не враховується). Детермінований за rng.
    /// </summary>
    public static int[] Split(IReadOnlyList<double> strength, Random rng, IReadOnlyList<int>? previous = null)
    {
        var n = strength.Count;
        if (n == 0) return [];
        if (n == 1) return [0];
        if (n > MaxExact) return Snake(strength);

        // Перший гравець завжди в команді 0 — так кожен поділ трапляється один раз, а не двічі з перейменуванням.
        var small = n / 2;
        var masks = new List<(int Mask, double Diff)>();
        var best = double.MaxValue;
        for (var mask = 0; mask < 1 << (n - 1); mask++)
        {
            var m = mask << 1;   // біт i — гравець i у команді 1
            var ones = System.Numerics.BitOperations.PopCount((uint)m);
            if (ones != small && ones != n - small) continue;
            double a = 0, b = 0;
            for (var i = 0; i < n; i++) if ((m >> i & 1) == 1) b += strength[i]; else a += strength[i];
            var diff = Math.Abs(a - b);
            masks.Add((m, diff));
            if (diff < best) best = diff;
        }
        var pool = masks.Where(x => x.Diff <= best + Slack + 1e-9).Select(x => x.Mask).ToList();
        if (previous is not null && pool.Count > 1)
        {
            var fresh = pool.Where(m => !Same(m, n, previous)).ToList();
            if (fresh.Count > 0) pool = fresh;
        }
        var pick = pool[rng.Next(pool.Count)];
        var flip = rng.Next(2);   // хто з команд «нульова» (колір, бік поля) — теж жеребом
        var r = new int[n];
        for (var i = 0; i < n; i++) r[i] = (pick >> i & 1) ^ flip;
        return r;
    }

    /// <summary>Чи поділ <paramref name="mask"/> той самий, що й <paramref name="previous"/>, з точністю до назв команд.</summary>
    static bool Same(int mask, int n, IReadOnlyList<int> previous)
    {
        bool direct = true, swapped = true, any = false;
        for (var i = 0; i < n && i < previous.Count; i++)
        {
            if (previous[i] is not (0 or 1)) continue;
            any = true;
            var t = mask >> i & 1;
            if (t != previous[i]) direct = false;
            if (t == previous[i]) swapped = false;
        }
        return any && (direct || swapped);
    }

    /// <summary>Великий стіл: найсильніший — у 0, двоє наступних — у 1, далі знову в 0… (0 1 1 0 0 1 1 0).</summary>
    static int[] Snake(IReadOnlyList<double> strength)
    {
        var order = Enumerable.Range(0, strength.Count).OrderByDescending(i => strength[i]).ThenBy(i => i).ToArray();
        var r = new int[strength.Count];
        for (var k = 0; k < order.Length; k++) r[order[k]] = (k + 1) / 2 % 2;
        return r;
    }

    /// <summary>Старий спосіб: через одного в порядку місць.</summary>
    public static int[] Alternate(int n) => [.. Enumerable.Range(0, n).Select(i => i % 2)];

    /// <summary>
    /// Поділ для гри: <paramref name="seats"/> — усі, хто грає (люди й боти, як гра їх перелічує), <paramref name="botLevel"/>
    /// — рівень бота на місці або null для людини. Сила людей — з <see cref="ISkills"/> у сервісах столу (нема — 0),
    /// попередній поділ — з <paramref name="memory"/> за ніком (місця на «Ще раз» зсуваються, ніки — ні). Повертає
    /// команду для кожного елемента <paramref name="seats"/>.
    /// </summary>
    public static int[] ForSeats(IRoomContext ctx, string gameId, IReadOnlyList<int> seats,
        Func<int, LiveBots.Level?> botLevel, Memory memory)
    {
        var skills = ctx.Services?.GetService<ISkills>();
        var who = new string[seats.Count];
        var power = new double[seats.Count];
        for (var i = 0; i < seats.Count; i++)
        {
            var s = seats[i];
            if (botLevel(s) is { } lvl)
            {
                who[i] = "🤖" + s;
                power[i] = BotStrength(lvl);
            }
            else
            {
                var nick = ctx.NickOf(s);
                who[i] = EconomyStore.Key(nick);
                power[i] = skills?.Strength(gameId, nick) ?? 0;
            }
        }
        var prev = memory.Last is { } last ? who.Select(w => last.TryGetValue(w, out var t) ? t : -1).ToArray() : null;
        var teams = Split(power, ctx.Rng, prev);
        memory.Last = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < who.Length; i++) memory.Last[who[i]] = teams[i];
        return teams;
    }

    /// <summary>Хто з ким був минулого разу (живе в екземплярі гри — він той самий на всі «Ще раз» столу).</summary>
    public sealed class Memory
    {
        public Dictionary<string, int>? Last;
    }

    /// <summary>Рядок у Журнал на старті партії з поділом за силою.</summary>
    public const string Note = "👥 Команди поділено за силою";
}
