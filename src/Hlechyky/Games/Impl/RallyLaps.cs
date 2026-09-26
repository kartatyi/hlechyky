using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hlechyky.Games.Impl;

/// <summary>Одне коло в рекордах траси: нік, мілісекунди, машина, коли.</summary>
public sealed record RallyLap(
    [property: JsonPropertyName("n")] string Nick,
    [property: JsonPropertyName("ms")] int Ms,
    [property: JsonPropertyName("c")] string Car,
    [property: JsonPropertyName("at")] DateTimeOffset At);

/// <summary>
/// Рекорди кіл «Сільського ралі» (spec §2.6): на кожну трасу до двадцяти найкращих кіл, одне на нік.
/// Живе один на сервер; читає сховище раз у конструкторі (поза замками кімнат), пише — з пулу потоків,
/// ніколи з-під замка кімнати: гра кличе <see cref="Post"/> з тика, і там лише словник у пам'яті.
/// </summary>
public sealed class RallyLaps
{
    public const int Keep = 20;
    public static string Key(string track) => $"rally:laps:{track}";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly IGameStore _store;
    readonly Action<Action> _defer;
    readonly object _lock = new();
    readonly object _writeLock = new();
    readonly Dictionary<string, List<RallyLap>> _tracks = new(StringComparer.Ordinal);

    /// <param name="defer">Як виконати запис у сховище: типово — пул потоків; тести передають «одразу».</param>
    public RallyLaps(IGameStore store, Action<Action>? defer = null)
    {
        _store = store;
        _defer = defer ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
        foreach (var id in RallyTracks.Ids) _tracks[id] = Load(id);
    }

    List<RallyLap> Load(string track)
    {
        try
        {
            var json = _store.LoadState(Key(track));
            if (string.IsNullOrWhiteSpace(json)) return [];
            var list = JsonSerializer.Deserialize<List<RallyLap>>(json, Json) ?? [];
            // старі чи криві записи не валять гру: чистимо й сортуємо, як нові
            var clean = list.Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Nick) && l.Ms > 0)
                .GroupBy(l => l.Nick, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(l => l.Ms).ThenBy(l => l.At).First())
                .OrderBy(l => l.Ms).ThenBy(l => l.At)
                .Take(Keep)
                .ToList();
            return clean;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Нове коло. Rank — місце в двадцятці (0 — не ввійшло або не краще за своє старе), BeatOther — став першим,
    /// а перше місце до того тримав інший нік.
    /// </summary>
    public (int Rank, bool BeatOther) Post(string track, string nick, string car, int ms, DateTimeOffset at)
    {
        if (ms <= 0 || string.IsNullOrWhiteSpace(nick)) return (0, false);
        int rank;
        bool beat;
        lock (_lock)
        {
            if (!_tracks.TryGetValue(track, out var list)) return (0, false);
            var top = list.Count > 0 ? list[0] : null;
            var mine = list.FindIndex(l => string.Equals(l.Nick, nick, StringComparison.OrdinalIgnoreCase));
            if (mine >= 0)
            {
                if (ms >= list[mine].Ms) return (0, false);
                list.RemoveAt(mine);
            }
            // при рівних мс старіший запис вищий: нове коло стає після всіх, хто не гірший
            var at_ = 0;
            while (at_ < list.Count && list[at_].Ms <= ms) at_++;
            if (at_ >= Keep) return (0, false);
            list.Insert(at_, new RallyLap(nick.Trim(), ms, car, at));
            if (list.Count > Keep) list.RemoveRange(Keep, list.Count - Keep);
            rank = at_ + 1;
            beat = rank == 1 && top is not null && !string.Equals(top.Nick, nick.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        _defer(() => Flush(track));
        return (rank, beat);
    }

    /// <summary>Перші n кіл траси (копія).</summary>
    public IReadOnlyList<RallyLap> Top(string track, int n = 10)
    {
        lock (_lock)
            return _tracks.TryGetValue(track, out var list) ? [.. list.Take(n)] : [];
    }

    /// <summary>Запис у сховище: знімок береться вже під замком запису, тож останній запис — завжди найсвіжіший.</summary>
    void Flush(string track)
    {
        lock (_writeLock)
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_tracks[track], Json);
            try { _store.SaveState(Key(track), json); }
            catch (Exception) { /* база впала — наступне коло спробує ще раз */ }
        }
    }
}

/// <summary>Підключення «Сільського ралі»: сервіс рекордів кіл. Кличеться одним рядком із <see cref="GamesSetup"/>.</summary>
public static class RallySetup
{
    public static IServiceCollection AddRally(IServiceCollection services)
    {
        services.AddSingleton(sp => new RallyLaps(sp.GetRequiredService<IGameStore>()));
        return services;
    }
}
