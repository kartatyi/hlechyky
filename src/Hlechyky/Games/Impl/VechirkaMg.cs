using System.Text.Json;

namespace Hlechyky.Games.Impl;

/// <summary>Рядок пулу міні-ігор вечірки (§8.1): категорія й ваги — з таблиці, межі й стеля — з екземпляра гри.</summary>
public sealed record VechirkaPoolEntry(string Id, string Title, string Howto, string Cat, int DuelW, int Weight, int Min, int Max, int CapMs);

/// <summary>
/// Міні-гра для ядра вечірки (§24.1). Місця — індекси P. Заглушка (<see cref="VechirkaStubMg"/>) — миттєвий
/// результат; справжній — на <see cref="MinigameHost"/> (S1.4).
/// </summary>
public interface IMgRunner
{
    bool Begin(string id, int[] pSeats, bool[] bot, LiveBots.Level level);
    string Title { get; }
    string Howto { get; }
    int CapMs { get; }
    ActResult Act(int p, string action, JsonElement payload);
    TickResult Tick();
    object? View(int? p);
    object? Frame();
    string[] Names();
    string[] SeatNames();
    MinigameResult? Result { get; }
}

/// <summary>
/// Заглушка: результат на першому тику, scores — зі свого ГВЧ (засіяного ззовні) або з <see cref="Scorer"/>
/// (тести: «людина, що грає добре»).
/// </summary>
public sealed class VechirkaStubMg(ulong seed) : IMgRunner
{
    readonly VechirkaRng _r = new(seed);
    int[] _seats = [];
    string _id = "";
    public Func<int[], long[]>? Scorer { get; set; }
    public bool Fail { get; set; }
    public int Begun { get; private set; }
    public List<string> Played { get; } = [];

    public bool Begin(string id, int[] pSeats, bool[] bot, LiveBots.Level level)
    {
        if (Fail) return false;
        _id = id; _seats = pSeats; Result = null; Begun++; Played.Add(id);
        return true;
    }

    public string Title => _id;
    public string Howto => "";
    public int CapMs => 1000;
    public ActResult Act(int p, string action, JsonElement payload) => ActResult.Done;

    public TickResult Tick()
    {
        if (Result is not null || _seats.Length == 0) return TickResult.None;
        var sc = Scorer?.Invoke(_seats) ?? [.. _seats.Select(_ => (long)_r.Next(100))];
        Result = MinigameResult.From(_seats.Length, Enumerable.Range(0, _seats.Length).ToDictionary(k => k, k => sc[k]), "", null, MinigameEnd.Finished);
        return TickResult.Both;
    }

    public object? View(int? p) => null;
    public object? Frame() => null;
    public string[] Names() => [.. _seats.Select(s => s.ToString())];
    public string[] SeatNames() => [.. _seats.Select(s => s.ToString())];
    public MinigameResult? Result { get; private set; }
}

/// <summary>
/// Батьківський контекст підгри (§8.3): «місця батька» — індекси P, а не крісла кімнати. Так повернення на інше
/// крісло й двоє, що повернулись навхрест, не плутають підгру; Log підгри не йде в Журнал сайту (вечірка пише свій
/// рядок підсумку); репліки підгри — через обмежувач вечірки.
/// </summary>
public sealed class VechirkaMgCtx(Vechirka game, int seed) : IRoomContext
{
    readonly Random _rng = new(seed);
    IRoomContext Room => game.Ctx;
    /// <summary>Скільки рядків Журналу підгра хотіла написати (тести: жоден не протік).</summary>
    public int Swallowed { get; private set; }
    public int Muted { get; private set; }

    public string RoomId => Room.RoomId;
    public int Players => game.Core?.N ?? 0;
    public int Round => Room.Round;
    public Random Rng => _rng;
    public IClock Clock => Room.Clock;
    public IReadOnlyDictionary<string, string> Options => Room.Options;
    public IServiceProvider Services => Room.Services;
    public string? NickOf(int seat) => game.Watching(seat) ? null : game.NickAt(seat);
    public bool Seated(int seat) => NickOf(seat) is not null;
    public int? HostSeat => Room.HostSeat is { } h ? game.POf(h) : null;
    public void Finish(int[] winners, string log, IReadOnlyDictionary<int, long>? scores = null, string? verdict = null) => Muted++;
    public void Log(string text) => Swallowed++;
    public void Say(string text) => game.SubSay(text);
    public void Score(int seat, double value, int? attempts = null) => Muted++;
    public void Award(int seat, int shards, string reason) => Muted++;
}

/// <summary>Справжня міні-гра на <see cref="MinigameHost"/> каркаса (S1.4).</summary>
public sealed class VechirkaHostMg(Vechirka game) : IMgRunner
{
    MinigameHost? _host;
    public VechirkaMgCtx? Ctx { get; private set; }
    public MinigameHost? Host => _host;

    public bool Begin(string id, int[] pSeats, bool[] bot, LiveBots.Level level)
    {
        var seed = game.Core?.Rand(int.MaxValue) ?? 1;
        Ctx = new VechirkaMgCtx(game, seed);
        var seats = pSeats.Select((p, k) => bot[k] ? PartySeat.BotAt(p) : PartySeat.Human(p)).ToArray();
        _host = MinigameHost.Create(id, Ctx, seats, level);
        if (_host is null) return false;
        _host.Start();
        return true;
    }

    public string Title => _host?.Title ?? "";
    public string Howto => _host?.Howto ?? "";
    public int CapMs => _host?.CapMs ?? 0;
    public ActResult Act(int p, string action, JsonElement payload) => _host?.Act(p, action, payload) ?? ActResult.Fail("Міні-гри нема");
    public TickResult Tick() => _host?.Tick() ?? TickResult.None;
    public object? View(int? p) => _host?.View(p);
    public object? Frame() => _host?.Frame();
    public string[] Names() => _host?.Names() ?? [];
    public string[] SeatNames() => _host?.SeatNames() ?? [];
    public MinigameResult? Result => _host?.Result;
}
