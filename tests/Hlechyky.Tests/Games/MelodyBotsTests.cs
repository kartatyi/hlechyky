using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Games.Impl;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Games;

/// <summary>
/// «Вгадай мелодію», пакет 2 (20.10): боти-вгадувачі. Сервер знає відповідь, бот «впізнає» з імовірністю за рівнем
/// (<see cref="Melody.BotKnows"/>) і пише крізь ті самі правила, що й люди. Без звуку: уривки — підробка.
/// </summary>
public class MelodyBotsTests
{
    static readonly string[] Artists = ["Бджоли", "Ліхтарі", "Барабани", "Вишні", "Козаки", "Хмари", "Вовки", "Гончарі",
        "Чайки", "Дзвони", "Каштани", "Мольфари", "Рибалки", "Ковалі", "Шпаки"];
    static readonly string[] Titles = ["Ягода", "Комета", "Буревій", "Соловей", "Тополя", "Кавун", "Маяк", "Вітрило",
        "Ожина", "Пісок", "Грім", "Лелека", "Зоря", "Криниця", "Сопілка"];

    static MelodyTrack[] Songs() =>
        [.. Enumerable.Range(0, Artists.Length).Select(i => new MelodyTrack($"t{i}", Titles[i], Artists[i], 200, null, "/dev/null"))];

    static RoomHarness Table(int humans, object? options = null, int seed = 3, bool bots = true)
    {
        var h = new RoomHarness("melody", options: options, seed: seed, services: RoomHarness.WithService<IMelodySource>(new FakeMelodySource(Songs())));
        string[] nicks = ["Оля", "Петро", "Ганна", "Іван"];
        for (var i = 0; i < humans; i++) h.Join(nicks[i]);
        if (bots) Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        return h;
    }

    static string Phase(RoomHarness h) => h.View(null).GetProperty("phase").GetString()!;

    static int[] Bots(RoomHarness h) => [.. h.View(0).GetProperty("bot").EnumerateArray().Select(x => x.GetInt32())];

    /// <summary>
    /// Догравати партію: люди мовчать і одразу «готові пропустити» (раунд тоді чекає лише ботів). Повертає, скільки разів
    /// за партію кожне місце вгадало виконавця.
    /// </summary>
    static int[] PlayOut(RoomHarness h, int[]? blocked = null)
    {
        var artist = new int[Melody.Seats];
        var lastRound = 0;
        for (var i = 0; i < 20_000 && h.Room.Status == RoomStatus.Playing; i++)
        {
            h.Tick();
            var v = h.View(null);
            var phase = v.GetProperty("phase").GetString();
            if (phase == "loading") { Thread.Sleep(1); continue; }
            var round = v.GetProperty("round").GetInt32();
            if (phase == "play" && round != lastRound)
            {
                lastRound = round;
                for (var s = 0; s < Melody.Seats; s++)
                    if (h.Room.Seats[s] is not null) h.Act(s, "skip");
            }
            if (phase == "reveal" && round == lastRound)
            {
                foreach (var f in v.GetProperty("found").EnumerateArray())
                {
                    if (f.GetProperty("artist").GetBoolean()) artist[f.GetProperty("seat").GetInt32()]++;
                    if (blocked is not null && f.TryGetProperty("blocked", out var b) && b.GetBoolean()) blocked[f.GetProperty("seat").GetInt32()]++;
                }
                lastRound = -round;   // цей раунд уже пораховано
            }
        }
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        return artist;
    }

    [Fact]
    public void Alone_with_bots_rounds_go_bots_score_and_there_are_no_rewards()
    {
        var h = Table(1, new { rounds = "5" });
        Assert.Equal([1, 2, 3], Bots(h));
        Assert.Equal(4, h.View(0).GetProperty("botFill").GetInt32());
        h.Start();
        Assert.Equal("🤖 бот 1", h.Room.Summary().Seats[1].Bot);
        PlayOut(h);
        var scores = h.View(null).GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        Assert.True(scores[1] + scores[2] + scores[3] > 0);   // за п'ять треків троє звичайних ботів щось та вгадали
        Assert.Equal(0, scores[0]);
        Assert.Contains("без нагород", h.Room.Result!.Verdict);
        Assert.Empty(h.Finished[0].Result.Winners);           // переможець — бот, людині — без нагород
        Assert.Empty(h.Scores);
        var nicks = h.View(null).GetProperty("result").GetProperty("nicks");
        Assert.StartsWith("🤖 бот", nicks[1].GetString());
    }

    [Fact]
    public void Two_humans_get_two_bots_and_a_bot_winner_beats_them_for_real()
    {
        var h = Table(2, new { rounds = "5", botlvl = "hard" });
        Assert.Equal([2, 3], Bots(h));
        h.Start();
        var hits = PlayOut(h);
        Assert.True(hits[2] + hits[3] > 0);
        Assert.Null(h.Room.Result!.Verdict);
        var fin = h.Finished[0].Result;
        Assert.Equal([0, 1], fin.Scores!.Keys.Order());       // очки в таблицю — лише людям
        Assert.All(fin.Winners, w => Assert.True(w >= 2));      // люди мовчали — перемога в ботів
    }

    [Fact]
    public void Hard_bots_guess_more_often_than_easy_ones()
    {
        int Hits(string lvl)
        {
            var sum = 0;
            for (var seed = 1; seed <= 3; seed++)
            {
                var h = Table(1, new { rounds = "15", clip = "10", botlvl = lvl }, seed: seed);
                h.Start();
                sum += PlayOut(h).Skip(1).Take(3).Sum();
            }
            return sum;
        }
        var easy = Hits("easy");
        var hard = Hits("hard");
        // 3 партії × 15 треків × 3 боти = 135 спроб: легкий ≈ (0,35 + 0,12)·135 ≈ 63, сильний ≈ 117
        Assert.True(hard > easy + 20, $"легкі {easy}, сильні {hard}");
        Assert.InRange(easy, 35, 95);
        Assert.InRange(hard, 90, 135);
    }

    [Fact]
    public void Bots_pick_choices_and_play_from_the_first_note()
    {
        var h = Table(1, new { rounds = "5", grow = "1", choices = "1", botlvl = "easy" });
        h.Start();
        var blocked = new int[Melody.Seats];
        var hits = PlayOut(h, blocked);
        // легкі боти з першої ноти впізнають рідко, тож частіше тицяють варіант навмання — і влучають, і профукують
        Assert.True(hits.Skip(1).Take(3).Sum() + blocked.Skip(1).Take(3).Sum() > 0);
    }

    [Fact]
    public void Bot_button_rules_lobby_and_mid_game()
    {
        var h = Table(1, new { rounds = "5" }, bots: false);
        Assert.True(h.View(0).GetProperty("botOffer").GetBoolean());
        Assert.Empty(Bots(h));
        Assert.False(h.Act(0, "skip").Ok);                     // до «Почати» ходів нема
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = true }).Ok);
        h.Start();
        Assert.False(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);
        PlayOut(h);
        Assert.True(h.Act(0, LiveBots.Toggle, new { on = false }).Ok);   // між партіями — можна
    }

    [Fact]
    public void Lone_human_without_bots_plays_as_before()
    {
        var h = Table(1, new { rounds = "5" }, bots: false);
        h.Start();
        PlayOut(h);
        Assert.Null(h.Room.Result!.Verdict);
    }
}
