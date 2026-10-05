using System.Collections.Concurrent;

namespace Hlechyky.Games;

/// <summary>
/// Місце в кімнаті так, як його бачить лобі: індекс і хто на ньому сидить. <c>Bot</c> — ім'я бота гри на місці без
/// людини (<see cref="Game.SeatBot"/>); на дріт іде лише тоді, коли є.
/// </summary>
public sealed record SeatSlot(int I, string? Nick,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Bot = null);

/// <summary>Підсумок партії для лобі й для картки кімнати. Scores лишаються всередині — лобі вони ні до чого.</summary>
public sealed record RoomResultDto(int[] Winners, bool Draw, string Text,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Verdict = null);

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
    DateTimeOffset? FinishedAt,
    EveningDto? Evening = null);

/// <summary>
/// «Рахунок вечора» (п. 225): скільки партій дограли за цим столом за всі «Ану ще раз» і хто скільки взяв.
/// <c>Points</c> — сума очок, які гра віддала в <c>Finish(scores)</c>; null, якщо гра очок не рахує.
/// </summary>
public sealed record EveningDto(int Games, IReadOnlyList<EveningRowDto> Rows);

public sealed record EveningRowDto(string Nick, int Wins, int Games, long? Points);

/// <summary>Рядок вечора в кімнаті: живе, поки живе стіл; ключ — нік без огляду на регістр.</summary>
public sealed class EveningRow
{
    public required string Nick { get; set; }
    public int Wins { get; set; }
    public int Games { get; set; }
    public long Points { get; set; }
    public bool HasPoints { get; set; }
    public int Order { get; init; }
}

/// <summary>
/// Види дограного столу, збережені перед перезапуском сервера: місце → вид (уже JSON, тим самим форматом, що летить
/// браузерам), плюс вид глядача. Місця без свого виду (сів новенький) бачать вид глядача.
/// </summary>
public sealed class RestoredViews(IReadOnlyDictionary<int, System.Text.Json.JsonElement> seats, System.Text.Json.JsonElement? watcher)
{
    public IReadOnlyDictionary<int, System.Text.Json.JsonElement> Seats => seats;
    public System.Text.Json.JsonElement? Watcher => watcher;

    public object? View(int? seat) => seat is { } s && seats.TryGetValue(s, out var v) ? v : watcher;
}

/// <summary>Те, що летить подією <c>room</c>: шапка кімнати, моє місце (null — глядач) і вид цього місця.</summary>
public sealed record RoomView(RoomSummary Room, int? Seat, object? View);

/// <summary>
/// Рядок події <c>solo</c>: хто зараз у своїй соло-грі. Приватні кімнати в лобі не потрапляють, тож без цього
/// ніхто б і не знав, що Оля саме крутить Гончарне коло.
/// </summary>
public sealed record SoloPlayer(string Game, string Nick);

