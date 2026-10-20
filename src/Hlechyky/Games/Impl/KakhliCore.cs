using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>Які бувають кахлі на полі «Кахлів» (docs/games/specs/kakhli.md §2). Числа — на дроті.</summary>
public static class KakhliKind
{
    /// <summary>Звичайна розписна кахля.</summary>
    public const byte Plain = 0;
    /// <summary>Рушник уздовж рядка: знімає весь рядок.</summary>
    public const byte Row = 1;
    /// <summary>Рушник уздовж стовпчика: знімає весь стовпчик.</summary>
    public const byte Col = 2;
    /// <summary>Жаринка: вибух 3×3.</summary>
    public const byte Ember = 3;
    /// <summary>Чарівний глек: без барви, міняєш з будь-якою — зникає вся та барва.</summary>
    public const byte Glek = 4;

    public static bool Stripe(byte k) => k is Row or Col;
}

/// <summary>Що спалахнуло на полі (для анімації): <c>[тип, клітинка, аргумент]</c>.</summary>
public static class KakhliFx
{
    public const int Row = 1, Col = 2, Ember = 3, Glek = 4, Cross = 5, Wide = 6;
}

/// <summary>SplitMix64: стан — одне число, тож поле з мішечком кахлів переживає збереження й перезапуск.</summary>
public sealed class KakhliRng(ulong state)
{
    public ulong State = state;

    public ulong NextU()
    {
        var z = State += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Ціле в [0, max).</summary>
    public int Next(int max) => max <= 1 ? 0 : (int)(NextU() % (ulong)max);
}

/// <summary>
/// Один крок розв'язку ходу: що зникло, що спалахнуло, що з'явилось на місці збігу, і поле після того, як кахлі
/// впали й насипались нові. Браузер лише програє ці кроки — правил він не знає.
/// </summary>
public sealed class KakhliStep
{
    /// <summary>Id кахлів, що зникли.</summary>
    public readonly List<int> Gone = [];
    /// <summary>Кахлі, що з'явились чи змінились на місці: <c>[id, клітинка, барва, вид]</c>.</summary>
    public readonly List<int[]> Made = [];
    /// <summary>Спалахи спецкахлів: <c>[<see cref="KakhliFx"/>, клітинка, аргумент]</c>.</summary>
    public readonly List<int[]> Fx = [];
    /// <summary>Очки над полем: <c>[клітинка, очки]</c>.</summary>
    public readonly List<int[]> Pts = [];
    /// <summary>Глина, що тріснула: <c>[клітинка, скільки шарів лишилось]</c>.</summary>
    public readonly List<int[]> Clay = [];
    /// <summary>Клітинки, де розв'язалась мотузка.</summary>
    public readonly List<int> Rope = [];
    /// <summary>Нові кахлі згори: <c>[id, барва, вид]</c>.</summary>
    public readonly List<int[]> Add = [];
    /// <summary>Id на кожній клітинці після падіння (−1 — порожньо або дірка).</summary>
    public int[] Board = [];
    /// <summary>Котрий це каскад ходу: 1 — сам хід, 2+ — те, що зійшлося після падіння.</summary>
    public int Chain;
    /// <summary>Глек перемішав поле, бо ходів не лишилось.</summary>
    public bool Shuffle;
    /// <summary>Крок «Глек доливає» — уже після останнього ходу.</summary>
    public bool Final;
    public int Points;

    public object Wire() => new
    {
        g = Gone.ToArray(),
        m = Made.ToArray(),
        fx = Fx.ToArray(),
        p = Pts.ToArray(),
        cl = Clay.ToArray(),
        r = Rope.ToArray(),
        a = Add.ToArray(),
        b = Board,
        ch = Chain,
        sh = Shuffle,
        fin = Final,
        pts = Points,
    };
}

/// <summary>Стан поля в game_state (Persistent): усе, щоб продовжити рівно з того самого місця.</summary>
public sealed record KakhliBoardState(int W, int H, int Colors, int[] Id, int[] Color, int[] Kind, string Holes, string Clay,
    string Rope, ulong Rng, int NextId, long Score, int[] Collected, int Stripes, int Embers, int Gleks, int MaxChain);

/// <summary>
/// Поле «Кахлів» без жодного слова про кімнати: розкласти, перевірити обмін, зняти збіги з рушниками, жаринками й
/// глеками, обсипати й досипати кахлі з власного генератора, перемішати, коли ходів нема. Усі три гри — день, на час
/// і шлях кахляра — крутять саме це ядро, тож правила в них однакові до клітинки.
/// </summary>
public sealed class KakhliBoard
{
    public const int MaxColors = 6;
    /// <summary>Страховка від вічного каскаду: стільки кроків на хід найбільше.</summary>
    public const int MaxSteps = 40;
    public const int TilePoints = 10, ClayPoints = 20, RopePoints = 20;

