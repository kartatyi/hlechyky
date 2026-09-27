using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Де це?» і тренування: паспорт, фази, шпильки, очки, розкриття, кінець, вихід, рематч, приховане й форма
/// видів на дроті (docs/games/specs/geo.md §8). Партія живе від тика (500 мс), тож більшість тестів —
/// «прокрути годинник і подивись».
/// </summary>
public class GeoTests
{
    const int MaxTicks = 3000;

    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан", "Леся", "Остап", "Ніна", "Юрко"];

    internal static RoomHarness Table(GeoCache c, int players = 2, object? options = null, int seed = 42, bool start = true)
    {
        var h = new RoomHarness("geo", options, seed, c.Services);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        if (start) Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    internal static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    internal static bool Playing(RoomHarness h) => h.Rooms.Find(h.RoomId) is { Status: RoomStatus.Playing };

    internal static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < MaxTicks && Playing(h) && Phase(h) != phase; i++) h.Tick(1);
    }

    /// <summary>Місце поточного раунду — через токен фото, як його бачив би сервер (клієнт цього не може).</summary>
    internal static GeoPlace Current(RoomHarness h, GeoCache c)
    {
        var url = h.View(null).GetProperty("photo").GetString()!;
        var path = c.Photos.Resolve(url["/api/games/geo/".Length..^4])!;
        return c.Bank.Places.First(p => p.Photos.Any(ph => c.Photos.PathFor(ph) == path));
    }

    internal static (int X, int Y) Truth(RoomHarness h, GeoCache c)
    {
        var p = Current(h, c);
        return GeoMap.Project(p.Lat, p.Lon);
    }

    static ActResult Pin(RoomHarness h, int seat, int x, int y) =>
        h.Act(seat, "guess", new { x = Math.Clamp(x, 0, GeoMap.W), y = Math.Clamp(y, 0, GeoMap.H) });

    static JsonElement Reveal(RoomHarness h) => h.View(null).GetProperty("reveal");

    static JsonElement Row(RoomHarness h, int seat) =>
        Reveal(h).GetProperty("rows").EnumerateArray().First(r => r.GetProperty("seat").GetInt32() == seat);

    static long Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt64();

    /// <summary>Зіграти всю партію: <paramref name="aim"/> каже, куди кожне місце ставить шпильку (null — не ставить).</summary>
    static void PlayAll(RoomHarness h, GeoCache c, Func<int, (int X, int Y), (int X, int Y)?> aim, int seats)
    {
        for (var guard = 0; guard < 40 && Playing(h); guard++)
        {
            Until(h, GeoMatch.PhaseGuess);
            if (!Playing(h)) break;
            var truth = Truth(h, c);
            for (var s = 0; s < seats; s++)
                if (aim(s, truth) is { } p)
                {
                    Assert.True(Pin(h, s, p.X, p.Y).Ok);
                    h.Act(s, "ready");
                }
            h.Tick(1);
            if (Phase(h) != GeoMatch.PhaseReveal) Until(h, GeoMatch.PhaseReveal);
            for (var s = 0; s < seats; s++) h.Act(s, "next");
            h.Tick(1);
        }
    }

    // ---------------------------------------------------------------------------------------
    // паспорт
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Catalog_lists_geo_as_party_by_host_hidden_with_five_options_and_solo_twin_in_solo_group()
    {
        var reg = RoomHarness.NewRegistry();
        var geo = reg.Catalog.Single(g => g.Id == "geo");
        Assert.Equal("Де це?", geo.Title);
        Assert.Equal("party", geo.Group);
        Assert.Equal("byHost", geo.Start);
        Assert.True(geo.Hidden);
        Assert.False(geo.Private);
        Assert.False(geo.Rated);
        Assert.Equal(500, geo.TickMs);
        Assert.Equal((1, 10), (geo.MinPlayers, geo.MaxPlayers));
        Assert.Equal(["rounds", "seconds", "cat", "level", "hints"], geo.Options.Select(o => o.Key));
        Assert.Equal(["5", "45", "all", "all", "full"], geo.Options.Select(o => o.Default));
        Assert.True(geo.Options.Single(o => o.Key == "cat").Multi);
        Assert.Equal(["5", "7", "10"], geo.Options[0].Values.Select(v => v[0]));
        Assert.True(geo.HasCss);
        Assert.Equal("geo", geo.Module);

        var solo = reg.Catalog.Single(g => g.Id == "geo-solo");
        Assert.Equal("solo", solo.Group);
        Assert.Equal("immediate", solo.Start);
        Assert.True(solo.Private);
        Assert.Equal("geo", solo.Module);
        Assert.True(solo.HasCss);
        Assert.Empty(solo.Options);
        Assert.Equal(ScoreOrder.HigherIsBetter, reg.Info("geo-solo")!.Score);
        Assert.Equal(ScoreOrder.HigherIsBetter, reg.Info("geo")!.Score);
    }

    [Fact]
    public void Seat_names_are_plain_numbers_one_to_ten()
    {
        using var c = new GeoCache();
        var h = Table(c, 1, start: false);
        var names = Enumerable.Range(0, 10).Select(h.Room.Game.SeatName);
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"], names);
    }

    // ---------------------------------------------------------------------------------------
    // старт і фази
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Host_can_start_alone_and_the_first_round_opens_after_two_seconds_of_between()
    {
        using var c = new GeoCache();
        var h = Table(c, 1);
        var v = h.View(0);
        Assert.Equal("between", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.Equal(5, v.GetProperty("rounds").GetInt32());
        Assert.Equal(2000, v.GetProperty("phaseMs").GetInt32());
        Assert.Matches("^/api/games/geo/[0-9a-f]{24}\\.jpg$", v.GetProperty("photo").GetString()!);
        h.Tick(3);
        Assert.Equal("between", Phase(h));
        h.Tick(1);
        Assert.Equal("guess", Phase(h));
        Assert.Equal(45000, h.View(0).GetProperty("phaseMs").GetInt32());
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "Оля сіли грати в «Де це?»");
    }

    [Fact]
    public void Start_is_refused_while_no_photo_is_ready()
    {
        using var c = new GeoCache(ready: _ => false);
        var h = Table(c, 2, start: false);
        Assert.False(h.Start().Ok);
        Assert.Equal(GeoMatch.NoPhotos, h.Reply.Message);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);

        c.Add(c.Bank.Places[0]);
        Assert.True(h.Start().Ok);
        Assert.Equal(1, h.View(null).GetProperty("rounds").GetInt32());

        var bare = new RoomHarness("geo", seed: 1);
        bare.Join("Оля");
        Assert.False(bare.Start().Ok);
        Assert.Equal(GeoMatch.NoBank, bare.Reply.Message);

        using var empty = new GeoCache(GeoCache.BankOf());
        var e = Table(empty, 1, start: false);
        Assert.False(e.Start().Ok);
        Assert.Equal(GeoMatch.NoBank, e.Reply.Message);
    }

    [Fact]
    public void Guess_is_accepted_moved_and_rejected_outside_the_grid_or_with_a_bad_payload()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        Assert.Equal("Зараз не вгадують", h.Act(0, "guess", new { x = 5, y = 5 }).Message);
        Until(h, "guess");

        Assert.True(h.Act(0, "guess", new { x = 100, y = 200 }).Ok);
        var my = h.View(0).GetProperty("my");
        Assert.Equal((100, 200, false), (my.GetProperty("x").GetInt32(), my.GetProperty("y").GetInt32(), my.GetProperty("ready").GetBoolean()));
        Assert.True(h.Act(0, "guess", new { x = 4000, y = 2730 }).Ok);
        Assert.Equal(4000, h.View(0).GetProperty("my").GetProperty("x").GetInt32());
        Assert.True(h.Act(0, "guess", new { x = "1500", y = "900" }).Ok);   // цілі в рядку — теж
        Assert.Equal(900, h.View(0).GetProperty("my").GetProperty("y").GetInt32());

        var before = Views.Text(h.Room.Game.View(0));
        Assert.Equal("Шпилька поза мапою", h.Act(0, "guess", new { x = -1, y = 5 }).Message);
        Assert.Equal("Шпилька поза мапою", h.Act(0, "guess", new { x = 4001, y = 5 }).Message);
        Assert.Equal("Шпилька поза мапою", h.Act(0, "guess", new { x = 5, y = 2731 }).Message);
        const string bad = "Тут треба дві цілі координати";
        Assert.Equal(bad, h.Act(0, "guess", new { x = 1.5, y = 2 }).Message);
        Assert.Equal(bad, h.Act(0, "guess", new { x = "abc", y = 2 }).Message);
        Assert.Equal(bad, h.Act(0, "guess", new { x = 5 }).Message);
        Assert.Equal(bad, h.Act(0, "guess", null).Message);
        Assert.Equal(bad, h.Act(0, "guess", 7).Message);
        Assert.Equal(bad, h.Act(0, "guess", new { x = true, y = 2 }).Message);
        Assert.Equal(before, Views.Text(h.Room.Game.View(0)));
    }

    [Fact]
    public void Ready_without_a_pin_is_refused_and_twice_is_refused()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        Assert.Equal("Зараз не вгадують", h.Act(0, "ready").Message);
        Until(h, "guess");
        Assert.Equal("Спершу постав шпильку на мапу", h.Act(0, "ready").Message);
        Pin(h, 0, 1000, 1000);
        Assert.True(h.Act(0, "ready").Ok);
        Assert.Equal("Уже готово", h.Act(0, "ready").Message);
        Assert.Equal("Ти вже натиснув «Готово»", Pin(h, 0, 1200, 1200).Message);
        Assert.True(h.View(0).GetProperty("my").GetProperty("ready").GetBoolean());
        Assert.Equal(1000, h.View(0).GetProperty("my").GetProperty("x").GetInt32());
    }

    [Fact]
    public void Next_is_only_for_the_reveal_and_only_once_and_unknown_actions_are_refused()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        Assert.Equal("Тут так не ходять", h.Act(0, "teleport").Message);
        Until(h, "guess");
        Assert.Equal("Зараз нічого пропускати", h.Act(0, "next").Message);
        Until(h, "reveal");
        Assert.True(h.Act(0, "next").Ok);
        Assert.Equal("Уже натиснув", h.Act(0, "next").Message);
        Assert.Equal("Зараз не вгадують", Pin(h, 1, 5, 5).Message);
        h.Tick(1);
        Assert.Equal("reveal", Phase(h));                 // другий ще не натиснув — чекаємо
        Assert.Contains(0, h.View(null).GetProperty("next").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void When_everyone_is_ready_the_reveal_comes_on_the_next_tick_not_at_the_deadline()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        Until(h, "guess");
        var started = h.Clock.UtcNow;
        Pin(h, 0, 1000, 1000);
        h.Act(0, "ready");
        h.Tick(2);
        Assert.Equal("guess", Phase(h));                  // другий ще думає
        Pin(h, 1, 2000, 1000);
        h.Act(1, "ready");
        Assert.Equal("guess", Phase(h));                  // перехід — лише тиком
        h.Tick(1);
        Assert.Equal("reveal", Phase(h));
        Assert.True(h.Clock.UtcNow - started < TimeSpan.FromSeconds(3));
        Assert.Equal(10_000, h.View(null).GetProperty("phaseMs").GetInt32());
    }

    [Fact]
    public void A_guess_between_the_deadline_and_the_next_tick_is_still_accepted()
    {
        using var c = new GeoCache();
        var h = Table(c, 1);
        Until(h, "guess");
        var truth = Truth(h, c);
        h.Clock.Advance(TimeSpan.FromSeconds(45.2));      // час вийшов, а тика ще не було
        Assert.True(Pin(h, 0, truth.X, truth.Y).Ok);
        h.Tick(1);
        Assert.Equal("reveal", Phase(h));
        Assert.Equal(5000, Row(h, 0).GetProperty("points").GetInt32());
    }

    [Fact]
    public void Guess_time_and_rounds_follow_the_options()
    {
        using var c = new GeoCache();
        var h = Table(c, 1, new { seconds = "30", rounds = "7", hints = "none" });
        Until(h, "guess");
        var v = h.View(0);
        Assert.Equal(30_000, v.GetProperty("phaseMs").GetInt32());
        Assert.Equal(30, v.GetProperty("seconds").GetInt32());
        Assert.Equal(7, v.GetProperty("rounds").GetInt32());
        Assert.Equal("none", v.GetProperty("hints").GetString());
        h.Tick(59);
        Assert.Equal("guess", Phase(h));
        h.Tick(1);
        Assert.Equal("reveal", Phase(h));

        // чуже значення опції каркас зводить до типового
        var odd = Table(c, 1, new { seconds = "999", rounds = "3", hints = "x" });
        Assert.Equal(5, odd.View(0).GetProperty("rounds").GetInt32());
        Assert.Equal(45, odd.View(0).GetProperty("seconds").GetInt32());
        Assert.Equal("full", odd.View(0).GetProperty("hints").GetString());
    }

    [Fact]
    public void Reveal_lasts_ten_seconds_or_until_everyone_pressed_next()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        Until(h, "reveal");
        h.Tick(19);
        Assert.Equal("reveal", Phase(h));
        h.Tick(1);
        Assert.Equal("between", Phase(h));
        Assert.Equal(2, h.View(null).GetProperty("round").GetInt32());

        Until(h, "reveal");
        h.Act(0, "next");
        h.Act(1, "next");
        h.Tick(1);
        Assert.Equal("between", Phase(h));
        Assert.Equal(3, h.View(null).GetProperty("round").GetInt32());
    }

    // ---------------------------------------------------------------------------------------
    // очки й розкриття
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_pin_on_the_truth_scores_5000_and_a_bullseye_and_a_far_pin_scores_by_the_curve()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        Until(h, "guess");
        var place = Current(h, c);
        var t = GeoMap.Project(place.Lat, place.Lon);
        var far = (X: t.X > 2000 ? t.X - 300 : t.X + 300, t.Y);
        Pin(h, 0, t.X, t.Y);
        Pin(h, 1, far.X, far.Y);
        Until(h, "reveal");

        var r0 = Row(h, 0);
        Assert.Equal(5000, r0.GetProperty("points").GetInt32());
        Assert.True(r0.GetProperty("bull").GetBoolean());
        Assert.InRange(r0.GetProperty("km").GetDouble(), 0, 0.3);
        var km = GeoScore.KmFromGrid(far.X, far.Y, place.Lat, place.Lon);
        Assert.InRange(km, 90, 115);
        var r1 = Row(h, 1);
        Assert.Equal(GeoScore.Points(km), r1.GetProperty("points").GetInt32());
        Assert.Equal(Math.Round(km, 1, MidpointRounding.AwayFromZero), r1.GetProperty("km").GetDouble());
        Assert.False(r1.GetProperty("bull").GetBoolean());
        Assert.Equal((far.X, far.Y), (r1.GetProperty("x").GetInt32(), r1.GetProperty("y").GetInt32()));
        Assert.Equal(5000, Score(h, 0));
        Assert.Equal(GeoScore.Points(km), Score(h, 1));

        var rv = Reveal(h);
        Assert.Equal((t.X, t.Y), (rv.GetProperty("x").GetInt32(), rv.GetProperty("y").GetInt32()));
        Assert.Equal(place.Name, rv.GetProperty("name").GetString());
        Assert.Equal(place.Region, rv.GetProperty("region").GetString());
        Assert.Equal(place.Photos[0].Author, rv.GetProperty("photo").GetProperty("author").GetString());
    }

    [Fact]
    public void Reveal_rows_are_sorted_by_points_then_time_then_seat_and_the_unpinned_go_last()
    {
        using var c = new GeoCache();
        var h = Table(c, 4);
        Until(h, "guess");
        var t = Truth(h, c);
        var dx = t.X > 2000 ? -1 : 1;
        Pin(h, 0, t.X + dx * 900, t.Y);             // далеко
        Pin(h, 3, t.X + dx * 30, t.Y);              // близько, але пізніше за першу правку місця 1
        h.Clock.AdvanceMs(100);
        Pin(h, 1, t.X + dx * 30, t.Y);              // так само близько…
        h.Clock.AdvanceMs(100);
        Pin(h, 3, t.X + dx * 30, t.Y);              // …а місце 3 передумало пізніше: його час — останньої шпильки
        Until(h, "reveal");
        var order = Reveal(h).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("seat").GetInt32()).ToArray();
        Assert.Equal([1, 3, 0, 2], order);
        var r2 = Row(h, 2);
        Assert.Equal(JsonValueKind.Null, r2.GetProperty("km").ValueKind);
        Assert.Equal(JsonValueKind.Null, r2.GetProperty("x").ValueKind);
        Assert.Equal(0, r2.GetProperty("points").GetInt32());
        Assert.True(Row(h, 1).GetProperty("best").GetBoolean());
        Assert.True(Row(h, 3).GetProperty("best").GetBoolean());
        Assert.False(Row(h, 0).GetProperty("best").GetBoolean());
    }

    [Fact]
    public void Best_flag_marks_all_equally_close_players_and_is_never_set_when_playing_alone()
    {
        using var c = new GeoCache();
        var alone = Table(c, 1);
        Until(alone, "guess");
        var t = Truth(alone, c);
        Pin(alone, 0, t.X, t.Y);
        Until(alone, "reveal");
        Assert.False(Row(alone, 0).GetProperty("best").GetBoolean());
        Assert.True(Row(alone, 0).GetProperty("bull").GetBoolean());

        var h = Table(c, 3);
        Until(h, "guess");
        t = Truth(h, c);
        Pin(h, 0, t.X + 50, t.Y);
        Pin(h, 1, t.X + 50, t.Y);
        Pin(h, 2, t.X + 80, t.Y);
        Until(h, "reveal");
        Assert.True(Row(h, 0).GetProperty("best").GetBoolean());
        Assert.True(Row(h, 1).GetProperty("best").GetBoolean());
        Assert.False(Row(h, 2).GetProperty("best").GetBoolean());
    }

    static string Say(RoomHarness h) => Reveal(h).GetProperty("say").GetString()!;

    [Fact]
    public void Glek_speaks_by_the_situation_bullseye_near_far_and_silence()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        Until(h, "guess");
        var t = Truth(h, c);
        Pin(h, 0, t.X, t.Y);
        Until(h, "reveal");
        Assert.Contains(Say(h), GeoLines.Bull.Select(l => string.Format(l, "Оля", "менше кілометра")));

        Until(h, "guess");
        Until(h, "reveal");                                // ніхто не поставив
        Assert.Contains(Say(h), GeoLines.None);

        Until(h, "guess");
        var place = Current(h, c);
        t = GeoMap.Project(place.Lat, place.Lon);
        var corner = (X: t.X < 2000 ? GeoMap.W : 0, Y: t.Y < 1300 ? GeoMap.H : 0);   // на другий край мапи
        Pin(h, 1, corner.X, corner.Y);
        Until(h, "reveal");
        var km = GeoText.Km(GeoScore.KmFromGrid(corner.X, corner.Y, place.Lat, place.Lon));
        Assert.Contains(Say(h), GeoLines.AllFar.Select(l => string.Format(l, "Петро", km)));

        Until(h, "guess");
        place = Current(h, c);
        t = GeoMap.Project(place.Lat, place.Lon);
        Pin(h, 0, t.X + 60, t.Y);                          // ~20 км
        Pin(h, 1, t.X + 200, t.Y);
        Until(h, "reveal");
        km = GeoText.Km(GeoScore.KmFromGrid(t.X + 60, t.Y, place.Lat, place.Lon));
        Assert.Contains(Say(h), GeoLines.Normal.Select(l => string.Format(l, "Оля", km)));

        var alone = Table(c, 1);
        Until(alone, "guess");
        place = Current(alone, c);
        t = GeoMap.Project(place.Lat, place.Lon);
        corner = (t.X < 2000 ? GeoMap.W : 0, t.Y < 1300 ? GeoMap.H : 0);
        Pin(alone, 0, corner.X, corner.Y);
        Until(alone, "reveal");
        km = GeoText.Km(GeoScore.KmFromGrid(corner.X, corner.Y, place.Lat, place.Lon));
        Assert.Contains(Say(alone), GeoLines.SoloFar.Select(l => string.Format(l, "Оля", km)));

        Until(alone, "guess");
        place = Current(alone, c);
        t = GeoMap.Project(place.Lat, place.Lon);
        Pin(alone, 0, t.X + 60, t.Y);
        Until(alone, "reveal");
        km = GeoText.Km(GeoScore.KmFromGrid(t.X + 60, t.Y, place.Lat, place.Lon));
        Assert.Contains(Say(alone), GeoLines.SoloNear.Select(l => string.Format(l, "Оля", km)));
    }

    // ---------------------------------------------------------------------------------------
    // кінець партії
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void After_the_last_round_the_match_is_done_with_winners_scores_and_a_journal_line()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        PlayAll(h, c, (s, t) => s == 0 ? t : (t.X > 2000 ? t.X - 300 : t.X + 300, t.Y), 2);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var v = h.View(0);
        Assert.Equal("done", v.GetProperty("phase").GetString());
        Assert.Equal([0], v.GetProperty("result").GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(25_000, v.GetProperty("scores")[0].GetInt64());
        var p1 = v.GetProperty("scores")[1].GetInt64();
        Assert.InRange(p1, 5 * 2300, 5 * 3400);
        var fin = Assert.Single(h.Finished);
        Assert.Equal([0], fin.Result.Winners);
        Assert.Equal(25_000, fin.Result.Scores![0]);
        Assert.Equal(p1, fin.Result.Scores[1]);
        Assert.Equal($"Де це?: Оля 25 000, Петро {GeoText.Num(p1)} — найкраще око: Оля", fin.Result.Text);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == fin.Result.Text);
        var recap = v.GetProperty("recap");
        Assert.Equal(5, recap.GetArrayLength());
        Assert.All(recap.EnumerateArray(), r => Assert.Equal([0], r.GetProperty("best").EnumerateArray().Select(e => e.GetInt32())));
        Assert.Equal(JsonValueKind.Object, v.GetProperty("reveal").ValueKind);   // останнє розкриття лишається
        Assert.Equal(JsonValueKind.Null, v.GetProperty("endsAt").ValueKind);
    }

    [Fact]
    public void A_tie_names_every_leader_in_the_journal()
    {
        using var c = new GeoCache();
        var h = Table(c, 3);
        PlayAll(h, c, (s, t) => s < 2 ? t : null, 3);
        var fin = Assert.Single(h.Finished);
        Assert.Equal([0, 1], fin.Result.Winners);
        Assert.EndsWith("— найкраще око: Оля і Петро", fin.Result.Text);
        Assert.Contains("Ганна 0", fin.Result.Text);
    }

    [Fact]
    public void Everyone_at_zero_is_a_draw_with_its_own_journal_text()
    {
        using var c = new GeoCache();
        var h = Table(c, 2, new { seconds = "30" });
        for (var i = 0; i < MaxTicks && Playing(h); i++) h.Tick(1);
        var fin = Assert.Single(h.Finished);
        Assert.True(fin.Result.Draw);
        Assert.Equal("Де це?: Оля 0, Петро 0 — ніхто нікуди не влучив", fin.Result.Text);
        Assert.Empty(h.Scores);                           // ніхто не ставив — у таблицю нічого
        Assert.Empty(h.Awards);
    }

    [Fact]
    public void Rounds_shrink_to_the_number_of_ready_places_when_the_pool_is_small()
    {
        using var c = new GeoCache(ready: p => p.Id is "g0001" or "g0002" or "g0003");
        var h = Table(c, 1, new { rounds = "10" });
        Assert.Equal(3, h.View(0).GetProperty("rounds").GetInt32());
        var seen = new HashSet<string>();
        for (var r = 0; r < 3; r++)
        {
            Until(h, "guess");
            seen.Add(Current(h, c).Id);
            Until(h, "reveal");
        }
        Assert.Equal(["g0001", "g0002", "g0003"], seen.Order());
        Until(h, "done");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Category_and_level_filters_pick_only_matching_places()
    {
        using var c = new GeoCache();
        var want = c.Bank.Places.Where(p => p.Cat == "castle" && p.Difficulty >= 2).Select(p => p.Id).ToHashSet();
        Assert.Equal(4, want.Count);
        var h = Table(c, 1, new { cat = "castle", level = "hard" });
        Assert.Equal(4, h.View(0).GetProperty("rounds").GetInt32());
        for (var r = 0; r < 4; r++)
        {
            Until(h, "guess");
            Assert.Contains(Current(h, c).Id, want);
            Until(h, "reveal");
        }

        var easy = Table(c, 1, new { cat = "nature,city", level = "easy", rounds = "10" });
        var fits = c.Bank.Places.Where(p => p.Cat is "nature" or "city" && p.Difficulty == 1).Select(p => p.Id).ToHashSet();
        Assert.Equal(fits.Count, easy.View(0).GetProperty("rounds").GetInt32());
        Until(easy, "guess");
        Assert.Contains(Current(easy, c).Id, fits);
    }

    [Fact]
    public void Places_do_not_repeat_across_rematches_until_the_pool_is_exhausted()
    {
        using var c = new GeoCache();
        var h = Table(c, 1);
        var all = new List<string>();
        for (var match = 0; match < 3; match++)
        {
            for (var r = 0; r < 5; r++)
            {
                Until(h, "guess");
                all.Add(Current(h, c).Id);
                Until(h, "reveal");
            }
            Until(h, "done");
            Assert.True(h.Rematch().Ok, h.Reply.Message);
        }
        Assert.Equal(15, all.Distinct().Count());
        // четверта партія: свіжих лишилось 4 < 5 — бачене забувається, партія все одно на 5 раундів
        Assert.Equal(5, h.View(0).GetProperty("rounds").GetInt32());
    }

    [Fact]
    public void Same_seed_and_same_cache_give_the_same_places_photos_and_phrases()
    {
        using var c = new GeoCache();
        List<string> Run(int seed)
        {
            var h = Table(c, 2, seed: seed);
            var log = new List<string>();
            for (var r = 0; r < 5; r++)
            {
                Until(h, "guess");
                var t = Truth(h, c);
                Pin(h, 0, t.X + 40, t.Y + 10);
                Pin(h, 1, t.X - 200, t.Y);
                Until(h, "reveal");
                var rv = Reveal(h);
                log.Add($"{rv.GetProperty("name").GetString()}|{rv.GetProperty("photo").GetProperty("title").GetString()}|{rv.GetProperty("say").GetString()}|{Score(h, 0)}|{Score(h, 1)}");
            }
            return log;
        }
        Assert.Equal(Run(7), Run(7));
        Assert.NotEqual(Run(7), Run(8));
    }

    [Fact]
    public void A_player_who_leaves_is_skipped_and_the_match_goes_on_until_nobody_is_left()
    {
        using var c = new GeoCache();
        var h = Table(c, 3);
        Until(h, "guess");
        Pin(h, 1, 1000, 1000);
        Pin(h, 0, 1100, 1000);
        h.Act(0, "ready");
        h.Act(2, "guess", new { x = 5, y = 5 });
        h.Act(2, "ready");
        h.Leave("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        h.Tick(1);                                        // решта готова — розкриття
        Assert.Equal("reveal", Phase(h));
        Assert.Equal([1], h.View(null).GetProperty("left").EnumerateArray().Select(e => e.GetInt32()));
        Assert.DoesNotContain(1, Reveal(h).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("seat").GetInt32()));
        Assert.Equal(2, Reveal(h).GetProperty("rows").GetArrayLength());

        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);  // Оля грає далі сама
        Until(h, "guess");
        Assert.True(Pin(h, 0, 10, 10).Ok);
        h.Leave("Оля");
        var fin = Assert.Single(h.Finished);
        Assert.Equal("Де це?: гравці розійшлись, партію не дограли", fin.Result.Text);
        Assert.True(fin.Result.Draw);
        Assert.Empty(h.Scores);
    }

    [Fact]
    public void Rematch_rotates_seats_and_starts_clean_but_keeps_the_seen_list()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        var first = new HashSet<string>();
        for (var r = 0; r < 5; r++)
        {
            Until(h, "guess");
            first.Add(Current(h, c).Id);
            var t = Truth(h, c);
            Pin(h, 0, t.X, t.Y);
            Until(h, "reveal");
        }
        Until(h, "done");
        Assert.Equal(25_000, Score(h, 0));
        Assert.True(h.Rematch().Ok);
        Assert.Equal(2, h.Room.Round);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal("Оля", h.NickOf(1));
        var v = h.View(0);
        Assert.Equal("between", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.All(v.GetProperty("scores").EnumerateArray(), s => Assert.Equal(0, s.GetInt64()));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("recap").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("reveal").ValueKind);
        Until(h, "guess");
        Assert.DoesNotContain(Current(h, c).Id, first);
    }

    // ---------------------------------------------------------------------------------------
    // таблиця, черепки, ачівки
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Score_is_reported_only_for_seats_that_pinned_at_least_once()
    {
        using var c = new GeoCache();
        var h = Table(c, 3);
        var round = 0;
        PlayAll(h, c, (s, t) => s == 2 || (s == 0 && round++ == 0) ? t : null, 3);
        Assert.Equal(["Ганна", "Оля"], h.Scores.Select(e => e.Nick).Order(StringComparer.Ordinal));
        Assert.Equal(25_000, h.Scores.Single(e => e.Nick == "Ганна").Score);
        Assert.Equal(5_000, h.Scores.Single(e => e.Nick == "Оля").Score);
        Assert.All(h.Scores, e => Assert.Equal("geo", e.GameId));
    }

    [Fact]
    public void Shards_for_points_are_awarded_per_5000_with_the_round_in_the_reason()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        PlayAll(h, c, (s, t) => s == 0 ? t : (t.X > 2000 ? t.X - 300 : t.X + 300, t.Y), 2);
        var shards = h.Awards.Where(a => a.Reason.StartsWith("points:", StringComparison.Ordinal)).ToList();
        Assert.Contains(shards, a => a is { Nick: "Оля", Shards: 5, Reason: "points:1" });
        var p1 = h.View(null).GetProperty("scores")[1].GetInt64();
        Assert.Contains(shards, a => a.Nick == "Петро" && a.Shards == (int)(p1 / 5000) && a.Reason == "points:1");

        Assert.True(h.Rematch().Ok);
        PlayAll(h, c, (s, t) => t, 2);
        Assert.Equal(2, h.Awards.Count(a => a.Reason == "points:2" && a.Shards == 5));
    }

    [Fact]
    public void Bullseye_and_20k_achievements_are_requested_exactly_once()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        PlayAll(h, c, (s, t) => s == 0 ? t : (t.X + 200, t.Y), 2);
        Assert.Equal(1, h.Awards.Count(a => a is { Nick: "Оля", Reason: "ach:geo-bull", Shards: 0 }));
        Assert.Equal(1, h.Awards.Count(a => a is { Nick: "Оля", Reason: "ach:geo-20k", Shards: 0 }));
        Assert.DoesNotContain(h.Awards, a => a.Nick == "Петро" && a.Reason.StartsWith("ach:", StringComparison.Ordinal));
        Assert.NotNull(Hlechyky.Games.Economy.AchievementCatalog.Get("geo-bull"));
        Assert.NotNull(Hlechyky.Games.Economy.AchievementCatalog.Get("geo-20k"));

        // чотири раунди по 5000 — це 20 000, але не «з п'яти й більше раундів»
        using var four = new GeoCache(ready: p => p.Id is "g0001" or "g0002" or "g0003" or "g0004");
        var f = Table(four, 1);
        PlayAll(f, four, (s, t) => t, 1);
        Assert.Equal(20_000, f.View(0).GetProperty("scores")[0].GetInt64());
        Assert.Contains(f.Awards, a => a.Reason == "ach:geo-bull");
        Assert.DoesNotContain(f.Awards, a => a.Reason == "ach:geo-20k");
    }

    // ---------------------------------------------------------------------------------------
    // приховане й дріт
    // ---------------------------------------------------------------------------------------

    static IEnumerable<string> Strings(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    yield return p.Name;
                    foreach (var s in Strings(p.Value)) yield return s;
                }
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) foreach (var s in Strings(x)) yield return s;
                break;
            case JsonValueKind.String:
                yield return e.GetString()!;
                break;
            case JsonValueKind.Number:
                yield return e.GetRawText();
                break;
        }
    }

    [Fact]
    public void During_guess_another_seat_sees_no_coordinates_of_my_pin()
    {
        using var c = new GeoCache();
        var h = Table(c, 3);
        Until(h, "guess");
        Pin(h, 0, 1234, 2345);
        h.Tick(1);
        foreach (int? seat in new int?[] { 1, 2, null })
        {
            var v = h.View(seat);
            var all = Strings(v).ToList();
            Assert.DoesNotContain("1234", all);
            Assert.DoesNotContain("2345", all);
            Assert.Equal(JsonValueKind.Null, v.GetProperty("my").ValueKind);
            Assert.Equal([0], v.GetProperty("pinned").EnumerateArray().Select(e => e.GetInt32()));
        }
        Assert.Equal(1234, h.View(0).GetProperty("my").GetProperty("x").GetInt32());
        foreach (var f in h.Outbox.OfType<RoomFrame>())
        {
            var all = Strings(Views.Json(f.Frame)).ToList();
            Assert.DoesNotContain("1234", all);
            Assert.DoesNotContain("2345", all);
        }
    }

    [Fact]
    public void The_watcher_view_has_no_pins_no_truth_and_my_is_null_until_reveal()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        foreach (var phase in new[] { "between", "guess" })
        {
            Until(h, phase);
            var place = Current(h, c);
            if (phase == "guess") Pin(h, 0, 700, 800);
            foreach (int? seat in new int?[] { null, 0, 1 })
            {
                var v = h.View(seat);
                var all = Strings(v).ToHashSet();
                foreach (var secret in new[] { "lat", "lon", "name", "region", "title", "author", "wikidata", place.Name, place.Region, place.Photos[0].Title, place.Id })
                    Assert.DoesNotContain(secret, all);
                Assert.Equal(JsonValueKind.Null, v.GetProperty("reveal").ValueKind);
                Assert.Equal(JsonValueKind.Null, v.GetProperty("recap").ValueKind);
            }
            Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("my").ValueKind);
        }
        Until(h, "reveal");
        var w = h.View(null);
        Assert.Equal(Current(h, c).Name, w.GetProperty("reveal").GetProperty("name").GetString());
        Assert.Equal(700, w.GetProperty("reveal").GetProperty("rows").EnumerateArray().First(r => r.GetProperty("seat").GetInt32() == 0).GetProperty("x").GetInt32());
        Assert.Equal(JsonValueKind.Null, w.GetProperty("my").ValueKind);
    }

    static readonly string[] FrameKeys = ["ph", "r", "ends", "pin", "rdy", "nxt"];

    [Fact]
    public void The_frame_never_carries_coordinates_and_is_under_150_bytes()
    {
        using var c = new GeoCache();
        var h = Table(c, 10);
        Until(h, "guess");
        for (var s = 0; s < 10; s++) { Pin(h, s, 3000 + s, 2000 + s); h.Act(s, "ready"); }
        h.Tick(1);
        Until(h, "reveal");
        for (var s = 0; s < 10; s++) h.Act(s, "next");
        h.Tick(1);
        var frames = h.Outbox.OfType<RoomFrame>().ToList();
        Assert.NotEmpty(frames);
        foreach (var f in frames)
        {
            var text = Views.Text(f.Frame);
            Assert.True(text.Length <= 150, $"{text.Length} Б: {text}");
            var json = Views.Json(f.Frame);
            Assert.Equal(FrameKeys, json.EnumerateObject().Select(p => p.Name));
            Assert.DoesNotContain("3000", text);
            Assert.DoesNotContain("2000", text);
        }
    }

    static readonly string[] ViewKeys = ["phase", "round", "rounds", "endsAt", "phaseMs", "seconds", "hints", "photo",
        "pinned", "ready", "next", "my", "reveal", "scores", "left", "result", "recap", "turn"];

    [Fact]
    public void The_view_shape_matches_the_spec_in_every_phase()
    {
        using var c = new GeoCache();
        var h = Table(c, 2, start: false);
        void Shape(string phase)
        {
            foreach (int? seat in new int?[] { 0, 1, null })
            {
                var v = h.View(seat);
                Assert.Equal(phase, v.GetProperty("phase").GetString());
                Assert.Equal(ViewKeys, v.EnumerateObject().Select(p => p.Name));
                Assert.Equal(10, v.GetProperty("scores").GetArrayLength());
                Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
                if (v.GetProperty("reveal") is { ValueKind: JsonValueKind.Object } rv)
                {
                    Assert.Equal(["x", "y", "lat", "lon", "name", "region", "cat", "wikidata", "photo", "say", "rows"], rv.EnumerateObject().Select(p => p.Name));
                    Assert.Equal(["title", "author", "license", "licenseUrl", "page"], rv.GetProperty("photo").EnumerateObject().Select(p => p.Name));
                    Assert.All(rv.GetProperty("rows").EnumerateArray(), r =>
                        Assert.Equal(["seat", "x", "y", "km", "points", "best", "bull"], r.EnumerateObject().Select(p => p.Name)));
                }
                if (v.GetProperty("recap") is { ValueKind: JsonValueKind.Array } rc)
                    Assert.All(rc.EnumerateArray(), r =>
                        Assert.Equal(["name", "region", "photo", "best", "km", "points"], r.EnumerateObject().Select(p => p.Name)));
            }
        }
        Shape("lobby");
        var lobby = h.View(null);
        Assert.Equal(0, lobby.GetProperty("round").GetInt32());
        Assert.Equal(JsonValueKind.Null, lobby.GetProperty("photo").ValueKind);
        Assert.Equal(JsonValueKind.Null, lobby.GetProperty("endsAt").ValueKind);
        h.Start();
        Shape("between");
        Until(h, "guess");
        Pin(h, 0, 10, 10);
        Shape("guess");
        Assert.Equal(JsonValueKind.String, h.View(0).GetProperty("endsAt").ValueKind);
        Until(h, "reveal");
        Shape("reveal");
        PlayAll(h, c, (s, t) => t, 2);
        Shape("done");
        Assert.Equal(JsonValueKind.Array, h.View(null).GetProperty("recap").ValueKind);
    }

    [Fact]
    public void Between_phase_carries_the_photo_url_but_no_reveal_and_no_pins()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        var v = h.View(1);
        Assert.Matches("^/api/games/geo/[0-9a-f]{24}\\.jpg$", v.GetProperty("photo").GetString()!);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("reveal").ValueKind);
        Assert.Empty(v.GetProperty("pinned").EnumerateArray());
        var first = v.GetProperty("photo").GetString();
        Until(h, "guess");
        Assert.Equal(first, h.View(1).GetProperty("photo").GetString());     // той самий токен до кінця раунду
        Pin(h, 0, 5, 5);
        Until(h, "reveal");
        Assert.Equal(first, h.View(1).GetProperty("photo").GetString());
        Until(h, "between");
        var second = h.View(1);
        Assert.NotEqual(first, second.GetProperty("photo").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("reveal").ValueKind);
        Assert.Empty(second.GetProperty("pinned").EnumerateArray());
        // файл за токеном — справжній JPEG без EXIF
        var path = c.Photos.Resolve(second.GetProperty("photo").GetString()!["/api/games/geo/".Length..^4])!;
        var bytes = File.ReadAllBytes(path);
        Assert.False(GeoImage.HasSegment(bytes, 0xE1));
        Assert.Equal(0xD8, bytes[1]);
    }

    [Fact]
    public void The_client_payload_shape_is_exactly_what_the_server_reads()
    {
        // модуль шле рівно це (web/games/geo.js) — тримаємо обидва боки одним тестом
        var js = File.ReadAllText(Paths.Resolve("web/games/geo.js"));
        Assert.Contains("ctx.act('guess', { x: ", js);
        Assert.Contains("ctx.act('ready')", js);
        Assert.Contains("ctx.act('next')", js);

        using var c = new GeoCache();
        var h = Table(c, 2);
        Until(h, "guess");
        Assert.True(h.Act(0, "guess", new { x = 1812, y = 1188 }).Ok);
        Assert.True(h.Act(1, "guess", new { x = "1812", y = "1188" }).Ok);
        Assert.True(h.Act(0, "ready", null).Ok);
        Assert.True(h.Act(1, "ready", new { }).Ok);
        h.Tick(1);
        Assert.Equal("reveal", Phase(h));
        Assert.True(h.Act(0, "next", null).Ok);
        Assert.True(h.Act(1, "next").Ok);
    }

    [Fact]
    public void A_quiet_tick_sends_nothing_and_a_round_costs_a_handful_of_messages()
    {
        using var c = new GeoCache();
        var h = Table(c, 3);
        Until(h, "guess");
        var mark = h.Outbox.Count;
        h.Tick(4);
        Assert.Equal(mark, h.Outbox.Count);               // тиша — нуль байтів на дроті

        Pin(h, 0, 10, 10);
        Pin(h, 0, 20, 20);                                // пересунув — кадру вже не треба
        Pin(h, 1, 30, 30);
        h.Tick(1);
        Assert.Single(h.Outbox.Skip(mark).OfType<RoomFrame>());
        Assert.Empty(h.Outbox.Skip(mark).OfType<RoomViews>());
        Pin(h, 1, 40, 40);
        h.Tick(1);
        Assert.Single(h.Outbox.Skip(mark).OfType<RoomFrame>());

        // повний раунд: між → гадаємо → розкриття → між
        var start = h.Outbox.Count;
        for (var s = 0; s < 3; s++) { Pin(h, s, 100, 100); h.Act(s, "ready"); h.Tick(1); }
        Until(h, "reveal");
        for (var s = 0; s < 3; s++) { h.Act(s, "next"); h.Tick(1); }
        Until(h, "guess");
        var sent = h.Outbox.Skip(start).ToList();
        Assert.InRange(sent.OfType<RoomViews>().Count(), 1, 4);
        Assert.InRange(sent.OfType<RoomFrame>().Count(), 1, 3 + 3 * 3);
    }

    // ---------------------------------------------------------------------------------------
    // тренування
    // ---------------------------------------------------------------------------------------

    static RoomHarness Solo(GeoCache c, int seed = 42)
    {
        var h = new RoomHarness("geo-solo", seed: seed, services: c.Services);
        Assert.True(h.Solo("Оля").Ok, h.Reply.Message);
        return h;
    }

    [Fact]
    public void Solo_opens_and_starts_immediately_with_five_rounds_of_sixty_seconds()
    {
        using var c = new GeoCache();
        var h = Solo(c);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        var v = h.View(0);
        Assert.Equal("between", v.GetProperty("phase").GetString());
        Assert.Equal(5, v.GetProperty("rounds").GetInt32());
        Assert.Equal(60, v.GetProperty("seconds").GetInt32());
        Assert.Equal("full", v.GetProperty("hints").GetString());
        Assert.Equal(1, v.GetProperty("scores").GetArrayLength());
        Until(h, "guess");
        Assert.Equal(60_000, h.View(0).GetProperty("phaseMs").GetInt32());
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), j => j.Text.Contains("сіли грати"));
        Assert.Equal("1", h.Room.Game.SeatName(0));
    }

    [Fact]
    public void Solo_with_no_photos_finishes_at_once_with_an_explanation_and_rematch_works_later()
    {
        using var c = new GeoCache(ready: _ => false);
        var h = Solo(c);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("Де це? Тренування: фото ще качаються — спробуй за пів хвилини", h.Room.Result!.Text);
        Assert.False(h.Rematch().Ok);
        Assert.Equal(GeoMatch.NoPhotos, h.Reply.Message);
        foreach (var p in c.Bank.Places.Take(6)) c.Add(p);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(5, h.View(0).GetProperty("rounds").GetInt32());
    }

    [Fact]
    public void Solo_journal_line_appears_only_from_15000_points_and_score_only_after_a_pin()
    {
        using var c = new GeoCache();
        var h = Solo(c);
        PlayAll(h, c, (s, t) => t, 1);
        Assert.Equal("Де це?: Оля — 25 000 очок у тренуванні", h.Room.Result!.Text);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "Де це?: Оля — 25 000 очок у тренуванні");
        var e = Assert.Single(h.Scores);
        Assert.Equal(("geo-solo", 25_000.0, ScoreOrder.HigherIsBetter), (e.GameId, e.Score, e.Order));
        Assert.Contains(h.Awards, a => a is { Reason: "points:1", Shards: 5 });

        var journals = h.Outbox.OfType<Journal>().Count();
        Assert.True(h.Rematch().Ok);
        PlayAll(h, c, (s, t) => (t.X < 2000 ? t.X + 600 : t.X - 600, t.Y), 1);   // ~200 км щоразу
        Assert.Equal(2, h.Scores.Count);
        Assert.InRange(h.Scores[1].Score, 1, 14_999);
        Assert.Equal(journals, h.Outbox.OfType<Journal>().Count());

        Assert.True(h.Rematch().Ok);
        for (var i = 0; i < MaxTicks && Playing(h); i++) h.Tick(1);      // кинуте тренування доходить само
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(2, h.Scores.Count);                                   // без шпильок — без запису
        Assert.Equal(journals, h.Outbox.OfType<Journal>().Count());
    }

    [Fact]
    public void Solo_next_by_the_only_player_ends_the_reveal_immediately_and_ready_ends_the_guess()
    {
        using var c = new GeoCache();
        var h = Solo(c);
        Until(h, "guess");
        Pin(h, 0, 100, 100);
        h.Act(0, "ready");
        h.Tick(1);
        Assert.Equal("reveal", Phase(h));
        Assert.True(h.Act(0, "next").Ok);
        h.Tick(1);
        Assert.Equal("between", Phase(h));
        Assert.Equal(2, h.View(0).GetProperty("round").GetInt32());
    }
}

