using System.Text.Json;
using Hlechyky.Padel;
using Hlechyky.Tests.Support;
using static Hlechyky.Tests.Padel.PadelMoneyRig;

namespace Hlechyky.Tests.Padel;

/// <summary>Рейтинг Ело, статистика, хімія пар і відзнаки (контракт §3.4) над підкладеною історією.</summary>
public sealed class PadelRatingTests : IDisposable
{
    readonly PadelMoneyRig _r = new();
    public void Dispose() => _r.Dispose();

    static readonly string[] AB = ["u:а", "u:б"], VG = ["u:в", "u:г"];

    PadelRatingBook Book() { _r.Clock.Advance(11); return _r.Rating.Book(); }
    int Elo(string pid) => (int)Math.Round(Book().Stats[pid].R);
    string[] Badges(string pid) => [.. (Book().Badges.GetValueOrDefault(pid)?.Keys.AsEnumerable() ?? []).Order(StringComparer.Ordinal)];
    JsonElement Profile(string pid) { _r.Clock.Advance(11); var (s, b) = R(_r.Rating.Profile(pid)); Assert.Equal(200, s); return b; }

    // ---------------------------------------------------------------- Ело

    [Fact]
    public void Equal_opponents_move_by_half_k()
    {
        _r.Result(AB, VG, 0);
        Assert.Equal([1220, 1220, 1180, 1180], new[] { "u:а", "u:б", "u:в", "u:г" }.Select(Elo));
        Assert.Equal(0.5, PadelRating.Expected(1300, 1300));
    }

    [Fact]
    public void Points_match_scores_by_the_share_of_points()
    {
        _r.Result(AB, VG, 0, points: [15, 9]);                  // 15/24 = 0,625 → 40·0,125 = +5
        Assert.Equal(1205, Elo("u:а"));
        Assert.Equal(1195, Elo("u:г"));
        Assert.Equal(0.5, PadelRating.Score(new PadelResult("x", default, "tour", null, "points", [AB, VG], [], [0, 0], -1, PadelFacts.None)));
    }

    [Fact]
    public void K_drops_to_24_after_ten_results()
    {
        for (var i = 0; i < 10; i++) _r.Result(AB, VG, -1);     // нічиї рівних — рейтинг стоїть
        Assert.Equal(1200, Elo("u:а"));
        _r.Result(AB, VG, 0);
        Assert.Equal(1212, Elo("u:а"));
        Assert.Equal(1188, Elo("u:в"));
    }

    [Fact]
    public void Team_rating_is_the_average_of_the_pair()
    {
        _r.Result(["u:а", "u:х"], ["u:б", "u:ч"], 0);           // а 1220, б 1180
        _r.Result(["u:а", "u:б"], ["u:в", "u:г"], 0);           // пара 1200 проти 1200 — знову ±20
        Assert.Equal(1240, Elo("u:а"));
        Assert.Equal(1200, Elo("u:б"));
    }

    [Fact]
    public void A_linked_guest_plays_as_the_account()
    {
        _r.Result(["g:1", "u:б"], VG, 0);
        _r.Who.Links["g:1"] = "u:петро";
        Assert.Equal(1220, Elo("u:петро"));
        Assert.False(Book().Stats.ContainsKey("g:1"));
    }

    // ---------------------------------------------------------------- таблиця, профіль, хімія

    [Fact]
    public void Rating_rows_provisional_and_titles()
    {
        for (var i = 0; i < 5; i++) _r.Result(AB, VG, 1);
        _r.Result(["u:а", "u:д"], ["u:б", "u:е"], 0);
        _r.Clock.Advance(11);
        var v = Views.Json(_r.Rating.Rating());
        var rows = v.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(["u:в", "u:г", "u:а", "u:б"], rows.Select(x => x.GetProperty("pid").GetString()));
        Assert.Equal("👑 Король Падельні · 🍳 Сковорідка тижня", rows[0].GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("title").ValueKind);
        Assert.Equal(5, rows[0].GetProperty("wins").GetInt32());
        Assert.Equal(5, rows[0].GetProperty("played").GetInt32());
        Assert.Equal(rows[0].GetProperty("rating").GetInt32() - 1200, rows[0].GetProperty("delta7").GetInt32());
        Assert.Equal(["u:д", "u:е"], v.GetProperty("provisional").EnumerateArray().Select(x => x.GetProperty("pid").GetString()).Order());
    }

