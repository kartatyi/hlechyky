using System.Collections.Concurrent;
using System.Diagnostics;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Розв'язувач сапера «як людина без вгадування»: від відкритого старту крутить лише певні висновки, доки щось
/// відкривається. Правила: одиночне число (решта мін = 0 → усі закриті сусіди чисті; решта = закритим → усі міни),
/// пара сусідніх чисел (скільки мін може і мусить бути в «лише своїх» клітинках B, коли частину покриває A —
/// це й підмножини, і перетини), а наприкінці — загальний лічильник мін (лишилось 0 → решта чиста).
/// Висновки лише певні, тож міну як «чисту» він не відкриє ніколи.
/// </summary>
public static class MinesSolver
{
    static readonly ConcurrentDictionary<(int W, int H), int[][]> Nbrs = new();

    static int[][] Neighbours(MinesBoard b) => Nbrs.GetOrAdd((b.W, b.H), _ =>
    {
        var all = new int[b.Cells][];
        for (var c = 0; c < b.Cells; c++) all[c] = [.. b.Around(c)];
        return all;
    });

    /// <summary>Чи розв'язне поле чистою логікою від <paramref name="start"/> (усі безпечні клітинки відкриються).</summary>
    public static bool Solvable(MinesBoard board, int start) => Progress(board, start) == board.Safe;

    /// <summary>
    /// Скільки безпечних клітинок відкриє чиста логіка від <paramref name="start"/> (з каскадами нулів). Дорівнює
    /// <see cref="MinesBoard.Safe"/> — поле без вгадування. Саме поле не чіпає: знання тримає в своїх масивах.
    /// </summary>
    public static int Progress(MinesBoard board, int start)
    {
        if (!board.Ready || !board.Valid(start) || board.IsMine(start)) return 0;
        var n = board.Cells;
        var nb = Neighbours(board);
        var open = new bool[n];
        var mine = new bool[n];
        var opened = 0;
        var known = 0;
        var stack = new Stack<int>();

        bool Reveal(int cell)
        {
            if (open[cell] || mine[cell]) return false;
            if (board.IsMine(cell)) throw new InvalidOperationException("розв'язувач вивів міну як чисту");
            stack.Push(cell);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (open[c]) continue;
                open[c] = true;
                opened++;
                if (board.Near(c) != 0) continue;
                foreach (var x in nb[c])
                    if (!open[x]) stack.Push(x);
            }
            return true;
        }

        bool Mark(int cell)
        {
            if (mine[cell] || open[cell]) return false;
            if (!board.IsMine(cell)) throw new InvalidOperationException("розв'язувач вивів чисту клітинку як міну");
            mine[cell] = true;
            known++;
            return true;
        }

        Reveal(start);
        var conAt = new int[n];
        var unk = new List<int[]>();
        var rem = new List<int>();
        var cellOf = new List<int>();
        while (opened < board.Safe)
        {
            var progress = false;
            // обмеження фронту: відкрите число з закритими невідомими сусідами
            Array.Fill(conAt, -1);
            unk.Clear(); rem.Clear(); cellOf.Clear();
            for (var c = 0; c < n; c++)
            {
                if (!open[c] || board.Near(c) == 0) continue;
                var u = 0;
                var m = 0;
                foreach (var x in nb[c])
                    if (mine[x]) m++;
                    else if (!open[x]) u++;
                if (u == 0) continue;
                var arr = new int[u];
                var i = 0;
                foreach (var x in nb[c])
                    if (!mine[x] && !open[x]) arr[i++] = x;
                conAt[c] = unk.Count;
                unk.Add(arr);
                rem.Add(board.Near(c) - m);
                cellOf.Add(c);
            }

            // одиночні правила
            for (var k = 0; k < unk.Count; k++)
            {
                if (rem[k] == 0) { foreach (var x in unk[k]) progress |= Reveal(x); }
                else if (rem[k] == unk[k].Length) { foreach (var x in unk[k]) progress |= Mark(x); }
            }
            if (progress) continue;

            // пари сусідніх чисел (на відстані ≤ 2 — лише вони можуть ділити закриті клітинки)
            for (var a = 0; a < unk.Count; a++)
            {
                var ca = cellOf[a];
                var (ax, ay) = (ca % board.W, ca / board.W);
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var (x, y) = (ax + dx, ay + dy);
                        if ((dx == 0 && dy == 0) || x < 0 || y < 0 || x >= board.W || y >= board.H) continue;
                        var bi = conAt[y * board.W + x];
                        if (bi < 0) continue;
                        var A = unk[a];
                        var B = unk[bi];
                        var inter = 0;
                        foreach (var v in A)
                            if (Array.IndexOf(B, v) >= 0) inter++;
                        if (inter == 0) continue;
                        var aOnly = A.Length - inter;
                        var bOnly = B.Length - inter;
                        if (bOnly == 0) continue;
                        var (ra, rb) = (rem[a], rem[bi]);
                        // у перетині мін щонайменше ra − aOnly і щонайбільше min(ra, inter)
                        var maxB = rb - Math.Max(0, ra - aOnly);
                        var minB = rb - Math.Min(ra, inter);
                        if (maxB <= 0)
                        {
                            foreach (var v in B)
                                if (Array.IndexOf(A, v) < 0) progress |= Reveal(v);
                        }
                        else if (minB >= bOnly)
                        {
                            foreach (var v in B)
                                if (Array.IndexOf(A, v) < 0) progress |= Mark(v);
                        }
                    }
            }
            if (progress) continue;

