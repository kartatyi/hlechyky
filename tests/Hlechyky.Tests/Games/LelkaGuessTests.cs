using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>Лелека, пакет 5 «Азарт чесно»: прогноз «долетить до ×2?», підсумок вечора, сальдо у виді.</summary>
public class LelkaGuessTests
{
    static LelkaKit Two() => new(("Оля", 1000), ("Петро", 1000));

    /// <summary>Один раунд: точка падіння <paramref name="cents"/>, під час прийому — <paramref name="during"/>; кінець — «шубовсть».</summary>
    static void Round(LelkaKit k, int cents, Action? during = null)
    {
        k.To(Lelka.Bets);
        k.Rig(cents);
        during?.Invoke();
        k.To(Lelka.Crashed);
    }

    static ActResult Guess(LelkaKit k, int seat, bool? yes) => k.H.Act(seat, "guess", new { yes });

    static LelkaGuess G(LelkaKit k, string nick) => k.S.Guesses[Auth.NickKey(nick)];

    static List<string> Journal(LelkaKit k) => [.. k.H.Outbox.OfType<Journal>().Select(j => j.Text)];

    [Fact]
    public void Exactly_x2_means_yes_and_below_means_no()
    {
        var k = Two();
        Round(k, 200, () =>
        {
            Assert.True(Guess(k, 0, true).Ok);
            Assert.True(Guess(k, 1, false).Ok);
        });
        Assert.Equal(1, G(k, "Оля").Streak);
        Assert.Equal(0, G(k, "Петро").Streak);
        var mine = k.View(0).GetProperty("guess");
        Assert.True(mine.GetProperty("last").GetProperty("ok").GetBoolean());
        Assert.Equal(1, mine.GetProperty("streak").GetInt32());
        Assert.False(k.View(1).GetProperty("guess").GetProperty("last").GetProperty("ok").GetBoolean());

        Round(k, 199, () =>
        {
            Guess(k, 0, true);
            Guess(k, 1, false);
        });
        Assert.Equal(0, G(k, "Оля").Streak);
        Assert.Equal(1, G(k, "Петро").Streak);
    }

    [Fact]
    public void Can_change_mind_only_while_bets_are_open()
    {
        var k = Two();
        Round(k, 500, () =>
        {
            Assert.True(Guess(k, 0, false).Ok);
            Assert.True(Guess(k, 0, true).Ok);
            Assert.True(k.View(0).GetProperty("guess").GetProperty("pick").GetBoolean());
            Assert.True(Guess(k, 1, true).Ok);
            Assert.True(Guess(k, 1, null).Ok);   // передумав прогнозувати взагалі
            Assert.Equal(JsonValueKind.Null, k.View(1).GetProperty("guess").GetProperty("pick").ValueKind);
            Assert.False(k.H.Act(0, "guess", new { yes = "так" }).Ok);
        });
        Assert.Equal(1, G(k, "Оля").Streak);
        Assert.False(k.S.Guesses.ContainsKey(Auth.NickKey("Петро")));
        Assert.False(Guess(k, 0, true).Ok);   // після падіння — ні
        k.To(Lelka.Bets);
        k.To(Lelka.Flight);
        Assert.False(Guess(k, 0, true).Ok);   // у польоті — ні
    }

    [Fact]
    public void Skipped_round_keeps_streak_and_a_miss_resets_it()
    {
        var k = Two();
        Round(k, 300, () => Guess(k, 0, true));
        Round(k, 150, () => Guess(k, 0, false));
        Assert.Equal(2, G(k, "Оля").Streak);
        Round(k, 120);   // не прогнозувала — серія ціла
        Assert.Equal(2, G(k, "Оля").Streak);
        Assert.Equal(JsonValueKind.Null, k.View(0).GetProperty("guess").GetProperty("last").ValueKind);
        Round(k, 300, () => Guess(k, 0, true));
        Assert.Equal(3, G(k, "Оля").Streak);
        Round(k, 100, () => Guess(k, 0, true));   // ×1,00 — правильне «ні»
        Assert.Equal(0, G(k, "Оля").Streak);
        Assert.Equal(3, G(k, "Оля").Best);
    }

