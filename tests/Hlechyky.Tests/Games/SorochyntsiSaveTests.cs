using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Сорочинський ярмарок» переживає перезапуск сервера (контракт §8): Save → нова гра → Configure/Start/Load/Resumed посеред
/// ходу, у фазі discard і guest дає ті самі види всім місцям і глядачу (строк — зсунутий на паузу), і партію можна дограти.
/// </summary>
public class SorochyntsiSaveTests
{
    static readonly TimeSpan Pause = TimeSpan.FromSeconds(47);

    /// <summary>Перезапуск «на місці»: знімок → пауза → нова гра в ту саму кімнату, як це робить RoomsFreeze.Resume.</summary>
    static Sorochyntsi Restart(RoomHarness h, TimeSpan pause)
    {
        var room = h.Room;
        lock (room.Sync)
        {
            var old = (Sorochyntsi)room.Game;
            var json = old.Save();
            Assert.False(string.IsNullOrEmpty(json));
            h.Clock.Advance(pause);
            var g = new Sorochyntsi { Ctx = old.Ctx };
            g.Configure(room.Options);
            g.Start();
            g.Load(json!);
            g.Resumed(pause);
            room.Game = g;
            return g;
        }
    }

    static (string[] Views, DateTimeOffset? Deadline) Look(RoomHarness h)
    {
        DateTimeOffset? deadline = null;
        var list = new List<string>();
        foreach (var seat in new int?[] { 0, 1, 2, 3, null })
        {
            var o = JsonNode.Parse(h.View(seat).GetRawText())!.AsObject();
            if (o["deadline"] is { } d) deadline = d.GetValue<DateTimeOffset>();
            o.Remove("deadline");
            list.Add(o.ToJsonString());
        }
        return ([.. list], deadline);
    }

    /// <summary>Перезапустити й звірити: види ті самі, строк — пізніший рівно на паузу.</summary>
    static void Same(RoomHarness h)
    {
        var (before, dl) = Look(h);
        var game = Restart(h, Pause);
        Assert.Same(game, h.Room.Game);
        var (after, dl2) = Look(h);
        for (var i = 0; i < before.Length; i++) Assert.Equal(before[i], after[i]);
        Assert.Equal(dl + (dl is null ? TimeSpan.Zero : Pause), dl2);
    }

    /// <summary>Двоє людей (0 і 1) і два Глеки (2 і 3).</summary>
    static RoomHarness Table(int seed = 5)
    {
        var h = new RoomHarness("sorochyntsi", new { timer = "60", bots = "2", vp = "16" }, seed: seed);
        h.Join("Оля");
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        return h;
    }

    [Fact]
    public void Mid_turn_restart_keeps_every_view_and_the_game_plays_on()
    {
        var h = Table();
        var c = SoRig.CoreOf(h);
        // Кілька ходів, щоб були і крамниці, і бронь наосліп.
        for (var i = 0; i < 14; i++) SoRig.Step(h, 0);
        while (c.Turn is not (0 or 1)) SoRig.Step(h, 0);
        var s = c.Turn;
        Assert.True(h.Act(s, "reserve", new { tier = 2 }).Ok);
        while (c.Turn is not (0 or 1)) SoRig.Step(h, 0);
        var reserved = c.Reserved.Select(r => r.ToArray()).ToList();
        Same(h);
        var core = SoRig.CoreOf(h);
        Assert.NotSame(c, core);
        Assert.Equal(reserved, core.Reserved.Select(r => r.ToArray()));
        // І на ході Глека (botIn — той самий: строк Глека теж зсунувся).
        SoRig.Step(h, 0);
        while (SoRig.CoreOf(h).Turn is 0 or 1) SoRig.Step(h, 0);
        Same(h);
        core = SoRig.CoreOf(h);
        for (var i = 0; i < 3000 && h.Finished.Count == 0; i++) SoRig.Step(h, 0);
        Assert.Single(h.Finished);
        SorochyntsiBotTests.Invariants(core);
    }

    [Fact]
    public void Restart_in_the_discard_and_guest_phases_keeps_the_phase_and_its_deadline()
    {
        var h = Table(6);
        var c = SoRig.CoreOf(h);
        while (c.Turn != 0) SoRig.Step(h, 0);
        c.Table = [0, 5, 7, 9, 2];
        SoRig.Own(c, 0, 0, 1, 2, 4, 8, 9, 10, 12, 16, 17, 18);
        SoRig.Hand(c, 0, 2, 2, 2, 2, 2);
        Assert.True(h.Act(0, "take", new { goods = new[] { "linen", "fish", "herbs" } }).Ok);
        Assert.Equal(SoPhase.Discard, c.Phase);
        Same(h);
        var g = SoRig.CoreOf(h);
        Assert.Equal(SoPhase.Discard, g.Phase);
        Assert.Equal(3, g.DiscardN);
        Assert.True(h.Act(0, "discard", new { give = new { linen = 1, fish = 1, herbs = 1 } }).Ok);
        Assert.Equal(SoPhase.Guest, g.Phase);
        Same(h);
        g = SoRig.CoreOf(h);
        Assert.Equal([0, 5], g.GuestChoice);
        Assert.True(h.Act(0, "guest", new { guest = 0 }).Ok);
        Assert.Equal([0], g.GuestsOf[0]);
        Assert.Equal(1, g.Turn);
    }

