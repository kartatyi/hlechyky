namespace Hlechyky.Games;

/// <summary>
/// Заклики за стіл. Досі про новий стіл нагадували тост на десять секунд і рядок у Журналі, а Журнал — окрема вкладка:
/// хто відволікся, той пропустив, і покликати вдруге чи покликати когось конкретного не було як. Тепер є особистий
/// заклик одній людині (<see cref="Invite"/> — кнопка «📣 Покликати», <see cref="Command"/> — /клич) і «покликати всіх
/// ще раз» (<see cref="Again"/>). Перевірки й паузи живуть тут, а не в хабі, щоб їх можна було перевірити тестом без
/// SignalR; розсилає, як і все в іграх, Broadcaster.
/// </summary>
/// <param name="rings">«Свій дзвінок» того, хто кличе особисто (Лавка); null — заклики без дзвінків (тести, сервер без Лавки).</param>
public sealed class Calls(Rooms rooms, Presence presence, IClock clock, IRings? rings = null)
{
    /// <summary>Той самий — тому самому не частіше: двічі поспіль «📣» в одну людину — це вже не заклик, а стукіт у шибку.</summary>
    public static readonly TimeSpan PairGap = TimeSpan.FromSeconds(30);
    /// <summary>Скільки особистих закликів один нік розсилає за хвилину — щоб «📣» не пройшовся по всіх, хто онлайн.</summary>
    public const int PerMinute = 6;
    /// <summary>«Покликати всіх ще раз» — не частіше на стіл. Створення столу теж рахується закликом.</summary>
    public static readonly TimeSpan AgainGap = TimeSpan.FromSeconds(120);

    static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    /// <summary>Скільки ніків і пар пам'ятати, перш ніж забути тих, хто давно нікого не кликав.</summary>
    const int PruneAt = 256;

    const string Alone = "Тут гра на одного — гукати нема кого";
    const string Going = "Партія вже йде — сісти нема куди";
    const string Whom = "Кого гукнути?";
    const string Self = "Себе гукати не треба — ти вже тут";
    const string Nameless = "Гостя без імені не покличеш — хай спершу назветься";
    const string NoTable = "Спершу сідай за стіл — тоді буде куди гукати";

    readonly object _lock = new();
    /// <summary>Коли кожен нік кликав особисто за останню хвилину (ключ — <see cref="Rooms.NickKey"/>).</summary>
    readonly Dictionary<string, Queue<DateTimeOffset>> _sent = new(StringComparer.Ordinal);
    /// <summary>Коли хто кого кликав востаннє (обидва — ключі ніків).</summary>
    readonly Dictionary<(string By, string To), DateTimeOffset> _pairs = [];

    // ---------- заклик усім ----------

    /// <summary>
    /// Заклик усім, хто зараз на сайті: подія <c>invite</c> (тост «Влад кличе в мафію» з кнопкою «Сісти») і рядок-заклик
    /// у Балачках — його побачить і той, хто зараз не в «Іграх» і тост пропустив. Кличуть обоє: Rooms.Create і <see cref="Again"/>.
    /// </summary>
    internal static void Everyone(Outbox outbox, string roomId, GameInfo info, string by, DateTimeOffset at)
    {
        var into = Into(info.Accusative);
        outbox.Add(new Invite(roomId, by, $"{by} кличе {into}"));
        outbox.Add(new InviteLine(roomId, by, $"кличе {into}", at));
    }

    /// <summary>
    /// «Покликати всіх ще раз» — тим, хто сидить за столом, що чекає гравців (лобі з вільним місцем). Раз на
    /// <see cref="AgainGap"/> на стіл, рахуючи від створення: інакше кнопка стала б сиреною.
    /// </summary>
    public RoomOutcome Again(string by, string roomId)
    {
        if (!Rooms.Named(by)) return RoomOutcome.Fail(Say.NoNick);
        if (rooms.Find(roomId) is not { } room) return RoomOutcome.Fail(Say.NoRoom);
        var now = clock.UtcNow;
        lock (room.Sync)
        {
            if (Refusal(room, by, everyone: true) is { } no) return RoomOutcome.Fail(no);
            if (room.CalledAt is { } at && now - at < AgainGap) return RoomOutcome.Fail($"Щойно кликали — ще {Seconds(at + AgainGap - now)} с");
            room.CalledAt = now;
        }
        var outbox = new Outbox();
        Everyone(outbox, room.Id, room.Info, by, now);
        return new RoomOutcome(outbox, new RoomReply(true, "📣 Заклик полетів усім", room.Id));
    }

    // ---------- особистий заклик ----------