    [Fact]
    public void Day_record_and_board_reset_with_a_new_kyiv_day()
    {
        var k = Two();
        for (var i = 0; i < 3; i++) Round(k, 300, () => Guess(k, 0, true));
        Round(k, 300, () => Guess(k, 0, false));
        Round(k, 300, () => Guess(k, 1, true));
        var board = k.View(null).GetProperty("guessBoard");
        Assert.Equal(2, board.GetArrayLength());
        Assert.Equal("Оля", board[0].GetProperty("nick").GetString());
        Assert.Equal(3, board[0].GetProperty("best").GetInt32());
        Assert.Equal(0, board[0].GetProperty("now").GetInt32());
        Assert.Equal("Петро", board[1].GetProperty("nick").GetString());

        k.H.Clock.Advance(TimeSpan.FromDays(1));
        k.H.Tick();
        Assert.Equal(0, k.View(null).GetProperty("guessBoard").GetArrayLength());
        Assert.Equal(0, k.View(1).GetProperty("guess").GetProperty("best").GetInt32());
        Round(k, 300, () => Guess(k, 1, true));
        Assert.Equal(2, G(k, "Петро").Streak);   // серія поспіль переходить через північ
        Assert.Equal(2, G(k, "Петро").Best);
        Assert.False(k.S.Guesses.ContainsKey(Auth.NickKey("Оля")));   // без серії й рекорду — прибрано
        Assert.Single(k.View(null).GetProperty("guessBoard").EnumerateArray());
    }

    [Fact]
    public void Day_record_from_five_goes_to_journal_not_more_often_than_the_gap()
    {
        var k = Two();
        for (var i = 0; i < 4; i++) Round(k, 300, () => Guess(k, 0, true));
        k.H.Tick();
        Assert.DoesNotContain(Journal(k), t => t.Contains("поспіль"));
        Round(k, 300, () => Guess(k, 0, true));
        k.H.Tick();
        Assert.Single(Journal(k), t => t.Contains("Оля — 5 поспіль"));
        Round(k, 300, () => Guess(k, 0, true));
        k.H.Tick();
        Assert.Single(Journal(k), t => t.Contains("поспіль"));   // 6 — чекає паузи Журналу
        k.H.Clock.Advance(Lelka.JournalGap);
        k.H.Tick();
        Assert.Contains(Journal(k), t => t.Contains("Оля — 6 поспіль"));
        Assert.Equal(2, Journal(k).Count(t => t.Contains("поспіль")));
    }

    [Fact]
    public void Guests_and_bots_guess_but_stay_off_the_board()
    {
        var k = new LelkaKit(("Оля", 0), ("гість Вася", 0), ("Клодік", 0));
        Round(k, 300, () =>
        {
            Assert.True(Guess(k, 0, true).Ok);   // порожній гаманець — прогноз однаково можна
            Assert.True(Guess(k, 1, true).Ok);
            Assert.True(Guess(k, 2, true).Ok);
        });
        Assert.Equal(1, k.View(1).GetProperty("guess").GetProperty("streak").GetInt32());
        var board = k.View(null).GetProperty("guessBoard");
        Assert.Single(board.EnumerateArray());
        Assert.Equal("Оля", board[0].GetProperty("nick").GetString());
    }

    [Fact]
    public void Guesses_and_evening_survive_save_and_load()
    {
        var k = Two();
        Round(k, 300, () =>
        {
            Guess(k, 0, true);
            k.Bet(1, 100);
        });
        var json = k.G.Save()!;
        k.G.Load(json);
        Assert.Equal(1, G(k, "Оля").Streak);
        Assert.Equal(1, k.S.Evening.Rounds);
        Assert.Equal(-100, k.S.Evening.Net[Auth.NickKey("Петро")].Net);
    }

    // ---------- підсумок вечора ----------

    static void Evening(LelkaKit k, int rounds)
    {
        for (var i = 0; i < rounds; i++)
            Round(k, i == 3 ? 5730 : 150, () =>
            {
                k.Bet(0, 100, 1.4);   // Оля: +40 за раунд (на ×57,30 — так само, автозабір)
                k.Bet(1, 100);        // Петро: −100
                if (i < 7 && i != 3) Guess(k, 1, false);   // серія 3, пропуск на ×57,30, ще 3 — разом 6
            });
    }

    [Fact]
    public void Evening_summary_after_twenty_quiet_minutes_once()
    {
        var k = Two();
        Evening(k, 10);
        k.H.Clock.Advance(Lelka.EveningQuiet - TimeSpan.FromSeconds(30));
        k.H.Tick();
        Assert.DoesNotContain(Journal(k), t => t.Contains("Вечір Лелеки"));
        k.H.Clock.Advance(TimeSpan.FromMinutes(1));
        k.H.Tick();
        var line = Assert.Single(Journal(k), t => t.Contains("Вечір Лелеки"));
        Assert.Equal("🪶 Вечір Лелеки: 10 польотів, найвище ×57,3 · у плюсі: Оля +400 · у мінусі: Петро −1 000 · прогноз: серія 6 — Петро", line);
        Assert.Equal(0, k.S.Evening.Rounds);
        k.H.Clock.Advance(Lelka.EveningQuiet * 2);
        k.H.Tick(5);
        Assert.Single(Journal(k), t => t.Contains("Вечір Лелеки"));
    }

