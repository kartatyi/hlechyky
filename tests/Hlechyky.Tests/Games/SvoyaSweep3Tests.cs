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
    public void A_miss_voided_by_an_appeal_is_not_the_dearest_miss()
    {
        var packs = new FakeSvoyaPacks();
        packs.Packs["b_g"] = (Grid("b_g", 1, 1), "");
        var h = Table(packs, "b_g");
        SvoyaTests.Open(h, 0, 0);
        Answer(h, 0, "т0п9");                                 // Оля: автомат не взяв, −100
        Assert.True(h.Act(1, "buzz").Ok, h.Reply.Message);
        Assert.True(h.Act(1, "answer", new { text = "т0п0" }).Ok);
        Assert.Equal(Svoya.Reveal, SvoyaTests.Phase(h));
        Assert.True(h.Act(0, "appeal").Ok, h.Reply.Message);
        Assert.True(h.Act(0, "judge", new { seat = 0, accept = true }).Ok, h.Reply.Message);   // промах Олі скасовано, плюс Петра — теж
        SvoyaTests.Until(h, Svoya.Strike);
        Assert.True(h.Act(0, "strike", new { theme = 0 }).Ok);
        Assert.True(h.Act(0, "strike", new { theme = 1 }).Ok);
        Assert.True(h.Act(0, "bet", new { amount = 1 }).Ok);
        SvoyaTests.Until(h, Svoya.FinalQuestion);
        Assert.True(h.Act(0, "answer", new { text = "ф2" }).Ok);
        SvoyaTests.Until(h, Svoya.Done, 400);
        Assert.DoesNotContain(Awards(h), a => a.GetProperty("icon").GetString() == "💸");
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

    // ---------- 19. «🎲 Мікс» незіграних тем ----------

    static string TempDb() => Path.Combine(Path.GetTempPath(), $"svoya-s3-{Guid.NewGuid():N}.db");

    static void Drop(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(f); } catch (IOException) { /* хай лежить у temp */ }
    }

    static string[] Themes(SvoyaPack p, bool final = false) => [.. p.Rounds.Where(r => r.IsFinal == final).SelectMany(r => r.Themes).Select(t => t.Name)];

    [Fact]
    public void Mix_takes_unseen_themes_first_keeps_difficulty_and_reprices()
    {
        var a = Grid("b_a", 5, 5, rounds: 3);
        var b = Grid("b_b", 5, 5, rounds: 3);
        var seen = a.Rounds.Where(r => !r.IsFinal).SelectMany((r, ri) => r.Themes.Select(t => SvoyaMix.Key(a.Id, ri, r, t)))
            .ToDictionary(k => k, _ => DateTimeOffset.UnixEpoch);
        seen["b_a/0/b_a р0 т0"] = DateTimeOffset.UnixEpoch.AddDays(1);
        var mix = SvoyaMix.Build([a, b], seen, new Random(3), out var fresh)!;

        Assert.Equal(SvoyaMix.Id, mix.Id);
        Assert.Empty(mix.Validate());
        Assert.Equal(4, mix.Rounds.Count);                                               // три раунди й фінал
        Assert.All(Themes(mix), n => Assert.StartsWith("b_b", n));                        // усе бачене — позаду
        Assert.All(mix.Rounds[0].Themes, t => Assert.StartsWith("b_b р0", t.Name));       // легкі — у першому раунді
        Assert.All(mix.Rounds[2].Themes, t => Assert.StartsWith("b_b р2", t.Name));
        Assert.Equal([300, 600, 900, 1200, 1500], mix.Rounds[2].Themes[0].Questions.Select(q => q.Price));
        Assert.Equal(15 + 5, fresh);                                                      // і фінальні теми ще ніхто не бачив
        Assert.Contains("Усі 20 тем", mix.Description);
        Assert.Equal(2, b.Rounds[2].Themes[0].Questions[0].Price / 100 - 1);                // рідний пакет не зачеплено (300)
    }

    [Fact]
    public void A_short_blitz_mix_describes_and_counts_only_what_is_on_the_board()
    {
        // Рецензія проходу №3: з «Один раунд і фінал» чи бліцом опис міксу казав «Усі 20 тем», а список — «80 запитань».
        var a = Grid("b_a", 5, 5, rounds: 3);
        var b = Grid("b_b", 5, 5, rounds: 3);
        var h = Table(new FakeSvoyaPacks(), SvoyaMix.Id, new { length = "one", pace = "blitz" },
            more: sc => sc.AddSingleton(new SvoyaBuiltin([a, b])), start: false);
        var pack = h.View(0).GetProperty("pack");
        var rounds = pack.GetProperty("rounds").EnumerateArray().ToList();
        Assert.Equal(2, rounds.Count);                                                   // один раунд і фінал
        var themes = rounds.Sum(r => r.GetProperty("themes").GetArrayLength());
        var finals = rounds[1].GetProperty("themes").GetArrayLength();
        Assert.Equal(4 + finals, themes);                                                // поле бліцу — 4 теми
        Assert.Contains($"Усі {themes} тем", pack.GetProperty("description").GetString());
        Assert.Equal(4 * 4 + finals, pack.GetProperty("questions").GetInt32());

        // повна довжина — опис як і був, на весь мікс
        var full = Table(new FakeSvoyaPacks(), SvoyaMix.Id, more: sc => sc.AddSingleton(new SvoyaBuiltin([a, b])), start: false);
        Assert.Contains($"Усі {15 + finals} тем", full.View(0).GetProperty("pack").GetProperty("description").GetString());
        Assert.Equal(75 + finals, full.View(0).GetProperty("pack").GetProperty("questions").GetInt32());
    }

    [Fact]
    public void Mix_skips_themes_with_media()
    {
        var a = Grid("b_a", 2, 2);
        a.Rounds[0].Themes[0].Questions[0].Media = new SvoyaMedia { Kind = SvoyaMedia.Image, File = "abc.jpg" };
        var mix = SvoyaMix.Build([a], new Dictionary<string, DateTimeOffset>(), new Random(1), out _)!;
        Assert.Equal(["b_a р0 т1"], Themes(mix));
    }

    [Fact]
    public void Themes_seen_at_one_table_are_left_out_of_the_mix_at_the_next()
    {
        var path = TempDb();
        try
        {
            var db = new Db(path);
            var a = Grid("b_a", 5, 5, rounds: 3);
            var b = Grid("b_b", 5, 5, rounds: 3);
            var packs = new FakeSvoyaPacks();
            packs.Packs["b_a"] = (a, "");
            void Services(ServiceCollection sc)
            {
                sc.AddSingleton(db);
                sc.AddSingleton(new SvoyaBuiltin([a, b]));
            }

            Table(packs, "b_a", more: Services);                                           // перший раунд b_a — на полі: бачили
            SvoyaSeen.Idle.Wait();

            var h = Table(packs, SvoyaMix.Id, more: Services, start: false);
            Assert.Equal(SvoyaMix.Title, h.View(0).GetProperty("pack").GetProperty("title").GetString());
            SvoyaSeen.Idle.Wait();                                                            // пам'ять столу підтяглась з бази
            h.View(0);                                                                        // …і мікс зібрано під неї
            var rounds = h.View(0).GetProperty("pack").GetProperty("rounds");
            var names = rounds.EnumerateArray().SelectMany(r => r.GetProperty("themes").EnumerateArray()).Select(t => t.GetString()!).ToList();
            Assert.DoesNotContain(names, n => n.StartsWith("b_a р0", StringComparison.Ordinal));
            Assert.True(h.Start().Ok, h.Reply.Message);
            SvoyaTests.Until(h, Svoya.Board);
            Assert.Equal(5, h.View(null).GetProperty("board").GetArrayLength());
        }
        finally { Drop(path); }
    }

    // ---------- 18. «👥 Про нас» ----------

    static SvoyaAbout.Facts Rich() => new()
    {
        WeekRequests = [new("владік", 20), new("Smaug", 12)],
        WeekLikes = [new("Smaug", 115), new("владік", 13)],
        TopTracks = [new("Реклама глека", 30, "18+ Анекдоти"), new("Я Канівес", 20, "MC Петя"), new("Потяг на Південь", 19, "Zwyntar")],
        LikedTracks = [new("Riders on the Storm", 4, "The Doors"), new("The House of the Rising Sun", 4, "The Animals")],   // нічия — не питаємо
        WeekGames = [new("tanks", 28), new("duel", 15)],
        WeekWins = [new("владік", 73), new("микола ( справжній )", 53)],
        GameWinners = [("tanks", [new("микола ( справжній )", 30), new("Smaug", 10)])],
        Earned = [new("владік", 4802), new("Smaug", 3330)],
        Achievements = [new("Smaug", 61), new("владік", 60)],
    };

    static string? Title(string id) => id switch { "tanks" => "Танчики", "duel" => "Дуель", _ => null };

    [Fact]
    public void About_asks_only_clear_facts_with_a_single_leader()
    {
        var (pack, n, reason) = SvoyaAbout.Compose(Rich(), Title, DateTimeOffset.UnixEpoch);
        Assert.NotNull(pack);
        Assert.Equal("", reason);
        Assert.Empty(pack.Validate());
        var qs = pack.Rounds.Single().Themes.SelectMany(t => t.Questions).ToList();
        Assert.Equal(n, qs.Count);
        Assert.Equal(["Радіо", "Ігри", "Черепки й коло"], Themes(pack));
        Assert.Contains(qs, q => q.Answer == "Я Канівес" && q.Comment!.Contains("MC Петя"));   // реклама — не пісня
        Assert.DoesNotContain(qs, q => q.Answer is "Riders on the Storm" or "The House of the Rising Sun");
        Assert.Contains(qs, q => q.Answer == "Танчики");
        var tanks = qs.Single(q => q.Text.Contains("«Танчики»"));
        Assert.Equal("микола ( справжній )", tanks.Answer);
        Assert.True(SvoyaAnswer.Hits("микола", tanks.Answers));                           // друзі пишуть коротко
        Assert.False(SvoyaAnswer.Hits("Smaug", tanks.Answers));
        Assert.All(qs, q => Assert.False(string.IsNullOrEmpty(q.Comment)));                // число для перевірки — завжди
    }

    [Fact]
    public void About_is_unavailable_with_a_reason_when_the_site_is_quiet()
    {
        var quiet = new SvoyaAbout.Facts { WeekRequests = [new("Оля", 5)], WeekWins = [new("Оля", 2), new("Петро", 2)] };
        var (pack, n, reason) = SvoyaAbout.Compose(quiet, Title, DateTimeOffset.UnixEpoch);
        Assert.Null(pack);
        Assert.Equal(1, n);
        Assert.Contains("Замало", reason);
    }

    [Fact]
    public void Nick_answers_accept_the_short_forms()
    {
        Assert.Contains("микола", SvoyaAbout.NickAccept("микола ( справжній )"));
        Assert.Contains("Ivan", SvoyaAbout.NickAccept("гість Ivan"));
        Assert.Contains("Mariana", SvoyaAbout.NickAccept("Mariana Matviienko"));
        Assert.Empty(SvoyaAbout.NickAccept("Smaug"));
        Assert.Equal(["Stefania"], SvoyaAbout.TitleAccept("Stefania (Kalush Orchestra)"));
    }

    [Fact]
    public void About_reads_the_site_database_and_the_game_plays_it()
    {
        var path = TempDb();
        try
        {
            var db = new Db(path);
            var clock = new FakeClock();
            var at = clock.UtcNow.AddDays(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            db.With(c =>
            {
                using var cmd = c.CreateCommand();
                var sql = new System.Text.StringBuilder();
                sql.Append($"INSERT INTO tracks(id, title, artist, source_url, created_at) VALUES('t1', 'Потяг на Південь', 'Zwyntar', 'x', '{at}'), ('t2', 'Інша', 'Хтось', 'x', '{at}');");
                for (var i = 0; i < 5; i++) sql.Append($"INSERT INTO plays(track_id, source, requested_by, started_at) VALUES('t1', 'user', 'Оля', '{at}');");
                sql.Append($"INSERT INTO plays(track_id, source, requested_by, started_at) VALUES('t2', 'user', 'Петро', '{at}');");
                for (var i = 0; i < 4; i++) sql.Append($"INSERT INTO likes(track_id, nick, created_at) VALUES('t{i % 2 + 1}', 'нік{i}', '{at}');");   // нічия 2:2 — без запитання
                for (var i = 0; i < 3; i++) sql.Append($"INSERT INTO achievements(nick_key, key, nick, unlocked_at) VALUES('оля', 'a{i}', 'Оля', '{at}');");
                for (var i = 0; i < 6; i++)
                    sql.Append($"INSERT INTO game_results(room_id, game, round, nick_key, nick, outcome, created_at) VALUES('r{i}', 'tanks', 1, 'оля', 'Оля', 'win', '{at}'), ('r{i}', 'tanks', 1, 'петро', 'Петро', 'loss', '{at}');");
                sql.Append($"INSERT INTO wallets(nick_key, nick, balance, earned, updated_at) VALUES('петро', 'Петро', 10, 500, '{at}'), ('оля', 'Оля', 5, 90, '{at}');");
                cmd.CommandText = sql.ToString();
                return cmd.ExecuteNonQuery();
            });
            var about = new SvoyaAbout(db, clock, Title);
            var snap = about.Get();
            Assert.True(snap.Pack is not null, snap.Reason);
            var qs = snap.Pack.Rounds[0].Themes.SelectMany(t => t.Questions).ToList();
            Assert.Contains(qs, q => q.Text.Contains("закинув") && q.Answer == "Оля");
            Assert.Contains(qs, q => q.Answer == "Потяг на Південь");
            Assert.Contains(qs, q => q.Text.Contains("«Танчики»") && q.Answer == "Оля");
            Assert.Contains(qs, q => q.Text.Contains("черепків") && q.Answer == "Петро");

            // гра бере готовий знімок із пам'яті; порожній сервіс — «збираю», а не база під замком
            var packs = new FakeSvoyaPacks();
            var cold = Table(packs, "b_mini", more: sc => sc.AddSingleton(new SvoyaAbout(null, clock)), start: false);
            Assert.Contains("Збираю факти", cold.Act(0, "pack", new { id = SvoyaAbout.Id }).Message);
            var h = Table(packs, "b_mini", more: sc => sc.AddSingleton(about), start: false);
            Assert.True(h.Act(0, "pack", new { id = SvoyaAbout.Id }).Ok, h.Reply.Message);
            Assert.True(h.Start().Ok, h.Reply.Message);
            var c = SvoyaTests.Open(h, 0, 0);
            Assert.Contains("?", h.View(null).GetProperty("question").GetProperty("text").GetString());
            Assert.True(c >= 0);
        }
        finally { Drop(path); }
    }
}
