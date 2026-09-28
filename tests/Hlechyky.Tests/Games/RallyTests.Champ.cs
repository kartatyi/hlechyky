using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Прохід №3 (29.09): дві нові траси (№90) і чемпіонат п'яти трас за очки (№86).</summary>
public partial class RallyTests
{
    // ---------- №90 нові траси ----------

    [Fact]
    public void Wedding_and_mountain_tracks_are_on_the_menu_with_their_own_critters()
    {
        var ves = RallyTracks.Get("vesillia");
        var hora = RallyTracks.Get("hora");
        Assert.Equal("Весілля", ves.Title);
        Assert.Equal("Гора", hora.Title);
        Assert.All(ves.Critters, c => Assert.Equal(3, c[0]));                  // 💃 гості
        Assert.All(hora.Critters, c => Assert.Equal(4, c[0]));                 // 🐐 кози
        Assert.Contains('J', string.Join("", hora.Map));                        // трамплін з уступу
        var h = Table(2, new { track = "hora", laps = "3", live = "1" });
        Assert.Equal("hora", Game(h).Track.Id);
        Assert.NotEqual(0, Core(h).Live);
    }

    // ---------- №86 чемпіонат ----------

    static RoomHarness Champ(int players, string bots = "0") => Table(players, new { track = "selo", laps = "3", champ = "5", bots });

    /// <summary>Кінець гонки: місця на фініші в порядку order (решта — без фінішу), один тик.</summary>
    static void EndRace(RoomHarness h, params int[] order)
    {
        var core = Core(h);
        for (var p = 0; p < order.Length; p++) { core.Cars[order[p]].Fin = p + 1; core.Cars[order[p]].Ghost = true; }
        core.Finished = order.Length;
        h.Tick();
    }

    [Fact]
    public void A_championship_runs_five_different_tracks_scores_places_and_finishes_once()
    {
        var h = Champ(3);
        var g = Game(h);
        Assert.Equal(Rally.ChampRaces, g.Series!.Count);
        Assert.Equal(Rally.ChampRaces, g.Series.Select(t => t.Id).Distinct().Count());
        int[][] orders = [[0, 1, 2], [1, 0, 2], [0, 2, 1], [1, 2, 0], [0, 1, 2]];
        for (var r = 0; r < Rally.ChampRaces; r++)
        {
            Assert.Equal(r, g.Race);
            Assert.Equal(g.Series[r].Id, g.Track.Id);
            Assert.Equal(g.Series[r].Id, h.View(null).GetProperty("track").GetProperty("id").GetString());
            Green(h);
            EndRace(h, orders[r]);
            if (r == Rally.ChampRaces - 1) break;
            Assert.Equal(Rally.PhTable, g.Phase);
            Assert.Equal(RoomStatus.Playing, h.Room.Status);
            Assert.Empty(h.Finished);
            var v = h.View(null);
            Assert.Equal(r + 1, v.GetProperty("champ").GetProperty("n").GetInt32());
            Assert.Equal(3, v.GetProperty("results").GetArrayLength());
            Assert.Equal(Rally.TableTicks, v.GetProperty("f").GetProperty("s").GetInt32());
            Assert.False(h.Act(0, "track", new { track = "ozero" }).Ok);         // у чемпіонаті траси за жеребом
            h.Tick(Rally.TableTicks - 1);
            Assert.Equal(Rally.PhTable, g.Phase);
            h.Tick();
            Assert.Equal(Rally.PhCount, g.Phase);
        }
        // 10/6/4 за місця: Оля 10+6+10+4+10, Петро 6+10+4+10+6, Ганна 4+4+6+6+4
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Single(h.Finished);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal([("Оля", 40), ("Петро", 36), ("Ганна", 24)], g.Standings.Select(r => (r.Nick, r.Pts)));
        Assert.Contains("🥇 Оля 40 · 🥈 Петро 36 · 🥉 Ганна 24", h.Room.Result.Text);
        Assert.StartsWith("🏆", h.Room.Result.Text);
        var champ = h.View(null).GetProperty("champ");
        Assert.Equal(5, champ.GetProperty("n").GetInt32());
        Assert.Equal(40, champ.GetProperty("pts")[0].GetProperty("pts").GetInt32());
        // «Ще раз» — нова серія з нуля
        Assert.True(h.Rematch().Ok);
        Assert.Equal(0, g.Race);
        Assert.Empty(g.Standings);
        Assert.Equal(Rally.PhCount, g.Phase);
    }

