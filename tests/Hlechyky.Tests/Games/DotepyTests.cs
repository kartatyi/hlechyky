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
/// «Дотепи» (specs/dotepy.md §9): роздача, написання, голосування, очки, кінці партії, приховане, голос Глека,
/// голос публіки, детермінізм. Швидкодія — окремим класом у <see cref="SerialPerf"/>.
/// </summary>
public class DotepyTests
{
    internal static readonly string[] Names = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Тарас", "Леся", "Богдан"];

    /// <summary>
    /// Голос для тестів: записує всі <c>Prepare</c> (з прапорцем терміновості), а <c>Ready</c> віддає кліп одразу
    /// (<paramref name="readyAfter"/> = 0), після N запитів або ніколи (−1). Тривалість — текст / 14 знаків на секунду.
    /// </summary>
    internal sealed class FakeVoice(bool enabled = true, int readyAfter = 0) : IDotepyVoice
    {
        public readonly List<(string Voice, string Text, bool Urgent)> Prepared = [];
        readonly Dictionary<string, int> _asked = [];

        public bool Enabled => enabled;

        public void Prepare(string voice, IEnumerable<string> texts, bool urgent = false)
        {
            foreach (var t in texts) Prepared.Add((voice, t, urgent));
        }

        public DotepyClip? Ready(string voice, string text)
        {
            if (readyAfter < 0) return null;
            var n = _asked[text] = _asked.GetValueOrDefault(text) + 1;
            return n > readyAfter ? new DotepyClip("/api/games/svoya/tts/" + Hash(text) + ".mp3", text.Length / 14.0) : null;
        }

        static string Hash(string s) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }

    /// <summary>Банк на <paramref name="normal"/> звичайних і <paramref name="finals"/> фінальних завдань.</summary>
    internal static IReadOnlyList<DotepyPrompt> Bank(int normal = 40, int finals = 12) =>
    [
        .. Enumerable.Range(1, normal).Select(i => new DotepyPrompt($"n{i:000}", $"Звичайне завдання номер {i}", ["тест"], false)),
        .. Enumerable.Range(1, finals).Select(i => new DotepyPrompt($"f{i:000}", $"Фінальне завдання номер {i}", ["тест"], true)),
    ];

