using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using static Hlechyky.Tests.Games.DotepyTests;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Додепи удвох з Глеком» (пакет 2, 20.10): двоє людей — третім сідає 🏺 Дядько Глек, раунди — дуелі на трьох
/// (двоє пишуть, третій голосує), Глек пише заготовками й голосує сам, має очки, але переможець для каркаса — людина.
/// </summary>
public class DotepyGlekTests
{
    static JsonElement Player(RoomHarness h, int seat) =>
        V(h).GetProperty("players").EnumerateArray().First(p => p.GetProperty("seat").GetInt32() == seat);

    static bool IsGlekText(string text) => DotepyStock.Glek.Contains(text);

    [Fact]
    public void Two_humans_get_glek_as_third_player_on_the_first_free_seat()
    {
        var h = Table(2);
        var g = Game(h);
        Assert.Equal(2, g.GlekSeat);
        Assert.Null(h.Room.Seats[2]);                                    // місце в каркасі вільне — Глек не людина
        Assert.Equal(Dotepy.GlekName, h.Room.SafeSeatBot(2));           // шапка кімнати каже його ім'я
        var players = V(h).GetProperty("players").EnumerateArray().ToList();
        Assert.Equal(3, players.Count);
        Assert.Equal(Dotepy.GlekName, Player(h, 2).GetProperty("nick").GetString());
        Assert.True(Player(h, 2).GetProperty("bot").GetBoolean());
        Assert.False(Player(h, 0).GetProperty("bot").GetBoolean());
        // дуелі на трьох: три завдання, у кожної людини — два
        Assert.Equal("duel", V(h).GetProperty("mode").GetString());
        Assert.Equal(3, V(h).GetProperty("prompts").GetArrayLength());
        Assert.Equal(2, Tasks(h, 0).Length);
        Assert.Equal(2, Tasks(h, 1).Length);
        Assert.NotEqual(Tasks(h, 0).Select(t => t.I).Order(), Tasks(h, 1).Select(t => t.I).Order());
    }

    [Fact]
    public void Three_humans_play_as_before_without_glek()
    {
        var h = Table(3);
        Assert.Equal(-1, Game(h).GlekSeat);
        Assert.Null(h.Room.SafeSeatBot(3));
        Assert.Equal("all", V(h).GetProperty("mode").GetString());
        Assert.All(V(h).GetProperty("players").EnumerateArray(), p => Assert.False(p.GetProperty("bot").GetBoolean()));
        Assert.Equal(3, V(h).GetProperty("players").GetArrayLength());
    }

    [Fact]
    public void Glek_writes_with_a_delay_not_instantly()
    {
        var h = Table(2);
        WriteAll(h);                                                      // люди здали все одразу
        h.Tick();
        Assert.Equal("write", Phase(h));
        Assert.False(Player(h, 2).GetProperty("ready").GetBoolean());
        var from = h.Clock.UtcNow;
        UntilPhase(h, "vote");
        var waited = (h.Clock.UtcNow - from).TotalMilliseconds;
        Assert.InRange(waited, Dotepy.GlekWriteMinMs - 2 * Dotepy.TickMs, Dotepy.GlekWriteMinMs + 2 * Dotepy.TickMs);
    }

    [Fact]
    public void Glek_finishes_his_tasks_before_the_deadline_even_if_humans_dawdle()
    {
        var h = Table(2);
        Until(h, () => Player(h, 2).GetProperty("ready").GetBoolean());
        Assert.Equal("write", Phase(h));                                  // люди ще пишуть — фаза триває
        Assert.True(V(h).GetProperty("endsAt").GetDateTimeOffset() > h.Clock.UtcNow);
    }

