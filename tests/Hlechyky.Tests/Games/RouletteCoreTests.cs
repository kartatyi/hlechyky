using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Правила й виплати рулетки (specs/roulette.md §2, §9 «RouletteCoreTests»): колесо, кольори, ключі полів.</summary>
public class RouletteCoreTests
{
    static readonly int[] Reds = [1, 3, 5, 7, 9, 12, 14, 16, 18, 19, 21, 23, 25, 27, 30, 32, 34, 36];

    [Fact]
    public void Every_number_has_the_right_colour()
    {
        Assert.Equal("g", RouletteCore.ColorOf(0));
        for (var n = 1; n <= 36; n++) Assert.Equal(Reds.Contains(n) ? "r" : "b", RouletteCore.ColorOf(n));
        Assert.Equal(18, Enumerable.Range(0, 37).Count(n => RouletteCore.ColorOf(n) == "r"));
        Assert.Equal(18, Enumerable.Range(0, 37).Count(n => RouletteCore.ColorOf(n) == "b"));
    }

    [Fact]
    public void Wheel_order_has_all_37_pockets_once()
    {
        Assert.Equal(37, RouletteCore.Wheel.Count);
        Assert.Equal(Enumerable.Range(0, 37), RouletteCore.Wheel.Order());
        Assert.Equal(0, RouletteCore.Wheel[0]);
        Assert.Equal(32, RouletteCore.Wheel[1]);
        Assert.Equal(26, RouletteCore.Wheel[^1]);
    }

    static void Pays(string spot, int k, int hit, int miss)
    {
        Assert.Equal(k, RouletteCore.Pays(spot));
        Assert.Equal(10 * (k + 1), RouletteCore.Return(spot, 10, hit));
        Assert.Equal(0, RouletteCore.Return(spot, 10, miss));
    }

    [Fact] public void Straight_pays_35_to_1() { Pays("straight:17", 35, 17, 18); Assert.Equal(36, RouletteCore.Return("straight:17", 1, 17)); }

    [Fact]
    public void Return_is_long_and_never_truncated()
    {
        Assert.Equal(36L * int.MaxValue, RouletteCore.Return("straight:17", int.MaxValue, 17));
        Assert.Equal(36L * RouletteCore.MaxPerSpot, RouletteCore.MaxReturn([("straight:17", RouletteCore.MaxPerSpot)]));
        Assert.True(36L * RouletteCore.MaxPerSpot <= int.MaxValue);
        Assert.True(36L * (RouletteCore.MaxPerSpot + 1) > int.MaxValue);
    }
    [Fact] public void Split_pays_17_to_1() { Pays("split:17-20", 17, 20, 18); Pays("split:17-18", 17, 17, 20); }
    [Fact] public void Street_pays_11_to_1() => Pays("street:13-14-15", 11, 14, 16);
    [Fact] public void Corner_pays_8_to_1() => Pays("corner:17-18-20-21", 8, 21, 19);
    [Fact] public void Line_pays_5_to_1() => Pays("line:13-14-15-16-17-18", 5, 18, 19);
    [Fact] public void Column_pays_2_to_1() { Pays("column:1", 2, 34, 35); Pays("column:3", 2, 3, 1); }
    [Fact] public void Dozen_pays_2_to_1() { Pays("dozen:2", 2, 13, 25); Pays("dozen:3", 2, 36, 12); }

    [Fact]
    public void Even_money_bets_pay_1_to_1()
    {
        Pays("red", 1, 1, 2);
        Pays("black", 1, 2, 1);
        Pays("even", 1, 2, 1);
        Pays("odd", 1, 1, 2);
        Pays("low", 1, 18, 19);
        Pays("high", 1, 19, 18);
    }

    [Fact]
    public void Zero_loses_every_outside_bet()
    {
        Assert.Equal(12, RouletteCore.Outside.Count);
        foreach (var spot in RouletteCore.Outside) Assert.Equal(0, RouletteCore.Return(spot, 100, 0));
        Assert.Equal(360, RouletteCore.Return("straight:0", 10, 0));
        Assert.Equal(180, RouletteCore.Return("split:0-2", 10, 0));
        Assert.Equal(120, RouletteCore.Return("street:0-1-2", 10, 0));
        Assert.Equal(90, RouletteCore.Return("corner:0-1-2-3", 10, 0));
    }

