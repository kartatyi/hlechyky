namespace Hlechyky.Games.Impl;

/// <summary>
/// Пул міні-ігор вечірки (§8.1): таблиця категорій і ваг — тут, межі гравців і стеля — з самої гри
/// (<see cref="IPartyMinigame"/>). Гра без режиму вечірки в пул не потрапляє, навіть якщо вписана: адаптер не встиг —
/// вечірка не ламається.
/// </summary>
public static class VechirkaPool
{
    /// <summary>id, категорія (move/brain/tap), вага в дуелях, вага в загальних (0 — лише дуелі).</summary>
    public static readonly (string Id, string Cat, int DuelW, int Weight)[] Table =
    [
        ("icefloe", "move", 1, 1),
        ("geese", "brain", 1, 1),
        ("tyr", "tap", 2, 1),
        ("skyrta", "tap", 2, 1),
        ("sklei", "brain", 1, 1),
        ("bakhne", "brain", 1, 1),
        ("thinice", "move", 1, 1),
        ("brid", "move", 1, 1),
        ("grushi", "move", 1, 1),
        ("hostyntsi", "tap", 2, 1),
        ("potato", "move", 0, 1),
        ("curve", "move", 1, 1),
        ("dino", "tap", 1, 1),
        ("freeze", "move", 0, 1),
        ("bomber", "move", 1, 1),
        ("skilky", "brain", 1, 1),
        ("duel", "tap", 3, 0),
        ("pong", "move", 3, 0),
        ("hockey", "move", 2, 0),
        ("tron", "move", 3, 0),
    ];

    static readonly Lazy<IReadOnlyList<VechirkaPoolEntry>> Cache = new(Build);

    /// <summary>Доступні зараз (кеш на процес: набір ігор міняється лише з деплоєм).</summary>
    public static IReadOnlyList<VechirkaPoolEntry> Available => Cache.Value;

    static IReadOnlyList<VechirkaPoolEntry> Build()
    {
        var res = new List<VechirkaPoolEntry>();
        foreach (var (id, cat, duelW, weight) in Table)
        {
            if (!PartyPool.Has(id) || PartyPool.Create(id) is not { } g || g is not IPartyMinigame mini) continue;
            res.Add(new VechirkaPoolEntry(id, g.Info.Title, mini.Howto, cat, duelW, weight,
                mini.PartyMin, mini.PartyMax, Math.Clamp(mini.PartyCapMs, 1000, MinigameHost.MaxCapMs)));
        }
        return res;
    }
}
