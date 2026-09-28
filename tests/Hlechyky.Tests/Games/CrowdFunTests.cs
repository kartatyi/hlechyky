using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Юрма, прохід №3: детектив для вибулих (п. 121) і смішинки на розкритті (п. 124).
/// </summary>
public class CrowdFunTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван"];

    static RoomHarness Table(int players, int seed)
    {
        var h = new RoomHarness("crowd", null, seed: seed);
        foreach (var nick in Nicks.Take(players)) h.Join(nick);
        h.Start();
        for (var i = 0; i < 200 && G(h).Phase != Crowd.PhaseGo; i++) h.Tick();
        Assert.Equal(Crowd.PhaseGo, G(h).Phase);
        // юрма стоїть і не купує — спалахи ботів тестам не заважають
        foreach (var v in G(h).CoreForTests.V)
        {
            CrowdCore.Forget(v);
            v.Stand = 100_000;
            v.Want = -1;
            v.Moving = false;
        }
        return h;
    }

    static Crowd G(RoomHarness h) => (Crowd)h.Room.Game;
    static CrowdSeat S(RoomHarness h, int seat) => G(h).SeatForTests(seat);
    static CrowdVillager Me(RoomHarness h, int seat) => G(h).CoreForTests.V[S(h, seat).Me];

    /// <summary>Місце <paramref name="hunter"/> збиває місце <paramref name="victim"/> прицільним камінцем.</summary>
    static void Kill(RoomHarness h, int hunter, int victim)
    {
        var a = Me(h, hunter);
        var b = Me(h, victim);
        a.X = b.X - 60;
        a.Y = b.Y;
        Assert.True(h.Act(hunter, "shoot", new { id = b.Id }).Ok, h.Reply.Message);
        h.Tick();
        if (G(h).Phase == Crowd.PhaseGo) h.Tick(Crowd.ShotCoolTicks);
    }

    static string[] Fun(RoomHarness h) =>
        [.. h.View(null).GetProperty("reveal").GetProperty("fun").EnumerateArray().Select(x => x.GetString()!)];

    [Fact]
    public void Only_the_eliminated_can_guess_and_a_right_guess_pays_one_point_on_reveal()
    {
        var h = Table(3, seed: 5);
        Assert.False(h.Act(1, "guess", new { id = Me(h, 2).Id, seat = 2 }).Ok);
        Assert.Equal("Вгадують ті, кого вже збили — а ти ще в грі", h.Reply.Message);

        Kill(h, 0, 1);
        Assert.False(S(h, 1).Alive);
        Assert.False(h.Act(1, "guess", new { id = Me(h, 0).Id, seat = 1 }).Ok);      // себе не вгадують
        Assert.True(h.Act(1, "guess", new { id = Me(h, 2).Id, seat = 2 }).Ok, h.Reply.Message);

        // свою здогадку бачить лише вгадувач
        Assert.Equal([2, Me(h, 2).Id], h.View(1).GetProperty("me").GetProperty("guess").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("me").GetProperty("guess").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("me").ValueKind);

        var before = S(h, 1).Total;
        Kill(h, 0, 2);   // живий лишився один — розкриття; збитого вгадано ще до пострілу
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        Assert.Equal(1, S(h, 1).GuessHits);
        Assert.Equal(before + Crowd.PtGuess, S(h, 1).Total);
        var row = h.View(null).GetProperty("reveal").GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("seat").GetInt32() == 1);
        Assert.Equal(1, row.GetProperty("guess").GetInt32());
        Assert.Equal(1, row.GetProperty("pts").GetInt32());
        Assert.Contains("🕵 Петро вгадав одного — +1", Fun(h));
    }

    [Fact]
    public void A_wrong_guess_pays_nothing_and_one_villager_carries_one_name()
    {
        var h = Table(4, seed: 7);
        Kill(h, 0, 1);
        var bot = G(h).CoreForTests.V.First(v => v.Owner < 0).Id;
        Assert.True(h.Act(1, "guess", new { id = bot, seat = 2 }).Ok, h.Reply.Message);
        Assert.True(h.Act(1, "guess", new { id = bot, seat = 3 }).Ok, h.Reply.Message);   // переніс ім'я
        Assert.Equal(-1, S(h, 1).Guess[2]);
        Assert.Equal(bot, S(h, 1).Guess[3]);
        Assert.True(h.Act(1, "guess", new { id = -1, seat = 3 }).Ok, h.Reply.Message);    // забрав
        Assert.Equal(-1, S(h, 1).Guess[3]);
        Assert.True(h.Act(1, "guess", new { id = bot, seat = 2 }).Ok, h.Reply.Message);
        Assert.False(h.Act(1, "guess", new { id = bot, seat = 1 }).Ok);                   // збитого не вгадують
        Assert.False(h.Act(1, "guess", new { id = 999, seat = 2 }).Ok);
        Kill(h, 0, 2);
        Kill(h, 0, 3);
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        Assert.Equal(0, S(h, 1).GuessHits);
        Assert.DoesNotContain(Fun(h), l => l.StartsWith("🕵"));
    }

    [Fact]
    public void Reveal_jokes_name_the_sniper_and_the_bluffer_whom_somebody_fell_for()
    {
        var h = Table(3, seed: 9);
        // Оля купує не зі свого списку
        var off = Enumerable.Range(0, 12).First(k => Array.IndexOf(S(h, 0).List, k) < 0);
        var st = CrowdMap.Stalls[off];
        var me = Me(h, 0);
        me.X = st.C0 % CrowdMap.W * CrowdMap.Cell + CrowdMap.Cell / 2;
        me.Y = st.C0 / CrowdMap.W * CrowdMap.Cell + CrowdMap.Cell / 2;
        Assert.True(h.Act(0, "buy", new { }).Ok, h.Reply.Message);
        h.Tick(Crowd.HaggleTicks + 1);
        Assert.Equal(1, S(h, 0).Bluffs);
        Assert.Equal(0, S(h, 0).Bought);

        // Петро «купився»: камінець у бота біля того самого прилавка
        var bot = G(h).CoreForTests.V.First(v => v.Owner < 0);
        bot.X = st.Fx + 20;
        bot.Y = st.Fy;
        var petro = Me(h, 1);
        petro.X = bot.X + 60;
        petro.Y = bot.Y;
        Assert.True(h.Act(1, "shoot", new { id = bot.Id }).Ok, h.Reply.Message);
        h.Tick(Crowd.ShotCoolTicks + 1);
        Assert.Equal(1, S(h, 0).Fooled);

        // Ганна першим же камінцем — у Петра, потім в Олю
        Kill(h, 2, 1);
        Kill(h, 2, 0);
        Assert.Equal(Crowd.PhaseReveal, G(h).Phase);
        var fun = Fun(h);
        Assert.Contains("🎯 Снайпер: Ганна — першим же камінцем", fun);
        Assert.Contains("🥸 Найкращий блеф: Оля купив не зі свого списку, а Петро на це купився", fun);
        Assert.True(fun.Length <= Crowd.FunMax);
    }

    [Fact]
    public void A_faithful_bot_standing_next_to_a_player_gets_a_line_with_seconds()
    {
        var h = Table(2, seed: 11);
        var me = Me(h, 0);
        var bot = G(h).CoreForTests.V.First(v => v.Owner < 0);
        var other = Me(h, 1);
        other.X = 5 * CrowdMap.Cell;             // другий гравець подалі — пара «поруч» не рахується
        other.Y = 18 * CrowdMap.Cell;
        foreach (var v in G(h).CoreForTests.V)
            if (v.Owner < 0 && v != bot) { v.X = 28 * CrowdMap.Cell + 16; v.Y = 16 * CrowdMap.Cell + 16; }
        bot.X = me.X + 10;
        bot.Y = me.Y;
        h.Tick(Crowd.TrailEvery * 30);            // 30 проб ≈ 14 с
        Kill(h, 0, 1);
        var line = Assert.Single(Fun(h), l => l.StartsWith("🐑"));
        var name = CrowdMap.Names[bot.Name];
        Assert.StartsWith($"🐑 Оля мав вірного хвостика: {name} ", line);
        Assert.EndsWith(bot.Name % 2 == 0 ? "трималась поруч" : "тримався поруч", line);
    }
}
