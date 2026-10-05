using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Старі ігри в режимі вечірки (адаптер AD1, docs/games/specs/party-minigame.md): Гарячий горщик, Бомбер, Кривуля,
/// Замри!. Кожна — одна коротка партія з ботами на місцях <c>bots</c>, Finish зі scores для кожного місця до стелі,
/// без нагород. Звичайні партії перевіряють їхні власні тести — тут лише режим вечірки.
/// </summary>
[Collection(SerialPerf.Name)]
public class PartyAdaptTests
{
    public static TheoryData<string, int> OnlyBots => new()
    {
        { "potato", 3 }, { "potato", 8 },
        { "bomber", 2 }, { "bomber", 6 },
        { "curve", 2 }, { "curve", 8 },
        { "freeze", 2 }, { "freeze", 8 },
        { "dino", 2 }, { "dino", 8 },
    };

    [Theory]
    [MemberData(nameof(OnlyBots))]
    public void Only_bots_finish_before_cap_with_scores_for_every_seat(string id, int bots)
    {
        var h = new PartyHarness(id, humans: 0, bots: bots, seed: 7);
        h.Start();
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(bots, r.Scores.Count);
        Assert.All(r.Scores.Values, v => Assert.True(v >= 0));
        Assert.Equal(bots, r.Places.Length);
        Assert.NotEmpty(r.Winners);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs), $"{id}: {h.Clock.UtcNow - h.StartedAt}");
        Assert.Equal(0, h.Ctx.Muted);              // ні ачівок, ні рекордів
        Assert.Equal(0, h.Parent.Leaked);
        Assert.Equal(0, h.Parent.Finishes);        // Finish перехоплено хостом
        Assert.True(h.Parent.Logs.Count <= 1);     // у Журнал — щонайбільше рядок підсумку
    }

    [Theory]
    [InlineData("potato", 4)]
    [InlineData("bomber", 4)]
    [InlineData("curve", 5)]
    [InlineData("freeze", 3)]
    [InlineData("dino", 4)]
    public void Party_is_deterministic_by_seed(string id, int bots)
    {
        string Run()
        {
            var h = new PartyHarness(id, humans: 0, bots: bots, seed: 11);
            h.Start();
            var r = h.RunToEnd()!;
            return string.Join(",", r.Scores.OrderBy(kv => kv.Key).Select(kv => kv.Value)) + "|" + h.Clock.UtcNow.ToUnixTimeMilliseconds();
        }
        Assert.Equal(Run(), Run());
    }

    [Theory]
    [InlineData("potato")]
    [InlineData("bomber")]
    [InlineData("curve")]
    [InlineData("freeze")]
    [InlineData("dino")]
    public void Midway_scores_cover_all_seats_and_howto_and_caps_are_set(string id)
    {
        var h = new PartyHarness(id, humans: 1, bots: 2, seed: 3);
        h.Start();
        h.Tick(500);   // 10 с
        var g = (IPartyMinigame)h.Game;
        var s = g.PartyScores();
        Assert.Equal(3, s.Count);
        Assert.False(string.IsNullOrWhiteSpace(g.Howto));
        Assert.InRange(g.PartyCapMs, 45_000, 90_000);
        Assert.True(PartyPool.Has(id));
    }

    [Theory]
    [InlineData("bomber")]
    [InlineData("curve")]
    [InlineData("freeze")]
    [InlineData("dino")]
    public void Idle_human_is_not_on_top_against_hard_bots(string id)
    {
        var h = new PartyHarness(id, humans: 1, bots: 3, level: LiveBots.Level.Hard, seed: 5);
        h.Start();
        var r = h.RunToEnd()!;
        Assert.Equal(4, r.Scores.Count);
        Assert.DoesNotContain(0, r.Winners);
    }

    [Fact]
    public void Freeze_human_who_moves_only_while_baba_sings_is_on_top()
    {
        var h = new PartyHarness("freeze", humans: 1, bots: 3, level: LiveBots.Level.Easy, seed: 9);
        h.Start();
        var fz = (Freeze)h.Game;
        var r = h.RunToEnd(x =>
        {
            var core = fz.CoreForTests;
            var go = core.Baba == FreezeCore.Sing && core.BabaLeft > 12;
            x.Input(0, "move", new { dir = go ? 0 : -1 });
        });
        Assert.NotNull(r);
        Assert.Contains(0, r!.Winners);
    }

    [Fact]
    public void Dino_hard_bots_run_further_than_easy_ones()
    {
        long Avg(LiveBots.Level lvl)
        {
            long sum = 0;
            for (var seed = 1; seed <= 4; seed++)
            {
                var h = new PartyHarness("dino", humans: 0, bots: 4, level: lvl, seed: seed);
                h.Start();
                sum += h.RunToEnd()!.Scores.Values.Sum();
            }
            return sum;
        }
        var easy = Avg(LiveBots.Level.Easy);
        var hard = Avg(LiveBots.Level.Hard);
        Assert.True(hard > easy, $"сильні {hard} м, легкі {easy} м");
    }

    [Fact]
    public void Curve_field_shrinks_in_party_only()
    {
        var h = new PartyHarness("curve", humans: 2, bots: 0, seed: 4);
        h.Start();
        var c = (CurveGame)h.Game;
        h.TickSub(CurveCore.ReadyTicks + CurveGame.PartyShrinkFrom - 10);
        Assert.Equal(0, c.Field.Inset);
        var room = new RoomHarness("curve", seed: 4);
        room.Join("Оля");
        room.Join("Петро");
        room.Start();
        room.Tick(CurveCore.ReadyTicks + 1200);
        Assert.Equal(0, ((CurveGame)room.Room.Game).Field.Inset);
    }

    [Fact]
    public void Bomber_party_uses_classic_field_with_early_shrink()
    {
        var h = new PartyHarness("bomber", humans: 0, bots: 4, seed: 2);
        h.Start();
        var v = h.View(null);
        Assert.Equal(Bomber.PartyLimit, v.GetProperty("limit").GetInt32());
        Assert.Equal(Bomber.PartyShrinkAt, v.GetProperty("shrinkAt").GetInt32());
    }

    [Fact]
    public void Potato_party_seats_a_pilot_on_every_bot_seat()
    {
        var h = new PartyHarness("potato", humans: 1, bots: 3, seed: 6);
        h.Start();
        var p = (Potato)h.Game;
        Assert.True(p.Party);
        Assert.Equal([1, 2, 3], p.PartyBots.ToArray());
        Assert.Equal(-1, p.Bot);
    }

    [Theory]
    [InlineData("potato")]
    [InlineData("bomber")]
    [InlineData("curve")]
    [InlineData("freeze")]
    [InlineData("dino")]
    public void Ordinary_table_ignores_party_keys(string id)
    {
        var room = new RoomHarness(id, options: new { party = "1", bots = "1,2" });
        room.Join("Оля");
        var g = room.Room.Game;
        var party = (bool)g.GetType().GetProperty("Party")!.GetValue(g)!;
        Assert.False(party);
    }

    [Theory, Trait("Category", "Perf")]
    [InlineData("potato")]
    [InlineData("bomber")]
    [InlineData("curve")]
    [InlineData("freeze")]
    [InlineData("dino")]
    public void Host_1000_parent_ticks_with_bots_under_2s(string id)
    {
        var h = new PartyHarness(id, humans: 0, bots: id == "bomber" ? 6 : 8, seed: 5);
        h.Start();
        var sw = Stopwatch.StartNew();
        h.Tick(1000);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }
}
