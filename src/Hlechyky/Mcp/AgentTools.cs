using System.Text.Json;
using Hlechyky.Games;

namespace Hlechyky.Mcp;

/// <summary>Рядок Балачок так, як його бачить агент.</summary>
public sealed record AgentChatLine(long Id, string Nick, string Text, string Kind, DateTimeOffset At);

/// <summary>Що вийшло з «сказати в Балачки».</summary>
public sealed record ChatSendResult(bool Ok, string Message, AgentChatLine? Line);

/// <summary>
/// Балачки з погляду агента. Окремим швом, бо за ними стоїть уся радіо-частина (база, хаб, команди),
/// а перевіряти інструменти треба без жодного з них.
/// </summary>
public interface IAgentChat
{
    IReadOnlyList<AgentChatLine> Recent(int limit);
    ChatSendResult Send(string nick, string text);
    /// <summary>Номер останнього рядка Балачок. Окремо від <see cref="Recent"/>: на це дивиться цикл очікування.</summary>
    long LastId();
}

/// <summary>Куди подіти розсилку, яку повернув каркас. У проді — Broadcaster, у тестах — нікуди.</summary>
public interface IAgentFlush
{
    Task FlushAsync(Outbox outbox);
}

/// <summary>Розсилка в нікуди: тести піднімають кімнати без SignalR.</summary>
public sealed class NoFlush : IAgentFlush
{
    public Task FlushAsync(Outbox outbox) => Task.CompletedTask;
}