    internal static RoomHarness Table(int players, object? options = null, int seed = 7, IDotepyVoice? voice = null,
        IReadOnlyList<DotepyPrompt>? bank = null, DotepySeen? seen = null, bool start = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new DotepyPrompts(bank ?? Bank()));
        services.AddSingleton(seen ?? new DotepySeen(Dotepy.SeenRing));
        if (voice is not null) services.AddSingleton(voice);
        var h = new RoomHarness("dotepy", options, seed, services.BuildServiceProvider());
        foreach (var n in Names.Take(players)) Assert.True(h.Join(n).Ok, h.Reply.Message);
        if (start) Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    internal static Dotepy Game(RoomHarness h) => (Dotepy)h.Room.Game;
    internal static JsonElement V(RoomHarness h, int? seat = null) => h.View(seat);
    internal static string Phase(RoomHarness h) => V(h).GetProperty("phase").GetString()!;
    internal static JsonElement Card(RoomHarness h) => V(h).GetProperty("card");
    internal static int CardI(RoomHarness h) => Card(h).GetProperty("i").GetInt32();
    internal static int[] Ints(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetInt32())];
    internal static int[] Voters(RoomHarness h) => Ints(Card(h).GetProperty("voters"));
    internal static int[] Voted(RoomHarness h) => Ints(Card(h).GetProperty("voted"));
    internal static int[] Mine(RoomHarness h, int seat) => Ints(V(h, seat).GetProperty("me").GetProperty("mine"));
    internal static long Score(RoomHarness h, int seat) =>
        V(h).GetProperty("players").EnumerateArray().First(p => p.GetProperty("seat").GetInt32() == seat).GetProperty("score").GetInt64();
    internal static JsonElement Answers(RoomHarness h) => Card(h).GetProperty("answers");
    internal static string Say(RoomHarness h) => V(h).GetProperty("say").GetProperty("text").GetString()!;

    /// <summary>Місця, що ще грають (не встали).</summary>
    internal static int[] Present(RoomHarness h) =>
        [.. V(h).GetProperty("players").EnumerateArray().Where(p => !p.GetProperty("left").GetBoolean()).Select(p => p.GetProperty("seat").GetInt32())];

    internal static (int I, string Prompt)[] Tasks(RoomHarness h, int seat) =>
        [.. V(h, seat).GetProperty("me").GetProperty("tasks").EnumerateArray().Select(t => (t.GetProperty("i").GetInt32(), t.GetProperty("prompt").GetString()!))];

    /// <summary>Усі присутні здають усі свої завдання.</summary>
    internal static void WriteAll(RoomHarness h, Func<int, int, string>? text = null, int[]? skip = null)
    {
        foreach (var s in Present(h))
        {
            if (skip?.Contains(s) == true) continue;
            foreach (var t in V(h, s).GetProperty("me").GetProperty("tasks").EnumerateArray().ToList())
            {
                if (t.GetProperty("done").GetBoolean()) continue;      // здане не перезаписуємо
                var i = t.GetProperty("i").GetInt32();
                Assert.True(h.Act(s, "answer", new { i, text = text?.Invoke(s, i) ?? $"дотеп {s}.{i}" }).Ok, h.Reply.Message);
            }
        }
    }

    /// <summary>Перша відповідь картки, яка не моя.</summary>
    internal static int FirstOther(RoomHarness h, int seat)
    {
        var mine = Mine(h, seat);
        var n = Answers(h).GetArrayLength();
        for (var i = 0; i < n; i++) if (!mine.Contains(i)) return i;
        throw new InvalidOperationException("нема за кого голосувати");
    }

    /// <summary>Повний бюлетень місця: перші чужі відповіді, скільки дозволено (у фіналі — усі медалі).</summary>
    internal static int[] Ballot(RoomHarness h, int seat)
    {
        var mine = Mine(h, seat);
        var per = Card(h).GetProperty("perVoter").GetInt32();
        return [.. Enumerable.Range(0, Answers(h).GetArrayLength()).Where(i => !mine.Contains(i)).Take(per)];
    }

    /// <summary>Усі, хто має голос і ще не голосував, голосують за <paramref name="pick"/>.</summary>
    internal static void VoteAll(RoomHarness h, Func<int, int[]> pick)
    {
        var card = CardI(h);
        var voted = Voted(h);
        foreach (var v in Voters(h))
        {
            if (voted.Contains(v)) continue;
            Assert.True(h.Act(v, "vote", new { card, picks = pick(v) }).Ok, h.Reply.Message);
        }
    }

    internal static void Until(RoomHarness h, Func<bool> done, int max = 4000)
    {
        for (var i = 0; i < max; i++)
        {
            if (done()) return;
            h.Tick();
        }
        Assert.Fail("не дочекались");
    }

    internal static void UntilPhase(RoomHarness h, string phase) => Until(h, () => Phase(h) == phase);

    /// <summary>
    /// Зіграти до кінця: пишуть усі, голосують усі (типово — за першу чужу). Повертає всі завдання партії.
    /// </summary>
    internal static List<string> PlayMatch(RoomHarness h, Func<int, int[]>? pick = null, Action<JsonElement>? onReveal = null)
    {
        var prompts = new List<string>();
        var lastReveal = -1;
        for (var guard = 0; guard < 20_000 && h.Room.Status == RoomStatus.Playing; guard++)
        {
            var v = V(h);
            var phase = v.GetProperty("phase").GetString();
            if (phase == "write")
            {
                prompts.AddRange(v.GetProperty("prompts").EnumerateArray().Select(p => p.GetString()!));
                WriteAll(h);
            }
            else if (phase == "vote" && Voted(h).Length < Voters(h).Length) VoteAll(h, pick ?? (s => [FirstOther(h, s)]));
            else if (phase == "reveal" && onReveal is not null)
            {
                var key = v.GetProperty("round").GetInt32() * 100 + v.GetProperty("card").GetProperty("i").GetInt32();
                if (key != lastReveal) { lastReveal = key; onReveal(v); }
            }
            h.Tick();
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        return prompts;
    }

    static int Count<T>(RoomHarness h) => h.Outbox.OfType<T>().Count();

    static readonly JsonSerializerOptions Readable = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Вид текстом без екранування кирилиці — щоб шукати в ньому слова (на дроті вона йде як «\uXXXX»).</summary>
    internal static string Text(RoomHarness h, int? seat)
    {
        lock (h.Room.Sync) return JsonSerializer.Serialize(h.Room.Game.View(seat), Readable);
    }

    // ======================================================================================
    // банк і каталог
    // ======================================================================================

    [Fact]
    public void Bank_loads_prompts_json_and_skips_broken_records()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                { "version": 1, "prompts": [
                  { "id": "d1", "text": "Найгірша  назва для\n радіо", "tags": ["радіо"], "final": false },
                  { "id": "d2", "tags": ["без тексту"] },
                  { "id": "d3", "text": "%LONG%" },
                  { "id": "d1", "text": "Повторний id" },
                  { "text": "Без id" },
                  "не об'єкт",
                  { "id": "d4", "text": "Фінальне", "final": true },
                ] }
                """.Replace("%LONG%", new string('я', DotepyBank.MaxText + 1)));
            var bank = DotepyBank.Load(path, out var problem);
            Assert.Equal(["d1", "d4"], bank.Select(p => p.Id));
            Assert.Equal("Найгірша назва для радіо", bank[0].Text);
            Assert.Equal(["радіо"], bank[0].Tags);
            Assert.False(bank[0].Final);
            Assert.True(bank[1].Final);
            Assert.Contains("5", problem);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Bank_missing_or_invalid_file_gives_empty_bank_not_exception()
    {
        Assert.Empty(DotepyBank.Load(Path.Combine(Path.GetTempPath(), "нема-" + Guid.NewGuid() + ".json"), out var missing));
        Assert.NotNull(missing);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ це не json");
            Assert.Empty(DotepyBank.Load(path, out var broken));
            Assert.NotNull(broken);
            File.WriteAllText(path, """{ "version": 1, "items": [] }""");
            Assert.Empty(DotepyBank.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Starter_bank_has_at_least_30_prompts_and_10_finals_with_unique_ids()
    {
        var bank = DotepyBank.Load(Paths.Resolve(DotepyBank.FileName), out var problem);
        Assert.Null(problem);
        Assert.True(bank.Count >= 30, $"у банку {bank.Count}");
        Assert.True(bank.Count(p => p.Final) >= 10);
        Assert.Equal(bank.Count, bank.Select(p => p.Id).Distinct().Count());
        Assert.All(bank, p => Assert.InRange(p.Text.Length, 10, DotepyBank.MaxText));
    }

    [Fact]
    public void Catalog_lists_dotepy_as_party_byHost_hidden_3_to_8_with_css_and_three_options()
    {
        var game = Assert.Single(new Registry().Catalog, g => g.Id == "dotepy");
        Assert.Equal("Дотепи", game.Title);
        Assert.Equal("party", game.Group);
        Assert.Equal("byHost", game.Start);
        Assert.True(game.Hidden);
        Assert.False(game.Private);
        Assert.Equal(3, game.MinPlayers);
        Assert.Equal(8, game.MaxPlayers);
        Assert.Equal(Dotepy.TickMs, game.TickMs);
        Assert.True(game.HasCss);
        Assert.Equal("dotepy", game.Module);
        Assert.Equal(["rounds", "write", "voice"], game.Options.Select(o => o.Key));
        Assert.Equal(["full", "90", "ostap"], game.Options.Select(o => o.Default));
        Assert.Equal("1", new Dotepy().SeatName(0));
        Assert.Equal("8", new Dotepy().SeatName(7));
    }

    [Fact]
    public void A_table_of_two_cannot_start()
    {
        var h = Table(2, start: false);
        var r = h.Start();
        Assert.False(r.Ok);
        Assert.Equal("Замало гравців, треба щонайменше 3", r.Message);
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal("lobby", Phase(h));
    }

    // ======================================================================================
    // роздача
    // ======================================================================================

    [Fact]
    public void Five_players_get_duels_two_tasks_each_and_two_authors_per_prompt()
    {
        var h = Table(5);
        var v = V(h);
        Assert.Equal("write", Phase(h));
        Assert.Equal("duel", v.GetProperty("mode").GetString());
        Assert.Equal(5, v.GetProperty("prompts").GetArrayLength());
        var authors = new int[5];
        for (var s = 0; s < 5; s++)
        {
            var tasks = Tasks(h, s);
            Assert.Equal(2, tasks.Length);
            Assert.NotEqual(tasks[0].I, tasks[1].I);
            foreach (var (i, prompt) in tasks)
            {
                authors[i]++;
                Assert.Equal(v.GetProperty("prompts")[i].GetString(), prompt);
            }
        }
        Assert.All(authors, n => Assert.Equal(2, n));
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("me").ValueKind);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Three_and_four_players_all_write_the_same_two_prompts(int players)
    {
        var h = Table(players);
        Assert.Equal("all", V(h).GetProperty("mode").GetString());
        Assert.Equal(2, V(h).GetProperty("prompts").GetArrayLength());
        for (var s = 0; s < players; s++) Assert.Equal([0, 1], Tasks(h, s).Select(t => t.I));
    }

    static HashSet<string> Pairs(RoomHarness h, int players)
    {
        var byCard = new Dictionary<int, List<int>>();
        for (var s = 0; s < players; s++)
            foreach (var (i, _) in Tasks(h, s))
                (byCard.TryGetValue(i, out var l) ? l : byCard[i] = []).Add(s);
        Assert.All(byCard.Values, l => Assert.Equal(2, l.Count));
        return [.. byCard.Values.Select(l => $"{Math.Min(l[0], l[1])}-{Math.Max(l[0], l[1])}")];
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Round_two_pairs_never_repeat_round_one_pairs_at_five_to_eight(int players)
    {
        var h = Table(players, seed: 100 + players);
        var one = Pairs(h, players);
        Assert.Equal(players, one.Count);
        WriteAll(h);
        h.Tick();
        Until(h, () =>
        {
            if (Phase(h) == "vote" && Voted(h).Length < Voters(h).Length) VoteAll(h, s => [0]);
            return Phase(h) == "write" && V(h).GetProperty("round").GetInt32() == 2;
        });
        Assert.Equal("duel", V(h).GetProperty("mode").GetString());
        var two = Pairs(h, players);
        Assert.Equal(players, two.Count);
        Assert.Empty(one.Intersect(two));
    }

    [Fact]
    public void Prompts_do_not_repeat_within_a_match_nor_after_rematch()
    {
        var h = Table(5, bank: Bank(normal: 30, finals: 6));
        var first = PlayMatch(h);
        Assert.Equal(5 + 5 + 1, first.Count);
        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        var second = PlayMatch(h);
        Assert.Empty(first.Intersect(second));
        Assert.Equal(second.Count, second.Distinct().Count());
    }

    [Fact]
    public void The_same_server_does_not_hand_one_prompt_to_two_tables_in_a_row()
    {
        var seen = new DotepySeen(Dotepy.SeenRing);
        var a = Table(3, options: new { rounds = "short" }, seed: 3, seen: seen);
        var b = Table(3, options: new { rounds = "short" }, seed: 3, seen: seen);
        var pa = V(a).GetProperty("prompts").EnumerateArray().Select(p => p.GetString()).ToList();
        var pb = V(b).GetProperty("prompts").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Empty(pa.Intersect(pb));
    }

    [Fact]
    public void Final_takes_a_final_flagged_prompt_when_the_bank_has_one()
    {
        var bank = Bank(normal: 20, finals: 0).Append(new DotepyPrompt("f1", "Єдине фінальне завдання", [], true)).ToList();
        var h = Table(3, options: new { rounds = "blitz" }, bank: bank);
        Assert.True(V(h).GetProperty("final").GetBoolean());
        Assert.Equal(["Єдине фінальне завдання"], V(h).GetProperty("prompts").EnumerateArray().Select(p => p.GetString()));

        // фінальних нема зовсім — береться будь-яке
        var plain = Table(3, options: new { rounds = "blitz" }, bank: Bank(normal: 5, finals: 0));
        Assert.Single(V(plain).GetProperty("prompts").EnumerateArray());
    }

    [Fact]
    public void Short_has_one_round_and_final_and_blitz_only_the_final()
    {
        var s = Table(4, options: new { rounds = "short" });
        Assert.Equal(2, V(s).GetProperty("rounds").GetInt32());
        Assert.False(V(s).GetProperty("final").GetBoolean());
        WriteAll(s);
        s.Tick();
        Until(s, () =>
        {
            if (Phase(s) == "vote" && Voted(s).Length < Voters(s).Length) VoteAll(s, x => [FirstOther(s, x)]);
            return Phase(s) == "write" && V(s).GetProperty("round").GetInt32() == 2;
        });
        Assert.True(V(s).GetProperty("final").GetBoolean());
        Assert.Equal(1, V(s).GetProperty("prompts").GetArrayLength());

        var b = Table(6, options: new { rounds = "blitz" });
        var v = V(b);
        Assert.Equal(1, v.GetProperty("rounds").GetInt32());
        Assert.True(v.GetProperty("final").GetBoolean());
        Assert.Equal("all", v.GetProperty("mode").GetString());
        Assert.Equal(Dotepy.FinalWriteMs(90_000), v.GetProperty("totalMs").GetInt32());
        for (var x = 0; x < 6; x++) Assert.Single(Tasks(b, x));
    }

    [Fact]
    public void Empty_bank_finishes_the_match_at_start_with_a_draw_and_a_journal_line()
    {
        var h = Table(3, bank: []);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(h.Room.Result!.Draw);
        Assert.Contains(h.Outbox.OfType<Journal>(), j => j.Text == "Дотепи: банк завдань не знайшовся, партії не буде");
        Assert.Equal("done", Phase(h));
    }

    // ======================================================================================
    // написання
    // ======================================================================================

    [Fact]
    public void Answer_is_cleaned_stored_and_visible_only_to_its_author()
    {
        var h = Table(3);
        Assert.True(h.Act(0, "answer", new { i = 0, text = "  Радіо\n «Куряча\u0007   сліпота»  " }).Ok);
        var task = V(h, 0).GetProperty("me").GetProperty("tasks")[0];
        Assert.Equal("Радіо «Куряча сліпота»", task.GetProperty("text").GetString());
        Assert.True(task.GetProperty("done").GetBoolean());
        Assert.DoesNotContain("Куряча", Text(h, 1));
        Assert.DoesNotContain("Куряча", Text(h, null));
        Assert.Contains("Куряча", Text(h, 0));
        // зданий текст можна замінити, поки пишуть
        Assert.True(h.Act(0, "answer", new { i = 0, text = "Хвиля «Дощ у сараї FM»" }).Ok);
        Assert.Equal("Хвиля «Дощ у сараї FM»", V(h, 0).GetProperty("me").GetProperty("tasks")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Empty_and_too_long_answers_are_refused_and_the_view_is_unchanged()
    {
        var h = Table(3);
        var before = V(h, 0).GetRawText();
        var r = h.Act(0, "answer", new { i = 0, text = "  \t\n " });
        Assert.False(r.Ok);
        Assert.Equal("Порожній дотеп — то ще не дотеп", r.Message);
        r = h.Act(0, "answer", new { i = 0, text = new string('ї', Dotepy.MaxAnswer + 1) });
        Assert.False(r.Ok);
        Assert.Equal("Задовго: до 80 знаків", r.Message);
        Assert.True(h.Act(0, "answer", new { i = 0, text = new string('ї', Dotepy.MaxAnswer) }).Ok);
        r = h.Act(0, "answer", new { i = 0 });
        Assert.Equal("Порожній дотеп — то ще не дотеп", r.Message);
        Assert.NotEqual(before, V(h, 0).GetRawText());
        before = V(h, 0).GetRawText();
        h.Act(0, "answer", new { i = 0, text = "" });
        Assert.Equal(before, V(h, 0).GetRawText());
    }

    [Fact]
    public void Answer_to_a_task_that_is_not_mine_is_refused()
    {
        var h = Table(5);
        var mine = Tasks(h, 0).Select(t => t.I).ToArray();
        var other = Enumerable.Range(0, 5).First(i => !mine.Contains(i));
        var before = V(h).GetRawText();
        foreach (var payload in new object[] { new { i = other, text = "чуже" }, new { i = 99, text = "нема" }, new { text = "без номера" } })
        {
            var r = h.Act(0, "answer", payload);
            Assert.False(r.Ok);
            Assert.Equal("Це не твоє завдання", r.Message);
        }
        Assert.Equal("Це не твоє завдання", h.Act(0, "edit", new { i = other }).Message);
        Assert.Equal("Тут так не ходять", h.Act(0, "dance", new { }).Message);
        Assert.Equal(before, V(h).GetRawText());
    }

    [Fact]
    public void Draft_input_is_kept_and_becomes_the_answer_at_the_deadline()
    {
        var h = Table(3);
        var views = Count<RoomViews>(h);
        h.Input(0, "draft", new { i = 0, text = "Півень  на  підписці" });
        h.Input(0, "draft", new { i = 5, text = "не туди" });
        h.Input(0, "draft", new { i = 1, text = new string('а', 200) });
        h.Tick();
        Assert.Equal(views, Count<RoomViews>(h));      // чернетка — без розсилки видів
        var tasks = V(h, 0).GetProperty("me").GetProperty("tasks");
        Assert.Equal("Півень на підписці", tasks[0].GetProperty("text").GetString());
        Assert.False(tasks[0].GetProperty("done").GetBoolean());
        Assert.Equal(Dotepy.MaxAnswer, tasks[1].GetProperty("text").GetString()!.Length);
        Assert.DoesNotContain("Півень", V(h, 1).GetRawText());

        h.Clock.AdvanceMs(90_000);
        h.Tick();
        Assert.Equal("vote", Phase(h));
        UntilPhase(h, "reveal");
        var answers = Answers(h).EnumerateArray().ToList();
        var mine = answers.Single(a => a.GetProperty("text").GetString() == "Півень на підписці");
        Assert.Equal(0, mine.GetProperty("seat").GetInt32());
        Assert.False(mine.GetProperty("stock").GetBoolean());
    }

    [Fact]
    public void Unanswered_task_gets_a_stock_answer_that_collects_votes_but_no_points()
    {
        var h = Table(3);
        WriteAll(h, skip: [0]);
        h.Clock.AdvanceMs(90_000);
        h.Tick();
        Assert.Equal("vote", Phase(h));
        var answers = Answers(h).EnumerateArray().ToList();
        var stock = answers.FindIndex(a => a.GetProperty("stock").GetBoolean());
        Assert.True(stock >= 0);
        Assert.Contains(answers[stock].GetProperty("text").GetString(), DotepyStock.Lines);
        Assert.Contains(stock, Mine(h, 0));   // підставна — на місці того, хто не встиг
        Assert.Equal("За себе не голосують", h.Act(0, "vote", new { card = CardI(h), picks = new[] { stock } }).Message);
        Assert.True(h.Act(1, "vote", new { card = CardI(h), picks = new[] { stock } }).Ok);
        Assert.True(h.Act(2, "vote", new { card = CardI(h), picks = new[] { stock } }).Ok);
        Assert.True(h.Act(0, "vote", new { card = CardI(h), picks = new[] { FirstOther(h, 0) } }).Ok);
        h.Tick();
        Assert.Equal("reveal", Phase(h));
        var a = Answers(h)[stock];
        Assert.Equal(2, a.GetProperty("votes").GetArrayLength());
        Assert.Equal(0, a.GetProperty("points").GetInt32());
        Assert.Equal(JsonValueKind.Null, Card(h).GetProperty("sweep").ValueKind);
        Assert.Equal(0, Score(h, 0));
        Assert.Equal(DotepyLines.StockWin, Say(h));
    }

    [Fact]
    public void Writing_closes_on_the_next_tick_once_everyone_is_ready()
    {
        var h = Table(4);
        WriteAll(h);
        Assert.Equal("write", Phase(h));      // Act фаз не міняє
        Assert.All(V(h).GetProperty("players").EnumerateArray(), p => Assert.True(p.GetProperty("ready").GetBoolean()));
        h.Tick();
        Assert.Equal("vote", Phase(h));
        Assert.Equal(0, CardI(h));
    }

    [Fact]
    public void Edit_takes_the_answer_back_and_ready_flag_drops()
    {
        var h = Table(3);
        Assert.Equal("Ще не здано", h.Act(0, "edit", new { i = 0 }).Message);
        WriteAll(h);
        Assert.True(V(h).GetProperty("players")[0].GetProperty("ready").GetBoolean());
        Assert.True(h.Act(0, "edit", new { i = 1 }).Ok);
        var task = V(h, 0).GetProperty("me").GetProperty("tasks")[1];
        Assert.False(task.GetProperty("done").GetBoolean());
        Assert.Equal("дотеп 0.1", task.GetProperty("text").GetString());
        Assert.False(V(h).GetProperty("players")[0].GetProperty("ready").GetBoolean());
        h.Tick();
        Assert.Equal("write", Phase(h));       // один не здав — чекаємо
        Assert.Equal("Ще не здано", h.Act(0, "edit", new { i = 1 }).Message);
    }

    [Fact]
    public void A_card_where_every_answer_is_stock_is_skipped()
    {
        var h = Table(5);
        var authors = Enumerable.Range(0, 5).Where(s => Tasks(h, s).Any(t => t.I == 0)).ToArray();
        foreach (var s in Present(h))
            foreach (var (i, _) in Tasks(h, s))
                if (i != 0) Assert.True(h.Act(s, "answer", new { i, text = $"д{s}{i}" }).Ok);
        Assert.Equal(2, authors.Length);
        h.Clock.AdvanceMs(90_000);
        h.Tick();
        Assert.Equal("vote", Phase(h));
        var seen = new List<int>();
        Until(h, () =>
        {
            if (Phase(h) == "vote")
            {
                if (!seen.Contains(CardI(h))) seen.Add(CardI(h));
                if (Voted(h).Length < Voters(h).Length) VoteAll(h, _ => [0]);
            }
            return Phase(h) == "table";
        });
        Assert.Equal([1, 2, 3, 4], seen);
    }

    // ======================================================================================
    // голосування
    // ======================================================================================

    [Fact]
    public void Duel_authors_do_not_vote_and_voting_closes_when_the_others_have_voted()
    {
        var h = Table(5);
        WriteAll(h);
        h.Tick();
        var voters = Voters(h);
        Assert.Equal(3, voters.Length);
        var authors = Enumerable.Range(0, 5).Except(voters).ToArray();
        foreach (var a in authors)
        {
            Assert.False(V(h, a).GetProperty("me").GetProperty("voter").GetBoolean());
            Assert.Single(Mine(h, a));
            Assert.Equal("Це твій дотеп — за нього голосують інші", h.Act(a, "vote", new { card = 0, picks = new[] { 0 } }).Message);
        }
        Assert.True(V(h, voters[0]).GetProperty("me").GetProperty("voter").GetBoolean());
        Assert.True(h.Act(voters[0], "vote", new { card = 0, picks = new[] { 0 } }).Ok);
        Assert.True(h.Act(voters[1], "vote", new { card = 0, picks = new[] { 1 } }).Ok);
        h.Tick();
        Assert.Equal("vote", Phase(h));
        Assert.Equal([voters[0], voters[1]], Voted(h));
        Assert.True(h.Act(voters[2], "vote", new { card = 0, picks = new[] { 1 } }).Ok);
        h.Tick();
        Assert.Equal("reveal", Phase(h));
    }

    [Fact]
    public void In_all_mode_everyone_votes_but_never_for_themselves()
    {
        var h = Table(3);
        WriteAll(h);
        h.Tick();
        Assert.Equal([0, 1, 2], Voters(h));
        for (var s = 0; s < 3; s++)
        {
            Assert.True(V(h, s).GetProperty("me").GetProperty("voter").GetBoolean());
            var mine = Assert.Single(Mine(h, s));
            Assert.Equal("За себе не голосують", h.Act(s, "vote", new { card = 0, picks = new[] { mine } }).Message);
        }
        VoteAll(h, s => [FirstOther(h, s)]);
        h.Tick();
        Assert.Equal("reveal", Phase(h));
    }

    [Fact]
    public void Vote_for_a_stale_card_or_outside_vote_phase_is_refused()
    {
        var h = Table(3);
        Assert.Equal("Зараз не голосують", h.Act(0, "vote", new { card = 0, picks = new[] { 1 } }).Message);
        WriteAll(h);
        h.Tick();
        Assert.Equal("Ця картка вже пішла", h.Act(0, "vote", new { card = 1, picks = new[] { FirstOther(h, 0) } }).Message);
        Assert.Equal("Ця картка вже пішла", h.Act(0, "vote", new { picks = new[] { FirstOther(h, 0) } }).Message);
        Assert.Equal("Зараз не пишуть", h.Act(0, "answer", new { i = 0, text = "пізно" }).Message);
        Assert.Equal("Зараз не пишуть", h.Act(0, "edit", new { i = 0 }).Message);
        VoteAll(h, s => [FirstOther(h, s)]);
        h.Tick();
        Assert.Equal("reveal", Phase(h));
        Assert.Equal("Зараз не голосують", h.Act(0, "vote", new { card = 0, picks = new[] { FirstOther(h, 0) } }).Message);
    }

    [Fact]
    public void Revote_replaces_the_previous_pick()
    {
        var h = Table(5);
        WriteAll(h);
        h.Tick();
        var v = Voters(h);
        Assert.True(h.Act(v[0], "vote", new { card = 0, picks = new[] { 0 } }).Ok);
        Assert.True(h.Act(v[0], "vote", new { card = 0, picks = new[] { 1 } }).Ok);
        Assert.Equal([1], Ints(V(h, v[0]).GetProperty("me").GetProperty("picks")));
        Assert.True(h.Act(v[1], "vote", new { card = 0, picks = new[] { 1 } }).Ok);
        Assert.True(h.Act(v[2], "vote", new { card = 0, picks = new[] { 0 } }).Ok);
        h.Tick();
        Assert.Equal(2, Answers(h)[1].GetProperty("votes").GetArrayLength());
        Assert.Equal(1, Answers(h)[0].GetProperty("votes").GetArrayLength());
    }

    [Fact]
    public void Final_ranking_accepts_partial_lists_and_caps_at_answers_minus_one()
    {
        var three = Table(3, options: new { rounds = "blitz" });
        WriteAll(three);
        three.Tick();
        Assert.Equal(2, Card(three).GetProperty("perVoter").GetInt32());
        Assert.True(Card(three).GetProperty("ranked").GetBoolean());
        var others = Enumerable.Range(0, 3).Where(i => !Mine(three, 0).Contains(i)).ToArray();
        Assert.True(three.Act(0, "vote", new { card = 0, picks = new[] { others[0] } }).Ok);
        Assert.True(three.Act(0, "vote", new { card = 0, picks = others }).Ok);
        Assert.Equal(others, Ints(V(three, 0).GetProperty("me").GetProperty("picks")));
        Assert.Equal("Забагато голосів", three.Act(0, "vote", new { card = 0, picks = new[] { others[0], others[1], Mine(three, 0)[0] } }).Message);

        var four = Table(4, options: new { rounds = "blitz" });
        WriteAll(four);
        four.Tick();
        Assert.Equal(3, Card(four).GetProperty("perVoter").GetInt32());
        var eight = Table(8, options: new { rounds = "blitz" });
        WriteAll(eight);
        eight.Tick();
        Assert.Equal(Dotepy.RankCount, Card(eight).GetProperty("perVoter").GetInt32());
    }

    [Fact]
    public void A_partial_final_ballot_counts_at_the_deadline_but_does_not_close_the_vote_early()
    {
        var h = Table(3, options: new { rounds = "blitz" });
        WriteAll(h);
        h.Tick();
        for (var s = 0; s < 3; s++) Assert.True(h.Act(s, "vote", new { card = 0, picks = new[] { FirstOther(h, s) } }).Ok);
        h.Tick(4);
        Assert.Equal("vote", Phase(h));                       // 🥇 є, 🥈 ще шукають
        Assert.Empty(Voted(h));
        Assert.All(V(h).GetProperty("players").EnumerateArray(), p => Assert.False(p.GetProperty("voted").GetBoolean()));
        var full = Enumerable.Range(0, 3).Where(i => !Mine(h, 0).Contains(i)).ToArray();
        Assert.True(h.Act(0, "vote", new { card = 0, picks = full }).Ok);
        Assert.Equal([0], Voted(h));
        UntilPhase(h, "reveal");                              // дедлайн: неповні бюлетені теж пішли в рахунок
        var medals = Answers(h).EnumerateArray().Sum(a => a.GetProperty("seat").ValueKind == JsonValueKind.Null ? 0 : 1);
        Assert.Equal(0, medals);                              // фінал розкривається по одній
        h.Tick(3 * Dotepy.FinalStepMs / Dotepy.TickMs);
        Assert.Equal(4, Answers(h).EnumerateArray().Sum(a => a.GetProperty("votes").GetArrayLength()));
    }

    [Fact]
    public void Guests_are_named_without_the_guest_prefix_in_glek_lines_but_fully_in_the_journal()
    {
        Assert.Equal("Оля", Dotepy.Spoken("гість Оля"));
        Assert.Equal("гість", Dotepy.Spoken("гість"));
        Assert.Equal("Петро", Dotepy.Spoken("Петро"));
        var services = new ServiceCollection();
        services.AddSingleton(new DotepyPrompts(Bank()));
        services.AddSingleton(new DotepySeen(Dotepy.SeenRing));
        var voice = new FakeVoice();
        services.AddSingleton<IDotepyVoice>(voice);
        var h = new RoomHarness("dotepy", null, 7, services.BuildServiceProvider());
        foreach (var n in new[] { "гість Оля", "гість Петро", "гість Ганна" }) Assert.True(h.Join(n).Ok);
        Assert.True(h.Start().Ok);
        Assert.Contains(voice.Prepared, p => p.Text == DotepyLines.Sweep("Оля"));
        Assert.DoesNotContain(voice.Prepared, p => p.Text.Contains("гість"));
        WriteAll(h);
        h.Tick();
        var target = Mine(h, 0)[0];
        h.Act(1, "vote", new { card = 0, picks = new[] { target } });
        h.Act(2, "vote", new { card = 0, picks = new[] { target } });
        h.Act(0, "vote", new { card = 0, picks = new[] { FirstOther(h, 0) } });
        UntilPhase(h, "reveal");
        Assert.Equal(DotepyLines.Sweep("Оля"), Say(h));
        h.Leave("гість Ганна");
        Assert.Equal("Дотепи: гравці розійшлись — попереду гість Оля", h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void Duplicate_too_many_or_own_picks_are_refused_with_the_spec_texts()
    {
        var h = Table(4, options: new { rounds = "blitz" });
        WriteAll(h);
        h.Tick();
        var own = Mine(h, 0)[0];
        var other = FirstOther(h, 0);
        var before = V(h, 0).GetRawText();
        Assert.Equal("Обери хоч один", h.Act(0, "vote", new { card = 0, picks = Array.Empty<int>() }).Message);
        Assert.Equal("Обери хоч один", h.Act(0, "vote", new { card = 0, picks = "1" }).Message);
        Assert.Equal("Обери хоч один", h.Act(0, "vote", new { card = 0, picks = new object[] { "1" } }).Message);
        Assert.Equal("Обери хоч один", h.Act(0, "vote", new { card = 0 }).Message);
        Assert.Equal("Забагато голосів", h.Act(0, "vote", new { card = 0, picks = new[] { 0, 1, 2, 3 } }).Message);
        Assert.Equal("Один дотеп — один голос", h.Act(0, "vote", new { card = 0, picks = new[] { other, other } }).Message);
        Assert.Equal("Такої відповіді нема", h.Act(0, "vote", new { card = 0, picks = new[] { 9 } }).Message);
        Assert.Equal("Такої відповіді нема", h.Act(0, "vote", new { card = 0, picks = new[] { -1 } }).Message);
        Assert.Equal("За себе не голосують", h.Act(0, "vote", new { card = 0, picks = new[] { other, own } }).Message);
        Assert.Equal(before, V(h, 0).GetRawText());
    }

    [Fact]
    public void Jury_vote_counts_once_per_nick_replaces_and_is_refused_outside_vote_phase()
    {
        var h = Table(3);
        var game = Game(h);
        ActResult Jury(string nick, int card, int pick)
        {
            lock (h.Room.Sync) return game.JuryVote(nick, card, pick);
        }
        Assert.Equal("Зараз не голосують", Jury("глядач", 0, 0).Message);
        WriteAll(h);
        h.Tick();
        var r = Jury("глядач", 0, 0);
        Assert.True(r.Ok);
        Assert.Equal("Голос публіки прийнято", r.Message);
        Assert.True(Jury("глядач", 0, 1).Ok);
        Assert.Equal(1, Card(h).GetProperty("juryVotes").GetInt32());
        Assert.True(Jury("сусід", 0, 1).Ok);
        Assert.Equal(2, Card(h).GetProperty("juryVotes").GetInt32());
        Assert.Equal("Ця картка вже пішла", Jury("глядач", 1, 0).Message);
        Assert.Equal("Такої відповіді нема", Jury("глядач", 0, 3).Message);
        VoteAll(h, s => [FirstOther(h, s)]);
        h.Tick();
        Assert.Equal("reveal", Phase(h));
        Assert.Equal(2, Answers(h)[1].GetProperty("jury").GetInt32());
        Assert.Equal(0, Answers(h)[0].GetProperty("jury").GetInt32());
        Assert.Equal("Зараз не голосують", Jury("глядач", 0, 0).Message);
    }

    [Fact]
    public void Jury_prize_goes_to_the_strictly_most_backed_answer_or_nobody_on_a_tie()
    {
        var h = Table(5);
        var game = Game(h);
        WriteAll(h);
        h.Tick();
        lock (h.Room.Sync)
        {
            game.JuryVote("а", 0, 0);
            game.JuryVote("б", 0, 0);
            game.JuryVote("в", 0, 1);
        }
        var v = Voters(h);
        h.Act(v[0], "vote", new { card = 0, picks = new[] { 0 } });
        h.Act(v[1], "vote", new { card = 0, picks = new[] { 1 } });
        h.Act(v[2], "vote", new { card = 0, picks = new[] { 1 } });
        h.Tick();
        var a = Answers(h);
        Assert.True(a[0].GetProperty("prize").GetBoolean());
        Assert.False(a[1].GetProperty("prize").GetBoolean());
        Assert.Equal(100 + Dotepy.JuryPrize, a[0].GetProperty("points").GetInt32());
        Assert.Equal(200, a[1].GetProperty("points").GetInt32());

        UntilPhase(h, "vote");
        lock (h.Room.Sync)
        {
            game.JuryVote("а", 1, 0);
            game.JuryVote("б", 1, 1);
        }
        VoteAll(h, _ => [0]);
        h.Tick();
        Assert.All(Answers(h).EnumerateArray(), x => Assert.False(x.GetProperty("prize").GetBoolean()));
    }

    [Fact]
    public void Jury_service_refuses_seated_nicks_non_watchers_and_floods()
    {
        var h = Table(3);
        var presence = new Presence();
        var jury = new DotepyJury(h.Rooms, presence, h.Clock);
        WriteAll(h);
        h.Tick();
        Assert.Equal("Спершу скажи, як тебе кликати", jury.Vote("гість", h.RoomId, 0, 0).Message);
        Assert.Equal("Спершу скажи, як тебе кликати", jury.Vote("", h.RoomId, 0, 0).Message);
        Assert.Equal("Такого столу вже нема", jury.Vote("Глядач", "nope", 0, 0).Message);
        Assert.Equal("Ти за столом — голосуй на картці", jury.Vote("оля", h.RoomId, 0, 0).Message);
        Assert.Equal("Спершу відкрий цей стіл", jury.Vote("Глядач", h.RoomId, 0, 0).Message);
        presence.Set("c1", "Глядач");
        Assert.Equal("Спершу відкрий цей стіл", jury.Vote("Глядач", h.RoomId, 0, 0).Message);
        h.Rooms.Watch(h.RoomId, "c1", "Глядач");
        Assert.Equal("Голос публіки прийнято", jury.Vote("Глядач", h.RoomId, 0, 0).Message);
        Assert.True(jury.Vote("глядач", h.RoomId, 0, 1).Ok);
        Assert.Equal("Не так швидко", jury.Vote("Глядач", h.RoomId, 0, 2).Message);
        Assert.Equal(1, Card(h).GetProperty("juryVotes").GetInt32());
        h.Clock.AdvanceMs(1000);
        Assert.Equal("Ця картка вже пішла", jury.Vote("Глядач", h.RoomId, 5, 0).Message);
        presence.Set("c2", "гість Оленка");
        h.Rooms.Watch(h.RoomId, "c2", "гість Оленка");
        Assert.True(jury.Vote("гість Оленка", h.RoomId, 0, 2).Ok);   // гість з ім'ям — як усі
        Assert.Equal(2, Card(h).GetProperty("juryVotes").GetInt32());
    }

    [Fact]
    public void Jury_votes_reach_the_players_on_the_next_tick()
    {
        var h = Table(3);
        WriteAll(h);
        h.Tick();
        var views = Count<RoomViews>(h);
        lock (h.Room.Sync) Game(h).JuryVote("глядач", 0, 0);
        h.Tick();
        Assert.Equal(views + 1, Count<RoomViews>(h));
    }

    // ======================================================================================
    // очки й кінець
    // ======================================================================================

    [Fact]
    public void Round_one_pays_100_per_vote_and_round_two_200()
    {
        var h = Table(5);
        WriteAll(h);
        h.Tick();
        var v = Voters(h);
        h.Act(v[0], "vote", new { card = 0, picks = new[] { 0 } });
        h.Act(v[1], "vote", new { card = 0, picks = new[] { 0 } });
        h.Act(v[2], "vote", new { card = 0, picks = new[] { 1 } });
        h.Tick();
        var a = Answers(h);
        Assert.Equal(200, a[0].GetProperty("points").GetInt32());
        Assert.Equal(100, a[1].GetProperty("points").GetInt32());
        Assert.Equal(200, Score(h, a[0].GetProperty("seat").GetInt32()));
        Assert.Equal(100, Score(h, a[1].GetProperty("seat").GetInt32()));

        Until(h, () =>
        {
            if (Phase(h) == "vote" && V(h).GetProperty("round").GetInt32() == 1 && Voted(h).Length < Voters(h).Length) VoteAll(h, _ => [0]);
            if (Phase(h) == "write" && V(h).GetProperty("round").GetInt32() == 2) WriteAll(h);
            return Phase(h) == "vote" && V(h).GetProperty("round").GetInt32() == 2;
        });
        var before = V(h).GetProperty("players").EnumerateArray().ToDictionary(p => p.GetProperty("seat").GetInt32(), p => p.GetProperty("score").GetInt64());
        v = Voters(h);
        var c = CardI(h);
        h.Act(v[0], "vote", new { card = c, picks = new[] { 1 } });
        h.Act(v[1], "vote", new { card = c, picks = new[] { 1 } });
        h.Act(v[2], "vote", new { card = c, picks = new[] { 0 } });
        h.Tick();
        a = Answers(h);
        Assert.Equal(400, a[1].GetProperty("points").GetInt32());
        Assert.Equal(200, a[0].GetProperty("points").GetInt32());
        var s1 = a[1].GetProperty("seat").GetInt32();
        Assert.Equal(before[s1] + 400, Score(h, s1));
    }

    [Fact]
    public void Sweep_needs_at_least_two_voters_all_for_one_pays_double_bonus_and_asks_the_achievement()
    {
        var h = Table(5);
        WriteAll(h);
        h.Tick();
        VoteAll(h, _ => [0]);
        h.Tick();
        var a = Answers(h)[0];
        var author = a.GetProperty("seat").GetInt32();
        Assert.Equal(3 * 100 + Dotepy.SweepBonus(1), a.GetProperty("points").GetInt32());
        Assert.Equal(0, Card(h).GetProperty("sweep").GetInt32());
        Assert.Equal(DotepyLines.Sweep(h.NickOf(author)), Say(h));
        Assert.Contains(h.Awards, x => x.Reason == "ach:dotepy-sweep" && x.Nick == h.NickOf(author));

        // лише один суддя — це не «Розгром», а просто голос
        var one = Table(5, seed: 9);
        var authors = Enumerable.Range(0, 5).Where(s => Tasks(one, s).Any(t => t.I == 0)).ToArray();
        var judges = Enumerable.Range(0, 5).Except(authors).ToArray();
        one.Leave(one.NickOf(judges[0]));
        one.Leave(one.NickOf(judges[1]));
        WriteAll(one);
        one.Clock.AdvanceMs(90_000);
        one.Tick();
        Assert.Equal(0, CardI(one));
        Assert.Equal([judges[2]], Voters(one));
        Assert.True(one.Act(judges[2], "vote", new { card = 0, picks = new[] { 0 } }).Ok);
        one.Tick();
        Assert.Equal(JsonValueKind.Null, Card(one).GetProperty("sweep").ValueKind);
        Assert.Equal(100, Answers(one)[0].GetProperty("points").GetInt32());
        Assert.DoesNotContain(one.Awards, x => x.Reason == "ach:dotepy-sweep");
    }

    [Fact]
    public void Sweep_in_all_mode_counts_everyone_but_the_author()
    {
        var h = Table(3);
        WriteAll(h);
        h.Tick();
        var target = Mine(h, 0)[0];
        Assert.True(h.Act(1, "vote", new { card = 0, picks = new[] { target } }).Ok);
        Assert.True(h.Act(2, "vote", new { card = 0, picks = new[] { target } }).Ok);
        Assert.True(h.Act(0, "vote", new { card = 0, picks = new[] { FirstOther(h, 0) } }).Ok);
        h.Tick();
        Assert.Equal(target, Card(h).GetProperty("sweep").GetInt32());
        Assert.Equal(200 + Dotepy.SweepBonus(1), Answers(h)[target].GetProperty("points").GetInt32());
    }

    [Fact]
    public void A_tied_duel_pays_both_by_their_votes_without_a_sweep()
    {
        var h = Table(6);
        WriteAll(h);
        h.Tick();
        var v = Voters(h);
        Assert.Equal(4, v.Length);
        h.Act(v[0], "vote", new { card = 0, picks = new[] { 0 } });
        h.Act(v[1], "vote", new { card = 0, picks = new[] { 0 } });
        h.Act(v[2], "vote", new { card = 0, picks = new[] { 1 } });
        h.Act(v[3], "vote", new { card = 0, picks = new[] { 1 } });
        h.Tick();
        Assert.Equal(200, Answers(h)[0].GetProperty("points").GetInt32());
        Assert.Equal(200, Answers(h)[1].GetProperty("points").GetInt32());
        Assert.Equal(JsonValueKind.Null, Card(h).GetProperty("sweep").ValueKind);
        Assert.Equal(DotepyLines.Tie, Say(h));
    }

    /// <summary>Фінал на чотирьох: місце s ранжує чужі відповіді так, що підсумок 1 → 900, 0 → 700, 2 → 500, 3 → 300.</summary>
    static RoomHarness FinalOfFour(out int[] mine)
    {
        var h = Table(4, options: new { rounds = "blitz" });
        WriteAll(h);
        h.Tick();
        var m = Enumerable.Range(0, 4).Select(s => Mine(h, s)[0]).ToArray();
        Assert.True(h.Act(0, "vote", new { card = 0, picks = new[] { m[1], m[2], m[3] } }).Ok);
        Assert.True(h.Act(1, "vote", new { card = 0, picks = new[] { m[0], m[2], m[3] } }).Ok);
        Assert.True(h.Act(2, "vote", new { card = 0, picks = new[] { m[1], m[0], m[3] } }).Ok);
        Assert.True(h.Act(3, "vote", new { card = 0, picks = new[] { m[1], m[0], m[2] } }).Ok);
        h.Tick();
        mine = m;
        return h;
    }

    [Fact]
    public void Final_ranks_pay_300_200_100_per_voter_and_reveal_from_last_to_first_every_2500_ms()
    {
        var h = FinalOfFour(out var m);
        Assert.Equal("reveal", Phase(h));
        Assert.Equal(0, Card(h).GetProperty("shown").GetInt32());
        Assert.All(Answers(h).EnumerateArray(), a => Assert.Equal(JsonValueKind.Null, a.GetProperty("seat").ValueKind));
        var expected = new (int Seat, int Points, int Rank)[] { (3, 300, 4), (2, 500, 3), (0, 700, 2), (1, 900, 1) };
        for (var k = 0; k < 4; k++)
        {
            h.Tick(Dotepy.FinalStepMs / Dotepy.TickMs - 1);
            Assert.Equal(k, Card(h).GetProperty("shown").GetInt32());
            h.Tick();
            Assert.Equal(k + 1, Card(h).GetProperty("shown").GetInt32());
            var a = Answers(h)[m[expected[k].Seat]];
            Assert.Equal(expected[k].Seat, a.GetProperty("seat").GetInt32());
            Assert.Equal(expected[k].Points, a.GetProperty("points").GetInt32());
            Assert.Equal(expected[k].Rank, a.GetProperty("rank").GetInt32());
        }
        Assert.Equal([3, 3, 3], Ints(Answers(h)[m[3]].GetProperty("medals")));
        Assert.Equal([1, 1, 1], Ints(Answers(h)[m[1]].GetProperty("medals")));
        Assert.Equal(DotepyLines.FinalWin("Петро"), Say(h));
        h.Tick(Dotepy.FinalHoldMs / Dotepy.TickMs);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
    }

    [Fact]
    public void Final_points_join_the_score_only_when_the_answer_is_shown()
    {
        var h = FinalOfFour(out _);
        Assert.All(Enumerable.Range(0, 4), s => Assert.Equal(0, Score(h, s)));
        h.Tick(Dotepy.FinalStepMs / Dotepy.TickMs);
        Assert.Equal(300, Score(h, 3));
        Assert.Equal(0, Score(h, 1));
        h.Tick(Dotepy.FinalStepMs / Dotepy.TickMs);
        Assert.Equal(500, Score(h, 2));
        Assert.Equal(0, Score(h, 0));
        h.Tick(2 * Dotepy.FinalStepMs / Dotepy.TickMs);
        Assert.Equal(700, Score(h, 0));
        Assert.Equal(900, Score(h, 1));
    }

    [Fact]
    public void Match_ends_with_leaders_as_winners_scores_events_and_the_journal_line()
    {
        var h = Table(4, seed: 21);
        PlayMatch(h);
        var result = V(h).GetProperty("result");
        var scores = Enumerable.Range(0, 4).Select(s => Score(h, s)).ToArray();
        var best = scores.Max();
        Assert.True(best > 0);
        var winners = Enumerable.Range(0, 4).Where(s => scores[s] == best).ToArray();
        Assert.Equal(winners, Ints(result.GetProperty("winners")));
        Assert.Equal(winners, h.Room.Result!.Winners);
        Assert.Equal(4, h.Scores.Count);
        Assert.All(h.Scores, e => Assert.Equal(scores[Array.IndexOf(Names, e.Nick)], (long)e.Score));
        var line = h.Outbox.OfType<Journal>().Last().Text;
        var order = Enumerable.Range(0, 4).OrderByDescending(s => scores[s]).ThenBy(s => s).Select(s => $"{Names[s]} {scores[s]}");
        Assert.StartsWith("Дотепи: " + string.Join(", ", order), line);
        Assert.Equal(Enumerable.Range(0, 4).Sum(s => scores[s]), Ints(result.GetProperty("scores")).Sum());
        Assert.Equal(winners.Length == 1 ? DotepyLines.GameWin(Names[winners[0]]) : DotepyLines.GameTie, Say(h));
    }

    [Fact]
    public void All_zero_scores_end_in_a_draw()
    {
        var h = Table(3, options: new { rounds = "blitz" });
        WriteAll(h);
        h.Tick();
        Until(h, () => h.Room.Status == RoomStatus.Finished);
        Assert.True(h.Room.Result!.Draw);
        Assert.Empty(V(h).GetProperty("result").GetProperty("winners").EnumerateArray());
        Assert.EndsWith("— нічия", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal(DotepyLines.GameTie, Say(h));
        Assert.DoesNotContain(h.Awards, a => a.Reason == "ach:dotepy-king");
    }

    [Fact]
    public void King_achievement_only_when_five_or_more_started()
    {
        var five = Table(5, options: new { rounds = "blitz" });
        PlayMatch(five);
        var winners = five.Room.Result!.Winners;
        Assert.NotEmpty(winners);
        foreach (var w in winners) Assert.Contains(five.Awards, a => a.Reason == "ach:dotepy-king" && a.Nick == Names[w]);

        var four = Table(4, options: new { rounds = "blitz" });
        PlayMatch(four);
        Assert.NotEmpty(four.Room.Result!.Winners);
        Assert.DoesNotContain(four.Awards, a => a.Reason == "ach:dotepy-king");
    }

    [Fact]
    public void Best_answer_of_the_round_and_top_three_of_the_match_are_the_highest_scoring()
    {
        var h = Table(5, seed: 33);
        var perRound = new Dictionary<int, int>();
        var all = new List<int>();
        var round = 1;
        for (var guard = 0; guard < 20_000 && h.Room.Status == RoomStatus.Playing; guard++)
        {
            var v = V(h);
            switch (v.GetProperty("phase").GetString())
            {
                case "write": WriteAll(h); break;
                case "vote" when Voted(h).Length < Voters(h).Length: VoteAll(h, s => [FirstOther(h, s)]); break;
                case "table":
                    var best = v.GetProperty("table").GetProperty("best");
                    Assert.Equal(perRound[round], best.GetProperty("points").GetInt32());
                    Assert.Equal(round, v.GetProperty("round").GetInt32());
                    var rows = v.GetProperty("table").GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("score").GetInt64()).ToArray();
                    Assert.Equal(rows.OrderByDescending(x => x), rows);
                    round++;
                    Until(h, () => Phase(h) != "table");
                    continue;
            }
            h.Tick();
            if (Phase(h) == "reveal" && V(h).GetProperty("card").GetProperty("shown").GetInt32() == Answers(h).GetArrayLength()
                && !V(h).GetProperty("final").GetBoolean())
            {
                var pts = Answers(h).EnumerateArray().Where(a => !a.GetProperty("stock").GetBoolean()).Select(a => a.GetProperty("points").GetInt32()).ToList();
                var key = V(h).GetProperty("round").GetInt32();
                var cardKey = key * 100 + CardI(h);
                if (!perRound.ContainsKey(-cardKey))
                {
                    perRound[-cardKey] = 1;
                    perRound[key] = Math.Max(perRound.GetValueOrDefault(key), pts.Max());
                    all.AddRange(pts.Where(p => p > 0));
                }
            }
        }
        var top = V(h).GetProperty("result").GetProperty("best").EnumerateArray().Select(b => b.GetProperty("points").GetInt32()).ToList();
        Assert.InRange(top.Count, 1, 3);
        Assert.Equal(top.OrderByDescending(x => x), top);
        Assert.True(top[0] >= all.Max());
    }

    [Fact]
    public void Rematch_gives_a_clean_state_rotated_seats_and_fresh_prompts()
    {
        var h = Table(3);
        var first = PlayMatch(h);
        var oldSeat0 = h.NickOf(0);
        var oldSeat1 = h.NickOf(1);
        Assert.True(h.Rematch().Ok, h.Reply.Message);
        Assert.Equal(oldSeat1, h.NickOf(0));
        Assert.NotEqual(oldSeat0, h.NickOf(0));
        var v = V(h);
        Assert.Equal("write", v.GetProperty("phase").GetString());
        Assert.Equal(1, v.GetProperty("round").GetInt32());
        Assert.All(v.GetProperty("players").EnumerateArray(), p =>
        {
            Assert.Equal(0, p.GetProperty("score").GetInt64());
            Assert.False(p.GetProperty("left").GetBoolean());
            Assert.Equal(h.NickOf(p.GetProperty("seat").GetInt32()), p.GetProperty("nick").GetString());
        });
        Assert.Equal(JsonValueKind.Null, v.GetProperty("result").ValueKind);
        Assert.Empty(first.Intersect(v.GetProperty("prompts").EnumerateArray().Select(p => p.GetString()!)));
    }

    [Fact]
    public void Leaving_player_gets_stock_answers_loses_the_vote_keeps_the_score_and_cannot_win()
    {
        var h = Table(5, seed: 44);
        var gone = h.NickOf(4);
        WriteAll(h);
        h.Tick();
        // раунд 1: усі голоси — за місце 4, де воно автор
        Until(h, () =>
        {
            if (Phase(h) == "vote" && Voted(h).Length < Voters(h).Length)
                VoteAll(h, s => Mine(h, 4) is { Length: > 0 } m ? [m[0]] : [0]);
            return Phase(h) == "write";
        });
        var kept = Score(h, 4);
        Assert.True(kept > 0);
        h.Leave(gone);
        Assert.True(V(h).GetProperty("players")[4].GetProperty("left").GetBoolean());
        Assert.Equal(kept, Score(h, 4));
        Assert.Equal(4, Present(h).Length);
        WriteAll(h);
        h.Tick();                     // той, хто пішов, не тримає написання
        Assert.Equal("vote", Phase(h));
        var stockSeen = 0;
        Until(h, () =>
        {
            if (Phase(h) == "vote")
            {
                Assert.DoesNotContain(4, Voters(h));
                if (Voted(h).Length < Voters(h).Length) VoteAll(h, s => [FirstOther(h, s)]);
            }
            if (Phase(h) == "reveal")
                foreach (var a in Answers(h).EnumerateArray())
                    if (a.GetProperty("seat").ValueKind == JsonValueKind.Number && a.GetProperty("seat").GetInt32() == 4)
                    {
                        Assert.True(a.GetProperty("stock").GetBoolean());
                        Assert.Equal(0, a.GetProperty("points").GetInt32());
                        stockSeen++;
                    }
            if (Phase(h) == "write") WriteAll(h);
            return h.Room.Status == RoomStatus.Finished;
        });
        Assert.True(stockSeen > 0);
        Assert.Equal(kept, Score(h, 4));
        Assert.DoesNotContain(4, h.Room.Result!.Winners);
        Assert.DoesNotContain(h.Scores, e => e.Nick == gone);
        Assert.DoesNotContain(gone, h.Outbox.OfType<Journal>().Last().Text);
    }

    [Fact]
    public void A_voter_who_leaves_mid_vote_no_longer_holds_the_card()
    {
        var h = Table(5);
        WriteAll(h);
        h.Tick();
        var v = Voters(h);
        h.Act(v[0], "vote", new { card = 0, picks = new[] { 0 } });
        h.Act(v[1], "vote", new { card = 0, picks = new[] { 1 } });
        h.Tick();
        Assert.Equal("vote", Phase(h));
        h.Leave(h.NickOf(v[2]));
        h.Tick();
        Assert.Equal("reveal", Phase(h));
    }

    [Fact]
    public void Fewer_than_three_present_ends_the_match_at_once_with_the_leader()
    {
        var h = Table(3);
        WriteAll(h);
        h.Tick();
        var target = Mine(h, 0)[0];
        h.Act(1, "vote", new { card = 0, picks = new[] { target } });
        h.Act(2, "vote", new { card = 0, picks = new[] { target } });
        h.Act(0, "vote", new { card = 0, picks = new[] { FirstOther(h, 0) } });
        h.Tick();
        Assert.True(Score(h, 0) > Score(h, 1));
        h.Leave(h.NickOf(2));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([0], h.Room.Result!.Winners);
        Assert.Equal("Дотепи: гравці розійшлись — попереду Оля", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal("done", Phase(h));
        Assert.Equal(DotepyLines.GameWin("Оля"), Say(h));       // не «Раунд перший…», що висів до виходу

        var zero = Table(3);
        zero.Leave(zero.NickOf(1));
        Assert.Equal(RoomStatus.Finished, zero.Room.Status);
        Assert.True(zero.Room.Result!.Draw);
        Assert.Equal("Дотепи: гравці розійшлись, партію не дограли", zero.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal(DotepyLines.Gone, Say(zero));
    }

    // ======================================================================================
    // види, приховане, дріт
    // ======================================================================================

    [Fact]
    public void Vote_phase_views_hide_authors_votes_points_and_others_picks_for_every_seat_and_the_spectator()
    {
        var h = Table(5);
        WriteAll(h);
        h.Tick();
        lock (h.Room.Sync) Game(h).JuryVote("глядач", 0, 1);
        var v = Voters(h);
        h.Act(v[0], "vote", new { card = 0, picks = new[] { 1 } });
        h.Tick();
        foreach (int? seat in new int?[] { null, 0, 1, 2, 3, 4 })
        {
            var view = V(h, seat);
            foreach (var a in view.GetProperty("card").GetProperty("answers").EnumerateArray())
                foreach (var hidden in new[] { "seat", "votes", "jury", "points", "rank", "medals", "prize" })
                    Assert.Equal(JsonValueKind.Null, a.GetProperty(hidden).ValueKind);
            Assert.Equal([v[0]], Ints(view.GetProperty("card").GetProperty("voted")));
            Assert.Equal(1, view.GetProperty("card").GetProperty("juryVotes").GetInt32());
            if (seat is { } s)
            {
                var picks = Ints(view.GetProperty("me").GetProperty("picks"));
                Assert.Equal(s == v[0] ? [1] : [], picks);
            }
        }
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("me").ValueKind);
    }

    [Fact]
    public void Write_phase_view_shows_only_my_tasks_and_no_drafts_of_others()
    {
        var h = Table(3);
        Assert.True(h.Act(0, "answer", new { i = 0, text = "СЕКРЕТ-А" }).Ok);
        h.Input(0, "draft", new { i = 1, text = "ЧЕРНЕТКА-Б" });
        h.Input(1, "draft", new { i = 0, text = "ЧУЖА-В" });
        foreach (int? seat in new int?[] { null, 1, 2 })
        {
            var text = Text(h, seat);
            Assert.DoesNotContain("СЕКРЕТ-А", text);
            Assert.DoesNotContain("ЧЕРНЕТКА-Б", text);
        }
        var mine = Text(h, 0);
        Assert.Contains("СЕКРЕТ-А", mine);
        Assert.Contains("ЧЕРНЕТКА-Б", mine);
        Assert.DoesNotContain("ЧУЖА-В", mine);
        Assert.Equal(2, V(h, 0).GetProperty("me").GetProperty("tasks").GetArrayLength());
        Assert.Equal(2, V(h).GetProperty("prompts").GetArrayLength());
        Assert.True(V(h).GetProperty("players")[0].GetProperty("ready").GetBoolean() == false);
    }

    static string[] Keys(JsonElement e) => [.. e.EnumerateObject().Select(p => p.Name)];

    [Fact]
    public void View_shape_on_the_wire_matches_the_spec_example_keys()
    {
        var h = Table(5);
        var top = new[] { "phase", "round", "rounds", "final", "mode", "endsAt", "totalMs", "waiting", "voice", "players", "prompts", "me", "card", "say", "table", "result" };
        Assert.Equal(top, Keys(V(h, 2)));
        Assert.Equal(top, Keys(V(h)));
        Assert.Equal(["seat", "nick", "score", "ready", "voted", "left"], Keys(V(h).GetProperty("players")[0]));
        Assert.Equal(["tasks", "mine", "voter", "picks"], Keys(V(h, 2).GetProperty("me")));
        Assert.Equal(["i", "prompt", "text", "done"], Keys(V(h, 2).GetProperty("me").GetProperty("tasks")[0]));
        Assert.Equal(["id", "text", "url", "seconds"], Keys(V(h).GetProperty("say")));
        Assert.Equal(JsonValueKind.String, V(h).GetProperty("endsAt").ValueKind);
        Assert.Equal("ostap", V(h).GetProperty("voice").GetString());

        WriteAll(h);
        h.Tick();
        var card = Card(h);
        Assert.Equal(["i", "of", "prompt", "answers", "voters", "voted", "juryVotes", "perVoter", "ranked", "sweep", "shown"], Keys(card));
        Assert.Equal(["text", "stock", "seat", "votes", "medals", "jury", "points", "rank", "prize"], Keys(card.GetProperty("answers")[0]));
        Assert.Equal(5, card.GetProperty("of").GetInt32());
        Assert.Equal(2, card.GetProperty("shown").GetInt32());
        Assert.Empty(V(h).GetProperty("prompts").EnumerateArray());
        Assert.Empty(V(h, 0).GetProperty("me").GetProperty("tasks").EnumerateArray());

        Until(h, () =>
        {
            if (Phase(h) == "vote" && Voted(h).Length < Voters(h).Length) VoteAll(h, _ => [0]);
            return Phase(h) == "table";
        });
        var table = V(h).GetProperty("table");
        Assert.Equal(["rows", "best"], Keys(table));
        Assert.Equal(["seat", "score", "delta"], Keys(table.GetProperty("rows")[0]));
        Assert.Equal(["prompt", "text", "seat", "points"], Keys(table.GetProperty("best")));

        PlayMatch(h);
        var result = V(h).GetProperty("result");
        Assert.Equal(["winners", "scores", "best"], Keys(result));
        Assert.Equal(Dotepy.MaxSeats, result.GetProperty("scores").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("card").ValueKind);
    }

    /// <summary>
    /// Фінал на восьми в розкритті, відповіді по 80 кириличних знаків. SignalR шле кирилицю як <c>\uXXXX</c> (×6 байтів),
    /// тож міряємо обидва: у UTF-8 без екранування (скільки це насправді тексту) і так, як іде на дріт.
    /// </summary>
    [Fact]
    public void Final_view_at_eight_players_is_under_4_kb()
    {
        var h = Table(8, options: new { rounds = "blitz" });
        WriteAll(h, (s, _) => new string((char)('а' + s), Dotepy.MaxAnswer));
        h.Tick();
        VoteAll(h, s => [.. Enumerable.Range(0, 8).Where(i => !Mine(h, s).Contains(i)).Take(3)]);
        h.Tick();
        h.Tick(8 * Dotepy.FinalStepMs / Dotepy.TickMs);
        Assert.Equal(8, Card(h).GetProperty("shown").GetInt32());
        object view;
        lock (h.Room.Sync) view = Game(h).View(3);
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(view, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var wire = System.Text.Encoding.UTF8.GetByteCount(Views.Text(view));
        Assert.True(utf8.Length < 4096, $"вид фіналу на восьми: {utf8.Length} Б");
        Assert.True(wire < 12 * 1024, $"на дроті: {wire} Б");
    }

    [Fact]
    public void Idle_ticks_send_nothing_and_changes_send_views_without_frames()
    {
        var h = Table(3);
        Assert.Null(Game(h).Frame());
        var views = Count<RoomViews>(h);
        h.Tick(8);
        Assert.Equal(views, Count<RoomViews>(h));
        Assert.Equal(0, Count<RoomFrame>(h));
        Assert.True(h.Act(0, "answer", new { i = 0, text = "є" }).Ok);
        Assert.Equal(views, Count<RoomViews>(h));   // реалтайм-кімната з Act видів не шле
        h.Tick();
        Assert.Equal(views + 1, Count<RoomViews>(h));
        h.Tick(3);
        Assert.Equal(views + 1, Count<RoomViews>(h));
        Assert.Equal(0, Count<RoomFrame>(h));
        TickResult r;
        lock (h.Room.Sync) r = Game(h).Tick();
        Assert.Equal(TickResult.None, r);
    }

    [Fact]
    public void The_server_accepts_exactly_what_the_module_sends()
    {
        var h = Table(5);
        var i = Tasks(h, 0)[0].I;
        h.Input(0, "draft", Views.Payload(new { i, text = "чернетка" }));
        Assert.Equal("чернетка", V(h, 0).GetProperty("me").GetProperty("tasks")[0].GetProperty("text").GetString());
        Assert.True(h.Act(0, "answer", new { i, text = "дотеп" }).Ok);
        Assert.True(h.Act(0, "edit", new { i }).Ok);
        WriteAll(h);
        h.Tick();
        var voter = Voters(h)[0];
        Assert.True(h.Act(voter, "vote", new { card = CardI(h), picks = new[] { 1 } }).Ok, h.Reply.Message);

        var f = Table(4, options: new { rounds = "blitz" });
        WriteAll(f);
        f.Tick();
        var seat = Enumerable.Range(0, 4).First(s => Mine(f, s)[0] is 1 or 3);
        Assert.True(f.Act(seat, "vote", new { card = 0, picks = new[] { 2, 0 } }).Ok, f.Reply.Message);
        Assert.Equal([2, 0], Ints(V(f, seat).GetProperty("me").GetProperty("picks")));
    }

    // ======================================================================================
    // голос Глека й детермінізм
    // ======================================================================================

    static List<string> Script(int seed)
    {
        var h = Table(5, seed: seed);
        var log = new List<string>();
        void Snap() { log.Add(V(h).GetRawText()); log.Add(V(h, 0).GetRawText()); log.Add(V(h, 3).GetRawText()); }
        Snap();
        for (var guard = 0; guard < 20_000 && h.Room.Status == RoomStatus.Playing; guard++)
        {
            var phase = Phase(h);
            if (phase == "write") WriteAll(h, skip: [2]);
            else if (phase == "vote" && Voted(h).Length < Voters(h).Length) VoteAll(h, s => [FirstOther(h, s)]);
            h.Tick();
            Snap();
        }
        return log;
    }

    [Fact]
    public void Same_seed_and_actions_give_identical_view_json()
    {
        var a = Script(11);
        var b = Script(11);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a, b);
        Assert.NotEqual(a, Script(12));
    }

    [Fact]
    public void Start_prepares_round_intros_and_named_verdicts_and_card_clips_go_urgent_after_writing()
    {
        var voice = new FakeVoice();
        var h = Table(3, voice: voice);
        Assert.Contains(voice.Prepared, p => p.Text == DotepyLines.Round1 && p.Urgent);
        foreach (var line in DotepyLines.Pure()) Assert.Contains(voice.Prepared, p => p.Text == line);
        foreach (var nick in Names.Take(3))
            foreach (var line in DotepyLines.Named(nick)) Assert.Contains(voice.Prepared, p => p.Text == line && !p.Urgent);
        Assert.All(voice.Prepared, p => Assert.Equal("ostap", p.Voice));
        Assert.Equal(DotepyLines.Round1, Say(h));

        voice.Prepared.Clear();
        WriteAll(h);
        h.Tick();
        var cards = voice.Prepared.Where(p => p.Urgent).ToList();
        Assert.Equal(2, cards.Select(c => c.Text).Distinct().Count());
        Assert.StartsWith(V(h).GetProperty("card").GetProperty("prompt").GetString()!, cards[0].Text);
        Assert.Contains("Перша: ", cards[0].Text);
        Assert.Contains("Третя: ", cards[0].Text);
        var say = V(h).GetProperty("say");
        Assert.Equal(cards[0].Text, say.GetProperty("text").GetString());
        Assert.StartsWith("/api/games/svoya/tts/", say.GetProperty("url").GetString());

        var polina = new FakeVoice();
        Table(3, options: new { voice = "polina" }, voice: polina);
        Assert.All(polina.Prepared, p => Assert.Equal("polina", p.Voice));
    }

    [Fact]
    public void A_card_goes_to_the_voice_queue_as_soon_as_all_its_authors_have_answered()
    {
        var voice = new FakeVoice(readyAfter: -1);
        var h = Table(5, voice: voice);
        voice.Prepared.Clear();
        var authors = Enumerable.Range(0, 5).Where(s => Tasks(h, s).Any(t => t.I == 0)).ToArray();
        Assert.True(h.Act(authors[0], "answer", new { i = 0, text = "Перший дотеп" }).Ok);
        Assert.DoesNotContain(voice.Prepared, p => p.Text.Contains("Перший дотеп"));
        Assert.True(h.Act(authors[1], "answer", new { i = 0, text = "Другий дотеп" }).Ok);
        var early = Assert.Single(voice.Prepared, p => p.Urgent);
        Assert.Equal("write", Phase(h));
        Assert.Contains("Перший дотеп", early.Text);
        Assert.Contains("Другий дотеп", early.Text);
        WriteAll(h);
        h.Tick();
        Assert.Equal(0, CardI(h));
        Assert.Contains(voice.Prepared, p => p.Text == early.Text && p.Urgent);   // те саме читання — той самий кліп
        h.Tick(Dotepy.VoiceWaitMs / Dotepy.TickMs);
        Assert.Equal(early.Text, Say(h));                                        // і саме його Глек читає на картці
    }

    [Fact]
    public void A_missing_clip_waits_at_most_3_s_then_goes_on_without_url()
    {
        var h = Table(3, voice: new FakeVoice(readyAfter: -1));
        WriteAll(h);
        h.Tick();
        var v = V(h);
        Assert.Equal("vote", v.GetProperty("phase").GetString());
        Assert.True(v.GetProperty("waiting").GetBoolean());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("endsAt").ValueKind);
        Assert.True(h.Act(0, "vote", new { card = 0, picks = new[] { FirstOther(h, 0) } }).Ok);   // голосувати вже можна
        h.Tick(Dotepy.VoiceWaitMs / Dotepy.TickMs - 1);
        Assert.True(V(h).GetProperty("waiting").GetBoolean());
        h.Tick();
        v = V(h);
        Assert.False(v.GetProperty("waiting").GetBoolean());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("say").GetProperty("url").ValueKind);
        Assert.Equal(JsonValueKind.String, v.GetProperty("endsAt").ValueKind);

        // кліп доспів на третьому запиті — голосування відкривається з голосом
        var late = Table(3, voice: new FakeVoice(readyAfter: 3));
        WriteAll(late);
        late.Tick();
        Assert.True(V(late).GetProperty("waiting").GetBoolean());
        late.Tick(2);
        Assert.True(V(late).GetProperty("waiting").GetBoolean());
        late.Tick();
        Assert.False(V(late).GetProperty("waiting").GetBoolean());
        Assert.Equal(JsonValueKind.String, V(late).GetProperty("say").GetProperty("url").ValueKind);
    }

    [Fact]
    public void A_long_final_reading_waits_longer_but_never_more_than_6_s()
    {
        Assert.Equal(Dotepy.VoiceWaitMs, Dotepy.VoiceWait("коротко"));
        Assert.Equal(Dotepy.MaxVoiceWaitMs, Dotepy.VoiceWait(new string('а', 900)));
        var h = Table(8, options: new { rounds = "blitz" }, voice: new FakeVoice(readyAfter: -1));
        WriteAll(h, (s, _) => new string((char)('а' + s), Dotepy.MaxAnswer));
        h.Tick();
        Assert.True(V(h).GetProperty("waiting").GetBoolean());
        h.Tick(Dotepy.VoiceWaitMs / Dotepy.TickMs);
        Assert.True(V(h).GetProperty("waiting").GetBoolean());       // 3 с минуло — довгий фінал ще чекаємо
        h.Tick((Dotepy.MaxVoiceWaitMs - Dotepy.VoiceWaitMs) / Dotepy.TickMs);
        Assert.False(V(h).GetProperty("waiting").GetBoolean());      // а 6 с — стеля
    }

    [Fact]
    public void Top_three_of_the_match_come_from_different_prompts_when_possible()
    {
        var h = Table(4, options: new { rounds = "short" }, seed: 5);
        PlayMatch(h, s => Ballot(h, s));
        var best = V(h).GetProperty("result").GetProperty("best").EnumerateArray().ToList();
        Assert.Equal(3, best.Count);
        Assert.Equal(3, best.Select(b => b.GetProperty("prompt").GetString()).Distinct().Count());
        var points = best.Select(b => b.GetProperty("points").GetInt32()).ToList();
        Assert.Equal(points.OrderByDescending(p => p), points);
    }

    [Fact]
    public void Voice_none_never_waits_and_never_prepares()
    {
        var voice = new FakeVoice(readyAfter: -1);
        var h = Table(3, options: new { voice = "none" }, voice: voice);
        WriteAll(h);
        h.Tick();
        Assert.Empty(voice.Prepared);
        Assert.False(V(h).GetProperty("waiting").GetBoolean());
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("say").GetProperty("url").ValueKind);
        Assert.Equal("none", V(h).GetProperty("voice").GetString());

        // голос вимкнений на сервері — теж без очікувань
        var off = Table(3, voice: new FakeVoice(enabled: false, readyAfter: -1));
        WriteAll(off);
        off.Tick();
        Assert.False(V(off).GetProperty("waiting").GetBoolean());
    }

    [Fact]
    public void Voting_does_not_cut_the_reading_short_when_everyone_is_quick()
    {
        var h = Table(5, voice: new FakeVoice());
        WriteAll(h);
        h.Tick();
        var seconds = V(h).GetProperty("say").GetProperty("seconds").GetDouble();
        Assert.True(seconds > 1);
        VoteAll(h, _ => [0]);
        h.Tick();
        Assert.Equal("vote", Phase(h));            // Глек ще читає — чекаємо
        Until(h, () => Phase(h) == "reveal", max: (int)(seconds * 1000 / Dotepy.TickMs) + 2);
    }

    [Fact]
    public void Tts_text_drops_emoji_and_keeps_letters_digits_and_punctuation()
    {
        Assert.Equal("Так так! 100 «дурня» — ой… (так: так; ні, п'ять) глек", DotepyVoice.Clean("Так🔥так! 100% «дурня» — ой… 😂 (так: так; ні, п'ять) #глек"));
        Assert.Equal("без слів", DotepyVoice.Clean("😂😂 🔥"));
        Assert.Equal("без слів", DotepyVoice.Clean(""));
        Assert.Equal("без слів", DotepyVoice.Clean("…!"));
        Assert.Equal("Ґава їсть", DotepyVoice.Clean("  Ґава\t\n їсть 👨‍👩‍👧 "));
        Assert.Equal("Хвиля «Дощ у сараї FM». Перша: а. Друга: б!",
            DotepyLines.Card("Хвиля «Дощ у сараї FM»", ["а", "б!"]));
        Assert.Equal("Якщо півень заспівав опівночі, то… Перша: так. Друга: ні.",
            DotepyLines.Card("Якщо півень заспівав опівночі, то…", ["так", "ні"]));
        Assert.Equal("Восьма", DotepyLines.Ordinal(7));
    }

    [Fact]
    public void Answer_text_keeps_emoji_whole_but_drops_control_characters()
    {
        Assert.Equal("кіт 🐈 на даху", Dotepy.Clean("кіт\u0000 🐈‎ на\r\nдаху "));
        var h = Table(3);
        h.Input(0, "draft", new { i = 0, text = new string('а', Dotepy.MaxAnswer - 1) + "😂" });
        var draft = V(h, 0).GetProperty("me").GetProperty("tasks")[0].GetProperty("text").GetString()!;
        Assert.Equal(Dotepy.MaxAnswer - 1, draft.Length);   // емодзі навпіл не ріжемо
    }

    [Fact]
    public void Verdict_line_matches_the_outcome()
    {
        // Win
        var win = Table(5);
        WriteAll(win);
        win.Tick();
        var v = Voters(win);
        win.Act(v[0], "vote", new { card = 0, picks = new[] { 0 } });
        win.Act(v[1], "vote", new { card = 0, picks = new[] { 0 } });
        win.Act(v[2], "vote", new { card = 0, picks = new[] { 1 } });
        win.Tick();
        Assert.Equal(DotepyLines.Win(win.NickOf(Answers(win)[0].GetProperty("seat").GetInt32())), Say(win));

        // Silence
        var silence = Table(3);
        WriteAll(silence);
        silence.Tick();
        UntilPhase(silence, "reveal");
        Assert.Equal(DotepyLines.Silence, Say(silence));

        // FinalWin і GameWin, Sweep, Tie, StockWin, GameTie — у своїх тестах вище; тут — що вердикт завжди нова репліка
        var ids = new HashSet<int>();
        var h = Table(3, options: new { rounds = "blitz" });
        WriteAll(h);
        h.Tick();
        ids.Add(V(h).GetProperty("say").GetProperty("id").GetInt32());
        VoteAll(h, s => [FirstOther(h, s)]);
        Until(h, () => h.Room.Status == RoomStatus.Finished);
        ids.Add(V(h).GetProperty("say").GetProperty("id").GetInt32());
        Assert.Equal(2, ids.Count);
        Assert.StartsWith("Перемагає ", Say(h));
    }

    [Fact]
    public void Say_ids_keep_growing_across_a_rematch()
    {
        var h = Table(3, options: new { rounds = "blitz" });
        PlayMatch(h);
        var last = V(h).GetProperty("say").GetProperty("id").GetInt32();
        Assert.True(h.Rematch().Ok);
        Assert.True(V(h).GetProperty("say").GetProperty("id").GetInt32() > last);
    }

    [Fact]
    public void Lobby_view_is_empty_but_well_formed()
    {
        var h = Table(3, start: false);
        var v = V(h, 0);
        Assert.Equal("lobby", v.GetProperty("phase").GetString());
        Assert.Empty(v.GetProperty("players").EnumerateArray());
        Assert.Empty(v.GetProperty("me").GetProperty("tasks").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, v.GetProperty("card").ValueKind);
    }
}

/// <summary>Швидкодія «Дотепів»: окремо й без сусідів, бо міряє стінним годинником (<see cref="SerialPerf"/>).</summary>
[Collection(SerialPerf.Name)]
public class DotepyPerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Perf_3000_ticks_of_a_full_match_at_eight_players_take_under_one_second()
    {
        var h = DotepyTests.Table(8, voice: new DotepyTests.FakeVoice());
        var game = DotepyTests.Game(h);
        var tick = new Stopwatch();
        var views = new Stopwatch();
        var ticks = 0;
        var viewTicks = 0;
        long bytes = 0;
        var maxWire = 0;
        var maxUtf8 = 0;
        var relaxed = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        while (h.Room.Status == RoomStatus.Playing && ticks < 3000)
        {
            var phase = DotepyTests.Phase(h);
            if (phase == "write") DotepyTests.WriteAll(h, (s, i) => $"Дотеп місця {s} на завдання {i}: про глек і кота");
            else if (phase == "vote" && DotepyTests.Voted(h).Length < DotepyTests.Voters(h).Length)
                DotepyTests.VoteAll(h, s => DotepyTests.Ballot(h, s));
            var before = h.Outbox.Count;
            tick.Start();
            h.Tick();
            tick.Stop();
            ticks++;
            if (h.Outbox.Skip(before).OfType<RoomViews>().Any())
            {
                // як Broadcaster: вид кожного місця й глядача, серіалізований
                views.Start();
                lock (h.Room.Sync)
                {
                    for (var s = 0; s < 8; s++)
                    {
                        var wire = Views.Text(game.View(s)).Length;       // як шле SignalR: кирилиця екранована, тож це ASCII
                        bytes += wire;
                        maxWire = Math.Max(maxWire, wire);
                    }
                    bytes += Views.Text(game.View(null)).Length;
                    maxUtf8 = Math.Max(maxUtf8, JsonSerializer.SerializeToUtf8Bytes(game.View(0), relaxed).Length);
                }
                views.Stop();
                viewTicks++;
            }
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        var matchTicks = ticks;
        var matchMs = tick.Elapsed.TotalMilliseconds;
        // решта до 3000 — холості тики дограної партії (так тикав би стіл, якби каркас не зупинявся)
        var idle = Stopwatch.StartNew();
        for (; ticks < 3000; ticks++)
            lock (h.Room.Sync) game.Tick();
        idle.Stop();
        var total = matchMs + idle.Elapsed.TotalMilliseconds;
        output.WriteLine($"партія на восьми: {matchTicks} тиків, середній тик {matchMs / matchTicks:0.0000} мс; " +
            $"розсилок видів {viewTicks}, 9 видів + серіалізація в середньому {views.Elapsed.TotalMilliseconds / Math.Max(1, viewTicks):0.000} мс, " +
            $"{bytes / Math.Max(1, viewTicks * 9)} Б на вид (найбільший {maxWire} Б на дроті, {maxUtf8} Б у UTF-8); 3000 тиків разом — {total:0.0} мс");
        Assert.True(total < 1000, $"3000 тиків — {total:0.0} мс");
        Assert.True(matchMs / matchTicks < 0.25, $"середній тик {matchMs / matchTicks:0.0000} мс");
    }
}