    [Fact]
    public void Profile_partners_rivals_nemesis_streak_and_recent()
    {
        for (var i = 0; i < 3; i++) _r.Result(AB, VG, 1, sets: [[4, 6], [6, 7], [8, 10]]);
        _r.Result(["u:а", "u:в"], ["u:б", "u:г"], 0, points: [13, 11]);
        var p = Profile("u:а");
        Assert.Equal("u:а", p.GetProperty("player").GetProperty("pid").GetString());
        Assert.Equal(4, p.GetProperty("played").GetInt32());
        Assert.Equal(1, p.GetProperty("wins").GetInt32());
        Assert.Equal(3, p.GetProperty("losses").GetInt32());
        Assert.Equal(25, p.GetProperty("winPct").GetInt32());
        Assert.Equal(3 * (4 + 6 + 0) + 13, p.GetProperty("pointsFor").GetInt32());     // супертайбрейк — один гейм
        Assert.Equal(3 * (6 + 7 + 1) + 11, p.GetProperty("pointsAgainst").GetInt32());
        Assert.Equal("w", p.GetProperty("streak").GetProperty("kind").GetString());
        Assert.Equal(1, p.GetProperty("streak").GetProperty("n").GetInt32());
        var best = p.GetProperty("best");
        Assert.Equal("u:б", best.GetProperty("partner").GetProperty("pid").GetString());
        Assert.Equal(0, best.GetProperty("partner").GetProperty("pct").GetInt32());
        Assert.Equal("u:г", best.GetProperty("rival").GetProperty("pid").GetString());     // 4 рази проти
        Assert.Equal("u:в", best.GetProperty("nemesis").GetProperty("pid").GetString());   // 3 поразки, менше ігор
        var recent = p.GetProperty("recent").EnumerateArray().ToList();
        Assert.Equal(4, recent.Count);
        Assert.Equal("13:11", recent[0].GetProperty("score").GetString());
        Assert.True(recent[0].GetProperty("won").GetBoolean());
        Assert.Equal("4:6 · 6:7 · 8:10", recent[1].GetProperty("score").GetString());
        Assert.Equal("u:в", recent[0].GetProperty("teams")[0][1].GetProperty("pid").GetString());
        Assert.Equal(4, p.GetProperty("ratingHistory").GetArrayLength());
        Assert.Equal(JsonValueKind.Number, p.GetProperty("rank").ValueKind);

        var v = Profile("u:в");
        Assert.Equal("w", v.GetProperty("streak").GetProperty("kind").GetString());
        Assert.Equal(4, v.GetProperty("streak").GetProperty("n").GetInt32());
        var b = Profile("u:б");
        Assert.Equal("l", b.GetProperty("streak").GetProperty("kind").GetString());
        Assert.Equal(4, b.GetProperty("streak").GetProperty("n").GetInt32());

        var chem = Views.Json(_r.Rating.Chemistry()).GetProperty("pairs").EnumerateArray().ToList();
        Assert.Equal(2, chem.Count);
        Assert.Equal(["u:в", "u:г"], new[] { chem[0].GetProperty("a").GetString(), chem[0].GetProperty("b").GetString() });
        Assert.Equal(100, chem[0].GetProperty("pct").GetInt32());
        Assert.Equal(0, chem[1].GetProperty("pct").GetInt32());
    }

