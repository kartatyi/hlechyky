namespace Hlechyky.Games;

/// <summary>
/// Гімн переможця (docs/games/specs/anthem.md §2), святкування й прокльон (docs/games/specs/flair.md §1.3, §3): слухає кінець
/// партії й кидає в розсилку <see cref="Anthem"/> (гімн і/або святкування переможця) та <see cref="Curse"/> (прокльон того,
/// хто програв) — їх чують і бачать усі, хто за цим столом. В ефір радіо нічого з цього не йде: за вечір закінчуються
/// десятки партій, і фанфари після кожної набридли б слухачам.
/// <para>
/// Соло, кооператив, нічия й партія без переможців — тиша. Перемога над ботом рахується: черепків тут нема, а радість є.
/// Кілька переможців-людей із гімнами чи святкуваннями (мафія, команди, поділений банк) — по колу між раундами:
/// <c>Round % N</c>. Прокльон — лише коли за столом хоча б двоє різних людей (інакше його розряджали б, програючи ботові
/// на самоті); кілька проклятих серед тих, хто програв, — так само <c>Round % N</c>. Одна партія — щонайбільше один гімн
/// і один прокльон: ключ (стіл, раунд) пам'ятається.
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

    /// <summary>Підписник шини: винятків назовні не кидає — гімн чи прокльон не варті зламаної партії.</summary>
    public void On(RoomFinishedEvent e)
    {
        if (e.Info.Solo || e.Info.Coop || e.Result.Draw || e.Result.Winners.Length == 0) return;
        try { if (!Remember(e.RoomId, e.Round)) return; }
        catch (Exception ex) { log.LogWarning(ex, "партія {Room} (раунд {Round}) без гімну й прокльону", e.RoomId, e.Round); return; }
        // Гімн і прокльон — окремо: зламане одне не має забрати друге
        try { Play(e); }
        catch (Exception ex) { log.LogWarning(ex, "гімн за столом {Room} (раунд {Round}) не зазвучав", e.RoomId, e.Round); }
        try { CurseLoser(e); }
        catch (Exception ex) { log.LogWarning(ex, "прокльон за столом {Room} (раунд {Round}) не зазвучав", e.RoomId, e.Round); }
    }

    /// <summary>Люди за столом у порядку місць: нік, місце, ключ. Боти й порожні місця — поза списком.</summary>
    static IEnumerable<(string Nick, int Seat, string Key)> People(RoomFinishedEvent e)
    {
        for (var seat = 0; seat < e.Seats.Count; seat++)
            if (e.Seats[seat] is { } nick && Rooms.Named(nick)) yield return (nick, seat, Auth.NickKey(nick));
    }

    void Play(RoomFinishedEvent e)
    {
        var candidates = new List<(string Nick, AnthemPlay? Play, string? Fx)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // в порядку місць: так «чия черга» між раундами однакова на кожному сервері й у тестах
        foreach (var seat in e.Result.Winners.Distinct().Order())
        {
            if (seat < 0 || seat >= e.Seats.Count || e.Seats[seat] is not { } nick || !Rooms.Named(nick)) continue;   // бот чи порожнє місце
            if (!seen.Add(Auth.NickKey(nick))) continue;
            var play = lavka.AnthemOf(nick);
            var fx = lavka.FxOf(nick);
            if (play is not null || fx is not null) candidates.Add((nick, play, fx));
        }
        if (candidates.Count == 0) return;

        var (who, anthem, flair) = Pick(candidates, e.Round);
        outbox.Post(new Anthem(e.RoomId, e.Round, who, anthem?.Title, anthem?.Emoji, anthem?.Url, flair, anthem?.Len));
    }

    void CurseLoser(RoomFinishedEvent e)
    {
        var people = People(e).ToList();
        if (people.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() < 2) return;   // сам на сам із ботом — не рахується

        var winners = e.Result.Winners.Where(w => w >= 0 && w < e.Seats.Count).ToHashSet();
        var won = people.Where(p => winners.Contains(p.Seat)).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<string>();
        foreach (var p in people)
            if (!won.Contains(p.Key) && seen.Add(p.Key) && lavka.Cursed(p.Nick)) candidates.Add(p.Nick);
        if (candidates.Count == 0) return;

        // Черга — з того, на кого випало, далі по колу: прокльон міг розрядитись чи зникнути між Cursed і HitCurse
        // (відкуп, рядок, за який так і не заплатили), і тоді звучить прокльон наступного, а не тиша.
        var from = Turn(candidates.Count, e.Round);
        for (var i = 0; i < candidates.Count; i++)
        {
            var loser = candidates[(from + i) % candidates.Count];
            if (lavka.HitCurse(loser) is not { } hit) continue;
            outbox.Post(new Curse(e.RoomId, e.Round, loser, hit.Title, hit.Emoji, hit.Url, hit.Left));
            return;
        }
    }

    /// <summary>Чия черга: <c>кандидати[Round % N]</c> (і від'ємний раунд не ламає).</summary>
    static T Pick<T>(List<T> candidates, int round) => candidates[Turn(candidates.Count, round)];
    static int Turn(int count, int round) => (round % count + count) % count;

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
