using System.Security.Cryptography;
using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>Овоч чи фрукт з воза: код на дроті (одна літера), назва, приріст множника (соті, на першому ярусі) і вага.</summary>
public sealed record KavunyKind(char Code, string Id, string Name, int Inc, int Weight);

/// <summary>
/// Один овоч послідовності: <c>Id</c> — номер з 1, <c>Code</c> — вид (<c>'x'</c> — гнилий гарбуз), <c>Inc</c> — приріст у сотих
/// (0 у гнилого), <c>At</c> — мілісекунди від <c>t0</c> раунду, коли вилітає, <c>Wave</c> — номер кидка. Геометрія — частки поля:
/// <c>X0</c> → <c>X1</c> по горизонталі, <c>H</c> — висота верхівки дуги, <c>Spin</c> — оберти за політ.
/// </summary>
public sealed record KavunyFruit(int Id, char Code, int Inc, int At, int Wave, double X0, double X1, double H, double Spin)
{
    public bool Rotten => Code == KavunyCore.Rot;
}

/// <summary>
/// Уся послідовність раунду: овочі по черзі (останній — гнилий, якщо <c>Rotten</c>), <c>Potential</c> — множник того, хто
/// розрізав би все (соті), <c>EndAt</c> — коли раунд скінчиться сам (мс від t0): виліт гнилого або «віз порожній».
/// </summary>
public sealed record KavunySeq(IReadOnlyList<KavunyFruit> Fruits, bool Rotten, int Potential, int EndAt)
{
    /// <summary>Овочі одним рядком кодів («saagx») — для виду й перевірки.</summary>
    public string Codes => new([.. Fruits.Select(f => f.Code)]);
}

/// <summary>
/// Математика «Кавунів на ярмарку» (docs/games/specs/kavuny.md §2–§3): таблиця овочів, яруси, розклад кидків і
/// послідовність раунду з seed — чесно наперед, як у Лелеки. Усе в цілих числах («сотих»), щоб сервер і браузер («ⓘ»)
/// рахували однаково.
/// <para>
/// Для овоча №k беремо <c>h = sha256("{seed}:{k}")</c> (UTF-8, hex малими): <c>roll</c> = h[0..8], <c>kind</c> = h[8..16],
/// <c>edge</c> = h[16..24], геометрія — h[24..32]. P — множник «ідеального різника» перед овочем, F — множник ярусу.
/// Гнилий, якщо (k = 1 і edge·100 &lt; 3·2³²) або roll·(Q·P + F·S) ≥ P·Q·2³², інакше вид — за вагою з
/// ⌊kind·Q / 2³²⌋, приріст F·inc. Звідси E[P після овоча · вижив] = P: множник того, хто ріже все, — мартингал, і будь-яке
/// правило «коли забрати» повертає рівно <see cref="Rtp"/> % (перший овоч бере перевагу дому).
/// </para>
/// </summary>
public static class KavunyCore
{
    /// <summary>Повернення за ідеальної гри, %: перевага дому — 3 %, бере її перший овоч.</summary>
    public const int Rtp = 97;
    /// <summary>Сума ваг таблиці.</summary>
    public const int Q = 1000;
    public const char Rot = 'x';

    /// <summary>Таблиця на першому ярусі. Порядок — порядок ваг для вибору виду; сума ваг — <see cref="Q"/>.</summary>
    public static readonly IReadOnlyList<KavunyKind> Kinds =
    [
        new('s', "plum", "слива", 2, 230),
        new('b', "beet", "буряк", 3, 170),
        new('a', "apple", "яблуко", 4, 200),
        new('g', "pear", "груша", 5, 150),
        new('d', "melon", "диня", 8, 110),
        new('p', "pumpkin", "гарбуз", 15, 80),
        new('k', "kavun", "кавун", 25, 50),
        new('K', "glek", "Глеків кавун", 100, 10),
    ];

    /// <summary>Σ вага·приріст на першому ярусі (середній приріст = S/Q = 0,0685).</summary>
    public static readonly int S = Kinds.Sum(k => k.Weight * k.Inc);

    /// <summary>Межі ярусів (соті множника «ідеального різника»): ×3, ×10, ×30.</summary>
    public static readonly int[] TierFrom = [300, 1000, 3000];
    /// <summary>У скільки разів більші прирости на ярусі: ярмарок розгулюється.</summary>
    public static readonly int[] TierFactor = [1, 2, 5, 10];
    /// <summary>Скільки овочів у кидку на ярусі (на нульовому — 1, 1, 2, 1, 1, 2…).</summary>
    public static readonly int[] TierWave = [1, 2, 3, 4];

    // ---------- розклад (мс) ----------
    /// <summary>Від «Поїхали!» до першого кидка: Глек замахується.</summary>
    public const int LeadMs = 1_200;
    /// <summary>Кидки — раз на стільки.</summary>
    public const int WaveMs = 850;
    /// <summary>Овочі одного кидка — один за одним через стільки.</summary>
    public const int StaggerMs = 150;
    /// <summary>Політ овоча від низу поля до низу.</summary>
    public const int FlyMs = 2_400;
    /// <summary>Скільки ще після падіння сервер приймає розріз (запізнення мережі й пачки намірів).</summary>
    public const int LagMs = 700;
    /// <summary>Авторізання ріже овоч через стільки після вильоту — раніше, ніж вилетить наступний.</summary>
    public const int AutoCutMs = 100;
    /// <summary>Запобіжник: довше ніхто не кидає (віз порожній).</summary>
    public const int MaxFruits = 5_000;

