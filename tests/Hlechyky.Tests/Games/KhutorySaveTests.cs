using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Хутори» переживають перезапуск сервера (контракт §8): Save → нова гра → Configure/Start/Load/Resumed у кожній фазі
/// дає ті самі види всім місцям і глядачу (строк хода — зсунутий на паузу), і партію можна дограти Глеками.
/// </summary>
public class KhutorySaveTests
{
    static readonly string[] Crew = ["Оля", "Петро"];
    static readonly TimeSpan Pause = TimeSpan.FromSeconds(47);

    /// <summary>Двоє людей (місця 0 і 1) і два Глеки (2 і 3), Кармелюк як у книжці (щоб було кого грабувати).</summary>
    static RoomHarness Table(int seed = 5, string timer = "90")
    {
        var h = new RoomHarness("khutory", new { map = "random", timer, bots = "2", friendly = "0" }, seed: seed);
        foreach (var n in Crew) h.Join(n);
        Assert.True(h.Start().Ok);
        return h;
    }

    static Khutory GameOf(RoomHarness h) => (Khutory)h.Room.Game;
    static KhutoryCore CoreOf(RoomHarness h) => GameOf(h).Core;
    static void Rearm(RoomHarness h) => KhutoryBotTests.Rearm(h);

    static void Give(KhutoryCore c, int s, int wood = 0, int clay = 0, int wool = 0, int grain = 0, int ore = 0) =>
        KhutoryBotTests.Give(c, s, wood, clay, wool, grain, ore);

    /// <summary>Перезапуск «на місці»: знімок → пауза → нова гра в ту саму кімнату так, як це робить RoomsFreeze.Resume.</summary>
    static Khutory Restart(RoomHarness h, TimeSpan pause)
    {
        var room = h.Room;
        lock (room.Sync)
        {
            var old = (Khutory)room.Game;
            var json = old.Save();
            Assert.False(string.IsNullOrEmpty(json));
            h.Clock.Advance(pause);
            var g = new Khutory { Ctx = old.Ctx };
            g.Configure(room.Options);
            g.Start();
            g.Load(json!);
            g.Resumed(pause);
            room.Game = g;
            return g;
        }
    }

    static readonly int?[] Viewers = [0, 1, 2, 3, null];

    /// <summary>Види всіх місць і глядача без строку (він законно зсувається) + сам строк окремо.</summary>
    static (string[] views, DateTimeOffset? deadline) Look(Func<int?, JsonElement> view)
    {
        DateTimeOffset? deadline = null;
        var list = new List<string>();
        foreach (var seat in Viewers)
        {
            var o = JsonNode.Parse(view(seat).GetRawText())!.AsObject();
            if (o["deadline"] is { } d) deadline = d.GetValue<DateTimeOffset>();
            o.Remove("deadline");
            list.Add(o.ToJsonString());
        }
        return ([.. list], deadline);
    }

    static (string[] views, DateTimeOffset? deadline) Look(RoomHarness h) => Look(h.View);

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

    /// <summary>Дограти Глеками й таймаутами (люди мовчать — після двох проспаних за них теж Глек).</summary>
    static void PlayOut(RoomHarness h)
    {
        for (var steps = 0; h.Finished.Count == 0; steps++)
        {
            Assert.True(steps < 8000, "партія не кінчається");
            if (h.View(0).GetProperty("botIn") is { ValueKind: JsonValueKind.Number } b)
            {
                h.Clock.AdvanceMs(Math.Max(1, b.GetInt32()));
                Assert.True(h.Act(0, "bot").Ok);
            }
            else
            {
                h.Clock.Advance(181);
                Assert.True(h.Act(0, "timeout").Ok);
            }
        }
        var c = CoreOf(h);
        Assert.True(c.Over);
        Assert.NotNull(c.Winner);
        Assert.True(c.Points(c.Winner!.Value) >= c.Target);
        for (var r = 0; r < 5; r++) Assert.Equal(KhutoryCore.BankEach, c.Bank[r] + Enumerable.Range(0, 4).Sum(s => c.Hand[s][r]));
        Assert.Single(h.Finished);
    }

    /// <summary>Один крок до потрібного стану: Глек (якщо черга його) або автохід за людину прямо в ядрі.</summary>
    static void Step(RoomHarness h, Random rng)
    {
        if (h.View(0).GetProperty("botIn") is { ValueKind: JsonValueKind.Number } b)
        {
            h.Clock.AdvanceMs(Math.Max(1, b.GetInt32()));
            Assert.True(h.Act(0, "bot").Ok);
            return;
        }
        var c = CoreOf(h);
        var s = Enumerable.Range(0, 4).Select(i => (c.Turn + i) % 4).First(c.MayAct);
        Assert.Null(KhutoryAuto.Step(c, s, rng));
        Rearm(h);
    }

