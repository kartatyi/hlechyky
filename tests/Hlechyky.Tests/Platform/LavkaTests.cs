using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Games;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// «Лавка Дядька Глека» (контракт _tools/lavka-contract.md): каталог, купівля й подарунки за черепки, вдягання, сезонні
/// речі, титули за ачівки, вміння з перервою (присвята в ефір і феєрверк), вигляд для всіх, записи гаманця й HTTP-форми.
/// Сервіс збирається на тимчасовій базі разом зі справжньою економікою (EconomyRig), а ефір, голос Глека й розсилка
/// підмінені заглушками — перевіряти треба правила, а не liquidsoap, edge-tts і SignalR.
/// </summary>
public sealed class LavkaTests : IDisposable
{
    // =============================================================================================
    // Заглушки
    // =============================================================================================

    /// <summary>Черга ефіру в пам'яті: тест кладе треки з потрібним станом, присвята стає туди, куди її поставили.</summary>
    sealed class FakeAir : ILavkaAir
    {
        int _n;
        public List<LavkaQueued> Items { get; } = [];
        /// <summary>Ефір відмовив узяти голосове.</summary>
        public bool Refuse { get; set; }

        public LavkaQueued Song(string nick, string title, string status = "ready", string artist = "Гурт")
        {
            var n = ++_n;
            var q = new LavkaQueued("item" + n, new TrackInfo("yt-" + n, title, artist, 180, null, "https://music.youtube.com/watch?v=" + n, null),
                nick, status);
            Items.Add(q);
            return q;
        }

        /// <summary>Трек пішов у liquidsoap — так його бачить рушій ефіру.</summary>
        public void Dispatch(LavkaQueued q) => Items[Items.FindIndex(x => x.ItemId == q.ItemId)] = q with { Status = "dispatched" };

        public IReadOnlyList<LavkaQueued> Queue() => [.. Items];

        public (bool Ok, string Message) AddVoiceBefore(TrackInfo track, string filePath, string nick, string beforeItemId)
        {
            if (Refuse) return (false, "Ефір не бере");
            var voice = new LavkaQueued("voice" + ++_n, track, nick, "ready");
            var at = Items.FindIndex(x => x.ItemId == beforeItemId);
            if (at < 0) Items.Add(voice);
            else Items.Insert(at, voice);
            return (true, "Присвята в черзі");
        }
    }

    /// <summary>Голос, який «уже озвучив»; <see cref="Silent"/> — TTS вимкнений чи впав.</summary>
    sealed class FakeVoice : ILavkaVoice
    {
        int _n;
        public bool Silent { get; set; }
        public List<string> Said { get; } = [];
        /// <summary>Що стається в ефірі, поки Глек озвучує.</summary>
        public Action? During { get; set; }

        public Task<LavkaVoiceClip?> SpeakAsync(string text, string title, string artist, CancellationToken ct)
        {
            Said.Add(text);
            During?.Invoke();
            if (Silent) return Task.FromResult<LavkaVoiceClip?>(null);
            var id = "voice-" + (++_n).ToString("D12");
            return Task.FromResult<LavkaVoiceClip?>(new LavkaVoiceClip(
                new TrackInfo(id, title, artist, 4, null, $"/api/voice/{id}.mp3", null), id + ".mp3"));
        }
    }

    /// <summary>Розсилка, яка лише запам'ятовує: рядки балачок — одразу як JSON, так, як їх побачить браузер.</summary>
    sealed class FakeWire : ILavkaWire
    {
        readonly object _lock = new();
        public List<(string Nick, LavkaLook? Look)> Shown { get; } = [];
        public List<string> Launched { get; } = [];
        public List<JsonElement> Lines { get; } = [];
        public List<(string Nick, string Text)> Toasts { get; } = [];

        void ILavkaWire.Look(string nick, LavkaLook? look) { lock (_lock) Shown.Add((nick, look)); }
        void ILavkaWire.Fireworks(string nick) { lock (_lock) Launched.Add(nick); }
        void ILavkaWire.Chat(object line) { lock (_lock) Lines.Add(Views.Json(line)); }
        void ILavkaWire.Toast(string nick, string text) { lock (_lock) Toasts.Add((nick, text)); }
    }

    sealed class LavkaRig : IDisposable
    {
        public EconomyRig Eco { get; } = new();
        public FakeAir Air { get; } = new();
        public FakeVoice Voice { get; } = new();
        public FakeWire Wire { get; } = new();
        public Lavka Lavka { get; }

        public Db Db => Eco.Db;
        public FakeClock Clock => Eco.Clock;

        public LavkaRig()
        {
            Assert.True(Db.AddAccount("Оля", "", "salt"));
            Assert.True(Db.AddAccount("Петро", "", "salt"));
            Assert.True(Db.AddAccount("Андрій", "", "salt"));
            Lavka = Fresh();
        }

        /// <summary>Лавка на тій самій базі, але з порожньою пам'яттю — як після перезапуску сервера.</summary>
        public Lavka Fresh() => new(new LavkaStore(Db), Eco.Economy, Eco.Store, Db, Eco.Presence, Air, Voice, Wire, Clock,
            NullLogger<Lavka>.Instance);

        public int Balance(string nick) => Eco.Economy.Balance(nick);

        /// <summary>Черепки на гаманець. Від сотні спрацює ачівка «Сотня» (+10), тож баланси тести рахують від «до».</summary>
        public void Give(string nick, int shards) => Eco.Economy.Grant(nick, shards, "listen", "test:" + Guid.NewGuid().ToString("N"));

        public LavkaReply Buy(string nick, string item, string? to = null) => Lavka.Buy(nick, account: true, item, to);

        public LavkaReply Wear(string nick, string slot, string? item) => Lavka.Wear(nick, account: true, slot, item);

        public LavkaReply Dedicate(string nick, string? to, string? phrase = "luck") =>
            Lavka.DedicateAsync(nick, account: true, to, phrase).GetAwaiter().GetResult();

        public bool Owns(string nick, string item) => new LavkaStore(Db).Owns(nick, item);

        /// <summary>Рядки Балачок, що лягли в базу, — найсвіжіші в кінці.</summary>
        public List<ChatMessage> Chat() => Db.RecentChat(50, 0);

        public void Dispose() => Eco.Dispose();
    }

    readonly LavkaRig _r = new();

    public void Dispose() => _r.Dispose();

    /// <summary>Запит від імені ніка, як його бачать маршрути: акаунт — з рядком у c.Items, як його кладе UseHlechykyAuth.</summary>
    static HttpContext As(string nick, bool account = true)
    {
        var c = Radio.As(nick);
        if (account) c.Items["account"] = new Account(nick, "", "salt", "member");
        return c;
    }

    // =============================================================================================
    // Каталог
    // =============================================================================================