/// <summary>
/// Село очима аі-агента. Один метод — один інструмент MCP; жодного HTTP і жодного JSON-RPC тут нема,
/// саме тому все це перевіряється звичайними тестами кімнат.
///
/// Головне правило те саме, що й для браузера: агент не бачить нічого, чого йому не дав <c>Game.View</c>.
/// Ролі, нічний чат і таємні голоси приходять рівно так, як людині за тим самим місцем.
/// </summary>
public sealed class AgentTools(Rooms rooms, Registry registry, IAgentChat chat, IAgentFlush flush, Db? db = null,
    ChatFlood? flood = null, IClock? clock = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Скільки чекає <c>wait</c>, якщо агент не сказав інакше, і скільки це може бути щонайбільше.</summary>
    public const int WaitDefaultMs = 15_000;
    public const int WaitMaxMs = 30_000;
    const int WaitPollMs = 250;

    // =========================================================================================
    // Хто я і що навколо
    // =========================================================================================

    public object Who(AgentSession s)
    {
        var room = MyRoom(s.Nick);
        return new
        {
            nick = s.Nick,
            session = s.Id,
            calls = s.Calls,
            myRoom = room?.Id,
            myGame = room?.Info.Id,
            mySeat = room?.SeatOf(s.Nick),
            note = string.IsNullOrEmpty(s.Nick)
                ? "Назвись через set_nick — без імені за стіл не пускають."
                : "Готовий грати. Столи — у list_rooms, правила гри — у rules.",
        };
    }

    public async Task<object> SetNick(AgentSession s, string? nick)
    {
        var clean = Auth.SanitizeNick(nick);
        if (clean == Auth.Guest) return Fail("Таке ім'я нічого не означає — вигадай своє");
        // Зареєстроване людиною ім'я — її, з паролем; агентові під ним не сісти.
        if (db?.FindAccount(clean) is not null) return Fail("Це ім'я вже зайняла людина — вигадай інше");
        // Нік — це те саме, що встати з-за столу: старе місце за старим ім'ям лишати не можна.
        if (!string.IsNullOrEmpty(s.Nick) && !string.Equals(s.Nick, clean, StringComparison.OrdinalIgnoreCase))
            await flush.FlushAsync(rooms.DropNick(s.Nick)).ConfigureAwait(false);
        s.Nick = clean;
        rooms.NoteOnline(clean);
        return new { ok = true, nick = clean };
    }

    public object GameList() => new
    {
        games = registry.Catalog.Select(g => new
        {
            id = g.Id,
            title = g.Title,
            group = g.Group,
            players = new { min = g.MinPlayers, max = g.MaxPlayers },
            start = g.Start,
            hint = g.Hint,
            options = g.Options.Select(o => new
            {
                key = o.Key,
                label = o.Label,
                @default = o.Default,
                multi = o.Multi,
                values = o.Values.Select(v => new { value = v[0], label = v[1] }),
            }),
        }),
    };

    public object RoomList(AgentSession s) => new
    {
        rooms = rooms.Snapshot().Select(r => new
        {
            id = r.Id,
            game = r.Game,
            status = r.Status,
            host = r.Host,
            round = r.Round,
            free = r.Seats.Count(x => x.Nick is null),
            players = r.Seats.Where(x => x.Nick is not null).Select(x => x.Nick),
            needs = r.MinPlayers,
            max = r.MaxPlayers,
            options = r.Options,
            mine = r.Seats.Any(x => string.Equals(x.Nick, s.Nick, StringComparison.OrdinalIgnoreCase)),
        }),
    };

    // =========================================================================================
    // Столи
    // =========================================================================================

    public Task<object> Create(AgentSession s, string? game, IReadOnlyDictionary<string, JsonElement>? options)
    {
        if (string.IsNullOrEmpty(s.Nick)) return Task.FromResult(NeedNick());
        if (string.IsNullOrEmpty(game)) return Task.FromResult(Fail("Скажи, у що граємо: створювати стіл наосліп нема сенсу"));
        return Run(s, () => rooms.Create(s.Nick, game, RoomOptions.From(options)));
    }

    /// <summary>Сісти за конкретний стіл або за перший вільний стіл названої гри.</summary>
    public Task<object> Join(AgentSession s, string? roomId, string? game)
    {
        if (string.IsNullOrEmpty(s.Nick)) return Task.FromResult(NeedNick());
        if (string.IsNullOrEmpty(roomId))
        {
            if (string.IsNullOrEmpty(game)) return Task.FromResult(Fail("Скажи, за який стіл сідати (room) або в яку гру (game)"));
            var open = rooms.Snapshot()
                .FirstOrDefault(r => string.Equals(r.Game, game, StringComparison.Ordinal)
                    && r.Status == "lobby" && r.Seats.Any(x => x.Nick is null));
            if (open is null) return Task.FromResult(Fail($"Вільних столів у грі «{game}» нема — постав свій через create_room"));
            roomId = open.Id;
        }
        var id = roomId;
        return Run(s, () => rooms.Join(id, s.Nick));
    }

    public Task<object> Leave(AgentSession s, string? roomId)
    {
        if (Which(s, roomId) is not { } id) return Task.FromResult(NoRoom());
        return Run(s, () => rooms.Leave(id, s.Nick));
    }

    public Task<object> StartGame(AgentSession s, string? roomId)
    {
        if (Which(s, roomId) is not { } id) return Task.FromResult(NoRoom());
        return Run(s, () => rooms.StartByHost(id, s.Nick));
    }

    public Task<object> Rematch(AgentSession s, string? roomId)
    {
        if (Which(s, roomId) is not { } id) return Task.FromResult(NoRoom());
        return Run(s, () => rooms.Rematch(id, s.Nick));
    }

    // =========================================================================================
    // Партія
    // =========================================================================================

    /// <summary>Що я бачу зі свого місця. Глядачеві дістається вид глядача — і нічого понад те.</summary>
    public object Look(AgentSession s, string? roomId)
    {
        if (Which(s, roomId) is not { } id) return NoRoom();
        if (Snap(s, id) is not { } snap) return Fail(Hlechyky.Games.Say.NoRoom);
        return snap;
    }

    public Task<object> Act(AgentSession s, string? roomId, string? action, JsonElement payload)
    {
        if (string.IsNullOrEmpty(s.Nick)) return Task.FromResult(NeedNick());
        if (Which(s, roomId) is not { } id) return Task.FromResult(NoRoom());
        if (string.IsNullOrEmpty(action)) return Task.FromResult(Fail("Не сказано, що робити"));
        return Run(s, () => rooms.Act(id, s.Nick, action, payload), id);
    }

    /// <summary>Хід мафії коротко: місце числом, решту допише сам інструмент.</summary>
    public Task<object> MafiaMove(AgentSession s, string? roomId, string action, int? seat, string? text)
    {
        var payload = action switch
        {
            "say" => JsonSerializer.SerializeToElement(new { text = text ?? "" }, Json),
            "vote" when seat is null => JsonSerializer.SerializeToElement(new { seat = (int?)null }, Json),
            _ => JsonSerializer.SerializeToElement(new { seat }, Json),
        };
        if (action != "say" && action != "vote" && seat is null)
            return Task.FromResult(Fail("Не сказано, на кого: передай seat (номер місця зі списку гравців)"));
        return Act(s, roomId, action, payload);
    }

    /// <summary>
    /// Дочекатись, поки в селі щось зміниться: новий вид, нова фаза, свіжий рядок у Балачках чи в балачці столу.
    /// Це головний інструмент агента: без нього довелось би крутити <c>look</c> у циклі й проґавити день.
    /// </summary>
    public async Task<object> Wait(AgentSession s, string? roomId, int? timeoutMs, CancellationToken ct)
    {
        if (Which(s, roomId) is not { } id) return NoRoom();
        var budget = Math.Clamp(timeoutMs ?? WaitDefaultMs, 1_000, WaitMaxMs);
        var deadline = DateTime.UtcNow.AddMilliseconds(budget);
        var started = DateTime.UtcNow;

        var before = Fingerprint(s, id);
        while (true)
        {
            if (ct.IsCancellationRequested) break;
            var now = Fingerprint(s, id);
            if (now is null) break;                       // стіл зник — хай агент подивиться сам
            if (!string.Equals(now, before, StringComparison.Ordinal)) break;
            if (DateTime.UtcNow >= deadline) break;
            try { await Task.Delay(WaitPollMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        var snap = Snap(s, id);
        var waited = (int)(DateTime.UtcNow - started).TotalMilliseconds;
        if (snap is null) return Fail(Hlechyky.Games.Say.NoRoom);
        return new { waited, changed = !string.Equals(Fingerprint(s, id), before, StringComparison.Ordinal), state = snap };
    }

    // =========================================================================================
    // Балачки
    // =========================================================================================

    /// <summary>Загальний чат села — для розмов про все. Гра за столом іде в балачці столу (<see cref="TableRead"/>).</summary>
    public object ChatRead(AgentSession s, int? limit, bool onlyNew)
    {
        var lines = chat.Recent(Math.Clamp(limit ?? 40, 1, 200));
        var fresh = onlyNew ? lines.Where(l => l.Id > s.SeenChatId).ToList() : [.. lines];
        if (lines.Count > 0) s.SeenChatId = Math.Max(s.SeenChatId, lines[^1].Id);
        return new { chat = fresh };
    }

    public object ChatSend(AgentSession s, string? text)
    {
        if (string.IsNullOrEmpty(s.Nick)) return NeedNick();
        var said = (text ?? "").Trim();
        if (said.Length == 0) return Fail("Порожнє нікому не цікаво");
        var r = chat.Send(s.Nick, said);
        // Курсор «докуди дочитав» тут не рухаємо: між останнім chat_read і цією реплікою могли написати інші, і
        // їхні рядки мають менші номери — з курсором на своєму рядку агент їх так і не побачив би.
        if (!r.Ok) return Fail(r.Message);
        return new { ok = true, line = r.Line };
    }

    // =========================================================================================
    // Балачка столу
    // =========================================================================================

    /// <summary>
    /// Балачка столу: що кажуть гравці й Глек-ведучий. Удень уся мафія відбувається саме тут. Бачить її той,
    /// хто за столом сидить (агентові без місця дивитись на стіл нема чим — у нього нема з'єднання).
    /// </summary>
    public object TableRead(AgentSession s, string? roomId, int? limit, bool onlyNew)
    {
        if (Which(s, roomId) is not { } id) return NoRoom();
        if (rooms.Find(id) is not { } room) return Fail(Hlechyky.Games.Say.NoRoom);
        if (!room.Talks) return Fail("У цього столу балачки нема — тут граєш сам");
        var all = rooms.TableLines(id);
        var tail = all.Skip(Math.Max(0, all.Count - Math.Clamp(limit ?? 40, 1, Rooms.TalkLines))).ToList();
        // Докуди дочитав — окремо для кожного столу: зазирнув за чужий стіл — своє непрочитане від цього не зникає.
        var seen = s.SeenTable.GetValueOrDefault(id);
        var fresh = onlyNew ? tail.Where(l => l.Id > seen).ToList() : tail;
        if (all.Count > 0) s.SeenTable[id] = Math.Max(seen, all[^1].Id);
        return new { room = id, table = fresh };
    }

    /// <summary>Сказати в балачку столу. Мертві в мафії мовчать — стіл сам не пустить.</summary>
    public async Task<object> TableSay(AgentSession s, string? roomId, string? text)
    {
        if (string.IsNullOrEmpty(s.Nick)) return NeedNick();
        if (Which(s, roomId) is not { } id) return NoRoom();
        var said = (text ?? "").Trim();
        if (said.Length == 0) return Fail("Порожнє нікому не цікаво");
        var kind = "chat";
        if (said.StartsWith('/'))
        {
            var r = ChatCommands.Run(said);
            if (r.Error is not null) return Fail(r.Error);
            (said, kind) = (r.Text!, r.Kind);
        }
        if (rooms.TalkRefusal(id, null, s.Nick) is { } why) return Fail(why);
        if (flood?.Check(s.Nick, text ?? "", (clock ?? SystemTime).UtcNow, "table:" + id) is { } tooMuch) return Fail(tooMuch);
        var (outbox, error) = rooms.TableSay(id, null, s.Nick, said, kind);
        if (error is not null) return Fail(error);
        await flush.FlushAsync(outbox).ConfigureAwait(false);
        // Курсор не рухаємо (див. ChatSend): чужі репліки, сказані поки агент думав, мають лишитись непрочитаними.
        var line = outbox.OfType<TableSaid>().Select(x => x.Line).FirstOrDefault();
        return new { ok = true, room = id, line };
    }

    static readonly IClock SystemTime = new SystemClock();

    // =========================================================================================
    // Довідка
    // =========================================================================================

    public object Help(string? gameId) => new
    {
        game = gameId ?? "mafia",
        text = string.Equals(gameId, "mafia", StringComparison.Ordinal) || string.IsNullOrEmpty(gameId)
            ? MafiaGuide
            : gameId == "svoya" ? SvoyaGuide
            : registry.Info(gameId ?? "") is { } info
                ? $"{info.Title}: {info.Hint}. Дії гри дивись у виді (look) — вона сама підказує, що зараз можна."
                : "Такої гри тут нема. Подивись list_games.",
    };

    /// <summary>
    /// Правила мафії для того, хто ніколи не бачив картки. Це не прикраса: агент не читає підказок
    /// клієнта, тому все, що людині малює екран, тут має бути словами.
    /// </summary>
    public const string MafiaGuide = """
        МАФІЯ на Глечиках — 3–12 гравців (утрьох перша ніч завжди тиха), ведучий Дядько Глек, обговорення йде в балачці столу (table_read / table_say).

        Як сісти грати:
          set_nick → create_room("mafia", {опції}) або join_room(game:"mafia") → коли всіх зібрано,
          господар тисне start_game. Далі гра йде сама, за годинником: чекай зміни через wait.

        Фази (тривалості залежать від опції pace, точні числа — у view.rules і view.phaseMs):
          intro  — усі дивляться свою роль;
          night  — нічні дії (див. нижче);
          day    — оголошення ранку й СУПЕРЕЧКА В БАЛАЧЦІ СТОЛУ (table_say / table_read). Кімната лише рахує час;
          vote   — голосування; більшість від живих виганяє, рівність і утримання лишають усіх;
          done   — кінець.

        Ролі (своя роль — у view.me.role):
          mafia   — уночі kill(seat) по мирному; своїх не чіпають. Уночі є шепіт say(text) — його бачить лише мафія;
          don     — той самий мафіозі, але його kill вирішальний, а комісару він показується мирним;
          sheriff — уночі check(seat), одна перевірка на ніч; результат лягає у view.night.myCheck;
          doctor  — уночі heal(seat); не двічі поспіль ту саму людину; себе — якщо view.rules.selfHeal;
          kuma    — уночі block(seat): до кого пішла в гості, той цієї ночі нічого не встигне (комісара не спиняє);
          maniac  — уночі kill(seat) сам по собі, проти всіх; виграє, коли лишається сам;
          civil   — уночі спить, удень говорить і голосує.

        Дії (інструмент mafia_move або act):
          kill(seat), check(seat), heal(seat), block(seat), say(text) — тільки вночі й тільки своєю роллю;
          vote(seat) або vote() без місця — утриматись; міняти голос можна до кінця фази.
          Місця беруться з view.players[].seat. Чужих ролей у виді нема — і не буде.

        Перемога: мирні — коли не лишилось ні мафії, ні маньяка; мафія — коли її не менше, ніж мирних
        (і маньяка в селі вже нема); маньяк — коли лишився сам. Мертві бачать усе й мовчать: поки йде партія,
        балачка столу пускає лише живих гравців (глядачам теж зась), а в загальних Балачках (chat_*) про партію
        мовчать за правилом честі — вони взагалі не для гри.

        Порада: після кожного ходу клич wait — він прокидається і від нового виду, і від нової репліки за столом;
        що саме сказали, дивись у table_read(onlyNew: true).
        """;

    /// <summary>«Своя гра» для агента: те, що людині показує поле й пульт, тут словами (specs/svoya.md).</summary>
    public const string SvoyaGuide = """
        СВОЯ ГРА на Глечиках — 1–9 місць, поле «теми × ціни», хто перший натиснув кнопку — той відповідає.

        Як сісти грати:
          create_room("svoya", {host:"auto"}) або join_room(game:"svoya"); коротка партія — {length:"one"} (раунд і фінал)
          чи {length:"two"}. Господар ще в лобі обирає пакет:
          act("pack", {id}) — вбудовані: b_ukraina, b_kino, b_nauka, b_dozvillia, b_potrokhu (решта — з сайту).
          Потім господар — start_game. Далі гра йде за годинником: чекай змін через wait.

        Фази (view.phase): intro → board → reading → buzz → answering → reveal → знову board; наприкінці
          strike → bet → final → finale → done. Спецклітинки: cat (кіт у мішку) і auction (аукціон).
        Дії (act):
          pick {theme, q}         — у board, якщо ти обирач (view.chooser == твоє місце);
          buzz                    — кнопка: у buzz (і в reading, якщо view.options.early == "on"; при "lock"
                                    натискання в reading — фальстарт: кнопка для тебе відкриється на 2 с пізніше,
                                    view.me.lockMs); поки хтось відповідає — стаєш у чергу (view.presses):
                                    він помилиться — відповідаєш ти;
          answer {text}           — коли view.answering == твоє місце, і у фіналі (фаза final);
          appeal / judge {seat, accept} — оскаржити промах у reveal / господар вирішує;
          give {seat}, catPrice {max} — кіт у мішку; bid {amount}, pass, allin — аукціон на своєму ході;
          strike {theme}, bet {amount} — фінал.
        Правильно — плюс ціна, неправильно — мінус ціна. Відповідь перевіряє автомат: одна помилка на п'ять
        літер прощається, «Тарас Шевченко» зараховується на «Шевченко».
        У режимі host:"live" веде жива людина: відповіді кажуть уголос, тож агентові краще грати з автоматом.
        Порада: після кожного ходу клич wait — він повертає свіжий вид.
        """;

    // =========================================================================================
    // Дрібниці
    // =========================================================================================

    /// <summary>Мій стіл: той, за яким я сиджу. Агент майже ніколи не має двох.</summary>
    Room? MyRoom(string nick) =>
        string.IsNullOrEmpty(nick) ? null : rooms.Snapshot()
            .Where(r => r.Seats.Any(x => string.Equals(x.Nick, nick, StringComparison.OrdinalIgnoreCase)))
            .Select(r => rooms.Find(r.Id))
            .FirstOrDefault(r => r is not null);

    string? Which(AgentSession s, string? roomId) =>
        !string.IsNullOrEmpty(roomId) ? roomId : MyRoom(s.Nick)?.Id;

    async Task<object> Run(AgentSession s, Func<RoomOutcome> work, string? known = null)
    {
        var outcome = work();
        await flush.FlushAsync(outcome.Out).ConfigureAwait(false);
        var id = outcome.Reply.RoomId ?? known ?? Which(s, null);
        var state = id is null ? null : Snap(s, id);
        return new
        {
            ok = outcome.Reply.Ok,
            message = outcome.Reply.Message,
            room = outcome.Reply.RoomId ?? id,
            state,
        };
    }

    /// <summary>Знімок столу для агента: шапка, моє місце, мій вид і підказка, що зараз можна зробити.</summary>
    object? Snap(AgentSession s, string roomId)
    {
        if (rooms.ViewsFor(roomId) is not { } b) return null;
        var seat = b.SeatOf(s.Nick);
        var raw = seat is { } i && b.SeatViews.TryGetValue(i, out var mine) ? mine : b.WatcherView;
        var view = JsonSerializer.SerializeToElement(raw, Json);
        return new
        {
            room = new
            {
                id = b.Summary.Id,
                game = b.Summary.Game,
                status = b.Summary.Status,
                host = b.Summary.Host,
                round = b.Summary.Round,
                options = b.Summary.Options,
                seats = b.Summary.Seats.Select(x => new { seat = x.I, nick = x.Nick }),
                result = b.Summary.Result,
            },
            seat,
            spectator = seat is null,
            view,
            can = b.Summary.Game == "mafia" ? MafiaCan(view, seat) : null,
        };
    }

    /// <summary>
    /// Рядок «що зараз можна зробити». Це підказка, а не правило: справжню перевірку однаково робить гра,
    /// і зібрано її з того самого виду, який агент уже бачить.
    /// </summary>
    static string[]? MafiaCan(JsonElement view, int? seat)
    {
        if (seat is null || view.ValueKind != JsonValueKind.Object) return null;
        if (!view.TryGetProperty("phase", out var p) || p.GetString() is not { } phase) return null;
        if (!view.TryGetProperty("me", out var me) || me.ValueKind != JsonValueKind.Object) return null;
        if (!me.TryGetProperty("alive", out var alive) || !alive.GetBoolean()) return ["Тебе вже нема серед живих: дивись і мовчи"];
        var role = me.TryGetProperty("role", out var r) ? r.GetString() : null;

        var quiet = view.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Object
            && rules.TryGetProperty("firstNightKill", out var fk) && !fk.GetBoolean()
            && view.TryGetProperty("day", out var d) && d.GetInt32() == 1;

        return (phase, role) switch
        {
            ("night", "mafia" or "don") => quiet
                ? ["say(text) — шепіт своїм; ножів цієї ночі нема"]
                : ["kill(seat) — показати на жертву", "say(text) — шепіт своїм"],
            ("night", "maniac") => quiet ? ["цієї ночі нічого; придивляйся"] : ["kill(seat) — зарізати будь-кого, крім себе"],
            ("night", "sheriff") => ["check(seat) — одна перевірка за ніч"],
            ("night", "doctor") => ["heal(seat) — врятувати від нічного ножа"],
            ("night", "kuma") => ["block(seat) — піти в гості й зірвати чужу нічну справу"],
            ("night", _) => ["спи; уночі за тебе працюють інші"],
            ("day", _) => ["table_say(text) — сперечайся за столом", "table_read() — читай, що кажуть інші"],
            ("vote", _) => ["vote(seat) — вигнати", "vote() без місця — утриматись"],
            ("intro", _) => ["запам'ятай свою роль і чекай ночі (wait)"],
            ("done", _) => ["rematch — зіграти ще раз", "leave_room — встати з-за столу"],
            _ => ["чекай, поки господар почне (start_game)"],
        };
    }

    static object NeedNick() => Fail("Спершу скажи, як тебе кликати: set_nick");
    static object NoRoom() => Fail("Не видно, за яким ти столом. Передай room або спершу сядь (join_room)");
    static object Fail(string message) => new { ok = false, message };

    /// <summary>
    /// Відбиток стану: усе, на що агенту варто прокинутись. Порівнюємо рядками — це дешевше й чесніше,
    /// ніж вигадувати грі номер версії, якого в неї нема.
    /// </summary>
    string? Fingerprint(AgentSession s, string roomId)
    {
        if (rooms.ViewsFor(roomId) is not { } b) return null;
        var seat = b.SeatOf(s.Nick);
        var raw = seat is { } i && b.SeatViews.TryGetValue(i, out var mine) ? mine : b.WatcherView;
        return $"{b.Summary.Status}|{b.Summary.Round}|{seat}|{chat.LastId()}|{rooms.TableLastId(roomId)}|{JsonSerializer.Serialize(raw, Json)}";
    }
}
