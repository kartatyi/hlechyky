using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Байкарі» (specs/bluff.md): банк, стіл і фази, брехня, картки, вибір і розкриття, приховане, кінець, вихід,
/// рематч, контракт із модулем і швидкодія. Партія живе від тика, тож майже кожен тест — «прокрути годинник і
/// подивись». Правила грають на своєму маленькому банку (<see cref="BluffBankSource"/>): справжній банк автори
/// переписують, а тест має падати лише тоді, коли ламається гра.
/// </summary>
public class BluffTests(ITestOutputHelper output)
{
    const int MaxTicks = 4000;

    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан", "Леся", "Остап"];
    /// <summary>Брехні, що не схожі ні на правду, ні на заготовки тестового банку, ні одна на одну.</summary>
    internal static readonly string[] Lies = ["Бамбарбія", "кергуду", "тиндирики", "шмуцики", "брязкальця", "ґаджиґулі", "хухрики", "пампулечки"];

    const string BankJson = """
        { "version": 1, "questions": [
          { "id": "t01", "cat": "ukraine", "q": "У XIX столітті у Львові вулиці вперше освітили лампами на ___", "answer": "гасі",
            "accept": ["гас", "гасові лампи"], "decoys": ["свинячому салі", "китовому жирі", "самогоні"], "note": "Лукасевич і Зег, 1853 рік." },
          { "id": "t02", "cat": "ukraine", "q": "Найдовша річка, що тече лише територією України, — ___", "answer": "Південний Буг",
            "accept": ["пд буг", "буг", "південний буг"], "decoys": ["Десна", "Інгулець", "Ворскла"], "note": "806 км." },
          { "id": "t03", "cat": "history", "q": "Анна, донька Ярослава Мудрого, стала королевою ___", "answer": "Франції",
            "accept": ["франція"], "decoys": ["Норвегії", "Угорщини", "Швеції"], "note": "" },
          { "id": "t04", "cat": "history", "q": "Перший безпечний ліфт Отіс показав публіці в ___", "answer": "Нью-Йорку",
            "accept": ["нью йорк"], "decoys": ["Лондоні", "Парижі", "Чикаго"] },
          { "id": "t05", "cat": "nature", "q": "Кров восьминога ___ кольору", "answer": "синього",
            "accept": ["синя", "блакитного"], "decoys": ["зеленого", "жовтого", "прозорого"], "note": "Гемоціанін." },
          { "id": "t06", "cat": "science", "q": "Найпоширеніший хімічний елемент у Всесвіті — ___", "answer": "водень",
            "accept": ["гідроген"], "decoys": ["залізо", "кисень", "кремній"], "note": "Три чверті речовини." },
          { "id": "t07", "cat": "food", "q": "До XVII століття морква була переважно ___ кольору", "answer": "фіолетового",
            "accept": ["фіолетова"], "decoys": ["білого", "чорного", "синього"], "note": "Голландці." },
          { "id": "t08", "cat": "world", "q": "У токійському метро осія ___ пасажирів у вагони", "answer": "заштовхують",
            "accept": ["штовхають"], "decoys": ["витягують", "рахують", "будять"], "note": "Білі рукавички." },
          { "id": "t09", "cat": "sport", "q": "Перший чемпіонат світу з футболу 1930 року відбувся в ___", "answer": "Уругваї",
            "accept": ["уругвай"], "decoys": ["Бразилії", "Італії", "Аргентині"], "note": "Монтевідео." },
          { "id": "t10", "cat": "lang", "q": "Українське слово «лелека» запозичене з ___ мов", "answer": "тюркських",
            "accept": ["тюркська"], "decoys": ["балтійських", "грецької", "романських"], "note": "leylek." }
        ] }
        """;

    static readonly IReadOnlyList<BluffQuestion> TestBank = BluffBank.Parse(BankJson);

    static IServiceProvider Services(Db? db = null, IReadOnlyList<BluffQuestion>? bank = null)
    {
        var services = new ServiceCollection().AddSingleton(new BluffBankSource(bank ?? TestBank));
        if (db is not null) services.AddSingleton(db);
        return services.BuildServiceProvider();
    }

    static RoomHarness Seated(int players, int seed = 42, object? options = null, IServiceProvider? services = null)
    {
        var h = new RoomHarness("bluff", options: options, seed: seed, services: services ?? Services());
        for (var i = 0; i < players; i++) Assert.True(h.Join(Nicks[i]).Ok, h.Reply.Message);
        return h;
    }

