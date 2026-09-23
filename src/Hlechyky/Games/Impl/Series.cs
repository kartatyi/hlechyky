namespace Hlechyky.Games.Impl;

/// <summary>
/// Рахунок серії за одним столом: «Ще раз» тим самим складом — і видно, хто кого вже скільки разів обіграв.
/// Живе в екземплярі гри (він один на кімнату й переживає кожен «Ще раз»), а рахує за ніками, не за місцями:
/// «Ще раз» місця обертає, а рахунок має їхати разом із людиною. Новий склад за столом — рахунок з нуля.
/// </summary>
public sealed class Series
{
    readonly Dictionary<string, int> _wins = new(StringComparer.Ordinal);
    int _draws;
    int _games;
    string _crew = "";

    static string Key(string nick) => nick.Trim().ToLowerInvariant();

    /// <summary>Хто зараз за столом — ключем, без порядку місць.</summary>
    static string Crew(IRoomContext ctx, int seats) => string.Join("|",
        Enumerable.Range(0, seats).Select(ctx.NickOf).Where(n => n is not null).Select(n => Key(n!)).Order(StringComparer.Ordinal));

    /// <summary>Кличеться зі Start(): склад змінився — рахунок попереднього складу нікому не цікавий.</summary>
    public void Begin(IRoomContext ctx, int seats)
    {
        var crew = Crew(ctx, seats);
        if (crew == _crew) return;
        _crew = crew;
        _wins.Clear();
        _draws = 0;
        _games = 0;
    }

    /// <summary>Партію дограно: порожній winners — нічия.</summary>
    public void Record(IRoomContext ctx, IReadOnlyCollection<int> winners)
    {
        _games++;
        if (winners.Count == 0) { _draws++; return; }
        foreach (var seat in winners)
            if (ctx.NickOf(seat) is { } nick) _wins[Key(nick)] = _wins.GetValueOrDefault(Key(nick)) + 1;
    }

    /// <summary>
    /// Для виду: перемоги кожного місця в поточній розсадці й нічиї. null, поки серія не почалась — першу
    /// партію столу нема з чим порівнювати, і рядок «0:0» був би шумом.
    /// </summary>
    public object? View(IRoomContext ctx, int seats) => _games == 0 ? null : new
    {
        wins = Enumerable.Range(0, seats).Select(s => ctx.NickOf(s) is { } n ? _wins.GetValueOrDefault(Key(n)) : 0).ToArray(),
        draws = _draws,
        games = _games,
    };
}
