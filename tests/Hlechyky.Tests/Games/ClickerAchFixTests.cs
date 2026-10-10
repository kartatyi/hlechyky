using System.Text.Json;
using System.Text.Json.Nodes;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Ачівки кола, які активний гравець не діставав (10.10, правило «будь-яку — за тиждень»): бездоганне горно й
/// звання «Бездоганне горно», «Золоті руки» (поріг майстерності), шана сіл і гостей (верхні пороги), «Кунсткамера»
/// (кидки Толоки й гостей тепер ведуть до дивовиж), «Хлібосол» (лічильник гостинців переживає обпал), і видача
/// ачівок на завантаженні тим, хто поріг уже переступив.
/// </summary>
public class ClickerAchFixTests
{
    static RoomHarness Wheel(int seed = 1)
    {
        var h = new RoomHarness("clicker", seed: seed);
        h.Solo("Оля");
        return h;
    }

    static JsonElement View(RoomHarness h) => h.View(0);
    static JsonElement Kiln(RoomHarness h) => View(h).GetProperty("kiln");
    static ActResult K(RoomHarness h, object payload) => h.Act(0, "kiln", payload);
    static ActResult Look(RoomHarness h) => h.Act(0, "look");
    static bool Got(RoomHarness h, string ach) => h.Awards.Any(a => a.Reason == "ach:" + ach);

    static void Patch(RoomHarness h, Action<JsonObject> edit)
    {
        lock (h.Room.Sync)
        {
            var node = JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
            edit(node);
            h.Room.Game.Load(node.ToJsonString());
        }
    }

    static JsonObject SaveNode(RoomHarness h)
    {
        lock (h.Room.Sync) return JsonNode.Parse(h.Room.Game.Save()!)!.AsObject();
    }

    static JsonObject Part(JsonObject s, string key) => (s[key] as JsonObject) ?? (JsonObject)(s[key] = new JsonObject());

    static void Rack(RoomHarness h, int dry) => Patch(h, s =>
    {
        var rack = new JsonArray();
        for (var i = 0; i < dry; i++)
            rack.Add(new JsonObject { ["ware"] = "pot", ["clay"] = "", ["dryAt"] = h.Clock.UtcNow.AddMinutes(-1).ToString("O") });
        s["craft"]!["rack"] = rack;
    });

    /// <summary>Умілий палій (як у ClickerKilnTests): прикриває перед перегрівом, відкриває при недогріві, підкидає, коли осідає.</summary>
    static List<(int Ms, int Act)> Stoker(int seed)
    {
        var acts = new List<(int Ms, int Act)>();
        var open = true;
        var last = -10_000;
        for (var i = 1; i < KilnHeat.Steps - 3 && acts.Count < KilnHeat.MaxActs; i++)
        {
            if (i * 100 - last < 300) continue;
            double fuel = 0, future = 0;
            KilnHeat.Run(seed, acts, (step, T, F, _) =>
            {
                if (step == i - 1) fuel = F;
                if (step == Math.Min(KilnHeat.Steps - 1, i + 20)) future = T;
            });
            var n = i + 21;
            var hi = n < KilnHeat.WarmSteps ? KilnHeat.Hi0 + (KilnHeat.Hi - KilnHeat.Hi0) * n / KilnHeat.WarmSteps : KilnHeat.Hi;
            var lo = n < KilnHeat.WarmSteps ? hi - 150 : KilnHeat.Lo;
            int? act = null;
            if (future > hi - 25 && open) act = KilnHeat.Close;
            else if (future < lo + 40 && !open) act = KilnHeat.Open;
            else if (future < lo + 50 && fuel < KilnHeat.FuelMax - 0.4) act = KilnHeat.Stoke;
            if (act is not { } a) continue;
            acts.Add((i * 100 + 50, a));
            if (a == KilnHeat.Close) open = false;
            if (a == KilnHeat.Open) open = true;
            last = i * 100;
        }
        return acts;
    }

    /// <summary>Перепал: підкидати без упину — тріщини майже напевно.</summary>
    static List<(int, int)> Spam() => Enumerable.Range(0, 75).Select(i => (i * 400, KilnHeat.Stoke)).ToList();

    /// <summary>Ручний обпал шести сухих з розписом на 100; повертає якості виробів.</summary>
    static List<int> Burn(RoomHarness h, bool overheat)
    {
        Patch(h, s => s["kiln"] = new JsonObject { ["beauty"] = 100 });
        Rack(h, Clicker.PerfectMin);
        var lit = K(h, new { op = "light" });
        Assert.True(lit.Ok, lit.Message);
        var seed = Kiln(h).GetProperty("seed").GetInt32();
        h.Clock.Advance(KilnHeat.Steps / 10.0 + 0.2);
        var acts = overheat ? Spam() : Stoker(seed);
        var r = K(h, new { op = "open", t = acts.Select(a => new[] { a.Item1, a.Item2 }).ToArray() });
        Assert.True(r.Ok, r.Message);
        var q = Kiln(h).GetProperty("last").GetProperty("items").EnumerateArray().Select(i => i[1].GetInt32()).ToList();
        h.Clock.Advance(Clicker.KilnCool + TimeSpan.FromSeconds(1));
        return q;
    }

