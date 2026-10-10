using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// «📈 Графіки» й «📖 Рекорди» (LitopysCharts.cs): очки й рух місць 🏁 Гонки, баланс у минулому з леджера (📈 Біржа),
/// реплей Ело тими самими правилами, що й живий рейтинг (і на копії прод-бази), Книга рекордів — хто тримає, кого
/// перебили, «🆕», без Глека.
/// </summary>
public class LitopysChartsTests
{
    /// <summary>Субота, 12:00 за Києвом (UTC+3): день почався 25.09 о 21:00 UTC, тиждень — 19.09 о 21:00 UTC.</summary>
    static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики", rated: true);
    static readonly GameInfo Party = EconomyRig.Info("pictionary", "Піктіонарі", "піктіонарі", GameGroup.Party, max: 8);

    sealed class Rig : IDisposable
    {
        public readonly EconomyRig E = new();
        public readonly Litopys L;

        public Rig()
        {
            E.Clock.UtcNow = Now;
            foreach (var g in new[] { Ttt, Party }) E.Names.Learn(g);
            L = new Litopys(E.Db, E.Names, E.Clock, new FixedOptions<SiteOptions>(new SiteOptions()));
        }

        public Db Db => E.Db;

        public void Result(string room, int round, string game, string nick, string outcome, DateTimeOffset at) =>
            Db.Exec("""
                INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, created_at)
                VALUES($r, $g, $n, $k, $nick, $o, $at)
                """, ("$r", room), ("$g", game), ("$n", round), ("$k", Auth.NickKey(nick)), ("$nick", nick), ("$o", outcome),
                ("$at", at.ToUniversalTime().ToString("o")));

        public void Duel(string room, string winner, string loser, DateTimeOffset at)
        {
            Result(room, 1, "ttt", winner, "win", at);
            Result(room, 1, "ttt", loser, "loss", at);
        }

        public void Solved(string nick, DateTimeOffset at) => Db.Exec("""
            INSERT INTO daily_results(day, game, nick_key, nick, solved, attempts, created_at) VALUES($d, 'wordle', $k, $n, 1, 3, $at)
            """, ("$d", Days.Of(at)), ("$k", Auth.NickKey(nick)), ("$n", nick), ("$at", at.ToUniversalTime().ToString("o")));

        public void Ledger(string nick, int delta, string reason, DateTimeOffset at) => Db.Exec(
            "INSERT INTO ledger(nick_key, delta, reason, created_at) VALUES($k, $d, $r, $at)",
            ("$k", Auth.NickKey(nick)), ("$d", delta), ("$r", reason), ("$at", at.ToUniversalTime().ToString("o")));

        public void Wallet(string nick, int balance) => Db.Exec("""
            INSERT INTO wallets(nick_key, nick, balance, earned, spent, updated_at) VALUES($k, $n, $b, 0, 0, $at)
            ON CONFLICT(nick_key) DO UPDATE SET balance = $b
            """, ("$k", Auth.NickKey(nick)), ("$n", nick), ("$b", balance), ("$at", Now.ToString("o")));

        public JsonElement Json(object o) => Views.Json(o);

        public void Dispose() => E.Dispose();
    }

    static List<JsonElement> Rows(JsonElement e, string name = "rows") => e.GetProperty(name).EnumerateArray().ToList();
    static JsonElement Row(JsonElement e, string nick) => Rows(e).Single(r => r.GetProperty("nick").GetString() == nick);

    // ---------- 🏁 Гонка ----------

