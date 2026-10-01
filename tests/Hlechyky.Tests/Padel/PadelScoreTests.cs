using Hlechyky.Padel;

namespace Hlechyky.Tests.Padel;

/// <summary>
/// Рушій рахунку падела (контракт §2.2): більше-менше / золоте / star, тайбрейк і супертайбрейк, подача й бік, зміна
/// сторін (після непарних геймів кожного сету), points із нічиєю, підказки, факти з журналу, детермінізм.
/// </summary>
public sealed class PadelScoreTests
{
    static readonly PadelRules Def = new();

    /// <summary>Розіграти очки підряд; повертає події кожного очка.</summary>
    static List<List<PadelEvent>> Play(PadelState m, PadelRules r, params int[] pts) => pts.Select(t => PadelScore.Step(m, t, r)).ToList();

    /// <summary>Гейм «під нуль» команді t (з 0:0 у геймі).</summary>
    static List<PadelEvent> Game(PadelState m, PadelRules r, int t)
    {
        List<PadelEvent> last = [];
        for (var i = 0; i < 4; i++) last = PadelScore.Step(m, t, r);
        return last;
    }

    static bool Has(IEnumerable<PadelEvent> ev, string k) => ev.Any(e => e.K == k);

    [Fact]
    public void Advantage_goes_on_until_two_clear()
    {
        var m = PadelScore.Fresh();
        Play(m, Def, 0, 0, 0, 1, 1);
        Assert.Contains(PadelScore.Step(m, 1, Def), e => e.K == "deuce" && e.N == 1 && !e.Decisive);
        Assert.Equal([3, 3], m.Pts);
        // 40:40 — рівно, не вирішальне
        for (var i = 0; i < 5; i++)
        {
            var a = PadelScore.Step(m, 0, Def);
            Assert.Equal("AD", PadelScore.Label(m, Def, 0));
            Assert.Equal("40", PadelScore.Label(m, Def, 1));
            var d = PadelScore.Step(m, 1, Def);
            Assert.Contains(d, e => e.K == "deuce" && !e.Decisive);
        }
        Assert.False(PadelScore.IsDecider(m, Def));
        Play(m, Def, 0, 0);
        Assert.Equal([1, 0], m.Games);
    }

    [Fact]
    public void Golden_point_decides_on_first_deuce()
    {
        var r = Def with { Deuce = "golden" };
        var m = PadelScore.Fresh();
        var ev = Play(m, r, 0, 0, 0, 1, 1, 1);
        Assert.Contains(ev[^1], e => e.K == "deuce" && e.Decisive && e.N == 1);
        Assert.True(PadelScore.IsDecider(m, r));
        Assert.Equal("choice", PadelScore.Side(m, r));
        Assert.Equal(("Золоте очко", -1), PadelScore.Hint(m, r));
        var last = PadelScore.Step(m, 1, r);
        Assert.True(Has(last, "decided"));
        Assert.Contains(last, e => e.K == "game" && e.T == 1 && e.Brk);
        Assert.Equal([0, 1], m.Games);
    }

    [Fact]
    public void Star_point_plays_two_advantages_then_decides()
    {
        var r = Def with { Deuce = "star" };
        var m = PadelScore.Fresh();
        var ev = Play(m, r, 0, 0, 0, 1, 1, 1);
        Assert.Contains(ev[^1], e => e.K == "deuce" && !e.Decisive);
        ev = Play(m, r, 0, 1);
        Assert.Contains(ev[^1], e => e.K == "deuce" && e.N == 2 && !e.Decisive);
        ev = Play(m, r, 1, 0);
        Assert.Contains(ev[^1], e => e.K == "deuce" && e.N == 3 && e.Decisive);
        Assert.Equal(("⭐ Star point", -1), PadelScore.Hint(m, r));
        Assert.True(Has(PadelScore.Step(m, 0, r), "decided"));
        Assert.Equal([1, 0], m.Games);
    }

