namespace Hlechyky.Games.Impl;

/// <summary>
/// Ряди на сітці й Глек-бот для «Чотирьох у ряд»: виграє, коли може; блокує найближчого суперника, що от-от
/// збере ряд; не підставляє клітинку, над якою суперник виграє; далі — центр і ряди, де ще є простір.
/// Не геній, але й не дарує партій — для компанії саме те. Чужого нічого не підглядає: поле й так відкрите.
/// </summary>
public static class GridBot
{
    static readonly (int Dx, int Dy)[] Dirs = [(1, 0), (0, 1), (1, 1), (1, -1)];

    /// <summary>Ряд потрібної довжини через клітинку <paramref name="index"/> з міток <paramref name="mark"/> або <paramref name="mate"/>; null — нема.</summary>
    public static int[]? Line(string?[] cells, GridRules rules, int index, string mark, string mate)
    {
        var (w, h) = (rules.Width, rules.Height);
        var (x0, y0) = (index % w, index / w);
        foreach (var (dx, dy) in Dirs)
        {
            var line = new List<int> { index };
            foreach (var step in (int[])[1, -1])
                for (int x = x0 + dx * step, y = y0 + dy * step;
                     x >= 0 && x < w && y >= 0 && y < h && cells[y * w + x] is { } c && (c == mark || c == mate);
                     x += dx * step, y += dy * step)
                    line.Add(y * w + x);
            if (line.Count >= rules.Need) return [.. line.Order()];
        }
        return null;
    }

    /// <summary>Куди ляже фішка в колонці (гравітація) або сама клітинка; -1 — нікуди.</summary>
    static int Drop(string?[] cells, GridRules rules, int move)
    {
        if (!rules.Gravity) return move >= 0 && move < cells.Length && cells[move] is null ? move : -1;
        for (var row = rules.Height - 1; row >= 0; row--)
            if (cells[row * rules.Width + move] is null) return row * rules.Width + move;
        return -1;
    }

    static bool Wins(string?[] cells, GridRules rules, int at, string mark, string mate)
    {
        cells[at] = mark;
        var win = Line(cells, rules, at, mark, mate) is not null;
        cells[at] = null;
        return win;
    }

    /// <summary>
    /// Хід Глека: колонка (у «Чотирьох») або клітинка. <paramref name="foes"/> — мітки суперників у порядку черги
    /// (перший ходить одразу після бота). -1 — ходити нікуди.
    /// </summary>
    public static int Pick(Random rng, string?[] cells, GridRules rules, string me, string mate, string[] foes)
    {
        var moves = rules.Gravity ? rules.Width : cells.Length;
        var spots = new int[moves];
        var any = false;
        for (var m = 0; m < moves; m++) { spots[m] = Drop(cells, rules, m); any |= spots[m] >= 0; }
        if (!any) return -1;

        // 1. Виграти.
        for (var m = 0; m < moves; m++)
            if (spots[m] >= 0 && Wins(cells, rules, spots[m], me, mate)) return m;
        // 2. Заблокувати — найперше того, хто ходить наступним: пізнішого ще встигнуть зупинити інші.
        foreach (var foe in foes)
            for (var m = 0; m < moves; m++)
                if (spots[m] >= 0 && Wins(cells, rules, spots[m], foe, foe)) return m;

        // 3. Оцінка: центр, свої ряди з простором, і не підставлятись під виграш згори.
        var best = -1;
        var bestScore = int.MinValue;
        var center = (rules.Width - 1) / 2.0;
        for (var m = 0; m < moves; m++)
        {
            var at = spots[m];
            if (at < 0) continue;
            var x = at % rules.Width;
            var score = (int)((rules.Width / 2.0 - Math.Abs(x - center)) * 4) + rng.Next(3);
            score += Room(cells, rules, at, me, mate);
            if (rules.Gravity && at >= rules.Width)
            {
                var above = at - rules.Width;
                cells[at] = me;
                foreach (var foe in foes)
                    if (Wins(cells, rules, above, foe, foe)) { score -= 1000; break; }
                cells[at] = null;
            }
            if (score > bestScore) (best, bestScore) = (m, score);
        }
        return best;
    }

    /// <summary>Скільки «живих» рядів проходить через клітинку: у кожному вікні без чужих міток — квадрат своїх.</summary>
    static int Room(string?[] cells, GridRules rules, int at, string me, string mate)
    {
        var (w, h, need) = (rules.Width, rules.Height, rules.Need);
        var (x0, y0) = (at % w, at / w);
        var total = 0;
        foreach (var (dx, dy) in Dirs)
            for (var shift = 0; shift < need; shift++)
            {
                var (sx, sy) = (x0 - dx * shift, y0 - dy * shift);
                var own = 1;
                var ok = true;
                for (var k = 0; k < need && ok; k++)
                {
                    var (x, y) = (sx + dx * k, sy + dy * k);
                    if (x < 0 || x >= w || y < 0 || y >= h) { ok = false; break; }
                    var c = cells[y * w + x];
                    if (y * w + x == at) continue;
                    if (c is null) continue;
                    if (c == me || c == mate) own++;
                    else ok = false;
                }
                if (ok) total += own * own;
            }
        return total;
    }
}