    static double Flawless(RoomHarness h) => View(h).GetProperty("titles").GetProperty("progress").GetProperty("flawless")[0].GetDouble();

    // ---------- 1. бездоганний обпал ----------

    [Theory]
    [InlineData(new[] { 3, 3, 3, 3, 3, 3 }, true)]
    [InlineData(new[] { 3, 3, 3, 1, 1, 1 }, true)]                  // половина дзвінкі
    [InlineData(new[] { 4, 3, 1, 1, 1, 1 }, false)]                 // третина — замало
    [InlineData(new[] { 3, 3, 3, 3, 3 }, false)]                    // п'ять — замало виробів
    [InlineData(new[] { 4, 4, 4, 4, 4, 0 }, false)]                 // одна тріщина — і вже не бездоганний
    [InlineData(new[] { 3, 3, 3, 3, 2, 2, 2, 2, 2, 2 }, true)]      // рівно 40 %
    [InlineData(new[] { 3, 3, 3, 2, 2, 2, 2, 2, 2, 2 }, false)]     // 30 %
    [InlineData(new[] { 4, 4, 4, 4, 4, 4, 4, 4, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, false)] // 8 з 22 — 36 %
    [InlineData(new[] { 4, 4, 4, 4, 4, 4, 4, 4, 4, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, true)]  // 9 з 22 — 41 %
    public void A_flawless_batch_is_six_or_more_without_cracks_and_two_fifths_ringing(int[] q, bool flawless)
    {
        Assert.Equal(6, Clicker.PerfectMin);
        Assert.Equal(flawless, Clicker.IsPerfectBatch(q));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(11)]
    [InlineData(18)]
    [InlineData(26)]
    public void A_masterly_firing_is_flawless_often_enough_for_ten_in_a_week(int wares)
    {
        // Майстерний жар і розпис на 100 (S = 1): дзвінкий чи розкішний — 30 % на виріб (KilnHeat.QualityOf). Стара умова
        // «усі дзвінкі» на 18 виробах — 0,3¹⁸ ≈ 4e-10; нова — кожен п'ятий-сьомий обпал, тож десять — за пару днів гри.
        var rng = new Random(wares);
        const int Runs = 20_000;
        var flawless = 0;
        for (var r = 0; r < Runs; r++)
        {
            var q = new int[wares];
            for (var i = 0; i < wares; i++) q[i] = KilnHeat.QualityOf(1, 1, rng.NextDouble());
            if (Clicker.IsPerfectBatch(q)) flawless++;
        }
        Assert.InRange(flawless / (double)Runs, 0.10, 0.35);
    }

    // ---------- 2. звання «Бездоганне горно» ----------

    [Fact]
    public void Flawless_title_counts_all_flawless_firings_and_a_cracked_one_does_not_reset_it()
    {
        Assert.Equal(10, Clicker.FlawlessKilns);
        var h = Wheel();
        // Стара «серія поспіль» зі збереження — початок лічби.
        Patch(h, s => Part(s, "titles")["perfectRun"] = Clicker.FlawlessKilns - 1);
        Assert.Equal(Clicker.FlawlessKilns - 1, Flawless(h));

        var q = Burn(h, overheat: true);
        Assert.False(Clicker.IsPerfectBatch(q));
        Assert.Equal(Clicker.FlawlessKilns - 1, Flawless(h));        // раніше тут був нуль
        Assert.True(Look(h).Ok);
        Assert.DoesNotContain("flawless", View(h).GetProperty("titles").GetProperty("earned").EnumerateObject().Select(p => p.Name));

        // Десятий бездоганний — звання.
        for (var i = 0; i < 30 && Flawless(h) < Clicker.FlawlessKilns; i++) Burn(h, overheat: false);
        Assert.Equal(Clicker.FlawlessKilns, Flawless(h));
        Assert.True(Look(h).Ok);
        Assert.Contains("flawless", View(h).GetProperty("titles").GetProperty("earned").EnumerateObject().Select(p => p.Name));
        Assert.Equal(Clicker.FlawlessKilns, SaveNode(h)["titles"]!["perfectRun"]!.GetValue<int>());
    }

    // ---------- 3. «Золоті руки» ----------

    [Fact]
    public void Mastery_tops_out_at_three_and_a_half_thousand()
    {
        Assert.Equal([5L, 15, 40, 100, 200, 400, 700, 1_200, 2_000, 3_500], Clicker.MasteryAt);
        Assert.Equal(9, Clicker.MasteryLevel(3_499));
        Assert.Equal(10, Clicker.MasteryLevel(3_500));
        var h = Wheel();
        Assert.Equal(Clicker.MasteryAt, View(h).GetProperty("catalog").GetProperty("album").GetProperty("masteryAt").EnumerateArray().Select(x => x.GetInt64()));
    }

    [Theory]
    [InlineData(3_499, false)]
    [InlineData(3_500, true)]
    [InlineData(12_000, true)]
    public void A_potter_already_past_the_top_gets_golden_hands_on_load(long fired, bool owed)
    {
        var h = Wheel();
        Patch(h, s => s["craft"]!["firedBy"] = new JsonObject { ["whistle"] = fired, ["pot"] = 10 });
        Assert.False(Got(h, "potter-mastery"));
        Assert.True(Look(h).Ok);                                     // черга ачівок віддається дією
        Assert.Equal(owed, Got(h, "potter-mastery"));
    }

    // ---------- 4. шана сіл і гостей ----------

    [Fact]
    public void Respect_tops_are_lower_and_the_bottom_is_as_it_was()
    {
        Assert.Equal([0, 8, 25, 60, 120, 220, 380], Clicker.FairRepLevels[..7]);
        Assert.Equal([0, 5, 15, 35, 70, 120, 190], Clicker.GuestRepLevels[..7]);
        Assert.Equal(950, Clicker.FairRepLevels[^1]);
        Assert.Equal(480, Clicker.GuestRepLevels[^1]);
        // Монотонно й гладко: кожен крок угору не менший за попередній на верхній ділянці.
        foreach (var levels in new[] { Clicker.FairRepLevels, Clicker.GuestRepLevels })
        {
            for (var i = 1; i < levels.Length; i++) Assert.True(levels[i] > levels[i - 1]);
            for (var i = 8; i < levels.Length; i++) Assert.True(levels[i] - levels[i - 1] >= levels[i - 1] - levels[i - 2]);
        }
        Assert.Equal(9, Clicker.FairLevelOf(949));
        Assert.Equal(10, Clicker.FairLevelOf(950));
        Assert.Equal(9, Clicker.GuestLevelOf(479));
        Assert.Equal(10, Clicker.GuestLevelOf(480));
    }

    [Fact]
    public void A_village_already_at_the_new_top_gives_the_achievement_on_load()
    {
        var h = Wheel();
        Patch(h, s => Part(s, "fair")["rep"] = new JsonObject { ["opishnia"] = 949, ["kosiv"] = 300 });
        Assert.True(Look(h).Ok);
        Assert.False(Got(h, "potter-rep10"));
        Patch(h, s => Part(s, "fair")["rep"] = new JsonObject { ["opishnia"] = 960, ["kosiv"] = 300 });
        Assert.True(Look(h).Ok);
        Assert.True(Got(h, "potter-rep10"));
    }

    [Fact]
    public void A_guest_already_at_the_new_top_gives_the_achievement_on_load()
    {
        var h = Wheel();
        Patch(h, s => Part(s, "guests")["rep"] = new JsonObject { ["canton"] = 479 });
        Assert.True(Look(h).Ok);
        Assert.False(Got(h, Clicker.GuestAchMax));
        Patch(h, s => Part(s, "guests")["rep"] = new JsonObject { ["canton"] = 500 });
        Assert.True(Look(h).Ok);
        Assert.True(Got(h, Clicker.GuestAchMax));
    }

    // ---------- 5. дивовижі з Толоки й гостей ----------

    [Theory]
    [InlineData("toloka")]
    [InlineData("guests")]
    public void Toloka_and_guests_rolls_lead_to_salt_amber_and_ribbon(string trigger)
    {
        var from = Clicker.Wonders.Where(w => w.Triggers.Contains(trigger)).Select(w => w.Key).Order().ToList();
        Assert.Equal(["amber", "ribbon", "salt"], from);
        var h = Wheel();
        var found = new HashSet<string>();
        for (var i = 0; i < 2000 && found.Count < 3; i++)
        {
            ClickerWonder? w;
            lock (h.Room.Sync) w = ((Clicker)h.Room.Game).RollWonder(trigger);
            if (w is not null) found.Add(w.Key);
        }
        Assert.Equal(["amber", "ribbon", "salt"], found.Order().ToList());
    }

    // ---------- 6. «Хлібосол» ----------

    [Fact]
    public void Treats_survive_the_workshop_firing_and_ten_already_sent_give_the_achievement_on_load()
    {
        var h = Wheel();
        Patch(h, s => Part(s, "guild")["treatsSent"] = 7);
        Patch(h, s => s["total"] = 2_000_000_000);
        Assert.True(h.Act(0, "fire").Ok);
        Assert.Equal(7, SaveNode(h)["guild"]!["treatsSent"]!.GetValue<int>());
        Assert.False(Got(h, "potter-treat"));

        Patch(h, s => Part(s, "guild")["treatsSent"] = 12);          // переступив поріг, а ачівки нема
        Assert.True(Look(h).Ok);
        Assert.True(Got(h, "potter-treat"));
    }
}
