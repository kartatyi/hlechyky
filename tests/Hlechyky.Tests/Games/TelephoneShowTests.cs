using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// Зіпсований телефон, прохід №3: показ-кіно (п. 166), голос Глека на показі й звання партії (п. 224).
/// </summary>
public class TelephoneShowTests
{
    static RoomHarness Table(int players, object? options = null, IDotepyVoice? voice = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TelephonePhrases(["кіт на даху"]));
        if (voice is not null) services.AddSingleton(voice);
        var h = new RoomHarness("telephone", options, 5, services.BuildServiceProvider());
        foreach (var nick in new[] { "Оля", "Петро", "Ганна", "Іван" }.Take(players)) h.Join(nick);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static JsonElement Reveal(RoomHarness h) => h.View(0).GetProperty("reveal");
    static int Shown(RoomHarness h) => Reveal(h).GetProperty("shown").GetInt32();

    /// <summary>Усі здають: фразу — <paramref name="text"/>(місце), малюнок — <paramref name="strokes"/> штрихів.</summary>
    static void Submit(RoomHarness h, int players, Func<int, string> text, int strokes = 1)
    {
        for (var s = 0; s < players; s++)
        {
            var task = h.View(s).GetProperty("task");
            if (task.GetProperty("kind").GetString() == Telephone.Draw)
            {
                for (var k = 0; k < strokes; k++) h.Input(s, "draw", new { s = k + 1, c = 1, w = 8, p = new[] { 10, 10 + k, 200, 300 } });
                Assert.True(h.Act(s, "done", new { n = strokes }).Ok);
            }
            else Assert.True(h.Act(s, "done", new { text = text(s) }).Ok);
        }
        h.Tick();
    }

    /// <summary>Трьом — три кроки: фраза, малюнок, опис. Потім показ.</summary>
    static RoomHarness ToReveal(object? options = null, IDotepyVoice? voice = null, Func<int, int, string>? text = null)
    {
        var h = Table(3, options, voice);
        text ??= (step, s) => $"фраза {s} крок {step}";
        for (var step = 0; step < 3; step++) Submit(h, 3, s => text(step, s), strokes: 5);
        Assert.Equal("reveal", Phase(h));
        return h;
    }

    // ---------------------------------------------------------------- кіно

    [Fact]
    public void The_show_turns_itself_a_phrase_by_its_length_a_drawing_after_replay()
    {
        var h = ToReveal();
        var r = Reveal(h);
        Assert.True(r.GetProperty("auto").GetBoolean());
        var textMs = Telephone.TextMs(r.GetProperty("entries")[0].GetProperty("text").GetString());
        Assert.Equal(textMs, r.GetProperty("autoMs").GetInt32());

        h.Clock.AdvanceMs(textMs - 2 * Telephone.TickMs);   // тик сам додає TickMs
        h.Tick();
        Assert.Equal(1, Shown(h));
        h.Clock.AdvanceMs(250);
        h.Tick();
        Assert.Equal(2, Shown(h));

        // малюнок: відтворення за drawMs, потім ще стоїть
        r = Reveal(h);
        Assert.Equal(Telephone.DrawMs(5), r.GetProperty("drawMs").GetInt32());
        Assert.Equal(Telephone.DrawMs(5) + Telephone.LookMs, r.GetProperty("autoMs").GetInt32());
    }

    [Fact]
    public void The_last_entry_of_a_chain_stays_longer_and_the_show_ends_by_itself()
    {
        var h = ToReveal();
        h.Tick();
        for (var i = 0; i < 2; i++) { h.Clock.AdvanceMs(Reveal(h).GetProperty("autoMs").GetInt32()); h.Tick(); }
        var r = Reveal(h);
        Assert.Equal(3, r.GetProperty("shown").GetInt32());
        Assert.Equal(Telephone.TextMs(r.GetProperty("entries")[2].GetProperty("text").GetString()) + Telephone.ChainEndMs,
            r.GetProperty("autoMs").GetInt32());

        for (var i = 0; i < 20 && Phase(h) != "done"; i++) { h.Clock.AdvanceMs(12_000); h.Tick(); }
        Assert.Equal("done", Phase(h));
        Assert.Single(h.Finished);
    }

    [Fact]
    public void Next_speeds_the_show_up_and_pause_stops_it_for_everyone()
    {
        var h = ToReveal();
        h.Clock.AdvanceMs(Telephone.NextEveryMs);
        Assert.True(h.Act(1, "next").Ok);
        Assert.Equal(2, Shown(h));

        Assert.True(h.Act(2, "pause").Ok);
        Assert.True(Reveal(h).GetProperty("paused").GetBoolean());
        h.Clock.AdvanceMs(30_000);
        h.Tick();
        Assert.Equal(2, Shown(h));                                  // на паузі кіно стоїть…
        h.Clock.AdvanceMs(Telephone.NextEveryMs);
        Assert.True(h.Act(0, "next").Ok);
        Assert.Equal(3, Shown(h));                                  // …а «Далі» гортає

        Assert.True(h.Act(0, "pause").Ok);
        h.Clock.AdvanceMs(Telephone.ResumeMs);
        h.Tick();
        Assert.Equal(1, Reveal(h).GetProperty("chain").GetInt32());    // «▶» — і кіно рушило до наступного ланцюжка
    }

    [Fact]
    public void A_manual_show_waits_for_next_as_before()
    {
        var h = ToReveal(new { show = "manual" });
        Assert.False(Reveal(h).GetProperty("auto").GetBoolean());
        h.Clock.AdvanceMs(60_000);
        h.Tick();
        Assert.Equal(1, Shown(h));
        Assert.False(h.Act(0, "pause").Ok);
    }

