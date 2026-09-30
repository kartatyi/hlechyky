using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hlechyky;

/// <summary>Налаштування Посиденьок (розділ «VoiceChat» в appsettings).</summary>
public sealed class VoiceChatOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Скільки людей в одній кімнаті голосу. Голос іде напряму між людьми (кожен шле кожному), тож на десятьох кожен
    /// віддає дев'ять потоків — для голосу це ще дрібниця, а далі вже впирається в мобільний інтернет.
    /// </summary>
    public int MaxPerRoom { get; set; } = 10;
    /// <summary>STUN/TURN для браузера. Порожньо — два публічні STUN (Google, Cloudflare).</summary>
    public List<IceServerOptions> IceServers { get; set; } = [];
    /// <summary>
    /// Секрет coturn (use-auth-secret): з ним кожен, хто заходить, отримує тимчасовий логін на <see cref="TurnUrls"/>.
    /// Порожньо — ретранслятора нема, і хто не проб'ється напряму, той не проб'ється.
    /// </summary>
    public string TurnSecret { get; set; } = "";
    public List<string> TurnUrls { get; set; } = [];
    public int TurnTtlHours { get; set; } = 12;
}

public sealed class IceServerOptions
{
    public List<string> Urls { get; set; } = [];
    public string? Username { get; set; }
    public string? Credential { get; set; }
}

public sealed record IceServer(IReadOnlyList<string> Urls, string? Username = null, string? Credential = null);

/// <summary>Відповідь на VoiceJoin: куди потрапив і через що пробиватись (ice).</summary>
public sealed record VoiceJoinReply(bool Ok, string? Error = null, string? Room = null, IReadOnlyList<IceServer>? Ice = null)
{
    public static VoiceJoinReply Fail(string error) => new(false, error);
}

public sealed record VoiceMemberDto(string Peer, string Nick, bool Muted, bool Deaf, bool Share);
/// <summary>Кімната голосу: <c>home</c> — Посиденьки, <c>t:&lt;id столу&gt;</c> — голос столу.</summary>
public sealed record VoiceRoomDto(string Id, string? Table, string? Game, string Title, IReadOnlyList<VoiceMemberDto> Members);
public sealed record VoiceRosterDto(IReadOnlyList<VoiceRoomDto> Rooms);
/// <summary>З ким як: Send — він мене чує, Recv — я його чую, Watch — він дивиться мій екран.</summary>
public sealed record VoiceLink(string Peer, bool Send, bool Recv, bool Watch);
public sealed record VoiceMeDto(string Peer, string Room, IReadOnlyList<VoiceLink> Links);

/// <summary>Одне відправлення: To — з'єднання, null — усім.</summary>
public sealed record VoiceSend(string? To, string Event, object? Payload);
public sealed record VoiceOutcome<T>(T Reply, IReadOnlyList<VoiceSend> Sends);

/// <summary>
/// Посиденьки — голосовий чат. Сам голос іде напряму між браузерами (WebRTC, кожен з кожним), сервер лише:
/// <list type="bullet">
/// <item>тримає, хто в якій кімнаті голосу (<c>home</c> — Посиденьки, <c>t:&lt;стіл&gt;</c> — голос столу) — подія
///   <c>voice</c> усім, щоб шапка й люди бачили, хто де говорить;</item>
/// <item>каже кожному, кого він чує і хто чує його (<c>voiceMe</c>): за столом це вирішує гра (<see cref="Game.Voice"/>) —
///   мафія вночі чує лише мафію, мертві говорять лише між собою; браузер слухається й відправником, і слухачем;</item>
/// <item>передає листи між браузерами, поки ті домовляються про з'єднання (<c>voiceSignal</c>) — лише в межах однієї кімнати.</item>
/// </list>
/// Позивний (peer) браузер вигадує сам і тримає, поки відкрита вкладка: сервер перезапустився з деплоєм — усі
/// заходять знову з тими самими позивними, і вже встановлені з'єднання між людьми живуть далі, голос не рветься.
/// Методи повертають, що розіслати (<see cref="VoiceSend"/>), — так їх перевіряють тести без SignalR; хаб відправляє
/// через <see cref="DispatchAsync"/>.
/// </summary>
public sealed class VoiceChat(Rooms rooms, IOptionsMonitor<VoiceChatOptions> options, IHubContext<RadioHub>? hub = null, ILogger<VoiceChat>? log = null)
{
    public const string Home = "home";
    public const string HomeTitle = "Посиденьки";
    const string TablePrefix = "t:";

