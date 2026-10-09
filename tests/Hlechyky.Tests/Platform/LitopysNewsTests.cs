using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using static Hlechyky.Tests.Support.Radio;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// «📰 Газета дня», «📅 місяць тому», «🎯 Мої цілі», «🔔 нове» (LitopysNews.cs): заголовки з подій дня, серії поспіль,
/// сенсації без команд, казино, новенькі, що повернулись; Глека серед людей нема; свіжий випуск або вчорашній з
/// позначкою; межі архіву; цілі — скільки бракує до звання й до «квитів»; сигнатура «нового» міняється, лише коли є що глянути.
/// </summary>
public class LitopysNewsTests
{
    /// <summary>Пʼятниця, 9 жовтня 2026, 12:00 за Києвом (UTC+3): 8 жовтня — з 7.10 21:00 до 8.10 21:00 UTC.</summary>
    static readonly DateTimeOffset Now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);
    /// <summary>Учора, 8 жовтня, 15:00 за Києвом.</summary>
    static readonly DateTimeOffset Yesterday = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");
    static readonly GameInfo Wordle = EconomyRig.Info("wordle", "Глек-слово", "глек-слово", GameGroup.Solo, max: 1, score: ScoreOrder.LowerIsBetter);

    sealed class Rig : IDisposable
    {
        public readonly EconomyRig E = new();
        public readonly Litopys L;

        public Rig()
        {
            E.Clock.UtcNow = Now;
            foreach (var g in new[] { Ttt, Wordle }) E.Names.Learn(g);
            L = new Litopys(E.Db, E.Names, E.Clock, new FixedOptions<SiteOptions>(new SiteOptions()));
        }

        public Db Db => E.Db;

        public void Result(string room, int round, string game, string nick, string outcome, DateTimeOffset at, double? score = null) =>
            Db.Exec("""
                INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, score, created_at)
                VALUES($r, $g, $n, $k, $nick, $o, $s, $at)
                """, ("$r", room), ("$g", game), ("$n", round), ("$k", Auth.NickKey(nick)), ("$nick", nick), ("$o", outcome),
                ("$s", score), ("$at", at.ToUniversalTime().ToString("o")));

        int _room;
        public void Duel(string winner, string loser, DateTimeOffset at, string game = "ttt")
        {
            var room = "r" + ++_room;
            Result(room, 1, game, winner, "win", at);
            Result(room, 1, game, loser, "loss", at);
        }

        public void Ledger(string nick, int delta, string reason, DateTimeOffset at) =>
            Db.Exec("INSERT INTO ledger(nick_key, delta, reason, created_at) VALUES($k, $d, $r, $at)",
                ("$k", Auth.NickKey(nick)), ("$d", delta), ("$r", reason), ("$at", at.ToUniversalTime().ToString("o")));

        public JsonElement Gazette(string? day = null) => Views.Json(L.Gazette(day));
        public JsonElement Goals(string nick) => Views.Json(L.Goals(nick));
        public string Pulse(string? nick) => Views.Json(L.Pulse(nick)).GetProperty("parts").GetProperty("overview").GetString()!;

        public void Dispose() => E.Dispose();
    }

    /// <summary>Заголовок текстом: ніки — у квадратних дужках.</summary>
    static string Text(JsonElement parts) => parts.ValueKind != JsonValueKind.Array ? "" : string.Concat(parts.EnumerateArray()
        .Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : "[" + p.GetProperty("n").GetString() + "]"));

    static List<(string Kind, string Text, string Sub)> Heads(JsonElement g)
    {
        var list = new List<(string, string, string)>();
        if (g.GetProperty("lead").ValueKind == JsonValueKind.Object) list.Add(Row(g.GetProperty("lead")));
        list.AddRange(g.GetProperty("items").EnumerateArray().Select(Row));
        return list;
        static (string, string, string) Row(JsonElement h) => (h.GetProperty("kind").GetString()!, Text(h.GetProperty("parts")),
            h.TryGetProperty("sub", out var s) ? Text(s) : "");
    }

    // ---------- газета ----------

    [Fact]
    public void Fifth_win_in_a_row_makes_the_front_page_with_both_nicks()
    {
        using var rig = new Rig();
        for (var i = 0; i < 3; i++) rig.Duel("Оля", "Петро", Now.AddDays(-3).AddMinutes(i));
        rig.Duel("Оля", "Петро", Yesterday);
        rig.Duel("Оля", "Петро", Yesterday.AddMinutes(5));

        var g = rig.Gazette("2026-10-08");
        Assert.Equal("2026-10-08", g.GetProperty("day").GetString());
        Assert.Equal(32, g.GetProperty("no").GetInt32());
        var streak = Heads(g).Single(h => h.Kind == "pair-streak");
        Assert.Contains("уп'яте", streak.Text);
        Assert.Contains("[Оля]", streak.Text);
        Assert.Contains("[Петро]", streak.Text);
        Assert.Equal("pair-streak", g.GetProperty("lead").GetProperty("kind").GetString());
    }

    [Fact]
    public void A_loss_breaks_the_streak()
    {
        using var rig = new Rig();
        for (var i = 0; i < 4; i++) rig.Duel("Оля", "Петро", Now.AddDays(-3).AddMinutes(i));
        rig.Duel("Петро", "Оля", Yesterday);
        rig.Duel("Оля", "Петро", Yesterday.AddMinutes(5));

        Assert.DoesNotContain(Heads(rig.Gazette("2026-10-08")), h => h.Kind == "pair-streak");
    }

    [Fact]
    public void Same_day_reads_the_same_for_everyone()
    {
        string Make()
        {
            using var rig = new Rig();
            for (var i = 0; i < 6; i++) rig.Duel("Оля", "Петро", Yesterday.AddMinutes(i));
            rig.Ledger("Яся", 900, "roulette-win:roulette", Yesterday);
            rig.Ledger("Яся", -100, "roulette-bet:roulette", Yesterday);
            return rig.Gazette("2026-10-08").GetRawText();
        }
        Assert.Equal(Make(), Make());
    }

    [Fact]
    public void Upset_needs_a_veteran_loser_and_a_single_winner()
    {
        using var rig = new Rig();
        for (var i = 0; i < Litopys.UpsetVeteran; i++) rig.Duel("Ветеран", "Груша" + i, Now.AddDays(-5).AddMinutes(i));
        rig.Duel("Новий", "Ветеран", Yesterday);
        // командна перемога над ветераном — не сенсація
        rig.Result("team", 1, "ttt", "Інший", "win", Yesterday);
        rig.Result("team", 1, "ttt", "Друг", "win", Yesterday);
        rig.Result("team", 1, "ttt", "Ветеран", "loss", Yesterday);

        var upsets = Heads(rig.Gazette("2026-10-08")).Where(h => h.Kind == "upset").ToList();
        var up = Assert.Single(upsets);
        Assert.Contains("[Новий]", up.Text);
        Assert.Contains("[Ветеран]", up.Text);
        Assert.Contains("15 перемог", up.Sub);
    }

    [Fact]
    public void Casino_jackpot_counts_net_and_bust_shows_the_loss()
    {
        using var rig = new Rig();
        rig.Ledger("Яся", 900, "roulette-win:roulette", Yesterday);
        rig.Ledger("Яся", -100, "roulette-bet:roulette", Yesterday);
        rig.Ledger("Петро", -450, "slot-bet:slots", Yesterday);
        Chat(rig.Db, "Петро", "ставлю все", Now.AddDays(-3));   // як писати нік — гаманець чи події, не леджер
        // обмін черепків — не казино
        rig.Ledger("Оля", 5000, "buy:shards", Yesterday);

        var heads = Heads(rig.Gazette("2026-10-08"));
        Assert.Contains("800 черепків", heads.Single(h => h.Kind == "jackpot").Text);
        var bust = heads.Single(h => h.Kind == "bust");
        Assert.Contains("[Петро]", bust.Text);
        Assert.Contains("450", bust.Text);
        Assert.DoesNotContain(heads, h => h.Text.Contains("[Оля]"));
    }

    [Fact]
    public void Glek_is_not_a_person_in_the_paper()
    {
        using var rig = new Rig();
        for (var i = 0; i < 30; i++) Chat(rig.Db, "Дядько Глек", "балачка " + i, Yesterday.AddMinutes(i));
        for (var i = 0; i < 16; i++) Chat(rig.Db, "Оля", "привіт " + i, Yesterday.AddMinutes(i));

        var heads = Heads(rig.Gazette("2026-10-08"));
        Assert.Contains(heads, h => h.Kind == "chatter" && h.Text.Contains("[Оля]") && h.Text.Contains("16 реплік"));
        Assert.DoesNotContain(heads, h => h.Text.Contains("[Дядько Глек]"));
    }

    [Fact]
    public void One_evening_visitor_is_not_news_in_an_old_issue_but_a_returning_one_is()
    {
        using var rig = new Rig();
        var old = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        Chat(rig.Db, "Гість", "раз", old);
        Chat(rig.Db, "Гість", "два", old.AddMinutes(1));
        Chat(rig.Db, "Свій", "раз", old);
        Chat(rig.Db, "Свій", "два", old.AddMinutes(1));
        Chat(rig.Db, "Свій", "я знову тут", old.AddDays(2));

        var heads = Heads(rig.Gazette("2026-10-01")).Where(h => h.Kind == "newbie").ToList();
        var nb = Assert.Single(heads);
        Assert.Contains("[Свій]", nb.Text);
    }

    [Fact]
    public void Quiet_today_falls_back_to_yesterday_and_month_ago_card_comes_along()
    {
        using var rig = new Rig();
        for (var i = 0; i < 6; i++) rig.Duel("Оля", "Петро", Yesterday.AddMinutes(i));
        // місяць тому, 9 вересня
        for (var i = 0; i < 4; i++) rig.Duel("Петро", "Яся", new DateTimeOffset(2026, 9, 9, 12, i, 0, TimeSpan.Zero));

        var g = rig.Gazette();
        Assert.Equal("2026-10-08", g.GetProperty("day").GetString());
        Assert.True(g.GetProperty("fallback").GetBoolean());
        Assert.False(g.GetProperty("today").GetBoolean());
        Assert.Equal("2026-10-09", g.GetProperty("next").GetString());

        var ago = g.GetProperty("ago");
        Assert.Equal("2026-09-09", ago.GetProperty("day").GetString());
        Assert.Equal("Цього дня місяць тому", ago.GetProperty("label").GetString());
        Assert.Equal(4, ago.GetProperty("top").GetProperty("rounds").GetInt32());
        Assert.NotEmpty(ago.GetProperty("heads").EnumerateArray());
    }

    [Fact]
    public void Archive_is_clamped_between_the_first_issue_and_today()
    {
        using var rig = new Rig();
        var first = rig.Gazette("2026-08-01");
        Assert.Equal(Litopys.GazetteFirst, first.GetProperty("day").GetString());
        Assert.Equal(1, first.GetProperty("no").GetInt32());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("prev").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("lead").ValueKind);
        Assert.Contains("туман", first.GetProperty("weather").GetString());

        var future = rig.Gazette("2027-01-01");
        Assert.Equal("2026-10-09", future.GetProperty("day").GetString());
        Assert.Equal(JsonValueKind.Null, future.GetProperty("next").ValueKind);
        Assert.Equal(JsonValueKind.Null, future.GetProperty("ago").ValueKind);   // картка — лише на свіжому
        // невідомий день — як без дня: свіжий випуск (сьогодні тихо — учорашній)
        Assert.True(rig.Gazette("сміття").GetProperty("fallback").GetBoolean());
    }

    [Fact]
    public void Solo_record_beats_everything_before_that_day()
    {
        using var rig = new Rig();
        rig.Result("w1", 1, "wordle", "Петро", "solo", Now.AddDays(-4), 4);
        rig.Result("w2", 1, "wordle", "Оля", "solo", Yesterday, 3);
        rig.Result("w3", 1, "wordle", "Яся", "solo", Yesterday, 5);

        var rec = Heads(rig.Gazette("2026-10-08")).Single(h => h.Kind == "record");
        Assert.Contains("[Оля]", rec.Text);
        Assert.Contains("[Петро]", rec.Sub);
    }

    // ---------- цілі ----------

    [Fact]
    public void Goals_say_how_many_replies_to_the_chatter_title_and_wins_to_even_the_score()
    {
        using var rig = new Rig();
        for (var i = 0; i < 20; i++) Chat(rig.Db, "Петро", "п " + i, Now.AddHours(-3).AddMinutes(i));
        for (var i = 0; i < 12; i++) Chat(rig.Db, "Оля", "о " + i, Now.AddHours(-2).AddMinutes(i));
        rig.Duel("Петро", "Оля", Now.AddDays(-2));
        rig.Duel("Петро", "Оля", Now.AddDays(-2).AddMinutes(5));
        rig.Duel("Оля", "Петро", Now.AddDays(-1));

        var goals = rig.Goals("Оля").GetProperty("goals").EnumerateArray().ToList();
        var chat = goals.Single(x => x.GetProperty("kind").GetString() == "title" && Text(x.GetProperty("parts")).Contains("Балакун"));
        Assert.Contains("Ще 9 реплік", Text(chat.GetProperty("parts")));
        Assert.Equal(9, chat.GetProperty("need").GetDouble());
        Assert.Equal("#chat", chat.GetProperty("href").GetString());

        var rival = goals.Single(x => x.GetProperty("kind").GetString() == "rival");
        Assert.Contains("Ще 1 перемога над [Петро]", Text(rival.GetProperty("parts")));
        Assert.Contains("1:2", Text(rival.GetProperty("parts")));

        // Петро сам тримає звання — йому одна ціль «тримай» (найгарячіша: Переможець, бо там Оля за одну перемогу)
        var hold = rig.Goals("Петро").GetProperty("goals").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "hold").ToList();
        Assert.Contains("відрив від [Оля] — лише 1 перемога", Text(Assert.Single(hold).GetProperty("parts")));
    }

    [Fact]
    public void Nobody_and_stranger_get_no_goals()
    {
        using var rig = new Rig();
        rig.Duel("Оля", "Петро", Now.AddHours(-1));
        Assert.Empty(rig.Goals("").GetProperty("goals").EnumerateArray());
        Assert.Empty(rig.Goals("Незнайомець").GetProperty("goals").EnumerateArray());
    }

    // ---------- 🔔 нове ----------

    [Fact]
    public void Pulse_changes_when_the_lead_flips_not_on_every_game()
    {
        using var rig = new Rig();
        rig.Duel("Оля", "Петро", Now.AddDays(-2));
        var a = rig.Pulse("Петро");
        Assert.Equal(a, rig.Pulse("Петро"));

        rig.E.Clock.UtcNow = Now.AddMinutes(2);   // повз хвилинний кеш
        rig.Duel("Оля", "Петро", Now.AddDays(-2).AddMinutes(1));
        Assert.Equal(a, rig.Pulse("Петро"));      // Оля й так попереду — нічого нового

        rig.E.Clock.UtcNow = Now.AddMinutes(4);
        rig.Duel("Петро", "Оля", Now.AddDays(-2).AddMinutes(2));
        rig.Duel("Петро", "Оля", Now.AddDays(-2).AddMinutes(3));
        Assert.NotEqual(a, rig.Pulse("Петро"));   // зрівнялись — є що глянути

        Assert.False(string.IsNullOrEmpty(rig.Pulse(null)));
    }
}
