using System.Security.Cryptography;
using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>Одна горошина кидка: шлях (біти «0»/«1» згори вниз, 1 — вправо), глечик (скільки разів управо), множник (десяті), виграш.</summary>
public sealed record PlinkoBall(string P, int S, int X, int Win);

/// <summary>
/// Математика «Горохової дошки» (плінко, docs/games/specs/plinko.md): таблиці множників, шлях горошини з seed, точне
/// повернення. Чиста статика без каркаса — те саме рахує браузер у «ⓘ».
/// <para>
/// Дошка на <c>n</c> рядів (8…16): горошина на кожному ряду відскакує вліво чи вправо з шансом ½ і падає в один із
/// <c>n + 1</c> глечиків — у глечик <c>k</c> з імовірністю C(n, k) / 2ⁿ. Множники — у десятих частках (×1,5 → 15), крок
/// 0,1 до ×10, далі 1, 5, 10 — тож виграш <c>ставка × множник</c> цілий для будь-якої ставки, кратної 10 (інші ставки
/// <see cref="PlinkoOptions.AllowedBets"/> не пускає). Кожна з 27 таблиць повертає 98 % ± 0,05 п.п. (на 8 рядах крок
/// таблиці грубший — 97,97 %); перевіряє тест точною формулою <see cref="Rtp"/>.
/// </para>
/// <para>
/// Чесно наперед: до кидка видно <c>hash = sha256(seed)</c> (UTF-8, hex малими), після — сам seed. Горошина №i
/// (з 1) кидка: <c>h = sha256("{seed}:{i}")</c>, ряд j (з 0) — біт j від старшого: <c>(h[j / 8] &gt;&gt; (7 − j % 8)) &amp; 1</c>,
/// 1 — вправо. Глечик — скільки одиниць. Шлях не залежить від ставки, рядів (лише скільки бітів узяти) і ризику.
/// </para>
/// </summary>
public static class PlinkoCore
{
    public const int MinRows = 8, MaxRows = 16;
    /// <summary>Повернення, на яке зроблено таблиці, %.</summary>
    public const int Rtp98 = 98;
    public const string Low = "low", Mid = "mid", High = "high";
    public static readonly string[] Risks = [Low, Mid, High];

    /// <summary>
    /// Множники в десятих частках, від лівого краю до правого: [ризик][ряди − 8][глечик]. Симетричні, від центру до
    /// краю не спадають. Низький — більшість горошин повертає ставку чи більше; високий — центр ×0,2, край ×30…×1000.
    /// </summary>
    static readonly int[][][] Tables =
    [
        [   // низький
            [50, 21, 12, 10, 4, 10, 12, 21, 50],
            [57, 25, 14, 10, 7, 7, 10, 14, 25, 57],
            [66, 27, 14, 11, 10, 5, 10, 11, 14, 27, 66],
            [70, 37, 19, 12, 10, 7, 7, 10, 12, 19, 37, 70],
            [87, 36, 19, 14, 11, 10, 4, 10, 11, 14, 19, 36, 87],
            [99, 47, 27, 16, 11, 10, 7, 7, 10, 11, 16, 27, 47, 99],
            [110, 48, 25, 18, 12, 10, 10, 5, 10, 10, 12, 18, 25, 48, 110],
            [130, 62, 34, 19, 14, 11, 10, 7, 7, 10, 11, 14, 19, 34, 62, 130],
            [150, 65, 34, 20, 14, 12, 10, 10, 5, 10, 10, 12, 14, 20, 34, 65, 150],
        ],
        [   // середній
            [120, 36, 12, 6, 5, 6, 12, 36, 120],
            [160, 51, 21, 9, 3, 3, 9, 21, 51, 160],
            [200, 66, 26, 13, 5, 3, 5, 13, 26, 66, 200],
            [270, 97, 40, 21, 5, 3, 3, 5, 21, 40, 97, 270],
            [350, 100, 47, 21, 11, 5, 3, 5, 11, 21, 47, 100, 350],
            [450, 170, 66, 30, 17, 5, 3, 3, 5, 17, 30, 66, 170, 450],
            [590, 220, 84, 37, 17, 10, 5, 2, 5, 10, 17, 37, 84, 220, 590],
            [770, 290, 120, 52, 24, 14, 5, 3, 3, 5, 14, 24, 52, 120, 290, 770],
            [1000, 380, 130, 64, 29, 14, 9, 5, 3, 5, 9, 14, 29, 64, 130, 380, 1000],
        ],
        [   // високий
            [300, 51, 13, 2, 2, 2, 13, 51, 300],
            [470, 88, 23, 2, 2, 2, 2, 23, 88, 470],
            [720, 160, 29, 6, 2, 2, 2, 6, 29, 160, 720],
            [1100, 230, 48, 14, 3, 1, 1, 3, 14, 48, 230, 1100],
            [1750, 350, 81, 24, 2, 2, 2, 2, 2, 24, 81, 350, 1750],
            [2700, 570, 130, 41, 3, 2, 2, 2, 2, 3, 41, 130, 570, 2700],
            [4200, 870, 190, 49, 15, 3, 2, 1, 2, 3, 15, 49, 190, 870, 4200],
            [6500, 1350, 310, 81, 26, 2, 2, 2, 2, 2, 2, 26, 81, 310, 1350, 6500],
            [10000, 2100, 480, 120, 29, 11, 2, 2, 2, 2, 2, 11, 29, 120, 480, 2100, 10000],
        ],
    ];

