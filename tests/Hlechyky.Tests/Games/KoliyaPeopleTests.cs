using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Колія» K28 — люди за столом: ніки над гравцями, реакції-емодзі, пінг і «відпав», глядачі. Нічого з цього не йде
/// в журнал правил: світ (і хеш) від них не міняється.
/// </summary>
public class KoliyaPeopleTests
{
    static RoomHarness Table(params string[] nicks)
    {
        var h = new RoomHarness("koliya", seed: 7);
        foreach (var n in nicks) h.Join(n);
        return h;
    }

    static Koliya Game(RoomHarness h) => (Koliya)h.Room.Game;

    static JsonElement Log(RoomHarness h)
    {
        lock (h.Room.Sync) return Views.Json(Game(h).Log(0));
    }

    static JsonElement LastFrame(RoomHarness h) => Views.Json(h.Outbox.OfType<RoomFrame>().Last().Frame);

    static string?[] Names(JsonElement j) =>
        [.. j.GetProperty("nm").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? null : x.GetString())];

    [Fact]
    public void Ніки_гравців_у_виді_й_журналі_другий_за_ПК_з_двійкою()
    {
        var h = Table("Оля", "Петро");
        Assert.Equal(["Оля", "Петро"], Names(h.View(0)));
        Assert.True(h.Act(0, "join2").Ok);
        Assert.Equal(["Оля", "Петро", "Оля ²"], Names(h.View(null)));
        Assert.Equal(["Оля", "Петро", "Оля ²"], Names(Log(h)));
        h.Leave("Петро");
        Assert.Equal(["Оля", null, "Оля ²"], Names(h.View(0)));
    }
}
