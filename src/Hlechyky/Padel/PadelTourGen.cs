using System.Globalization;

namespace Hlechyky.Padel;

/// <summary>Матч турніру: корт, пари, рахунок (null — ще нема), живий матч на табло, хто вніс.</summary>
public sealed class PadelTMatch
{
    public int Court { get; set; }
    public string[] A { get; set; } = [];
    public string[] B { get; set; } = [];
    public int? Sa { get; set; }
    public int? Sb { get; set; }
    public string? Live { get; set; }
    public string? By { get; set; }
    public DateTimeOffset? At { get; set; }
    /// <summary>У плей-оф: final | third (обидва в одному раунді на різних кортах); в групах — null.</summary>
    public string? Stage { get; set; }
    /// <summary>Група "A"/"B" для формату groups.</summary>
    public string? Group { get; set; }
    public bool Scored => Sa is not null && Sb is not null;
}

public sealed class PadelRound
{
    public int N { get; set; }
    /// <summary>group | semi | final | null.</summary>
    public string? Stage { get; set; }
    public bool Ready { get; set; }
    public List<PadelTMatch> Matches { get; set; } = [];
    public List<string> Sit { get; set; } = [];
    public bool Done => Ready && Matches.Count > 0 && Matches.All(m => m.Scored);
}

/// <summary>Рядок таблиці турніру: одиниця — гравець (особисті формати) чи пара.</summary>
public sealed record PadelRow(string[] Unit, int Pts, int Con, int Pl, int W, int D, int Sat, double Avg, int Diff, List<int> List);

public sealed record PadelPlan(int Slots, int Sit, int PerRound, int Fit, int Full, int Fair, int Rec, bool Avg, string? Note);

public sealed record PadelAward(string Key, string Emoji, string Title, string Text, string[] Pids);

/// <summary>
/// Розклади турнірів — перенесено з макета (<c>genAmericano / roundAmericano / roundMexicano / chooseSit / statsFrom /
/// standings / renderFinal</c>) і доповнено міксом, «Королем корту», колом і групами. Випадковість — лише з
/// переданого <see cref="Random"/> (сід турніру), тож той самий турнір складається так само.
/// </summary>
public static class PadelTourGen
{
    public static readonly string[] Formats = ["americano", "mexicano", "mixed", "king", "team", "groups"];
    public static bool PairFormat(string f) => f is "team" or "groups";

    public static int SlotsFor(int n, int courts) => Math.Min(courts * 4, n / 4 * 4);

