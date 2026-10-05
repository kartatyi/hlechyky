namespace Hlechyky.Games;

/// <summary>
/// «⚙ Налаштування» між партіями: господар міняє опції столу (ті, що обирались у попапі «Поставити стіл») без
/// «встати й поставити новий». У чистому лобі — нові опції тій самій грі. За дограним столом (і за тим, що після партії
/// знову відкрився новачкові) гра вже пам'ятає минулу партію, тож стіл вертається в лобі з новою грою, налаштованою
/// наново, — як щойно поставлений: видно й налаштування самої гри (пакет «Своєї гри», «🤖 + бот»), а починає господар.
/// Склад, ставка, балачка й рахунок вечора лишаються; місця зсуваються, як у «Ану ще раз». Рахунок серії, що живе в
/// самій грі (шахи, шашки), починається з нуля — нові опції, нова серія.
/// </summary>
public sealed partial class Rooms
{
    const string NothingToSet = "У цієї гри налаштувань нема";
    const string HostSets = "Налаштування міняє господар столу";
    const string MidGame = "Посеред партії налаштування не міняють — дограйте";
    const string Unchanged = "Усе й так стоїть саме так";
    const string SetFailed = "Не вийшло — стіл лишився як був";

    /// <summary>
    /// Нові опції столу <paramref name="id"/>. <paramref name="options"/> — лише те, що міняємо (решта лишається, як
    /// була); перевіряються за паспортом гри, як у <see cref="Create"/>. Ставка тут не міняється.
    /// </summary>
    public RoomOutcome Reconfigure(string id, string nick, IReadOnlyDictionary<string, string>? options)
    {
        if (!Named(nick)) return RoomOutcome.Fail(Say.NoNick);
        if (Find(id) is not { } room || room.Info.Private) return RoomOutcome.Fail(Say.NoRoom);
        var info = room.Info;
        if (info.Solo || info.Options is not { Count: > 0 }) return RoomOutcome.Fail(NothingToSet);

        var outbox = new Outbox();
        bool fresh;
        lock (room.Sync)
        {
            if (!room.Has(nick)) return RoomOutcome.Fail(Say.NotPlaying);
            if (!string.Equals(room.Host, nick, StringComparison.OrdinalIgnoreCase)) return RoomOutcome.Fail(HostSets);
            if (room.Status == RoomStatus.Playing) return RoomOutcome.Fail(MidGame);

            var asked = new Dictionary<string, string>(room.Options, StringComparer.Ordinal);
            if (options is not null)
                foreach (var (key, value) in options) asked[key] = value;
            var next = Effective(info, asked);
            var changed = (info.Options ?? []).Where(o => room.Options.GetValueOrDefault(o.Key) != next[o.Key]).ToList();
            fresh = room.Status == RoomStatus.Finished || room.StartedAt is not null;
            if (!fresh && changed.Count == 0) return new RoomOutcome(outbox, new RoomReply(true, Unchanged, room.Id));

            if ((fresh ? Renew(room, next) : Retune(room, next)) is { } failed) return RoomOutcome.Fail(failed);

            room.Options = next;
            room.LastActivity = _clock.UtcNow;
            outbox.Add(new TableSaid(room.Id, AppendTalk(room, DjName, Line(nick, changed, next, fresh), "dj")));
        }
        outbox.Add(new LobbyChanged());
        outbox.Add(new RoomViews(room.Id));
        outbox.RunAfter(_log);
        return new RoomOutcome(outbox, new RoomReply(true,
            fresh ? "Стіл знову в лобі — тисни «Почати», коли всі готові" : "Готово", room.Id));
    }

    /// <summary>Чисте лобі: ті самі гра й вибір у лобі (пакет, бот), лише нові опції. Під замком кімнати.</summary>
    string? Retune(Room room, IReadOnlyDictionary<string, string> next)
    {
        try
        {
            room.Game.Configure(next);
            return null;
        }
        catch (Exception ex)
        {
            // Configure міг устигнути переписати половину полів — повертаємо грі старі опції.
            try { room.Game.Configure(room.Options); }
            catch (Exception again) { _log.LogWarning(again, "гра {Game} не повернулась до старих опцій", room.Info.Id); }
            if (ex is GameError) return ex.Message;
            _log.LogWarning(ex, "гра {Game} не змогла переналаштуватись", room.Info.Id);
            return SetFailed;
        }
    }

    /// <summary>
    /// Дограний стіл — у лобі з новою грою, як щойно поставлений. Нове зерно, щоб перша партія з новими опціями не
    /// роздала ту саму руку, що й найперша за цим столом. Під замком кімнати.
    /// </summary>
    string? Renew(Room room, IReadOnlyDictionary<string, string> next)
    {
        var seed = room.Seed;
        room.Seed = SeedOverride ?? unchecked((int)(_clock.UtcNow.Ticks ^ Interlocked.Increment(ref _seedCounter)));
        Game game;
        try
        {
            game = _registry.Create(room.Info.Id)!;
            game.Ctx = new RoomContext(room, this);
            game.Configure(next);
        }
        catch (Exception ex)
        {
            room.Seed = seed;
            if (ex is GameError) return ex.Message;
            _log.LogWarning(ex, "гра {Game} не змогла налаштуватись наново", room.Info.Id);
            return SetFailed;
        }

        room.Game = game;
        if (room.Status == RoomStatus.Finished)
        {
            room.Round++;   // щоб ключі ставок наступної партії не збіглися з минулою
            // Наступна партія — та сама «Ану ще раз», лише з іншими опціями: місця зсуваються так само, як у Rematch,
            // щоб починав інший, а не знову той, хто починав щойно.
            var was = (string?[])room.Seats.Clone();
            var taken = Enumerable.Range(0, was.Length).Where(i => was[i] is not null).ToArray();
            for (var i = 0; i < taken.Length; i++) room.Seats[taken[i]] = was[taken[(i + 1) % taken.Length]];
        }
        room.Status = RoomStatus.Lobby;
        room.Result = null;
        room.StartedAt = null;
        room.FinishedAt = null;
        room.Moves = 0;
        room.Charged.Clear();
        return null;
    }

    /// <summary>Рядок Глека в балачку столу: хто що змінив — щоб решта за столом не дізнавалась про це посеред партії.</summary>
    static string Line(string nick, IReadOnlyList<GameOption> changed, IReadOnlyDictionary<string, string> next, bool fresh)
    {
        var what = string.Join("; ", changed.Select(o => $"{o.Label} — {ValueLabel(o, next[o.Key])}"));
        if (!fresh) return $"⚙ {nick} міняє налаштування: {what}";
        return changed.Count == 0
            ? $"⚙ {nick} ставить стіл наново — налаштовуємось і починаємо"
            : $"⚙ {nick} ставить стіл наново: {what}";
    }

    /// <summary>Підпис значення, як у попапі: «швидкий», а для кількох — «Україна · Наука».</summary>
    static string ValueLabel(GameOption o, string value)
    {
        IReadOnlyList<string> picked = o.Multi ? GameOption.Split(value) : [value];
        return string.Join(" · ", picked.Select(v =>
        {
            var label = o.Values.FirstOrDefault(x => x.Value == v).Label;
            return string.IsNullOrEmpty(label) ? v : label;
        }));
    }
}
