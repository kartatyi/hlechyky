using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Скільки?», пакет 2 (20.10): «🤖 + боти» у звичайному лобі. Самотня людина — троє ботів, двоє людей — двоє,
/// стіл доповнюється до чотирьох (<see cref="Skilky.SoloBots"/>). Бот пише число з шумом за рівнем, ставить на чуже,
/// грає в команді; сам із ботами — без нагород.
/// </summary>
public class SkilkyBotsTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван"];
    const int MaxTicks = 600;

    static RoomHarness Table(int humans, object? options = null, int seed = 42, bool bots = true)
    {
        var h = new RoomHarness("skilky", options: options, seed: seed);
        for (var i = 0; i < humans; i++) h.Join(Nicks[i]);
        if (bots) Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < MaxTicks && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++) h.Tick(1);
    }

    static int[] Bots(RoomHarness h) => [.. h.View(0).GetProperty("bot").EnumerateArray().Select(x => x.GetInt32())];

    static int[] RevealSeats(RoomHarness h) =>
        [.. h.View(null).GetProperty("reveal").GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("seat").GetInt32()).Order()];

    [Fact]
    public void Alone_with_bots_the_table_fills_to_four_and_bots_answer_every_question()
    {
        var h = Table(1, new { questions = "3" });
        var v = h.View(0);
        Assert.True(v.GetProperty("botOffer").GetBoolean());
        Assert.Equal(4, v.GetProperty("botFill").GetInt32());
        Assert.Equal([1, 2, 3], Bots(h));
        h.Start();
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("🤖 бот 1", h.Room.Summary().Seats[1].Bot);
        for (var q = 0; q < 3; q++)
        {
            Until(h, Skilky.PhaseAsk);
            Until(h, Skilky.PhaseReveal);
            // людина мовчала — боти однаково написали числа
            Assert.Equal([1, 2, 3], RevealSeats(h));
        }
        Until(h, Skilky.PhaseDone);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        // сам із ботами — без нагород: ні черепків за очки, ні людських переможців-ботів
        Assert.Empty(h.Awards);
        Assert.DoesNotContain(h.Finished[0].Result.Winners, s => s != 0);
        Assert.Contains("без нагород", h.Room.Result!.Verdict);
        Assert.Contains("🤖 бот", h.Room.Result!.Text);
    }

    [Fact]
    public void Bot_waits_for_the_bot_button_and_lone_human_can_still_play_without_bots()
    {
        var h = Table(1, new { questions = "3" }, bots: false);
        Assert.Empty(Bots(h));
        h.Start();
        Until(h, Skilky.PhaseAsk);
        Until(h, Skilky.PhaseReveal);
        Assert.Empty(RevealSeats(h));
    }

    [Fact]
    public void Two_humans_get_two_bots_and_a_full_table_gets_none()
    {
        var h = Table(2);
        Assert.Equal([2, 3], Bots(h));
        h.Join(Nicks[2]);
        Assert.Equal([3], Bots(h));
        h.Join(Nicks[3]);
        Assert.Empty(Bots(h));
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);   // стіл повний — кликати нікого
    }

    [Fact]
    public void Bots_cannot_be_called_mid_game_but_can_between_games()
    {
        var h = Table(1, new { questions = "3" });
        h.Start();
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);
        Until(h, Skilky.PhaseDone);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);
    }

    [Fact]
    public void Bets_open_with_one_human_and_bots_and_bots_bet_too()
    {
        var h = Table(1, new { questions = "3", bets = "on" });
        h.Start();
        Until(h, Skilky.PhaseAsk);
        Assert.True(h.Act(0, "answer", new { value = 1000 }).Ok);
        Until(h, Skilky.PhaseBet);
        Assert.Equal(Skilky.PhaseBet, Phase(h));
        var values = h.View(0).GetProperty("bet").GetProperty("values").EnumerateArray().Select(x => x.GetProperty("seat").GetInt32()).ToArray();
        Assert.Equal(4, values.Length);                                    // числа ботів теж на столі
        Assert.True(h.Act(0, "bet", new { seat = 1 }).Ok);
        Until(h, Skilky.PhaseReveal);
        var bets = h.View(null).GetProperty("reveal").GetProperty("bets").EnumerateArray().Select(b => b.GetProperty("seat").GetInt32()).Order().ToArray();
        Assert.Equal([0, 1, 2, 3], bets);
    }

    [Fact]
    public void Bots_play_in_teams_and_a_bot_captain_hands_in_the_number()
    {
        var h = Table(1, new { questions = "5", teams = "2" });
        h.Start();
        var teams = h.View(0).GetProperty("teams");
        Assert.Equal(2, teams.GetArrayLength());
        Assert.Equal([0, 1, 2, 3], teams.EnumerateArray().SelectMany(t => t.GetProperty("seats").EnumerateArray().Select(x => x.GetInt32())).Order());
        // команда, де людини нема, мусить подати число сама — розкриття має рядок кожної команди
        Until(h, Skilky.PhaseAsk);
        Until(h, Skilky.PhaseReveal);
        var rows = h.View(null).GetProperty("reveal").GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("team").GetInt32()).ToArray();
        Assert.Contains(rows, t => t >= 0);
        Assert.True(rows.Length >= 1);
        var botTeam = teams.EnumerateArray().Select((t, i) => (i, seats: t.GetProperty("seats").EnumerateArray().Select(x => x.GetInt32()).ToArray()))
            .First(t => !t.seats.Contains(0)).i;
        Assert.Contains(botTeam, rows);
    }

    [Fact]
    public void Two_humans_with_bots_play_for_real_and_earn_shards()
    {
        var h = Table(2, new { questions = "3" });
        h.Start();
        for (var q = 0; q < 3; q++)
        {
            Until(h, Skilky.PhaseAsk);
            var text = h.View(null).GetProperty("question").GetString();
            var truth = SkilkyBank.All.First(x => x.Q == text).A!.Value;
            Assert.True(h.Act(0, "answer", new { value = truth }).Ok);
            Assert.True(h.Act(1, "answer", new { value = truth }).Ok);
            Until(h, Skilky.PhaseReveal);
            Assert.Equal([0, 1, 2, 3], RevealSeats(h));
        }
        Until(h, Skilky.PhaseDone);
        Assert.Contains(h.Awards, a => a.Nick == Nicks[0] && a.Reason.StartsWith("points:"));
        Assert.Null(h.Room.Result!.Verdict);
    }

    /// <summary>Сильний бот ближчий до правди за легкого — на багатьох запитаннях.</summary>
    [Fact]
    public void Hard_bots_are_closer_than_easy_ones()
    {
        double Miss(string lvl)
        {
            var sum = 0.0;
            var n = 0;
            for (var seed = 1; seed <= 8; seed++)
            {
                var h = Table(1, new { questions = "5", botlvl = lvl }, seed: seed);
                h.Start();
                for (var q = 0; q < 5 && h.Room.Status == RoomStatus.Playing; q++)
                {
                    Until(h, Skilky.PhaseReveal);
                    var rv = h.View(null).GetProperty("reveal");
                    var truth = rv.GetProperty("answer").GetDouble();
                    var years = rv.GetProperty("years").GetBoolean();
                    foreach (var r in rv.GetProperty("rows").EnumerateArray())
                    {
                        var val = r.GetProperty("value").GetDouble();
                        sum += years ? Math.Min(1, Math.Abs(val - truth) / 50) : Math.Min(1, Math.Abs(Math.Log(Math.Max(val, 1e-9) / truth)));
                        n++;
                    }
                    h.Tick(Skilky.RevealSeconds + 1);
                }
            }
            return sum / n;
        }
        Assert.True(Miss("hard") < Miss("easy"));
    }

    [Fact]
    public void Daily_has_no_bot_button()
    {
        var h = new RoomHarness("skilky-daily");
        h.Solo("Оля");
        Assert.False(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
    }
}
