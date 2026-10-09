using System.Text.Json;
using Microsoft.AspNetCore.SignalR;

namespace Hlechyky.Games;

/// <summary>Місце одного гравця в одній грі турніру.</summary>
public sealed record TournamentPlace(string Nick, int Place, int Points, long? Score);

/// <summary>Одна зіграна (або пропущена) гра турніру.</summary>
public sealed record TournamentGameResult(string GameId, string Title, bool Skipped, IReadOnlyList<TournamentPlace> Places);

/// <summary>
/// Турнір на вечір: кілька ігор поспіль, турнірні очки за місця в кожній, чемпіон отримує 👑 до наступного турніру.
/// Турнір один на сайт і живе в пам'яті (як і столи); корона — у сховищі стану, щоб пережити рестарт.
/// <para>
/// Сервіс нічого не знає про SignalR, крім однієї речі: після кожної зміни розсилає всім подію <c>tournament</c>
/// зі знімком (<see cref="Snapshot"/>). Столи ставить сам через <see cref="Rooms"/> — так само, як це зробили б
/// гравці: господар турніру створює, решта сідає, господар тисне «Почати».
/// </para>
/// </summary>
public sealed class Tournament(Rooms rooms, Registry registry, GameEvents events, IOutbox outbox, IGameStore store,
    Presence presence, IHubContext<RadioHub> hub, IClock clock, ILogger<Tournament> log) : IHostedService
{
    public const int MinGames = 2, MaxGames = 6;
    const string CrownKey = "tournament:crown";

    /// <summary>
    /// Скільки після дограної гри чекаємо, перш ніж сайт сам поставить наступний стіл (записка #23): досить, щоб
    /// глянути підсумок і таблицю, і мало, щоб не вставати й не шукати новий стіл руками.
    /// </summary>
    public static readonly TimeSpan AutoDelay = TimeSpan.FromSeconds(10);

    public const string Gathering = "gathering", Playing = "playing", Between = "between", Done = "done";

    readonly object _lock = new();
    State? _t;
    string[]? _crown;

    sealed class State
    {
        public required string Id { get; init; }
        public required string Host { get; set; }
        public required List<string> Games { get; init; }
        public List<string> Players { get; } = [];
        public string Stage { get; set; } = Gathering;
        /// <summary>Скільки ігор уже зіграно (і номер наступної, рахуючи з нуля).</summary>
        public int Index { get; set; }
        public string? Room { get; set; }
        public Dictionary<string, int> Points { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TournamentGameResult> Results { get; } = [];
        public string[] Champions { get; set; } = [];
        public DateTimeOffset CreatedAt { get; init; }
        /// <summary>Чи ставили вже хоч один стіл: пропуск ще на зборі відліку не вмикає — люди, може, ще сходяться.</summary>
        public bool Started { get; set; }
        /// <summary>Коли сайт сам поставить наступний стіл; null — відліку нема (пауза, збір, кінець).</summary>
        public DateTimeOffset? NextAt { get; set; }
        /// <summary>Господар натиснув «⏸ Пауза» в цій перерві — далі лише вручну.</summary>
        public bool Held { get; set; }
        /// <summary>Чому наступна гра не поставилась сама (хтось сидить за іншим столом тощо) — бачать усі.</summary>
        public string? Note { get; set; }
        /// <summary>Стіл щойно дограної гри: з нього клієнти самі переходять за новий і показують там відлік.</summary>
        public string? Prev { get; set; }
    }

    Timer? _timer;

    /// <summary>Свій таймер відліку; тести його вимикають і кличуть <see cref="Tick"/> самі, крутячи годинник.</summary>
    public bool OwnTimer { get; init; } = true;

    public Task StartAsync(CancellationToken ct)
    {
        events.RoomFinished += OnRoomFinished;
        // Відлік живе тут, а не в браузерах: стіл поставиться, навіть якщо господар закрив вкладку.
        if (OwnTimer) _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        events.RoomFinished -= OnRoomFinished;
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    // =========================================================================================
    // Дії
    // =========================================================================================

    /// <summary>Зібрати турнір. null — вийшло, інакше текст, чому ні.</summary>
    public string? Create(string nick, IReadOnlyList<string>? games)
    {
        var list = Clean(games);
        if (CheckGames(list) is { } why) return why;
        lock (_lock)
        {
            if (_t is { Stage: not Done }) return "Турнір уже йде — гайда до нього!";
            _t = new State { Id = Guid.NewGuid().ToString("N")[..8], Host = nick, Games = list, CreatedAt = clock.UtcNow };
            _t.Players.Add(nick);
            _t.Points[nick] = 0;
        }
        outbox.Post(new Journal($"🏆 {nick} збирає турнір: {string.Join(" → ", list.Select(Title))}. Гайда в «Ігри» → «Турнір»!"));
        Changed();
        return null;
    }

    static List<string> Clean(IReadOnlyList<string>? games) => [.. (games ?? []).Where(g => !string.IsNullOrWhiteSpace(g))];

    /// <summary>Ті самі межі списку, що й при створенні: 2–6 ігор, кожна — на щонайменше двох.</summary>
    string? CheckGames(IReadOnlyList<string> list)
    {
        if (list.Count is < MinGames or > MaxGames) return $"Обери від {MinGames} до {MaxGames} ігор";
        foreach (var g in list)
        {
            if (registry.Info(g) is not { } info) return "Нема такої гри";
            if (rooms.Closed(g) is not null) return $"«{info.Title}» на сайті вимкнено";
            if (!Fits(info)) return $"«{info.Title}» — не для турніру: потрібна гра, у яку грають щонайменше вдвох";
        }
        return null;
    }

    /// <summary>
    /// Змінити список ігор (записка #20): додати, прибрати, переставити. Поки турнір збирається — увесь список,
    /// у перерві між іграми — лише ще не зіграні: зігране (і пропущене) вже в таблиці, його не переписуємо.
    /// Шлють увесь список разом із зіграним початком — так сервер бачить, що правили саме цей стан: якщо, поки
    /// господар правив, почалась наступна гра, початок не зійдеться, і правка не ляже на чужий стан.
    /// </summary>
    public string? Edit(string nick, IReadOnlyList<string>? games)
    {
        var list = Clean(games);
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return "Турніру зараз нема";
            Refresh(t);
            if (!CanLead(t, nick)) return "Змінити ігри може той, хто збирав турнір";
            if (t.Stage == Playing) return "Гра ще йде — ігри поміняєш у перерві";
            if (t.Stage == Done) return "Турнір уже скінчився";
            var played = t.Index;
            if (list.Count < played || !list.Take(played).SequenceEqual(t.Games.Take(played), StringComparer.OrdinalIgnoreCase))
                return "Зіграні ігри вже не поміняєш — глянь, що зараз у турнірі";
            if (list.Count == played) return "Лиши хоч одну незіграну гру — або заверши турнір";
            if (CheckGames(list) is { } why) return why;
            if (list.SequenceEqual(t.Games, StringComparer.OrdinalIgnoreCase)) return null;
            t.Games.Clear();
            t.Games.AddRange(list);
        }
        outbox.Post(new Journal($"🏆 {nick} змінює ігри турніру: {string.Join(" → ", list.Select(Title))}"));
        Changed();
        return null;
    }

    /// <summary>«⏸ Пауза»: зупинити відлік до наступної гри — далі господар тисне «Далі» сам, коли всі готові.</summary>
    public string? Pause(string nick)
    {
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return "Турніру зараз нема";
            if (!CanLead(t, nick)) return "Пауза — за тим, хто збирав турнір";
            if (t.Stage != Between || t.NextAt is null) return "Зараз нема відліку — нема чого зупиняти";
            t.NextAt = null;
            t.Held = true;
        }
        Changed();
        return null;
    }

    public string? Join(string nick)
    {
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return "Турніру зараз нема — збери свій";
            if (t.Players.Contains(nick, StringComparer.OrdinalIgnoreCase)) return "Ти вже в турнірі";
            t.Players.Add(nick);
            t.Points.TryAdd(nick, 0);
        }
        Changed();
        return null;
    }

    public string? Leave(string nick)
    {
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return "Турніру зараз нема";
            var i = t.Players.FindIndex(p => string.Equals(p, nick, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return "Тебе нема в турнірі";
            t.Players.RemoveAt(i);
            // очки не забираємо: хто вже заробив — той у таблиці; а в збиранні людини просто нема
            if (t.Stage == Gathering) t.Points.Remove(nick);
            if (t.Players.Count == 0) { _t = null; }
            else if (string.Equals(t.Host, nick, StringComparison.OrdinalIgnoreCase)) t.Host = t.Players[0];
        }
        Changed();
        return null;
    }

    public string? Cancel(string nick)
    {
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return "Турніру зараз нема";
            if (!CanLead(t, nick)) return "Скасувати може лише той, хто збирав турнір";
            if (t.Results.Count > 0) Finish(t, early: true);
            else _t = null;
        }
        if (_t is null) outbox.Post(new Journal($"🏆 Турнір скасовано"));
        Changed();
        return null;
    }

    /// <summary>
    /// Наступна гра: стіл ставить сервер — господар стола перший онлайн-учасник, решта сідає, і партія стартує.
    /// Учасники, яких зараз нема на сайті, цю гру пропускають (0 очок). Під час відліку це ж кнопка «Зараз».
    /// </summary>
    public string? Next(string nick)
    {
        Outbox outs = new();
        string? line, room;
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return "Турніру зараз нема";
            Refresh(t);
            if (!CanLead(t, nick)) return "Наступну гру запускає той, хто збирав турнір";
            if (Launch(t, outs, out line) is { } why) return why;
            room = t.Room;
        }
        outs.Add(new Journal(line!, room));
        Flush(outs);
        Changed();
        return null;
    }

    /// <summary>
    /// Раз на пів секунди (таймер сервісу; тести кличуть самі): стіл поточної гри зник — гра пропущена; відлік
    /// перерви добіг кінця — ставимо наступний стіл, як «Далі». Не вийшло — відлік зупиняється, а причину бачать
    /// усі: далі господар вирішує сам (зачекати, пропустити гру, «Далі»).
    /// </summary>
    public void Tick()
    {
        try
        {
            Outbox outs = new();
            string? line = null, room = null;
            bool changed;
            lock (_lock)
            {
                if (_t is not { Stage: not Done } t) return;
                var (stage, index) = (t.Stage, t.Index);
                Refresh(t);
                changed = t.Stage != stage || t.Index != index;
                if (t is { Stage: Between, NextAt: { } at } && clock.UtcNow >= at)
                {
                    t.NextAt = null;
                    if (Launch(t, outs, out line) is { } why) t.Note = $"Наступна гра сама не почалась: {why}";
                    room = t.Room;
                    changed = true;
                }
            }
            if (line is not null) outs.Add(new Journal(line, room));
            Flush(outs);
            if (changed) Changed();
        }
        catch (Exception ex) { log.LogWarning(ex, "турнір не зміг поставити наступний стіл"); }
    }

    /// <summary>Поставити стіл наступної гри й посадити учасників. Під замком турніру; null — вийшло.</summary>
    string? Launch(State t, Outbox outs, out string? line)
    {
        line = null;
        if (t.Stage == Playing) return "Гра ще йде — спершу дограйте";
        if (t.Index >= t.Games.Count) return "Усі ігри вже зіграно";

        var gameId = t.Games[t.Index];
        var info = registry.Info(gameId)!;
        var here = t.Players.Where(presence.IsOnline).ToList();
        if (here.Count < Math.Max(2, info.MinPlayers)) return $"Для «{info.Title}» треба щонайменше {Math.Max(2, info.MinPlayers)} учасників на сайті";
        if (here.Count > info.MaxPlayers) return $"У «{info.Title}» грає щонайбільше {info.MaxPlayers}, а вас {here.Count}";
        if (info.Start == StartMode.WhenFull && here.Count != info.MaxPlayers) return $"«{info.Title}» стартує лише повним складом ({info.MaxPlayers})";
        var lobby = rooms.Snapshot();
        bool At(RoomSummary r, string p) => r.Seats.Any(s => string.Equals(s.Nick, p, StringComparison.OrdinalIgnoreCase));
        // Хто сидить за іншим, не дограним столом, того турнір не висмикує — ні руками, ні після відліку.
        var busy = here.FirstOrDefault(p => lobby.Any(r => r.Status != "finished" && At(r, p)));
        if (busy is not null) return $"{busy} ще сидить за іншим столом — хай встане";
        // За дограним столом (зокрема минулою грою турніру) каркас теж тримає місце — звільняємо його самі.
        foreach (var r in lobby.Where(r => r.Status == "finished"))
            foreach (var p in here.Where(p => At(r, p))) outs.Adopt(rooms.Leave(r.Id, p).Out);

        var created = rooms.Create(here[0], gameId, null);
        // Турнір садить усіх сам, тож кликати за цей стіл нікого: інакше в Балачках лишився б рядок «кличе в …»
        // за стіл, на якому вже нема місця.
        created.Out.RemoveAll(m => m is Invite or InviteLine);
        outs.Adopt(created.Out);
        if (!created.Reply.Ok || created.Reply.RoomId is not { } id) { Flush(outs); return created.Reply.Message; }
        foreach (var p in here.Skip(1))
        {
            var joined = rooms.Join(id, p);
            outs.Adopt(joined.Out);
            if (!joined.Reply.Ok)
            {
                foreach (var q in here) outs.Adopt(rooms.Leave(id, q).Out);
                Flush(outs);
                return $"{p} не може сісти: {joined.Reply.Message}";
            }
        }
        if (info.Start == StartMode.ByHost)
        {
            var started = rooms.StartByHost(id, here[0]);
            outs.Adopt(started.Out);
            if (!started.Reply.Ok)
            {
                foreach (var q in here) outs.Adopt(rooms.Leave(id, q).Out);
                Flush(outs);
                return started.Reply.Message;
            }
        }
        t.Room = id;
        t.Stage = Playing;
        t.Started = true;
        t.NextAt = null;
        t.Held = false;
        t.Note = null;
        line = $"🏆 Турнір, гра {t.Index + 1} з {t.Games.Count}: {info.Title}. Гайда за стіл!";
        return null;
    }

    /// <summary>
    /// Гра зависла (усі повставали, стіл прибрали) — рахуємо пропущеною й ідемо далі. Між іграми (і ще на зборі)
    /// так само можна пропустити наступну: інакше гра, у яку нинішній склад не влазить (дуель на двох, а вас
    /// троє), назавжди ставала б стіною, і турнір лишалось би хіба скасувати.
    /// </summary>
    public string? Skip(string nick)
    {
        Room? room = null;
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t || t.Index >= t.Games.Count) return "Зараз нема чого пропускати";
            if (!CanLead(t, nick)) return "Пропустити гру може той, хто збирав турнір";
            // стіл поточної гри міг уже зникнути — тоді Refresh сам рахує її пропущеною, і вдруге (уже наступну) не пропускаємо
            var index = t.Index;
            Refresh(t);
            if (t.Index == index && t.Stage != Done)
            {
                if (t.Stage == Playing && t.Room is not null) room = rooms.Find(t.Room);
                // Спершу гра стає пропущеною, а вже потім учасники встають: інакше техпоразка того, хто встав
                // першим, порахувалась би як справжній результат — очки за те, що сидів не на тому місці.
                Skipped(t);
            }
            if (room is not null && room.Status == RoomStatus.Playing)
                foreach (var p in room.Seats.OfType<string>().ToList()) Flush(rooms.Leave(room.Id, p).Out);
        }
        Changed();
        return null;
    }

    bool CanLead(State t, string nick) =>
        string.Equals(t.Host, nick, StringComparison.OrdinalIgnoreCase)
        // господаря нема на сайті — турнір не має стояти: веде будь-хто з учасників
        || !presence.IsOnline(t.Host) && t.Players.Contains(nick, StringComparer.OrdinalIgnoreCase);

    static bool Fits(GameInfo info) => !info.Solo && !info.Private && info.MaxPlayers >= 2;

    string Title(string gameId) => registry.Info(gameId)?.Title ?? gameId;

    void Flush(Outbox outs)
    {
        foreach (var m in outs) outbox.Post(m);
        outs.Clear();
    }

    // =========================================================================================
    // Результати
    // =========================================================================================

    void OnRoomFinished(RoomFinishedEvent e)
    {
        try
        {
            lock (_lock)
            {
                if (_t is not { Stage: Playing } t || t.Room != e.RoomId) return;
                var places = Places(e.Seats, e.Result.Scores, e.Result.Winners);
                foreach (var p in places) t.Points[p.Nick] = t.Points.GetValueOrDefault(p.Nick) + p.Points;
                t.Results.Add(new TournamentGameResult(e.GameId, e.Info.Title, false, places));
                // спершу результат гри, потім (якщо це була остання) — чемпіон: так і в Журналі читається
                var line = string.Join(", ", places.Select(p => $"{p.Place}. {p.Nick} +{p.Points}"));
                outbox.Post(new Journal($"🏆 Турнір, {e.Info.Title}: {line}"));
                Advance(t);
            }
            Changed();
        }
        catch (Exception ex) { log.LogWarning(ex, "турнір не порахував стіл {Room}", e.RoomId); }
    }

    /// <summary>
    /// Місця й турнірні очки за одну гру. Є рахунок — переможці вище за решту, а всередині кожної купки місце за
    /// рахунком (рівний рахунок — рівне місце); нема — переможці перші, решта другі, нічия — усі перші. Очки: гравців у грі − місце + 1 (утрьох: 3, 2, 1).
    /// </summary>
    public static List<TournamentPlace> Places(IReadOnlyList<string?> seats, IReadOnlyDictionary<int, long>? scores, int[] winners)
    {
        var who = seats.Select((n, i) => (Nick: n, Seat: i)).Where(x => !string.IsNullOrWhiteSpace(x.Nick)).ToList();
        var n = who.Count;
        List<(string Nick, int Place, long? Score)> ranked;
        if (scores is { Count: > 0 })
        {
            long Of(int seat) => scores.TryGetValue(seat, out var v) ? v : long.MinValue;
            // Переможець — завжди вище за тих, хто програв, хай навіть рахунок у нього менший: у сапері
            // той, хто наступив на міну, міг мати більше відкритих клітинок, але партію програв саме він.
            bool Won(int seat) => winners.Length == 0 || winners.Contains(seat);
            bool Above(int a, int b) => Won(a) != Won(b) ? Won(a) : Of(a) > Of(b);
            ranked = [.. who.Select(x => (x.Nick!, 1 + who.Count(y => Above(y.Seat, x.Seat)), scores.TryGetValue(x.Seat, out var s) ? (long?)s : null))];
        }
        else
        {
            ranked = [.. who.Select(x => (x.Nick!, winners.Length == 0 || winners.Contains(x.Seat) ? 1 : 2, (long?)null))];
        }
        return [.. ranked.OrderBy(r => r.Place).ThenBy(r => r.Nick, StringComparer.OrdinalIgnoreCase)
            .Select(r => new TournamentPlace(r.Nick, r.Place, n - r.Place + 1, r.Score))];
    }

    void Skipped(State t)
    {
        t.Results.Add(new TournamentGameResult(t.Games[t.Index], Title(t.Games[t.Index]), true, []));
        outbox.Post(new Journal($"🏆 Турнір: «{Title(t.Games[t.Index])}» не дограли — гру пропущено"));
        Advance(t);
    }

    void Advance(State t)
    {
        t.Index++;
        if (t.Room is not null) t.Prev = t.Room;
        t.Room = null;
        t.Held = false;
        t.Note = null;
        t.NextAt = null;
        if (t.Index >= t.Games.Count) Finish(t, early: false);
        else
        {
            t.Stage = Between;
            // Остання гра відліку не має — одразу підсумок і корона; а пропуск ще на зборі — не привід саджати людей.
            if (t.Started) t.NextAt = clock.UtcNow + AutoDelay;
        }
    }

    void Finish(State t, bool early)
    {
        t.Stage = Done;
        t.Room = null;
        t.NextAt = null;
        var best = t.Points.Count == 0 ? 0 : t.Points.Values.Max();
        t.Champions = best > 0 ? [.. t.Points.Where(kv => kv.Value == best).Select(kv => kv.Key).Order(StringComparer.OrdinalIgnoreCase)] : [];
        if (t.Champions.Length == 0)
        {
            outbox.Post(new Journal("🏆 Турнір закінчено — без чемпіона"));
            return;
        }
        _crown = t.Champions;
        var json = JsonSerializer.Serialize(new { nicks = t.Champions, at = clock.UtcNow });
        // Один короткий запис раз на турнір: під замком турніру (не кімнати) це нікому не заважає.
        try { store.SaveState(CrownKey, json); } catch (Exception ex) { log.LogWarning(ex, "корону не збережено"); }
        var who = string.Join(" і ", t.Champions);
        var table = string.Join(", ", t.Points.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
        outbox.Post(new Journal($"👑 {(early ? "Турнір закінчили достроково. " : "Овва! ")}Чемпіон турніру — {who}! Таблиця: {table}"));
        outbox.Post(new DjSays($"Вітаю, {who}! Корона ваша до наступного турніру 👑"));
    }

    /// <summary>Стіл поточної гри зник, не дограний (усі встали ще в лобі, прибиральник) — не чекати ж вічно.</summary>
    void Refresh(State t)
    {
        if (t.Stage != Playing || t.Room is null) return;
        if (rooms.Find(t.Room) is null) Skipped(t);
    }

    // =========================================================================================
    // Знімок
    // =========================================================================================

    public string[] Crown()
    {
        lock (_lock)
        {
            if (_crown is not null) return _crown;
        }
        string[] loaded = [];
        try
        {
            if (store.LoadState(CrownKey) is { } json)
                loaded = JsonDocument.Parse(json).RootElement.GetProperty("nicks").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray();
        }
        catch (Exception) { /* зіпсований запис — просто без корони */ }
        lock (_lock) { return _crown ??= loaded; }
    }

    public object Snapshot()
    {
        var crown = Crown();
        lock (_lock)
        {
            if (_t is { } t0) Refresh(t0);
            if (_t is not { } t) return new { active = false, crown };
            var room = t.Room is null ? null : rooms.Find(t.Room);
            return new
            {
                active = t.Stage != Done,
                id = t.Id,
                host = t.Host,
                stage = t.Stage,
                index = t.Index,
                games = t.Games.Select(g => new { id = g, title = Title(g) }).ToArray(),
                players = t.Players.ToArray(),
                online = t.Players.Where(presence.IsOnline).ToArray(),
                standings = t.Points.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new { nick = kv.Key, points = kv.Value }).ToArray(),
                results = t.Results.Select(r => new
                {
                    game = r.GameId, title = r.Title, skipped = r.Skipped,
                    places = r.Places.Select(p => new { nick = p.Nick, place = p.Place, points = p.Points, score = p.Score }).ToArray(),
                }).ToArray(),
                // seats — щоб браузер переводив за новий стіл лише тих, кого турнір справді посадив
                room = room is null ? null : new { id = room.Id, status = room.Status.ToString().ToLowerInvariant(), seats = room.Seats.OfType<string>().ToArray() },
                prev = t.Prev,
                // Скільки лишилось до наступного столу, а не момент: годинники в браузерах бувають криві на хвилини.
                nextIn = t is { Stage: Between, NextAt: { } at } ? (int?)Math.Max(0, (int)Math.Ceiling((at - clock.UtcNow).TotalMilliseconds)) : null,
                nextOf = (int)AutoDelay.TotalMilliseconds,
                held = t.Stage == Between && t.Held,
                note = t.Stage == Between ? t.Note : null,
                champions = t.Champions,
                crown,
            };
        }
    }

    void Changed()
    {
        object snap;
        try { snap = Snapshot(); }
        catch (Exception ex) { log.LogWarning(ex, "знімок турніру не склався"); return; }
        _ = hub.Clients.All.SendAsync("tournament", snap);
    }

    // =========================================================================================
    // Перезапуск сервера
    // =========================================================================================

    sealed record Frozen(string Id, string Host, List<string> Games, List<string> Players, string Stage, int Index, string? Room,
        Dictionary<string, int> Points, List<TournamentGameResult> Results, string[] Champions, DateTimeOffset CreatedAt);

    static readonly JsonSerializerOptions FrozenJson = new(JsonSerializerDefaults.Web);

    /// <summary>Турнір, що йде, — у знімок столів (<see cref="TablesKeeper"/>); null — турніру нема або він уже скінчився.</summary>
    public JsonElement? Freeze()
    {
        lock (_lock)
        {
            if (_t is not { Stage: not Done } t) return null;
            return JsonSerializer.SerializeToElement(new Frozen(t.Id, t.Host, [.. t.Games], [.. t.Players], t.Stage, t.Index, t.Room,
                new Dictionary<string, int>(t.Points, StringComparer.OrdinalIgnoreCase), [.. t.Results], t.Champions, t.CreatedAt), FrozenJson);
        }
    }

    /// <summary>Повернути турнір зі знімка — на старті, після того як столи вже відновлено (його стіл має той самий id).</summary>
    public void Restore(JsonElement json)
    {
        if (json.Deserialize<Frozen>(FrozenJson) is not { } f) return;
        lock (_lock)
        {
            var t = new State { Id = f.Id, Host = f.Host, Games = f.Games, Stage = f.Stage, Index = f.Index, Room = f.Room, CreatedAt = f.CreatedAt };
            t.Players.AddRange(f.Players);
            foreach (var (nick, points) in f.Points) t.Points[nick] = points;
            t.Results.AddRange(f.Results);
            t.Champions = f.Champions;
            _t = t;
            // Гру турніру перервав перезапуск (подія «дограно» не прилетить) — рахуємо пропущеною, інакше «Наступна гра»
            // відповідала б «Гра ще йде», аж поки стіл не приберуть.
            if (t.Stage == Playing && t.Room is { } id && rooms.Find(id) is { } room)
            {
                string? verdict;
                lock (room.Sync) verdict = room.Status == RoomStatus.Finished ? room.Result?.Verdict : null;
                if (verdict == Rooms.InterruptedVerdict) Skipped(t);
            }
        }
    }

    /// <summary>Хтось зайшов або вийшов — список «хто зараз тут» у турнірі міг змінитись.</summary>
    public void PresenceChanged()
    {
        bool any;
        lock (_lock) any = _t is { Stage: not Done };
        if (any) Changed();
    }
}
