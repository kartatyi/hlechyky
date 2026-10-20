using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек-слово, пакет 4 «Щоденні» (specs/wordle.md): програні дні, табло дня, сітки друзів, архів, складний режим ✦.
/// День FakeClock — 2026-09-10.
/// </summary>
public class WordleP4Tests(WordleWords fx) : IClassFixture<WordleWords>
{
    const string Day = "2026-09-10";

    string Answer => fx.Words.Daily5ForDay(Day);

    string[] Wrong(int n) => [.. fx.Five.Where(w => w != Answer).Take(n)];

    sealed class Rig
    {
        public required RoomHarness H;
        public required DailyCard Card;
    }

    Rig Solo(string nick = "Оля", DailyCard? card = null, WordleFriends? friends = null)
    {
        card ??= new DailyCard(null, new FakeClock());
        friends ??= new WordleFriends(null, new FakeClock());
        var sp = new ServiceCollection().AddSingleton(fx.Words).AddSingleton(card).AddSingleton(friends).BuildServiceProvider();
        var h = new RoomHarness("wordle", services: sp);
        h.Solo(nick);
        return new Rig { H = h, Card = card };
    }

    static ActResult Guess(RoomHarness h, string word) => h.Act(0, "guess", new { word });

    // ----------------------------------------------------------------------------- 1. програні дні

    [Fact]
    public void Six_misses_send_one_daily_miss_and_a_cross_on_the_day_card()
    {
        var r = Solo();
        foreach (var w in Wrong(6)) Assert.True(Guess(r.H, w).Ok);

        var miss = Assert.Single(r.H.Scores);
        Assert.True(miss.Missed);
        Assert.Equal(Wordle.MaxTries, miss.Attempts);
        Assert.StartsWith("daily:wordle:", miss.Key);
        var card = r.Card.Get("wordle")!;
        Assert.Equal(("Оля", 6), (Assert.Single(card.Missed!).Nick, card.Missed![0].Attempts));
    }

    [Fact]
    public void A_lost_day_written_by_the_room_lands_in_daily_results_as_unsolved()
    {
        using var rig = new EconomyRig();
        var r = Solo();
        foreach (var w in Wrong(6)) Guess(r.H, w);
        rig.Events.Raise(Assert.Single(r.H.Scores) with { Key = $"daily:wordle:{rig.Daily.Today()}:оля" });

        var row = rig.Daily.MyResult("Оля", "wordle")!;
        Assert.False(row.Solved);
        Assert.Equal(6, row.Attempts);
    }

    [Fact]
    public void Reopening_a_lost_day_does_not_send_the_miss_again()
    {
        var r = Solo();
        foreach (var w in Wrong(6)) Guess(r.H, w);
        var saved = r.H.Room.Game.Save()!;

        var again = Solo("Оля");
        again.H.Room.Game.Load(saved);
        again.H.Rematch();
        Assert.Empty(again.H.Scores);
        Assert.True(again.H.View(0).GetProperty("failed").GetBoolean());
    }

    [Fact]
    public void Stats_count_lost_days_in_the_cross_column_but_not_in_the_streak()
    {
        var solved = new[] { ("2026-09-08", 3), ("2026-09-09", 4) };
        var s = WordleStats.Of(solved, "2026-09-10", ["2026-09-07", "2026-09-10", "2026-09-09"]);
        Assert.Equal(2, s.Miss);          // 09-09 розв'язано — промах того дня не рахується
        Assert.True(s.MissToday);
        Assert.Equal(2, s.Streak);
        Assert.Equal(2, s.Solved);
        Assert.Equal(0, WordleStats.Of(solved, "2026-09-10").Miss);
    }

    // ------------------------------------------------------------------------------ 2. табло дня

    [Fact]
    public void Day_board_shows_solves_and_misses_before_and_after_my_own_game()
    {
        var card = new DailyCard(null, new FakeClock());
        var petro = Solo("Петро", card);
        foreach (var w in Wrong(6)) Guess(petro.H, w);
        var olia = Solo("Оля", card);
        Guess(olia.H, Wrong(1)[0]);
        Assert.True(Guess(olia.H, Answer).Ok);

        // я ще не грав — табло вже є
        var me = Solo("Іван", card);
        var b = me.H.View(0).GetProperty("board");
        var row = Assert.Single(b.GetProperty("rows").EnumerateArray());
        Assert.Equal(("Оля", 2), (row.GetProperty("n").GetString(), row.GetProperty("a").GetInt32()));
        Assert.Equal("Петро", Assert.Single(b.GetProperty("miss").EnumerateArray()).GetProperty("n").GetString());
        Assert.Equal(1, olia.H.View(0).GetProperty("streak").GetInt32());
    }

    // ---------------------------------------------------------------------------- 3. сітки друзів

