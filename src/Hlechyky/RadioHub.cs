using System.Collections.Concurrent;
using System.Text.Json;
using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hlechyky;

public sealed class RadioHub(Presence presence, RadioEngine engine, Db db, Rooms rooms, Broadcaster broadcaster, IClock clock, DjBrain brain) : Hub
{
    static readonly HashSet<string> Emojis = ["🔥", "❤️", "😂", "🕺", "🤘", "😴", "🤮", "🫠"];
    static readonly ConcurrentDictionary<string, DateTime> LastReaction = new();
    static readonly ConcurrentDictionary<string, DateTime> LastCommand = new();

    /// <summary>Скільки дій і скільки реалтайм-вводу можна на секунду з одного з'єднання (ARCHITECTURE §9).</summary>
    const int ActsPerSecond = 10, InputsPerSecond = 30;
    static readonly ConcurrentDictionary<string, (long Second, int Acts, int Inputs)> Rates = new();

    public override async Task OnConnectedAsync()
    {
        var nick = Auth.SanitizeNick(Context.GetHttpContext()?.Request.Query["nick"].ToString());
        presence.Set(Context.ConnectionId, nick);
        rooms.NoteOnline(nick);
        await Clients.Caller.SendAsync("chatHistory", db.RecentChat(100, 120));
        await Clients.Caller.SendAsync("rooms", rooms.Snapshot());
        await Clients.All.SendAsync("state", engine.Snapshot());
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var gone = presence.Get(Context.ConnectionId);
        presence.Remove(Context.ConnectionId);
        rooms.DropWatcher(Context.ConnectionId);
        Rates.TryRemove(Context.ConnectionId, out _);
        // Місце тримається ще grace-час: F5 і провал зв'язку в метро не мають коштувати партії.
        if (gone is not null && !presence.IsOnline(gone)) rooms.NoteOffline(gone, clock.UtcNow);
        await Clients.All.SendAsync("state", engine.Snapshot());
    }

    /// <summary>Повертає текст помилки тому, хто писав (нікому більше), або null, якщо все гаразд.</summary>
    public async Task<string?> SendChat(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return null;
        // Обрізаємо по символах, але не посеред смайла: у .NET він займає дві клітинки рядка.
        if (text.Length > 500) text = text[..(char.IsHighSurrogate(text[499]) ? 499 : 500)];
        var nick = Nick();
        var (chatText, kind) = (text, "chat");
        if (text.StartsWith('/'))
        {
            var now = DateTime.UtcNow;
            if (LastCommand.TryGetValue(nick, out var last) && (now - last).TotalMilliseconds < 1200) return "Не так швидко";
            var r = ChatCommands.Run(text);
            if (r.Error is not null) return r.Error;   // на друкарську помилку паузу не вішаємо
            LastCommand[nick] = now;
            (chatText, kind) = (r.Text!, r.Kind);
        }
        await Clients.All.SendAsync("chat", db.AddChat(nick, chatText, kind));
        if (kind == "chat") brain.OnChat(nick, chatText); // Глек вирішить сам, чи це до нього; кубик не його справа
        return null;
    }

    /// <summary>Emoji flying over the cover for everyone. Not persisted, lightly rate-limited per nick.</summary>
    public async Task React(string emoji)
    {
        if (!Emojis.Contains(emoji ?? "")) return;
        var nick = presence.Get(Context.ConnectionId) ?? "гість";
        var now = DateTime.UtcNow;
        if (LastReaction.TryGetValue(nick, out var last) && (now - last).TotalMilliseconds < 400) return;
        LastReaction[nick] = now;
        await Clients.All.SendAsync("reaction", new { nick, emoji });
    }

