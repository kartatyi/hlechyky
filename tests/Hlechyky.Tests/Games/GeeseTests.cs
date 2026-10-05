using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

[Collection(SerialPerf.Name)]
public class GeeseTests
{
    // ---------- помічники ----------

    static Geese G(RoomHarness h) => (Geese)h.Room.Game;
    static string Ph(RoomHarness h) => h.View(null).GetProperty("ph").GetString()!;

    /// <summary>Тикає, доки фаза не стане <paramref name="ph"/> (або партія не скінчиться).</summary>
    static void Until(RoomHarness h, string ph, int limit = 5000)
    {
        for (var i = 0; i < limit && Ph(h) != ph; i++) h.Tick();
        Assert.Equal(ph, Ph(h));
    }

    static RoomHarness Two(int seed = 42)
    {
        var h = new RoomHarness("geese", seed: seed);
        h.Join("Оля");
        h.Join("Петро");
        h.Start();
        return h;
    }

    static object Pay(GeeseRound r, int value) => r.Q.Choice ? new { c = value } : new { n = value };

    // ---------- паспорт ----------

    [Fact]
    public void Info_and_pool()
    {
        var g = new Geese();
        Assert.Equal(GameGroup.Party, g.Info.Group);
        Assert.Equal((1, 8), (g.Info.MinPlayers, g.Info.MaxPlayers));
        Assert.DoesNotContain(g.Info.Options ?? [], o => o.Key is "party" or "bots");
        var d = new GeeseDaily();
        Assert.Equal(GameGroup.Solo, d.Info.Group);
        Assert.IsAssignableFrom<IDailyGame>(d);
        Assert.Equal("geese", d.Info.Client);
        Assert.True(PartyPool.Has("geese"));
        Assert.False(PartyPool.Has("geese-daily"));
        foreach (var k in new[] { "geese-shepherd", "geese-eagle", "geese-days5" }) Assert.NotNull(AchievementCatalog.Get(k));
        Assert.False(string.IsNullOrWhiteSpace(((IPartyMinigame)g).Howto));
        Assert.InRange(((IPartyMinigame)g).PartyCapMs, 60_000, 120_000);
    }

    // ---------- парад ----------

    /// <summary>
    /// Парад — це потік, а не натовп: тварини виходять і в другій половині параду, а одночасно на подвір'ї — не всі.
    /// Перевіряємо й довжини вечірки (8–9 с) — там дорога через двір з'їдала половину параду.
    /// </summary>
    [Fact]
    public void Parade_flows_through_the_whole_parade_not_a_frozen_crowd()
    {
        double worstLast = 1, worstCrowd = 0;
        for (var seed = 0; seed < 200; seed++)
        for (var level = 0; level <= 2; level++)
        foreach (int? ms in new int?[] { null, 8000, 9000 })
        {
            var rng = new Random(seed * 7 + level);
            var r = GeeseParade.Make(rng, level, true, GeeseParade.PickTraits(rng), null, ms);
            var last = r.Animals.Max(a => a.T0) / (double)r.Ms;
            worstLast = Math.Min(worstLast, last);
            for (var t = 0; t < r.Ms; t += 100)
            {
                var on = r.Animals.Count(a => t >= a.T0 && (t - a.T0) / 1000.0 * a.V <= 1 + 2 * GeeseParade.Margin);
                worstCrowd = Math.Max(worstCrowd, on / (double)r.Animals.Length);
            }
        }
        Assert.True(worstLast >= 0.45, $"остання тварина виходить на {worstLast:P0} параду");
        Assert.True(worstCrowd <= 0.65, $"одночасно на подвір'ї до {worstCrowd:P0} тварин");
    }

