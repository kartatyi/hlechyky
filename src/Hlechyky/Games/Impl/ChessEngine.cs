namespace Hlechyky.Games.Impl;

/// <summary>
/// Шаховий «мозок» Дядька Глека і розв'язувач задач. Навмисно простий: альфа-бета на 2–4 півходи з оцінкою
/// матеріалу й позиції (таблиці полів), взяття досчитуються тихим пошуком, щоб Глек не віддавав ферзя за пішака.
/// Бюджет — кількість вузлів, а не годинник: так хід Глека однаковий на однаковому сіді (тести), а стеля
/// вузлів тримає розрахунок у межах ~100 мс навіть у гущі середньої гри.
///
/// Мат у N (для «Задачі дня») — окремо: <see cref="ForcedMate"/> перебирає все без оцінок, бо там потрібна
/// не «добра» відповідь, а доказ.
/// </summary>
public static class ChessEngine
{
    /// <summary>Рівень Глека: глибина, шум в оцінці коренем (сотих пішака) і як часто він «позіхає» — бере не найкращий хід.</summary>
    public sealed record Level(string Key, int Depth, int Noise, int YawnPercent, int Nodes);

    public static readonly Level Easy = new("easy", 2, 60, 15, 12_000);
    public static readonly Level Medium = new("medium", 3, 12, 0, 40_000);

    public static Level LevelOf(string? key) => key == "medium" ? Medium : Easy;

    const int Inf = 1_000_000;
    public const int MateScore = 100_000;
    static readonly int[] Worth = [0, 100, 320, 330, 500, 900, 0];

