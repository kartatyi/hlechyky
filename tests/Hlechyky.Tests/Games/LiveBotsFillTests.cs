using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Xunit;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «🤖 Боти доповнюють стіл» (пакет 1, 19.10): двоє-троє людей кличуть ботів і грають повним столом
/// <c>1 + Most</c>. Most = 1 — усе як було (бот лише з самотньою людиною).
/// </summary>
public class LiveBotsFillTests
{
    sealed class Ctx(params string?[] nicks) : IRoomContext
    {
        public string?[] Nicks = nicks;
        public string RoomId => "t";
        public int Players => Nicks.Count(n => n is not null);
        public int Round => 1;
        public Random Rng { get; } = new(1);
        public IClock Clock => throw new NotSupportedException();
        public IReadOnlyDictionary<string, string> Options { get; } = new Dictionary<string, string>();
        public IServiceProvider Services => throw new NotSupportedException();
        public string? NickOf(int seat) => seat >= 0 && seat < Nicks.Length ? Nicks[seat] : null;
        public bool Seated(int seat) => NickOf(seat) is not null;
        public int? HostSeat => Array.FindIndex(Nicks, n => n is not null) is var i && i >= 0 ? i : null;
        public void Finish(int[] winners, string log, IReadOnlyDictionary<int, long>? scores = null, string? verdict = null) { }
        public void Log(string text) { }
        public void Say(string text) { }
        public void Score(int seat, double value, int? attempts = null) { }
        public void Award(int seat, int shards, string reason) { }
    }

    static JsonElement On(bool on) => JsonSerializer.SerializeToElement(new { on });

    /// <summary>Стіл на 8 місць з <paramref name="humans"/> людьми підряд з місця 0.</summary>
    static Ctx Table(int humans) => new([.. Enumerable.Range(0, 8).Select(i => i < humans ? $"гравець{i}" : null)]);

    [Theory]
    // most, людей, ботів у партії, чи можна кликнути, чи пропонуємо кнопку (не кликали)
    [InlineData(1, 1, 1, true, true)]
    [InlineData(1, 2, 0, false, false)]
    [InlineData(1, 3, 0, false, false)]
    [InlineData(1, 4, 0, false, false)]
    [InlineData(3, 1, 3, true, true)]
    [InlineData(3, 2, 2, true, true)]
    [InlineData(3, 3, 1, true, true)]
    [InlineData(3, 4, 0, false, false)]
    public void Count_Active_Switch_Offer(int most, int humans, int bots, bool canCall, bool offer)
    {
        var ctx = Table(humans);
        var b = new SoloBot(most);
        Assert.Equal(most, b.Most);
        Assert.Equal(1 + most, b.Fill);
        Assert.Equal(0, b.Count(ctx, 8));                 // не кликали — ботів нема
        Assert.False(b.Active(ctx, 8));
        Assert.Equal(offer, b.Offer(ctx, 8));
        var r = b.Switch(ctx, 0, On(true), 8);
        Assert.Equal(canCall, r.Ok);
        if (!canCall)
        {
            Assert.Equal(most == 1 ? "Бот грає лише з тим, хто сам за столом" : $"Боти доповнюють стіл лише до {1 + most}", r.Message);
            return;
        }
        Assert.Equal(bots, b.Count(ctx, 8));
        Assert.Equal(bots > 0, b.Active(ctx, 8));
        Assert.True(b.Offer(ctx, 8));                     // кликали — кнопка «Прогнати» лишається
        Assert.Null(b.CanStart(ctx, 8));
        Assert.True(b.Switch(ctx, 0, On(false), 8).Ok);   // прогнати — завжди
        Assert.Equal(0, b.Count(ctx, 8));
    }

    [Fact]
    public void Texts_for_one_bot_stay_and_many_bots_say_how_many()
    {
        var one = new SoloBot();
        Assert.Equal("🤖 Бот сів навпроти", one.Switch(Table(1), 0, On(true), 4).Message);
        Assert.Equal("Бот пішов", one.Switch(Table(1), 0, On(false), 4).Message);

        var many = new SoloBot(3);
        Assert.Equal("🤖 Боти сіли: 3", many.Switch(Table(1), 0, On(true), 8).Message);
        Assert.Equal("Боти пішли", many.Switch(Table(1), 0, On(false), 8).Message);
        Assert.Equal("🤖 Боти доповнять стіл до 4", many.Switch(Table(2), 0, On(true), 8).Message);
        Assert.Equal("Ботів кличе господар столу", many.Switch(Table(2), 1, On(true), 8).Message);
        Assert.Equal("🤖 Рівень ботів", LiveBots.LevelFor(3).Label);
        Assert.Equal("🤖 Бот, коли граєш сам", LiveBots.LevelFor(1).Label);
        Assert.Equal(LiveBots.LevelOption.Key, LiveBots.LevelsOption.Key);
    }

