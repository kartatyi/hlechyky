using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Глекомети, прохід №3: емоції над хатою, підсумок серії, снаряди-приколи, погода й мапи, «Залп».</summary>
public partial class GlekometTests
{
    // =============================================================================================
    // 100. Емоції над хатою
    // =============================================================================================

    [Fact]
    public void Emotion_flies_in_a_frame_to_everybody_and_is_throttled()
    {
        var h = Table(3);
        Ready(h);
        var other = (Game(h).Turn + 1) % 3;
        var mark = h.Outbox.Count;
        h.Input(other, "emo", new { e = 2 });
        h.Tick(1);
        var em = Frames(h, mark).Single(f => Views.Has(f, "em")).GetProperty("em");
        Assert.Equal($"[[{other},2]]", em.GetRawText());

        mark = h.Outbox.Count;
        h.Input(other, "emo", new { e = 1 });                            // одразу вдруге — мовчки ні
        h.Tick(1);
        Assert.DoesNotContain(Frames(h, mark), f => Views.Has(f, "em"));
        h.Tick(Glekomet.EmoGap);
        mark = h.Outbox.Count;
        h.Input(other, "emo", 1);                                        // голе число теж
        h.Tick(1);
        Assert.Contains(Frames(h, mark), f => Views.Has(f, "em"));
    }

    [Fact]
    public void Emotion_is_refused_for_bad_payload_and_does_not_touch_the_game()
    {
        var h = Table(2);
        Ready(h);
        var before = V(h).GetRawText();
        Assert.False(h.Act(1, "emo", new { e = 9 }).Ok);
        Assert.False(h.Act(1, "emo", new { x = 1 }).Ok);
        Assert.True(h.Act(1, "emo", new { e = 0 }).Ok);
        Assert.Equal(before, V(h).GetRawText());                         // вид той самий: емоція лише в кадрі
    }

    // =============================================================================================
    // 104. Підсумок серії
    // =============================================================================================

    [Fact]
    public void Series_sums_damage_and_keeps_the_best_shot_across_rematches()
    {
        var h = Table(2);
        Ready(h);
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("series").ValueKind);
        Flatten(h, 100, (0, 200), (1, 700));
        Core(h).Huts[1].Hp = 30;
        HitHut(h, 0, 1);                                                 // Оля розбиває Петра: 30
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var s = V(h).GetProperty("series");
        Assert.Equal(1, s.GetProperty("games").GetInt32());
        Assert.Equal(30, s.GetProperty("rows")[0].GetProperty("dmg").GetInt32());
        Assert.Equal("Оля", s.GetProperty("best").GetProperty("nick").GetString());

