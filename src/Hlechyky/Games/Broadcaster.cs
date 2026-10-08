using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hlechyky.Games;

/// <summary>Кому летить повідомлення.</summary>
public abstract record SendTarget;
/// <summary>Усім підключеним.</summary>
public sealed record ToAll : SendTarget;
/// <summary>Групі SignalR (<c>room:&lt;id&gt;</c>).</summary>
public sealed record ToGroup(string Name) : SendTarget;
/// <summary>Групі, крім названих з'єднань — ті вже отримали свій, персональний вид.</summary>
public sealed record ToGroupExcept(string Name, IReadOnlyList<string> Except) : SendTarget;
/// <summary>Переліченим з'єднанням.</summary>
public sealed record ToConnections(IReadOnlyList<string> Ids) : SendTarget;

/// <summary>Одне готове відправлення: кому, яка подія, що в тілі.</summary>
public sealed record Send(SendTarget Target, string Event, object? Payload);

/// <summary>
/// Outbox → SignalR. Уся розкладка (кому який вид, що склеїти, що викинути) робиться чистою функцією
/// <see cref="Plan"/>, і саме її перевіряють тести; <see cref="FlushAsync"/> лише відправляє готове.
/// Заразом Broadcaster — це <see cref="IOutbox"/> для сервісів: вони кладуть свої повідомлення в чергу
/// з будь-якого потоку, а зливає її TickEngine на найближчому колі.
/// </summary>
public sealed class Broadcaster(
    IHubContext<RadioHub> hub,
    Rooms rooms,
    Presence presence,
    Db db,
    RadioEngine engine,
    IOptionsMonitor<SiteOptions> site,
    ILogger<Broadcaster> log) : IOutbox
{
    readonly ConcurrentQueue<Outgoing> _posted = new();

    /// <summary>Група глядачів кімнати.</summary>
    public static string RoomGroup(string id) => "room:" + id;

    /// <summary>Сервіси (WP1) кладуть сюди своє з інших потоків; зливає TickEngine.</summary>
    public void Post(Outgoing message) => _posted.Enqueue(message);

    /// <summary>
    /// Пачка пішла на розсилку. Так голос столу (VoiceChat) дізнається, що за столом щось змінилось, — мафія заснула,
    /// хтось устав, — не встромляючись у кожне місце, де це стається.
    /// </summary>
    public event Action<IReadOnlyList<Outgoing>>? Flushed;

    /// <summary>Розіслати. Разом із чергою від сервісів, щоб нічого не зависало до наступного тика.</summary>
    public async Task FlushAsync(IEnumerable<Outgoing> messages, CancellationToken ct = default)
    {
        var all = Drain(messages);
        if (all.Count == 0) return;
        try { Flushed?.Invoke(all); }
        catch (Exception ex) { log.LogWarning(ex, "підписник розсилки впав"); }

        // Дедлайн на всю пачку: те, що не дописалось повільному клієнтові за 2 с, для нього пропадає.
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));

        List<Send> sends;
        try
        {
            // Усе, що в Журнал пишуть ігри й сервіси (столи, підсумки, ачівки, турнір), — під фільтром «🎮 Ігри».
            // Рядок-заклик — не Журнал, а репліка в Балачках від імені того, хто кличе (kind invite).
            sends = Plan(all, rooms.Snapshot, rooms.SoloNow, rooms.ViewsFor, presence.Get, presence.ConnectionsOf,
                (text, roomId) => db.AddChat(site.CurrentValue.Name, text, "system", roomId, topic: "games"),
                (by, text, roomId) => db.AddChat(by, text, "invite", roomId),
                (roomId, conn) => rooms.SnapshotFor(roomId, conn, presence.Get(conn)));
        }
        catch (Exception ex)
        {
            deadline.Dispose();
            log.LogWarning(ex, "не вдалось скласти розсилку");
            return;
        }

        // Не чекаємо на найповільнішого. SignalR пише кожному з'єднанню одразу, а Task лишається незавершеним лише
        // заради того, у кого забитий канал (телефон у ліфті). Раніше цикл тика чекав на нього до дедлайну — і
        // реалтайм завмирав у ВСІХ за столом: заміри 28.09 — паузи між кадрами 1,5–2 с у здорового гравця, поки
        // один бот не читав сокет. Тепер недописане доганяє повільного у фоні (своя черга з'єднання в SignalR береже
        // порядок), а коло йде далі.
        List<Task>? slow = null;
        foreach (var send in sends)
        {
            Task task;
            try { task = Dispatch(send, deadline.Token); }
            catch (Exception ex) { log.LogWarning(ex, "не відправилось {Event}", send.Event); continue; }
            if (task.IsCompletedSuccessfully) continue;
            (slow ??= []).Add(Watch(task, send.Event));
        }
        if (slow is null) deadline.Dispose();
        else _ = Task.WhenAll(slow).ContinueWith(_ => deadline.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        foreach (var say in all.OfType<DjSays>())
        {
            try { await engine.SayAsync(say.Text); }
            catch (Exception ex) { log.LogWarning(ex, "Глек не сказав своє слово"); }
        }
    }

    long _lateWarnAt;

    /// <summary>
    /// Дочекатись відправки повільному з'єднанню у фоні. Не вклалось у дедлайн — це звична справа телефона в ліфті:
    /// пишемо в лог не частіше ніж раз на 10 с, інакше застряглий клієнт на 25 кадрах за секунду засипав би лог.
    /// </summary>
    async Task Watch(Task task, string ev)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lateWarnAt);
            if (now - last >= 10_000 && Interlocked.CompareExchange(ref _lateWarnAt, now, last) == last)
                log.LogWarning("розсилка {Event} комусь не вклалась у дедлайн (повільне з'єднання)", ev);
        }
        catch (Exception ex) { log.LogWarning(ex, "не відправилось {Event}", ev); }
    }

    /// <summary>
    /// Пачка на відправку: те, що дала дія, плюс усе, що сервіси (WP1) поклали через <see cref="Post"/> з
    /// інших потоків. Черга спорожняється навіть тоді, коли своїх повідомлень нема.
    /// </summary>
    public List<Outgoing> Drain(IEnumerable<Outgoing> messages)
    {
        var all = new List<Outgoing>(messages);
        while (_posted.TryDequeue(out var extra)) all.Add(extra);
        return all;
    }

    Task Dispatch(Send send, CancellationToken ct) => send.Target switch
    {
        ToAll => hub.Clients.All.SendAsync(send.Event, send.Payload, ct),
        ToGroup g => hub.Clients.Group(g.Name).SendAsync(send.Event, send.Payload, ct),
        ToGroupExcept g => g.Except.Count == 0
            ? hub.Clients.Group(g.Name).SendAsync(send.Event, send.Payload, ct)
            : hub.Clients.GroupExcept(g.Name, g.Except).SendAsync(send.Event, send.Payload, ct),
        ToConnections c => c.Ids.Count == 0 ? Task.CompletedTask : hub.Clients.Clients(c.Ids).SendAsync(send.Event, send.Payload, ct),
        _ => Task.CompletedTask,
    };

    /// <summary>
    /// Розкладка без жодного SignalR — саме тому її можна перевірити тестом. Склеює повторення: кілька
    /// <see cref="LobbyChanged"/> (і так само <see cref="SoloChanged"/>) в одному Outbox стають одним, кілька
    /// <see cref="RoomViews"/> однієї кімнати — теж (лишається останнє: воно й так рахується від свіжого стану), а з
    /// кадрів однієї кімнати лишається останній. <see cref="DjSays"/> сюди не потрапляє: його вміє лише RadioEngine.
    /// Балачка столу (<see cref="TableSaid"/>, <see cref="TableHistory"/>) не склеюється: кожна репліка — окрема.
    /// <paramref name="inviteLine"/> — записати в базу загальний рядок-заклик (хто кличе, текст, стіл) і віддати те, що
    /// полетить у <c>chat</c>; null — загальних рядків-закликів ця розсилка не пише.
    /// </summary>
    public static List<Send> Plan(
        IReadOnlyList<Outgoing> messages,
        Func<List<RoomSummary>> snapshot,
        Func<List<SoloPlayer>> soloNow,
        Func<string, RoomBroadcast?> viewsFor,
        Func<string, string?> nickOf,
        Func<string, IReadOnlyList<string>> connectionsOf,
        Func<string, string?, object> journal,
        Func<string, string, string, object>? inviteLine = null,
        Func<string, string, RoomView?>? snapshotFor = null)
    {
        var keep = Coalesce(messages);
        var sends = new List<Send>();
        Send? lobby = null;
        // Новенькі (RoomSnapshot) цієї ж пачки легкого виду не отримують: їм летить повний, і двічі те саме ні до чого.
        Dictionary<string, HashSet<string>>? fresh = null;
        if (snapshotFor is not null)
            foreach (var m in messages)
            {
                if (m is not RoomSnapshot rs) continue;
                fresh ??= new(StringComparer.Ordinal);
                if (!fresh.TryGetValue(rs.RoomId, out var set)) fresh[rs.RoomId] = set = new(StringComparer.Ordinal);
                set.Add(rs.ConnectionId);
            }
        foreach (var index in keep)
        {
            switch (messages[index])
            {
                case LobbyChanged:
                    // Знімок лобі рахується від живого стану, а не від місця в черзі, тож і летить першим
                    // (нижче). Інакше рядок «Оля і Петро сідають грати» доходив би до браузера раніше за
                    // новину, що за тим столом уже нема місця, і кнопка на ньому кликала б сідати.
                    lobby = new Send(new ToAll(), "rooms", snapshot());
                    break;
                case SoloChanged:
                    sends.Add(new Send(new ToAll(), "solo", soloNow()));
                    break;
                case RoomViews views:
                    if (viewsFor(views.RoomId) is { } b)
                        sends.AddRange(ViewSends(b, nickOf, fresh is not null && fresh.TryGetValue(views.RoomId, out var skip) ? skip : null));
                    break;
                case RoomSnapshot snap:
                    // Старий виклик без snapshotFor (тести) — хай буде бодай звичайна розсилка виду.
                    if (snapshotFor is null) { if (viewsFor(snap.RoomId) is { } b2) sends.AddRange(ViewSends(b2, nickOf)); }
                    else if (snapshotFor(snap.RoomId, snap.ConnectionId) is { } full)
                        sends.Add(new Send(new ToConnections([snap.ConnectionId]), "room", full));
                    break;
                case RoomFrame frame:
                    sends.Add(new Send(new ToGroup(RoomGroup(frame.RoomId)), "frame", new { id = frame.RoomId, f = frame.Frame }));
                    break;
                case Journal line:
                    // Рядок Журналу дорогою в чат заходить у SQLite. Впала база — це біда одного рядка,
                    // а не всієї пачки: види, кадри й лобі мають полетіти однаково.
                    try { sends.Add(new Send(new ToAll(), "chat", journal(line.Text, line.RoomId))); }
                    catch (Exception) { }
                    break;
                case Invite invite:
                    // Особистий заклик — лише на з'єднання того, кого кличуть; загальний — усім (свій браузер відкине сам).
                    sends.Add(invite.To is { } to
                        ? new Send(new ToConnections(connectionsOf(to)), "invite",
                            new { roomId = invite.RoomId, by = invite.By, text = invite.Text, personal = true })
                        : new Send(new ToAll(), "invite",
                            new { roomId = invite.RoomId, by = invite.By, text = invite.Text, personal = false }));
                    break;
                case InviteLine line when line.To is { } whom:
                    // Особистий рядок у базу не лягає: id 0, як у відповіді на /столи, і лише тому, кого кличуть.
                    sends.Add(new Send(new ToConnections(connectionsOf(whom)), "chat", new
                    {
                        id = 0L, kind = "invite", nick = line.By, text = line.Text, at = line.At, roomId = line.RoomId, personal = true,
                    }));
                    break;
                case InviteLine line:
                    // Той самий захист, що й у Журналу: зайнята база коштує одного рядка, а не всієї пачки.
                    if (inviteLine is null) break;
                    try { sends.Add(new Send(new ToAll(), "chat", inviteLine(line.By, line.Text, line.RoomId))); }
                    catch (Exception) { }
                    break;
                case TableSaid said:
                    // Балачка столу — лише тим, хто на нього дивиться, як і види з кадрами.
                    sends.Add(new Send(new ToGroup(RoomGroup(said.RoomId)), "tableChat", new { id = said.RoomId, line = said.Line }));
                    break;
                case TableReact rx:
                    sends.Add(new Send(new ToGroup(RoomGroup(rx.RoomId)), "tableReact",
                        new { id = rx.RoomId, nick = rx.Nick, seat = rx.Seat, e = rx.E }));
                    break;
                case Anthem anthem:
                    sends.AddRange(AnthemSends(anthem, viewsFor, connectionsOf));
                    break;
                case TableHistory history:
                    sends.Add(new Send(new ToConnections([history.ConnectionId]), "tableHistory",
                        new { id = history.RoomId, lines = history.Lines }));
                    break;
                case WalletChanged w:
                    sends.Add(new Send(new ToConnections(connectionsOf(w.Nick)), "wallet",
                        new { balance = w.Balance, delta = w.Delta, reason = w.Reason, text = w.Text }));
                    break;
                case AchievementUnlocked a:
                    sends.Add(new Send(new ToConnections(connectionsOf(a.Nick)), "achievement",
                        new { key = a.Key, title = a.Title, text = a.Text, icon = a.Icon, reward = a.Reward }));
                    break;
                case ToastFor t:
                    sends.Add(new Send(new ToConnections(connectionsOf(t.Nick)), "toast", new { text = t.Text, kind = t.Kind }));
                    break;
            }
        }
        if (lobby is not null) sends.Insert(0, lobby);
        return sends;
    }

    /// <summary>Індекси повідомлень, які варто відправити: повторення лобі, соло, видів і кадрів згортаються в останнє.</summary>
    static List<int> Coalesce(IReadOnlyList<Outgoing> messages)
    {
        var lobby = -1;
        var solo = -1;
        var views = new Dictionary<string, int>(StringComparer.Ordinal);
        var frames = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
            switch (messages[i])
            {
                case LobbyChanged: lobby = i; break;
                case SoloChanged: solo = i; break;
                case RoomViews v: views[v.RoomId] = i; break;
                case RoomFrame f: frames[f.RoomId] = i; break;
            }

        var keep = new List<int>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var drop = messages[i] switch
            {
                LobbyChanged => i != lobby,
                SoloChanged => i != solo,
                RoomViews v => views[v.RoomId] != i,
                RoomFrame f => frames[f.RoomId] != i,
                DjSays => true,
                _ => false,
            };
            if (!drop) keep.Add(i);
        }
        return keep;
    }

    /// <summary>
    /// Гімн — лише тим, хто за столом: групі столу (гравці й глядачі на його сторінці) і тим, хто сидить за ним, але
    /// зараз деінде на сайті. Реалтайм-стіл браузер поза «Іграми» не тримає в групі (там 25 кадрів на секунду), тож без
    /// другої адреси переможець, що визирнув в Ефір, власного гімну не почув би. З'єднання з групи (<see cref="RoomBroadcast.Watchers"/> —
    /// це рівно вона) другий раз не отримують; хто таки отримав двічі (стіл зник між тиком і розсилкою), відсіє браузер
    /// за ключем «стіл:раунд». В ефір і в лобі гімн не йде.
    /// </summary>
    static IEnumerable<Send> AnthemSends(Anthem anthem, Func<string, RoomBroadcast?> viewsFor, Func<string, IReadOnlyList<string>> connectionsOf)
    {
        var body = new { id = anthem.RoomId, round = anthem.Round, nick = anthem.Nick, title = anthem.Title, emoji = anthem.Emoji, url = anthem.Url };
        yield return new Send(new ToGroup(RoomGroup(anthem.RoomId)), "anthem", body);
        if (viewsFor(anthem.RoomId) is not { } b) yield break;
        var inGroup = new HashSet<string>(b.Watchers, StringComparer.Ordinal);
        var away = new List<string>();
        foreach (var nick in b.Seats)
        {
            if (string.IsNullOrEmpty(nick)) continue;
            foreach (var conn in connectionsOf(nick))
                if (inGroup.Add(conn)) away.Add(conn);   // Add — заразом і дубль між двома місцями одного ніка
        }
        if (away.Count > 0) yield return new Send(new ToConnections(away), "anthem", body);
    }

    /// <summary>
    /// Хто сидить — отримує свій вид на своє місце персонально (у Hidden-іграх він в інших і не такий);
    /// решта групи бачить вид глядача одним повідомленням. Гравець, який зараз не дивиться на кімнату,
    /// не отримує нічого — він на іншій вкладці, і це правильно.
    /// </summary>
    static IEnumerable<Send> ViewSends(RoomBroadcast b, Func<string, string?> nickOf, HashSet<string>? skip = null)
    {
        var seated = new Dictionary<int, List<string>>();
        foreach (var conn in b.Watchers)
        {
            if (skip is not null && skip.Contains(conn)) continue;
            if (b.SeatOf(nickOf(conn)) is not { } seat) continue;
            if (!seated.TryGetValue(seat, out var list)) seated[seat] = list = [];
            list.Add(conn);
        }
        foreach (var (seat, conns) in seated)
            yield return new Send(new ToConnections(conns), "room",
                new RoomView(b.Summary, seat, b.SeatViews.TryGetValue(seat, out var v) ? v : b.WatcherView));

        var mine = seated.Values.SelectMany(x => x).ToList();
        if (skip is not null) mine.AddRange(skip);
        yield return new Send(new ToGroupExcept(RoomGroup(b.RoomId), mine), "room", new RoomView(b.Summary, null, b.WatcherView));
    }
}
