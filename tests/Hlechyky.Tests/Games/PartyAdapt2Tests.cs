using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Старі ігри в режимі вечірки, адаптер AD2 (docs/games/specs/party-minigame.md): Скільки?, Дуель, Понг,
/// Аерохокей, Мотоцикли. Кожна — одна коротка партія з ботами на місцях <c>bots</c> (у дуелях — хоч обидва місця),
/// Finish зі scores для кожного місця до стелі, без нагород. Звичайні партії перевіряють їхні власні тести.
/// </summary>
[Collection(SerialPerf.Name)]
public class PartyAdapt2Tests
{
    public static TheoryData<string, int> OnlyBots => new()
    {
        { "skilky", 2 }, { "skilky", 8 },
        { "duel", 2 },
        { "pong", 2 },
        { "hockey", 2 },
        { "tron", 2 },
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
        Assert.True(r.Scores.Values.Sum() > 0, $"{id}: боти мали б щось набрати");
        Assert.Equal(bots, r.Places.Length);
        Assert.NotEmpty(r.Winners);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs), $"{id}: {h.Clock.UtcNow - h.StartedAt}");
        Assert.Equal(0, h.Ctx.Muted);              // ні ачівок, ні рекордів
        Assert.Equal(0, h.Parent.Leaked);
        Assert.Equal(0, h.Parent.Finishes);        // Finish перехоплено хостом
        Assert.True(h.Parent.Logs.Count <= 1);     // у Журнал — щонайбільше рядок підсумку
    }

    [Theory]
    [InlineData("skilky", 5)]
    [InlineData("duel", 2)]
    [InlineData("pong", 2)]
    [InlineData("hockey", 2)]
    [InlineData("tron", 2)]
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
    [InlineData("skilky", 4)]
    [InlineData("duel", 2)]
    [InlineData("pong", 2)]
    [InlineData("hockey", 2)]
    [InlineData("tron", 2)]
    public void Party_scores_midway_cover_every_seat(string id, int bots)
    {
        var h = new PartyHarness(id, humans: 0, bots: bots, seed: 3);
        h.Start();
        h.Tick(15_000 / PartyHarness.ParentTickMs);
        var sc = ((IPartyMinigame)h.Game).PartyScores();
        Assert.Equal(Enumerable.Range(0, bots), sc.Keys.Order());
    }

    [Theory]
    [InlineData("skilky", 2, 8)]
    [InlineData("duel", 2, 2)]
    [InlineData("pong", 2, 2)]
    [InlineData("hockey", 2, 2)]
    [InlineData("tron", 2, 2)]
    public void Party_bounds_and_howto(string id, int min, int max)
    {
        var g = (IPartyMinigame)PartyPool.Create(id)!;
        Assert.Equal(min, g.PartyMin);
        Assert.Equal(max, g.PartyMax);
        Assert.InRange(g.PartyCapMs, 30_000, 120_000);
        Assert.False(string.IsNullOrWhiteSpace(g.Howto));
        // дуельний кандидат вечірки — стеля не довша за хвилину (SPEC §8.1)
        if (max == 2) Assert.True(g.PartyCapMs <= 60_000);
    }

    [Fact]
    public void Daily_skilky_is_not_a_party_minigame()
    {
        Assert.True(PartyPool.Has("skilky"));
        Assert.False(PartyPool.Has("skilky-daily"));
    }

    // ---------- людина, що грає добре, — вгорі; бездіяльна — внизу ----------

    [Fact]
    public void Skilky_exact_answers_beat_strong_bots()
    {
        var h = new PartyHarness("skilky", humans: 1, bots: 3, level: LiveBots.Level.Hard, seed: 5);
        h.Start();
        var r = h.RunToEnd(x =>
        {
            var v = x.View(0);
            if (v.GetProperty("phase").GetString() != Skilky.PhaseAsk || v.GetProperty("my").ValueKind != System.Text.Json.JsonValueKind.Null) return;
            var text = v.GetProperty("question").GetString();
            var a = SkilkyBank.All.First(q => q.Q == text).A!.Value;
            Assert.True(x.Act(0, "answer", new { value = a }).Ok);
        })!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal(r.Scores.Values.Max(), r.Scores[0]);
        Assert.Equal(1, r.Places[0]);
        Assert.Equal(0, h.Ctx.Muted);
    }

    [Fact]
    public void Skilky_strong_bots_are_closer_than_easy_ones()
    {
        long Sum(LiveBots.Level lvl)
        {
            long total = 0;
            for (var seed = 1; seed <= 6; seed++)
            {
                var h = new PartyHarness("skilky", humans: 0, bots: 4, level: lvl, seed: seed);
                h.Start();
                total += h.RunToEnd()!.Scores.Values.Sum();
            }
            return total;
        }
        Assert.True(Sum(LiveBots.Level.Hard) > Sum(LiveBots.Level.Easy));
    }

    [Fact]
    public void Duel_quick_hand_beats_strong_bot_two_nil()
    {
        var h = new PartyHarness("duel", humans: 1, bots: 1, level: LiveBots.Level.Hard, seed: 9);
        h.Start();
        var r = h.RunToEnd(x =>
        {
            if (x.View(0).GetProperty("phase").GetString() == "fire") x.Act(0, "shoot");
        })!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.True(r.Scores[0] > r.Scores[1]);
        Assert.Equal(2, ((Duel)h.Game).PartyScores()[0] / 10_000);
        Assert.Equal(0, h.Ctx.Muted);
    }

    [Fact]
    public void Duel_two_idle_humans_end_before_cap_all_level()
    {
        var h = new PartyHarness("duel", humans: 2, bots: 0, seed: 4);
        h.Start();
        var r = h.RunToEnd()!;
        Assert.Equal(MinigameEnd.Finished, r.How);    // три раунди «заснули» — кінець, не стеля
        Assert.All(r.Scores.Values, v => Assert.Equal(0, v));
        Assert.Equal([1, 1], r.Places);
    }

    [Fact]
    public void Pong_tracking_human_holds_against_easy_bot_and_idle_one_loses()
    {
        var h = new PartyHarness("pong", humans: 1, bots: 1, level: LiveBots.Level.Easy, seed: 6);
        h.Start();
        var r = h.RunToEnd(x =>
        {
            if (x.Frame() is { } f && f.TryGetProperty("by", out var by)) x.Act(0, "to", new { y = by.GetDouble() });
        })!;
        Assert.True(r.Scores[0] >= r.Scores[1], $"{r.Scores[0]}:{r.Scores[1]}");

        var idle = new PartyHarness("pong", humans: 1, bots: 1, level: LiveBots.Level.Normal, seed: 6);
        idle.Start();
        var ri = idle.RunToEnd()!;
        Assert.Equal(MinigameEnd.Finished, ri.How);
        Assert.True(ri.Scores[1] > ri.Scores[0]);
        Assert.Equal(Pong.PartyTarget, ri.Scores[1]);
    }

    [Fact]
    public void Hockey_idle_human_loses_to_bot()
    {
        var h = new PartyHarness("hockey", humans: 1, bots: 1, level: LiveBots.Level.Normal, seed: 8);
        h.Start();
        var r = h.RunToEnd()!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.True(r.Scores[1] > r.Scores[0], $"{r.Scores[0]}:{r.Scores[1]}");
        Assert.Equal(0, h.Ctx.Muted);
    }

    [Fact]
    public void Tron_idle_human_loses_the_series()
    {
        var h = new PartyHarness("tron", humans: 1, bots: 1, level: LiveBots.Level.Normal, seed: 8);
        h.Start();
        var r = h.RunToEnd()!;
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal(2, r.Scores[1]);
        Assert.True(r.Scores[0] < 2);
        Assert.Equal(1, r.Places[1]);
    }

    // ---------- звичайна гра — без змін ----------

    [Fact]
    public void Ordinary_games_keep_their_length()
    {
        var pong = new RoomHarness("pong");
        pong.Join("Оля"); pong.Join("Петро");
        pong.Start();
        Assert.Equal(PongCore.Target, pong.View(0).GetProperty("target").GetInt32());
        var hockey = new RoomHarness("hockey");
        hockey.Join("Оля"); hockey.Join("Петро");
        hockey.Start();
        Assert.Equal(7, hockey.View(0).GetProperty("target").GetInt32());
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_party_ticks_are_fast()
    {
        foreach (var id in new[] { "skilky", "duel", "pong", "hockey", "tron" })
        {
            var h = new PartyHarness(id, humans: 0, bots: id == "skilky" ? 8 : 2, seed: 2);
            h.Start();
            var sw = Stopwatch.StartNew();
            h.Tick(1000);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"{id}: {sw.ElapsedMilliseconds} мс");
        }
    }
}
