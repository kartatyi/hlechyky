using Hlechyky.Games;

namespace Hlechyky.Tests.Support;

/// <summary>
/// Ставки без SQLite: баланси виставляє тест, кожен виклик лишає слід у <see cref="Calls"/>, повтор із тим
/// самим refKey — no-op (саме так поводиться справжня економіка).
/// </summary>
public sealed class FakeStakes : IStakes
{
    readonly Dictionary<string, int> _balances = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _refs = new(StringComparer.Ordinal);

    /// <summary>Усе, що каркас попросив: «spend:Оля:5:stake:room:1:оля», «grant:Оля:10:stake-win:…».</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Кличеться на кожній виплаті — щоб тест побачив, у якому оточенні каркас її робить.</summary>
    public Action? OnGrant { get; set; }

    public FakeStakes Set(string nick, int balance)
    {
        _balances[nick] = balance;
        return this;
    }

    public int Balance(string nick) => _balances.TryGetValue(nick, out var b) ? b : 0;

    public bool TrySpend(string nick, int amount, string reason, string refKey)
    {
        if (!_refs.Add(refKey)) return true;   // уже списано цим ключем
        if (Balance(nick) < amount) return false;
        _balances[nick] = Balance(nick) - amount;
        Calls.Add($"spend:{nick}:{amount}:{refKey}");
        return true;
    }

    public void Grant(string nick, int amount, string reason, string refKey)
    {
        OnGrant?.Invoke();
        if (!_refs.Add(refKey)) return;
        _balances[nick] = Balance(nick) + amount;
        Calls.Add($"grant:{nick}:{amount}:{refKey}");
    }
}

/// <summary>Сховище станів Persistent-ігор у пам'яті.</summary>
public sealed class FakeStore : IGameStore
{
    public Dictionary<string, string> States { get; } = new(StringComparer.Ordinal);
    /// <summary>Скільки разів каркас справді щось зберіг — щоб перевіряти «зберігає після кожного ходу».</summary>
    public int Saves { get; private set; }

    public void SaveState(string key, string json)
    {
        States[key] = json;
        Saves++;
    }

    public string? LoadState(string key) => States.TryGetValue(key, out var json) ? json : null;

    public void DeleteState(string key) => States.Remove(key);
}

/// <summary>Черга розсилки, яка нікуди не летить: тест просто дивиться, що в ній опинилось.</summary>
public sealed class FakeOutbox : IOutbox
{
    public List<Outgoing> Posted { get; } = [];

    public void Post(Outgoing message) => Posted.Add(message);
}
