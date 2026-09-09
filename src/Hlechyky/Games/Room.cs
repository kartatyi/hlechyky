using System.Collections.Concurrent;

namespace Hlechyky.Games;

/// <summary>Місце в кімнаті так, як його бачить лобі: індекс і хто на ньому сидить.</summary>
public sealed record SeatSlot(int I, string? Nick);

/// <summary>Підсумок партії для лобі й для картки кімнати. Scores лишаються всередині — лобі вони ні до чого.</summary>
public sealed record RoomResultDto(int[] Winners, bool Draw, string Text);

/// <summary>
/// Кімната так, як її бачить браузер (PROTOCOL §2). Будується під замком кімнати і летить у лобі та в
/// кожен <see cref="RoomView"/>: клієнт малює шапку картки виключно з цих полів.
/// </summary>
public sealed record RoomSummary(
    string Id,
    string Game,
    string Status,
    IReadOnlyList<SeatSlot> Seats,
    IReadOnlyList<string> SeatNames,
    string Host,
    int MinPlayers,
    int MaxPlayers,
    IReadOnlyDictionary<string, string> Options,
    int Stake,
    int Round,
    int Watchers,
    RoomResultDto? Result,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>Те, що летить подією <c>room</c>: шапка кімнати, моє місце (null — глядач) і вид цього місця.</summary>
public sealed record RoomView(RoomSummary Room, int? Seat, object? View);

/// <summary>
/// Одна партія: гра, місця, статус, глядачі. Усе, що міняє стан кімнати, робиться під <see cref="Sync"/>;
/// розсилка збирається в Outbox і йде вже поза замком. Кімната живе в пам'яті: партія — це п'ять хвилин
/// на перекур, а не те, що варто переживати рестарт (див. ARCHITECTURE §4.4, §4.7).
/// </summary>
public sealed class Room
{
    /// <summary>8 hex — рівно стільки, щоб не збігтись, і достатньо мало, щоб влізти в URL і в лог.</summary>
    public required string Id { get; init; }
    public required GameInfo Info { get; init; }
    public required Game Game { get; init; }
    /// <summary>Нік на кожному місці; null — місце вільне. Довжина завжди <see cref="GameInfo.MaxPlayers"/>.</summary>
    public required string?[] Seats { get; init; }
    /// <summary>Хто створив кімнату (або найстарше зайняте місце, якщо той пішов).</summary>
    public string Host { get; set; } = "";
    public RoomStatus Status { get; set; } = RoomStatus.Lobby;
    /// <summary>Опції, з якими кімнату створили (уже перевірені й доповнені типовими значеннями). Ставки тут нема.</summary>
    public required IReadOnlyDictionary<string, string> Options { get; init; }
    /// <summary>Черепків з кожного гравця; 0 — граємо просто так.</summary>
    public int Stake { get; set; }
    /// <summary>1 для першої партії, +1 на кожен «Ще раз».</summary>
    public int Round { get; set; } = 1;
    public RoomResult? Result { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>З'єднання, які зараз дивляться на цю кімнату. Ключ — connectionId, значення нікого не цікавить.</summary>
    public ConcurrentDictionary<string, byte> Watchers { get; } = new();
    /// <summary>Замок кімнати: хід, тик, вхід, вихід — усе під ним. Await під ним не буває.</summary>
    public object Sync { get; } = new();
    public required int Seed { get; init; }
    /// <summary>Ключ особистої кімнати для Persistent/Private ігор («daily:wordle:2026-09-09:оля»); null для звичайних.</summary>
    public string? Key { get; init; }
    /// <summary>Коли цій кімнаті наступного разу тикати; має значення лише для <see cref="GameInfo.RealTime"/>.</summary>
    public DateTimeOffset NextTickAt { get; set; }
    /// <summary>Скільки ходів (успішних Act) прийнято за поточну партію.</summary>
    public int Moves { get; set; }
    /// <summary>Останній рух у кімнаті — за ним прибирання розуміє, що тут уже нікого нема.</summary>
    public DateTimeOffset LastActivity { get; set; }
    /// <summary>З кого цього раунду списано ставку — щоб виплата й повернення знали, кому й скільки.</summary>
    public List<string> Charged { get; } = [];
    /// <summary>Склад, про який востаннє написали в Журнал «сіли грати». null — ще не писали жодного разу.</summary>
    public string?[]? LoggedSeats { get; set; }

    public int? SeatOf(string? nick)
    {
        if (string.IsNullOrEmpty(nick)) return null;
        for (var i = 0; i < Seats.Length; i++)
            if (string.Equals(Seats[i], nick, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    public bool Has(string? nick) => SeatOf(nick) is not null;

    public int Occupied => Seats.Count(s => s is not null);

    public bool Full => Occupied >= Info.MaxPlayers;

    /// <summary>Перше вільне місце або -1.</summary>
    public int FreeSeat => Array.FindIndex(Seats, s => s is null);

    /// <summary>
    /// Назва місця від гри, але так, щоб крива гра не завалила лобі: одна помилка в одному з двадцяти
    /// класів не має коштувати сайту ні знімка кімнат, ні розсилки (тому ж — SafeView у Rooms).
    /// </summary>
    public string SafeSeatName(int seat)
    {
        try { return Game.SeatName(seat); }
        catch { return seat == 0 ? "перший" : seat == 1 ? "другий" : $"гравець {seat + 1}"; }
    }

    /// <summary>Шапка кімнати для дроту. Кличеться під <see cref="Sync"/>.</summary>
    public RoomSummary Summary()
    {
        var slots = new SeatSlot[Seats.Length];
        var names = new string[Seats.Length];
        for (var i = 0; i < Seats.Length; i++)
        {
            slots[i] = new SeatSlot(i, Seats[i]);
            names[i] = SafeSeatName(i);
        }
        return new RoomSummary(
            Id, Info.Id, Status.ToString().ToLowerInvariant(), slots, names, Host,
            Info.MinPlayers, Info.MaxPlayers, Options, Stake, Round, Watchers.Count,
            Result is { } r ? new RoomResultDto(r.Winners, r.Draw, r.Text) : null,
            CreatedAt, StartedAt, FinishedAt);
    }
}