    // ---------------------------------------------------------------- голос

    [Fact]
    public void Glek_reads_phrases_on_the_show_and_they_were_voiced_ahead()
    {
        var voice = new DotepyTests.FakeVoice();
        var h = ToReveal(voice: voice);

        Assert.Equal("ostap", h.View(0).GetProperty("voice").GetString());
        var first = Reveal(h).GetProperty("entries")[0].GetProperty("text").GetString()!;
        Assert.Contains(voice.Prepared, p => p.Text == first && !p.Urgent);   // озвучено ще до показу
        var say = Reveal(h).GetProperty("say");
        Assert.EndsWith(".mp3", say.GetProperty("url").GetString());

        // малюнок Глек не читає
        h.Clock.AdvanceMs(Reveal(h).GetProperty("autoMs").GetInt32());
        h.Tick();
        Assert.Equal(JsonValueKind.Null, Reveal(h).GetProperty("say").ValueKind);
    }

    [Fact]
    public void A_long_clip_holds_the_phrase_but_a_late_one_is_skipped_and_the_show_never_waits()
    {
        var longText = new string('а', Telephone.MaxText);
        var voice = new DotepyTests.FakeVoice();                    // 80 знаків / 14 ≈ 5,7 с + вдих — довше, ніж стоїть сама фраза
        var h = ToReveal(voice: voice, text: (step, s) => step == 0 ? longText : $"опис {s}");
        Assert.True(Reveal(h).GetProperty("autoMs").GetInt32() > Telephone.TextMaxMs);

        var slow = new DotepyTests.FakeVoice(readyAfter: -1);
        var quiet = ToReveal(voice: slow);
        var ms = Telephone.TextMs(Reveal(quiet).GetProperty("entries")[0].GetProperty("text").GetString());
        Assert.Equal(ms, Reveal(quiet).GetProperty("autoMs").GetInt32());
        quiet.Clock.AdvanceMs(ms);
        quiet.Tick();
        Assert.Equal(2, Shown(quiet));                              // кліпа нема — показ іде далі
    }

    [Fact]
    public void Without_voice_nobody_speaks()
    {
        var h = ToReveal(new { voice = "none" }, new DotepyTests.FakeVoice());
        Assert.Equal("none", h.View(0).GetProperty("voice").GetString());
        Assert.Equal(JsonValueKind.Null, Reveal(h).GetProperty("say").ValueKind);
    }

    // ---------------------------------------------------------------- звання

    static JsonElement[] Awards(RoomHarness h) =>
        [.. h.View(null).GetProperty("result").GetProperty("awards").EnumerateArray()];

    static void Finish(RoomHarness h, Action<int, int>? onEntry = null)
    {
        for (var i = 0; i < 20 && Phase(h) != "done"; i++)
        {
            var r = Reveal(h);
            onEntry?.Invoke(r.GetProperty("chain").GetInt32(), r.GetProperty("shown").GetInt32() - 1);
            h.Clock.AdvanceMs(Telephone.NextEveryMs);
            Assert.True(h.Act(0, "next").Ok);
        }
        Assert.Equal("done", Phase(h));
    }

    [Fact]
    public void Picasso_and_poet_go_to_the_most_liked_drawer_and_writer()
    {
        var h = ToReveal(new { show = "manual" });
        Finish(h, (chain, index) =>
        {
            var e = Reveal(h).GetProperty("entries")[index];
            var seat = e.GetProperty("seat").GetInt32();
            // малюнки Петра (місце 1) і фрази Ганни (місце 2) — у ❤
            var liker = seat == 0 ? 1 : 0;
            if (e.GetProperty("kind").GetString() == "drawing" && seat == 1 || e.GetProperty("kind").GetString() == "text" && seat == 2)
                Assert.True(h.Act(liker, "like", new { chain, index }).Ok);
        });

        var awards = Awards(h);
        var picasso = Assert.Single(awards, a => a.GetProperty("title").GetString() == "Пікассо");
        Assert.Equal([1], picasso.GetProperty("seats").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal("🎨", picasso.GetProperty("icon").GetString());
        var poet = Assert.Single(awards, a => a.GetProperty("title").GetString() == "Поет");
        Assert.Equal([2], poet.GetProperty("seats").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void No_likes_no_picasso_and_the_chain_that_lost_all_sense_is_named()
    {
        // фрази першого кроку й описи третього не мають жодного спільного кореня — усі три ланцюжки «зламані»
        var h = ToReveal(new { show = "manual" }, text: (step, s) => step == 0 ? $"слон грає на трубі {s}" : $"хмара над морем {s}");
        Finish(h);

        var awards = Awards(h);
        Assert.DoesNotContain(awards, a => a.GetProperty("title").GetString() is "Пікассо" or "Поет");
        var broken = Assert.Single(awards, a => a.GetProperty("title").GetString() == "Злам сенсу");
        Assert.StartsWith("слон грає на трубі", broken.GetProperty("from").GetString());
        Assert.StartsWith("хмара над морем", broken.GetProperty("to").GetString());
    }

    [Fact]
    public void A_chain_that_kept_its_words_is_not_broken()
    {
        var h = ToReveal(new { show = "manual" }, text: (step, s) => step == 0 ? $"велосипед у гаражі {s}" : $"старий велосипеді {s}");
        Finish(h);
        Assert.DoesNotContain(Awards(h), a => a.GetProperty("title").GetString() == "Злам сенсу");
    }

    [Fact]
    public void Roots_skip_short_and_service_words()
    {
        Assert.Equal(["кіт", "даху"], Telephone.Roots("Кіт на даху"));
        Assert.Equal(["мята", "вело"], Telephone.Roots("м'ята під велосипедом"));
    }
}
