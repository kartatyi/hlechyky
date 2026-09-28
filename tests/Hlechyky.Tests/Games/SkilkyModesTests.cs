using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Скільки?», прохід №3: «Ставлю на чуже», команди, питання про нас, фото «Якого року?» і «Скільки? дня».
/// </summary>
public class SkilkyModesTests
{
    static readonly string[] Nicks = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан"];
    const int MaxTicks = 400;

    static RoomHarness Table(int players, object? options = null, int seed = 42, IServiceProvider? services = null, Action<RoomHarness>? lobby = null)
    {
        var h = new RoomHarness("skilky", options: options, seed: seed, services: services);
        for (var i = 0; i < players; i++) h.Join(Nicks[i]);
        lobby?.Invoke(h);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;
    static long Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt64();

    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < MaxTicks && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++) h.Tick(1);
    }

    static double Truth(RoomHarness h)
    {
        var text = h.View(null).GetProperty("question").GetString();
        return SkilkyBank.All.First(q => q.Q == text).A!.Value;
    }

    /// <summary>Число, що дає рівно стільки очок за точність (звичайні числа й роки).</summary>
    static double Off(double truth, bool years, int how) => how switch
    {
        0 => truth,
        1 => years ? truth + 10 : truth * 1.4,
        _ => years ? truth + 400 : truth * 50,
    };

    static bool Years(RoomHarness h) => h.View(null).GetProperty("unit").GetString() == "рік";

    // ---------- «Ставлю на чуже» ----------

    [Fact]
    public void Bets_open_the_numbers_without_the_truth_and_pay_two_for_the_closest()
    {
        var h = Table(3, new { bets = "on" });
        Until(h, Skilky.PhaseAsk);
        var t = Truth(h);
        var y = Years(h);
        Assert.True(h.Act(0, "answer", new { value = Off(t, y, 0) }).Ok);
        Assert.True(h.Act(1, "answer", new { value = Off(t, y, 1) }).Ok);
        Assert.True(h.Act(2, "answer", new { value = Off(t, y, 2) }).Ok);
        h.Tick(1);
        Assert.Equal(Skilky.PhaseBet, Phase(h));

        // Числа на столі, правди ще нема — ні в кого.
        var bet = h.View(1).GetProperty("bet");
        Assert.Equal(3, bet.GetProperty("values").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, h.View(1).GetProperty("reveal").ValueKind);

        Assert.False(h.Act(1, "bet", new { seat = 1 }).Ok);           // на себе не можна
        Assert.True(h.Act(1, "bet", new { seat = 0 }).Ok);            // Петро вгадав: Оля найточніша
        Assert.True(h.Act(2, "bet", new { seat = 1 }).Ok);            // Ганна — ні
        Assert.Equal(0, h.View(1).GetProperty("bet").GetProperty("on").GetInt32());
        var before1 = Score(h, 1);
        var before2 = Score(h, 2);
        Assert.True(h.Act(0, "bet", new { seat = 2 }).Ok);            // усі поставили — розкриття наступним тиком
        h.Tick(1);
        Assert.Equal(Skilky.PhaseReveal, Phase(h));
        var reveal = h.View(null).GetProperty("reveal");
        var bets = reveal.GetProperty("bets").EnumerateArray().ToList();
        Assert.Equal(3, bets.Count);
        Assert.True(bets.Single(b => b.GetProperty("seat").GetInt32() == 1).GetProperty("ok").GetBoolean());
        Assert.False(bets.Single(b => b.GetProperty("seat").GetInt32() == 2).GetProperty("ok").GetBoolean());
        var row1 = reveal.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("seat").GetInt32() == 1).GetProperty("points").GetInt32();
        var row2 = reveal.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("seat").GetInt32() == 2).GetProperty("points").GetInt32();
        Assert.Equal(before1 + row1 + Skilky.BetBonus, Score(h, 1));
        Assert.Equal(before2 + row2, Score(h, 2));
    }

    [Fact]
    public void Without_the_option_or_with_two_players_there_is_no_betting()
    {
        foreach (var (players, opts) in new[] { (3, (object?)null), (2, new { bets = "on" }) })
        {
            var h = Table(players, opts);
            Until(h, Skilky.PhaseAsk);
            var t = Truth(h);
            for (var s = 0; s < players; s++) Assert.True(h.Act(s, "answer", new { value = t }).Ok);
            h.Tick(1);
            Assert.Equal(Skilky.PhaseReveal, Phase(h));
            Assert.False(h.Act(0, "bet", new { seat = 1 }).Ok);
        }
    }

    // ---------- команди ----------

    [Fact]
    public void Teams_play_with_one_number_from_the_captain_and_share_the_points()
    {
        var h = Table(4, new { teams = "2" });
        var teams = h.View(null).GetProperty("teams");
        Assert.Equal(2, teams.GetArrayLength());
        Assert.Equal([0, 2], teams[0].GetProperty("seats").EnumerateArray().Select(x => x.GetInt32()));
        Until(h, Skilky.PhaseAsk);
        var t = Truth(h);
        var y = Years(h);
        Assert.Equal(0, h.View(null).GetProperty("teams")[0].GetProperty("captain").GetInt32());

        // Ганна пропонує — свої бачать і голосують, чужі не бачать нічого.
        Assert.True(h.Act(2, "answer", new { value = 123456.5 }).Ok);
        Assert.Contains("123456.5", h.View(0).GetProperty("team").GetRawText());
        Assert.DoesNotContain("123456.5", h.View(1).GetRawText());
        Assert.DoesNotContain("123456.5", h.View(null).GetRawText());
        Assert.True(h.Act(0, "vote", new { seat = 2, up = true }).Ok);
        Assert.False(h.Act(1, "vote", new { seat = 2, up = true }).Ok);   // чужа команда
        Assert.Equal(1, h.View(2).GetProperty("team").GetProperty("drafts")[0].GetProperty("up").GetInt32());

        Assert.True(h.Act(0, "answer", new { value = Off(t, y, 0) }).Ok);   // капітан подає
        Assert.True(h.Act(1, "answer", new { value = Off(t, y, 2) }).Ok);   // капітан синіх — мимо
        h.Tick(1);
        Assert.Equal(Skilky.PhaseReveal, Phase(h));
        var rows = h.View(null).GetProperty("reveal").GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(0, rows[0].GetProperty("team").GetInt32());
        Assert.Equal(Score(h, 0), Score(h, 2));
        Assert.Equal(Score(h, 1), Score(h, 3));
        Assert.True(Score(h, 0) > Score(h, 1));
    }

    [Fact]
    public void A_silent_captain_leaves_the_team_with_its_most_liked_proposal()
    {
        var h = Table(4, new { teams = "2" });
        Until(h, Skilky.PhaseAsk);
        var t = Truth(h);
        Assert.True(h.Act(2, "answer", new { value = t }).Ok);
        Assert.True(h.Act(0, "vote", new { seat = 2 }).Ok);
        Assert.True(h.Act(1, "answer", new { value = t }).Ok);
        Until(h, Skilky.PhaseReveal);
        var rows = h.View(null).GetProperty("reveal").GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(0, r.GetProperty("diff").GetDouble(), 6));
    }

    // ---------- питання про нас ----------

    [Fact]
    public void Our_question_is_asked_and_its_author_only_watches()
    {
        const string text = "Скільки кілометрів Петро проїхав на велику цього літа";
        var h = Table(3, new { questions = "3" }, lobby: x =>
        {
            Assert.False(x.Act(1, "ours", new { q = "Ну?", a = 5 }).Ok);                // закоротке
            Assert.False(x.Act(1, "ours", new { q = text, a = "багато" }).Ok);               // не число
            Assert.True(x.Act(1, "ours", new { q = text, a = "1 200", unit = "км" }).Ok);
            Assert.Equal(text + "?", x.View(1).GetProperty("ours").GetProperty("mine").GetProperty("q").GetString());
            Assert.Equal(JsonValueKind.Null, x.View(0).GetProperty("ours").GetProperty("mine").ValueKind);
            Assert.Equal([1], x.View(0).GetProperty("ours").GetProperty("seats").EnumerateArray().Select(e => e.GetInt32()));
        });
        Assert.Equal(3, h.View(null).GetProperty("of").GetInt32());
        for (var i = 0; i < 3; i++)
        {
            Until(h, Skilky.PhaseAsk);
            if (h.View(null).GetProperty("question").GetString() == text + "?")
            {
                Assert.Equal(1, h.View(null).GetProperty("by").GetInt32());
                Assert.False(h.Act(1, "answer", new { value = 1200 }).Ok);
                Assert.True(h.Act(0, "answer", new { value = 1200 }).Ok);
                Assert.True(h.Act(2, "answer", new { value = 600 }).Ok);
                h.Tick(1);   // автор мовчить, а розкриття — одразу
                Assert.Equal(Skilky.PhaseReveal, Phase(h));
                Assert.Equal(1200, h.View(null).GetProperty("reveal").GetProperty("answer").GetDouble());
                return;
            }
            Until(h, Skilky.PhaseReveal);
        }
        Assert.Fail("своє питання так і не прозвучало");
    }

    // ---------- фото «Якого року?» ----------

    static SkilkyPhotos Photos(string dir, int count = 3)
    {
        var list = Enumerable.Range(0, count).Select(i => new SkilkyPhoto
        {
            Id = "test-" + i, File = "File:T" + i + ".jpg", Url = "https://upload.wikimedia.org/x/" + i + ".jpg", Year = 1950 + i * 10,
            Caption = "Підпис " + i, Author = "Автор", License = "CC BY-SA 4.0", Page = "https://commons.wikimedia.org/wiki/File:T" + i + ".jpg",
        }).ToList();
        return SkilkyPhotos.Offline(list, dir);
    }

    static byte[] Jpeg => [0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xDA, 0x00, 0x02, 0x01, 0x02, 0xFF, 0xD9];

    [Fact]
    public void Photo_topic_waits_for_photos_and_then_asks_the_year_with_credit_after()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sk-photo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var photos = Photos(dir);
            var services = RoomHarness.WithService(photos);
            var h = new RoomHarness("skilky", options: new { topic = "photo", questions = "3" }, services: services);
            h.Join("Оля");
            Assert.False(h.Start().Ok);                                        // фото ще нема — не стартуємо
            foreach (var p in photos.All) Assert.True(photos.Store(p.Id, Jpeg));
            Assert.True(h.Start().Ok);
            Until(h, Skilky.PhaseAsk);
            var v = h.View(0);
            var url = v.GetProperty("photo").GetString()!;
            Assert.StartsWith("/api/games/skilky/photo/", url);
            Assert.DoesNotContain("test-", url);                               // рік і назву файлу з адреси не підглянеш
            Assert.Equal(JsonValueKind.Null, v.GetProperty("credit").ValueKind);
            Assert.NotNull(photos.Resolve(url[24..^4]));
            Assert.True(h.Act(0, "answer", new { value = 1960 }).Ok);
            h.Tick(1);
            Assert.Equal("Автор", h.View(0).GetProperty("credit").GetProperty("author").GetString());
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Manifest_in_the_repo_is_valid_and_free()
    {
        var list = SkilkyPhotos.Load(Paths.Resolve(SkilkyPhotos.FileName));
        if (list.Count == 0) return;   // бібліотеку ще добирають
        Assert.Equal(list.Count, list.Select(p => p.Id).Distinct().Count());
        Assert.All(list, p =>
        {
            Assert.InRange(p.Year, 1826, 2025);
            Assert.False(string.IsNullOrWhiteSpace(p.Caption), p.Id);
            Assert.False(string.IsNullOrWhiteSpace(p.Author), p.Id);
            Assert.Matches("^(Public domain|PD|CC0|CC BY|CC BY-SA)", p.License);
            Assert.StartsWith("https://commons.wikimedia.org/wiki/File:", p.Page);
        });
    }

    [Fact]
    public async Task Heavy_photo_is_shrunk_on_the_way_to_cache_and_light_one_is_not()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sk-shrink-" + Guid.NewGuid().ToString("N"));
        try
        {
            var calls = 0;
            var list = Photos(dir).All;
            var photos = SkilkyPhotos.Offline(list, dir, shrink: (bytes, _) => { calls++; return Task.FromResult<byte[]?>(Jpeg); });

            Assert.True(await photos.StoreAsync("test-0", Jpeg, CancellationToken.None));          // легке — як є
            Assert.Equal(0, calls);

            var heavy = new byte[SkilkyPhotos.TargetBytes + 1000];
            Assert.True(await photos.StoreAsync("test-1", heavy, CancellationToken.None));         // важке — через перетискач
            Assert.Equal(1, calls);
            Assert.True(await photos.StoreAsync("test-0", [.. Jpeg, 0x00, 0x01], CancellationToken.None));   // кривий хвіст — теж
            Assert.Equal(2, calls);
            calls = 1;
            Assert.Equal(Jpeg.Length, new FileInfo(photos.PathFor("test-1")).Length);

            // Докачане ще до перетискання: важкий файл у кеші — перетиснути раз, і більше не смикати.
            File.WriteAllBytes(photos.PathFor("test-2"), heavy);
            photos.Rescan();
            Assert.Equal(1, await photos.ShrinkCachedAsync(CancellationToken.None));
            Assert.Equal(2, calls);
            Assert.True(new FileInfo(photos.PathFor("test-2")).Length <= SkilkyPhotos.TargetBytes);
            Assert.Equal(0, await photos.ShrinkCachedAsync(CancellationToken.None));
            Assert.Equal(2, calls);

            // Перетискач упав — не кидаємо: кладемо як є (тут байти не JPEG, тож кеш їх просто не бере).
            var broken = SkilkyPhotos.Offline(list, dir, shrink: (_, _) => throw new InvalidOperationException("ffmpeg нема"));
            Assert.False(await broken.StoreAsync("test-0", heavy, CancellationToken.None));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Seen_memory_is_filled_by_the_room_at_once_and_by_the_database_in_the_background()
    {
        using var temp = new TempDb();
        var seen = new SkilkySeen(temp.Db);
        var q = SkilkyBank.All[0];
        var at = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        // Кімната: пам'ять — одразу, база — після фону.
        seen.Remember([SkilkySeen.NickKey("Оля")], q, at);
        Assert.Equal(at, seen.Peek([SkilkySeen.NickKey("Оля")])[q.Key]);
        seen.Flush();
        Assert.Equal(at, seen.LastSeen([SkilkySeen.NickKey("Оля")])[q.Key]);

        // Рядок, записаний «до рестарту» (просто в базу, мимо пам'яті), кімната побачить після прогріву фоном.
        var old = SkilkyBank.All[1];
        temp.Db.With(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO skilky_seen(nick_key, q_key, seen_at) VALUES('петро', $q, $at)";
            cmd.Parameters.AddWithValue("$q", old.Key);
            cmd.Parameters.AddWithValue("$at", at.AddDays(-3).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        });
        seen.Warm();
        Assert.Equal(at.AddDays(-3), seen.Peek(["петро"])[old.Key]);
        // Бачене Олею не губиться після прогріву, а спільна пам'ять однакова для всіх кімнат цієї бази.
        Assert.Equal(2, new SkilkySeen(temp.Db).Peek(["петро", SkilkySeen.NickKey("Оля")]).Count);
    }

    // ---------- «Скільки? дня» ----------

    [Fact]
    public void Daily_gives_everyone_the_same_five_and_records_the_day_once()
    {
        var board = new SkilkyDailyBoard(null);
        var services = RoomHarness.WithService(board);
        string[] Questions(string nick, out RoomHarness h)
        {
            h = new RoomHarness("skilky-daily", services: services);
            h.Solo(nick);
            var list = new List<string>();
            for (var i = 0; i < SkilkyDaily.DayQuestions; i++)
            {
                Until(h, Skilky.PhaseAsk);
                list.Add(h.View(0).GetProperty("question").GetString()!);
                Assert.False(h.Act(0, "ours", new { q = "Скільки років нашому столу", a = 3 }).Ok);
                Assert.True(h.Act(0, "answer", new { value = Truth(h) }).Ok);
                Until(h, Skilky.PhaseReveal);
            }
            Until(h, Skilky.PhaseDone);
            return [.. list];
        }
        var a = Questions("Оля", out var ha);
        var b = Questions("Петро", out var hb);
        Assert.Equal(a, b);
        Assert.Equal(5, a.Distinct().Count());
        Assert.Single(ha.Finished);
        Assert.Contains("25 з 25", ha.Finished[0].Result.Text);
        Assert.Contains(ha.Awards, x => x.Reason == "daily:skilky-daily");
        var daily = hb.View(0).GetProperty("daily");
        Assert.Equal(2, daily.GetProperty("board").GetArrayLength());
        Assert.StartsWith("Скільки? дня №", daily.GetProperty("share").GetString());
        // «Ще раз» на зіграному дні нової партії не дає.
        ha.Rematch("Оля");
        Assert.Equal(Skilky.PhaseDone, Phase(ha));
        Assert.Single(board.Top(Days.Today(ha.Clock)).Where(r => r.Nick == "Оля"));
    }
}
