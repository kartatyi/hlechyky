using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games;

/// <summary>
/// Знімок усіх столів на диску (<see cref="TablesKeeper"/>). <c>Clean</c> — знімок «заморозки» перед перезапуском:
/// після нього ходів уже не було, тож партію, яку гра вміє зберегти (<see cref="Game.Save"/>), можна грати далі. Знімок
/// без заморозки пишеться про всяк випадок раз на кілька секунд (сервер міг і впасти) — з нього столи повертаються, а
/// партії, що саме йшли, вважаються перерваними: між знімком і падінням хтось міг устигнути походити.
/// </summary>
public sealed record FrozenTables(int Version, DateTimeOffset At, bool Clean, IReadOnlyList<FrozenRoom> Rooms, JsonElement? Tournament = null)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// Один стіл у знімку. <c>State</c> — <see cref="Game.Save"/> партії, що йде (лише в чистому знімку й лише в ігор із
/// <see cref="Game.Resumable"/>). <c>LobbyActs</c> — налаштування столу в лобі, які програються новій грі наново. <c>Views</c>/<c>WatcherView</c> — повні види (<see cref="Game.Snapshot"/>) дограного чи перерваного столу
/// тим самим JSON, що летить браузерам: нова гра в новому процесі тієї дошки не знає, а людям треба бачити підсумок.
/// </summary>
public sealed record FrozenRoom(
    string Id,
    string Game,
    IReadOnlyDictionary<string, string> Options,
    string?[] Seats,
    string Host,
    RoomStatus Status,
    int Stake,
    int Round,
    FrozenResult? Result,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Key,
    int Moves,
    IReadOnlyList<string> Charged,
    string?[]? LoggedSeats,
    DateTimeOffset? CalledAt,
    IReadOnlyList<TableLine> Talk,
    IReadOnlyList<EveningRow> Evening,
    int EveningGames,
    string? State = null,
    IReadOnlyDictionary<int, JsonElement>? Views = null,
    JsonElement? WatcherView = null,
    IReadOnlyList<LobbyAct>? LobbyActs = null,
    TableBank? Bank = null,
    IReadOnlyDictionary<string, int>? Owed = null);

public sealed record FrozenResult(int[] Winners, bool Draw, string Text, IReadOnlyDictionary<int, long>? Scores, string? Verdict)
{
    public static FrozenResult Of(RoomResult r) =>
        new(r.Winners, r.Draw, r.Text, r.Scores is null ? null : new Dictionary<int, long>(r.Scores), r.Verdict);

    public RoomResult ToResult() => new(Winners, Draw, Text, Scores, Verdict);
}

/// <summary>Що вийшло з відновлення: столів усього, з них партій, що грають далі, перерваних, соло-кімнат; не вдалось — Skipped.</summary>
public sealed record RestoreReport(int Tables, int Continued, int Interrupted, int Solo, int Skipped);

/// <summary>Партія, яку перезапуск перервав би: деплой чекає, поки таких не лишиться (<see cref="Rooms.Busy"/>).</summary>
public sealed record BusyTable(string Id, string Game, string Title, IReadOnlyList<string> Players, DateTimeOffset? Since, bool Solo);

public sealed partial class Rooms
{
    /// <summary>
    /// Поверх <see cref="Grace"/> після відновлення: з'єднання рвуться всі разом, браузери стукають наново хто за секунду,
    /// а хто (телефон у кишені, схована вкладка) — пізніше. Місце тримаємо стільки від старту нового процесу.
    /// </summary>
    public static readonly TimeSpan RestoreGrace = TimeSpan.FromSeconds(60);

    /// <summary>Підпис партії, яку перервав перезапуск (у шапці столу замість «Нічия»).</summary>
    public const string InterruptedVerdict = "⚡ Партію перервав перезапуск сайту";

    volatile bool _frozen;
    DateTimeOffset _frozenAt;

