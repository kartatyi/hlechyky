using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Прохід №3 (№217): «Шпигун» удвох — Дядько Глек сідає третім гравцем.</summary>
public sealed class SpyGlekTests
{
    static readonly Lazy<SpyLocations> Real = new(() => SpyLocations.Load(Paths.Resolve(SpyLocations.FileName)));

    static RoomHarness Duo(int seed = 1, object? options = null)
    {
        var h = new RoomHarness("spy", options ?? new { time = "4", rounds = "3" }, seed, RoomHarness.WithService(Real.Value));
        Assert.True(h.Join("Оля").Ok);
        Assert.True(h.Join("Петро").Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Spy Game(RoomHarness h) => (Spy)h.Room.Game;
    static JsonElement V(RoomHarness h, int? seat = null) => h.View(seat);
    static string Phase(RoomHarness h) => V(h).GetProperty("phase").GetString()!;
    static int? Asker(RoomHarness h) => V(h).GetProperty("asker") is { ValueKind: JsonValueKind.Number } a ? a.GetInt32() : null;
    static int? AskedBy(RoomHarness h) => V(h).GetProperty("askedBy") is { ValueKind: JsonValueKind.Number } a ? a.GetInt32() : null;
    static bool IsSpy(RoomHarness h, int seat) => V(h, seat).GetProperty("me") is { ValueKind: JsonValueKind.Object } m && m.GetProperty("spy").GetBoolean();
    static int SpyOf(RoomHarness h) => IsSpy(h, 0) ? 0 : IsSpy(h, 1) ? 1 : Game(h).GlekSeat;
    static List<string> Said(RoomHarness h) => [.. h.Outbox.OfType<TableSaid>().Select(x => x.Line.Text)];

    static void Until(RoomHarness h, Func<bool> done, int max = 6000)
    {
        for (var i = 0; i < max && !done(); i++) h.Tick();
        Assert.True(done(), "не дочекались");
    }

    [Fact]
    public void Two_players_start_and_glek_takes_the_third_seat_with_a_card()
    {
        var h = Duo();
        var glek = Game(h).GlekSeat;
        Assert.Equal(2, glek);
        var players = V(h).GetProperty("players").EnumerateArray().ToList();
        Assert.Equal(3, players.Count);
        var g = players.Single(p => p.GetProperty("seat").GetInt32() == glek);
        Assert.Equal(Spy.GlekName, g.GetProperty("nick").GetString());
        Assert.True(g.GetProperty("here").GetBoolean());
        // картку Глекові роздано, як усім: або він шпигун, або в нього локація й роль
        var me = V(h, glek).GetProperty("me");
        Assert.Equal(JsonValueKind.Object, me.ValueKind);
    }

    [Fact]
    public void Three_humans_play_without_glek_and_glek_off_keeps_two_in_the_lobby()
    {
        var h = new RoomHarness("spy", null, 1, RoomHarness.WithService(Real.Value));
        foreach (var n in new[] { "Оля", "Петро", "Ганна" }) Assert.True(h.Join(n).Ok);
        Assert.True(h.Start().Ok);
        Assert.Equal(-1, Game(h).GlekSeat);
        Assert.Equal(3, V(h).GetProperty("players").GetArrayLength());

        var off = new RoomHarness("spy", new { glek = "off" }, 1, RoomHarness.WithService(Real.Value));
        off.Join("Оля");
        off.Join("Петро");
        Assert.False(off.Start().Ok);
    }

    [Fact]
    public void Asked_glek_answers_in_one_line_and_asks_the_other_human()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var h = Duo(seed);
            var glek = Game(h).GlekSeat;
            Until(h, () => Phase(h) == "play");
            // хто питає першим — Глек теж може бути; тоді він сам питає когось із людей
            if (Asker(h) == glek) Until(h, () => Asker(h) != glek);
            // кого щойно питав Глек, той Глека одразу питати не може (правило «не того, хто питав тебе») — хай спершу спитає людину
            if (AskedBy(h) == glek) { var a = Asker(h)!.Value; Assert.True(h.Act(a, "ask", new { seat = 1 - a }).Ok, h.Reply.Message); }
            var asker = Asker(h)!.Value;
            var before = Said(h).Count;
            Assert.True(h.Act(asker, "ask", new { seat = glek }).Ok, h.Reply.Message);
            h.Tick(Spy.GlekThinkMs / 250 - 2);
            Assert.Equal(glek, Asker(h));                               // ще «думає»
            Until(h, () => Asker(h) != glek, 20);
            Assert.Equal(1 - asker, Asker(h));                          // питає іншу людину, а не того, хто щойно питав
            var line = Assert.Single(Said(h).Skip(before));
            Assert.Contains("А тепер питаю — " + (asker == 0 ? "Петро" : "Оля") + ":", line);
        }
    }