    [Fact]
    public void Each_card_has_two_authors_and_only_the_third_votes_and_glek_votes_too()
    {
        var h = Table(2, seed: 11);
        var duelCards = 0;
        var glekVoted = 0;
        var finals = 0;
        PlayMatch(h, onReveal: v =>
        {
            var card = v.GetProperty("card");
            var answers = card.GetProperty("answers").EnumerateArray().ToList();
            if (v.GetProperty("final").GetBoolean())
            {
                finals++;
                return;
            }
            duelCards++;
            Assert.Equal(2, answers.Count);
            var authors = answers.Select(a => a.GetProperty("seat").GetInt32()).ToArray();
            Assert.Equal(2, authors.Distinct().Count());
            var judge = new[] { 0, 1, 2 }.Single(s => !authors.Contains(s));
            var votes = answers.SelectMany(a => a.GetProperty("votes").EnumerateArray().Select(x => x.GetInt32())).ToArray();
            Assert.Equal([judge], votes);                                 // голос один — від того, хто не автор
            if (judge == 2) glekVoted++;
        });
        Assert.Equal(6, duelCards);                                       // 2 раунди × 3 завдання
        Assert.Equal(2, glekVoted);                                       // Глек судить картку двох людей щораунду
        Assert.Equal(1, finals);
    }

