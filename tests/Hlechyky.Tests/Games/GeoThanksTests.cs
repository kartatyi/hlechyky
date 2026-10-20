using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Де це?», пакет 4: 📷 Глек дякує автору фото, коли друг уперше вгадує його фото (ближче 50 км) — лише чуже
/// вгадування, лише перші три фото автора, повтор не платить.
/// </summary>
public sealed class GeoThanksTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "geo-thanks-" + Guid.NewGuid().ToString("N")[..10]);
    readonly HashSet<string> _refs = [];
    readonly List<(string Nick, int Amount, string Ref)> _paid = [];
    readonly List<string> _journal = [];

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static (int X, int Y) Kyiv => GeoMap.Project(50.4501, 30.5234);
    static (int X, int Y) Lviv => GeoMap.Project(49.8397, 24.0297);

    /// <summary>Сервіс із «леджером» у пам'яті: ref платить раз, як справжня економіка.</summary>
    GeoThanks Thanks() => new(null)
    {
        Run = a => { a(); return Task.CompletedTask; },
        Pay = (nick, amount, reason, refKey, text) =>
        {
            Assert.Equal(GeoThanks.Reason, reason);
            if (!_refs.Add(refKey)) return false;
            _paid.Add((nick, amount, refKey));
            return true;
        },
        Journal = _journal.Add,
    };

    GeoMine Mine() => new(null, _dir, null) { Encoder = (b, _) => Task.FromResult<byte[]?>(b) };

    /// <summary>JPEG 640×480 за заголовком (1×1 менший за <see cref="GeoMine.MinSide"/>).</summary>
    static byte[] Photo()
    {
        var jpeg = GeoCache.Jpeg;
        var sof = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        jpeg[sof + 5] = 0x01; jpeg[sof + 6] = 0xE0;
        jpeg[sof + 7] = 0x02; jpeg[sof + 8] = 0x80;
        return jpeg;
    }

    static async Task<GeoMinePhoto> Add(GeoMine m, string nick, string title = "Ратуша") =>
        (await m.AddAsync(nick, Photo(), Lviv.X, Lviv.Y, title, "Стояв під дощем", DateTimeOffset.UtcNow, CancellationToken.None)).Photo!;

    /// <summary>Стіл «лише наші», одна партія: Петро ставить шпильку в <paramref name="aim"/>.</summary>
    static void Round(GeoCache c, GeoMine m, GeoThanks t, (int X, int Y) aim, params string[] nicks)
    {
        var sp = new ServiceCollection().AddSingleton(c.Photos).AddSingleton(m).AddSingleton(t).BuildServiceProvider();
        var h = new RoomHarness("geo", new { friends = "only" }, 7, sp);
        foreach (var n in nicks) h.Join(n);
        Assert.True(h.Start().Ok, h.Reply.Message);
        GeoTests.Until(h, GeoMatch.PhaseGuess);
        for (var s = 0; s < nicks.Length; s++)
        {
            if (h.Act(s, "guess", new { x = aim.X, y = aim.Y }).Ok) h.Act(s, "ready");
        }
        GeoTests.Until(h, GeoMatch.PhaseReveal);
    }

    [Fact]
    public async Task First_close_guess_by_a_friend_pays_the_author_once()
    {
        using var c = new GeoCache();
        var m = Mine();
        var t = Thanks();
        var p = await Add(m, "Оля");
        Round(c, m, t, Kyiv, "Оля", "Петро");          // ~470 км — не вгадали
        Assert.Empty(_paid);
        Round(c, m, t, Lviv, "Оля", "Петро");
        Assert.Equal(("Оля", GeoThanks.Shards, "geo-thanks:" + p.Id), Assert.Single(_paid));
        Assert.Equal($"📷 Глек дякує · Оля: фото «Ратуша» вгадали — +{GeoThanks.Shards} 🏺", Assert.Single(_journal));
        Round(c, m, t, Lviv, "Оля", "Ганна");           // інший друг, те саме фото — вже не перший раз
        Assert.Single(_paid);
        Assert.Equal(1, t.Count(p.NickKey));
    }

    [Fact]
    public async Task Own_photo_guests_and_beyond_three_photos_do_not_pay()
    {
        var m = Mine();
        var t = Thanks();
        var photos = new List<GeoMinePhoto>();
        for (var i = 0; i < 4; i++) photos.Add(await Add(m, "Оля", "Місце " + i));
        t.Guessed(photos[0], "Оля");         // своє
        t.Guessed(photos[0], "оля ");        // своє, інакше записаний нік
        t.Guessed(photos[0], "Гість 4821");  // гість
        t.Guessed(photos[0], null);
        Assert.Empty(_paid);
        foreach (var p in photos) t.Guessed(p, "Петро");
        foreach (var p in photos) t.Guessed(p, "Ганна");
        Assert.Equal(photos.Take(GeoThanks.Photos).Select(p => "geo-thanks:" + p.Id), _paid.Select(x => x.Ref));
        Assert.All(_paid, x => Assert.Equal("Оля", x.Nick));
        Assert.Equal(GeoThanks.Photos, _journal.Count);
        Assert.False(t.Thanked(photos[3].Id));
    }

    [Fact]
    public async Task Thanks_survive_a_restart_through_the_database()
    {
        Directory.CreateDirectory(_dir);
        var db = new Db(Path.Combine(_dir, "t.db"));
        var m = Mine();
        var p = await Add(m, "Оля");
        var t = new GeoThanks(db) { Run = a => { a(); return Task.CompletedTask; }, Pay = (_, _, _, _, _) => true };
        await t.Loaded;
        t.Guessed(p, "Петро");
        var again = new GeoThanks(db);
        await again.Loaded;
        Assert.True(again.Thanked(p.Id));
        Assert.Equal(1, again.Count(p.NickKey));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
