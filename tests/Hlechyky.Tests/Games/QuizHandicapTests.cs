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
}
