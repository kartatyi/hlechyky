using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Шпигун»: колода, роздача, м'який покажчик питань, підозра з одностайним голосуванням, вгадування локації,
/// фінальне голосування більшістю, очки за раунди, вихід із-за столу — і головне, приховування. Шпигуна й
/// локацію тест дізнається так само, як людина: зі свого виду (<c>me</c>). Жодних «дверцят» у гру звідси нема.
/// </summary>
[Collection(SerialPerf.Name)]
public class SpyTests(ITestOutputHelper output)
{
    static readonly string[] Crew = ["Оля", "Петро", "Ганна", "Микола", "Іван", "Марія", "Тарас", "Соломія", "Богдан", "Леся", "Остап"];

    /// <summary>Справжній банк із репозиторію — той самий, що й у проді, але підкладений явно, як сервіс.</summary>
    static readonly Lazy<SpyLocations> Real = new(() => SpyLocations.Load(Paths.Resolve(SpyLocations.FileName)));

    static RoomHarness Table(int players = 4, int seed = 1, object? options = null, SpyLocations? bank = null)
    {
        var h = new RoomHarness("spy", options, seed, RoomHarness.WithService(bank ?? Real.Value));
        for (var i = 0; i < players; i++) Assert.True(h.Join(Crew[i]).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    // ---------- як тест дивиться на стіл ----------

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static IEnumerable<int> Seated(RoomHarness h) =>
        Enumerable.Range(0, h.Room.Seats.Length).Where(s => h.Room.Seats[s] is not null);

    static JsonElement Me(RoomHarness h, int seat) => h.View(seat).GetProperty("me");

    /// <summary>Хто шпигун — як людина: той, у чиєму виді <c>me.spy == true</c>.</summary>
    static int SpySeat(RoomHarness h) => Seated(h).Single(s => Me(h, s) is { ValueKind: JsonValueKind.Object } m && m.GetProperty("spy").GetBoolean());

    static int[] Villagers(RoomHarness h) => [.. Seated(h).Where(s => s != SpySeat(h))];

    static string Loc(RoomHarness h, int seat) => Me(h, seat).GetProperty("loc").GetString()!;

    static string Role(RoomHarness h, int seat) => Me(h, seat).GetProperty("role").GetString()!;

    /// <summary>Локація раунду очима першого-ліпшого не-шпигуна.</summary>
    static string Location(RoomHarness h) => Loc(h, Villagers(h)[0]);

    static int? Asker(RoomHarness h) => h.View(null).GetProperty("asker") is { ValueKind: JsonValueKind.Number } a ? a.GetInt32() : null;

    static int? AskedBy(RoomHarness h) => h.View(null).GetProperty("askedBy") is { ValueKind: JsonValueKind.Number } a ? a.GetInt32() : null;

    static JsonElement Player(RoomHarness h, int seat) =>
        h.View(null).GetProperty("players").EnumerateArray().First(p => p.GetProperty("seat").GetInt32() == seat);

    static long Score(RoomHarness h, int seat) => Player(h, seat).GetProperty("score").GetInt64();

    static long LeftMs(RoomHarness h) => h.View(null).GetProperty("clock").GetProperty("leftMs").GetInt64();

    static int Round(RoomHarness h) => h.View(null).GetProperty("round").GetInt32();

    static string[] Deck(RoomHarness h) => [.. h.View(null).GetProperty("deck").EnumerateArray().Select(d => d[0].GetString()!)];

    /// <summary>Що Глек-ведучий сказав у балачку столу — за весь час, у порядку появи.</summary>
    static List<string> Hlek(RoomHarness h) => [.. h.Outbox.OfType<TableSaid>().Where(x => x.Line.Kind == "dj").Select(x => x.Line.Text)];

    /// <summary>Тикати, доки не станеться те, чого чекаємо. Ліміт — щоб зациклений тест падав, а не висів.</summary>
    static void Until(RoomHarness h, Func<bool> done, int limit = 3000)
    {
        for (var i = 0; i < limit && !done(); i++) h.Tick();
        Assert.True(done(), $"не дочекались: фаза {Phase(h)}, раунд {Round(h)}");
    }

    static void To(RoomHarness h, string phase) => Until(h, () => Phase(h) == phase);

    static void ToPlay(RoomHarness h) => To(h, "play");

    /// <summary>Будь-яка присутня локація з колоди, крім правильної.</summary>
    static string WrongLoc(RoomHarness h) => Deck(h).First(id => id != Location(h));

    /// <summary>Не-шпигун, відмінний від названих.</summary>
    static int Villager(RoomHarness h, params int[] except) => Villagers(h).First(s => !except.Contains(s));

    /// <summary>Підозра й одностайне «так» від усіх, хто має голос.</summary>
    static void Convict(RoomHarness h, int accuser, int suspect)
    {
        Assert.True(h.Act(accuser, "accuse", new { seat = suspect }).Ok, h.Reply.Message);
        foreach (var s in Seated(h)) if (s != suspect && s != accuser) Assert.True(h.Act(s, "vote", new { yes = true }).Ok);
        h.Tick();
    }

    /// <summary>Дограти раунд найкоротшим шляхом — шпигун помиляється з локацією.</summary>
    static void Misguess(RoomHarness h)
    {
        ToPlay(h);
        Assert.True(h.Act(SpySeat(h), "guess", new { loc = WrongLoc(h) }).Ok);
        Assert.Equal("reveal", Phase(h));
    }

    static void AllReady(RoomHarness h)
    {
        foreach (var s in Seated(h)) h.Act(s, "ready", new { });
        h.Tick();
    }

    /// <summary>Вид без поля <c>me</c> — щоб порівняти, чим відрізняються місця.</summary>
    static string WithoutMe(RoomHarness h, int? seat)
    {
        var node = JsonNode.Parse(h.View(seat).GetRawText())!.AsObject();
        node.Remove("me");
        return node.ToJsonString();
    }

    static SpyLocations SmallBank(int n, string set = "ua") => new(Enumerable.Range(0, n).Select(i =>
        new SpyLocation($"loc-{i}", $"Місце {i}", "📍", set, ["перший", "другий", "третій", "четвертий", "п'ятий", "шостий", "сьомий"])));

    // =========================================================================================
    // Банк і каталог
    // =========================================================================================

    [Fact]
    public void The_bank_has_at_least_40_locations_each_with_7_roles_an_icon_and_a_set()
    {
        var bank = Real.Value;
        Assert.True(bank.All.Count >= 40, $"локацій лише {bank.All.Count}");
        Assert.All(bank.All, l =>
        {
            Assert.True(l.Roles.Length >= 7, $"{l.Id}: ролей {l.Roles.Length}");
            Assert.False(string.IsNullOrWhiteSpace(l.Icon));
            Assert.Contains(l.Set, new[] { "ua", "classic" });
        });
        Assert.Equal(24, bank.CountIn("ua"));
        Assert.Equal(20, bank.CountIn("classic"));
    }

    [Fact]
    public void Location_ids_and_titles_are_unique_and_ids_match_the_pattern()
    {
        var bank = Real.Value;
        Assert.Equal(bank.All.Count, bank.All.Select(l => l.Id).Distinct().Count());
        Assert.Equal(bank.All.Count, bank.All.Select(l => l.Title).Distinct().Count());
        Assert.All(bank.All, l => Assert.Matches("^[a-z0-9-]{2,24}$", l.Id));
        // Жодної російської «лодки» і жодного HTML у текстах — вони йдуть на екран як є.
        Assert.All(bank.All.SelectMany(l => l.Roles.Append(l.Title)), t => Assert.DoesNotContain('<', t));
        Assert.DoesNotContain(bank.All, l => l.Title.Contains("лодка", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_missing_or_broken_bank_file_gives_an_empty_bank_not_an_exception()
    {
        Assert.Empty(SpyLocations.Load(Path.Combine(Path.GetTempPath(), "нема-такого-" + Guid.NewGuid() + ".json")).All);
        Assert.Empty(SpyLocations.Parse("{ це не json").All);
        Assert.Empty(SpyLocations.Parse("42").All);
        Assert.Empty(SpyLocations.Parse("{\"locations\": 5}").All);
        // голий масив — теж формат
        Assert.Single(SpyLocations.Parse("[{\"id\":\"ab\",\"title\":\"Аб\",\"icon\":\"x\",\"set\":\"ua\",\"roles\":[\"а\",\"б\",\"в\"]}]").All);
    }

    [Fact]
    public void A_crooked_entry_is_dropped_and_the_rest_survive()
    {
        const string json = """
        { "locations": [
          { "id": "good", "title": "Добре", "icon": "🙂", "set": "ua", "roles": ["а", "б", "в"] },
          { "id": "Bad Id", "title": "Кривий id", "set": "ua", "roles": ["а", "б", "в"] },
          { "id": "notitle", "set": "ua", "roles": ["а", "б", "в"] },
          { "id": "mars", "title": "Марс", "set": "mars", "roles": ["а", "б", "в"] },
          { "id": "few", "title": "Мало ролей", "set": "ua", "roles": ["а", "б", "  "] },
          { "id": "good", "title": "Повтор", "set": "classic", "roles": ["а", "б", "в"] },
          { "id": "nums", "title": "Числа", "set": "ua", "roles": [1, 2, 3] },
          { "id": "noicon", "title": "Без значка", "set": "classic", "roles": ["а", "б", "в", "в"] }
        ] }
        """;
        var bank = SpyLocations.Parse(json);
        Assert.Equal(["good", "noicon"], bank.All.Select(l => l.Id).ToArray());
        Assert.Equal("📍", bank.All[1].Icon);                  // значок за замовчуванням
        Assert.Equal(3, bank.All[1].Roles.Length);              // повтор ролі відкинуто
    }

    [Fact]
    public void Without_enough_locations_the_match_does_not_start_and_says_so()
    {
        var h = Table(3, bank: SmallBank(2));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("done", Phase(h));
        Assert.True(h.Finished.Single().Result.Draw);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text.Contains("локацій не знайшлось"));
    }

    [Fact]
    public void The_catalog_lists_spy_as_party_by_host_hidden_3_to_10_with_time_rounds_and_set()
    {
        var info = RoomHarness.NewRegistry().Info("spy")!;
        Assert.Equal(("Шпигун", GameGroup.Party, 2, 10), (info.Title, info.Group, info.MinPlayers, info.MaxPlayers));   // удвох — з Глеком
        Assert.Equal("on", info.Options!.Single(o => o.Key == "glek").Default);
        Assert.Equal((250, StartMode.ByHost, true, false, false), (info.TickMs, info.Start, info.Hidden, info.Rated, info.Persistent));
        var options = info.Options!.ToDictionary(o => o.Key);
        Assert.Equal(["4", "6", "8", "10"], options["time"].Values.Select(v => v.Value).ToArray());
        Assert.Equal("6", options["time"].Default);
        Assert.Equal(["1", "3", "5", Spy.EachRound], options["rounds"].Values.Select(v => v.Value).ToArray());
        Assert.Equal("3", options["rounds"].Default);
        Assert.True(options["set"].Multi);
        Assert.Equal(["all", "ua", "classic"], options["set"].Values.Select(v => v.Value).ToArray());
        Assert.Equal("10", RoomHarness.NewRegistry().Create("spy")!.SeatName(9));
    }

    [Fact]
    public void Two_players_without_glek_cannot_start_and_the_reply_says_at_least_three()
    {
        var h = new RoomHarness("spy", new { glek = "off" }, services: RoomHarness.WithService(Real.Value));
        h.Join("Оля");
        h.Join("Петро");
        var reply = h.Start();
        Assert.False(reply.Ok);
        Assert.Contains("щонайменше 3", reply.Message);
        Assert.Equal("lobby", Phase(h));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("me").ValueKind);
    }

    [Fact]
    public void Unknown_option_values_fall_back_to_defaults()
    {
        var h = Table(3, options: new { time = "7", rounds = "2", set = "mars" });
        var rules = h.View(null).GetProperty("rules");
        Assert.Equal(6, rules.GetProperty("minutes").GetInt32());
        Assert.Equal(3, rules.GetProperty("rounds").GetInt32());
        Assert.Equal(["all"], rules.GetProperty("sets").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.Equal(3, h.View(null).GetProperty("of").GetInt32());
    }

    [Fact]
    public void Options_ten_minutes_five_rounds_and_both_sets_are_read()
    {
        var h = Table(3, options: new { time = "10", rounds = "5", set = "classic,ua" });
        var rules = h.View(null).GetProperty("rules");
        Assert.Equal((10, 5), (rules.GetProperty("minutes").GetInt32(), rules.GetProperty("rounds").GetInt32()));
        Assert.Equal(["ua", "classic"], rules.GetProperty("sets").EnumerateArray().Select(x => x.GetString()!).ToArray());
        ToPlay(h);
        Assert.Equal(600_000, h.View(null).GetProperty("clock").GetProperty("totalMs").GetInt64());
    }

    [Fact]
    public void Four_minute_rounds_are_an_option()
    {
        var h = Table(3, options: new { time = "4" });
        Assert.Equal(4, h.View(null).GetProperty("rules").GetProperty("minutes").GetInt32());
        ToPlay(h);
        Assert.Equal(240_000, LeftMs(h));
    }

    [Fact]
    public void Each_once_makes_as_many_rounds_as_players_and_everyone_is_spy_exactly_once()
    {
        var lobby = new RoomHarness("spy", new { rounds = Spy.EachRound }, 1, RoomHarness.WithService(Real.Value));
        foreach (var n in Crew.Take(4)) lobby.Join(n);
        var rules = lobby.View(null).GetProperty("rules");
        Assert.True(rules.GetProperty("each").GetBoolean());                 // у лобі — «кожен по разу», число ще невідоме
        Assert.Equal(0, rules.GetProperty("rounds").GetInt32());

        var h = Table(4, seed: 3, options: new { rounds = Spy.EachRound });
        Assert.Equal(4, h.View(null).GetProperty("of").GetInt32());
        Assert.Equal(4, h.View(null).GetProperty("rules").GetProperty("rounds").GetInt32());
        var spies = new List<int>();
        for (var r = 0; r < 4; r++)
        {
            ToPlay(h);
            spies.Add(SpySeat(h));
            h.Act(SpySeat(h), "guess", new { loc = WrongLoc(h) });
            AllReady(h);
        }
        Assert.Equal("done", Phase(h));
        Assert.Equal(Seated(h).Order().ToArray(), spies.Order().ToArray());   // кожен — рівно раз
    }

    [Fact]
    public void Each_once_loses_the_round_of_someone_who_left_before_being_spy()
    {
        var h = Table(5, seed: 4, options: new { rounds = Spy.EachRound });
        ToPlay(h);
        var first = SpySeat(h);
        h.Act(first, "guess", new { loc = WrongLoc(h) });
        AllReady(h);
        ToPlay(h);
        var second = SpySeat(h);
        var gone = Seated(h).First(s => s != first && s != second);
        h.Leave(h.NickOf(gone));                            // шпигуном так і не побув
        Assert.Equal(4, h.View(null).GetProperty("of").GetInt32());
        var spies = new List<int> { first, second };
        h.Act(second, "guess", new { loc = WrongLoc(h) });
        AllReady(h);
        for (var r = 0; r < 2; r++)
        {
            ToPlay(h);
            spies.Add(SpySeat(h));
            h.Act(SpySeat(h), "guess", new { loc = WrongLoc(h) });
            AllReady(h);
        }
        Assert.Equal("done", Phase(h));
        Assert.Equal(4, spies.Distinct().Count());
        Assert.DoesNotContain(gone, spies);
    }

    // =========================================================================================
    // Роздача
    // =========================================================================================

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(10)]
    public void Exactly_one_spy_is_dealt_and_everyone_else_shares_the_location(int players)
    {
        var h = Table(players, seed: players * 7);
        var spy = SpySeat(h);
        var me = Me(h, spy);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("loc").ValueKind);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("role").ValueKind);
        var locs = Villagers(h).Select(s => Loc(h, s)).Distinct().ToArray();
        Assert.Single(locs);
        Assert.Equal(players - 1, Villagers(h).Length);
        Assert.Contains(locs[0], Deck(h));
    }

    [Fact]
    public void Roles_are_distinct_while_there_are_enough_and_wrap_after_the_eighth()
    {
        var nine = Table(9, seed: 3);
        var roles9 = Villagers(nine).Select(s => Role(nine, s)).ToArray();
        Assert.Equal(8, roles9.Distinct().Count());

        var ten = Table(10, seed: 3);
        var roles10 = Villagers(ten).Select(s => Role(ten, s)).ToArray();
        Assert.Equal(9, roles10.Length);
        Assert.Equal(8, roles10.Distinct().Count());
        Assert.Equal(2, roles10.GroupBy(r => r).Max(g => g.Count()));
        var loc = Real.Value.All.First(l => l.Id == Location(ten));
        Assert.All(roles10, r => Assert.Contains(r, loc.Roles));
    }

    [Fact]
    public void The_deck_holds_24_sorted_locations_from_the_chosen_sets_and_contains_the_round_location()
    {
        var h = Table(4, seed: 11);
        var deck = h.View(null).GetProperty("deck").EnumerateArray().ToArray();
        Assert.Equal(Spy.DeckSize, deck.Length);
        var titles = deck.Select(d => d[1].GetString()!).ToArray();
        Assert.Equal(titles.OrderBy(t => t, Spy.TitleOrder).ToArray(), titles);
        Assert.All(deck, d => Assert.Equal(3, d.GetArrayLength()));
        Assert.Contains(Location(h), deck.Select(d => d[0].GetString()));
        // іконка на місці
        var first = Real.Value.All.First(l => l.Id == deck[0][0].GetString());
        Assert.Equal(first.Icon, deck[0][2].GetString());
    }

    [Fact]
    public void The_set_option_narrows_the_deck_to_ua_or_classic()
    {
        var ua = Table(3, options: new { set = "ua" });
        var uaIds = Real.Value.All.Where(l => l.Set == "ua").Select(l => l.Id).ToHashSet();
        Assert.Equal(24, Deck(ua).Length);
        Assert.All(Deck(ua), id => Assert.Contains(id, uaIds));

        var classic = Table(3, options: new { set = "classic" });
        Assert.Equal(20, Deck(classic).Length);
        Assert.All(Deck(classic), id => Assert.Equal("classic", Real.Value.All.First(l => l.Id == id).Set));
    }

    [Fact]
    public void Locations_never_repeat_within_a_match_while_the_deck_allows()
    {
        var h = Table(4, seed: 5, options: new { rounds = "5" });
        var seen = new List<string>();
        for (var r = 0; r < 5; r++)
        {
            seen.Add(Location(h));
            Misguess(h);
            if (r < 4) { AllReady(h); Assert.Equal("deal", Phase(h)); }
        }
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public void A_tiny_deck_repeats_locations_only_when_it_runs_out()
    {
        var h = Table(3, seed: 2, options: new { rounds = "5" }, bank: SmallBank(3));
        var seen = new List<string>();
        for (var r = 0; r < 5; r++)
        {
            seen.Add(Location(h));
            Misguess(h);
            if (r < 4) AllReady(h);
        }
        Assert.Equal(3, seen.Take(3).Distinct().Count());
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public void Nobody_is_spy_twice_before_everyone_was_spy_once()
    {
        var h = Table(3, seed: 9);
        var spies = new List<int>();
        for (var r = 0; r < 3; r++)
        {
            spies.Add(SpySeat(h));
            Misguess(h);
            if (r < 2) AllReady(h);
        }
        Assert.Equal(3, spies.Distinct().Count());

        var five = Table(4, seed: 4, options: new { rounds = "5" });
        var counts = new Dictionary<int, int>();
        for (var r = 0; r < 5; r++)
        {
            var s = SpySeat(five);
            counts[s] = counts.GetValueOrDefault(s) + 1;
            Misguess(five);
            if (r < 4) AllReady(five);
        }
        Assert.Equal(4, counts.Count);
        Assert.Equal(2, counts.Values.Max());
    }

    [Fact]
    public void The_same_seed_deals_the_same_spy_location_roles_and_first_asker()
    {
        string Script(int seed)
        {
            var h = Table(6, seed: seed);
            var log = new List<string>();
            ToPlay(h);
            var a = Asker(h)!.Value;
            h.Act(a, "ask", new { seat = Seated(h).First(s => s != a) });
            h.Tick(3);
            var v = Villager(h, Asker(h)!.Value);
            h.Act(Asker(h)!.Value == v ? Villager(h, v) : v, "accuse", new { seat = SpySeat(h) });
            h.Tick(100);
            foreach (var s in Seated(h)) log.Add(Views.Text(h.Room.Game.View(s)));
            log.Add(Views.Text(h.Room.Game.View(null)));
            log.Add(Views.Text(h.Room.Game.Frame()));
            log.AddRange(Hlek(h));
            return string.Join("\n", log);
        }
        Assert.Equal(Script(42), Script(42));
        Assert.NotEqual(Script(42), Script(43));
    }

    [Fact]
    public void Deal_lasts_eight_seconds_and_the_round_clock_starts_only_in_play()
    {
        var h = Table(4);
        Assert.Equal("deal", Phase(h));
        var clock = h.View(null).GetProperty("clock");
        Assert.True(clock.GetProperty("paused").GetBoolean());
        Assert.Equal(360_000, clock.GetProperty("leftMs").GetInt64());
        Assert.Equal(Spy.DealMs, h.View(null).GetProperty("phaseMs").GetInt32());

        h.Tick(Spy.DealMs / Spy.TickMs - 1);
        Assert.Equal("deal", Phase(h));
        h.Tick();
        Assert.Equal("play", Phase(h));
        clock = h.View(null).GetProperty("clock");
        Assert.False(clock.GetProperty("paused").GetBoolean());
        Assert.Equal(360_000, clock.GetProperty("leftMs").GetInt64());
        h.Tick(4);
        Assert.Equal(359_000, LeftMs(h));
    }

    // =========================================================================================
    // Черга питань
    // =========================================================================================

    [Fact]
    public void Only_the_asker_may_ask_and_the_target_becomes_the_next_asker()
    {
        var h = Table(4);
        ToPlay(h);
        var asker = Asker(h)!.Value;
        var other = Seated(h).First(s => s != asker);
        var target = Seated(h).First(s => s != asker && s != other);

        var before = WithoutMe(h, null);
        var no = h.Act(other, "ask", new { seat = target });
        Assert.False(no.Ok);
        Assert.Contains("Зараз питає " + h.NickOf(asker), no.Message);
        Assert.Equal(before, WithoutMe(h, null));

        Assert.True(h.Act(asker, "ask", new { seat = target }).Ok);
        Assert.Equal(target, Asker(h));
        Assert.Equal(asker, AskedBy(h));
    }

    [Fact]
    public void The_asker_cannot_ask_back_the_one_who_just_asked_them_nor_themselves()
    {
        var h = Table(3);
        ToPlay(h);
        var a = Asker(h)!.Value;
        var b = Seated(h).First(s => s != a);
        var c = Seated(h).First(s => s != a && s != b);
        Assert.True(h.Act(a, "ask", new { seat = b }).Ok);

        var back = h.Act(b, "ask", new { seat = a });
        Assert.False(back.Ok);
        Assert.Equal($"{h.NickOf(a)} щойно питав тебе — спитай когось іншого", back.Message);
        Assert.Equal("Себе питати нема сенсу", h.Act(b, "ask", new { seat = b }).Message);
        Assert.True(h.Act(b, "ask", new { seat = c }).Ok);
        // тепер «щойно питав» — b, а a вже можна
        Assert.True(h.Act(c, "ask", new { seat = a }).Ok);
    }

    [Fact]
    public void Anyone_may_take_the_word_after_thirty_silent_seconds_and_not_before()
    {
        var h = Table(4);
        ToPlay(h);
        var asker = Asker(h)!.Value;
        var taker = Seated(h).First(s => s != asker);
        var target = Seated(h).First(s => s != asker && s != taker);

        h.Tick(Spy.AskGraceMs / Spy.TickMs - 1);
        Assert.False(h.View(null).GetProperty("askGrace").GetBoolean());
        Assert.False(h.Act(taker, "ask", new { seat = target }).Ok);

        var views = h.Outbox.Count;
        h.Tick();
        Assert.True(h.View(null).GetProperty("askGrace").GetBoolean());
        // Сам покажчик «можна перехопити» розіслано без жодного натиску.
        Assert.Contains(h.Outbox.Skip(views), m => m is RoomViews);

        Assert.True(h.Act(taker, "ask", new { seat = asker }).Ok);   // перехопив — і може питати навіть того, чия була черга
        Assert.Equal(asker, Asker(h));
        Assert.Equal(taker, AskedBy(h));
        Assert.False(h.View(null).GetProperty("askGrace").GetBoolean());
    }

    [Fact]
    public void Asking_someone_who_left_or_outside_play_is_refused_and_changes_nothing()
    {
        var h = Table(5);
        var asker = Asker(h)!.Value;
        var target = Seated(h).First(s => s != asker);
        Assert.Equal("Зараз не час питати", h.Act(asker, "ask", new { seat = target }).Message);   // ще роздача

        ToPlay(h);
        var gone = Seated(h).First(s => s != asker && s != SpySeat(h) && s != target);
        h.Leave(h.NickOf(gone));
        asker = Asker(h)!.Value;
        var before = WithoutMe(h, null);
        Assert.Equal("Його вже нема за столом", h.Act(asker, "ask", new { seat = gone }).Message);
        Assert.Equal("Не зрозумів, кого питати", h.Act(asker, "ask", new { seat = 42 }).Message);
        Assert.Equal("Не зрозумів, кого питати", h.Act(asker, "ask", new { who = 1 }).Message);
        Assert.Equal(before, WithoutMe(h, null));
    }

    // =========================================================================================
    // Підозра й голосування
    // =========================================================================================

    [Fact]
    public void An_accusation_pauses_the_round_clock_and_opens_a_twenty_second_vote()
    {
        var h = Table(4);
        ToPlay(h);
        h.Tick(40);                                        // 10 с раунду минуло
        var accuser = Seated(h).First();
        var suspect = Seated(h).Skip(1).First();
        var r = h.Act(accuser, "accuse", new { seat = suspect });
        Assert.True(r.Ok);
        Assert.Equal("Підозра висунута — голосуємо", r.Message);
        Assert.Equal("vote", Phase(h));                    // одразу, без тика

        var vote = h.View(null).GetProperty("vote");
        Assert.Equal((suspect, accuser, 3), (vote.GetProperty("suspect").GetInt32(), vote.GetProperty("accuser").GetInt32(), vote.GetProperty("need").GetInt32()));
        Assert.Equal(Spy.VoteMs, h.View(null).GetProperty("phaseMs").GetInt32());
        var left = LeftMs(h);
        Assert.Equal(350_000, left);
        h.Tick(20);
        Assert.Equal(left, LeftMs(h));                     // годинник стоїть
        Assert.True(h.View(null).GetProperty("clock").GetProperty("paused").GetBoolean());

        // кадр про зміну фази летить на найближчому тику
        var h2 = Table(4);
        ToPlay(h2);
        h2.Act(0, "accuse", new { seat = 1 });
        var n = h2.Outbox.Count;
        h2.Tick();
        Assert.Contains(h2.Outbox.Skip(n), m => m is RoomFrame);
    }

    [Fact]
    public void Each_player_accuses_once_per_round_and_again_next_round()
    {
        var h = Table(4);
        ToPlay(h);
        var spy = SpySeat(h);
        var accuser = Villager(h);
        var innocent = Villager(h, accuser);
        Assert.True(h.Act(accuser, "accuse", new { seat = innocent }).Ok);
        h.Act(innocent == spy ? accuser : spy, "vote", new { yes = false });
        Until(h, () => Phase(h) == "play");
        Assert.True(Player(h, accuser).GetProperty("accused").GetBoolean());
        Assert.Equal("Ти вже висував підозру цього раунду", h.Act(accuser, "accuse", new { seat = spy }).Message);

        Assert.True(h.Act(spy, "guess", new { loc = WrongLoc(h) }).Ok);
        AllReady(h);
        ToPlay(h);
        Assert.False(Player(h, accuser).GetProperty("accused").GetBoolean());
        Assert.True(h.Act(accuser, "accuse", new { seat = Seated(h).First(s => s != accuser) }).Ok);
    }

    [Fact]
    public void The_accused_cannot_vote_and_the_accuser_counts_as_yes()
    {
        var h = Table(4);
        ToPlay(h);
        h.Act(0, "accuse", new { seat = 1 });
        Assert.Equal("Підозрюваний не голосує", h.Act(1, "vote", new { yes = false }).Message);
        var votes = h.View(null).GetProperty("vote").GetProperty("votes");
        Assert.True(votes.GetProperty("0").GetBoolean());
        Assert.False(votes.TryGetProperty("1", out _));
        Assert.Equal("Голос: це шпигун", h.Act(2, "vote", new { yes = true }).Message);
        Assert.Equal("Голос: не він", h.Act(3, "vote", new { yes = false }).Message);
    }

    [Fact]
    public void A_unanimous_yes_on_the_spy_ends_the_round_with_one_point_each_and_two_for_the_accuser()
    {
        var h = Table(5);
        ToPlay(h);
        var spy = SpySeat(h);
        var accuser = Villager(h);
        Convict(h, accuser, spy);

        Assert.Equal("reveal", Phase(h));
        var reveal = h.View(null).GetProperty("reveal");
        Assert.Equal("caught", reveal.GetProperty("how").GetString());
        Assert.Equal(spy, reveal.GetProperty("spy").GetInt32());
        Assert.Equal(accuser, reveal.GetProperty("accuser").GetInt32());
        Assert.Equal(0, Score(h, spy));
        Assert.Equal(2, Score(h, accuser));
        foreach (var v in Villagers(h).Where(v => v != accuser)) Assert.Equal(1, Score(h, v));
        Assert.Equal(2, reveal.GetProperty("gained").GetProperty(accuser.ToString()).GetInt64());
        Assert.Contains(h.Awards, a => a.Reason == "ach:spy-catch" && a.Nick == h.NickOf(accuser) && a.Shards == 0);
        Assert.NotNull(AchievementCatalog.Get("spy-catch"));
    }

    [Fact]
    public void A_unanimous_yes_on_an_innocent_gives_the_spy_four_points()
    {
        var h = Table(4);
        ToPlay(h);
        var spy = SpySeat(h);
        var innocent = Villager(h);
        Convict(h, spy, innocent);                         // шпигун сам підставив невинного
        Assert.Equal("reveal", Phase(h));
        Assert.Equal("wrong", h.View(null).GetProperty("reveal").GetProperty("how").GetString());
        Assert.Equal(innocent, h.View(null).GetProperty("reveal").GetProperty("suspect").GetInt32());
        Assert.Equal(Spy.SpyFramedPts, Score(h, spy));
        Assert.All(Villagers(h), v => Assert.Equal(0, Score(h, v)));
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:spy-catch");
    }

    [Fact]
    public void One_no_or_a_silent_voter_resumes_play_with_the_clock_where_it_stopped()
    {
        var h = Table(4);
        ToPlay(h);
        h.Tick(20);
        var spy = SpySeat(h);
        var accuser = Villager(h);
        h.Act(accuser, "accuse", new { seat = spy });
        var left = LeftMs(h);
        var silent = Villager(h, accuser);
        foreach (var s in Seated(h)) if (s != spy && s != accuser && s != silent) h.Act(s, "vote", new { yes = true });
        Until(h, () => Phase(h) != "vote", 100);           // мовчання = «ні»: 20 с — і назад
        Assert.Equal("play", Phase(h));
        Assert.InRange(LeftMs(h), left - Spy.TickMs, left);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("vote").ValueKind);
        Assert.Equal(1, Hlek(h).Count(l => SpyGlek.Accuse.Any(p => l.StartsWith(p[..6], StringComparison.Ordinal))));
        Assert.All(Seated(h), s => Assert.Equal(0, Score(h, s)));

        // Одне явне «ні» — те саме, і без чекання дедлайну.
        var v2 = Villager(h, accuser);
        Assert.True(h.Act(v2, "accuse", new { seat = spy }).Ok);
        foreach (var s in Seated(h)) if (s != spy && s != v2) h.Act(s, "vote", new { yes = s != accuser });
        h.Tick();
        Assert.Equal("play", Phase(h));
    }

    [Fact]
    public void Votes_can_be_changed_until_the_deadline_and_resolve_early_when_all_voted()
    {
        var h = Table(4);
        ToPlay(h);
        var spy = SpySeat(h);
        var accuser = Villager(h);
        var a = Villager(h, accuser);
        var b = Villager(h, accuser, a);
        h.Act(accuser, "accuse", new { seat = spy });
        h.Act(a, "vote", new { yes = false });
        h.Tick();
        Assert.Equal("vote", Phase(h));                    // ще не всі
        h.Act(a, "vote", new { yes = true });              // передумав
        h.Act(b, "vote", new { yes = true });
        h.Tick();
        Assert.Equal("reveal", Phase(h));                  // усі проголосували — одразу, не за 20 с
        Assert.Equal("caught", h.View(null).GetProperty("reveal").GetProperty("how").GetString());
    }

    [Fact]
    public void An_accusation_in_the_last_second_that_fails_goes_straight_to_the_final_vote()
    {
        var h = Table(4);
        ToPlay(h);
        Until(h, () => LeftMs(h) <= 500, 2000);
        var spy = SpySeat(h);
        var accuser = Villager(h);
        Assert.True(h.Act(accuser, "accuse", new { seat = spy }).Ok);
        Assert.Equal(500, LeftMs(h));
        foreach (var s in Seated(h)) if (s != spy && s != accuser) h.Act(s, "vote", new { yes = false });
        h.Tick();
        Assert.Equal("play", Phase(h));                    // пів секунди ще лишалось
        h.Tick(2);
        Assert.Equal("final", Phase(h));

    }

    [Fact]
    public void A_failed_vote_with_the_clock_at_zero_opens_the_final_vote()
    {
        var h2 = Table(4, seed: 8);
        ToPlay(h2);
        var s2 = SpySeat(h2);
        var a2 = Villager(h2);
        h2.Clock.AdvanceMs(360_000);                       // час вийшов, а тика ще не було
        Assert.True(h2.Act(a2, "accuse", new { seat = s2 }).Ok);
        Assert.Equal(0, LeftMs(h2));
        foreach (var s in Seated(h2)) if (s != s2 && s != a2) h2.Act(s, "vote", new { yes = false });
        h2.Tick();
        Assert.Equal("final", Phase(h2));
        Assert.Contains(Hlek(h2), l => SpyGlek.NotUnanimousFinal.Contains(l));
    }

    [Fact]
    public void Accusing_yourself_or_during_a_vote_is_refused()
    {
        var h = Table(4);
        Assert.Equal("Підозру висувають, поки йде раунд", h.Act(0, "accuse", new { seat = 1 }).Message);
        ToPlay(h);
        Assert.Equal("На себе не показують", h.Act(0, "accuse", new { seat = 0 }).Message);
        Assert.Equal("Не зрозумів, кого підозрюєш", h.Act(0, "accuse", new { seat = "1" }).Message);
        Assert.False(Player(h, 0).GetProperty("accused").GetBoolean());
        h.Act(0, "accuse", new { seat = 1 });
        Assert.Equal("Зачекай, іде голосування", h.Act(2, "accuse", new { seat = 3 }).Message);
        Assert.False(Player(h, 2).GetProperty("accused").GetBoolean());
        Assert.Equal("Голосування зараз нема", Table(3).Act(0, "vote", new { yes = true }).Message);
        Assert.Equal("Так чи ні?", h.Act(2, "vote", new { yes = "так" }).Message);
    }

    [Fact]
    public void A_suspect_leaving_cancels_the_vote_and_the_accuser_may_accuse_again()
    {
        var h = Table(5);
        ToPlay(h);
        var spy = SpySeat(h);
        var accuser = Villager(h);
        var innocent = Villager(h, accuser);
        h.Act(accuser, "accuse", new { seat = innocent });
        h.Leave(h.NickOf(innocent));
        Assert.Equal("play", Phase(h));
        Assert.Contains(Hlek(h), l => SpyGlek.VoteCancelled.Contains(l));
        Assert.False(Player(h, accuser).GetProperty("accused").GetBoolean());
        Assert.True(h.Act(accuser, "accuse", new { seat = spy }).Ok);
    }

    [Fact]
    public void A_suspect_leaving_a_table_of_three_folds_it_without_saying_play_on()
    {
        // Утрьох підозрюваний устав — лишилось двоє. Глек не має казати «граємо далі», щоб за мить сказати «згорнуто».
        var h = Table(3);
        ToPlay(h);
        var spy = SpySeat(h);
        var accuser = Villager(h);
        var innocent = Villager(h, accuser);
        Assert.True(h.Act(accuser, "accuse", new { seat = innocent }).Ok);
        var before = Hlek(h).Count;
        h.Leave(h.NickOf(innocent));
        Assert.Equal("done", Phase(h));
        var said = Hlek(h).Skip(before).ToList();
        Assert.DoesNotContain(said, l => SpyGlek.VoteCancelled.Contains(l));
        Assert.Single(said, l => SpyGlek.Fold.Contains(l));
        Assert.Equal(spy, h.View(null).GetProperty("reveal").GetProperty("spy").GetInt32());
    }

    // =========================================================================================
    // Вгадування
    // =========================================================================================

    [Fact]
    public void Only_the_spy_may_guess_and_only_a_deck_location()
    {
        var h = Table(4, options: new { set = "ua" });
        ToPlay(h);
        var spy = SpySeat(h);
        var v = Villager(h);
        var before = WithoutMe(h, null);
        Assert.Equal("Ти не шпигун, тобі й так усе відомо", h.Act(v, "guess", new { loc = Location(h) }).Message);
        var outside = Real.Value.All.First(l => l.Set == "classic").Id;
        Assert.Equal("Такої локації в колоді нема", h.Act(spy, "guess", new { loc = outside }).Message);
        Assert.Equal("Такої локації в колоді нема", h.Act(spy, "guess", new { loc = 5 }).Message);
        Assert.Equal(before, WithoutMe(h, null));
        Assert.Equal("play", Phase(h));
    }

    [Fact]
    public void A_right_guess_gives_the_spy_four_points_and_the_spy_guess_achievement()
    {
        var h = Table(4);
        ToPlay(h);
        var spy = SpySeat(h);
        var loc = Location(h);
        Assert.True(h.Act(spy, "guess", new { loc }).Ok);
        Assert.Equal("reveal", Phase(h));
        var reveal = h.View(null).GetProperty("reveal");
        Assert.Equal("guessed", reveal.GetProperty("how").GetString());
        Assert.Equal(loc, reveal.GetProperty("guess").GetString());
        Assert.Equal(Spy.SpyGuessPts, Score(h, spy));
        Assert.All(Villagers(h), s => Assert.Equal(0, Score(h, s)));
        Assert.Contains(h.Awards, a => a.Reason == "ach:spy-guess" && a.Nick == h.NickOf(spy));
        Assert.NotNull(AchievementCatalog.Get("spy-guess"));
    }

    [Fact]
    public void A_wrong_guess_gives_every_present_non_spy_one_point()
    {
        var h = Table(5);
        ToPlay(h);
        var spy = SpySeat(h);
        var gone = Villager(h);
        h.Leave(h.NickOf(gone));
        var wrong = WrongLoc(h);
        Assert.True(h.Act(spy, "guess", new { loc = wrong }).Ok);
        var reveal = h.View(null).GetProperty("reveal");
        Assert.Equal("misguess", reveal.GetProperty("how").GetString());
        Assert.Equal(wrong, reveal.GetProperty("guess").GetString());
        Assert.Equal(0, Score(h, spy));
        Assert.Equal(0, Score(h, gone));                   // хто встав, за раунд нічого не бере
        foreach (var s in Seated(h).Where(s => s != spy)) Assert.Equal(Spy.VillagePts, Score(h, s));
    }

    [Fact]
    public void Guessing_during_an_accusation_vote_is_refused_but_allowed_in_the_final_vote()
    {
        var h = Table(4);
        var spy = SpySeat(h);
        Assert.Equal("Зараз не час вгадувати", h.Act(spy, "guess", new { loc = Location(h) }).Message);   // роздача
        ToPlay(h);
        var accuser = Villager(h);
        h.Act(accuser, "accuse", new { seat = spy });
        Assert.Equal("Зачекай, іде голосування", h.Act(spy, "guess", new { loc = Location(h) }).Message);
        Until(h, () => Phase(h) == "play", 200);
        h.Clock.AdvanceMs(400_000);
        h.Tick();
        Assert.Equal("final", Phase(h));
        foreach (var s in Villagers(h)) h.Act(s, "blame", new { seat = spy });   // на нього всі показують…
        Assert.True(h.Act(spy, "guess", new { loc = Location(h) }).Ok);          // …а він устигає вгадати
        Assert.Equal("guessed", h.View(null).GetProperty("reveal").GetProperty("how").GetString());
        Assert.Equal(Spy.SpyGuessPts, Score(h, spy));
    }

    // =========================================================================================
    // Фінал
    // =========================================================================================

    [Fact]
    public void When_time_runs_out_the_final_vote_opens_for_forty_five_seconds()
    {
        var h = Table(4);
        ToPlay(h);
        Assert.Equal("Ще не час — фінальне голосування буде, коли вийде час", h.Act(0, "blame", new { seat = 1 }).Message);
        h.Tick(360_000 / Spy.TickMs - 1);
        Assert.Equal("play", Phase(h));
        h.Tick();
        Assert.Equal("final", Phase(h));
        Assert.Equal(Spy.FinalMs, h.View(null).GetProperty("phaseMs").GetInt32());
        Assert.Equal(0, LeftMs(h));
        Assert.Contains(Hlek(h), l => SpyGlek.TimeUp.Contains(l));
        var blame = h.View(null).GetProperty("blame");
        Assert.Equal(3, blame.GetProperty("need").GetInt32());
        h.Tick(Spy.FinalMs / Spy.TickMs - 1);
        Assert.Equal("final", Phase(h));
        h.Tick();
        Assert.Equal("reveal", Phase(h));
        Assert.Equal("timeout", h.View(null).GetProperty("reveal").GetProperty("how").GetString());
    }

    [Fact]
    public void A_strict_majority_on_the_spy_catches_them_and_only_those_who_pointed_at_the_spy_get_the_bonus()
    {
        var h = Table(5);
        ToPlay(h);
        h.Clock.AdvanceMs(360_000);
        h.Tick();
        var spy = SpySeat(h);
        var vs = Villagers(h);
        h.Act(vs[0], "blame", new { seat = spy });
        h.Act(vs[1], "blame", new { seat = spy });
        h.Act(vs[2], "blame", new { seat = spy });
        Assert.Equal("На себе не показують", h.Act(vs[3], "blame", new { seat = vs[3] }).Message);
        var votes = h.View(null).GetProperty("blame").GetProperty("votes");
        Assert.Equal(spy, votes.GetProperty(vs[0].ToString()).GetInt32());   // голоси відкриті
        h.Act(vs[3], "blame", new { seat = vs[0] });        // не влучив
        Until(h, () => Phase(h) == "reveal", 200);
        var reveal = h.View(null).GetProperty("reveal");
        Assert.Equal("caught", reveal.GetProperty("how").GetString());
        Assert.Equal(JsonValueKind.Null, reveal.GetProperty("accuser").ValueKind);
        // Селу по очку, а «влучне око» — хто показав саме на шпигуна — ще одне, як обвинувач за підозру.
        Assert.Equal([2L, 2L, 2L, 1L], vs.Select(v => Score(h, v)).ToArray());
        Assert.Equal(0, Score(h, spy));
        Assert.Equal(2, reveal.GetProperty("gained").GetProperty(vs[0].ToString()).GetInt64());
        // Хто на кого показав — на розкритті видно всім.
        var pointed = reveal.GetProperty("pointed");
        Assert.Equal(spy, pointed.GetProperty(vs[1].ToString()).GetInt32());
        Assert.Equal(vs[0], pointed.GetProperty(vs[3].ToString()).GetInt32());
        Assert.Contains(Hlek(h), l => SpyGlek.CaughtFinal.Any(p => l.StartsWith(p[..12], StringComparison.Ordinal)));
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:spy-catch");   // ачівка — лише за підозру
    }

    [Fact]
    public void A_majority_on_an_innocent_gives_the_spy_four_and_no_majority_gives_two()
    {
        var h = Table(4);
        ToPlay(h);
        h.Clock.AdvanceMs(360_000);
        h.Tick();
        var spy = SpySeat(h);
        var vs = Villagers(h);
        h.Act(spy, "blame", new { seat = vs[0] });
        h.Act(vs[1], "blame", new { seat = vs[0] });
        h.Act(vs[2], "blame", new { seat = vs[0] });
        h.Act(vs[0], "blame", new { seat = spy });
        h.Tick();
        Assert.Equal("wrong", h.View(null).GetProperty("reveal").GetProperty("how").GetString());
        Assert.Equal(Spy.SpyFramedPts, Score(h, spy));

        // 2 з 4 — це не більшість
        var t = Table(4, seed: 6);
        ToPlay(t);
        t.Clock.AdvanceMs(360_000);
        t.Tick();
        var ts = SpySeat(t);
        var tv = Villagers(t);
        t.Act(tv[0], "blame", new { seat = ts });
        t.Act(tv[1], "blame", new { seat = ts });
        t.Act(tv[2], "blame", new { seat = tv[0] });
        t.Act(ts, "blame", new { seat = tv[0] });
        t.Tick();
        Assert.Equal("timeout", t.View(null).GetProperty("reveal").GetProperty("how").GetString());
        Assert.Equal(Spy.SpyTimeoutPts, Score(t, ts));
        Assert.All(tv, v => Assert.Equal(0, Score(t, v)));      // без вироку бонусу «влучного ока» нема
        Assert.Equal(4, t.View(null).GetProperty("reveal").GetProperty("pointed").EnumerateObject().Count());
    }

    [Fact]
    public void The_final_vote_resolves_early_when_everyone_pointed()
    {
        var h = Table(3);
        ToPlay(h);
        h.Clock.AdvanceMs(360_000);
        h.Tick();
        var spy = SpySeat(h);
        var vs = Villagers(h);
        h.Act(vs[0], "blame", new { seat = spy });
        h.Act(vs[1], "blame", new { seat = vs[0] });     // передумає
        h.Tick();
        Assert.Equal("final", Phase(h));
        h.Act(vs[1], "blame", new { seat = spy });
        h.Act(spy, "blame", new { seat = vs[0] });
        h.Tick();
        Assert.Equal("reveal", Phase(h));
        Assert.Equal("caught", h.View(null).GetProperty("reveal").GetProperty("how").GetString());
    }

    // =========================================================================================
    // Раунди й кінець партії
    // =========================================================================================

    [Fact]
    public void Reveal_lasts_fifteen_seconds_or_until_all_present_are_ready()
    {
        var h = Table(4);
        Misguess(h);
        Assert.Equal(Spy.RevealMs, h.View(null).GetProperty("phaseMs").GetInt32());
        Assert.Equal("Готовність потрібна лише на розкритті", Table(3).Act(0, "ready", new { }).Message);
        h.Tick(Spy.RevealMs / Spy.TickMs - 1);
        Assert.Equal("reveal", Phase(h));
        h.Tick();
        Assert.Equal("deal", Phase(h));
        Assert.Equal(2, Round(h));

        Misguess(h);
        foreach (var s in Seated(h).Skip(1)) h.Act(s, "ready", new { });
        h.Tick();
        Assert.Equal("reveal", Phase(h));
        Assert.True(Player(h, Seated(h).Last()).GetProperty("ready").GetBoolean());
        h.Act(Seated(h).First(), "ready", new { });
        h.Tick();
        Assert.Equal("deal", Phase(h));
        Assert.Equal(3, Round(h));
    }

    [Fact]
    public void After_the_last_round_the_match_finishes_with_the_leaders_scores_and_a_journal_line()
    {
        var h = Table(4, options: new { rounds = "3" });
        var spyPoints = new Dictionary<int, long>();
        for (var r = 0; r < 3; r++)
        {
            ToPlay(h);
            var spy = SpySeat(h);
            h.Act(spy, "guess", new { loc = Location(h) });
            spyPoints[spy] = spyPoints.GetValueOrDefault(spy) + Spy.SpyGuessPts;
            AllReady(h);
        }
        Assert.Equal("done", Phase(h));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = h.Finished.Single();
        Assert.Equal(spyPoints.Keys.Order().ToArray(), fin.Result.Winners.Order().ToArray());
        Assert.Equal(3, h.View(null).GetProperty("history").GetArrayLength());
        foreach (var s in Seated(h)) Assert.Equal(spyPoints.GetValueOrDefault(s), fin.Result.Scores![s]);
        var text = h.Outbox.OfType<Journal>().Last().Text;
        Assert.StartsWith("Шпигун: ", text);
        foreach (var s in Seated(h)) Assert.Contains(h.NickOf(s), text);
        Assert.Equal(fin.Result.Winners.Order().ToArray(),
            h.View(null).GetProperty("result").GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()).Order().ToArray());
    }

    [Fact]
    public void Several_leaders_share_the_win()
    {
        var h = Table(3, options: new { rounds = "3" });
        for (var r = 0; r < 3; r++)
        {
            ToPlay(h);
            h.Act(SpySeat(h), "guess", new { loc = Location(h) });   // кожен по разу шпигун і вгадав: 4/4/4
            AllReady(h);
        }
        Assert.Equal(3, h.Finished.Single().Result.Winners.Length);
        Assert.Contains(Hlek(h), l => l.Contains("по 4 очки"));
    }

    [Fact]
    public void A_match_whose_only_scorer_left_ends_in_a_draw()
    {
        // Очко в раунді дістається завжди комусь, хто за столом. Нічия — коли очки пішли разом із тим, хто встав:
        // за неявку перемоги не дають, а з нулями серед присутніх переможця нема.
        var h = Table(4, options: new { rounds = "1" });
        ToPlay(h);
        var spy = SpySeat(h);
        Assert.True(h.Act(spy, "guess", new { loc = Location(h) }).Ok);
        Assert.Equal(Spy.SpyGuessPts, Score(h, spy));
        h.Leave(h.NickOf(spy));                            // пішов із розкриття разом зі своїми 4 очками
        Assert.Equal("reveal", Phase(h));
        AllReady(h);
        Assert.Equal("done", Phase(h));
        var fin = h.Finished.Single();
        Assert.True(fin.Result.Draw);
        Assert.Contains("нічия", fin.Result.Text);
        Assert.Equal(Spy.SpyGuessPts, fin.Result.Scores![spy]);
        Assert.Equal(0, h.View(null).GetProperty("result").GetProperty("winners").GetArrayLength());
        Assert.Contains(Hlek(h), l => SpyGlek.GameOverDraw.Contains(l));
    }

    [Fact]
    public void One_round_option_finishes_after_a_single_reveal()
    {
        var h = Table(4, options: new { rounds = "1" });
        Assert.Contains(Hlek(h), l => SpyGlek.RoundStartOne.Any(p => l.StartsWith(p[..10], StringComparison.Ordinal)));
        Misguess(h);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        AllReady(h);
        Assert.Equal("done", Phase(h));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(1, h.View(null).GetProperty("history").GetArrayLength());
        Assert.NotEqual(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void Rematch_resets_scores_rounds_and_spy_counts_and_deals_a_fresh_deck()
    {
        var h = Table(4, options: new { rounds = "1" });
        var deck1 = Deck(h);
        ToPlay(h);
        h.Act(SpySeat(h), "guess", new { loc = Location(h) });
        AllReady(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var seats = (string?[])h.Room.Seats.Clone();

        Assert.True(h.Rematch().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.NotEqual(seats, h.Room.Seats);             // місця обернулись
        Assert.Equal("deal", Phase(h));
        Assert.Equal(1, Round(h));
        Assert.All(Seated(h), s => Assert.Equal(0, Score(h, s)));
        Assert.Equal(0, h.View(null).GetProperty("history").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Assert.NotEqual(deck1, Deck(h));
        Assert.Contains(Location(h), Deck(h));
    }

    // =========================================================================================
    // Вихід
    // =========================================================================================

    [Fact]
    public void When_the_spy_leaves_the_round_ends_with_one_point_for_each_non_spy()
    {
        var h = Table(4);
        ToPlay(h);
        var spy = SpySeat(h);
        var villagers = Villagers(h);
        h.Leave(h.NickOf(spy));
        Assert.Equal("reveal", Phase(h));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);   // техпоразки нема
        var reveal = h.View(null).GetProperty("reveal");
        Assert.Equal("left", reveal.GetProperty("how").GetString());
        Assert.Equal(spy, reveal.GetProperty("spy").GetInt32());
        Assert.All(villagers, v => Assert.Equal(1, Score(h, v)));
        Assert.False(Player(h, spy).GetProperty("here").GetBoolean());
        Assert.Contains(Hlek(h), l => SpyGlek.Left.Any(p => l.StartsWith(p[..8], StringComparison.Ordinal)));
        AllReady(h);
        Assert.Equal("deal", Phase(h));
        Assert.DoesNotContain(spy, Seated(h));
        Assert.Contains(SpySeat(h), villagers);                // новий шпигун — із тих, хто лишився
    }

    [Fact]
    public void When_a_non_spy_leaves_the_round_goes_on_without_them_and_they_get_no_points()
    {
        var h = Table(4);
        ToPlay(h);
        var spy = SpySeat(h);
        var gone = Villager(h, Asker(h)!.Value);
        h.Leave(h.NickOf(gone));
        Assert.Equal("play", Phase(h));
        var accuser = Villager(h);
        Convict(h, accuser, spy);
        Assert.Equal("caught", h.View(null).GetProperty("reveal").GetProperty("how").GetString());
        Assert.Equal(0, Score(h, gone));
        Assert.Equal(2, Score(h, accuser));
        Assert.False(h.View(null).GetProperty("reveal").GetProperty("gained").TryGetProperty(gone.ToString(), out var g) && g.GetInt64() > 0);
    }

    [Fact]
    public void When_fewer_than_three_remain_the_match_folds_with_current_standings()
    {
        var h = Table(3, options: new { rounds = "3" });
        ToPlay(h);
        var spy = SpySeat(h);
        h.Act(spy, "guess", new { loc = Location(h) });
        AllReady(h);
        ToPlay(h);
        var spy2 = SpySeat(h);
        var leaver = Seated(h).First(s => s != spy && s != spy2);
        h.Leave(h.NickOf(leaver));
        Assert.Equal("done", Phase(h));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = h.Finished.Single();
        Assert.Equal([spy], fin.Result.Winners);
        Assert.Contains("лишилось двоє — партію згорнули", fin.Result.Text);
        Assert.Contains(h.NickOf(spy) + " 4", fin.Result.Text);
        // Недограний раунд розкриваємо (таємницю вже нема від кого берегти), але в хроніку він не йде й очок не дає.
        var reveal = h.View(null).GetProperty("reveal");
        Assert.Equal("fold", reveal.GetProperty("how").GetString());
        Assert.Equal(spy2, reveal.GetProperty("spy").GetInt32());
        Assert.All(reveal.GetProperty("gained").EnumerateObject(), g => Assert.Equal(0, g.Value.GetInt64()));
        Assert.True(h.View(null).GetProperty("result").GetProperty("folded").GetBoolean());
        Assert.Equal(1, h.View(null).GetProperty("history").GetArrayLength());
        Assert.Contains(Hlek(h), l => SpyGlek.Fold.Contains(l));
    }

    [Fact]
    public void When_fewer_than_three_remain_before_any_round_ended_it_is_a_draw()
    {
        var h = Table(3);
        h.Leave(h.NickOf(Villager(h)));                    // ще на роздачі, і пішов не шпигун
        Assert.Equal("done", Phase(h));
        var fin = h.Finished.Single();
        Assert.True(fin.Result.Draw);
        Assert.Contains("партії не вийшло", fin.Result.Text);
        Assert.Equal("fold", h.View(null).GetProperty("reveal").GetProperty("how").GetString());
        Assert.Equal(0, h.View(null).GetProperty("result").GetProperty("winners").GetArrayLength());
        Assert.True(h.View(null).GetProperty("result").GetProperty("folded").GetBoolean());
        Assert.Equal(0, h.View(null).GetProperty("history").GetArrayLength());
    }

    /// <summary>Стіл, де перший, хто питає, — не шпигун (інакше його вихід закрив би раунд).</summary>
    static RoomHarness TableWithHonestAsker(int players)
    {
        for (var seed = 1; ; seed++)
        {
            var h = Table(players, seed: seed);
            ToPlay(h);
            if (Asker(h) != SpySeat(h)) return h;
        }
    }

    [Fact]
    public void The_asker_pointer_moves_when_the_asker_leaves()
    {
        var h = TableWithHonestAsker(5);
        var a = Asker(h)!.Value;
        var b = Villager(h, a);
        Assert.True(h.Act(a, "ask", new { seat = b }).Ok);   // тепер питає b, а «щойно питав» — a
        h.Leave(h.NickOf(b));
        Assert.Equal("play", Phase(h));
        Assert.Equal(a, Asker(h));                         // слово вертається до того, хто питав
        Assert.Null(AskedBy(h));

        // Того, хто питав, за столом уже нема — слово бере перший за столом.
        var h2 = TableWithHonestAsker(5);
        var first = Asker(h2)!.Value;
        h2.Leave(h2.NickOf(first));
        Assert.Equal(Seated(h2).First(), Asker(h2));
        Assert.Null(AskedBy(h2));
    }

    [Fact]
    public void A_voter_leaving_shrinks_the_quorum_and_a_pointer_at_them_is_forgotten()
    {
        var h = Table(5);
        ToPlay(h);
        h.Clock.AdvanceMs(360_000);
        h.Tick();
        var spy = SpySeat(h);
        var vs = Villagers(h);
        h.Act(vs[0], "blame", new { seat = vs[3] });
        h.Act(vs[1], "blame", new { seat = spy });
        h.Leave(h.NickOf(vs[3]));                          // на нього показали — голос за нього згорів
        var blame = h.View(null).GetProperty("blame");
        Assert.False(blame.GetProperty("votes").TryGetProperty(vs[0].ToString(), out _));
        Assert.Equal(3, blame.GetProperty("need").GetInt32());   // 4 присутніх → більшість із 3

        var v = Table(5, seed: 2);
        ToPlay(v);
        var vspy = SpySeat(v);
        var vv = Villagers(v);
        v.Act(vv[0], "accuse", new { seat = vspy });
        v.Act(vv[1], "vote", new { yes = true });
        v.Act(vv[2], "vote", new { yes = true });
        Assert.Equal(4, v.View(null).GetProperty("vote").GetProperty("need").GetInt32());
        v.Leave(v.NickOf(vv[3]));                          // мовчун устав — лишились самі «так»
        Assert.Equal(3, v.View(null).GetProperty("vote").GetProperty("need").GetInt32());
        v.Tick();
        Assert.Equal("caught", v.View(null).GetProperty("reveal").GetProperty("how").GetString());
    }

    // =========================================================================================
    // Приховане й дріт
    // =========================================================================================

    [Fact]
    public void The_spectator_view_has_no_me_no_location_and_no_spy_until_the_reveal()
    {
        var h = Table(4);
        var spy = SpySeat(h);
        var loc = Location(h);
        var roles = Villagers(h).Select(s => Role(h, s)).ToArray();
        foreach (var phase in new[] { "deal", "play" })
        {
            To(h, phase);
            var text = h.View(null).GetRawText();
            Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("me").ValueKind);
            Assert.DoesNotContain("\"" + loc + "\"", text.Replace("[\"" + loc + "\"", ""));   // лише в колоді
            Assert.All(roles, r => Assert.DoesNotContain(Views.Text(r).Trim('"'), text));
            Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
            Assert.DoesNotContain("\"spy\"", text);
        }
        Assert.Equal(Spy.DeckSize, Deck(h).Length);        // колоду глядач бачить
        // Глек посеред раунду не проговорився ні локацією, ні шпигуном у ролі шпигуна.
        var title = Real.Value.All.First(l => l.Id == loc).Title;
        Assert.All(Hlek(h), l => Assert.DoesNotContain(title, l));
        _ = spy;
    }

    [Fact]
    public void Views_of_all_seats_differ_only_in_me()
    {
        var h = Table(6);
        void Check()
        {
            var spectator = WithoutMe(h, null);
            foreach (var s in Seated(h)) Assert.Equal(spectator, WithoutMe(h, s));
        }
        Check();                                           // deal
        ToPlay(h);
        Check();
        var spy = SpySeat(h);
        h.Act(Villager(h), "accuse", new { seat = spy });
        Check();                                           // vote
        Until(h, () => Phase(h) == "play", 200);
        h.Clock.AdvanceMs(400_000);
        h.Tick();
        Assert.Equal("final", Phase(h));
        h.Act(Villager(h), "blame", new { seat = spy });
        Check();                                           // final
        // а свої картки справді різні
        Assert.NotEqual(Me(h, spy).GetRawText(), Me(h, Villager(h)).GetRawText());
    }

    [Fact]
    public void The_frame_carries_nothing_hidden_and_is_under_300_bytes()
    {
        var h = Table(10);
        ToPlay(h);
        var loc = Location(h);
        var roles = Villagers(h).Select(s => Role(h, s)).ToArray();
        foreach (var phase in new[] { "play", "vote" })
        {
            if (phase == "vote") h.Act(Villager(h), "accuse", new { seat = SpySeat(h) });
            var text = Views.Text(h.Room.Game.Frame());
            Assert.True(text.Length < 300, $"кадр {text.Length} Б: {text}");
            Assert.DoesNotContain(loc, text);
            Assert.DoesNotContain("\"me\"", text);
            Assert.DoesNotContain("\"spy\"", text);
            Assert.DoesNotContain("\"role\"", text);
            Assert.All(roles, r => Assert.DoesNotContain(Views.Text(r).Trim('"'), text));
            var f = Views.Json(h.Room.Game.Frame());
            Assert.Equal(phase, f.GetProperty("phase").GetString());
            foreach (var name in new[] { "round", "of", "endsAt", "phaseMs", "clockLeftMs", "paused", "asker" })
                Assert.True(Views.Has(f, name), name);
        }
        output.WriteLine($"кадр на 10 гравцях: {Views.Text(h.Room.Game.Frame()).Length} Б");
    }

    [Fact]
    public void Reveal_and_history_show_the_spy_and_location_to_everyone_after_the_round()
    {
        var h = Table(4);
        ToPlay(h);
        var spy = SpySeat(h);
        var loc = Location(h);
        var roles = Villagers(h).ToDictionary(s => s, s => Role(h, s));
        h.Act(spy, "guess", new { loc = WrongLoc(h) });
        var reveal = h.View(null).GetProperty("reveal");
        Assert.Equal(spy, reveal.GetProperty("spy").GetInt32());
        Assert.Equal(loc, reveal.GetProperty("loc").GetString());
        foreach (var (s, r) in roles) Assert.Equal(r, reveal.GetProperty("roles").GetProperty(s.ToString()).GetString());
        Assert.False(reveal.GetProperty("roles").TryGetProperty(spy.ToString(), out _));
        var hist = h.View(null).GetProperty("history")[0];
        Assert.Equal((1, spy, loc, "misguess"), (hist.GetProperty("round").GetInt32(), hist.GetProperty("spy").GetInt32(),
            hist.GetProperty("loc").GetString(), hist.GetProperty("how").GetString()));
        AllReady(h);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);   // новий раунд — розкриття сховано
        Assert.Equal(1, h.View(null).GetProperty("history").GetArrayLength());
    }

    [Fact]
    public void A_newcomer_at_a_finished_table_is_a_spectator()
    {
        var h = Table(3);
        var gone = Seated(h).First();
        h.Leave(h.NickOf(gone));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Join("Остап").Ok);
        var seat = Array.IndexOf(h.Room.Seats, "Остап");
        Assert.Equal(gone, seat);
        var view = h.View(seat);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("me").ValueKind);
        var mine = view.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat);
        Assert.Equal("Остап", mine.GetProperty("nick").GetString());
        Assert.Equal(0, mine.GetProperty("score").GetInt64());
        lock (h.Room.Sync) Assert.Null(h.Room.Game.TalkBlock(seat));
        Assert.Equal(Crew[gone], h.View(null).GetProperty("names").GetProperty(gone.ToString()).GetString());
        // Нова партія — і новачок уже грає.
        Assert.True(h.Start().Ok);
        Assert.Equal(JsonValueKind.Object, h.View(seat).GetProperty("me").ValueKind);
    }