    [Fact]
    public void Every_valid_spot_parses_and_round_trips()
    {
        Assert.Equal(157, RouletteCore.All.Count);
        var byType = RouletteCore.All.GroupBy(RouletteCore.Type).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(37, byType["straight"]);
        Assert.Equal(60, byType["split"]);
        Assert.Equal(14, byType["street"]);
        Assert.Equal(23, byType["corner"]);
        Assert.Equal(11, byType["line"]);
        Assert.Equal(3, byType["column"]);
        Assert.Equal(3, byType["dozen"]);
        foreach (var spot in RouletteCore.All)
        {
            Assert.Equal(spot, RouletteCore.Canon(spot));
            var nums = RouletteCore.Covers(spot);
            Assert.NotEmpty(nums);
            var type = RouletteCore.Type(spot);
            if (type is "straight" or "split" or "street" or "corner" or "line")
            {
                // числа задом наперед — той самий канон, ті самі числа
                var back = RouletteCore.Canon($"{type}:{string.Join('-', nums.Reverse())}");
                Assert.Equal(spot, back);
                Assert.Equal(nums, RouletteCore.Covers(back!));
                Assert.Equal(spot, RouletteCore.FromParts(type, [.. nums.Reverse()], null));
            }
            Assert.False(string.IsNullOrEmpty(RouletteCore.Label(spot)));
        }
    }

    [Fact]
    public void Invalid_spots_are_refused()
    {
        foreach (var bad in new[] { "split:3-4", "split:1-5", "corner:3-4-6-7", "street:2-3-4", "line:2-3-4-5-6-7", "column:4",
                     "straight:37", "dozen:0", "red:1", "split:0-4", "corner:0-1-2", "", "nonsense", "straight:-1", "line:1-2-3" })
            Assert.Null(RouletteCore.Canon(bad));
        Assert.Equal("Такої ставки на полі нема", RouletteCore.Read(Views.Payload(new { spot = "split:3-4", amount = 5 }), out _));
        Assert.Equal("Не зрозумів ставки", RouletteCore.Read(Views.Payload(new { type = "split", numbers = "17,20" }), out _));
        Assert.Equal("Не зрозумів ставки", RouletteCore.Read(Views.Payload(new { type = "zigzag", numbers = new[] { 1 } }), out _));
        Assert.Equal("Не зрозумів ставки", RouletteCore.Read(Views.Payload(new { amount = 5 }), out _));
        Assert.Equal("Не зрозумів ставки", RouletteCore.Read(Views.Payload(new { spot = 17 }), out _));
        Assert.Equal("Такої ставки на полі нема", RouletteCore.Read(Views.Payload(new { type = "dozen", target = 4 }), out _));
    }

    [Fact]
    public void Numbers_in_any_order_become_one_spot()
    {
        Assert.Equal("split:17-20", RouletteCore.FromParts("split", [20, 17], null));
        Assert.Equal("split:17-20", RouletteCore.FromParts("split", [17, 20], null));
        Assert.Null(RouletteCore.Read(Views.Payload(new { type = "split", numbers = new[] { 20, 17 }, amount = 1 }), out var a));
        Assert.Equal("split:17-20", a);
        Assert.Null(RouletteCore.Read(Views.Payload(new { type = "dozen", target = 2, amount = 5 }), out var d));
        Assert.Equal("dozen:2", d);
        Assert.Null(RouletteCore.Read(Views.Payload(new { spot = "corner:21-17-20-18" }), out var c));
        Assert.Equal("corner:17-18-20-21", c);
        // і spot, і type — береться spot
        Assert.Null(RouletteCore.Read(Views.Payload(new { spot = "red", type = "black" }), out var r));
        Assert.Equal("red", r);
    }

    [Fact]
    public void Labels_are_human()
    {
        Assert.Equal("Число 17", RouletteCore.Label("straight:17"));
        Assert.Equal("Спліт 17·20", RouletteCore.Label("split:17-20"));
        Assert.Equal("Вулиця 13·14·15", RouletteCore.Label("street:13-14-15"));
        Assert.Equal("Кут 17·18·20·21", RouletteCore.Label("corner:17-18-20-21"));
        Assert.Equal("Перші чотири", RouletteCore.Label("corner:0-1-2-3"));
        Assert.Equal("Лінія 13–18", RouletteCore.Label("line:13-14-15-16-17-18"));
        Assert.Equal("Колонка 2", RouletteCore.Label("column:2"));
        Assert.Equal("Дюжина 13–24", RouletteCore.Label("dozen:2"));
        Assert.Equal("Червоне", RouletteCore.Label("red"));
        Assert.Equal("19–36", RouletteCore.Label("high"));
    }
}