    /// <summary>Найбільший множник на всій дошці (десяті) — від нього межа ставки: виграш має влізти в int.</summary>
    public static readonly int TopX = Tables.SelectMany(r => r).SelectMany(t => t).Max();

    public static bool IsRisk(string? risk) => risk is Low or Mid or High;
    public static bool IsRows(int rows) => rows is >= MinRows and <= MaxRows;

    /// <summary>Таблиця множників (десяті), від лівого краю. Ряди чи ризик не ті — виняток.</summary>
    public static IReadOnlyList<int> Table(int rows, string risk)
    {
        var r = Array.IndexOf(Risks, risk);
        if (r < 0 || !IsRows(rows)) throw new ArgumentOutOfRangeException(nameof(rows), $"нема таблиці {risk}/{rows}");
        return Tables[r][rows - MinRows];
    }

    /// <summary>Усі таблиці для виду: { low: [[…8 рядів], …, […16]], mid: …, high: … }.</summary>
    public static IReadOnlyDictionary<string, int[][]> All() =>
        Risks.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => Tables[x.i]);

    /// <summary>Точне повернення таблиці: Σ C(n, k)·множник / 2ⁿ (частка, 0,98 = 98 %).</summary>
    public static double Rtp(int rows, string risk)
    {
        var t = Table(rows, risk);
        double sum = 0;
        for (var k = 0; k <= rows; k++) sum += Choose(rows, k) * t[k];
        return sum / 10.0 / (1L << rows);
    }

    /// <summary>Частка горошин, що повертають ставку чи більше (множник ≥ 1).</summary>
    public static double HitRate(int rows, string risk)
    {
        var t = Table(rows, risk);
        double sum = 0;
        for (var k = 0; k <= rows; k++) if (t[k] >= 10) sum += Choose(rows, k);
        return sum / (1L << rows);
    }

    public static long Choose(int n, int k)
    {
        long c = 1;
        for (var i = 1; i <= k; i++) c = c * (n - k + i) / i;
        return c;
    }

    /// <summary>Новий seed: 32 байти з <paramref name="rng"/> (тести) чи криптографічного генератора (прод), hex малими.</summary>
    public static string NewSeed(Random? rng)
    {
        var bytes = new byte[32];
        if (rng is null) RandomNumberGenerator.Fill(bytes);
        else rng.NextBytes(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>sha256 від UTF-8 рядка, hex малими.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Шлях горошини №<paramref name="i"/> (з 1): <paramref name="rows"/> бітів «0»/«1» згори вниз, 1 — вправо.</summary>
    public static string Path(string seed, int i, int rows)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{i}"));
        var p = new char[rows];
        for (var j = 0; j < rows; j++) p[j] = ((h[j >> 3] >> (7 - (j & 7))) & 1) == 1 ? '1' : '0';
        return new string(p);
    }

    /// <summary>Глечик шляху — скільки разів управо (0 — лівий край, n — правий).</summary>
    public static int Slot(string path) => path.Count(c => c == '1');

    /// <summary>Виграш: ставка × множник (десяті), донизу до цілого черепка.</summary>
    public static int Win(int bet, int x) => (int)Math.Min(int.MaxValue, (long)bet * x / 10);

    /// <summary>Горошини кидка: <paramref name="count"/> штук із seed, кожна зі своїм шляхом, глечиком, множником і виграшем.</summary>
    public static IReadOnlyList<PlinkoBall> Drop(string seed, int count, int rows, string risk, int bet)
    {
        var t = Table(rows, risk);
        var balls = new List<PlinkoBall>(count);
        for (var i = 1; i <= count; i++)
        {
            var p = Path(seed, i, rows);
            var s = Slot(p);
            balls.Add(new PlinkoBall(p, s, t[s], Win(bet, t[s])));
        }
        return balls;
    }

    /// <summary>Десяті → «1,5», «0,2», «1000».</summary>
    public static string Fmt(int x) => x % 10 == 0 ? (x / 10).ToString() : $"{x / 10},{x % 10}";
}
