using static Hlechyky.Games.Impl.ReversiCore;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Дядько Глек у реверсі. Рахує прямо на бітбордах <see cref="ReversiCore"/> і тримається бюджету вузлів,
/// а не годинника: так партія за сідом відтворюється, а найгірша позиція все одно вкладається в ~100 мс
/// (перевіряє Perf-тест) — рахує ж він під замком кімнати.
/// <list type="bullet">
/// <item>легкий — на хід уперед: вага поля плюс шум, частина ходів навмання, кути бачить не завжди;</item>
/// <item>звичайний — альфа-бета на глибину 3 з оцінкою «ваги полів + мобільність»;</item>
/// <item>сильний — ітеративне поглиблення до 8, кути першими, точна кінцівка з ≤ 12 порожніх.</item>
/// </list>
/// </summary>
public static class ReversiEngine
{
    /// <param name="Depth">Найглибше ітеративне поглиблення (півходів).</param>
    /// <param name="Nodes">Бюджет вузлів на один хід.</param>
    /// <param name="Exact">З якої кількості порожніх рахувати кінцівку до кінця (0 — ніколи).</param>
    /// <param name="Random">Відсоток зовсім випадкових ходів (лише легкий).</param>
    /// <param name="Noise">Шум до оцінки ходу в корені.</param>
    public sealed record Level(string Key, string Name, int Depth, int Nodes, int Exact, int Random, int Noise);

    public static readonly Level Easy = new("easy", "легкий", 1, 1_000, 0, 20, 30);
    public static readonly Level Medium = new("medium", "звичайний", 3, 40_000, 0, 0, 3);
    public static readonly Level Hard = new("hard", "сильний", 8, 250_000, 12, 0, 0);

    public static Level? LevelOf(string? key) => key switch
    {
        "easy" => Easy,
        "medium" => Medium,
        "hard" => Hard,
        _ => null,
    };

    /// <summary>Класична таблиця ваг: кути дорогі, X-поля (по діагоналі від кута) і C-поля (поруч із кутом) — погані.</summary>
    static readonly int[] W =
    [
        100, -20, 10,  5,  5, 10, -20, 100,
        -20, -50, -2, -2, -2, -2, -50, -20,
         10,  -2,  1,  1,  1,  1,  -2,  10,
          5,  -2,  1,  0,  0,  1,  -2,   5,
          5,  -2,  1,  0,  0,  1,  -2,   5,
         10,  -2,  1,  1,  1,  1,  -2,  10,
        -20, -50, -2, -2, -2, -2, -50, -20,
        100, -20, 10,  5,  5, 10, -20, 100,
    ];

    const ulong Corners = 0x8100000000000081UL;

    /// <summary>Кут і три його сусіди: поки кут порожній, сусіди — пастка; коли зайнятий, вони вже безпечні.</summary>
    static readonly (int Corner, int[] Near)[] CornerZones =
    [
        (0, [1, 8, 9]), (7, [6, 15, 14]), (56, [57, 48, 49]), (63, [62, 55, 54]),
    ];

    /// <summary>Поля в порядку перебору: кути першими, X-поля останніми — альфа-бета від цього відсікає більше.</summary>
    static readonly int[] Order = [.. Enumerable.Range(0, 64).OrderByDescending(i => W[i]).ThenBy(i => i)];

    public static bool IsCorner(int cell) => (Corners & Bit(cell)) != 0;

    /// <summary>Оцінка позиції з погляду того, чиї фішки <paramref name="me"/> (він і ходить).</summary>
    public static int Evaluate(ulong me, ulong opp)
    {
        var score = 0;
        for (var i = 0; i < 64; i++)
        {
            var b = Bit(i);
            if ((me & b) != 0) score += W[i];
            else if ((opp & b) != 0) score -= W[i];
        }
        foreach (var (corner, near) in CornerZones)
        {
            if (((me | opp) & Bit(corner)) == 0) continue;
            foreach (var n in near)   // кут уже зайнятий — штраф біля нього знімаємо
            {
                var b = Bit(n);
                if ((me & b) != 0) score -= W[n];
                else if ((opp & b) != 0) score += W[n];
            }
        }
        int mm = Pop(Moves(me, opp)), om = Pop(Moves(opp, me));
        score += 6 * (mm - om);
        // Під кінець важать уже самі фішки.
        var empties = 64 - Pop(me | opp);
        if (empties < 16) score += (16 - empties) * (Pop(me) - Pop(opp)) / 2;
        return score;
    }

