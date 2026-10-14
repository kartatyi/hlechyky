using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// «Своє тло» (записка #49 від Smaug, 14.10.2026): річ полиці тла за 2500 🏺 — купівля й подарунок, завантаження (хто,
/// що, скільки й як часто), носіння в слоті тла поруч із візерунками, прибрати, адмінське «Зняти», віддача файла з
/// правильними заголовками, вигляд для всіх і стара база без таблиці.
/// </summary>
public sealed class LavkaBgTests : IDisposable
{
    // =============================================================================================
    // Заглушки й рига
    // =============================================================================================

    sealed class NoAir : ILavkaAir
    {
        public IReadOnlyList<LavkaQueued> Queue() => [];
        public (bool Ok, string Message) AddVoiceBefore(TrackInfo track, string filePath, string nick, string beforeItemId) => (false, "");
    }

    sealed class NoVoice : ILavkaVoice
    {
        public Task<LavkaVoiceClip?> SpeakAsync(string text, string title, string artist, CancellationToken ct) => Task.FromResult<LavkaVoiceClip?>(null);
    }

    sealed class FakeWire : ILavkaWire
    {
        readonly object _lock = new();
        public List<(string Nick, LavkaLook? Look)> Shown { get; } = [];
        public List<(string Nick, string Text)> Toasts { get; } = [];
        void ILavkaWire.Look(string nick, LavkaLook? look) { lock (_lock) Shown.Add((nick, look)); }
        void ILavkaWire.Fireworks(string nick) { }
        void ILavkaWire.Chat(object line) { }
        void ILavkaWire.Toast(string nick, string text) { lock (_lock) Toasts.Add((nick, text)); }
    }

    readonly EconomyRig _eco = new();
    readonly FakeWire _wire = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hlechyky-bg-" + Guid.NewGuid().ToString("N"));
    readonly string _avaDir = Path.Combine(Path.GetTempPath(), "hlechyky-ava-" + Guid.NewGuid().ToString("N"));
    readonly LavkaStore _store;
    readonly Lavka _lavka;
    readonly LavkaBackgrounds _bgs;
    readonly LavkaPhotos _photos;

    FakeClock Clock => _eco.Clock;

    public LavkaBgTests()
    {
        Assert.True(_eco.Db.AddAccount("Оля", "", "salt"));
        Assert.True(_eco.Db.AddAccount("Петро", "", "salt"));
        Assert.True(_eco.Db.AddAccount("Андрій", "", "salt"));
        Clock.UtcNow = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        _store = new LavkaStore(_eco.Db);
        _lavka = new Lavka(_store, _eco.Economy, _eco.Store, _eco.Db, _eco.Presence, new NoAir(), new NoVoice(), _wire, Clock,
            NullLogger<Lavka>.Instance);
        _bgs = new LavkaBackgrounds(_store, _lavka, _wire, Clock, new LavkaBgDir(_dir), NullLogger<LavkaBackgrounds>.Instance);
        _photos = new LavkaPhotos(_store, _lavka, _wire, Clock, new LavkaPhotoDir(_avaDir), NullLogger<LavkaPhotos>.Instance);
    }

    public void Dispose()
    {
        _eco.Dispose();
        foreach (var d in new[] { _dir, _avaDir })
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch (IOException) { /* хай лежить у temp */ }
    }

    void Give(string nick, int shards) => _eco.Economy.Grant(nick, shards, "listen", "test:" + Guid.NewGuid().ToString("N"));
    int Balance(string nick) => _eco.Economy.Balance(nick);

    /// <summary>Нік купив «Своє тло» — з запасом черепків.</summary>
    void Owner(string nick)
    {
        Give(nick, 3000);
        Assert.True(_lavka.Buy(nick, true, "bgphoto", null).Ok);
        _wire.Shown.Clear();
    }

