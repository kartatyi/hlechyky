using System.Text.Json;
using Hlechyky.Padel;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Padel;

/// <summary>
/// Турніри Падельні (контракт §2.4): розклади шести форматів, план раундів, таблиця (sum/avg), права на рахунок,
/// живий матч → рахунок у турнір, плей-оф без нічиїх, підсумки з нагородами, історія.
/// </summary>
public sealed class PadelTourTests : IDisposable
{
    readonly PadelPlayTests.Rig _r = new();
    public void Dispose() => _r.Dispose();

    static string U(string nick) => PadelPlayTests.Rig.U(nick);
    static string[] Ps(int n) => [.. PadelPlayTests.Rig.Nicks.Take(n).Select(U)];
    static PadelWho Who(string nick, bool admin = false) => PadelPlayTests.Rig.Who(nick, admin);

    static JsonElement Body(PadelReply r)
    {
        Assert.True(r.Error is null, r.Error);
        return Views.Json(r.Body);
    }

    JsonElement Create(string format, string[]? players = null, string[][]? pairs = null, string[]? women = null, int courts = 2,
        object? total = null, int? rounds = null, string by = "Влад", int? minutes = null)
    {
        var t = total is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(total);
        return Body(_r.Tours.Create(Who(by), new PadelTourRequest(format, null, players, pairs, women, courts, t, minutes, rounds, 2, null)))
            .GetProperty("tournament");
    }

    JsonElement Score(string id, int round, int court, int? a, int? b, string by = "Влад") =>
        Body(_r.Tours.Score(id, Who(by), new PadelScoreRequest(round, court, a, b))).GetProperty("tournament");

    static string[] Side(JsonElement m, string k) => [.. m.GetProperty(k).EnumerateArray().Select(x => x.GetString()!)];

    static int PartnerRepeats(IEnumerable<PadelRound> rs) => rs.SelectMany(r => r.Matches.SelectMany(m => new[] { m.A, m.B }))
        .GroupBy(p => string.Join("|", p.Order(StringComparer.Ordinal))).Sum(g => g.Count() - 1);

    // ============================================================ розклади

    [Theory]
    [InlineData(8, 7)]
    [InlineData(9, 9)]
    [InlineData(10, 5)]
    public void Americano_no_repeated_partners_and_equal_rests(int n, int rounds)
    {
        var ids = Ps(n);
        foreach (var seed in new[] { 1, 2, 3 })
        {
            var rs = PadelTourGen.Americano(ids, 2, rounds, new Random(seed));
            Assert.Equal(rounds, rs.Count);
            Assert.Equal(0, PartnerRepeats(rs));
            Assert.Single(ids.Select(i => rs.Count(r => r.Sit.Contains(i))).Distinct());
            Assert.All(rs, r => Assert.Equal(n, r.Sit.Count + r.Matches.Sum(m => 4)));
        }
        // Детермінізм: той самий сід — той самий розклад
        var a = PadelTourGen.Americano(ids, 2, rounds, new Random(5));
        var b = PadelTourGen.Americano(ids, 2, rounds, new Random(5));
        Assert.Equal(Views.Text(a), Views.Text(b));
    }

    [Fact]
    public void Mexicano_next_round_is_one_three_against_two_four()
    {
        var t = Create("mexicano", Ps(8), rounds: 3);
        var id = t.GetProperty("id").GetString()!;
        Assert.False(t.GetProperty("rounds")[1].GetProperty("ready").GetBoolean());
        Assert.Equal(0, t.GetProperty("rounds")[1].GetProperty("matches").GetArrayLength());
        Score(id, 1, 1, 15, 9);
        t = Score(id, 1, 2, 13, 11);
        var order = t.GetProperty("table").EnumerateArray().Select(r => r.GetProperty("unit")[0].GetString()!).ToList();
        var r2 = t.GetProperty("rounds")[1];
        Assert.True(r2.GetProperty("ready").GetBoolean());
        var c1 = r2.GetProperty("matches")[0];
        Assert.Equal([order[0], order[2]], Side(c1, "a"));
        Assert.Equal([order[1], order[3]], Side(c1, "b"));
        Assert.Equal([order[4], order[6]], Side(r2.GetProperty("matches")[1], "a"));
        // Правка старого раунду наступного не переробляє
        t = Score(id, 1, 1, 9, 15);
        Assert.Equal([order[0], order[2]], Side(t.GetProperty("rounds")[1].GetProperty("matches")[0], "a"));
    }

