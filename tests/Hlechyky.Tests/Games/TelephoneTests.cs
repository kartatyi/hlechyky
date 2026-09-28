using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Зіпсований телефон (specs/telephone.md): фраза → малюнок → опис → … по колу, потім показ і ❤.
/// </summary>
public class TelephoneTests
{
    static readonly TelephonePhrases Phrases = new(["кіт на даху"]);

    static RoomHarness Table(int players, object? options = null)
    {
        var h = new RoomHarness("telephone", options: options, seed: 5, services: RoomHarness.WithService(Phrases));
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Іван", "Марта" }.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static JsonElement Task(RoomHarness h, int seat) => h.View(seat).GetProperty("task");
    static string Kind(RoomHarness h, int seat) => Task(h, seat).GetProperty("kind").GetString()!;
    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static int Step(RoomHarness h) => h.View(null).GetProperty("step").GetInt32();

    static void Write(RoomHarness h, int seat, string text) => Assert.True(h.Act(seat, "done", new { text }).Ok);

    static void DrawAndSubmit(RoomHarness h, int seat)
    {
        h.Input(seat, "draw", new { s = 1, c = 1, w = 8, p = new[] { 10, 10, seat * 100, 300 } });
        Assert.True(h.Act(seat, "done", new { n = 1 }).Ok);
    }

    /// <summary>Усі за столом здають своє завдання, і тик переводить на наступний крок.</summary>
    static void EveryoneSubmits(RoomHarness h, int players)
    {
        for (var s = 0; s < players; s++)
        {
            if (Kind(h, s) == Telephone.Draw) DrawAndSubmit(h, s);
            else Write(h, s, $"{Kind(h, s)} від {s} крок {Step(h)}");
        }
        h.Tick();
    }

    static void Next(RoomHarness h, int seat = 0)
    {
        h.Clock.AdvanceMs(Telephone.NextEveryMs);
        Assert.True(h.Act(seat, "next").Ok);
        h.Tick();
    }

    // ---------------------------------------------------------------- кроки

    [Fact]
    public void Everyone_starts_by_writing_a_phrase_with_ideas_to_roll()
    {
        var h = Table(3);
        Assert.Equal("step", Phase(h));
        for (var s = 0; s < 3; s++)
        {
            Assert.Equal(Telephone.Phrase, Kind(h, s));
            Assert.Equal(JsonValueKind.Null, Task(h, s).GetProperty("prompt").ValueKind);
            Assert.Contains("кіт на даху", Task(h, s).GetProperty("ideas").EnumerateArray().Select(e => e.GetString()));
        }
        Assert.Equal(JsonValueKind.Null, h.View(null).GetProperty("task").ValueKind);
        Assert.Equal(3, h.View(null).GetProperty("steps").GetInt32());
    }

    [Fact]
    public void The_next_player_draws_your_phrase_and_nobody_else_sees_it()
    {
        var h = Table(3);
        Write(h, 0, "слон на роликах");
        Write(h, 1, "бабуся в космосі");
        Write(h, 2, "борщ з кавуном");
        h.Tick();

        Assert.Equal(2, Step(h));
        // Петро (місце 1) малює ланцюжок Олі (0), Ганна — Петрів, Оля — Ганнин
        Assert.Equal(Telephone.Draw, Kind(h, 1));
        Assert.Equal("слон на роликах", Task(h, 1).GetProperty("prompt").GetProperty("text").GetString());
        Assert.Equal("бабуся в космосі", Task(h, 2).GetProperty("prompt").GetProperty("text").GetString());
        Assert.Equal("борщ з кавуном", Task(h, 0).GetProperty("prompt").GetProperty("text").GetString());
        Assert.DoesNotContain("слон на роликах", h.View(2).GetRawText());
        Assert.DoesNotContain("слон на роликах", h.View(null).GetRawText());
    }

    [Fact]
    public void After_a_drawing_comes_a_description_of_that_drawing()
    {
        var h = Table(3);
        EveryoneSubmits(h, 3);
        EveryoneSubmits(h, 3);

        Assert.Equal(3, Step(h));
        Assert.Equal(Telephone.Describe, Kind(h, 2));
        var prompt = h.Snapshot(2).GetProperty("task").GetProperty("prompt");
        Assert.Equal("drawing", prompt.GetProperty("kind").GetString());
        // Ганна (2) описує ланцюжок Олі — його малював Петро (1)
        Assert.Equal(100, prompt.GetProperty("ops")[0][6].GetInt32());
    }

    [Fact]
    public void A_step_waits_until_everyone_submits()
    {
        var h = Table(3);
        Write(h, 0, "раз");
        Write(h, 1, "два");
        h.Tick(5);
        Assert.Equal(1, Step(h));
        Assert.Equal([2], h.View(null).GetProperty("waiting").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void Edit_takes_a_submission_back()
    {
        var h = Table(3);
        Write(h, 0, "раз");
        Assert.True(h.Act(0, "edit").Ok);
        Write(h, 1, "два");
        Write(h, 2, "три");
        h.Tick();
        Assert.Equal(1, Step(h));
        Assert.False(Task(h, 0).GetProperty("ready").GetBoolean());
    }

    [Fact]
    public void An_empty_phrase_cannot_be_submitted()
    {
        var h = Table(3);
        Assert.False(h.Act(0, "done", new { text = "   " }).Ok);
    }

    [Fact]
    public void Time_running_out_fills_in_what_was_missing()
    {
        var h = Table(3, new { tempo = "fast" });
        h.Input(0, "text", new { text = "чернетка Олі" });
        h.Tick(40_000 / Telephone.TickMs + 1);

        Assert.Equal(2, Step(h));
        Assert.Equal("чернетка Олі", Task(h, 1).GetProperty("prompt").GetProperty("text").GetString());
        Assert.Equal("кіт на даху", Task(h, 2).GetProperty("prompt").GetProperty("text").GetString());   // Петро нічого не написав

        // малюнок, який не здали, все одно йде далі таким, яким був
        h.Input(1, "draw", new { s = 1, c = 1, w = 8, p = new[] { 1, 1, 2, 2 } });
        h.Tick(60_000 / Telephone.TickMs + 1);
        Assert.Equal(1, h.Snapshot(2).GetProperty("task").GetProperty("prompt").GetProperty("ops").GetArrayLength());
        Assert.Equal(1, Task(h, 2).GetProperty("prompt").GetProperty("n").GetInt32());
    }

    [Fact]
    public void A_drawing_that_did_not_fully_arrive_is_not_accepted()
    {
        var h = Table(3);
        EveryoneSubmits(h, 3);
        h.Input(1, "draw", new { s = 1, c = 1, w = 8, p = new[] { 1, 1, 2, 2 } });
        var r = h.Act(1, "done", new { n = 2 });
        Assert.False(r.Ok);
        Assert.Contains("не цілком", r.Message);
    }

    [Fact]
    public void Only_the_drawer_of_this_step_can_draw()
    {
        var h = Table(3);
        h.Input(0, "draw", new { s = 1, c = 1, w = 8, p = new[] { 1, 1, 2, 2 } });   // зараз фраза, не малюнок
        EveryoneSubmits(h, 3);
        Assert.Equal(0, Task(h, 0).GetProperty("n").GetInt32());
    }

    [Fact]
    public void Steps_option_cuts_the_chain_short()
    {
        var h = Table(5, new { steps = "4" });
        Assert.Equal(4, h.View(null).GetProperty("steps").GetInt32());
    }

    // ---------------------------------------------------------------- показ

    static RoomHarness Revealing()
    {
        var h = Table(3);
        for (var i = 0; i < 3; i++) EveryoneSubmits(h, 3);
        return h;
    }

    [Fact]
    public void After_the_last_step_chains_are_revealed_one_entry_at_a_time()
    {
        var h = Revealing();
        Assert.Equal("reveal", Phase(h));
        var r = h.View(1).GetProperty("reveal");
        Assert.Equal(0, r.GetProperty("owner").GetInt32());
        Assert.Equal(3, r.GetProperty("total").GetInt32());
        Assert.Equal(1, r.GetProperty("entries").GetArrayLength());

        Next(h);
        Next(h);
        var entries = h.View(1).GetProperty("reveal").GetProperty("entries");
        Assert.Equal(["text", "drawing", "text"], entries.EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal([0, 1, 2], entries.EnumerateArray().Select(e => e.GetProperty("seat").GetInt32()));

        Next(h);
        Assert.Equal(1, h.View(1).GetProperty("reveal").GetProperty("owner").GetInt32());
    }

    [Fact]
    public void A_double_click_on_next_turns_only_one_page()
    {
        var h = Revealing();
        h.Clock.AdvanceMs(Telephone.NextEveryMs);
        h.Act(0, "next");
        h.Act(1, "next");
        Assert.Equal(2, h.View(0).GetProperty("reveal").GetProperty("shown").GetInt32());
    }

    [Fact]
    public void Likes_go_to_the_author_and_toggle()
    {
        var h = Revealing();
        Assert.True(h.Act(1, "like", new { chain = 0, index = 0 }).Ok);
        Assert.True(h.Act(2, "like", new { chain = 0, index = 0 }).Ok);
        Assert.Equal(2, h.View(null).GetProperty("likes")[0].GetInt32());
        Assert.True(h.View(1).GetProperty("reveal").GetProperty("entries")[0].GetProperty("liked").GetBoolean());

        Assert.True(h.Act(2, "like", new { chain = 0, index = 0 }).Ok);
        Assert.Equal(1, h.View(null).GetProperty("likes")[0].GetInt32());
    }

    [Fact]
    public void A_like_travels_in_a_small_frame_not_in_full_views()
    {
        // прохід 28.09: кожне ❤ розсилало кожному повний вид з усіма малюнками ланцюжка
        var h = Revealing();
        h.Clock.AdvanceMs(Telephone.NextEveryMs);
        h.Act(0, "next");
        h.Tick();
        h.Outbox.Clear();

        Assert.True(h.Act(1, "like", new { chain = 0, index = 0 }).Ok);
        Assert.True(h.Act(2, "like", new { chain = 0, index = 0 }).Ok);
        Assert.True(h.Act(2, "like", new { chain = 0, index = 1 }).Ok);
        h.Tick();

        Assert.Empty(h.Outbox.OfType<RoomViews>());
        var frame = Views.Json(Assert.Single(h.Outbox.OfType<RoomFrame>()).Frame);
        Assert.Equal(0, frame.GetProperty("chain").GetInt32());
        Assert.Equal([2, 1], frame.GetProperty("likes").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        // вид, як і раніше, знає правду — з нього стартує той, хто щойно відкрив стіл
        Assert.Equal(2, h.View(null).GetProperty("reveal").GetProperty("entries")[0].GetProperty("likes").GetInt32());

        h.Outbox.Clear();
        h.Tick();
        Assert.Empty(h.Outbox);   // нічого не змінилось — нічого й не летить
    }

    [Fact]
    public void You_cannot_like_yourself_or_what_was_not_shown()
    {
        var h = Revealing();
        Assert.False(h.Act(0, "like", new { chain = 0, index = 0 }).Ok);
        Assert.False(h.Act(0, "like", new { chain = 0, index = 1 }).Ok);
        Assert.False(h.Act(0, "like", new { chain = 1, index = 0 }).Ok);
    }

    [Fact]
    public void The_most_liked_wins_when_the_show_ends()
    {
        var h = Revealing();
        Next(h);
        h.Act(0, "like", new { chain = 0, index = 1 });   // Петрів малюнок
        h.Act(2, "like", new { chain = 0, index = 1 });
        for (var i = 0; i < 8 && h.Room.Status == RoomStatus.Playing; i++) Next(h);

        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal([1], h.Room.Result!.Winners);
        Assert.Contains("найбільше ❤ у Петра", h.Outbox.OfType<Journal>().Last().Text);
        Assert.Equal(3, h.Scores.Count);
    }

    [Fact]
    public void No_likes_at_all_is_a_draw()
    {
        var h = Revealing();
        for (var i = 0; i < 9 && h.Room.Status == RoomStatus.Playing; i++) Next(h);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Empty(h.Room.Result!.Winners);
    }

    // ---------------------------------------------------------------- вихід

    [Fact]
    public void Someone_leaving_mid_step_does_not_hold_the_others()
    {
        var h = Table(4);
        Write(h, 0, "раз");
        Write(h, 1, "два");
        Write(h, 2, "три");
        h.Leave("Іван");
        h.Tick();

        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.Equal(2, Step(h));
    }

    [Fact]
    public void A_chain_that_lost_its_author_starts_later_with_a_phrase()
    {
        var h = Table(4);
        h.Leave("Іван");      // ланцюжок Івана (3) так і не почався
        Write(h, 0, "раз");
        Write(h, 1, "два");
        Write(h, 2, "три");
        h.Tick();
        // на другому кроці ланцюжок 3 дістається Олі (0): там порожньо, тож пише фразу
        Assert.Equal(Telephone.Phrase, Kind(h, 0));
        Assert.Equal(Telephone.Draw, Kind(h, 1));
    }

    [Fact]
    public void Down_to_one_player_the_game_ends()
    {
        var h = Table(3);
        h.Leave("Петро");
        h.Leave("Ганна");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Rematch_starts_from_phrases_again()
    {
        var h = Revealing();
        for (var i = 0; i < 9 && h.Room.Status == RoomStatus.Playing; i++) Next(h);
        h.Rematch();
        Assert.Equal("step", Phase(h));
        Assert.Equal(1, Step(h));
        Assert.All(h.View(null).GetProperty("likes").EnumerateArray(), e => Assert.Equal(0, e.GetInt32()));
    }

    [Fact]
    public void The_real_phrase_list_is_there()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "liquidsoap", "radio.liq"))) dir = dir.Parent;
        var phrases = TelephonePhrases.Load(Path.Combine(dir!.FullName, TelephonePhrases.FileName));
        Assert.True(phrases.All.Count >= 150, $"фраз лише {phrases.All.Count}");
        Assert.All(phrases.All, p => Assert.InRange(p.Length, 5, Telephone.MaxText));
    }

    // ---------------------------------------------------------------- удвох: фразу загадує Глек

    [Fact]
    public void Two_players_start_by_drawing_a_phrase_from_the_jug()
    {
        var h = Table(2);

        Assert.Equal("step", Phase(h));
        Assert.True(h.View(null).GetProperty("duo").GetBoolean());
        Assert.Equal(Telephone.DuoSteps, h.View(null).GetProperty("steps").GetInt32());
        for (var s = 0; s < 2; s++)
        {
            Assert.Equal(Telephone.Draw, Kind(h, s));
            var prompt = Task(h, s).GetProperty("prompt");
            Assert.Equal("text", prompt.GetProperty("kind").GetString());
            Assert.Equal("кіт на даху", prompt.GetProperty("text").GetString());
        }
    }

    [Fact]
    public void Two_players_describe_each_others_drawing_and_see_no_own_chain()
    {
        var h = Table(2);
        EveryoneSubmits(h, 2);

        Assert.Equal(2, Step(h));
        for (var s = 0; s < 2; s++)
        {
            Assert.Equal(Telephone.Describe, Kind(h, s));
            Assert.Equal(1 - s, Task(h, s).GetProperty("chain").GetInt32());   // чужий ланцюжок
        }
    }

    [Fact]
    public void Two_players_reveal_the_jug_phrase_first_and_nobody_likes_it()
    {
        var h = Table(2);
        for (var i = 0; i < Telephone.DuoSteps; i++) EveryoneSubmits(h, 2);

        Assert.Equal("reveal", Phase(h));
        var reveal = h.View(0).GetProperty("reveal");
        Assert.Equal(1 + Telephone.DuoSteps, reveal.GetProperty("total").GetInt32());
        Assert.Equal(2, reveal.GetProperty("chains").GetInt32());
        var first = reveal.GetProperty("entries")[0];
        Assert.Equal(Telephone.Jug, first.GetProperty("seat").GetInt32());

        var r = h.Act(0, "like", new { chain = 0, index = 0 });
        Assert.False(r.Ok);
        Assert.Contains("Глек", r.Message);
    }

    [Fact]
    public void Two_players_play_to_the_end_and_likes_decide()
    {
        var h = Table(2);
        for (var i = 0; i < Telephone.DuoSteps; i++) EveryoneSubmits(h, 2);

        Next(h);                                                    // малюнок Олі в її ланцюжку
        Assert.True(h.Act(1, "like", new { chain = 0, index = 1 }).Ok);
        for (var i = 0; i < 12 && Phase(h) != "done"; i++) Next(h);

        Assert.Equal("done", Phase(h));
        var finished = Assert.Single(h.Finished);
        Assert.Equal([0], finished.Result.Winners);
        Assert.Contains("2 ланцюжки", finished.Result.Text);
    }

    [Fact]
    public void Two_players_go_four_steps_and_draw_the_neighbours_description_of_their_own_drawing()
    {
        var h = Table(2);
        EveryoneSubmits(h, 2);                                      // малюють фразу Глека
        EveryoneSubmits(h, 2);                                      // описують чужий малюнок

        Assert.Equal("step", Phase(h));
        Assert.Equal(3, Step(h));
        for (var s = 0; s < 2; s++)
        {
            Assert.Equal(Telephone.Draw, Kind(h, s));
            Assert.Equal(s, Task(h, s).GetProperty("chain").GetInt32());   // знову свій ланцюжок — опис сусіда
            Assert.Equal($"describe від {1 - s} крок 2", Task(h, s).GetProperty("prompt").GetProperty("text").GetString());
        }
        EveryoneSubmits(h, 2);
        Assert.Equal(Telephone.Describe, Kind(h, 0));
        Assert.Equal(1, Task(h, 0).GetProperty("chain").GetInt32());
        EveryoneSubmits(h, 2);
        Assert.Equal("reveal", Phase(h));
    }

    [Fact]
    public void Three_players_still_write_their_own_phrases()
    {
        var h = Table(3);
        Assert.False(h.View(null).GetProperty("duo").GetBoolean());
        Assert.Equal(Telephone.Phrase, Kind(h, 0));
    }
}

/// <summary>
/// Легкий вид і знімок (прохід №3, п. 247): розсилка не возить старих малюнків, новенький отримує все.
/// </summary>
public class TelephoneSnapshotTests
{
    static readonly TelephonePhrases Phrases = new(["кіт на даху"]);

    static RoomHarness Table()
    {
        var h = new RoomHarness("telephone", seed: 5, services: RoomHarness.WithService(Phrases));
        foreach (var nick in new[] { "Оля", "Петро", "Ганна" }) h.Join(nick);
        h.Start();
        return h;
    }

    static string Kind(RoomHarness h, int seat) => h.View(seat).GetProperty("task").GetProperty("kind").GetString()!;

    static void Ink(RoomHarness h, int seat, int x = 10) => h.Input(seat, "draw", new { s = 1, c = 1, w = 8, p = new[] { x, 10, x + 50, 300 } });

    static void EveryoneSubmits(RoomHarness h)
    {
        for (var s = 0; s < 3; s++)
        {
            if (Kind(h, s) == Telephone.Draw) { Ink(h, s, s * 100); Assert.True(h.Act(s, "done", new { n = 1 }).Ok); }
            else Assert.True(h.Act(s, "done", new { text = "фраза " + s }).Ok);
        }
        h.Tick();
    }

    [Fact]
    public void While_drawing_the_broadcast_view_does_not_carry_your_own_canvas_but_the_snapshot_does()
    {
        var h = Table();
        EveryoneSubmits(h);
        Assert.Equal(Telephone.Draw, Kind(h, 1));
        Ink(h, 1);
        Ink(h, 1, 200);

        var light = h.View(1).GetProperty("task");
        Assert.Equal(JsonValueKind.Null, light.GetProperty("ops").ValueKind);
        Assert.Equal(2, light.GetProperty("n").GetInt32());
        Assert.Equal(2, h.Snapshot(1).GetProperty("task").GetProperty("ops").GetArrayLength());
    }

    [Fact]
    public void The_drawing_to_describe_comes_in_the_snapshot_and_the_light_view_says_how_big_it_is()
    {
        var h = Table();
        EveryoneSubmits(h);
        EveryoneSubmits(h);
        Assert.Equal(Telephone.Describe, Kind(h, 2));

        var light = h.View(2).GetProperty("task").GetProperty("prompt");
        Assert.Equal("drawing", light.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, light.GetProperty("ops").ValueKind);
        Assert.Equal(1, light.GetProperty("n").GetInt32());
        Assert.Equal(1, h.Snapshot(2).GetProperty("task").GetProperty("prompt").GetProperty("ops").GetArrayLength());
    }

    [Fact]
    public void A_drawing_that_did_not_fully_arrive_rides_in_the_light_view_until_you_draw_on()
    {
        var h = Table();
        EveryoneSubmits(h);
        Ink(h, 1);
        Assert.False(h.Act(1, "done", new { n = 2 }).Ok);   // у браузері два, на сервері один

        Assert.Equal(1, h.View(1).GetProperty("task").GetProperty("ops").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("task").GetProperty("ops").ValueKind);   // сусідам — ні

        Ink(h, 1, 300);
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("task").GetProperty("ops").ValueKind);
    }

    [Fact]
    public void On_reveal_only_the_freshly_opened_entry_carries_its_drawing()
    {
        var h = Table();
        for (var i = 0; i < 3; i++) EveryoneSubmits(h);
        for (var i = 0; i < 2; i++) { h.Clock.AdvanceMs(Telephone.NextEveryMs); Assert.True(h.Act(0, "next").Ok); h.Tick(); }

        var entries = h.View(null).GetProperty("reveal").GetProperty("entries");
        Assert.Equal(3, entries.GetArrayLength());
        var drawing = entries[1];
        Assert.Equal("drawing", drawing.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, drawing.GetProperty("ops").ValueKind);   // показали раніше — у всіх уже є
        Assert.Equal(1, drawing.GetProperty("n").GetInt32());
        Assert.Equal(1, h.Snapshot(null).GetProperty("reveal").GetProperty("entries")[1].GetProperty("ops").GetArrayLength());

        h.Clock.AdvanceMs(Telephone.NextEveryMs);
        h.Act(0, "next");
        h.Tick();
        // другий ланцюжок: перший запис — фраза; малюнок приїде разом із наступним «Далі»
        h.Clock.AdvanceMs(Telephone.NextEveryMs);
        h.Act(0, "next");
        var fresh = h.View(null).GetProperty("reveal").GetProperty("entries")[1];
        Assert.Equal(1, fresh.GetProperty("ops").GetArrayLength());
    }
}

/// <summary>
/// Швидкодія Зіпсованого телефону на десятьох (прохід 28.09): десять ланцюжків, п'ять малюнків у кожному по ~3000
/// точок. Міряємо тик і розмір видів — на кроці й на показі, де кожне «Далі» і кожне ❤ розсилає вид кожному.
/// </summary>
[Collection(SerialPerf.Name)]
public class TelephonePerfTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Ten_players_a_whole_game_stay_cheap()
    {
        var h = new RoomHarness("telephone", seed: 5, services: RoomHarness.WithService(new TelephonePhrases(["кіт на даху"])));
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан", "Софія", "Тарас", "Мар'яна", "Остап" }) h.Join(nick);
        h.Start();
        var rng = new Random(3);
        var ticks = new System.Diagnostics.Stopwatch();
        int tickCount = 0;
        long stepViewMax = 0, revealViews = 0, revealBytes = 0, revealMax = 0, snapMax = 0;