    [Fact]
    public void Friends_grids_are_colours_only_until_my_day_is_over_then_words()
    {
        var friends = new WordleFriends(null, new FakeClock());
        var olia = Solo("Оля", friends: friends);
        var oliaWord = Wrong(1)[0];
        Guess(olia.H, oliaWord);
        Guess(olia.H, Answer);

        var me = Solo("Іван", friends: friends);
        var mine = Wrong(7)[1..];
        Guess(me.H, mine[0]);
        var v = me.H.View(0);
        var f = Assert.Single(v.GetProperty("friends").EnumerateArray());
        Assert.Equal("Оля", f.GetProperty("n").GetString());
        Assert.True(f.GetProperty("solved").GetBoolean());
        Assert.Equal(["" + Wordle.Marks(Answer, oliaWord), "GGGGG"], f.GetProperty("marks").EnumerateArray().Select(x => x.GetString()!));
        Assert.Equal(JsonValueKind.Null, f.GetProperty("words").ValueKind);
        // ні слова друга, ні відповіді дня у виді нема — ні мені, ні глядачеві
        foreach (var seat in new int?[] { 0, null })
        {
            var text = Views.Text(me.H.Room.Game.View(seat));
            Assert.DoesNotContain(Answer, text, StringComparison.Ordinal);
            Assert.DoesNotContain(oliaWord, text, StringComparison.Ordinal);
        }
        // сам себе я в друзях не бачу
        Assert.DoesNotContain(olia.H.View(0).GetProperty("friends").EnumerateArray(), x => x.GetProperty("n").GetString() == "Оля");

        foreach (var w in mine[1..]) Guess(me.H, w);
        f = Assert.Single(me.H.View(0).GetProperty("friends").EnumerateArray());
        Assert.Equal([oliaWord, Answer], f.GetProperty("words").EnumerateArray().Select(x => x.GetString()!));
    }

    [Fact]
    public void Friends_grids_are_read_from_todays_saved_states()
    {
        using var rig = new EconomyRig();
        var store = new SqliteGameStore(rig.Db, rig.Clock);
        var today = rig.Daily.Today();
        var w = fx.Five.Where(x => x != fx.Words.Daily5ForDay(today)).Take(2).ToArray();
        // старий формат стану — без поля hard
        store.SaveState($"daily:wordle:{today}:петро", JsonSerializer.Serialize(new { day = today, guesses = w, solved = false, failed = false }));
        store.SaveState("daily:wordle:2026-01-01:петро", JsonSerializer.Serialize(new { day = "2026-01-01", guesses = w, solved = false, failed = false }));
        rig.Daily.Record("wordle", "Петро", solved: true, attempts: 3, ms: 0);
        var friends = new WordleFriends(rig.Db, rig.Clock);
        friends.LoadNow();
        var g = Assert.Single(friends.Today());
        Assert.Equal(("Петро", 2, false), (g.Nick, g.Guesses.Count, g.Hard));
    }

    // ---------------------------------------------------------------------------------- 4. архів

