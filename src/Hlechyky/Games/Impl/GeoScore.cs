using System.Globalization;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Очки «Де це?»: відстань гаверсинусом між шпилькою й правдою, далі плавна крива — 5000 за кілометр і ближче,
/// 2885 за сто кілометрів, 313 за п'ятсот, крихти за пів країни. Шкала неперервна й чесна однаково вдвох і
/// вдесятьох: очки за точність, а не за місце за столом.
/// </summary>
public static class GeoScore
{
    /// <summary>Середній радіус Землі (IUGG), км.</summary>
    public const double EarthKm = 6371.0088;
    /// <summary>Максимум за раунд.</summary>
    public const int Max = 5000;
    /// <summary>«В яблучко»: до кілометра — повні очки й значок 🎯. Це три одиниці сітки — точніше на екрані й не поставиш.</summary>
    public const double BullKm = 1;
    /// <summary>Швидкість спаду кривої: кожні 180 км понад перший очки падають у e разів.</summary>
    public const double FalloffKm = 180;

    /// <summary>Відстань великого кола між двома точками (градуси), км.</summary>
    public static double Km(double lat1, double lon1, double lat2, double lon2)
    {
        const double rad = Math.PI / 180;
        var p1 = lat1 * rad;
        var p2 = lat2 * rad;
        var dp = (lat2 - lat1) * rad;
        var dl = (lon2 - lon1) * rad;
        var s1 = Math.Sin(dp / 2);
        var s2 = Math.Sin(dl / 2);
        var a = s1 * s1 + Math.Cos(p1) * Math.Cos(p2) * s2 * s2;
        return 2 * EarthKm * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1 - a)));
    }

    /// <summary>Відстань від точки сітки (шпилька) до правди, км.</summary>
    public static double KmFromGrid(int x, int y, double lat, double lon)
    {
        var (plat, plon) = GeoMap.Unproject(x, y);
        return Km(plat, plon, lat, lon);
    }

    /// <summary>Очки раунду за відстань: ≤ 1 км — 5000, далі <c>5000·exp(−(d−1)/180)</c>, округлено.</summary>
    public static int Points(double km)
    {
        if (double.IsNaN(km)) return 0;
        if (km <= BullKm) return Max;
        return (int)Math.Round(Max * Math.Exp(-(km - BullKm) / FalloffKm), MidpointRounding.AwayFromZero);
    }
}

/// <summary>Тексти для гравця: кілометри й очки так, як їх пишуть люди, а не комп'ютери. Те саме — у geo.js.</summary>
public static class GeoText
{
    /// <summary>«менше кілометра», «7,3 км», «1 043 км».</summary>
    public static string Km(double km)
    {
        if (km < 1) return "менше кілометра";
        var tenth = Math.Round(km * 10, MidpointRounding.AwayFromZero) / 10;
        if (tenth < 10) return tenth.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " км";
        return Num((long)Math.Round(km, MidpointRounding.AwayFromZero)) + " км";
    }

    /// <summary>Ціле з пробілами між тисячами: 21 340.</summary>
    public static string Num(long n) => n.ToString("#,##0", CultureInfo.InvariantCulture).Replace(",", " ");
}