    [Fact]
    public void Glek_never_votes_for_himself_in_the_final_and_gives_a_full_ballot()
    {
        var h = Table(2, options: new { rounds = "blitz" });
        WriteAll(h);
        UntilPhase(h, "vote");
        Assert.Equal(2, Card(h).GetProperty("perVoter").GetInt32());
        VoteAll(h, s => Ballot(h, s));
        UntilPhase(h, "reveal");
        var answers = Answers(h).EnumerateArray().ToList();
        Assert.Equal(3, answers.Count);
        Until(h, () => Answers(h).EnumerateArray().All(a => a.GetProperty("seat").ValueKind == JsonValueKind.Number));
        answers = [.. Answers(h).EnumerateArray()];
        var glekMedals = answers.Sum(a => a.GetProperty("votes").EnumerateArray().Count(x => x.GetInt32() == 2));
        Assert.Equal(2, glekMedals);
        var own = answers.Single(a => a.GetProperty("seat").GetInt32() == 2);
        Assert.DoesNotContain(2, own.GetProperty("votes").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void Glek_scores_points_can_win_the_table_but_the_best_human_is_the_winner()
    {
        var h = Table(2, seed: 5);
        var glekTexts = new List<string>();
        // люди щоразу голосують за додеп Глека — він набирає найбільше
        PlayMatch(h, pick: s =>
        {
            var answers = Answers(h).EnumerateArray().Select(a => a.GetProperty("text").GetString()!).ToList();
            var mine = Mine(h, s);
            var glek = Enumerable.Range(0, answers.Count).Where(i => !mine.Contains(i) && IsGlekText(answers[i])).ToList();
            var others = Enumerable.Range(0, answers.Count).Where(i => !mine.Contains(i) && !glek.Contains(i));
            return [.. glek.Concat(others).Take(Card(h).GetProperty("perVoter").GetInt32())];
        }, onReveal: v =>
        {
            foreach (var a in v.GetProperty("card").GetProperty("answers").EnumerateArray())
                if (a.GetProperty("seat").ValueKind == JsonValueKind.Number && a.GetProperty("seat").GetInt32() == 2)
                    glekTexts.Add(a.GetProperty("text").GetString()!);
        });

        Assert.True(Score(h, 2) > Score(h, 0) && Score(h, 2) > Score(h, 1));
        Assert.All(glekTexts, t => Assert.True(IsGlekText(t), t));
        Assert.Equal(glekTexts.Distinct().Count(), glekTexts.Count);     // за партію не повторюється

        var fin = Assert.Single(h.Finished);
        Assert.DoesNotContain(2, fin.Result.Winners);
        Assert.NotEmpty(fin.Result.Winners);
        Assert.False(fin.Result.Scores!.ContainsKey(2));                 // таблиця «кращих» — лише люди
        var best = fin.Result.Winners.Single();
        Assert.True(Score(h, best) >= Score(h, 1 - best));

        var log = h.Outbox.OfType<Journal>().Last().Text;
        Assert.Contains(Dotepy.GlekName, log);
        Assert.Contains($"Глек переміг, люди — {h.NickOf(best)}", log);
        var result = V(h).GetProperty("result");
        Assert.Equal(2, result.GetProperty("glek").GetInt32());
        Assert.True(result.GetProperty("glekWon").GetBoolean());
        Assert.StartsWith("Перемагаю я, Дядько Глек!", Say(h));
        Assert.DoesNotContain(h.Awards, a => a.Nick == Dotepy.GlekName);
    }

    [Fact]
    public void Humans_beating_glek_win_as_usual()
    {
        var h = Table(2, seed: 3);
        // люди голосують за людину, де можуть; Глек голосує випадково — та все одно в людей більше голосів
        PlayMatch(h, pick: s =>
        {
            var answers = Answers(h).EnumerateArray().Select(a => a.GetProperty("text").GetString()!).ToList();
            var mine = Mine(h, s);
            var human = Enumerable.Range(0, answers.Count).Where(i => !mine.Contains(i) && !IsGlekText(answers[i]));
            var glek = Enumerable.Range(0, answers.Count).Where(i => !mine.Contains(i) && IsGlekText(answers[i]));
            return [.. human.Concat(glek).Take(Card(h).GetProperty("perVoter").GetInt32())];
        });
        var fin = Assert.Single(h.Finished);
        Assert.False(V(h).GetProperty("result").GetProperty("glekWon").GetBoolean());
        Assert.DoesNotContain("Глек переміг", h.Outbox.OfType<Journal>().Last().Text);
        Assert.All(fin.Result.Winners, w => Assert.InRange(w, 0, 1));
    }

    [Fact]
    public void One_human_leaving_ends_the_match_glek_does_not_keep_it_alive()
    {
        var h = Table(2);
        WriteAll(h);
        UntilPhase(h, "vote");
        h.Leave(h.NickOf(1));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var fin = Assert.Single(h.Finished);
        Assert.DoesNotContain(2, fin.Result.Winners);
        Assert.False(fin.Result.Scores!.ContainsKey(2));
    }

    [Fact]
    public void Glek_is_not_remembered_as_having_seen_prompts()
    {
        using var temp = new TempDb();
        var services = new ServiceCollection();
        services.AddSingleton(temp.Db);
        services.AddSingleton(new DotepyPrompts(Bank()));
        services.AddSingleton(new DotepySeen(Dotepy.SeenRing));
        var h = new RoomHarness("dotepy", null, 7, services.BuildServiceProvider());
        Assert.True(h.Join(Names[0]).Ok);
        Assert.True(h.Join(Names[1]).Ok);
        Assert.True(h.Start().Ok);
        Assert.Equal(2, Game(h).GlekSeat);
        var seen = new GameSeen(temp.Db);
        Assert.NotEmpty(seen.Peek("dotepy", [GameSeen.NickKey(Names[0])]));
        Assert.Empty(seen.Peek("dotepy", [GameSeen.NickKey(Dotepy.GlekName)]));
    }

    [Fact]
    public void Rematch_of_two_seats_glek_again_and_same_seed_plays_the_same()
    {
        var a = Table(2, seed: 21);
        var b = Table(2, seed: 21);
        PlayMatch(a);
        PlayMatch(b);
        Assert.Equal(Text(a, null), Text(b, null));
        Assert.True(a.Rematch().Ok, a.Reply.Message);
        Assert.Equal(RoomStatus.Playing, a.Room.Status);
        Assert.True(Game(a).GlekSeat >= 0);
        Assert.Null(a.Room.Seats[Game(a).GlekSeat]);
        Assert.Equal(3, V(a).GetProperty("players").GetArrayLength());
    }
}