    /// <summary>
    /// Покликати одну людину (<paramref name="who"/>) за стіл <paramref name="roomId"/>, за яким сидить <paramref name="by"/>.
    /// Той, кого кличуть, має бути на сайті й ще не сидіти за цим столом. Тост і рядок у Балачках летять лише на його
    /// з'єднання (у базу рядок не лягає), а тому, хто кликав, — відповідь «📣 Заклик полетів: Оля».
    /// </summary>
    public RoomOutcome Invite(string by, string roomId, string who) => Personal(by, roomId, who).Outcome;

    /// <summary>
    /// /клич Оля — той самий особистий заклик, лише стіл обирає сервер (<see cref="TableFor"/>). Відповідь — як у команди
    /// чату: помилку бачить лише автор, а вдалий заклик несе особистий рядок для нього (kind <c>note</c>) і готову
    /// розсилку (<see cref="ChatCommands.Result.Out"/>), яку хаб розсилає сам.
    /// </summary>
    public ChatCommands.Result Command(string by, string who, string? here = null)
    {
        if (TableFor(by, here) is not { } id) return new(Error: NoTable);
        var (outcome, target, game) = Personal(by, id, who);
        if (!outcome.Reply.Ok || game is null) return new(Error: outcome.Reply.Message);
        return new(Text: $"📣 Заклик {Into(game.Accusative, afterConsonant: true)} полетів: {target}", Kind: "note", Out: outcome.Out);
    }

    /// <summary>
    /// За який стіл кличе /клич: за той, у балачці якого це написали (<paramref name="here"/>), якщо автор за ним сидить;
    /// інакше — за найсвіжіший із тих, де він сидить і куди ще можна сісти; а коли таких нема — за найсвіжіший, де сидить
    /// (хай тоді <see cref="Invite"/> сам пояснить, чому туди не покличеш). null — автор ні за яким столом не сидить.
    /// </summary>
    public string? TableFor(string by, string? here = null)
    {
        var mine = rooms.Snapshot()
            .Where(r => r.MaxPlayers > 1 && r.Seats.Any(s => Same(s.Nick, by)))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
        return (mine.Find(r => r.Id == here) ?? mine.Find(Open) ?? mine.FirstOrDefault())?.Id;
    }

    /// <summary>Куди ще можна сісти: партія не йде і місце є (дограний стіл із місцем новенький відкриє наново).</summary>
    static bool Open(RoomSummary r) => r.Status != "playing" && r.Seats.Any(s => s.Nick is null);

    (RoomOutcome Outcome, string? Target, GameInfo? Game) Personal(string by, string roomId, string who)
    {
        static (RoomOutcome, string?, GameInfo?) No(string message) => (RoomOutcome.Fail(message), null, null);

        if (!Rooms.Named(by)) return No(Say.NoNick);
        if (rooms.Find(roomId) is not { } room) return No(Say.NoRoom);
        lock (room.Sync)
            if (Refusal(room, by, everyone: false) is { } no) return No(no);

        var name = Clean(who);
        if (name.Length == 0) return No(Whom);
        var target = Online(name);
        if (Same(target ?? name, by)) return No(Self);
        if (target is null) return No($"{Auth.CleanNick(name)} зараз не на сайті");
        if (!Rooms.Named(target)) return No(Nameless);
        lock (room.Sync)
            if (room.Has(target)) return No($"{target} вже за цим столом");

        var now = clock.UtcNow;
        lock (_lock)
        {
            var pair = (By: Rooms.NickKey(by), To: Rooms.NickKey(target));
            if (_pairs.TryGetValue(pair, out var last) && now - last < PairGap)
                return No($"{target} вже має твій заклик — ще {Seconds(last + PairGap - now)} с");
            var sent = Recent(pair.By, now);
            if (sent.Count >= PerMinute) return No($"Забагато закликів поспіль — ще {Seconds(sent.Peek() + Minute - now)} с");
            // Паузу й ліміт рахуємо лише вдалим закликам: «Оля зараз не на сайті» нічого не коштує.
            if (_pairs.Count >= PruneAt || _sent.Count >= PruneAt) Prune(now);
            _pairs[pair] = now;
            sent.Enqueue(now);
            _sent[pair.By] = sent;
        }

        var into = Into(room.Info.Accusative);
        var outbox = new Outbox();
        outbox.Add(new Invite(room.Id, by, $"{by} кличе тебе {into}", target, RingOf(by)));
        outbox.Add(new InviteLine(room.Id, by, $"кличе тебе {into}", now, target));
        return (new RoomOutcome(outbox, new RoomReply(true, $"📣 Заклик полетів: {target}", room.Id)), target, room.Info);
    }

