using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Нагороди не чують глини на колі (08.11, владік: «як я поставив червону глину — ціна возу стала більша»). Віз, Око,
/// купець, гості, ярмарок, гостинці й ціни виробів міряються так, ніби на колі своя чорна глина з її рівнем
/// (<see cref="Clicker.PayMinute"/>); клік, пасив і полиця глину на колі, як і були, чують (<see cref="Clicker.PlayMinute"/> —
/// твій справжній темп для окупності).
/// </summary>
public class ClickerClayPayTests
{
    static readonly string[] AllClays = ["red", "white", "black", "blue", "kaolin"];

    static RoomHarness Wheel(ClickerGuildService? svc = null)
    {
        var h = new RoomHarness("clicker", services: svc is null ? null : RoomHarness.WithService(svc));
        h.Solo("Оля");
        Assert.True(h.Act(0, "look").Ok);
        // Трохи пасиву, кліка й частки пасиву в кліку — щоб глина мала що множити.
        Patch(h, s =>
        {
            s["upgrades"]!["apprentice"] = 40;
            s["upgrades"]!["wheel"] = 9;
            s["upgrades"]!["swing"] = 5;
        });
        return h;
    }

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonObject House(JsonObject s) => (s["house"] as JsonObject) ?? (JsonObject)(s["house"] = new JsonObject());

    /// <summary>Поставити глину на коло просто в збереженні — без відлежування й без ціни.</summary>
    static void Clay(RoomHarness h, string key) => Patch(h, s =>
    {
        var house = House(s);
        house["clays"] = new JsonArray(AllClays.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        house["clay"] = key;
    });

    static (double Play, double Pay) Minutes(RoomHarness h)
    {
        lock (h.Room.Sync)
        {
            var c = (Clicker)h.Room.Game;
            return (c.PlayMinute, c.PayMinute);
        }
    }

    static long Pots(RoomHarness h) => h.View(0).GetProperty("pots").GetInt64();

    [Fact]
    public void The_pay_minute_is_the_play_minute_on_your_own_black_clay_whatever_is_on_the_wheel()
    {
        var h = Wheel();
        var (plain, pay) = Minutes(h);
        Assert.True(pay > plain, $"чорна щедріша на полицю: {pay} проти {plain}");

        Clay(h, "black");
        var (black, payBlack) = Minutes(h);
        Assert.Equal(black, pay);                                   // мірка нагород — рівно хвилина гри на своїй чорній
        Assert.Equal(pay, payBlack);

        Clay(h, "red");
        var (red, payRed) = Minutes(h);
        Assert.Equal(pay, payRed);
        Assert.True(red > plain * 1.5, $"червона: {red} проти {plain}");   // клік ×2 — темп справді вищий, але нагород не чіпає

        foreach (var key in new[] { "white", "blue", "kaolin" })
        {
            Clay(h, key);
            Assert.Equal(pay, Minutes(h).Pay);
        }
    }

    [Fact]
    public void Your_level_on_black_clay_raises_the_rewards_even_with_another_clay_on_the_wheel()
    {
        var h = Wheel();
        Clay(h, "blue");
        var fresh = Minutes(h).Pay;
        Patch(h, s => House(s)["clayFormed"] = new JsonObject { ["black"] = Clicker.ClayLevelAt[^1] });
        Assert.Equal("blue", h.View(0).GetProperty("house").GetProperty("clay").GetString());
        Assert.True(Minutes(h).Pay > fresh, "«бабусина» чорна платить більше за свіжу");
    }

    [Fact]
    public void Black_clay_never_touches_click_or_passive_so_item_prices_do_not_hear_the_clay()
    {
        // PayPassive і PayClick рахуються без глини, бо чорна їх не міняє на жодному рівні — сторож цієї умови.
        var black = Clicker.Clays.Single(c => c.Key == "black");
        for (var level = 0; level <= Clicker.ClayLevelAt.Length; level++)
        {
            var c = Clicker.ClayLevelled(black, level);
            Assert.Equal(1, c.Click);
            Assert.Equal(1, c.Passive);
        }
    }

    [Fact]
    public void Red_clay_before_the_wagon_no_longer_changes_its_reward()
    {
        long Claim(string clay)
        {
            var svc = new ClickerGuildService(new FakeStore(), new FakeClock());
            var h = Wheel(svc);
            Clay(h, clay);
            var need = svc.Summary(ClickerGuildService.Key("Оля"), h.Clock.UtcNow).Today.Chests[0].Need;
            Patch(h, s => s["craft"]!["items"] = new JsonObject { ["pot||1"] = need });
            Assert.True(h.Act(0, "guild", new { op = "give", key = "pot||1", n = need, keep = false }).Ok);
            var before = Pots(h);
            var r = h.Act(0, "guild", new { op = "claim" });
            Assert.True(r.Ok, r.Message);
            return Pots(h) - before;
        }

        var plain = Claim("");
        Assert.True(plain > 0);
        Assert.Equal(plain, Claim("red"));
        Assert.Equal(plain, Claim("white"));
        Assert.Equal(plain, Claim("black"));
    }

    [Fact]
    public void The_merchant_still_gives_black_on_the_wheel_its_catcher_bonus()
    {
        long Merchant(string clay)
        {
            var h = Wheel();
            Clay(h, clay);
            Patch(h, s =>
            {
                var now = h.Clock.UtcNow;
                s["golden"] = new JsonObject
                {
                    ["at"] = now.ToString("O"),
                    ["until"] = (now + Clicker.GoldenShown).ToString("O"),
                    ["kind"] = (int)Clicker.GoldenKind.Merchant,
                    ["x"] = 10,
                    ["y"] = 10,
                };
            });
            var before = Pots(h);
            Assert.True(h.Act(0, "catch").Ok);
            return Pots(h) - before;
        }

        var plain = Merchant("");
        Assert.True(plain > 0);
        Assert.Equal(plain, Merchant("red"));
        Assert.InRange((double)Merchant("black") / plain, 1.49, 1.51);   // ×1,5 «для ловця» — поверх тієї самої мірки
    }
}
