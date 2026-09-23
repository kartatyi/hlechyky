using System.Collections.Concurrent;
using System.Text.Json;
using Hlechyky.Games;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Hlechyky;

public sealed class RadioHub(Presence presence, RadioEngine engine, Db db, Rooms rooms, Broadcaster broadcaster, IClock clock, RateGate rates, DjBrain brain, Tournament tournament, ChatFlood flood, Curfew curfew) : Hub
{
    static readonly HashSet<string> Emojis = ["🔥", "❤️", "😂", "🕺", "🤘", "😴", "🤮", "🫠"];
    static readonly ConcurrentDictionary<string, DateTime> LastReaction = new();
    static readonly ConcurrentDictionary<string, DateTime> LastCommand = new();
    static readonly ConcurrentDictionary<string, DateTime> LastLike = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Коли з'єднання востаннє казало «пишу…» — частіше за <see cref="TypingGapMs"/> не пересилаємо.</summary>
    static readonly ConcurrentDictionary<string, DateTime> LastTyping = new();
    const int TypingGapMs = 1500;
    /// <summary>Скільки старіших повідомлень віддавати за раз, коли людина гортає балачки вгору.</summary>
    const int OlderBatch = 60;

    public override async Task OnConnectedAsync()
    {
        // Нік уже порахував Auth: з сесії — для акаунта, з ?nick= і з приставкою «гість » — для решти.
        var http = Context.GetHttpContext();
        var nick = http is null ? Auth.Guest : Auth.Nick(http);
        presence.Set(Context.ConnectionId, nick);
        rooms.NoteOnline(nick);
        if (http is not null && Auth.IsUser(http)) db.TouchAccount(nick);
        await Clients.Caller.SendAsync("chatHistory", db.RecentChat(100, 120));
        // Лобі не має ціни підключення: якщо знімок чомусь не склався, людина все одно заходить слухати.
        List<RoomSummary> lobby;
        try { lobby = rooms.Snapshot(); }
        catch (Exception) { lobby = []; }
        await Clients.Caller.SendAsync("rooms", lobby);
        // Хто зараз у своїй соло-грі — теж одразу, а не з першою зміною: плитки в лобі мають знати це з порога.
        try { await Clients.Caller.SendAsync("solo", rooms.SoloNow()); } catch (Exception) { /* так само не привід не пустити */ }
        await Clients.All.SendAsync("state", engine.Snapshot());
        try { await Clients.Caller.SendAsync("tournament", tournament.Snapshot()); } catch (Exception) { /* турнір — не привід не пустити */ }
        tournament.PresenceChanged();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var gone = presence.Get(Context.ConnectionId);
        presence.Remove(Context.ConnectionId);
        var left = rooms.DropWatcher(Context.ConnectionId);
        rates.Forget(Context.ConnectionId);
        LastTyping.TryRemove(Context.ConnectionId, out _);
        // Місце тримається ще grace-час: F5 і провал зв'язку в метро не мають коштувати партії.
        if (gone is not null && !presence.IsOnline(gone)) rooms.NoteOffline(gone, clock.UtcNow);
        await Clients.All.SendAsync("state", engine.Snapshot());
        // Закрив вкладку з відкритим Гончарним колом — з плиток лобі його ім'я теж зникає.
        await broadcaster.FlushAsync(left);
        tournament.PresenceChanged();
    }

    /// <summary>Повертає текст помилки тому, хто писав (нікому більше), або null, якщо все гаразд.</summary>
    public Task<string?> SendChat(string text) => Say(text, null);

    /// <summary>Відповідь на повідомлення <paramref name="replyTo"/>. Те саме, що SendChat, лише з цитатою.</summary>
    public Task<string?> SendReply(string text, long replyTo) => Say(text, replyTo);

