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
    /// </summary>
    RoomReply? Unready(Room room, string nick, bool force, Outbox outbox)
    {
        if (room.Info.Solo) return null;
        var who = room.Seats.FirstOrDefault(s => string.Equals(s, nick, StringComparison.OrdinalIgnoreCase));
        var marked = who is not null && room.Ready.Add(who);
        var wait = force ? [] : room.NotReady(nick);
        if (wait.Count == 0) return null;   // старт і так розішле все, а готовність скине StartRound
        if (marked)
        {
            outbox.Add(new LobbyChanged());
            outbox.Add(new RoomViews(room.Id));
        }
        return new RoomReply(false, Say.NotReady(wait), room.Id, wait);
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