/// <summary>
/// Одна партія: гра, місця, статус, глядачі. Усе, що міняє стан кімнати, робиться під <see cref="Sync"/>;
/// розсилка збирається в Outbox і йде вже поза замком. Кімната живе в пам'яті, а перезапуск сервера переживає
/// знімком (<see cref="Rooms.Freeze"/> → <see cref="Rooms.Restore"/>, ARCHITECTURE §4.7).
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
    /// <summary>
    /// Вкладки власника, у яких ця соло-кімната зараз на екрані (<see cref="Rooms.Focus"/>). Не те саме, що
    /// <see cref="Watchers"/>: на свою кімнату браузер підписаний і з лобі, а тут — лише поки людина справді в грі.
    /// </summary>
    public ConcurrentDictionary<string, byte> OnScreen { get; } = new();
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
    /// <summary>Склад, про який востаннє написали в Журнал «сідають грати». null — ще не писали жодного разу.</summary>
    public string?[]? LoggedSeats { get; set; }
    /// <summary>
    /// Коли стіл востаннє кликав усіх — при створенні чи «Покликати ще раз». Частіше за <see cref="Calls.AgainGap"/>
    /// стіл усіх не кличе. null — ще не кликав (стіл стартував одразу).
    /// </summary>
    public DateTimeOffset? CalledAt { get; set; }
    /// <summary>
    /// Балачка столу: останні <see cref="Rooms.TalkLines"/> реплік гравців, глядачів і Глека-ведучого. Під
    /// <see cref="Sync"/>; живе й помирає разом зі столом, як і сама партія.
    /// </summary>
    public List<TableLine> Talk { get; } = [];

    /// <summary>Рахунок вечора: партії, дограні за цим столом (зі «Ану ще раз» і новими гостями). Лише під <see cref="Sync"/>.</summary>
    public Dictionary<string, EveningRow> Evening { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int EveningGames { get; set; }

    /// <summary>Коли нік востаннє кидав реакцію-емодзі (квота Rooms.ReactGapMs). Лише під <see cref="Sync"/>.</summary>
    public Dictionary<string, DateTimeOffset> ReactAt { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Види столу, яким він був до перезапуску сервера (<see cref="Rooms.Restore"/>): партію дограно чи перервано, а гра
    /// в цьому процесі новенька й тієї дошки не знає. Поки стіл не почав нову партію, браузерам летять саме вони —
    /// люди бачать той самий підсумок, що й до перезапуску. Лише під <see cref="Sync"/>.
    /// </summary>
    public RestoredViews? Restored { get; set; }

    /// <summary>Записати дограну партію у вечір (кличе RoomContext.Finish під замком). Соло сюди не йде.</summary>
    public void TallyEvening(int[] winners, IReadOnlyDictionary<int, long>? scores)
    {
        if (Info.Solo) return;
        EveningGames++;
        var points = scores is { Count: > 0 } && Info.Score != ScoreOrder.LowerIsBetter;
        for (var i = 0; i < Seats.Length; i++)
        {
            if (Seats[i] is not { } nick) continue;
            if (!Evening.TryGetValue(nick, out var row))
                Evening[nick] = row = new EveningRow { Nick = nick, Order = Evening.Count };
            row.Nick = nick;
            row.Games++;
            if (Array.IndexOf(winners, i) >= 0) row.Wins++;
            if (points && scores!.TryGetValue(i, out var v)) { row.Points += v; row.HasPoints = true; }
        }
    }

    EveningDto? EveningSummary()
    {
        if (EveningGames == 0 || Evening.Count == 0) return null;
        var rows = Evening.Values
            .OrderByDescending(r => r.Wins).ThenByDescending(r => r.HasPoints ? r.Points : 0).ThenBy(r => r.Order)
            .Select(r => new EveningRowDto(r.Nick, r.Wins, r.Games, r.HasPoints ? r.Points : null))
            .ToArray();
        return new EveningDto(EveningGames, rows);
    }
    /// <summary>Чи є в цього столу своя балачка: у соло й приватній кімнаті говорити нема з ким.</summary>
    public bool Talks => !Info.Solo && !Info.Private;

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

    /// <summary>Бот гри на порожньому місці; крива гра не валить шапку (як <see cref="SafeSeatName"/>).</summary>
    public string? SafeSeatBot(int seat)
    {
        try { return Game.SeatBot(seat); }
        catch { return null; }
    }

    /// <summary>Шапка кімнати для дроту. Кличеться під <see cref="Sync"/>.</summary>
    public RoomSummary Summary()
    {
        var slots = new SeatSlot[Seats.Length];
        var names = new string[Seats.Length];
        for (var i = 0; i < Seats.Length; i++)
        {
            slots[i] = new SeatSlot(i, Seats[i], Seats[i] is null && Status != RoomStatus.Lobby ? SafeSeatBot(i) : null);
            names[i] = SafeSeatName(i);
        }
        return new RoomSummary(
            Id, Info.Id, Status.ToString().ToLowerInvariant(), slots, names, Host,
            Info.MinPlayers, Info.MaxPlayers, Options, Stake, Round, Watchers.Count,
            Result is { } r ? new RoomResultDto(r.Winners, r.Draw, r.Text, r.Verdict) : null,
            CreatedAt, StartedAt, FinishedAt, EveningSummary());
    }
}
