using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;
using static Hlechyky.Tests.Support.Radio;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// «Літопис» (Litopys.cs): «✨ Огляд» — підсумок, звання, хто кого, коли тусимо (київський час), перл, рідкісні ачівки;
/// «🎮 Усі ігри» без рядків-рекордів ігор на кількох; виконавці без Глека; «✨ Цікавинки» людини; кеш на хвилину.
/// </summary>
public class LitopysTests
{
    /// <summary>Субота, 12:00 за Києвом (UTC+3): тиждень почався 20.09 (19.09 о 21:00 UTC).</summary>
    static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");
    static readonly GameInfo Pictionary = EconomyRig.Info("pictionary", "Піктіонарі", "піктіонарі", GameGroup.Party, max: 8, score: ScoreOrder.HigherIsBetter);
    static readonly GameInfo Wordle = EconomyRig.Info("wordle", "Глек-слово", "глек-слово", GameGroup.Solo, max: 1, score: ScoreOrder.LowerIsBetter);

    sealed class Rig : IDisposable
    {
        public readonly EconomyRig E = new();
        public readonly Litopys L;

        public Rig()
        {
            E.Clock.UtcNow = Now;
            foreach (var g in new[] { Ttt, Pictionary, Wordle }) E.Names.Learn(g);
            L = new Litopys(E.Db, E.Names, E.Clock, new FixedOptions<SiteOptions>(new SiteOptions()));
        }

        public Db Db => E.Db;

        /// <summary>Рядок game_results як є: так пишуть і партії, і рекорди («solo»).</summary>
        public void Result(string room, int round, string game, string nick, string outcome, DateTimeOffset at, double? score = null) =>
            Db.Exec("""
                INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, score, created_at)
                VALUES($r, $g, $n, $k, $nick, $o, $s, $at)
                """, ("$r", room), ("$g", game), ("$n", round), ("$k", Auth.NickKey(nick)), ("$nick", nick), ("$o", outcome),
                ("$s", score), ("$at", at.ToUniversalTime().ToString("o")));

        /// <summary>Партія на двох: перший виграв.</summary>
        public void Duel(string room, string game, string winner, string loser, DateTimeOffset at)
        {
            Result(room, 1, game, winner, "win", at);
            Result(room, 1, game, loser, "loss", at);
        }

        public JsonElement Overview(string period) => Views.Json(L.Overview(period));

        public void Dispose() => E.Dispose();
    }

    static JsonElement Title(JsonElement ov, string key) =>
        ov.GetProperty("titles").EnumerateArray().FirstOrDefault(t => t.GetProperty("key").GetString() == key);

    static List<string> Nicks(JsonElement title) => title.GetProperty("nicks").EnumerateArray().Select(n => n.GetString()!).ToList();

    // ---------- хто кого й звання ----------

    [Fact]
    public void Rivals_count_who_beat_whom_and_titles_crown_the_leader()
    {
        using var rig = new Rig();
        var day = Now.AddHours(-2);
        rig.Duel("r1", "ttt", "Оля", "Петро", day);
        rig.Duel("r2", "ttt", "Оля", "Петро", day.AddMinutes(5));
        rig.Duel("r3", "ttt", "Оля", "Петро", day.AddMinutes(10));
        rig.Duel("r4", "ttt", "Петро", "Оля", day.AddMinutes(15));
        // нічия — нікого не обіграли
        rig.Result("r5", 1, "ttt", "Оля", "draw", day.AddMinutes(20));
        rig.Result("r5", 1, "ttt", "Яся", "draw", day.AddMinutes(20));

        var ov = rig.Overview("week");
        var rivals = ov.GetProperty("rivals").EnumerateArray().ToList();
        Assert.Single(rivals);
        Assert.Equal("Оля", rivals[0].GetProperty("a").GetString());
        Assert.Equal("Петро", rivals[0].GetProperty("b").GetString());
        Assert.Equal(3, rivals[0].GetProperty("aw").GetInt32());
        Assert.Equal(1, rivals[0].GetProperty("bw").GetInt32());
        Assert.Equal("ttt", rivals[0].GetProperty("games")[0].GetProperty("game").GetString());

        var winner = Title(ov, "winner");
        Assert.Equal(["Оля"], Nicks(winner));
        Assert.Equal("3 перемоги", winner.GetProperty("text").GetString());
        Assert.Equal("Петро", winner.GetProperty("second").GetProperty("nick").GetString());
        Assert.False(winner.GetProperty("roast").GetBoolean());

        var loser = Title(ov, "loser");
        Assert.Equal(["Петро"], Nicks(loser));
        Assert.True(loser.GetProperty("roast").GetBoolean());
        Assert.Equal(5, ov.GetProperty("totals").GetProperty("rounds").GetInt32());
    }

