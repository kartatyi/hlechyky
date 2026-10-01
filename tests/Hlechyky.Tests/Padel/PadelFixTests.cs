using System.Text.Json;
using Hlechyky.Padel;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using static Hlechyky.Tests.Padel.PadelMoneyRig;

namespace Hlechyky.Tests.Padel;

/// <summary>Виправлення після рецензії 01.10: банки, прив'язка гостя, подвійний тиць, «↶» турнірного матчу, межі.</summary>
public sealed class PadelFixTests : IDisposable
{
    readonly PadelPlayTests.Rig _p = new();
    readonly PadelMoneyRig _m = new();

    public void Dispose()
    {
        _p.Dispose();
        _m.Dispose();
    }

    static string Up(string nick) => PadelPlayTests.Rig.U(nick);
    static PadelWho Who(string nick, bool admin = false) => PadelPlayTests.Rig.Who(nick, admin);

    static JsonElement Body(PadelReply r)
    {
        Assert.True(r.Error is null, r.Error);
        return Views.Json(r.Body);
    }

    static string[][] Teams => [[Up("Влад"), Up("Микола")], [Up("Smaug"), Up("Андрій")]];

    string NewMatch(DateTimeOffset? until = null) =>
        Body(_p.Matches.Create(Who("Влад"), new PadelMatchRequest(Teams, null, null, null, until))).GetProperty("match").GetProperty("id").GetString()!;

    // ============================================================ банки (п. 1)

    static PadelMoney.ExpenseRequest Ex(string? payer, string[] people, int court) => new(null, null, payer, people, court, null, null, null);

    JsonElement Owe(string nick, string to) =>
        R(_m.Money.View(U(nick))).Body.GetProperty("owe").EnumerateArray().Single(o => o.GetProperty("pid").GetString() == to);

    [Fact]
    public void Creditor_banks_only_for_a_debt_someone_else_recorded()
    {
        Assert.Equal(200, R(_m.Money.SetBanks(U("влад"), [new(null, "mono", "", "4111 1111 1111 1111", null)])).Status);

        // Сам собі «борг»: платіж «влад скинув тані», записаний танею, — сума є, банків нема
        Assert.Equal(200, R(_m.Money.Pay(U("таня"), new("u:влад", "u:таня", 300, null))).Status);
        var o = Owe("таня", "u:влад");
        Assert.Equal(300, o.GetProperty("amount").GetInt32());
        Assert.Empty(o.GetProperty("banks").EnumerateArray());

        // Витрата, де «платив влад», записана самою танею, — теж ні
        Assert.Equal(200, R(_m.Money.Create(U("таня"), Ex("u:влад", ["u:таня"], 100))).Status);
        o = Owe("таня", "u:влад");
        Assert.Equal(400, o.GetProperty("amount").GetInt32());
        Assert.Empty(o.GetProperty("banks").EnumerateArray());

        // Звичайно: витрату записав сам платник — банки видно, сума — повна
        Assert.Equal(200, R(_m.Money.Create(U("влад"), Ex(null, ["u:влад", "u:таня"], 200))).Status);
        o = Owe("таня", "u:влад");
        Assert.Equal(500, o.GetProperty("amount").GetInt32());
        Assert.Equal("4111111111111111", Assert.Single(o.GetProperty("banks").EnumerateArray()).GetProperty("card").GetString());

        // Витрату записав третій (оля), платив влад — діма бачить банки
        Assert.Equal(200, R(_m.Money.Create(U("оля"), Ex("u:влад", ["u:діма"], 100))).Status);
        Assert.Single(Owe("діма", "u:влад").GetProperty("banks").EnumerateArray());
    }

    [Fact]
    public void Payment_toasts_name_the_sender_without_gender()
    {
        R(_m.Money.Pay(U("оля"), new("u:оля", "u:влад", 300, null)));
        Assert.Equal(("влад", $"💸 Від {NickCases.Genitive("оля")}: скинуто тобі 300 грн"), _m.Out.Toasts[^1]);
        R(_m.Money.Pay(U("влад"), new("u:оля", "u:влад", 50, null)));
        Assert.Equal(("оля", "💸 влад: від тебе отримано 50 грн"), _m.Out.Toasts[^1]);
    }

    [Fact]
    public void Expense_view_tells_who_recorded_it()
    {
        var (_, b) = R(_m.Money.Create(U("таня"), Ex("u:влад", ["u:влад", "u:таня"], 100)));
        Assert.Equal("u:таня", b.GetProperty("expense").GetProperty("byPid").GetString());
    }

    // ============================================================ гості (п. 2)