    static void Until(RoomHarness h, Func<KhutoryCore, bool> done)
    {
        var rng = new Random(3);
        for (var i = 0; !done(CoreOf(h)); i++)
        {
            Assert.True(i < 400, "не дійшли до потрібного стану");
            Step(h, rng);
        }
    }

    /// <summary>Розставились (як грали б), руки порожні, хід <paramref name="turn"/> одразу після кидка.</summary>
    static KhutoryCore Main(RoomHarness h, int turn)
    {
        Until(h, c => c.Phase != KhPhase.Setup);
        var c = CoreOf(h);
        for (var s = 0; s < 4; s++)
            for (var r = 0; r < 5; r++) { c.Bank[r] += c.Hand[s][r]; c.Hand[s][r] = 0; }
        c.Phase = KhPhase.Main;
        c.Turn = turn;
        c.TurnIdx = Array.IndexOf(c.Order, turn);
        c.Rolled = true;
        c.Dice = [2, 3];
        Rearm(h);
        return c;
    }

    // =========================================================================================
    // Розстановка
    // =========================================================================================

    [Fact]
    public void Setup_town_of_the_first_round_survives_a_restart()
    {
        var h = Table();
        var c = CoreOf(h);
        Assert.Equal(KhPhase.Setup, c.Phase);
        Assert.False(c.SetupRoad);
        Same(h);
        PlayOut(h);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Setup_steps_survive_a_restart(int round, bool road)
    {
        var h = Table(seed: 6 + round);
        Until(h, c => c.Phase == KhPhase.Setup && c.SetupRoad == road && (c.SetupIdx < c.Seats) == (round == 1) && c.SetupIdx % c.Seats > 0);
        var c0 = CoreOf(h);
        var towns = c0.VOwner.Count(o => o >= 0);
        Same(h);
        var c = CoreOf(h);
        Assert.NotSame(c0, c);
        Assert.Equal(towns, c.VOwner.Count(o => o >= 0));
        Assert.Equal(c0.SetupTown, c.SetupTown);
        PlayOut(h);
    }

    // =========================================================================================
    // Кидок, сімка, Кармелюк
    // =========================================================================================

    [Fact]
    public void Roll_survives_a_restart_and_deck_order_is_kept()
    {
        var h = Table();
        Until(h, c => c.Phase == KhPhase.Roll && c.Rounds >= 1);
        var deck = CoreOf(h).Deck.ToArray();
        var seq = CoreOf(h).Seq;
        Same(h);
        Assert.Equal(deck, CoreOf(h).Deck);
        Assert.Equal(seq, CoreOf(h).Seq);
        PlayOut(h);
    }

    /// <summary>Сімка на кидку Олі: Петро й Глек скидають; Петро вже скинув — Глек ще ні.</summary>
    static KhutoryCore Seven(RoomHarness h)
    {
        var c = Main(h, 0);
        c.Phase = KhPhase.Roll;
        c.Rolled = false;
        c.Dice = null;
        Give(c, 1, wood: 5, ore: 4);
        Give(c, 2, clay: 8);
        Give(c, 3, wool: 2, grain: 1);
        Give(c, 0, grain: 2);
        Assert.Null(c.Roll(0, 3, 4));
        Rearm(h);
        Assert.Equal(KhPhase.Discard, c.Phase);
        return c;
    }

    [Fact]
    public void Discard_with_several_players_survives_a_restart()
    {
        var h = Table();
        var c = Seven(h);
        Assert.Equal([0, 4, 4, 0], c.DiscardNeed);
        Same(h);
        // Скидає й Петро після перезапуску — сам, своєю рукою.
        Assert.True(h.Act(1, "discard", new { res = new { wood = 2, ore = 2 } }).Ok);
        Assert.Equal(5, CoreOf(h).Total(1));
        Same(h);
        PlayOut(h);
    }

    [Fact]
    public void Robber_and_steal_survive_a_restart()
    {
        var h = Table();
        var c = Seven(h);
        Assert.Null(c.Discard(1, [2, 0, 0, 0, 2]));
        Assert.Null(c.Discard(2, [0, 4, 0, 0, 0]));
        Rearm(h);
        Assert.Equal(KhPhase.Robber, c.Phase);
        Same(h);

        // Гекс, де стоять двоє інших з картами, — далі крадіжка з вибором.
        c = CoreOf(h);
        var hex = Enumerable.Range(0, KhutoryGeo.Hexes).FirstOrDefault(x => x != c.Robber
            && c.OwnersAt(x).Count(o => o != 0 && c.Total(o) > 0) >= 2, -1);
        Assert.True(hex >= 0, "нема гекса з двома сусідами");
        Assert.True(h.Act(0, "robber", new { h = hex }).Ok);
        c = CoreOf(h);
        Assert.Equal(KhPhase.Steal, c.Phase);
        var from = c.StealFrom.ToArray();
        Same(h);
        Assert.Equal(from, CoreOf(h).StealFrom);
        Assert.True(h.Act(0, "steal", new { seat = from[0] }).Ok);
        Assert.Equal(KhPhase.Main, CoreOf(h).Phase);
        PlayOut(h);
    }

    // =========================================================================================
    // Торг
    // =========================================================================================

    [Fact]
    public void Open_offers_and_counter_offers_survive_a_restart()
    {
        var h = Table();
        var c = Main(h, 0);
        Give(c, 0, wood: 2, clay: 1);
        Give(c, 1, ore: 2, wool: 1);
        Give(c, 2, ore: 1);
        Rearm(h);
        Assert.True(h.Act(0, "offer", new { give = new { wood = 2 }, get = new { ore = 1 } }).Ok);
        Assert.True(h.Act(0, "offer", new { give = new { clay = 1 }, get = new { wool = 1 }, to = 1 }).Ok);
        Assert.True(h.Act(1, "offer", new { give = new { ore = 1 }, get = new { clay = 1 } }).Ok);   // зустрічна
        var first = c.Offers[0].Id;
        Assert.True(h.Act(1, "answer", new { id = first, ok = true }).Ok);
        Assert.Equal(3, c.Offers.Count);
        Same(h);
        c = CoreOf(h);
        Assert.Equal(3, c.Offers.Count);
        Assert.Equal([1], c.Offers[0].Acc);
        // Угода зі знімка проходить, і нові пропозиції беруть свіжі номери, а не повторюють старі.
        Assert.True(h.Act(0, "deal", new { id = first, seat = 1 }).Ok);
        Assert.Equal(1, c.Hand[0][4]);
        Assert.True(h.Act(0, "offer", new { give = new { clay = 1 }, get = new { ore = 1 }, to = 2 }).Ok);
        Assert.Equal(c.Offers.Max(o => o.Id), c.Offers[^1].Id);
        Assert.Equal(c.Offers.Select(o => o.Id).Distinct().Count(), c.Offers.Count);
        PlayOut(h);
    }

    [Fact]
    public void Glek_waiting_for_answers_keeps_waiting_and_does_not_offer_twice()
    {
        var h = Table();
        var c = Main(h, 2);
        var spare = new[] { 0, 1 }.First(r => c.Rates(2)[r] >= 3);
        Give(c, 2, grain: 2, ore: 2);
        c.Hand[2][spare] += 4; c.Bank[spare] -= 4;
        Give(c, 0, ore: 1);
        Rearm(h);
        h.Clock.AdvanceMs(BoardBots.ThinkMs);
        Assert.True(h.Act(0, "bot").Ok);
        var offer = Assert.Single(c.Offers);
        Assert.Equal(2, offer.From);
        Assert.True(h.Act(1, "answer", new { id = offer.Id, ok = false }).Ok);
        // Другий Глек відповідає сам; Оля мовчить — Глек на ходу чекає її до 10 с.
        for (var i = 0; i < 5 && !offer.Acc.Contains(3) && !offer.Dec.Contains(3); i++)
        {
            h.Clock.AdvanceMs(Math.Max(1, h.View(0).GetProperty("botIn").GetInt32()));
            h.Act(0, "bot");
        }
        Assert.True(offer.Dec.Contains(3));
        h.Clock.AdvanceMs(1000);
        var botIn = h.View(0).GetProperty("botIn").GetInt32();
        Assert.InRange(botIn, 1, KhutoryBot.OfferWaitMs - 1000);
        Same(h);
        // Чекає рівно стільки, скільки лишалось до перезапуску: раніше не штовхається.
        Assert.Equal(botIn, h.View(0).GetProperty("botIn").GetInt32());
        h.Clock.AdvanceMs(botIn - 1);
        Assert.False(h.Act(0, "bot").Ok);
        h.Clock.AdvanceMs(1);
        Assert.True(h.Act(0, "bot").Ok);
        c = CoreOf(h);
        Assert.Empty(c.Offers);   // не дочекався Олі — скасував
        for (var i = 0; i < 20 && c.Turn == 2; i++)
        {
            Assert.Empty(c.Offers.Where(o => o.From == 2));   // за цей хід удруге не пропонує
            h.Clock.AdvanceMs(BoardBots.ThinkMs);
            Assert.True(h.Act(0, "bot").Ok);
        }
        Assert.NotEqual(2, c.Turn);
        PlayOut(h);
    }

    // =========================================================================================
    // Толока, кінець
    // =========================================================================================

    [Fact]
    public void Toloka_roads_survive_a_restart()
    {
        var h = Table();
        var c = Main(h, 0);
        c.Devs[0].Add(new KhDev { K = KhutoryCore.Toloka });
        c.Deck.Remove(KhutoryCore.Toloka);
        Rearm(h);
        Assert.True(h.Act(0, "play", new { k = "road" }).Ok);
        Assert.Equal(KhPhase.Road2, c.Phase);
        Same(h);
        c = CoreOf(h);
        Assert.Equal(KhPhase.Road2, c.Phase);
        Assert.Equal(2, c.Road2Left);
        Assert.True(c.PlayedDev);
        PlayOut(h);
    }

    [Fact]
    public void Finished_game_survives_a_restart_without_a_second_finish()
    {
        var h = Table();
        PlayOut(h);
        Same(h);
        Assert.True(CoreOf(h).Over);
        Assert.Single(h.Finished);
        Assert.Equal("over", h.View(0).GetProperty("phase").GetString());
    }

    // =========================================================================================
    // Строки
    // =========================================================================================

    [Fact]
    public void Resumed_shifts_the_turn_deadline_by_the_pause()
    {
        var h = Table(timer: "60");
        var c = Main(h, 0);
        var dl = h.View(0).GetProperty("deadline").GetDateTimeOffset();
        h.Clock.Advance(20);
        var left = dl - h.Clock.UtcNow;
        var pause = TimeSpan.FromMinutes(3);
        Restart(h, pause);
        Assert.Equal(dl + pause, h.View(0).GetProperty("deadline").GetDateTimeOffset());
        // Пауза — не за рахунок Олі: стара межа минула, а час ще є.
        h.Clock.Advance(left - TimeSpan.FromSeconds(1));
        Assert.False(h.Act(0, "timeout").Ok);
        Assert.Equal(0, CoreOf(h).Turn);
        h.Clock.Advance(2);
        Assert.True(h.Act(0, "timeout").Ok);
        Assert.NotEqual(0, CoreOf(h).Turn);
    }

    [Fact]
    public void Glek_voice_does_not_retell_the_history_after_a_restart()
    {
        var h = Table();
        Until(h, c => c.Rounds >= 2);
        Restart(h, Pause);
        var heard = (long)typeof(Khutory).GetField("_heardSeq", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(h.Room.Game)!;
        Assert.Equal(CoreOf(h).Seq, heard);
    }

    // =========================================================================================
    // Каркас: заморозка й відновлення столу (як на деплої)
    // =========================================================================================

    static FrozenTables RoundTrip(FrozenTables t)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        return JsonSerializer.Deserialize<FrozenTables>(JsonSerializer.Serialize(t, json), json)!;
    }

    [Fact]
    public void Rooms_freeze_and_restore_continue_the_game_with_the_same_views()
    {
        var h = Table();
        var c = Main(h, 0);
        Give(c, 0, wood: 2);
        Give(c, 1, ore: 1);
        Rearm(h);
        Assert.True(h.Act(0, "offer", new { give = new { wood = 2 }, get = new { ore = 1 } }).Ok);
        var (before, dl) = Look(h);
        var snap = RoundTrip(h.Rooms.Freeze());
        h.Clock.Advance(Pause);
        var rooms = new Rooms(h.Registry, h.Clock, new GameEvents(), h.Stakes, new FakeStore(), RoomHarness.Empty()) { SeedOverride = 99 };
        Assert.Equal(1, rooms.Restore(snap).Continued);
        var room = rooms.Find(h.RoomId)!;
        Assert.Equal(RoomStatus.Playing, room.Status);
        Assert.NotSame(h.Room.Game, room.Game);
        JsonElement View(int? seat)
        {
            lock (room.Sync) return Views.Json(room.Game.View(seat));
        }
        var (after, dl2) = Look(View);
        for (var i = 0; i < before.Length; i++) Assert.Equal(before[i], after[i]);
        Assert.Equal(dl + Pause, dl2);

        // Догравання в новому процесі: Глеки й таймаути до перемоги.
        var nick = room.Seats[0]!;
        for (var steps = 0; room.Status == RoomStatus.Playing; steps++)
        {
            Assert.True(steps < 8000, "відновлена партія не кінчається");
            var payload = Views.Payload(null);
            if (View(0).GetProperty("botIn") is { ValueKind: JsonValueKind.Number } b)
            {
                h.Clock.AdvanceMs(Math.Max(1, b.GetInt32()));
                Assert.True(rooms.Act(room.Id, nick, "bot", payload).Reply.Ok);
            }
            else
            {
                h.Clock.Advance(181);
                Assert.True(rooms.Act(room.Id, nick, "timeout", payload).Reply.Ok);
            }
        }
        Assert.Equal(RoomStatus.Finished, room.Status);
        Assert.True(((Khutory)room.Game).Core.Winner is not null);
    }
}