    static string Pk(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

    /// <summary>Хто з ким уже грав у парі (pc) і проти (oc), скільки разів відпочивав, хто сидів щойно.</summary>
    public sealed class Stats
    {
        public Dictionary<string, int> Pc { get; } = [];
        public Dictionary<string, int> Oc { get; } = [];
        public Dictionary<string, int> Sat { get; } = [];
        public HashSet<string> Last { get; set; } = [];
        int Get(Dictionary<string, int> d, string k) => d.TryGetValue(k, out var v) ? v : 0;
        public int PcOf(string a, string b) => Get(Pc, Pk(a, b));
        public int OcOf(string a, string b) => Get(Oc, Pk(a, b));
        public int SatOf(string a) => Get(Sat, a);

        public void Add(PadelRound r)
        {
            static void Inc(Dictionary<string, int> d, string k) => d[k] = (d.TryGetValue(k, out var v) ? v : 0) + 1;
            foreach (var m in r.Matches)
            {
                Inc(Pc, Pk(m.A[0], m.A[1]));
                Inc(Pc, Pk(m.B[0], m.B[1]));
                foreach (var x in m.A) foreach (var y in m.B) Inc(Oc, Pk(x, y));
            }
            foreach (var s in r.Sit) Inc(Sat, s);
            Last = [.. r.Sit];
        }

        public static Stats From(IEnumerable<PadelRound> rounds)
        {
            var st = new Stats();
            foreach (var r in rounds) if (r.Ready) st.Add(r);
            return st;
        }
    }

    static List<T> Shuffle<T>(IEnumerable<T> src, Random rnd)
    {
        var a = src.ToList();
        for (var i = a.Count - 1; i > 0; i--) { var j = rnd.Next(i + 1); (a[i], a[j]) = (a[j], a[i]); }
        return a;
    }

    /// <summary>Хто сидить: найменше відпочивали, і не ті, хто сидів щойно (за рівних — навмання).</summary>
    public static List<string> ChooseSit(IEnumerable<string> ids, int k, Stats st, Random rnd) =>
        k <= 0 ? [] : [.. Shuffle(ids, rnd).OrderBy(st.SatOf).ThenBy(x => st.Last.Contains(x) ? 1 : 0).Take(k)];

    static int Cost(Stats st, string[] a, string[] b)
    {
        int Oc(string x, string y) { var v = st.OcOf(x, y); return v * v; }
        return 100 * (st.PcOf(a[0], a[1]) + st.PcOf(b[0], b[1])) + 3 * (Oc(a[0], b[0]) + Oc(a[0], b[1]) + Oc(a[1], b[0]) + Oc(a[1], b[1]));
    }

    public static PadelRound RoundAmericano(IReadOnlyList<string> ids, int courts, Stats st, int iters, Random rnd)
    {
        var sit = ChooseSit(ids, ids.Count - SlotsFor(ids.Count, courts), st, rnd);
        var play = ids.Where(i => !sit.Contains(i)).ToList();
        List<(string[] A, string[] B)>? best = null;
        var bc = int.MaxValue;
        for (var it = 0; it < iters && bc > 0; it++)
        {
            var sh = Shuffle(play, rnd);
            var cost = 0;
            var ms = new List<(string[], string[])>();
            for (var g = 0; g + 3 < sh.Count; g += 4)
            {
                string p = sh[g], q = sh[g + 1], x = sh[g + 2], y = sh[g + 3];
                (string[] A, string[] B)? bo = null;
                var boc = int.MaxValue;
                foreach (var (a, b) in new[] { (new[] { p, q }, new[] { x, y }), (new[] { p, x }, new[] { q, y }), (new[] { p, y }, new[] { q, x }) })
                {
                    var c = Cost(st, a, b);
                    if (c < boc) { boc = c; bo = (a, b); }
                }
                cost += boc;
                ms.Add(bo!.Value);
            }
            if (cost < bc) { bc = cost; best = ms; }
        }
        return Make(best ?? [], sit);
    }

    static PadelRound Make(List<(string[] A, string[] B)> ms, List<string> sit) => new()
    {
        Ready = true,
        Matches = [.. ms.Select((m, i) => new PadelTMatch { Court = i + 1, A = m.A, B = m.B })],
        Sit = sit,
    };

    /// <summary>Усі раунди американо: 40 перезапусків, найкращий — без повторів напарників і з рівними суперниками.</summary>
    public static List<PadelRound> Americano(IReadOnlyList<string> ids, int courts, int n, Random rnd) =>
        Restarts(n, st => RoundAmericano(ids, courts, st, 250, rnd));

    static List<PadelRound> Restarts(int n, Func<Stats, PadelRound> round)
    {
        List<PadelRound>? best = null;
        var bs = int.MaxValue;
        for (var a = 0; a < 40; a++)
        {
            var st = new Stats();
            var rs = new List<PadelRound>();
            for (var r = 0; r < n; r++) { var x = round(st); rs.Add(x); st.Add(x); }
            var rep = st.Pc.Values.Sum(v => v > 1 ? v - 1 : 0);
            var om = st.Oc.Values.ToList();
            var spread = om.Count > 0 ? om.Max() - om.Min() : 0;
            var score = rep * 100 + spread;
            if (score < bs) { bs = score; best = rs; }
            if (rep == 0 && spread <= 1) break;
        }
        return best ?? [];
    }

    /// <summary>Мікст: кожна пара — жінка й чоловік; зайві відпочивають по черзі (окремо серед жінок і чоловіків).</summary>
    public static PadelRound RoundMixed(IReadOnlyList<string> women, IReadOnlyList<string> men, int courts, Stats st, int iters, Random rnd)
    {
        var k = Math.Min(Math.Min(women.Count, men.Count), courts * 2);
        k -= k % 2;
        var sit = ChooseSit(women, women.Count - k, st, rnd).Concat(ChooseSit(men, men.Count - k, st, rnd)).ToList();
        var w = women.Where(x => !sit.Contains(x)).ToList();
        var m = men.Where(x => !sit.Contains(x)).ToList();
        List<(string[] A, string[] B)>? best = null;
        var bc = int.MaxValue;
        for (var it = 0; it < iters && bc > 0; it++)
        {
            var sw = Shuffle(w, rnd);
            var sm = Shuffle(m, rnd);
            var ms = new List<(string[], string[])>();
            var cost = 0;
            for (var g = 0; g + 1 < k; g += 2)
            {
                string[] a = [sw[g], sm[g]], b = [sw[g + 1], sm[g + 1]];
                cost += Cost(st, a, b);
                ms.Add((a, b));
            }
            if (cost < bc) { bc = cost; best = ms; }
        }
        return Make(best ?? [], sit);
    }

    public static List<PadelRound> Mixed(IReadOnlyList<string> women, IReadOnlyList<string> men, int courts, int n, Random rnd) =>
        Restarts(n, st => RoundMixed(women, men, courts, st, 250, rnd));

    /// <summary>Мексикано: за таблицею четвірками — 1+3 проти 2+4 (перший раунд — навмання).</summary>
    public static PadelRound Mexicano(IReadOnlyList<string> ids, int courts, Stats st, IReadOnlyList<string> order, Random rnd)
    {
        var sit = ChooseSit(ids, ids.Count - SlotsFor(ids.Count, courts), st, rnd);
        var play = order.Where(i => !sit.Contains(i)).ToList();
        var ms = new List<(string[], string[])>();
        for (var g = 0; g + 3 < play.Count; g += 4) ms.Add(([play[g], play[g + 2]], [play[g + 1], play[g + 3]]));
        return Make(ms, sit);
    }

    public static PadelRound First(IReadOnlyList<string> ids, int courts, Random rnd) =>
        Mexicano(ids, courts, new Stats(), Shuffle(ids, rnd), rnd);

    /// <summary>
    /// «Король корту»: переможці корту k → k−1 (верхній лишається), переможені → k+1 (нижній лишається); на корті
    /// пари перебиваються — кожен грає з кимось із другої пари. Нічия — вгору пара з кращою різницею за турнір
    /// (<paramref name="diff"/>), далі навмання. Хто відпочивав, заходить на нижній корт.
    /// </summary>
    public static PadelRound King(PadelRound prev, IReadOnlyList<string> ids, int courts, Stats st, Func<string, int> diff, Random rnd)
    {
        var ms = prev.Matches.OrderBy(m => m.Court).ToList();
        var c = ms.Count;
        var arrive = Enumerable.Range(0, c).Select(_ => new List<string[]>()).ToList();
        for (var i = 0; i < c; i++)
        {
            var m = ms[i];
            var aUp = m.Sa > m.Sb;
            if (m.Sa == m.Sb)
            {
                int da = m.A.Sum(diff), db = m.B.Sum(diff);
                aUp = da != db ? da > db : rnd.Next(2) == 0;
            }
            var (up, down) = aUp ? (m.A, m.B) : (m.B, m.A);
            arrive[Math.Max(0, i - 1)].Add(up);
            arrive[Math.Min(c - 1, i + 1)].Add(down);
        }
        var queue = arrive.SelectMany(a => a.SelectMany(p => p)).ToList();
        var sit = ChooseSit(ids, ids.Count - SlotsFor(ids.Count, courts), st, rnd);
        queue = [.. queue.Where(p => !sit.Contains(p)), .. prev.Sit.Where(p => !sit.Contains(p))];
        var next = new List<(string[], string[])>();
        for (var g = 0; g + 3 < queue.Count; g += 4)
            next.Add(([queue[g], queue[g + 2]], [queue[g + 1], queue[g + 3]]));
        return Make(next, sit);
    }

    /// <summary>Коло (circle method): раунди пар індексів; при непарній кількості хтось відпочиває.</summary>
    public static List<List<(int A, int B)>> Circle(int n)
    {
        var list = Enumerable.Range(0, n).ToList();
        if (n % 2 == 1) list.Add(-1);
        var m = list.Count;
        var rounds = new List<List<(int, int)>>();
        for (var r = 0; r < m - 1; r++)
        {
            var round = new List<(int, int)>();
            for (var i = 0; i < m / 2; i++)
            {
                int a = list[i], b = list[m - 1 - i];
                if (a >= 0 && b >= 0) round.Add((a, b));
            }
            rounds.Add(round);
            list.Insert(1, list[^1]);
            list.RemoveAt(list.Count - 1);
        }
        return rounds;
    }

    /// <summary>Розкласти матчі по раундах: до <paramref name="courts"/> на раунд, пара — не двічі за раунд.</summary>
    public static List<List<(int A, int B, string? G)>> Pack(IEnumerable<(int A, int B, string? G)> matches, int courts)
    {
        var left = matches.ToList();
        var rounds = new List<List<(int, int, string?)>>();
        while (left.Count > 0)
        {
            var used = new HashSet<int>();
            var round = new List<(int, int, string?)>();
            foreach (var x in left.ToList())
            {
                if (round.Count >= courts) break;
                if (used.Contains(x.A) || used.Contains(x.B)) continue;
                used.Add(x.A); used.Add(x.B);
                round.Add(x);
                left.Remove(x);
            }
            rounds.Add(round);
        }
        return rounds;
    }

    public static PadelRound PairRound(List<string[]> pairs, List<(int A, int B, string? G)> ms, string? stage)
    {
        var playing = ms.SelectMany(m => new[] { m.A, m.B }).ToHashSet();
        return new PadelRound
        {
            Ready = true, Stage = stage,
            Matches = [.. ms.Select((m, i) => new PadelTMatch { Court = i + 1, A = pairs[m.A], B = pairs[m.B], Group = m.G })],
            Sit = [.. pairs.Where((_, i) => !playing.Contains(i)).SelectMany(p => p)],
        };
    }

    /// <summary>«Кожна з кожною» фіксованими парами: кола повторюються, поки не набереться <paramref name="n"/> раундів.</summary>
    public static List<PadelRound> Team(List<string[]> pairs, int courts, int n)
    {
        var circle = Circle(pairs.Count).SelectMany(r => r).Select(x => (x.A, x.B, (string?)null)).ToList();
        var rounds = new List<PadelRound>();
        while (rounds.Count < n && circle.Count > 0)
            foreach (var r in Pack(circle, courts))
                if (rounds.Count < n) rounds.Add(PairRound(pairs, r, null));
        return rounds;
    }

    /// <summary>Групи: ≥6 пар — дві змійкою за порядком, інакше одна. Повертає індекси пар у групах.</summary>
    public static List<List<int>> Groups(int pairs)
    {
        if (pairs < 6) return [[.. Enumerable.Range(0, pairs)]];
        var a = new List<int>();
        var b = new List<int>();
        for (var i = 0; i < pairs; i++) (i % 4 is 0 or 3 ? a : b).Add(i);
        return [a, b];
    }

    public static List<PadelRound> GroupRounds(List<string[]> pairs, List<List<int>> groups, int courts)
    {
        var circles = groups.Select((g, gi) => Circle(g.Count)
            .Select(r => r.Select(x => (g[x.A], g[x.B], (string?)((char)('A' + gi)).ToString())).ToList()).ToList()).ToList();
        var all = new List<(int, int, string?)>();
        for (var r = 0; r < circles.Max(c => c.Count); r++)
            foreach (var c in circles) if (r < c.Count) all.AddRange(c[r]);
        return [.. Pack(all, courts).Select(r => PairRound(pairs, r, "group"))];
    }

    // ------------------------------------------------------------------ таблиця

    /// <summary>
    /// Таблиця за раундами (до <paramref name="upto"/>): очки, пропущені, матчі, перемоги, нічиї, відпочинки (лише за
    /// повні раунди), середнє, різниця. Очок за відпочинок нема.
    /// </summary>
    public static List<PadelRow> Table(List<string[]> units, IEnumerable<PadelRound> rounds, string rankBy, Func<string[], string> name)
    {
        var acc = units.Select(u => (U: u, Pts: 0, Con: 0, Pl: 0, W: 0, D: 0, Sat: 0, List: new List<int>())).ToList();
        int Find(string[] side) => acc.FindIndex(a => a.U.Length == side.Length ? a.U.All(side.Contains) : false);
        int FindOne(string p) => acc.FindIndex(a => a.U.Length == 1 && a.U[0] == p);
        var pairs = units.Count > 0 && units[0].Length == 2;
        foreach (var r in rounds)
        {
            if (!r.Ready) continue;
            foreach (var m in r.Matches)
            {
                if (!m.Scored) continue;
                foreach (var (side, sc, oc) in new[] { (m.A, m.Sa!.Value, m.Sb!.Value), (m.B, m.Sb!.Value, m.Sa!.Value) })
                {
                    int[] idx = pairs ? [Find(side)] : [.. side.Select(FindOne)];
                    foreach (var i in idx)
                    {
                        if (i < 0) continue;
                        var a = acc[i];
                        a.Pts += sc; a.Con += oc; a.Pl++; a.List.Add(sc);
                        if (sc > oc) a.W++; else if (sc == oc) a.D++;
                        acc[i] = a;
                    }
                }
            }
            if (r.Done)
                for (var i = 0; i < acc.Count; i++)
                    if (acc[i].U.All(r.Sit.Contains)) { var a = acc[i]; a.Sat++; acc[i] = a; }
        }
        var rows = acc.Select(a => new PadelRow(a.U, a.Pts, a.Con, a.Pl, a.W, a.D, a.Sat, a.Pl > 0 ? (double)a.Pts / a.Pl : 0, a.Pts - a.Con, a.List)).ToList();
        var uk = StringComparer.Create(CultureInfo.GetCultureInfo("uk-UA"), true);
        IOrderedEnumerable<PadelRow> sorted = rankBy switch
        {
            "avg" => rows.OrderByDescending(r => r.Avg).ThenByDescending(r => r.Pts).ThenByDescending(r => r.W),
            "wins" => rows.OrderByDescending(r => r.W).ThenByDescending(r => r.Diff).ThenByDescending(r => r.Pts),
            _ => rows.OrderByDescending(r => r.Pts).ThenByDescending(r => r.W).ThenByDescending(r => r.Diff),
        };
        return [.. sorted.ThenBy(r => name(r.Unit), uk)];
    }

    // ------------------------------------------------------------------ план

    static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    /// <summary>
    /// Порада, скільки раундів: perRound = total·0,5+2 хв (на час — minutes+2), fit — скільки влазить в оренду, full —
    /// усі з усіма, fair — найменше раундів, за якого всі відпочивають порівну; rec — найбільше кратне fair ≤ fit
    /// (американо — ще й ≤ full), а як нема — fit і «середнє».
    /// </summary>
    public static PadelPlan Plan(string format, int n, int courts, string total, int? minutes, double? booking)
    {
        courts = Math.Max(1, courts);
        var pair = PairFormat(format);
        var perMatch = pair ? Math.Min(courts, n / 2) : SlotsFor(n, courts) / 4;
        var slots = pair ? perMatch * 2 : perMatch * 4;
        var sit = Math.Max(0, n - slots);
        var perRound = total == "time" ? (minutes ?? 15) + 2 : (int)Math.Round(int.Parse(total, CultureInfo.InvariantCulture) * 0.5 + 2, MidpointRounding.AwayFromZero);
        var fit = Math.Max(1, (int)Math.Floor((booking ?? 2) * 60 / perRound));
        var full = format switch
        {
            "americano" or "mixed" => slots > 0 ? (int)Math.Ceiling((double)(n - 1) * n / slots) : 0,
            "team" => perMatch > 0 ? Pack(Circle(n).SelectMany(r => r).Select(x => (x.A, x.B, (string?)null)), courts).Count : 0,
            "groups" => GroupRounds([.. Enumerable.Range(0, n).Select(i => new[] { "p" + i, "q" + i })], Groups(n), courts).Count,
            _ => fit,
        };
        var fair = sit == 0 || n == 0 ? 1 : n / Gcd(n, sit);
        if (format == "groups")
        {
            var groups = Groups(n).Count;
            var playoff = groups == 2 || n >= 4 ? (courts >= 2 ? 2 : 4) : 1;
            return new(slots, sit, perRound, fit, full, fair, full + playoff, false, null);
        }
        var limit = format == "americano" ? Math.Min(fit, Math.Max(1, full)) : fit;
        var rec = limit / fair * fair;
        return rec > 0
            ? new(slots, sit, perRound, fit, full, fair, rec, false, null)
            : new(slots, sit, perRound, fit, full, fair, Math.Max(1, limit), true, "Порівну відпочити не вийде — таблиця рахуватиме середнє за матч");
    }

    // ------------------------------------------------------------------ нагороди

    static string Plural(int n, string a, string b, string c)
    {
        n = Math.Abs(n) % 100;
        var d = n % 10;
        if (n is > 10 and < 20) return c;
        if (d is > 1 and < 5) return b;
        return d == 1 ? a : c;
    }

    static double Sd(List<int> a)
    {
        var m = a.Average();
        return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / a.Count);
    }

