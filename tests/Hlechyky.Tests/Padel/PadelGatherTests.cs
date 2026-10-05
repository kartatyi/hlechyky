using Hlechyky.Padel;
using Hlechyky.Tests.Support;
using static Hlechyky.Tests.Padel.PadelMoneyRig;

namespace Hlechyky.Tests.Padel;

/// <summary>Збори на гру (контракт §3.1): форма, права, черга з автопідняттям, скасування, нагадування, агенда.</summary>
public sealed class PadelGatherTests : IDisposable
{
    readonly PadelMoneyRig _r = new();
    public void Dispose() => _r.Dispose();

    static string[] Pids(System.Text.Json.JsonElement g, string list) =>
        [.. g.GetProperty(list).EnumerateArray().Select(p => p.GetProperty("pid").GetString()!)];

    [Fact]
    public void Create_converts_kyiv_time_counts_slots_and_puts_the_creator_in()
    {
        // 2026-10-04 18:00 за Києвом (літній час, UTC+3) = 15:00 UTC
        var (s, b) = R(_r.Gather.Create(U("влад"), new PadelGather.Form("2026-10-04T18:00", 1.5, 2, "  Padel   Club Позняки ", null, null)));
        Assert.Equal(200, s);
        var g = b.GetProperty("gathering");
        Assert.Equal("g1", g.GetProperty("id").GetString());
        Assert.Equal(new DateTime(2026, 10, 4, 15, 0, 0, DateTimeKind.Utc), g.GetProperty("start").GetDateTime().ToUniversalTime());
        Assert.Equal(new DateTime(2026, 10, 4, 16, 30, 0, DateTimeKind.Utc), g.GetProperty("until").GetDateTime().ToUniversalTime());
        Assert.Equal("2026-10-04T18:00", g.GetProperty("local").GetString());
        Assert.Equal(8, g.GetProperty("slots").GetInt32());
        Assert.Equal("Padel Club Позняки", g.GetProperty("place").GetString());
        Assert.Equal("open", g.GetProperty("status").GetString());
        Assert.Equal("влад", g.GetProperty("by").GetString());
        Assert.Equal(["u:влад"], Pids(g, "going"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, g.GetProperty("expense").ValueKind);
        Assert.Single(_r.Out.Gatherings);
        Assert.Equal(1, _r.Out.Lobby);
    }

    [Theory]
    [InlineData("2026-10-04T18:00", 0.7, 1, "Корт")]       // не крок пів години
    [InlineData("2026-10-04T18:00", 4.5, 1, "Корт")]
    [InlineData("2026-10-04T18:00", 1, 7, "Корт")]
    [InlineData("2026-10-04T18:00", 1, 1, "   ")]
    [InlineData("2026-09-10T13:00", 1, 1, "Корт")]         // дві години тому
    [InlineData("завтра", 1, 1, "Корт")]
    public void Create_refuses_bad_forms(string local, double hours, int courts, string place)
    {
        var (s, b) = R(_r.Gather.Create(U("влад"), new PadelGather.Form(local, hours, courts, place, null, null)));
        Assert.Equal(400, s);
        Assert.False(b.GetProperty("ok").GetBoolean());
        Assert.Empty(_r.Out.Gatherings);
    }

    [Fact]
    public void Site_guest_cannot_act_but_can_look()
    {
        var gid = _r.NewGathering();
        Assert.Equal(403, R(_r.Gather.Create(SiteGuest, new PadelGather.Form(_r.LocalIn(60), 1, 1, "Корт", null, null))).Status);
        Assert.Equal(403, R(_r.Gather.Join(SiteGuest, gid, null, null)).Status);
        Assert.Equal(403, R(_r.Gather.Leave(SiteGuest, gid, "u:влад")).Status);
        Assert.Single(Views.Json(_r.Gather.List()).GetProperty("upcoming").EnumerateArray());
    }

    [Fact]
    public void Over_slots_goes_to_the_queue_and_a_freed_place_lifts_the_first_with_a_toast()
    {
        var gid = _r.NewGathering(slots: 2);
        _r.Join(gid, "оля");
        _r.Join(gid, "таня");
        var g = _r.Join(gid, "оля", pid: "g:1");          // Оля вписує гостя Петра — він другий у черзі
        Assert.Equal(["u:влад", "u:оля"], Pids(g, "going"));
        Assert.Equal(["u:таня", "g:1"], Pids(g, "wait"));
        Assert.Equal(2, _r.Gather.Find(gid)!.Going);

        var (s, b) = R(_r.Gather.Leave(U("оля"), gid, null));
        Assert.Equal(200, s);
        g = b.GetProperty("gathering");
        Assert.Equal(["u:влад", "u:таня"], Pids(g, "going"));
        Assert.Equal(["g:1"], Pids(g, "wait"));
        var toast = Assert.Single(_r.Out.Toasts);
        Assert.Equal("таня", toast.Nick);
        Assert.StartsWith("🍳 Звільнилось місце — ти йдеш на падел", toast.Text);
        Assert.Equal(["u:влад", "u:таня"], _r.Gather.Going(gid));
    }

    [Fact]
    public void Join_twice_is_harmless_and_only_updates_the_racket()
    {
        var gid = _r.NewGathering();
        _r.Join(gid, "оля");
        var g = _r.Join(gid, "оля", racket: true);
        var mine = Assert.Single(g.GetProperty("going").EnumerateArray(), p => p.GetProperty("pid").GetString() == "u:оля");
        Assert.True(mine.GetProperty("racket").GetBoolean());
        Assert.Equal(2, g.GetProperty("going").GetArrayLength());
        Assert.Equal(400, R(_r.Gather.Join(U("оля"), gid, "g:99", null)).Status);   // нема такого гостя
    }

    [Fact]
    public void Only_self_the_one_who_added_the_creator_or_admin_can_take_someone_out()
    {
        var gid = _r.NewGathering();
        _r.Join(gid, "оля", pid: "g:1");
        _r.Join(gid, "таня");
        Assert.Equal(403, R(_r.Gather.Leave(U("таня"), gid, "g:1")).Status);
        Assert.Equal(403, R(_r.Gather.Racket(U("таня"), gid, "g:1", true)).Status);
        Assert.Equal(200, R(_r.Gather.Racket(U("оля"), gid, "g:1", true)).Status);      // вписала — може й ракетку
        Assert.Equal(200, R(_r.Gather.Leave(U("оля"), gid, "g:1")).Status);
        Assert.Equal(200, R(_r.Gather.Leave(U("влад"), gid, "u:таня")).Status);         // творець
        Assert.Equal(400, R(_r.Gather.Leave(U("влад"), gid, "u:таня")).Status);         // уже нема
        _r.Join(gid, "таня");
        Assert.Equal(200, R(_r.Gather.Leave(U("адмін", admin: true), gid, "u:таня")).Status);
    }

    [Fact]
    public void Going_is_canonical_and_a_linked_guest_is_the_same_person()
    {
        var gid = _r.NewGathering();
        _r.Join(gid, "влад", pid: "g:1");
        _r.Who.Links["g:1"] = "u:петро";
        Assert.Equal(["u:влад", "u:петро"], _r.Gather.Going(gid));
        var g = _r.Join(gid, "петро");                         // сам Петро — уже записаний як гість
        Assert.Equal(2, g.GetProperty("going").GetArrayLength());
    }

    [Fact]
    public void Edit_moves_slots_with_courts_and_lifts_the_queue()
    {
        var gid = _r.NewGathering(courts: 1);                  // 4 місця
        foreach (var n in new[] { "оля", "таня", "діма", "сашко" }) _r.Join(gid, n);
        Assert.Equal(403, R(_r.Gather.Edit(U("оля"), gid, new PadelGather.Form(null, null, 2, null, null, null))).Status);
        var (s, b) = R(_r.Gather.Edit(U("влад"), gid, new PadelGather.Form(null, 2, 2, null, "м'ячі з собою", null)));
        Assert.Equal(200, s);
        var g = b.GetProperty("gathering");
        Assert.Equal(8, g.GetProperty("slots").GetInt32());
        Assert.Equal(2.0, g.GetProperty("hours").GetDouble());
        Assert.Equal(5, g.GetProperty("going").GetArrayLength());
        Assert.Contains(_r.Out.Toasts, t => t.Nick == "сашко" && t.Text.Contains("Звільнилось місце"));
    }

    [Fact]
    public void Cancel_tells_everyone_who_was_going_except_the_one_who_cancelled()
    {
        var gid = _r.NewGathering(slots: 2);
        _r.Join(gid, "оля");
        _r.Join(gid, "таня");                                   // у черзі — їй не кажемо
        _r.Join(gid, "оля", pid: "g:1");
        Assert.Equal(403, R(_r.Gather.Cancel(U("оля"), gid)).Status);
        var (s, b) = R(_r.Gather.Cancel(U("влад"), gid));
        Assert.Equal(200, s);
        Assert.Equal("cancelled", b.GetProperty("gathering").GetProperty("status").GetString());
        var t = Assert.Single(_r.Out.Toasts);
        Assert.Equal("оля", t.Nick);
        Assert.Contains("скасовано", t.Text);
        Assert.Equal(400, R(_r.Gather.Join(U("діма"), gid, null, null)).Status);
        Assert.Empty(_r.Gather.Upcoming(5));
        var list = Views.Json(_r.Gather.List());
        Assert.Empty(list.GetProperty("upcoming").EnumerateArray());
        Assert.Single(list.GetProperty("recent").EnumerateArray());
    }

    [Fact]
    public void Reminder_goes_once_an_hour_before_to_accounts_that_are_going_and_survives_a_restart()
    {
        var soon = _r.NewGathering(slots: 2, inMinutes: 50);
        _r.Join(soon, "оля", racket: true);
        _r.Join(soon, "таня");                                  // черга — без нагадування
        var later = _r.NewGathering(by: "діма", inMinutes: 120);
        _r.Out.Toasts.Clear();

        Assert.Equal(2, _r.Gather.Remind());
        Assert.Contains(_r.Out.Toasts, t => t.Nick == "влад" && t.Text.EndsWith("Ракетку береш?") && t.Text.Contains("Padel Club Позняки"));
        Assert.Contains(_r.Out.Toasts, t => t.Nick == "оля" && t.Text.EndsWith("Ракетку беремо там"));
        Assert.Equal(0, _r.Gather.Remind());
        Assert.Equal(0, _r.NewGather().Remind());              // рестарт: позначки в базі

        _r.Clock.Advance(TimeSpan.FromMinutes(61));             // «пізніший» тепер за 59 хв
        Assert.Equal(1, _r.Gather.Remind());
        Assert.Equal("діма", _r.Out.Toasts[^1].Nick);
        _ = later;
    }

    [Fact]
    public void Reminder_never_comes_for_a_game_that_already_started()
    {
        var gid = _r.NewGathering(inMinutes: 30);
        _r.Clock.Advance(TimeSpan.FromMinutes(40));             // сервер лежав, поки гра почалась
        Assert.Equal(0, _r.NewGather().Remind());
        Assert.Equal(1, _r.Gather.Upcoming(5).Count);           // а в агенді збір ще є — він іде
        _ = gid;
    }

    [Fact]
    public void Agenda_and_places_for_the_lobby_and_the_form()
    {
        var a = _r.NewGathering(inMinutes: 300, place: "Корт на Оболоні");
        var b = _r.NewGathering(inMinutes: 100, courts: 2);
        var up = _r.Gather.Upcoming(1);
        var item = Assert.Single(up);
        Assert.Equal(b, item.Id);
        Assert.Equal(8, item.Slots);
        Assert.Equal(1, item.Going);
        Assert.Equal(item.Start.AddHours(1.5), item.Until);
        Assert.Equal(a, _r.Gather.Find(a)!.Id);
        Assert.Null(_r.Gather.Find("g99"));
        Assert.Null(_r.Gather.Find("e1"));
        var places = Views.Json(_r.Gather.Places()).GetProperty("places").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(["Padel Club Позняки", "Корт на Оболоні"], places);
    }
}