    [Fact]
    public void Mixed_pairs_are_woman_and_man_and_extra_men_rest_in_turn()
    {
        string[] women = [U("Оля"), U("Таня"), U("Ірина"), U("Іра")];
        string[] men = [U("Влад"), U("Микола"), U("Smaug"), U("Андрій"), U("Діма")];
        var t = Create("mixed", [.. women, .. men], women: women, rounds: 5);
        var rounds = t.GetProperty("rounds").EnumerateArray().ToList();
        Assert.Equal(5, rounds.Count);
        foreach (var m in rounds.SelectMany(r => r.GetProperty("matches").EnumerateArray()))
            foreach (var side in new[] { Side(m, "a"), Side(m, "b") })
                Assert.Equal(1, side.Count(women.Contains));
        Assert.All(men, x => Assert.Equal(1, rounds.Count(r => r.GetProperty("sit").EnumerateArray().Any(s => s.GetString() == x))));
        Assert.NotNull(_r.Tours.Create(Who("Влад"), new("mixed", null, Ps(6), null, [U("Оля")], 1, null, null, 2, 2, null)).Error);
    }

    [Fact]
    public void King_moves_winners_up_and_losers_down_and_mixes_pairs()
    {
        var t = Create("king", Ps(8), rounds: 3);
        var id = t.GetProperty("id").GetString()!;
        var r1 = t.GetProperty("rounds")[0].GetProperty("matches");
        string[] c1a = Side(r1[0], "a"), c1b = Side(r1[0], "b"), c2a = Side(r1[1], "a"), c2b = Side(r1[1], "b");
        Score(id, 1, 1, 15, 9);
        t = Score(id, 1, 2, 10, 14);
        var r2 = t.GetProperty("rounds")[1].GetProperty("matches");
        var top = Side(r2[0], "a").Concat(Side(r2[0], "b")).ToHashSet();
        Assert.True(top.SetEquals(c1a.Concat(c2b)));
        var bottom = Side(r2[1], "a").Concat(Side(r2[1], "b")).ToHashSet();
        Assert.True(bottom.SetEquals(c1b.Concat(c2a)));
        foreach (var side in new[] { Side(r2[0], "a"), Side(r2[0], "b") })
        {
            Assert.Single(side, c1a.Contains);
            Assert.Single(side, c2b.Contains);
        }
    }

    [Fact]
    public void Team_round_robin_without_repeats()
    {
        var all = Ps(10);
        string[][] pairs = [.. Enumerable.Range(0, 5).Select(i => new[] { all[2 * i], all[2 * i + 1] })];
        var plan = PadelTourGen.Plan("team", 5, 2, "24", null, 5);
        Assert.Equal(5, plan.Full);
        var t = Create("team", pairs: pairs, rounds: plan.Full);
        var ms = t.GetProperty("rounds").EnumerateArray().SelectMany(r => r.GetProperty("matches").EnumerateArray())
            .Select(m => string.Join("~", new[] { Side(m, "a")[0], Side(m, "b")[0] }.Order(StringComparer.Ordinal))).ToList();
        Assert.Equal(10, ms.Count);
        Assert.Equal(10, ms.Distinct().Count());
        Assert.Equal("sum", t.GetProperty("rankBy").GetString());
        Assert.Equal(2, t.GetProperty("table")[0].GetProperty("unit").GetArrayLength());
    }