    /// <summary>Найдовший лист між браузерами (SDP з відео — кілька КБ; кандидати браузер шле пачками).</summary>
    public const int MaxSignalChars = 24_000;   // SignalR типово не приймає повідомлень, більших за 32 КБ
    /// <summary>
    /// Листів за секунду з одного з'єднання. Новачок у кімнаті на десятьох шле кожному привіт, опис і кілька пачок
    /// кандидатів — це десятки листів за раз, а загублений лист коштує з'єднання, яке сторож піднімає секунди.
    /// </summary>
    public const int SignalsPerSecond = 200;
    /// <summary>Хто де (подія voice) — не частіше: інакше один акаунт, клацаючи мікрофоном, смикав би весь сайт.</summary>
    public const int RosterGapMs = 250;

    public const string NotAccount = "Посиденьки — лише для акаунтів: зареєструй нік, і заходь";
    public const string Off = "Посиденьки зараз зачинені";
    public const string BadPeer = "Щось не те з голосом — онови сторінку";
    public const string NotIn = "Спершу зайди в Посиденьки";
    public const string NotAtTable = "Голос столу — для тих, хто за ним сидить чи дивиться";
    public const string Moved = "Посиденьки перейшли в іншу вкладку";

    static readonly Regex PeerRx = new("^[a-z0-9]{8,32}$", RegexOptions.CultureInvariant);
    static readonly IceServer[] DefaultIce = [new(["stun:stun.l.google.com:19302"]), new(["stun:stun.cloudflare.com:3478"])];

    sealed class Member
    {
        public required string Peer;
        public required string Nick;
        public required string Conn;
        public string Room = Home;
        public bool Muted, Deaf, Share;
        /// <summary>Чиї екрани я дивлюсь (позивні).</summary>
        public readonly HashSet<string> Watching = new(StringComparer.Ordinal);
        public VoiceMeDto? Me;
        public string MeSig = "";
        public long QuotaSecond;
        public int QuotaUsed;
    }

    readonly object _lock = new();
    readonly Dictionary<string, Member> _byConn = new(StringComparer.Ordinal);
    /// <summary>Назви столів, у яких хтось говорить: id столу → (гра, назва).</summary>
    readonly Dictionary<string, (string Game, string Title)> _tables = new(StringComparer.Ordinal);
    VoiceRosterDto _roster = new([]);
    string _rosterSig = JsonSerializer.Serialize(new VoiceRosterDto([]));
    int _pending;
    Timer? _sweep;

    /// <summary>Що зараз знає з'єднання про себе (останній voiceMe); null — його нема в голосі.</summary>
    public VoiceMeDto? MeOf(string conn) { lock (_lock) return _byConn.TryGetValue(conn, out var m) ? m.Me : null; }

    /// <summary>Хто де говорить зараз (з позивними — для тих, хто в голосі).</summary>
    public VoiceRosterDto Roster { get { lock (_lock) return _roster; } }

    /// <summary>
    /// Те саме без позивних — для всіх, хто не в голосі (новенькому з'єднанню — одразу при підключенні): їм досить ніків,
    /// а знаючи чужий позивний, після перезапуску сервера його можна було б зайняти раніше за власника.
    /// </summary>
    public VoiceRosterDto PublicRoster => Public(Roster);

    static VoiceRosterDto Public(VoiceRosterDto r) =>
        new([.. r.Rooms.Select(x => x with { Members = [.. x.Members.Select(m => m with { Peer = "" })] })]);

    // ---------- вхід і вихід ----------

