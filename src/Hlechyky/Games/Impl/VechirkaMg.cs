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
