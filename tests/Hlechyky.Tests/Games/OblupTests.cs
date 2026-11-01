using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Як облупленого» (specs/oblup.md): черга «в центрі», вибір питання, рамки й риска, приховані види, свої питання,
/// хто пішов, кінець і нагороди, банк, що їде з грою. Партія живе від тика — тож тут переважно «прокрути й подивись».
/// </summary>
public class OblupTests
{
    const int MaxTicks = 2000;

    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан", "Леся", "Остап"];

    /// <summary>Свій маленький банк: питання «Питання N?», останні <paramref name="spicy"/> — гострі.</summary>
    static List<OblupQuestion> Bank(int n = 20, int spicy = 0) =>
        [.. Enumerable.Range(0, n).Select(i => new OblupQuestion($"t{i:00}", $"Питання {i}?", "ліво", "право", [], i >= n - spicy))];

    static RoomHarness Table(int players = 3, object? options = null, int seed = 42, IReadOnlyList<OblupQuestion>? bank = null, bool start = true)
    {
        var h = new RoomHarness("oblup", options, seed, RoomHarness.WithService(new OblupQuestions(bank ?? Bank())));
        for (var i = 0; i < players; i++) Assert.True(h.Join(Nicks[i]).Ok);
        if (start) Assert.True(h.Start().Ok);
        return h;
    }

