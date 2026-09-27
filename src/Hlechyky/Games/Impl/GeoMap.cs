using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>
/// Мапа «Де це?»: цілочисельна сітка <see cref="W"/>×<see cref="H"/> (1 одиниця ≈ 0,335 км) у сферичній
/// рівновеликій конічній проєкції Альберса — форма України як на шкільній мапі, без розтягнутого півдня.
/// <para>
/// Проєкція живе лише тут, на сервері. Клієнт працює в одиницях сітки: мапа (<c>web/games/geo-map.json</c>)
/// уже спроєктована збірником <c>data/geo/build-map.py</c> тими самими числами, шпильку браузер шле в
/// одиницях, правду й чужі шпильки отримує в одиницях. Тож жодної спільної C#/JS-математики — і жодної
/// розбіжності між мовами.
/// </para>
/// </summary>
public static class GeoMap
{
    public const int W = 4000;
    public const int H = 2730;

    // Параметри проєкції (docs/games/specs/geo.md §5.1). Міняти лише разом зі збірником мапи.
    const double Phi1 = 46 * Math.PI / 180, Phi2 = 51 * Math.PI / 180, Phi0 = 48.5 * Math.PI / 180, Lam0 = 31.5 * Math.PI / 180;
    const double X0 = -0.110, Y0 = 0.070, K = 19000;
    static readonly double N = (Math.Sin(Phi1) + Math.Sin(Phi2)) / 2;
    static readonly double C = Math.Cos(Phi1) * Math.Cos(Phi1) + 2 * N * Math.Sin(Phi1);
    static readonly double Rho0 = Math.Sqrt(C - 2 * N * Math.Sin(Phi0)) / N;

    /// <summary>Широта й довгота (градуси) → точні (дробові) координати сітки.</summary>
    public static (double X, double Y) ProjectExact(double lat, double lon)
    {
        var rho = Math.Sqrt(C - 2 * N * Math.Sin(lat * Math.PI / 180)) / N;
        var th = N * (lon * Math.PI / 180 - Lam0);
        var x = rho * Math.Sin(th);
        var y = Rho0 - rho * Math.Cos(th);
        return ((x - X0) * K, (Y0 - y) * K);
    }

    /// <summary>Широта й довгота → ціла клітинка сітки (так правду бачить клієнт).</summary>
    public static (int X, int Y) Project(double lat, double lon)
    {
        var (x, y) = ProjectExact(lat, lon);
        return ((int)Math.Round(x, MidpointRounding.AwayFromZero), (int)Math.Round(y, MidpointRounding.AwayFromZero));
    }

    /// <summary>Координати сітки → широта й довгота (градуси). Зворотна до <see cref="ProjectExact"/>.</summary>
    public static (double Lat, double Lon) Unproject(double gx, double gy)
    {
        var x = gx / K + X0;
        var y = Y0 - gy / K;
        var dy = Rho0 - y;
        var rho = Math.Sqrt(x * x + dy * dy);
        var th = Math.Atan2(x, dy);
        var s = (C - rho * rho * N * N) / (2 * N);
        var phi = Math.Asin(Math.Clamp(s, -1, 1));
        return (phi * 180 / Math.PI, (Lam0 + th / N) * 180 / Math.PI);
    }

    /// <summary>Чи точка всередині сітки (шпилька поза нею — «поза мапою»).</summary>
    public static bool Inside(int x, int y) => x >= 0 && x <= W && y >= 0 && y <= H;

    // ---------------------------------------------------------------------------------------
    // області (для тестів узгодженості банку й мапи та майбутньої підказки «область»)
    // ---------------------------------------------------------------------------------------

    static readonly Lazy<GeoMapFile?> Default = new(() => GeoMapFile.Load(Paths.Resolve("web/games/geo-map.json")));

    /// <summary>Мапа з <c>web/games/geo-map.json</c>; null — файла нема чи він битий.</summary>
    public static GeoMapFile? File => Default.Value;

    /// <summary>Область за точкою сітки (<c>UA-30</c>); null — море, закордон або мапи нема.</summary>
    public static string? RegionAt(int x, int y) => Default.Value?.RegionAt(x, y);
}

