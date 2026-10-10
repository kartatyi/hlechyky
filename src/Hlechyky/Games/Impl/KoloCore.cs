using System.Security.Cryptography;
using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Математика «Гончарного колеса» (docs/games/specs/kolo.md §2): розкладка сегментів, seed → сегмент, повернення.
/// Чиста статика без каркаса — те саме рахує браузер у «ⓘ» (перевірка чесності).
/// <para>
/// Коло — 125 сегментів: миска ×2 — 60, горщик ×3 — 40, макітра ×6 — 20, «Глек удався!» ×30 — 4, «Тріснув!» — 1.
/// Кожен множник повертає рівно 96 %: 2·60 = 3·40 = 6·20 = 30·4 = 120 = 0,96·125. Решта 0,8 % кола — «Тріснув!»:
/// там програють усі.
/// </para>
/// <para>
/// Чесно наперед: на початку ставок сервер бере seed (32 випадкові байти, 64 hex малими) і показує
/// <c>hash = sha256(UTF-8 рядка seed)</c>. Сегмент: <c>n</c> = ціле з перших 13 hex-символів seed (52 біти),
/// <c>seg = n mod 125</c>; множник — <see cref="Wheel"/>[seg]. Нерівномірність від mod — 2^52 mod 125 зайвих значень на
/// 2^52, тобто менше за 10^-13: на RTP не впливає.
/// </para>
/// </summary>
public static class KoloCore
{
    /// <summary>«Тріснув!» — сегмент, де програють усі.</summary>
    public const int Crack = 0;
    /// <summary>Множники, на які можна ставити, — у порядку кнопок.</summary>
    public static readonly int[] Picks = [2, 3, 6, 30];
    /// <summary>Скільки сегментів кожного множника на колі.</summary>
    public static readonly IReadOnlyDictionary<int, int> Counts = new Dictionary<int, int> { [2] = 60, [3] = 40, [6] = 20, [30] = 4, [Crack] = 1 };
    /// <summary>Найбільший множник: від нього рахується межа ставки (виграш має влізти в int).</summary>
    public const int Top = 30;
    /// <summary>Найбільше на один множник за раунд: ставка × 30 мусить влізти в int (гаманці й леджер — int).</summary>
    public const int MaxPerPick = int.MaxValue / Top;

    /// <summary>Коло за годинниковою від «пальця майстра»: множник кожного сегмента, 0 — «Тріснув!».</summary>
    public static readonly int[] Wheel = Build();
    public static int Size => Wheel.Length;

    /// <summary>
    /// Розкладка: «Тріснув!» — сегмент 0; далі горщики, макітри й глеки розкидані рівно (за ідеальними позиціями
    /// (j + ½)·64/c), а між ними — миски, крім чотирьох проміжків. Так жодні два сусіди не однакові, а глеки стоять
    /// по колу майже через чверть.
    /// </summary>
    static int[] Build()
    {
        var others = new List<(double Pos, int Pri, int X)>();
        var rare = new[] { (X: 30, C: Counts[30]), (X: 6, C: Counts[6]), (X: 3, C: Counts[3]) };
        var n = rare.Sum(r => r.C);   // 64
        for (var pri = 0; pri < rare.Length; pri++)
            for (var j = 0; j < rare[pri].C; j++)
                others.Add(((j + 0.5) * n / rare[pri].C, pri, rare[pri].X));
        others.Sort((a, b) => a.Pos != b.Pos ? a.Pos.CompareTo(b.Pos) : a.Pri.CompareTo(b.Pri));
        var gaps = n - Counts[2];   // 4 проміжки без миски
        var skip = new HashSet<int>(Enumerable.Range(0, gaps).Select(j => (int)((j + 0.5) * n / gaps)));
        var wheel = new List<int> { Crack };
        for (var i = 0; i < others.Count; i++)
        {
            wheel.Add(others[i].X);
            if (!skip.Contains(i)) wheel.Add(2);
        }
        return [.. wheel];
    }

    public static bool IsPick(int x) => Array.IndexOf(Picks, x) >= 0;

    /// <summary>Новий seed: 32 байти з <paramref name="rng"/> (тести) чи криптографічного генератора (прод).</summary>
    public static string NewSeed(Random? rng)
    {
        var bytes = new byte[32];
        if (rng is null) RandomNumberGenerator.Fill(bytes);
        else rng.NextBytes(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>sha256 від UTF-8 рядка seed, hex малими.</summary>
    public static string Hash(string seed) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    /// <summary>Сегмент із seed: перші 13 hex-символів (52 біти) mod 125.</summary>
    public static int Seg(string seed)
    {
        if (seed is null || seed.Length < 13) throw new ArgumentException("seed закороткий", nameof(seed));
        return (int)(Convert.ToInt64(seed[..13], 16) % Size);
    }

    /// <summary>Множник сегмента (0 — «Тріснув!»).</summary>
    public static int X(int seg) => Wheel[seg];

    /// <summary>Перший сегмент із таким множником (для тестів і «Rig»); -1 — нема.</summary>
    public static int FirstSeg(int x) => Array.IndexOf(Wheel, x);

    /// <summary>Повернення ставки на <paramref name="pick"/>, коли випав <paramref name="x"/>: ставка × множник або 0.</summary>
    public static long Return(int pick, long amount, int x) => pick == x && x != Crack ? amount * pick : 0;

    /// <summary>Повернення всіх ставок гравця за раунд (сума по множниках).</summary>
    public static long Return(IEnumerable<(int Pick, int Amount)> stakes, int x) => stakes.Sum(s => Return(s.Pick, s.Amount, x));

    /// <summary>Частка повернення ставки на множник (0,96 для кожного), точно з розкладки.</summary>
    public static double Rtp(int pick) => (double)pick * Counts[pick] / Size;

    /// <summary>Назва сегмента: «миска», «горщик», «макітра», «глек», «тріснув».</summary>
    public static string Name(int x) => x switch
    {
        2 => "миска",
        3 => "горщик",
        6 => "макітра",
        30 => "глек",
        _ => "тріснув",
    };

    /// <summary>Як Глек вигукує сегмент: «Миска ×2!», «Глек удався! ×30», «Тріснув!».</summary>
    public static string Shout(int x) => x switch
    {
        2 => "Миска ×2!",
        3 => "Горщик ×3!",
        6 => "Макітра ×6!",
        30 => "Глек удався! ×30!",
        _ => "Тріснув!",
    };
}