        void Tick()
        {
            h.Outbox.Clear();
            ticks.Start(); h.Tick(); ticks.Stop(); tickCount++;
            if (!h.Outbox.OfType<RoomViews>().Any()) return;
            var size = System.Text.Encoding.UTF8.GetByteCount(Views.Text(h.Room.Game.View(0)));
            snapMax = Math.Max(snapMax, Views.WireBytes(h.Room.Game.Snapshot(0)));
            if (h.View(null).GetProperty("phase").GetString() == "reveal") { revealViews++; revealBytes += size; revealMax = Math.Max(revealMax, size); }
            else stepViewMax = Math.Max(stepViewMax, size);
        }

        while (h.View(null).GetProperty("phase").GetString() == "step")
        {
            for (var s = 0; s < 10; s++)
            {
                var kind = h.View(s).GetProperty("task").GetProperty("kind").GetString();
                if (kind == Telephone.Draw)
                {
                    for (var c = 0; c < 150; c++)
                    {
                        var p = new int[40];
                        for (var i = 0; i < 40; i += 2) { p[i] = rng.Next(0, 1000); p[i + 1] = rng.Next(0, 750); }
                        h.Input(s, "draw", new { s = c / 10 + 1, c = 1 + c % 9, w = 8, p });
                        if (c % 15 == 0) Tick();
                    }
                    Assert.True(h.Act(s, "done", new { n = 150 }).Ok);
                }
                else Assert.True(h.Act(s, "done", new { text = "кіт на даху грає на скрипці " + s }).Ok);
                Tick();
            }
        }
        while (h.View(null).GetProperty("phase").GetString() == "reveal")
        {
            var r = h.View(0).GetProperty("reveal");
            var last = r.GetProperty("entries").EnumerateArray().Last();
            // троє ставлять ❤ кожному запису, потім «Далі»
            for (var s = 1; s <= 3; s++)
                if (last.GetProperty("seat").GetInt32() != s)
                    h.Act(s, "like", new { chain = r.GetProperty("chain").GetInt32(), index = last.GetProperty("index").GetInt32() });
            Tick();
            h.Clock.AdvanceMs(Telephone.NextEveryMs);
            h.Act(0, "next");
            Tick();
        }

        var tickUs = ticks.Elapsed.TotalMilliseconds * 1000 / tickCount;
        output.WriteLine($"тиків {tickCount}: {tickUs:F1} мкс на тик");
        output.WriteLine($"вид на кроці (одне місце): макс {stepViewMax} Б");
        output.WriteLine($"видів на показі {revealViews} (на одне місце): сер {revealBytes / Math.Max(1, revealViews)} Б, макс {revealMax} Б");
        output.WriteLine($"знімок новенькому (одне місце): макс {snapMax} Б");
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.True(tickUs < 250, $"тик {tickUs:F0} мкс");
        // Прохід №3, п. 247: розсилка без старих малюнків — на кроці їх нема зовсім, на показі лише щойно відкритий.
        Assert.True(stepViewMax < 4_000, $"вид на кроці {stepViewMax} Б");
        Assert.True(revealMax * 3 < snapMax, $"вид на показі {revealMax} Б проти знімка {snapMax} Б");
    }
}