    [Fact]
    public void Team_win_beats_every_loser_but_not_a_teammate()
    {
        using var rig = new Rig();
        var at = Now.AddHours(-1);
        rig.Result("m", 1, "ttt", "Оля", "win", at);
        rig.Result("m", 1, "ttt", "Яся", "win", at);
        rig.Result("m", 1, "ttt", "Петро", "loss", at);
        rig.Result("m", 1, "ttt", "Іван", "loss", at);

        var pairs = rig.Overview("week").GetProperty("rivals").EnumerateArray()
            .Select(r => r.GetProperty("a").GetString() + ">" + r.GetProperty("b").GetString() + " " + r.GetProperty("aw").GetInt32() + ":" + r.GetProperty("bw").GetInt32())
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(["Оля>Іван 1:0", "Оля>Петро 1:0", "Яся>Іван 1:0", "Яся>Петро 1:0"], pairs);
    }

    [Fact]
    public void Ties_on_top_share_the_title()
    {
        using var rig = new Rig();
        for (var i = 0; i < 3; i++)
        {
            Chat(rig.Db, "Оля", "привіт " + i, Now.AddMinutes(-30 + i));
            Chat(rig.Db, "Петро", "здоров " + i, Now.AddMinutes(-20 + i));
        }
        Chat(rig.Db, "Яся", "я тут", Now.AddMinutes(-5));
        var chatter = Title(rig.Overview("week"), "chatter");
        Assert.Equal(["Оля", "Петро"], Nicks(chatter));
        Assert.Equal("3 репліки", chatter.GetProperty("text").GetString());
    }

    // ---------- рекорди ігор на кількох — не партії ----------

    [Fact]
    public void Record_rows_of_party_games_are_not_rounds_but_real_solo_counts()
    {
        using var rig = new Rig();
        var at = Now.AddHours(-3);
        // Піктіонарі: одна партія на трьох + кожному рядок-рекорд «solo»
        rig.Result("p", 1, "pictionary", "Оля", "win", at, 300);
        rig.Result("p", 1, "pictionary", "Петро", "loss", at, 200);
        rig.Result("p", 1, "pictionary", "Яся", "loss", at, 100);
        foreach (var (n, s) in new[] { ("Оля", 300.0), ("Петро", 200.0), ("Яся", 100.0) })
            rig.Result("rec:" + n, 0, "pictionary", n, "solo", at, s);
        // Глек-слово: справжнє соло, менше — краще
        rig.Result("w:оля", 0, "wordle", "Оля", "solo", at, 4);
        rig.Result("w:петро", 0, "wordle", "Петро", "solo", at, 2);

        var ov = rig.Overview("week");
        Assert.Equal(1, ov.GetProperty("totals").GetProperty("rounds").GetInt32());
        Assert.Equal(2, ov.GetProperty("totals").GetProperty("solo").GetInt32());

        var games = Views.Json(rig.L.Games("week")).GetProperty("games").EnumerateArray()
            .ToDictionary(g => g.GetProperty("game").GetString()!, g => g);
        Assert.Equal(1, games["pictionary"].GetProperty("rounds").GetInt32());
        Assert.Equal(3, games["pictionary"].GetProperty("players").GetInt32());
        Assert.Equal("Оля", games["pictionary"].GetProperty("champ").GetProperty("nick").GetString());
        Assert.Equal(2, games["wordle"].GetProperty("rounds").GetInt32());
        Assert.True(games["wordle"].GetProperty("solo").GetBoolean());
        Assert.Equal("lower", games["wordle"].GetProperty("order").GetString());
        Assert.Equal("Петро", games["wordle"].GetProperty("best").GetProperty("nick").GetString());
        Assert.Equal(2, games["wordle"].GetProperty("best").GetProperty("score").GetDouble());
    }

    // ---------- коли тусимо: київський час і межі періоду ----------

