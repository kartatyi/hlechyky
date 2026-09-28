using System.Text.Json;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>«Своя гра», прохід №3 (29.09): нагороди партії, бліц, «🎲 Мікс» незіграних тем, тема «👥 Про нас».</summary>
public sealed class SvoyaSweep3Tests
{
    static readonly SvoyaPhrases Book = SvoyaPhrases.Load(Paths.Resolve("data/svoya/host.json"));

    /// <summary>Пакет «теми × запитання» з простими відповідями «т{тема}п{запитання}» і фіналом на три теми.</summary>
    internal static SvoyaPack Grid(string id, int themes, int questions, int rounds = 1, string? title = null)
    {
        var p = new SvoyaPack { Id = id, Title = title ?? id };
        for (var r = 0; r < rounds; r++)
            p.Rounds.Add(new SvoyaRound
            {
                Name = $"Раунд {r + 1}",
                Themes = [.. Enumerable.Range(0, themes).Select(t => new SvoyaTheme
                {
                    Name = $"{id} р{r} т{t}",
                    Questions = [.. Enumerable.Range(0, questions).Select(q => new SvoyaQuestion
                    {
                        Price = (q + 1) * 100 * (r + 1), Text = $"Запитання {id} {r}.{t}.{q}", Answer = $"т{t}п{q}",
                    })],
                })],
            });
        p.Rounds.Add(new SvoyaRound
        {
            Name = "Фінал",
            Type = SvoyaRound.Final,
            Themes = [.. Enumerable.Range(0, 3).Select(t => new SvoyaTheme
            {
                Name = $"{id} ф{t}", Questions = [new SvoyaQuestion { Text = $"Фінал {id} {t}", Answer = $"ф{t}" }],
            })],
        });
        SvoyaBuiltin.Stamp(p, id);
        return p;
    }