/// <summary>
/// Прочитаний <c>geo-map.json</c>: дуги, області з кільцями, річки, міста. Кільця збираються з дуг так само, як
/// у браузері (кінець дуги = початок наступної; від'ємний індекс — дуга навпаки).
/// </summary>
public sealed class GeoMapFile
{
    public sealed record Region(string Id, string Name, string Short, IReadOnlyList<int[]> Rings);

    public required int[][] Arcs { get; init; }
    public required IReadOnlyList<Region> Regions { get; init; }
    public required int Rivers { get; init; }
    public required int Cities { get; init; }
    /// <summary>Скільки кілець посилається на кожну дугу: 1 — контур країни, 2 — межа двох областей.</summary>
    public required int[] RefCount { get; init; }

    public static GeoMapFile? Load(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(path));
            var root = doc.RootElement;
            var arcs = root.GetProperty("arcs").EnumerateArray().Select(a => a.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray();
            var refs = new int[arcs.Length];
            var regions = new List<Region>();
            foreach (var r in root.GetProperty("regions").EnumerateArray())
            {
                var rings = new List<int[]>();
                foreach (var ring in r.GetProperty("rings").EnumerateArray())
                {
                    var ids = ring.EnumerateArray().Select(v => v.GetInt32()).ToArray();
                    foreach (var id in ids) refs[Math.Abs(id) - 1]++;
                    rings.Add(Build(arcs, ids));
                }
                regions.Add(new Region(r.GetProperty("id").GetString()!, r.GetProperty("name").GetString()!,
                    r.GetProperty("short").GetString()!, rings));
            }
            return new GeoMapFile
            {
                Arcs = arcs,
                Regions = regions,
                Rivers = root.TryGetProperty("rivers", out var rv) ? rv.GetArrayLength() : 0,
                Cities = root.TryGetProperty("cities", out var ct) ? ct.GetArrayLength() : 0,
                RefCount = refs,
            };
        }
        catch (Exception)
        {
            // битий файл — не причина валити сервер: RegionAt просто скаже «не знаю»
            return null;
        }
    }

    /// <summary>Кільце з дуг: плоский масив x, y, x, y… без повтору спільних вузлів.</summary>
    static int[] Build(int[][] arcs, int[] ids)
    {
        var pts = new List<int>();
        foreach (var id in ids)
        {
            var a = arcs[Math.Abs(id) - 1];
            var n = a.Length / 2;
            for (var k = 0; k < n; k++)
            {
                if (pts.Count > 0 && k == 0) continue;
                var i = id > 0 ? k : n - 1 - k;
                pts.Add(a[2 * i]);
                pts.Add(a[2 * i + 1]);
            }
        }
        return [.. pts];
    }

    /// <summary>Область за точкою: парний-непарний тест по кільцях кожної області (дірка Києва в Київській теж).</summary>
    public string? RegionAt(double x, double y)
    {
        foreach (var r in Regions)
        {
            var inside = false;
            foreach (var ring in r.Rings)
                if (InRing(ring, x, y)) inside = !inside;
            if (inside) return r.Id;
        }
        return null;
    }

    static bool InRing(int[] ring, double x, double y)
    {
        var c = false;
        var n = ring.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1], xj = ring[2 * j], yj = ring[2 * j + 1];
            if ((yi > y) != (yj > y) && x < xi + (y - yi) * (xj - xi) / (yj - yi)) c = !c;
        }
        return c;
    }

    /// <summary>Найближча область у межах <paramref name="radius"/> одиниць (для точок на самій межі); null — нема.</summary>
    public string? RegionNear(double x, double y, double radius)
    {
        if (RegionAt(x, y) is { } exact) return exact;
        for (var r = 1.0; r <= radius; r += 1)
            for (var a = 0; a < 16; a++)
            {
                var t = a * Math.PI / 8;
                if (RegionAt(x + r * Math.Cos(t), y + r * Math.Sin(t)) is { } near) return near;
            }
        return null;
    }
}
