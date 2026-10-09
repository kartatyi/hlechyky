using Microsoft.Extensions.DependencyInjection;

namespace Hlechyky.Games;

/// <summary>
/// Ставки на столах (bets-contract §4) з боку каркаса: самі ставки, кефи й гроші живуть у <c>Bets/TableBets.cs</c>, а
/// тут — лише те, що треба знати про стіл під його замком, і сигнал «склад змінився». Сервіс ставок Rooms бере з DI
/// ліниво (<see cref="ITableBetsHook"/>): він сам залежить від Rooms, а тести каркаса живуть і без нього.
/// </summary>
public interface ITableBetsHook
{
    /// <summary>
    /// Стіл став іншим, ніж той, на який ставили (сів, встав, бот, налаштування): відкриті ставки на <paramref name="refKey"/>
    /// треба повернути. Кличеться вже поза замком кімнати (Outbox.After) — там база.
    /// </summary>
    void CrewChanged(string roomId, string refKey);
}

/// <summary>
/// Стіл очима ставок — знімок під замком кімнати. <see cref="Bots"/> — бот на вільному місці (відомо лише після партії:
/// у лобі гра ще не вирішила, де сидітимуть боти). <see cref="Crew"/> — підпис складу й налаштувань: ставку приймаємо,
/// лише якщо він не змінився, поки її записували. <see cref="BotCalled"/> — у лобі натиснуто «🤖 + бот»: хто сяде і чи
/// сяде взагалі, видно лише з першої партії, тож до неї ставок нема.
/// </summary>
public sealed record BetTable(string Id, GameInfo Info, RoomStatus Status, int Round, string?[] Seats, string?[] Bots,
    int? MySeat, bool Watching, DateTimeOffset? FinishedAt, string Crew, bool BotCalled = false)
{
    /// <summary>
    /// Партія, на яку зараз ставлять: у лобі — та, що почнеться (раунд не зміниться ні від «Почати», ні від повного столу),
    /// після партії — «Ще раз» (Rematch додає один; новий гравець чи «⚙ Налаштування» теж додають один і ставлять
    /// стіл у лобі, але це зміна складу — ставки однаково повертаються).
    /// </summary>
    public int NextRound => Status == RoomStatus.Finished ? Round + 1 : Round;

    public string NextRef => Rooms.BetRef(Id, NextRound);

    /// <summary>Скільки людей сидить (боти не рахуються: ставки — коли за столом хоча б двоє живих).</summary>
    public int Humans => Seats.Count(s => s is not null);
}

public sealed partial class Rooms
{
    /// <summary>Ref ставок однієї партії столу: <c>roomId:round</c>.</summary>
    public static string BetRef(string roomId, int round) => roomId + ":" + round;

    ITableBetsHook? BetsHook => _services.GetService<ITableBetsHook>();

    /// <summary>
    /// Стіл для ставок: хто де сидить, яка партія наступна, чи цей нік сидить, чи це з'єднання дивиться. null — столу
    /// нема, або він соло / приватний (там ставок не буває взагалі).
    /// </summary>
    public BetTable? BetTableOf(string id, string? nick = null, string? connId = null)
    {
        if (Find(id) is not { } room || room.Info.Solo || room.Info.Private) return null;
        lock (room.Sync)
        {
            var bots = new string?[room.Seats.Length];
            if (room.Status != RoomStatus.Lobby)
                for (var i = 0; i < bots.Length; i++) bots[i] = room.Seats[i] is null ? room.SafeSeatBot(i) : null;
            return new BetTable(room.Id, room.Info, room.Status, room.Round, (string?[])room.Seats.Clone(), bots,
                string.IsNullOrEmpty(nick) ? null : room.SeatOf(nick),
                connId is not null && room.Watchers.ContainsKey(connId), room.FinishedAt, CrewSign(room),
                room.Status == RoomStatus.Lobby && BotCalled(room));
        }
    }

    /// <summary>
    /// Чи кликали в лобі «🤖 + бот» і не прогнали. Гра тримає це в собі (SoloBot.Wanted) і назовні не каже, а каркас
    /// пам'ятає всі прийняті дії лобі (<see cref="Room.LobbyActs"/>) — проганяємо їх: <c>{on}</c> ставить, без нього — перемикає.
    /// </summary>
    static bool BotCalled(Room room)
    {
        var on = false;
        foreach (var a in room.LobbyActs)
        {
            if (a.Action != Impl.LiveBots.Toggle) continue;
            bool? set = null;
            if (a.Payload is { } raw)
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty("on", out var p))
                        set = p.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                catch (System.Text.Json.JsonException) { /* кривий payload гра теж прочитала як «перемкнути» */ }
            on = set ?? !on;
        }
        return on;
    }

    /// <summary>
    /// Склад (люди без огляду на місця — «Ще раз» їх обертає) і налаштування одним рядком. Ботів тут нема: у лобі їх
    /// не видно, а з'являються вони на старті — підпис мінявся б від самого «Почати».
    /// </summary>
    static string CrewSign(Room room)
    {
        var people = room.Seats.Where(s => s is not null).Select(s => NickKey(s!)).Order(StringComparer.Ordinal);
        var options = room.Options.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value);
        return string.Join(",", people) + "|" + string.Join("&", options);
    }

    /// <summary>
    /// Рядок Глека в балачку столу від сервісу (ставки): розіслати — справа того, хто кличе (IOutbox). Порожній Outbox —
    /// столу вже нема або балачки за ним не буває.
    /// </summary>
    public Outbox BetSay(string id, string text)
    {
        var outbox = new Outbox();
        if (string.IsNullOrWhiteSpace(text) || Find(id) is not { } room || !room.Talks) return outbox;
        lock (room.Sync) outbox.Add(new TableSaid(room.Id, AppendTalk(room, DjName, text.Trim(), "dj")));
        return outbox;
    }

    /// <summary>
    /// Ставки на партію <paramref name="round"/> цього столу ставились на інший склад — повернути (поза замком). Кличеться
    /// під замком кімнати з <see cref="CrewChanged"/> і з Join, коли повний стіл стартував сам.
    /// </summary>
    void BetsCrewChanged(Room room, Outbox outbox, int round)
    {
        if (room.Info.Solo || room.Info.Private || BetsHook is not { } hook) return;
        var id = room.Id;
        var refKey = BetRef(id, round);
        outbox.After(() => hook.CrewChanged(id, refKey));
    }
}