    /// <summary>
    /// Зайти в голос: <paramref name="table"/> null — у Посиденьки, інакше — у голос цього столу (якщо за ним сидиш чи
    /// дивишся). Той самий нік з іншої вкладки — інша вкладка вилітає (двоє однакових у голосі — це луна). Той самий
    /// позивний із новим з'єднанням — це реконект або сервер після деплою: місце й стан лишаються.
    /// </summary>
    public VoiceOutcome<VoiceJoinReply> Join(string conn, string nick, bool account, string? peer, string? table, bool muted, bool deaf)
    {
        var o = options.CurrentValue;
        if (!o.Enabled) return new(VoiceJoinReply.Fail(Off), []);
        if (!account) return new(VoiceJoinReply.Fail(NotAccount), []);
        if (peer is null || !PeerRx.IsMatch(peer)) return new(VoiceJoinReply.Fail(BadPeer), []);
        var sends = new List<VoiceSend>();
        lock (_lock)
        {
            if (_byConn.Values.FirstOrDefault(m => m.Peer == peer && !SameNick(m.Nick, nick)) is not null)
                return new(VoiceJoinReply.Fail(BadPeer), []);
            string? keepRoom = null;
            foreach (var old in _byConn.Values.Where(m => m.Conn == conn || SameNick(m.Nick, nick)).ToList())
            {
                _byConn.Remove(old.Conn);
                if (old.Peer == peer) keepRoom = old.Room;
                else if (old.Conn != conn) sends.Add(new(old.Conn, "voiceKick", new { text = Moved }));
            }
            var room = keepRoom ?? Home;
            if (!string.IsNullOrEmpty(table))
            {
                if (!rooms.VoiceAllowed(table, nick, conn)) return new(VoiceJoinReply.Fail(NotAtTable), Finish(sends));
                room = TablePrefix + table;
            }
            if (Count(room) >= o.MaxPerRoom) return new(VoiceJoinReply.Fail(Full(room)), Finish(sends));
            _byConn[conn] = new Member { Peer = peer, Nick = nick, Conn = conn, Room = room, Muted = muted, Deaf = deaf };
            sends.AddRange(Recompute());
            EnsureSweep();
            return new(new VoiceJoinReply(true, Room: _byConn[conn].Room, Ice: Ice(peer)), sends);
        }
    }

    /// <summary>Вийти з голосу (кнопка чи закрита вкладка — Drop).</summary>
    public IReadOnlyList<VoiceSend> Leave(string conn)
    {
        lock (_lock) return _byConn.Remove(conn) ? Recompute() : [];
    }

    /// <summary>З'єднання закрилось. Позивний браузер пам'ятає: повернеться — зайде знову, і з'єднання з людьми живуть.</summary>
    public IReadOnlyList<VoiceSend> Drop(string conn) => Leave(conn);

    /// <summary>Перейти: <paramref name="table"/> null — у Посиденьки, інакше — у голос столу.</summary>
    public VoiceOutcome<string?> Follow(string conn, string? table)
    {
        lock (_lock)
        {
            if (!_byConn.TryGetValue(conn, out var me)) return new(NotIn, []);
            var room = Home;
            if (!string.IsNullOrEmpty(table))
            {
                if (!rooms.VoiceAllowed(table, me.Nick, conn)) return new(NotAtTable, []);
                room = TablePrefix + table;
            }
            if (me.Room == room) return new(null, []);
            if (Count(room) >= options.CurrentValue.MaxPerRoom) return new(Full(room), []);
            me.Room = room;
            me.Watching.Clear();
            return new(null, Recompute());
        }
    }

    /// <summary>Свій мікрофон вимкнено / нікого не чую — усім видно в списку.</summary>
    public IReadOnlyList<VoiceSend> Set(string conn, bool muted, bool deaf)
    {
        lock (_lock)
        {
            if (!_byConn.TryGetValue(conn, out var me) || (me.Muted == muted && me.Deaf == deaf)) return [];
            me.Muted = muted;
            me.Deaf = deaf;
            return Recompute();
        }
    }

    /// <summary>Показую свій екран чи вже ні. Перестав — ті, хто дивився, більше не дивляться.</summary>
    public IReadOnlyList<VoiceSend> Share(string conn, bool on)
    {
        lock (_lock)
        {
            if (!_byConn.TryGetValue(conn, out var me) || me.Share == on) return [];
            me.Share = on;
            if (!on) foreach (var m in _byConn.Values) m.Watching.Remove(me.Peer);
            return Recompute();
        }
    }

    /// <summary>Дивитись (чи вже ні) екран <paramref name="peer"/>: той почне (чи перестане) слати мені відео.</summary>
    public VoiceOutcome<string?> Watch(string conn, string peer, bool on)
    {
        lock (_lock)
        {
            if (!_byConn.TryGetValue(conn, out var me)) return new(NotIn, []);
            if (!on) return me.Watching.Remove(peer) ? new(null, Recompute()) : new(null, []);
            var them = _byConn.Values.FirstOrDefault(m => m.Peer == peer);
            if (them is null || them.Room != me.Room || them == me) return new("Цієї людини тут уже нема", []);
            if (!them.Share) return new("Цей екран уже не показують", []);
            return me.Watching.Add(peer) ? new(null, Recompute()) : new(null, []);
        }
    }

