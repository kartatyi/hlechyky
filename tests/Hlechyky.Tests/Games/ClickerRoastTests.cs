using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Прожарка дня (v13 Г6, ClickerRoast.cs + ClickerDay.cs): о 23:30 після підсумку Глек пише окреме повідомлення — intro,
/// переможці «Майстра» й «Ловця», до трьох рядків із доступних (кліки, палій, посол, зник, ледар), outro. Рід — за ніком,
/// менше двох людей — тиша, вимикач <c>Clicker:Day:Roast</c>.
/// </summary>
public class ClickerRoastTests
{
    static DateTimeOffset Local(int y, int m, int d, int h, int min) =>
        new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, m, d, h, min, 0, DateTimeKind.Unspecified), Days.Kyiv), TimeSpan.Zero);

    static string BookPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "data", "clicker", "roast.json");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("data/clicker/roast.json");
    }

    /// <summary>Книга з однією міченою фразою на вид — щоб у тесті видно, які рядки склалися.</summary>
    static ClickerRoastBook Tagged() => ClickerRoastBook.Parse("""
        {"intro":["INTRO"],"masterDay":["M {nick} {points}"],"catcherDay":["C {nick} {n}"],"clicks":["CL {nick} {n}"],
         "stoker":["ST {nick} {n}"],"envoy":["EN {nick} {n}"],"absent":["AB {nick} {days}"],"lazy":["LZ {nick} {n}"],"outro":["OUTRO"]}
        """).Book;

    static string[] Lines(string? text) => text!.Split('\n');

    static string Roast(ClickerRoastBook book, RoastWinner[] master, RoastWinner[] catcher, RoastPerson[] today, RoastAbsent[] absent,
        Func<string, bool>? she = null, int seed = 1) =>
        ClickerRoast.Compose(book, master, catcher, today, absent, she ?? (_ => false), new Random(seed))!;

    [Fact]
    public void The_real_book_has_no_problems_and_every_kind_has_phrases()
    {
        var (book, problems) = ClickerRoastBook.Parse(File.ReadAllText(BookPath()));
        Assert.Empty(problems);
        foreach (var kind in ClickerRoastBook.Kinds) Assert.True(book.Count(kind) >= 5, kind);
    }

    [Fact]
    public void Every_real_phrase_fills_all_its_braces_in_both_genders()
    {
        var (book, _) = ClickerRoastBook.Parse(File.ReadAllText(BookPath()));
        var rng = new Random(7);
        foreach (var kind in ClickerRoastBook.Kinds)
            for (var i = 0; i < 60; i++)
                foreach (var she in new[] { false, true })
                {
                    var text = ClickerRoastBook.Fill(book.Pick(kind, rng), "Оля", 12, 34, 3, she);
                    Assert.DoesNotContain("{", text);
                    Assert.DoesNotContain("}", text);
                }
    }

    [Theory]
    [InlineData(1, "1 раз")]
    [InlineData(2, "2 рази")]
    [InlineData(4, "4 рази")]
    [InlineData(5, "5 разів")]
    [InlineData(11, "11 разів")]
    [InlineData(12, "12 разів")]
    [InlineData(21, "21 раз")]
    [InlineData(22, "22 рази")]
    [InlineData(25, "25 разів")]
    [InlineData(101, "101 раз")]
    [InlineData(111, "111 разів")]
    [InlineData(12345, "12 345 разів")]
    public void A_declined_number_takes_the_right_form(long n, string expected) =>
        Assert.Equal(expected, ClickerRoastBook.Fill("{n:раз|рази|разів}", "Оля", n, n, (int)n, false));

    [Fact]
    public void Declension_works_for_n_points_and_days_and_keeps_gender_pairs()
    {
        Assert.Equal("Оля 2 дні 3 бали 21 клацання, вона пішла",
            ClickerRoastBook.Fill("{nick} {days:день|дні|днів} {points:бал|бали|балів} {n:клацання|клацання|клацань}, {він|вона} {пішов|пішла}", "Оля", 21, 3, 2, true));
        Assert.Equal("5 днів і 1 день", ClickerRoastBook.Fill("{days:день|дні|днів} і {n:день|дні|днів}", "x", 1, 0, 5, false));
    }

    [Fact]
    public void A_broken_declension_is_a_problem()
    {
        var (_, problems) = ClickerRoastBook.Parse("""{"intro":["{n:раз|рази}"],"outro":["{x:а|б|в}"],"clicks":["{n:раз|рази|разів} {nick}"]}""");
        Assert.Contains(problems, p => p.StartsWith("intro:"));
        Assert.Contains(problems, p => p.StartsWith("outro:"));
        Assert.DoesNotContain(problems, p => p.StartsWith("clicks:"));
    }

    [Fact]
    public void No_real_phrase_leaves_a_bare_number_next_to_a_noun()
    {
        // Голе {n}/{points}/{days} у справжній книзі — це «2 днів»: число завжди з формами слова.
        var json = File.ReadAllText(BookPath());
        using var doc = JsonDocument.Parse(json);   // JSON валідний
        foreach (var prop in doc.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_')))
            foreach (var line in prop.Value.EnumerateArray().Select(x => x.GetString()!))
                Assert.DoesNotMatch(@"\{(n|points|days)\}", line);
    }

    [Fact]
    public void An_unknown_kind_or_placeholder_is_a_problem_not_silence()
    {
        var (book, problems) = ClickerRoastBook.Parse("""{"_про":"x","intro":["Привіт, {ким}"],"fish":["a"],"outro":["Бувайте"]}""");
        Assert.Contains(problems, p => p.StartsWith("intro:") && p.Contains("{ким}"));
        Assert.Contains(problems, p => p.StartsWith("fish:"));
        Assert.Equal(0, book.Count("intro"));
        Assert.Equal(1, book.Count("outro"));
    }

    [Fact]
    public void A_roast_is_intro_winners_up_to_three_extras_and_outro_and_never_names_a_person_twice()
    {
        var today = new RoastPerson[]
        {
            new("Smaug", 5000, 20, 2), new("Назар", 150, 0, 0), new("Тарас", 2000, 9, 1), new("Ірина", 120, 5, 0), new("Богдан", 800, 4, 1),
        };
        var absent = new RoastAbsent[] { new("Марія", 5) };
        for (var seed = 1; seed <= 25; seed++)
        {
            var lines = Lines(Roast(Tagged(), [new("Smaug", 112)], [new("Назар", 45)], today, absent, seed: seed));
            Assert.Equal("INTRO", lines[0]);
            Assert.Equal("M Smaug 112", lines[1]);
            Assert.Equal("C Назар 45", lines[2]);
            Assert.Equal("OUTRO", lines[^1]);
            var extras = lines[3..^1];
            Assert.Equal(3, extras.Length);
            var nicks = extras.Select(l => l.Split(' ')[1]).Concat(["Smaug", "Назар"]).ToList();
            Assert.Equal(nicks.Count, nicks.Distinct().Count());                  // переможці не повторюються і в екстрах
            Assert.DoesNotContain(extras, l => l.Contains("Smaug") || l.Contains("Назар"));
        }
    }

    [Fact]
    public void Each_extra_is_picked_from_its_own_numbers()
    {
        // Лише «кліки»: найбільше серед не названих.
        var clicks = Roast(Tagged(), [], [], [new("Оля", 900, 0, 0), new("Тарас", 4000, 0, 0)], []);
        Assert.Equal("CL Тарас 4 000", Lines(clicks)[1]);

        // Лише «палій»: від трьох партій, найбільше.
        var stoker = Lines(Roast(Tagged(), [], [], [new("Оля", 10, 2, 0), new("Тарас", 10, 11, 0), new("Ірина", 10, 7, 0)], [])).Where(l => l.StartsWith("ST")).ToList();
        Assert.Equal(["ST Тарас 11"], stoker);

        // «Посол»: хто наповнив, {n} — скільки.
        var envoy = Lines(Roast(Tagged(), [], [], [new("Оля", 500, 0, 2), new("Тарас", 500, 0, 0)], [], seed: 3)).Where(l => l.StartsWith("EN")).ToList();
        Assert.True(envoy.Count == 0 || envoy.Single() == "EN Оля 2");

        // «Зник»: найдовша відсутність від двох днів; один день — ще не зник.
        var gone = Lines(Roast(Tagged(), [new("A", 50)], [new("B", 50)], [], [new("Марія", 3), new("Петро", 9), new("Іван", 1)]));
        Assert.Contains("AB Петро 9", gone);
        Assert.DoesNotContain(gone, l => l.StartsWith("AB") && (l.Contains("Марія") || l.Contains("Іван")));

        // «Ледар»: грав, але менше двохсот кліків — найменше.
        var lazy = Lines(Roast(Tagged(), [new("A", 50)], [new("B", 50)], [new("Оля", 150, 0, 0), new("Тарас", 40, 0, 0), new("Ірина", 250, 0, 0)], []));
        Assert.Contains("LZ Тарас 40", lazy);
        Assert.DoesNotContain(lazy, l => l.StartsWith("LZ") && l.Contains("Ірина"));
    }

    [Fact]
    public void Quiet_days_do_not_make_up_extras()
    {
        // Ні палія, ні посла, зниклих і ледарів нема, кліків замало (крім одного) — лишаються переможці й єдиний рядок.
        var today = new RoastPerson[] { new("Smaug", 250, 1, 0), new("Назар", 300, 0, 0) };
        var lines = Lines(Roast(Tagged(), [new("Smaug", 50)], [], today, []));
        Assert.Equal(["INTRO", "M Smaug 50", "CL Назар 300", "OUTRO"], lines);
    }

    [Fact]
    public void The_form_of_a_phrase_follows_the_gender_of_the_nick()
    {
        var book = ClickerRoastBook.Parse("""{"intro":["Привіт"],"masterDay":["{nick} {виграв|виграла} {points}"],"outro":["Бувайте"]}""").Book;
        var him = Lines(ClickerRoast.Compose(book, [new("Тарас", 50)], [], [new("Оля", 10, 0, 0)], [], n => n == "Оля", new Random(1)));
        Assert.Equal("Тарас виграв 50", him[1]);
        var her = Lines(ClickerRoast.Compose(book, [new("Оля", 50)], [], [new("Тарас", 10, 0, 0)], [], n => n == "Оля", new Random(1)));
        Assert.Equal("Оля виграла 50", her[1]);
    }

    [Fact]
    public void Fewer_than_two_people_means_no_roast()
    {
        var one = ClickerRoast.Compose(Tagged(), [new("Оля", 50)], [new("Оля", 30)], [new("Оля", 900, 5, 1)], [], _ => false, new Random(1));
        Assert.Null(one);
        var none = ClickerRoast.Compose(Tagged(), [], [], [], [new("Марія", 4)], _ => false, new Random(1));
        Assert.Null(none);
        // Двоє — вже можна: переможець і ще один, що грав.
        Assert.NotNull(ClickerRoast.Compose(Tagged(), [new("Оля", 50)], [], [new("Тарас", 100, 0, 0)], [], _ => false, new Random(1)));
        // Порожня книга — прожарки нема.
        Assert.Null(ClickerRoast.Compose(ClickerRoastBook.Empty, [new("Оля", 50)], [], [new("Тарас", 100, 0, 0)], [], _ => false, new Random(1)));
    }

    // ---------- щоденник → дані дня ----------

    static ClickerDiaryRow Row(string day, string nick, long clicks, long auto, long? envoys = null) =>
        new(day, nick, default, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["dayClicks"] = clicks, ["kiln"] = new { batches = 1, auto },
            ["envoysFull"] = envoys,
        }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value)), false);

    [Fact]
    public void The_diary_gives_clicks_stoker_and_envoy_differences_and_who_has_vanished()
    {
        var rows = new[]
        {
            Row("2026-11-07", "Smaug", 100, 100, 4),
            Row("2026-11-08", "Smaug", 5000, 120, 5),             // +20 партій палія, +1 посол
            Row("2026-11-05", "Назар", 100, 10),                  // давній знімок без envoysFull = «посольських ще не було»
            Row("2026-11-08", "Назар", 150, 10, 2),               // +0 палія, +2 посла
            Row("2026-11-08", "Новачок", 700, 40, 3),             // знімка до цього дня нема — різниці невідомі
            Row("2026-11-03", "Марія", 100, 5),                   // 5 днів не бачили
            Row("2026-11-07", "Вчорашній", 100, 5),               // лише вчора — ще не зник
        };
        var (today, absent) = ClickerRoast.Gather(rows, "2026-11-08");
        Assert.Equal(["Smaug", "Назар", "Новачок"], today.Select(p => p.Nick).OrderBy(n => n == "Smaug" ? 0 : n == "Назар" ? 1 : 2));
        var smaug = today.Single(p => p.Nick == "Smaug");
        Assert.Equal((5000L, 20L, 1), (smaug.Clicks, smaug.Stoker, smaug.Envoys));
        var nazar = today.Single(p => p.Nick == "Назар");
        Assert.Equal((150L, 0L, 2), (nazar.Clicks, nazar.Stoker, nazar.Envoys));
        var fresh = today.Single(p => p.Nick == "Новачок");
        Assert.Equal((700L, 0L, 0), (fresh.Clicks, fresh.Stoker, fresh.Envoys));
        var gone = Assert.Single(absent);
        Assert.Equal(("Марія", 5), (gone.Nick, gone.Days));
    }

    // ---------- сервіс: підсумок → прожарка ----------

    static (ClickerDayService Svc, FakeSlotsWire Wire, ClickerDiaryService Diary) Kit(ClickerDayOptions? o = null, Func<ClickerRoastBook?>? book = null)
    {
        var clock = new FakeClock { UtcNow = Local(2026, 11, 8, 12, 0) };
        var wire = new FakeSlotsWire();
        var diary = new ClickerDiaryService(null, () => new ClickerDiaryOptions(), clock, person: _ => true);
        var svc = new ClickerDayService(null, new FakeStakes(), () => o ?? new ClickerDayOptions(), clock, wire, person: _ => true)
        {
            Diary = diary,
            RoastBook = book ?? (() => Tagged()),
            She = n => n == "Оля",
            Rng = new Random(5),
        };
        return (svc, wire, diary);
    }

    static void Play(ClickerDayService svc, ClickerDiaryService diary)
    {
        var noon = Local(2026, 11, 8, 12, 0);
        svc.Add("Smaug", 100, noon);
        svc.Add("Назар", 50, noon);
        svc.AddCatch("Оля", 70, noon);
        svc.AddCatch("Тарас", 40, noon);
        diary.Put("Smaug", "2026-11-07", noon.AddDays(-1), """{"dayClicks":100,"kiln":{"auto":100}}""");
        diary.Put("Smaug", "2026-11-08", noon, """{"dayClicks":9000,"kiln":{"auto":130}}""");
        diary.Put("Назар", "2026-11-08", noon, """{"dayClicks":120,"kiln":{"auto":0}}""");
        diary.Put("Оля", "2026-11-08", noon, """{"dayClicks":800,"kiln":{"auto":0}}""");
        diary.Put("Тарас", "2026-11-08", noon, """{"dayClicks":1000,"kiln":{"auto":0}}""");
    }

    [Fact]
    public void After_the_summary_lines_the_glek_posts_one_roast_per_day()
    {
        var (svc, wire, diary) = Kit();
        Play(svc, diary);
        Assert.Equal(1, svc.Settle(Local(2026, 11, 8, 23, 31)));
        // два підсумки (Майстер, Ловець) і одне окреме повідомлення-прожарка
        Assert.Equal(3, wire.Chats.Count);
        Assert.StartsWith("🏆 Майстер дня", wire.Chats[0]);
        Assert.StartsWith("🧺 Ловець дня", wire.Chats[1]);
        var roast = Lines(wire.Chats[2]);
        Assert.Equal("INTRO", roast[0]);
        Assert.Equal("M Smaug 100", roast[1]);
        Assert.Equal("C Оля 70", roast[2]);
        Assert.Equal("OUTRO", roast[^1]);
        Assert.InRange(roast.Length, 5, 7);
        // повторне підбиття нічого не пише
        Assert.Equal(0, svc.Settle(Local(2026, 11, 8, 23, 45)));
        Assert.Equal(3, wire.Chats.Count);
    }

    [Fact]
    public void The_roast_can_be_switched_off()
    {
        var (svc, wire, diary) = Kit(new ClickerDayOptions { Roast = false });
        Play(svc, diary);
        svc.Settle(Local(2026, 11, 8, 23, 31));
        Assert.Equal(2, wire.Chats.Count);                          // лише підсумки дошок
        Assert.DoesNotContain(wire.Chats, c => c.Contains("INTRO"));
    }

    [Fact]
    public void With_fewer_than_two_people_the_day_is_settled_without_a_roast()
    {
        var (svc, wire, diary) = Kit(new ClickerDayOptions { MinPlayers = 1 });
        svc.Add("Smaug", 100, Local(2026, 11, 8, 12, 0));
        diary.Put("Smaug", "2026-11-08", Local(2026, 11, 8, 12, 0), """{"dayClicks":900,"kiln":{"auto":0}}""");
        svc.Settle(Local(2026, 11, 8, 23, 31));
        Assert.Single(wire.Chats);
        Assert.StartsWith("🏆 Майстер дня", wire.Chats[0]);
    }

    [Fact]
    public void No_book_or_a_stale_server_start_means_no_roast_but_the_day_is_still_paid()
    {
        var (svc, wire, diary) = Kit(book: () => null);
        Play(svc, diary);
        Assert.Equal(1, svc.Settle(Local(2026, 11, 8, 23, 31)));
        Assert.Equal(2, wire.Chats.Count);

        // сервер піднявся лише наступного дня — новина прострочена
        var (svc2, wire2, diary2) = Kit();
        Play(svc2, diary2);
        Assert.Equal(1, svc2.Settle(Local(2026, 11, 9, 14, 0)));
        Assert.Equal(2, wire2.Chats.Count);
    }

    [Fact]
    public void The_roast_works_without_a_diary_using_just_the_winners()
    {
        var clock = new FakeClock { UtcNow = Local(2026, 11, 8, 12, 0) };
        var wire = new FakeSlotsWire();
        var svc = new ClickerDayService(null, new FakeStakes(), () => new ClickerDayOptions(), clock, wire, person: _ => true) { RoastBook = () => Tagged() };
        svc.Add("Smaug", 100, clock.UtcNow);
        svc.Add("Назар", 50, clock.UtcNow);
        svc.Settle(Local(2026, 11, 8, 23, 31));
        Assert.Equal(["INTRO", "M Smaug 100", "OUTRO"], Lines(wire.Chats[^1]));
    }
}