    [Fact]
    public void Parade_is_sane_for_many_seeds_and_levels()
    {
        for (var seed = 0; seed < 300; seed++)
        for (var level = 0; level <= GeeseParade.MaxLevel; level++)
        {
            var rng = new Random(seed * 13 + level);
            var traits = GeeseParade.PickTraits(rng);
            Assert.InRange(traits.Length, 2, 3);
            var r = GeeseParade.Make(rng, level, level < 3, traits);
            Assert.InRange(r.Animals.Length, GeeseParade.MinAnimals, GeeseParade.MaxAnimals);
            Assert.Equal(GeeseParade.Lanes(level), r.Lanes);
            Assert.All(r.Animals, a =>
            {
                Assert.InRange(a.L, 0, r.Lanes - 1);
                Assert.True(a.D is 1 or -1);
                Assert.True(a.T0 >= 0);
                // Кожна тварина встигає перебігти двір до кінця параду.
                Assert.True(a.T0 + (1 + 2 * GeeseParade.Margin) / a.V * 1000 <= r.Ms, $"seed {seed} level {level}");
                if (a.Tr is not null) Assert.Equal(GeeseParade.Trait(a.Tr).Kind, a.K);
            });
            if (level < 2) Assert.All(r.Animals, a => Assert.Equal(1, a.D));
            if (level < 3) Assert.Empty(r.Covers);
            else Assert.NotEmpty(r.Covers);
            // Відповідь = тварини-відповідь.
            Assert.All(r.Hits, i => Assert.InRange(i, 0, r.Animals.Length - 1));
            switch (r.Q.Type)
            {
                case "most":
                    var best = r.Q.Opts![r.Answer];
                    var counts = r.Animals.GroupBy(a => a.K).ToDictionary(g => g.Key, g => g.Count());
                    Assert.Single(counts, kv => kv.Value == counts.Values.Max());
                    Assert.Equal(counts.Values.Max(), counts[best]);
                    Assert.Equal(counts[best], r.Hits.Length);
                    Assert.Equal(r.Q.Opts!.Distinct().Count(), r.Q.Opts!.Length);
                    break;
                case "trait":
                    Assert.True(r.Answer >= 1);
                    Assert.Equal(r.Animals.Count(a => a.Tr == r.Q.Trait), r.Answer);
                    Assert.Contains(r.Q.Trait, traits);
                    break;
                case "dir":
                    Assert.True(r.Answer >= 1);
                    Assert.Equal(r.Animals.Count(a => a.D < 0), r.Answer);
                    break;
                case "sum":
                    Assert.Equal(2, r.Q.Kinds.Length);
                    Assert.Equal(r.Animals.Count(a => r.Q.Kinds.Contains(a.K)), r.Answer);
                    break;
                default:
                    Assert.Equal("count", r.Q.Type);
                    Assert.Equal(r.Animals.Count(a => a.K == r.Q.Kinds[0]), r.Answer);
                    Assert.True(r.Answer >= 1);
                    break;
            }
            if (!r.Q.Choice) Assert.Equal(r.Answer, r.Hits.Length);
        }
    }

    [Fact]
    public void Parade_is_deterministic_and_question_types_vary()
    {
        string Make(int seed) => JsonSerializer.Serialize(GeeseParade.Make(new Random(seed), 4, false, ["hustka", "hlechyk"]));
        Assert.Equal(Make(5), Make(5));
        Assert.NotEqual(Make(5), Make(6));
        var types = new HashSet<string>();
        for (var s = 0; s < 200; s++) types.Add(GeeseParade.Make(new Random(s), 5, false, ["hustka", "chorna"]).Q.Type);
        Assert.Equal(["count", "dir", "most", "sum", "trait"], types.Order());
        // Двічі поспіль той самий тип — не даємо.
        for (var s = 0; s < 100; s++)
            Assert.NotEqual("most", GeeseParade.Make(new Random(s), 3, false, ["bant"], lastType: "most").Q.Type);
    }

    // ---------- партія за столом ----------