    [Fact]
    public void Server_rotates_each_game_and_side_follows_points()
    {
        var m = PadelScore.Fresh();
        Assert.Equal("A0", m.ServerSlot);
        Assert.Equal("right", PadelScore.Side(m, Def));
        PadelScore.Step(m, 0, Def);
        Assert.Equal("left", PadelScore.Side(m, Def));
        Game(m, Def, 0);
        Assert.Equal("B0", m.ServerSlot);
        Game(m, Def, 0);
        Assert.Equal("A1", m.ServerSlot);
        Game(m, Def, 0);
        Assert.Equal("B1", m.ServerSlot);
    }

    [Fact]
    public void Serve_change_only_at_game_start()
    {
        var m = PadelScore.Fresh("B1");
        Assert.Equal(["B1", "A0", "B0", "A1"], m.Order);
        PadelScore.Step(m, 0, Def);
        Assert.Equal("Подавача можна поміняти лише на початку гейму", PadelScore.SetServer(m, Def, "A1"));
        var p = Def with { Mode = "points", Total = 24 };
        var q = PadelScore.Fresh();
        Play(q, p, 0, 1, 0);
        Assert.NotNull(PadelScore.SetServer(q, p, "B0"));
        PadelScore.Step(q, 1, p);
        Assert.Null(PadelScore.SetServer(q, p, "B0"));
        Assert.Equal("B0", q.ServerSlot);
    }

    [Fact]
    public void Tiebreak_7_0_and_next_set_serve()
    {
        var r = Def with { Sets = "3" };
        var m = PadelScore.Fresh();
        List<PadelEvent> ev = [];
        for (var g = 0; g < 12; g++) ev = Game(m, r, g % 2);
        Assert.True(Has(ev, "tb"));
        Assert.False(Has(ev, "ends")); // 6:6 — парно
        Assert.True(m.Tb);
        Assert.Equal(0, m.TbSrv0);
        Assert.Equal(("Тайбрейк", -1), PadelScore.Hint(m, r));
        var slots = new List<string>();
        for (var i = 0; i < 7; i++) { slots.Add(m.ServerSlot); ev = PadelScore.Step(m, 0, r); }
        // Подача: перше очко — один, далі по двоє
        Assert.Equal(["A0", "B0", "B0", "A1", "A1", "B1", "B1"], slots);
        Assert.True(Has(ev, "set"));
        Assert.True(Has(ev, "ends")); // 7:6 — непарно
        Assert.Equal([7, 6], m.Sets[0].G);
        Assert.Equal([7, 0], m.Sets[0].Tb!);
        // Наступний сет — той, хто був наступним після першого подавача тайбрейку
        Assert.Equal("B0", m.ServerSlot);
    }

    [Fact]
    public void Tiebreak_changes_ends_every_six_points()
    {
        var m = PadelScore.Fresh();
        for (var g = 0; g < 12; g++) Game(m, Def, g % 2);
        var ends = new List<int>();
        int[] seq = [0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 0];
        for (var i = 0; i < seq.Length; i++) if (Has(PadelScore.Step(m, seq[i], Def), "ends")) ends.Add(i + 1);
        Assert.Equal([6, 12], ends);
        Assert.True(m.Over);
        Assert.Equal([8, 6], m.Sets[0].Tb!);
    }

    [Fact]
    public void Ends_change_after_odd_games_of_every_set()
    {
        var r = Def with { Sets = "3" };
        var m = PadelScore.Fresh();
        int[] games = [0, 1, 0, 1, 0, 1, 0, 0, 0];
        var ends = new List<int>();
        for (var i = 0; i < games.Length; i++) if (Has(Game(m, r, games[i]), "ends")) ends.Add(i + 1);
        Assert.Equal([1, 3, 5, 7, 9], ends);
        Assert.Equal([6, 3], m.Sets[0].G);
        // Новий сет: після 1-го гейму — знову зміна
        Assert.True(Has(Game(m, r, 1), "ends"));
        Assert.False(Has(Game(m, r, 1), "ends"));
    }

