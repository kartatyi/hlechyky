using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Морський бій, прохід №3: годинник ходу опцією (№61), тихі тости (№63), слово Глека (№60), реакції (№62),
/// остання помста (№57) і «Глек підсідає» — бот на порожнє місце.
/// </summary>
public class BattleshipSweep3Tests
{
    static readonly int[][] Blue =
        [[0, 1, 2, 3], [5, 6, 7], [20, 21, 22], [24, 25], [27, 28], [40, 41], [43], [45], [47], [49]];
    static readonly int[][] Red =
        [[0, 10, 20, 30], [2, 12, 22], [4, 14, 24], [6, 16], [8, 18], [50, 60], [52], [54], [56], [58]];
    static readonly string[] Crew = ["Оля", "Петро", "Іра", "Марко"];

    static object Fleet(int[][] ships) => new { ships = ships.Select(s => new { cells = s }).ToArray() };
    static int[] Ints(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetInt32())];
    static void Secs(RoomHarness h, int seconds) => h.Tick(seconds * (1000 / Battleship.TickMillis));

    static RoomHarness Company(int n, object? options = null, int seed = 42)
    {
        var h = new RoomHarness("battleship", options: options, seed: seed);
        foreach (var nick in Crew.Take(n)) h.Join(nick);
        Assert.True(h.Start().Ok);
        for (var s = 0; s < n; s++) h.Act(s, "place", Fleet(s % 2 == 0 ? Blue : Red));
        for (var s = 0; s < n; s++) h.Act(s, "ready");
        return h;
    }

    // ---------- №61 годинник ходу ----------

    [Theory]
    [InlineData("20", 20)]
    [InlineData("60", 60)]
    public void The_turn_clock_is_a_table_option(string clock, int seconds)
    {
        var h = Company(2, new { clock });
        Assert.Equal(seconds, h.View(0).GetProperty("turnSeconds").GetInt32());
        Secs(h, seconds - 1);
        Assert.Equal(0, h.View(0).GetProperty("shots").GetInt32());
        Secs(h, 2);
        Assert.Equal(1, h.View(0).GetProperty("shots").GetInt32());   // гармата вистрілила сама
    }

    [Fact]
    public void The_default_clock_is_still_forty_seconds()
    {
        var h = Company(2);
        Assert.Equal(40, h.View(0).GetProperty("turnSeconds").GetInt32());
    }

    // ---------- №63 тости, №60 слово Глека ----------

    [Fact]
    public void Only_a_sinking_speaks_up_and_the_feed_gets_a_quip()
    {
        var h = Company(2);
        h.Act(0, "shoot", new { cell = 52 });        // однопалубний червоного — на дно
        h.Tick();
        var feed = h.View(1).GetProperty("feed");
        var last = feed[feed.GetArrayLength() - 1];
        Assert.Equal("sunk", last.GetProperty("res").GetString());
        Assert.Contains("Однопалубний", last.GetProperty("quip").GetString());
    }

    [Fact]
    public void A_sinking_still_has_a_toast()
    {
        var h = Company(2);
        Assert.Equal("Є! Корабель на дні — стріляй ще", h.Act(0, "shoot", new { cell = 54 }).Message);
        Assert.Equal("", h.Act(0, "shoot", new { cell = 99 }).Message);
    }

    // ---------- №62 реакції ----------

    [Fact]
    public void Reactions_stick_to_the_last_shot_and_are_rate_limited()
    {
        var h = Company(2);
        Assert.False(h.Act(1, "react", new { e = 0 }).Ok);                  // ще нема на що
        h.Act(0, "shoot", new { cell = 0 });
        Assert.True(h.Act(1, "react", new { e = 0 }).Ok);
        Assert.Equal("Не так часто — хай усі розгледять", h.Act(1, "react", new { e = 1 }).Message);
        Assert.False(h.Act(0, "react", new { e = 7 }).Ok);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(h.Act(1, "react", new { e = 1 }).Ok);
        h.Tick();
        var v = h.View(null);
        var feed = v.GetProperty("feed");
        Assert.Equal([-1, 1, -1, -1], Ints(feed[feed.GetArrayLength() - 1].GetProperty("rx")));
        Assert.Equal(2, Ints(v.GetProperty("reactN"))[1]);
    }

    // ---------- №57 остання помста ----------

    static void SinkAll(RoomHarness h, int by, int at)
    {
        foreach (var cell in (at % 2 == 0 ? Blue : Red).SelectMany(s => s))
            Assert.True(h.Act(by, "shoot", new { cell, at }).Ok);
    }

    [Fact]
    public void A_sunk_captain_gets_one_revenge_shot_at_the_sinker()
    {
        var h = Company(3);
        SinkAll(h, 0, 1);
        var v = h.View(1);
        Assert.Equal(1, v.GetProperty("revenge").GetProperty("by").GetInt32());
        Assert.Equal(0, v.GetProperty("revenge").GetProperty("on").GetInt32());
        Assert.Equal(1, v.GetProperty("turn").GetInt32());
        Assert.False(h.Act(0, "shoot", new { cell = 99, at = 2 }).Ok);                  // кривдник чекає
        Assert.Equal("Помста — лише по Олі", h.Act(1, "shoot", new { cell = 0, at = 2 }).Message);
        Assert.True(h.Act(1, "shoot", new { cell = 0, at = 0 }).Ok);                    // влучив, але хід не лишається
        v = h.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("revenge").ValueKind);
        Assert.Equal(0, v.GetProperty("turn").GetInt32());                               // хід знову в кривдника
        Assert.Equal("Твій флот на дні — лишається дивитись", h.Act(1, "shoot", new { cell = 1, at = 0 }).Message);
        var feed = v.GetProperty("feed");
        Assert.True(feed[feed.GetArrayLength() - 1].GetProperty("revenge").GetBoolean());
    }

    [Fact]
    public void A_missed_revenge_gives_the_turn_back()
    {
        var h = Company(3);
        h.Act(0, "shoot", new { cell = 99, at = 1 });          // синій мимо → хід червоного
        h.Act(1, "shoot", new { cell = 99, at = 2 });          // червоний мимо → хід зеленого
        foreach (var cell in Blue.SelectMany(s => s).Where(c => c != 49))
            Assert.True(h.Act(2, "shoot", new { cell, at = 0 }).Ok);
        // зелений добиває синього — синій мститься по зеленому мимо, і хід вертається зеленому
        Assert.True(h.Act(2, "shoot", new { cell = 49, at = 0 }).Ok);
        Assert.Equal(0, h.View(0).GetProperty("revenge").GetProperty("by").GetInt32());
        Assert.True(h.Act(0, "shoot", new { cell = 98, at = 2 }).Ok);
        Assert.Equal(2, h.View(0).GetProperty("turn").GetInt32());
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void A_revenge_that_sinks_the_sinker_hands_the_game_to_the_third()
    {
        var h = Company(3);
        // синій (0) б'є по зеленому (2, Blue) і лишає його з однопалубним 49; червоний (1) топить синього майже весь
        h.Act(0, "shoot", new { cell = 99, at = 2 });          // мимо → хід червоного
        foreach (var cell in Blue.SelectMany(s => s).Where(c => c != 49))
            Assert.True(h.Act(1, "shoot", new { cell, at = 0 }).Ok);
        h.Act(1, "shoot", new { cell = 97, at = 2 });          // мимо → хід зеленого
        foreach (var cell in Red.SelectMany(s => s).Where(c => c != 58))
            Assert.True(h.Act(2, "shoot", new { cell, at = 1 }).Ok);
        h.Act(2, "shoot", new { cell = 98, at = 0 });          // мимо → хід синього
        // синій добиває червоного однопалубним 58 — червоний мститься по синьому і влучає в його останній 49
        Assert.True(h.Act(0, "shoot", new { cell = 58, at = 1 }).Ok);
        Assert.Equal(1, h.View(0).GetProperty("revenge").GetProperty("by").GetInt32());
        Assert.True(h.Act(1, "shoot", new { cell = 49, at = 0 }).Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([2], h.Room.Result!.Winners);
    }

    [Fact]
    public void A_stalled_revenge_fires_by_itself_at_the_sinker()
    {
        var h = Company(3);
        SinkAll(h, 0, 1);
        Secs(h, 41);
        var v = h.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("revenge").ValueKind);
        var feed = v.GetProperty("feed");
        var last = feed[feed.GetArrayLength() - 1];
        Assert.True(last.GetProperty("revenge").GetBoolean());
        Assert.True(last.GetProperty("auto").GetBoolean());
        Assert.Equal(0, last.GetProperty("at").GetInt32());
    }

    [Fact]
    public void A_duel_has_no_revenge()
    {
        var h = Company(2);
        SinkAll(h, 0, 1);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    // ---------- «Глек підсідає» ----------

    [Fact]
    public void Alone_without_a_bot_the_table_does_not_start()
    {
        var h = new RoomHarness("battleship");
        h.Join("Оля");
        Assert.False(h.Start().Ok);
        Assert.Contains("Глек підсідає", h.Reply.Message);
    }

    [Fact]
    public void A_bot_takes_the_empty_seat_and_is_ready_at_once()
    {
        var h = new RoomHarness("battleship", options: new { bots = "1" });
        h.Join("Оля");
        Assert.True(h.Start().Ok);
        var v = h.View(0);
        Assert.Equal([0, 1], Ints(v.GetProperty("players")));
        var bot = v.GetProperty("boards")[1];
        Assert.Equal("Глек 🤖", bot.GetProperty("bot").GetString());
        Assert.True(bot.GetProperty("ready").GetBoolean());
        // бот не бачить і не видає свого флоту
        Assert.DoesNotContain("\"ships\"", h.View(null).GetRawText().Replace("\"me\":null", ""));
    }

    [Fact]
    public void A_bot_plays_a_whole_game_and_nobody_is_paid_for_it()
    {
        var h = new RoomHarness("battleship", options: new { bots = "1", fleet = "quick" }, seed: 7);
        h.Join("Оля");
        h.Start();
        h.Act(0, "random");
        h.Act(0, "ready");
        // людина весь час простоює — її гармата стріляє сама кожні 40 с, а бот — щосекунди після своїх влучань
        for (var i = 0; i < 4000 && h.Room.Status == RoomStatus.Playing; i++) h.Tick(4);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = Assert.Single(h.Finished);
        Assert.Single(fin.Seats.Where(s => s is not null));    // місце бота порожнє: ні черепків, ні таблиці
    }

    [Fact]
    public void The_bot_finishes_a_wounded_ship_along_its_line()
    {
        var sea = BattleshipRules.Classic;
        var rng = new Random(1);
        var hits = new HashSet<int> { 44, 45 };
        for (var i = 0; i < 50; i++)
        {
            var c = BattleshipBot.Aim(rng, sea, hits, new HashSet<int>(), []);
            Assert.Contains(c, new[] { 43, 46 });
        }
        // без підбитих — шаховий візерунок
        for (var i = 0; i < 50; i++)
        {
            var c = BattleshipBot.Aim(rng, sea, new HashSet<int>(), new HashSet<int>(), []);
            Assert.Equal(0, (c % 10 + c / 10) % 2);
        }
    }

    [Fact]
    public void When_the_last_human_leaves_the_bots_do_not_play_on()
    {
        var h = new RoomHarness("battleship", options: new { bots = "2" });
        h.Join("Оля");
        h.Start();
        h.Act(0, "random");
        h.Act(0, "ready");
        h.Leave("Оля");
        Assert.Single(h.Finished);   // партію закрито одразу (а порожня кімната прибрана)
    }

    [Fact]
    public void Save_and_load_keep_bots_clock_and_revenge()
    {
        var h = Company(3, new { clock = "20" });
        SinkAll(h, 0, 1);
        var game = (Battleship)h.Room.Game;
        var json = game.Save()!;
        var copy = new Battleship();
        copy.Load(json);
        Assert.Equal(json, copy.Save());
    }
}