    public async Task SetNick(string nick)
    {
        var old = presence.Get(Context.ConnectionId);
        presence.Set(Context.ConnectionId, Auth.SanitizeNick(nick));
        rooms.NoteOnline(Nick());
        await Clients.All.SendAsync("state", engine.Snapshot());
        // Свідома зміна ніка — це те саме, що встати з-за столу: grace тут ні до чого.
        if (old is not null && !presence.IsOnline(old)) await broadcaster.FlushAsync(rooms.DropNick(old));
    }

    // ---------- ігри (PROTOCOL §1) ----------

    public Task<RoomReply> CreateRoom(string gameId, Dictionary<string, string>? options) =>
        Act(() => rooms.Create(Nick(), gameId ?? "", options));

    public async Task<RoomReply> OpenSolo(string gameId, string? key)
    {
        var reply = await Act(() => rooms.OpenSolo(Nick(), gameId ?? "", key));
        // Особиста кімната нікуди не «видно»: щоб гравець одразу побачив свій вид, підписуємо його самі.
        if (reply.Ok && reply.RoomId is { } id) await WatchRoom(id);
        return reply;
    }

    public Task<RoomReply> JoinRoom(string roomId) => Act(() => rooms.Join(roomId ?? "", Nick()));

    public Task<RoomReply> LeaveRoom(string roomId) => Act(() => rooms.Leave(roomId ?? "", Nick()));

    public Task<RoomReply> StartRoom(string roomId) => Act(() => rooms.StartByHost(roomId ?? "", Nick()));

    public Task<RoomReply> Rematch(string roomId) => Act(() => rooms.Rematch(roomId ?? "", Nick()));

    public Task<RoomReply> Act(string roomId, string action, JsonElement payload) =>
        Act(() => rooms.Act(roomId ?? "", Nick(), action ?? "", payload));

    /// <summary>Реалтайм-ввід. Відповіді нема: наступний кадр і так намалює, що вийшло.</summary>
    public async Task Input(string roomId, string action, JsonElement payload)
    {
        if (!Allow(input: true)) return;   // зайве мовчки викидаємо, скаржитись тут нема на що
        await broadcaster.FlushAsync(rooms.Input(roomId ?? "", Nick(), action ?? "", payload));
    }

    /// <summary>Види й кадри летять лише тим, хто на цю кімнату дивиться.</summary>
    public async Task WatchRoom(string roomId)
    {
        var id = roomId ?? "";
        var outbox = rooms.Watch(id, Context.ConnectionId, Nick());
        if (outbox.Count == 0) return;   // кімнати нема або вона чужа приватна — мовчки нічого
        await Groups.AddToGroupAsync(Context.ConnectionId, Broadcaster.RoomGroup(id));
        await broadcaster.FlushAsync(outbox);
    }

    public async Task UnwatchRoom(string roomId)
    {
        rooms.Unwatch(roomId ?? "", Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Broadcaster.RoomGroup(roomId ?? ""));
    }

    async Task<RoomReply> Act(Func<RoomOutcome> action)
    {
        if (!Allow(input: false)) return RoomReply.Fail(Games.Say.TooFast);
        var outcome = action();
        await broadcaster.FlushAsync(outcome.Out);
        return outcome.Reply;
    }

    /// <summary>Проста квота на секунду з одного з'єднання: десять дій, тридцять вводів.</summary>
    bool Allow(bool input)
    {
        var second = clock.UtcNow.ToUnixTimeSeconds();
        var allowed = true;
        Rates.AddOrUpdate(Context.ConnectionId,
            _ => (second, input ? 0 : 1, input ? 1 : 0),
            (_, old) =>
            {
                if (old.Second != second) return (second, input ? 0 : 1, input ? 1 : 0);
                if (input && old.Inputs >= InputsPerSecond) { allowed = false; return old; }
                if (!input && old.Acts >= ActsPerSecond) { allowed = false; return old; }
                return (second, old.Acts + (input ? 0 : 1), old.Inputs + (input ? 1 : 0));
            });
        return allowed;
    }

    string Nick() => presence.Get(Context.ConnectionId) ?? "гість";
}