    [Fact]
    public void A_friend_in_the_lobby_means_fewer_bots_and_free_seats_cap_them()
    {
        var ctx = Table(1);
        var b = new SoloBot(3);
        Assert.True(b.Switch(ctx, 0, On(true), 4).Ok);
        Assert.Equal(3, b.Count(ctx, 4));
        ctx.Nicks[1] = "друг";
        Assert.Equal(2, b.Count(ctx, 4));
        ctx.Nicks[2] = "сусід";
        Assert.Equal(1, b.Count(ctx, 4));
        ctx.Nicks[3] = "кума";
        Assert.Equal(0, b.Count(ctx, 4));
        Assert.False(b.Active(ctx, 4));
        Assert.True(b.Offer(ctx, 4));                     // кликали — прогнати ще можна
        // Місць менше, ніж треба до повного: ботів — скільки влізе.
        Assert.Equal(1, b.Count(Table(2), 3));
        Assert.Equal(0, b.Count(new Ctx(null, null), 2)); // за столом нікого — ботів нема
        b.Most = 0;                                       // менше одного не буває
        Assert.Equal(1, b.Most);
    }

    // ---------- кімнати: двоє людей + боти грають повним столом ----------

    /// <summary>Поганяти тики до кінця партії (люди — манекени, нічого не тиснуть).</summary>
    static void PlayOut(RoomHarness h, int maxTicks)
    {
        for (var t = 0; t < maxTicks && h.Room.Status == RoomStatus.Playing; t++) h.Tick();
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    /// <summary>Партія з ботами на двох людях записується як людська: рядки — лише людям, бот-переможець — у winners.</summary>
    static void AssertHumansOnly(RoomHarness h, int[] bots)
    {
        var fin = Assert.Single(h.Finished);
        Assert.Equal(2, fin.Seats.Count(n => n is not null));       // рядки game_results пишуться лише людям
        foreach (var b in bots) Assert.Null(fin.Seats[b]);
        Assert.Null(fin.Result.Verdict);                            // не «сам із ботами» — звичайний кінець
        if (fin.Result.Winners.Any(bots.Contains))
            Assert.False(fin.Result.Draw);                          // переміг бот — людям поразка, а не нічия
    }

    static RoomHarness TwoWithBots(string game, object? options = null, int seed = 3)
    {
        var h = new RoomHarness(game, options, seed);
        h.Join("Оля");
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        h.Join("Петро");
        return h;
    }

    [Fact]
    public void Curve_two_people_play_with_two_bots_to_the_end()
    {
        var h = TwoWithBots("curve");
        var v = h.View(0);
        Assert.True(v.GetProperty("botOffer").GetBoolean());
        Assert.Equal(4, v.GetProperty("botFill").GetInt32());
        Assert.Equal([2, 3], v.GetProperty("bot").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = (CurveGame)h.Room.Game;
        Assert.Equal([2, 3], g.Bots);
        h.Join("Ігор");                                             // посеред партії склад не міняється
        Assert.Equal([2, 3], g.Bots);
        PlayOut(h, 120_000);
        AssertHumansOnly(h, [2, 3]);
    }

    [Fact]
    public void Curve_a_third_in_the_lobby_leaves_one_bot()
    {
        var h = TwoWithBots("curve");
        h.Join("Ігор");
        Assert.Equal([3], h.View(0).GetProperty("bot").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal([3], ((CurveGame)h.Room.Game).Bots);
    }

    [Fact]
    public void Bomber_two_people_play_with_two_bots_to_the_end()
    {
        var h = TwoWithBots("bomber");
        Assert.Equal([2, 3], h.View(0).GetProperty("bot").EnumerateArray().Select(x => x.GetInt32()));
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.NotNull(h.Room.SafeSeatBot(2));
        Assert.NotNull(h.Room.SafeSeatBot(3));
        PlayOut(h, 200_000);
        AssertHumansOnly(h, [2, 3]);
    }

    [Fact]
    public void Bomber_a_third_in_the_lobby_leaves_one_bot()
    {
        var h = TwoWithBots("bomber");
        h.Join("Ігор");
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.NotNull(h.Room.SafeSeatBot(3));
        Assert.Equal([3], h.View(0).GetProperty("bot").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void Icefloe_two_people_play_with_one_bot_to_the_end()
    {
        var h = TwoWithBots("icefloe");
        Assert.Equal(3, h.View(0).GetProperty("botFill").GetInt32());
        Assert.True(h.Start().Ok, h.Reply.Message);
        var g = (Icefloe)h.Room.Game;
        Assert.Equal([2], g.Bots);
        PlayOut(h, 200_000);
        AssertHumansOnly(h, [2]);
    }

    [Fact]
    public void Icefloe_a_third_in_the_lobby_sends_the_bots_away()
    {
        var h = TwoWithBots("icefloe");
        h.Join("Ігор");
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Empty(((Icefloe)h.Room.Game).Bots);
    }

    [Fact]
    public void One_bot_games_still_refuse_a_bot_for_two()
    {
        var h = new RoomHarness("hockey");
        h.Join("Оля");
        h.Join("Петро");
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
    }
}