    // Таблиці полів з боку білих (індекс як у ChessCore: 0 — a8, 63 — h1). Чорним — дзеркально (sq ^ 56).
    static readonly int[] PawnT =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 50, 50, 50, 50, 50, 50, 50, 50, 10, 10, 20, 30, 30, 20, 10, 10, 5, 5, 10, 25, 25, 10, 5, 5,
        0, 0, 0, 20, 20, 0, 0, 0, 5, -5, -10, 0, 0, -10, -5, 5, 5, 10, 10, -20, -20, 10, 10, 5, 0, 0, 0, 0, 0, 0, 0, 0,
    ];
    static readonly int[] KnightT =
    [
        -50, -40, -30, -30, -30, -30, -40, -50, -40, -20, 0, 0, 0, 0, -20, -40, -30, 0, 10, 15, 15, 10, 0, -30,
        -30, 5, 15, 20, 20, 15, 5, -30, -30, 0, 15, 20, 20, 15, 0, -30, -30, 5, 10, 15, 15, 10, 5, -30,
        -40, -20, 0, 5, 5, 0, -20, -40, -50, -40, -30, -30, -30, -30, -40, -50,
    ];
    static readonly int[] BishopT =
    [
        -20, -10, -10, -10, -10, -10, -10, -20, -10, 0, 0, 0, 0, 0, 0, -10, -10, 0, 5, 10, 10, 5, 0, -10,
        -10, 5, 5, 10, 10, 5, 5, -10, -10, 0, 10, 10, 10, 10, 0, -10, -10, 10, 10, 10, 10, 10, 10, -10,
        -10, 5, 0, 0, 0, 0, 5, -10, -20, -10, -10, -10, -10, -10, -10, -20,
    ];
    static readonly int[] RookT =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 5, 10, 10, 10, 10, 10, 10, 5, -5, 0, 0, 0, 0, 0, 0, -5, -5, 0, 0, 0, 0, 0, 0, -5,
        -5, 0, 0, 0, 0, 0, 0, -5, -5, 0, 0, 0, 0, 0, 0, -5, -5, 0, 0, 0, 0, 0, 0, -5, 0, 0, 0, 5, 5, 0, 0, 0,
    ];
    static readonly int[] KingMidT =
    [
        -30, -40, -40, -50, -50, -40, -40, -30, -30, -40, -40, -50, -50, -40, -40, -30, -30, -40, -40, -50, -50, -40, -40, -30,
        -30, -40, -40, -50, -50, -40, -40, -30, -20, -30, -30, -40, -40, -30, -30, -20, -10, -20, -20, -20, -20, -20, -20, -10,
        20, 20, 0, 0, 0, 0, 20, 20, 20, 30, 10, 0, 0, 10, 30, 20,
    ];

    /// <summary>Близькість до центру: 0 у кутку, 6 у самому центрі. Для ферзя й короля в ендшпілі.</summary>
    static int Center(int sq)
    {
        int f = sq & 7, r = sq >> 3;
        return 6 - (Math.Max(3 - f, f - 4) + Math.Max(3 - r, r - 4));
    }

    /// <summary>Оцінка з боку того, хто ходить: плюс — йому добре.</summary>
    public static int Evaluate(ChessCore c)
    {
        int score = 0, heavy = 0;
        for (var sq = 0; sq < 64; sq++)
        {
            var p = c.PieceAt(sq);
            if (p == 0) continue;
            var t = ChessCore.Type(p);
            if (t is ChessCore.Knight or ChessCore.Bishop or ChessCore.Rook or ChessCore.Queen) heavy += Worth[t];
        }
        var endgame = heavy <= 1300;
        for (var sq = 0; sq < 64; sq++)
        {
            var p = c.PieceAt(sq);
            if (p == 0) continue;
            var white = p > 0;
            var i = white ? sq : sq ^ 56;
            var t = ChessCore.Type(p);
            var v = Worth[t] + t switch
            {
                ChessCore.Pawn => PawnT[i],
                ChessCore.Knight => KnightT[i],
                ChessCore.Bishop => BishopT[i],
                ChessCore.Rook => RookT[i],
                ChessCore.Queen => Center(sq),
                _ => endgame ? Center(sq) * 8 : KingMidT[i],
            };
            score += white ? v : -v;
        }
        if (c.Variant == ChessVariant.Anti) score = -score;   // у піддавках матеріал — тягар
        return c.WhiteToMove ? score : -score;
    }

    // ------------------------------------------------------------------------------------------
    // Хід Глека
    // ------------------------------------------------------------------------------------------

    sealed class Search(ChessCore core, int budget)
    {
        public readonly ChessCore C = core;
        public int Nodes;
        public readonly int Budget = budget;
        public bool Out => Nodes > Budget;
    }

    /// <summary>
    /// Хід Глека на рівні <paramref name="level"/>. Ітеративне поглиблення до глибини рівня, поки вистачає вузлів;
    /// у корені кожен хід отримує чесну оцінку в межах шуму — тоді шум і «позіхання» обирають серед справді близьких.
    /// </summary>
    public static ChessMove? Pick(ChessCore c, Level level, Random rng)
    {
        var root = c.Legal();
        if (root.Count == 0) return null;
        if (root.Count == 1) return root[0];
        var s = new Search(c, level.Nodes);
        Order(c, root);
        var scores = new int[root.Count];
        var done = new int[root.Count];
        var have = false;
        for (var depth = 1; depth <= level.Depth; depth++)
        {
            var best = -Inf;
            var complete = true;
            for (var i = 0; i < root.Count; i++)
            {
                c.Make(root[i], out var u);
                // Вікно ширше за шум: ходи, гірші за найкращий більше ніж на 2 шуми, точної оцінки не потребують.
                var floor = best == -Inf ? -Inf : best - 2 * level.Noise - 1;
                var v = -Negamax(s, depth - 1, -Inf, -floor, 1);
                c.Unmake(root[i], u);
                if (s.Out) { complete = false; break; }
                done[i] = v;
                if (v > best) best = v;
            }
            if (!complete && have) break;
            Array.Copy(done, scores, root.Count);
            have = true;
            if (!complete) break;
            // Наступна глибина — у порядку з попередньої: найкращі спершу, альфа-бета ріже більше.
            var idx = Enumerable.Range(0, root.Count).OrderByDescending(k => scores[k]).ToArray();
            var sorted = idx.Select(k => root[k]).ToList();
            var ss = idx.Select(k => scores[k]).ToArray();
            root = sorted;
            Array.Copy(ss, scores, ss.Length);
            if (best >= MateScore - 100) break;   // мат знайдено — глибше нема чого
        }

        var noisy = new (int Score, int I)[root.Count];
        for (var i = 0; i < root.Count; i++)
            noisy[i] = (scores[i] + (level.Noise > 0 ? rng.Next(-level.Noise, level.Noise + 1) : 0), i);
        Array.Sort(noisy, (a, b) => b.Score.CompareTo(a.Score));
        var pick = noisy[0].I;
        // «Позіхнув»: бере другий-третій хід, але не той, що віддає мат, і не зі збитком понад пів пішака.
        if (level.YawnPercent > 0 && noisy.Length > 1 && rng.Next(100) < level.YawnPercent)
        {
            var alt = noisy[1 + rng.Next(Math.Min(2, noisy.Length - 1))].I;
            if (scores[alt] > -MateScore + 100 && scores[pick] - scores[alt] <= 150) pick = alt;
        }
        return root[pick];
    }

    static int Negamax(Search s, int depth, int alpha, int beta, int ply)
    {
        s.Nodes++;
        if (s.Out) return 0;
        var c = s.C;
        if (depth <= 0) return Quiet(s, alpha, beta, ply, 4);
        var moves = c.Variant == ChessVariant.Anti ? c.Legal() : c.PseudoLegal();
        Order(c, moves);
        var white = c.WhiteToMove;
        var any = false;
        foreach (var m in moves)
        {
            c.Make(m, out var u);
            if (c.Variant != ChessVariant.Anti && c.KingAttacked(white)) { c.Unmake(m, u); continue; }
            any = true;
            var v = -Negamax(s, depth - 1, -beta, -alpha, ply + 1);
            c.Unmake(m, u);
            if (s.Out) return 0;
            if (v >= beta) return v;
            if (v > alpha) alpha = v;
        }
        if (!any)
        {
            if (c.Variant == ChessVariant.Anti) return MateScore - ply;       // нема чим ходити — перемога
            return c.InCheck() ? -(MateScore - ply) : 0;
        }
        return alpha;
    }

    /// <summary>Тихий пошук: лише взяття, щоб оцінка не обривалась посеред розміну.</summary>
    static int Quiet(Search s, int alpha, int beta, int ply, int left)
    {
        var c = s.C;
        var stand = Evaluate(c);
        if (c.Variant == ChessVariant.Anti || left <= 0) return stand;   // у піддавках взяття й так обов'язкові — там досить оцінки
        if (stand >= beta) return stand;
        if (stand > alpha) alpha = stand;
        var moves = c.PseudoLegal();
        moves.RemoveAll(m => !c.IsCapture(m) && m.Promo == 0);
        Order(c, moves);
        var white = c.WhiteToMove;
        foreach (var m in moves)
        {
            s.Nodes++;
            if (s.Out) return 0;
            c.Make(m, out var u);
            if (c.KingAttacked(white)) { c.Unmake(m, u); continue; }
            var v = -Quiet(s, -beta, -alpha, ply + 1, left - 1);
            c.Unmake(m, u);
            if (v >= beta) return v;
            if (v > alpha) alpha = v;
        }
        return alpha;
    }

    /// <summary>Спершу взяття дорогого дешевим і перетворення — з ними відсікання працює найкраще.</summary>
    static void Order(ChessCore c, List<ChessMove> moves)
    {
        if (moves.Count < 2) return;
        var keys = new int[moves.Count];
        for (var i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            var cap = ChessCore.Type(c.CapturedPiece(m));
            keys[i] = (cap > 0 ? 10 * Worth[cap] - Worth[ChessCore.Type(c.PieceAt(m.From))] / 10 + 10_000 : 0) + (m.Promo != 0 ? 9_000 : 0);
        }
        var arr = moves.ToArray();
        Array.Sort(keys, arr);
        moves.Clear();
        for (var i = arr.Length - 1; i >= 0; i--) moves.Add(arr[i]);
    }

    // ------------------------------------------------------------------------------------------
    // Мат у N — для задач
    // ------------------------------------------------------------------------------------------

    /// <summary>Мат тому, хто зараз ходить.</summary>
    public static bool IsMate(ChessCore c) => c.InCheck() && c.Legal().Count == 0;

    /// <summary>Чи може той, хто ходить, заматувати не пізніше ніж своїм <paramref name="n"/>-м ходом проти будь-якого захисту.</summary>
    public static bool ForcedMate(ChessCore c, int n)
    {
        foreach (var m in c.Legal())
        {
            c.Make(m, out var u);
            var ok = n <= 1 ? IsMate(c) : Defenceless(c, n - 1);
            c.Unmake(m, u);
            if (ok) return true;
        }
        return false;
    }

    /// <summary>Хід захисту: чи будь-яка відповідь веде до мату за <paramref name="n"/> ходів нападу. Мат на дошці — теж так; пат — ні.</summary>
    public static bool Defenceless(ChessCore c, int n)
    {
        var replies = c.Legal();
        if (replies.Count == 0) return c.InCheck();
        foreach (var r in replies)
        {
            c.Make(r, out var u);
            var ok = ForcedMate(c, n);
            c.Unmake(r, u);
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Усі перші ходи, що ведуть до мату за n. У справжній задачі такий хід рівно один.</summary>
    public static List<ChessMove> MateKeys(ChessCore c, int n)
    {
        var keys = new List<ChessMove>();
        foreach (var m in c.Legal())
        {
            c.Make(m, out var u);
            var ok = n <= 1 ? IsMate(c) : Defenceless(c, n - 1);
            c.Unmake(m, u);
            if (ok) keys.Add(m);
        }
        return keys;
    }

    /// <summary>
    /// Відповідь захисту в задачі: та, після якої мату одним ходом нема (найупертіша), а серед рівних — випадкова.
    /// Так задача не розв'язується «сама», а в різних людей захист буває різний.
    /// </summary>
    public static ChessMove? Defend(ChessCore c, Random rng)
    {
        var replies = c.Legal();
        if (replies.Count == 0) return null;
        var stubborn = new List<ChessMove>();
        foreach (var r in replies)
        {
            c.Make(r, out var u);
            if (!ForcedMate(c, 1)) stubborn.Add(r);
            c.Unmake(r, u);
        }
        var pool = stubborn.Count > 0 ? stubborn : replies;
        return pool[rng.Next(pool.Count)];
    }
}