    [Fact]
    public void Race_points_count_tables_wins_draws_and_dailies_but_not_solo()
    {
        using var rig = new Rig();
        var t = Now.AddHours(-2);
        rig.Duel("r1", "Оля", "Петро", t);                                     // Оля 3, Петро 1
        rig.Result("r2", 1, "ttt", "Оля", "draw", t.AddMinutes(5));             // Оля +2, Яся +2
        rig.Result("r2", 1, "ttt", "Яся", "draw", t.AddMinutes(5));
        rig.Result("s1", 0, "wordle", "Петро", "solo", t);                     // соло — не партія
        rig.Solved("Петро", t.AddMinutes(1));                                  // щоденка +2

        var rows = rig.L.RaceTable("day", Now);
        Assert.Equal(["Оля", "Петро", "Яся"], rows.Select(r => r.Nick));
        Assert.Equal(5, rows[0].Points);
        Assert.Equal((2, 1, 1), (rows[0].Games, rows[0].Wins, rows[0].Draws));
        Assert.Equal(3, rows[1].Points);
        Assert.Equal(1, rows[1].Dailies);
        // однакові очки — однакове місце (Петро 3 ≠ Яся 2, тож тут ні; перевіримо нічию нижче)
        Assert.Equal([1, 2, 3], rows.Select(r => r.Place));
        rig.Result("r3", 1, "ttt", "Яся", "draw", t.AddMinutes(9));
        rig.Result("r3", 1, "ttt", "Петро", "draw", t.AddMinutes(9));
        rig.E.Clock.Advance(TimeSpan.FromMinutes(2));   // сирі події живуть хвилину в кеші
        var tie = rig.L.RaceTable("day", Now);
        // Петро й Оля — по 5: обидва перші, Яся — третя, а не друга
        Assert.Equal([1, 1, 3], tie.Select(r => r.Place));
        Assert.Equal(5, tie.Single(r => r.Nick == "Петро").Points);
    }

    [Fact]
    public void Race_window_is_the_one_of_that_moment_and_empty_base_is_empty()
    {
        using var rig = new Rig();
        Assert.Empty(rig.L.RaceTable("week", Now));
        Assert.Empty(Rows(rig.Json(rig.L.Race("week"))));
        rig.E.Clock.Advance(TimeSpan.FromMinutes(2));
        rig.Duel("old", "Оля", "Петро", Now.AddDays(-1));   // учора — це не «сьогодні»
        rig.Duel("new", "Петро", "Оля", Now.AddHours(-1));
        Assert.Equal("Петро", Assert.Single(rig.L.RaceTable("day", Now), r => r.Place == 1).Nick);
        // учорашній день — свій вікном того дня: там першою була Оля, а сьогоднішньої партії ще не було
        var yesterday = rig.L.RaceTable("day", Now.AddDays(-1).AddHours(1));
        Assert.Equal("Оля", yesterday[0].Nick);
        Assert.Equal(1, yesterday[0].Games);
    }

