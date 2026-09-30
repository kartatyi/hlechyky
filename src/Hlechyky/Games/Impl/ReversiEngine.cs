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
    public static readonly Level Hard = new("hard", "сильний", 8, 160_000, 12, 0, 0);

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
    static readonly (ulong Corner, ulong C, ulong X)[] CornerZones =
    [
        (Bit(0), Bit(1) | Bit(8), Bit(9)), (Bit(7), Bit(6) | Bit(15), Bit(14)),
        (Bit(56), Bit(57) | Bit(48), Bit(49)), (Bit(63), Bit(62) | Bit(55), Bit(54)),
    ];
    const int CWeight = -20, XWeight = -50;

    /// <summary>Таблиця ваг, згорнута в маски за вагою: оцінка — кілька popcount замість циклу по 64 полях.</summary>
    static readonly (int W, ulong Mask)[] Classes =
        [.. Enumerable.Range(0, 64).GroupBy(i => W[i]).Where(g => g.Key != 0)
            .Select(g => (g.Key, g.Aggregate(0UL, (m, i) => m | Bit(i))))];

    /// <summary>Поля в порядку перебору: кути першими, X-поля останніми — альфа-бета від цього відсікає більше.</summary>
    static readonly int[] Order = [.. Enumerable.Range(0, 64).OrderByDescending(i => W[i]).ThenBy(i => i)];

    public static bool IsCorner(int cell) => (Corners & Bit(cell)) != 0;

    /// <summary>Оцінка позиції з погляду того, чиї фішки <paramref name="me"/> (він і ходить).</summary>
    public static int Evaluate(ulong me, ulong opp)
    {
        var score = 0;
        foreach (var (w, mask) in Classes) score += w * (Pop(me & mask) - Pop(opp & mask));
        foreach (var (corner, c, x) in CornerZones)
        {
            if (((me | opp) & corner) == 0) continue;
            // Кут уже зайнятий — штраф біля нього знімаємо (сусіди кута тоді вже не пастка).
            score -= CWeight * (Pop(me & c) - Pop(opp & c)) + XWeight * (Pop(me & x) - Pop(opp & x));
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

        // Точна кінцівка бере весь бюджет; не вклалась — звичайний пошук на половині бюджету (з 12 порожніх
        // глибина 8 і так бачить майже до кінця). Найгірший випадок — півтора бюджети.
        var budget = level.Nodes;
        if (level.Exact > 0 && 64 - Pop(me | opp) <= level.Exact)
        {
            var (cell, _, done) = SolveExact(core, level.Nodes);
            if (done) return cell;
            budget /= 2;
        }

        // Ітеративне поглиблення: глибина, яку бюджет не дорахував, не рахується — лишається попередня.
        var search = new Search(budget);
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

    /// <summary>Перебір до кінця партії: найкращий хід за точною різницею фішок, скільки вузлів пішло і чи вклались.</summary>
    public static (int Cell, int Nodes, bool Done) SolveExact(ReversiCore core, int budget)
    {
        ulong me = core.Discs[core.Side], opp = core.Discs[1 - core.Side];
        var list = Cells(Moves(me, opp)).ToList();
        if (list.Count == 0) return (-1, 0, true);
        var s = new Search(budget);
        var best = Root(me, opp, list, 64, s, true, null, out _);
        return (best, s.Nodes, !s.Out);
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

        if (exact && 64 - Pop(me | opp) > 6) return FastestFirst(me, opp, moves, alpha, beta, s);

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

    /// <summary>
    /// Точна кінцівка: спершу ходи, після яких у суперника найменше відповідей («найшвидший першим») — так
    /// перебір до кінця партії з 12 порожніх укладається в бюджет.
    /// </summary>
    static int FastestFirst(ulong me, ulong opp, ulong moves, int alpha, int beta, Search s)
    {
        Span<(int Key, int Cell, ulong Flips)> list = stackalloc (int, int, ulong)[Pop(moves)];
        var n = 0;
        foreach (var c in Cells(moves))
        {
            var f = Flips(me, opp, c);
            ulong nme = opp & ~f, nopp = me | f | Bit(c);
            list[n++] = (Pop(Moves(nme, nopp)) * 4 - (IsCorner(c) ? 3 : 0), c, f);
        }
        list.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Cell.CompareTo(b.Cell));
        var best = -Inf;
        foreach (var (_, c, f) in list)
        {
            var v = -Negamax(opp & ~f, me | f | Bit(c), 0, -beta, -alpha, s, true, false);
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