    [Fact]
    public void Groups_then_playoff_without_draws_and_podium_from_final()
    {
        var all = Ps(12);
        string[][] pairs = [.. Enumerable.Range(0, 6).Select(i => new[] { all[2 * i], all[2 * i + 1] })];
        var t = Create("groups", pairs: pairs);
        var id = t.GetProperty("id").GetString()!;
        Assert.Equal("wins", t.GetProperty("rankBy").GetString());
        int Idx(string[] side) => Array.FindIndex(pairs, p => p[0] == side[0]);
        var groupOf = new Dictionary<int, string>();
        foreach (var r in t.GetProperty("rounds").EnumerateArray())
        {
            Assert.Equal("group", r.GetProperty("stage").GetString());
            foreach (var m in r.GetProperty("matches").EnumerateArray())
            {
                int a = Idx(Side(m, "a")), b = Idx(Side(m, "b"));
                groupOf[a] = groupOf[b] = m.GetProperty("group").GetString()!;
                // Нижчий номер пари виграє
                t = Score(id, r.GetProperty("n").GetInt32(), m.GetProperty("court").GetInt32(), a < b ? 16 : 8, a < b ? 8 : 16);
            }
        }
        Assert.Equal(["A", "B", "B", "A", "A", "B"], Enumerable.Range(0, 6).Select(i => groupOf[i]));
        var semi = t.GetProperty("rounds").EnumerateArray().Single(r => r.GetProperty("stage").GetString() == "semi");
        var sm = semi.GetProperty("matches");
        Assert.Equal((0, 2), (Idx(Side(sm[0], "a")), Idx(Side(sm[0], "b"))));
        Assert.Equal((1, 3), (Idx(Side(sm[1], "a")), Idx(Side(sm[1], "b"))));
        var n = semi.GetProperty("n").GetInt32();
        Assert.Equal("У плей-оф нічиєї нема", _r.Tours.Score(id, Who("Влад"), new(n, 1, 12, 12)).Error);
        Score(id, n, 1, 14, 10);
        t = Score(id, n, 2, 14, 10);
        var fin = t.GetProperty("rounds").EnumerateArray().Last();
        Assert.Equal("final", fin.GetProperty("stage").GetString());
        Assert.Equal("third", fin.GetProperty("matches")[1].GetProperty("stage").GetString());
        Score(id, n + 1, 1, 15, 9);
        Score(id, n + 1, 2, 9, 15);
        Assert.Equal("У групах раунди складає сітка", _r.Tours.AddRounds(id, Who("Влад"), new(1)).Error);
        t = Body(_r.Tours.Finish(id, Who("Влад"))).GetProperty("tournament");
        var podium = t.GetProperty("final").GetProperty("podium").EnumerateArray().Select(u => Idx([u[0].GetString()!])).ToList();
        Assert.Equal([0, 1, 3], podium);
    }

    [Fact]
    public void Plan_recommends_fair_rounds_or_average()
    {
        var p = PadelTourGen.Plan("americano", 10, 2, "24", null, 2);
        Assert.Equal((8, 2, 14, 8, 12, 5, 5, false), (p.Slots, p.Sit, p.PerRound, p.Fit, p.Full, p.Fair, p.Rec, p.Avg));
        p = PadelTourGen.Plan("americano", 9, 2, "24", null, 1.5);
        Assert.Equal((6, 9, 6, true), (p.Fit, p.Fair, p.Rec, p.Avg));
        Assert.Equal("Порівну відпочити не вийде — таблиця рахуватиме середнє за матч", p.Note);
        p = PadelTourGen.Plan("americano", 8, 2, "24", null, 3);
        Assert.Equal((1, 7), (p.Fair, p.Rec));
        Assert.Equal(17, PadelTourGen.Plan("mexicano", 8, 2, "time", 15, 2).PerRound);
        Assert.Equal("Невідомий формат", PadelTours.PlanFor("bingo", 8, 2, "24", null, 2).Error);
    }