    /// <summary>Нагороди як <c>renderFinal</c> макета: найкраща пара, розгром, найрівніша гра, залізна стіна, стабільний.</summary>
    public static List<PadelAward> Awards(IReadOnlyList<PadelRound> rounds, List<PadelRow> table, Func<string, string> name)
    {
        var uk = CultureInfo.GetCultureInfo("uk-UA");
        string Names(IEnumerable<string> ps) => string.Join(" + ", ps.Select(name));
        var all = rounds.SelectMany((r, ri) => r.Matches.Where(m => m.Scored).Select(m => (M: m, Ri: ri))).ToList();
        var aw = new List<PadelAward>();
        if (all.Count == 0) return aw;
        var bp = all.SelectMany(x => new[] { (Ps: x.M.A, Sc: x.M.Sa!.Value, x.Ri), (Ps: x.M.B, Sc: x.M.Sb!.Value, x.Ri) })
            .Aggregate((a, b) => b.Sc > a.Sc ? b : a);
        aw.Add(new("pair", "🤝", "Найкраща пара",
            $"{Names(bp.Ps)} — {bp.Sc} {Plural(bp.Sc, "очко", "очки", "очок")} за матч (раунд {bp.Ri + 1})", bp.Ps));
        var rz = all.Aggregate((a, b) => Math.Abs(b.M.Sa!.Value - b.M.Sb!.Value) > Math.Abs(a.M.Sa!.Value - a.M.Sb!.Value) ? b : a);
        var rd = Math.Abs(rz.M.Sa!.Value - rz.M.Sb!.Value);
        if (rd > 0)
        {
            var aWon = rz.M.Sa > rz.M.Sb;
            var (w, l) = aWon ? (rz.M.A, rz.M.B) : (rz.M.B, rz.M.A);
            aw.Add(new("rout", "💥", "Найбільший розгром",
                $"{Math.Max(rz.M.Sa!.Value, rz.M.Sb!.Value)}:{Math.Min(rz.M.Sa!.Value, rz.M.Sb!.Value)} — {Names(w)} проти {Names(l)}", w));
        }
        if (all.Count > 1)
        {
            var eq = all.Aggregate((a, b) => Math.Abs(b.M.Sa!.Value - b.M.Sb!.Value) < Math.Abs(a.M.Sa!.Value - a.M.Sb!.Value) ? b : a);
            aw.Add(new("close", "⚖️", "Найрівніша гра", $"{eq.M.Sa}:{eq.M.Sb} у раунді {eq.Ri + 1}, корт {eq.M.Court}", [.. eq.M.A, .. eq.M.B]));
        }
        var played = table.Where(r => r.Pl > 0).ToList();
        if (played.Count > 0)
        {
            var wall = played.OrderBy(r => (double)r.Con / r.Pl).First();
            aw.Add(new("wall", "🧱", "Залізна стіна",
                $"{Names(wall.Unit)} — пропускав у середньому {((double)wall.Con / wall.Pl).ToString("0.0", uk)} за матч", wall.Unit));
        }
        var stab = played.Where(r => r.Pl >= 3).Select(r => (R: r, V: Sd(r.List))).OrderBy(x => x.V).FirstOrDefault();
        if (stab.R is not null)
            aw.Add(new("steady", "🎯", "Стабільний, як годинник", $"{Names(stab.R.Unit)} — розкид лише ±{stab.V.ToString("0.0", uk)}", stab.R.Unit));
        return aw;
    }
}
