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
/// Відповіді на записки друзів (28.09.2026): сезонний значок «Гарбуз» 🎃 (Mariana) і «Своя фотка» на аватарку (Назар) —
/// купівля й подарунок вміння, завантаження (хто, що, скільки й як часто), прибрати, адмінське «Зняти», віддача файла з
/// правильними заголовками, вигляд для всіх і сумісність зі старою базою та старим клієнтом.
/// </summary>
public sealed class LavkaPhotoTests : IDisposable
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
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hlechyky-ava-" + Guid.NewGuid().ToString("N"));
    readonly LavkaStore _store;
    readonly Lavka _lavka;
    readonly LavkaPhotos _photos;

    FakeClock Clock => _eco.Clock;

    public LavkaPhotoTests()
    {
        Assert.True(_eco.Db.AddAccount("Оля", "", "salt"));
        Assert.True(_eco.Db.AddAccount("Петро", "", "salt"));
        Assert.True(_eco.Db.AddAccount("Андрій", "", "salt"));
        Clock.UtcNow = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);   // 12:00 28.09 за Києвом
        _store = new LavkaStore(_eco.Db);
        _lavka = new Lavka(_store, _eco.Economy, _eco.Store, _eco.Db, _eco.Presence, new NoAir(), new NoVoice(), _wire, Clock,
            NullLogger<Lavka>.Instance);
        _photos = new LavkaPhotos(_store, _lavka, _wire, Clock, new LavkaPhotoDir(_dir), NullLogger<LavkaPhotos>.Instance);
    }

    public void Dispose()
    {
        _eco.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* хай лежить у temp */ }
    }

    void Give(string nick, int shards) => _eco.Economy.Grant(nick, shards, "listen", "test:" + Guid.NewGuid().ToString("N"));
    int Balance(string nick) => _eco.Economy.Balance(nick);

    /// <summary>Нік купив «Своя фотка» — з запасом черепків.</summary>
    void Owner(string nick)
    {
        Give(nick, 3000);
        Assert.True(_lavka.Buy(nick, true, "photo", null).Ok);
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

    /// <summary>JPEG: SOI, JFIF, кадр SOF0 з розмірами, кінець. <paramref name="pad"/> — скільки байтів дописати в кінець.</summary>
    static byte[] Jpeg(int w = 256, int h = 256, int pad = 0, byte seed = 1)
    {
        var b = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        b.AddRange("JFIF\0"u8.ToArray());
        b.AddRange([1, 1, 0, 0, 1, 0, 1, 0, 0]);
        b.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1]);
        b.AddRange(Enumerable.Repeat(seed, pad));
        b.AddRange([0xFF, 0xD9]);
        return [.. b];
    }

    static byte[] Png(int w = 256, int h = 256)
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

    static byte[] Riff(string chunk, byte[] data)
    {
        var b = new byte[20 + data.Length];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(b.Length - 8));
        "WEBP"u8.CopyTo(b.AsSpan(8));
        Encoding.ASCII.GetBytes(chunk).CopyTo(b, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), (uint)data.Length);
        data.CopyTo(b, 20);
        return b;
    }

    /// <summary>WebP із втратами (VP8) — такий дає canvas.toBlob('image/webp').</summary>
    static byte[] WebpLossy(int w = 256, int h = 256)
    {
        var d = new byte[30];
        d[3] = 0x9D; d[4] = 0x01; d[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(6), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(8), (ushort)h);
        return Riff("VP8 ", d);
    }

    static byte[] WebpLossless(int w, int h)
    {
        var d = new byte[16];
        d[0] = 0x2F;
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(1), (uint)((w - 1) | (h - 1) << 14));
        return Riff("VP8L", d);
    }

    static byte[] WebpExtended(int w, int h)
    {
        var d = new byte[18];
        d[4] = (byte)(w - 1); d[5] = (byte)((w - 1) >> 8); d[6] = (byte)((w - 1) >> 16);
        d[7] = (byte)(h - 1); d[8] = (byte)((h - 1) >> 8); d[9] = (byte)((h - 1) >> 16);
        return Riff("VP8X", d);
    }

    // =============================================================================================
    // 🎃 Гарбуз
    // =============================================================================================

    [Fact]
    public void Pumpkin_is_an_autumn_icon_for_300()
    {
        var p = LavkaCatalog.Get("pumpkin");
        Assert.NotNull(p);
        Assert.Equal(LavkaKind.Icon, p!.Kind);
        Assert.Equal("Гарбуз", p.Title);
        Assert.Equal("🎃", p.Art);
        Assert.Equal(300, p.Price);
        Assert.Equal(3, p.Tier);
        Assert.Equal(new LavkaSeason("09-15", "11-30"), p.Season);
        Assert.Equal("Лавка — значок «Гарбуз» 🎃", _eco.Economy.Reason("shop:pumpkin"));
    }

    [Theory]
    [InlineData("09-14", false)]
    [InlineData("09-15", true)]
    [InlineData("09-28", true)]
    [InlineData("10-31", true)]
    [InlineData("11-30", true)]
    [InlineData("12-01", false)]
    [InlineData("01-01", false)]
    [InlineData("06-15", false)]
    public void Pumpkin_season_is_plain_mid_september_to_end_of_november(string day, bool open) =>
        Assert.Equal(open, LavkaCatalog.Get("pumpkin")!.Season!.Open(day));

    [Fact]
    public void Pumpkin_sells_today_and_the_shop_says_until_when()
    {
        var item = Views.Json(_lavka.View("Оля", true)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == "pumpkin");
        var season = item.GetProperty("season");
        Assert.Equal("09-15", season.GetProperty("from").GetString());
        Assert.Equal("11-30", season.GetProperty("to").GetString());
        Assert.True(season.GetProperty("open").GetBoolean());          // 28.09 — сезон

        Give("Оля", 1000);
        var before = Balance("Оля");
        var r = _lavka.Buy("Оля", true, "pumpkin", null);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("Значок «Гарбуз» 🎃 тепер твій назавжди — уже на аватарці", r.Message);
        Assert.Equal(before - 300, Balance("Оля"));
        Assert.Equal("🎃", _lavka.LookOf("Оля")!.Icon);
    }

    [Fact]
    public void Pumpkin_season_turns_at_kyiv_midnight_and_what_was_bought_stays()
    {
        Give("Оля", 1000);
        Give("Петро", 1000);
        Clock.UtcNow = new DateTimeOffset(2026, 9, 14, 20, 59, 0, TimeSpan.Zero);    // 23:59 14.09 за Києвом — ще ні
        Assert.Equal("Повернеться 15.09", _lavka.Buy("Оля", true, "pumpkin", null).Message);
        Clock.UtcNow = new DateTimeOffset(2026, 9, 14, 21, 0, 0, TimeSpan.Zero);     // 00:00 15.09 — уже
        Assert.True(_lavka.Buy("Оля", true, "pumpkin", null).Ok);

        Clock.UtcNow = new DateTimeOffset(2026, 11, 30, 21, 59, 0, TimeSpan.Zero);   // 23:59 30.11 (зимовий час) — ще можна
        Assert.True(_lavka.Buy("Петро", true, "pumpkin", "Андрій").Ok);
        Clock.UtcNow = new DateTimeOffset(2026, 11, 30, 22, 0, 0, TimeSpan.Zero);    // 00:00 01.12 — усе
        Assert.Equal("Повернеться 15.09", _lavka.Buy("Петро", true, "pumpkin", null).Message);
        Assert.Equal("🎃", _lavka.LookOf("Оля")!.Icon);                              // куплене — назавжди
        Assert.Equal("🎃", _lavka.LookOf("Андрій")!.Icon);
    }

    // =============================================================================================
    // Своя фотка: купівля й подарунок
    // =============================================================================================

    [Fact]
    public void Photo_is_a_forever_perk_for_2000()
    {
        var p = LavkaCatalog.Get("photo");
        Assert.NotNull(p);
        Assert.Equal(LavkaKind.Perk, p!.Kind);
        Assert.Equal("Своя фотка", p.Title);
        Assert.Equal(2000, p.Price);
        Assert.Null(p.Season);
        Assert.Equal("Лавка — Своя фотка", _eco.Economy.Reason("shop:photo"));
        Assert.Equal("подарунок — Своя фотка", _eco.Economy.Reason("gift:photo"));
    }

    [Fact]
    public void Account_buys_the_photo_once_and_nothing_is_worn()
    {
        Give("Оля", 2500);
        var before = Balance("Оля");

        var r = _lavka.Buy("Оля", true, "photo", null);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("«Своя фотка» тепер твоя назавжди — обери фото, і воно стане аватаркою", r.Message);
        Assert.Equal(before - 2000, Balance("Оля"));
        Assert.True(_store.Owns("Оля", "photo"));
        Assert.Null(_lavka.LookOf("Оля"));                              // вміння — не річ на аватарці
        Assert.Equal(Lavka.Mine, _lavka.Buy("Оля", true, "photo", null).Message);
        Assert.Equal(before - 2000, Balance("Оля"));

        var perk = Views.Json(_lavka.View("Оля", true)).GetProperty("perks").GetProperty("photo");
        Assert.Equal(["owned", "readyAt", "url"], perk.EnumerateObject().Select(p => p.Name));
        Assert.True(perk.GetProperty("owned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, perk.GetProperty("readyAt").ValueKind);   // перше фото — одразу
        Assert.Equal(JsonValueKind.Null, perk.GetProperty("url").ValueKind);
    }

    [Fact]
    public void Guest_and_the_poor_do_not_get_the_photo()
    {
        Give("гість Вася", 5000);
        var vasya = Balance("гість Вася");
        Assert.Equal(Lavka.NotAccount, _lavka.Buy("гість Вася", false, "photo", null).Message);
        Assert.Equal(vasya, Balance("гість Вася"));                   // нічого не списано
        Assert.False(_store.Owns("гість Вася", "photo"));

        Give("Петро", 1500);
        var petro = Balance("Петро");
        var poor = _lavka.Buy("Петро", true, "photo", null);
        Assert.False(poor.Ok);
        Assert.Equal($"Бракує {2000 - petro} 🏺", poor.Message);
        Assert.Equal(petro, Balance("Петро"));
        Assert.False(_store.Owns("Петро", "photo"));

        var perk = Views.Json(_lavka.View("гість Вася", false)).GetProperty("perks").GetProperty("photo");
        Assert.False(perk.GetProperty("owned").GetBoolean());
    }

    [Fact]
    public void The_photo_can_be_gifted_and_the_recipient_sets_it_at_once()
    {
        Give("Оля", 3000);
        var r = _lavka.Buy("Оля", true, "photo", "Петро");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("Подаровано Петрові: «Своя фотка» — назавжди", r.Message);
        Assert.True(_store.Owns("Петро", "photo"));
        Assert.False(_store.Owns("Оля", "photo"));

        Assert.True(_photos.Set("Петро", true, Jpeg()).Ok);
        Assert.Equal(LavkaPhotos.NotOwned, _photos.Set("Оля", true, Jpeg()).Message);   // подарувала — не собі
    }

    // =============================================================================================
    // Своя фотка: поставити
    // =============================================================================================

    [Fact]
    public void Owner_sets_a_photo_everyone_sees_it_and_the_file_is_named_by_hash_not_nick()
    {
        Owner("Оля");

        var r = _photos.Set("Оля", true, WebpLossy());

        Assert.True(r.Ok, r.Message);
        Assert.Equal(LavkaPhotos.Saved, r.Message);
        Assert.Matches("^/api/lavka/photo/[0-9a-f]{16}-[0-9a-f]{12}\\.webp$", r.Url);
        Assert.Equal(Clock.UtcNow + Lavka.PhotoGap, r.ReadyAt);
        var file = Assert.Single(Files());
        Assert.Equal(r.Url, LavkaPhotos.UrlPrefix + file);
        Assert.DoesNotContain("оля", file);
        Assert.Equal(WebpLossy(), File.ReadAllBytes(Path.Combine(_dir, file)));

        Assert.Equal(r.Url, _lavka.LookOf("Оля")!.Photo);
        var (nick, look) = Assert.Single(_wire.Shown);
        Assert.Equal("Оля", nick);
        Assert.Equal(r.Url, look!.Photo);
        // фото без жодної речі — теж вигляд: його бачать усі в /api/lavka/looks
        var olya = Views.Json(LavkaSetup.Looks(_lavka)).GetProperty("looks").GetProperty("Оля");
        Assert.Equal(r.Url, olya.GetProperty("photo").GetString());
        Assert.Equal(JsonValueKind.Null, olya.GetProperty("icon").ValueKind);
        // і в своїй вітрині
        Assert.Equal(r.Url, Views.Json(_lavka.View("Оля", true)).GetProperty("perks").GetProperty("photo").GetProperty("url").GetString());
    }

    [Fact]
    public void Photo_goes_with_the_frame_and_the_icon_in_one_look()
    {
        Give("Оля", 10000);
        Assert.True(_lavka.Buy("Оля", true, "gold", null).Ok);
        Assert.True(_lavka.Buy("Оля", true, "fox", null).Ok);
        Owner("Оля");
        var url = _photos.Set("Оля", true, Jpeg()).Url;

        Assert.Equal(new LavkaLook("🦊", "gold", null, null, null, url), _lavka.LookOf("Оля"));
        var e = Views.Json(LavkaSetup.Looks(_lavka)).GetProperty("looks").GetProperty("Оля");
        Assert.Equal(["icon", "frame", "color", "title", "bg", "photo"], e.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Without_a_photo_the_look_has_no_photo_field_at_all_like_before()
    {
        Give("Оля", 1000);
        Assert.True(_lavka.Buy("Оля", true, "fox", null).Ok);
        var e = Views.Json(LavkaSetup.Looks(_lavka)).GetProperty("looks").GetProperty("Оля");
        Assert.Equal(["icon", "frame", "color", "title", "bg"], e.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("photo", Views.Text(_lavka.LookOf("Оля")));
    }

    [Fact]
    public void Only_the_owner_account_may_set_a_photo()
    {
        Assert.Equal(LavkaPhotos.NotOwned, _photos.Set("Оля", true, Jpeg()).Message);
        Give("гість Вася", 5000);
        Assert.Equal(Lavka.NotAccount, _photos.Set("гість Вася", false, Jpeg()).Message);
        Owner("Оля");
        Assert.Equal(Lavka.NotAccount, _photos.Set("Оля", false, Jpeg()).Message);   // той самий нік, але не з акаунта
        Assert.Empty(Files());
        Assert.Empty(_wire.Shown);
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("svg")]
    [InlineData("html")]
    [InlineData("text")]
    [InlineData("bmp")]
    [InlineData("riff-avi")]
    [InlineData("half-png")]
    [InlineData("jpeg-without-frame")]
    [InlineData("png-without-size")]
    public void Only_jpeg_png_and_webp_by_magic_bytes(string kind)
    {
        Owner("Оля");
        byte[] body = kind switch
        {
            "gif" => [.. "GIF89a"u8.ToArray(), 1, 0, 1, 0, 0, 0, 0],
            "svg" => Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"),
            "html" => Encoding.UTF8.GetBytes("<!doctype html><script>alert(1)</script>"),
            "text" => Encoding.UTF8.GetBytes("це точно фото, чесно"),
            "bmp" => [.. "BM"u8.ToArray(), .. new byte[60]],
            "riff-avi" => [.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "AVI LIST"u8.ToArray(), .. new byte[30]],
            "half-png" => Png()[..20],
            "png-without-size" => Png(0, 256),
            _ => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0, 0, 0xFF, 0xD9],
        };

        var r = _photos.Set("Оля", true, body);

        Assert.False(r.Ok);
        Assert.Equal(LavkaPhotos.NotImage, r.Message);
        Assert.Empty(Files());
        Assert.Null(_lavka.LookOf("Оля"));
        Assert.Null(_lavka.ReadyAt("Оля", "photo"));                 // невдала спроба перерви не вмикає
    }

    [Fact]
    public void Empty_and_too_heavy_bodies_are_refused_but_the_limit_itself_passes()
    {
        Owner("Оля");
        Assert.Equal(LavkaPhotos.Empty, _photos.Set("Оля", true, []).Message);

        var heavy = Jpeg(pad: LavkaPhotos.MaxBytes);                 // на кілька десятків байтів більше за межу
        Assert.True(heavy.Length > LavkaPhotos.MaxBytes);
        Assert.Equal("Завелике фото: до 200 КБ", _photos.Set("Оля", true, heavy).Message);
        Assert.Empty(Files());

        var edge = Jpeg(pad: LavkaPhotos.MaxBytes - Jpeg().Length);
        Assert.Equal(LavkaPhotos.MaxBytes, edge.Length);
        Assert.True(_photos.Set("Оля", true, edge).Ok);
    }

    [Theory]
    [InlineData(1025, 256)]
    [InlineData(256, 1025)]
    [InlineData(20000, 20000)]
    public void A_tiny_file_with_a_giant_picture_inside_is_refused(int w, int h)
    {
        Owner("Оля");
        var r = _photos.Set("Оля", true, Png(w, h));
        Assert.False(r.Ok);
        Assert.Equal("Завелика картинка: до 1024×1024", r.Message);
        Assert.Empty(Files());
    }

    [Fact]
    public void New_photo_not_more_often_than_once_a_day_and_the_old_file_is_removed()
    {
        Owner("Оля");
        var first = _photos.Set("Оля", true, Jpeg(seed: 1));
        Assert.True(first.Ok);

        Clock.Advance(TimeSpan.FromHours(20));
        var soon = _photos.Set("Оля", true, Jpeg(pad: 10, seed: 2));
        Assert.False(soon.Ok);
        Assert.Equal("Нове фото — через 4 год", soon.Message);
        Assert.Equal(first.Url, soon.Url);                            // старе стоїть, як стояло
        Assert.Single(Files());

        Clock.Advance(TimeSpan.FromHours(4));
        var next = _photos.Set("Оля", true, Jpeg(pad: 10, seed: 2));
        Assert.True(next.Ok, next.Message);
        Assert.NotEqual(first.Url, next.Url);                         // нова версія — нова адреса
        Assert.Equal(next.Url, LavkaPhotos.UrlPrefix + Assert.Single(Files()));
        Assert.Equal(next.Url, _lavka.LookOf("Оля")!.Photo);
    }

    [Fact]
    public void The_daily_limit_survives_a_restart()
    {
        Owner("Оля");
        Assert.True(_photos.Set("Оля", true, Jpeg()).Ok);
        var lavka = new Lavka(new LavkaStore(_eco.Db), _eco.Economy, _eco.Store, _eco.Db, _eco.Presence, new NoAir(), new NoVoice(),
            _wire, Clock, NullLogger<Lavka>.Instance);
        var photos = new LavkaPhotos(new LavkaStore(_eco.Db), lavka, _wire, Clock, new LavkaPhotoDir(_dir), NullLogger<LavkaPhotos>.Instance);
        Assert.Equal("Нове фото — через 24 год", photos.Set("Оля", true, Png()).Message);
    }

    [Fact]
    public void Double_upload_at_once_sets_exactly_one()
    {
        Owner("Оля");
        var replies = new LavkaPhotoReply[4];
        Parallel.For(0, replies.Length, i => replies[i] = _photos.Set("Оля", true, Jpeg(pad: i, seed: (byte)i)));
        Assert.Single(replies, r => r.Ok);
        Assert.Single(Files());
    }

    // =============================================================================================
    // Прибрати своє фото
    // =============================================================================================

    [Fact]
    public void Owner_removes_the_photo_any_time_but_the_daily_limit_stays()
    {
        Give("Оля", 1000);
        Assert.True(_lavka.Buy("Оля", true, "fox", null).Ok);
        Owner("Оля");
        Assert.True(_photos.Set("Оля", true, Jpeg()).Ok);
        _wire.Shown.Clear();

        var r = _photos.Remove("Оля", true);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("Фото прибрано — знову значок", r.Message);
        Assert.Empty(Files());
        Assert.Equal(new LavkaLook("🦊", null, null, null, null), _lavka.LookOf("Оля"));   // знову значок
        Assert.Equal(("Оля", (LavkaLook?)new LavkaLook("🦊", null, null, null, null)), Assert.Single(_wire.Shown));
        Assert.True(_store.Owns("Оля", "photo"));
        // прибрати й одразу поставити інше — то вже обхід доби
        Assert.Equal("Нове фото — через 24 год", _photos.Set("Оля", true, Png()).Message);
        Assert.Equal("Фото й так нема", _photos.Remove("Оля", true).Message);
        Assert.Equal(Lavka.NotAccount, _photos.Remove("Оля", false).Message);
    }

    // =============================================================================================
    // Адмін
    // =============================================================================================

    [Fact]
    public void Admin_lists_every_photo_fresh_first()
    {
        Owner("Оля");
        Owner("Петро");
        var olya = _photos.Set("Оля", true, Jpeg());
        Clock.Advance(TimeSpan.FromMinutes(5));
        var petro = _photos.Set("Петро", true, Png());

        var (status, body) = Radio.Reply(LavkaSetup.AdminPhotos(As("Андрій", admin: true), _photos));

        Assert.Equal(200, status);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Петро", "Оля"], items.Select(i => i.GetProperty("nick").GetString()));
        Assert.Equal(["nick", "url", "at", "bytes"], items[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(petro.Url, items[0].GetProperty("url").GetString());
        Assert.Equal(olya.Url, items[1].GetProperty("url").GetString());
        Assert.Equal(Clock.UtcNow, items[0].GetProperty("at").GetDateTimeOffset());
        Assert.Equal(Png().Length, items[0].GetProperty("bytes").GetInt32());

        var (denied, why) = Radio.Reply(LavkaSetup.AdminPhotos(As("Оля"), _photos));
        Assert.Equal(400, denied);
        Assert.Equal("Це бачить лише розробник", why.GetProperty("message").GetString());
        Assert.False(why.TryGetProperty("items", out _));
    }

    [Fact]
    public void Admin_takes_a_photo_down_the_skill_stays_and_a_new_photo_can_go_up_at_once()
    {
        Owner("Оля");
        Assert.True(_photos.Set("Оля", true, Jpeg()).Ok);
        _wire.Shown.Clear();

        var (status, body) = Radio.Reply(LavkaSetup.TakeDown(As("Андрій", admin: true), new LavkaSetup.TakeDownRequest("оля"), _photos));

        Assert.Equal(200, status);
        Assert.Equal("Фото Олі знято", body.GetProperty("message").GetString());
        Assert.Empty(Files());
        Assert.Null(_lavka.LookOf("Оля"));
        Assert.Equal(("Оля", (LavkaLook?)null), Assert.Single(_wire.Shown));
        Assert.Equal([("Оля", "📷 Розробник зняв твоє фото — можна одразу поставити інше")], _wire.Toasts);
        Assert.True(_store.Owns("Оля", "photo"));                    // вміння лишається
        Assert.Null(_lavka.ReadyAt("Оля", "photo"));                  // і доби чекати не треба
        var again = _photos.Set("Оля", true, Png());
        Assert.True(again.Ok, again.Message);
        var item = Assert.Single(Radio.Reply(LavkaSetup.AdminPhotos(As("Андрій", admin: true), _photos)).Body.GetProperty("items").EnumerateArray());
        Assert.Equal(again.Url, item.GetProperty("url").GetString());
    }

    [Fact]
    public void Take_down_refusals()
    {
        Owner("Оля");
        Assert.True(_photos.Set("Оля", true, Jpeg()).Ok);

        var (status, body) = Radio.Reply(LavkaSetup.TakeDown(As("Петро"), new LavkaSetup.TakeDownRequest("Оля"), _photos));
        Assert.Equal(400, status);
        Assert.Equal("Це вміє лише розробник", body.GetProperty("message").GetString());
        Assert.Single(Files());

        Assert.Equal("У Петра фото нема", _photos.TakeDown("Петро").Message);
        Assert.Equal("В Андрія фото нема", _photos.TakeDown("@Андрій").Message);
        Assert.Equal("Чиє фото зняти?", _photos.TakeDown("  ").Message);
    }

    // =============================================================================================
    // HTTP: POST/DELETE /api/lavka/photo і віддача файла
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
        var (status, body) = Radio.Reply(await LavkaSetup.SetPhoto(Upload("Оля", WebpLossy(), "image/webp"), _photos));
        Assert.Equal(200, status);
        Assert.Equal(["ok", "message", "url", "readyAt"], body.EnumerateObject().Select(p => p.Name));
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.EndsWith(".webp", body.GetProperty("url").GetString());
        Assert.Equal(Clock.UtcNow + Lavka.PhotoGap, body.GetProperty("readyAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Upload_route_trusts_bytes_not_the_claimed_type()
    {
        Owner("Оля");
        // «я — JPEG», а всередині GIF: не пройде
        var (bad, why) = Radio.Reply(await LavkaSetup.SetPhoto(Upload("Оля", [.. "GIF89a"u8.ToArray(), 1, 0, 1, 0], "image/jpeg"), _photos));
        Assert.Equal(400, bad);
        Assert.Equal(LavkaPhotos.NotImage, why.GetProperty("message").GetString());
        // «я — JPEG», а всередині PNG: годиться, і лягає як PNG
        var (ok, body) = Radio.Reply(await LavkaSetup.SetPhoto(Upload("Оля", Png(), "image/jpeg"), _photos));
        Assert.Equal(200, ok);
        Assert.EndsWith(".png", body.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Upload_route_does_not_swallow_a_huge_body()
    {
        Owner("Оля");
        var huge = Jpeg(pad: 5 * 1024 * 1024);
        var c = Upload("Оля", huge, "image/jpeg");
        var (status, body) = Radio.Reply(await LavkaSetup.SetPhoto(c, _photos));
        Assert.Equal(400, status);
        Assert.Equal("Завелике фото: до 200 КБ", body.GetProperty("message").GetString());
        Assert.True(c.Request.Body.Position <= LavkaPhotos.MaxBytes + 1);   // решту навіть не читали
    }

    [Fact]
    public async Task Guest_upload_and_remove_are_refused()
    {
        var (status, body) = Radio.Reply(await LavkaSetup.SetPhoto(Upload("гість Вася", Jpeg(), "image/jpeg", account: false), _photos));
        Assert.Equal(400, status);
        Assert.Equal(Lavka.NotAccount, body.GetProperty("message").GetString());
        Assert.Equal(400, Radio.Reply(LavkaSetup.RemovePhoto(As("гість Вася", account: false), _photos)).Status);
    }

    [Fact]
    public void Remove_route_answers_with_the_limit()
    {
        Owner("Оля");
        Assert.True(_photos.Set("Оля", true, Jpeg()).Ok);
        var (status, body) = Radio.Reply(LavkaSetup.RemovePhoto(As("Оля"), _photos));
        Assert.Equal(200, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("url").ValueKind);
        Assert.Equal(Clock.UtcNow + Lavka.PhotoGap, body.GetProperty("readyAt").GetDateTimeOffset());
    }

    /// <summary>Виконати <see cref="LavkaSetup.PhotoFile"/> так, як це зробив би ASP.NET: код, заголовки, тіло.</summary>
    async Task<(int Status, IHeaderDictionary Headers, byte[] Body)> Get(string file)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await LavkaSetup.PhotoFile(file, _photos, ctx).ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, ctx.Response.Headers, body.ToArray());
    }

    [Theory]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("png", "image/png")]
    [InlineData("webp", "image/webp")]
    public async Task The_file_is_served_by_its_magic_bytes_nosniff_and_cached_for_a_year(string kind, string mime)
    {
        Owner("Оля");
        var bytes = kind switch { "jpeg" => Jpeg(), "png" => Png(), _ => WebpLossy() };
        var url = _photos.Set("Оля", true, bytes).Url!;

        var (status, headers, body) = await Get(url[LavkaPhotos.UrlPrefix.Length..]);

        Assert.Equal(200, status);
        Assert.Equal(mime, headers.ContentType.ToString());
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Equal("public, max-age=31536000, immutable", headers.CacheControl.ToString());
        Assert.Equal(bytes, body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef-0123456789ab.jpg")]            // схоже, але такого нема
    [InlineData("0123456789ABCDEF-0123456789ab.jpg")]            // великими
    [InlineData("0123456789abcdef-0123456789ab.gif")]
    [InlineData("0123456789abcdef-0123456789ab.jpg.tmp")]
    [InlineData("..%2F..%2Fdata%2Fhlechyky.db")]
    [InlineData("../../data/hlechyky.db")]
    [InlineData("оля.jpg")]
    public async Task Anything_but_a_live_photo_name_is_404(string file)
    {
        Owner("Оля");
        Assert.True(_photos.Set("Оля", true, Jpeg()).Ok);
        Assert.Equal(404, (await Get(file)).Status);
    }

    [Fact]
    public async Task Replaced_and_removed_photos_are_gone_from_the_route()
    {
        Owner("Оля");
        var first = _photos.Set("Оля", true, Jpeg()).Url!;
        Clock.Advance(Lavka.PhotoGap);
        var second = _photos.Set("Оля", true, Png()).Url!;
        Assert.Equal(404, (await Get(first[LavkaPhotos.UrlPrefix.Length..])).Status);
        Assert.Equal(200, (await Get(second[LavkaPhotos.UrlPrefix.Length..])).Status);
        Assert.True(_photos.Remove("Оля", true).Ok);
        Assert.Equal(404, (await Get(second[LavkaPhotos.UrlPrefix.Length..])).Status);
    }

    // =============================================================================================
    // Магічні байти й розміри
    // =============================================================================================

    [Fact]
    public void Sniff_reads_type_and_size_from_the_header()
    {
        Assert.Equal(new LavkaImage("image/jpeg", "jpg", 640, 480), LavkaImage.Sniff(Jpeg(640, 480)));
        Assert.Equal(new LavkaImage("image/png", "png", 300, 200), LavkaImage.Sniff(Png(300, 200)));
        Assert.Equal(new LavkaImage("image/webp", "webp", 256, 250), LavkaImage.Sniff(WebpLossy(256, 250)));
        Assert.Equal(new LavkaImage("image/webp", "webp", 1000, 3), LavkaImage.Sniff(WebpLossless(1000, 3)));
        Assert.Equal(new LavkaImage("image/webp", "webp", 70000, 2), LavkaImage.Sniff(WebpExtended(70000, 2)));
        Assert.Null(LavkaImage.Sniff([]));
        Assert.Null(LavkaImage.Sniff([0xFF, 0xD8, 0xFF]));
        Assert.Null(LavkaImage.Sniff(Jpeg()[..20]));                  // обірвався до кадру
    }

    [Fact]
    public void Sniff_walks_past_a_big_exif_block_to_the_frame()
    {
        // Фото з телефона: спершу APP1 з EXIF на десятки кілобайтів, лише потім кадр
        var exif = new byte[40000];
        byte[] body = [0xFF, 0xD8, 0xFF, 0xE1, (byte)((exif.Length + 2) >> 8), (byte)(exif.Length + 2), .. exif, .. Jpeg(512, 384)[2..]];
        Assert.Equal(new LavkaImage("image/jpeg", "jpg", 512, 384), LavkaImage.Sniff(body));
    }

    // =============================================================================================
    // Стара база
    // =============================================================================================

    [Fact]
    public void Old_database_without_the_photo_table_opens_and_the_migration_is_idempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), "hlechyky-old-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // база з проду до фоток: таблиці Лавки вже є, lavka_photo — ще нема
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE lavka_owned(nick_key TEXT NOT NULL, item TEXT NOT NULL, source TEXT NOT NULL, from_nick TEXT,
                        price INTEGER NOT NULL, at TEXT NOT NULL, PRIMARY KEY(nick_key, item));
                    CREATE TABLE lavka_worn(nick_key TEXT NOT NULL, nick TEXT NOT NULL, slot TEXT NOT NULL, item TEXT NOT NULL, PRIMARY KEY(nick_key, slot));
                    CREATE TABLE lavka_perk(nick_key TEXT NOT NULL, perk TEXT NOT NULL, used_at TEXT NOT NULL, PRIMARY KEY(nick_key, perk));
                    INSERT INTO lavka_owned VALUES('оля', 'fox', 'buy', NULL, 250, '2026-09-26T10:00:00.0000000+00:00');
                    INSERT INTO lavka_worn VALUES('оля', 'Оля', 'icon', 'fox');
                    """;
                cmd.ExecuteNonQuery();
            }
            var db = new Db(path);
            var store = new LavkaStore(db);
            _ = new LavkaStore(db);                                      // удруге — нічого не ламається
            Assert.Null(store.Photo("Оля"));
            Assert.Empty(store.AllPhotos());
            Assert.Equal(["icon"], store.WornBy("Оля").Keys);
            Assert.Null(store.DropPhoto("Оля"));
        }
        finally
        {
            // лише свій пул (як TempDb): ClearAllPools закривав би з'єднання паралельних тестів
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
                SqliteConnection.ClearPool(c);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(path + suffix); } catch (IOException) { /* хай лежить у temp */ }
        }
    }
}