    string[] Files() => Directory.Exists(_dir) ? [.. Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal)] : [];

    static HttpContext As(string nick, bool account = true, bool admin = false)
    {
        var c = Radio.As(nick, admin);
        if (account) c.Items["account"] = new Account(nick, "", "salt", admin ? "admin" : "member");
        return c;
    }

    // ---------- картинки: рівно ті байти, на які дивиться сервер (заголовок із розмірами), решта — хвіст ----------

    static byte[] Jpeg(int w = 1200, int h = 400, int pad = 0, byte seed = 1)
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        b.AddRange("JFIF\0"u8.ToArray());
        b.AddRange([1, 1, 0, 0, 1, 0, 1, 0, 0]);
        b.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1]);
        b.AddRange(Enumerable.Repeat(seed, pad));
        b.AddRange([0xFF, 0xD9]);
        return [.. b];
    }

    static byte[] Png(int w = 1200, int h = 400)
    {
        var b = new byte[8 + 25 + 12];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)h);
        b[24] = 8; b[25] = 2;
        "IEND"u8.CopyTo(b.AsSpan(37));
        return b;
    }

    /// <summary>WebP із втратами (VP8) — такий дає canvas.toBlob('image/webp').</summary>
    static byte[] Webp(int w = 1200, int h = 400)
    {
        var d = new byte[30];
        d[3] = 0x9D; d[4] = 0x01; d[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(6), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(8), (ushort)h);
        var b = new byte[20 + d.Length];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(b.Length - 8));
        "WEBP"u8.CopyTo(b.AsSpan(8));
        "VP8 "u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), (uint)d.Length);
        d.CopyTo(b, 20);
        return b;
    }

    // =============================================================================================
    // Купівля й подарунок
    // =============================================================================================

    [Fact]
    public void Own_bg_is_a_forever_item_of_the_bg_shelf_for_2500()
    {
        var p = LavkaCatalog.Get("bgphoto");
        Assert.NotNull(p);
        Assert.Equal(LavkaKind.Bg, p!.Kind);                          // на полиці «🖼 Тло», поруч із візерунками
        Assert.Equal("Своє тло", p.Title);
        Assert.Equal(2500, p.Price);
        Assert.Equal(LavkaCatalog.OwnBgArt, p.Art);
        Assert.Null(p.Season);
        Assert.Equal("Лавка — Своє тло", _eco.Economy.Reason("shop:bgphoto"));
        Assert.Equal("подарунок — Своє тло", _eco.Economy.Reason("gift:bgphoto"));
    }

    [Fact]
    public void Account_buys_it_once_and_nothing_is_worn_until_there_is_a_picture()
    {
        Give("Оля", 1000);
        Assert.True(_lavka.Buy("Оля", true, "stars", null).Ok);       // візерунок уже вдягнуто — він і лишається
        Give("Оля", 3000);
        var before = Balance("Оля");

        var r = _lavka.Buy("Оля", true, "bgphoto", null);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("«Своє тло» тепер твоє назавжди — обери картинку, і вона ляже в шапку профілю", r.Message);
        Assert.Equal(before - 2500, Balance("Оля"));
        Assert.True(_store.Owns("Оля", "bgphoto"));
        Assert.Equal("stars", _store.Worn("Оля", LavkaKind.Bg));
        Assert.Equal("stars", _lavka.LookOf("Оля")!.Bg);
        Assert.Equal(Lavka.Mine, _lavka.Buy("Оля", true, "bgphoto", null).Message);
        Assert.Equal(before - 2500, Balance("Оля"));

        var view = Views.Json(_lavka.View("Оля", true));
        var own = view.GetProperty("ownBg");
        Assert.Equal(["url", "readyAt"], own.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, own.GetProperty("url").ValueKind);
        Assert.Equal(JsonValueKind.Null, own.GetProperty("readyAt").ValueKind);   // перша картинка — одразу
        var item = view.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "bgphoto");
        Assert.Equal("bg", item.GetProperty("kind").GetString());
        Assert.True(item.GetProperty("owned").GetBoolean());
        Assert.False(item.GetProperty("worn").GetBoolean());
    }

    [Fact]
    public void Guest_and_the_poor_do_not_get_it()
    {
        Give("гість Вася", 5000);
        var vasya = Balance("гість Вася");
        Assert.Equal(Lavka.NotAccount, _lavka.Buy("гість Вася", false, "bgphoto", null).Message);
        Assert.Equal(vasya, Balance("гість Вася"));
        Assert.False(_store.Owns("гість Вася", "bgphoto"));

        Give("Петро", 2000);
        var petro = Balance("Петро");
        var poor = _lavka.Buy("Петро", true, "bgphoto", null);
        Assert.False(poor.Ok);
        Assert.Equal($"Бракує {2500 - petro} 🏺", poor.Message);
        Assert.Equal(petro, Balance("Петро"));
        Assert.False(_store.Owns("Петро", "bgphoto"));

        var own = Views.Json(_lavka.View("гість Вася", false)).GetProperty("ownBg");
        Assert.Equal(JsonValueKind.Null, own.GetProperty("url").ValueKind);
    }

    [Fact]
    public void It_can_be_gifted_and_the_recipient_sets_the_picture()
    {
        Give("Оля", 3000);
        var r = _lavka.Buy("Оля", true, "bgphoto", "Петро");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("Подаровано Петрові: «Своє тло» — назавжди", r.Message);
        Assert.True(_store.Owns("Петро", "bgphoto"));
        Assert.Null(_store.Worn("Петро", LavkaKind.Bg));               // слот порожній, але без картинки вдягати нічого
        Assert.Null(_lavka.LookOf("Петро"));

        Assert.True(_bgs.Set("Петро", true, Jpeg()).Ok);
        Assert.Equal(LavkaBackgrounds.NotOwned, _bgs.Set("Оля", true, Jpeg()).Message);   // подарувала — не собі
    }

    // =============================================================================================
    // Поставити
    // =============================================================================================

    [Fact]
    public void Owner_sets_a_picture_it_is_worn_at_once_and_everyone_sees_it()
    {
        Owner("Оля");

        var r = _bgs.Set("Оля", true, Webp());

        Assert.True(r.Ok, r.Message);
        Assert.Equal(LavkaBackgrounds.Saved, r.Message);
        Assert.Matches("^/api/lavka/bg/[0-9a-f]{16}-[0-9a-f]{12}\\.webp$", r.Url);
        Assert.Equal(Clock.UtcNow + Lavka.PhotoGap, r.ReadyAt);
        var file = Assert.Single(Files());
        Assert.Equal(r.Url, LavkaBackgrounds.UrlPrefix + file);
        Assert.DoesNotContain("оля", file);
        Assert.Equal(Webp(), File.ReadAllBytes(Path.Combine(_dir, file)));

        Assert.Equal("bgphoto", _store.Worn("Оля", LavkaKind.Bg));
        Assert.Equal(new LavkaLook(null, null, null, null, "own", null, r.Url), _lavka.LookOf("Оля"));
        var (nick, look) = Assert.Single(_wire.Shown);
        Assert.Equal("Оля", nick);
        Assert.Equal(r.Url, look!.BgUrl);
        // усі бачать у /api/lavka/looks: bg = own і адреса окремим полем
        var olya = Views.Json(LavkaSetup.Looks(_lavka)).GetProperty("looks").GetProperty("Оля");
        Assert.Equal(["icon", "frame", "color", "title", "bg", "bgUrl"], olya.EnumerateObject().Select(p => p.Name));
        Assert.Equal("own", olya.GetProperty("bg").GetString());
        Assert.Equal(r.Url, olya.GetProperty("bgUrl").GetString());
        // і в своїй вітрині
        var view = Views.Json(_lavka.View("Оля", true));
        Assert.Equal(r.Url, view.GetProperty("ownBg").GetProperty("url").GetString());
        Assert.Equal("bgphoto", view.GetProperty("worn").GetProperty("bg").GetString());
    }

    [Fact]
    public void Own_bg_and_own_photo_live_side_by_side_in_one_look()
    {
        Owner("Оля");
        Give("Оля", 3000);
        Assert.True(_lavka.Buy("Оля", true, "photo", null).Ok);
        var photo = _photos.Set("Оля", true, Jpeg(256, 256)).Url;
        var bg = _bgs.Set("Оля", true, Jpeg()).Url;

        Assert.Equal(new LavkaLook(null, null, null, null, "own", photo, bg), _lavka.LookOf("Оля"));
        var e = Views.Json(LavkaSetup.Looks(_lavka)).GetProperty("looks").GetProperty("Оля");
        Assert.Equal(["icon", "frame", "color", "title", "bg", "photo", "bgUrl"], e.EnumerateObject().Select(p => p.Name));
        Assert.StartsWith(LavkaPhotos.UrlPrefix, photo);
        Assert.StartsWith(LavkaBackgrounds.UrlPrefix, bg);
    }

    [Fact]
    public void Only_the_owner_account_may_set_a_picture()
    {
        Assert.Equal(LavkaBackgrounds.NotOwned, _bgs.Set("Оля", true, Jpeg()).Message);
        Give("гість Вася", 5000);
        Assert.Equal(Lavka.NotAccount, _bgs.Set("гість Вася", false, Jpeg()).Message);
        Owner("Оля");
        Assert.Equal(Lavka.NotAccount, _bgs.Set("Оля", false, Jpeg()).Message);
        Assert.Empty(Files());
        Assert.Empty(_wire.Shown);
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("svg")]
    [InlineData("html")]
    [InlineData("half-png")]
    [InlineData("png-without-size")]
    public void Only_jpeg_png_and_webp_by_magic_bytes(string kind)
    {
        Owner("Оля");
        byte[] body = kind switch
        {
            "gif" => [.. "GIF89a"u8.ToArray(), 1, 0, 1, 0, 0, 0, 0],
            "svg" => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"),
            "html" => Encoding.UTF8.GetBytes("<!doctype html><script>alert(1)</script>"),
            "half-png" => Png()[..20],
            _ => Png(0, 400),
        };

        var r = _bgs.Set("Оля", true, body);

        Assert.False(r.Ok);
        Assert.Equal(LavkaBackgrounds.NotImage, r.Message);
        Assert.Empty(Files());
        Assert.Null(_lavka.LookOf("Оля"));
        Assert.Null(_lavka.ReadyAt("Оля", "bgphoto"));                // невдала спроба перерви не вмикає
    }

    [Fact]
    public void Empty_and_too_heavy_bodies_are_refused_but_the_limit_itself_passes()
    {
        Owner("Оля");
        Assert.Equal(LavkaBackgrounds.Empty, _bgs.Set("Оля", true, []).Message);

        var heavy = Jpeg(pad: LavkaBackgrounds.MaxBytes);
        Assert.True(heavy.Length > LavkaBackgrounds.MaxBytes);
        Assert.Equal("Завеликий файл: до 400 КБ", _bgs.Set("Оля", true, heavy).Message);
        Assert.Empty(Files());

        var edge = Jpeg(pad: LavkaBackgrounds.MaxBytes - Jpeg().Length);
        Assert.Equal(LavkaBackgrounds.MaxBytes, edge.Length);
        Assert.True(_bgs.Set("Оля", true, edge).Ok);
    }

    [Theory]
    [InlineData(2001, 667, false)]
    [InlineData(600, 2001, false)]
    [InlineData(20000, 20000, false)]
    [InlineData(2000, 667, true)]
    [InlineData(2000, 2000, true)]
    public void Sides_up_to_2000(int w, int h, bool ok)
    {
        Owner("Оля");
        var r = _bgs.Set("Оля", true, Png(w, h));
        Assert.Equal(ok, r.Ok);
        if (!ok) Assert.Equal("Завелика картинка: до 2000×2000", r.Message);
    }

    [Fact]
    public void New_picture_not_more_often_than_once_a_day_and_the_old_file_is_removed()
    {
        Owner("Оля");
        var first = _bgs.Set("Оля", true, Jpeg(seed: 1));
        Assert.True(first.Ok);

        Clock.Advance(TimeSpan.FromHours(20));
        var soon = _bgs.Set("Оля", true, Jpeg(pad: 10, seed: 2));
        Assert.False(soon.Ok);
        Assert.Equal("Нове тло — через 4 год", soon.Message);
        Assert.Equal(first.Url, soon.Url);
        Assert.Single(Files());

        Clock.Advance(TimeSpan.FromHours(4));
        var next = _bgs.Set("Оля", true, Jpeg(pad: 10, seed: 2));
        Assert.True(next.Ok, next.Message);
        Assert.NotEqual(first.Url, next.Url);                         // нова версія — нова адреса
        Assert.Equal(next.Url, LavkaBackgrounds.UrlPrefix + Assert.Single(Files()));
        Assert.Equal(next.Url, _lavka.LookOf("Оля")!.BgUrl);
    }

    [Fact]
    public void Photo_and_bg_limits_are_separate()
    {
        Owner("Оля");
        Give("Оля", 3000);
        Assert.True(_lavka.Buy("Оля", true, "photo", null).Ok);
        Assert.True(_photos.Set("Оля", true, Jpeg(256, 256)).Ok);
        Assert.True(_bgs.Set("Оля", true, Jpeg()).Ok);                 // фото щойно — а тло ще можна
        Assert.StartsWith("Нове фото", _photos.Set("Оля", true, Png(256, 256)).Message);
        Assert.StartsWith("Нове тло", _bgs.Set("Оля", true, Png()).Message);
    }

    // =============================================================================================
    // Носіння: своє тло — один із варіантів слота тла
    // =============================================================================================

    [Fact]
    public void Pattern_and_own_bg_switch_in_one_slot()
    {
        Give("Оля", 1000);
        Assert.True(_lavka.Buy("Оля", true, "stars", null).Ok);
        Owner("Оля");
        // без картинки своє тло не вдягнеш
        Assert.Equal(Lavka.NoOwnBg, _lavka.Wear("Оля", true, "bg", "bgphoto").Message);
        Assert.Equal("stars", _lavka.LookOf("Оля")!.Bg);

        var url = _bgs.Set("Оля", true, Jpeg()).Url;
        Assert.Equal("own", _lavka.LookOf("Оля")!.Bg);                // поставив — і вдягнулось замість зірок

        Assert.True(_lavka.Wear("Оля", true, "bg", "stars").Ok);       // візерунок — своє знялось
        Assert.Equal(new LavkaLook(null, null, null, null, "stars"), _lavka.LookOf("Оля"));
        Assert.DoesNotContain("bgUrl", Views.Text(_lavka.LookOf("Оля")));
        Assert.Single(Files());                                       // а картинка лишилась — можна вдягти назад
        Assert.Equal(url, Views.Json(_lavka.View("Оля", true)).GetProperty("ownBg").GetProperty("url").GetString());

        Assert.True(_lavka.Wear("Оля", true, "bg", "bgphoto").Ok);     // назад — без доби очікування
        Assert.Equal(new LavkaLook(null, null, null, null, "own", null, url), _lavka.LookOf("Оля"));

        Assert.True(_lavka.Wear("Оля", true, "bg", null).Ok);          // «Зняти» — без тла
        Assert.Null(_lavka.LookOf("Оля"));
        Assert.Equal(Lavka.WrongSlot, _lavka.Wear("Оля", true, "icon", "bgphoto").Message);
        Assert.Equal(Lavka.NotOwned, _lavka.Wear("Петро", true, "bg", "bgphoto").Message);
    }

    // =============================================================================================
    // Прибрати
    // =============================================================================================

    [Fact]
    public void Owner_removes_the_worn_picture_and_the_profile_has_no_bg_but_the_daily_limit_stays()
    {
        Owner("Оля");
        Assert.True(_bgs.Set("Оля", true, Jpeg()).Ok);
        _wire.Shown.Clear();

        var r = _bgs.Remove("Оля", true);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("Тло прибрано — профіль знову без тла", r.Message);
        Assert.Empty(Files());
        Assert.Null(_store.Worn("Оля", LavkaKind.Bg));
        Assert.Null(_lavka.LookOf("Оля"));
        Assert.Equal(("Оля", (LavkaLook?)null), Assert.Single(_wire.Shown));
        Assert.True(_store.Owns("Оля", "bgphoto"));
        Assert.Equal("Нове тло — через 24 год", _bgs.Set("Оля", true, Png()).Message);   // прибрати й одразу інше — обхід доби
        Assert.Equal("Свого тла й так нема", _bgs.Remove("Оля", true).Message);
        Assert.Equal(Lavka.NotAccount, _bgs.Remove("Оля", false).Message);
    }

    [Fact]
    public void Removing_a_picture_that_is_not_worn_keeps_the_pattern()
    {
        Give("Оля", 1000);
        Assert.True(_lavka.Buy("Оля", true, "stripes", null).Ok);
        Owner("Оля");
        Assert.True(_bgs.Set("Оля", true, Jpeg()).Ok);
        Assert.True(_lavka.Wear("Оля", true, "bg", "stripes").Ok);

        var r = _bgs.Remove("Оля", true);

        Assert.Equal("Тло прибрано", r.Message);
        Assert.Equal("stripes", _lavka.LookOf("Оля")!.Bg);
        Assert.Equal(Lavka.NoOwnBg, _lavka.Wear("Оля", true, "bg", "bgphoto").Message);
    }

    // =============================================================================================
    // Адмін
    // =============================================================================================

    [Fact]
    public void Admin_lists_every_bg_fresh_first_and_photos_stay_apart()
    {
        Owner("Оля");
        Owner("Петро");
        var olya = _bgs.Set("Оля", true, Jpeg());
        Clock.Advance(TimeSpan.FromMinutes(5));
        var petro = _bgs.Set("Петро", true, Png());

        var (status, body) = Radio.Reply(LavkaSetup.AdminBgs(As("Андрій", admin: true), _bgs));

        Assert.Equal(200, status);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Петро", "Оля"], items.Select(i => i.GetProperty("nick").GetString()));
        Assert.Equal(["nick", "url", "at", "bytes"], items[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(petro.Url, items[0].GetProperty("url").GetString());
        Assert.Equal(olya.Url, items[1].GetProperty("url").GetString());
        Assert.Equal(Png().Length, items[0].GetProperty("bytes").GetInt32());
        Assert.Empty(_photos.All());                                  // тла — не фото

        var (denied, why) = Radio.Reply(LavkaSetup.AdminBgs(As("Оля"), _bgs));
        Assert.Equal(400, denied);
        Assert.Equal("Це бачить лише розробник", why.GetProperty("message").GetString());
    }

    [Fact]
    public void Admin_takes_a_bg_down_the_item_stays_and_a_new_picture_can_go_up_at_once()
    {
        Owner("Оля");
        Assert.True(_bgs.Set("Оля", true, Jpeg()).Ok);
        _wire.Shown.Clear();

        var (status, body) = Radio.Reply(LavkaSetup.TakeDownBg(As("Андрій", admin: true), new LavkaSetup.TakeDownRequest("оля"), _bgs));

        Assert.Equal(200, status);
        Assert.Equal("Тло Олі знято", body.GetProperty("message").GetString());
        Assert.Empty(Files());
        Assert.Null(_lavka.LookOf("Оля"));
        Assert.Null(_store.Worn("Оля", LavkaKind.Bg));
        Assert.Equal(("Оля", (LavkaLook?)null), Assert.Single(_wire.Shown));
        Assert.Equal([("Оля", "🖼 Розробник зняв твоє тло — можна одразу поставити інше")], _wire.Toasts);
        Assert.True(_store.Owns("Оля", "bgphoto"));                   // річ лишається
        Assert.Null(_lavka.ReadyAt("Оля", "bgphoto"));                 // і доби чекати не треба
        Assert.True(_bgs.Set("Оля", true, Png()).Ok);
    }

    [Fact]
    public void Take_down_refusals()
    {
        Owner("Оля");
        Assert.True(_bgs.Set("Оля", true, Jpeg()).Ok);

        var (status, body) = Radio.Reply(LavkaSetup.TakeDownBg(As("Петро"), new LavkaSetup.TakeDownRequest("Оля"), _bgs));
        Assert.Equal(400, status);
        Assert.Equal("Це вміє лише розробник", body.GetProperty("message").GetString());
        Assert.Single(Files());

        Assert.Equal("У Петра свого тла нема", _bgs.TakeDown("Петро").Message);
        Assert.Equal("Чиє тло зняти?", _bgs.TakeDown("  ").Message);
    }

    // =============================================================================================
    // HTTP: POST/DELETE /api/lavka/bg і віддача файла
    // =============================================================================================

    static HttpContext Upload(string nick, byte[] body, string contentType, bool account = true)
    {
        var c = As(nick, account);
        c.Request.Method = "POST";
        c.Request.ContentType = contentType;
        c.Request.Body = new MemoryStream(body);
        return c;
    }

    [Fact]
    public async Task Upload_route_reads_the_body_and_answers_with_the_new_address()
    {
        Owner("Оля");
        var (status, body) = Radio.Reply(await LavkaSetup.SetBg(Upload("Оля", Webp(), "image/webp"), _bgs));
        Assert.Equal(200, status);
        Assert.Equal(["ok", "message", "url", "readyAt"], body.EnumerateObject().Select(p => p.Name));
        Assert.StartsWith(LavkaBackgrounds.UrlPrefix, body.GetProperty("url").GetString());
        Assert.EndsWith(".webp", body.GetProperty("url").GetString());

        var (gone, removed) = Radio.Reply(LavkaSetup.RemoveBg(As("Оля"), _bgs));
        Assert.Equal(200, gone);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("url").ValueKind);
        Assert.Equal(Clock.UtcNow + Lavka.PhotoGap, removed.GetProperty("readyAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Upload_route_does_not_swallow_a_huge_body_and_refuses_guests()
    {
        Owner("Оля");
        var c = Upload("Оля", Jpeg(pad: 5 * 1024 * 1024), "image/jpeg");
        var (status, body) = Radio.Reply(await LavkaSetup.SetBg(c, _bgs));
        Assert.Equal(400, status);
        Assert.Equal("Завеликий файл: до 400 КБ", body.GetProperty("message").GetString());
        Assert.True(c.Request.Body.Position <= LavkaBackgrounds.MaxBytes + 1);

        var (guest, why) = Radio.Reply(await LavkaSetup.SetBg(Upload("гість Вася", Jpeg(), "image/jpeg", account: false), _bgs));
        Assert.Equal(400, guest);
        Assert.Equal(Lavka.NotAccount, why.GetProperty("message").GetString());
    }

    async Task<(int Status, IHeaderDictionary Headers, byte[] Body)> Get(string file)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await LavkaSetup.BgFile(file, _bgs, ctx).ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, ctx.Response.Headers, body.ToArray());
    }

    [Theory]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("png", "image/png")]
    [InlineData("webp", "image/webp")]
    public async Task The_file_is_served_by_its_magic_bytes_nosniff_and_cached_for_a_year(string kind, string mime)
    {
        Owner("Оля");
        var bytes = kind switch { "jpeg" => Jpeg(), "png" => Png(), _ => Webp() };
        var url = _bgs.Set("Оля", true, bytes).Url!;

        var (status, headers, body) = await Get(url[LavkaBackgrounds.UrlPrefix.Length..]);

        Assert.Equal(200, status);
        Assert.Equal(mime, headers.ContentType.ToString());
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Equal("public, max-age=31536000, immutable", headers.CacheControl.ToString());
        Assert.Equal(bytes, body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef-0123456789ab.jpg")]
    [InlineData("0123456789abcdef-0123456789ab.gif")]
    [InlineData("../../data/hlechyky.db")]
    [InlineData("оля.jpg")]
    public async Task Anything_but_a_live_bg_name_is_404(string file)
    {
        Owner("Оля");
        Assert.True(_bgs.Set("Оля", true, Jpeg()).Ok);
        Assert.Equal(404, (await Get(file)).Status);
    }

    [Fact]
    public async Task A_photo_is_not_served_from_the_bg_route()
    {
        Owner("Оля");
        Give("Оля", 3000);
        Assert.True(_lavka.Buy("Оля", true, "photo", null).Ok);
        var photo = _photos.Set("Оля", true, Jpeg(256, 256)).Url!;
        Assert.Equal(404, (await Get(photo[LavkaPhotos.UrlPrefix.Length..])).Status);
    }

    // =============================================================================================
    // Стара база
    // =============================================================================================

    [Fact]
    public void Old_database_without_the_bg_table_opens_and_the_migration_is_idempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), "hlechyky-oldbg-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // база з проду до свого тла: фото вже є, lavka_bgphoto — ще нема
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE lavka_worn(nick_key TEXT NOT NULL, nick TEXT NOT NULL, slot TEXT NOT NULL, item TEXT NOT NULL, PRIMARY KEY(nick_key, slot));
                    CREATE TABLE lavka_photo(nick_key TEXT NOT NULL PRIMARY KEY, nick TEXT NOT NULL, file TEXT NOT NULL, bytes INTEGER NOT NULL, at TEXT NOT NULL);
                    INSERT INTO lavka_photo VALUES('оля', 'Оля', '0123456789abcdef-0123456789ab.jpg', 100, '2026-10-01T10:00:00.0000000+00:00');
                    INSERT INTO lavka_worn VALUES('оля', 'Оля', 'bg', 'stars');
                    """;
                cmd.ExecuteNonQuery();
            }
            var db = new Db(path);
            var store = new LavkaStore(db);
            _ = new LavkaStore(db);
            Assert.Null(store.BgPhoto("Оля"));
            Assert.Empty(store.AllBgPhotos());
            Assert.Null(store.DropBgPhoto("Оля"));
            Assert.Equal(LavkaPhotos.UrlPrefix + "0123456789abcdef-0123456789ab.jpg", store.Photo("Оля")!.Url);   // фото — як було
            Assert.Equal("stars", store.Worn("Оля", "bg"));
        }
        finally
        {
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
                SqliteConnection.ClearPool(c);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(path + suffix); } catch (IOException) { /* хай лежить у temp */ }
        }
    }
}
