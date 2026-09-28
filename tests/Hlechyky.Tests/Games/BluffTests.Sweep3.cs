using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Прохід №3 (29.09): звання партії, тему обирає гравець, голос Глека (питання, вердикти з ніками, переможець — гра
/// ніколи не чекає на озвучку) і «свіжих для цього столу» в лобі.
/// </summary>
public partial class BluffTests
{
    static IServiceProvider Voiced(IDotepyVoice voice) => new ServiceCollection()
        .AddSingleton(new BluffBankSource(TestBank))
        .AddSingleton(voice)
        .BuildServiceProvider();

    static JsonElement Result(RoomHarness h) => V(h).GetProperty("result");

    /// <summary>Усі пишуть; місця <paramref name="fooled"/> обирають брехню місця 0, решта — правду.</summary>
    static void Round(RoomHarness h, int players, params int[] fooled)
    {
        WriteAndPick(h, [.. Enumerable.Range(0, players)]);
        var lie0 = CardOf(h, Lies[0]);
        var truth = TruthCard(h);
        for (var s = 0; s < players; s++)
            PickCard(h, s, fooled.Contains(s) ? lie0 : s == 0 ? truth : truth);
        NextQuestion(h);
    }

    static JsonElement? TitleOf(JsonElement result, string key)
    {
        foreach (var t in result.GetProperty("titles").EnumerateArray())
            if (t.GetProperty("key").GetString() == key) return t;
        return null;
    }

    // ---- №3 звання партії ----

    [Fact]
    public void Titles_fox_nose_and_sheep_come_from_the_cards()
    {
        var h = Table(3, options: new { questions = "5" });
        for (var q = 0; q < 5; q++) Round(h, 3, fooled: [1, 2]);
        Assert.False(Playing(h));
        var r = Result(h);
        var fox = TitleOf(r, "fox")!.Value;
        Assert.Equal([0], Ints(fox.GetProperty("seats")));
        Assert.Equal(10, fox.GetProperty("n").GetInt32());
        Assert.Equal("10 жертв", fox.GetProperty("text").GetString());
        var nose = TitleOf(r, "nose")!.Value;
        Assert.Equal([0], Ints(nose.GetProperty("seats")));
        Assert.Equal("5 правд", nose.GetProperty("text").GetString());
        var sheep = TitleOf(r, "sheep")!.Value;
        Assert.Equal([1, 2], Ints(sheep.GetProperty("seats")));
        Assert.Equal("5 разів на гачку", sheep.GetProperty("text").GetString());
        // ❤ ніхто не ставив — звання нема.
        Assert.Null(TitleOf(r, "heart"));
    }

    [Fact]
    public void Title_shared_by_everyone_is_not_a_title()
    {
        var h = Table(2, options: new { questions = "5" });
        // Обоє щоразу вгадують правду: «Нюх» ділять усі — звання нема; жертв і довірливих нема зовсім.
        for (var q = 0; q < 5; q++) Round(h, 2);
        var r = Result(h);
        Assert.Equal(0, r.GetProperty("titles").GetArrayLength());
    }

    [Fact]
    public void Heart_title_goes_to_the_most_liked_liar()
    {
        var h = Table(3, options: new { questions = "5" });
        WriteAndPick(h, 0, 1, 2);
        var lie0 = CardOf(h, Lies[0]);
        PickCard(h, 1, lie0);
        PickCard(h, 2, TruthCard(h));
        PickCard(h, 0, TruthCard(h));
        UntilOpen(h, lie0);
        Assert.True(h.Act(2, "like", new { i = lie0 }).Ok);
        for (var q = 1; q < 5; q++) Round(h, 3);
        var heart = TitleOf(Result(h), "heart")!.Value;
        Assert.Equal([0], Ints(heart.GetProperty("seats")));
        Assert.Equal("1 ❤", heart.GetProperty("text").GetString());
    }

    // ---- №7 тему обирає гравець ----

    [Fact]
    public void Default_table_has_no_topic_phase()
    {
        var h = Table(3);
        Assert.Equal(Bluff.PhaseRead, Phase(h));
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("topic").ValueKind);
    }

    [Fact]
    public void Chooser_picks_one_of_two_topics_and_gets_that_question()
    {
        var h = Table(3, options: new { questions = "5", topic = "turn" });
        Assert.Equal(Bluff.PhaseTopic, Phase(h));
        Assert.Equal(1, QNo(h));
        Assert.Equal(5, V(h).GetProperty("of").GetInt32());
        var t = V(h).GetProperty("topic");
        var by = t.GetProperty("by").GetInt32();
        var keys = t.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("key").GetString()!).ToArray();
        Assert.Equal(2, keys.Length);
        Assert.NotEqual(keys[0], keys[1]);
        var other = (by + 1) % 3;
        Assert.False(h.Act(other, "topic", new { k = keys[1] }).Ok);
        Assert.False(h.Act(by, "topic", new { k = "nope" }).Ok);
        Assert.True(h.Act(by, "topic", new { k = keys[1] }).Ok);
        h.Tick();
        Assert.Equal(Bluff.PhaseRead, Phase(h));
        Assert.Equal(keys[1], V(h).GetProperty("cat").GetString());
        Assert.Equal(by, V(h).GetProperty("chooser").GetInt32());
        Assert.Equal(1, QNo(h));
    }

    [Fact]
    public void Silent_chooser_gets_hleks_topic_and_the_turn_moves_on()
    {
        var h = Table(3, options: new { questions = "5", topic = "turn" });
        var first = V(h).GetProperty("topic");
        var by = first.GetProperty("by").GetInt32();
        var fresher = first.GetProperty("options")[0].GetProperty("key").GetString();
        Until(h, Bluff.PhaseRead);
        Assert.Equal(fresher, V(h).GetProperty("cat").GetString());
        Assert.Equal(-1, V(h).GetProperty("chooser").GetInt32());
        Round(h, 3);
        Assert.Equal(Bluff.PhaseTopic, Phase(h));
        Assert.NotEqual(by, V(h).GetProperty("topic").GetProperty("by").GetInt32());
        PlayOut(h);
        Assert.Equal(5, Result(h).GetProperty("recap").GetArrayLength());
    }

    [Fact]
    public void One_topic_left_means_no_choice()
    {
        var h = Table(3, options: new { topic = "turn", cat = "ukraine" });
        Assert.Equal(Bluff.PhaseRead, Phase(h));
        Assert.Equal(2, V(h).GetProperty("of").GetInt32());
    }

    // ---- №4 Глек зачитує ----

    [Fact]
    public void Voice_reads_question_verdicts_with_nicks_and_the_winner()
    {
        var voice = new DotepyTests.FakeVoice();
        var h = Table(3, options: new { questions = "5" }, services: Voiced(voice));
        // На старті: перше питання — терміново, «Перемагає …» — для кожного за столом.
        Assert.Contains(voice.Prepared, p => p.Urgent && p.Text.Contains(BluffLines.Beep, StringComparison.Ordinal));
        Assert.Contains(voice.Prepared, p => p.Text == BluffLines.Win("Оля"));
        h.Tick();
        var say = V(h).GetProperty("say");
        Assert.Equal(BluffLines.Question(Question(h).Q, false), say.GetProperty("text").GetString());
        Assert.StartsWith("/api/games/svoya/tts/", say.GetProperty("url").GetString());
        Assert.Equal("ostap", V(h).GetProperty("voice").GetString());

        WriteAndPick(h, 0, 1, 2);
        var lie0 = CardOf(h, Lies[0]);
        PickCard(h, 1, lie0);
        PickCard(h, 2, lie0);
        PickCard(h, 0, TruthCard(h));
        UntilOpen(h, lie0);
        var text = V(h).GetProperty("say").GetProperty("text").GetString()!;
        Assert.Equal(BluffLines.Lie("Петро і Ганна", "Оля", 1), text);
        // Карток Глек не читає: тексту брехні в репліці нема.
        Assert.DoesNotContain(Lies[0], text, StringComparison.Ordinal);
        Assert.True(PhaseMs(h) >= Bluff.StepPickedMs);
        UntilOpen(h, TruthCard(h));
        Assert.StartsWith("А правда — ", V(h).GetProperty("say").GetProperty("text").GetString());
        PlayOut(h);
        Assert.Equal(BluffLines.Win("Оля"), V(h).GetProperty("say").GetProperty("text").GetString());
    }

    [Fact]
    public void Long_verdict_stretches_the_step_but_not_past_the_cap()
    {
        var voice = new DotepyTests.FakeVoice();
        var h = Table(3, options: new { questions = "5" }, services: Voiced(voice));
        WriteAndPick(h, 0, 1, 2);
        var lie0 = CardOf(h, Lies[0]);
        PickCard(h, 1, lie0);
        PickCard(h, 2, lie0);
        PickCard(h, 0, TruthCard(h));
        UntilOpen(h, lie0);
        var line = BluffLines.Lie("Петро і Ганна", "Оля", 1);
        var want = (int)(line.Length / 14.0 * 1000) + Bluff.VoicePadMs;
        Assert.InRange(PhaseMs(h), Math.Clamp(want, Bluff.StepPickedMs, Bluff.MaxStepMs) - 2, Math.Clamp(want, Bluff.StepPickedMs, Bluff.MaxStepMs) + 2);
    }

    [Fact]
    public void Game_never_waits_for_a_clip_that_is_not_ready()
    {
        var slow = new DotepyTests.FakeVoice(readyAfter: -1);
        var h = Table(3, options: new { questions = "5" }, services: Voiced(slow));
        var plain = Table(3, options: new { questions = "5" });
        foreach (var t in new[] { h, plain })
        {
            WriteAndPick(t, 0, 1, 2);
            var lie0 = CardOf(t, Lies[0]);
            PickCard(t, 1, lie0);
            PickCard(t, 2, lie0);
            PickCard(t, 0, TruthCard(t));
            UntilOpen(t, lie0);
        }
        Assert.Equal(Bluff.StepPickedMs, PhaseMs(h));
        Assert.Equal(PhaseMs(plain), PhaseMs(h));
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("say").ValueKind);
    }

    [Fact]
    public void Voice_none_prepares_nothing()
    {
        var voice = new DotepyTests.FakeVoice();
        var h = Table(3, options: new { questions = "5", voice = "none" }, services: Voiced(voice));
        PlayOut(h);
        Assert.Empty(voice.Prepared);
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("say").ValueKind);
    }

    [Fact]
    public void Question_line_beeps_the_blank()
    {
        Assert.Equal("Кров восьминога …біп… кольору.", BluffLines.Question("Кров восьминога ___ кольору", false));
        Assert.Equal(BluffLines.FinalPrefix + "Лампами на …біп…", BluffLines.Question("Лампами на ___", true));
        Assert.Equal("А правда — гасі! І ніхто не вгадав.", BluffLines.Truth("гасі", "", 0, 3));
        Assert.Equal("А правда — гасі! Вгадали всі.", BluffLines.Truth("гасі", "Оля і Петро", 2, 2));
        Assert.Equal("А правда — гасі! З нюхом — Оля.", BluffLines.Truth("гасі", "Оля", 1, 3));
    }

    // ---- №1 свіжих для цього столу ----

    [Fact]
    public void Lobby_counts_fresh_questions_for_this_table()
    {
        var h = Seated(2);
        var fresh = V(h).GetProperty("fresh");
        Assert.Equal(TestBank.Count, fresh.GetProperty("n").GetInt32());
        Assert.Equal(TestBank.Count, fresh.GetProperty("of").GetInt32());
        Assert.True(h.Start().Ok, h.Reply.Message);
        Assert.Equal(JsonValueKind.Null, V(h).GetProperty("fresh").ValueKind);
        PlayOut(h);
        h.Clock.Advance(5);
        fresh = V(h).GetProperty("fresh");
        Assert.Equal(TestBank.Count - Bluff.DefaultQuestions, fresh.GetProperty("n").GetInt32());
    }
}
