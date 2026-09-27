using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Де це?»: фото — зрізання метаданих, кеш на диску, токени, завантажувач із Вікісховища (§7.2, §7.3).
/// Мережі тут нема: завантажувач ходить у заглушку <see cref="Stub"/>.
/// </summary>
public class GeoPhotosTests
{
    /// <summary>JPEG із «секретами»: EXIF (поворот + GPS-текст), XMP, ICC, коментар — усе, що не має дійти до гравця.</summary>
    static byte[] Dirty(int orientation = 6)
    {
        var jpeg = GeoCache.Jpeg;
        var segs = new List<byte[]>
        {
            Exif(orientation, "GPS 48.67333N 26.5625E Kamianets"),
            Seg(0xE1, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta>Kamianets-Podilskyi</x:xmpmeta>")),
            Seg(0xE2, Encoding.ASCII.GetBytes("ICC_PROFILE\0\u0001\u0001fake-profile")),
            Seg(0xFE, Encoding.ASCII.GetBytes("Zamek w Kamiencu Podolskim 2019")),
        };
        var ms = new MemoryStream();
        ms.Write(jpeg, 0, 2);                                   // SOI
        foreach (var s in segs) ms.Write(s);
        ms.Write(jpeg, 2, jpeg.Length - 2);                    // APP0 і решта
        return ms.ToArray();
    }

    static byte[] Seg(byte marker, byte[] body)
    {
        var len = body.Length + 2;
        return [0xFF, marker, (byte)(len >> 8), (byte)len, .. body];
    }

    /// <summary>APP1 EXIF (little-endian): IFD0 з Orientation і хвостом тексту «GPS…».</summary>
    static byte[] Exif(int orientation, string secret)
    {
        var tiff = new List<byte> { (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00 };
        tiff.AddRange([0x01, 0x00]);                                                     // одна позиція
        tiff.AddRange([0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00]);
        tiff.AddRange([0x00, 0x00, 0x00, 0x00]);                                         // наступного IFD нема
        tiff.AddRange(Encoding.ASCII.GetBytes(secret));
        return Seg(0xE1, [.. Encoding.ASCII.GetBytes("Exif\0\0"), .. tiff]);
    }

    static bool Contains(byte[] data, string text)
    {
        var needle = Encoding.ASCII.GetBytes(text);
        return data.AsSpan().IndexOf(needle) >= 0;
    }

    // ---------------------------------------------------------------------------------------
    // Strip
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Strip_removes_exif_and_comment_segments_but_keeps_the_picture_decodable()
    {
        var dirty = Dirty(orientation: 1);
        Assert.True(GeoImage.HasSegment(dirty, 0xE1));
        var clean = GeoImage.Strip(dirty)!;
        Assert.NotNull(clean);
        Assert.False(GeoImage.HasSegment(clean, 0xE1));
        Assert.False(GeoImage.HasSegment(clean, 0xE2));
        Assert.False(GeoImage.HasSegment(clean, 0xFE));
        Assert.True(GeoImage.HasSegment(clean, 0xE0));         // JFIF лишився
        Assert.True(GeoImage.HasSegment(clean, 0xDB));         // таблиці квантування
        Assert.True(GeoImage.HasSegment(clean, 0xC0));         // кадр
        foreach (var secret in new[] { "GPS", "Kamianets", "Kamiencu", "xmpmeta", "ICC_PROFILE" })
            Assert.False(Contains(clean, secret), secret);
        // картинка та сама: очищений брудний = очищений чистий, байт у байт
        Assert.Equal(GeoImage.Strip(GeoCache.Jpeg), clean);
        Assert.Equal(GeoCache.Jpeg, clean);
        Assert.Equal(0xD9, clean[^1]);
    }

    [Fact]
    public void Strip_keeps_only_a_bare_orientation_tag_from_exif()
    {
        var clean = GeoImage.Strip(Dirty(orientation: 6))!;
        Assert.True(GeoImage.HasSegment(clean, 0xE1));
        Assert.False(Contains(clean, "GPS"));
        var seg = GeoImage.OrientationSegment(6);
        Assert.Equal(36, seg.Length);                          // маркер, довжина 34 і тіло
        Assert.True(clean.AsSpan().IndexOf(seg) > 0);
        Assert.Equal(GeoCache.Jpeg.Length + seg.Length, clean.Length);
    }

    [Fact]
    public void Strip_rejects_bytes_that_are_not_a_jpeg()
    {
        Assert.Null(GeoImage.Strip([]));
        Assert.Null(GeoImage.Strip(Encoding.ASCII.GetBytes("<html>404</html>")));
        Assert.Null(GeoImage.Strip([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));   // PNG
        var jpeg = GeoCache.Jpeg;
        Assert.Null(GeoImage.Strip(jpeg.AsSpan(0, jpeg.Length / 2)));                        // обрізаний
        var broken = (byte[])jpeg.Clone();
        broken[2] = 0x00;                                                                    // сміття замість маркера
        Assert.Null(GeoImage.Strip(broken));
        Assert.Null(GeoImage.Strip([0xFF, 0xD8, 0xFF, 0xD9]));                               // без жодного кадру
    }

    // ---------------------------------------------------------------------------------------
    // кеш і токени
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Offline_photos_report_ready_only_for_places_with_a_cached_file()
    {
        using var c = new GeoCache(ready: p => p.Id is "g0001" or "g0016");
        Assert.True(c.Photos.Ready("g0001"));
        Assert.True(c.Photos.Ready("g0016"));
        Assert.False(c.Photos.Ready("g0002"));
        Assert.Equal(2, c.Photos.ReadyPlaces);
        Assert.Equal([0], c.Photos.ReadyPhotos("g0016"));       // лише перше з двох
        Assert.Empty(c.Photos.ReadyPhotos("g0002"));
        c.Add(c.Bank.Find("g0002")!);
        Assert.True(c.Photos.Ready("g0002"));
        c.Add(c.Bank.Find("g0016")!);
        Assert.Equal([0, 1], c.Photos.ReadyPhotos("g0016"));
    }

    [Fact]
    public void Issue_gives_24_hex_tokens_that_resolve_to_the_file_and_expire_after_45_minutes()
    {
        using var c = new GeoCache();
        var token = c.Photos.Issue("g0001", 0);
        Assert.Matches("^[0-9a-f]{24}$", token);
        var path = c.Photos.Resolve(token);
        Assert.Equal(c.Photos.PathFor(c.Bank.Find("g0001")!.Photos[0]), path);
        Assert.True(File.Exists(path));
        Assert.NotEqual(token, c.Photos.Issue("g0001", 0));    // щоразу новий
        c.Clock.Advance(TimeSpan.FromMinutes(44));
        Assert.NotNull(c.Photos.Resolve(token));
        c.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(c.Photos.Resolve(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456")]            // 23
    [InlineData("0123456789ABCDEF01234567")]           // великі
    [InlineData("../../data/hlechyky.db0000")]
    [InlineData("0123456789abcdef0123456g")]
    public void Resolve_refuses_anything_that_is_not_an_issued_token(string? token)
    {
        using var c = new GeoCache();
        c.Photos.Issue("g0001", 0);
        Assert.Null(c.Photos.Resolve(token));
    }

    [Fact]
    public void A_token_never_contains_the_place_id_or_the_source_file_name()
    {
        using var c = new GeoCache();
        foreach (var p in c.Bank.Places)
        {
            var token = c.Photos.Issue(p.Id, 0);
            Assert.DoesNotContain(p.Id, token);
            var file = Path.GetFileName(c.Photos.Resolve(token)!);
            Assert.Matches("^[0-9a-f]{32}\\.jpg$", file);
            Assert.DoesNotContain(Path.GetFileNameWithoutExtension(p.Photos[0].Url), file);
        }
    }

    [Fact]
    public void Tokens_are_capped_and_the_oldest_are_evicted()
    {
        using var c = new GeoCache();
        var first = c.Photos.Issue("g0001", 0);
        c.Clock.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < GeoPhotos.MaxTokens; i++) c.Photos.Issue("g0002", 0);
        Assert.Null(c.Photos.Resolve(first));
    }

    [Fact]
    public void Cache_file_name_is_a_hash_and_the_file_has_no_app1_segment()
    {
        using var c = new GeoCache(GeoCache.BankOf(GeoCache.Place("x1")), ready: _ => false);
        var url = c.Bank.Places[0].Photos[0].Url;
        Assert.True(c.Photos.Store(url, Dirty(orientation: 1)));
        var name = GeoPhotos.FileNameFor(url);
        Assert.Matches("^[0-9a-f]{32}\\.jpg$", name);
        var bytes = File.ReadAllBytes(Path.Combine(c.Dir, name));
        Assert.False(GeoImage.HasSegment(bytes, 0xE1));
        Assert.False(Contains(bytes, "Kamianets"));
        Assert.Empty(Directory.GetFiles(c.Dir, "*.tmp"));
        Assert.False(c.Photos.Store(url, Encoding.ASCII.GetBytes("не картинка")));
    }

    [Fact]
    public void Sweep_removes_files_that_are_not_in_the_bank()
    {
        using var c = new GeoCache();
        var stray = Path.Combine(c.Dir, new string('a', 32) + ".jpg");
        File.WriteAllBytes(stray, GeoCache.Jpeg);
        var count = Directory.GetFiles(c.Dir).Length;
        Assert.Equal(1, c.Photos.Sweep());
        Assert.False(File.Exists(stray));
        Assert.Equal(count - 1, Directory.GetFiles(c.Dir).Length);
        Assert.Equal(19, c.Photos.ReadyPlaces);
    }

    // ---------------------------------------------------------------------------------------
    // завантажувач
    // ---------------------------------------------------------------------------------------

    /// <summary>Заглушка мережі: запам'ятовує запити, відповідає за правилом.</summary>
    sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    static HttpResponseMessage Ok(byte[] body, string type)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return r;
    }

    [Fact]
    public async Task Fetch_uses_the_hlechyky_user_agent_and_skips_non_jpeg_responses()
    {
        var bank = GeoCache.BankOf(GeoCache.Place("j1"), GeoCache.Place("p1"));
        using var c = new GeoCache(bank, ready: _ => false);
        var stub = new Stub(req => req.RequestUri!.AbsolutePath.Contains("p1")
            ? Ok([0x89, 0x50, 0x4E, 0x47], "image/png")
            : Ok(Dirty(), "image/jpeg"));
        using var photos = new GeoPhotos(bank, c.Dir, c.Clock, handler: stub, pause: TimeSpan.Zero, online: false);
        Assert.Equal(1, await photos.PassAsync(CancellationToken.None));
        Assert.True(photos.Ready("j1"));
        Assert.False(photos.Ready("p1"));
        Assert.All(stub.Seen, r => Assert.Equal(GeoPhotos.UserAgent, r.Headers.UserAgent.ToString()));
        var file = File.ReadAllBytes(photos.PathFor(bank.Find("j1")!.Photos[0]));
        Assert.False(Contains(file, "GPS"));
        // PNG безнадійний: наступний прохід його вже не смикає, а готове не качає вдруге
        var before = stub.Seen.Count;
        Assert.Equal(0, await photos.PassAsync(CancellationToken.None));
        Assert.Equal(before, stub.Seen.Count);
    }

    [Fact]
    public async Task Fetch_refreshes_the_url_through_commons_after_three_failures_then_sleeps()
    {
        var bank = GeoCache.BankOf(GeoCache.Place("m1"));
        using var c = new GeoCache(bank, ready: _ => false);
        var moved = "https://upload.wikimedia.org/wikipedia/commons/thumb/a/ab/m1-new.jpg/1280px-m1-new.jpg";
        var stub = new Stub(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u.StartsWith("https://commons.wikimedia.org/w/api.php", StringComparison.Ordinal))
            {
                Assert.Contains("iiurlwidth=1280", u);
                var body = "{\"query\":{\"pages\":{\"1\":{\"imageinfo\":[{\"thumburl\":\"" + moved + "?utm=1\",\"url\":\"x\"}]}}}}";
                return Ok(Encoding.UTF8.GetBytes(body), "application/json");
            }
            return u == moved ? Ok(GeoCache.Jpeg, "image/jpeg") : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var photos = new GeoPhotos(bank, c.Dir, c.Clock, handler: stub, pause: TimeSpan.Zero, online: false);
        for (var i = 0; i < 2; i++) Assert.Equal(0, await photos.PassAsync(CancellationToken.None));
        Assert.Equal(1, await photos.PassAsync(CancellationToken.None));   // третя невдача → нова адреса → є
        Assert.True(photos.Ready("m1"));
        // файл названо за адресою з банку, а не за новою: банк лишився той самий
        Assert.True(File.Exists(photos.PathFor(bank.Places[0].Photos[0])));
    }

    [Fact]
    public async Task A_photo_that_keeps_failing_goes_to_sleep_until_restart()
    {
        var bank = GeoCache.BankOf(GeoCache.Place("z1"));
        using var c = new GeoCache(bank, ready: _ => false);
        var stub = new Stub(req => req.RequestUri!.Host == "commons.wikimedia.org"
            ? Ok(Encoding.UTF8.GetBytes("""{"query":{"pages":{"-1":{"missing":""}}}}"""), "application/json")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var photos = new GeoPhotos(bank, c.Dir, c.Clock, handler: stub, pause: TimeSpan.Zero, online: false);
        for (var i = 0; i < 5; i++) await photos.PassAsync(CancellationToken.None);
        Assert.False(photos.Ready("z1"));
        Assert.Equal(4, stub.Seen.Count);        // три спроби + один запит до API, далі тиша
    }

    // ---------------------------------------------------------------------------------------
    // ендпоінт /api/games/geo/<токен>.jpg
    // ---------------------------------------------------------------------------------------

    /// <summary>Виконати <see cref="GeoSetup.Serve"/> так, як це зробив би ASP.NET: код, заголовки, тіло.</summary>
    static async Task<(int Status, IHeaderDictionary Headers, byte[] Body)> Get(GeoCache c, string file)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await GeoSetup.Serve(file, c.Photos, ctx).ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, ctx.Response.Headers, body.ToArray());
    }

    [Fact]
    public async Task The_photo_endpoint_serves_a_live_token_as_a_private_nosniff_jpeg()
    {
        using var c = new GeoCache();
        var token = c.Photos.Issue("g0001", 0);
        var (status, headers, body) = await Get(c, token + ".jpg");
        Assert.Equal(200, status);
        Assert.Equal("image/jpeg", headers.ContentType.ToString());
        Assert.Equal("private, max-age=1800", headers.CacheControl.ToString());
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Equal(File.ReadAllBytes(c.Photos.Resolve(token)!), body);
    }

    [Theory]
    [InlineData("{token}")]                               // без .jpg
    [InlineData("{token}.png")]
    [InlineData("{token}.JPG")]
    [InlineData("{TOKEN}.jpg")]                           // великими літерами
    [InlineData("0123456789abcdef01234567.jpg")]          // схожий, але не виданий
    [InlineData("..%2F..%2Fdata%2Fhlechyky.db.jpg")]
    [InlineData("../../data/geo/places.jpg")]
    [InlineData("{name}")]                                // ім'я файла з кешу — не токен
    [InlineData(".jpg")]
    public async Task The_photo_endpoint_answers_404_to_anything_but_a_live_token(string file)
    {
        using var c = new GeoCache();
        var token = c.Photos.Issue("g0001", 0);
        var name = Path.GetFileName(c.Photos.Resolve(token)!);
        file = file.Replace("{token}", token).Replace("{TOKEN}", token.ToUpperInvariant()).Replace("{name}", name);
        var (status, _, body) = await Get(c, file);
        Assert.Equal(404, status);
        Assert.Empty(body);
    }

    [Fact]
    public async Task The_photo_endpoint_forgets_a_token_after_45_minutes()
    {
        using var c = new GeoCache();
        var token = c.Photos.Issue("g0001", 0);
        Assert.Equal(200, (await Get(c, token + ".jpg")).Status);
        c.Clock.Advance(TimeSpan.FromMinutes(46));
        Assert.Equal(404, (await Get(c, token + ".jpg")).Status);
    }
}