    [Fact]
    public void Catalog_has_every_group_from_the_contract_and_unique_ids()
    {
        var all = LavkaCatalog.All;
        Assert.Equal(all.Count, all.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(45, all.Count(i => i.Kind == LavkaKind.Icon));        // 24 звичайних + 21 сезонний (календар на весь рік з 28.09)
        Assert.Equal(4, all.Count(i => i.Kind == LavkaKind.Frame));
        Assert.Equal(17, all.Count(i => i.Kind == LavkaKind.Color));       // 16 кольорів і веселка
        Assert.Equal(13, all.Count(i => i.Kind == LavkaKind.Title));       // 8 купованих і 5 за ачівки
        Assert.Equal(5, all.Count(i => i.Kind == LavkaKind.Bg));
        Assert.Equal(["dedication", "fireworks", "photo"], all.Where(i => i.Kind == LavkaKind.Perk).Select(i => i.Id));
        // id латиницею — без пробілів і великих літер
        Assert.All(all, i => Assert.Matches("^[a-z]+$", i.Id));
    }

    [Theory]
    [InlineData("pot", 150, 1)]
    [InlineData("ball", 150, 1)]
    [InlineData("fox", 250, 2)]
    [InlineData("alien", 250, 2)]
    [InlineData("violin", 300, 3)]
    [InlineData("tree", 300, 3)]
    [InlineData("copper", 400, 0)]
    [InlineData("silver", 1000, 0)]
    [InlineData("gold", 2500, 0)]
    [InlineData("alive", 5000, 0)]
    [InlineData("sky", 250, 0)]
    [InlineData("rainbow", 1500, 0)]
    [InlineData("nightowl", 500, 0)]
    [InlineData("meloman", 0, 0)]
    [InlineData("stars", 800, 0)]
    [InlineData("vyshyvanka", 1200, 0)]
    [InlineData("trypillia", 1500, 0)]
    [InlineData("dedication", 1500, 0)]
    [InlineData("fireworks", 600, 0)]
    public void Prices_and_tiers_follow_the_contract(string id, int price, int tier)
    {
        var item = LavkaCatalog.Get(id);
        Assert.NotNull(item);
        Assert.Equal(price, item!.Price);
        Assert.Equal(tier, item.Tier);
    }

    [Fact]
    public void Art_is_what_the_browser_draws()
    {
        Assert.Equal("🦊", LavkaCatalog.Get("fox")!.Art);
        Assert.Equal("🇺🇦", LavkaCatalog.Get("flag")!.Art);
        Assert.Equal("gold", LavkaCatalog.Get("gold")!.Art);
        Assert.Equal(212, LavkaCatalog.Get("cornflower")!.Art);
        Assert.Equal(0, LavkaCatalog.Get("ember")!.Art);
        Assert.Equal("rainbow", LavkaCatalog.Get("rainbow")!.Art);
        Assert.Equal("Нічна сова", LavkaCatalog.Get("nightowl")!.Art);
        Assert.Equal("petrykivka", LavkaCatalog.Get("petrykivka")!.Art);
        // id без регістру й країв: браузер пришле як є
        Assert.Same(LavkaCatalog.Get("fox"), LavkaCatalog.Get(" FOX "));
        Assert.Null(LavkaCatalog.Get("owl-of-doom"));
        Assert.Null(LavkaCatalog.Get(null));
    }

    [Theory]
    [InlineData("12-15", "01-15", "12-14", false)]
    [InlineData("12-15", "01-15", "12-15", true)]
    [InlineData("12-15", "01-15", "12-31", true)]
    [InlineData("12-15", "01-15", "01-01", true)]
    [InlineData("12-15", "01-15", "01-15", true)]
    [InlineData("12-15", "01-15", "01-16", false)]
    [InlineData("12-15", "01-15", "06-20", false)]
    [InlineData("08-18", "08-31", "08-17", false)]
    [InlineData("08-18", "08-31", "08-18", true)]
    [InlineData("08-18", "08-31", "08-31", true)]
    [InlineData("08-18", "08-31", "09-01", false)]
    public void Season_bounds_are_inclusive_and_cross_new_year(string from, string to, string day, bool open)
    {
        Assert.Equal(open, new LavkaSeason(from, to).Open(day));
    }

    [Fact]
    public void Every_day_of_the_year_has_a_seasonal_icon_on_the_shelf()
    {
        var seasonal = LavkaCatalog.All.Where(i => i.Kind == LavkaKind.Icon && i.Season is not null).ToList();
        // високосний рік — щоб перевірити й 29 лютого
        for (var d = new DateOnly(2028, 1, 1); d.Year == 2028; d = d.AddDays(1))
        {
            var md = d.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(seasonal.Any(i => i.Season!.Open(md)), $"{md}: жодного сезонного значка");
        }
        // межі — справжні дні «MM-dd»
        Assert.All(seasonal, i =>
        {
            Assert.True(DateOnly.TryParseExact("2028-" + i.Season!.From, "yyyy-MM-dd", out _), i.Id);
            Assert.True(DateOnly.TryParseExact("2028-" + i.Season!.To, "yyyy-MM-dd", out _), i.Id);
        });
    }

    // =============================================================================================
    // Вітрина: GET /api/lavka
    // =============================================================================================

    [Fact]
    public void Guest_sees_the_shop_but_nothing_in_it_is_theirs()
    {
        _r.Give("гість Вася", 40);
        var e = Views.Json(LavkaSetup.Shop(As("гість Вася", account: false), _r.Lavka));

        Assert.False(e.GetProperty("account").GetBoolean());
        Assert.Equal(40, e.GetProperty("balance").GetInt32());
        var items = e.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(LavkaCatalog.All.Count, items.Count);
        Assert.All(items, i => Assert.False(i.GetProperty("owned").GetBoolean()));
        Assert.All(items, i => Assert.False(i.GetProperty("worn").GetBoolean()));
        Assert.All(e.GetProperty("worn").EnumerateObject(), p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));
        Assert.False(e.GetProperty("perks").GetProperty("fireworks").GetProperty("owned").GetBoolean());
        Assert.False(e.GetProperty("perks").GetProperty("dedication").GetProperty("owned").GetBoolean());
    }

    [Fact]
    public void Shop_has_the_exact_shape_of_the_contract()
    {
        _r.Give("Оля", 5000);
        Assert.True(_r.Buy("Оля", "fox").Ok);
        Assert.True(_r.Buy("Оля", "sky").Ok);
        Assert.True(_r.Buy("Оля", "fireworks").Ok);
        var e = Views.Json(LavkaSetup.Shop(As("Оля"), _r.Lavka));

        Assert.Equal(["account", "balance", "items", "worn", "perks", "phrases"], e.EnumerateObject().Select(p => p.Name));
        Assert.True(e.GetProperty("account").GetBoolean());
        Assert.Equal(_r.Balance("Оля"), e.GetProperty("balance").GetInt32());

        var fox = e.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "fox");
        Assert.Equal(["id", "kind", "title", "price", "art", "tier", "season", "earned", "owned", "worn"], fox.EnumerateObject().Select(p => p.Name));
        Assert.Equal("icon", fox.GetProperty("kind").GetString());
        Assert.Equal("Лис", fox.GetProperty("title").GetString());
        Assert.Equal(250, fox.GetProperty("price").GetInt32());
        Assert.Equal("🦊", fox.GetProperty("art").GetString());
        Assert.Equal(2, fox.GetProperty("tier").GetInt32());
        Assert.Equal(JsonValueKind.Null, fox.GetProperty("season").ValueKind);
        Assert.Equal(JsonValueKind.Null, fox.GetProperty("earned").ValueKind);
        Assert.True(fox.GetProperty("owned").GetBoolean());
        Assert.True(fox.GetProperty("worn").GetBoolean());

