using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Піктіонарі (specs/pictionary.md): художник обирає слово з трьох і малює, решта вгадує, художники по черзі.
/// Словник у тестах свій, крихітний: з одного слова видно, що саме випало.
/// </summary>
public class PictionaryTests
{
    static readonly int PickTicks = Pictionary.PickMs / Pictionary.TickMs;
    static readonly int RevealTicks = Pictionary.RevealMs / Pictionary.TickMs;

    static PictionaryWords Words(params string[] words) => new(words.Select(w => ("animals", w)));

    static RoomHarness Table(PictionaryWords words, object? options = null, params string[] nicks)
    {
        var h = new RoomHarness("pictionary", options: options, seed: 7, services: RoomHarness.WithService(words));
        foreach (var nick in nicks) h.Join(nick);
        h.Start();
        return h;
    }

    static RoomHarness Three(object? options = null) => Table(Words("кіт"), options, "Оля", "Петро", "Ганна");

    /// <summary>Художник обирає перше слово, і хід починається.</summary>
    static void PickFirst(RoomHarness h)
    {
        var drawer = h.View(null).GetProperty("drawer").GetInt32();
        Assert.True(h.Act(drawer, "pick", new { i = 0 }).Ok);
    }

    static ActResult Guess(RoomHarness h, int seat, string text)
    {
        h.Clock.AdvanceMs(Pictionary.GuessEveryMs);
        return h.Act(seat, "guess", new { text });
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static int Drawer(RoomHarness h) => h.View(null).GetProperty("drawer").GetInt32();
    static int Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt32();

    static JsonElement LastFrame(RoomHarness h) =>
        Views.Json(h.Outbox.OfType<RoomFrame>().Last(f => f.RoomId == h.RoomId).Frame);

    /// <summary>Малюнок на дроті — рядком (SketchWire, прохід №3); тут — назад у масиви.</summary>
    internal static List<int[]> Ops(JsonElement holder) => SketchWire.Unpack(holder.GetProperty("z").GetString())!;

    static object Line(int stroke, params int[] p) => new { s = stroke, c = 1, w = 8, p };

    // ---------------------------------------------------------------- вибір слова

    [Fact]
    public void Only_the_drawer_sees_the_choices()
    {
        var h = Table(Words("кіт", "пес", "їжак", "сова"), null, "Оля", "Петро");

        Assert.Equal("pick", Phase(h));
        Assert.Equal(0, Drawer(h));
        Assert.Equal(Pictionary.Choices, h.View(0).GetProperty("choices").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("choices").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("choices").ValueKind);
    }

    [Fact]
    public void Picking_starts_the_drawing_and_hides_the_word_from_guessers()
    {
        var h = Three();
        PickFirst(h);

        Assert.Equal("draw", Phase(h));
        Assert.Equal("кіт", h.View(0).GetProperty("word").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("word").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("word").ValueKind);
        Assert.Equal("___", h.View(1).GetProperty("mask").GetString());
    }

    [Fact]
    public void A_guesser_cannot_pick_the_word()
    {
        var h = Three();
        Assert.False(h.Act(1, "pick", new { i = 0 }).Ok);
        Assert.Equal("pick", Phase(h));
    }

    [Fact]
    public void A_slow_drawer_gets_a_word_picked_for_them()
    {
        var h = Three();
        h.Tick(PickTicks + 1);
        Assert.Equal("draw", Phase(h));
        Assert.Equal("кіт", h.View(0).GetProperty("word").GetString());
    }

    [Fact]
    public void The_frame_never_carries_the_word()
    {
        var h = Table(Words("жирафа"), null, "Оля", "Петро");
        PickFirst(h);
        h.Tick();

        var text = LastFrame(h).GetRawText();
        Assert.DoesNotContain("жирафа", text);
        Assert.Equal("______", LastFrame(h).GetProperty("mask").GetString());
    }

    // ---------------------------------------------------------------- здогадки

    [Fact]
    public void A_right_guess_scores_and_reveals_the_word_to_that_guesser()
    {
        var h = Three();
        PickFirst(h);

        var r = Guess(h, 1, "  КІТ ");

        Assert.True(r.Ok);
        Assert.Contains("Лови", r.Message);
        Assert.True(Score(h, 1) >= Pictionary.MaxGuessPoints);          // майже весь час лишався + перший
        Assert.Equal("кіт", h.View(1).GetProperty("word").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(2).GetProperty("word").ValueKind);
        Assert.Contains(1, h.View(null).GetProperty("guessed").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void A_wrong_guess_lands_in_the_feed_for_everyone()
    {
        var h = Three();
        PickFirst(h);

        Assert.True(Guess(h, 1, "собака").Ok);

        var feed = h.View(2).GetProperty("feed").EnumerateArray().ToList();
        Assert.Contains(feed, f => f.GetProperty("kind").GetString() == "guess" && f.GetProperty("text").GetString() == "собака");
        Assert.Equal(0, Score(h, 1));
    }

    [Fact]
    public void A_close_guess_is_told_only_to_the_guesser()
    {
        var h = Table(Words("корова"), null, "Оля", "Петро");
        PickFirst(h);

        var r = Guess(h, 1, "короваа");   // одна зайва літера
        Assert.False(r.Ok);
        Assert.Contains("Гаряче", r.Message);
        Assert.DoesNotContain(h.View(0).GetProperty("feed").EnumerateArray(), f => f.GetProperty("kind").GetString() == "guess");
    }

    [Fact]
    public void The_drawer_cannot_guess()
    {
        var h = Three();
        PickFirst(h);
        Assert.False(Guess(h, 0, "кіт").Ok);
    }

    [Fact]
    public void Guessing_twice_after_a_hit_is_refused()
    {
        var h = Three();
        PickFirst(h);
        Assert.True(Guess(h, 1, "кіт").Ok);
        Assert.False(Guess(h, 1, "кіт").Ok);
    }

    [Fact]
    public void Guesses_are_rate_limited()
    {
        var h = Three();
        PickFirst(h);
        Assert.True(Guess(h, 1, "пес").Ok);
        var r = h.Act(1, "guess", new { text = "кінь" });
        Assert.False(r.Ok);
        Assert.Equal("Не так швидко", r.Message);
    }

    [Fact]
    public void An_earlier_guess_is_worth_more_and_the_first_gets_a_bonus()
    {
        var h = Three();
        PickFirst(h);
        Guess(h, 1, "кіт");
        h.Clock.AdvanceMs(40_000);
        Guess(h, 2, "кіт");

        Assert.True(Score(h, 1) > Score(h, 2) + Pictionary.FirstBonus);
    }

    [Fact]
    public void When_everyone_guessed_the_turn_ends_and_the_drawer_gets_a_share()
    {
        var h = Three();
        PickFirst(h);
        Guess(h, 1, "кіт");
        Guess(h, 2, "кіт");

        Assert.Equal("reveal", Phase(h));
        Assert.True(Score(h, 0) > 0);
        Assert.True(Score(h, 0) < Score(h, 1));
        Assert.Equal("кіт", h.View(null).GetProperty("word").GetString());
    }

    [Fact]
    public void Time_running_out_ends_the_turn_without_points()
    {
        var h = Three(new { seconds = "60" });
        PickFirst(h);
        h.Tick(60_000 / Pictionary.TickMs + 1);

        Assert.Equal("reveal", Phase(h));
        Assert.Equal(0, Score(h, 0));
    }

    [Theory]
    [InlineData("хот-дог", "хот дог", true)]
    [InlineData("хот-дог", "ХОТДОГ", true)]
    [InlineData("м'яч", "м’яч", true)]
    [InlineData("кіт", "це кіт!", true)]
    [InlineData("кіт", "кит", false)]
    [InlineData("повітряна кулька", "кулька", false)]
    [InlineData("кіт", "", false)]
    public void Matching_ignores_case_hyphens_and_apostrophes(string word, string guess, bool expected) =>
        Assert.Equal(expected, Pictionary.Matches(guess, word));

    [Theory]
    [InlineData("корова", "карова", true)]
    [InlineData("корова", "корова", false)]
    [InlineData("кіт", "кит", false)]              // коротке слово: одна літера — вже пів відповіді
    [InlineData("холодильник", "холодилник", true)]
    [InlineData("корова", "собака", false)]
    [InlineData("кажан", "кажна", true)]            // переставлені сусідні літери — одна помилка, а не дві
    [InlineData("холодильник", "холодлиьник", true)]
    [InlineData("кажан", "ажкан", false)]
    public void Close_means_one_or_two_letters_away(string word, string guess, bool expected) =>
        Assert.Equal(expected, Pictionary.Close(guess, word));

    [Fact]
    public void Hints_open_letters_as_time_passes()
    {
        var h = Table(Words("холодильник"), new { seconds = "60" }, "Оля", "Петро");
        PickFirst(h);
        Assert.DoesNotContain(h.View(1).GetProperty("mask").GetString()!, c => c != '_');

        h.Tick(45_000 / Pictionary.TickMs);
        var mask = h.View(1).GetProperty("mask").GetString()!;
        Assert.Equal(11, mask.Length);
        Assert.InRange(mask.Count(c => c != '_'), 1, 3);
    }

    [Fact]
    public void A_hint_travels_in_a_frame_not_in_full_views()
    {
        // прохід 28.09: підказка слала кожному місцю повний вид з усім малюнком — заради однієї літери
        var h = Table(Words("холодильник"), new { seconds = "60" }, "Оля", "Петро");
        PickFirst(h);
        h.Tick(29_900 / Pictionary.TickMs);
        h.Outbox.Clear();

        h.Tick(2);   // 30-та секунда — половина часу, перша літера

        Assert.Empty(h.Outbox.OfType<RoomViews>());
        var mask = LastFrame(h).GetProperty("mask").GetString()!;
        Assert.Equal(1, mask.Count(c => c != '_'));
        Assert.Equal(mask, h.View(1).GetProperty("mask").GetString());
    }

    [Fact]
    public void Frames_carry_the_feed_only_when_something_new_is_in_it()
    {
        // прохід 28.09: кожен кадр (десять на секунду) віз дванадцять останніх здогадок наново
        var h = Three();
        PickFirst(h);
        h.Tick();

        Guess(h, 1, "пес");
        Guess(h, 2, "сова");
        h.Tick();
        var feed = LastFrame(h).GetProperty("feed");
        Assert.Equal(["пес", "сова"], feed.EnumerateArray().Select(f => f.GetProperty("text").GetString()!).ToArray());

        h.Input(0, "draw", Line(1, 10, 10, 200, 200));
        h.Tick();
        Assert.Equal(JsonValueKind.Null, LastFrame(h).GetProperty("feed").ValueKind);

        Guess(h, 1, "лис");
        h.Tick();
        feed = LastFrame(h).GetProperty("feed");
        Assert.Equal(["лис"], feed.EnumerateArray().Select(f => f.GetProperty("text").GetString()!).ToArray());
        // а вид, з якого стартує новенький, як і раніше несе всю стрічку
        Assert.Equal(3, h.View(null).GetProperty("feed").GetArrayLength());
    }

    // ---------------------------------------------------------------- малюнок

    [Fact]
    public void Only_the_drawer_draws_and_frames_carry_the_new_strokes()
    {
        var h = Three();
        PickFirst(h);
        h.Tick();

        h.Input(0, "draw", Line(1, 10, 10, 200, 200));
        h.Input(1, "draw", Line(1, 500, 500, 600, 600));   // не художник — мовчки ні
        h.Tick();

        var f = LastFrame(h);
        Assert.Equal(1, f.GetProperty("n").GetInt32());
        var op = Ops(f)[0];
        Assert.Equal([0, 1, 1, 8, 10, 10, 200, 200], op);
        Assert.Equal(1, h.View(2).GetProperty("drawing").GetProperty("n").GetInt32());
    }

    [Fact]
    public void A_frame_carries_only_what_was_added_since_the_last_one()
    {
        var h = Three();
        PickFirst(h);
        h.Input(0, "draw", Line(1, 1, 1, 2, 2));
        h.Tick();
        h.Input(0, "draw", Line(1, 2, 2, 3, 3));
        h.Tick();

        var f = LastFrame(h);
        Assert.Equal(1, f.GetProperty("from").GetInt32());
        Assert.Equal(2, f.GetProperty("n").GetInt32());
        Assert.Equal(1, Ops(f).Count);
    }

    [Fact]
    public void Undo_removes_the_whole_last_stroke_and_bumps_the_version()
    {
        var h = Three();
        PickFirst(h);
        h.Input(0, "draw", Line(1, 1, 1, 2, 2));
        h.Input(0, "draw", Line(2, 5, 5, 6, 6));
        h.Input(0, "draw", Line(2, 6, 6, 7, 7));
        h.Tick();
        var ver = LastFrame(h).GetProperty("ver").GetInt32();

        h.Input(0, "undo");
        h.Tick();

        var f = LastFrame(h);
        Assert.Equal(ver + 1, f.GetProperty("ver").GetInt32());
        Assert.Equal(0, f.GetProperty("from").GetInt32());
        Assert.Equal(1, f.GetProperty("n").GetInt32());
        Assert.Equal(1, Ops(f)[0][1]);
    }

    [Fact]
    public void Clear_wipes_the_canvas()
    {
        var h = Three();
        PickFirst(h);
        h.Input(0, "draw", Line(1, 1, 1, 2, 2));
        h.Input(0, "fill", new { s = 2, c = 5, x = 100, y = 100 });
        h.Input(0, "clear");
        h.Tick();

        Assert.Equal(0, h.View(1).GetProperty("drawing").GetProperty("n").GetInt32());
    }

    [Fact]
    public void Coordinates_are_clamped_to_the_canvas_and_bad_strokes_dropped()
    {
        var h = Three();
        PickFirst(h);
        h.Input(0, "draw", Line(1, -50, 99999, 20, 30));
        h.Input(0, "draw", new { s = 2, c = 99, w = 8, p = new[] { 1, 1 } });          // нема такого кольору
        h.Input(0, "draw", new { s = 3, c = 1, w = 8, p = new[] { 1, 1, 2 } });        // непарна кількість
        h.Input(0, "draw", new { s = 4, c = 1, w = 8, p = Enumerable.Repeat(1, (Pictionary.MaxChunkPoints + 1) * 2).ToArray() });
        h.Tick();

        var ops = Ops(h.View(1).GetProperty("drawing"));
        Assert.Single(ops);
        Assert.Equal([0, 1, 1, 8, 0, Pictionary.CanvasH, 20, 30], ops[0]);
    }

    [Fact]
    public void Nobody_draws_while_the_word_is_being_picked()
    {
        var h = Three();
        h.Input(0, "draw", Line(1, 1, 1, 2, 2));
        Assert.Equal(0, h.View(1).GetProperty("drawing").GetProperty("n").GetInt32());
    }

    // ---------------------------------------------------------------- черга й кінець

    [Fact]
    public void After_the_reveal_the_next_seat_draws_on_a_clean_canvas()
    {
        var h = Three();
        PickFirst(h);
        h.Input(0, "draw", Line(1, 1, 1, 2, 2));
        Guess(h, 1, "кіт");
        Guess(h, 2, "кіт");
        h.Tick(RevealTicks + 1);

        Assert.Equal("pick", Phase(h));
        Assert.Equal(1, Drawer(h));
        Assert.Equal(0, h.View(2).GetProperty("drawing").GetProperty("n").GetInt32());
        Assert.Empty(h.View(null).GetProperty("guessed").EnumerateArray());
    }

    [Fact]
    public void Everyone_draws_rounds_times_and_the_best_wins()
    {
        var h = Table(Words("кіт", "пес", "сова", "їжак"), new { rounds = "1" }, "Оля", "Петро");

        // Оля малює, Петро вгадує
        PickFirst(h);
        Guess(h, 1, h.View(0).GetProperty("word").GetString()!);
        h.Tick(RevealTicks + 1);
        // Петро малює, Оля не вгадує
        Assert.Equal(1, Drawer(h));
        PickFirst(h);
        h.Tick(Pictionary.DefaultSeconds * 1000 / Pictionary.TickMs + 1);
        h.Tick(RevealTicks + 1);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Contains("попереду Петро", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal(2, h.Scores.Count);
    }

    [Fact]
    public void Nobody_guessing_anything_is_a_draw()
    {
        var h = Table(Words("кіт", "пес"), new { rounds = "1", seconds = "60" }, "Оля", "Петро");
        for (var t = 0; t < 2; t++)
        {
            h.Tick(PickTicks + 1);
            h.Tick(60_000 / Pictionary.TickMs + 1);
            h.Tick(RevealTicks + 1);
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Room.Result!.Winners);
    }

    [Fact]
    public void The_drawer_leaving_ends_their_turn_but_not_the_game()
    {
        var h = Table(Words("кіт", "пес", "сова"), null, "Оля", "Петро", "Ганна");
        PickFirst(h);
        h.Leave("Оля");
        h.Tick();

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("reveal", Phase(h));
    }

    [Fact]
    public void Down_to_one_player_the_game_ends()
    {
        var h = Table(Words("кіт"), null, "Оля", "Петро");
        PickFirst(h);
        h.Leave("Петро");
        h.Tick();

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Rematch_starts_fresh()
    {
        var h = Table(Words("кіт", "пес"), new { rounds = "1", seconds = "60" }, "Оля", "Петро");
        PickFirst(h);
        Guess(h, 1, h.View(0).GetProperty("word").GetString()!);
        for (var i = 0; i < 3 && h.Room.Status == RoomStatus.Playing; i++)
            h.Tick(PickTicks + 60_000 / Pictionary.TickMs + RevealTicks + 3);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        h.Rematch();

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal("pick", Phase(h));
        Assert.All(h.View(null).GetProperty("scores").EnumerateArray(), s => Assert.Equal(0, s.GetInt32()));
        Assert.Empty(h.View(null).GetProperty("feed").EnumerateArray());
    }

    [Fact]
    public void An_empty_dictionary_refuses_to_set_the_table()
    {
        var h = new RoomHarness("pictionary", services: RoomHarness.WithService(Words()));
        var reply = h.Join("Оля");
        Assert.False(reply.Ok);
        Assert.Contains("словника", reply.Message);
    }

    // ---------------------------------------------------------------- словник

    [Fact]
    public void The_real_dictionary_is_big_and_every_topic_has_words()
    {
        var root = FindRoot();
        var words = PictionaryWords.Load(Path.Combine(root, PictionaryWords.FileName));

        Assert.True(words.Count >= 1000, $"слів лише {words.Count}");
        foreach (var (key, _) in PictionaryWords.Topics.Where(t => t.Key != PictionaryWords.AnyTopic))
            Assert.True(words.CountIn(key) >= 40, $"у темі {key} лише {words.CountIn(key)}");

        // кожна тема у файлі має бути в списку опцій — інакше її не обрати
        var keys = File.ReadAllLines(Path.Combine(root, PictionaryWords.FileName))
            .Where(l => l.StartsWith("##", StringComparison.Ordinal))
            .Select(l => l[2..].Split('|')[0].Trim());
        Assert.All(keys, k => Assert.Contains(PictionaryWords.Topics, t => t.Key == k));
    }

    [Fact]
    public void Topics_limit_the_words_on_offer()
    {
        var words = new PictionaryWords([("animals", "кіт"), ("food", "борщ"), ("food", "сало"), ("food", "хліб")]);
        var picked = words.Pick(new Random(1), PictionaryWords.ParseTopics("food"), new HashSet<string>(), 3);
        Assert.Equal(new HashSet<string>(["борщ", "сало", "хліб"]), picked.ToHashSet());
        Assert.Null(PictionaryWords.ParseTopics("all,food"));
        Assert.Null(PictionaryWords.ParseTopics("nonsense"));
    }

    [Fact]
    public void Words_are_not_repeated_while_fresh_ones_remain()
    {
        var words = Words("кіт", "пес", "сова", "їжак", "лев", "вовк");
        var used = new HashSet<string>(["кіт", "пес", "сова"]);
        var picked = words.Pick(new Random(3), null, used, 3);
        Assert.Equal(new HashSet<string>(["вовк", "лев", "їжак"]), picked.ToHashSet());
    }

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "liquidsoap", "radio.liq"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("корінь репозиторію не знайдено");
    }
}

/// <summary>
/// Швидкодія Піктіонарі на повному столі (прохід 28.09): десятеро, художник малює весь хід так, як шле клієнт
/// (шматок штриха кожні 60 мс), решта кидає промахи, дехто влучає. Міряємо тик, розмір кадру й виду.
/// Окремою колекцією: стінний годинник у паралельному прогоні бреше (<see cref="SerialPerf"/>).
/// </summary>
[Collection(SerialPerf.Name)]
public class PictionaryPerfTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марія", "Богдан", "Софія", "Тарас", "Мар'яна", "Остап"];
    static readonly string[] Misses = ["хата", "сонце", "дерево", "машина", "човен", "м'яч", "квітка", "гора", "риба", "хмара"];

    [Fact]
    [Trait("Category", "Perf")]
    public void Ten_players_drawing_a_whole_game_stay_cheap()
    {
        var words = new PictionaryWords(Enumerable.Range(0, 60).Select(i => ("animals", "слово" + (char)('а' + i % 30) + i)));
        var h = new RoomHarness("pictionary", options: new { rounds = "1", seconds = "60" }, seed: 5, services: RoomHarness.WithService(words));
        foreach (var n in Nicks) h.Join(n);
        h.Start();

        var rng = new Random(9);
        var ticks = new System.Diagnostics.Stopwatch();
        var acts = new System.Diagnostics.Stopwatch();
        int tickCount = 0, actCount = 0, stroke = 1, frames = 0;
        long frameBytes = 0, frameMax = 0, viewMax = 0, viewBytes = 0, viewCount = 0;
        var t = 0;
        while (h.Room.Status == RoomStatus.Playing && t < 20_000)
        {
            t++;
            var v = h.View(null);
            var phase = v.GetProperty("phase").GetString();
            var drawer = v.GetProperty("drawer").GetInt32();
            if (phase == "pick") { acts.Start(); h.Act(drawer, "pick", new { i = 0 }); acts.Stop(); actCount++; }
            else if (phase == "draw")
            {
                // півтора шматка на тик (кожні 60 мс), по 6 точок: так шле клієнт зі звичайною мишею
                for (var k = 0; k < (t % 2 == 0 ? 2 : 1); k++)
                {
                    var p = new int[12];
                    for (var i = 0; i < 12; i += 2) { p[i] = rng.Next(100, 900); p[i + 1] = rng.Next(100, 650); }
                    acts.Start(); h.Input(drawer, "draw", new { s = stroke, c = 1 + t % 7, w = 8, p }); acts.Stop(); actCount++;
                    if (t % 20 == 0) stroke++;
                }
                // кожен вгадувач — промах раз на ~4 с, а на 30-й секунді ходу половина влучає
                var guesser = t % Nicks.Length;
                if (guesser != drawer && t % 4 == 0)
                {
                    var word = h.View(drawer).GetProperty("word").GetString()!;
                    var elapsed = (h.Clock.UtcNow - DateTimeOffset.Parse(v.GetProperty("until").GetString()!)).TotalSeconds + 60;
                    h.Clock.AdvanceMs(Pictionary.GuessEveryMs);
                    acts.Start();
                    h.Act(guesser, "guess", new { text = elapsed > 30 && guesser % 2 == 0 ? word : Misses[rng.Next(Misses.Length)] });
                    acts.Stop(); actCount++;
                }
            }

            h.Outbox.Clear();
            ticks.Start();
            h.Tick();
            ticks.Stop();
            tickCount++;
            foreach (var o in h.Outbox)
            {
                if (o is RoomFrame f)
                {
                    var size = System.Text.Encoding.UTF8.GetByteCount(Views.Text(f.Frame));
                    frames++; frameBytes += size; frameMax = Math.Max(frameMax, size);
                }
                else if (o is RoomViews && phase == "draw")
                {
                    var size = System.Text.Encoding.UTF8.GetByteCount(Views.Text(h.Room.Game.View(0)));
                    viewCount++; viewBytes += size; viewMax = Math.Max(viewMax, size);
                }
            }
        }

        var tickUs = ticks.Elapsed.TotalMilliseconds * 1000 / tickCount;
        var actUs = acts.Elapsed.TotalMilliseconds * 1000 / Math.Max(1, actCount);
        output.WriteLine($"тиків {tickCount}: {tickUs:F1} мкс на тик; ходів/вводів {actCount}: {actUs:F1} мкс на один (з JSON тесту)");
        output.WriteLine($"кадрів {frames}: сер {frameBytes / Math.Max(1, frames)} Б, макс {frameMax} Б");
        output.WriteLine($"видів у малюванні {viewCount} (на одне місце): сер {viewBytes / Math.Max(1, viewCount)} Б, макс {viewMax} Б");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(tickUs < 250, $"тик {tickUs:F0} мкс");
    }
}
