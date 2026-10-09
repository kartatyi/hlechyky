using System.Security.Cryptography;
using System.Text;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Математика Лелеки (docs/games/specs/lelka.md §2): множник польоту й точка падіння з seed. Усе в сотих («центах»), щоб
/// сервер і браузер («ⓘ» на столі) рахували однаково, без розбіжностей плаваючої коми.
/// <para>
/// seed — 64 hex-символи (32 випадкові байти). <c>hash = sha256(UTF-8 рядка seed)</c> у hex — його стіл показує на
/// початку прийому ставок. Точка падіння: <c>n = перші 13 hex-символів seed</c> (52 біти), <c>crash = ⌊96·2^52 / (2^52 − n)⌋</c>
/// сотих, менше за 100 — це 100 (×1,00), більше за 100000 — 100000 (×1000). Звідси <c>P(crash ≥ x) = 0,96 / x</c> для
/// x &gt; 1 (сітка 0,01) і рівно 4 % раундів на ×1,00.
/// </para>
/// </summary>
public static class LelkaCore
{
    /// <summary>Швидкість росту: m(t) = e^(K·t), t — секунди від старту польоту (×2 ≈ 9,2 с, ×10 ≈ 30,7 с, ×100 ≈ 61,4 с).</summary>
    public const double K = 0.075;
    /// <summary>Стеля: ×1000 (у сотих).</summary>
    public const int MaxCents = 100_000;
    /// <summary>Найменший автозабір — ×1,01 (на ×1,00 це було б «повернути ставку без ризику»).</summary>
    public const int MinAutoCents = 101;
    /// <summary>Перевага дому: 96 % — повертається гравцям за будь-якої стратегії.</summary>
    public const long Rtp = 96;

    const long Two52 = 1L << 52;

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

    /// <summary>Точка падіння в сотих (100 = ×1,00 … 100000 = ×1000).</summary>
    public static int CrashCents(string seed)
    {
        if (seed is null || seed.Length < 13) throw new ArgumentException("seed закороткий", nameof(seed));
        var n = Convert.ToInt64(seed[..13], 16);   // 0 … 2^52−1
        var cents = Rtp * Two52 / (Two52 - n);   // 96·2^52 < 2^59 — влазить у long
        return (int)Math.Clamp(cents, 100, MaxCents);
    }

    /// <summary>Множник у мить <paramref name="ms"/> від старту польоту, у сотих, донизу (100 на старті).</summary>
    public static int CentsAt(double ms)
    {
        if (ms <= 0) return 100;
        var m = Math.Exp(K * ms / 1000.0);
        return (int)Math.Min(int.MaxValue, Math.Floor(m * 100 + 1e-9));
    }

    /// <summary>Скільки мілісекунд летіти до множника <paramref name="cents"/>.</summary>
    public static double MsTo(int cents) => cents <= 100 ? 0 : Math.Log(cents / 100.0) / K * 1000.0;

    /// <summary>Виграш: ставка × множник, донизу до цілого черепка.</summary>
    public static int Win(int amount, int cents) => (int)Math.Min(int.MaxValue, (long)amount * cents / 100);

    /// <summary>Соті → множник для дроту (2.37).</summary>
    public static double X(int cents) => cents / 100.0;

    /// <summary>Множник з дроту (2.37, «2,37» не приймаємо) → соті, донизу. null — не число.</summary>
    public static int? Cents(double x) => double.IsFinite(x) ? (int)Math.Clamp(Math.Floor(x * 100 + 1e-6), 0, int.MaxValue) : null;
}