    /// <summary>Стеля множника за замовчуванням (×100) і межі для конфігу (×2 … ×1000), соті.</summary>
    public const int DefaultCap = 10_000, MinCap = 200, MaxCap = 100_000;
    /// <summary>Найменший автозабір — ×1,01.</summary>
    public const int MinAuto = 101;

    const ulong Two32 = 1UL << 32;

    public static int Tier(int p) => p < TierFrom[0] ? 0 : p < TierFrom[1] ? 1 : p < TierFrom[2] ? 2 : 3;
    public static int Factor(int p) => TierFactor[Tier(p)];
    public static int WaveSize(int p, int wave) => Tier(p) == 0 ? (wave % 3 == 2 ? 2 : 1) : TierWave[Tier(p)];
    public static int CapOf(int cents) => Math.Clamp(cents, MinCap, MaxCap);

    public static KavunyKind? Kind(char code) => Kinds.FirstOrDefault(k => k.Code == code);

    /// <summary>
    /// Один крок (спільний для гри й симуляції): <paramref name="p"/> — множник «ідеального різника» перед овочем, далі
    /// три 32-бітні кидки. Повертає (гнилий?, індекс виду в <see cref="Kinds"/>, приріст у сотих).
    /// </summary>
    public static (bool Rot, int Kind, int Inc) Step(int p, uint roll, uint kind, uint edge, bool first)
    {
        var f = p < 300 ? 1 : p < 1000 ? 2 : p < 3000 ? 5 : 10;   // = Factor(p), без виклику: симуляція кличе це сотні мільйонів разів
        if (first && (ulong)edge * 100 < (100 - Rtp) * Two32) return (true, -1, 0);
        if ((ulong)roll * ((ulong)Q * (ulong)p + (ulong)(f * S)) >= (ulong)p * Q * Two32) return (true, -1, 0);
        var pick = (int)(((ulong)kind * Q) >> 32);
        var w = Weights;
        var i = 0;
        while (i < w.Length - 1 && pick >= w[i]) pick -= w[i++];
        return (false, i, f * Incs[i]);
    }

    static readonly int[] Weights = [.. Kinds.Select(k => k.Weight)];
    static readonly int[] Incs = [.. Kinds.Select(k => k.Inc)];

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

    /// <summary>
    /// Уся послідовність раунду з seed і стелі (соті): овочі з розкладом і геометрією, доки не вилетить гнилий або множник
    /// «ідеального різника» не сягне стелі (тоді віз порожній — гнилого нема).
    /// </summary>
    public static KavunySeq Generate(string seed, int cap)
    {
        cap = CapOf(cap);
        var list = new List<KavunyFruit>();
        int p = 100, wave = 0, inWave = 0, size = WaveSize(p, 0);
        var rotten = false;
        for (var k = 1; k <= MaxFruits; k++)
        {
            if (inWave == size)
            {
                wave++;
                inWave = 0;
                size = WaveSize(p, wave);
            }
            var h = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{k}"));
            var (rot, kind, inc) = Step(p, U32(h, 0), U32(h, 4), U32(h, 8), k == 1);
            var at = wave * WaveMs + inWave * StaggerMs;
            inWave++;
            var x0 = R3(0.12 + 0.76 * h[12] / 255.0);
            var x1 = R3(Math.Clamp(x0 + (h[13] / 255.0 - 0.5) * 0.36, 0.06, 0.94));
            var top = R3(0.58 + 0.30 * h[14] / 255.0);
            var spin = R3((h[15] / 255.0 - 0.5) * 2);
            list.Add(new KavunyFruit(k, rot ? Rot : Kinds[kind].Code, inc, at, wave, x0, x1, top, spin));
            if (rot)
            {
                rotten = true;
                break;
            }
            p += inc;
            if (p >= cap) break;
        }
        var last = list[^1];
        var end = rotten ? last.At : last.At + FlyMs + LagMs;
        return new KavunySeq(list, rotten, p, end);
    }

    static uint U32(byte[] h, int at) => (uint)(h[at] << 24 | h[at + 1] << 16 | h[at + 2] << 8 | h[at + 3]);
    static double R3(double x) => Math.Round(x, 3);

    /// <summary>Виграш: ставка × множник, донизу до цілого черепка.</summary>
    public static int Win(int amount, int cents) => (int)Math.Min(int.MaxValue, (long)amount * cents / 100);

    /// <summary>Соті → множник для дроту (2.37).</summary>
    public static double X(int cents) => cents / 100.0;

    /// <summary>Множник з дроту → соті, донизу. null — не число.</summary>
    public static int? Cents(double x) => double.IsFinite(x) ? (int)Math.Clamp(Math.Floor(x * 100 + 1e-6), 0, int.MaxValue) : null;

    /// <summary>Соті → «2,37».</summary>
    public static string Fmt(int cents) => $"{cents / 100},{cents % 100:00}";
}
