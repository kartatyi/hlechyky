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
    public void The_starter_bank_has_at_least_fifteen_questions_with_one_blank_each()
    {
        var bank = BluffBank.All;
        Assert.True(bank.Count >= 15, $"у банку лише {bank.Count} питань");
        Assert.All(bank, q =>
        {
            Assert.Equal(1, CountBlanks(q.Q));
            Assert.False(string.IsNullOrWhiteSpace(q.Answer));
            Assert.True(q.Decoys.Count >= 2, $"«{q.Q}»: заготовок {q.Decoys.Count}");
            Assert.True(q.Answer.Length <= Bluff.MaxLie, $"«{q.Q}»: правда довша за брехню");
            // Жодна заготовка не близнюк правди (банк таких відсіює мовчки).
            Assert.All(q.Decoys, d => Assert.DoesNotContain(q.Forms, f => BluffText.LooksSame(d, f)));
        });
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
        // Кожну тему з попапа можна обрати й зіграти.
        Assert.All(known, k => Assert.Contains(BluffBank.All, q => q.Cat == k));
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
        Assert.Contains("сіли грати в байкарів", h.Outbox.OfType<Journal>().Last().Text);
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
        Assert.Single(fresh.OfType<RoomFrame>());

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
    public void Pressing_the_dice_again_gives_the_next_decoy_in_a_circle()
    {
        var h = Table(2);
        Until(h, Bluff.PhaseWrite);
        var q = Question(h);
        string Mine() => V(h, 0).GetProperty("my").GetProperty("lie").GetString()!;
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal(q.Decoys[0], Mine());
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal(q.Decoys[1], Mine());
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal(q.Decoys[2], Mine());
        Assert.True(h.Act(0, "lie", new { auto = true }).Ok);
        Assert.Equal(q.Decoys[0], Mine());
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
        Assert.NotEmpty(h.Outbox.Skip(n).OfType<RoomFrame>());
        Assert.All(h.Outbox.Skip(n).OfType<RoomFrame>(), f => Assert.DoesNotContain(secret, Raw(f.Frame)));
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
             "options", "revealed", "note", "quip", "scores", "delta", "likeDelta", "victims", "result"],
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
    public void The_frame_is_small_and_carries_no_text()
    {
        var h = Table(8);
        Until(h, Bluff.PhaseWrite);
        for (var s = 0; s < 8; s++) Lie(h, s);
        Until(h, Bluff.PhasePick);
        var frame = Frame(h);
        output.WriteLine($"кадр: {frame.Length} Б — {frame}");
        Assert.True(frame.Length <= 300, $"кадр {frame.Length} Б");
        foreach (var text in Lies.Concat(Nicks).Append(Question(h).Answer).Append(Question(h).Q))
            Assert.DoesNotContain(text, frame);
        Assert.Equal(["phase", "q", "step", "endsAt", "wrote", "picked", "scores"],
            JsonDocument.Parse(frame).RootElement.EnumerateObject().Select(p => p.Name));
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
        Assert.Equal($"Байкарі: Оля 1 000, Петро 0, Ганна 0 · найкраща брехня — «{Lies[0]}» (Оля, 2 жертви)",
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
        Assert.EndsWith("найкраща брехня — «Бамбарбія» (Оля і Петро, 1 жертва)", co.Outbox.OfType<Journal>().Last().Text);
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
        Assert.EndsWith($"«{Lies[2]}» (Ганна, 1 жертва)", result.Text);
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