    sealed class Search(int budget)
    {
        public int Nodes;
        public bool Out => Nodes > budget;
    }

    const int Inf = 1_000_000;
    const int WinBase = 100_000;

    /// <summary>
    /// Хід Глека за того, чия черга в <paramref name="core"/>. −1 — ходити нікуди (пас).
    /// </summary>
    public static int Pick(ReversiCore core, Level level, Random rng)
    {
        ulong me = core.Discs[core.Side], opp = core.Discs[1 - core.Side];
        var moves = Moves(me, opp);
        if (moves == 0) return -1;
        var list = Cells(moves).ToList();
        if (list.Count == 1) return list[0];

        if (level.Depth <= 1) return PickEasy(me, opp, list, level, rng);

        var empties = 64 - Pop(me | opp);
        if (level.Exact > 0 && empties <= level.Exact)
        {
            var exact = new Search(level.Nodes * 2);
            var best = Root(me, opp, list, empties + 1, exact, true, null, out _);
            if (!exact.Out && best >= 0) return best;
        }

        // Ітеративне поглиблення: глибина, яку бюджет не дорахував, не рахується — лишається попередня.
        var search = new Search(level.Nodes);
        int pick = list[0];
        Dictionary<int, int>? noise = level.Noise > 0 ? list.ToDictionary(c => c, _ => rng.Next(-level.Noise, level.Noise + 1)) : null;
        for (var depth = 1; depth <= level.Depth; depth++)
        {
            var best = Root(me, opp, list, depth, search, false, noise, out var scores);
            if (search.Out) break;
            pick = best;
            // Найкращий хід попередньої глибини — першим: відсікання на наступній різкіше.
            list = [.. list.OrderByDescending(c => scores[c])];
        }
        return pick;
    }

    static int PickEasy(ulong me, ulong opp, List<int> list, Level level, Random rng)
    {
        if (rng.Next(100) < level.Random) return list[rng.Next(list.Count)];
        // Кут бачить у двох випадках з трьох — в інших вважає його звичайним полем.
        var seesCorners = rng.Next(3) > 0;
        int best = list[0], bestScore = int.MinValue;
        foreach (var c in list)
        {
            var w = IsCorner(c) && !seesCorners ? 5 : W[c];
            var s = w + Pop(Flips(me, opp, c)) + rng.Next(level.Noise + 1);
            if (s > bestScore) (best, bestScore) = (c, s);
        }
        return best;
    }

    static int Root(ulong me, ulong opp, List<int> list, int depth, Search s, bool exact,
        Dictionary<int, int>? noise, out Dictionary<int, int> scores)
    {
        scores = [];
        int best = list[0], alpha = -Inf;
        foreach (var c in list)
        {
            var f = Flips(me, opp, c);
            var v = -Negamax(opp & ~f, me | f | Bit(c), depth - 1, -Inf, -alpha + (noise is null ? 0 : 64), s, exact, false);
            if (noise is not null) v += noise[c];
            scores[c] = v;
            if (s.Out) return best;
            if (v > alpha) (alpha, best) = (v, c);
        }
        return best;
    }

    static int Negamax(ulong me, ulong opp, int depth, int alpha, int beta, Search s, bool exact, bool passed)
    {
        s.Nodes++;
        if (s.Out) return 0;
        var moves = Moves(me, opp);
        if (moves == 0)
        {
            if (passed || Moves(opp, me) == 0) return Final(me, opp);
            return -Negamax(opp, me, depth, -beta, -alpha, s, exact, true);
        }
        if (depth <= 0 && !exact) return Evaluate(me, opp);

        var best = -Inf;
        foreach (var c in Order)
        {
            if ((moves & Bit(c)) == 0) continue;
            var f = Flips(me, opp, c);
            var v = -Negamax(opp & ~f, me | f | Bit(c), depth - 1, -beta, -alpha, s, exact, false);
            if (s.Out) return 0;
            if (v > best) best = v;
            if (v > alpha) alpha = v;
            if (alpha >= beta) break;
        }
        return best;
    }

    /// <summary>Кінець гри: виграш/програш важить більше за будь-яку оцінку, а всередині — різниця фішок.</summary>
    static int Final(ulong me, ulong opp)
    {
        var diff = Pop(me) - Pop(opp);
        return diff > 0 ? WinBase + diff : diff < 0 ? -WinBase + diff : 0;
    }
}
