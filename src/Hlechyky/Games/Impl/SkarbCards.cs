namespace Hlechyky.Games.Impl;

/// <summary>Вид карти «Скарбу Полуботка»: хід, зламати, полагодити, стара мапа, завал (контракт §1.2).</summary>
public enum SkKind { Path, Break, Fix, Map, Rockfall }

/// <summary>Форма ходу (чи мітки, чи колодязя): маска країв при r = 0 і чи з'єднані краї через середину.</summary>
public sealed record SkShape(string S, int M, bool C);

/// <summary>
/// Карта колоди зі сталим id 0–66 (§1.2). <see cref="Shape"/> — лише для ходу; <see cref="Tools"/> — маска інструментів
/// (кирка 1, лампа 2, тачка 4): для зламу — один, для ремонту — один чи два.
/// </summary>
public sealed record SkCard(int Id, SkKind K, SkShape? Shape, int Tools);

/// <summary>
/// Сталі «Скарбу Полуботка» без Ctx (контракт §1, §4): краї й маски, форми, колода з id, ролі, рука й злитки за
/// кількістю гравців. Клієнт тримає те саме в <c>web/games/skarb/consts.js</c>.
/// </summary>
public static class SkarbCards
{
    public const int N = 1, E = 2, S = 4, W = 8;
    /// <summary>Напрями за індексом 0..3: N E S W — біт <c>1 &lt;&lt; d</c>.</summary>
    public static readonly string[] Dirs = ["N", "E", "S", "W"];
    public static readonly int[] Dx = [0, 1, 0, -1];
    public static readonly int[] Dy = [-1, 0, 1, 0];
    public static int Opp(int d) => (d + 2) & 3;

    /// <summary>Поворот маски на 180° (§4.1).</summary>
    public static int Rot(int m) => ((m & 1) << 2) | ((m & 4) >> 2) | ((m & 2) << 2) | ((m & 8) >> 2);

    /// <summary>Маска з поворотом r (0 — як намальовано, 1 — на 180°).</summary>
    public static int Mask(SkShape sh, int r) => r == 1 ? Rot(sh.M) : sh.M;

    /// <summary>Симетрична форма: r = 1 дає ту саму маску, тож зводиться до 0.</summary>
    public static bool Symmetric(SkShape sh) => Rot(sh.M) == sh.M;

    public static int Edges(int m) => (m & 1) + ((m >> 1) & 1) + ((m >> 2) & 1) + ((m >> 3) & 1);

    // ---------- інструменти ----------

    public static readonly string[] Tools = ["pick", "lamp", "cart"];
    public static readonly string[] ToolName = ["кирка", "лампа", "тачка"];
    public static readonly string[] ToolAcc = ["кирку", "лампу", "тачку"];
    /// <summary>«Кирку зламано — спершу полагодь» — чому ходу не покласти (перший зламаний за TOOLS).</summary>
    public static readonly string[] BrokenWhy =
    [
        "Кирку зламано — спершу полагодь", "Лампу розбито — спершу полагодь", "Тачка без колеса — спершу полагодь",
    ];
    public static int ToolIdx(string? name) => name is null ? -1 : Array.IndexOf(Tools, name);

    // ---------- форми ----------

    public static readonly SkShape Start = new("start", 15, true);
    public static readonly SkShape Gold = new("gold", 15, true);
    public static readonly SkShape StoneA = new("stoneA", 9, true);
    public static readonly SkShape StoneB = new("stoneB", 12, true);

    static readonly SkShape[] PathShapes =
    [
        new("x", 15, true), new("tv", 7, true), new("th", 14, true), new("v", 5, true), new("h", 10, true),
        new("ce", 6, true), new("cw", 12, true),
        new("dx", 15, false), new("dtv", 7, false), new("dth", 14, false), new("dv", 5, false), new("dh", 10, false),
        new("dce", 6, false), new("dcw", 12, false), new("ds", 4, false), new("de", 2, false),
    ];

    public static SkShape ShapeOf(string s) => PathShapes.FirstOrDefault(p => p.S == s)
        ?? (s == "start" ? Start : s == "gold" ? Gold : s == "stoneA" ? StoneA : s == "stoneB" ? StoneB
            : throw new ArgumentException($"нема форми {s}"));

    // ---------- колода (§1.2) ----------

    public const int DeckSize = 67;

    /// <summary>Уся колода за id. Склад: (форма/дія, кількість) у порядку id.</summary>
    public static readonly SkCard[] Deck = BuildDeck();

