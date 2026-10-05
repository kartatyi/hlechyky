using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Стіл на двох, що стартує, коли всі сіли, з опціями (звичайна й multi) і з налаштуванням у лобі («пакет») та
/// «🤖 + бот» між партіями — щоб перевірити «⚙ Налаштування» (<see cref="Rooms.Reconfigure"/>).
/// </summary>
public sealed class TestTuned : Game
{
    static int _made;

    public override GameInfo Info { get; } = new(
        "t-tuned", "Тестове налаштування", "тестове налаштування", GameGroup.Board, 2, 2, Rated: true,
        Options:
        [
            new GameOption("mode", "Режим", [("a", "перший"), ("b", "другий"), ("bad", "ламаний")], "a"),
            new GameOption("topics", "Теми", [("all", "усі"), ("x", "ікс"), ("y", "ігрек")], "all", Multi: true),
        ]);

    /// <summary>Котрий це екземпляр за процес: нова гра столу — нове число.</summary>
    public int Made { get; } = Interlocked.Increment(ref _made);
    public string Mode { get; private set; } = "";
    public string Topics { get; private set; } = "";
    public int Configures { get; private set; }
    public int Starts { get; private set; }
    public string? Pack { get; private set; }
    public bool Bot { get; private set; }
    bool _over;

    public override void Configure(IReadOnlyDictionary<string, string> options)
    {
        if (options["mode"] == "bad") throw new GameError("Так не можна");
        Mode = options["mode"];
        Topics = options["topics"];
        Configures++;
    }

    public override bool ActsInLobby => true;

    public override void Start()
    {
        Starts++;
        _over = false;
    }

    public override ActResult Act(int seat, string action, JsonElement payload)
    {
        switch (action)
        {
            case "bot":
                Bot = !Bot;
                return ActResult.Done;
            case "pack":
                if (Starts > 0) return ActResult.Fail("Пакет — лише в лобі");
                Pack = payload.GetProperty("id").GetString();
                return ActResult.Done;
            case "win":
                if (_over) return ActResult.Fail("Уже все");
                _over = true;
                Ctx.Finish([seat], $"налаштування: виграв {Ctx.NickOf(seat)}");
                return ActResult.Done;
            default:
                return ActResult.Fail("Тут так не ходять");
        }
    }

    public override object View(int? seat) => new { turn = 0, mode = Mode, pack = Pack, bot = Bot };
}