    /// <summary>
    /// ❤ на повідомленні (ще раз — зняти). Усі отримують «chatLikes» зі свіжим списком тих, хто лайкнув.
    /// Повертає текст помилки тому, хто тиснув, або null.
    /// </summary>
    public async Task<string?> LikeChat(long id)
    {
        var nick = Nick();
        var now = DateTime.UtcNow;
        // подвійний клік і дрібний спам: одна зміна на ніка за 250 мс
        if (LastLike.TryGetValue(nick, out var last) && (now - last).TotalMilliseconds < 250) return null;
        LastLike[nick] = now;
        if (db.ToggleChatLike(id, nick) is not { } likes) return "Це повідомлення не лайкнути";
        await Clients.All.SendAsync("chatLikes", new { id, likes });
        return null;
    }

    async Task<string?> Say(string text, long? replyTo)
    {
        text = Cut(text);
        if (text.Length == 0) return null;
        var nick = Nick();
        var (chatText, kind) = (text, "chat");
        if (text.StartsWith('/'))
        {
            if (text.StartsWith("/пароль", StringComparison.OrdinalIgnoreCase)) return CommandTooFast(nick) ?? ResetPassword(text);
            var r = Command(nick, text, rooms.LiveIds);
            if (r.Error is not null) return r.Error;
            // /столи — погляд у лобі, не виходячи з балачок: відповідь бачить лише той, хто спитав, і в базу
            // вона не лягає. Самі столи браузер уже має з події rooms, тож звідси йдуть тільки їхні id.
            if (r.Rooms is { } tables)
            {
                await Clients.Caller.SendAsync("chat",
                    new { id = 0L, nick, text = r.Text, at = DateTimeOffset.UtcNow, kind = r.Kind, rooms = tables });
                return null;
            }
            (chatText, kind) = (r.Text!, r.Kind);
        }
        if (flood.Check(nick, text, clock.UtcNow) is { } tooMuch) return tooMuch;
        // Відповідь має сенс лише для звичайної репліки: кубик чи монетка «у відповідь» — це вже просто кубик.
        await Clients.All.SendAsync("chat", db.AddChat(nick, chatText, kind, replyTo: kind == "chat" ? replyTo : null));
        if (kind == "chat") brain.OnChat(nick, chatText); // Глек вирішить сам, чи це до нього; кубик не його справа
        return null;
    }

    /// <summary>
    /// Репліка в балачці столу <paramref name="roomId"/>: чують ті, хто за ним сидить чи дивиться. Кубик і монетка
    /// тут теж працюють — вирішити, хто ходить першим, найчастіше треба саме за столом. Помилку (не за столом,
    /// мертві мовчать, флуд) бачить лише той, хто писав.
    /// </summary>
    public async Task<string?> TableSay(string roomId, string text)
    {
        text = Cut(text);
        if (text.Length == 0) return null;
        var nick = Nick();
        var (said, kind) = (text, "chat");
        if (text.StartsWith('/'))
        {
            // null замість столів: /столи тут ні до чого, стіл і так перед очима
            var r = Command(nick, text, null);
            if (r.Error is not null) return r.Error;
            (said, kind) = (r.Text!, r.Kind);
        }
        // Спершу — чи можна тут говорити взагалі (не за столом, мертві мовчать): на це флуд-лічильник не витрачаємо.
        if (rooms.TalkRefusal(roomId ?? "", Context.ConnectionId, nick) is { } why) return why;
        if (flood.Check(nick, text, clock.UtcNow, "table:" + roomId) is { } tooMuch) return tooMuch;
        var (outbox, error) = rooms.TableSay(roomId ?? "", Context.ConnectionId, nick, said, kind);
        if (error is not null) return error;
        await broadcaster.FlushAsync(outbox);
        return null;
    }