    static SkCard[] BuildDeck()
    {
        var list = new List<SkCard>();
        void Path(string s, int n) { for (var i = 0; i < n; i++) list.Add(new SkCard(list.Count, SkKind.Path, ShapeOf(s), 0)); }
        void Act(SkKind k, int tools, int n) { for (var i = 0; i < n; i++) list.Add(new SkCard(list.Count, k, null, tools)); }
        Path("x", 5); Path("tv", 5); Path("th", 5); Path("v", 4); Path("h", 3); Path("ce", 4); Path("cw", 5);
        foreach (var s in new[] { "dx", "dtv", "dth", "dv", "dh", "dce", "dcw", "ds", "de" }) Path(s, 1);
        Act(SkKind.Break, 1, 3); Act(SkKind.Break, 2, 3); Act(SkKind.Break, 4, 3);
        Act(SkKind.Fix, 1, 2); Act(SkKind.Fix, 2, 2); Act(SkKind.Fix, 4, 2);
        Act(SkKind.Fix, 1 | 2, 1); Act(SkKind.Fix, 1 | 4, 1); Act(SkKind.Fix, 2 | 4, 1);
        Act(SkKind.Map, 0, 6);
        Act(SkKind.Rockfall, 0, 3);
        return [.. list];
    }

    /// <summary>Перший інструмент маски (0..2) або -1.</summary>
    public static int FirstTool(int tools) => (tools & 1) != 0 ? 0 : (tools & 2) != 0 ? 1 : (tools & 4) != 0 ? 2 : -1;

    // ---------- ролі, рука, злитки (§1.1, §2.6) ----------

    public const int MinSeats = 3, MaxSeats = 10, Rounds = 3;

    /// <summary>(кротів, копачів) у колоді ролей за кількістю гравців.</summary>
    public static (int Moles, int Diggers) Pack(int n) => n switch
    {
        <= 3 => (1, 3),
        4 => (1, 4),
        5 => (2, 4),
        6 => (2, 5),
        7 => (3, 5),
        8 => (3, 6),
        9 => (3, 7),
        _ => (4, 7),
    };

    /// <summary>Карт у руці на старті раунду.</summary>
    public static int Hand(int n) => n switch { <= 5 => 6, 6 or 7 => 5, _ => 4 };

    /// <summary>Злитків кожному кротові, коли кроти перемогли, за кількістю кротів у грі.</summary>
    public static int MoleGold(int moles) => moles switch { 1 => 4, 2 or 3 => 3, _ => 2 };

    /// <summary>Колода злитків до тасування: 16 × 1, 8 × 2, 4 × 3 (44 злитки).</summary>
    public static int[] GoldCards() => [.. Enumerable.Repeat(1, 16), .. Enumerable.Repeat(2, 8), .. Enumerable.Repeat(3, 4)];

    // ---------- поле (§2.3) ----------

    public const int X0 = -4, X1 = 12, Y0 = -4, Y1 = 4;
    public const int Width = X1 - X0 + 1, Height = Y1 - Y0 + 1, Cells = Width * Height;
    public const int GoalX = 8;
    public static readonly int[] GoalY = [-2, 0, 2];
    public static readonly string[] GoalPlace = ["верхня мітка", "середня мітка", "нижня мітка"];
    public static readonly string[] GoalPlaceAcc = ["верхню мітку", "середню мітку", "нижню мітку"];

    public static bool InBounds(int x, int y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
    public static int Cell(int x, int y) => (y - Y0) * Width + (x - X0);
    public static int CellX(int cell) => cell % Width + X0;
    public static int CellY(int cell) => cell / Width + Y0;
    public static bool IsShaft(int x, int y) => x == 0 && y is >= -4 and <= -1;
    public static int GoalAt(int x, int y) => x == GoalX ? Array.IndexOf(GoalY, y) : -1;

    /// <summary>Сусід клітинки в напрямі d або -1, якщо за межею поля.</summary>
    public static int Step(int cell, int d)
    {
        int x = CellX(cell) + Dx[d], y = CellY(cell) + Dy[d];
        return InBounds(x, y) ? Cell(x, y) : -1;
    }

    /// <summary>«1 злиток», «2 злитки», «5 злитків».</summary>
    public static string Bars(int n)
    {
        int a = Math.Abs(n) % 100, b = a % 10;
        var w = a is > 10 and < 20 ? "злитків" : b == 1 ? "злиток" : b is >= 2 and <= 4 ? "злитки" : "злитків";
        return $"{n} {w}";
    }
}
