using System.Diagnostics;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Гарячий горщик: мапу, крок, горщики, ляпаси й мозок ботів перевіряємо на голому <see cref="PotatoCore"/> (там
/// селянина можна поставити рівно туди, куди треба), а раунди, очки, дії й приховане — через справжню кімнату
/// (<see cref="RoomHarness"/>). Клас у серійній колекції через перф-тест.
/// </summary>
[Collection(SerialPerf.Name)]
public class PotatoTests(ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Юрко", "Соня", "Богдан"];

    // ---------- підмостки ----------

    static RoomHarness Table(int players = 2, int seed = 42, object? options = null)
    {
        var h = new RoomHarness("potato", options, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static Potato G(RoomHarness h) => (Potato)h.Room.Game;
    static PotatoCore Core(RoomHarness h) => G(h).CoreForTests;
    static PotatoSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static PotatoVillager Me(RoomHarness h, int seat) => Core(h).V[S(h, seat).Me];

    /// <summary>До фази «go» (горщики вже роздано).</summary>
    static void Go(RoomHarness h)
    {
        for (var i = 0; i < 200 && G(h).Phase != Potato.PhaseGo; i++) h.Tick();
        Assert.Equal(Potato.PhaseGo, G(h).Phase);
    }

    /// <summary>Поставити селянина в центр клітинки (x, y) + зсув; стоїть, нікуди не хоче.</summary>
    static PotatoVillager Put(PotatoVillager v, int cx, int cy, int ox = 0, int oy = 0, int dir = 0)
    {
        v.X = cx * PotatoMap.Cell + PotatoMap.Cell / 2 + ox;
        v.Y = cy * PotatoMap.Cell + PotatoMap.Cell / 2 + oy;
        v.Dir = dir;
        v.Want = -1;
        v.Moving = false;
        v.Blocked = false;
        return v;
    }

    /// <summary>
    /// Усі боти, крім переданих, «сплять»: стоять рядком унизу мапи далеко від усього й нікуди не хочуть. Горщики, що
    /// були в сплячих, переходять до першого з <paramref name="keep"/> (або лишаються, якщо того нема).
    /// </summary>
    static void Park(RoomHarness h, params int[] keep)
    {
        var i = 0;
        foreach (var v in Core(h).V)
        {
            if (Array.IndexOf(keep, v.Id) >= 0) continue;
            Put(v, 1 + i % 22, 14 - i / 22 % 2);
            v.Stand = 100_000;
            v.Brave = true;
            v.Mode = 0;
            i++;
        }
    }

    /// <summary>Віддати горщик <paramref name="k"/> селянинові <paramref name="to"/> «чарами» (для тестів): щойно отримав, фитіль — як є.</summary>
    static void Hand(RoomHarness h, int k, PotatoVillager to, int? fuse = null)
    {
        var core = Core(h);
        var p = core.Pots[k];
        if (p.Carrier >= 0) core.V[p.Carrier].Pot = -1;
        p.Carrier = to.Id;
        p.Since = core.Now;
        p.Giver = p.GiverSeat = -1;
        p.Respawn = 0;
        if (fuse is { } f) p.Fuse = f;
        to.Pot = k;
        to.Mode = 0;
        to.Stand = to.Owner < 0 ? 100_000 : 0;
    }

    /// <summary>Чиста толока «на двох»: усіх приспано, горщик — у <paramref name="to"/> з довгим ґнотом.</summary>
    static RoomHarness Quiet(int players, int seed, out PotatoCore core, object? options = null)
    {
        var h = Table(players, seed, options);
        Go(h);
        core = Core(h);
        Park(h);
        foreach (var p in core.Pots) p.Fuse = 100_000;
        return h;
    }

    /// <summary>Після вибуху новий горщик не з'являється (щоб тест бачив лише те, що перевіряє).</summary>
    static void NoRespawn(PotatoCore core)
    {
        foreach (var p in core.Pots) if (p.Carrier < 0) p.Respawn = 100_000;
    }

    static JsonElement LastFrame(RoomHarness h) => Views.Json(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame);

    static PotatoVillager Bot(RoomHarness h, int skip = 0) => Core(h).V.Where(v => v.Owner < 0).Skip(skip).First();

    // =============================================================================================
    // Мапа й навігація
    // =============================================================================================

    [Fact]
    public void Map_is_24_by_16_fenced_all_round_with_283_walkable_cells()
    {
        Assert.Equal(16, PotatoMap.Rows.Length);
        Assert.All(PotatoMap.Rows, r => Assert.Equal(24, r.Length));
        Assert.All(PotatoMap.Rows[0], c => Assert.Equal('#', c));
        Assert.All(PotatoMap.Rows[15], c => Assert.Equal('#', c));
        Assert.All(PotatoMap.Rows, r => Assert.True(r[0] == '#' && r[23] == '#'));
        Assert.Equal(283, PotatoMap.Walkable.Length);
        Assert.Equal(48, PotatoMap.Names.Distinct().Count());
        Assert.All(string.Concat(PotatoMap.Rows), c => Assert.Contains(c, "#.=TYWBV"));
    }

    [Fact]
    public void Every_walkable_cell_is_reachable_from_every_other()
    {
        var hops = PotatoMap.NextHop;
        foreach (var target in PotatoMap.Walkable)
            foreach (var cell in PotatoMap.Walkable)
            {
                var hop = hops[target * PotatoMap.Cells + cell];
                if (cell == target) Assert.Equal(PotatoMap.NoHop, hop);
                else Assert.InRange(hop, 0, 3);
            }
    }

    [Fact]
    public void Next_hop_table_leads_to_the_target_in_bfs_distance_steps()
    {
        var rng = new Random(5);
        var walk = PotatoMap.Walkable;
        for (var i = 0; i < 60; i++)
        {
            int from = walk[rng.Next(walk.Length)], to = walk[rng.Next(walk.Length)];
            var dist = Bfs(from, to);
            var cell = from;
            var steps = 0;
            while (cell != to && steps < 500)
            {
                var hop = PotatoMap.NextHop[to * PotatoMap.Cells + cell];
                cell += PotatoCore.DX[hop] + PotatoCore.DY[hop] * PotatoMap.W;
                Assert.True(PotatoMap.Pass[cell]);
                steps++;
            }
            Assert.Equal(dist, steps);
        }
    }

    static int Bfs(int from, int to)
    {
        var dist = new int[PotatoMap.Cells];
        Array.Fill(dist, -1);
        var q = new Queue<int>();
        q.Enqueue(from);
        dist[from] = 0;
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            if (u == to) return dist[u];
            for (var d = 0; d < 4; d++)
            {
                int x = u % PotatoMap.W + PotatoCore.DX[d], y = u / PotatoMap.W + PotatoCore.DY[d];
                if (x < 0 || y < 0 || x >= PotatoMap.W || y >= PotatoMap.H) continue;
                var n = y * PotatoMap.W + x;
                if (!PotatoMap.Pass[n] || dist[n] >= 0) continue;
                dist[n] = dist[u] + 1;
                q.Enqueue(n);
            }
        }
        return -1;
    }

    // =============================================================================================
    // Рух — один для всіх
    // =============================================================================================

    [Fact]
    public void A_bot_and_a_player_given_the_same_want_walk_the_same_path()
    {
        var bot = new PotatoVillager { Owner = -1 };
        var man = new PotatoVillager { Owner = 3 };
        Put(bot, 3, 3, 5, -4);
        Put(man, 3, 3, 5, -4);
        var rng = new Random(9);
        for (var t = 0; t < 400; t++)
        {
            if (t % 7 == 0) bot.Want = man.Want = rng.Next(-1, 4);
            PotatoCore.Step(bot);
            PotatoCore.Step(man);
            Assert.Equal((bot.X, bot.Y, bot.Dir, bot.State), (man.X, man.Y, man.Dir, man.State));
        }
    }

    [Fact]
    public void Everyone_moves_exactly_three_units_along_one_axis_or_not_at_all()
    {
        var h = Table(4, seed: 11);
        var core = Core(h);
        var rng = new Random(2);
        var was = core.V.Select(v => (v.X, v.Y)).ToArray();
        for (var t = 0; t < 600 && G(h).Phase is Potato.PhaseStart or Potato.PhaseGo; t++)
        {
            if (t % 6 == 0)
                for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            h.Tick();
            foreach (var v in core.V)
            {
                int dx = Math.Abs(v.X - was[v.Id].X), dy = Math.Abs(v.Y - was[v.Id].Y);
                Assert.True((dx, dy) is (0, 0) or (3, 0) or (0, 3), $"{v.Id}: {dx},{dy}");
                Assert.True(PotatoMap.BoxFits(v.X, v.Y));
                was[v.Id] = (v.X, v.Y);
            }
        }
    }

    [Fact]
    public void Stunned_fallen_and_dead_villagers_do_not_move_whatever_they_want()
    {
        foreach (var set in new Action<PotatoVillager>[] { v => v.Stun = 5, v => v.Fallen = 5, v => v.Dead = true })
        {
            var v = Put(new PotatoVillager(), 5, 5, dir: 2);
            set(v);
            v.Want = 0;
            PotatoCore.Step(v);
            Assert.Equal((5 * 32 + 16, 5 * 32 + 16), (v.X, v.Y));
            Assert.False(v.Moving);
            Assert.Equal(2, v.Dir);
        }
        var s = new PotatoVillager { Stun = 1 };
        Assert.Equal(4, s.State);
        Assert.Equal(2, new PotatoVillager { Fallen = 1 }.State);
        Assert.Equal(3, new PotatoVillager { Dead = true }.State);
    }

    // =============================================================================================
    // Горщики
    // =============================================================================================

    [Fact]
    public void Pots_appear_only_when_the_round_opens_not_during_the_look_around()
    {
        var h = Table(2, seed: 13);
        Assert.Equal(Potato.PhaseStart, G(h).Phase);
        h.Tick();
        Assert.Empty(LastFrame(h).GetProperty("p").EnumerateArray());
        Assert.All(Core(h).V, v => Assert.Equal(-1, v.Pot));
        Go(h);
        var f = LastFrame(h);
        var p = f.GetProperty("p").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Equal(2, p.Length);
        Assert.InRange(p[0], 0, Core(h).N - 1);
        Assert.Equal(0, Core(h).V[p[0]].Pot);
        // і новий горщик оголошено подією [4, горщик, у кого]
        Assert.Contains(f.GetProperty("ev").EnumerateArray(), e => e[0].GetInt32() == 4 && e[2].GetInt32() == p[0]);
    }

    [Fact]
    public void Pot_count_follows_the_option_and_auto_gives_two_from_five_players()
    {
        Assert.Equal(1, G(Table(4, seed: 1)).PotCount);
        Assert.Equal(2, G(Table(5, seed: 1)).PotCount);
        Assert.Equal(2, G(Table(2, seed: 1, options: new { pots = "2" })).PotCount);
        Assert.Equal(1, G(Table(6, seed: 1, options: new { pots = "1" })).PotCount);
        Assert.Equal(1, G(Table(3, seed: 1, options: new { pots = "7" })).PotCount);
        var h = Table(5, seed: 3);
        Go(h);
        var carriers = Core(h).Pots.Select(p => p.Carrier).ToArray();
        Assert.Equal(2, carriers.Distinct().Count());
        Assert.All(carriers, c => Assert.True(c >= 0));
    }

    [Fact]
    public void Fuse_is_twelve_to_thirty_seconds_and_the_frame_shows_only_how_it_smokes()
    {
        var fuses = new List<int>();
        for (var seed = 1; seed <= 40; seed++)
        {
            var h = Table(2, seed);
            Go(h);
            var p = Core(h).Pots[0];
            fuses.Add(p.Fuse + 1);
            Assert.InRange(p.H1, PotatoCore.H1Min, PotatoCore.H1Max);
            Assert.InRange(p.H2, PotatoCore.H2Min, PotatoCore.H2Max);
            Assert.InRange(p.H3, PotatoCore.H3Min, PotatoCore.H3Max);
            var f = LastFrame(h).GetProperty("p");
            Assert.Equal(2, f.GetArrayLength());
            Assert.InRange(f[1].GetInt32(), 0, 3);
        }
        Assert.All(fuses, x => Assert.InRange(x, PotatoCore.FuseMin, PotatoCore.FuseMax + 1));
        Assert.True(fuses.Distinct().Count() > 20);
        Assert.Equal(300, PotatoCore.FuseMin);
        Assert.Equal(750, PotatoCore.FuseMax);
    }

    [Fact]
    public void Smoke_thickens_from_zero_to_three_as_the_fuse_burns_down()
    {
        var h = Quiet(2, 15, out var core);
        var p = core.Pots[0];
        (p.H1, p.H2, p.H3) = (250, 120, 50);
        p.Fuse = 400;
        var seen = new List<int>();
        for (var t = 0; t < 399; t++)
        {
            h.Tick();
            var heat = core.Heat(0);
            if (seen.Count == 0 || seen[^1] != heat) seen.Add(heat);
            var want = p.Fuse <= 50 ? 3 : p.Fuse <= 120 ? 2 : p.Fuse <= 250 ? 1 : 0;
            Assert.Equal(want, heat);
        }
        Assert.Equal([0, 1, 2, 3], seen);
    }

    [Fact]
    public void Pass_goes_to_the_nearest_in_touch_range_and_only_after_half_a_second_in_hand()
    {
        var h = Quiet(2, 17, out var core);
        var me = Me(h, 0);
        var near = Bot(h);
        var far = Bot(h, 1);
        Put(me, 5, 3);
        Put(near, 5, 3, 20, 0);
        Put(far, 5, 3, 0, 30);
        Hand(h, 0, me);
        Assert.False(h.Act(0, "pass", new { }).Ok);
        Assert.Equal("Горщик ще пече руки — мить!", h.Reply.Message);
        h.Tick(PotatoCore.HoldMin - 1);
        Assert.False(h.Act(0, "pass", new { }).Ok);
        h.Tick();
        Assert.True(h.Act(0, "pass", new { }).Ok, h.Reply.Message);
        Assert.Equal(near.Id, core.Pots[0].Carrier);
        Assert.Equal(0, near.Pot);
        Assert.Equal(-1, me.Pot);
        h.Tick();
        Assert.Contains(LastFrame(h).GetProperty("ev").EnumerateArray(), e => e[0].GetInt32() == 1 && e[2].GetInt32() == me.Id && e[3].GetInt32() == near.Id);
        // нікого впритул — відмова
        Hand(h, 0, me);
        Put(near, 9, 3);
        Put(far, 9, 5);
        h.Tick(PotatoCore.HoldMin);
        Assert.False(h.Act(0, "pass", new { }).Ok);
        Assert.Equal("Нікого поруч — підійди впритул", h.Reply.Message);
        Assert.False(h.Act(1, "pass", new { }).Ok);
        Assert.Equal("Нема в тебе горщика", h.Reply.Message);
    }

    [Fact]
    public void Explicit_pass_target_is_accepted_up_to_48_units_and_refused_beyond()
    {
        var h = Quiet(2, 19, out var core);
        var me = Me(h, 0);
        var a = Bot(h);
        Put(me, 5, 3);
        Put(a, 5, 3, 48, 0);
        Hand(h, 0, me);
        h.Tick(PotatoCore.HoldMin);
        Put(a, 5, 3, 49, 0);
        Assert.False(h.Act(0, "pass", new { id = a.Id }).Ok);
        Assert.Equal("Далеко — не дотягнешся", h.Reply.Message);
        Put(a, 5, 3, 48, 0);
        Assert.True(h.Act(0, "pass", new { id = a.Id }).Ok, h.Reply.Message);
        Assert.Equal(a.Id, core.Pots[0].Carrier);
    }

    [Fact]
    public void Pass_refusals_self_lying_holder_giver_and_junk()
    {
        var h = Table(2, seed: 21, options: new { pots = "2" });
        Go(h);
        var core = Core(h);
        Park(h);
        foreach (var p in core.Pots) p.Fuse = 100_000;
        var me = Me(h, 0);
        var him = Me(h, 1);
        var lying = Bot(h);
        var holder = Bot(h, 1);
        Put(me, 5, 3);
        Put(him, 5, 3, 20);
        Put(lying, 5, 3, -20);
        Put(holder, 5, 3, 0, 20);
        lying.Fallen = 50;
        Hand(h, 1, holder);
        Hand(h, 0, him);
        h.Tick(PotatoCore.HoldMin);
        Assert.True(h.Act(1, "pass", new { id = me.Id }).Ok, h.Reply.Message);
        h.Tick(PotatoCore.HoldMin);
        foreach (var (payload, text) in new (object, string)[]
        {
            (new { id = me.Id }, "Сам собі не передаси"),
            (new { id = lying.Id }, "Лежачому горщик не тицяють"),
            (new { id = holder.Id }, "У того вже є горщик"),
            (new { id = him.Id }, "Назад одразу не можна — хай хоч потримає"),
            (new { id = 999 }, "Такого селянина нема"),
            (new { id = "x" }, "Такого селянина нема"),
        })
        {
            Assert.False(h.Act(0, "pass", payload).Ok);
            Assert.Equal(text, h.Reply.Message);
        }
        // автоціль теж не повертає горщик тому, хто щойно дав, — поки не мине 2 с
        Put(holder, 12, 12);
        Put(lying, 12, 11);
        Assert.False(h.Act(0, "pass", new { }).Ok);
        Assert.Equal("Нікого поруч — підійди впритул", h.Reply.Message);
        h.Tick(PotatoCore.NoBackTicks);
        Assert.True(h.Act(0, "pass", new { }).Ok, h.Reply.Message);
        Assert.Equal(him.Id, core.Pots[0].Carrier);
    }

    [Fact]
    public void Explosion_knocks_a_bot_down_for_three_seconds_and_a_new_pot_appears_two_seconds_later()
    {
        var h = Quiet(2, 23, out var core);
        var bot = Bot(h);
        Put(bot, 8, 8);
        Hand(h, 0, bot, fuse: 5);
        h.Tick(5);
        Assert.Equal(2, bot.State);
        Assert.Equal(-1, bot.Pot);
        Assert.Equal(-1, core.Pots[0].Carrier);
        Assert.Equal(PotatoCore.RespawnTicks, core.Pots[0].Respawn);
        var ev = LastFrame(h).GetProperty("ev").EnumerateArray().Single(e => e[0].GetInt32() == 3);
        Assert.Equal([3, 0, bot.Id, 0, -1], ev.EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(-1, LastFrame(h).GetProperty("p")[0].GetInt32());
        h.Tick(PotatoCore.RespawnTicks - 1);
        Assert.Equal(-1, core.Pots[0].Carrier);
        h.Tick();
        Assert.True(core.Pots[0].Carrier >= 0);
        h.Tick(PotatoCore.FallTicks - PotatoCore.RespawnTicks);
        Assert.Equal(0, bot.Fallen);
        Assert.NotEqual(2, bot.State);
        Assert.True(S(h, 0).Alive && S(h, 1).Alive);
    }

    [Fact]
    public void Explosion_in_a_players_hands_takes_him_out_and_names_him_to_everyone()
    {
        var h = Quiet(3, 25, out var core);
        var him = Me(h, 1);
        Hand(h, 0, him, fuse: 3);
        h.Tick(3);
        Assert.False(S(h, 1).Alive);
        Assert.True(him.Dead);
        Assert.Equal(3, him.State);
        var ev = LastFrame(h).GetProperty("ev").EnumerateArray().Single(e => e[0].GetInt32() == 3);
        Assert.Equal([3, 0, him.Id, 1, 1], ev.EnumerateArray().Select(e => e.GetInt32()));
        var dead = h.View(null).GetProperty("dead").EnumerateArray().Single();
        Assert.Equal((1, him.Id), (dead.GetProperty("seat").GetInt32(), dead.GetProperty("id").GetInt32()));
        Assert.Equal(Potato.PhaseGo, G(h).Phase);
        Assert.False(h.Act(1, "slap", new { }).Ok);
        Assert.Equal("Тебе вже рознесло — дивись, хто кого", h.Reply.Message);
    }

    [Fact]
    public void Burn_pays_two_to_the_player_who_passed_it_but_nothing_when_a_bot_passed()
    {
        var h = Quiet(3, 27, out var core);
        var me = Me(h, 0);
        var him = Me(h, 1);
        var bot = Bot(h);
        Put(me, 5, 3);
        Put(him, 5, 3, 20);
        Hand(h, 0, me, fuse: 100);
        h.Tick(PotatoCore.HoldMin);
        Assert.True(h.Act(0, "pass", new { id = him.Id }).Ok, h.Reply.Message);
        Put(him, 10, 10);
        h.Tick(100);
        Assert.False(S(h, 1).Alive);
        Assert.Equal(1, S(h, 0).Burns);
        Assert.Equal(Potato.PtBurn, S(h, 0).Total);
        // бот підкинув третьому — нікому нічого
        h.Tick(PotatoCore.RespawnTicks);
        var third = Me(h, 2);
        Put(bot, 12, 12);
        Put(third, 12, 12, 20);
        Hand(h, 0, bot, fuse: 60);
        bot.Stand = 0;
        bot.Mode = 1;
        h.Tick(59);
        Assert.Equal(third.Id, core.Pots[0].Carrier);
        Put(third, 3, 3);
        h.Tick(5);
        Assert.False(S(h, 2).Alive);
        Assert.Equal(1, S(h, 0).Burns);
        Assert.Equal(0, S(h, 1).Burns + S(h, 2).Burns);
    }

    [Fact]
    public void A_stunned_carrier_cannot_pass_but_a_stunned_villager_can_receive()
    {
        var h = Quiet(2, 29, out var core);
        var me = Me(h, 0);
        var him = Me(h, 1);
        Put(me, 5, 3);
        Put(him, 5, 3, 24, 0, dir: 2);
        Hand(h, 0, me);
        h.Tick(PotatoCore.HoldMin);
        Assert.True(h.Act(1, "slap", new { }).Ok, h.Reply.Message);
        Assert.Equal(PotatoCore.StunTicks, me.Stun);
        Assert.False(h.Act(0, "pass", new { }).Ok);
        Assert.Equal("У тебе ще іскри в очах", h.Reply.Message);
        // навпаки: оглушеному горщик тицьнути можна — у тому й сіль ляпаса
        h.Tick(PotatoCore.StunTicks);
        Assert.True(h.Act(0, "slap", new { id = him.Id }).Ok, h.Reply.Message);
        Assert.Equal(4, him.State);
        Assert.True(h.Act(0, "pass", new { id = him.Id }).Ok, h.Reply.Message);
        Assert.Equal(him.Id, core.Pots[0].Carrier);
    }

    // =============================================================================================
    // Ляпас
    // =============================================================================================

    [Fact]
    public void Slapping_a_player_stuns_him_for_three_seconds_and_pays_one_point_hidden_till_the_reveal()
    {
        var h = Quiet(2, 31, out var core);
        var me = Me(h, 0);
        var him = Me(h, 1);
        Put(me, 5, 5, dir: 0);
        Put(him, 5, 5, 30, 0);
        him.Want = 2;
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        Assert.Equal(PotatoCore.StunTicks, him.Stun);
        Assert.Equal(0, me.Stun);
        Assert.Equal(1, S(h, 0).Slaps);
        var x = him.X;
        h.Tick();
        Assert.Equal(4, LastFrame(h).GetProperty("v")[him.Id * 4 + 3].GetInt32());
        var ev = LastFrame(h).GetProperty("ev").EnumerateArray().Single(e => e[0].GetInt32() == 2);
        Assert.Equal([2, me.Id, him.Id, him.Id], ev.EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(0, h.View(null).GetProperty("seats")[0].GetProperty("total").GetInt32());
        h.Tick(PotatoCore.StunTicks - 2);
        Assert.Equal(x, him.X);
        h.Input(1, "move", new { dir = 2 });   // модуль підтверджує затиснуту стрілку раз на секунду
        h.Tick(2);
        Assert.NotEqual(x, him.X);       // оклигав — і затиснута стрілка знову веде
    }

    [Fact]
    public void Slapping_a_bot_dazes_the_slapper_and_the_frame_shows_the_same_state_four()
    {
        var h = Quiet(2, 33, out var core);
        var me = Me(h, 0);
        var bot = Bot(h);
        Put(me, 5, 5, dir: 1);
        Put(bot, 5, 5, 0, 30);
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);
        Assert.Equal(PotatoCore.StunTicks, me.Stun);
        Assert.Equal(0, bot.Stun);
        Assert.Equal(0, S(h, 0).Slaps);
        h.Tick();
        var f = LastFrame(h);
        Assert.Equal(4, f.GetProperty("v")[me.Id * 4 + 3].GetInt32());
        var ev = f.GetProperty("ev").EnumerateArray().Single(e => e[0].GetInt32() == 2);
        Assert.Equal([2, me.Id, bot.Id, me.Id], ev.EnumerateArray().Select(e => e.GetInt32()));
        me.Want = 0;
        h.Input(0, "move", new { dir = 0 });
        var x = me.X;
        h.Tick(10);
        Assert.Equal(x, me.X);
        Assert.False(h.Act(0, "slap", new { }).Ok);
        Assert.Equal("У тебе ще іскри в очах", h.Reply.Message);
    }

    [Fact]
    public void A_bot_slap_follows_the_same_rule_it_stuns_a_player_and_dazes_itself_on_a_bot()
    {
        var core = new PotatoCore(new Random(3));
        core.Deal([0], 3);
        foreach (var v in core.V) { PotatoCore.Forget(v); Put(v, 20, 12); }
        var man = core.V.First(v => v.Owner == 0);
        var bots = core.V.Where(v => v.Owner < 0).ToArray();
        Put(bots[0], 5, 5, dir: 0);
        Put(man, 5, 5, 30);
        Assert.Equal(PotatoNo.None, core.TrySlap(bots[0], null));
        Assert.Equal(PotatoCore.StunTicks, man.Stun);
        Assert.Equal(0, bots[0].Stun);
        Put(bots[1], 8, 5, dir: 0);
        Put(bots[2], 8, 5, 30);
        Assert.Equal(PotatoNo.None, core.TrySlap(bots[1], null));
        Assert.Equal(PotatoCore.StunTicks, bots[1].Stun);
        Assert.Equal(0, bots[2].Stun);
        Assert.Equal(4, bots[1].State);
        Assert.Equal(4, man.State);
    }

    [Fact]
    public void Slap_cone_takes_the_nearest_upright_within_forty_five_degrees_and_forty_units()
    {
        var core = new PotatoCore(new Random(1));
        core.Deal([0], 4);
        foreach (var v in core.V) { PotatoCore.Forget(v); Put(v, 20, 12); }
        var a = core.V.First(v => v.Owner == 0);
        var others = core.V.Where(v => v != a).ToArray();
        Put(a, 5, 5, dir: 0);
        Put(others[0], 5, 5, 38, 30);    // 50 — поза дальністю
        Put(others[1], 5, 5, 30, 28);    // понад 45°
        Put(others[2], 5, 5, 36, 10);    // у конусі
        Put(others[3], 5, 5, -20, 0);    // за спиною
        Assert.Equal(others[2].Id, core.SlapTarget(a));
        Put(others[1], 5, 5, 20, 5);
        Assert.Equal(others[1].Id, core.SlapTarget(a));
        others[1].Fallen = 10;
        Assert.Equal(others[2].Id, core.SlapTarget(a));
        others[2].Stun = 10;
        Assert.Equal(-1, core.SlapTarget(a));
        // явна ціль — до 52 у будь-який бік; б'ючи, обертається до цілі
        others[3].X = a.X - 52;
        Assert.Equal(PotatoNo.None, core.TrySlap(a, others[3].Id));
        Assert.Equal(2, a.Dir);
    }

    [Fact]
    public void Slap_has_a_four_second_cooldown_and_refuses_self_lying_far_and_nobody()
    {
        var h = Quiet(3, 35, out var core);
        var me = Me(h, 0);
        var him = Me(h, 1);
        var third = Me(h, 2);
        Put(me, 5, 5, dir: 0);
        Put(him, 5, 5, 30);
        Put(third, 9, 5);
        foreach (var (payload, text) in new (object, string)[]
        {
            (new { id = me.Id }, "Себе не ляскають"),
            (new { id = third.Id }, "Далеко — не дотягнешся"),
            (new { id = -3 }, "Такого селянина нема"),
        })
        {
            Assert.False(h.Act(0, "slap", payload).Ok);
            Assert.Equal(text, h.Reply.Message);
        }
        me.Dir = 2;
        Assert.False(h.Act(0, "slap", new { }).Ok);
        Assert.Equal("Перед тобою нікого", h.Reply.Message);
        Assert.True(h.Act(0, "slap", new { id = him.Id }).Ok, h.Reply.Message);
        Assert.False(h.Act(0, "slap", new { id = him.Id }).Ok);
        Assert.Equal("Рука ще не відійшла", h.Reply.Message);
        h.Tick(PotatoCore.SlapCoolTicks - 1);
        Assert.False(h.Act(0, "slap", new { id = him.Id }).Ok);
        h.Tick();
        him.Stun = 0;
        him.Dead = true;
        Assert.False(h.Act(0, "slap", new { id = him.Id }).Ok);
        Assert.Equal("Лежачого не б'ють", h.Reply.Message);
    }

    // =============================================================================================
    // Раунд і фази
    // =============================================================================================

    [Fact]
    public void Match_starts_with_a_three_second_look_around_where_pass_and_slap_are_refused()
    {
        var h = Table(2, seed: 37);
        Assert.Equal(Potato.PhaseStart, G(h).Phase);
        Assert.Equal(Potato.StartTicks, G(h).Left);
        Assert.False(h.Act(0, "slap", new { }).Ok);
        Assert.Equal("Зачекай, толока ще збирається", h.Reply.Message);
        Assert.False(h.Act(0, "pass", new { }).Ok);
        h.Tick(Potato.StartTicks - 1);
        Assert.Equal(Potato.PhaseStart, G(h).Phase);
        h.Tick();
        Assert.Equal(Potato.PhaseGo, G(h).Phase);
        Assert.Equal(Potato.RoundTicks, G(h).Left);
    }

    [Fact]
    public void Everyone_may_walk_during_the_look_around()
    {
        var h = Table(2, seed: 39);
        var me = Me(h, 0);
        Put(me, 5, 3);
        h.Input(0, "move", new { dir = 0 });
        h.Tick(10);
        Assert.Equal(5 * 32 + 16 + 30, me.X);
        Assert.Contains(Core(h).V, v => v.Owner < 0 && v.Moving);
    }

    [Fact]
    public void The_last_player_on_his_feet_wins_the_round_at_once()
    {
        var h = Quiet(2, 41, out var core);
        Hand(h, 0, Me(h, 1), fuse: 2);
        h.Tick(2);
        Assert.Equal(Potato.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("last", r.GetProperty("why").GetString());
        Assert.Equal([0], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(Potato.PtRound + Potato.PtAlive, S(h, 0).Total);
        Assert.Equal(0, S(h, 1).Total);
    }

    [Fact]
    public void On_time_the_one_who_held_the_pot_least_wins_and_equals_share()
    {
        var h = Quiet(3, 43, out var core);
        Hand(h, 0, Me(h, 2));
        h.Tick(30);
        Hand(h, 0, Bot(h));
        h.Tick(Potato.RoundTicks);
        Assert.Equal(Potato.PhaseReveal, G(h).Phase);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("time", r.GetProperty("why").GetString());
        Assert.Equal([0, 1], r.GetProperty("winners").EnumerateArray().Select(e => e.GetInt32()));
        var rows = r.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(30, rows.Single(x => x.GetProperty("seat").GetInt32() == 2).GetProperty("held").GetInt32());
        Assert.Equal(Potato.PtRound + Potato.PtAlive, S(h, 0).Total);
        Assert.Equal(Potato.PtAlive, S(h, 2).Total);
    }

    [Fact]
    public void On_time_with_everyone_equal_nobody_takes_the_round()
    {
        var h = Quiet(2, 45, out var core);
        Hand(h, 0, Bot(h));
        h.Tick(Potato.RoundTicks);
        var r = h.View(null).GetProperty("reveal");
        Assert.Equal("none", r.GetProperty("why").GetString());
        Assert.Empty(r.GetProperty("winners").EnumerateArray());
        Assert.Equal(Potato.PtAlive, S(h, 0).Total);
        Assert.Equal(Potato.PtAlive, S(h, 1).Total);
    }

    [Fact]
    public void Reveal_lasts_six_seconds_freezes_everyone_clears_the_pots_then_a_fresh_round_starts()
    {
        var h = Quiet(2, 47, out var core);
        var oldIds = (S(h, 0).Me, S(h, 1).Me);
        Hand(h, 0, Me(h, 1), fuse: 1);
        h.Tick();
        Assert.Equal(Potato.PhaseReveal, G(h).Phase);
        Assert.Empty(core.Pots);
        Assert.Empty(LastFrame(h).GetProperty("p").EnumerateArray());
        h.Input(0, "move", new { dir = 0 });
        var at = core.V.Select(v => (v.X, v.Y)).ToArray();
        h.Tick(Potato.RevealTicks - 1);
        Assert.Equal(at, core.V.Select(v => (v.X, v.Y)).ToArray());
        h.Tick();
        Assert.Equal(Potato.PhaseStart, G(h).Phase);
        Assert.Equal(2, G(h).RoundNo);
        Assert.True(S(h, 0).Alive && S(h, 1).Alive);
        Assert.Equal(0, S(h, 1).Held);
        Assert.All(core.V, v => Assert.False(v.Dead));
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        _ = oldIds;
    }

    [Fact]
    public void After_the_last_round_the_match_finishes_with_totals_in_the_journal()
    {
        var h = Table(2, seed: 49, options: new { rounds = "1" });
        Go(h);
        Park(h);
        Hand(h, 0, Me(h, 1), fuse: 2);
        h.Tick(2 + Potato.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(Potato.PhaseOver, G(h).Phase);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
        Assert.Contains("Гарячий горщик: Оля 4 : Петро 0", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Contains(h.Scores, s => s.Nick == "Оля" && s.Score == 4);
        var result = h.View(null).GetProperty("result");
        Assert.Equal("end", result.GetProperty("why").GetString());
    }

    [Fact]
    public void Equal_totals_make_a_draw()
    {
        var h = Table(2, seed: 51, options: new { rounds = "1" });
        Go(h);
        Park(h);
        Hand(h, 0, Bot(h), fuse: 100_000);
        h.Tick(Potato.RoundTicks + Potato.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Finished.Single().Result.Winners);
        Assert.Contains("— нічия", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Options_rounds_and_crowd_are_honoured_and_junk_falls_back()
    {
        foreach (var (rounds, want) in new[] { ("1", 1), ("5", 5), ("7", 3) })
        {
            var h = Table(2, seed: 53, options: new { rounds });
            Assert.Equal(want, h.View(null).GetProperty("of").GetInt32());
        }
        Assert.Equal(2 + 14, Core(Table(2, 1)).N);
        Assert.Equal(4 + 16, Core(Table(4, 1)).N);
        Assert.Equal(8 + 22, Core(Table(8, 1)).N);
        Assert.Equal(3 + 12, Core(Table(3, 1, new { crowd = "small" })).N);
        Assert.Equal(8 + 30, Core(Table(8, 1, new { crowd = "big" })).N);
        Assert.Equal(2 + 14, Core(Table(2, 1, new { crowd = "huge" })).N);
    }

    // =============================================================================================
    // Вихід, F5, «Ще раз»
    // =============================================================================================

    [Fact]
    public void A_leaver_becomes_a_bot_and_passes_the_pot_on()
    {
        var h = Quiet(3, 55, out var core);
        var his = Me(h, 2);
        var bot = Bot(h);
        Put(his, 8, 8);
        Put(bot, 8, 8, 20);
        bot.Stand = 0;
        Hand(h, 0, his);
        var n = core.N;
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(-1, his.Owner);
        Assert.Equal(n, core.N);
        h.Tick(60);
        Assert.NotEqual(his.Id, core.Pots[0].Carrier);
        Assert.Equal(4 * n, LastFrame(h).GetProperty("v").GetArrayLength());
        var seat = h.View(null).GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("seat").GetInt32() == 2);
        Assert.True(seat.GetProperty("out").GetBoolean());
        h.Tick(Potato.RoundTicks + Potato.RevealTicks);
        Assert.Equal(2, G(h).RoundNo);
        Assert.Equal(n, Core(h).N);
        Assert.Equal(2, Core(h).V.Count(v => v.Owner >= 0));
    }

    [Fact]
    public void When_only_one_player_remains_the_match_ends_in_his_favour_with_a_reveal()
    {
        var h = Table(2, seed: 57);
        Go(h);
        h.Leave("Оля");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Finished.Single().Result.Winners);
        Assert.Contains("Петро лишається на толоці", h.Outbox.OfType<Journal>().Last().Text);
        var v = h.View(null);
        Assert.Equal("left", v.GetProperty("reveal").GetProperty("why").GetString());
        Assert.Equal(2, v.GetProperty("reveal").GetProperty("ids").GetArrayLength());
        Assert.Equal("left", v.GetProperty("result").GetProperty("why").GetString());
    }

    [Fact]
    public void Rematch_gives_a_clean_match_with_rotated_seats()
    {
        var h = Table(2, seed: 59, options: new { rounds = "1" });
        Go(h);
        Park(h);
        Hand(h, 0, Me(h, 1), fuse: 2);
        h.Tick(2 + Potato.RevealTicks);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        h.Rematch("Оля");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("Петро", h.NickOf(0));
        Assert.Equal(Potato.PhaseStart, G(h).Phase);
        Assert.Equal(1, G(h).RoundNo);
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(0, S(h, s).Total));
        Assert.True(S(h, 0).Alive && S(h, 1).Alive);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("result").ValueKind);
    }

    [Fact]
    public void Move_input_from_a_dead_or_reveal_phase_player_is_swallowed_silently()
    {
        var h = Quiet(3, 61, out var core);
        var him = Me(h, 1);
        Put(him, 8, 8);
        Hand(h, 0, him, fuse: 1);
        h.Tick();
        NoRespawn(core);
        Assert.True(h.Act(1, "move", new { dir = 0 }).Ok);
        h.Tick(5);
        Assert.Equal(8 * 32 + 16, him.X);
        Assert.False(h.Act(0, "move", new { dir = 7 }).Ok);
        Assert.Equal("Такого напрямку нема", h.Reply.Message);
        h.Tick(Potato.RoundTicks);
        Assert.Equal(Potato.PhaseReveal, G(h).Phase);
        Assert.True(h.Act(0, "move", 2).Ok);
        Assert.Equal(-1, Me(h, 0).Want);
    }

    [Fact]
    public void Match_that_has_not_started_refuses_every_action_and_shows_a_standing_crowd()
    {
        var h = new RoomHarness("potato", seed: 1);
        h.Join("Оля");
        h.Join("Петро");
        var view = h.View(0);
        Assert.Equal("lobby", view.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("me").ValueKind);
        Assert.Equal(16 * 4, view.GetProperty("v").GetArrayLength());
        var game = (Potato)h.Room.Game;
        Assert.False(game.Act(0, "move", Views.Payload(new { dir = 1 })).Ok);
        Assert.Equal("Партія ще не почалась", game.Act(0, "pass", Views.Payload(new { })).Message);
    }

    [Fact]
    public void A_held_arrow_that_is_not_confirmed_for_three_seconds_is_let_go()
    {
        var h = Quiet(2, 63, out var core);
        var me = Me(h, 0);
        Put(me, 2, 7);
        h.Input(0, "move", new { dir = 0 });
        h.Tick(Potato.MoveHoldTicks);
        Assert.Equal(0, me.Want);
        h.Tick(2);
        Assert.Equal(-1, me.Want);
    }

    // =============================================================================================
    // Приховане
    // =============================================================================================

    [Fact]
    public void Frame_has_four_numbers_per_villager_two_per_pot_and_no_other_keys()
    {
        var h = Table(5, seed: 65);
        Go(h);
        h.Tick();
        var f = LastFrame(h);
        Assert.Equal(["t", "ph", "left", "v", "p", "ev"], f.EnumerateObject().Select(p => p.Name));
        Assert.Equal(4 * Core(h).N, f.GetProperty("v").GetArrayLength());
        Assert.Equal(2 * 2, f.GetProperty("p").GetArrayLength());
        Assert.All(f.GetProperty("v").EnumerateArray(), e => Assert.Equal(JsonValueKind.Number, e.ValueKind));
    }

    [Fact]
    public void Frame_json_never_mentions_seats_owners_or_how_long_anyone_held_the_pot()
    {
        var h = Table(4, seed: 67);
        Go(h);
        var rng = new Random(1);
        for (var t = 0; t < 900 && G(h).Phase == Potato.PhaseGo; t++)
        {
            if (t % 8 == 0) for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            if (t % 20 == 0) for (var s = 0; s < 4; s++) h.Act(s, s % 2 == 0 ? "pass" : "slap", new { });
            h.Tick();
        }
        foreach (var f in h.Outbox.OfType<RoomFrame>())
        {
            var text = Views.Text(f.Frame);
            foreach (var word in new[] { "\"seat", "\"me", "\"owner", "\"held", "\"nick", "\"alive", "\"fuse" })
                Assert.DoesNotContain(word, text);
        }
    }

    [Fact]
    public void Player_ids_are_shuffled_among_bots_across_seeds()
    {
        var high = 0;
        var notFirst = 0;
        for (var seed = 1; seed <= 100; seed++)
        {
            var core = new PotatoCore(new Random(seed));
            core.Deal([0, 1], 14);
            var ids = core.V.Where(v => v.Owner >= 0).Select(v => v.Id).ToArray();
            if (ids.Any(id => id >= core.N / 2)) high++;
            if (ids.Any(id => id >= 2)) notFirst++;
        }
        Assert.True(high > 50, $"{high}");
        Assert.True(notFirst > 90, $"{notFirst}");
    }

    /// <summary>Вид без <c>me</c> — щоб порівняти два види.</summary>
    static string Strip(JsonElement view)
    {
        var d = view.EnumerateObject().Where(p => p.Name != "me").ToDictionary(p => p.Name, p => p.Value);
        return Views.Text(d);
    }

    [Fact]
    public void Watcher_view_has_no_me_and_other_seats_differ_from_it_only_by_their_own_me()
    {
        var h = Table(3, seed: 69);
        Go(h);
        var watcher = h.View(null);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("me").ValueKind);
        Assert.Equal(JsonValueKind.Null, watcher.GetProperty("reveal").ValueKind);
        Assert.Empty(watcher.GetProperty("dead").EnumerateArray());
        Assert.All(watcher.GetProperty("seats").EnumerateArray(), s => Assert.Equal(JsonValueKind.Null, s.GetProperty("held").ValueKind));
        for (var seat = 0; seat < 3; seat++)
        {
            var v = h.View(seat);
            Assert.Equal(S(h, seat).Me, v.GetProperty("me").GetProperty("id").GetInt32());
            Assert.Equal(Strip(watcher), Strip(v));
        }
    }

    [Fact]
    public void Dead_player_view_equals_the_watcher_view_plus_his_own_me()
    {
        var h = Quiet(3, 71, out var core);
        var him = Me(h, 1);
        Hand(h, 0, him, fuse: 1);
        h.Tick();
        var dead = h.View(1);
        Assert.False(dead.GetProperty("me").GetProperty("alive").GetBoolean());
        Assert.Equal(Strip(h.View(null)), Strip(dead));
        var ids = dead.GetProperty("dead").EnumerateArray().Select(d => d.GetProperty("id").GetInt32()).ToList();
        Assert.Equal([him.Id], ids);
        Assert.DoesNotContain("\"ids\"", Views.Text(dead));
    }

    [Fact]
    public void Reveal_ids_and_held_counters_appear_only_in_reveal_and_over()
    {
        var h = Quiet(2, 73, out var core, new { rounds = "1" });
        Hand(h, 0, Me(h, 0));
        h.Tick(20);
        Hand(h, 0, Bot(h));
        h.Tick(Potato.RoundTicks - 21);
        Assert.Equal(Potato.PhaseGo, G(h).Phase);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("seats")[0].GetProperty("held").ValueKind);
        Assert.Equal(20, h.View(0).GetProperty("me").GetProperty("held").GetInt32());   // своє — собі видно
        h.Tick();
        Assert.Equal(Potato.PhaseReveal, G(h).Phase);
        var reveal = h.View(null).GetProperty("reveal");
        var ids = reveal.GetProperty("ids").EnumerateArray().Select(e => (e.GetProperty("seat").GetInt32(), e.GetProperty("id").GetInt32())).ToList();
        Assert.Equal([(0, S(h, 0).Me), (1, S(h, 1).Me)], ids);
        Assert.Equal(20, h.View(null).GetProperty("seats")[0].GetProperty("held").GetInt32());
        h.Tick(Potato.RevealTicks);
        Assert.Equal(Potato.PhaseOver, G(h).Phase);
        Assert.NotEqual(JsonValueKind.Null, h.View(null).GetProperty("reveal").ValueKind);
    }

    [Fact]
    public void Round_points_stay_hidden_until_the_reveal_so_a_burn_does_not_name_who_passed()
    {
        var h = Quiet(3, 75, out var core);
        var me = Me(h, 0);
        var him = Me(h, 1);
        Put(me, 5, 3);
        Put(him, 5, 3, 20);
        Hand(h, 0, me, fuse: 40);
        h.Tick(PotatoCore.HoldMin);
        Assert.True(h.Act(0, "pass", new { id = him.Id }).Ok, h.Reply.Message);
        h.Tick(40);
        Assert.False(S(h, 1).Alive);
        Assert.Equal(Potato.PtBurn, S(h, 0).Total);
        var seats = h.View(null).GetProperty("seats").EnumerateArray().ToList();
        Assert.All(seats, s => Assert.Equal(0, s.GetProperty("total").GetInt32()));
        Assert.All(seats, s => Assert.Equal(JsonValueKind.Null, s.GetProperty("held").ValueKind));
    }

    [Fact]
    public void Views_fly_on_every_pass_alike_whether_bots_or_players_pass()
    {
        var h = Quiet(2, 77, out var core);
        var a = Bot(h);
        var b = Bot(h, 1);
        Put(a, 8, 8);
        Put(b, 8, 8, 20);
        Hand(h, 0, a);
        a.Stand = 0;
        a.Mode = 1;
        var before = h.Outbox.Count;
        for (var t = 0; t < 30 && core.Pots[0].Carrier == a.Id; t++) h.Tick();
        Assert.Equal(b.Id, core.Pots[0].Carrier);
        // тик передачі між ботами розіслав види — так само, як після передачі гравцем
        Assert.Contains(h.Outbox.Skip(before), o => o is RoomViews);
    }

    [Fact]
    public void Per_tick_steps_and_states_of_players_are_a_subset_of_those_of_bots()
    {
        var h = Table(4, seed: 79);
        var rng = new Random(3);
        var core = Core(h);
        var botSteps = new HashSet<(int, int, int)>();
        var playerSteps = new HashSet<(int, int, int)>();
        var was = core.V.Select(v => (v.X, v.Y)).ToArray();
        for (var t = 0; t < 3000 && h.Room.Status == RoomStatus.Playing; t++)
        {
            if (G(h).Phase is not (Potato.PhaseStart or Potato.PhaseGo)) { h.Tick(); was = core.V.Select(v => (v.X, v.Y)).ToArray(); continue; }
            if (t % 9 == 0)
                for (var s = 0; s < 4; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
            if (t % 37 == 0)
                for (var s = 0; s < 4; s++) h.Act(s, "slap", new { });
            if (t % 5 == 0)
                for (var s = 0; s < 4; s++) h.Act(s, "pass", new { });
            h.Tick();
            if (core.V.Length != was.Length) was = core.V.Select(v => (v.X, v.Y)).ToArray();
            foreach (var v in core.V)
            {
                // вибулий гравець лежить у всіх на очах і з ніком — це не таємниця
                if (v.State != 3) (v.Owner >= 0 ? playerSteps : botSteps).Add((v.X - was[v.Id].X, v.Y - was[v.Id].Y, v.State));
                was[v.Id] = (v.X, v.Y);
            }
        }
        output.WriteLine($"гравці: {string.Join(" ", playerSteps.OrderBy(x => x))}");
        Assert.Contains(playerSteps, s => s.Item3 == 4);
        Assert.Subset(botSteps, playerSteps);
    }

    // ---------- людина за клавіатурою і що видно в кадрах ----------

    /// <summary>
    /// Людина: тримає стрілку 5–41 тик, відпускає на 1–31, зрідка задумується на 4–10 с. З горщиком — біжить до
    /// найближчого й за 3–10 тиків тисне «передай»; горщик поруч — частенько відходить геть.
    /// </summary>
    sealed class PotatoHuman(Random rng)
    {
        int _left, _dir = -1, _press = -1;

        public int Next(PotatoCore core, PotatoVillager me)
        {
            if (me.Pot >= 0)
            {
                if (core.PassTarget(me) >= 0)
                {
                    if (_press < 0) _press = rng.Next(3, 11);
                    if (_press-- == 0) { core.TryPass(me, null); _press = -1; }
                }
                var best = -1;
                long bd = long.MaxValue;
                foreach (var q in core.V)
                {
                    if (q == me || !q.Standing || q.Pot >= 0) continue;
                    var d = PotatoCore.Dist2(q, me.X, me.Y);
                    if (d < bd) { bd = d; best = q.Id; }
                }
                if (best >= 0 && rng.Next(100) < 80)
                {
                    int dx = core.V[best].X - me.X, dy = core.V[best].Y - me.Y;
                    return Math.Abs(dx) >= Math.Abs(dy) ? (dx > 0 ? 0 : 2) : (dy > 0 ? 1 : 3);
                }
            }
            if (_left-- > 0) return _dir;
            if (_dir >= 0 && rng.Next(100) >= 35)
            {
                _dir = -1;
                _left = rng.Next(100) < 6 ? rng.Next(100, 251) : rng.Next(0, 31);
            }
            else
            {
                _dir = rng.Next(4);
                _left = rng.Next(4, 41);
            }
            return _dir;
        }
    }

    sealed class PotatoTrace
    {
        public readonly HashSet<(int, int)> Spots = [];
        public long Standing, StandingOff;
        public readonly Dictionary<int, int> Runs = [];
        public int RunCount;
        public readonly HashSet<(int, int)> Lanes = [];
        public long Moving, MovingOff;
        public int Catches, Passes, Booms, Slaps;

        public static int RunBin(int len) => len switch { <= 3 => 0, <= 7 => 1, <= 11 => 2, <= 24 => 3, <= 50 => 4, <= 100 => 5, <= 200 => 6, _ => 7 };
        public static bool Off(int r) => Math.Abs(r - 16) > 8;
        public int Share(int from, int to) => Runs.Where(p => p.Key >= from && p.Key <= to).Sum(p => p.Value) * 1000 / Math.Max(1, RunCount);
    }

    /// <summary>
    /// Толока на голому ядрі з горщиком: 2 «людини» і 14 ботів, 6000 тиків на сід. Порядок тика — як у грі. Для кожного
    /// селянина пишемо, де він стоїть і скільки, якою смугою йде, скільки ловить і передає горщик.
    /// </summary>
    static (PotatoTrace Players, PotatoTrace Bots) Observe(int seeds, int players = 2, int bots = 14, int ticks = 6000, int pots = 1)
    {
        var pl = new PotatoTrace();
        var bt = new PotatoTrace();
        for (var seed = 1; seed <= seeds; seed++)
        {
            var core = new PotatoCore(new Random(seed));
            core.Deal([.. Enumerable.Range(0, players)], bots);
            core.Live = true;
            core.StartPots(pots);
            var humans = Enumerable.Range(0, players).Select(i => new PotatoHuman(new Random(seed * 10 + i))).ToArray();
            var n = core.N;
            var px = core.V.Select(v => v.X).ToArray();
            var py = core.V.Select(v => v.Y).ToArray();
            var run = new int[n];
            for (var t = 0; t < ticks; t++)
            {
                core.TimersAll();
                core.ThinkAll();
                foreach (var v in core.V)
                    if (v.Owner >= 0 && !v.Dead) v.Want = humans[v.Owner].Next(core, v);
                foreach (var e in core.Log)
                {
                    var who = e.Kind == PotatoEvent.Pass || e.Kind == PotatoEvent.Spawn ? core.V[e.Kind == PotatoEvent.Pass ? e.B : e.A] : core.V[e.A];
                    var side = who.Owner >= 0 ? pl : bt;
                    if (e.Kind is PotatoEvent.Pass or PotatoEvent.Spawn) side.Catches++;
                    if (e.Kind == PotatoEvent.Pass) (core.V[e.A].Owner >= 0 ? pl : bt).Passes++;
                    if (e.Kind == PotatoEvent.Boom) side.Booms++;
                    if (e.Kind == PotatoEvent.Slap) side.Slaps++;
                }
                core.Log.Clear();
                core.StepAll();
                foreach (var v in core.V)
                {
                    if (v.Dead)
                    {
                        // вибулий лежить до кінця — у «раунді» на 6000 тиків його просто воскрешаємо
                        v.Dead = false;
                        continue;
                    }
                    var side = v.Owner >= 0 ? pl : bt;
                    int rx = v.X % 32, ry = v.Y % 32;
                    var still = v.State == 0 && v.X == px[v.Id] && v.Y == py[v.Id];
                    if (still)
                    {
                        side.Standing++;
                        if (PotatoTrace.Off(rx) || PotatoTrace.Off(ry)) side.StandingOff++;
                        side.Spots.Add((rx / 4, ry / 4));
                        run[v.Id]++;
                    }
                    else
                    {
                        if (run[v.Id] > 0 && t > 200)
                        {
                            var bin = PotatoTrace.RunBin(run[v.Id]);
                            side.Runs[bin] = side.Runs.GetValueOrDefault(bin) + 1;
                            side.RunCount++;
                        }
                        run[v.Id] = 0;
                        if (v.State == 1)
                        {
                            var across = v.Dir is 0 or 2 ? ry : rx;
                            side.Moving++;
                            if (PotatoTrace.Off(across)) side.MovingOff++;
                            side.Lanes.Add((v.Dir & 1, across / 4));
                        }
                    }
                    px[v.Id] = v.X;
                    py[v.Id] = v.Y;
                }
            }
        }
        return (pl, bt);
    }

    [Fact]
    public void Players_stand_on_spots_where_bots_stand_too()
    {
        var (players, bots) = Observe(3);
        output.WriteLine($"стоять поза квадратом ±8: гравці {players.StandingOff * 100 / players.Standing}%, боти {bots.StandingOff * 100 / bots.Standing}%");
        Assert.Subset(bots.Spots, players.Spots);
        Assert.True(bots.StandingOff * 2 >= players.StandingOff * bots.Standing / players.Standing,
            $"боти стоять поза центром {bots.StandingOff}/{bots.Standing}, гравці — {players.StandingOff}/{players.Standing}");
    }

    [Fact]
    public void Players_walk_the_same_lanes_as_bots()
    {
        var (players, bots) = Observe(3);
        output.WriteLine($"ідуть поза смугою ±8: гравці {players.MovingOff * 100 / players.Moving}%, боти {bots.MovingOff * 100 / bots.Moving}%");
        Assert.Subset(bots.Lanes, players.Lanes);
        Assert.True(bots.MovingOff * 2 >= players.MovingOff * bots.Moving / players.Moving,
            $"боти йдуть поза смугою {bots.MovingOff}/{bots.Moving}, гравці — {players.MovingOff}/{players.Moving}");
    }

    [Fact]
    public void Players_stand_as_long_or_as_short_as_bots_do()
    {
        var (players, bots) = Observe(3);
        string Show(PotatoTrace s) => string.Join(" ", Enumerable.Range(0, 8).Select(b => $"{b}:{s.Share(b, b) / 10.0:F1}%"));
        output.WriteLine($"серії стояння, гравці: {Show(players)}");
        output.WriteLine($"серії стояння, боти:   {Show(bots)}");
        Assert.Subset(bots.Runs.Keys.ToHashSet(), players.Runs.Keys.ToHashSet());
        Assert.True(bots.Share(0, 2) * 2 >= players.Share(0, 2), $"коротких зупинок: боти {bots.Share(0, 2)}, гравці {players.Share(0, 2)}");
        Assert.True(bots.Share(6, 7) * 2 >= players.Share(6, 7), $"довгих стоянь: боти {bots.Share(6, 7)}, гравці {players.Share(6, 7)}");
    }

    [Fact]
    public void The_pot_keeps_moving_through_the_crowd_and_reaches_players_on_two()
    {
        var (players, bots) = Observe(6, ticks: 2250);
        var catchesPerPlayer = players.Catches / 12.0;
        var catchesPerBot = bots.Catches / (6 * 14.0);
        output.WriteLine($"за раунд: ловить гравець {catchesPerPlayer:F1}, бот {catchesPerBot:F1}; вибухів у гравців {players.Booms}, у ботів {bots.Booms}; " +
            $"передач ботами {bots.Passes / 6.0:F1}, гравцями {players.Passes / 6.0:F1}; ляпасів ботів {bots.Slaps / 6.0:F1}");
        Assert.True(bots.Passes >= 6 * 10, $"боти передали {bots.Passes}");
        Assert.True(bots.Booms >= 6 * 2, $"у ботів рвонуло {bots.Booms}");
        Assert.True(catchesPerPlayer >= 1, $"гравець за раунд ловить {catchesPerPlayer:F1}");
        Assert.True(bots.Slaps >= 6, $"боти ляснули {bots.Slaps}");
    }

    [Fact]
    public void Bots_with_the_pot_look_for_someone_but_not_at_once_and_some_run_or_hold()
    {
        var modes = new int[4];
        var waits = new List<int>();
        for (var seed = 1; seed <= 60; seed++)
        {
            var core = new PotatoCore(new Random(seed));
            core.Deal([], 10);
            core.Live = true;
            core.StartPots(1);
            var c = core.V[core.Pots[0].Carrier];
            modes[c.Mode]++;
            waits.Add(c.Stand);
        }
        output.WriteLine($"норов: шукає {modes[1]}, панікує {modes[2]}, тримає {modes[3]}; «ой гаряче» {waits.Min()}–{waits.Max()} тиків");
        Assert.True(modes[1] > modes[2] && modes[1] > modes[3]);
        Assert.True(modes[2] > 0 && modes[3] > 0);
        Assert.All(waits, w => Assert.InRange(w, PotatoCore.CatchStandMin, PotatoCore.CatchStandMax));
    }

    [Fact]
    public void Some_bots_step_away_from_a_pot_that_comes_close_and_brave_ones_do_not()
    {
        var core = new PotatoCore(new Random(4));
        core.Deal([], 12);
        foreach (var v in core.V) { PotatoCore.Forget(v); v.Stand = 100_000; Put(v, 2 + v.Id % 10, 12); }
        core.Live = true;
        core.StartPots(1);
        var carrier = core.V[core.Pots[0].Carrier];
        carrier.Mode = 3;
        carrier.ModeLeft = 100_000;
        carrier.Stand = 100_000;
        core.Pots[0].Fuse = 100_000;
        var shy = core.V.First(v => v != carrier && !v.Brave);
        var brave = core.V.First(v => v != carrier && v.Brave);
        Put(carrier, 8, 3);
        Put(shy, 8, 3, 40);
        Put(brave, 8, 3, -40);
        shy.Stand = brave.Stand = 0;
        var shyTicks = 0;
        for (var t = 0; t < 300; t++)
        {
            var was = PotatoCore.Dist2(shy, carrier.X, carrier.Y);
            core.TimersAll();
            core.ThinkAll();
            var going = shy.Shy > 0;
            core.StepAll();
            if (going)
            {
                shyTicks++;
                Assert.True(PotatoCore.Dist2(shy, carrier.X, carrier.Y) >= was, "відходить, а не наближається");
            }
            Assert.Equal(0, brave.Shy);
        }
        Assert.True(shyTicks >= PotatoCore.ShyMin, $"{shyTicks}");
    }

    // =============================================================================================
    // Контракт і детермінізм
    // =============================================================================================

    [Fact]
    public void The_server_accepts_exactly_what_the_module_sends()
    {
        var h = Table(2, seed: 81);
        var me = Me(h, 0);
        Put(me, 5, 3);
        h.Input(0, "move", new { dir = 0 });
        h.Tick();
        Assert.Equal(0, me.Want);
        h.Input(0, "move", 2);
        Assert.Equal(2, me.Want);
        h.Input(0, "move", new { dir = -1 });
        Assert.Equal(-1, me.Want);
        Go(h);
        Park(h);
        var core = Core(h);
        foreach (var p in core.Pots) p.Fuse = 100_000;
        var bot = Bot(h);
        Put(me, 5, 3, dir: 0);
        Put(bot, 5, 3, 24);
        Hand(h, 0, me);
        h.Tick(PotatoCore.HoldMin);
        Assert.True(h.Act(0, "pass", new { }).Ok, h.Reply.Message);                   // пробіл: найближчому
        Hand(h, 0, me);
        Put(bot, 5, 3, 24);
        bot.Stand = 100_000;
        h.Tick(PotatoCore.HoldMin);
        Assert.True(h.Act(0, "pass", new { id = bot.Id }).Ok, h.Reply.Message);       // клік по селянину з горщиком у руках
        Put(bot, 5, 3, 24);
        bot.Stand = 100_000;
        Assert.True(h.Act(0, "slap", new { }).Ok, h.Reply.Message);                   // E: конус
        h.Tick(PotatoCore.SlapCoolTicks);
        Assert.True(h.Act(0, "slap", new { id = bot.Id }).Ok, h.Reply.Message);       // клік по селянину без горщика
        h.Tick(PotatoCore.StunTicks);
        var before = Views.Text(h.View(0));
        foreach (var (action, payload) in new (string, object?)[] { ("move", new { dir = "up" }), ("move", new { d = 1 }), ("jump", null), ("slap", "x"), ("pass", new { id = 99 }) })
            Assert.False(h.Act(0, action, payload).Ok);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не знайшов корінь репозиторію");
    }

    [Fact]
    public void The_module_sends_only_actions_and_payloads_the_server_reads()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "potato.js"));
        var sent = System.Text.RegularExpressions.Regex.Matches(js, @"ctx\.(?:act|input)\('(\w+)'")
            .Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Equal(["move", "pass", "slap"], sent);
        Assert.Contains("ctx.input('move', { dir: d })", js);
        Assert.Contains("ctx.input('move', { dir: -1 })", js);
        Assert.Contains("ctx.act('pass', id == null ? {} : { id })", js);
        Assert.Contains("ctx.act('slap', id == null ? {} : { id })", js);
    }

    [Fact]
    public void Module_constants_match_the_server_rules()
    {
        var js = File.ReadAllText(Path.Combine(FindRoot(), "web", "games", "potato.js"));
        int Const(string name)
        {
            var m = System.Text.RegularExpressions.Regex.Match(js, @"\b" + name + @"\s*=\s*(\d+)");
            Assert.True(m.Success, name);
            return int.Parse(m.Groups[1].Value);
        }
        Assert.Equal(Potato.TickMs, Const("TICK_MS"));
        Assert.Equal(PotatoMap.Cell, Const("CELL"));
        Assert.Equal(PotatoMap.WorldW, Const("WW"));
        Assert.Equal(PotatoMap.WorldH, Const("WH"));
        Assert.Equal(PotatoCore.PassRange, Const("PASS_RANGE"));
        Assert.Equal(PotatoCore.PassRangeMax, Const("PASS_MAX"));
        Assert.Equal(PotatoCore.SlapRange, Const("SLAP_RANGE"));
        Assert.Equal(PotatoCore.SlapRangeMax, Const("SLAP_MAX"));
        Assert.Equal(PotatoCore.SlapCos2Milli, Const("SLAP_CONE"));
        Assert.Equal(PotatoCore.HoldMin * Potato.TickMs, Const("HOLD_MS"));
        Assert.Equal(PotatoCore.SlapCoolTicks * Potato.TickMs, Const("SLAP_COOL_MS"));
        Assert.Equal(PotatoCore.NoBackTicks * Potato.TickMs, Const("NO_BACK_MS"));
        Assert.Equal(Potato.RoundTicks, Const("ROUND_TICKS"));
    }

    static List<string> Replay(int seed)
    {
        var h = Table(3, seed: seed);
        var frames = new List<string>();
        for (var t = 0; t < 900; t++)
        {
            if (t % 11 == 0) h.Input(t % 3, "move", new { dir = t % 5 - 1 });
            if (t % 23 == 0) h.Act(t % 3, "pass", new { });
            if (t % 97 == 0) h.Act((t + 1) % 3, "slap", new { });
            h.Tick();
            frames.Add(Views.Text(((RoomFrame)h.Outbox.Last(o => o is RoomFrame)).Frame));
        }
        frames.Add(Views.Text(h.View(0)));
        return frames;
    }

    [Fact]
    public void Same_seed_and_same_inputs_give_byte_identical_frames()
    {
        var a = Replay(83);
        var b = Replay(83);
        Assert.Equal(a, b);
        Assert.NotEqual(a, Replay(84));
    }

    [Fact]
    public void Views_json_matches_the_spec_shape()
    {
        var h = Table(2, seed: 85);
        Go(h);
        var v = h.View(0);
        foreach (var key in new[] { "phase", "round", "of", "left", "t", "width", "height", "cell", "n", "pots", "map", "looks", "names", "v", "p", "seats", "dead", "me", "reveal", "result", "turn" })
            Assert.True(Views.Has(v, key), key);
        var n = v.GetProperty("n").GetInt32();
        Assert.Equal(24, v.GetProperty("width").GetInt32());
        Assert.Equal(16, v.GetProperty("map").GetArrayLength());
        Assert.Equal(4 * n, v.GetProperty("looks").GetArrayLength());
        Assert.Equal(n, v.GetProperty("names").GetArrayLength());
        Assert.Equal(4 * n, v.GetProperty("v").GetArrayLength());
        Assert.Equal(2, v.GetProperty("p").GetArrayLength());
        var me = v.GetProperty("me");
        foreach (var key in new[] { "id", "alive", "stun", "slapCool", "held", "passIn" }) Assert.True(Views.Has(me, key), key);
        var seat = v.GetProperty("seats")[0];
        foreach (var key in new[] { "seat", "nick", "alive", "out", "held", "total" }) Assert.True(Views.Has(seat, key), key);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("turn").ValueKind);
        output.WriteLine($"вид на {n} селян: {Views.Text(G(h).View(0)).Length} Б");
    }

    [Fact]
    public void Catalog_lists_potato_as_live_by_host_hidden_tick_forty_with_its_module_and_css()
    {
        var info = new Potato().Info;
        Assert.Equal("potato", info.Id);
        Assert.Equal("Гарячий горщик", info.Title);
        Assert.Equal(GameGroup.Live, info.Group);
        Assert.Equal((2, 8), (info.MinPlayers, info.MaxPlayers));
        Assert.Equal(40, info.TickMs);
        Assert.Equal(StartMode.ByHost, info.Start);
        Assert.True(info.Hidden);
        Assert.Equal(ScoreOrder.HigherIsBetter, info.Score);
        Assert.Equal(["rounds", "crowd", "pots"], info.Options!.Select(o => o.Key));
        var root = FindRoot();
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "potato.js")));
        Assert.True(File.Exists(Path.Combine(root, "web", "games", "potato.css")));
        Assert.Equal("фіолетовий", new Potato().SeatName(6));
        Assert.True(RoomHarness.NewRegistry().Has("potato"));
    }

    [Fact]
    public void Achievements_gift_and_cool_are_requested_only_at_the_reveal_and_only_when_earned()
    {
        Assert.NotNull(AchievementCatalog.Get("potato-gift"));
        Assert.NotNull(AchievementCatalog.Get("potato-cool"));
        // Оля підкидає Ганні — рвонуло; Петро тим часом тричі ловив горщик від ботів і виграв на час
        var h = Quiet(3, 87, out var core);
        var me = Me(h, 0);
        var ganna = Me(h, 2);
        Put(me, 5, 3);
        Put(ganna, 5, 3, 20);
        Hand(h, 0, me, fuse: 30);
        h.Tick(PotatoCore.HoldMin);
        Assert.True(h.Act(0, "pass", new { id = ganna.Id }).Ok, h.Reply.Message);
        h.Tick(30);
        Assert.False(S(h, 2).Alive);
        Assert.Empty(h.Awards);                         // посеред раунду — нічого: сповіщення назвало б Олю
        for (var i = 0; i < 3; i++)
        {
            Hand(h, 0, Me(h, 1), fuse: 100_000);
            S(h, 1).Catches++;
            h.Tick(1);
        }
        Hand(h, 0, Bot(h), fuse: 100_000);
        for (var i = 0; i < 4; i++) { Hand(h, 0, me, 100_000); h.Tick(); }
        Hand(h, 0, Bot(h), fuse: 100_000);
        h.Tick(Potato.RoundTicks);
        Assert.Equal(Potato.PhaseReveal, G(h).Phase);
        var awards = h.Awards.Select(a => (a.Nick, a.Reason, a.Shards)).ToList();
        Assert.Contains(("Оля", "ach:potato-gift", 0), awards);
        Assert.Contains(("Петро", "ach:potato-cool", 0), awards);
        Assert.Equal(2, awards.Count);
    }

    // =============================================================================================
    // Швидкодія
    // =============================================================================================

    [Fact]
    [Trait("Category", "Perf")]
    public void Three_thousand_ticks_with_eight_players_and_thirty_bots_fit_in_a_second()
    {
        long best = long.MaxValue;
        for (var attempt = 0; attempt < 3 && best >= 1000; attempt++)
        {
            var h = Table(8, seed: 90 + attempt, options: new { crowd = "big", rounds = "5" });
            var rng = new Random(attempt);
            var sw = Stopwatch.StartNew();
            for (var t = 0; t < 3000 && h.Room.Status == RoomStatus.Playing; t++)
            {
                if (t % 10 == 0)
                    for (var s = 0; s < 8; s++) h.Input(s, "move", new { dir = rng.Next(-1, 4) });
                if (t % 50 == 25)
                    for (var s = 0; s < 8; s++) h.Act(s, s % 2 == 0 ? "pass" : "slap", new { });
                h.Tick();
            }
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        var pure = PureTickMicros();
        output.WriteLine($"3000 тиків через кімнату: {best} мс; чистий Tick(): {pure:F1} мкс");
        Assert.True(best < 1000, $"{best} мс");
        Assert.True(pure < 250, $"{pure} мкс на тик");
    }

    static double PureTickMicros()
    {
        var bestUs = double.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var h = Table(8, seed: 95 + attempt, options: new { crowd = "big", rounds = "5" });
            var game = G(h);
            Go(h);
            var rng = new Random(attempt);
            for (var i = 0; i < 200; i++) game.Tick();
            var sw = Stopwatch.StartNew();
            var n = 0;
            for (var t = 0; t < 3000 && game.Phase != Potato.PhaseOver; t++)
            {
                if (t % 10 == 0)
                    for (var s = 0; s < 8; s++) game.Act(s, "move", Views.Payload(new { dir = rng.Next(-1, 4) }));
                var r = game.Tick();
                if (r.Frame) game.Frame();
                n++;
            }
            bestUs = Math.Min(bestUs, sw.Elapsed.TotalMicroseconds / Math.Max(1, n));
        }
        return bestUs;
    }

    [Fact]
    public void A_frame_with_thirty_eight_villagers_serialises_under_900_bytes()
    {
        var h = Table(8, seed: 97, options: new { crowd = "big" });
        Go(h);
        Assert.Equal(38, Core(h).N);
        var max = 0;
        for (var t = 0; t < 400 && G(h).Phase == Potato.PhaseGo; t++)
        {
            h.Tick();
            max = Math.Max(max, Views.Text(G(h).Frame()).Length);
        }
        output.WriteLine($"кадр на 38 селян: найбільший {max} Б");
        Assert.True(max < 900, $"{max} Б");
    }
}
