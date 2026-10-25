using System.Text.Json.Nodes;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Звірка аналітика з живими збереженнями — лише з CLK_ADVISE_SAVES=&lt;тека з save-&lt;нік&gt;.json&gt;, інакше Skip.</summary>
public sealed class ClickerSavesFactAttribute : FactAttribute
{
    public ClickerSavesFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLK_ADVISE_SAVES")))
            Skip = "звірка з живими збереженнями — лише з CLK_ADVISE_SAVES";
    }
}

/// <summary>
/// Аналітик кола (аналітика кола, етап 2): модель години гри для кожної своєї глини, множники з «до стелі», ворота,
/// гроші без діла, обпал. Стан гончаря аналітик не чіпає. Звірка з числами 25.10 (smaug / владік / Назар) — на
/// збереженнях поза репозиторієм (<see cref="ClickerSavesFactAttribute"/>): чужі збереження в git не кладемо.
/// </summary>
public class ClickerAdvisorTests
{
    static RoomHarness Wheel(Action<JsonObject>? edit = null)
    {
        var h = new RoomHarness("clicker");
        h.Solo("Назар");
        Patch(h, s =>
        {
            s["upgrades"] = new JsonObject { ["apprentice"] = 60, ["kiln"] = 60, ["workshop"] = 80, ["fair"] = 40, ["wheel"] = 60, ["flywheel"] = 4, ["basket"] = 5 };
            s["pots"] = 1e9;
            s["total"] = 1e10;
            edit?.Invoke(s);
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

    static void Clays(JsonObject s, string on, params string[] owned)
    {
        if (s["house"] is not JsonObject house) s["house"] = house = new JsonObject();
        house["clays"] = new JsonArray([.. owned.Select(c => (JsonNode)c)]);
        house["clay"] = on;
    }

    static ClickerAdvice Advise(RoomHarness h, double cps = Clicker.AdviseCps)
    {
        lock (h.Room.Sync) return ((Clicker)h.Room.Game).Advise(cps);
    }

    [Fact]
    public void Advising_leaves_the_potter_exactly_as_he_was()
    {
        var h = Wheel(s => Clays(s, "white", "white", "black", "blue"));
        string before, after;
        lock (h.Room.Sync) before = h.Room.Game.Save()!;
        Advise(h);
        lock (h.Room.Sync) after = h.Room.Game.Save()!;
        Assert.Equal(before, after);
    }

    [Fact]
    public void With_a_long_streak_black_wins_the_hour_on_the_wheel_and_blue_wins_forming()
    {
        // Довга серія — полиця головна, і чорна (полиця частіше й щедріша) б'є білу біля кола.
        var h = Wheel(s => { Clays(s, "white", "white", "black", "blue"); s["fallStreak"] = 2000; });
        var a = Advise(h);
        Assert.Equal(["", "white", "black", "blue"], a.Clays.Select(c => c.Key));
        var play = a.Tips.Single(t => t.Kind == "clay-play");
        Assert.Equal("black", play.Facts["clay"]);
        var white = a.Clays.Single(c => c.Key == "white").Hour.Play;
        var black = a.Clays.Single(c => c.Key == "black").Hour.Play;
        Assert.Equal(black / white, play.Gain, 6);
        Assert.True(play.Gain > 1.5);
        Assert.Equal("blue", a.Tips.Single(t => t.Kind == "clay-form").Facts["clay"]);
        Assert.DoesNotContain(a.Tips, t => t.Kind == "clay-leave");      // біла й так найкраща без гравця
        // Модель — та сама, що в «поточної»: година з поточною глиною дорівнює рядку білої.
        Assert.Equal(white, a.Hour.Play, 3);
    }

    [Fact]
    public void Early_on_with_no_streak_white_is_still_best_at_the_wheel()
    {
        var a = Advise(Wheel(s => Clays(s, "white", "white", "black")));
        Assert.DoesNotContain(a.Tips, t => t.Kind == "clay-play");
    }

    [Fact]
    public void Gates_and_marks_out_of_reach_are_not_advised()
    {
        var a = Advise(Wheel());
        Assert.All(a.Tips.Where(t => t.Cost > 0 && t.Kind is "slava" or "toloka-gate" or "mark-passive"),
            t => Assert.True(t.Cost <= Math.Max(1e9, a.Hour.Play * Clicker.AdviseReachHours), t.Kind));
        Assert.All(a.Tips, t => Assert.True(t.Gain < 100, t.Kind + " ×" + t.Gain));
    }

    [Fact]
    public void Leaving_on_black_clay_hears_about_white()
    {
        var h = Wheel(s => Clays(s, "black", "white", "black"));
        var leave = Advise(h).Tips.Single(t => t.Kind == "clay-leave");
        Assert.Equal("white", leave.Facts["clay"]);
        Assert.True(leave.Gain > 1.4);
    }

    [Fact]
    public void An_empty_album_shows_the_stove_and_the_missing_cells_with_their_gain()
    {
        var a = Advise(Wheel());
        var stove = a.Tips.Single(t => t.Kind == "album-stove");
        Assert.Equal(Clicker.StoveSlots, stove.Facts["slots"]);
        Assert.Equal(0.58, (double)stove.Facts["add"], 6);                  // 12 розкішних по 4 % і вся піч дзвінка — +10 %
        var grid = a.Tips.Single(t => t.Kind == "album-grid");
        Assert.Equal(Clicker.AlbumSize, ((List<string>)grid.Facts["cells"]).Count);
        var album = a.Mult.Single(m => m.Key == "album");
        Assert.True(album.Max > album.Now);
    }

    [Fact]
    public void Villages_and_guests_say_how_much_respect_each_still_needs()
    {
        var a = Advise(Wheel());
        var fair = a.Tips.Single(t => t.Kind == "fair-rep");
        var need = (Dictionary<string, int>)fair.Facts["need"];
        Assert.Equal(Clicker.FairVillages.Length, need.Count);
        Assert.All(need.Values, n => Assert.Equal(Clicker.FairRepLevels[^1], n));
        Assert.Equal(a.Mult.Single(m => m.Key == "fair").Max / a.Mult.Single(m => m.Key == "fair").Now, fair.Gain, 9);
    }

    [Fact]
    public void Idle_money_is_spent_greedily_on_paper_only()
    {
        var h = Wheel(s => s["pots"] = 1e15);
        var wallet = Advise(h).Tips.Single(t => t.Kind == "wallet");
        Assert.True(wallet.Gain >= Clicker.AdviseWalletMin);
        Assert.True(wallet.Cost <= 1e15 && wallet.Cost > 0);
        lock (h.Room.Sync) Assert.Equal(1e15, JsonNode.Parse(h.Room.Game.Save()!)!["pots"]!.GetValue<double>());
    }

    [Fact]
    public void Without_glory_the_first_overseas_workshop_waits_for_twenty()
    {
        var slava = Advise(Wheel(s => s["pots"] = 1e40)).Tips.Single(t => t.Kind == "slava");
        Assert.Equal("athens", slava.Facts["tier"]);
        Assert.Equal(20L, slava.Facts["need"]);
    }

    // ---------- звірка з 25.10 ----------

    static Clicker Live(string nick, out RoomHarness h)
    {
        var dir = Environment.GetEnvironmentVariable("CLK_ADVISE_SAVES")!;
        var json = File.ReadAllText(Path.Combine(dir, $"save-{nick}.json"));
        h = new RoomHarness("clicker");
        h.Clock.UtcNow = DateTimeOffset.Parse(JsonNode.Parse(json)!["lastSync"]!.GetValue<string>());
        h.Solo("звірка-" + nick);
        lock (h.Room.Sync) h.Room.Game.Load(json);
        return (Clicker)h.Room.Game;
    }

    [ClickerSavesFact]
    public void Matches_the_measurements_of_october_25()
    {
        // 25.10 заміряно з 7 кліками за секунду: чорна проти білої за годину біля кола.
        foreach (var (nick, black, albumNow) in new[] { ("владік", 2.55, 7.01), ("назар", 2.26, 4.72), ("smaug", 3.33, 6.945) })
        {
            var g = Live(nick, out var h);
            ClickerAdvice a;
            lock (h.Room.Sync) a = g.Advise(7);
            var ratio = a.Clays.Single(c => c.Key == "black").Hour.Play / a.Clays.Single(c => c.Key == "white").Hour.Play;
            Assert.Equal(black, ratio, 1);
            Assert.Equal(albumNow, a.Mult.Single(m => m.Key == "album").Now, 2);
        }
        // Назар: порожня піч і 7 клітинок + 20 зірок — разом ≈ ×1,48 до всього.
        var n = Live("назар", out var hn);
        ClickerAdvice an;
        lock (hn.Room.Sync) an = n.Advise(7);
        var album = an.Mult.Single(m => m.Key == "album");
        Assert.InRange(album.Max / album.Now, 1.45, 1.52);
        Assert.Equal(7, ((List<string>)an.Tips.Single(t => t.Kind == "album-grid").Facts["cells"]).Count);
        Assert.Equal(20, ((List<string>)an.Tips.Single(t => t.Kind == "album-grid").Facts["stars"]).Count);
        // Владік: шана гостей 49 з 60 — до стелі ≈ +13 %.
        var v = Live("владік", out var hv);
        ClickerAdvice av;
        lock (hv.Room.Sync) av = v.Advise(7);
        Assert.Equal(1.134, av.Tips.Single(t => t.Kind == "guest-rep").Gain, 2);
        // smaug: до азулежу 258 Слави.
        var s = Live("smaug", out var hs);
        ClickerAdvice asg;
        lock (hs.Room.Sync) asg = s.Advise(7);
        var slava = asg.Tips.Single(t => t.Kind == "slava");
        Assert.Equal(("azulejo", 258L), ((string)slava.Facts["tier"], (long)slava.Facts["need"]));
    }
}