    static bool Away(RoomHarness h, int seat) =>
        h.View(null).GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat).GetProperty("away").GetBoolean();

    [Fact]
    public void Ya_tut_works_after_a_restart_and_a_restart_after_it_changes_nothing()
    {
        var h = Table(7);
        var c = SoRig.CoreOf(h);
        // Оля (місце 0) двічі проспала свій хід — за неї Глек.
        for (var i = 0; i < 2; i++)
        {
            while (c.Turn != 0) SoRig.Step(h, 1);
            h.Clock.Advance(61);
            Assert.True(h.Act(1, "timeout").Ok);
            c = SoRig.CoreOf(h);
        }
        Assert.True(Away(h, 0));
        Same(h);
        Assert.True(Away(h, 0));
        Assert.True(h.Act(0, "back").Ok);
        Assert.False(Away(h, 0));
        Same(h);
        Assert.False(Away(h, 0));
        Assert.Equal("Ти й так за прилавком", h.Act(0, "back").Message);
    }

    /// <summary>Рядки «майже булава» серед того, що Глек сказав за столом, про <paramref name="nick"/>.</summary>
    static int NearLines(RoomHarness h, string nick, int skip)
    {
        var re = SorochyntsiLines.Lines(SoMoment.NearBulava)
            .Select(t => new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Escape(t), @"\\\{\w+}", ".+?") + "$")).ToList();
        return h.Outbox.OfType<TableSaid>().Skip(skip).Count(s => s.Line.Text.Contains(nick) && re.Any(r => r.IsMatch(s.Line.Text)));
    }

    /// <summary>Один простий хід того, чия черга: товари (перші купи), повернути перші жетони, перший гість.</summary>
    static void Plain(RoomHarness h)
    {
        var c = SoRig.CoreOf(h);
        var s = c.Turn;
        var me = h.View(s).GetProperty("me");
        var must = me.GetProperty("must").GetString();
        if (must == "discard")
        {
            var give = new Dictionary<string, int>();
            var left = me.GetProperty("discard").GetInt32();
            for (var i = 0; i < 6 && left > 0; i++)
            {
                var n = Math.Min(left, c.Tok[s][i]);
                if (n > 0) give[SorochyntsiCards.Tokens[i]] = n;
                left -= n;
            }
            Assert.True(h.Act(s, "discard", new { give }).Ok);
        }
        else if (must == "guest") Assert.True(h.Act(s, "guest", new { guest = me.GetProperty("guests")[0].GetInt32() }).Ok);
        else if (me.GetProperty("can").GetProperty("take").GetBoolean())
        {
            var take = me.GetProperty("take");
            var goods = take.GetProperty("piles").EnumerateArray().Take(take.GetProperty("need").GetInt32()).Select(x => x.GetString()).ToArray();
            Assert.True(h.Act(s, "take", new { goods }).Ok);
        }
        else if (me.GetProperty("can").GetProperty("reserveDeck")[0].GetBoolean()) Assert.True(h.Act(s, "reserve", new { tier = 1 }).Ok);
        else Assert.True(h.Act(s, "pass", new { }).Ok);
    }

    [Theory]
    [InlineData("said", 0)]      // знімок пам'ятає, що Глек уже попередив Олю — після перезапуску не повторює
    [InlineData("old", 0)]       // знімок старої збірки без поля: хто зараз «майже при булаві» — уже попереджений
    [InlineData("fresh", 1)]     // контроль: нікого не попереджав — і після перезапуску скаже (раз)
    public void Near_bulava_is_said_once_per_person_even_across_a_restart(string snapshot, int expected)
    {
        var h = new RoomHarness("sorochyntsi", new { timer = "none", bots = "0", vp = "16" }, seed: 3);
        h.Join("Оля");
        h.Join("Петро");
        Assert.True(h.Start().Ok);
        var c = SoRig.CoreOf(h);
        SoRig.Own(c, 0, 7, 15, 23, 31, 39, 70, 73);       // 13 ⭐, усі буденні знижки, без цвіту — «майже при булаві»
        var room = h.Room;
        lock (room.Sync)
        {
            var old = (Sorochyntsi)room.Game;
            var node = JsonNode.Parse(old.Save()!)!.AsObject();
            if (snapshot == "said") node["nearSaid"] = new JsonArray("Оля");
            else if (snapshot == "old") node.Remove("nearSaid");
            else node["nearSaid"] = new JsonArray();
            var g = new Sorochyntsi { Ctx = old.Ctx };
            g.Configure(room.Options);
            g.Start();
            g.Load(node.ToJsonString());
            room.Game = g;
        }
        var skip = h.Outbox.OfType<TableSaid>().Count();
        for (var i = 0; i < 40; i++)
        {
            h.Clock.Advance(25);
            Plain(h);
        }
        Assert.Equal(expected, NearLines(h, "Оля", skip));
        Assert.Equal(-1, SoRig.CoreOf(h).Bulava);
    }

    [Fact]
    public void Restored_core_counts_bonuses_stars_and_icons_from_the_shops_again()
    {
        var c = SorochyntsiBotTests.Core(4, 12);
        for (var i = 0; i < 120 && !c.Over; i++) SorochyntsiBot.Step(c, c.Turn);
        var back = SorochyntsiCore.FromSnapshot(JsonSerializer.Deserialize<SoCoreSnap>(JsonSerializer.Serialize(c.Snapshot()))!);
        Assert.Equal(JsonSerializer.Serialize(c.Snapshot()), JsonSerializer.Serialize(back.Snapshot()));
        Assert.Equal(c.Bonus, back.Bonus);
        Assert.Equal(c.Order.Select(c.Points), back.Order.Select(back.Points));
        Assert.Equal(c.Icons, back.Icons);
        SorochyntsiBotTests.Invariants(back);
        Assert.Null(new Sorochyntsi().Save());
    }
}