    [Fact]
    public void A_newcomer_on_a_vacated_seat_does_not_take_the_leavers_name_in_the_chronicle()
    {
        // Шпигун устав, партію дограли, і на його звільнене місце сів новачок: у «Як це було» шпигуном має лишитись
        // той, хто грав, а не новачок. Імена партії вид віддає окремо — players після партії показує вже новачка.
        var h = Table(4, options: new { rounds = "1" });
        ToPlay(h);
        var spy = SpySeat(h);
        var spyNick = h.NickOf(spy)!;
        h.Leave(spyNick);
        AllReady(h);
        Assert.Equal("done", Phase(h));
        Assert.True(h.Join("Чужинець").Ok);
        Assert.Equal(spy, Array.IndexOf(h.Room.Seats, "Чужинець"));
        var view = h.View(null);
        Assert.Equal(spy, view.GetProperty("history")[0].GetProperty("spy").GetInt32());
        Assert.Equal("Чужинець", view.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == spy).GetProperty("nick").GetString());
        var names = view.GetProperty("names");
        Assert.Equal(spyNick, names.GetProperty(spy.ToString()).GetString());
        Assert.Equal(4, names.EnumerateObject().Count());
        // посеред партії імен окремо не шлемо — там players і так правдиві
        Assert.Equal(JsonValueKind.Null, Table(3).View(null).GetProperty("names").ValueKind);
    }

    [Fact]
    public void The_view_shape_matches_the_spec()
    {
        var h = Table(4);
        ToPlay(h);
        var view = h.View(0);
        foreach (var name in new[] { "phase", "round", "of", "endsAt", "phaseMs", "phaseLeftMs", "clock", "rules", "players", "asker", "askedBy",
                     "askGrace", "vote", "blame", "deck", "me", "reveal", "history", "result", "names" })
            Assert.True(Views.Has(view, name), name);
        foreach (var name in new[] { "endsAt", "leftMs", "paused", "totalMs" }) Assert.True(Views.Has(view.GetProperty("clock"), name), name);
        foreach (var name in new[] { "minutes", "rounds", "sets", "dealMs", "voteMs", "finalMs", "revealMs", "askGraceMs", "each" })
            Assert.True(Views.Has(view.GetProperty("rules"), name), name);
        foreach (var name in new[] { "seat", "nick", "here", "score", "accused", "ready" })
            Assert.True(Views.Has(view.GetProperty("players")[0], name), name);
        foreach (var name in new[] { "spy", "loc", "role" }) Assert.True(Views.Has(view.GetProperty("me"), name), name);
        Assert.Equal(JsonValueKind.String, view.GetProperty("endsAt").ValueKind);
        Assert.Equal(JsonValueKind.Number, view.GetProperty("asker").ValueKind);
        Assert.Equal(JsonValueKind.Array, view.GetProperty("deck").ValueKind);
        Assert.Equal(Crew[0], view.GetProperty("players")[0].GetProperty("nick").GetString());
        Assert.Equal("play", view.GetProperty("phase").GetString());
        Assert.Equal(LeftMs(h), view.GetProperty("phaseLeftMs").GetInt64());   // у play фаза кінчається разом із годинником

        h.Act(1, "accuse", new { seat = 2 });
        var vote = h.View(0).GetProperty("vote");
        foreach (var name in new[] { "suspect", "accuser", "votes", "need", "endsAt" }) Assert.True(Views.Has(vote, name), name);
        h.Act(3, "vote", new { yes = false });
        h.Act(0, "vote", new { yes = false });
        h.Tick();
        h.Act(SpySeat(h), "guess", new { loc = Location(h) });
        var reveal = h.View(0).GetProperty("reveal");
        foreach (var name in new[] { "spy", "loc", "roles", "how", "gained", "guess", "suspect", "accuser", "pointed" })
            Assert.True(Views.Has(reveal, name), name);
        AllReady(h);
        for (var r = 2; r <= 3; r++)
        {
            ToPlay(h);
            h.Act(SpySeat(h), "guess", new { loc = Location(h) });
            AllReady(h);
        }
        Assert.Equal("done", Phase(h));
        var result = h.View(0).GetProperty("result");
        foreach (var name in new[] { "winners", "folded" }) Assert.True(Views.Has(result, name), name);
        Assert.Equal(JsonValueKind.Object, h.View(0).GetProperty("names").ValueKind);
    }

    [Fact]
    public void Payloads_are_accepted_as_objects_and_bare_values_and_rubbish_is_refused()
    {
        // Рівно те, що шле модуль web/games/spy.js: {seat}, {yes}, {loc}, {}.
        var h = Table(5);
        ToPlay(h);
        var a = Asker(h)!.Value;
        var t = Seated(h).First(s => s != a);
        Assert.True(h.Act(a, "ask", new { seat = t }).Ok);
        var t2 = Seated(h).First(s => s != a && s != t);
        Assert.True(h.Act(t, "ask", t2).Ok);               // голе число теж годиться
        Assert.False(h.Act(t2, "ask", "2").Ok);
        Assert.Equal("Тут так не ходять", h.Act(0, "dance", new { }).Message);

        var spy = SpySeat(h);
        var acc = Villager(h);
        Assert.True(h.Act(acc, "accuse", new { seat = spy }).Ok);
        var voters = Seated(h).Where(s => s != spy && s != acc).ToArray();
        Assert.True(h.Act(voters[0], "vote", new { yes = true }).Ok);
        Assert.True(h.Act(voters[1], "vote", true).Ok);    // голе true
        Assert.Equal("Так чи ні?", h.Act(voters[2], "vote", new { }).Message);
        Assert.Equal("Так чи ні?", h.Act(voters[2], "vote", 1).Message);
        Assert.True(h.Act(voters[2], "vote", false).Ok);
        h.Tick();
        Assert.Equal("play", Phase(h));
        Assert.True(h.Act(spy, "guess", Location(h)).Ok);  // голий рядок
        Assert.True(h.Act(voters[0], "ready", new { }).Ok);
        Assert.True(h.Act(voters[1], "ready", null).Ok);
    }

    [Fact]
    public void Uncle_Hlek_speaks_at_round_start_accusation_verdict_and_match_end_and_never_per_question()
    {
        var h = Table(4, options: new { rounds = "1" });
        Assert.Single(Hlek(h));                            // старт раунду
        Assert.All(h.Outbox.OfType<TableSaid>(), x => Assert.Equal("dj", x.Line.Kind));
        ToPlay(h);
        var n = Hlek(h).Count;
        for (var i = 0; i < 6; i++)
        {
            var a = Asker(h)!.Value;
            var target = Seated(h).First(s => s != a && s != AskedBy(h));
            Assert.True(h.Act(a, "ask", new { seat = target }).Ok);
            h.Tick(10);
        }
        Assert.Equal(n, Hlek(h).Count);                    // жодного слова про питання
        var spy = SpySeat(h);
        h.Act(Villager(h), "accuse", new { seat = spy });
        Assert.Equal(n + 1, Hlek(h).Count);                // підозра
        foreach (var s in Seated(h)) if (s != spy) h.Act(s, "vote", new { yes = false });
        h.Tick();
        Assert.Equal("play", Phase(h));
        Assert.Equal(n + 1, Hlek(h).Count);                // невдала підозра — мовчки: картка й так показує
        h.Act(spy, "guess", new { loc = WrongLoc(h) });
        Assert.Equal(n + 2, Hlek(h).Count);                // вердикт
        foreach (var s in Seated(h)) h.Act(s, "ready", new { });
        h.Tick();
        Assert.Equal(n + 3, Hlek(h).Count);                // кінець партії
        Assert.All(SpyGlek.All, l => { Assert.False(string.IsNullOrWhiteSpace(l)); Assert.DoesNotContain('<', l); });
        // Нік буває «гість Петро»: рядок, що починається з ніка чи ставить його одразу після «!»/«.», вийшов би з малої.
        Assert.All(SpyGlek.All, l => Assert.DoesNotMatch(@"^\{|[.!?…] \{", l));
    }

    [Fact]
    public void Spectators_are_silenced_during_play_and_free_before_and_after()
    {
        var h = new RoomHarness("spy", services: RoomHarness.WithService(Real.Value));
        foreach (var n in Crew.Take(3)) h.Join(n);
        h.Rooms.Watch(h.RoomId, "c-глядач", "Тарас");
        string? Talk(string nick, string? conn = null)
        {
            var (outbox, error) = h.Rooms.TableSay(h.RoomId, conn, nick, "а я знаю, де ви!");
            h.Outbox.AddRange(outbox);
            return error;
        }
        Assert.Null(Talk("Тарас", "c-глядач"));            // лобі
        h.Start();
        Assert.Equal("Поки йде партія, глядачі мовчать", Talk("Тарас", "c-глядач"));
        Assert.Null(Talk("Оля"));
        var spy = SpySeat(h);
        ToPlay(h);
        Assert.Equal("Поки йде партія, глядачі мовчать", Talk("Тарас", "c-глядач"));
        h.Act(spy, "guess", new { loc = WrongLoc(h) });
        Assert.Null(Talk("Тарас", "c-глядач"));            // розкриття — балакають усі
        h.Leave(h.NickOf(Seated(h).First()));
        Assert.Null(Talk("Тарас", "c-глядач"));            // після партії
    }

    [Fact]
    public void A_move_makes_the_next_tick_carry_fresh_views_and_a_phase_change_carries_a_frame()
    {
        var h = Table(4);
        ToPlay(h);
        var n = h.Outbox.Count;
        h.Tick();
        Assert.DoesNotContain(h.Outbox.Skip(n), m => m is RoomViews or RoomFrame);   // порожній тик — нічого

        var a = Asker(h)!.Value;
        n = h.Outbox.Count;
        h.Act(a, "ask", new { seat = Seated(h).First(s => s != a) });
        Assert.DoesNotContain(h.Outbox.Skip(n), m => m is RoomViews);                 // реалтайм: види шле тик
        h.Tick();
        Assert.Contains(h.Outbox.Skip(n), m => m is RoomViews);
        Assert.DoesNotContain(h.Outbox.Skip(n), m => m is RoomFrame);                 // фаза та сама — кадру нема

        n = h.Outbox.Count;
        h.Clock.AdvanceMs(400_000);
        h.Tick();
        Assert.Contains(h.Outbox.Skip(n), m => m is RoomFrame);
        Assert.Contains(h.Outbox.Skip(n), m => m is RoomViews);
    }

    [Fact]
    public void Acting_outside_the_match_is_refused_with_the_right_words()
    {
        var lobby = new RoomHarness("spy", services: RoomHarness.WithService(Real.Value));
        foreach (var n in Crew.Take(3)) lobby.Join(n);
        Assert.Equal("Чекаємо на гравців", lobby.Act(0, "ask", new { seat = 1 }).Message);   // каркас сам

        var h = Table(3);
        var game = h.Room.Game;
        lock (h.Room.Sync) Assert.Equal("Ти тут не граєш", game.Act(7, "ask", Views.Payload(new { seat = 1 })).Message);
        h.Leave(h.NickOf(0));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        // Текст відмови — каркасний Say.Played (internal, тестам не видно): тут «Ще раз», а в main уже «Ану ще раз».
        // Тож звіряємо без регістру — тест має пережити злиття, а не знати назву кнопки.
        string played;
        lock (h.Room.Sync) played = game.Act(1, "ask", Views.Payload(new { seat = 2 })).Message;
        Assert.StartsWith("Партію зіграно", played);
        Assert.Contains("ще раз", played, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Input_goes_through_exactly_the_same_rules_as_Act()
    {
        // Гра реалтаймова (TickMs > 0), тож каркас пускає в Act і шлях Input (без відповіді). Відрізнити його гра не
        // може — тому всі правила однакові: чужа черга, не шпигун, повтор — ті самі відмови, стан не міняється.
        var h = Table(4);
        ToPlay(h);
        var a = Asker(h)!.Value;
        var other = Seated(h).First(s => s != a);
        var target = Seated(h).First(s => s != a && s != other);
        var before = WithoutMe(h, null);
        h.Input(other, "ask", new { seat = target });                 // не його черга
        h.Input(Villager(h), "guess", new { loc = Location(h) });     // не шпигун
        h.Input(other, "vote", new { yes = true });                   // голосування нема
        Assert.Equal(before, WithoutMe(h, null));
        h.Input(a, "ask", new { seat = target });                     // легальне — те саме, що через Act
        Assert.Equal(target, Asker(h));
        h.Input(SpySeat(h), "guess", new { loc = WrongLoc(h) });
        Assert.Equal("misguess", h.View(null).GetProperty("reveal").GetProperty("how").GetString());
    }

    [Fact]
    public void Points_and_names_read_like_ukrainian()
    {
        Assert.Equal("1 очко", SpyGlek.Points(1));
        Assert.Equal("3 очки", SpyGlek.Points(3));
        Assert.Equal("5 очок", SpyGlek.Points(5));
        Assert.Equal("11 очок", SpyGlek.Points(11));
        Assert.Equal("12 очок", SpyGlek.Points(12));
        Assert.Equal("21 очко", SpyGlek.Points(21));
        Assert.Equal("22 очки", SpyGlek.Points(22));
        Assert.Equal("Оля й Петро", SpyGlek.Names(["Оля", "Петро"]));
        Assert.Equal("Оля, Петро й Ганна", SpyGlek.Names(["Оля", "Петро", "Ганна"]));
        // кожен шаблон приймає свої аргументи без винятку
        var rng = new Random(1);
        foreach (var pool in new[] { SpyGlek.RoundStart, SpyGlek.RoundStartOne })
            foreach (var _ in pool) Assert.DoesNotContain("{", SpyGlek.Pick(rng, pool, 1, 3, "Оля"));
        foreach (var _ in SpyGlek.Framed) Assert.DoesNotContain("{", SpyGlek.Pick(rng, SpyGlek.Framed, "Оля", "Петро", "Пасіка"));
        foreach (var _ in SpyGlek.Misguess) Assert.DoesNotContain("{", SpyGlek.Pick(rng, SpyGlek.Misguess, "Оля", "Базар", "Пасіка"));
        foreach (var _ in SpyGlek.CaughtBy) Assert.DoesNotContain("{", SpyGlek.Pick(rng, SpyGlek.CaughtBy, "Оля", "Пасіка", "Петро"));
    }

    // =========================================================================================
    // Швидкодія й розміри
    // =========================================================================================

    [Fact]
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_of_a_ten_player_table_with_actions_stay_under_a_second()
    {
        var h = Table(10, options: new { rounds = "5" });
        var sw = new Stopwatch();
        var tickOnly = new Stopwatch();
        var rounds = 0;
        for (var i = 0; i < 3000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) { h.Rematch(); rounds++; }
            var v = h.View(null);
            var ph = v.GetProperty("phase").GetString();
            if (ph == "play" && i % 40 == 0)
            {
                var a = v.GetProperty("asker").GetInt32();
                var by = v.GetProperty("askedBy").ValueKind == JsonValueKind.Number ? v.GetProperty("askedBy").GetInt32() : -1;
                var t = Seated(h).First(s => s != a && s != by);
                h.Act(a, "ask", new { seat = t });
            }
            if (ph == "play" && i % 160 == 80)
            {
                var accuser = Seated(h).FirstOrDefault(s => !Player(h, s).GetProperty("accused").GetBoolean(), -1);
                if (accuser >= 0) h.Act(accuser, "accuse", new { seat = Seated(h).First(s => s != accuser) });
                foreach (var s in Seated(h)) h.Act(s, "vote", new { yes = s % 3 != 0 });
            }
            if (ph == "play" && i % 160 == 150) h.Act(SpySeat(h), "guess", new { loc = Deck(h)[i % Spy.DeckSize] });
            if (ph == "reveal" && i % 8 == 0) foreach (var s in Seated(h)) h.Act(s, "ready", new { });

            h.Clock.AdvanceMs(Spy.TickMs);
            sw.Start();
            foreach (var room in h.Rooms.TickDue(h.Clock.UtcNow)) h.Outbox.AddRange(h.Rooms.Tick(room));
            sw.Stop();
        }
        // голий Tick() гри — без каркаса й розсилки
        var bare = Table(10);
        ToPlay(bare);
        lock (bare.Room.Sync)
        {
            tickOnly.Start();
            for (var i = 0; i < 100_000; i++) bare.Room.Game.Tick();
            tickOnly.Stop();
        }
        output.WriteLine($"3000 тиків столу на 10 (Rooms.Tick): {sw.Elapsed.TotalMilliseconds:F2} мс, у середньому {sw.Elapsed.TotalMilliseconds / 3000 * 1000:F2} мкс; партій: {rounds + 1}");
        output.WriteLine($"голий Tick() гри: {tickOnly.Elapsed.TotalMilliseconds / 100_000 * 1000:F3} мкс");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"3000 тиків зайняли {sw.Elapsed}");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_seat_view_with_a_full_deck_stays_under_4_KB()
    {
        var h = Table(10);
        ToPlay(h);
        var sizes = new List<int>();
        foreach (var s in Seated(h)) sizes.Add(Views.Text(h.Room.Game.View(s)).Length);
        sizes.Add(Views.Text(h.Room.Game.View(null)).Length);
        output.WriteLine($"вид у play на 10: {sizes.Min()}–{sizes.Max()} Б");
        Assert.All(sizes, n => Assert.True(n < 4096, $"вид {n} Б"));

        // Скільки коштує зібрати й серіалізувати всі 11 видів на один «брудний» тик.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
            foreach (int? s in Seated(h).Cast<int?>().Append(null)) _ = Views.Text(h.Room.Game.View(s));
        sw.Stop();
        output.WriteLine($"11 видів (збірка + JSON): {sw.Elapsed.TotalMilliseconds / 200:F3} мс");

        h.Act(SpySeat(h), "guess", new { loc = Location(h) });
        var reveal = Views.Text(h.Room.Game.View(0)).Length;
        output.WriteLine($"вид на розкритті на 10: {reveal} Б");
        Assert.True(reveal < 8192, $"розкриття {reveal} Б");
    }

    /// <summary>
    /// Прохід 28.09: без поля <c>added</c> у модулі плитка не світиться «🆕 нова гра» (core.js, isNewGame) — за перший день на
    /// проді «Шпигуна» не зіграли жодного разу. Лобі пояснює гру («Як грати»), а плитка каже, що питати можна й уголос і що
    /// треба щонайменше троє.
    /// </summary>
    [Fact]
    public void The_module_marks_the_game_new_and_the_lobby_explains_how_to_play()
    {
        var js = File.ReadAllText(Paths.Resolve("web/games/spy.js"));
        Assert.Matches(@"added:\s*'2026-09-27'", js);
        Assert.Contains("class=\"spy-how\"", js);
        var hint = new Spy().Info.Hint;
        Assert.Contains("вголос", hint);
        Assert.Contains("Троє й більше", hint);
    }
}