    /// <summary>Столи «заморожено» перед перезапуском: ходи, тики й прибирання стоять, знімок уже на диску.</summary>
    public bool Frozen => _frozen;
    public DateTimeOffset FrozenAt => _frozenAt;

    /// <summary>
    /// Заморозити столи й зняти чистий знімок. З цієї миті кожна дія за столом відповідає «⏳ Сайт оновлюється», реалтайм
    /// стоїть, нікого не викидає з-за столу — процес от-от зупинять. Прапорець перевіряється під замком кожної кімнати,
    /// а знімок бере той самий замок, тож хід або встиг і є в знімку, або відбився.
    /// </summary>
    public FrozenTables Freeze()
    {
        _frozenAt = _clock.UtcNow;
        _frozen = true;
        return Capture(clean: true);
    }

    /// <summary>Перезапуск не відбувся (деплой передумав, процес не зупинили) — грати далі.</summary>
    public void Thaw() => _frozen = false;

    /// <summary>
    /// Знімок усіх столів. <paramref name="clean"/> — заморожено, і партія, що йде, береться разом зі станом гри;
    /// інакше (знімок про всяк випадок) — лише видами, бо після відновлення вона однаково буде перервана.
    /// </summary>
    public FrozenTables Capture(bool clean)
    {
        var list = new List<FrozenRoom>();
        foreach (var room in Live())
        {
            try
            {
                lock (room.Sync)
                    if (Freeze(room, clean) is { } frozen) list.Add(frozen);
            }
            catch (Exception ex) { _log.LogWarning(ex, "стіл {Room} не вліз у знімок", room.Id); }
        }
        return new FrozenTables(FrozenTables.CurrentVersion, _clock.UtcNow, clean, list);
    }

    /// <summary>Один стіл у знімок. Під замком кімнати. null — тут нічого берегти.</summary>
    FrozenRoom? Freeze(Room room, bool clean)
    {
        // Порожній стіл не бережемо — крім партії, за якою лежать черепки (турнір на черепки, з-за якого всі встали й
        // ще можуть вернутись): без знімка банк зник би разом зі столом. Відновлена — або йде далі (і сама розрахується),
        // або перервана — з поверненням.
        if (room.Occupied == 0 && !(room.Status == RoomStatus.Playing && room.Bank.Held > 0)) return null;
        // Соло: Persistent-гра й так у сховищі після кожного ходу — бережемо лише кімнату (той самий id у браузері).
        // Решта соло — короткі забіги, яких ніхто не чекатиме назад (а деплой чекає, поки вони скінчаться, — Busy).
        if (room.Info.Solo && (!room.Info.Persistent || room.Key is null)) return null;

        string? state = null;
        if (clean && room.Status == RoomStatus.Playing && !room.Info.Solo && room.Game.Resumable)
        {
            try { state = room.Game.Save(); }
            catch (Exception ex) { _log.LogWarning(ex, "Save впав у кімнаті {Room} — партія буде перервана", room.Id); }
        }
        // Види — тим, що після відновлення стоятиме дограним: дограний стіл і партія, яку нема чим продовжити.
        Dictionary<int, JsonElement>? views = null;
        JsonElement? watcher = null;
        if (!room.Info.Solo && (room.Status == RoomStatus.Finished || (room.Status == RoomStatus.Playing && state is null)))
        {
            views = [];
            for (var i = 0; i < room.Seats.Length; i++)
                if (room.Seats[i] is not null && Wire(FullView(room, i)) is { } v) views[i] = v;
            watcher = Wire(FullView(room, null));
        }

        return new FrozenRoom(
            room.Id, room.Info.Id, new Dictionary<string, string>(room.Options), (string?[])room.Seats.Clone(), room.Host,
            room.Status, room.Stake, room.Round, room.Result is { } r ? FrozenResult.Of(r) : null,
            room.CreatedAt, room.StartedAt, room.FinishedAt, room.Key, room.Moves, [.. room.Charged],
            room.LoggedSeats is null ? null : (string?[])room.LoggedSeats.Clone(), room.CalledAt,
            [.. room.Talk],
            [.. room.Evening.Values.Select(e => new EveningRow
            {
                Nick = e.Nick, Wins = e.Wins, Games = e.Games, Points = e.Points, HasPoints = e.HasPoints, Order = e.Order,
            })],
            room.EveningGames, state, views, watcher,
            room.Status == RoomStatus.Lobby && room.LobbyActs.Count > 0 ? [.. room.LobbyActs] : null,
            room.Bank.Accounts.Count > 0 ? room.Bank.Clone() : null,
            room.Status == RoomStatus.Playing && room.Bank.Held > 0 ? OwedNow(room) : null);
    }

