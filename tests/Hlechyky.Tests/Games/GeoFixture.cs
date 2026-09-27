using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Спільне для тестів «Де це?»: крихітний справжній JPEG, тимчасова тека кешу, банк і стіл під ключ.
/// Тека кешу видаляється в <see cref="Dispose"/> — диск на цій машині тісний.
/// </summary>
public sealed class GeoCache : IDisposable
{
    /// <summary>1×1 JPEG (PIL, якість 50): SOI, APP0, DQT×2, SOF0, DHT×4, SOS, дані, EOI — 632 байти.</summary>
    public const string JpegB64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdASFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwCSiiivGPWP/9k=";

    public static byte[] Jpeg => Convert.FromBase64String(JpegB64);

    /// <summary>Стартовий банк із гіта (19 місць).</summary>
    public static GeoBank Starter => GeoBank.Load(Paths.Resolve(GeoBank.FileName), null);

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "geo-test-" + Guid.NewGuid().ToString("N")[..10]);
    public GeoBank Bank { get; }
    public FakeClock Clock { get; } = new();
    public GeoPhotos Photos { get; private set; }

    /// <param name="bank">Банк; null — стартовий.</param>
    /// <param name="ready">Які місця вже «скачані» (перше фото); null — усі фото всіх місць.</param>
    public GeoCache(GeoBank? bank = null, Func<GeoPlace, bool>? ready = null)
    {
        Bank = bank ?? Starter;
        Directory.CreateDirectory(Dir);
        foreach (var p in Bank.Places)
        {
            if (ready is not null && !ready(p)) continue;
            var photos = ready is null ? p.Photos : p.Photos.Take(1);
            foreach (var ph in photos) File.WriteAllBytes(Path.Combine(Dir, GeoPhotos.FileNameFor(ph.Url)), Jpeg);
        }
        Photos = GeoPhotos.Offline(Bank, Dir, Clock);
    }

    /// <summary>Докласти фото місця на диск і перечитати теку (імітація завантажувача).</summary>
    public void Add(GeoPlace place)
    {
        foreach (var ph in place.Photos) File.WriteAllBytes(Path.Combine(Dir, GeoPhotos.FileNameFor(ph.Url)), Jpeg);
        Photos.Rescan();
    }

    public IServiceProvider Services => RoomHarness.WithService(Photos);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Місце банку з простими даними — для тестів фільтрів і банку.</summary>
    public static GeoPlace Place(string id, string cat = "city", int difficulty = 1, double lat = 49.0, double lon = 31.0, int photos = 1) =>
        new(id, "Місце " + id, "Черкаська область", cat, lat, lon, difficulty, "Q1",
            [.. Enumerable.Range(0, photos).Select(k => new GeoPhoto($"File:{id}-{k}.jpg",
                $"https://upload.wikimedia.org/wikipedia/commons/a/ab/{id}-{k}.jpg", "Автор", "CC BY-SA 4.0",
                "https://creativecommons.org/licenses/by-sa/4.0", $"https://commons.wikimedia.org/wiki/File:{id}-{k}.jpg"))]);

    public static GeoBank BankOf(params GeoPlace[] places) => new(places, []);
}
