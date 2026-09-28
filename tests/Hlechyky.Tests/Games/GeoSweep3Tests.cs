using System.Security.Cryptography;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Де це?», прохід №3 (29.09): пам'ять бачених місць (п. 51), запечатане наступне фото (п. 50), «Де це? дня»
/// (п. 49), дуель на час (п. 54), підказка «область» (п. 53).
/// </summary>
public class GeoSweep3Tests
{
    static IServiceProvider With(GeoCache c, GeoSeen? seen = null)
    {
        var s = new ServiceCollection().AddSingleton(c.Photos);
        if (seen is not null) s.AddSingleton(seen);
        return s.BuildServiceProvider();
    }

    static RoomHarness Table(GeoCache c, int players, object? options = null, GeoSeen? seen = null, int seed = 42)
    {
        var h = new RoomHarness("geo", options, seed, With(c, seen));
        string[] nicks = ["Оля", "Петро", "Ганна"];
        for (var i = 0; i < players; i++) h.Join(nicks[i]);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static (int X, int Y) Truth(RoomHarness h, GeoCache c) => GeoTests.Truth(h, c);

    // ---------------------------------------------------------------- 51: пам'ять

    [Fact]
    public void Places_seen_by_anyone_at_the_table_go_last_even_at_a_brand_new_table()
    {
        using var c = new GeoCache();
        var seen = new GeoSeen(null);
        var all = c.Bank.Places.Select(p => p.Id).ToList();
        // Оля вже бачила все, крім п'яти місць, — нова партія мусить узяти саме ці п'ять
        var fresh = all.Take(5).ToHashSet();
        seen.Mark([GeoSeen.NickKey("Оля")], "x", DateTimeOffset.UnixEpoch);
        foreach (var id in all.Skip(5)) seen.Mark([GeoSeen.NickKey("оля ")], id, DateTimeOffset.UtcNow);
        var h = Table(c, 2, new { rounds = "5" }, seen);
        var got = new HashSet<string>();
        for (var r = 0; r < 5; r++)
        {
            GeoTests.Until(h, GeoMatch.PhaseGuess);
            got.Add(GeoTests.Current(h, c).Id);
            h.Act(0, "guess", new { x = 100, y = 100 });
            h.Act(1, "guess", new { x = 100, y = 100 });
            h.Act(0, "ready"); h.Act(1, "ready");
            GeoTests.Until(h, GeoMatch.PhaseReveal);
            h.Act(0, "next"); h.Act(1, "next");
            h.Tick(1);
        }
        Assert.Equal(fresh, got);
        // і тепер Петро ці п'ять теж «бачив»
        var petro = seen.LastSeen([GeoSeen.NickKey("Петро")]);
        Assert.True(fresh.All(petro.ContainsKey));
    }

    // ---------------------------------------------------------------- 50: запечатане фото

    [Fact]
    public void Next_photo_is_sealed_during_reveal_and_its_key_comes_only_in_get_ready()
    {
        using var c = new GeoCache();
        var h = Table(c, 1, new { rounds = "5" });
        GeoTests.Until(h, GeoMatch.PhaseGuess);
        var v = h.View(0);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("pre").ValueKind);
        h.Act(0, "guess", new { x = 500, y = 500 });
        h.Act(0, "ready");
        GeoTests.Until(h, GeoMatch.PhaseReveal);
        var pre = h.View(0).GetProperty("pre").GetString()!;
        Assert.Matches("^/api/games/geo/[0-9a-f]{24}\\.bin$", pre);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("seal").ValueKind);
        var token = pre["/api/games/geo/".Length..^4];
        // запечатаний токен .jpg не віддає
        Assert.Null(c.Photos.Resolve(token));
        var sealedHit = c.Photos.ResolveSealed(token);
        Assert.NotNull(sealedHit);

        h.Act(0, "next");
        GeoTests.Until(h, GeoMatch.PhaseBetween);
        var seal = h.View(0).GetProperty("seal");
        Assert.Equal(pre, seal.GetProperty("url").GetString());
        var key = Convert.FromBase64String(seal.GetProperty("key").GetString()!);
        Assert.Equal(sealedHit!.Value.Key, key);

