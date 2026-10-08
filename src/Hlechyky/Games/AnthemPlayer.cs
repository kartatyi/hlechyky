namespace Hlechyky.Games;

/// <summary>
/// Гімн переможця (docs/games/specs/anthem.md §2): слухає кінець партії й, якщо виграла людина з гімном, кидає
/// <see cref="Anthem"/> у розсилку — він звучить у всіх, хто за цим столом. В ефір радіо гімн не йде: за вечір
/// закінчуються десятки партій, і фанфари після кожної набридли б слухачам.
/// <para>
/// Соло, кооператив, нічия й партія без переможців — тиша. Перемога над ботом рахується: черепків тут нема, а радість є.
/// Кілька переможців-людей із гімнами (мафія, команди, поділений банк) — по колу між раундами: <c>Round % N</c>.
/// Одна партія — щонайбільше один гімн: ключ (стіл, раунд) пам'ятається.
/// </para>
/// </summary>
public sealed class AnthemPlayer(GameEvents events, Lavka lavka, IOutbox outbox, ILogger<AnthemPlayer> log) : IHostedService
{
    /// <summary>Скільки останніх партій пам'ятати: подія приходить одразу, тож повтор буває лише зовсім свіжий.</summary>
    const int Memory = 500;

    readonly object _gate = new();
    readonly HashSet<(string RoomId, int Round)> _played = [];
    readonly Queue<(string RoomId, int Round)> _order = new();

    public Task StartAsync(CancellationToken ct) { events.RoomFinished += On; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { events.RoomFinished -= On; return Task.CompletedTask; }

    /// <summary>Підписник шини: винятків назовні не кидає — гімн не вартий зламаної партії.</summary>
    public void On(RoomFinishedEvent e)
    {
        try { Play(e); }
        catch (Exception ex) { log.LogWarning(ex, "гімн за столом {Room} (раунд {Round}) не зазвучав", e.RoomId, e.Round); }
    }

    void Play(RoomFinishedEvent e)
    {
        if (e.Info.Solo || e.Info.Coop || e.Result.Draw || e.Result.Winners.Length == 0) return;
        if (!Remember(e.RoomId, e.Round)) return;

        var candidates = new List<(string Nick, AnthemPlay Play)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // в порядку місць: так «чия черга» між раундами однакова на кожному сервері й у тестах
        foreach (var seat in e.Result.Winners.Distinct().Order())
        {
            if (seat < 0 || seat >= e.Seats.Count || e.Seats[seat] is not { } nick || !Rooms.Named(nick)) continue;   // бот чи порожнє місце
            if (!seen.Add(Auth.NickKey(nick))) continue;
            if (lavka.AnthemOf(nick) is { } play) candidates.Add((nick, play));
        }
        if (candidates.Count == 0) return;

        var (who, anthem) = candidates[(e.Round % candidates.Count + candidates.Count) % candidates.Count];
        outbox.Post(new Anthem(e.RoomId, e.Round, who, anthem.Title, anthem.Emoji, anthem.Url));
    }

    /// <summary>Запам'ятати партію; false — вона вже була (другий виклик тієї самої події).</summary>
    bool Remember(string roomId, int round)
    {
        lock (_gate)
        {
            if (!_played.Add((roomId, round))) return false;
            _order.Enqueue((roomId, round));
            while (_order.Count > Memory) _played.Remove(_order.Dequeue());
            return true;
        }
    }
}