    [Fact]
    public void Super_tiebreak_at_one_set_all()
    {
        var r = Def with { Sets = "3s" };
        var m = PadelScore.Fresh();
        for (var g = 0; g < 6; g++) Game(m, r, 0);
        List<PadelEvent> ev = [];
        for (var g = 0; g < 6; g++) ev = Game(m, r, 1);
        Assert.True(Has(ev, "stb"));
        Assert.True(m.SuperTb);
        Assert.Equal(10, m.TbTo);
        Assert.Equal(("Супертайбрейк", -1), PadelScore.Hint(m, r));
        for (var i = 0; i < 9; i++) PadelScore.Step(m, 0, r);
        Assert.Equal(("Матч-бол", 0), PadelScore.Hint(m, r));
        ev = PadelScore.Step(m, 0, r);
        Assert.Contains(ev, e => e.K == "match" && e.T == 0);
        Assert.True(m.Sets[2].Stb);
        Assert.Equal([10, 0], m.Sets[2].G);
    }

    [Fact]
    public void Free_sets_never_end_until_finish()
    {
        var r = Def with { Sets = "free", SetTo = 4 };
        var m = PadelScore.Fresh();
        for (var g = 0; g < 12; g++) Game(m, r, 0);
        Assert.False(m.Over);
        Assert.Equal(3, m.Won[0]);
        Game(m, r, 1);
        PadelScore.Finish(m, r);
        Assert.True(m.Over);
        Assert.Equal(0, m.Winner);
        Assert.Equal([0, 1], m.Sets[^1].G);
    }

    [Fact]
    public void Points_24_with_draw_and_serve_every_four()
    {
        var r = new PadelRules("points", Total: 24);
        var m = PadelScore.Fresh();
        var serves = new List<int>();
        for (var i = 0; i < 23; i++) if (Has(PadelScore.Step(m, i % 2, r), "serve")) serves.Add(i + 1);
        Assert.Equal([4, 8, 12, 16, 20], serves);
        Assert.Equal(("Останній розіграш", -1), PadelScore.Hint(m, r));
        var ev = PadelScore.Step(m, 1, r);
        Assert.Contains(ev, e => e.K == "match" && e.T == -1);
        Assert.Equal(-1, m.Winner);
        Assert.Equal("12:12", PadelScore.ScoreText(m, r));
        // На час — кінця за очками нема
        var t = new PadelRules("points");
        var q = PadelScore.Fresh();
        for (var i = 0; i < 60; i++) PadelScore.Step(q, 0, t);
        Assert.False(q.Over);
    }

    [Fact]
    public void Hints_match_set_and_break_point()
    {
        var m = PadelScore.Fresh();
        for (var g = 0; g < 5; g++) Game(m, Def, 0);
        Play(m, Def, 0, 0, 0);
        Assert.Equal(("Матч-бол", 0), PadelScore.Hint(m, Def));
        var r3 = Def with { Sets = "3" };
        var s = PadelScore.Fresh();
        for (var g = 0; g < 5; g++) Game(s, r3, 0);
        Play(s, r3, 0, 0, 0);
        Assert.Equal(("Сет-бол", 0), PadelScore.Hint(s, r3));
        var b = PadelScore.Fresh();
        Play(b, Def, 1, 1, 1);
        Assert.Equal(("Брейк-пойнт", 1), PadelScore.Hint(b, Def));
        Assert.Null(PadelScore.Hint(PadelScore.Fresh(), Def));
        Assert.Equal("0:0 · 0:40", PadelScore.ScoreText(b, Def));
    }