    [Fact]
    public void Short_evening_resets_quietly()
    {
        var k = Two();
        Evening(k, 9);
        k.H.Clock.Advance(Lelka.EveningQuiet + TimeSpan.FromMinutes(1));
        k.H.Tick();
        Assert.DoesNotContain(Journal(k), t => t.Contains("Вечір Лелеки"));
        Assert.Equal(0, k.S.Evening.Rounds);
        Assert.Null(k.S.Evening.LastBetAt);
    }

    [Fact]
    public void Empty_table_sums_up_before_it_is_dropped()
    {
        var k = Two();
        Evening(k, 10);
        k.To(Lelka.Bets);
        Assert.True(k.H.Leave("Оля").Ok);
        Assert.True(k.H.Leave("Петро").Ok);
        k.H.Tick(10 * 60);
        Assert.DoesNotContain(Journal(k), t => t.Contains("Вечір Лелеки"));
        k.H.Tick(10 * 35);
        Assert.Single(Journal(k), t => t.Contains("Вечір Лелеки") && t.Contains("10 польотів"));
    }

    [Fact]
    public void Evening_line_reads_well()
    {
        var e = new LelkaEvening { Rounds = 122, Top = 5730, Streak = 7, StreakNick = "Smaug" };
        e.Net["оля"] = new LelkaNet { Nick = "Оля", Net = 1200 };
        e.Net["петро"] = new LelkaNet { Nick = "Петро", Net = -800 };
        e.Net["ганна"] = new LelkaNet { Nick = "Ганна", Net = -10 };
        Assert.Equal("🪶 Вечір Лелеки: 122 польоти, найвище ×57,3 · у плюсі: Оля +1 200 · у мінусі: Петро −800 · прогноз: серія 7 — Smaug",
            Lelka.EveningLine(e));
        Assert.Equal("🪶 Вечір Лелеки: 21 політ, найвище ×2", Lelka.EveningLine(new LelkaEvening { Rounds = 21, Top = 200, Streak = 1, StreakNick = "x" }));
        Assert.Equal("1,05", Lelka.Short(105));
    }

    // ---------- сальдо у виді ----------

    sealed class FixedSaldo(Saldo s) : IAzartSaldo
    {
        public List<(string Kind, string? Nick)> Asked { get; } = [];
        public Saldo Get(string kind, string? nick)
        {
            Asked.Add((kind, nick));
            return s;
        }
    }

    [Fact]
    public void Saldo_in_view_from_the_service_or_nothing()
    {
        Assert.Equal(JsonValueKind.Null, Two().View(0).GetProperty("saldo").ValueKind);

        var fake = new FixedSaldo(new Saldo(2000, 1700, 9000, 9500));
        var k = new LelkaKit(null, null, null, fake, ("Оля", 1000));
        var s = k.View(0).GetProperty("saldo");
        Assert.Equal(2000, s.GetProperty("day").GetProperty("bet").GetInt64());
        Assert.Equal(1700, s.GetProperty("day").GetProperty("win").GetInt64());
        Assert.Equal(9500, s.GetProperty("all").GetProperty("win").GetInt64());
        Assert.Contains(("lelka", "Оля"), fake.Asked);
        Assert.Equal(JsonValueKind.Null, k.View(null).GetProperty("saldo").ValueKind);   // глядачу — нема
    }

    [Fact]
    public void Saldo_in_view_follows_real_azart_saldo()
    {
        var clock = new FakeClock();
        var real = new AzartSaldo(null, null, clock);
        var k = new LelkaKit(null, null, null, real, ("Оля", 1000));
        clock.UtcNow = k.H.Clock.UtcNow;
        real.Note("Оля", -300, "lelka-bet:t:1:оля");
        real.Note("Оля", 450, "lelka-win:t:1:оля");
        var s = k.View(0).GetProperty("saldo");
        Assert.Equal(300, s.GetProperty("day").GetProperty("bet").GetInt64());
        Assert.Equal(450, s.GetProperty("day").GetProperty("win").GetInt64());
        Assert.Equal(300, s.GetProperty("all").GetProperty("bet").GetInt64());
    }
}