    internal static RoomHarness Table(int players = 3, int seed = 42, object? options = null, IServiceProvider? services = null)
    {
        var h = Seated(players, seed, options, services);
        Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static JsonElement V(RoomHarness h, int? seat = null) => h.View(seat);
    static string Phase(RoomHarness h) => V(h).GetProperty("phase").GetString()!;
    static int QNo(RoomHarness h) => V(h).GetProperty("q").GetInt32();
    static long Score(RoomHarness h, int seat) => V(h).GetProperty("scores")[seat].GetInt64();
    static int PhaseMs(RoomHarness h) => V(h).GetProperty("phaseMs").GetInt32();
    static bool Playing(RoomHarness h) => h.Room.Status == RoomStatus.Playing;
    static int[] Ints(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetInt32())];
    static int[] Revealed(RoomHarness h) => Ints(V(h).GetProperty("revealed"));

    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < MaxTicks && Playing(h) && Phase(h) != phase; i++) h.Tick();
        Assert.True(!Playing(h) || Phase(h) == phase, $"не дочекались фази {phase}");
    }

    /// <summary>Догортати поточне питання: до наступного або до кінця партії.</summary>
    static void NextQuestion(RoomHarness h)
    {
        var q = QNo(h);
        for (var i = 0; i < MaxTicks && Playing(h) && QNo(h) == q; i++) h.Tick();
    }

    static void PlayOut(RoomHarness h)
    {
        for (var i = 0; i < MaxTicks * 4 && Playing(h); i++) h.Tick();
        Assert.False(Playing(h));
    }

    static BluffQuestion Question(RoomHarness h, IReadOnlyList<BluffQuestion>? bank = null)
    {
        var text = V(h).GetProperty("text").GetString();
        return (bank ?? TestBank).First(q => q.Q == text);
    }

    static void Lie(RoomHarness h, int seat, string? text = null)
    {
        var r = h.Act(seat, "lie", new { text = text ?? Lies[seat] });
        Assert.True(r.Ok, r.Message);
    }

    /// <summary>Дочекатись фази брехні й роздати брехні вказаним місцям.</summary>
    static BluffQuestion Write(RoomHarness h, params int[] seats)
    {
        Until(h, Bluff.PhaseWrite);
        var q = Question(h);
        foreach (var s in seats) Lie(h, s);
        return q;
    }

    /// <summary>Усі вказані пишуть, і тик одразу веде до вибору (коли пишуть усі за столом).</summary>
    static BluffQuestion WriteAndPick(RoomHarness h, params int[] seats)
    {
        var q = Write(h, seats);
        Until(h, Bluff.PhasePick);
        return q;
    }

    static JsonElement Cards(RoomHarness h, int? seat = null) => V(h, seat).GetProperty("options");

    static int CardOf(RoomHarness h, string text)
    {
        var cards = Cards(h);
        for (var i = 0; i < cards.GetArrayLength(); i++)
            if (BluffText.Norm(cards[i].GetProperty("text").GetString()) == BluffText.Norm(text)) return i;
        return -1;
    }

    static int TruthCard(RoomHarness h) => CardOf(h, Question(h).Answer);

    static void PickCard(RoomHarness h, int seat, int i)
    {
        var r = h.Act(seat, "pick", new { i });
        Assert.True(r.Ok, r.Message);
    }

    /// <summary>Тикати, доки не відкриють картку i (або доки розкриття не скінчиться).</summary>
    static void UntilOpen(RoomHarness h, int i)
    {
        Until(h, Bluff.PhaseReveal);
        for (var t = 0; t < MaxTicks && Playing(h) && Phase(h) == Bluff.PhaseReveal && !Revealed(h).Contains(i); t++) h.Tick();
        Assert.Contains(i, Revealed(h));
    }

    static string Frame(RoomHarness h)
    {
        lock (h.Room.Sync) return Raw(h.Room.Game.Frame());
    }

    static readonly JsonSerializerOptions Plain = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// JSON без екранування кирилиці: дріт (SignalR) пише її як \uXXXX, і пошук підрядка «Таємниця» в такому тексті
    /// нічого б не знайшов навіть там, де вона є. Для перевірок «приховане не тече» — лише цей.
    /// </summary>
    static string Raw(object? view) => JsonSerializer.Serialize(view, Plain);

    static string RawView(RoomHarness h, int? seat)
    {
        lock (h.Room.Sync) return Raw(h.Room.Game.View(seat));
    }

    // =====================================================================================
    // банк
    // =====================================================================================

    [Fact]
    public void The_real_bank_is_the_verified_one_with_one_blank_and_honest_decoys_everywhere()
    {
        var bank = BluffBank.All;
        Assert.True(bank.Count >= 250, $"у банку лише {bank.Count} питань — перевірений банк не підклали?");
        Assert.All(bank, q =>
        {
            Assert.Equal(1, CountBlanks(q.Q));
            Assert.False(string.IsNullOrWhiteSpace(q.Answer));
            Assert.True(q.Decoys.Count >= 2, $"«{q.Q}»: заготовок {q.Decoys.Count}");
            Assert.True(q.Answer.Length <= Bluff.MaxLie, $"«{q.Q}»: правда довша за брехню");
            Assert.False(q.Note.Length == 0, $"«{q.Q}»: нема «а насправді»");
            // Жодна заготовка не близнюк правди (банк таких відсіює мовчки) і не «правда» для гравця, що надрукує її сам.
            Assert.All(q.Decoys, d => Assert.DoesNotContain(q.Forms, f => BluffText.LooksSame(d, f)));
            Assert.All(q.Decoys, d => Assert.False(BluffText.LooksTrue(d, q), $"«{q.Q}»: заготовка «{d}» схожа на правду"));
            Assert.False(BluffText.MixedScripts(q.Answer), q.Answer);
            Assert.All(q.Decoys, d => Assert.False(BluffText.MixedScripts(d), d));
            // Гравець, що надрукує заготовку сам, не почує «пиши кирилицею».
            Assert.False(BluffText.ForeignLetters(q.Answer), q.Answer);
            Assert.All(q.Decoys, d => Assert.Null(Bluff.Refuse(d, q)));
        });
        // Та сама правда двічі — друге питання в тій самій партії вгадували б з пам'яті.
        var truths = bank.GroupBy(q => BluffText.Norm(q.Answer)).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(truths.Count == 0, "одна правда в кількох питаннях: " + string.Join(", ", truths));
    }

    /// <summary>
    /// Схема самого файла (формат bluff із CONTENT-FORMATS.md), без поблажливого парсера гри: той мовчки відкидає криве,
    /// а тут криве має впасти голосно — інакше зламане питання чи заготовка тихо зникли б із гри.
    /// </summary>
    [Fact]
    public void The_bank_file_matches_the_bluff_format_and_nothing_is_silently_dropped()
    {
        var path = Hlechyky.Paths.Resolve(BluffBank.FileName);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));      // JSON без коментарів і хвостових ком
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var raw = root.GetProperty("questions").EnumerateArray().ToList();
        var known = BluffCats.All.Select(c => c.Key).Where(k => k != BluffCats.Any).ToHashSet();
        var ids = new HashSet<string>();
        foreach (var e in raw)
        {
            var id = e.GetProperty("id").GetString()!;
            Assert.Matches("^b[0-9]{4}$", id);
            Assert.True(ids.Add(id), $"id {id} двічі");
            Assert.Contains(e.GetProperty("cat").GetString()!, known);
            var q = e.GetProperty("q").GetString()!;
            Assert.Equal(1, CountBlanks(q));
            Assert.DoesNotContain("____", q);
            var answer = e.GetProperty("answer").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(answer), id);
            var accept = e.GetProperty("accept").EnumerateArray().Select(x => BluffText.Norm(x.GetString())).ToList();
            var decoys = e.GetProperty("decoys").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.True(decoys.Count >= 2, $"{id}: заготовок {decoys.Count}");
            // Після нормалізації заготовка не збігається ні з правдою, ні з жодним її написанням, ні з іншою заготовкою.
            foreach (var d in decoys)
            {
                Assert.NotEqual(BluffText.Norm(answer), BluffText.Norm(d));
                Assert.DoesNotContain(BluffText.Norm(d), accept);
            }
            Assert.Equal(decoys.Count, decoys.Select(BluffText.Norm).Distinct().Count());
            Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("note").GetString()), id);
            Assert.StartsWith("https://", e.GetProperty("source").GetString());
        }
        // Гра вантажить саме цей файл і нічого з нього не губить.
        var bank = BluffBank.Load(path);
        Assert.Equal(raw.Count, bank.Count);
        Assert.Equal(raw.Count, BluffBank.All.Count);
        foreach (var e in raw)
        {
            var q = bank.Single(x => x.Id == e.GetProperty("id").GetString());
            Assert.Equal(e.GetProperty("decoys").GetArrayLength(), q.Decoys.Count);
        }
    }

    static int CountBlanks(string text)
    {
        var n = 0;
        for (var at = text.IndexOf(BluffBank.Blank, StringComparison.Ordinal); at >= 0; at = text.IndexOf(BluffBank.Blank, at + 3, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Question_ids_and_keys_are_unique()
    {
        var bank = BluffBank.All;
        Assert.Equal(bank.Count, bank.Select(q => q.Key).Distinct().Count());
        var ids = bank.Where(q => q.Id.Length > 0).Select(q => q.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Every_category_in_the_bank_is_known_and_every_topic_has_questions()
    {
        var known = BluffCats.All.Select(c => c.Key).Where(k => k != BluffCats.Any).ToHashSet();
        Assert.Equal(9, known.Count);
        Assert.All(BluffBank.All, q => Assert.Contains(q.Cat, known));
        // Кожну тему з попапа можна обрати й зіграти навіть найдовшу партію (10 питань) — і ще на кілька «Ще раз».
        Assert.All(known, k =>
        {
            var n = BluffBank.All.Count(q => q.Cat == k);
            Assert.True(n >= 15, $"у темі {k} лише {n} питань");
        });
    }

    [Fact]
    public void A_missing_or_broken_bank_file_loads_as_empty_not_as_an_exception()
    {
        Assert.Empty(BluffBank.Load(Path.Combine(Path.GetTempPath(), "нема-" + Guid.NewGuid().ToString("N") + ".json")));
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ \"questions\": [ { \"q\": ");
            Assert.Empty(BluffBank.Load(path));
            File.WriteAllText(path, "\"рядок замість банку\"");
            Assert.Empty(BluffBank.Load(path));
        }
        finally { File.Delete(path); }
        Assert.Empty(BluffBank.Parse("[]"));
    }

    [Fact]
    public void Unknown_category_falls_back_to_odd_and_a_question_without_blank_is_dropped()
    {
        var bank = BluffBank.Parse("""
            [ { "cat": "космос", "q": "Мінімальне питання з ____ пропуском", "answer": "одним" },
              { "q": "Без пропуску взагалі", "answer": "так" },
              { "q": "Два ___ пропуски ___", "answer": "ні" },
              { "q": "Без правди ___", "answer": "" },
              { "q": "Заготовка-правда ___", "answer": "гасі", "decoys": ["гасу", "салі", "Салі!", 5, "на гасі й сірниках"] },
              "не об'єкт" ]
            """);
        Assert.Equal(2, bank.Count);
        var first = bank[0];
        Assert.Equal(BluffCats.Odd, first.Cat);
        Assert.Equal("Мінімальне питання з ___ пропуском", first.Q);
        Assert.Empty(first.Accept);
        Assert.Empty(first.Decoys);
        Assert.Equal("", first.Note);
        // «гасу» — сама правда з одруківкою, «Салі!» — дубль «салі», 5 — не рядок; фраза, де правда лише всередині,
        // лишається: заготовки звіряють люди, грубе сито — для гравців.
        Assert.Equal(["салі", "на гасі й сірниках"], bank[1].Decoys);
    }

    [Fact]
    public void Topics_parse_like_the_lobby_sends_them()
    {
        Assert.Null(BluffCats.Parse(null));
        Assert.Null(BluffCats.Parse("all"));
        Assert.Null(BluffCats.Parse("history,all"));
        Assert.Null(BluffCats.Parse("космос"));
        Assert.Equal(["history", "lang"], BluffCats.Parse("history,lang,космос")!.Order());
        Assert.Equal("Слова й мова", BluffCats.Label("lang"));
        Assert.Equal((30, 20), BluffPace.Seconds("fast"));
        Assert.Equal((45, 30), BluffPace.Seconds("normal"));
        Assert.Equal((60, 45), BluffPace.Seconds("slow"));
    }

    // =====================================================================================
    // стіл і фази
    // =====================================================================================

    [Fact]
    public void The_table_waits_for_the_host_and_shows_the_question_only_from_read()
    {
        var h = Seated(2);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal("", V(h).GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("options").ValueKind);
        Assert.False(h.Act(0, "lie", new { text = "рано" }).Ok);

        Assert.True(h.Start().Ok);
        Assert.Equal(Bluff.PhaseRead, Phase(h));
        Assert.Contains(BluffBank.Blank, V(h).GetProperty("text").GetString());
        Assert.Equal(1, QNo(h));
        Assert.Equal(Bluff.DefaultQuestions, V(h).GetProperty("of").GetInt32());
        Assert.Contains("грати в байкарів", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void One_player_cannot_start_and_empty_topics_refuse_to_start_with_a_clear_message()
    {
        var alone = Seated(1);
        Assert.False(alone.Start().Ok);
        Assert.Contains("щонайменше 2", alone.Reply.Message);

        var h = Seated(2, options: new { cat = "odd" });   // у тестовому банку «Дивного» нема
        Assert.False(h.Start().Ok);
        Assert.Equal("У цих темах ще нема питань — обери інші теми", h.Reply.Message);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
    }

    [Fact]
    public void Options_questions_pace_and_topics_are_read_from_the_lobby()
    {
        // Історії й мови в тестовому банку три питання, а просили п'ять — граємо стільки, скільки є.
        var h = Table(2, options: new { questions = "5", pace = "fast", cat = "history,lang" });
        Assert.Equal(3, V(h).GetProperty("of").GetInt32());
        var cats = new List<string>();
        while (Playing(h))
        {
            cats.Add(V(h).GetProperty("cat").GetString()!);
            Until(h, Bluff.PhaseWrite);
            Assert.Equal(30_000, PhaseMs(h));
            Until(h, Bluff.PhasePick);
            Assert.Equal(20_000, PhaseMs(h));
            NextQuestion(h);
        }
        Assert.All(cats, c => Assert.Contains(c, new[] { "history", "lang" }));
        Assert.Equal(3, cats.Count);

        var slow = Table(2, options: new { pace = "slow", questions = "10" });
        Assert.Equal(10, V(slow).GetProperty("of").GetInt32());
        Until(slow, Bluff.PhaseWrite);
        Assert.Equal(60_000, PhaseMs(slow));
    }

    [Fact]
    public void Read_lasts_three_seconds_then_write_opens()
    {
        var h = Table(2);
        Assert.Equal(Bluff.ReadMs, PhaseMs(h));
        h.Tick(5);
        Assert.Equal(Bluff.PhaseRead, Phase(h));
        h.Tick();
        Assert.Equal(Bluff.PhaseWrite, Phase(h));
        Assert.Equal(45_000, PhaseMs(h));
        // Без брехень фаза чекає свої 45 секунд до останньої.
        h.Tick(89);
        Assert.Equal(Bluff.PhaseWrite, Phase(h));
        h.Tick();
        Assert.Equal(Bluff.PhasePick, Phase(h));
    }

    [Fact]
    public void Write_ends_early_when_everyone_has_a_lie()
    {
        var h = Table(2);
        Write(h, 0, 1);
        Assert.Equal(Bluff.PhaseWrite, Phase(h));   // переходи — лише з тика
        h.Tick();
        Assert.Equal(Bluff.PhasePick, Phase(h));
        Assert.Equal(30_000, PhaseMs(h));
    }

    [Fact]
    public void Pick_ends_early_when_everyone_has_picked()
    {
        var h = Table(2);
        WriteAndPick(h, 0, 1);
        PickCard(h, 0, TruthCard(h));
        h.Tick();
        Assert.Equal(Bluff.PhasePick, Phase(h));      // ще один не обрав
        PickCard(h, 1, TruthCard(h));
        h.Tick();
        Assert.Equal(Bluff.PhaseReveal, Phase(h));
    }

    [Fact]
    public void A_move_makes_the_next_tick_carry_the_fresh_views_and_a_quiet_tick_sends_nothing()
    {
        var h = Table(3);
        Until(h, Bluff.PhaseWrite);
        var n = h.Outbox.Count;
        h.Tick(4);
        Assert.Equal(n, h.Outbox.Count);            // тихі тики — нічого

        Lie(h, 0);
        h.Tick();
        var fresh = h.Outbox.Skip(n).ToList();
        Assert.Single(fresh.OfType<RoomViews>());
        Assert.Empty(fresh.OfType<RoomFrame>());

        var m = h.Outbox.Count;
        h.Tick(3);
        Assert.Equal(m, h.Outbox.Count);
    }

    [Fact]
    public void A_quiet_tick_allocates_nothing()
    {
        var h = Table(8);
        Until(h, Bluff.PhaseWrite);
        Lie(h, 0);
        h.Tick();
        var game = h.Room.Game;
        lock (h.Room.Sync)
        {
            var quiet = 0;
            for (var i = 0; i < 1000; i++) if (game.Tick() == TickResult.None) quiet++;   // прогрів JIT
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++) if (game.Tick() == TickResult.None) quiet++;
            var after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(11_000, quiet);
            // Разові байти рантайму (перекомпіляція гарячого циклу) бувають; на тик — жодного.
            Assert.True(after - before < 256, $"10 000 тихих тиків виділили {after - before} Б");
        }
    }

    // =====================================================================================
    // брехня
    // =====================================================================================

    [Fact]
    public void A_lie_is_recorded_and_echoed_only_to_its_author()
    {
        var h = Table(3);
        Until(h, Bluff.PhaseWrite);
        var r = h.Act(0, "lie", new { text = "  свинячому\u00a0салі " });
        Assert.True(r.Ok);
        Assert.Equal("Записано: «свинячому салі»", r.Message);
        Assert.Equal("свинячому салі", V(h, 0).GetProperty("my").GetProperty("lie").GetString());
        Assert.False(V(h, 0).GetProperty("my").GetProperty("auto").GetBoolean());
        Assert.Equal(JsonValueKind.Null, V(h, 1).GetProperty("my").GetProperty("lie").ValueKind);
        Assert.Equal(JsonValueKind.Null, V(h, null).GetProperty("my").ValueKind);
        h.Tick();
        Assert.True(V(h, 2).GetProperty("wrote")[0].GetBoolean());
        Assert.False(V(h, 2).GetProperty("wrote")[1].GetBoolean());
    }

    [Fact]
    public void A_lie_can_be_rewritten_until_the_phase_ends()
    {
        var h = Table(2);
        Until(h, Bluff.PhaseWrite);
        Lie(h, 0, "перша брехня");
        Lie(h, 0, "друга брехня");
        Assert.Equal("друга брехня", V(h, 0).GetProperty("my").GetProperty("lie").GetString());
        Lie(h, 1);
        Until(h, Bluff.PhasePick);
        Assert.Equal(-1, CardOf(h, "перша брехня"));
        Assert.True(CardOf(h, "друга брехня") >= 0);
        Assert.False(h.Act(0, "lie", new { text = "третя" }).Ok);
    }

    [Fact]
    public void An_empty_lie_and_a_too_long_lie_are_refused_and_change_nothing()
    {
        var h = Table(2);
        Until(h, Bluff.PhaseWrite);
        Lie(h, 0, "стара");
        var before = Views.Text(h.View(0));
        foreach (var (payload, why) in new (object, string)[]
        {
            (new { text = "   \u200b " }, "Порожня брехня нікого не надурить"),
            (new { text = "" }, "Порожня брехня нікого не надурить"),
            (new { }, "Порожня брехня нікого не надурить"),
            (new { text = new string('я', Bluff.MaxLie + 1) }, "Коротше — до 40 знаків"),
        })
        {
            var r = h.Act(0, "lie", payload);
            Assert.False(r.Ok);
            Assert.Equal(why, r.Message);
        }
        Assert.Equal(before, Views.Text(h.View(0)));
        Assert.True(h.Act(0, "lie", new { text = new string('я', Bluff.MaxLie) }).Ok);   // рівно 40 — можна
    }

    [Fact]
    public void The_truth_written_by_accident_is_refused_with_the_friendly_text()
    {
        var h = Table(2);
        Until(h, Bluff.PhaseWrite);
        var q = Question(h);
        var before = Views.Text(h.View(0));
        foreach (var text in new[] { q.Answer, q.Answer.ToUpperInvariant() + "!", "звісно ж " + q.Answer, q.Accept[0] })
        {
            var r = h.Act(0, "lie", new { text });
            Assert.False(r.Ok, text);
            Assert.Equal(Bluff.Truthy, r.Message);
        }
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    [Fact]
    public void A_lie_outside_the_write_phase_is_refused()
    {
        var h = Table(2);
        Assert.Equal(Bluff.PhaseRead, Phase(h));
        Assert.Equal("Зараз не час брехати", h.Act(0, "lie", new { text = "рано" }).Message);
        WriteAndPick(h, 0, 1);
        Assert.Equal("Зараз не час брехати", h.Act(0, "lie", new { text = "пізно" }).Message);
        Assert.Equal("Зараз не час брехати", h.Act(0, "lie", new { auto = true }).Message);
    }

    [Fact]
    public void Hlek_lends_a_decoy_as_an_auto_lie_and_it_earns_points_like_any_lie()
    {
        var h = Table(2);
        Until(h, Bluff.PhaseWrite);
        var q = Question(h);
        var r = h.Act(0, "lie", new { auto = true });
        Assert.True(r.Ok);
        Assert.Equal($"Глек підказав: «{q.Decoys[0]}». Можеш переписати", r.Message);
        var my = V(h, 0).GetProperty("my");
        Assert.True(my.GetProperty("auto").GetBoolean());
        Assert.Equal(q.Decoys[0], my.GetProperty("lie").GetString());
        Lie(h, 1);
        Until(h, Bluff.PhasePick);

        Assert.Equal(5, Cards(h).GetArrayLength());           // 2 брехні + правда + 2 решта заготовок
        var borrowed = CardOf(h, q.Decoys[0]);
        Assert.True(Cards(h, 0)[borrowed].GetProperty("mine").GetBoolean());
        Assert.Equal(1, Cards(h).EnumerateArray().Count(c => c.GetProperty("text").GetString() == q.Decoys[0]));
        PickCard(h, 1, borrowed);
        PickCard(h, 0, TruthCard(h));
        Until(h, Bluff.PhaseScore);
        Assert.Equal(Bluff.TruthPts + Bluff.FooledPts, Score(h, 0));
        Assert.Equal(0, Score(h, 1));
        var card = Cards(h)[borrowed];
        Assert.Equal([0], Ints(card.GetProperty("by")));
        Assert.False(card.GetProperty("decoy").GetBoolean());
    }

    [Fact]
    public void Pressing_the_dice_again_gives_the_next_decoy_but_only_two_per_player()
    {
        // Утрьох: брехні трьох і правда — вже чотири картки, тож стіл заготовок не потребує (на двох — див. MinTable).
        var h = Table(3);
        Until(h, Bluff.PhaseWrite);
        var q = Question(h);
        string Mine() => V(h, 0).GetProperty("my").GetProperty("lie").GetString()!;
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal(q.Decoys[0], Mine());
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal(q.Decoys[1], Mine());
        // Третьої Глек не показує (Bluff.DiceSeen = 2): вона ще може лягти на стіл, а ти мав би її знати.
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal(q.Decoys[0], Mine());
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal(q.Decoys[1], Mine());
        Lie(h, 0, "своя");                                      // переписав сам — уже не від Глека
        Assert.False(V(h, 0).GetProperty("my").GetProperty("auto").GetBoolean());
    }

    [Fact]
    public void When_decoys_run_out_the_auto_lie_is_refused()
    {
        var h = Table(4);
        Until(h, Bluff.PhaseWrite);
        var q = Question(h);
        Assert.Equal(3, q.Decoys.Count);
        var got = new HashSet<string>();
        for (var s = 0; s < 3; s++)
        {
            Assert.True(h.Act(s, "lie", new { auto = true }).Ok);
            got.Add(V(h, s).GetProperty("my").GetProperty("lie").GetString()!);
        }
        Assert.Equal(3, got.Count);                             // кожному своя
        var r = h.Act(3, "lie", new { auto = true });
        Assert.False(r.Ok);
        Assert.Equal("Глек уже все вибрехав — пиши сам 🙂", r.Message);
        Assert.Equal(JsonValueKind.Null, V(h, 3).GetProperty("my").GetProperty("lie").ValueKind);
    }

    [Fact]
    public void Someone_who_never_wrote_still_picks_and_likes_but_has_no_card()
    {
        var h = Table(3);
        Write(h, 0, 1);
        Until(h, Bluff.PhasePick);                              // таймер, бо третій так і не написав
        Assert.Equal(5, Cards(h).GetArrayLength());
        Assert.All(Cards(h, 2).EnumerateArray(), c => Assert.False(c.GetProperty("mine").GetBoolean()));
        var olya = CardOf(h, Lies[0]);
        PickCard(h, 2, olya);
        PickCard(h, 1, olya);
        PickCard(h, 0, TruthCard(h));
        UntilOpen(h, olya);
        Assert.True(h.Act(2, "like", new { i = olya }).Ok);
        Until(h, Bluff.PhaseScore);
        Assert.Equal(Bluff.TruthPts + 2 * Bluff.FooledPts + Bluff.LikePts, Score(h, 0));
        Assert.Equal(2, V(h).GetProperty("victims")[0].GetInt32());
        Assert.Equal(0, Score(h, 2));
    }

    // =====================================================================================
    // картки
    // =====================================================================================

    [Fact]
    public void Two_players_get_five_cards_with_two_decoys()
    {
        var h = Table(2);
        WriteAndPick(h, 0, 1);
        Assert.Equal(Bluff.MinOptions, Cards(h).GetArrayLength());
        Until(h, Bluff.PhaseScore);
        var cards = Cards(h).EnumerateArray().ToList();
        Assert.Equal(2, cards.Count(c => c.GetProperty("decoy").GetBoolean()));
        Assert.Single(cards, c => c.GetProperty("truth").GetBoolean());
        Assert.Equal(2, cards.Count(c => c.GetProperty("by").GetArrayLength() > 0));
    }

    [Fact]
    public void Five_players_who_all_wrote_get_no_decoys_and_three_get_one()
    {
        var five = Table(5);
        WriteAndPick(five, 0, 1, 2, 3, 4);
        Assert.Equal(6, Cards(five).GetArrayLength());
        Until(five, Bluff.PhaseScore);
        Assert.DoesNotContain(Cards(five).EnumerateArray(), c => c.GetProperty("decoy").GetBoolean());

        var three = Table(3);
        WriteAndPick(three, 0, 1, 2);
        Assert.Equal(5, Cards(three).GetArrayLength());
        Until(three, Bluff.PhaseScore);
        Assert.Single(Cards(three).EnumerateArray(), c => c.GetProperty("decoy").GetBoolean());
    }

    [Fact]
    public void Identical_lies_merge_into_one_card_with_both_authors_and_neither_can_pick_it()
    {
        var h = Table(3);
        Until(h, Bluff.PhaseWrite);
        Lie(h, 0, "Бамбарбія");
        Lie(h, 1, "бамбарбія!");
        Lie(h, 2, "кергуду");
        Until(h, Bluff.PhasePick);
        Assert.Equal(5, Cards(h).GetArrayLength());             // 2 брехні + правда + 2 заготовки
        var shared = CardOf(h, "Бамбарбія");
        Assert.Equal("Бамбарбія", Cards(h)[shared].GetProperty("text").GetString());   // перший автор
        Assert.True(Cards(h, 0)[shared].GetProperty("mine").GetBoolean());
        Assert.True(Cards(h, 1)[shared].GetProperty("mine").GetBoolean());
        Assert.False(Cards(h, 2)[shared].GetProperty("mine").GetBoolean());
        Assert.Equal("Свою брехню обирати не можна 🙂", h.Act(0, "pick", new { i = shared }).Message);
        Assert.Equal("Свою брехню обирати не можна 🙂", h.Act(1, "pick", new { i = shared }).Message);
        PickCard(h, 2, shared);
        PickCard(h, 0, TruthCard(h));
        PickCard(h, 1, TruthCard(h));
        Until(h, Bluff.PhaseScore);
        Assert.Equal([0, 1], Ints(Cards(h)[shared].GetProperty("by")));
        Assert.Equal(Bluff.TruthPts + Bluff.FooledPts, Score(h, 0));
        Assert.Equal(Bluff.TruthPts + Bluff.FooledPts, Score(h, 1));
        Assert.Equal(0, Score(h, 2));
    }

    [Fact]
    public void A_decoy_that_matches_a_players_lie_is_skipped()
    {
        var h = Table(2);
        Until(h, Bluff.PhaseWrite);
        var q = Question(h);
        Lie(h, 0, q.Decoys[0]);                                 // руками, не через 🎲
        Lie(h, 1);
        Until(h, Bluff.PhasePick);
        Assert.Equal(5, Cards(h).GetArrayLength());
        Assert.Equal(1, Cards(h).EnumerateArray().Count(c => BluffText.LooksSame(c.GetProperty("text").GetString(), q.Decoys[0])));
        Assert.True(CardOf(h, q.Decoys[1]) >= 0 && CardOf(h, q.Decoys[2]) >= 0);
        Until(h, Bluff.PhaseScore);
        var card = Cards(h)[CardOf(h, q.Decoys[0])];
        Assert.Equal([0], Ints(card.GetProperty("by")));
        Assert.False(card.GetProperty("decoy").GetBoolean());
    }

    [Fact]
    public void Card_order_is_the_same_for_every_seat_and_the_watcher()
    {
        var h = Table(3);
        WriteAndPick(h, 0, 1, 2);
        static string Texts(JsonElement cards) => string.Join("|", cards.EnumerateArray().Select(c => c.GetProperty("text").GetString()));
        var watcher = Texts(Cards(h, null));
        for (var s = 0; s < 3; s++) Assert.Equal(watcher, Texts(Cards(h, s)));
        Assert.Equal(Enumerable.Range(0, 5), Cards(h).EnumerateArray().Select(c => c.GetProperty("i").GetInt32()));
    }

    [Fact]
    public void The_same_seed_and_moves_give_the_same_cards_and_the_same_views()
    {
        static string Play(int seed)
        {
            var h = Table(3, seed);
            var log = new List<string>();
            for (var q = 0; q < 2; q++)
            {
                WriteAndPick(h, 0, 1, 2);
                log.Add(Views.Text(h.View(null)));
                PickCard(h, 0, TruthCard(h));
                PickCard(h, 1, CardOf(h, Lies[2]));
                PickCard(h, 2, CardOf(h, Lies[0]));
                Until(h, Bluff.PhaseScore);
                log.Add(Views.Text(h.View(0)));
                log.Add(Views.Text(h.View(null)));
                NextQuestion(h);
            }
            return string.Join("\n", log);
        }

        var a = Play(7);
        Assert.Equal(a, Play(7));
        Assert.Contains(Enumerable.Range(8, 10), s => Play(s) != a);   // сід таки щось вирішує
    }

    // =====================================================================================
    // вибір і розкриття
    // =====================================================================================

    [Fact]
    public void Picking_your_own_card_is_refused()
    {
        var h = Table(2);
        WriteAndPick(h, 0, 1);
        var before = Views.Text(h.View(0));
        var r = h.Act(0, "pick", new { i = CardOf(h, Lies[0]) });
        Assert.False(r.Ok);
        Assert.Equal("Свою брехню обирати не можна 🙂", r.Message);
        Assert.Equal(before, Views.Text(h.View(0)));
    }

    [Fact]
    public void A_pick_can_be_changed_and_an_out_of_range_pick_is_refused()
    {
        var h = Table(2);
        WriteAndPick(h, 0, 1);
        var peter = CardOf(h, Lies[1]);
        var truth = TruthCard(h);
        PickCard(h, 0, peter);
        PickCard(h, 0, truth);
        Assert.Equal(truth, V(h, 0).GetProperty("my").GetProperty("pick").GetInt32());
        var before = Views.Text(h.View(0));
        foreach (var bad in new object[] { new { i = 99 }, new { i = -1 }, new { i = "три" }, new { }, "x" })
            Assert.Equal("Нема такої картки", h.Act(0, "pick", bad).Message);
        Assert.Equal(before, Views.Text(h.View(0)));
        Assert.Equal(JsonValueKind.Null, V(h, 1).GetProperty("my").GetProperty("pick").ValueKind);
        Assert.True(V(h, 1).GetProperty("picked")[0].GetBoolean());
    }

    [Fact]
    public void Truth_pickers_get_a_thousand_each()
    {
        var h = Table(3);
        WriteAndPick(h, 0, 1, 2);
        for (var s = 0; s < 3; s++) PickCard(h, s, TruthCard(h));
        Until(h, Bluff.PhaseScore);
        for (var s = 0; s < 3; s++)
        {
            Assert.Equal(Bluff.TruthPts, Score(h, s));
            Assert.Equal(Bluff.TruthPts, V(h).GetProperty("delta")[s].GetInt64());
        }
    }

    [Fact]
    public void A_liar_gets_five_hundred_per_victim_and_co_authors_each_get_the_full_amount()
    {
        var h = Table(3);
        Until(h, Bluff.PhaseWrite);
        Lie(h, 0, "Бамбарбія");
        Lie(h, 1, "Бамбарбія");
        Lie(h, 2, "кергуду");
        Until(h, Bluff.PhasePick);
        PickCard(h, 2, CardOf(h, "Бамбарбія"));
        PickCard(h, 0, CardOf(h, "кергуду"));
        PickCard(h, 1, CardOf(h, "кергуду"));
        Until(h, Bluff.PhaseScore);
        Assert.Equal(Bluff.FooledPts, Score(h, 0));
        Assert.Equal(Bluff.FooledPts, Score(h, 1));
        Assert.Equal(2 * Bluff.FooledPts, Score(h, 2));
        Assert.Equal([1, 1, 2], Ints(V(h).GetProperty("victims")).Take(3));
    }

    [Fact]
    public void Victims_of_a_decoy_pay_nobody()
    {
        var h = Table(2);
        var q = WriteAndPick(h, 0, 1);
        var decoys = q.Decoys.Select(d => CardOf(h, d)).Where(i => i >= 0).ToList();
        Assert.Equal(2, decoys.Count);
        PickCard(h, 0, decoys[0]);
        PickCard(h, 1, decoys[1]);
        Until(h, Bluff.PhaseScore);
        Assert.Equal(0, Score(h, 0));
        Assert.Equal(0, Score(h, 1));
        Assert.All(decoys, i => Assert.Empty(Cards(h)[i].GetProperty("by").EnumerateArray()));
    }

    [Fact]
    public void The_final_question_doubles_truth_and_fooling_but_not_likes()
    {
        var h = Table(2, options: new { questions = "5" });
        for (var q = 0; q < 4; q++)
        {
            Assert.False(V(h).GetProperty("final").GetBoolean());
            NextQuestion(h);
        }
        Assert.Equal(5, QNo(h));
        Assert.True(V(h).GetProperty("final").GetBoolean());
        WriteAndPick(h, 0, 1);
        var olya = CardOf(h, Lies[0]);
        PickCard(h, 0, TruthCard(h));
        PickCard(h, 1, olya);
        UntilOpen(h, olya);
        Assert.True(h.Act(1, "like", new { i = olya }).Ok);
        PlayOut(h);
        const int expected = Bluff.TruthPts * Bluff.FinalMult + Bluff.FooledPts * Bluff.FinalMult + Bluff.LikePts;
        Assert.Equal(expected, h.Finished.Single().Result.Scores![0]);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
    }

    /// <summary>Чотири брехні на чотирьох: 5 карток без заготовок. Вибір дає картки з 0, 0, 1, 1 і 2 голосами.</summary>
    static (RoomHarness H, int[] Card) FourWithVotes()
    {
        var h = Table(4);
        WriteAndPick(h, 0, 1, 2, 3);
        var card = new[] { CardOf(h, Lies[0]), CardOf(h, Lies[1]), CardOf(h, Lies[2]), CardOf(h, Lies[3]), TruthCard(h) };
        PickCard(h, 0, card[1]);
        PickCard(h, 2, card[1]);
        PickCard(h, 1, card[3]);
        PickCard(h, 3, card[4]);
        return (h, card);
    }

    [Fact]
    public void Cards_are_revealed_from_least_to_most_picked_with_the_truth_last()
    {
        var (h, card) = FourWithVotes();
        Until(h, Bluff.PhaseScore);
        var zero = new[] { card[0], card[2] }.Order().ToArray();
        Assert.Equal([.. zero, card[3], card[1], card[4]], Revealed(h));
    }

    [Fact]
    public void Reveal_steps_last_three_seconds_when_picked_one_and_a_half_when_not_and_four_for_the_truth()
    {
        var (h, card) = FourWithVotes();
        Until(h, Bluff.PhaseReveal);
        var steps = new List<(int Card, int Ms)>();
        while (Playing(h) && Phase(h) == Bluff.PhaseReveal)
        {
            var open = Revealed(h);
            if (steps.Count < open.Length) steps.Add((open[^1], PhaseMs(h)));
            h.Tick();
        }
        Assert.Equal(5, steps.Count);
        foreach (var (c, ms) in steps)
            Assert.Equal(c == card[4] ? Bluff.StepTruthMs : c == card[1] || c == card[3] ? Bluff.StepPickedMs : Bluff.StepEmptyMs, ms);
        Assert.Equal(Bluff.PhaseScore, Phase(h));
        Assert.Equal(Bluff.ScoreMs, PhaseMs(h));
    }

    [Fact]
    public void The_truth_step_brings_the_note_and_a_word_from_hlek()
    {
        var (h, card) = FourWithVotes();
        UntilOpen(h, card[1]);
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("note").ValueKind);
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("quip").ValueKind);
        UntilOpen(h, card[4]);
        var q = Question(h);
        if (q.Note.Length > 0) Assert.Equal(q.Note, V(h).GetProperty("note").GetString());
        Assert.Contains(V(h).GetProperty("quip").GetString(), Bluff.Quips);
    }

    [Fact]
    public void A_like_adds_a_hundred_to_each_author_and_a_second_like_takes_it_back()
    {
        var h = Table(3);
        Until(h, Bluff.PhaseWrite);
        Lie(h, 0, "Бамбарбія");
        Lie(h, 1, "Бамбарбія");
        Lie(h, 2);
        Until(h, Bluff.PhasePick);
        var shared = CardOf(h, "Бамбарбія");
        UntilOpen(h, shared);
        Assert.True(h.Act(2, "like", new { i = shared }).Ok);
        h.Tick();
        Assert.Equal(Bluff.LikePts, Score(h, 0));
        Assert.Equal(Bluff.LikePts, Score(h, 1));
        Assert.Equal(Bluff.LikePts, V(h).GetProperty("likeDelta")[1].GetInt64());
        Assert.Equal(1, Cards(h)[shared].GetProperty("likes").GetInt32());
        Assert.Equal([shared], Ints(V(h, 2).GetProperty("my").GetProperty("likes")));
        Assert.True(h.Act(2, "like", new { i = shared }).Ok);
        Assert.Equal(0, Score(h, 0));
        Assert.Equal(0, Cards(h)[shared].GetProperty("likes").GetInt32());
        Assert.Empty(V(h, 2).GetProperty("my").GetProperty("likes").EnumerateArray());
        Assert.Equal(0, V(h).GetProperty("delta")[0].GetInt64());   // ❤ — окремо від очок питання
    }

    [Fact]
    public void Likes_are_refused_before_the_card_is_revealed_on_own_cards_on_the_truth_and_after_the_score_phase()
    {
        var h = Table(2);
        var q = WriteAndPick(h, 0, 1);
        var olya = CardOf(h, Lies[0]);
        var truth = TruthCard(h);
        Assert.Equal("❤ ставлять на розкритті", h.Act(1, "like", new { i = olya }).Message);
        PickCard(h, 1, olya);
        PickCard(h, 0, truth);
        Until(h, Bluff.PhaseReveal);
        if (!Revealed(h).Contains(truth))
            Assert.Equal("Цього ще не показували", h.Act(1, "like", new { i = truth }).Message);
        UntilOpen(h, olya);
        Assert.Equal("Собі ❤ не ставлять 🙂", h.Act(0, "like", new { i = olya }).Message);
        Assert.Equal("Нема такої картки", h.Act(1, "like", new { i = 42 }).Message);
        Until(h, Bluff.PhaseScore);
        Assert.Equal("❤ ставлять брехням гравців", h.Act(0, "like", new { i = truth }).Message);
        Assert.Equal("❤ ставлять брехням гравців", h.Act(0, "like", new { i = CardOf(h, q.Decoys.First(d => CardOf(h, d) >= 0)) }).Message);
        Assert.True(h.Act(1, "like", new { i = olya }).Ok);    // у рахунку ще можна
        NextQuestion(h);
        Assert.Equal("❤ ставлять на розкритті", h.Act(1, "like", new { i = olya }).Message);
    }

    // =====================================================================================
    // приховане
    // =====================================================================================

    [Fact]
    public void During_write_no_other_view_and_no_frame_contains_my_lie()
    {
        var h = Table(3);
        Until(h, Bluff.PhaseWrite);
        const string secret = "Таємниця Олі";
        Lie(h, 0, secret);
        var n = h.Outbox.Count;
        h.Tick();
        Assert.Contains(secret, RawView(h, 0));
        Assert.DoesNotContain(secret, RawView(h, 1));
        Assert.DoesNotContain(secret, RawView(h, 2));
        Assert.DoesNotContain(secret, RawView(h, null));
        Assert.DoesNotContain(secret, Frame(h));
        Assert.Empty(h.Outbox.Skip(n).OfType<RoomFrame>());          // кадру нема зовсім — модуль його не читає
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), j => j.Text.Contains(secret));
    }

    [Fact]
    public void During_pick_nobody_sees_authors_picks_truth_or_decoy_flags()
    {
        var h = Table(3);
        WriteAndPick(h, 0, 1, 2);
        PickCard(h, 0, TruthCard(h));
        h.Tick();
        foreach (var seat in new int?[] { 0, 1, 2, null })
            Assert.All(Cards(h, seat).EnumerateArray(), c =>
            {
                foreach (var name in new[] { "by", "picks", "truth", "decoy" })
                    Assert.Equal(JsonValueKind.Null, c.GetProperty(name).ValueKind);
                Assert.Equal(0, c.GetProperty("likes").GetInt32());
            });
        Assert.Equal(JsonValueKind.Null, V(h, 1).GetProperty("my").GetProperty("pick").ValueKind);
        Assert.True(V(h, 1).GetProperty("picked")[0].GetBoolean());
        Assert.DoesNotContain("\"pick\":" + TruthCard(h), RawView(h, 1));
    }

    [Fact]
    public void During_pick_mine_is_true_only_in_the_authors_view()
    {
        var h = Table(3);
        WriteAndPick(h, 0, 1, 2);
        for (var author = 0; author < 3; author++)
        {
            var i = CardOf(h, Lies[author]);
            foreach (var seat in new int?[] { 0, 1, 2, null })
                Assert.Equal(seat == author, Cards(h, seat)[i].GetProperty("mine").GetBoolean());
        }
    }

    [Fact]
    public void During_reveal_unrevealed_cards_stay_null_including_the_truth_flag()
    {
        var (h, card) = FourWithVotes();
        Until(h, Bluff.PhaseReveal);
        var open = Revealed(h);
        Assert.Single(open);
        foreach (var seat in new int?[] { 0, 3, null })
        {
            var cards = Cards(h, seat);
            for (var i = 0; i < cards.GetArrayLength(); i++)
            {
                var c = cards[i];
                if (open.Contains(i))
                {
                    Assert.False(c.GetProperty("truth").GetBoolean());
                    Assert.Equal(JsonValueKind.Array, c.GetProperty("by").ValueKind);
                }
                else
                    foreach (var name in new[] { "by", "picks", "truth", "decoy" })
                        Assert.Equal(JsonValueKind.Null, c.GetProperty(name).ValueKind);
            }
        }
        Assert.DoesNotContain("\"truth\":true", RawView(h, null));
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("note").ValueKind);
        _ = card;
    }

    [Fact]
    public void The_watcher_view_has_no_my_and_matches_the_spec_shape()
    {
        var h = Table(3);
        WriteAndPick(h, 0, 1, 2);
        var v = V(h, null);
        Assert.Equal(
            ["phase", "q", "of", "final", "endsAt", "phaseMs", "cat", "catLabel", "text", "nicks", "present", "wrote", "picked", "my",
             "options", "revealed", "note", "quip", "scores", "delta", "truthDelta", "likeDelta", "victims", "result"],
            v.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("my").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(Bluff.Seats, v.GetProperty("nicks").GetArrayLength());
        Assert.Equal("Оля", v.GetProperty("nicks")[0].GetString());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("nicks")[5].ValueKind);
        Assert.Equal(Bluff.Seats, v.GetProperty("scores").GetArrayLength());
        Assert.True(DateTimeOffset.TryParse(v.GetProperty("endsAt").GetString(), out _));
        Assert.Equal(["i", "text", "mine", "by", "picks", "truth", "decoy", "likes"],
            v.GetProperty("options")[0].EnumerateObject().Select(p => p.Name));
        Assert.All(v.GetProperty("options").EnumerateArray(), c => Assert.False(c.GetProperty("mine").GetBoolean()));
        Assert.Equal(["lie", "auto", "pick", "likes"], V(h, 0).GetProperty("my").EnumerateObject().Select(p => p.Name));
        Assert.Equal(TestBank.First(q => q.Q == v.GetProperty("text").GetString()).Cat, v.GetProperty("cat").GetString());
        Assert.Equal(BluffCats.Label(v.GetProperty("cat").GetString()), v.GetProperty("catLabel").GetString());
    }

    [Fact]
    public void Views_stay_small_on_eight_players_with_long_lies()
    {
        var h = Table(8, options: new { questions = "10" });
        var longest = 0;
        var lies = Lies.Select(l => (l + " і ще трохи вигадки").PadRight(Bluff.MaxLie, 'ю')[..Bluff.MaxLie]).ToArray();
        for (var q = 0; q < 10; q++)
        {
            Until(h, Bluff.PhaseWrite);
            for (var s = 0; s < 8; s++) Lie(h, s, lies[s]);
            Until(h, Bluff.PhasePick);
            longest = Math.Max(longest, Views.Text(h.View(0)).Length);
            for (var s = 0; s < 8; s++) PickCard(h, s, CardOf(h, lies[(s + 1) % 8]));
            Until(h, Bluff.PhaseReveal);
            while (Playing(h) && Phase(h) == Bluff.PhaseReveal)
            {
                longest = Math.Max(longest, Views.Text(h.View(0)).Length);
                h.Tick();
            }
            NextQuestion(h);
        }
        var done = Views.Text(h.View(0)).Length;
        output.WriteLine($"вид на 8 гравцях з брехнями по 40 знаків: найбільший під час гри {longest} Б, у кінці з підсумком {done} Б");
        // Кирилиця на дроті — \uXXXX (6 байт на літеру), тож 8 × 40 знаків самі по собі ≈ 2 КБ.
        Assert.True(longest <= 5 * 1024, $"вид {longest} Б");
        Assert.True(done <= 12 * 1024, $"підсумок {done} Б");
    }

    // =====================================================================================
    // кінець, вихід, рематч
    // =====================================================================================

    [Fact]
    public void Seven_questions_end_with_the_top_scorer_winning_and_scores_in_the_event()
    {
        var h = Table(3);
        var played = 0;
        while (Playing(h))
        {
            WriteAndPick(h, 0, 1, 2);
            PickCard(h, 0, TruthCard(h));
            played++;
            NextQuestion(h);
        }
        Assert.Equal(Bluff.DefaultQuestions, played);
        var result = h.Finished.Single().Result;
        Assert.Equal([0], result.Winners);
        Assert.False(result.Draw);
        const long olya = (Bluff.DefaultQuestions - 1) * Bluff.TruthPts + Bluff.TruthPts * Bluff.FinalMult;
        Assert.Equal(olya, result.Scores![0]);
        Assert.Equal([0, 1, 2], result.Scores.Keys.Order());
        Assert.Equal(Bluff.PhaseDone, Phase(h));
        Assert.Equal("", V(h).GetProperty("text").GetString());
        Assert.Equal([0], Ints(V(h).GetProperty("result").GetProperty("winners")));
        Assert.False(V(h).GetProperty("result").GetProperty("left").GetBoolean());
    }

    [Fact]
    public void Equal_scores_give_several_winners_and_all_zeros_give_a_draw()
    {
        var h = Table(2, options: new { questions = "5" });
        WriteAndPick(h, 0, 1);
        PickCard(h, 0, TruthCard(h));
        PickCard(h, 1, TruthCard(h));
        PlayOut(h);
        Assert.Equal([0, 1], h.Finished.Single().Result.Winners);

        var idle = Table(2, options: new { questions = "5" });
        PlayOut(idle);
        Assert.True(idle.Finished.Single().Result.Draw);
        Assert.Empty(idle.Finished.Single().Result.Winners);
        Assert.EndsWith("· нікого так і не надурили", idle.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void The_journal_line_names_the_best_lie_and_its_victims()
    {
        var h = Table(3, options: new { questions = "5" });
        WriteAndPick(h, 0, 1, 2);
        PickCard(h, 1, CardOf(h, Lies[0]));
        PickCard(h, 2, CardOf(h, Lies[0]));
        NextQuestion(h);
        PlayOut(h);
        Assert.Equal("Байкарі: Оля 1 000, Петро 0, Ганна 0 · найкраща брехня — Оля, 2 жертви",
            h.Outbox.OfType<Journal>().Last().Text);
        var best = V(h).GetProperty("result").GetProperty("best");
        Assert.Equal(Lies[0], best.GetProperty("text").GetString());
        Assert.Equal(2, best.GetProperty("victims").GetInt32());
        Assert.Equal(1, best.GetProperty("q").GetInt32());

        // Спільна брехня — обидва автори через «і».
        var co = Table(3, options: new { questions = "5" });
        Until(co, Bluff.PhaseWrite);
        Lie(co, 0, "Бамбарбія");
        Lie(co, 1, "бамбарбія");
        Lie(co, 2);
        Until(co, Bluff.PhasePick);
        PickCard(co, 2, CardOf(co, "Бамбарбія"));
        PlayOut(co);
        Assert.EndsWith("найкраща брехня — Оля і Петро, 1 жертва", co.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Victims_are_counted_in_ukrainian_and_numbers_get_spaces()
    {
        Assert.Equal("1 жертва", Bluff.Victims(1));
        Assert.Equal("2 жертви", Bluff.Victims(2));
        Assert.Equal("4 жертви", Bluff.Victims(4));
        Assert.Equal("5 жертв", Bluff.Victims(5));
        Assert.Equal("11 жертв", Bluff.Victims(11));
        Assert.Equal("21 жертва", Bluff.Victims(21));
        Assert.Equal("6 500", Bluff.Num(6500));
        Assert.Equal("0", Bluff.Num(0));
        Assert.Equal("12 300", Bluff.Num(12_300));
    }

    [Fact]
    public void The_recap_lists_every_question_with_its_truth()
    {
        var h = Table(2, options: new { questions = "5" });
        var asked = new List<string>();
        while (Playing(h))
        {
            Until(h, Bluff.PhaseWrite);
            if (!Playing(h)) break;
            asked.Add(V(h).GetProperty("text").GetString()!);
            NextQuestion(h);
        }
        var recap = V(h).GetProperty("result").GetProperty("recap");
        Assert.Equal(5, recap.GetArrayLength());
        for (var n = 0; n < 5; n++)
        {
            var r = recap[n];
            var q = TestBank.First(x => x.Q == asked[n]);
            Assert.Equal(n + 1, r.GetProperty("q").GetInt32());
            Assert.Equal(q.Q, r.GetProperty("text").GetString());
            Assert.Equal(q.Answer, r.GetProperty("answer").GetString());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("best").ValueKind);   // ніхто нікого не надурив
            if (q.Note.Length == 0) Assert.Equal(JsonValueKind.Null, r.GetProperty("note").ValueKind);
        }
    }

    [Fact]
    public void One_of_three_leaving_keeps_the_party_going_and_leaves_their_card_on_the_table()
    {
        var h = Table(3);
        WriteAndPick(h, 0, 1, 2);
        var hanna = CardOf(h, Lies[2]);
        PickCard(h, 0, hanna);
        Assert.True(h.Leave("Ганна").Ok);
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.False(V(h).GetProperty("present")[2].GetBoolean());
        Assert.Equal("Ганна", V(h).GetProperty("nicks")[2].GetString());
        PickCard(h, 1, TruthCard(h));
        h.Tick();
        Assert.Equal(Bluff.PhaseReveal, Phase(h));             // вибору чекали лише від тих, хто лишився
        Until(h, Bluff.PhaseScore);
        Assert.Equal([2], Ints(Cards(h)[hanna].GetProperty("by")));
        Assert.Equal([0], Ints(Cards(h)[hanna].GetProperty("picks")));
        Assert.Equal(0, Score(h, 2));                          // очки тому, хто пішов, не йдуть
        NextQuestion(h);
        Assert.Equal(2, QNo(h));
        Write(h, 0, 1);
        h.Tick();
        Assert.Equal(Bluff.PhasePick, Phase(h));               // брехні чекали лише від двох
    }

    [Fact]
    public void The_second_of_two_leaving_ends_the_party_in_a_draw()
    {
        var h = Table(2);
        Write(h, 0);
        Assert.True(h.Leave("Петро").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var result = h.Finished.Single().Result;
        Assert.True(result.Draw);
        Assert.Empty(result.Winners);
        Assert.Equal("Байкарі: гравці розійшлись, партію не дограли", result.Text);
        Assert.Equal(Bluff.PhaseDone, Phase(h));
        Assert.True(V(h).GetProperty("result").GetProperty("left").GetBoolean());   // клієнт пише «розійшлись», а не «ніхто не переграв»
    }

    [Fact]
    public void A_leaver_earns_nothing_and_cannot_win()
    {
        var h = Table(3, options: new { questions = "5" });
        WriteAndPick(h, 0, 1, 2);
        PickCard(h, 2, TruthCard(h));                          // Ганна вгадала
        PickCard(h, 1, CardOf(h, Lies[2]));                    // і надурила Петра
        Until(h, Bluff.PhaseScore);
        Assert.Equal(Bluff.TruthPts + Bluff.FooledPts, Score(h, 2));
        Assert.True(h.Leave("Ганна").Ok);
        NextQuestion(h);
        WriteAndPick(h, 0, 1);
        PickCard(h, 0, TruthCard(h));
        PlayOut(h);
        var result = h.Finished.Single().Result;
        Assert.Equal([0], result.Winners);
        Assert.False(result.Scores!.ContainsKey(2));
        // У рахунку її нема; а от її брехня лишилась найкращою в партії — картка ж стояла на столі.
        Assert.DoesNotContain("Ганна", result.Text.Split(" · ")[0]);
        Assert.EndsWith("найкраща брехня — Ганна, 1 жертва", result.Text);
    }

    [Fact]
    public async Task Rematch_gives_a_clean_state_and_marks_seen_questions()
    {
        using var temp = new TempDb();
        var h = Table(2, options: new { questions = "5" }, services: Services(temp.Db));
        var first = new List<string>();
        while (Playing(h))
        {
            first.Add(V(h).GetProperty("text").GetString()!);
            WriteAndPick(h, 0, 1);
            PickCard(h, 0, TruthCard(h));
            NextQuestion(h);
        }
        Assert.True(h.Rematch().Ok);
        var v = V(h);
        Assert.Equal(Bluff.PhaseRead, v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("q").GetInt32());
        Assert.All(v.GetProperty("scores").EnumerateArray(), s => Assert.Equal(0, s.GetInt64()));
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("options").ValueKind);
        Assert.Empty(v.GetProperty("revealed").EnumerateArray());
        // Місця обернулись: тепер на нульовому Петро.
        Assert.Equal("Петро", v.GetProperty("nicks")[0].GetString());

        await BluffSeen.Idle.WaitAsync(TimeSpan.FromSeconds(10));
        var rows = temp.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM bluff_seen";
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });
        Assert.True(rows >= 10, $"у bluff_seen {rows} рядків");         // 5 питань × 2 ніки (+ перше нової партії)

        // Банк на десять — друга партія бере п'ять інших.
        var second = new List<string>();
        while (Playing(h))
        {
            second.Add(V(h).GetProperty("text").GetString()!);
            NextQuestion(h);
        }
        Assert.Equal(5, second.Count);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void Without_a_database_the_memory_stays_quiet_and_questions_still_come()
    {
        var h = Table(2, options: new { questions = "10" });
        var seen = new HashSet<string>();
        while (Playing(h))
        {
            seen.Add(V(h).GetProperty("text").GetString()!);
            NextQuestion(h);
        }
        Assert.Equal(10, seen.Count);                          // без повторів у межах партії
    }

    [Fact]
    public void A_rematch_at_the_same_table_brings_fresh_questions_even_before_the_database_catches_up()
    {
        // Без бази взагалі: стіл пам'ятає свої позначки сам, тож «Ще раз» не повторює щойно бачене, а запис у базу
        // (фоном) може й не встигнути — перша партія закінчується за мить до рематчу.
        var h = Table(2, options: new { questions = "5" });
        var first = new List<string>();
        while (Playing(h))
        {
            first.Add(V(h).GetProperty("text").GetString()!);
            NextQuestion(h);
        }
        Assert.True(h.Rematch().Ok);
        var second = new List<string>();
        while (Playing(h))
        {
            second.Add(V(h).GetProperty("text").GetString()!);
            NextQuestion(h);
        }
        Assert.Equal(5, second.Count);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void Fox_and_nose_achievements_are_requested_once_and_only_when_earned()
    {
        var h = Table(3, options: new { questions = "5" });
        while (Playing(h))
        {
            WriteAndPick(h, 0, 1, 2);
            PickCard(h, 0, TruthCard(h));                      // Оля щоразу вгадує
            PickCard(h, 1, CardOf(h, Lies[2]));                // Ганна щоразу дурить двох
            PickCard(h, 2, TruthCard(h));
            if (QNo(h) == 3) PickCard(h, 2, CardOf(h, Lies[1]));   // а Ганна раз схибила
            NextQuestion(h);
        }
        var fox = h.Awards.Where(a => a.Reason == "ach:bluff-fox").ToList();
        Assert.Empty(fox);                                      // лише одна жертва за раз — не лис

        var foxy = Table(3, options: new { questions = "5" });
        while (Playing(foxy))
        {
            WriteAndPick(foxy, 0, 1, 2);
            PickCard(foxy, 0, CardOf(foxy, Lies[2]));
            PickCard(foxy, 1, CardOf(foxy, Lies[2]));
            PickCard(foxy, 2, TruthCard(foxy));
            NextQuestion(foxy);
        }
        var got = foxy.Awards.Where(a => a.Reason == "ach:bluff-fox").ToList();
        Assert.Single(got);                                     // раз на партію, хоч дурила двох щоразу
        Assert.Equal("Ганна", got[0].Nick);
        Assert.Equal(0, got[0].Shards);
        Assert.Equal(["Ганна"], foxy.Awards.Where(a => a.Reason == "ach:bluff-nose").Select(a => a.Nick));
        Assert.Equal(["Оля"], h.Awards.Where(a => a.Reason == "ach:bluff-nose").Select(a => a.Nick));
    }

    [Fact]
    public void The_nose_needs_a_full_party_of_at_least_five_questions()
    {
        var h = Table(2, options: new { questions = "5", cat = "history,lang" });   // лише три питання
        while (Playing(h))
        {
            WriteAndPick(h, 0, 1);
            PickCard(h, 0, TruthCard(h));
            PickCard(h, 1, TruthCard(h));
            NextQuestion(h);
        }
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:bluff-nose");
    }

    // =====================================================================================
    // після рецензій: двійники правди, 🎲, перебір, рахунок того, хто встав, пам'ять, Журнал
    // =====================================================================================

    const string HydrogenJson = """
        [ { "id": "h1", "cat": "science", "q": "Найпоширеніший хімічний елемент у Всесвіті — ___", "answer": "водень",
            "accept": ["гідроген"], "decoys": ["залізо", "кисень", "кремній"], "note": "Три чверті речовини." } ]
        """;

    static IServiceProvider Bank(string json) => Services(bank: BluffBank.Parse(json));

    static string? MyLie(RoomHarness h, int seat) => V(h, seat).GetProperty("my").GetProperty("lie").GetString();

    static List<string> Texts(RoomHarness h) => [.. Cards(h).EnumerateArray().Select(c => c.GetProperty("text").GetString()!)];

    [Fact]
    public void An_uppercase_twin_of_the_truth_in_another_alphabet_never_reaches_the_table()
    {
        var h = Table(2, services: Bank(HydrogenJson));
        Until(h, Bluff.PhaseWrite);
        var before = Views.Text(h.View(0));
        // Латинські B, O, E, H і грецькі епсилон і ета на великій картці — те саме «ВОДЕНЬ»; невидима «літера» теж не рятує.
        foreach (var twin in new[] { "BOДEHЬ", "ВОД\u0395\u0397Ь", "в\u3164о\u3164день", "во\U000E0020день", "В0ДЕНЬ" })
        {
            var r = h.Act(0, "lie", new { text = twin });
            Assert.False(r.Ok, twin);
            Assert.Contains(r.Message, new[] { Bluff.Truthy, Bluff.MixedAbc });
        }
        Assert.Equal(before, Views.Text(h.View(0)));
        // Слово з двох абеток — навіть не правда — не приймаємо: на картці його не відрізнити від «чистого».
        Assert.Equal(Bluff.MixedAbc, h.Act(0, "lie", new { text = "кис\u0065нь" }).Message);
        Assert.True(h.Act(0, "lie", new { text = "iPhone-ом" }).Ok);   // різні абетки в різних словах — можна
    }

    /// <summary>
    /// Рецензія: «МАШИНОЮ», де М і А — черокі (U+13B7, U+13AA), сервер записав, а на картці це та сама правда «МАШИНОЮ». Літери інших
    /// письмен тепер не проходять узагалі, і «ти написав правду» на них не витрачається.
    /// </summary>
    [Fact]
    public void A_twin_in_cherokee_or_lisu_letters_is_refused_before_it_reaches_the_table()
    {
        var h = Table(2, services: Bank(HydrogenJson));
        Until(h, Bluff.PhaseWrite);
        var before = Views.Text(h.View(0));
        foreach (var twin in new[] { "\u13F4ОДЕНЬ", "ВОД\u13AC\u13BBЬ", "\uA4D0ОДЕНЬ", "\u1D0Fводень", "\u13F4\u13AC\u13BB" })
        {
            var r = h.Act(0, "lie", new { text = twin });
            Assert.False(r.Ok, twin);
            Assert.Equal(Bluff.ForeignAbc, r.Message);
        }
        Assert.Equal(before, Views.Text(h.View(0)));
        // П'ять відмов — а правду вгадувати досі можна тими самими спробами: «ти написав правду» не лічилось.
        Assert.Equal(Bluff.Truthy, h.Act(0, "lie", new { text = "водню" }).Message);
        Assert.True(h.Act(0, "lie", new { text = "Лісу — це народ" }).Ok);
    }

    /// <summary>
    /// Двійники на всьому справжньому банку: у кожній правді підміняємо літери двійниками з латиниці, грецької,
    /// черокі, лісу, капітелі, розширених латиниці й кирилиці, знаками (× ∏ € …) і цифрами (0, 3) — поодинці, парами серед
    /// перших трьох (так проходило сито одруківок) і всі разом. Жоден такий двійник не має лягти на стіл.
    /// </summary>
    [Fact]
    public void No_twin_of_any_truth_in_the_real_bank_is_accepted_as_a_lie()
    {
        string[][] groups =
        [
            ["А", "A", "\u0391", "\u13AA", "\uA4EE", "\u1D00", "\uFF21", "\U0001D400"],
            ["В", "B", "\u0392", "\u13F4", "\uA4D0", "\u0299", "\u03D0"],
            ["Е", "E", "\u0395", "\u13AC", "\uA4F0", "\u1D07", "\u212E"],
            ["Н", "H", "\u0397", "\u13BB", "\uA4E7", "\u029C", "\u04BA"],
            ["М", "M", "\u039C", "\u13B7", "\uA4DF", "\u1D0D", "\u216F"],
            ["І", "I", "\u0399", "\uA4F2", "\u0131", "|", "\u04C0", "\u01C0", "\u026A", "\u2160", "\u2502", "\u23D0", "\u05C0"],
            ["Т", "T", "\u03A4", "\u13A2", "\uA4D4", "\u1D1B", "\u22A4"],
            ["С", "C", "\u03F9", "\u13DF", "\uA4DA", "\u1D04", "\u216D", "\u00A2"],
            ["Р", "P", "\u03A1", "\u13E2", "\uA4D1", "\u1D18", "\u00FE"],
            ["К", "K", "\u039A", "\u13E6", "\uA4D7", "\u1D0B", "\u0138", "\u212A"],
            ["О", "O", "\u039F", "0", "\uA4F3", "\u1D0F", "\u0555", "\u2C9F", "\u1C82", "\u0966", "\u25EF", "\u25CB", "\u00B0", "\u2205"],
            ["Х", "X", "\u03A7", "\uA4EB", "\u00D7", "\u2715", "\u2573"],
            ["У", "Y", "\u03A5", "\uA4EC", "\u04AE", "\u03D2"],
            ["З", "3", "\u01B7", "\u04E0"],
            ["Л", "\u039B", "\u0245", "\u2227"],
            ["П", "\u03A0", "\u220F", "\u1D28"],
            ["Д", "\u0394", "\u2206"],
            ["Г", "\u0393", "\u1D26"],
            ["Ф", "\u03A6", "\u0278"],
            ["Б", "\u0182"],
            ["Ь", "\u0184", "\u0185"],
            ["Є", "\u20AC", "\u2208", "\u0190", "\u0510"],
            ["Ї", "\u00CF", "\u03AA"],
            ["Ш", "\u019C"],
            ["И", "\u0376"],
            ["L", "\u13DE", "\uA4E1", "\u216C"],
            ["D", "\u13A0", "\uA4D3", "\u0501"],
            ["N", "\u039D", "\uA4E0"],
            ["W", "\u13B3", "\uA4EA", "\u051C"],
            ["S", "\u0405", "\u13DA"],
            ["G", "\u13C0"],
            ["V", "\u13D9"],
            ["R", "\u13A1"],
            ["Z", "\u0396"],
            ["J", "\u0408"],
            ["F", "\u03DC"],
        ];
        var byLetter = new Dictionary<char, string[]>();
        foreach (var g in groups) byLetter[g[0][0]] = g;
        var columns = groups.Max(g => g.Length);
        string[]? Group(char ch) => byLetter.GetValueOrDefault(char.ToUpperInvariant(ch));

        var bank = BluffBank.All;
        var tried = 0;
        var leaks = new List<string>();
        void Try(BluffQuestion q, string twin)
        {
            tried++;
            if (Bluff.Refuse(BluffText.Clean(twin), q) is null) leaks.Add($"{q.Id}: «{q.Answer}» → «{twin}»");
        }
        foreach (var q in bank)
        {
            var truth = q.Answer;
            var slots = new List<int>();
            for (var i = 0; i < truth.Length; i++) if (Group(truth[i]) is not null) slots.Add(i);
            // поодинці: кожна літера — кожним двійником
            foreach (var i in slots)
                foreach (var twin in Group(truth[i])!.Skip(1))
                    Try(q, truth[..i] + twin + truth[(i + 1)..]);
            // стовпчиком: усі літери разом j-им двійником (де такого нема — лишається своя)
            for (var j = 1; j < columns; j++)
            {
                var sb = new System.Text.StringBuilder();
                var any = false;
                foreach (var ch in truth)
                    if (Group(ch) is { } g && j < g.Length) { sb.Append(g[j]); any = true; }
                    else sb.Append(ch);
                if (any) Try(q, sb.ToString());
            }
            // парами серед перших трьох підмінних літер — дві правки без спільного початку
            var head = slots.Take(3).ToList();
            for (var a = 0; a < head.Count; a++)
                for (var b = a + 1; b < head.Count; b++)
                    for (var j = 1; j < columns; j++)
                    {
                        var ga = Group(truth[head[a]])!;
                        var gb = Group(truth[head[b]])!;
                        if (j >= ga.Length || j >= gb.Length) continue;
                        var chars = truth.Select(c => c.ToString()).ToArray();
                        chars[head[a]] = ga[j];
                        chars[head[b]] = gb[j];
                        Try(q, string.Concat(chars));
                    }
        }
        output.WriteLine($"двійників перевірено: {tried} на {bank.Count} правдах");
        Assert.True(tried > 15000, $"перевірено лише {tried}");
        Assert.True(leaks.Count == 0, $"{leaks.Count} двійників пройшли, напр.: " + string.Join("; ", leaks.Take(15)));
    }

    /// <summary>
    /// Рецензія: правду склеювали («кістокмамонта», «РічардаЛевовеСерце») чи розбивали пробілом, дефісом, крапкою
    /// («Діс-ней-ленд», «ДІСНЕЙ.ЛЕНД», «ші-сть») — звичайною клавіатурою, — і сервер приймав її як брехню. На всьому
    /// справжньому банку кожне написання правди (відповідь і кожна форма accept) склеюємо цілком (і посеред фрази, і
    /// ВЕЛИКИМИ), склеюємо кожну пару сусідніх слів і розбиваємо кожне слово в кожному місці пробілом, дефісом і
    /// крапкою. Жодне таке написання не має лягти на стіл.
    /// </summary>
    [Fact]
    public void No_glued_or_split_truth_in_the_real_bank_is_accepted_as_a_lie()
    {
        var bank = BluffBank.All;
        var tried = 0;
        var leaks = new List<string>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        void Try(BluffQuestion q, string lie)
        {
            var text = BluffText.Clean(lie);
            if (text.Length == 0 || text.Length > Bluff.MaxLie) return;
            tried++;
            if (Bluff.Refuse(text, q) is null) leaks.Add($"{q.Id}: «{q.Answer}» → «{text}»");
        }
        foreach (var q in bank)
            foreach (var form in q.Forms.Distinct())
            {
                var words = form.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 1)
                {
                    var glued = string.Concat(words);
                    Try(q, glued);
                    Try(q, glued.ToUpperInvariant());
                    Try(q, "звісно ж, " + glued);
                    for (var w = 0; w + 1 < words.Length && words.Length > 2; w++)
                        Try(q, string.Join(' ', words[..w].Append(words[w] + words[w + 1]).Concat(words[(w + 2)..])));
                }
                for (var w = 0; w < words.Length; w++)
                    for (var cut = 1; cut < words[w].Length; cut++)
                        foreach (var gap in new[] { " ", "-", "." })
                        {
                            var parts = (string[])words.Clone();
                            parts[w] = words[w][..cut] + gap + words[w][cut..];
                            Try(q, string.Join(' ', parts));
                        }
            }
        output.WriteLine($"склеєних і розбитих правд перевірено: {tried} на {bank.Count} питаннях, {watch.ElapsedMilliseconds} мс");
        Assert.True(tried > 20000, $"перевірено лише {tried}");
        Assert.True(leaks.Count == 0, $"{leaks.Count} склеєних чи розбитих правд пройшли, напр.: " + string.Join("; ", leaks.Take(20)));
    }

    const string SwedenJson = """
        [ { "id": "s1", "cat": "history", "q": "3 вересня 1967 року вся Швеція за один день ___",
            "answer": "перейшла на правосторонній рух", "accept": ["перейшла на правий бік"],
            "decoys": ["перевела годинники на пів години", "відмовилася від монет", "перейменувала всі вулиці"],
            "note": "День Г." } ]
        """;

    /// <summary>
    /// Рецензія, жива партія (b0191): «ПЕРЕЙШЛА НАПРАВОСТОРОННІЙ РУХ» і «перейшла на право сторонній рух» сервер записав, і
    /// обидві лягли на стіл поруч із правдою. Тепер це «ти випадково написав правду», а чесна брехня поруч — проходить.
    /// </summary>
    [Fact]
    public void A_glued_or_split_truth_never_reaches_the_table()
    {
        var h = Table(2, services: Bank(SwedenJson));
        Until(h, Bluff.PhaseWrite);
        var before = Views.Text(h.View(0));
        foreach (var twin in new[]
        {
            "ПЕРЕЙШЛА НАПРАВОСТОРОННІЙ РУХ", "перейшла на право сторонній рух", "ПерейшлаНаПравостороннійРух",
            "перейшла на право-сторонній.рух",
        })
        {
            var r = h.Act(0, "lie", new { text = twin });
            Assert.False(r.Ok, twin);
            Assert.Equal(Bluff.Truthy, r.Message);
        }
        Assert.Equal(before, Views.Text(h.View(0)));
        Assert.True(h.Act(0, "lie", new { text = "перейшла на лівосторонній рух" }).Ok);
        Assert.Equal("перейшла на лівосторонній рух", MyLie(h, 0));
    }

    /// <summary>
    /// Рецензія: знаки-двійники поза старим списком («В◯ДЕНЬ», «Д│СНЕЙЛЕНД», «МА✕ОРКА», «Ап°ллон») проходили. Тепер
    /// посеред слова — лише білий список знаків; відмова своя, і «ти написав правду» на неї не витрачається.
    /// </summary>
    [Fact]
    public void A_mark_inside_a_word_is_refused_and_is_no_hint_about_the_truth()
    {
        var h = Table(2, services: Bank(HydrogenJson));
        Until(h, Bluff.PhaseWrite);
        var before = Views.Text(h.View(0));
        foreach (var twin in new[] { "В\u25EFДЕНЬ", "В\u25CBДЕНЬ", "В\u00B0ДЕНЬ", "В\u2205ДЕНЬ", "ВОД\u2502НЬ", "ВОДЕН\u2573Ь" })
        {
            var r = h.Act(0, "lie", new { text = twin });
            Assert.False(r.Ok, twin);
            Assert.Equal(Bluff.InWordMark, r.Message);
        }
        Assert.Equal(before, Views.Text(h.View(0)));
        // Шість відмов — а п'ять «ти написав правду» ще попереду: знак посеред слова підказкою не лічиться.
        for (var n = 0; n < Bluff.MaxTruthy; n++) Assert.Equal(Bluff.Truthy, h.Act(0, "lie", new { text = "водню" }).Message);
        Assert.Equal(Bluff.TooManyTries, h.Act(0, "lie", new { text = "гелій" }).Message);
        // Знаки біля пробілу й звичайні розділові посеред слова — можна.
        Assert.True(h.Act(1, "lie", new { text = "гелій \U0001F388 (мабуть), Пд.Буг/кіт" }).Ok);
    }

    [Fact]
    public void The_truth_with_another_service_word_is_still_the_truth()
    {
        var h = Table(2, services: Bank("""
            [ { "cat": "nature", "q": "Рогівка ока не має судин, а кисень отримує ___", "answer": "зі сліз",
                "accept": ["з сліз"], "decoys": ["з війок", "від кришталика", "з брів"] },
              { "cat": "odd", "q": "Якийсь час «Мона Ліза» висіла ___ Наполеона", "answer": "у спальні",
                "decoys": ["у ванній", "на кухні", "у кареті"] } ]
            """));
        for (var n = 0; n < 2; n++)
        {
            Until(h, Bluff.PhaseWrite);
            var q = Question(h, BluffBank.Parse("""
                [ { "q": "Рогівка ока не має судин, а кисень отримує ___", "answer": "зі сліз" },
                  { "q": "Якийсь час «Мона Ліза» висіла ___ Наполеона", "answer": "у спальні" } ]
                """));
            var twin = q.Answer == "зі сліз" ? "із сліз" : "в спальні";
            Assert.Equal(Bluff.Truthy, h.Act(0, "lie", new { text = twin }).Message);
            NextQuestion(h);
        }
    }

    [Fact]
    public void Different_words_a_typo_apart_stay_separate_cards_with_their_own_authors()
    {
        var h = Table(3, services: Bank("""
            [ { "cat": "sport", "q": "Перший чемпіонат світу з футболу 1930 року відбувся в ___", "answer": "Уругваї",
                "decoys": ["Бразилії", "Італії", "Аргентині"] } ]
            """));
        Until(h, Bluff.PhaseWrite);
        Lie(h, 0, "Іраку");
        Lie(h, 1, "Ірану");
        Lie(h, 2, "Австрії");
        Until(h, Bluff.PhasePick);
        var iraq = CardOf(h, "Іраку");
        var iran = CardOf(h, "Ірану");
        Assert.True(iraq >= 0 && iran >= 0 && iraq != iran, string.Join(", ", Texts(h)));
        Assert.True(Cards(h, 1)[iran].GetProperty("mine").GetBoolean());
        Assert.False(Cards(h, 1)[iraq].GetProperty("mine").GetBoolean());
        Assert.True(h.Act(1, "pick", new { i = iraq }).Ok);          // чужу — можна обрати
    }

    [Fact]
    public void Decoys_seen_through_the_dice_never_land_on_the_table_as_hleks_cards()
    {
        var h = Table(3, services: Bank(HydrogenJson));
        Until(h, Bluff.PhaseWrite);
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal("залізо", MyLie(h, 0));
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal("кисень", MyLie(h, 0));
        // Третій натиск нового не показує — Глек крутить уже бачені, щоб на столі лишилось, чого ти не знаєш.
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal("залізо", MyLie(h, 0));
        // Сусідові — лише те, чого ніхто не бачив.
        Assert.True(h.Act(1, "lie", new { auto = true }).Ok);
        Assert.Equal("кремній", MyLie(h, 1));
        Lie(h, 0, "Бамбарбія");                                     // передумав і написав сам
        Lie(h, 2, "кергуду");
        Until(h, Bluff.PhasePick);
        var texts = Texts(h);
        Assert.DoesNotContain("залізо", texts);                     // бачене через 🎲 Глек на стіл не кладе
        Assert.DoesNotContain("кисень", texts);
        Assert.Equal(4, texts.Count);                               // Бамбарбія, кремній Петра, кергуду Ганни і правда

        var once = Table(2, services: Bank(HydrogenJson));
        Until(once, Bluff.PhaseWrite);
        Assert.True(once.Act(0, "lie", new { auto = true }).Ok);
        Lie(once, 0, "Бамбарбія");
        Lie(once, 1, "кергуду");
        Until(once, Bluff.PhasePick);
        Assert.Equal(["Бамбарбія", "водень", "кергуду", "кисень", "кремній"], Texts(once).Order());
    }

    /// <summary>
    /// Рецензія: на двох Оля двічі тиснула 🎲 і написала своє, Петро взяв третю заготовку — на столі лишились три картки,
    /// і кожен вгадував 50 на 50. Тепер 🎲 не з'їдає стіл нижче <see cref="Bluff.MinTable"/>.
    /// </summary>
    [Fact]
    public void At_two_the_dice_never_thins_the_table_below_four_cards()
    {
        var h = Table(2, services: Bank(HydrogenJson));
        Until(h, Bluff.PhaseWrite);
        Assert.Equal("Глек підказав: «залізо». Можеш переписати", h.Act(0, "lie", new { auto = true }).Message);
        // Друга нова заготовка з'їла б першу 🎲 Петра, а третя — ще й стіл: Глек лишає ту, що є.
        var again = h.Act(0, "lie", new { auto = true });
        Assert.False(again.Ok);
        Assert.Equal(Bluff.DiceHeld, again.Message);
        Assert.Equal("залізо", MyLie(h, 0));
        Lie(h, 0, "Бамбарбія");
        Assert.Equal("Глек збрехав за тебе: «кисень»", h.Act(1, "lie", new { auto = true }).Message);
        Until(h, Bluff.PhasePick);
        Assert.Equal(["Бамбарбія", "водень", "кисень", "кремній"], Texts(h).Order());
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(3, Cards(h, s).EnumerateArray().Count(c => !c.GetProperty("mine").GetBoolean())));

        // Коли сусід уже написав своє, першу 🎲 для нього берегти не треба: друга заготовка — можна, третя — вже стіл.
        var solo = Table(2, services: Bank(HydrogenJson));
        Until(solo, Bluff.PhaseWrite);
        Assert.True(solo.Act(0, "lie", new { auto = true }).Ok);
        Lie(solo, 1, "кергуду");
        Assert.True(solo.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal("кисень", MyLie(solo, 0));
        Assert.True(solo.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal("залізо", MyLie(solo, 0));                    // по колу між баченими, «кремній» — для столу
        Until(solo, Bluff.PhasePick);
        Assert.Equal(["водень", "залізо", "кергуду", "кремній"], Texts(solo).Order());

        // Обидва взяли по першій — друга нова вже з'їла б стіл.
        var both = Table(2, services: Bank(HydrogenJson));
        Until(both, Bluff.PhaseWrite);
        Assert.True(both.Act(0, "lie", new { auto = true }).Ok);
        Assert.True(both.Act(1, "lie", new { auto = true }).Ok);
        Assert.Equal(Bluff.DiceHeld, both.Act(1, "lie", new { auto = true }).Message);
        Assert.Equal("кисень", MyLie(both, 1));
    }

    [Fact]
    public void The_dice_as_the_last_lie_does_not_promise_a_rewrite()
    {
        var h = Table(2, services: Bank(HydrogenJson));
        Until(h, Bluff.PhaseWrite);
        Assert.Equal("Глек підказав: «залізо». Можеш переписати", h.Act(0, "lie", new { auto = true }).Message);
        Lie(h, 0, "Бамбарбія");
        // Петро вже написав би сам, а Оля — останнє слово: фаза піде далі, тож «можеш переписати» було б неправдою.
        Assert.Equal("Глек збрехав за тебе: «кисень»", h.Act(1, "lie", new { auto = true }).Message);
    }

    [Fact]
    public void Guessing_the_truth_by_brute_force_runs_out_of_tries()
    {
        var h = Table(2, services: Bank(HydrogenJson));
        Until(h, Bluff.PhaseWrite);
        for (var n = 0; n < Bluff.MaxTruthy; n++) Assert.Equal(Bluff.Truthy, h.Act(0, "lie", new { text = "водень" }).Message);
        // Далі сервер уже не каже, правда це чи ні: відмова однакова на будь-який текст.
        Assert.Equal(Bluff.TooManyTries, h.Act(0, "lie", new { text = "водень" }).Message);
        Assert.Equal(Bluff.TooManyTries, h.Act(0, "lie", new { text = "Бамбарбія" }).Message);
        Assert.Null(MyLie(h, 0));
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);      // 🎲 лишається

        // Чесна брехня, переписана вдвадцяте, — досить; і реалтайм-ввід (Input) лічиться так само.
        for (var n = 0; n < Bluff.MaxLieTries; n++) Assert.True(h.Act(1, "lie", new { text = "брехня " + n }).Ok, n.ToString());
        Assert.Equal(Bluff.TooManyTries, h.Act(1, "lie", new { text = "ще одна" }).Message);
        h.Input(1, "lie", new { text = "через Input" });
        Assert.Equal("брехня " + (Bluff.MaxLieTries - 1), MyLie(h, 1));

    }

    [Fact]
    public void Tries_come_back_with_the_next_question()
    {
        var h = Table(2, options: new { questions = "5" });
        Until(h, Bluff.PhaseWrite);
        var q = Question(h);
        for (var n = 0; n < Bluff.MaxTruthy; n++) h.Act(0, "lie", new { text = q.Answer });
        Assert.Equal(Bluff.TooManyTries, h.Act(0, "lie", new { text = Lies[0] }).Message);
        NextQuestion(h);
        Until(h, Bluff.PhaseWrite);
        Assert.True(h.Act(0, "lie", new { text = Lies[0] }).Ok);
    }

    [Fact]
    public void Someone_who_left_gets_no_truth_chip_even_though_their_pick_stays_on_the_card()
    {
        var h = Table(3);
        WriteAndPick(h, 0, 1, 2);
        var truth = TruthCard(h);
        PickCard(h, 0, truth);
        PickCard(h, 2, truth);
        Assert.True(h.Leave("Ганна").Ok);
        PickCard(h, 1, CardOf(h, Lies[0]));
        UntilOpen(h, truth);
        var v = V(h);
        Assert.Equal([0, 2], Ints(Cards(h)[truth].GetProperty("picks")));     // вибір лишився на картці
        Assert.Equal(Bluff.TruthPts, v.GetProperty("truthDelta")[0].GetInt64());
        Assert.Equal(0, v.GetProperty("truthDelta")[2].GetInt64());           // а очок — ні
        Assert.Equal(0, v.GetProperty("delta")[2].GetInt64());
        Assert.Equal(0, v.GetProperty("truthDelta")[1].GetInt64());
    }

    [Fact]
    public void Ticks_send_views_but_no_frame_nobody_reads()
    {
        var h = Table(3);
        Until(h, Bluff.PhaseWrite);
        var n = h.Outbox.Count;
        Lie(h, 0);
        h.Tick();
        Until(h, Bluff.PhasePick);
        var fresh = h.Outbox.Skip(n).ToList();
        Assert.NotEmpty(fresh.OfType<RoomViews>());
        Assert.Empty(fresh.OfType<RoomFrame>());
        Assert.Null(h.Room.Game.Frame());
    }

    [Fact]
    public void The_journal_names_the_best_liar_but_never_repeats_free_text_to_the_whole_site()
    {
        var h = Table(3, options: new { questions = "5" });
        const string spicy = "Пікантна вигадка";
        Until(h, Bluff.PhaseWrite);
        Lie(h, 0, spicy);
        Lie(h, 1);
        Lie(h, 2);
        Until(h, Bluff.PhasePick);
        PickCard(h, 1, CardOf(h, spicy));
        PickCard(h, 2, CardOf(h, spicy));
        PlayOut(h);
        var line = h.Outbox.OfType<Journal>().Last().Text;
        Assert.Equal("Байкарі: Оля 1 000, Петро 0, Ганна 0 · найкраща брехня — Оля, 2 жертви", line);
        Assert.DoesNotContain(h.Outbox.OfType<Journal>(), j => j.Text.Contains(spicy));
        Assert.Equal(spicy, V(h).GetProperty("result").GetProperty("best").GetProperty("text").GetString());   // стіл бачить
    }

    [Fact]
    public void Hleks_word_after_the_truth_fits_how_many_found_it()
    {
        static string Quip(int who)
        {
            var h = Table(2);
            WriteAndPick(h, 0, 1);
            var truth = TruthCard(h);
            PickCard(h, 0, who >= 1 ? truth : CardOf(h, Lies[1]));
            PickCard(h, 1, who >= 2 ? truth : CardOf(h, Lies[0]));
            UntilOpen(h, truth);
            return V(h).GetProperty("quip").GetString()!;
        }

        Assert.Contains(Quip(0), Bluff.QuipsNobody);
        Assert.Contains(Quip(1), Bluff.QuipsSome);
        Assert.Contains(Quip(2), Bluff.QuipsAll);
        Assert.Empty(Bluff.QuipsNobody.Intersect(Bluff.QuipsAll));
        Assert.Equal(Bluff.QuipsNobody.Length + Bluff.QuipsSome.Length + Bluff.QuipsAll.Length, Bluff.Quips.Length);
    }

    [Fact]
    public void At_equal_victims_the_best_lie_is_a_hand_written_one_and_a_dice_lie_is_marked()
    {
        for (var seed = 1; seed < 60; seed++)
        {
            var h = Table(3, seed, options: new { questions = "5" });
            Until(h, Bluff.PhaseWrite);
            var q = Question(h);
            Assert.True(h.Act(0, "lie", new { auto = true }).Ok);           // Оля — через 🎲
            Lie(h, 1, "Бамбарбія");
            Lie(h, 2);
            Until(h, Bluff.PhasePick);
            var dice = CardOf(h, q.Decoys[0]);
            var hand = CardOf(h, "Бамбарбія");
            if (dice > hand) continue;                                    // потрібен стіл, де 🎲-картка раніша
            PickCard(h, 1, dice);
            PickCard(h, 2, hand);
            PickCard(h, 0, TruthCard(h));
            PlayOut(h);
            var best = V(h).GetProperty("result").GetProperty("best");
            Assert.Equal("Бамбарбія", best.GetProperty("text").GetString());
            Assert.False(best.GetProperty("hlek").GetBoolean());
            Assert.EndsWith("найкраща брехня — Петро, 1 жертва", h.Outbox.OfType<Journal>().Last().Text);

            // Коли 🎲-брехня таки найкраща — підсумок так і каже: «від Глека».
            var solo = Table(2, seed, options: new { questions = "5" });
            Until(solo, Bluff.PhaseWrite);
            var sq = Question(solo);
            Assert.True(solo.Act(0, "lie", new { auto = true }).Ok);
            Lie(solo, 1);
            Until(solo, Bluff.PhasePick);
            PickCard(solo, 1, CardOf(solo, sq.Decoys[0]));
            PlayOut(solo);
            var top = V(solo).GetProperty("result").GetProperty("best");
            Assert.True(top.GetProperty("hlek").GetBoolean());
            Assert.True(V(solo).GetProperty("result").GetProperty("recap")[0].GetProperty("best").GetProperty("hlek").GetBoolean());
            return;
        }
        Assert.Fail("не знайшлось столу, де 🎲-картка стоїть раніше");
    }

    [Fact]
    public void A_dice_lie_that_fools_two_earns_points_but_not_the_fox()
    {
        var h = Table(3);
        Until(h, Bluff.PhaseWrite);
        var q = Question(h);
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Lie(h, 1);
        Lie(h, 2);
        Until(h, Bluff.PhasePick);
        var dice = CardOf(h, q.Decoys[0]);
        PickCard(h, 1, dice);
        PickCard(h, 2, dice);
        Until(h, Bluff.PhaseScore);
        Assert.Equal(2 * Bluff.FooledPts, Score(h, 0));                  // очки — як за будь-яку брехню
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:bluff-fox");  // а «Хитрий лис» — лише за свою
    }

    [Fact]
    public async Task A_new_table_takes_its_memory_in_the_lobby_and_start_never_asks_the_database()
    {
        using var temp = new TempDb();
        var first = Table(2, options: new { questions = "5" }, services: Services(temp.Db));
        var seen = new List<string>();
        while (Playing(first))
        {
            seen.Add(V(first).GetProperty("text").GetString()!);
            NextQuestion(first);
        }
        await BluffSeen.Idle.WaitAsync(TimeSpan.FromSeconds(10));

        // Ті самі люди сідають за новий стіл: лобі показали — пам'ять підтяглась фоном.
        var h = Seated(2, options: new { questions = "5" }, services: Services(temp.Db));
        _ = V(h, 0);
        await BluffSeen.Idle.WaitAsync(TimeSpan.FromSeconds(10));
        // Базу витерли вже після лобі: Start під замком кімнати в неї не ходить, тож пам'ять не губиться.
        temp.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM bluff_seen";
            cmd.ExecuteNonQuery();
        });
        Assert.True(h.Start().Ok, h.Reply.Message);
        var second = new List<string>();
        while (Playing(h))
        {
            second.Add(V(h).GetProperty("text").GetString()!);
            NextQuestion(h);
        }
        Assert.Equal(5, second.Count);
        Assert.Empty(seen.Intersect(second));
    }

    [Fact]
    public void The_bank_hides_the_style_of_the_truth_and_drops_answers_longer_than_a_lie()
    {
        var bank = BluffBank.Parse("""
            [ { "q": "Кондратюків спосіб узяли в програмі ___", "answer": "«Аполлон»", "decoys": ["«Джеміні»", "“Шаттл”"] },
              { "q": "Boring побраталося з селом ___", "answer": "Dull («тьмяне»)", "decoys": ["Yawn («позіх»)", "Sleepy (сонне)"] },
              { "q": "На кордоні штрафували за ___", "answer": "яйце «Кіндер Сюрприз»", "decoys": ["\"вівсяне\" печиво", "перо"] },
              { "q": "Задовга правда ___", "answer": "дуже довга правда, якої жоден гравець не напише", "decoys": ["а", "б"] } ]
            """);
        Assert.Equal(3, bank.Count);
        Assert.Equal("Аполлон", bank[0].Answer);
        Assert.Equal(["Джеміні", "Шаттл"], bank[0].Decoys);
        Assert.Equal("Dull", bank[1].Answer);
        Assert.Equal(["Yawn", "Sleepy"], bank[1].Decoys);
        Assert.Equal("яйце Кіндер Сюрприз", bank[2].Answer);
        Assert.Equal(["вівсяне печиво", "перо"], bank[2].Decoys);
    }

    // =====================================================================================
    // контракт і швидкодія
    // =====================================================================================

    [Fact]
    public void The_server_accepts_exactly_what_the_module_sends()
    {
        var h = Table(3);
        Until(h, Bluff.PhaseWrite);
        Assert.True(h.Act(0, "lie", new { text = Lies[0] }).Ok);      // «Готово» / Enter
        Assert.True(h.Act(1, "lie", new { auto = true }).Ok);         // 🎲
        Assert.True(h.Act(2, "lie", Lies[2]).Ok);                     // голий рядок теж годиться
        Assert.Equal("Тут так не ходять", h.Act(0, "answer", new { value = 1 }).Message);
        h.Tick();
        Assert.Equal(Bluff.PhasePick, Phase(h));
        var truth = TruthCard(h);
        Assert.True(h.Act(0, "pick", new { i = truth }).Ok);          // клік по картці
        Assert.True(h.Act(1, "pick", truth).Ok);                      // голе число
        Assert.True(h.Act(2, "pick", truth.ToString(System.Globalization.CultureInfo.InvariantCulture)).Ok);   // рядок
        var olya = CardOf(h, Lies[0]);
        Assert.True(h.Act(1, "pick", new { i = olya }).Ok);
        UntilOpen(h, olya);
        Assert.True(h.Act(1, "like", new { i = olya }).Ok);           // ❤
        Assert.True(h.Act(2, "like", olya).Ok);
    }

    [Fact]
    public void A_real_party_on_the_real_bank_plays_to_the_end()
    {
        var bank = BluffBank.All;
        var h = Table(3, seed: 11, services: new ServiceCollection().BuildServiceProvider());
        var rounds = 0;
        while (Playing(h))
        {
            Until(h, Bluff.PhaseWrite);
            if (!Playing(h)) break;
            var q = Question(h, bank);
            for (var s = 0; s < 3; s++)
            {
                var lie = Lies.Skip(s).Concat(Lies).First(l => !BluffText.LooksTrue(l, q) && !q.Decoys.Any(d => BluffText.LooksSame(d, l)));
                Assert.True(h.Act(s, "lie", new { text = lie }).Ok);
            }
            Until(h, Bluff.PhasePick);
            Assert.True(h.Act(0, "pick", new { i = CardOf(h, q.Answer) }).Ok);
            rounds++;
            NextQuestion(h);
        }
        Assert.Equal(Bluff.DefaultQuestions, rounds);
        Assert.Equal([0], h.Finished.Single().Result.Winners);
    }
}