    [Fact]
    public void Heat_and_owl_use_kyiv_hours_and_the_period_edge()
    {
        using var rig = new Rig();
        // 25.09 о 21:30 UTC — це вже субота 00:30 за Києвом
        var night = new DateTimeOffset(2026, 9, 25, 21, 30, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++) Chat(rig.Db, "Сова", "не сплю " + i, night.AddMinutes(i));
        // до тижня (19.09, 20:00 UTC = 23:00 за Києвом, ще субота 19.09) — не рахується
        Chat(rig.Db, "Сова", "давнє", new DateTimeOffset(2026, 9, 19, 20, 0, 0, TimeSpan.Zero));

        var ov = rig.Overview("week");
        Assert.Equal(3, ov.GetProperty("totals").GetProperty("messages").GetInt32());
        var heat = ov.GetProperty("heat");
        Assert.Equal(3, heat.GetProperty("cells")[5][0].GetInt32());   // субота (пн = 0), 0-та година
        Assert.Equal(5, heat.GetProperty("peak").GetProperty("dow").GetInt32());
        Assert.Equal(0, heat.GetProperty("peak").GetProperty("hour").GetInt32());
        Assert.Equal(["Сова"], Nicks(Title(ov, "owl")));

        Assert.Equal(4, rig.Overview("all").GetProperty("totals").GetProperty("messages").GetInt32());
        // тиждень — по днях, з першого дня періоду до сьогодні
        var series = ov.GetProperty("series");
        Assert.Equal("day", series.GetProperty("unit").GetString());
        var buckets = series.GetProperty("buckets").EnumerateArray().ToList();
        Assert.Equal(7, buckets.Count);
        Assert.Equal("2026-09-20", buckets[0].GetProperty("at").GetString());
        Assert.Equal(3, buckets[^1].GetProperty("messages").GetInt32());
        // день — по годинах
        Assert.Equal("hour", rig.Overview("day").GetProperty("series").GetProperty("unit").GetString());
    }

    // ---------- перл ----------