            // загальний лічильник: усі міни вже знайдено — решта чиста
            if (known >= board.Mines)
                for (var c = 0; c < n; c++)
                    if (!open[c] && !mine[c]) progress |= Reveal(c);
            if (!progress) break;
        }
        return opened;
    }
}

/// <summary>Готове поле дня: де міни, з якого кандидата (1 — перший сід), чи справді без вгадування, скільки мс рахували.</summary>
public sealed record MinesLayout(int[] Mines, int Tries, bool NoGuess, double Ms);

/// <summary>
/// Поля дня без вгадування. Кандидати — детерміновано від сіду дня: перший — <c>Days.Seed(головоломка, день)</c>, далі
/// <c>Days.Seed("головоломка#k", день)</c>; перший розв'язний чистою логікою від центру — поле дня. Не знайшовся за
/// <see cref="MaxTries"/> — беремо кандидата, де логіка відкрила найбільше (вгадування мінімальне). Рахувати повільно
/// (мілісекунди-десятки), тому — не під замком кімнати: кеш у пам'яті, прогрів з <c>Configure</c> і фоном на старті дня.
/// </summary>
public static class MinesFields
{
    public const int MaxTries = 3000;

    static readonly ConcurrentDictionary<string, Lazy<MinesLayout>> Cache = new(StringComparer.Ordinal);

    public static int SeedOf(string puzzle, string day, int k) => k == 0 ? Days.Seed(puzzle, day) : Days.Seed($"{puzzle}#{k}", day);

    /// <summary>Порахувати поле без кешу (тести й заміри).</summary>
    public static MinesLayout Compute(string puzzle, string day, int w, int h, int mines, int center)
    {
        var sw = Stopwatch.StartNew();
        int[]? best = null;
        var bestProgress = -1;
        var bestTry = 0;
        for (var k = 0; k < MaxTries; k++)
        {
            var b = new MinesBoard(w, h, mines);
            b.Generate(new Random(SeedOf(puzzle, day, k)), center);
            var p = MinesSolver.Progress(b, center);
            if (p == b.Safe) return new MinesLayout(b.MineCells(), k + 1, true, sw.Elapsed.TotalMilliseconds);
            if (p > bestProgress) { bestProgress = p; best = b.MineCells(); bestTry = k + 1; }
        }
        return new MinesLayout(best ?? [], bestTry, false, sw.Elapsed.TotalMilliseconds);
    }

    static string Key(string puzzle, string day, int w, int h, int mines) => $"{puzzle}|{day}|{w}x{h}|{mines}";

    /// <summary>Поле з кешу; нема — рахує тут (і хто прийде слідом, чекає на те саме обчислення, а не рахує вдруге).</summary>
    public static MinesLayout Get(string puzzle, string day, int w, int h, int mines, int center) =>
        Cache.GetOrAdd(Key(puzzle, day, w, h, mines),
            _ => new Lazy<MinesLayout>(() => Compute(puzzle, day, w, h, mines, center), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Чи поле вже пораховане (тести).</summary>
    public static bool Ready(string puzzle, string day, int w, int h, int mines) =>
        Cache.TryGetValue(Key(puzzle, day, w, h, mines), out var l) && l.IsValueCreated;

    /// <summary>Порахувати фоном (не чекає).</summary>
    public static void Warm(string puzzle, string day, int w, int h, int mines, int center) =>
        _ = Task.Run(() => { try { Get(puzzle, day, w, h, mines, center); } catch { /* наступний Get спробує ще */ } });

    /// <summary>Викинути поля старші за вчора — щоб кеш не ріс вічно.</summary>
    public static void Prune(string today)
    {
        var yesterday = DateOnly.ParseExact(today, "yyyy-MM-dd").AddDays(-1).ToString("yyyy-MM-dd");
        foreach (var key in Cache.Keys)
        {
            var day = key.Split('|')[1];
            if (string.CompareOrdinal(day, yesterday) < 0) Cache.TryRemove(key, out _);
        }
    }
}