    /// <summary>Дзвінок того, хто кличе. Зламана база Лавки коштує лише дзвінка — заклик однаково летить.</summary>
    InviteRing? RingOf(string by)
    {
        try { return rings?.RingOf(by); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Чому <paramref name="by"/> зараз не покличе за цей стіл; null — можна. Кликати має сенс лише туди, куди можна сісти, —
    /// так, як це вирішує <see cref="Rooms.Join"/>: лобі з вільним місцем або дограний стіл із місцем (новенький відкриє
    /// його наново). «Ще раз усім» (<paramref name="everyone"/>) — лише з лобі: дограний стіл на всю округу не кличе.
    /// Під замком кімнати.
    /// </summary>
    static string? Refusal(Room room, string by, bool everyone)
    {
        if (!room.Has(by)) return Say.NotPlaying;
        if (room.Info.Solo || room.Info.Private) return Alone;
        if (room.Status == RoomStatus.Playing) return Going;
        if (everyone && room.Status == RoomStatus.Finished) return Say.Played;
        return room.FreeSeat < 0 ? Say.NoSeats : null;
    }

    /// <summary>
    /// Кого з тих, хто зараз на сайті, мають на увазі: «оля» — це «Оля»; «Оля, давай до нас» — теж «Оля» (найдовший нік,
    /// яким рядок починається: у /клич після ніка часто дописують слово-друге); «Петро» — «гість Петро», коли Петра з
    /// акаунтом зараз нема. null — такого на сайті нема.
    /// </summary>
    string? Online(string name)
    {
        var online = presence.Online;
        return online.FirstOrDefault(n => Same(n, name))
            ?? online.Where(n => Rooms.Named(n) && Starts(name, n)).MaxBy(n => n.Length)
            ?? online.FirstOrDefault(n => Same(n, Auth.GuestPrefix + name));
    }

    /// <summary>Рядок починається з ніка, а далі — не продовження слова: «Оля, давай» — так, «Олянка» — ні.</summary>
    static bool Starts(string text, string nick) =>
        text.Length > nick.Length && text.StartsWith(nick, StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(text[nick.Length]);

    /// <summary>Нік із того, що прийшло: без «@» попереду (так людей згадують у Балачках) і без зайвих пробілів.</summary>
    static string Clean(string? who) =>
        string.Join(' ', (who ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimStart('@').Trim();

    static bool Same(string? a, string? b) => a is not null && b is not null && Rooms.NickKey(a) == Rooms.NickKey(b);

    /// <summary>Особисті заклики ніка за останню хвилину: старші випадають. Під <see cref="_lock"/>.</summary>
    Queue<DateTimeOffset> Recent(string key, DateTimeOffset now)
    {
        if (!_sent.TryGetValue(key, out var sent)) return new Queue<DateTimeOffset>();
        while (sent.Count > 0 && now - sent.Peek() >= Minute) sent.Dequeue();
        return sent;
    }

    /// <summary>Забути тих, хто давно нікого не кликав: для них ні пауза, ні ліміт уже нічого не важать. Під <see cref="_lock"/>.</summary>
    void Prune(DateTimeOffset now)
    {
        foreach (var key in _sent.Where(p => p.Value.Count == 0 || now - p.Value.Last() >= Minute).Select(p => p.Key).ToList())
            _sent.Remove(key);
        foreach (var pair in _pairs.Where(p => now - p.Value >= PairGap).Select(p => p.Key).ToList())
            _pairs.Remove(pair);
    }

    /// <summary>Скільки секунд ще чекати — вгору: «ще 0 с» ніхто не прочитає як «ще трохи».</summary>
    static int Seconds(TimeSpan left) => Math.Max(1, (int)Math.Ceiling(left.TotalSeconds));

    /// <summary>
    /// «в мафію», «у «Свою гру»»: прийменник за милозвучністю. Перед в, ф і сполученнями льв, св, тв, хв — завжди «у»
    /// (не «в «Вгадай мелодію»»); після приголосного перед приголосним (<paramref name="afterConsonant"/>) — теж «у»
    /// («заклик у мафію»); решта — «в» («кличе в мафію», «заклик в ерудит»). Публічне — для тестів.
    /// </summary>
    public static string Into(string accusative, bool afterConsonant = false)
    {
        var head = accusative.TrimStart('«', '"', '„', '\'').ToLowerInvariant();
        var u = head.StartsWith('в') || head.StartsWith('ф')
            || head.StartsWith("льв", StringComparison.Ordinal) || head.StartsWith("св", StringComparison.Ordinal)
            || head.StartsWith("тв", StringComparison.Ordinal) || head.StartsWith("хв", StringComparison.Ordinal)
            || (afterConsonant && head.Length > 0 && !Vowels.Contains(head[0]));
        return (u ? "у " : "в ") + accusative;
    }

    const string Vowels = "аеєиіїоуюя";
}
