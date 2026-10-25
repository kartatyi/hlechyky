using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using static Hlechyky.Tests.Games.FuraTests;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Чумаки», крок 2c (specs/fura.md, «Зіткнення (2c)»): опція столу <c>collide</c>, пересилання удару лише адресату
/// (поле <c>h</c> кадру), ліміт частоти пари, відмови (далеко, бокси, привиди) і поблажка судді адресату.
/// </summary>
public sealed class FuraCrashTests
{
    /// <summary>Стіл з опцією collide (null — типова): Оля створила, решта сіла.</summary>
    static RoomHarness Table(string? collide, params string[] nicks)
    {
        RoomHarness? h = null;
        h = new RoomHarness("fura", collide is null ? null : new { collide }, seed: 42, services: Garage(() => h!.Store));
        foreach (var n in nicks.Length > 0 ? nicks : ["Оля", "Петро"]) Assert.True(h.Join(n).Ok, h.Reply.Message);
        return h;
    }

    /// <summary>Фура на (x, z), курсом на північ: причіп позаду на 8 м. Поза свіжа — для судді й удару.</summary>
    static void At(RoomHarness h, int seat, double x, double z) =>
        h.Input(seat, "pose", new { x, z, v = 0.0, c = true, rx = x, rz = z - 8, ry = 0.0, h = 0.0 });

