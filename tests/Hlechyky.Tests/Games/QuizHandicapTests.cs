using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «⚖ Фора» у вікторинах (пакет 3, 20.10): Мелодія, Піктіонарі, Байкарі, «Скільки?», Своя гра. Сильний (сила +0,5)
/// за той самий заробіток отримує ×0,6, слабкий (−0,5) — ×1,4; «без фори» — як було; підпис у Журналі.
/// </summary>
public class QuizHandicapTests
{
    sealed class FixedSkills(Dictionary<string, double> map) : ISkills
    {
        public double Strength(string gameId, string? nick) => map.TryGetValue(nick ?? "", out var s) ? s : 0;
    }

    /// <summary>Оля сильна, Петро слабкий: середня 0 → Оля ×0,6, Петро ×1,4 (рівно межі).</summary>
    static readonly Dictionary<string, double> Power = new() { ["Оля"] = 0.5, ["Петро"] = -0.5 };
    const double Strong = 0.6, Weak = 1.4;

    static IServiceProvider Services(Action<ServiceCollection>? more = null)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISkills>(new FixedSkills(Power));
        more?.Invoke(sc);
        return sc.BuildServiceProvider();
    }

    static string? Note(RoomHarness h) =>
        h.Outbox.OfType<Journal>().Select(j => j.Text).FirstOrDefault(t => t.StartsWith("⚖ Фора"));

    static void NoteSaysBoth(RoomHarness h)
    {
        var note = Note(h);
        Assert.NotNull(note);
        Assert.Contains("Оля ×0,6", note);
        Assert.Contains("Петро ×1,4", note);
    }

    static double[] Factors(RoomHarness h, int? seat = null) =>
        [.. h.View(seat).GetProperty("handicap").EnumerateArray().Select(x => x.GetDouble())];

    // ---------------------------------------------------------------- Вгадай мелодію

    static MelodyTrack T(string id, string artist, string title) => new(id, title, artist, 200, null, "/dev/null");

    static readonly MelodyTrack[] Songs =
    [
        T("a", "Океан Ельзи", "Обійми"), T("b", "Скрябін", "Старі фотографії"), T("c", "DakhaBrakha", "Vesna"),
        T("d", "KALUSH", "Stefania"), T("e", "The Hardkiss", "Journey"),
    ];

    static RoomHarness MelodyTable(string handicap)
    {
        var h = new RoomHarness("melody", options: new { rounds = "5", handicap }, seed: 3,
            services: Services(sc => sc.AddSingleton<IMelodySource>(new FakeMelodySource(Songs))));
        Assert.True(h.Join("Оля").Ok);
        Assert.True(h.Join("Петро").Ok);
        h.Start();
        for (var i = 0; i < 400 && h.View(null).GetProperty("phase").GetString() != "play"; i++) { h.Tick(); Thread.Sleep(2); }
        return h;
    }

    static int MelodyScore(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt32();

    [Fact]
    public void Melody_multiplies_everything_earned_on_a_track()
    {
        var h = MelodyTable(Handicap.Auto);
        NoteSaysBoth(h);
        Assert.Equal([Strong, Weak], Factors(h, 0).Take(2));
        // Оля перша й швидка: 50 + 20 + ⚡20 = 90 → ×0,6 = 54
        h.Clock.AdvanceMs(Melody.GuessEveryMs);
        var r = h.Act(0, "guess", new { text = "Океан Ельзи" });
        Assert.True(r.Ok);
        Assert.Contains("⚖ фора: +54", r.Message);
        Assert.Equal(54, MelodyScore(h, 0));
        // Петро — той самий виконавець після п'яти секунд: 50 → ×1,4 = 70
        h.Clock.AdvanceMs(Melody.SpeedMs + Melody.GuessEveryMs);
        Assert.True(h.Act(1, "guess", new { text = "Океан Ельзи" }).Ok);
        Assert.Equal(70, MelodyScore(h, 1));
    }

    [Fact]
    public void Melody_without_handicap_counts_as_before()
    {
        var h = MelodyTable(Handicap.Off);
        Assert.Null(Note(h));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, h.View(0).GetProperty("handicap").ValueKind);
        h.Clock.AdvanceMs(Melody.GuessEveryMs);
        Assert.True(h.Act(0, "guess", new { text = "Океан Ельзи" }).Ok);
        Assert.Equal(Melody.ArtistPoints + Melody.ArtistFirst + Melody.SpeedBonus, MelodyScore(h, 0));
    }

    // ---------------------------------------------------------------- Піктіонарі

    /// <summary>Оля (×0,6) малює, Петро (×1,4) і Ганна (×1) вгадують те саме слово в ті самі миті.</summary>
    static RoomHarness PictionaryTurn(string handicap)
    {
        var h = new RoomHarness("pictionary", options: new { handicap }, seed: 7,
            services: Services(sc => sc.AddSingleton(new PictionaryWords([("animals", "кіт")]))));
        foreach (var n in new[] { "Оля", "Петро", "Ганна" }) Assert.True(h.Join(n).Ok);
        h.Start();
        Assert.Equal(0, h.View(null).GetProperty("drawer").GetInt32());
        Assert.True(h.Act(0, "pick", new { i = 0 }).Ok);
        h.Clock.AdvanceMs(Pictionary.GuessEveryMs);
        Assert.True(h.Act(1, "guess", new { text = "кіт" }).Ok);
        h.Clock.AdvanceMs(10_000);
        Assert.True(h.Act(2, "guess", new { text = "кіт" }).Ok);   // усі вгадали — хід скінчився, художник бере частку
        return h;
    }

    static int[] PictionaryScores(RoomHarness h) =>
        [.. h.View(null).GetProperty("scores").EnumerateArray().Take(3).Select(x => x.GetInt32())];

    [Fact]
    public void Pictionary_multiplies_the_guesser_and_the_drawer_share()
    {
        var off = PictionaryScores(PictionaryTurn(Handicap.Off));
        var h = PictionaryTurn(Handicap.Auto);
        var on = PictionaryScores(h);
        NoteSaysBoth(h);
        Assert.Equal((int)Math.Floor(off[1] * Weak + 1e-9), on[1]);      // слабкий вгадувач — більше
        Assert.Equal(off[2], on[2]);                                     // середній — як було
        Assert.Equal((int)Math.Floor(off[0] * Strong + 1e-9), on[0]);    // сильний художник — менша частка, від чесних очок
        Assert.True(on[0] > 0 && on[0] < off[0]);
        Assert.Equal(Strong, Factors(h)[0]);
    }

    [Fact]
    public void Pictionary_duo_has_no_handicap()
    {
        var h = new RoomHarness("pictionary", options: new { mode = "duo" }, seed: 7,
            services: Services(sc => sc.AddSingleton(new PictionaryWords([("animals", "кіт"), ("animals", "пес")]))));
        foreach (var n in new[] { "Оля", "Петро" }) Assert.True(h.Join(n).Ok);
        h.Start();
        Assert.Null(Note(h));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, h.View(null).GetProperty("handicap").ValueKind);
    }
}