    PadelReply Link(PadelWho who, string guest, string? nick = null, Func<string, bool>? money = null) =>
        PadelPlaySetup.LinkGuest(who, new PadelLinkRequest(guest, nick), _p.Players, money ?? (_ => false), _p.W, NullLogger.Instance);

    [Fact]
    public void Guest_link_rules_toast_and_money_guard()
    {
        var g = Body(_p.Players.AddGuest("Вася", "Оля")).GetProperty("player").GetProperty("pid").GetString()!;
        Assert.Equal(403, Link(Who("Влад"), g, nick: "Smaug").Status);                         // до чужого ніка — ні
        Assert.Equal(403, Link(new PadelWho(null, "гість", false), g).Status);
        // Гість з грошима — лише адмін
        Assert.Equal("У цього гостя є гроші в розрахунках — прив'язати може адмін", Link(Who("Влад"), g, money: p => p == g).Error);
        Assert.Equal(g, _p.Players.Canon(g));

        Body(Link(Who("Влад"), g));
        Assert.Equal(Up("Влад"), _p.Players.Canon(g));
        var toast = Assert.Single(_p.W.Toasts);
        Assert.Equal("оля", toast.Nick);
        Assert.Contains("Вася", toast.Text);
        Assert.Equal("Цього гостя вже прив'язано до Влад", Link(Who("Smaug"), g).Error);   // переприв'язати — ні
        Assert.Single(_p.W.Toasts);
        Body(Link(Who("Влад"), g));                                                           // ще раз собі — нічого нового
        Assert.Single(_p.W.Toasts);
        Body(Link(Who("Таня", admin: true), g, nick: "Smaug", money: _ => true));             // адмін — може все
        Assert.Equal(Up("Smaug"), _p.Players.Canon(g));

        // Свого гостя прив'язати собі — тосту нема (сам знає)
        var mine = Body(_p.Players.AddGuest("Петро Новий", "Костя")).GetProperty("player").GetProperty("pid").GetString()!;
        Body(Link(Who("Костя"), mine));
        Assert.Equal(2, _p.W.Toasts.Count);
    }

    [Fact]
    public void Money_touches_any_record_of_the_guest()
    {
        Assert.False(_m.Money.Touches("g:1"));
        R(_m.Money.Create(U("влад"), Ex(null, ["u:влад", "g:2"], 100)));
        Assert.False(_m.Money.Touches("g:1"));
        Assert.True(_m.Money.Touches("g:2"));
        R(_m.Money.Pay(U("влад"), new("g:1", "u:влад", 10, null)));
        Assert.True(_m.Money.Touches("g:1"));
    }

    // ============================================================ табло (п. 3, 9, 11, 12)