    [Fact]
    public void Rank_by_average_when_rests_are_unequal()
    {
        Assert.Equal("avg", Create("americano", Ps(9), rounds: 4).GetProperty("rankBy").GetString());
        Assert.Equal("sum", Create("americano", Ps(9), rounds: 9).GetProperty("rankBy").GetString());
        Assert.Equal("sum", Create("americano", Ps(8), rounds: 3).GetProperty("rankBy").GetString());
    }

    // ============================================================ рахунок, живий матч, підсумки

    [Fact]
    public void Score_rights_and_totals()
    {
        var t = Create("americano", Ps(8), rounds: 2);
        var id = t.GetProperty("id").GetString()!;
        var m1 = t.GetProperty("rounds")[0].GetProperty("matches")[0];
        var m2 = t.GetProperty("rounds")[0].GetProperty("matches")[1];
        var stranger = _r.Players.Name(Side(m2, "a").First(p => p != U("Влад")));
        var player = _r.Players.Name(Side(m1, "a").First(p => p != U("Влад")));
        Assert.Equal("Рахунок вносять гравці цього корту", _r.Tours.Score(id, Who(stranger), new(1, 1, 15, 9)).Error);
        Assert.Equal(403, _r.Tours.Score(id, new PadelWho(null, "гість", false), new(1, 1, 15, 9)).Status);
        Assert.Equal("Разом має бути 24", _r.Tours.Score(id, Who(player), new(1, 1, 15, 8)).Error);
        t = Score(id, 1, 1, 15, 9, by: player);
        Assert.Equal(player, t.GetProperty("rounds")[0].GetProperty("matches")[0].GetProperty("by").GetString());
        Score(id, 1, 1, 14, 10, by: "Влад");
        t = Score(id, 1, 1, null, null);
        Assert.Equal(JsonValueKind.Null, t.GetProperty("rounds")[0].GetProperty("matches")[0].GetProperty("sa").ValueKind);
        Assert.Equal("Ще нема жодного рахунку", _r.Tours.Finish(id, Who("Влад")).Error);
        // Видалити: організатор — поки нема рахунків
        Score(id, 1, 2, 12, 12);
        Assert.NotNull(_r.Tours.Delete(id, Who("Влад")).Error);
        Assert.Null(_r.Tours.Delete(id, Who("Оля", admin: true)).Error);
        Assert.Null(_r.Tours.View(id));
    }

    [Fact]
    public void Live_match_writes_score_into_tournament()
    {
        var t = Create("americano", Ps(8), rounds: 2, total: "16");
        var id = t.GetProperty("id").GetString()!;
        var m1 = t.GetProperty("rounds")[0].GetProperty("matches")[0];
        var player = _r.Players.Name(Side(m1, "b")[0]);
        var live = Body(_r.Tours.Live(id, Who(player), new(1, 1))).GetProperty("match");
        Assert.Equal("points", live.GetProperty("rules").GetProperty("mode").GetString());
        Assert.Equal(16, live.GetProperty("rules").GetProperty("total").GetInt32());
        Assert.Equal(id, live.GetProperty("tour").GetProperty("id").GetString());
        var mid = live.GetProperty("id").GetString()!;
        Assert.Equal(mid, Body(_r.Tours.Live(id, Who(player), new(1, 1))).GetProperty("match").GetProperty("id").GetString());
        Assert.Equal(mid, Views.Json(_r.Tours.View(id)).GetProperty("rounds")[0].GetProperty("matches")[0].GetProperty("live").GetString());
        for (var i = 0; i < 16; i++) _r.Matches.Act(mid, Who(player), new("point", i < 10 ? 1 : 0, null, null));
        var tm = Views.Json(_r.Tours.View(id)).GetProperty("rounds")[0].GetProperty("matches")[0];
        Assert.Equal((6, 10), (tm.GetProperty("sa").GetInt32(), tm.GetProperty("sb").GetInt32()));
        Assert.Equal(player, tm.GetProperty("by").GetString());
        Assert.Equal(JsonValueKind.Null, tm.GetProperty("live").ValueKind);
        Assert.Empty(_r.W.Chats);
        var res = new PadelHistory(_r.Matches, _r.Tours, _r.Players).Results();
        var one = Assert.Single(res);
        Assert.Equal("tour", one.Source);
        Assert.Equal([6, 10], one.Points);
        Assert.Equal(1, one.Winner);
    }