    public int W { get; }
    public int H { get; }
    public int Colors { get; }
    public int Cells => W * H;

    readonly int[] _id;
    readonly sbyte[] _color;
    readonly byte[] _kind;
    readonly bool[] _hole;
    readonly byte[] _clay;
    readonly bool[] _rope;
    public KakhliRng Rng { get; private set; }
    int _nextId = 1;

    public long Score { get; set; }
    /// <summary>Скільки кахлів кожної барви знято (для цілей «збери»).</summary>
    public int[] Collected { get; } = new int[MaxColors];
    public int Stripes { get; private set; }
    public int Embers { get; private set; }
    public int Gleks { get; private set; }
    /// <summary>Найдовший каскад одним ходом.</summary>
    public int MaxChain { get; private set; }

    public KakhliBoard(int w, int h, int colors, ulong seed, string? map = null)
    {
        W = w;
        H = h;
        Colors = Math.Clamp(colors, 3, MaxColors);
        _id = new int[w * h];
        _color = new sbyte[w * h];
        _kind = new byte[w * h];
        _hole = new bool[w * h];
        _clay = new byte[w * h];
        _rope = new bool[w * h];
        Rng = new KakhliRng(seed);
        Array.Fill(_id, -1);
        Array.Fill(_color, (sbyte)-1);
        if (map is not null) Mark(map);
    }

    /// <summary>
    /// Розмітка рівня: рядки по W знаків. <c>.</c> — звичайна, <c>#</c> — дірки нема поля, <c>1</c>/<c>2</c> — сира глина
    /// в один-два шари, <c>r</c> — мотузка, <c>x</c> — мотузка на глині.
    /// </summary>
    void Mark(string map)
    {
        var rows = map.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var y = 0; y < H && y < rows.Length; y++)
            for (var x = 0; x < W && x < rows[y].Length; x++)
            {
                var c = y * W + x;
                switch (rows[y][x])
                {
                    case '#': _hole[c] = true; break;
                    case '1': _clay[c] = 1; break;
                    case '2': _clay[c] = 2; break;
                    case 'r': _rope[c] = true; break;
                    case 'x': _rope[c] = true; _clay[c] = 1; break;
                }
            }
    }

    public bool Valid(int c) => c >= 0 && c < Cells;
    public bool IsHole(int c) => _hole[c];
    public int IdAt(int c) => _id[c];
    public int ColorAt(int c) => _color[c];
    public byte KindAt(int c) => _kind[c];
    public int ClayAt(int c) => _clay[c];
    public bool RopeAt(int c) => _rope[c];
    public int ClayLeft => _clay.Sum(x => x);
    public int RopesLeft => _rope.Count(x => x);

    bool Movable(int c) => !_hole[c] && _id[c] >= 0 && !_rope[c];

    /// <summary>Клітинка-сусід у напрямку <paramref name="dir"/> (0 → · 1 ↓ · 2 ← · 3 ↑), −1 — за краєм.</summary>
    public int Neighbor(int c, int dir)
    {
        if (!Valid(c)) return -1;
        var (x, y) = (c % W, c / W);
        (x, y) = dir switch { 0 => (x + 1, y), 1 => (x, y + 1), 2 => (x - 1, y), 3 => (x, y - 1), _ => (-1, -1) };
        return x >= 0 && x < W && y >= 0 && y < H ? y * W + x : -1;
    }

    void Put(int c, int color, byte kind)
    {
        _id[c] = _nextId++;
        _color[c] = (sbyte)color;
        _kind[c] = kind;
    }

    // ---------- розклад ----------