    [Fact]
    public void Same_seq_twice_scores_one_point_and_returns_fresh_view()
    {
        var id = NewMatch();
        var a = _p.Matches.Act(id, Who("Влад"), new("point", 0, null, null, Seq: 1));
        var seq = Views.Json(a.Body).GetProperty("match").GetProperty("last").GetProperty("seq").GetInt32();
        Assert.Equal(2, seq);
        var b = _p.Matches.Act(id, Who("Микола"), new("point", 0, null, null, Seq: 1));
        Assert.Equal(409, b.Status);
        Assert.Equal(PadelMatches.Stale, b.Error);
        var fresh = Views.Json(b.Body);
        Assert.False(fresh.GetProperty("ok").GetBoolean());
        Assert.Equal(2, fresh.GetProperty("match").GetProperty("last").GetProperty("seq").GetInt32());
        Assert.Equal([1, 0], Views.Json(_p.Matches.View(id)).GetProperty("state").GetProperty("pts").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(409, _p.Matches.Act(id, Who("Влад"), new("undo", null, null, null, Seq: 1)).Status);
        Assert.Null(_p.Matches.Act(id, Who("Влад"), new("point", 1, null, null)).Error);         // старий клієнт без seq
        Assert.Null(_p.Matches.Act(id, Who("Влад"), new("point", 1, null, null, Seq: 3)).Error);
    }

    [Fact]
    public void Parallel_points_with_one_seq_land_once()
    {
        var id = NewMatch();
        var codes = new int[8];
        Parallel.For(0, 8, i => codes[i] = _p.Matches.Act(id, Who("Влад"), new("point", 0, null, null, Seq: 1)).Status);
        Assert.Equal(1, codes.Count(c => c == 200));
        Assert.Equal(7, codes.Count(c => c == 409));
        Assert.Equal(1, Views.Json(_p.Matches.View(id)).GetProperty("state").GetProperty("pts")[0].GetInt32());
    }

    [Fact]
    public void Court_until_is_for_live_matches_within_bounds()
    {
        var now = _p.Clock.UtcNow;
        Assert.Equal("Оренда — не далі ніж на 12 годин наперед",
            _p.Matches.Create(Who("Влад"), new PadelMatchRequest(Teams, null, null, null, now.AddHours(13))).Error);
        var id = NewMatch();
        Assert.Equal(400, _p.Matches.Act(id, Who("Влад"), new("until", null, null, now.AddHours(12.5))).Status);
        Assert.Equal(400, _p.Matches.Act(id, Who("Влад"), new("until", null, null, now.AddMinutes(-10))).Status);
        Assert.Null(_p.Matches.Act(id, Who("Влад"), new("until", null, null, now.AddMinutes(-3))).Error);   // щойно скінчилась
        Assert.Null(_p.Matches.Act(id, Who("Влад"), new("until", null, null, now.AddHours(2))).Error);
        Assert.Null(_p.Matches.Act(id, Who("Влад"), new("until", null, null, null)).Error);
        Assert.Null(_p.Matches.Act(id, Who("Влад"), new("finish", null, null, null)).Error);
        Assert.Equal("Матч уже скінчено", _p.Matches.Act(id, Who("Влад"), new("until", null, null, now.AddHours(1))).Error);
    }

    [Fact]
    public void Unknown_gathering_is_refused_for_matches_and_tournaments()
    {
        Assert.Equal("Нема такого збору", _p.Matches.Create(Who("Влад"), new PadelMatchRequest(Teams, null, null, "g999", null)).Error);
        var players = PadelPlayTests.Rig.Nicks.Take(4).Select(Up).ToArray();
        Assert.Equal("Нема такого збору", _p.Tours.Create(Who("Влад"),
            new PadelTourRequest("americano", null, players, null, null, 1, default, null, 1, 2, "g999")).Error);
    }

    [Fact]
    public async Task Late_clip_resends_the_same_seq_with_clip()
    {
        _p.Matches.ClipPoll = TimeSpan.FromMilliseconds(30);
        var id = NewMatch();
        var m = Views.Json(_p.Matches.Act(id, Who("Влад"), new("point", 0, null, null)).Body).GetProperty("match");
        var last = m.GetProperty("last");
        var say = last.GetProperty("say").GetProperty("text").GetString()!;
        Assert.Equal(JsonValueKind.Null, last.GetProperty("say").GetProperty("clip").ValueKind);
        var seq = last.GetProperty("seq").GetInt32();
        lock (_p.V.Ready) _p.V.Ready.Add(say);
        JsonElement? again = null;
        for (var i = 0; i < 100 && again is null; i++)
        {
            await Task.Delay(20);
            again = _p.W.MatchesNow().Skip(1).Cast<JsonElement?>()
                .FirstOrDefault(x => x!.Value.GetProperty("last").GetProperty("seq").GetInt32() == seq &&
                    x.Value.GetProperty("last").GetProperty("say").GetProperty("clip").ValueKind == JsonValueKind.String);
        }
        Assert.NotNull(again);
    }

    // ============================================================ турніри (п. 6, 7, 16)

    (string Id, JsonElement T) Tour()
    {
        var t = Body(_p.Tours.Create(Who("Влад"), new PadelTourRequest("americano", null, [.. PadelPlayTests.Rig.Nicks.Take(8).Select(Up)],
            null, null, 2, JsonDocument.Parse("\"16\"").RootElement, null, 2, 2, null))).GetProperty("tournament");
        return (t.GetProperty("id").GetString()!, t);
    }

    static string FirstPlayer(JsonElement t, PadelPlayers players) =>
        players.Name(t.GetProperty("rounds")[0].GetProperty("matches")[0].GetProperty("a")[0].GetString()!);

    [Fact]
    public void Undo_after_a_tour_match_ended_gives_the_court_back_and_keeps_the_score()
    {
        var (id, t) = Tour();
        var player = FirstPlayer(t, _p.Players);
        var mid = Body(_p.Tours.Live(id, Who(player), new(1, 1))).GetProperty("match").GetProperty("id").GetString()!;
        for (var i = 0; i < 16; i++) _p.Matches.Act(mid, Who(player), new("point", i < 9 ? 0 : 1, null, null));
        var tm = Views.Json(_p.Tours.View(id)).GetProperty("rounds")[0].GetProperty("matches")[0];
        Assert.Equal(JsonValueKind.Null, tm.GetProperty("live").ValueKind);
        Assert.Equal(9, tm.GetProperty("sa").GetInt32());

        var back = Body(_p.Matches.Act(mid, Who(player), new("undo", null, null, null))).GetProperty("match");
        Assert.Equal("live", back.GetProperty("status").GetString());
        tm = Views.Json(_p.Tours.View(id)).GetProperty("rounds")[0].GetProperty("matches")[0];
        Assert.Equal(mid, tm.GetProperty("live").GetString());
        Assert.Equal(9, tm.GetProperty("sa").GetInt32());                                      // рахунок лишився
        _p.Matches.Act(mid, Who(player), new("point", 0, null, null));
        tm = Views.Json(_p.Tours.View(id)).GetProperty("rounds")[0].GetProperty("matches")[0];
        Assert.Equal(JsonValueKind.Null, tm.GetProperty("live").ValueKind);
        Assert.Equal((10, 6), (tm.GetProperty("sa").GetInt32(), tm.GetProperty("sb").GetInt32()));
    }

    [Fact]
    public void Double_live_call_opens_one_match()
    {
        var (id, t) = Tour();
        var player = FirstPlayer(t, _p.Players);
        var ids = new string?[8];
        Parallel.For(0, 8, i => ids[i] = Views.Json(_p.Tours.Live(id, Who(player), new(1, 1)).Body).GetProperty("match").GetProperty("id").GetString());
        Assert.Single(ids.Distinct());
        Assert.Single(_p.Matches.Live());
    }

    [Fact]
    public void Tour_delete_rights()
    {
        var (id, t) = Tour();
        var player = FirstPlayer(t, _p.Players);
        var mid = Body(_p.Tours.Live(id, Who(player), new(1, 1))).GetProperty("match").GetProperty("id").GetString()!;
        Assert.Equal(403, _p.Tours.Delete(id, Who(player == "Влад" ? "Микола" : player)).Status);  // гравець — не організатор
        Assert.Equal(403, _p.Tours.Delete(id, new PadelWho(null, "гість", false)).Status);
        Assert.Null(_p.Tours.Delete(id, Who("Влад")).Error);                                     // організатор, рахунків нема
        Assert.Null(_p.Tours.View(id));
        Assert.Equal("abandoned", Views.Json(_p.Matches.View(mid)).GetProperty("status").GetString());
        Assert.Equal(404, _p.Tours.Delete(id, Who("Влад", admin: true)).Status);
    }

    [Fact]
    public void Mixed_plan_counts_real_courts_and_rests()
    {
        var even = PadelTourGen.Plan("mixed", 8, 2, "24", null, 2, women: 4);
        Assert.Equal((8, 0, false), (even.Slots, even.Sit, even.Avg));
        var odd = PadelTourGen.Plan("mixed", 6, 2, "24", null, 2, women: 3);              // одна жінка й один чоловік сидять
        Assert.Equal((4, 2, 3, false), (odd.Slots, odd.Sit, odd.Fair, odd.Avg));
        Assert.Equal(0, odd.Rec % 3);
        var skew = PadelTourGen.Plan("mixed", 10, 3, "24", null, 2, women: 6);            // 4 чоловіки — лише 2 корти
        Assert.Equal((8, 2, true), (skew.Slots, skew.Sit, skew.Avg));
        Assert.Contains("не порівну", skew.Note);
        var plan = Views.Json(Body(PadelTours.PlanFor("mixed", 10, 3, "24", null, 2, 6)));
        Assert.Equal(8, plan.GetProperty("slots").GetInt32());
    }

    // ============================================================ збори (п. 10, 14) і рейтинг (п. 8)

    [Fact]
    public void Past_gathering_is_closed_to_everyone_but_its_owner()
    {
        var gid = _m.NewGathering(inMinutes: 60);
        _m.Join(gid, "таня");
        _m.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal("Збір уже минув", R(_m.Gather.Join(U("діма"), gid, null, null)).Body.GetProperty("message").GetString());
        Assert.Equal(400, R(_m.Gather.Leave(U("таня"), gid, null)).Status);
        var g = _m.Join(gid, "влад", pid: "u:діма");                                             // творець виправляє, хто прийшов
        Assert.Contains(g.GetProperty("going").EnumerateArray(), x => x.GetProperty("pid").GetString() == "u:діма" &&
            x.GetProperty("addedBy").GetString() == "u:влад");
        Assert.Equal(200, R(_m.Gather.Leave(U("адмін", admin: true), gid, "u:таня")).Status);
    }

    [Fact]
    public void Rating_follows_an_edited_score()
    {
        string[] a = ["u:а", "u:б"], b = ["u:в", "u:г"];
        var r = _m.Result(a, b, 0, points: [15, 9]);
        _m.Clock.Advance(11);
        Assert.Equal(1205, (int)Math.Round(_m.Rating.Book().Stats["u:а"].R));
        _m.Hist.Res[_m.Hist.Res.IndexOf(r)] = r with { Points = [20, 4] };                      // правка рахунку, той самий Id
        _m.Clock.Advance(11);
        Assert.Equal(1213, (int)Math.Round(_m.Rating.Book().Stats["u:а"].R));
    }
}
