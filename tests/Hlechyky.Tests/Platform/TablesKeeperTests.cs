using System.Net;
using System.Text.Json;
using Hlechyky.Games;
using Hlechyky.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hlechyky.Tests.Platform;

/// <summary>
/// Столи переживають перезапуск сервера (TablesKeeper, Rooms.Freeze/Restore): старий процес заморожує столи й пише знімок,
/// новий на старті повертає їх із тими самими id. Тут «сервер» — це пара Rooms + TablesKeeper над спільною текою й
/// спільними ставками/сховищем (вони в базі й рестарт переживають і так).
/// </summary>
public sealed class TablesKeeperTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hlechyky-tables-" + Guid.NewGuid().ToString("N"));
    readonly FakeClock _clock = new();
    readonly FakeStakes _stakes = new();
    readonly FakeStore _store = new();
    readonly Registry _registry = RoomHarness.NewRegistry();

    public TablesKeeperTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Один «процес сервера»: свої кімнати й події, а тека, ставки, сховище й годинник — спільні.</summary>
    sealed record Server(Rooms Rooms, TablesKeeper Keeper, GameEvents Events, List<RoomFinishedEvent> Finished);

    Server Boot()
    {
        var events = new GameEvents();
        var finished = new List<RoomFinishedEvent>();
        events.RoomFinished += finished.Add;
        var rooms = new Rooms(_registry, _clock, events, _stakes, _store, RoomHarness.Empty()) { SeedOverride = 5 };
        var keeper = new TablesKeeper(rooms, null, _clock, NullLogger<TablesKeeper>.Instance) { Root = _root };
        return new Server(rooms, keeper, events, finished);
    }

    /// <summary>Старий процес заморозив столи, новий підняв їх зі знімка.</summary>
    Server Restart(Server old, double downSeconds = 2)
    {
        old.Keeper.Freeze();
        _clock.Advance(downSeconds);
        var next = Boot();
        next.Keeper.RestoreAtStart();
        return next;
    }

    static JsonElement P(object? payload) => Views.Payload(payload);

    static string Wire(object? view) => Views.Text(view);

    static Room Room(Server s, string id) => s.Rooms.Find(id) ?? throw new InvalidOperationException($"стола {id} нема");

    static string Table(Server s, string game, params string[] nicks)
    {
        var id = s.Rooms.Create(nicks[0], game, null).Reply.RoomId!;
        foreach (var nick in nicks.Skip(1)) Assert.True(s.Rooms.Join(id, nick).Reply.Ok);
        return id;
    }

    // ---------- столи повертаються ----------

    [Fact]
    public void Lobby_table_comes_back_with_its_id_seats_host_options_and_talk()
    {
        var a = Boot();
        var id = a.Rooms.Create("Оля", "t-tick", new Dictionary<string, string> { ["boom"] = "7" }).Reply.RoomId!;
        Assert.Null(a.Rooms.TableSay(id, null, "Оля", "хто зі мною?").Error);

        var b = Restart(a);

        var room = Room(b, id);
        Assert.Equal(RoomStatus.Lobby, room.Status);
        Assert.Equal(new string?[] { "Оля", null }, room.Seats);
        Assert.Equal("Оля", room.Host);
        Assert.Equal("7", room.Options["boom"]);
        Assert.Equal("хто зі мною?", Assert.Single(b.Rooms.TableLines(id)).Text);
        Assert.Single(b.Rooms.Snapshot());
        // і стіл живий: другий сідає — партія стартує, як і без перезапуску
        Assert.True(b.Rooms.Join(id, "Петро").Reply.Ok);
        Assert.Equal(RoomStatus.Playing, room.Status);
    }

    [Fact]
    public void Finished_table_shows_the_same_result_and_board_until_the_next_round()
    {
        var a = Boot();
        var id = Table(a, "t-count", "Оля", "Петро");
        Assert.True(a.Rooms.Act(id, "Оля", "add", P(new { v = 4 })).Reply.Ok);
        Assert.True(a.Rooms.Act(id, "Петро", "end", P(null)).Reply.Ok);
        var before = Room(a, id);
        string olya, watcher;
        lock (before.Sync)
        {
            olya = Wire(before.Game.Snapshot(0));
            watcher = Wire(before.Game.Snapshot(null));
        }

        var b = Restart(a);

        var room = Room(b, id);
        Assert.Equal(RoomStatus.Finished, room.Status);
        Assert.Equal(new[] { 1 }, room.Result!.Winners);
        Assert.Equal("лічильник: 4", room.Result.Text);
        Assert.Equal(1, Assert.Single(room.Evening.Values, e => e.Nick == "Петро").Wins);
        Assert.Equal(1, room.EveningGames);
        // Гра в новому процесі новенька (сума 0), але люди бачать той самий підсумок, що й до перезапуску.
        b.Rooms.Watch(id, "c1", "Оля");
        Assert.Equal(olya, Wire(b.Rooms.SnapshotFor(id, "c1", "Оля")!.View));
        var views = b.Rooms.ViewsFor(id)!;
        Assert.Equal(olya, Wire(views.SeatViews[0]));
        Assert.Equal(watcher, Wire(views.WatcherView));

        // «Ще раз» — нова партія, і вид уже від гри
        Assert.True(b.Rooms.Rematch(id, "Оля").Reply.Ok);
        Assert.Null(room.Restored);
        Assert.Equal(0, b.Rooms.ViewsFor(id)!.SeatViews.Values.Select(v => Views.Json(v).GetProperty("sum").GetInt32()).First());
        Assert.Equal(2, room.Round);
    }

    [Fact]
    public void Game_that_can_save_plays_on_after_a_clean_freeze()
    {
        var a = Boot();
        var id = Table(a, "t-count", "Оля", "Петро");
        Assert.True(a.Rooms.Act(id, "Оля", "add", P(new { v = 2 })).Reply.Ok);
        Assert.True(a.Rooms.Act(id, "Петро", "add", P(new { v = 3 })).Reply.Ok);
        var talk = a.Rooms.TableLines(id).Count;

        var b = Restart(a);

        var room = Room(b, id);
        Assert.Equal(RoomStatus.Playing, room.Status);
        Assert.Null(room.Restored);
        var view = Views.Json(b.Rooms.ViewsFor(id)!.SeatViews[0]);
        Assert.Equal(5, view.GetProperty("sum").GetInt32());
        Assert.Equal(1, view.GetProperty("last").GetInt32());
        Assert.Equal(2, room.Moves);
        // Start нової гри сказав би «Почали лічити» вдруге — балачка мусить бути рівно та сама.
        Assert.Equal(talk, b.Rooms.TableLines(id).Count);

        Assert.True(b.Rooms.Act(id, "Оля", "add", P(new { v = 1 })).Reply.Ok);
        Assert.True(b.Rooms.Act(id, "Петро", "end", P(null)).Reply.Ok);
        Assert.Equal("лічильник: 6", Assert.Single(b.Finished).Result.Text);
    }

    [Fact]
    public void New_server_that_dies_right_after_restoring_leaves_the_clean_snapshot_for_the_rollback()
    {
        var a = Boot();
        var id = Table(a, "t-count", "Оля", "Петро");
        Assert.True(a.Rooms.Act(id, "Оля", "add", P(new { v = 7 })).Reply.Ok);
        a.Keeper.Freeze();

        _clock.Advance(2);
        Boot().Keeper.RestoreAtStart();   // новий код підняв столи — і впав, не дійшовши до свого знімка
        _clock.Advance(10);
        var back = Boot();                // відкат: попередня збірка стартує з того самого файла

        Assert.Equal(1, back.Keeper.RestoreAtStart()!.Continued);
        Assert.Equal(RoomStatus.Playing, Room(back, id).Status);
        Assert.Equal(7, Views.Json(back.Rooms.ViewsFor(id)!.SeatViews[0]).GetProperty("sum").GetInt32());
    }

    [Fact]
    public void Game_with_a_save_but_not_resumable_is_busy_and_gets_interrupted()
    {
        var a = Boot();
        var id = Table(a, "t-saveonly", "Оля", "Петро");
        Assert.Equal(id, Assert.Single(a.Rooms.Busy()).Id);   // деплой мусить чекати — партію не продовжити

        var b = Restart(a);

        Assert.Equal(RoomStatus.Finished, Room(b, id).Status);
        Assert.Equal(Rooms.InterruptedVerdict, Room(b, id).Result!.Verdict);
    }

    [Fact]
    public void Resumed_chess_clock_does_not_charge_the_restart_to_the_player_on_move()
    {
        var a = Boot();
        var id = a.Rooms.Create("Оля", "chess", new Dictionary<string, string> { ["clock"] = "3" }).Reply.RoomId!;
        Assert.True(a.Rooms.Join(id, "Петро").Reply.Ok);
        var white = Room(a, id).Seats[0]!;
        Assert.True(a.Rooms.Act(id, white, "move", P(new { from = "e2", to = "e4" })).Reply.Ok);
        _clock.Advance(10);                                       // чорні думають 10 с…
        var before = Views.Json(a.Rooms.ViewsFor(id)!.WatcherView).GetProperty("clock").GetProperty("ms")[1].GetInt64();

        var b = Restart(a, downSeconds: 30);                      // …а потім сервер 30 с перезапускається

        var after = Views.Json(b.Rooms.ViewsFor(id)!.WatcherView).GetProperty("clock").GetProperty("ms")[1].GetInt64();
        Assert.Equal(RoomStatus.Playing, Room(b, id).Status);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Table_settings_made_in_the_lobby_come_back()
    {
        var a = Boot();
        var id = a.Rooms.Create("Оля", "t-lobby", null).Reply.RoomId!;
        Assert.True(a.Rooms.Act(id, "Оля", "pack", P(new { id = "весняний" })).Reply.Ok);

        var b = Restart(a);

        Assert.Equal("весняний", Views.Json(b.Rooms.ViewsFor(id)!.WatcherView).GetProperty("pack").GetString());
        Assert.True(b.Rooms.StartByHost(id, "Оля").Reply.Ok);   // без пакета «Почати» відмовило б
        Assert.Empty(Room(b, id).LobbyActs);
    }

    [Fact]
    public void Restore_from_a_routine_snapshot_does_not_refund_a_round_already_paid()
    {
        _stakes.Set("Оля", 20).Set("Петро", 20);
        var a = Boot();
        var id = a.Rooms.Create("Оля", "t-duel", new Dictionary<string, string> { ["stake"] = "5" }).Reply.RoomId!;
        Assert.True(a.Rooms.Join(id, "Петро").Reply.Ok);
        a.Keeper.Snapshot();                                      // знімок застав партію, що йде…
        Assert.True(a.Rooms.Act(id, "Оля", "win", P(null)).Reply.Ok);   // …а її дограли й заплатили вже після нього
        Assert.Equal(25, _stakes.Balance("Оля"));
        _clock.Advance(3);

        var b = Boot();
        Assert.Equal(1, b.Keeper.RestoreAtStart()!.Interrupted);

        Assert.Equal(25, _stakes.Balance("Оля"));
        Assert.Equal(15, _stakes.Balance("Петро"));
    }

    [Fact]
    public void Stakes_come_back_when_a_playing_table_cannot_be_restored()
    {
        var a = Boot();
        var frozen = new FrozenRoom("bad00001", "t-badconfig", new Dictionary<string, string>(), ["Оля", "Петро"], "Оля",
            RoomStatus.Playing, 5, 1, null, _clock.UtcNow, _clock.UtcNow, null, null, 3, ["Оля", "Петро"], null, null, [], [], 0);

        var report = a.Rooms.Restore(new FrozenTables(FrozenTables.CurrentVersion, _clock.UtcNow, true, [frozen]));

        Assert.Equal(1, report.Skipped);
        Assert.Null(a.Rooms.Find("bad00001"));
        Assert.Contains("grant:Оля:5:stake-refund:bad00001:1:оля", _stakes.Calls);
        Assert.Contains("grant:Петро:5:stake-refund:bad00001:1:петро", _stakes.Calls);
    }

    [Fact]
    public void Game_without_save_is_interrupted_and_stakes_come_back()
    {
        _stakes.Set("Оля", 20).Set("Петро", 20);
        var a = Boot();
        var id = a.Rooms.Create("Оля", "t-duel", new Dictionary<string, string> { ["stake"] = "5" }).Reply.RoomId!;
        Assert.True(a.Rooms.Join(id, "Петро").Reply.Ok);
        Assert.Equal(RoomStatus.Playing, Room(a, id).Status);
        Assert.Equal(15, _stakes.Balance("Оля"));

        var b = Restart(a);

        var room = Room(b, id);
        Assert.Equal(RoomStatus.Finished, room.Status);
        Assert.Equal(Rooms.InterruptedVerdict, room.Result!.Verdict);
        Assert.Empty(room.Result.Winners);
        Assert.Equal(20, _stakes.Balance("Оля"));
        Assert.Equal(20, _stakes.Balance("Петро"));
        Assert.Contains($"grant:Оля:5:stake-refund:{id}:1:оля", _stakes.Calls);
        Assert.Empty(room.Charged);
        // ні результату, ні рейтингу: партію ніхто не дограв
        Assert.Empty(b.Finished);
        Assert.Equal(0, room.EveningGames);
        // і можна грати далі тим самим складом
        Assert.True(b.Rooms.Rematch(id, "Петро").Reply.Ok);
        Assert.Equal(RoomStatus.Playing, room.Status);
    }

    [Fact]
    public void Snapshot_without_freeze_brings_tables_back_but_cuts_the_game_even_if_it_could_save()
    {
        var a = Boot();
        var id = Table(a, "t-count", "Оля", "Петро");
        Assert.True(a.Rooms.Act(id, "Оля", "add", P(new { v = 9 })).Reply.Ok);
        a.Keeper.Snapshot();                       // знімок про всяк випадок…
        _clock.Advance(5);                         // …а потім сервер упав, і ходи після знімка загубились би
        var b = Boot();

        Assert.Equal(1, b.Keeper.RestoreAtStart()!.Interrupted);

        var room = Room(b, id);
        Assert.Equal(RoomStatus.Finished, room.Status);
        Assert.Equal(Rooms.InterruptedVerdict, room.Result!.Verdict);
        // видно дошку, якою вона була на знімку
        Assert.Equal(9, Views.Json(b.Rooms.ViewsFor(id)!.SeatViews[0]).GetProperty("sum").GetInt32());
    }

    [Fact]
    public void Old_snapshot_is_not_taken()
    {
        var a = Boot();
        Table(a, "t-count", "Оля", "Петро");
        a.Keeper.Snapshot();
        _clock.Advance(TablesKeeper.MaxAge + TimeSpan.FromSeconds(1));

        var b = Boot();
        Assert.Null(b.Keeper.RestoreAtStart());
        Assert.Empty(b.Rooms.Snapshot());
    }

    [Fact]
    public void Nothing_to_restore_without_a_file()
    {
        var b = Boot();
        Assert.Null(b.Keeper.RestoreAtStart());
    }

    // ---------- заморозка ----------

    [Fact]
    public void Frozen_tables_refuse_moves_seats_and_ticks_until_thawed()
    {
        var a = Boot();
        var tick = Table(a, "t-tick", "Оля", "Петро");
        var count = Table(a, "t-count", "Іра", "Тарас");
        Assert.Equal(RoomStatus.Playing, Room(a, tick).Status);
        _clock.AdvanceMs(200);
        Assert.Single(a.Rooms.TickDue(_clock.UtcNow));

        a.Keeper.Freeze();

        Assert.True(a.Rooms.Frozen);
        Assert.Empty(a.Rooms.TickDue(_clock.UtcNow));
        Assert.Empty(a.Rooms.Tick(Room(a, tick)));
        var act = a.Rooms.Act(count, "Іра", "add", P(null)).Reply;
        Assert.False(act.Ok);
        Assert.Equal("⏳ Сайт оновлюється — за пару секунд продовжимо", act.Message);
        Assert.False(a.Rooms.Leave(count, "Тарас").Reply.Ok);
        Assert.False(a.Rooms.Create("Богдан", "t-duel", null).Reply.Ok);
        Assert.NotNull(a.Rooms.TableSay(count, null, "Іра", "ау").Error);
        // усі з'єднання зараз урвуться разом — це не привід звільняти місця
        a.Rooms.NoteOffline("Іра", _clock.UtcNow);
        _clock.Advance(Rooms.Grace + TimeSpan.FromSeconds(5));
        Assert.Empty(a.Rooms.DropIfGone(_clock.UtcNow));
        Assert.Equal("Іра", Room(a, count).Seats[0]);

        a.Keeper.Thaw();

        Assert.False(a.Rooms.Frozen);
        Assert.True(a.Rooms.Act(count, "Іра", "add", P(null)).Reply.Ok);
        Assert.Single(a.Rooms.TickDue(_clock.UtcNow));
    }

    [Fact]
    public void Freeze_that_never_ends_in_a_restart_is_thawed_and_the_file_rewritten()
    {
        var a = Boot();
        var id = Table(a, "t-count", "Оля", "Петро");
        a.Keeper.Freeze();
        Assert.True(Read().Clean);

        a.Keeper.Thaw();

        Assert.False(Read().Clean);   // інакше колись пізніше старт узяв би чистий знімок і повернув партію без нових ходів
        Assert.True(a.Rooms.Act(id, "Оля", "add", P(null)).Reply.Ok);
    }

    FrozenTables Read() => JsonSerializer.Deserialize<FrozenTables>(File.ReadAllBytes(Path.Combine(_root, TablesKeeper.FileName)),
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;

    // ---------- люди повертаються ----------

    [Fact]
    public void Restored_players_get_a_minute_to_come_back()
    {
        var a = Boot();
        var id = Table(a, "t-party", "Оля", "Петро");
        var b = Restart(a);

        _clock.Advance(Rooms.Grace + TimeSpan.FromSeconds(10));   // звичайний grace минув — а місце ще тримаємо
        Assert.Empty(b.Rooms.DropIfGone(_clock.UtcNow));
        b.Rooms.NoteOnline("Оля");                                 // Оля повернулась, Петро — ні
        _clock.Advance(Rooms.RestoreGrace);
        b.Rooms.DropIfGone(_clock.UtcNow);

        Assert.Equal(new string?[] { "Оля", null, null, null }, Room(b, id).Seats);
    }

    [Fact]
    public void Talk_numbers_go_on_after_a_restart()
    {
        var a = Boot();
        var id = Table(a, "t-party", "Оля", "Петро");
        a.Rooms.TableSay(id, null, "Оля", "раз");
        a.Rooms.TableSay(id, null, "Петро", "два");
        var last = a.Rooms.TableLastId(id);

        var b = Restart(a);
        b.Rooms.TableSay(id, null, "Оля", "три");

        Assert.Equal(new[] { "раз", "два", "три" }, b.Rooms.TableLines(id).Select(l => l.Text));
        Assert.True(b.Rooms.TableLastId(id) > last);
    }

    [Fact]
    public void Persistent_solo_room_comes_back_with_the_same_id_and_saved_state()
    {
        var a = Boot();
        var id = a.Rooms.OpenSolo("Оля", "t-solo", null).Reply.RoomId!;
        Assert.True(a.Rooms.Act(id, "Оля", "add", P(new { v = 5 })).Reply.Ok);

        var b = Restart(a);

        Assert.Equal(5, Views.Json(b.Rooms.ViewsFor(id)!.SeatViews[0]).GetProperty("value").GetInt64());
        Assert.Equal(id, b.Rooms.OpenSolo("Оля", "t-solo", null).Reply.RoomId);
    }

    // ---------- коли можна перезапускати ----------

    [Fact]
    public void Busy_lists_only_games_a_restart_would_cut()
    {
        var a = Boot();
        var duel = Table(a, "t-duel", "Оля", "Петро");      // йде і не вміє зберегтись — перервалась би
        Table(a, "t-count", "Іра", "Тарас");                // йде, але вміє — переживе
        Table(a, "t-party", "Богдан", "Олена");             // лобі
        a.Rooms.OpenSolo("Марко", "t-solo", null);          // Persistent-соло — у сховищі й так

        var busy = Assert.Single(a.Rooms.Busy());
        Assert.Equal(duel, busy.Id);
        Assert.Equal(new[] { "Оля", "Петро" }, busy.Players);

        Assert.True(a.Rooms.Act(duel, "Оля", "win", P(null)).Reply.Ok);
        Assert.Empty(a.Rooms.Busy());
    }

    [Fact]
    public void Internal_endpoints_want_this_machine_and_the_key()
    {
        static HttpContext Call(string ip, string? key)
        {
            var c = new DefaultHttpContext();
            c.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            if (key is not null) c.Request.Headers[TablesKeeper.KeyHeader] = key;
            return c;
        }
        var key = Boot().Keeper.ControlKey();

        Assert.True(key.Length >= 32);
        Assert.Equal(key, Boot().Keeper.ControlKey());            // ключ створюється раз і лежить у data/
        Assert.True(TablesKeeper.Allowed(Call("127.0.0.1", key), key));
        Assert.True(TablesKeeper.Allowed(Call("::1", key), key));
        Assert.False(TablesKeeper.Allowed(Call("127.0.0.1", null), key));
        Assert.False(TablesKeeper.Allowed(Call("127.0.0.1", key[..^1] + "x"), key));
        Assert.False(TablesKeeper.Allowed(Call("93.184.216.34", key), key));   // ззовні (через Caddy) — ні, навіть із ключем
    }

    // ---------- кожна справжня гра ----------

    /// <summary>
    /// Кожна гра сервера: стіл у лобі, а де вдається почати — і посеред партії, переживає знімок і повернення, і види
    /// дограного/перерваного столу серіалізуються. Без сервісів частина ігор не стартує — тоді перевіряємо лише лобі.
    /// </summary>
    [Fact]
    public void Every_game_table_survives_a_restart()
    {
        var nicks = new[] { "Оля", "Петро", "Іра", "Тарас", "Богдан", "Олена", "Марко", "Ніна", "Яким", "Зоя", "Лука", "Віра" };
        var failures = new List<string>();
        int checkedTables = 0, midGame = 0;
        foreach (var info in _registry.Games)
        {
            if (info.Solo || info.Id.StartsWith("t-", StringComparison.Ordinal)) continue;
            var a = Boot();
            var created = a.Rooms.Create(nicks[0], info.Id, null);
            if (!created.Reply.Ok) continue;   // без сервісів (словники, фото) гра не налаштовується — це не про перезапуск
            var id = created.Reply.RoomId!;
            // «Коли всі місця зайняті» — повний стіл; «Почати» — щонайменше двоє: самому реалтайм без бота не стартує
            var need = info.Start == StartMode.WhenFull ? info.MaxPlayers : Math.Max(2, info.MinPlayers);
            for (var i = 1; i < need && i < nicks.Length; i++) a.Rooms.Join(id, nicks[i]);
            if (info.Start == StartMode.ByHost) a.Rooms.StartByHost(id, nicks[0]);
            var was = Room(a, id).Status;

            Server b;
            try { b = Restart(a); }
            catch (Exception ex) { failures.Add($"{info.Id}: перезапуск упав — {ex.Message}"); continue; }
            finally { File.Delete(Path.Combine(_root, TablesKeeper.FileName)); }

            var room = b.Rooms.Find(id);
            if (room is null) { failures.Add($"{info.Id}: стіл ({was}) не повернувся"); continue; }
            checkedTables++;
            if (was == RoomStatus.Playing) midGame++;
            if (b.Rooms.ViewsFor(id) is null) failures.Add($"{info.Id}: стіл ({was} → {room.Status}) не складає видів");
            if (was == RoomStatus.Playing && room.Status == RoomStatus.Finished && room.Restored is null)
                failures.Add($"{info.Id}: перервана партія без збереженого виду");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        // щоб тест не позеленів тихо, коли ігри перестануть створюватись чи стартувати без сервісів
        Assert.True(checkedTables >= 50, $"перевірено лише {checkedTables} столів");
        Assert.True(midGame >= 40, $"посеред партії перевірено лише {midGame}");
    }
}