    [Fact]
    public void Equal_points_and_wins_at_the_end_of_a_championship_is_a_draw()
    {
        var h = Champ(2);
        var g = Game(h);
        int[][] orders = [[0, 1], [1, 0], [0, 1], [1, 0]];
        foreach (var order in orders)
        {
            Green(h);
            EndRace(h, order);
            h.Tick(Rally.TableTicks);
        }
        // остання гонка — ніхто не доїхав за 4 хвилини: очок нікому (кермо крутять, щоб не зняла «тиша»)
        Green(h);
        for (var i = 0; i <= RallyCore.MaxRaceTicks && h.Room.Status == RoomStatus.Playing; i++)
        {
            if (i % 100 == 0) Ctl(h, 0, i % 200 == 0 ? 1 : 2);
            h.Tick();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(32, g.Standings[0].Pts);
        Assert.Equal(32, g.Standings[1].Pts);
        Assert.Empty(h.Room.Result!.Winners);
        Assert.Contains("нічия", h.Room.Result.Text);
    }

    [Fact]
    public void Leaving_mid_championship_keeps_the_points_and_the_last_human_leaving_ends_it()
    {
        var h = Champ(3);
        var g = Game(h);
        Green(h);
        EndRace(h, 2, 0, 1);                                                   // Ганна виграла першу
        Assert.Equal(Rally.PhTable, g.Phase);
        h.Leave("Ганна");
        Assert.Equal(Rally.PhTable, g.Phase);                                  // таблиця стоїть, Over удруге не кличемо
        h.Tick(Rally.TableTicks);
        Assert.Equal(Rally.PhCount, g.Phase);
        Assert.Equal(2, Core(h).Cars.Count(c => c.Present));                    // Ганниної машини вже нема
        Assert.Contains(g.Standings, r => r.Nick == "Ганна" && r.Pts == 10);     // а очки лишились
        Green(h);
        EndRace(h, 0, 1);
        Assert.Equal("Оля", g.Standings[0].Nick);                              // 6 + 10 = 16
        h.Leave("Петро");
        h.Leave("Оля");
        var end = Assert.Single(h.Finished).Result;                            // кімнату без людей каркас уже прибрав
        Assert.Empty(end.Winners);
        Assert.Contains("всі роз'їхались", end.Text);
        Assert.Contains("Ганна 10", end.Text);
    }

    [Fact]
    public void Silence_at_the_wheel_stops_the_whole_championship_not_just_the_race()
    {
        var h = Champ(2);
        Green(h);
        EndRace(h, 1, 0);
        h.Tick(Rally.TableTicks);
        Green(h);
        h.Tick(Rally.IdleTicks + 2);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Single(h.Finished);
        Assert.Contains("зупинено після 2 з 5", h.Room.Result!.Text);
        Assert.Equal([1], h.Room.Result.Winners);                              // Петро: 10 проти 6
    }

    [Fact]
    public void Bots_score_in_the_championship_table_but_the_champion_is_a_human()
    {
        var h = Champ(2, bots: "2");
        var g = Game(h);
        for (var r = 0; r < Rally.ChampRaces; r++)
        {
            Green(h);
            var bots = Enumerable.Range(0, RallyCore.Seats).Where(g.IsBot).ToArray();
            EndRace(h, [.. bots, 1, 0]);                                          // боти завжди попереду
            if (r < Rally.ChampRaces - 1) h.Tick(Rally.TableTicks);
        }
        Assert.True(g.Standings[0].Bot);
        Assert.Equal([1], h.Room.Result!.Winners);                             // найкраща людина — Петро
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:rally-win3");
    }

    [Fact]
    public void Points_words_decline_like_people_say_them()
    {
        Assert.Equal("1 очко", Rally.PtsWord(1));
        Assert.Equal("3 очки", Rally.PtsWord(3));
        Assert.Equal("10 очок", Rally.PtsWord(10));
        Assert.Equal("12 очок", Rally.PtsWord(12));
        Assert.Equal("22 очки", Rally.PtsWord(22));
    }
}