    static JsonElement V(RoomHarness h, int? seat = null) => h.View(seat);
    static string Phase(RoomHarness h) => V(h).GetProperty("phase").GetString()!;
    static int Hero(RoomHarness h) => V(h).GetProperty("hero").GetInt32();
    static long Score(RoomHarness h, int seat) => V(h).GetProperty("scores")[seat].GetInt64();

    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < MaxTicks && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++) h.Tick(1);
        Assert.Equal(phase, Phase(h));
    }

    /// <summary>Дочекатись вибору, обрати перше питання й дочекатись ставок. Повертає героя.</summary>
    static int ToGuess(RoomHarness h, int pick = 0)
    {
        Until(h, Oblup.PhasePick);
        var hero = Hero(h);
        Assert.True(h.Act(hero, "pick", new { i = pick }).Ok);
        Until(h, Oblup.PhaseGuess);
        return hero;
    }

    static JsonElement Row(RoomHarness h, int seat) =>
        V(h).GetProperty("reveal").GetProperty("rows").EnumerateArray().First(r => r.GetProperty("seat").GetInt32() == seat);

    [Fact]
    public void Rims_score_by_width_and_a_bullseye_adds_one()
    {
        var h = Table(4, new { handicap = "0" });
        var hero = ToGuess(h);
        var others = Enumerable.Range(0, 4).Where(s => s != hero).ToArray();
        Assert.True(h.Act(hero, "place", new { pos = 50 }).Ok);
        Assert.True(h.Act(others[0], "place", new { pos = 64, size = "w" }).Ok);   // 14 від центру ≤ 15 — влучання
        Assert.True(h.Act(others[1], "place", new { pos = 54, size = "n" }).Ok);   // 4 ≤ 4 — межа включно
        Assert.True(h.Act(others[2], "place", new { pos = 51, size = "m" }).Ok);   // ±1 — у яблучко
        h.Tick(1);
        Assert.Equal(Oblup.PhaseReveal, Phase(h));
        Assert.Equal(50, V(h).GetProperty("reveal").GetProperty("pos").GetInt32());
        Assert.Equal(2, Row(h, others[0]).GetProperty("points").GetInt32());
        Assert.Equal(6, Row(h, others[1]).GetProperty("points").GetInt32());
        Assert.False(Row(h, others[1]).GetProperty("bull").GetBoolean());
        Assert.Equal(3 + Oblup.BullBonus, Row(h, others[2]).GetProperty("points").GetInt32());
        Assert.True(Row(h, others[2]).GetProperty("bull").GetBoolean());
        Assert.Equal(2, Score(h, others[0]));
        Assert.Equal(6, Score(h, others[1]));
        Assert.Equal(0, Score(h, hero));
        Assert.False(string.IsNullOrEmpty(V(h).GetProperty("reveal").GetProperty("say").GetString()));
    }

    [Fact]
    public void A_miss_scores_nothing_and_rims_stay_on_the_scale()
    {
        var h = Table(3, new { handicap = "0" });
        var hero = ToGuess(h);
        var others = Enumerable.Range(0, 3).Where(s => s != hero).ToArray();
        Assert.True(h.Act(others[0], "place", new { pos = 0, size = "n" }).Ok);
        Assert.True(h.Act(others[1], "place", new { pos = 100, size = "w" }).Ok);
        // рамка цілком на шкалі: вузька не лівіше за 4, широка не правіше за 85
        Assert.Equal(4, V(h, others[0]).GetProperty("my").GetProperty("pos").GetInt32());
        Assert.Equal(85, V(h, others[1]).GetProperty("my").GetProperty("pos").GetInt32());
        Assert.True(h.Act(hero, "place", new { pos = 30 }).Ok);
        h.Tick(1);
        Assert.False(Row(h, others[0]).GetProperty("hit").GetBoolean());
        Assert.False(Row(h, others[1]).GetProperty("hit").GetBoolean());
        Assert.Equal(0, Score(h, others[0]) + Score(h, others[1]));
    }

    [Fact]
    public void Nobody_sees_foreign_marks_before_the_line_drops()
    {
        var h = Table(3);
        Until(h, Oblup.PhasePick);
        var hero = Hero(h);
        var other = Enumerable.Range(0, 3).First(s => s != hero);
        // два питання на вибір — лише героєві
        Assert.Equal(2, V(h, hero).GetProperty("choices").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, V(h, other).GetProperty("choices").ValueKind);
        Assert.Equal(JsonValueKind.Null, V(h, other).GetProperty("q").ValueKind);
        Assert.True(h.Act(hero, "pick", new { i = 1 }).Ok);
        Until(h, Oblup.PhaseGuess);
        Assert.True(h.Act(hero, "place", new { pos = 77 }).Ok);
        Assert.True(h.Act(other, "place", new { pos = 33, size = "m" }).Ok);
        h.Tick(1);
        foreach (int? seat in new int?[] { null, 0, 1, 2 })
        {
            var v = V(h, seat);
            Assert.Equal(JsonValueKind.Null, v.GetProperty("reveal").ValueKind);
            var mine = seat == hero ? "77" : seat == other ? "33" : null;
            var raw = v.GetProperty("my").GetRawText();
            if (mine is null) Assert.DoesNotContain("pos", raw);
            else Assert.Contains(mine, raw);
        }
        // галочки видно всім, і в кадрі теж
        var done = V(h).GetProperty("done");
        Assert.True(done[hero].GetBoolean());
        Assert.True(done[other].GetBoolean());
    }

    [Fact]
    public void Illegal_moves_fail_and_change_nothing()
    {
        var lobby = Table(3, start: false);
        Assert.False(lobby.Act(0, "place", new { pos = 10, size = "w" }).Ok);

        var h = Table(3);
        Until(h, Oblup.PhasePick);
        var hero = Hero(h);
        var other = Enumerable.Range(0, 3).First(s => s != hero);
        var before = V(h, other).GetRawText();
        Assert.False(h.Act(other, "pick", new { i = 0 }).Ok);          // обирає лише герой
        Assert.False(h.Act(other, "place", new { pos = 10, size = "w" }).Ok);   // ще обирають
        Assert.False(h.Act(hero, "pick", new { i = 5 }).Ok);
        Assert.False(h.Act(other, "dance").Ok);
        Assert.Equal(before, V(h, other).GetRawText());
        Assert.True(h.Act(hero, "pick", new { i = 0 }).Ok);
        Until(h, Oblup.PhaseGuess);
        Assert.False(h.Act(other, "place", new { pos = 10, size = "xl" }).Ok);
        Assert.False(h.Act(other, "place", new { size = "w" }).Ok);
        Assert.Equal(JsonValueKind.Null, V(h, other).GetProperty("my").ValueKind);
    }

    [Fact]
    public void Without_a_mark_from_the_hero_the_round_scores_nothing()
    {
        var h = Table(3);
        var hero = ToGuess(h);
        foreach (var s in Enumerable.Range(0, 3).Where(s => s != hero)) h.Act(s, "place", new { pos = 50, size = "w" });
        Until(h, Oblup.PhaseReveal);
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("reveal").GetProperty("pos").ValueKind);
        Assert.All(Enumerable.Range(0, 3), s => Assert.Equal(0, Score(h, s)));
    }

    [Fact]
    public void Pick_timeout_takes_the_first_question()
    {
        var h = Table(3);
        Until(h, Oblup.PhasePick);
        var first = V(h, Hero(h)).GetProperty("choices")[0].GetProperty("q").GetString();
        Until(h, Oblup.PhaseGuess);
        Assert.Equal(first, V(h).GetProperty("q").GetProperty("q").GetString());
    }

    /// <summary>Пройти партію до кінця: герой завжди 40, решта — широкою рамкою в 45. Повертає героїв і питання раундів.</summary>
    static (List<int> Heroes, List<string> Questions) PlayOut(RoomHarness h)
    {
        var heroes = new List<int>();
        var qs = new List<string>();
        for (var guard = 0; guard < 100 && h.Room.Status == RoomStatus.Playing; guard++)
        {
            for (var i = 0; i < MaxTicks && h.Room.Status == RoomStatus.Playing && Phase(h) != Oblup.PhasePick; i++) h.Tick(1);
            if (h.Room.Status != RoomStatus.Playing) break;
            var hero = Hero(h);
            heroes.Add(hero);
            h.Act(hero, "pick", new { i = 0 });
            Until(h, Oblup.PhaseGuess);
            qs.Add(V(h).GetProperty("q").GetProperty("q").GetString()!);
            h.Act(hero, "place", new { pos = 40 });
            for (var s = 0; s < Oblup.MaxSeats; s++)
                if (s != hero && s < h.Room.Seats.Length && h.Room.Seats[s] is not null) h.Act(s, "place", new { pos = 45, size = "w" });
            Until(h, Oblup.PhaseReveal);
        }
        return (heroes, qs);
    }

    [Fact]
    public void Everyone_is_in_the_centre_equally_and_questions_do_not_repeat()
    {
        var h = Table(3);
        var (heroes, qs) = PlayOut(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(6, heroes.Count);                                  // auto: утрьох — два кола
        Assert.All(Enumerable.Range(0, 3), s => Assert.Equal(2, heroes.Count(x => x == s)));
        Assert.Equal(qs.Count, qs.Distinct().Count());

        var big = Table(5, new { laps = "auto" });
        Assert.Equal(5, PlayOut(big).Heroes.Count);                     // від п'яти — одне коло
        var three = Table(2, new { laps = "3" });
        Assert.Equal(6, PlayOut(three).Heroes.Count);
    }

    [Fact]
    public void The_end_names_winners_pays_shards_and_tells_who_knows_whom()
    {
        var h = Table(3, new { handicap = "0" });
        PlayOut(h);
        var fin = Assert.Single(h.Finished);
        // кожен вгадував чотири рази широкою (+2): 8 очок у всіх — нічия на трьох
        Assert.Equal(3, fin.Result.Winners.Length);
        Assert.All(Enumerable.Range(0, 3), s => Assert.Equal(8, Score(h, s)));
        Assert.Equal(3, h.Awards.Count(a => a.Shards == Oblup.Shards(8) && a.Reason.StartsWith("points:", StringComparison.Ordinal)));
        var recap = V(h).GetProperty("recap");
        Assert.Equal(6, recap.GetProperty("rounds").GetArrayLength());
        Assert.Equal(3, recap.GetProperty("knows").GetArrayLength());
        Assert.Equal(Oblup.PhaseDone, Phase(h));
    }

    [Fact]
    public void Open_book_and_dark_horse_appear_when_heroes_differ()
    {
        var h = Table(3, new { handicap = "0", laps = "1" });
        for (var round = 0; round < 3; round++)
        {
            var hero = ToGuess(h);
            h.Act(hero, "place", new { pos = hero == 0 ? 50 : 95 });
            foreach (var s in Enumerable.Range(0, 3).Where(s => s != hero)) h.Act(s, "place", new { pos = 50, size = "w" });
            Until(h, Oblup.PhaseReveal);
        }
        Until(h, Oblup.PhaseDone);
        var recap = V(h).GetProperty("recap");
        Assert.Equal(0, recap.GetProperty("open").GetProperty("seat").GetInt32());
        Assert.Equal(JsonValueKind.Null, recap.GetProperty("dark").ValueKind);   // 1 і 2 — однаково темні: не вибираємо
    }

    [Fact]
    public void When_the_hero_leaves_the_round_is_void_and_the_game_goes_on()
    {
        var h = Table(3);
        var hero = ToGuess(h);
        var others = Enumerable.Range(0, 3).Where(s => s != hero).ToArray();
        h.Act(others[0], "place", new { pos = 50, size = "w" });
        h.Leave(h.NickOf(hero));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(Oblup.PhaseReveal, Phase(h));
        Assert.Equal(0, Score(h, others[0]));
        var (heroes, _) = PlayOut(h);
        Assert.DoesNotContain(hero, heroes);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Two_at_the_table_and_one_leaves_ends_the_game()
    {
        var h = Table(2);
        ToGuess(h);
        h.Leave(Nicks[1]);
        Assert.NotEqual(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Own_questions_are_validated_capped_and_never_go_to_their_author()
    {
        var h = Table(3, start: false);
        Assert.False(h.Act(0, "mine", new { q = "Що?", l = "а", r = "б" }).Ok);
        Assert.False(h.Act(0, "mine", new { q = "Наскільки ти любиш котів?", l = "", r = "дуже" }).Ok);
        Assert.False(h.Act(0, "mine", new { q = "Наскільки ти любиш котів?", l = new string('а', 40), r = "дуже" }).Ok);
        for (var i = 0; i < Oblup.MineMax; i++) Assert.True(h.Act(0, "mine", new { q = $"Своє питання номер {i}", l = "ні", r = "так" }).Ok);
        Assert.False(h.Act(0, "mine", new { q = "Ще одне своє питання", l = "ні", r = "так" }).Ok);
        Assert.Equal(Oblup.MineMax, V(h, 0).GetProperty("mine").GetProperty("list").GetArrayLength());
        Assert.Equal(0, V(h, 1).GetProperty("mine").GetProperty("list").GetArrayLength());
        var del = V(h, 0).GetProperty("mine").GetProperty("list")[0].GetProperty("id").GetString();
        Assert.True(h.Act(0, "mine", new { del }).Ok);
        Assert.False(h.Act(1, "mine", new { del = V(h, 0).GetProperty("mine").GetProperty("list")[0].GetProperty("id").GetString() }).Ok);
        Assert.True(h.Start().Ok);

        var offered = 0;
        for (var guard = 0; guard < 6 && h.Room.Status == RoomStatus.Playing; guard++)
        {
            Until(h, Oblup.PhasePick);
            var hero = Hero(h);
            var choices = V(h, hero).GetProperty("choices").EnumerateArray().ToList();
            var own = choices.FindIndex(c => c.GetProperty("own").GetBoolean());
            if (hero == 0) Assert.Equal(-1, own);
            else if (own >= 0) offered++;
            Assert.True(choices.Count(c => c.GetProperty("own").GetBoolean()) <= 1);
            h.Act(hero, "pick", new { i = own >= 0 ? own : 0 });
            Until(h, Oblup.PhaseGuess);
            h.Act(hero, "place", new { pos = 10 });
            Until(h, Oblup.PhaseReveal);
        }
        Assert.Equal(2, offered);   // обидва свої питання дісталися іншим
    }

    [Fact]
    public void Spicy_questions_come_only_with_the_option()
    {
        var bank = Bank(12, spicy: 6);
        var mild = Table(3, bank: bank);
        Assert.All(PlayOut(mild).Questions, q => Assert.True(int.Parse(q[8..^1]) < 6, q));
        var hot = Table(4, new { spicy = "on", laps = "3" }, bank: bank);
        Assert.Contains(PlayOut(hot).Questions, q => int.Parse(q[8..^1]) >= 6);
    }

    [Fact]
    public void Same_seed_same_game()
    {
        string Trace(int seed)
        {
            var h = Table(4, seed: seed);
            var (heroes, qs) = PlayOut(h);
            return string.Join(",", heroes) + "|" + string.Join(",", qs);
        }
        Assert.Equal(Trace(7), Trace(7));
        Assert.NotEqual(Trace(7), Trace(8));
    }

    [Fact]
    public void Rematch_starts_a_fresh_game()
    {
        var h = Table(2);
        PlayOut(h);
        Assert.True(h.Rematch().Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(Oblup.PhaseBetween, Phase(h));
        Assert.All(Enumerable.Range(0, 2), s => Assert.Equal(0, Score(h, s)));
    }

    /// <summary>Банк, що їде з грою: JSON цілий, id унікальні, усе влазить на картку, і завантажувач нічого не відкинув.</summary>
    [Fact]
    public void Bank_file_is_valid_and_the_game_loads_every_record()
    {
        var path = Paths.Resolve(OblupBank.FileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var raw = doc.RootElement.GetProperty("questions").EnumerateArray().ToList();
        Assert.True(raw.Count >= 300, $"у банку {raw.Count}");
        var bank = OblupBank.Load(path, out var problem);
        Assert.Null(problem);
        Assert.Equal(raw.Count, bank.Count);
        Assert.Equal(bank.Count, bank.Select(q => q.Id).Distinct().Count());
        Assert.Equal(bank.Count, bank.Select(q => q.Q.ToLowerInvariant()).Distinct().Count());
        Assert.True(bank.Count(q => q.Spicy) >= 20);
        Assert.True(bank.Count(q => !q.Spicy) >= 280);
        Assert.All(bank, q => Assert.True(OblupBank.Fits(q.Q, q.L, q.R), q.Id));
        Assert.Equal(bank.Select(q => q.Id), OblupBank.All.Select(q => q.Id));
    }

    [Fact]
    public void Broken_records_are_skipped_not_fatal()
    {
        var list = OblupBank.Parse("""
            { "questions": [
              {"id":"a","q":"Нормальне?","l":"ні","r":"так"},
              {"id":"a","q":"Повтор id","l":"ні","r":"так"},
              {"id":"b","q":"Без полюса","l":"","r":"так"},
              {"id":"c","q":"Задовгий полюс","l":"ні","r":"дуже-дуже-дуже-дуже-дуже-дуже-дуже довгий"},
              7
            ] }
            """, out var problem);
        Assert.Single(list);
        Assert.NotNull(problem);
        Assert.Empty(OblupBank.Parse("{ кривий", out var bad));
        Assert.NotNull(bad);
    }
}

[Collection(SerialPerf.Name)]
public class OblupPerfTests
{
    [Fact]
    [Trait("Category", "Perf")]
    public void A_thousand_ticks_of_a_full_table_are_instant()
    {
        var bank = Enumerable.Range(0, 40).Select(i => new OblupQuestion($"p{i}", $"Питання {i}?", "ліво", "право", [], false)).ToList();
        var h = new RoomHarness("oblup", null, 3, RoomHarness.WithService(new OblupQuestions(bank)));
        foreach (var n in new[] { "a", "b", "c", "d", "e", "f", "g", "h" }) h.Join(n);
        h.Start();
        var sw = CpuWatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            if (h.Room.Status == RoomStatus.Finished) h.Rematch();
            var v = h.View(null);
            var phase = v.GetProperty("phase").GetString();
            var hero = v.GetProperty("hero").GetInt32();
            if (phase == Oblup.PhasePick) h.Act(hero, "pick", new { i = 1 });
            if (phase == Oblup.PhaseGuess)
                for (var s = 0; s < Oblup.MaxSeats; s++) h.Act(s, "place", new { pos = (i * 7 + s * 13) % 101, size = "m" });
            h.Tick(1);
        }
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"1000 тиків зайняли {sw.Elapsed}");
    }
}