    [Fact]
    public void Profile_with_no_history_is_all_zeros_and_unknown_is_404()
    {
        var p = Profile("u:новенький");
        Assert.Equal(1200, p.GetProperty("rating").GetInt32());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("rank").ValueKind);
        Assert.Equal(0, p.GetProperty("played").GetInt32());
        Assert.Equal(0, p.GetProperty("winPct").GetInt32());
        Assert.Equal(0, p.GetProperty("streak").GetProperty("n").GetInt32());
        Assert.Empty(p.GetProperty("partners").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("best").GetProperty("partner").ValueKind);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("best").GetProperty("nemesis").ValueKind);
        Assert.Empty(p.GetProperty("badges").EnumerateArray());
        Assert.Empty(p.GetProperty("recent").EnumerateArray());
        Assert.Empty(p.GetProperty("ratingHistory").EnumerateArray());
        Assert.Equal(404, R(_r.Rating.Profile("хтось")).Status);
        Assert.Equal(404, R(_r.Rating.Profile("g:99")).Status);
        var empty = Views.Json(_r.Rating.Rating());
        Assert.Empty(empty.GetProperty("rows").EnumerateArray());
        Assert.Empty(Views.Json(_r.Rating.Chemistry()).GetProperty("pairs").EnumerateArray());
    }

    // ---------------------------------------------------------------- відзнаки

    [Fact]
    public void Badges_from_match_facts()
    {
        _r.Result(AB, VG, 0, facts: new PadelFacts([3, 2], [1, 0], [true, false], [false, true]));
        Assert.Equal(["bagel", "first", "golden"], Badges("u:а"));
        Assert.Equal(["comeback", "first"], Badges("u:в"));
        _r.Result(AB, VG, 0, facts: new PadelFacts([0, 0], [1, 0], [false, false], [false, false]));
        Assert.DoesNotContain("tiebreak", Badges("u:а"));
        _r.Result(AB, VG, 0, facts: new PadelFacts([0, 0], [1, 2], [false, false], [false, false]));
        Assert.Contains("tiebreak", Badges("u:а"));
        Assert.DoesNotContain("tiebreak", Badges("u:в"));                        // лише 2
    }

    [Fact]
    public void Streak_social_and_marathon()
    {
        for (var i = 0; i < 4; i++) _r.Result(AB, VG, 0);
        _r.Result(AB, VG, -1);                                                    // нічия рве серію
        for (var i = 0; i < 4; i++) _r.Result(AB, VG, 0);
        Assert.DoesNotContain("streak", Badges("u:а"));
        _r.Result(AB, VG, 0);
        Assert.Contains("streak", Badges("u:а"));

        for (var i = 1; i <= 9; i++) _r.Result(["u:соц", "u:п" + i], VG, 1);
        Assert.DoesNotContain("social", Badges("u:соц"));
        _r.Result(["u:соц", "u:п10"], VG, 1);
        Assert.Contains("social", Badges("u:соц"));

        Assert.DoesNotContain("marathon", Badges("u:в"));                        // 20 результатів
        for (var i = 0; i < 30; i++) _r.Result(["u:в", "u:х"], ["u:ч", "u:ш"], 0);
        Assert.Contains("marathon", Badges("u:в"));
    }

    [Fact]
    public void Badges_from_tournaments()
    {
        for (var i = 0; i < 3; i++)
            _r.Hist.Tours.Add(new PadelTourResult("t" + i, _r.Clock.UtcNow.AddMinutes(i), "Американо", "americano",
                [["u:а"], ["u:б"], ["u:в"], ["u:г"]], new Dictionary<string, string[]> { ["wall"] = i == 0 ? ["u:г"] : [] }));
        Assert.Equal(["champion", "podium"], Badges("u:а"));
        Assert.Equal(["podium"], Badges("u:в"));
        Assert.Equal(["wall"], Badges("u:г"));
        _r.Hist.Tours.Add(new PadelTourResult("t9", _r.Clock.UtcNow, "Пари", "team", [["u:д", "u:е"], ["u:а", "u:б"]],
            new Dictionary<string, string[]>()));
        Assert.Contains("champion", Badges("u:е"));                               // пара — обом
    }

    [Fact]
    public void Regular_counts_gatherings_that_passed()
    {
        for (var i = 0; i < 10; i++) _r.NewGathering(by: "завсідник", hours: 1, inMinutes: 60 * (i + 1));
        var cancelled = _r.NewGathering(by: "завсідник", inMinutes: 30);
        R(_r.Gather.Cancel(U("завсідник"), cancelled));
        _r.Clock.Advance(TimeSpan.FromHours(10));                                 // минуло 9 зборів
        Assert.DoesNotContain("regular", Badges("u:завсідник"));
        _r.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Contains("regular", Badges("u:завсідник"));
    }

    [Fact]
    public void First_check_is_silent_then_new_badges_toast_accounts_once()
    {
        _r.Result(["u:а", "g:1"], VG, 0);
        Assert.True(_r.Rating.CheckBadges() > 0);
        Assert.Empty(_r.Out.Toasts);                                              // старт — мовчки
        Assert.Equal(0, _r.Out.Rating);

        _r.Result(["u:а", "g:1"], VG, 0, facts: new PadelFacts([0, 0], [0, 0], [true, false], [false, false]));
        Assert.Equal(2, _r.Rating.CheckBadges());                                 // бублик а і гостю
        var t = Assert.Single(_r.Out.Toasts);
        Assert.Equal(("а", "🏅 Нова відзнака в Падельні: Бублик"), t);
        Assert.Equal(1, _r.Out.Rating);
        Assert.Equal(0, _r.Rating.CheckBadges());

        var p = Profile("u:а");
        Assert.Equal(["first", "bagel"], p.GetProperty("badges").EnumerateArray().Select(x => x.GetProperty("key").GetString()));
        Assert.Equal("🥯", p.GetProperty("badges")[1].GetProperty("emoji").GetString());
    }
}