    [Fact]
    public void Race_shows_moves_over_a_day_newcomers_and_last_weeks_winner()
    {
        using var rig = new Rig();
        // минулий тиждень (до 19.09 21:00 UTC) виграв Сем
        rig.Duel("pw", "Сем", "Оля", new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
        // дві доби тому: Оля попереду Петра
        rig.Duel("a", "Оля", "Петро", Now.AddDays(-2));
        // за останню добу Петро двічі обіграв Олю — обійшов її, а Яся вперше з'явилась
        rig.Duel("b", "Петро", "Оля", Now.AddHours(-3));
        rig.Duel("c", "Петро", "Оля", Now.AddHours(-2));
        rig.Result("d", 1, "ttt", "Яся", "draw", Now.AddHours(-1));
        rig.Result("d", 1, "ttt", "Петро", "draw", Now.AddHours(-1));

        var race = rig.Json(rig.L.Race("week"));
        Assert.Equal("за добу", race.GetProperty("moveWord").GetString());
        Assert.Equal(1, Row(race, "Петро").GetProperty("move").GetInt32());
        Assert.Equal(-1, Row(race, "Оля").GetProperty("move").GetInt32());
        Assert.True(Row(race, "Яся").GetProperty("fresh").GetBoolean());
        Assert.Equal("Петро", race.GetProperty("climb").GetProperty("nick").GetString());
        var prev = race.GetProperty("prev");
        Assert.Equal("минулого тижня", prev.GetProperty("word").GetString());
        Assert.Equal(["Сем"], prev.GetProperty("nicks").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(3, race.GetProperty("points").GetProperty("win").GetInt32());
    }

    // ---------- 📈 Біржа ----------

    [Fact]
    public void Bourse_rebuilds_balance_back_from_wallet_and_names_the_topic()
    {
        using var rig = new Rig();
        rig.Ledger("Оля", 100, "listen", Now.AddDays(-3));
        rig.Ledger("Оля", 500, "roulette-win:roulette", Now.AddHours(-3));
        rig.Ledger("Оля", -50, "roulette-bet:roulette", Now.AddHours(-2));
        rig.Wallet("Оля", 550);
        rig.Ledger("Петро", 300, "listen", Now.AddDays(-3));
        rig.Ledger("Петро", -250, "shop:dragon", Now.AddHours(-1));
        rig.Wallet("Петро", 50);
        // гаманець, у якого леджер почався не з нуля: 40 черепків узялися нізвідки
        rig.Ledger("Яся", 10, "listen", Now.AddHours(-1));
        rig.Wallet("Яся", 50);

        var b = rig.Json(rig.L.Bourse("day", null));
        var olya = b.GetProperty("series").EnumerateArray().Single(s => s.GetProperty("nick").GetString() == "Оля");
        var pts = olya.GetProperty("pts").EnumerateArray().Select(p => p[1].GetInt64()).ToList();
        Assert.Equal([100, 600, 550, 550], pts);   // початок дня, виграш, ставка, «зараз»
        var up = b.GetProperty("up")[0];
        Assert.Equal("Оля", up.GetProperty("nick").GetString());
        Assert.Equal(450, up.GetProperty("net").GetInt64());
        Assert.Equal("🎡 рулетка", up.GetProperty("topic").GetString());
        var down = b.GetProperty("down")[0];
        Assert.Equal("Петро", down.GetProperty("nick").GetString());
        Assert.Equal("🛍 Лавка", down.GetProperty("topic").GetString());
        Assert.Equal((1, 40L), rig.L.BourseGap());
        var yasya = b.GetProperty("series").EnumerateArray().Single(s => s.GetProperty("nick").GetString() == "Яся");
        Assert.Equal(50, yasya.GetProperty("pts")[0][1].GetInt64());   // лінія — з першого відомого запису
    }

    [Fact]
    public void Bourse_draws_the_top_by_balance_plus_me()
    {
        using var rig = new Rig();
        for (var i = 0; i < Litopys.BourseLines + 2; i++)
        {
            rig.Ledger("гравець " + i, 100 + i, "listen", Now.AddHours(-1));
            rig.Wallet("гравець " + i, 100 + i);
        }
        var nicks = rig.Json(rig.L.Bourse("week", "гравець 0")).GetProperty("series").EnumerateArray()
            .Select(s => s.GetProperty("nick").GetString()).ToList();
        Assert.Equal(Litopys.BourseLines + 1, nicks.Count);
        Assert.Equal("гравець " + (Litopys.BourseLines + 1), nicks[0]);
        Assert.Contains("гравець 0", nicks);
        Assert.DoesNotContain("гравець 1", nicks);
    }

    // ---------- 📊 Ело ----------

    [Fact]
    public void Elo_replay_matches_the_live_ratings_written_by_rewards()
    {
        using var rig = new Rig();
        var people = new[] { "Оля", "Петро", "Яся", "Сем" };
        var rnd = new Random(7);
        for (var i = 0; i < 40; i++)
        {
            var a = people[rnd.Next(4)];
            var b = people.Where(p => p != a).ElementAt(rnd.Next(3));
            var kind = rnd.Next(3);
            rig.E.Rewards.OnRoomFinished(rig.E.Finished("room" + i, Ttt, [a, b], kind == 2 ? [] : [kind], draw: kind == 2));
            rig.E.Clock.Advance(TimeSpan.FromMinutes(7));
        }
        var replay = rig.L.EloReplay();
        var live = rig.Db.With(c =>
        {
            var d = new Dictionary<(string, string), (int, int)>();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT game, nick_key, elo, games FROM ratings";
            using var r = cmd.ExecuteReader();
            while (r.Read()) d[(r.GetString(0), r.GetString(1))] = (r.GetInt32(2), r.GetInt32(3));
            return d;
        });
        Assert.Equal(4, live.Count);
        foreach (var (k, v) in live) Assert.Equal(v, replay[k]);
        Assert.Equal(live.Count, replay.Count);

        var chart = rig.Json(rig.L.EloChart(null, "all"));
        Assert.Equal("ttt", chart.GetProperty("game").GetString());
        var top = Rows(chart)[0];
        Assert.Equal(live.Values.Max(v => v.Item1), top.GetProperty("elo").GetInt32());
    }

    [Fact]
    public void Elo_lists_only_rated_two_player_games_with_two_people()
    {
        using var rig = new Rig();
        rig.Result("p1", 1, "pictionary", "Оля", "win", Now.AddHours(-1));   // не рейтингова
        rig.Result("p1", 1, "pictionary", "Петро", "loss", Now.AddHours(-1));
        var e = rig.Json(rig.L.EloChart(null, "all"));
        Assert.Empty(e.GetProperty("games").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("game").ValueKind);
        rig.E.Clock.Advance(TimeSpan.FromMinutes(2));
        rig.Duel("t1", "Оля", "Петро", Now.AddHours(-1));
        Assert.Equal(1024, rig.L.EloReplay()[("ttt", Auth.NickKey("Оля"))].Elo);   // перша партія — K=48, рівні — +24
    }

    /// <summary>
    /// Реплей усієї копії прод-бази (data/hlechyky.db у робочій теці) дає той самий рейтинг, що в <c>ratings</c>, для
    /// кожної гри, яка зараз рейтингова. Ігри, що колись мали Ело, а тепер ні (сапер, дурень, морський бій, понг), у
    /// реплей не йдуть — їхні рядки в <c>ratings</c> лишились від старих правил. Нема копії — тест нічого не перевіряє.
    /// </summary>
    [Fact]
    public void Elo_replay_of_the_prod_copy_matches_ratings()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "games"))) dir = Path.GetDirectoryName(dir);
        var src = dir is null ? null : Path.Combine(dir, "data", "hlechyky.db");
        if (src is null || !File.Exists(src)) return;
        var tmp = Path.Combine(Path.GetTempPath(), "litopys-elo-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var srcDb = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + src + ";Mode=ReadOnly"))
            using (var dst = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + tmp))
            {
                srcDb.Open();
                dst.Open();
                srcDb.BackupDatabase(dst);
            }
            var db = new Db(tmp);
            var names = new GameNames(new Registry());
            var l = new Litopys(db, names, new FakeClock { UtcNow = DateTimeOffset.UtcNow }, new FixedOptions<SiteOptions>(new SiteOptions()));
            var replay = l.EloReplay();
            var live = db.With(c =>
            {
                var d = new Dictionary<(string, string), (int, int)>();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT game, nick_key, elo, games FROM ratings WHERE games > 0";
                using var r = cmd.ExecuteReader();
                while (r.Read()) d[(r.GetString(0), r.GetString(1))] = (r.GetInt32(2), r.GetInt32(3));
                return d;
            });
            var rated = live.Where(kv => names.Get(kv.Key.Item1) is { Rated: true, MaxPlayers: 2 }).ToList();
            Assert.NotEmpty(rated);
            var diff = rated.Where(kv => !replay.TryGetValue(kv.Key, out var v) || v != kv.Value)
                .Select(kv => $"{kv.Key}: ratings {kv.Value}, реплей {(replay.TryGetValue(kv.Key, out var v) ? v.ToString() : "—")}").ToList();
            Assert.True(diff.Count == 0, string.Join("\n", diff));
            // і зайвих людей реплей не вигадує
            Assert.All(replay.Keys, k => Assert.True(live.ContainsKey(k), $"{k} є в реплеї, але нема в ratings"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    // ---------- 📖 Рекорди ----------

    static JsonElement? Record(JsonElement recs, string key) =>
        recs.GetProperty("records").EnumerateArray().Where(r => r.GetProperty("key").GetString() == key).Cast<JsonElement?>().FirstOrDefault();

    [Fact]
    public void Records_crown_the_holder_and_remember_who_held_it_before()
    {
        using var rig = new Rig();
        var old = Now.AddDays(-10);
        rig.Duel("a1", "Оля", "Петро", old);
        rig.Duel("a2", "Оля", "Петро", old.AddMinutes(5));                  // Оля: 2 поспіль
        rig.Duel("b1", "Петро", "Оля", Now.AddHours(-3));
        rig.Result("b2", 1, "ttt", "Петро", "draw", Now.AddHours(-2));       // нічия серію не рве
        rig.Result("b2", 1, "ttt", "Оля", "draw", Now.AddHours(-2));
        rig.Duel("b3", "Петро", "Оля", Now.AddHours(-1));
        rig.Duel("b4", "Петро", "Оля", Now.AddMinutes(-30));                // Петро: 3 поспіль — новий рекорд
        var sig1 = rig.L.RecordsSig();

        var recs = rig.Json(rig.L.Records());
        var streak = Record(recs, "streak")!.Value;
        Assert.Equal(["Петро"], streak.GetProperty("nicks").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal("3 перемоги поспіль", streak.GetProperty("text").GetString());
        Assert.True(streak.GetProperty("fresh").GetBoolean());
        Assert.Equal("Оля", streak.GetProperty("prev").GetProperty("nicks")[0].GetString());
        Assert.Equal("2 перемоги поспіль", streak.GetProperty("prev").GetProperty("text").GetString());

        // за хвилину кеш оновиться, і перебитий рекорд міняє сигнатуру
        rig.E.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(sig1, rig.L.RecordsSig());
        rig.Duel("c1", "Оля", "Яся", Now);
        rig.Duel("c2", "Оля", "Яся", Now.AddMinutes(1));
        rig.Duel("c3", "Оля", "Яся", Now.AddMinutes(2));
        rig.Duel("c4", "Оля", "Яся", Now.AddMinutes(3));
        rig.E.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotEqual(sig1, rig.L.RecordsSig());
    }

    [Fact]
    public void Records_count_wins_casino_and_days_without_glek()
    {
        using var rig = new Rig();
        var old = Now.AddDays(-20);
        rig.Ledger("Оля", 300, "roulette-win:roulette", old);
        rig.Ledger("Петро", 500, "table-prize:poker", Now.AddDays(-5));
        rig.Ledger("Петро", 5000, "buy:42", Now.AddDays(-4));                 // куплене — не виграш
        rig.Ledger("Дядько Глек", 9000, "roulette-win:roulette", Now);        // Глек не гаманець рекордів
        rig.Wallet("Оля", 300);
        rig.Wallet("Петро", 5500);
        rig.Wallet("Дядько Глек", 9000);
        rig.Db.Exec("INSERT INTO chat(nick, text, kind, created_at) VALUES('Дядько Глек', 'я тут', 'chat', $at)", ("$at", Now.ToString("o")));
        for (var i = 0; i < 3; i++)
            rig.Db.Exec("INSERT INTO chat(nick, text, kind, created_at) VALUES('Оля', 'привіт', 'chat', $at)", ("$at", Now.AddMinutes(-i).ToString("o")));

        var recs = rig.Json(rig.L.Records());
        var win = Record(recs, "win")!.Value;
        Assert.Equal("Петро", win.GetProperty("nicks")[0].GetString());
        Assert.Equal(500, win.GetProperty("value").GetDouble());
        Assert.False(win.GetProperty("fresh").GetBoolean());
        Assert.Equal("Оля", win.GetProperty("prev").GetProperty("nicks")[0].GetString());
        var casino = Record(recs, "casino")!.Value;
        Assert.Equal("Оля", casino.GetProperty("nicks")[0].GetString());
        Assert.Equal("рулетка", casino.GetProperty("note").GetString());
        Assert.Equal("Петро", Record(recs, "rich")!.Value.GetProperty("nicks")[0].GetString());
        var chat = Record(recs, "chat-day")!.Value;
        Assert.Equal(["Оля"], chat.GetProperty("nicks").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(3, chat.GetProperty("value").GetDouble());
        var busy = Record(recs, "busy-day")!.Value;
        Assert.True(busy.GetProperty("site").GetBoolean());
        Assert.Equal(Days.Of(Now), busy.GetProperty("day").GetString());
        Assert.Null(Record(recs, "streak"));   // партій нема — і рекорду нема
    }

    [Fact]
    public void Records_of_an_empty_base_are_empty()
    {
        using var rig = new Rig();
        Assert.Empty(rig.Json(rig.L.Records()).GetProperty("records").EnumerateArray());
        Assert.NotEmpty(rig.L.RecordsSig());
    }
}