        var sky = e.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "sky");
        Assert.Equal(195, sky.GetProperty("art").GetInt32());
        Assert.Equal(0, sky.GetProperty("tier").GetInt32());

        var worn = e.GetProperty("worn");
        Assert.Equal(["icon", "frame", "color", "title", "bg"], worn.EnumerateObject().Select(p => p.Name));
        Assert.Equal("fox", worn.GetProperty("icon").GetString());
        Assert.Equal("sky", worn.GetProperty("color").GetString());
        Assert.Equal(JsonValueKind.Null, worn.GetProperty("frame").ValueKind);

        var perks = e.GetProperty("perks");
        Assert.True(perks.GetProperty("fireworks").GetProperty("owned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, perks.GetProperty("fireworks").GetProperty("readyAt").ValueKind);
        Assert.False(perks.GetProperty("dedication").GetProperty("owned").GetBoolean());

        var phrases = e.GetProperty("phrases").EnumerateArray().ToList();
        Assert.Equal(["love", "luck", "miss", "just", "night", "thanks"], phrases.Select(p => p.GetProperty("key").GetString()));
        Assert.Equal("з любов'ю", phrases[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Seasonal_item_says_its_season_and_whether_it_is_open_by_kyiv_date()
    {
        _r.Clock.UtcNow = new DateTimeOffset(2026, 12, 14, 21, 30, 0, TimeSpan.Zero);   // 23:30 14.12 за Києвом
        var tree = Tree(Views.Json(_r.Lavka.View("Оля", account: true)));
        Assert.Equal("12-15", tree.GetProperty("from").GetString());
        Assert.Equal("01-15", tree.GetProperty("to").GetString());
        Assert.False(tree.GetProperty("open").GetBoolean());

        _r.Clock.UtcNow = new DateTimeOffset(2026, 12, 14, 22, 10, 0, TimeSpan.Zero);   // 00:10 15.12 за Києвом — уже сезон
        Assert.True(Tree(Views.Json(_r.Lavka.View("Оля", account: true))).GetProperty("open").GetBoolean());

        static JsonElement Tree(JsonElement e) =>
            e.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "tree").GetProperty("season");
    }

    // =============================================================================================
    // Купівля собі
    // =============================================================================================

    [Fact]
    public void Buying_charges_the_price_keeps_it_forever_and_wears_it_at_once()
    {
        _r.Give("Оля", 1000);
        var before = _r.Balance("Оля");

        var r = _r.Buy("Оля", "fox");

        Assert.True(r.Ok, r.Message);
        Assert.Equal("Значок «Лис» 🦊 тепер твій назавжди — уже на аватарці", r.Message);
        Assert.Equal(before - 250, r.Balance);
        Assert.Equal(before - 250, _r.Balance("Оля"));
        Assert.True(_r.Owns("оля", "fox"));                       // нік без регістру
        Assert.Equal(new LavkaLook("🦊", null, null, null, null), _r.Lavka.LookOf("Оля"));
        // усім — новий вигляд
        var (nick, look) = Assert.Single(_r.Wire.Shown);
        Assert.Equal("Оля", nick);
        Assert.Equal("🦊", look!.Icon);
        // рядків у балачках і тостів купівля собі не робить
        Assert.Empty(_r.Wire.Lines);
        Assert.Empty(_r.Wire.Toasts);
    }

    [Fact]
    public void Buying_into_a_taken_slot_puts_the_new_thing_on()
    {
        _r.Give("Оля", 1000);
        Assert.True(_r.Buy("Оля", "fox").Ok);
        Assert.True(_r.Buy("Оля", "cat").Ok);

        Assert.Equal("🐈", _r.Lavka.LookOf("Оля")!.Icon);
        Assert.True(_r.Owns("Оля", "fox"));                       // лис нікуди не подівся — лежить у шафі
    }

    [Fact]
    public void Buying_a_perk_wears_nothing()
    {
        _r.Give("Оля", 2000);
        var r = _r.Buy("Оля", "dedication");

        Assert.True(r.Ok, r.Message);
        Assert.Contains("Присвята в ефір", r.Message);
        Assert.Null(_r.Lavka.LookOf("Оля"));
        Assert.True(_r.Owns("Оля", "dedication"));
    }

    [Fact]
    public void Not_enough_shards_says_how_many_are_missing_and_charges_nothing()
    {
        _r.Give("Оля", 90);

        var r = _r.Buy("Оля", "fox");

        Assert.False(r.Ok);
        Assert.Equal("Бракує 160 🏺", r.Message);
        Assert.Equal(90, r.Balance);
        Assert.Equal(90, _r.Balance("Оля"));
        Assert.False(_r.Owns("Оля", "fox"));
        Assert.Empty(_r.Wire.Shown);
    }

    [Fact]
    public void What_is_already_yours_is_not_sold_twice()
    {
        _r.Give("Оля", 1000);
        Assert.True(_r.Buy("Оля", "fox").Ok);
        var paid = _r.Balance("Оля");

        var again = _r.Buy("Оля", "FOX");

        Assert.False(again.Ok);
        Assert.Equal(Lavka.Mine, again.Message);
        Assert.Equal(paid, _r.Balance("Оля"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("owl-of-doom")]
    public void Unknown_item_is_refused(string? item)
    {
        _r.Give("Оля", 1000);
        var r = _r.Lavka.Buy("Оля", true, item, null);
        Assert.False(r.Ok);
        Assert.Equal(Lavka.NoItem, r.Message);
    }

    // =============================================================================================
    // Подарунки
    // =============================================================================================

    [Fact]
    public void Gift_goes_to_an_account_is_worn_into_an_empty_slot_and_everyone_hears_about_it()
    {
        _r.Give("Оля", 2000);
        var before = _r.Balance("Оля");

        var r = _r.Buy("Оля", "silver", to: "петро");            // нік без регістру — той самий акаунт

        Assert.True(r.Ok, r.Message);
        Assert.Equal("Подаровано Петрові: «Срібна рамка» — назавжди", r.Message);
        Assert.Equal(before - 1000, _r.Balance("Оля"));
        Assert.True(_r.Owns("Петро", "silver"));
        Assert.False(_r.Owns("Оля", "silver"));
        Assert.Equal("silver", _r.Lavka.LookOf("Петро")!.Frame);   // слот був порожній — рамка вже на ньому

        // рядок у балачках — у базі й усім, з полем to
        var saved = Assert.Single(_r.Chat(), m => m.Kind == "gift");
        Assert.Equal("Оля", saved.Nick);
        Assert.Equal("дарує Петрові «Срібна рамка»", saved.Text);
        var line = Assert.Single(_r.Wire.Lines);
        Assert.Equal("gift", line.GetProperty("kind").GetString());
        Assert.Equal(saved.Id, line.GetProperty("id").GetInt64());
        Assert.Equal("Оля", line.GetProperty("nick").GetString());
        Assert.Equal("дарує Петрові «Срібна рамка»", line.GetProperty("text").GetString());
        Assert.Equal("Петро", line.GetProperty("to").GetString());
        // тому, кому подарували, — тост на його з'єднання, і всім — його новий вигляд
        Assert.Equal([("Петро", "🎁 Оля дарує тобі «Срібна рамка»")], _r.Wire.Toasts);
        Assert.Equal("Петро", Assert.Single(_r.Wire.Shown).Nick);
    }

    [Fact]
    public void Gift_does_not_take_off_what_the_recipient_already_wears()
    {
        _r.Give("Петро", 1000);
        Assert.True(_r.Buy("Петро", "copper").Ok);
        _r.Give("Оля", 2000);

        Assert.True(_r.Buy("Оля", "silver", to: "Петро").Ok);

        Assert.Equal("copper", _r.Lavka.LookOf("Петро")!.Frame);
        Assert.True(_r.Owns("Петро", "silver"));                   // у шафі — вдягне, коли схоче
        Assert.True(_r.Wear("Петро", "frame", "silver").Ok);
    }

    [Fact]
    public void Gift_of_an_icon_names_the_thing_inside_the_line()
    {
        _r.Give("Оля", 1000);
        Assert.True(_r.Buy("Оля", "fox", to: "Андрій").Ok);

        Assert.Equal("дарує Андрієві значок «Лис» 🦊", Assert.Single(_r.Chat(), m => m.Kind == "gift").Text);
        Assert.Equal("🎁 Оля дарує тобі значок «Лис» 🦊", Assert.Single(_r.Wire.Toasts).Text);
    }

    [Fact]
    public void Gift_refusals()
    {
        _r.Give("Оля", 5000);
        var before = _r.Balance("Оля");

        var guest = _r.Buy("Оля", "fox", to: "гість Вася");
        Assert.False(guest.Ok);
        Assert.Contains("не акаунт", guest.Message);

        var nobody = _r.Buy("Оля", "fox", to: "Хтосьтам");
        Assert.False(nobody.Ok);
        Assert.Contains("не акаунт", nobody.Message);

        var self = _r.Buy("Оля", "fox", to: "@оля");
        Assert.False(self.Ok);
        Assert.Equal(Lavka.SelfGift, self.Message);

        _r.Give("Петро", 1000);
        Assert.True(_r.Buy("Петро", "fox").Ok);
        var has = _r.Buy("Оля", "fox", to: "Петро");
        Assert.False(has.Ok);
        Assert.Equal("У Петра це вже є", has.Message);

        // у того, хто дарує, мало черепків — так само, як на купівлю собі
        Assert.Equal("Бракує 250 🏺", _r.Buy("Андрій", "fox", to: "Оля").Message);
        Assert.False(_r.Owns("Оля", "fox"));
        Assert.Equal(before, _r.Balance("Оля"));
        Assert.Empty(_r.Wire.Toasts);
    }

    [Fact]
    public void Already_has_it_reads_well_before_a_vowel()
    {
        _r.Give("Оля", 1000);
        Assert.True(_r.Buy("Оля", "fox").Ok);
        _r.Give("Петро", 1000);

        Assert.Equal("В Олі це вже є", _r.Buy("Петро", "fox", to: "Оля").Message);
    }

    // =============================================================================================
    // Вдягання
    // =============================================================================================

    [Fact]
    public void Wear_what_is_yours_and_take_it_off()
    {
        _r.Give("Оля", 2000);
        Assert.True(_r.Buy("Оля", "fox").Ok);
        Assert.True(_r.Buy("Оля", "cat").Ok);
        _r.Wire.Shown.Clear();

        var on = _r.Wear("Оля", "icon", "fox");
        Assert.True(on.Ok, on.Message);
        Assert.Equal("🦊", _r.Lavka.LookOf("Оля")!.Icon);
        Assert.Equal("🦊", Assert.Single(_r.Wire.Shown).Look!.Icon);

        var off = _r.Wear("Оля", "icon", null);
        Assert.True(off.Ok, off.Message);
        Assert.Null(_r.Lavka.LookOf("Оля"));
        Assert.Equal(("Оля", (LavkaLook?)null), _r.Wire.Shown[^1]);   // зняла все — вигляду нема, і всі про це знають
        Assert.True(_r.Owns("Оля", "fox"));                             // знята річ лишається своєю
    }

    [Fact]
    public void Cannot_wear_what_is_not_yours_or_into_a_wrong_slot()
    {
        _r.Give("Оля", 2000);
        Assert.True(_r.Buy("Оля", "fox").Ok);
        Assert.True(_r.Buy("Оля", "fireworks").Ok);

        Assert.Equal(Lavka.NotOwned, _r.Wear("Оля", "icon", "cat").Message);
        Assert.Equal(Lavka.WrongSlot, _r.Wear("Оля", "frame", "fox").Message);
        Assert.Equal(Lavka.NoSlot, _r.Wear("Оля", "perk", "fireworks").Message);   // вміння не вдягають
        Assert.Equal(Lavka.NoSlot, _r.Wear("Оля", "hat", "fox").Message);
        Assert.Equal(Lavka.NoItem, _r.Wear("Оля", "icon", "owl-of-doom").Message);
        Assert.Equal("🦊", _r.Lavka.LookOf("Оля")!.Icon);
    }

    [Fact]
    public void Earned_title_is_in_the_closet_only_with_its_achievement_and_never_for_sale()
    {
        _r.Give("Оля", 1000);

        var buy = _r.Buy("Оля", "meloman");
        Assert.False(buy.Ok);
        Assert.Equal("Цей титул не купується — його дає ачівка «Меломан»", buy.Message);
        Assert.False(_r.Buy("Оля", "meloman", to: "Петро").Ok);
        Assert.Equal("Цей титул дає ачівка «Меломан»", _r.Wear("Оля", "title", "meloman").Message);
        Assert.False(Earned(_r.Lavka.View("Оля", true)).GetProperty("owned").GetBoolean());

        _r.Eco.Store.Unlock("оля", "Оля", "listener-100h", _r.Clock.UtcNow);

        var view = Earned(_r.Lavka.View("Оля", true));
        Assert.True(view.GetProperty("owned").GetBoolean());
        Assert.Equal(0, view.GetProperty("price").GetInt32());
        Assert.Equal("listener-100h", view.GetProperty("earned").GetProperty("ach").GetString());
        Assert.Equal("Меломан", view.GetProperty("earned").GetProperty("achTitle").GetString());
        Assert.True(_r.Wear("Оля", "title", "meloman").Ok);
        Assert.Equal("Меломан", _r.Lavka.LookOf("Оля")!.Title);
        Assert.False(_r.Buy("Оля", "meloman").Ok);                   // і тепер не продається — він і так твій

        static JsonElement Earned(object view) =>
            Views.Json(view).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "meloman");
    }

    // =============================================================================================
    // Сезон
    // =============================================================================================

    [Fact]
    public void Out_of_season_it_is_not_sold_but_what_you_bought_stays_forever()
    {
        _r.Give("Оля", 1000);
        var before = _r.Balance("Оля");

        var early = _r.Buy("Оля", "tree");                                // 10 вересня — до ялинки ще далеко
        Assert.False(early.Ok);
        Assert.Equal("Повернеться 15.12", early.Message);
        Assert.Equal(before, _r.Balance("Оля"));

        _r.Clock.UtcNow = new DateTimeOffset(2026, 12, 14, 22, 10, 0, TimeSpan.Zero);   // 00:10 15.12 за Києвом
        Assert.True(_r.Buy("Оля", "tree").Ok);

        _r.Clock.UtcNow = new DateTimeOffset(2027, 2, 1, 12, 0, 0, TimeSpan.Zero);      // сезон минув
        Assert.Equal("🎄", _r.Lavka.LookOf("Оля")!.Icon);
        Assert.Equal(Lavka.Mine, _r.Buy("Оля", "tree").Message);
        Assert.True(_r.Wear("Оля", "icon", null).Ok);
        Assert.True(_r.Wear("Оля", "icon", "tree").Ok);                   // вдягнути своє можна й поза сезоном
        Assert.Equal("Повернеться 15.12", _r.Buy("Петро", "tree").Message);
    }

    [Fact]
    public void Season_turns_at_kyiv_midnight_and_crosses_new_year()
    {
        _r.Give("Оля", 2000);
        _r.Clock.UtcNow = new DateTimeOffset(2027, 1, 15, 21, 59, 0, TimeSpan.Zero);   // 23:59 15.01 за Києвом — ще можна
        Assert.True(_r.Buy("Оля", "tree").Ok);

        _r.Give("Петро", 2000);
        _r.Clock.UtcNow = new DateTimeOffset(2027, 1, 15, 22, 0, 0, TimeSpan.Zero);    // 00:00 16.01 — уже ні
        Assert.Equal("Повернеться 15.12", _r.Buy("Петро", "tree").Message);

        _r.Clock.UtcNow = new DateTimeOffset(2027, 3, 31, 20, 30, 0, TimeSpan.Zero);   // 23:30 31.03 (літній час) — писанки ще нема
        Assert.Equal("Повернеться 01.04", _r.Buy("Петро", "egg").Message);
        _r.Clock.UtcNow = new DateTimeOffset(2027, 3, 31, 21, 10, 0, TimeSpan.Zero);   // 00:10 01.04 — уже є
        Assert.True(_r.Buy("Петро", "egg").Ok);
    }

    // =============================================================================================
    // Подвійний клік і гроші
    // =============================================================================================

    [Fact]
    public void Double_click_charges_once()
    {
        _r.Give("Оля", 1000);
        var before = _r.Balance("Оля");

        var replies = new LavkaReply[4];
        Parallel.For(0, replies.Length, i => replies[i] = _r.Buy("Оля", "fox"));

        Assert.Single(replies, x => x.Ok);
        Assert.All(replies.Where(x => !x.Ok), x => Assert.Equal(Lavka.Mine, x.Message));
        Assert.Equal(before - 250, _r.Balance("Оля"));
        Assert.Single(_r.Eco.Outbox.Of<WalletChanged>(), w => w.Reason == "shop:fox");
    }

    [Fact]
    public void Retry_after_a_crash_between_payment_and_record_does_not_charge_again()
    {
        _r.Give("Оля", 1000);
        // черепки вже списались, а запис про річ не встиг лягти — сервер упав посередині
        Assert.True(_r.Eco.Economy.TrySpend("Оля", 250, "shop:fox", "lavka:оля:fox"));
        var paid = _r.Balance("Оля");

        var retry = _r.Buy("Оля", "fox");

        Assert.True(retry.Ok, retry.Message);
        Assert.Equal(paid, _r.Balance("Оля"));
        Assert.True(_r.Owns("Оля", "fox"));
    }

    [Fact]
    public void Wallet_moves_read_as_shop_and_gift_and_group_in_the_month()
    {
        _r.Clock.UtcNow = new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
        _r.Give("Оля", 2000);
        _r.Eco.Outbox.Clear();

        Assert.True(_r.Buy("Оля", "fox").Ok);
        Assert.True(_r.Buy("Оля", "silver", to: "Петро").Ok);

        var moves = _r.Eco.Outbox.Of<WalletChanged>();
        Assert.Equal(["shop:fox", "gift:silver"], moves.Select(w => w.Reason));
        Assert.Equal(["−250 черепків: Лавка — значок «Лис» 🦊", "−1000 черепків: подарунок — Срібна рамка"], moves.Select(w => w.Text));
        Assert.Equal("Лавка — колір ніка «Небо»", _r.Eco.Economy.Reason("shop:sky"));
        Assert.Equal("Лавка — Присвята в ефір", _r.Eco.Economy.Reason("shop:dedication"));
        Assert.Equal("подарунок — титул «Нічна сова»", _r.Eco.Economy.Reason("gift:nightowl"));
        Assert.Equal("Лавка — lost-thing", _r.Eco.Economy.Reason("shop:lost-thing"));   // річ, якої вже нема, — хоч кодом

        var e = Views.Json(PeopleEndpoints.Ledger(As("Оля"), null, _r.Eco.Store, _r.Eco.Economy, _r.Clock));
        var month = e.GetProperty("month").EnumerateArray().ToList();
        var shop = Assert.Single(month, g => g.GetProperty("cat").GetString() == "shop");
        Assert.Equal("Лавка", shop.GetProperty("title").GetString());
        Assert.Equal(250, shop.GetProperty("spent").GetInt32());
        var gift = Assert.Single(month, g => g.GetProperty("cat").GetString() == "gift");
        Assert.Equal("подарунки", gift.GetProperty("title").GetString());
        Assert.Equal(1000, gift.GetProperty("spent").GetInt32());
        Assert.Equal("подарунок — Срібна рамка", e.GetProperty("items")[0].GetProperty("text").GetString());
    }

    // =============================================================================================
    // Гість
    // =============================================================================================

    [Fact]
    public async Task Guest_is_refused_everywhere()
    {
        _r.Give("гість Вася", 5000);
        var before = _r.Balance("гість Вася");

        Assert.Equal(Lavka.NotAccount, _r.Lavka.Buy("гість Вася", false, "fox", null).Message);
        Assert.Equal(Lavka.NotAccount, _r.Lavka.Buy("гість Вася", false, "fox", "Оля").Message);
        Assert.Equal(Lavka.NotAccount, _r.Lavka.Wear("гість Вася", false, "icon", "fox").Message);
        Assert.Equal(Lavka.NotAccount, (await _r.Lavka.DedicateAsync("гість Вася", false, "Оля", "luck")).Message);
        Assert.Equal(Lavka.NotAccount, _r.Lavka.Fireworks("гість Вася", false));
        Assert.Equal("Закріпи нік — тоді Лавка твоя", Lavka.NotAccount);
        Assert.Equal(before, _r.Balance("гість Вася"));
        Assert.Empty(_r.Wire.Shown);
    }

    // =============================================================================================
    // Вигляд: GET /api/lavka/looks і подія look
    // =============================================================================================

    [Fact]
    public void Looks_are_ready_to_draw_and_only_for_those_wearing_something()
    {
        _r.Give("Оля", 20000);
        foreach (var id in new[] { "fox", "gold", "cornflower", "nightowl", "petrykivka" }) Assert.True(_r.Buy("Оля", id).Ok, id);
        _r.Give("Андрій", 3000);
        Assert.True(_r.Buy("Андрій", "rainbow").Ok);
        Assert.True(_r.Buy("Андрій", "fireworks").Ok);            // вміння на вигляд не впливає

        var e = Views.Json(LavkaSetup.Looks(_r.Lavka));

        var looks = e.GetProperty("looks");
        Assert.Equal(["Андрій", "Оля"], looks.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));   // Петро нічого не вдягнув
        var olya = looks.GetProperty("Оля");
        Assert.Equal(["icon", "frame", "color", "title", "bg"], olya.EnumerateObject().Select(p => p.Name));
        Assert.Equal("🦊", olya.GetProperty("icon").GetString());
        Assert.Equal("gold", olya.GetProperty("frame").GetString());
        Assert.Equal(212, olya.GetProperty("color").GetInt32());
        Assert.Equal("Нічна сова", olya.GetProperty("title").GetString());
        Assert.Equal("petrykivka", olya.GetProperty("bg").GetString());
        var andriy = looks.GetProperty("Андрій");
        Assert.Equal("rainbow", andriy.GetProperty("color").GetString());
        Assert.Equal(JsonValueKind.Null, andriy.GetProperty("icon").ValueKind);

        // зняв усе — зник із вигляду
        Assert.True(_r.Wear("Андрій", "color", null).Ok);
        Assert.False(Views.Json(LavkaSetup.Looks(_r.Lavka)).GetProperty("looks").TryGetProperty("Андрій", out _));
    }

    [Fact]
    public void Look_is_signed_with_the_account_spelling()
    {
        _r.Give("Оля", 1000);
        Assert.True(_r.Lavka.Buy("Оля", true, "fox", null).Ok);

        Assert.True(_r.Lavka.Looks().ContainsKey("Оля"));
        Assert.Equal("Оля", _r.Wire.Shown[^1].Nick);
    }

    // =============================================================================================
    // Феєрверк
    // =============================================================================================

    [Fact]
    public void Fireworks_needs_the_perk_goes_to_everyone_and_rests_ten_minutes()
    {
        Assert.Contains("спершу купи", _r.Lavka.Fireworks("Оля", true));
        _r.Give("Оля", 1000);
        Assert.True(_r.Buy("Оля", "fireworks").Ok);

        Assert.Null(_r.Lavka.Fireworks("Оля", true));

        Assert.Equal(["Оля"], _r.Wire.Launched);
        var line = Assert.Single(_r.Wire.Lines);
        Assert.Equal(["id", "kind", "nick", "text", "at"], line.EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, line.GetProperty("id").GetInt64());
        Assert.Equal("fx", line.GetProperty("kind").GetString());
        Assert.Equal("Оля", line.GetProperty("nick").GetString());
        Assert.Equal("🎆 бахає феєрверк!", line.GetProperty("text").GetString());
        Assert.Equal(_r.Clock.UtcNow, line.GetProperty("at").GetDateTimeOffset());
        Assert.DoesNotContain(_r.Chat(), m => m.Kind == "fx");        // у базу не лягає

        Assert.Equal("Новий феєрверк — через 10 хв", _r.Lavka.Fireworks("Оля", true));
        _r.Clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("Новий феєрверк — через 1 хв", _r.Lavka.Fireworks("Оля", true));
        _r.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("Новий феєрверк — через 30 с", _r.Lavka.Fireworks("Оля", true));
        _r.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(_r.Lavka.Fireworks("Оля", true));
        Assert.Equal(2, _r.Wire.Launched.Count);
    }

    [Fact]
    public async Task Perk_rest_survives_a_restart()
    {
        _r.Give("Оля", 3000);
        Assert.True(_r.Buy("Оля", "fireworks").Ok);
        Assert.True(_r.Buy("Оля", "dedication").Ok);
        _r.Air.Song("Оля", "Пісня");
        Assert.Null(_r.Lavka.Fireworks("Оля", true));
        Assert.True(_r.Dedicate("Оля", "Петро").Ok);
        _r.Clock.Advance(TimeSpan.FromMinutes(1));

        var fresh = _r.Fresh();

        Assert.Equal("Новий феєрверк — через 9 хв", fresh.Fireworks("Оля", true));
        Assert.Equal(_r.Clock.UtcNow.AddMinutes(9), fresh.ReadyAt("Оля", LavkaCatalog.Fireworks));
        var perks = Views.Json(fresh.View("Оля", true)).GetProperty("perks");
        Assert.Equal(_r.Clock.UtcNow.AddMinutes(9), perks.GetProperty("fireworks").GetProperty("readyAt").GetDateTimeOffset());
        Assert.Equal(_r.Clock.UtcNow.AddMinutes(179), perks.GetProperty("dedication").GetProperty("readyAt").GetDateTimeOffset());
        _r.Air.Song("Оля", "Ще пісня");
        Assert.Equal("Наступна присвята — через 2 год 59 хв", (await fresh.DedicateAsync("Оля", true, "Петро", "luck")).Message);
    }

    // =============================================================================================
    // Присвята в ефір
    // =============================================================================================

    LavkaRig Dedicator()
    {
        _r.Give("Оля", 2000);
        Assert.True(_r.Buy("Оля", "dedication").Ok);
        _r.Wire.Shown.Clear();
        return _r;
    }

    [Fact]
    public void Dedication_stands_right_before_the_next_track_not_yet_sent_to_liquidsoap()
    {
        var r = Dedicator();
        var head = r.Air.Song("Оля", "Перша", status: "dispatched");   // уже в liquidsoap — перед нею не стати
        r.Air.Song("Петро", "Чужа");
        r.Air.Items.Add(new LavkaQueued("mine-voice", new TrackInfo("voice-000000000abc", "Голосове", "Оля", 5, null, "/v", null), "Оля", "ready"));
        var target = r.Air.Song("оля", "Друга", status: "downloading", artist: "Океан Ельзи");
        r.Air.Song("Оля", "Третя");

        var reply = r.Dedicate("Оля", "петро", "luck");

        Assert.True(reply.Ok, reply.Message);
        Assert.Equal("Є! Глек скаже присвяту перед «Друга — Океан Ельзи»", reply.Message);
        Assert.Equal(["Цю пісню Оля присвячує Петрові — на удачу"], r.Voice.Said);
        var order = r.Air.Items.Select(q => q.Track.Title).ToList();
        Assert.Equal(["Перша", "Чужа", "Голосове", "Присвята Петрові", "Друга", "Третя"], order);
        var voice = r.Air.Items[3];
        Assert.StartsWith("voice-", voice.Track.Id);
        Assert.Equal("Оля", voice.Track.Artist);
        Assert.Equal("Оля", voice.RequestedBy);
        Assert.Equal(head.ItemId, r.Air.Items[0].ItemId);
        Assert.Equal(target.ItemId, r.Air.Items[4].ItemId);

        // рядок у балачках — у базі й усім; Глек-бот такого kind не слухає
        var saved = Assert.Single(r.Chat(), m => m.Kind == "dedication");
        Assert.Equal("Оля", saved.Nick);
        Assert.Equal("присвячує «Друга — Океан Ельзи» Петрові — на удачу", saved.Text);
        var line = Assert.Single(r.Wire.Lines);
        Assert.Equal("dedication", line.GetProperty("kind").GetString());
        Assert.Equal(saved.Id, line.GetProperty("id").GetInt64());
        Assert.Equal("Петро", line.GetProperty("to").GetString());
    }

    [Theory]
    [InlineData("*")]
    [InlineData(" * ")]
    [InlineData("всім")]
    [InlineData("Усім")]
    public void Dedication_to_everyone(string to)
    {
        var r = Dedicator();
        r.Air.Song("Оля", "Пісня", artist: "");

        var reply = r.Dedicate("Оля", to, "love");

        Assert.True(reply.Ok, reply.Message);
        Assert.Equal(["Цю пісню Оля присвячує всім, хто слухає — з любов'ю"], r.Voice.Said);
        Assert.Equal("Присвята всім", r.Air.Items[0].Track.Title);
        Assert.Equal("присвячує «Пісня» всім, хто слухає — з любов'ю", Assert.Single(r.Chat(), m => m.Kind == "dedication").Text);
        Assert.Equal("*", Assert.Single(r.Wire.Lines).GetProperty("to").GetString());
    }

    [Fact]
    public void Dedication_without_a_track_asks_to_queue_one_first_and_costs_nothing()
    {
        var r = Dedicator();
        r.Air.Song("Петро", "Чужа");
        r.Air.Song("Оля", "Вже грає наступною", status: "dispatched");
        r.Air.Song("Оля", "Не скачалась", status: "failed");

        var reply = r.Dedicate("Оля", "Петро");

        Assert.False(reply.Ok);
        Assert.Equal("Спершу закинь пісню — присвята прозвучить перед нею", reply.Message);
        Assert.Empty(r.Voice.Said);
        Assert.Empty(r.Wire.Lines);
        Assert.Null(r.Lavka.ReadyAt("Оля", LavkaCatalog.Dedication));   // перерва не почалась

        r.Air.Song("Оля", "Нарешті");
        Assert.True(r.Dedicate("Оля", "Петро").Ok);
    }

    [Fact]
    public void Without_voice_the_dedication_still_lands_in_the_chat()
    {
        var r = Dedicator();
        r.Voice.Silent = true;
        r.Air.Song("Оля", "Пісня");

        var reply = r.Dedicate("Оля", "Петро", "night");

        Assert.True(reply.Ok, reply.Message);
        Assert.Equal("Присвята лягла в балачки — без голосу: Глек зараз мовчить", reply.Message);
        Assert.Single(r.Air.Items);                                     // у черзі — лише сама пісня
        Assert.Equal("присвячує «Пісня — Гурт» Петрові — на добраніч", Assert.Single(r.Chat(), m => m.Kind == "dedication").Text);
        Assert.Single(r.Wire.Lines);
        Assert.NotNull(r.Lavka.ReadyAt("Оля", LavkaCatalog.Dedication));
        Assert.True(r.Owns("Оля", "dedication"));                      // вміння нікуди не ділось
    }

    [Fact]
    public void When_the_track_leaves_while_glek_speaks_the_dedication_goes_without_voice()
    {
        var r = Dedicator();
        var song = r.Air.Song("Оля", "Пісня");
        r.Voice.During = () => r.Air.Dispatch(song);

        var reply = r.Dedicate("Оля", "Петро");

        Assert.True(reply.Ok, reply.Message);
        Assert.Equal("Присвята лягла в балачки — без голосу: пісня вже пішла в ефір", reply.Message);
        Assert.Single(r.Air.Items);
        Assert.Equal("присвячує «Пісня — Гурт» Петрові — на удачу", Assert.Single(r.Chat(), m => m.Kind == "dedication").Text);
    }

    [Fact]
    public void When_the_air_refuses_the_voice_the_dedication_still_lands_in_the_chat()
    {
        var r = Dedicator();
        r.Air.Refuse = true;
        r.Air.Song("Оля", "Пісня");

        var reply = r.Dedicate("Оля", "Петро");

        Assert.True(reply.Ok, reply.Message);
        Assert.Contains("без голосу", reply.Message);
        Assert.Single(r.Chat(), m => m.Kind == "dedication");
    }

    [Fact]
    public void Dedication_rests_three_hours()
    {
        var r = Dedicator();
        r.Air.Song("Оля", "Перша");
        Assert.True(r.Dedicate("Оля", "Петро").Ok);
        r.Air.Song("Оля", "Друга");

        Assert.Equal("Наступна присвята — через 3 год", r.Dedicate("Оля", "Петро").Message);
        r.Clock.Advance(TimeSpan.FromMinutes(46));
        Assert.Equal("Наступна присвята — через 2 год 14 хв", r.Dedicate("Оля", "Петро").Message);
        r.Clock.Advance(TimeSpan.FromMinutes(134));
        Assert.True(r.Dedicate("Оля", "Петро").Ok);
        Assert.Equal(2, r.Voice.Said.Count);
    }

    [Fact]
    public void Dedication_refusals()
    {
        _r.Air.Song("Оля", "Пісня");
        Assert.Contains("спершу купи", _r.Dedicate("Оля", "Петро").Message);

        var r = Dedicator();
        Assert.Equal("Обери, з чим присвятити", r.Dedicate("Оля", "Петро", phrase: "because").Message);
        Assert.Equal("Обери, з чим присвятити", r.Dedicate("Оля", "Петро", phrase: null).Message);
        Assert.Equal("Кому присвятити?", r.Dedicate("Оля", "  ").Message);
        Assert.Equal("Кому присвятити?", r.Dedicate("Оля", null).Message);
        Assert.Equal("Собі — то вже не присвята. Обери когось або всіх", r.Dedicate("Оля", "@ОЛЯ").Message);
        Assert.Empty(r.Voice.Said);
        Assert.Null(r.Lavka.ReadyAt("Оля", LavkaCatalog.Dedication));
    }

    [Fact]
    public void Dedication_finds_how_the_one_it_is_for_is_written()
    {
        var r = Dedicator();
        r.Eco.Presence.Set("c1", "гість Вася");
        r.Air.Song("Оля", "Пісня");

        Assert.True(r.Dedicate("Оля", "вася", "just").Ok);

        Assert.Equal(["Цю пісню Оля присвячує гостю Васі — просто так"], r.Voice.Said);
        Assert.Equal("гість Вася", Assert.Single(r.Wire.Lines).GetProperty("to").GetString());
    }

    [Fact]
    public void Dedication_to_someone_who_is_not_here_is_fine_too()
    {
        var r = Dedicator();
        r.Air.Song("Оля", "Пісня");

        Assert.True(r.Dedicate("Оля", "Бабуся", "love").Ok);

        Assert.Equal(["Цю пісню Оля присвячує Бабусі — з любов'ю"], r.Voice.Said);
    }

    [Fact]
    public async Task Second_click_while_glek_speaks_does_not_start_a_second_dedication()
    {
        var r = Dedicator();
        r.Air.Song("Оля", "Пісня");
        var gate = new TaskCompletionSource<LavkaVoiceClip?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new SlowVoice(gate.Task);
        var lavka = new Lavka(new LavkaStore(r.Db), r.Eco.Economy, r.Eco.Store, r.Db, r.Eco.Presence, r.Air, slow, r.Wire, r.Clock,
            NullLogger<Lavka>.Instance);

        var first = lavka.DedicateAsync("Оля", true, "Петро", "luck");
        var second = await lavka.DedicateAsync("Оля", true, "Петро", "luck");
        gate.SetResult(null);

        Assert.False(second.Ok);
        Assert.Equal("Присвята вже летить — ще мить", second.Message);
        Assert.True((await first).Ok);
        Assert.Single(r.Chat(), m => m.Kind == "dedication");
    }

    /// <summary>Голос, який озвучує рівно доти, доки тест не скаже «готово».</summary>
    sealed class SlowVoice(Task<LavkaVoiceClip?> done) : ILavkaVoice
    {
        public Task<LavkaVoiceClip?> SpeakAsync(string text, string title, string artist, CancellationToken ct) => done;
    }

    // =============================================================================================
    // HTTP: форми відповідей
    // =============================================================================================

    [Fact]
    public void Buy_endpoint_answers_ok_message_and_balance()
    {
        _r.Give("Оля", 1000);
        var before = _r.Balance("Оля");

        var (status, body) = Radio.Reply(LavkaSetup.Buy(As("Оля"), new LavkaSetup.BuyRequest("fox", null), _r.Lavka));
        Assert.Equal(200, status);
        Assert.Equal(["ok", "message", "balance"], body.EnumerateObject().Select(p => p.Name));
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(before - 250, body.GetProperty("balance").GetInt32());

        var (failStatus, fail) = Radio.Reply(LavkaSetup.Buy(As("Оля"), new LavkaSetup.BuyRequest("fox", null), _r.Lavka));
        Assert.Equal(400, failStatus);
        Assert.False(fail.GetProperty("ok").GetBoolean());
        Assert.Equal("Уже твоє", fail.GetProperty("message").GetString());
        Assert.Equal(before - 250, fail.GetProperty("balance").GetInt32());

        var (guestStatus, guest) = Radio.Reply(LavkaSetup.Buy(As("гість Вася", account: false), new LavkaSetup.BuyRequest("fox", null), _r.Lavka));
        Assert.Equal(400, guestStatus);
        Assert.Equal("Закріпи нік — тоді Лавка твоя", guest.GetProperty("message").GetString());

        var (giftStatus, gift) = Radio.Reply(LavkaSetup.Buy(As("Оля"), new LavkaSetup.BuyRequest("cat", "Петро"), _r.Lavka));
        Assert.Equal(200, giftStatus);
        Assert.Equal("Подаровано Петрові: значок «Кіт» 🐈 — назавжди", gift.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Wear_and_dedicate_endpoints_answer_ok_and_message()
    {
        _r.Give("Оля", 3000);
        Assert.True(_r.Buy("Оля", "fox").Ok);
        Assert.True(_r.Buy("Оля", "dedication").Ok);

        var (status, body) = Radio.Reply(LavkaSetup.Wear(As("Оля"), new LavkaSetup.WearRequest("icon", null), _r.Lavka));
        Assert.Equal(200, status);
        Assert.Equal(["ok", "message"], body.EnumerateObject().Select(p => p.Name));
        Assert.Null(_r.Lavka.LookOf("Оля"));

        var (badStatus, bad) = Radio.Reply(LavkaSetup.Wear(As("Оля"), new LavkaSetup.WearRequest("icon", "cat"), _r.Lavka));
        Assert.Equal(400, badStatus);
        Assert.False(bad.GetProperty("ok").GetBoolean());

        var (noSongStatus, noSong) = Radio.Reply(await LavkaSetup.Dedicate(As("Оля"), new LavkaSetup.DedicateRequest("Петро", "luck"), _r.Lavka));
        Assert.Equal(400, noSongStatus);
        Assert.Equal(Lavka.NoSong, noSong.GetProperty("message").GetString());

        _r.Air.Song("Оля", "Пісня");
        var (okStatus, ok) = Radio.Reply(await LavkaSetup.Dedicate(As("Оля"), new LavkaSetup.DedicateRequest("Петро", "luck"), _r.Lavka));
        Assert.Equal(200, okStatus);
        Assert.True(ok.GetProperty("ok").GetBoolean());
    }

    // =============================================================================================
    // Дрібниці: скільки чекати
    // =============================================================================================

    [Theory]
    [InlineData(0, "1 с")]
    [InlineData(40, "40 с")]
    [InlineData(60, "1 хв")]
    [InlineData(61, "2 хв")]
    [InlineData(600, "10 хв")]
    [InlineData(3600, "1 год")]
    [InlineData(8040, "2 год 14 хв")]
    [InlineData(10800, "3 год")]
    public void Wait_reads_like_a_human_would_say_it(int seconds, string text)
    {
        Assert.Equal(text, Lavka.Left(TimeSpan.FromSeconds(seconds)));
    }

    // =============================================================================================
    // Справжній голос поверх TtsService (edge-tts підмінено FakeTtsEngine)
    // =============================================================================================

    [Fact]
    public async Task Real_voice_puts_the_line_into_the_air_cache_as_a_voice_message()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lavka-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new FakeTtsEngine();
            var tts = new TtsService(engine, new FixedOptions<TtsOptions>(new TtsOptions { CacheDir = Path.Combine(dir, "tts") }),
                NullLogger<TtsService>.Instance);
            var voice = new TtsLavkaVoice(tts, new FixedOptions<YtDlpOptions>(new YtDlpOptions { CacheDir = dir }),
                NullLogger<TtsLavkaVoice>.Instance) { Wait = TimeSpan.FromSeconds(10) };

            var speaking = voice.SpeakAsync("Цю пісню Оля присвячує Петрові — на удачу", "Присвята Петрові", "Оля", CancellationToken.None);
            while (await tts.StepAsync(CancellationToken.None)) { }   // воркер озвучки — за тест
            var clip = await speaking;

            Assert.NotNull(clip);
            Assert.Equal(["Цю пісню Оля присвячує Петрові — на удачу"], engine.Said);
            Assert.StartsWith(VoiceService.Prefix, clip!.Track.Id);
            Assert.Equal(Path.Combine(dir, clip.Track.Id + ".mp3"), clip.FilePath);
            Assert.True(File.Exists(clip.FilePath));
            Assert.Equal(2, clip.Track.DurationSec);                    // 1,5 с — угору
            Assert.Equal("Присвята Петрові", clip.Track.Title);
            Assert.Equal("Оля", clip.Track.Artist);
            Assert.Equal($"/api/voice/{clip.Track.Id}.mp3", clip.Track.SourceUrl);

            // той самий текст удруге — з кешу, без нової озвучки, але новим голосовим
            var again = await voice.SpeakAsync("Цю пісню Оля присвячує Петрові — на удачу", "Присвята Петрові", "Оля", CancellationToken.None);
            Assert.NotNull(again);
            Assert.NotEqual(clip.Track.Id, again!.Track.Id);
            Assert.Single(engine.Said);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Real_voice_is_silent_when_tts_is_off_or_fails()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lavka-voice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var engine = new FakeTtsEngine();
            var opts = new TtsOptions { CacheDir = Path.Combine(dir, "tts"), Enabled = false };
            var tts = new TtsService(engine, new FixedOptions<TtsOptions>(opts), NullLogger<TtsService>.Instance);
            var voice = new TtsLavkaVoice(tts, new FixedOptions<YtDlpOptions>(new YtDlpOptions { CacheDir = dir }),
                NullLogger<TtsLavkaVoice>.Instance) { Wait = TimeSpan.FromMilliseconds(300) };

            Assert.Null(await voice.SpeakAsync("Цю пісню Оля присвячує Петрові — на удачу", "Присвята Петрові", "Оля", CancellationToken.None));
            Assert.Empty(engine.Said);

            opts.Enabled = true;
            engine.Broken.Add("Цю пісню Оля присвячує Петрові — на удачу");
            var speaking = voice.SpeakAsync("Цю пісню Оля присвячує Петрові — на удачу", "Присвята Петрові", "Оля", CancellationToken.None);
            while (await tts.StepAsync(CancellationToken.None)) { }
            Assert.Null(await speaking);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // =============================================================================================
    // Відмінки ніків
    // =============================================================================================

    [Theory]
    [InlineData("Петро", "Петрові", "Петра")]
    [InlineData("Оля", "Олі", "Олі")]
    [InlineData("Микола", "Миколі", "Миколи")]
    [InlineData("Ольга", "Ользі", "Ольги")]
    [InlineData("Галка", "Галці", "Галки")]
    [InlineData("Саша", "Саші", "Саші")]
    [InlineData("Марія", "Марії", "Марії")]
    [InlineData("Ілля", "Іллі", "Іллі")]
    [InlineData("Андрій", "Андрієві", "Андрія")]
    [InlineData("Василь", "Василеві", "Василя")]
    [InlineData("Влад", "Владові", "Влада")]
    [InlineData("владік", "владікові", "владіка")]
    [InlineData("Лукаш", "Лукашеві", "Лукаша")]
    [InlineData("Ігор", "Ігореві", "Ігоря")]
    [InlineData("Кіт", "Котові", "Кота")]
    [InlineData("Злий", "Злому", "Злого")]
    [InlineData("ОЛЯ", "ОЛІ", "ОЛІ")]
    [InlineData("Лео", "Лео", "Лео")]
    [InlineData("Бекі", "Бекі", "Бекі")]
    [InlineData("smaug", "smaug", "smaug")]
    [InlineData("Оля2000", "Оля2000", "Оля2000")]
    [InlineData("гість Вася", "гостю Васі", "гостя Васі")]
    [InlineData("гість", "гостю", "гостя")]
    [InlineData("микола ( справжній )", "миколі ( справжній )", "миколи ( справжній )")]
    public void Nick_declines_like_a_name(string nick, string dative, string genitive)
    {
        Assert.Equal(dative, NickCases.Dative(nick));
        Assert.Equal(genitive, NickCases.Genitive(nick));
    }

    [Theory]
    [InlineData("Олі", "В")]
    [InlineData("Андрія", "В")]
    [InlineData("Петра", "У")]
    [InlineData("smaug", "У")]
    public void Preposition_at_the_start_follows_euphony(string next, string preposition)
    {
        Assert.Equal(preposition, NickCases.AtStart(next));
    }
}