    [Fact]
    public void Finish_gives_podium_awards_chat_and_history()
    {
        var t = Create("americano", Ps(8), rounds: 3);
        var id = t.GetProperty("id").GetString()!;
        Assert.Equal("Американо 10 вересня", t.GetProperty("title").GetString());
        var scores = new[] { (20, 4), (13, 11), (12, 12), (16, 8), (18, 6), (11, 13) };
        var k = 0;
        foreach (var r in t.GetProperty("rounds").EnumerateArray())
            foreach (var m in r.GetProperty("matches").EnumerateArray())
            {
                var (a, b) = scores[k++ % scores.Length];
                Score(id, r.GetProperty("n").GetInt32(), m.GetProperty("court").GetInt32(), a, b);
            }
        Assert.Equal(403, _r.Tours.Finish(id, Who("Оля")).Status);
        t = Body(_r.Tours.Finish(id, Who("Влад"))).GetProperty("tournament");
        Assert.Equal("done", t.GetProperty("status").GetString());
        var fin = t.GetProperty("final");
        Assert.Equal(3, fin.GetProperty("podium").GetArrayLength());
        var keys = fin.GetProperty("awards").EnumerateArray().Select(a => a.GetProperty("key").GetString()).ToList();
        Assert.Equal(["pair", "rout", "close", "wall", "steady"], keys);
        var rout = fin.GetProperty("awards")[1];
        Assert.StartsWith("20:4 — ", rout.GetProperty("text").GetString());
        Assert.Equal(2, rout.GetProperty("pids").GetArrayLength());
        var chat = Assert.Single(_r.W.Chats).GetProperty("text").GetString()!;
        Assert.StartsWith("🍳 Падельня: «Американо 10 вересня» — 🥇 ", chat);
        Assert.Contains("🥉", chat);
        var h = new PadelHistory(_r.Matches, _r.Tours, _r.Players);
        Assert.Equal(6, h.Results().Count);
        var tr = Assert.Single(h.Tournaments());
        Assert.Equal(8, tr.Ranked.Length);
        Assert.Equal(["pair", "rout", "close", "wall", "steady"], tr.Awards.Keys.ToList());
        var list = Views.Json(_r.Tours.List());
        Assert.Equal(1, list.GetProperty("recent").GetArrayLength());
        Assert.Equal(8, list.GetProperty("recent")[0].GetProperty("players").GetInt32());
    }

    [Fact]
    public void Organizer_adds_rounds_and_lobby_shows_progress()
    {
        var t = Create("americano", Ps(8), rounds: 2);
        var id = t.GetProperty("id").GetString()!;
        Assert.Equal(403, _r.Tours.AddRounds(id, Who("Оля"), new(1)).Status);
        t = Body(_r.Tours.AddRounds(id, Who("Влад"), new(1))).GetProperty("tournament");
        Assert.Equal(3, t.GetProperty("rounds").GetArrayLength());
        Assert.True(t.GetProperty("rounds")[2].GetProperty("ready").GetBoolean());
        var lobby = Views.Json(new PadelLobby(_r.Matches, _r.Tours, _r.Players, new NoPadelAgenda()).Summary());
        var tour = Assert.Single(lobby.GetProperty("tours").EnumerateArray());
        Assert.Equal((1, 3), (tour.GetProperty("round").GetInt32(), tour.GetProperty("of").GetInt32()));
        // Перезапуск: турнір на місці
        _r.Reopen();
        Assert.Equal(3, Views.Json(_r.Tours.View(id)).GetProperty("rounds").GetArrayLength());
    }
}
