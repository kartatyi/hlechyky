using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games;

/// <summary>
/// Чи розгадав нік сьогоднішню головоломку дня (<see cref="IDailyGame"/>): у щоденну підглядати можна лише після своєї —
/// інакше це просто списати відповідь (і очки Січі). null — гра не щоденна, питати нема про що. Rooms бере сервіс із DI
/// ліниво (<c>Economy/DailyPeek.cs</c>), тести каркаса живуть і без нього.
/// </summary>
public interface IDailyPeek
{
    bool? Solved(string gameId, string nick);
}

/// <summary>
/// «👁 Підглянути» в чужу соло-гру (запит 29.10): той, хто саме грає (подія <c>solo</c>), видно в лобі, і на його гру можна
/// дивитись збоку — вид глядача, як за звичайним столом. Підглядача ніщо не садить: ходи сервер і так приймає лише від
/// того, хто сидить. З'єднання підглядачів лежать і у <see cref="Room.Watchers"/> (туди летять види й кадри), і в
/// <see cref="Room.Peeks"/> — щоб «ніхто не дивиться» (<see cref="Game.Unwatched"/>) і прибирання соло рахували лише
/// вкладки самого гравця: коло, на яке дивиться лише сусід, не має думати, що гончар біля нього.
/// </summary>
public sealed partial class Rooms
{
    IDailyPeek? DailyPeek => _services.GetService<IDailyPeek>();

    /// <summary>Чому <paramref name="nick"/> не можна підглянути в кімнату <paramref name="id"/>; null — можна (своя теж «можна»).</summary>
    public string? PeekRefusal(string id, string nick) => Find(id) is { } room ? PeekRefusal(room, nick) : "Ця гра вже скінчилась";

    string? PeekRefusal(Room room, string nick)
    {
        if (!Named(nick)) return Say.NoNick;
        if (!room.Info.Solo) return Say.NoRoom;
        string? owner;
        lock (room.Sync) owner = room.Seats[0];
        if (owner is null) return "Ця гра вже скінчилась";
        if (string.Equals(owner, nick, StringComparison.OrdinalIgnoreCase)) return null;
        // Лише поки людина справді грає (Focus): кімната живе ще пів години після неї, але дивитись там уже нема на кого.
        if (room.OnScreen.IsEmpty) return owner + " уже не грає";
        if (DailyPeek?.Solved(room.Info.Id, nick) == false)
            return "Це щоденна — спершу розгадай свою, а тоді підглядай";
        return null;
    }

    /// <summary>
    /// Хто підглядає (ніки без повторів: дві вкладки — одна людина) — для шапки гравця «👁 Оля»; null — ніхто. Спершу
    /// впорядковуємо, щоб із двох написань одного ніка завжди лишалось те саме.
    /// </summary>
    internal static IReadOnlyList<string>? PeekNicks(Room room) => room.Peeks.IsEmpty ? null
        : [.. room.Peeks.Values.Order(StringComparer.Ordinal).DistinctBy(NickKey).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Вкладки самого гравця на кімнату вже не дивляться — лишились самі підглядачі (або нікого).</summary>
    static bool OwnerGone(Room room) => room.Watchers.Keys.All(room.Peeks.ContainsKey);

    /// <summary>
    /// З'єднання відписалось від кімнати. Підглядач пішов — гравцеві новий вид (у шапці вже без нього); пішла вкладка
    /// гравця — може, вже нікому дивитись (<see cref="NoWatchers"/>).
    /// </summary>
    void Unsubscribed(Room room, string connId, Outbox outbox)
    {
        if (room.Peeks.TryRemove(connId, out _)) outbox.Add(new RoomViews(room.Id));
        else NoWatchers(room);
    }
}