    /// <summary>Розкласти поле без готових збігів і з хоча б одним ходом. Випадковість — лише з власного генератора.</summary>
    public void Fill()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            for (var c = 0; c < Cells; c++)
            {
                if (_hole[c]) continue;
                Put(c, FreshColor(c), KakhliKind.Plain);
            }
            if (FindMove() is not null) return;
        }
    }

    /// <summary>Барва, що не складе трійки з двома сусідами зліва чи згори (розклад без готових збігів).</summary>
    int FreshColor(int c)
    {
        Span<bool> banned = stackalloc bool[MaxColors];
        var (x, y) = (c % W, c / W);
        if (x >= 2 && SameColor(c - 1, c - 2)) banned[_color[c - 1]] = true;
        if (y >= 2 && SameColor(c - W, c - 2 * W)) banned[_color[c - W]] = true;
        var free = 0;
        for (var k = 0; k < Colors; k++) if (!banned[k]) free++;
        var pick = Rng.Next(free);
        for (var k = 0; k < Colors; k++)
        {
            if (banned[k]) continue;
            if (pick-- == 0) return k;
        }
        return 0;
    }

    bool SameColor(int a, int b) => _id[a] >= 0 && _id[b] >= 0 && _color[a] >= 0 && _color[a] == _color[b];

    // ---------- обмін ----------

    void Exchange(int a, int b)
    {
        (_id[a], _id[b]) = (_id[b], _id[a]);
        (_color[a], _color[b]) = (_color[b], _color[a]);
        (_kind[a], _kind[b]) = (_kind[b], _kind[a]);
    }

    /// <summary>Чи через клітинку <paramref name="c"/> іде лінія з трьох однакових.</summary>
    bool LineAt(int c)
    {
        if (_id[c] < 0 || _color[c] < 0) return false;
        var (x, y) = (c % W, c / W);
        var n = 1;
        for (var i = x - 1; i >= 0 && SameColor(c, y * W + i); i--) n++;
        for (var i = x + 1; i < W && SameColor(c, y * W + i); i++) n++;
        if (n >= 3) return true;
        n = 1;
        for (var j = y - 1; j >= 0 && SameColor(c, j * W + x); j--) n++;
        for (var j = y + 1; j < H && SameColor(c, j * W + x); j++) n++;
        return n >= 3;
    }

    /// <summary>
    /// Чи можна поміняти сусідні кахлі: обидві на місці й не зв'язані, і обмін або складає лінію, або в ньому глек,
    /// або це дві спецкахлі (їхня пара спрацьовує сама).
    /// </summary>
    public bool CanSwap(int a, int b)
    {
        if (!Valid(a) || !Valid(b) || !Movable(a) || !Movable(b)) return false;
        var (ax, ay, bx, by) = (a % W, a / W, b % W, b / W);
        if (Math.Abs(ax - bx) + Math.Abs(ay - by) != 1) return false;
        if (_kind[a] == KakhliKind.Glek || _kind[b] == KakhliKind.Glek) return true;
        if (_kind[a] != KakhliKind.Plain && _kind[b] != KakhliKind.Plain) return true;
        Exchange(a, b);
        var ok = LineAt(a) || LineAt(b);
        Exchange(a, b);
        return ok;
    }

    /// <summary>Перший можливий хід (зліва направо, згори вниз); null — ходів нема.</summary>
    public (int A, int B)? FindMove()
    {
        for (var c = 0; c < Cells; c++)
        {
            if (!Movable(c)) continue;
            if (c % W < W - 1 && CanSwap(c, c + 1)) return (c, c + 1);
            if (c / W < H - 1 && CanSwap(c, c + W)) return (c, c + W);
        }
        return null;
    }

    /// <summary>Усі можливі ходи (для бота й тестів).</summary>
    public List<(int A, int B)> Moves()
    {
        var list = new List<(int, int)>();
        for (var c = 0; c < Cells; c++)
        {
            if (!Movable(c)) continue;
            if (c % W < W - 1 && CanSwap(c, c + 1)) list.Add((c, c + 1));
            if (c / W < H - 1 && CanSwap(c, c + W)) list.Add((c, c + W));
        }
        return list;
    }

    /// <summary>
    /// Хід: поміняти <paramref name="a"/> з <paramref name="b"/> і розв'язати все до кінця. null — так не можна (поле
    /// не змінилось). Кроки — для анімації; очки вже додано до <see cref="Score"/>.
    /// </summary>
    public List<KakhliStep>? Swap(int a, int b)
    {
        if (!CanSwap(a, b)) return null;
        Exchange(a, b);
        var st = new KakhliStep();
        List<int> start;
        List<Make> make;
        if (_kind[a] == KakhliKind.Glek || _kind[b] == KakhliKind.Glek)
        {
            start = GlekCombo(a, b, st);
            make = [];
        }
        else if (_kind[a] != KakhliKind.Plain && _kind[b] != KakhliKind.Plain)
        {
            start = Combo(a, b, st);
            make = [];
        }
        else (start, make) = FromGroups(Groups(), [b, a], 1, st);
        var steps = new List<KakhliStep>();
        Resolve(st, start, make, steps);
        if (FindMove() is null) steps.Add(Shuffle());
        return steps;
    }

    /// <summary>Глек у обміні: з глеком — чисте поле; зі спецкахлею — уся та барва стає такими й спрацьовує; зі звичайною — зникає її барва.</summary>
    List<int> GlekCombo(int a, int b, KakhliStep st)
    {
        var g = _kind[a] == KakhliKind.Glek ? a : b;
        var o = g == a ? b : a;
        var start = new List<int>();
        if (_kind[o] == KakhliKind.Glek)
        {
            _kind[a] = _kind[b] = KakhliKind.Plain;
            for (var c = 0; c < Cells; c++) if (!_hole[c] && _id[c] >= 0) start.Add(c);
            st.Fx.Add([KakhliFx.Glek, b, -1]);
            return start;
        }
        var color = _color[o];
        var turn = _kind[o];
        _kind[g] = KakhliKind.Plain;   // глек віддав себе — сам більше не спрацює
        start.Add(g);
        st.Fx.Add([KakhliFx.Glek, g, color]);
        for (var c = 0; c < Cells; c++)
        {
            if (_hole[c] || _id[c] < 0 || _color[c] != color) continue;
            if (turn != KakhliKind.Plain && _kind[c] == KakhliKind.Plain && !_rope[c])
            {
                _kind[c] = KakhliKind.Stripe(turn) ? (Rng.Next(2) == 0 ? KakhliKind.Row : KakhliKind.Col) : turn;
                st.Made.Add([_id[c], c, _color[c], _kind[c]]);
            }
            start.Add(c);
        }
        return start;
    }

    /// <summary>Пара спецкахлів без глека: обидві віддають себе в одне спільне спрацювання на місці <paramref name="b"/>.</summary>
    List<int> Combo(int a, int b, KakhliStep st)
    {
        var (ka, kb) = (_kind[a], _kind[b]);
        _kind[a] = _kind[b] = KakhliKind.Plain;
        var start = new List<int> { a, b };
        var (bx, by) = (b % W, b / W);
        if (KakhliKind.Stripe(ka) && KakhliKind.Stripe(kb))
        {
            st.Fx.Add([KakhliFx.Cross, b, 0]);
            for (var x = 0; x < W; x++) start.Add(by * W + x);
            for (var y = 0; y < H; y++) start.Add(y * W + bx);
        }
        else if (ka == KakhliKind.Ember && kb == KakhliKind.Ember)
        {
            st.Fx.Add([KakhliFx.Ember, b, 2]);
            Square(b, 2, start);
        }
        else
        {
            st.Fx.Add([KakhliFx.Wide, b, 0]);
            for (var d = -1; d <= 1; d++)
            {
                if (by + d >= 0 && by + d < H) for (var x = 0; x < W; x++) start.Add((by + d) * W + x);
                if (bx + d >= 0 && bx + d < W) for (var y = 0; y < H; y++) start.Add(y * W + bx + d);
            }
        }
        return start;
    }

    void Square(int c, int r, List<int> into)
    {
        var (cx, cy) = (c % W, c / W);
        for (var y = Math.Max(0, cy - r); y <= Math.Min(H - 1, cy + r); y++)
            for (var x = Math.Max(0, cx - r); x <= Math.Min(W - 1, cx + r); x++)
                into.Add(y * W + x);
    }

    // ---------- збіги ----------

    /// <summary>Група збігу: лінії однієї барви, що торкаються. <see cref="Cross"/> — клітинка на перетині (кут Г чи Т).</summary>
    sealed class Group
    {
        public readonly List<int> Cells = [];
        public readonly List<(int[] Run, bool H)> Runs = [];
        public int Cross = -1;
        public bool HasH => Runs.Exists(r => r.H);
        public bool HasV => Runs.Exists(r => !r.H);
        public int Longest => Runs.Max(r => r.Run.Length);

        public byte Special()
        {
            if (Longest >= 5) return KakhliKind.Glek;
            if (HasH && HasV) return KakhliKind.Ember;
            if (Longest == 4) return Runs.First(r => r.Run.Length == 4).H ? KakhliKind.Row : KakhliKind.Col;
            return KakhliKind.Plain;
        }
    }

    readonly record struct Make(int Cell, int Color, byte Kind);

    List<Group> Groups()
    {
        var runs = new List<(int[] Run, bool H)>();
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W;)
            {
                var c = y * W + x;
                var n = 1;
                if (_id[c] >= 0 && _color[c] >= 0)
                    while (x + n < W && SameColor(c, c + n)) n++;
                if (n >= 3 && _id[c] >= 0 && _color[c] >= 0) runs.Add((Enumerable.Range(0, n).Select(i => c + i).ToArray(), true));
                x += n;
            }
        for (var x = 0; x < W; x++)
            for (var y = 0; y < H;)
            {
                var c = y * W + x;
                var n = 1;
                if (_id[c] >= 0 && _color[c] >= 0)
                    while (y + n < H && SameColor(c, c + n * W)) n++;
                if (n >= 3 && _id[c] >= 0 && _color[c] >= 0) runs.Add((Enumerable.Range(0, n).Select(i => c + i * W).ToArray(), false));
                y += n;
            }
        if (runs.Count == 0) return [];

        // лінії, що мають спільну клітинку, — одна група (Г, Т, хрест)
        var parent = Enumerable.Range(0, runs.Count).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var owner = new Dictionary<int, int>();
        var cross = new HashSet<int>();
        for (var i = 0; i < runs.Count; i++)
            foreach (var c in runs[i].Run)
            {
                if (owner.TryGetValue(c, out var j)) { parent[Root(i)] = Root(j); cross.Add(c); }
                else owner[c] = i;
            }
        var groups = new Dictionary<int, Group>();
        for (var i = 0; i < runs.Count; i++)
        {
            var root = Root(i);
            if (!groups.TryGetValue(root, out var g)) groups[root] = g = new Group();
            g.Runs.Add(runs[i]);
            foreach (var c in runs[i].Run)
                if (!g.Cells.Contains(c)) g.Cells.Add(c);
        }
        foreach (var g in groups.Values)
            g.Cross = g.Cells.FirstOrDefault(cross.Contains, -1);
        return [.. groups.Values];
    }

    /// <summary>
    /// Що зняти й що поставити за знайденими групами. Спецкахля стає туди, куди посунули кахлю (<paramref name="prefer"/>),
    /// у каскаді — на перетин Г/Т чи в середину лінії; на зв'язану кахлю не стає. Очки групи — одразу над полем.
    /// </summary>
    (List<int> Start, List<Make> Make) FromGroups(List<Group> groups, int[] prefer, int chain, KakhliStep st)
    {
        var start = new List<int>();
        var make = new List<Make>();
        foreach (var g in groups)
        {
            start.AddRange(g.Cells);
            var kind = g.Special();
            var spot = Spot(g, prefer);
            var bonus = 0;
            if (kind != KakhliKind.Plain && spot >= 0)
            {
                make.Add(new Make(spot, kind == KakhliKind.Glek ? -1 : _color[g.Cells[0]], kind));
                bonus = kind switch { KakhliKind.Glek => 60, KakhliKind.Ember => 40, _ => 20 } * chain;
                Score += bonus;
                st.Points += bonus;
            }
            var show = spot >= 0 ? spot : g.Cells[g.Cells.Count / 2];
            st.Pts.Add([show, g.Cells.Count * TilePoints * chain + bonus]);
        }
        return (start, make);
    }

    int Spot(Group g, int[] prefer)
    {
        foreach (var p in prefer)
            if (g.Cells.Contains(p) && !_rope[p]) return p;
        if (g.Cross >= 0 && !_rope[g.Cross]) return g.Cross;
        var run = g.Runs.OrderByDescending(r => r.Run.Length).First().Run;
        for (var i = 0; i < run.Length; i++)
        {
            // від середини лінії в обидва боки
            var k = run.Length / 2 + (i % 2 == 0 ? i / 2 : -(i / 2 + 1));
            if (k >= 0 && k < run.Length && !_rope[run[k]]) return run[k];
        }
        return -1;
    }

    // ---------- зняти, обсипати, досипати ----------

    void Resolve(KakhliStep st, List<int> start, List<Make> make, List<KakhliStep> steps)
    {
        var chain = 1;
        while (true)
        {
            st.Chain = chain;
            var before = st.Pts.Sum(p => p[1]);
            var pts = Clear(st, start, chain);
            foreach (var m in make)
            {
                if (_hole[m.Cell] || _id[m.Cell] >= 0) continue;
                Put(m.Cell, m.Color, m.Kind);
                st.Made.Add([_id[m.Cell], m.Cell, m.Color, m.Kind]);
                switch (m.Kind)
                {
                    case KakhliKind.Glek: Gleks++; break;
                    case KakhliKind.Ember: Embers++; break;
                    default: Stripes++; break;
                }
            }
            Score += pts;
            st.Points += pts;
            // те, що зняли вибухи понад групи, — окремим числом над першим спалахом
            var extra = st.Points - before;
            if (extra > 0 && st.Fx.Count > 0) st.Pts.Add([st.Fx[0][1], extra]);
            Fall(st);
            steps.Add(st);
            MaxChain = Math.Max(MaxChain, chain);
            var groups = Groups();
            if (groups.Count == 0 || steps.Count >= MaxSteps) break;
            chain++;
            st = new KakhliStep();
            (start, make) = FromGroups(groups, [], chain, st);
        }
    }

    /// <summary>
    /// Зняти клітинки разом з усім, що зачеплені спецкахлі вибухом допишуть у чергу. Мотузка бере удар на себе
    /// (розв'язується, кахля лишається); глина під кахлею тріскає на шар. Повертає очки за зняте.
    /// </summary>
    int Clear(KakhliStep st, List<int> start, int chain)
    {
        var queue = new Queue<int>(start);
        var hit = new HashSet<int>();
        var pts = 0;
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (!Valid(c) || _hole[c] || !hit.Add(c) || _id[c] < 0) continue;
            if (_rope[c])
            {
                _rope[c] = false;
                st.Rope.Add(c);
                pts += RopePoints;
                continue;
            }
            if (_kind[c] != KakhliKind.Plain) Activate(c, queue, st);
            if (_clay[c] > 0)
            {
                _clay[c]--;
                st.Clay.Add([c, _clay[c]]);
                pts += ClayPoints;
            }
            if (_color[c] >= 0) Collected[_color[c]]++;
            st.Gone.Add(_id[c]);
            _id[c] = -1;
            _color[c] = -1;
            _kind[c] = KakhliKind.Plain;
            pts += TilePoints * chain;
        }
        return pts;
    }

    void Activate(int c, Queue<int> queue, KakhliStep st)
    {
        var (cx, cy) = (c % W, c / W);
        switch (_kind[c])
        {
            case KakhliKind.Row:
                st.Fx.Add([KakhliFx.Row, c, 0]);
                for (var x = 0; x < W; x++) queue.Enqueue(cy * W + x);
                break;
            case KakhliKind.Col:
                st.Fx.Add([KakhliFx.Col, c, 0]);
                for (var y = 0; y < H; y++) queue.Enqueue(y * W + cx);
                break;
            case KakhliKind.Ember:
                st.Fx.Add([KakhliFx.Ember, c, 1]);
                var around = new List<int>();
                Square(c, 1, around);
                foreach (var a in around) queue.Enqueue(a);
                break;
            case KakhliKind.Glek:
                // глек, якого зачепило вибухом, забирає барву, якої на полі найбільше
                var color = Commonest();
                st.Fx.Add([KakhliFx.Glek, c, color]);
                if (color < 0) break;
                for (var i = 0; i < Cells; i++) if (_id[i] >= 0 && _color[i] == color) queue.Enqueue(i);
                break;
        }
    }

    int Commonest()
    {
        Span<int> n = stackalloc int[MaxColors];
        for (var c = 0; c < Cells; c++) if (_id[c] >= 0 && _color[c] >= 0) n[_color[c]]++;
        var best = -1;
        for (var k = 0; k < MaxColors; k++) if (n[k] > 0 && (best < 0 || n[k] > n[best])) best = k;
        return best;
    }

    /// <summary>Кахлі падають униз повз дірки (мотузка їде з кахлею, глина лишається на клітинці); згори досипаються нові.</summary>
    void Fall(KakhliStep st)
    {
        for (var x = 0; x < W; x++)
        {
            var write = H - 1;
            for (var y = H - 1; y >= 0; y--)
            {
                var c = y * W + x;
                if (_hole[c] || _id[c] < 0) continue;
                while (write >= 0 && _hole[write * W + x]) write--;
                var to = write * W + x;
                if (to != c)
                {
                    _id[to] = _id[c];
                    _color[to] = _color[c];
                    _kind[to] = _kind[c];
                    _rope[to] = _rope[c];
                    _id[c] = -1;
                    _color[c] = -1;
                    _kind[c] = KakhliKind.Plain;
                    _rope[c] = false;
                }
                write--;
            }
            for (var y = 0; y <= write; y++)
            {
                var c = y * W + x;
                if (_hole[c] || _id[c] >= 0) continue;
                Put(c, Rng.Next(Colors), KakhliKind.Plain);
                st.Add.Add([_id[c], _color[c], _kind[c]]);
            }
        }
        st.Board = (int[])_id.Clone();
    }

    /// <summary>Ходів нема — Глек перемішує те, що лежить (без готових збігів і з ходом); не вийшло — перефарбовує.</summary>
    public KakhliStep Shuffle()
    {
        var st = new KakhliStep { Shuffle = true };
        var cells = Enumerable.Range(0, Cells).Where(Movable).ToArray();
        var ok = false;
        for (var attempt = 0; attempt < 200 && !ok; attempt++)
        {
            for (var i = cells.Length - 1; i > 0; i--)
            {
                var j = Rng.Next(i + 1);
                Exchange(cells[i], cells[j]);
            }
            ok = Groups().Count == 0 && FindMove() is not null;
        }
        var recolored = false;
        for (var attempt = 0; attempt < 50 && !ok; attempt++)
        {
            recolored = true;
            foreach (var c in cells)
            {
                if (_kind[c] != KakhliKind.Plain) continue;
                _color[c] = -1;
                _color[c] = (sbyte)FreshColor(c);
            }
            ok = Groups().Count == 0 && FindMove() is not null;
        }
        // id ті самі — браузер просто пересуне кахлі; барви шлемо, лише коли їх перефарбовано
        if (recolored) foreach (var c in cells) st.Made.Add([_id[c], c, _color[c], _kind[c]]);
        st.Board = (int[])_id.Clone();
        return st;
    }

    /// <summary>
    /// «Глек доливає» в кінці: <paramref name="convert"/> випадкових звичайних кахлів стають рушниками, і все спецове на
    /// полі спрацьовує, поки є що (з каскадами). Очки — як за звичайні ходи.
    /// </summary>
    public List<KakhliStep> Finale(int convert)
    {
        var steps = new List<KakhliStep>();
        for (var round = 0; round < 6; round++)
        {
            var st = new KakhliStep();
            if (round == 0)
                for (var i = 0; i < convert; i++)
                {
                    var plain = Enumerable.Range(0, Cells).Where(c => Movable(c) && _kind[c] == KakhliKind.Plain).ToArray();
                    if (plain.Length == 0) break;
                    var c = plain[Rng.Next(plain.Length)];
                    _kind[c] = Rng.Next(2) == 0 ? KakhliKind.Row : KakhliKind.Col;
                    st.Made.Add([_id[c], c, _color[c], _kind[c]]);
                }
            var start = Enumerable.Range(0, Cells).Where(c => !_hole[c] && _id[c] >= 0 && _kind[c] != KakhliKind.Plain).ToList();
            if (start.Count == 0) break;
            Resolve(st, start, [], steps);
        }
        return steps;
    }

    // ---------- дріт і збереження ----------

    string Mask(Func<int, char> f)
    {
        var sb = new StringBuilder(Cells);
        for (var c = 0; c < Cells; c++) sb.Append(f(c));
        return sb.ToString();
    }

    /// <summary>Поле для браузера: id, барви й види по клітинках, дірки, глина, мотузки.</summary>
    public object Wire() => new
    {
        w = W,
        h = H,
        ids = (int[])_id.Clone(),
        col = _color.Select(x => (int)x).ToArray(),
        kind = _kind.Select(x => (int)x).ToArray(),
        holes = Mask(c => _hole[c] ? '#' : '.'),
        clay = Mask(c => (char)('0' + _clay[c])),
        rope = Mask(c => _rope[c] ? '1' : '0'),
    };

    public KakhliBoardState Save() => new(W, H, Colors, (int[])_id.Clone(), [.. _color.Select(x => (int)x)], [.. _kind.Select(x => (int)x)],
        Mask(c => _hole[c] ? '#' : '.'), Mask(c => (char)('0' + _clay[c])), Mask(c => _rope[c] ? '1' : '0'),
        Rng.State, _nextId, Score, (int[])Collected.Clone(), Stripes, Embers, Gleks, MaxChain);

    /// <summary>Відновити поле зі збереження; null — збереження не того розміру чи зіпсоване.</summary>
    public static KakhliBoard? Load(KakhliBoardState? s)
    {
        if (s is null || s.W <= 0 || s.H <= 0 || s.W * s.H > 400) return null;
        var n = s.W * s.H;
        if (s.Id?.Length != n || s.Color?.Length != n || s.Kind?.Length != n
            || s.Holes?.Length != n || s.Clay?.Length != n || s.Rope?.Length != n) return null;
        var b = new KakhliBoard(s.W, s.H, s.Colors, s.Rng);
        for (var c = 0; c < n; c++)
        {
            b._id[c] = s.Id[c];
            b._color[c] = (sbyte)Math.Clamp(s.Color[c], -1, MaxColors - 1);
            b._kind[c] = (byte)Math.Clamp(s.Kind[c], 0, KakhliKind.Glek);
            b._hole[c] = s.Holes[c] == '#';
            b._clay[c] = (byte)Math.Clamp(s.Clay[c] - '0', 0, 2);
            b._rope[c] = s.Rope[c] == '1';
        }
        b._nextId = Math.Max(s.NextId, s.Id.DefaultIfEmpty(0).Max() + 1);
        b.Score = Math.Max(0, s.Score);
        for (var k = 0; k < MaxColors && k < (s.Collected?.Length ?? 0); k++) b.Collected[k] = Math.Max(0, s.Collected![k]);
        b.Stripes = s.Stripes;
        b.Embers = s.Embers;
        b.Gleks = s.Gleks;
        b.MaxChain = s.MaxChain;
        return b;
    }

    /// <summary>
    /// Глибока копія. <paramref name="reseed"/> — інший мішечок: бот, що приміряє хід, не має знати, що насиплеться
    /// насправді (тоді він вибирав би каскади, яких людина не бачить).
    /// </summary>
    public KakhliBoard Clone(ulong? reseed = null)
    {
        var s = Save();
        return Load(reseed is { } r ? s with { Rng = r } : s)!;
    }

    /// <summary>На полі нема готових збігів (так має бути між ходами).</summary>
    public bool Settled => Groups().Count == 0;

    /// <summary>
    /// Поле з тексту — для тестів і налагодження: рядки клітинок через пробіл. <c>0</c>–<c>5</c> — барва; суфікс <c>h</c>/<c>v</c>
    /// — рушник рядка/стовпчика, <c>e</c> — жаринка; <c>g</c> — глек; <c>#</c> — дірка; <c>.</c> — порожньо; <c>~</c> — шар
    /// глини (до двох), <c>@</c> — мотузка.
    /// </summary>
    public static KakhliBoard Lay(string text, int colors = MaxColors, ulong seed = 1)
    {
        var rows = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var b = new KakhliBoard(rows[0].Length, rows.Length, colors, seed);
        for (var y = 0; y < b.H; y++)
            for (var x = 0; x < b.W; x++)
            {
                var c = y * b.W + x;
                var t = rows[y][x];
                if (t == "#") { b._hole[c] = true; continue; }
                b._clay[c] = (byte)Math.Min(2, t.Count(ch => ch == '~'));
                b._rope[c] = t.Contains('@');
                var core = t.TrimEnd('~', '@');
                if (core == ".") continue;
                if (core == "g") { b.Put(c, -1, KakhliKind.Glek); continue; }
                var kind = core.Length < 2 ? KakhliKind.Plain : core[1] switch
                {
                    'h' => KakhliKind.Row,
                    'v' => KakhliKind.Col,
                    'e' => KakhliKind.Ember,
                    _ => KakhliKind.Plain,
                };
                b.Put(c, core[0] - '0', kind);
            }
        return b;
    }
}