    [Fact]
    public void Speak_reads_from_server_side()
    {
        var m = PadelScore.Fresh();
        string Team(int t) => t == 0 ? "Влад і Микола" : "Smaug і Андрій";
        var ev = PadelScore.Step(m, 1, Def);
        Assert.Equal("нуль — п’ятнадцять", PadelScore.Speak(m, Def, ev, Team));
        Play(m, Def, 1, 0, 0, 0);
        ev = PadelScore.Step(m, 1, Def);
        Assert.Equal("Рівно", PadelScore.Speak(m, Def, ev, Team));
        ev = PadelScore.Step(m, 1, Def);
        Assert.Equal("Перевага: Smaug і Андрій", PadelScore.Speak(m, Def, ev, Team));
        ev = PadelScore.Step(m, 1, Def);
        Assert.Equal("Гейм, Smaug і Андрій. 0 — 1. Зміна сторін", PadelScore.Speak(m, Def, ev, Team));
        Assert.Contains("тридцять — п’ятнадцять", PadelScore.CommonPhrases());
    }

    [Fact]
    public void Facts_from_journal_bagel_comeback_golden_tiebreaks()
    {
        var r = new PadelRules("match", "golden", "3s", 6);
        var j = new List<string>();
        void G(int t, int n = 1) { for (var k = 0; k < n; k++) for (var i = 0; i < 4; i++) j.Add(t.ToString()); }
        // Сет 1: A після 1:5 — 7:5 (камбек), з одним золотим очком у першому гейму
        j.AddRange(["0", "0", "0", "1", "1", "1", "0"]);
        G(1, 5); G(0, 6);
        // Сет 2: бублик B
        G(1, 6);
        // Супертайбрейк: B 10:3
        for (var i = 0; i < 3; i++) j.Add("0");
        for (var i = 0; i < 10; i++) j.Add("1");
        var rp = PadelScore.Replay(r, PadelScore.Slots, j);
        Assert.True(rp.State.Over);
        Assert.Equal(1, rp.State.Winner);
        Assert.Equal([1, 0], rp.Facts.GoldenWon);
        Assert.Equal([0, 1], rp.Facts.TieBreaksWon);
        Assert.Equal([false, true], rp.Facts.Bagel);
        Assert.Equal([true, false], rp.Facts.Comeback);
        Assert.Equal([7, 5], rp.State.Sets[0].G);
        Assert.Equal(1, rp.Stats.Golden[0]);
        Assert.Equal(j.Count, rp.Stats.Momentum.Length);
        Assert.Equal(rp.Stats.Serve[0].Of + rp.Stats.Serve[1].Of, j.Count);
        Assert.True(rp.Stats.Streak[1] >= 24);
    }

    [Fact]
    public void Replay_is_deterministic_and_matches_stepping()
    {
        var r = new PadelRules(Sets: "3", Deuce: "star");
        var rnd = new Random(7);
        var j = Enumerable.Range(0, 300).Select(_ => rnd.Next(2).ToString()).ToList();
        j.Insert(0, "s:B1");
        var a = PadelScore.Replay(r, PadelScore.Slots, j);
        var b = PadelScore.Replay(r, PadelScore.Slots, j);
        Assert.Equal(PadelScore.ScoreText(a.State, r), PadelScore.ScoreText(b.State, r));
        Assert.Equal(a.State.Log, b.State.Log);
        var m = PadelScore.Fresh("B1");
        foreach (var e in j.Skip(1)) PadelScore.Step(m, e[0] - '0', r);
        Assert.Equal(PadelScore.ScoreText(m, r), PadelScore.ScoreText(a.State, r));
        Assert.Equal(m.Order, a.State.Order);
        Assert.Equal(m.Srv, a.State.Srv);
        // Ручне завершення — у журналі, і скасовується разом із ним
        var f = PadelScore.Replay(new PadelRules(Sets: "free"), PadelScore.Slots, ["0", "0", "0", "0", "f"]);
        Assert.True(f.State.Over);
        Assert.Equal(0, f.State.Winner);
        Assert.Equal(PadelScore.VoiceId("Рівно"), PadelScore.VoiceId(" Рівно "));
        Assert.Equal(16, PadelScore.VoiceId("Рівно").Length);
    }
}
