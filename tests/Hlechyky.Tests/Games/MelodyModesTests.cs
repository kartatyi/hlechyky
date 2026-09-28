using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Вгадай мелодію», прохід №3 (specs/melody.md, «Прохід №3»): бонус швидкості, «з першої ноти», варіанти на вибір,
/// «Хто закинув?», команди на дивані, фінальна дуель.
/// </summary>
public class MelodyModesTests
{
    static MelodyTrack T(string id, string artist, string title) => new(id, title, artist, 200, null, "/dev/null");

    static readonly MelodyTrack[] Songs =
    [
        T("a", "Океан Ельзи", "Обійми"),
        T("b", "Скрябін", "Старі фотографії"),
        T("c", "DakhaBrakha", "Vesna"),
        T("d", "KALUSH", "Stefania"),
        T("e", "The Hardkiss", "Journey"),
    ];

    static RoomHarness Table(FakeMelodySource source, object options, params string[] nicks)
    {
        var h = new RoomHarness("melody", options: options, seed: 3, services: RoomHarness.WithService<IMelodySource>(source));
        foreach (var n in nicks.Length == 0 ? ["Оля", "Петро"] : nicks) Assert.True(h.Join(n).Ok);
        h.Start();
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static void Until(RoomHarness h, string phase)
    {
        for (var i = 0; i < 400 && h.Room.Status == RoomStatus.Playing && Phase(h) != phase; i++)
        {
            h.Tick();
            if (Phase(h) != phase) Thread.Sleep(2);
        }
        Assert.Equal(phase, Phase(h));
    }

    static ActResult Guess(RoomHarness h, int seat, string text)
    {
        h.Clock.AdvanceMs(Melody.GuessEveryMs);
        return h.Act(seat, "guess", new { text });
    }

    static ActResult Do(RoomHarness h, int seat, string action, object payload)
    {
        h.Clock.AdvanceMs(Melody.GuessEveryMs);
        return h.Act(seat, action, payload);
    }

    static int Score(RoomHarness h, int seat) => h.View(null).GetProperty("scores")[seat].GetInt32();

    /// <summary>Дочекати кінця раунду (час вийшов) — розкриття.</summary>
    static void EndRound(RoomHarness h)
    {
        h.Clock.AdvanceMs(60_000);
        h.Tick();
        Assert.Equal("reveal", Phase(h));
    }

    /// <summary>Розкриття скінчилось — наступний трек.</summary>
    static void NextRound(RoomHarness h)
    {
        h.Clock.AdvanceMs(Melody.RevealMs + 100);
        h.Tick();
        Until(h, "play");
    }

    static string Clip(RoomHarness h) => h.View(0).GetProperty("clip").GetString()!;

    // ---------------------------------------------------------------- бонус швидкості

    [Fact]
    public void Speed_bonus_is_a_little_extra_once_per_track_and_only_in_the_first_seconds()
    {
        var h = Table(new FakeMelodySource(Songs), new { rounds = "5" });
        Until(h, "play");
        var r = Guess(h, 0, "Океан Ельзи");
        Assert.True(r.Ok);
        Assert.Contains("⚡ з 1 секунди", r.Message);
        Assert.Equal(Melody.ArtistPoints + Melody.ArtistFirst + Melody.SpeedBonus, Score(h, 0));
        // той самий гравець далі — бонус уже взяв
        Assert.True(Guess(h, 0, "Обійми").Ok);
        Assert.Equal(Melody.ArtistPoints + Melody.ArtistFirst + Melody.SpeedBonus + Melody.TitlePoints + Melody.TitleFirst, Score(h, 0));
        // інший — уже після п'яти секунд: без бонусу
        h.Clock.AdvanceMs(Melody.SpeedMs);
        Assert.DoesNotContain("⚡", Guess(h, 1, "Океан Ельзи").Message);
        Assert.Equal(Melody.ArtistPoints, Score(h, 1));
        var fast = h.View(0).GetProperty("found").EnumerateArray().First(f => f.GetProperty("seat").GetInt32() == 0);
        Assert.Equal(1, fast.GetProperty("fast").GetInt32());
    }

    // ---------------------------------------------------------------- «з першої ноти»

    [Fact]
    public void From_the_first_note_grows_the_clip_and_rewards_the_shorter_one()
    {
        var src = new FakeMelodySource(Songs);
        var h = Table(src, new { rounds = "5", grow = "1" });
        Until(h, "play");
        Assert.Equal([3, 6, 10], src.Lengths.Take(3).ToArray());
        var v = h.View(0);
        Assert.Equal(0, v.GetProperty("grow").GetProperty("stage").GetInt32());
        Assert.Equal(27_000 + Melody.ExtraMs, v.GetProperty("totalMs").GetInt32());
        var first = Clip(h);

        h.Clock.AdvanceMs(7_000);
        h.Tick();
        Assert.Equal(1, h.View(0).GetProperty("grow").GetProperty("stage").GetInt32());
        Assert.NotEqual(first, Clip(h));
        var r = Guess(h, 1, "Обійми");
        Assert.Contains("⚡ з 6 секунд", r.Message);
        Assert.Equal(Melody.TitlePoints + Melody.TitleFirst + 10, Score(h, 1));

        h.Clock.AdvanceMs(10_000);
        h.Tick();
        Assert.Equal(2, h.View(0).GetProperty("grow").GetProperty("stage").GetInt32());
        Assert.DoesNotContain("⚡", Guess(h, 0, "Обійми").Message);
        Assert.Equal(Melody.TitlePoints, Score(h, 0));
    }

    // ---------------------------------------------------------------- варіанти

    [Fact]
    public void Choices_show_up_in_the_last_five_seconds_and_a_wrong_one_costs_the_track()
    {
        var h = Table(new FakeMelodySource(Songs), new { rounds = "5", choices = "1", clip = "15" }, "Оля", "Петро", "Ганна");
        Until(h, "play");
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("choices").ValueKind);
        Assert.False(Do(h, 0, "pick", new { i = 0 }).Ok);                   // ще рано
        h.Clock.AdvanceMs(15_000 - Melody.ChoicesLeadMs - 2_000);
        h.Tick();
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("choices").ValueKind);
        h.Clock.AdvanceMs(2_000);
        h.Tick();
        var choices = h.View(0).GetProperty("choices").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(Melody.ChoicesCount, choices.Count);
        Assert.Contains("Океан Ельзи", choices);
        Assert.Equal(choices.Count, choices.Distinct().Count());