    internal static RoomHarness Table(FakeSvoyaPacks packs, string pack, object? options = null, string[]? nicks = null,
        Action<ServiceCollection>? more = null, bool start = true)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISvoyaPackSource>(packs);
        sc.AddSingleton(Book);
        more?.Invoke(sc);
        var h = new RoomHarness("svoya", options, 5, sc.BuildServiceProvider());
        foreach (var n in nicks ?? ["Оля", "Петро"]) h.Join(n);
        Assert.True(h.Act(0, "pack", new { id = pack }).Ok, h.Reply.Message);
        if (start) Assert.True(h.Start().Ok, h.Reply.Message);
        return h;
    }

    static void Answer(RoomHarness h, int seat, string text, int wait = 0)
    {
        SvoyaTests.Until(h, Svoya.Buzz);
        h.Tick(wait);
        Assert.True(h.Act(seat, "buzz").Ok, h.Reply.Message);
        Assert.True(h.Act(seat, "answer", new { text }).Ok, h.Reply.Message);
    }

    static JsonElement[] Awards(RoomHarness h) => [.. h.View(null).GetProperty("result").GetProperty("awards").EnumerateArray()];

    static JsonElement Award(RoomHarness h, string icon) => Awards(h).Single(a => a.GetProperty("icon").GetString() == icon);

    // ---------- 20. нагороди партії ----------

    [Fact]
    public void Awards_name_the_fastest_hand_the_streak_the_sharpest_and_the_dearest_miss()
    {
        var packs = new FakeSvoyaPacks();
        packs.Packs["b_g"] = (Grid("b_g", 1, 4), "");
        var h = Table(packs, "b_g");
        SvoyaTests.Open(h, 0, 0);
        Answer(h, 0, "т0п0", wait: 3);                        // 0,75 с після відкриття кнопки
        SvoyaTests.Open(h, 0, 1);
        Answer(h, 0, "т0п1", wait: 5);
        SvoyaTests.Open(h, 0, 2);
        Answer(h, 0, "т0п2", wait: 2);                        // найшвидша — 0,5 с; серія 3
        SvoyaTests.Open(h, 0, 3);
        Answer(h, 1, "не знаю", wait: 1);                     // Петро: 0,25 с, але помилився на 400
        Answer(h, 0, "т0п3", wait: 8);                        // серія 4
        SvoyaTests.Until(h, Svoya.Strike);                    // у фіналі лише Оля (Петро в мінусі)
        Assert.True(h.Act(0, "strike", new { theme = 0 }).Ok);
        Assert.True(h.Act(0, "strike", new { theme = 1 }).Ok);
        Assert.True(h.Act(0, "bet", new { amount = 1000 }).Ok);
        SvoyaTests.Until(h, Svoya.FinalQuestion);
        Assert.True(h.Act(0, "answer", new { text = "мимо" }).Ok);
        SvoyaTests.Until(h, Svoya.Done, 400);

        var fast = Award(h, "⚡");
        Assert.Equal("Петро", fast.GetProperty("nick").GetString());
        Assert.Equal("0,25 с", fast.GetProperty("note").GetString());
        Assert.Equal("4 поспіль", Award(h, "🔥").GetProperty("note").GetString());
        Assert.Equal("Оля", Award(h, "🎯").GetProperty("nick").GetString());
        var miss = Award(h, "💸");                               // ставка у фіналі дорожча за 400
        Assert.Equal("Оля", miss.GetProperty("nick").GetString());
        Assert.Equal("−1000", miss.GetProperty("note").GetString());
        // голосом — серія (вона важить більше за швидкість), слідом за підсумком
        var said = h.View(null).GetProperty("say").GetProperty("text").GetString()!;
        Assert.Contains(Book.Pool("awardStreak").Select(t => SvoyaPhrases.Fill(t, ("nick", "Оля"), ("sum", "чотири"))), said.EndsWith);
    }

    [Fact]
    public void A_quiet_game_has_no_empty_awards()
    {
        var packs = new FakeSvoyaPacks();
        packs.Packs["b_g"] = (Grid("b_g", 1, 1), "");
        var h = Table(packs, "b_g");
        SvoyaTests.Open(h, 0, 0);
        SvoyaTests.Until(h, Svoya.Done);                      // ніхто не тиснув, у фінал ніхто не пройшов
        Assert.Empty(Awards(h));
    }

    [Fact]
    public void Book_has_award_lines_with_a_choice()
    {
        Assert.True(Book.Count("awardFast") >= 3);
        Assert.True(Book.Count("awardStreak") >= 3);
    }

    // ---------- 21. бліц ----------

    [Fact]
    public void Blitz_is_a_four_by_four_board_with_quick_buttons_and_no_voice()
    {
        var packs = new FakeSvoyaPacks();
        packs.Packs["b_big"] = (Grid("b_big", 5, 5, rounds: 2), "");
        var voice = new FakeSvoyaVoice(seconds: 9);
        var h = Table(packs, "b_big", new { pace = "blitz", length = "one" }, more: sc => sc.AddSingleton<ISvoyaVoice>(voice));
        var v = h.View(1);
        Assert.Equal("blitz", v.GetProperty("options").GetProperty("pace").GetString());
        Assert.Equal(5, v.GetProperty("options").GetProperty("buzz").GetInt32());
        Assert.Equal(10, v.GetProperty("options").GetProperty("answer").GetInt32());
        Assert.Equal("none", v.GetProperty("options").GetProperty("voice").GetString());
        Assert.False(v.GetProperty("voice").GetProperty("available").GetBoolean());
        Assert.Equal(2, v.GetProperty("pack").GetProperty("rounds").GetArrayLength());   // один раунд і фінал

        SvoyaTests.Until(h, Svoya.Board);
        var board = h.View(null).GetProperty("board");
        Assert.Equal(4, board.GetArrayLength());
        Assert.All(board.EnumerateArray(), t => Assert.Equal(4, t.GetProperty("cells").GetArrayLength()));
        Assert.Empty(voice.Prepared);                                                     // голос нічого не озвучує

        SvoyaTests.Open(h, 0, 0);
        var ticks = 0;
        while (SvoyaTests.Phase(h) == Svoya.Reading) { h.Tick(); ticks++; }
        Assert.Equal(Svoya.Buzz, SvoyaTests.Phase(h));
        Assert.InRange(ticks * Svoya.TickMs, Svoya.BlitzReadMinMs, Svoya.BlitzReadMaxMs + 500);   // не 9 с голосу
        ticks = 0;
        while (SvoyaTests.Phase(h) == Svoya.Buzz) { h.Tick(); ticks++; }
        Assert.Equal(Svoya.BlitzBuzzSec * 1000 / Svoya.TickMs, ticks);
    }

    [Fact]
    public void Normal_pace_keeps_the_whole_board()
    {
        var packs = new FakeSvoyaPacks();
        packs.Packs["b_big"] = (Grid("b_big", 5, 5), "");
        var h = Table(packs, "b_big");
        SvoyaTests.Until(h, Svoya.Board);
        Assert.Equal(5, h.View(null).GetProperty("board").GetArrayLength());
        Assert.Equal("normal", h.View(null).GetProperty("options").GetProperty("pace").GetString());
    }
}
