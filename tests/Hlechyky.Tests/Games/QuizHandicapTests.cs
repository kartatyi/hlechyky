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

    // ---------------------------------------------------------------- Байкарі

    static readonly IReadOnlyList<BluffQuestion> BluffQs = BluffBank.Parse("""
        { "version": 1, "questions": [
          { "id": "h1", "cat": "nature", "q": "Кров восьминога ___ кольору", "answer": "синього", "accept": ["синя"], "decoys": ["зеленого", "жовтого", "прозорого"] },
          { "id": "h2", "cat": "science", "q": "Найпоширеніший хімічний елемент у Всесвіті — ___", "answer": "водень", "accept": [], "decoys": ["залізо", "кисень", "кремній"] },
          { "id": "h3", "cat": "food", "q": "До XVII століття морква була переважно ___ кольору", "answer": "фіолетового", "accept": [], "decoys": ["білого", "чорного", "синього"] },
          { "id": "h4", "cat": "sport", "q": "Перший чемпіонат світу з футболу 1930 року відбувся в ___", "answer": "Уругваї", "accept": [], "decoys": ["Бразилії", "Італії", "Аргентині"] },
          { "id": "h5", "cat": "history", "q": "Анна, донька Ярослава Мудрого, стала королевою ___", "answer": "Франції", "accept": [], "decoys": ["Норвегії", "Угорщини", "Швеції"] }
        ] }
        """);

    static readonly string[] BluffLies = ["Бамбарбія", "кергуду", "тиндирики"];

    static string BluffPhase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static void BluffUntil(RoomHarness h, string phase)
    {
        for (var i = 0; i < 4000 && h.Room.Status == RoomStatus.Playing && BluffPhase(h) != phase; i++) h.Tick();
        Assert.Equal(phase, BluffPhase(h));
    }

    static int BluffCard(RoomHarness h, string text)
    {
        var cards = h.View(null).GetProperty("options");
        for (var i = 0; i < cards.GetArrayLength(); i++)
            if (BluffText.Norm(cards[i].GetProperty("text").GetString()) == BluffText.Norm(text)) return i;
        throw new InvalidOperationException("нема картки " + text);
    }

    /// <summary>Оля (×0,6) і Петро (×1,4) знаходять правду; Ганна (×1) вибирає Петрову брехню.</summary>
    static RoomHarness BluffQuestion1(string handicap)
    {
        var h = new RoomHarness("bluff", options: new { handicap, questions = "5", voice = "none" }, seed: 42,
            services: Services(sc => sc.AddSingleton(new BluffBankSource(BluffQs))));
        foreach (var n in new[] { "Оля", "Петро", "Ганна" }) Assert.True(h.Join(n).Ok);
        Assert.True(h.Start().Ok);
        BluffUntil(h, Bluff.PhaseWrite);
        var text = h.View(null).GetProperty("text").GetString();
        var answer = BluffQs.First(q => q.Q == text).Answer;
        for (var s = 0; s < 3; s++) Assert.True(h.Act(s, "lie", new { text = BluffLies[s] }).Ok);
        BluffUntil(h, Bluff.PhasePick);
        Assert.True(h.Act(0, "pick", new { i = BluffCard(h, answer) }).Ok);
        Assert.True(h.Act(1, "pick", new { i = BluffCard(h, answer) }).Ok);
        Assert.True(h.Act(2, "pick", new { i = BluffCard(h, BluffLies[1]) }).Ok);
        BluffUntil(h, Bluff.PhaseScore);
        return h;
    }

    static long[] BluffScores(RoomHarness h) =>
        [.. h.View(null).GetProperty("scores").EnumerateArray().Take(3).Select(x => x.GetInt64())];

    [Fact]
    public void Bluff_multiplies_found_truth_and_fooled_players()
    {
        var off = BluffScores(BluffQuestion1(Handicap.Off));
        Assert.Equal([Bluff.TruthPts, Bluff.TruthPts + Bluff.FooledPts, 0L], off);
        var h = BluffQuestion1(Handicap.Auto);
        NoteSaysBoth(h);
        Assert.Equal([(long)(Bluff.TruthPts * Strong), (long)((Bluff.TruthPts + Bluff.FooledPts) * Weak), 0L], BluffScores(h));
        var truth = h.View(null).GetProperty("truthDelta");
        Assert.Equal((long)(Bluff.TruthPts * Strong), truth[0].GetInt64());
        Assert.Equal(Strong, Factors(h)[0]);
    }

    // ---------------------------------------------------------------- Скільки?

    static RoomHarness SkilkyTable(object options, params string[] nicks)
    {
        var h = new RoomHarness("skilky", options: options, seed: 42, services: Services());
        foreach (var n in nicks) Assert.True(h.Join(n).Ok);
        h.Start();
        SkilkyUntil(h, Skilky.PhaseAsk);
        return h;
    }

    static string SkilkyPhase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static void SkilkyUntil(RoomHarness h, string phase)
    {
        for (var i = 0; i < 2000 && h.Room.Status == RoomStatus.Playing && SkilkyPhase(h) != phase; i++) h.Tick(1);
        Assert.Equal(phase, SkilkyPhase(h));
    }

    static double SkilkyTruth(RoomHarness h)
    {
        var text = h.View(null).GetProperty("question").GetString();
        return SkilkyBank.All.First(q => q.Q == text).A!.Value;
    }

    static long[] SkilkyScores(RoomHarness h, int n) =>
        [.. h.View(null).GetProperty("scores").EnumerateArray().Take(n).Select(x => x.GetInt64())];

    /// <summary>Обидва називають точне число в ту саму мить: кожному «в яблучко» + найближчому = 6 очок до фори.</summary>
    static RoomHarness SkilkyBothExact(string handicap)
    {
        var h = SkilkyTable(new { handicap }, "Оля", "Петро");
        var t = SkilkyTruth(h);
        Assert.True(h.Act(0, "answer", new { value = t }).Ok);
        Assert.True(h.Act(1, "answer", new { value = t }).Ok);
        SkilkyUntil(h, Skilky.PhaseReveal);
        return h;
    }

    [Fact]
    public void Skilky_multiplies_the_round_points()
    {
        const int perfect = Skilky.Bullseye + Skilky.BestBonus;
        Assert.Equal([perfect, perfect], SkilkyScores(SkilkyBothExact(Handicap.Off), 2));
        var h = SkilkyBothExact(Handicap.Auto);
        NoteSaysBoth(h);
        Assert.Equal([3L, 8L], SkilkyScores(h, 2));                          // 6 × 0,6 = 3,6 → 3; 6 × 1,4 = 8,4 → 8
        var rows = h.View(null).GetProperty("reveal").GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("seat").GetInt32(), r => r.GetProperty("points").GetInt32());
        Assert.Equal(3, rows[0]);                                            // у таблиці розкриття — зараховане
        Assert.Equal(8, rows[1]);
        Assert.Equal(Strong, Factors(h)[0]);
    }

    [Fact]
    public void Skilky_teams_get_the_mean_factor_of_their_players_and_stay_equal()
    {
        var h = SkilkyTable(new { teams = "2" }, "Оля", "Петро", "Ганна", "Іван");
        var t = SkilkyTruth(h);
        var teams = h.View(null).GetProperty("teams").EnumerateArray().ToArray();
        foreach (var team in teams) Assert.True(h.Act(team.GetProperty("captain").GetInt32(), "answer", new { value = t }).Ok);
        SkilkyUntil(h, Skilky.PhaseReveal);
        var f = Factors(h);
        var sc = SkilkyScores(h, 4);
        foreach (var team in teams)
        {
            var seats = team.GetProperty("seats").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var want = (long)Math.Floor((Skilky.Bullseye + Skilky.BestBonus) * seats.Average(s => f[s]) + 1e-9);
            Assert.All(seats, s => Assert.Equal(want, sc[s]));
        }
    }
}