        var right = choices.IndexOf("Океан Ельзи");
        Assert.True(Do(h, 0, "pick", new { i = right }).Ok);
        Assert.Equal((Melody.ArtistPoints + Melody.ArtistFirst) / 2, Score(h, 0));

        var wrong = right == 0 ? 1 : 0;
        Assert.True(Do(h, 1, "pick", new { i = wrong }).Ok);                 // «Ні, не …»
        Assert.Equal(0, Score(h, 1));
        Assert.True(h.View(1).GetProperty("me").GetProperty("blocked").GetBoolean());
        Assert.False(Guess(h, 1, "Обійми").Ok);                            // трек уже без нього

        // написав руками вже при відкритих варіантах — теж удвічі менше
        Assert.True(Guess(h, 2, "Океан Ельзи").Ok);
        Assert.Equal(Melody.ArtistPoints / 2, Score(h, 2));
    }

    [Fact]
    public void Choices_are_four_different_artists_with_the_right_one()
    {
        var all = Songs.Select(s => s.Artist).Append("Океан Ельзи feat. Хтось").ToList();
        for (var seed = 0; seed < 20; seed++)
        {
            var c = Melody.Choices(Songs[0], all, new Random(seed));
            Assert.Equal(4, c.Length);
            Assert.Single(c, x => x.StartsWith("Океан Ельзи", StringComparison.Ordinal));
        }
    }

    // ---------------------------------------------------------------- «Хто закинув?»

    static FakeMelodySource Asked(int n, params string[] by) => new(Songs)
    {
        Requested = [.. Songs.Take(n).Select((s, i) => new MelodyRequested(s, [by[i % by.Length]]))],
    };

    [Fact]
    public void Who_asked_lets_you_name_the_requester_once_after_a_hit()
    {
        var h = Table(Asked(5, "Оля", "Петро"), new { rounds = "5", cat = "who" });
        Until(h, "play");
        var v = h.View(1);
        Assert.NotEqual(JsonValueKind.Null, v.GetProperty("who").ValueKind);
        Assert.Equal(JsonValueKind.Null, v.GetProperty("who").GetProperty("by").ValueKind);   // хто — лише після раунду
        Assert.False(Do(h, 1, "who", new { nick = "Оля" }).Ok);            // спершу виконавця чи назву

        // пісні «Хто закинув?» перемішані — шукаємо, котра звучить (мимо нічого не коштує)
        h.Clock.AdvanceMs(Melody.SpeedMs);
        var k = Array.FindIndex(Songs, t => Guess(h, 1, t.Artist + " " + t.Title).Ok);
        Assert.True(k >= 0);
        string asker = k % 2 == 0 ? "Оля" : "Петро", other = k % 2 == 0 ? "Петро" : "Оля";
        Assert.Equal("play", Phase(h));                                   // Петро вгадав усе, але ще не назвав замовника
        var r = Do(h, 1, "who", new { nick = asker.ToLowerInvariant() });
        Assert.True(r.Ok);
        Assert.Contains($"+{Melody.WhoPoints}", r.Message);
        Assert.Equal(Melody.ArtistPoints + Melody.ArtistFirst + Melody.TitlePoints + Melody.TitleFirst + Melody.WhoPoints, Score(h, 1));
        Assert.False(Do(h, 1, "who", new { nick = other }).Ok);           // одна спроба

        Assert.True(Guess(h, 0, Songs[k].Title).Ok);
        Assert.True(Do(h, 0, "who", new { nick = other }).Ok);            // не вгадала — без очок
        Assert.Equal(Melody.TitlePoints, Score(h, 0));
        h.Clock.AdvanceMs(Melody.GuessEveryMs);
        var sk = h.Act(0, "skip");
        Assert.True(sk.Ok, sk.Message);
        h.Tick();
        Assert.Equal("reveal", Phase(h));
        Assert.Equal(asker, h.View(0).GetProperty("who").GetProperty("by")[0].GetString());
    }

    [Fact]
    public void Who_asked_alone_with_too_few_requests_explains_and_closes()
    {
        var h = Table(Asked(2, "Оля"), new { rounds = "5", cat = "who" });
        for (var i = 0; i < 50 && h.Room.Status == RoomStatus.Playing; i++) h.Tick();
        Assert.NotEqual(RoomStatus.Playing, h.Room.Status);
        Assert.Contains("лише 2 пісні", h.View(null).GetProperty("error").GetString());
    }

    [Fact]
    public void Who_asked_with_other_categories_and_too_few_requests_plays_on_with_a_note()
    {
        var h = Table(Asked(1, "Оля"), new { rounds = "5", cat = "who,ua" });
        Until(h, "play");
        Assert.Contains("граємо без неї", h.View(0).GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("who").ValueKind);
    }

    [Fact]
    public void Who_asked_songs_are_mixed_in_as_their_share()
    {
        List<MelodyTrack> w = [Songs[0], Songs[1]], r = [Songs[2], Songs[3], Songs[4]];
        Assert.Equal(["a", "c", "b", "d", "e"], Melody.Mix(w, r, 2).Select(t => t.Id));
        Assert.Equal(["a", "b"], Melody.Mix(w, [], 3).Select(t => t.Id));
    }

    [Fact]
    public void All_does_not_switch_on_who_asked_but_it_can_be_picked()
    {
        var all = MelodyCategories.Parse("all", MelodyClassics.Default);
        Assert.DoesNotContain(MelodyCategories.Who, all);
        Assert.Contains(MelodyCategories.Fav, all);
        Assert.Equal([MelodyCategories.Who], MelodyCategories.Parse("who", MelodyClassics.Default));
    }

    // ---------------------------------------------------------------- команди

    static readonly string[] Six = ["Оля", "Петро", "Ганна", "Іван", "Марта", "Богдан"];

    [Fact]
    public void Teams_score_each_find_once_and_the_better_team_wins()
    {
        var h = Table(new FakeMelodySource(Songs), new { rounds = "5", teams = "1" }, Six);
        Until(h, "play");
        var teams = h.View(0).GetProperty("teams");
        Assert.Equal([0, 1, 0, 1, 0, 1], teams.GetProperty("of").EnumerateArray().Take(6).Select(x => x.GetInt32()));
        h.Clock.AdvanceMs(Melody.SpeedMs);
        Assert.True(Guess(h, 0, "Океан Ельзи").Ok);
        var again = Guess(h, 2, "Океан Ельзи");                          // та сама команда — нуль, але не «мимо»
        Assert.True(again.Ok);
        Assert.Contains("команда вже має", again.Message);
        Assert.Equal(0, Score(h, 2));
        Assert.True(Guess(h, 1, "Океан Ельзи").Ok);                       // інша команда — свої очки, без першості
        Assert.True(Guess(h, 4, "Обійми").Ok);
        var sc = h.View(0).GetProperty("teams").GetProperty("scores");
        Assert.Equal(Melody.ArtistPoints + Melody.ArtistFirst + Melody.TitlePoints + Melody.TitleFirst, sc[0].GetInt32());
        Assert.Equal(Melody.ArtistPoints, sc[1].GetInt32());
        // команда 0 має все — її гравцям пропускати нема чого; лишилась команда 1
        Assert.False(h.Act(2, "skip").Ok);
        for (var s = 1; s < 6; s += 2) Assert.True(h.Act(s, "skip").Ok);
        h.Tick();
        Assert.Equal("reveal", Phase(h));

        for (var i = 0; i < 4; i++) { NextRound(h); EndRound(h); }
        h.Clock.AdvanceMs(Melody.RevealMs + 100);
        h.Tick();
        var res = h.View(0).GetProperty("result");
        Assert.Equal(0, res.GetProperty("team").GetInt32());
        Assert.Equal([0, 2, 4], res.GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Fact]
    public void Teams_need_six_players()
    {
        var h = Table(new FakeMelodySource(Songs), new { rounds = "5", teams = "1" }, "Оля", "Петро", "Ганна", "Іван");
        Until(h, "play");
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("teams").ValueKind);
        Assert.Contains("Команд не буде", h.View(0).GetProperty("note").GetString());
    }

    // ---------------------------------------------------------------- фінальна дуель

    [Fact]
    public void Final_duel_is_for_the_two_leaders_and_the_rest_bet_who_takes_it()
    {
        var h = Table(new FakeMelodySource(Songs), new { rounds = "5", duel = "1" }, "Оля", "Петро", "Ганна");
        Until(h, "play");
        for (var round = 1; round <= 4; round++)
        {
            h.Clock.AdvanceMs(Melody.SpeedMs);
            var t = Songs[round - 1];
            Assert.True(Guess(h, 0, t.Title).Ok);
            Assert.True(Guess(h, 1, t.Artist).Ok);
            EndRound(h);
            if (round < 4)
            {
                Assert.Equal(JsonValueKind.Null, h.View(2).GetProperty("duel").ValueKind);
                NextRound(h);
            }
        }
        var duel = h.View(2).GetProperty("duel");
        Assert.Equal(0, duel.GetProperty("a").GetInt32());
        Assert.Equal(1, duel.GetProperty("b").GetInt32());
        Assert.Equal(5, duel.GetProperty("round").GetInt32());
        Assert.False(Do(h, 0, "bet", new { seat = 1 }).Ok);               // дуелянт не ставить
        Assert.False(Do(h, 2, "bet", new { seat = 2 }).Ok);               // лише на дуелянтів
        Assert.True(Do(h, 2, "bet", new { seat = 1 }).Ok);
        Assert.Equal(1, h.View(2).GetProperty("duel").GetProperty("mine").GetInt32());

        NextRound(h);
        Assert.False(Guess(h, 2, "Journey").Ok);                         // у дуелі вгадують лише двоє
        var before = Score(h, 2);
        Assert.True(Guess(h, 1, "Journey").Ok);
        Assert.True(h.View(2).GetProperty("duel").GetProperty("closed").GetBoolean());
        Assert.False(Do(h, 2, "bet", new { seat = 0 }).Ok);               // після влучання ставки зачинено
        EndRound(h);
        var after = h.View(2).GetProperty("duel");
        Assert.Equal(1, after.GetProperty("winner").GetInt32());
        Assert.Equal(before + Melody.BetPoints, Score(h, 2));
    }

    // ---------------------------------------------------------------- фініш проходу №3: хвости рецензії

    static int[] Done(RoomHarness h) => [.. h.View(null).GetProperty("done").EnumerateArray().Select(x => x.GetInt32())];

    [Fact]
    public void Done_is_counted_by_the_server_for_who_asked_and_for_teams()
    {
        // «Хто закинув?»: вгадав виконавця й назву, але замовника ще не назвав — не «готово», пропустити можна
        var h = Table(Asked(5, "Оля", "Петро"), new { rounds = "5", cat = "who" });
        Until(h, "play");
        h.Clock.AdvanceMs(Melody.SpeedMs);
        var k = Array.FindIndex(Songs, t => Guess(h, 1, t.Artist + " " + t.Title).Ok);
        Assert.True(k >= 0);
        Assert.DoesNotContain(1, Done(h));
        Assert.True(Do(h, 1, "skip", new { }).Ok);
        Assert.True(Do(h, 1, "who", new { nick = "Оля" }).Ok);
        Assert.Contains(1, Done(h));

        // команди: команда 0 має все — «готово» всім її гравцям, хоч особисто вони нічого не вгадали
        var t = Table(new FakeMelodySource(Songs), new { rounds = "5", teams = "1" }, Six);
        Until(t, "play");
        Assert.True(Guess(t, 0, "Океан Ельзи Обійми").Ok);
        Assert.Equal([0, 2, 4], Done(t));
    }

    [Fact]
    public void No_duel_when_the_leaders_have_nothing()
    {
        var h = Table(new FakeMelodySource(Songs), new { rounds = "5", duel = "1" }, "Оля", "Петро", "Ганна");
        Until(h, "play");
        h.Clock.AdvanceMs(Melody.SpeedMs);
        Assert.True(Guess(h, 2, "Обійми").Ok);                            // очки лише в Ганни — другого лідера нема
        EndRound(h);
        for (var i = 0; i < 3; i++) { NextRound(h); EndRound(h); }
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("duel").ValueKind);
        NextRound(h);
        Assert.True(Guess(h, 0, "Journey").Ok);                           // останній трек — для всіх
    }

    [Fact]
    public void A_duel_whose_track_never_came_is_taken_down()
    {
        // останній трек ще качається (порожній id — не з кешу), коли після четвертого оголошують дуель
        MelodyTrack[] songs = [.. Songs.Take(4), new("", "Journey", "The Hardkiss", 0, null, "")];
        var src = new FakeMelodySource(songs, slow: new HashSet<string> { "Journey" });
        var h = Table(src, new { rounds = "5", duel = "1" }, "Оля", "Петро", "Ганна");
        Until(h, "play");
        for (var round = 1; round <= 4; round++)
        {
            h.Clock.AdvanceMs(Melody.SpeedMs);
            Assert.True(Guess(h, 0, Songs[round - 1].Title).Ok);
            Assert.True(Guess(h, 1, Songs[round - 1].Artist).Ok);
            EndRound(h);
            if (round < 4) NextRound(h);
        }
        Assert.NotEqual(JsonValueKind.Null, h.View(2).GetProperty("duel").ValueKind);   // оголосили
        src.Release("Journey", ok: false);                                              // а трек не скачався
        h.Clock.AdvanceMs(Melody.RevealMs + 100);
        Until(h, "done");
        Assert.True(JsonValueKind.Null == h.View(2).GetProperty("duel").ValueKind, h.View(2).GetRawText());
    }

    [Fact]
    public void No_promise_of_choices_when_there_is_nothing_to_choose_from()
    {
        MelodyTrack[] one = [.. Songs.Select(t => t with { Artist = "Океан Ельзи" })];
        var h = Table(new FakeMelodySource(one), new { rounds = "5", choices = "1", clip = "15" });
        Until(h, "play");
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("choicesAt").ValueKind);
        var two = Table(new FakeMelodySource(Songs), new { rounds = "5", choices = "1", clip = "15" });
        Until(two, "play");
        Assert.NotEqual(JsonValueKind.Null, two.View(0).GetProperty("choicesAt").ValueKind);
    }

    [Fact]
    public void No_duel_for_two()
    {
        var h = Table(new FakeMelodySource(Songs), new { rounds = "5", duel = "1" });
        Until(h, "play");
        for (var i = 0; i < 4; i++) { EndRound(h); NextRound(h); }
        Assert.Equal(JsonValueKind.Null, h.View(0).GetProperty("duel").ValueKind);
        Assert.True(Guess(h, 1, "Journey").Ok);
    }

    // ---------------------------------------------------------------- рецензія: вихід посеред дуелі й команд

    [Fact]
    public void Duelist_leaving_mid_duel_does_not_hang_the_track_and_bets_settle()
    {
        var h = Table(new FakeMelodySource(Songs), new { rounds = "5", duel = "1" }, "Оля", "Петро", "Ганна", "Іван");
        Until(h, "play");
        for (var round = 1; round <= 4; round++)
        {
            h.Clock.AdvanceMs(Melody.SpeedMs);
            var t = Songs[round - 1];
            Assert.True(Guess(h, 0, t.Title).Ok);
            Assert.True(Guess(h, 1, t.Artist).Ok);
            EndRound(h);
            if (round < 4) NextRound(h);
        }
        Assert.True(Do(h, 2, "bet", new { seat = 1 }).Ok);
        Assert.True(Do(h, 3, "bet", new { seat = 0 }).Ok);
        NextRound(h);
        h.Leave("Оля");                                                   // лідер пішов посеред дуелі
        Assert.True(Guess(h, 1, "The Hardkiss Journey").Ok);
        h.Tick();                                                         // решта лише ставить — чекати нема кого
        Assert.Equal("reveal", Phase(h));
        Assert.Equal(1, h.View(2).GetProperty("duel").GetProperty("winner").GetInt32());
        var before = Score(h, 2);
        h.Clock.AdvanceMs(Melody.RevealMs + 100);
        h.Tick();
        Assert.Equal("done", Phase(h));
        Assert.Equal(before, Score(h, 2));                                // ставку зараховано ще на розкритті
        Assert.True(Score(h, 2) >= Melody.BetPoints);
        Assert.Equal(0, Score(h, 3));                                     // ставив на того, хто пішов
    }

    [Fact]
    public void Team_player_leaving_mid_track_does_not_hang_and_the_team_still_wins()
    {
        var h = Table(new FakeMelodySource(Songs), new { rounds = "5", teams = "1" }, Six);
        Until(h, "play");
        h.Clock.AdvanceMs(Melody.SpeedMs);
        Assert.True(Guess(h, 0, "Океан Ельзи Обійми").Ok);
        h.Leave("Оля");
        for (var s = 1; s < 6; s += 2) Assert.True(h.Act(s, "skip").Ok);
        h.Tick();
        Assert.Equal("reveal", Phase(h));                                 // команда 0 має все, команда 1 пропустила
        for (var i = 0; i < 4; i++) { NextRound(h); EndRound(h); }
        h.Clock.AdvanceMs(Melody.RevealMs + 100);
        h.Tick();
        var res = h.View(1).GetProperty("result");
        Assert.Equal(0, res.GetProperty("team").GetInt32());
        Assert.Equal([2, 4], res.GetProperty("winners").EnumerateArray().Select(x => x.GetInt32()));
    }
}
