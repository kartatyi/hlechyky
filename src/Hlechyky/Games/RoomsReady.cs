namespace Hlechyky.Games;

/// <summary>
/// «✋ Готовий» (bets-contract §5): кожен, хто сидить, може сказати «я готовий» у лобі столу чи після партії, і
/// «Почати» / «Ще раз» без force спершу питає, чи готові решта. Боти готові завжди (на місці бота ніка нема — нема кого
/// й питати). Готовність скидається для всіх, щойно стіл став іншим: хтось сів чи встав, «🤖 + бот», нові налаштування
/// (<see cref="CrewChanged"/>), і на старті партії (<see cref="StartRound"/>).
/// </summary>
public sealed partial class Rooms
{
    const string ReadyNoOne = "Тут готуватись нема з ким";
    const string ReadyMidGame = "Партія вже йде";

    /// <summary>
    /// Поставити чи зняти свою готовність. Бажаний стан приходить явно (а не «перемкнути»), щоб подвійний клік чи
    /// повтор після реконекту не вимикали щойно ввімкнене.
    /// </summary>
    public RoomOutcome SetReady(string id, string nick, bool on)
    {
        if (!Named(nick)) return RoomOutcome.Fail(Say.NoNick);
        if (Find(id) is not { } room) return RoomOutcome.Fail(Say.NoRoom);
        if (room.Info.Solo || room.Info.Private) return RoomOutcome.Fail(ReadyNoOne);
        var outbox = new Outbox();
        lock (room.Sync)
        {
            if (_frozen) return RoomOutcome.Fail(Say.Restarting);
            if (!room.Has(nick)) return RoomOutcome.Fail(Say.NotPlaying);
            if (room.Status == RoomStatus.Playing) return RoomOutcome.Fail(ReadyMidGame);
            Seen(room, nick);
            var changed = on ? room.Ready.Add(nick) : room.Ready.Remove(nick);
            if (changed)
            {
                room.LastActivity = _clock.UtcNow;
                // Лобі теж: шапку відкритого столу браузер бере і з події 'rooms', тож хай і там буде свіже ✋.
                outbox.Add(new LobbyChanged());
                outbox.Add(new RoomViews(room.Id));
            }
        }
        outbox.RunAfter(_log);
        return new RoomOutcome(outbox, new RoomReply(true, "", room.Id));
    }

    /// <summary>
    /// Перевірка перед «Почати» / «Ще раз» під замком кімнати. Той, хто тисне, готовий сам (і решта це побачить, навіть
    /// якщо стартувати ще не можна). null — можна починати; інакше відмова зі списком не готових.
    /// <para>
    /// Хто зараз не на зв'язку (<see cref="Room.Away"/> — у відліку Grace), той не готовий, навіть якщо казав «✋»: інакше
    /// «Ану ще раз» з тим, хто закрив вкладку, давало фальстарт — партія за кілька секунд кінчалась його «встає з-за столу»
    /// (аудит столів №8). «Почати все одно» від людини (<paramref name="dropAway"/>) спершу звільняє їхні місця — у лобі й
    /// між партіями це не техпоразка. Турнір (force без dropAway) нікого не знімає: він сам усіх посадив.
    /// </para>
    /// </summary>
    RoomReply? Unready(Room room, string nick, bool force, Outbox outbox, bool dropAway = false)
    {
        if (room.Info.Solo) return null;
        var away = room.Seats
            .Where(s => s is not null && room.Away.ContainsKey(s) && !string.Equals(s, nick, StringComparison.OrdinalIgnoreCase))
            .Select(s => s!).ToList();
        if (force && dropAway && away.Count > 0)
        {
            foreach (var gone in away)
                if (room.SeatOf(gone) is { } seat) Vacate(room, seat, outbox);
            away.Clear();
            if (room.Occupied < room.Info.MinPlayers) return new RoomReply(false, Say.TooFew(room.Info.MinPlayers), room.Id);
        }
        var who = room.Seats.FirstOrDefault(s => string.Equals(s, nick, StringComparison.OrdinalIgnoreCase));
        var marked = who is not null && room.Ready.Add(who);
        var waitReady = force ? [] : room.NotReady(nick).Where(n => !away.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (force) away.Clear();
        if (waitReady.Count == 0 && away.Count == 0) return null;   // старт і так розішле все, а готовність скине StartRound
        if (marked)
        {
            outbox.Add(new LobbyChanged());
            outbox.Add(new RoomViews(room.Id));
        }
        var text = away.Count == 0 ? Say.NotReady(waitReady)
            : waitReady.Count == 0 ? Say.Away(away)
            : Say.NotReady(waitReady) + ". " + Say.Away(away);
        return new RoomReply(false, text, room.Id, [.. waitReady, .. away]);
    }

    /// <summary>
    /// Стіл став іншим, ніж той, до якого казали «готовий»: хтось сів чи встав, «🤖 + бот», нові налаштування. Кличеться
    /// під замком кімнати. Розсилку робить той, хто кличе (RoomViews/LobbyChanged він і так шле); outbox — для ставок
    /// на столі (bets-contract §4.1): відкриті ставки на наступну партію повертаються. Посеред партії її ставки не
    /// чіпаємо — хто встав, той програв; а ставок на наступну тоді ще й нема.
    /// </summary>
    void CrewChanged(Room room, Outbox outbox)
    {
        room.Ready.Clear();
        if (room.Status != RoomStatus.Playing)
            BetsCrewChanged(room, outbox, room.Status == RoomStatus.Finished ? room.Round + 1 : room.Round);
    }
}