    /// <summary>
    /// «Оля пише…». Браузер шле це раз на кілька секунд, поки людина набирає; <paramref name="roomId"/> — балачка
    /// столу (чують лише ті, хто на нього дивиться), null — загальні Балачки. Відповіді нема: це не більше ніж натяк.
    /// </summary>
    public async Task Typing(string? roomId)
    {
        var now = DateTime.UtcNow;
        if (LastTyping.TryGetValue(Context.ConnectionId, out var last) && (now - last).TotalMilliseconds < TypingGapMs) return;
        LastTyping[Context.ConnectionId] = now;
        var nick = Nick();
        if (nick == Auth.Guest) return;   // безіменному «гостю» нема кого показувати
        if (string.IsNullOrEmpty(roomId))
        {
            await Clients.Others.SendAsync("typing", new { nick, room = (string?)null });
            return;
        }
        // Хто за столом говорити не може (мертвий у мафії, чужий), той і «пише…» не світить.
        if (rooms.TalkRefusal(roomId, Context.ConnectionId, nick) is not null) return;
        await Clients.OthersInGroup(Broadcaster.RoomGroup(roomId)).SendAsync("typing", new { nick, room = roomId });
    }

    /// <summary>
    /// Старіші повідомлення — коли людина гортає вгору Балачки (або Журнал, <paramref name="log"/>). null — «не зараз»
    /// (квота на секунду вичерпана), а не «історія скінчилась»: порожній список браузер читає як «далі нічого нема».
    /// </summary>
    public List<ChatMessage>? ChatBefore(long beforeId, bool log) =>
        !Allow(input: false) ? null : beforeId <= 1 ? [] : db.ChatBefore(beforeId, OlderBatch, log);

    /// <summary>Обрізаємо по символах, але не посеред смайла: у .NET він займає дві клітинки рядка.</summary>
    static string Cut(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length <= 500 ? t : t[..(char.IsHighSurrogate(t[499]) ? 499 : 500)];
    }

    /// <summary>Команди — не частіше ніж раз на 1,2 с від ніка. null — можна.</summary>
    static string? CommandTooFast(string nick) =>
        LastCommand.TryGetValue(nick, out var last) && (DateTime.UtcNow - last).TotalMilliseconds < 1200 ? "Не так швидко" : null;

