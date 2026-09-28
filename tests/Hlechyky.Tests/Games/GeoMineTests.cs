using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Де це?», п. 52 «📷 Мої фото»: закидання (перетискання, EXIF геть, «це справді фото»), ліміти, хто може прибрати,
/// опція столу «Фото друзів», автор не вгадує й отримує половину середнього, історія — лише після розкриття.
/// </summary>
public sealed class GeoMineTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "geo-mine-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    static (int X, int Y) Kyiv => GeoMap.Project(50.4501, 30.5234);
    static (int X, int Y) Lviv => GeoMap.Project(49.8397, 24.0297);

    /// <summary>Крихітний JPEG, у якого SOF каже «640×480», плюс EXIF з GPS і коментар — усе, що не має дійти до гравця.</summary>
    static byte[] Dirty()
    {
        var jpeg = GeoCache.Jpeg;
        var sof = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        jpeg[sof + 5] = 0x01; jpeg[sof + 6] = 0xE0;    // висота 480
        jpeg[sof + 7] = 0x02; jpeg[sof + 8] = 0x80;    // ширина 640
        var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);
        ms.Write(Seg(0xE1, [.. Encoding.ASCII.GetBytes("Exif\0\0II*\0"), 8, 0, 0, 0, 0, 0, .. Encoding.ASCII.GetBytes("GPS 50.4501N 30.5234E")]));
        ms.Write(Seg(0xFE, Encoding.ASCII.GetBytes("secret comment")));
        ms.Write(jpeg, 2, jpeg.Length - 2);
        return ms.ToArray();
    }

    static byte[] Seg(byte marker, byte[] body)
    {
        var len = body.Length + 2;
        return [0xFF, marker, (byte)(len >> 8), (byte)len, .. body];
    }

    static bool Has(byte[] data, string text) => data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    /// <summary>Сховище з «перетискачем», що віддає прислане як є: перевіряємо, що сервер сам зрізає метадані.</summary>
    GeoMine Store(Db? db = null, long cap = GeoMine.MaxTotal) =>
        new(db, _dir, null) { Encoder = (b, _) => Task.FromResult<byte[]?>(b), TotalCap = cap };

    static Task<GeoMineReply> Add(GeoMine m, string nick, (int X, int Y)? at = null, string title = "Моя хата", string story = "Тут я виріс") =>
        m.AddAsync(nick, Dirty(), (at ?? Kyiv).X, (at ?? Kyiv).Y, title, story, Now, CancellationToken.None);

    // ---------------------------------------------------------------- закидання

    [Fact]
    public async Task Upload_strips_exif_on_the_server_even_if_the_encoder_left_it()
    {
        var m = Store();
        var r = await Add(m, "Оля");
        Assert.True(r.Ok, r.Message);
        var bytes = File.ReadAllBytes(m.PathOf(r.Photo!.Id));
        Assert.False(Has(bytes, "GPS"));
        Assert.False(Has(bytes, "secret"));
        Assert.False(GeoImage.HasSegment(bytes, 0xE1));
        Assert.Contains("Київ", r.Photo.Region);
        Assert.Equal("u:" + r.Photo.Id, r.Photo.PlaceId);
    }

    [Fact]
    public async Task Not_an_image_or_sea_or_empty_title_is_refused()
    {
        var m = Store();
        var html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        Assert.Equal(GeoMine.NotImage, (await m.AddAsync("Оля", html, Kyiv.X, Kyiv.Y, "Хата", "", Now, default)).Message);
        // «JPEG» за магічними байтами, а всередині — сміття: перетискач (ffmpeg) не розкодує — відмова
        var liar = new GeoMine(null, _dir, null) { Encoder = (_, _) => Task.FromResult<byte[]?>(null) };
        Assert.Equal(GeoMine.NotImage, (await liar.AddAsync("Оля", Dirty(), Kyiv.X, Kyiv.Y, "Хата", "", Now, default)).Message);
        Assert.Equal(GeoMine.NotLand, (await m.AddAsync("Оля", Dirty(), 5, 5, "Хата", "", Now, default)).Message);
        Assert.False((await Add(m, "Оля", title: " ")).Ok);
        Assert.False((await m.AddAsync("Оля", new byte[GeoMine.MaxBody + 1], Kyiv.X, Kyiv.Y, "Хата", "", Now, default)).Ok);
        // без ffmpeg і без перетискача фото не приймаються зовсім — неперевірене в гру не йде
        var bare = new GeoMine(null, _dir, Path.Combine(_dir, "нема-ffmpeg.exe"));
        Assert.Equal(GeoMine.NoTool, (await bare.AddAsync("Оля", Dirty(), Kyiv.X, Kyiv.Y, "Хата", "", Now, default)).Message);
        Assert.Empty(m.All);
    }

    [Fact]
    public async Task Limits_per_nick_and_for_the_whole_shelf()
    {
        var m = Store();
        for (var i = 0; i < GeoMine.MaxPerNick; i++) Assert.True((await Add(m, "Оля")).Ok);
        Assert.False((await Add(m, " оля ")).Ok);
        Assert.True((await Add(m, "Петро")).Ok);

        var small = Store(cap: GeoMine.MaxFile + 10);
        Assert.True((await Add(small, "Оля")).Ok);
        var full = await Add(small, "Петро");
        Assert.False(full.Ok);
        Assert.Contains("Полиця", full.Message);
    }

    [Fact]
    public void Titles_and_stories_lose_control_and_bidi_characters()
    {
        Assert.Equal("Хата бабусі", GeoMine.Clean("  Хата\u202E\u0000  бабусі \n", 60, lines: false));
        Assert.Equal("рядок 1\n\nрядок 2", GeoMine.Clean("рядок 1\n\n\n\n рядок 2", 280, lines: true));
        Assert.Equal(60, GeoMine.Clean(new string('я', 100), 60, false).Length);
    }

    // ---------------------------------------------------------------- прибрати, база

    [Fact]
    public async Task Only_the_author_or_admin_deletes_and_the_file_goes_too()
    {
        var m = Store();
        var id = (await Add(m, "Оля")).Photo!.Id;
        Assert.False(m.Delete(id, "Петро", admin: false).Ok);
        Assert.True(File.Exists(m.PathOf(id)));
        Assert.True(m.Delete(id, "ОЛЯ", admin: false).Ok);
        Assert.False(File.Exists(m.PathOf(id)));
        var id2 = (await Add(m, "Оля")).Photo!.Id;
        Assert.True(m.Delete(id2, "Адмін", admin: true).Ok);
        Assert.Empty(m.All);
        Assert.False(m.Delete("../../etc", "Адмін", admin: true).Ok);
    }

    [Fact]
    public async Task Photos_survive_a_restart()
    {
        var db = new Db(Path.Combine(_dir, "t.db"));
        var m = Store(db);
        await m.Loaded;
        var r = await Add(m, "Оля", Lviv, "Ратуша", "Сфоткав\nу дощ");
        var again = new GeoMine(new Db(Path.Combine(_dir, "t.db")), _dir, null);
        await again.Loaded;
        var p = Assert.Single(again.All);
        Assert.Equal((r.Photo!.Id, "Ратуша", "Сфоткав\nу дощ", "Оля"), (p.Id, p.Title, p.Story, p.Nick));
        Assert.Equal(Lviv, (p.X, p.Y));
        again.Delete(p.Id, "Оля", false);
        var third = new GeoMine(new Db(Path.Combine(_dir, "t.db")), _dir, null);
        await third.Loaded;
        Assert.Empty(third.All);
    }

    // ---------------------------------------------------------------- ендпоінти

    static HttpContext Http(string? nick, bool admin = false)
    {
        var c = new DefaultHttpContext();
        if (nick is not null) c.Items["nick"] = nick;
        if (admin) c.Items["role"] = "admin";
        return c;
    }

    [Fact]
    public async Task Guests_cannot_upload_and_others_cannot_peek_at_the_file()
    {
        var m = Store();
        var id = (await Add(m, "Оля")).Photo!.Id;
        Assert.False(GeoSetup.CanUpload(Http(null)));
        Assert.False(GeoSetup.CanUpload(Http("гість Вася")));
        Assert.True(GeoSetup.CanUpload(Http("Оля")));
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(GeoSetup.MineFile(id + ".jpg", Http("оля"), m));
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(GeoSetup.MineFile(id + ".jpg", Http("Петро"), m));
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(GeoSetup.MineFile(id + ".jpg", Http("гість"), m));
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(GeoSetup.MineFile(id + ".jpg", Http("Адмін", admin: true), m));
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(GeoSetup.MineFile("..%2f.jpg", Http("Адмін", admin: true), m));
    }

    // ---------------------------------------------------------------- справжній ffmpeg

    static string? Ffmpeg()
    {
        foreach (var p in new[] { Paths.Resolve("tools/yt-dlp/ffmpeg.exe"), "D:/or/tools/yt-dlp/ffmpeg.exe" })
            if (File.Exists(p)) return p;
        return null;
    }

    [Fact]
    public async Task Real_ffmpeg_reencodes_a_photo_and_rejects_a_fake_one()
    {
        if (Ffmpeg() is not { } ff) return;   // на машині без ffmpeg — нема що перевіряти
        Directory.CreateDirectory(_dir);
        var src = Path.Combine(_dir, "src.jpg");
        var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=1600x1200:rate=1", "-frames:v", "1", "-q:v", "2", src })
            psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) { await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); }
        var raw = File.ReadAllBytes(src);
        // вшиваємо EXIF з GPS після SOI
        byte[] dirty = [.. raw[..2], .. Seg(0xE1, [.. Encoding.ASCII.GetBytes("Exif\0\0II*\0"), 8, 0, 0, 0, 0, 0, .. Encoding.ASCII.GetBytes("GPS 50.45N 30.52E")]), .. raw[2..]];
        var m = new GeoMine(null, _dir, ff);
        var r = await m.AddAsync("Оля", dirty, Kyiv.X, Kyiv.Y, "Тест", "", Now, default);
        Assert.True(r.Ok, r.Message);
        var got = File.ReadAllBytes(m.PathOf(r.Photo!.Id));
        Assert.False(Has(got, "GPS"));
        var img = LavkaImage.Sniff(got)!;
        Assert.Equal((1280, 960), (img.Width, img.Height));
        Assert.True(got.Length <= GeoMine.MaxFile);
        // заголовок JPEG правдивий, а даних — сміття: ffmpeg не розкодує
        var junk = dirty[..600].Concat(Enumerable.Repeat((byte)0x55, 4000)).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();
        Assert.False((await m.AddAsync("Оля", junk, Kyiv.X, Kyiv.Y, "Тест", "", Now, default)).Ok);
        Assert.Empty(Directory.GetFiles(_dir, "in-*"));
    }

    // ---------------------------------------------------------------- гра

    static RoomHarness Table(GeoCache c, GeoMine m, object options, params string[] nicks)
    {
        var sp = new ServiceCollection().AddSingleton(c.Photos).AddSingleton(m).BuildServiceProvider();
        var h = new RoomHarness("geo", options, 7, sp);
        foreach (var n in nicks) h.Join(n);
        return h;
    }

    static string? Author(RoomHarness h) =>
        h.View(null).GetProperty("by") is { ValueKind: JsonValueKind.Object } by ? by.GetProperty("nick").GetString() : null;

    [Fact]
    public async Task Only_ours_needs_photos_and_the_author_alone_does_not_count()
    {
        using var c = new GeoCache();
        var m = Store();
        var empty = Table(c, m, new { friends = "only" }, "Оля", "Петро");
        Assert.False(empty.Start().Ok);
        Assert.Equal(GeoMatch.NoFriends, empty.Reply.Message);
        await Add(m, "Оля");
        var alone = Table(c, m, new { friends = "only" }, "Оля");
        Assert.False(alone.Start().Ok);
        var two = Table(c, m, new { friends = "only" }, "Оля", "Петро");
        Assert.True(two.Start().Ok, two.Reply.Message);
        Assert.Equal(1, two.View(0).GetProperty("rounds").GetInt32());
    }

    [Fact]
    public async Task Author_does_not_guess_gets_half_the_average_and_the_story_comes_only_on_reveal()
    {
        using var c = new GeoCache();
        var m = Store();
        var photo = (await Add(m, "Оля", Lviv, "Ратуша <b>вночі</b>", "Стояв під дощем <script>x</script>")).Photo!;
        var h = Table(c, m, new { friends = "only" }, "Оля", "Петро", "Ганна");
        Assert.True(h.Start().Ok, h.Reply.Message);
        GeoTests.Until(h, GeoMatch.PhaseGuess);
        Assert.Equal("Оля", Author(h));
        Assert.Equal([0], h.View(1).GetProperty("by").GetProperty("seats").EnumerateArray().Select(e => e.GetInt32()));
        // приховане: ні назви, ні історії, ні координат — нікому
        foreach (var seat in new int?[] { 0, 1, 2, null })
        {
            var raw = h.View(seat).GetRawText();
            Assert.DoesNotContain("Ратуша", raw);
            Assert.DoesNotContain("дощем", raw);
            Assert.DoesNotContain(photo.Id, raw);
        }
        Assert.DoesNotContain("Ратуша", JsonSerializer.Serialize(h.Room.Game.Frame()));
        Assert.Equal(GeoMatch.OwnPhoto, h.Act(0, "guess", new { x = Lviv.X, y = Lviv.Y }).Message);
        Assert.Equal(GeoMatch.OwnPhoto, h.Act(0, "area").Message);
        Assert.True(h.Act(1, "guess", new { x = Lviv.X, y = Lviv.Y }).Ok);
        Assert.True(h.Act(2, "guess", new { x = Kyiv.X, y = Kyiv.Y }).Ok);
        h.Act(1, "ready"); h.Act(2, "ready");
        h.Tick(1);   // автора не чекаємо: усі, хто шукав, готові — розкриття одразу
        Assert.Equal(GeoMatch.PhaseReveal, h.View(0).GetProperty("phase").GetString());
        var rv = h.View(1).GetProperty("reveal");
        Assert.Equal("Стояв під дощем <script>x</script>", rv.GetProperty("photo").GetProperty("story").GetString());
        Assert.Equal("Оля", rv.GetProperty("photo").GetProperty("author").GetString());
        var rows = rv.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal([0], h.View(1).GetProperty("by").GetProperty("seats").EnumerateArray().Select(e => e.GetInt32()));
        var own = rows.Single(r => r.GetProperty("seat").GetInt32() == 0);
        Assert.Equal(JsonValueKind.Null, own.GetProperty("km").ValueKind);
        var others = rows.Where(r => r.GetProperty("seat").GetInt32() != 0).Select(r => r.GetProperty("points").GetInt32()).ToList();
        Assert.Equal(2, others.Count);
        Assert.Equal((int)Math.Round(others.Average() / 2, MidpointRounding.AwayFromZero), own.GetProperty("points").GetInt32());
        Assert.False(own.GetProperty("best").GetBoolean());
    }

    [Fact]
    public async Task Mix_puts_up_to_a_third_of_rounds_from_friends()
    {
        using var c = new GeoCache();
        var m = Store();
        for (var i = 0; i < 5; i++) await Add(m, "Ганна");
        var h = Table(c, m, new { friends = "mix", rounds = "7" }, "Оля", "Петро");
        Assert.True(h.Start().Ok, h.Reply.Message);
        var friends = 0;
        for (var r = 0; r < 7; r++)
        {
            GeoTests.Until(h, GeoMatch.PhaseGuess);
            if (Author(h) is not null) friends++;
            h.Act(0, "guess", new { x = 100, y = 100 }); h.Act(1, "guess", new { x = 100, y = 100 });
            h.Act(0, "ready"); h.Act(1, "ready");
            GeoTests.Until(h, GeoMatch.PhaseReveal);
            h.Act(0, "next"); h.Act(1, "next");
            h.Tick(1);
        }
        Assert.Equal(2, friends);
        // вимкнено — жодного
        var off = Table(c, m, new { rounds = "5" }, "Оля", "Петро");
        Assert.True(off.Start().Ok);
        GeoTests.Until(off, GeoMatch.PhaseGuess);
        Assert.Null(Author(off));
    }
}
