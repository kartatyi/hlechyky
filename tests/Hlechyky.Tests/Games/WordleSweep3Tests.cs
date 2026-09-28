using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Глек-слово, прохід №3: чужі літери тому, хто вже вгадав; 💡 підказка за очко; спринт «кожен своє»;
/// слова на 4 і 6 літер; серія й розподіл спроб на картці щоденного.
/// </summary>
public class WordleSweep3Tests(WordleWords fx) : IClassFixture<WordleWords>
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Тарас"];

    // малі списки на 4 і 6 літер: відповіді + кілька «просто слів» для спроб
    static readonly string[] Four = ["глек", "кіно", "море", "сова", "хата", "липа", "сіно", "торт", "шарф", "юшка",
        "авто", "база", "вода", "гора", "дума", "жаба", "зима", "каша", "лава", "мама", "небо"];
    static readonly string[] Six = ["молоко", "яблуко", "калина", "дитина", "машина", "газета", "гітара", "гречка", "дружба",
        "корона", "лисиця", "малина", "пасіка", "ракета", "собака", "сестра", "цибуля", "шахтар", "ялинка", "їжачок"];
    static readonly string[] Extra = ["воза", "мами", "колеса", "молока"];

    IServiceProvider Services(bool lists = true)
    {
        var sc = new ServiceCollection().AddSingleton(fx.Words);
        if (lists) sc.AddSingleton(new WordleLists([.. Four, .. Six], Extra));
        return sc.BuildServiceProvider();
    }

    RoomHarness Table(int players = 3, object? options = null, int seed = 1)
    {
        var h = new RoomHarness("wordle-race", options, seed, Services());
        for (var i = 0; i < players; i++) Assert.True(h.Join(Nicks[i]).Ok, h.Reply.Message);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static WordleRace Game(RoomHarness h) => (WordleRace)h.Room.Game;
    static string Answer(RoomHarness h, int seat = 0) => Game(h).AnswerOf(seat);
    static ActResult Guess(RoomHarness h, int seat, string word) => h.Act(seat, "guess", new { word });

    string[] Wrong(RoomHarness h, int seat, int n)
    {
        var a = Answer(h, seat);
        var pool = a.Length switch { 4 => Four, 6 => Six, _ => fx.Five };
        return [.. pool.Where(w => w != a).Take(n)];
    }

    static JsonElement Player(JsonElement view, int seat) =>
        view.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("seat").GetInt32() == seat);

    static bool SeesWords(JsonElement view, int seat) => Player(view, seat).GetProperty("words").ValueKind == JsonValueKind.Array;

    // ------------------------------------------------------------------------------ типово — як було

    [Fact]
    public void Defaults_are_the_old_game_one_word_five_letters_no_hints()
    {
        var info = new WordleRace().Info;
        Assert.Equal("same", info.Options!.Single(o => o.Key == "mode").Default);
        Assert.Equal("5", info.Options!.Single(o => o.Key == "len").Default);
        Assert.Equal("off", info.Options!.Single(o => o.Key == "hint").Default);

        var h = Table();
        var v = h.View(0);
        Assert.Equal("same", v.GetProperty("mode").GetString());
        Assert.Equal(5, v.GetProperty("len").GetInt32());
        Assert.False(v.GetProperty("hint").GetBoolean());
        Assert.False(h.Act(0, "hint", new { }).Ok);
    }

    // ------------------------------------------------------------------------------ 106: чужі літери

    [Fact]
    public void Who_solved_sees_rivals_letters_the_rest_and_spectators_do_not()
    {
        var h = Table(3);
        Guess(h, 1, Wrong(h, 1, 1)[0]);
        Guess(h, 2, Wrong(h, 2, 2)[1]);
        Assert.False(SeesWords(h.View(0), 1));

        Assert.Contains("чужі літери", Guess(h, 0, Answer(h)).Message);
        var mine = h.View(0);
        Assert.True(SeesWords(mine, 1));
        Assert.Equal(Wrong(h, 1, 1)[0], Player(mine, 1).GetProperty("words")[0].GetString());
        Assert.True(SeesWords(mine, 2));

        // той, хто ще гадає, і глядач — як і раніше, самі кольори
        Assert.False(SeesWords(h.View(1), 2));
        Assert.False(SeesWords(h.View(null), 1));
        Assert.False(SeesWords(h.View(null), 0));
    }

    [Fact]
    public void Who_ran_out_of_tries_sees_letters_too()
    {
        var h = Table(3);
        foreach (var w in Wrong(h, 0, 6)) Guess(h, 0, w);
        Guess(h, 1, Wrong(h, 1, 1)[0]);
        Assert.True(SeesWords(h.View(0), 1));
        Assert.False(SeesWords(h.View(2), 1));
    }

    // ------------------------------------------------------------------------------ 109: підказка

    [Fact]
    public void Hint_opens_one_letter_in_place_and_costs_a_point_of_the_win()
    {
        var h = Table(2, new { hint = "on" });
        var r = h.Act(0, "hint", new { });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("💡", r.Message);
        var hints = h.View(0).GetProperty("me").GetProperty("hints");
        Assert.Equal(1, hints.GetArrayLength());
        var at = hints[0].GetProperty("i").GetInt32();
        Assert.Equal(Answer(h)[at].ToString(), hints[0].GetProperty("ch").GetString());
        // суперник бачить лише, що підказку взято, а не саму літеру
        Assert.Equal(1, Player(h.View(1), 0).GetProperty("hints").GetInt32());
        Assert.Equal(0, h.View(1).GetProperty("me").GetProperty("hints").GetArrayLength());

        Assert.True(h.Act(0, "hint", new { }).Ok);
        Assert.False(h.Act(0, "hint", new { }).Ok);   // більше двох не дають
        var two = h.View(0).GetProperty("me").GetProperty("hints");
        Assert.NotEqual(two[0].GetProperty("i").GetInt32(), two[1].GetProperty("i").GetInt32());

        Guess(h, 0, Answer(h));   // з першої, першим: 6 + 1 − 2 підказки
        Assert.Equal(5, Player(h.View(0), 0).GetProperty("gained").GetInt32());
    }

    [Fact]
    public void Hint_never_reveals_a_letter_already_green_and_never_drives_below_zero()
    {
        var h = Table(2, new { hint = "on" }, seed: 7);
        var a = Answer(h);
        // спроба з чотирма зеленими: лишається одне місце — саме його й підкаже
        var near = fx.Five.FirstOrDefault(w => w != a && Enumerable.Range(0, 5).Count(i => w[i] == a[i]) == 4);
        if (near is not null)
        {
            Guess(h, 0, near);
            Assert.True(h.Act(0, "hint", new { }).Ok);
            var i = h.View(0).GetProperty("me").GetProperty("hints")[0].GetProperty("i").GetInt32();
            Assert.NotEqual(near[i], a[i]);
            Assert.False(h.Act(0, "hint", new { }).Ok);   // відкривати більше нічого
        }

        var g = Table(2, new { hint = "on" }, seed: 3);
        Guess(g, 1, Answer(g));   // перший — не наш
        g.Act(0, "hint", new { });
        g.Act(0, "hint", new { });
        foreach (var w in Wrong(g, 0, 5)) Guess(g, 0, w);
        Guess(g, 0, Answer(g));   // з шостої: 1 − 2 = 0, не мінус
        Assert.Equal(0, Player(g.View(0), 0).GetProperty("gained").GetInt32());
    }

    // ------------------------------------------------------------------------------ 108: спринт

    [Fact]
    public void Sprint_gives_everyone_an_own_word_and_three_solved_win()
    {
        var h = Table(3, new { mode = "sprint" });
        Assert.True(Game(h).Sprint);
        var v = h.View(0);
        Assert.Equal("sprint", v.GetProperty("mode").GetString());
        Assert.Equal(WordleRace.SprintTarget, v.GetProperty("target").GetInt32());
        Assert.Equal(h.Clock.UtcNow.AddSeconds(WordleRace.DefaultSeconds * WordleRace.SprintTarget), v.GetProperty("endsAt").GetDateTimeOffset());
        Assert.Equal(3, new[] { Answer(h, 0), Answer(h, 1), Answer(h, 2) }.Distinct().Count());
        Assert.False(h.Act(0, "hint", new { }).Ok);   // у спринті підказок нема

        var first = Answer(h, 0);
        Assert.Contains("1 з 3", Guess(h, 0, first).Message);
        Assert.NotEqual(first, Answer(h, 0));
        Assert.Equal(0, h.View(0).GetProperty("me").GetProperty("attempts").GetInt32());
        // своє друге слово — чужі літери все одно сховані
        Assert.False(SeesWords(h.View(0), 1));

        Guess(h, 1, Answer(h, 1));
        Guess(h, 0, Answer(h, 0));
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Guess(h, 0, Answer(h, 0));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var e = Assert.Single(h.Finished);
        Assert.Equal([0], e.Result.Winners);
        Assert.Equal(3, e.Result.Scores![0]);
        Assert.Equal(1, e.Result.Scores[1]);
        Assert.Equal(0, e.Result.Scores[2]);
        var played = Player(h.View(null), 0).GetProperty("played");
        Assert.Equal(3, played.GetArrayLength());
        Assert.True(SeesWords(h.View(null), 1));   // партію зіграно — відкрито все
    }

    [Fact]
    public void Sprint_six_misses_bring_the_next_word_and_the_old_one_is_told()
    {
        var h = Table(2, new { mode = "sprint" });
        var was = Answer(h, 0);
        ActResult r = default!;
        foreach (var w in Wrong(h, 0, 6)) r = Guess(h, 0, w);
        Assert.Contains(was.ToUpperInvariant(), r.Message);
        Assert.NotEqual(was, Answer(h, 0));
        var me = h.View(0).GetProperty("me");
        Assert.Equal(0, me.GetProperty("attempts").GetInt32());
        Assert.False(me.GetProperty("played")[0].GetProperty("ok").GetBoolean());
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
    }

    [Fact]
    public void Sprint_time_out_goes_to_more_words_then_to_the_quicker()
    {
        var h = Table(3, new { mode = "sprint", seconds = "90" });
        Guess(h, 2, Answer(h, 2));
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Guess(h, 1, Answer(h, 1));
        h.Tick(90 * 3 * 1000 / h.Room.Info.TickMs + 2);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([2], Assert.Single(h.Finished).Result.Winners);

        var z = Table(2, new { mode = "sprint", seconds = "90" });
        z.Tick(90 * 3 * 1000 / z.Room.Info.TickMs + 2);
        Assert.Empty(Assert.Single(z.Finished).Result.Winners);
    }

    // ------------------------------------------------------------------------------ 112: 4 і 6 літер

    [Theory]
    [InlineData("4")]
    [InlineData("6")]
    public void Four_and_six_letter_words_come_from_their_lists(string len)
    {
        var n = int.Parse(len);
        var h = Table(2, new { len });
        Assert.Equal(n, Answer(h).Length);
        Assert.Equal(n, h.View(0).GetProperty("len").GetInt32());
        var bad = Guess(h, 0, fx.Five[0]);
        Assert.False(bad.Ok);
        Assert.Contains(n == 4 ? "чотири" : "шість", bad.Message);
        Assert.False(Guess(h, 0, n == 4 ? "ґґґґ" : "ґґґґґґ").Ok);   // не слово
        Assert.True(Guess(h, 0, n == 4 ? "мами" : "молока").Ok);   // словоформа з великого словника — можна
        var row = h.View(0).GetProperty("me").GetProperty("rows")[0];
        Assert.Equal(n, row.GetProperty("marks").GetString()!.Length);
        Assert.True(Guess(h, 0, Answer(h)).Ok);
        Assert.Equal(new string('G', n), h.View(0).GetProperty("me").GetProperty("rows")[1].GetProperty("marks").GetString());
    }

    [Fact]
    public void Sprint_on_six_letters_works_too()
    {
        var h = Table(2, new { mode = "sprint", len = "6" });
        Assert.Equal(6, Answer(h, 0).Length);
        Assert.True(Guess(h, 0, Answer(h, 0)).Ok);
        Assert.Equal(6, Answer(h, 0).Length);
    }

    [Fact]
    public void Without_lists_a_four_letter_table_says_so_instead_of_breaking()
    {
        var h = new RoomHarness("wordle-race", new { len = "4" }, 1, Services(lists: false));
        var r = h.Join("Оля");
        Assert.False(r.Ok);
        Assert.Contains("4 літери", r.Message);
    }

    [Fact]
    public void Marks_work_on_six_letters_with_doubles()
    {
        Assert.Equal("GGGGGG", Wordle.Marks("молоко", "молоко"));
        Assert.Equal("YGGGBB", Wordle.Marks("молоко", "колода"));   // к не на місці, «о» з трьох уже зелені
        Assert.Equal("BBBB", Wordle.Marks("глек", "тиша"));
        Assert.Equal("YYGY", Wordle.Marks("кіно", "окні"));
    }

    [Fact]
    public void Shipped_four_and_six_lists_are_clean_and_long_enough()
    {
        var dir = Paths.Resolve("data/words");
        foreach (var (n, min) in new[] { (4, 250), (6, 800) })
        {
            var words = File.ReadAllLines(Path.Combine(dir, $"uk-{n}.txt")).Where(l => l.Length > 0).ToArray();
            Assert.True(words.Length >= min, $"uk-{n}: {words.Length}");
            Assert.All(words, w => Assert.Equal(n, w.Length));
            Assert.All(words, w => Assert.Equal(w, Hlechyky.Games.Economy.Words.Normalize(w)));
            Assert.Equal(words.Length, words.Distinct().Count());
            foreach (var bad in new[] { "дурень", "нацист", "кремль", "максим", "кров" }) Assert.DoesNotContain(bad, words);
        }
        Assert.Contains("глек", File.ReadAllLines(Path.Combine(dir, "uk-4.txt")));
    }

    // ------------------------------------------------------------------------------ 136: серія й розподіл

    [Fact]
    public void Stats_count_streak_best_and_the_spread_of_tries()
    {
        var rows = new[]
        {
            ("2026-09-20", 3), ("2026-09-19", 4), ("2026-09-18", 3),        // серія 3 до сьогодні
            ("2026-09-10", 2), ("2026-09-09", 1), ("2026-09-08", 6), ("2026-09-07", 3), ("2026-09-06", 5),   // найдовша 5
            ("кривий", 2),
        };
        var s = WordleStats.Of(rows, "2026-09-20");
        Assert.Equal(3, s.Streak);
        Assert.Equal(5, s.Best);
        Assert.Equal(8, s.Solved);
        Assert.Equal([1, 1, 3, 1, 1, 1], s.Dist);
        Assert.Equal(3, s.Today);

        // сьогодні ще не грав — серія тримається вчорашнім днем
        var y = WordleStats.Of(rows, "2026-09-21");
        Assert.Equal(3, y.Streak);
        Assert.Equal(0, y.Today);
        // пропустив день — серія згоріла
        Assert.Equal(0, WordleStats.Of(rows, "2026-09-22").Streak);
        Assert.Equal(0, WordleStats.Of([], "2026-09-22").Best);
    }
}