    [Fact]
    public void Pearl_is_the_most_liked_living_message()
    {
        using var rig = new Rig();
        Chat(rig.Db, "Оля", "смішне", Now.AddHours(-3));
        Chat(rig.Db, "Петро", "ще смішніше", Now.AddHours(-2));
        Chat(rig.Db, "Яся", "прибране", Now.AddHours(-1));
        long Id(string text) => rig.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id FROM chat WHERE text = $t";
            cmd.Parameters.AddWithValue("$t", text);
            return (long)cmd.ExecuteScalar()!;
        });
        void LikeChat(string text, string nick) => rig.Db.Exec("INSERT INTO chat_likes(chat_id, nick, created_at) VALUES($id, $n, $at)",
            ("$id", Id(text)), ("$n", nick), ("$at", Now.ToString("o")));
        LikeChat("смішне", "Петро");
        LikeChat("ще смішніше", "Оля");
        LikeChat("ще смішніше", "Яся");
        foreach (var n in new[] { "Оля", "Петро", "Іван" }) LikeChat("прибране", n);
        rig.Db.Exec("UPDATE chat SET deleted_at = $at WHERE text = 'прибране'", ("$at", Now.ToString("o")));

        var pearl = rig.Overview("week").GetProperty("pearl");
        Assert.Equal("ще смішніше", pearl.GetProperty("text").GetString());
        Assert.Equal("Петро", pearl.GetProperty("nick").GetString());
        Assert.Equal(2, pearl.GetProperty("likes").GetInt32());
        Assert.Equal(["Оля", "Яся"], pearl.GetProperty("likers").EnumerateArray().Select(x => x.GetString()).ToList());
    }

    // ---------- музика без Глека ----------

    [Fact]
    public void Music_counts_people_not_glek_and_not_voice_tracks()
    {
        using var rig = new Rig();
        Track(rig.Db, "a1", "Океан Ельзи");
        Track(rig.Db, "a2", "Океан Ельзи");
        Track(rig.Db, "voice-1", "🔥 Прожарка: Оля");
        Play(rig.Db, "a1", "user", "Оля", Now.AddHours(-5));
        Play(rig.Db, "a2", "user", "Петро", Now.AddHours(-4));
        Play(rig.Db, "a1", "user", "оля", Now.AddHours(-3));
        Play(rig.Db, "voice-1", "user", "Дядько Глек", Now.AddHours(-2));
        Play(rig.Db, "a2", "autodj", null, Now.AddHours(-1));

        Assert.Equal(3, rig.Overview("week").GetProperty("totals").GetProperty("songs").GetInt32());
        var music = Views.Json(rig.L.Music("week"));
        var artists = music.GetProperty("artists").EnumerateArray().ToList();
        Assert.Single(artists);
        Assert.Equal("Океан Ельзи", artists[0].GetProperty("artist").GetString());
        Assert.Equal(3, artists[0].GetProperty("n").GetInt32());
        Assert.Equal(2, artists[0].GetProperty("people").GetInt32());
        var fan = artists[0].GetProperty("fans")[0];
        Assert.Equal(2, fan.GetProperty("n").GetInt32());   // «Оля» й «оля» — одна людина
    }

    // ---------- рідкісні ачівки ----------

    [Fact]
    public void Rare_achievements_are_held_by_at_most_two()
    {
        using var rig = new Rig();
        var keys = AchievementCatalog.All.Take(2).Select(a => a.Key).ToList();
        void Give(string key, string nick) => rig.Db.Exec("INSERT INTO achievements(nick_key, key, nick, unlocked_at) VALUES($k, $a, $n, $at)",
            ("$k", Auth.NickKey(nick)), ("$a", key), ("$n", nick), ("$at", Now.ToString("o")));
        Give(keys[0], "Оля");
        foreach (var n in new[] { "Оля", "Петро", "Яся" }) Give(keys[1], n);

        var rare = rig.Overview("week").GetProperty("rare").EnumerateArray().ToList();
        Assert.Single(rare);
        Assert.Equal(keys[0], rare[0].GetProperty("key").GetString());
        Assert.Equal(["Оля"], rare[0].GetProperty("holders").EnumerateArray().Select(x => x.GetString()).ToList());
    }

    // ---------- цікавинки людини ----------

    [Fact]
    public void Person_knows_nemesis_victim_and_hours()
    {
        using var rig = new Rig();
        var at = Now.AddHours(-2);   // 10:00 за Києвом, субота
        for (var i = 0; i < 3; i++) rig.Duel("a" + i, "ttt", "Петро", "Оля", at.AddMinutes(i));
        for (var i = 0; i < 2; i++) rig.Duel("b" + i, "ttt", "Оля", "Яся", at.AddMinutes(10 + i));
        rig.Duel("c", "ttt", "Оля", "Петро", at.AddMinutes(20));

        var p = Views.Json(rig.L.Person("оля"));
        Assert.Equal("Оля", p.GetProperty("nick").GetString());
        Assert.Equal("Петро", p.GetProperty("nemesis").GetProperty("nick").GetString());
        Assert.Equal(1, p.GetProperty("nemesis").GetProperty("w").GetInt32());
        Assert.Equal(3, p.GetProperty("nemesis").GetProperty("l").GetInt32());
        Assert.Equal("Яся", p.GetProperty("victim").GetProperty("nick").GetString());
        Assert.Equal(3, p.GetProperty("streak").GetInt32());   // Ясю двічі й Петра раз — поспіль
        Assert.Equal(6, p.GetProperty("hours")[10].GetInt32());
        Assert.Equal(6, p.GetProperty("dows")[5].GetInt32());
        Assert.Equal("2026-09-26", p.GetProperty("first").GetString());
        Assert.Equal("ttt", p.GetProperty("fav").GetProperty("game").GetString());

        Assert.Null(rig.L.Person("Ніхто"));
        Assert.Null(rig.L.Person("   "));
    }

    // ---------- кеш ----------

    [Fact]
    public void Answer_lives_a_minute()
    {
        using var rig = new Rig();
        Chat(rig.Db, "Оля", "раз", Now.AddMinutes(-10));
        Assert.Equal(1, rig.Overview("week").GetProperty("totals").GetProperty("messages").GetInt32());
        Chat(rig.Db, "Оля", "два", Now.AddMinutes(-5));
        Assert.Equal(1, rig.Overview("week").GetProperty("totals").GetProperty("messages").GetInt32());
        rig.E.Clock.UtcNow = Now + Litopys.CacheFor + TimeSpan.FromSeconds(1);
        Assert.Equal(2, rig.Overview("week").GetProperty("totals").GetProperty("messages").GetInt32());
    }
}