    /// <summary>Кубик, монетка, куля, /столи. На друкарську помилку паузу не вішаємо — лише на вдалу команду.</summary>
    static ChatCommands.Result Command(string nick, string text, Func<IReadOnlyList<string>>? live)
    {
        if (CommandTooFast(nick) is { } slow) return new(Error: slow);
        var r = ChatCommands.Run(text, live);
        if (r.Error is null) LastCommand[nick] = DateTime.UtcNow;
        return r;
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

    /// <summary>Вкладка каже, що її плеєр грає чи замовк: так рейтинг знає, хто саме слухав трек.</summary>
    public async Task SetListening(bool on)
    {
        // Решта кімнати бачить, хто саме зараз у навушниках.
        if (presence.SetListening(Context.ConnectionId, on)) await Clients.All.SendAsync("state", engine.Snapshot());
    }

    /// <summary>Гість перейменовується як хоче (приставка лишається); в акаунта нік один, і зміна — це вже інший вхід.</summary>
    public async Task SetNick(string nick)
    {
        var http = Context.GetHttpContext();
        var fresh = http is not null && Auth.IsUser(http) ? Auth.Me(http)!.Nick : Auth.GuestNick(nick);
        var old = presence.Get(Context.ConnectionId);
        if (old == fresh) return;
        presence.Set(Context.ConnectionId, fresh);
        rooms.NoteOnline(Nick());
        await Clients.All.SendAsync("state", engine.Snapshot());
        // Свідома зміна ніка — це те саме, що встати з-за столу: grace тут ні до чого.
        if (old is not null && !presence.IsOnline(old)) await broadcaster.FlushAsync(rooms.DropNick(old));
    }

    // ---------- «Вгадай мелодію» ----------

    /// <summary>
    /// 👎 треку: у «Вгадай мелодію» він (і та сама пісня з інших завантажень) більше не трапиться. Ще раз — зняти.
    /// Відповідь: <c>{ ok, disliked, total, message }</c>.
    /// </summary>
    public object MelodyDislike(string trackId)
    {
        if (!Allow(input: false)) return new { ok = false, disliked = false, total = 0, message = Games.Say.TooFast };
        if (string.IsNullOrWhiteSpace(trackId) || VoiceService.IsVoice(trackId)) return new { ok = false, disliked = false, total = 0, message = "Нема такого треку" };
        var result = db.ToggleMelodyDislike(trackId, Nick());
        return result is { } r
            ? new { ok = true, disliked = r.Mine, total = r.Total, message = r.Mine ? "👎 Більше не трапиться у «Вгадай мелодію»" : "Дизлайк знято" }
            : new { ok = false, disliked = false, total = 0, message = "Нема такого треку" };
    }

    // ---------- турнір ----------
    // Кожен метод повертає текст відмови тому, хто тиснув, або null; зміни всім розсилає сам Tournament.

    public Task<string?> TournamentCreate(string[] games) => Lead(() => tournament.Create(Nick(), games));
    public Task<string?> TournamentJoin() => Lead(() => tournament.Join(Nick()));
    public Task<string?> TournamentLeave() => Lead(() => tournament.Leave(Nick()));
    public Task<string?> TournamentNext() => Lead(() => tournament.Next(Nick()));
    public Task<string?> TournamentSkip() => Lead(() => tournament.Skip(Nick()));
    public Task<string?> TournamentCancel() => Lead(() => tournament.Cancel(Nick()));

    Task<string?> Lead(Func<string?> action) =>
        Task.FromResult(Allow(input: false) ? action() : Games.Say.TooFast);

    // ---------- ігри (PROTOCOL §1) ----------

    /// <summary>
    /// Опції приходять сирим JSON: PROTOCOL §1 обіцяє <c>stake</c> числом, а <c>Dictionary&lt;string, string&gt;</c>
    /// на <c>{"stake": 5}</c> просто впав би при прив'язці аргументів.
    /// </summary>
    public Task<RoomReply> CreateRoom(string gameId, Dictionary<string, JsonElement>? options) =>
        Play(gameId, () => rooms.Create(Nick(), gameId ?? "", RoomOptions.From(options)));

    public async Task<RoomReply> OpenSolo(string gameId, string? key)
    {
        var reply = await Play(gameId, () => rooms.OpenSolo(Nick(), gameId ?? "", key));
        // Особиста кімната нікуди не «видно»: щоб гравець одразу побачив свій вид, підписуємо його самі.
        if (reply.Ok && reply.RoomId is { } id) await WatchRoom(id);
        return reply;
    }

    public Task<RoomReply> JoinRoom(string roomId) => Play(GameOf(roomId), () => rooms.Join(roomId ?? "", Nick()));

    public Task<RoomReply> LeaveRoom(string roomId) => Act(() => rooms.Leave(roomId ?? "", Nick()));

    public Task<RoomReply> StartRoom(string roomId) => Play(GameOf(roomId), () => rooms.StartByHost(roomId ?? "", Nick()));

    public Task<RoomReply> Rematch(string roomId) => Play(GameOf(roomId), () => rooms.Rematch(roomId ?? "", Nick()));

    public Task<RoomReply> Act(string roomId, string action, JsonElement payload) =>
        NightAt(roomId) is { } night ? Task.FromResult(RoomReply.Fail(night))
            : Act(() => rooms.Act(roomId ?? "", Nick(), action ?? "", payload));

    /// <summary>Реалтайм-ввід. Відповіді нема: наступний кадр і так намалює, що вийшло.</summary>
    public async Task Input(string roomId, string action, JsonElement payload)
    {
        if (!Allow(input: true)) return;   // зайве мовчки викидаємо, скаржитись тут нема на що
        if (NightAt(roomId) is not null) return;   // під відбоєм теж мовчки: плашка на сайті вже все сказала
        await broadcaster.FlushAsync(rooms.Input(roomId ?? "", Nick(), action ?? "", payload));
    }

    /// <summary>Види й кадри летять лише тим, хто на цю кімнату дивиться.</summary>
    public async Task WatchRoom(string roomId)
    {
        if (!Allow(input: true)) return;   // підписка теж коштує розсилки, тож і вона під квотою
        var id = roomId ?? "";
        var outbox = rooms.Watch(id, Context.ConnectionId, Nick());
        if (outbox.Count == 0) return;   // кімнати нема або вона чужа приватна — мовчки нічого
        await Groups.AddToGroupAsync(Context.ConnectionId, Broadcaster.RoomGroup(id));
        // Розмову столу знімаємо вже з групою: так між знімком і підпискою не загубиться жоден рядок.
        if (rooms.TalkHistory(id, Context.ConnectionId) is { } history) outbox.Add(history);
        await broadcaster.FlushAsync(outbox);
    }

    public async Task UnwatchRoom(string roomId)
    {
        if (!Allow(input: true)) return;
        rooms.Unwatch(roomId ?? "", Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Broadcaster.RoomGroup(roomId ?? ""));
    }

    /// <summary>
    /// Яка кімната зараз на екрані цієї вкладки (null — жодна). Так решта бачить, хто саме зараз у своїй соло-грі
    /// (подія <c>solo</c>): браузер шле це сам, коли людина відкриває гру, іде в лобі чи надовго ховає вкладку.
    /// </summary>
    public async Task FocusRoom(string? roomId)
    {
        if (!Allow(input: true)) return;   // кожна зміна — розсилка всім, тож теж під квотою
        await broadcaster.FlushAsync(rooms.Focus(Context.ConnectionId, Nick(), roomId));
    }

    /// <summary>Нічний відбій (Curfew): текст відмови цьому гравцеві в грі <paramref name="gameId"/>, null — можна грати.</summary>
    string? NightFor(string? gameId) => curfew.Refusal(Nick(), Context.GetHttpContext(), gameId);

    /// <summary>Хід за столом під відбоєм: відмова, крім партії, яку почали ще до ночі з іншими людьми.</summary>
    string? NightAt(string? roomId) => curfew.MoveRefusal(Nick(), Context.GetHttpContext(), rooms.Find(roomId));

    /// <summary>У яку гру грають за цим столом (null — столу нема).</summary>
    string? GameOf(string? roomId) => rooms.Find(roomId)?.Info.Id;

    /// <summary>Усе, з чого гра починається (стіл, соло, сісти, почати, ще раз): під відбоєм — одразу відмова.</summary>
    Task<RoomReply> Play(string? gameId, Func<RoomOutcome> action) =>
        NightFor(gameId) is { } night ? Task.FromResult(RoomReply.Fail(night)) : Act(action);

    async Task<RoomReply> Act(Func<RoomOutcome> action)
    {
        if (!Allow(input: false)) return RoomReply.Fail(Games.Say.TooFast);
        var outcome = action();
        await broadcaster.FlushAsync(outcome.Out);
        return outcome.Reply;
    }

    /// <summary>Квота на секунду з одного з'єднання: десять дій, тридцять вводів (див. <see cref="RateGate"/>).</summary>
    bool Allow(bool input) => rates.Allow(Context.ConnectionId, input, clock.UtcNow.ToUnixTimeSeconds());

    /// <summary>
    /// /пароль Влад новий123 — адмін ставить людині новий пароль, коли та свій забула. Відповідь бачить
    /// лише адмін, у базу не лягає; старі сесії того акаунта одразу гаснуть (у куці — сіль пароля).
    /// </summary>
    string ResetPassword(string text)
    {
        var http = Context.GetHttpContext();
        if (http is null || !Auth.IsAdmin(http)) return "Пароль міняє лише адмін";
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return "Так: /пароль нік новий_пароль";
        var password = parts[^1];
        var nick = string.Join(' ', parts[1..^1]);
        if (password.Length < Auth.PasswordMin) return $"Пароль — хоча б {Auth.PasswordMin} символів";
        if (db.FindAccount(nick) is not { } a) return $"Акаунта «{nick}» нема";
        db.SetAccountPassword(a.Nick, Auth.HashPassword(password, out var salt), salt);
        return $"Пароль для «{a.Nick}» змінено";
    }

    string Nick() => presence.Get(Context.ConnectionId) ?? Auth.Guest;
}
