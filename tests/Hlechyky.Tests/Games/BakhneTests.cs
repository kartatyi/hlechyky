using System.Diagnostics;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>«Куди бахне» (spec bakhne.md): стрілки, такти, бахи, серця, кінці, бот, вечірка.</summary>
[Collection(SerialPerf.Name)]
public class BakhneTests
{
    static RoomHarness Table(int people, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("bakhne", options, seed);
        for (var i = 0; i < people; i++) h.Join($"гравець{i}");
        return h;
    }

    static RoomHarness Started(int people, int seed = 42)
    {
        var h = Table(people, seed);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static Bakhne G(RoomHarness h) => (Bakhne)h.Room.Game;

    /// <summary>Тикати, поки фаза не стане потрібною (не довше max тиків).</summary>
    static void Until(RoomHarness h, Func<Bakhne, bool> cond, int max = 5000)
    {
        for (var i = 0; i < max && !cond(G(h)); i++) h.Tick();
        Assert.True(cond(G(h)), "умова так і не настала");
    }

    /// <summary>
    /// «Гравець-скрипт»: на кожен такт ступає правильно (good) або стоїть. Пам'ятає, на який такт уже ступив, щоб
    /// не слати двічі. Ступає посеред такту.
    /// </summary>
    sealed class Script(Func<Bakhne, int, bool> good)
    {
        readonly Dictionary<int, (int Round, int Beat)> _done = [];

        public void Step(Bakhne g, Action<int, int> act, IEnumerable<int> seats)
        {
            if (g.Phase != Bakhne.PhSteps) return;
            var k = g.Beat;
            foreach (var s in seats)
            {
                if (!g.AliveOf(s) || _done.TryGetValue(s, out var d) && d == (g.RoundNo, k)) continue;
                _done[s] = (g.RoundNo, k);
                if (good(g, s)) act(s, g.Need[k]);
            }
        }
    }

    static void PlayRoom(RoomHarness h, Script sc, int[] seats, int max = 20000)
    {
        for (var i = 0; i < max && G(h).Phase != Bakhne.PhOver; i++)
        {
            sc.Step(G(h), (s, d) => h.Input(s, "step", new { d }), seats);
            h.Tick();
        }
    }

    // ---------- паспорт і лобі ----------

    [Fact]
    public void Info_is_live_1_to_8_with_bot_level_and_in_party_pool()
    {
        var g = new Bakhne();
        Assert.Equal("bakhne", g.Info.Id);
        Assert.Equal(GameGroup.Live, g.Info.Group);
        Assert.Equal(1, g.Info.MinPlayers);
        Assert.Equal(8, g.Info.MaxPlayers);
        Assert.Equal(50, g.Info.TickMs);
        Assert.Contains(g.Info.Options!, o => o.Key == "botlvl");
        Assert.True(PartyPool.Has("bakhne"));
        Assert.True(g.PartyCapMs <= 120_000);
        Assert.Equal(2, g.PartyMin);
        Assert.Equal(8, g.PartyMax);
        Assert.False(string.IsNullOrWhiteSpace(g.Howto));
    }

    [Fact]
    public void Achievements_are_in_catalog()
    {
        foreach (var k in new[] { "bakhne-elephant", "bakhne-sober", "bakhne-clean" })
            Assert.NotNull(Hlechyky.Games.Economy.AchievementCatalog.Get(k));
    }

    [Fact]
    public void Alone_without_bot_does_not_start_with_bot_does()
    {
        var h = Table(1);
        Assert.False(h.Start().Ok);
        Assert.Equal(LiveBots.AloneText, h.Reply.Message);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal([1, 2], h.View(0).GetProperty("bot").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = G(h);
        Assert.Equal([1, 2], g.Bots);
        Assert.True(g.BotGame);
        Assert.StartsWith(LiveBots.Name, g.SeatBot(1));
        Assert.Null(g.SeatBot(0));
        Assert.True(g.PlaysOf(1) && g.PlaysOf(2) && !g.PlaysOf(3));
    }

    [Fact]
    public void Lobby_view_shows_center_yards_of_seated()
    {
        var h = Table(2);
        var v = h.View(null);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        var p = v.GetProperty("frame").GetProperty("p");
        Assert.Equal(8, p.GetArrayLength());
        Assert.Equal(1, p[0][0].GetInt32());
        Assert.Equal(1, p[1][1].GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, p[2].ValueKind);
    }

    // ---------- раунд і послідовність ----------

    [Fact]
    public void Round_one_has_three_steps_inside_yard_and_rounds_grow()
    {
        var h = Started(2);
        Until(h, g => g.Phase == Bakhne.PhShow);
        Assert.Equal(1, G(h).RoundNo);
        Assert.Equal(3, G(h).Len);
        Assert.Equal(Bakhne.TrickNone, G(h).Trick);
        AssertInside(G(h));
        var sc = new Script((_, _) => true);
        for (var i = 0; i < 4000 && G(h).RoundNo < 3; i++) { sc.Step(G(h), (s, d) => h.Input(s, "step", new { d }), [0, 1]); h.Tick(); }
        Assert.Equal(3, G(h).RoundNo);
        Assert.Equal(5, G(h).Len);
        AssertInside(G(h));
    }

    static void AssertInside(Bakhne g)
    {
        int x = 1, y = 1;
        foreach (var d in g.Need)
        {
            x += d == 0 ? 1 : d == 2 ? -1 : 0;
            y += d == 1 ? 1 : d == 3 ? -1 : 0;
            Assert.InRange(x, 0, 2);
            Assert.InRange(y, 0, 2);
        }
    }

    [Fact]
    public void Tricks_flip_shown_arrows_and_never_before_round_four()
    {
        var seenDrunk = false;
        var seenRed = false;
        for (var seed = 1; seed <= 12 && !(seenDrunk && seenRed); seed++)
        {
            var h = Started(2, seed);
            var sc = new Script((_, _) => true);
            var drunkHere = false;
            for (var i = 0; i < 20000 && G(h).Phase != Bakhne.PhOver; i++)
            {
                var g = G(h);
                if (g.Phase == Bakhne.PhShow && g.Len > 0)
                {
                    AssertInside(g);
                    if (g.RoundNo < Bakhne.TrickFrom) Assert.Equal(Bakhne.TrickNone, g.Trick);
                    for (var k = 0; k < g.Len; k++)
                    {
                        if (g.Trick == Bakhne.TrickDrunk) Assert.Equal((g.Need[k] + 2) % 4, g.Shown[k]);
                        else if (g.Trick != Bakhne.TrickRed) Assert.Equal(g.Need[k], g.Shown[k]);
                    }
                    seenDrunk |= g.Trick == Bakhne.TrickDrunk;
                    if (g.Trick == Bakhne.TrickRed)
                    {
                        seenRed = true;
                        var a = h.View(null).GetProperty("frame").GetProperty("a");
                        foreach (var e in a.EnumerateArray().Select((e, k) => (e, k)))
                            Assert.Equal(e.e[1].GetInt32() == 1 ? (g.Need[e.k] + 2) % 4 : g.Need[e.k], e.e[0].GetInt32());
                    }
                }
                drunkHere |= g.Trick == Bakhne.TrickDrunk;
                sc.Step(g, (s, d) => h.Input(s, "step", new { d }), [0, 1]);
                h.Tick();
            }
            // обидва бездоганні: 12 раундів, нічия на серцях, а п'яний раунд без втрат — «Тверезий» обом
            Assert.Equal([0, 1], G(h).Winners);
            Assert.Equal(drunkHere ? 2 : 0, h.Awards.Count(a => a.Reason == "ach:bakhne-sober"));
        }
        Assert.True(seenDrunk && seenRed);
    }

    // ---------- Act ----------

    [Fact]
    public void Illegal_acts_fail_and_do_not_change_view()
    {
        var h = Table(2);
        Assert.False(h.Act(0, "step", new { d = 0 }).Ok);   // ще не почали
        h.Start();
        Until(h, g => g.Phase == Bakhne.PhShow);
        var before = h.View(0).GetRawText();
        Assert.False(h.Act(0, "step", new { d = 0 }).Ok);   // Глек показує
        Assert.False(h.Act(0, "jump", new { d = 0 }).Ok);
        Assert.Equal(before, h.View(0).GetRawText());
        Until(h, g => g.Phase == Bakhne.PhSteps);
        before = h.View(0).GetRawText();
        Assert.False(h.Act(0, "step", new { d = 7 }).Ok);
        Assert.False(h.Act(0, "step", new { x = 1 }).Ok);
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.Equal(before, h.View(0).GetRawText());
        Assert.True(h.Act(0, "step", new { d = G(h).Need[0] }).Ok);
        Assert.False(h.Act(0, "step", new { d = G(h).Need[0] }).Ok);   // один крок на такт
        Assert.True(h.Act(1, "step", G(h).Need[0]).Ok);                 // голе число теж годиться
    }

    [Fact]
    public void Right_step_survives_missed_step_costs_heart_and_snaps_to_safe_tile()
    {
        var h = Started(2);
        Until(h, g => g.Phase == Bakhne.PhSteps);
        var g = G(h);
        h.Input(0, "step", new { d = g.Need[0] });
        // місце 1 стоїть
        Until(h, x => x.Resolved >= 1);
        Assert.Equal(2, g.HeartsOf(0));
        Assert.Equal(1, g.HeartsOf(1));
        Assert.Equal(g.PosOf(0), g.PosOf(1));          // обох — на правильній плитці
        var f = h.View(null).GetProperty("frame");
        Assert.Equal(g.PosOf(0).X, f.GetProperty("safe")[0].GetInt32());
        Assert.Equal(1, f.GetProperty("need").GetArrayLength());
        Assert.Equal(1, f.GetProperty("a").GetArrayLength());   // показано лише бахнуту стрілку
        Assert.Equal(1, h.View(0).GetProperty("hearts")[1].GetInt32());
    }

    [Fact]
    public void Two_misses_knock_out_and_last_alive_wins_with_clean_achievement()
    {
        var h = Started(2);
        var sc = new Script((_, s) => s == 0);
        PlayRoom(h, sc, [0, 1]);
        var g = G(h);
        Assert.Equal(Bakhne.PhOver, g.Phase);
        Assert.Equal([0], g.Winners);
        Assert.False(g.AliveOf(1));
        Assert.Equal(1, g.OutRoundOf(1));
        var fin = Assert.Single(h.Finished);
        Assert.Contains(h.Awards, a => a.Reason == "ach:bakhne-clean" && a.Nick == h.NickOf(0));
        Assert.DoesNotContain(h.Awards, a => a.Nick == h.NickOf(1));
        Assert.Equal([0], h.View(1).GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal("over", h.View(1).GetProperty("phase").GetString());
    }

    [Fact]
    public void All_idle_die_on_same_beat_and_share_the_win()
    {
        var h = Started(3);
        Until(h, g => g.Phase == Bakhne.PhOver);
        Assert.Equal([0, 1, 2], G(h).Winners);
        Assert.Single(h.Finished);
        Assert.Empty(h.Awards);   // серця губили всі
    }

    [Fact]
    public void Fence_eats_the_beat()
    {
        var h = Started(2);
        Until(h, g => g.Phase == Bakhne.PhSteps);
        var g = G(h);
        // з центру паркан на 2 кроки — у будь-який бік дійдемо: ступаємо туди, куди не треба, двічі за два такти.
        var wrong = (g.Need[0] + 2) % 4;
        Assert.True(h.Act(0, "step", new { d = wrong }).Ok);
        Until(h, x => x.Resolved >= 1);
        Assert.Equal(1, g.HeartsOf(0));
    }

    [Fact]
    public void Late_press_within_grace_counts_for_previous_beat()
    {
        var h = Started(2);
        Until(h, g => g.Phase == Bakhne.PhSteps);
        var g = G(h);
        // дочекатись першого тика другого такту: такт 0 ще не бахнув
        Until(h, x => x.Beat == 1);
        Assert.Equal(0, g.Resolved);
        Assert.True(h.Act(0, "step", new { d = g.Need[0] }).Ok);
        Assert.True(h.Act(0, "step", new { d = g.Need[1] }).Ok);   // і вже свій, другий
        Until(h, x => x.Resolved >= 2);
        Assert.Equal(2, g.HeartsOf(0));
    }

    [Fact]
    public void Skipped_beat_then_on_beat_press_costs_one_heart_not_two()
    {
        var h = Started(2, 5);   // сід 5: перші кроки 1, 0, 3 — другий не веде туди ж, куди перший
        Until(h, g => g.Phase == Bakhne.PhSteps);
        var g = G(h);
        Assert.NotEqual(g.Need[0], g.Need[1]);
        Until(h, x => x.Beat == 1);
        Assert.True(h.Act(0, "step", new { d = g.Need[1] }).Ok);   // такт 0 проґавив, ступив «у долю» такту 1
        Until(h, x => x.Resolved >= 2);
        Assert.Equal(1, g.HeartsOf(0));
        Assert.True(g.AliveOf(0));
    }

    [Fact]
    public void Early_press_on_go_lands_on_first_beat_only_at_the_very_end()
    {
        var h = Started(2);
        Until(h, g => g.Phase == Bakhne.PhGo);
        Assert.False(h.Act(0, "step", new { d = G(h).Need[0] }).Ok);
        Until(h, g => g.Phase == Bakhne.PhSteps || g.Phase == Bakhne.PhGo && h.View(null).GetProperty("frame").GetProperty("left").GetInt32() <= Bakhne.Grace);
        Assert.True(h.Act(0, "step", new { d = G(h).Need[0] }).Ok);
        Until(h, x => x.Resolved >= 1);
        Assert.Equal(2, G(h).HeartsOf(0));
    }

    // ---------- вид і кадр ----------

    [Fact]
    public void Views_are_same_for_all_and_frame_hides_sequence_during_steps()
    {
        var h = Started(3);
        Until(h, g => g.Phase == Bakhne.PhShow && h.View(null).GetProperty("frame").GetProperty("a").GetArrayLength() == 1);
        Assert.Equal(h.View(null).GetRawText(), h.View(0).GetRawText());
        Assert.Equal(h.View(1).GetRawText(), h.View(2).GetRawText());
        Until(h, g => g.Phase == Bakhne.PhGo);
        Assert.Contains(h.View(null).GetProperty("frame").GetProperty("ev").EnumerateArray(), e => e[1].GetInt32() == Bakhne.EvGo);
        Until(h, g => g.Phase == Bakhne.PhSteps);
        var f = h.View(null).GetProperty("frame");
        Assert.Equal(0, f.GetProperty("a").GetArrayLength());
        Assert.Equal(0, f.GetProperty("need").GetArrayLength());
        Assert.Equal(3, f.GetProperty("len").GetInt32());
        Assert.Equal(Bakhne.PhSteps, f.GetProperty("ph").GetInt32());
        Assert.True(f.GetProperty("bt").GetInt32() >= 12);
        // у кадрі — лише «такт уже з кроком», а куди — таємниця до баху (свій крок клієнт малює сам)
        var d = G(h).Need[0];
        h.Input(0, "step", new { d });
        var p0 = h.View(null).GetProperty("frame").GetProperty("p")[0];
        Assert.Equal(4, p0[3].GetInt32() & 4);
        Assert.Equal(1, p0[0].GetInt32());
        Assert.Equal(1, p0[1].GetInt32());
    }

    [Fact]
    public void Frame_does_not_leak_where_others_stepped_until_bang()
    {
        var h = Started(2);
        Until(h, g => g.Phase == Bakhne.PhSteps);
        var g = G(h);
        var d = g.Need[0];
        // другий ступає не туди — щоб не можна було й «від протилежного»; перший дивиться на кадр
        var wrong = (d + 1) % 4;
        h.Input(1, "step", new { d = wrong });
        for (var i = 0; i < 200 && g.Resolved == 0; i++)
        {
            var f = h.View(0).GetProperty("frame");
            var p1 = f.GetProperty("p")[1];
            Assert.Equal(Bakhne.Center, p1[0].GetInt32());
            Assert.Equal(Bakhne.Center, p1[1].GetInt32());
            if (g.Beat == 0) Assert.Equal(4, p1[3].GetInt32() & 4);   // «уже ступив» — лише в його такті
            Assert.DoesNotContain(f.GetProperty("ev").EnumerateArray(), e => e[1].GetInt32() is 8 or 9);
            h.Tick();
        }
        Assert.Equal(1, g.Resolved);
        // після баху — зарахована позиція (влучило — переносить на правильну плитку)
        var after = h.View(0).GetProperty("frame");
        Assert.Equal(after.GetProperty("safe")[0].GetInt32(), after.GetProperty("p")[1][0].GetInt32());
        Assert.Equal(after.GetProperty("safe")[1].GetInt32(), after.GetProperty("p")[1][1].GetInt32());
    }

    [Fact]
    public void Red_round_marks_distinct_beats_len_over_three()
    {
        var seen = 0;
        for (var seed = 1; seed <= 30 && seen < 3; seed++)
        {
            var h = Started(2, seed);
            var sc = new Script((_, _) => true);
            var last = -1;
            for (var i = 0; i < 20000 && G(h).Phase != Bakhne.PhOver; i++)
            {
                var g = G(h);
                if (g.Phase == Bakhne.PhEnd && g.Trick == Bakhne.TrickRed && last != g.RoundNo)
                {
                    last = g.RoundNo;
                    seen++;
                    var a = h.View(null).GetProperty("frame").GetProperty("a");
                    var reds = a.EnumerateArray().Count(e => e[1].GetInt32() == 1);
                    Assert.Equal(Math.Max(1, g.Len / 3), reds);
                }
                sc.Step(g, (s, d) => h.Input(s, "step", new { d }), [0, 1]);
                h.Tick();
            }
        }
        Assert.True(seen >= 3, "червоних раундів не трапилось");
    }

    [Fact]
    public void Same_seed_same_game()
    {
        string Run()
        {
            var h = Started(3, 77);
            var sc = new Script((g, s) => (g.T + s) % 5 != 0);
            var log = new List<string>();
            for (var i = 0; i < 3000 && G(h).Phase != Bakhne.PhOver; i++)
            {
                sc.Step(G(h), (s, d) => h.Input(s, "step", new { d }), [0, 1, 2]);
                h.Tick();
                if (i % 50 == 0) log.Add(h.View(null).GetProperty("frame").GetRawText());
            }
            return string.Join("\n", log);
        }
        Assert.Equal(Run(), Run());
    }

    // ---------- кінці ----------

    [Fact]
    public void Twelve_rounds_then_most_hearts_win_and_elephant_awarded()
    {
        var h = Started(2, 5);
        // обидва бездоганні, але місце 1 раз схибить у 2-му раунді
        var sc = new Script((g, s) => s == 0 || !(g.RoundNo == 2 && g.Beat == 0));
        PlayRoom(h, sc, [0, 1], 60000);
        var g = G(h);
        Assert.Equal(Bakhne.PhOver, g.Phase);
        Assert.Equal(Bakhne.RoundsMax, g.RoundNo);
        Assert.Equal([0], g.Winners);
        Assert.Contains(h.Awards, a => a.Reason == "ach:bakhne-elephant" && a.Nick == h.NickOf(1));
        Assert.Contains(h.Awards, a => a.Reason == "ach:bakhne-clean" && a.Nick == h.NickOf(0));
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:bakhne-clean" && a.Nick == h.NickOf(1));
    }

    [Fact]
    public void Leaving_mid_game_lets_others_play_then_ends()
    {
        var h = Started(3);
        Until(h, g => g.Phase == Bakhne.PhSteps);
        h.Leave(h.NickOf(2));
        Assert.Empty(h.Finished);
        Assert.False(G(h).PlaysOf(2));
        h.Leave(h.NickOf(1));
        var fin = Assert.Single(h.Finished);
        Assert.Equal(Bakhne.PhOver, G(h).Phase);
    }

    [Fact]
    public void Rematch_starts_fresh_round()
    {
        var h = Started(2);
        Until(h, g => g.Phase == Bakhne.PhOver);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        var g = G(h);
        Assert.NotEqual(Bakhne.PhOver, g.Phase);
        Assert.Equal(0, g.RoundNo);
        Until(h, x => x.Phase == Bakhne.PhShow);
        Assert.Equal(1, g.RoundNo);
        Assert.Equal(2, g.HeartsOf(0));
    }

    // ---------- соло з ботом ----------

    static RoomHarness Solo(string lvl, int seed)
    {
        var h = new RoomHarness("bakhne", new { botlvl = lvl }, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    [Fact]
    public void Bots_really_step_and_good_human_beats_normal_bots_without_awards()
    {
        var h = Solo("normal", 3);
        var sc = new Script((_, s) => s == 0);
        PlayRoom(h, sc, [0], 80000);
        var g = G(h);
        Assert.Equal(Bakhne.PhOver, g.Phase);
        Assert.Contains(0, g.Winners);
        Assert.True(g.RoundNo >= 2, "боти протягнули хоч раунд");
        Assert.Empty(h.Awards);
        var fin = Assert.Single(h.Finished);
    }

    [Fact]
    public void Bot_beats_idle_human_and_verdict_says_so()
    {
        var h = Solo("hard", 4);
        Until(h, g => g.Phase == Bakhne.PhOver, 40000);
        var g = G(h);
        Assert.DoesNotContain(0, g.Winners);
        Assert.NotEmpty(g.Winners);
        Assert.False(g.AliveOf(0));
        Assert.Single(h.Finished);
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Solo_human_knocked_out_ends_at_once_bots_with_most_hearts_win()
    {
        for (var seed = 1; seed <= 6; seed++)
        {
            var h = Solo("easy", seed);
            Until(h, g => !g.AliveOf(0), 4000);
            var g = G(h);
            // людину накрило — партія кінчається тим самим тактом, а не за кілька раундів ботячої дуелі
            Assert.Equal(Bakhne.PhOver, g.Phase);
            var alive = g.Bots.Where(g.AliveOf).ToArray();
            Assert.NotEmpty(g.Winners);
            if (alive.Length > 0)
            {
                var best = alive.Max(g.HeartsOf);
                Assert.Equal(alive.Where(s => g.HeartsOf(s) == best).Order(), g.Winners.Order());
            }
            var fin = Assert.Single(h.Finished);
            Assert.Empty(fin.Result.Winners);
            Assert.Contains("Пам'ять міцніша", fin.Result.Verdict ?? "");
            Assert.Empty(h.Awards);
        }
    }

    [Fact]
    public void Friend_joining_turns_bot_off()
    {
        var h = Table(1);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        h.Join("друг");
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Empty(G(h).Bots);
        Assert.False(G(h).BotGame);
    }

    static double MeanScore(LiveBots.Level lvl)
    {
        double sum = 0;
        var n = 0;
        for (var seed = 1; seed <= 12; seed++)
        {
            var h = new PartyHarness("bakhne", humans: 0, bots: 4, level: lvl, seed: seed);
            h.Start();
            var r = h.RunToEnd()!;
            sum += r.Scores.Values.Sum();
            n += r.Scores.Count;
        }
        return sum / n;
    }

    [Fact]
    public void Hard_bots_remember_better_than_easy()
    {
        var easy = MeanScore(LiveBots.Level.Easy);
        var hard = MeanScore(LiveBots.Level.Hard);
        Assert.True(hard > easy + 1, $"легкий {easy:0.00}, сильний {hard:0.00}");
    }

    // ---------- вечірка ----------

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Party_bots_only_finish_before_cap_with_scores_for_all(int bots)
    {
        var h = new PartyHarness("bakhne", humans: 0, bots: bots, seed: 7);
        h.Start();
        var g = (Bakhne)h.Game;
        Assert.True(g.Party);
        Assert.Equal(1, g.StartHearts);
        var r = h.RunToEnd();
        Assert.NotNull(r);
        Assert.Equal(MinigameEnd.Finished, r!.How);
        Assert.Equal(bots, r.Scores.Count);
        Assert.All(r.Scores.Values, v => Assert.InRange(v, 1, Bakhne.PartyRounds + 1));
        Assert.NotEmpty(r.Winners);
        Assert.True(h.Clock.UtcNow - h.StartedAt <= TimeSpan.FromMilliseconds(h.Host.CapMs));
        Assert.True(g.RoundNo <= Bakhne.PartyRounds);
        Assert.Equal(0, h.Ctx.Muted);
        Assert.Equal(0, h.Parent.Leaked);
        Assert.Equal(0, h.Parent.Finishes);
    }

    [Fact]
    public void Party_first_round_is_four_steps_and_idle_human_is_last()
    {
        var h = new PartyHarness("bakhne", humans: 2, bots: 2, level: LiveBots.Level.Normal, seed: 3);
        h.Start();
        var g = (Bakhne)h.Game;
        Assert.Equal([2, 3], g.Bots);
        h.TickSub(Bakhne.PartyReadyTicks + 1);
        Assert.Equal(Bakhne.PartyFirstLen, g.Len);
        var r = h.RunToEnd()!;
        Assert.Equal(4, r.Scores.Count);
        Assert.Equal(1, r.Scores[0]);
        Assert.Equal(1, r.Scores[1]);
        Assert.True(r.Scores[2] >= 1 && r.Scores[3] >= 1);
        Assert.Equal(0, h.Ctx.Muted);
    }

    [Fact]
    public void Party_good_human_is_on_top_and_scores_halfway_cover_all()
    {
        var h = new PartyHarness("bakhne", humans: 1, bots: 5, level: LiveBots.Level.Hard, seed: 9);
        h.Start();
        var g = (Bakhne)h.Game;
        var sc = new Script((_, _) => true);
        var half = false;
        var r = h.RunToEnd(x =>
        {
            sc.Step(g, (s, d) => x.Act(s, "step", new { d }), [0]);
            if (!half && g.RoundNo == 2)
            {
                half = true;
                Assert.Equal(6, g.PartyScores().Count);
            }
        })!;
        Assert.True(half);
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal(r.Scores.Values.Max(), r.Scores[0]);
        Assert.Contains(0, r.Winners);
        Assert.Equal(1, r.Places[0]);
        Assert.Equal(0, h.Ctx.Muted);
    }

    [Fact]
    public void Party_human_who_left_mid_game_is_played_out_with_score()
    {
        var h = new PartyHarness("bakhne", humans: 2, bots: 2, level: LiveBots.Level.Normal, seed: 4);
        h.Start();
        var g = (Bakhne)h.Game;
        var sc = new Script((_, _) => true);
        var left = false;
        var r = h.RunToEnd(x =>
        {
            sc.Step(g, (s, d) => x.Act(s, "step", new { d }), left ? [0] : [0, 1]);
            if (!left && g.RoundNo == 2 && g.Phase == Bakhne.PhSteps)
            {
                left = true;
                x.Parent.Away.Add(1);   // вийшов посеред міні-гри: OnLeave не кличуть, лише Seated → false
            }
        })!;
        Assert.True(left);
        Assert.Equal(MinigameEnd.Finished, r.How);
        Assert.Equal(4, r.Scores.Count);
        Assert.True(r.Scores[1] >= 2, "дограв без нього — score за раунд, де накрило");
        Assert.True(r.Scores[0] > r.Scores[1]);
        Assert.Equal(0, h.Ctx.Muted);
    }

    [Fact]
    public void Party_is_deterministic_by_seed()
    {
        string Run()
        {
            var h = new PartyHarness("bakhne", humans: 0, bots: 6, seed: 11);
            h.Start();
            var r = h.RunToEnd()!;
            return string.Join(",", r.Scores.OrderBy(kv => kv.Key).Select(kv => kv.Value)) + "|" + h.Clock.UtcNow.Ticks;
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Ordinary_table_ignores_party_keys()
    {
        var h = new RoomHarness("bakhne", options: new { party = "1", bots = "1,2" });
        h.Join("Оля");
        Assert.False(((Bakhne)h.Room.Game).Party);
    }

    // ---------- швидкість ----------

    [Fact]
    [Trait("Category", "Perf")]
    public void Thousand_ticks_with_eight_bots_under_2s()
    {
        var h = new PartyHarness("bakhne", humans: 0, bots: 8, level: LiveBots.Level.Easy, seed: 5);
        h.Start();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000 && !h.Host.Over; i++) { h.TickSub(); h.Frame(); }
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");

        var room = Solo("hard", 6);
        sw.Restart();
        for (var i = 0; i < 1000; i++) room.Tick();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} мс");
    }
}