        Assert.True(h.Rematch().Ok, h.Reply.Message);                   // місця обернулись: Оля — 1
        Ready(h);
        Flatten(h, 100, (0, 200), (1, 700));
        Core(h).Huts[1].Hp = 20;
        if (Game(h).Turn != 0) ShootAway(h);
        HitHut(h, 0, 1);                                                 // Петро розбиває Олю: 20
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        s = V(h).GetProperty("series");
        Assert.Equal(2, s.GetProperty("games").GetInt32());
        Assert.Equal(30, s.GetProperty("rows")[1].GetProperty("dmg").GetInt32());   // Оля — уже на місці 1
        Assert.Equal(20, s.GetProperty("rows")[0].GetProperty("dmg").GetInt32());
        Assert.Equal(30, s.GetProperty("best").GetProperty("dmg").GetInt32());
    }

    // =============================================================================================
    // 101. Снаряди-приколи
    // =============================================================================================

    static (int X, int Y, string Kind)? Shoot(GlekometCore core, int seat, int a, int p, int w, int wind = 0)
    {
        core.Fire(seat, a, p, w, wind);
        return Fly(core);
    }

    [Fact]
    public void Jokes_are_off_by_default_and_the_store_grows_to_ten_with_the_option()
    {
        var h = Table(2);
        Ready(h);
        Assert.Equal(6, V(h).GetProperty("inv")[0].GetArrayLength());
        Assert.False(Fire(h, Game(h).Turn, 45, 50, GlekometCore.Rooster).Ok);
        Assert.Equal("Такої зброї в коморі нема", h.Reply.Message);

        var j = Table(2, options: new { arms = "jokes" });
        Ready(j);
        Assert.Equal([-1, 2, 1, 2, 1, 2, 2, 2, 1, 1], V(j).GetProperty("inv")[0].EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(10, V(j).GetProperty("kinds").GetInt32());
        Assert.True(Fire(j, Game(j).Turn, 45, 50, GlekometCore.Rooster).Ok, j.Reply.Message);
    }

    [Fact]
    public void Rooster_flies_flatter_than_a_pot_and_the_wind_carries_it_twice_as_far()
    {
        var pot = Bare();
        pot.Place(0, 100);
        var rooster = Bare();
        rooster.Place(0, 100);
        var a = Shoot(pot, 0, 30, 40, GlekometCore.Pot)!.Value;
        var b = Shoot(rooster, 0, 30, 40, GlekometCore.Rooster)!.Value;
        Assert.True(b.X > a.X + 100, $"{a.X} → {b.X}");                   // майже без тяжіння — далі

        var calm = Bare(); calm.Place(0, 100);
        var windy = Bare(); windy.Place(0, 100);
        var c0 = Shoot(calm, 0, 60, 30, GlekometCore.Rooster)!.Value;
        var c1 = Shoot(windy, 0, 60, 30, GlekometCore.Rooster, wind: -5)!.Value;
        var p0 = Bare(); p0.Place(0, 100);
        var p1 = Bare(); p1.Place(0, 100);
        var d0 = Shoot(p0, 0, 60, 30, GlekometCore.Pot)!.Value;
        var d1 = Shoot(p1, 0, 60, 30, GlekometCore.Pot, wind: -5)!.Value;
        Assert.True(c0.X - c1.X > d0.X - d1.X, $"півень {c0.X - c1.X}, глек {d0.X - d1.X}");
    }

    [Fact]
    public void Honey_sticks_the_hut_for_its_next_turn()
    {
        var h = Table(2, options: new { arms = "jokes" });
        Ready(h);
        var s = Game(h).Turn;
        var other = 1 - s;
        Flatten(h, 100, (s, 200), (other, 700));
        HitHut(h, s, other, GlekometCore.Honey);
        Assert.Equal(other, Game(h).Turn);
        Assert.True(Core(h).Huts[other].Stuck);
        Assert.False(h.Act(other, "move", new { dir = 1 }).Ok);
        Assert.Equal("Хата в меду — цей хід не рушить", h.Reply.Message);
        Assert.True(V(h).GetProperty("huts")[other].GetProperty("stuck").GetBoolean());
        Assert.Contains(V(h).GetProperty("log").EnumerateArray(), l => l.GetString()!.Contains("липко") || l.GetString()!.Contains("прилипли"));
    }

    [Fact]
    public void Twister_throws_huts_away_from_the_blast()
    {
        var core = Bare();
        core.Place(0, 100);
        core.Place(1, 500);
        core.BeginShot(0, GlekometCore.Twister);
        core.Explode(GlekometCore.Twister, 470, 100, -1);
        Assert.True(core.Huts[1].X > 520, core.Huts[1].X.ToString());
        Assert.NotEqual(0, core.ShovedBy[0] & 2);
        Assert.Equal(100, core.Huts[0].X);                                // далеко — не зачепило
    }

    [Fact]
    public void Horseshoe_pulls_a_rivals_shell_but_not_its_owners()
    {
        (int X, int Y, string Kind) Pot(bool magnet, int owner)
        {
            var core = Bare();
            core.Place(0, 100);
            core.Place(1, 900);
            if (magnet)
            {
                core.BeginShot(owner, GlekometCore.Horseshoe);
                core.Explode(GlekometCore.Horseshoe, 520, 100, -1);
            }
            return Shoot(core, 0, 45, 55, GlekometCore.Pot)!.Value;
        }
        var free = Pot(false, 1);
        var pulled = Pot(true, 1);
        var own = Pot(true, 0);
        Assert.Equal(free, own);                                           // своя підкова свого глека не чіпає
        Assert.True(Math.Abs(pulled.X - 520) < Math.Abs(free.X - 520), $"{free.X} → {pulled.X}");
    }

    [Fact]
    public void Horseshoe_lasts_until_its_owners_next_turn()
    {
        var h = Table(2, options: new { arms = "jokes" });
        Ready(h);
        var s = Game(h).Turn;
        Flatten(h, 100, (s, 200), (1 - s, 700));
        Assert.True(Fire(h, s, 45, 40, GlekometCore.Horseshoe).Ok, h.Reply.Message);
        Settle(h);
        Assert.Equal(1, V(h).GetProperty("mag").GetArrayLength());
        ShootAway(h);                                                      // суперник стріляє — підкова ще лежить
        Assert.Equal(s, Game(h).Turn);
        Assert.Equal(0, V(h).GetProperty("mag").GetArrayLength());        // свій хід настав — відслужила
    }

    // =============================================================================================
    // 102. Погода й мапи
    // =============================================================================================

    [Fact]
    public void Fair_has_a_pond_in_the_middle_and_nobody_stands_in_it()
    {
        for (var n = 2; n <= 6; n++)
        {
            var h = Table(n, seed: 40 + n, options: new { map = "fair" });
            Ready(h);
            var v = V(h);
            Assert.Equal("fair", v.GetProperty("map").GetString());
            Assert.True(v.GetProperty("h")[125].GetInt32() < v.GetProperty("water").GetInt32());
            foreach (var hut in v.GetProperty("huts").EnumerateArray().Where(x => x.GetProperty("alive").GetBoolean()))
            {
                var x = hut.GetProperty("x").GetInt32();
                Assert.True(x < 390 || x > 610, $"{n}: хата на {x}");
                Assert.True(hut.GetProperty("y").GetInt32() > 20);
            }
        }
    }

    [Fact]
    public void Fair_pond_splashes_a_pot_at_its_own_level()
    {
        var core = Bare();
        core.Map = GlekometCore.MapFair;
        for (var c = 115; c <= 135; c++) core.H[c] = 10;
        core.BeginShot(0, GlekometCore.Pot);
        core.Spawn(GlekometCore.Pot, 500, 200, 0, -50, false, 0);
        var end = Fly(core)!.Value;
        Assert.Equal(("splash", GlekometCore.PondLevel), (end.Kind, end.Y));
    }

    [Fact]
    public void Winter_slides_a_hit_hut_away_and_plain_does_not()
    {
        foreach (var winter in new[] { false, true })
        {
            var core = Bare();
            core.Map = winter ? GlekometCore.MapWinter : GlekometCore.MapPlain;
            core.Place(0, 100);
            core.Place(1, 500);
            core.BeginShot(0, GlekometCore.Pot);
            core.Explode(GlekometCore.Pot, 470, 100, -1);
            Assert.True(core.Huts[1].Hp < 100);
            if (winter) Assert.True(core.Huts[1].X > 500, core.Huts[1].X.ToString());
            else Assert.Equal(500, core.Huts[1].X);
        }
    }

    [Fact]
    public void Default_map_is_plain_and_mix_is_reproducible_by_seed()
    {
        var h = Table(3);
        Ready(h);
        Assert.Equal("plain", V(h).GetProperty("map").GetString());
        var a = Table(3, seed: 7, options: new { map = "mix" });
        var b = Table(3, seed: 7, options: new { map = "mix" });
        Ready(a);
        Ready(b);
        Assert.Equal(V(a).GetProperty("map").GetString(), V(b).GetProperty("map").GetString());
        Assert.Equal(V(a).GetProperty("h").GetRawText(), V(b).GetProperty("h").GetRawText());
        Assert.Contains(V(a).GetProperty("map").GetString(), Glekomet.MapKeys);
    }

    // =============================================================================================
    // 99. «Залп» — усі цілять разом
    // =============================================================================================

    [Fact]
    public void Volley_lets_everybody_aim_at_once_and_launches_when_all_are_ready()
    {
        var h = Table(3, options: new { mode = "volley" });
        Ready(h);
        var v = V(h);
        Assert.Equal("volley", v.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        Assert.Equal(Glekomet.VolleySecs * 1000, v.GetProperty("turnMs").GetInt32());
        Flatten(h, 100, (0, 150), (1, 500), (2, 850));

        var mark = h.Outbox.Count;
        h.Input(1, "aim", new { a = 60, p = 70, w = 0 });                  // чужих прицілів не видно
        Assert.True(Fire(h, 0, 135, 100).Ok, h.Reply.Message);
        Assert.False(Fire(h, 0, 135, 100).Ok);
        Assert.Equal("Постріл уже заряджено — чекаємо решту", h.Reply.Message);
        Assert.True(Fire(h, 1, 45, 100, GlekometCore.Varenyk).Ok, h.Reply.Message);
        h.Tick(1);
        Assert.DoesNotContain(Frames(h, mark), f => Views.Has(f, "aim"));
        Assert.Contains(Frames(h, mark), f => Views.Has(f, "rd") && f.GetProperty("rd").GetInt32() == 3);
        Assert.Equal(Glekomet.PhaseAim, Phase(h));
        Assert.Equal(1, V(h).GetProperty("inv")[1][GlekometCore.Varenyk].GetInt32());   // запас — лише на вильоті

        Assert.True(h.Act(2, "skip").Ok, h.Reply.Message);
        h.Tick(1);
        Assert.Equal(Glekomet.PhaseFly, Phase(h));
        Assert.Equal(2, Core(h).LiveShells);
        Assert.Equal(4, V(h).GetProperty("shells")[0].GetArrayLength());   // [x, y, kind, чий]
        Settle(h);
        v = V(h);
        Assert.Equal(1, v.GetProperty("stats")[0].GetProperty("shots").GetInt32());
        Assert.Equal(1, v.GetProperty("stats")[1].GetProperty("shots").GetInt32());
        Assert.Equal(0, v.GetProperty("stats")[2].GetProperty("shots").GetInt32());
        Assert.Equal(0, v.GetProperty("inv")[1][GlekometCore.Varenyk].GetInt32());
        Assert.Equal(2, v.GetProperty("round").GetInt32());
        Assert.All(v.GetProperty("ready").EnumerateArray(), r => Assert.False(r.GetBoolean()));
    }

    [Fact]
    public void Volley_credits_each_shooter_with_their_own_damage_and_ruins()
    {
        var h = Table(3, options: new { mode = "volley" });
        Ready(h);
        var core = Flatten(h, 100, (0, 200), (1, 700), (2, 900));
        core.Huts[1].Hp = 20;
        core.Wind = 0;
        Assert.True(Fire(h, 0, 45, PowerFor(core, 0, 45, 700)).Ok, h.Reply.Message);    // Оля — в Петра
        Assert.True(Fire(h, 2, 135, PowerFor(core, 2, 135, 200)).Ok, h.Reply.Message);  // Ганна — в Олю
        Assert.True(Fire(h, 1, 45, 100).Ok, h.Reply.Message);                           // Петро — за край
        h.Tick(1);
        Settle(h);
        var v = V(h);
        Assert.Equal(20, v.GetProperty("stats")[0].GetProperty("dmg").GetInt32());
        Assert.Equal(1, v.GetProperty("stats")[0].GetProperty("kills").GetInt32());
        Assert.Equal(35, v.GetProperty("stats")[2].GetProperty("dmg").GetInt32());
        Assert.Equal(0, v.GetProperty("stats")[1].GetProperty("dmg").GetInt32());
        Assert.Contains(v.GetProperty("log").EnumerateArray(), l => l.GetString() == "Петро: хата в руїнах (Оля)");
        Assert.StartsWith("Залп: ", v.GetProperty("last").GetProperty("text").GetString());
    }

    [Fact]
    public void Volley_sleepers_are_skipped_and_a_sleeping_table_ends_in_a_draw()
    {
        var h = Table(2, options: new { mode = "volley" });
        Ready(h);
        for (var k = 0; k < 3 && h.Room.Status == RoomStatus.Playing; k++)
        {
            for (var i = 0; i < 2000 && Phase(h) == Glekomet.PhaseAim; i++) h.Tick(1);
            Settle(h);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal("idle", V(h).GetProperty("result").GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Volley_of_six_shard_pots_ticks_fast_and_frames_stay_small()
    {
        var h = Table(6, options: new { mode = "volley", arms = "jokes" });
        Ready(h);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ticks = 0;
        var biggest = 0;
        for (var round = 0; round < 6 && h.Room.Status == RoomStatus.Playing; round++)
        {
            for (var s = 0; s < 6; s++)
                if (Core(h).Huts[s].Alive) h.Act(s, "fire", new { a = Core(h).Huts[s].X < 500 ? 60 : 120, p = 70, w = round < 2 ? GlekometCore.Shards : GlekometCore.Rooster });
            for (var i = 0; i < 400 && h.Room.Status == RoomStatus.Playing && (i == 0 || Phase(h) != Glekomet.PhaseAim); i++)
            {
                var mark = h.Outbox.Count;
                h.Tick(1);
                ticks++;
                foreach (var f in Frames(h, mark)) biggest = Math.Max(biggest, f.GetRawText().Length);
            }
        }
        sw.Stop();
        output.WriteLine($"Залп на шістьох: {ticks} тиків, {sw.Elapsed.TotalMilliseconds / Math.Max(1, ticks):F4} мс/тик (з розсилкою), найбільший кадр {biggest} Б");
        Assert.True(ticks > 100);
        Assert.True(biggest < 1536, $"{biggest} Б");
        Assert.True(sw.Elapsed.TotalMilliseconds / ticks < 1.0, $"{sw.Elapsed.TotalMilliseconds / ticks} мс/тик");
    }

    [Fact]
    public void Volley_moving_is_allowed_before_the_shot_is_locked()
    {
        var h = Table(2, options: new { mode = "volley" });
        Ready(h);
        Flatten(h, 100, (0, 200), (1, 700));
        Assert.True(h.Act(1, "move", new { dir = -1 }).Ok, h.Reply.Message);
        Assert.True(h.Act(0, "move", new { dir = 1 }).Ok, h.Reply.Message);
        Assert.True(Fire(h, 0, 45, 50).Ok);
        Assert.False(h.Act(0, "move", new { dir = 1 }).Ok);
        Assert.Equal((208, 692), (Core(h).Huts[0].X, Core(h).Huts[1].X));
    }
}