/// <summary>«⚙ Налаштування» між партіями: господар міняє опції столу без «встати й поставити новий».</summary>
public class RoomSettingsTests
{
    static Dictionary<string, string> Set(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    static TestTuned Game(RoomHarness h) => (TestTuned)h.Room.Game;

    static RoomOutcome Configure(RoomHarness h, string nick, params (string Key, string Value)[] pairs)
    {
        var outcome = h.Rooms.Reconfigure(h.RoomId, nick, Set(pairs));
        h.Outbox.AddRange(outcome.Out);
        return outcome;
    }

    /// <summary>Двоє зіграли партію: Оля виграла, стіл дограно.</summary>
    static RoomHarness Played()
    {
        var h = new RoomHarness("t-tuned", new { mode = "a" });
        h.Join("Оля");
        h.Join("Петро");
        Assert.Equal(RoomStatus.Playing, h.Room.Status);
        Assert.True(h.Act(0, "win").Ok);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        return h;
    }

    [Fact]
    public void Host_changes_options_in_a_clean_lobby_on_the_same_game()
    {
        var h = new RoomHarness("t-tuned");
        h.Join("Оля");
        Assert.True(h.Act(0, "pack", new { id = "весна" }).Ok);
        var game = Game(h);
        h.Outbox.Clear();

        var outcome = Configure(h, "Оля", ("mode", "b"), ("topics", "y,x"));

        Assert.True(outcome.Reply.Ok);
        Assert.Equal("b", h.Room.Options["mode"]);
        Assert.Equal("x,y", h.Room.Options["topics"]);   // у порядку паспорта, як при створенні
        Assert.Same(game, h.Room.Game);                  // та сама гра: обраний у лобі пакет нікуди не дівся
        Assert.Equal(("b", "x,y", 2, "весна"), (game.Mode, game.Topics, game.Configures, game.Pack));
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Contains(h.Outbox, m => m is LobbyChanged);
        Assert.Contains(h.Outbox, m => m is RoomViews);
        var said = Assert.Single(h.Outbox.OfType<TableSaid>());
        Assert.Equal("⚙ Оля міняє налаштування: Режим — другий; Теми — ікс · ігрек", said.Line.Text);
        Assert.Equal("dj", said.Line.Kind);
    }

    [Fact]
    public void Options_not_passed_stay_unknown_values_fall_back_and_the_stake_is_not_touched()
    {
        var h = new RoomHarness("t-tuned", new { mode = "b", topics = "x", stake = 5 });
        h.Stakes.Set("Оля", 50);
        h.Join("Оля");
        Assert.Equal(5, h.Room.Stake);

        Assert.True(Configure(h, "Оля", ("topics", "y"), ("stake", "25")).Reply.Ok);
        Assert.Equal(("b", "y"), (h.Room.Options["mode"], h.Room.Options["topics"]));
        Assert.Equal(5, h.Room.Stake);
        Assert.False(h.Room.Options.ContainsKey("stake"));

        Assert.True(Configure(h, "Оля", ("mode", "космос"), ("topics", "y,all")).Reply.Ok);
        Assert.Equal(("a", "all"), (h.Room.Options["mode"], h.Room.Options["topics"]));
    }

    [Fact]
    public void Nothing_changed_in_a_clean_lobby_is_a_quiet_no_op()
    {
        var h = new RoomHarness("t-tuned");
        h.Join("Оля");
        var game = Game(h);
        h.Outbox.Clear();

        var outcome = Configure(h, "Оля", ("mode", "a"));

        Assert.True(outcome.Reply.Ok);
        Assert.Equal("Усе й так стоїть саме так", outcome.Reply.Message);
        Assert.Equal(1, game.Configures);
        Assert.Empty(h.Outbox);
    }

    [Fact]
    public void Only_the_seated_host_between_games_can_change_options()
    {
        var h = new RoomHarness("t-tuned");
        h.Join("Оля");
        h.Join("Петро");   // повний стіл — партія пішла

        Assert.Equal("Посеред партії налаштування не міняють — дограйте", Configure(h, "Оля", ("mode", "b")).Reply.Message);
        h.Act(0, "win");
        Assert.Equal("Налаштування міняє господар столу", Configure(h, "Петро", ("mode", "b")).Reply.Message);
        Assert.Equal("Ти тут не граєш", Configure(h, "Чужий", ("mode", "b")).Reply.Message);
        Assert.Equal("Такого столу вже нема", h.Rooms.Reconfigure("нема", "Оля", Set(("mode", "b"))).Reply.Message);
        Assert.Equal("Спершу скажи, як тебе кликати", h.Rooms.Reconfigure(h.RoomId, "", null).Reply.Message);
        Assert.Equal("a", h.Room.Options["mode"]);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
    }

    [Fact]
    public void Games_without_options_and_solo_have_nothing_to_set()
    {
        var party = new RoomHarness("t-party");
        party.Join("Оля");
        Assert.Equal("У цієї гри налаштувань нема", party.Rooms.Reconfigure(party.RoomId, "Оля", null).Reply.Message);

        var solo = new RoomHarness("t-solo");
        solo.Solo("Оля");
        Assert.Equal("Такого столу вже нема", solo.Rooms.Reconfigure(solo.RoomId, "Оля", null).Reply.Message);
    }

    [Fact]
    public void Finished_table_goes_back_to_the_lobby_with_a_fresh_game_and_keeps_seats_and_the_evening()
    {
        var h = Played();
        var old = Game(h);
        var seed = h.Room.Seed;
        h.Outbox.Clear();

        var outcome = Configure(h, "Оля", ("mode", "b"));

        Assert.True(outcome.Reply.Ok);
        Assert.Equal("Стіл знову в лобі — тисни «Почати», коли всі готові", outcome.Reply.Message);
        var room = h.Room;
        Assert.Equal(RoomStatus.Lobby, room.Status);
        Assert.Null(room.Result);
        Assert.Null(room.StartedAt);
        Assert.Null(room.FinishedAt);
        Assert.Equal(2, room.Round);                    // ключі ставок наступної партії — нові
        Assert.Equal(new string?[] { "Петро", "Оля" }, room.Seats);   // як в «Ану ще раз»: тепер починає інший
        Assert.Equal("Оля", room.Host);
        Assert.Equal(1, room.EveningGames);             // рахунок вечора лишився
        var fresh = Game(h);
        Assert.NotSame(old, fresh);
        Assert.Equal(("b", 1, 0, null), (fresh.Mode, fresh.Configures, fresh.Starts, fresh.Pack));
        Assert.Equal(room.Id, fresh.Ctx.RoomId);
        Assert.Equal(seed, room.Seed);                  // у тестах зерно задане (SeedOverride) — і нова гра бере його ж
        Assert.Contains(h.Outbox, m => m is LobbyChanged);
        Assert.Equal("⚙ Оля ставить стіл наново: Режим — другий", Assert.Single(h.Outbox.OfType<TableSaid>()).Line.Text);

        // У лобі нова гра приймає налаштування лобі, як за щойно поставленим столом, а починає господар:
        // повний стіл гри «коли всі сіли» сам уже не стартує.
        Assert.True(h.Act(0, "pack", new { id = "літо" }).Ok);
        Assert.True(h.Start().Ok);
        Assert.Equal(RoomStatus.Playing, room.Status);
        Assert.Equal((1, "літо"), (fresh.Starts, fresh.Pack));
        Assert.True(h.Act(1, "win").Ok);
        Assert.Equal(2, room.EveningGames);
        Assert.Equal(2, h.Finished.Count);
        Assert.Equal(2, h.Finished[^1].Round);
    }

    [Fact]
    public void Back_in_the_lobby_the_next_change_is_on_the_same_fresh_game()
    {
        var h = Played();
        Configure(h, "Оля");                            // без змін — просто в лобі, налаштуватись
        Assert.Equal(RoomStatus.Lobby, h.Room.Status);
        Assert.Equal("⚙ Оля ставить стіл наново — налаштовуємось і починаємо", h.Outbox.OfType<TableSaid>().Last().Line.Text);
        var fresh = Game(h);

        Assert.True(Configure(h, "Оля", ("mode", "b")).Reply.Ok);
        Assert.Same(fresh, Game(h));
        Assert.Equal(2, h.Room.Round);
        Assert.Equal(("b", 2), (fresh.Mode, fresh.Configures));
    }

    [Fact]
    public void A_game_that_refuses_the_options_leaves_the_table_as_it_was()
    {
        var lobby = new RoomHarness("t-tuned", new { mode = "b" });
        lobby.Join("Оля");
        var game = Game(lobby);
        Assert.Equal("Так не можна", Configure(lobby, "Оля", ("mode", "bad")).Reply.Message);
        Assert.Equal("b", lobby.Room.Options["mode"]);
        Assert.Equal("b", game.Mode);                   // гра повернулась до старих опцій

        var h = Played();
        var old = Game(h);
        Assert.Equal("Так не можна", Configure(h, "Оля", ("mode", "bad")).Reply.Message);
        Assert.Same(old, Game(h));
        Assert.Equal(RoomStatus.Finished, h.Room.Status);
        Assert.Equal(1, h.Room.Round);
        Assert.NotNull(h.Room.Result);
    }

    [Fact]
    public void Bot_toggle_works_between_games_but_other_moves_wait_for_the_rematch()
    {
        var h = Played();
        var moves = h.Room.Moves;
        h.Outbox.Clear();

        Assert.True(h.Act(0, "bot").Ok);
        Assert.True(Game(h).Bot);
        Assert.Contains(h.Outbox, m => m is RoomViews);   // у дограного столу тика нема — вид шле каркас
        Assert.Equal(moves, h.Room.Moves);                // налаштування — не хід партії
        Assert.Equal("Партію зіграно, тисни «Ану ще раз»", h.Rooms.Act(h.RoomId, "Оля", "pack", Views.Payload(new { id = "x" })).Reply.Message);
        Assert.Equal(RoomStatus.Finished, h.Room.Status);

        // гра без налаштувань у лобі за дограним столом нічого не приймає — і «bot» теж
        var party = new RoomHarness("t-party");
        party.Join("Оля");
        party.Join("Петро");
        party.Start();
        party.Act(0, "win");
        Assert.Equal("Партію зіграно, тисни «Ану ще раз»", party.Rooms.Act(party.RoomId, "Оля", "bot", default).Reply.Message);
    }

    /// <summary>
    /// Справжні ігри з опціями: інші опції в чистому лобі (Configure ще раз на тій самій грі) і стіл, вернутий із
    /// дограного в лобі (нова гра), — без винятків, і вид після цього складається.
    /// </summary>
    [Fact]
    public void Every_real_game_with_options_takes_new_options_in_the_lobby_and_after_a_game()
    {
        var registry = RoomHarness.NewRegistry();
        var games = registry.Catalog
            .Where(g => !g.Id.StartsWith("t-", StringComparison.Ordinal) && g.MaxPlayers > 1 && !g.Private && g.Options.Count > 0)
            .ToList();
        var checkedGames = 0;
        foreach (var g in games)
        {
            var rooms = new Rooms(registry, new FakeClock(), new GameEvents(), new FakeStakes(), new FakeStore(), RoomHarness.Empty()) { SeedOverride = 3 };
            var created = rooms.Create("Оля", g.Id, null);
            if (!created.Reply.Ok || created.Reply.RoomId is not { } id) continue;   // без сервісів сервера не ставиться — не цей тест
            var room = rooms.Find(id)!;
            // Усі «останні» значення: найдальше від типового, multi — усе, крім «усе».
            var other = g.Options.ToDictionary(o => o.Key,
                o => o.Multi ? string.Join(",", o.Values.Select(v => v[0]).Where(v => v != o.Default)) : o.Values.Count > 0 ? o.Values[^1][0] : o.Default);

            var lobby = rooms.Reconfigure(id, "Оля", other);
            Assert.True(lobby.Reply.Ok || lobby.Reply.Message.Length > 0, g.Id);
            lock (room.Sync) Views.Json(room.Game.View(0));

            lock (room.Sync) { room.Status = RoomStatus.Finished; room.StartedAt = room.CreatedAt; }
            var back = rooms.Reconfigure(id, "Оля", g.Options.ToDictionary(o => o.Key, o => o.Default));
            Assert.True(back.Reply.Ok, $"{g.Id}: {back.Reply.Message}");
            Assert.Equal(RoomStatus.Lobby, room.Status);
            lock (room.Sync)
            {
                Views.Json(room.Game.View(0));
                Views.Json(room.Game.View(null));
                Views.Json(room.Summary());
            }
            checkedGames++;
        }
        Assert.True(checkedGames >= 20, $"перевірено лише {checkedGames} ігор із {games.Count}");
    }

    /// <summary>
    /// Configure ще раз у лобі має дати те саме, що й один Configure з цими опціями: без «if (так) _поле = …» без else,
    /// яке потім не вертається до типового (так було в Шибениці, Позивних і Зіпсованому телефоні). Звіряємо прості
    /// поля гри: стіл, переналаштований з «усе не типове» на типові, і стіл, одразу поставлений із типовими.
    /// </summary>
    [Fact]
    public void Configure_again_in_the_lobby_equals_a_table_set_up_with_those_options()
    {
        var registry = RoomHarness.NewRegistry();
        var compared = 0;
        var wrong = new List<string>();
        foreach (var g in registry.Catalog.Where(g =>
            !g.Id.StartsWith("t-", StringComparison.Ordinal) && g.MaxPlayers > 1 && !g.Private && g.Options.Count > 0))
        {
            var defaults = g.Options.ToDictionary(o => o.Key, o => o.Default);
            var other = g.Options.ToDictionary(o => o.Key,
                o => o.Multi ? string.Join(",", o.Values.Select(v => v[0]).Where(v => v != o.Default)) : o.Values.Count > 0 ? o.Values[^1][0] : o.Default);
            var plain = Table(registry, g.Id, defaults);
            var retuned = Table(registry, g.Id, other);
            if (plain is null || retuned is null) continue;
            if (!retuned.Value.Rooms.Reconfigure(retuned.Value.Id, "Оля", defaults).Reply.Ok) continue;
            var a = Fields(plain.Value.Rooms.Find(plain.Value.Id)!.Game);
            var b = Fields(retuned.Value.Rooms.Find(retuned.Value.Id)!.Game);
            wrong.AddRange(a.Keys.Where(k => !Equals(a[k], b.GetValueOrDefault(k))).Select(k => $"{g.Id}.{k}: {a[k]} ≠ {b.GetValueOrDefault(k)}"));
            compared++;
        }
        Assert.Empty(wrong);
        Assert.True(compared >= 20, $"звірено лише {compared} ігор");
    }

    static (Rooms Rooms, string Id)? Table(Registry registry, string gameId, Dictionary<string, string> options)
    {
        var rooms = new Rooms(registry, new FakeClock(), new GameEvents(), new FakeStakes(), new FakeStore(), RoomHarness.Empty()) { SeedOverride = 3 };
        var created = rooms.Create("Оля", gameId, options);
        return created.Reply.Ok && created.Reply.RoomId is { } id ? (rooms, id) : null;
    }

    /// <summary>Прості поля гри (числа, рядки, перелічення, bool) по всьому ланцюжку класів до <see cref="Game"/>.</summary>
    static Dictionary<string, object?> Fields(Game game)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var t = game.GetType(); t is not null && t != typeof(Game); t = t.BaseType)
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                         | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                var type = Nullable.GetUnderlyingType(f.FieldType) ?? f.FieldType;
                if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal))
                    result[$"{t.Name}.{f.Name}"] = f.GetValue(game);
            }
        return result;
    }
}