    [Fact]
    public void Phases_go_in_order_and_question_hides_until_answer_after_round_three()
    {
        var h = Two();
        Assert.Equal(GeeseBase.PhReady, Ph(h));
        var v = h.View(0);
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Equal(7, v.GetProperty("rounds").GetInt32());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("parade").ValueKind);
        Assert.Equal(JsonValueKind.Object, v.GetProperty("q").ValueKind);      // 1-й раунд: питання до параду
        for (var round = 1; round <= 7; round++)
        {
            Until(h, GeeseBase.PhReady);
            v = h.View(0);
            Assert.Equal(round, v.GetProperty("round").GetInt32());
            var pre = round <= 3;
            Assert.Equal(pre, v.GetProperty("pre").GetBoolean());
            Assert.Equal(pre ? JsonValueKind.Object : JsonValueKind.Null, v.GetProperty("q").ValueKind);
            Assert.Equal(pre ? JsonValueKind.Null : JsonValueKind.String, v.GetProperty("note").ValueKind);
            Until(h, GeeseBase.PhParade);
            v = h.View(0);
            Assert.Equal(JsonValueKind.Object, v.GetProperty("parade").ValueKind);
            Assert.Equal(pre ? JsonValueKind.Object : JsonValueKind.Null, v.GetProperty("q").ValueKind);
            Assert.Equal(JsonValueKind.Null, v.GetProperty("reveal").ValueKind);
            Until(h, GeeseBase.PhAnswer);
            v = h.View(null);
            Assert.Equal(JsonValueKind.Object, v.GetProperty("q").ValueKind);
            Assert.Equal(JsonValueKind.Null, v.GetProperty("reveal").ValueKind);   // відповіді ще нема у виді
            Until(h, GeeseBase.PhReveal);
            Assert.Equal(JsonValueKind.Object, h.View(null).GetProperty("reveal").ValueKind);
        }
        Until(h, GeeseBase.PhDone);
        Assert.Single(h.Finished);
        Assert.Empty(h.Finished[0].Result.Winners);    // ніхто не відповідав — нічия
    }

    [Fact]
    public void Illegal_acts_fail_and_do_not_change_view()
    {
        var h = Two();
        var g = G(h);
        string Snap() => h.View(0).GetRawText() + h.View(1).GetRawText();
        var before = Snap();
        Assert.False(h.Act(0, "answer", new { n = 3 }).Ok);          // ще «готуйсь»
        Assert.False(h.Act(0, "dance").Ok);
        Assert.False(h.Act(0, LiveBots.Toggle).Ok);                   // партія йде
        Assert.Equal(before, Snap());
        Until(h, GeeseBase.PhParade);
        Assert.False(h.Act(0, "answer", new { n = 3 }).Ok);          // парад ще біжить
        Until(h, GeeseBase.PhAnswer);
        var r = g.Rounds[g.RoundIndex];
        before = Snap();
        Assert.False(h.Act(0, "answer", new { x = 1 }).Ok);
        Assert.False(h.Act(0, "answer", r.Q.Choice ? new { c = 9 } : new { n = 99 }).Ok);
        Assert.False(h.Act(0, "answer", r.Q.Choice ? new { c = -1 } : new { n = -1 }).Ok);
        Assert.False(h.Act(0, "answer", r.Q.Choice ? (object)new { n = 1 } : new { c = 1 }).Ok);
        Assert.Equal(before, Snap());
        Assert.True(h.Act(0, "answer", Pay(r, r.Answer)).Ok);
        Assert.False(h.Act(0, "answer", Pay(r, 0)).Ok);               // вдруге — ні
        h.Tick();
        var v0 = h.View(0);
        Assert.Equal(r.Answer, v0.GetProperty("mine").GetInt32());
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("mine").ValueKind);   // чужого числа не видно
        Assert.Equal([0], h.View(1).GetProperty("answered").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void Scoring_exact_near_and_fastest_bonus()
    {
        var h = Two(seed: 3);
        var g = G(h);
        // Шукаємо раунд із числом (не вибором).
        for (var guard = 0; guard < 7; guard++)
        {
            Until(h, GeeseBase.PhAnswer);
            if (!g.Rounds[g.RoundIndex].Q.Choice) break;
            Until(h, GeeseBase.PhReveal);
        }
        var r = g.Rounds[g.RoundIndex];
        Assert.False(r.Q.Choice);
        var s0 = g.ScoreOf(0);
        var s1 = g.ScoreOf(1);
        Assert.True(h.Act(1, "answer", new { n = r.Answer + 1 }).Ok);
        h.Tick(3);
        Assert.True(h.Act(0, "answer", new { n = r.Answer }).Ok);
        h.Tick();       // усі відповіли — показ одразу
        Assert.Equal(GeeseBase.PhReveal, Ph(h));
        Assert.Equal(s0 + GeeseBase.Exact + GeeseBase.FastBonus, g.ScoreOf(0));
        Assert.Equal(s1 + GeeseBase.Near, g.ScoreOf(1));
        var rows = h.View(null).GetProperty("reveal").GetProperty("rows").EnumerateArray().ToList();
        Assert.True(rows[0].GetProperty("fast").GetBoolean());
        Assert.Equal(r.Answer, h.View(null).GetProperty("reveal").GetProperty("answer").GetInt32());
        Assert.Equal(r.Hits.Length, h.View(null).GetProperty("reveal").GetProperty("hits").GetArrayLength());
    }

    [Fact]
    public void Both_exact_earlier_gets_bonus_and_perfect_game_gives_achievements()
    {
        var h = Two(seed: 9);
        var g = G(h);
        for (var i = 0; i < 7; i++)
        {
            Until(h, GeeseBase.PhAnswer);
            var r = g.Rounds[g.RoundIndex];
            Assert.True(h.Act(1, "answer", Pay(r, r.Answer)).Ok);
            h.Tick();
            Assert.True(h.Act(0, "answer", Pay(r, r.Answer)).Ok);
            Until(h, GeeseBase.PhReveal);
        }
        Until(h, GeeseBase.PhDone);
        Assert.Equal(7 * GeeseBase.Exact + 7 * GeeseBase.FastBonus, g.ScoreOf(1));
        Assert.Equal(7 * GeeseBase.Exact, g.ScoreOf(0));
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Contains(h.Awards, a => a.Reason == "ach:geese-shepherd" && a.Nick == "Оля");
        Assert.Contains(h.Awards, a => a.Reason == "ach:geese-shepherd" && a.Nick == "Петро");
        if (!g.Rounds[^1].Q.Choice) Assert.Contains(h.Awards, a => a.Reason == "ach:geese-eagle");
        var recap = h.View(0).GetProperty("recap");
        Assert.Equal(7, recap.GetArrayLength());
    }

    [Fact]
    public void Deterministic_by_seed_and_rematch_gives_new_parade()
    {
        string Rounds(RoomHarness h) => JsonSerializer.Serialize(G(h).Rounds);
        Assert.Equal(Rounds(Two(7)), Rounds(Two(7)));
        var h = Two(7);
        var first = Rounds(h);
        Until(h, GeeseBase.PhDone);
        h.Rematch();
        Assert.Equal(GeeseBase.PhReady, Ph(h));
        Assert.NotEqual(first, Rounds(h));
        Assert.All(Enumerable.Range(0, 2), s => Assert.Equal(0, G(h).ScoreOf(s)));
    }

    [Fact]
    public void Alone_can_play_without_bot_and_leaving_ends()
    {
        var h = new RoomHarness("geese");
        h.Join("Оля");
        h.Start();
        Assert.Equal(GeeseBase.PhReady, Ph(h));
        Until(h, GeeseBase.PhAnswer);
        var g = G(h);
        var r = g.Rounds[0];
        Assert.True(h.Act(0, "answer", Pay(r, r.Answer)).Ok);
        h.Tick();
        Assert.Equal(GeeseBase.Exact, g.ScoreOf(0));     // самому — без бонусу швидкості
        h.Leave("Оля");
        Assert.Single(h.Finished);
    }

    [Fact]
    public void One_leaves_the_other_plays_on()
    {
        var h = Two();
        Until(h, GeeseBase.PhAnswer);
        h.Leave("Петро");
        Assert.Empty(h.Finished);
        var g = G(h);
        var r = g.Rounds[g.RoundIndex];
        Assert.True(h.Act(0, "answer", Pay(r, r.Answer)).Ok);
        h.Tick();
        Assert.Equal(GeeseBase.PhReveal, Ph(h));        // на того, хто пішов, не чекаємо
        Until(h, GeeseBase.PhDone);
        Assert.Single(h.Finished);
    }

    // ---------- соло з ботом ----------

    static long BotGame(LiveBots.Level level, int seed)
    {
        var h = new RoomHarness("geese", options: new { botlvl = LiveBots.Key(level) }, seed: seed);
        h.Join("Оля");
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        h.Start();
        var g = G(h);
        Assert.Equal([1, 2], g.Bots);
        Until(h, GeeseBase.PhDone);
        Assert.Empty(h.Awards);           // з ботами — ні ачівок
        return g.ScoreOf(1) + g.ScoreOf(2);
    }

    [Fact]
    public void Bots_play_and_hard_beats_easy()
    {
        long easy = 0, hard = 0;
        for (var s = 1; s <= 12; s++)
        {
            easy += BotGame(LiveBots.Level.Easy, s);
            hard += BotGame(LiveBots.Level.Hard, s);
        }
        Assert.True(easy > 0);
        Assert.True(hard > easy * 1.5, $"легкий {easy}, сильний {hard}");
        // Сильний не ідеальний: за 12 партій по двоє ботів хоч раз помиляється.
        Assert.True(hard < 12 * 2 * 7 * (GeeseBase.Exact + GeeseBase.FastBonus));
    }

    [Fact]
    public void Bot_plan_stays_inside_answer_window()
    {
        var rng = new Random(1);
        for (var i = 0; i < 300; i++)
        {
            var r = GeeseParade.Make(rng, i % 7, false, ["hustka", "bant"]);
            foreach (var lvl in new[] { LiveBots.Level.Easy, LiveBots.Level.Normal, LiveBots.Level.Hard })
            {
                var (at, value) = GeeseBot.Plan(rng, lvl, r, 8000);
                Assert.InRange(at, 0, 8000 - 300);
                Assert.InRange(value, 0, r.Q.Choice ? r.Q.Opts!.Length - 1 : GeeseParade.MaxAnswer);
            }
        }
    }

    // ---------- режим вечірки ----------

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(8)]
    public void Party_bots_only_finish_before_cap_with_scores_for_all(int bots)
    {
        var h = new PartyHarness("geese", humans: 0, bots: bots, seed: 4);
        h.Start();
        var g = (Geese)h.Game;
        Assert.Equal(3, g.Rounds.Count);
        Assert.True(g.Rounds[0].Pre);
        Assert.False(g.Rounds[1].Pre);
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(bots, r.Scores.Count);
        Assert.Equal(bots, r.Places.Length);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromSeconds(80));
        Assert.Equal(0, h.Ctx.Muted);
        Assert.Equal(0, h.Parent.Finishes);
        Assert.Empty(h.Parent.Says);       // у вечірці Глек мовчить — міні-ігор багато
    }

    [Fact]
    public void Party_idle_humans_do_not_stall()
    {
        var h = new PartyHarness("geese", humans: 3, bots: 0, seed: 2);
        h.Start();
        var r = h.RunToEnd();
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(3, r.Scores.Count);
        Assert.All(r.Scores.Values, v => Assert.Equal(0, v));
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(((IPartyMinigame)h.Game).PartyCapMs));
    }

    [Fact]
    public void Party_good_human_is_on_top_and_midway_scores_cover_all()
    {
        var h = new PartyHarness("geese", humans: 1, bots: 3, level: LiveBots.Level.Hard, seed: 8);
        h.Start();
        var g = (Geese)h.Game;
        var answered = -1;
        var midway = false;
        var r = h.RunToEnd(x =>
        {
            if (g.Phase == GeeseBase.PhAnswer && answered != g.RoundIndex)
            {
                answered = g.RoundIndex;
                var round = g.Rounds[g.RoundIndex];
                Assert.True(x.Act(0, "answer", Pay(round, round.Answer)).Ok);
            }
            if (g.RoundIndex == 1 && !midway)
            {
                midway = true;
                Assert.Equal(4, g.PartyScores().Count);
            }
        });
        Assert.True(midway);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(1, r.Places[0]);
        Assert.Equal(r.Scores.Values.Max(), r.Scores[0]);
        Assert.Equal(12, r.Scores[0]);     // точно в кожному з трьох і щоразу першим (відповів на першому тику)
    }

    [Fact]
    public void Party_is_deterministic_by_seed()
    {
        string Run()
        {
            var h = new PartyHarness("geese", humans: 0, bots: 6, seed: 11);
            h.Start();
            var r = h.RunToEnd()!;
            return string.Join(",", r.Scores.OrderBy(kv => kv.Key).Select(kv => kv.Value)) + "|" + h.Clock.UtcNow.ToUnixTimeMilliseconds();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Party_mode_has_no_bot_button()
    {
        var h = new PartyHarness("geese", humans: 1, bots: 1, seed: 1);
        h.Start();
        Assert.False(h.Act(0, LiveBots.Toggle).Ok);
        var v = JsonSerializer.SerializeToElement(h.Game.View(0));
        Assert.False(v.GetProperty("botOffer").GetBoolean());
        Assert.True(v.GetProperty("party").GetBoolean());
    }

    // ---------- «Гуси дня» ----------

    [Fact]
    public void Daily_same_parade_for_all_records_once_and_rematch_gives_nothing()
    {
        var board = new GeeseDailyBoard(null);
        var services = RoomHarness.WithService(board);
        string Play(string nick, bool good, out RoomHarness h)
        {
            h = new RoomHarness("geese-daily", services: services);
            h.Solo(nick);
            var g = (GeeseDaily)h.Room.Game;
            var rounds = JsonSerializer.Serialize(g.Rounds);
            for (var i = 0; i < 5; i++)
            {
                Until(h, GeeseBase.PhAnswer);
                var r = g.Rounds[g.RoundIndex];
                Assert.True(h.Act(0, "answer", Pay(r, good ? r.Answer : r.Q.Choice ? (r.Answer + 1) % r.Q.Opts!.Length : r.Answer + 5)).Ok);
                Assert.False(h.Act(0, LiveBots.Toggle).Ok);
                Until(h, GeeseBase.PhReveal);
            }
            Until(h, GeeseBase.PhDone);
            return rounds;
        }
        var a = Play("Оля", true, out var ha);
        var b = Play("Петро", false, out var hb);
        Assert.Equal(a, b);
        Assert.Contains("15 з 15", ha.Finished.Single().Result.Text);
        Assert.Contains("🎯🎯🎯🎯🎯", ha.Finished.Single().Result.Text);
        Assert.Contains(ha.Awards, x => x.Reason == "daily:geese-daily");
        var daily = hb.View(0).GetProperty("daily");
        Assert.Equal(2, daily.GetProperty("board").GetArrayLength());
        Assert.Equal(2, daily.GetProperty("place").GetInt32());
        Assert.StartsWith("Гуси дня №", daily.GetProperty("share").GetString());
        Assert.False(hb.View(0).GetProperty("botOffer").GetBoolean());
        ha.Rematch("Оля");
        Assert.Equal(GeeseBase.PhDone, Ph(ha));
        Assert.Single(board.Top(Days.Today(ha.Clock)), r => r.Nick == "Оля");
        Assert.Equal(15, ((IDailyPoints)board).Points(Days.Today(ha.Clock), "оля"));
    }

    /// <summary>
    /// Раунд «на пам'ять»: побачив питання, вийшов — після відновлення той самий парад удруге не показують,
    /// раунд іде одразу з відповіді (і складу параду у виді на відповіді нема).
    /// </summary>
    [Fact]
    public void Daily_leaving_mid_answer_does_not_replay_the_parade()
    {
        var h = new RoomHarness("geese-daily", services: RoomHarness.WithService(new GeeseDailyBoard(null)));
        h.Solo("Оля");
        var g = (GeeseDaily)h.Room.Game;
        for (var i = 0; i < 3; i++)
        {
            Until(h, GeeseBase.PhAnswer);
            var r = g.Rounds[g.RoundIndex];
            h.Act(0, "answer", Pay(r, r.Answer));
            Until(h, GeeseBase.PhReveal);
        }
        Until(h, GeeseBase.PhAnswer);
        Assert.Equal(3, g.RoundIndex);
        Assert.False(g.Rounds[3].Pre);
        Assert.Equal(0, h.View(0).GetProperty("parade").GetProperty("animals").GetArrayLength());
        var saved = g.Save()!;
        var h2 = new RoomHarness("geese-daily", services: RoomHarness.WithService(new GeeseDailyBoard(null)));
        h2.Solo("Оля");
        var g2 = (GeeseDaily)h2.Room.Game;
        g2.Load(saved);
        Assert.Equal(3, g2.RoundIndex);
        Assert.Equal(GeeseBase.PhAnswer, g2.Phase);
        Assert.Equal(0, h2.View(0).GetProperty("parade").GetProperty("animals").GetArrayLength());
        // Після відповіді — звичайний показ із повним парадом.
        var r4 = g2.Rounds[3];
        h2.Act(0, "answer", Pay(r4, r4.Answer));
        Until(h2, GeeseBase.PhReveal);
        Assert.Equal(r4.Animals.Length, h2.View(0).GetProperty("parade").GetProperty("animals").GetArrayLength());
        Assert.Equal(4 * GeeseBase.Exact, g2.ScoreOf(0));
    }

    [Fact]
    public void Daily_load_save_resumes_mid_day_and_done_day_stays_done()
    {
        var h = new RoomHarness("geese-daily", services: RoomHarness.WithService(new GeeseDailyBoard(null)));
        h.Solo("Оля");
        var g = (GeeseDaily)h.Room.Game;
        for (var i = 0; i < 2; i++)
        {
            Until(h, GeeseBase.PhAnswer);
            var r = g.Rounds[g.RoundIndex];
            h.Act(0, "answer", Pay(r, r.Answer));
            Until(h, GeeseBase.PhReveal);
        }
        var saved = g.Save()!;
        var fresh = new GeeseDaily();
        var h2 = new RoomHarness("geese-daily", services: RoomHarness.WithService(new GeeseDailyBoard(null)));
        h2.Solo("Оля");
        var g2 = (GeeseDaily)h2.Room.Game;
        g2.Load(saved);
        Assert.Equal(2, g2.RoundIndex);
        Assert.Equal(GeeseBase.PhReady, g2.Phase);
        Assert.Equal(2 * GeeseBase.Exact, g2.ScoreOf(0));
        Assert.Equal(JsonSerializer.Serialize(g.Rounds), JsonSerializer.Serialize(g2.Rounds));
        Until(h, GeeseBase.PhDone);
        var done = g.Save()!;
        var h3 = new RoomHarness("geese-daily", services: RoomHarness.WithService(new GeeseDailyBoard(null)));
        h3.Solo("Оля");
        ((GeeseDaily)h3.Room.Game).Load(done);
        Assert.Equal(GeeseBase.PhDone, ((GeeseDaily)h3.Room.Game).Phase);
        Assert.NotNull(fresh.Info);
    }

    [Fact]
    public void Five_days_of_geese_in_a_row_unlock_the_achievement()
    {
        using var rig = new EconomyRig();
        var day = DateOnly.ParseExact(rig.Daily.Today(), "yyyy-MM-dd");
        for (var i = 4; i >= 1; i--)
            rig.Daily.Record("geese-daily", "Оля", solved: true, attempts: 1, ms: 0, day: day.AddDays(-i).ToString("yyyy-MM-dd"));
        rig.Achievements.OnDaily("Оля", rig.Daily.MyResult("Оля", "geese-daily") ?? new DailyRow(day.AddDays(-1).ToString("yyyy-MM-dd"), "geese-daily", "оля", "Оля", true, 1, 0),
            rig.Daily.Streak("Оля", "geese-daily"));
        Assert.False(rig.Achievements.Has("Оля", "geese-days5"));
        rig.Daily.Record("geese-daily", "Оля", solved: true, attempts: 1, ms: 0, day: day.ToString("yyyy-MM-dd"));
        rig.Achievements.OnDaily("Оля", rig.Daily.MyResult("Оля", "geese-daily")!, rig.Daily.Streak("Оля", "geese-daily"));
        Assert.True(rig.Achievements.Has("Оля", "geese-days5"));
    }

    // ---------- швидкість ----------

    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_with_eight_seats_are_fast()
    {
        var h = new RoomHarness("geese", seed: 5);
        for (var i = 0; i < 8; i++) h.Join("Гравець" + i);
        h.Start();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            h.Tick();
            if (i % 50 == 0) h.View(i % 8);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), sw.Elapsed.ToString());
    }
}