    /// <summary>Банк столу на мить знімка: кому скільки належить, якщо партію не продовжать (<see cref="Game.SettleTable"/>).</summary>
    IReadOnlyDictionary<string, int>? OwedNow(Room room)
    {
        try { return room.Game.SettleTable() is { } owed ? new Dictionary<string, int>(owed) : null; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SettleTable впав у кімнаті {Room} — при перериванні поверну «вніс мінус забрав»", room.Id);
            return null;
        }
    }

    /// <summary>Повний вид місця (той, що отримує новенький), а коли стіл сам відновлений — збережений. Під замком кімнати.</summary>
    object? FullView(Room room, int? seat)
    {
        if (room.Restored is { } restored) return restored.View(seat);
        try { return room.Game.Snapshot(seat); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Snapshot({Seat}) впав у кімнаті {Room}", seat, room.Id);
            return SafeView(room, seat);
        }
    }

    /// <summary>
    /// Вид у JSON рівно так, як його пише SignalR (camelCase і решта налаштувань хаба): відновлений стіл віддасть цей
    /// JsonElement як є, і модуль гри не відрізнить його від живого виду.
    /// </summary>
    JsonElement? Wire(object? view)
    {
        if (view is null) return null;
        try { return JsonSerializer.SerializeToElement(view, view.GetType(), WireJson); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "вид {Type} не серіалізувався для знімка", view.GetType().Name);
            return null;
        }
    }

    JsonSerializerOptions WireJson => _wireJson ??=
        _services.GetService<IOptions<JsonHubProtocolOptions>>()?.Value.PayloadSerializerOptions
        ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    JsonSerializerOptions? _wireJson;

    /// <summary>
    /// Повернути столи зі знімка — на старті сервера, до першого з'єднання. Лобі, місця, господар, рахунок вечора, балачка,
    /// ставки — як були. Партія, що йшла: з чистого знімка й зі станом гри — грає далі; інакше — перервана, без результату
    /// й рейтингу, ставки повернуто. Хто сидів, має <see cref="RestoreGrace"/>, щоб повернутись.
    /// </summary>
    public RestoreReport Restore(FrozenTables tables)
    {
        var now = _clock.UtcNow;
        var pause = now > tables.At ? now - tables.At : TimeSpan.Zero;
        int count = 0, continued = 0, interrupted = 0, solo = 0, skipped = 0;
        var refunds = new List<(string Nick, int Amount, string Reason, string Ref)>();
        var seated = new List<string>();
        long talk = 0;

        foreach (var f in tables.Rooms)
        {
            try
            {
                if (_registry.Info(f.Game) is not { } info) { Refund(f, refunds); skipped++; continue; }
                if (Find(f.Id) is not null) { skipped++; continue; }
                if (info.Solo)
                {
                    if (RestoreSolo(info, f, now)) solo++;
                    else skipped++;
                    continue;
                }

                var room = Rebuild(info, f, now);
                switch (f.Status)
                {
                    case RoomStatus.Lobby:
                        room.Status = RoomStatus.Lobby;
                        Replay(room, f.LobbyActs);
                        break;
                    case RoomStatus.Finished:
                        room.Status = RoomStatus.Finished;
                        room.Result = f.Result?.ToResult();
                        room.Restored = ViewsOf(f);
                        break;
                    default:
                        if (tables.Clean && f.State is { Length: > 0 } state && Resume(room, state, now, pause)) continued++;
                        else
                        {
                            Interrupt(room, f, now, refunds);
                            interrupted++;
                        }
                        break;
                }
                // Балачку й вечір — уже після Start/Load: Ctx.Say ведучого на старті гри дописав би в балачку зайве.
                room.Talk.Clear();
                room.Talk.AddRange(f.Talk.TakeLast(TalkLines));
                foreach (var e in f.Evening)
                    room.Evening[e.Nick] = new EveningRow
                    {
                        Nick = e.Nick, Wins = e.Wins, Games = e.Games, Points = e.Points, HasPoints = e.HasPoints, Order = e.Order,
                    };
                room.EveningGames = f.EveningGames;
                if (room.Talk.Count > 0) talk = Math.Max(talk, room.Talk[^1].Id);

                lock (_lock) _rooms.Add(room);
                seated.AddRange(room.Seats.OfType<string>());
                count++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "стіл {Room} ({Game}) не відновився", f.Id, f.Game);
                Refund(f, refunds);
                skipped++;
            }
        }

        // Номери реплік наскрізні на весь сервер: браузер відкидає рядок, номер якого вже бачив. Знімок не з заморозки
        // міг не застати останніх реплік, які браузери вже бачили, — тоді з запасом, щоб нові не збіглися з ними номером.
        if (!tables.Clean && talk > 0) talk += 1000;
        if (talk > Interlocked.Read(ref _talkSeq)) Interlocked.Exchange(ref _talkSeq, talk);
        // Нікого з тих, хто сидів, ще нема на зв'язку: відлік grace — як від виходу, тільки довший.
        lock (_lock)
            foreach (var nick in seated) _offline[nick] = now + (RestoreGrace - Grace);
        foreach (var (nick, amount, reason, reference) in refunds)
        {
            try { _stakes.Grant(nick, amount, reason, reference); }
            catch (Exception ex) { _log.LogWarning(ex, "ставку {Nick} не повернуто ({Ref})", nick, reference); }
        }
        return new RestoreReport(count, continued, interrupted, solo, skipped);
    }

    /// <summary>Кімната з тим самим id, місцями й опціями; гра — нова, налаштована тими ж опціями. Сід — новий.</summary>
    Room Rebuild(GameInfo info, FrozenRoom f, DateTimeOffset now)
    {
        var game = _registry.Create(f.Game)!;
        var effective = Effective(info, f.Options);
        var seats = new string?[info.MaxPlayers];
        Array.Copy(f.Seats, seats, Math.Min(f.Seats.Length, seats.Length));
        var room = new Room
        {
            Id = f.Id,
            Info = info,
            Game = game,
            Seats = seats,
            Options = effective,
            Stake = f.Stake,
            Key = f.Key,
            // Сід новий: з тим самим наступна партія почалась би тими ж картами, що перша за цим столом.
            Seed = SeedOverride ?? unchecked((int)(now.Ticks ^ Interlocked.Increment(ref _seedCounter))),
            CreatedAt = f.CreatedAt,
            LastActivity = now,
        };
        room.Host = f.Host;
        room.Round = f.Round;
        room.StartedAt = f.StartedAt;
        room.FinishedAt = f.FinishedAt;
        room.Moves = f.Moves;
        room.Charged.AddRange(f.Charged);
        if (f.Bank is { } bank) room.Bank = bank.Clone();
        room.LoggedSeats = f.LoggedSeats;
        room.CalledAt = f.CalledAt;
        game.Ctx = new RoomContext(room, this);
        game.Configure(effective);
        return room;
    }

    /// <summary>
    /// Партія грає далі: нова гра стартує й одразу бере стан зі знімка (як OpenSolo для Persistent-ігор), а годинники
    /// зсуваються на час, поки сервер стояв (<see cref="Game.Resumed"/>).
    /// </summary>
    bool Resume(Room room, string state, DateTimeOffset now, TimeSpan pause)
    {
        if (!room.Game.Resumable) return false;   // знімок від версії, де гра ще вміла, — а ця вже ні
        var ctx = (RoomContext)room.Game.Ctx;
        try
        {
            // Розсилку старту викидаємо: це не нова партія, а та сама, і Журналу та гаманцям про неї нічого не кажемо.
            // Банк столу мовчить: викупи Start-у вже за столом, облік — зі знімка (Rebuild).
            ctx.Quiet = true;
            using (ctx.Collect(new Outbox()))
            {
                room.Game.Start();
                room.Game.Load(state);
                room.Game.Resumed(pause);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "партія в кімнаті {Room} не відновилась зі стану — перериваю", room.Id);
            return false;
        }
        finally { ctx.Quiet = false; }
        room.Status = RoomStatus.Playing;
        room.Result = null;
        room.FinishedAt = null;
        room.NextTickAt = now.AddMilliseconds(room.Info.TickMs);
        return true;
    }

    /// <summary>Партію нема чим продовжити: стіл стоїть дограним із підписом, без результату й рейтингу, ставки — назад.</summary>
    void Interrupt(Room room, FrozenRoom f, DateTimeOffset now, List<(string, int, string, string)> refunds)
    {
        room.Status = RoomStatus.Finished;
        room.FinishedAt = now;
        // Знімок не з заморозки міг застати партію, яку встигли дограти й розрахувати вже після нього: ставки тоді
        // вже в переможця чи повернуті, і повертати їх удруге не можна.
        if (room.Charged.Count > 0 && _stakes.Settled(room.Id, room.Round)) room.Charged.Clear();
        var back = room.Stake > 0 && room.Charged.Count > 0;
        var table = room.Bank.Held > 0;
        room.Result = new RoomResult([], true,
            $"{room.Info.Title}: партію перервав перезапуск сайту{(back ? " — ставки повернуто" : table ? " — черепки зі столу повернуто" : "")}",
            null, InterruptedVerdict);
        foreach (var nick in room.Charged)
            refunds.Add((nick, room.Stake, "stake-refund", $"stake-refund:{room.Id}:{room.Round}:{NickKey(nick)}"));
        room.Charged.Clear();
        RefundBank(room.Id, room.Round, room.Info.Id, room.Bank, f.Owed, refunds);
        room.Restored = ViewsOf(f);
    }

    /// <summary>
    /// Банк столу перерваної партії — назад: кожному те, що гра сказала на мить знімка (<see cref="FrozenRoom.Owed"/>), інакше
    /// «вніс мінус забрав». Ключ — наступний table-out ніка: повтор відновлення з того самого знімка нічого не подвоїть, а
    /// виплата, що встигла пройти вже після знімка (знімок про всяк випадок), займе той самий ключ і повернення не задублює.
    /// Облік банку оновлюється тут же — кімната стоїть розрахованою.
    /// </summary>
    void RefundBank(string roomId, int round, string gameId, TableBank bank, IReadOnlyDictionary<string, int>? owed,
        List<(string, int, string, string)> refunds)
    {
        foreach (var (nick, amount) in bank.Owed(owed, _log, roomId))
        {
            var acc = bank.Of(nick);
            var n = ++acc.OutN;
            acc.Out += amount;
            refunds.Add((nick, amount, TableMoney.Reason(TableMoney.Refund, gameId), $"table-out:{roomId}:{round}:{NickKey(nick)}:{n}"));
        }
    }

    /// <summary>Стіл не відновився (гру прибрали, налаштування вже не ті), а ставки за партію, що йшла, списано, — назад.</summary>
    void Refund(FrozenRoom f, List<(string, int, string, string)> refunds)
    {
        if (f.Status == RoomStatus.Playing && f.Bank is { } bank)
            RefundBank(f.Id, f.Round, f.Game, bank.Clone(), f.Owed, refunds);
        if (f.Status != RoomStatus.Playing || f.Stake <= 0 || f.Charged.Count == 0) return;
        try { if (_stakes.Settled(f.Id, f.Round)) return; }
        catch (Exception ex) { _log.LogWarning(ex, "не вдалось перевірити розрахунок столу {Room}", f.Id); }
        foreach (var nick in f.Charged)
            refunds.Add((nick, f.Stake, "stake-refund", $"stake-refund:{f.Id}:{f.Round}:{NickKey(nick)}"));
    }

    /// <summary>Налаштування столу в лобі — новій грі тими самими ходами. Не прийнялось (місце вже порожнє) — пропускаємо.</summary>
    void Replay(Room room, IReadOnlyList<LobbyAct>? acts)
    {
        if (acts is null || acts.Count == 0) return;
        var ctx = (RoomContext)room.Game.Ctx;
        using (ctx.Collect(new Outbox()))
            foreach (var a in acts)
            {
                try
                {
                    var payload = a.Payload is null ? default : JsonDocument.Parse(a.Payload).RootElement.Clone();
                    if (room.Game.Act(a.Seat, a.Action, payload).Ok) room.LobbyActs.Add(a);
                }
                catch (Exception ex) { _log.LogDebug(ex, "налаштування {Action} столу {Room} не програлось", a.Action, room.Id); }
            }
    }

    static RestoredViews? ViewsOf(FrozenRoom f) =>
        f.Views is null && f.WatcherView is null ? null : new RestoredViews(f.Views ?? new Dictionary<int, JsonElement>(), f.WatcherView);

    /// <summary>Особиста кімната Persistent-гри з тим самим id: стан — зі сховища, як у OpenSolo.</summary>
    bool RestoreSolo(GameInfo info, FrozenRoom f, DateTimeOffset now)
    {
        if (!info.Persistent || f.Key is not { } key || f.Seats.FirstOrDefault() is not { } nick) return false;
        lock (_lock)
            if (_rooms.Any(r => r.Info.Solo && r.Key == key && r.Has(nick))) return false;
        var room = Rebuild(info, f with { Stake = 0, Charged = [] }, now);
        string? failed;
        lock (room.Sync)
        {
            failed = StartRound(room, new Outbox());
            if (failed is null && _store.LoadState(key) is { Length: > 0 } saved)
            {
                try { room.Game.Load(saved); }
                catch (Exception ex) { _log.LogWarning(ex, "не вдалось відновити стан {Key}, граємо з чистого", key); }
            }
        }
        if (failed is not null) return false;
        lock (_lock) _rooms.Add(room);
        return true;
    }

    /// <summary>
    /// Партії, які перезапуск зараз перервав би: мультиплеєрні, що йдуть, і соло-забіги, що в когось на екрані. Ігри з
    /// <see cref="Game.Resumable"/> не рахуються — вони переживуть перезапуск і так.
    /// </summary>
    public List<BusyTable> Busy()
    {
        var list = new List<BusyTable>();
        foreach (var room in Live())
        {
            lock (room.Sync)
            {
                // Порожній стіл деплою не тримає, навіть із банком столу: людей, чию партію урвало б, нема, а черепки
                // знімок береже (Freeze) — стіл продовжиться чи при перериванні поверне внески.
                if (room.Status != RoomStatus.Playing || room.Occupied == 0) continue;
                if (room.Info.Solo)
                {
                    if (room.Info.Persistent || room.OnScreen.IsEmpty) continue;
                }
                else if (room.Game.Resumable) continue;
                list.Add(new BusyTable(room.Id, room.Info.Id, room.Info.Title, [.. room.Seats.OfType<string>()], room.StartedAt, room.Info.Solo));
            }
        }
        return list;
    }
}
