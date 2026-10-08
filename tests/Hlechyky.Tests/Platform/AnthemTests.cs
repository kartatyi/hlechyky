using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Гімн переможця (docs/games/specs/anthem.md): полиця «🎺 Гімни» в Лавці, який гімн у людини (<see cref="Lavka.AnthemOf"/>),
/// коли він звучить за столом (<see cref="AnthemPlayer"/>), розсилка групі столу, «Свій трек» (<see cref="LavkaAnthems"/>:
/// хто, з чого, як часто, обрізання start/len) і HTTP-форми. ffmpeg підмінено — тести не падають на машині без нього;
/// справжній ffmpeg ріже лише в одному тесті, і то тільки коли він є (інакше Skip). Жодного звуку: файли лише пишуться.
/// </summary>
public sealed class AnthemTests : IDisposable
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

    /// <summary>ffprobe й ffmpeg без ffmpeg: «бачать» те, що скаже тест, а «вирізане» — кілька байтів mp3 у потрібному місці.</summary>
    sealed class FakeCutter : IAnthemCutter
    {
        public AnthemProbe? Probe { get; set; } = new(true, 180);
        public string? Fail { get; set; }
        public List<(double Start, double Len)> Cuts { get; } = [];
        public int Probes;

        public Task<AnthemProbe?> ProbeAsync(string path, CancellationToken ct)
        {
            Probes++;
            Assert.True(File.Exists(path));   // ffprobe дивиться на вже прийнятий файл
            return Task.FromResult(Probe);
        }

        /// <summary>Що зробити з джерелом перед «нарізкою» — напр., стерти його, як це зробив би TrackCache.</summary>
        public Action<string>? BeforeCut { get; set; }

        public Task<string?> CutAsync(string src, string dst, double start, double len, CancellationToken ct)
        {
            BeforeCut?.Invoke(src);
            Cuts.Add((start, len));
            if (Fail is not null) return Task.FromResult<string?>(Fail);
            File.WriteAllBytes(dst, Mp3(400));
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Пісні з пошуку без мережі й yt-dlp: «кеш радіо» — словник id → файл у своїй тимчасовій теці, «скачування» пише кілька
    /// байтів туди ж. Що пісня є в YouTube, каже тест (<see cref="Song"/>).
    /// </summary>
    sealed class FakeSource(string dir) : IAnthemSource
    {
        public Dictionary<string, AnthemSourceTrack> Cache { get; } = new();
        public Dictionary<string, TrackInfo> Known { get; } = new();
        public string? Fail { get; set; }
        public int Infos, Downloads;

        public AnthemSourceTrack? Find(string id) => Cache.GetValueOrDefault(id);

        public Task<TrackInfo?> InfoAsync(string id, CancellationToken ct)
        {
            Infos++;
            return Task.FromResult(Known.GetValueOrDefault(id));
        }

        public Task<string> DownloadAsync(TrackInfo t, CancellationToken ct)
        {
            Downloads++;
            if (Fail is not null) throw new InvalidOperationException(Fail);
            return Task.FromResult(Put(t).Path);
        }

        public TrackInfo Song(string id, string artist = "Гурт", string title = "Пісня", int seconds = 200) =>
            Known[id] = new TrackInfo(id, title, artist, seconds, null, "https://music.youtube.com/watch?v=" + id, null);

        /// <summary>Пісня вже лежить у кеші радіо.</summary>
        public AnthemSourceTrack Put(TrackInfo t)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, t.Id + ".m4a");
            File.WriteAllBytes(path, new byte[300]);
            return Cache[t.Id] = new AnthemSourceTrack(t.Id, t.Title, t.Artist, t.DurationSec, path);
        }
    }

    readonly EconomyRig _eco = new();
    readonly FakeWire _wire = new();
    readonly FakeCutter _cutter = new();
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hlechyky-anthem-" + Guid.NewGuid().ToString("N"));
    readonly FakeSource _source;
    readonly LavkaStore _store;
    readonly Lavka _lavka;
    readonly LavkaAnthems _anthems;
    readonly AnthemPlayer _player;

    FakeClock Clock => _eco.Clock;
    List<Anthem> Played => _eco.Outbox.Of<Anthem>();

    public AnthemTests()
    {
        Assert.True(_eco.Db.AddAccount("Оля", "", "salt"));
        Assert.True(_eco.Db.AddAccount("Петро", "", "salt"));
        Assert.True(_eco.Db.AddAccount("Андрій", "", "salt"));
        _store = new LavkaStore(_eco.Db);
        _lavka = new Lavka(_store, _eco.Economy, _eco.Store, _eco.Db, _eco.Presence, new NoAir(), new NoVoice(), _wire, Clock,
            NullLogger<Lavka>.Instance);
        _source = new FakeSource(_dir + "-cache");
        _anthems = new LavkaAnthems(_store, _lavka, _wire, Clock, new AnthemDir(_dir), _cutter, _source, NullLogger<LavkaAnthems>.Instance);
        _player = new AnthemPlayer(_eco.Events, _lavka, _eco.Outbox, NullLogger<AnthemPlayer>.Instance);
        _player.StartAsync(default).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _player.StopAsync(default).GetAwaiter().GetResult();
        _eco.Dispose();
        foreach (var d in new[] { _dir, _dir + "-cache" })
            try { Directory.Delete(d, recursive: true); } catch (IOException) { /* теки нема чи хай лежить у temp */ }
    }

    void Give(string nick, int shards) => _eco.Economy.Grant(nick, shards, "listen", "test:" + Guid.NewGuid().ToString("N"));

    LavkaReply Buy(string nick, string item, string? to = null) => _lavka.Buy(nick, account: true, item, to);

    /// <summary>У ніка є «Свій трек» (куплений і вдягнутий).</summary>
    void OwnTrack(string nick)
    {
        Give(nick, 3000);
        Assert.True(Buy(nick, LavkaCatalog.OwnAnthem).Ok);
    }

    /// <summary>Початок mp3 з ID3 і нулі — ChatFileKind.Sniff бачить звук.</summary>
    static byte[] Mp3(int size)
    {
        var b = new byte[size];
        "ID3"u8.CopyTo(b);
        b[3] = 4;
        return b;
    }

    /// <summary>MOV з айфона: ftyp із брендом «qt  ».</summary>
    static byte[] Mov(int size)
    {
        var b = new byte[size];
        new byte[] { 0, 0, 0, 0x14 }.CopyTo(b, 0);
        "ftypqt  "u8.CopyTo(b.AsSpan(4));
        return b;
    }

    Task<LavkaAnthemReply> Up(string nick, byte[] bytes, double? start = null, double? len = null, string? title = null, bool account = true) =>
        _anthems.SetAsync(nick, account, start, len, title, bytes.Length, new MemoryStream(bytes), default);

    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нулики", "хрестики-нулики");
    static readonly GameInfo Mafia = EconomyRig.Info("mafia", "Мафія", "мафію", GameGroup.Party, max: 10);

    // =============================================================================================
    // Каталог
    // =============================================================================================

    static readonly string[] Ready = ["fanfare", "drumroll", "chiptune", "trombone", "dzen", "trembita", "bayan", "bells", "applause",
        "hopak", "cosmos", "solemn"];

    [Fact]
    public void Shelf_has_thirteen_anthems_with_unique_ids_prices_and_tiers()
    {
        var anthems = LavkaCatalog.All.Where(i => i.Kind == LavkaKind.Anthem).ToList();
        Assert.Equal([.. Ready, LavkaCatalog.OwnAnthem], anthems.Select(i => i.Id));
        Assert.Equal(anthems.Count, anthems.Select(i => i.Id).Distinct().Count());
        Assert.Contains(LavkaKind.Anthem, LavkaCatalog.Slots);

        Assert.All(anthems.Take(5), i => Assert.Equal((300, 1), (i.Price, i.Tier)));
        Assert.All(anthems.Skip(5).Take(4), i => Assert.Equal((600, 2), (i.Price, i.Tier)));
        Assert.All(anthems.Skip(9).Take(3), i => Assert.Equal((1000, 3), (i.Price, i.Tier)));
        var own = LavkaCatalog.Get(LavkaCatalog.OwnAnthem)!;
        Assert.Equal(("Свій трек", 3000, 3), (own.Title, own.Price, own.Tier));
        Assert.True(own.Price > LavkaCatalog.Get(LavkaCatalog.Photo)!.Price);   // звук для всіх за столом — дорожче за фотку

        var trembita = LavkaCatalog.Get("trembita")!;
        Assert.Equal(("Трембіта", 600), (trembita.Title, trembita.Price));
        Assert.Equal(new LavkaAnthemArt("📯", "/static/anthems/trembita.mp3"), trembita.Art);
        Assert.Equal("гімн «Трембіта»", LavkaCatalog.Label(trembita));
    }

    [Fact]
    public void Every_ready_anthem_has_its_mp3_in_web_static()
    {
        Assert.All(Ready, id => Assert.Equal($"/static/anthems/{id}.mp3", ((LavkaAnthemArt)LavkaCatalog.Get(id)!.Art).Url));
        // Файли синтезує docs/games/dev/anthems-make.py, і вони лежать у репозиторії: нема теки — полиця грала б тишу
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "static"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var anthems = Path.Combine(dir!, "web", "static", "anthems");
        Assert.True(Directory.Exists(anthems), "нема web/static/anthems — готові гімни не закомічено");
        Assert.All(Ready, id =>
        {
            var file = new FileInfo(Path.Combine(anthems, id + ".mp3"));
            Assert.True(file.Exists, $"нема web/static/anthems/{id}.mp3");
            Assert.True(file.Length > 0, $"порожній {id}.mp3");
        });
    }

    [Fact]
    public void Shop_draws_ready_anthems_with_a_url_and_own_track_with_own_flag()
    {
        var e = Views.Json(_lavka.View("Оля", true));
        var items = e.GetProperty("items").EnumerateArray().ToList();
        var fanfare = items.Single(i => i.GetProperty("id").GetString() == "fanfare");
        Assert.Equal("anthem", fanfare.GetProperty("kind").GetString());
        var art = fanfare.GetProperty("art");
        Assert.Equal(["emoji", "url"], art.EnumerateObject().Select(p => p.Name));
        Assert.Equal("🎺", art.GetProperty("emoji").GetString());
        Assert.Equal("/static/anthems/fanfare.mp3", art.GetProperty("url").GetString());

        var own = items.Single(i => i.GetProperty("id").GetString() == "own-anthem").GetProperty("art");
        Assert.Equal(["emoji", "own"], own.EnumerateObject().Select(p => p.Name));
        Assert.Equal("🎤", own.GetProperty("emoji").GetString());
        Assert.True(own.GetProperty("own").GetBoolean());

        Assert.Equal(JsonValueKind.Null, e.GetProperty("worn").GetProperty("anthem").ValueKind);
        var block = e.GetProperty("ownAnthem");
        Assert.Equal(["url", "title", "readyAt"], block.EnumerateObject().Select(p => p.Name));
        Assert.All(block.EnumerateObject(), p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));
    }

    // =============================================================================================
    // Купівля, подарунок, вдягання
    // =============================================================================================

    [Fact]
    public void Bought_anthem_is_worn_at_once_and_the_wallet_says_what_it_was()
    {
        Give("Оля", 1000);
        var r = Buy("Оля", "trembita");
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Трембіта", r.Message);
        Assert.Equal("trembita", _store.Worn("Оля", LavkaKind.Anthem));
        Assert.Equal("trembita", Views.Json(_lavka.View("Оля", true)).GetProperty("worn").GetProperty("anthem").GetString());
        Assert.Equal(-600, _eco.Paid("Оля", "shop:trembita"));
        Assert.Contains(_eco.Outbox.Of<WalletChanged>(), w => w.Text == "−600 черепків: Лавка — гімн «Трембіта»");

        // друга покупка — теж вдягається (собі), перша лишається в шафі
        Give("Оля", 300);
        Assert.True(Buy("Оля", "fanfare").Ok);
        Assert.Equal("fanfare", _store.Worn("Оля", LavkaKind.Anthem));
        Assert.True(_store.Owns("Оля", "trembita"));
    }

    [Fact]
    public void Ready_anthem_can_be_gifted_but_own_track_cannot()
    {
        Give("Оля", 5000);
        var gift = Buy("Оля", "bells", to: "Петро");
        Assert.True(gift.Ok, gift.Message);
        Assert.True(_store.Owns("Петро", "bells"));
        Assert.Equal("bells", _store.Worn("Петро", LavkaKind.Anthem));          // слот був порожній — вдягнуто
        Assert.Contains(_wire.Toasts, t => t.Nick == "Петро" && t.Text.Contains("гімн «Дзвони»"));

        var before = _eco.Economy.Balance("Оля");
        var own = Buy("Оля", LavkaCatalog.OwnAnthem, to: "Петро");
        Assert.False(own.Ok);
        Assert.Equal("Свій трек дарувати не можна — його ставлять собі", own.Message);
        Assert.Equal(before, _eco.Economy.Balance("Оля"));
        Assert.False(_store.Owns("Петро", LavkaCatalog.OwnAnthem));
    }

    [Fact]
    public void Gift_into_a_taken_slot_does_not_change_what_plays()
    {
        Give("Петро", 300);
        Assert.True(Buy("Петро", "dzen").Ok);
        Give("Оля", 1000);
        Assert.True(Buy("Оля", "hopak", to: "Петро").Ok);
        Assert.Equal("dzen", _store.Worn("Петро", LavkaKind.Anthem));
    }

    [Fact]
    public void Wearing_and_taking_off_an_anthem_goes_through_the_usual_wear_and_tells_nobody()
    {
        Give("Оля", 1000);
        Assert.True(Buy("Оля", "cosmos").Ok);
        _wire.Shown.Clear();

        Assert.True(_lavka.Wear("Оля", true, "anthem", null).Ok);
        Assert.Null(_store.Worn("Оля", LavkaKind.Anthem));
        Assert.Null(_lavka.AnthemOf("Оля"));
        Assert.True(_lavka.Wear("Оля", true, "anthem", "cosmos").Ok);
        Assert.Equal("cosmos", _store.Worn("Оля", LavkaKind.Anthem));
        Assert.Empty(_wire.Shown);                                              // гімн не малюється — вигляд усім не летить

        Assert.Equal(Lavka.NotOwned, _lavka.Wear("Оля", true, "anthem", "solemn").Message);
        Assert.Equal(Lavka.WrongSlot, _lavka.Wear("Оля", true, "anthem", "fox").Message);
        Assert.Equal(Lavka.WrongSlot, _lavka.Wear("Оля", true, "icon", "cosmos").Message);
        // у вигляді гімну нема зовсім — старі клієнти й Looks бачать усе як було
        Assert.Null(_lavka.LookOf("Оля"));
    }

    // =============================================================================================
    // Який гімн у людини
    // =============================================================================================

    [Fact]
    public async Task AnthemOf_ready_own_without_cut_own_with_cut_and_taken_down()
    {
        Assert.Null(_lavka.AnthemOf("Оля"));                                     // нічого не вдягнуто

        Give("Оля", 1000);
        Assert.True(Buy("Оля", "trembita").Ok);
        Assert.Equal(new AnthemPlay("Трембіта", "📯", "/static/anthems/trembita.mp3"), _lavka.AnthemOf("Оля"));

        OwnTrack("Оля");
        Assert.Equal(LavkaCatalog.OwnAnthem, _store.Worn("Оля", LavkaKind.Anthem));
        Assert.Null(_lavka.AnthemOf("Оля"));                                     // вдягнуто, але уривка ще нема — тиша

        var r = await Up("Оля", Mp3(5000), 30, 10, "  Наша  ");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(new AnthemPlay("Наша", "🎤", r.Url!), _lavka.AnthemOf("Оля"));
        Assert.StartsWith("/api/lavka/anthem/", r.Url);

        Assert.True(_anthems.TakeDown("Оля").Ok);
        Assert.Null(_lavka.AnthemOf("Оля"));                                     // адмін зняв — знову тиша
        Assert.Contains(_wire.Toasts, t => t.Nick == "Оля" && t.Text.Contains("Адмін зняв твій гімн — постав інший уривок"));
    }

    [Fact]
    public async Task Own_track_without_a_title_plays_as_svij_trek()
    {
        OwnTrack("Оля");
        Assert.True((await Up("Оля", Mp3(5000))).Ok);
        Assert.Equal("Свій трек", _lavka.AnthemOf("Оля")!.Title);
    }

    // =============================================================================================
    // Коли звучить
    // =============================================================================================

    void Wear(string nick, string anthem)
    {
        Give(nick, LavkaCatalog.Get(anthem)!.Price);
        Assert.True(Buy(nick, anthem).Ok);
    }

    [Fact]
    public void One_human_winner_with_an_anthem_gets_exactly_one_event_with_the_right_url()
    {
        Wear("Оля", "trembita");
        _eco.Events.Raise(_eco.Finished("r1", Ttt, ["Оля", "Петро"], [0], round: 3));

        var a = Assert.Single(Played);
        Assert.Equal(new Anthem("r1", 3, "Оля", "Трембіта", "📯", "/static/anthems/trembita.mp3"), a);
    }

    [Fact]
    public void Solo_coop_draw_and_no_winners_stay_silent()
    {
        Wear("Оля", "trembita");
        var solo = EconomyRig.Info("snake", "Змійка", "змійку", GameGroup.Solo, max: 1);
        var coop = Ttt with { Id = "snake-all", Coop = true };
        _eco.Events.Raise(_eco.Finished("s1", solo, ["Оля"], [0]));
        _eco.Events.Raise(_eco.Finished("c1", coop, ["Оля", "Петро"], [0, 1]));
        _eco.Events.Raise(_eco.Finished("d1", Ttt, ["Оля", "Петро"], [0, 1], draw: true));
        _eco.Events.Raise(_eco.Finished("e1", Ttt, ["Оля", "Петро"], []));
        Assert.Empty(Played);
    }

    [Fact]
    public void Bot_winner_stays_silent_but_a_win_over_a_bot_plays()
    {
        Wear("Оля", "fanfare");
        _eco.Events.Raise(_eco.Finished("r1", Ttt, ["Оля", null], [1]));          // виграв бот
        Assert.Empty(Played);
        _eco.Events.Raise(_eco.Finished("r1", Ttt, ["Оля", null], [0], round: 2)); // Оля виграла в бота
        Assert.Equal("Оля", Assert.Single(Played).Nick);
    }

    [Fact]
    public void Guest_and_winner_without_an_anthem_stay_silent()
    {
        _eco.Events.Raise(_eco.Finished("r1", Ttt, ["гість", "Петро"], [0]));
        _eco.Events.Raise(_eco.Finished("r2", Ttt, ["Оля", "Петро"], [1]));
        OwnTrack("Оля");                                                         // свій трек без уривка — теж тиша
        _eco.Events.Raise(_eco.Finished("r3", Ttt, ["Оля", "Петро"], [0]));
        Assert.Empty(Played);
    }

    [Fact]
    public void Several_winners_take_turns_by_round_in_seat_order()
    {
        Wear("Оля", "trembita");
        Wear("Андрій", "bayan");
        // Петро теж виграв, але гімну в нього нема — у колі лише Оля (місце 0) й Андрій (місце 3)
        for (var round = 1; round <= 4; round++)
            _eco.Events.Raise(_eco.Finished("m1", Mafia, ["Оля", "Петро", null, "Андрій", "гість Вася"], [3, 1, 0], round: round));

        Assert.Equal(["Андрій", "Оля", "Андрій", "Оля"], Played.Select(a => a.Nick));   // кандидати[Round % 2]
        Assert.Equal([1, 2, 3, 4], Played.Select(a => a.Round));
        Assert.Equal("Баян-туш", Played[0].Title);
    }

    [Fact]
    public void The_same_room_and_round_plays_only_once()
    {
        Wear("Оля", "solemn");
        var e = _eco.Finished("r1", Ttt, ["Оля", "Петро"], [0], round: 5);
        _eco.Events.Raise(e);
        _player.On(e);
        _eco.Events.Raise(e with { FinishedAt = e.FinishedAt.AddSeconds(1) });
        Assert.Single(Played);
        _eco.Events.Raise(e with { Round = 6 });                                 // наступний раунд — знову
        Assert.Equal(2, Played.Count);
        _eco.Events.Raise(e with { RoomId = "r2" });                             // інший стіл, той самий раунд — теж
        Assert.Equal(3, Played.Count);
    }

    [Fact]
    public async Task Own_track_plays_its_cut_with_the_given_title()
    {
        OwnTrack("Оля");
        var r = await Up("Оля", Mp3(5000), title: "Моя");
        _eco.Events.Raise(_eco.Finished("r1", Ttt, ["Петро", "Оля"], [1]));
        var a = Assert.Single(Played);
        Assert.Equal(("Моя", "🎤", r.Url), (a.Title, a.Emoji, a.Url));
    }

    [Fact]
    public void Seats_outside_the_table_and_negative_are_skipped()
    {
        Wear("Оля", "fanfare");
        // місце поза столом і від'ємне — просто пропускаються
        _player.On(_eco.Finished("r1", Ttt, ["Оля"], [5, -1, 0]));
        Assert.Single(Played);
    }

    [Fact]
    public void A_broken_subscriber_does_not_throw_out()
    {
        Wear("Оля", "fanfare");
        // база Лавки зламалась посеред вечора: Lavka.AnthemOf кидає SqliteException усередині обробника
        _eco.Db.Exec("DROP TABLE lavka_worn");
        Assert.ThrowsAny<Exception>(() => _lavka.AnthemOf("Оля"));

        var e = _eco.Finished("r1", Ttt, ["Оля", "Петро"], [0], round: 2);
        var thrown = Record.Exception(() => _player.On(e));                     // сам підписник
        Assert.Null(thrown);
        Assert.Null(Record.Exception(() => _eco.Events.Raise(e with { Round = 3 })));   // і через шину
        Assert.Empty(Played);
    }

    // =============================================================================================
    // Розсилка
    // =============================================================================================

    [Fact]
    public void Broadcaster_sends_anthem_to_the_table_group_with_the_wire_fields()
    {
        var sends = Broadcaster.Plan([new Anthem("r7", 3, "Оля", "Трембіта", "📯", "/static/anthems/trembita.mp3")],
            () => [], () => [], _ => null, _ => null, _ => [], (_, _) => new object());
        var s = Assert.Single(sends);
        Assert.Equal(new ToGroup("room:r7"), s.Target);
        Assert.Equal("anthem", s.Event);
        var body = Views.Json(s.Payload);
        Assert.Equal(["id", "round", "nick", "title", "emoji", "url"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("r7", body.GetProperty("id").GetString());
        Assert.Equal(3, body.GetProperty("round").GetInt32());
        Assert.Equal("Оля", body.GetProperty("nick").GetString());
        Assert.Equal("Трембіта", body.GetProperty("title").GetString());
        Assert.Equal("📯", body.GetProperty("emoji").GetString());
        Assert.Equal("/static/anthems/trembita.mp3", body.GetProperty("url").GetString());
    }

    [Fact]
    public void Broadcaster_also_reaches_seated_players_who_are_elsewhere_but_each_connection_once()
    {
        // За столом Оля (дві вкладки: c1 дивиться на стіл, c2 — в Ефірі) і Петро (c3, деінде); c4 — глядач.
        // Реалтайм-стіл поза «Іграми» браузер з групи виводить, тож c2 і c3 почують гімн лише за ніком.
        var b = new RoomBroadcast("r7", null!, false, ["Оля", null, "Петро", "Оля"], new Dictionary<int, object?>(), null, ["c1", "c4"]);
        var conns = new Dictionary<string, IReadOnlyList<string>> { ["Оля"] = ["c1", "c2"], ["Петро"] = ["c3"] };
        var sends = Broadcaster.Plan([new Anthem("r7", 3, "Оля", "Трембіта", "📯", "/static/anthems/trembita.mp3")],
            () => [], () => [], id => id == "r7" ? b : null, _ => null, n => conns.GetValueOrDefault(n) ?? [], (_, _) => new object());

        Assert.Equal(2, sends.Count);
        Assert.Equal(new ToGroup("room:r7"), sends[0].Target);
        var direct = Assert.IsType<ToConnections>(sends[1].Target);
        Assert.Equal(["c2", "c3"], direct.Ids);                                // c1 уже в групі, Оля на двох місцях — раз
        Assert.All(sends, x => Assert.Equal("anthem", x.Event));
        Assert.Equal(Views.Json(sends[0].Payload).GetRawText(), Views.Json(sends[1].Payload).GetRawText());

        // усі, хто сидить, і так у групі — другої адреси нема
        var all = b with { Watchers = ["c1", "c2", "c3"] };
        Assert.Single(Broadcaster.Plan([new Anthem("r7", 3, "Оля", "Т", "📯", "/x.mp3")],
            () => [], () => [], _ => all, _ => null, n => conns.GetValueOrDefault(n) ?? [], (_, _) => new object()));
    }

    // =============================================================================================
    // Свій трек: завантаження
    // =============================================================================================

    [Fact]
    public async Task Upload_needs_an_account_and_the_bought_track()
    {
        Assert.Equal(Lavka.NotAccount, (await Up("гість Вася", Mp3(5000), account: false)).Message);
        Assert.Equal("Спершу купи «Свій трек»", (await Up("Оля", Mp3(5000))).Message);
        Give("Оля", 1000);
        Assert.True(Buy("Оля", "trembita").Ok);                                  // готовий гімн — не те
        Assert.Equal(LavkaAnthems.NotOwned, (await Up("Оля", Mp3(5000))).Message);
        Assert.Equal(0, _cutter.Probes);
    }

    [Fact]
    public async Task Only_audio_or_video_by_magic_bytes_goes_to_ffmpeg()
    {
        OwnTrack("Оля");
        var html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>" + new string(' ', 500));
        Assert.Equal("Це не схоже на пісню чи відео", (await Up("Оля", html)).Message);
        var png = new byte[600];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        Assert.Equal(LavkaAnthems.NotSong, (await Up("Оля", png)).Message);
        Assert.Equal(LavkaAnthems.Empty, (await Up("Оля", [])).Message);
        Assert.Equal(0, _cutter.Probes);

        var mov = await Up("Оля", Mov(5000));                                   // відео з айфона — годиться
        Assert.True(mov.Ok, mov.Message);
        // жодних тимчасових файлів по собі — лише вирізаний mp3
        Assert.Equal([Path.GetFileName(mov.Url!)], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Ffprobe_rules_sound_and_length()
    {
        OwnTrack("Оля");
        _cutter.Probe = null;
        Assert.Equal(LavkaAnthems.Unreadable, (await Up("Оля", Mp3(5000))).Message);
        _cutter.Probe = new(false, 60);
        Assert.Equal(LavkaAnthems.NoSound, (await Up("Оля", Mp3(5000))).Message);
        _cutter.Probe = new(true, 2.9);
        Assert.Equal(LavkaAnthems.TooShort, (await Up("Оля", Mp3(5000))).Message);
        _cutter.Probe = new(true, 20 * 60 + 1);
        Assert.Equal(LavkaAnthems.TooLong, (await Up("Оля", Mp3(5000))).Message);
        Assert.Empty(_cutter.Cuts);
        Assert.Empty(Directory.GetFiles(_dir));                                  // .part прибрано і після відмови
    }

    [Fact]
    public void Ffprobe_json_prefers_the_audio_stream_duration_and_unknown_length_is_unreadable()
    {
        static AnthemProbe? P(string json) => FfmpegAnthemCutter.ParseProbe(json);
        Assert.Equal(new AnthemProbe(true, 12.5), P("""{"streams":[{"codec_type":"audio","duration":"12.5"}],"format":{"duration":"40.0"}}"""));
        // WebM доріжці тривалості не пише — береться тривалість файла
        Assert.Equal(new AnthemProbe(true, 8.008), P("""{"streams":[{"codec_type":"audio"}],"format":{"duration":"8.008"}}"""));
        // файл N/A, доріжка знає — годиться
        Assert.Equal(new AnthemProbe(true, 30), P("""{"streams":[{"codec_type":"audio","duration":"30.000000"}],"format":{}}"""));
        // обидві невідомі — «не прочитали», а не «закороткий»
        Assert.Null(P("""{"streams":[{"codec_type":"audio","duration":"N/A"}],"format":{"duration":"N/A"}}"""));
        Assert.Null(P("""{"streams":[{"codec_type":"audio"}],"format":{}}"""));
        Assert.Equal(new AnthemProbe(false, 0), P("""{"streams":[],"format":{"duration":"60"}}"""));
        Assert.Null(P("не json"));
    }

    [Fact]
    public async Task Stale_parts_and_tmps_older_than_an_hour_are_swept_before_a_new_cut()
    {
        Directory.CreateDirectory(_dir);
        string Put(string name, TimeSpan age)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            File.SetLastWriteTimeUtc(path, (Clock.UtcNow - age).UtcDateTime);
            return path;
        }
        var oldPart = Put(".up-0123456789abcdef0123456789abcdef.part", TimeSpan.FromHours(2));
        var oldTmp = Put("0123456789abcdef-0123456789ab-0-10000.mp3.tmp", TimeSpan.FromMinutes(61));
        var freshPart = Put(".up-fedcba9876543210fedcba9876543210.part", TimeSpan.FromMinutes(10));   // хтось саме вантажить
        var oldCut = Put("0123456789abcdef-0123456789ab-0-10000.mp3", TimeSpan.FromDays(30));     // вирізаний гімн — не сміття
        var stranger = Put("notes.txt", TimeSpan.FromDays(30));

        _anthems.Sweep();
        Assert.False(File.Exists(oldPart));
        Assert.False(File.Exists(oldTmp));
        Assert.True(File.Exists(freshPart));
        Assert.True(File.Exists(oldCut));
        Assert.True(File.Exists(stranger));

        // година минула — і свіжий теж покинутий; прибирає вже сам новий уривок
        Clock.Advance(TimeSpan.FromMinutes(51));
        OwnTrack("Оля");
        Assert.True((await Up("Оля", Mp3(5000))).Ok);
        Assert.False(File.Exists(freshPart));
    }

    [Theory]
    [InlineData(30.0, 10.0, 180.0, 30.0, 10.0)]
    [InlineData(30.0, 2.0, 180.0, 30.0, 5.0)]        // коротше 5 — 5
    [InlineData(30.0, 40.0, 180.0, 30.0, 15.0)]      // довше 15 — 15
    [InlineData(175.0, 10.0, 180.0, 170.0, 10.0)]    // за кінцем — до кінця мінус довжина
    [InlineData(-4.0, 10.0, 180.0, 0.0, 10.0)]
    [InlineData(null, null, 180.0, 0.0, 10.0)]       // не надіслали — з початку, 10 с
    [InlineData(5.0, 15.0, 4.0, 0.0, 4.0)]           // файл на 4 с грає весь
    [InlineData(double.NaN, double.PositiveInfinity, 60.0, 0.0, 10.0)]
    public void Start_and_len_are_clamped(double? start, double? len, double duration, double wantStart, double wantLen)
    {
        Assert.Equal((wantStart, wantLen), LavkaAnthems.Clamp(start, len, duration));
    }

    [Fact]
    public async Task Upload_cuts_clamped_piece_and_names_the_file_by_nick_content_start_and_len()
    {
        OwnTrack("Оля");
        _cutter.Probe = new(true, 100);
        var r = await Up("Оля", Mp3(5000), 97.25, 40, "Тест");
        Assert.True(r.Ok, r.Message);
        Assert.Equal("Гімн стоїть — зазвучить за столом, коли виграєш", r.Message);
        Assert.Equal((85.0, 15.0), Assert.Single(_cutter.Cuts));
        Assert.Matches("^/api/lavka/anthem/[0-9a-f]{16}-[0-9a-f]{12}-85000-15000\\.mp3$", r.Url);
        Assert.Equal("Тест", r.Title);
        Assert.Equal(Clock.UtcNow + Lavka.AnthemGap, r.ReadyAt);
        var row = _store.Anthem("Оля")!;
        Assert.Equal((85000, 15000, "Тест"), (row.StartMs, row.LenMs, row.Title));
        Assert.True(File.Exists(Path.Combine(_dir, Path.GetFileName(r.Url!))));
    }

    [Fact]
    public async Task New_cut_waits_two_minutes_and_replaces_the_old_file()
    {
        OwnTrack("Оля");
        var first = await Up("Оля", Mp3(5000), 10, 10);
        Assert.True(first.Ok);

        var early = await Up("Оля", Mp3(5000), 20, 10);
        Assert.False(early.Ok);
        Assert.Equal("Новий уривок можна буде за 2 хв", early.Message);
        Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("Новий уривок можна буде за 1 хв", (await Up("Оля", Mp3(5000), 20, 10)).Message);
        Assert.Single(_cutter.Cuts);

        Clock.Advance(TimeSpan.FromSeconds(60));
        var second = await Up("Оля", Mp3(5000), 20, 10);
        Assert.True(second.Ok, second.Message);
        Assert.NotEqual(first.Url, second.Url);                                 // нова адреса — кеш immutable не заважає
        Assert.Equal([Path.GetFileName(second.Url!)], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Ffmpeg_failure_is_short_and_without_paths_and_changes_nothing()
    {
        OwnTrack("Оля");
        _cutter.Fail = $"ffmpeg 1: {_dir}/secret.part: Invalid data found";
        var r = await Up("Оля", Mp3(5000));
        Assert.False(r.Ok);
        Assert.Equal(LavkaAnthems.CutFailed, r.Message);
        Assert.DoesNotContain(_dir, r.Message);
        Assert.Null(_store.Anthem("Оля"));
        Assert.Null(_lavka.ReadyAt("Оля", LavkaCatalog.OwnAnthem));             // перерва не почалась — можна одразу ще
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Too_big_upload_is_refused_by_length_and_while_reading()
    {
        OwnTrack("Оля");
        var r = await _anthems.SetAsync("Оля", true, 0, 10, null, LavkaAnthems.MaxBytes + 1, new MemoryStream(Mp3(10)), default);
        Assert.Equal("Завеликий файл — до 40 МБ", r.Message);
        // браузер збрехав про довжину — рахуємо самі
        var big = new MemoryStream(new byte[LavkaAnthems.MaxBytes + 10]);
        Assert.Equal(LavkaAnthems.TooBig, (await _anthems.SetAsync("Оля", true, 0, 10, null, null, big, default)).Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Title_is_trimmed_cleaned_and_capped_at_forty()
    {
        Assert.Null(LavkaAnthems.CleanTitle("   "));
        Assert.Null(LavkaAnthems.CleanTitle(null));
        Assert.Equal("Гоп", LavkaAnthems.CleanTitle(" Го\u0007п\n "));
        Assert.Equal(new string('я', 40), LavkaAnthems.CleanTitle(new string('я', 55)));
        Assert.Equal(new string('я', 39), LavkaAnthems.CleanTitle(new string('я', 39) + "🎺🎺"));   // емодзі навпіл не ріжемо
    }

    // =============================================================================================
    // HTTP
    // =============================================================================================

    static HttpContext As(string nick, bool account = true, bool admin = false)
    {
        var c = Radio.As(nick, admin);
        if (account) c.Items["account"] = new Account(nick, "", "salt", admin ? "admin" : "member");
        return c;
    }

    static HttpContext Upload(HttpContext c, byte[] bytes, string query, string? title = null)
    {
        c.Request.Method = "POST";
        c.Request.QueryString = new QueryString(query);
        c.Request.Body = new MemoryStream(bytes);
        c.Request.ContentLength = bytes.Length;
        if (title is not null) c.Request.Headers["X-Anthem-Title"] = Uri.EscapeDataString(title);
        return c;
    }

    [Fact]
    public async Task Upload_endpoint_reads_query_with_a_dot_and_title_from_the_header()
    {
        OwnTrack("Оля");
        var (status, body) = Radio.Reply(await LavkaSetup.SetAnthem(Upload(As("Оля"), Mp3(5000), "?start=12.5&len=7.25", "Гоп «тест» 🎺"), _anthems));
        Assert.Equal(200, status);
        Assert.Equal(["ok", "message", "url", "title", "readyAt"], body.EnumerateObject().Select(p => p.Name));
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("Гоп «тест» 🎺", body.GetProperty("title").GetString());
        Assert.Equal((12.5, 7.25), Assert.Single(_cutter.Cuts));
        Assert.Equal(Clock.UtcNow + Lavka.AnthemGap, body.GetProperty("readyAt").GetDateTimeOffset());

        // вітрина бачить те саме
        var own = Views.Json(LavkaSetup.Shop(As("Оля"), _lavka)).GetProperty("ownAnthem");
        Assert.Equal(body.GetProperty("url").GetString(), own.GetProperty("url").GetString());
        Assert.Equal("Гоп «тест» 🎺", own.GetProperty("title").GetString());
        Assert.Equal(Clock.UtcNow + Lavka.AnthemGap, own.GetProperty("readyAt").GetDateTimeOffset());
        Assert.Equal("own-anthem", Views.Json(LavkaSetup.Shop(As("Оля"), _lavka)).GetProperty("worn").GetProperty("anthem").GetString());
    }

    [Fact]
    public async Task Upload_endpoint_refuses_a_guest_with_400_ok_false_and_message()
    {
        var (status, body) = Radio.Reply(await LavkaSetup.SetAnthem(Upload(As("гість Вася", account: false), Mp3(5000), "?start=0&len=10"), _anthems));
        Assert.Equal(400, status);
        Assert.Equal(["ok", "message"], body.EnumerateObject().Select(p => p.Name));
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal(Lavka.NotAccount, body.GetProperty("message").GetString());
    }

    [Fact]
    public void Profile_endpoint_gives_title_emoji_url_or_404()
    {
        Assert.Equal(404, ((IStatusCodeHttpResult)LavkaSetup.AnthemOf("Оля", _lavka)).StatusCode);
        Wear("Оля", "hopak");
        var (status, body) = Radio.Reply(LavkaSetup.AnthemOf("оля", _lavka));
        Assert.Equal(200, status);
        Assert.Equal(["title", "emoji", "url"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Гопак", body.GetProperty("title").GetString());
        Assert.Equal("💃", body.GetProperty("emoji").GetString());
        Assert.Equal("/static/anthems/hopak.mp3", body.GetProperty("url").GetString());
    }

    /// <summary>Виконати <see cref="LavkaSetup.AnthemFile"/> так, як це зробив би ASP.NET: код, заголовки, тіло.</summary>
    async Task<(int Status, IHeaderDictionary Headers, byte[] Body)> Get(string file)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await LavkaSetup.AnthemFile(file, _anthems, ctx).ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, ctx.Response.Headers, body.ToArray());
    }

    [Fact]
    public async Task Cut_is_served_as_mp3_nosniff_immutable_and_foreign_names_are_404()
    {
        OwnTrack("Оля");
        var r = await Up("Оля", Mp3(5000));
        var (status, headers, body) = await Get(Path.GetFileName(r.Url!));
        Assert.Equal(200, status);
        Assert.Equal("audio/mpeg", headers.ContentType.ToString());
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Contains("immutable", headers.CacheControl.ToString());
        Assert.Equal("bytes", headers.AcceptRanges.ToString());
        Assert.Equal(400, body.Length);

        File.WriteAllText(Path.Combine(_dir, "secret.txt"), "ні");
        Assert.Equal(404, (await Get("secret.txt")).Status);
        Assert.Equal(404, (await Get("../hlechyky.db")).Status);
        Assert.Equal(404, (await Get("0123456789abcdef-0123456789ab-0-10000.mp3")).Status);   // наше ім'я, але файла нема
    }

    [Fact]
    public async Task Admin_lists_and_takes_down_others_cannot()
    {
        OwnTrack("Оля");
        var r = await Up("Оля", Mp3(5000), title: "Наша");

        Assert.Equal(400, ((IStatusCodeHttpResult)LavkaSetup.AdminAnthems(As("Петро"), _anthems)).StatusCode);
        var (status, list) = Radio.Reply(LavkaSetup.AdminAnthems(As("Адмін", admin: true), _anthems));
        Assert.Equal(200, status);
        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal(["nick", "title", "url", "at"], item.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("Оля", "Наша", r.Url), (item.GetProperty("nick").GetString(), item.GetProperty("title").GetString(), item.GetProperty("url").GetString()));

        var (no, refused) = Radio.Reply(LavkaSetup.TakeDownAnthem(As("Петро"), new LavkaSetup.TakeDownRequest("Оля"), _anthems));
        Assert.Equal(400, no);
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.NotNull(_store.Anthem("Оля"));

        var (ok, done) = Radio.Reply(LavkaSetup.TakeDownAnthem(As("Адмін", admin: true), new LavkaSetup.TakeDownRequest("@оля"), _anthems));
        Assert.Equal(200, ok);
        Assert.Equal(["ok", "message"], done.EnumerateObject().Select(p => p.Name));
        Assert.Null(_store.Anthem("Оля"));
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Null(_lavka.ReadyAt("Оля", LavkaCatalog.OwnAnthem));             // новий уривок — одразу
        Assert.True(_store.Owns("Оля", LavkaCatalog.OwnAnthem));                 // річ лишилась, черепки не повертаються
        Assert.True((await Up("Оля", Mp3(6000))).Ok);

        Assert.False(_anthems.TakeDown("Петро").Ok);
        Assert.False(_anthems.TakeDown("  ").Ok);
    }

    // =============================================================================================
    // Свій трек: пісня з пошуку радіо
    // =============================================================================================

    const string Song1 = "dQw4w9WgXcQ";

    Task<AnthemSourceReply> Fetch(string nick, string? id, bool account = true) => _anthems.FetchAsync(nick, account, id, default);

    Task<LavkaAnthemReply> CutSong(string nick, string? id, double? start = null, double? len = null, string? title = null, bool account = true) =>
        _anthems.CutTrackAsync(nick, account, id, start, len, title, default);

    [Fact]
    public async Task Song_fetch_needs_an_account_and_an_honest_id()
    {
        _source.Song(Song1);
        Assert.Equal(Lavka.NotAccount, (await Fetch("гість Вася", Song1, account: false)).Message);
        foreach (var bad in new[] { null, "", "abc", "../hlechyky", "dQw4w9WgXc/", "dQw4w9WgXcQ.mp3", "dQw4w9WgXcQ ", "..%2F..%2Fdb" })
            Assert.Equal(LavkaAnthems.BadId, (await Fetch("Оля", bad)).Message);
        Assert.Equal(0, _source.Infos);                                          // до YouTube нічого не дійшло
        Assert.Equal(0, _cutter.Probes);
    }

    [Fact]
    public async Task Fetching_a_song_does_not_need_the_bought_track_but_cutting_does()
    {
        _source.Song(Song1, "Океан Ельзи", "Обійми");
        _cutter.Probe = new(true, 241.5);
        var r = await Fetch("Оля", Song1);                                       // «Свій трек» ще не куплено — слухати можна
        Assert.True(r.Ok, r.Message);
        Assert.Equal((Song1, "Обійми", "Океан Ельзи", 241.5, "/api/lavka/anthem/src/" + Song1), (r.Id, r.Title, r.Artist, r.Duration, r.PreviewUrl));
        Assert.Equal(1, _source.Downloads);
        Assert.NotNull(_anthems.SourcePath(Song1));

        Assert.Equal(LavkaAnthems.NotOwned, (await CutSong("Оля", Song1, 30, 10)).Message);
        Assert.Empty(_cutter.Cuts);
        OwnTrack("Оля");
        var cut = await CutSong("Оля", Song1, 30, 10);
        Assert.True(cut.Ok, cut.Message);
        Assert.Equal((30.0, 10.0), Assert.Single(_cutter.Cuts));
    }

    [Fact]
    public async Task Fetch_quota_is_ten_downloads_an_hour_and_the_cache_is_free()
    {
        _source.Put(_source.Song(Song1));
        for (var i = 0; i < 15; i++) Assert.True((await Fetch("Оля", Song1)).Ok);   // уже в кеші — не скачування
        Assert.Equal(0, _source.Downloads);

        string Id(int i) => $"song{i:D7}";
        for (var i = 0; i < LavkaAnthems.FetchesPerHour; i++)
        {
            _source.Song(Id(i));
            Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True((await Fetch("Оля", Id(i))).Ok);
        }
        _source.Song(Id(99));
        var no = await Fetch("Оля", Id(99));
        Assert.False(no.Ok);
        Assert.Equal("За годину можна взяти 10 пісень — наступну за 51 хв (або обери файл з телефона)", no.Message);
        Assert.Equal(LavkaAnthems.FetchesPerHour, _source.Downloads);
        Assert.True((await Fetch("оля", Id(3))).Ok);                             // скачане раніше — можна й понад ліміт
        Assert.True((await Fetch("Петро", Id(99))).Ok);                          // ліміт — свій у кожного
        Clock.Advance(TimeSpan.FromMinutes(51));
        _source.Song(Id(98));
        Assert.True((await Fetch("Оля", Id(98))).Ok);
    }

    [Fact]
    public async Task Fetch_failures_are_short_and_too_long_songs_are_not_downloaded()
    {
        Assert.Equal(LavkaAnthems.NoSuchSong, (await Fetch("Оля", Song1)).Message);
        _source.Song(Song1, seconds: 21 * 60);
        Assert.Equal("Задовга пісня — до 20 хвилин", (await Fetch("Оля", Song1)).Message);
        Assert.Equal(0, _source.Downloads);

        _source.Song(Song1);
        _source.Fail = "ERROR: /srv/radio/cache/x.part: HTTP Error 403";
        var r = await Fetch("Оля", Song1);
        Assert.Equal("Не вдалося взяти пісню — спробуй ще", r.Message);
        Assert.DoesNotContain("/srv", r.Message);

        _source.Fail = null;
        _cutter.Probe = new(true, 20 * 60 + 5);                                 // база збрехала про тривалість — ffprobe ні
        Assert.Equal(LavkaAnthems.TooLongSong, (await Fetch("Оля", Song1)).Message);
        _cutter.Probe = null;
        Assert.Equal(LavkaAnthems.SourceFailed, (await Fetch("Оля", Song1)).Message);
    }

    [Fact]
    public async Task Cut_from_a_cached_song_follows_the_upload_rules_and_titles_it_artist_dash_title()
    {
        OwnTrack("Оля");
        _source.Put(_source.Song(Song1, "Океан Ельзи", "Обійми"));
        _cutter.Probe = new(true, 100);
        var r = await CutSong("Оля", Song1, 97.25, 40);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(LavkaAnthems.Saved, r.Message);
        Assert.Equal((85.0, 15.0), Assert.Single(_cutter.Cuts));
        Assert.Matches("^/api/lavka/anthem/[0-9a-f]{16}-[0-9a-f]{12}-85000-15000\\.mp3$", r.Url);
        Assert.Equal("Океан Ельзи — Обійми", r.Title);
        Assert.Equal(Clock.UtcNow + Lavka.AnthemGap, r.ReadyAt);
        Assert.Equal("Океан Ельзи — Обійми", _lavka.AnthemOf("Оля")!.Title);
        Assert.Equal([Path.GetFileName(r.Url!)], Directory.GetFiles(_dir).Select(Path.GetFileName));   // лише вирізаний mp3
        Assert.True(File.Exists(_source.Cache[Song1].Path));                     // пісня лишилась у кеші радіо

        Assert.Equal("Новий уривок можна буде за 2 хв", (await CutSong("Оля", Song1, 10, 10, "Інша")).Message);
        Clock.Advance(Lavka.AnthemGap + TimeSpan.FromSeconds(1));
        var named = await CutSong("Оля", Song1, 10, 10, "  Моя  ");
        Assert.True(named.Ok, named.Message);
        Assert.Equal("Моя", named.Title);
        Assert.Equal([Path.GetFileName(named.Url!)], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Default_title_is_artist_dash_title_capped_at_forty_without_halving_emoji()
    {
        Assert.Equal("Гурт — Пісня", LavkaAnthems.DefaultTitle("Гурт", "Пісня"));
        Assert.Equal("Пісня", LavkaAnthems.DefaultTitle(" ", "Пісня"));
        Assert.Equal("Гурт", LavkaAnthems.DefaultTitle("Гурт", null));
        Assert.Null(LavkaAnthems.DefaultTitle(null, " "));
        Assert.Equal(40, LavkaAnthems.DefaultTitle(new string('а', 30), new string('б', 30))!.Length);
        Assert.Equal(new string('а', 36) + " — Х", LavkaAnthems.DefaultTitle(new string('а', 36), "Х"));        // рівно 40 — цілком
        Assert.Equal(new string('а', 34) + " — 🎺", LavkaAnthems.DefaultTitle(new string('а', 34), "🎺🎺"));   // 40-й знак — пів емодзі
    }

    [Fact]
    public async Task Missing_song_file_gives_a_friendly_try_again()
    {
        OwnTrack("Оля");
        Assert.Equal(LavkaAnthems.SourceFailed, (await CutSong("Оля", Song1)).Message);   // ніколи не брали
        Assert.Equal(LavkaAnthems.BadId, (await CutSong("Оля", "../../db")).Message);

        var song = _source.Put(_source.Song(Song1));
        File.Delete(song.Path);                                                  // TrackCache прибрав, поки людина слухала
        Assert.Equal("Не вдалося взяти пісню — спробуй ще", (await CutSong("Оля", Song1)).Message);
        Assert.Null(_anthems.SourcePath(Song1));

        _source.Put(_source.Song(Song1));
        _cutter.BeforeCut = File.Delete;                                         // зникло посеред нарізки
        _cutter.Fail = "ffmpeg 1: No such file or directory";
        Assert.Equal(LavkaAnthems.SourceFailed, (await CutSong("Оля", Song1)).Message);
        Assert.Null(_store.Anthem("Оля"));
        Assert.Null(_lavka.ReadyAt("Оля", LavkaCatalog.OwnAnthem));             // перерва не почалась — пробуй одразу
        Assert.Empty(Directory.GetFiles(_dir));
    }

    static HttpContext Json(HttpContext c)
    {
        c.Request.Method = "POST";
        return c;
    }

    [Fact]
    public async Task Fetch_and_cut_endpoints_give_the_documented_shapes()
    {
        _source.Song(Song1, "Гурт", "Пісня");
        _cutter.Probe = new(true, 120);
        var (status, body) = Radio.Reply(await LavkaSetup.FetchAnthemSource(Json(As("Оля")), new LavkaSetup.AnthemFetchRequest(Song1), _anthems));
        Assert.Equal(200, status);
        Assert.Equal(["ok", "message", "id", "title", "artist", "duration", "previewUrl"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(120, body.GetProperty("duration").GetDouble());
        Assert.Equal("/api/lavka/anthem/src/" + Song1, body.GetProperty("previewUrl").GetString());

        var (guest, refused) = Radio.Reply(await LavkaSetup.FetchAnthemSource(Json(As("гість Вася", account: false)), new LavkaSetup.AnthemFetchRequest(Song1), _anthems));
        Assert.Equal(400, guest);
        Assert.Equal(["ok", "message"], refused.EnumerateObject().Select(p => p.Name));

        var (notOwned, why) = Radio.Reply(await LavkaSetup.CutAnthemTrack(Json(As("Оля")), new LavkaSetup.AnthemTrackRequest(Song1, 10, 10, null), _anthems));
        Assert.Equal(400, notOwned);
        Assert.Equal(LavkaAnthems.NotOwned, why.GetProperty("message").GetString());

        OwnTrack("Оля");
        var (ok, cut) = Radio.Reply(await LavkaSetup.CutAnthemTrack(Json(As("Оля")), new LavkaSetup.AnthemTrackRequest(Song1, 12.5, 7, "Гоп 🎺"), _anthems));
        Assert.Equal(200, ok);
        Assert.Equal(["ok", "message", "url", "title", "readyAt"], cut.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Гоп 🎺", cut.GetProperty("title").GetString());
        Assert.Equal((12.5, 7.0), Assert.Single(_cutter.Cuts));
    }

    /// <summary>Виконати <see cref="LavkaSetup.AnthemSource"/> так, як це зробив би ASP.NET.</summary>
    async Task<(int Status, IHeaderDictionary Headers, byte[] Body)> GetSource(string id, bool account = true)
    {
        var ctx = As(account ? "Оля" : "гість Вася", account);
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        var body = new MemoryStream();
        ctx.Response.Body = body;
        await LavkaSetup.AnthemSource(id, _anthems, ctx).ExecuteAsync(ctx);
        return (ctx.Response.StatusCode, ctx.Response.Headers, body.ToArray());
    }

    [Fact]
    public async Task Whole_song_preview_is_for_accounts_by_strict_id_with_ranges_and_short_cache()
    {
        _source.Put(_source.Song(Song1));
        var (status, headers, body) = await GetSource(Song1);
        Assert.Equal(200, status);
        Assert.Equal("audio/mp4", headers.ContentType.ToString());
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Equal("private, max-age=300", headers.CacheControl.ToString());
        Assert.Equal("bytes", headers.AcceptRanges.ToString());
        Assert.Equal(300, body.Length);

        Assert.Equal(404, (await GetSource(Song1, account: false)).Status);    // гостю — ні
        Assert.Equal(404, (await GetSource("../hlechyky")).Status);
        Assert.Equal(404, (await GetSource("song0000000")).Status);             // наше ім'я, але в кеші нема
    }

    // =============================================================================================
    // Справжній ffmpeg (Skip, коли його нема)
    // =============================================================================================

    static string? FfmpegDir()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var dirs = new[] { Environment.GetEnvironmentVariable("HLECHYKY_FFMPEG_DIR"), "D:/or/tools/yt-dlp" }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var d in dirs)
            if (!string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "ffmpeg" + exe)) && File.Exists(Path.Combine(d, "ffprobe" + exe))) return d;
        return null;
    }

    public sealed class FfmpegFactAttribute : FactAttribute
    {
        public FfmpegFactAttribute() { if (FfmpegDir() is null) Skip = "нема ffmpeg і ffprobe (HLECHYKY_FFMPEG_DIR чи PATH)"; }
    }

    [FfmpegFact]
    public async Task Real_ffmpeg_probes_cuts_and_normalizes_a_sine()
    {
        var dir = FfmpegDir()!;
        var cutter = new FfmpegAnthemCutter(new FixedOptions<YtDlpOptions>(new YtDlpOptions { FfmpegDir = dir }));
        Directory.CreateDirectory(_dir);
        // 30 с синуса у WAV — лише файл на диску, нічого не грає
        var src = Path.Combine(_dir, "src.wav");
        var psi = new ProcessStartInfo(Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=30", "-ac", "1", src })
            psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!) await p.WaitForExitAsync();

        var probe = await cutter.ProbeAsync(src, default);
        Assert.NotNull(probe);
        Assert.True(probe!.HasAudio);
        Assert.InRange(probe.Seconds, 29.5, 30.5);

        var anthems = new LavkaAnthems(_store, _lavka, _wire, Clock, new AnthemDir(_dir), cutter, _source, NullLogger<LavkaAnthems>.Instance);
        OwnTrack("Оля");
        LavkaAnthemReply r;
        await using (var upload = File.OpenRead(src)) r = await anthems.SetAsync("Оля", true, 25, 10, null, null, upload, default);
        Assert.True(r.Ok, r.Message);
        Assert.EndsWith("-20000-10000.mp3", r.Url);                              // 25 + 10 за кінцем — зсунуто до 20
        var cut = await cutter.ProbeAsync(Path.Combine(_dir, Path.GetFileName(r.Url!)), default);
        Assert.NotNull(cut);
        Assert.True(cut!.HasAudio);
        Assert.InRange(cut.Seconds, 9.5, 10.5);

        // не звук — ffprobe бачить, що доріжки нема чи файл не читається
        var text = Path.Combine(_dir, "x.txt");
        File.WriteAllText(text, "просто текст");
        Assert.True(await cutter.ProbeAsync(text, default) is null or { HasAudio: false });

        // m4a (AAC у MP4, як із телефона чи з кешу радіо) — під списком демуксерів теж читається й ріжеться
        var m4a = Path.Combine(_dir, "src.m4a");
        Assert.Equal(0, await Ffmpeg(dir, "-f", "lavfi", "-i", "sine=frequency=330:duration=12", "-c:a", "aac", m4a));
        var mp4 = await cutter.ProbeAsync(m4a, default);
        Assert.NotNull(mp4);
        Assert.True(mp4!.HasAudio);
        Assert.InRange(mp4.Seconds, 11.5, 12.5);
        var piece = Path.Combine(_dir, "piece.mp3");
        Assert.Null(await cutter.CutAsync(m4a, piece, 2, 6, default));
        Assert.InRange((await cutter.ProbeAsync(piece, default))!.Seconds, 5.5, 6.5);

        // плейлист під виглядом пісні: HLS-демуксера в списку нема — ffprobe відмовляє, нікуди не ходячи
        var m3u = Path.Combine(_dir, "evil.m3u8");
        File.WriteAllText(m3u, $"#EXTM3U\n#EXT-X-TARGETDURATION:30\n#EXTINF:30,\n{src}\n#EXT-X-ENDLIST\n");
        Assert.Null(await cutter.ProbeAsync(m3u, default));
        Assert.NotNull(await cutter.CutAsync(m3u, Path.Combine(_dir, "evil.mp3"), 0, 5, default));
    }

    /// <summary>ffmpeg без звуку: лише пише файл, код виходу.</summary>
    static async Task<int> Ffmpeg(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-v", "error", "-nostdin", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