    static JsonElement Frame(RoomHarness h)
    {
        h.Outbox.Clear();
        h.Tick();
        return Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);
    }

    /// <summary>Записи <c>h</c> кадру по 8 чисел; поля нема — порожньо.</summary>
    static int[][] Hits(JsonElement f) =>
        f.TryGetProperty("h", out var h) ? [.. h.EnumerateArray().Select(x => x.GetInt32()).Chunk(8)] : [];

    // ---------- опція ----------

    [Fact]
    public void The_table_option_is_ghosts_by_default_and_crash_only_when_chosen()
    {
        var opt = Assert.Single(RoomHarness.NewRegistry().Info("fura")!.Options!);
        Assert.Equal((Fura.CollideKey, Fura.Ghosts), (opt.Key, opt.Default));
        Assert.Equal([Fura.Ghosts, Fura.Crash], opt.Values.Select(v => v.Value));

        Assert.Equal("ghosts", Table(null).View(0).GetProperty("collide").GetString());
        Assert.Equal("ghosts", Table("щось чуже").View(0).GetProperty("collide").GetString());
        var crash = Table("crash");
        Assert.Equal("crash", crash.View(0).GetProperty("collide").GetString());
        Assert.Equal("crash", crash.View(null).GetProperty("collide").GetString());   // глядач теж бачить
        Assert.Equal("crash", crash.Room.Options[Fura.CollideKey]);                   // лобі — з опцій столу
    }

    [Fact]
    public void The_option_does_not_change_mid_game()
    {
        var h = Table("crash");
        var r = h.Rooms.Reconfigure(h.RoomId, "Оля", new Dictionary<string, string> { [Fura.CollideKey] = Fura.Ghosts });
        Assert.False(r.Reply.Ok);
        Assert.Equal("crash", h.View(0).GetProperty("collide").GetString());
    }

    [Fact]
    public void A_ghost_table_refuses_hits_and_the_frame_has_no_h()
    {
        var h = Table(null);
        At(h, 0, 0, 0);
        At(h, 1, 0, 9);
        Assert.False(h.Act(0, "hit", new { to = 1, ix = 0, iz = 20000 }).Ok);
        Assert.False(Frame(h).TryGetProperty("h", out _));
    }

    // ---------- пересилання ----------

    [Fact]
    public void A_hit_reaches_only_its_addressee_for_three_ticks()
    {
        var h = Table("crash", "Оля", "Петро", "Іра");
        At(h, 0, 0, 0);
        At(h, 1, 0, 9);
        At(h, 2, 4, -3);
        h.Tick();
        Assert.True(h.Act(0, "hit", new { to = 1, ix = 1500.4, iz = 20000, at = 1, ox = -1.04, oz = 3.5 }).Ok);

        // [to, from, id, at, ix, iz, ox·10, oz·10] — адресат Петро (1), б'є Оля (0); Іра (2) записів собі не має
        var first = Hits(Frame(h));
        var hit = Assert.Single(first);
        Assert.Equal([1, 0], hit[..2]);
        Assert.Equal([1, 1500, 20000, -10, 35], hit[3..]);
        Assert.DoesNotContain(first, x => x[0] != 1);
        // той самий удар (той самий id) — ще два кадри (каркас може згорнути кадр), далі зникає
        Assert.Equal(hit, Assert.Single(Hits(Frame(h))));
        Assert.Equal(hit, Assert.Single(Hits(Frame(h))));
        Assert.Empty(Hits(Frame(h)));
        // своя фура в кадрі — без змін: p і q на місці
        Assert.Equal(2, Frame(h).GetProperty("v").GetInt32());
    }

    [Fact]
    public void A_huge_impulse_is_cut_to_the_ceiling_keeping_its_direction()
    {
        var h = Table("crash");
        At(h, 0, 0, 0);
        At(h, 1, 0, 9);
        Assert.True(h.Act(0, "hit", new { to = 1, ix = 3e6, iz = 4e6, ox = 99, oz = -99 }).Ok);
        var hit = Assert.Single(Hits(Frame(h)));
        Assert.Equal(0, hit[3]);                                        // без at — тягач
        Assert.Equal((72000, 96000), (hit[4], hit[5]));                 // 3:4, довжина 120 000
        Assert.Equal((80, -80), (hit[6], hit[7]));                      // точка — у межах ±8 м
    }

    [Fact]
    public void One_pair_is_forwarded_at_most_four_times_a_second_and_other_pairs_do_not_wait()
    {
        var h = Table("crash", "Оля", "Петро", "Іра");
        At(h, 0, 0, 0);
        At(h, 1, 0, 9);
        At(h, 2, -5, 0);
        h.Tick();
        Assert.True(h.Act(0, "hit", new { to = 1, iz = 9000 }).Ok);
        h.Clock.AdvanceMs(100);
        Assert.True(h.Act(0, "hit", new { to = 1, iz = 9001 }).Ok);    // за 0,1 с — мовчки відкинуто
        Assert.True(h.Act(0, "hit", new { to = 2, ix = -9000 }).Ok);   // інша пара — своя черга
        Assert.True(h.Act(1, "hit", new { to = 0, iz = -9000 }).Ok);   // і зворотна пара теж
        var hits = Hits(Frame(h));
        Assert.Equal([(1, 0, 9000), (2, 0, 0), (0, 1, -9000)], hits.Select(x => (x[0], x[1], x[5])));

        h.Clock.AdvanceMs(150);                                          // 0,25 с від першого (тик теж ішов)
        Assert.True(h.Act(0, "hit", new { to = 1, iz = 9002 }).Ok);
        Assert.Contains(Hits(Frame(h)), x => x[5] == 9002);
    }

    [Fact]
    public void A_hit_is_refused_far_away_at_the_docks_on_itself_and_on_an_empty_seat()
    {
        var h = Table("crash");
        At(h, 0, 0, 0);
        At(h, 1, 0, 40);
        Assert.Equal("Задалеко для удару", h.Act(0, "hit", new { to = 1, iz = 9000 }).Message);
        Assert.False(h.Act(0, "hit", new { to = 0, iz = 9000 }).Ok);
        Assert.False(h.Act(0, "hit", new { to = 5, iz = 9000 }).Ok);
        Assert.False(h.Act(0, "hit", new { iz = 9000 }).Ok);

        // двір депо: Петро чіпляється біля боксу 2 — Оля його не штовхне, і він її теж
        var dock = Map.Place("dvir")!.Dock("2")!;
        At(h, 1, dock.X, dock.Z + 8);
        At(h, 0, dock.X, dock.Z + 28);
        Assert.Equal("Біля боксів фури не б'ються", h.Act(0, "hit", new { to = 1, iz = 9000 }).Message);
        Assert.Equal("Біля боксів фури не б'ються", h.Act(1, "hit", new { to = 0, iz = -9000 }).Message);

        // поза старша за 3 с — удару нема
        At(h, 0, 0, 0);
        At(h, 1, 0, 9);
        h.Clock.Advance(3.5);
        At(h, 0, 0, 0);
        Assert.False(h.Act(0, "hit", new { to = 1, iz = 9000 }).Ok);
        Assert.Empty(Hits(Frame(h)));

        // а решта двору тверда: двоє щойно сіли на місці старту депо (35 м від боксів) — удар іде
        At(h, 0, -25, -350);
        At(h, 1, -25, -340);
        Assert.True(Map.Place("dvir")!.Yard.Contains(-25, -350));
        Assert.True(h.Act(0, "hit", new { to = 1, iz = 9000 }).Ok);
        Assert.Contains(Hits(Frame(h)), x => x[5] == 9000);
    }

    [Fact]
    public void The_addressee_leaving_drops_the_hit_from_the_frame()
    {
        var h = Table("crash", "Оля", "Петро", "Іра");
        At(h, 0, 0, 0);
        At(h, 1, 0, 9);
        At(h, 2, 0, -12);
        Assert.True(h.Act(0, "hit", new { to = 1, iz = 9000 }).Ok);
        Assert.True(h.Leave("Петро").Ok);
        Assert.Empty(Hits(Frame(h)));
    }

    // ---------- суддя ----------

    [Fact]
    public void The_judge_forgives_a_shove_for_three_seconds()
    {
        var t0 = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
        FuraPose P(double z) => new(0, z, 0, true, 100, 0, z - 8, 0);
        var j = FuraJudge.Begin(t0, P(0), t0);
        j.Shove(t0);
        // 10 м за 0,2 с = 180 км/год; мінус 3 м — 126: не порушення
        Assert.False(j.Feed(P(10), t0.AddSeconds(0.2)));
        Assert.Equal(0, j.Violations);
        // поблажка ще діє на кожен вимір у межах 3 с
        Assert.False(j.Feed(P(20), t0.AddSeconds(0.4)));
        Assert.Equal(0, j.Violations);
        // а після 3 с — звичайний суддя
        j.Feed(P(20), t0.AddSeconds(3.2));
        Assert.True(j.Feed(P(30), t0.AddSeconds(3.4)));
        Assert.Equal(1, j.Violations);
    }

    [Fact]
    public void A_forwarded_hit_softens_the_addressees_judge_and_not_the_strikers()
    {
        var h = Table("crash");
        Assert.True(h.Act(1, "take", new { id = 1 }).Ok);
        Assert.True(h.Act(0, "take", new { id = 1 }).Ok);
        At(h, 0, 0, 0);
        At(h, 1, 0, 9);
        h.Clock.AdvanceMs(200);
        Assert.True(h.Act(0, "hit", new { to = 1, iz = 20000 }).Ok);
        // обидві фури «стрибнули» на 10 м за 0,2 с: Петра штовхнули — пробачено, Олю ні
        At(h, 1, 0, 19);
        At(h, 0, 0, 10);
        h.Tick();
        Assert.Equal(0, h.View(1).GetProperty("trip").GetProperty("violations").GetInt32());
        Assert.Equal(1, h.View(0).GetProperty("trip").GetProperty("violations").GetInt32());
    }
}