    /// <summary>
    /// Лист між браузерами (опис з'єднання чи кандидати) — лише тому, хто в тій самій кімнаті голосу. Зайве мовчки
    /// відкидається: браузер повторить, а спамер нічого не доб'ється.
    /// </summary>
    public IReadOnlyList<VoiceSend> Signal(string conn, string? to, string? data, long unixSecond)
    {
        if (string.IsNullOrEmpty(to) || string.IsNullOrEmpty(data) || data.Length > MaxSignalChars) return [];
        lock (_lock)
        {
            if (!_byConn.TryGetValue(conn, out var me)) return [];
            if (me.QuotaSecond != unixSecond) (me.QuotaSecond, me.QuotaUsed) = (unixSecond, 0);
            if (++me.QuotaUsed > SignalsPerSecond) return [];
            var them = _byConn.Values.FirstOrDefault(m => m.Peer == to);
            if (them is null || them == me || them.Room != me.Room) return [];
            return [new(them.Conn, "voiceSignal", new { from = me.Peer, data })];
        }
    }

    // ---------- стіл змінився ----------

    /// <summary>
    /// Щось змінилось за столами (Broadcaster розіслав види чи лобі): перерахувати, хто кого чує, — мафія заснула, хтось
    /// устав з-за столу. Рахуємо не тут, а трохи згодом у пулі потоків: розсилка чекати на голос не мусить.
    /// </summary>
    public void OnFlushed(IReadOnlyList<Outgoing> all)
    {
        if (!HasTables || !all.Any(x => x is RoomViews or LobbyChanged)) return;
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(30);
            Interlocked.Exchange(ref _pending, 0);
            await DispatchAsync(Refresh());
        });
    }

    /// <summary>Перерахувати все й сказати, що змінилось (тестам — напряму).</summary>
    public IReadOnlyList<VoiceSend> Refresh()
    {
        lock (_lock) return Recompute();
    }

    bool HasTables { get { lock (_lock) return _byConn.Values.Any(m => m.Room != Home); } }

    /// <summary>
    /// Раз на дві секунди, поки хтось говорить за столом, — страховка: не кожна зміна за столом (хтось пішов, стіл
    /// закрився) проходить через розсилку видів.
    /// </summary>
    void EnsureSweep()
    {
        if (_sweep is not null || hub is null) return;
        _sweep = new Timer(_ =>
        {
            if (!HasTables) return;
            try { _ = DispatchAsync(Refresh()); }
            catch (Exception ex) { log?.LogWarning(ex, "голос: перерахунок упав"); }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    // ---------- розрахунок ----------

    /// <summary>
    /// Під <see cref="_lock"/>: хто випав зі столу — назад у Посиденьки; кожному — з ким як (voiceMe), якщо змінилось;
    /// усім — хто де (voice), якщо змінилось.
    /// </summary>
    List<VoiceSend> Recompute()
    {
        var sends = new List<VoiceSend>();
        var rules = new Dictionary<Member, VoiceRule>();
        _tables.Clear();
        foreach (var group in _byConn.Values.Where(m => m.Room != Home).GroupBy(m => m.Room).ToList())
        {
            var list = group.ToList();
            var table = group.Key[TablePrefix.Length..];
            var got = rooms.VoiceRules(table, [.. list.Select(m => (m.Nick, m.Conn))]);
            for (var i = 0; i < list.Count; i++)
            {
                if (got?.Rules[i] is { } rule) rules[list[i]] = rule;
                else { list[i].Room = Home; list[i].Watching.Clear(); }
            }
            if (got is { } g) _tables[table] = (g.Game, g.Title);
        }

        foreach (var me in _byConn.Values)
        {
            var links = new List<VoiceLink>();
            foreach (var them in _byConn.Values)
            {
                if (them == me || them.Room != me.Room) continue;
                var (send, recv) = me.Room == Home || !rules.TryGetValue(me, out var mine) || !rules.TryGetValue(them, out var theirs)
                    ? (true, true)
                    : (theirs.Hears(mine), mine.Hears(theirs));
                links.Add(new VoiceLink(them.Peer, send, recv, me.Share && them.Watching.Contains(me.Peer)));
            }
            links.Sort((a, b) => string.CompareOrdinal(a.Peer, b.Peer));
            var dto = new VoiceMeDto(me.Peer, me.Room, links);
            var sig = JsonSerializer.Serialize(dto);
            me.Me = dto;
            if (sig == me.MeSig) continue;
            me.MeSig = sig;
            sends.Add(new(me.Conn, "voiceMe", dto));
        }

        var roster = new VoiceRosterDto(_byConn.Values
            .GroupBy(m => m.Room)
            .Select(g => RoomDto(g.Key, g))
            .OrderBy(r => r.Id == Home ? 0 : 1).ThenBy(r => r.Title, StringComparer.CurrentCulture).ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToList());
        var rosterSig = JsonSerializer.Serialize(roster);
        if (rosterSig != _rosterSig)
        {
            _rosterSig = rosterSig;
            _roster = roster;
            sends.Add(new(null, "voice", roster));
        }
        return sends;
    }

    VoiceRoomDto RoomDto(string room, IEnumerable<Member> members)
    {
        var list = members.OrderBy(m => m.Nick, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => new VoiceMemberDto(m.Peer, m.Nick, m.Muted, m.Deaf, m.Share)).ToList();
        if (room == Home) return new(Home, null, null, HomeTitle, list);
        var table = room[TablePrefix.Length..];
        var (game, title) = _tables.TryGetValue(table, out var t) ? t : ("", "Стіл");
        return new(room, table, game, title, list);
    }

    int Count(string room) => _byConn.Values.Count(m => m.Room == room);

    string Full(string room) => room == Home
        ? $"У Посиденьках уже {options.CurrentValue.MaxPerRoom} — більше мережа не потягне"
        : $"У голосі столу вже {options.CurrentValue.MaxPerRoom} — більше мережа не потягне";

    /// <summary>Зібрати те, що вже набралось (вилетіла інша вкладка), навіть коли сам вхід не вдався.</summary>
    List<VoiceSend> Finish(List<VoiceSend> sends)
    {
        sends.AddRange(Recompute());
        return sends;
    }

    static bool SameNick(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>STUN і, якщо налаштовано, TURN з тимчасовим логіном (coturn use-auth-secret: ім'я «строк:позивний»).</summary>
    IReadOnlyList<IceServer> Ice(string peer)
    {
        var o = options.CurrentValue;
        var list = o.IceServers.Where(s => s.Urls.Count > 0).Select(s => new IceServer(s.Urls, s.Username, s.Credential)).ToList();
        if (list.Count == 0) list.AddRange(DefaultIce);
        if (o.TurnSecret.Length > 0 && o.TurnUrls.Count > 0)
        {
            var user = $"{DateTimeOffset.UtcNow.AddHours(Math.Max(1, o.TurnTtlHours)).ToUnixTimeSeconds()}:{peer}";
            var pass = Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(o.TurnSecret), Encoding.UTF8.GetBytes(user)));
            list.Add(new IceServer(o.TurnUrls, user, pass));
        }
        return list;
    }

    // ---------- відправка ----------

    public async Task DispatchAsync(IReadOnlyList<VoiceSend> sends)
    {
        if (hub is null) return;
        foreach (var s in sends)
        {
            // Хто де — склеюємо: летить найсвіжіший список не частіше за RosterGapMs.
            if (s.To is null && s.Event == "voice") { ScheduleRoster(); continue; }
            try
            {
                var client = s.To is null ? hub.Clients.All : hub.Clients.Client(s.To);
                await client.SendAsync(s.Event, s.Payload);
            }
            catch (Exception ex) { log?.LogWarning(ex, "голос: не відправилось {Event}", s.Event); }
        }
    }

    int _rosterQueued;
    long _rosterSentAt = long.MinValue / 2;

    void ScheduleRoster()
    {
        if (Interlocked.Exchange(ref _rosterQueued, 1) == 1) return;   // уже летить — прихопить і цю зміну
        var wait = RosterGapMs - (Environment.TickCount64 - Interlocked.Read(ref _rosterSentAt));
        _ = Task.Run(async () =>
        {
            try
            {
                if (wait > 0) await Task.Delay((int)wait);
                Interlocked.Exchange(ref _rosterQueued, 0);
                Interlocked.Exchange(ref _rosterSentAt, Environment.TickCount64);
                VoiceRosterDto full;
                string[] members;
                lock (_lock) { full = _roster; members = [.. _byConn.Keys]; }
                // Тим, хто в голосі, — з позивними (їм з'єднуватись), решті — лише ніки.
                if (members.Length > 0) await hub!.Clients.Clients(members).SendAsync("voice", full);
                await hub!.Clients.AllExcept(members).SendAsync("voice", Public(full));
            }
            catch (Exception ex) { log?.LogWarning(ex, "голос: не відправився список"); }
        });
    }
}
