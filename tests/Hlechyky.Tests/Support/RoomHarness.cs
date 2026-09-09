using System.Text.Json;
using Hlechyky.Games;
using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Tests.Support;

/// <summary>
/// Одна кімната під ключ: реєстр, кімнати, фейкові годинник, ставки й сховище. Гра створюється через
/// <see cref="Registry"/> — так само, як її побачить сервер, тож тест гри перевіряє саме те, у що гратимуть
/// люди (TESTING.md §2).
/// </summary>
/// <example>
/// <code>
/// var h = new RoomHarness("ttt", seed: 42);
/// h.Join("Оля"); h.Join("Петро");             // WhenFull → партія почалась
/// h.Act(0, "move", new { cell = 4 }).Ok;
/// h.View(1).GetProperty("turn").GetInt32();
/// </code>
/// </example>
public sealed class RoomHarness
{
    readonly string _gameId;
    readonly Dictionary<string, string>? _options;
    readonly List<string> _joined = [];

    public RoomHarness(string gameId, object? options = null, int seed = 1, IServiceProvider? services = null)
    {
        _gameId = gameId;
        _options = ToOptions(options);
        Registry = NewRegistry();
        Rooms = new Rooms(Registry, Clock, Events, Stakes, Store, services ?? Empty()) { SeedOverride = seed };
        Events.RoomFinished += e => Finished.Add(e);
        Events.SoloScored += e => Scores.Add(e);
        Events.Awarded += e => Awards.Add(e);
    }

    public FakeClock Clock { get; } = new();
    public GameEvents Events { get; } = new();
    public FakeStakes Stakes { get; } = new();
    public FakeStore Store { get; } = new();
    public Registry Registry { get; }
    public Rooms Rooms { get; }

    /// <summary>Усі повідомлення розсилки за весь час життя обгортки, у порядку появи.</summary>
    public List<Outgoing> Outbox { get; } = [];
    public List<RoomFinishedEvent> Finished { get; } = [];
    public List<SoloScoreEvent> Scores { get; } = [];
    public List<AwardEvent> Awards { get; } = [];

    /// <summary>Останній RoomReply — щоб тест бачив текст відмови.</summary>
    public RoomReply Reply { get; private set; } = RoomReply.Done;

    public string RoomId { get; private set; } = "";

    public Room Room => Rooms.Find(RoomId) ?? throw new InvalidOperationException("кімнати вже нема");

    /// <summary>Порожній провайдер, коли грі нічого від сервера не треба.</summary>
    public static IServiceProvider Empty() => new ServiceCollection().BuildServiceProvider();

    /// <summary>Реєстр із іграми сервера і заглушками з <c>Support/TestGames.cs</c>.</summary>
    public static Registry NewRegistry() => new(null, typeof(RoomHarness).Assembly);

    /// <summary>Провайдер з одним сервісом: <c>RoomHarness.WithService&lt;Words&gt;(words)</c>.</summary>
    public static IServiceProvider WithService<T>(T instance) where T : class =>
        new ServiceCollection().AddSingleton(instance).BuildServiceProvider();

    /// <summary>Перший гість створює кімнату і стає господарем, решта сідають.</summary>
    public RoomReply Join(string nick)
    {
        var outcome = _joined.Count == 0
            ? Rooms.Create(nick, _gameId, _options)
            : Rooms.Join(RoomId, nick);
        if (outcome.Reply.Ok)
        {
            _joined.Add(nick);
            RoomId = outcome.Reply.RoomId ?? RoomId;
        }
        return Take(outcome);
    }

    /// <summary>Особиста соло-кімната (клікер, щоденне).</summary>
    public RoomReply Solo(string nick, string? key = null)
    {
        var outcome = Rooms.OpenSolo(nick, _gameId, key);
        if (outcome.Reply.Ok)
        {
            if (!_joined.Contains(nick)) _joined.Add(nick);
            RoomId = outcome.Reply.RoomId ?? RoomId;
        }
        return Take(outcome);
    }

    /// <summary>Господар тисне «Почати» (ігри з <see cref="StartMode.ByHost"/>).</summary>
    public RoomReply Start() => Take(Rooms.StartByHost(RoomId, Room.Host));

    public RoomReply Leave(string nick) => Take(Rooms.Leave(RoomId, nick));

    public RoomReply Rematch(string? nick = null) => Take(Rooms.Rematch(RoomId, nick ?? Seated()));

    /// <summary>Хід із місця seat. Payload серіалізується так само, як його передасть хаб.</summary>
    public ActResult Act(int seat, string action, object? payload = null)
    {
        var nick = NickOf(seat);
        var outcome = Rooms.Act(RoomId, nick, action, Views.Payload(payload));
        Take(outcome);
        return new ActResult(outcome.Reply.Ok, outcome.Reply.Message);
    }

    /// <summary>Реалтайм-ввід із місця seat.</summary>
    public void Input(int seat, string action, object? payload = null)
    {
        Outbox.AddRange(Rooms.Input(RoomId, NickOf(seat), action, Views.Payload(payload)));
    }

    /// <summary>Стільки тиків, скільки просять: годинник рухається рівно на TickMs, як у проді.</summary>
    public void Tick(int times = 1)
    {
        var step = Math.Max(1, Room.Info.TickMs);
        for (var i = 0; i < times; i++)
        {
            Clock.AdvanceMs(step);
            foreach (var room in Rooms.TickDue(Clock.UtcNow)) Outbox.AddRange(Rooms.Tick(room));
        }
    }

    /// <summary>Вид місця seat (null — глядач) у тому самому JSON, що піде на дріт.</summary>
    public JsonElement View(int? seat)
    {
        var room = Room;
        lock (room.Sync) return Views.Json(room.Game.View(seat));
    }

    public string NickOf(int seat) => Room.Seats[seat] ?? throw new InvalidOperationException($"місце {seat} вільне");

    string Seated() => Room.Seats.FirstOrDefault(s => s is not null) ?? throw new InvalidOperationException("за столом нікого");

    RoomReply Take(RoomOutcome outcome)
    {
        Outbox.AddRange(outcome.Out);
        Reply = outcome.Reply;
        return outcome.Reply;
    }

    static Dictionary<string, string>? ToOptions(object? options)
    {
        if (options is null) return null;
        if (options is IReadOnlyDictionary<string, string> ready) return new Dictionary<string, string>(ready, StringComparer.Ordinal);
        var json = Views.Json(options);
        if (json.ValueKind != JsonValueKind.Object) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in json.EnumerateObject())
            result[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        return result;
    }
}