    static readonly DateTimeOffset Later = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);   // день №11

    (RoomHarness H, WordleHistory History) Archive(string nick = "Оля")
    {
        var history = new WordleHistory(null, new FakeClock { UtcNow = Later });
        var sp = new ServiceCollection().AddSingleton(fx.Words).AddSingleton(history).BuildServiceProvider();
        var h = new RoomHarness("wordle-archive", services: sp);
        h.Clock.UtcNow = Later;
        h.Solo(nick);
        return (h, history);
    }

    [Fact]
    public void Archive_lists_only_past_days_and_refuses_today_and_the_future()
    {
        var (h, _) = Archive();
        Assert.Equal("wordle-archive:оля", h.Room.Key);
        var days = h.View(0).GetProperty("days").EnumerateArray().Select(d => d.GetProperty("day").GetString()).ToArray();
        Assert.Equal(10, days.Length);                  // дні №1…№10, сьогоднішнього (№11) нема
        Assert.Equal("2026-09-19", days[0]);
        Assert.Equal("2026-09-10", days[^1]);
        Assert.False(h.Act(0, "open", new { day = "2026-09-20" }).Ok);
        Assert.False(h.Act(0, "open", new { day = "2026-09-21" }).Ok);
        Assert.False(h.Act(0, "open", new { day = "2026-09-09" }).Ok);
        Assert.False(h.Act(0, "guess", new { word = Answer }).Ok);   // день не обрано
    }

    [Fact]
    public void Archive_play_writes_nothing_pays_nothing_and_hides_the_answer_until_the_end()
    {
        var (h, _) = Archive();
        const string day = "2026-09-15";
        var answer = fx.Words.Daily5ForDay(day);
        Assert.True(h.Act(0, "open", new { day }).Ok);
        var wrong = fx.Five.First(w => w != answer);
        Assert.True(h.Act(0, "guess", new { word = wrong }).Ok);
        var cur = h.View(0).GetProperty("cur");
        Assert.Equal(JsonValueKind.Null, cur.GetProperty("answer").ValueKind);
        Assert.DoesNotContain(answer, Views.Text(h.Room.Game.View(0)), StringComparison.Ordinal);

        Assert.True(h.Act(0, "guess", new { word = answer }).Ok);
        cur = h.View(0).GetProperty("cur");
        Assert.True(cur.GetProperty("solved").GetBoolean());
        Assert.Equal(answer, cur.GetProperty("answer").GetString());
        Assert.Contains("(архів) 2/6", cur.GetProperty("share").GetString());
        Assert.False(h.Act(0, "guess", new { word = wrong }).Ok);   // дограно

        Assert.Empty(h.Scores);
        Assert.Empty(h.Awards);
        Assert.Empty(h.Finished);
        Assert.Empty(h.Outbox.OfType<Journal>());
        var d = h.View(0).GetProperty("days").EnumerateArray().First(x => x.GetProperty("day").GetString() == day);
        Assert.Equal(("s", 2), (d.GetProperty("arch").GetString(), d.GetProperty("aa").GetInt32()));

        // збереження переживає перезапуск
        var saved = h.Room.Game.Save()!;
        var (again, _) = Archive();
        again.Room.Game.Load(saved);
        Assert.True(again.View(0).GetProperty("cur").GetProperty("solved").GetBoolean());
    }

    [Fact]
    public void A_day_played_on_its_own_day_is_view_only_with_my_old_grid()
    {
        var (h, history) = Archive();
        const string day = "2026-09-12";
        var answer = fx.Words.Daily5ForDay(day);
        var wrong = fx.Five.First(w => w != answer);
        history.Put("Оля", new Dictionary<string, WordleDayResult> { [day] = new(true, 2, [wrong, answer]) });

        Assert.True(h.Act(0, "open", new { day }).Ok);
        Assert.False(h.Act(0, "guess", new { word = answer }).Ok);
        var cur = h.View(0).GetProperty("cur");
        Assert.True(cur.GetProperty("view").GetBoolean());
        Assert.Equal(2, cur.GetProperty("rows").GetArrayLength());
        Assert.Equal(answer, cur.GetProperty("answer").GetString());
        var d = h.View(0).GetProperty("days").EnumerateArray().First(x => x.GetProperty("day").GetString() == day);
        Assert.Equal(("s", 2), (d.GetProperty("mine").GetString(), d.GetProperty("a").GetInt32()));
    }

    [Fact]
    public void History_reads_my_past_results_and_grids_from_the_base()
    {
        using var rig = new EconomyRig();
        rig.Clock.UtcNow = Later;
        var day = "2026-09-12";
        var answer = fx.Words.Daily5ForDay(day);
        var store = new SqliteGameStore(rig.Db, rig.Clock);
        store.SaveState($"daily:wordle:{day}:оля", JsonSerializer.Serialize(new { day, guesses = new[] { answer }, solved = true, failed = false }));
        store.SaveState("daily:wordle:2026-09-20:оля", JsonSerializer.Serialize(new { day = "2026-09-20", guesses = new[] { answer }, solved = false, failed = false }));
        rig.Daily.Record("wordle", "Оля", solved: true, attempts: 1, ms: 0);   // сьогодні — не архів
        rig.Db.Exec("INSERT INTO daily_results(day, game, nick_key, nick, solved, attempts, ms, created_at) VALUES($d, 'wordle', 'оля', 'Оля', 1, 1, 0, 'x')", ("$d", day));
        var history = new WordleHistory(rig.Db, rig.Clock);
        history.LoadNow("Оля");
        var mine = history.Get("Оля")!;
        var r = Assert.Single(mine);
        Assert.Equal(day, r.Key);
        Assert.Equal([answer], r.Value.Guesses);
    }

    [Fact]
    public void Old_lost_days_from_game_state_are_backfilled_once()
    {
        using var rig = new EconomyRig();
        var lostDay = "2026-09-05";
        var wrong = fx.Five.Where(w => w != fx.Words.Daily5ForDay(lostDay)).Take(6).ToArray();
        var store = new SqliteGameStore(rig.Db, rig.Clock);
        store.SaveState($"daily:wordle:{lostDay}:оля", JsonSerializer.Serialize(new { day = lostDay, guesses = wrong, solved = false, failed = true }));
        // розв'язаний і недограний дні не чіпаються
        store.SaveState("daily:wordle:2026-09-06:оля", JsonSerializer.Serialize(new { day = "2026-09-06", guesses = new[] { fx.Words.Daily5ForDay("2026-09-06") }, solved = true, failed = false }));
        store.SaveState("daily:wordle:2026-09-07:петро", JsonSerializer.Serialize(new { day = "2026-09-07", guesses = wrong[..3], solved = false, failed = false }));
        rig.Daily.Record("wordle", "Оля", solved: true, attempts: 2, ms: 0);   // звідси береться нік із великої

        Assert.Equal(1, WordleSetup.BackfillMisses(rig.Db, fx.Words));
        Assert.Equal(0, WordleSetup.BackfillMisses(rig.Db, fx.Words));   // ідемпотентно

        var nick = rig.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT nick || '|' || solved || '|' || attempts FROM daily_results WHERE day = $d AND game = 'wordle'";
            cmd.Parameters.AddWithValue("$d", lostDay);
            return cmd.ExecuteScalar() as string;
        });
        Assert.Equal("Оля|0|6", nick);
    }
}
