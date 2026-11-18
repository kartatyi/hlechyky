using System.Buffers.Binary;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Здам хату»: банк квартир від гравців (перевірки фото, схвалення, зняття), шкала балів і сама партія — фази,
/// приховані числа, автор за столом, черепки лише за квартири, бачені вперше.
/// </summary>
public class KhataTests
{
    const int MaxTicks = 600;

    /// <summary>Найменший «PNG», який пропускає перевірка заголовка: підпис + IHDR із розмірами; salt — щоб хеші різнились.</summary>
    static byte[] Png(int salt, int w = 800, int h = 600)
    {
        var b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), (uint)w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), (uint)h);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(40), salt);
        return b;
    }

    sealed class Bank : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "khata-test-" + Guid.NewGuid().ToString("n")[..8]);
        public KhataStore Store { get; }
        public List<(string Nick, int Shards, string Ref)> Rewards { get; } = [];
        int _salt;

        public Bank(Db? db = null)
        {
            Store = new KhataStore(db, Dir, new FakeClock()) { Reward = (nick, n, r) => Rewards.Add((nick, n, r)) };
        }

        /// <summary>Схвалена квартира від «Глека» (розробника): одразу в грі.</summary>
        public KhataFlat Flat(string city, int price, string region = KhataRegions.Ua, string by = "Глек") =>
            Store.Add(by, true, true, region, city, price, [Png(++_salt)]).Flat!;

        public byte[] Photo() => Png(++_salt);

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static RoomHarness Table(Bank bank, int players = 2, object? options = null, int seed = 7, IServiceProvider? services = null)
    {
        var h = new RoomHarness("khata", options: options, seed: seed, services: services ?? RoomHarness.WithService(bank.Store));
        string[] nicks = ["Оля", "Петро", "Ганна", "Іван"];
        for (var i = 0; i < players; i++) h.Join(nicks[i]);
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static long Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt64();

    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < MaxTicks && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++) h.Tick(1);
        Assert.Equal(phase, Phase(h));
    }

    /// <summary>Квартира, що зараз на столі (місто в кожної своє).</summary>
    static KhataFlat Current(RoomHarness h, Bank bank)
    {
        var city = h.View(null).GetProperty("city").GetString();
        return bank.Store.Live.Single(f => f.City == city);
    }

    // ------------------------------------------------------------------------------------------
    // шкала балів і читання числа
    // ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(17500, 17500, 1000)]
    [InlineData(17800, 17500, 1000)]   // +1,7 % — ще «в яблучко»
    [InlineData(17200, 17500, 1000)]   // −1,7 %
    [InlineData(19250, 17500, 797)]    // +10 %
    [InlineData(14000, 17500, 543)]    // −20 %
    [InlineData(21875, 17500, 543)]    // +25 % — те саме «у 1,25 раза», що й −20 %
    [InlineData(35000, 17500, 133)]    // удвічі більше
    [InlineData(8750, 17500, 133)]     // удвічі менше — так само далеко
    [InlineData(52500, 17500, 39)]     // утричі
    public void Points_fall_smoothly_with_the_ratio(double guess, double price, int points) =>
        Assert.Equal(points, Khata.Points(guess, price));

    [Fact]
    public void Points_are_zero_for_nonsense_and_never_rise_with_distance()
    {
        Assert.Equal(0, Khata.Points(0, 1000));
        Assert.Equal(0, Khata.Points(-5, 1000));
        Assert.Equal(0, Khata.Points(double.NaN, 1000));
        Assert.Equal(0, Khata.Points(double.PositiveInfinity, 1000));
        Assert.Equal(0, Khata.Points(1000, 0));
        var last = Khata.Max;
        for (var g = 1000.0; g < 100_000; g *= 1.07)
        {
            var p = Khata.Points(g, 1000);
            Assert.True(p <= last, $"{g}: {p} > {last}");
            last = p;
        }
        Assert.Equal(0, last);
    }

    [Theory]
    [InlineData("15000", 15000)]
    [InlineData("15 000", 15000)]
    [InlineData("15 000 ₴", 15000)]
    [InlineData("$1200", 1200)]
    [InlineData("15к", 15000)]
    [InlineData("15k", 15000)]
    [InlineData("1,5 тис", 1500)]
    [InlineData("12.5К", 12500)]
    [InlineData("800 грн", 800)]
    public void Price_is_read_the_way_people_write_it(string raw, double value) => Assert.Equal(value, Khata.Text(raw));

    [Theory]
    [InlineData("")]
    [InlineData("дорого")]
    [InlineData("-500")]
    [InlineData("1e9")]
    [InlineData("к")]
    public void Not_a_price_is_not_read(string raw) => Assert.Null(Khata.Text(raw));

    [Fact]
    public void Regions_option_parses_to_a_set_or_everything()
    {
        Assert.Null(KhataRegions.Parse(null));
        Assert.Null(KhataRegions.Parse("all"));
        Assert.Null(KhataRegions.Parse("ua,all"));
        Assert.Null(KhataRegions.Parse("ua,eu,world"));
        Assert.Null(KhataRegions.Parse("mars"));
        Assert.Equal(["eu", "ua"], KhataRegions.Parse("ua,eu")!.Order());
        Assert.Equal("₴", KhataRegions.Unit("ua"));
        Assert.Equal("$", KhataRegions.Unit("eu"));
        Assert.Equal("$", KhataRegions.Unit("world"));
    }

    // ------------------------------------------------------------------------------------------
    // банк квартир
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_flat_from_a_player_waits_for_approval_and_pays_the_author_once()
    {
        using var bank = new Bank();
        var r = bank.Store.Add("Оля", true, false, "ua", "  Львів  ", 17500, [bank.Photo(), bank.Photo()]);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("Львів", r.Flat!.City);
        Assert.Equal(KhataFlat.Pending, r.Flat.Status);
        Assert.Equal(2, r.Flat.Photos.Count);
        Assert.Empty(bank.Store.Live);
        Assert.Equal(1, bank.Store.PendingCount);
        Assert.Empty(bank.Rewards);
        Assert.All(r.Flat.Photos, f => Assert.NotNull(bank.Store.Resolve(f)));

        Assert.True(bank.Store.Approve(r.Flat.Id).Ok);
        Assert.Single(bank.Store.Live);
        Assert.Equal(0, bank.Store.PendingCount);
        Assert.Equal([("Оля", KhataStore.AuthorReward, $"khata-flat:{r.Flat.Id}")], bank.Rewards);

        Assert.True(bank.Store.Approve(r.Flat.Id).Ok);   // вдруге — «вже в грі», без другої виплати
        Assert.Single(bank.Rewards);
    }

    [Fact]
    public void A_flat_from_the_admin_goes_live_at_once()
    {
        using var bank = new Bank();
        var flat = bank.Flat("Київ", 25000);
        Assert.Equal(KhataFlat.Ok, flat.Status);
        Assert.Single(bank.Store.Live);
        Assert.Single(bank.Rewards);
        Assert.Equal("₴", flat.Unit);
    }

    [Fact]
    public void Add_checks_everything_it_is_given()
    {
        using var bank = new Bank();
        var ok = bank.Photo();
        KhataReply Add(bool account = true, string? region = "ua", string? city = "Одеса", long price = 12000, params byte[][] photos) =>
            bank.Store.Add("Оля", account, false, region, city, price, photos);

        Assert.False(Add(account: false, photos: ok).Ok);
        Assert.False(Add(region: "mars", photos: ok).Ok);
        Assert.False(Add(city: " ", photos: ok).Ok);
        Assert.False(Add(city: new string('я', KhataStore.MaxCity + 1), photos: ok).Ok);
        Assert.False(Add(price: 0, photos: ok).Ok);
        Assert.False(Add(price: KhataStore.MaxPrice + 1L, photos: ok).Ok);
        Assert.False(Add().Ok);                                                                   // без фото
        Assert.False(Add(photos: [.. Enumerable.Range(0, KhataStore.MaxPhotos + 1).Select(_ => bank.Photo())]).Ok);
        Assert.False(Add(photos: [new byte[64]]).Ok);                                             // не картинка
        Assert.False(Add(photos: [Png(9001, w: KhataStore.MaxSide + 1)]).Ok);
        Assert.False(Add(photos: [Png(9002, w: 100, h: 100)]).Ok);
        Assert.False(Add(photos: [new byte[KhataStore.MaxBytes + 1]]).Ok);
        Assert.False(Add(photos: [ok, ok]).Ok);                                                   // те саме фото двічі
        Assert.Empty(bank.Store.Mine("Оля"));
        Assert.False(Directory.Exists(bank.Dir) && Directory.EnumerateFiles(bank.Dir).Any());

        Assert.True(Add(photos: ok).Ok);
        var again = Add(city: "Харків", photos: ok);                                              // те саме фото в іншій квартирі
        Assert.False(again.Ok);
        Assert.Contains("вже є", again.Message);
    }

    [Fact]
    public void Only_so_many_flats_of_one_author_may_wait()
    {
        using var bank = new Bank();
        for (var i = 0; i < KhataStore.MaxPending; i++)
            Assert.True(bank.Store.Add("Оля", true, false, "ua", "Місто" + i, 1000 + i, [bank.Photo()]).Ok);
        Assert.False(bank.Store.Add("Оля", true, false, "ua", "Ще одне", 5000, [bank.Photo()]).Ok);
        Assert.True(bank.Store.Add("Петро", true, false, "ua", "Ще одне", 5000, [bank.Photo()]).Ok);   // чужа черга — своя
    }

    [Fact]
    public void The_author_removes_their_own_flat_the_admin_any_and_files_go_too()
    {
        using var bank = new Bank();
        var mine = bank.Store.Add("Оля", true, false, "eu", "Лісабон", 1200, [bank.Photo()]).Flat!;
        var other = bank.Flat("Київ", 25000, by: "Петро");
        var file = mine.Photos[0];

        Assert.False(bank.Store.Remove(other.Id, "Оля", true, false).Ok);     // чужу — ні
        Assert.False(bank.Store.Remove(mine.Id, "Оля", false, false).Ok);     // гість із тим самим ніком — ні
        Assert.True(bank.Store.Remove(mine.Id, " оля ", true, false).Ok);      // нік без регістру й пробілів
        Assert.Null(bank.Store.Resolve(file));
        Assert.Empty(bank.Store.Mine("Оля"));

        Assert.True(bank.Store.Remove(other.Id, "Глек", true, true).Ok);
        Assert.Empty(bank.Store.Live);
        Assert.False(bank.Store.Remove(other.Id, "Глек", true, true).Ok);     // вже нема
    }

    [Fact]
    public void The_admin_fixes_a_typo_without_touching_the_photos()
    {
        using var bank = new Bank();
        var flat = bank.Flat("Лвів", 175000);
        var r = bank.Store.Edit(flat.Id, "ua", "Львів", 17500);
        Assert.True(r.Ok);
        Assert.Equal(("Львів", 17500), (bank.Store.Live[0].City, bank.Store.Live[0].Price));
        Assert.Equal(flat.Photos, bank.Store.Live[0].Photos);
        Assert.False(bank.Store.Edit(flat.Id, "ua", "", 17500).Ok);
        Assert.False(bank.Store.Edit(999, "ua", "Львів", 17500).Ok);
    }

    [Theory]
    [InlineData("../secret.jpg")]
    [InlineData("0123456789abcdef0123.gif")]
    [InlineData("0123456789ABCDEF0123.jpg")]
    [InlineData("")]
    public void A_strange_file_name_resolves_to_nothing(string file)
    {
        using var bank = new Bank();
        Assert.Null(bank.Store.Resolve(file));
    }

    [Fact]
    public void The_bank_survives_a_restart()
    {
        using var temp = new TempDb();
        using var bank = new Bank(temp.Db);
        var live = bank.Flat("Київ", 25000);
        var waits = bank.Store.Add("Оля", true, false, "world", "Токіо", 900, [bank.Photo(), bank.Photo()]).Flat!;
        var gone = bank.Flat("Суми", 9000);
        bank.Store.Remove(gone.Id, "Глек", true, true);

        var again = new KhataStore(temp.Db, bank.Dir, new FakeClock());
        Assert.Equal([live.Id], again.Live.Select(f => f.Id));
        Assert.Equal(1, again.PendingCount);
        var back = again.Everything().Single(f => f.Id == waits.Id);
        Assert.Equal(("world", "Токіо", 900, 2, "Оля"), (back.Region, back.City, back.Price, back.Photos.Count, back.Author));
    }

    // ------------------------------------------------------------------------------------------
    // партія
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_bank_refuses_to_start_and_says_why()
    {
        using var bank = new Bank();
        var h = Table(bank);
        var r = h.Start();
        Assert.False(r.Ok);
        Assert.Contains("Додати квартиру", r.Message);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);

        bank.Flat("Берлін", 1500, KhataRegions.Eu);
        var ua = Table(bank, options: new { region = "ua" });
        Assert.False(ua.Start().Ok);                       // для України квартир нема
        Assert.True(Table(bank, options: new { region = "ua,eu" }).Start().Ok);
    }

    [Fact]
    public void The_city_and_photos_appear_only_with_the_question_and_prices_stay_hidden()
    {
        using var bank = new Bank();
        bank.Flat("Львів", 17500);
        bank.Flat("Київ", 25000);
        var h = Table(bank, options: new { flats = "3" });
        Assert.Equal(2, h.View(null).GetProperty("bank").GetProperty("fit").GetInt32());
        Assert.True(h.Start().Ok);

        var v = h.View(0);
        Assert.Equal("between", v.GetProperty("phase").GetString());
        Assert.Equal("", v.GetProperty("city").GetString());
        Assert.Equal(0, v.GetProperty("photos").GetArrayLength());
        Assert.Equal(2, v.GetProperty("of").GetInt32());   // у банку дві — партія на дві, хоч просили три

        Until(h, "ask");
        var flat = Current(h, bank);
        v = h.View(0);
        Assert.Equal(flat.Urls, v.GetProperty("photos").EnumerateArray().Select(p => p.GetString()!));
        Assert.Equal("₴", v.GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("reveal").ValueKind);

        Assert.True(h.Act(0, "answer", new { value = "17 000" }).Ok);
        h.Tick(1);
        Assert.Equal(17000, h.View(0).GetProperty("my").GetDouble());
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("my").ValueKind);        // чужого числа не видно
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("my").ValueKind);     // глядачеві теж
        Assert.True(h.View(1).GetProperty("answered")[0].GetBoolean());
        Assert.DoesNotContain(flat.Price.ToString(), h.View(1).GetRawText());           // правди у виді ще нема
    }

    [Fact]
    public void Everyone_answered_reveals_early_and_closer_takes_more()
    {
        using var bank = new Bank();
        bank.Flat("Львів", 20000);
        var h = Table(bank, players: 3, options: new { flats = "3", seconds = "60" });
        h.Start();
        Until(h, "ask");
        Assert.True(h.Act(0, "answer", new { value = 20000 }).Ok);
        Assert.True(h.Act(1, "answer", new { value = "25к" }).Ok);
        h.Tick(1);
        Assert.Equal("ask", Phase(h));                     // третій ще думає
        Assert.True(h.Act(2, "answer", new { value = 40000 }).Ok);
        h.Tick(1);
        Assert.Equal("reveal", Phase(h));

        var rev = h.View(null).GetProperty("reveal");
        Assert.Equal(20000, rev.GetProperty("price").GetInt32());
        Assert.Equal("Глек", rev.GetProperty("by").GetString());
        Assert.False(string.IsNullOrEmpty(rev.GetProperty("say").GetString()));
        var rows = rev.GetProperty("rows").EnumerateArray().Select(r => (Seat: r.GetProperty("seat").GetInt32(), Points: r.GetProperty("points").GetInt32())).ToArray();
        Assert.Equal([(0, 1000), (1, 543), (2, 133)], rows);
        Assert.Equal(1000, Score(h, 0));
        Assert.Equal(543, Score(h, 1));

        Until(h, "done");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.View(null).GetProperty("result").GetProperty("winners").EnumerateArray().Select(w => w.GetInt32()));
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Equal(1, h.View(null).GetProperty("recap").GetArrayLength());
    }

    [Fact]
    public void An_answer_is_refused_out_of_turn_and_when_it_is_not_a_price()
    {
        using var bank = new Bank();
        bank.Flat("Львів", 20000);
        var h = Table(bank);
        h.Start();
        Assert.False(h.Act(0, "answer", new { value = 100 }).Ok);        // ще пауза
        Until(h, "ask");
        Assert.False(h.Act(0, "answer", new { value = "дорого" }).Ok);
        Assert.False(h.Act(0, "answer", new { value = 0 }).Ok);
        Assert.False(h.Act(0, "answer", new { value = -3 }).Ok);
        Assert.False(h.Act(0, "pass").Ok);
        Assert.True(h.Act(0, "answer", new { value = 100 }).Ok);
        Assert.True(h.Act(0, "answer", new { value = 19000 }).Ok);       // передумав — можна, поки є час
        Until(h, "reveal");
        Assert.False(h.Act(1, "answer", new { value = 100 }).Ok);
        Assert.Equal(Khata.Points(19000, 20000), Score(h, 0));
        Assert.Equal(0, Score(h, 1));                                    // промовчав — нуль
    }

    [Fact]
    public void Nobody_answers_and_the_party_ends_in_a_draw()
    {
        using var bank = new Bank();
        bank.Flat("Львів", 20000);
        bank.Flat("Київ", 25000);
        var h = Table(bank);
        h.Start();
        Until(h, "done");
        Assert.Empty(h.Finished.Single().Result.Winners);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void In_company_the_author_sits_their_own_flat_out_and_it_comes_last()
    {
        using var bank = new Bank();
        bank.Flat("Олина хата", 9000, by: "Оля");
        for (var i = 0; i < 3; i++) bank.Flat("Місто" + i, 10000 + i);
        var h = Table(bank, options: new { flats = "3" });
        h.Start();
        // Трьох чужих квартир вистачає — Олина в партію не йде взагалі.
        for (var round = 0; round < 3; round++)
        {
            Until(h, "ask");
            Assert.NotEqual("Олина хата", Current(h, bank).City);
            Until(h, round < 2 ? "between" : "done");
        }

        var all = Table(bank, options: new { flats = "5" });
        all.Start();
        var met = false;
        for (var round = 0; round < 4; round++)
        {
            Until(all, "ask");
            if (Current(all, bank).City == "Олина хата")
            {
                met = true;
                Assert.Equal(4, all.View(null).GetProperty("round").GetInt32());         // остання в черзі
                Assert.True(all.View(0).GetProperty("own").GetBoolean());
                Assert.False(all.View(1).GetProperty("own").GetBoolean());
                var no = all.Act(0, "answer", new { value = 9000 });
                Assert.False(no.Ok);
                Assert.Contains("твоя квартира", no.Message);
                Assert.True(all.Act(1, "answer", new { value = 9000 }).Ok);
                all.Tick(1);
                Assert.Equal("reveal", Phase(all));                                       // автора не чекаємо
            }
            Until(all, round < 3 ? "between" : "done");
        }
        Assert.True(met);
    }

    [Fact]
    public void Alone_the_author_plays_their_own_flat_but_earns_no_shards_for_it()
    {
        using var bank = new Bank();
        for (var i = 0; i < 5; i++) bank.Flat("Місто" + i, 10000 + i * 1000, by: "Оля");
        var h = Table(bank, players: 1);
        Assert.True(h.Start().Ok);
        for (var round = 0; round < 5; round++)
        {
            Until(h, "ask");
            Assert.False(h.View(0).GetProperty("own").GetBoolean());
            Assert.True(h.Act(0, "answer", new { value = Current(h, bank).Price }).Ok);
            Until(h, round < 4 ? "between" : "done");
        }
        Assert.Equal(5000, Score(h, 0));
        Assert.Empty(h.Awards);
        Assert.Equal(0, h.View(0).GetProperty("result").GetProperty("fresh")[0].GetInt64());
    }

    [Fact]
    public void Shards_come_only_for_flats_seen_for_the_first_time()
    {
        using var temp = new TempDb();
        using var bank = new Bank();
        for (var i = 0; i < 5; i++) bank.Flat("Місто" + i, 10000 + i * 1000);
        new GameSeen(temp.Db).Warm(Khata.GameId);
        var services = new ServiceCollection().AddSingleton(temp.Db).AddSingleton(bank.Store).BuildServiceProvider();

        void Play(RoomHarness h)
        {
            for (var round = 0; round < 5; round++)
            {
                Until(h, "ask");
                Assert.True(h.Act(0, "answer", new { value = Current(h, bank).Price }).Ok);   // Оля знає все, Петро мовчить
                Until(h, round < 4 ? "between" : "done");
            }
        }

        var first = Table(bank, services: services);
        first.Start();
        Play(first);
        var award = first.Awards.Single();
        Assert.Equal(("Оля", Khata.Shards(5000), "points:1"), (award.Nick, award.Shards, award.Reason));
        Assert.Equal(10, award.Shards);

        // Ті самі п'ять квартир удруге: бали ті самі, а черепків уже нема — напам'ять не нафармиш.
        var second = Table(bank, services: services);
        second.Start();
        Play(second);
        Assert.Equal(5000, Score(second, 0));
        Assert.Empty(second.Awards);
    }

    [Fact]
    public void The_region_option_keeps_other_cities_out()
    {
        using var bank = new Bank();
        bank.Flat("Львів", 17500, KhataRegions.Ua);
        bank.Flat("Лісабон", 1200, KhataRegions.Eu);
        bank.Flat("Токіо", 900, KhataRegions.World);
        var h = Table(bank, options: new { flats = "5", region = "eu,world" });
        Assert.Equal(2, h.View(null).GetProperty("bank").GetProperty("fit").GetInt32());
        Assert.Equal(3, h.View(null).GetProperty("bank").GetProperty("total").GetInt32());
        h.Start();
        var seen = new List<string>();
        for (var round = 0; round < 2; round++)
        {
            Until(h, "ask");
            seen.Add(Current(h, bank).City);
            Assert.Equal("$", h.View(null).GetProperty("unit").GetString());
            Until(h, round < 1 ? "between" : "done");
        }
        Assert.Equal(["Лісабон", "Токіо"], seen.Order());
    }

    [Fact]
    public void One_leaves_and_the_other_plays_on_all_leave_and_the_party_ends()
    {
        using var bank = new Bank();
        bank.Flat("Львів", 20000);
        bank.Flat("Київ", 25000);
        var h = Table(bank);
        h.Start();
        Until(h, "ask");
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.View(null).GetProperty("names")[1].GetString());   // устав — а в підсумку лишився ніком
        Assert.True(h.Act(0, "answer", new { value = Current(h, bank).Price }).Ok);
        h.Tick(1);
        Assert.Equal("reveal", Phase(h));                 // того, хто пішов, не чекаємо
        h.Leave("Оля");
        Assert.NotEqual(RoomStatus.Playing, h.Rooms.Find(h.RoomId)?.Status ?? RoomStatus.Finished);
    }

    [Fact]
    public void A_rematch_starts_clean()
    {
        using var bank = new Bank();
        bank.Flat("Львів", 20000);
        var h = Table(bank);
        h.Start();
        Until(h, "ask");
        h.Act(0, "answer", new { value = 20000 });
        Until(h, "done");
        Assert.True(h.Rematch().Ok);
        Assert.Equal("between", Phase(h));
        Assert.Equal(0, Score(h, 0));
        Assert.Equal(0, Score(h, 1));
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }
}
