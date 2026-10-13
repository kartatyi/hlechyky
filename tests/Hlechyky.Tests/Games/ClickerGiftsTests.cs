using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Подарунки гончарям із конфігу (ClickerGifts.cs): 13.10 серії smaug і Назара порвав вихід з кола й поганий зв'язок —
/// майстерня додає до серії й кладе черепки, раз на подарунок.
/// </summary>
public class ClickerGiftsTests
{
    static (RoomHarness H, FakeStakes Stakes) Wheel(string nick, params ClickerGift[] gifts)
    {
        var stakes = new FakeStakes();
        var services = new ServiceCollection()
            .Configure<ClickerOptions>(o => o.Gifts.AddRange(gifts))
            .AddSingleton<IStakes>(stakes)
            .BuildServiceProvider();
        var h = new RoomHarness("clicker", services: services);
        h.Solo(nick);
        return (h, stakes);
    }

    static int Streak(RoomHarness h) => h.View(0).GetProperty("fall").GetProperty("streak").GetInt32();
    static ActResult Spin(RoomHarness h) => h.Act(0, "spin", PotterHands.Human(5));

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static ClickerGift Sorry(string nick, int streak, int shards) => new()
    {
        Id = "streak-1013-" + nick, Nick = nick, Streak = streak, Shards = shards, Note = "🏺 Дядько Глек вибачається за розбиту серію",
    };

    [Fact]
    public void The_gift_adds_to_the_streak_and_pays_shards_once()
    {
        var (h, stakes) = Wheel("Smaug", Sorry("smaug", 1120, 5000));
        Patch(h, s => s["fallStreak"] = 343);

        var r = Spin(h);
        Assert.True(r.Ok);
        Assert.Contains("Дядько Глек вибачається", r.Message);
        Assert.Contains("+1120 до серії · +5000 черепків", r.Message);
        Assert.Equal(1463, Streak(h));
        Assert.Equal(5000, stakes.Balance("Smaug"));
        Assert.Equal(Clicker.GiftReason, stakes.Reasons["clicker-gift:streak-1013-smaug"]);

        // Удруге — нічого: ні серії, ні черепків, ні тосту.
        var again = Spin(h);
        Assert.DoesNotContain("вибачається", again.Message);
        Assert.Equal(1463, Streak(h));
        Assert.Equal(5000, stakes.Balance("Smaug"));
    }

    [Fact]
    public void The_gift_is_remembered_in_the_save()
    {
        var (h, stakes) = Wheel("Назар", Sorry("назар", 300, 3000));
        Patch(h, s => s["fallStreak"] = 110);
        Assert.True(Spin(h).Ok);
        Patch(h, _ => { });                                    // кімнату вивантажили й підняли з бази
        Assert.True(Spin(h).Ok);

        Assert.Equal(410, Streak(h));
        Assert.Single(stakes.Ledger);
    }

    [Fact]
    public void Someone_else_gets_nothing()
    {
        var (h, stakes) = Wheel("владік", Sorry("smaug", 1120, 5000));
        Patch(h, s => s["fallStreak"] = 7);
        var r = Spin(h);

        Assert.DoesNotContain("вибачається", r.Message);
        Assert.Equal(7, Streak(h));
        Assert.Empty(stakes.Ledger);
    }

    [Fact]
    public void Only_a_real_successful_act_takes_the_gift()
    {
        // Тиха дія (коло зникло з очей, дзвоник цеху) й відмова подарунка не забирають: гончар мусить бути тут і бачити тост.
        var (h, stakes) = Wheel("Smaug", Sorry("smaug", 1120, 5000));
        Assert.True(h.Act(0, "sight", new { on = false }).Ok);
        Assert.False(h.Act(0, "grab").Ok);                     // глек ще на полиці
        Assert.Equal(0, Streak(h));
        Assert.Empty(stakes.Ledger);

        Assert.Contains("вибачається", h.Act(0, "sight", new { on = true }).Message);
        Assert.Equal(1120, Streak(h));
    }

    [Fact]
    public void A_gift_with_a_letter_shows_the_letter_until_it_is_closed()
    {
        var gift = Sorry("smaug", 1120, 5000);
        gift.Title = "Лист від Дядька Глека";
        gift.Letter = "Шановна пані Smaug!\n\nПишу носиком.";
        var (h, stakes) = Wheel("Smaug", gift);
        Patch(h, s => s["fallStreak"] = 343);

        var r = Spin(h);
        Assert.DoesNotContain("вибачається", r.Message);   // з листом — без тосту: вікно скаже більше
        Assert.Equal(1463, Streak(h));
        Assert.Equal(5000, stakes.Balance("Smaug"));
        var letter = h.View(0).GetProperty("letter");
        Assert.Equal("streak-1013-smaug", letter.GetProperty("id").GetString());
        Assert.Equal("Лист від Дядька Глека", letter.GetProperty("title").GetString());
        Assert.StartsWith("Шановна пані Smaug!", letter.GetProperty("text").GetString());
        Assert.Equal("+1120 до серії · +5000 черепків", letter.GetProperty("gift").GetString());

        Patch(h, _ => { });                                    // лист чекає й після перезапуску
        Assert.Equal(JsonValueKind.Object, h.View(0).GetProperty("letter").ValueKind);
        Assert.True(h.Act(0, "letter", new { id = "streak-1013-smaug" }).Ok);
        Assert.True(!h.View(0).TryGetProperty("letter", out var gone) || gone.ValueKind == JsonValueKind.Null);
        Assert.Equal(1463, Streak(h));                         // закрив лист — подарунок лишається
    }
}