/// <summary>
/// Швидкодія «Байкарів» — окремою колекцією: у паралельному прогоні стінний годинник бреше в рази (SerialPerf).
/// </summary>
[Collection(SerialPerf.Name)]
public class BluffPerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Eight_liars_for_three_thousand_ticks_stay_cheap()
    {
        var best = double.MaxValue;
        var bestTotal = 0.0;
        var games = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var h = BluffTests.Table(8, seed: 3, options: new { questions = "10", pace = "fast" });
            var ticks = new Stopwatch();
            var total = Stopwatch.StartNew();
            var key = "";
            games = 1;
            for (var t = 0; t < 3000; t++)
            {
                Drive(h, ref key);
                ticks.Start();
                h.Tick();
                ticks.Stop();
                if (h.Room.Status != RoomStatus.Playing)
                {
                    Assert.True(h.Rematch().Ok);
                    games++;
                    key = "";
                }
            }
            total.Stop();
            if (ticks.Elapsed.TotalMilliseconds < best)
            {
                best = ticks.Elapsed.TotalMilliseconds;
                bestTotal = total.Elapsed.TotalMilliseconds;
            }
        }
        output.WriteLine($"3000 тиків на восьмох: {best:F1} мс ({best / 3000 * 1000:F2} мкс на тик), разом із ходами й видами тесту {bestTotal:F0} мс, партій {games}");
        Assert.True(best < 1000, $"3000 тиків — {best:F0} мс");
        Assert.True(games >= 3, $"за 3000 тиків зіграно лише {games} партій — ходи не доходять?");
    }

    /// <summary>Один крок «живої» партії на вісьмох: на кожну нову фазу всі роблять те, що зробили б люди.</summary>
    internal static void Drive(RoomHarness h, ref string key)
    {
        var v = h.View(null);
        var phase = v.GetProperty("phase").GetString()!;
        var now = $"{v.GetProperty("q").GetInt32()}:{phase}:{v.GetProperty("revealed").GetArrayLength()}";
        if (now == key) return;
        key = now;
        switch (phase)
        {
            case Bluff.PhaseWrite:
                for (var s = 0; s < 8; s++)
                    if (s == 7) h.Act(s, "lie", new { auto = true });
                    else h.Act(s, "lie", new { text = BluffTests.Lies[s] });
                break;
            case Bluff.PhasePick:
                for (var s = 0; s < 8; s++)
                {
                    var cards = h.View(s).GetProperty("options");
                    var len = cards.GetArrayLength();
                    for (int i = s * 3 % len, n = 0; n < len; n++, i = (i + 1) % len)
                        if (!cards[i].GetProperty("mine").GetBoolean()) { h.Act(s, "pick", new { i }); break; }
                }
                break;
            case Bluff.PhaseReveal:
                var last = v.GetProperty("revealed")[v.GetProperty("revealed").GetArrayLength() - 1].GetInt32();
                for (var s = 0; s < 8; s++) h.Act(s, "like", new { i = last });
                break;
        }
    }
}