/// <summary>Швидкодія «Де це?»: тик на десятьох і розміри видів/кадру на дроті. Окремо від решти — стінний годинник.</summary>
[Collection(SerialPerf.Name)]
public class GeoPerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_three_thousand_ticks_with_ten_pinning_players_take_under_a_second()
    {
        using var c = new GeoCache();
        var h = GeoTests.Table(c, 10, new { rounds = "10", seconds = "30" });
        var rng = new Random(5);
        var sw = new Stopwatch();
        var ticks = 0;
        var pinnedRound = -1;
        var matches = 1;
        while (ticks < 3000)
        {
            if (h.Room.Status == RoomStatus.Finished)
            {
                Assert.True(h.Rematch().Ok);
                matches++;
                pinnedRound = -1;
            }
            var v = h.View(null);
            var phase = v.GetProperty("phase").GetString();
            var round = v.GetProperty("round").GetInt32();
            if (phase == "guess" && pinnedRound != round)
            {
                pinnedRound = round;
                for (var s = 0; s < 10; s++)
                {
                    h.Act(s, "guess", new { x = rng.Next(GeoMap.W), y = rng.Next(GeoMap.H) });
                    if (s % 3 != 0) h.Act(s, "ready");      // хтось думає до кінця — тикам є що перевіряти
                }
            }
            sw.Start();
            h.Tick(1);
            sw.Stop();
            ticks++;
        }
        output.WriteLine($"3000 тиків на десятьох (з кадрами й розкриттями, {matches} партій): {sw.Elapsed.TotalMilliseconds:F1} мс, у середньому {sw.Elapsed.TotalMilliseconds / 3000:F4} мс");

        // чистий Game.Tick без каркаса: типовий тик у фазі відгадування
        var h2 = GeoTests.Table(c, 10, new { seconds = "60" });
        GeoTests.Until(h2, "guess");
        var g2 = h2.Room.Game;
        var sw2 = Stopwatch.StartNew();
        lock (h2.Room.Sync) for (var i = 0; i < 100_000; i++) g2.Tick();
        sw2.Stop();
        output.WriteLine($"Game.Tick у спокої: {sw2.Elapsed.TotalMilliseconds * 1000 / 100_000:F3} мкс");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"{sw.Elapsed.TotalMilliseconds} мс");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_view_and_frame_sizes_stay_within_budget()
    {
        using var c = new GeoCache();
        var h = GeoTests.Table(c, 10, new { rounds = "10" });
        int maxReveal = 0, maxGuess = 0, maxFrame = 0, done = 0;
        var rng = new Random(3);
        while (GeoTests.Playing(h))
        {
            GeoTests.Until(h, "guess");
            if (!GeoTests.Playing(h)) break;
            for (var s = 0; s < 10; s++) { h.Act(s, "guess", new { x = rng.Next(GeoMap.W), y = rng.Next(GeoMap.H) }); h.Act(s, "ready"); }
            maxGuess = Math.Max(maxGuess, Views.Text(h.Room.Game.View(0)).Length);
            h.Tick(1);
            maxReveal = Math.Max(maxReveal, Views.Text(h.Room.Game.View(0)).Length);
            for (var s = 0; s < 10; s++) h.Act(s, "next");
            h.Tick(1);
        }
        done = Views.Text(h.Room.Game.View(0)).Length;
        foreach (var f in h.Outbox.OfType<RoomFrame>()) maxFrame = Math.Max(maxFrame, Views.Text(f.Frame).Length);
        output.WriteLine($"вид guess ≤ {maxGuess} Б, вид reveal на десятьох ≤ {maxReveal} Б, done з 10 раундами {done} Б, кадр ≤ {maxFrame} Б");
        Assert.InRange(maxGuess, 1, 900);
        Assert.InRange(maxReveal, 1, 3500);
        Assert.InRange(done, 1, 6000);
        Assert.InRange(maxFrame, 1, 150);
    }
}
