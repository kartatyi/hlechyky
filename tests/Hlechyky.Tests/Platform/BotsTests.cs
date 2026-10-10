using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Mcp;
using Hlechyky.Tests.Support;
using static Hlechyky.Tests.Support.Radio;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Записка #31 (Smaug): «прибрати ботів з хто скільки». Боти — аі-агенти /mcp (bot_nicks) і імена з «🤖»; у таблицях
/// ігор і черепків, часі, «✨ Огляді» (звання, «Хто кого», рідкісні ачівки), 🏁 Гонці й 📈 Біржі їх нема. Партія людини з
/// ботом лишається людині, а бот не стає її суперником.
/// </summary>
public class BotsTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
    static readonly GameInfo Mafia = EconomyRig.Info("mafia", "Мафія", "мафія", GameGroup.Party, max: 8);
    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");
    static readonly GameInfo Clicker = EconomyRig.Info("clicker", "Гончарне коло", "гончарне коло",
        GameGroup.Solo, max: 1, score: ScoreOrder.HigherIsBetter);

    const string Agent = "Пан Агент";

    sealed class Rig : IDisposable
    {
        public readonly EconomyRig E = new();
        public readonly Litopys L;

        public Rig()
        {
            E.Clock.UtcNow = Now;
            foreach (var g in new[] { Mafia, Ttt, Clicker }) E.Names.Learn(g);
            L = new Litopys(E.Db, E.Names, E.Clock, new FixedOptions<SiteOptions>(new SiteOptions()));
            Assert.True(Bots.Mark(E.Db, Agent, "mcp", Now));
        }

        public Db Db => E.Db;

        public void Result(string room, int round, string game, string nick, string outcome, DateTimeOffset at, double? score = null) =>
            Db.Exec("""
                INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, score, created_at)
                VALUES($r, $g, $n, $k, $nick, $o, $s, $at)
                """, ("$r", room), ("$g", game), ("$n", round), ("$k", Auth.NickKey(nick)), ("$nick", nick), ("$o", outcome),
                ("$s", score), ("$at", at.ToUniversalTime().ToString("o")));

        public void Ach(string nick, string key, DateTimeOffset at) =>
            Db.Exec("INSERT INTO achievements(nick_key, key, nick, unlocked_at) VALUES($k, $a, $n, $t)",
                ("$k", Auth.NickKey(nick)), ("$a", key), ("$n", nick), ("$t", at.ToUniversalTime().ToString("o")));

        public void Dispose() => E.Dispose();
    }

    static JsonElement Json(object o) => Views.Json(o);
    static List<string> NicksOf(JsonElement rows) => rows.EnumerateArray().Select(r => r.GetProperty("nick").GetString()!).ToList();

    // ---------- хто бот ----------

    [Fact]
    public void Agents_who_played_before_bot_nicks_are_bots_from_the_start()
    {
        using var t = new TempDb();
        var bots = Bots.Load(t.Db);
        Assert.True(bots.Has("Гнат Шершень"));
        Assert.True(bots.Has("кум панас"));
        Assert.True(bots.Has("🤖 Дід Панас"));      // вбудований бот — за значком
        Assert.True(bots.Has(Hlechyky.Games.Impl.LiveBots.Name));
        Assert.False(bots.Has("Smaug"));
        Assert.False(bots.Has("Дядько Глек"));      // Глек — не бот: його «Хто скільки» відділяє сам
    }

    [Fact]
    public async Task Agent_nick_is_remembered_on_set_nick_and_from_the_url_but_an_account_is_not()
    {
        using var t = new TempDb();
        var v = new VillageHarness();
        var tools = new AgentTools(v.Rooms, v.Registry, v.Chat, new NoFlush(), t.Db);

        var s = v.Sessions.Open("")!;
        await tools.SetNick(s, "Ворожка");
        Assert.True(Bots.Load(t.Db).Has("ворожка"));

        // ?nick= у адресі /mcp: сесія вже з ніком — запам'ятовує перший же інструмент
        var url = v.Sessions.Open("Мавка")!;
        tools.Remember(url);
        Assert.True(Bots.Load(t.Db).Has("Мавка"));

        // людина з акаунтом, що зайшла своїм ніком через /mcp, лишається людиною
        Assert.True(t.Db.AddAccount("Smaug", "h", "s"));
        var human = v.Sessions.Open("Smaug")!;
        tools.Remember(human);
        Assert.False(Bots.Load(t.Db).Has("Smaug"));
    }

    // ---------- таблиці ігор, черепки, час ----------

    [Fact]
    public void Game_tables_shards_and_time_show_only_people()
    {
        using var rig = new Rig();
        rig.E.Economy.Grant(Agent, 500, "listen");
        rig.E.Economy.Grant("🤖 бот", 400, "listen");
        rig.E.Economy.Grant("Оля", 30, "listen");

        Assert.Equal(["Оля"], NicksOf(Json(rig.E.Boards.Leaderboard("shards", "all", null)).GetProperty("rows")));
        Assert.Equal(["Оля"], NicksOf(Json(rig.E.Boards.Leaderboard("shards", "day", null)).GetProperty("rows")));
        Assert.DoesNotContain(rig.E.Economy.Top(10), r => r.Nick == Agent);

        rig.Result("m1", 1, "mafia", Agent, "win", Now.AddHours(-1));
        rig.Result("m1", 1, "mafia", "Оля", "loss", Now.AddHours(-1));
        rig.Result("m1", 1, "mafia", "Петро", "win", Now.AddHours(-1));
        Assert.Equal(["Петро", "Оля"], NicksOf(Json(rig.E.Boards.Leaderboard("mafia", "all", null)).GetProperty("rows")));

        rig.Result("clicker:агент:1", 0, "clicker", Agent, "solo", Now.AddHours(-1), 1e9);
        rig.Result("clicker:оля:1", 0, "clicker", "Оля", "solo", Now.AddHours(-1), 10);
        Assert.Equal(["Оля"], NicksOf(Json(rig.E.Boards.Leaderboard("clicker", "all", null)).GetProperty("rows")));

        rig.E.Store.AddTime([new TimeRow(Auth.NickKey(Agent), PlayClock.Site, "2026-09-26", 9000),
            new TimeRow("оля", PlayClock.Site, "2026-09-26", 60)]);
        var people = Json(rig.E.Boards.Time("all")).GetProperty("people");
        Assert.Equal(["Оля"], NicksOf(people));
    }

    // ---------- «Хто скільки» v3 ----------

    [Fact]
    public void Overview_keeps_the_human_game_but_the_bot_is_nobodys_rival_or_title()
    {
        using var rig = new Rig();
        var at = Now.AddHours(-2);
        // Мафія з агентом: Оля з агентом виграли, Петро програв
        rig.Result("m1", 1, "mafia", "Оля", "win", at);
        rig.Result("m1", 1, "mafia", Agent, "win", at);
        rig.Result("m1", 1, "mafia", "Петро", "loss", at);
        // агент обіграв Олю ще раз
        rig.Result("m2", 1, "mafia", Agent, "win", at.AddMinutes(10));
        rig.Result("m2", 1, "mafia", "Оля", "loss", at.AddMinutes(10));
        for (var i = 0; i < 5; i++) Chat(rig.Db, Agent, "балачка " + i, at.AddMinutes(i));
        Chat(rig.Db, "Петро", "привіт", at);
        rig.Ach(Agent, "first-win", at);
        rig.Ach("Оля", "first-win", at);

        var ov = Json(rig.L.Overview("week"));
        var all = ov.ToString();
        Assert.DoesNotContain(Agent, all);

        // обидві партії людей лишились партіями, а перемога Олі — її перемогою
        Assert.Equal(2, ov.GetProperty("totals").GetProperty("rounds").GetInt32());
        Assert.Equal(1, ov.GetProperty("totals").GetProperty("messages").GetInt32());
        var pairs = ov.GetProperty("rivals").EnumerateArray()
            .Select(r => r.GetProperty("a").GetString() + ">" + r.GetProperty("b").GetString()).ToList();
        Assert.Equal(["Оля>Петро"], pairs);
        var rare = ov.GetProperty("rare").EnumerateArray().Single(a => a.GetProperty("key").GetString() == "first-win");
        Assert.Equal(["Оля"], rare.GetProperty("holders").EnumerateArray().Select(n => n.GetString()));
    }

    [Fact]
    public void Race_and_bourse_have_no_bots()
    {
        using var rig = new Rig();
        rig.E.Economy.Grant(Agent, 900, "win:mafia");
        rig.E.Economy.Grant("Оля", 20, "win:mafia");
        rig.Result("m1", 1, "mafia", Agent, "win", Now.AddHours(-1));
        rig.Result("m1", 1, "mafia", "Оля", "loss", Now.AddHours(-1));

        var race = Json(rig.L.Race("week"));
        Assert.Equal(["Оля"], NicksOf(race.GetProperty("rows")));
        Assert.DoesNotContain(Agent, Json(rig.L.Bourse("week", null)).ToString());
        Assert.DoesNotContain(Agent, Json(rig.L.Records()).ToString());
    }
}
