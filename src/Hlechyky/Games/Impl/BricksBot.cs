namespace Hlechyky.Games.Impl;

/// <summary>
/// Простий жадібний бот (дзеркало <c>botPlan</c> у bricks.js): перебирає оберти й зсуви, кожен варіант справді
/// програє на копії стіни й зважує результат цілими числами — висота, дірки, горбатість, ряди. Грає за стіл
/// «🤖 + боти» (<see cref="BricksBotDriver"/>), а ще ним зафіксовано журнали стенда й перф-тести.
/// </summary>
public static class BricksBot
{
    /// <summary>Скільки тиків думає бот зафіксованих журналів (BricksFixtures, стенд). Бот за столом має свій темп.</summary>
    public const int Delay = 8;
    const int WLines = 760, WHeight = 510, WHoles = 3560, WBump = 184;
    /// <summary>Скільки найкращих ходів пам'ятаємо — з них бот, що помиляється, бере не перший.</summary>
    public const int Top = 5;

    /// <summary>Клавіші для поточної фігурки: оберти, кроки (натиск+відпуск), жорстке падіння.</summary>
    public static int[] Plan(BricksCore c, BricksCore scratch) => Plan(c, scratch, 0);

    /// <summary>
    /// Те саме, але хід — <paramref name="pick"/>-й за оцінкою (0 — найкращий; більше, ніж варіантів, — останній
    /// з тих, що є). Рівні оцінки стоять у порядку перебору, тож <c>pick = 0</c> — рівно той хід, що й завжди.
    /// </summary>
    public static int[] Plan(BricksCore c, BricksCore scratch, int pick)
    {
        if (c.Type < 0 || !c.Alive) return [];
        Span<int> score = stackalloc int[Top];
        Span<int> rot = stackalloc int[Top];
        Span<int> sh = stackalloc int[Top];
        var n = 0;
        var rots = c.Type == BricksCore.O ? 1 : 4;
        for (var r = 0; r < rots; r++)
        {
            for (var dx = -5; dx <= 5; dx++)
            {
                scratch.CopyFrom(c);
                if (!Place(scratch, r, dx)) continue;
                var lines = scratch.Lines;
                scratch.Apply(BricksCore.KHard);
                var s = Score(scratch, scratch.Lines - lines);
                // вставка за спаданням; рівний стає ПІСЛЯ рівних — перший знайдений лишається першим
                var at = n;
                while (at > 0 && score[at - 1] < s) at--;
                if (at >= Top) continue;
                for (var i = Math.Min(n, Top - 1); i > at; i--)
                {
                    score[i] = score[i - 1];
                    rot[i] = rot[i - 1];
                    sh[i] = sh[i - 1];
                }
                score[at] = s;
                rot[at] = r;
                sh[at] = dx;
                if (n < Top) n++;
            }
        }
        if (n == 0) return [BricksCore.KHard];
        var k = Math.Clamp(pick, 0, n - 1);
        int bestR = rot[k], bestDx = sh[k];
        var keys = new List<int>(12);
        if (bestR == 3) keys.Add(BricksCore.KCcw);
        else for (var i = 0; i < bestR; i++) keys.Add(BricksCore.KCw);
        for (var i = 0; i < Math.Abs(bestDx); i++)
        {
            keys.Add(bestDx < 0 ? BricksCore.KLeft : BricksCore.KRight);
            keys.Add(bestDx < 0 ? BricksCore.KLeftUp : BricksCore.KRightUp);
        }
        keys.Add(BricksCore.KHard);
        return [.. keys];
    }

    static bool Place(BricksCore c, int r, int dx)
    {
        if (r == 3)
        {
            c.Apply(BricksCore.KCcw);
            if (c.Rot != 3) return false;
        }
        else
        {
            for (var i = 0; i < r; i++)
            {
                var was = c.Rot;
                c.Apply(BricksCore.KCw);
                if (c.Rot == was) return false;
            }
        }
        for (var i = 0; i < Math.Abs(dx); i++)
        {
            var bx = c.Bx;
            c.Apply(dx < 0 ? BricksCore.KLeft : BricksCore.KRight);
            c.Apply(dx < 0 ? BricksCore.KLeftUp : BricksCore.KRightUp);
            if (c.Bx == bx) return false;
        }
        return true;
    }

    /// <summary>Оцінка стіни так, ніби повні ряди вже зняли.</summary>
    public static int Score(BricksCore c, int lines)
    {
        if (!c.Alive) return int.MinValue + 1;
        int agg = 0, holes = 0, bump = 0, prev = -1;
        for (var x = 0; x < BricksCore.W; x++)
        {
            int ey = 0, top = 0;
            for (var y = 0; y < BricksCore.H; y++)
            {
                if (c.Mask[y] == BricksCore.FullRow) continue;
                if ((c.Mask[y] >> x & 1) != 0) top = ey + 1;
                ey++;
            }
            ey = 0;
            for (var y = 0; y < BricksCore.H; y++)
            {
                if (c.Mask[y] == BricksCore.FullRow) continue;
                if (ey < top && (c.Mask[y] >> x & 1) == 0) holes++;
                ey++;
            }
            agg += top;
            if (prev >= 0) bump += Math.Abs(top - prev);
            prev = top;
        }
        return lines * WLines - agg * WHeight - holes * WHoles - bump * WBump;
    }
}