    [Fact]
    public void Glek_never_names_the_location_or_a_role_in_his_lines()
    {
        var bot = SpyBot.Load(Paths.Resolve(SpyBot.FileName));
        Assert.True(bot.Ask.Count >= 12);
        Assert.True(bot.Vague.Count >= 12);
        foreach (var loc in Real.Value.Pool([SpyLocations.AnySet]))
        {
            Assert.True(bot.Hints.TryGetValue(loc.Id, out var hints), loc.Id);
            Assert.True(hints!.Length >= 4, loc.Id);
            var words = loc.Roles.Append(loc.Title).SelectMany(r => Regex.Split(r.ToLowerInvariant(), @"[^\p{L}'’-]+"))
                .Where(w => w.Length >= 4).ToHashSet();
            foreach (var line in hints)
            {
                var said = Regex.Split(line.ToLowerInvariant(), @"[^\p{L}'’-]+");
                Assert.DoesNotContain(said, w => words.Contains(w));
            }
        }
        var titles = Real.Value.Pool([SpyLocations.AnySet]).Select(l => l.Title.ToLowerInvariant()).ToList();
        Assert.All(bot.Vague.Concat(bot.Ask), line => Assert.DoesNotContain(titles, t => line.ToLowerInvariant().Contains(t)));
    }

    [Fact]
    public void Glek_as_spy_answers_only_vaguely_and_as_villager_mostly_with_hints_of_the_real_location()
    {
        var bot = SpyBot.Load(Paths.Resolve(SpyBot.FileName));
        var vague = bot.Vague.ToHashSet();
        var sawSpy = false;
        var sawVillager = false;
        for (var seed = 1; seed <= 40 && !(sawSpy && sawVillager); seed++)
        {
            var h = Duo(seed);
            var glek = Game(h).GlekSeat;
            Until(h, () => Phase(h) == "play");
            var spy = SpyOf(h);
            var loc = spy == 0 ? V(h, 1).GetProperty("me").GetProperty("loc").GetString()! : V(h, 0).GetProperty("me").GetProperty("loc").GetString()!;
            var hints = bot.Hints[loc].ToHashSet();
            for (var turn = 0; turn < 4 && Phase(h) == "play"; turn++)
            {
                if (Asker(h) == glek) Until(h, () => Asker(h) != glek);
                if (AskedBy(h) == glek) { var a = Asker(h)!.Value; Assert.True(h.Act(a, "ask", new { seat = 1 - a }).Ok, h.Reply.Message); }
                var before = Said(h).Count;
                Assert.True(h.Act(Asker(h)!.Value, "ask", new { seat = glek }).Ok, h.Reply.Message);
                Until(h, () => Asker(h) != glek, 40);
                var line = Said(h).Skip(before).Single();
                var answer = line[..line.IndexOf(" А тепер питаю", StringComparison.Ordinal)];
                if (spy == glek) { sawSpy = true; Assert.Contains(answer, vague); }
                else { sawVillager = true; Assert.True(hints.Contains(answer) || vague.Contains(answer), answer); }
            }
        }
        Assert.True(sawSpy && sawVillager);
    }

    [Fact]
    public void Glek_votes_on_a_suspicion_and_as_spy_says_yes_to_frame_a_human()
    {
        for (var seed = 1; seed <= 40; seed++)
        {
            var h = Duo(seed);
            var glek = Game(h).GlekSeat;
            Until(h, () => Phase(h) == "play");
            if (SpyOf(h) != glek) continue;
            Assert.True(h.Act(0, "accuse", new { seat = 1 }).Ok, h.Reply.Message);
            Assert.Equal("vote", Phase(h));
            Until(h, () => Phase(h) == "reveal", 40);                    // Глек проголосував «так» — одностайно
            var reveal = V(h).GetProperty("reveal");
            Assert.Equal("wrong", reveal.GetProperty("how").GetString());
            Assert.Equal(Spy.SpyFramedPts, V(h).GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == glek).GetProperty("score").GetInt64());
            return;
        }
        Assert.Fail("за 40 сідів Глек жодного разу не був шпигуном у першому раунді");
    }

    [Fact]
    public void Glek_is_ready_on_reveal_blames_in_final_and_never_wins_or_lands_in_the_scores()
    {
        var h = Duo(3, new { time = "4", rounds = "1" });
        var glek = Game(h).GlekSeat;
        Until(h, () => Phase(h) == "final");
        Assert.True(h.Act(0, "blame", new { seat = 1 }).Ok, h.Reply.Message);
        Assert.True(h.Act(1, "blame", new { seat = 0 }).Ok, h.Reply.Message);
        Until(h, () => Phase(h) == "reveal", 40);                        // Глек показав на когось — усі проголосували
        h.Tick();
        Assert.True(V(h).GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == glek).GetProperty("ready").GetBoolean());
        Until(h, () => Phase(h) == "done");
        var result = Assert.Single(h.Finished).Result;
        Assert.DoesNotContain(glek, result.Winners);
        Assert.False(result.Scores!.ContainsKey(glek));
    }

    [Fact]
    public void One_human_leaving_folds_the_table_glek_does_not_play_alone()
    {
        var h = Duo();
        Until(h, () => Phase(h) == "play");
        h.Leave("Петро");
        Assert.Equal("done", Phase(h));
        Assert.Single(h.Finished);
    }
}