        // шифр розшифровується ключем у той самий файл, що й звичайний токен раунду
        var plain = File.ReadAllBytes(sealedHit.Value.Path);
        var box = GeoPhotos.Seal(plain, key);
        var back = new byte[plain.Length];
        using (var gcm = new AesGcm(key, 16))
            gcm.Decrypt(box.AsSpan(0, 12), box.AsSpan(12, plain.Length), box.AsSpan(12 + plain.Length), back);
        Assert.Equal(plain, back);
        var photo = h.View(0).GetProperty("photo").GetString()!;
        Assert.Equal(sealedHit.Value.Path, c.Photos.Resolve(photo["/api/games/geo/".Length..^4]));
        // у guess ключа вже нема
        GeoTests.Until(h, GeoMatch.PhaseGuess);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("seal").ValueKind);
    }

    // ---------------------------------------------------------------- 49: «Де це? дня»

    static List<string> DailyPlaces(GeoCache c, RoomHarness h)
    {
        var ids = new List<string>();
        for (var r = 0; r < 5; r++)
        {
            GeoTests.Until(h, GeoMatch.PhaseGuess);
            ids.Add(GeoTests.Current(h, c).Id);
            var (x, y) = Truth(h, c);
            h.Act(0, "guess", new { x = x + 30 * r, y });
            h.Act(0, "ready");
            GeoTests.Until(h, GeoMatch.PhaseReveal);
            h.Act(0, "next");
            h.Tick(1);
        }
        return ids;
    }

    [Fact]
    public void Daily_gives_everyone_the_same_five_places_one_attempt_and_a_share_line()
    {
        using var c = new GeoCache();
        var a = new RoomHarness("geo-daily", null, 1, With(c));
        Assert.True(a.Solo("Оля").Ok, a.Reply.Message);
        var b = new RoomHarness("geo-daily", null, 777, With(c));
        Assert.True(b.Solo("Петро").Ok, b.Reply.Message);
        var pa = DailyPlaces(c, a);
        var pb = DailyPlaces(c, b);
        Assert.Equal(pa, pb);
        Assert.Equal(5, pa.Distinct().Count());
        Assert.Equal(GeoMatch.PhaseDone, GeoTests.Phase(a));
        var share = a.View(0).GetProperty("share").GetString()!;
        Assert.StartsWith("📍 Де це? дня №", share);
        Assert.Contains("🟩", share);
        Assert.Single(a.Scores);
        Assert.Contains(a.Awards, w => w.Reason == "daily:geo-daily");
        // той самий день — друга спроба не дається
        Assert.False(a.Rematch().Ok);
        Assert.Equal(GeoDaily.Played, a.Reply.Message);
    }

    [Fact]
    public void Daily_reopened_mid_day_continues_from_the_next_photo_not_from_the_first()
    {
        using var c = new GeoCache();
        var h = new RoomHarness("geo-daily", null, 1, With(c));
        Assert.True(h.Solo("Оля").Ok);
        var first = new List<string>();
        for (var r = 0; r < 2; r++)
        {
            GeoTests.Until(h, GeoMatch.PhaseGuess);
            first.Add(GeoTests.Current(h, c).Id);
            var (x, y) = Truth(h, c);
            h.Act(0, "guess", new { x, y });
            h.Act(0, "ready");
            GeoTests.Until(h, GeoMatch.PhaseReveal);
            h.Act(0, "next");       // збереження — на успішній дії
        }
        var game = (GeoDaily)h.Room.Game;
        var saved = game.Save()!;
        Assert.Contains("\"Total\":10000", saved);

        var again = new RoomHarness("geo-daily", null, 1, With(c));
        again.Solo("Оля");
        var g2 = (GeoDaily)again.Room.Game;
        g2.Load(saved);
        Assert.Equal(3, again.View(0).GetProperty("round").GetInt32());
        Assert.Equal(10000, again.View(0).GetProperty("scores")[0].GetInt64());
    }

    // ---------------------------------------------------------------- 54: дуель на час

    [Fact]
    public void Duel_gives_fifteen_seconds_and_a_thousand_to_the_first_close_pin()
    {
        using var c = new GeoCache();
        var h = Table(c, 2, new { mode = "duel" });
        GeoTests.Until(h, GeoMatch.PhaseGuess);
        Assert.Equal(15, h.View(0).GetProperty("seconds").GetInt32());
        Assert.Equal(15_000, h.View(0).GetProperty("phaseMs").GetInt32());
        var (x, y) = Truth(h, c);
        h.Act(1, "guess", new { x = x + 60, y });      // ~20 км, але пізніше
        h.Clock.Advance(TimeSpan.FromMilliseconds(100));
        h.Act(0, "guess", new { x = x + 30, y });      // ~10 км
        h.Clock.Advance(TimeSpan.FromMilliseconds(100));
        h.Act(1, "guess", new { x, y });               // Петро переставив — тепер він визначився пізніше
        h.Act(0, "ready"); h.Act(1, "ready");
        GeoTests.Until(h, GeoMatch.PhaseReveal);
        var rows = h.View(null).GetProperty("reveal").GetProperty("rows").EnumerateArray().ToList();
        var ola = rows.First(r => r.GetProperty("seat").GetInt32() == 0);
        var petro = rows.First(r => r.GetProperty("seat").GetInt32() == 1);
        Assert.True(ola.GetProperty("fast").GetBoolean());
        Assert.False(petro.GetProperty("fast").GetBoolean());
        Assert.InRange(ola.GetProperty("points").GetInt32() - 1000 - GeoScore.Points(ola.GetProperty("km").GetDouble()), -10, 10);
    }

    [Fact]
    public void Duel_bonus_needs_a_pin_within_fifty_km()
    {
        using var c = new GeoCache();
        var h = Table(c, 2, new { mode = "duel" });
        GeoTests.Until(h, GeoMatch.PhaseGuess);
        var (x, y) = Truth(h, c);
        h.Act(0, "guess", new { x = Math.Clamp(x + 600, 0, GeoMap.W), y });   // ~200 км, перший
        h.Clock.Advance(TimeSpan.FromMilliseconds(100));
        h.Act(1, "guess", new { x = x + 3, y });
        h.Act(0, "ready"); h.Act(1, "ready");
        GeoTests.Until(h, GeoMatch.PhaseReveal);
        var rows = h.View(null).GetProperty("reveal").GetProperty("rows").EnumerateArray().ToList();
        Assert.True(rows.Single(r => r.GetProperty("seat").GetInt32() == 1).GetProperty("fast").GetBoolean());
        Assert.False(rows.Single(r => r.GetProperty("seat").GetInt32() == 0).GetProperty("fast").GetBoolean());
    }

    // ---------------------------------------------------------------- 53: підказка «область»

    [Fact]
    public void Area_hint_shows_the_region_only_to_the_asker_and_costs_forty_percent()
    {
        using var c = new GeoCache();
        var h = Table(c, 2);
        GeoTests.Until(h, GeoMatch.PhaseGuess);
        var place = GeoTests.Current(h, c);
        Assert.True(h.Act(0, "area").Ok);
        Assert.False(h.Act(0, "area").Ok);
        Assert.Equal(place.Region, h.View(0).GetProperty("area").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("area").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("area").ValueKind);
        var (x, y) = Truth(h, c);
        h.Act(0, "guess", new { x, y });
        h.Act(1, "guess", new { x, y });
        h.Act(0, "ready"); h.Act(1, "ready");
        GeoTests.Until(h, GeoMatch.PhaseReveal);
        var rows = h.View(null).GetProperty("reveal").GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(3000, rows.Single(r => r.GetProperty("seat").GetInt32() == 0).GetProperty("points").GetInt32());
        Assert.True(rows.Single(r => r.GetProperty("seat").GetInt32() == 0).GetProperty("area").GetBoolean());
        Assert.Equal(5000, rows.Single(r => r.GetProperty("seat").GetInt32() == 1).GetProperty("points").GetInt32());
        // наступний раунд — підказка знову своя, з нуля
        h.Act(0, "next"); h.Act(1, "next");
        GeoTests.Until(h, GeoMatch.PhaseGuess);
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("area").ValueKind);
        Assert.True(h.Act(0, "area").Ok);
    }

    [Fact]
    public void Area_hint_can_be_switched_off_by_the_table()
    {
        using var c = new GeoCache();
        var h = Table(c, 1, new { area = "off" });
        GeoTests.Until(h, GeoMatch.PhaseGuess);
        Assert.False(h.Act(0, "area").Ok);
        Assert.False(h.View(0).GetProperty("areaOn").GetBoolean());
    }
}
