using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Economy;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// «🏺 Глек» (LitopysGlek.cs): його треки проти ваших (скіпи, ❤ за відтворенням, без власних лайків), на чиїх піснях
/// учиться, критики й «чий смак кращий» з порогом, прожарки й цитата дня, партії, які він вів, і каса — кожна голова
/// причини гаманця на своєму місці, Глек — друга сторона кожного запису.
/// </summary>
public class LitopysGlekTests
{
    /// <summary>Субота, 12:00 за Києвом (UTC+3): тиждень почався 20.09.</summary>
    static readonly DateTimeOffset Now = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);
    const string GlekNick = "Дядько Глек";

    static readonly GameInfo Mafia = EconomyRig.Info("mafia", "Мафія", "мафію", GameGroup.Party, max: 12);
    static readonly GameInfo Ttt = EconomyRig.Info("ttt", "Хрестики-нолики", "хрестики-нолики");

    sealed class Rig : IDisposable
    {
        public readonly EconomyRig E = new();
        public readonly Litopys L;
        int _id;

        public Rig()
        {
            E.Clock.UtcNow = Now;
            E.Names.Learn(Mafia);
            E.Names.Learn(Ttt);
            _ = new LiveAdsStore(E.Db);   // таблиця live_ads
            L = new Litopys(E.Db, E.Names, E.Clock, new FixedOptions<SiteOptions>(new SiteOptions()));
        }

        public Db Db => E.Db;
        static string T(DateTimeOffset at) => at.ToUniversalTime().ToString("o");

        public void Track(string id, string artist, string title) =>
            Db.Exec("INSERT OR IGNORE INTO tracks(id, title, artist, source_url, created_at) VALUES($i, $t, $a, '', $at)",
                ("$i", id), ("$t", title), ("$a", artist), ("$at", T(Now)));

        /// <summary>Відтворення: source autodj — вибір Глека, user — замовлення (by), 4 хв звучання.</summary>
        public long Play(string track, string source, DateTimeOffset at, string? by = null, string? reason = null, bool skipped = false, string? via = null)
        {
            Db.Exec("""
                INSERT INTO plays(track_id, source, requested_by, reason, started_at, ended_at, skipped, via, duration_sec)
                VALUES($t, $s, $b, $r, $at, $end, $sk, $v, 240)
                """, ("$t", track), ("$s", source), ("$b", by), ("$r", reason), ("$at", T(at)), ("$end", T(at.AddMinutes(4))),
                ("$sk", skipped ? 1 : 0), ("$v", via));
            return ++_id;
        }

        public void Like(string track, string nick, DateTimeOffset at) =>
            Db.Exec("INSERT INTO likes(track_id, nick, created_at) VALUES($t, $n, $at)", ("$t", track), ("$n", nick), ("$at", T(at)));

        public void Feedback(string nick, string kind, string artistKey, DateTimeOffset at) =>
            Db.Exec("INSERT INTO dj_feedback(artist_key, seed_id, kind, nick, created_at) VALUES($a, NULL, $k, $n, $at)",
                ("$a", artistKey), ("$k", kind), ("$n", nick), ("$at", T(at)));

        /// <summary>Запис гаманця; нік гаманця — звідти «Літопис» і знає, як його писати.</summary>
        public void Led(string nick, int delta, string reason, DateTimeOffset at)
        {
            Db.Exec("INSERT OR IGNORE INTO wallets(nick_key, nick, updated_at) VALUES($k, $n, $at)", ("$k", Auth.NickKey(nick)), ("$n", nick), ("$at", T(at)));
            Db.Exec("INSERT INTO ledger(nick_key, delta, reason, created_at) VALUES($k, $d, $r, $at)",
                ("$k", Auth.NickKey(nick)), ("$d", delta), ("$r", reason), ("$at", T(at)));
        }

        public void Ad(string kind, string? target, string status, DateTimeOffset at, string text = "", string? buyer = null, bool anon = false, int price = 0) =>
            Db.Exec("""
                INSERT INTO live_ads(kind, target, target_key, buyer, buyer_key, anon, price, text, seconds, status, created_at, played_at, day)
                VALUES($k, $t, $tk, $b, $bk, $an, $p, $x, 30, $s, $at, $pl, $d)
                """, ("$k", kind), ("$t", target), ("$tk", target is null ? null : Auth.NickKey(target)), ("$b", buyer),
                ("$bk", buyer is null ? null : Auth.NickKey(buyer)), ("$an", anon ? 1 : 0), ("$p", price), ("$x", text), ("$s", status),
                ("$at", T(at)), ("$pl", status == "aired" ? T(at.AddMinutes(1)) : null), ("$d", Days.Of(at)));

        public void Say(string text, string kind, DateTimeOffset at) =>
            Db.Exec("INSERT INTO chat(nick, text, kind, created_at) VALUES($n, $t, $k, $at)", ("$n", GlekNick), ("$t", text), ("$k", kind), ("$at", T(at)));

        public void Result(string room, int round, string game, string nick, string outcome, DateTimeOffset at) =>
            Db.Exec("""
                INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, created_at) VALUES($r, $g, $n, $k, $nick, $o, $at)
                """, ("$r", room), ("$g", game), ("$n", round), ("$k", Auth.NickKey(nick)), ("$nick", nick), ("$o", outcome), ("$at", T(at)));

        public JsonElement Glek(string period) => Views.Json(L.Glek(period));

        public void Dispose() => E.Dispose();
    }

    static List<string?> Nicks(JsonElement list) => list.EnumerateArray().Select(x => x.GetProperty("nick").GetString()).ToList();

    // ---------- 🎛 діджей ----------

    [Fact]
    public void Air_counts_glek_against_people_without_his_voice_and_own_likes()
    {
        using var rig = new Rig();
        var t = Now.AddHours(-3);
        rig.Track("a", "Adele", "Hello");
        rig.Track("b", "Nirvana", "Lithium");
        rig.Play("a", "autodj", t);
        rig.Play("a", "autodj", t.AddMinutes(10), skipped: true);
        rig.Play("b", "user", t.AddMinutes(20), by: "Оля");
        rig.Play("b", "user", t.AddMinutes(30), by: "Петро", via: "suggestion");
        rig.Play("voice-live-1", "user", t.AddMinutes(40), by: GlekNick);   // його прожарка — не пісня
        // ❤ — тому відтворенню, що грало останнім до неї: перша Олі — другому autodj, друга — Петровому
        rig.Like("a", "Оля", t.AddMinutes(12));
        rig.Like("b", "Петро", t.AddMinutes(31));      // свій же трек — не рахуємо
        rig.Like("b", "Яся", t.AddMinutes(32));
        rig.Like("a", "Яся", t.AddMinutes(-60));        // до будь-якого відтворення в періоді — нічия
        rig.Feedback("Оля", "skip", "adele", t.AddMinutes(11));

        var air = rig.Glek("week").GetProperty("air");
        Assert.Equal(2, air.GetProperty("glek").GetInt32());
        Assert.Equal(2, air.GetProperty("people").GetInt32());
        Assert.Equal(1, air.GetProperty("glekSkips").GetInt32());
        Assert.Equal(0, air.GetProperty("peopleSkips").GetInt32());
        Assert.Equal(1, air.GetProperty("glekLikes").GetInt32());
        Assert.Equal(1, air.GetProperty("peopleLikes").GetInt32());
        Assert.Equal(1, air.GetProperty("tips").GetInt32());
        Assert.Equal(1, air.GetProperty("rejects").GetInt32());
        Assert.Equal(480, air.GetProperty("glekSec").GetInt64());

        var talk = rig.Glek("week").GetProperty("talk");
        Assert.Equal(1, talk.GetProperty("voice").GetInt32());
        Assert.Equal(240, talk.GetProperty("voiceSec").GetInt64());
    }

    [Fact]
    public void Seeds_credit_whoever_requested_the_seed_song_most_and_the_rest_is_his_own_muse()
    {
        using var rig = new Rig();
        var t = Now.AddHours(-5);
        rig.Track("s1", "Adele", "Hello");
        rig.Track("s2", "Queen", "Bohemian Rhapsody");
        rig.Track("x", "Lana Del Rey", "Video Games");
        // «Hello» колись замовляли: Оля двічі (ще до тижня), Петро раз
        rig.Play("s1", "user", Now.AddDays(-20), by: "Оля");
        rig.Play("s1", "user", Now.AddDays(-19), by: "Оля");
        rig.Play("s1", "user", t, by: "Петро");
        for (var i = 0; i < 3; i++) rig.Play("x", "autodj", t.AddMinutes(10 + i), reason: "схоже на Adele — Hello");
        rig.Play("x", "autodj", t.AddMinutes(20), reason: "схоже на Queen — Bohemian Rhapsody");   // ніхто з людей не замовляв
        rig.Play("x", "autodj", t.AddMinutes(30), reason: "з нашого архіву");

        var s = rig.Glek("week").GetProperty("seeds");
        Assert.Equal(["Оля"], Nicks(s.GetProperty("people")));
        Assert.Equal(3, s.GetProperty("people")[0].GetProperty("n").GetInt32());
        Assert.Equal(1, s.GetProperty("self").GetInt32());
        Assert.Equal(1, s.GetProperty("archive").GetInt32());
        var top = s.GetProperty("top")[0];
        Assert.Equal("Adele — Hello", top.GetProperty("label").GetString());
        Assert.Equal("Оля", top.GetProperty("nick").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("top")[1].GetProperty("nick").ValueKind);
    }

    [Fact]
    public void Hits_flops_and_favourite_artists_are_his_picks_only()
    {
        using var rig = new Rig();
        var t = Now.AddHours(-4);
        rig.Track("h", "Aerosmith", "Dream On");
        rig.Track("f", "Скрябін", "Старі фотографії");
        rig.Track("u", "Adele", "Hello");
        rig.Play("h", "autodj", t);
        rig.Play("f", "autodj", t.AddMinutes(5));
        rig.Play("f", "autodj", t.AddMinutes(9));
        rig.Play("u", "user", t.AddMinutes(15), by: "Оля");
        rig.Like("h", "Оля", t.AddMinutes(1));
        rig.Like("h", "Петро", t.AddMinutes(2));
        rig.Like("u", "Петро", t.AddMinutes(16));   // людський трек у хіти Глека не йде
        rig.Feedback("Петро", "dismiss", "скрябін", t.AddMinutes(6));
        rig.Feedback("Оля", "skip", "скрябін", t.AddMinutes(10));

        var d = rig.Glek("week");
        var hits = d.GetProperty("hits");
        Assert.Equal(1, hits.GetArrayLength());
        Assert.Equal("Aerosmith — Dream On", hits[0].GetProperty("label").GetString());
        Assert.Equal(2, hits[0].GetProperty("likes").GetInt32());
        var flop = d.GetProperty("flops")[0];
        Assert.Equal("Скрябін", flop.GetProperty("artist").GetString());
        Assert.Equal(2, flop.GetProperty("n").GetInt32());
        Assert.Equal(2, flop.GetProperty("plays").GetInt32());
        var artists = d.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("artist").GetString()).ToList();
        Assert.Equal(["Скрябін", "Aerosmith"], artists);
    }

    // ---------- ⚔ проти вас ----------

    [Fact]
    public void Critics_rank_people_and_feedback_without_a_nick_is_only_a_reject()
    {
        using var rig = new Rig();
        var t = Now.AddHours(-2);
        rig.Feedback("Smaug", "dismiss", "a", t);
        rig.Feedback("Smaug", "skip", "b", t);
        rig.Feedback("Оля", "skip", "a", t);
        rig.Db.Exec("INSERT INTO dj_feedback(artist_key, kind, nick, created_at) VALUES('c', 'skip', NULL, $at)", ("$at", t.ToString("o")));
        rig.Feedback(GlekNick, "skip", "c", t);

        var d = rig.Glek("week");
        var critics = d.GetProperty("critics");
        Assert.Equal(["Smaug", "Оля"], Nicks(critics));
        Assert.Equal(1, critics[0].GetProperty("skip").GetInt32());
        Assert.Equal(1, critics[0].GetProperty("dismiss").GetInt32());
        Assert.Equal(5, d.GetProperty("air").GetProperty("rejects").GetInt32());
    }

    [Fact]
    public void Taste_ranks_likes_per_hundred_with_glek_row_and_skips_people_below_the_threshold()
    {
        using var rig = new Rig();
        Assert.Equal(5, Litopys.TasteMin("week"));
        var t = Now.AddDays(-2);
        rig.Track("o", "Adele", "Hello");
        rig.Track("p", "Queen", "Radio Ga Ga");
        rig.Track("g", "Nirvana", "Lithium");
        rig.Track("y", "Muse", "Uprising");
        for (var i = 0; i < 5; i++) rig.Play("o", "user", t.AddMinutes(i * 10), by: "Оля", skipped: i == 0);
        for (var i = 0; i < 5; i++) rig.Play("p", "user", t.AddHours(2).AddMinutes(i * 10), by: "Петро");
        for (var i = 0; i < 4; i++) rig.Play("g", "autodj", t.AddHours(4).AddMinutes(i * 10));
        rig.Play("y", "user", t.AddHours(5), by: "Яся");   // один трек — у рейтинг не йде
        rig.Like("o", "Петро", t.AddMinutes(41));
        rig.Like("o", "Яся", t.AddMinutes(42));
        rig.Like("g", "Оля", t.AddHours(4).AddMinutes(31));

        var d = rig.Glek("week");
        var taste = d.GetProperty("taste").EnumerateArray().ToList();
        Assert.Equal(["Оля", GlekNick, "Петро"], taste.Select(x => x.GetProperty("nick").GetString()).ToList());
        Assert.True(taste[1].GetProperty("glek").GetBoolean());
        Assert.Equal(40.0, taste[0].GetProperty("per100").GetDouble());
        Assert.Equal(25.0, taste[1].GetProperty("per100").GetDouble());
        Assert.Equal(20, taste[0].GetProperty("skipPct").GetInt32());
        Assert.Equal(1, d.GetProperty("fewTaste").GetInt32());
        Assert.Equal(5, d.GetProperty("tasteMin").GetInt32());
    }

    // ---------- 🔥 прожарки й балачки ----------

    [Fact]
    public void Roasts_count_targets_and_buyers_hide_anonymous_ones_and_fresh_is_the_last_aired()
    {
        using var rig = new Rig();
        var t = Now.AddHours(-6);
        rig.Ad("roast", "Smaug", "aired", t, "Прожарка від Дядька Глека. На сковорідці — Smaug. [ба-дум-тсс]");
        rig.Ad("roast", "Smaug", "expired", t.AddMinutes(5));
        rig.Ad("roast", "Оля", "aired", t.AddMinutes(10), "Оля сьогодні тиха. Глек усе бачить.", buyer: "Петро", price: 100);
        rig.Ad("roast", "Smaug", "aired", t.AddMinutes(20), "Ще одна.", buyer: "Яся", anon: true, price: 150);
        rig.Ad("roast", "Оля", "failed", t.AddMinutes(30));

        var talk = rig.Glek("week").GetProperty("talk");
        Assert.Equal(4, talk.GetProperty("roasts").GetInt32());
        Assert.Equal(3, talk.GetProperty("aired").GetInt32());
        var targets = talk.GetProperty("targets");
        Assert.Equal(["Smaug", "Оля"], Nicks(targets));
        Assert.Equal(3, targets[0].GetProperty("written").GetInt32());
        Assert.Equal(2, targets[0].GetProperty("aired").GetInt32());
        Assert.Equal(["Петро"], Nicks(talk.GetProperty("buyers")));
        Assert.Equal(150, talk.GetProperty("anon").GetProperty("shards").GetInt32());
        Assert.DoesNotContain("Яся", talk.GetRawText());
        var fresh = talk.GetProperty("fresh");
        Assert.Equal("Ще одна.", fresh.GetProperty("text").GetString());
        Assert.Equal("Smaug", fresh.GetProperty("target").GetString());
    }

    [Fact]
    public void Quote_of_the_day_is_the_same_all_day_skips_today_and_private_lines()
    {
        using var rig = new Rig();
        var y = Now.AddDays(-1);
        rig.Say("Мафія перемогла. Тепер це їхнє село, а ви тут гості.", "dj", y);
        rig.Say("Дзвоніть мені на 067 123 45 67, я все розкажу про ваші ставки.", "dj", y);   // номер — ні
        rig.Say("Коротко.", "dj-game", y);                                                       // закоротке
        rig.Say("Цю фразу я сказав сьогодні, її ще рано цитувати, нехай настоїться.", "dj", Now.AddHours(-1));

        var q1 = rig.Glek("week").GetProperty("talk").GetProperty("quote");
        Assert.Equal("Мафія перемогла. Тепер це їхнє село, а ви тут гості.", q1.GetProperty("text").GetString());
        rig.E.Clock.UtcNow = Now.AddHours(5);   // той самий київський день, кеш вичах
        Assert.Equal(q1.GetProperty("text").GetString(), rig.Glek("day").GetProperty("talk").GetProperty("quote").GetProperty("text").GetString());
    }

    [Fact]
    public void Hosted_counts_rounds_of_games_he_leads_and_speech_in_chat()
    {
        using var rig = new Rig();
        var t = Now.AddHours(-2);
        foreach (var n in new[] { "Оля", "Петро", "Яся" })
        {
            rig.Result("m1", 1, "mafia", n, n == "Оля" ? "loss" : "win", t);
            rig.Result("m1", 2, "mafia", n, "win", t.AddMinutes(20));
        }
        rig.Result("x1", 1, "ttt", "Оля", "win", t);
        rig.Result("x1", 1, "ttt", "Петро", "loss", t);
        rig.Say("Голосуємо. Хто передумає — ще встигне, доки я рахую.", "dj", t);
        rig.Say("Найближче — Оля: різниця 9.", "dj-game", t);
        rig.Say("Тримайте: Adele — Hello — з нашого архіву.", "dj-auto", t);

        var talk = rig.Glek("week").GetProperty("talk");
        var hosted = talk.GetProperty("hosted");
        Assert.Equal(1, hosted.GetArrayLength());
        Assert.Equal("mafia", hosted[0].GetProperty("game").GetString());
        Assert.Equal("Мафія", hosted[0].GetProperty("title").GetString());
        Assert.Equal(2, hosted[0].GetProperty("rounds").GetInt32());
        Assert.Equal(2, talk.GetProperty("said").GetInt32());
        Assert.Equal(1, talk.GetProperty("announced").GetInt32());
    }

    // ---------- 💰 каса ----------

    [Theory]
    [InlineData("listen", "give")]
    [InlineData("win:ttt", "give")]
    [InlineData("draw:ttt", "give")]
    [InlineData("play:ttt", "give")]
    [InlineData("solo:wordle", "give")]
    [InlineData("points:ttt", "give")]
    [InlineData("daily:wordle", "give")]
    [InlineData("ach:first", "give")]
    [InlineData("clicker", "give")]
    [InlineData("ad:vote", "give")]
    [InlineData("award:x", "give")]
    [InlineData("stake:r:1", "tables")]
    [InlineData("stake-win:r:1", "tables")]
    [InlineData("stake-refund:r:1", "tables")]
    [InlineData("table-buyin:poker", "tables")]
    [InlineData("table-fee:poker", "tables")]
    [InlineData("table-cashout:poker", "tables")]
    [InlineData("table-prize:poker", "tables")]
    [InlineData("table-refund:poker", "tables")]
    [InlineData("shop:mushroom", "earn")]
    [InlineData("gift:mushroom", "earn")]
    [InlineData("curse:frog", "earn")]
    [InlineData("curse-ransom:1", "earn")]
    [InlineData("curse-reveal:1", "earn")]
    [InlineData("liveads:3", "earn")]
    [InlineData("liveads-refund:3", "earn")]
    [InlineData("ban:x", "earn")]
    [InlineData("unban:x", "earn")]
    [InlineData("ban-refund:x", "earn")]
    [InlineData("roulette-bet:roulette", "casino")]
    [InlineData("roulette-win:roulette", "casino")]
    [InlineData("roulette-back:roulette", "casino")]
    [InlineData("slot-bet:slot-glek", "casino")]
    [InlineData("slot-win:slot-glek", "casino")]
    [InlineData("slot-jackpot:slot-glek", "casino")]
    [InlineData("lelka-bet:1", "casino")]
    [InlineData("lelka-win:1", "casino")]
    [InlineData("lelka-back:1", "casino")]
    [InlineData("bet:event", "casino")]
    [InlineData("bet-win:table", "casino")]
    [InlineData("bet-back:event", "casino")]
    [InlineData("buy:17", "exchange")]
    [InlineData("buy-gift:17", "exchange")]
    [InlineData("sell:3", "exchange")]
    [InlineData("sell-back:3", "exchange")]
    [InlineData("щось-нове:1", "other")]
    public void Every_wallet_reason_has_its_place_in_the_kasa(string reason, string group) =>
        Assert.Equal(group, Litopys.KasaOf(reason).Group);

    [Fact]
    public void Kasa_is_the_other_side_of_the_ledger_with_casino_payers_and_robbers()
    {
        using var rig = new Rig();
        var t = Now.AddDays(-1);
        rig.Led("Оля", -300, "shop:mushroom", t);
        rig.Led("Оля", -100, "roulette-bet:roulette", t);
        rig.Led("Петро", -100, "roulette-bet:roulette", t);
        rig.Led("Петро", 350, "roulette-win:roulette", t);
        rig.Led("Яся", 20, "listen", t);
        rig.Led("Яся", 5, "щось-нове:1", t);
        rig.Led("Яся", 10, "listen", Now.AddDays(-30));   // поза тижнем

        var k = rig.Glek("week").GetProperty("kasa");
        Assert.Equal(300 + 100 + 100 - 350 - 20 - 5, k.GetProperty("total").GetInt64());
        var groups = k.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("key").GetString()!, g => g);
        Assert.Equal(300, groups["earn"].GetProperty("sum").GetInt64());
        var roulette = groups["casino"].GetProperty("items")[0];
        Assert.Equal(-150, roulette.GetProperty("sum").GetInt64());
        Assert.Equal(200, roulette.GetProperty("bets").GetInt64());
        Assert.Equal(350, roulette.GetProperty("wins").GetInt64());
        Assert.Equal(-20, groups["give"].GetProperty("sum").GetInt64());
        Assert.Equal("щось-нове", groups["other"].GetProperty("items")[0].GetProperty("key").GetString());

        var payers = k.GetProperty("payers");
        Assert.Equal(["Оля"], Nicks(payers));
        Assert.Equal(400, payers[0].GetProperty("sum").GetInt64());
        var gamblers = k.GetProperty("gamblers");
        Assert.Equal(["Петро", "Оля"], Nicks(gamblers));
        Assert.Equal(250, gamblers[0].GetProperty("net").GetInt64());
        Assert.Equal(-100, gamblers[1].GetProperty("net").GetInt64());

        // тиждень — по днях окремо, 7 точок; останні сім днів, вчора — уся каса дня
        var s = k.GetProperty("series");
        Assert.False(s.GetProperty("cumulative").GetBoolean());
        var pts = s.GetProperty("pts").EnumerateArray().ToList();
        Assert.Equal(7, pts.Count);
        Assert.Equal(300 + 100 + 100 - 350 - 20 - 5, pts[^2][1].GetInt64());
        Assert.Equal(-150, pts[^2][2].GetInt64());

        // за весь час — наростом: останнє значення = уся каса
        var all = rig.Glek("all").GetProperty("kasa");
        Assert.True(all.GetProperty("series").GetProperty("cumulative").GetBoolean());
        Assert.Equal(all.GetProperty("total").GetInt64(), all.GetProperty("series").GetProperty("pts").EnumerateArray().Last()[1].GetInt64());
        Assert.Equal(300 + 100 + 100 - 350 - 20 - 5 - 10, all.GetProperty("total").GetInt64());
    }

    [Fact]
    public void Day_kasa_goes_by_hours()
    {
        using var rig = new Rig();
        rig.Led("Оля", -50, "ban:x", Now.AddHours(-1));
        var s = rig.Glek("day").GetProperty("kasa").GetProperty("series");
        Assert.Equal("hour", s.GetProperty("unit").GetString());
        var pts = s.GetProperty("pts").EnumerateArray().ToList();
        Assert.Equal(13, pts.Count);   // 00:00…12:00 за Києвом
        Assert.Equal(50, pts[^2][1].GetInt64());
    }

    [Fact]
    public void Empty_period_draws_nothing_and_does_not_fall()
    {
        using var rig = new Rig();
        var d = rig.Glek("day");
        Assert.Equal(0, d.GetProperty("air").GetProperty("glek").GetInt32());
        Assert.Equal(0, d.GetProperty("taste").GetArrayLength());
        Assert.Equal(0, d.GetProperty("critics").GetArrayLength());
        Assert.Equal(0, d.GetProperty("kasa").GetProperty("groups").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("talk").GetProperty("quote").ValueKind);
        Assert.Equal(JsonValueKind.Null, d.GetProperty("talk").GetProperty("fresh").ValueKind);
        Assert.Equal("Дядько Глек", d.GetProperty("dj").GetString());
    }

    [Fact]
    public void Answer_is_cached_for_a_minute()
    {
        using var rig = new Rig();
        Assert.Equal(0, rig.Glek("week").GetProperty("air").GetProperty("glek").GetInt32());
        rig.Track("a", "Adele", "Hello");
        rig.Play("a", "autodj", Now.AddMinutes(-5));
        Assert.Equal(0, rig.Glek("week").GetProperty("air").GetProperty("glek").GetInt32());
        rig.E.Clock.UtcNow = Now + Litopys.CacheFor;
        Assert.Equal(1, rig.Glek("week").GetProperty("air").GetProperty("glek").GetInt32());
    }
}
